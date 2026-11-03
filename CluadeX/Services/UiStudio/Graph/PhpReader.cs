using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CluadeX.Services.UiStudio.Graph;

/// <summary>
/// PHP and Laravel. Class names resolve the way PHP does — <c>use</c> aliases (grouped too), then the file's
/// namespace — to the file that declares that class (or where composer's psr-4 puts it). Edges: use (imports),
/// extends / implements / trait use, new, static calls, type hints; routes (<c>Route::get('/x', [C::class, 'm'])</c>,
/// resource, invokable, view and 'C@m' strings) with the URI on the edge and the controller method as second
/// evidence; <c>view('a.b')</c> / Inertia::render to the template; Blade @extends / @include / &lt;x-…&gt; /
/// livewire; container bindings. A class name that does not resolve but is declared exactly once in the project is
/// an inferred edge.
/// </summary>
internal static class PhpReader
{
    private static readonly Regex Namespace = new(@"(?<![\w\\$])namespace\s+(?<ns>[\w\\]+)\s*[;{]", RegexOptions.Compiled);
    private static readonly Regex ClassDecl = new(@"(?<![\w$:>\\])(?:(?:abstract|final|readonly)\s+)*(?<k>class|interface|trait|enum)\s+(?<n>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UseStmt = new(@"(?<![\w$>:\\])use\s+(?<fn>function\s+|const\s+)?(?<l>[\\\w\s,{}]+?)\s*;", RegexOptions.Compiled);
    private static readonly Regex Extends = new(@"(?<![\w$])(?<k>extends|implements)\s+(?<l>[\\\w\s,]+?)(?=\s*(?:\{|implements\b))", RegexOptions.Compiled);
    private static readonly Regex NewExpr = new(@"(?<![\w$])new\s+(?<n>\\?[A-Za-z_][\\\w]*)", RegexOptions.Compiled);
    private static readonly Regex StaticCall = new(@"(?<![\w$\\>])(?<n>\\?[A-Za-z_][\\\w]*)::(?<m>[A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex ClassConst = new(@"(?<![\w$\\>])(?<n>\\?[A-Za-z_][\\\w]*)::class\b", RegexOptions.Compiled);
    private static readonly Regex TypeHint = new(@"(?:[(,|?]\s*|\)\s*:\s*\??)(?<n>\\?[A-Z][\\\w]*)(?=\s*(?:&?\s*(?:\.\.\.)?\$|\{|;|=>|\|))|(?<![\w$])(?:instanceof|catch\s*\()\s*(?<n>\\?[A-Z][\\\w]*)", RegexOptions.Compiled);
    private static readonly Regex ViewCall = new(@"(?<![\w$>])(?<f>view|View::make|View::first|Inertia::render|inertia|->view|markdown)\s*\(\s*(?<q>['""])(?<v>[^'""\n]+)\k<q>", RegexOptions.Compiled);
    private static readonly Regex RouteCall = new(@"(?<![\w$])Route::(?<verb>get|post|put|patch|delete|options|any|match|resource|apiResource|controller|view|redirect|livewire)\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Require = new(@"(?<![\w$])(?:require|include)(?:_once)?\s*\(?\s*__DIR__\s*\.\s*(?<q>['""])(?<p>[^'""\n]+)\k<q>", RegexOptions.Compiled);
    private static readonly Regex Binding = new(@"(?:\$this->app|app\(\)|App::|\$app)\s*(?:->|::)?\s*(?<m>bind|singleton|scoped|instance|bindIf|singletonIf|when|give|needs)\s*\(\s*(?<a>\\?[\w\\]+)::class(?:\s*,\s*(?<b>\\?[\w\\]+)::class)?", RegexOptions.Compiled);
    private static readonly Regex Give = new(@"->(?:needs|give)\s*\(\s*(?<a>\\?[\w\\]+)::class", RegexOptions.Compiled);

    // Blade
    private static readonly Regex BladeView = new(@"@(?<d>extends|include|includeIf|includeWhen|includeUnless|includeFirst|component|each|livewire)\s*\(\s*(?:[^,'""()]*,\s*)?\[?\s*(?<q>['""])(?<v>[^'""\n]+)\k<q>", RegexOptions.Compiled);
    private static readonly Regex BladeTag = new(@"<(?<k>x-|livewire:)(?<c>[\w.\-]+)(?=[\s/>])", RegexOptions.Compiled);

    private static readonly HashSet<string> Special = new(StringComparer.OrdinalIgnoreCase) { "self", "static", "parent", "array", "string", "int", "float", "bool", "callable", "iterable", "object", "mixed", "void", "null", "never", "true", "false", "Closure" };

    private sealed record Decl(string Fqn, SourceFile File, int Line);

    public static void Read(GraphContext cx)
    {
        var php = cx.OfExt(".php").ToList();
        if (php.Count == 0) return;
        var psr4 = LoadPsr4(cx);

        // ── every declared class, by full name (PHP class names ignore case) ──
        var byFqn = new Dictionary<string, Decl>(StringComparer.OrdinalIgnoreCase);
        var bySimple = new Dictionary<string, List<Decl>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in php.Where(f => !f.IsBlade))
        {
            cx.Ct.ThrowIfCancellationRequested();
            cx.Graph.ById[f.Id].Kind = GraphNodeKind.Code;
            string code = f.Masked;
            foreach (Match m in ClassDecl.Matches(code))
            {
                string n = m.Groups["n"].Value;
                if (n is "extends" or "implements") continue;
                string ns = NamespaceAt(code, m.Index);
                var d = new Decl(ns.Length > 0 ? ns + "\\" + n : n, f, f.Lines.LineOf(m.Index));
                byFqn.TryAdd(d.Fqn, d);
                if (!bySimple.TryGetValue(n, out var l)) bySimple[n] = l = new();
                l.Add(d);
            }
        }

        SourceFile? FileOfClass(string fqn)
        {
            if (byFqn.TryGetValue(fqn, out var d)) return d.File;
            foreach (var (prefix, dirs) in psr4.OrderByDescending(p => p.Key.Length))
                if (fqn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    foreach (var dir in dirs)
                        if (cx.FileAt(Path.Combine(dir, fqn[prefix.Length..].Replace('\\', '/') + ".php")) is { } f) return f;
            return null;
        }

        foreach (var f in php)
        {
            cx.Ct.ThrowIfCancellationRequested();
            if (f.IsBlade) { ReadBlade(cx, f, FileOfClass); continue; }
            string code = f.Masked;
            var classSpans = ClassDecl.Matches(code).Select(m => (m.Index, End: code.IndexOf('{', m.Index) is var b and >= 0 ? DartReader.MatchBrace(code, b) : code.Length)).ToList();
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            (SourceFile File, bool Inferred, string? Why)? Resolve(string name, int at)
            {
                if (Special.Contains(name)) return null;
                string fqn;
                if (name.StartsWith('\\')) fqn = name.TrimStart('\\');
                else
                {
                    int slash = name.IndexOf('\\');
                    string head = slash < 0 ? name : name[..slash];
                    fqn = aliases.TryGetValue(head, out var full) ? full + (slash < 0 ? "" : name[slash..])
                        : NamespaceAt(code, at) is { Length: > 0 } ns ? ns + "\\" + name : name;
                }
                if (FileOfClass(fqn) is { } hit) return (hit, false, null);
                string simple = name[(name.LastIndexOf('\\') + 1)..];
                if (bySimple.TryGetValue(simple, out var l) && l.Select(x => x.File).Distinct().Count() == 1)
                    return (l[0].File, true, $"'{name}' resolves to {fqn}, which the project does not declare — matched the only project class named {simple}");
                return null;
            }
            void Edge(string name, int at, GraphEdgeKind kind, string? label = null)
            {
                if (Resolve(name, at) is { } r && r.File != f) cx.Edge(f, r.File, kind, f.Lines.LineOf(at), r.Inferred, r.Why, label);
            }

            // use … ; (imports at the top, traits inside a class)
            var consumed = new HashSet<int>();
            foreach (Match m in UseStmt.Matches(code))
            {
                bool trait = classSpans.Any(s => m.Index > s.Index && m.Index < s.End);
                if (m.Groups["fn"].Success) continue;
                foreach (var (alias, full, offset) in UseList(m))
                {
                    if (trait) { Edge(full, m.Index, GraphEdgeKind.Inherits, "trait"); continue; }
                    aliases[alias] = full.TrimStart('\\');
                    var target = FileOfClass(full.TrimStart('\\'));
                    if (target != null) cx.Edge(f, target, GraphEdgeKind.Imports, f.Lines.LineOf(m.Index));
                    else if (cx.Package(full.TrimStart('\\').Split('\\')[0], "composer") is { } pid) cx.Edge(f, pid, GraphEdgeKind.Imports, f.Lines.LineOf(m.Index));
                }
            }
            foreach (Match m in Extends.Matches(code))
                foreach (Match t in Regex.Matches(m.Groups["l"].Value, @"\\?[A-Za-z_][\\\w]*"))
                    Edge(t.Value, m.Groups["l"].Index + t.Index, m.Groups["k"].Value == "extends" ? GraphEdgeKind.Inherits : GraphEdgeKind.Implements);
            foreach (Match m in NewExpr.Matches(code)) Edge(m.Groups["n"].Value, m.Index, GraphEdgeKind.References, "new");
            foreach (Match m in StaticCall.Matches(code)) Edge(m.Groups["n"].Value, m.Index, GraphEdgeKind.Calls, m.Groups["n"].Value.Split('\\')[^1] + "::" + m.Groups["m"].Value);
            foreach (Match m in TypeHint.Matches(code)) Edge(m.Groups["n"].Value, m.Groups["n"].Index, GraphEdgeKind.References);

            // views and Inertia pages
            foreach (Match m in ViewCall.Matches(code))
            {
                string v = f.Text.Substring(m.Groups["v"].Index, m.Groups["v"].Length);
                var target = m.Groups["f"].Value.StartsWith("Inertia", StringComparison.Ordinal) || m.Groups["f"].Value == "inertia" ? InertiaPage(cx, v) : View(cx, v);
                if (target != null) cx.Edge(f, target, GraphEdgeKind.Renders, f.Lines.LineOf(m.Index), label: v);
            }

            // routes
            foreach (Match m in RouteCall.Matches(code)) ReadRoute(cx, f, code, m, Resolve, FileOfClass);
            foreach (Match m in Require.Matches(code))
                if (cx.FileAt(Path.Combine(f.Dir, f.Text.Substring(m.Groups["p"].Index, m.Groups["p"].Length).TrimStart('/'))) is { } inc)
                    cx.Edge(f, inc, GraphEdgeKind.Imports, f.Lines.LineOf(m.Index), label: "require");

            // the service container
            foreach (Match m in Binding.Matches(code))
            {
                Edge(m.Groups["a"].Value, m.Index, GraphEdgeKind.Registers, m.Groups["m"].Value);
                if (m.Groups["b"].Success) Edge(m.Groups["b"].Value, m.Index, GraphEdgeKind.Registers, m.Groups["m"].Value);
            }
            foreach (Match m in Give.Matches(code)) Edge(m.Groups["a"].Value, m.Index, GraphEdgeKind.Registers, "give");

            // X::class anywhere else (event maps, policies, middleware lists) is a reference
            foreach (Match m in ClassConst.Matches(code)) Edge(m.Groups["n"].Value, m.Index, GraphEdgeKind.References);
        }
    }

    private static string NamespaceAt(string code, int at)
    {
        string ns = "";
        foreach (Match m in Namespace.Matches(code))
        {
            if (m.Index > at) break;
            ns = m.Groups["ns"].Value;
        }
        return ns;
    }

    /// <summary>use A\B; use A\B as C; use A\{B, C as D};</summary>
    private static IEnumerable<(string Alias, string Full, int Offset)> UseList(Match m)
    {
        string list = Regex.Replace(m.Groups["l"].Value, @"\s+", " ").Trim();
        int brace = list.IndexOf('{');
        IEnumerable<string> items;
        string prefix = "";
        if (brace >= 0)
        {
            prefix = list[..brace].Trim().TrimEnd('\\') + "\\";
            items = list[(brace + 1)..].TrimEnd('}', ' ').Split(',');
        }
        else items = list.Split(',');
        foreach (var raw in items)
        {
            string item = raw.Trim();
            if (item.Length == 0) continue;
            var parts = Regex.Split(item, @"\s+as\s+", RegexOptions.IgnoreCase);
            string full = prefix + parts[0].Trim();
            string alias = parts.Length > 1 ? parts[1].Trim() : full[(full.LastIndexOf('\\') + 1)..];
            yield return (alias, full, m.Index);
        }
    }

    private static void ReadRoute(GraphContext cx, SourceFile f, string code, Match m,
        Func<string, int, (SourceFile File, bool Inferred, string? Why)?> resolve, Func<string, SourceFile?> fileOfClass)
    {
        int open = m.Index + m.Length - 1;
        int close = DartReader.MatchParen(code, open);
        string body = code[open..close];
        string verb = m.Groups["verb"].Value.ToLowerInvariant();
        string Str(Group g) => f.Text.Substring(open + g.Index, g.Length);
        var uriM = Regex.Match(body, @"^\(\s*(?<q>['""])(?<u>[^'""\n]*)\k<q>");
        string uri = uriM.Success ? "/" + Str(uriM.Groups["u"]).TrimStart('/') : "";
        int line = f.Lines.LineOf(m.Index);

        void ToController(string cls, string? method, int at, bool inferredByConvention)
        {
            var r = resolve(cls, open + at);
            if (r == null && inferredByConvention && fileOfClass("App\\Http\\Controllers\\" + cls) is { } conv)
                r = (conv, true, $"'{cls}@…' is a string — read in App\\Http\\Controllers, Laravel's default controller namespace");
            if (r is not { } hit || hit.File == f) return;
            string label = (verb is "resource" or "apiresource" ? verb : verb.ToUpperInvariant()) + (uri.Length > 0 ? " " + uri : "") + (method != null ? " → " + method : "");
            var e = cx.Edge(f, hit.File, GraphEdgeKind.Routes, line, hit.Inferred, hit.Why, label);
            // the controller method is the other half of the proof
            if (e != null && method != null && Regex.Match(hit.File.Masked, @"function\s+" + Regex.Escape(method) + @"\s*\(") is { Success: true } fm
                && e.Evidence.Count < GraphContext.MaxEvidence)
                e.Evidence.Add(new GraphEvidence(hit.File.Id, hit.File.Lines.LineOf(fm.Index), hit.File.Lines.Text(hit.File.Lines.LineOf(fm.Index))));
        }

        if (verb == "view")
        {
            var vm = Regex.Match(body, @"^\(\s*(?<q>['""])[^'""\n]*\k<q>\s*,\s*(?<q2>['""])(?<v>[^'""\n]+)\k<q2>");
            if (vm.Success && View(cx, Str(vm.Groups["v"])) is { } blade) cx.Edge(f, blade, GraphEdgeKind.Routes, line, label: "VIEW " + uri);
            return;
        }
        // (string contents are blank in the masked body: match the quotes, read the text from the source)
        var arr = Regex.Match(body, @"\[\s*(?<c>\\?[\w\\]+)::class\s*,\s*(?<q>['""])(?<mth>[^'""\n]+)\k<q>\s*\]");
        if (arr.Success) { ToController(arr.Groups["c"].Value, Str(arr.Groups["mth"]).Trim(), arr.Index, false); return; }
        var str = Regex.Match(body, @",\s*(?<q>['""])(?<s>[^'""\n]+)\k<q>");
        if (str.Success && Regex.Match(Str(str.Groups["s"]), @"^(?<c>[\w\\]+)@(?<mth>\w+)$") is { Success: true } cm)
        { ToController(cm.Groups["c"].Value, cm.Groups["mth"].Value, str.Index, true); return; }
        var inv = Regex.Match(body, @"(?<c>\\?[\w\\]+)::class");
        if (inv.Success) ToController(inv.Groups["c"].Value, verb is "resource" or "apiresource" or "controller" ? null : "__invoke", inv.Index, false);
    }

    /// <summary>'users.index' → resources/views/users/index.blade.php (or .php); 'pkg::x' is a package's.</summary>
    private static SourceFile? View(GraphContext cx, string name)
    {
        if (name.Contains("::", StringComparison.Ordinal) || name.Contains('$')) return null;
        string path = name.Replace('.', '/');
        foreach (var root in ViewRoots(cx))
            if ((cx.FileAt(Path.Combine(root, path + ".blade.php")) ?? cx.FileAt(Path.Combine(root, path + ".php"))) is { } f) return f;
        return null;
    }

    private static IEnumerable<string> ViewRoots(GraphContext cx)
    {
        yield return Path.Combine(cx.FullRoot, "resources", "views");
        // a Laravel app one folder down (backend/, api/)
        foreach (var composer in cx.Configs.Where(c => Path.GetFileName(c.Full) == "composer.json"))
            yield return Path.Combine(composer.Dir, "resources", "views");
    }

    private static SourceFile? InertiaPage(GraphContext cx, string name)
    {
        foreach (var root in ViewRoots(cx))
        {
            string pages = Path.Combine(Path.GetDirectoryName(root)!, "js", "Pages", name);
            foreach (var ext in new[] { ".vue", ".tsx", ".jsx", ".svelte", ".ts", ".js" })
                if (cx.FileAt(pages + ext) is { } f) return f;
        }
        return null;
    }

    private static void ReadBlade(GraphContext cx, SourceFile f, Func<string, SourceFile?> fileOfClass)
    {
        cx.Graph.ById[f.Id].Kind = f.Id.Contains("/components/", StringComparison.OrdinalIgnoreCase) ? GraphNodeKind.Component : GraphNodeKind.Page;
        string code = f.Masked;
        foreach (Match m in BladeView.Matches(code))
        {
            string v = m.Groups["v"].Value, d = m.Groups["d"].Value;
            int line = f.Lines.LineOf(m.Index);
            if (d == "livewire") { if (Livewire(cx, v, fileOfClass) is { } lw) cx.Edge(f, lw, GraphEdgeKind.Renders, line, label: v); continue; }
            if (View(cx, v) is { } target) cx.Edge(f, target, d == "extends" ? GraphEdgeKind.Inherits : GraphEdgeKind.Renders, line, label: "@" + d);
        }
        foreach (Match m in BladeTag.Matches(code))
        {
            string c = m.Groups["c"].Value;
            int line = f.Lines.LineOf(m.Index);
            if (m.Groups["k"].Value == "livewire:") { if (Livewire(cx, c, fileOfClass) is { } lw) cx.Edge(f, lw, GraphEdgeKind.Renders, line, label: "livewire"); continue; }
            if (c is "slot" or "dynamic-component" || c.Contains("::", StringComparison.Ordinal)) continue;
            // a class component (App\View\Components\Forms\Input) renders its own view; an anonymous one is just the view
            string cls = "App\\View\\Components\\" + string.Join("\\", c.Split('.').Select(Studly));
            var target = fileOfClass(cls) ?? View(cx, "components." + c) ?? View(cx, "components." + c + ".index");
            if (target != null) cx.Edge(f, target, GraphEdgeKind.Renders, line, label: "<x-" + c + ">");
        }
    }

    private static SourceFile? Livewire(GraphContext cx, string name, Func<string, SourceFile?> fileOfClass)
    {
        string cls = string.Join("\\", name.Split('.').Select(Studly));
        return fileOfClass("App\\Livewire\\" + cls) ?? fileOfClass("App\\Http\\Livewire\\" + cls);
    }

    private static string Studly(string kebab) => string.Concat(kebab.Split('-', '_').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    private static Dictionary<string, List<string>> LoadPsr4(GraphContext cx)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cx.Configs.Where(c => Path.GetFileName(c.Full) == "composer.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(c.Text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                foreach (var section in new[] { "autoload", "autoload-dev" })
                    if (doc.RootElement.TryGetProperty(section, out var a) && a.TryGetProperty("psr-4", out var p) && p.ValueKind == JsonValueKind.Object)
                        foreach (var prop in p.EnumerateObject())
                        {
                            var dirs = prop.Value.ValueKind == JsonValueKind.Array ? prop.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                                : new List<string> { prop.Value.GetString() ?? "" };
                            if (!map.TryGetValue(prop.Name, out var list)) map[prop.Name] = list = new();
                            list.AddRange(dirs.Select(d => Path.GetFullPath(Path.Combine(c.Dir, d))));
                        }
            }
            catch (JsonException) { }
        }
        return map;
    }
}
