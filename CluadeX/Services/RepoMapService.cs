using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services;

/// <summary>
/// Builds a compact, ranked "repo map" — a symbol-level outline of the project (files → their key
/// type / function declarations) that gets injected into the agent's system prompt. This gives the
/// agent passive awareness of the whole codebase structure WITHOUT the user pointing at files — the
/// core of IDE-grade code intelligence (à la Aider's repomap). No embeddings required: extraction is
/// heuristic regex per language, bounded and cached per working directory so it's cheap to rebuild.
/// On Anthropic the map rides inside the prompt-cached system prompt, so it's effectively free after
/// the first request.
/// </summary>
public class RepoMapService
{
    private readonly FileSystemService _fileSystem;
    private string? _cachedMap;
    private string? _cachedForDir;
    private readonly object _lock = new();

    public RepoMapService(FileSystemService fileSystem) => _fileSystem = fileSystem;

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".java", ".rs", ".rb", ".php",
        ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".swift", ".kt", ".scala", ".dart",
    };

    private static readonly string[] IgnoreDirs =
        { "node_modules", "bin", "obj", "__pycache__", ".vs", "dist", "build", "target", ".next", "packages", "vendor" };

    // Heuristic, language-agnostic declaration matchers.
    private static readonly Regex TypeDecl = new(
        @"\b(class|interface|struct|enum|record|trait|impl|protocol)\s+([A-Za-z_]\w*)",
        RegexOptions.Compiled);
    private static readonly Regex FuncDecl = new(
        @"^\s*(?:@\w+\s+)*(?:public|private|protected|internal|export|static|async|abstract|virtual|override|final|pub|def|func|function|fn)\b[^;{=]*\b([A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled);

    /// <summary>Get (or build + cache) a ranked symbol map of the current project, capped at maxChars.</summary>
    public string GetRepoMap(int maxChars = 6000)
    {
        if (!_fileSystem.HasWorkingDirectory) return "";
        string dir = _fileSystem.WorkingDirectory;

        lock (_lock)
        {
            if (_cachedMap != null && string.Equals(_cachedForDir, dir, StringComparison.OrdinalIgnoreCase))
                return _cachedMap;
        }

        string map;
        try { map = BuildMap(dir, maxChars); }
        catch { map = ""; }

        lock (_lock) { _cachedMap = map; _cachedForDir = dir; }
        return map;
    }

    /// <summary>Drop the cache (e.g., the user switched projects or wants a fresh map after big edits).</summary>
    public void Invalidate()
    {
        lock (_lock) { _cachedMap = null; _cachedForDir = null; }
    }

    /// <summary>
    /// Ranked keyword search across the codebase — combines filename, declaration, and content signals
    /// into one relevance score so the agent gets the most relevant files for a query in a single call
    /// (better than raw grep, zero external deps). Foundation that embeddings-based search layers on later.
    /// </summary>
    public List<CodeHit> SearchCode(string query, int maxResults = 12)
    {
        var hits = new List<CodeHit>();
        if (!_fileSystem.HasWorkingDirectory) return hits;

        var terms = Tokenize(query);
        if (terms.Count == 0) return hits;

        string root = _fileSystem.WorkingDirectory;
        foreach (var file in EnumerateCodeFiles(root).Take(2000))
        {
            string rel;
            string[] lines;
            try
            {
                var fi = new FileInfo(file);
                if (fi.Length > 512 * 1024) continue;
                rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                lines = File.ReadAllLines(file);
            }
            catch { continue; }

            int score = 0;
            string relLower = rel.ToLowerInvariant();
            foreach (var t in terms)
                if (relLower.Contains(t)) score += 8; // filename match — strong signal

            int bestLine = -1, bestLineScore = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0 || line.Length > 400) continue;
                string lower = line.ToLowerInvariant();
                int lineScore = 0;
                foreach (var t in terms)
                    if (lower.Contains(t))
                        lineScore += IsDeclarationLine(line) ? 5 : 2; // a declaration line is more relevant
                if (lineScore > bestLineScore) { bestLineScore = lineScore; bestLine = i; }
            }

            score += bestLineScore;
            if (score <= 0) continue;
            hits.Add(new CodeHit
            {
                File = rel,
                Line = bestLine,
                Score = score,
                Snippet = bestLine >= 0 ? lines[bestLine].Trim() : "",
            });
        }

        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.File).Take(maxResults).ToList();
    }

    private static readonly Regex WordSplit = new(@"[^A-Za-z0-9_]+", RegexOptions.Compiled);

    private static List<string> Tokenize(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        return WordSplit.Split(query.ToLowerInvariant())
            .Where(w => w.Length >= 2)
            .Distinct()
            .Take(12)
            .ToList();
    }

    private static bool IsDeclarationLine(string line)
        => TypeDecl.IsMatch(line) || (line.Contains('(') && FuncDecl.IsMatch(line));

    private string BuildMap(string root, int maxChars)
    {
        var entries = new List<(int symbolCount, string block)>();

        foreach (var file in EnumerateCodeFiles(root).Take(600))
        {
            string[] lines;
            try
            {
                var fi = new FileInfo(file);
                if (fi.Length > 256 * 1024) continue; // skip huge / generated files
                lines = File.ReadAllLines(file);
            }
            catch { continue; }

            var symbols = ExtractSymbols(lines);
            if (symbols.Count == 0) continue;

            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var sb = new StringBuilder();
            sb.Append(rel).Append('\n');
            foreach (var s in symbols)
                sb.Append("  ").Append(s).Append('\n');
            entries.Add((symbols.Count, sb.ToString()));
        }

        // Rank by declaration count — the API-defining files (services, models) surface first.
        entries.Sort((a, b) => b.symbolCount.CompareTo(a.symbolCount));

        var outSb = new StringBuilder();
        foreach (var e in entries)
        {
            if (outSb.Length + e.block.Length > maxChars) break;
            outSb.Append(e.block);
        }
        return outSb.ToString().TrimEnd();
    }

    private static List<string> ExtractSymbols(string[] lines)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in lines)
        {
            if (result.Count >= 30) break;
            string line = raw.TrimEnd();
            if (line.Length == 0 || line.Length > 220) continue;

            string? sym = null;
            var t = TypeDecl.Match(line);
            if (t.Success)
                sym = $"{t.Groups[1].Value} {t.Groups[2].Value}";
            else if (line.Contains('(') && FuncDecl.IsMatch(line))
                sym = line.Trim().TrimEnd('{', ' ');

            if (sym == null) continue;
            if (sym.Length > 140) sym = sym[..140] + "…";
            if (seen.Add(sym)) result.Add(sym);
        }
        return result;
    }

    /// <summary>
    /// Manual recursive walk that PRUNES ignore dirs (never descends into node_modules/bin/obj/…) and
    /// catches per-directory access errors — unlike Directory.EnumerateFiles(AllDirectories), which
    /// descends into everything and aborts the whole enumeration on the first UnauthorizedAccess.
    /// </summary>
    private static IEnumerable<string> EnumerateCodeFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            string[] dirFiles;
            try { dirFiles = Directory.GetFiles(dir); }
            catch { continue; }
            foreach (var f in dirFiles)
                if (CodeExtensions.Contains(Path.GetExtension(f)))
                    yield return f;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var sd in subdirs)
            {
                string name = Path.GetFileName(sd);
                if (name.StartsWith('.')) continue;
                if (Array.IndexOf(IgnoreDirs, name) >= 0) continue;
                // Skip symlinks/junctions — a reparse point looping back to an ancestor would re-yield the
                // same files until the .Take() cap, crowding out real files.
                try { if ((File.GetAttributes(sd) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                stack.Push(sd);
            }
        }
    }

    // ── Code navigation (Wave 2): symbol definition search + per-file outline. No LSP needed. ──

    /// <summary>
    /// Find where a symbol is DECLARED across the repo (a "go to definition" by NAME — the agent passes a
    /// name, not line/char coords, so weak models can use it). Regex-based; ranks exact type/method
    /// declarations highest. Returns file + 0-based line + the declaration snippet.
    /// </summary>
    public List<CodeHit> FindSymbolDefinitions(string name, int maxResults = 25)
    {
        var hits = new List<CodeHit>();
        if (!_fileSystem.HasWorkingDirectory || string.IsNullOrWhiteSpace(name)) return hits;

        string needle = name.Trim();
        Regex nameRx;
        try { nameRx = new Regex($@"\b{Regex.Escape(needle)}\b", RegexOptions.Compiled); }
        catch { return hits; }
        string root = _fileSystem.WorkingDirectory;

        foreach (var file in EnumerateCodeFiles(root).Take(3000))
        {
            string rel; string[] lines;
            try
            {
                var fi = new FileInfo(file);
                if (fi.Length > 512 * 1024) continue;
                rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                lines = File.ReadAllLines(file);
            }
            catch { continue; }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0 || line.Length > 400) continue;
                if (!nameRx.IsMatch(line) || !IsDeclarationLine(line)) continue;

                int score = 10;
                var t = TypeDecl.Match(line);
                if (t.Success && t.Groups[2].Value == needle) score += 25;          // 'class/record/… Needle'
                else if (Regex.IsMatch(line, $@"\b{Regex.Escape(needle)}\s*\(")) score += 15; // method/func named Needle
                hits.Add(new CodeHit { File = rel, Line = i, Score = score, Snippet = line.Trim() });
            }
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.File).Take(maxResults).ToList();
    }

    /// <summary>Line-numbered (1-based) outline of one file's top-level symbols. Pure — pass the content in.</summary>
    public List<(int line, string symbol)> OutlineContent(string content)
    {
        var result = new List<(int, string)>();
        if (string.IsNullOrEmpty(content)) return result;
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.Length == 0 || line.Length > 220) continue;
            string? sym = null;
            var t = TypeDecl.Match(line);
            if (t.Success) sym = $"{t.Groups[1].Value} {t.Groups[2].Value}";
            else if (line.Contains('(') && FuncDecl.IsMatch(line)) sym = line.Trim().TrimEnd('{', ' ');
            if (sym == null) continue;
            if (sym.Length > 160) sym = sym[..160] + "…";
            result.Add((i + 1, sym));
            if (result.Count >= 400) break;
        }
        return result;
    }
}

/// <summary>A ranked code-search hit: file + best-matching line (0-based, -1 = filename-only match) + score.</summary>
public class CodeHit
{
    public string File { get; set; } = "";
    public int Line { get; set; } = -1;
    public int Score { get; set; }
    public string Snippet { get; set; } = "";
}
