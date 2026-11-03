using System.Text.Json;

namespace CluadeX.Services.Agents;

/// <summary>
/// A workflow as the agent declares it — data, not a script (docs/agent-orchestration.md §2). Phases run in order;
/// inside a phase: <see cref="WorkflowPhase.Agents"/> fan out (a barrier at the phase's end), an inline
/// <see cref="WorkflowPhase.Verify"/> refutes each agent's items as soon as that agent ends (no barrier), a
/// <see cref="WorkflowPhase.Pipeline"/> carries each item through its stages on its own (no barrier between stages),
/// and a verify-only phase refutes what an earlier phase produced.
/// </summary>
public sealed class WorkflowPlan
{
    public string Name { get; set; } = "workflow";
    public string Description { get; set; } = "";
    /// <summary>Agents of this workflow at once (the engine also holds the machine's <see cref="AgentLoad"/> cap).</summary>
    public int Concurrency { get; set; } = 3;
    public List<WorkflowPhase> Phases { get; set; } = new();

    /// <summary>The most agents one workflow may plan (an estimate past this is refused before anything starts).</summary>
    public const int MaxAgents = 60;
    public const int MaxRefuters = 7;
    /// <summary>How many items an agent is assumed to return when the estimate cannot know yet.</summary>
    public const int AssumedItemsPerAgent = 3;

    private static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Read and check a plan. Null + <paramref name="error"/> when it cannot run as written; the error says
    /// what to fix (the model gets it back).</summary>
    public static WorkflowPlan? Parse(string json, out string? error)
    {
        error = null;
        WorkflowPlan? plan;
        try { plan = JsonSerializer.Deserialize<WorkflowPlan>(json, Read); }
        catch (JsonException ex) { error = "The workflow is not valid JSON: " + ex.Message; return null; }
        if (plan == null) { error = "The workflow is empty."; return null; }
        error = plan.Check();
        return error == null ? plan : null;
    }

    public string? Check()
    {
        if (string.IsNullOrWhiteSpace(Name)) Name = "workflow";
        Name = Name.Trim();
        if (Name.Length > 60) Name = Name[..60];
        Concurrency = Math.Clamp(Concurrency <= 0 ? 3 : Concurrency, 1, DispatchPolicy.MaxParallelCap);
        if (Phases.Count == 0) return "The workflow has no phases: give at least one phase with agents.";
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Phases.Count; i++)
        {
            var p = Phases[i];
            if (string.IsNullOrWhiteSpace(p.Title)) p.Title = $"Phase {i + 1}";
            p.Title = p.Title.Trim();
            if (!titles.Add(p.Title)) return $"Two phases are called \"{p.Title}\": give each phase its own title.";
            int kinds = (p.Agents is { Count: > 0 } ? 1 : 0) + (p.Pipeline != null ? 1 : 0) + (p.Verify != null && p.Agents is not { Count: > 0 } ? 1 : 0);
            if (kinds != 1) return $"Phase \"{p.Title}\" must have exactly one of: agents (optionally with verify), pipeline, or verify with from.";
            if (p.Agents is { Count: > 0 } agents)
            {
                var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int a = 0; a < agents.Count; a++)
                {
                    var spec = agents[a];
                    if (string.IsNullOrWhiteSpace(spec.Prompt)) return $"Agent {a + 1} of phase \"{p.Title}\" has no prompt.";
                    if (string.IsNullOrWhiteSpace(spec.Label)) spec.Label = $"agent {a + 1}";
                    if (!labels.Add(spec.Label)) return $"Two agents of phase \"{p.Title}\" are labelled \"{spec.Label}\".";
                    if (spec.CheckType() is { } bad) return bad;
                    if (CheckIsolation(spec.Isolation, $"Agent \"{spec.Label}\" of phase \"{p.Title}\"", out var iso) is { } badIso) return badIso;
                    spec.Isolation = iso;
                }
            }
            if (CheckIsolation(p.Isolation, $"Phase \"{p.Title}\"", out var phaseIso) is { } badPhase) return badPhase;
            p.Isolation = phaseIso;
            if (p.Agents is not { Count: > 0 } && p.Isolation != null)
                return $"Phase \"{p.Title}\" has isolation, but only an agents phase can run its agents in worktrees.";
            // An agent in its own worktree is there to edit: it runs as general.
            foreach (var spec in p.Agents ?? new()) if (p.IsIsolated(spec)) spec.Type = "general";
            p.Gate = string.IsNullOrWhiteSpace(p.Gate) ? null : p.Gate.Trim();
            if (p.Merge is { } m) m.Test = string.IsNullOrWhiteSpace(m.Test) ? null : m.Test.Trim();
            bool anyIsolated = p.Agents?.Any(p.IsIsolated) == true;
            if ((p.Gate != null || p.Merge != null) && !anyIsolated)
                return $"Phase \"{p.Title}\" has a gate or merge, but none of its agents works in a worktree: add isolation \"worktree\".";
            if (p.Gate is { Length: > MaxCommandChars } || p.Merge?.Test is { Length: > MaxCommandChars })
                return $"The gate / merge test command of phase \"{p.Title}\" is longer than {MaxCommandChars} characters.";
            if (p.Pipeline is { } pipe)
            {
                if (pipe.Stages.Count == 0) return $"The pipeline of phase \"{p.Title}\" has no stages.";
                if (pipe.Items is not { Count: > 0 } && string.IsNullOrWhiteSpace(pipe.From))
                    return $"The pipeline of phase \"{p.Title}\" needs items, or from = an earlier phase's title.";
                if (!string.IsNullOrWhiteSpace(pipe.From)
                    && (!titles.Contains(pipe.From) || string.Equals(pipe.From, p.Title, StringComparison.OrdinalIgnoreCase)))
                    return $"The pipeline of phase \"{p.Title}\" reads from \"{pipe.From}\", which is not an earlier phase.";
                for (int s = 0; s < pipe.Stages.Count; s++)
                {
                    var st = pipe.Stages[s];
                    if (string.IsNullOrWhiteSpace(st.Prompt)) return $"Stage {s + 1} of the pipeline in \"{p.Title}\" has no prompt.";
                    if (string.IsNullOrWhiteSpace(st.Label)) st.Label = $"stage {s + 1}";
                    if (st.CheckType() is { } bad) return bad;
                    if (!string.IsNullOrWhiteSpace(st.Isolation) && st.Isolation.Trim().ToLowerInvariant() is not ("none" or "shared"))
                        return $"Stage \"{st.Label}\" of the pipeline in \"{p.Title}\" has isolation: only an agents phase can run its agents in worktrees.";
                }
            }
            if (p.Verify is { } v)
            {
                v.Refuters = Math.Clamp(v.Refuters <= 0 ? 3 : v.Refuters, 1, MaxRefuters);
                if (p.Agents is not { Count: > 0 })
                {
                    if (string.IsNullOrWhiteSpace(v.From)) return $"The verify phase \"{p.Title}\" needs from = an earlier phase's title.";
                    if (!titles.Contains(v.From) || string.Equals(v.From, p.Title, StringComparison.OrdinalIgnoreCase))
                        return $"The verify phase \"{p.Title}\" reads from \"{v.From}\", which is not an earlier phase.";
                }
            }
        }
        if (EstimateAgents() > MaxAgents)
            return $"This workflow would start about {EstimateAgents()} agents; the most is {MaxAgents}. Use fewer agents, items or refuters.";
        return null;
    }

    /// <summary>An upper estimate of the agents this plan starts (items the plan cannot know yet are assumed).</summary>
    public int EstimateAgents()
    {
        int total = 0;
        var itemsOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Phases)
        {
            int items = 0;
            if (p.Agents is { Count: > 0 } agents)
            {
                total += agents.Count;
                items = agents.Count * (p.Verify != null || HasField(p) ? AssumedItemsPerAgent : 1);
                if (p.Verify is { } v) total += items * v.Refuters;
            }
            else if (p.Pipeline is { } pipe)
            {
                items = pipe.Items is { Count: > 0 } list ? list.Count
                    : itemsOf.TryGetValue(pipe.From ?? "", out int n) ? n * (string.IsNullOrWhiteSpace(pipe.Field) ? 1 : AssumedItemsPerAgent) : AssumedItemsPerAgent;
                total += items * pipe.Stages.Count;
            }
            else if (p.Verify is { } v)
            {
                items = itemsOf.TryGetValue(v.From ?? "", out int n) ? n : AssumedItemsPerAgent;
                total += items * v.Refuters;
            }
            itemsOf[p.Title] = Math.Max(1, items);
        }
        return total;

        static bool HasField(WorkflowPhase p) => p.Verify?.Field is { Length: > 0 };
    }

    public int CountAgentsDeclared() => Phases.Sum(p => p.Agents?.Count ?? 0);

    /// <summary>Does any agent of this plan work in its own worktree (the run folder must then be a git repository)?</summary>
    public bool UsesWorktrees => Phases.Any(p => p.Agents?.Any(p.IsIsolated) == true);

    /// <summary>The commands the engine itself runs (gates and merge tests) — the permission prompt shows them.</summary>
    public List<string> Commands() => Phases.SelectMany(p => new[] { p.Gate, p.Merge?.Test })
        .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).Distinct().ToList();

    public const int MaxCommandChars = 2000;

    private static string? CheckIsolation(string? value, string who, out string? normalized)
    {
        normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
        if (normalized is null or "none" or "shared") { normalized = null; return null; }
        return normalized == "worktree" ? null : $"{who} has isolation \"{value}\": use \"worktree\" (or leave it out).";
    }
}

public sealed class WorkflowPhase
{
    public string Title { get; set; } = "";
    public List<WorkflowAgentSpec>? Agents { get; set; }
    public WorkflowVerify? Verify { get; set; }
    public WorkflowPipeline? Pipeline { get; set; }
    /// <summary>"worktree" = every agent of this phase works in its own git worktree, so parallel edits never collide;
    /// its result carries its diff. Null = the run folder, shared.</summary>
    public string? Isolation { get; set; }
    /// <summary>The command the engine — not the agent — runs in each agent's worktree after it ends (build, tests):
    /// exit 0 = green. A red agent's diff is never merged.</summary>
    public string? Gate { get; set; }
    /// <summary>When every agent of the phase has ended: apply the green diffs to the run folder one by one.</summary>
    public WorkflowMerge? Merge { get; set; }

    public bool IsIsolated(WorkflowAgentSpec spec) => (spec.Isolation ?? Isolation) == "worktree";
}

public sealed class WorkflowMerge
{
    /// <summary>Run once on the merged result (the full test suite). Null = the phase's gate.</summary>
    public string? Test { get; set; }
}

public sealed class WorkflowAgentSpec
{
    public string Label { get; set; } = "";
    public string Prompt { get; set; } = "";
    /// <summary>"explore" (read-only, default) or "general" (may edit files).</summary>
    public string Type { get; set; } = "explore";
    /// <summary>"worktree" = this agent alone works in its own git worktree (overrides the phase's isolation).</summary>
    public string? Isolation { get; set; }
    /// <summary>A JSON schema the answer must match (an object with "required" and "properties" is checked).</summary>
    public JsonElement? Schema { get; set; }

    internal string? CheckType()
    {
        Type = string.IsNullOrWhiteSpace(Type) ? "explore" : Type.Trim().ToLowerInvariant();
        return Type is "explore" or "general" ? null : $"Agent \"{Label}\" has type \"{Type}\": use explore (read-only) or general (may edit).";
    }
}

public sealed class WorkflowVerify
{
    /// <summary>The earlier phase to verify (a verify-only phase). On an agents phase: that phase itself.</summary>
    public string? From { get; set; }
    /// <summary>The array field of each answer that holds the claims ("findings"); empty = the answer is one claim.</summary>
    public string? Field { get; set; }
    public int Refuters { get; set; } = 3;
    /// <summary>Extra instructions for the refuters (what counts as wrong here).</summary>
    public string? Prompt { get; set; }
}

public sealed class WorkflowPipeline
{
    public string? From { get; set; }
    public string? Field { get; set; }
    public List<string>? Items { get; set; }
    /// <summary>Each item passes through these in order; a prompt may use {{item}} and {{prev}} (the stage before).</summary>
    public List<WorkflowAgentSpec> Stages { get; set; } = new();
}
