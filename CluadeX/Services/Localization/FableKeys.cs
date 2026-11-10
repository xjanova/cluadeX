namespace CluadeX.Services.Localization;

/// <summary>Localized strings for sending very hard tasks to Claude Fable 5.2: the offer card, the Fable chat's notes and
/// the report it sends back. Every key needs both "en" and "th".</summary>
internal static class FableKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // Which of the verdict's two texts to show (the card is built in code).
        ["fable.lang"] = new() { ["en"] = "en", ["th"] = "th" },

        // ── the offer card (never a switch by itself: the owner says yes or no) ──
        ["fable.offer.recap"] = new()
        {
            ["en"] = "This looks very hard: {0}. Claude Fable 5.2 can take it in a new chat from a focused brief — about ${1:0.00}–${2:0.00} "
                   + "(effort high, task budget). Forking the whole chat instead would cost ~${3:0.00} in cache writes before it starts.",
            ["th"] = "งานนี้ดูยากมาก: {0} · ส่งให้ Claude Fable 5.2 ทำในแชทใหม่ด้วย brief โฟกัสได้ ประมาณ ${1:0.00}–${2:0.00} "
                   + "(effort high มี task budget) ถ้า fork ทั้งแชทไปจะเสียค่าเขียนแคชราว ${3:0.00} ตั้งแต่ยังไม่เริ่ม",
        },
        ["fable.offer.ask"] = new() { ["en"] = "Send it to Fable in a new chat?", ["th"] = "ส่งให้ Fable ในแชทใหม่ไหม?" },
        ["fable.offer.yes"] = new() { ["en"] = "Send to Fable", ["th"] = "ส่งให้ Fable" },
        ["fable.offer.no"] = new() { ["en"] = "Don't send", ["th"] = "ไม่ส่ง" },
        ["fable.offer.declined"] = new() { ["en"] = "Kept on this model — not sent to Fable.", ["th"] = "ไม่ส่งให้ Fable — ทำต่อด้วยรุ่นเดิม" },
        ["fable.offer.failed"] = new() { ["en"] = "Could not send it to Fable: {0}", ["th"] = "ส่งให้ Fable ไม่ได้: {0}" },
        ["fable.offer.overBudget"] = new()
        {
            ["en"] = "Not sent to Fable: it would cost about ${0:0.00}–${1:0.00}, more than is left of the per-run or daily budget (Settings ▸ Agent).",
            ["th"] = "ไม่ได้ส่งให้ Fable: น่าจะใช้ราว ${0:0.00}–${1:0.00} เกินงบต่อรอบหรืองบรายวันที่เหลือ (ตั้งค่า ▸ Agent)",
        },
        ["fable.offer.problem"] = new()
        {
            ["en"] = "{0}\n\nThe model on this chat could not finish it: {1}.",
            ["th"] = "{0}\n\nรุ่นที่ใช้ในแชทนี้ทำไม่สำเร็จ: {1}",
        },

        // ── the Fable chat ──
        ["fable.chat.opened"] = new()
        {
            ["en"] = "🧠 This chat runs on Claude Fable 5.2 — effort high, task budget {0:N0} tokens, from a focused brief of ~{1:N0} tokens "
                   + "(not the whole chat). Estimated ${2:0.00}–${3:0.00}. Its report goes back to \"{4}\" when it ends its turn.",
            ["th"] = "🧠 แชทนี้ใช้ Claude Fable 5.2 — effort high, task budget {0:N0} โทเค็น เริ่มจาก brief โฟกัส ~{1:N0} โทเค็น "
                   + "(ไม่ใช่ทั้งแชท) ประมาณ ${2:0.00}–${3:0.00} เมื่อจบรอบจะส่งรายงานกลับไปที่ \"{4}\"",
        },
        ["fable.chat.providerOff"] = new()
        {
            ["en"] = "This chat is set to Claude Fable 5.2, but the provider now is {0}, which cannot run it — this turn uses the model picked in the model button.",
            ["th"] = "แชทนี้ตั้งให้ใช้ Claude Fable 5.2 แต่ provider ตอนนี้คือ {0} ซึ่งรัน Fable ไม่ได้ — รอบนี้ใช้รุ่นที่เลือกในปุ่มรุ่นแทน",
        },
        ["fable.report"] = new() { ["en"] = "🧠 Fable's report from \"{0}\": {1}", ["th"] = "🧠 รายงานจาก Fable (\"{0}\"): {1}" },
        ["fable.model.tip"] = new()
        {
            ["en"] = "This chat runs on its own model (set when it was opened). Picking a model here switches it back to your pick.",
            ["th"] = "แชทนี้ใช้รุ่นของตัวเอง (ตั้งตอนเปิดแชท) ถ้าเลือกรุ่นที่ปุ่มนี้ จะกลับไปใช้รุ่นที่คุณเลือก",
        },
    };
}
