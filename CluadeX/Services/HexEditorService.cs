using System.IO;
using System.Text;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Hex editor backend — opens a binary file fully into memory, supports
/// random-access reads, byte-level patches with undo/redo, hex/text search,
/// and saves with optional backup. Designed to back both the WPF view and
/// the AI agent tool surface (hex_open / hex_read / hex_search / hex_patch).
///
/// Memory model: small/medium files (&lt; 64 MB) are held in memory verbatim.
/// Larger files load only the first 64 MB and warn the user — full-file
/// edit-and-save for huge binaries is out of scope (chunked editing is
/// available via the AI tools for surgical patches).
/// </summary>
public class HexEditorService
{
    private const int MaxInMemoryBytes = 64 * 1024 * 1024;  // 64 MB cap
    private const int MaxRowsPerLoad = 64 * 1024;           // ~1 MB of rows at 16 bytes
    public const int BytesPerRow = 16;

    private byte[]? _data;
    private string? _path;
    private bool _isDirty;
    private bool _isReadOnly;

    public event Action? Changed;
    public event Action<long, int>? BytesPatched;    // (offset, length)
    public event Action<HexFileInfo>? FileLoaded;

    private readonly Stack<HexEdit> _undo = new();
    private readonly Stack<HexEdit> _redo = new();

    public bool HasFile => _data != null && _path != null;
    public string? CurrentPath => _path;
    public long Size => _data?.LongLength ?? 0;
    public bool IsDirty => _isDirty;
    public bool IsReadOnly => _isReadOnly;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;
    public HexFileInfo? CurrentFileInfo { get; private set; }

    // ═══════════════════════════════════════════
    // File open / save
    // ═══════════════════════════════════════════

    /// <summary>Open a file fully into memory. Truncated to 64 MB for huge files.</summary>
    public async Task<HexFileInfo> OpenAsync(string path, bool readOnly = false, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("File not found.", path);

        var fi = new FileInfo(path);
        long actual = fi.Length;
        long toLoad = Math.Min(actual, MaxInMemoryBytes);

        byte[] buffer = new byte[toLoad];
        await using (var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await fs.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
                if (read == 0) break;
                total += read;
            }
        }
        _data = buffer;
        _path = path;
        _isDirty = false;
        _isReadOnly = readOnly || (fi.Attributes & FileAttributes.ReadOnly) != 0;
        _undo.Clear();
        _redo.Clear();

        var info = HexFileInfo.From(path, buffer.AsSpan(0, Math.Min(64, buffer.Length)).ToArray(), actual);
        // SHA-256 of what's loaded (not full file when truncated)
        await using (var s = new MemoryStream(buffer))
        {
            info.ComputeSha256(s);
        }
        CurrentFileInfo = info;
        FileLoaded?.Invoke(info);
        Changed?.Invoke();
        return info;
    }

    /// <summary>Save changes back to the original path. Optionally writes a `.bak` first.</summary>
    public async Task<bool> SaveAsync(bool createBackup = true, CancellationToken ct = default)
    {
        if (_data == null || _path == null) return false;
        if (_isReadOnly) throw new InvalidOperationException("File is read-only.");

        if (createBackup)
        {
            try
            {
                string bak = _path + ".bak";
                File.Copy(_path, bak, overwrite: true);
            }
            catch { /* best-effort */ }
        }

        await using (var fs = File.Open(_path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await fs.WriteAsync(_data, ct);
        }
        _isDirty = false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Save to a different path (does not change CurrentPath).</summary>
    public async Task<bool> SaveAsAsync(string newPath, CancellationToken ct = default)
    {
        if (_data == null) return false;
        await using var fs = File.Open(newPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await fs.WriteAsync(_data, ct);
        return true;
    }

    public void Close()
    {
        _data = null;
        _path = null;
        _isDirty = false;
        _undo.Clear();
        _redo.Clear();
        CurrentFileInfo = null;
        Changed?.Invoke();
    }

    // ═══════════════════════════════════════════
    // Random-access read
    // ═══════════════════════════════════════════

    /// <summary>Read bytes at offset. Returns empty array on out-of-range.</summary>
    public byte[] Read(long offset, int length)
    {
        if (_data == null) return Array.Empty<byte>();
        if (offset < 0 || offset >= _data.LongLength) return Array.Empty<byte>();
        int safeLen = (int)Math.Min((long)length, _data.LongLength - offset);
        var output = new byte[safeLen];
        Array.Copy(_data, offset, output, 0, safeLen);
        return output;
    }

    /// <summary>Build a windowed view of HexRow objects for the editor grid.</summary>
    public List<HexRow> GetRows(long startOffset, int rowCount)
    {
        var list = new List<HexRow>();
        if (_data == null) return list;
        // Align start to row boundary
        long start = (startOffset / BytesPerRow) * BytesPerRow;
        for (int i = 0; i < rowCount; i++)
        {
            long off = start + (long)i * BytesPerRow;
            if (off >= _data.LongLength) break;
            int len = (int)Math.Min((long)BytesPerRow, _data.LongLength - off);
            var row = new HexRow { Offset = off, Bytes = new byte[len] };
            Array.Copy(_data, off, row.Bytes, 0, len);
            list.Add(row);
        }
        return list;
    }

    /// <summary>Total number of rows in the file (for virtualizer row count).</summary>
    public long TotalRows => _data == null ? 0 : (_data.LongLength + BytesPerRow - 1) / BytesPerRow;

    // ═══════════════════════════════════════════
    // Patch (with undo/redo)
    // ═══════════════════════════════════════════

    /// <summary>Write bytes at offset. In-place — does NOT extend the file.</summary>
    public bool Patch(long offset, byte[] newBytes, string description = "patch")
    {
        if (_data == null) return false;
        if (_isReadOnly) throw new InvalidOperationException("File is read-only.");
        if (offset < 0) return false;
        if (offset + newBytes.Length > _data.LongLength) return false;

        var old = new byte[newBytes.Length];
        Array.Copy(_data, offset, old, 0, newBytes.Length);

        // If no change, skip
        bool any = false;
        for (int i = 0; i < newBytes.Length; i++) { if (old[i] != newBytes[i]) { any = true; break; } }
        if (!any) return false;

        Array.Copy(newBytes, 0, _data, offset, newBytes.Length);
        _undo.Push(new HexEdit { Offset = offset, OldBytes = old, NewBytes = newBytes, Description = description });
        _redo.Clear();
        _isDirty = true;
        BytesPatched?.Invoke(offset, newBytes.Length);
        Changed?.Invoke();
        return true;
    }

    public bool Undo()
    {
        if (_data == null || _undo.Count == 0) return false;
        var edit = _undo.Pop();
        Array.Copy(edit.OldBytes, 0, _data, edit.Offset, edit.OldBytes.Length);
        _redo.Push(edit);
        _isDirty = _undo.Count > 0;
        BytesPatched?.Invoke(edit.Offset, edit.OldBytes.Length);
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_data == null || _redo.Count == 0) return false;
        var edit = _redo.Pop();
        Array.Copy(edit.NewBytes, 0, _data, edit.Offset, edit.NewBytes.Length);
        _undo.Push(edit);
        _isDirty = true;
        BytesPatched?.Invoke(edit.Offset, edit.NewBytes.Length);
        Changed?.Invoke();
        return true;
    }

    // ═══════════════════════════════════════════
    // Search
    // ═══════════════════════════════════════════

    public enum SearchMode { Hex, Text, TextCaseInsensitive }

    /// <summary>Find every occurrence of a pattern. Stops at maxResults to keep memory sane.</summary>
    public List<HexSearchResult> Search(string pattern, SearchMode mode, int maxResults = 5000, long fromOffset = 0)
    {
        var results = new List<HexSearchResult>();
        if (_data == null || string.IsNullOrWhiteSpace(pattern)) return results;

        byte[] needle;
        switch (mode)
        {
            case SearchMode.Hex:
                if (!TryParseHex(pattern, out needle)) return results;
                break;
            case SearchMode.Text:
                needle = Encoding.UTF8.GetBytes(pattern);
                break;
            case SearchMode.TextCaseInsensitive:
                return SearchTextCaseInsensitive(pattern, maxResults, fromOffset);
            default:
                return results;
        }

        if (needle.Length == 0) return results;

        long start = Math.Max(0, fromOffset);
        for (long i = start; i <= _data.LongLength - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (_data[i + j] != needle[j]) { match = false; break; }
            }
            if (match)
            {
                results.Add(new HexSearchResult
                {
                    Offset = i,
                    Length = needle.Length,
                    Preview = BuildPreview(i, needle.Length),
                });
                if (results.Count >= maxResults) break;
                // Don't skip past the match — overlapping matches are real
                i += needle.Length - 1;
            }
        }
        return results;
    }

    private List<HexSearchResult> SearchTextCaseInsensitive(string pattern, int maxResults, long fromOffset)
    {
        var results = new List<HexSearchResult>();
        if (_data == null) return results;
        byte[] lower = Encoding.UTF8.GetBytes(pattern.ToLowerInvariant());
        if (lower.Length == 0) return results;

        long start = Math.Max(0, fromOffset);
        for (long i = start; i <= _data.LongLength - lower.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < lower.Length; j++)
            {
                byte a = _data[i + j];
                if (a >= 'A' && a <= 'Z') a = (byte)(a + 32);
                if (a != lower[j]) { match = false; break; }
            }
            if (match)
            {
                results.Add(new HexSearchResult
                {
                    Offset = i,
                    Length = lower.Length,
                    Preview = BuildPreview(i, lower.Length),
                });
                if (results.Count >= maxResults) break;
                i += lower.Length - 1;
            }
        }
        return results;
    }

    private string BuildPreview(long offset, int length)
    {
        if (_data == null) return "";
        // Show ~24 chars of context as ASCII
        long start = Math.Max(0, offset - 4);
        int end = (int)Math.Min(_data.LongLength, offset + length + 16) - (int)start;
        var sb = new StringBuilder(end);
        for (int i = 0; i < end; i++)
        {
            byte b = _data[start + i];
            sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '·');
        }
        return sb.ToString();
    }

    /// <summary>Replace all matches of pattern with replacement. Returns count replaced.</summary>
    public int ReplaceAll(string pattern, string replacement, SearchMode mode)
    {
        if (_data == null) return 0;
        if (_isReadOnly) return 0;

        byte[] needle, repl;
        switch (mode)
        {
            case SearchMode.Hex:
                if (!TryParseHex(pattern, out needle) || !TryParseHex(replacement, out repl)) return 0;
                break;
            case SearchMode.Text:
                needle = Encoding.UTF8.GetBytes(pattern);
                repl = Encoding.UTF8.GetBytes(replacement);
                break;
            default:
                return 0;
        }
        if (needle.Length == 0 || needle.Length != repl.Length) return 0;
        // Same-length only (we don't shift) — matches hexed.it semantics.

        var hits = Search(pattern, mode, maxResults: 100_000);
        foreach (var h in hits)
        {
            Patch(h.Offset, repl, $"replace at 0x{h.Offset:X8}");
        }
        return hits.Count;
    }

    // ═══════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════

    /// <summary>Parse a hex string like "DE AD BE EF" or "deadbeef" or "0xDEAD" into bytes.</summary>
    public static bool TryParseHex(string input, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(input)) return false;
        var clean = new StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == ' ' || c == '_' || c == '-' || c == ',') continue;
            if (c == '0' && i + 1 < input.Length && (input[i + 1] == 'x' || input[i + 1] == 'X'))
            {
                i++; continue;
            }
            clean.Append(c);
        }
        var s = clean.ToString();
        if (s.Length == 0 || s.Length % 2 != 0) return false;
        bytes = new byte[s.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(s.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                return false;
        }
        return true;
    }

    /// <summary>Format a byte range as a hex string ("DE AD BE EF").</summary>
    public string FormatHex(long offset, int length)
    {
        if (_data == null) return "";
        long safeEnd = Math.Min(_data.LongLength, offset + length);
        var sb = new StringBuilder();
        for (long i = offset; i < safeEnd; i++)
        {
            if (i > offset) sb.Append(' ');
            sb.Append(_data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>For the AI agent: read range + return both hex and ascii.</summary>
    public (string hex, string ascii) ReadHexAndAscii(long offset, int length)
    {
        var bytes = Read(offset, length);
        var hex = new StringBuilder();
        var ascii = new StringBuilder();
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) hex.Append(' ');
            hex.Append(bytes[i].ToString("X2"));
            ascii.Append(bytes[i] >= 0x20 && bytes[i] < 0x7F ? (char)bytes[i] : '.');
        }
        return (hex.ToString(), ascii.ToString());
    }
}
