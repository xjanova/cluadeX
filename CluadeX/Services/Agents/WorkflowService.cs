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
                    foreach (var a in st.Agents.Where(a => a.State is "queued" or "running")) a.State = "stopped";
                    Write(st);
                    n++;
                }
                catch { }
            }
        }
        catch { }
        return n;
    }

    /// <summary>Run a new workflow for chat <paramref name="sessionKey"/> and wait for its end (the tool's call).
    /// <paramref name="ct"/> is the turn's: stopping the turn stops the workflow.</summary>
    public async Task<WorkflowRunState> RunAsync(WorkflowPlan plan, string planJson, string sessionKey, CancellationToken ct)
    {
        var est = EstimateFor(plan);
        var st = new WorkflowRunState
        {
            SessionKey = sessionKey, Name = plan.Name, PlanJson = planJson,
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
        };
        engine.RowChanged += (s, r) => { try { RowChanged?.Invoke(s, r); } catch { } };
        RunActivity.WorkflowStarted(st.SessionKey, st.Id);
        try
        {
            try { Started?.Invoke(st); } catch { }
            Write(st);
            await engine.RunAsync(st, plan, cts.Token);
        }
        finally
        {
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
