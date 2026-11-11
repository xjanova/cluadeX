using System.Text.Json;
using System.Text.Json.Nodes;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// A turn CluadeX did not see end — the owner closed the app while a chat worked or waited on a card, or it crashed —
/// and what the chat shows about it after the restart (owner 2026-11-11: "ถ้าเราออกจากโปรแกรมในขณะที่ แชทยังทำงาน หรือยังค้าง
/// ในบล๊อกคำถาม เราต้องมีวิธีจัดการเมื่อกลับมาใหม่อย่างไรให้ดี"). Pure, so the harness checks it; the chat page does the rest
/// (<c>ChatViewModel.Recovery.cs</c>).
/// <para>The card is a next-step box (<see cref="ChatMessage.NextStepTag"/>, its data in ToolOutput — no schema change)
/// marked <c>"resume"</c>, like the Fable card is marked <c>"fable"</c>: its buttons act in CluadeX, they are not a message
/// to the model. Every CLI provider replays the transcript on each turn (none keeps a CLI session to resume — see
/// ClaudeDevProvider's note on <c>--continue</c>), so "continue" is a new turn whose history is the whole chat.</para>
/// </summary>
public static class RunRecovery
{
    /// <summary>The card's marker in its JSON.</summary>
    public const string Marker = "resume";

    public const string WhyClosed = "closed", WhyCrash = "crash";

    /// <summary>What the card knows: why the turn was cut off, what it waited on (a <see cref="LiveRunLedger"/> wait kind
    /// or null), and the questions left open on its cards (their text, for the message that carries on).</summary>
    public sealed record Info(string Why, string? Waiting, IReadOnlyList<string> Questions, DateTime At);

    /// <summary>A question card nobody answered: answering or closing one writes "→ …" under it (AnswerQuestion,
    /// CloseQuestion, the steps' review), so a card read back without it was still waiting.</summary>
    public static bool IsOpenQuestion(ChatMessage m) =>
        m.Role == MessageRole.Question && !(m.Content ?? "").Contains("\n→ ", StringComparison.Ordinal);

    /// <summary>Index of the first row of the turn that ends at <paramref name="end"/> (exclusive): the row after the last
    /// user message before it, or 0.</summary>
    public static int TurnStart(IReadOnlyList<ChatMessage> rows, int end)
    {
        for (int i = Math.Min(end, rows.Count) - 1; i >= 0; i--)
            if (rows[i].Role == MessageRole.User) return i + 1;
        return 0;
    }

    /// <summary>The open question cards of the last turn of <paramref name="rows"/> (up to <paramref name="end"/>).</summary>
    public static List<ChatMessage> OpenQuestions(IReadOnlyList<ChatMessage> rows, int end)
    {
        end = Math.Min(end, rows.Count);
        var open = new List<ChatMessage>();
        for (int i = TurnStart(rows, end); i < end; i++)
            if (IsOpenQuestion(rows[i])) open.Add(rows[i]);
        return open;
    }

    /// <summary>The card's row. <paramref name="loc"/> gives the words in the UI language now (the card keeps them).</summary>
    public static ChatMessage Card(Info info, Func<string, string> loc)
    {
        bool asked = info.Questions.Count > 0 || info.Waiting == LiveRunLedger.WaitQuestion;
        bool perm = !asked && info.Waiting == LiveRunLedger.WaitPermission;
        string recap = loc(info.Why == WhyClosed ? "chat.recover.closed" : "chat.recover.crash")
                       + " " + loc(asked ? "chat.recover.waitQuestion" : perm ? "chat.recover.waitPermission" : "chat.recover.working");
        string ask = loc(asked ? "chat.recover.askQuestion" : "chat.recover.ask");
        string go = loc(asked ? "chat.recover.reask" : "chat.recover.continue");
        string dismiss = loc("chat.recover.dismiss");
        var json = new JsonObject
        {
            ["recap"] = recap,
            ["ask"] = ask,
            ["actions"] = new JsonArray(go, dismiss),
            [Marker] = new JsonObject
            {
                ["why"] = info.Why,
                ["waiting"] = info.Waiting,
                ["q"] = new JsonArray(info.Questions.Select(q => (JsonNode?)JsonValue.Create(q)).ToArray()),
                ["at"] = info.At.ToUniversalTime().ToString("o"),
            },
        };
        return new ChatMessage
        {
            Role = MessageRole.System,
            ToolName = ChatMessage.NextStepTag,
            ToolOutput = json.ToJsonString(),
            Content = "⏸ " + recap,
            IsInterrupted = true,
        };
    }

    /// <summary>A recovery card's data (null for any other row) and its first button's label (continue / ask again).</summary>
    public static (Info Info, string Go)? Read(ChatMessage row)
    {
        if (row.ToolName != ChatMessage.NextStepTag || row.ToolOutput is not { Length: > 0 } json
            || !json.Contains("\"" + Marker + "\"", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty(Marker, out var m) || m.ValueKind != JsonValueKind.Object) return null;
            string why = m.TryGetProperty("why", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString()! : WhyCrash;
            string? waiting = m.TryGetProperty("waiting", out var wt) && wt.ValueKind == JsonValueKind.String ? wt.GetString() : null;
            var qs = m.TryGetProperty("q", out var q) && q.ValueKind == JsonValueKind.Array
                ? q.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : new List<string>();
            DateTime at = m.TryGetProperty("at", out var a) && a.ValueKind == JsonValueKind.String && DateTime.TryParse(a.GetString(),
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue;
            string go = r.TryGetProperty("actions", out var ac) && ac.ValueKind == JsonValueKind.Array && ac.GetArrayLength() > 0
                ? ac[0].GetString() ?? "" : "";
            return (new Info(why, waiting, qs, at), go);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The card still waits for the owner: nothing was picked and no message was sent after it.</summary>
    public static bool IsOpenCard(IReadOnlyList<ChatMessage> rows, int at) =>
        at >= 0 && at < rows.Count && rows[at].ToolSummary is not { Length: > 0 } && Read(rows[at]) != null
        && !rows.Skip(at + 1).Any(m => m.Role == MessageRole.User);

    // ─── the question's own data, kept on its row so a reloaded card can be answered again ───

    /// <summary>A question card's options (one question) or steps (several), as stored in its ToolOutput.</summary>
    public static string QuestionJson(IReadOnlyList<string>? options, IReadOnlyList<AskStep>? steps)
    {
        var o = new JsonObject();
        if (steps is { Count: > 0 })
            o["steps"] = new JsonArray(steps.Select(s => (JsonNode)new JsonObject
            {
                ["q"] = s.Question,
                ["options"] = new JsonArray(s.Options.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["notes"] = new JsonArray(s.Notes.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["multi"] = s.Multi,
            }).ToArray());
        else o["options"] = new JsonArray((options ?? Array.Empty<string>()).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        return o.ToJsonString();
    }

    /// <summary>What <see cref="QuestionJson"/> wrote; for a row from an older build (no data), its text as one free
    /// question (the steps' text is "❓ q" per step, a line apart).</summary>
    public static (List<string> Options, List<AskStep> Steps) ReadQuestion(ChatMessage row)
    {
        var options = new List<string>();
        var steps = new List<AskStep>();
        if (row.ToolOutput is { Length: > 0 } json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                static List<string> Strings(JsonElement e, string name) =>
                    e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
                        ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : new();
                if (r.TryGetProperty("steps", out var st) && st.ValueKind == JsonValueKind.Array)
                    foreach (var s in st.EnumerateArray())
                        steps.Add(new AskStep(s.TryGetProperty("q", out var q) ? q.GetString() ?? "" : "", Strings(s, "options"), Strings(s, "notes"),
                            s.TryGetProperty("multi", out var mu) && mu.ValueKind == JsonValueKind.True));
                options = Strings(r, "options");
                return (options, steps);
            }
            catch (JsonException) { }
        }
        return (options, steps);
    }

    /// <summary>The question(s) a card shows, without the "❓ " marks (for the card and for the message that carries on).</summary>
    public static string QuestionText(ChatMessage row)
    {
        var (_, steps) = ReadQuestion(row);
        if (steps.Count > 0) return string.Join("\n", steps.Select((s, i) => steps.Count > 1 ? $"{i + 1}. {s.Question.Trim()}" : s.Question.Trim()));
        return string.Join("\n", (row.Content ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
            .Select(l => l.StartsWith("❓", StringComparison.Ordinal) ? l[1..].Trim() : l));
    }

    /// <summary>The owner's message when the card's first button is clicked: carry on (or ask again) after the close.</summary>
    public static string ContinueMessage(Info info, Func<string, string> loc)
    {
        if (info.Questions.Count > 0)
            return loc("chat.recover.msgReask") + "\n" + string.Join("\n", info.Questions.Select(q => "• " + q.Replace("\n", " ")));
        return loc(info.Waiting == LiveRunLedger.WaitPermission ? "chat.recover.msgPermission" : "chat.recover.msgContinue");
    }

    /// <summary>The owner's message when a reloaded question card is answered: the question with the answer, then carry on —
    /// the model never saw its ask_user return (the CLI that asked is gone).</summary>
    public static string AnswerMessage(string question, string answer, Func<string, string> loc) =>
        loc("chat.recover.msgAnswer") + "\n" + (question.Trim().Length > 0 ? question.Trim() + "\n→ " : "") + answer.Trim();
}
