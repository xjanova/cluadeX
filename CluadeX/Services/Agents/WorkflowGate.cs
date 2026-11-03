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

    /// <summary>How to decide between doing it yourself, sub-agents, a workflow and a chat of its own. Pure.
    /// <paramref name="cli"/>: Claude Code's names (its Agent tool, mcp__cluadex__*).</summary>
    public static string SplitGuide(bool cli)
    {
        string spawn = cli ? "the Agent tool" : "agent_spawn";
        string wf = cli ? "mcp__cluadex__workflow" : "workflow";
        string chat = cli ? "mcp__cluadex__new_chat" : "new_chat";
        string close = cli ? "mcp__cluadex__close_chat" : "close_chat";
        return "The user switched Ultra on for this chat (or asked for multi-agent work): you may split the work, and you decide "
             + "how. Go down this list and take the first that fits each piece: "
             + "(1) Do it yourself — one or two files, steps that depend on each other, or work that needs this chat's context. "
             + $"(2) Sub-agents with {spawn} — two or more independent parts that are mostly reading or checking (several folders, "
             + "questions, logs); start them in one response so they run in parallel. "
             + $"(3) {wf} — three or more independent pieces of real work, or findings that must be checked (a review, an audit, a "
             + "multi-part build): phases of agents with JSON schemas, and refuters on what they find. "
             + $"(4) {chat} — a separate deliverable the user may want to steer or review on its own (another app or repository, a "
             + "design or asset track beside the code), or a new standalone request typed while you work; it runs in parallel and "
             + $"shows under this chat — put it away with {close} once its result is in. "
             + "Never split pieces that edit the same files, or hand off a piece whose result you need for your very next step. "
             + "Every brief is complete on its own: the agent or chat cannot see this conversation. Say in one line how you split "
             + "the work and why, and put the pieces in your plan.";
    }
}
