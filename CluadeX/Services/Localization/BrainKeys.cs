namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the BrainX invitation window, the brain bridge, and the MCP config-file
/// safety notices / supervisor messages that go with them. Every key needs both "en" and "th".</summary>
internal static class BrainKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ─── Where a server is restarted from ───
        // The Ctrl+K palette lists the page as "MCP Servers" in BOTH languages (its search does not know the Thai
        // name), so the Thai route names it exactly that. The 🧠 chip is offered only for the brain: it is about
        // BrainX, and for any other server it opens the wrong thing. {n} in the messages below is one of these.
        ["mcp.route.page"] = new() { ["en"] = "Ctrl+K → MCP Servers", ["th"] = "Ctrl+K → MCP Servers" },
        ["mcp.route.pageOrChip"] = new()
        {
            ["en"] = "Ctrl+K → MCP Servers, or click the 🧠 chip",
            ["th"] = "Ctrl+K → MCP Servers หรือคลิกชิป 🧠",
        },

        // ─── MCP supervisor (chip tooltip + MCP Servers page log) ───
        ["mcp.supervisor.crashLoop"] = new()
        {
            ["en"] = "Died {0}× within {1}s of starting — not restarting it again. Fix the server, then restart it from MCP Servers ({2}).",
            ["th"] = "หยุดทำงานไป {0} ครั้งภายใน {1} วินาทีหลังเริ่ม — จะไม่เริ่มใหม่ให้อีก แก้เซิร์ฟเวอร์ให้เรียบร้อยแล้วเริ่มใหม่จากหน้า เซิร์ฟเวอร์ MCP ({2})",
        },
        ["mcp.supervisor.gaveUp"] = new()
        {
            ["en"] = "Gave up after {0} reconnect attempts — restart it from MCP Servers ({1}).",
            ["th"] = "ลองเชื่อมต่อใหม่ {0} ครั้งแล้วไม่สำเร็จ — เริ่มใหม่ได้จากหน้า เซิร์ฟเวอร์ MCP ({1})",
        },

        // ─── mcp_servers.json safety (MCP Servers page status line) ───
        ["mcp.config.unreadable"] = new()
        {
            ["en"] = "mcp_servers.json could not be read, so CluadeX is not changing it — every server and key in it is safe. Fix the file (the reason is in the Debug Log), then press Refresh.",
            ["th"] = "อ่านไฟล์ mcp_servers.json ไม่ได้ CluadeX จึงไม่แก้ไขไฟล์นี้ — เซิร์ฟเวอร์และคีย์ทั้งหมดในไฟล์ยังอยู่ครบ แก้ไฟล์ให้ถูกต้อง (ดูสาเหตุได้ที่หน้า Debug Log) แล้วกด รีเฟรช",
        },
        ["mcp.config.someUnreadable"] = new()
        {
            ["en"] = "Some servers in mcp_servers.json could not be read and are not listed: {0}. They are left in the file exactly as they are — fix them, then press Refresh.",
            ["th"] = "อ่านเซิร์ฟเวอร์บางตัวใน mcp_servers.json ไม่ได้ จึงไม่แสดงในรายการ: {0} — ข้อมูลของเซิร์ฟเวอร์เหล่านี้ยังอยู่ในไฟล์ครบตามเดิม แก้ให้ถูกต้องแล้วกด รีเฟรช",
        },
        // A write that failed although the file reads fine: locked, read-only, disk full — not "could not be read".
        ["mcp.config.saveFailed"] = new()
        {
            ["en"] = "Couldn't save mcp_servers.json — it may be open in another program or read-only, or the disk is full. The file is as it was; the change lasts until CluadeX closes. Details are in the Debug Log.",
            ["th"] = "บันทึก mcp_servers.json ไม่สำเร็จ — ไฟล์อาจถูกเปิดค้างในโปรแกรมอื่น ถูกตั้งเป็นอ่านอย่างเดียว หรือดิสก์เต็ม ไฟล์ยังเหมือนเดิม การเปลี่ยนแปลงมีผลจนกว่าจะปิด CluadeX ดูรายละเอียดที่หน้า Debug Log",
        },
        ["mcp.config.rereading"] = new()
        {
            ["en"] = "Reading mcp_servers.json again…",
            ["th"] = "กำลังอ่าน mcp_servers.json ใหม่…",
        },
        ["mcp.config.reloaded"] = new()
        {
            ["en"] = "mcp_servers.json was read again — {0} server(s), {1} running.",
            ["th"] = "อ่าน mcp_servers.json ใหม่แล้ว — เซิร์ฟเวอร์ {0} ตัว ทำงานอยู่ {1} ตัว",
        },
        // Read again from the BrainX window: the servers startup never got to are waiting for the page's Refresh.
        ["mcp.config.reloadedNotStarted"] = new()
        {
            ["en"] = "mcp_servers.json was read again. Not started yet: {0} — press Refresh to start them.",
            ["th"] = "อ่าน mcp_servers.json ใหม่แล้ว ยังไม่ได้เริ่ม: {0} — กด รีเฟรช เพื่อเริ่มเซิร์ฟเวอร์เหล่านี้",
        },

        // ─── Brain bridge (Instincts page) ───
        ["brain.sync.notRunning"] = new()
        {
            ["en"] = "The BrainX brain server is not running. Set it up in MCP Servers (Ctrl+K → MCP Servers, or click the 🧠 chip).",
            ["th"] = "เซิร์ฟเวอร์สมอง BrainX ไม่ได้ทำงานอยู่ ตั้งค่าได้ที่หน้า เซิร์ฟเวอร์ MCP (Ctrl+K → MCP Servers หรือคลิกชิป 🧠)",
        },

        // ─── BrainX invitation window ───
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
        ["brainx.subStarting"] = new()
        {
            ["en"] = "BrainX is starting up — it will be ready in a moment.",
            ["th"] = "BrainX กำลังเริ่มทำงาน — อีกสักครู่จะพร้อมใช้",
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
        // {0} is one of the brainx.reason* sentences below — never raw exception text.
        // The chip is not offered as a route here: while BrainX is not connected it opens this same window.
        ["brainx.connectFailed"] = new()
        {
            ["en"] = "Couldn't connect: {0}. The server log is on the MCP Servers page (Ctrl+K → MCP Servers).",
            ["th"] = "เชื่อมต่อไม่สำเร็จ: {0} — ดูบันทึกของเซิร์ฟเวอร์ได้ที่หน้า เซิร์ฟเวอร์ MCP (Ctrl+K → MCP Servers)",
        },
        // Connect already read the file once more before saying this — so: fix it, then try again.
        ["brainx.reasonConfig"] = new()
        {
            ["en"] = "mcp_servers.json could not be read, so CluadeX did not change it — fix the file, then try again (or press Refresh on the MCP Servers page)",
            ["th"] = "อ่านไฟล์ mcp_servers.json ไม่ได้ CluadeX จึงไม่แก้ไขไฟล์นี้ — แก้ไฟล์ให้ถูกต้องแล้วลองใหม่ (หรือกด รีเฟรช ที่หน้า เซิร์ฟเวอร์ MCP)",
        },
        ["brainx.reasonSave"] = new()
        {
            ["en"] = "mcp_servers.json could not be saved — it may be open in another program or read-only; it is as it was",
            ["th"] = "บันทึกไฟล์ mcp_servers.json ไม่ได้ — ไฟล์อาจถูกเปิดค้างในโปรแกรมอื่นหรือถูกตั้งเป็นอ่านอย่างเดียว ไฟล์ยังเหมือนเดิม",
        },
        ["brainx.reasonMissing"] = new()
        {
            ["en"] = "BrainX's MCP program was not found on this PC",
            ["th"] = "ไม่พบโปรแกรม MCP ของ BrainX ในเครื่องนี้",
        },
        ["brainx.reasonStart"] = new()
        {
            ["en"] = "the BrainX server did not start",
            ["th"] = "เซิร์ฟเวอร์ BrainX ไม่เริ่มทำงาน",
        },
        ["brainx.openFailed"] = new()
        {
            ["en"] = "Couldn't open the browser — visit {0} yourself.",
            ["th"] = "เปิดเบราว์เซอร์ไม่ได้ — เข้าไปที่ {0} ด้วยตัวเองได้เลย",
        },
        ["brainx.waitingInstall"] = new()
        {
            ["en"] = "The BrainX download page is open. When the install finishes, CluadeX connects by itself — keep this window open.",
            ["th"] = "เปิดหน้าดาวน์โหลด BrainX แล้ว ติดตั้งเสร็จเมื่อไร CluadeX จะเชื่อมต่อให้เอง — เปิดหน้าต่างนี้ทิ้งไว้ได้เลย",
        },
        ["brainx.chipTip"] = new() { ["en"] = "BrainX — click for status or to connect", ["th"] = "BrainX — คลิกเพื่อดูสถานะหรือเชื่อมต่อ" },
    };
}
