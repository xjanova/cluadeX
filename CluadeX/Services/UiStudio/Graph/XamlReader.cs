using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CluadeX.Services.UiStudio.Graph;

/// <summary>
/// XAML (WPF, MAUI, Avalonia, WinUI) read as XML, against the types the C# read bound: the code-behind
/// (<c>x:Class</c>, and event handlers that exist in it), the controls it places (<c>&lt;views:ChatView/&gt;</c> →
/// that view's .xaml), the resource dictionaries it merges, the types its markup names (<c>{x:Type}</c>,
/// <c>{x:Static}</c>, converters, markup extensions) and its bindings. A binding becomes an edge to the file that
/// declares the bound property, when the DataContext is known from the code: <c>d:DataContext</c>, a
/// <c>.DataContext</c> element, a typed DataTemplate (its DataType, and the view placed inside it), or
/// <c>DataContext = …</c> in C#. With none of those, the view model is matched by name (FooView → FooViewModel) and
/// the edge is inferred.
/// </summary>
internal static class XamlReader
{
    private const string Xns = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string Dns = "http://schemas.microsoft.com/expression/blend/2008";

    private static readonly Regex Ext = new(@"^\{\s*(?<n>[\w]+:?[\w.]*)\s*(?<a>.*)\}$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Inner = new(@"\{\s*(?<n>[\w]+:[\w.]+)\s*(?<a>[^{}]*)\}", RegexOptions.Compiled);

    private sealed record Ctx(string? Vm, string Why, bool Inferred);

    public static void Read(GraphContext cx, CSharpModel? cs)
    {
        var files = cx.OfExt(".xaml", ".axaml").ToList();
        if (files.Count == 0) return;
        var docs = new Dictionary<SourceFile, XDocument>();
        foreach (var f in files)
        {
            cx.Ct.ThrowIfCancellationRequested();
            // XAML has no DTD: one is refused (an entity bomb in a cloned repo never expands), and nothing is fetched
            try
            {
                using var reader = XmlReader.Create(new StringReader(f.Text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                docs[f] = XDocument.Load(reader, LoadOptions.SetLineInfo);
            }
            catch (XmlException) { FallbackCodeBehind(cx, f); }
        }

        // pass 1: what each view's DataContext is, from typed DataTemplates anywhere (<DataTemplate DataType="{x:Type vm:Chat}"><views:ChatView/>)
        var templated = new Dictionary<string, (string Vm, SourceFile File, int Line)>(StringComparer.Ordinal);
        if (cs != null)
            foreach (var (f, doc) in docs)
                foreach (var tpl in doc.Descendants().Where(e => e.Name.LocalName == "DataTemplate"))
                {
                    string? vm = TemplateType(tpl, cs);
                    var first = tpl.Elements().FirstOrDefault();
                    if (vm == null || first == null || ClrNamespace(first.Name.NamespaceName) is not { } ns) continue;
                    string view = ns + "." + first.Name.LocalName;
                    if (cs.Type(view) != null) templated.TryAdd(view, (vm, f, Line(first)));
                }

        foreach (var (file, doc) in docs)
        {
            cx.Ct.ThrowIfCancellationRequested();
            var root = doc.Root!;
            cx.Graph.ById[file.Id].Kind = root.Name.LocalName switch
            {
                "Window" or "Page" or "NavigationWindow" or "ContentPage" or "TabbedPage" or "Shell" => GraphNodeKind.Page,
                "UserControl" or "ContentView" or "TemplatedControl" or "Grid" or "Border" => GraphNodeKind.Component,
                "ResourceDictionary" or "Styles" or "Style" => GraphNodeKind.Style,
                _ => GraphNodeKind.Code,
            };
            string? cls = root.Attribute(XName.Get("Class", Xns))?.Value;
            SourceFile? codeBehind = null;
            if (cls != null)
            {
                codeBehind = cx.FileAt(file.Full + ".cs") ?? (cs?.PrimaryFile(cls));
                if (codeBehind != null) cx.Edge(file, codeBehind, GraphEdgeKind.CodeBehind, Line(root.Attribute(XName.Get("Class", Xns))!), label: "x:Class");
            }

            // the root DataContext, strongest proof first
            Ctx? rootCtx = null;
            if (cs != null)
            {
                if (root.Attribute(XName.Get("DataContext", Dns)) is { } dd && DesignType(dd, cs) is { } dvm) rootCtx = new Ctx(dvm, "d:DataContext", false);
                else if (root.Elements().FirstOrDefault(e => e.Name.LocalName.EndsWith(".DataContext", StringComparison.Ordinal))?.Elements().FirstOrDefault() is { } dcEl
                         && TypeOf(dcEl, cs) is { } evm) rootCtx = new Ctx(evm, "DataContext element", false);
                else if (cls != null && cs.DataContexts.TryGetValue(cls, out var set)) rootCtx = new Ctx(set.Vm, $"DataContext = … in {set.File.Id}:{set.Line}", false);
                else if (cls != null && templated.TryGetValue(cls, out var t)) rootCtx = new Ctx(t.Vm, $"DataTemplate in {t.File.Id}:{t.Line}", false);
                else if (cls != null && ByConvention(cls, cs) is { } guess)
                    rootCtx = new Ctx(guess, $"the DataContext is not set in XAML or code — matched by the view's name ({Simple(cls)} → {Simple(guess)})", true);
            }

            Walk(cx, cs, file, root, rootCtx, rootCtx, cls, codeBehind);
        }
    }

    private static void Walk(GraphContext cx, CSharpModel? cs, SourceFile file, XElement el, Ctx? ctx, Ctx? rootCtx, string? cls, SourceFile? codeBehind)
    {
        // a typed template switches the binding context for what it holds; an untyped one makes it unknown
        if (el.Name.LocalName is "DataTemplate" or "HierarchicalDataTemplate" or "ControlTemplate")
            ctx = cs != null && TemplateType(el, cs) is { } tvm ? new Ctx(tvm, "DataTemplate DataType", false) : null;

        if (cs != null && ClrNamespace(el.Name.NamespaceName) is { } ns && !el.Name.LocalName.Contains('.'))
        {
            string full = ns + "." + el.Name.LocalName;
            if (cs.Type(full) is { } type && cs.PrimaryFile(type) is { } target)
            {
                bool isDataContext = el.Parent?.Name.LocalName.EndsWith(".DataContext", StringComparison.Ordinal) == true;
                var view = target.Full.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase) ? cx.FileAt(target.Full[..^3]) : null;
                if (isDataContext) cx.Edge(file, target, GraphEdgeKind.Binds, Line(el), label: "DataContext");
                else if (view != null) cx.Edge(file, view, GraphEdgeKind.Renders, Line(el));
                else cx.Edge(file, target, GraphEdgeKind.References, Line(el));
            }
        }

        foreach (var attr in el.Attributes())
        {
            if (attr.IsNamespaceDeclaration) continue;
            string v = attr.Value.Trim();
            string name = attr.Name.LocalName;
            if (attr.Name.NamespaceName == Xns) continue;
            if (name == "Source" && v.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && !v.StartsWith('{'))
            {
                if (ResolveXamlPath(cx, file, v) is { } dict) cx.Edge(file, dict, GraphEdgeKind.Loads, Line(attr));
                continue;
            }
            if (name == "StartupUri" && ResolveXamlPath(cx, file, v) is { } start) { cx.Edge(file, start, GraphEdgeKind.Loads, Line(attr)); continue; }
            if (!v.StartsWith('{'))
            {
                // an event handler: a plain name that is a method of the code-behind class
                if (cls != null && codeBehind != null && cs != null && Regex.IsMatch(v, @"^[A-Za-z_]\w*$")
                    && cs.Member(cls, v) is { Member: Microsoft.CodeAnalysis.IMethodSymbol } handler)
                    cx.Edge(file, handler.File, GraphEdgeKind.CodeBehind, Line(attr), label: "x:Class");
                continue;
            }
            if (cs == null) continue;
            if (name == "DataContext" && attr.Name.NamespaceName != Dns)
            {
                // DataContext="{Binding Foo}": what follows sees Foo's type, when the code says what it is
                var bind = BindingPath(v);
                ctx = bind != null && ctx?.Vm != null && cs.Member(ctx.Vm, bind.Split('.')[0]) is { } m && cs.MemberType(m.Member) is { } sub
                    ? new Ctx(sub, ctx.Why, ctx.Inferred) : null;
            }
            Markup(cx, cs, file, el, attr, v, ctx, rootCtx, cls);
        }

        foreach (var child in el.Elements()) Walk(cx, cs, file, child, ctx, rootCtx, cls, codeBehind);
    }

    /// <summary>The markup extensions in one attribute value: types named ({x:Type}, {x:Static}, {local:LocExtension}),
    /// and the binding's first path segment checked against the context's type.</summary>
    private static void Markup(GraphContext cx, CSharpModel cs, SourceFile file, XElement el, XAttribute attr, string value, Ctx? ctx, Ctx? rootCtx, string? cls)
    {
        int line = Line(attr);
        foreach (Match m in Inner.Matches(value))
        {
            string n = m.Groups["n"].Value, args = m.Groups["a"].Value.Trim();
            int colon = n.IndexOf(':');
            string prefix = n[..colon], local = n[(colon + 1)..];
            if (prefix == "x" && local is "Type" or "Static")
            {
                string typeRef = args.Split(',')[0].Trim();
                if (typeRef.StartsWith("Type=", StringComparison.Ordinal) || typeRef.StartsWith("Member=", StringComparison.Ordinal)) typeRef = typeRef[(typeRef.IndexOf('=') + 1)..];
                if (local == "Static" && typeRef.LastIndexOf('.') is var dot and > 0) typeRef = typeRef[..dot];
                if (ResolveTypeRef(el, typeRef, cs) is { } full && cs.PrimaryFile(full) is { } f) cx.Edge(file, f, GraphEdgeKind.References, line);
                continue;
            }
            if (prefix is "x" or "d") continue;
            // {services:Loc key} → LocExtension (or Loc)
            if (el.GetNamespaceOfPrefix(prefix) is { } xn && ClrNamespace(xn.NamespaceName) is { } ns)
            {
                var f = cs.PrimaryFile(ns + "." + local + "Extension") ?? cs.PrimaryFile(ns + "." + local);
                if (f != null) cx.Edge(file, f, GraphEdgeKind.References, line);
            }
        }

        var top = Ext.Match(value);
        if (!top.Success) return;
        string kind = top.Groups["n"].Value;
        if (kind is not ("Binding" or "x:Bind" or "TemplateBinding")) return;
        if (kind == "TemplateBinding") return;
        string args0 = top.Groups["a"].Value;
        string? path = BindingPath(value);
        if (string.IsNullOrEmpty(path) || args0.Contains("ElementName=", StringComparison.Ordinal) || Regex.IsMatch(args0, @"(?<![\w.])Source\s*=")) return;

        Ctx? use = ctx;
        if (kind == "x:Bind") use = cls != null ? new Ctx(cls, "x:Bind (the page itself)", false) : null;
        else if (args0.Contains("RelativeSource", StringComparison.Ordinal))
        {
            // {Binding DataContext.Foo, RelativeSource={RelativeSource AncestorType=UserControl}} → the view's own DataContext
            if (!path.StartsWith("DataContext.", StringComparison.Ordinal)) return;
            path = path["DataContext.".Length..];
            use = rootCtx;
        }
        if (use?.Vm == null) return;
        string first = Regex.Match(path, @"^[A-Za-z_]\w*").Value;
        if (first.Length == 0) return;
        if (cs.Member(use.Vm, first) is { } member)
            cx.Edge(file, member.File, GraphEdgeKind.Binds, line, use.Inferred, use.Inferred ? use.Why : null, label: Simple(use.Vm));
    }

    /// <summary>{Binding Foo.Bar, Mode=TwoWay} / {Binding Path=Foo.Bar} → "Foo.Bar"; null for {Binding}.</summary>
    private static string? BindingPath(string value)
    {
        var top = Ext.Match(value);
        if (!top.Success) return null;
        // drop nested extensions so their commas do not split the list
        string args = Regex.Replace(top.Groups["a"].Value, @"\{[^{}]*\}", "");
        foreach (var raw in args.Split(','))
        {
            string part = raw.Trim();
            if (part.StartsWith("Path=", StringComparison.Ordinal)) return part[5..].Trim();
            if (part.Length > 0 && !part.Contains('=')) return part;
        }
        return null;
    }

    private static string? TemplateType(XElement tpl, CSharpModel cs)
    {
        var dt = tpl.Attributes().FirstOrDefault(a => a.Name.LocalName == "DataType");
        if (dt == null) return null;
        string v = dt.Value.Trim();
        var m = Regex.Match(v, @"^\{\s*x:Type\s+(?:TypeName=)?(?<t>[\w:.]+)\s*\}$");
        return ResolveTypeRef(tpl, m.Success ? m.Groups["t"].Value : v, cs);
    }

    private static string? DesignType(XAttribute a, CSharpModel cs)
    {
        var m = Regex.Match(a.Value, @"DesignInstance\s+(?:Type=)?(?:\{\s*x:Type\s+)?(?<t>[\w:.]+)");
        return m.Success ? ResolveTypeRef(a.Parent!, m.Groups["t"].Value, cs) : null;
    }

    private static string? TypeOf(XElement el, CSharpModel cs) =>
        ClrNamespace(el.Name.NamespaceName) is { } ns && cs.Type(ns + "." + el.Name.LocalName) != null ? ns + "." + el.Name.LocalName : null;

    /// <summary>"vm:ChatViewModel" in this element's scope → "App.ViewModels.ChatViewModel", when it is a project type.</summary>
    private static string? ResolveTypeRef(XElement scope, string typeRef, CSharpModel cs)
    {
        int colon = typeRef.IndexOf(':');
        if (colon <= 0) return null;
        var xn = scope.GetNamespaceOfPrefix(typeRef[..colon]);
        if (xn == null || ClrNamespace(xn.NamespaceName) is not { } ns) return null;
        string full = ns + "." + typeRef[(colon + 1)..];
        return cs.Type(full) != null ? full : null;
    }

    /// <summary>FooView / FooWindow / FooPage → FooViewModel (or FooWindowViewModel…), when exactly one project type has that name.</summary>
    private static string? ByConvention(string cls, CSharpModel cs)
    {
        string simple = Simple(cls);
        var names = new List<string> { simple + "ViewModel" };
        foreach (var suffix in new[] { "View", "Window", "Page", "Control", "Dialog" })
            if (simple.EndsWith(suffix, StringComparison.Ordinal) && simple.Length > suffix.Length) names.Add(simple[..^suffix.Length] + "ViewModel");
        foreach (var n in names)
            if (cs.BySimple.TryGetValue(n, out var decls) && decls.Select(d => d.File).Distinct().Count() == 1)
            {
                var file = decls[0].File;
                var t = cs.TypeIn(file, n);
                if (t != null) return t;
            }
        return null;
    }

    private static string Simple(string full) => full[(full.LastIndexOf('.') + 1)..];

    public static string? ClrNamespace(string xmlns)
    {
        if (xmlns.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            string rest = xmlns["clr-namespace:".Length..];
            int semi = rest.IndexOf(';');
            return semi >= 0 ? rest[..semi] : rest;
        }
        if (xmlns.StartsWith("using:", StringComparison.Ordinal)) return xmlns["using:".Length..];
        return null;
    }

    /// <summary>"Themes/Dark.xaml", "/Themes/Dark.xaml", "pack://application:,,,/App;component/Themes/Dark.xaml",
    /// "avares://App/Themes/Dark.axaml" → the project file.</summary>
    private static SourceFile? ResolveXamlPath(GraphContext cx, SourceFile from, string v)
    {
        string p = v;
        int comp = p.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
        if (comp >= 0) p = "/" + p[(comp + ";component/".Length)..];
        else if (p.StartsWith("avares://", StringComparison.OrdinalIgnoreCase)) { int slash = p.IndexOf('/', 9); p = slash > 0 ? p[slash..] : p; }
        if (!p.StartsWith('/')) return cx.FileAt(Path.Combine(from.Dir, p));
        // rooted: relative to the project file's folder — try each folder up from this one
        for (var dir = new DirectoryInfo(from.Dir); dir != null && dir.FullName.Length + 1 >= cx.FullRoot.Length; dir = dir.Parent)
            if (cx.FileAt(Path.Combine(dir.FullName, p.TrimStart('/'))) is { } f) return f;
        return null;
    }

    private static void FallbackCodeBehind(GraphContext cx, SourceFile f)
    {
        // a XAML file that is not well-formed (mid-edit): at least its code-behind, from x:Class outside comments
        var m = Regex.Match(f.Masked, @"\bx:Class\s*=\s*""[^""]+""");
        if (m.Success && cx.FileAt(f.Full + ".cs") is { } cb) cx.Edge(f, cb, GraphEdgeKind.CodeBehind, f.Lines.LineOf(m.Index), label: "x:Class");
    }

    private static int Line(IXmlLineInfo i) => i.HasLineInfo() ? i.LineNumber : 1;
}
