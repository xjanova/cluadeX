namespace CluadeX.Services.Localization;

/// <summary>Localized strings for watches (watch_ci / watch_process): the chat's card, the notice when one ends, the
/// Settings switch. Every key has "en" and "th".</summary>
internal static class WatchKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["watch.card.ci"] = new() { ["en"] = "Watching CI: {0}", ["th"] = "เฝ้า CI: {0}" },
        ["watch.card.process"] = new() { ["en"] = "Watching {0}", ["th"] = "เฝ้างานเบื้องหลัง {0}" },
        ["watch.card.until"] = new() { ["en"] = "until {0}", ["th"] = "ถึง {0}" },
        ["watch.card.lastLook"] = new() { ["en"] = "last look {0}", ["th"] = "ดูล่าสุด {0}" },
        ["watch.card.note"] = new() { ["en"] = "Then: ", ["th"] = "จากนั้น: " },
        ["watch.cancel"] = new() { ["en"] = "Cancel", ["th"] = "ยกเลิก" },
        ["watch.cancelTip"] = new() { ["en"] = "Stop watching — nothing is told to the chat and no turn starts.", ["th"] = "หยุดเฝ้า — จะไม่แจ้งแชทและไม่เริ่มเทิร์นใหม่" },
        ["watch.state.watching"] = new() { ["en"] = "watching", ["th"] = "กำลังเฝ้า" },
        ["watch.state.passed"] = new() { ["en"] = "passed", ["th"] = "ผ่าน" },
        ["watch.state.failed"] = new() { ["en"] = "failed", ["th"] = "ไม่ผ่าน" },
        ["watch.state.ended"] = new() { ["en"] = "ended", ["th"] = "จบแล้ว" },
        ["watch.state.timedout"] = new() { ["en"] = "timed out", ["th"] = "หมดเวลา" },
        ["watch.state.error"] = new() { ["en"] = "could not be watched", ["th"] = "เฝ้าต่อไม่ได้" },
        ["watch.state.cancelled"] = new() { ["en"] = "cancelled", ["th"] = "ยกเลิกแล้ว" },
        ["watch.ended"] = new() { ["en"] = "{0} — {1}", ["th"] = "{0} — {1}" },
        ["watch.noAuto"] = new() { ["en"] = "The AI does not carry on by itself (Settings ▸ Agent ▸ Carry on when a watch ends is off) — send a message when you want it to.", ["th"] = "AI จะไม่ทำต่อเอง (ตั้งค่า ▸ เอเจนต์ ▸ ทำต่อเองเมื่อการเฝ้างานจบ ปิดอยู่) — ส่งข้อความเมื่ออยากให้ทำต่อ" },
        ["watch.wakeLimit"] = new() { ["en"] = "Watches already started {0} turns in this chat within {1} — it waits for you now: send a message to carry on.", ["th"] = "การเฝ้างานเริ่มเทิร์นในแชทนี้ไปแล้ว {0} ครั้งภายใน {1} — ตอนนี้รอคุณก่อน: ส่งข้อความเพื่อให้ทำต่อ" },
        ["watch.notStarted"] = new() { ["en"] = "The AI could not carry on by itself: {0}", ["th"] = "AI ทำต่อเองไม่ได้: {0}" },
        ["watch.cancelledByYou"] = new() { ["en"] = "you cancelled it", ["th"] = "คุณยกเลิก" },
        ["watch.cancelledByHotkey"] = new() { ["en"] = "stopped with Ctrl+Alt+End", ["th"] = "หยุดด้วย Ctrl+Alt+End" },
        ["settings.watchAutoContinue"] = new() { ["en"] = "Carry on when a watch ends", ["th"] = "ทำต่อเองเมื่อการเฝ้างานจบ" },
        ["settings.watchAutoContinueDesc"] = new() { ["en"] = "When the AI waits for CI or a background process (watch_ci / watch_process) and it ends while nobody works in that chat, a new turn starts there with the result — a failed check gets read and fixed. At most 6 such turns per chat in 2 hours; Ctrl+Alt+End cancels every watch. Off = a notice only.", ["th"] = "เมื่อ AI รอ CI หรืองานเบื้องหลัง (watch_ci / watch_process) แล้วงานนั้นจบตอนที่ไม่มีใครทำงานในแชทนั้น จะเริ่มเทิร์นใหม่ในแชทพร้อมผล — ถ้าเช็คไม่ผ่านก็อ่านแล้วแก้ต่อ ไม่เกิน 6 เทิร์นต่อแชทใน 2 ชั่วโมง กด Ctrl+Alt+End เพื่อยกเลิกการเฝ้าทั้งหมด ปิด = แจ้งเตือนอย่างเดียว" },
    };
}
