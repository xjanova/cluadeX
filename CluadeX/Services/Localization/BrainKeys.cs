namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the BrainX invitation window. Every key needs both "en" and "th".</summary>
internal static class BrainKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["brainx.kicker"] = new() { ["en"] = "BRAINX — MEMORY FOR AGENTS", ["th"] = "BRAINX — ความจำสำหรับเอเจนต์" },
        ["brainx.title"] = new() { ["en"] = "Make the coding agent smarter with BrainX", ["th"] = "ให้เอเจนต์เขียนโค้ดได้ฉลาดขึ้นด้วย BrainX" },
        ["brainx.subNotInstalled"] = new()
        {
            ["en"] = "BrainX isn't on this PC yet. Install it and CluadeX connects to it by itself.",
            ["th"] = "ยังไม่พบ BrainX ในเครื่องนี้ ติดตั้งแล้ว CluadeX จะเชื่อมต่อให้เองอัตโนมัติ",
        },
        ["brainx.subInstalled"] = new()
        {
            ["en"] = "BrainX is installed on this PC but not connected to CluadeX yet.",
            ["th"] = "พบ BrainX ในเครื่องนี้แล้ว แต่ยังไม่ได้เชื่อมต่อกับ CluadeX",
        },
        ["brainx.subConfigured"] = new()
        {
            ["en"] = "BrainX is set up but not running right now.",
            ["th"] = "ตั้งค่า BrainX ไว้แล้ว แต่ตอนนี้ยังไม่ได้ทำงาน",
        },
        ["brainx.subConnected"] = new()
        {
            ["en"] = "BrainX is connected — the agent recalls past work before it starts.",
            ["th"] = "เชื่อมต่อ BrainX แล้ว — เอเจนต์ค้นความจำจากงานเก่าก่อนลงมือทุกครั้ง",
        },
        ["brainx.benefitsTitle"] = new() { ["en"] = "WHAT YOU GET", ["th"] = "สิ่งที่ได้" },
        ["brainx.b1"] = new() { ["en"] = "Remembers lessons from past work", ["th"] = "จำบทเรียนจากงานเก่า" },
        ["brainx.b1d"] = new() { ["en"] = "The agent searches its memory before it acts — fewer repeated mistakes.", ["th"] = "เอเจนต์ค้นความจำก่อนลงมือ ลดการทำผิดซ้ำ" },
        ["brainx.b2"] = new() { ["en"] = "Knows your projects", ["th"] = "รู้จักโปรเจกต์ของคุณ" },
        ["brainx.b2d"] = new() { ["en"] = "Notes, conventions and fix history are pulled in while it codes.", ["th"] = "โน้ต แนวทาง และประวัติการแก้ไขถูกดึงมาใช้ตอนเขียนโค้ด" },
        ["brainx.b3"] = new() { ["en"] = "Writes down what it learns", ["th"] = "บันทึกสิ่งที่ได้เรียนรู้ให้เอง" },
        ["brainx.b3d"] = new() { ["en"] = "The next task starts from what is already known.", ["th"] = "งานถัดไปเริ่มจากสิ่งที่รู้แล้ว" },
        ["brainx.b4"] = new() { ["en"] = "Your data stays on your PC", ["th"] = "ข้อมูลอยู่ในเครื่องคุณ" },
        ["brainx.b4d"] = new() { ["en"] = "Memory lives in your own folder. Free, no key needed.", ["th"] = "ความจำเก็บในโฟลเดอร์ของคุณเอง ใช้ฟรีไม่ต้องมีคีย์" },
        ["brainx.dontShow"] = new() { ["en"] = "Don't show this again", ["th"] = "ไม่ต้องแสดงหน้าต่างนี้อีก" },
        ["brainx.readFirst"] = new() { ["en"] = "Read more first", ["th"] = "อ่านรายละเอียดก่อน" },
        ["brainx.notInterested"] = new() { ["en"] = "Not interested", ["th"] = "ไม่สนใจ" },
        ["brainx.install"] = new() { ["en"] = "Install BrainX (free)", ["th"] = "ติดตั้ง BrainX (ฟรี)" },
        ["brainx.connect"] = new() { ["en"] = "Connect BrainX", ["th"] = "เชื่อมต่อ BrainX" },
        ["brainx.close"] = new() { ["en"] = "Close", ["th"] = "ปิด" },
        ["brainx.connecting"] = new() { ["en"] = "Connecting to BrainX…", ["th"] = "กำลังเชื่อมต่อ BrainX…" },
        ["brainx.connected"] = new() { ["en"] = "Connected — {0} tools available to the agent.", ["th"] = "เชื่อมต่อแล้ว — เอเจนต์ใช้เครื่องมือของ BrainX ได้ {0} ตัว" },
        ["brainx.connectFailed"] = new()
        {
            ["en"] = "Couldn't connect: {0}. Open MCP Servers (Ctrl+5) for the log.",
            ["th"] = "เชื่อมต่อไม่สำเร็จ: {0} — ดูบันทึกได้ที่หน้า MCP Servers (Ctrl+5)",
        },
        ["brainx.waitingInstall"] = new()
        {
            ["en"] = "The BrainX download page is open. When the install finishes, CluadeX connects by itself — keep this window open.",
            ["th"] = "เปิดหน้าดาวน์โหลด BrainX แล้ว ติดตั้งเสร็จเมื่อไร CluadeX จะเชื่อมต่อให้เอง — เปิดหน้าต่างนี้ทิ้งไว้ได้เลย",
        },
        ["brainx.chipTip"] = new() { ["en"] = "BrainX — click for status or to connect", ["th"] = "BrainX — คลิกเพื่อดูสถานะหรือเชื่อมต่อ" },
    };
}
