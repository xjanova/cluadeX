using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Browser;
using CluadeX.Services.Tools;
using CluadeX.Services.Vision;
using CluadeX.ViewModels;
using CluadeX.Views.Preview;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BrowserTest;

/// <summary>Stands in for MainWindow's view model: the two full-window overlays the page must step aside for.</summary>
public sealed class FakeMain : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool _loading;
    public bool IsModelLoading { get => _loading; set { _loading = value; PropertyChanged?.Invoke(this, new(nameof(IsModelLoading))); } }
    public FakePalette Palette { get; } = new();
}

public sealed class FakePalette : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool _open;
    public bool IsOpen { get => _open; set { _open = value; PropertyChanged?.Invoke(this, new(nameof(IsOpen))); } }
}

/// <summary>
/// The preview pane in real WPF (browsertest --ui, a child process of the main run): CluadeX's own App resources,
/// the real PreviewPane / PreviewViewModel / PreviewPresenter, the engine on the WPF UI thread exactly as in the
/// app, and only off-screen, never-activated windows (nothing appears on the owner's screen). Covers the review
/// findings that need a real visual tree: the pop-out pane taking the page, the discarded-pane leak, the airspace
/// step-aside, the language-following Copy label, the presenter's focus and per-chat dismissal, the Close button
/// and the Dev Preview switch closing the engine.
/// </summary>
internal static class UiSuite
{
    static int Pass, Fail;
    static BrowserService Browser = null!;
    static BrowserToolModule Module = null!;
    static string Local = "";

    static void Check(string id, string name, bool ok, string detail = "")
    {
        if (ok) Pass++; else Fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {id,-5} {name}");
        if (!ok && detail.Length > 0) Console.WriteLine("            " + (detail.Length > 900 ? detail[..900] : detail).Replace("\n", "\n            "));
    }

    public static int Start()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        var app = new CluadeX.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        int rc = 2;
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try { rc = await Run(); }
            catch (Exception ex) { Console.WriteLine("  FAIL U*    the UI suite threw: " + ex); rc = 2; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
        return rc;
    }

    static async Task Render(int ms = 0)
    {
        for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (ms > 0) await Task.Delay(ms);
        for (int i = 0; i < 2; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    static Window OffScreen(UIElement? content, object? dataContext = null) => new()
    {
        Content = content, Width = 1000, Height = 700, Left = -30000, Top = -30000, DataContext = dataContext,
        ShowInTaskbar = false, WindowStyle = WindowStyle.None, ShowActivated = false,
    };

    static Task<ToolResult> T(string chat, string tool, params (string k, string v)[] args)
    {
        var type = Module.ResolveToolType(tool)!.Value;
        var call = new ToolCall { Type = type, ToolName = tool, Arguments = args.ToDictionary(a => a.k, a => a.v) };
        var ctx = new ToolModuleContext { SessionKey = chat, WorkingDirectory = AppContext.BaseDirectory, ShellDirectory = AppContext.BaseDirectory, OutputBudgetChars = 30_000 };
        // The agent's tool calls run on pool threads, never on the UI thread.
        return Task.Run(async () => { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); return await Module.ExecuteAsync(call, ctx, cts.Token); });
    }

    static async Task<int> Frames()
    {
        var r = await T("ui", "browser_eval", ("timeout_ms", "4000"), ("expression",
            "await new Promise(res => { let n = 0; const t0 = performance.now(); requestAnimationFrame(function f() { n++; if (performance.now() - t0 < 500) requestAnimationFrame(f); else res(n); }); setTimeout(() => res(n || -1), 2500); })"));
        return r.Success && int.TryParse(Program.Body(r.Output), out var n) ? n : -2;
    }

    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);

    static async Task<int> Run()
    {
        string exeDir = AppContext.BaseDirectory.TrimEnd('\\');
        string udf = Path.Combine(exeDir, "udf-ui-" + Guid.NewGuid().ToString("N")[..6]);
        var configure = typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!;
        var services = new ServiceCollection();
        configure.Invoke(null, new object[] { services });
        // The app's own threading: no Dispatcher option → the engine lives on Application.Current's UI thread.
        services.Replace(ServiceDescriptor.Singleton(sp => new BrowserService(sp.GetRequiredService<SettingsService>(),
            new BrowserServiceOptions { UserDataFolder = udf, ExtraArguments = Program.HostRules })));
        services.Replace(ServiceDescriptor.Singleton(sp => new BrowserToolModule(sp.GetRequiredService<BrowserService>(), sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ActivationService>(), new ToolImageStore(Path.Combine(exeDir, "screens-ui")), sp.GetRequiredService<FileSystemService>(),
            null, sp.GetRequiredService<IPreviewPresenter>())));
        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<SettingsService>();
        if (!settings.DataRoot.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("  FAIL U*    ABORT: data root not isolated"); return 3; }
        LocalizedResources.Instance.Initialize(provider.GetRequiredService<LocalizationService>());
        var loc = provider.GetRequiredService<LocalizationService>();
        loc.SetLanguage("en");

        // An activated (trial) license, in memory only — nothing is sent to any license server.
        var act = provider.GetRequiredService<ActivationService>();
        typeof(ActivationService).GetField("_hasKey", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(act, true);
        typeof(ActivationService).GetField("_tier", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(act, "trial");
        settings.Settings.Features.DevPreview = true;
        Check("U0", "in-memory trial activation opens the Dev Preview gate (the pane is not 'locked')", DevPreviewGate.Refusal(settings, act, "x") == null);

        Browser = provider.GetRequiredService<BrowserService>();
        Module = provider.GetRequiredService<BrowserToolModule>();
        var vm = provider.GetRequiredService<PreviewViewModel>();
        var presenter = (PreviewPresenter)provider.GetRequiredService<IPreviewPresenter>();
        using var server = new FixtureServer(exeDir);
        Local = $"http://127.0.0.1:{server.Port}/ui.html";
        File.WriteAllText(Path.Combine(exeDir, "ui.html"),
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>ui</title></head><body style=\"margin:0;background:#123\"><canvas id=\"g\" width=\"300\" height=\"200\"></canvas><script>let n=0;const c=document.getElementById('g').getContext('2d');(function f(){n++;window.__frames=n;c.fillStyle='hsl('+(n%360)+',80%,50%)';c.fillRect(0,0,300,200);requestAnimationFrame(f);})();</script></body></html>");

        // ── The main window (off-screen) with the docked pane ──
        var fake = new FakeMain();
        var paneA = new PreviewPane();
        var winA = OffScreen(paneA, fake);
        winA.Show();
        Application.Current.MainWindow = winA;
        await Render(400);
        var open = await T("ui", "browser_open", ("url", Local));
        await Render(700);
        Check("U1", "browser_open with the docked pane on screen: the page lands in it (no pop-out)",
            open.Success && paneA.Host != null && Browser.ActiveHost == paneA.Host.Handle && presenter.Window == null, $"{open.Error} active={Browser.ActiveHost}");

        // ── The pop-out button: a pane in its own window takes the page ──
        var paneB = new PreviewPane { IsInWindow = true };
        var winB = OffScreen(paneB);
        winB.Show();
        await Render(800);
        bool bHas = paneB.Host != null && Browser.ActiveHost == paneB.Host.Handle;
        Check("U2", "the pop-out pane shows the page right away (not 'the page is in the other window'), and the docked pane offers 'Show it here'",
            bHas && paneB.ElsewhereState.Visibility != Visibility.Visible && paneA.ElsewhereState.Visibility == Visibility.Visible,
            $"popout has page={bHas} B.elsewhere={paneB.ElsewhereState.Visibility} A.elsewhere={paneA.ElsewhereState.Visibility}");
        var paneC = new PreviewPane();
        var winC = OffScreen(paneC);
        winC.Show();
        await Render(700);
        Check("U2b", "negative control: a newly shown docked pane does NOT take the page from the window that has it",
            Browser.ActiveHost == paneB.Host?.Handle && paneC.ElsewhereState.Visibility == Visibility.Visible, $"active={Browser.ActiveHost}");
        winC.Close();
        winB.Close();
        await Render(900);
        int fr = await Frames();
        Check("U2c", "closing the pop-out keeps the page alive (frames run) and the docked pane takes it back",
            Browser.Session != null && fr >= 15 && paneA.Host != null && Browser.ActiveHost == paneA.Host.Handle, $"frames={fr} active={Browser.ActiveHost}");

        // ── A CluadeX page switch discards the workbench view: nothing may keep it alive ──
        int liveBefore = Live();
        IntPtr oldHwnd = paneA.Host!.Handle;
        var weakA = new WeakReference(paneA);
        winA.Content = null;
        await Render(500);
        bool parked = Browser.ActiveHost == IntPtr.Zero;
        int frParked = await Frames();
        var paneA2 = new PreviewPane();
        winA.Content = paneA2;
        await Render(800);
        paneA = null!;
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); await Render(); }
        Check("U3", "a page switch parks the page (it keeps running), the new view shows it again, and the discarded pane, its host and its child window are released",
            parked && frParked >= 15 && Browser.ActiveHost == paneA2.Host?.Handle && !weakA.IsAlive && !IsWindow(oldHwnd) && Live() == liveBefore,
            $"parked={parked} frames={frParked} alive={weakA.IsAlive} oldWindow={IsWindow(oldHwnd)} live {liveBefore}→{Live()}");
        Check("U3b", "negative control: the pane still on screen is alive and keeps its window", paneA2.Host != null && IsWindow(paneA2.Host.Handle));

        // ── Airspace: full-window overlays of the main window ──
        fake.IsModelLoading = true;
        await Render(500);
        bool coveredLoading = paneA2.IsCovered && Browser.ActiveHost == IntPtr.Zero && paneA2.CoveredState.Visibility == Visibility.Visible;
        int frCovered = await Frames();
        fake.IsModelLoading = false;
        await Render(500);
        bool backLoading = !paneA2.IsCovered && Browser.ActiveHost == paneA2.Host?.Handle;
        fake.Palette.IsOpen = true;
        await Render(500);
        bool coveredPalette = paneA2.IsCovered && Browser.ActiveHost == IntPtr.Zero;
        fake.Palette.IsOpen = false;
        await Render(500);
        bool backPalette = !paneA2.IsCovered && Browser.ActiveHost == paneA2.Host?.Handle;
        Check("U4", "while the model-loading overlay or the command palette is open the page steps aside (parked, still running) and comes back after",
            coveredLoading && frCovered >= 15 && backLoading && coveredPalette && backPalette,
            $"loading={coveredLoading} frames={frCovered} back={backLoading} palette={coveredPalette} back={backPalette}");
        var paneD = new PreviewPane();
        var winD = OffScreen(paneD, new FakeMain { IsModelLoading = true });
        winD.Show();
        await Render(400);
        Check("U4b", "negative control: a pane in another window (the pop-out) does not react to a view model that is not the main window's", !paneD.IsCovered);
        winD.Close();
        await Render(300);

        // ── The Copy-command label follows the language and 'Copied' does not stick ──
        var copiedAt = typeof(PreviewViewModel).GetField("_copiedAt", BindingFlags.NonPublic | BindingFlags.Instance)!;
        vm.Refresh();
        string en = vm.CopyLabel;
        loc.SetLanguage("th");
        vm.Refresh();
        string th = vm.CopyLabel;
        string copiedExpected = loc.T("preview.copied");
        copiedAt.SetValue(vm, DateTime.UtcNow);
        vm.Refresh();
        string copiedTh = vm.CopyLabel;
        copiedAt.SetValue(vm, DateTime.UtcNow - TimeSpan.FromSeconds(5));
        vm.Refresh();
        string after = vm.CopyLabel;
        loc.SetLanguage("en");
        vm.Refresh();
        Check("U5", "the Copy-command label is re-localized (Thai after a switch) and 'Copied' goes back to 'Copy command' after a few seconds",
            en == "Copy command" && Regex.IsMatch(th, "[฀-๿]") && copiedTh == copiedExpected && copiedTh != th && after == th,
            $"en={en} th={th} copied={copiedTh} after={after}");

        // ── The presenter: no pane on screen → a pop-out that does not take the focus, once per chat ──
        winA.Content = null;
        await Render(500);
        var pop1 = await T("pop-1", "browser_open", ("url", Local));
        await Render(1000);
        var w1 = presenter.Window;
        bool w1Ok = w1 != null && !w1.ShowActivated && !w1.IsActive && w1.Pane.Host != null && Browser.ActiveHost == w1.Pane.Host.Handle;
        Check("U6", "browser_open with no pane on screen pops the window out WITHOUT taking the focus, and the page is in it",
            pop1.Success && w1Ok, $"window={w1 != null} showActivated={w1?.ShowActivated} active={w1?.IsActive}");
        w1?.Close();
        await Render(600);
        var pop1b = await T("pop-1", "browser_open", ("url", Local));
        await Render(800);
        Check("U6b", "after the user closed it, the next browser_open in the same chat leaves it closed and tells the agent so",
            pop1b.Success && presenter.Window == null && presenter.IsDismissed("pop-1") && pop1b.Output.Contains("closed the preview window"), pop1b.Output);
        var pop2 = await T("pop-2", "browser_open", ("url", Local));
        await Render(1000);
        Check("U6c", "negative control: in another chat the window pops out again", pop2.Success && presenter.Window != null);
        presenter.Window?.Close();
        await Render(500);
        winA.Content = paneA2;
        await Render(600);

        // ── The pane's Reload after a chat switch: this load goes through, nobody's approvals change ──
        string rl = server.Ext("reload"), rlOrigin = UrlPolicy.OriginOf(rl)!;
        int ExtLoads() => server.Hits($"reload.ext.test:{server.Port}/ext").Split(" | ", StringSplitOptions.RemoveEmptyEntries)
            .Count(h => h.StartsWith("GET ", StringComparison.Ordinal) && h.EndsWith("/ext", StringComparison.Ordinal));
        var openRl = await T("rl-a", "browser_open", ("url", rl + "/ext"));
        await Render(300);
        int loads0 = ExtLoads();
        Browser.ActiveChat = "rl-b";   // the user switched to a chat that never approved the site
        var reload = typeof(PreviewViewModel).GetMethod("ReloadAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)reload.Invoke(vm, null)!;
        await Render(300);
        bool reloaded = vm.Status.Length == 0 && (Browser.Session?.CurrentUrl ?? "").StartsWith(rl) && ExtLoads() == loads0 + 1;
        bool nobody = !Browser.IsUserApproved(rlOrigin) && !Browser.IsApprovedForChat("rl-b", rlOrigin);
        await T("rl-c", "browser_open", ("url", Local));
        var probe = await T("rl-c", "browser_eval", ("expression", $"await fetch('{rl}/fetch?d=u11').then(r => r.status, e => 'err')"));
        string probeText = probe.Success ? Program.Body(probe.Output) : "ERROR " + probe.Error;
        await Task.Delay(300);
        Check("U11", "the pane's Reload after a chat switch reloads the external page, yet approves its site for nobody: another chat's page still cannot contact it (403, never sent)",
            openRl.Success && reloaded && nobody && probeText == "403" && !server.Saw("d=u11"),
            $"open={openRl.Success} reloaded={reloaded} (loads {loads0}→{ExtLoads()}, status='{vm.Status}', url={Browser.Session?.CurrentUrl}) nobody={nobody} user={Browser.IsUserApproved(rlOrigin)} fetch={probeText}");
        // Negative control: what Reload used to grant (a user approval, valid in every chat) does let that page through.
        string rl2 = server.Ext("reload2");
        Browser.ApproveByUser(UrlPolicy.OriginOf(rl2)!);
        var probe2 = await T("rl-c", "browser_eval", ("expression", $"await fetch('{rl2}/fetch?d=u11n').then(r => r.status, e => 'err')"));
        string probe2Text = probe2.Success ? Program.Body(probe2.Output) : "ERROR " + probe2.Error;
        Check("U11b", "negative control: a user-level approval (what Reload used to grant) lets another chat's page contact the site (200, it arrived)",
            probe2Text == "200" && server.Saw("d=u11n"), $"fetch={probe2Text}");

        // ── Close button, and Dev Preview switched off ──
        vm.ClosePageCommand.Execute(null);
        await WaitFor(() => Browser.Session == null, 5000);
        Check("U7", "the pane's Close button closes the page and stops the engine", Browser.Session == null);
        await T("ui", "browser_open", ("url", Local));
        settings.Settings.Features.DevPreview = true;
        settings.Save();
        await Render(800);
        bool kept = Browser.Session != null;
        settings.Settings.Features.DevPreview = false;
        settings.Save();
        bool closed = await WaitFor(() => Browser.Session == null, 5000);
        settings.Settings.Features.DevPreview = true;
        settings.Save();
        Check("U7b", "switching Dev Preview off closes the page and its engine (saving with it on keeps it — negative control)", kept && closed, $"kept={kept} closed={closed}");

        // ── The workbench split keeps its state across page switches ──
        vm.IsDockOpen = true;
        vm.DockWidth = 700;
        var dock = new PreviewDock();
        string? repo = FindRepo();
        string editorXaml = repo != null ? File.ReadAllText(Path.Combine(repo, @"CluadeX\Views\CodeEditorView.xaml")) : "";
        Check("U8", "the PREVIEW split's open state and width live in the app-wide view model (a rebuilt workbench gets them back)",
            dock.Width == 700 && Regex.IsMatch(editorXaml, @"PreviewToggle[^>]*IsChecked=""\{Binding IsDockOpen, Source=\{x:Static vm:PreviewViewModel\.Current\}"),
            $"width={dock.Width} binding={(repo == null ? "repo not found" : "checked")}");

        // ── Engine missing → 'Check again' finds the runtime ──
        BrowserEngineProbe.SetCachedForTest(new BrowserEngineInfo { ProbedUtc = DateTime.UtcNow });
        vm.Refresh();
        bool missing = vm.IsEngineMissing;
        vm.CheckEngineCommand.Execute(null);
        Check("U9", "'Check again' next to the install command probes again and the pane leaves the 'not installed' state", missing && !vm.IsEngineMissing);

        winA.Close();
        await Render(300);
        Browser.Dispose();
        await Task.Delay(3000);
        int left = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name = 'msedgewebview2.exe'");
            foreach (ManagementObject mo in searcher.Get()) { if ((mo["CommandLine"] as string ?? "").Contains(udf, StringComparison.OrdinalIgnoreCase)) left++; mo.Dispose(); }
        }
        catch { }
        Check("U10", "app-style shutdown on the UI thread leaves no msedgewebview2.exe for the profile", left == 0, $"left={left}");
        try { Directory.Delete(udf, true); } catch { }
        try { File.Delete(Path.Combine(exeDir, "ui.html")); } catch { }

        Console.WriteLine($"═══ UI {Pass} pass · {Fail} FAIL ═══");
        return Fail == 0 ? 0 : 1;
    }

    static int Live() => PreviewPaneHost.LiveCount;

    static async Task<bool> WaitFor(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(100); }
        return cond();
    }

    static string? FindRepo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, @"CluadeX\CluadeX.csproj"))) d = d.Parent;
        return d?.FullName;
    }
}
