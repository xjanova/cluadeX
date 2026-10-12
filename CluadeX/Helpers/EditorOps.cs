using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Helpers;

/// <summary>
/// The editor's text tools, as plain functions on strings so they can be proven without a window: comment
/// toggling per language, the Notepad++ line operations (sort, remove duplicates / empty lines, trim, join, case,
/// tabs ↔ spaces), line endings, and find / replace. The editor view applies the results to the document as one
/// undoable edit.
/// </summary>
public static class EditorOps
{
    // ───────────────────────── comments ─────────────────────────

    /// <summary>How a file comments a line, or wraps a block (null when it has no such syntax).</summary>
    public sealed record CommentSyntax(string? Line, string? BlockOpen, string? BlockClose)
    {
        public bool IsNone => Line == null && BlockOpen == null;
    }

    public static CommentSyntax CommentSyntaxFor(string fileName)
    {
        string name = Path.GetFileName(fileName).ToLowerInvariant();
        if (name is "dockerfile" or "makefile" or "gnumakefile" or ".gitignore" or ".gitattributes" or ".dockerignore"
            or ".editorconfig" or ".env" or "cmakelists.txt" or "requirements.txt" or "gemfile" or "rakefile" or "procfile")
            return new("#", null, null);
        if (name.StartsWith(".env")) return new("#", null, null);
        string ext = Path.GetExtension(name).TrimStart('.');
        return ext switch
        {
            "cs" or "csx" or "java" or "js" or "mjs" or "cjs" or "jsx" or "ts" or "tsx" or "c" or "h" or "cpp" or "cc" or "cxx"
                or "hpp" or "hh" or "go" or "rs" or "swift" or "kt" or "kts" or "dart" or "scala" or "groovy" or "gradle"
                or "php" or "jsonc" or "json5" or "fs" or "fsx" or "gdshader" or "shader" or "hlsl" or "glsl" or "frag" or "vert"
                or "cg" or "proto" or "scss" or "less" or "sol" or "zig" or "v" or "verilog" or "sv" or "m" or "mm"
                => new("//", "/*", "*/"),
            "py" or "pyw" or "rb" or "sh" or "bash" or "zsh" or "fish" or "ps1" or "psm1" or "psd1" or "yaml" or "yml"
                or "toml" or "r" or "pl" or "pm" or "conf" or "cfg" or "properties" or "gd" or "nim" or "cmake" or "mk"
                or "tf" or "hcl" or "dockerfile" or "jl" or "coffee" or "cr" or "ex" or "exs" or "gitignore" or "env"
                => new("#", null, null),
            "sql" or "lua" or "hs" or "elm" or "ada" or "vhd" or "vhdl" => new("--", null, null),
            "ini" or "asm" or "s" or "lisp" or "clj" or "cljs" or "el" or "scm" or "rkt" => new(";", null, null),
            "bat" or "cmd" => new("REM", null, null),
            "vb" or "vbs" or "bas" => new("'", null, null),
            "tex" or "sty" or "erl" or "matlab" => new("%", null, null),
            "html" or "htm" or "xhtml" or "xml" or "xaml" or "csproj" or "vbproj" or "fsproj" or "props" or "targets"
                or "config" or "svg" or "xsl" or "xslt" or "resx" or "plist" or "md" or "markdown" or "vue" or "svelte"
                => new(null, "<!--", "-->"),
            "css" => new(null, "/*", "*/"),
            _ => new(null, null, null),
        };
    }

    /// <summary>
    /// Comment or uncomment lines with a line-comment token, the way every editor's Ctrl+/ does: when every
    /// non-blank line is already commented they are uncommented, otherwise all are commented at the shallowest
    /// indent. Blank lines are left alone.
    /// </summary>
    public static string[] ToggleLineComment(string[] lines, string token)
    {
        var body = lines.Where(l => l.Trim().Length > 0).ToList();
        if (body.Count == 0) return lines;
        bool allCommented = body.All(l => l.TrimStart().StartsWith(token, StringComparison.Ordinal));
        if (allCommented)
        {
            return lines.Select(l =>
            {
                int at = l.Length - l.TrimStart().Length;
                if (l.Trim().Length == 0 || string.CompareOrdinal(l, at, token, 0, token.Length) != 0) return l;
                int cut = token.Length + (at + token.Length < l.Length && l[at + token.Length] == ' ' ? 1 : 0);
                return l.Remove(at, cut);
            }).ToArray();
        }
        int indent = body.Min(l => l.Length - l.TrimStart().Length);
        return lines.Select(l => l.Trim().Length == 0 ? l : l.Insert(indent, token + " ")).ToArray();
    }

    /// <summary>Wrap a block in open/close comment markers, or unwrap it when it already is wrapped. The block
    /// keeps its indentation: the markers go just inside it.</summary>
    public static string ToggleBlockComment(string block, string open, string close)
    {
        string trimmed = block.Trim();
        if (trimmed.StartsWith(open, StringComparison.Ordinal) && trimmed.EndsWith(close, StringComparison.Ordinal)
            && trimmed.Length >= open.Length + close.Length)
        {
            int start = block.IndexOf(open, StringComparison.Ordinal);
            int end = block.LastIndexOf(close, StringComparison.Ordinal);
            string inner = block.Substring(start + open.Length, end - start - open.Length);
            if (inner.StartsWith(' ')) inner = inner[1..];
            if (inner.EndsWith(' ')) inner = inner[..^1];
            return block[..start] + inner + block[(end + close.Length)..];
        }
        int lead = block.Length - block.TrimStart().Length;
        int trail = block.Length - block.TrimEnd().Length;
        return block[..lead] + open + " " + block.Substring(lead, block.Length - lead - trail) + " " + close + block[(block.Length - trail)..];
    }

    // ───────────────────────── line operations ─────────────────────────

    /// <summary>Sorted like a dictionary in the user's language (Thai leading vowels sort by their consonant).</summary>
    public static string[] SortLines(string[] lines, bool descending, bool ignoreCase)
    {
        var cmp = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase);
        var sorted = lines.OrderBy(l => l, cmp).ThenBy(l => l, StringComparer.Ordinal).ToArray();
        if (descending) Array.Reverse(sorted);
        return sorted;
    }

    /// <summary>Each line once, in the order first seen.</summary>
    public static string[] RemoveDuplicateLines(string[] lines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return lines.Where(seen.Add).ToArray();
    }

    public static string[] RemoveEmptyLines(string[] lines) => lines.Where(l => l.Trim().Length > 0).ToArray();

    public static string[] TrimTrailing(string[] lines) => lines.Select(l => l.TrimEnd(' ', '\t')).ToArray();

    /// <summary>One line: the first keeps its indent, the rest are trimmed and joined with a space.</summary>
    public static string JoinLines(string[] lines)
    {
        if (lines.Length == 0) return "";
        var sb = new StringBuilder(lines[0].TrimEnd());
        foreach (var l in lines.Skip(1))
        {
            string t = l.Trim();
            if (t.Length == 0) continue;
            if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1])) sb.Append(' ');
            sb.Append(t);
        }
        return sb.ToString();
    }

    public enum CaseKind { Upper, Lower, Title, Invert }

    public static string ChangeCase(string text, CaseKind kind)
    {
        var ti = CultureInfo.CurrentCulture.TextInfo;
        return kind switch
        {
            CaseKind.Upper => ti.ToUpper(text),
            CaseKind.Lower => ti.ToLower(text),
            CaseKind.Title => ti.ToTitleCase(ti.ToLower(text)),
            _ => new string(text.Select(c => char.IsUpper(c) ? char.ToLower(c) : char.IsLower(c) ? char.ToUpper(c) : c).ToArray()),
        };
    }

    /// <summary>Every tab becomes the spaces up to the next tab stop.</summary>
    public static string[] TabsToSpaces(string[] lines, int tabSize)
    {
        return lines.Select(l =>
        {
            if (!l.Contains('\t')) return l;
            var sb = new StringBuilder(l.Length + 8);
            foreach (char c in l)
            {
                if (c == '\t') sb.Append(' ', tabSize - sb.Length % tabSize);
                else sb.Append(c);
            }
            return sb.ToString();
        }).ToArray();
    }

    /// <summary>Leading spaces become tabs (whole tab stops only; the remainder stays as spaces).</summary>
    public static string[] LeadingSpacesToTabs(string[] lines, int tabSize)
    {
        return lines.Select(l =>
        {
            int width = 0, i = 0;
            for (; i < l.Length && (l[i] == ' ' || l[i] == '\t'); i++)
                width = l[i] == '\t' ? (width / tabSize + 1) * tabSize : width + 1;
            if (i == 0) return l;
            return new string('\t', width / tabSize) + new string(' ', width % tabSize) + l[i..];
        }).ToArray();
    }

    // ───────────────────────── line endings ─────────────────────────

    public enum Eol { None, CrLf, Lf, Cr, Mixed }

    /// <summary>The line ending a text uses — Mixed when it has more than one kind.</summary>
    public static Eol DetectEol(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (c == '\n') lf++;
        }
        int kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        return kinds == 0 ? Eol.None : kinds > 1 ? Eol.Mixed : crlf > 0 ? Eol.CrLf : lf > 0 ? Eol.Lf : Eol.Cr;
    }

    public static string EolText(Eol eol) => eol switch { Eol.Lf => "\n", Eol.Cr => "\r", _ => "\r\n" };
    public static string EolLabel(Eol eol) => eol switch
    {
        Eol.CrLf => "CRLF", Eol.Lf => "LF", Eol.Cr => "CR", Eol.Mixed => "Mixed", _ => "CRLF",
    };

    private static readonly Regex AnyEol = new(@"\r\n|\r|\n", RegexOptions.Compiled);

    public static string ConvertEol(string text, Eol to) => AnyEol.Replace(text, EolText(to));

    // ───────────────────────── find / replace ─────────────────────────

    public sealed record FindOptions(bool MatchCase, bool WholeWord, bool UseRegex);

    /// <summary>A search can never hang the editor: each match attempt gives up after this.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The regex a find runs. Plain text is escaped; "whole word" means no letter, digit or underscore on either
    /// side. In regex mode two Windows-file traps are taken out, as Notepad++ does: '.' never matches \r or \n
    /// (so "a.*" on a CRLF line does not eat the \r), and '$' matches before "\r\n" too. Null and a reason for an
    /// invalid pattern.
    /// </summary>
    public static Regex? BuildFindRegex(string query, FindOptions o, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(query)) return null;
        string pattern = o.UseRegex ? AdaptRegex(query) : Regex.Escape(query);
        if (o.WholeWord) pattern = @"(?<![\w])(?:" + pattern + @")(?![\w])";
        var opts = RegexOptions.CultureInvariant | RegexOptions.Multiline;
        if (!o.MatchCase) opts |= RegexOptions.IgnoreCase;
        try { return new Regex(pattern, opts, MatchTimeout); }
        catch (ArgumentException ex) { error = ex.Message; return null; }
    }

    /// <summary>'.' → [^\r\n] and '$' → (?=\r?$) outside character classes and escapes.</summary>
    internal static string AdaptRegex(string pattern)
    {
        var sb = new StringBuilder(pattern.Length + 16);
        bool inClass = false;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length) { sb.Append(c).Append(pattern[++i]); continue; }
            if (inClass) { if (c == ']') inClass = false; sb.Append(c); continue; }
            if (c == '[')
            {
                inClass = true;
                sb.Append(c);
                // A ']' right after '[' or '[^' is a literal member, not the end of the class.
                if (i + 1 < pattern.Length && pattern[i + 1] == '^') sb.Append(pattern[++i]);
                if (i + 1 < pattern.Length && pattern[i + 1] == ']') sb.Append(pattern[++i]);
                continue;
            }
            if (c == '.') { sb.Append(@"[^\r\n]"); continue; }
            if (c == '$') { sb.Append(@"(?=\r?$)"); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The next match at or after <paramref name="start"/> (or the last one ending at or before it when going
    /// backwards), wrapping around the end. Null when there is none — or when the pattern timed out.
    /// </summary>
    public static (int Index, int Length)? FindNext(string text, Regex rx, int start, bool backwards, bool wrap = true)
    {
        start = Math.Clamp(start, 0, text.Length);
        try
        {
            if (!backwards)
            {
                var m = rx.Match(text, start);
                if (m.Success) return (m.Index, m.Length);
                if (!wrap || start == 0) return null;
                m = rx.Match(text, 0);
                return m.Success && m.Index < start ? (m.Index, m.Length) : null;
            }
            // The last match starting before the caret; wrapping, the last one in the text.
            (int, int)? before = null, last = null;
            for (var m = rx.Match(text, 0); m.Success; m = m.NextMatch())
            {
                if (m.Index < start) before = (m.Index, m.Length);
                else if (!wrap || before != null) break;
                last = (m.Index, m.Length);
            }
            return before ?? (wrap ? last : null);
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>How many matches (stops counting at <paramref name="cap"/>); -1 when the pattern timed out.</summary>
    public static int CountMatches(string text, Regex rx, int cap = 100_000)
    {
        int n = 0;
        try
        {
            for (var m = rx.Match(text); m.Success && n < cap; m = m.NextMatch()) n++;
            return n;
        }
        catch (RegexMatchTimeoutException) { return -1; }
    }

    /// <summary>What one match is replaced with: the text as typed, or in regex mode with $1 / ${name} groups and
    /// \n \r \t \\ escapes.</summary>
    public static string ReplacementFor(Match m, string replacement, bool useRegex) =>
        useRegex ? m.Result(ExpandEscapes(replacement)) : replacement;

    /// <summary>Every replacement a Replace All makes, front to back: (index, length, new text).</summary>
    public static List<(int Index, int Length, string Text)> Replacements(string text, Regex rx, string replacement, bool useRegex)
    {
        var list = new List<(int, int, string)>();
        string expanded = useRegex ? ExpandEscapes(replacement) : replacement;
        for (var m = rx.Match(text); m.Success; m = m.NextMatch())
            list.Add((m.Index, m.Length, useRegex ? m.Result(expanded) : expanded));
        return list;
    }

    /// <summary>Replace All on a string: the new text and how many were replaced.</summary>
    public static (string Text, int Count) ReplaceAll(string text, Regex rx, string replacement, bool useRegex)
    {
        int count = 0;
        string expanded = useRegex ? ExpandEscapes(replacement) : replacement;
        string result = rx.Replace(text, m => { count++; return useRegex ? m.Result(expanded) : expanded; });
        return (result, count);
    }

    /// <summary>\n \r \t \0 and \\ in a regex-mode replacement; $ groups are left to Match.Result.</summary>
    internal static string ExpandEscapes(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            char n = s[++i];
            sb.Append(n switch { 'n' => "\n", 'r' => "\r", 't' => "\t", '0' => "\0", '\\' => "\\", _ => "\\" + n });
        }
        return sb.ToString();
    }

    /// <summary>"120" or "120:8" (line, optional column), both 1-based.</summary>
    public static bool TryParseGoTo(string input, out int line, out int column)
    {
        line = 0; column = 1;
        var parts = (input ?? "").Trim().Split(new[] { ':', ',' }, StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out line) || line < 1)
            return false;
        if (parts.Length == 2 && (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out column) || column < 1))
            return false;
        return true;
    }

    /// <summary>A selection worth highlighting everywhere else on screen (Notepad++ "smart highlighting"): one
    /// word of 2–100 letters, digits or underscores.</summary>
    public static bool IsSmartHighlightWord(string selected) =>
        selected.Length is >= 2 and <= 100 && selected.All(c => char.IsLetterOrDigit(c) || c == '_');
}
