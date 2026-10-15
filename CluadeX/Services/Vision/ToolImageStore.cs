using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Vision;

/// <summary>
/// Where tool images live on disk: DataRoot\screens\&lt;session&gt;\NNN.jpg. A screenshot is saved here once and
/// referred to by path everywhere text goes (tool output, chat rows, the "[screenshot omitted — saved at …]" note)
/// — the base64 itself only ever travels in ToolResult.Images / ContentBlock.Images.
///
/// Bounded: <see cref="Prune"/> keeps at most <see cref="MaxPerSession"/> images per chat and nothing older than
/// <see cref="MaxAge"/>. Only files this store names (digits + ".jpg") are ever deleted, and folders that are
/// reparse points (a junction planted under screens\) are never entered, so a tampered folder cannot turn a
/// prune into deleting files elsewhere.
/// </summary>
public sealed partial class ToolImageStore
{
    public const int MaxPerSession = 200;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    /// <summary>Largest image accepted (a 1568-px JPEG is ~0.5 MB; this only stops a runaway caller).</summary>
    public const int MaxBytes = 20 * 1024 * 1024;

    private readonly object _gate = new();

    /// <summary>The folder that holds one sub-folder per chat.</summary>
    public string Root { get; }

    public ToolImageStore(SettingsService settings) : this(Path.Combine(settings.DataRoot, "screens")) { }

    /// <summary>A store under any folder (harnesses use a temp folder).</summary>
    public ToolImageStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("root is required", nameof(root));
        Root = Path.GetFullPath(root);
    }

    /// <summary>
    /// Save one JPEG for <paramref name="sessionKey"/> and return its full path (…\screens\&lt;session&gt;\007.jpg).
    /// Throws <see cref="ArgumentException"/> for bytes that are empty, too large or not a JPEG, and
    /// <see cref="IOException"/> when the disk refuses — callers turn either into a text note, never into a failed
    /// tool call that loses the rest of its result.
    /// </summary>
    public string Save(byte[] jpeg, string? sessionKey)
    {
        if (jpeg == null || jpeg.Length < 4) throw new ArgumentException("No image bytes.", nameof(jpeg));
        if (jpeg.Length > MaxBytes) throw new ArgumentException($"Image is {jpeg.Length:N0} bytes (max {MaxBytes:N0}).", nameof(jpeg));
        // SOI marker FF D8 FF: this store writes .jpg files and nothing else.
        if (jpeg[0] != 0xFF || jpeg[1] != 0xD8 || jpeg[2] != 0xFF) throw new ArgumentException("Not a JPEG image.", nameof(jpeg));

        string dir = SessionDirectory(sessionKey);
        lock (_gate)
        {
            Directory.CreateDirectory(dir);
            if (IsReparsePoint(dir)) throw new IOException("The screenshot folder is a link — not writing through it.");
            int next = NextNumber(dir);
            for (int attempt = 0; attempt < 50; attempt++, next++)
            {
                string path = Path.Combine(dir, next.ToString("D3") + ".jpg");
                FileStream fs;
                // CreateNew: never overwrite — another process (a second store over the same folder) may have
                // taken this number since the scan.
                try { fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
                catch (IOException) when (File.Exists(path)) { continue; /* taken — try the next number */ }
                try
                {
                    using (fs) fs.Write(jpeg, 0, jpeg.Length);
                    return path;
                }
                catch
                {
                    // Disk full / yanked drive: no half-written JPEG left behind for a later prompt to point at.
                    try { File.Delete(path); } catch { }
                    throw;
                }
            }
            throw new IOException("Could not find a free file name for the screenshot.");
        }
    }

    /// <summary>
    /// Delete images older than <see cref="MaxAge"/> and all but the newest <see cref="MaxPerSession"/> per chat;
    /// remove chat folders left empty. <paramref name="sessionKey"/> limits the work to one chat (null = all).
    /// Returns how many files were deleted. Never throws.
    /// </summary>
    public int Prune(string? sessionKey = null, DateTime? nowUtc = null)
    {
        int deleted = 0;
        DateTime cutoff = (nowUtc ?? DateTime.UtcNow) - MaxAge;
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(Root) || IsReparsePoint(Root)) return 0;
                IEnumerable<string> dirs = sessionKey != null
                    ? new[] { SessionDirectory(sessionKey) }.Where(Directory.Exists)
                    : Directory.EnumerateDirectories(Root);
                foreach (var dir in dirs.ToList())
                {
                    try
                    {
                        if (IsReparsePoint(dir)) continue;
                        var files = Directory.EnumerateFiles(dir, "*.jpg", SearchOption.TopDirectoryOnly)
                            .Where(f => OwnFileName().IsMatch(Path.GetFileName(f)))
                            .Select(f => new FileInfo(f))
                            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
                            .OrderByDescending(f => NumberOf(f.Name))
                            .ToList();
                        for (int i = 0; i < files.Count; i++)
                        {
                            if (i < MaxPerSession && files[i].LastWriteTimeUtc >= cutoff) continue;
                            try { files[i].Delete(); deleted++; } catch { /* in use — next prune */ }
                        }
                        if (!Directory.EnumerateFileSystemEntries(dir).Any())
                            try { Directory.Delete(dir); } catch { }
                    }
                    catch { /* one unreadable folder must not stop the rest */ }
                }
            }
            catch { }
        }
        return deleted;
    }

    /// <summary>The folder for one chat. Any key maps to a safe single folder name: letters, digits, '-' and '_'
    /// up to 64 chars are kept as they are; anything else ("..", "a\b", a drive, Thai text) becomes a hash.</summary>
    public string SessionDirectory(string? sessionKey) => Path.Combine(Root, SafeSessionName(sessionKey));

    public static string SafeSessionName(string? sessionKey)
    {
        if (string.IsNullOrWhiteSpace(sessionKey)) return "default";
        if (sessionKey.Length <= 64 && SafeName().IsMatch(sessionKey)) return sessionKey;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sessionKey));
        return "s-" + Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    private static int NextNumber(string dir)
    {
        int max = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*.jpg", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(f);
            if (OwnFileName().IsMatch(name)) max = Math.Max(max, NumberOf(name));
        }
        return max + 1;
    }

    private static int NumberOf(string fileName) =>
        int.TryParse(Path.GetFileNameWithoutExtension(fileName), out var n) ? n : 0;

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }   // can't tell = don't touch
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"^\d{1,9}\.jpg$", RegexOptions.IgnoreCase)]
    private static partial Regex OwnFileName();
}
