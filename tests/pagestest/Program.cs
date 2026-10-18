using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.UiStudio;
using CluadeX.ViewModels;
using CluadeX.Views;
using CluadeX.Views.UiStudio;
using Microsoft.Extensions.DependencyInjection;

namespace PagesTest;

/// <summary>
/// The UI Studio's page gallery, driven for real: CluadeX's own DI graph builds the shipping MainWindow (off-screen),
/// the Studio opens fixture projects and the gallery takes REAL thumbnails with its hidden browser —
///   · a static site: every page shot, colours checked in the saved JPEGs, the risky page never opened, a download
///     cancelled, alert()/window.open refused, a card click loads that page, an edit makes the shot old and re-takes
///     it, the mobile size shoots portrait;
///   · a web page added by hand;
///   · a Laravel site opened at a local address (the routes come from routes/web.php), and at an internet address
///     where nothing is visited on its own;
///   · a Flutter app: screens simulated from the code, the stage, a click on a block opens its line in the editor.
/// Pictures of the gallery and the stage are written next to the report for a human look.
/// No network beyond localhost, no model, no ~/.cluadex (portable.txt → &lt;exe&gt;\Data, wiped every run).
/// </summary>
internal static partial class Program
{
    static int Pass, Fail;
    static readonly List<string> Failures = new();
    static readonly StringBuilder Report = new();
    static readonly List<string> BlockedRequests = new();
    static ServiceProvider Sp = null!;
    static MainViewModel MainVm = null!;
    static Window MainWin = null!;
    static string Out = Path.Combine(AppContext.BaseDirectory, "pages-out");

    static string? LookAt;
    static bool LookRender;

    [STAThread]
    static int Main(string[] args)
    {
        // pagestest --look <folder>: open a real project READ-ONLY and save pictures of its gallery (+ the stage for an
        // app). Nothing is shot (no page is visited): only what needs no network is drawn.
        // pagestest --look <folder> --render: the same, with the screens of a Flutter app drawn for real (flutter test).
        if (args.Length is 2 or 3 && args[0] == "--look") { LookAt = args[1]; LookRender = args.Length == 3 && args[2] == "--render"; }
        var app = new CluadeX.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int rc = 2;
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try { rc = await RunAll(); }
            catch (Exception ex) { Log("FATAL " + ex); rc = 2; }
            finally
            {
                try { File.WriteAllText(Path.Combine(Out, "pages-report.md"), Report.ToString(), new UTF8Encoding(false)); } catch { }
                Console.WriteLine($"\nreport + pictures: {Out}");
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        Dispatcher.Run();
        Environment.Exit(rc);
        return rc;
    }

    // ═══════════════════════════════════════════ plumbing ═══════════════════════════════════════════

    static void Log(string line) { Console.WriteLine(line); Report.AppendLine(line); }

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) Pass++; else { Fail++; Failures.Add(name + (detail.Length > 0 ? " — " + detail : "")); }
        Log($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "   [" + detail + "]" : "")}");
    }

    static async Task Idle(int ms = 0)
    {
        for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (ms > 0) await Task.Delay(ms);
        for (int i = 0; i < 2; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    static async Task<bool> WaitUntil(Func<bool> cond, int timeoutMs = 20000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            await Task.Delay(60);
        }
        return cond();
    }

    static object? GetField(object o, string name)
    {
        for (var t = o.GetType(); t != null; t = t.BaseType)
            if (t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance) is { } f) return f.GetValue(o);
        return null;
    }

    static void SetField(object o, string name, object? value)
    {
        for (var t = o.GetType(); t != null; t = t.BaseType)
            if (t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance) is { } f) { f.SetValue(o, value); return; }
    }

    static object? Invoke(object o, string method, params object?[] args) =>
        o.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o, args);

    sealed class BlockingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (BlockedRequests) BlockedRequests.Add(request.RequestUri?.Host ?? "?");
            throw new HttpRequestException("pagestest-network-blocked: " + request.RequestUri?.Host);
        }
    }

    static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in Find<T>(c)) yield return x;
        }
    }

    static void SavePng(FrameworkElement el, string name)
    {
        try
        {
            el.UpdateLayout();
            int w = (int)Math.Ceiling(el.ActualWidth), h = (int)Math.Ceiling(el.ActualHeight);
            if (w <= 0 || h <= 0) { Log($"  (no picture: {name} has no size)"); return; }
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(el);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(Out, name));
            enc.Save(fs);
            Log($"  picture: {name} ({w}×{h})");
        }
        catch (Exception ex) { Log($"  (picture {name} failed: {ex.Message})"); }
    }

    /// <summary>The colour at the middle of a saved JPEG (and its size).</summary>
    static (Color C, int W, int H) JpegCentre(string path)
    {
        using var fs = File.OpenRead(path);
        var f = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var conv = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        conv.CopyPixels(new Int32Rect(conv.PixelWidth / 2, conv.PixelHeight * 2 / 3, 1, 1), px, 4, 0);
        return (Color.FromRgb(px[2], px[1], px[0]), conv.PixelWidth, conv.PixelHeight);
    }

    static bool Near(Color a, Color b, int tol = 40) => Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

    static void W(string root, string rel, string text)
    {
        string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    static string Page(string title, string colour, string extra = "") =>
        $"<!doctype html><html><head><title>{title}</title><style>html,body{{margin:0;height:100%;background:{colour}}}</style></head><body><h1 style=\"color:#fff;font:32px sans-serif;margin:24px\">{title}</h1>{extra}</body></html>";

    static StudioService Studio(UiStudioViewModel vm) => (StudioService)GetField(vm, "_studio")!;
    static List<PageCard> Cards(UiStudioViewModel vm) => (List<PageCard>)GetField(vm, "_allCards")!;
    static PageCard? Card(UiStudioViewModel vm, string route) => Cards(vm).FirstOrDefault(c => c.Page.Route == route || c.Page.Url == route);

    // ═══════════════════════════════════════════ run ═══════════════════════════════════════════

    static async Task<int> RunAll()
    {
        Directory.CreateDirectory(Out);
        Log("# UI Studio page gallery — real app, real browser\n");
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"))) { Log("FATAL portable.txt is missing — refusing to run against the real ~/.cluadex"); return 2; }
        string dataRoot = Path.Combine(AppContext.BaseDirectory, "Data");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);

        var services = new ServiceCollection();
        typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [services]);
        Sp = services.BuildServiceProvider();
        var settings = Sp.GetRequiredService<SettingsService>();
        bool portable = settings.DataRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);
        Check("setup: portable data root (never the user's ~/.cluadex)", portable, settings.DataRoot);
        if (!portable) return 2;
        Directory.CreateDirectory(settings.DataRoot);
        foreach (var (svc, field) in new (object, string)[]
                 {
                     (Sp.GetRequiredService<AutoUpdateService>(), "_httpClient"), (Sp.GetRequiredService<XmanLicenseService>(), "_httpClient"),
                     (Sp.GetRequiredService<HuggingFaceService>(), "_httpClient"), (Sp.GetRequiredService<DgxSparkService>(), "_probe"), (Sp.GetRequiredService<DgxSparkService>(), "_pull"),
                 })
            SetField(svc, field, new HttpClient(new BlockingHandler()) { Timeout = TimeSpan.FromSeconds(5) });
        settings.UpdateSettings(s =>
        {
            s.ActiveProvider = AiProviderType.Local;
            s.SelectedModelPath = null;
            s.SelectedModelName = null;
            s.AutoExecuteCode = false;
            s.Language = "th";
        });
        var loc = Sp.GetRequiredService<LocalizationService>();
        CluadeX.Services.LocalizedResources.Instance.Initialize(loc);
        loc.SetLanguage("th");

        MainWin = Sp.GetRequiredService<CluadeX.MainWindow>();
        MainVm = (MainViewModel)MainWin.DataContext;
        MainWin.WindowStartupLocation = WindowStartupLocation.Manual;
        MainWin.Left = -20000; MainWin.Top = -20000;
        MainWin.Width = 1440; MainWin.Height = 900;
        MainWin.ShowActivated = false;
        MainWin.ShowInTaskbar = false;
        MainWin.Show();
        Check("setup: chat VM finished its startup load", await WaitUntil(() => MainVm.ChatVM.CurrentSession != null));

        Invoke(MainVm, "NavigateTo", "UiStudio");
        await Idle(300);
        var view = await WaitFor(() => Find<UiStudioView>(MainWin).FirstOrDefault(v => v.IsVisible));
        Check("setup: the UI Studio page is on screen", view != null);
        if (view == null) return Finish();
        var vm = MainVm.UiStudioVM!;

        StaticXaml(loc);
        if (LookAt != null) { await Look(vm, view, LookAt, LookRender); return Finish(); }

        string fx = Path.Combine(AppContext.BaseDirectory, "fixture");
        if (Directory.Exists(fx)) Directory.Delete(fx, true);

        await StaticSite(vm, view, fx);
        await AddedWebPage(vm, fx);
        await LaravelSite(vm, view, fx);
        await FlutterApp(vm, view, fx);
        await FlutterReal(vm, view, fx);
        await FlutterTools(vm, view, fx);
        await FlutterParams(vm, view, fx);
        await FlutterHelpers(vm, view, fx);
        await FlutterSampleScreens(vm, view, fx);
        await FlutterTranslations(vm, view, fx);
        await WpfApp(vm, view, fx);

        return Finish();
    }

    static async Task<T?> WaitFor<T>(Func<T?> get, int timeoutMs = 15000) where T : class
    {
        T? v = null;
        await WaitUntil(() => (v = get()) != null, timeoutMs);
        return v;
    }

    /// <summary>What compiles but renders blank: every {services:Loc} key the Studio's XAML and gallery code use has a
    /// Thai value, every {StaticResource}/{DynamicResource} key exists, every {Binding} names a real property.</summary>
    static void StaticXaml(LocalizationService loc)
    {
        Log("\n## Static checks (UI Studio XAML)");
        string? repo = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CluadeX", "Views", "UiStudioView.xaml"))) { repo = d.FullName; break; }
        if (repo == null) { Check("static: repository found next to the harness", false); return; }
        string xaml = File.ReadAllText(Path.Combine(repo, "CluadeX", "Views", "UiStudioView.xaml"));
        string styles = File.ReadAllText(Path.Combine(repo, "CluadeX", "Views", "UiStudio", "StudioStyles.xaml"));
        string resources = File.ReadAllText(Path.Combine(repo, "CluadeX", "Resources", "Theme.xaml")) + File.ReadAllText(Path.Combine(repo, "CluadeX", "App.xaml")) + styles + xaml;
        string theme = File.ReadAllText(Path.Combine(repo, "CluadeX", "Resources", "Themes", "Midnight.xaml")) + resources;
        string code = File.ReadAllText(Path.Combine(repo, "CluadeX", "ViewModels", "UiStudioViewModel.Pages.cs"))
                      + File.ReadAllText(Path.Combine(repo, "CluadeX", "ViewModels", "UiStudioViewModel.Render.cs"))
                      + File.ReadAllText(Path.Combine(repo, "CluadeX", "ViewModels", "UiStudioViewModel.Tools.cs"))
                      + File.ReadAllText(Path.Combine(repo, "CluadeX", "ViewModels", "UiStudioViewModel.Session.cs"))
                      + File.ReadAllText(Path.Combine(repo, "CluadeX", "Services", "UiStudio", "StudioService.cs"));
        // the edit engine's refusals ("studio.fedit." + reason) and the palette's names / samples ("studio.fedit.palette." + key)
        string engine = File.ReadAllText(Path.Combine(repo, "CluadeX", "Services", "UiStudio", "Sketch", "FlutterCodeEdit.cs"));
        // the renderer and the translations' editor hand back keys as plain strings ("studio.render.noFlutter",
        // "studio.fedit.arb.bad") that the view model shows with T()
        string renderer = File.ReadAllText(Path.Combine(repo, "CluadeX", "Services", "UiStudio", "Sketch", "FlutterRenderer.cs"))
                          + File.ReadAllText(Path.Combine(repo, "CluadeX", "Services", "UiStudio", "Sketch", "FlutterL10n.cs"));
        loc.SetLanguage("th");
        var keys = System.Text.RegularExpressions.Regex.Matches(xaml, @"\{services:Loc\s+([\w.]+)\}").Select(m => m.Groups[1].Value)
            .Concat(System.Text.RegularExpressions.Regex.Matches(code, @"\bT\(""([\w.]+)""").Select(m => m.Groups[1].Value))
            .Concat(System.Text.RegularExpressions.Regex.Matches(renderer, @"""(studio\.[\w.]+)""").Select(m => m.Groups[1].Value))
            .Concat(System.Text.RegularExpressions.Regex.Matches(engine, @"Fail\(""([\w.]+)""\)").Select(m => "studio.fedit." + m.Groups[1].Value))
            .Concat(System.Text.RegularExpressions.Regex.Matches(engine, @"why = ""([\w.]+)""").Select(m => "studio.fedit." + m.Groups[1].Value))
            .Concat(MainVm.UiStudioVM!.Palette.Select(p => "studio.fedit.palette." + p.Key))
            .Concat(new[] { "studio.fedit.sample.text", "studio.fedit.sample.button", "studio.fedit.sample.link", "studio.fedit.sample.title", "studio.fedit.sample.subtitle", "studio.fedit.sample.field", "studio.fedit.changedMeanwhile" })
            .Where(k => !k.EndsWith('.')).Distinct().ToList();
        // (product names stay in English in Thai too: "UI Studio", "Node Graph", "WIDGET", "HTTP 404")
        var missing = keys.Where(k => loc.T(k) == k || !System.Text.RegularExpressions.Regex.IsMatch(loc.T(k), "[\u0E00-\u0E7F]") && k is not ("studio.title" or "graph.title" or "studio.stage.widget" or "studio.pages.httpError" or "studio.kind.vite")).ToList();
        Check("static: every localized key used by the Studio's XAML and the gallery has a Thai value", missing.Count == 0, string.Join(", ", missing));
        var missingRes = System.Text.RegularExpressions.Regex.Matches(xaml, @"\{StaticResource\s+([\w.]+)\}").Select(m => m.Groups[1].Value).Distinct()
            .Where(k => !resources.Contains($"x:Key=\"{k}\"", StringComparison.Ordinal)).ToList();
        Check("static: every {StaticResource} exists", missingRes.Count == 0, string.Join(", ", missingRes));
        var missingDyn = System.Text.RegularExpressions.Regex.Matches(xaml, @"\{DynamicResource\s+([\w.]+)\}").Select(m => m.Groups[1].Value).Distinct()
            .Where(k => !theme.Contains($"x:Key=\"{k}\"", StringComparison.Ordinal)).ToList();
        Check("static: every {DynamicResource} is a theme token", missingDyn.Count == 0, string.Join(", ", missingDyn));
        var types = new[] { typeof(UiStudioViewModel), typeof(PageCard), typeof(PageSection), typeof(PageRow), typeof(LayerItem), typeof(StyleGroup), typeof(StyleField),
            typeof(AttrField), typeof(TemplateCard), typeof(CluadeX.Services.UiStudio.SourceCandidate), typeof(CluadeX.Services.UiStudio.StudioTemplate),
            typeof(WidgetPaletteItem), typeof(SwatchItem), typeof(ChoiceItem) };
        var badBindings = System.Text.RegularExpressions.Regex.Matches(xaml, @"\{Binding\s+(?:Path=)?([\w.]+)").Select(m => m.Groups[1].Value)
            .Select(p => p.StartsWith("DataContext.", StringComparison.Ordinal) ? p["DataContext.".Length..] : p)
            .Select(p => p.Split('.')[0]).Distinct()
            .Where(p => !types.Any(t => t.GetProperty(p) != null)).ToList();
        Check("static: every {Binding} names a real property", badBindings.Count == 0, string.Join(", ", badBindings));
    }

    static async Task Look(UiStudioViewModel vm, UiStudioView view, string folder, bool render)
    {
        Log($"\n## A look at {folder}{(render ? " — screens drawn by Flutter" : "")}");
        vm.Shooter = null;   // never visit a page of a real project from a test
        vm.RenderConsentOverride = render;
        if (render) CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Add("flutter.bat");
        vm.OpenProject(folder, autoStart: false);
        vm.ShowPages = true;
        await WaitUntil(() => Cards(vm).Count > 0, 60000);
        if (vm.IsSimulated) await WaitUntil(() => Cards(vm).All(c => !c.IsScreen || c.State is "sketch" or "failed" or "done" or "rendering"), 120000);
        if (render && vm.IsSimulated)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await WaitUntil(() => vm.IsRendering, 30000);
            await WaitUntil(() => !vm.IsRendering, 600000);
            Log($"  drawn in {sw.Elapsed.TotalSeconds:0.0}s{(vm.CompileError != null ? " — COMPILE ERROR: " + vm.CompileError : "")}");
            foreach (var c in Cards(vm).Where(c => c.IsScreen))
                Log($"  {c.Page.Route ?? c.Page.Detail,-28} {c.State,-9} {c.StateText}");
        }
        await Idle(800);
        Log($"  {Cards(vm).Count} pages · {vm.PagesSummary}");
        SavePng((FrameworkElement)view.FindName("PagesPanel"), "look-gallery.png");
        // an app's stage: its first routed screen (a desktop app has no routes: its first view)
        if (vm.IsSimulated && (Cards(vm).FirstOrDefault(c => c.HasSketch && c.Page.Route != null) ?? Cards(vm).FirstOrDefault(c => c.HasSketch)) is { } first)
        {
            vm.OpenCardCommand.Execute(Cards(vm).FirstOrDefault(c => c.Page.Route == "/users") ?? first);
            await Idle(600);
            SavePng(MainWin.Content as FrameworkElement ?? view, "look-studio.png");
            if (render)
            {
                vm.ShowRealRender = false;
                await Idle(300);
                SavePng(MainWin.Content as FrameworkElement ?? view, "look-studio-wireframe.png");
                vm.ShowRealRender = true;
            }
        }
    }

    static int Finish()
    {
        lock (BlockedRequests) Log($"\nnetwork: {BlockedRequests.Count} app request(s) refused");
        Log($"\n## Result: {Pass} pass / {Fail} FAIL");
        foreach (var f in Failures) Log("  - " + f);
        return Fail == 0 ? 0 : 1;
    }

    // ═══════════════════════════════════════════ a static site ═══════════════════════════════════════════

    static async Task StaticSite(UiStudioViewModel vm, UiStudioView view, string fx)
    {
        Log("\n## Static site");
        string site = Path.Combine(fx, "site");
        W(site, "index.html", Page("Home", "#E53935"));
        W(site, "about.html", Page("About", "#43A047"));
        W(site, "blog/post.html", Page("Post", "#1E88E5"));
        W(site, "logout.html", Page("Logout", "#000000", "<script>document.title='LOGGED OUT'</script>"));
        W(site, "getfile.html", Page("Get the file", "#FB8C00", "<script>setTimeout(()=>{const a=document.createElement('a');a.href='file.zip';a.download='file.zip';document.body.appendChild(a);a.click()},50)</script>"));
        W(site, "file.zip", "PK not really a zip");
        W(site, "noisy.html", Page("Noisy", "#6D4C41", "<script>alert('hi');window.open('https://example.invalid/');confirm('sure?')</script>"));

        var calls = new List<string>();
        var original = vm.Shooter!;
        vm.Shooter = (u, w, h, ct) => { lock (calls) calls.Add(u); return original(u, w, h, ct); };

        vm.OpenProject(site, autoStart: true);
        Check("static: the preview server runs", await WaitUntil(() => vm.IsServing && Studio(vm).CurrentBaseUrl != null));
        vm.ShowPages = true;
        Check("static: the gallery lists every page", await WaitUntil(() => Cards(vm).Count == 6), string.Join(", ", Cards(vm).Select(c => c.Page.Route)));
        bool shot = await WaitUntil(() => new[] { "/", "/about.html", "/blog/post.html", "/getfile.html", "/noisy.html" }.All(r => Card(vm, r)?.State is "done" or "failed"), 90000);
        Check("static: the safe pages are shot on their own", shot, string.Join(" · ", Cards(vm).Select(c => $"{c.Page.Route}={c.State}")));
        var store = (CluadeX.Services.UiStudio.Pages.PageShotStore)GetField(vm, "_store")!;
        foreach (var (route, colour) in new[] { ("/", "#E53935"), ("/about.html", "#43A047"), ("/blog/post.html", "#1E88E5") })
        {
            var card = Card(vm, route)!;
            var e = store.Get(card.Page.Id, "desktop");
            if (e == null) { Check($"static: {route} saved", false); continue; }
            var (c, w, h) = JpegCentre(store.ImagePath(e));
            Check($"static: {route}'s thumbnail is that page ({colour})", Near(c, (Color)ColorConverter.ConvertFromString(colour)) && w > h, $"{c} {w}×{h}");
        }
        Check("static: the risky page is never opened", Card(vm, "/logout.html")?.State == "risky" && !calls.Any(u => u.Contains("logout", StringComparison.OrdinalIgnoreCase)),
              $"{Card(vm, "/logout.html")?.State} · calls: {string.Join(", ", calls)}");
        var shooter = GetField(view, "_shooter") as PageShooter;
        Check("static: the download a page started was cancelled", shooter?.BlockedDownloads >= 1, $"{shooter?.BlockedDownloads}");
        Check("static: alert / confirm did not hang the queue and window.open was refused", Card(vm, "/noisy.html")?.State == "done" && shooter?.BlockedWindows == 0,
              $"{Card(vm, "/noisy.html")?.State} windows={shooter?.BlockedWindows}");
        await Idle(300);
        var panel = (FrameworkElement)view.FindName("PagesPanel");
        int images = Find<Image>(panel).Count(i => i.Source != null && i.IsVisible);
        Check("static: the cards show their pictures", images >= 4, $"{images}");
        SavePng(panel, "gallery-static.png");
        Check("static: the summary counts the pictures", vm.PagesSummary.Contains("6", StringComparison.Ordinal), vm.PagesSummary);

        // a click loads the page
        vm.OpenCardCommand.Execute(Card(vm, "/about.html"));
        Check("static: a card click closes the gallery and opens that page", !vm.ShowPages && await WaitUntil(() => vm.Url.EndsWith("/about.html", StringComparison.Ordinal)), vm.Url);
        Check("static: the page shown now is marked", await WaitUntil(() => Card(vm, "/about.html")?.IsCurrent == true));

        // an edit makes the shot old; reopening the gallery re-takes it
        W(site, "about.html", Page("About", "#8E24AA"));
        File.SetLastWriteTimeUtc(Path.Combine(site, "about.html"), DateTime.UtcNow.AddSeconds(5));
        vm.ShowPages = true;   // back to the gallery: no Refresh pressed — the change is noticed on its own
        bool retaken = await WaitUntil(() =>
        {
            var c = Card(vm, "/about.html");
            if (c == null || c.State != "done" || c.IsStale) return false;
            var e = store.Get(c.Page.Id, "desktop");
            return e != null && Near(JpegCentre(store.ImagePath(e)).C, Color.FromRgb(0x8E, 0x24, 0xAA));
        }, 60000);
        Check("static: after an edit the page is shot again (old picture shown until then)", retaken, Card(vm, "/about.html")?.State ?? "");

        // the mobile size shoots portrait
        vm.Device = "mobile";
        bool portrait = await WaitUntil(() => Card(vm, "/") is { State: "done" } c && store.Get(c.Page.Id, "mobile") is { } e && JpegCentre(store.ImagePath(e)) is var (_, w, h) && h > w, 60000);
        Check("static: the mobile width gives portrait thumbnails", portrait);
        Check("static: phone-shaped cards", vm.IsPhoneLayout && vm.CardWidth < 200);
        SavePng(panel, "gallery-static-mobile.png");
        vm.Device = "desktop";
        vm.ShowPages = false;
        vm.Shooter = original;
    }

    // ═══════════════════════════════════════════ a web page added by hand ═══════════════════════════════════════════

    static async Task AddedWebPage(UiStudioViewModel vm, string fx)
    {
        Log("\n## A web page added by hand");
        string other = Path.Combine(fx, "other-site");
        W(other, "x.html", Page("Another site", "#00897B"));
        using var server = new StudioStaticServer(other);
        server.Start();
        vm.ShowPages = true;
        vm.NewWebUrl = server.BaseUrl + "x.html";
        vm.AddWebPageCommand.Execute(null);
        var card = await WaitFor(() => Cards(vm).FirstOrDefault(c => c.IsExternal && c.Page.Url == server.BaseUrl + "x.html"));
        Check("web page: it joins the gallery", card != null);
        Check("web page: it is shot, and named by its own title", card != null && await WaitUntil(() => card.State == "done" && card.Title == "Another site", 40000), $"{card?.State} '{card?.Title}'");
        vm.RemoveWebPageCommand.Execute(Cards(vm).First(c => c.IsExternal));
        Check("web page: it can be removed again", !Cards(vm).Any(c => c.IsExternal));
        vm.ShowPages = false;
    }

    // ═══════════════════════════════════════════ a Laravel site ═══════════════════════════════════════════

    static async Task LaravelSite(UiStudioViewModel vm, UiStudioView view, string fx)
    {
        Log("\n## Laravel site");
        string root = Path.Combine(fx, "laravel");
        W(root, "artisan", "php");
        W(root, "composer.json", "{\"require\":{\"laravel/framework\":\"^11\"}}");
        W(root, "package.json", "{\"scripts\":{\"dev\":\"vite\"}}");
        W(root, ".env", "APP_URL=http://my-shop.test\n");
        W(root, "routes/web.php", "<?php\nRoute::view('/', 'welcome');\nRoute::view('/about', 'about');\nRoute::get('/cache/clear', fn () => view('ok'));\n");
        W(root, "resources/views/welcome.blade.php", "<title>Shop home</title>");
        W(root, "resources/views/about.blade.php", "@section('title', 'เกี่ยวกับร้าน')");
        // "where the site runs": a local server answering those routes
        string running = Path.Combine(fx, "laravel-running");
        W(running, "index.html", Page("Shop home", "#3949AB"));
        W(running, "about.html", Page("About the shop", "#C0CA33"));
        using var server = new StudioStaticServer(running);
        server.Start();

        var calls = new List<string>();
        var original = vm.Shooter!;
        vm.Shooter = (u, w, h, ct) => { lock (calls) calls.Add(u); return original(u, w, h, ct); };

        vm.OpenProject(root, autoStart: false);
        Check("laravel: opened as a server site, nothing started", vm.IsServerSite && !Studio(vm).IsServing);
        vm.ShowPages = true;
        Check("laravel: the routes are the pages", await WaitUntil(() => Cards(vm).Count == 3), string.Join(", ", Cards(vm).Select(c => c.Page.Route)));
        Check("laravel: APP_URL is the suggested address", vm.BaseUrlText == "http://my-shop.test/", vm.BaseUrlText);
        Check("laravel: titles come from the Blade views", Card(vm, "/about")?.Title == "เกี่ยวกับร้าน");
        vm.BaseUrlText = server.BaseUrl;
        vm.ApplyBaseUrlCommand.Execute(null);
        bool done = await WaitUntil(() => Card(vm, "/")?.State == "done" && Card(vm, "/about")?.State == "done", 60000);
        Check("laravel: pages are shot where the site runs", done, string.Join(" · ", Cards(vm).Select(c => $"{c.Page.Route}={c.State}")));
        Check("laravel: /cache/clear is not opened", !calls.Any(u => u.Contains("cache", StringComparison.Ordinal)) && Card(vm, "/cache/clear")?.State == "risky");
        SavePng((FrameworkElement)view.FindName("PagesPanel"), "gallery-laravel.png");

        // on the internet: nothing is visited on its own
        int before;
        lock (calls) before = calls.Count;
        vm.BaseUrlText = "https://shop.example.invalid/";
        vm.ApplyBaseUrlCommand.Execute(null);
        await Idle(1500);
        Check("laravel: an internet address is never visited on its own", calls.Count == before && vm.IsRemoteSite && Card(vm, "/")?.State == "manual",
              $"calls {calls.Count - before} · {Card(vm, "/")?.State}");
        vm.ShowPages = false;
        vm.Shooter = original;
    }

    // ═══════════════════════════════════════════ a Flutter app ═══════════════════════════════════════════

    static async Task FlutterApp(UiStudioViewModel vm, UiStudioView view, string fx)
    {
        Log("\n## Flutter app (simulated)");
        string root = Path.Combine(fx, "flutter");
        W(root, "pubspec.yaml", "name: demo\ndependencies:\n  flutter:\n    sdk: flutter\n");
        W(root, "lib/main.dart", "void main() => runApp(MaterialApp.router(routerConfig: router, theme: ThemeData(colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xFF006C4C)))));\n");
        W(root, "lib/router.dart", "final router = GoRouter(routes: [\n  GoRoute(path: '/', builder: (c, s) => const HomeScreen()),\n  GoRoute(path: '/profile', builder: (c, s) => const ProfileScreen()),\n]);\n");
        W(root, "lib/home_screen.dart", @"class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key});
  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('หน้าแรก')),
      body: Column(children: [
        const Text('ยินดีต้อนรับกลับมา'),
        ElevatedButton(onPressed: () {}, child: const Text('เริ่มเลย')),
        const ListTile(leading: Icon(Icons.person), title: Text('โปรไฟล์'), subtitle: Text('ดูและแก้ข้อมูล')),
      ]),
      floatingActionButton: FloatingActionButton(onPressed: () {}, child: const Icon(Icons.add)),
    );
  }
}
");
        W(root, "lib/profile_screen.dart", "class ProfileScreen extends StatelessWidget {\n  const ProfileScreen({super.key});\n  @override\n  Widget build(BuildContext context) => Scaffold(appBar: AppBar(title: const Text('โปรไฟล์')), body: const Column(children: [TextField(decoration: InputDecoration(labelText: 'ชื่อ'))]));\n}\n");

        vm.RenderConsentOverride = false;   // this fixture is not a buildable app: wireframes only (FlutterReal draws one for real)
        vm.OpenProject(root, autoStart: true);
        Check("flutter: opened as simulated screens, the gallery opens by itself", vm.IsSimulated && vm.ShowPages);
        bool sketched = await WaitUntil(() => Cards(vm).Count == 2 && Cards(vm).All(c => c.HasSketch), 30000);
        Check("flutter: every screen is drawn from its code", sketched, string.Join(", ", Cards(vm).Select(c => $"{c.Page.Route}:{c.State}")));
        Check("flutter: a card is named by the screen's own title", Card(vm, "/")?.Title == "หน้าแรก", Card(vm, "/")?.Title ?? "");
        Check("flutter: the live browser is hidden (nothing to run)", ((FrameworkElement)view.FindName("Frame")).Visibility != Visibility.Visible);
        await Idle(300);
        SavePng((FrameworkElement)view.FindName("PagesPanel"), "gallery-flutter.png");

        vm.OpenCardCommand.Execute(Card(vm, "/"));
        Check("flutter: a click opens the screen on the stage", !vm.ShowPages && vm.StageCard == Card(vm, "/") && vm.StageSketch != null);
        await Idle(400);
        var stage = (FrameworkElement)view.FindName("Stage");
        SavePng(stage, "stage-flutter.png");
        var button = vm.StageSketch!.Root.Walk().First(n => n.Kind == CluadeX.Services.UiStudio.Sketch.SketchKind.Button);
        var hit = HitAt((SketchView)view.FindName("StageView"), button);
        Check("flutter: a click where the button is drawn hits the button (not the column around it)", ReferenceEquals(hit, button), hit?.ToString() ?? "nothing");
        vm.PickStageNode(button, open: false);
        Check("flutter: picking a block shows where it is written", vm.StageNodeWhere == "lib/home_screen.dart:9" && vm.StageNodeText == "เริ่มเลย", $"{vm.StageNodeWhere} '{vm.StageNodeText}'");
        vm.OpenStageNodeCommand.Execute(null);
        bool opened = await WaitUntil(() => MainVm.CodeEditorVM.ActiveTab?.FullPath?.EndsWith("home_screen.dart", StringComparison.OrdinalIgnoreCase) == true, 15000);
        Check("flutter: 'open its line' opens the screen's file in the editor", opened, MainVm.CodeEditorVM.ActiveTab?.FullPath ?? "");
        string prompt = (string)Invoke(vm, "BuildStagePrompt", "make the button bigger", false)!;
        Check("flutter: the AI request names the screen, its file and the picked block",
              prompt.Contains("HomeScreen") && prompt.Contains("lib/home_screen.dart") && prompt.Contains("ElevatedButton") && prompt.Contains(":9"), prompt);
        Invoke(MainVm, "NavigateTo", "UiStudio");
        await Idle(200);
        Check("flutter: with real drawing declined the cards keep their wireframes", Cards(vm).All(c => c.State == "sketch" && !c.HasThumb && c.ShowSketch),
              string.Join(", ", Cards(vm).Select(c => $"{c.Page.Route}:{c.State}")));
        vm.RenderConsentOverride = null;
    }

    // ═══════════════════════════════════════════ a Flutter app drawn for real ═══════════════════════════════════════════

    const string RealHome = @"import 'package:flutter/material.dart';

class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('หน้าแรก')),
      body: Column(
        children: [
          Container(height: 160, color: const Color(0xFF1565C0)),
          const Padding(padding: EdgeInsets.all(16), child: Text('ยินดีต้อนรับกลับมา')),
          ElevatedButton(onPressed: () {}, child: const Text('เริ่มเลย')),
        ],
      ),
    );
  }
}
";

    /// <summary>A small real Flutter app (it compiles): Flutter draws its screens with flutter test, the pictures are
    /// kept, a click on the picture finds the exact line, an edit draws again by itself, a compile error and an
    /// overflow are reported and offered to the AI. Skipped when Flutter or its offline package cache is missing.</summary>
    static async Task FlutterReal(UiStudioViewModel vm, UiStudioView view, string fx)
    {
        Log("\n## Flutter app drawn for real (flutter test)");
        string? flutter = CluadeX.Services.UiStudio.Sketch.FlutterRenderer.FindFlutter();
        if (flutter == null) { Log("  (skipped: Flutter is not installed on this PC)"); return; }
        string root = Path.Combine(fx, "flutter-real");
        W(root, "pubspec.yaml", "name: cxdemo\nenvironment:\n  sdk: '>=3.0.0 <4.0.0'\ndependencies:\n  flutter:\n    sdk: flutter\ndev_dependencies:\n  flutter_test:\n    sdk: flutter\nflutter:\n  uses-material-design: true\n");
        W(root, "lib/main.dart", "import 'package:flutter/material.dart';\nimport 'home_screen.dart';\nimport 'wide_screen.dart';\n\nvoid main() => runApp(MaterialApp(\n      theme: ThemeData(colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xFF006C4C))),\n      home: const HomeScreen(),\n      routes: {'/wide': (c) => const WideScreen()},\n    ));\n");
        W(root, "lib/home_screen.dart", RealHome);
        // too wide for a 390-wide phone: Flutter reports the overflow
        W(root, "lib/wide_screen.dart", "import 'package:flutter/material.dart';\n\nclass WideScreen extends StatelessWidget {\n  const WideScreen({super.key});\n\n  @override\n  Widget build(BuildContext context) => Scaffold(\n        body: Row(children: [Container(width: 520, height: 40, color: const Color(0xFFE53935))]),\n      );\n}\n");
        // packages from the local cache only — the harness never touches the network
        var pub = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{flutter}\" pub get --offline\"")
        { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var pubOut = pub.StandardOutput.ReadToEndAsync();
        var pubErr = pub.StandardError.ReadToEndAsync();
        await pub.WaitForExitAsync();
        if (pub.ExitCode != 0 || !File.Exists(Path.Combine(root, ".dart_tool", "package_config.json")))
        {
            Log("  (skipped: `flutter pub get --offline` could not resolve the SDK's packages from the cache) " + (await pubErr).Trim().Split('\n').LastOrDefault());
            return;
        }
        _ = await pubOut;

        CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Add("flutter.bat");
        vm.RenderConsentOverride = true;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            vm.OpenProject(root, autoStart: true);
            bool drawn = await WaitUntil(() => Cards(vm).Count == 2 && Cards(vm).All(c => c.State == "done" && c.HasThumb), 300000);
            Check("real: Flutter draws every screen and the gallery keeps the pictures", drawn,
                  $"{sw.Elapsed.TotalSeconds:0}s · " + string.Join(", ", Cards(vm).Select(c => $"{c.Page.Route}:{c.State} '{c.StateText}'")) + (vm.CompileError != null ? " COMPILE " + vm.CompileError : ""));
            if (!drawn) return;
            var store = (CluadeX.Services.UiStudio.Pages.PageShotStore)GetField(vm, "_store")!;
            var home = Card(vm, "/")!;
            var entry = store.Get(home.Page.Id, "app");
            Check("real: the picture is kept on disk with its widget map", entry != null && File.Exists(store.ImagePath(entry)) && File.Exists(store.SidecarPath(entry)));
            var (blue, w, h) = JpegAt(store.ImagePath(entry!), 0.5, 0.17);
            Check("real: it is the app's own drawing (the blue band where the code puts it)", Near(blue, Color.FromRgb(0x15, 0x65, 0xC0)) && h > w, $"{blue} {w}×{h}");
            Check("real: the card shows the picture, not the wireframe", home.HasThumb && !home.ShowSketch);
            // Thai drawn with a real font: the test engine's own font draws one empty box per letter (1 em each)
            var homeBoxes = UiStudioViewModel.ReadRenderSidecarPublic(store.ReadSidecar(entry!));
            var welcome = homeBoxes.FirstOrDefault(b => b.Widget == "Text" && b.Line == 13);
            Check("real: Thai text is drawn with a real font (not a box per letter)", welcome != null && welcome.W < 0.6 * 14 * "ยินดีต้อนรับกลับมา".Length && welcome.W > 40,
                  welcome == null ? "no box" : $"{welcome.W:0} px for {"ยินดีต้อนรับกลับมา".Length} letters");
            var wide = Card(vm, "/wide")!;
            Check("real: an overflow is reported on its card", wide.HasProblems, $"{wide.Problems} · {wide.StateText}");
            await Idle(300);
            SavePng((FrameworkElement)view.FindName("PagesPanel"), "gallery-real.png");

            vm.OpenCardCommand.Execute(home);
            await Idle(400);
            Check("real: the stage shows Flutter's picture", vm.StageShowsRender && vm.StageBoxes.Count > 5, $"{vm.StageShowsRender} boxes={vm.StageBoxes.Count}");
            var button = vm.StageBoxes.FirstOrDefault(b => b.Widget == "ElevatedButton");
            Check("real: the button's place on the picture points at its line", button != null && button.File.EndsWith("home_screen.dart", StringComparison.OrdinalIgnoreCase) && button.Line == 14,
                  button == null ? "no box" : $"{button.File}:{button.Line} @{button.X:0},{button.Y:0} {button.W:0}×{button.H:0}");
            var render = (RenderStageView)view.FindName("RenderView");
            render.UpdateLayout();
            if (button != null)
            {
                double s = render.ActualWidth / 390.0;
                var hit = render.HitTestBox(new Point((button.X + button.W / 2) * s, (button.Y + button.H / 2) * s));
                Check("real: a click on the button on the picture finds the button (not the screen around it)", hit?.Line == 14,
                      hit == null ? "nothing" : $"{hit.Widget} :{hit.Line}");
                vm.PickStageBox(button, open: false);
                Check("real: picking it shows where it is written", vm.StageNodeWhere == "lib/home_screen.dart:14", vm.StageNodeWhere ?? "");
                string prompt = (string)Invoke(vm, "BuildStagePrompt", "make the button wider", false)!;
                Check("real: the AI request names the exact widget and line", prompt.Contains("ElevatedButton") && prompt.Contains("home_screen.dart:14"), prompt);
            }
            SavePng((FrameworkElement)view.FindName("Stage"), "stage-real.png");
            vm.ShowRealRender = false;
            Check("real: the switch shows the wireframe instead", vm.StageShowsSketch && !vm.StageShowsRender);
            vm.ShowRealRender = true;

            // the picture follows the code: a save draws the stage screen again by itself
            var before = entry!.CapturedUtc;
            string file = Path.Combine(root, "lib", "home_screen.dart");
            File.WriteAllText(file, RealHome.Replace("0xFF1565C0", "0xFF2E7D32"));
            bool redrawn = await WaitUntil(() => store.Get(home.Page.Id, "app")?.CapturedUtc > before && !vm.IsRendering, 300000);
            var (green, _, _) = redrawn ? JpegAt(store.ImagePath(store.Get(home.Page.Id, "app")!), 0.5, 0.17) : (Colors.Black, 0, 0);
            Check("real: an edit in the code draws the screen again by itself, with the change", redrawn && Near(green, Color.FromRgb(0x2E, 0x7D, 0x32)), $"{green}");

            // a mistake in the code: the compiler's message is shown and handed to the AI on request
            File.WriteAllText(file, RealHome.Replace("const Text('เริ่มเลย')", "const Text('เริ่มเลย'"));
            bool caught = await WaitUntil(() => vm.HasCompileError && !vm.IsRendering, 300000);
            Check("real: a compile error is caught with the compiler's message", caught && vm.CompileError!.Contains("home_screen.dart", StringComparison.OrdinalIgnoreCase), vm.CompileError ?? "");
            Check("real: 'ask AI to fix' is offered for it", vm.FixProblemsCommand.CanExecute(null));
            Check("real: the last good picture stays on the stage", vm.StageShowsRender);
            File.WriteAllText(file, RealHome);
            bool cleared = await WaitUntil(() => !vm.HasCompileError && !vm.IsRendering && home.State == "done", 300000);
            Check("real: fixing the code clears it and draws again", cleared, $"{home.State} {vm.CompileError}");
            SavePng((FrameworkElement)view.FindName("Stage"), "stage-real-after.png");
        }
        finally
        {
            vm.RenderConsentOverride = null;
            CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Remove("flutter.bat");
        }
    }

    /// <summary>The colour at a place (fractions of width / height) of a saved JPEG, and its size.</summary>
    static (Color C, int W, int H) JpegAt(string path, double fx, double fy)
    {
        using var fs = File.OpenRead(path);
        var f = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var conv = new FormatConvertedBitmap(f, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        conv.CopyPixels(new Int32Rect((int)(conv.PixelWidth * fx), (int)(conv.PixelHeight * fy), 1, 1), px, 4, 0);
        return (Color.FromRgb(px[2], px[1], px[0]), conv.PixelWidth, conv.PixelHeight);
    }

    /// <summary>The block the stage finds at the centre of where it drew <paramref name="n"/> — the hit-test a mouse
    /// click on the stage goes through.</summary>
    static CluadeX.Services.UiStudio.Sketch.SketchNode? HitAt(SketchView v, CluadeX.Services.UiStudio.Sketch.SketchNode n)
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var t = typeof(SketchView);
        if (t.GetMethod("Rect", Private)!.Invoke(v, new object[] { n }) is not Rect r) return null;
        var (s, x, y) = ((double, double, double))t.GetMethod("Fit", Private)!.Invoke(v, null)!;
        var p = new Point(x + (r.X + r.Width / 2) * s, y + (r.Y + r.Height / 2) * s);
        return (CluadeX.Services.UiStudio.Sketch.SketchNode?)t.GetMethod("HitTest", Private)!.Invoke(v, new object[] { p });
    }

    static int LineOf(string text, string needle) => text[..text.IndexOf(needle, StringComparison.Ordinal)].Count(c => c == '\n') + 1;

    // ═══════════════════════════════════════════ a WPF app ═══════════════════════════════════════════

    static async Task WpfApp(UiStudioViewModel vm, UiStudioView view, string fx)
    {
        Log("\n## WPF app (simulated from XAML)");
        string root = Path.Combine(fx, "wpf");
        const string ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
        W(root, "Desk.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net8.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>");
        W(root, "App.xaml", $$$"""
            <Application x:Class="Desk.App" {{{ns}}} StartupUri="MainWindow.xaml">
                <Application.Resources>
                    <ResourceDictionary>
                        <SolidColorBrush x:Key="Page.Background" Color="#14161C"/>
                        <SolidColorBrush x:Key="Accent.1" Color="#2EC27E"/>
                        <SolidColorBrush x:Key="Surface.Card" Color="#1F232B"/>
                        <Style x:Key="PrimaryButton" TargetType="Button"/>
                    </ResourceDictionary>
                </Application.Resources>
            </Application>
            """);
        W(root, "MainWindow.xaml", $$$"""
            <Window x:Class="Desk.MainWindow" {{{ns}}} xmlns:vm="clr-namespace:Desk.ViewModels" xmlns:views="clr-namespace:Desk.Views"
                    Title="Desk" Width="1200" Height="760" Background="{DynamicResource Page.Background}">
                <Window.Resources>
                    <DataTemplate DataType="{x:Type vm:HomeViewModel}"><views:HomeView/></DataTemplate>
                </Window.Resources>
                <DockPanel>
                    <Border DockPanel.Dock="Left" Width="220" Background="{DynamicResource Surface.Card}">
                        <StackPanel Margin="12">
                            <TextBlock Text="Desk" FontSize="20" FontWeight="Bold"/>
                            <RadioButton Content="หน้าแรก" IsChecked="True"/>
                            <RadioButton Content="รายงาน"/>
                        </StackPanel>
                    </Border>
                    <ContentControl Content="{Binding CurrentView}"/>
                </DockPanel>
            </Window>
            """);
        string home = $$$"""
            <UserControl x:Class="Desk.Views.HomeView" {{{ns}}}>
                <StackPanel Margin="24">
                    <TextBlock Text="หน้าแรก" FontSize="24" FontWeight="Bold"/>
                    <TextBlock Text="{Binding Summary}"/>
                    <Grid>
                        <TextBlock Text="ค้นหาลูกค้า" IsHitTestVisible="False" Visibility="{Binding Query, Converter={StaticResource EmptyToVisible}}"/>
                        <TextBox Text="{Binding Query}"/>
                    </Grid>
                    <StackPanel Orientation="Horizontal">
                        <Button Content="บันทึก" Style="{StaticResource PrimaryButton}"/>
                        <Button Content="ยกเลิก"/>
                    </StackPanel>
                </StackPanel>
            </UserControl>
            """;
        W(root, "Views/HomeView.xaml", home);
        W(root, "Views/AboutWindow.xaml", $$$"""
            <Window x:Class="Desk.Views.AboutWindow" {{{ns}}} Title="เกี่ยวกับ Desk" Width="520" SizeToContent="Height">
                <StackPanel Margin="20"><TextBlock Text="Desk 1.0"/><Button Content="ปิด" IsCancel="True"/></StackPanel>
            </Window>
            """);

        vm.OpenProject(root, autoStart: true);
        Check("wpf: opened as a desktop app simulated from its XAML, the gallery opens by itself", vm.IsSimulated && vm.IsDesktopApp && vm.ShowPages, $"{vm.IsSimulated} {vm.IsDesktopApp} {vm.ShowPages}");
        bool sketched = await WaitUntil(() => Cards(vm).Count == 3 && Cards(vm).All(c => c.HasSketch), 30000);
        Check("wpf: every view and window is drawn from its XAML", sketched, string.Join(", ", Cards(vm).Select(c => $"{c.Page.Detail}:{c.State}")));
        Check("wpf: landscape cards — a desktop window, not a phone", !vm.IsPhoneLayout && vm.CardWidth > 200 && Cards(vm).All(c => c.Sketch?.Desktop == true), $"{vm.IsPhoneLayout} {vm.CardWidth}");
        Check("wpf: the main window first, named by its Title; a dialog by its Title too",
              Cards(vm).First().Page.Detail == "MainWindow" && Cards(vm).First().Title == "Desk" && Cards(vm).Any(c => c.Title == "เกี่ยวกับ Desk"), string.Join(" | ", Cards(vm).Select(c => c.Title)));
        Check("wpf: filter chips for views and windows", vm.PageSections.Any(s => s.Key == "~group:view") && vm.PageSections.Any(s => s.Key == "~group:window"),
              string.Join(", ", vm.PageSections.Select(s => s.Label)));
        Check("wpf: the live browser is hidden (nothing to run)", ((FrameworkElement)view.FindName("Frame")).Visibility != Visibility.Visible);
        await Idle(300);
        SavePng((FrameworkElement)view.FindName("PagesPanel"), "gallery-wpf.png");

        var homeCard = Cards(vm).First(c => c.Page.Detail == "HomeView");
        vm.OpenCardCommand.Execute(homeCard);
        Check("wpf: a click on a card opens that view on the stage", !vm.ShowPages && vm.StageCard == homeCard && vm.StageSketch?.Desktop == true);
        await Idle(400);
        var stageView = (SketchView)view.FindName("StageView");
        Check("wpf: the stage draws a wide window, not a phone in a bezel", stageView.ActualWidth > 500 && stageView.ActualWidth > stageView.ActualHeight * 0.9, $"{stageView.ActualWidth:0}×{stageView.ActualHeight:0}");
        SavePng((FrameworkElement)view.FindName("Stage"), "stage-wpf.png");

        var save = vm.StageSketch!.Root.Walk().First(n => n.Kind == CluadeX.Services.UiStudio.Sketch.SketchKind.Button && n.Text == "บันทึก");
        var hit = HitAt(stageView, save);
        Check("wpf: a click where the button is drawn hits that button", ReferenceEquals(hit, save), hit?.ToString() ?? "nothing");
        int requested = -1;
        Action<int> onLine = l => requested = l;
        MainVm.CodeEditorVM.ScrollToLineRequested += onLine;
        vm.PickStageNode(hit ?? save, open: true);
        int want = LineOf(home, "Content=\"บันทึก\"");
        bool opened = await WaitUntil(() => MainVm.CodeEditorVM.ActiveTab?.FullPath?.EndsWith("HomeView.xaml", StringComparison.OrdinalIgnoreCase) == true && requested > 0, 15000);
        MainVm.CodeEditorVM.ScrollToLineRequested -= onLine;
        Check("wpf: clicking a block opens its XAML file at that line", opened && requested == want, $"{MainVm.CodeEditorVM.ActiveTab?.FullPath} line {requested} (want {want})");
        Check("wpf: the inspector names the element and where it is written", vm.StageNodeWhere == "Views/HomeView.xaml:" + want && vm.StageNodeLabel == "Button" && vm.StageNodeText == "บันทึก",
              $"{vm.StageNodeWhere} {vm.StageNodeLabel} '{vm.StageNodeText}'");
        Check("wpf: the hint text over the search box is its placeholder", vm.StageSketch.Root.Walk().Any(n => n.Kind == CluadeX.Services.UiStudio.Sketch.SketchKind.Input && n.Text == "ค้นหาลูกค้า"));
        string prompt = (string)Invoke(vm, "BuildStagePrompt", "make the save button bigger", false)!;
        Check("wpf: the AI request names the WPF view, its XAML file, the picked element and its line",
              prompt.Contains("WPF") && prompt.Contains("HomeView") && prompt.Contains("Views/HomeView.xaml") && prompt.Contains("<Button>") && prompt.Contains(":" + want) && prompt.Contains("{DynamicResource}"), prompt);
        Invoke(MainVm, "NavigateTo", "UiStudio");
        await Idle(200);

        // the main window: the view its DataTemplate shows, inside the window, beside the 220-px side bar
        var mainCard = Cards(vm).First();
        vm.OpenScreenCommand.Execute(mainCard);
        await Idle(300);
        var nodes = vm.StageSketch!.Root.Walk().ToList();
        Check("wpf: the main window draws its first page inside it (the view a DataTemplate names)",
              nodes.Any(n => n.Text == "หน้าแรก" && n.File!.EndsWith("HomeView.xaml") && n.UseFile!.EndsWith("MainWindow.xaml")) && nodes.Any(n => n.Width == 220),
              string.Join(" | ", nodes.Where(n => n.Text != null).Select(n => $"{n.Kind}:{n.Text}:{Path.GetFileName(n.File)}")));
        SavePng((FrameworkElement)view.FindName("Stage"), "stage-wpf-main.png");
    }
}
