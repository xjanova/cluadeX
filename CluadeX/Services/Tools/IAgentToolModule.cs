using CluadeX.Models;
using CluadeX.Services.Providers;

namespace CluadeX.Services.Tools;

/// <summary>
/// A family of built-in agent tools that lives outside <see cref="AgentToolService"/> — the background
/// processes (bg_*) and the built-in browser (browser_*). Register an implementation in DI as
/// <c>IAgentToolModule</c>; AgentToolService then uses it everywhere a built-in tool is handled:
/// <list type="bullet">
/// <item>the native schema build (only tools whose <see cref="GetRefusal"/> is null are offered),</item>
/// <item>tool-name resolution (<see cref="ResolveToolType"/>, after the built-in names — a module never takes a
///   built-in name over),</item>
/// <item>the feature gate (<see cref="GetRefusal"/> — its text is what the model and the user are told),</item>
/// <item>the permission block (<see cref="DescribePermission"/>; scope "browse" is matched with
///   PermissionService.FindUrlRule, every other scope with FindRule),</item>
/// <item>the dispatch default branch (<see cref="ExecuteAsync"/>), after PreToolUse hooks ran,</item>
/// <item>the small-context local catalogue (<see cref="LocalCoreToolNames"/>) and the system prompt
///   (<see cref="GetPromptSection"/>, only when one of the module's tools is offered).</item>
/// </list>
/// Implementations must not throw out of any member except <see cref="ExecuteAsync"/> (whose exceptions become a
/// failed tool result); a throwing schema or permission member is treated as "not offered" / "ask".
/// </summary>
public interface IAgentToolModule
{
    /// <summary>Short stable id for logs ("background", "browser").</summary>
    string Id { get; }

    /// <summary>The <see cref="ToolType"/> members this module executes. Two modules must not share one.</summary>
    IReadOnlyCollection<ToolType> ToolTypes { get; }

    /// <summary>True when the tools are useful with no project folder open. False (the default for built-ins)
    /// keeps them out of a plain chat, like the file tools.</summary>
    bool WorksWithoutProject { get; }

    /// <summary>Map a tool name as a model writes it — canonical ("bg_start"), alias ("task_create"), any case —
    /// to one of <see cref="ToolTypes"/>, or null when the name is not this module's.</summary>
    ToolType? ResolveToolType(string toolName);

    /// <summary>The native schemas of every tool this module has (names must be ^[a-zA-Z0-9_-]{1,64}$ and resolve
    /// through <see cref="ResolveToolType"/>). AgentToolService filters them by <see cref="GetRefusal"/>, so the
    /// offer can never disagree with the gate.</summary>
    IEnumerable<ToolSchema> GetToolSchemas();

    /// <summary>Null when <paramref name="type"/> may run right now; otherwise the localized reason it may not
    /// (license, Features switch, missing engine) — it names the reason and the one action that fixes it.</summary>
    string? GetRefusal(ToolType type);

    /// <summary>What the permission gate judges for this call. Called only when the call passed
    /// <see cref="GetRefusal"/>, before PreToolUse hooks.</summary>
    ToolPermission DescribePermission(ToolCall call, ToolModuleContext context);

    /// <summary>Run the call. Never put image base64 in <see cref="ToolResult.Output"/> — use
    /// <see cref="ToolResult.Images"/> and save the file through <see cref="Vision.ToolImageStore"/>.</summary>
    Task<ToolResult> ExecuteAsync(ToolCall call, ToolModuleContext context, CancellationToken ct);

    /// <summary>Tool names admitted to the CORE set a small-context local model gets (below 16k ctx), next to
    /// read_file / run_command. Re-read before every turn, so it may depend on the open project.</summary>
    IReadOnlyCollection<string> LocalCoreToolNames { get; }

    /// <summary>Text for the system prompt, added only when at least one of this module's tools is offered
    /// this turn. Null/empty = nothing. Keep it short — it is paid for on every request.</summary>
    string? GetPromptSection(PromptSectionContext context);
}

/// <summary>What the permission gate decides on for one module tool call.</summary>
public sealed record ToolPermission
{
    /// <summary>"read", "write", "execute" or "browse". Rules are written per scope; "browse" resources are URLs
    /// and are matched only by URL rules (PermissionService.FindUrlRule).</summary>
    public string Scope { get; init; } = "execute";

    /// <summary>What a rule is matched against and what the prompt names: the command for "execute", the path
    /// for "read"/"write", the URL for "browse". Empty = the tool name.</summary>
    public string Resource { get; init; } = "";

    /// <summary>With no matching user rule the call runs without asking (reading inside the project, a localhost
    /// page). A user rule still wins — a Deny rule denies a free call.</summary>
    public bool IsFree { get; init; }

    /// <summary>Asked about on every call, even when a rule allows it or the permission system is switched off.
    /// A Deny rule still denies.</summary>
    public bool AlwaysAsk { get; init; }

    /// <summary>When set, one "Allow" answers every later call with the same key in this chat (e.g. clicks on one
    /// external site: "click:example.com"). Cleared when the chat changes.</summary>
    public string? OnceKey { get; init; }

    /// <summary>Extra lines for the prompt: the code about to run, the target folder, the risky items.</summary>
    public string? Detail { get; init; }

    /// <summary>A call that needs no permission at all.</summary>
    public static ToolPermission Free(string scope = "read", string resource = "") =>
        new() { Scope = scope, Resource = resource, IsFree = true };

    /// <summary>A call that asks unless a user rule decides.</summary>
    public static ToolPermission Ask(string scope, string resource, string? detail = null, string? onceKey = null) =>
        new() { Scope = scope, Resource = resource, Detail = detail, OnceKey = onceKey };
}

/// <summary>Where a module call runs.</summary>
public sealed class ToolModuleContext
{
    /// <summary>The chat the call belongs to (a safe file-name token) — keys per-chat state such as saved
    /// screenshots (ToolImageStore) and ask-once approvals.</summary>
    public string SessionKey { get; init; } = "default";

    /// <summary>The open project folder, or null when none is open.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Where run_command's persistent `cd` currently is (inside the project), or the project root.</summary>
    public string? ShellDirectory { get; init; }

    /// <summary>The active model runs locally (llama.cpp / LLamaSharp / Ollama) — budgets are smaller.</summary>
    public bool IsLocalProvider { get; init; }

    /// <summary>Largest text output this call should return, in characters.</summary>
    public int OutputBudgetChars { get; init; } = 30_000;
}
