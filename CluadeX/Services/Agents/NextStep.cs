using System.Text.Json;

namespace CluadeX.Services.Agents;

/// <summary>
/// The next-step box at the end of a turn (wrap_up): a recap of two or three sentences, at most one question, one or two
/// answer buttons. A button's label goes back as the owner's own message. Kept in the chat row's ToolOutput as JSON.
/// </summary>
public sealed record NextStep(string Recap, string? Ask, IReadOnlyList<string> Actions)
{
    public const int MaxRecap = 300, MaxAsk = 100, MaxLabel = 24, MaxActions = 2;

    /// <summary>From the tool's arguments. Null + <paramref name="error"/> when it cannot be shown as written.</summary>
    public static NextStep? FromArgs(string recap, string ask, string actionsRaw, out string? error)
    {
        error = null;
        recap = (recap ?? "").Trim();
        ask = (ask ?? "").Trim();
        if (recap.Length == 0) { error = "recap is required: two or three sentences on what is now true and what is not done."; return null; }
        if (recap.Length > MaxRecap) recap = recap[..(MaxRecap - 1)].TrimEnd() + "…";
        if (ask.Length > MaxAsk) { error = $"ask is {ask.Length} characters; keep the one question under {MaxAsk}."; return null; }
        var actions = Labels(actionsRaw);
        if (actions.Count > MaxActions) { error = $"Give at most {MaxActions} actions (your pick first)."; return null; }
        if (actions.Count > 0 && ask.Length == 0) { error = "Buttons answer a question: give ask, or leave actions out."; return null; }
        return new NextStep(recap, ask.Length > 0 ? ask : null, actions);
    }

    /// <summary>["Yes, fix it","Not now"], [{"label":"Yes"}], "Yes, Not now" → labels (each cut to <see cref="MaxLabel"/>).</summary>
    public static List<string> Labels(string raw)
    {
        var list = new List<string>();
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return list;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    string? l = e.ValueKind == JsonValueKind.String ? e.GetString()
                        : e.ValueKind == JsonValueKind.Object && e.TryGetProperty("label", out var lb) ? lb.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(l)) list.Add(Cut(l.Trim()));
                }
                return list;
            }
        }
        catch (JsonException) { }
        foreach (var part in raw.Split(new[] { '\n', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            list.Add(Cut(part));
        return list;

        static string Cut(string s) => s.Length <= MaxLabel ? s : s[..MaxLabel].TrimEnd();
    }

    public string ToJson() => JsonSerializer.Serialize(new { recap = Recap, ask = Ask, actions = Actions });

    public static NextStep? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string recap = r.TryGetProperty("recap", out var rc) ? rc.GetString() ?? "" : "";
            string? ask = r.TryGetProperty("ask", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            var actions = r.TryGetProperty("actions", out var ac) && ac.ValueKind == JsonValueKind.Array
                ? ac.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
            return recap.Length == 0 ? null : new NextStep(recap, ask, actions);
        }
        catch (JsonException) { return null; }
    }
}
