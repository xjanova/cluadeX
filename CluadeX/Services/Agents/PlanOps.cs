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
