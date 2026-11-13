namespace CluadeX.Services.Agents;

/// <summary>
/// When CluadeX offers to run something in a new chat instead of the chat on screen (owner 2026-11-13: "ถ้าคำสั่งที่เข้ามา
/// กลางแชทมันคนละ effort หรือควรเปลี่ยนโมเดล ก็แยกแชทไปดีกว่าไหม ใช้ได้กับทุก provider เลยด้วย").
/// <para>What a long chat costs is not the effort level — on the API loops a level change is free, and a CLI turn takes
/// its own level anyway — but the conversation itself: every step of a new, unrelated task re-reads it, and another model
/// reads all of it from nothing. A new chat starts from the task alone, and works the same on every provider (each chat
/// keeps its own model).</para>
/// <para>Three places, all the owner's call — nothing is split off unasked:</para>
/// <list type="bullet">
/// <item>a message typed while the chat's reply runs: its queue row has "do it in a new chat" beside "Send now", always
/// (no guess is needed there);</item>
/// <item>a message sent in a chat that is already long (<see cref="BigChatTokens"/>) and that reads as a task of its own
/// (<see cref="IsOwnTask"/>): a card asks where to run it;</item>
/// <item>another model picked in a chat that is already long: a card asks whether to switch here or start a new chat with
/// that model.</item>
/// </list>
/// The two cards can be turned off (AppSettings.SuggestNewChat); the button stays.
/// </summary>
public static class NewChatAdvice
{
    /// <summary>A chat that carries this much is "long": below it a new task costs little to do in place. Settable for
    /// the harness only.</summary>
    public static int BigChatTokens { get; set; } = 80_000;

    /// <summary>A task of its own needs a few words: "แก้ต่อ" or "ok thanks" is about the work in the chat.</summary>
    public const int MinTaskChars = 40;

    /// <summary>
    /// Does <paramref name="message"/> read as a task that stands by itself — real work (not a question, a go-ahead, a
    /// "still broken"), with nothing in it that points back at the conversation? Conservative on purpose: a card shown
    /// for a follow-up costs the owner a click every time, a card not shown costs nothing that was not already paid.
    /// </summary>
    public static bool IsOwnTask(string? message, IReadOnlyList<string>? earlier = null)
    {
        string own = AutoEffortPolicy.OwnWords(message);
        if (own.Length < MinTaskChars || own.StartsWith('/')) return false;
        var pick = AutoEffortPolicy.Pick(new AutoEffortPolicy.Input(own, 0, 0, earlier));
        // chat / question: nothing to run · retry: the fix made here did not hold · carryOn: "ต่อ" · image / bigContext:
        // lifted for what the chat holds, not for the task
        if (pick.Level < 1 || pick.ReasonKey is "chat" or "question" or "retry" or "carryOn" or "image" or "bigContext") return false;
        string lower = own.ToLowerInvariant();
        foreach (string back in PointsBack)
            if (lower.Contains(back, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>A message about to be sent in a chat that carries <paramref name="contextTokens"/>: offer a new chat?</summary>
    public static bool ForBigChat(string? message, int contextTokens, IReadOnlyList<string>? earlier = null) =>
        contextTokens >= BigChatTokens && IsOwnTask(message, earlier);

    /// <summary>Another model picked in a chat that carries <paramref name="contextTokens"/> and has spoken already: the
    /// new model would read all of it from nothing.</summary>
    public static bool ForModelSwitch(int contextTokens, bool hasConversation) => hasConversation && contextTokens >= BigChatTokens;

    /// <summary>Words that tie a message to what was said or done in the chat. A message with one stays where it is.</summary>
    private static readonly string[] PointsBack =
    [
        "เมื่อกี้", "เมื่อกี๊", "เมื่อครู่", "ที่ทำไป", "ที่แก้ไป", "ที่เพิ่งทำ", "ที่เพิ่งแก้", "ที่เพิ่ง", "ข้างบน", "ด้านบน", "ก่อนหน้า",
        "อันนี้", "อันนั้น", "ตัวนี้", "ตัวนั้น", "ต่อจาก", "งานนี้", "งานเดิม", "แชทนี้", "ที่คุยกัน", "ที่คุยไว้", "ตามที่บอก", "ตามที่คุย",
        "ที่บอกไป", "ที่ว่ามา", "ที่เสนอ", "ที่คุณ", "ที่ทำมา", "เหมือนเดิม", "ทำต่อ", "แก้ต่อ",
        "above", "previous", "earlier", "you just", "just now", "that change", "this chat", "as discussed", "as we said", "same file",
        "the fix", "your last", "your change", "you made", "you wrote", "you suggested", "continue", "carry on", "keep going",
    ];
}
