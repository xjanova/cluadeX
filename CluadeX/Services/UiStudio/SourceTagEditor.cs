using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services.UiStudio;

/// <summary>How a source file writes markup: plain HTML, JSX/TSX, a Vue SFC template or a Svelte component.</summary>
public enum SourceDialect { Html, Jsx, Vue, Svelte }

/// <summary>One attribute of an opening tag, as written in the source.</summary>
public sealed class SourceAttr
{
    public string Name { get; init; } = "";
    /// <summary>The whole attribute: from the first letter of the name to the end of the value.</summary>
    public int Start { get; init; }
    public int End { get; init; }
    /// <summary>The value between its quotes or braces (or the bare value); -1 when the attribute has no value.</summary>
    public int ValueStart { get; init; } = -1;
    public int ValueEnd { get; init; } = -1;
    /// <summary>'"' or '\'' for a quoted value, '{' for an expression, ' ' for a bare value, '\0' for none.</summary>
    public char Quote { get; init; }

    public bool IsExpression => Quote == '{';
    public string? Value(string text) => ValueStart < 0 ? null : text[ValueStart..ValueEnd];
}

/// <summary>An opening tag found in the source.</summary>
public sealed class SourceTag
{
    public int Start { get; init; }          // the '<'
    public int NameEnd { get; init; }        // just past the tag name
    public int End { get; init; }            // just past the closing '>' of the opening tag
    public int CloseMarker { get; init; }    // where "/>" or ">" begins
    public string Name { get; init; } = "";
    public bool SelfClosing { get; init; }
    public List<SourceAttr> Attrs { get; } = new();

    public SourceAttr? Find(string name) =>
        Attrs.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
}

/// <summary>The result of a source edit: the new text, or the reason it could not be done.</summary>
public readonly record struct SourceEdit(string? Text, string? Error)
{
    public bool Ok => Text != null;
    public static SourceEdit Fail(string error) => new(null, error);
    public static SourceEdit Done(string text) => new(text, null);
}

/// <summary>
/// Edits markup in a source file at an exact element — the one the UI Studio overlay reports through its
/// <c>data-cx-src="file:line:col"</c> locator. Every edit starts from the element's opening '&lt;' and checks the
/// tag name there first, so a stale location (the file changed since the page was rendered) is refused instead of
/// writing into the wrong element. Nothing here uses a regex over the whole file: the tag and its attributes are
/// scanned the way the language reads them (quoted values, JSX braces with nested strings and template literals,
/// Vue/Svelte directives), and anything that is code rather than static markup — <c>className={cls}</c>, a text
/// made of <c>{expressions}</c> — is refused with the reason, so the user can hand that one to the AI instead.
/// </summary>
public static class SourceTagEditor
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr",
    };

    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "textarea", "title",
    };

    public static SourceDialect DialectFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jsx" or ".tsx" or ".js" or ".ts" or ".mjs" or ".cjs" or ".mdx" => SourceDialect.Jsx,
        ".vue" => SourceDialect.Vue,
        ".svelte" => SourceDialect.Svelte,
        _ => SourceDialect.Html,
    };

    // ───────────────────────────── locating ─────────────────────────────

    /// <summary>The offset of 1-based (line, column) in <paramref name="text"/>, or -1 when out of range.</summary>
    public static int OffsetOf(string text, int line, int column)
    {
        if (line < 1 || column < 1) return -1;
        int offset = 0;
        for (int l = 1; l < line; l++)
        {
            int nl = text.IndexOf('\n', offset);
            if (nl < 0) return -1;
            offset = nl + 1;
        }
        int lineEnd = text.IndexOf('\n', offset);
        if (lineEnd < 0) lineEnd = text.Length;
        int at = offset + column - 1;
        return at <= lineEnd ? at : -1;
    }

    /// <summary>1-based (line, column) of <paramref name="offset"/>.</summary>
    public static (int Line, int Column) LineColOf(string text, int offset)
    {
        int line = 1, lineStart = 0;
        for (int i = 0; i < offset && i < text.Length; i++)
            if (text[i] == '\n') { line++; lineStart = i + 1; }
        return (line, offset - lineStart + 1);
    }

    /// <summary>
    /// The opening tag of <paramref name="tagName"/> at (line, column). The column must point at the '&lt;'; when it
    /// does not (a formatter moved it within the line), the only '&lt;tagName' on that line is accepted. Null with a
    /// reason when the source no longer has that element there.
    /// </summary>
    public static SourceTag? Locate(string text, int line, int column, string tagName, SourceDialect dialect, out string? error)
    {
        error = null;
        int at = OffsetOf(text, line, column);
        if (string.IsNullOrEmpty(tagName))
        {
            // Any element (a sibling whose tag the page did not report): the column must point at its '<'.
            if (at >= 0 && at + 1 < text.Length && text[at] == '<' && char.IsLetter(text[at + 1]))
                return ParseOpenTag(text, at, dialect);
            error = "stale";
            return null;
        }
        if (at >= 0 && StartsTag(text, at, tagName))
            return ParseOpenTag(text, at, dialect);

        // Same line, exactly one candidate: accept it (columns drift when a tool re-indents).
        int lineStart = OffsetOf(text, line, 1);
        if (lineStart >= 0)
        {
            int lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = text.Length;
            int found = -1, count = 0;
            for (int i = lineStart; i < lineEnd; i++)
                if (StartsTag(text, i, tagName)) { found = i; count++; }
            if (count == 1) return ParseOpenTag(text, found, dialect);
        }
        error = "stale";
        return null;
    }

    private static bool StartsTag(string text, int at, string tagName)
    {
        if (at < 0 || at >= text.Length || text[at] != '<') return false;
        int end = at + 1 + tagName.Length;
        if (end > text.Length) return false;
        if (string.Compare(text, at + 1, tagName, 0, tagName.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        return end == text.Length || !IsNameChar(text[end]);
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_' or ':' or '.' or '$';

    // ───────────────────────────── parsing ─────────────────────────────

    /// <summary>Parse the opening tag whose '&lt;' is at <paramref name="start"/>. Null when it is not a tag.</summary>
    public static SourceTag? ParseOpenTag(string text, int start, SourceDialect dialect)
    {
        if (start < 0 || start >= text.Length - 1 || text[start] != '<' || !char.IsLetter(text[start + 1])) return null;
        int i = start + 1;
        while (i < text.Length && IsNameChar(text[i])) i++;
        string name = text[(start + 1)..i];
        var tag = new List<SourceAttr>();
        int nameEnd = i;

        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) return null;
            char c = text[i];
            if (c == '>')
                return Build(start, nameEnd, i + 1, i, name, false, tag);
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '>')
                return Build(start, nameEnd, i + 2, i, name, true, tag);
            if (c == '{' && dialect is SourceDialect.Jsx or SourceDialect.Svelte)
            {
                // {...spread} in JSX, {shorthand} in Svelte
                int close = SkipBraces(text, i);
                if (close < 0) return null;
                tag.Add(new SourceAttr { Name = "{" , Start = i, End = close, ValueStart = i + 1, ValueEnd = close - 1, Quote = '{' });
                i = close;
                continue;
            }
            if (c == '/') { i++; continue; }   // stray slash (e.g. "<br/ >")

            int nameStart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '=' and not '>' and not '"' and not '\''
                   && !(text[i] == '/' && i + 1 < text.Length && text[i + 1] == '>'))
            {
                if (text[i] == '{' && dialect == SourceDialect.Svelte) break;
                i++;
            }
            if (i == nameStart) { i++; continue; }   // garbage character: step over it
            string attrName = text[nameStart..i];
            int afterName = i;
            int j = i;
            while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
            if (j < text.Length && text[j] == '=')
            {
                j++;
                while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                if (j >= text.Length) return null;
                char q = text[j];
                if (q is '"' or '\'')
                {
                    int closeQ = text.IndexOf(q, j + 1);
                    if (closeQ < 0) return null;
                    tag.Add(new SourceAttr { Name = attrName, Start = nameStart, End = closeQ + 1, ValueStart = j + 1, ValueEnd = closeQ, Quote = q });
                    i = closeQ + 1;
                }
                else if (q == '{')
                {
                    int close = SkipBraces(text, j);
                    if (close < 0) return null;
                    tag.Add(new SourceAttr { Name = attrName, Start = nameStart, End = close, ValueStart = j + 1, ValueEnd = close - 1, Quote = '{' });
                    i = close;
                }
                else
                {
                    int k = j;
                    while (k < text.Length && !char.IsWhiteSpace(text[k]) && text[k] != '>'
                           && !(text[k] == '/' && k + 1 < text.Length && text[k + 1] == '>')) k++;
                    tag.Add(new SourceAttr { Name = attrName, Start = nameStart, End = k, ValueStart = j, ValueEnd = k, Quote = ' ' });
                    i = k;
                }
            }
            else
            {
                tag.Add(new SourceAttr { Name = attrName, Start = nameStart, End = afterName, Quote = '\0' });
                i = afterName;
            }
        }
        return null;

        static SourceTag Build(int s, int ne, int e, int cm, string n, bool sc, List<SourceAttr> attrs)
        {
            var t = new SourceTag { Start = s, NameEnd = ne, End = e, CloseMarker = cm, Name = n, SelfClosing = sc };
            t.Attrs.AddRange(attrs);
            return t;
        }
    }

    /// <summary>Index just past the '}' matching the '{' at <paramref name="open"/>; skips JS strings, template
    /// literals (with nested ${}) and comments. -1 when unbalanced.</summary>
    public static int SkipBraces(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return i + 1;
                    break;
                case '"':
                case '\'':
                    i = SkipString(text, i, c);
                    if (i < 0) return -1;
                    break;
                case '`':
                    i = SkipTemplate(text, i);
                    if (i < 0) return -1;
                    break;
                case '/' when i + 1 < text.Length && text[i + 1] == '/':
                    i = text.IndexOf('\n', i);
                    if (i < 0) return -1;
                    break;
                case '/' when i + 1 < text.Length && text[i + 1] == '*':
                    i = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (i < 0) return -1;
                    i++;
                    break;
            }
        }
        return -1;
    }

    private static int SkipString(string text, int open, char q)
    {
        for (int i = open + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == q) return i;
            if (text[i] == '\n') return i;   // unterminated: stop at the line end rather than eat the file
        }
        return -1;
    }

    private static int SkipTemplate(string text, int open)
    {
        for (int i = open + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '`') return i;
            if (text[i] == '$' && i + 1 < text.Length && text[i + 1] == '{')
            {
                int close = SkipBraces(text, i + 1);
                if (close < 0) return -1;
                i = close - 1;
            }
        }
        return -1;
    }

    /// <summary>
    /// The element's whole extent: from its '&lt;' to just past its closing tag (or its opening tag when it has
    /// none). Nested elements of the same name are counted. -1 when no closing tag is found.
    /// </summary>
    public static int FindElementEnd(string text, SourceTag tag, SourceDialect dialect)
    {
        if (tag.SelfClosing) return tag.End;
        if (dialect != SourceDialect.Jsx && VoidElements.Contains(tag.Name)) return tag.End;
        if (RawTextElements.Contains(tag.Name) && dialect != SourceDialect.Jsx)
        {
            int close = IndexOfCloseTag(text, tag.Name, tag.End);
            return close < 0 ? -1 : text.IndexOf('>', close) + 1;
        }

        int depth = 0;
        int i = tag.End;
        while (i < text.Length)
        {
            int lt = text.IndexOf('<', i);
            if (lt < 0 || lt + 1 >= text.Length) return -1;
            char n = text[lt + 1];
            if (n == '!' && string.CompareOrdinal(text, lt, "<!--", 0, 4) == 0)
            {
                int endC = text.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                if (endC < 0) return -1;
                i = endC + 3;
                continue;
            }
            if (n == '/')
            {
                int k = lt + 2;
                while (k < text.Length && IsNameChar(text[k])) k++;
                string closeName = text[(lt + 2)..k];
                int gt = text.IndexOf('>', k);
                if (gt < 0) return -1;
                if (string.Equals(closeName, tag.Name, dialect == SourceDialect.Jsx ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                {
                    if (depth == 0) return gt + 1;
                    depth--;
                }
                i = gt + 1;
                continue;
            }
            if (char.IsLetter(n))
            {
                var inner = ParseOpenTag(text, lt, dialect);
                if (inner == null) { i = lt + 1; continue; }
                bool same = string.Equals(inner.Name, tag.Name, dialect == SourceDialect.Jsx ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                bool opens = !inner.SelfClosing && !(dialect != SourceDialect.Jsx && VoidElements.Contains(inner.Name));
                if (opens && RawTextElements.Contains(inner.Name) && dialect != SourceDialect.Jsx)
                {
                    int close = IndexOfCloseTag(text, inner.Name, inner.End);
                    if (close < 0) return -1;
                    i = text.IndexOf('>', close) + 1;
                    continue;
                }
                if (same && opens) depth++;
                i = inner.End;
                continue;
            }
            i = lt + 1;   // "a < b", "<>" fragments, "<=": not an element
        }
        return -1;
    }

    private static int IndexOfCloseTag(string text, string name, int from)
    {
        int i = from;
        while (true)
        {
            int at = text.IndexOf("</", i, StringComparison.Ordinal);
            if (at < 0) return -1;
            if (string.Compare(text, at + 2, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0) return at;
            i = at + 2;
        }
    }

    // ───────────────────────────── attribute edits ─────────────────────────────

    /// <summary>The attribute name the dialect uses for an HTML attribute (class → className in JSX).</summary>
    public static string AttrNameFor(string htmlName, SourceDialect dialect) => dialect == SourceDialect.Jsx
        ? htmlName switch { "class" => "className", "for" => "htmlFor", "tabindex" => "tabIndex", "readonly" => "readOnly", _ => htmlName }
        : htmlName;

    /// <summary>Set (or with null remove) a static attribute. Refused when the attribute is an expression.</summary>
    public static SourceEdit SetAttribute(string text, SourceTag tag, SourceDialect dialect, string htmlName, string? value)
    {
        string name = AttrNameFor(htmlName, dialect);
        var attr = tag.Find(name);
        if (attr is { IsExpression: true } && !IsPlainStringExpression(text, attr))
            return SourceEdit.Fail("expression");
        // Vue :class / Svelte class:x stay as they are — they are bindings next to the static attribute.

        if (value == null)
        {
            if (attr == null) return SourceEdit.Done(text);
            int from = attr.Start;
            while (from > tag.NameEnd && text[from - 1] is ' ' or '\t') from--;
            int nl = text.IndexOf('\n', attr.End);
            if (from > tag.NameEnd && text[from - 1] == '\n' && nl >= 0 && nl < tag.CloseMarker
                && string.IsNullOrWhiteSpace(text[attr.End..nl]))
                return SourceEdit.Done(text[..from] + text[(nl + 1)..]);   // the attribute owned its line
            return SourceEdit.Done(text[..from] + text[attr.End..]);
        }

        string rendered = RenderAttribute(name, value, dialect);
        if (attr != null)
            return SourceEdit.Done(text[..attr.Start] + rendered + text[attr.End..]);

        // Append after the last attribute, before "/>" or ">" — keeping a multi-line tag's layout.
        int insertAt = tag.CloseMarker;
        while (insertAt > tag.NameEnd && char.IsWhiteSpace(text[insertAt - 1])) insertAt--;
        string lead = " ";
        if (tag.Attrs.Count > 0)
        {
            // multi-line attributes: put the new one on its own line with the same indentation
            var last = tag.Attrs[^1];
            int lastLineStart = text.LastIndexOf('\n', last.Start) + 1;
            if (lastLineStart > tag.Start)
            {
                string indent = text[lastLineStart..last.Start];
                if (string.IsNullOrWhiteSpace(indent)) lead = "\n" + indent;
            }
        }
        return SourceEdit.Done(text[..insertAt] + lead + rendered + text[insertAt..]);
    }

    /// <summary>A JSX <c>attr={"plain"}</c> or <c>{'plain'}</c> is a static string written as an expression.</summary>
    private static bool IsPlainStringExpression(string text, SourceAttr attr)
    {
        string inner = text[attr.ValueStart..attr.ValueEnd].Trim();
        return inner.Length >= 2 && inner[0] is '"' or '\'' && inner[^1] == inner[0] && inner.IndexOf(inner[0], 1) == inner.Length - 1;
    }

    private static string RenderAttribute(string name, string value, SourceDialect dialect)
    {
        if (dialect == SourceDialect.Jsx)
        {
            // JSX string attributes have no escapes: a value with a double quote goes in as a JS string expression.
            if (value.Contains('"') || value.Contains('\n'))
                return $"{name}={{{JsString(value)}}}";
            return $"{name}=\"{value}\"";
        }
        return $"{name}=\"{HtmlAttrEscape(value)}\"";
    }

    public static string JsString(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    public static string HtmlAttrEscape(string value) =>
        value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>The static attribute's value as the browser would read it (entities decoded), or null.</summary>
    public static string? ReadStaticAttribute(string text, SourceTag tag, SourceDialect dialect, string htmlName)
    {
        var attr = tag.Find(AttrNameFor(htmlName, dialect));
        if (attr == null) return null;
        if (attr.IsExpression)
        {
            if (!IsPlainStringExpression(text, attr)) return null;
            string inner = text[attr.ValueStart..attr.ValueEnd].Trim();
            return inner[1..^1];
        }
        string? raw = attr.Value(text);
        return raw == null ? "" : System.Net.WebUtility.HtmlDecode(raw);
    }

    // ───────────────────────────── style ─────────────────────────────

    /// <summary>
    /// Merge CSS declarations into the element's inline style (a null value removes that property). HTML, Vue and
    /// Svelte write <c>style="a: b; c: d"</c>; JSX writes <c>style={{ a: 'b' }}</c> with camelCase keys. A JSX
    /// style that is not a plain object of string/number literals (a variable, a spread, a ternary) is refused.
    /// </summary>
    public static SourceEdit SetStyle(string text, SourceTag tag, SourceDialect dialect, IReadOnlyDictionary<string, string?> changes)
    {
        if (changes.Count == 0) return SourceEdit.Done(text);
        var attr = tag.Find("style");

        if (dialect == SourceDialect.Jsx)
        {
            List<(string Key, string Value)> decls;
            if (attr == null) decls = new();
            else if (!TryParseJsxStyleObject(text, attr, out decls)) return SourceEdit.Fail("styleExpression");
            foreach (var (prop, val) in changes)
            {
                string key = CamelCase(prop);
                int idx = decls.FindIndex(d => d.Key == key);
                if (val == null) { if (idx >= 0) decls.RemoveAt(idx); continue; }
                string lit = JsStyleLiteral(val);
                if (idx >= 0) decls[idx] = (key, lit); else decls.Add((key, lit));
            }
            if (decls.Count == 0)
                return attr == null ? SourceEdit.Done(text) : RemoveAttr(text, tag, attr);
            string obj = "style={{ " + string.Join(", ", decls.Select(d => $"{JsKey(d.Key)}: {d.Value}")) + " }}";
            return attr != null
                ? SourceEdit.Done(text[..attr.Start] + obj + text[attr.End..])
                : InsertRaw(text, tag, obj);
        }

        if (attr is { IsExpression: true }) return SourceEdit.Fail("styleExpression");
        var css = ParseCssDeclarations(attr?.Value(text) is { } raw ? System.Net.WebUtility.HtmlDecode(raw) : "");
        foreach (var (prop, val) in changes)
        {
            int idx = css.FindIndex(d => string.Equals(d.Key, prop, StringComparison.OrdinalIgnoreCase));
            if (val == null) { if (idx >= 0) css.RemoveAt(idx); continue; }
            if (idx >= 0) css[idx] = (css[idx].Key, val); else css.Add((prop, val));
        }
        string joined = string.Join("; ", css.Select(d => $"{d.Key}: {d.Value}"));
        return SetAttribute(text, tag, dialect, "style", css.Count == 0 ? null : joined);
    }

    private static SourceEdit RemoveAttr(string text, SourceTag tag, SourceAttr attr)
    {
        int from = attr.Start;
        while (from > tag.NameEnd && text[from - 1] is ' ' or '\t') from--;
        return SourceEdit.Done(text[..from] + text[attr.End..]);
    }

    private static SourceEdit InsertRaw(string text, SourceTag tag, string raw)
    {
        int insertAt = tag.CloseMarker;
        while (insertAt > tag.NameEnd && char.IsWhiteSpace(text[insertAt - 1])) insertAt--;
        return SourceEdit.Done(text[..insertAt] + " " + raw + text[insertAt..]);
    }

    public static List<(string Key, string Value)> ParseCssDeclarations(string css)
    {
        var list = new List<(string, string)>();
        foreach (var part in SplitCss(css))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            string key = part[..colon].Trim();
            string val = part[(colon + 1)..].Trim();
            if (key.Length > 0 && val.Length > 0) list.Add((key, val));
        }
        return list;
    }

    /// <summary>Split on ';' outside parentheses and quotes (url(data:...;base64,...) stays whole).</summary>
    private static IEnumerable<string> SplitCss(string css)
    {
        int depth = 0, start = 0;
        char quote = '\0';
        for (int i = 0; i < css.Length; i++)
        {
            char c = css[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (c == ';' && depth == 0) { yield return css[start..i]; start = i + 1; }
        }
        if (start < css.Length) yield return css[start..];
    }

    private static readonly Regex JsStyleEntry = new(
        @"^\s*(?:(?<k>[A-Za-z_$][\w$]*)|'(?<k>[^']*)'|""(?<k>[^""]*)"")\s*:\s*(?<v>'[^'\\\n]*'|""[^""\\\n]*""|-?\d+(?:\.\d+)?)\s*$",
        RegexOptions.CultureInvariant);

    /// <summary>Read <c>style={{ key: 'v', other: 2 }}</c> — only literal keys and literal values.</summary>
    private static bool TryParseJsxStyleObject(string text, SourceAttr attr, out List<(string Key, string Value)> decls)
    {
        decls = new();
        string inner = text[attr.ValueStart..attr.ValueEnd].Trim();
        if (inner.Length < 2 || inner[0] != '{' || inner[^1] != '}') return false;
        string body = inner[1..^1].Trim();
        if (body.Length == 0) return true;
        foreach (var raw in SplitTopLevel(body, ','))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var m = JsStyleEntry.Match(raw);
            if (!m.Success) return false;
            decls.Add((m.Groups["k"].Value, m.Groups["v"].Value));
        }
        return true;
    }

    private static IEnumerable<string> SplitTopLevel(string s, char sep)
    {
        int depth = 0, start = 0;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0') { if (c == '\\') { i++; continue; } if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'' or '`') quote = c;
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == sep && depth == 0) { yield return s[start..i]; start = i + 1; }
        }
        yield return s[start..];
    }

    public static string CamelCase(string cssProp)
    {
        if (cssProp.StartsWith("--", StringComparison.Ordinal)) return cssProp;   // custom properties keep their name
        var sb = new StringBuilder();
        bool up = false;
        foreach (char c in cssProp.Trim())
        {
            if (c == '-') { up = sb.Length > 0; continue; }
            sb.Append(up ? char.ToUpperInvariant(c) : c);
            up = false;
        }
        return sb.ToString();
    }

    private static string JsKey(string key) =>
        Regex.IsMatch(key, @"^[A-Za-z_$][\w$]*$") ? key : "'" + key.Replace("'", "\\'") + "'";

    private static string JsStyleLiteral(string value) =>
        "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    // ───────────────────────────── text ─────────────────────────────

    /// <summary>
    /// Replace the element's text. Only an element whose content is plain text (no child tags, no
    /// <c>{expression}</c> / <c>{{ mustache }}</c>) can be edited this way; leading and trailing whitespace of
    /// the content (the source's indentation) is kept.
    /// </summary>
    public static SourceEdit SetText(string text, SourceTag tag, SourceDialect dialect, string newText)
    {
        if (tag.SelfClosing || (dialect != SourceDialect.Jsx && VoidElements.Contains(tag.Name)))
            return SourceEdit.Fail("noContent");
        int end = FindElementEnd(text, tag, dialect);
        if (end < 0) return SourceEdit.Fail("noClose");
        int closeStart = text.LastIndexOf("</", end - 1, StringComparison.Ordinal);
        if (closeStart < tag.End) return SourceEdit.Fail("noClose");
        string inner = text[tag.End..closeStart];
        if (inner.Contains('<')) return SourceEdit.Fail("hasChildren");
        if (inner.Contains('{')) return SourceEdit.Fail("textExpression");

        int lead = 0;
        while (lead < inner.Length && char.IsWhiteSpace(inner[lead])) lead++;
        int trail = inner.Length;
        while (trail > lead && char.IsWhiteSpace(inner[trail - 1])) trail--;

        string encoded = EncodeText(newText, dialect);
        string rebuilt = inner[..lead] + encoded + inner[trail..];
        return SourceEdit.Done(text[..tag.End] + rebuilt + text[closeStart..]);
    }

    public static string EncodeText(string value, SourceDialect dialect)
    {
        if (dialect == SourceDialect.Jsx)
        {
            // JSX text cannot hold { } < > — a value with them becomes a string expression.
            return value.IndexOfAny(new[] { '{', '}', '<', '>' }) >= 0 ? "{" + JsString(value) + "}" : value;
        }
        string html = value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        if (dialect is SourceDialect.Vue) html = html.Replace("{{", "{&#123;");
        if (dialect is SourceDialect.Svelte) html = html.Replace("{", "&#123;").Replace("}", "&#125;");
        return html;
    }

    // ───────────────────────────── structure ─────────────────────────────

    /// <summary>Remove the element; when it owned whole lines, those lines go too.</summary>
    public static SourceEdit Delete(string text, SourceTag tag, SourceDialect dialect)
    {
        int end = FindElementEnd(text, tag, dialect);
        if (end < 0) return SourceEdit.Fail("noClose");
        var (from, to) = LineSpan(text, tag.Start, end);
        return SourceEdit.Done(text[..from] + text[to..]);
    }

    /// <summary>Insert a copy of the element right after it (on its own line when it owns its lines).</summary>
    public static SourceEdit Duplicate(string text, SourceTag tag, SourceDialect dialect)
    {
        int end = FindElementEnd(text, tag, dialect);
        if (end < 0) return SourceEdit.Fail("noClose");
        var (from, to) = LineSpan(text, tag.Start, end);
        if (from != tag.Start || to != end)
        {
            string block = text[from..to];
            if (!block.EndsWith('\n')) block += "\n";
            return SourceEdit.Done(text[..to] + (to > 0 && text[to - 1] != '\n' ? "\n" : "") + block + text[to..]);
        }
        string copy = text[tag.Start..end];
        return SourceEdit.Done(text[..end] + copy + text[end..]);
    }

    /// <summary>
    /// Swap the element with the element that follows it in the source. Refused unless only whitespace (or
    /// comments) separates them — then they are siblings written next to each other, not two elements that a
    /// loop or another component happened to render side by side.
    /// </summary>
    public static SourceEdit SwapWithNext(string text, SourceTag tag, SourceDialect dialect, int expectedNextStart = -1)
    {
        int end = FindElementEnd(text, tag, dialect);
        if (end < 0) return SourceEdit.Fail("noClose");
        int i = end;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
            {
                int c = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (c < 0) return SourceEdit.Fail("noSibling");
                i = c + 3;
                continue;
            }
            break;
        }
        if (i >= text.Length - 1 || text[i] != '<' || !char.IsLetter(text[i + 1])) return SourceEdit.Fail("noSibling");
        // The page said which element comes next; the source must agree, or these two are not written side by side.
        if (expectedNextStart >= 0 && i != expectedNextStart) return SourceEdit.Fail("noSibling");
        var next = ParseOpenTag(text, i, dialect);
        if (next == null) return SourceEdit.Fail("noSibling");
        int nextEnd = FindElementEnd(text, next, dialect);
        if (nextEnd < 0) return SourceEdit.Fail("noClose");

        string a = text[tag.Start..end];
        string between = text[end..next.Start];
        string b = text[next.Start..nextEnd];
        return SourceEdit.Done(text[..tag.Start] + b + between + a + text[nextEnd..]);
    }

    /// <summary>[from, to) widened to whole lines when the span starts and ends its lines (only whitespace around).</summary>
    private static (int From, int To) LineSpan(string text, int start, int end)
    {
        int lineStart = start;
        while (lineStart > 0 && text[lineStart - 1] is ' ' or '\t') lineStart--;
        bool ownsStart = lineStart == 0 || text[lineStart - 1] == '\n';
        int lineEnd = end;
        while (lineEnd < text.Length && text[lineEnd] is ' ' or '\t' or '\r') lineEnd++;
        bool ownsEnd = lineEnd == text.Length || text[lineEnd] == '\n';
        if (ownsStart && ownsEnd)
            return (lineStart, lineEnd < text.Length ? lineEnd + 1 : lineEnd);
        return (start, end);
    }
}
