using System.IO;
using CluadeX.Services.UiStudio;
using CluadeX.Services.UiStudio.Pages;
using CluadeX.Services.UiStudio.Sketch;

namespace StudioTest;

/// <summary>
/// The page gallery's reading of projects, through the REAL CluadeX.dll: every page of a static site, file-based
/// routers (Next, Nuxt, SvelteKit, Astro), route tables in code (React Router JSX + objects, Vue Router, Angular), a
/// Laravel site (groups, prefixes, resources, controller → view, JSON routes left out), the safety filter, the
/// thumbnail store — and Flutter screens simulated from their source (go_router, ARB text, a project widget drawn with
/// the caller's arguments, the data branch over the spinner). Temp folders only.
/// </summary>
internal static partial class Program
{
    static void W(string root, string rel, string text)
    {
        string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    static StudioPage? Route(IEnumerable<StudioPage> pages, string route) => pages.FirstOrDefault(p => p.Route == route);
    static string Routes(IEnumerable<StudioPage> pages) => string.Join(", ", pages.Select(p => p.Route ?? p.Detail));

    // ───────────────────────────── web projects ─────────────────────────────

    static void PageDiscovery(string temp)
    {
        Console.WriteLine("Page gallery: web projects");

        // static site
        string site = Path.Combine(temp, "pages-static");
        W(site, "index.html", "<!doctype html><html><head><title>Home &amp; Co</title></head><body><h1>x</h1></body></html>");
        W(site, "about.html", "<html><body><h1>About <b>us</b></h1></body></html>");
        W(site, "blog/index.html", "<title>Blog</title>");
        W(site, "blog/post.html", "<title>A post</title>");
        W(site, "logout.html", "<title>Bye</title>");
        W(site, "เกี่ยวกับ.html", "<title>ไทย</title>");
        W(site, "node_modules/lib/x.html", "<title>no</title>");
        W(site, ".hidden/y.html", "<title>no</title>");
        var info = StudioProject.Detect(site);
        var r = PageCatalog.Discover(info, "en");
        var pages = r.Pages;
        Check("static: every HTML file is a page (node_modules and dot-folders are not)", pages.Count == 6 && pages.All(p => p.Kind == StudioPageKind.Html), Routes(pages));
        Check("static: the entry page comes first", pages[0].Route == "/", Routes(pages));
        Check("static: titles from <title> (decoded) or the first <h1>", Route(pages, "/")?.Title == "Home & Co" && Route(pages, "/about.html")?.Title == "About us", string.Join(" | ", pages.Select(p => p.Title)));
        Check("static: a folder's index.html is the folder's route", Route(pages, "/blog/") != null && Route(pages, "/blog/post.html") != null);
        Check("static: /logout.html is never shot on its own", Route(pages, "/logout.html")?.Risky == true && Route(pages, "/about.html")?.Risky == false);
        var thai = pages.First(p => p.Title == "ไทย");
        Check("static: a Thai file name is escaped in its URL", thai.UrlOn("http://localhost:5000/") == "http://localhost:5000/" + Uri.EscapeDataString("เกี่ยวกับ.html"), thai.UrlOn("http://localhost:5000/") ?? "");

        // Next.js (app router + pages router)
        string next = Path.Combine(temp, "pages-next");
        W(next, "package.json", "{\"scripts\":{\"dev\":\"next dev\"},\"dependencies\":{\"next\":\"14\",\"react\":\"18\"}}");
        W(next, "app/page.tsx", "export default function Page() { return <main><h1>Start</h1></main> }");
        W(next, "app/(marketing)/pricing/page.tsx", "export const metadata = { title: 'Pricing' }\nexport default function P() { return <div/> }");
        W(next, "app/blog/[slug]/page.tsx", "export default function P() { return <div/> }");
        W(next, "app/docs/[[...slug]]/page.tsx", "export default function P() { return <div/> }");
        W(next, "app/@modal/login/page.tsx", "export default function P() { return <div/> }");
        W(next, "app/_lib/page.tsx", "export default function P() { return <div/> }");
        W(next, "app/api/hello/route.ts", "export function GET() {}");
        W(next, "pages/legacy.tsx", "export default function L() { return <div/> }");
        W(next, "pages/_app.tsx", "export default function A() {}");
        W(next, "pages/api/x.ts", "export default function h() {}");
        pages = PageCatalog.Discover(StudioProject.Detect(next), "en").Pages;
        Check("next: app + pages routers", new[] { "/", "/pricing", "/blog/:slug", "/docs", "/legacy" }.All(x => Route(pages, x) != null), Routes(pages));
        Check("next: (groups) vanish, @slots / _private / api / _app are not pages", pages.Count == 5, Routes(pages));
        Check("next: [slug] is a parameter, [[...slug]] optional", Route(pages, "/blog/:slug")?.Params.SequenceEqual(new[] { "slug" }) == true && Route(pages, "/docs")?.NeedsParams == false);
        Check("next: metadata title and the first <h1>", Route(pages, "/pricing")?.Title == "Pricing" && Route(pages, "/")?.Title == "Start", string.Join(" | ", pages.Select(p => p.Title)));

        // Nuxt / SvelteKit / Astro
        string nuxt = Path.Combine(temp, "pages-nuxt");
        W(nuxt, "package.json", "{\"scripts\":{\"dev\":\"nuxt dev\"},\"dependencies\":{\"nuxt\":\"3\",\"vue\":\"3\"}}");
        W(nuxt, "pages/index.vue", "<template><h1>Hi</h1></template>");
        W(nuxt, "pages/users/[id].vue", "<template/>");
        W(nuxt, "pages/about.vue", "<template/>");
        pages = PageCatalog.Discover(StudioProject.Detect(nuxt), "en").Pages;
        Check("nuxt: pages/ is the router", Routes(pages) == "/, /about, /users/:id", Routes(pages));
        string kit = Path.Combine(temp, "pages-kit");
        W(kit, "package.json", "{\"scripts\":{\"dev\":\"vite dev\"},\"devDependencies\":{\"@sveltejs/kit\":\"2\",\"svelte\":\"4\",\"vite\":\"5\"}}");
        W(kit, "vite.config.js", "export default {}");
        W(kit, "src/routes/+page.svelte", "<h1>Home</h1>");
        W(kit, "src/routes/(app)/dashboard/+page.svelte", "<svelte:head><title>Dash</title></svelte:head>");
        W(kit, "src/routes/blog/[slug]/+page.svelte", "<p/>");
        W(kit, "src/routes/blog/[slug]/+page.ts", "export const load = () => {}");
        pages = PageCatalog.Discover(StudioProject.Detect(kit), "en").Pages;
        Check("sveltekit: +page.svelte files, (group) folders vanish", Routes(pages) == "/, /dashboard, /blog/:slug", Routes(pages));
        Check("sveltekit: <title> in svelte:head", Route(pages, "/dashboard")?.Title == "Dash");
        string astro = Path.Combine(temp, "pages-astro");
        W(astro, "package.json", "{\"scripts\":{\"dev\":\"astro dev\"},\"dependencies\":{\"astro\":\"4\"}}");
        W(astro, "src/pages/index.astro", "---\nconst title = 'x'\n---\n<h1>Astro home</h1>");
        W(astro, "src/pages/about.md", "---\ntitle: 'About Astro'\n---\n# About");
        W(astro, "src/pages/_draft.astro", "<p/>");
        W(astro, "src/pages/posts/[slug].astro", "<p/>");
        pages = PageCatalog.Discover(StudioProject.Detect(astro), "en").Pages;
        Check("astro: src/pages, _drafts left out", Routes(pages) == "/, /about, /posts/:slug", Routes(pages));
        Check("astro: frontmatter title", Route(pages, "/about")?.Title == "About Astro", Route(pages, "/about")?.Title ?? "");

        // React Router: objects + JSX, in one Vite app
        string rr = Path.Combine(temp, "pages-react");
        W(rr, "package.json", "{\"scripts\":{\"dev\":\"vite\"},\"dependencies\":{\"react\":\"18\",\"react-router-dom\":\"6\"},\"devDependencies\":{\"vite\":\"5\"}}");
        W(rr, "vite.config.ts", "export default {}");
        W(rr, "index.html", "<title>Shop app</title><div id=root></div>");
        W(rr, "src/pages/About.tsx", "export default function About() { return <h1>About</h1> }");
        W(rr, "src/pages/Login.tsx", "export default function Login() { return <form/> }");
        W(rr, "src/main.tsx", @"import { createBrowserRouter } from 'react-router-dom'
import Layout from './Layout'
import Home from './pages/Home'
import About from './pages/About'
// { path: '/ghost', element: <Ghost /> }  — a comment, not a route
const pattern = /path: '\/nope'/
export const router = createBrowserRouter([
  {
    path: '/',
    element: <Layout />,
    children: [
      { index: true, element: <Home /> },
      { path: 'about', element: <About />, handle: { title: 'About us' } },
      { path: 'users/:id', element: <User /> },
      { path: '*', element: <NotFound /> },
    ],
  },
  { path: '/login', lazy: () => import('./pages/Login') },
])
");
        W(rr, "src/App.tsx", @"export function App() {
  return (
    <Routes>
      <Route path=""/shop"" element={<Shop />}>
        <Route path=""cart"" element={<Cart />} />
      </Route>
      <Route path=""/logout"" element={<Logout />} />
      <p>Don't {count} stop</p>
    </Routes>
  )
}
");
        pages = PageCatalog.Discover(StudioProject.Detect(rr), "en").Pages;
        Check("react-router: objects with children, index and lazy", new[] { "/", "/about", "/users/:id", "/login" }.All(x => Route(pages, x) != null), Routes(pages));
        Check("react-router: JSX <Route> nesting", Route(pages, "/shop") != null && Route(pages, "/shop/cart") != null, Routes(pages));
        Check("react-router: catch-all, commented and regex 'routes' are not pages", Route(pages, "/ghost") == null && Route(pages, "/nope") == null && !pages.Any(p => p.Route!.Contains('*')), Routes(pages));
        Check("react-router: a route's own handle title, and its component's file", Route(pages, "/about") is { Title: "About us" } a && a.SourcePath!.EndsWith("About.tsx"), $"{Route(pages, "/about")?.Title} {Route(pages, "/about")?.SourcePath}");
        Check("react-router: lazy import resolved to its file", Route(pages, "/login")?.SourcePath?.EndsWith("Login.tsx") == true, Route(pages, "/login")?.SourcePath ?? "");
        Check("react-router: /logout is never shot on its own", Route(pages, "/logout")?.Risky == true);

        // Vue Router (hash mode) and Angular
        string vue = Path.Combine(temp, "pages-vue");
        W(vue, "package.json", "{\"scripts\":{\"dev\":\"vite\"},\"dependencies\":{\"vue\":\"3\",\"vue-router\":\"4\"},\"devDependencies\":{\"vite\":\"5\"}}");
        W(vue, "vite.config.js", "export default {}");
        W(vue, "src/router/index.ts", @"import { createRouter, createWebHashHistory } from 'vue-router'
import Home from '../views/Home.vue'
export default createRouter({
  history: createWebHashHistory(),
  routes: [
    { path: '/', name: 'home', component: Home },
    { path: '/settings', component: () => import('../views/Settings.vue'), meta: { title: 'Settings' },
      children: [{ path: 'profile', component: Profile }] },
    { path: '/old', redirect: '/' },
  ],
})
");
        W(vue, "src/views/Settings.vue", "<template/>");
        pages = PageCatalog.Discover(StudioProject.Detect(vue), "en").Pages;
        Check("vue-router: routes, children, a redirect left out", Routes(pages) == "/, /settings, /settings/profile", Routes(pages));
        Check("vue-router: hash history → #/ URLs", Route(pages, "/settings")?.UrlOn("http://localhost:5173/") == "http://localhost:5173/#/settings", Route(pages, "/settings")?.UrlOn("http://localhost:5173/") ?? "");
        Check("vue-router: meta title", Route(pages, "/settings")?.Title == "Settings");
        string ng = Path.Combine(temp, "pages-ng");
        W(ng, "package.json", "{\"scripts\":{\"start\":\"ng serve\"},\"dependencies\":{\"@angular/core\":\"17\"}}");
        W(ng, "src/app/app.routes.ts", "import { Routes } from '@angular/router';\nexport const routes: Routes = [\n  { path: '', component: HomeComponent },\n  { path: 'heroes', component: HeroesComponent, title: 'Heroes' },\n  { path: '**', component: NotFoundComponent },\n];\n");
        pages = PageCatalog.Discover(StudioProject.Detect(ng), "en").Pages;
        Check("angular: relative paths, ** left out, route title", Routes(pages) == "/, /heroes" && Route(pages, "/heroes")?.Title == "Heroes", Routes(pages));

        // the safety filter on its own
        Check("safety: action routes are flagged", new[] { "/logout", "/admin/users/delete", "/cache/clear", "/payment/callback", "/cron/run" }.All(x => PageSafety.IsRisky(x, null)));
        Check("safety: ordinary pages are not", new[] { "/", "/about", "/payments", "/users/settings-help", "/blog/clearance" }.All(x => !PageSafety.IsRisky(x, null)),
              string.Join(",", new[] { "/", "/about", "/payments", "/users/settings-help", "/blog/clearance" }.Where(x => PageSafety.IsRisky(x, null))));
        Check("safety: a route NAME can flag it too", PageSafety.IsRisky("/x", "admin.cache.clear"));
    }

    // ───────────────────────────── Laravel ─────────────────────────────

    static void LaravelSite(string temp)
    {
        Console.WriteLine("Page gallery: a Laravel site");
        string root = Path.Combine(temp, "laravel-site");
        W(root, "artisan", "#!/usr/bin/env php");
        W(root, "composer.json", "{\"require\":{\"laravel/framework\":\"^11.0\"}}");
        W(root, "package.json", "{\"scripts\":{\"dev\":\"vite\",\"build\":\"vite build\"},\"devDependencies\":{\"vite\":\"5\",\"laravel-vite-plugin\":\"1\"}}");
        W(root, "vite.config.js", "export default {}");
        W(root, ".env", "APP_NAME=Shop\nAPP_KEY=base64:SECRET-must-not-leak\nAPP_URL=http://shop.test\nDB_PASSWORD=hunter2\n");
        W(root, "routes/web.php", @"<?php
use Illuminate\Support\Facades\Route;
use App\Http\Controllers\PageController;
use App\Http\Controllers\Admin\UserController;
// Route::get('/commented', fn () => view('nope'));
# Route::get('/hashed', fn () => view('nope'));
Route::get('/', function () { return view('welcome'); })->name('home');
Route::view('/about', 'pages.about')->name('about');
Route::get('/contact', [PageController::class, 'contact'])->name('contact');
Route::post('/contact', [PageController::class, 'send']);
Route::get('/api-ish', [PageController::class, 'json']);
Route::get('/logout', [PageController::class, 'logout'])->name('logout');
Route::get('/cache/clear', fn () => view('ok'));
Route::middleware(['auth'])->prefix('admin')->name('admin.')->group(function () {
    Route::get('/dashboard', [PageController::class, 'dashboard'])->name('dashboard');
    Route::resource('users', UserController::class)->only(['index', 'show']);
});
Route::group(['prefix' => 'shop'], function () {
    Route::get('/item/{id}', [PageController::class, 'item']);
    Route::get('/list/{page?}', [PageController::class, 'list']);
    Route::get('/preview/{level}.png', [PageController::class, 'item']);
});
require __DIR__.'/auth.php';
");
        W(root, "routes/auth.php", "<?php\nuse Illuminate\\Support\\Facades\\Route;\nRoute::get('/login', fn () => view('auth.login'))->name('login');\n");
        W(root, "routes/api.php", "<?php\nRoute::get('/users', fn () => view('nope'));\n");
        W(root, "app/Http/Controllers/PageController.php", @"<?php
namespace App\Http\Controllers;
class PageController extends Controller
{
    public function contact() { return view('pages.contact'); }
    public function json(Request $r): JsonResponse { return response()->json(['ok' => true]); }
    public function logout() { Auth::logout(); return redirect('/'); }
    public function dashboard() { $x = 1; return view('admin.dashboard', compact('x')); }
    public function item($id) { return view('shop.item'); }
    public function list($page = 1) { return view('shop.list'); }
    public function send() { return back(); }
}
");
        W(root, "app/Http/Controllers/Admin/UserController.php", "<?php\nclass UserController { public function index() { return view('admin.users.index'); } public function show($u) { return view('admin.users.show'); } }\n");
        W(root, "resources/views/welcome.blade.php", "<html><head><title>Welcome</title></head></html>");
        W(root, "resources/views/pages/about.blade.php", "@extends('layouts.app')\n@section('title', 'เกี่ยวกับเรา')\n@section('content') x @endsection");
        W(root, "resources/views/pages/contact.blade.php", "<h1>Contact {{ $name }} @if($vip) VIP @endif</h1>");
        W(root, "resources/views/admin/dashboard.blade.php", "<x-admin-layout title=\"Dashboard\"></x-admin-layout>");
        W(root, "resources/views/admin/users/index.blade.php", "<h1>Users</h1>");
        W(root, "resources/views/auth/login.blade.php", "<h1>Sign in</h1>");

        var info = StudioProject.Detect(root);
        Check("laravel: detected as a server site, not Vite (its dev script is only the asset server)", info.Kind == StudioProjectKind.Server && info.Framework == "laravel", info.Kind.ToString());
        var r = PageCatalog.Discover(info, "en");
        var pages = r.Pages;
        Check("laravel: GET pages from web.php, groups and required files",
              new[] { "/", "/about", "/contact", "/admin/dashboard", "/admin/users", "/admin/users/:user", "/shop/item/:id", "/shop/list", "/login" }.All(x => Route(pages, x) != null), Routes(pages));
        Check("laravel: JSON / redirect actions are not pages (api-ish, logout)", Route(pages, "/api-ish") == null && Route(pages, "/logout") == null, Routes(pages));
        Check("laravel: commented routes and routes/api.php are not read", Route(pages, "/commented") == null && Route(pages, "/hashed") == null && Route(pages, "/users") == null);
        Check("laravel: resource ->only([index, show])", Route(pages, "/admin/users/create") == null && Route(pages, "/admin/users/:user/edit") == null);
        Check("laravel: {page?} is optional, {id} a parameter, a parameter inside a segment too",
              Route(pages, "/shop/list")?.NeedsParams == false && Route(pages, "/shop/item/:id")?.NeedsParams == true && Route(pages, "/shop/preview/:level.png")?.NeedsParams == true, Routes(pages));
        Check("laravel: auth middleware marks login pages", Route(pages, "/admin/dashboard")?.NeedsLogin == true && Route(pages, "/about")?.NeedsLogin == false);
        Check("laravel: titles from the view (@section, <title>, a layout's title=, <h1> without Blade)",
              Route(pages, "/about")?.Title == "เกี่ยวกับเรา" && Route(pages, "/")?.Title == "Welcome" && Route(pages, "/admin/dashboard")?.Title == "Dashboard" && Route(pages, "/contact")?.Title == "Contact VIP",
              string.Join(" | ", pages.Select(p => p.Route + "=" + p.Title)));
        Check("laravel: each page opens its Blade view", Route(pages, "/contact")?.SourcePath?.EndsWith(Path.Combine("pages", "contact.blade.php")) == true
              && Route(pages, "/admin/users")?.SourcePath?.EndsWith(Path.Combine("users", "index.blade.php")) == true, Route(pages, "/contact")?.SourcePath ?? "");
        Check("laravel: /cache/clear is listed but never shot on its own", Route(pages, "/cache/clear")?.Risky == true);
        Check("laravel: APP_URL is the suggested address — and nothing else from .env comes out", r.SuggestedBaseUrl == "http://shop.test/"
              && !string.Join("", pages.Select(p => p.Title + p.Detail + p.Route)).Contains("SECRET", StringComparison.Ordinal), r.SuggestedBaseUrl ?? "");
        Check("laravel: the URL of a route on the site", Route(pages, "/admin/dashboard")?.UrlOn(r.SuggestedBaseUrl) == "http://shop.test/admin/dashboard");
        string bare = Path.Combine(temp, "my-shop");
        W(bare, ".env", "APP_URL=http://localhost\n");
        string laragon = Path.Combine(temp, "laragon");
        Directory.CreateDirectory(laragon);
        Check("laravel: Laravel's bare http://localhost + Laragon installed → Laragon's address for the folder",
              LaravelPages.AppUrl(bare, laragon) == "http://my-shop.test/" && LaravelPages.AppUrl(bare, null) == "http://localhost/", $"{LaravelPages.AppUrl(bare, laragon)} / {LaravelPages.AppUrl(bare, null)}");
    }

    // ───────────────────────────── the thumbnail store ─────────────────────────────

    static void ShotStore(string temp)
    {
        Console.WriteLine("Page gallery: thumbnail store");
        string data = Path.Combine(temp, "store-data");
        string project = Path.Combine(temp, "store-project");
        W(project, "index.html", "<title>x</title>");
        var store = new PageShotStore(data, project);
        Check("store: lives in the app's data folder, never in the project", store.Folder.StartsWith(data, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(Path.Combine(project, "UiStudio")));
        var e = store.Save("html:index.html", "desktop", new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, "http://localhost:1/", "Home", 200);
        Check("store: a saved shot is found again, per device", store.Get("html:index.html", "desktop") != null && store.Get("html:index.html", "mobile") == null && File.Exists(store.ImagePath(e)));
        var again = new PageShotStore(data, project);
        Check("store: kept across sessions", again.Get("html:index.html", "desktop")?.Title == "Home");
        var stampBefore = PageShotStore.ProjectStamp(project);
        Thread.Sleep(30);
        W(project, "index.html", "<title>changed</title>");
        File.SetLastWriteTimeUtc(Path.Combine(project, "index.html"), DateTime.UtcNow.AddSeconds(5));
        var stampAfter = PageShotStore.ProjectStamp(project);
        Check("store: an edit makes the project newer than its shots (they show as old and are re-taken)", stampAfter > stampBefore && stampAfter > e.CapturedUtc);
        again.Prune(new[] { "html:other.html" });
        Check("store: shots of pages that are gone are deleted", again.Get("html:index.html", "desktop") == null && !File.Exists(store.ImagePath(e)));
        Check("store: only http(s) web pages can be added", again.AddWebPage("https://example.com/x", null) && !again.AddWebPage("javascript:alert(1)", null) && !again.AddWebPage("file:///C:/Windows/win.ini", null));
        for (int i = 0; i < 60; i++) again.AddWebPage($"https://example.com/p{i}", null);
        Check("store: the web page list is capped", again.State.WebPages.Count == PageShotStore.MaxWebPages && again.State.WebPages[0].Url == "https://example.com/p59");
        Check("store: local hosts are visited on their own, the internet only when asked",
              PageShotStore.IsLocalHost("http://localhost:5173/") && PageShotStore.IsLocalHost("http://127.0.0.1:8000/x") && PageShotStore.IsLocalHost("http://shop.test/")
              && !PageShotStore.IsLocalHost("https://xman4289.com/") && !PageShotStore.IsLocalHost("https://example.test.evil.com/"));
        // a tampered index can't aim a delete outside the thumbnail folder
        string victim = Path.Combine(temp, "victim.txt");
        File.WriteAllText(victim, "keep me");
        string rel = Path.GetRelativePath(again.Folder, victim);
        File.WriteAllText(Path.Combine(again.Folder, "index.json"),
            "{\"Shots\":{\"a|desktop\":{\"File\":" + System.Text.Json.JsonSerializer.Serialize(rel) + "},\"b|desktop\":{\"File\":" + System.Text.Json.JsonSerializer.Serialize(victim) + "}}}");
        var tampered = new PageShotStore(data, project);
        tampered.ClearShots();
        Check("store: a tampered index never deletes a file outside its folder", File.Exists(victim) && tampered.ImagePath(new PageShotEntry { File = rel }).StartsWith(tampered.Folder, StringComparison.OrdinalIgnoreCase));
        File.WriteAllText(Path.Combine(again.Folder, "index.json"), "{ not json");
        Check("store: a damaged index starts over instead of crashing", new PageShotStore(data, project).State.Shots.Count == 0);
    }

    // ───────────────────────────── Flutter ─────────────────────────────

    static void DartReading()
    {
        Console.WriteLine("Page gallery: reading Dart");
        string root = Path.Combine(Path.GetTempPath(), "cx-dart-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            W(root, "pubspec.yaml", "name: x\ndependencies:\n  flutter:\n    sdk: flutter\n");
            W(root, "lib/broken.dart", "class Broken extends StatelessWidget {\n  Widget build(BuildContext c) {\n    return Column(children: [Text('a', style: ");
            W(root, "lib/tricky.dart", @"/* a /* nested */ comment */
class TrickyScreen extends StatelessWidget {
  const TrickyScreen({super.key});
  @override
  Widget build(BuildContext context) {
    final items = <Widget>[];
    final path = r'C:\temp\$notInterpolated';
    return Scaffold(
      appBar: AppBar(title: Text('Hi ${user.name} — $count new')),
      body: Column(children: [
        for (final i in list) Text('row'),
        ...[const Divider(), Text(path)],
        switch (mode) { Mode.a => const Text('A'), _ => Text('Other') },
        Builder<int>(builder: (context) => Text(a < b ? 'less' : 'more')),
      ]),
    );
  }
}
");
            FlutterProject? p = null;
            Exception? err = null;
            try { p = FlutterProject.Load(root, "en"); } catch (Exception ex) { err = ex; }
            Check("dart: a half-written file never breaks loading", p != null && err == null, err?.Message ?? "");
            var pages = p?.Pages() ?? new List<StudioPage>();
            var tricky = pages.FirstOrDefault(x => x.Detail == "TrickyScreen");
            var sk = tricky == null ? null : p!.Sketch(tricky);
            var texts = sk?.Root.Walk().Select(n => n.Text).Where(t => t != null).ToList() ?? new List<string?>();
            Check("dart: interpolation shows as …, a raw string stays raw", sk?.Title == "Hi … — … new" && texts.Contains(@"C:\temp\$notInterpolated"), sk?.Title + " | " + string.Join(" | ", texts));
            Check("dart: for / spread / switch / generic builder all read", texts.Count(t => t == "row") >= 2 && sk!.Root.Walk().Any(n => n.Kind == SketchKind.Divider)
                  && (texts.Contains("A") || texts.Contains("Other")) && (texts.Contains("less") || texts.Contains("more")), string.Join(" | ", texts));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static void FlutterScreens(string temp)
    {
        Console.WriteLine("Page gallery: Flutter screens, simulated");
        string root = Path.Combine(temp, "flutter-app");
        W(root, "pubspec.yaml", "name: demo\ndependencies:\n  flutter:\n    sdk: flutter\n  go_router: ^14.0.0\n");
        W(root, "l10n.yaml", "arb-dir: lib/l10n\ntemplate-arb-file: app_en.arb\noutput-class: AppLocalizations\n");
        W(root, "lib/l10n/app_en.arb", "{ \"homeTitle\": \"Welcome home\", \"@homeTitle\": {}, \"greeting\": \"Hello {name}\", \"items\": \"{count, plural, =0{no items} other{{count} items}}\" }");
        W(root, "lib/l10n/app_th.arb", "{ \"homeTitle\": \"ยินดีต้อนรับ\", \"greeting\": \"สวัสดี {name}\", \"items\": \"{count, plural, other{{count} รายการ}}\" }");
        W(root, "lib/main.dart", "void main() => runApp(MaterialApp.router(routerConfig: router, theme: ThemeData(colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xFF0061A4)))));\n");
        W(root, "lib/router.dart", @"final router = GoRouter(
  initialLocation: '/',
  routes: [
    GoRoute(path: '/', builder: (context, state) => const HomeScreen()),
    ShellRoute(
      builder: (context, state, child) => AppShell(child: child),
      routes: [
        GoRoute(path: '/feed', builder: (_, __) => const FeedScreen(), routes: [
          GoRoute(path: 'post/:id', builder: (_, s) => PostScreen(id: s.pathParameters['id']!)),
        ]),
      ],
    ),
    GoRoute(path: '/logout', builder: (_, __) => const LogoutScreen()),
  ],
);
");
        W(root, "lib/home_screen.dart", @"class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Scaffold(
      appBar: AppBar(
        title: Text(l10n.homeTitle),
        actions: [IconButton(icon: const Icon(Icons.search), onPressed: () {})],
      ),
      body: ListView(children: [
        MyCard(title: 'Today', child: Text(l10n.greeting('x'))),
        TextField(decoration: const InputDecoration(labelText: 'Email')),
        ElevatedButton(onPressed: () {}, child: const Text('Sign in')),
        if (loading) const CircularProgressIndicator(),
        Text(l10n.items(3)),
      ]),
      floatingActionButton: FloatingActionButton(onPressed: () {}, child: const Icon(Icons.add)),
    );
  }
}
");
        W(root, "lib/widgets/my_card.dart", @"class MyCard extends StatelessWidget {
  const MyCard({super.key, required this.title, this.actions = const [], this.padding = const EdgeInsets.all(8), required this.child});
  final String title;
  final List<Widget> actions;
  final EdgeInsets padding;
  final Widget child;

  @override
  Widget build(BuildContext context) => Card(
        child: Column(children: [
          Text(title, style: const TextStyle(fontSize: 20, fontWeight: FontWeight.bold)),
          child,
        ]),
      );
}
");
        W(root, "lib/feed_screen.dart", @"class FeedScreen extends ConsumerWidget {
  const FeedScreen({super.key});
  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final posts = ref.watch(postsProvider);
    return Scaffold(
      body: posts.when(
        data: (list) => ListView.builder(
          itemCount: list.length,
          itemBuilder: (c, i) => ListTile(leading: const Icon(Icons.person), title: const Text('Post'), subtitle: const Text('by me')),
        ),
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => const Text('Error'),
      ),
    );
  }
}
");
        W(root, "lib/app_shell.dart", @"class AppShell extends StatelessWidget {
  const AppShell({super.key, required this.child});
  final Widget child;
  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: child,
      bottomNavigationBar: NavigationBar(destinations: const [
        NavigationDestination(icon: Icon(Icons.home), label: 'Home'),
        NavigationDestination(icon: Icon(Icons.settings), label: 'Settings'),
      ]),
    );
  }
}
");
        W(root, "lib/post_screen.dart", @"class PostScreen extends StatefulWidget {
  const PostScreen({super.key, required this.id});
  final String id;
  @override
  State<PostScreen> createState() => _PostScreenState();
}
class _PostScreenState extends State<PostScreen> {
  bool _busy = true;
  @override
  Widget build(BuildContext context) {
    if (_busy && data == null) return const Center(child: CircularProgressIndicator());
    return Scaffold(appBar: AppBar(title: Text('Post ${widget.id}')), body: _body());
  }
  Widget _body() => const Column(children: [Text('Body text'), Divider()]);
}
");
        W(root, "lib/logout_screen.dart", "class LogoutScreen extends StatelessWidget { const LogoutScreen({super.key}); @override Widget build(BuildContext context) => const Scaffold(body: Text('Bye')); }\n");
        W(root, "lib/settings_page.dart", "class SettingsPage extends StatelessWidget { const SettingsPage({super.key}); @override Widget build(BuildContext context) => Scaffold(appBar: AppBar(title: const Text('Settings')), body: Column(children: const [SwitchListTile(title: Text('Dark mode'), value: true, onChanged: null)])); }\n");
        W(root, "lib/widgets/layout_page.dart", "class LayoutPage extends StatelessWidget { const LayoutPage({super.key, required this.children}); final List<Widget> children; @override Widget build(BuildContext context) => Column(children: children); }\n");

        var info = StudioProject.Detect(root);
        Check("flutter: detected as a simulated app (it can't run in the Studio's browser)", info.Kind == StudioProjectKind.Simulated && info.Framework == "flutter", info.Kind.ToString());
        var r = PageCatalog.Discover(info, "th");
        var pages = r.Pages;
        Check("flutter: go_router routes, nested relative paths", new[] { "/", "/feed", "/feed/post/:id", "/logout" }.All(x => Route(pages, x) != null), Routes(pages));
        Check("flutter: routes under a ShellRoute are in the shell", Route(pages, "/feed")?.InShell == true && Route(pages, "/feed/post/:id")?.InShell == true && Route(pages, "/")?.InShell == false);
        Check("flutter: a screen no route reaches is listed; widgets that wrap content are not",
              pages.Any(x => x.Detail == "SettingsPage" && x.Route == null) && !pages.Any(x => x.Detail is "LayoutPage" or "MyCard" or "AppShell"), Routes(pages));
        Check("flutter: /logout is flagged", Route(pages, "/logout")?.Risky == true);
        Check("flutter: each screen opens its own file", Route(pages, "/")?.SourcePath?.EndsWith("home_screen.dart") == true && Route(pages, "/")?.SourceLine == 1);

        var home = r.Flutter!.Sketch(Route(pages, "/")!)!;
        var nodes = home.Root.Walk().ToList();
        Check("sketch: the app bar title comes from the ARB file in the UI language", home.Title == "ยินดีต้อนรับ", home.Title ?? "");
        Check("sketch: a project widget is drawn with the caller's arguments (also after a 'const []' default)",
              nodes.Any(n => n.Kind == SketchKind.Heading && n.Text == "Today" && n.File!.EndsWith("my_card.dart") && n.UseFile!.EndsWith("home_screen.dart")),
              string.Join(" | ", nodes.Where(n => n.Text != null).Select(n => $"{n.Kind}:{n.Text}:{Path.GetFileName(n.File)}")));
        Check("sketch: ARB placeholders and plurals read as text", nodes.Any(n => n.Text == "สวัสดี …") && nodes.Any(n => n.Text == "… รายการ"), string.Join(" | ", nodes.Select(n => n.Text).Where(t => t != null)));
        Check("sketch: input label, button text, app bar action, FAB", nodes.Any(n => n.Kind == SketchKind.Input && n.Text == "Email") && nodes.Any(n => n.Kind == SketchKind.Button && n.Text == "Sign in")
              && nodes.Any(n => n.Kind == SketchKind.AppBar && n.ItemIcons.Contains("search")) && nodes.Any(n => n.Kind == SketchKind.Fab && n.Floating && n.Icon == "add"));
        Check("sketch: 'if (loading)' extra content is left out (a snapshot shows the content, not the spinner)", !nodes.Any(n => n.Kind == SketchKind.Progress));
        var signIn = nodes.First(n => n.Kind == SketchKind.Button);
        Check("sketch: a block remembers its line", signIn.File!.EndsWith("home_screen.dart") && signIn.Line == 15 && signIn.Target.Line == 15, $"{signIn.File}:{signIn.Line}");
        Check("sketch: the app's seed colour", home.Theme.Accent == 0xFF0061A4, home.Theme.Accent.ToString("X8"));

        var feed = r.Flutter.Sketch(Route(pages, "/feed")!)!;
        var fn = feed.Root.Walk().ToList();
        Check("sketch: a shell route is drawn inside its shell's nav bar", fn.Any(n => n.Kind == SketchKind.NavBar && n.Items.SequenceEqual(new[] { "Home", "Settings" })),
              string.Join(" | ", fn.Select(n => n.Kind + (n.Items.Count > 0 ? "[" + string.Join(",", n.Items) + "]" : ""))));
        Check("sketch: asyncValue.when draws the data branch — a few rows of its item, not the spinner",
              fn.Count(n => n.Kind == SketchKind.Tile && n.Text == "Post" && n.Text2 == "by me") == 3 && !fn.Any(n => n.Kind == SketchKind.Progress) && !fn.Any(n => n.Text == "Error"));
        var post = r.Flutter.Sketch(Route(pages, "/feed/post/:id")!)!;
        var pn = post.Root.Walk().ToList();
        Check("sketch: a State class's build, early 'loading' return skipped, a helper method and widget.field",
              post.Title == "Post …" && pn.Any(n => n.Text == "Body text") && pn.Any(n => n.Kind == SketchKind.Divider), post.Title + " | " + string.Join(" | ", pn.Select(n => n.Kind + ":" + n.Text)));
        var settings = r.Flutter.Sketch(pages.First(x => x.Detail == "SettingsPage"))!;
        Check("sketch: SwitchListTile is a tile with a toggle", settings.Root.Walk().Any(n => n.Kind == SketchKind.Tile && n.Text == "Dark mode" && n.Variant!.Contains("+toggle")));

        var en = PageCatalog.Discover(info, "en");
        Check("sketch: the same screen in English", en.Flutter!.Sketch(Route(en.Pages, "/")!)!.Title == "Welcome home");

        // drawn by the real SketchView
        var bmp = SaveSketch(home, null, 1);
        var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
        var colours = new HashSet<int>();
        for (int i = 0; i < px.Length; i += 4 * 7) colours.Add(px[i] << 16 | px[i + 1] << 8 | px[i + 2]);
        Check("render: the wireframe draws (phone-sized, many colours)", bmp.PixelWidth == 360 && bmp.PixelHeight == 760 && colours.Count > 12, $"{bmp.PixelWidth}x{bmp.PixelHeight}, {colours.Count} colours");
        var full = SaveSketch(feed, null, 1, fullHeight: true);
        Check("render: the stage grows to the whole screen", full.PixelHeight >= 760);
    }
}
