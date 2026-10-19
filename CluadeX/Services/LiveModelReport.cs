using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// What really answers a chat: the model the provider says it served and the effort its request really went out with
/// (owner 2026-10-19: "ส่วนที่ชี้ว่าใช้โมเดลอะไร effort อะไรต้องแสดงตามจริงที่กำลังใช้ เช่น opus 5.5 effort max").
/// The chat used to show the model that was SET (or "Claude Code default") and the effort that was PICKED — not what a
/// provider rounded it to, nor what the Claude Code CLI actually ran at.
///
/// Providers report as they go: <see cref="Sent"/> when a request leaves (the model id and the effort field as written on
/// the wire, or "none" for a provider without one), <see cref="Served"/> when the response names the model that answered,
/// and <see cref="Reported"/> when the other side says which level it used (the Claude Code CLI's hooks do). The report
/// lands on the current run's scope — several chats can run at once — and as the provider's latest, for outside a run.
/// </summary>
public static class LiveModelReport
{
    /// <summary>The provider or this model has no effort setting at all (local models, Gemini CLI, the BrainX room).</summary>
    public const string NoEffort = "none";

    /// <summary>Nothing was sent: the model runs at its own default level.</summary>
    public const string ModelDefault = "default";

    public sealed record Snapshot(AiProviderType Provider, string? Model, string? Effort, bool EffortConfirmed, DateTime At)
    {
        /// <summary>Why "auto" picked this level, in a few words (null = not auto, or nothing to say).</summary>
        public string? Why { get; init; }
    }

    private static readonly ConcurrentDictionary<AiProviderType, Snapshot> Latest = new();

    /// <summary>Raised on every report (any thread).</summary>
    public static event Action? Changed;

    /// <summary>A request is leaving: <paramref name="model"/> as sent, <paramref name="effortSent"/> the effort field as
    /// written on the wire (null = left out, so the model's default), <paramref name="hasEffort"/> false when the provider
    /// or model has no effort to set.</summary>
    public static void Sent(AiProviderType provider, string? model, string? effortSent, bool hasEffort)
    {
        string effort = !hasEffort ? NoEffort : string.IsNullOrWhiteSpace(effortSent) ? ModelDefault : effortSent.Trim().ToLowerInvariant();
        Update(provider, prev => new Snapshot(provider,
            // A response already named the served model for this same id family: keep it until the next response.
            string.IsNullOrWhiteSpace(model) ? prev?.Model : model.Trim(), effort, false, DateTime.Now) { Why = AgentRunScope.Current?.EffortWhy });
    }

    /// <summary>The response named the model that answered (an alias resolves to a dated id, a default to a real one).</summary>
    public static void Served(AiProviderType provider, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return;
        // Every streamed chunk names the model: only a change is news.
        var known = For(AgentRunScope.Current, provider);
        if (known != null && string.Equals(known.Model, model.Trim(), StringComparison.Ordinal)
            && (AgentRunScope.Current == null || AgentRunScope.Current.LiveReport != null)) return;
        Update(provider, prev => (prev ?? new Snapshot(provider, null, null, false, DateTime.Now)) with { Model = model.Trim(), At = DateTime.Now });
    }

    /// <summary>The other side said which level it really ran at (Claude Code's hook input: effort.level).</summary>
    public static void Reported(AiProviderType provider, string? level)
    {
        string l = level?.Trim().ToLowerInvariant() ?? "";
        if (l.Length == 0 || l.Length > 16) return;
        Update(provider, prev => (prev ?? new Snapshot(provider, null, null, false, DateTime.Now)) with { Effort = l, EffortConfirmed = true, At = DateTime.Now });
    }

    /// <summary>This step's effort changed without a new top-level field (Anthropic's effort-only system messages).</summary>
    public static void StepEffort(AiProviderType provider, string level) => Update(provider, prev =>
        (prev ?? new Snapshot(provider, null, null, false, DateTime.Now)) with { Effort = level, At = DateTime.Now, Why = AgentRunScope.Current?.EffortWhy ?? prev?.Why });

    /// <summary>The run's report when it has one for this provider, else the provider's latest.</summary>
    public static Snapshot? For(AgentRunScope? scope, AiProviderType provider)
    {
        if (scope?.LiveReport is { } s && s.Provider == provider) return s;
        return Latest.TryGetValue(provider, out var last) ? last : null;
    }

    /// <summary>Forget everything (tests).</summary>
    public static void Reset() => Latest.Clear();

    /// <summary>Forget a provider's latest (the user changed the effort pick: the old request no longer says what is next).</summary>
    public static void Forget(AiProviderType provider) => Latest.TryRemove(provider, out _);

    private static void Update(AiProviderType provider, Func<Snapshot?, Snapshot> next)
    {
        var scope = AgentRunScope.Current;
        Snapshot? prev = scope?.LiveReport is { } own && own.Provider == provider ? own
            : Latest.TryGetValue(provider, out var l) ? l : null;
        var snap = next(prev);
        Latest[provider] = snap;
        if (scope != null) scope.LiveReport = snap;
        try { Changed?.Invoke(); } catch { /* the chat's label must never break a request */ }
    }

    // ─── What the chat shows ───

    /// <summary>
    /// "Opus 5.5 · max", "Opus 5.5 · auto → high", "Qwen3 8B · no effort": the model that really answers and the effort
    /// it really runs at. <paramref name="setting"/> is the effort picker; <paramref name="fallbackModel"/> what is set
    /// when no request has gone out yet. A level the provider rounded is shown as sent, with the pick beside it.
    /// </summary>
    public static string Describe(string? setting, Snapshot? snap, string? fallbackModel, Func<string, string> t)
    {
        string pick = EffortLevels.Normalize(setting);
        string model = FriendlyModel(snap?.Model ?? fallbackModel);
        string? effort = snap?.Effort;
        string shown = effort switch
        {
            null => pick,
            NoEffort => t("live.effort.none"),
            ModelDefault => pick == EffortLevels.Auto ? "auto → " + t("live.effort.modelDefault") : t("live.effort.modelDefault"),
            _ when pick == EffortLevels.Auto => "auto → " + effort,
            _ when effort == pick => effort,
            _ => string.Format(t("live.effort.rounded"), effort, pick),
        };
        if (pick == EffortLevels.Auto && effort is not (null or NoEffort) && !string.IsNullOrWhiteSpace(snap?.Why))
            shown += " (" + snap!.Why + ")";
        return string.IsNullOrWhiteSpace(model) ? shown : model + " · " + shown;
    }

    /// <summary>A model id as people say it: claude-opus-5-5 → "Opus 5.5", gpt-5.5 → "GPT-5.5",
    /// gemini-3-pro-preview → "Gemini 3 Pro Preview". Anything else (a GGUF file, an Ollama tag) stays as it is.</summary>
    public static string FriendlyModel(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        string m = id.Trim();
        int slash = m.LastIndexOf('/');
        string bare = slash >= 0 && slash < m.Length - 1 ? m[(slash + 1)..] : m;
        var claude = Regex.Match(bare, @"^claude-(opus|sonnet|haiku|fable)-(\d+)(?:-(\d{1,2}))?(?:-\d{8})?(?:\[1m\])?$", RegexOptions.IgnoreCase);
        if (claude.Success)
        {
            string fam = char.ToUpperInvariant(claude.Groups[1].Value[0]) + claude.Groups[1].Value[1..].ToLowerInvariant();
            return fam + " " + claude.Groups[2].Value + (claude.Groups[3].Success ? "." + claude.Groups[3].Value : "");
        }
        var old = Regex.Match(bare, @"^claude-(\d+)(?:-(\d))?-(opus|sonnet|haiku)(?:-\d{8})?$", RegexOptions.IgnoreCase);
        if (old.Success)
            return char.ToUpperInvariant(old.Groups[3].Value[0]) + old.Groups[3].Value[1..] + " " + old.Groups[1].Value
                   + (old.Groups[2].Success ? "." + old.Groups[2].Value : "");
        if (bare.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
        {
            // gpt-6.1-sol → "GPT-6.1 Sol", gpt-5.4-mini → "GPT-5.4 Mini" (OpenAI's own names); a dated snapshot loses the date.
            string[] parts = Regex.Replace(bare[4..], @"-\d{4}-\d{2}-\d{2}$", "").Split('-');
            return "GPT-" + parts[0] + string.Concat(parts.Skip(1).Select(w => w.Length == 0 ? "" : " " + char.ToUpperInvariant(w[0]) + w[1..]));
        }
        if (bare.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
            return string.Join(' ', bare.Split('-').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
        return bare;
    }
}
