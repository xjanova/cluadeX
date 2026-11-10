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
    /// <summary>The chat's Ultra switch is on, or the owner's own words this turn asked for multi-agent work
    /// (<see cref="WorkflowGate.Allowed"/>). It lifts the step to at least <see cref="DispatchPolicy.UltraEffort"/>, so
    /// writing workers are allowed: on 2026-11-09 a Codex chat with Ultra on, fixing 152–202 build errors, stayed at
    /// "auto" → medium (read-only fan-out only) and never split the edits.</summary>
    public bool Ultra { get; init; }
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
    /// <summary>The run's or the day's spend guard is nearly used: every sub-agent runs cheap (<see cref="DispatchPolicy.TierFor"/>).</summary>
    public bool BudgetCapped { get; init; }
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

    /// <summary>The least level a step runs its dispatch at while Ultra is on: the one that allows writing workers.</summary>
    public const string UltraEffort = "high";

    public static DispatchDecision Decide(DispatchInput i)
    {
        int par = Math.Clamp(i.MaxParallel, 1, MaxParallelCap);
        if (!i.PolicyEnabled)
            return new DispatchDecision(DispatchStrategy.FanOut, i.ProviderCanRunSubAgents, false, false, false, false, par, 0,
                "policy off — sub-agents as before") { Legacy = true };
        if (!i.ProviderCanRunSubAgents)
            return Single(par, "this model cannot run sub-agents");

        // Ultra = the owner opted in to splitting: never below the level that allows workers. The spend guards below
        // still cap it — Ultra does not lift a budget.
        int asked = i.Ultra ? Math.Max(Rank(i.Effort), Rank(UltraEffort)) : Rank(i.Effort);
        string effortName = asked > Rank(i.Effort) ? "Ultra" : "effort " + EffortLevels.Normalize(i.Effort);
        int rank = asked;
        if (rank == 0) return Single(par, "effort low — the main loop does it alone");

        // ── spend guards ──
        bool low = false;
        if (i.RunBudgetUsd > 0)
        {
            decimal budget = i.RunBudgetUsd + i.BudgetGrantedUsd;
            decimal left = budget - i.RunSpentUsd;
            if (left <= 0) return Single(par, $"run budget used (${i.RunSpentUsd:0.00} of ${budget:0.00})");
            if (left < budget * LowBudgetShare) { rank = Math.Min(rank, 1); low = true; }
        }
        if (i.DailyBudgetUsd > 0)
        {
            if (i.TodaySpentUsd >= i.DailyBudgetUsd) return Single(par, $"daily cap reached (${i.TodaySpentUsd:0.00} of ${i.DailyBudgetUsd:0.00})");
            if (i.TodaySpentUsd >= i.DailyBudgetUsd * (1 - LowBudgetShare)) { rank = Math.Min(rank, 1); low = true; }
        }
        bool capped = rank < asked;

        if (rank == 1)
            return new DispatchDecision(DispatchStrategy.FanOut, true, true, false, false, false, par, 0,
                capped ? "budget nearly used — read-only fan-out only" : low ? "budget nearly used — sub-agents run cheap"
                    : "effort medium — read-only fan-out on the cheapest model") { BudgetCapped = low };

        // high and up: workers and fixers edit in git worktrees — they need a repo and the right to edit.
        if (!i.IsGitRepo || !i.CanEditProject)
            return new DispatchDecision(DispatchStrategy.FanOut, true, true, false, false, false, par, 0,
                !i.IsGitRepo ? $"{effortName}, but not a git repository with a commit yet — no worktrees, read-only fan-out only (a first commit opens them)"
                             : $"{effortName}, but edits need asking here — read-only fan-out only");

        int fixers = Math.Clamp(i.SpeculativeFixCount, 2, 3);
        bool spec = i.SpeculativeFixEnabled && par >= 2;
        if (spec) fixers = Math.Min(fixers, par);
        return new DispatchDecision(spec ? DispatchStrategy.SpeculativeFix : DispatchStrategy.Workers, true, false, true, true,
            spec, par, spec ? fixers : 0,
            rank >= 3 ? $"{effortName} — workers in worktrees, speculative fixes on the first red build"
                      : $"{effortName} — workers in worktrees; speculative fixes after the main fix fails once");
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
            AiProviderType.Gemini => m.Contains("pro") || m.Contains("argon") ? runModel : "gemini-3.1-pro-preview",
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

    // ── tier routing (owner, 2026-11-08: "เรียกเอเจนย่อย ฉลาดในการเรียกใช้โมเดล หรือ effort ที่อ่อนกว่าเพื่อประหยัดโทเค็น") ──

    /// <summary>A middle model of a provider: good enough for a scoped edit or a review, a fraction of the top one.
    /// Null = no routing (local models, the CLIs: the loaded / logged-in model).</summary>
    public static string? MidModel(AiProviderType p) => p switch
    {
        AiProviderType.Anthropic => "claude-sonnet-5-5",
        AiProviderType.OpenAI => "gpt-6.1-sol",
        AiProviderType.Gemini => "gemini-3.8-flash",
        _ => null,
    };

    /// <summary>How strong a model is within its provider: 0 = the cheap class, 1 = the middle, 2 = the top. An id this
    /// table does not know counts as the top, so routing may still go down from it, never up past it.</summary>
    public static int ClassOf(AiProviderType p, string? model)
    {
        string m = (model ?? "").ToLowerInvariant();
        return p switch
        {
            AiProviderType.Anthropic => m.Contains("haiku") ? 0 : m.Contains("sonnet") ? 1 : 2,
            AiProviderType.OpenAI => m.Contains("luna") || m.Contains("mini") || m.Contains("nano") ? 0
                : m.Contains("sol") || m.Contains("terra") ? 1 : 2,
            AiProviderType.Gemini => m.Contains("flash-lite") ? 0 : m.Contains("flash") ? 1 : 2,
            _ => 2,
        };
    }

    /// <summary>A tier written as text ("cheap", "mid", "deep", case-insensitive; "low"/"medium"/"high" also read); null
    /// = none or unknown — the sub-agent's kind decides then.</summary>
    public static SubAgentTier? ParseTier(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "cheap" or "low" or "light" => SubAgentTier.Cheap,
        "mid" or "medium" => SubAgentTier.Mid,
        "deep" or "high" or "hard" => SubAgentTier.Deep,
        _ => null,
    };

    /// <summary>
    /// The tier a sub-agent runs at: the one it asked for (agent_spawn's tier, a specialist's file), else by kind —
    /// read-only work is cheap, editing work deep (the run's model, as before tiers). Near or past the spend guard
    /// (<see cref="DispatchDecision.BudgetCapped"/>) everything is cheap.
    /// </summary>
    public static SubAgentTier TierFor(SubAgentKind kind, SubAgentTier? asked, bool budgetCapped)
    {
        if (budgetCapped) return SubAgentTier.Cheap;
        if (asked is { } t) return t;
        return kind == SubAgentKind.Explore ? SubAgentTier.Cheap : SubAgentTier.Deep;
    }

    /// <summary>
    /// The model a sub-agent at <paramref name="tier"/> runs on. In order: the agent file's own model (an alias
    /// opus/sonnet/haiku, or an id of the active provider — one of another provider is ignored); Settings' override for
    /// the tier ("cheap"/"mid"/"deep"); then cheap → the provider's cheapest (the "explore" override still applies to
    /// read-only work), mid → its middle model, deep → as <see cref="ModelFor"/>. Cheap and mid never go above the
    /// run's own model (a run on Haiku keeps its mid sub-agents on Haiku). Null = the run's own model.
    /// </summary>
    public static string? ModelForTier(SubAgentTier tier, SubAgentKind kind, AiProviderType provider, string? runModel,
        string? agentModel = null, IReadOnlyDictionary<string, string>? overrides = null, bool budgetCapped = false)
    {
        if (!budgetCapped && ResolveAgentModel(provider, agentModel) is { } own) return own;
        // Settings' overrides are the user's own pick: they stand as written (no clamp below).
        if (Override(overrides, tier.ToString().ToLowerInvariant()) is { } byTier) return byTier;
        if (tier == SubAgentTier.Cheap && kind == SubAgentKind.Explore && Override(overrides, "explore") is { } byKind) return byKind;
        // deep = the run's own model; for editing work the per-kind pick ("code") still stands
        if (tier == SubAgentTier.Deep)
            return kind == SubAgentKind.Explore ? NullIfEmpty(runModel) : ModelFor(kind, provider, runModel, overrides);
        string? pick = tier == SubAgentTier.Cheap ? CheapestModel(provider) : MidModel(provider);
        if (pick == null) return NullIfEmpty(runModel);
        // never up past the run's model (tier deep and the hard-fix step-up are the only climbs)
        if (!string.IsNullOrWhiteSpace(runModel) && ClassOf(provider, pick) > ClassOf(provider, runModel)) return NullIfEmpty(runModel);
        return pick;
    }

    /// <summary>Auto model off: the user's per-kind model when one is set, else the run's own (no routing down).</summary>
    public static string? ManualModelFor(SubAgentKind kind, string? runModel, IReadOnlyDictionary<string, string>? overrides = null)
        => Override(overrides, kind.ToString().ToLowerInvariant()) ?? NullIfEmpty(runModel);

    static string? Override(IReadOnlyDictionary<string, string>? overrides, string key) =>
        overrides != null && overrides.TryGetValue(key, out var o) && !string.IsNullOrWhiteSpace(o) ? o.Trim() : null;

    /// <summary>An agent file's <c>model:</c> as an id the active provider takes: opus / sonnet / haiku on Anthropic, or
    /// a full id of that provider. Null = none, or one this provider cannot run (the tier decides then).</summary>
    public static string? ResolveAgentModel(AiProviderType p, string? model)
    {
        string m = (model ?? "").Trim();
        if (m.Length == 0) return null;
        string l = m.ToLowerInvariant();
        if (p == AiProviderType.Anthropic)
        {
            string? alias = l switch
            {
                "opus" => "claude-opus-5-5", "sonnet" => "claude-sonnet-5-5", "haiku" => "claude-haiku-5-5",
                "fable" => FableModel, _ => null,
            };
            return alias ?? (l.StartsWith("claude-", StringComparison.Ordinal) ? m : null);
        }
        return p switch
        {
            AiProviderType.OpenAI => l.StartsWith("gpt-", StringComparison.Ordinal) || (l.Length > 1 && l[0] == 'o' && char.IsDigit(l[1])) ? m : null,
            AiProviderType.Gemini => l.StartsWith("gemini-", StringComparison.Ordinal) ? m : null,
            _ => null,
        };
    }

    /// <summary>The most effort a sub-agent at <paramref name="tier"/> may think with: cheap → low, mid → medium, deep →
    /// no cap (the run's own). It is a ceiling, read by <see cref="EffortLevels.Effective"/>: it never raises a level.</summary>
    public static string? EffortCapFor(SubAgentTier tier) => tier switch
    {
        SubAgentTier.Cheap => "low",
        SubAgentTier.Mid => "medium",
        _ => null,
    };

    /// <summary>The steps a sub-agent at <paramref name="tier"/> may take (of <paramref name="max"/>, the deep budget):
    /// a cheap model that loops spends a cheap tier's worth, then reports (and may climb one tier).</summary>
    public static int MaxStepsFor(SubAgentTier tier, int max) => tier switch
    {
        SubAgentTier.Cheap => Math.Max(1, Math.Min(max, 16)),
        SubAgentTier.Mid => Math.Max(1, Math.Min(max, 24)),
        _ => Math.Max(1, max),
    };

    /// <summary>Fewer runs than this in the window: too little to judge, the tier stands.</summary>
    public const int StartTierMinRuns = 4;
    /// <summary>This share of climbs or more: start one tier up (the cheap attempt mostly paid twice).</summary>
    public const double StartTierClimbShare = 0.4;

    /// <summary>
    /// The tier to start at, given how this kind of sub-agent did when it started at <paramref name="proposed"/>
    /// (<see cref="TierHistory"/>): when it had to climb in at least <see cref="StartTierClimbShare"/> of the last runs
    /// (and there were at least <see cref="StartTierMinRuns"/>), start one tier up — doing it twice costs more than doing
    /// it once on the stronger model. The spend guard still wins: near the limit it stays cheap.
    /// </summary>
    public static SubAgentTier StartTier(SubAgentTier proposed, int runs, int climbs, bool budgetCapped)
    {
        if (budgetCapped || proposed == SubAgentTier.Deep || runs < StartTierMinRuns) return proposed;
        return (double)climbs / runs >= StartTierClimbShare ? NextTier(proposed) : proposed;
    }

    /// <summary>What the next tier gets from a weak attempt, so it does not read everything again: the attempt's report
    /// and the tool calls it made (at most <paramref name="maxCalls"/>, newest last). Empty when there is nothing.</summary>
    public static string CarryOver(string? report, IReadOnlyList<string> toolCalls, int maxCalls = 25)
    {
        var sb = new System.Text.StringBuilder();
        string r = (report ?? "").Trim();
        if (r.Length > 1500) r = r[..1500] + "…";
        if (toolCalls.Count > 0)
        {
            sb.Append("An earlier, cheaper attempt at this task fell short. It already ran:\n");
            foreach (var c in toolCalls.Skip(Math.Max(0, toolCalls.Count - maxCalls))) sb.Append("- ").Append(c).Append('\n');
        }
        if (r.Length > 0) sb.Append(sb.Length == 0 ? "An earlier, cheaper attempt at this task fell short. " : "").Append("It reported:\n").Append(r).Append('\n');
        if (sb.Length > 0) sb.Append("Use what helps, check what it could not, and finish the task.");
        return sb.ToString();
    }

    /// <summary>The next tier up; deep stays deep.</summary>
    public static SubAgentTier NextTier(SubAgentTier t) => t == SubAgentTier.Cheap ? SubAgentTier.Mid : SubAgentTier.Deep;

    /// <summary>Climbs at most this many tiers for one request (cheap → mid → deep).</summary>
    public const int MaxEscalations = 2;

    private static readonly string[] FailurePhrases =
    [
        "could not find", "couldn't find", "unable to find", "was unable to", "i was not able", "not able to locate",
        "no relevant", "nothing relevant", "could not determine", "couldn't determine", "i don't have enough",
        "ไม่พบ", "หาไม่เจอ", "ไม่สามารถ", "ไม่เจอ",
    ];

    /// <summary>
    /// The report says the work did not get done: none at all, a placeholder the engine wrote (in parentheses — "wrote
    /// no report", "used its step budget"), very short, or a short report that says it could not find / was unable to.
    /// A long report that mentions "not found" on the way is a real report, and so is a short one that names a path,
    /// a file:line or code. A stop by the owner or the run ("[…]") is not
    /// a weak report — it must not climb.
    /// </summary>
    public static bool ReportLooksWeak(string? report)
    {
        string r = (report ?? "").Trim();
        if (r.Length == 0) return true;
        if (r.StartsWith('[')) return false;
        if (r.StartsWith('(') && r.EndsWith(')')) return true;
        // short but concrete ("in src/Bar.cs:42", "`Foo()`") is a real answer: redoing it would pay twice for nothing
        if (r.Length < 40) return !LooksConcrete(r);
        if (r.Length > 600) return false;
        string l = r.ToLowerInvariant();
        return FailurePhrases.Any(p => l.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>A short report that names something real: a path, a file:line, or code in backticks.</summary>
    static bool LooksConcrete(string r) =>
        r.Contains('/') || r.Contains('\\') || r.Contains('`')
        || System.Text.RegularExpressions.Regex.IsMatch(r, @"\w\.\w{1,8}(:\d+)?|:\d+");

    /// <summary>Run it again one tier up? Only with auto-model and climbing on, below deep, not past
    /// <see cref="MaxEscalations"/>, not when the owner stopped it, the spend guard is nearly used, or it was a
    /// carried-on conversation (its thread belongs to the model it ran on).</summary>
    public static bool ShouldEscalate(SubAgentTier tier, string? report, int escalations, bool escalateOn, bool stoppedByOwner,
        bool budgetCapped, bool continuation)
        => escalateOn && tier != SubAgentTier.Deep && escalations < MaxEscalations && !stoppedByOwner && !budgetCapped
           && !continuation && ReportLooksWeak(report);

    /// <summary>
    /// A cheap read-only sub-agent goes to the model on this machine instead of the API: the owner turned it on, the run
    /// is on an API model (a run already local stays as it is), and a local model that can run a tool loop is already
    /// loaded — never one this would have to load (it would take the GPU from whatever holds it).
    /// </summary>
    public static bool UseLocalForCheap(bool settingOn, SubAgentTier tier, bool canEdit, bool runIsLocal,
        bool localReady, bool localToolUse, ModelCapability.Tier localSize)
        => tier == SubAgentTier.Cheap && UseLocalFor(settingOn, tier, canEdit, runIsLocal, localReady, localToolUse, localSize, null);

    /// <summary>The smallest model (billions of parameters; a MoE counts its whole size) that takes the MID tier's
    /// read-only work too: a 30B+ model on this PC (colibri runs a 35B MoE on an 8 GB card) reads and explores about as
    /// well as the API's mid model.</summary>
    public const double MidLocalMinBillions = 30;

    /// <summary>
    /// A read-only sub-agent goes to the model on this machine instead of the API (owner 2026-11-08: "งานไหนใช้การ์ดจอ local
    /// ได้ ยิ่งดีเลย … โมเดลใหญ่ แต่ใช้การ์ดจอเล็ก ประหยัดโทเค็น"): the cheap tier on any capable (7B+) local model, the mid
    /// tier too on a 30B+ one, the deep tier never. Otherwise as before: the owner turned it on, the run is on an API
    /// model, the local model is up and can run a tool loop, and the sub-agent does not edit.
    /// </summary>
    public static bool UseLocalFor(bool settingOn, SubAgentTier tier, bool canEdit, bool runIsLocal,
        bool localReady, bool localToolUse, ModelCapability.Tier localSize, double? localBillions)
        => settingOn && !canEdit && !runIsLocal && localReady && localToolUse && tier switch
        {
            SubAgentTier.Cheap => localSize == ModelCapability.Tier.Capable,
            SubAgentTier.Mid => localBillions >= MidLocalMinBillions,
            _ => false,
        };

    /// <summary>The kind an agent_spawn / subagent_invoke request is.</summary>
    public static SubAgentKind KindOf(bool canEdit) => canEdit ? SubAgentKind.Code : SubAgentKind.Explore;

    /// <summary>Sub-agents go to Claude Dev (the user's own login) instead of the API: its quota ran out and failover is on.</summary>
    public static bool UseClaudeDev(bool quotaExhausted, bool autoFailover, AiProviderType active) =>
        quotaExhausted && autoFailover && active is not (AiProviderType.ClaudeDev or AiProviderType.GeminiDev or AiProviderType.AntigravityDev or AiProviderType.OpenAIDev or AiProviderType.GrokDev or AiProviderType.BrainX);

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

    // ── very hard tasks → Claude Fable 5.2 (owner 2026-11-10: "งานยาก" = a task Opus 5.5 cannot crack, or one deep, many
    //    layers, many repos, that must not go wrong). Fable never takes over by itself: CluadeX offers it with a price
    //    (a card), the owner says yes — or asks for it in words — and then it goes to a focused brief, never the whole chat.

    /// <summary>The Fable model a hard task goes to.</summary>
    public const string FableModel = "claude-fable-5-2";

    /// <summary>Fable runs at high, not max: gains above high are uneven, and its cost is output and thinking.</summary>
    public const string FableEffort = "high";

    /// <summary>Task budgets (beta <c>task-budgets-2026-03-13</c>): what a Fable run may generate and read back this turn
    /// — advisory, the model paces itself and finishes instead of being cut off. The API's minimum is 20,000.</summary>
    public const int FableChatTaskBudget = 160_000;
    public const int FableSubAgentTaskBudget = 60_000;
    public const int TaskBudgetMinimum = 20_000;

    /// <summary>How long a Fable report may be (said in its brief: the report is what comes back, the rest stays there).</summary>
    public const int FableReportWords = 300;

    /// <summary>Fable 5.2's prices per million tokens: input, cache read, 5-minute cache write, output (thinking included).</summary>
    public const decimal FableInputPerM = 8m, FableCacheReadPerM = 0.20m, FableCacheWritePerM = 10m, FableOutputPerM = 40m;

    /// <summary>A Fable (or Mythos) model id or alias.</summary>
    public static bool IsFable(string? model)
    {
        string m = (model ?? "").Trim().ToLowerInvariant();
        return m.Contains("fable") || m.Contains("mythos");
    }

    /// <summary>"fable" / "fable 5.2" / a full Fable id as the id the API takes; anything else null.</summary>
    public static string? FableIdOf(string? asked)
    {
        string m = (asked ?? "").Trim();
        if (m.Length == 0 || !IsFable(m)) return null;
        string l = m.ToLowerInvariant();
        return l.StartsWith("claude-", StringComparison.Ordinal) ? m : FableModel;
    }

    /// <summary>What the owner's words ask Fable to do: nothing, think through one part (a sub-agent), or take the task
    /// in a new chat.</summary>
    public enum FableRoute { None, SubAgent, NewChat }

    private static readonly System.Text.RegularExpressions.Regex FableWord = new(
        @"\bfable\b|\bmythos\b|เฟ\s*เบิ้?ล", System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    private static readonly System.Text.RegularExpressions.Regex NewChatWords = new(
        @"\bnew\s+(chat|session|thread)\b|\bseparate\s+chat\b|\bown\s+chat\b|แชท\s*ใหม่|เซสชั?น\s*ใหม่|แยก\s*แชท|ห้อง\s*ใหม่",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    /// <summary>Words that put Fable to work rather than mention it ("what is fable?" asks nothing).</summary>
    private static readonly System.Text.RegularExpressions.Regex FableAskWords = new(
        @"\b(use|let|ask|send|give|hand|route|switch|run|try|have)\b|\bthink\b|\bon\s+fable\b|\bwith\s+fable\b|\bto\s+fable\b|"
        + @"ให้|ใช้|ส่ง|โยน|คิด|ลอง|ทำ",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// What the owner asked Fable to do, from their own words (Thai or English): "ส่วนนี้ให้ fable คิด" / "use fable for
    /// this" → one part on a Fable sub-agent; "โยนไปแชทใหม่ใช้ fable" / "fable in a new chat" → the task in a new chat.
    /// A question about Fable, or the word alone, asks for nothing. Pure.
    /// </summary>
    public static FableRoute ParseFableAsk(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return FableRoute.None;
        try
        {
            if (!FableWord.IsMatch(text) || !FableAskWords.IsMatch(text)) return FableRoute.None;
            return NewChatWords.IsMatch(text) ? FableRoute.NewChat : FableRoute.SubAgent;
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return FableRoute.None; }
    }

    /// <summary>What a run went through that says Opus is stuck (CluadeX's own signals), and what the owner said about
    /// the task (theirs).</summary>
    public sealed record HardTaskSignals
    {
        /// <summary>The model's own fixes that left the same build red, in a row (0 once it went green).</summary>
        public int FailedOwnFixes { get; init; }
        /// <summary>Speculative fixers raced on that red build and none turned it green.</summary>
        public bool SpeculativeFixFailed { get; init; }
        /// <summary>The loop guard stopped the run (a cycle the model could not leave).</summary>
        public bool LoopGuardStopped { get; init; }
        /// <summary>Folders the task spans (the project plus related ones opened beside it).</summary>
        public int Repos { get; init; } = 1;
        /// <summary>The owner's words: it must not go wrong / it is many layers deep / it spans several repos.</summary>
        public bool OwnerSaysCritical { get; init; }
        public bool OwnerSaysLayered { get; init; }
        public bool OwnerSaysMultiRepo { get; init; }
    }

    /// <summary>The verdict: hard (worth offering Fable) and why, in English (log / tool text) and Thai (the card).</summary>
    public sealed record HardTaskVerdict(bool Hard, string Why, string WhyTh);

    /// <summary>Own fixes in a row that left the build red, after which the task counts as stuck even without fixers.</summary>
    public const int StuckOwnFixes = 3;

    /// <summary>
    /// Is this a "very hard" task — one for Fable? CluadeX's signal: Opus failed the same problem again and again (its own
    /// fix left the build red and the speculative fixers failed too, or <see cref="StuckOwnFixes"/> own fixes in a row
    /// failed, or the loop guard had to stop it). The owner's signal: the task is complex in layers or repos AND must not
    /// go wrong (any two of the three). A run already on Fable is never offered Fable again. Pure.
    /// </summary>
    public static HardTaskVerdict AssessHardTask(HardTaskSignals s, string? runModel)
    {
        if (IsFable(runModel)) return new(false, "already on Fable", "ใช้ Fable อยู่แล้ว");
        if (s.FailedOwnFixes >= 1 && s.SpeculativeFixFailed)
            return new(true, $"the build stayed red after {s.FailedOwnFixes} own fix(es) and the speculative fixers failed too",
                $"แก้เองแล้ว build ยังแดง {s.FailedOwnFixes} ครั้ง และตัวแก้แข่งกันก็แก้ไม่ได้");
        if (s.FailedOwnFixes >= StuckOwnFixes)
            return new(true, $"{s.FailedOwnFixes} own fixes in a row left the build red",
                $"แก้เองติดกัน {s.FailedOwnFixes} ครั้งแล้ว build ยังแดง");
        if (s.LoopGuardStopped)
            return new(true, "the loop guard stopped the run (it went round in circles)", "loop guard ต้องหยุดรัน (วนอยู่ที่เดิม)");
        bool multiRepo = s.OwnerSaysMultiRepo || s.Repos >= 2;
        int owner = (s.OwnerSaysCritical ? 1 : 0) + (s.OwnerSaysLayered ? 1 : 0) + (multiRepo ? 1 : 0);
        if (owner >= 2 && (s.OwnerSaysCritical || s.OwnerSaysLayered))
        {
            var en = new List<string>(); var th = new List<string>();
            if (s.OwnerSaysLayered) { en.Add("many layers"); th.Add("ซับซ้อนหลายชั้น"); }
            if (multiRepo) { en.Add(s.Repos >= 2 ? $"{s.Repos} repos" : "several repos"); th.Add(s.Repos >= 2 ? $"{s.Repos} repo" : "หลาย repo"); }
            if (s.OwnerSaysCritical) { en.Add("must not go wrong"); th.Add("ต้องไม่ผิดพลาด"); }
            return new(true, "the owner's task: " + string.Join(", ", en), "งานที่เจ้าของบอกว่า" + string.Join(" ", th));
        }
        return new(false, "", "");
    }

    private static readonly System.Text.RegularExpressions.Regex CriticalWords = new(
        @"must\s+not\s+(fail|break|go\s+wrong)|no\s+mistakes?|zero\s+(mistakes?|errors?)|cannot\s+afford|mission[- ]critical|"
        + @"ห้าม\s*(ผิด|พลาด|พัง)|ต้อง\s*ไม่\s*(ผิด|พลาด|พัง)|ผิด\s*ไม่\s*ได้|พลาด\s*ไม่\s*ได้|ห้ามผิดพลาด",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    private static readonly System.Text.RegularExpressions.Regex LayeredWords = new(
        @"many\s+layers|multi[- ]?layer|deep(ly)?\s+nested|several\s+layers|complex\s+and\s+(subtle|deep)|"
        + @"หลาย\s*ชั้น|ซับซ้อน|ลึกลับ|ซ้อน\s*กัน\s*หลาย",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    private static readonly System.Text.RegularExpressions.Regex MultiRepoWords = new(
        @"(several|many|multiple|across|two|three)\s+(repos|repositories|projects)|multi[- ]?repo|cross[- ]repo|"
        + @"หลาย\s*(repo|รีโป|โปรเจ[กค]ต์?)|ข้าม\s*(repo|รีโป|โปรเจ[กค]ต์?)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    /// <summary>The owner's signal words in <paramref name="text"/>. Pure.</summary>
    public static (bool Critical, bool Layered, bool MultiRepo) OwnerHardWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (false, false, false);
        try { return (CriticalWords.IsMatch(text), LayeredWords.IsMatch(text), MultiRepoWords.IsMatch(text)); }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return (false, false, false); }
    }

    /// <summary>A Fable run's likely cost: a low and a high case, in USD, and the tokens the estimate started from.</summary>
    public sealed record FableEstimate(decimal LowUsd, decimal HighUsd, int StartTokens, int TaskBudget);

    /// <summary>
    /// What a Fable run on a <paramref name="briefTokens"/> brief will likely cost, on top of <paramref name="baseTokens"/>
    /// of system prompt and tool definitions. Each step reads the cached prefix ($0.20/M), writes what it added to the
    /// cache ($10/M) and generates its reply and thinking ($40/M) — the output is most of it, not the cache. The low case
    /// is a short run (8 steps, 1.5K out each); the high case uses the whole task budget (each step 4K out + 4K read
    /// back). Pure.
    /// </summary>
    public static FableEstimate EstimateFable(int briefTokens, int baseTokens, int taskBudget)
    {
        int start = Math.Max(0, briefTokens) + Math.Max(0, baseTokens);
        decimal Case(int steps, int grow, int outPerStep)
        {
            decimal tokRead = 0, tokWrite = start, tokOut = 0;
            for (int k = 0; k < steps; k++)
            {
                tokRead += start + (decimal)k * grow;
                tokWrite += grow;
                tokOut += outPerStep;
            }
            return (tokRead * FableCacheReadPerM + tokWrite * FableCacheWritePerM + tokOut * FableOutputPerM) / 1_000_000m;
        }
        int budget = Math.Max(TaskBudgetMinimum, taskBudget);
        int highSteps = Math.Clamp(budget / 8000, 4, 60);
        return new FableEstimate(Math.Round(Case(8, 2500, 1500), 2), Math.Round(Case(highSteps, 4000, 4000), 2), start, budget);
    }

    /// <summary>What it costs only to hand Fable <paramref name="historyTokens"/> of chat history (a fork): one cache write.</summary>
    public static decimal ForkWriteUsd(int historyTokens) => Math.Round(Math.Max(0, historyTokens) * FableCacheWritePerM / 1_000_000m, 2);

    /// <summary>
    /// The spend guards for a Fable run of <paramref name="est"/>: null = go; otherwise why not (English, for the tool's
    /// result and the log). A run it would start fresh (a new chat) has the whole per-run budget; a sub-agent shares its
    /// run's. Refused when even the low case does not fit what is left; the high case past it only warns
    /// (<see cref="FableBudgetWarning"/>). Pure.
    /// </summary>
    public static string? FableBudgetRefusal(FableEstimate est, decimal runBudgetUsd, decimal grantedUsd, decimal runSpentUsd,
        decimal dailyBudgetUsd, decimal todaySpentUsd, bool freshRun)
    {
        if (runBudgetUsd > 0)
        {
            decimal left = runBudgetUsd + grantedUsd - (freshRun ? 0 : runSpentUsd);
            if (left < est.LowUsd)
                return $"Fable would cost about ${est.LowUsd:0.00}–${est.HighUsd:0.00}, more than the ${Math.Max(0, left):0.00} left of the per-run budget "
                     + $"(${runBudgetUsd:0.00}, Settings ▸ Agent). Raise the budget or keep the work on this model.";
        }
        if (dailyBudgetUsd > 0)
        {
            decimal left = dailyBudgetUsd - todaySpentUsd;
            if (left < est.LowUsd)
                return $"Fable would cost about ${est.LowUsd:0.00}–${est.HighUsd:0.00}, more than the ${Math.Max(0, left):0.00} left of today's cap "
                     + $"(${dailyBudgetUsd:0.00}). Raise the daily cap or keep the work on this model.";
        }
        return null;
    }

    /// <summary>The high case passes a guard (the run may be stopped on the way): the warning, or null.</summary>
    public static string? FableBudgetWarning(FableEstimate est, decimal runBudgetUsd, decimal grantedUsd, decimal runSpentUsd,
        decimal dailyBudgetUsd, decimal todaySpentUsd, bool freshRun)
    {
        decimal runLeft = runBudgetUsd > 0 ? runBudgetUsd + grantedUsd - (freshRun ? 0 : runSpentUsd) : decimal.MaxValue;
        decimal dayLeft = dailyBudgetUsd > 0 ? dailyBudgetUsd - todaySpentUsd : decimal.MaxValue;
        decimal left = Math.Min(runLeft, dayLeft);
        return est.HighUsd > left
            ? $"A long Fable run (about ${est.HighUsd:0.00}) would pass the ${left:0.00} left of the budget; the spend guard stops it there."
            : null;
    }

    /// <summary>What a Fable brief ends with: the report is all that comes back, so it is short and says what matters.</summary>
    public static string FableReportInstruction(bool newChat) =>
        $"When you are done, end with a report of at most {FableReportWords} words: the cause you found, what you changed "
        + "(files), how you checked it (the build / test and its result), and anything left open. "
        + (newChat ? "Only that report goes back to the chat that sent this task; " : "Only that report goes back to the agent that asked; ")
        + "the steps stay here. Work at the depth the problem needs, not more.";
}
