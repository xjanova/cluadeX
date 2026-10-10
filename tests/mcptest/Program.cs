using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Mcp;
using CluadeX.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace McpTest;

/// <summary>
/// T5 "mcp-run" contract: MCP tools judged by their REAL leaf (native-loop names, bridged leaves, policies first),
/// server-wide rules, PermissionSystem=off, the schema sanitizer through the real Anthropic / OpenAI / Gemini request
/// builders, the startup timeout, the named-pipe JSON-RPC host and the install marker — all against the REAL built
/// CluadeX.dll, App.ConfigureServices' own DI graph and a real stdio MCP server (this exe started with --stub-mcp).
///
/// No model, no paid API, no network beyond localhost. portable.txt keeps every file next to this exe; the harness
/// refuses to run otherwise and proves at the end that ~/.cluadex was not written. The provider mock answers with a
/// marker that every provider call must return — a request that went anywhere else could not produce it.
///
///   powershell … locked.ps1 &lt;CluadeX dir&gt; dotnet build CluadeX.csproj -p:OutDir=&lt;bin&gt;\
///   powershell … locked.ps1 tests\mcptest dotnet run -p:CluadeXBin=&lt;bin&gt;\
/// </summary>
internal static class Program
{
    static int Pass, Fail;
    static readonly List<string> Problems = new();
    const string MockMarker = "MCPTEST-MOCK-7731";
    const string StubMarker = "STUB-OK";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--stub-mcp") return StubMcp.Run(args);
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
        if (detail.Length > 0) Console.WriteLine("            " + Trunc(detail, 700).Replace("\n", "\n            "));
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

    // ── the "user" who answers permission prompts ──
    static readonly List<string> Prompts = new();
    static bool Answer;
    static AgentToolService Svc = null!;
    static readonly TestPolicy Policy = new();

    static async Task<ToolResult> Call(string tool, params (string k, string v)[] args)
    {
        // Exactly what the native loop builds: the qualified name, the resolved type, the arguments — no
        // McpServerName / McpToolName (CodeAgentService.cs native dispatch).
        var type = Svc.ResolveToolTypePublic(tool) ?? throw new Exception("tool name did not resolve: " + tool);
        var call = new ToolCall { Type = type, ToolName = tool, Arguments = args.ToDictionary(a => a.k, a => a.v) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await Svc.ExecuteToolAsync(call, cts.Token);
    }

    static async Task<(ToolResult Result, int Prompts, string LastPrompt)> Gate(string tool, params (string k, string v)[] args)
    {
        int before;
        lock (Prompts) before = Prompts.Count;
        var r = await Call(tool, args);
        lock (Prompts) return (r, Prompts.Count - before, Prompts.Count > 0 ? Prompts[^1] : "");
    }

    static async Task<int> Run()
    {
        string exeDir = AppContext.BaseDirectory.TrimEnd('\\');
        // ~/.cluadex must not change — snapshot what a leaky harness used to write (metadata only, never content).
        string profileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex");
        var profileBefore = ProfileSnapshot(profileDir);

        var configure = typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!;
        var services = new ServiceCollection();
        configure.Invoke(null, new object[] { services });
        services.AddSingleton<IMcpToolPolicy>(Policy);
        using var provider = services.BuildServiceProvider();

        var settings = provider.GetRequiredService<SettingsService>();
        Console.WriteLine($"data root : {settings.DataRoot}");
        if (!settings.DataRoot.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("ABORT: data root is not isolated (portable.txt missing) — refusing to touch real user data.");
            return 3;
        }
        LocalizedResources.Instance.Initialize(provider.GetRequiredService<LocalizationService>());

        string project = Path.Combine(exeDir, "mcp-project");
        if (Directory.Exists(project)) DeleteTree(project);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "notes.txt"), "alpha\nbeta\n");

        var fs = provider.GetRequiredService<FileSystemService>();
        var perms = provider.GetRequiredService<PermissionService>();
        var mcp = provider.GetRequiredService<McpServerManager>();
        typeof(HookService).GetField("_cachedHooks", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(provider.GetRequiredService<HookService>(), new List<HookDefinition>());   // never the user's own hooks
        Svc = provider.GetRequiredService<AgentToolService>();
        Svc.OnPermissionRequired += info => { lock (Prompts) Prompts.Add(info.Detail); return Task.FromResult(Answer); };
        fs.WorkingDirectory = project;
        foreach (var r in perms.Rules.ToList()) perms.RemoveRule(r);   // a clean rule set for this run
        settings.Settings.Features.PermissionSystem = true;
        settings.Settings.Features.McpServers = true;

        string exe = Environment.ProcessPath!;
        Console.WriteLine($"project   : {project}\nstub      : {exe} --stub-mcp\n");

        // ═══════════════════ the stub server ═══════════════════
        Console.WriteLine("── stub MCP server (this exe, --stub-mcp) ──");
        mcp.SetConfig("stub", new McpServerConfig { Command = exe, Args = new() { "--stub-mcp" }, Enabled = true });
        bool started = await mcp.StartServerAsync("stub");
        bool marker = mcp.ToolRegistry.FindTool("mcp__stub__stub_marker") != null;
        Check("S1", "the stub starts and proves it is the stub (its marker tool is listed) before anything is called", started && marker,
            $"started={started} marker={marker} state={mcp.GetServerState("stub")}");
        if (!started || !marker) return Finish(profileDir, profileBefore);
        // The same stub under a brain-looking name: BrainX's read-only list applies on a brain server only.
        mcp.SetConfig("stub-brain", new McpServerConfig { Command = exe, Args = new() { "--stub-mcp" }, Enabled = true });
        bool brainStarted = await mcp.StartServerAsync("stub-brain") && mcp.ToolRegistry.FindTool("mcp__stub-brain__stub_marker") != null;
        Check("S2", "a second stub as 'stub-brain' (a brain-looking server name) starts and proves its marker", brainStarted, $"state={mcp.GetServerState("stub-brain")}");
        if (!brainStarted) return Finish(profileDir, profileBefore);

        // ═══════════════════ classification by the REAL leaf ═══════════════════
        Console.WriteLine("\n── MCP calls classified by server + real leaf (native-loop names) ──");
        await Case("A1", async () =>
        {
            Answer = false;
            var (r, n, _) = await Gate("mcp__stub-brain__brain_search", ("query", "lesson"));
            Check("A1", "native-loop 'mcp__stub-brain__brain_search' runs WITHOUT a prompt (it asked every time before)", n == 0 && r.Success && Seen(r).Contains(StubMarker + " brain_search"), $"prompts={n} {Seen(r)}");
            // MINOR (review): BrainX's read-only list used to apply to ANY server's leaf — a third-party server's
            // "brain_search" ran unseen, and its arguments could carry anything anywhere.
            var (r3, n3, last3) = await Gate("mcp__stub__brain_search", ("query", "lesson"));
            Check("A1c", "…the same leaf on a third-party server ('stub', not a brain) asks, arguments shown", n3 == 1 && last3.Contains("lesson") && !r3.Success, $"prompts={n3} '{last3}'");
        });
        await Case("A2", async () =>
        {
            Answer = false;
            var (r, n, last) = await Gate("mcp__stub__browser_click", ("element", "Login button"), ("ref", "e12"));
            Check("A2", "native-loop 'mcp__stub__browser_click' asks, showing server, tool and arguments", n == 1 && last.Contains("stub") && last.Contains("browser_click") && last.Contains("Login button"), $"prompts={n} '{last}'");
            Check("A2b", "…and a Deny keeps it from running", !r.Success && !Seen(r).Contains(StubMarker), Seen(r));
            // MINOR (review): arguments were cut at 600 characters — the end of a long argument never reached the prompt.
            Check("A2c", "negative control: short arguments are shown whole, with no 'not shown' note", !last.Contains("not shown"), last);
            var (_, nl, longPrompt) = await Gate("mcp__stub__browser_click", ("element", new string('a', 5000) + "DANGER-TAIL"));
            Check("A2d", "a 5,000-character argument shows its start AND its end, and says how much is not shown", nl == 1 && longPrompt.Contains("DANGER-TAIL") && longPrompt.Contains("not shown") && longPrompt.Length < 3000,
                $"len={longPrompt.Length} tail={longPrompt.Contains("DANGER-TAIL")}");
        });
        await Case("A3", async () =>
        {
            Answer = true;
            var call = new ToolCall { Type = ToolType.McpTool, ToolName = "mcp__stub__browser_click", Arguments = new() { ["element"] = "x" } };
            var r = await Svc.ExecuteToolAsync(call);
            Check("A3", "the gate writes the resolved server + leaf onto the call (stub / browser_click) and the call reaches the server",
                call.McpServerName == "stub" && call.McpToolName == "browser_click" && Seen(r).Contains(StubMarker + " browser_click"), $"{call.McpServerName}/{call.McpToolName} {Seen(r)}");
        });
        await Case("A4", async () =>
        {
            Answer = false;
            var (r1, n1, last) = await Gate("mcp__stub-brain__chrome__navigate", ("url", "https://example.com/"));
            var (r2, n2, _) = await Gate("mcp__stub-brain__chrome__read_page");
            Check("A4", "a bridged leaf on the brain: chrome__navigate asks, chrome__read_page (a bridged read) does not", n1 == 1 && last.Contains("chrome__navigate") && n2 == 0 && r2.Success, $"navigate prompts={n1} read_page prompts={n2} {Seen(r2)}");
            var (_, n4, _) = await Gate("mcp__stub__chrome__read_page");
            Check("A4b", "…the 'a bridged lookup is free' rule is BrainX's: on a third-party server the same leaf asks", n4 == 1, $"prompts={n4}");
        });
        await Case("A5", () =>
        {
            var reg = new McpToolRegistry();
            reg.UpdateServer("brainx-brain", new List<McpTool> { new() { ServerName = "brainx-brain", Name = "chrome__navigate" }, new() { ServerName = "brainx-brain", Name = "brain_search" } });
            var key = McpToolClassifier.Resolve(new ToolCall { ToolName = "mcp__brainx-brain__chrome__navigate" }, reg, out bool known);
            bool parsed = McpToolClassifier.TryParseQualified("mcp__brainx-brain__chrome__navigate", out var s, out var l);
            Check("A5", "mcp__brainx-brain__chrome__navigate → server brainx-brain, leaf chrome__navigate (registry and plain parse agree)",
                known && key is { ServerName: "brainx-brain", Leaf: "chrome__navigate" } && key.Value.Bridge == "chrome" && parsed && s == "brainx-brain" && l == "chrome__navigate",
                $"{key} known={known} parsed={s}/{l}");
            Check("A5b", "bare-leaf rules unchanged (apitest's cases) and a qualified name is reduced to its leaf",
                AgentToolService.ClassifyMcpTool("brain_search") == AgentToolService.McpRisk.Read
                && AgentToolService.ClassifyMcpTool("brain_remember") == AgentToolService.McpRisk.MemoryWrite
                && AgentToolService.ClassifyMcpTool("ssh_run") == AgentToolService.McpRisk.Risky
                && AgentToolService.ClassifyMcpTool("unity__find_gameobjects") == AgentToolService.McpRisk.Read
                && AgentToolService.ClassifyMcpTool("unity__execute_code") == AgentToolService.McpRisk.Risky
                && AgentToolService.ClassifyMcpTool("mcp__brainx-brain__brain_recall") == AgentToolService.McpRisk.Read
                && AgentToolService.ClassifyMcpTool("mcp__brainx-brain__task_queue") == AgentToolService.McpRisk.Read
                && AgentToolService.ClassifyMcpTool("mcp__brainx-brain__agent_activity") == AgentToolService.McpRisk.Read);
            Check("A5c", "a 'read'-looking bridged name that writes still asks (x__get_and_delete, x__search_replace)",
                AgentToolService.ClassifyMcpTool("x__get_and_delete") == AgentToolService.McpRisk.Risky
                && AgentToolService.ClassifyMcpTool("x__search_replace") == AgentToolService.McpRisk.Risky);
            // MINOR (review): the read verb had no word boundary and the write words were a short deny-list.
            var mustAsk = new[] { "x__finder_launch", "x__read_and_erase", "x__get_or_add", "unity__inspect_and_terminate", "x__search_and_purge",
                                  "x__getter_wipe", "x__list_and_publish", "x__read_then_share" };
            var wrongly = mustAsk.Where(t => AgentToolService.ClassifyMcpTool(t) != AgentToolService.McpRisk.Risky).ToList();
            Check("A5d", "a verb that is not a whole word, or a compound/writing name, asks (finder_launch, read_and_erase, get_or_add, inspect_and_terminate, search_and_purge …)",
                wrongly.Count == 0, "classified Read: " + string.Join(", ", wrongly));
            var mustRead = new[] { "unity__find_gameobjects", "unity__get_sha", "unity__read_console", "roblox__script_read", "roblox__search_game_tree",
                                   "unreal__list_toolsets", "chrome__get_page_text", "chrome__find", "unity__validate_script" };
            var lost = mustRead.Where(t => AgentToolService.ClassifyMcpTool(t) != AgentToolService.McpRisk.Read).ToList();
            Check("A5e", "…while the real bridged lookups stay free (find_gameobjects, get_sha, script_read, list_toolsets, chrome__find …)", lost.Count == 0, "now Risky: " + string.Join(", ", lost));
            Check("A5f", "BrainX's lists apply on a brain server only: mcp__evil-remote__brain_search / __brain_remember / __unity__find_x ask; on brainx-brain they don't",
                AgentToolService.ClassifyMcpTool("mcp__evil-remote__brain_search") == AgentToolService.McpRisk.Risky
                && AgentToolService.ClassifyMcpTool("mcp__evil-remote__brain_remember") == AgentToolService.McpRisk.Risky
                && AgentToolService.ClassifyMcpTool("mcp__evil-remote__unity__find_x") == AgentToolService.McpRisk.Risky
                && AgentToolService.ClassifyMcpTool("mcp__brainx-brain__brain_search") == AgentToolService.McpRisk.Read
                && AgentToolService.ClassifyMcpTool("mcp__brainx-brain__brain_remember") == AgentToolService.McpRisk.MemoryWrite
                && AgentToolService.ClassifyMcpTool("mcp__brainx-brain__unity__find_x") == AgentToolService.McpRisk.Read);
            return Task.CompletedTask;
        });
        await Case("A6", async () =>
        {
            // The six tools that prompted in the 2026-10-09 run, as the native loop names them, on a registry entry
            // for brainx-brain (the server is not running here: the gate runs before the call fails).
            mcp.ToolRegistry.UpdateServer("brainx-brain", new[] { "brain_recall", "task_queue", "brain_search", "brain_list", "agent_activity", "cowork_task" }
                .Select(n => new McpTool { ServerName = "brainx-brain", Name = n, Description = n }).ToList());
            Answer = false;
            int asked = 0;
            var askedNames = new List<string>();
            foreach (var n in new[] { "brain_recall", "task_queue", "brain_search", "brain_list", "agent_activity" })
            {
                var (_, k, _) = await Gate($"mcp__brainx-brain__{n}");
                asked += k;
                if (k > 0) askedNames.Add(n);
            }
            var (_, cowork, _) = await Gate("mcp__brainx-brain__cowork_task", ("task", "x"));
            Check("A6", "the 2026-10-09 run: brain_recall / task_queue / brain_search / brain_list / agent_activity no longer prompt", asked == 0, "asked: " + string.Join(",", askedNames));
            Check("A6b", "…while cowork_task (it hands work to another agent) still asks", cowork == 1, $"prompts={cowork}");
            mcp.ToolRegistry.RemoveServer("brainx-brain");
        });
        await Case("A7", async () =>
        {
            Policy.Active = true;
            try
            {
                Answer = false;
                var (r, n, last) = await Gate("mcp__stub__brain_search", ("query", "q"));
                Check("A7", "a policy that owns the tool is asked FIRST: its AlwaysAsk wins over the read-only default", n == 1 && last.Contains("POLICY-DETAIL") && Policy.Classified > 0, $"prompts={n} '{last}' classified={Policy.Classified}");
            }
            finally { Policy.Active = false; }
        });
        await Case("K1", async () =>
        {
            // LOGIC (review): a policy's "Free" (e.g. the Chrome policy's get_page_text) used to return before the user's
            // own Ask rule naming that tool was looked at — the user could never make it ask.
            Policy.Active = true;
            Policy.Risk = McpCallRisk.Free;
            var ask = new PermissionRule { Pattern = "*", Scope = "*", Action = PermAction.Ask, ToolName = "mcp__stub__brain_search" };
            try
            {
                Answer = true;
                var (r0, n0, _) = await Gate("mcp__stub__brain_search", ("query", "free"));
                Check("K1", "negative control: the policy says Free and no rule → runs without a prompt", n0 == 0 && r0.Success, $"prompts={n0} {Seen(r0)}");
                perms.AddRule(ask);
                var (r1, n1, last) = await Gate("mcp__stub__brain_search", ("query", "free"));
                var (_, n2, _) = await Gate("mcp__stub__brain_search", ("query", "again"));
                Check("K1b", "an Ask rule naming the tool makes a policy-Free call ask — every time (no once-memory)", n1 == 1 && n2 == 1 && r1.Success && last.Contains("brain_search"), $"prompts={n1},{n2} '{last}'");
                Answer = false;
                var (r3, n3, _) = await Gate("mcp__stub__brain_search", ("query", "denied"));
                Check("K1c", "…and a Deny at that prompt keeps it from running", n3 == 1 && !r3.Success && !Seen(r3).Contains(StubMarker), Seen(r3));
            }
            finally
            {
                perms.RemoveRule(ask);
                Policy.Risk = McpCallRisk.AlwaysAsk;
                Policy.Active = false;
            }
        });

        // ═══════════════════ rules ═══════════════════
        Console.WriteLine("\n── rules: server-wide 'mcp__<server>__*', exact names, a blanket '*' ──");
        await Case("B1", async () =>
        {
            var rule = new PermissionRule { Pattern = "*", Scope = "*", Action = PermAction.Allow, ToolName = "mcp__stub__*" };
            perms.AddRule(rule);
            try
            {
                Answer = false;
                var (r, n, _) = await Gate("mcp__stub__browser_click", ("element", "y"));
                Check("B1", "an Allow rule 'mcp__stub__*' removes the prompt for the stub's risky tool", n == 0 && Seen(r).Contains(StubMarker + " browser_click"), $"prompts={n} {Seen(r)}");
                var exact = new PermissionRule { Pattern = "*", Scope = "*", Action = PermAction.Deny, ToolName = "mcp__stub__browser_click" };
                perms.AddRule(exact);
                try
                {
                    var (r2, n2, _) = await Gate("mcp__stub__browser_click", ("element", "y"));
                    Check("B1b", "a Deny rule naming the tool beats the server-wide Allow (no prompt, refused)", n2 == 0 && !r2.Success && Seen(r2).Contains("denied"), $"prompts={n2} {Seen(r2)}");
                }
                finally { perms.RemoveRule(exact); }
            }
            finally { perms.RemoveRule(rule); }
        });
        await Case("B1c", async () =>
        {
            // MINOR (review): an Allow naming only the leaf ('browser_click') beat a Deny of the whole server.
            var denyServer = new PermissionRule { Pattern = "*", Scope = "*", Action = PermAction.Deny, ToolName = "mcp__stub__*" };
            var allowLeaf = new PermissionRule { Pattern = "*", Scope = "*", Action = PermAction.Allow, ToolName = "browser_click" };
            var allowFull = new PermissionRule { Pattern = "*", Scope = "*", Action = PermAction.Allow, ToolName = "mcp__stub__browser_click" };
            perms.AddRule(allowLeaf);
            try
            {
                Answer = false;
                var (r0, n0, _) = await Gate("mcp__stub__browser_click", ("element", "leaf"));
                Check("B1c", "negative control: a leaf-only Allow ('browser_click') alone lets it run", n0 == 0 && r0.Success, $"prompts={n0} {Seen(r0)}");
                perms.AddRule(denyServer);
                var (r1, n1, _) = await Gate("mcp__stub__browser_click", ("element", "leaf"));
                perms.AddRule(allowFull);
                var (r2, n2, _) = await Gate("mcp__stub__browser_click", ("element", "full"));
                Check("B1d", "a server-wide Deny wins over an Allow naming the leaf AND over one naming the full tool (Deny of any kind wins)",
                    n1 == 0 && !r1.Success && n2 == 0 && !r2.Success && !Seen(r1).Contains(StubMarker) && !Seen(r2).Contains(StubMarker), $"{Seen(r1)} / {Seen(r2)}");
                perms.RemoveRule(allowLeaf); perms.RemoveRule(denyServer); perms.RemoveRule(allowFull);
                Check("B1e", "FindMcpToolRule: full name beats server-wide, server-wide beats a bare leaf; any Deny wins; another server's rule never counts",
                    RuleFor(perms, ("mcp__x__*", PermAction.Ask), ("mcp__x__t", PermAction.Allow)) == PermAction.Allow
                    && RuleFor(perms, ("mcp__x__*", PermAction.Ask), ("t", PermAction.Allow)) == PermAction.Ask
                    && RuleFor(perms, ("mcp__x__*", PermAction.Allow), ("t", PermAction.Deny)) == PermAction.Deny
                    && RuleFor(perms, ("mcp__y__*", PermAction.Deny), ("t", PermAction.Allow)) == PermAction.Allow
                    && RuleFor(perms) == null);
            }
            finally { perms.RemoveRule(allowLeaf); perms.RemoveRule(denyServer); perms.RemoveRule(allowFull); }
        });
        await Case("B2", async () =>
        {
            var other = new PermissionRule { Pattern = "*", Scope = "*", Action = PermAction.Allow, ToolName = "mcp__stu__*" };
            var blanket = new PermissionRule { Pattern = "*", Scope = "*", Action = PermAction.Allow, ToolName = "*" };
            perms.AddRule(other);
            perms.AddRule(blanket);
            try
            {
                Answer = false;
                var (_, n, _) = await Gate("mcp__stub__browser_click", ("element", "z"));
                Check("B2", "neither another server's rule ('mcp__stu__*') nor a blanket '*' tool rule lets a risky MCP tool through", n == 1, $"prompts={n}");
            }
            finally { perms.RemoveRule(other); perms.RemoveRule(blanket); }
        });
        await Case("B3", async () =>
        {
            var deny = new PermissionRule { Pattern = "*", Scope = "*", Action = PermAction.Deny, ToolName = "mcp__stub-brain__*" };
            perms.AddRule(deny);
            try
            {
                var (r, n, _) = await Gate("mcp__stub-brain__brain_search", ("query", "q"));
                Check("B3", "a server-wide Deny refuses even its read-only tools, without a prompt", n == 0 && !r.Success && !Seen(r).Contains(StubMarker), $"prompts={n} {Seen(r)}");
            }
            finally { perms.RemoveRule(deny); }
        });
        await Case("B4", async () =>
        {
            var ask = new PermissionRule { Pattern = "*", Scope = "*", Action = PermAction.Ask, ToolName = "mcp__stub-brain__brain_search" };
            perms.AddRule(ask);
            try
            {
                Answer = true;
                var (r, n, _) = await Gate("mcp__stub-brain__brain_search", ("query", "q"));
                Check("B4", "an Ask rule naming a read-only tool makes it ask (any rule used to mean 'no prompt')", n == 1 && r.Success, $"prompts={n}");
            }
            finally { perms.RemoveRule(ask); }
        });

        // ═══════════════════ PermissionSystem = off ═══════════════════
        Console.WriteLine("\n── PermissionSystem = off ──");
        await Case("C1", async () =>
        {
            settings.Settings.Features.PermissionSystem = false;
            try
            {
                Answer = false;
                var (r1, n1, _) = await Gate("mcp__stub-brain__brain_search", ("query", "q"));
                var (_, n2, _) = await Gate("mcp__stub__browser_click", ("element", "q"));
                Check("C1", "off: a read-only MCP tool runs, a Risky one STILL asks (a blanket switch is not consent to reach other machines)", n1 == 0 && r1.Success && n2 == 1, $"read prompts={n1} risky prompts={n2}");
                var rule = new PermissionRule { Pattern = "*", Scope = "*", Action = PermAction.Allow, ToolName = "mcp__stub__*" };
                perms.AddRule(rule);
                try
                {
                    var (_, n3, _) = await Gate("mcp__stub__browser_click", ("element", "q"));
                    Check("C1b", "off: a rule naming the server lets it run", n3 == 0, $"prompts={n3}");
                }
                finally { perms.RemoveRule(rule); }
                var (w, n4, _) = await Gate("write_file", ("path", "off.txt"), ("content", "x"));
                Check("C1c", "off: built-in file tools run without prompts, as before", n4 == 0 && w.Success, $"prompts={n4} {Seen(w)}");
            }
            finally { settings.Settings.Features.PermissionSystem = true; }
        });

        // ═══════════════════ schema sanitizer ═══════════════════
        Console.WriteLine("\n── MCP input schemas: one sanitized form, structure kept ──");
        var schemas = Svc.BuildNativeToolSchemas();
        var complex = schemas.FirstOrDefault(s => s.Name == "mcp__stub__complex_tool");
        var nullable = schemas.FirstOrDefault(s => s.Name == "mcp__stub__nullable_tool");
        await Case("D1", () =>
        {
            Check("D1", "both stub schemas are offered", complex != null && nullable != null);
            if (complex == null || nullable == null) return Task.CompletedTask;
            var p = complex.InputSchema.GetProperty("properties");
            Check("D1a", "array items + enum kept (tags: array of red|green|blue)",
                p.GetProperty("tags").GetProperty("type").GetString() == "array"
                && p.GetProperty("tags").GetProperty("items").GetProperty("enum").GetArrayLength() == 3, Raw(p.GetProperty("tags")));
            Check("D1b", "enum + default kept on a string (mode: fast|safe, default safe)",
                p.GetProperty("mode").GetProperty("enum").GetArrayLength() == 2 && p.GetProperty("mode").GetProperty("default").GetString() == "safe", Raw(p.GetProperty("mode")));
            var options = p.GetProperty("options");
            Check("D1c", "nested object kept two levels deep (options.depth integer, options.flags array of boolean, options.inner.name)",
                options.GetProperty("properties").GetProperty("depth").GetProperty("type").GetString() == "integer"
                && options.GetProperty("properties").GetProperty("flags").GetProperty("items").GetProperty("type").GetString() == "boolean"
                && options.GetProperty("properties").GetProperty("inner").GetProperty("properties").GetProperty("name").GetProperty("type").GetString() == "string"
                && options.GetProperty("required").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "depth" }), Raw(options));
            Check("D1d", "required names that do not exist are dropped (inner.ghost, root 'nonexistent')",
                !Raw(options).Contains("ghost") && complex.InputSchema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "tags", "mode" }), Raw(complex.InputSchema.GetProperty("required")));
            Check("D1e", "a local $ref is inlined (target.url, target.tab; format folded into the description)",
                p.GetProperty("target").GetProperty("properties").GetProperty("url").GetProperty("description").GetString()?.Contains("uri") == true
                && p.GetProperty("target").GetProperty("properties").TryGetProperty("tab", out _), Raw(p.GetProperty("target")));
            Check("D1f", "anyOf [null, enum string] → the string branch, description kept",
                p.GetProperty("choice").GetProperty("type").GetString() == "string" && p.GetProperty("choice").GetProperty("enum").GetArrayLength() == 2
                && p.GetProperty("choice").GetProperty("description").GetString() == "A or B", Raw(p.GetProperty("choice")));
            Check("D1g", "every array has items (loose → string items; points → array of string arrays)",
                p.GetProperty("loose").GetProperty("items").GetProperty("type").GetString() == "string"
                && p.GetProperty("points").GetProperty("items").GetProperty("items").GetProperty("type").GetString() == "string", Raw(p.GetProperty("points")));
            Check("D1h", "an object with anyOf constraints keeps its own properties (pick.a, pick.b)",
                p.GetProperty("pick").GetProperty("properties").TryGetProperty("a", out _) && p.GetProperty("pick").GetProperty("properties").TryGetProperty("b", out _), Raw(p.GetProperty("pick")));
            string? v = SchemaRules.Violation(complex.InputSchema);
            Check("D1i", "nothing a provider rejects is left ($schema, additionalProperties, format, $ref, $defs, anyOf/oneOf/allOf, type arrays, item-less arrays)", v == null, v ?? "");

            var np = nullable.InputSchema.GetProperty("properties");
            Check("D2", "a type array ['string','null'] no longer drops the parameters after it (note, count, after all present)",
                np.TryGetProperty("note", out var note) && note.GetProperty("type").GetString() == "string" && np.TryGetProperty("count", out _) && np.TryGetProperty("after", out _), Raw(np));
            Check("D2b", "…its null enum value goes too, and the sanitized form passes the rules", SchemaRules.Violation(nullable.InputSchema) == null && !Raw(np).Contains("null"), Raw(np));

            Check("D3", "a schema that is not an object, or garbage, becomes {type: object, properties: {}} (never throws)",
                Raw(McpSchemaSanitizer.Sanitize(JsonDocument.Parse("[1,2]").RootElement)) == "{\"type\":\"object\",\"properties\":{}}"
                && Raw(McpSchemaSanitizer.Sanitize(null)) == "{\"type\":\"object\",\"properties\":{}}");
            string text = Svc.GetToolDefinitionsPrompt();
            Check("D4", "the [ACTION:] catalogue no longer throws on a type array and lists the parameters after it",
                text.Contains("mcp__stub__nullable_tool") && text.Contains("count: <integer>") && text.Contains("note: <string>"), Trunc(text[Math.Max(0, text.IndexOf("nullable_tool", StringComparison.Ordinal) - 20)..], 400));
            return Task.CompletedTask;
        });
        await Case("D5", async () =>
        {
            Answer = true;
            var r = await Call("mcp__stub__nullable_tool", ("note", "2026"), ("count", "3"), ("after", "true"));
            string o = Seen(r);
            Check("D5", "typed arguments reach the server: a ['string','null'] property stays a string (\"2026\"), integer is a number, a string 'true' stays a string",
                o.Contains("\"note\":\"2026\"") && o.Contains("\"count\":3") && o.Contains("\"after\":\"true\""), o);
            var r2 = await Call("mcp__stub__complex_tool", ("tags", "[\"red\",\"blue\"]"), ("mode", "fast"), ("options", "{\"depth\":2,\"flags\":[true]}"));
            string o2 = Seen(r2);
            Check("D5b", "an array / object argument (flattened to JSON text by the native loop) arrives as real JSON", o2.Contains("\"tags\":[\"red\",\"blue\"]") && o2.Contains("\"options\":{\"depth\":2"), o2);
            // LOGIC (review): typing read only a raw "type" key — anyOf[string,null] (pydantic's Optional[str]) and a $ref
            // to a string were told to the model as strings but sent as 2026 / true.
            var r3 = await Call("mcp__stub__typed_tool", ("note", "2026"), ("label", "true"), ("plain", "2026"), ("count", "5"), ("flag", "true"));
            string o3 = Seen(r3);
            Check("D5c", "anyOf[string,null] and a $ref to a string stay strings (\"2026\", \"true\") — as the sanitized schema tells the model",
                o3.Contains("\"note\":\"2026\"") && o3.Contains("\"label\":\"true\"") && o3.Contains("\"plain\":\"2026\""), o3);
            Check("D5d", "negative control: anyOf[integer,null] and oneOf[null,boolean] are still typed (5, true)",
                o3.Contains("\"count\":5") && o3.Contains("\"flag\":true"), o3);
            var typed = Svc.BuildNativeToolSchemas().FirstOrDefault(s => s.Name == "mcp__stub__typed_tool");
            Check("D5e", "…and that is what the model is offered for them (note/label: string, count: integer)",
                typed != null && typed.InputSchema.GetProperty("properties").GetProperty("note").GetProperty("type").GetString() == "string"
                && typed.InputSchema.GetProperty("properties").GetProperty("label").GetProperty("type").GetString() == "string"
                && typed.InputSchema.GetProperty("properties").GetProperty("count").GetProperty("type").GetString() == "integer",
                typed == null ? "not offered" : Raw(typed.InputSchema));
        });
        await Case("D8", () =>
        {
            // LOGIC (review): inlining $refs had no size budget — a definition reused at every level multiplied (1.5 KB →
            // 11.9 MB, +144 MB heap), and the schema build re-did it several times a turn.
            var bomb = JsonDocument.Parse(StubMcp.BombSchema(7, 6)).RootElement.Clone();
            long heap0 = GC.GetTotalMemory(true);
            var sw = Stopwatch.StartNew();
            var cut = McpSchemaSanitizer.Sanitize(bomb);
            long ms = sw.ElapsedMilliseconds;
            long heap = GC.GetTotalMemory(false) - heap0;
            string cutText = Raw(cut);
            int budget = McpSchemaSanitizer.CharBudgetFor(bomb.GetRawText().Length);
            Check("D8", $"a schema bomb (fan 7, 6 levels, {bomb.GetRawText().Length:N0} B raw) is cut to its budget: ≤ {budget + budget / 2:N0} chars, in < 1 s",
                cutText.Length <= budget + budget / 2 && ms < 1000 && cutText.Contains(McpSchemaSanitizer.TooLargeNote), $"{cutText.Length:N0} chars in {ms} ms, heap +{heap / 1024:N0} KB");
            Check("D8b", "…and what is left is still a schema every API accepts", SchemaRules.Violation(cut) == null, SchemaRules.Violation(cut) ?? "");

            // The live path: the stub offers the bomb; the per-turn schema build is memoized.
            var sw2 = Stopwatch.StartNew();
            var first = Svc.BuildNativeToolSchemas().FirstOrDefault(s => s.Name == "mcp__stub__bomb_tool");
            long firstMs = sw2.ElapsedMilliseconds;
            int cached = McpSchemaSanitizer.CachedCount;
            sw2.Restart();
            for (int i = 0; i < 4; i++) Svc.BuildNativeToolSchemas();
            long fourMs = sw2.ElapsedMilliseconds;
            Check("D8c", "the stub's bomb tool is offered within the budget, and four more schema builds (a turn's worth) re-sanitize nothing",
                first != null && Raw(first.InputSchema).Length <= budget + budget / 2 && McpSchemaSanitizer.CachedCount == cached && fourMs < 1000,
                $"offered={first != null} {(first == null ? 0 : Raw(first.InputSchema).Length):N0} chars, first build {firstMs} ms, 4 builds {fourMs} ms, cache {cached}→{McpSchemaSanitizer.CachedCount}");

            // Negative control: a big but honest schema (300 described parameters, no refs) loses nothing.
            var big = new StringBuilder("{\"type\":\"object\",\"properties\":{");
            for (int i = 0; i < 300; i++)
                big.Append(i == 0 ? "" : ",").Append($"\"p{i}\":{{\"type\":\"{(i % 3 == 0 ? "integer" : "string")}\",\"description\":\"Parameter number {i} — a reasonably long description of what it does.\"}}");
            big.Append("}}");
            var bigOut = McpSchemaSanitizer.Sanitize(JsonDocument.Parse(big.ToString()).RootElement);
            var bigProps = bigOut.GetProperty("properties");
            bool allKept = Enumerable.Range(0, 300).All(i => bigProps.TryGetProperty($"p{i}", out var pp)
                && pp.GetProperty("type").GetString() == (i % 3 == 0 ? "integer" : "string") && pp.GetProperty("description").GetString()!.Contains($"number {i} "));
            Check("D8d", $"negative control: an honest {big.Length:N0}-char schema with 300 parameters is kept whole (types and descriptions, nothing collapsed)",
                allKept && !Raw(bigOut).Contains(McpSchemaSanitizer.TooLargeNote), Trunc(Raw(bigOut), 300));
            return Task.CompletedTask;
        });
        await Case("D8e", () =>
        {
            // LOGIC (re-review): enum values were de-duplicated pairwise with a re-parse per comparison (2,000 values: 1 s,
            // 20,000: 59 s on the first turn) and never counted against the tool's budget (sent whole, however large).
            static string EnumSchema(int n, int dupes) =>
                "{\"type\":\"object\",\"properties\":{\"icon\":{\"type\":\"string\",\"description\":\"Icon name.\",\"enum\":["
                + string.Join(",", Enumerable.Range(0, n).Select(i => $"\"v{i}\"").Concat(Enumerable.Repeat("\"v1\"", dupes))) + "]}}}";
            var timings = new List<string>();
            bool allFast = true;
            JsonElement big = default;
            string bigRaw = "";
            foreach (int n in new[] { 2_000, 20_000, 60_000 })
            {
                string raw = EnumSchema(n, 500);
                var parsed = JsonDocument.Parse(raw).RootElement.Clone();
                var sw = Stopwatch.StartNew();
                var outEl = McpSchemaSanitizer.Sanitize(parsed);
                long ms = sw.ElapsedMilliseconds;
                timings.Add($"{n:N0} values: {ms} ms → {Raw(outEl).Length:N0} chars");
                if (ms >= 1000) allFast = false;
                if (n == 20_000) { big = outEl; bigRaw = raw; }
            }
            var icon = big.GetProperty("properties").GetProperty("icon");
            string iconDesc = icon.TryGetProperty("description", out var idd) ? idd.GetString() ?? "" : "";
            int budget = McpSchemaSanitizer.CharBudgetFor(bigRaw.Length);
            Check("D8e", "enums of 2,000 / 20,000 / 60,000 values (+500 duplicates) are sanitized in < 1 s each",
                allFast, string.Join("; ", timings));
            Check("D8e2", "a 20,000-value enum is not sent whole: within the tool's budget and MaxEnumChars, offered as a plain string whose description says how many values there are and lists the first ones",
                Raw(big).Length <= Math.Min(budget, McpSchemaSanitizer.MaxEnumChars) && !icon.TryGetProperty("enum", out _)
                && icon.GetProperty("type").GetString() == "string" && iconDesc.StartsWith("Icon name.") && iconDesc.Contains(McpSchemaSanitizer.TooManyValuesNote)
                && iconDesc.Contains("20,000") && iconDesc.Contains("\"v0\", \"v1\", \"v2\"") && SchemaRules.Violation(big) == null,
                $"{Raw(big).Length:N0} chars (budget {budget:N0}): {Trunc(Raw(icon), 300)}");

            // Negative controls: an ordinary enum is kept as an enum — duplicates dropped, order kept, numbers by value.
            var small = McpSchemaSanitizer.Sanitize(JsonDocument.Parse(
                "{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\",\"enum\":[\"a\",\"b\",\"a\",\"c\",\"b\"]},"
                + "\"ratio\":{\"type\":\"number\",\"enum\":[1,1.0,2.5,2.5,3]}}}").RootElement);
            var sp = small.GetProperty("properties");
            string modes = string.Join(",", sp.GetProperty("mode").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
            string ratios = string.Join(",", sp.GetProperty("ratio").GetProperty("enum").EnumerateArray().Select(x => x.GetRawText()));
            var thousand = McpSchemaSanitizer.Sanitize(JsonDocument.Parse(EnumSchema(1_000, 10)).RootElement).GetProperty("properties").GetProperty("icon");
            Check("D8e3", "negative control: an ordinary enum stays an enum, each value once in order (a,b,a,c,b → a,b,c; 1, 1.0 → 1); a 1,000-value one (~8 KB) is kept whole",
                modes == "a,b,c" && ratios == "1,2.5,3" && thousand.TryGetProperty("enum", out var te) && te.GetArrayLength() == 1_000
                && !(thousand.GetProperty("description").GetString() ?? "").Contains(McpSchemaSanitizer.TooManyValuesNote),
                $"mode={modes} ratio={ratios} thousand={(thousand.TryGetProperty("enum", out var te2) ? te2.GetArrayLength() : -1)}");
            return Task.CompletedTask;
        });
        await Case("D8f", () =>
        {
            // MINOR (re-review): past the budget a $ref was always collapsed to an object — a $ref to a string was then
            // offered (and typed) as an object.
            string bomb = StubMcp.BombSchema(7, 6);
            string defs = "\"$defs\":{";
            int at = bomb.IndexOf(defs, StringComparison.Ordinal) + defs.Length;
            string schema = bomb[..at] + "\"Label\":{\"type\":\"string\"},\"Tags\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},"
                + "\"Maybe\":{\"anyOf\":[{\"type\":\"null\"},{\"type\":\"integer\"}]}," + bomb[at..];
            schema = schema[..^2] + ",\"label\":{\"$ref\":\"#/$defs/Label\"},\"tags\":{\"$ref\":\"#/$defs/Tags\"},\"maybe\":{\"$ref\":\"#/$defs/Maybe\"},\"deep\":{\"$ref\":\"#/$defs/L1\"}}}";
            var cut = McpSchemaSanitizer.Sanitize(JsonDocument.Parse(schema).RootElement);
            var types = McpSchemaSanitizer.DeclaredTypes(cut);
            var props = cut.GetProperty("properties");
            bool collapsed = props.TryGetProperty("label", out var lab) && (lab.GetProperty("description").GetString() ?? "") == McpSchemaSanitizer.TooLargeNote;
            Check("D8f", "over the budget, a $ref is followed one step for its type: label ($ref → string) is a string, tags an array (with items), maybe ($ref → anyOf[null,integer]) an integer",
                collapsed && types.GetValueOrDefault("label") == "string" && types.GetValueOrDefault("tags") == "array"
                && props.GetProperty("tags").TryGetProperty("items", out _) && types.GetValueOrDefault("maybe") == "integer" && SchemaRules.Violation(cut) == null,
                $"collapsed={collapsed} types={string.Join(", ", types.Select(kv => kv.Key + ":" + kv.Value))}");
            Check("D8f2", "negative control: a $ref to an object over the budget is still an object (deep → L1)",
                types.GetValueOrDefault("deep") == "object" && (props.GetProperty("deep").GetProperty("description").GetString() ?? "") == McpSchemaSanitizer.TooLargeNote,
                Trunc(Raw(props.GetProperty("deep")), 200));
            return Task.CompletedTask;
        });
        await Case("D8g", () =>
        {
            // Same budget rule for the other free-text parts: one parameter's 600 KB description or 300 KB default went in
            // whole, uncounted.
            string schema = "{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\",\"description\":\"" + new string('d', 600_000)
                + "\",\"default\":\"" + new string('z', 300_000) + "\"},\"n\":{\"type\":\"integer\",\"default\":5,\"description\":\"How many.\"}}}";
            var sw = Stopwatch.StartNew();
            var outEl = McpSchemaSanitizer.Sanitize(JsonDocument.Parse(schema).RootElement);
            long ms = sw.ElapsedMilliseconds;
            var q = outEl.GetProperty("properties").GetProperty("q");
            var n = outEl.GetProperty("properties").GetProperty("n");
            string qd = q.GetProperty("description").GetString() ?? "";
            Check("D8g", "a 600 KB description is cut (≤ MaxDescriptionChars, ends with …) and a 300 KB default dropped — the tool stays small; the next parameter keeps its own default and description (control)",
                Raw(outEl).Length <= McpSchemaSanitizer.MaxDescriptionChars + 1024 && qd.Length <= McpSchemaSanitizer.MaxDescriptionChars + 1 && qd.EndsWith("…")
                && !q.TryGetProperty("default", out _) && n.TryGetProperty("default", out var nd) && nd.GetInt32() == 5
                && n.GetProperty("description").GetString() == "How many." && ms < 1000,
                $"{Raw(outEl).Length:N0} chars in {ms} ms; q.description {qd.Length:N0}; n={Raw(n)}");
            return Task.CompletedTask;
        });
        await Case("D9", () =>
        {
            // MINOR (review): value limits were dropped, and property names were never checked.
            var schemas2 = Svc.BuildNativeToolSchemas();
            var cx = schemas2.FirstOrDefault(s => s.Name == "mcp__stub__complex_tool");
            if (cx == null) { Check("D9", "complex_tool offered", false); return Task.CompletedTask; }
            var p = cx.InputSchema.GetProperty("properties");
            string slug = p.TryGetProperty("slug", out var sl) ? sl.GetProperty("description").GetString() ?? "" : "";
            string depthDesc = p.GetProperty("options").GetProperty("properties").GetProperty("depth").TryGetProperty("description", out var dd) ? dd.GetString() ?? "" : "";
            Check("D9", "value limits are folded into the description (slug: length 1-64 and its pattern; options.depth: minimum 1)",
                slug.Contains("at least (characters) 1") && slug.Contains("at most (characters) 64") && slug.Contains("pattern ^[a-z-]+$") && depthDesc.Contains("minimum 1"), $"slug='{slug}' depth='{depthDesc}'");
            bool rawHasBad = mcp.ToolRegistry.FindTool("mcp__stub__complex_tool")?.InputSchema?.GetProperty("properties").TryGetProperty("bad key!", out _) == true;
            Check("D9b", "a parameter whose name the APIs refuse ('bad key!') is left out — the server's raw schema has it (control), the offered one does not, the rest stay",
                rawHasBad && !p.TryGetProperty("bad key!", out _) && p.TryGetProperty("tags", out _) && p.TryGetProperty("slug", out _), Trunc(Raw(p), 300));
            return Task.CompletedTask;
        });
        await Case("D6", async () =>
        {
            if (complex == null || nullable == null) { Check("D6", "schemas available", false); return; }
            var tools = new List<ToolSchema> { complex, nullable };
            await ProviderWire.Run(tools, Check);
        });

        // ═══════════════════ startup timeout ═══════════════════
        Console.WriteLine("\n── initialize timeout: per server, longer for a package runner's first start ──");
        await Case("E1", () =>
        {
            Check("E1", "StartupTimeoutFor: entry value, else 120 s for an npx/uvx first start, else 15 s",
                mcp.StartupTimeoutFor("t1", new McpServerConfig { Command = "node", StartupTimeoutSeconds = 45 }) == 45
                && mcp.StartupTimeoutFor("t2", new McpServerConfig { Command = "npx", Args = new() { "@playwright/mcp@latest" } }) == 120
                && mcp.StartupTimeoutFor("t3", new McpServerConfig { Command = "cmd", Args = new() { "/c", "npx", "-y", "x" } }) == 120
                && mcp.StartupTimeoutFor("t4", new McpServerConfig { Command = @"C:\Users\x\.local\bin\uvx.exe", Args = new() { "mcp-server-git" } }) == 120
                && mcp.StartupTimeoutFor("t5", new McpServerConfig { Command = "node", Args = new() { "server.js" } }) == 15
                && mcp.StartupTimeoutFor("t6", new McpServerConfig { Command = "node", StartupTimeoutSeconds = 99999 }) == McpServerManager.MaxStartupTimeoutSeconds);
            // LOGIC (review): "first start" was remembered in memory only, so every app start was a first start — one
            // silent npx server held startup for 120 s each time. Now a launch that answered once is on disk.
            var stubCfg = new McpServerConfig { Command = exe, Args = new() { "--stub-mcp" } };
            Check("E1b", "the stub's launch (it answered initialize above) is remembered on disk: a fresh history (= the next app start) knows it; another launch does not",
                new McpStartHistory(settings.DataRoot).HasBeenReady("stub", stubCfg)
                && !new McpStartHistory(settings.DataRoot).HasBeenReady("stub", new McpServerConfig { Command = exe, Args = new() { "--stub-mcp", "--other" } })
                && !new McpStartHistory(settings.DataRoot).HasBeenReady("never-started", stubCfg));
            string pkg = "pkg-" + Guid.NewGuid().ToString("N")[..8];
            var npx = new McpServerConfig { Command = "npx", Args = new() { "-y", "@example/mcp-" + pkg } };
            int before = new McpServerManager(settings).StartupTimeoutFor(pkg, npx);
            new McpStartHistory(settings.DataRoot).Record(pkg, npx);
            int after = new McpServerManager(settings).StartupTimeoutFor(pkg, npx);
            int otherPackage = new McpServerManager(settings).StartupTimeoutFor(pkg, new McpServerConfig { Command = "npx", Args = new() { "-y", "@example/another" } });
            Check("E1c", "an npx launch gets 120 s until it has answered once; after that a NEW manager (the next app start) gives it 15 s — a different package under the same name gets 120 s again",
                before == 120 && after == 15 && otherPackage == 120, $"before={before} after={after} otherPackage={otherPackage}");
            return Task.CompletedTask;
        });
        await Case("E2", async () =>
        {
            mcp.SetConfig("slow30", new McpServerConfig { Command = exe, Args = new() { "--stub-mcp", "--init-delay", "20" }, Enabled = true, StartupTimeoutSeconds = 30 });
            mcp.SetConfig("slow15", new McpServerConfig { Command = exe, Args = new() { "--stub-mcp", "--init-delay", "20" }, Enabled = true, StartupTimeoutSeconds = 15 });
            var sw = Stopwatch.StartNew();
            var t30 = mcp.StartServerAsync("slow30");
            var t15 = mcp.StartServerAsync("slow15");
            bool ok15 = await t15;
            long ms15 = sw.ElapsedMilliseconds;
            bool ok30 = await t30;
            long ms30 = sw.ElapsedMilliseconds;
            string err15 = mcp.GetServerLastError("slow15") ?? "";
            Check("E2", "a 20 s initialize with StartupTimeoutSeconds=30 ends Ready", ok30 && mcp.GetServerState("slow30") == McpServerState.Ready && ms30 >= 19_000, $"ok={ok30} state={mcp.GetServerState("slow30")} after {ms30} ms");
            Check("E2b", "…with 15 it ends Failed at ~15 s, the message names the limit and the fix", !ok15 && mcp.GetServerState("slow15") == McpServerState.Failed
                && ms15 is >= 14_000 and < 19_500 && err15.Contains("15") && err15.Contains("startupTimeoutSeconds"), $"ok={ok15} after {ms15} ms: {err15}");
            // UX (review): the Thai message said "กด Restart" — the button on the MCP page reads "เริ่มใหม่" in Thai.
            var loc = provider.GetRequiredService<LocalizationService>();
            string lang = loc.CurrentLanguage;
            try
            {
                loc.SetLanguage("th");
                string th = loc.T("mcp.handshake.timedOut"), thButton = loc.T("mcp.restart");
                loc.SetLanguage("en");
                string en = loc.T("mcp.handshake.timedOut"), enButton = loc.T("mcp.restart");
                Check("E2c", "the timeout message names the Restart button by its label in each language (th: 'เริ่มใหม่'; en: 'Restart' — control)",
                    thButton == "เริ่มใหม่" && th.Contains(thButton) && !th.Contains("Restart") && en.Contains(enButton), th);
            }
            finally { loc.SetLanguage(lang); }
            await mcp.StopServerAsync("slow30");
            await mcp.StopServerAsync("slow15");
            mcp.RemoveConfig("slow30");
            mcp.RemoveConfig("slow15");
        });
        await Case("E3", async () =>
        {
            // LOGIC (review): StartAllEnabledAsync started servers one after another — one that never answers held every
            // server behind it (BrainX included) for its whole timeout (probe P: 124,831 ms).
            mcp.SetConfig("slowpar", new McpServerConfig { Command = exe, Args = new() { "--stub-mcp", "--init-delay", "8" }, Enabled = true, StartupTimeoutSeconds = 20 });
            mcp.SetConfig("fastpar", new McpServerConfig { Command = exe, Args = new() { "--stub-mcp" }, Enabled = true });
            try
            {
                var sw = Stopwatch.StartNew();
                var all = mcp.StartAllEnabledAsync();
                long fastAt = -1;
                McpServerState slowWhenFastReady = McpServerState.Stopped;
                while (!all.IsCompleted && sw.ElapsedMilliseconds < 30_000)
                {
                    if (fastAt < 0 && mcp.GetServerState("fastpar") == McpServerState.Ready)
                    {
                        fastAt = sw.ElapsedMilliseconds;
                        slowWhenFastReady = mcp.GetServerState("slowpar");
                    }
                    await Task.Delay(50);
                }
                await all;
                long total = sw.ElapsedMilliseconds;
                Check("E3", "StartAllEnabledAsync starts servers side by side: a fast one is Ready within 6 s while an 8 s one is still starting",
                    fastAt is >= 0 and < 6000 && slowWhenFastReady == McpServerState.Starting && total >= 7500
                    && mcp.GetServerState("slowpar") == McpServerState.Ready && mcp.GetServerState("stub") == McpServerState.Ready,
                    $"fast Ready at {fastAt} ms (slow was {slowWhenFastReady}), all done at {total} ms, slow={mcp.GetServerState("slowpar")} stub={mcp.GetServerState("stub")}");
            }
            finally
            {
                await mcp.StopServerAsync("slowpar");
                await mcp.StopServerAsync("fastpar");
                mcp.RemoveConfig("slowpar");
                mcp.RemoveConfig("fastpar");
            }
        });
        await Case("E1d", async () =>
        {
            // MINOR (re-review): Record wrote one shared "mcp-started.json.tmp" outside the lock while four servers start
            // at once — writers collided (IOException swallowed) or an older snapshot landed last, and a launch was lost.
            string dir = Path.Combine(exeDir, "start-history-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(dir);
            try
            {
                var lost = new List<string>();
                for (int round = 0; round < 5; round++)
                {
                    var history = new McpStartHistory(dir);
                    var names = Enumerable.Range(0, 24).Select(i => $"r{round}-srv{i}").ToList();
                    using var go = new ManualResetEventSlim(false);
                    var writers = names.Select(n => Task.Run(() => { go.Wait(); history.Record(n, new McpServerConfig { Command = "node", Args = new() { n + ".js" } }); })).ToList();
                    go.Set();
                    await Task.WhenAll(writers);
                    var fresh = new McpStartHistory(dir);   // = the next app start, reading the file
                    lost.AddRange(names.Where(n => !fresh.HasBeenReady(n, new McpServerConfig { Command = "node", Args = new() { n + ".js" } })));
                }
                var leftovers = Directory.GetFiles(dir, "*.tmp");
                var reread = new McpStartHistory(dir);
                bool earlierRounds = reread.HasBeenReady("r0-srv0", new McpServerConfig { Command = "node", Args = new() { "r0-srv0.js" } });
                bool unknown = reread.HasBeenReady("never-recorded", new McpServerConfig { Command = "node", Args = new() { "x.js" } });
                Check("E1d", "24 launches recorded at once (5 rounds, each a separate history like separate app starts) are all on disk afterwards; no .tmp left; earlier rounds kept; an unrecorded launch is unknown (control)",
                    lost.Count == 0 && leftovers.Length == 0 && earlierRounds && !unknown,
                    $"lost={string.Join(",", lost)} tmp={leftovers.Length} earlier={earlierRounds} unknown={unknown}");
            }
            finally { DeleteTree(dir); }
        });
        await Case("E4", async () =>
        {
            // MINOR (re-review): the pipe accepts tasks before the startup MCP work (BrainX) is done, and its caller was
            // not told. A task waits for it briefly (App.HandleMcpToolAsync) and is told when it is still not ready.
            var fresh = new McpServerManager(settings);
            var sw = Stopwatch.StartNew();
            bool none = await fresh.WaitForStartupAsync(TimeSpan.FromSeconds(10));
            long noneMs = sw.ElapsedMilliseconds;
            var startup = new TaskCompletionSource();
            fresh.TrackStartup(startup.Task);
            sw.Restart();
            bool early = await fresh.WaitForStartupAsync(TimeSpan.FromMilliseconds(400));
            long earlyMs = sw.ElapsedMilliseconds;
            var waiting = fresh.WaitForStartupAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(150);
            sw.Restart();
            startup.SetResult();
            bool done = await waiting;
            long doneMs = sw.ElapsedMilliseconds;
            bool after = await fresh.WaitForStartupAsync(TimeSpan.FromSeconds(10));
            Check("E4", "a task waits for the startup MCP work: still running after the wait → false (~0.4 s); finishing during the wait → true at once; nothing tracked (harness / later task) → true without waiting (control)",
                none && noneMs < 200 && !early && earlyMs is >= 350 and < 3000 && done && doneMs < 1000 && after,
                $"none={none} {noneMs} ms, early={early} {earlyMs} ms, done={done} {doneMs} ms, after={after}");
            using var cancel = new CancellationTokenSource(200);
            var startup2 = new TaskCompletionSource();
            fresh.TrackStartup(startup2.Task);
            bool cancelled = false;
            try { await fresh.WaitForStartupAsync(TimeSpan.FromSeconds(30), cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            startup2.SetResult();
            Check("E4b", "a caller that goes away during the wait is not held for it (cancelled), and the note the caller gets names the cause",
                cancelled && McpServerManager.StartupNotReadyNote.Contains("still starting"), $"cancelled={cancelled}");
        });

        // ═══════════════════ Accept edits ═══════════════════
        Console.WriteLine("\n── Accept edits (session mode) ──");
        await Case("H1", async () =>
        {
            string outside = Path.Combine(exeDir, "outside-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(outside);
            string link = Path.Combine(project, "linked");
            Cmd($"mklink /J \"{link}\" \"{outside}\"");
            Directory.CreateDirectory(Path.Combine(project, ".git", "hooks"));
            try
            {
                Check("H1", "path rule: inside yes; the root, '..', another folder, .git, .cluadex, a junction out, an ADS, a device name, an 8.3 alias no",
                    PermissionService.IsPlainPathInside(project, "src/a.cs") && PermissionService.IsPlainPathInside(project, Path.Combine(project, "b.txt"))
                    && !PermissionService.IsPlainPathInside(project, ".") && !PermissionService.IsPlainPathInside(project, "..\\x.txt")
                    && !PermissionService.IsPlainPathInside(project, Path.Combine(outside, "y.txt"))
                    && !PermissionService.IsPlainPathInside(project, ".git/hooks/pre-commit") && !PermissionService.IsPlainPathInside(project, ".cluadex/hooks.json")
                    && !PermissionService.IsPlainPathInside(project, "linked/evil.txt") && !PermissionService.IsPlainPathInside(project, "a.txt:hidden")
                    && !PermissionService.IsPlainPathInside(project, "nul.txt") && !PermissionService.IsPlainPathInside(project, "GIT~1/hooks/x"),
                    $"junction exists={Directory.Exists(link)}");

                Answer = false;
                perms.SetAcceptEdits(null);
                var (_, off, _) = await Gate("edit_file", ("path", "notes.txt"), ("find", "alpha"), ("replace", "ALPHA"));
                perms.SetAcceptEdits(project);
                var (e, on, _) = await Gate("edit_file", ("path", "notes.txt"), ("find", "alpha"), ("replace", "ALPHA"));
                var (_, cmd, _) = await Gate("run_command", ("command", "echo hi"));
                var (_, outsideW, _) = await Gate("write_file", ("path", Path.Combine(outside, "z.txt")), ("content", "x"));
                var (_, viaLink, _) = await Gate("write_file", ("path", "linked/z.txt"), ("content", "x"));
                var (_, gitHook, _) = await Gate("write_file", ("path", ".git/hooks/pre-commit"), ("content", "x"));
                var (_, mcpRisky, _) = await Gate("mcp__stub__browser_click", ("element", "e"));
                Check("H2", "on: edit_file inside the project runs without a prompt (it asked when off)", off == 1 && on == 0 && e.Success && File.ReadAllText(Path.Combine(project, "notes.txt")).Contains("ALPHA"), $"off={off} on={on} {Seen(e)}");
                Check("H2b", "on: run_command, a write outside, through a junction, into .git/hooks and a risky MCP tool still ask", cmd == 1 && outsideW == 1 && viaLink == 1 && gitHook == 1 && mcpRisky == 1,
                    $"cmd={cmd} outside={outsideW} link={viaLink} git={gitHook} mcp={mcpRisky}");
                var deny = new PermissionRule { Pattern = "secret/*", Scope = "write", Action = PermAction.Deny };
                perms.AddRule(deny);
                try
                {
                    var (d, dn, _) = await Gate("write_file", ("path", "secret/k.txt"), ("content", "x"));
                    Check("H2c", "on: a user's Deny rule still wins", !d.Success && dn == 0, Seen(d));
                }
                finally { perms.RemoveRule(deny); }

                string other = Path.Combine(exeDir, "mcp-project-2");
                Directory.CreateDirectory(other);
                fs.WorkingDirectory = other;
                var (_, switched, _) = await Gate("write_file", ("path", "new.txt"), ("content", "x"));
                fs.WorkingDirectory = project;
                Check("H3", "it is bound to the folder it was switched on for: in another project the edit asks", switched == 1, $"prompts={switched}");

                // CRITICAL (review): files that define or run the build, hooks, editor tasks and CI were inside "the
                // project" — written unseen, then run by the automatic build check. They are outside Accept edits now.
                var codeRunning = new[] { "Directory.Build.props", "Directory.Build.targets", "src/App/App.csproj", "App.sln", "build/x.targets",
                    "package.json", "web/package.json", ".npmrc", "vite.config.ts", "webpack.config.js", "Makefile", "CMakeLists.txt", "build.rs",
                    "Cargo.toml", "pyproject.toml", "setup.py", "build.gradle.kts", "pom.xml", "global.json", "nuget.config",
                    ".husky/pre-commit", ".githooks/pre-push", ".vscode/tasks.json", ".claude/settings.json", ".github/workflows/ci.yml",
                    ".github/actions/x/action.yml", ".idea/workspace.xml", ".devcontainer/devcontainer.json", ".ssh/authorized_keys", ".aws/credentials" };
                var stillCovered = codeRunning.Where(f => PermissionService.IsPlainPathInside(project, f)).ToList();
                Check("H4", "Accept edits never covers build/hook/editor/CI files (Directory.Build.props, *.csproj, package.json, vite.config.ts, Makefile, .husky, .vscode, .claude, .github/workflows …)",
                    stillCovered.Count == 0, "still covered: " + string.Join(", ", stillCovered));
                var plain = new[] { "src/Program.cs", "README.md", ".github/ISSUE_TEMPLATE/bug.md", "docs/package.json.md", "config/app.config.json", "src/makefile_notes.txt", "web/src/main.ts" };
                var notCovered = plain.Where(f => !PermissionService.IsPlainPathInside(project, f)).ToList();
                Check("H4b", "negative control: ordinary source and docs (Program.cs, main.ts, an issue template, app.config.json) are still covered",
                    notCovered.Count == 0, "not covered: " + string.Join(", ", notCovered));
                Answer = false;
                var (_, propsAsked, _) = await Gate("write_file", ("path", "Directory.Build.props"), ("content", "<Project><Target Name=\"X\" BeforeTargets=\"Build\"><Exec Command=\"echo pwned\" /></Target></Project>"));
                var (_, tasksAsked, _) = await Gate("write_file", ("path", ".vscode/tasks.json"), ("content", "{}"));
                Check("H4c", "on: writing Directory.Build.props (an <Exec> target) or .vscode/tasks.json asks", propsAsked == 1 && tasksAsked == 1 && !File.Exists(Path.Combine(project, "Directory.Build.props")), $"props={propsAsked} tasks={tasksAsked}");
            }
            finally
            {
                perms.SetAcceptEdits(null);
                try { Directory.Delete(link); } catch { }
                DeleteTree(outside);
            }
        });
        await Case("H4d", () =>
        {
            // MINOR (re-review): more files that run code later without anyone running them on purpose.
            var runsLater = new[] { "src/App/App.csproj.user", "Lib.vbproj.user", "conftest.py", "tests/conftest.py", "pytest.ini", "tox.ini", "noxfile.py",
                ".pre-commit-config.yaml", ".prettierrc.cjs", ".eslintrc.js", ".babelrc.js", "web/.mocharc.mjs", ".stylelintrc.cjs" };
            var stillCovered = runsLater.Where(f => PermissionService.IsPlainPathInside(project, f)).ToList();
            Check("H4d", "Accept edits never covers *.csproj.user, conftest.py, pytest.ini, tox.ini, noxfile.py, .pre-commit-config.yaml and .*rc.js / .cjs / .mjs",
                stillCovered.Count == 0, "still covered: " + string.Join(", ", stillCovered));
            var plain = new[] { "tests/test_app.py", "src/rc.js", "src/app.user", "docs/conftest.md", "src/user.ts", "src/eslintrc.md", "web/src/arc.js", "notes.user.txt" };
            var notCovered = plain.Where(f => !PermissionService.IsPlainPathInside(project, f)).ToList();
            Check("H4d2", "negative control: test_app.py, src/rc.js, arc.js, app.user, user.ts, docs/conftest.md stay covered",
                notCovered.Count == 0, "not covered: " + string.Join(", ", notCovered));
            return Task.CompletedTask;
        });
        await Case("H5e", () =>
        {
            // MINOR (re-review): Documents was not refused — it holds the PowerShell profiles, which run at every start.
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) { Check("H5e", "a Documents folder exists on this machine", false); return Task.CompletedTask; }
            bool docsRefused = PermissionService.IsUnsafeAcceptEditsRoot(docs, settings.DataRoot);
            bool psRefused = PermissionService.IsUnsafeAcceptEditsRoot(Path.Combine(docs, "WindowsPowerShell"), settings.DataRoot)
                && PermissionService.IsUnsafeAcceptEditsRoot(Path.Combine(docs, "PowerShell", "Modules"), settings.DataRoot);
            bool projectInDocs = !PermissionService.IsUnsafeAcceptEditsRoot(Path.Combine(docs, "Visual Studio 2022", "Projects", "app"), settings.DataRoot);
            Check("H5e", "Documents (and the PowerShell profile folders in it) are refused; a project INSIDE Documents is fine (control)",
                docsRefused && psRefused && projectInDocs, $"docs={docsRefused} ps={psRefused} projectInDocs={projectInDocs} ({docs})");
            return Task.CompletedTask;
        });
        await Case("H5", () =>
        {
            // LOGIC (review): a pipe caller could switch Accept edits on for C:\ or the user folder — "inside the
            // project" then included the Startup folder and ~/.ssh.
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string drive = Path.GetPathRoot(exeDir)!;
            var unsafeRoots = new[] { drive, @"C:\", profile, Path.Combine(profile, ".ssh"), Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SomeApp"), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                startup, Path.Combine(profile, "AppData"), settings.DataRoot, Path.Combine(settings.DataRoot, "sub"), exeDir };
            var wronglySafe = unsafeRoots.Where(r => !PermissionService.IsUnsafeAcceptEditsRoot(r, settings.DataRoot)).ToList();
            Check("H5", "a drive root, the user folder (or a folder holding it), ~/.ssh, Windows, Program Files, AppData, Startup and CluadeX's data folder are refused",
                wronglySafe.Count == 0, "accepted: " + string.Join(", ", wronglySafe));
            var fine = new[] { project, Path.Combine(profile, "source", "repos", "app"), Path.Combine(drive, "Code", "app") };
            var wronglyUnsafe = fine.Where(r => PermissionService.IsUnsafeAcceptEditsRoot(r, settings.DataRoot)).ToList();
            Check("H5b", "negative control: a normal project folder (the harness project, ~/source/repos/app, <drive>\\Code\\app) is fine", wronglyUnsafe.Count == 0, "refused: " + string.Join(", ", wronglyUnsafe));
            bool refused = !perms.SetAcceptEdits(@"C:\");
            bool off = perms.AcceptEditsProject == null && !perms.AcceptEditsCovers(@"C:\", Path.Combine(startup, "probe.cmd"));
            bool profileRefused = !perms.SetAcceptEdits(profile) && !perms.AcceptEditsCovers(profile, @".ssh\authorized_keys");
            bool projectOk = perms.SetAcceptEdits(project) && perms.AcceptEditsCovers(project, "src/a.cs");
            perms.SetAcceptEdits(null);
            Check("H5c", "SetAcceptEdits refuses C:\\ and the user folder (stays off: Startup\\probe.cmd, .ssh\\authorized_keys not covered) — and accepts the project (control)",
                refused && off && profileRefused && projectOk, $"refused={refused} off={off} profile={profileRefused} project={projectOk}");
            // A project folder that is a junction is judged where it really points.
            string toData = Path.Combine(exeDir, "junction-to-data"), toProject = Path.Combine(exeDir, "junction-to-project");
            try
            {
                Cmd($"mklink /J \"{toData}\" \"{settings.DataRoot}\"");
                Cmd($"mklink /J \"{toProject}\" \"{project}\"");
                Check("H5d", "a junction to CluadeX's data folder is refused (resolved); a junction to an ordinary project is fine (control)",
                    Directory.Exists(toData) && PermissionService.IsUnsafeAcceptEditsRoot(toData, settings.DataRoot)
                    && Directory.Exists(toProject) && !PermissionService.IsUnsafeAcceptEditsRoot(toProject, settings.DataRoot),
                    $"toData exists={Directory.Exists(toData)} toProject exists={Directory.Exists(toProject)}");
            }
            finally
            {
                try { Directory.Delete(toData); } catch { }
                try { Directory.Delete(toProject); } catch { }
            }
            return Task.CompletedTask;
        });
        await Case("H6", async () =>
        {
            // CRITICAL (review): an edit Accept edits let through unseen, then the automatic build check — which skipped
            // the gate — ran the project's build (probe X: an <Exec> marker was created with prompts=0).
            string marker = Path.Combine(project, "verify-ran.txt");
            string oldCmd = settings.Settings.AutoVerifyCommand;
            settings.Settings.AutoVerifyCommand = "echo verified> verify-ran.txt";
            try
            {
                // The Accept-edits cases above left unseen edits behind: the first build check asks about those (approved
                // here), so the control below starts from "every edit was seen".
                perms.SetAcceptEdits(null);
                Answer = true;
                int pending; lock (Prompts) pending = Prompts.Count;
                await Svc.RunVerifyBuildAsync(CancellationToken.None);
                lock (Prompts) pending = Prompts.Count - pending;
                Check("H6a", "the unseen edits the earlier cases made are still pending: the first build check asks once (Accept edits being off now changes nothing)",
                    pending == 1 && !Svc.HasUnreviewedAcceptedEdits, $"prompts={pending}");
                if (File.Exists(marker)) File.Delete(marker);
                // Control: Accept edits OFF, the edit is prompted (approved) → the build check runs without a prompt.
                var (_, editPrompted, _) = await Gate("write_file", ("path", "seen.txt"), ("content", "seen"));
                int p0; lock (Prompts) p0 = Prompts.Count;
                var v0 = await Svc.RunVerifyBuildAsync(CancellationToken.None);
                int n0; lock (Prompts) n0 = Prompts.Count - p0;
                Check("H6", "negative control: after an edit the user approved, the automatic build check runs without a prompt (as before)",
                    editPrompted == 1 && v0 is { Success: true } && n0 == 0 && File.Exists(marker), $"edit prompts={editPrompted} verify={(v0 == null ? "null" : v0.Success.ToString())} prompts={n0}");
                File.Delete(marker);

                // Accept edits ON: the edit runs unseen → the build check asks first; declined = not run, not asked again.
                perms.SetAcceptEdits(project);
                Answer = false;
                var (_, unseen, _) = await Gate("write_file", ("path", "scripts/build-helper.js"), ("content", "require('child_process').exec('calc')"));
                lock (Prompts) p0 = Prompts.Count;
                var v1 = await Svc.RunVerifyBuildAsync(CancellationToken.None);
                string ask; lock (Prompts) ask = Prompts.Count > p0 ? Prompts[^1] : "";
                int n1; lock (Prompts) n1 = Prompts.Count - p0;
                var v1b = await Svc.RunVerifyBuildAsync(CancellationToken.None);
                int n1b; lock (Prompts) n1b = Prompts.Count - p0 - n1;
                Check("H6b", "after an edit Accept edits let through unseen, the build check ASKS (naming the command); declined → not run (null), no marker, and not asked again for the same edits",
                    unseen == 0 && n1 == 1 && ask.Contains("echo verified") && v1 == null && v1b == null && n1b == 0 && !File.Exists(marker),
                    $"edit prompts={unseen} verify prompts={n1},{n1b} '{ask}' marker={File.Exists(marker)}");

                // Another unseen edit → asked again; approved → it runs.
                await Gate("write_file", ("path", "scripts/build-helper.js"), ("content", "// v2"));
                Answer = true;
                lock (Prompts) p0 = Prompts.Count;
                var v2 = await Svc.RunVerifyBuildAsync(CancellationToken.None);
                int n2; lock (Prompts) n2 = Prompts.Count - p0;
                var v3 = await Svc.RunVerifyBuildAsync(CancellationToken.None);
                int n3; lock (Prompts) n3 = Prompts.Count - p0 - n2;
                Check("H6c", "a new unseen edit asks again; approved → the build runs, and a second check over the same edits does not ask",
                    n2 == 1 && v2 is { Success: true } && File.Exists(marker) && n3 == 0 && v3 is { Success: true }, $"prompts={n2},{n3} v2={v2?.Success} marker={File.Exists(marker)}");

                // The user's Allow rule for the build command runs it without a prompt; their Deny never runs it.
                File.Delete(marker);
                await Gate("write_file", ("path", "scripts/build-helper.js"), ("content", "// v3"));
                var deny = new PermissionRule { Pattern = "*", Scope = "execute", Action = PermAction.Deny, ToolName = "run_build" };
                perms.AddRule(deny);
                lock (Prompts) p0 = Prompts.Count;
                var v4 = await Svc.RunVerifyBuildAsync(CancellationToken.None);
                perms.RemoveRule(deny);
                await Gate("write_file", ("path", "scripts/build-helper.js"), ("content", "// v4"));
                var allow = new PermissionRule { Pattern = "*", Scope = "execute", Action = PermAction.Allow, ToolName = "run_build" };
                perms.AddRule(allow);
                var v5 = await Svc.RunVerifyBuildAsync(CancellationToken.None);
                perms.RemoveRule(allow);
                int n4; lock (Prompts) n4 = Prompts.Count - p0;
                Check("H6d", "a Deny rule for run_build skips the check, an Allow rule runs it — neither asks", v4 == null && v5 is { Success: true } && n4 == 0 && File.Exists(marker),
                    $"deny→{(v4 == null ? "null" : "ran")} allow→{v5?.Success} prompts={n4}");
            }
            finally
            {
                settings.Settings.AutoVerifyCommand = oldCmd;
                perms.SetAcceptEdits(null);
                if (File.Exists(marker)) File.Delete(marker);
            }
        });
        await Case("H7", async () =>
        {
            // CRITICAL (re-review): the Autonomous loop ran its build/test command itself — past the gate, never looking at
            // edits Accept edits let through unseen (probe X: prompts=0, build runs 0→1, GoalMet=True right after the
            // user said No to the same build). Here the unseen edit IS the script the verify command runs.
            var auto = provider.GetRequiredService<AutonomousCodingService>();
            var s = settings.Settings;
            var keep = (s.AutoVerifyCommand, s.AutoFixMaxIterations, s.AutoReviewEnabled);
            string marker = Path.Combine(project, "auto-ran.txt");
            const string verify = @"scripts\helper.cmd";
            s.AutoVerifyCommand = verify;
            s.AutoFixMaxIterations = 1;
            s.AutoReviewEnabled = false;
            var phases = new List<string>();
            Action<string> onPhase = p => { lock (phases) phases.Add(p); };
            auto.OnPhase += onPhase;
            int Runs() => File.Exists(marker) ? File.ReadAllLines(marker).Length : 0;
            // A one-line brain-status question is answered by the app itself (no model call): iteration 1 "implements",
            // then the loop runs its verify command exactly as it does after a model turn.
            async Task<(AutonomousResult R, int Prompts, string Ask)> Autonomous()
            {
                int p0; lock (Prompts) p0 = Prompts.Count;
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var r = await auto.RunAsync(new List<ChatMessage>(), "is the brain connected?", cts.Token);
                lock (Prompts) return (r, Prompts.Count - p0, Prompts.Count > p0 ? Prompts[^1] : "");
            }
            try
            {
                if (File.Exists(marker)) File.Delete(marker);
                perms.SetAcceptEdits(project);
                Answer = false;
                var (w, writePrompts, _) = await Gate("write_file", ("path", "scripts/helper.cmd"), ("content", "@echo ran>> auto-ran.txt\r\n"));
                bool unseen = Svc.HasUnreviewedAcceptedEdits;
                var (r1, n1, ask1) = await Autonomous();
                string declined = string.Format(LocalizedResources.Instance["chat.auto.verifyDeclined"], verify);
                bool phaseSaid; lock (phases) phaseSaid = phases.Any(p => p.Contains(declined));
                Check("H7", "Autonomous loop after an unseen (Accept edits) edit ASKS before its build/test command (naming it and why); declined → not run, GoalMet false, the run stops and says so (localized)",
                    w.Success && writePrompts == 0 && unseen && n1 == 1 && ask1.Contains(verify) && ask1.Contains(LocalizedResources.Instance["perm.autoVerify.autonomous"])
                    && Runs() == 0 && !r1.GoalMet && r1.VerifyDeclined && r1.FixIterations == 1 && r1.Summary.Contains(declined) && phaseSaid,
                    $"write prompts={writePrompts} unseen={unseen} prompts={n1} runs={Runs()} goal={r1.GoalMet} declined={r1.VerifyDeclined} ask='{Trunc(ask1, 200)}' summary='{Trunc(r1.Summary, 300)}'");

                // That No stands for the verify-after-edit (no nagging every step) — but a run the user starts afterwards is
                // a new decision: it asks again.
                int pBefore; lock (Prompts) pBefore = Prompts.Count;
                var v = await Svc.RunVerifyBuildAsync(CancellationToken.None);
                int vPrompts; lock (Prompts) vPrompts = Prompts.Count - pBefore;
                var (r1b, n1b, _) = await Autonomous();
                Check("H7b", "that No holds for the verify-after-edit (null, unasked) but not for a run the user starts afterwards: it asks again; No again → still not run",
                    v == null && vPrompts == 0 && n1b == 1 && Runs() == 0 && !r1b.GoalMet && r1b.VerifyDeclined, $"verify={(v == null ? "null" : "ran")} prompts={vPrompts},{n1b} runs={Runs()}");

                // Approved → it runs; a later run over the same (now approved) edits does not ask.
                Answer = true;
                var (r2, n2, _) = await Autonomous();
                int runsAfterYes = Runs();
                var (r3, n3, _) = await Autonomous();
                Check("H7c", "control: approved → the command runs (marker) and the goal is met; the next run over the same edits runs without asking",
                    n2 == 1 && runsAfterYes == 1 && r2.GoalMet && !r2.VerifyDeclined && n3 == 0 && Runs() == 2 && r3.GoalMet,
                    $"prompts={n2},{n3} runs={runsAfterYes}→{Runs()} goal={r2.GoalMet},{r3.GoalMet}");

                // A No given DURING a run (e.g. by the agent turn's own verify-after-edit) stops it without a second question.
                Answer = false;
                await Gate("write_file", ("path", "scripts/helper.cmd"), ("content", "@echo ran>> auto-ran.txt\r\n@echo v2\r\n"));
                long mark = Svc.AutoBuildDeclineMark;
                lock (Prompts) pBefore = Prompts.Count;
                var inRun = await Svc.RunVerifyBuildAsync(CancellationToken.None);
                bool again = await Svc.ApproveAutomaticBuildAsync(verify, "perm.autoVerify.autonomous", mark);
                int inRunPrompts; lock (Prompts) inRunPrompts = Prompts.Count - pBefore;
                bool fresh = await Svc.ApproveAutomaticBuildAsync(verify, "perm.autoVerify.autonomous", Svc.AutoBuildDeclineMark);
                int freshPrompts; lock (Prompts) freshPrompts = Prompts.Count - pBefore - inRunPrompts;
                Check("H7d", "a No given during a run holds for that run (no second question); a run started after it asks again (control)",
                    inRun == null && !again && inRunPrompts == 1 && !fresh && freshPrompts == 1 && Runs() == 2,
                    $"inRun={(inRun == null ? "null" : "ran")} again={again} prompts={inRunPrompts},{freshPrompts} runs={Runs()}");

                // The user's own rule for run_build decides without a prompt (Deny → not run; Allow → runs).
                var deny = new PermissionRule { Pattern = "*", Scope = "execute", Action = PermAction.Deny, ToolName = "run_build" };
                perms.AddRule(deny);
                var (r4, n4, _) = await Autonomous();
                perms.RemoveRule(deny);
                var allow = new PermissionRule { Pattern = "*", Scope = "execute", Action = PermAction.Allow, ToolName = "run_build" };
                perms.AddRule(allow);
                var (r5, n5, _) = await Autonomous();
                perms.RemoveRule(allow);
                Check("H7e", "a Deny rule for run_build stops the run unasked; an Allow rule runs it unasked",
                    n4 == 0 && r4.VerifyDeclined && !r4.GoalMet && n5 == 0 && r5.GoalMet && Runs() == 3, $"prompts={n4},{n5} runs={Runs()} deny→{r4.VerifyDeclined} allow→{r5.GoalMet}");

                // The two new lines exist in Thai.
                var loc = provider.GetRequiredService<LocalizationService>();
                string lang = loc.CurrentLanguage;
                try
                {
                    loc.SetLanguage("th");
                    string th1 = loc.T("chat.auto.verifyDeclined"), th2 = loc.T("perm.autoVerify.autonomous");
                    loc.SetLanguage("en");
                    string en1 = loc.T("chat.auto.verifyDeclined"), en2 = loc.T("perm.autoVerify.autonomous");
                    Check("H7f", "the stop message and the prompt line have Thai values (not the English, not the raw key)",
                        th1 != en1 && th2 != en2 && th1.Contains("{0}") && th1.Any(c => c is >= '฀' and <= '๿') && th2.Any(c => c is >= '฀' and <= '๿'), th1);
                }
                finally { loc.SetLanguage(lang); }
            }
            finally
            {
                auto.OnPhase -= onPhase;
                (s.AutoVerifyCommand, s.AutoFixMaxIterations, s.AutoReviewEnabled) = keep;
                perms.SetAcceptEdits(null);
                Answer = true;
                if (File.Exists(marker)) File.Delete(marker);
            }
        });

        // ═══════════════════ named-pipe host ═══════════════════
        Console.WriteLine("\n── named-pipe JSON-RPC host (a private pipe name) ──");
        await PipeTests.Run(settings, Check, exeDir);

        // ═══════════════════ install marker ═══════════════════
        Console.WriteLine("\n── install marker ──");
        await Case("G1", () =>
        {
            string marker = Path.Combine(settings.DataRoot, "install.json");
            if (File.Exists(marker)) File.Delete(marker);
            bool wrote = McpHostService.WriteInstallMarker(settings.DataRoot, "cluadex-mcp", Path.Combine(settings.DataRoot, "mcp-host-token"));
            Check("G1", "a non-CluadeX host (this harness) writes no install.json", !McpHostService.IsCluadeXHost() && !wrote && !File.Exists(marker), $"isHost={McpHostService.IsCluadeXHost()} wrote={wrote}");
            Check("G1b", "host identity: CluadeX.exe yes; dotnet CluadeX.dll, chattest.exe and a renamed harness no",
                McpHostService.IsCluadeXHost(@"C:\Apps\CluadeX\CluadeX.exe", "CluadeX")
                && !McpHostService.IsCluadeXHost(@"C:\Program Files\dotnet\dotnet.exe", "CluadeX")
                && !McpHostService.IsCluadeXHost(@"E:\x\chattest.exe", "chattest")
                && !McpHostService.IsCluadeXHost(@"E:\x\CluadeX.exe", "mcptest"));
            // MINOR (review): App.OnStartup used the .exe check too, so "dotnet CluadeX.dll" started no app and hung.
            Check("G1d", "App.OnStartup's own check is the entry assembly: CluadeX (exe or 'dotnet CluadeX.dll') yes, chattest/mcptest no",
                McpHostService.IsCluadeXApp("CluadeX") && !McpHostService.IsCluadeXApp("chattest") && !McpHostService.IsCluadeXApp("mcptest") && !McpHostService.IsCluadeXApp());
            string tmp = Path.Combine(exeDir, "marker-test");
            if (Directory.Exists(tmp)) DeleteTree(tmp);
            bool real = McpHostService.WriteInstallMarker(tmp, "cluadex-mcp", Path.Combine(tmp, "mcp-host-token"), @"C:\Apps\CluadeX\CluadeX.exe", "CluadeX");
            string json = real ? File.ReadAllText(Path.Combine(tmp, "install.json")) : "";
            Check("G1c", "the app itself writes its marker into the data root it is given (exe, pipe, token file)", real && json.Contains("CluadeX.exe") && json.Contains("cluadex-mcp") && json.Contains("mcp-host-token"), json);
            DeleteTree(tmp);
            return Task.CompletedTask;
        });

        await mcp.StopServerAsync("stub");
        mcp.RemoveConfig("stub");
        await mcp.StopServerAsync("stub-brain");
        mcp.RemoveConfig("stub-brain");
        return Finish(profileDir, profileBefore);
    }

    static int Finish(string profileDir, Dictionary<string, DateTime?> before)
    {
        var after = ProfileSnapshot(profileDir);
        var changed = before.Where(kv => after.TryGetValue(kv.Key, out var a) && a != kv.Value).Select(kv => kv.Key).ToList();
        Check("Z1", "~/.cluadex was not written by this harness (install.json, mcp-host.log, mcp-host-token, mcp_servers.json, settings.json, permissions.json)", changed.Count == 0, string.Join(", ", changed));
        Console.WriteLine($"\n═══ {Pass} pass · {Fail} FAIL ═══");
        foreach (var p in Problems) Console.WriteLine("   " + p);
        return Fail == 0 ? 0 : 1;
    }

    static Dictionary<string, DateTime?> ProfileSnapshot(string dir)
    {
        var d = new Dictionary<string, DateTime?>();
        foreach (var name in new[] { "install.json", "mcp-host.log", "mcp-host-token", "mcp_servers.json", "settings.json", "permissions.json" })
        {
            string p = Path.Combine(dir, name);
            d[name] = File.Exists(p) ? File.GetLastWriteTimeUtc(p) : null;
        }
        return d;
    }

    internal static string Raw(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? "(undefined)" : e.GetRawText();

    /// <summary>FindMcpToolRule("x", "t", "mcp__x__t") with exactly these named rules in place (removed afterwards).</summary>
    static PermAction? RuleFor(PermissionService perms, params (string Tool, PermAction Action)[] rules)
    {
        var added = rules.Select(r => new PermissionRule { Pattern = "*", Scope = "*", Action = r.Action, ToolName = r.Tool }).ToList();
        foreach (var r in added) perms.AddRule(r);
        try { return perms.FindMcpToolRule("x", "t", "mcp__x__t"); }
        finally { foreach (var r in added) perms.RemoveRule(r); }
    }

    static void Cmd(string args)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c " + args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);
    }

    /// <summary>Delete a folder this harness made — a junction inside is unlinked, never followed.</summary>
    internal static void DeleteTree(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var sub in Directory.GetDirectories(dir))
            {
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) Directory.Delete(sub);
                else DeleteTree(sub);
            }
            foreach (var f in Directory.GetFiles(dir)) { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
            Directory.Delete(dir);
        }
        catch { }
    }
}

/// <summary>A policy that owns stub/brain_search only while <see cref="Active"/> — proves the classifier asks it first.</summary>
sealed class TestPolicy : IMcpToolPolicy
{
    public volatile bool Active;
    public McpCallRisk Risk = McpCallRisk.AlwaysAsk;
    public int Classified;
    public string Id => "mcptest";
    public bool Owns(McpToolKey tool) => Active && tool.ServerName == "stub" && tool.Leaf == "brain_search";
    public bool Hide(McpToolKey tool) => false;
    public string? RewriteDescription(McpToolKey tool, string description) => null;
    public McpCallDecision? Classify(McpToolKey tool, IReadOnlyDictionary<string, string> arguments)
    {
        Interlocked.Increment(ref Classified);
        return new McpCallDecision(Risk) { Detail = "POLICY-DETAIL" };
    }
    public void PostProcess(McpToolKey tool, ToolCall call, McpToolResult? raw, ToolResult result) { }
}

/// <summary>What the three tool APIs reject, checked on a sanitized schema (the harness's own rules, independent of
/// the code under test).</summary>
static class SchemaRules
{
    static readonly HashSet<string> Allowed = new() { "type", "properties", "items", "enum", "required", "description", "default" };
    static readonly HashSet<string> Types = new() { "string", "number", "integer", "boolean", "array", "object" };

    public static string? Violation(JsonElement schema, string path = "$")
    {
        if (schema.ValueKind != JsonValueKind.Object) return $"{path}: not an object";
        if (path == "$" && (!schema.TryGetProperty("type", out var rt) || rt.GetString() != "object" || !schema.TryGetProperty("properties", out _)))
            return "$: the root must be {type: object, properties: …}";
        foreach (var p in schema.EnumerateObject())
            if (!Allowed.Contains(p.Name)) return $"{path}: keyword '{p.Name}' is not in the portable set";
        if (!schema.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String || !Types.Contains(t.GetString()!))
            return $"{path}: 'type' must be one known string";
        string type = t.GetString()!;
        if (type == "array")
        {
            if (!schema.TryGetProperty("items", out var items)) return $"{path}: array without items";
            if (Violation(items, path + ".items") is { } v) return v;
        }
        if (schema.TryGetProperty("enum", out var en))
            foreach (var x in en.EnumerateArray())
                if (type == "string" && x.ValueKind != JsonValueKind.String || x.ValueKind == JsonValueKind.Null)
                    return $"{path}: enum value {x.GetRawText()} does not match type {type}";
        if (schema.TryGetProperty("properties", out var props))
        {
            if (type != "object") return $"{path}: properties on a {type}";
            foreach (var p in props.EnumerateObject())
            {
                // Anthropic: "Property keys should match pattern '^[a-zA-Z0-9_.-]{1,64}$'".
                if (!System.Text.RegularExpressions.Regex.IsMatch(p.Name, "^[a-zA-Z0-9_.-]{1,64}$")) return $"{path}: property name '{p.Name}' is refused";
                if (Violation(p.Value, $"{path}.{p.Name}") is { } v) return v;
            }
        }
        if (schema.TryGetProperty("required", out var req))
            foreach (var r in req.EnumerateArray())
                if (!(schema.TryGetProperty("properties", out var pp) && pp.TryGetProperty(r.GetString() ?? "", out _)))
                    return $"{path}: required '{r.GetString()}' is not a property";
        return null;
    }
}

/// <summary>
/// The sanitized schemas through the REAL Anthropic, OpenAI (Responses) and Gemini request builders, against a
/// localhost mock that refuses (400, like the APIs) any tool schema breaking <see cref="SchemaRules"/> and otherwise
/// answers with a marker — so a reply carrying the marker proves the request went to the mock and was accepted.
/// </summary>
static class ProviderWire
{
    static readonly List<(string Path, string Body)> Captured = new();
    const string Marker = "MCPTEST-MOCK-7731";

    public static async Task Run(List<ToolSchema> tools, Action<string, string, bool, string> check)
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(() => Serve(listener));
        string root = $"http://localhost:{port}";

        // The mock proves itself before any provider is pointed at it.
        using (var http = new System.Net.Http.HttpClient())
        {
            string hello = await http.GetStringAsync(root + "/marker");
            check("D6", "the localhost mock answers with its marker before any provider call", hello == Marker, hello);
            if (hello != Marker) return;
        }

        var svc = new SettingsService();   // portable — the harness's own settings file
        var s = svc.Settings;
        s.MaxTokens = 1024;
        s.ExtendedThinkingEnabled = false;
        var messages = new List<NativeMessage> { new() { Role = "user", Content = new() { new ContentBlock { Type = "text", Text = "use the tools" } } } };

        s.ProviderConfigs["Anthropic"] = new ProviderConfig { ApiKey = "sk-ant-mcptest", BaseUrl = root, SelectedModel = "claude-opus-5-5", WorkspaceId = "wrkspc_mcptest" };
        var an = await new AnthropicProvider(svc).ChatWithToolsAsync(messages, "sys", tools);
        Verify(check, "D6a", "Anthropic", "/v1/messages", an.TextContent, body => body.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("input_schema")));

        s.ProviderConfigs["OpenAI"] = new ProviderConfig { ApiKey = "sk-mcptest", BaseUrl = root + "/v1", SelectedModel = "gpt-6-sol" };
        var oa = await new OpenAiProvider(svc).ChatWithToolsAsync(messages, "sys", tools);
        Verify(check, "D6b", "OpenAI (Responses)", "/v1/responses", oa.TextContent, body => body.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("parameters")));

        s.ProviderConfigs["Gemini"] = new ProviderConfig { ApiKey = "AIza-mcptest", BaseUrl = root, SelectedModel = "gemini-3.8-flash" };
        var gm = await new GeminiProvider(svc).ChatWithToolsAsync(messages, "sys", tools);
        Verify(check, "D6c", "Gemini", "streamGenerateContent", gm.TextContent,
            body => body.GetProperty("tools")[0].GetProperty("functionDeclarations").EnumerateArray().Select(f => f.GetProperty("parametersJsonSchema")));
        listener.Stop();
    }

    static void Verify(Action<string, string, bool, string> check, string id, string name, string path, string? reply,
        Func<JsonElement, IEnumerable<JsonElement>> schemasOf)
    {
        string? body;
        lock (Captured) body = Captured.LastOrDefault(c => c.Path.Contains(path)).Body;
        if (body == null) { check(id, $"{name}: the request reached the mock", false, "nothing captured"); return; }
        using var doc = JsonDocument.Parse(body);
        var schemas = schemasOf(doc.RootElement).ToList();
        string? violation = schemas.Select(sc => SchemaRules.Violation(sc)).FirstOrDefault(v => v != null);
        bool structure = schemas.Count == 2 && schemas[0].GetProperty("properties").GetProperty("tags").GetProperty("items").TryGetProperty("enum", out _)
                         && schemas[0].GetProperty("properties").GetProperty("options").GetProperty("properties").TryGetProperty("flags", out _)
                         && schemas[1].GetProperty("properties").TryGetProperty("after", out _);
        check(id, $"{name}: both MCP tools go out with items, enum and nested properties, the request is accepted (marker reply)",
            reply?.Contains(Marker) == true && violation == null && structure, $"reply='{reply}' violation={violation} structure={structure}");
    }

    static async Task Serve(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            try
            {
                string path = ctx.Request.Url!.PathAndQuery;
                string body = ctx.Request.HasEntityBody ? await new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEndAsync() : "";
                lock (Captured) Captured.Add((path, body));
                if (path.StartsWith("/marker")) { await Write(ctx, 200, "text/plain", Marker); continue; }

                // Refuse like the APIs would: any tool schema outside the portable set is a 400.
                string? bad = null;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    IEnumerable<JsonElement> schemas = path.Contains("/v1/messages")
                        ? doc.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("input_schema"))
                        : path.Contains("/v1/responses")
                            ? doc.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("parameters"))
                            : doc.RootElement.GetProperty("tools")[0].GetProperty("functionDeclarations").EnumerateArray().Select(f => f.GetProperty("parametersJsonSchema"));
                    bad = schemas.Select(sc => SchemaRules.Violation(sc)).FirstOrDefault(v => v != null);
                }
                catch (Exception ex) { bad = "unreadable request: " + ex.Message; }
                if (bad != null) { await Write(ctx, 400, "application/json", "{\"error\":{\"message\":" + JsonSerializer.Serialize("Invalid schema: " + bad) + "}}"); continue; }

                if (path.Contains("/v1/messages"))
                    await Write(ctx, 200, "text/event-stream",
                        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m1\",\"usage\":{\"input_tokens\":3,\"output_tokens\":0}}}\n\n" +
                        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
                        $"event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{Marker}\"}}}}\n\n" +
                        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
                        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n" +
                        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
                else if (path.Contains("/v1/responses"))
                    await Write(ctx, 200, "text/event-stream",
                        $"data: {{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"{Marker}\"}}\n\n" +
                        $"data: {{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{{\"type\":\"message\",\"id\":\"msg_1\",\"role\":\"assistant\",\"content\":[{{\"type\":\"output_text\",\"text\":\"{Marker}\"}}]}}}}\n\n" +
                        "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"usage\":{\"input_tokens\":3,\"output_tokens\":1}}}\n\n");
                else if (path.Contains("streamGenerateContent"))
                    await Write(ctx, 200, "text/event-stream",
                        $"data: {{\"candidates\":[{{\"content\":{{\"role\":\"model\",\"parts\":[{{\"text\":\"{Marker}\"}}]}},\"finishReason\":\"STOP\"}}],\"usageMetadata\":{{\"promptTokenCount\":3,\"candidatesTokenCount\":1}}}}\n\n");
                else
                    await Write(ctx, 404, "application/json", "{\"error\":{\"message\":\"no such path in the mcptest mock\"}}");
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }
    }

    static async Task Write(HttpListenerContext ctx, int status, string type, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }
}

/// <summary>The pipe host on a private pipe name, with a test dispatcher that can hang until cancelled.</summary>
static class PipeTests
{
    static TaskCompletionSource SlowStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    static TaskCompletionSource SlowCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    static readonly List<string> Marks = new();

    public static async Task Run(SettingsService settings, Action<string, string, bool, string> check, string exeDir)
    {
        string pipe = "cluadex-mcptest-" + Guid.NewGuid().ToString("N")[..10];
        using var host = new McpHostService(settings, pipe);
        host.ToolDispatcher = async (inv, ct) =>
        {
            if (inv.ToolName == "slow")
            {
                SlowStarted.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { SlowCancelled.TrySetResult(); throw; }
            }
            if (inv.ToolName == "mark" && inv.Arguments.ValueKind == JsonValueKind.Object && inv.Arguments.TryGetProperty("n", out var n))
                lock (Marks) Marks.Add(n.GetRawText());
            return new McpToolResult { Content = { new McpContentItem { Type = "text", Text = "echo " + inv.ToolName } } };
        };
        await host.StartAsync();
        check("F0", "the host keeps its token and log in the portable data root (never ~/.cluadex)",
            host.DataDirectory.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(host.DataDirectory, "mcp-host-token")), host.DataDirectory);

        // F1 — string and number ids are echoed exactly; a wrong token is -32000.
        using (var c = await Client.Open(pipe))
        {
            var bad = await c.Ask("{\"jsonrpc\":\"2.0\",\"id\":\"auth-1\",\"method\":\"initialize\",\"params\":{\"authToken\":\"wrong-token-xxxxxxxxxxxxxxxxxxxxxxxxxxx\"}}");
            check("F1", "a wrong token gets -32000 (and its string id back)", bad?.Code == -32000 && bad.IdRaw == "\"auth-1\"", bad?.Raw ?? "no reply");
            var unauth = await c.Ask("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/list\"}");
            check("F1b", "tools/list before a good initialize is refused (-32000)", unauth?.Code == -32000, unauth?.Raw ?? "no reply");
            var ok = await c.Ask(Init("abc-1", host.AuthToken));
            check("F2", "a string id is echoed as the same string", ok != null && ok.IdRaw == "\"abc-1\"" && ok.Code == null, ok?.Raw ?? "no reply");
            var num = await c.Ask("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\"}");
            check("F2b", "a number id is echoed as the same number", num?.IdRaw == "7", num?.Raw ?? "no reply");
            var list = await c.Ask("{\"jsonrpc\":\"2.0\",\"id\":\"l\",\"method\":\"tools/list\"}");
            check("F2c", "the catalogue describes what is returned (no 'diff plus transcript'; isError; maxSteps + acceptEdits)",
                list != null && !list.Raw.Contains("diff plus") && list.Raw.Contains("isError") && list.Raw.Contains("maxSteps") && list.Raw.Contains("acceptEdits"), Program_Trunc(list?.Raw));
            check("F2d", "initialize documents the one-request-at-a-time model", ok?.Raw.Contains("one at a time") == true, Program_Trunc(ok?.Raw));

            // F3 — a notification gets NO reply; a flood of them neither, and the next request is answered first.
            await c.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            for (int i = 0; i < 500; i++) await c.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"n\":" + i + "}}");
            var sw = Stopwatch.StartNew();
            var after = await c.Ask("{\"jsonrpc\":\"2.0\",\"id\":\"p1\",\"method\":\"ping\"}");
            check("F3", "notifications get no reply — 501 of them, then a ping: the first line back is the ping's", after?.IdRaw == "\"p1\"" && after.Code == null && sw.ElapsedMilliseconds < 5000, $"{after?.Raw} in {sw.ElapsedMilliseconds} ms");
            var garbage = await c.Ask("{not json");
            check("F3b", "malformed JSON → -32700 with id null", garbage?.Code == -32700 && garbage.IdRaw == "null", garbage?.Raw ?? "no reply");
        }

        // F4 — closing the pipe cancels the run that is in flight.
        using (var c = await Client.Open(pipe))
        {
            await c.Ask(Init(1, host.AuthToken));
            await c.Send("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"slow\",\"arguments\":{}}}");
            bool began = await Task.WhenAny(SlowStarted.Task, Task.Delay(5000)) == SlowStarted.Task;
            c.Dispose();   // the caller goes away mid-run
            bool cancelled = await Task.WhenAny(SlowCancelled.Task, Task.Delay(5000)) == SlowCancelled.Task;
            check("F4", "a client disconnecting mid-run cancels the run's token", began && cancelled, $"began={began} cancelled={cancelled}");
        }

        // F5 — notifications/cancelled stops the request and no response is sent for it.
        SlowStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SlowCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var c = await Client.Open(pipe))
        {
            await c.Ask(Init("i", host.AuthToken));
            await c.Send("{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"tools/call\",\"params\":{\"name\":\"slow\",\"arguments\":{}}}");
            await Task.WhenAny(SlowStarted.Task, Task.Delay(5000));
            await c.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":42,\"reason\":\"test\"}}");
            bool cancelled = await Task.WhenAny(SlowCancelled.Task, Task.Delay(5000)) == SlowCancelled.Task;
            var next = await c.Ask("{\"jsonrpc\":\"2.0\",\"id\":43,\"method\":\"ping\"}");
            check("F5", "notifications/cancelled stops the running call, and the next line back is the ping's (none for the cancelled one)", cancelled && next?.IdRaw == "43", $"cancelled={cancelled} next={next?.Raw}");
        }

        // F7 — MINOR (review): a cancellation for a request still WAITING in the queue was ignored; it ran later.
        SlowStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SlowCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Marks) Marks.Clear();
        using (var c = await Client.Open(pipe))
        {
            await c.Ask(Init("q", host.AuthToken));
            await c.Send("{\"jsonrpc\":\"2.0\",\"id\":70,\"method\":\"tools/call\",\"params\":{\"name\":\"slow\",\"arguments\":{}}}");
            await Task.WhenAny(SlowStarted.Task, Task.Delay(5000));
            await c.Send("{\"jsonrpc\":\"2.0\",\"id\":71,\"method\":\"tools/call\",\"params\":{\"name\":\"mark\",\"arguments\":{\"n\":71}}}");
            await c.Send("{\"jsonrpc\":\"2.0\",\"id\":\"72\",\"method\":\"tools/call\",\"params\":{\"name\":\"mark\",\"arguments\":{\"n\":72}}}");
            await Task.Delay(200);   // both are queued behind the slow one
            await c.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":71}}");
            await c.Send("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":70}}");
            var first = await c.Ask("{\"jsonrpc\":\"2.0\",\"id\":73,\"method\":\"ping\"}");
            await Task.Delay(300);
            string marks;
            lock (Marks) marks = string.Join(",", Marks);
            check("F7", "a request cancelled while it waited in the queue never runs and gets no reply (71); the one not cancelled (\"72\") does — its reply comes first",
                first?.IdRaw == "\"72\"" && marks == "72", $"first reply={first?.Raw} marks=[{marks}]");
        }

        // F6 — five wrong tokens close the connection.
        using (var c = await Client.Open(pipe))
        {
            for (int i = 0; i < McpHostService.MaxFailedAuth; i++)
                await c.Ask("{\"jsonrpc\":\"2.0\",\"id\":" + i + ",\"method\":\"initialize\",\"params\":{\"authToken\":\"nope\"}}");
            var afterClose = await c.Ask(Init(99, host.AuthToken), 2000);
            check("F6", $"{McpHostService.MaxFailedAuth} wrong tokens close the connection (a right one after that gets nothing)", afterClose == null, afterClose?.Raw ?? "closed");
        }
        host.Stop();
    }

    static string Program_Trunc(string? s) => s == null ? "(none)" : (s.Length <= 500 ? s : s[..500] + "…");

    static string Init(object id, string token) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + JsonSerializer.Serialize(id) + ",\"method\":\"initialize\",\"params\":{\"authToken\":" + JsonSerializer.Serialize(token) + ",\"protocolVersion\":\"2025-03-26\",\"clientInfo\":{\"name\":\"mcptest\",\"version\":\"1\"}}}";

    sealed record Reply(string Raw, string IdRaw, int? Code);

    sealed class Client : IDisposable
    {
        readonly NamedPipeClientStream _pipe;
        readonly StreamReader _reader;
        readonly StreamWriter _writer;
        Client(NamedPipeClientStream p)
        {
            _pipe = p;
            _reader = new StreamReader(p, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            _writer = new StreamWriter(p, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        }

        public static async Task<Client> Open(string name)
        {
            var p = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await p.ConnectAsync(5000);
            return new Client(p);
        }

        public async Task Send(string line)
        {
            try { await _writer.WriteLineAsync(line); } catch { }
        }

        public async Task<Reply?> Ask(string line, int timeoutMs = 5000)
        {
            await Send(line);
            var read = _reader.ReadLineAsync();
            if (await Task.WhenAny(read, Task.Delay(timeoutMs)) != read) return null;
            string? text;
            try { text = await read; } catch { return null; }
            if (text == null) return null;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            int? code = root.TryGetProperty("error", out var e) ? e.GetProperty("code").GetInt32() : null;
            return new Reply(text, root.TryGetProperty("id", out var id) ? id.GetRawText() : "(missing)", code);
        }

        public void Dispose()
        {
            try { _writer.Dispose(); } catch { }
            try { _reader.Dispose(); } catch { }
            try { _pipe.Dispose(); } catch { }
        }
    }
}

/// <summary>This exe as a stdio MCP server: newline-delimited JSON-RPC, the tools the contract needs, an optional
/// slow initialize (--init-delay N seconds). tools/call echoes the arguments it really received.</summary>
static class StubMcp
{
    const string ComplexSchema = """
    {
      "$schema": "http://json-schema.org/draft-07/schema#",
      "type": "object",
      "additionalProperties": false,
      "$defs": { "Target": { "type": "object", "properties": { "url": { "type": "string", "format": "uri" }, "tab": { "type": "integer" } }, "required": ["url"] } },
      "properties": {
        "tags": { "type": "array", "items": { "type": "string", "enum": ["red", "green", "blue"] }, "description": "Colours" },
        "mode": { "type": "string", "enum": ["fast", "safe"], "default": "safe" },
        "options": { "type": "object", "properties": {
            "depth": { "type": "integer", "minimum": 1 },
            "flags": { "type": "array", "items": { "type": "boolean" } },
            "inner": { "type": "object", "properties": { "name": { "type": "string" } }, "required": ["name", "ghost"] } },
          "required": ["depth"] },
        "points": { "type": "array", "items": { "type": "array" } },
        "target": { "$ref": "#/$defs/Target" },
        "choice": { "anyOf": [ { "type": "null" }, { "type": "string", "enum": ["a", "b"] } ], "description": "A or B" },
        "loose": { "type": "array" },
        "self": { "$ref": "#" },
        "pick": { "type": "object", "properties": { "a": { "type": "string" }, "b": { "type": "string" } }, "anyOf": [ { "required": ["a"] }, { "required": ["b"] } ] },
        "slug": { "type": "string", "minLength": 1, "maxLength": 64, "pattern": "^[a-z-]+$" },
        "bad key!": { "type": "string" }
      },
      "required": ["tags", "mode", "nonexistent"]
    }
    """;

    const string NullableSchema = """
    { "type": "object", "properties": {
        "note": { "type": ["string", "null"], "enum": ["2026", null, "other"], "description": "optional note" },
        "count": { "type": "integer" },
        "after": { "type": "string" } },
      "required": ["count"] }
    """;

    const string TypedSchema = """
    { "type": "object", "$defs": { "Label": { "type": "string" } }, "properties": {
        "note": { "anyOf": [ { "type": "string" }, { "type": "null" } ] },
        "label": { "$ref": "#/$defs/Label" },
        "plain": { "type": "string" },
        "count": { "anyOf": [ { "type": "integer" }, { "type": "null" } ] },
        "flag": { "oneOf": [ { "type": "null" }, { "type": "boolean" } ] } } }
    """;

    /// <summary>A "schema bomb": one definition per level, each referenced <paramref name="fan"/> times by the level
    /// above — tiny as text, fan^depth nodes when every $ref is inlined.</summary>
    public static string BombSchema(int fan, int depth)
    {
        var defs = new StringBuilder();
        for (int level = 0; level < depth; level++)
        {
            defs.Append(level == 0 ? "" : ",").Append($"\"L{level}\":{{\"type\":\"object\",\"properties\":{{");
            for (int f = 0; f < fan; f++)
                defs.Append(f == 0 ? "" : ",").Append($"\"p{f}\":{{\"$ref\":\"#/$defs/L{level + 1}\"}}");
            defs.Append("}}");
        }
        defs.Append($",\"L{depth}\":{{\"type\":\"string\",\"description\":\"leaf\"}}");
        return "{\"type\":\"object\",\"$defs\":{" + defs + "},\"properties\":{\"root\":{\"$ref\":\"#/$defs/L0\"}}}";
    }

    public static int Run(string[] args)
    {
        int delay = 0;
        int i = Array.IndexOf(args, "--init-delay");
        if (i >= 0 && i + 1 < args.Length) int.TryParse(args[i + 1], out delay);
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
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
                string result;
                switch (method)
                {
                    case "initialize":
                        if (delay > 0) Thread.Sleep(delay * 1000);
                        result = "{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"mcptest-stub\",\"version\":\"1.0\"}}";
                        break;
                    case "tools/list":
                        result = "{\"tools\":[" + string.Join(",",
                            Tool("stub_marker", "mcptest stub marker", "{\"type\":\"object\",\"properties\":{}}"),
                            Tool("brain_search", "Search the brain.", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}"),
                            Tool("browser_click", "Click an element.", "{\"type\":\"object\",\"properties\":{\"element\":{\"type\":\"string\"},\"ref\":{\"type\":\"string\"}},\"required\":[\"element\"]}"),
                            Tool("chrome__navigate", "Go to a URL.", "{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\"}}}"),
                            Tool("chrome__read_page", "Read the page.", "{\"type\":\"object\",\"properties\":{}}"),
                            Tool("complex_tool", "Arrays, enums, nested objects, refs.", ComplexSchema),
                            Tool("nullable_tool", "A type array first.", NullableSchema),
                            Tool("typed_tool", "anyOf / $ref strings.", TypedSchema),
                            Tool("bomb_tool", "A schema whose refs multiply.", BombSchema(7, 6))) + "]}";
                        break;
                    case "tools/call":
                        string name = root.GetProperty("params").GetProperty("name").GetString() ?? "";
                        string a = root.GetProperty("params").TryGetProperty("arguments", out var ar) ? ar.GetRawText() : "{}";
                        result = "{\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize($"STUB-OK {name} {a}") + "}]}";
                        break;
                    default:
                        result = "{}";
                        break;
                }
                stdout.WriteLine($"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"result\":{result}}}");
            }
        }
        return 0;
    }

    static string Tool(string name, string description, string schema)
    {
        // Compact: stdio MCP is one JSON message per line, so the schema must not carry the literal's newlines.
        using var d = JsonDocument.Parse(schema);
        return "{\"name\":" + JsonSerializer.Serialize(name) + ",\"description\":" + JsonSerializer.Serialize(description) + ",\"inputSchema\":" + JsonSerializer.Serialize(d.RootElement) + "}";
    }
}
