using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Problems;

/// <summary>
/// The comment the editor puts on a line the user edited by hand (owner 2026-11-09: "มาร์กคอมเม้นต์ไว้เอง ว่าจุดไหน user แก้
/// และสามารถนำมาค้นหาภายหลังได้"): <c>// [user-edit 2026-11-09]</c> in the file's own line-comment syntax, at the END of the
/// first line of each run of edited lines — never on a line of its own, so no line moves.
///
/// One searchable tag: <see cref="Tag"/> (<c>[user-edit</c>). A marker is only written where it cannot change what the
/// code does: a small per-language scanner (<see cref="SafeLineEnds"/>) knows where each line ends — in code, in a line
/// comment, or inside a string / block comment / heredoc that runs on — and a marker goes only after code (not after a
/// comment already there), never on a line that continues (<c>\</c>, a PowerShell backtick). Languages it does not know, JSON, Markdown,
/// XML/HTML/XAML, YAML, JSX/TSX, PHP and data files get no marker (the editor still remembers the lines: UserEditStore).
/// </summary>
public static class UserEditMarker
{
    /// <summary>What every marker contains and what search looks for.</summary>
    public const string Tag = "[user-edit";

    /// <summary>A marker as written: the tag, a date, the closing bracket.</summary>
    public static string Text(DateTime date) => $"{Tag} {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}]";

    /// <summary>How a marker is wrapped in a file: <c>Open</c> before it, <c>Close</c> after (block-comment languages).</summary>
    public sealed record Style(string Open, string Close, Family Family);

    public enum Family { CLike, Hash, Shell, PowerShell, Ruby, Dash, Lua, Css }

    /// <summary>The marker style for a file, or null when markers are not written in it.</summary>
    public static Style? StyleFor(string fileName)
    {
        string name = Path.GetFileName(fileName ?? "").ToLowerInvariant();
        if (name is "dockerfile" or "makefile" or "gnumakefile" or "cmakelists.txt") return new("# ", "", Family.Shell);
        string ext = Path.GetExtension(name).TrimStart('.');
        return ext switch
        {
            "cs" or "csx" or "java" or "js" or "mjs" or "cjs" or "ts" or "mts" or "cts" or "c" or "h" or "cpp" or "cc" or "cxx"
                or "hpp" or "hh" or "hxx" or "go" or "rs" or "swift" or "kt" or "kts" or "dart" or "scala" or "groovy" or "gradle"
                or "jsonc" or "json5" or "proto" or "scss" or "less" or "sol" or "zig" or "glsl" or "hlsl" or "shader" or "frag"
                or "vert" or "gdshader" or "m" or "mm"
                => new("// ", "", Family.CLike),
            "py" or "pyw" or "pyi" or "r" or "jl" or "nim" or "cmake" or "toml" or "tf" or "hcl" or "gd" or "ex" or "exs" or "cr"
                => new("# ", "", Family.Hash),
            "sh" or "bash" or "zsh" or "fish" or "mk" or "pl" or "pm" or "dockerfile" => new("# ", "", Family.Shell),
            "ps1" or "psm1" or "psd1" => new("# ", "", Family.PowerShell),
            "rb" or "rake" or "gemspec" => new("# ", "", Family.Ruby),
            "sql" or "hs" => new("-- ", "", Family.Dash),
            "lua" => new("-- ", "", Family.Lua),
            "css" => new("/* ", " */", Family.Css),
            _ => null,
        };
    }

    private static readonly Regex MarkerRx = new(
        @"\s*(?://|#|--|/\*)\s*\[user-edit(?:\s+\d{4}-\d{2}-\d{2})?\](?:\s*\*/)?\s*$", RegexOptions.CultureInvariant);

    private static readonly Regex DateRx = new(@"\[user-edit\s+(\d{4}-\d{2}-\d{2})\]", RegexOptions.CultureInvariant);

    public static bool HasMarker(string line) => line.Contains(Tag, StringComparison.Ordinal);

    /// <summary>The marker's date (yyyy-MM-dd), or "" when the line has none or no date.</summary>
    public static string DateOf(string line) => DateRx.Match(line) is { Success: true } m ? m.Groups[1].Value : "";

    /// <summary>The line without a marker the editor put at its end (other text, other comments stay).</summary>
    public static string Strip(string line) => MarkerRx.Replace(line, "");

    /// <summary>A line as user-edit records compare it: marker removed, whitespace at both ends ignored.</summary>
    public static string Normalize(string line) => Strip(line).Trim();

    // ───────────────────────── where a marker may go ─────────────────────────

    private enum St { Code, Block, MultiString, Heredoc }

    /// <summary>
    /// Per line of <paramref name="text"/> (index 0 = line 1): true when a comment appended at its end would be read as a
    /// comment by the language — the line ends in code (not in a comment of its own), it does not continue onto the next line, and
    /// no string, block comment or heredoc is open there. An unknown language: all false.
    /// </summary>
    public static bool[] SafeLineEnds(string text, string fileName)
    {
        var lines = SplitLines(text);
        var safe = new bool[lines.Length];
        var style = StyleFor(fileName);
        if (style == null) return safe;
        var f = style.Family;
        bool cLike = f is Family.CLike or Family.Css;
        string ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        bool js = ext is "js" or "mjs" or "cjs" or "ts" or "mts" or "cts";

        St state = St.Code;
        string close = "";        // what ends the open block / multi-line string / heredoc
        bool closeEscapes = false; // a backslash escapes inside the open multi-line string
        bool verbatimQuote = false; // C# @"…": "" is a quote, not the end

        for (int li = 0; li < lines.Length; li++)
        {
            string s = lines[li];
            int i = 0;
            bool lineComment = false, brokenString = false;

            if (state == St.Heredoc)
            {
                // The heredoc ends at a line that is exactly its word (<<- allows leading tabs); that line is still part of it.
                if (s.Trim() == close) state = St.Code;
                safe[li] = false;
                continue;
            }

            while (i < s.Length && !lineComment)
            {
                if (state == St.Block)
                {
                    int end = s.IndexOf(close, i, StringComparison.Ordinal);
                    if (end < 0) { i = s.Length; break; }
                    i = end + close.Length;
                    state = St.Code;
                    continue;
                }
                if (state == St.MultiString)
                {
                    int k = i;
                    bool closed = false;
                    while (k < s.Length)
                    {
                        if (closeEscapes && s[k] == '\\') { k += 2; continue; }
                        if (verbatimQuote && s[k] == '"' && k + 1 < s.Length && s[k + 1] == '"') { k += 2; continue; }
                        if (string.CompareOrdinal(s, k, close, 0, close.Length) == 0) { k += close.Length; closed = true; break; }
                        k++;
                    }
                    i = Math.Min(k, s.Length);
                    if (closed) { state = St.Code; verbatimQuote = false; }
                    continue;
                }

                // ── code ──
                char c = s[i];
                string rest = s.Substring(i);

                // block comments and line comments
                if (f == Family.Lua && rest.StartsWith("--[") && LuaLong(rest, 2) is { } lc) { state = St.Block; close = lc.Close; i += lc.OpenLen; continue; }
                if (cLike && rest.StartsWith("/*")) { state = St.Block; close = "*/"; i += 2; continue; }
                if (f == Family.Dash && ext == "sql" && rest.StartsWith("/*")) { state = St.Block; close = "*/"; i += 2; continue; }
                if (f == Family.Dash && ext == "hs" && rest.StartsWith("{-")) { state = St.Block; close = "-}"; i += 2; continue; }
                if (f == Family.PowerShell && rest.StartsWith("<#")) { state = St.Block; close = "#>"; i += 2; continue; }
                if (f == Family.Ruby && i == 0 && s.StartsWith("=begin")) { state = St.Block; close = "=end"; i = s.Length; continue; }
                if (f == Family.CLike && rest.StartsWith("//")) { lineComment = true; break; }
                if (f is Family.Dash or Family.Lua && rest.StartsWith("--")) { lineComment = true; break; }
                if (f is Family.Hash or Family.Ruby && c == '#') { lineComment = true; break; }
                if (f is Family.Shell or Family.PowerShell && c == '#' && (i == 0 || char.IsWhiteSpace(s[i - 1]) || s[i - 1] == ';')) { lineComment = true; break; }

                // multi-line strings
                if (f == Family.CLike)
                {
                    int raw = RunOf(s, i, '"');
                    int pre = 0;
                    if (ext is "cs" or "csx" && (rest.StartsWith("@\"") || rest.StartsWith("$@\"") || rest.StartsWith("@$\"")))
                    {
                        pre = rest.IndexOf('"');
                        if (RunOf(s, i + pre, '"') < 3) { state = St.MultiString; close = "\""; closeEscapes = false; verbatimQuote = true; i += pre + 1; continue; }
                    }
                    if (ext is "cs" or "csx" && rest.StartsWith("$") && RunOf(s, i + 1 + CountOf(s, i + 1, '$'), '"') >= 3)
                    {
                        int q = i + 1 + CountOf(s, i + 1, '$');
                        int n = RunOf(s, q, '"');
                        state = St.MultiString; close = new string('"', n); closeEscapes = false; i = q + n; continue;
                    }
                    if (raw >= 3 && ext is "cs" or "csx" or "java" or "kt" or "kts" or "swift" or "scala" or "groovy" or "dart")
                    {
                        int n = ext is "cs" or "csx" ? raw : 3;
                        state = St.MultiString; close = new string('"', n); closeEscapes = ext is not ("cs" or "csx" or "scala");
                        i += n; continue;
                    }
                    if (ext == "dart" && rest.StartsWith("'''")) { state = St.MultiString; close = "'''"; closeEscapes = true; i += 3; continue; }
                    if (c == '`' && (js || ext == "go"))
                    {
                        state = St.MultiString; close = "`"; closeEscapes = js; i++; continue;
                    }
                    if (ext == "rs" && c == 'r' && (i == 0 || !IsIdent(s[i - 1])))
                    {
                        int hashes = CountOf(s, i + 1, '#');
                        if (i + 1 + hashes < s.Length && s[i + 1 + hashes] == '"')
                        {
                            state = St.MultiString; close = "\"" + new string('#', hashes); closeEscapes = false; i += hashes + 2; continue;
                        }
                    }
                    if (ext is "cpp" or "cc" or "cxx" or "hpp" or "hh" or "hxx" or "h" && c == 'R' && rest.Length > 1 && rest[1] == '"'
                        && (i == 0 || !IsIdent(s[i - 1])))
                    {
                        int paren = rest.IndexOf('(');
                        if (paren > 1)
                        {
                            string delim = rest.Substring(2, paren - 2);
                            state = St.MultiString; close = ")" + delim + "\""; closeEscapes = false; i += paren + 1; continue;
                        }
                    }
                }
                if (f is Family.Hash && (rest.StartsWith("\"\"\"") || rest.StartsWith("'''")))
                {
                    state = St.MultiString; close = rest[..3]; closeEscapes = ext is "py" or "pyw" or "pyi" || ext == "toml" && close == "\"\"\"";
                    i += 3; continue;
                }
                if (f == Family.Lua && c == '[' && LuaLong(rest, 0) is { } ls) { state = St.MultiString; close = ls.Close; closeEscapes = false; i += ls.OpenLen; continue; }
                if (f == Family.PowerShell && (rest.StartsWith("@\"") || rest.StartsWith("@'")) && rest.Length == 2)
                {
                    state = St.MultiString; close = "\n" + rest[1] + "@"; closeEscapes = false; i += 2;   // closed on a line of its own (below)
                    continue;
                }
                if (f is Family.Shell or Family.Ruby && rest.StartsWith("<<") && HeredocWord(rest, ruby: f == Family.Ruby) is { } word)
                {
                    // The heredoc's body starts on the NEXT line; this line ends in code (or after the rest of its command).
                    state = St.Heredoc; close = word;
                    i = s.Length;
                    break;
                }

                // strings that may run onto the next line (shell, Ruby, PowerShell, Perl, SQL)
                bool multiQuote = f is Family.Shell or Family.Ruby or Family.PowerShell || f == Family.Dash && ext == "sql";
                if (c == '"' || c == '\'' && !(f == Family.CLike && ext == "rs" && IsLifetime(s, i)))
                {
                    char q = c;
                    bool escapes = !(f == Family.PowerShell) && !(q == '\'' && f is Family.Shell or Family.Dash);
                    int k = i + 1;
                    bool closed = false;
                    while (k < s.Length)
                    {
                        if (escapes && s[k] == '\\') { k += 2; continue; }
                        if (f == Family.PowerShell && s[k] == '`') { k += 2; continue; }
                        if (s[k] == q)
                        {
                            if ((f == Family.Dash || f == Family.PowerShell) && k + 1 < s.Length && s[k + 1] == q) { k += 2; continue; }
                            closed = true; k++; break;
                        }
                        k++;
                    }
                    if (!closed)
                    {
                        if (multiQuote) { state = St.MultiString; close = q.ToString(); closeEscapes = escapes; i = s.Length; break; }
                        brokenString = true;   // an unclosed one-line string (or a char/lifetime the scanner misread): skip this line
                        i = s.Length; break;
                    }
                    i = k;
                    continue;
                }
                i++;
            }

            // PowerShell here-string: its closing "@ / '@ must start a line.
            if (state == St.MultiString && close.StartsWith('\n'))
            {
                // opened on this line: nothing more on it; the next lines are inside until one starts with the closer.
                int j = li + 1;
                while (j < lines.Length && !lines[j].StartsWith(close[1..], StringComparison.Ordinal)) safe[j++] = false;
                if (j < lines.Length) { safe[j] = false; li = j; }
                else li = lines.Length;
                state = St.Code;
                continue;
            }

            bool continues = s.EndsWith('\\') && f is not Family.Css and not Family.Dash and not Family.Lua
                             || f == Family.PowerShell && s.EndsWith('`');
            // JS/TS may hold JSX markup, where "//" is text on the page: no marker on a line that looks like markup.
            string t = s.Trim();
            bool markup = js && (t.StartsWith('<') || t.EndsWith('>') && !t.EndsWith("=>"));
            // A line that already ends in a comment gets none: that comment may be a directive that reads the rest of the line
            // (eslint-disable-line, //go:generate, # type: ignore, # noqa). Nor a shebang, or a C/C# preprocessor line (#region
            // takes the rest of the line as its name).
            bool directive = li == 0 && t.StartsWith("#!") || f == Family.CLike && t.StartsWith('#');
            safe[li] = state == St.Code && !lineComment && !brokenString && !continues && !markup && !directive && t.Length > 0;
        }
        return safe;
    }

    private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int RunOf(string s, int at, char c) { int n = 0; while (at + n < s.Length && s[at + n] == c) n++; return n; }

    private static int CountOf(string s, int at, char c) => RunOf(s, at, c);

    /// <summary>Rust: <c>'a</c> in <c>&amp;'a str</c> is a lifetime, not a char literal (a char closes within 1–10 chars).</summary>
    private static bool IsLifetime(string s, int i)
    {
        if (i + 2 < s.Length && s[i + 1] == '\\') return false;
        if (i + 2 < s.Length && s[i + 2] == '\'') return false;
        return i + 1 < s.Length && (char.IsLetter(s[i + 1]) || s[i + 1] == '_');
    }

    /// <summary>A Lua long bracket at <paramref name="at"/> (<c>[[</c>, <c>[==[</c>): its length and closer.</summary>
    private static (int OpenLen, string Close)? LuaLong(string rest, int at)
    {
        if (at >= rest.Length || rest[at] != '[') return null;
        int eq = RunOf(rest, at + 1, '=');
        if (at + 1 + eq >= rest.Length || rest[at + 1 + eq] != '[') return null;
        return (at + eq + 2, "]" + new string('=', eq) + "]");
    }

    private static readonly Regex HeredocRx = new(@"^<<[-~]?\s*(['""]?)([A-Za-z_][A-Za-z0-9_]*)\1", RegexOptions.CultureInvariant);

    private static readonly Regex RubyHeredocRx = new(@"^<<[-~]?(['""]?)([A-Z_][A-Za-z0-9_]*)\1", RegexOptions.CultureInvariant);

    /// <summary>The word a heredoc ends with. Ruby's needs no space and a capital (<c>arr &lt;&lt; x</c> is an append).</summary>
    private static string? HeredocWord(string rest, bool ruby)
    {
        if (rest.StartsWith("<<<")) return null;   // a here-string (bash <<<): one line
        var m = (ruby ? RubyHeredocRx : HeredocRx).Match(rest);
        return m.Success ? m.Groups[2].Value : null;
    }

    // ───────────────────────── writing markers ─────────────────────────

    /// <summary>
    /// Put a marker on each run of user-edited lines of <paramref name="text"/> (1-based, inclusive ranges in that text):
    /// at the end of the first line of the run where it is safe. A run that already carries a marker, or has no safe line,
    /// gets none. Line ends are kept exactly. Returns the new text and the lines that got one.
    /// </summary>
    public static (string Text, List<int> Marked) AddMarkers(string text, string fileName, IEnumerable<(int Start, int Count)> runs, DateTime date)
    {
        var marked = new List<int>();
        var style = StyleFor(fileName);
        if (style == null || string.IsNullOrEmpty(text)) return (text, marked);
        var safe = SafeLineEnds(text, fileName);
        var (lines, ends) = SplitWithEnds(text);
        string marker = style.Open + Text(date) + style.Close;
        foreach (var (start, count) in runs.OrderBy(r => r.Start))
        {
            if (count <= 0) continue;
            int from = Math.Max(1, start), to = Math.Min(lines.Length, start + count - 1);
            bool already = false;
            for (int l = from; l <= to; l++) if (HasMarker(lines[l - 1])) { already = true; break; }
            if (already) continue;
            for (int l = from; l <= to; l++)
            {
                if (!safe[l - 1]) continue;
                lines[l - 1] = lines[l - 1].TrimEnd(' ', '\t') + " " + marker;
                marked.Add(l);
                break;
            }
        }
        if (marked.Count == 0) return (text, marked);
        var sb = new StringBuilder(text.Length + marked.Count * (marker.Length + 1));
        for (int i = 0; i < lines.Length; i++) sb.Append(lines[i]).Append(ends[i]);
        return (sb.ToString(), marked);
    }

    /// <summary>Every marker in <paramref name="text"/>: (line, date, the line without its marker).</summary>
    public static List<(int Line, string Date, string Text)> Find(string text)
    {
        var found = new List<(int, string, string)>();
        if (string.IsNullOrEmpty(text) || !text.Contains(Tag, StringComparison.Ordinal)) return found;
        var lines = SplitLines(text);
        for (int i = 0; i < lines.Length; i++)
            if (HasMarker(lines[i])) found.Add((i + 1, DateOf(lines[i]), Strip(lines[i]).Trim()));
        return found;
    }

    /// <summary>Lines the way the editor counts them ("a\n" is two lines: "a" and "").</summary>
    public static string[] SplitLines(string text) => SplitWithEnds(text).Lines;

    internal static (string[] Lines, string[] Ends) SplitWithEnds(string text)
    {
        var lines = new List<string>();
        var ends = new List<string>();
        int start = 0;
        text ??= "";
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { lines.Add(text[start..i]); ends.Add("\r\n"); i++; start = i + 1; }
            else if (text[i] == '\n' || text[i] == '\r') { lines.Add(text[start..i]); ends.Add(text[i].ToString()); start = i + 1; }
        }
        lines.Add(text[start..]);
        ends.Add("");
        return (lines.ToArray(), ends.ToArray());
    }
}
