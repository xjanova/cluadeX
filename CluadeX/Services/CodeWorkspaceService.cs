using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Backend for the Code Editor page. Builds a recursive file tree
/// rooted at the current FileSystemService.WorkingDirectory, opens files
/// into in-memory tabs, and writes them back on save.
///
/// Tree builds are throttled (skips well-known noise folders like node_modules,
/// bin, obj, .git, etc) and lazy-load deep subtrees so a 50k-file repo
/// doesn't freeze the UI on first render.
/// </summary>
public class CodeWorkspaceService
{
    private readonly FileSystemService _fs;
    private readonly GitService _git;

    public CodeWorkspaceService(FileSystemService fs, GitService git)
    {
        _fs = fs;
        _git = git;
    }

    public string WorkingDirectory => _fs.WorkingDirectory;
    public bool HasWorkingDirectory => _fs.HasWorkingDirectory;

    // Folders we DON'T descend into — they explode the tree size.
    private static readonly HashSet<string> ExcludedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "dist", "build", "out",
        ".vs", ".idea", ".vscode-test", "__pycache__", ".pytest_cache",
        "target", "venv", ".venv", "env", ".env",
        "packages", ".nuget",
    };

    // Cap files-per-folder so a huge generated folder doesn't enumerate millions of entries.
    private const int MaxEntriesPerFolder = 2000;

    /// <summary>Build a lazy tree from the working directory root.</summary>
    public List<FileTreeNode> BuildTree()
    {
        var result = new List<FileTreeNode>();
        if (!HasWorkingDirectory) return result;

        // Root entry — represents the working directory itself; expanded by default
        var root = new FileTreeNode
        {
            FullPath = WorkingDirectory,
            Name = Path.GetFileName(WorkingDirectory.TrimEnd('\\', '/')) ?? WorkingDirectory,
            IsDirectory = true,
            IsExpanded = true,
        };
        PopulateChildren(root);
        result.Add(root);
        return result;
    }

    /// <summary>Synchronously populate a folder's children (cheap because we cap + filter). UI thread only once the
    /// folder is on screen — see <see cref="ReadChildren"/> for the part that can run anywhere.</summary>
    public void PopulateChildren(FileTreeNode folder)
    {
        if (!folder.IsDirectory) return;
        SetChildren(folder, ReadChildren(folder.FullPath));
    }

    /// <summary>
    /// A folder's entries as new nodes — disk only, safe on any thread. Expanding a folder reads here off the UI
    /// thread and hands the list to <see cref="SetChildren"/> on it: filling the bound Children collection from a
    /// worker thread threw (a CollectionView only takes changes on its own thread), so a click on a folder in the
    /// explorer silently did nothing.
    /// </summary>
    public List<FileTreeNode> ReadChildren(string folderPath)
    {
        var list = new List<FileTreeNode>();
        try
        {
            var dirs = Directory.EnumerateDirectories(folderPath)
                .Where(d =>
                {
                    var name = Path.GetFileName(d);
                    if (string.IsNullOrEmpty(name)) return false;
                    if (name.StartsWith(".") && name != ".cluadex" && name != ".claude") return false;
                    return !ExcludedFolders.Contains(name);
                })
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Take(MaxEntriesPerFolder);
            foreach (var d in dirs)
                list.Add(new FileTreeNode { FullPath = d, Name = Path.GetFileName(d) ?? d, IsDirectory = true });

            var files = Directory.EnumerateFiles(folderPath)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(MaxEntriesPerFolder);
            foreach (var f in files)
                list.Add(new FileTreeNode { FullPath = f, Name = Path.GetFileName(f), IsDirectory = false });
        }
        catch
        {
            // Permission denied / IO error — just leave it empty
        }
        return list;
    }

    /// <summary>Replace a folder's children (on the thread that owns the tree once it is on screen).</summary>
    public void SetChildren(FileTreeNode folder, IEnumerable<FileTreeNode> children)
    {
        folder.Children.Clear();
        foreach (var c in children) folder.Children.Add(c);
    }

    /// <summary>Largest file the diff view compares — a side-by-side diff of anything bigger is unreadable anyway.</summary>
    public const long MaxEditableBytes = 2 * 1024 * 1024;

    /// <summary>Largest file the editor opens, Notepad++ style: logs, dumps and data files open too. The editor
    /// switches to its large-file mode (lazy sync, no auto-completion; no colours past ~24 MB) to stay smooth.
    /// Binary files are still refused, at any size.</summary>
    public const long MaxOpenBytes = 512L * 1024 * 1024;

    /// <summary>Up to this size the disk stamp is a SHA-256 of the bytes; above it, length + last-write time — hashing
    /// half a gigabyte on every save would freeze the editor.</summary>
    private const long HashedStampBytes = 16L * 1024 * 1024;

    /// <summary>
    /// What the file on disk looked like when a tab last read or wrote it. Kept beside the tab (the
    /// model class isn't ours to extend) and weakly keyed, so a closed tab's stamp just goes away.
    /// Save compares against it: without this, saving a tab silently overwrote whatever the agent,
    /// a git checkout or another editor had written to that file since it was opened.
    /// </summary>
    private sealed class DiskStamp
    {
        public bool Existed;
        public long Length;
        public byte[] Hash = Array.Empty<byte>();
        /// <summary>Set instead of <see cref="Hash"/> for files above <see cref="HashedStampBytes"/>.</summary>
        public DateTime LastWriteUtc;
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<OpenFileTab, DiskStamp> _stamps = new();

    private static DiskStamp StampOf(byte[] bytes, string fullPath)
    {
        if (bytes.LongLength <= HashedStampBytes)
            return new DiskStamp { Existed = true, Length = bytes.LongLength, Hash = System.Security.Cryptography.SHA256.HashData(bytes) };
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(fullPath); } catch { written = DateTime.MinValue; }
        return new DiskStamp { Existed = true, Length = bytes.LongLength, LastWriteUtc = written };
    }

    private void SetStamp(OpenFileTab tab, DiskStamp stamp)
    {
        _stamps.Remove(tab);
        _stamps.Add(tab, stamp);
    }

    /// <summary>Open a file and return its tab — caller pushes to ObservableCollection.</summary>
    public async Task<OpenFileTab?> OpenFileAsync(string fullPath, CancellationToken ct = default)
    {
        if (!File.Exists(fullPath)) return null;

        var fi = new FileInfo(fullPath);
        if (fi.Length > MaxOpenBytes) return null;

        CluadeX.Helpers.TextFileCodec.EditorText decoded;
        DiskStamp stamp;
        try
        {
            // Read, sniff, decode and fingerprint off the UI thread: for a big file each step is a full pass.
            var loaded = await Task.Run(async () =>
            {
                byte[] bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
                // Detect binary by checking for NUL bytes in the first 1 KB — refuse to open it as text. A UTF-16
                // file (BOM FF FE / FE FF) is full of NULs and is text.
                bool utf16 = bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));
                if (!utf16)
                    for (int i = 0; i < Math.Min(1024, bytes.Length); i++)
                        if (bytes[i] == 0) return ((CluadeX.Helpers.TextFileCodec.EditorText, DiskStamp)?)null;
                return (CluadeX.Helpers.TextFileCodec.DecodeForEditor(bytes), StampOf(bytes, fullPath));
            }, ct);
            if (loaded == null) return null;
            (decoded, stamp) = loaded.Value;
        }
        catch
        {
            return null;
        }

        var tab = new OpenFileTab
        {
            FullPath = fullPath, HasBom = decoded.HasBom, CodePage = decoded.CodePage, DecodedFaithfully = decoded.Faithful,
        };
        tab.MarkOpened(decoded.Text);
        SetStamp(tab, stamp);
        return tab;
    }

    /// <summary>
    /// <paramref name="target"/> was just reloaded with <paramref name="freshlyLoaded"/>'s text
    /// (agent live-follow, branch switch, rename). Carry the fresh disk fingerprint over too —
    /// otherwise the next Save would compare against the OLD file and cry "changed on disk" about
    /// a change the tab already shows.
    /// </summary>
    public void AdoptDiskState(OpenFileTab target, OpenFileTab freshlyLoaded)
    {
        if (_stamps.TryGetValue(freshlyLoaded, out var stamp)) SetStamp(target, stamp);
        target.HasBom = freshlyLoaded.HasBom;
        target.CodePage = freshlyLoaded.CodePage;
        target.DecodedFaithfully = freshlyLoaded.DecodedFaithfully;
    }

    /// <summary>True when the file on disk is no longer what this tab last read or wrote (edited by
    /// the agent / git / another program, or deleted).</summary>
    public bool HasChangedOnDisk(OpenFileTab tab)
    {
        if (!_stamps.TryGetValue(tab, out var stamp)) return false;   // never read from disk — nothing to compare
        try
        {
            if (!File.Exists(tab.FullPath)) return stamp.Existed;
            var fi = new FileInfo(tab.FullPath);
            if (fi.Length != stamp.Length) return true;
            // A large file's stamp is its write time (see HashedStampBytes).
            if (stamp.Hash.Length == 0) return fi.LastWriteTimeUtc != stamp.LastWriteUtc;
            // Same length is not proof: hash (bounded by HashedStampBytes, so this stays quick).
            var hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(tab.FullPath));
            return !hash.AsSpan().SequenceEqual(stamp.Hash);
        }
        catch
        {
            return false;   // unreadable right now — let the save itself report the real error
        }
    }

    /// <summary>The current disk text of a tab's file (for the "compare" choice), or null.</summary>
    public async Task<string?> ReadDiskTextAsync(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length > MaxOpenBytes) return null;
            return await Task.Run(async () =>
                CluadeX.Helpers.TextFileCodec.DecodeForEditor(await File.ReadAllBytesAsync(fullPath).ConfigureAwait(false)).Text);
        }
        catch { return null; }
    }

    /// <summary>
    /// Save a tab exactly as the file was encoded: a BOM only if it had one — Save used to write
    /// Encoding.UTF8, which prepends a BOM to every file, so saving a shell script or package.json
    /// from the editor broke it. A tab opened from a non-UTF-8 file is never saved (returns false):
    /// its text holds replacement characters, and writing them would destroy the originals.
    /// A file that changed on disk since the tab read it is NOT overwritten (returns false) — use
    /// <see cref="SaveTabCheckedAsync"/> to find out why and to overwrite deliberately.
    /// </summary>
    public async Task<bool> SaveTabAsync(OpenFileTab tab, CancellationToken ct = default)
        => (await SaveTabCheckedAsync(tab, overwriteExternalChanges: false, ct)).Outcome == SaveOutcome.Saved;

    /// <summary>
    /// Save with the reason when it doesn't happen. Writes atomically: the bytes go to a temp file
    /// in the same folder and replace the original in one step (File.Replace), so a crash, a full
    /// disk or a power cut mid-save leaves either the old file or the new one — never a truncated
    /// half-file, which is what a plain WriteAllText can leave behind.
    /// </summary>
    public async Task<SaveResult> SaveTabCheckedAsync(OpenFileTab tab, bool overwriteExternalChanges, CancellationToken ct = default)
    {
        var refused = PrepareSave(tab, overwriteExternalChanges, out string content, out byte[] bytes);
        if (refused != null) return refused;
        try
        {
            await WriteAtomicAsync(tab.FullPath, bytes, ct);
        }
        catch (Exception ex)
        {
            return new SaveResult(SaveOutcome.Failed, ex.GetType().Name + ": " + ex.Message);
        }
        return CompleteSave(tab, content, bytes);
    }

    /// <summary>
    /// Save As: write the tab's text, in its encoding, to <paramref name="newPath"/> (atomically — an existing file
    /// there is replaced only once the new bytes are complete). The tab itself is untouched; the caller opens the
    /// new file in its place. The old file on disk keeps what it had, as in Notepad++.
    /// </summary>
    public async Task<SaveResult> SaveCopyAsAsync(OpenFileTab tab, string newPath, CancellationToken ct = default)
    {
        if (!tab.DecodedFaithfully) return new SaveResult(SaveOutcome.NotUtf8, null);
        tab.FlushPendingEdits();
        byte[] bytes;
        try { bytes = CluadeX.Helpers.TextFileCodec.EncodeForEditor(tab.Content, tab.CodePage, tab.HasBom); }
        catch (EncoderFallbackException) { return new SaveResult(SaveOutcome.CannotEncode, null); }
        try { await WriteAtomicAsync(newPath, bytes, ct); }
        catch (Exception ex) { return new SaveResult(SaveOutcome.Failed, ex.GetType().Name + ": " + ex.Message); }
        return new SaveResult(SaveOutcome.Saved, null);
    }

    /// <summary>A new empty file (never over an existing one: false when the name is taken or cannot be made).</summary>
    public static bool TryCreateEmptyFile(string path, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>Synchronous save, for the one caller that cannot await: the window's Closing handler
    /// (awaiting there would let the window close before the bytes are written).</summary>
    public SaveResult SaveTabChecked(OpenFileTab tab, bool overwriteExternalChanges)
    {
        var refused = PrepareSave(tab, overwriteExternalChanges, out string content, out byte[] bytes);
        if (refused != null) return refused;
        try
        {
            WriteAtomic(tab.FullPath, bytes);
        }
        catch (Exception ex)
        {
            return new SaveResult(SaveOutcome.Failed, ex.GetType().Name + ": " + ex.Message);
        }
        return CompleteSave(tab, content, bytes);
    }

    /// <summary>The checks every save makes before touching the disk; null = go ahead.</summary>
    private SaveResult? PrepareSave(OpenFileTab tab, bool overwriteExternalChanges, out string content, out byte[] bytes)
    {
        content = "";
        bytes = Array.Empty<byte>();
        if (tab == null || string.IsNullOrEmpty(tab.FullPath)) return new SaveResult(SaveOutcome.Failed, "no path");
        if (!tab.DecodedFaithfully) return new SaveResult(SaveOutcome.NotUtf8, null);
        tab.FlushPendingEdits();   // a large file's last keystrokes may still be only in the editor

        if (!overwriteExternalChanges && HasChangedOnDisk(tab))
            return new SaveResult(SaveOutcome.ChangedOnDisk, null) { DeletedOnDisk = !File.Exists(tab.FullPath) };

        content = tab.Content;   // snapshot: the user keeps typing while the write runs
        try { bytes = CluadeX.Helpers.TextFileCodec.EncodeForEditor(content, tab.CodePage, tab.HasBom); }
        catch (EncoderFallbackException) { return new SaveResult(SaveOutcome.CannotEncode, null); }
        return null;
    }

    private SaveResult CompleteSave(OpenFileTab tab, string content, byte[] bytes)
    {
        SetStamp(tab, StampOf(bytes, tab.FullPath));
        // Only clear the dirty flag if nothing was typed during the write; otherwise the newer
        // keystrokes would show as saved when they are not.
        if (string.Equals(tab.Content, content, StringComparison.Ordinal)) tab.MarkSaved();
        return new SaveResult(SaveOutcome.Saved, null);
    }

    private static string TempPathFor(string path)
    {
        string dir = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.cluadex-tmp");
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        string temp = TempPathFor(path);
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous))
            {
                await fs.WriteAsync(bytes, ct);
                await fs.FlushAsync(ct);
                fs.Flush(flushToDisk: true);
            }
            ReplaceWith(temp, path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort temp cleanup */ }
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string temp = TempPathFor(path);
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            ReplaceWith(temp, path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>Swap the fully written temp file in for <paramref name="path"/> in one step.</summary>
    private static void ReplaceWith(string temp, string path)
    {
        if (File.Exists(path))
        {
            try
            {
                // Keeps the original's ACLs, creation time and attributes.
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
            {
                // Some volumes (FAT, a few network shares) can't ReplaceFile — fall back to an
                // overwrite copy. Still never truncates in place before the new bytes exist.
                File.Copy(temp, path, overwrite: true);
            }
        }
        else
        {
            File.Move(temp, path);
        }
    }

    // ─── Git status enrichment (best-effort) ──────────────────────────
    /// <summary>Run git status and tag matching nodes with M/A/U/? badges.</summary>
    public async Task EnrichGitStatusAsync(IEnumerable<FileTreeNode> roots)
    {
        if (!HasWorkingDirectory) return;
        try
        {
            // Porcelain -z: unquoted, unescaped paths. The --short parse here used to miss every
            // file with a space or a Thai name, so those never got a badge.
            var st = await _git.StatusEntriesAsync();
            if (!st.Success) return;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // full path → badge
            foreach (var e in st.Entries)
            {
                if (string.IsNullOrEmpty(e.FullPath)) continue;
                string code = e.Code;
                string full = e.FullPath;

                string badge = code switch
                {
                    "M" or "MM" or "AM" or "RM" => "M",
                    "A" or "AA" or "AD" => "A",
                    "??" => "U",
                    "D" or "DD" => "D",
                    "R" or "RR" => "R",
                    _ => code.Length > 0 ? code[0].ToString() : "?",
                };
                map[full] = badge;   // FileTreeNode.GitBadgeColorHex derives the theme colour from it
            }

            foreach (var root in roots) WalkAndTag(root, map);
        }
        catch { /* git not installed / not a repo — silently skip */ }
    }

    private static void WalkAndTag(FileTreeNode node, Dictionary<string, string> map)
    {
        if (!node.IsDirectory && map.TryGetValue(node.FullPath, out var badge))
            node.GitBadge = badge;
        foreach (var c in node.Children) WalkAndTag(c, map);
    }
}

public enum SaveOutcome
{
    Saved,
    /// <summary>The tab's text is a lossy reading of the file (no encoding reproduced its bytes); saving would
    /// destroy its characters.</summary>
    NotUtf8,
    /// <summary>The file's legacy encoding (e.g. TIS-620) cannot hold a character in the tab — nothing was written.</summary>
    CannotEncode,
    /// <summary>The file on disk changed (or vanished) since the tab read it — nothing was written.</summary>
    ChangedOnDisk,
    Failed,
}

public sealed record SaveResult(SaveOutcome Outcome, string? Error)
{
    /// <summary>With <see cref="SaveOutcome.ChangedOnDisk"/>: the file is gone, not just different.</summary>
    public bool DeletedOnDisk { get; init; }
    // Error is a diagnostic for the log (exception type + message), never for the UI.
}
