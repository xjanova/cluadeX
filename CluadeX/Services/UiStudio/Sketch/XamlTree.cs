using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace CluadeX.Services.UiStudio.Sketch;

/// <summary>One element of a XAML file as written: its name, attributes, children and the line it starts on.</summary>
internal sealed class XEl
{
    /// <summary>Local name ("Grid", "Button.Content", "#text" for a run of text).</summary>
    public string Name { get; init; } = "";
    /// <summary>The XML namespace the element is in (a clr-namespace for the project's own controls).</summary>
    public string Ns { get; init; } = "";
    /// <summary>Attributes by local name; XAML's own are "x:Key", "x:Name", "x:Class", the designer's "d:DesignWidth".</summary>
    public Dictionary<string, string> Attrs { get; } = new(StringComparer.Ordinal);
    public List<XEl> Kids { get; } = new();
    public XEl? Parent { get; set; }
    public string? Text { get; init; }
    public int Line { get; init; }

    public bool IsText => Name == "#text";
    /// <summary>A property element (&lt;Button.Content&gt;, &lt;Grid.ColumnDefinitions&gt;).</summary>
    public bool IsProperty => !IsText && Name.Contains('.');
    /// <summary>The property a property element sets ("Content" for &lt;Button.Content&gt;).</summary>
    public string Property => IsProperty ? Name[(Name.LastIndexOf('.') + 1)..] : "";

    public string? A(string name) => Attrs.TryGetValue(name, out var v) ? v : null;

    /// <summary>The property element &lt;X.name&gt; among the children.</summary>
    public XEl? Prop(string name) => Kids.FirstOrDefault(k => k.IsProperty && k.Property == name);

    public IEnumerable<XEl> Elements => Kids.Where(k => !k.IsText);

    public IEnumerable<XEl> Descendants()
    {
        foreach (var k in Kids)
        {
            yield return k;
            foreach (var d in k.Descendants()) yield return d;
        }
    }

    public override string ToString() => $"<{Name}> :{Line}";
}

/// <summary>A markup extension value: <c>{Binding Path}</c>, <c>{services:Loc key}</c>, <c>{StaticResource Key}</c>.</summary>
internal sealed record XExt(string Name, string Arg, Dictionary<string, string> Named)
{
    /// <summary>"Loc" for "services:Loc", "Binding" for "Binding".</summary>
    public string Short => Name.Contains(':') ? Name[(Name.IndexOf(':') + 1)..] : Name;
}

/// <summary>
/// Reads XAML forgivingly: an <see cref="XmlReader"/> walks the file and builds a light tree where every element
/// keeps the line it starts on (<see cref="IXmlLineInfo"/>). A half-written file keeps what was read before the
/// error, so a view being edited still draws. No DTDs, no external entities, no resolver: a project file can't make
/// the reader fetch or expand anything.
/// </summary>
internal static class XamlTree
{
    public const string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string DesignNs = "http://schemas.microsoft.com/expression/blend/2008";
    private const string CompatNs = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    /// <summary>Real views nest a few dozen levels; deeper than this is not drawn.</summary>
    public const int MaxDepth = 256;

    public static XEl? Parse(string text, out bool complete, int maxElements = 40_000)
    {
        complete = false;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true, MaxCharactersInDocument = 8_000_000, CheckCharacters = false,
        };
        XEl? root = null;
        var stack = new Stack<XEl>();
        int count = 0;
        try
        {
            using var sr = new StringReader(text);
            using var r = XmlReader.Create(sr, settings);
            var info = (IXmlLineInfo)r;
            while (r.Read())
            {
                switch (r.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        if (++count > maxElements) return root;
                        var el = new XEl { Name = r.LocalName, Ns = r.NamespaceURI, Line = info.LineNumber };
                        bool empty = r.IsEmptyElement;
                        if (r.MoveToFirstAttribute())
                        {
                            do
                            {
                                if (r.Prefix == "xmlns" || r.LocalName == "xmlns" || r.NamespaceURI == CompatNs) continue;
                                string key = r.NamespaceURI == XamlNs ? "x:" + r.LocalName : r.NamespaceURI == DesignNs ? "d:" + r.LocalName : r.LocalName;
                                el.Attrs[key] = r.Value;
                            } while (r.MoveToNextAttribute());
                            r.MoveToElement();
                        }
                        if (stack.Count > 0) { el.Parent = stack.Peek(); stack.Peek().Kids.Add(el); }
                        else if (root == null) root = el;
                        else return root;   // a second root: not XAML
                        if (!empty)
                        {
                            // the walks over the tree recurse per level: a file nested absurdly deep stops being read
                            // here instead of overflowing the stack later
                            if (stack.Count >= MaxDepth) return root;
                            stack.Push(el);
                        }
                        break;
                    }
                    case XmlNodeType.EndElement:
                        if (stack.Count > 0) stack.Pop();
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                        if (stack.Count > 0)
                        {
                            string t = Regex.Replace(r.Value, @"\s+", " ");
                            if (t.Trim().Length > 0) stack.Peek().Kids.Add(new XEl { Name = "#text", Text = t, Line = info.LineNumber, Parent = stack.Peek() });
                        }
                        break;
                }
            }
            complete = true;
        }
        catch (XmlException) { /* a half-written file: what was read before the error stays */ }
        return root;
    }

    /// <summary>The root element of a file — read on its own (a broken file further down still lists).</summary>
    public static XEl? ReadRoot(string text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true, CheckCharacters = false };
        try
        {
            using var sr = new StringReader(text);
            using var r = XmlReader.Create(sr, settings);
            var info = (IXmlLineInfo)r;
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element) continue;
                var el = new XEl { Name = r.LocalName, Ns = r.NamespaceURI, Line = info.LineNumber };
                if (r.MoveToFirstAttribute())
                {
                    do
                    {
                        if (r.Prefix == "xmlns" || r.LocalName == "xmlns") el.Attrs["xmlns:" + (r.Prefix == "xmlns" ? r.LocalName : "")] = r.Value;
                        else el.Attrs[r.NamespaceURI == XamlNs ? "x:" + r.LocalName : r.NamespaceURI == DesignNs ? "d:" + r.LocalName : r.LocalName] = r.Value;
                    } while (r.MoveToNextAttribute());
                }
                return el;
            }
        }
        catch (XmlException) { }
        return null;
    }

    // ───────────────────────────── markup extensions ─────────────────────────────

    /// <summary>A <c>{…}</c> value as a markup extension (null for a literal, and for "{}" — XAML's escape for a
    /// literal starting with a brace).</summary>
    public static XExt? Ext(string? value)
    {
        if (value == null) return null;
        string v = value.Trim();
        if (v.Length < 2 || v[0] != '{' || v.StartsWith("{}", StringComparison.Ordinal) || v[^1] != '}') return null;
        string body = v[1..^1].Trim();
        int sp = 0;
        while (sp < body.Length && !char.IsWhiteSpace(body[sp]) && body[sp] != ',') sp++;
        string name = body[..sp];
        string rest = body[sp..].Trim().TrimStart(',').Trim();
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string arg = "";
        foreach (var part in SplitTop(rest))
        {
            int eq = IndexOfTop(part, '=');
            if (eq > 0) named[part[..eq].Trim()] = part[(eq + 1)..].Trim();
            else if (arg.Length == 0) arg = part.Trim();
        }
        if (arg.Length == 0 && named.TryGetValue("Path", out var path)) arg = path;
        if (arg.Length == 0 && named.TryGetValue("Key", out var key)) arg = key;
        if (arg.Length == 0 && named.TryGetValue("ResourceKey", out var rk)) arg = rk;
        return new XExt(name, arg, named);
    }

    /// <summary>A literal value ("{}" escape removed); null when the value is a markup extension.</summary>
    public static string? Literal(string? value)
    {
        if (value == null) return null;
        if (value.StartsWith("{}", StringComparison.Ordinal)) return value[2..];
        return Ext(value) == null ? value : null;
    }

    private static IEnumerable<string> SplitTop(string s)
    {
        int depth = 0, start = 0;
        bool quote = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\'') quote = !quote;
            else if (quote) continue;
            else if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (c == ',' && depth == 0)
            {
                if (i > start) yield return s[start..i];
                start = i + 1;
            }
        }
        if (start < s.Length) yield return s[start..];
    }

    private static int IndexOfTop(string s, char ch)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}') depth--;
            else if (s[i] == ch && depth == 0) return i;
        }
        return -1;
    }

    /// <summary>A number as XAML writes it ("120", "12.5"); null for "Auto", "*", NaN and extensions.</summary>
    public static double? Number(string? v)
    {
        if (v == null) return null;
        return double.TryParse(v.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) && !double.IsNaN(d) && !double.IsInfinity(d) ? d : null;
    }

    /// <summary>The first number of a thickness/corner value ("8", "8,4" → 8).</summary>
    public static double? First(string? v) => v == null ? null : Number(v.Split(',', ' ')[0]);

    /// <summary>The CLR namespace of an element's XML namespace ("clr-namespace:App.Views;assembly=App" → "App.Views").</summary>
    public static string? ClrNamespace(string ns)
    {
        string n = ns.StartsWith("clr-namespace:", StringComparison.Ordinal) ? ns["clr-namespace:".Length..]
            : ns.StartsWith("using:", StringComparison.Ordinal) ? ns["using:".Length..] : "";
        int semi = n.IndexOf(';');
        if (semi >= 0) n = n[..semi];
        return n.Length > 0 ? n : null;
    }

    /// <summary>The text an element's inline content reads as (&lt;Run&gt;, &lt;Bold&gt;, &lt;Hyperlink&gt;, plain text).</summary>
    public static string InlineText(XEl el, Func<string?, string?> resolve, int depth = 0)
    {
        if (depth > 8) return "";
        var sb = new StringBuilder();
        foreach (var k in el.Kids)
        {
            if (k.IsText) { sb.Append(k.Text); continue; }
            if (k.IsProperty) { if (k.Property == "Inlines") sb.Append(InlineText(k, resolve, depth + 1)); continue; }
            switch (k.Name)
            {
                case "Run":
                    sb.Append(k.A("Text") is { } t ? resolve(t) ?? "…" : InlineText(k, resolve, depth + 1));
                    break;
                case "LineBreak":
                    sb.Append(' ');
                    break;
                case "Bold" or "Italic" or "Underline" or "Span" or "Hyperlink":
                    sb.Append(InlineText(k, resolve, depth + 1));
                    break;
            }
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ");
    }
}
