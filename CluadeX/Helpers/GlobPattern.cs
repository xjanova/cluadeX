using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Helpers;

/// <summary>
/// File-glob matching with ripgrep's semantics, shared by grep_search, glob and search_files.
///
/// • A pattern without a slash matches the file NAME at any depth ("*.cs" ≡ "**/*.cs").
/// • "**/" matches zero or more directories; "*" and "?" never cross a slash.
/// • One level of braces expands: "*.{cs,xaml}".
/// • Several patterns can be given, separated by commas or spaces; a leading "!" excludes.
///
/// Paths are compared relative to the search root with forward slashes, case-insensitively (Windows).
/// </summary>
public sealed class GlobPattern
{
    private readonly List<Regex> _includes = new();
    private readonly List<Regex> _excludes = new();

    public bool IsEmpty => _includes.Count == 0 && _excludes.Count == 0;

    public static GlobPattern Parse(string? patterns)
    {
        var g = new GlobPattern();
        if (string.IsNullOrWhiteSpace(patterns)) return g;

        foreach (var raw in SplitTopLevel(patterns))
        {
            string p = raw.Trim();
            if (p.Length == 0 || p is "*" or "*.*" or "**" or "**/*") continue;   // "everything" is no filter
            bool exclude = p.StartsWith('!');
            if (exclude) p = p[1..];
            foreach (var expanded in ExpandBraces(p))
            {
                var rx = ToRegex(expanded);
                if (rx == null) continue;
                (exclude ? g._excludes : g._includes).Add(rx);
            }
        }
        return g;
    }

    /// <summary>True when <paramref name="relativePath"/> (forward slashes) passes the filter.</summary>
    public bool Matches(string relativePath)
    {
        if (_excludes.Any(r => r.IsMatch(relativePath))) return false;
        return _includes.Count == 0 || _includes.Any(r => r.IsMatch(relativePath));
    }

    // Split on commas/whitespace that are NOT inside braces, so "*.{cs,xaml}" stays one pattern.
    private static IEnumerable<string> SplitTopLevel(string s)
    {
        var sb = new StringBuilder();
        int depth = 0;
        foreach (char c in s)
        {
            if (c == '{') depth++;
            else if (c == '}') depth = Math.Max(0, depth - 1);
            if (depth == 0 && (c == ',' || char.IsWhiteSpace(c)))
            {
                if (sb.Length > 0) yield return sb.ToString();
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static IEnumerable<string> ExpandBraces(string p)
    {
        int open = p.IndexOf('{');
        int close = open >= 0 ? p.IndexOf('}', open) : -1;
        if (open < 0 || close < 0) { yield return p; yield break; }
        string head = p[..open], tail = p[(close + 1)..];
        foreach (var alt in p[(open + 1)..close].Split(','))
            foreach (var rest in ExpandBraces(tail))
                yield return head + alt + rest;
    }

    private static Regex? ToRegex(string glob)
    {
        glob = glob.Replace('\\', '/');
        if (glob.StartsWith("./")) glob = glob[2..];
        bool anchored = glob.StartsWith('/');
        if (anchored) glob = glob[1..];
        if (!anchored && !glob.Contains('/')) glob = "**/" + glob;       // bare name → any depth
        if (glob.EndsWith('/')) glob += "**";

        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*')
            {
                bool doubleStar = i + 1 < glob.Length && glob[i + 1] == '*';
                if (doubleStar)
                {
                    if (i + 2 < glob.Length && glob[i + 2] == '/') { sb.Append("(?:.*/)?"); i += 2; }
                    else { sb.Append(".*"); i++; }
                }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        try { return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
        catch { return null; }
    }
}
