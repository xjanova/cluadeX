using System.IO;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;

namespace CluadeX.Views.Editor;

/// <summary>
/// Code folding (the [−] marks in the gutter, as in Notepad++): braces for C-like languages (plus #region and
/// multi-line /* */ comments), tags for XML/HTML, indentation for Python/YAML/GDScript. Pure functions of the text,
/// so they can be proven without a window; the view recomputes them after a pause in typing.
/// </summary>
public static class EditorFolding
{
    public enum Kind { None, Brace, Xml, Indent }

    public static Kind For(string fileName)
    {
        string ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "xml" or "xaml" or "csproj" or "vbproj" or "fsproj" or "props" or "targets" or "config" or "svg" or "resx"
                or "plist" or "xsl" or "xslt" or "html" or "htm" or "xhtml" or "vue" => Kind.Xml,
            "py" or "pyw" or "yaml" or "yml" or "gd" or "nim" or "coffee" => Kind.Indent,
            "cs" or "csx" or "java" or "js" or "mjs" or "cjs" or "jsx" or "ts" or "tsx" or "c" or "h" or "cpp" or "cc" or "cxx"
                or "hpp" or "go" or "rs" or "swift" or "kt" or "kts" or "dart" or "scala" or "php" or "json" or "jsonc" or "json5"
                or "css" or "scss" or "less" or "gdshader" or "shader" or "hlsl" or "glsl" or "proto" or "sol" or "zig" or "ps1"
                or "psm1" or "groovy" or "gradle" or "m" or "mm" => Kind.Brace,
            _ => Kind.None,
        };
    }

    /// <summary>Foldings for a brace language: { … } blocks that span lines, /* … */ comments that span lines, and
    /// #region … #endregion. Braces inside strings and comments are ignored.</summary>
    public static List<NewFolding> Braces(string text)
    {
        var result = new List<NewFolding>();
        var open = new Stack<int>();
        var regions = new Stack<(int Start, string Name)>();
        int n = text.Length, i = 0;
        bool lineStart = true;
        while (i < n)
        {
            char c = text[i];
            if (c == '\n') { lineStart = true; i++; continue; }
            if (lineStart && (c == ' ' || c == '\t' || c == '\r')) { i++; continue; }
            bool atLineStart = lineStart;
            lineStart = false;

            if (c == '#' && atLineStart)
            {
                int end = LineEnd(text, i);
                string directive = text[i..end].TrimEnd();
                if (directive.StartsWith("#region", StringComparison.Ordinal)) regions.Push((i, directive));
                else if (directive.StartsWith("#endregion", StringComparison.Ordinal) && regions.Count > 0)
                {
                    var r = regions.Pop();
                    result.Add(new NewFolding(r.Start, end) { Name = r.Name });
                }
                i = end;
                continue;
            }
            if (c == '/' && i + 1 < n && text[i + 1] == '/') { i = LineEnd(text, i); continue; }
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                int stop = close < 0 ? n : close + 2;
                if (text.IndexOf('\n', i, stop - i) >= 0) result.Add(new NewFolding(i, stop) { Name = "/* … */" });
                i = stop;
                continue;
            }
            if (c is '"' or '\'' or '`') { i = SkipString(text, i, c); continue; }
            if (c == '{') open.Push(i);
            else if (c == '}' && open.Count > 0)
            {
                int start = open.Pop();
                if (text.IndexOf('\n', start, i - start) >= 0) result.Add(new NewFolding(start, i + 1));
            }
            i++;
        }
        result.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return result;
    }

    /// <summary>Foldings by indentation: a line followed by more-indented lines folds them (from the end of the line,
    /// so the header stays visible). Blank lines inside a block belong to it.</summary>
    public static List<NewFolding> Indentation(string text)
    {
        var result = new List<NewFolding>();
        var lines = new List<(int Start, int End, int Indent, bool Blank)>();
        int pos = 0;
        while (pos <= text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            int end = nl < 0 ? text.Length : nl;
            int contentEnd = end > pos && text[end - 1] == '\r' ? end - 1 : end;
            int indent = 0, k = pos;
            for (; k < contentEnd && (text[k] == ' ' || text[k] == '\t'); k++) indent += text[k] == '\t' ? 4 : 1;
            lines.Add((pos, contentEnd, indent, k == contentEnd));
            if (nl < 0) break;
            pos = nl + 1;
        }
        var stack = new Stack<int>();   // indices of header lines still open
        int lastContent = -1;
        for (int li = 0; li < lines.Count; li++)
        {
            var l = lines[li];
            if (l.Blank) continue;
            while (stack.Count > 0 && lines[stack.Peek()].Indent >= l.Indent)
                Close(stack.Pop(), lastContent);
            if (lastContent >= 0 && l.Indent > lines[lastContent].Indent) stack.Push(lastContent);
            lastContent = li;
        }
        while (stack.Count > 0) Close(stack.Pop(), lastContent);
        result.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return result;

        void Close(int header, int last)
        {
            if (last > header) result.Add(new NewFolding(lines[header].End, lines[last].End));
        }
    }

    /// <summary>Recompute the foldings of <paramref name="document"/> for <paramref name="kind"/>.</summary>
    public static void Update(FoldingManager manager, TextDocument document, Kind kind)
    {
        switch (kind)
        {
            case Kind.Xml:
                new XmlFoldingStrategy { ShowAttributesWhenFolded = true }.UpdateFoldings(manager, document);
                break;
            case Kind.Brace:
                manager.UpdateFoldings(Braces(document.Text), -1);
                break;
            case Kind.Indent:
                manager.UpdateFoldings(Indentation(document.Text), -1);
                break;
            default:
                manager.Clear();
                break;
        }
    }

    private static int LineEnd(string text, int from)
    {
        int nl = text.IndexOf('\n', from);
        return nl < 0 ? text.Length : nl;
    }

    /// <summary>Past a string literal. Ordinary quotes end at the line's end at the latest (an apostrophe in a Rust
    /// lifetime or an unclosed quote must not swallow the rest of the file); backtick templates may span lines.</summary>
    private static int SkipString(string text, int i, char quote)
    {
        int n = text.Length;
        for (int k = i + 1; k < n; k++)
        {
            char c = text[k];
            if (c == '\\') { k++; continue; }
            if (c == quote) return k + 1;
            if (c == '\n' && quote != '`') return k;
        }
        return n;
    }
}
