namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the learning loop's card in the chat ("remember this next time?") and the
/// Instincts page's project label. Every key has "en" and "th".</summary>
internal static class InstinctKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["instinct.card.title"] = new() { ["en"] = "Remember this for next time?", ["th"] = "จำสิ่งนี้ไว้ใช้ครั้งหน้าไหม" },
        ["instinct.card.from"] = new() { ["en"] = "Learned from the chat “{0}”", ["th"] = "เรียนรู้จากแชท “{0}”" },
        ["instinct.card.yes"] = new() { ["en"] = "✓ Remember", ["th"] = "✓ จำไว้" },
        ["instinct.card.no"] = new() { ["en"] = "✗ No", ["th"] = "✗ ไม่ต้อง" },
        ["instinct.card.yesTip"] = new() { ["en"] = "Yes, this is how I work — the AI gets it in every chat it applies to from now on.", ["th"] = "ใช่ ทำแบบนี้ — AI จะได้รับข้อนี้ในทุกแชทที่เกี่ยวข้องตั้งแต่ตอนนี้" },
        ["instinct.card.noTip"] = new() { ["en"] = "No, that was a one-off or wrong — it never goes into the AI's instructions.", ["th"] = "ไม่ใช่ เป็นแค่ครั้งเดียวหรือเข้าใจผิด — จะไม่ใส่ลงในคำสั่งของ AI อีก" },
        ["instinct.card.accepted"] = new() { ["en"] = "remembered", ["th"] = "จำไว้แล้ว" },
        ["instinct.card.rejected"] = new() { ["en"] = "won't be used", ["th"] = "จะไม่ใช้" },
        ["instinct.card.gone"] = new() { ["en"] = "deleted", ["th"] = "ถูกลบแล้ว" },
        ["instinct.card.scopeGlobal"] = new() { ["en"] = "every project", ["th"] = "ทุกโปรเจกต์" },
        ["instinct.card.scopeProject"] = new() { ["en"] = "only in {0}", ["th"] = "เฉพาะโปรเจกต์ {0}" },
    };
}
