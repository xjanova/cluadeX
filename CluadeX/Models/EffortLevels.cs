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
        return s == Auto && Services.AgentRunScope.Current?.StepEffort is { } step ? Normalize(step) : s;
    }

    /// <summary>A stored or bound value as one of <see cref="All"/>; anything unknown is "auto".</summary>
    public static string Normalize(string? value)
    {
        string v = value?.Trim().ToLowerInvariant() ?? "";
        return Array.IndexOf(All, v) > 0 ? v : Auto;
    }
}
