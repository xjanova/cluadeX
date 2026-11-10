namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the sidebar's run badges and the activity bar's น้อง CluadeX. Every key needs both
/// "en" and "th".</summary>
internal static class RunBadgeKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["sidebar.run.idle"] = new() { ["en"] = "Nothing running", ["th"] = "ว่าง — ไม่มีงานค้าง" },
        ["sidebar.run.working"] = new() { ["en"] = "Working…", ["th"] = "กำลังทำงาน…" },
        ["sidebar.run.waiting"] = new() { ["en"] = "Waiting for you", ["th"] = "รอคุณตอบอยู่" },
        ["sidebar.run.done"] = new() { ["en"] = "Finished — a new result you have not opened", ["th"] = "เสร็จแล้ว — มีผลใหม่ที่ยังไม่ได้เปิดดู" },
        ["sidebar.run.failed"] = new() { ["en"] = "The last turn ended on an error", ["th"] = "รอบล่าสุดจบด้วยข้อผิดพลาด" },
        ["sidebar.run.stopped"] = new() { ["en"] = "Stopped before it finished", ["th"] = "หยุดไปก่อนทำเสร็จ" },

        // the split-chat chips under a command (ChatView): short words beside "working" / "done"
        ["orch.splits.failed"] = new() { ["en"] = "failed", ["th"] = "ล้มเหลว" },
        ["orch.splits.stopped"] = new() { ["en"] = "stopped", ["th"] = "หยุดแล้ว" },

        ["buddy.mood.idle"] = new() { ["en"] = "CluadeX is here — nothing running", ["th"] = "น้อง CluadeX อยู่นี่ — ยังไม่มีงาน" },
        ["buddy.mood.working"] = new() { ["en"] = "CluadeX is working…", ["th"] = "น้อง CluadeX กำลังทำงาน…" },
        ["buddy.mood.waiting"] = new() { ["en"] = "CluadeX is waiting for you", ["th"] = "น้อง CluadeX รอคุณตอบอยู่" },
        ["buddy.mood.happy"] = new() { ["en"] = "Done!", ["th"] = "เสร็จแล้ว!" },
        ["buddy.mood.sad"] = new() { ["en"] = "Something went wrong", ["th"] = "มีบางอย่างผิดพลาด" },
        ["buddy.mood.sleep"] = new() { ["en"] = "Nothing to do — CluadeX is napping", ["th"] = "ไม่มีงาน — น้อง CluadeX งีบอยู่ zzz" },
        // clicking her: back to Home (a new chat with no project open); chats that run keep running
        ["buddy.goHome"] = new() { ["en"] = "Click: back to Home — running chats keep going", ["th"] = "คลิก: กลับหน้า Home — แชทที่รันอยู่ยังทำงานต่อ" },
        ["buddy.goHomeName"] = new() { ["en"] = "CluadeX — Home", ["th"] = "น้อง CluadeX — หน้า Home" },
    };
}
