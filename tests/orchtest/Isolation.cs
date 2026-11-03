using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Tools;

namespace OrchTest;

/// <summary>
/// Isolation, gate and merge (a workflow phase with isolation "worktree"): scripted agents edit their own git worktrees of a
/// real throwaway repository; the engine itself runs the gate there (the app's real shell, or a scripted runner when the
/// test needs to count or combine), merges the green diffs one by one, and runs the full test once on the merged result.
/// </summary>
internal static partial class Program
{
    static async Task<string> Git(string cwd, params string[] args)
    {
        var r = await WorktreeSandbox.GitAsync(cwd, CancellationToken.None, null, args);
        if (r.Code != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {r.Out}");
        return r.Out;
    }

    static async Task<string> NewRepo(string name, Dictionary<string, string> files)
    {
        string dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        await Git(dir, "init", "-q", "-b", "main");
        await Git(dir, "config", "user.email", "t@t");
        await Git(dir, "config", "user.name", "t");
        await Git(dir, "config", "core.autocrlf", "false");
        foreach (var (k, v) in files) File.WriteAllText(Path.Combine(dir, k), v);
        await Git(dir, "add", "-A");
        await Git(dir, "commit", "-q", "-m", "init");
        return dir;
    }

    static async Task<int> WorktreeCount(string repo) =>
        (await Git(repo, "worktree", "list", "--porcelain")).Split('\n').Count(l => l.StartsWith("worktree "));

    static async Task<bool> NoWorktreesLeft(string repo) =>
        await WorktreeCount(repo) == 1 && !Directory.Exists(WorktreeSandbox.HomeFor(repo));

    static string Read(string dir, string file) => File.ReadAllText(Path.Combine(dir, file)).Replace("\r\n", "\n");

    /// <summary>A gate through the app's real shell: red when any .txt in the folder says BROKEN.</summary>
    static string NoBrokenGate() => ShellRunner.DefaultShell == ShellRunner.Shell.Bash
        ? "! grep -q BROKEN *.txt"
        : "findstr /m BROKEN *.txt && exit 1 || exit 0";

    /// <summary>Only I1 and I3 run real commands (a grep over the throwaway repository): the engine's verdict must come from
    /// a real exit code. Every other check, and the rest of this harness, stays in the default dry-run (ProcessLaunchPolicy).</summary>
    static Func<Task> RealShell(Func<Task> body) => async () =>
    {
        bool was = CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun;
        CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun = false;
        try { await body(); }
        finally { CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun = was; }
    };

    /// <summary>Every command the engine ran (command, folder) — through the real shell.</summary>
    static ConcurrentQueue<(string Cmd, string Cwd)> CountRealCommands(WorkflowService svc)
    {
        var ran = new ConcurrentQueue<(string, string)>();
        svc.RunCommand = async (cmd, cwd, ct) => { ran.Enqueue((cmd, cwd)); return await WorkflowEngine.DefaultRunCommandAsync(cmd, cwd, ct); };
        return ran;
    }

    static string IsoPlan(string agents, string gate = null, string test = null, bool merge = true)
    {
        var phase = new Dictionary<string, object> { ["title"] = "implement", ["isolation"] = "worktree", ["agents"] = JsonDocument.Parse("[" + agents + "]").RootElement };
        if (gate != null) phase["gate"] = gate;
        if (merge) phase["merge"] = test != null ? new Dictionary<string, object> { ["test"] = test } : new Dictionary<string, object>();
        return JsonSerializer.Serialize(new { name = "iso", concurrency = 3, phases = new[] { phase } });
    }

    static string Agent(string label, string prompt = null) => JsonSerializer.Serialize(new { label, prompt = prompt ?? "change " + label });

    /// <summary>Scripted agents: each label writes its file in the folder it was given (its worktree).</summary>
    static Func<WorkflowAgentCall, CancellationToken, Task<string>> Writes(Dictionary<string, (string File, string Text)> byLabel) =>
        async (c, ct) =>
        {
            await Task.Delay(20, ct);
            if (c.WorkingDirectory == null) throw new Exception("no worktree given to " + c.Label);
            var (file, text) = byLabel[c.Label];
            File.WriteAllText(Path.Combine(c.WorkingDirectory, file), text);
            return $"{c.Label} wrote {file}";
        };

    // ─── I1: two agents, different files, green → both merged, tests once ───

    static async Task TestIsolatedCleanMerge()
    {
        string repo = await NewRepo("iso-clean", new() { ["a.txt"] = "a1\n", ["b.txt"] = "b1\n" });
        File.WriteAllText(Path.Combine(repo, "notes.md"), "uncommitted note\n");   // the run folder's own work: every worktree starts from it
        string head = (await Git(repo, "rev-parse", "HEAD")).Trim();
        var (svc, mock, slots) = NewService();
        var ran = CountRealCommands(svc);
        var dirs = new ConcurrentDictionary<string, string>();
        var sawNote = new ConcurrentDictionary<string, bool>();
        var write = Writes(new() { ["a"] = ("a.txt", "a2\n"), ["b"] = ("b.txt", "b2\n") });
        mock.Answer = async (c, ct) =>
        {
            dirs[c.Label] = c.WorkingDirectory;
            sawNote[c.Label] = File.Exists(Path.Combine(c.WorkingDirectory, "notes.md"));
            return await write(c, ct);
        };
        string test = ShellRunner.DefaultShell == ShellRunner.Shell.Bash ? "grep -q a2 a.txt && grep -q b2 b.txt" : "findstr a2 a.txt && findstr b2 b.txt";
        var st = await svc.RunAsync(PlanOf(IsoPlan(Agent("a") + "," + Agent("b"), NoBrokenGate(), test)), LastJson, "orch-i1", default, repo);

        Check("I1a each agent got its own worktree, inside the worktree home, not the run folder",
            dirs.Count == 2 && dirs["a"] != dirs["b"] && dirs.Values.All(d => d.StartsWith(WorktreeSandbox.HomeFor(repo), StringComparison.OrdinalIgnoreCase)),
            string.Join(" | ", dirs.Select(kv => kv.Key + "=" + kv.Value)));
        Check("I1b each worktree started from the run folder's uncommitted state", sawNote.Count == 2 && sawNote.Values.All(x => x));
        var rows = st.Agents.Where(a => a.Kind == "agent").ToDictionary(a => a.Label);
        Check("I1c each agent's result carries its diff and its changed files",
            rows["a"].Patch?.Contains("+a2") == true && rows["b"].Patch?.Contains("+b2") == true
            && rows["a"].ChangedFiles.SequenceEqual(new[] { "a.txt" }) && rows["b"].ChangedFiles.SequenceEqual(new[] { "b.txt" }), rows["a"].Patch);
        Check("I1d the engine ran the gate itself in each worktree (real shell): both green",
            rows["a"].Gate == "green" && rows["b"].Gate == "green" && rows["a"].GateOutput?.Contains("dry-run") != true
            && ran.Count(r => r.Cmd == NoBrokenGate() && dirs.Values.Contains(r.Cwd)) == 2, string.Join(" | ", ran));
        Check("I1e both diffs merged into the run folder; nothing committed, the branch did not move, the note is untouched",
            st.Merges.Count == 2 && st.Merges.All(m => m.Outcome == "merged") && Read(repo, "a.txt") == "a2\n" && Read(repo, "b.txt") == "b2\n"
            && (await Git(repo, "rev-parse", "HEAD")).Trim() == head && Read(repo, "notes.md") == "uncommitted note\n",
            string.Join(", ", st.Merges.Select(m => m.Label + ":" + m.Outcome + " " + m.Detail)));
        Check("I1f the gate re-ran on the merged tree after each merge, then the full test ran once there: green",
            ran.Count(r => r.Cmd == NoBrokenGate() && r.Cwd == repo) == 2 && ran.Count(r => r.Cmd == test) == 1
            && st.Tests.Count == 1 && st.Tests[0].Green == true && st.Tests[0].Merged == 2, string.Join(" | ", ran));
        Check("I1g every worktree is gone (git and disk), the state lists none", await NoWorktreesLeft(repo) && st.Worktrees.Count == 0,
            await Git(repo, "worktree", "list"));
        string report = WorkflowService.Report(st);
        Check("I1h the report and summary say exactly what passed",
            report.Contains("a: merged (a.txt)") && report.Contains("→ GREEN") && st.Summary.Contains("2 merged") && st.Summary.Contains("tests on the merged result: green"),
            report);
        Check("I1i an isolated agent runs as general (it edits) and is told where its copy is",
            mock.Seen.All(c => c.Type == "general" && c.Prompt.Contains(c.WorkingDirectory)) && slots[0] == slots[1]);
        var card = new CluadeX.ViewModels.WorkflowCard(st.Id, svc);
        string en = card.MergeText, rowEn = card.Rows[0].StateText;
        LocSvc.SetLanguage("th");
        card.MarkDirty();
        card.Flush();
        string th = card.MergeText, rowTh = card.Rows[0].StateText;
        LocSvc.SetLanguage("en");
        Check("I1j the card shows the merge and the test on the merged result, and each row's gate — in English and Thai",
            en == "merge: 2 merged · 0 left out · tests on the merged result ✓" && rowEn == "done · gate ✓"
            && th.Contains("รวมแล้ว 2") && th.Contains("เทสต์บนงานที่รวมแล้วผ่าน") && rowTh.Contains("ผ่านการตรวจ"), $"{en} | {rowEn} | {th} | {rowTh}");
    }

    // ─── I2: same lines → conflict reported, never dropped ───

    static async Task TestIsolatedConflict()
    {
        string repo = await NewRepo("iso-conflict", new() { ["a.txt"] = "line1\nline2\n" });
        var (svc, mock, _) = NewService();
        mock.Answer = Writes(new() { ["first"] = ("a.txt", "FIRST\nline2\n"), ["second"] = ("a.txt", "SECOND\nline2\n") });
        var st = await svc.RunAsync(PlanOf(IsoPlan(Agent("first") + "," + Agent("second"))), LastJson, "orch-i2", default, repo);
        var second = st.Merges.SingleOrDefault(m => m.Label == "second");
        Check("I2a two agents changing the same line: the first merges, the second is reported as a conflict (with git's reason)",
            st.Merges.Count == 2 && st.Merges[0].Outcome == "merged" && second?.Outcome == "conflict" && second.Detail?.Length > 0,
            string.Join(", ", st.Merges.Select(m => m.Label + ":" + m.Outcome + " " + m.Detail)));
        Check("I2b the project holds the first change only", Read(repo, "a.txt") == "FIRST\nline2\n", Read(repo, "a.txt"));
        string report = WorkflowService.Report(st);
        Check("I2c the report names it and carries its diff so it can be done by hand",
            report.Contains("second: NOT merged — its diff no longer applies") && report.Contains("+SECOND") && st.Summary.Contains("1 conflict"), report);
        Check("I2d no gate, no test: nothing ran a command, and no worktree is left", st.Tests.Count == 0 && await NoWorktreesLeft(repo));
    }

    // ─── I3: a red gate excludes that diff; a diff red only on the merged tree is taken back out ───

    static async Task TestIsolatedRedGate()
    {
        string repo = await NewRepo("iso-red", new() { ["a.txt"] = "a1\n", ["b.txt"] = "b1\n" });
        var (svc, mock, _) = NewService();
        var ran = CountRealCommands(svc);
        mock.Answer = Writes(new() { ["good"] = ("a.txt", "a2\n"), ["bad"] = ("b.txt", "BROKEN\n") });
        var st = await svc.RunAsync(PlanOf(IsoPlan(Agent("good") + "," + Agent("bad"), NoBrokenGate())), LastJson, "orch-i3", default, repo);
        var rows = st.Agents.ToDictionary(a => a.Label);
        Check("I3a the agent whose build is red in its own worktree is red (the engine's verdict, not the agent's word)",
            rows["bad"].Gate == "red" && rows["good"].Gate == "green" && rows["bad"].Output.Contains("wrote")
            && rows["bad"].GateOutput?.Contains("dry-run") != true, rows["bad"].GateOutput);
        Check("I3b its diff is excluded from the merge and reported; the green one merged",
            st.Merges.Single(m => m.Label == "bad").Outcome == "gate-red" && st.Merges.Single(m => m.Label == "good").Outcome == "merged"
            && Read(repo, "b.txt") == "b1\n" && Read(repo, "a.txt") == "a2\n",
            string.Join(", ", st.Merges.Select(m => m.Label + ":" + m.Outcome)));
        Check("I3c with no merge test, the gate runs once more on the merged result: green",
            st.Tests.Count == 1 && st.Tests[0].Command == NoBrokenGate() && st.Tests[0].Green == true && st.Tests[0].Merged == 1
            && WorkflowService.Report(st).Contains("bad: NOT merged — its gate was red"), string.Join(" | ", ran));

        // Green alone, red together: the scripted gate fails only when a.txt says X AND b.txt says Y.
        string repo2 = await NewRepo("iso-red-merged", new() { ["a.txt"] = "a1\n", ["b.txt"] = "b1\n" });
        var (svc2, mock2, _) = NewService();
        var ran2 = new ConcurrentQueue<(string Cmd, string Cwd)>();
        svc2.RunCommand = (cmd, cwd, ct) =>
        {
            ran2.Enqueue((cmd, cwd));
            bool both = Read(cwd, "a.txt").Contains('X') && Read(cwd, "b.txt").Contains('Y');
            return Task.FromResult(cmd == "check" && both ? (false, "error: X and Y together break the build") : (true, "ok"));
        };
        mock2.Answer = Writes(new() { ["x"] = ("a.txt", "X\n"), ["y"] = ("b.txt", "Y\n") });
        var st2 = await svc2.RunAsync(PlanOf(IsoPlan(Agent("x") + "," + Agent("y"), "check", "fulltest")), LastJson, "orch-i3b", default, repo2);
        var y = st2.Merges.SingleOrDefault(m => m.Label == "y");
        Check("I3d green alone but red on the merged tree: reported, taken back out, the earlier merge kept",
            st2.Agents.All(a => a.Gate == "green") && st2.Merges[0].Outcome == "merged" && y?.Outcome == "red-after-merge"
            && y.Detail.Contains("X and Y") && Read(repo2, "a.txt") == "X\n" && Read(repo2, "b.txt") == "b1\n",
            string.Join(", ", st2.Merges.Select(m => m.Label + ":" + m.Outcome + " " + m.Detail)));
        Check("I3e then the full test ran exactly once, in the run folder, on what was merged",
            ran2.Count(r => r.Cmd == "fulltest") == 1 && ran2.Single(r => r.Cmd == "fulltest").Cwd == repo2 && st2.Tests.Single().Green == true
            && st2.Summary.Contains("1 red-after-merge"), st2.Summary);
        Check("I3f no worktree left by either run", await NoWorktreesLeft(repo) && await NoWorktreesLeft(repo2));
    }

    // ─── I4: Stop removes every worktree and merges nothing ───

    static async Task TestIsolatedStop()
    {
        string repo = await NewRepo("iso-stop", new() { ["a.txt"] = "a1\n" });
        var (svc, mock, slots) = NewService();
        mock.Answer = async (c, ct) =>
        {
            File.WriteAllText(Path.Combine(c.WorkingDirectory, "a.txt"), c.Label + "\n");
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        };
        string id = null;
        svc.Started += s => id = s.Id;
        var run = svc.RunAsync(PlanOf(IsoPlan(Agent("p") + "," + Agent("q") + "," + Agent("r"), "check")), LastJson, "orch-i4", default, repo);
        bool three = await WaitUntil(() => mock.Running == 3);
        int whileRunning = await WorktreeCount(repo);
        bool recorded = svc.Get(id)?.Worktrees.Count == 3;
        svc.Stop(id);
        var done = await Task.WhenAny(run, Task.Delay(15000));
        var st = done == run ? await run : null;
        Check("I4a three agents were working in three worktrees, each recorded in the state before it started",
            three && whileRunning == 4 && recorded, $"worktrees={whileRunning} recorded={recorded}");
        Check("I4b Stop ends the workflow", st?.Status == "stopped" && st.Agents.All(a => a.State == "stopped"), st?.Summary ?? "did not end in 15 s");
        Check("I4c every worktree is removed (git and disk) and the state lists none", await NoWorktreesLeft(repo) && st?.Worktrees.Count == 0,
            await Git(repo, "worktree", "list"));
        Check("I4d nothing reached the project: no merge ran, the tree is clean",
            st?.Merges.Count == 0 && (await Git(repo, "status", "--porcelain")).Trim().Length == 0 && slots[0] == slots[1]);
    }

    // ─── I5: a crash leaves worktrees; the next start sweeps only this workflow's ───

    static async Task TestIsolatedCrashSweep()
    {
        string repo = await NewRepo("iso-crash", new() { ["a.txt"] = "a1\n" });
        string dir = Path.Combine(Root, "crash-data");
        // What a crashed app left: a worktree of wf-0123456789 (recorded), and one a stop cut off before it was recorded.
        var mine = await WorktreeSandbox.CreateAsync(repo, "wf-0123456789-1", default);
        var unrecorded = await WorktreeSandbox.CreateAsync(repo, "wf-0123456789-2", default);
        var other = await WorktreeSandbox.CreateAsync(repo, "wf-9999999999-1", default);   // another workflow's: not ours to touch
        string decoy = Path.Combine(Root, "decoy-folder");
        Directory.CreateDirectory(decoy);
        File.WriteAllText(Path.Combine(decoy, "keep.txt"), "keep");
        var st = new WorkflowRunState
        {
            Id = "wf-0123456789", Name = "crashed", Status = "running", RunFolder = repo, SessionKey = "orch-i5",
            PlanJson = IsoPlan(Agent("a")), Worktrees = { mine.Root, decoy },
        };
        Directory.CreateDirectory(Path.Combine(dir, "workflows"));
        File.WriteAllText(Path.Combine(dir, "workflows", st.Id + ".json"), JsonSerializer.Serialize(st));

        var svc = new WorkflowService(dir) { Runner = (_, _) => Task.FromResult(new WorkflowAgentResult("", 0)) };
        int interrupted = svc.MarkInterrupted();
        int swept = await svc.SweepLeftWorktreesAsync();
        Check("I5a after a crash the next start removes the workflow's worktrees (recorded or not)",
            interrupted == 1 && swept == 2 && !Directory.Exists(mine.Root) && !Directory.Exists(unrecorded.Root)
            && !(await Git(repo, "worktree", "list")).Contains(Path.GetFileName(mine.Root)), $"interrupted={interrupted} swept={swept}");
        Check("I5b a path in the state file outside the worktree home is never deleted; another workflow's worktree stays",
            File.Exists(Path.Combine(decoy, "keep.txt")) && Directory.Exists(other.Root));
        Check("I5c the state file no longer lists worktrees", svc.Get(st.Id)?.Worktrees.Count == 0
            && JsonSerializer.Deserialize<WorkflowRunState>(File.ReadAllText(Path.Combine(dir, "workflows", st.Id + ".json")))!.Worktrees.Count == 0);
        var tampered = new WorkflowRunState { Id = "", RunFolder = repo, Worktrees = { other.Root } };
        Check("I5d a state whose id is not a workflow id sweeps nothing", await WorkflowEngine.SweepWorktreesAsync(tampered) == 0 && Directory.Exists(other.Root));
        await other.DisposeAsync();
        await mine.DisposeAsync();
        await unrecorded.DisposeAsync();
        Check("I5e clean afterwards", await NoWorktreesLeft(repo));
    }

    // ─── I6: Resume reuses a finished agent's diff (from the cache) and merges it ───

    static async Task TestIsolatedResume()
    {
        string repo = await NewRepo("iso-resume", new() { ["a.txt"] = "a1\n", ["b.txt"] = "b1\n" });
        var (svc, mock, _) = NewService();
        var fast = Writes(new() { ["a"] = ("a.txt", "a2\n"), ["b"] = ("b.txt", "b2\n") });
        mock.Answer = async (c, ct) =>
        {
            if (c.Label == "b") await Task.Delay(Timeout.Infinite, ct);
            return await fast(c, ct);
        };
        string id = null;
        svc.Started += s => id ??= s.Id;
        svc.RunCommand = (_, _, _) => Task.FromResult((true, "ok"));
        var run = svc.RunAsync(PlanOf(IsoPlan(Agent("a") + "," + Agent("b"), "check")), LastJson, "orch-i6", default, repo);
        await WaitUntil(() => svc.Get(id)?.Agents.Any(a => a.Label == "a" && a.State == "done") == true);
        svc.Stop(id);
        await run;
        var stopped = svc.Get(id);
        Check("I6a stopped with a finished: its diff is in the cache, nothing merged yet",
            stopped.Status == "stopped" && stopped.Cache.Values.Any(c => c.Patch?.Contains("+a2") == true) && Read(repo, "a.txt") == "a1\n"
            && await NoWorktreesLeft(repo));
        int callsA = mock.Seen.Count(c => c.Label == "a");
        mock.Answer = fast;
        var st = await svc.ResumeAsync(id, default);
        Check("I6b Resume: a comes from the cache (not run again), b runs; both diffs merge",
            st?.Status == "done" && mock.Seen.Count(c => c.Label == "a") == callsA && st.Agents.Single(a => a.Label == "a").State == "cached"
            && st.Merges.All(m => m.Outcome == "merged") && Read(repo, "a.txt") == "a2\n" && Read(repo, "b.txt") == "b2\n",
            st?.Summary);
        Check("I6c no worktree left", await NoWorktreesLeft(repo));
    }

    // ─── I7: the plan's checks, the folder check, the tool ───

    static async Task TestIsolationPlanChecks()
    {
        string Err(string json) { WorkflowPlan.Parse(json, out var e); return e ?? ""; }
        Check("I7a a gate or merge without isolation is refused",
            Err($$"""{"phases":[{"title":"s","gate":"make","agents":[{{Agents(1)}}]}]}""").Contains("isolation"));
        Check("I7b an unknown isolation is refused", Err($$"""{"phases":[{"title":"s","isolation":"box","agents":[{{Agents(1)}}]}]}""").Contains("worktree"));
        Check("I7c a pipeline cannot be isolated",
            Err("""{"phases":[{"title":"p","isolation":"worktree","pipeline":{"items":["x"],"stages":[{"label":"s","prompt":"p"}]}}]}""").Length > 0);
        var plan = PlanOf(IsoPlan(Agent("a"), "dotnet build", "dotnet test"));
        Check("I7d an isolated agent becomes general; the plan lists the commands the engine will run",
            plan.Phases[0].Agents[0].Type == "general" && plan.UsesWorktrees && plan.Commands().SequenceEqual(new[] { "dotnet build", "dotnet test" }));
        var mixed = PlanOf($$"""{"phases":[{"title":"s","agents":[{"label":"x","prompt":"p","isolation":"worktree"},{"label":"y","prompt":"p"}]}]}""");
        Check("I7e isolation can be set on one agent only", mixed.Phases[0].IsIsolated(mixed.Phases[0].Agents[0]) && !mixed.Phases[0].IsIsolated(mixed.Phases[0].Agents[1])
            && mixed.Phases[0].Agents[1].Type == "explore");

        string plain = Path.Combine(Root, "not-a-repo");
        Directory.CreateDirectory(plain);
        Check("I7f a folder that is not a git repository is refused before anything starts, with why",
            (await WorkflowService.FolderRefusalAsync(plan, plain))?.Contains("not a git repository") == true
            && await WorkflowService.FolderRefusalAsync(plan, null) != null && await WorkflowService.FolderRefusalAsync(PlanOf($$"""{"phases":[{"title":"s","agents":[{{Agents(1)}}]}]}"""), plain) == null);
        var (svc, mock, _) = NewService();
        var st = await svc.RunAsync(plan, LastJson, "orch-i7", default, plain);
        Check("I7g the engine itself refuses it too (resume from a card): failed with why, no agent ran",
            st.Status == "failed" && st.StopReason.Contains("not a git repository") && mock.Calls == 0, st.StopReason);
    }

    static async Task TestIsolationThroughTool()
    {
        string repo = await NewRepo("iso-tool", new() { ["a.txt"] = "a1\n" });
        var (svc, mock, _) = NewService();
        svc.RunCommand = (_, _, _) => Task.FromResult((true, "ok"));
        mock.Answer = Writes(new() { ["a"] = ("a.txt", "a2\n") });
        var old = WorkflowService.Current;
        WorkflowService.Current = svc;
        const string chat = "orch-i8";
        AgentRunScope.Enter(new AgentRunScope(chat, repo));
        WorkflowGate.SetUltra(chat, true);
        var asked = new List<string>();
        Func<PermissionRequestInfo, Task<bool>> prompt = info => { lock (asked) asked.Add(info.Detail ?? ""); return Task.FromResult(true); };
        Svc.OnPermissionRequired += prompt;
        try
        {
            var r = await T("workflow", ("plan", IsoPlan(Agent("a"), "make check", "make test")));
            Check("I8a Ultra on, but a plan whose gate runs commands still asks, and the prompt names the commands",
                asked.Count == 1 && asked[0].Contains("`make check`") && asked[0].Contains("`make test`") && asked[0].Contains("worktrees"), string.Join(" | ", asked));
            Check("I8b the tool runs it in the chat's project folder and returns the merge report",
                r.Success && Seen(r).Contains("Merge of \"implement\"") && Seen(r).Contains("a: merged (a.txt)") && Read(repo, "a.txt") == "a2\n", Seen(r));
            Check("I8c the schema the model sees offers isolation, gate and merge",
                OrchestrationToolModule.Schemas().First().InputSchema.GetRawText() is var s && s.Contains("\"isolation\"") && s.Contains("\"gate\"") && s.Contains("\"merge\""));
            AgentRunScope.Enter(new AgentRunScope(chat, Path.Combine(Root, "not-a-repo")));
            int calls = mock.Calls;
            var no = await T("workflow", ("plan", IsoPlan(Agent("a"), "make check")));
            Check("I8d from a folder that is no repository: refused with why, no agent ran", !no.Success && Seen(no).Contains("not a git repository") && mock.Calls == calls, Seen(no));
        }
        finally
        {
            Svc.OnPermissionRequired -= prompt;
            AgentRunScope.Enter(null);
            WorkflowGate.Load(null);
            WorkflowService.Current = old;
        }
    }
}
