using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Agents;

/// <summary>
/// The plan card as it is kept with the chat (DatabaseService stores it in the row's tool columns, like the agent map),
/// and the plan a chat's next turn carries on from. A card read back never runs: the step that was in progress shows as
/// stopped (<see cref="PlanItemView.IsStopped"/>), since nothing works on it any more. Pure, for the harness.
/// </summary>
/// <param name="Stopped">Its run was stopped by the owner (or was still going when the chat was saved last) with a step
/// in progress — the header reads "หยุดไว้" rather than "งานจบแล้ว".</param>
public sealed record PlanRecord(IReadOnlyList<TodoItem> Items, string? Scope, bool Stopped = false)
{
    /// <summary>From a card's rows (their state words back to todo statuses). A row still working counts as stopped: by the
    /// time the record is read back, nothing runs it.</summary>
    public static PlanRecord FromCard(IReadOnlyList<PlanItemView> rows, string? scope, bool stopped = false)
        => new(rows.Select(r => new TodoItem { Content = r.Text, Status = StatusOf(r.State) }).ToList(), scope,
               stopped || rows.Any(r => r.IsWorking));

    public static string StatusOf(string state) => state switch
    {
        "done" => "completed",
        "active" => "in_progress",
        _ => "pending",
    };

    public static string StateOf(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "completed" or "done" => "done",
        "in_progress" or "active" => "active",
        _ => "pending",
    };

    /// <summary>The card's rows. <paramref name="live"/> false = a card read back: its step in progress is stopped.</summary>
    public List<PlanItemView> ToRows(bool live)
        => Items.Select((t, i) => new PlanItemView(i + 1, t.Content, StateOf(t.Status)) { IsLive = live }).ToList();

    public bool HasWorkLeft => Items.Any(t => t.Status != "completed");

    public string ToJson() => JsonSerializer.Serialize(new
    {
        scope = Scope,
        stopped = Stopped,
        items = Items.Select(t => new { content = t.Content, status = t.Status }),
    });

    /// <summary>Null when <paramref name="json"/> is not a plan (the row keeps its text then).</summary>
    public static PlanRecord? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            var items = new List<TodoItem>();
            foreach (var e in arr.EnumerateArray())
            {
                string text = e.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                if (text.Trim().Length == 0) continue;
                string st = e.TryGetProperty("status", out var s) ? s.GetString() ?? "pending" : "pending";
                items.Add(new TodoItem { Content = text, Status = StatusOf(StateOf(st)) });
            }
            string? scope = r.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.String ? PlanOps.Scope(sc.GetString()) : null;
            bool stopped = r.TryGetProperty("stopped", out var sp) && sp.ValueKind == JsonValueKind.True;
            return items.Count == 0 ? null : new PlanRecord(items, scope, stopped);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The header of a card that is not running: "เสร็จแล้ว (6/6)"; "หยุดไว้ (2/6)" when its run was stopped
    /// with a step in progress; else "งานจบแล้ว (ติ๊กแผนไว้ 2/6)".</summary>
    public static string EndedHeader(IReadOnlyList<PlanItemView> rows, bool stopped, Func<string, string> loc)
    {
        int done = rows.Count(r => r.IsDone), total = rows.Count;
        string key = done == total && total > 0 ? "chat.plan.done"
            : stopped && rows.Any(r => r.IsActive) ? "plan.card.stopped"
            : "chat.plan.ended";
        return string.Format(loc(key), done, total);
    }

    /// <summary>
    /// What the model of the chat's next turn is told about the plan the last turn left open (the plan itself carries on
    /// in the new run, so advance / done / start work on it). Rides on the turn's message, never the system prompt, so the
    /// cached prompt stays the same.
    /// </summary>
    public static string CarryNote(IReadOnlyList<TodoItem> items)
    {
        var sb = new StringBuilder("[CluadeX: your plan from the last turn is still open — ");
        for (int i = 0; i < items.Count; i++)
        {
            string mark = items[i].Status switch { "completed" => "done", "in_progress" => "stopped mid-way", _ => "pending" };
            if (i > 0) sb.Append(" · ");
            sb.Append(i + 1).Append(". ").Append(items[i].Content).Append(" (").Append(mark).Append(')');
        }
        sb.Append(". If this message continues that work, carry on with todo_write advance / done + start (scope continuation) ")
          .Append("and do not redo finished steps; if it is a new request, send a new whole list with scope new_task.]");
        return sb.ToString();
    }
}
