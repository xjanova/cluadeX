namespace CluadeX.Services.Tools;

/// <summary>
/// A section of the system prompt that belongs to a group of tools which are not a tool module — e.g. the
/// Claude in Chrome tools, which arrive as MCP tools. Register in DI as <c>IPromptSectionProvider</c>.
/// CodeAgentService adds the section only when at least one tool the provider owns is offered to the model this
/// turn, so a model is never taught about tools it does not have. Must not throw (a throw = no section).
/// </summary>
public interface IPromptSectionProvider
{
    /// <summary>Short stable id for logs.</summary>
    string Id { get; }

    /// <summary>Does this offered tool name belong to the section? Names are as offered to the model:
    /// "browser_open", or a qualified MCP name such as "mcp__claude-in-chrome__navigate" or
    /// "mcp__brainx-brain__chrome__navigate" (a bridge prefix inside the leaf).</summary>
    bool OwnsTool(string offeredToolName);

    /// <summary>The section text, or null/empty for nothing. Keep it short — it is paid for on every request.</summary>
    string? BuildSection(PromptSectionContext context);
}

/// <summary>What a prompt section may adapt to.</summary>
public sealed class PromptSectionContext
{
    /// <summary>Every tool name offered this turn (built-ins, module tools, qualified MCP names).</summary>
    public required IReadOnlyCollection<string> OfferedToolNames { get; init; }

    /// <summary>The model runs locally (small context — be brief).</summary>
    public bool IsLocalModel { get; init; }

    /// <summary>The model's context window in tokens (very large for API models).</summary>
    public int ContextTokens { get; init; }

    /// <summary>Tools go through the provider's native tool calling (otherwise the [ACTION:] text protocol).</summary>
    public bool NativeTools { get; init; }

    /// <summary>UI language, "en" or "th" — the reply language, not the language of the section.</summary>
    public string Language { get; init; } = "en";

    /// <summary>A project folder is open.</summary>
    public bool HasProject { get; init; }
}
