namespace CluadeX.Services.Agents;

/// <summary>
/// The model a sub-agent's requests go out on, set for its async flow only (<see cref="DispatchPolicy.ModelFor"/>):
/// an explore sub-agent on Haiku while the run itself stays on Opus. The API providers read it where they read the
/// configured model; null = the configured model. Requests outside the sub-agent's flow never see it.
/// </summary>
public static class SubAgentModel
{
    private static readonly AsyncLocal<string?> Slot = new();

    public static string? Override => Slot.Value;

    /// <summary>Set for the rest of the calling async method (and what it awaits); restored when that method returns.</summary>
    public static void Use(string? model) => Slot.Value = string.IsNullOrWhiteSpace(model) ? null : model.Trim();

    /// <summary>The configured model, unless this flow is a sub-agent routed to another.</summary>
    public static string Pick(string configured) => Override ?? configured;
}
