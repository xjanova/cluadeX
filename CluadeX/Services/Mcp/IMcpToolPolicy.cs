using CluadeX.Models;

namespace CluadeX.Services.Mcp;

/// <summary>
/// Special handling for the tools of particular MCP servers (e.g. Claude in Chrome): which tools are hidden, how
/// a description reads, how risky one CALL is, and how a raw result is turned into what the model sees.
/// Register in DI as <c>IMcpToolPolicy</c>. AgentToolService asks the FIRST policy whose <see cref="Owns"/>
/// returns true; tools no policy owns keep the default behaviour.
///
/// Tools are matched by (server, leaf) — never by the qualified name alone — because the same tool can reach
/// CluadeX two ways: straight from its own server ("claude-in-chrome" / "navigate") or through a bridge server
/// that prefixes the leaf ("brainx-brain" / "chrome__navigate"). <see cref="McpToolKey"/> splits that prefix off.
///
/// Every member is called on the agent thread and must be fast and must not throw (a throw counts as "no
/// opinion"). Nothing here may read, log or forward credential values.
/// </summary>
public interface IMcpToolPolicy
{
    /// <summary>Short stable id for logs.</summary>
    string Id { get; }

    /// <summary>Does this policy speak for the tool?</summary>
    bool Owns(McpToolKey tool);

    /// <summary>Keep the tool away from the model: not offered in any catalogue, and a call to it is refused.
    /// May change at runtime (e.g. switch_browser only while more than one browser is connected).</summary>
    bool Hide(McpToolKey tool);

    /// <summary>The description to offer instead of the server's, or null to keep it.</summary>
    string? RewriteDescription(McpToolKey tool, string description);

    /// <summary>How the permission gate treats this call, or null for the default classification.</summary>
    McpCallDecision? Classify(McpToolKey tool, IReadOnlyDictionary<string, string> arguments);

    /// <summary>
    /// Adjust the finished result before the model sees it: mark a "not connected" text as a failure, replace a
    /// raw extension error with a localized explanation, update connector state. <paramref name="raw"/> is the
    /// server's reply, or null when the call never got one (server not running, transport error — then
    /// <paramref name="result"/> is already a failure).
    /// </summary>
    void PostProcess(McpToolKey tool, ToolCall call, McpToolResult? raw, ToolResult result);
}

/// <summary>
/// One MCP tool as policies see it. <see cref="Leaf"/> is the tool's name on its server, exactly as the server
/// listed it; for a bridge server it carries the bridge prefix ("chrome__navigate"), which <see cref="Bridge"/>
/// and <see cref="BareName"/> split at the LAST "__" ("chrome" / "navigate").
/// </summary>
public readonly record struct McpToolKey(string ServerName, string Leaf)
{
    /// <summary>The bridge prefix of <see cref="Leaf"/> ("chrome" in "chrome__navigate"), or null.</summary>
    public string? Bridge
    {
        get
        {
            int sep = (Leaf ?? "").LastIndexOf("__", StringComparison.Ordinal);
            return sep > 0 ? Leaf![..sep] : null;
        }
    }

    /// <summary>The tool's own name without a bridge prefix ("navigate").</summary>
    public string BareName
    {
        get
        {
            string leaf = Leaf ?? "";
            int sep = leaf.LastIndexOf("__", StringComparison.Ordinal);
            return sep > 0 && sep + 2 < leaf.Length ? leaf[(sep + 2)..] : leaf;
        }
    }

    /// <summary>mcp__{server}__{leaf} — the name the model calls.</summary>
    public string QualifiedName => $"mcp__{ServerName}__{Leaf}";

    public static McpToolKey From(McpTool tool) => new(tool.ServerName, tool.Name);
}

/// <summary>How risky one MCP call is.</summary>
public enum McpCallRisk
{
    /// <summary>Runs without asking (looking at a page, reading the console).</summary>
    Free,
    /// <summary>Asks unless a rule naming the tool decides, or an earlier "Allow" for the same
    /// <see cref="McpCallDecision.OnceKey"/> in this chat covers it.</summary>
    Ask,
    /// <summary>Asks on every call, showing <see cref="McpCallDecision.Detail"/> (code it will run). A Deny rule
    /// still denies; no rule allows it silently.</summary>
    AlwaysAsk,
    /// <summary>Refused; <see cref="McpCallDecision.Detail"/> is the localized reason.</summary>
    Deny,
}

/// <summary>A policy's verdict on one MCP call.</summary>
public sealed record McpCallDecision(McpCallRisk Risk)
{
    /// <summary>Ask: one "Allow" covers every later call with this key in the same chat
    /// (e.g. "chrome:act:example.com" — ask once per site per chat).</summary>
    public string? OnceKey { get; init; }

    /// <summary>Ask: the URL the call goes to. The user's URL rules (scope "browse") decide first.</summary>
    public string? Url { get; init; }

    /// <summary>Shown in the prompt under the call (the code, the risky items of a batch); the reason for Deny.</summary>
    public string? Detail { get; init; }

    public static McpCallDecision Free { get; } = new(McpCallRisk.Free);
}
