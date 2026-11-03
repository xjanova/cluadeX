using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Agents;

/// <summary>One agent's run as the engine asks for it. <paramref name="WorkingDirectory"/>: its own git worktree (isolation
/// "worktree"), where it must do all its work; null = the run folder.</summary>
public sealed record WorkflowAgentCall(string WorkflowId, string Key, string Phase, string Label, string Prompt, string Type, int Attempt,
    string SessionKey = "", string? WorkingDirectory = null);

/// <summary>What one agent answered and what it cost.</summary>
public sealed record WorkflowAgentResult(string Text, decimal CostUsd);

/// <summary>One agent of a workflow, as the card and the state file show it. Changed only under the run's lock.</summary>
public sealed class WorkflowAgentState
{
    public string Key { get; set; } = "";
    public string Phase { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>"agent", "stage" or "refuter".</summary>
    public string Kind { get; set; } = "agent";
    /// <summary>queued · running · checking (its gate runs) · done · failed · stopped · skipped (budget) · cached (from an
    /// earlier run).</summary>
    public string State { get; set; } = "queued";
    public decimal CostUsd { get; set; }
    public string Prompt { get; set; } = "";
    public string? Output { get; set; }
    public string? Error { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    /// <summary>Its worktree while it works (isolation "worktree"); null once removed.</summary>
    public string? Worktree { get; set; }
    /// <summary>An isolated agent: the files it changed (repository-relative).</summary>
    public List<string>? ChangedFiles { get; set; }
    /// <summary>An isolated agent's gate, as the engine ran it in its worktree: green · red · null (no gate, or no change).</summary>
    public string? Gate { get; set; }
    public string? GateOutput { get; set; }
    /// <summary>An isolated agent's work: <c>git diff seed</c> of its worktree (the cache keeps it for Resume).</summary>
    [JsonIgnore] public string? Patch { get; set; }
}

/// <summary>What the merge step did with one isolated agent's diff.</summary>
public sealed class WorkflowMergeEntry
{
    public string Phase { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>merged · conflict (no longer applies) · red-after-merge (green alone, red on the merged tree; taken back
    /// out) · gate-red (its own gate was red) · no-change · not-run (the agent did not finish).</summary>
    public string Outcome { get; set; } = "";
    public string? Detail { get; set; }
    public List<string> Files { get; set; } = new();
    /// <summary>The diff, kept for what was left out (the report shows it so it can be done by hand).</summary>
    public string? Patch { get; set; }
}

/// <summary>The full test command, run once on a phase's merged result.</summary>
public sealed class WorkflowTestRun
{
    public string Phase { get; set; } = "";
    public string Command { get; set; } = "";
    /// <summary>Null = not run (nothing was merged).</summary>
    public bool? Green { get; set; }
    public string Output { get; set; } = "";
    public int Merged { get; set; }
}

/// <summary>A refuted-or-kept claim of a verify step.</summary>
public sealed class WorkflowVerdict
{
    public string Phase { get; set; } = "";
    public string Claim { get; set; } = "";
    public int Refuted { get; set; }
    public int Votes { get; set; }
    public int Refuters { get; set; }
    /// <summary>More than half of the refuters refuted it.</summary>
    public bool Killed { get; set; }
}

/// <summary>A finished agent's answer, reused by Resume.</summary>
public sealed class WorkflowCached
{
    public string Output { get; set; } = "";
    public decimal CostUsd { get; set; }
    /// <summary>An isolated agent: its diff, files and gate (it is cached only once all three are known).</summary>
    public string? Patch { get; set; }
    public List<string>? Files { get; set; }
    public string? Gate { get; set; }
    public string? GateOutput { get; set; }
}

/// <summary>A workflow run: its plan, every agent, the cache Resume reads. Saved as DataRoot/workflows/&lt;id&gt;.json.</summary>
public sealed class WorkflowRunState
{
    public string Id { get; set; } = "wf-" + Guid.NewGuid().ToString("N")[..10];
    public string SessionKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string PlanJson { get; set; } = "";
    /// <summary>running · done · stopped · failed.</summary>
    public string Status { get; set; } = "running";
    public string? StopReason { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? EndedAt { get; set; }
    /// <summary>Seconds spent in earlier attempts (before a Resume).</summary>
    public double ElapsedBefore { get; set; }
    public decimal EstimateUsd { get; set; }
    public long EstimateRamMb { get; set; }
    public int EstimateAgents { get; set; }
    public List<WorkflowAgentState> Agents { get; set; } = new();
    public Dictionary<string, WorkflowCached> Cache { get; set; } = new();
    public List<WorkflowVerdict> Verdicts { get; set; } = new();
    /// <summary>What each phase produced (survivors of a verify), in order — the tool's result is built from it.</summary>
    public Dictionary<string, List<string>> PhaseItems { get; set; } = new();
    public string? Summary { get; set; }
    /// <summary>The project folder the run started in: worktrees are made from it and diffs merge back into it.</summary>
    public string? RunFolder { get; set; }
    /// <summary>Worktrees that exist right now — written before an agent starts in one, so a crash leaves a record the
    /// next start sweeps (<see cref="WorkflowEngine.SweepWorktreesAsync"/>).</summary>
    public List<string> Worktrees { get; set; } = new();
    public List<WorkflowMergeEntry> Merges { get; set; } = new();
    public List<WorkflowTestRun> Tests { get; set; } = new();

    internal readonly object Gate = new();

    public double ElapsedSeconds => ElapsedBefore + ((EndedAt ?? DateTime.Now) - StartedAt).TotalSeconds;
    public decimal SpentUsd { get { lock (Gate) return Agents.Sum(a => a.CostUsd); } }
    public bool IsRunning => Status == "running";
}

/// <summary>
/// Runs a <see cref="WorkflowPlan"/> deterministically on whatever runs one agent (the app: a native sub-agent; the
/// harness: a mock). Owns the workflow's concurrency, the machine-wide slot, the budget check before every start,
/// Stop (one token for every agent) and the cache that lets Resume skip what already finished.
/// An agents phase with isolation "worktree" gives each agent its own <see cref="WorktreeSandbox"/>; the engine itself
/// (not the agent's word) runs the phase's gate in it and keeps the diff, then the worktree goes. With merge, the green
/// diffs are applied to the run folder one by one (each checked by the gate on the merged tree; a red or conflicting one
/// is taken out and reported), and the full test command runs once on the result.
/// </summary>
public sealed class WorkflowEngine
{
    private readonly Func<WorkflowAgentCall, CancellationToken, Task<WorkflowAgentResult>> _runner;

    /// <summary>The most agent rows one run may have, items from earlier phases included (twice what a plan may declare).</summary>
    public const int HardMaxAgents = WorkflowPlan.MaxAgents * 2;

    /// <summary>Takes one machine-wide slot (the app: <see cref="AgentLoad.EnterAsync"/>); each needs one LeaveSlot.</summary>
    public Func<CancellationToken, Task> EnterSlot { get; init; } = AgentLoad.EnterAsync;
    public Action LeaveSlot { get; init; } = AgentLoad.Leave;
    /// <summary>The run's spend guard: true = start nothing more (each agent not started is "skipped").</summary>
    public Func<bool> BudgetSpent { get; init; } = () => false;
    /// <summary>Called after every finished agent (the app writes the state file).</summary>
    public Action<WorkflowRunState>? Save { get; init; }
    /// <summary>Runs a gate or test command in a folder: green = exit 0 (the app: the shell <see cref="ShellRunner"/> uses).</summary>
    public Func<string, string, CancellationToken, Task<(bool Green, string Output)>> RunCommand { get; init; } = DefaultRunCommandAsync;

    /// <summary>A gate or test command that runs longer than this is red.</summary>
    public const int CommandTimeoutMs = 600_000;

    /// <summary>git takes one lock for worktree add / remove: one at a time, across workflows.</summary>
    private static readonly SemaphoreSlim WorktreeLock = new(1, 1);

    /// <summary>A row changed (any thread). The card coalesces these.</summary>
    public event Action<WorkflowRunState, WorkflowAgentState>? RowChanged;

    public WorkflowEngine(Func<WorkflowAgentCall, CancellationToken, Task<WorkflowAgentResult>> runner) => _runner = runner;

    /// <summary>Run (or resume) <paramref name="st"/>. Never throws for a stop: the state says "stopped".</summary>
    public async Task RunAsync(WorkflowRunState st, WorkflowPlan plan, CancellationToken ct)
    {
        lock (st.Gate)
        {
            st.Status = "running";
            st.StopReason = null;
            st.EndedAt = null;
            st.StartedAt = DateTime.Now;
            st.Name = plan.Name;
            // A resume starts the rows again: what finished comes back as "cached" when its turn comes.
            st.Agents.Clear();
            st.Verdicts.Clear();
            st.PhaseItems.Clear();
            st.Merges.Clear();
            st.Tests.Clear();
        }
        using var sem = new SemaphoreSlim(plan.Concurrency);
        try
        {
            if (plan.UsesWorktrees && !await WorktreeSandbox.IsGitRepoAsync(st.RunFolder, ct))
                throw new InvalidOperationException((st.RunFolder is { Length: > 0 } f ? f : "The run's folder")
                    + " is not a git repository with a commit, so its agents cannot work in worktrees.");
            foreach (var phase in plan.Phases)
            {
                ct.ThrowIfCancellationRequested();
                List<string> items;
                if (phase.Agents is { Count: > 0 } agents)
                {
                    var jobs = agents.Select(spec => AgentThenVerifyAsync(st, phase, spec, sem, ct)).ToList();
                    items = (await WhenAllSettled(jobs)).SelectMany(x => x).ToList();
                    if (phase.Merge != null) await MergeAsync(st, phase, ct);
                }
                else if (phase.Pipeline is { } pipe)
                {
                    var source = pipe.Items is { Count: > 0 } given ? given : ItemsFrom(st, pipe.From!, pipe.Field);
                    var jobs = source.Select((item, i) => PipelineItemAsync(st, phase, pipe, item, i + 1, sem, ct)).ToList();
                    items = (await WhenAllSettled(jobs)).Where(x => x != null).Select(x => x!).ToList();
                }
                else
                {
                    var v = phase.Verify!;
                    items = await VerifyAsync(st, phase.Title, phase.Title, ItemsFrom(st, v.From!, v.Field), v, sem, ct);
                }
                lock (st.Gate) st.PhaseItems[phase.Title] = items;
            }
            Finish(st, "done", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finish(st, "stopped", st.StopReason ?? "stopped");
        }
        catch (Exception ex)
        {
            Finish(st, "failed", ex.Message);
        }
    }

    private void Finish(WorkflowRunState st, string status, string? reason)
    {
        List<WorkflowAgentState> touched;
        lock (st.Gate)
        {
            st.Status = status;
            st.StopReason = reason;
            st.EndedAt = DateTime.Now;
            touched = st.Agents.Where(a => a.State is "queued" or "running" or "checking").ToList();
            foreach (var a in touched) { a.State = "stopped"; a.EndedAt = DateTime.Now; }
            st.Summary = Summarize(st);
        }
        foreach (var a in touched) Raise(st, a);
        Save?.Invoke(st);
    }

    /// <summary>Every job to its end (a stop ends them all quickly), then the first stop rethrown.</summary>
    private static async Task<T[]> WhenAllSettled<T>(List<Task<T>> jobs)
    {
        try { return await Task.WhenAll(jobs); }
        catch
        {
            // WhenAll already waited for all of them; surface a cancellation as such.
            if (jobs.FirstOrDefault(j => j.IsCanceled) is { } c) await c;
            throw;
        }
    }

    private async Task<List<string>> AgentThenVerifyAsync(WorkflowRunState st, WorkflowPhase phase, WorkflowAgentSpec spec, SemaphoreSlim sem, CancellationToken ct)
    {
        string? output = await RunOneAsync(st, phase.Title, spec.Label, "agent", spec.Prompt, spec.Type, spec.Schema, sem, ct,
            phase.IsIsolated(spec), phase.Gate);
        if (output == null) return new();
        if (phase.Verify is not { } v) return new() { output };
        // No barrier: this agent's claims are refuted now, while the phase's other agents still work.
        var claims = Extract(output, v.Field);
        return await VerifyAsync(st, phase.Title, spec.Label, claims, v, sem, ct);
    }

    private async Task<string?> PipelineItemAsync(WorkflowRunState st, WorkflowPhase phase, WorkflowPipeline pipe, string item, int n,
        SemaphoreSlim sem, CancellationToken ct)
    {
        string? prev = null;
        foreach (var stage in pipe.Stages)
        {
            string prompt = Fill(stage.Prompt, item, prev);
            prev = await RunOneAsync(st, phase.Title, $"{stage.Label} · {n}", "stage", prompt, stage.Type, stage.Schema, sem, ct);
            if (prev == null) return null;   // a failed stage ends this item; the other items go on
        }
        return prev;
    }

    private async Task<List<string>> VerifyAsync(WorkflowRunState st, string phase, string source, List<string> claims, WorkflowVerify v,
        SemaphoreSlim sem, CancellationToken ct)
    {
        var jobs = claims.Select((claim, ci) => VerifyOneAsync(st, phase, source, claim, ci + 1, v, sem, ct)).ToList();
        var kept = await WhenAllSettled(jobs);
        return kept.Where(k => k != null).Select(k => k!).ToList();
    }

    private async Task<string?> VerifyOneAsync(WorkflowRunState st, string phase, string source, string claim, int ci, WorkflowVerify v,
        SemaphoreSlim sem, CancellationToken ct)
    {
        var votes = Enumerable.Range(1, v.Refuters).Select(r => RunOneAsync(st, phase, $"{source} · #{ci} refute {r}/{v.Refuters}", "refuter",
            RefutePrompt(claim, r, v), "explore", RefuterSchema, sem, ct)).ToList();
        var answers = await WhenAllSettled(votes);
        int refuted = 0, counted = 0;
        foreach (var a in answers)
        {
            if (a == null) continue;
            counted++;
            try
            {
                using var doc = JsonDocument.Parse(a);
                if (doc.RootElement.TryGetProperty("refuted", out var r) && r.ValueKind == JsonValueKind.True) refuted++;
            }
            catch (JsonException) { }
        }
        // Majority kills: more than half of the refuters asked (an abstaining one does not save the claim either way).
        bool killed = refuted * 2 > v.Refuters;
        lock (st.Gate)
            st.Verdicts.Add(new WorkflowVerdict { Phase = phase, Claim = claim, Refuted = refuted, Votes = counted, Refuters = v.Refuters, Killed = killed });
        return killed ? null : claim;
    }

    /// <summary>One agent: from the cache, or under the workflow's concurrency, the machine's slot and the budget.
    /// Null = it did not produce an answer (failed, skipped); a stop throws. <paramref name="isolated"/>: it works in a
    /// worktree of its own, made once it has its slot and removed when it ends; its diff and <paramref name="gate"/>'s
    /// verdict land on its row (and in the cache only together with its answer).</summary>
    private async Task<string?> RunOneAsync(WorkflowRunState st, string phase, string label, string kind, string prompt, string type,
        JsonElement? schema, SemaphoreSlim sem, CancellationToken ct, bool isolated = false, string? gate = null)
    {
        // An isolated agent's cache entry holds its diff and gate: a plan that changes either runs it again.
        string key = KeyOf(phase, label, isolated ? prompt + "\u001fworktree\u001f" + gate : prompt, schema);
        var row = new WorkflowAgentState { Key = key, Phase = phase, Label = label, Kind = kind, Prompt = prompt };
        WorkflowCached? cached;
        bool overLimit;
        lock (st.Gate)
        {
            // The plan's check counts what it can see; items an earlier phase produces (a pipeline or verify "from") are
            // only known now. Past the ceiling, nothing more starts.
            overLimit = st.Agents.Count >= HardMaxAgents;
            st.Agents.Add(row);
            st.Cache.TryGetValue(key, out cached);
            if (cached != null)
            {
                row.State = "cached"; row.Output = cached.Output; row.EndedAt = DateTime.Now;
                row.Patch = cached.Patch; row.ChangedFiles = cached.Files; row.Gate = cached.Gate; row.GateOutput = cached.GateOutput;
            }
            else if (overLimit) { row.State = "skipped"; row.Error = $"agent limit ({HardMaxAgents})"; row.EndedAt = DateTime.Now; }
        }
        Raise(st, row);
        if (cached != null) return cached.Output;
        if (overLimit) return null;

        try { await sem.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            Set(st, row, r => { r.State = "stopped"; r.EndedAt = DateTime.Now; });
            throw;
        }
        try
        {
            // A Stop releases the running agents' places as it cancels them, and a queued agent can be handed one before its
            // own wait sees the stop: it must not start then.
            ct.ThrowIfCancellationRequested();
            if (BudgetSpent())
            {
                Set(st, row, r => { r.State = "skipped"; r.Error = "budget"; r.EndedAt = DateTime.Now; });
                return null;
            }
            await EnterSlot(ct);
            WorktreeSandbox? box = null;
            try
            {
                Set(st, row, r => { r.State = "running"; r.StartedAt = DateTime.Now; });
                if (isolated)
                {
                    box = await CreateWorktreeAsync(st, ct);
                    Set(st, row, r => r.Worktree = box.Root);
                }
                string? wd = box?.WorkingDirectory;
                string full = prompt + SchemaInstruction(schema) + (box != null ? WorktreeInstruction(box.WorkingDirectory) : "");
                var res = await _runner(new WorkflowAgentCall(st.Id, key, phase, label, full, type, 1, st.SessionKey, wd), ct);
                decimal cost = res.CostUsd;
                string? answer = res.Text;
                string? problem = null;
                if (schema is { } sch && !Conforms(res.Text, sch, out answer, out problem))
                {
                    // One repair turn: the agent sees what was wrong with its answer.
                    var again = await _runner(new WorkflowAgentCall(st.Id, key, phase, label,
                        full + "\n\nYour previous answer did not match the required JSON (" + problem + "). Previous answer:\n"
                        + Clip(res.Text, 4000) + "\n\nAnswer again with ONLY the JSON object.", type, 2, st.SessionKey, wd), ct);
                    cost += again.CostUsd;
                    if (!Conforms(again.Text, sch, out answer, out problem)) answer = null;
                }
                if (answer == null)
                {
                    Set(st, row, r => { r.State = "failed"; r.CostUsd = cost; r.Error = "answer did not match the schema: " + problem; r.Output = res.Text; r.EndedAt = DateTime.Now; });
                    return null;
                }
                var entry = new WorkflowCached { Output = answer, CostUsd = cost };
                if (box != null)
                {
                    // The engine looks at the worktree itself: what changed, and whether the gate passes there.
                    entry.Patch = await box.DiffAsync(ct);
                    entry.Files = await box.ChangedFilesAsync(ct);
                    if (gate != null && entry.Files.Count > 0)
                    {
                        Set(st, row, r => { r.State = "checking"; r.CostUsd = cost; r.Output = answer; });
                        var (green, outText) = await RunCommand(gate, box.WorkingDirectory, ct);
                        entry.Gate = green ? "green" : "red";
                        entry.GateOutput = SpeculativeFixer.TrimOutput(outText, 3000);
                    }
                }
                Set(st, row, r =>
                {
                    r.State = "done"; r.CostUsd = cost; r.Output = answer; r.EndedAt = DateTime.Now;
                    r.Patch = entry.Patch; r.ChangedFiles = entry.Files; r.Gate = entry.Gate; r.GateOutput = entry.GateOutput;
                });
                lock (st.Gate) st.Cache[key] = entry;
                Save?.Invoke(st);
                return answer;
            }
            finally
            {
                LeaveSlot();
                if (box != null) await RemoveWorktreeAsync(st, row, box);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Set(st, row, r => { r.State = "stopped"; r.EndedAt = DateTime.Now; });
            throw;
        }
        catch (Exception ex)
        {
            Set(st, row, r => { r.State = "failed"; r.Error = ex.Message; r.EndedAt = DateTime.Now; });
            return null;
        }
        finally { sem.Release(); }
    }

    private void Set(WorkflowRunState st, WorkflowAgentState row, Action<WorkflowAgentState> change)
    {
        lock (st.Gate) change(row);
        Raise(st, row);
    }

    private void Raise(WorkflowRunState st, WorkflowAgentState row)
    {
        try { RowChanged?.Invoke(st, row); } catch { /* a view must not break the run */ }
    }

    // ─── worktrees, gate, merge ───

    /// <summary>The app's command runner for gates and tests: its shell, ten minutes at most.</summary>
    public static async Task<(bool Green, string Output)> DefaultRunCommandAsync(string command, string cwd, CancellationToken ct)
    {
        var r = await ShellRunner.RunAsync(ShellRunner.DefaultShell, command, cwd, CommandTimeoutMs, ct);
        if (r.Cancelled) ct.ThrowIfCancellationRequested();
        string output = r.TimedOut ? r.Output + $"\n(timed out after {CommandTimeoutMs / 60_000} min)" : r.Output;
        return (r.ExitCode == 0 && !r.TimedOut && !r.Cancelled, output);
    }

    /// <summary>This workflow's worktree label: its folders are <c>&lt;repo&gt;-&lt;id&gt;-&lt;n&gt;-…</c> in the worktree home.</summary>
    private static string WorktreeLabel(WorkflowRunState st) => st.Id + "-";

    private static int _worktreeCounter;

    private async Task<WorktreeSandbox> CreateWorktreeAsync(WorkflowRunState st, CancellationToken ct)
    {
        string folder = st.RunFolder ?? throw new InvalidOperationException("this workflow has no project folder to make worktrees from");
        await WorktreeLock.WaitAsync(ct);
        try
        {
            var box = await WorktreeSandbox.CreateAsync(folder, WorktreeLabel(st) + Interlocked.Increment(ref _worktreeCounter), ct);
            lock (st.Gate) st.Worktrees.Add(box.Root);
            Save?.Invoke(st);
            return box;
        }
        finally { WorktreeLock.Release(); }
    }

    private async Task RemoveWorktreeAsync(WorkflowRunState st, WorkflowAgentState row, WorktreeSandbox box)
    {
        await WorktreeLock.WaitAsync(CancellationToken.None);
        try { await box.DisposeAsync(); }
        finally { WorktreeLock.Release(); }
        Set(st, row, r => r.Worktree = null);
        lock (st.Gate) st.Worktrees.Remove(box.Root);
        Save?.Invoke(st);
    }

    private static readonly Regex WorkflowIdRx = new("^wf-[0-9a-f]{10}$", RegexOptions.Compiled);

    /// <summary>Remove every worktree this workflow left behind (an app crash, a stop inside <c>git worktree add</c>): the
    /// ones its state names and any folder of its own name in the worktree home. Only folders named after this workflow,
    /// directly in that home, are touched — a state file naming any other path is ignored. Returns how many it removed.</summary>
    public static async Task<int> SweepWorktreesAsync(WorkflowRunState st)
    {
        List<string> listed;
        lock (st.Gate) listed = st.Worktrees.ToList();
        if (!WorkflowIdRx.IsMatch(st.Id ?? "")) return 0;
        string? repo = await WorktreeSandbox.RepoRootOfAsync(st.RunFolder ?? "", CancellationToken.None);
        if (repo == null) return 0;
        string home = Path.GetFullPath(WorktreeSandbox.HomeFor(repo)).TrimEnd('\\', '/');
        string prefix = Path.GetFileName(repo) + "-" + WorktreeLabel(st);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in listed) try { candidates.Add(Path.GetFullPath(p).TrimEnd('\\', '/')); } catch { }
        try { if (Directory.Exists(home)) foreach (var d in Directory.EnumerateDirectories(home)) candidates.Add(Path.GetFullPath(d)); } catch { }
        int removed = 0;
        await WorktreeLock.WaitAsync(CancellationToken.None);
        try
        {
            foreach (var path in candidates)
            {
                if (!string.Equals(Path.GetDirectoryName(path), home, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                bool there = Directory.Exists(path);
                await WorktreeSandbox.RemoveAsync(repo, path);
                if (there && !Directory.Exists(path)) removed++;
            }
        }
        finally { WorktreeLock.Release(); }
        lock (st.Gate) st.Worktrees.Clear();
        return removed;
    }

    private static string WorktreeInstruction(string folder) =>
        "\n\nYou work in your own copy of the project (a git worktree): " + folder + " — read, edit and run commands only "
        + "there, and do not commit. Other agents work in their own copies at the same time. When you end, your change is "
        + "checked and merged for you.";

    /// <summary>The merge step of <paramref name="phase"/>: its isolated agents' diffs, in plan order, into the run folder.
    /// Each one is applied (checked first), then the gate runs on the merged tree; a diff that no longer applies, or that
    /// turns the merged tree red, is left out (taken back out) and reported. Then the full test runs once.</summary>
    private async Task MergeAsync(WorkflowRunState st, WorkflowPhase phase, CancellationToken ct)
    {
        string folder = st.RunFolder!;
        int merged = 0;
        foreach (var spec in phase.Agents!.Where(phase.IsIsolated))
        {
            ct.ThrowIfCancellationRequested();
            WorkflowAgentState? row;
            lock (st.Gate) row = st.Agents.LastOrDefault(a => a.Phase == phase.Title && a.Kind == "agent" && a.Label == spec.Label);
            var e = new WorkflowMergeEntry { Phase = phase.Title, Label = spec.Label, Files = row?.ChangedFiles?.ToList() ?? new() };
            string patch = row?.Patch ?? "";
            if (row == null || row.State is not ("done" or "cached"))
            { e.Outcome = "not-run"; e.Detail = row?.Error ?? row?.State ?? "did not run"; }
            else if (patch.Trim().Length == 0) e.Outcome = "no-change";
            else if (row.Gate == "red")
            { e.Outcome = "gate-red"; e.Detail = FirstLine(row.GateOutput); e.Patch = patch; }
            else if (await WorktreeSandbox.IsAppliedAsync(folder, patch, CancellationToken.None))
            { e.Outcome = "merged"; e.Detail = "already in the project (merged by an earlier attempt)"; merged++; }
            else if (await WorktreeSandbox.ApplyAsync(folder, patch, CancellationToken.None) is { } conflict)
            { e.Outcome = "conflict"; e.Detail = conflict; e.Patch = patch; }
            else if (phase.Gate is { } check)
            {
                (bool Green, string Output) r;
                try { r = await RunCommand(check, folder, ct); }
                catch (OperationCanceledException)
                {
                    // Stopped before the merged tree was checked: the diff does not stay in the project unverified.
                    await WorktreeSandbox.RevertAsync(folder, patch, CancellationToken.None);
                    throw;
                }
                if (r.Green) { e.Outcome = "merged"; merged++; }
                else
                {
                    string? back = await WorktreeSandbox.RevertAsync(folder, patch, CancellationToken.None);
                    e.Outcome = "red-after-merge";
                    e.Detail = FirstLine(r.Output) + (back != null ? " — and it could not be taken back out: " + back : "");
                    e.Patch = patch;
                }
            }
            else { e.Outcome = "merged"; merged++; }
            lock (st.Gate) st.Merges.Add(e);
            Save?.Invoke(st);
        }
        ct.ThrowIfCancellationRequested();
        string? test = phase.Merge!.Test ?? phase.Gate;
        if (test == null) return;
        var run = new WorkflowTestRun { Phase = phase.Title, Command = test, Merged = merged };
        if (merged > 0)
        {
            var (green, output) = await RunCommand(test, folder, ct);
            run.Green = green;
            run.Output = SpeculativeFixer.TrimOutput(output, 3000);
        }
        else run.Output = "nothing was merged, so nothing was tested";
        lock (st.Gate) st.Tests.Add(run);
        Save?.Invoke(st);
    }

    private static readonly Regex ErrorLine = new(@"\b(error|failed|failure|exception|assert)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string FirstLine(string? s)
    {
        var lines = (s ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        string line = lines.FirstOrDefault(l => ErrorLine.IsMatch(l)) ?? lines.FirstOrDefault() ?? "";
        return line.Length > 200 ? line[..200] + "…" : line;
    }

    // ─── items, prompts, schema ───

    /// <summary>The items an earlier phase left, with <paramref name="field"/> taken out of each answer that has it.</summary>
    private static List<string> ItemsFrom(WorkflowRunState st, string phase, string? field)
    {
        List<string> outputs;
        lock (st.Gate) outputs = st.PhaseItems.TryGetValue(phase, out var list) ? list.ToList() : new();
        return outputs.SelectMany(o => Extract(o, field)).ToList();
    }

    /// <summary>An answer's claims: the array at <paramref name="field"/> (each element as text), a top-level array's
    /// elements, or the answer itself. Public for the harness.</summary>
    public static List<string> Extract(string output, string? field)
    {
        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            JsonElement arr = default;
            if (!string.IsNullOrWhiteSpace(field) && root.ValueKind == JsonValueKind.Object && root.TryGetProperty(field, out var f)
                && f.ValueKind == JsonValueKind.Array) arr = f;
            else if (root.ValueKind == JsonValueKind.Array) arr = root;
            if (arr.ValueKind == JsonValueKind.Array)
                return arr.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText())
                          .Where(s => s.Length > 0).ToList();
        }
        catch (JsonException) { }
        return new() { output };
    }

    private static string Fill(string template, string item, string? prev)
    {
        string s = template.Replace("{{item}}", item).Replace("{{prev}}", prev ?? "");
        // A stage that names neither still needs its input.
        if (!template.Contains("{{item}}") && !template.Contains("{{prev}}"))
            s += "\n\nInput:\n" + (prev ?? item);
        return s;
    }

    private static string RefutePrompt(string claim, int n, WorkflowVerify v) =>
        "You are an adversarial verifier. Another agent made the claim below. Try hard to REFUTE it: check the code, the "
        + "files and the facts yourself; do not trust the claim's own reasoning. Refute it only with concrete evidence "
        + "(file:line, exact text) that it is wrong or does not hold; if you cannot, it stands."
        + (n > 1 ? $" You are refuter {n}: look at it from a different angle than the obvious one." : "")
        + (string.IsNullOrWhiteSpace(v.Prompt) ? "" : "\n\n" + v.Prompt!.Trim())
        + "\n\nClaim:\n" + claim;

    /// <summary>What every refuter answers.</summary>
    public static readonly JsonElement RefuterSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"refuted":{"type":"boolean"},"reason":{"type":"string"}},"required":["refuted","reason"]}""").RootElement.Clone();

    private static string SchemaInstruction(JsonElement? schema) => schema is { } s
        ? "\n\nEnd your work with your answer as ONE JSON object and nothing else (no prose around it), matching this JSON schema:\n" + s.GetRawText()
        : "";

    /// <summary>Does <paramref name="text"/> hold one JSON object that matches <paramref name="schema"/> (required
    /// properties present, top-level types right)? <paramref name="json"/>: the object alone. Public for the harness.</summary>
    public static bool Conforms(string text, JsonElement schema, out string? json, out string? problem)
    {
        json = null;
        problem = null;
        string t = (text ?? "").Trim();
        int a = t.IndexOf('{'), b = t.LastIndexOf('}');
        if (a < 0 || b <= a) { problem = "no JSON object"; return false; }
        string candidate = t[a..(b + 1)];
        try
        {
            using var doc = JsonDocument.Parse(candidate);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { problem = "not an object"; return false; }
            if (schema.ValueKind == JsonValueKind.Object)
            {
                if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                    foreach (var r in req.EnumerateArray())
                        if (r.GetString() is { } name && !root.TryGetProperty(name, out _)) { problem = $"missing \"{name}\""; return false; }
                if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                    foreach (var p in props.EnumerateObject())
                        if (root.TryGetProperty(p.Name, out var val) && p.Value.TryGetProperty("type", out var ty) && ty.GetString() is { } want
                            && !KindMatches(val, want))
                        { problem = $"\"{p.Name}\" should be {want}"; return false; }
            }
            json = candidate;
            return true;
        }
        catch (JsonException ex) { problem = "invalid JSON: " + ex.Message; return false; }
    }

    private static bool KindMatches(JsonElement v, string type) => type switch
    {
        "string" => v.ValueKind == JsonValueKind.String,
        "number" => v.ValueKind == JsonValueKind.Number,
        "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
        "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => v.ValueKind == JsonValueKind.Array,
        "object" => v.ValueKind == JsonValueKind.Object,
        "null" => v.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    public static string KeyOf(string phase, string label, string prompt, JsonElement? schema)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(phase + "\u001f" + label + "\u001f" + prompt + "\u001f" + (schema?.GetRawText() ?? "")));
        return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>One line per phase: what it produced, what the verifiers killed, the spend.</summary>
    private static string Summarize(WorkflowRunState st)
    {
        var sb = new StringBuilder();
        int done = st.Agents.Count(a => a.State is "done" or "cached"), failed = st.Agents.Count(a => a.State == "failed"),
            skipped = st.Agents.Count(a => a.State == "skipped" && a.Error == "budget"), stopped = st.Agents.Count(a => a.State == "stopped"),
            limited = st.Agents.Count(a => a.State == "skipped" && a.Error != "budget");
        sb.Append($"{st.Name}: {st.Status} · {st.Agents.Count} agent(s): {done} done");
        if (failed > 0) sb.Append($", {failed} failed");
        if (skipped > 0) sb.Append($", {skipped} skipped (budget)");
        if (limited > 0) sb.Append($", {limited} skipped (agent limit {HardMaxAgents})");
        if (stopped > 0) sb.Append($", {stopped} stopped");
        sb.Append($" · ${st.Agents.Sum(a => a.CostUsd):0.00}");
        int killed = st.Verdicts.Count(v => v.Killed);
        if (st.Verdicts.Count > 0) sb.Append($" · verify: {st.Verdicts.Count - killed} kept, {killed} refuted");
        if (st.Merges.Count > 0)
            sb.Append(" · merge: " + string.Join(", ", st.Merges.GroupBy(m => m.Outcome).Select(g => $"{g.Count()} {g.Key}")));
        foreach (var t in st.Tests)
            sb.Append(t.Green switch
            {
                true => $" · tests on the merged result: green (`{t.Command}`)",
                false => $" · tests on the merged result: RED (`{t.Command}`)",
                _ => " · tests: not run (nothing merged)",
            });
        return sb.ToString();
    }
}
