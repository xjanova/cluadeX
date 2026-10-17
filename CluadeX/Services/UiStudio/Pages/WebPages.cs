using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace CluadeX.Services.UiStudio.Pages;

/// <summary>
/// Finds the pages of a web project the way its framework does: every HTML file of a static site, the folders of
/// a file-based router (Next, Nuxt, SvelteKit, Astro), and the route tables written in code (React Router's
/// <c>&lt;Route&gt;</c> and route objects, Vue Router, Angular). A page exists only where the project says so.
/// </summary>
public static class WebPages
{
    // ───────────────────────────── static sites ─────────────────────────────

    private static readonly HashSet<string> HtmlExt = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm" };

    /// <summary>Every HTML file of a static site: route = its path from the root ("/" for index.html).</summary>
    public static List<StudioPage> StaticHtml(string root, string? entryHtml, int max = 400)
    {
        string fullRoot = FullRoot(root);
        var files = SourceSearch.ProjectFiles(root, HtmlExt, max).ToList();
        // The entry may sit in a folder the scan skips ("dist/index.html"): that folder is the site then.
        if (entryHtml != null)
        {
            string entryFull = Path.GetFullPath(Path.Combine(root, entryHtml));
            if (File.Exists(entryFull) && !files.Contains(entryFull, StringComparer.OrdinalIgnoreCase))
            {
                string dir = Path.GetDirectoryName(entryFull)!;
                files.AddRange(Directory.EnumerateFiles(dir, "*.htm*", SearchOption.TopDirectoryOnly).Where(f => HtmlExt.Contains(Path.GetExtension(f))));
            }
        }
        var pages = new List<StudioPage>();
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string rel = Rel(fullRoot, file);
            string route = "/" + rel;
            if (Path.GetFileName(rel).Equals("index.html", StringComparison.OrdinalIgnoreCase))
                route = "/" + rel[..^"index.html".Length];
            string text = SafeRead(file, 400_000);
            pages.Add(new StudioPage
            {
                Id = "html:" + rel.ToLowerInvariant(),
                Kind = StudioPageKind.Html,
                Route = route,
                Title = HtmlTitle(text) ?? Humanize(Path.GetFileNameWithoutExtension(file)),
                SourcePath = file,
                SourceLine = 1,
                Detail = rel,
                Risky = PageSafety.IsRisky(route, null),
            });
        }
        string? entryRoute = entryHtml == null ? null : "/" + entryHtml.Replace('\\', '/');
        return pages
            .OrderByDescending(p => p.Route == "/" || entryRoute != null && (p.Route == entryRoute || p.Route + "index.html" == entryRoute))
            .ThenBy(p => p.Route!.Count(c => c == '/'))
            .ThenBy(p => p.Route, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The page's own name: its &lt;title&gt;, else its first &lt;h1&gt;.</summary>
    public static string? HtmlTitle(string html)
    {
        var m = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        string? t = m.Success ? Clean(m.Groups[1].Value) : null;
        if (!string.IsNullOrEmpty(t) && !t.Contains("{{", StringComparison.Ordinal)) return t;
        m = Regex.Match(html, @"<h1[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        t = m.Success ? Clean(Regex.Replace(m.Groups[1].Value, "<[^>]+>", " ")) : null;
        return string.IsNullOrEmpty(t) ? null : t;
    }

    private static string Clean(string s)
    {
        s = WebUtility.HtmlDecode(Regex.Replace(s, @"\s+", " ")).Trim();
        return s.Length > 80 ? s[..80] + "…" : s;
    }

    // ───────────────────────────── file-based routers ─────────────────────────────

    public enum FileRouter { Next, Nuxt, SvelteKit, Astro }

    private static readonly Regex DynSeg = new(@"^\[\[?(?:\.\.\.)?(?<n>[^\]=]+)(?:=[^\]]+)?\]\]?$", RegexOptions.Compiled);

    /// <summary>The routes of a file-based router. Each folder/file is a URL segment; "(group)" folders vanish;
    /// "[id]" becomes a parameter; private ("_x"), parallel ("@slot") and API files are not pages.</summary>
    public static List<StudioPage> FileRoutes(string root, FileRouter router)
    {
        var pages = new List<StudioPage>();
        string fullRoot = FullRoot(root);
        foreach (var (dir, pagesRouter) in RouterDirs(root, router))
        {
            if (!Directory.Exists(dir)) continue;
            string dirFull = FullRoot(dir);
            foreach (var file in SourceSearch.ProjectFiles(dir, null, 2000))
            {
                string relInDir = Rel(dirFull, file);
                var route = RouteOfFile(relInDir, router, pagesRouter, out var parameters, out bool optionalOnly);
                if (route == null) continue;
                string text = SafeRead(file, 300_000);
                string rel = Rel(fullRoot, file);
                pages.Add(new StudioPage
                {
                    Id = "route:" + route.ToLowerInvariant(),
                    Kind = StudioPageKind.Route,
                    Route = route,
                    Title = SourceTitle(text) ?? RouteTitle(route),
                    SourcePath = file,
                    SourceLine = 1,
                    Detail = rel,
                    Params = parameters,
                    Risky = PageSafety.IsRisky(route, null),
                });
            }
        }
        return Dedupe(pages);
    }

    private static IEnumerable<(string Dir, bool PagesRouter)> RouterDirs(string root, FileRouter router) => router switch
    {
        FileRouter.Next => new[]
        {
            (Path.Combine(root, "app"), false), (Path.Combine(root, "src", "app"), false),
            (Path.Combine(root, "pages"), true), (Path.Combine(root, "src", "pages"), true),
        },
        FileRouter.Nuxt => new[] { (Path.Combine(root, "pages"), true), (Path.Combine(root, "app", "pages"), true) },
        FileRouter.SvelteKit => new[] { (Path.Combine(root, "src", "routes"), false) },
        FileRouter.Astro => new[] { (Path.Combine(root, "src", "pages"), true) },
        _ => Array.Empty<(string, bool)>(),
    };

    /// <summary>"blog/[slug]/page.tsx" → "/blog/:slug" (null when the file is not a page).</summary>
    internal static string? RouteOfFile(string relInDir, FileRouter router, bool pagesRouter, out List<string> parameters, out bool optionalOnly)
    {
        parameters = new List<string>();
        optionalOnly = true;
        string ext = Path.GetExtension(relInDir).ToLowerInvariant();
        var segs = relInDir.Split('/').ToList();
        string file = Path.GetFileNameWithoutExtension(segs[^1]);
        segs.RemoveAt(segs.Count - 1);

        switch (router)
        {
            case FileRouter.Next when !pagesRouter:
                if (file != "page" || ext is not (".tsx" or ".ts" or ".jsx" or ".js" or ".mdx" or ".md")) return null;
                break;
            case FileRouter.Next:
                if (ext is not (".tsx" or ".ts" or ".jsx" or ".js" or ".mdx" or ".md")) return null;
                if (file.StartsWith('_') || segs.Count > 0 && segs[0] == "api") return null;
                if (file != "index") segs.Add(file);
                break;
            case FileRouter.Nuxt:
                if (ext != ".vue") return null;
                if (file != "index") segs.Add(file);
                break;
            case FileRouter.SvelteKit:
                if (file != "+page" || ext != ".svelte") return null;
                break;
            case FileRouter.Astro:
                if (ext is not (".astro" or ".md" or ".mdx" or ".html")) return null;
                if (file.StartsWith('_')) return null;
                if (file != "index") segs.Add(file);
                break;
        }

        var outSegs = new List<string>();
        foreach (var raw in segs)
        {
            string s = raw;
            if (s.Length == 0) continue;
            if (s.StartsWith('(') && s.EndsWith(')'))
            {
                if (s.StartsWith("(.", StringComparison.Ordinal)) return null;   // Next intercepting route
                continue;                                                          // route group: no URL segment
            }
            if (s.StartsWith('@')) return null;                                   // parallel route slot
            if (s.StartsWith('_') && router is not FileRouter.Nuxt) return null;  // private folder
            var m = DynSeg.Match(s);
            if (m.Success)
            {
                bool optional = s.StartsWith("[[", StringComparison.Ordinal);
                if (optional) continue;                                            // [[optional]]: renders without it
                parameters.Add(m.Groups["n"].Value);
                optionalOnly = false;
                outSegs.Add(":" + m.Groups["n"].Value);
                continue;
            }
            if (router == FileRouter.Nuxt && s.StartsWith('_') && s.Length > 1)   // Nuxt 2: _id.vue
            {
                parameters.Add(s[1..]);
                outSegs.Add(":" + s[1..]);
                continue;
            }
            outSegs.Add(s);
        }
        return "/" + string.Join('/', outSegs);
    }

    // ───────────────────────────── routes written in code ─────────────────────────────

    private static readonly HashSet<string> CodeExt = new(StringComparer.OrdinalIgnoreCase) { ".js", ".jsx", ".ts", ".tsx", ".mjs" };
    private static readonly Regex RouterHint = new(@"createBrowserRouter|createHashRouter|createMemoryRouter|useRoutes|<Route\b|createRouter|RouterModule|Routes\s*=|routes\s*[:=]|provideRouter", RegexOptions.Compiled);
    private static readonly Regex PathKey = new(@"(?<![\w$.])path\s*:", RegexOptions.Compiled);
    private static readonly Regex IndexKey = new(@"(?<![\w$.])index\s*:\s*true", RegexOptions.Compiled);
    private static readonly Regex RouterKeys = new(@"(?<![\w$.])(?:component|Component|element|lazy|loadComponent|loadChildren|children|redirect|redirectTo|name|meta|index|loader|errorElement|beforeEnter|title|canActivate)\s*:", RegexOptions.Compiled);
    private static readonly Regex HashMode = new(@"createHashRouter|<HashRouter|createWebHashHistory|HashLocationStrategy|withHashLocation|useHash\s*:\s*true", RegexOptions.Compiled);

    /// <summary>Routes defined in code: React Router (JSX and objects), Vue Router, Angular.</summary>
    public static List<StudioPage> CodeRoutes(string root, out bool hashMode, int maxFiles = 1500)
    {
        hashMode = false;
        var pages = new List<StudioPage>();
        string fullRoot = FullRoot(root);
        var resolver = new ModuleResolver(root);
        foreach (var file in SourceSearch.ProjectFiles(root, CodeExt, maxFiles))
        {
            string text = SafeRead(file, 400_000);
            if (text.Length == 0 || !RouterHint.IsMatch(text)) continue;
            if (HashMode.IsMatch(text)) hashMode = true;
            var map = new JsCodeMap(text);
            var imports = ModuleResolver.ImportsOf(text);
            string rel = Rel(fullRoot, file);
            foreach (var r in ObjectRoutes(map, file, imports, resolver).Concat(JsxRoutes(map, file, imports, resolver)))
                pages.Add(r);
        }
        if (hashMode)
            pages = pages.Select(p => new StudioPage
            {
                Id = p.Id, Kind = p.Kind, Title = p.Title, Route = p.Route, SourcePath = p.SourcePath, SourceLine = p.SourceLine,
                Detail = p.Detail, Params = p.Params, Risky = p.Risky, HashRoute = true,
            }).ToList();
        return Dedupe(pages);
    }

    private static IEnumerable<StudioPage> ObjectRoutes(JsCodeMap map, string file, Dictionary<string, string> imports, ModuleResolver resolver)
    {
        string t = map.Text;
        var seen = new HashSet<int>();

        foreach (Match m in PathKey.Matches(t))
        {
            if (!map.IsCode[m.Index]) continue;
            int obj = map.Inner[m.Index];
            if (obj < 0 || t[obj] != '{' || !seen.Add(obj)) continue;
            string? value = map.LiteralAt(m.Index + m.Length, out _);
            if (value == null || !HasKey(map, obj, RouterKeys)) continue;
            if ((HasKey(map, obj, "redirect") || HasKey(map, obj, "redirectTo")) && !HasKey(map, obj, "children")) continue;
            var page = MakeObjectRoute(map, obj, value, file, imports, resolver);
            if (page != null) yield return page;
        }
        foreach (Match m in IndexKey.Matches(t))
        {
            if (!map.IsCode[m.Index]) continue;
            int obj = map.Inner[m.Index];
            if (obj < 0 || t[obj] != '{' || seen.Contains(obj) || KeyValue(map, obj, PathKey) != null) continue;
            seen.Add(obj);
            var page = MakeObjectRoute(map, obj, "", file, imports, resolver);
            if (page != null) yield return page;
        }
    }

    private static StudioPage? MakeObjectRoute(JsCodeMap map, int obj, string ownPath, string file, Dictionary<string, string> imports, ModuleResolver resolver)
    {
        string t = map.Text;
        string full = ownPath;
        int cur = obj;
        for (int guard = 0; guard < 20; guard++)
        {
            int arr = map.Parent(cur);
            if (arr < 0 || t[arr] != '[') break;
            string? key = map.KeyBefore(arr);
            if (key is not ("children" or "routes")) break;
            int parent = map.Parent(arr);
            if (parent < 0 || t[parent] != '{') break;
            string? parentPath = KeyValue(map, parent, PathKey);
            if (parentPath != null) full = JoinRoute(parentPath, full);
            cur = parent;
        }
        var route = NormalizeRoute(full, out var parameters);
        if (route == null) return null;

        // what renders it — read before its own children: a layout route without an element is not its child's page
        int bodyEnd = Math.Min(map.CloseOf(obj) + 1, t.Length);
        foreach (Match ch in Regex.Matches(t[obj..bodyEnd], @"(?<![\w$.])children\s*:"))
            if (map.IsCode[obj + ch.Index] && map.Inner[obj + ch.Index] == obj) { bodyEnd = obj + ch.Index; break; }
        string body = t[obj..bodyEnd];
        string? comp = null, spec = null;
        var em = Regex.Match(body, @"(?<![\w$])element\s*:\s*<\s*(?<n>[A-Za-z_$][\w$.]*)");
        if (em.Success) comp = em.Groups["n"].Value;
        var cm = Regex.Match(body, @"(?<![\w$])(?:component|Component)\s*:\s*(?<n>[A-Za-z_$][\w$]*)\s*[,}\n]");
        if (comp == null && cm.Success) comp = cm.Groups["n"].Value;
        var lm = Regex.Match(body, @"(?<![\w$])(?:component|Component|lazy|loadComponent)\s*:\s*(?:async\s*)?\(\s*\)\s*=>\s*import\(\s*['""](?<p>[^'""]+)['""]");
        if (lm.Success) spec = lm.Groups["p"].Value;
        if (spec == null && comp != null && imports.TryGetValue(comp, out var s)) spec = s;
        string? source = spec != null ? resolver.Resolve(Path.GetDirectoryName(file)!, spec) : null;

        string? title = OwnTitle(map, obj);
        string? name = KeyValue(map, obj, new Regex(@"(?<![\w$.])name\s*:"));
        var (line, _) = SourceTagEditor.LineColOf(t, obj);
        return new StudioPage
        {
            Id = "route:" + route.ToLowerInvariant(),
            Kind = StudioPageKind.Route,
            Route = route,
            Title = title ?? (comp != null ? Humanize(comp) : null) ?? (source != null ? Humanize(Path.GetFileNameWithoutExtension(source)) : null) ?? RouteTitle(route),
            SourcePath = source ?? file,
            SourceLine = source != null ? 1 : line,
            Detail = comp ?? (source != null ? Path.GetFileName(source) : null),
            Params = parameters,
            Risky = PageSafety.IsRisky(route, name),
        };
    }

    /// <summary>A title the route gives itself (title: / meta: { title }) — not one of its children's.</summary>
    private static string? OwnTitle(JsCodeMap map, int obj)
    {
        string t = map.Text;
        int end = map.CloseOf(obj);
        foreach (Match m in Regex.Matches(t[obj..Math.Min(end, t.Length)], @"(?<![\w$.])title\s*:"))
        {
            int at = obj + m.Index;
            if (!map.IsCode[at]) continue;
            int owner = map.Inner[at];
            // its own object, or a meta/handle/data object directly inside it
            if (owner != obj && !(map.Parent(owner) == obj && map.KeyBefore(owner) is "meta" or "handle" or "data")) continue;
            string? v = map.LiteralAt(at + m.Length, out _);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }

    private static bool HasKey(JsCodeMap map, int obj, string key) => HasKey(map, obj, new Regex(@"(?<![\w$.])" + key + @"\s*:"));

    private static bool HasKey(JsCodeMap map, int obj, Regex key)
    {
        string t = map.Text;
        int end = Math.Min(map.CloseOf(obj), t.Length);
        foreach (Match m in key.Matches(t, obj))
        {
            if (m.Index >= end) break;
            if (map.IsCode[m.Index] && map.Inner[m.Index] == obj) return true;
        }
        return false;
    }

    /// <summary>The string literal value of a key that belongs to <paramref name="obj"/> itself.</summary>
    private static string? KeyValue(JsCodeMap map, int obj, Regex key)
    {
        string t = map.Text;
        int end = Math.Min(map.CloseOf(obj), t.Length);
        foreach (Match m in key.Matches(t, obj))
        {
            if (m.Index >= end) break;
            if (map.IsCode[m.Index] && map.Inner[m.Index] == obj) return map.LiteralAt(m.Index + m.Length, out _);
        }
        return null;
    }

    private sealed record JsxRoute(int Start, int End, string? Path, bool Index, string? Comp, int Line);

    private static IEnumerable<StudioPage> JsxRoutes(JsCodeMap map, string file, Dictionary<string, string> imports, ModuleResolver resolver)
    {
        string t = map.Text;
        var routes = new List<JsxRoute>();
        int at = 0;
        while ((at = t.IndexOf("<Route", at, StringComparison.Ordinal)) >= 0)
        {
            int here = at;
            at += 6;
            if (here + 6 < t.Length && !(char.IsWhiteSpace(t[here + 6]) || t[here + 6] is '>' or '/')) continue;
            if (!map.IsCode[here]) continue;
            var tag = SourceTagEditor.ParseOpenTag(t, here, SourceDialect.Jsx);
            if (tag == null) continue;
            string? path = AttrLiteral(t, tag.Find("path"));
            bool index = tag.Find("index") != null;
            if (path == null && !index && tag.Find("path") != null) continue;   // path={variable}: unknown
            string? comp = null;
            if (tag.Find("element") is { IsExpression: true } el)
            {
                var m = Regex.Match(el.Value(t) ?? "", @"^\s*<\s*([A-Za-z_$][\w$.]*)");
                if (m.Success) comp = m.Groups[1].Value;
            }
            foreach (var n in new[] { "Component", "component" })
                if (comp == null && tag.Find(n) is { IsExpression: true } c && Regex.Match(c.Value(t) ?? "", @"^\s*([A-Za-z_$][\w$]*)\s*$") is { Success: true } cm)
                    comp = cm.Groups[1].Value;
            int end = tag.SelfClosing ? tag.End : SourceTagEditor.FindElementEnd(t, tag, SourceDialect.Jsx);
            if (end <= tag.Start) end = tag.End;
            routes.Add(new JsxRoute(tag.Start, end, path, index, comp, SourceTagEditor.LineColOf(t, tag.Start).Line));
        }
        foreach (var r in routes)
        {
            if (r.Path == null && !r.Index) continue;   // a layout route: only its children are pages
            string full = r.Path ?? "";
            foreach (var p in routes.Where(p => p.Start < r.Start && p.End >= r.End).OrderByDescending(p => p.Start))
                if (p.Path != null) full = JoinRoute(p.Path, full);
            var route = NormalizeRoute(full, out var parameters);
            if (route == null) continue;
            string? source = r.Comp != null && imports.TryGetValue(r.Comp, out var spec) ? resolver.Resolve(Path.GetDirectoryName(file)!, spec) : null;
            yield return new StudioPage
            {
                Id = "route:" + route.ToLowerInvariant(),
                Kind = StudioPageKind.Route,
                Route = route,
                Title = r.Comp != null ? Humanize(r.Comp) : RouteTitle(route),
                SourcePath = source ?? file,
                SourceLine = source != null ? 1 : r.Line,
                Detail = r.Comp,
                Params = parameters,
                Risky = PageSafety.IsRisky(route, null),
            };
        }
    }

    private static string? AttrLiteral(string t, SourceAttr? a)
    {
        if (a == null) return null;
        string? v = a.Value(t);
        if (v == null) return null;
        if (!a.IsExpression) return v;
        var m = Regex.Match(v, @"^\s*(['""`])([^'""`$]*)\1\s*$");
        return m.Success ? m.Groups[2].Value : null;
    }

    // ───────────────────────────── route text ─────────────────────────────

    internal static string JoinRoute(string parent, string child)
    {
        if (child.StartsWith('/')) return child;
        if (child.Length == 0) return parent;
        return parent.TrimEnd('/') + "/" + child;
    }

    /// <summary>"/users/:id?" → "/users"; "users/:id" → "/users/:id" with id a parameter; "*" or "**" → null
    /// (a catch-all is a not-found page, not a page of its own).</summary>
    internal static string? NormalizeRoute(string path, out List<string> parameters)
    {
        parameters = new List<string>();
        var segs = new List<string>();
        foreach (var raw in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string s = raw.Trim();
            if (s is "*" or "**" || s.Contains('*') || s.Contains("(.*)", StringComparison.Ordinal)) return null;
            if (s.StartsWith(':'))
            {
                if (s.EndsWith('?')) continue;   // optional: the page renders without it
                string name = Regex.Match(s, @"^:([\w$-]+)").Groups[1].Value;
                parameters.Add(name.Length > 0 ? name : s[1..]);
                segs.Add(":" + (name.Length > 0 ? name : s[1..]));
                continue;
            }
            segs.Add(s);
        }
        return "/" + string.Join('/', segs);
    }

    /// <summary>"/admin/user-settings" → "User settings"; "/" → "Home".</summary>
    public static string RouteTitle(string route)
    {
        var last = route.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(s => !s.StartsWith(':') && !s.StartsWith('{'));
        if (last == null) return route.Trim('/').Length == 0 ? "Home" : route;
        return Humanize(Path.GetFileNameWithoutExtension(last));
    }

    /// <summary>"UserDetailsPage" → "User Details"; "about-us" → "About us".</summary>
    public static string Humanize(string name)
    {
        string n = Regex.Replace(name, @"(Page|Screen|View|Route|Component)$", "");
        if (n.Length == 0) n = name;
        // "UserDetails" → "User Details", "Verify2FA" → "Verify 2FA", "HTMLPage" → "HTML Page"
        n = Regex.Replace(n, @"(?<=[a-z])(?=[A-Z0-9])|(?<=[A-Z])(?=[A-Z][a-z])", " ").Replace('_', ' ').Replace('-', ' ').Trim();
        if (n.Length == 0) return name;
        return char.ToUpperInvariant(n[0]) + n[1..];
    }

    private static readonly Regex[] TitlePatterns =
    {
        new(@"<title[^>]*>\s*([^<{]+?)\s*</title>", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(?<![\w$.])title\s*[:=]\s*(['""])(?<t>[^'""{}<>\n]{2,80})\1", RegexOptions.Compiled),
        new(@"<h1[^>]*>\s*([^<{]+?)\s*<", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    /// <summary>A page component's own title: &lt;title&gt;, a <c>title:</c> in its metadata/head, or its first &lt;h1&gt;.</summary>
    public static string? SourceTitle(string text)
    {
        foreach (var re in TitlePatterns)
        {
            var m = re.Match(text);
            if (!m.Success) continue;
            string v = (m.Groups["t"].Success ? m.Groups["t"].Value : m.Groups[1].Value).Trim();
            if (v.Length >= 2) return Clean(v);
        }
        return null;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    internal static List<StudioPage> Dedupe(List<StudioPage> pages) => pages
        .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.OrderByDescending(p => p.Detail != null).First())
        .OrderBy(p => p.Route == "/" ? 0 : 1)
        .ThenBy(p => p.NeedsParams ? 1 : 0)
        .ThenBy(p => p.Route, StringComparer.OrdinalIgnoreCase)
        .ToList();

    internal static string FullRoot(string root) => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    internal static string Rel(string fullRoot, string file) =>
        (file.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? file[fullRoot.Length..] : Path.GetFileName(file)).Replace('\\', '/');

    internal static string SafeRead(string path, long max)
    {
        try { return new FileInfo(path).Length > max ? "" : File.ReadAllText(path); }
        catch { return ""; }
    }
}

/// <summary>Turns an import specifier into the project file it means ("./pages/Home" → …/pages/Home.tsx).</summary>
internal sealed class ModuleResolver
{
    private readonly string _fullRoot;
    private static readonly string[] TryExt = { "", ".tsx", ".ts", ".jsx", ".js", ".mjs", ".vue", ".svelte", ".astro", "/index.tsx", "/index.ts", "/index.jsx", "/index.js", "/index.vue" };
    private static readonly Regex DefaultImport = new(@"import\s+(?<n>[A-Za-z_$][\w$]*)\s*(?:,\s*\{[^}]*\})?\s+from\s+['""](?<p>[^'""]+)['""]", RegexOptions.Compiled);
    private static readonly Regex NamedImport = new(@"import\s+(?:[A-Za-z_$][\w$]*\s*,\s*)?\{(?<names>[^}]*)\}\s*from\s+['""](?<p>[^'""]+)['""]", RegexOptions.Compiled);
    private static readonly Regex LazyImport = new(@"(?:const|let|var)\s+(?<n>[A-Za-z_$][\w$]*)\s*=\s*(?:React\.)?(?:lazy|defineAsyncComponent|loadable)\(\s*(?:async\s*)?\(\s*\)\s*=>\s*import\(\s*['""](?<p>[^'""]+)['""]", RegexOptions.Compiled);

    public ModuleResolver(string root) => _fullRoot = WebPages.FullRoot(root);

    /// <summary>Local name → import specifier, for every import in the file.</summary>
    public static Dictionary<string, string> ImportsOf(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in DefaultImport.Matches(text)) map.TryAdd(m.Groups["n"].Value, m.Groups["p"].Value);
        foreach (Match m in LazyImport.Matches(text)) map.TryAdd(m.Groups["n"].Value, m.Groups["p"].Value);
        foreach (Match m in NamedImport.Matches(text))
            foreach (var part in m.Groups["names"].Value.Split(','))
            {
                var bits = part.Trim().Split(new[] { " as " }, StringSplitOptions.TrimEntries);
                string local = bits[^1];
                if (Regex.IsMatch(local, @"^[A-Za-z_$][\w$]*$")) map.TryAdd(local, m.Groups["p"].Value);
            }
        return map;
    }

    public string? Resolve(string fromDir, string spec)
    {
        string clean = spec.Split('?', '#')[0];
        string? baseFull = clean.StartsWith("./", StringComparison.Ordinal) || clean.StartsWith("../", StringComparison.Ordinal) ? Path.Combine(fromDir, clean)
            : clean.StartsWith("@/", StringComparison.Ordinal) || clean.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(_fullRoot, "src", clean[2..])
            : clean.StartsWith('/') ? Path.Combine(_fullRoot, clean.TrimStart('/'))
            : null;
        if (baseFull == null) return null;
        foreach (var e in TryExt)
        {
            string candidate;
            try { candidate = Path.GetFullPath(baseFull.Replace('/', Path.DirectorySeparatorChar) + e.Replace('/', Path.DirectorySeparatorChar)); }
            catch { return null; }
            if (!candidate.StartsWith(_fullRoot, StringComparison.OrdinalIgnoreCase)) return null;
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>
/// Which pages must never be opened by the gallery on its own. Shooting a thumbnail is a real GET request: on a
/// server-rendered site a route like /logout, /cache/clear or /payment/callback DOES something when it is
/// fetched. Such a route still has its card; the user opens or shoots it by hand.
/// </summary>
public static class PageSafety
{
    private static readonly Regex Act = new(
        @"(?:^|[/._\-:{])(?:log-?out|sign-?out|logoff|delete|destroy|remove|trash|approve|reject|verify|verification|unsubscribe|cancel|callback|webhooks?|cron|clear|reset|toggle|activate|deactivate|enable|disable|ban|unban|block|unblock|suspend|impersonate|export|download|backup|restore|install|upgrade|migrate|seed|artisan|run|execute|exec|send|resend|notify|pay|payment|checkout|charge|refund|confirm|accept|decline|purge|flush|optimize|sync|import|oauth|redirect|mark-?as|publish|unpublish|kill|stop|start|restart|deploy|rollback)(?:$|[/._\-:}])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsRisky(string? route, string? name) =>
        route != null && Act.IsMatch(route) || name != null && Act.IsMatch(name);
}
