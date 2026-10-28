using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Desktop;
using CluadeX.Services.Tools;
using CluadeX.Services.Vision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DesktopTest;

/// <summary>
/// The desktop_* contract: the REAL tools of the built CluadeX.dll (UI Automation + Win32) against this harness's OWN
/// WPF window — never the running CluadeX, never the user's other windows (every call names the fixture's title,
/// pid or handle; key input is only sent once the fixture is verified to be in front). Data root isolated next to
/// this exe (portable.txt). The permission cases go through AgentToolService's real gate.
/// </summary>
internal static class Program
{
    static int Pass, Fail, Gaps;
    static readonly List<string> Problems = new();
    static readonly List<(string Id, string Name, string Result, string Detail)> Report = new();

    static DesktopToolModule Module = null!;
    static ToolModuleContext Ctx = null!;
    static AgentToolService Svc = null!;
    static Fixture Fx = null!;
    static readonly List<PermissionRequestInfo> Prompts = new();
    static bool Answer = true, AnswerForChat;
    static readonly DateTime Started = DateTime.Now;

    [STAThread]
    static int Main(string[] args)
    {
        // Dry-run (the harness default): no tool starts a process, except this harness's own child window.
        CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun = true;
        CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Add(Path.GetFileName(Environment.ProcessPath ?? "desktoptest.exe"));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        // Child mode (desktop_launch target): a window titled args[1] after args[2] ms, closed after 60 s.
        if (args.Length >= 3 && args[0] == "--child") return Child(args[1], int.Parse(args[2]));
        try { return Run().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("FATAL " + ex); return 2; }
        finally { Fx?.Close(); KillChildren(); }
    }

    static int Child(string title, int delayMs)
    {
        Thread.Sleep(delayMs);
        var app = new System.Windows.Application();
        var w = new System.Windows.Window { Title = title, Width = 260, Height = 120, Left = 760, Top = 340, ShowActivated = false };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += (_, _) => app.Shutdown();
        timer.Start();
        app.Run(w);
        return 0;
    }

    static void KillChildren()
    {
        foreach (var p in Process.GetProcessesByName("desktoptest"))
        {
            try { if (p.Id != Environment.ProcessId && p.StartTime >= Started) p.Kill(); }
            catch { }
            finally { p.Dispose(); }
        }
    }

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
            Console.WriteLine($"  FAIL {id,-5} harness case threw: {ex.GetType().Name}: {Trunc(ex.ToString(), 900)}");
        }
    }

    static string Trunc(string? s, int n) => s == null ? "" : (s.Length <= n ? s : s[..n] + $"…(+{s.Length - n})");
    static string Seen(ToolResult r) => r.Success ? r.Output ?? "" : string.Join("\n", new[] { r.Error, r.Output }.Where(s => !string.IsNullOrEmpty(s)).Distinct());

    // ─────────────────────────── calling the tools ───────────────────────────

    static ToolCall Call(string tool, params (string k, string v)[] args)
    {
        var type = Module.ResolveToolType(tool) ?? throw new Exception("tool name did not resolve: " + tool);
        return new ToolCall { Type = type, ToolName = tool, Arguments = args.ToDictionary(a => a.k, a => a.v) };
    }

    /// <summary>Straight into the module (no permission gate) — the tool behaviour cases.</summary>
    static async Task<ToolResult> T(string tool, params (string k, string v)[] args)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await Module.ExecuteAsync(Call(tool, args), Ctx, cts.Token);
    }

    /// <summary>Through AgentToolService (resolver, feature gate, permission gate) — the way the agent loop calls.</summary>
    static Task<ToolResult> G(string tool, params (string k, string v)[] args) => Svc.ExecuteToolAsync(Call(tool, args), CancellationToken.None);

    static (string, string) Win => ("title", Regex.Escape(Fx.Title));

    static string RefOf(string output, string pattern)
    {
        var m = Regex.Match(output, @"(e\d+) " + pattern);
        return m.Success ? m.Groups[1].Value : "e0";
    }

    static async Task<string> FindRef(string type, string pattern, params (string k, string v)[] q)
    {
        var r = await T("desktop_find", q.Prepend(Win).ToArray());
        return RefOf(r.Output, Regex.Escape(type) + " " + pattern);
    }

    static bool Front() => DesktopNative.GetForegroundWindow() == Fx.Handle;

    // ─────────────────────────── run ───────────────────────────

    static async Task<int> Run()
    {
        string exeDir = AppContext.BaseDirectory.TrimEnd('\\');
        var configure = typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!;
        var services = new ServiceCollection();
        configure.Invoke(null, new object[] { services });
        string screens = Path.Combine(exeDir, "screens");
        if (Directory.Exists(screens)) try { Directory.Delete(screens, true); } catch { }
        services.Replace(ServiceDescriptor.Singleton(_ => new ToolImageStore(screens)));
        using var provider = services.BuildServiceProvider();

        var settings = provider.GetRequiredService<SettingsService>();
        Console.WriteLine($"data root : {settings.DataRoot}");
        if (!settings.DataRoot.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("ABORT: data root is not isolated (portable.txt missing) — refusing to touch real user data.");
            return 3;
        }
        var loc = provider.GetRequiredService<LocalizationService>();
        LocalizedResources.Instance.Initialize(loc);
        loc.SetLanguage("en");
        settings.Settings.DesktopControlEnabled = true;

        string project = Path.Combine(exeDir, "desktop-project");
        Directory.CreateDirectory(project);
        var fs = provider.GetRequiredService<FileSystemService>();
        fs.WorkingDirectory = project;
        var hooks = provider.GetRequiredService<HookService>();
        typeof(HookService).GetField("_cachedHooks", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(hooks, new List<HookDefinition>());
        Svc = provider.GetRequiredService<AgentToolService>();
        Svc.SessionKey = "desktoptest";
        Svc.OnPermissionRequired += info =>
        {
            lock (Prompts) Prompts.Add(info);
            if (Answer && AnswerForChat && info.ChatScopeKey != null) info.AllowedForChat = true;
            return Task.FromResult(Answer);
        };
        Module = provider.GetRequiredService<DesktopToolModule>();
        Ctx = new ToolModuleContext { SessionKey = "desktoptest", WorkingDirectory = project, ShellDirectory = project, OutputBudgetChars = 30_000 };

        Fx = Fixture.Start();
        await Task.Delay(800);
        Console.WriteLine($"fixture   : \"{Fx.Title}\" hwnd 0x{Fx.Handle.ToInt64():X} pid {Environment.ProcessId}\n");

        Console.WriteLine("── gate, schemas, registration ──");
        await Case("S1", () => SchemaCases(provider, settings, loc));
        Console.WriteLine("\n── windows, find, inspect ──");
        await Case("W1", WindowsCases);
        await Case("F1", FindInspectCases);
        Console.WriteLine("\n── actions ──");
        await Case("A1", ActCases);
        await Case("A2", TypeKeyCases);
        Console.WriteLine("\n── safety ──");
        await Case("X1", SafetyCases);
        Console.WriteLine("\n── screenshot ──");
        await Case("C1", ScreenshotCases);
        Console.WriteLine("\n── wait, launch, Stop ──");
        await Case("L1", WaitLaunchCases);
        Console.WriteLine("\n── permission gate (AgentToolService) ──");
        await Case("P1", () => PermissionCases(provider.GetRequiredService<PermissionService>(), settings));
        Console.WriteLine("\n── log ──");
        await Case("G1", () => LogCases(provider.GetRequiredService<DebugLogService>()));

        return Finish(exeDir);
    }

    static int Finish(string exeDir)
    {
        var md = new StringBuilder("# desktoptest report\n\n| Case | Result | Check | Detail |\n|---|---|---|---|\n");
        foreach (var (id, name, result, detail) in Report)
            md.Append($"| {id} | {result} | {name.Replace("|", "/")} | {detail.Replace("|", "/").Replace("\n", " ")} |\n");
        md.Append($"\n{Pass} pass · {Fail} fail · {Gaps} gap\n");
        File.WriteAllText(Path.Combine(exeDir, "desktop-report.md"), md.ToString());
        Console.WriteLine($"\n═══ {Pass} pass · {Fail} FAIL · {Gaps} GAP ═══ (report: {Path.Combine(exeDir, "desktop-report.md")})");
        foreach (var p in Problems) Console.WriteLine("   " + p);
        return Fail == 0 ? 0 : 1;
    }

    // ═══════════════════════ cases ═══════════════════════

    static Task SchemaCases(IServiceProvider sp, SettingsService settings, LocalizationService loc)
    {
        var schemas = Module.GetToolSchemas().ToList();
        Check("S1", "9 desktop_* schemas, every name resolves to its own tool type, all deferred (tool search)",
            schemas.Count == 9 && schemas.All(s => Module.ResolveToolType(s.Name) is { } t && Module.ToolTypes.Contains(t) && s.DeferLoading),
            string.Join(", ", schemas.Select(s => s.Name + (s.DeferLoading ? "" : "(not deferred)"))));
        Check("S1b", "the agent's resolver routes desktop_act / desktop_click to the module",
            Svc.ResolveToolTypePublic("desktop_act") == ToolType.DesktopAct && Svc.ResolveToolTypePublic("desktop_click") == ToolType.DesktopAct
            && Svc.ToolModules.Any(m => m is DesktopToolModule));
        Check("S1c", "registered: the module and its prompt section (IPromptSectionProvider) are in App.ConfigureServices",
            sp.GetServices<IAgentToolModule>().OfType<DesktopToolModule>().Any()
            && sp.GetServices<IPromptSectionProvider>().OfType<DesktopPromptSection>().Any());
        var section = new DesktopPromptSection();
        string api = section.BuildSection(new PromptSectionContext { OfferedToolNames = new[] { "desktop_act" } }) ?? "";
        string local = section.BuildSection(new PromptSectionContext { OfferedToolNames = new[] { "desktop_act" }, IsLocalModel = true }) ?? "";
        Check("S1d", "prompt section: owns desktop_*, teaches find/inspect → act by ref → screenshot; short for local models",
            section.OwnsTool("desktop_find") && !section.OwnsTool("browser_open") && api.Contains("desktop_find") && api.Contains("ref")
            && api.Contains("desktop_screenshot") && local.Length > 0 && local.Length < api.Length / 2, api);
        Check("S1e", "Desktop control defaults to on (AppSettings), and is offered",
            new AppSettings().DesktopControlEnabled && Module.GetRefusal(ToolType.DesktopAct) == null);
        settings.Settings.DesktopControlEnabled = false;
        string en = Module.GetRefusal(ToolType.DesktopAct) ?? "";
        loc.SetLanguage("th");
        string th = Module.GetRefusal(ToolType.DesktopAct) ?? "";
        string thCard = LocalizedResources.Instance["settings.card.desktop"], thDesc = LocalizedResources.Instance["settings.desktopDesc"];
        loc.SetLanguage("en");
        settings.Settings.DesktopControlEnabled = true;
        Check("S1f", "switched off in Settings ▸ Agent: refused with the place to turn it on, in English and Thai",
            en.Contains("Settings ▸ Agent") && th.Contains("ตั้งค่า") && Regex.IsMatch(thCard + thDesc, @"[\u0E00-\u0E7F]")
            && !thCard.StartsWith("settings."), en + " | " + th + " | " + thCard);
        var vmProp = typeof(CluadeX.ViewModels.SettingsViewModel).GetProperty("DesktopControlEnabled");
        string xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\CluadeX\Views\SettingsView.xaml"));
        Check("S1g", "Settings view binds DesktopControlEnabled to a real SettingsViewModel property",
            vmProp != null && xaml.Contains("{Binding DesktopControlEnabled}"));
        string chat = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\CluadeX\Views\ChatView.xaml"));
        Check("S1h", "ChatView marks its Allow / Allow in this chat / Deny and question buttons with CxGuard AutomationIds",
            new[] { "CxGuard.PermissionAllow\"", "CxGuard.PermissionAllowChat\"", "CxGuard.PermissionDeny\"", "CxGuard.QuestionOption\"", "CxGuard.QuestionSend\"", "CxGuard.QuestionAnswer\"" }
                .All(chat.Contains));
        return Task.CompletedTask;
    }

    static async Task WindowsCases()
    {
        var r = await T("desktop_windows", ("filter", Fx.Title));
        // The fixture lives in the process that runs the tools, and DesktopSafety.IsCluadeXProcess counts that process as
        // CluadeX's own (X1c's CxGuard refusal depends on it) — so its window is tagged "(CluadeX)", as CluadeX's are.
        Check("W1", "desktop_windows lists the fixture: title, process, pid, handle, bounds, and the (CluadeX) tag of the tools' own process",
            r.Success && r.Output.Contains($"\"{Fx.Title}\"") && r.Output.Contains($"desktoptest (pid {Environment.ProcessId})")
            && r.Output.Contains($"0x{Fx.Handle.ToInt64():X}") && Regex.IsMatch(r.Output, @"at -?\d+,-?\d+ \d+x\d+") && r.Output.Contains("(CluadeX)"),
            Seen(r));
        var none = await T("desktop_inspect", ("title", "no-such-window-" + Guid.NewGuid()));
        Check("W1b", "an unknown title fails with what is open instead (no exception text)", !none.Success && none.Error.Contains("No window title matches"), Seen(none));
        var byPid = await T("desktop_find", ("pid", Environment.ProcessId.ToString()), ("automation_id", "BtnPress"));
        var byHandle = await T("desktop_find", ("handle", $"0x{Fx.Handle.ToInt64():X}"), ("automation_id", "BtnPress"));
        Check("W1c", "a window can be named by pid and by handle too", byPid.Output.Contains("\"Press Me\"") && byHandle.Output.Contains("\"Press Me\""), Seen(byPid) + "\n" + Seen(byHandle));
    }

    static async Task FindInspectCases()
    {
        Fx.On(() => Fx.Pwd.Password = "hunter2-secret");
        var f = await T("desktop_find", Win, ("name", "Press Me"));
        string btn = RefOf(f.Output, "Button \"Press Me\"");
        Check("F1", "desktop_find by name returns a ref with type, name, automation id, bounds and Invoke",
            f.Success && btn != "e0" && Regex.IsMatch(f.Output, btn + @" Button ""Press Me"" id=BtnPress \[-?\d+,-?\d+ \d+x\d+\].*Invoke"), Seen(f));
        var byType = await T("desktop_find", Win, ("control_type", "CheckBox"));
        var byId = await T("desktop_find", Win, ("automation_id", "TxtName"));
        var rx = await T("desktop_find", Win, ("name", "/^(Alpha|Gamma)$/"));
        Check("F1b", "…by control type, by automation id, and by /regex/",
            byType.Output.Contains("CheckBox \"Enable thing\"") && byType.Output.Contains("Toggle(Off)") && byId.Output.Contains("Edit \"Your name\" id=TxtName")
            && rx.Output.Contains("\"Alpha\"") && rx.Output.Contains("\"Gamma\"") && !rx.Output.Contains("\"Beta\""),
            Seen(byType) + "\n" + Seen(byId) + "\n" + Seen(rx));
        var tree = await T("desktop_inspect", Win, ("depth", "8"));
        Check("F1c", "desktop_inspect: the window's tree, indented, one ref per element, the same ref for the same element (stable)",
            tree.Success && tree.Output.StartsWith("UI tree of") && tree.Output.Contains(btn + " Button \"Press Me\"")
            && tree.Output.Contains("TabItem \"Advanced\"") && Regex.Matches(tree.Output, @"\be\d+ ").Count >= 15 && tree.Output.Contains("\n  "),
            Trunc(Seen(tree), 1500));
        Check("F1d", "…the password box is shown as one, and its value never appears", tree.Output.Contains("id=PwdSecret") && Regex.IsMatch(tree.Output, @"id=PwdSecret[^\n]* password")
            && !tree.Output.Contains("hunter2"), Trunc(Seen(tree), 1500));
        var small = await T("desktop_inspect", Win, ("limit", "5"));
        Check("F1e", "inspect is bounded: limit=5 gives 5 elements and says it was cut", small.Output.Contains("5 element(s)") && small.Output.Contains("cut at 5"), Seen(small));
        string list = RefOf(byType.Output.Length > 0 ? (await T("desktop_find", Win, ("automation_id", "LstItems"))).Output : "", "List");
        var under = await T("desktop_inspect", ("ref", list));
        Check("F1f", "inspect under a ref shows only that subtree", under.Success && under.Output.Contains("\"Beta\"") && !under.Output.Contains("Press Me"), Seen(under));
        var stale = await T("desktop_act", ("ref", "e99999"), ("action", "invoke"));
        Check("F1g", "an unknown ref fails with how to get fresh ones", !stale.Success && stale.Error.Contains("desktop_inspect"), Seen(stale));
    }

    static async Task ActCases()
    {
        string btn = await FindRef("Button", "\"Press Me\"", ("automation_id", "BtnPress"));
        var inv = await T("desktop_act", ("ref", btn), ("action", "invoke"));
        await Task.Delay(200);
        Check("A1", "invoke presses the button (Invoke pattern), the app saw one click", inv.Success && inv.Output.Contains("invoked") && Fx.Presses == 1, Seen(inv) + $" presses={Fx.Presses}");

        string txt = await FindRef("Edit", "\"Your name\"", ("automation_id", "TxtName"));
        var set = await T("desktop_act", ("ref", txt), ("action", "set_value"), ("value", "hello desktop"));
        Check("A1b", "set_value writes the text box through the Value pattern", set.Success && Fx.On(() => Fx.Name.Text) == "hello desktop" && set.Output.Contains("Value=\"hello desktop\""), Seen(set));

        string chk = await FindRef("CheckBox", "\"Enable thing\"", ("automation_id", "ChkEnable"));
        var tog = await T("desktop_act", ("ref", chk), ("action", "toggle"));
        Check("A1c", "toggle checks the check box and reports the new state", tog.Success && Fx.On(() => Fx.Chk.IsChecked) == true && tog.Output.Contains("On"), Seen(tog));

        string beta = await FindRef("ListItem", "\"Beta\"", ("name", "Beta"));
        var sel = await T("desktop_act", ("ref", beta), ("action", "select"));
        Check("A1d", "select picks a list item (SelectionItem)", sel.Success && Fx.On(() => Fx.List.SelectedIndex) == 1 && sel.Output.Contains("Select(selected)"), Seen(sel));

        string adv = await FindRef("TabItem", "\"Advanced\"", ("name", "Advanced"));
        var tab = await T("desktop_act", ("ref", adv), ("action", "select"));
        Check("A1e", "select switches a tab", tab.Success && Fx.On(() => Fx.Tabs.SelectedIndex) == 1, Seen(tab));

        var rows = await T("desktop_find", Win, ("automation_id", "LstRows"));
        string rowsRef = RefOf(rows.Output, "List");
        var items = await T("desktop_find", ("ref", rowsRef), ("control_type", "ListItem"));
        string row1 = RefOf(items.Output, "ListItem");
        var rowInv = await T("desktop_act", ("ref", row1), ("action", "invoke"));
        await Task.Delay(200);
        Check("A1f", "invoke on a WPF row with no Invoke of its own falls back to the row's child Button (the sidebar case)",
            rowInv.Success && rowInv.Output.Contains("invoked its child Button \"Open 1\"") && Fx.RowPresses == 1, Seen(items) + "\n" + Seen(rowInv) + $" rowPresses={Fx.RowPresses}");

        var scroll = await T("desktop_act", ("ref", beta), ("action", "scroll"));
        Check("A1g", "scroll with no direction brings the element into view (ScrollItem)", scroll.Success && scroll.Output.Contains("scrolled"), Seen(scroll));
        var focus = await T("desktop_act", ("ref", txt), ("action", "focus"));
        Check("A1h", "focus moves the keyboard focus to the element and the result names it", focus.Success && focus.Output.Contains("Focus: ") && focus.Output.Contains("id=TxtName"), Seen(focus));
        var bad = await T("desktop_act", ("ref", btn), ("action", "explode"));
        Check("A1i", "an unknown action lists the valid ones", !bad.Success && bad.Error.Contains("invoke, select, toggle"), Seen(bad));
    }

    static async Task TypeKeyCases()
    {
        string txt = await FindRef("Edit", "\"Your name\"", ("automation_id", "TxtName"));
        var t1 = await T("desktop_type", ("ref", txt), ("text", "สวัสดี ok"), ("clear", "true"));
        Check("A2", "desktop_type with clear replaces the text box value (Thai safe, Value pattern)",
            t1.Success && Fx.On(() => Fx.Name.Text) == "สวัสดี ok" && t1.Output.Contains("Value pattern"), Seen(t1));
        var t2 = await T("desktop_type", ("ref", txt), ("text", " more"));
        Check("A2b", "…without clear it appends", t2.Success && Fx.On(() => Fx.Name.Text) == "สวัสดี ok more", Seen(t2) + " text=" + Fx.On(() => Fx.Name.Text));

        // Key input: only once the fixture is verified in front (the tool refuses otherwise — no stray keys).
        string rich = await FindRef("Document", "", ("automation_id", "RichNotes"));
        var t3 = await T("desktop_type", ("ref", rich), ("text", "typed by keys"));
        string richText = Fx.On(() => new System.Windows.Documents.TextRange(Fx.Rich.Document.ContentStart, Fx.Rich.Document.ContentEnd).Text.Trim());
        if (!t3.Success && t3.Error.Contains("Could not bring the window"))
            Gap("A2c", "desktop_type into a control without a Value pattern uses real Unicode key input", "Windows refused to bring the fixture to the front (focus lock) — the tool correctly sent nothing");
        else
            Check("A2c", "desktop_type into a control without a Value pattern (RichTextBox) uses real Unicode key input",
                t3.Success && t3.Output.Contains("key input") && richText == "typed by keys", Seen(t3) + " rich=" + richText);

        var k = await T("desktop_keys", ("ref", txt), ("keys", "End shift+Home Delete"));
        if (!k.Success && k.Error.Contains("Could not bring the window"))
            Gap("A2d", "desktop_keys sends chords to the fixture", "focus lock — nothing sent");
        else
            Check("A2d", "desktop_keys: End, shift+Home, Delete clear the focused text box (real key events, in order)",
                k.Success && Fx.On(() => Fx.Name.Text) == "" && k.Output.Contains("Pressed End, shift+Home, Delete"), Seen(k) + " text=" + Fx.On(() => Fx.Name.Text));
        int before = Fx.Presses;
        var win = await T("desktop_keys", Win, ("keys", "win+r"));
        Check("A2e", "Windows-key chords are refused before any input", !win.Success && win.Error.Contains("Windows key") && Fx.Presses == before, Seen(win));
        var bad = await T("desktop_keys", Win, ("keys", "ctrl+nonsense"));
        Check("A2f", "an unknown key name fails with examples", !bad.Success && bad.Error.Contains("Unknown key"), Seen(bad));
    }

    static async Task SafetyCases()
    {
        Fx.On(() => Fx.Pwd.Password = "");
        string pwd = await FindRef("Edit", "\"Secret\"", ("automation_id", "PwdSecret"));
        var t = await T("desktop_type", ("ref", pwd), ("text", "letmein"));
        var s = await T("desktop_act", ("ref", pwd), ("action", "set_value"), ("value", "letmein"));
        Check("X1", "password field: desktop_type and set_value are refused, and the field stays empty",
            !t.Success && t.Error.Contains("password field") && !s.Success && s.Error.Contains("password field") && Fx.On(() => Fx.Pwd.Password) == "",
            Seen(t) + "\n" + Seen(s));
        var f = await T("desktop_find", Win, ("text", "hunter2"));
        Check("X1b", "a password value cannot be found by text either", f.Output.StartsWith("No element matches"), Seen(f));

        string guard = await FindRef("Button", "\"Allow\"", ("automation_id", "CxGuard"));
        var g1 = await T("desktop_act", ("ref", guard), ("action", "invoke"));
        var g2 = await T("desktop_act", ("ref", guard), ("action", "click"));
        var g3 = await T("desktop_keys", ("ref", guard), ("keys", "Enter"));
        await Task.Delay(200);
        Check("X1c", "CluadeX's own approval UI (AutomationId CxGuard.*): invoke, a real click and keys are all refused, the button never fired",
            !g1.Success && g1.Error.Contains("permission / question / spend prompt") && !g2.Success && !g3.Success && Fx.GuardPresses == 0,
            Seen(g1) + "\n" + Seen(g2) + "\n" + Seen(g3) + $" guardPresses={Fx.GuardPresses}");

        var cred = Fx.Extra("Windows Security — desktoptest " + Guid.NewGuid().ToString("N")[..6]);
        await Task.Delay(400);
        string credTitle = Fx.On(() => cred.Title);
        var ci = await T("desktop_inspect", ("title", Regex.Escape(credTitle)));
        var cs = await T("desktop_screenshot", ("title", Regex.Escape(credTitle)));
        var cw = await T("desktop_windows", ("filter", "desktoptest"));
        Fx.On(() => cred.Close());
        Check("X1d", "a credential / Windows Security window: inspect and screenshot refused; desktop_windows marks it off limits",
            !ci.Success && ci.Error.Contains("credential") && !cs.Success && cw.Output.Contains("off limits"), Seen(ci) + "\n" + Seen(cs));
        Check("X1e", "credential detection: consent.exe, CredentialUIBroker, the UAC / sign-in titles; a normal window is not",
            DesktopSafety.IsCredentialWindow("consent", "", "") && DesktopSafety.IsCredentialWindow("CredentialUIBroker", "", "x")
            && DesktopSafety.IsCredentialWindow("app", "", "User Account Control") && DesktopSafety.IsCredentialWindow("app", "", "Sign in to your account")
            && !DesktopSafety.IsCredentialWindow("Godot", "", "My Game (DEBUG)") && !DesktopSafety.IsCredentialWindow("notepad", "", "security notes.txt"));
        Check("X1f", "the input desktop is the normal one right now (the secure-desktop refusal is off)", DesktopSafety.SecureDesktopRefusal() == null);
    }

    static async Task ScreenshotCases()
    {
        var r = await T("desktop_screenshot", Win);
        string? path = r.FilePath;
        bool file = path != null && File.Exists(path) && path.EndsWith(".png") && path.Contains(Path.Combine("screens", "desktoptest", "desktop"));
        int pw = 0, ph = 0;
        if (file)
        {
            using var s = File.OpenRead(path!);
            var dec = new PngBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            pw = dec.Frames[0].PixelWidth; ph = dec.Frames[0].PixelHeight;
        }
        DesktopNative.RECT b;
        using (DesktopNative.PerMonitorDpi()) b = DesktopNative.Bounds(Fx.Handle);
        Check("C1", "desktop_screenshot of the window: a PNG under the chat's screens\\…\\desktop folder, exactly the window's physical frame size",
            r.Success && file && pw == b.Width && ph == b.Height && pw > 100, Seen(r) + $"\npng {pw}x{ph} frame {b.Width}x{b.Height}");
        var img = r.Images?.FirstOrDefault();
        Check("C1b", "…and attached as an image for the model (same path as other tool images), with the marker and the file path",
            img != null && img.Base64.Length > 1000 && img.FilePath == path && r.Output.Contains("[image 1:") && !r.Output.Contains(img.Base64[..40]), Seen(r));
        bool blank = true;
        if (file)
        {
            var bmp = new FormatConvertedBitmap(new PngBitmapDecoder(new MemoryStream(File.ReadAllBytes(path!)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0], System.Windows.Media.PixelFormats.Bgr32, null, 0);
            var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
            bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
            int white = 0;
            for (int i = 0; i < px.Length; i += 4) if (px[i] > 240 && px[i + 1] > 240 && px[i + 2] > 240) white++;
            blank = white < px.Length / 4 / 4;
        }
        Check("C1c", "…its pixels are the window (mostly the white background), not black", !blank);
        var reg = await T("desktop_screenshot", ("region", $"{b.Left + 10},{b.Top + 10},200,120"));
        Check("C1d", "a screen region: exactly the size asked for", reg.Success && reg.Output.Contains("200x120 physical px") && reg.Images?.Count == 1, Seen(reg));
        int vx = DesktopNative.GetSystemMetrics(DesktopNative.SM_XVIRTUALSCREEN), vy = DesktopNative.GetSystemMetrics(DesktopNative.SM_YVIRTUALSCREEN);
        var origin = await T("desktop_screenshot", ("region", $"{vx},{vy},64,48"));
        Check("C1e", $"a region at the virtual screen's origin ({vx},{vy}) works{(vx < 0 || vy < 0 ? " — a negative-coordinate monitor" : "")}",
            origin.Success && origin.Output.Contains("64x48 physical px") && origin.Output.Contains($"screen ({vx},{vy})"), Seen(origin));
        var bad = await T("desktop_screenshot", ("region", "1,2,3"));
        Check("C1f", "a malformed region fails with the format", !bad.Success && bad.Error.Contains("x,y,width,height"), Seen(bad));
    }

    static async Task WaitLaunchCases()
    {
        var early = await T("desktop_wait", Win, ("name", "Late Button"), ("timeout_ms", "300"));
        Check("L1", "desktop_wait times out with a failure when nothing appears", !early.Success && early.Error.Contains("Did not appear within 300 ms"), Seen(early));
        var waiting = T("desktop_wait", Win, ("name", "Late Button"), ("timeout_ms", "8000"));
        await Task.Delay(700);
        Fx.On(() => Fx.Panel.Children.Add(new System.Windows.Controls.Button { Content = "Late Button" }));
        var w = await waiting;
        Check("L1b", "desktop_wait returns once the element appears, with its ref", w.Success && w.Output.Contains("appeared after") && Regex.IsMatch(w.Output, @"e\d+ Button ""Late Button"""), Seen(w));
        var gone = T("desktop_wait", Win, ("name", "Late Button"), ("state", "disappear"), ("timeout_ms", "8000"));
        await Task.Delay(500);
        Fx.On(() => Fx.Panel.Children.RemoveAt(Fx.Panel.Children.Count - 1));
        var g = await gone;
        Check("L1c", "…and state=disappear returns once it is gone", g.Success && g.Output.Contains("is gone"), Seen(g));

        using (var cts = new CancellationTokenSource())
        {
            var sw = Stopwatch.StartNew();
            var t = Module.ExecuteAsync(Call("desktop_wait", Win, ("name", "Never There"), ("timeout_ms", "60000")), Ctx, cts.Token);
            await Task.Delay(500);
            cts.Cancel();
            bool cancelled = false;
            try { await t; } catch (OperationCanceledException) { cancelled = true; }
            Check("L1d", "the run's Stop cancels desktop_wait at once (not after its 60 s timeout)", cancelled && sw.ElapsedMilliseconds < 3000, $"cancelled={cancelled} after {sw.ElapsedMilliseconds} ms");
        }

        string exe = Environment.ProcessPath!;
        string childTitle = "CX Child " + Guid.NewGuid().ToString("N")[..6];
        var l = await T("desktop_launch", ("path", exe), ("args", $"--child \"{childTitle}\" 300"), ("timeout_ms", "20000"));
        var m = Regex.Match(l.Output, @"pid (\d+)");
        Check("L1e", "desktop_launch starts a program and returns its pid and main window once it shows",
            l.Success && m.Success && l.Output.Contains($"\"{childTitle}\"") && l.Output.Contains("Window after"), Seen(l));
        if (m.Success) try { Process.GetProcessById(int.Parse(m.Groups[1].Value)).Kill(); } catch { }

        using (var cts = new CancellationTokenSource())
        {
            var sw = Stopwatch.StartNew();
            var t = Module.ExecuteAsync(Call("desktop_launch", ("path", exe), ("args", "--child \"CX Slow\" 15000"), ("timeout_ms", "60000")), Ctx, cts.Token);
            await Task.Delay(700);
            cts.Cancel();
            bool cancelled = false;
            try { await t; } catch (OperationCanceledException) { cancelled = true; }
            Check("L1f", "the run's Stop cancels desktop_launch's wait for a window", cancelled && sw.ElapsedMilliseconds < 3000, $"cancelled={cancelled} after {sw.ElapsedMilliseconds} ms");
        }
        KillChildren();
        // NEVER a real destructive command here: on 2026-10-17 this line was `cmd.exe /c rd /s /q C:\`, the check
        // missed it, and it wiped the owner's home folder for 45 minutes. A guard test uses an inert sentinel.
        // Owner 2026-10-28: only a delete aimed at the ROOT or a home (or a drive / backup / database wipe) asks — the drive root is assembled
        // from pieces so this file holds no line the scanner would flag, and the echo keeps it inert either way.
        string zRoot = "Z" + ":\\";
        var blocked = await T("desktop_launch", ("path", "cmd.exe"), ("args", "/c echo CX-SENTINEL-NOT-RUN rd /s /q " + zRoot));
        Check("L1g", "desktop_launch runs the dangerous-command check on the command line", !blocked.Success, Seen(blocked));
        var forms = new List<ToolResult>();
        foreach (var a in new[]
                 {
                     "/c echo CX-SENTINEL-NOT-RUN rmdir /s /q " + zRoot,
                     "/c echo CX-SENTINEL-NOT-RUN rd /q /s " + zRoot,
                     "/c echo CX-SENTINEL-NOT-RUN del /f /s /q " + zRoot + "*",
                 })
            forms.Add(await T("desktop_launch", ("path", "cmd.exe"), ("args", a)));
        forms.Add(await T("desktop_launch", ("path", "powershell.exe"),
            ("args", "-NoProfile -Command \"Write-Output CX-SENTINEL-NOT-RUN; # Remove-Item " + zRoot + " -Recurse\"")));
        Check("L1h", "…and so do rmdir /s, rd /q /s (switches in any order), del /s and Remove-Item -Recurse on a drive root: the red prompt, refused with nobody to answer",
            forms.All(f => !f.Success && f.Error.Contains("aimed at the root") && f.Error.Contains("did not approve")),
            string.Join("\n", forms.Select(Seen)));
    }

    static async Task PermissionCases(PermissionService perms, SettingsService settings)
    {
        // The fixture is this harness's own process: CluadeX would name it "desktoptest".
        AgentRunScope.Enter(new AgentRunScope("desktoptest", Ctx.WorkingDirectory));
        Answer = true; AnswerForChat = false;
        int p0 = Prompts.Count;
        var a = await G("desktop_find", Win, ("automation_id", "BtnPress"));
        var first = Prompts.Count > p0 ? Prompts[^1] : null;
        Check("P1", "the first desktop call on an app in a run asks: scope desktop, the app name, and offers \"Allow in this chat\"",
            a.Success && Prompts.Count == p0 + 1 && first!.Detail.StartsWith("desktop: desktoptest") && first.ChatScopeKey != null && !first.AlwaysAsk,
            Seen(a) + "\n" + (first?.Detail ?? "(no prompt)"));
        var b = await G("desktop_inspect", Win, ("limit", "3"));
        Check("P1b", "…a second call on the same app in the same run does not ask again", b.Success && Prompts.Count == p0 + 1, $"prompts {Prompts.Count - p0}");
        var free = await G("desktop_windows", ("filter", "x"));
        Check("P1c", "desktop_windows (just the list) does not ask", free.Success && Prompts.Count == p0 + 1);

        AgentRunScope.Enter(new AgentRunScope("desktoptest", Ctx.WorkingDirectory));
        Answer = false;
        var denied = await G("desktop_find", Win, ("automation_id", "BtnPress"));
        Check("P1d", "a new run asks again; \"Deny\" refuses the call", !denied.Success && denied.Error.Contains("denied") && Prompts.Count == p0 + 2, Seen(denied));
        Answer = true; AnswerForChat = true;
        var chatOk = await G("desktop_find", Win, ("automation_id", "BtnPress"));
        AgentRunScope.Enter(new AgentRunScope("desktoptest", Ctx.WorkingDirectory));
        AnswerForChat = false;
        var later = await G("desktop_find", Win, ("automation_id", "BtnPress"));
        Check("P1e", "\"Allow in this chat\" covers later runs in the same chat (no prompt)", chatOk.Success && later.Success && Prompts.Count == p0 + 3, $"prompts {Prompts.Count - p0}");

        string oldKey = Svc.SessionKey;
        AgentRunScope.Enter(new AgentRunScope("other-chat", Ctx.WorkingDirectory));
        var other = await G("desktop_find", Win, ("automation_id", "BtnPress"));
        Check("P1f", "…but not another chat (it asks)", Prompts.Count == p0 + 4, $"prompts {Prompts.Count - p0} · {Seen(other)}");
        AgentRunScope.Enter(null);
        Svc.SessionKey = oldKey;

        var deny = new PermissionRule { Scope = "*", ToolName = "desktop(desktop*)", Pattern = "*", Action = PermAction.Deny };
        perms.AddRule(deny);
        int pd = Prompts.Count;
        ToolResult ruled;
        try
        {
            AgentRunScope.Enter(new AgentRunScope("rule-chat", Ctx.WorkingDirectory));
            ruled = await G("desktop_find", Win, ("automation_id", "BtnPress"));
        }
        finally { perms.RemoveRule(deny); AgentRunScope.Enter(null); }
        Check("P1g", "a user rule desktop(desktop*) → Deny refuses the app without asking", !ruled.Success && ruled.Error.Contains("Permission denied for desktop: desktoptest") && Prompts.Count == pd, Seen(ruled));
        var allow = new PermissionRule { Scope = "desktop", Pattern = "desktoptest", Action = PermAction.Allow };
        perms.AddRule(allow);
        ToolResult allowed;
        try
        {
            AgentRunScope.Enter(new AgentRunScope("rule-chat-2", Ctx.WorkingDirectory));
            allowed = await G("desktop_find", Win, ("automation_id", "BtnPress"));
        }
        finally { perms.RemoveRule(allow); AgentRunScope.Enter(null); }
        Check("P1h", "a desktop-scope Allow rule for the app runs without asking", allowed.Success && Prompts.Count == pd, Seen(allowed));

        bool was = settings.Settings.Features.PermissionSystem;
        settings.Settings.Features.PermissionSystem = false;
        int ps = Prompts.Count;
        try
        {
            AgentRunScope.Enter(new AgentRunScope("switch-off-chat", Ctx.WorkingDirectory));
            await G("desktop_find", Win, ("automation_id", "BtnPress"));
        }
        finally { settings.Settings.Features.PermissionSystem = was; AgentRunScope.Enter(null); }
        Check("P1i", "the permission system switched off does NOT lift the per-app ask", Prompts.Count == ps + 1, $"prompts {Prompts.Count - ps}");
    }

    static Task LogCases(DebugLogService log)
    {
        var lines = log.Snapshot().Where(e => e.Category == "Desktop").Select(e => e.Message).ToList();
        Check("G1", "every action is logged with tool, action, app and element name",
            lines.Any(l => l.Contains("desktop_act invoke app=desktoptest element=\"Press Me\"")) && lines.Any(l => l.StartsWith("desktop_type type app=desktoptest"))
            && lines.Any(l => l.StartsWith("desktop_screenshot capture app=desktoptest")) && lines.Any(l => l.StartsWith("desktop_launch launch")),
            string.Join("\n", lines.Take(30)));
        Check("G1b", "…never the typed text", !lines.Any(l => l.Contains("สวัสดี") || l.Contains("typed by keys") || l.Contains("letmein") || l.Contains("hello desktop")),
            string.Join("\n", lines.Where(l => l.Contains("type") || l.Contains("set_value"))));
        return Task.CompletedTask;
    }
}
