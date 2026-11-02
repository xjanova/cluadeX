using System.IO;
using System.Text.RegularExpressions;

namespace CluadeX.Services.UiStudio.Graph;

/// <summary>
/// Python: <c>import a.b</c> / <c>from .x import y</c> / <c>from ..pkg import mod</c> resolved like the interpreter
/// would from the project's source roots (the project folder, src/, and the folder above each top-level package),
/// a submodule winning over a name in __init__.py. Then what the file does with the names it imported: subclasses
/// them (inherits), calls them, uses them; Django <c>path(…, views.x)</c> / <c>include('app.urls')</c> and FastAPI /
/// Flask router registrations are routes. An absolute import the roots cannot resolve but whose last part names
/// exactly one project module is an inferred edge.
/// </summary>
internal static class PythonReader
{
    private static readonly Regex Import = new(@"^[ \t]*import[ \t]+(?<l>[\w. \t,]+)", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex FromImport = new(@"^[ \t]*from[ \t]+(?<m>\.*[\w.]*)[ \t]+import[ \t]+(?<l>\([^)]*\)|[^\n#]+)", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ClassDef = new(@"^[ \t]*class[ \t]+(?<n>\w+)[ \t]*\((?<b>[^)]*)\)", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex DjangoPath = new(@"(?<![\w.])(?:re_)?path\s*\(\s*(?<q>['""])(?<u>[^'""\n]*)\k<q>\s*,\s*(?<v>include\s*\(\s*(?<q2>['""])(?<inc>[^'""\n]+)\k<q2>|[\w.]+)", RegexOptions.Compiled);
    private static readonly Regex Router = new(@"\.(?<m>include_router|register_blueprint|add_api_route|mount)\s*\(\s*(?<n>[\w.]+)", RegexOptions.Compiled);

    public static void Read(GraphContext cx)
    {
        var py = cx.OfExt(".py").ToList();
        if (py.Count == 0) return;
        foreach (var f in py) cx.Graph.ById[f.Id].Kind = GraphNodeKind.Code;
        var roots = SourceRoots(cx, py);
        var byStem = py.GroupBy(f => Path.GetFileNameWithoutExtension(f.Full) == "__init__" ? Path.GetFileName(f.Dir) : Path.GetFileNameWithoutExtension(f.Full), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var f in py)
        {
            cx.Ct.ThrowIfCancellationRequested();
            string code = f.Masked;
            // local name → (module file, confirmed?, why)
            var names = new Dictionary<string, (SourceFile File, bool Inferred, string? Why)>(StringComparer.Ordinal);

            foreach (Match m in Import.Matches(code))
                foreach (var part in m.Groups["l"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var bits = Regex.Split(part, @"\s+as\s+");
                    string module = bits[0].Trim();
                    var r = Module(cx, f, module, roots, byStem);
                    int line = f.Lines.LineOf(m.Index);
                    if (r == null) { Package(cx, f, module, line); continue; }
                    cx.Edge(f, r.Value.File, GraphEdgeKind.Imports, line, r.Value.Inferred, r.Value.Why);
                    names[bits.Length > 1 ? bits[1].Trim() : module.Split('.')[0]] = r.Value;
                    if (bits.Length == 1) names[module] = r.Value;   // a.b.c used as a.b.c.x
                }
            foreach (Match m in FromImport.Matches(code))
            {
                string module = m.Groups["m"].Value;
                int line = f.Lines.LineOf(m.Index);
                var items = m.Groups["l"].Value.Trim().TrimStart('(').TrimEnd(')').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var baseMod = Module(cx, f, module, roots, byStem);
                foreach (var item in items)
                {
                    var bits = Regex.Split(item.Trim(), @"\s+as\s+");
                    string name = bits[0].Trim();
                    if (name == "*" || name.Length == 0) { if (baseMod is { } all) cx.Edge(f, all.File, GraphEdgeKind.Imports, line, all.Inferred, all.Why); continue; }
                    // from pkg import mod: the submodule file wins over a name defined in pkg/__init__.py
                    var r = Module(cx, f, module.EndsWith('.') ? module + name : module + "." + name, roots, byStem, quietInferred: true) ?? baseMod;
                    if (r == null) continue;
                    cx.Edge(f, r.Value.File, GraphEdgeKind.Imports, line, r.Value.Inferred, r.Value.Why);
                    names[bits.Length > 1 ? bits[1].Trim() : name] = r.Value;
                }
                if (baseMod == null && !module.StartsWith('.')) Package(cx, f, module, line);
            }

            void Use(string name, GraphEdgeKind kind, int at, string? label = null)
            {
                if (names.TryGetValue(name, out var r) && r.File != f) cx.Edge(f, r.File, kind, f.Lines.LineOf(at), r.Inferred, r.Why, label);
            }
            foreach (Match m in ClassDef.Matches(code))
                foreach (Match b in Regex.Matches(m.Groups["b"].Value, @"(?<![\w.=])(?<n>[A-Za-z_][\w.]*)(?![\w.]*\s*=)"))
                {
                    string n = b.Groups["n"].Value;
                    Use(names.ContainsKey(n) ? n : n.Split('.')[0], GraphEdgeKind.Inherits, m.Groups["b"].Index + b.Index);
                }
            var importLines = new HashSet<int>(Import.Matches(code).Concat(FromImport.Matches(code)).SelectMany(m => Enumerable.Range(f.Lines.LineOf(m.Index), f.Lines.LineOf(m.Index + m.Length) - f.Lines.LineOf(m.Index) + 1)));
            foreach (var name in names.Keys.ToList())
            {
                foreach (Match u in Regex.Matches(code, @"(?<![\w.])" + Regex.Escape(name) + @"(?<rest>(?:\.\w+)*)\s*(?<call>\()?"))
                {
                    if (importLines.Contains(f.Lines.LineOf(u.Index))) continue;
                    if (Regex.IsMatch(code[Math.Max(0, u.Index - 6)..u.Index], @"(?:def|class)\s+$")) continue;
                    Use(name, u.Groups["call"].Success ? GraphEdgeKind.Calls : GraphEdgeKind.References, u.Index);
                    if (u.Groups["call"].Success) break;
                }
            }

            // Django urls and app routers
            foreach (Match m in DjangoPath.Matches(code))
            {
                string uri = "/" + f.Text.Substring(m.Groups["u"].Index, m.Groups["u"].Length).TrimStart('^', '/');
                int line = f.Lines.LineOf(m.Index);
                if (m.Groups["inc"].Success)
                {
                    string mod = f.Text.Substring(m.Groups["inc"].Index, m.Groups["inc"].Length);
                    if (Module(cx, f, mod, roots, byStem) is { } inc && inc.File != f) cx.Edge(f, inc.File, GraphEdgeKind.Routes, line, inc.Inferred, inc.Why, uri);
                    continue;
                }
                string view = m.Groups["v"].Value;
                string head = view.Split('.')[0];
                if (names.TryGetValue(head, out var r) && r.File != f) cx.Edge(f, r.File, GraphEdgeKind.Routes, line, r.Inferred, r.Why, uri + " → " + view);
            }
            foreach (Match m in Router.Matches(code))
            {
                string head = m.Groups["n"].Value.Split('.')[0];
                if (names.TryGetValue(head, out var r) && r.File != f) cx.Edge(f, r.File, GraphEdgeKind.Routes, f.Lines.LineOf(m.Index), r.Inferred, r.Why, m.Groups["m"].Value);
            }
        }
    }

    private static void Package(GraphContext cx, SourceFile f, string module, int line)
    {
        string top = module.TrimStart('.').Split('.')[0];
        if (top.Length > 0 && cx.Package(top, "pip") is { } id) cx.Edge(f, id, GraphEdgeKind.Imports, line);
    }

    /// <summary>The folders a module path is read from: the project, src/, and the parent of every top-level package
    /// (a folder with __init__.py whose parent has none).</summary>
    private static List<string> SourceRoots(GraphContext cx, List<SourceFile> py)
    {
        var roots = new List<string> { cx.FullRoot.TrimEnd(Path.DirectorySeparatorChar), Path.Combine(cx.FullRoot, "src") };
        var packages = py.Where(f => Path.GetFileName(f.Full) == "__init__.py").Select(f => f.Dir).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in packages)
        {
            string? parent = Path.GetDirectoryName(pkg);
            if (parent != null && !packages.Contains(parent) && !roots.Contains(parent, StringComparer.OrdinalIgnoreCase)) roots.Add(parent);
        }
        return roots;
    }

    private static (SourceFile File, bool Inferred, string? Why)? Module(GraphContext cx, SourceFile from, string module, List<string> roots,
        Dictionary<string, List<SourceFile>> byStem, bool quietInferred = false)
    {
        if (module.StartsWith('.'))
        {
            int dots = module.TakeWhile(c => c == '.').Count();
            string dir = from.Dir;
            for (int i = 1; i < dots; i++) dir = Path.GetDirectoryName(dir) ?? dir;
            string rest = module[dots..];
            return Find(cx, dir, rest) is { } rel ? (rel, false, null) : null;
        }
        // a.b.c, then a.b (c is a name in a/b.py), then a
        var parts = module.Split('.');
        for (int n = parts.Length; n >= 1; n--)
        {
            string sub = string.Join('.', parts.Take(n));
            foreach (var root in roots)
                if (Find(cx, root, sub) is { } f) return (f, false, null);
            if (quietInferred) return null;
        }
        string last = parts[^1];
        if (byStem.TryGetValue(last, out var l) && l.Count == 1 && l[0] != from)
            return (l[0], true, $"'{module}' is not found under the project's source roots — matched the only project module named {last}");
        return null;
    }

    private static SourceFile? Find(GraphContext cx, string dir, string dotted)
    {
        if (dotted.Length == 0) return cx.FileAt(Path.Combine(dir, "__init__.py"));
        string path = Path.Combine(dir, dotted.Replace('.', Path.DirectorySeparatorChar));
        return cx.FileAt(path + ".py") ?? cx.FileAt(Path.Combine(path, "__init__.py"));
    }
}
