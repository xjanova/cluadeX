namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the chat's activity card (ChatViewModel.Activity, Views/ActivityCardView). Every key
/// needs both "en" and "th".</summary>
internal static class ActivityKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["activity.title"] = new() { ["en"] = "AI at work", ["th"] = "AI กำลังทำอะไร" },
        ["activity.close"] = new() { ["en"] = "Close the activity card (turn it on again from the + menu)", ["th"] = "ปิดการ์ดนี้ (เปิดใหม่ได้จากเมนู +)" },
        ["activity.toggle"] = new() { ["en"] = "Activity card (what the AI is doing)", ["th"] = "การ์ดจำลองการทำงานของ AI" },
        ["activity.name"] = new() { ["en"] = "What the AI is doing now", ["th"] = "สิ่งที่ AI กำลังทำตอนนี้" },

        // the headline: how the turn stands now
        ["activity.head.idle"] = new() { ["en"] = "Ready for your next task", ["th"] = "พร้อมรับงานถัดไป" },
        ["activity.head.reading"] = new() { ["en"] = "Reading your request…", ["th"] = "กำลังอ่านคำสั่งของคุณ…" },
        ["activity.head.thinking"] = new() { ["en"] = "Thinking about the next step…", ["th"] = "กำลังคิดขั้นต่อไป…" },
        ["activity.head.writing"] = new() { ["en"] = "Writing the answer…", ["th"] = "กำลังเรียบเรียงคำตอบ…" },
        ["activity.head.explore"] = new() { ["en"] = "Looking through the project…", ["th"] = "กำลังสำรวจโปรเจกต์…" },
        ["activity.head.edit"] = new() { ["en"] = "Changing files…", ["th"] = "กำลังแก้ไขไฟล์…" },
        ["activity.head.run"] = new() { ["en"] = "Running a command…", ["th"] = "กำลังรันคำสั่ง…" },
        ["activity.head.team"] = new() { ["en"] = "Sub-agents are on it…", ["th"] = "ทีมเอเจนต์ย่อยกำลังช่วยกันทำ…" },
        ["activity.head.waiting"] = new() { ["en"] = "Waiting for your answer", ["th"] = "รอคุณตอบอยู่" },
        ["activity.head.done"] = new() { ["en"] = "Done", ["th"] = "เสร็จเรียบร้อย" },
        ["activity.head.failed"] = new() { ["en"] = "Ended on an error", ["th"] = "จบด้วยข้อผิดพลาด" },
        ["activity.head.stopped"] = new() { ["en"] = "Stopped", ["th"] = "หยุดแล้ว" },

        // the five stations of the scene (short: they sit under small circles)
        ["activity.station.think"] = new() { ["en"] = "Think", ["th"] = "คิด" },
        ["activity.station.explore"] = new() { ["en"] = "Explore", ["th"] = "สำรวจ" },
        ["activity.station.edit"] = new() { ["en"] = "Edit", ["th"] = "แก้ไข" },
        ["activity.station.run"] = new() { ["en"] = "Run", ["th"] = "รัน" },
        ["activity.station.team"] = new() { ["en"] = "Team", ["th"] = "ทีม" },

        // the counters
        ["activity.count.reads"] = new() { ["en"] = "looked", ["th"] = "สำรวจ" },
        ["activity.count.edits"] = new() { ["en"] = "edits", ["th"] = "แก้ไข" },
        ["activity.count.commands"] = new() { ["en"] = "runs", ["th"] = "คำสั่ง" },
        ["activity.count.agents"] = new() { ["en"] = "agents", ["th"] = "เอเจนต์" },
        ["activity.count.readsTip"] = new() { ["en"] = "Files read, searches and pages opened this turn", ["th"] = "จำนวนไฟล์ที่อ่าน การค้นหา และเว็บที่เปิดในรอบนี้" },
        ["activity.count.editsTip"] = new() { ["en"] = "Changes written to files this turn", ["th"] = "จำนวนครั้งที่เขียน/แก้ไฟล์ในรอบนี้" },
        ["activity.count.commandsTip"] = new() { ["en"] = "Commands, builds and tests run this turn", ["th"] = "คำสั่ง บิลด์ และเทสต์ที่รันในรอบนี้" },
        ["activity.count.agentsTip"] = new() { ["en"] = "Sub-agents started this turn", ["th"] = "เอเจนต์ย่อยที่เรียกใช้ในรอบนี้" },

        ["activity.feed.empty"] = new() { ["en"] = "Steps appear here as the AI works", ["th"] = "ขั้นตอนที่ AI ทำจะขึ้นตรงนี้" },
        ["activity.elapsedTip"] = new() { ["en"] = "How long this turn has run", ["th"] = "เวลาที่ใช้ในรอบนี้" },
    };
}
