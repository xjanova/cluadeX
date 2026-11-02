using System.IO;
using System.Text.RegularExpressions;

namespace CluadeX.Services.UiStudio.Graph;

/// <summary>
/// Dart and Flutter: <c>import</c> / <c>export</c> / <c>part</c> (relative, and <c>package:name/…</c> through every
/// pubspec.yaml in the project), then what each file does with the names it can see — extends / with / implements,
/// builds a widget (renders), calls a static or top-level function. A name is resolved through this file's imports
/// (and what those export, honouring show/hide and <c>as</c> prefixes); a project class used without an import that
/// reaches it is an inferred edge. Routes: the <c>routes:</c> map, GoRouter's GoRoute(path:, builder:),
/// onGenerateRoute's cases, and every pushNamed / context.go / MaterialPageRoute(builder:) that leads to a page.
/// DI: get_it registrations and Provider/Riverpod/Bloc providers.
/// </summary>
internal static class DartReader
{
    private static readonly Regex Directive = new(@"(?<![\w$])(?<k>import|export|part)\s+(?<q>['""])(?<p>[^'""\n]*)\k<q>(?<rest>[^;]*);", RegexOptions.Compiled);
    private static readonly Regex Conditional = new(@"if\s*\([^)]*\)\s*(?<q>['""])(?<p>[^'""\n]*)\k<q>", RegexOptions.Compiled);
    private static readonly Regex ClassDecl = new(@"(?<![\w$])(?:(?:abstract|base|final|sealed|interface)\s+)*(?<k>class|mixin\s+class|mixin|enum|extension\s+type|extension)\s+(?<n>[A-Za-z_$][\w$]*)", RegexOptions.Compiled);
    private static readonly Regex TypedefDecl = new(@"(?<![\w$])typedef\s+(?<n>[A-Z_$][\w$]*)", RegexOptions.Compiled);
    private static readonly Regex TopFunction = new(@"^(?![ 	])(?:[\w$<>?,\[\] ]+?\s+)?(?<n>[a-z_$][\w$]*)\s*(?:<[^>\n]*>)?\s*\([^;{]*?\)\s*(?:async\s*\*?|sync\s*\*)?\s*(?:\{|=>)", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex UpperName = new(@"(?<![\w$.])(?<n>[A-Z][\w$]*)", RegexOptions.Compiled);
    private static readonly Regex Prefixed = new(@"(?<![\w$.])(?<p>[a-z_$][\w$]*)\.(?<n>[A-Za-z_$][\w$]*)", RegexOptions.Compiled);
    private static readonly Regex LowerCall = new(@"(?<![\w$.])(?<n>[a-z_$][\w$]*)\s*(?:<[^<>()\n]*>)?\s*\(", RegexOptions.Compiled);
    private static readonly Regex RouteConst = new(@"(?<![\w$])static\s+const\s+(?:String\s+)?(?<f>[\w$]+)\s*=\s*(?<q>['""])(?<p>[^'""\n]*)\k<q>", RegexOptions.Compiled);
    private static readonly Regex MapRoute = new(@"(?:(?<q>['""])(?<p>[^'""\n]*)\k<q>|(?<c>[A-Z][\w$]*\.[\w$]+))\s*:\s*\(\s*[\w$\s,]*\)\s*(?:=>|\{)", RegexOptions.Compiled);
    private static readonly Regex CaseRoute = new(@"(?<![\w$])case\s+(?:(?<q>['""])(?<p>[^'""\n]*)\k<q>|(?<c>[A-Z][\w$]*\.[\w$]+))\s*:", RegexOptions.Compiled);
    private static readonly Regex PushNamed = new(@"\.(?:pushNamed|pushReplacementNamed|popAndPushNamed|pushNamedAndRemoveUntil|restorablePushNamed)\s*(?:<[^<>()]*>)?\s*\((?:\s*context\s*,)?\s*(?:(?<q>['""])(?<p>[^'""\n]*)\k<q>|(?<c>[A-Z][\w$]*\.[\w$]+))", RegexOptions.Compiled);
    private static readonly Regex GoCall = new(@"\.(?<m>go|push|replace|pushReplacement|goNamed|pushNamed|replaceNamed)\s*(?:<[^<>()]*>)?\s*\(\s*(?<q>['""])(?<p>[^'""\n]*)\k<q>", RegexOptions.Compiled);
    private static readonly Regex PageRoute = new(@"(?<![\w$])(?:MaterialPageRoute|CupertinoPageRoute|PageRouteBuilder|MaterialPage|CupertinoPage|DialogRoute)\s*(?:<[^<>()]*>)?\s*\(", RegexOptions.Compiled);
    private static readonly Regex GoRoute = new(@"(?<![\w$])GoRoute\s*\(", RegexOptions.Compiled);
    private static readonly Regex GetIt = new(@"\.register(?:Singleton|LazySingleton|Factory|FactoryParam|SingletonAsync|LazySingletonAsync|FactoryAsync|SingletonWithDependencies)\s*(?:<\s*(?<t>[A-Z][\w$]*)[^<>()]*>)?\s*\(", RegexOptions.Compiled);
    private static readonly Regex Providers = new(@"(?<![\w$])(?:ChangeNotifierProvider|ListenableProvider|StateNotifierProvider|NotifierProvider|AsyncNotifierProvider|BlocProvider|RepositoryProvider|FutureProvider|StreamProvider|StateProvider|Provider)(?:\.\w+)?\s*(?:<\s*(?<t>[A-Z][\w$]*)[^<>()]*>)?\s*\(", RegexOptions.Compiled);

    private static readonly HashSet<string> WidgetBases = new(StringComparer.Ordinal)
    {
        "StatelessWidget", "StatefulWidget", "Widget", "HookWidget", "StatefulHookWidget", "ConsumerWidget", "ConsumerStatefulWidget",
        "HookConsumerWidget", "InheritedWidget", "InheritedNotifier", "InheritedModel", "RenderObjectWidget", "LeafRenderObjectWidget",
        "SingleChildRenderObjectWidget", "MultiChildRenderObjectWidget", "ProxyWidget", "PreferredSizeWidget", "GetView", "GetWidget",
    };

    private sealed class DartClass
    {
        public required string Name;
        public required SourceFile File;
        public int Line;
        public int Start, End;   // offsets of the declaration body
        public string? Extends;
        public List<(string Name, GraphEdgeKind Kind, int Offset)> Supers = new();
        public bool Widget;
    }

    private sealed class Lib
    {
        public required SourceFile File;
        public List<(SourceFile Target, string? Prefix, HashSet<string>? Show, HashSet<string>? Hide)> Imports = new();
        public List<(SourceFile Target, HashSet<string>? Show, HashSet<string>? Hide)> Exports = new();
        public List<SourceFile> Parts = new();
        public SourceFile? PartOf;
        public List<(int Start, int End)> DirectiveSpans = new();
    }

    private sealed record Route(string Path, string? Name, string Widget, SourceFile File, int Line, int At);

    public static void Read(GraphContext cx)
    {
        var files = cx.OfExt(".dart").ToList();
        if (files.Count == 0) return;
        var packages = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in cx.Configs.Where(c => Path.GetFileName(c.Full) == "pubspec.yaml"))
            if (Regex.Match(p.Text, @"^name:\s*['""]?(?<n>[\w]+)", RegexOptions.Multiline) is { Success: true } m)
                packages.TryAdd(m.Groups["n"].Value, Path.Combine(p.Dir, "lib"));

        // ── declarations and directives ──
        var classes = new Dictionary<string, List<DartClass>>(StringComparer.Ordinal);
        var functions = new Dictionary<string, List<(SourceFile File, int Line)>>(StringComparer.Ordinal);
        var declaredIn = new Dictionary<SourceFile, HashSet<string>>();
        var libs = new Dictionary<SourceFile, Lib>();
        foreach (var f in files)
        {
            cx.Ct.ThrowIfCancellationRequested();
            cx.Graph.ById[f.Id].Kind = GraphNodeKind.Code;
            var lib = libs[f] = new Lib { File = f };
            var names = declaredIn[f] = new HashSet<string>(StringComparer.Ordinal);
            string code = f.Masked;
            foreach (Match m in Directive.Matches(code))
            {
                lib.DirectiveSpans.Add((m.Index, m.Index + m.Length));
                string rest = m.Groups["rest"].Value;
                var specs = new List<string> { Spec(f, m.Groups["p"]) };
                foreach (Match c in Conditional.Matches(rest))
                    specs.Add(f.Text.Substring(m.Groups["rest"].Index + c.Groups["p"].Index, c.Groups["p"].Length));
                bool partOf = m.Groups["k"].Value == "part" && Regex.IsMatch(code[m.Index..(m.Index + m.Length)], @"^part\s+of\b");
                string? prefix = Regex.Match(rest, @"\bas\s+(?<a>[\w$]+)") is { Success: true } a ? a.Groups["a"].Value : null;
                HashSet<string>? show = List(rest, "show"), hide = List(rest, "hide");
                int line = f.Lines.LineOf(m.Index);
                foreach (var spec in specs.Distinct())
                {
                    var target = ResolveUri(cx, f, spec, packages);
                    if (target == null)
                    {
                        string pkg = spec.StartsWith("dart:", StringComparison.Ordinal) ? spec : spec.StartsWith("package:", StringComparison.Ordinal) ? spec[8..].Split('/')[0] : "";
                        if (pkg.Length > 0 && cx.Package(pkg, "pub") is { } pid) cx.Edge(f, pid, GraphEdgeKind.Imports, line);
                        continue;
                    }
                    cx.Edge(f, target, GraphEdgeKind.Imports, line, label: partOf ? "part of" : m.Groups["k"].Value == "import" ? null : m.Groups["k"].Value);
                    switch (m.Groups["k"].Value)
                    {
                        case "import": lib.Imports.Add((target, prefix, show, hide)); break;
                        case "export": lib.Exports.Add((target, show, hide)); break;
                        default: if (partOf) lib.PartOf = target; else lib.Parts.Add(target); break;
                    }
                }
            }
            foreach (Match m in ClassDecl.Matches(code))
            {
                string name = m.Groups["n"].Value;
                if (name is "on") continue;   // an unnamed extension: "extension on Foo"
                var cls = new DartClass { Name = name, File = f, Line = f.Lines.LineOf(m.Index), Start = m.Index };
                int brace = code.IndexOf('{', m.Index + m.Length);
                int semi = code.IndexOf(';', m.Index + m.Length);
                int headerEnd = brace < 0 ? (semi < 0 ? code.Length : semi) : semi >= 0 && semi < brace ? semi : brace;
                string header = code[(m.Index + m.Length)..headerEnd];
                int hOff = m.Index + m.Length;
                cls.End = brace >= 0 ? MatchBrace(code, brace) : headerEnd;
                foreach (Match s in Regex.Matches(header, @"\b(?<k>extends|with|implements|on)\s+(?<l>[^{]*?)(?=\b(?:extends|with|implements|on)\b|$)"))
                {
                    var kind = s.Groups["k"].Value == "extends" ? GraphEdgeKind.Inherits : GraphEdgeKind.Implements;
                    foreach (Match t in Regex.Matches(s.Groups["l"].Value, @"(?<![\w$.<])(?<n>[A-Z_$][\w$]*)(?:\s*<[^{]*?>)?"))
                    {
                        cls.Supers.Add((t.Groups["n"].Value, kind, hOff + s.Groups["l"].Index + t.Index));
                        if (kind == GraphEdgeKind.Inherits) cls.Extends ??= t.Groups["n"].Value;
                    }
                }
                if (!classes.TryGetValue(name, out var list)) classes[name] = list = new();
                list.Add(cls);
                names.Add(name);
            }
            foreach (Match m in TypedefDecl.Matches(code)) names.Add(m.Groups["n"].Value);
            foreach (Match m in TopFunction.Matches(code))
            {
                string n = m.Groups["n"].Value;
                if (n is "if" or "for" or "while" or "switch" or "catch" or "return" or "main") continue;
                if (!functions.TryGetValue(n, out var l)) functions[n] = l = new();
                l.Add((f, f.Lines.LineOf(m.Index)));
                names.Add(n);
            }
        }

        // widgets: a class that extends a Flutter widget base, or a project widget (to a fixpoint)
        var all = classes.Values.SelectMany(x => x).ToList();
        foreach (var c in all) c.Widget = c.Extends != null && WidgetBases.Contains(c.Extends);
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var c in all.Where(c => !c.Widget && c.Extends != null && classes.TryGetValue(c.Extends, out var b) && b.Any(x => x.Widget)))
                changed = c.Widget = true;
        }
        foreach (var c in all.Where(c => c.Widget)) cx.Graph.ById[c.File.Id].Kind = GraphNodeKind.Component;

        // what each file can see: its own library (parts included), its imports and what they export
        var exportCache = new Dictionary<SourceFile, Dictionary<string, SourceFile>>();
        Dictionary<string, SourceFile> Exported(SourceFile f, int depth)
        {
            if (exportCache.TryGetValue(f, out var hit)) return hit;
            var map = exportCache[f] = new Dictionary<string, SourceFile>(StringComparer.Ordinal);
            if (depth > 12) return map;
            foreach (var n in declaredIn[f]) map.TryAdd(n, f);
            foreach (var part in libs[f].Parts) if (declaredIn.TryGetValue(part, out var pn)) foreach (var n in pn) map.TryAdd(n, part);
            foreach (var (target, show, hide) in libs[f].Exports)
                if (libs.ContainsKey(target))
                    foreach (var (n, where) in Exported(target, depth + 1))
                        if ((show == null || show.Contains(n)) && (hide == null || !hide.Contains(n))) map.TryAdd(n, where);
            return map;
        }
        Lib LibraryOf(SourceFile f) => libs[f].PartOf is { } owner && libs.ContainsKey(owner) ? libs[owner] : libs[f];

        var allNames = declaredIn.Values.SelectMany(x => x).ToHashSet(StringComparer.Ordinal);
        var routes = CollectRoutes(cx, files, classes);

        foreach (var f in files)
        {
            cx.Ct.ThrowIfCancellationRequested();
            var lib = LibraryOf(f);
            var visible = new Dictionary<string, SourceFile>(StringComparer.Ordinal);
            foreach (var (n, where) in Exported(lib.File, 0)) visible.TryAdd(n, where);
            var prefixed = new Dictionary<string, Dictionary<string, SourceFile>>(StringComparer.Ordinal);
            foreach (var (target, prefix, show, hide) in lib.Imports)
            {
                if (!libs.ContainsKey(target)) continue;
                var into = prefix == null ? visible : prefixed.TryGetValue(prefix, out var p) ? p : prefixed[prefix] = new(StringComparer.Ordinal);
                foreach (var (n, where) in Exported(target, 0))
                    if ((show == null || show.Contains(n)) && (hide == null || !hide.Contains(n))) into.TryAdd(n, where);
            }

            // name → (file, confirmed?, why)
            (SourceFile File, bool Inferred, string? Why)? Find(string name, string? prefix = null)
            {
                if (prefix != null)
                    return prefixed.TryGetValue(prefix, out var p) && p.TryGetValue(name, out var pf) ? (pf, false, null) : null;
                if (visible.TryGetValue(name, out var vf)) return (vf, false, null);
                var decl = classes.TryGetValue(name, out var cl) ? cl.Select(c => c.File).Distinct().ToList()
                    : functions.TryGetValue(name, out var fl) ? fl.Select(x => x.File).Distinct().ToList() : null;
                if (decl is { Count: 1 }) return (decl[0], true, $"'{name}' is not reachable through this file's imports — matched the only project declaration of that name");
                return null;
            }
            bool IsWidget(string name, SourceFile where) => classes.TryGetValue(name, out var cl) && cl.Any(c => c.File == where && c.Widget);
            void Edge(string name, GraphEdgeKind kind, int offset, string? prefix = null, string? label = null)
            {
                if (Find(name, prefix) is not { } hit || hit.File == f) return;
                cx.Edge(f, hit.File, kind, f.Lines.LineOf(offset), hit.Inferred, hit.Why, label);
            }

            string code = f.Masked;
            var skip = new List<(int, int)>(lib.File == f ? lib.DirectiveSpans : libs[f].DirectiveSpans);
            var used = new HashSet<int>();   // offsets already read as a header, a route or a registration

            // class headers
            foreach (var c in classes.Values.SelectMany(x => x).Where(c => c.File == f))
                foreach (var (name, kind, at) in c.Supers)
                {
                    used.Add(at);
                    if (declaredIn[f].Contains(name) && visible.TryGetValue(name, out var self) && self == f) continue;
                    Edge(name, kind, at);
                }

            // routes in this file: tables point at pages, navigation points at the page of that route
            foreach (var r in routes.Where(r => r.File == f))
                if (Find(r.Widget) is { } page && page.File != f)
                {
                    used.Add(r.At);   // the page in a route table is routed to, not rendered here
                    cx.Edge(f, page.File, GraphEdgeKind.Routes, r.Line, page.Inferred, page.Why, r.Path);
                    cx.Graph.ById[page.File.Id].Kind = GraphNodeKind.Page;
                }
            foreach (var (path, at, isName) in Navigations(f, routes))
            {
                var r = isName ? routes.FirstOrDefault(x => x.Name == path) : routes.FirstOrDefault(x => PathMatches(x.Path, path));
                if (r == null) continue;
                // the page is resolved where the route table is, not here
                var hit = Exported(LibraryOf(r.File).File, 0).TryGetValue(r.Widget, out var pf) ? pf : null;
                if (hit == null)
                    foreach (var (target, _, _, _) in LibraryOf(r.File).Imports)
                        if (libs.ContainsKey(target) && Exported(target, 0).TryGetValue(r.Widget, out var pf2)) { hit = pf2; break; }
                if (hit != null && hit != f) cx.Edge(f, hit, GraphEdgeKind.Routes, f.Lines.LineOf(at), label: r.Path);
            }
            foreach (Match m in PageRoute.Matches(code))
            {
                int open = m.Index + m.Length - 1;
                int close = MatchParen(code, open);
                foreach (Match u in UpperName.Matches(code[open..close]))
                {
                    string n = u.Groups["n"].Value;
                    if (Find(n) is not { } hit || !IsWidget(n, hit.File)) continue;
                    used.Add(open + u.Index);
                    if (hit.File != f) cx.Edge(f, hit.File, GraphEdgeKind.Routes, f.Lines.LineOf(open + u.Index), hit.Inferred, hit.Why, n);
                    break;
                }
            }

            // DI
            foreach (var rx in new[] { GetIt, Providers })
                foreach (Match m in rx.Matches(code))
                {
                    int open = m.Index + m.Length - 1;
                    int close = MatchParen(code, open);
                    if (m.Groups["t"].Success) { used.Add(m.Groups["t"].Index); Edge(m.Groups["t"].Value, GraphEdgeKind.Registers, m.Index); }
                    foreach (Match u in UpperName.Matches(code[open..close]))
                    {
                        string n = u.Groups["n"].Value;
                        if (Find(n) is not { } hit || IsWidget(n, hit.File) || !classes.ContainsKey(n)) continue;
                        if (!Regex.IsMatch(code[(open + u.Index + n.Length)..Math.Min(close, open + u.Index + n.Length + 40)], @"^\s*(?:<[^<>()]*>)?\s*[(.]")) continue;
                        used.Add(open + u.Index);
                        Edge(n, GraphEdgeKind.Registers, open + u.Index);
                        break;
                    }
                }

            // every other use of a project name
            foreach (Match m in Prefixed.Matches(code))
            {
                if (In(skip, m.Index) || !prefixed.ContainsKey(m.Groups["p"].Value)) continue;
                string n = m.Groups["n"].Value;
                string after = code[(m.Index + m.Length)..Math.Min(code.Length, m.Index + m.Length + 30)];
                var kind = char.IsUpper(n[0])
                    ? Regex.IsMatch(after, @"^\s*(?:<[^<>()]*>)?\s*\(") ? (prefixed[m.Groups["p"].Value].TryGetValue(n, out var pw) && IsWidget(n, pw) ? GraphEdgeKind.Renders : GraphEdgeKind.References)
                    : Regex.IsMatch(after, @"^\.[\w$]+\s*\(") ? GraphEdgeKind.Calls : GraphEdgeKind.References
                    : GraphEdgeKind.Calls;
                Edge(n, kind, m.Index, m.Groups["p"].Value);
            }
            foreach (Match m in UpperName.Matches(code))
            {
                if (used.Contains(m.Index) || In(skip, m.Index)) continue;
                string n = m.Groups["n"].Value;
                if (!allNames.Contains(n)) continue;
                if (declaredIn[f].Contains(n)) continue;
                if (ClassDeclAt(code, m.Index)) continue;
                string after = code[(m.Index + m.Length)..Math.Min(code.Length, m.Index + m.Length + 40)];
                GraphEdgeKind kind;
                if (Regex.IsMatch(after, @"^\s*(?:<[^<>()]*>)?\s*\("))
                    kind = Find(n) is { } w && IsWidget(n, w.File) ? GraphEdgeKind.Renders : GraphEdgeKind.References;
                else if (Regex.IsMatch(after, @"^\.[\w$]+\s*(?:<[^<>()]*>)?\s*\(")) kind = GraphEdgeKind.Calls;
                else kind = GraphEdgeKind.References;
                Edge(n, kind, m.Index);
            }
            foreach (Match m in LowerCall.Matches(code))
            {
                string n = m.Groups["n"].Value;
                if (!functions.ContainsKey(n) || declaredIn[f].Contains(n) || In(skip, m.Index)) continue;
                if (TopFunctionAt(code, m.Index)) continue;
                Edge(n, GraphEdgeKind.Calls, m.Index);
            }
        }
    }

    private static string Spec(SourceFile f, Group g) => f.Text.Substring(g.Index, g.Length);

    private static HashSet<string>? List(string rest, string word)
    {
        var m = Regex.Match(rest, @"\b" + word + @"\s+(?<l>[\w$\s,]+?)(?=\b(?:show|hide|as|deferred)\b|$)");
        if (!m.Success) return null;
        return m.Groups["l"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
    }

    private static SourceFile? ResolveUri(GraphContext cx, SourceFile from, string spec, Dictionary<string, string> packages)
    {
        if (spec.StartsWith("dart:", StringComparison.Ordinal)) return null;
        if (spec.StartsWith("package:", StringComparison.Ordinal))
        {
            string rest = spec[8..];
            int slash = rest.IndexOf('/');
            if (slash < 0 || !packages.TryGetValue(rest[..slash], out var lib)) return null;
            return cx.FileAt(Path.Combine(lib, rest[(slash + 1)..]));
        }
        if (spec.Contains(':')) return null;
        return cx.FileAt(Path.Combine(from.Dir, spec));
    }

    private static bool In(List<(int Start, int End)> spans, int at) => spans.Any(s => at >= s.Start && at < s.End);

    private static bool ClassDeclAt(string code, int at)
    {
        int s = Math.Max(0, at - 24);
        return Regex.IsMatch(code[s..at], @"(?:class|mixin|enum|typedef|extension|type)\s+$");
    }

    private static bool TopFunctionAt(string code, int at)
    {
        int ls = code.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        if (ls < code.Length && code[ls] is ' ' or '\t') return false;
        int le = code.IndexOf('\n', at);
        string line = code[ls..(le < 0 ? code.Length : le)];
        return Regex.IsMatch(line, @"\)\s*(?:async\s*\*?|sync\s*\*)?\s*(?:\{|=>)") && !Regex.IsMatch(code[ls..at], @"[=(]");
    }

    public static int MatchParen(string code, int open) => Match(code, open, '(', ')');
    public static int MatchBrace(string code, int open) => Match(code, open, '{', '}');

    private static int Match(string code, int open, char o, char c)
    {
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == o) depth++;
            else if (code[i] == c && --depth == 0) return i;
        }
        return code.Length;
    }

    // ───────────────────────────── routes ─────────────────────────────

    private static List<Route> CollectRoutes(GraphContext cx, List<SourceFile> files, Dictionary<string, List<DartClass>> classes)
    {
        var routes = new List<Route>();
        var consts = new Dictionary<string, string>(StringComparer.Ordinal);   // "Cls.routeName" → "/path"
        foreach (var f in files)
            foreach (Match m in RouteConst.Matches(f.Masked))
                if (classes.Values.SelectMany(x => x).FirstOrDefault(c => c.File == f && m.Index > c.Start && m.Index < c.End) is { } owner)
                    consts[owner.Name + "." + m.Groups["f"].Value] = Spec(f, m.Groups["p"]);

        string? Key(SourceFile f, Match m) => m.Groups["p"].Success ? Spec(f, m.Groups["p"]) : consts.TryGetValue(m.Groups["c"].Value, out var v) ? v : null;
        // the first project class named in a window of code, and where (offset in the window)
        (string Name, int At)? FirstClass(string window) =>
            UpperName.Matches(window).Where(u => classes.ContainsKey(u.Groups["n"].Value)).Select(u => ((string, int)?)(u.Groups["n"].Value, u.Index)).FirstOrDefault();

        foreach (var f in files)
        {
            string code = f.Masked;
            // routes: { '/settings': (context) => const SettingsPage(), SettingsPage.routeName: (_) => … }
            foreach (Match m in MapRoute.Matches(code))
            {
                string? path = Key(f, m);
                if (path == null || !path.StartsWith('/')) continue;
                int end = Math.Min(code.Length, m.Index + m.Length + 300);
                int comma = NextTopLevel(code, m.Index + m.Length, end);
                if (FirstClass(code[(m.Index + m.Length)..comma]) is { } w) routes.Add(new Route(path, null, w.Name, f, f.Lines.LineOf(m.Index), m.Index + m.Length + w.At));
            }
            // onGenerateRoute: case '/x': return MaterialPageRoute(builder: (_) => X());
            foreach (Match m in CaseRoute.Matches(code))
            {
                string? path = Key(f, m);
                if (path == null || !path.StartsWith('/')) continue;
                int start = m.Index + m.Length;
                int next = Regex.Match(code[start..Math.Min(code.Length, start + 600)], @"(?<![\w$])(?:case|default)\b") is { Success: true } n ? start + n.Index : Math.Min(code.Length, start + 600);
                if (FirstClass(code[start..next]) is { } w) routes.Add(new Route(path, null, w.Name, f, f.Lines.LineOf(m.Index), start + w.At));
            }
            // GoRouter: GoRoute(path: '/x', name: 'x', builder: (c, s) => const X(), routes: [GoRoute(path: 'child', …)])
            var spans = new List<(int Start, int End, string Path, string? Name, string? Widget, int Line, int At)>();
            foreach (Match m in GoRoute.Matches(code))
            {
                int open = m.Index + m.Length - 1, close = MatchParen(code, open);
                string body = code[open..close];
                int nested = GoRoute.Match(body, 1) is { Success: true } nm ? nm.Index : body.Length;
                string own = body[..nested];
                var pm = Regex.Match(own, @"(?<![\w$])path\s*:\s*(?<q>['""])(?<p>[^'""\n]*)\k<q>");
                if (!pm.Success) continue;
                string path = f.Text.Substring(open + pm.Groups["p"].Index, pm.Groups["p"].Length);
                var nmm = Regex.Match(own, @"(?<![\w$])name\s*:\s*(?<q>['""])(?<p>[^'""\n]*)\k<q>");
                string? name = nmm.Success ? f.Text.Substring(open + nmm.Groups["p"].Index, nmm.Groups["p"].Length) : null;
                var bm = Regex.Match(own, @"(?<![\w$])(?:builder|pageBuilder)\s*:");
                var w = bm.Success ? FirstClass(own[(bm.Index + bm.Length)..]) : null;
                spans.Add((open, close, path, name, w?.Name, f.Lines.LineOf(m.Index), w == null ? 0 : open + bm.Index + bm.Length + w.Value.At));
            }
            foreach (var s in spans)
            {
                // a child route's path is relative to its parent's
                string full = s.Path;
                var parent = spans.Where(p => p.Start < s.Start && p.End > s.End).OrderByDescending(p => p.Start).FirstOrDefault();
                for (var p = parent; p != default && !full.StartsWith('/'); p = spans.Where(q => q.Start < p.Start && q.End > p.End).OrderByDescending(q => q.Start).FirstOrDefault())
                    full = p.Path.TrimEnd('/') + "/" + full;
                if (s.Widget != null) routes.Add(new Route(full.StartsWith('/') ? full : "/" + full, s.Name, s.Widget, f, s.Line, s.At));
            }
        }
        return routes;
    }

    private static int NextTopLevel(string code, int from, int end)
    {
        int depth = 0;
        for (int i = from; i < end; i++)
        {
            char c = code[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') { if (--depth < 0) return i; }
            else if (c == ',' && depth == 0) return i;
        }
        return end;
    }

    private static IEnumerable<(string Path, int At, bool IsName)> Navigations(SourceFile f, List<Route> routes)
    {
        string code = f.Masked;
        foreach (Match m in PushNamed.Matches(code))
        {
            if (m.Groups["p"].Success) yield return (Spec(f, m.Groups["p"]), m.Index, false);
            else
            {
                // pushNamed(context, SettingsPage.routeName): the route table keyed by the same constant
                string c = m.Groups["c"].Value;
                var r = routes.FirstOrDefault(x => x.Widget == c.Split('.')[0]);
                if (r != null) yield return (r.Path, m.Index, false);
            }
        }
        foreach (Match m in GoCall.Matches(code))
            yield return (Spec(f, m.Groups["p"]), m.Index, m.Groups["m"].Value.EndsWith("Named", StringComparison.Ordinal));
    }

    /// <summary>A route pattern ('/user/:id') against a path written in a navigation ('/user/42', query dropped).</summary>
    private static bool PathMatches(string pattern, string path)
    {
        path = path.Split('?', '#')[0];
        if (pattern == path) return true;
        var a = pattern.Trim('/').Split('/');
        var b = path.Trim('/').Split('/');
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!a[i].StartsWith(':') && a[i] != b[i] && !(b[i].Length == 0 && a[i].Length == 0)) return false;
        return true;
    }
}
