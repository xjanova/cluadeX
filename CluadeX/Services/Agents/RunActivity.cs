using System.Collections.Concurrent;

namespace CluadeX.Services.Agents;

/// <summary>
/// What runs now under each chat — the line under the plan's active step ("2 sub-agents · 1 workflow · 1 background
/// command"), like Claude Code's "1 running task". It is a count, not a second progress model: sub-agents are counted
/// from the same <see cref="CodeAgentService.SubAgentEvent"/> the agent map draws, workflows from the engine's start
/// and end, background commands from <see cref="BackgroundProcessService"/>'s own owners (asked, never copied).
/// Thread-safe; <see cref="Changed"/> is raised on the calling thread with the chat's key.
/// </summary>
public static class RunActivity
{
    public sealed record Counts(int SubAgents, int Workflows, int Background)
    {
        public bool IsEmpty => SubAgents == 0 && Workflows == 0 && Background == 0;
    }

    // chat → ids of sub-agents running in it (a set: a "done" seen twice, or a "tool" after "start", counts once).
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> SubAgents = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> Workflows = new(StringComparer.Ordinal);

    /// <summary>Running background commands of a chat (set by the background service; null = none known).</summary>
    public static Func<string, int>? BackgroundCount { get; set; }

    public static event Action<string>? Changed;

    public static void SubAgentStarted(string? chat, string id) => Mark(SubAgents, chat, id, true);
    public static void SubAgentEnded(string? chat, string id) => Mark(SubAgents, chat, id, false);
    public static void WorkflowStarted(string? chat, string id) => Mark(Workflows, chat, id, true);
    public static void WorkflowEnded(string? chat, string id) => Mark(Workflows, chat, id, false);

    /// <summary>A chat's background commands changed (started / ended).</summary>
    public static void BackgroundChanged(string? chat)
    {
        if (!string.IsNullOrEmpty(chat)) Raise(chat);
    }

    private static void Mark(ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> map, string? chat, string id, bool on)
    {
        if (string.IsNullOrEmpty(chat)) return;
        var set = map.GetOrAdd(chat, _ => new(StringComparer.Ordinal));
        bool changed = on ? set.TryAdd(id, 0) : set.TryRemove(id, out _);
        if (changed) Raise(chat);
    }

    private static void Raise(string chat)
    {
        try { Changed?.Invoke(chat); } catch { /* a view's refresh must not break the run */ }
    }

    public static Counts Of(string? chat)
    {
        if (string.IsNullOrEmpty(chat)) return new(0, 0, 0);
        int bg = 0;
        try { bg = BackgroundCount?.Invoke(chat) ?? 0; } catch { }
        return new(SubAgents.TryGetValue(chat, out var s) ? s.Count : 0,
                   Workflows.TryGetValue(chat, out var w) ? w.Count : 0, bg);
    }

    /// <summary>The chat's run ended: its sub-agents are gone (one killed with its run raises no "done"). Background
    /// commands and a workflow resumed from its card outlive a run, so they stay.</summary>
    public static void ClearRun(string? chat)
    {
        if (string.IsNullOrEmpty(chat)) return;
        if (SubAgents.TryRemove(chat, out var s) && !s.IsEmpty) Raise(chat);
    }

    /// <summary>The line under the active step, in the UI language: "" when nothing runs.</summary>
    public static string Describe(Counts c, Func<string, string> loc)
    {
        if (c.IsEmpty) return "";
        var parts = new List<string>();
        if (c.SubAgents > 0) parts.Add(string.Format(loc(c.SubAgents == 1 ? "plan.running.subAgent" : "plan.running.subAgents"), c.SubAgents));
        if (c.Workflows > 0) parts.Add(string.Format(loc(c.Workflows == 1 ? "plan.running.workflow" : "plan.running.workflows"), c.Workflows));
        if (c.Background > 0) parts.Add(string.Format(loc(c.Background == 1 ? "plan.running.bg" : "plan.running.bgs"), c.Background));
        return string.Format(loc("plan.running"), string.Join(" · ", parts));
    }
}
