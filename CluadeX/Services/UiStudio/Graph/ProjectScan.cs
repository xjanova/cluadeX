using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace CluadeX.Services.UiStudio.Graph;

/// <summary>One source file the graph reads: its id (path from the project root, with /), its text and the text with
/// comments and strings blanked (<see cref="CodeMask"/>), made once per version of the file on disk.</summary>
internal sealed class SourceFile
{
    public required string Full { get; init; }
    public required string Id { get; init; }
    public required string Ext { get; init; }
    public required string Text { get; init; }
    public required DateTime Stamp { get; init; }
    public required long Length { get; init; }
    public string Dir => Path.GetDirectoryName(Full)!;
    public bool IsBlade => Full.EndsWith(".blade.php", StringComparison.OrdinalIgnoreCase);

    private LineIndex? _lines;
    public LineIndex Lines => _lines ??= new LineIndex(Text);

    private string? _masked;
    public string Masked => _masked ??= MaskFor(this);

    /// <summary>Anything a reader wants to keep with this version of the file (a parsed syntax tree).</summary>
    public object? Parsed { get; set; }

    private static string MaskFor(SourceFile f) => f.Ext switch
    {
        ".js" or ".jsx" or ".ts" or ".tsx" or ".mjs" or ".cjs" => CodeMask.Mask(f.Text, CodeMask.Lang.Js),
        ".vue" or ".svelte" or ".astro" => CodeMask.Sfc(f.Text),
        ".css" or ".scss" or ".sass" or ".less" => CodeMask.Mask(f.Text, CodeMask.Lang.Css),
        ".html" or ".htm" => CodeMask.Mask(f.Text, CodeMask.Lang.Html),
        ".xaml" or ".axaml" => CodeMask.Mask(f.Text, CodeMask.Lang.Xml),
        ".dart" => CodeMask.Mask(f.Text, CodeMask.Lang.Dart),
        ".py" => CodeMask.Mask(f.Text, CodeMask.Lang.Python),
        ".php" => f.IsBlade ? CodeMask.Mask(f.Text, CodeMask.Lang.Blade) : CodeMask.Mask(f.Text, CodeMask.Lang.Php),
        _ => f.Text,
    };
}

/// <summary>
/// The bounded read of a project: at most <c>maxFiles</c> source files, none over <see cref="MaxFileBytes"/>, at
/// most <see cref="MaxTotalBytes"/> in all; binary files (a NUL byte), minified bundles and generated code are left
/// out. Folders are read breadth-first, so a project too big to read whole still shows its top levels. Files are kept
/// between builds by (path, last write, size): a live update re-reads only what changed.
/// </summary>
internal static class ProjectScan
{
    public const long MaxFileBytes = 512 * 1024;
    public const long MaxTotalBytes = 48L * 1024 * 1024;
    public const int MaxConfigs = 400;

    public static readonly HashSet<string> SourceExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".vue", ".svelte", ".astro", ".css", ".scss", ".sass", ".less",
        ".cs", ".xaml", ".axaml", ".dart", ".php", ".py",
    };

    /// <summary>Files that say how names resolve (aliases, package names, namespaces) — read, never drawn.</summary>
    public static readonly HashSet<string> ConfigNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "tsconfig.json", "jsconfig.json", "package.json", "pubspec.yaml", "composer.json",
        "vite.config.ts", "vite.config.js", "vite.config.mjs", "vite.config.mts",
    };

    private static readonly HashSet<string> SkipDirs = new(SourceSearch.SkipDirs, StringComparer.OrdinalIgnoreCase)
    {
        "__pycache__", "venv", "env", "site-packages", "storage", "target", "Pods", "DerivedData", "bower_components", "jspm_packages",
    };

    private static bool Generated(string name) =>
        name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".g.dart", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".freezed.dart", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".mocks.dart", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase);

    public sealed class Result
    {
        public List<SourceFile> Files { get; } = new();
        public List<SourceFile> Configs { get; } = new();
        public bool Truncated { get; set; }
        public string? TruncatedWhy { get; set; }
        public int Skipped { get; set; }
    }

    // the last two projects' files (switching between two chats is the common case)
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SourceFile>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> CacheOrder = new();

    public static Result Read(string root, int maxFiles, CancellationToken ct)
    {
        var result = new Result();
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var cache = CacheFor(fullRoot);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var queue = new Queue<string>();
        queue.Enqueue(fullRoot.TrimEnd(Path.DirectorySeparatorChar));
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = queue.Dequeue();
            FileInfo[] files;
            DirectoryInfo[] dirs;
            try
            {
                var di = new DirectoryInfo(dir);
                files = di.GetFiles();
                dirs = di.GetDirectories();
            }
            catch { continue; }   // gone (deleted mid-scan) or unreadable: what it held is simply not drawn
            foreach (var fi in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                bool config = ConfigNames.Contains(fi.Name);
                if (!config && !SourceExt.Contains(fi.Extension)) continue;
                if (config && result.Configs.Count >= MaxConfigs) continue;
                if (!config && result.Files.Count >= maxFiles)
                {
                    result.Truncated = true;
                    result.TruncatedWhy ??= "files";
                    continue;
                }
                if (fi.Length > MaxFileBytes || Generated(fi.Name)) { result.Skipped++; continue; }
                if (!config && total + fi.Length > MaxTotalBytes)
                {
                    result.Truncated = true;
                    result.TruncatedWhy ??= "size";
                    continue;
                }
                var f = Load(fi, fullRoot, cache);
                if (f == null) { result.Skipped++; continue; }
                seen.Add(f.Full);
                if (config) result.Configs.Add(f);
                else
                {
                    result.Files.Add(f);
                    total += f.Length;
                }
            }
            foreach (var d in dirs.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (SkipDirs.Contains(d.Name) || d.Name.StartsWith('.')) continue;
                if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;   // a junction or link can loop back
                queue.Enqueue(d.FullName);
            }
        }
        foreach (var gone in cache.Keys.Where(k => !seen.Contains(k)).ToList()) cache.TryRemove(gone, out _);
        result.Truncated |= result.Files.Count >= maxFiles && result.TruncatedWhy != null;
        return result;
    }

    private static ConcurrentDictionary<string, SourceFile> CacheFor(string fullRoot)
    {
        if (Cache.TryGetValue(fullRoot, out var c)) return c;
        c = Cache.GetOrAdd(fullRoot, _ => new ConcurrentDictionary<string, SourceFile>(StringComparer.OrdinalIgnoreCase));
        CacheOrder.Enqueue(fullRoot);
        while (CacheOrder.Count > 2 && CacheOrder.TryDequeue(out var old))
            if (!string.Equals(old, fullRoot, StringComparison.OrdinalIgnoreCase)) Cache.TryRemove(old, out _);
        return c;
    }

    private static SourceFile? Load(FileInfo fi, string fullRoot, ConcurrentDictionary<string, SourceFile> cache)
    {
        try
        {
            if (cache.TryGetValue(fi.FullName, out var hit) && hit.Stamp == fi.LastWriteTimeUtc && hit.Length == fi.Length) return hit;
            byte[] bytes = File.ReadAllBytes(fi.FullName);
            // a UTF-16 text file (Windows tools still write them: .xaml, .cs from older Visual Studio) is full of NUL
            // bytes, so its BOM is read before the NUL probe; without a BOM, NULs mean binary, whatever the name says
            bool utf16 = bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF);
            int probe = utf16 ? 0 : Math.Min(bytes.Length, 8192);
            for (int i = 0; i < probe; i++)
                if (bytes[i] == 0) { cache.TryRemove(fi.FullName, out _); return null; }
            string text = DecodeText(bytes);
            string id = fi.FullName.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? fi.FullName[fullRoot.Length..].Replace('\\', '/') : fi.FullName;
            var f = new SourceFile
            {
                Full = fi.FullName, Id = id, Ext = fi.Extension.ToLowerInvariant(), Text = text,
                Stamp = fi.LastWriteTimeUtc, Length = fi.Length,
            };
            cache[fi.FullName] = f;
            return f;
        }
        catch { return null; }   // locked, deleted between listing and reading
    }

    private static string DecodeText(byte[] bytes)
    {
        // the BOM decides (UTF-8, UTF-16 LE/BE); none means UTF-8
        var text = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true).ReadToEnd();
        return text.Replace("\r\n", "\n");
    }

    /// <summary>For the harness: forget every cached file.</summary>
    internal static void ClearCache() { Cache.Clear(); while (CacheOrder.TryDequeue(out _)) { } }
}
