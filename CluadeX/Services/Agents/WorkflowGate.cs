using CluadeX.Models;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Agents;

/// <summary>
/// Workflows are opt-in, like Claude Code's ultracode: many agents spend many tokens. On for a chat when its Ultra switch
/// is on, or when the owner's own message in this turn asks for one. Off by default; the plan list and a single
/// agent_spawn never need it. The owner's message is recorded by the chat page on Send (a pipe caller's text is not the
/// owner's words and is never recorded).
/// </summary>
public static class WorkflowGate
{
    private static readonly HashSet<string> Ultra = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> OwnerWords = new(StringComparer.Ordinal);
    private static readonly object Lock = new();
    private static string? _file;

    /// <summary>Words that opt in: "ultracode", "use/run a workflow", "multi-agent", "fan out agents", Thai equivalents.</summary>
    private static readonly Regex Asked = new(
        @"\bultra\s*code\b|\bultracode\b|\b(use|run|start)\s+(a\s+|the\s+)?workflows?\b|\bmulti[- ]?agents?\b|\bfan\s*out\b.*\bagents?\b|"
        + @"\borchestrat\w*\b.*\bagents?\b|อัลตร้า\s*โค้ด|เวิร์[กค]\s*โฟลว์|หลาย\s*เอเจน|แตก\s*เอเจน|ใช้\s*workflow",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    /// <summary>Persist the per-chat switch here (DataRoot/ultra-chats.json). Null = memory only (the harness).</summary>
    public static void Load(string? dataRoot)
    {
        lock (Lock)
        {
            _file = dataRoot == null ? null : Path.Combine(dataRoot, "ultra-chats.json");
            Ultra.Clear();
            try
            {
                if (_file != null && File.Exists(_file))
                    foreach (var id in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file)) ?? new()) Ultra.Add(id);
            }
            catch { /* a damaged file = every chat off, the safe default */ }
        }
    }

    public static event Action<string>? UltraChanged;

    public static bool IsUltra(string? chat)
    {
        if (string.IsNullOrEmpty(chat)) return false;
        lock (Lock) return Ultra.Contains(chat);
    }

    public static void SetUltra(string chat, bool on)
    {
        if (string.IsNullOrEmpty(chat)) return;
        lock (Lock)
        {
            if (!(on ? Ultra.Add(chat) : Ultra.Remove(chat))) return;
            try { if (_file != null) File.WriteAllText(_file, JsonSerializer.Serialize(Ultra.ToList())); } catch { }
        }
        UltraChanged?.Invoke(chat);
    }

    /// <summary>The owner sent <paramref name="text"/> in <paramref name="chat"/> (this turn's words; the next turn's replace
    /// them). <paramref name="append"/>: typed into the running turn — it adds to the turn's words instead.</summary>
    public static void NoteOwnerMessage(string chat, string? text, bool append = false)
    {
        if (string.IsNullOrEmpty(chat)) return;
        lock (Lock) OwnerWords[chat] = append && OwnerWords.TryGetValue(chat, out var before) ? before + "\n" + text : text ?? "";
    }

    /// <summary>Did these words ask for a workflow? Pure, for the harness.</summary>
    public static bool AsksForWorkflow(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { return Asked.IsMatch(text); } catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>Why: "ultra" (the switch), "asked" (the owner's words), or null = not allowed.</summary>
    public static string? Why(string? chat)
    {
        if (string.IsNullOrEmpty(chat)) return null;
        if (IsUltra(chat)) return "ultra";
        string? words;
        lock (Lock) OwnerWords.TryGetValue(chat, out words);
        return AsksForWorkflow(words) ? "asked" : null;
    }

    public static bool Allowed(string? chat) => Why(chat) != null;

    /// <summary>The chat the current run belongs to (a sub-agent's run belongs to its root chat).</summary>
    public static string? CurrentChat
    {
        get
        {
            var s = AgentRunScope.Current;
            while (s?.Parent != null) s = s.Parent;
            return s?.SessionKey;
        }
    }

    /// <summary>The split guide for a chat that opted in, else null. The model was never told Ultra was on: the switch only
    /// opened the workflow tool, and a chat with Ultra on still did everything in one agent (GigLive, 2026-11-03).</summary>
    public static string? SplitGuideFor(string? chat, bool cli) => Allowed(chat) ? SplitGuide(cli) : null;

    /// <summary>The split guide for a chat that opted in, in <paramref name="cli"/>'s own tool names, else null. Only Claude
    /// Dev used to get it, so Ultra on a Codex / Gemini / Antigravity / Grok chat changed nothing (parity audit 2026-11-06).</summary>
    public static string? SplitGuideFor(string? chat, AiProviderType cli) => Allowed(chat) ? SplitGuide(cli) : null;

    /// <summary>How to decide between doing it yourself, sub-agents, a workflow and a chat of its own. Pure.
    /// <paramref name="cli"/>: Claude Code's names (its Agent tool, mcp__cluadex__*).</summary>
    public static string SplitGuide(bool cli) => cli
        ? SplitGuide("the Agent tool", "mcp__cluadex__")
        : SplitGuide("agent_spawn", "");

    /// <summary>The guide in a CLI agent's own names: its spawn tool and how it calls the cluadex server's tools. Pure.
    /// Antigravity has no sub-agent tool: its guide goes from doing it itself straight to the workflow.</summary>
    public static string SplitGuide(AiProviderType cli) => cli switch
    {
        AiProviderType.ClaudeDev => SplitGuide(true),
        AiProviderType.OpenAIDev => CodexSplitGuide(),
        AiProviderType.GeminiDev => SplitGuide($"your sub-agents ({CliAgentPolicy.ReadOnlyType(cli)})", "", " (MCP server cluadex)"),
        AiProviderType.GrokDev => SplitGuide($"spawn_subagent ({CliAgentPolicy.ReadOnlyType(cli)} for read-only work)", "cluadex__", " (through use_tool)"),
        AiProviderType.AntigravityDev => SplitGuide(null, "", " (MCP server cluadex)"),
        _ => SplitGuide(false),
    };

    /// <summary>
    /// Codex's guide. Its own spawn_agent agents share its folder (no worktree, no spawn hook), so they only read; edits
    /// are split through the cluadex workflow with worktree isolation, each agent its own `codex exec -C &lt;worktree&gt;` on
    /// the owner's ChatGPT plan. Until 2026-11-09 the guide said "every edit stays with you": a Codex chat with Ultra on,
    /// fixing 152–202 build errors, fixed them one by one in one agent.
    /// </summary>
    private static string CodexSplitGuide() =>
        "The user switched Ultra on for this chat (or asked for multi-agent work): you may split the work, and you decide how. "
        + "Go down this list and take the first that fits each piece: "
        + "(1) Do it yourself — one or two files, steps that depend on each other, or work that needs this chat's context. "
        + $"(2) spawn_agent (agent_type \"{CliAgentPolicy.CodexExplorer}\") — two or more independent parts that are only reading or "
        + "checking; its agents share your folder, so they never edit. "
        + "(3) workflow (MCP server cluadex) for edits in several files at once — above all a red build or test run with many "
        + "errors: run it once, group the errors by file (a group = files that only need each other; no file in two groups), "
        + "then call workflow with one agents phase that has isolation \"worktree\", gate = the build or test command, and "
        + "merge; give each agent its group's error lines and the files it owns, complete in its prompt. Each agent runs as its "
        + "own codex exec in its own git worktree on the user's ChatGPT plan, and only diffs that turn the gate green are merged "
        + "into the project. Read-only agents of a workflow may run on the model on this PC when the user turned that on. "
        + "Afterwards run the command again and fix what is left yourself. "
        + "(4) new_chat (MCP server cluadex) — a separate deliverable the user may want to steer or review on its own; put it "
        + "away with close_chat once its result is in. A new_chat in THIS project that edits files while you are still working "
        + "here gets isolation \"worktree\" (its own branch; you merge it or open a PR when it is told done). "
        + "Never split pieces that edit the same files, or hand off a piece whose result you need for your very next step. "
        + "Every brief is complete on its own: the agent cannot see this conversation. Say in one line how you split the work "
        + "and why, and put the pieces in your plan.";

    private static string SplitGuide(string? spawn, string prefix, string server = "")
    {
        string wf = prefix + "workflow" + server;
        string chat = prefix + "new_chat";
        string close = prefix + "close_chat";
        return "The user switched Ultra on for this chat (or asked for multi-agent work): you may split the work, and you decide "
             + "how. Go down this list and take the first that fits each piece: "
             + "(1) Do it yourself — one or two files, steps that depend on each other, or work that needs this chat's context. "
             + (spawn != null
                 ? $"(2) Sub-agents with {spawn} — two or more independent parts that are mostly reading or checking (several "
                   + "folders, questions, logs); start them in one response so they run in parallel. "
                 : "(2) You have no sub-agent tool of your own: independent reading or checking goes to the workflow below. ")
             + $"(3) {wf} — three or more independent pieces of real work, or findings that must be checked (a review, an audit, a "
             + "multi-part build): phases of agents with JSON schemas, and refuters on what they find. "
             + $"(4) {chat} — a separate deliverable the user may want to steer or review on its own (another app or repository, a "
             + "design or asset track beside the code), or a new standalone request typed while you work; it runs in parallel and "
             + $"shows under this chat — put it away with {close} once its result is in. A {chat} in THIS project that edits files "
             + "while you (or another split chat) still work here gets isolation \"worktree\": its own branch in its own git "
             + "worktree, which you merge or open a PR from when it is done. "
             + "Never split pieces that edit the same files, or hand off a piece whose result you need for your very next step. "
             + "Every brief is complete on its own: the agent or chat cannot see this conversation. Say in one line how you split "
             + "the work and why, and put the pieces in your plan.";
    }
}
