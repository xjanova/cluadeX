namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the per-turn test card (TurnTests). Every key needs both "en" and "th".</summary>
internal static class TurnTestKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["tests.title"] = new() { ["en"] = "Tests", ["th"] = "ผลการทดสอบ" },
        ["tests.status.allPassed"] = new() { ["en"] = "{0} suite(s) · all passed", ["th"] = "{0} ชุด · ผ่านทั้งหมด" },
        ["tests.status.someFailed"] = new() { ["en"] = "{0} of {1} suite(s) failed", ["th"] = "ไม่ผ่าน {0} จาก {1} ชุด" },
        ["tests.status.pending"] = new() { ["en"] = "still running in the background", ["th"] = "ยังรันอยู่เบื้องหลัง" },
        ["tests.status.none"] = new() { ["en"] = "No tests ran this turn", ["th"] = "เทิร์นนี้ไม่ได้รันเทส" },
        ["tests.col.suite"] = new() { ["en"] = "Suite", ["th"] = "ชุดทดสอบ" },
        ["tests.col.result"] = new() { ["en"] = "Result", ["th"] = "ผล" },
        ["tests.col.time"] = new() { ["en"] = "Time", ["th"] = "เวลา" },
        ["tests.passed"] = new() { ["en"] = "{0} passed", ["th"] = "ผ่าน {0}" },
        ["tests.failed"] = new() { ["en"] = "{0} failed", ["th"] = "ไม่ผ่าน {0}" },
        ["tests.skipped"] = new() { ["en"] = "{0} skipped", ["th"] = "ข้าม {0}" },
        ["tests.ok"] = new() { ["en"] = "passed", ["th"] = "ผ่าน" },
        ["tests.notOk"] = new() { ["en"] = "failed", ["th"] = "ไม่ผ่าน" },
        ["tests.running"] = new() { ["en"] = "running…", ["th"] = "กำลังรัน…" },
        ["tests.runs"] = new() { ["en"] = "×{0}", ["th"] = "×{0}" },
        ["tests.runsTip"] = new() { ["en"] = "Ran {0} times this turn — the line shows the last run", ["th"] = "รัน {0} ครั้งในเทิร์นนี้ — แถวนี้แสดงผลครั้งล่าสุด" },
        ["tests.kind.real"] = new() { ["en"] = "REAL", ["th"] = "ต้องแก้" },
        ["tests.kind.flaky"] = new() { ["en"] = "FLAKY", ["th"] = "ไม่นิ่ง" },
        ["tests.kind.unrelated"] = new() { ["en"] = "UNRELATED", ["th"] = "ไม่เกี่ยว" },
        ["tests.kind.fixed"] = new() { ["en"] = "FIXED", ["th"] = "แก้แล้ว" },
        ["tests.why.real"] = new() { ["en"] = "still fails, and it touches what this turn changed", ["th"] = "ยังไม่ผ่าน และเกี่ยวกับสิ่งที่เทิร์นนี้แก้" },
        ["tests.why.realNoChanges"] = new() { ["en"] = "still fails", ["th"] = "ยังไม่ผ่าน" },
        ["tests.why.flaky"] = new() { ["en"] = "a rerun passed with nothing changed in between", ["th"] = "รันซ้ำแล้วผ่าน โดยไม่ได้แก้อะไรระหว่างนั้น" },
        ["tests.why.unrelated"] = new() { ["en"] = "still fails, but it touches none of the {0} file(s) this turn changed", ["th"] = "ยังไม่ผ่าน แต่ไม่เกี่ยวกับ {0} ไฟล์ที่เทิร์นนี้แก้" },
        ["tests.why.fixed"] = new() { ["en"] = "passed after a fix later in this turn", ["th"] = "ผ่านหลังแก้โค้ดในเทิร์นนี้" },
        ["tests.unnamed"] = new() { ["en"] = "the runner named no test", ["th"] = "ตัวรันไม่ได้บอกชื่อเทส" },
        ["tests.moreFailures"] = new() { ["en"] = "+{0} more failing", ["th"] = "ไม่ผ่านอีก {0} รายการ" },
        ["tests.skippedTitle"] = new() { ["en"] = "Not run — out of scope", ["th"] = "ไม่ได้รัน — ไม่เกี่ยวกับงานนี้" },
        ["tests.commandTip"] = new() { ["en"] = "Command: {0}", ["th"] = "คำสั่ง: {0}" },
        ["tests.plain"] = new() { ["en"] = "Tests: {0}", ["th"] = "ผลการทดสอบ: {0}" },
    };
}
