namespace CluadeX.Services.Agents;

/// <summary>
/// The model a sub-agent's requests go out on, set for its async flow only (<see cref="DispatchPolicy.ModelFor"/>):
/// an explore sub-agent on Haiku while the run itself stays on Opus. The API providers read it where they read the
/// configured model; null = the configured model. Requests outside the sub-agent's flow never see it.
/// <para>A chat may also have a model of its own (a split chat opened with new_chat model "fable"): its run's flow
/// carries it in <see cref="ChatModel"/>, under the sub-agent slot — a sub-agent routed elsewhere still wins.</para>
/// </summary>
public static class SubAgentModel
{
    private static readonly AsyncLocal<string?> Slot = new();
    private static readonly AsyncLocal<string?> ChatSlot = new();
    private static readonly AsyncLocal<int?> BudgetSlot = new();

    public static string? Override => Slot.Value;

    /// <summary>The model of the chat this flow runs for, when that chat has its own (null = the app's pick).</summary>
    public static string? ChatModel => ChatSlot.Value;

    /// <summary>Set for the rest of the calling async method (and what it awaits); restored when that method returns.</summary>
    public static void Use(string? model) => Slot.Value = string.IsNullOrWhiteSpace(model) ? null : model.Trim();

    /// <summary>This flow is a run of a chat with its own model (null = none). Set before the run starts.</summary>
    public static void UseChat(string? model) => ChatSlot.Value = string.IsNullOrWhiteSpace(model) ? null : model.Trim();

    /// <summary>The configured model, unless this flow is a sub-agent routed to another or a chat with its own.</summary>
    public static string Pick(string configured) => Override ?? ChatModel ?? configured;

    /// <summary>A task budget (tokens) for this flow's Fable requests (<c>output_config.task_budget</c>), or null.</summary>
    public static int? TaskBudget => BudgetSlot.Value;

    /// <summary>Set this flow's task budget (null = none). Below the API's minimum it goes out at the minimum.</summary>
    public static void UseTaskBudget(int? tokens) =>
        BudgetSlot.Value = tokens is > 0 ? Math.Max(DispatchPolicy.TaskBudgetMinimum, tokens.Value) : null;
}
