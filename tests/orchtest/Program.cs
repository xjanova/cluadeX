using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace OrchTest;

/// <summary>
/// The agent-orchestration contract (docs/agent-orchestration.md), against the built CluadeX.dll: the plan tool through
/// the real AgentToolService (native names, the [ACTION:] parser, the CLI bridge), the workflow engine and service with
/// scripted sub-agents (fan-out, pipeline with no barrier, majority verify, schema repair, budget, machine slots, Stop,
/// Resume from the cache, an app restart mid-workflow, a double Resume), the opt-in gate, wrap_up's box.
///
/// No model, no network, no GPU: every sub-agent answer is scripted here. portable.txt keeps settings next to this exe.
///   dotnet build CluadeX/CluadeX.csproj   then   cd tests/orchtest && dotnet run
/// </summary>
internal static partial class Program
{
    static int Pass, Fail;
    static readonly StringBuilder Report = new();
    static readonly List<string> Failures = new();
    static AgentToolService Svc;
    static LocalizationService LocSvc;
    static string Root;

    [STAThread]
    static int Main()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        int rc;
        try { rc = Run().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log("FATAL " + ex); rc = 2; }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "orch-report.md"), Report.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"\nreport: {Path.Combine(AppContext.BaseDirectory, "orch-report.md")}");
        try { Directory.Delete(Root, true); } catch { }
        return rc;
    }

    static void Log(string line) { Console.WriteLine(line); Report.AppendLine(line); }

    static void Check(string name, bool ok, string detail = "")
    {
        detail ??= "";
        if (ok) Pass++; else { Fail++; Failures.Add(name + (detail.Length > 0 ? " — " + detail : "")); }
        Log($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 && !ok ? "   [" + Cut(detail, 600) + "]" : "")}");
    }

    static string Cut(string s, int n) => s == null ? "" : s.Length <= n ? s : s[..n] + "…";

    static async Task Case(string id, Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex) { Check(id + " (threw)", false, ex.GetType().Name + ": " + ex.Message + " @ " + ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()); }
    }

    static async Task<bool> WaitUntil(Func<bool> cond, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            await Task.Delay(15);
        }
        return cond();
    }

    // ═══════════════════════════════ scripted sub-agents ═══════════════════════════════

    /// <summary>A sub-agent runner whose answers the test writes. Counts calls, the most running at once, cancellations.</summary>
    sealed class Mock
    {
        public int Calls, Running, MaxRunning, Cancelled;
        public readonly ConcurrentQueue<WorkflowAgentCall> Seen = new();
        public readonly ConcurrentDictionary<string, (DateTime Start, DateTime End)> Times = new();
        public Func<WorkflowAgentCall, CancellationToken, Task<string>> Answer = (c, _) => Task.FromResult("answer of " + c.Label);

        public async Task<WorkflowAgentResult> Run(WorkflowAgentCall c, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Seen.Enqueue(c);
            int now = Interlocked.Increment(ref Running);
            lock (this) MaxRunning = Math.Max(MaxRunning, now);
            var start = DateTime.Now;
            try { return new WorkflowAgentResult(await Answer(c, ct), 0.01m); }
            catch (OperationCanceledException) { Interlocked.Increment(ref Cancelled); throw; }
            finally
            {
                Interlocked.Decrement(ref Running);
                Times[c.Label] = (start, DateTime.Now);
            }
        }
    }

    /// <summary>A service that writes nothing (or into <paramref name="dir"/>), with counted machine slots.</summary>
    static (WorkflowService Svc, Mock Mock, int[] Slots) NewService(string dir = null, SemaphoreSlim machine = null)
    {
        var mock = new Mock();
        var slots = new int[2];   // [0] entered, [1] left
        var svc = new WorkflowService(dir)
        {
            Runner = mock.Run,
            MachineCap = () => 8,
            EnterSlot = async ct => { if (machine != null) await machine.WaitAsync(ct); Interlocked.Increment(ref slots[0]); },
            LeaveSlot = () => { Interlocked.Increment(ref slots[1]); machine?.Release(); },
        };
        return (svc, mock, slots);
    }

    /// <summary>The JSON the last <see cref="PlanOf"/> parsed (what the tool passes on, and what Resume reads back).</summary>
    static string LastJson;

    static WorkflowPlan PlanOf(string json)
    {
        LastJson = json;
        var plan = WorkflowPlan.Parse(json, out var err);
        if (plan == null) throw new Exception("plan did not parse: " + err);
        return plan;
    }

    static string Agents(int n, string prefix = "a") =>
        string.Join(",", Enumerable.Range(1, n).Select(i => $"{{\"label\":\"{prefix}{i}\",\"prompt\":\"task {prefix}{i}\"}}"));

    // ═══════════════════════════════ run ═══════════════════════════════

    static async Task<int> Run()
    {
        Log("# Agent orchestration harness\n");
        var services = new ServiceCollection();
        typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [services]);
        var sp = services.BuildServiceProvider();
        var settings = sp.GetRequiredService<SettingsService>();
        Check("setup: portable data root (never the user's ~/.cluadex)",
            settings.DataRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase), settings.DataRoot);
        if (!settings.DataRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)) return 3;
        var loc = LocSvc = sp.GetRequiredService<LocalizationService>();
        LocalizedResources.Instance.Initialize(loc);
        loc.SetLanguage("en");
        Svc = sp.GetRequiredService<AgentToolService>();
        Root = Path.Combine(Path.GetTempPath(), "cluadex-orchtest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        WorkflowGate.Load(null);   // memory only: the owner's real ultra-chats.json is never read or written

        Log("## Plan tool (piece 1)");
        await Case("P1", TestPlanOps);
        await Case("P2", TestPlanThroughToolService);
        await Case("P3", TestPlanLegacyParse);
        await Case("P4", TestPlanThroughCliBridge);
        await Case("P5", TestRunningLine);

        Log("\n## Workflow plan and engine (piece 2)");
        await Case("W1", TestPlanChecks);
        await Case("W2", TestFanOut);
        await Case("W3", TestPipelineNoBarrier);
        await Case("W4", TestVerifyMajority);
        await Case("W5", TestSchemaRepair);
        await Case("W6", TestBudget);
        await Case("W7", TestMachineSlots);
        await Case("W8", TestStopDuringFanOut);
        await Case("W9", TestResumeFromCache);
        await Case("W10", TestRestartMidWorkflow);
        await Case("W11", TestDoubleResume);
        await Case("W12", TestEstimate);
        await Case("W13", TestHardCap);

        Log("\n## Isolation, gate and merge (agents in their own git worktrees)");
        await Case("I1", RealShell(TestIsolatedCleanMerge));
        await Case("I2", TestIsolatedConflict);
        await Case("I3", RealShell(TestIsolatedRedGate));
        await Case("I4", TestIsolatedStop);
        await Case("I5", TestIsolatedCrashSweep);
        await Case("I6", TestIsolatedResume);
        await Case("I7", TestIsolationPlanChecks);
        await Case("I8", TestIsolationThroughTool);

        Log("\n## Opt-in gate and the workflow tool");
        await Case("G1", TestGate);
        await Case("G2", TestWorkflowTool);

        Log("\n## wrap_up (piece 6)");
        await Case("N1", TestNextStepArgs);
        await Case("N2", TestWrapUpTool);

        Log($"\n## Result: {Pass} pass / {Fail} FAIL");
        foreach (var f in Failures) Log("  - " + f);
        sp.Dispose();
        return Fail == 0 ? 0 : 1;
    }

    // ═══════════════════════════════ piece 1: the plan ═══════════════════════════════

    static List<TodoItem> Items(params string[] statuses) =>
        statuses.Select((s, i) => new TodoItem { Content = "step " + (i + 1), Status = s }).ToList();

    static string Statuses(IEnumerable<TodoItem> items) => string.Join(",", items.Select(t => t.Status));

    static Task TestPlanOps()
    {
        var a = Items("in_progress", "pending", "pending");
        Check("P1a advance: the step in progress is done, the next pending starts",
            PlanOps.Apply(a, true, [], null) == null && Statuses(a) == "completed,in_progress,pending", Statuses(a));
        var b = Items("in_progress", "pending", "pending", "pending");
        Check("P1b done [1,2] + start 4: one step in progress, the others done or pending",
            PlanOps.Apply(b, false, [1, 2], 4) == null && Statuses(b) == "completed,completed,pending,in_progress", Statuses(b));
        var c = Items("in_progress", "pending");
        string bad = PlanOps.Apply(c, false, [5], null);
        Check("P1c a step that does not exist is refused and nothing changes",
            bad != null && bad.Contains("no step 5") && Statuses(c) == "in_progress,pending", bad);
        Check("P1d no plan yet: the step operations ask for the whole list first",
            PlanOps.Apply(new List<TodoItem>(), true, [], null)?.Contains("todos") == true);
        Check("P1e numbers read the ways a model writes them",
            string.Join(",", PlanOps.Numbers("[1, 3]")) == "1,3" && string.Join(",", PlanOps.Numbers("2")) == "2"
            && PlanOps.Numbers("x").Count == 0 && PlanOps.IsTrue("true") && PlanOps.IsTrue("1") && !PlanOps.IsTrue("false"));
        return Task.CompletedTask;
    }

    static async Task<ToolResult> T(string tool, params (string k, string v)[] args)
    {
        var type = Svc.ResolveToolTypePublic(tool) ?? throw new Exception("tool name did not resolve: " + tool);
        var call = new ToolCall { Type = type, ToolName = tool, Arguments = args.ToDictionary(a => a.k, a => a.v) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await Svc.ExecuteToolAsync(call, cts.Token);
    }

    static string Seen(ToolResult r) => string.Join("\n", new[] { r.Output, r.Error }.Where(s => !string.IsNullOrEmpty(s)));

    static async Task TestPlanThroughToolService()
    {
        Check("P2a the plan answers to update_plan / plan_update / todo_write (one tool, one plan)",
            new[] { "update_plan", "plan_update", "todo_write", "updateplan" }.All(n => Svc.ResolveToolTypePublic(n) == ToolType.TodoWrite));
        var scope = new AgentRunScope("orch-p2", Root);
        AgentRunScope.Enter(scope);
        int changed = 0;
        Action onChange = () => changed++;
        Svc.TodoChanged += onChange;
        try
        {
            var r1 = await T("update_plan", ("todos", """[{"content":"Read","status":"in_progress"},{"content":"Fix"},{"content":"Test"}]"""));
            Check("P2b the whole list in one call", r1.Success && scope.Todos.Count == 3 && Statuses(scope.Todos) == "in_progress,pending,pending", Seen(r1));
            var r2 = await T("update_plan", ("advance", "true"));
            Check("P2c advance: step 1 done, step 2 now, the answer names it",
                r2.Success && Statuses(scope.Todos) == "completed,in_progress,pending" && r2.Output.Contains("now: Fix"), Seen(r2));
            var r3 = await T("update_plan", ("done", "[2]"), ("start", "3"));
            Check("P2d done [2] + start 3", r3.Success && Statuses(scope.Todos) == "completed,completed,in_progress", Seen(r3));
            var r4 = await T("update_plan", ("done", "9"));
            Check("P2e a wrong step number fails with the reason, the plan is unchanged",
                !r4.Success && Seen(r4).Contains("no step 9") && Statuses(scope.Todos) == "completed,completed,in_progress", Seen(r4));
            var r5 = await T("todo_write", ("todos", """[{"content":"A","status":"completed"},{"content":"B"}]"""), ("start", "2"));
            Check("P2f the whole list and a step operation together", r5.Success && Statuses(scope.Todos) == "completed,in_progress", Seen(r5));
            Check("P2g every change reaches the plan card and bar (TodoChanged), the refused one too or not at all — never a stale view",
                changed >= 4, $"changed={changed}");
            var schema = Svc.BuildNativeToolSchemas().FirstOrDefault(s => s.Name == "todo_write");
            var props = schema?.InputSchema.GetProperty("properties");
            Check("P2h the native schema offers advance / done / start",
                props is { } p && p.TryGetProperty("advance", out _) && p.TryGetProperty("done", out var d)
                && d.GetProperty("items").GetProperty("type").GetString() == "integer" && p.TryGetProperty("start", out _));
        }
        finally { Svc.TodoChanged -= onChange; AgentRunScope.Enter(null); }
    }

    static Task TestPlanLegacyParse()
    {
        var calls = Svc.ParseToolCalls("I will move on.\n[ACTION: update_plan]\nadvance: true\n[/ACTION]");
        Check("P3a [ACTION: update_plan] parses to the plan tool with advance (local models' text protocol)",
            calls.Count == 1 && calls[0].Type == ToolType.TodoWrite && calls[0].GetArg("advance") == "true",
            string.Join("; ", calls.Select(c => c.Type + " " + string.Join(",", c.Arguments.Select(kv => kv.Key + "=" + kv.Value)))));
        var whole = Svc.ParseToolCalls("[ACTION: update_plan]\ntodos: [{\"content\":\"One\",\"status\":\"in_progress\"}]\n[/ACTION]");
        Check("P3b and the whole list", whole.Count == 1 && whole[0].GetArg("todos").Contains("\"One\""));
        var wrap = Svc.ParseToolCalls("[ACTION: wrap_up]\nrecap: Fixed it; the build passes.\nask: Run the live test now?\nactions: [\"Yes, run it\", \"Not now\"]\n[/ACTION]");
        Check("P3c [ACTION: wrap_up] (the prompt's own example) parses to wrap_up with its fields",
            wrap.Count == 1 && wrap[0].Type == ToolType.WrapUp && wrap[0].GetArg("ask") == "Run the live test now?"
            && NextStep.Labels(wrap[0].GetArg("actions")).Count == 2,
            string.Join("; ", wrap.Select(c => c.Type + " " + string.Join(",", c.Arguments.Select(kv => kv.Key + "=" + kv.Value)))));
        return Task.CompletedTask;
    }

    static async Task TestPlanThroughCliBridge()
    {
        Check("P4a CLI agents are offered update_plan, workflow and wrap_up (mcp__cluadex__*)",
            new[] { "update_plan", "workflow", "wrap_up" }.All(n => ChatToolBridge.QualifiedToolNames.Contains("mcp__cluadex__" + n)));
        var list = JsonSerializer.SerializeToElement(ChatToolBridge.ToolList());
        Check("P4b tools/list carries their schemas",
            new[] { "update_plan", "workflow", "wrap_up" }.All(n => list.EnumerateArray().Any(t => t.GetProperty("name").GetString() == n)));
        var scope = new AgentRunScope("orch-p4", Root);
        using var turn = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        using var ticket = ChatToolBridge.Register(scope, (call, ct) => Svc.ExecuteToolAsync(call, ct), Root, turn.Token)!;
        string Req(string tool, object args) => JsonSerializer.Serialize(new { token = ticket.Token, tool, args });
        var a = await ChatToolBridge.HandleAsync(Req("update_plan", new { todos = new object[] { new { content = "A", status = "in_progress" }, new { content = "B" } } }));
        Check("P4c a CLI agent's update_plan writes ITS chat's plan", a.Ok && Statuses(scope.Todos) == "in_progress,pending", a.Text);
        var b = await ChatToolBridge.HandleAsync(Req("update_plan", new { advance = true }));
        Check("P4d and steps it along", b.Ok && Statuses(scope.Todos) == "completed,in_progress", b.Text);
        var c = await ChatToolBridge.HandleAsync(Req("workflow", new { name = "x", phases = new object[] { new { title = "t", agents = new object[] { new { label = "l", prompt = "p" } } } } }));
        Check("P4e a CLI agent's workflow is refused while the chat has not opted in, with the reason", !c.Ok && c.Text.Contains("Ultra"), c.Text);
        Check("P4f CLI agents are offered add_code_comments, with its schema",
            ChatToolBridge.QualifiedToolNames.Contains("mcp__cluadex__add_code_comments")
            && list.EnumerateArray().Any(t => t.GetProperty("name").GetString() == "add_code_comments"));
        scope.AddUserNote("also do C");
        var d = await ChatToolBridge.HandleAsync(Req("update_plan", new { advance = true }));
        Check("P4g what the user typed mid-run rides on the CLI agent's next CluadeX tool reply",
            d.Ok && d.Text.Contains("also do C") && !scope.HasUserNotes, d.Text);
        var e = await ChatToolBridge.HandleAsync(Req("update_plan", new { advance = true }));
        Check("P4h ...once", e.Ok && !e.Text.Contains("also do C"), e.Text);
    }

    static Task TestRunningLine()
    {
        string L(string k) => LocalizedResources.Instance[k];
        string chat = "orch-p5";
        RunActivity.SubAgentStarted(chat, "s1");
        RunActivity.SubAgentStarted(chat, "s2");
        RunActivity.SubAgentStarted(chat, "s2");   // a repeated start counts once
        RunActivity.WorkflowStarted(chat, "wf-1");
        string line = RunActivity.Describe(RunActivity.Of(chat), L);
        Check("P5a the active step's line counts sub-agents and workflows once each", line == "Running: 2 sub-agents · 1 workflow", line);
        RunActivity.ClearRun(chat);
        line = RunActivity.Describe(RunActivity.Of(chat), L);
        Check("P5b the run ended: its sub-agents go, a workflow (it can outlive the run from its card) stays", line == "Running: 1 workflow", line);
        RunActivity.WorkflowEnded(chat, "wf-1");
        Check("P5c nothing runs: no line", RunActivity.Describe(RunActivity.Of(chat), L) == "");
        string th = RunActivity.Describe(new RunActivity.Counts(1, 0, 2), k => LocalizedResources.Instance[k]);
        Check("P5d background commands count too", th.Contains("1 sub-agent") && th.Contains("2 background commands"), th);
        return Task.CompletedTask;
    }

    // ═══════════════════════════════ piece 2: the workflow ═══════════════════════════════

    static Task TestPlanChecks()
    {
        string Err(string json) { WorkflowPlan.Parse(json, out var e); return e ?? ""; }
        Check("W1a a valid plan parses (comments and trailing commas allowed, as models write them)",
            WorkflowPlan.Parse("""
                {"name":"ok", // review
                  "phases":[{"title":"scan","agents":[{"label":"a","prompt":"p"},]}]}
                """, out var e0) != null, e0);
        Check("W1b two agents with one label are refused", Err($$"""{"phases":[{"title":"s","agents":[{{Agents(1)}},{{Agents(1)}}]}]}""").Length > 0);
        Check("W1c a pipeline from a phase that is not earlier is refused",
            Err("""{"phases":[{"title":"p","pipeline":{"from":"later","stages":[{"label":"x","prompt":"y"}]}},{"title":"later","agents":[{"label":"a","prompt":"p"}]}]}""").Length > 0);
        Check("W1d a phase with two kinds (agents AND pipeline) is refused",
            Err("""{"phases":[{"title":"p","agents":[{"label":"a","prompt":"p"}],"pipeline":{"items":["i"],"stages":[{"label":"x","prompt":"y"}]}}]}""").Length > 0);
        Check("W1e more than 60 agents is refused before anything starts",
            Err($$"""{"phases":[{"title":"s","agents":[{{Agents(61)}}]}]}""").Contains("60"));
        Check("W1f a verify-only phase needs from", Err("""{"phases":[{"title":"v","verify":{"refuters":3} }]}""").Length > 0);
        Check("W1g broken JSON says so", Err("{not json").Length > 0);
        return Task.CompletedTask;
    }

    static async Task TestFanOut()
    {
        var (svc, mock, slots) = NewService();
        mock.Answer = async (c, ct) => { await Task.Delay(60, ct); return "found in " + c.Label; };
        var st = await svc.RunAsync(PlanOf($$"""{"name":"fan","concurrency":2,"phases":[{"title":"scan","agents":[{{Agents(5)}}]}]}"""), LastJson, "orch-w2", default);
        Check("W2a fan-out: every agent ran and finished", st.Status == "done" && st.Agents.Count == 5 && st.Agents.All(a => a.State == "done"), st.Summary);
        Check("W2b never more than the plan's concurrency at once", mock.MaxRunning == 2, $"max={mock.MaxRunning}");
        Check("W2c every machine slot taken was given back", slots[0] == 5 && slots[1] == 5, $"{slots[0]}/{slots[1]}");
        Check("W2d the phase's results are in the report", WorkflowService.Report(st).Contains("found in a3"));
        Check("W2e the spend adds up per agent", st.SpentUsd == 0.05m, st.SpentUsd.ToString());
    }

    static async Task TestPipelineNoBarrier()
    {
        var (svc, mock, _) = NewService();
        mock.Answer = async (c, ct) =>
        {
            await Task.Delay(c.Label == "s1 · 3" ? 900 : 40, ct);
            return c.Label.StartsWith("s1") ? "one:" + c.Prompt : "two:" + c.Prompt;
        };
        var st = await svc.RunAsync(PlanOf("""
            {"name":"pipe","concurrency":3,"phases":[{"title":"chain","pipeline":{"items":["a","b","c"],
              "stages":[{"label":"s1","prompt":"first {{item}}"},{"label":"s2","prompt":"second {{prev}}"}]}}]}
            """), LastJson, "orch-w3", default);
        var t = mock.Times;
        Check("W3a every item went through both stages", st.Status == "done" && st.Agents.Count == 6 && st.PhaseItems["chain"].Count == 3, st.Summary);
        t.TryGetValue("s2 · 1", out var s21);
        t.TryGetValue("s1 · 3", out var s13);
        Check("W3b no barrier: item 1's second stage started before item 3's first stage ended",
            s21.Start != default && s13.End != default && s21.Start < s13.End,
            $"s2·1 start {s21.Start:HH:mm:ss.fff}, s1·3 end {s13.End:HH:mm:ss.fff}");
        var second = mock.Seen.FirstOrDefault(c => c.Label == "s2 · 2");
        Check("W3c a stage gets the previous stage's answer ({{prev}}) and the first gets the item ({{item}})",
            second?.Prompt.Contains("second one:first b") == true, second?.Prompt);
    }

    static async Task TestVerifyMajority()
    {
        const string schema = """{"type":"object","properties":{"findings":{"type":"array"}},"required":["findings"]}""";
        Func<WorkflowAgentCall, CancellationToken, Task<string>> answers = (c, _) =>
        {
            if (!c.Label.Contains("refute")) return Task.FromResult("""{"findings":["REAL bug in a.cs","FAKE bug in b.cs"]}""");
            int r = int.Parse(c.Label[(c.Label.LastIndexOf("refute ", StringComparison.Ordinal) + 7)..].Split('/')[0]);
            bool fake = c.Prompt.Contains("FAKE bug");
            bool refuted = fake ? r <= 2 : r == 1;   // the fake: 2 of 3 refute it; the real one: 1 of 3
            return Task.FromResult($$"""{"refuted":{{(refuted ? "true" : "false")}},"reason":"checked"}""");
        };
        var (svc, mock, _) = NewService();
        mock.Answer = answers;
        var st = await svc.RunAsync(PlanOf($$"""
            {"name":"inline","concurrency":4,"phases":[{"title":"find","agents":[{"label":"f","prompt":"find bugs","schema":{{schema}} }],
              "verify":{"field":"findings","refuters":3} }]}
            """), LastJson, "orch-w4", default);
        Check("W4a inline verify: the claim most refuters refute is dropped, the other kept",
            st.Status == "done" && st.PhaseItems["find"].SequenceEqual(new[] { "REAL bug in a.cs" }), string.Join(" | ", st.PhaseItems["find"]));
        Check("W4b three refuters per claim, majority recorded", st.Verdicts.Count == 2 && st.Verdicts.Count(v => v.Killed) == 1
            && st.Verdicts.Single(v => v.Killed).Refuted == 2 && st.Agents.Count(a => a.Kind == "refuter") == 6);
        Check("W4c the refuters were asked for the refuter JSON (refuted, reason)",
            mock.Seen.Where(c => c.Label.Contains("refute")).All(c => c.Prompt.Contains("\"refuted\"") && c.Type == "explore"));
        Check("W4d the report lists the refuted claim under its own heading", WorkflowService.Report(st).Contains("Refuted by the verifiers (1)"));

        var (svc2, mock2, _) = NewService();
        mock2.Answer = answers;
        var st2 = await svc2.RunAsync(PlanOf($$"""
            {"name":"phase","phases":[{"title":"find","agents":[{"label":"f","prompt":"find bugs","schema":{{schema}} }]},
              {"title":"check","verify":{"from":"find","field":"findings","refuters":3} }]}
            """), LastJson, "orch-w4", default);
        Check("W4e a verify phase over an earlier phase's items does the same",
            st2.Status == "done" && st2.PhaseItems["check"].SequenceEqual(new[] { "REAL bug in a.cs" }), string.Join(" | ", st2.PhaseItems.GetValueOrDefault("check") ?? new()));
    }

    static async Task TestSchemaRepair()
    {
        const string schema = """{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}""";
        var (svc, mock, _) = NewService();
        mock.Answer = (c, _) => Task.FromResult(c.Attempt == 1 ? "Sure! The answer is 42." : """Here: {"answer":"42"}""");
        var st = await svc.RunAsync(PlanOf($$"""{"phases":[{"title":"q","agents":[{"label":"r","prompt":"p","schema":{{schema}} }]}]}"""), LastJson, "orch-w5", default);
        var second = mock.Seen.FirstOrDefault(c => c.Attempt == 2);
        Check("W5a an answer that is not the JSON gets one repair turn that says what was wrong",
            st.Agents.Single().State == "done" && st.Agents.Single().Output == """{"answer":"42"}""" && second?.Prompt.Contains("did not match the required JSON (no JSON object)") == true && second.Prompt.Contains("Sure! The answer is 42."),
            second?.Prompt);
        var (svc2, mock2, _) = NewService();
        mock2.Answer = (_, _) => Task.FromResult("still prose");
        var st2 = await svc2.RunAsync(PlanOf($$"""{"phases":[{"title":"q","agents":[{"label":"r","prompt":"p","schema":{{schema}} }]}]}"""), LastJson, "orch-w5", default);
        Check("W5b wrong twice: that agent fails (with why), the workflow ends", st2.Status == "done" && st2.Agents.Single().State == "failed"
            && st2.Agents.Single().Error.Contains("schema") && mock2.Calls == 2, st2.Agents.Single().Error);
        Check("W5c Conforms: required, top-level types, prose around the object",
            WorkflowEngine.Conforms("x {\"answer\":\"a\"} y", JsonDocument.Parse(schema).RootElement, out var j, out _) && j == "{\"answer\":\"a\"}"
            && !WorkflowEngine.Conforms("{\"answer\":1}", JsonDocument.Parse(schema).RootElement, out _, out var why) && why.Contains("string"));
    }

    static async Task TestBudget()
    {
        var (svc, mock, slots) = NewService();
        svc.BudgetSpent = () => mock.Calls >= 2;
        var st = await svc.RunAsync(PlanOf($$"""{"concurrency":1,"phases":[{"title":"s","agents":[{{Agents(4)}}]}]}"""), LastJson, "orch-w6", default);
        Check("W6a the run's budget is spent: no agent starts after that, each is marked skipped",
            mock.Calls == 2 && st.Agents.Count(a => a.State == "skipped") == 2 && st.Agents.Count(a => a.State == "done") == 2, st.Summary);
        Check("W6b skipped agents take no machine slot", slots[0] == 2 && slots[1] == 2, $"{slots[0]}/{slots[1]}");
        Check("W6c the summary says the budget skipped them", st.Summary.Contains("skipped (budget)"), st.Summary);
    }

    static async Task TestMachineSlots()
    {
        using var machine = new SemaphoreSlim(1);
        var (svc, mock, _) = NewService(machine: machine);
        mock.Answer = async (c, ct) => { await Task.Delay(50, ct); return "x"; };
        await svc.RunAsync(PlanOf($$"""{"concurrency":3,"phases":[{"title":"s","agents":[{{Agents(3)}}]}]}"""), LastJson, "orch-w7", default);
        Check("W7 the machine's limit (AgentLoad) holds over the plan's concurrency", mock.MaxRunning == 1, $"max={mock.MaxRunning}");
    }

    static async Task TestStopDuringFanOut()
    {
        var (svc, mock, slots) = NewService();
        mock.Answer = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ""; };
        string id = null;
        svc.Started += s => id = s.Id;
        const string chat = "orch-w8";
        var run = svc.RunAsync(PlanOf($$"""{"concurrency":3,"phases":[{"title":"s","agents":[{{Agents(6)}}]}]}"""), "{}", chat, default);
        bool three = await WaitUntil(() => mock.Running == 3);
        bool counted = RunActivity.Of(chat).Workflows == 1;
        bool stopped = svc.Stop(id);
        var done = await Task.WhenAny(run, Task.Delay(5000));
        var st = done == run ? await run : null;
        Check("W8a Stop during a fan-out ends the workflow at once", three && stopped && st?.Status == "stopped", st?.Summary ?? "did not end in 5 s");
        Check("W8b every running agent was cancelled, none left queued or running",
            mock.Cancelled == 3 && st != null && st.Agents.All(a => a.State == "stopped") && mock.Running == 0, $"cancelled={mock.Cancelled}");
        Check("W8c the three that never started did not start", mock.Calls == 3, $"calls={mock.Calls}");
        Check("W8d machine slots balance after a stop", slots[0] == slots[1], $"{slots[0]}/{slots[1]}");
        Check("W8e the plan's 'N running' line counted it while it ran, and not after", counted && RunActivity.Of(chat).Workflows == 0);
        Check("W8f a second Stop finds nothing running", !svc.Stop(id));
        Check("W8g the report tells the model how to resume", WorkflowService.Report(st).Contains("resume_id"));
    }

    static async Task TestResumeFromCache()
    {
        var (svc, mock, _) = NewService();
        mock.Answer = async (c, ct) =>
        {
            if (c.Label is "a3" or "a4") await Task.Delay(Timeout.Infinite, ct);
            return "result " + c.Label;
        };
        string id = null;
        svc.Started += s => id = s.Id;
        var run = svc.RunAsync(PlanOf($$"""{"concurrency":4,"phases":[{"title":"s","agents":[{{Agents(4)}}]}]}"""), LastJson, "orch-w9", default);
        await WaitUntil(() => id != null && svc.Get(id).Agents.Count(a => a.State == "done") == 2 && mock.Running == 2);
        await Task.Delay(30);
        svc.Stop(id);
        await run;
        Check("W9a stopped with two finished", svc.Get(id).Status == "stopped" && svc.Get(id).Cache.Count == 2);
        Check("W9b it can be resumed", svc.ResumeRefusal(id) == null, svc.ResumeRefusal(id));
        mock.Answer = (c, _) => Task.FromResult("result " + c.Label);
        int before = mock.Calls;
        var st = await svc.ResumeAsync(id, default);
        Check("W9c Resume runs only what did not finish; the rest comes from the cache",
            st?.Status == "done" && mock.Calls - before == 2 && st.Agents.Count(a => a.State == "cached") == 2
            && st.Agents.Where(a => a.State == "cached").All(a => a.Label is "a1" or "a2"), st?.Summary);
        Check("W9d the earlier attempt's time stays on the clock", st.ElapsedBefore > 0, st.ElapsedBefore.ToString());
        Check("W9e a finished workflow is not resumed again", svc.ResumeRefusal(id)?.Contains("already finished") == true);
    }

    static async Task TestRestartMidWorkflow()
    {
        string dir = Path.Combine(Root, "data-w10");
        var (svc1, mock1, _) = NewService(dir);
        mock1.Answer = async (c, ct) => { if (c.Label == "a2") await Task.Delay(Timeout.Infinite, ct); return "first " + c.Label; };
        string id = null;
        svc1.Started += s => id = s.Id;
        var run = svc1.RunAsync(PlanOf($$"""{"name":"restart","concurrency":2,"phases":[{"title":"s","agents":[{{Agents(2)}}]}]}"""), LastJson, "orch-w10", default);
        await WaitUntil(() => id != null && svc1.Get(id).Agents.Any(a => a.Label == "a1" && a.State == "done") && mock1.Running == 1);
        string file = Path.Combine(dir, "workflows", id + ".json");
        Check("W10a the state file is written while it runs", File.Exists(file) && File.ReadAllText(file).Contains("\"running\""));

        // The app closes without stopping it (the process ends); the next app run reads the folder.
        var (svc2, mock2, _) = NewService(dir);
        int marked = svc2.MarkInterrupted();
        var seen = svc2.Get(id);
        Check("W10b after a restart the workflow reads as stopped (app closed), with Resume offered",
            marked == 1 && seen?.Status == "stopped" && seen.StopReason == "app closed" && svc2.ResumeRefusal(id) == null, $"marked={marked} {seen?.Status} {seen?.StopReason}");
        svc1.Stop(id);
        await run;

        var st = await svc2.ResumeAsync(id, default);
        Check("W10c resumed after the restart: only the unfinished agent runs again",
            st?.Status == "done" && mock2.Calls == 1 && mock2.Seen.Single().Label == "a2"
            && st.Agents.Single(a => a.Label == "a1").State == "cached", st?.Summary);
        Check("W10d MarkInterrupted leaves finished workflows alone", svc2.MarkInterrupted() == 0);
        Check("W10e an unknown or path-like id is refused, not read", svc2.Get("..\\evil") == null && svc2.ResumeRefusal("wf-nope") != null);
    }

    static async Task TestDoubleResume()
    {
        var (svc, mock, _) = NewService();
        mock.Answer = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ""; };
        string id = null;
        svc.Started += s => id = s.Id;
        var run = svc.RunAsync(PlanOf($$"""{"phases":[{"title":"s","agents":[{{Agents(2)}}]}]}"""), LastJson, "orch-w11", default);
        await WaitUntil(() => mock.Running == 2);
        svc.Stop(id);
        await run;
        mock.Answer = async (_, ct) => { await Task.Delay(200, ct); return "x"; };
        int startedBefore = 0;
        svc.Started += _ => Interlocked.Increment(ref startedBefore);
        var both = await Task.WhenAll(svc.ResumeAsync(id, default), svc.ResumeAsync(id, default));
        Check("W11 a double Resume starts it once (the other gets null)",
            both.Count(s => s != null) == 1 && startedBefore == 1 && both.First(s => s != null).Status == "done", $"started={startedBefore}");
    }

    static async Task TestHardCap()
    {
        var (svc, mock, _) = NewService();
        var many = "[" + string.Join(",", Enumerable.Range(1, 150).Select(i => $"\"item {i}\"")) + "]";
        mock.Answer = (c, _) => Task.FromResult(c.Label == "src" ? many : "ok");
        var st = await svc.RunAsync(PlanOf("""
            {"concurrency":8,"phases":[{"title":"list","agents":[{"label":"src","prompt":"list"}]},
              {"title":"each","pipeline":{"from":"list","stages":[{"label":"do","prompt":"do {{item}}"}]}}]}
            """), LastJson, "orch-w13", default);
        int ran = st.Agents.Count(a => a.State == "done"), capped = st.Agents.Count(a => a.State == "skipped");
        Check("W13 items an earlier phase produced cannot breed past the hard ceiling: the rest are skipped, not run",
            st.Status == "done" && ran == WorkflowEngine.HardMaxAgents && mock.Calls == WorkflowEngine.HardMaxAgents
            && capped == 151 - WorkflowEngine.HardMaxAgents && st.Summary.Contains("agent limit"), $"ran={ran} capped={capped} calls={mock.Calls} · {st.Summary}");
    }

    static Task TestEstimate()
    {
        var (svc, _, _) = NewService();
        svc.MachineCap = () => 2;
        var plan = PlanOf($$"""{"concurrency":3,"phases":[{"title":"s","agents":[{{Agents(4)}}],"verify":{"field":"findings","refuters":3} }]}""");
        var est = svc.EstimateFor(plan);
        Check("W12 the estimate counts refuters, prices each agent, and RAM by what runs at once (the machine's cap wins)",
            est.Agents == plan.EstimateAgents() && est.Agents > 4 && est.Usd == est.Agents * svc.PerAgentUsd && est.AtOnce == 2
            && est.RamMb == 2 * AgentLoad.RamPerAgentMb, $"{est}");
        return Task.CompletedTask;
    }

    // ═══════════════════════════════ the gate and the tool ═══════════════════════════════

    static Task TestGate()
    {
        const string chat = "orch-g1";
        Check("G1a off by default", !WorkflowGate.Allowed(chat) && OrchestrationToolModule.WorkflowRefusal(chat)?.Contains("Ultra") == true);
        WorkflowGate.NoteOwnerMessage(chat, "please review the repo, use a workflow with verifiers");
        Check("G1b the owner's own words open it for that turn", WorkflowGate.Why(chat) == "asked");
        WorkflowGate.NoteOwnerMessage(chat, "and check the tests too", append: true);
        Check("G1c a message typed into the same running turn keeps it open", WorkflowGate.Why(chat) == "asked");
        WorkflowGate.NoteOwnerMessage(chat, "thanks, now fix the typo");
        Check("G1d the next turn's words close it again", WorkflowGate.Why(chat) == null);
        Check("G1e the words that count (English and Thai) and ones that do not",
            new[] { "ultracode this", "run a workflow", "multi-agent review", "fan out 5 agents", "ใช้ workflow ตรวจ", "แตกเอเจนต์หลายตัว", "อัลตร้าโค้ด" }.All(WorkflowGate.AsksForWorkflow)
            && !new[] { "fix the workflow file", "what is an agent", "", null }.Any(WorkflowGate.AsksForWorkflow));
        WorkflowGate.SetUltra(chat, true);
        Check("G1f the Ultra switch opens it for the chat", WorkflowGate.Why(chat) == "ultra" && WorkflowGate.IsUltra(chat) && !WorkflowGate.IsUltra("orch-other"));
        string? cliGuide = WorkflowGate.SplitGuideFor(chat, cli: true), nativeGuide = WorkflowGate.SplitGuideFor(chat, cli: false);
        Check("G1i with Ultra on the model is told so, with when to do it itself, spawn, run a workflow or split off a chat",
            cliGuide != null && cliGuide.Contains("Ultra") && cliGuide.Contains("the Agent tool") && cliGuide.Contains("mcp__cluadex__workflow")
            && cliGuide.Contains("mcp__cluadex__new_chat") && cliGuide.Contains("same files") && !cliGuide.Contains('\n')
            && nativeGuide != null && nativeGuide.Contains("agent_spawn") && !nativeGuide.Contains("mcp__"), cliGuide ?? "(none)");
        string args = CluadeX.Services.Providers.ClaudeDevProvider.BuildArgs(Root, new(true, true), "default", "auto",
            toolsConfig: Path.Combine(Root, "tools.json"), splitGuide: cliGuide);
        Check("G1j a Claude Dev worker's wrapper carries the guide", args.Contains("take the first that fits"), "");
        WorkflowGate.SetUltra(chat, false);
        Check("G1g and off again", !WorkflowGate.Allowed(chat));
        Check("G1k no guide while off", WorkflowGate.SplitGuideFor(chat, cli: true) == null);
        string dir = Path.Combine(Root, "gate");
        Directory.CreateDirectory(dir);
        WorkflowGate.Load(dir);
        WorkflowGate.SetUltra("persisted", true);
        WorkflowGate.Load(dir);
        bool kept = WorkflowGate.IsUltra("persisted");
        File.WriteAllText(Path.Combine(dir, "ultra-chats.json"), "{damaged");
        WorkflowGate.Load(dir);
        Check("G1h the switch survives a restart; a damaged file means every chat off", kept && !WorkflowGate.IsUltra("persisted"));
        WorkflowGate.Load(null);
        return Task.CompletedTask;
    }

    static async Task TestWorkflowTool()
    {
        var (svc, mock, _) = NewService();
        mock.Answer = (c, _) => Task.FromResult("answer from " + c.Label);
        var old = WorkflowService.Current;
        WorkflowService.Current = svc;
        const string chat = "orch-g2";
        var scope = new AgentRunScope(chat, Root);
        AgentRunScope.Enter(scope);
        var asked = new List<string>();
        bool allow = true;
        Func<PermissionRequestInfo, Task<bool>> prompt = info => { lock (asked) asked.Add(info.Detail ?? ""); return Task.FromResult(allow); };
        Svc.OnPermissionRequired += prompt;
        string phases = $$"""[{"title":"scan","agents":[{{Agents(2)}}]}]""";
        try
        {
            Check("G2a the workflow tool is not offered while the chat has not opted in",
                !Svc.BuildNativeToolSchemas().Any(s => s.Name == "workflow") && Svc.BuildNativeToolSchemas().Any(s => s.Name == "wrap_up"));
            var off = await T("workflow", ("name", "rev"), ("phases", phases));
            Check("G2b called anyway: refused with the reason, no agent ran", !off.Success && Seen(off).Contains("Ultra") && mock.Calls == 0, Seen(off));

            WorkflowGate.SetUltra(chat, true);
            Check("G2c Ultra on: offered", Svc.BuildNativeToolSchemas().Any(s => s.Name == "workflow"));
            var on = await T("workflow", ("name", "rev"), ("phases", phases));
            Check("G2d it runs the plan and returns each phase's results; Ultra = no extra prompt",
                on.Success && on.Output.Contains("answer from a1") && on.Output.Contains("answer from a2") && asked.Count == 0, Seen(on));

            svc.BudgetLeft = () => 0.01m;
            int calls = mock.Calls;
            var poor = await T("workflow", ("name", "rev"), ("phases", phases));
            Check("G2e an estimate over the run's budget left is refused before any agent starts",
                !poor.Success && Seen(poor).Contains("budget") && mock.Calls == calls, Seen(poor));
            svc.BudgetLeft = () => null;

            WorkflowGate.SetUltra(chat, false);
            WorkflowGate.NoteOwnerMessage(chat, "run a workflow over the services");
            var ask = await T("workflow", ("plan", $$"""{"name":"by words","phases":{{phases}} }"""));
            Check("G2f opened by the owner's words: the call asks first and shows the estimate (agents, $, RAM)",
                ask.Success && asked.Count == 1 && asked[0].Contains("agents") && asked[0].Contains("$") && asked[0].Contains("GB RAM"), string.Join(" | ", asked));
            allow = false;
            var denied = await T("workflow", ("plan", $$"""{"name":"denied","phases":{{phases}} }"""));
            Check("G2g a No on that prompt runs nothing", !denied.Success && mock.Calls == calls + 2, Seen(denied));

            svc.RunnerRefusal = () => "no model with native tools is ready";
            allow = true;
            var noRunner = await T("workflow", ("plan", $$"""{"name":"x","phases":{{phases}} }"""));
            Check("G2h a provider that cannot run sub-agents is refused with why (not a dead button)", !noRunner.Success && Seen(noRunner).Contains("native tools"), Seen(noRunner));
            svc.RunnerRefusal = null;

            var badPlan = await T("workflow", ("plan", """{"phases":[{"title":"x"}]}"""));
            Check("G2i a broken plan comes back as an error the model can fix", !badPlan.Success && Seen(badPlan).Length > 0, Seen(badPlan));

            // Another chat's stopped workflow: not resumable from here.
            mock.Answer = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ""; };
            string otherId = null;
            Action<WorkflowRunState> grab = s => otherId ??= s.Id;
            svc.Started += grab;
            var other = svc.RunAsync(PlanOf($$"""{"name":"theirs","phases":{{phases}} }"""), LastJson, "orch-other-chat", default);
            await WaitUntil(() => mock.Running > 0);
            svc.Stop(otherId);
            await other;
            svc.Started -= grab;
            int callsNow = mock.Calls;
            var cross = await T("workflow", ("resume_id", otherId));
            Check("G2j a workflow of another chat cannot be resumed from this one", !cross.Success && Seen(cross).Contains("another chat") && mock.Calls == callsNow, Seen(cross));
        }
        finally
        {
            Svc.OnPermissionRequired -= prompt;
            AgentRunScope.Enter(null);
            WorkflowGate.Load(null);
            WorkflowService.Current = old;
        }
    }

    // ═══════════════════════════════ piece 6: wrap_up ═══════════════════════════════

    static Task TestNextStepArgs()
    {
        var ok = NextStep.FromArgs("Fixed the timeout; the build passes. Not run live.", "Run the live test now?", """["Yes, run it","Not now"]""", out var e1);
        Check("N1a recap, one question, two buttons", ok != null && ok.Actions.Count == 2 && ok.Actions[0] == "Yes, run it", e1);
        Check("N1b the recap is required", NextStep.FromArgs(" ", "", "", out var e2) == null && e2.Contains("recap"));
        Check("N1c a question over 100 characters is refused", NextStep.FromArgs("r", new string('q', 101), "", out var e3) == null && e3.Contains("100"));
        Check("N1d three buttons are refused", NextStep.FromArgs("r", "q?", "a|b|c", out _) == null);
        Check("N1e buttons need a question", NextStep.FromArgs("r", "", "[\"Yes\"]", out var e5) == null && e5.Contains("ask"));
        Check("N1f labels from JSON strings, objects, or a | list; each cut to 24 characters",
            NextStep.Labels("""[{"label":"Yes"},"No"]""").SequenceEqual(new[] { "Yes", "No" }) && NextStep.Labels("A | B").SequenceEqual(new[] { "A", "B" })
            && NextStep.Labels("[\"" + new string('x', 40) + "\"]")[0].Length == 24);
        var back = NextStep.FromJson(ok.ToJson());
        Check("N1g kept with the chat row as JSON and read back the same", back != null && back.Recap == ok.Recap && back.Ask == ok.Ask && back.Actions.SequenceEqual(ok.Actions));
        return Task.CompletedTask;
    }

    static async Task TestWrapUpTool()
    {
        const string chat = "orch-n2";
        AgentRunScope.Enter(new AgentRunScope(chat, Root));
        var got = new List<(string Chat, NextStep Step)>();
        Action<string, NextStep> on = (c, s) => got.Add((c, s));
        OrchestrationToolModule.NextStepRequested += on;
        try
        {
            var r = await T("wrap_up", ("recap", "Done the thing. Not verified live."), ("ask", "Ship it?"), ("actions", """["Yes","Not yet"]"""));
            Check("N2a wrap_up hands the box to its own chat and tells the model to write its closing message",
                r.Success && got.Count == 1 && got[0].Chat == chat && got[0].Step.Ask == "Ship it?" && r.Output.Contains("closing message"), Seen(r));
            // Buttons with no question are dropped, not refused: the refusal made models redo the whole task to
            // "try again" (smallbench, 2026-11-08). The box shows its recap and the reply says not to redo anything.
            var fixedUp = await T("next_step", ("recap", "x"), ("actions", "a|b|c"));
            Check("N2b buttons with no question: the box shows without them, and the reply says what was left out and not to redo work",
                fixedUp.Success && got.Count == 2 && got[1].Step.Actions.Count == 0 && got[1].Step.Ask == null
                && fixedUp.Output.Contains("left out") && fixedUp.Output.Contains("do not redo"), Seen(fixedUp));
            var bad = await T("next_step", ("recap", " "), ("ask", "Ship it?"), ("actions", "[\"Yes\"]"));
            Check("N2c a box with no recap still fails with why (and shows nothing)", !bad.Success && got.Count == 2, Seen(bad));
        }
        finally
        {
            OrchestrationToolModule.NextStepRequested -= on;
            AgentRunScope.Enter(null);
        }
    }
}
