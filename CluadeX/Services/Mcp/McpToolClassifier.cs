using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services.Mcp;

/// <summary>
/// How risky one MCP call is, decided on the tool's REAL identity: the server it lives on and its leaf name as
/// that server listed it — never on the qualified name the model calls.
///
/// The native tool loop hands the gate only "mcp__brainx-brain__brain_search". The old gate classified that whole
/// string, found it in no list, and asked — six prompts for brain_recall / task_queue / cowork_task / brain_search /
/// brain_list / agent_activity in one real run (2026-10-09). Here the name is resolved to (server, leaf) through the
/// tool registry first ("brainx-brain" / "brain_search"), including a leaf that a bridge server prefixes
/// ("brainx-brain" / "chrome__navigate"), and then:
///   1. a policy that owns the tool (<see cref="IMcpToolPolicy"/>, e.g. Claude in Chrome) decides the call;
///   2. otherwise, on a brain server, the leaf is looked up: known read-only tools run, writes to the user's own
///      memory run; anything else — unknown tools, and every tool of a third-party server — asks, with its
///      arguments shown (a user rule naming the tool or its server can change that).
/// </summary>
public static class McpToolClassifier
{
    /// <summary>BrainX tools that only look things up.</summary>
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "brain_search", "brain_semantic_search", "brain_recall", "brain_get_note", "brain_list", "brain_walk",
        "brain_bundle", "brain_bundles_list", "brain_stats", "brain_expertise", "brain_get_backlinks",
        "brain_suggest_links", "brain_suggest_topics", "brain_find_contradictions", "brain_scope_list",
        "brain_audit", "bridge_status", "agent_peers", "agent_inbox", "agent_activity", "cowork_read",
        "cowork_who", "task_queue", "fetch_review_queue", "media_status", "ssh_profiles_list",
    };

    /// <summary>BrainX tools that add to the user's own memory — saving a lesson must not need a click.</summary>
    private static readonly HashSet<string> MemoryWriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "brain_create_note", "brain_append_note", "brain_remember", "brain_mark_verified", "brain_synthesize",
    };

    /// <summary>BrainX's agent coordination: handing a task to another agent, updating it, the cowork room. They post
    /// to the owner's own agent bus on this machine (owner 2026-10-18: "เปิดเครื่องมือ agent ของ brainx" — every call
    /// asked). agent_send (a direct message to any peer) and the remote tools (ssh_run) still ask.</summary>
    public static readonly HashSet<string> AgentBusTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "task_handoff", "task_update", "cowork_join", "cowork_leave", "cowork_say", "cowork_task",
    };

    /// <summary>The agent tools a capped tool list keeps ahead of the brain's other tools (the readers and the bus).</summary>
    public static readonly HashSet<string> AgentToolLeaves = new(StringComparer.OrdinalIgnoreCase)
    {
        "task_queue", "task_handoff", "task_update", "agent_inbox", "agent_peers", "agent_activity",
        "cowork_join", "cowork_leave", "cowork_read", "cowork_say", "cowork_task", "cowork_who",
    };

    /// <summary>A bridged editor/browser tool that only looks (unity__find_gameobjects, roblox__script_read): the verb
    /// must be a whole word — "finder_launch" or "getter_x" is not a lookup.</summary>
    private static readonly Regex ReadVerb = new(
        "^(get|find|read|list|search|inspect|describe|validate|script_read|script_grep|script_search)(_|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A word anywhere in the name that means the call changes something (or does more than one thing:
    /// "read_and_erase", "get_or_add"). "get_and_delete" or "search_replace" start like a lookup and are not one, so
    /// they keep asking.</summary>
    private static readonly Regex WriteWord = new(
        @"(^|_)(write|edit|delete|remove|replace|create|update|set|put|post|patch|execute|exec|run|apply|move|rename|"
        + @"insert|upload|send|kill|stop|start|install|commit|push|click|type|press|navigate|eval|drop|reset|clear|"
        + @"erase|purge|wipe|terminate|launch|spawn|add|fix|modify|overwrite|save|submit|trash|archive|deploy|publish|"
        + @"share|grant|revoke|approve|merge|and|or|then)(_|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The default classification of one tool name. Unknown tools are Risky.
    /// A qualified "mcp__server__leaf" is judged with its server (<see cref="ClassifyLeaf(string, string?)"/>); a bare
    /// leaf (no server known — the apitest table, an old [ACTION:] call) gets the BrainX rules. This is the rule the gate
    /// applies when no policy owns the tool; <see cref="AgentToolService.ClassifyMcpTool"/> forwards here.
    /// </summary>
    public static AgentToolService.McpRisk ClassifyLeaf(string? name)
    {
        if (TryParseQualified(name, out var server, out var leaf)) return ClassifyLeaf(leaf, server);
        return ClassifyLeaf(name, null);
    }

    /// <summary>
    /// The default classification of <paramref name="leaf"/> on <paramref name="serverName"/>. The read-only and
    /// memory-write lists, and the "a bridged lookup is free" rule, describe BrainX's own tools and its editor/browser
    /// bridges — so they apply only on a brain server (<see cref="McpServerManager.LooksLikeBrain"/>, the same test the
    /// Chrome policy uses). A third-party server's "brain_search" asks: its arguments could carry anything anywhere.
    /// <paramref name="serverName"/> null = not known (a bare leaf): the BrainX rules apply, as they always did.
    /// </summary>
    public static AgentToolService.McpRisk ClassifyLeaf(string? leaf, string? serverName)
    {
        leaf ??= "";
        if (leaf.Length == 0) return AgentToolService.McpRisk.Risky;
        if (serverName != null && !McpServerManager.LooksLikeBrain(serverName)) return AgentToolService.McpRisk.Risky;
        if (ReadOnlyTools.Contains(leaf)) return AgentToolService.McpRisk.Read;
        if (MemoryWriteTools.Contains(leaf)) return AgentToolService.McpRisk.MemoryWrite;
        if (AgentBusTools.Contains(leaf)) return AgentToolService.McpRisk.AgentBus;
        // Editor/browser bridges (unity__, unreal__, roblox__, chrome__): looking is free, changing it asks.
        int sep = leaf.LastIndexOf("__", StringComparison.Ordinal);
        if (sep > 0 && sep + 2 < leaf.Length)
        {
            string bare = leaf[(sep + 2)..];
            if (ReadVerb.IsMatch(bare) && !WriteWord.IsMatch(bare)) return AgentToolService.McpRisk.Read;
        }
        return AgentToolService.McpRisk.Risky;
    }

    /// <summary>"mcp__server__leaf" → leaf (split at the first "__" after the server); anything else unchanged.</summary>
    public static string LeafOf(string name) => TryParseQualified(name, out _, out var leaf) ? leaf : name;

    /// <summary>
    /// Split a qualified name WITHOUT the registry: "mcp__brainx-brain__chrome__navigate" → "brainx-brain" /
    /// "chrome__navigate". Server keys may contain '-', '.' and single '_' but are cut at the first "__"; the
    /// registry, which knows the real split, always takes precedence (<see cref="Resolve"/>).
    /// </summary>
    public static bool TryParseQualified(string? name, out string server, out string leaf)
    {
        server = leaf = "";
        if (string.IsNullOrEmpty(name) || !name.StartsWith("mcp__", StringComparison.Ordinal)) return false;
        int sep = name.IndexOf("__", 5, StringComparison.Ordinal);
        if (sep <= 5 || sep + 2 >= name.Length) return false;
        server = name[5..sep];
        leaf = name[(sep + 2)..];
        return true;
    }

    /// <summary>
    /// The server and real leaf of an MCP call: what the [ACTION:] parser already set, else the registry entry for
    /// the qualified name (how the native loop calls), else the name split by <see cref="TryParseQualified"/>.
    /// <paramref name="known"/> is true when the tool is in the registry (or the parser set it).
    /// </summary>
    public static McpToolKey? Resolve(ToolCall call, McpToolRegistry? registry, out bool known)
    {
        known = false;
        if (!string.IsNullOrEmpty(call.McpServerName) && !string.IsNullOrEmpty(call.McpToolName))
        {
            known = true;
            return new McpToolKey(call.McpServerName!, call.McpToolName!);
        }
        var tool = registry?.ResolveTool(call.ToolName);
        if (tool != null)
        {
            known = true;
            return McpToolKey.From(tool);
        }
        return TryParseQualified(call.ToolName, out var server, out var leaf) ? new McpToolKey(server, leaf) : null;
    }

    /// <summary>The first policy that owns <paramref name="key"/> (a policy that throws owns nothing).</summary>
    public static IMcpToolPolicy? OwnerOf(McpToolKey key, IEnumerable<IMcpToolPolicy>? policies)
    {
        if (policies == null) return null;
        foreach (var p in policies)
        {
            if (p == null) continue;
            try { if (p.Owns(key)) return p; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[mcp policy {SafeId(p)}] Owns threw: {ex.Message}"); }
        }
        return null;
    }

    /// <summary>
    /// Classify one call: the owning policy first (hidden? its verdict on these arguments?), then the default rule
    /// on the real leaf. Never throws — a policy that throws has no opinion.
    /// </summary>
    public static McpCallClassification Classify(McpToolKey key, IReadOnlyDictionary<string, string>? arguments,
        IEnumerable<IMcpToolPolicy>? policies)
    {
        var owner = OwnerOf(key, policies);
        bool hidden = false;
        McpCallDecision? decision = null;
        if (owner != null)
        {
            try { hidden = owner.Hide(key); } catch { hidden = false; }
            if (!hidden)
            {
                try { decision = owner.Classify(key, arguments ?? new Dictionary<string, string>()); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[mcp policy {SafeId(owner)}] Classify threw: {ex.Message}"); }
            }
        }
        return new McpCallClassification(key, owner, hidden, decision, ClassifyLeaf(key.Leaf, key.ServerName));
    }

    /// <summary>Arguments longer than this are shown as their start and their END with the count left out between:
    /// the dangerous part of a long argument is as likely to be at the end, and a cut that hid it let the user approve
    /// what they never saw.</summary>
    public const int MaxShownArgumentChars = 2000;

    /// <summary>What the permission prompt shows for an MCP call: the server, the tool and its arguments — in full up
    /// to <see cref="MaxShownArgumentChars"/>, else head + tail and how many characters are not shown.</summary>
    public static string DescribeCall(ToolCall call, string leaf)
    {
        string args = call.Arguments.Count == 0 ? "(no arguments)"
            : System.Text.Json.JsonSerializer.Serialize(call.Arguments,
                new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (args.Length > MaxShownArgumentChars)
        {
            const int head = 1400, tail = 500;
            int hidden = args.Length - head - tail;
            args = args[..head] + $"\n… [{hidden:N0} characters not shown — {args.Length:N0} in all] …\n" + args[^tail..];
        }
        return $"run {call.McpServerName ?? "MCP"} · {leaf} {args}";
    }

    private static string SafeId(IMcpToolPolicy p)
    {
        try { return p.Id; } catch { return p.GetType().Name; }
    }
}

/// <summary>One MCP call as the permission gate sees it.</summary>
/// <param name="Key">Server + real leaf.</param>
/// <param name="Policy">The policy that owns the tool, or null.</param>
/// <param name="Hidden">The owning policy keeps this tool away from the agent: the call is refused.</param>
/// <param name="Decision">The owning policy's verdict on this call, or null for the default rule.</param>
/// <param name="Risk">The default rule's verdict on the leaf (used when <paramref name="Decision"/> is null).</param>
public sealed record McpCallClassification(McpToolKey Key, IMcpToolPolicy? Policy, bool Hidden, McpCallDecision? Decision,
    AgentToolService.McpRisk Risk);
