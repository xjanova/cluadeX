using CluadeX.Models;

namespace CluadeX.Services.Agents;

/// <summary>How a run spread its work — the tag its cost is recorded under (<see cref="CostTrackingService.RecordRun"/>).
/// Ordered: a run that used several reports the highest.</summary>
public enum DispatchStrategy { Single = 0, FanOut = 1, Workers = 2, SpeculativeFix = 3 }

/// <summary>What a sub-agent is for — which model it runs on (<see cref="DispatchPolicy.ModelFor"/>).</summary>
public enum SubAgentKind { Explore, Code, Review, Fix, HardFix }

/// <summary>What the policy looks at for one step of a run. Effort is the level this step really runs at (the user's
/// pick, or the smart-effort pick under "auto").</summary>
public sealed record DispatchInput
{
    public string Effort { get; init; } = "medium";
    public bool PolicyEnabled { get; init; } = true;
    /// <summary>The model runs sub-agents at all (native tool use, ready).</summary>
    public bool ProviderCanRunSubAgents { get; init; } = true;
    /// <summary>The per-run spend guard (<see cref="AppSettings.RunBudgetUsd"/>, 0 = none), what the user allowed past
    /// it, and what the run has spent.</summary>
    public decimal RunBudgetUsd { get; init; }
    public decimal BudgetGrantedUsd { get; init; }
    public decimal RunSpentUsd { get; init; }
    /// <summary>The daily cap (0 = none) and what today spent so far.</summary>
    public decimal DailyBudgetUsd { get; init; }
    public decimal TodaySpentUsd { get; init; }
    public int MaxParallel { get; init; } = 3;
    public bool SpeculativeFixEnabled { get; init; } = true;
    public int SpeculativeFixCount { get; init; } = 3;
    /// <summary>The run may edit its folder without asking (workers and fixers edit in worktrees, then their diff lands
    /// there) and the folder is a git repository (worktrees need one).</summary>
    public bool CanEditProject { get; init; }
    public bool IsGitRepo { get; init; }
}

/// <summary>What a step may fan out to. <see cref="Ceiling"/> is the most this step may use; the run's recorded
/// strategy is what it actually used.</summary>
public sealed record DispatchDecision(
    DispatchStrategy Ceiling,
    bool SubAgents,
    bool ReadOnlyOnly,
    bool Workers,
    bool Reviewer,
    bool SpeculativeFix,
    int MaxConcurrency,
    int FixerCount,
    string Reason)
{
    /// <summary>The policy is off: everything as before (sub-agents allowed, no worktrees, no routing).</summary>
    public bool Legacy { get; init; }
}

/// <summary>
/// Effort-aware dispatch (owner, 2026-10-17: "ระบบส่งเอเจนแยกทำงาน ต้องฉลาดตามความพยายาม"). Every step re-sending a
/// ~170k-token context cost ~$0.10; a sub-agent with a small context on the right model costs a fraction. Decides per
/// step whether to fan out, how far, on which model:
/// <list type="bullet">
/// <item>low — no sub-agents.</item>
/// <item>medium — read-only fan-out (explore) in parallel on the provider's cheapest capable model; edits stay in the main loop.</item>
/// <item>high — also workers: pieces that touch disjoint files, each in its own git worktree, a reviewer on the combined
/// diff before it lands; a speculative fix once the main loop's own fix of a red build failed.</item>
/// <item>xhigh / max — as high, and speculative fixes on the first red build.</item>
/// </list>
/// The per-run and daily spend guards cap it: near the limit only the cheap read-only fan-out stays, past it nothing.
/// Pure — no services, no clock — so the contract harness checks every branch.
/// </summary>
public static class DispatchPolicy
{
    /// <summary>Below this share of the run's budget left, only the cheap read-only fan-out stays.</summary>
    public const decimal LowBudgetShare = 0.25m;
    public const int MaxParallelCap = 8;

    public static string Tag(DispatchStrategy s) => s switch
    {
        DispatchStrategy.FanOut => "fan-out",
        DispatchStrategy.Workers => "workers",
        DispatchStrategy.SpeculativeFix => "speculative-fix",
        _ => "single",
    };

    public static readonly string[] AllTags = ["single", "fan-out", "workers", "speculative-fix"];

    /// <summary>The effort a step runs at: the setting, or under "auto" the loop's pick (medium when none yet).</summary>
    public static string ResolveEffort(string? setting, string? stepEffort)
    {
        string s = EffortLevels.Normalize(setting);
        if (s != EffortLevels.Auto) return s;
        string step = EffortLevels.Normalize(stepEffort);
        return step == EffortLevels.Auto ? "medium" : step;
    }

    static int Rank(string effort) => EffortLevels.Normalize(effort) switch
    {
        "low" => 0, "medium" => 1, "high" => 2, "xhigh" => 3, "max" => 4, _ => 1,
    };

    public static DispatchDecision Decide(DispatchInput i)
    {
        int par = Math.Clamp(i.MaxParallel, 1, MaxParallelCap);
        if (!i.PolicyEnabled)
            return new DispatchDecision(DispatchStrategy.FanOut, i.ProviderCanRunSubAgents, false, false, false, false, par, 0,
                "policy off — sub-agents as before") { Legacy = true };
        if (!i.ProviderCanRunSubAgents)
            return Single(par, "this model cannot run sub-agents");

        int rank = Rank(i.Effort);
        if (rank == 0) return Single(par, "effort low — the main loop does it alone");

        // ── spend guards ──
        if (i.RunBudgetUsd > 0)
        {
            decimal budget = i.RunBudgetUsd + i.BudgetGrantedUsd;
            decimal left = budget - i.RunSpentUsd;
            if (left <= 0) return Single(par, $"run budget used (${i.RunSpentUsd:0.00} of ${budget:0.00})");
            if (left < budget * LowBudgetShare) rank = Math.Min(rank, 1);
        }
        if (i.DailyBudgetUsd > 0)
        {
            if (i.TodaySpentUsd >= i.DailyBudgetUsd) return Single(par, $"daily cap reached (${i.TodaySpentUsd:0.00} of ${i.DailyBudgetUsd:0.00})");
            if (i.TodaySpentUsd >= i.DailyBudgetUsd * (1 - LowBudgetShare)) rank = Math.Min(rank, 1);
        }
        bool capped = rank < Rank(i.Effort);

        if (rank == 1)
            return new DispatchDecision(DispatchStrategy.FanOut, true, true, false, false, false, par, 0,
                capped ? "budget nearly used — read-only fan-out only" : "effort medium — read-only fan-out on the cheapest model");

        // high and up: workers and fixers edit in git worktrees — they need a repo and the right to edit.
        if (!i.IsGitRepo || !i.CanEditProject)
            return new DispatchDecision(DispatchStrategy.FanOut, true, true, false, false, false, par, 0,
                !i.IsGitRepo ? "not a git repository — no worktrees, read-only fan-out only"
                             : "edits need asking here — read-only fan-out only");

        int fixers = Math.Clamp(i.SpeculativeFixCount, 2, 3);
        bool spec = i.SpeculativeFixEnabled && par >= 2;
        if (spec) fixers = Math.Min(fixers, par);
        return new DispatchDecision(spec ? DispatchStrategy.SpeculativeFix : DispatchStrategy.Workers, true, false, true, true,
            spec, par, spec ? fixers : 0,
            rank >= 3 ? $"effort {EffortLevels.Normalize(i.Effort)} — workers in worktrees, speculative fixes on the first red build"
                      : "effort high — workers in worktrees; speculative fixes after the main fix fails once");
    }

    static DispatchDecision Single(int par, string reason) =>
        new(DispatchStrategy.Single, false, false, false, false, false, par, 0, reason);

    /// <summary>
    /// Launch speculative fixers now? A red build or test command, not already raced for this same failure, and either
    /// the main loop's own fix already failed once or the effort is xhigh/max.
    /// </summary>
    public static bool ShouldSpeculate(DispatchDecision d, string effort, bool buildRed, int failedFixAttempts, bool alreadyRaced)
    {
        if (!d.SpeculativeFix || !buildRed || alreadyRaced) return false;
        return failedFixAttempts >= 1 || Rank(effort) >= 3;
    }

    /// <summary>May this sub-agent run under the decision? Null = yes; otherwise the line the main agent gets back.</summary>
    public static string? Gate(DispatchDecision d, string type, bool canEdit)
    {
        if (d.Legacy) return string.Equals(type, "workers", StringComparison.OrdinalIgnoreCase)
            ? "Workers are off (Settings ▸ Agent ▸ dispatch policy). Do the pieces yourself." : null;
        if (!d.SubAgents) return $"Sub-agents are off for this step ({d.Reason}). Do it yourself in this conversation.";
        if (string.Equals(type, "workers", StringComparison.OrdinalIgnoreCase))
            return d.Workers ? null : $"Workers are not available for this step ({d.Reason}). Make the edits yourself.";
        if (d.ReadOnlyOnly && (canEdit || string.Equals(type, "general", StringComparison.OrdinalIgnoreCase)))
            return $"Only read-only sub-agents for this step ({d.Reason}): use type \"explore\" to investigate, and make the edits yourself.";
        return null;
    }

    // ── model routing ──

    /// <summary>The cheapest model of a provider that still runs a tool loop well; null = no routing (local models,
    /// Claude Dev: the loaded / logged-in model).</summary>
    public static string? CheapestModel(AiProviderType p) => p switch
    {
        AiProviderType.Anthropic => "claude-haiku-5-5",
        AiProviderType.OpenAI => "gpt-6-luna",
        AiProviderType.Gemini => "gemini-3.5-flash-lite",
        _ => null,
    };

    /// <summary>One step up from the run's model (Opus-class) for a fix two fixers could not make.</summary>
    public static string? StrongerModel(AiProviderType p, string? runModel)
    {
        string m = (runModel ?? "").ToLowerInvariant();
        return p switch
        {
            AiProviderType.Anthropic => m.Contains("opus") || m.Contains("fable") || m.Contains("mythos") ? runModel : "claude-opus-5-5",
            AiProviderType.OpenAI => m.Contains("astra") ? runModel : "gpt-6-astra",
            AiProviderType.Gemini => m.Contains("pro") ? runModel : "gemini-3.1-pro-preview",
            _ => null,
        };
    }

    /// <summary>
    /// The model a sub-agent of <paramref name="kind"/> runs on: the user's override for the kind when set; explore → the
    /// cheapest; code, review and fix → the run's model; a hard fix (after <paramref name="failedFixers"/> ≥ 2) → one
    /// step up. Null = the run's own model (no override).
    /// </summary>
    public static string? ModelFor(SubAgentKind kind, AiProviderType provider, string? runModel,
        IReadOnlyDictionary<string, string>? overrides = null, int failedFixers = 0)
    {
        if (kind == SubAgentKind.Fix && failedFixers >= 2) kind = SubAgentKind.HardFix;
        if (overrides != null && overrides.TryGetValue(kind.ToString().ToLowerInvariant(), out var o) && !string.IsNullOrWhiteSpace(o))
            return o.Trim();
        return kind switch
        {
            SubAgentKind.Explore => CheapestModel(provider) ?? NullIfEmpty(runModel),
            SubAgentKind.HardFix => StrongerModel(provider, runModel) ?? NullIfEmpty(runModel),
            _ => NullIfEmpty(runModel),
        };
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>The kind an agent_spawn / subagent_invoke request is.</summary>
    public static SubAgentKind KindOf(bool canEdit) => canEdit ? SubAgentKind.Code : SubAgentKind.Explore;

    /// <summary>Sub-agents go to Claude Dev (the user's own login) instead of the API: its quota ran out and failover is on.</summary>
    public static bool UseClaudeDev(bool quotaExhausted, bool autoFailover, AiProviderType active) =>
        quotaExhausted && autoFailover && active is not (AiProviderType.ClaudeDev or AiProviderType.GeminiDev or AiProviderType.BrainX);

    // ── workers ──

    /// <summary>One piece of a split task: what to do and the files it may touch.</summary>
    public sealed record WorkPiece(string Task, IReadOnlyList<string> Files);

    /// <summary>Null when the pieces can run side by side (2+, each names its files, no file in two pieces); otherwise why not.</summary>
    public static string? CheckPieces(IReadOnlyList<WorkPiece> pieces)
    {
        if (pieces.Count < 2) return "Give at least 2 pieces (one piece = do it yourself).";
        var owner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int k = 0; k < pieces.Count; k++)
        {
            if (string.IsNullOrWhiteSpace(pieces[k].Task)) return $"Piece {k + 1} has no task.";
            if (pieces[k].Files.Count == 0) return $"Piece {k + 1} names no files — every piece must list the files it may change.";
            foreach (var f in pieces[k].Files)
            {
                string key = NormalizePath(f);
                if (owner.TryGetValue(key, out int other) && other != k)
                    return $"{f} is in piece {other + 1} and piece {k + 1} — pieces must touch disjoint files.";
                owner[key] = k;
            }
        }
        return null;
    }

    public static string NormalizePath(string p) => p.Trim().Replace('\\', '/').TrimStart('.', '/');

    /// <summary>Files a diff touched outside a piece's own list (a worker that wandered).</summary>
    public static List<string> OutsideFiles(IEnumerable<string> changed, IEnumerable<string> allowed)
    {
        var ok = new HashSet<string>(allowed.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
        return changed.Select(NormalizePath).Where(c => !ok.Contains(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
