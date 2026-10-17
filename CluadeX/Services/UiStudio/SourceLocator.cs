using System.IO;
using System.Text;

namespace CluadeX.Services.UiStudio;

/// <summary>A source location the page reported for an element: a file inside the project and a 1-based line/column.</summary>
public sealed record SourceRef(string FullPath, string RelativePath, int Line, int Column)
{
    public override string ToString() => $"{RelativePath}:{Line}:{Column}";
}

/// <summary>
/// Reads and writes the <c>data-cx-src</c> locator. The page carries <c>relative/path.ext:line:col</c> (from the
/// CluadeX Vite plugin or from <see cref="InstrumentHtml"/>) or an absolute path (React's dev-only _debugSource).
/// A location is only ever resolved inside the open project and never into node_modules, .git or a dot-folder —
/// the page is not trusted to choose which file on the disk gets written.
/// </summary>
public static class SourceLocator
{
    public const string Attribute = "data-cx-src";

    /// <summary>Resolve "<c>path:line:col</c>" against <paramref name="root"/>. Null when malformed or outside the project.</summary>
    public static SourceRef? Resolve(string? src, string root)
    {
        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(root)) return null;
        src = src.Trim();
        // Strip a file:// or Vite "/@fs/" prefix the dev runtime may add.
        if (src.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)) src = Uri.UnescapeDataString(src[8..]);
        if (src.StartsWith("/@fs/", StringComparison.Ordinal)) src = src[5..];

        int c2 = src.LastIndexOf(':');
        if (c2 <= 0) return null;
        int c1 = src.LastIndexOf(':', c2 - 1);
        if (c1 <= 0) return null;
        if (!int.TryParse(src[(c1 + 1)..c2], out int line) || !int.TryParse(src[(c2 + 1)..], out int col)) return null;
        if (line < 1 || col < 1) return null;
        string path = src[..c1].Replace('/', Path.DirectorySeparatorChar);

        string fullRoot;
        string full;
        try
        {
            fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(fullRoot, path.TrimStart(Path.DirectorySeparatorChar)));
        }
        catch { return null; }

        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return null;
        string rel = full[fullRoot.Length..];
        foreach (var segment in rel.Split(Path.DirectorySeparatorChar))
            if (segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase) || segment.StartsWith('.'))
                return null;
        if (!File.Exists(full)) return null;
        return new SourceRef(full, rel.Replace(Path.DirectorySeparatorChar, '/'), line, col);
    }

    private static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "html", "head", "meta", "link", "script", "style", "title", "base", "noscript", "template",
    };

    /// <summary>
    /// Add <c>data-cx-src="rel:line:col"</c> to every element start tag of a plain HTML file — what the CluadeX
    /// static server does to the pages it serves, so a static site gets exact locations with no build step.
    /// Comments, doctype and the content of script/style/textarea are left alone. On anything it cannot read
    /// it returns the page unchanged (an un-instrumented page still works, just without locations).
    /// </summary>
    public static string InstrumentHtml(string html, string relativePath)
    {
        var sb = new StringBuilder(html.Length + html.Length / 8);
        string rel = SourceTagEditor.HtmlAttrEscape(relativePath.Replace('\\', '/'));
        int i = 0, copied = 0, line = 1, lineStart = 0;

        void AdvanceTo(int target)
        {
            for (int k = i; k < target && k < html.Length; k++)
                if (html[k] == '\n') { line++; lineStart = k + 1; }
            i = target;
        }

        try
        {
            while (i < html.Length)
            {
                int lt = html.IndexOf('<', i);
                if (lt < 0 || lt + 1 >= html.Length) break;
                AdvanceTo(lt);
                char n = html[lt + 1];
                if (n == '!' || n == '?')
                {
                    int end = string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0
                        ? html.IndexOf("-->", lt + 4, StringComparison.Ordinal) + 3
                        : html.IndexOf('>', lt) + 1;
                    if (end <= 0) break;
                    AdvanceTo(end);
                    continue;
                }
                if (n == '/' || !char.IsLetter(n)) { AdvanceTo(lt + 1); continue; }

                var tag = SourceTagEditor.ParseOpenTag(html, lt, SourceDialect.Html);
                if (tag == null) { AdvanceTo(lt + 1); continue; }
                if (!SkipTags.Contains(tag.Name) && tag.Find(Attribute) == null)
                {
                    sb.Append(html, copied, tag.NameEnd - copied);
                    sb.Append(' ').Append(Attribute).Append("=\"").Append(rel).Append(':').Append(line).Append(':').Append(lt - lineStart + 1).Append('"');
                    copied = tag.NameEnd;
                }
                AdvanceTo(tag.End);
                if (!tag.SelfClosing && tag.Name.ToLowerInvariant() is "script" or "style" or "textarea" or "title")
                {
                    int close = html.IndexOf("</" , i, StringComparison.Ordinal);
                    while (close >= 0 && string.Compare(html, close + 2, tag.Name, 0, tag.Name.Length, StringComparison.OrdinalIgnoreCase) != 0)
                        close = html.IndexOf("</", close + 2, StringComparison.Ordinal);
                    if (close < 0) break;
                    AdvanceTo(close);
                }
            }
            sb.Append(html, copied, html.Length - copied);
            return sb.ToString();
        }
        catch
        {
            return html;
        }
    }
}
