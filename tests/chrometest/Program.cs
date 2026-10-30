using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Management;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BrowserTest;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Browser;
using CluadeX.Services.Chrome;
using CluadeX.Services.Tools;
using CluadeX.Services.Vision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Win32;

namespace ChromeTest;

/// <summary>
/// CluadeX's own Chrome connection, end to end: the REAL browser_* tools of the built CluadeX.dll, through the REAL
/// ChromeBridgeServer and the REAL CluadeX.exe as native host, into the REAL extension in real Chrome. No model, no
/// paid API, no internet: every "external" site is this exe's fixture server under a *.ext.test name.
///
/// Isolation: Chrome runs on a throwaway profile and the user's Chrome is never touched. The data root is next to this
/// exe (portable.txt). The one thing outside is the per-user native host key, removed in the finally and checked.
/// Commands stay in dry run: this harness has no reason to leave <c>ProcessLaunchPolicy</c>'s default.
/// </summary>
internal static class Program
{
    const string HostRules = "MAP *.ext.test 127.0.0.1";

    static int Pass, Fail;
    static readonly List<string> Problems = new();

    static BrowserService Browser = null!;
    static BrowserToolModule Module = null!;
    static ToolModuleContext Ctx = null!;
    static FixtureServer Server = null!;
    static ChromeBridgeServer Bridge = null!;
    static ChromeDriver Driver = null!;
    static SettingsService Settings = null!;
    static string BaseUrl = "";
    static string HostExe = "";
    static readonly List<BrowserNotice> Notices = new();

    [STAThread]
    static int Main()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        try { return Run().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("FATAL " + ex); return 2; }
    }

    // ─────────────────────────── reporting ───────────────────────────

    static void Check(string id, string name, bool ok, string detail = "")
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS {id,-5} {name}"); return; }
        Fail++; Problems.Add($"FAIL {id} {name}");
        Console.WriteLine($"  FAIL {id,-5} {name}");
        if (detail.Length > 0) Console.WriteLine("            " + Trunc(detail, 900).Replace("\n", "\n            "));
    }

    static async Task Case(string id, Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex)
        {
            Fail++; Problems.Add($"FAIL {id} threw {ex.GetType().Name}");
            Console.WriteLine($"  FAIL {id,-5} harness case threw: {ex.GetType().Name}: {Trunc(ex.Message, 400)}");
        }
    }

    static string Trunc(string? s, int n) => s == null ? "" : (s.Length <= n ? s : s[..n] + $"…(+{s.Length - n})");
    static string Seen(ToolResult r) => r.Success ? r.Output ?? "" : string.Join("\n", new[] { r.Error, r.Output }.Where(s => !string.IsNullOrEmpty(s)));

    static async Task<bool> WaitUntil(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(100); }
        return cond();
    }

    static async Task<bool> WaitUntilAsync(Func<Task<bool>> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (await cond()) return true; await Task.Delay(200); }
        return await cond();
    }

    // ─────────────────────────── calling the tools ───────────────────────────

    static async Task<ToolResult> T(string tool, params (string k, string v)[] args)
    {
        var type = Module.ResolveToolType(tool) ?? throw new Exception("tool name did not resolve: " + tool);
        var call = new ToolCall { Type = type, ToolName = tool, Arguments = args.ToDictionary(a => a.k, a => a.v) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await Module.ExecuteAsync(call, Ctx, cts.Token);
    }

    static string Body(string s) => UrlPolicy.EnvelopeBody(s) ?? s.Trim();

    static async Task<string> E(string expression)
    {
        var r = await T("browser_eval", ("expression", expression));
        return r.Success ? Body(r.Output) : "ERROR: " + Seen(r);
    }

    static Task<ToolResult> Click(int x, int y) => T("browser_click", ("x", x.ToString()), ("y", y.ToString()), ("css", "true"));

    // ─────────────────────────── the user's side of Chrome ───────────────────────────

    /// <summary>What Chrome has open, from the extension: [{id, focused, state, tabs:[{id, url, active}]}].</summary>
    static async Task<JsonElement> Windows() => await Driver.EvalAsync(
        "chrome.windows.getAll({populate:true}).then(l => l.map(w => ({id:w.id, focused:w.focused, state:w.state, tabs:w.tabs.map(t => ({id:t.id, url:t.url, active:t.active}))})))");

    static async Task<int> WindowCount() => (await Windows()).GetArrayLength();

    static async Task<int> TabCount() => (await Windows()).EnumerateArray().Sum(w => w.GetProperty("tabs").GetArrayLength());

    static async Task<JsonElement> Agent() => await Driver.EvalAsync("agent");

    /// <summary>Tabs chrome.debugger is on right now.</summary>
    static async Task<int[]> AttachedTabs()
    {
        var list = await Driver.EvalAsync("chrome.debugger.getTargets().then(l => l.filter(t => t.attached && t.tabId != null).map(t => t.tabId))");
        return list.EnumerateArray().Select(t => t.GetInt32()).ToArray();
    }

    /// <summary>The user presses the CluadeX icon in the toolbar.</summary>
    static Task PressIcon() => Driver.EvalAsync("chrome.action.onClicked.dispatch({id: 0})");

    /// <summary>A reader of the state the harness has no other way to see.</summary>
    static List<string> RelayProcesses()
    {
        using var query = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name='CluadeX.exe'");
        var found = new List<string>();
        foreach (ManagementObject p in query.Get())
            using (p)
                if (p["CommandLine"] is string line && line.Contains("chrome-extension://", StringComparison.OrdinalIgnoreCase)) found.Add(line);
        return found;
    }

    // ─────────────────────────── run ───────────────────────────

    static async Task<int> Run()
    {
        string exeDir = AppContext.BaseDirectory.TrimEnd('\\');
        string bin = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CluadeXBin").Value!;
        HostExe = Path.GetFullPath(Path.Combine(bin, "CluadeX.exe"));
        if (!File.Exists(HostExe)) { Console.WriteLine($"ABORT: {HostExe} is not built."); return 3; }

        var configure = typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!;
        var services = new ServiceCollection();
        configure.Invoke(null, new object[] { services });

        string udf = Path.Combine(exeDir, "udf-" + Guid.NewGuid().ToString("N")[..8]);
        var options = new BrowserServiceOptions { UserDataFolder = udf, DisableGpu = false, ExtraArguments = $"--host-resolver-rules=\"{HostRules}\"" };
        var images = new ToolImageStore(Path.Combine(exeDir, "screens"));
        services.Replace(ServiceDescriptor.Singleton(sp => new BrowserService(sp.GetRequiredService<SettingsService>(), options)
        {
            ChromeBridge = sp.GetRequiredService<ChromeBridgeServer>(),
        }));
        // The real module, with the license gate opened (the free tier would refuse every call).
        services.Replace(ServiceDescriptor.Singleton(sp => new BrowserToolModule(sp.GetRequiredService<BrowserService>(), sp.GetRequiredService<SettingsService>(), null, images,
            sp.GetRequiredService<FileSystemService>(), null, null, _ => null)));
        using var provider = services.BuildServiceProvider();

        Settings = provider.GetRequiredService<SettingsService>();
        Console.WriteLine($"data root : {Settings.DataRoot}");
        if (!Settings.DataRoot.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("ABORT: data root is not isolated (portable.txt missing) — refusing to touch real user data.");
            return 3;
        }
        LocalizedResources.Instance.Initialize(provider.GetRequiredService<LocalizationService>());

        string? chrome = BrowserEngineProbe.Probe(refresh: true).ChromePath;
        if (chrome == null) { Console.WriteLine("ABORT: Chrome is not installed."); return 3; }

        string project = Path.Combine(exeDir, "chrome-project");
        if (Directory.Exists(project)) Directory.Delete(project, true);
        Directory.CreateDirectory(project);
        Server = new FixtureServer(project);
        BaseUrl = $"http://127.0.0.1:{Server.Port}";
        WriteFixtures(project);

        var fs = provider.GetRequiredService<FileSystemService>();
        fs.WorkingDirectory = project;
        Browser = provider.GetRequiredService<BrowserService>();
        Browser.NoticeRaised += n => { lock (Notices) Notices.Add(n); };
        Bridge = provider.GetRequiredService<ChromeBridgeServer>();
        Module = provider.GetRequiredService<BrowserToolModule>();
        Ctx = new ToolModuleContext { SessionKey = "chrometest", WorkingDirectory = project, ShellDirectory = project, OutputBudgetChars = 30_000 };
        Settings.UpdateSettings(s => s.BrowserEngine = BrowserService.ChromeEngine);

        Console.WriteLine($"host      : {HostExe}");
        Console.WriteLine($"chrome    : {chrome}");
        Console.WriteLine($"fixtures  : {BaseUrl}/\n");

        bool cleaned = false;
        try
        {
            Console.WriteLine("── the host, before Chrome ──");
            await Case("H1", H1);
            // Before Chrome only: the connector names the running exe as host, and that is this harness.
            await Case("S1", () => S1(provider));

            ChromeBridgeInstall.Install(Settings.DataRoot, HostExe);
            Bridge.ExpectedVersion = ChromeBridgeInstall.BundledVersion;
            Bridge.Start();
            await Case("I1", I1);

            var (driver, extensionId) = await ChromeDriver.StartAsync(Path.Combine(exeDir, "launch-chrome.mjs"), chrome, ChromeBridgeInstall.ExtensionFolder(Settings.DataRoot), HostRules);
            Driver = driver;
            using (driver)
            {
                Console.WriteLine("\n── connection ──");
                await Case("C1", () => C1(extensionId));
                await Case("C2", C2);

                Console.WriteLine("\n── the tools, in a window of its own ──");
                await Case("T1", T1);
                await Case("T2", T2);
                await Case("T3", T3);
                await Case("T4", T4);
                await Case("T5", T5);
                await Case("T6", T6);
                await Case("T7", T7);

                Console.WriteLine("\n── the policy: nothing reaches a site nobody approved ──");
                await Case("P1", P1);
                await Case("P2", P2);
                await Case("P3", P3);
                await Case("P4", P4);
                await Case("P5", P5);
                await Case("P6", P6);

                Console.WriteLine("\n── a page cannot get out of its window ──");
                await Case("D1", D1);
                await Case("D2", D2);
                await Case("D3", D3);

                Console.WriteLine("\n── the user's profile ──");
                await Case("U1", U1);
                await Case("U2", U2);
                await Case("U3", U3);
                await Case("U4", U4);

                Console.WriteLine("\n── the user stays in charge ──");
                await Case("R1", R1);
                await Case("R2", R2);
                await Case("R3", R3);

                Console.WriteLine("\n── the connection itself ──");
                await Case("L1", L1);
                await Case("L2", L2);
                await Case("L3", L3);
                await Case("L4", L4);
                await Case("L5", L5);
                await Case("L6", L6);

                await Case("Z1", Z1);
                await Case("Z0", Z0);
                await driver.QuitAsync();
                if (driver.ProfileLeft != null) Check("Z2", "the throwaway profile was deleted", false, driver.ProfileLeft);
            }
            await Case("Z3", Z3);
        }
        finally
        {
            Browser.Dispose();
            Bridge.Stop();
            ChromeBridgeInstall.Uninstall(Settings.DataRoot);
            using (var left = Registry.CurrentUser.OpenSubKey(@"Software\Google\Chrome\NativeMessagingHosts\" + ChromeBridgeInstall.HostName))
                cleaned = left == null;
            Server.Dispose();
            try { if (Directory.Exists(udf)) Directory.Delete(udf, true); } catch (IOException) { }
        }
        Check("Z4", "the native host key is gone from this user's registry", cleaned);

        Console.WriteLine($"\n{Pass} passed, {Fail} failed");
        foreach (string p in Problems) Console.WriteLine("  " + p);
        return Fail == 0 ? 0 : 1;
    }

    static void WriteFixtures(string project)
    {
        string ext = Server.Ext("blocked");
        File.WriteAllText(Path.Combine(project, "index.html"), $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>chrometest</title></head>
            <body style="margin:0">
            <button id="count" style="position:absolute;left:0;top:0;width:200px;height:60px" onclick="window.clicks=(window.clicks||0)+1">count</button>
            <a id="out" href="{{ext}}/link" style="position:absolute;left:0;top:80px;width:200px;height:40px;display:block">external link</a>
            <button id="pop" style="position:absolute;left:0;top:140px;width:200px;height:40px" onclick="window.open('/other.html')">popup</button>
            <input id="name" style="position:absolute;left:0;top:200px;width:200px;height:30px">
            <script>console.log('chrometest-console-line')</script>
            </body></html>
            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(project, "other.html"), "<!doctype html><title>other</title><p>other</p>", new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(project, "file.bin"), new byte[64]);
    }

    // ─────────────────────────── the host ───────────────────────────

    static async Task<(int Code, long Ms)> RunHost(string origin)
    {
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(new ProcessStartInfo(HostExe, origin) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        p.StandardInput.Close();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, sw.ElapsedMilliseconds);
    }

    static async Task H1()
    {
        var (other, _) = await RunHost("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/");
        Check("H1a", "the host refuses any extension but CluadeX's own", other == 1, "exit " + other);
        var (mine, ms) = await RunHost($"chrome-extension://{ChromeBridgeInstall.ExtensionId}/");
        Check("H1b", "a host whose Chrome is gone leaves at once (and no window of the app opens)", mine == 0 && ms < 8000, $"exit {mine} after {ms} ms");
    }

    /// <summary>The row in Settings ▸ Agent ▸ Your Chrome: the real view model on the real connector.</summary>
    static async Task S1(IServiceProvider provider)
    {
        var vm = provider.GetRequiredService<CluadeX.ViewModels.SettingsViewModel>();
        var connector = provider.GetRequiredService<ChromeBridgeConnector>();
        string Text(string key) => LocalizedResources.Instance[key];

        vm.ChromeBridgeOn = false;
        Check("S1a", "off: the tools go back to the built-in browser and the host is gone",
            !connector.Enabled && !ChromeBridgeInstall.IsRegistered && !Browser.UsesChrome && !vm.ChromeBridgeNeedsExtension && vm.ChromeBridgeStatus == Text("chrome.status.off"), vm.ChromeBridgeStatus);
        vm.ChromeBridgeOn = true;
        Check("S1b", "on: the host is registered and the setting holds",
            connector.Enabled && ChromeBridgeInstall.IsRegistered && Browser.UsesChrome && Directory.Exists(connector.ExtensionFolder), connector.ExtensionFolder);
        Check("S1c", "and with nothing connected the row says so and offers the one step left", vm.ChromeBridgeNeedsExtension && vm.ChromeBridgeStatus == Text("chromeBridge.status.waiting"), vm.ChromeBridgeStatus);
        vm.ChromeBridgeOn = true;
        Check("S1d", "pressing it twice changes nothing", connector.Enabled && ChromeBridgeInstall.IsRegistered);
        vm.ChromeBridgeOn = false;
        vm.ChromeBridgeOn = true;
        Check("S1e", "off and on again works, the pipe included", connector.Enabled && ChromeBridgeInstall.IsRegistered && Bridge.LastError == null, Bridge.LastError ?? "");
        Bridge.Stop();

        string xaml = File.ReadAllText(FindUp("CluadeX", "Views", "SettingsView.xaml"));
        foreach (string name in new[] { "ChromeBridgeOn", "ChromeBridgeStatus", "ChromeBridgeNeedsExtension", "AddChromeExtensionCommand" })
            Check("S1f." + name, "the row binds to what the view model has", xaml.Contains("{Binding " + name) && typeof(CluadeX.ViewModels.SettingsViewModel).GetProperty(name) != null);
        foreach (string key in new[] { "settings.chromeBridge", "settings.chromeBridgeDesc", "settings.chromeBridgeAdd", "chromeBridge.status.waiting", "chromeBridge.status.connected", "chromeBridge.status.paused", "chromeBridge.status.noChrome", "chromeBridge.status.steps", "browser.chrome.notConnected", "browser.chrome.paused", "browser.chrome.openFailed", "browser.open.realChrome" })
            Check("S1g." + key, "text exists", Text(key) != key);
        await Task.CompletedTask;
    }

    /// <summary>A file of the repository, found upward from this exe.</summary>
    static string FindUp(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }

    static Task I1()
    {
        Check("I1a", "the host is registered for this user only", ChromeBridgeInstall.IsRegistered);
        string manifest = File.ReadAllText(Path.Combine(ChromeBridgeInstall.ExtensionFolder(Settings.DataRoot), "manifest.json"));
        using var doc = JsonDocument.Parse(manifest);
        Check("I1b", "the extension's files are on disk, with the version CluadeX expects",
            doc.RootElement.GetProperty("version").GetString() == ChromeBridgeInstall.BundledVersion && ChromeBridgeInstall.BundledVersion.Length > 0);
        var perms = doc.RootElement.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()).ToArray();
        Check("I1c", "the extension asks for neither cookies nor browsingData", !perms.Contains("cookies") && !perms.Contains("browsingData"), string.Join(",", perms));
        return Task.CompletedTask;
    }

    // ─────────────────────────── connection ───────────────────────────

    static async Task C1(string extensionId)
    {
        Check("C1a", "Chrome gave the extension the fixed id", extensionId == ChromeBridgeInstall.ExtensionId, extensionId);
        bool up = await Bridge.WaitForConnectionAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
        Check("C1b", "the extension reached CluadeX through the native host, by itself", up, "last error: " + Bridge.LastError);
        if (!up) throw new InvalidOperationException("no connection: nothing further can run");
        Check("C1c", "hello carries the version and Chrome's user agent",
            Bridge.ExtensionVersion == ChromeBridgeInstall.BundledVersion && Bridge.UserAgent?.Contains("Chrome/") == true, $"{Bridge.ExtensionVersion} / {Bridge.UserAgent}");
        Check("C1d", "CluadeX.exe is the relay", RelayProcesses().Count == 1, string.Join(" | ", RelayProcesses()));
        Check("C1e", "the connection is not paused", !Bridge.Paused);
    }

    static async Task C2()
    {
        Check("C2a", "with the Chrome engine the browser tools work with no project open", Module.WorksWithoutProject);
        Check("C2b", "and the core tools are offered without a web project", Module.LocalCoreToolNames.Any());
        await Task.CompletedTask;
    }

    // ─────────────────────────── the tools ───────────────────────────

    static async Task T1()
    {
        int before = await WindowCount();
        var r = await T("browser_open", ("url", BaseUrl + "/index.html"));
        Check("T1a", "browser_open works", r.Success, Seen(r));
        Check("T1b", "the agent is told this is the user's real Chrome", Seen(r).Contains(LocalizedResources.Instance["browser.open.realChrome"]), Seen(r));
        Check("T1c", "the engine is the extension", Browser.Session?.Engine == ChromeExtensionChannel.EngineName, Browser.Session?.Engine ?? "(none)");
        Check("T1d", "it opened one window of its own", await WindowCount() == before + 1, $"{before} → {await WindowCount()}");
        var agent = await Agent();
        var win = (await Windows()).EnumerateArray().First(w => w.GetProperty("id").GetInt32() == agent.GetProperty("windowId").GetInt32());
        Check("T1e", "and left it unfocused: the user keeps typing where they were", !win.GetProperty("focused").GetBoolean(), win.ToString());
        Check("T1f", "the debugger is on the agent's tab and no other", (await AttachedTabs()).SequenceEqual(new[] { agent.GetProperty("tabId").GetInt32() }), string.Join(",", await AttachedTabs()));
    }

    static async Task T2()
    {
        var r = await T("browser_console");
        Check("T2", "the page's console reaches the agent", Seen(r).Contains("chrometest-console-line"), Seen(r));
    }

    static async Task T3()
    {
        var clicked = await Click(100, 30);
        Check("T3a", "a real click lands", clicked.Success && await E("window.clicks") == "1", Seen(clicked));
        await Click(100, 215);
        var typed = await T("browser_type", ("text", "สวัสดี abc"));
        string value = await E("document.getElementById('name').value");
        Check("T3b", "typing lands, Thai included", typed.Success && value.Contains("สวัสดี abc"), Seen(typed) + " / " + value);
    }

    static async Task T4()
    {
        var sw = Stopwatch.StartNew();
        var r = await T("browser_screenshot");
        Check("T4", "a screenshot of the unfocused window comes back", r.Success && Browser.Session?.LastShot is { ImageW: > 0 } && sw.ElapsedMilliseconds < 10_000, $"{sw.ElapsedMilliseconds} ms: {Seen(r)}");
    }

    static async Task T5()
    {
        await Driver.EvalAsync("chrome.windows.update(agent.windowId, {state:'minimized'})");
        var sw = Stopwatch.StartNew();
        var r = await T("browser_screenshot");
        int agentWindow = (await Agent()).GetProperty("windowId").GetInt32();
        var win = (await Windows()).EnumerateArray().First(w => w.GetProperty("id").GetInt32() == agentWindow);
        Check("T5", "the user minimizing the window does not hang the screenshot", r.Success && sw.ElapsedMilliseconds < 12_000 && win.GetProperty("state").GetString() != "minimized", $"{sw.ElapsedMilliseconds} ms, {win}: {Seen(r)}");
    }

    static async Task T6()
    {
        await Driver.EvalAsync("chrome.tabs.create({windowId: agent.windowId, active: true, url: 'about:blank'}).then(t => { self.userTab = t.id; return t.id; })");
        var sw = Stopwatch.StartNew();
        var r = await T("browser_screenshot");
        bool active = (await Driver.EvalAsync("chrome.tabs.get(agent.tabId).then(t => t.active)")).GetBoolean();
        await Driver.EvalAsync("chrome.tabs.remove(self.userTab)");
        Check("T6", "a tab the user opened over the page does not hang the screenshot", r.Success && active && sw.ElapsedMilliseconds < 12_000, $"{sw.ElapsedMilliseconds} ms: {Seen(r)}");
    }

    static async Task T7()
    {
        await Driver.EvalAsync("chrome.windows.create({tabId: agent.tabId, focused: false}).then(w => w.id)");
        await Task.Delay(500);
        var r = await T("browser_screenshot");
        var again = await E("document.title");
        Check("T7", "the user dragging the tab to another window changes nothing", r.Success && again.Contains("chrometest"), Seen(r) + " / " + again);
    }

    // ─────────────────────────── the policy ───────────────────────────

    /// <summary>What a page sees when it tries, and whether the fixture server got the request.</summary>
    static async Task Blocked(string id, string name, string host, string expression)
    {
        lock (Notices) Notices.Clear();
        string seen = await E(expression.Replace("__URL__", Server.Ext(host)));
        await Task.Delay(400);
        Check(id, name, !Server.Saw(host + ".ext.test"), "page saw: " + seen + "\nserver saw: " + Server.Hits(host + ".ext.test"));
    }

    static Task P1() => Blocked("P1", "fetch to an unapproved site never leaves the machine, preflighted POST included", "fetchsite",
        "await Promise.all([fetch('__URL__/fetch'), fetch('__URL__/fetch', {method: 'POST', headers: {'Content-Type': 'application/json', 'X-Secret': '1'}, body: '{}'})]).then(l => 'status ' + l.map(r => r.status), e => 'error ' + e.message)");

    static Task P2() => Blocked("P2", "neither does a beacon, an event source or a frame", "subres",
        "await new Promise(res => { navigator.sendBeacon('__URL__/beacon', 'x'); const s = new EventSource('__URL__/events'); const f = document.createElement('iframe'); f.src = '__URL__/ext'; document.body.append(f); setTimeout(() => { s.close(); f.remove(); res('done'); }, 900); })");

    static Task P3() => Blocked("P3", "nor a fetch from a worker", "workersite",
        "await new Promise(res => { const code = \"fetch('__URL__/fetch').then(r => postMessage('status ' + r.status), e => postMessage('error ' + e.message))\"; const w = new Worker(URL.createObjectURL(new Blob([code], {type:'text/javascript'}))); w.onmessage = e => res(e.data); setTimeout(() => res('timeout'), 8000); })");

    static Task P4() => Blocked("P4", "nor a WebSocket", "wssite",
        "await new Promise(res => { let s; try { s = new WebSocket('__URL__/ws'.replace('http', 'ws')); } catch (e) { res('threw ' + e.message); return; } s.onopen = () => res('open'); s.onerror = () => res('refused'); setTimeout(() => res('timeout'), 3000); })");

    static async Task P5()
    {
        await T("browser_open", ("url", BaseUrl + "/index.html"));
        lock (Notices) Notices.Clear();
        var r = await Click(100, 100);
        await Task.Delay(800);
        string url = Browser.Session?.CurrentUrl ?? "";
        bool noticed;
        lock (Notices) noticed = Notices.Any(n => n.Kind == BrowserNoticeKind.NavigationBlocked);
        Check("P5a", "a link to an unapproved site does not open it", !Server.Saw("blocked.ext.test"), Server.Hits("blocked.ext.test"));
        Check("P5b", "the agent is told", noticed, string.Join(" | ", Notices.Select(n => n.Kind + " " + n.Text)));
        Check("P5c", "and the page stays where it was", url.StartsWith(BaseUrl, StringComparison.Ordinal) && (await E("document.title")).Contains("chrometest"), url + " · " + Seen(r));
        var again = await Click(100, 30);
        Check("P5d", "the session still works afterwards", again.Success, Seen(again));
        await E("location.href = '" + Server.Ext("scripted") + "/ext'; 'moved'");
        await Task.Delay(600);
        await E("const f = document.createElement('form'); f.method = 'post'; f.action = '" + Server.Ext("formsite") + "/put'; document.body.append(f); f.submit(); 'sent'");
        await Task.Delay(600);
        Check("P5e", "neither does a scripted navigation or a form post", !Server.Saw("scripted.ext.test") && !Server.Saw("formsite.ext.test") && (await E("document.title")).Contains("chrometest"), Server.AllHits());
    }

    static async Task P6()
    {
        string origin = Server.Ext("okay");
        Browser.ApproveByUser(origin);
        string seen = await E($"await fetch('{origin}/fetch').then(r => 'status ' + r.status, e => 'error ' + e.message)");
        Check("P6a", "an approved site works", seen.Contains("status 200") && Server.Saw("okay.ext.test"), seen);
        string redirected = await E($"await fetch('{origin}/redir').then(r => 'status ' + r.status, e => 'error ' + e.message)");
        Check("P6b", "and a redirect out of it is held to the same rule", !Server.Saw("other.ext.test") && Server.Saw("/redir"), redirected + "\n" + Server.AllHits());
        var sw = Stopwatch.StartNew();
        var r = await T("browser_open", ("url", origin + "/redir"));
        await Task.Delay(300);
        Check("P6c", "a page load redirected out of it too, and it says so at once", !Server.Saw("other.ext.test") && sw.ElapsedMilliseconds < 8000, $"{sw.ElapsedMilliseconds} ms: {Seen(r)}\n{Server.AllHits()}");
        await T("browser_open", ("url", BaseUrl + "/index.html"));
    }

    // ─────────────────────────── contained ───────────────────────────

    static async Task D1()
    {
        lock (Notices) Notices.Clear();
        int tabs = await TabCount();
        string alerted = await E("alert('hello from the page'); 'after alert'");
        bool noticed;
        lock (Notices) noticed = Notices.Any(n => n.Kind == BrowserNoticeKind.Dialog);
        Check("D1a", "alert does not hang the page, and the agent hears of it", alerted.Contains("after alert") && noticed, alerted);
        string confirmed = await E("confirm('sure?')");
        Check("D1b", "confirm is answered Cancel", confirmed == "false", confirmed);
        Check("D1c", "no tab appeared", await TabCount() == tabs);
    }

    static async Task D2()
    {
        lock (Notices) Notices.Clear();
        int tabs = await TabCount();
        await Click(100, 160);
        bool noticed = await WaitUntil(() => { lock (Notices) return Notices.Any(n => n.Kind == BrowserNoticeKind.PopupBlocked); }, 4000);
        await WaitUntilAsync(async () => await TabCount() == tabs, 3000);
        Check("D2a", "a popup is closed and reported", noticed && await TabCount() == tabs, $"{tabs} → {await TabCount()}, noticed={noticed}");
        // A popup's tab is not under the debugger: closing it afterwards lost the race to its request 3 times in 8.
        int reported = 0;
        for (int i = 0; i < 8; i++)
        {
            lock (Notices) Notices.Clear();
            await E("document.getElementById('pop').onclick = () => window.open('" + Server.Ext("popsite") + "/ext?try=" + i + "'); 'armed'");
            await Click(100, 160);
            if (await WaitUntil(() => { lock (Notices) return Notices.Any(n => n.Kind == BrowserNoticeKind.PopupBlocked); }, 4000)) reported++;
            await WaitUntilAsync(async () => await TabCount() == tabs, 3000);
        }
        Check("D2b", "a popup to an unapproved site never sends its request, and the agent hears of it (8 tries)", reported == 8 && !Server.Saw("popsite.ext.test"), $"reported {reported}/8, server saw: {Server.Hits("popsite.ext.test")}");

        await E("document.getElementById('pop').onclick = e => { e.preventDefault(); const a = document.createElement('a'); a.href = '" + Server.Ext("linksite") + "/ext'; a.target = '_blank'; document.body.append(a); a.click(); }; 'armed'");
        await Click(100, 160);
        await E("document.getElementById('pop').onclick = () => { const f = document.createElement('form'); f.action = '" + Server.Ext("formpop") + "/ext'; f.target = '_blank'; document.body.append(f); f.submit(); }; 'armed'");
        await Click(100, 160);
        await Task.Delay(500);
        Check("D2c", "nor does a target=_blank link or a form", !Server.Saw("linksite.ext.test") && !Server.Saw("formpop.ext.test"), Server.Hits("linksite.ext.test") + Server.Hits("formpop.ext.test"));

        await E("document.getElementById('out').href = '" + Server.Ext("ctrlsite") + "/ext'; 'armed'");
        for (int i = 0; i < 6; i++)
        {
            await T("browser_click", ("x", "100"), ("y", "100"), ("css", "true"), ("modifiers", "ctrl"));
            await T("browser_click", ("x", "100"), ("y", "100"), ("css", "true"), ("modifiers", "shift"));
            await T("browser_click", ("x", "100"), ("y", "100"), ("css", "true"), ("button", "middle"));
        }
        await Task.Delay(500);
        await WaitUntilAsync(async () => await TabCount() == tabs, 3000);
        Check("D2e", "nor a ctrl-, shift- or middle-click on a link (6 tries each)", !Server.Saw("ctrlsite.ext.test") && await TabCount() == tabs, Server.Hits("ctrlsite.ext.test"));
        await E("document.getElementById('count').onclick = e => { window.withCtrl = e.ctrlKey; }; 'armed'");
        await T("browser_click", ("x", "100"), ("y", "30"), ("css", "true"), ("modifiers", "ctrl"));
        Check("D2f", "while ctrl-click on anything else still works", await E("window.withCtrl") == "true");
        var page = await E("(() => { localStorage.setItem('sandbox', '1'); return [window.origin, localStorage.getItem('sandbox')].join(' '); })()");
        Check("D2d", "the sandbox leaves the page its own origin and storage", page.Contains(BaseUrl) && page.Contains(" 1"), page);
    }

    static async Task D3()
    {
        var r = await T("browser_open", ("url", "chrome://settings"));
        Check("D3a", "chrome:// pages stay out of reach", !r.Success, Seen(r));
        var file = await T("browser_open", ("url", "file:///C:/Windows/win.ini"));
        Check("D3b", "and so do files outside the project", !file.Success, Seen(file));
        var project = await T("browser_open", ("url", "other.html"));
        string title = project.Success ? await E("document.title") : "";
        Check("D3c", "a file of the project opens, or says why not", project.Success ? title.Contains("other") : Seen(project).Length > 0 && !Seen(project).Contains("Exception"), Seen(project) + " / " + title);
        var back = await T("browser_open", ("url", BaseUrl + "/index.html"));
        Check("D3d", "and the session carries on either way", back.Success, Seen(back));
    }

    // ─────────────────────────── the profile ───────────────────────────

    static async Task U1()
    {
        await T("browser_open", ("url", BaseUrl + "/index.html"));
        await E("document.cookie = 'kept=1; max-age=600'; localStorage.setItem('kept', '1'); 'set'");
        lock (Notices) Notices.Clear();
        var r = await T("browser_open", ("url", BaseUrl + "/index.html"), ("fresh", "true"));
        string cookie = await E("document.cookie");
        string stored = await E("localStorage.getItem('kept')");
        bool noticed;
        lock (Notices) noticed = Notices.Any(n => n.Kind == BrowserNoticeKind.PermissionDenied);
        Check("U1a", "fresh=true does not sign the user out of anything", r.Success && cookie.Contains("kept=1") && stored.Contains('1'), $"{cookie} / {stored} / {Seen(r)}");
        Check("U1b", "and says it did nothing", noticed);
    }

    static async Task Refused(string id, string name, string method, string parameters)
    {
        try
        {
            var r = await Bridge.RequestAsync("cdp", w =>
            {
                w.WriteString("method", method);
                w.WritePropertyName("params");
                w.WriteRawValue(parameters);
            }, TimeSpan.FromSeconds(5), CancellationToken.None);
            Check(id, name, false, "answered: " + r);
        }
        catch (CdpException ex) { Check(id, name, ex.Message.Contains("not allowed"), ex.Message); }
    }

    static async Task U2()
    {
        await Refused("U2a", "the extension refuses to read cookies", "Network.getAllCookies", "{}");
        await Refused("U2b", "the extension refuses to read cookies another way", "Storage.getCookies", "{}");
        await Refused("U2c", "the extension refuses to clear a site's data", "Storage.clearDataForOrigin", "{\"origin\":\"http://x.test\",\"storageTypes\":\"all\"}");
        await Refused("U2d", "the extension refuses new targets", "Target.createTarget", "{\"url\":\"about:blank\"}");
        await Refused("U2e", "the extension refuses to reach other targets", "Target.attachToTarget", "{\"targetId\":\"x\"}");
        await Refused("U2f", "the extension refuses to close the browser", "Browser.close", "{}");
    }

    static async Task U3()
    {
        string big = new('x', ChromeBridgeServer.MaxCommandBytes + 10);
        bool refused = false;
        try { await Bridge.RequestAsync("cdp", w => { w.WriteString("method", "Runtime.evaluate"); w.WritePropertyName("params"); w.WriteStartObject(); w.WriteString("expression", big); w.WriteEndObject(); }, TimeSpan.FromSeconds(5), CancellationToken.None); }
        catch (CdpException ex) { refused = ex.Message.Contains("too large"); }
        Check("U3a", "a command over Chrome's 1 MB limit is refused here", refused);
        var r = await T("browser_type", ("text", new string('y', 1_200_000)));
        Check("U3b", "as a tool it fails with words", !r.Success && Seen(r).Length > 0 && !Seen(r).Contains("Exception"), Trunc(Seen(r), 300));
        Check("U3c", "and the connection survives", await E("1 + 1") == "2" && Bridge.IsConnected);
    }

    static async Task U4()
    {
        lock (Notices) Notices.Clear();
        await E("const a = document.createElement('a'); a.href = '/file.bin'; a.download = 'file.bin'; a.click(); 'clicked'");
        bool noticed = await WaitUntil(() => { lock (Notices) return Notices.Any(n => n.Kind == BrowserNoticeKind.Download); }, 5000);
        Check("U4", "a download is reported, not hidden", noticed, string.Join(" | ", Notices.Select(n => n.Kind + " " + n.Text)));
    }

    // ─────────────────────────── the user in charge ───────────────────────────

    static async Task R1()
    {
        await T("browser_open", ("url", BaseUrl + "/index.html"));
        int windows = await WindowCount();
        lock (Notices) Notices.Clear();
        await PressIcon();
        bool paused = await WaitUntil(() => Bridge.Paused, 4000);
        Check("R1a", "the icon pauses CluadeX, and the app hears of it", paused);
        Check("R1b", "the debugger lets go at once", await WaitUntilAsync(async () => (await AttachedTabs()).Length == 0, 2000), string.Join(",", await AttachedTabs()));
        Check("R1c", "what the agent was showing stays open", await WindowCount() == windows);
        bool told = await WaitUntil(() => { lock (Notices) return Notices.Any(n => n.Kind == BrowserNoticeKind.BrowserExited); }, 3000);
        Check("R1d", "the agent is told who stopped it", told);
        var r = await T("browser_open", ("url", BaseUrl + "/index.html"));
        Check("R1e", "while paused every tool refuses, in words", !r.Success && Seen(r).Contains(LocalizedResources.Instance["browser.chrome.paused"]), Seen(r));
        Check("R1f", "and opens nothing", await WindowCount() == windows);
        var opened = await Agent();
        Check("R1g", "the extension itself holds no window for the agent", opened.ValueKind == JsonValueKind.Null, opened.ToString());

        await PressIcon();
        Check("R1h", "the icon lets it work again", await WaitUntil(() => !Bridge.Paused, 4000));
        var again = await T("browser_open", ("url", BaseUrl + "/index.html"));
        Check("R1i", "and the next browser_open works", again.Success, Seen(again));
        await Driver.EvalAsync("chrome.windows.getAll({populate:true}).then(l => Promise.all(l.filter(w => w.id !== agent.windowId && w.tabs.some(t => (t.url || '').includes('index.html'))).map(w => chrome.windows.remove(w.id))))");
    }

    static async Task R2()
    {
        await Driver.EvalAsync("chrome.storage.local.set({paused: true}).then(() => setPaused(true))");
        bool paused = await WaitUntil(() => Bridge.Paused, 4000);
        await Driver.EvalAsync("chrome.runtime.reload()").ContinueWith(_ => { });
        await Task.Delay(2500);
        bool up = await Bridge.WaitForConnectionAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
        Check("R2a", "a pause survives the extension restarting", paused && up && await WaitUntil(() => Bridge.Paused, 5000), $"paused={Bridge.Paused} up={up}");
        await WaitUntilAsync(async () => { try { await Driver.EvalAsync("1"); return true; } catch (InvalidOperationException) { return false; } }, 8000);
        await PressIcon();
        Check("R2b", "and ends with the icon", await WaitUntil(() => !Bridge.Paused, 4000));
    }

    static async Task R3()
    {
        var opened = await T("browser_open", ("url", BaseUrl + "/index.html"));
        await Driver.EvalAsync("chrome.windows.remove(agent.windowId)");
        Check("R3a", "the user closing the window ends the session", await WaitUntil(() => !Alive(), 4000), Seen(opened));
        var r = await T("browser_open", ("url", BaseUrl + "/index.html"));
        Check("R3b", "and the next browser_open opens a new one", r.Success, Seen(r));
        var eval = await E("document.title");
        Check("R3c", "which works", eval.Contains("chrometest"), eval);
    }

    static bool Alive() => (typeof(BrowserService).GetField("_channel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Browser) as ICdpChannel)?.IsAlive == true;

    // ─────────────────────────── the connection ───────────────────────────

    static async Task L1()
    {
        using var second = new NamedPipeClientStream(".", ChromeBridgeInstall.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        bool got = true;
        try { await second.ConnectAsync(800); } catch (TimeoutException) { got = false; }
        Check("L1", "a second client waits: it cannot take the connection away", !got && Bridge.IsConnected);
    }

    static async Task L2()
    {
        await T("browser_open", ("url", BaseUrl + "/index.html"));
        Bridge.Stop();
        Check("L2a", "CluadeX going away ends the session", await WaitUntil(() => !Alive() && !Bridge.IsConnected, 4000));
        var refused = await T("browser_open", ("url", BaseUrl + "/index.html"));
        Check("L2b", "with no connection the tools refuse, in words, and never fall back to WebView2",
            !refused.Success && Seen(refused).Contains(LocalizedResources.Instance["browser.chrome.notConnected"]) && Browser.Session == null, Seen(refused));
        Check("L2c", "the debugger lets go of the window", await WaitUntilAsync(async () => (await AttachedTabs()).Length == 0, 5000));
        Bridge.Start();
        Check("L2d", "CluadeX coming back reconnects by itself", await Bridge.WaitForConnectionAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
        var r = await T("browser_open", ("url", BaseUrl + "/index.html"));
        Check("L2e", "and the tools work again", r.Success, Seen(r));
    }

    static async Task L3()
    {
        await Driver.EvalAsync("self.notReloaded = true");
        int hellos = 0;
        void Count() { if (Bridge.IsConnected) hellos++; }
        Bridge.StateChanged += Count;
        Bridge.Stop();
        Bridge.ExpectedVersion = "9.9.9";
        Bridge.Start();
        await Task.Delay(9000);
        Bridge.StateChanged -= Count;
        Bridge.ExpectedVersion = ChromeBridgeInstall.BundledVersion;
        bool reloaded = await WaitUntilAsync(async () =>
        {
            try { return (await Driver.EvalAsync("self.notReloaded === undefined")).GetBoolean(); }
            catch (InvalidOperationException) { return false; }
        }, 5000);
        Check("L3", "another version is told to reload, once, and comes back", reloaded && Bridge.IsConnected && hellos is >= 1 and <= 3, $"reloaded={reloaded} connected={Bridge.IsConnected} hellos={hellos}");
    }

    static async Task L4()
    {
        var r = await T("browser_open", ("url", BaseUrl + "/index.html"));
        Check("L4a", "the session comes up after the reload", r.Success, Seen(r));
        Settings.UpdateSettings(s => s.BrowserEngine = "auto");
        var web = await T("browser_open", ("url", BaseUrl + "/other.html"));
        Check("L4b", "switching the setting off moves the page to the built-in browser", web.Success && Browser.Session?.Engine != ChromeExtensionChannel.EngineName, Seen(web));
        Check("L4c", "and closes the window in Chrome", await WaitUntilAsync(async () => (await AttachedTabs()).Length == 0, 4000));
        Settings.UpdateSettings(s => s.BrowserEngine = BrowserService.ChromeEngine);
        var back = await T("browser_open", ("url", BaseUrl + "/index.html"));
        Check("L4d", "and back", back.Success && Browser.Session?.Engine == ChromeExtensionChannel.EngineName, Seen(back));
    }

    static async Task L5()
    {
        int windows = await WindowCount();
        var r = await T("browser_close");
        Check("L5a", "browser_close removes the window", r.Success && await WindowCount() == windows - 1, $"{windows} → {await WindowCount()}");
        Check("L5b", "and the debugger", (await AttachedTabs()).Length == 0);
        var rules = await Driver.EvalAsync("chrome.declarativeNetRequest.getSessionRules().then(l => l.length)");
        Check("L5c", "and the sandbox rule", rules.GetInt32() == 0, rules.ToString());
    }

    /// <summary>The switch in Settings, with Chrome already open and the extension already in it.</summary>
    static async Task L6()
    {
        Bridge.Stop();
        ChromeBridgeInstall.Uninstall(Settings.DataRoot);
        Check("L6a", "switching off leaves no relay behind", await WaitUntil(() => RelayProcesses().Count == 0, 5000), string.Join(" | ", RelayProcesses()));
        var refused = await T("browser_open", ("url", BaseUrl + "/index.html"));
        Check("L6b", "and the tools refuse in words", !refused.Success && Seen(refused).Contains(LocalizedResources.Instance["browser.chrome.notConnected"]), Seen(refused));

        ChromeBridgeInstall.Install(Settings.DataRoot, HostExe);
        Bridge.Start();
        Check("L6c", "switching on again finds the extension within its next try, a minute at most", await Bridge.WaitForConnectionAsync(TimeSpan.FromSeconds(70), CancellationToken.None));
    }

    /// <summary>The hooks of the app's exit, in the order the app runs them.</summary>
    static async Task Z0()
    {
        var r = await T("browser_open", ("url", BaseUrl + "/index.html"));
        int windows = await WindowCount();
        Browser.OnAppShutdown();
        Bridge.Stop();
        Check("Z0", "CluadeX exiting closes its window in Chrome", r.Success && await WaitUntilAsync(async () => await WindowCount() == windows - 1, 3000), $"{windows} → {await WindowCount()}");
    }

    static async Task Z1()
    {
        Check("Z1", "exactly one relay runs while Chrome is open", RelayProcesses().Count == 1, string.Join(" | ", RelayProcesses()));
        await Task.CompletedTask;
    }

    static async Task Z3()
    {
        bool gone = await WaitUntil(() => RelayProcesses().Count == 0, 8000);
        Check("Z3", "the relay leaves with Chrome", gone, string.Join(" | ", RelayProcesses()));
    }
}
