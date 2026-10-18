using System.IO;
using System.Text.RegularExpressions;

namespace CluadeX.Services.UiStudio.Pages;

/// <summary>
/// The pages of a Laravel site: every GET route in <c>routes/web.php</c> (and the route files it requires or the app
/// registers with a prefix), with its group prefix, name and middleware, and the Blade view or Inertia page that
/// renders it — found from <c>Route::view</c>, the closure, or the controller method's <c>view('…')</c>.
/// POST/PUT/DELETE routes are actions, not pages, and are never listed; API route files are skipped.
/// </summary>
public static class LaravelPages
{
    public static bool IsLaravel(string root) =>
        File.Exists(Path.Combine(root, "artisan")) && File.Exists(Path.Combine(root, "composer.json"))
        && WebPages.SafeRead(Path.Combine(root, "composer.json"), 200_000).Contains("laravel/framework", StringComparison.Ordinal);

    /// <summary>Where the site most likely runs: APP_URL from .env — only that line is read, nothing else in the
    /// file (it holds secrets) is kept — or, when that is Laravel's bare default (http://localhost) and Laragon is
    /// installed, Laragon's automatic address for the folder (http://&lt;folder&gt;.test).</summary>
    public static string? AppUrl(string root, string? laragonDir = @"C:\laragon")
    {
        string? url = ReadAppUrl(root);
        if (url is null or "http://localhost/" && laragonDir != null && Directory.Exists(laragonDir))
        {
            string folder = Path.GetFileName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)).ToLowerInvariant();
            if (Regex.IsMatch(folder, @"^[a-z0-9][a-z0-9\-]*$")) return "http://" + folder + ".test/";
        }
        return url;
    }

    private static string? ReadAppUrl(string root)
    {
        foreach (var name in new[] { ".env", ".env.example" })
        {
            string path = Path.Combine(root, name);
            if (!File.Exists(path)) continue;
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    var m = Regex.Match(line, @"^\s*APP_URL\s*=\s*['""]?(?<u>[^'""#\s]+)");
                    if (m.Success && Uri.TryCreate(m.Groups["u"].Value, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
                        return u.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
                }
            }
            catch { /* unreadable: no suggestion */ }
        }
        return null;
    }

    private sealed class Ctx
    {
        public string Prefix = "";
        public string NamePrefix = "";
        public List<string> Middleware = new();
        public string? Controller;
        public Ctx Clone() => new() { Prefix = Prefix, NamePrefix = NamePrefix, Middleware = new List<string>(Middleware), Controller = Controller };
    }

    private sealed record Call(string Name, int Open, int Close);

    private static readonly HashSet<string> Verbs = new(StringComparer.Ordinal) { "get", "view", "match", "any", "resource", "inertia", "livewire" };

    public static List<StudioPage> Discover(string root, CancellationToken ct = default)
    {
        var pages = new List<StudioPage>();
        string routes = Path.Combine(root, "routes");
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var controllers = new Lazy<Dictionary<string, string>>(() => ControllerFiles(root));

        void ParseFile(string file, Ctx ctx, int depth)
        {
            if (depth > 4 || !done.Add(Path.GetFullPath(file)) || !File.Exists(file)) return;
            if (Path.GetFileName(file).Equals("api.php", StringComparison.OrdinalIgnoreCase)) return;
            ct.ThrowIfCancellationRequested();
            string text = WebPages.SafeRead(file, 600_000);
            if (text.Length == 0) return;
            var map = new JsCodeMap(text, php: true);
            var groups = new List<(int Start, int End, Ctx Ctx)>();

            // Statements in source order: groups first come before their routes, so a route sees every group around it.
            foreach (var (start, chain) in Chains(map))
            {
                var outer = groups.Where(g => g.Start < start && start < g.End).OrderBy(g => g.Start).LastOrDefault().Ctx ?? ctx;
                var groupCall = chain.FirstOrDefault(c => c.Name == "group");
                if (groupCall != null)
                {
                    var g = outer.Clone();
                    foreach (var c in chain.TakeWhile(c => c != groupCall)) ApplyAttr(map, c, g);
                    string args = Args(map, groupCall);
                    // Route::group(['prefix' => 'x', 'middleware' => [...]], function () {...})
                    if (args.TrimStart().StartsWith('[')) ApplyArrayAttrs(args, g);
                    int body = ClosureBody(map, groupCall);
                    if (body >= 0) groups.Add((body, map.CloseOf(body), g));
                    else if (Regex.Match(args, @"base_path\(\s*['""](?<f>routes/[^'""]+\.php)['""]") is { Success: true } bp)
                        ParseFile(Path.Combine(root, bp.Groups["f"].Value.Replace('/', Path.DirectorySeparatorChar)), g, depth + 1);
                    else if (Regex.Match(args, @"__DIR__\s*\.\s*['""]/(?<f>[^'""]+\.php)['""]") is { Success: true } dp)
                        ParseFile(Path.Combine(Path.GetDirectoryName(file)!, dp.Groups["f"].Value), g, depth + 1);
                    continue;
                }
                var verb = chain[0];
                if (!Verbs.Contains(verb.Name)) continue;
                var own = outer.Clone();
                string? name = null;
                foreach (var c in chain.Skip(1))
                {
                    if (c.Name == "name") name = FirstString(Args(map, c));
                    else if (c.Name == "middleware") own.Middleware.AddRange(Strings(Args(map, c)));
                }
                foreach (var p in RoutePages(map, chain, own, name, file, root, controllers)) pages.Add(p);
            }

            // require __DIR__.'/auth.php';  (top-level or inside a group)
            foreach (Match m in Regex.Matches(text, @"(?:require|include)(?:_once)?\s*\(?\s*__DIR__\s*\.\s*['""]/(?<f>[^'""]+\.php)['""]"))
            {
                if (!map.IsCode[m.Index]) continue;
                var inGroup = groups.Where(g => g.Start < m.Index && m.Index < g.End).OrderBy(g => g.Start).LastOrDefault().Ctx ?? ctx;
                ParseFile(Path.Combine(Path.GetDirectoryName(file)!, m.Groups["f"].Value), inGroup, depth + 1);
            }
        }

        ParseFile(Path.Combine(routes, "web.php"), new Ctx(), 0);
        // Route files the app registers itself (Laravel ≤10 RouteServiceProvider, 11+ bootstrap/app.php "then:").
        foreach (var registrar in new[] { Path.Combine(root, "app", "Providers", "RouteServiceProvider.php"), Path.Combine(root, "bootstrap", "app.php") })
        {
            string text = WebPages.SafeRead(registrar, 300_000);
            if (text.Length == 0) continue;
            var map = new JsCodeMap(text, php: true);
            foreach (var (_, chain) in Chains(map))
            {
                var groupCall = chain.FirstOrDefault(c => c.Name == "group");
                if (groupCall == null) continue;
                var g = new Ctx();
                foreach (var c in chain.TakeWhile(c => c != groupCall)) ApplyAttr(map, c, g);
                if (g.Prefix.Trim('/').StartsWith("api", StringComparison.OrdinalIgnoreCase) || g.Middleware.Contains("api")) continue;
                if (Regex.Match(Args(map, groupCall), @"base_path\(\s*['""](?<f>routes/[^'""]+\.php)['""]") is { Success: true } bp)
                    ParseFile(Path.Combine(root, bp.Groups["f"].Value.Replace('/', Path.DirectorySeparatorChar)), g, 1);
            }
        }
        return WebPages.Dedupe(pages);
    }

    /// <summary>Every <c>Route::a(...)->b(...)->c(...)</c> chain in the file: its start and its calls.</summary>
    private static IEnumerable<(int Start, List<Call> Chain)> Chains(JsCodeMap map)
    {
        string t = map.Text;
        foreach (Match m in Regex.Matches(t, @"(?<![\w$\\])(?:\\?Illuminate\\Support\\Facades\\)?(?:Route|Volt)::(?<n>\w+)\s*\("))
        {
            if (!map.IsCode[m.Index]) continue;
            var chain = new List<Call>();
            int open = m.Index + m.Length - 1;
            string first = m.Groups["n"].Value;
            if (t.AsSpan(m.Index).StartsWith("Volt::", StringComparison.Ordinal) && first == "route") first = "get";
            chain.Add(new Call(first, open, map.CloseOf(open)));
            int k = map.CloseOf(open) + 1;
            while (true)
            {
                var next = Regex.Match(t[k..Math.Min(t.Length, k + 200)], @"^\s*->\s*(?<n>\w+)\s*\(");
                if (!next.Success) break;
                int o = k + next.Length - 1;
                chain.Add(new Call(next.Groups["n"].Value, o, map.CloseOf(o)));
                k = map.CloseOf(o) + 1;
                if (k >= t.Length) break;
            }
            yield return (m.Index, chain);
        }
    }

    private static string Args(JsCodeMap map, Call c) => c.Close > c.Open ? map.Text[(c.Open + 1)..c.Close] : "";

    private static void ApplyAttr(JsCodeMap map, Call c, Ctx g)
    {
        string a = Args(map, c);
        switch (c.Name)
        {
            case "prefix": if (FirstString(a) is { } p) g.Prefix = WebPages.JoinRoute(g.Prefix, p.Trim('/')); break;
            case "name" or "as": if (FirstString(a) is { } n) g.NamePrefix += n; break;
            case "middleware": g.Middleware.AddRange(Strings(a)); break;
            case "controller": if (Regex.Match(a, @"(?<c>[A-Za-z_\\]+)::class") is { Success: true } cm) g.Controller = cm.Groups["c"].Value.Split('\\')[^1]; break;
        }
    }

    private static void ApplyArrayAttrs(string args, Ctx g)
    {
        var p = Regex.Match(args, @"['""]prefix['""]\s*=>\s*['""](?<v>[^'""]*)['""]");
        if (p.Success) g.Prefix = WebPages.JoinRoute(g.Prefix, p.Groups["v"].Value.Trim('/'));
        var n = Regex.Match(args, @"['""]as['""]\s*=>\s*['""](?<v>[^'""]*)['""]");
        if (n.Success) g.NamePrefix += n.Groups["v"].Value;
        var mw = Regex.Match(args, @"['""]middleware['""]\s*=>\s*(?<v>\[[^\]]*\]|['""][^'""]*['""])");
        if (mw.Success) g.Middleware.AddRange(Strings(mw.Groups["v"].Value));
    }

    /// <summary>The '{' of the closure passed to group(), or -1.</summary>
    private static int ClosureBody(JsCodeMap map, Call c)
    {
        string a = Args(map, c);
        var m = Regex.Match(a, @"function\s*\([^)]*\)\s*(?:use\s*\([^)]*\)\s*)?(?::\s*\??\w+\s*)?\{");
        return m.Success ? c.Open + 1 + m.Index + m.Length - 1 : -1;
    }

    private static string? FirstString(string args)
    {
        var m = Regex.Match(args, @"^\s*(['""])(?<v>[^'""]*)\1");
        return m.Success ? m.Groups["v"].Value : null;
    }

    private static IEnumerable<string> Strings(string args) =>
        Regex.Matches(args, @"['""](?<v>[^'""]+)['""]").Select(m => m.Groups["v"].Value);

    private static readonly Regex ViewCall = new(@"(?:view|View::make|response\(\)\s*->\s*view)\(\s*['""](?<v>[\w.\-/:]+)['""]|Inertia::render\(\s*['""](?<i>[\w./\-]+)['""]|inertia\(\s*['""](?<i>[\w./\-]+)['""]", RegexOptions.Compiled);

    private static IEnumerable<StudioPage> RoutePages(JsCodeMap map, List<Call> chain, Ctx ctx, string? name, string file, string root, Lazy<Dictionary<string, string>> controllers)
    {
        var verb = chain[0];
        string args = Args(map, verb);
        if (verb.Name == "match" && !Regex.IsMatch(args, @"^\s*\[[^\]]*['""](?:get|GET)['""]")) yield break;
        string afterMethods = verb.Name == "match" ? Regex.Replace(args, @"^\s*\[[^\]]*\]\s*,", "") : args;
        string? uri = FirstString(afterMethods);
        if (uri == null) yield break;
        var (line, _) = SourceTagEditor.LineColOf(map.Text, verb.Open);
        string fullName = name != null ? ctx.NamePrefix + name : "";

        if (verb.Name == "resource")
        {
            var only = chain.FirstOrDefault(c => c.Name == "only") is { } o ? Strings(Args(map, o)).ToHashSet() : null;
            var except = chain.FirstOrDefault(c => c.Name == "except") is { } x ? Strings(Args(map, x)).ToHashSet() : null;
            string resource = uri.Trim('/');
            string param = Singular(resource.Split('/')[^1].Split('.')[^1]);
            string? ctrl = Regex.Match(args, @"(?<c>[A-Za-z_\\]+)::class") is { Success: true } cm ? cm.Groups["c"].Value.Split('\\')[^1] : null;
            foreach (var (suffix, action) in new[] { ("", "index"), ("/create", "create"), ("/{" + param + "}", "show"), ("/{" + param + "}/edit", "edit") })
            {
                if (only != null && !only.Contains(action) || except != null && except.Contains(action)) continue;
                var (rv, rKnown) = ViewOf(root, ctrl, action, controllers);
                if (rKnown && rv == null) continue;   // the action answers with JSON or a redirect: not a page
                yield return Page(root, resource.Replace('.', '/') + suffix, ctx, (ctx.NamePrefix + resource + "." + action).Trim('.'), rv, file, line);
            }
            yield break;
        }

        string? view = null;
        bool known = false;   // the code that answers was found — so "no view in it" means it isn't a page
        if (verb.Name == "view" || verb.Name == "inertia" || verb.Name == "livewire")
        {
            var rest = Regex.Match(afterMethods, @"^\s*(['""])[^'""]*\1\s*,\s*(['""])(?<v>[^'""]+)\2");
            if (rest.Success) view = verb.Name == "inertia" ? "inertia:" + rest.Groups["v"].Value : rest.Groups["v"].Value;
        }
        else
        {
            // closure: function () { return view('x'); } / fn () => view('x')
            var direct = ViewCall.Match(afterMethods);
            if (direct.Success) view = direct.Groups["v"].Success ? direct.Groups["v"].Value : "inertia:" + direct.Groups["i"].Value;
            else if (Regex.IsMatch(afterMethods, @"(?<![\w$])(?:function|fn)\s*\("))
                known = true;   // a closure that renders no view: JSON, a redirect, a file
            else
            {
                var ca = Regex.Match(afterMethods, @"\[\s*(?<c>[A-Za-z_\\]+)::class\s*,\s*['""](?<m>\w+)['""]\s*\]");
                var inv = Regex.Match(afterMethods, @"^\s*(['""])[^'""]*\1\s*,\s*(?<c>[A-Za-z_\\]+)::class\s*\)?\s*$");
                var at = Regex.Match(afterMethods, @"['""](?<c>[A-Za-z_\\]+)@(?<m>\w+)['""]");
                var groupMethod = Regex.Match(afterMethods, @"^\s*(['""])[^'""]*\1\s*,\s*(['""])(?<m>\w+)\2\s*$");
                if (ca.Success) (view, known) = ViewOf(root, ca.Groups["c"].Value.Split('\\')[^1], ca.Groups["m"].Value, controllers);
                else if (inv.Success) (view, known) = ViewOf(root, inv.Groups["c"].Value.Split('\\')[^1], "__invoke", controllers);
                else if (at.Success) (view, known) = ViewOf(root, at.Groups["c"].Value.Split('\\')[^1], at.Groups["m"].Value, controllers);
                else if (groupMethod.Success && ctx.Controller != null) (view, known) = ViewOf(root, ctx.Controller, groupMethod.Groups["m"].Value, controllers);
            }
        }
        if (known && view == null) yield break;   // JSON, a redirect, a download: not a page
        yield return Page(root, uri, ctx, fullName, view, file, line);
    }

    private static StudioPage Page(string root, string uri, Ctx ctx, string name, string? view, string routeFile, int line)
    {
        string path = WebPages.JoinRoute(ctx.Prefix.Length > 0 ? "/" + ctx.Prefix.Trim('/') : "", uri.Trim().Trim('/'));
        var parameters = new List<string>();
        var segs = new List<string>();
        foreach (var s in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(s, @"^\{(?<n>\w+)(?<opt>\?)?\}$");
            if (m.Success)
            {
                if (m.Groups["opt"].Success) continue;   // {page?}: renders without it
                parameters.Add(m.Groups["n"].Value);
                segs.Add(":" + m.Groups["n"].Value);
                continue;
            }
            // a parameter inside a segment: "preview/{level}.png"
            var inner = Regex.Matches(s, @"\{(?<n>\w+)\??\}");
            foreach (Match im in inner) parameters.Add(im.Groups["n"].Value);
            segs.Add(inner.Count > 0 ? Regex.Replace(s, @"\{(?<n>\w+)\??\}", ":${n}") : s);
        }
        string route = "/" + string.Join('/', segs);
        string? viewFile = ViewFile(root, view);
        string? title = viewFile != null ? BladeTitle(WebPages.SafeRead(viewFile, 300_000)) : null;
        bool login = ctx.Middleware.Any(mw => Regex.IsMatch(mw, @"^(auth|verified|admin|role|can|permission|password\.confirm)\b", RegexOptions.IgnoreCase));
        return new StudioPage
        {
            Id = "route:" + route.ToLowerInvariant(),
            Kind = StudioPageKind.Route,
            Route = route,
            Title = title ?? (name.Length > 0 ? WebPages.Humanize(name.Split('.')[^1] is "index" && name.Contains('.') ? name.Split('.')[^2] : name.Split('.')[^1]) : WebPages.RouteTitle(route)),
            SourcePath = viewFile ?? routeFile,
            SourceLine = viewFile != null ? 1 : line,
            Detail = view?.Replace("inertia:", "") ?? (name.Length > 0 ? name : null),
            Params = parameters,
            Risky = PageSafety.IsRisky(route, name),
            NeedsLogin = login,
        };
    }

    private static string Singular(string word) =>
        word.EndsWith("ies", StringComparison.Ordinal) ? word[..^3] + "y"
        : word.EndsWith("ses", StringComparison.Ordinal) ? word[..^2]
        : word.EndsWith('s') ? word[..^1] : word;

    /// <summary>Controller class name → its file under app/Http/Controllers (first one wins).</summary>
    private static Dictionary<string, string> ControllerFiles(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string dir = Path.Combine(root, "app", "Http", "Controllers");
        if (!Directory.Exists(dir)) return map;
        foreach (var f in SourceSearch.ProjectFiles(dir, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".php" }, 3000))
            map.TryAdd(Path.GetFileNameWithoutExtension(f), f);
        return map;
    }

    /// <summary>The view a controller action renders: the first view('…') / Inertia::render('…') in its body.
    /// Known = the method's body was read (so a null view means the action is not a page).</summary>
    private static (string? View, bool Known) ViewOf(string root, string? controller, string method, Lazy<Dictionary<string, string>> controllers)
    {
        if (controller == null || !controllers.Value.TryGetValue(controller, out var file)) return (null, false);
        string text = WebPages.SafeRead(file, 600_000);
        var m = Regex.Match(text, @"function\s+" + Regex.Escape(method) + @"\s*\(");
        if (!m.Success) return (null, false);
        var map = new JsCodeMap(text, php: true);
        int paren = m.Index + m.Length - 1;
        int afterParams = Math.Min(map.CloseOf(paren), text.Length - 1);
        int brace = text.IndexOf('{', afterParams);
        if (brace < 0) return (null, false);
        int end = Math.Min(map.CloseOf(brace), text.Length);
        string body = text[brace..end];
        var v = ViewCall.Match(body);
        if (v.Success) return (v.Groups["v"].Success ? v.Groups["v"].Value : "inertia:" + v.Groups["i"].Value, true);
        // the view comes from somewhere the scan can't follow ($this->render(…), a variable): keep it as a page
        bool other = Regex.IsMatch(body, @"view\(|->render\(|Inertia::|inertia\(|Livewire|->make\(|return\s+\$this->\w+\(");
        return (null, !other);
    }

    /// <summary>"pages.about" → resources/views/pages/about.blade.php; "inertia:Users/Index" → resources/js/Pages/Users/Index.vue.</summary>
    internal static string? ViewFile(string root, string? view)
    {
        if (string.IsNullOrEmpty(view) || view.Contains("::", StringComparison.Ordinal)) return null;
        if (view.StartsWith("inertia:", StringComparison.Ordinal))
        {
            string page = view["inertia:".Length..].Replace('/', Path.DirectorySeparatorChar);
            foreach (var dir in new[] { "Pages", "pages" })
                foreach (var ext in new[] { ".vue", ".jsx", ".tsx", ".svelte", ".js", ".ts" })
                {
                    string f = Path.Combine(root, "resources", "js", dir, page + ext);
                    if (File.Exists(f)) return f;
                }
            return null;
        }
        string blade = Path.Combine(root, "resources", "views", view.Replace('.', Path.DirectorySeparatorChar) + ".blade.php");
        if (File.Exists(blade)) return blade;
        string index = Path.Combine(root, "resources", "views", view.Replace('.', Path.DirectorySeparatorChar), "index.blade.php");
        return File.Exists(index) ? index : null;
    }

    /// <summary>A Blade view's title: @section('title', '…'), a title="…" passed to its layout, &lt;title&gt; or its first &lt;h1&gt;.</summary>
    internal static string? BladeTitle(string text)
    {
        var m = Regex.Match(text, @"@section\(\s*['""]title['""]\s*,\s*['""](?<t>[^'""]+)['""]");
        if (m.Success) return m.Groups["t"].Value.Trim();
        m = Regex.Match(text, @"@section\(\s*['""]title['""]\s*\)\s*(?<t>[^@<{]+?)\s*@(?:endsection|stop)");
        if (m.Success) return m.Groups["t"].Value.Trim();
        m = Regex.Match(text, @"<x-[\w.\-:]+[^>]*\stitle=['""](?<t>[^'""{]+)['""]");
        if (m.Success) return m.Groups["t"].Value.Trim();
        string noBlade = Regex.Replace(text, @"\{\{.*?\}\}|\{!!.*?!!\}|@\w+(?:\s*\((?:[^()]|\([^()]*\))*\))?", "");
        return WebPages.HtmlTitle(noBlade);
    }
}
