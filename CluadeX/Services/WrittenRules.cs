using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace CluadeX.Services;

/// <summary>
/// The rules the user already wrote down — the project's CLAUDE.md / AGENTS.md / GEMINI.md and the global ones the CLI
/// agents read. An instinct that only repeats one of them is a second copy of the same rule (owner 2026-11-10: "กฎ
/// ซ้ำซ้อนไม่ดีแน่ๆ"): it would go into the prompt next to the file that already says it, and the two can drift apart.
/// So the extractor is shown these rules and told not to learn them again, and an instinct that repeats one anyway is
/// neither recorded nor put into a prompt.
///
/// The match is by words (<see cref="Repeats"/>): the instinct's content words almost all inside one rule line. It
/// catches a lesson that restates a line; one the file only implies is left to the extractor, which read the rules.
/// </summary>
public static class WrittenRules
{
    /// <summary>The files in a project (its git checkout root, and the chat's own folder when it is deeper).</summary>
    public static readonly string[] ProjectFiles =
        { "CLAUDE.md", "CLAUDE.local.md", ".claude/CLAUDE.md", ".cluadex/CLAUDE.md", "AGENTS.md", "GEMINI.md" };

    /// <summary>The user's global rule files. Tests point it elsewhere.</summary>
    internal static IReadOnlyList<string> GlobalFiles { get; set; } = DefaultGlobalFiles();

    private static IReadOnlyList<string> DefaultGlobalFiles()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Path.Combine(home, ".claude", "CLAUDE.md"),
            Path.Combine(home, ".codex", "AGENTS.md"),
            Path.Combine(home, ".gemini", "GEMINI.md"),
        };
    }

    /// <summary>How much of the rules the extractor is shown.</summary>
    public const int MaxPromptChars = 6000;

    /// <summary>A file larger than this is not read (a generated or pasted dump, not rules).</summary>
    private const long MaxFileBytes = 512 * 1024;

    /// <summary>An instinct is a repeat when at least this share of its content words is in one rule line.</summary>
    private const double CoveredShare = 0.8;

    internal sealed record Piece(string Text, HashSet<string> Stems, bool Never, bool Always, string Compact);

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, long Length, List<Piece> Pieces)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The rule files that apply to a chat in <paramref name="projectFolder"/>, existing ones only, the
    /// project's first.</summary>
    public static List<string> FilesFor(string? projectFolder)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectFolder))
        {
            try
            {
                string full = Path.GetFullPath(projectFolder).TrimEnd('\\', '/');
                if (InstinctService.ProjectKey(full) is { } root) dirs.Add(root);
                if (!dirs.Any(d => d.Equals(full, StringComparison.OrdinalIgnoreCase))) dirs.Add(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        var files = dirs.SelectMany(d => ProjectFiles.Select(f => Path.Combine(d, f)))
            .Concat(GlobalFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToList();
        return files;
    }

    /// <summary>The rules for the extractor's prompt ("" when there are none), at most <paramref name="maxChars"/>.</summary>
    public static string ForPrompt(string? projectFolder, int maxChars = MaxPromptChars)
    {
        var sb = new StringBuilder();
        foreach (var piece in PiecesFor(projectFolder))
        {
            string line = "- " + piece.Text;
            if (sb.Length + line.Length + 1 > maxChars) break;
            sb.Append(line).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>True when <paramref name="pattern"/> says what one of the written rules already says.</summary>
    public static bool Repeats(string? pattern, string? projectFolder) => RepeatsIn(pattern, PiecesFor(projectFolder));

    /// <summary><see cref="Repeats(string?, string?)"/> against rules read once (<see cref="PiecesFor"/>) for many patterns.</summary>
    internal static bool RepeatsIn(string? pattern, IReadOnlyList<Piece> pieces)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pieces.Count == 0) return false;
        var raw = InstinctService.RawWords(pattern);
        bool never = raw.Contains("never"), always = raw.Contains("always");
        var stems = InstinctService.Stems(raw);
        string compact = InstinctService.Compact(pattern);
        foreach (var p in pieces)
        {
            if ((never && p.Always && !p.Never) || (always && p.Never && !p.Always)) continue;   // the opposite rule
            if (stems.Count >= 3)
            {
                int inside = stems.Count(p.Stems.Contains);
                if (inside >= CoveredShare * stems.Count) return true;
            }
            else if (compact.Length >= 4)
            {
                // Short or unspaced text (Thai): the rule line holds it, or the two read the same.
                if (p.Compact.Contains(compact, StringComparison.Ordinal)) return true;
                if (p.Compact.Length <= compact.Length * 2 && InstinctService.Dice(compact, p.Compact) >= 0.8) return true;
            }
        }
        return false;
    }

    internal static List<Piece> PiecesFor(string? projectFolder)
    {
        var all = new List<Piece>();
        foreach (var file in FilesFor(projectFolder)) all.AddRange(Read(file));
        return all;
    }

    private static List<Piece> Read(string file)
    {
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists || fi.Length > MaxFileBytes) return new();
            if (Cache.TryGetValue(file, out var hit) && hit.Stamp == fi.LastWriteTimeUtc && hit.Length == fi.Length)
                return hit.Pieces;
            var pieces = Split(File.ReadAllText(file));
            Cache[file] = (fi.LastWriteTimeUtc, fi.Length, pieces);
            return pieces;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>The lines of a rules file that can hold a rule: no code blocks, markdown marks gone, a table row
    /// split into its cells, a long paragraph into its sentences.</summary>
    internal static List<Piece> Split(string text)
    {
        var pieces = new List<Piece>();
        bool inCode = false;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }
            if (inCode || line.Length < 3) continue;
            var parts = line.StartsWith('|') ? line.Split('|') : new[] { line };
            foreach (var part in parts)
            {
                string clean = Clean(part);
                if (clean.Length < 3 || clean.All(c => c is '-' or ':' or ' ')) continue;
                if (clean.Length <= 300) { Add(pieces, clean); continue; }
                foreach (var sentence in clean.Split(new[] { ". ", "; ", "? ", "! " }, StringSplitOptions.RemoveEmptyEntries))
                    if (sentence.Trim().Length >= 3) Add(pieces, sentence.Trim());
            }
        }
        return pieces;
    }

    private static void Add(List<Piece> pieces, string text)
    {
        var raw = InstinctService.RawWords(text);
        pieces.Add(new Piece(text, InstinctService.Stems(raw), raw.Contains("never"), raw.Contains("always"),
            InstinctService.Compact(text)));
    }

    private static string Clean(string s)
    {
        s = s.Trim().TrimStart('#', '>', '-', '*', '+', ' ', '\t');
        int i = 0;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i > 0 && i < s.Length && s[i] is '.' or ')') s = s[(i + 1)..];
        return s.Replace("**", "").Replace("`", "").Replace("__", "").Trim();
    }

    /// <summary>Forget what was read (tests).</summary>
    internal static void ClearCache() => Cache.Clear();
}
