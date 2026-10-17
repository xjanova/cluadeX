// Effort-aware dispatch (owner, 2026-10-17: "ระบบส่งเอเจนแยกทำงาน ต้องฉลาดตามความพยายาม … เพื่อแก้ไขงานได้รวดเร็ว" ·
// "นี่แหละจะทำให้เสียเงินค่าโทเค็นคุ้มที่สุด"). Checks the pure DispatchPolicy for every effort level and spend state,
// the model routing per sub-agent kind, a speculative-fix race in a real git repository with a fake build command
// that only one fixer can turn green, the worker pool's disjoint pieces / reviewer / merge, and the cost ledger's
// strategy tags. No model is called: the fixers and workers are plain delegates editing their worktrees.
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;

namespace ApiTest;

internal static class DispatchWire
{
    static DispatchInput Base(string effort) => new()
    {
        Effort = effort, PolicyEnabled = true, ProviderCanRunSubAgents = true, RunBudgetUsd = 2m, RunSpentUsd = 0.10m,
        MaxParallel = 3, SpeculativeFixEnabled = true, SpeculativeFixCount = 3, CanEditProject = true, IsGitRepo = true,
    };

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · dispatch policy (effort → fan-out, workers, speculative fix)");
        Policy(check);
        Routing(check);
        Helpers(check);
        Ledger(check);

        section("offline · speculative fix race in a real git worktree (fake build, one fixer can win)");
        string root = Path.Combine(Path.GetTempPath(), "cx-dispatch-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await SpeculativeRace(check, Path.Combine(root, "race"));
            await NoWinner(check, Path.Combine(root, "nowin"));
            section("offline · workers in worktrees (disjoint pieces, reviewer, merge)");
            await Workers(check, Path.Combine(root, "workers"));
        }
        finally
        {
            try { ForceDelete(root); } catch { }
        }
    }

    // ─────────────────────────── the policy ───────────────────────────

    static void Policy(Action<string, string, bool, string> check)
    {
        var low = DispatchPolicy.Decide(Base("low"));
        check("dispatch", "low: no sub-agents at all", !low.SubAgents && low.Ceiling == DispatchStrategy.Single, low.Reason);

        var med = DispatchPolicy.Decide(Base("medium"));
        check("dispatch", "medium: read-only fan-out only — no workers, no speculative fix",
            med.SubAgents && med.ReadOnlyOnly && !med.Workers && !med.SpeculativeFix && med.Ceiling == DispatchStrategy.FanOut, med.Reason);

        var high = DispatchPolicy.Decide(Base("high"));
        check("dispatch", "high: workers with a reviewer, edits allowed, speculative fix available (after a failed fix)",
            high.SubAgents && !high.ReadOnlyOnly && high.Workers && high.Reviewer && high.SpeculativeFix && high.FixerCount == 3, high.Reason);
        foreach (var e in new[] { "xhigh", "max" })
        {
            var d = DispatchPolicy.Decide(Base(e));
            check("dispatch", $"{e}: as high, plus speculative fixes", d.Workers && d.SpeculativeFix && d.Ceiling == DispatchStrategy.SpeculativeFix, d.Reason);
        }

        // auto = the loop's step pick
        check("dispatch", "auto resolves to the loop's step effort (medium before it picked)",
            DispatchPolicy.ResolveEffort("auto", "high") == "high" && DispatchPolicy.ResolveEffort("auto", null) == "medium"
            && DispatchPolicy.ResolveEffort("low", "xhigh") == "low", "");

        // spend guards
        var spent = DispatchPolicy.Decide(Base("max") with { RunSpentUsd = 2.5m });
        check("dispatch", "run budget used up → single, whatever the effort", !spent.SubAgents && spent.Reason.Contains("budget"), spent.Reason);
        var granted = DispatchPolicy.Decide(Base("max") with { RunSpentUsd = 2.5m, BudgetGrantedUsd = 2m });
        check("dispatch", "…the user's \"go on\" grant counts as budget again", granted.Workers, granted.Reason);
        var near = DispatchPolicy.Decide(Base("xhigh") with { RunSpentUsd = 1.6m });
        check("dispatch", "under 25% of the run budget left → cheap read-only fan-out only",
            near.SubAgents && near.ReadOnlyOnly && !near.Workers && !near.SpeculativeFix, near.Reason);
        var noBudget = DispatchPolicy.Decide(Base("xhigh") with { RunBudgetUsd = 0, RunSpentUsd = 50m });
        check("dispatch", "no run budget set (0) → never capped by it", noBudget.Workers, noBudget.Reason);
        var daily = DispatchPolicy.Decide(Base("high") with { DailyBudgetUsd = 10m, TodaySpentUsd = 10m });
        check("dispatch", "daily cap reached → single", !daily.SubAgents, daily.Reason);
        var dailyNear = DispatchPolicy.Decide(Base("high") with { DailyBudgetUsd = 10m, TodaySpentUsd = 8m });
        check("dispatch", "daily cap nearly reached → read-only fan-out only", dailyNear.ReadOnlyOnly && !dailyNear.Workers, dailyNear.Reason);

        // what workers and fixers need
        var noGit = DispatchPolicy.Decide(Base("max") with { IsGitRepo = false });
        check("dispatch", "not a git repo → no worktrees (read-only fan-out)", noGit.SubAgents && !noGit.Workers && !noGit.SpeculativeFix, noGit.Reason);
        var noEdit = DispatchPolicy.Decide(Base("max") with { CanEditProject = false });
        check("dispatch", "edits need asking in this folder → no workers or fixers", !noEdit.Workers && !noEdit.SpeculativeFix, noEdit.Reason);
        var cannot = DispatchPolicy.Decide(Base("max") with { ProviderCanRunSubAgents = false });
        check("dispatch", "a model without native tool use → single", !cannot.SubAgents, cannot.Reason);

        // concurrency and fixer count
        var one = DispatchPolicy.Decide(Base("max") with { MaxParallel = 1 });
        check("dispatch", "max parallel 1 → workers one at a time, no speculative race", one.Workers && !one.SpeculativeFix && one.MaxConcurrency == 1, one.Reason);
        var two = DispatchPolicy.Decide(Base("max") with { MaxParallel = 2, SpeculativeFixCount = 3 });
        check("dispatch", "fixers never exceed max parallel (3 asked, 2 allowed)", two.FixerCount == 2, two.FixerCount.ToString());
        var many = DispatchPolicy.Decide(Base("high") with { MaxParallel = 50, SpeculativeFixCount = 9 });
        check("dispatch", "max parallel capped at 8, fixers at 3", many.MaxConcurrency == 8 && many.FixerCount == 3, $"{many.MaxConcurrency}/{many.FixerCount}");
        var specOff = DispatchPolicy.Decide(Base("max") with { SpeculativeFixEnabled = false });
        check("dispatch", "speculative fix switched off → workers stay, no race", specOff.Workers && !specOff.SpeculativeFix && specOff.Ceiling == DispatchStrategy.Workers, "");

        var off = DispatchPolicy.Decide(Base("low") with { PolicyEnabled = false });
        check("dispatch", "policy off → legacy: sub-agents as before even at low, no workers", off.Legacy && off.SubAgents && !off.Workers
            && DispatchPolicy.Gate(off, "general", true) == null && DispatchPolicy.Gate(off, "workers", true) != null, off.Reason);

        // when to race
        check("dispatch", "speculate at high only after the main loop's own fix failed once",
            !DispatchPolicy.ShouldSpeculate(high, "high", true, 0, false) && DispatchPolicy.ShouldSpeculate(high, "high", true, 1, false), "");
        var xhigh = DispatchPolicy.Decide(Base("xhigh"));
        check("dispatch", "speculate at xhigh on the first red build; never on green, never twice on the same failure",
            DispatchPolicy.ShouldSpeculate(xhigh, "xhigh", true, 0, false) && !DispatchPolicy.ShouldSpeculate(xhigh, "xhigh", false, 3, false)
            && !DispatchPolicy.ShouldSpeculate(xhigh, "xhigh", true, 3, true), "");
        check("dispatch", "never at medium, whatever failed", !DispatchPolicy.ShouldSpeculate(med, "medium", true, 5, false), "");

        // the gate the main agent's agent_spawn goes through
        check("dispatch", "gate: low refuses every sub-agent with a reason to do it yourself",
            DispatchPolicy.Gate(low, "explore", false) is { } r1 && r1.Contains("yourself"), DispatchPolicy.Gate(low, "explore", false) ?? "");
        check("dispatch", "gate: medium lets explore through, refuses general / an editing specialist / workers",
            DispatchPolicy.Gate(med, "explore", false) == null && DispatchPolicy.Gate(med, "general", true) != null
            && DispatchPolicy.Gate(med, "code-reviewer", true) != null && DispatchPolicy.Gate(med, "code-reviewer", false) == null
            && DispatchPolicy.Gate(med, "workers", true) != null, "");
        check("dispatch", "gate: high lets general and workers through", DispatchPolicy.Gate(high, "general", true) == null
            && DispatchPolicy.Gate(high, "workers", true) == null, "");
    }

    static void Routing(Action<string, string, bool, string> check)
    {
        check("routing", "explore → the cheapest model of the active provider",
            DispatchPolicy.ModelFor(SubAgentKind.Explore, AiProviderType.Anthropic, "claude-opus-5-5") == "claude-haiku-5-5"
            && DispatchPolicy.ModelFor(SubAgentKind.Explore, AiProviderType.OpenAI, "gpt-6-astra") == "gpt-6-luna"
            && DispatchPolicy.ModelFor(SubAgentKind.Explore, AiProviderType.Gemini, "gemini-3.8-flash") == "gemini-3.5-flash-lite", "");
        check("routing", "code / review / fix → the run's model",
            DispatchPolicy.ModelFor(SubAgentKind.Code, AiProviderType.Anthropic, "claude-sonnet-5-5") == "claude-sonnet-5-5"
            && DispatchPolicy.ModelFor(SubAgentKind.Fix, AiProviderType.Anthropic, "claude-sonnet-5-5", null, 1) == "claude-sonnet-5-5", "");
        check("routing", "a fix after two failed fixers → one step up (Opus-class); already Opus/Fable stays",
            DispatchPolicy.ModelFor(SubAgentKind.Fix, AiProviderType.Anthropic, "claude-sonnet-5-5", null, 2) == "claude-opus-5-5"
            && DispatchPolicy.ModelFor(SubAgentKind.Fix, AiProviderType.Anthropic, "claude-fable-5-1", null, 2) == "claude-fable-5-1"
            && DispatchPolicy.ModelFor(SubAgentKind.HardFix, AiProviderType.OpenAI, "gpt-6-sol") == "gpt-6-astra", "");
        var ov = new Dictionary<string, string> { ["explore"] = "claude-sonnet-5-5", ["hardfix"] = "claude-fable-5-1", ["code"] = " " };
        check("routing", "per-kind overrides from Settings win; a blank one is ignored",
            DispatchPolicy.ModelFor(SubAgentKind.Explore, AiProviderType.Anthropic, "claude-opus-5-5", ov) == "claude-sonnet-5-5"
            && DispatchPolicy.ModelFor(SubAgentKind.Fix, AiProviderType.Anthropic, "claude-opus-5-5", ov, 2) == "claude-fable-5-1"
            && DispatchPolicy.ModelFor(SubAgentKind.Code, AiProviderType.Anthropic, "claude-opus-5-5", ov) == "claude-opus-5-5", "");
        check("routing", "local models and Claude Dev are not re-routed (the loaded / logged-in model)",
            DispatchPolicy.ModelFor(SubAgentKind.Explore, AiProviderType.LlamaServer, "") == null
            && DispatchPolicy.ModelFor(SubAgentKind.Explore, AiProviderType.ClaudeDev, "") == null, "");
        check("routing", "quota exhausted + failover on → sub-agents go to Claude Dev (never when already on it)",
            DispatchPolicy.UseClaudeDev(true, true, AiProviderType.Anthropic) && !DispatchPolicy.UseClaudeDev(true, false, AiProviderType.Anthropic)
            && !DispatchPolicy.UseClaudeDev(false, true, AiProviderType.Anthropic) && !DispatchPolicy.UseClaudeDev(true, true, AiProviderType.ClaudeDev), "");
        check("routing", "the quota test reads the API's real wording",
            CodeAgentService.IsQuotaExhausted("Your credit balance is too low to access the Anthropic API")
            && !CodeAgentService.IsQuotaExhausted("rate_limit_error: too many requests"), "");

        // The override flows with the sub-agent's async call only.
        string? inside = null;
        async Task SubAgent() { SubAgentModel.Use("claude-haiku-5-5"); await Task.Yield(); inside = SubAgentModel.Pick("claude-opus-5-5"); }
        SubAgent().GetAwaiter().GetResult();
        check("routing", "the sub-agent's model override holds in its own flow and is gone for the caller",
            inside == "claude-haiku-5-5" && SubAgentModel.Override == null && SubAgentModel.Pick("claude-opus-5-5") == "claude-opus-5-5", inside ?? "");
    }

    static void Helpers(Action<string, string, bool, string> check)
    {
        var pieces = CodeAgentService.ParsePieces("[{\"task\":\"API\",\"files\":[\"src/Api.cs\"]},{\"task\":\"UI\",\"files\":\"src/View.xaml, src/View.xaml.cs\"}]");
        check("dispatch", "pieces parse from the model's JSON (files as an array or a comma list)",
            pieces.Count == 2 && pieces[1].Files.Count == 2 && pieces[1].Files[1] == "src/View.xaml.cs", string.Join(" | ", pieces.Select(p => string.Join(",", p.Files))));
        check("dispatch", "disjoint pieces pass; a file in two pieces, a piece with no files, a single piece are refused",
            DispatchPolicy.CheckPieces(pieces) == null
            && DispatchPolicy.CheckPieces(new[] { new DispatchPolicy.WorkPiece("a", new[] { "src/A.cs" }), new DispatchPolicy.WorkPiece("b", new[] { ".\\src\\a.cs" }) }) is { } dup && dup.Contains("disjoint")
            && DispatchPolicy.CheckPieces(new[] { new DispatchPolicy.WorkPiece("a", new[] { "x" }), new DispatchPolicy.WorkPiece("b", Array.Empty<string>()) }) != null
            && DispatchPolicy.CheckPieces(new[] { new DispatchPolicy.WorkPiece("a", new[] { "x" }) }) != null, "");
        var cmdResult = new ToolResult { Type = ToolType.RunBuild, Success = false, Output = "$ dotnet build App.sln   (detected: App.sln)\nerror CS0103" };
        check("dispatch", "the failing command is read back from the build row", CodeAgentService.FailingCommandOf(cmdResult) == "dotnet build App.sln",
            CodeAgentService.FailingCommandOf(cmdResult) ?? "");
        check("dispatch", "one failure = one signature (line numbers ignored), another failure differs",
            CodeAgentService.FailureSignature("src/A.cs(12,5): error CS0103: x") == CodeAgentService.FailureSignature("src/A.cs(14,9): error CS0103: x")
            && CodeAgentService.FailureSignature("error CS0103: x") != CodeAgentService.FailureSignature("error CS1002: ; expected"), "");
    }

    static void Ledger(Action<string, string, bool, string> check)
    {
        var cost = new CostTrackingService();
        cost.RecordRun("single", 0.80m, 120, true);
        cost.RecordRun("single", 1.20m, 300, false);
        cost.RecordRun("fan-out", 0.30m, 60, true);
        cost.RecordRun("speculative-fix", 0.50m, 90, true);
        cost.RecordRun("workers", 0.70m, 200, true, DateTime.Now.AddDays(-20));   // outside the 14 days
        var rows = cost.StrategyBreakdown(14);
        var single = rows.FirstOrDefault(r => r.Strategy == "single");
        check("cost-tag", "runs are tagged by strategy: count, successes, cost, time; per-run averages",
            single is { Runs: 2, Successes: 1 } && single.Usd == 2.00m && single.UsdPerRun == 1.00m && Math.Abs(single.SecondsPerRun - 210) < 0.01
            && Math.Abs(single.SuccessShare - 0.5) < 1e-9, single == null ? "none" : $"{single.Runs} runs ${single.Usd}");
        check("cost-tag", "14 days only, in policy order (single, fan-out, … speculative-fix)",
            rows.Select(r => r.Strategy).SequenceEqual(new[] { "single", "fan-out", "speculative-fix" }), string.Join(",", rows.Select(r => r.Strategy)));
        var day = new DailyUsage { ByStrategy = new() { ["fan-out"] = new StrategyUsage { Runs = 3, Successes = 2, Usd = 0.4m, Seconds = 50 } } };
        var back = JsonSerializer.Deserialize<DailyUsage>(JsonSerializer.Serialize(day));
        check("cost-tag", "the strategy split survives the ledger's JSON (usage-daily.json)",
            back?.ByStrategy?["fan-out"] is { Runs: 3, Successes: 2, Usd: 0.4m }, "");

        // A sub-agent's scope reports its cost and strategy to the run.
        var run = new AgentRunScope("t", null);
        var child = run.CreateChild(@"C:\wt\fix1", acceptEditsInFolder: true);
        var tracker = new CostTrackingService();
        AgentRunScope.Enter(child);
        try { tracker.RecordUsage("claude-haiku-5-5", 10_000, 2_000); }
        finally { AgentRunScope.Enter(null); }
        child.MarkStrategy((int)DispatchStrategy.SpeculativeFix);
        run.MarkStrategy((int)DispatchStrategy.FanOut);
        check("cost-tag", "a sub-agent's spend shows on its own node AND adds up in the run (budget guard sees it)",
            child.CostUsd > 0 && run.CostUsd == child.CostUsd && run.Requests == 1, $"child ${child.CostUsd} run ${run.CostUsd}");
        check("cost-tag", "the run is recorded under the furthest strategy it used", run.Strategy == (int)DispatchStrategy.SpeculativeFix
            && DispatchPolicy.Tag((DispatchStrategy)run.Strategy) == "speculative-fix", run.Strategy.ToString());
        check("cost-tag", "a worktree child scope: its own folder, Accept edits only there",
            child.WorkingDirectory == @"C:\wt\fix1" && child.AcceptEditsRoot == @"C:\wt\fix1" && child.Parent == run, "");
    }

    // ─────────────────────────── the race ───────────────────────────

    static async Task<string> Git(string cwd, params string[] args)
    {
        var r = await WorktreeSandbox.GitAsync(cwd, CancellationToken.None, null, args);
        if (r.Code != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {r.Out}");
        return r.Out;
    }

    static async Task<string> NewRepo(string dir, Dictionary<string, string> files)
    {
        Directory.CreateDirectory(dir);
        await Git(dir, "init", "-q", "-b", "main");
        await Git(dir, "config", "user.email", "t@t"); await Git(dir, "config", "user.name", "t");
        await Git(dir, "config", "core.autocrlf", "false");
        foreach (var (k, v) in files) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, k))!); File.WriteAllText(Path.Combine(dir, k), v); }
        await Git(dir, "add", "-A");
        await Git(dir, "commit", "-q", "-m", "init");
        return dir;
    }

    /// <summary>The fake build: green only when calc.txt says answer=42 (via the shell the app really uses).</summary>
    static string FakeBuild() => ShellRunner.DefaultShell == ShellRunner.Shell.Bash
        ? "grep -q \"answer=42\" calc.txt"
        : "findstr /c:\"answer=42\" calc.txt";

    static async Task<(bool, string)> RunCmd(string cmd, string cwd, CancellationToken ct)
    {
        var r = await ShellRunner.RunAsync(ShellRunner.DefaultShell, cmd, cwd, 60_000, ct);
        return (r.ExitCode == 0 && !r.TimedOut && !r.Cancelled, r.Output);
    }

    static async Task SpeculativeRace(Action<string, string, bool, string> check, string repo)
    {
        await NewRepo(repo, new() { ["calc.txt"] = "answer=41\n", ["readme.txt"] = "v1\n" });
        // Uncommitted work in the run folder that every fixer must start from — and that must survive the merge.
        File.WriteAllText(Path.Combine(repo, "readme.txt"), "v2 (uncommitted)\n");
        File.WriteAllText(Path.Combine(repo, "notes.md"), "untracked\n");
        string branchBefore = (await Git(repo, "rev-parse", "HEAD")).Trim();

        var (redBefore, buildOut) = await RunCmd(FakeBuild(), repo, CancellationToken.None);
        check("spec-fix", "the fake build is red before the race", !redBefore, "");

        var seeded = new bool[3];
        var prompts = new string[3];
        var events = new List<SpeculativeFixer.Event>();
        async Task<string> Fixer(SpeculativeFixer.FixerContext fc, CancellationToken ct)
        {
            prompts[fc.Index] = fc.Prompt;
            seeded[fc.Index] = File.ReadAllText(Path.Combine(fc.WorkingDirectory, "readme.txt")).StartsWith("v2")
                               && File.Exists(Path.Combine(fc.WorkingDirectory, "notes.md"));
            switch (fc.Index)
            {
                case 0: await Task.Delay(100, ct); File.WriteAllText(Path.Combine(fc.WorkingDirectory, "calc.txt"), "answer=40\n"); return "set it to 40";
                case 1: await Task.Delay(400, ct); File.WriteAllText(Path.Combine(fc.WorkingDirectory, "calc.txt"), "answer=42\n"); return "the answer is 42";
                default: await Task.Delay(30_000, ct); File.WriteAllText(Path.Combine(fc.WorkingDirectory, "calc.txt"), "answer=42\n"); return "slow";
            }
        }
        var req = new SpeculativeFixer.Request(repo, FakeBuild(), "error: calc.txt says answer=41 (expected 42)\n" + buildOut,
            SpeculativeFixer.FilesNamedIn("error in calc.txt(1,1): wrong answer", repo).Concat(new[] { "calc.txt" }).Distinct().ToList(),
            "calc.txt\nreadme.txt", "lesson: the answer is always 42", 3, TimeSpan.FromMinutes(2));
        var sw = Stopwatch.StartNew();
        var result = await SpeculativeFixer.RunAsync(req, Fixer, RunCmd, e => { lock (events) events.Add(e); }, CancellationToken.None);
        sw.Stop();

        check("spec-fix", "every fixer started from the run folder's uncommitted state (tracked change + untracked file)", seeded.All(x => x), string.Join(",", seeded));
        check("spec-fix", "each fixer's brief holds only the command, its output, the named files, its approach, repo map + recall",
            prompts.All(p => p != null && p.Contains(FakeBuild()) && p.Contains("answer=41") && p.Contains("calc.txt") && p.Contains("lesson: the answer"))
            && prompts[0].Contains("MINIMAL") && prompts[1].Contains("ROOT CAUSE") && prompts[2].Contains("ALTERNATIVE"), "");
        check("spec-fix", "the only fixer that turns the command green wins (#2)", result.Fixed && result.Winner?.Index == 1, result.Summary);
        check("spec-fix", "the winner's diff is applied to the run folder — the command is green there now",
            File.ReadAllText(Path.Combine(repo, "calc.txt")).Trim() == "answer=42" && (await RunCmd(FakeBuild(), repo, CancellationToken.None)).Item1, "");
        check("spec-fix", "the user's uncommitted work is untouched, the branch did not move",
            File.ReadAllText(Path.Combine(repo, "readme.txt")).StartsWith("v2") && File.ReadAllText(Path.Combine(repo, "notes.md")) == "untracked\n"
            && (await Git(repo, "rev-parse", "HEAD")).Trim() == branchBefore, "");
        check("spec-fix", "the wrong fixer is red with its reason; the slow one was cancelled, not awaited (30 s)",
            result.All[0] is { Green: false, Error: not null } && result.All[2].Error?.Contains("cancelled") == true && sw.Elapsed < TimeSpan.FromSeconds(20),
            $"{result.All[2].Error} · {sw.Elapsed.TotalSeconds:0.0}s");
        string worktrees = await Git(repo, "worktree", "list");
        check("spec-fix", "every worktree is removed afterwards (git and disk)",
            worktrees.Trim().Split('\n').Length == 1 && !Directory.Exists(WorktreeSandbox.HomeFor(repo)), worktrees);
        check("spec-fix", "the agent map heard start → re-run → green / red / cancelled",
            events.Any(e => e.Index == 1 && e.Phase == "green") && events.Any(e => e.Index == 0 && e.Phase == "red")
            && events.Any(e => e.Index == 2 && e.Phase == "cancelled") && events.Count(e => e.Phase == "start") == 3,
            string.Join(" ", events.Select(e => $"{e.Index}:{e.Phase}")));
        check("spec-fix", "the main loop is told who won, how, and what changed", result.Summary.Contains("#2") && result.Summary.Contains("calc.txt"), result.Summary);
    }

    static async Task NoWinner(Action<string, string, bool, string> check, string repo)
    {
        await NewRepo(repo, new() { ["calc.txt"] = "answer=41\n" });
        async Task<string> Wrong(SpeculativeFixer.FixerContext fc, CancellationToken ct)
        {
            await Task.Yield();
            File.WriteAllText(Path.Combine(fc.WorkingDirectory, "calc.txt"), $"answer={30 + fc.Index}\n");
            return $"tried {30 + fc.Index}: the value comes from config.ini";
        }
        var req = new SpeculativeFixer.Request(repo, FakeBuild(), "error: wrong answer", new[] { "calc.txt" }, "", null, 2, TimeSpan.FromMinutes(1));
        var r = await SpeculativeFixer.RunAsync(req, Wrong, RunCmd, null, CancellationToken.None);
        check("spec-fix", "no fixer green → nothing applied, the project is as it was",
            !r.Fixed && r.Winner == null && File.ReadAllText(Path.Combine(repo, "calc.txt")) == "answer=41\n", "");
        check("spec-fix", "…and the main loop gets what each one learned", r.Summary.Contains("config.ini") && r.Summary.Contains("#1") && r.Summary.Contains("#2"), r.Summary);

        async Task<string> Slow(SpeculativeFixer.FixerContext fc, CancellationToken ct) { await Task.Delay(60_000, ct); return ""; }
        var sw = Stopwatch.StartNew();
        var t = await SpeculativeFixer.RunAsync(req with { Timeout = TimeSpan.FromSeconds(2) }, Slow, RunCmd, null, CancellationToken.None);
        check("spec-fix", "none green within the time limit → back to the main loop (timed out, worktrees gone)",
            !t.Fixed && t.All.All(o => o.Error == "timed out") && sw.Elapsed < TimeSpan.FromSeconds(20)
            && (await Git(repo, "worktree", "list")).Trim().Split('\n').Length == 1, $"{sw.Elapsed.TotalSeconds:0.0}s");
    }

    // ─────────────────────────── workers ───────────────────────────

    static async Task Workers(Action<string, string, bool, string> check, string repo)
    {
        await NewRepo(repo, new() { ["src/a.txt"] = "a1\n", ["src/b.txt"] = "b1\n", ["src/c.txt"] = "c1\n" });
        var pieces = new[]
        {
            new DispatchPolicy.WorkPiece("change a", new[] { "src/a.txt" }),
            new DispatchPolicy.WorkPiece("change b", new[] { "src/b.txt" }),
        };
        int running = 0, peak = 0;
        async Task<string> Worker(WorkerPool.WorkerContext wc, CancellationToken ct)
        {
            int now = Interlocked.Increment(ref running);
            lock (pieces) peak = Math.Max(peak, now);
            await Task.Delay(300, ct);
            string f = wc.Piece.Files[0];
            File.WriteAllText(Path.Combine(wc.WorkingDirectory, f), f.Contains("a.") ? "a2\n" : "b2\n");
            Interlocked.Decrement(ref running);
            return $"changed {f}";
        }
        string? reviewed = null;
        Task<string> Approve(string prompt, CancellationToken ct) { reviewed = prompt; return Task.FromResult("APPROVE — the two pieces are independent."); }
        var r = await WorkerPool.RunAsync(repo, "change a and b", pieces, "", "map", 2, Worker, Approve, null, CancellationToken.None);
        check("workers", "two disjoint pieces ran side by side, the reviewer saw both diffs, both merged",
            r.Merged && peak == 2 && File.ReadAllText(Path.Combine(repo, "src/a.txt")) == "a2\n" && File.ReadAllText(Path.Combine(repo, "src/b.txt")) == "b2\n"
            && reviewed != null && reviewed.Contains("-a1") && reviewed.Contains("+b2"), r.Summary);
        check("workers", "worktrees removed after the merge", (await Git(repo, "worktree", "list")).Trim().Split('\n').Length == 1, "");

        await Git(repo, "checkout", "-q", "--", ".");
        int ran = 0, peak1 = 0;
        async Task<string> Serial(WorkerPool.WorkerContext wc, CancellationToken ct)
        {
            int now = Interlocked.Increment(ref ran); lock (pieces) peak1 = Math.Max(peak1, now);
            await Task.Delay(150, ct); Interlocked.Decrement(ref ran); return "";
        }
        await WorkerPool.RunAsync(repo, "t", pieces, "", "", 1, Serial, null, null, CancellationToken.None);
        check("workers", "max parallel 1 → one worker at a time", peak1 == 1, peak1.ToString());

        // A worker that wanders outside its piece is dropped; a rejecting reviewer merges nothing.
        async Task<string> Wander(WorkerPool.WorkerContext wc, CancellationToken ct)
        {
            await Task.Yield();
            File.WriteAllText(Path.Combine(wc.WorkingDirectory, wc.Piece.Files[0]), "changed\n");
            if (wc.Index == 1) File.WriteAllText(Path.Combine(wc.WorkingDirectory, "src/c.txt"), "not mine\n");
            return "";
        }
        var w = await WorkerPool.RunAsync(repo, "t", pieces, "", "", 2, Wander, (_, _) => Task.FromResult("APPROVE"), null, CancellationToken.None);
        check("workers", "a worker that changed a file outside its piece is dropped from the merge; the other lands",
            w.Merged && w.Workers[1].OutsideFiles.Contains("src/c.txt") && File.ReadAllText(Path.Combine(repo, "src/c.txt")) == "c1\n"
            && File.ReadAllText(Path.Combine(repo, "src/a.txt")) == "changed\n" && File.ReadAllText(Path.Combine(repo, "src/b.txt")) == "b1\n", w.Summary);
        await Git(repo, "checkout", "-q", "--", ".");
        var rej = await WorkerPool.RunAsync(repo, "t", pieces, "", "", 2, Worker, (_, _) => Task.FromResult("REJECT: b breaks a's caller"), null, CancellationToken.None);
        check("workers", "the reviewer says REJECT → nothing is merged, its reasons reach the main agent",
            !rej.Merged && File.ReadAllText(Path.Combine(repo, "src/a.txt")) == "a1\n" && rej.Summary.Contains("b breaks"), rej.Summary);
        check("workers", "the reviewer's verdict needs APPROVE as its first word",
            WorkerPool.ParseReview("**APPROVE** looks fine").Approved && !WorkerPool.ParseReview("I would not APPROVE this").Approved, "");
    }

    static void ForceDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }
}
