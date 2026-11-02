using System.Text;

namespace CluadeX.Services.UiStudio.Graph;

/// <summary>
/// Blanks out what is not code, so a pattern can only match code: comments become spaces, and so does the inside of
/// every string (the quotes stay). The result has the same length and the same line breaks as the source, so an
/// offset found in it is the same offset in the source — that is how an import's path is read back: the pattern
/// finds the quotes in the masked text, the path is taken from the source between them. An import written inside a
/// comment or inside a string is spaces here, so it can never become an edge.
/// </summary>
internal static class CodeMask
{
    public enum Lang { Js, Dart, Php, Python, Css, Html, Blade, Xml }

    public static string Mask(string s, Lang lang) => lang switch
    {
        Lang.Js => CLike(s, jsRegex: true, nestedBlock: false, hashComment: false, dart: false, php: false),
        Lang.Dart => CLike(s, jsRegex: false, nestedBlock: true, hashComment: false, dart: true, php: false),
        Lang.Php => CLike(s, jsRegex: false, nestedBlock: false, hashComment: true, dart: false, php: true),
        Lang.Css => CLike(s, jsRegex: false, nestedBlock: false, hashComment: false, dart: false, php: false, lineComments: false),
        Lang.Python => Python(s),
        Lang.Html or Lang.Xml => Delimited(s, "<!--", "-->"),
        Lang.Blade => Delimited(Delimited(s, "{{--", "--}}"), "<!--", "-->"),
        _ => s,
    };

    /// <summary>A .vue / .svelte / .astro file: HTML comments everywhere, JS rules inside &lt;script&gt; (and the
    /// Astro frontmatter). The template keeps its text — an apostrophe in "don't" is not a string there.</summary>
    public static string Sfc(string s)
    {
        var sb = new StringBuilder(Delimited(s, "<!--", "-->"));
        void MaskRange(int from, int to)
        {
            if (to <= from) return;
            string part = CLike(sb.ToString(from, to - from), jsRegex: true, nestedBlock: false, hashComment: false, dart: false, php: false);
            for (int i = 0; i < part.Length; i++) sb[from + i] = part[i];
        }
        string text = sb.ToString();
        if (text.StartsWith("---", StringComparison.Ordinal))
        {
            int end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0) MaskRange(3, end);
        }
        int at = 0;
        while ((at = text.IndexOf("<script", at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int open = text.IndexOf('>', at);
            if (open < 0) break;
            int close = text.IndexOf("</script", open, StringComparison.OrdinalIgnoreCase);
            if (close < 0) close = text.Length;
            MaskRange(open + 1, close);
            at = close + 1;
        }
        return sb.ToString();
    }

    private static void Blank(StringBuilder sb, int from, int to)
    {
        for (int i = from; i < to && i < sb.Length; i++)
            if (sb[i] != '\n' && sb[i] != '\r') sb[i] = ' ';
    }

    private static string Delimited(string s, string open, string close)
    {
        if (!s.Contains(open, StringComparison.Ordinal)) return s;
        var sb = new StringBuilder(s);
        int at = 0;
        while ((at = s.IndexOf(open, at, StringComparison.Ordinal)) >= 0)
        {
            int end = s.IndexOf(close, at + open.Length, StringComparison.Ordinal);
            end = end < 0 ? s.Length : end + close.Length;
            Blank(sb, at, end);
            at = end;
        }
        return sb.ToString();
    }

    /// <summary>C-family scanner: // and /* */ comments (nested for Dart), '…' "…" strings, JS `…` templates (with
    /// ${…} inside), Dart '''…''' and r'…', PHP # comments and heredocs, and JS regex literals (/'/ is not a quote).</summary>
    private static string CLike(string s, bool jsRegex, bool nestedBlock, bool hashComment, bool dart, bool php, bool lineComments = true)
    {
        var sb = new StringBuilder(s);
        int n = s.Length, i = 0;
        char prevSig = '\0';   // last non-space code character, to tell a regex from a division
        while (i < n)
        {
            char c = s[i];
            char next = i + 1 < n ? s[i + 1] : '\0';
            if (lineComments && c == '/' && next == '/' || hashComment && c == '#' && next != '[')
            {
                int end = s.IndexOf('\n', i);
                if (end < 0) end = n;
                if (php)
                {
                    // a PHP line comment ends at ?> too
                    int tag = s.IndexOf("?>", i, end - i, StringComparison.Ordinal);
                    if (tag >= 0) end = tag;
                }
                Blank(sb, i, end);
                i = end;
                continue;
            }
            if (c == '/' && next == '*')
            {
                int depth = 1, j = i + 2;
                while (j < n && depth > 0)
                {
                    if (nestedBlock && s[j] == '/' && j + 1 < n && s[j + 1] == '*') { depth++; j += 2; }
                    else if (s[j] == '*' && j + 1 < n && s[j + 1] == '/') { depth--; j += 2; }
                    else j++;
                }
                Blank(sb, i, j);
                i = j;
                continue;
            }
            if (php && c == '<' && next == '<' && i + 2 < n && s[i + 2] == '<')
            {
                // heredoc / nowdoc: <<<ID or <<<'ID' … then a line that starts (after spaces) with ID
                int j = i + 3;
                while (j < n && s[j] is ' ' or '\t') j++;
                bool quoted = j < n && s[j] is '\'' or '"';
                if (quoted) j++;
                int idStart = j;
                while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                string id = s[idStart..j];
                int lineEnd = s.IndexOf('\n', j);
                if (id.Length > 0 && lineEnd > 0)
                {
                    int k = lineEnd + 1, bodyEnd = n;
                    while (k < n)
                    {
                        int le = s.IndexOf('\n', k);
                        if (le < 0) le = n;
                        if (s.AsSpan(k, le - k).TrimStart().StartsWith(id, StringComparison.Ordinal)) { bodyEnd = k; break; }
                        k = le + 1;
                    }
                    Blank(sb, lineEnd + 1, bodyEnd);
                    i = bodyEnd;
                    prevSig = '"';
                    continue;
                }
            }
            if (c is '\'' or '"' || c == '`' && jsRegex)
            {
                bool raw = dart && i > 0 && s[i - 1] == 'r' && (i < 2 || !char.IsLetterOrDigit(s[i - 2]));
                bool triple = dart && i + 2 < n && s[i + 1] == c && s[i + 2] == c;
                int j = triple ? i + 3 : i + 1;
                int depthTpl = 0;
                while (j < n)
                {
                    char d = s[j];
                    if (d == '\\' && !raw) { j += 2; continue; }
                    if (c == '`')
                    {
                        if (d == '$' && j + 1 < n && s[j + 1] == '{') { depthTpl++; j += 2; continue; }
                        if (depthTpl > 0) { if (d == '{') depthTpl++; else if (d == '}') depthTpl--; j++; continue; }
                    }
                    if (triple)
                    {
                        if (d == c && j + 2 < n && s[j + 1] == c && s[j + 2] == c) break;
                    }
                    else if (d == c) break;
                    else if (d == '\n' && c != '`' && !php) break;   // an unclosed quote ends at the line (JS/Dart/CSS)
                    j++;
                }
                int q = triple ? 3 : 1;
                int contentEnd = Math.Min(j, n);
                Blank(sb, i + q, contentEnd);
                i = Math.Min(n, contentEnd + (j < n && s[j] != '\n' ? q : 0));
                prevSig = c;
                continue;
            }
            if (jsRegex && c == '/' && RegexCanStart(s, i, prevSig))
            {
                int j = i + 1;
                bool inClass = false;
                while (j < n && s[j] != '\n')
                {
                    if (s[j] == '\\') { j += 2; continue; }
                    if (s[j] == '[') inClass = true;
                    else if (s[j] == ']') inClass = false;
                    else if (s[j] == '/' && !inClass) break;
                    j++;
                }
                if (j < n && s[j] == '/')
                {
                    Blank(sb, i + 1, j);
                    i = j + 1;
                    prevSig = '/';
                    continue;
                }
            }
            if (!char.IsWhiteSpace(c)) prevSig = c;
            i++;
        }
        return sb.ToString();
    }

    private static bool RegexCanStart(string s, int i, char prevSig)
    {
        if (prevSig is '\0' or '(' or ',' or '=' or ':' or '[' or '!' or '&' or '|' or '?' or '{' or '}' or ';' or '+' or '-' or '*' or '%' or '<' or '>' or '~' or '^')
            return true;
        // after a keyword: return /x/, typeof /x/ …
        int j = i - 1;
        while (j >= 0 && char.IsWhiteSpace(s[j])) j--;
        int end = j + 1;
        while (j >= 0 && char.IsLetter(s[j])) j--;
        string word = s[(j + 1)..end];
        return word is "return" or "typeof" or "case" or "do" or "else" or "in" or "of" or "void" or "yield" or "await";
    }

    private static string Python(string s)
    {
        var sb = new StringBuilder(s);
        int n = s.Length, i = 0;
        while (i < n)
        {
            char c = s[i];
            if (c == '#')
            {
                int end = s.IndexOf('\n', i);
                if (end < 0) end = n;
                Blank(sb, i, end);
                i = end;
                continue;
            }
            if (c is '\'' or '"')
            {
                // prefixes r b f u (any case, combined) are code letters before the quote — only r turns escapes off
                int p = i - 1;
                bool raw = false;
                while (p >= 0 && "rRbBfFuU".Contains(s[p])) { if (s[p] is 'r' or 'R') raw = true; p--; }
                if (p >= 0 && (char.IsLetterOrDigit(s[p]) || s[p] == '_')) raw = false;
                bool triple = i + 2 < n && s[i + 1] == c && s[i + 2] == c;
                int j = triple ? i + 3 : i + 1;
                while (j < n)
                {
                    if (s[j] == '\\' && !raw) { j += 2; continue; }
                    if (triple) { if (s[j] == c && j + 2 < n && s[j + 1] == c && s[j + 2] == c) break; }
                    else if (s[j] == c || s[j] == '\n') break;
                    j++;
                }
                int q = triple ? 3 : 1;
                int contentEnd = Math.Min(j, n);
                Blank(sb, i + q, contentEnd);
                i = Math.Min(n, contentEnd + (j < n && s[j] != '\n' ? q : 0));
                continue;
            }
            i++;
        }
        return sb.ToString();
    }
}

/// <summary>Offset → 1-based line, and the text of a line (trimmed, capped) for an edge's evidence.</summary>
internal sealed class LineIndex
{
    private readonly string _text;
    private readonly List<int> _starts = new() { 0 };

    public LineIndex(string text)
    {
        _text = text;
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') _starts.Add(i + 1);
    }

    public int Count => _starts.Count;

    public int LineOf(int offset)
    {
        int lo = 0, hi = _starts.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_starts[mid] <= offset) lo = mid; else hi = mid - 1;
        }
        return lo + 1;
    }

    public string Text(int line)
    {
        if (line < 1 || line > _starts.Count) return "";
        int s = _starts[line - 1];
        int e = line < _starts.Count ? _starts[line] - 1 : _text.Length;
        return Clip(_text[s..Math.Max(s, e)]);
    }

    public static string Clip(string line)
    {
        line = line.Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
