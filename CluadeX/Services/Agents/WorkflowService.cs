using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace CluadeX.Services.Agents;

/// <summary>
/// The app's workflows: starts one for the <c>workflow</c> tool (the call waits for it), stops one, resumes one from its
/// card after a stop or an app restart (from the cached results in its state file), and tells the chat page what
/// changed. The agent runner comes from <see cref="CodeAgentService"/> (a native sub-agent), set at startup — this
/// class holds no reference to it, so the tool module that uses it needs nothing that needs the agent.
/// </summary>
public sealed class WorkflowService
{
    /// <summary>The app's instance (the tool module and the chat bridge reach it here).</summary>
    public static WorkflowService? Current { get; set; }

    private readonly string? _dir;
    private readonly ConcurrentDictionary<string, Active> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WorkflowRunState> _known = new(StringComparer.Ordinal);

    private sealed record Active(WorkflowRunState State, CancellationTokenSource Cts, Task Done);

    /// <summary>Runs one agent (set by CodeAgentService: a native sub-agent under the calling run's scope).</summary>
    public Func<WorkflowAgentCall, CancellationToken, Task<WorkflowAgentResult>>? Runner { get; set; }
    /// <summary>Why agents cannot run now (no provider with native tools), or null.</summary>
    public Func<string?>? RunnerRefusal { get; set; }
    /// <summary>The calling run's spend guard (read in the run's own flow).</summary>
    public Func<bool> BudgetSpent { get; set; } = () => false;
    /// <summary>What the calling run may still spend (null = no guard set).</summary>
    public Func<decimal?> BudgetLeft { get; set; } = () => null;
    /// <summary>The average cost of one agent, for the estimate.</summary>
    public decimal PerAgentUsd { get; set; } = 0.05m;
    /// <summary>The machine's slot (tests replace it).</summary>
    public Func<CancellationToken, Task> EnterSlot { get; set; } = AgentLoad.EnterAsync;
    public Action LeaveSlot { get; set; } = AgentLoad.Leave;
    public Func<int> MachineCap { get; set; } = AgentLoad.CurrentCap;
    /// <summary>Runs a gate or merge test in a folder (null = the app's shell; the harness scripts it).</summary>
    public Func<string, string, CancellationToken, Task<(bool Green, string Output)>>? RunCommand { get; set; }

    public event Action<WorkflowRunState>? Started;
    public event Action<WorkflowRunState, WorkflowAgentState>? RowChanged;
    public event Action<WorkflowRunState>? Finished;

    /// <summary><paramref name="dataRoot"/> null = nothing is written (the harness's in-memory runs).</summary>
    public WorkflowService(string? dataRoot)
    {
        if (dataRoot != null)
        {
            _dir = Path.Combine(dataRoot, "workflows");
            try { Directory.CreateDirectory(_dir); } catch { _dir = null; }
        }
    }

    public sealed record Estimate(int Agents, decimal Usd, long RamMb, int AtOnce);

    public Estimate EstimateFor(WorkflowPlan plan)
    {
        int agents = plan.EstimateAgents();
        int atOnce = Math.Max(1, Math.Min(plan.Concurrency, MachineCap()));
        return new Estimate(agents, agents * PerAgentUsd, atOnce * AgentLoad.RamPerAgentMb, atOnce);
    }

    public bool IsActive(string id) => _active.ContainsKey(id);

    /// <summary>The workflow's state (live, or read from its file). Null = unknown.</summary>
    public WorkflowRunState? Get(string id)
    {
        if (_active.TryGetValue(id, out var a)) return a.State;
        if (_known.TryGetValue(id, out var k)) return k;
        if (_dir == null || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        try
        {
            string file = Path.Combine(_dir, id + ".json");
            if (!File.Exists(file)) return null;
            var st = JsonSerializer.Deserialize<WorkflowRunState>(File.ReadAllText(file));
            if (st == null) return null;
            // Read back while the app was not running it: it is not running now, whatever the file says.
            if (st.Status == "running") { st.Status = "stopped"; st.StopReason = "app closed"; }
            return _known.GetOrAdd(id, st);
        }
        catch { return null; }
    }

    /// <summary>At startup: every state file left "running" by a closed app says "stopped (app closed)" — its card
    /// offers Resume. Returns how many.</summary>
    public int MarkInterrupted()
    {
        if (_dir == null) return 0;
        int n = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_dir, "wf-*.json"))
            {
                try
                {
                    var st = JsonSerializer.Deserialize<WorkflowRunState>(File.ReadAllText(file));
                    if (st?.Status != "running" || _active.ContainsKey(st.Id)) continue;
                    st.Status = "stopped";
                    st.StopReason = "app closed";
                    st.EndedAt ??= DateTime.Now;
                    foreach (var a in st.Agents.Where(a => a.State is "queued" or "running" or "checking")) a.State = "stopped";
                    Write(st);
                    n++;
                }
                catch { }
            }
        }
        catch { }
        return n;
    }

    /// <summary>At startup, after <see cref="MarkInterrupted"/>: remove the worktrees a closed (or crashed) app left
    /// behind for workflows that are not running. Returns how many were removed.</summary>
    public async Task<int> SweepLeftWorktreesAsync()
    {
        if (_dir == null) return 0;
        int n = 0;
        List<string> files;
        try { files = Directory.EnumerateFiles(_dir, "wf-*.json").ToList(); } catch { return 0; }
        foreach (var file in files)
        {
            try
            {
                var st = JsonSerializer.Deserialize<WorkflowRunState>(File.ReadAllText(file));
                if (st == null || _active.ContainsKey(st.Id) || st.Worktrees.Count == 0) continue;
                n += await WorkflowEngine.SweepWorktreesAsync(st);
                if (_known.TryGetValue(st.Id, out var known)) lock (known.Gate) known.Worktrees.Clear();
                Write(st);
            }
            catch { }
        }
        return n;
    }

    /// <summary>Why <paramref name="plan"/> cannot run in <paramref name="runFolder"/>, or null: agents in worktrees need
    /// the folder to be a git repository with a commit.</summary>
    public static async Task<string?> FolderRefusalAsync(WorkflowPlan plan, string? runFolder, CancellationToken ct = default)
    {
        if (!plan.UsesWorktrees) return null;
        if (string.IsNullOrWhiteSpace(runFolder))
            return "This workflow runs agents in worktrees (isolation \"worktree\"), but no project folder is open. Open the project first.";
        return await WorktreeSandbox.IsGitRepoAsync(runFolder, ct) ? null
            : $"This workflow runs agents in worktrees (isolation \"worktree\"), but {runFolder} is not a git repository with a commit. "
              + "Leave isolation out (the agents then share the folder), or the user can make it a repository first.";
    }

    /// <summary>Run a new workflow for chat <paramref name="sessionKey"/> and wait for its end (the tool's call).
    /// <paramref name="ct"/> is the turn's: stopping the turn stops the workflow. <paramref name="runFolder"/>: the project
    /// its worktrees come from and its merges go to.</summary>
    public async Task<WorkflowRunState> RunAsync(WorkflowPlan plan, string planJson, string sessionKey, CancellationToken ct,
        string? runFolder = null)
    {
        var est = EstimateFor(plan);
        var st = new WorkflowRunState
        {
            SessionKey = sessionKey, Name = plan.Name, PlanJson = planJson, RunFolder = runFolder,
            EstimateAgents = est.Agents, EstimateUsd = est.Usd, EstimateRamMb = est.RamMb,
        };
        await RunStateAsync(st, plan, ct);
        return st;
    }

    /// <summary>Why <paramref name="id"/> cannot be resumed now, or null when it can.</summary>
    public string? ResumeRefusal(string id)
    {
        var st = Get(id);
        if (st == null) return $"There is no workflow {id}.";
        if (_active.ContainsKey(id) || st.Status == "running") return $"Workflow {id} is already running.";
        if (st.Status == "done") return $"Workflow {id} already finished.";
        return WorkflowPlan.Parse(st.PlanJson, out var err) == null ? "Its plan cannot be read: " + err : null;
    }

    /// <summary>Resume a stopped workflow: what finished is reused from its cache, the rest runs. From the card it is not
    /// tied to a turn (the card's Stop stops it); from the tool, <paramref name="ct"/> is the turn's. Null when it cannot
    /// be resumed (<see cref="ResumeRefusal"/>) — a second click while it runs is one of those.</summary>
    public async Task<WorkflowRunState?> ResumeAsync(string id, CancellationToken ct)
    {
        if (ResumeRefusal(id) != null) return null;
        var st = Get(id)!;
        var plan = WorkflowPlan.Parse(st.PlanJson, out _)!;
        try { await RunStateAsync(st, plan, ct); }
        catch (InvalidOperationException) { return null; }   // a second Resume raced this one in
        return st;
    }

    /// <summary>Stop a running workflow (every agent). False when it is not running.</summary>
    public bool Stop(string id, string reason = "stopped")
    {
        if (!_active.TryGetValue(id, out var a)) return false;
        lock (a.State.Gate) a.State.StopReason = reason;
        try { a.Cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    /// <summary>Stop every workflow of a chat (the chat was cleared or deleted). Returns how many.</summary>
    public int StopForSession(string sessionKey) =>
        _active.Values.Where(a => a.State.SessionKey == sessionKey).Count(a => Stop(a.State.Id));

    private async Task RunStateAsync(WorkflowRunState st, WorkflowPlan plan, CancellationToken ct)
    {
        var runner = Runner ?? throw new InvalidOperationException("No agent runner is connected.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_active.TryAdd(st.Id, new Active(st, cts, done.Task)))
            throw new InvalidOperationException("This workflow is already running.");
        _known[st.Id] = st;
        // A resume: the time the earlier attempts took stays on the card's clock.
        lock (st.Gate)
            if (st.EndedAt is { } ended) st.ElapsedBefore += Math.Max(0, (ended - st.StartedAt).TotalSeconds);
        var engine = new WorkflowEngine(runner)
        {
            EnterSlot = EnterSlot, LeaveSlot = LeaveSlot, BudgetSpent = BudgetSpent, Save = Write,
            RunCommand = RunCommand ?? WorkflowEngine.DefaultRunCommandAsync,
        };
        engine.RowChanged += (s, r) => { try { RowChanged?.Invoke(s, r); } catch { } };
        RunActivity.WorkflowStarted(st.SessionKey, st.Id);
        try
        {
            try { Started?.Invoke(st); } catch { }
            // A resume after a crash: what the last attempt left on disk goes first.
            if (st.Worktrees.Count > 0) await WorkflowEngine.SweepWorktreesAsync(st);
            Write(st);
            await engine.RunAsync(st, plan, cts.Token);
        }
        finally
        {
            // Every worktree goes with the run, however it ended (one a stop cut off inside `git worktree add` included).
            if (plan.UsesWorktrees) try { await WorkflowEngine.SweepWorktreesAsync(st); } catch { }
            _active.TryRemove(st.Id, out _);
            done.TrySetResult();
            RunActivity.WorkflowEnded(st.SessionKey, st.Id);
            Write(st);
            try { Finished?.Invoke(st); } catch { }
        }
    }

    private readonly object _writeLock = new();

    private void Write(WorkflowRunState st)
    {
        if (_dir == null) return;
        try
        {
            string json;
            lock (st.Gate) json = JsonSerializer.Serialize(st);
            lock (_writeLock) File.WriteAllText(Path.Combine(_dir, st.Id + ".json"), json);
        }
        catch { /* the state file is for Resume; a run goes on without it */ }
    }

    /// <summary>The tool's answer: the summary, each phase's survivors (the last phase in full), the refuted claims.</summary>
    public static string Report(WorkflowRunState st, int maxChars = 20_000)
    {
        var sb = new System.Text.StringBuilder();
        lock (st.Gate)
        {
            sb.AppendLine(st.Summary ?? st.Name);
            if (st.Status == "stopped")
                sb.AppendLine($"Stopped ({st.StopReason}). What finished is kept: the owner can press Resume on the workflow card, or call workflow again with resume_id \"{st.Id}\".");
            foreach (var (phase, items) in st.PhaseItems)
            {
                sb.AppendLine().AppendLine($"## {phase} — {items.Count} result(s)");
                foreach (var item in items) sb.AppendLine("- " + item.Trim());
            }
            var killed = st.Verdicts.Where(v => v.Killed).ToList();
            if (killed.Count > 0)
            {
                sb.AppendLine().AppendLine($"## Refuted by the verifiers ({killed.Count}) — do not act on these");
                foreach (var v in killed) sb.AppendLine($"- ({v.Refuted}/{v.Refuters}) " + v.Claim.Trim());
            }
            foreach (var phase in st.Merges.Select(m => m.Phase).Concat(st.Tests.Select(t => t.Phase)).Distinct().ToList())
            {
                sb.AppendLine().AppendLine($"## Merge of \"{phase}\" into the project");
                foreach (var m in st.Merges.Where(m => m.Phase == phase))
                {
                    string files = m.Files.Count > 0 ? " (" + string.Join(", ", m.Files) + ")" : "";
                    sb.AppendLine(m.Outcome switch
                    {
                        "merged" => $"- {m.Label}: merged{files}" + (m.Detail != null ? " — " + m.Detail : ""),
                        "conflict" => $"- {m.Label}: NOT merged — its diff no longer applies on top of the others{files}: {m.Detail}",
                        "red-after-merge" => $"- {m.Label}: NOT merged — green on its own, but the merged tree went red, so it was taken back out{files}: {m.Detail}",
                        "gate-red" => $"- {m.Label}: NOT merged — its gate was red in its own worktree{files}: {m.Detail}",
                        "no-change" => $"- {m.Label}: changed nothing",
                        _ => $"- {m.Label}: NOT merged — the agent did not finish ({m.Detail})",
                    });
                    if (m.Patch is { Length: > 0 } p)
                        sb.AppendLine("  ```diff").AppendLine(p.Length > 4000 ? p[..4000] + "\n… (diff cut)" : p.TrimEnd()).AppendLine("  ```");
                }
                foreach (var t in st.Tests.Where(t => t.Phase == phase))
                {
                    if (t.Green == null) { sb.AppendLine($"Tests on the merged result: not run — {t.Output}."); continue; }
                    sb.AppendLine($"Tests on the merged result ({t.Merged} diff(s) merged): `{t.Command}` → {(t.Green == true ? "GREEN" : "RED")}");
                    if (t.Green == false && t.Output.Length > 0) sb.AppendLine("```").AppendLine(t.Output.Trim()).AppendLine("```");
                }
                if (st.Merges.Any(m => m.Phase == phase && m.Outcome == "merged"))
                    sb.AppendLine("The merged changes are in the project's working tree, not committed.");
            }
            var failed = st.Agents.Where(a => a.State == "failed").ToList();
            if (failed.Count > 0)
            {
                sb.AppendLine().AppendLine("## Failed agents");
                foreach (var a in failed) sb.AppendLine($"- {a.Phase} / {a.Label}: {a.Error}");
            }
        }
        string s = sb.ToString();
        return s.Length <= maxChars ? s : s[..maxChars] + "\n… (cut)";
    }
}
