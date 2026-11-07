using CluadeX.Models;

namespace CluadeX.Services.Agents;

/// <summary>
/// The plan's step operations, the way Claude Code's update_plan takes them: <c>advance</c> finishes the item in
/// progress and starts the next pending one; <c>done</c> (1-based numbers) finishes items and <c>start</c> starts one.
/// They change the run's own plan (<see cref="AgentRunScope.Todos"/>), so the plan card and the plan bar follow through
/// the same TodoChanged as a whole-list todo_write. Pure, for the contract harness.
/// </summary>
public static class PlanOps
{
    /// <summary>Apply the step operations to <paramref name="items"/> in place. Null = done; otherwise the error to
    /// return to the model (nothing was changed then).</summary>
    public static string? Apply(List<TodoItem> items, bool advance, IReadOnlyList<int> done, int? start)
    {
        if (items.Count == 0) return "There is no plan yet: send the whole list first (todos).";
        foreach (int n in done.Append(start ?? 1))
            if (n < 1 || n > items.Count) return $"There is no step {n}: the plan has {items.Count} (numbered from 1).";

        if (advance)
        {
            int cur = items.FindIndex(t => t.Status == "in_progress");
            if (cur >= 0) items[cur].Status = "completed";
            int next = items.FindIndex(Math.Max(0, cur + 1), t => t.Status == "pending");
            if (next < 0) next = items.FindIndex(t => t.Status == "pending");
            if (next >= 0 && start == null) items[next].Status = "in_progress";
        }
        foreach (int n in done) items[n - 1].Status = "completed";
        if (start is int s)
        {
            // One item in progress at a time: starting one hands the others back to pending (or keeps them done).
            foreach (var t in items.Where(t => t.Status == "in_progress")) t.Status = "pending";
            items[s - 1].Status = "in_progress";
        }
        return null;
    }

    /// <summary>
    /// A whole list sent with <c>todos</c>, brought to the rule the card shows: exactly one item in progress while any work
    /// is left. A second "in_progress" goes back to pending (the first one wins); no item in progress while some are
    /// pending starts the first pending one. Returns what was changed ("" = nothing), for the tool's answer.
    /// </summary>
    public static string Normalize(List<TodoItem> items)
    {
        var notes = new List<string>();
        int first = items.FindIndex(t => t.Status == "in_progress");
        int extra = 0;
        for (int i = first + 1; first >= 0 && i < items.Count; i++)
            if (items[i].Status == "in_progress") { items[i].Status = "pending"; extra++; }
        if (extra > 0) notes.Add($"{extra + 1} items were in_progress; kept step {first + 1}, the others are pending");
        if (first < 0 && items.FindIndex(t => t.Status == "pending") is int p and >= 0)
        {
            items[p].Status = "in_progress";
            notes.Add($"no item was in_progress, so step {p + 1} started");
        }
        return string.Join("; ", notes);
    }

    /// <summary>wrap_up: the turn is over, so every item still open is checked off (Claude Code's wrap_up does the same,
    /// "no closing update_plan is needed"). Returns how many items it finished.</summary>
    public static int FinishAll(List<TodoItem> items)
    {
        int n = 0;
        foreach (var t in items.Where(t => t.Status != "completed")) { t.Status = "completed"; n++; }
        return n;
    }

    /// <summary>wrap_up in <paramref name="run"/>: its plan is checked off only when it is the run's own (not a sub-agent's
    /// parent's) and this run wrote or moved it — a plan carried over from the last turn that this turn never touched
    /// belongs to another request and stays as it was. Returns how many items it finished.</summary>
    public static int FinishForWrapUp(AgentRunScope? run)
        => run == null || run.Parent != null || !run.PlanTouched ? 0 : FinishAll(run.Todos);

    /// <summary>
    /// The step-3 plan reminder (CodeAgentService), or null when the run needs none: no plan at all → write one; a plan
    /// carried over from the last turn that this run has not touched → carry it on or replace it. A plan this run wrote or
    /// moved needs nothing. <paramref name="todoCount"/> is the plan the tool sees (the app's list outside a run).
    /// </summary>
    public static string? Reminder(AgentRunScope? run, int todoCount, bool thai)
    {
        if (todoCount == 0)
            return thai
                ? "[เตือน: งานนี้ยังไม่มีแผน — เรียก todo_write ครั้งเดียวในขั้นถัดไป โดยส่ง todos เป็นแผนทั้งชุด 3-7 ข้อ ข้อที่กำลังทำเป็น in_progress พร้อม scope แล้วส่ง advance: true เมื่อแต่ละข้อเสร็จ ผู้ใช้ดูความคืบหน้าจากแผนนี้]"
                : "[Reminder: this task has no plan yet — in your next step call todo_write ONCE with `todos` = the whole plan (3-7 items, the current one in_progress), and scope; then advance: true as each item finishes. The user follows your progress through this plan.]";
        if (run is { Parent: null, PlanCarried: true, PlanTouched: false })
            return thai
                ? "[เตือน: แผนจากเทิร์นก่อนยังค้างอยู่ และเทิร์นนี้ยังไม่ได้แตะ — ถ้างานนี้ต่อจากแผนนั้น เรียก todo_write advance: true (scope continuation) ถ้าเป็นงานใหม่ ส่ง todos ทั้งชุดใหม่พร้อม scope new_task ผู้ใช้ดูความคืบหน้าจากแผนนี้]"
                : "[Reminder: the plan from the last turn is still open and this turn has not touched it — if this work continues it, call todo_write advance: true (scope continuation); if it is a new task, send a new whole list with scope new_task. The user follows your progress through this plan.]";
        return null;
    }

    /// <summary>What the request is to the chat, as the plan's <c>scope</c> says it.</summary>
    public const string FirstTask = "first_task", Continuation = "continuation", NewTask = "new_task";

    /// <summary>"first_task" / "first" / "continuation" / "continue" / "new_task" / "new" → the tag; anything else → null.</summary>
    public static string? Scope(string? raw) => (raw ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_') switch
    {
        "first_task" or "first" => FirstTask,
        "continuation" or "continue" or "continued" => Continuation,
        "new_task" or "new" => NewTask,
        _ => null,
    };

    /// <summary>"[1,3]", "1, 3", "2" → numbers; anything unreadable is left out.</summary>
    public static List<int> Numbers(string raw)
    {
        var list = new List<int>();
        foreach (var part in (raw ?? "").Trim().Trim('[', ']').Split(',', ' ', ';'))
            if (int.TryParse(part.Trim().Trim('"'), out int n)) list.Add(n);
        return list;
    }

    public static bool IsTrue(string raw) => raw?.Trim().ToLowerInvariant() is "true" or "1" or "yes";

    /// <summary>The tool's answer after a change: "Plan: 2/5 done, now: Run the tests".</summary>
    public static string Describe(IReadOnlyList<TodoItem> items)
    {
        int doneCount = items.Count(t => t.Status == "completed");
        return $"Plan updated: {items.Count} item(s), {doneCount} done"
               + (items.FirstOrDefault(t => t.Status == "in_progress") is { } cur ? $", now: {cur.Content}" : "");
    }
}
