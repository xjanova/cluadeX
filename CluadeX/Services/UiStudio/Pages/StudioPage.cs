namespace CluadeX.Services.UiStudio.Pages;

/// <summary>Where a page in the gallery comes from.</summary>
public enum StudioPageKind
{
    /// <summary>An HTML file of a static site (served by CluadeX).</summary>
    Html,
    /// <summary>A route of a web app (file-based router, a router config, or a server route like Laravel's).</summary>
    Route,
    /// <summary>A screen of a native app (Flutter) — simulated from its source, never run.</summary>
    Screen,
    /// <summary>Any web page the user added or opened from the address bar.</summary>
    External,
}

/// <summary>One page of the project, as the page gallery lists it.</summary>
public sealed class StudioPage
{
    /// <summary>Stable key (kind + route/file) — the thumbnail cache is keyed by it.</summary>
    public string Id { get; init; } = "";
    public StudioPageKind Kind { get; init; }
    public string Title { get; set; } = "";
    /// <summary>The page's path in the app ("/about", "/users/:id") — null for a screen no route reaches.</summary>
    public string? Route { get; init; }
    /// <summary>An absolute URL (external pages only; project pages get theirs from the running server).</summary>
    public string? Url { get; init; }
    /// <summary>The file that renders it (full path), when known.</summary>
    public string? SourcePath { get; init; }
    public int SourceLine { get; init; }
    /// <summary>The component / view / widget that renders it ("UsersScreen", "pages.about").</summary>
    public string? Detail { get; init; }
    /// <summary>Route parameters with no value yet (":id") — such a page can't be opened or shot as it is.</summary>
    public IReadOnlyList<string> Params { get; init; } = Array.Empty<string>();
    /// <summary>Opening it may DO something (log out, delete, pay, run a job…) — it is never shot automatically.</summary>
    public bool Risky { get; init; }
    /// <summary>Behind a login (route middleware) — informational.</summary>
    public bool NeedsLogin { get; init; }
    /// <summary>Flutter: shown inside the app's tab shell (the shell's bar is drawn around it).</summary>
    public bool InShell { get; init; }
    /// <summary>The page lives under a hash router ("#/about").</summary>
    public bool HashRoute { get; init; }
    /// <summary>A desktop app's kind of screen — "window" or "view" (UserControl / Page): the gallery's filter chips.</summary>
    public string? Group { get; init; }

    public bool NeedsParams => Params.Count > 0;

    /// <summary>The URL of this page on a server rooted at <paramref name="baseUrl"/> (null when it needs a
    /// parameter or has no route). Each path segment is escaped, so Thai or spaced file names stay valid.</summary>
    public string? UrlOn(string? baseUrl)
    {
        if (Url != null) return Url;
        if (Route == null || NeedsParams || string.IsNullOrEmpty(baseUrl)) return null;
        string b = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        string path = string.Join('/', Route.TrimStart('/').Split('/').Select(s => s.Length == 0 ? s : Uri.EscapeDataString(Uri.UnescapeDataString(s))));
        return HashRoute ? b + "#/" + path : b + path;
    }

    public override string ToString() => $"{Kind} {Route ?? Detail ?? Id} \"{Title}\"";
}

/// <summary>What the catalog found in a project.</summary>
public sealed class PageCatalogResult
{
    public List<StudioPage> Pages { get; } = new();
    /// <summary>How the routes were read ("html", "next", "react-router", "laravel", "go_router"…).</summary>
    public string Source { get; set; } = "";
    /// <summary>A base URL the project says it runs at (Laravel's APP_URL) — a suggestion the user can change.</summary>
    public string? SuggestedBaseUrl { get; set; }
    public bool Truncated { get; set; }
    /// <summary>A Flutter project, read: it draws each screen's wireframe (<see cref="Sketch.FlutterProject.Sketch"/>).</summary>
    public Sketch.FlutterProject? Flutter { get; set; }
    /// <summary>A WPF project, read: it draws each window's and view's wireframe from its XAML.</summary>
    public Sketch.WpfProject? Wpf { get; set; }
    /// <summary>Whatever draws this project's screens (Flutter or WPF); null for a web project.</summary>
    public Sketch.IScreenSketcher? Screens => (Sketch.IScreenSketcher?)Flutter ?? Wpf;
}

/// <summary>Lists a project's pages, whatever it is built with.</summary>
public static class PageCatalog
{
    public static PageCatalogResult Discover(StudioProjectInfo info, string language, CancellationToken ct = default)
    {
        var r = new PageCatalogResult();
        string root = info.Root;
        if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root)) return r;
        switch (info.Kind)
        {
            case StudioProjectKind.Static:
                r.Source = "html";
                r.Pages.AddRange(WebPages.StaticHtml(root, info.EntryHtml));
                break;

            case StudioProjectKind.Server:
                r.Source = "laravel";
                r.Pages.AddRange(LaravelPages.Discover(root, ct));
                r.SuggestedBaseUrl = LaravelPages.AppUrl(root) ?? "http://localhost:8000/";
                break;

            case StudioProjectKind.Simulated when info.Framework == "wpf":
            {
                r.Source = "wpf";
                var wpf = Sketch.WpfProject.Load(root, language, ct);
                r.Wpf = wpf;
                r.Truncated = wpf.Truncated;
                r.Pages.AddRange(wpf.Pages());
                break;
            }

            case StudioProjectKind.Simulated:
            {
                r.Source = "flutter";
                var flutter = Sketch.FlutterProject.Load(root, language, ct);
                r.Flutter = flutter;
                r.Truncated = flutter.Truncated;
                r.Pages.AddRange(flutter.Pages());
                break;
            }

            case StudioProjectKind.Vite:
            case StudioProjectKind.NodeOther:
            {
                string pkg = WebPages.SafeRead(System.IO.Path.Combine(root, "package.json"), 300_000);
                WebPages.FileRouter? router = info.Framework switch
                {
                    "next" => WebPages.FileRouter.Next,
                    "nuxt" => WebPages.FileRouter.Nuxt,
                    "astro" => WebPages.FileRouter.Astro,
                    _ when pkg.Contains("\"@sveltejs/kit\"", StringComparison.Ordinal) => WebPages.FileRouter.SvelteKit,
                    _ => null,
                };
                if (router is { } fr)
                {
                    r.Source = fr.ToString().ToLowerInvariant();
                    r.Pages.AddRange(WebPages.FileRoutes(root, fr));
                }
                if (r.Pages.Count == 0)
                {
                    var code = WebPages.CodeRoutes(root, out _);
                    if (code.Count > 0) { r.Source = info.Framework is "vue" ? "vue-router" : info.Framework is "angular" ? "angular" : "react-router"; r.Pages.AddRange(code); }
                }
                // Vite serves every HTML file of the project (multi-page apps); public/ is copied as it is.
                foreach (var html in WebPages.StaticHtml(root, null))
                {
                    if (html.Route == "/" && r.Pages.Any(p => p.Route == "/")) continue;
                    if (html.Detail!.StartsWith("public/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (html.Route != "/" && router != null) continue;
                    r.Pages.Add(new StudioPage
                    {
                        Id = "route:" + html.Route!.ToLowerInvariant(), Kind = StudioPageKind.Route, Route = html.Route, Title = html.Title,
                        SourcePath = html.SourcePath, SourceLine = 1, Detail = html.Detail, Risky = html.Risky,
                    });
                }
                if (r.Source.Length == 0) r.Source = "html";
                if (!r.Pages.Any(p => p.Route == "/"))
                    r.Pages.Insert(0, new StudioPage { Id = "route:/", Kind = StudioPageKind.Route, Route = "/", Title = WebPages.RouteTitle("/") });
                var ordered = WebPages.Dedupe(r.Pages);
                r.Pages.Clear();
                r.Pages.AddRange(ordered);
                break;
            }
        }
        return r;
    }
}
