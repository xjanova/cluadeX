namespace CluadeX.Services.Localization;

/// <summary>Localized strings for Design mode (the composer's switch and its notice) and for the UI Studio while an AI
/// run edits its project (the look-only lock). Every key has "en" and "th".</summary>
internal static class DesignKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ─── the composer's Design switch ───
        ["chat.design"] = new() { ["en"] = "Design", ["th"] = "ดีไซน์" },
        ["chat.designTip"] = new() { ["en"] = "Design: while the AI works on this chat's screens, the UI Studio comes forward to show the real page, and the AI is told to keep it runnable. Off by default — asking for a design in your message (design, redesign, UI, ออกแบบ…) also turns it on for that turn.", ["th"] = "ดีไซน์: ระหว่างที่ AI ทำหน้าจอของแชทนี้ จะพาไปหน้า UI Studio เพื่อดูหน้าจอจริง และบอก AI ให้หน้าเว็บรันได้ตลอด ปิดไว้เป็นค่าเริ่มต้น — ถ้าพิมพ์ขอให้ออกแบบในข้อความ (ออกแบบ, ดีไซน์, หน้าตา, UI…) ก็เปิดให้เฉพาะรอบนั้น" },
        ["chat.design.notice"] = new() { ["en"] = "Design mode: the UI Studio shows the real screen while the AI works (editing there is locked until it finishes)", ["th"] = "โหมดดีไซน์: UI Studio แสดงหน้าจอจริงระหว่างที่ AI ทำงาน (แก้ไขในหน้านั้นไม่ได้จนกว่า AI จะทำเสร็จ)" },

        // ─── the UI Studio while an AI run edits its project ───
        ["studio.err.aiWorking"] = new() { ["en"] = "AI is editing this project — editing is locked until it finishes", ["th"] = "AI กำลังแก้โปรเจกต์นี้อยู่ — แก้ไขไม่ได้จนกว่า AI จะทำเสร็จ" },
        ["studio.lock.banner"] = new() { ["en"] = "AI is editing this project — editing is locked until it finishes. You can still look around and pick elements.", ["th"] = "AI กำลังแก้โปรเจกต์นี้อยู่ — แก้ไขไม่ได้จนกว่า AI จะทำเสร็จ ยังดูหน้าและเลือก element ได้ตามปกติ" },
        ["studio.lock.waiting"] = new() { ["en"] = "AI is waiting for your answer in its chat — editing stays locked until it finishes", ["th"] = "AI กำลังรอคำตอบจากคุณในแชท — ยังแก้ไขไม่ได้จนกว่า AI จะทำเสร็จ" },
        ["studio.lock.openChat"] = new() { ["en"] = "Open chat", ["th"] = "เปิดแชท" },
        ["studio.lock.reverted"] = new() { ["en"] = "AI started editing this project — your unsaved changes were taken back (the AI may move the line they belong to)", ["th"] = "AI เริ่มแก้โปรเจกต์นี้ — ยกเลิกการแก้ที่ยังไม่ได้บันทึกแล้ว (AI อาจย้ายบรรทัดที่การแก้นั้นต้องไปลง)" },
        ["studio.lock.done"] = new() { ["en"] = "AI finished — the page is reloaded and editing is back on", ["th"] = "AI ทำเสร็จแล้ว — โหลดหน้าใหม่และแก้ไขได้อีกครั้ง" },
        ["studio.lock.devWaits"] = new() { ["en"] = "The dev server starts when the AI finishes (its install would clash with the AI's work)", ["th"] = "จะเริ่ม dev server เมื่อ AI ทำเสร็จ (การติดตั้งจะชนกับงานของ AI)" },
        ["studio.lock.kindChanged"] = new() { ["en"] = "The project is now: {0} — starting its preview", ["th"] = "ตอนนี้โปรเจกต์เป็น: {0} — กำลังเริ่มพรีวิว" },
    };
}
