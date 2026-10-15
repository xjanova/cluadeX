using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Mcp;
using CluadeX.Services.Providers;
using CluadeX.Services.Tools;
using CluadeX.Services.Vision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SeamTest;

/// <summary>
/// The S0 scaffold contract: every extension seam the feature teams build on (tool modules, MCP tool policies,
/// MCP launch resolvers, shell interceptors, prompt sections, shutdown hooks, tool images, the Dev Preview gate)
/// is driven through the REAL built CluadeX.dll with fake implementations registered exactly as a team would
/// register them — on top of App.ConfigureServices. No model, no network, no paid API; the data root is
/// isolated next to this exe (portable.txt), and the one child process it starts is this exe as a stub MCP server.
/// </summary>
internal static class Program
{
    static int Pass, Fail;
    static readonly List<string> Problems = new();

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--stub-mcp") return StubMcp.Run();
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        try { return Run().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("FATAL " + ex); return 2; }
    }

    static void Check(string id, string name, bool ok, string detail = "")
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS {id,-5} {name}"); return; }
        Fail++; Problems.Add($"FAIL {id} {name}");
        Console.WriteLine($"  FAIL {id,-5} {name}");
        if (detail.Length > 0) Console.WriteLine("            " + Trunc(detail, 600).Replace("\n", "\n            "));
    }

    static async Task Case(string id, Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex)
        {
            Fail++; Problems.Add($"FAIL {id} threw {ex.GetType().Name}");
            Console.WriteLine($"  FAIL {id,-5} harness case threw: {ex.GetType().Name}: {Trunc(ex.Message, 300)}");
        }
    }

    static string Trunc(string? s, int n) => s == null ? "" : (s.Length <= n ? s : s[..n] + $"…(+{s.Length - n})");
    static string Seen(ToolResult r) => r.Success ? r.Output ?? "" : string.Join("\n", new[] { r.Error, r.Output }.Where(s => !string.IsNullOrEmpty(s)));

    // ── the fakes, registered like a team would ──
    static readonly FakeModule Module = new();
    static readonly FakePolicy Policy = new();
    static readonly FakeInterceptor Interceptor = new();
    static readonly FakeResolver Resolver = new();
    static readonly CountingHook Hook = new();

    // permission prompts the "user" sees, and what the user answers
    static readonly List<string> Prompts = new();
    static bool Answer = true;

    static AgentToolService Svc = null!;

    static async Task<ToolResult> T(string tool, params (string k, string v)[] args)
    {
        var type = Svc.ResolveToolTypePublic(tool) ?? throw new Exception("tool name did not resolve: " + tool);
        var call = new ToolCall { Type = type, ToolName = tool, Arguments = args.ToDictionary(a => a.k, a => a.v) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await Svc.ExecuteToolAsync(call, cts.Token);
    }

    static async Task<int> Run()
    {
        var configure = typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!;
        var services = new ServiceCollection();
        configure.Invoke(null, new object[] { services });
        // The real feature modules (bg_*, browser_*) and shell interceptors register in App.ConfigureServices too.
        // This contract drives every seam with fakes only — and the fakes claim the same tool types — so the real
        // ones are taken out first (each team's own harness tests its real module).
        services.RemoveAll<IAgentToolModule>();
        services.RemoveAll<IShellCommandInterceptor>();
        services.AddSingleton<IAgentToolModule>(Module);
        services.AddSingleton<IMcpToolPolicy>(new ThrowingPolicy());   // a broken policy first: it must not take the tool
        services.AddSingleton<IMcpToolPolicy>(Policy);
        services.AddSingleton<IShellCommandInterceptor>(new ThrowingInterceptor());   // a throw = no opinion
        services.AddSingleton<IShellCommandInterceptor>(Interceptor);
        services.AddSingleton<IMcpLaunchResolver>(Resolver);
        services.AddSingleton<IPromptSectionProvider>(new FakeSection("fake-chrome", n => n.StartsWith("mcp__brainx-brain__chrome__", StringComparison.Ordinal), "FAKE-CHROME-SECTION"));
        services.AddSingleton<IPromptSectionProvider>(new FakeSection("absent", n => n == "never_offered", "ABSENT-SECTION"));
        services.AddSingleton<IAppShutdownHook>(new ThrowingHook());
        services.AddSingleton<IAppShutdownHook>(Hook);
        using var provider = services.BuildServiceProvider();

        var settings = provider.GetRequiredService<SettingsService>();
        string exeDir = AppContext.BaseDirectory.TrimEnd('\\');
        Console.WriteLine($"data root : {settings.DataRoot}");
        if (!settings.DataRoot.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("ABORT: data root is not isolated (portable.txt missing) — refusing to touch real user data.");
            return 3;
        }

        string project = Path.Combine(exeDir, "seam-project");
        if (Directory.Exists(project)) Directory.Delete(project, true);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "index.html"), "<h1>seam</h1>");

        var fs = provider.GetRequiredService<FileSystemService>();
        var perms = provider.GetRequiredService<PermissionService>();
        var mcp = provider.GetRequiredService<McpServerManager>();
        var hooks = provider.GetRequiredService<HookService>();
        Svc = provider.GetRequiredService<AgentToolService>();
        Svc.OnPermissionRequired += info => { lock (Prompts) Prompts.Add(info.Detail); return Task.FromResult(Answer); };
        fs.WorkingDirectory = project;
        // The real ~/.cluadex hooks (the user's own, and the bundled library) are NOT part of this contract and
        // spawn PowerShell on every shell call — keep this harness off them.
        SilenceHooks(hooks);

        // MCP tools reach the agent through the registry — fill it like a running server would.
        mcp.ToolRegistry.UpdateServer("brainx-brain", new List<McpTool>
        {
            Tool("brainx-brain", "chrome__navigate", "Go to a URL; with several tabs ask with AskUserQuestion."),
            Tool("brainx-brain", "chrome__read_page", "Read the page."),
            Tool("brainx-brain", "chrome__computer", "Click and type."),
            Tool("brainx-brain", "chrome__javascript_tool", "Run JS."),
            Tool("brainx-brain", "chrome__shortcuts_execute", "Run a shortcut."),
            Tool("brainx-brain", "chrome__file_upload", "Upload a local file."),
            Tool("brainx-brain", "brain_search", "Search the brain."),
        });
        mcp.ToolRegistry.UpdateServer("plain", new List<McpTool> { Tool("plain", "do_thing", "Does a thing.") });

        Console.WriteLine($"project   : {project}\n");

        // ═══════════════════ types ═══════════════════
        Console.WriteLine("── S0 types ──");
        await Case("Y1", () =>
        {
            string b64 = Convert.ToBase64String(new byte[3000]);
            var img = new ToolImage("image/jpeg", b64, 320, 160, @"C:\x\001.jpg", "cap");
            Check("Y1", "ToolImage.ToString never prints the base64 (logs, exception text)", !img.ToString().Contains(b64[..40]) && img.ToString().Contains("320x160"), img.ToString());
            string tr = JsonSerializer.Serialize(new ToolResult { Output = "o", Images = new() { img } });
            string cb = JsonSerializer.Serialize(new ContentBlock { Type = "tool_result", Content = "c", Images = new() { img } });
            Check("Y1b", "ToolResult.Images and ContentBlock.Images are never serialized (no base64 in a DB or log)", !tr.Contains(b64[..40]) && !cb.Contains(b64[..40]), tr + " | " + cb);
            return Task.CompletedTask;
        });
        await Case("Y2", () =>
        {
            bool Safe(ToolType t) => new ToolCall { Type = t }.IsConcurrencySafe && new ToolCall { Type = t }.IsReadOnly;
            var readers = new[] { ToolType.BgOutput, ToolType.BgList, ToolType.BrowserScreenshot, ToolType.BrowserConsole, ToolType.BrowserNetwork, ToolType.BrowserRead, ToolType.BrowserFind };
            var actors = new[] { ToolType.BgStart, ToolType.BgServe, ToolType.BgStop, ToolType.BrowserOpen, ToolType.BrowserClick, ToolType.BrowserPointer, ToolType.BrowserType, ToolType.BrowserKey, ToolType.BrowserScroll, ToolType.BrowserEval, ToolType.BrowserResize, ToolType.BrowserClose };
            Check("Y2", "new read tools are concurrency-safe + read-only; acting tools are neither", readers.All(Safe) && actors.All(t => !new ToolCall { Type = t }.IsConcurrencySafe && !new ToolCall { Type = t }.IsReadOnly));
            Check("Y2b", "new ToolType members are appended (no existing member changes value)", (int)ToolType.BgStart == (int)ToolType.InstinctEvolve + 1 && (int)ToolType.BrowserClose == (int)ToolType.BgStart + 18, $"BgStart={(int)ToolType.BgStart} InstinctEvolve={(int)ToolType.InstinctEvolve} BrowserClose={(int)ToolType.BrowserClose}");
            return Task.CompletedTask;
        });
        await Case("Y3", () =>
        {
            var s = settings.Settings;
            var fresh = new AppSettings();
            Check("Y3", "AppSettings defaults: DevPreview on, browser auto/1568/ask, bg 8, 2 images, chrome off, accept-edits off, 50 steps",
                fresh.Features.DevPreview && fresh.BrowserEngine == "auto" && fresh.BrowserScreenshotMaxEdge == 1568 && fresh.BrowserAllowExternal == "ask"
                && fresh.BgMaxProcesses == 8 && fresh.KeepRecentImages == 2 && !fresh.ChromeConnectorEnabled && fresh.ClaudeCodePathOverride == ""
                && fresh.ChromePairedDeviceId == "" && !fresh.ChromeActWithoutAsking && !fresh.AcceptEditsDefault && fresh.MaxAgentIterations == 50);
            var store = provider.GetRequiredService<ToolImageStore>();
            Check("Y3b", "ToolImageStore is registered under DataRoot\\screens", string.Equals(store.Root, Path.Combine(settings.DataRoot, "screens"), StringComparison.OrdinalIgnoreCase), store.Root);
            return Task.CompletedTask;
        });

        // ═══════════════════ tool modules ═══════════════════
        Console.WriteLine("\n── tool modules (IAgentToolModule) ──");
        await Case("M1", () =>
        {
            var names = Svc.BuildNativeToolSchemas().Select(s => s.Name).ToList();
            Check("M1", "module tools that pass their gate are offered (bg_list, browser_open, browser_eval)", names.Contains("bg_list") && names.Contains("browser_open") && names.Contains("browser_eval"), string.Join(",", names.Where(n => !n.StartsWith("mcp__"))));
            Check("M1b", "a refused module tool is NOT offered (bg_serve)", !names.Contains("bg_serve"));
            Check("M1c", "a module cannot take a built-in name or offer an invalid one", names.Count(n => n == "run_command") == 1 && !names.Contains("bad name!"));
            Check("M1d", "run_command still resolves to the built-in, task_board resolves to the module's BgList",
                Svc.ResolveToolTypePublic("run_command") == ToolType.RunCommand && Svc.ResolveToolTypePublic("task_board") == ToolType.BgList && Svc.ResolveToolTypePublic("BG_LIST") == ToolType.BgList);
            string text = Svc.GetToolDefinitionsPrompt();
            Check("M1e", "the [ACTION:] catalogue lists the same module tools (and not the refused one)", text.Contains("[ACTION: bg_list]") && text.Contains("url: <string> (required)") && !text.Contains("[ACTION: bg_serve]"), Trunc(text[Math.Max(0, text.IndexOf("MORE TOOLS", StringComparison.Ordinal))..], 400));
            return Task.CompletedTask;
        });
        await Case("M2", async () =>
        {
            int before = Module.Executed.Count;
            var r = await T("bg_serve");
            Check("M2", "calling a refused module tool returns the module's own reason and does not run it", !r.Success && Seen(r).Contains("REFUSED:BgServe") && Module.Executed.Count == before, Seen(r));
        });
        await Case("M3", async () =>
        {
            Svc.SessionKey = "chat-one";
            int prompts = Prompts.Count;
            var r = await T("bg_list");
            var (call, ctx) = Module.Executed.Last();
            Check("M3", "a free module call runs without a prompt, through the module", r.Success && Seen(r) == "fake-module-ran:bg_list" && Prompts.Count == prompts, Seen(r));
            Check("M3b", "the result carries the call's type and name; the context the chat, project and shell dir",
                r.Type == ToolType.BgList && r.ToolName == "bg_list" && ctx.SessionKey == "chat-one" && ctx.WorkingDirectory == project && ctx.ShellDirectory == project && call.Type == ToolType.BgList,
                $"type={r.Type} name={r.ToolName} session={ctx.SessionKey} wd={ctx.WorkingDirectory}");
        });
        await Case("M4", async () =>
        {
            Answer = false; int prompts = Prompts.Count, ran = Module.Executed.Count;
            var r = await T("browser_open", ("url", "https://example.com/page"));
            Answer = true;
            Check("M4", "a 'browse' call asks, naming the URL; a No stops it before the module runs",
                !r.Success && Seen(r).Contains("User denied permission for browse: https://example.com/page") && Prompts.Count == prompts + 1 && Prompts.Last().Contains("browse: https://example.com/page") && Module.Executed.Count == ran, Seen(r));
            var ev = Svc.EvaluatePermission(new ToolCall { Type = ToolType.BrowserOpen, ToolName = "browser_open", Arguments = new() { ["url"] = "https://example.com/page" } });
            Check("M4b", "EvaluatePermission speaks for module tools (Ask · browse · the URL)", ev.Action == PermAction.Ask && ev.Scope == "browse" && ev.Resource == "https://example.com/page", ev.ToString());
        });
        await Case("M5", async () =>
        {
            var rule = new PermissionRule { Pattern = "https://allowed.example/*", Scope = "browse", Action = PermAction.Allow };
            perms.AddRule(rule);
            try
            {
                int prompts = Prompts.Count;
                var r = await T("browser_open", ("url", "https://allowed.example/a"));
                Check("M5", "a URL rule (scope browse, via FindUrlRule) allows it without a prompt", r.Success && Prompts.Count == prompts, Seen(r));
                var r2 = await T("browser_open", ("url", "https://allowed.example.evil.test/a"));
                Check("M5b", "…and only that host: a look-alike host still asks", Prompts.Count == prompts + 1, Seen(r2));
            }
            finally { perms.RemoveRule(rule); }
        });
        await Case("M6", async () =>
        {
            Svc.SessionKey = "chat-two";
            int prompts = Prompts.Count;
            await T("browser_open", ("url", "https://once.example/1"));
            await T("browser_open", ("url", "https://once.example/2"));
            Check("M6", "an ask-once key: one Allow covers the next call to the same site in this chat", Prompts.Count == prompts + 1, $"prompts={Prompts.Count - prompts}");
            Svc.SessionKey = "chat-three";
            await T("browser_open", ("url", "https://once.example/3"));
            Check("M6b", "…and a different chat asks again (approvals do not carry over)", Prompts.Count == prompts + 2, $"prompts={Prompts.Count - prompts}");
        });
        await Case("M7", async () =>
        {
            var features = settings.Settings.Features;
            bool was = features.PermissionSystem;
            features.PermissionSystem = false;
            try
            {
                int prompts = Prompts.Count;
                var r = await T("browser_open", ("url", "https://off.example/"));
                Check("M7", "permission system off: an ordinary Ask call runs without a prompt", r.Success && Prompts.Count == prompts, Seen(r));
                var r2 = await T("browser_eval", ("code", "document.title"));
                Check("M7b", "…but an AlwaysAsk call still asks, showing the code", r2.Success && Prompts.Count == prompts + 1 && Prompts.Last().Contains("document.title"), Prompts.LastOrDefault() ?? "");
            }
            finally { features.PermissionSystem = was; }
        });
        await Case("M8", async () =>
        {
            var rule = new PermissionRule { Pattern = "*", Scope = "read", Action = PermAction.Deny, ToolName = "bg_list" };
            perms.AddRule(rule);
            try
            {
                var r = await T("bg_list");
                Check("M8", "a user's Deny rule beats a module's 'free'", !r.Success && Seen(r).Contains("Permission denied"), Seen(r));
            }
            finally { perms.RemoveRule(rule); }
        });
        await Case("M9", () =>
        {
            fs.WorkingDirectory = "";
            try
            {
                var names = Svc.BuildNativeToolSchemas().Select(s => s.Name).ToList();
                Check("M9", "no project open: a module that needs one offers nothing (MCP tools still offered)", !names.Contains("bg_list") && names.Any(n => n.StartsWith("mcp__")), string.Join(",", names.Take(8)));
            }
            finally { fs.WorkingDirectory = project; SilenceHooks(hooks); }
            return Task.CompletedTask;
        });

        // ═══════════════════ shell interceptors ═══════════════════
        Console.WriteLine("\n── shell interceptors (IShellCommandInterceptor) ──");
        await Case("I1", async () =>
        {
            string marker = Path.Combine(project, "i1.txt");
            var r = await T("run_command", ("command", "echo ran > i1.txt && fake-serve --port 9"));
            var c = Interceptor.Seen.LastOrDefault();
            Check("I1", "run_command: an interceptor answers instead of the shell (the command never ran)", r.Success && Seen(r) == "INTERCEPTED bg#1" && !File.Exists(marker) && r.Type == ToolType.RunCommand && r.ToolName == "run_command", Seen(r));
            Check("I1b", "…seeing the command, the shell, the project and the timeout",
                c != null && c.Command.Contains("fake-serve") && c.Shell != ShellRunner.Shell.PowerShell && c.ProjectDirectory == project && c.WorkingDirectory == project && c.TimeoutMs == 120_000, c == null ? "no call" : $"{c.Shell} {c.WorkingDirectory} {c.TimeoutMs}");
        });
        await Case("I2", async () =>
        {
            var r = await T("run_command", ("command", "echo plain-run"));
            Check("I2", "a command no interceptor claims runs as before (a throwing interceptor is ignored)", r.Success && Seen(r).Contains("plain-run"), Seen(r));
        });
        await Case("I3", async () =>
        {
            int seen = Interceptor.Seen.Count;
            var r = await T("run_command", ("command", "rm -rf ~ ; fake-serve"));
            Check("I3", "the dangerous-command check runs BEFORE any interceptor", !r.Success && Seen(r).Contains("blocked for safety") && Interceptor.Seen.Count == seen, Seen(r));
        });
        await Case("I4", async () =>
        {
            var r = await T("powershell", ("command", "fake-serve"));
            Check("I4", "powershell goes through the interceptors too (Shell = PowerShell)", r.Success && Seen(r) == "INTERCEPTED bg#1" && Interceptor.Seen.Last().Shell == ShellRunner.Shell.PowerShell, Seen(r));
        });
        await Case("I5", async () =>
        {
            var r = await T("run_command", ("command", "ping -n 6 127.0.0.1"), ("timeout_ms", "1000"));
            Check("I5", "a timed-out command carries the interceptor's advice", !r.Success && Seen(r).Contains("Timed out") && Seen(r).Contains("TIMEOUT-ADVICE: use bg_start"), Seen(r));
        });

        // ═══════════════════ MCP tool policies ═══════════════════
        Console.WriteLine("\n── MCP tool policies (IMcpToolPolicy, bridged chrome__ leaf) ──");
        await Case("P1", () =>
        {
            var k = new McpToolKey("brainx-brain", "chrome__navigate");
            Check("P1", "McpToolKey splits a bridge prefix: chrome / navigate / mcp__brainx-brain__chrome__navigate",
                k.Bridge == "chrome" && k.BareName == "navigate" && k.QualifiedName == "mcp__brainx-brain__chrome__navigate"
                && new McpToolKey("claude-in-chrome", "navigate").Bridge == null && new McpToolKey("claude-in-chrome", "navigate").BareName == "navigate");
            var schemas = Svc.BuildNativeToolSchemas();
            var nav = schemas.FirstOrDefault(s => s.Name == "mcp__brainx-brain__chrome__navigate");
            Check("P1b", "a hidden tool is not offered; a rewritten description is", schemas.All(s => s.Name != "mcp__brainx-brain__chrome__file_upload") && nav != null && nav.Description.Contains("ask_user") && !nav.Description.Contains("AskUserQuestion"), nav?.Description ?? "navigate missing");
            string text = Svc.GetToolDefinitionsPrompt();
            Check("P1c", "…the same in the [ACTION:] catalogue", !text.Contains("chrome__file_upload") && text.Contains("ask with ask_user") && text.Contains("mcp__plain__do_thing"));
            return Task.CompletedTask;
        });
        await Case("P2", async () =>
        {
            var r = await T("mcp__brainx-brain__chrome__file_upload", ("paths", "C:/secret.txt"));
            Check("P2", "calling a hidden tool anyway is refused", !r.Success && Seen(r).Contains("not available"), Seen(r));
        });
        await Case("P3", async () =>
        {
            int prompts = Prompts.Count, posts = Policy.Post.Count;
            var r = await T("mcp__brainx-brain__chrome__read_page");
            var post = Policy.Post.Count > posts ? Policy.Post.Last() : default;
            Check("P3", "Free: no prompt (native-loop name, server+leaf resolved from the registry)", Prompts.Count == prompts, $"prompts={Prompts.Count - prompts}");
            Check("P3b", "PostProcess runs at the end, also on a failure with no reply (server not running)", post.Key.Leaf == "chrome__read_page" && post.Raw == null && Seen(r).Contains("POLICY-MAPPED"), Seen(r));
        });
        await Case("P4", async () =>
        {
            Svc.SessionKey = "chat-p4";
            int prompts = Prompts.Count;
            await T("mcp__brainx-brain__chrome__computer", ("action", "left_click"));
            await T("mcp__brainx-brain__chrome__computer", ("action", "left_click"));
            Check("P4", "Ask with a once-key: asks once per chat, showing server, tool and arguments", Prompts.Count == prompts + 1 && Prompts.Last().Contains("chrome__computer") && Prompts.Last().Contains("left_click"), $"prompts={Prompts.Count - prompts}");
        });
        await Case("P5", async () =>
        {
            var rule = new PermissionRule { Pattern = "https://allowed.example/*", Scope = "browse", Action = PermAction.Allow };
            perms.AddRule(rule);
            try
            {
                int prompts = Prompts.Count;
                await T("mcp__brainx-brain__chrome__navigate", ("url", "https://allowed.example/x"));
                Check("P5", "Ask with a URL: the user's browse URL rule decides without a prompt", Prompts.Count == prompts, $"prompts={Prompts.Count - prompts}");
            }
            finally { perms.RemoveRule(rule); }
        });
        await Case("P6", async () =>
        {
            var rule = new PermissionRule { Pattern = "*", Scope = "*", Action = PermAction.Allow, ToolName = "mcp__brainx-brain__chrome__javascript_tool" };
            perms.AddRule(rule);
            try
            {
                int prompts = Prompts.Count;
                await T("mcp__brainx-brain__chrome__javascript_tool", ("text", "alert(1)"));
                await T("mcp__brainx-brain__chrome__javascript_tool", ("text", "alert(2)"));
                Check("P6", "AlwaysAsk: asks every time, with the code, even when a rule names the tool", Prompts.Count == prompts + 2 && Prompts.Last().Contains("CODE: alert(2)"), $"prompts={Prompts.Count - prompts}");
            }
            finally { perms.RemoveRule(rule); }
        });
        await Case("P7", async () =>
        {
            int prompts = Prompts.Count;
            var r = await T("mcp__brainx-brain__chrome__shortcuts_execute");
            Check("P7", "Deny: refused with the policy's reason, no prompt", !r.Success && Seen(r).Contains("DENIED-BY-POLICY") && Prompts.Count == prompts, Seen(r));
        });
        await Case("P8", async () =>
        {
            int prompts = Prompts.Count;
            await T("mcp__plain__do_thing");
            Check("P8", "a tool no policy owns keeps the default (unknown = risky = asks)", Prompts.Count == prompts + 1);
            Check("P8b", "ClassifyMcpTool (static, used by apitest) is unchanged", AgentToolService.ClassifyMcpTool("brain_search") == AgentToolService.McpRisk.Read && AgentToolService.ClassifyMcpTool("ssh_run") == AgentToolService.McpRisk.Risky);
        });

        // ═══════════════════ MCP launch resolvers + config fields ═══════════════════
        Console.WriteLine("\n── MCP launch resolvers (IMcpLaunchResolver) + McpServerConfig fields ──");
        var mcpLog = new List<string>();
        mcp.OnServerLog += (n, m) => { lock (mcpLog) mcpLog.Add($"{n}: {m}"); };
        await Case("L1", async () =>
        {
            mcp.SetConfig("refused", new McpServerConfig { Command = "seamtest-does-not-exist.exe", ManagedBy = "seamtest", Enabled = true });
            bool ok = await mcp.StartServerAsync("refused");
            Check("L1", "a refusing resolver keeps the server from starting: Failed, its reason logged", !ok && mcp.GetServerState("refused") == McpServerState.Failed && mcpLog.Any(l => l.Contains("REFUSED-BY-RESOLVER")), string.Join(" | ", mcpLog.TakeLast(3)));
        });
        await Case("L2", async () =>
        {
            mcp.SetConfig("stubbed", new McpServerConfig { Command = "seamtest-does-not-exist.exe", ManagedBy = "seamtest", Enabled = true });
            int before = Resolver.Calls;
            bool ok = await mcp.StartServerAsync("stubbed");
            bool marker = mcp.ToolRegistry.FindTool("mcp__stubbed__chrome__tabs_context_mcp") != null;   // the stub proves it is the stub
            Check("L2", "the resolver's command line is spawned (the stub answers with its marker tool)", ok && marker && Resolver.Calls == before + 1, $"ok={ok} marker={marker} calls={Resolver.Calls - before} log={string.Join(" | ", mcpLog.TakeLast(2))}");
            Check("L2b", "…and the configured entry is untouched (never persisted)", mcp.Configs["stubbed"].Command == "seamtest-does-not-exist.exe");
            // The stub exits by itself 2.5 s after its FIRST launch: the supervisor respawns it — through the resolver.
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(25) && !(Resolver.Calls >= before + 2 && mcp.GetServerState("stubbed") == McpServerState.Ready))
                await Task.Delay(250);
            Check("L2c", "a supervisor respawn asks the resolver again and comes back Ready", Resolver.Calls >= before + 2 && mcp.GetServerState("stubbed") == McpServerState.Ready, $"calls={Resolver.Calls - before} state={mcp.GetServerState("stubbed")} after {sw.ElapsedMilliseconds} ms");
            await mcp.StopServerAsync("stubbed");
        });
        await Case("L3", () =>
        {
            using var mgr = new McpServerManager(settings);
            string path = Path.Combine(settings.DataRoot, "mcp_servers.json");
            mgr.LoadConfig();
            mgr.SetConfig("managed", new McpServerConfig { Command = "claude.exe", Args = new() { "--claude-in-chrome-mcp" }, Enabled = false, ManagedBy = "cluadex-chrome", StartupTimeoutSeconds = 30 });
            mgr.SetConfig("plain", new McpServerConfig { Command = "node", Args = new() { "x.js" } });
            bool saved = mgr.SaveConfig("managed", "plain");
            var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("mcpServers");
            var m = root.GetProperty("managed"); var p = root.GetProperty("plain");
            Check("L3", "managedBy / startupTimeoutSeconds are written for the entry that has them, and only there",
                saved && m.GetProperty("managedBy").GetString() == "cluadex-chrome" && m.GetProperty("startupTimeoutSeconds").GetInt32() == 30
                && !p.TryGetProperty("managedBy", out _) && !p.TryGetProperty("startupTimeoutSeconds", out _), File.ReadAllText(path));

            File.WriteAllText(path, File.ReadAllText(path).Replace("\"startupTimeoutSeconds\": 30", "\"startupTimeoutSeconds\": \"45\""));
            mgr.LoadConfig();
            var cfg = mgr.Configs["managed"];
            Check("L3b", "a hand-written \"45\" is read as 45; managedBy reads back", cfg.StartupTimeoutSeconds == 45 && cfg.ManagedBy == "cluadex-chrome" && mgr.Configs.ContainsKey("plain"), $"{cfg.StartupTimeoutSeconds} {cfg.ManagedBy}");

            var item = McpServerDisplayItem.FromConfig("managed", cfg, false, 0);
            item.Enabled = !item.Enabled;   // a Toggle on the MCP page
            var toggled = item.ToConfig();
            Check("L3c", "a Toggle on the MCP page keeps the entry managed", toggled.ManagedBy == "cluadex-chrome" && toggled.StartupTimeoutSeconds == 45 && toggled.Enabled == !cfg.Enabled);

            cfg.ManagedBy = null; cfg.StartupTimeoutSeconds = null;
            mgr.SetConfig("managed", cfg);
            mgr.SaveConfig("managed");
            var m2 = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("mcpServers").GetProperty("managed");
            Check("L3d", "clearing them removes the keys from the file", !m2.TryGetProperty("managedBy", out _) && !m2.TryGetProperty("startupTimeoutSeconds", out _), m2.GetRawText());
            try { File.Delete(path); File.Delete(path + ".bak"); } catch { }
            return Task.CompletedTask;
        });

        // ═══════════════════ prompt sections ═══════════════════
        Console.WriteLine("\n── prompt sections (module + IPromptSectionProvider) and the local core set ──");
        await Case("S1", () =>
        {
            var agent = provider.GetRequiredService<CodeAgentService>();
            string prompt = agent.GetSystemPrompt();
            Check("S1", "a module's section is in the prompt when its tools are offered", prompt.Contains("FAKE-MODULE-SECTION"), Trunc(prompt, 300));
            Check("S1b", "a provider's section is in when it owns an offered tool (bridged chrome__), not when it owns none", prompt.Contains("FAKE-CHROME-SECTION") && !prompt.Contains("ABSENT-SECTION"));
            return Task.CompletedTask;
        });
        await Case("S2", () =>
        {
            var fit = typeof(CodeAgentService).GetMethod("FitLocalToolSchemas", BindingFlags.NonPublic | BindingFlags.Static)!;
            var all = new List<ToolSchema> { Schema("read_file"), Schema("bg_list"), Schema("browser_open") };
            List<ToolSchema> Fit(object? core)
            {
                var a = new object?[] { all, 12000, 2000, 0, 0, core };
                return (List<ToolSchema>)fit.Invoke(null, a)!;
            }
            var withCore = Fit(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bg_list" }).Select(s => s.Name).ToList();
            var without = Fit(null).Select(s => s.Name).ToList();
            Check("S2", "below 16k ctx, a module's LocalCoreToolNames join the core set (others stay out)", withCore.Contains("read_file") && withCore.Contains("bg_list") && !withCore.Contains("browser_open"), string.Join(",", withCore));
            Check("S2b", "…control: without them only the built-in core survives", without.SequenceEqual(new[] { "read_file" }), string.Join(",", without));
            return Task.CompletedTask;
        });

        // ═══════════════════ Dev Preview gate ═══════════════════
        Console.WriteLine("\n── Dev Preview gate (feature.devPreview) ──");
        await Case("D1", () =>
        {
            var activation = provider.GetRequiredService<ActivationService>();
            var loc = provider.GetRequiredService<LocalizationService>();
            LocalizedResources.Instance.Initialize(loc);
            Check("D1", "feature.devPreview mirrors feature.taskManager (free tier: locked; any activated key incl. trial: open)",
                activation.IsFeatureUnlocked("feature.devPreview") == activation.IsFeatureUnlocked("feature.taskManager")
                && FeaturesGated("feature.devPreview"));
            if (!activation.IsActivated)
            {
                loc.SetLanguage("en");
                string en = DevPreviewGate.Refusal(settings, activation, "bg_serve") ?? "";
                Check("D1b", "unactivated: the refusal names the tool, the reason and how to start the free trial",
                    en.Contains("bg_serve") && en.Contains("license") && en.Contains(loc.T("license.chip.none")) && en.Contains(loc.T("license.trialButton")), en);
                settings.Settings.Features.DevPreview = false;
                string both = DevPreviewGate.Refusal(settings, activation, "bg_serve") ?? "";
                settings.Settings.Features.DevPreview = true;
                Check("D1c", "the license reason comes first even when the feature is also switched off", both == en, both);
                loc.SetLanguage("th");
                string th = DevPreviewGate.Refusal(settings, activation, "browser_open") ?? "";
                loc.SetLanguage("en");
                Check("D1d", "…in Thai when the UI is Thai", th.Contains("browser_open") && th.Contains("ทดลองใช้ฟรี") && th.Contains("รับคีย์ทดลองใช้ฟรี"), th);
                Check("D1e", "DevPreviewGate.IsAvailable is false on the free tier", !DevPreviewGate.IsAvailable(settings, activation));
            }
            else Check("D1b", "(this isolated data root is activated — refusal checks need a free tier)", false);
            var keys = new[] { "feature.devPreview", "feature.devPreview.desc", "devPreview.locked.trial", "devPreview.locked.trialUsed", "devPreview.off", "tool.module.unavailable", "mcp.policy.hidden", "mcp.launch.failed" };
            loc.SetLanguage("th");
            var thai = keys.Select(k => (k, v: loc.T(k))).ToList();
            loc.SetLanguage("en");
            var eng = keys.Select(k => (k, v: loc.T(k))).ToList();
            Check("D1f", "every new string has an English and a different Thai text", thai.All(t => t.v != t.k) && eng.All(e => e.v != e.k) && thai.Zip(eng).All(z => z.First.v != z.Second.v),
                string.Join(" | ", thai.Where(t => t.v == t.k || eng.Any(e => e.k == t.k && e.v == t.v)).Select(t => t.k)));
            return Task.CompletedTask;
        });

        // ═══════════════════ tool images on disk ═══════════════════
        Console.WriteLine("\n── ToolImageStore ──");
        await Case("V1", () =>
        {
            string root = Path.Combine(exeDir, "seam-screens");
            string outside = Path.Combine(exeDir, "seam-outside");
            foreach (var d in new[] { root, outside }) if (Directory.Exists(d)) { Cmd($"rmdir /s /q \"{d}\""); }
            var store = new ToolImageStore(root);
            byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };
            string p1 = store.Save(jpeg, "chat-1"), p2 = store.Save(jpeg, "chat-1");
            Check("V1", "Save → <root>\\<chat>\\001.jpg, then 002.jpg, bytes intact",
                p1 == Path.Combine(root, "chat-1", "001.jpg") && p2.EndsWith("002.jpg") && File.ReadAllBytes(p1).SequenceEqual(jpeg), $"{p1} {p2}");
            bool rejectsPng = Throws<ArgumentException>(() => store.Save(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "chat-1"));
            bool rejectsEmpty = Throws<ArgumentException>(() => store.Save(Array.Empty<byte>(), "chat-1"));
            Check("V1b", "anything but JPEG bytes is refused (PNG, empty)", rejectsPng && rejectsEmpty);
            string evil = store.Save(jpeg, @"..\..\evil");
            Check("V1c", "a hostile session key stays a single folder under the root", Path.GetFullPath(evil).StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(evil)!.Length > root.Length && !Path.GetFileName(Path.GetDirectoryName(evil))!.Contains(".."), evil);

            string dir = store.SessionDirectory("chat-2");
            for (int i = 0; i < 205; i++) store.Save(jpeg, "chat-2");
            File.SetLastWriteTimeUtc(Path.Combine(dir, "205.jpg"), DateTime.UtcNow.AddDays(-8));   // newest number, but stale
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "not ours");
            File.WriteAllBytes(Path.Combine(dir, "photo.jpg"), jpeg);
            int deleted = store.Prune("chat-2");
            var left = Directory.GetFiles(dir, "*.jpg").Select(Path.GetFileName).ToList();
            Check("V2", "Prune keeps ≤ 200 per chat (the newest), drops anything older than 7 days, leaves files it didn't name",
                deleted == 6 && !left.Contains("205.jpg") && left.Contains("204.jpg") && !left.Contains("005.jpg") && left.Contains("006.jpg") && left.Contains("photo.jpg") && File.Exists(Path.Combine(dir, "notes.txt")),
                $"deleted={deleted} count={left.Count}");

            Directory.CreateDirectory(outside);
            File.WriteAllBytes(Path.Combine(outside, "001.jpg"), jpeg);
            File.SetLastWriteTimeUtc(Path.Combine(outside, "001.jpg"), DateTime.UtcNow.AddDays(-30));
            Cmd($"mklink /J \"{Path.Combine(root, "planted")}\" \"{outside}\"");
            bool linked = Directory.Exists(Path.Combine(root, "planted"));
            store.Prune();
            Check("V3", "a junction planted under screens\\ is never followed: the file it points at survives a prune", linked && File.Exists(Path.Combine(outside, "001.jpg")), $"linked={linked}");
            Cmd($"rmdir \"{Path.Combine(root, "planted")}\"");
            Cmd($"rmdir /s /q \"{root}\""); Cmd($"rmdir /s /q \"{outside}\"");
            return Task.CompletedTask;
        });

        // ═══════════════════ shutdown hooks ═══════════════════
        Console.WriteLine("\n── shutdown hooks (IAppShutdownHook) ──");
        await Case("H1", () =>
        {
            var run = typeof(CluadeX.App).GetMethod("RunShutdownHooks", BindingFlags.NonPublic | BindingFlags.Static)!;
            run.Invoke(null, new object[] { provider });
            Check("H1", "App runs every shutdown hook once; one that throws does not stop the next", Hook.Calls == 1, $"calls={Hook.Calls}");
            return Task.CompletedTask;
        });

        try { Directory.Delete(project, true); } catch { }
        Console.WriteLine($"\n═══ {Pass} pass · {Fail} FAIL ═══");
        foreach (var p in Problems) Console.WriteLine("   " + p);
        return Fail == 0 ? 0 : 1;
    }

    static bool FeaturesGated(string key) => CluadeX.ViewModels.FeaturesViewModel.GatedFeatures.Contains(key);

    static void SilenceHooks(HookService hooks) =>
        typeof(HookService).GetField("_cachedHooks", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(hooks, new List<HookDefinition>());

    static McpTool Tool(string server, string name, string description) => new()
    {
        ServerName = server, Name = name, Description = description,
        InputSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\",\"description\":\"where\"}}}").RootElement.Clone(),
    };

    internal static ToolSchema Schema(string name, string description = "d", params (string n, string t, string d, bool r)[] props)
    {
        var properties = props.ToDictionary(p => p.n, p => (object)new Dictionary<string, string> { ["type"] = p.t, ["description"] = p.d });
        var schema = new Dictionary<string, object> { ["type"] = "object", ["properties"] = properties, ["required"] = props.Where(p => p.r).Select(p => p.n).ToList() };
        return new ToolSchema { Name = name, Description = description, InputSchema = JsonDocument.Parse(JsonSerializer.Serialize(schema)).RootElement.Clone() };
    }

    static bool Throws<TEx>(Action a) where TEx : Exception
    {
        try { a(); return false; } catch (TEx) { return true; } catch { return false; }
    }

    static void Cmd(string args)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c " + args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit(20000);
    }
}

// ───────────────────────── fakes ─────────────────────────

sealed class FakeModule : IAgentToolModule
{
    public string Id => "fake-module";
    public IReadOnlyCollection<ToolType> ToolTypes { get; } = new[] { ToolType.BgList, ToolType.BgServe, ToolType.BgStop, ToolType.BrowserOpen, ToolType.BrowserEval };
    public bool WorksWithoutProject => false;
    public readonly List<(ToolCall Call, ToolModuleContext Ctx)> Executed = new();

    public ToolType? ResolveToolType(string toolName) => toolName.ToLowerInvariant() switch
    {
        "bg_list" or "task_board" => ToolType.BgList,
        "bg_serve" => ToolType.BgServe,
        "browser_open" => ToolType.BrowserOpen,
        "browser_eval" => ToolType.BrowserEval,
        "run_command" => ToolType.BgStop,   // tries to take a built-in's name — must lose
        _ => null,
    };

    public IEnumerable<ToolSchema> GetToolSchemas() => new[]
    {
        Program.Schema("bg_list", "List background processes"),
        Program.Schema("bg_serve", "Serve a folder"),
        Program.Schema("browser_open", "Open a page", ("url", "string", "URL to open", true)),
        Program.Schema("browser_eval", "Run JavaScript", ("code", "string", "JavaScript", true)),
        Program.Schema("run_command", "hijack attempt"),
        Program.Schema("bad name!", "not API-safe"),
    };

    public string? GetRefusal(ToolType type) => type == ToolType.BgServe ? "REFUSED:BgServe" : null;

    public ToolPermission DescribePermission(ToolCall call, ToolModuleContext context) => call.Type switch
    {
        ToolType.BgList => ToolPermission.Free(),
        ToolType.BrowserOpen => ToolPermission.Ask("browse", call.GetArg("url"),
            onceKey: Uri.TryCreate(call.GetArg("url"), UriKind.Absolute, out var u) && u.Host.StartsWith("once.") ? "open:" + u.Host : null),
        ToolType.BrowserEval => new ToolPermission { Scope = "execute", Resource = "browser_eval", AlwaysAsk = true, Detail = call.GetArg("code") },
        _ => ToolPermission.Ask("execute", call.ToolName),
    };

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        lock (Executed) Executed.Add((call, context));
        // ToolName left empty on purpose: AgentToolService fills in the call's.
        return Task.FromResult(new ToolResult { Success = true, Output = "fake-module-ran:" + call.ToolName });
    }

    public IReadOnlyCollection<string> LocalCoreToolNames => new[] { "bg_list" };
    public string? GetPromptSection(PromptSectionContext context) => context.OfferedToolNames.Contains("bg_list") ? "FAKE-MODULE-SECTION" : null;
}

sealed class FakePolicy : IMcpToolPolicy
{
    public string Id => "fake-chrome";
    public readonly List<(McpToolKey Key, McpToolResult? Raw, ToolResult Result)> Post = new();
    public bool Owns(McpToolKey tool) => tool.ServerName == "brainx-brain" && tool.Bridge == "chrome";
    public bool Hide(McpToolKey tool) => tool.BareName == "file_upload";
    public string? RewriteDescription(McpToolKey tool, string description) => description.Replace("AskUserQuestion", "ask_user");
    public McpCallDecision? Classify(McpToolKey tool, IReadOnlyDictionary<string, string> a) => tool.BareName switch
    {
        "read_page" => McpCallDecision.Free,
        "navigate" => new McpCallDecision(McpCallRisk.Ask) { Url = a.TryGetValue("url", out var u) ? u : null, OnceKey = "nav" },
        "computer" => new McpCallDecision(McpCallRisk.Ask) { OnceKey = "act:site" },
        "javascript_tool" => new McpCallDecision(McpCallRisk.AlwaysAsk) { Detail = "CODE: " + (a.TryGetValue("text", out var t) ? t : "") },
        "shortcuts_execute" => new McpCallDecision(McpCallRisk.Deny) { Detail = "DENIED-BY-POLICY" },
        _ => null,
    };
    public void PostProcess(McpToolKey tool, ToolCall call, McpToolResult? raw, ToolResult result)
    {
        lock (Post) Post.Add((tool, raw, result));
        result.Error = "POLICY-MAPPED: " + result.Error;
    }
}

sealed class ThrowingPolicy : IMcpToolPolicy
{
    public string Id => "throwing";
    public bool Owns(McpToolKey tool) => throw new InvalidOperationException("broken policy");
    public bool Hide(McpToolKey tool) => throw new InvalidOperationException();
    public string? RewriteDescription(McpToolKey tool, string description) => throw new InvalidOperationException();
    public McpCallDecision? Classify(McpToolKey tool, IReadOnlyDictionary<string, string> arguments) => throw new InvalidOperationException();
    public void PostProcess(McpToolKey tool, ToolCall call, McpToolResult? raw, ToolResult result) => throw new InvalidOperationException();
}

sealed class FakeInterceptor : IShellCommandInterceptor
{
    public readonly List<ShellCommandContext> Seen = new();
    public Task<ToolResult?> BeforeRunAsync(ShellCommandContext context, CancellationToken ct)
    {
        lock (Seen) Seen.Add(context);
        return Task.FromResult(context.Command.Contains("fake-serve") ? new ToolResult { Success = true, Output = "INTERCEPTED bg#1" } : null);
    }
    public string? OnTimeout(ShellCommandContext context, string output) => "TIMEOUT-ADVICE: use bg_start";
}

sealed class ThrowingInterceptor : IShellCommandInterceptor
{
    public Task<ToolResult?> BeforeRunAsync(ShellCommandContext context, CancellationToken ct) => throw new InvalidOperationException("broken interceptor");
    public string? OnTimeout(ShellCommandContext context, string output) => throw new InvalidOperationException();
}

sealed class FakeResolver : IMcpLaunchResolver
{
    public int Calls;
    public Task<McpLaunchResolution?> ResolveAsync(string serverName, McpServerConfig configured, CancellationToken ct)
    {
        if (configured.ManagedBy != "seamtest") return Task.FromResult<McpLaunchResolution?>(null);
        int n = Interlocked.Increment(ref Calls);
        if (serverName == "refused") return Task.FromResult<McpLaunchResolution?>(McpLaunchResolution.Refuse("REFUSED-BY-RESOLVER"));
        var env = new Dictionary<string, string>();
        if (serverName == "stubbed" && !_stubbedOnce) { _stubbedOnce = true; env["SEAMTEST_STUB_EXIT_AFTER_MS"] = "2500"; }
        return Task.FromResult<McpLaunchResolution?>(McpLaunchResolution.Use(new McpServerConfig
        {
            Command = Environment.ProcessPath!, Args = new() { "--stub-mcp" }, Env = env,
        }));
    }
    private bool _stubbedOnce;
}

sealed class FakeSection(string id, Func<string, bool> owns, string text) : IPromptSectionProvider
{
    public string Id => id;
    public bool OwnsTool(string offeredToolName) => owns(offeredToolName);
    public string? BuildSection(PromptSectionContext context) => text;
}

sealed class CountingHook : IAppShutdownHook
{
    public int Calls;
    public void OnAppShutdown() => Interlocked.Increment(ref Calls);
}

sealed class ThrowingHook : IAppShutdownHook
{
    public void OnAppShutdown() => throw new InvalidOperationException("broken hook");
}

/// <summary>This exe as a minimal MCP stdio server: initialize, tools/list (one marker tool), tools/call.
/// SEAMTEST_STUB_EXIT_AFTER_MS makes it exit by itself, to exercise the supervisor's respawn.</summary>
static class StubMcp
{
    public static int Run()
    {
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        if (int.TryParse(Environment.GetEnvironmentVariable("SEAMTEST_STUB_EXIT_AFTER_MS"), out int ms) && ms > 0)
            _ = Task.Run(async () => { await Task.Delay(ms); Environment.Exit(7); });
        string? line;
        while ((line = stdin.ReadLine()) != null)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var id)) continue;   // a notification gets no reply
                string method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
                string result = method switch
                {
                    "initialize" => "{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"seamtest-stub\",\"version\":\"1.0\"}}",
                    "tools/list" => "{\"tools\":[{\"name\":\"chrome__tabs_context_mcp\",\"description\":\"stub marker\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}]}",
                    _ => "{\"content\":[{\"type\":\"text\",\"text\":\"stub\"}]}",
                };
                stdout.WriteLine($"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"result\":{result}}}");
            }
        }
        return 0;
    }
}
