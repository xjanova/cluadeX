namespace CluadeX.Services.Agents;

/// <summary>
/// Claude Fable 5.2 is never chosen by the model on its own (owner 2026-11-10: "ห้ามสลับไป Fable เองอัตโนมัติ"). A run may
/// put work on Fable — agent_spawn / new_chat with model "fable" — only when the owner asked for it in words this turn
/// ("ส่วนนี้ให้ fable คิด", "use fable for this", "โยนไปแชทใหม่ใช้ fable") or said yes on CluadeX's Fable card. The chat
/// page records the owner's own message on Send, as <see cref="WorkflowGate"/> does (a pipe caller's text is not the
/// owner's words and is never recorded). Even then the spend guards decide (<see cref="DispatchPolicy.FableBudgetRefusal"/>).
/// </summary>
public static class FableGate
{
    private static readonly Dictionary<string, string> OwnerWords = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Granted = new(StringComparer.Ordinal);
    private static readonly object Lock = new();

    /// <summary>The owner sent <paramref name="text"/> in <paramref name="chat"/> (this turn's words; the next turn's replace
    /// them, and a yes on the card is used up). <paramref name="append"/>: typed into the running turn.</summary>
    public static void NoteOwnerMessage(string chat, string? text, bool append = false)
    {
        if (string.IsNullOrEmpty(chat)) return;
        lock (Lock)
        {
            OwnerWords[chat] = append && OwnerWords.TryGetValue(chat, out var before) ? before + "\n" + text : text ?? "";
            if (!append) Granted.Remove(chat);
        }
    }

    /// <summary>The owner said yes on the Fable card in <paramref name="chat"/> (for that chat's next run).</summary>
    public static void Grant(string chat)
    {
        if (string.IsNullOrEmpty(chat)) return;
        lock (Lock) Granted.Add(chat);
    }

    /// <summary>What the owner's words this turn asked Fable to do.</summary>
    public static DispatchPolicy.FableRoute Asked(string? chat)
    {
        if (string.IsNullOrEmpty(chat)) return DispatchPolicy.FableRoute.None;
        string? words;
        lock (Lock) OwnerWords.TryGetValue(chat, out words);
        return DispatchPolicy.ParseFableAsk(words);
    }

    /// <summary>The owner's words in <paramref name="chat"/> this turn ("" = none recorded).</summary>
    public static string OwnerWordsOf(string? chat)
    {
        if (string.IsNullOrEmpty(chat)) return "";
        lock (Lock) return OwnerWords.TryGetValue(chat, out var w) ? w : "";
    }

    /// <summary>May a run in <paramref name="chat"/> put work on Fable: the owner asked, or said yes on the card.</summary>
    public static bool Allowed(string? chat)
    {
        if (string.IsNullOrEmpty(chat)) return false;
        lock (Lock) if (Granted.Contains(chat)) return true;
        return Asked(chat) != DispatchPolicy.FableRoute.None;
    }

    /// <summary>The line a run gets back when it reached for Fable without the owner's say.</summary>
    public const string NotAskedText =
        "Claude Fable 5.2 runs only when the user asks for it in their own words (e.g. \"use fable for this\", \"ส่วนนี้ให้ fable คิด\") "
        + "or says yes on CluadeX's Fable card — it costs several times this model. Do the work here, or ask the user whether "
        + "they want it sent to Fable (say what it would cost).";

    /// <summary>Forget everything (the harness).</summary>
    public static void Reset()
    {
        lock (Lock) { OwnerWords.Clear(); Granted.Clear(); }
    }
}
