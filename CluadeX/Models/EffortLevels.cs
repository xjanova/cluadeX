namespace CluadeX.Models;

/// <summary>
/// The user's "how hard should the model work" pick (<see cref="AppSettings.ReasoningEffort"/>), one scale
/// for every provider: Anthropic output_config.effort, OpenAI reasoning_effort, Gemini thinking level,
/// Claude Code --effort. "auto" sends nothing and leaves each model on its own default. Providers round a
/// level the model doesn't have down to the nearest one it does.
/// </summary>
public static class EffortLevels
{
    public const string Auto = "auto";

    /// <summary>Lowest to highest, "auto" first.</summary>
    public static readonly string[] All = [Auto, "low", "medium", "high", "xhigh", "max"];

    /// <summary>The effort a request goes out with: the setting, except "auto" inside an agent run, where the loop's
    /// pick for this step (<see cref="Services.CodeAgentService.SmartEffort"/>, on the run's scope) stands in. Outside
    /// a run, or before the loop picked, "auto" stays "auto" (each provider's own default).</summary>
    public static string Effective(string? setting)
    {
        string s = Normalize(setting);
        var scope = Services.AgentRunScope.Current;
        if (s == Auto && scope?.StepEffort is { } step) s = Normalize(step);
        return Capped(s, scope?.EffortCap);
    }

    /// <summary><paramref name="level"/> held under <paramref name="cap"/> (a sub-agent's tier ceiling,
    /// <see cref="Services.AgentRunScope.EffortCap"/>): the lower of the two. "auto" under a cap is the cap itself — the
    /// provider's own default may think far more than a cheap sub-agent needs. No cap (or an unknown one): unchanged.</summary>
    public static string Capped(string? level, string? cap)
    {
        string l = Normalize(level), c = Normalize(cap);
        if (c == Auto) return l;
        if (l == Auto) return c;
        return Array.IndexOf(All, l) > Array.IndexOf(All, c) ? c : l;
    }

    /// <summary>A per-message effort (Anthropic's effort-only system messages) held under this flow's cap.</summary>
    public static string? CappedHere(string? level) =>
        level == null || Services.AgentRunScope.Current?.EffortCap is not { } cap ? level : Capped(level, cap);

    /// <summary>A stored or bound value as one of <see cref="All"/>; anything unknown is "auto".</summary>
    public static string Normalize(string? value)
    {
        string v = value?.Trim().ToLowerInvariant() ?? "";
        return Array.IndexOf(All, v) > 0 ? v : Auto;
    }
}
