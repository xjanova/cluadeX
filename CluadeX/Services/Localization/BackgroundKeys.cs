namespace CluadeX.Services.Localization;

/// <summary>Localized strings for background processes and dev servers (bg_*, Task Manager) (team T2 owns this file). Every key needs both "en" and "th".</summary>
internal static class BackgroundKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ═══════ registry ═══════
        ["bg.none"] = new() { ["en"] = "none", ["th"] = "ไม่มี" },
        ["bg.err.badId"] = new() { ["en"] = "\"{0}\" is not a background process id — use a number such as 3 or bg#3.", ["th"] = "\"{0}\" ไม่ใช่รหัสโปรเซสเบื้องหลัง — ใช้ตัวเลข เช่น 3 หรือ bg#3" },
        ["bg.err.liveIds"] = new() { ["en"] = "Running now: {0}.", ["th"] = "ที่กำลังรันอยู่: {0}" },
        ["bg.err.unknownId"] = new() { ["en"] = "There is no background process bg#{0}. Running now: {1}.", ["th"] = "ไม่มีโปรเซสเบื้องหลัง bg#{0} — ที่กำลังรันอยู่: {1}" },
        ["bg.err.notRunning"] = new() { ["en"] = "bg#{0} is not running ({1}).", ["th"] = "bg#{0} ไม่ได้รันอยู่ ({1})" },
        ["bg.err.noUrl"] = new() { ["en"] = "bg#{0} has not printed a URL and has no known port yet — check bg_output id={0}.", ["th"] = "bg#{0} ยังไม่ได้แสดง URL และยังไม่รู้พอร์ต — ดู bg_output id={0}" },
        ["bg.err.shuttingDown"] = new() { ["en"] = "CluadeX is closing — nothing new is started.", ["th"] = "CluadeX กำลังปิด — จะไม่เริ่มโปรเซสใหม่" },
        ["bg.err.noCommand"] = new() { ["en"] = "Give a command to run (or config = the name of a launch.json configuration).", ["th"] = "ระบุคำสั่งที่จะรัน (หรือ config = ชื่อการตั้งค่าใน launch.json)" },
        ["bg.err.noFolder"] = new() { ["en"] = "The folder \"{0}\" does not exist.", ["th"] = "ไม่มีโฟลเดอร์ \"{0}\"" },
        ["bg.err.badPattern"] = new() { ["en"] = "The pattern is not a valid regular expression: {0}", ["th"] = "รูปแบบนี้ไม่ใช่ regular expression ที่ถูกต้อง: {0}" },
        ["bg.err.cap"] = new() { ["en"] = "{0} background processes are already running ({1}) — the most allowed at once. Stop one with bg_stop first.", ["th"] = "มีโปรเซสเบื้องหลังรันอยู่แล้ว {0} ตัว ({1}) ซึ่งเป็นจำนวนสูงสุด — หยุดสักตัวด้วย bg_stop ก่อน" },
        ["bg.err.startFailed"] = new() { ["en"] = "Could not start it: {0}", ["th"] = "เริ่มไม่สำเร็จ: {0}" },
        ["bg.err.portBusyPid"] = new() { ["en"] = "Port {0} is already in use by PID {1} ({2}). Stop that process or pick another port.", ["th"] = "พอร์ต {0} ถูกใช้อยู่แล้วโดย PID {1} ({2}) — หยุดโปรเซสนั้นหรือเลือกพอร์ตอื่น" },
        ["bg.err.portBusy"] = new() { ["en"] = "Port {0} is already in use. Stop whatever holds it or pick another port.", ["th"] = "พอร์ต {0} ถูกใช้อยู่แล้ว — หยุดโปรแกรมที่ใช้อยู่หรือเลือกพอร์ตอื่น" },
        ["bg.err.noFreePort"] = new() { ["en"] = "No free port between {0} and {1}. Stop a server or pass port=.", ["th"] = "ไม่มีพอร์ตว่างระหว่าง {0} ถึง {1} — หยุดเซิร์ฟเวอร์สักตัวหรือระบุ port=" },
        ["bg.err.exitedEarly"] = new() { ["en"] = "It exited with code {0} before it was ready.", ["th"] = "จบการทำงานด้วยรหัส {0} ก่อนจะพร้อมใช้งาน" },
        ["bg.static.name"] = new() { ["en"] = "static server: {0}", ["th"] = "เซิร์ฟเวอร์ไฟล์: {0}" },
        ["bg.log.startFailed"] = new() { ["en"] = "could not start: {0}", ["th"] = "เริ่มไม่สำเร็จ: {0}" },
        ["bg.log.noJob"] = new() { ["en"] = "not contained in a job object ({0}) — processes it leaves behind may survive a stop", ["th"] = "ไม่ได้อยู่ใน job object ({0}) — โปรเซสลูกที่หลุดออกไปอาจไม่ถูกหยุดตาม" },
        ["bg.log.serving"] = new() { ["en"] = "serving {0} at {1} (this computer only)", ["th"] = "ให้บริการ {0} ที่ {1} (เฉพาะเครื่องนี้)" },
        ["bg.log.stopped"] = new() { ["en"] = "stopped", ["th"] = "หยุดแล้ว" },
        ["bg.log.exited"] = new() { ["en"] = "exited with code {0}", ["th"] = "จบการทำงาน รหัส {0}" },

        // ═══════ run_command / powershell interceptor ═══════
        ["bg.watcher.refused"] = new()
        {
            ["en"] = "Not run: {0} watches for changes and never exits, so this call would hang until its timeout. Run the one-shot form instead: {1}.",
            ["th"] = "ไม่ได้รัน: {0} คอยเฝ้าดูการเปลี่ยนแปลงและไม่จบเอง คำสั่งนี้จะค้างจนหมดเวลา — ใช้แบบรันครั้งเดียวแทน: {1}",
        },
        ["bg.watcher.noWatchFlag"] = new() { ["en"] = "the same command without its watch flag", ["th"] = "คำสั่งเดิมโดยไม่ใส่ตัวเลือก watch" },
        ["bg.watcher.useBgStart"] = new() { ["en"] = "To keep a watcher running on purpose, start it with bg_start.", ["th"] = "ถ้าตั้งใจให้รันค้างไว้ ให้เริ่มด้วย bg_start" },
        ["bg.watcher.runOnce"] = new() { ["en"] = "the command itself, once", ["th"] = "รันคำสั่งนั้นตรง ๆ ครั้งเดียว" },
        ["bg.locked.server"] = new()
        {
            ["en"] = "Not run: {0} is a dev server that never exits — run_command would hang until its timeout. It would start in the background, but: {1} Meanwhile you can run it yourself in the Terminal drawer of the Code page, where it keeps running.",
            ["th"] = "ไม่ได้รัน: {0} เป็น dev server ที่ไม่จบเอง run_command จะค้างจนหมดเวลา ปกติจะเริ่มให้ในเบื้องหลัง แต่: {1} ระหว่างนี้รันเองได้ในแถบ Terminal ของหน้า Code ซึ่งจะรันค้างไว้ให้",
        },
        ["bg.timeout.server"] = new()
        {
            ["en"] = "[It printed {0} — this is a server. Start it with bg_start so it keeps running (bg_output reads its log, bg_stop ends it).]",
            ["th"] = "[คำสั่งนี้แสดง {0} — แปลว่าเป็นเซิร์ฟเวอร์ ให้เริ่มด้วย bg_start เพื่อให้รันค้างไว้ (อ่าน log ด้วย bg_output หยุดด้วย bg_stop)]",
        },
        ["bg.timeout.serverLocked"] = new()
        {
            ["en"] = "[It printed {0} — this is a server, which needs to run in the background: {1} Meanwhile run it yourself in the Terminal drawer of the Code page.]",
            ["th"] = "[คำสั่งนี้แสดง {0} — เป็นเซิร์ฟเวอร์ที่ต้องรันเบื้องหลัง แต่: {1} ระหว่างนี้รันเองได้ในแถบ Terminal ของหน้า Code]",
        },

        // ═══════ tool results ═══════
        ["bg.perm.runsIn"] = new() { ["en"] = "runs in: {0} — in the background, until stopped", ["th"] = "รันในโฟลเดอร์: {0} — ทำงานเบื้องหลังจนกว่าจะสั่งหยุด" },
        ["bg.perm.fromConfig"] = new() { ["en"] = "from launch.json configuration \"{0}\" ({1})", ["th"] = "จากการตั้งค่า launch.json \"{0}\" ({1})" },
        ["bg.perm.serveOutside"] = new() { ["en"] = "serve this folder (outside the project) at http://127.0.0.1 — this computer only", ["th"] = "เปิดให้เข้าถึงโฟลเดอร์นี้ (นอกโปรเจกต์) ที่ http://127.0.0.1 — เฉพาะเครื่องนี้" },
        ["bg.err.unknownTool"] = new() { ["en"] = "Unknown background tool: {0}", ["th"] = "ไม่รู้จักเครื่องมือเบื้องหลัง: {0}" },
        ["bg.err.badEnv"] = new() { ["en"] = "env must be a JSON object such as {\"PORT\":\"3001\"}.", ["th"] = "env ต้องเป็น JSON object เช่น {\"PORT\":\"3001\"}" },
        ["bg.err.noConfig"] = new() { ["en"] = "No launch configuration named \"{0}\" in .cluadex/launch.json or .claude/launch.json. Defined: {1}.", ["th"] = "ไม่พบการตั้งค่าชื่อ \"{0}\" ใน .cluadex/launch.json หรือ .claude/launch.json — ที่มีอยู่: {1}" },
        ["bg.err.badPort"] = new() { ["en"] = "\"{0}\" is not a port number (1-65535).", ["th"] = "\"{0}\" ไม่ใช่หมายเลขพอร์ต (1-65535)" },
        ["bg.err.configChanged"] = new()
        {
            ["en"] = "The launch.json configuration \"{0}\" changed after it was checked — nothing was started. Run bg_start config={0} again.",
            ["th"] = "การตั้งค่า launch.json \"{0}\" ถูกแก้หลังจากตรวจไปแล้ว — ยังไม่ได้เริ่มอะไร ให้สั่ง bg_start config={0} ใหม่อีกครั้ง",
        },
        ["bg.out.output"] = new() { ["en"] = "--- output ---", ["th"] = "--- ผลลัพธ์ ---" },
        ["bg.out.fullHint"] = new()
        {
            ["en"] = "(the last {1} of {2} lines are shown — all of it: bg_output id={0} since=start lines=2000)",
            ["th"] = "(แสดง {1} บรรทัดสุดท้ายจากทั้งหมด {2} บรรทัด — ดูทั้งหมด: bg_output id={0} since=start lines=2000)",
        },
        ["bg.out.alreadyShown"] = new() { ["en"] = "({0} earlier lines already shown — since=start shows them again)", ["th"] = "({0} บรรทัดก่อนหน้าแสดงไปแล้ว — ใช้ since=start เพื่อดูอีกครั้ง)" },
        ["bg.out.omitted"] = new() { ["en"] = "({0} older lines left out — raise lines= to see more)", ["th"] = "(ข้าม {0} บรรทัดที่เก่ากว่า — เพิ่ม lines= เพื่อดูมากขึ้น)" },
        ["bg.out.empty"] = new() { ["en"] = "(no output)", ["th"] = "(ไม่มีผลลัพธ์)" },
        ["bg.out.noNew"] = new() { ["en"] = "(no new output since the last read)", ["th"] = "(ไม่มีผลลัพธ์ใหม่ตั้งแต่อ่านครั้งก่อน)" },
        ["bg.out.inLog"] = new() { ["en"] = "({0} older lines are only in the log file: {1})", ["th"] = "({0} บรรทัดที่เก่ากว่าอยู่ในไฟล์ log เท่านั้น: {1})" },
        ["bg.out.lastLines"] = new() { ["en"] = "--- last output ---", ["th"] = "--- ผลลัพธ์ล่าสุด ---" },
        ["bg.sum.output"] = new() { ["en"] = "bg#{0}: {1} lines", ["th"] = "bg#{0}: {1} บรรทัด" },
        ["bg.sum.list"] = new() { ["en"] = "{0} running of {1}", ["th"] = "รันอยู่ {0} จาก {1}" },
        ["bg.sum.already"] = new() { ["en"] = "already running", ["th"] = "รันอยู่แล้ว" },
        ["bg.sum.finished"] = new() { ["en"] = "bg#{0} finished", ["th"] = "bg#{0} ทำงานจบแล้ว" },
        ["bg.stop.noneRunning"] = new() { ["en"] = "No background process is running.", ["th"] = "ไม่มีโปรเซสเบื้องหลังที่รันอยู่" },
        ["bg.stop.all"] = new() { ["en"] = "Stopped {0} background process(es) with everything they started.", ["th"] = "หยุดโปรเซสเบื้องหลัง {0} ตัวพร้อมทุกอย่างที่มันเปิดไว้แล้ว" },
        ["bg.stop.done"] = new() { ["en"] = "Stopped bg#{0} with everything it started.", ["th"] = "หยุด bg#{0} พร้อมทุกอย่างที่มันเปิดไว้แล้ว" },
        ["bg.stop.already"] = new() { ["en"] = "bg#{0} had already ended ({1}, exit code {2}).", ["th"] = "bg#{0} จบไปก่อนแล้ว ({1}, รหัส {2})" },
        ["bg.stop.portStillBusy"] = new() { ["en"] = "Port {0} is still in use: {1}", ["th"] = "พอร์ต {0} ยังถูกใช้อยู่: {1}" },
        ["bg.list.empty"] = new() { ["en"] = "No background processes.", ["th"] = "ไม่มีโปรเซสเบื้องหลัง" },
        ["bg.list.cwd"] = new() { ["en"] = "in {0}", ["th"] = "ที่ {0}" },
        ["bg.list.last"] = new() { ["en"] = "last: {0}", ["th"] = "ล่าสุด: {0}" },
        ["bg.exitCode"] = new() { ["en"] = "exit code {0}", ["th"] = "รหัส {0}" },
        ["bg.inProcess"] = new() { ["en"] = "inside CluadeX", ["th"] = "ทำงานใน CluadeX" },
        ["bg.up"] = new() { ["en"] = "up {0}", ["th"] = "รันมา {0}" },
        ["bg.ran"] = new() { ["en"] = "ran {0}", ["th"] = "รันไป {0}" },
        ["bg.noJobShort"] = new() { ["en"] = "not in a job", ["th"] = "ไม่ได้อยู่ใน job" },
        ["bg.start.readyIn"] = new() { ["en"] = "ready in {0} s", ["th"] = "พร้อมใน {0} วินาที" },
        ["bg.start.ready"] = new() { ["en"] = "Started in background as bg#{0} → {1}{2}. It keeps running after this call.", ["th"] = "เริ่มในเบื้องหลังเป็น bg#{0} → {1}{2} และจะรันต่อไปหลังจบคำสั่งนี้" },
        ["bg.start.notReady"] = new() { ["en"] = "Started in background as bg#{0} — still running after {1}, but nothing has said it is ready yet (no URL printed). It keeps running.", ["th"] = "เริ่มในเบื้องหลังเป็น bg#{0} — ยังรันอยู่หลังผ่านไป {1} แต่ยังไม่มีสัญญาณว่าพร้อม (ยังไม่แสดง URL) และจะรันต่อไป" },
        ["bg.start.portNotOpen"] = new() { ["en"] = "Port {0} is not accepting connections yet.", ["th"] = "พอร์ต {0} ยังไม่รับการเชื่อมต่อ" },
        ["bg.start.already"] = new() { ["en"] = "Already running as bg#{0} → {1} (since {2}). To restart it: bg_stop id={0}, then start it again.", ["th"] = "รันอยู่แล้วเป็น bg#{0} → {1} (ตั้งแต่ {2}) ถ้าจะเริ่มใหม่: bg_stop id={0} แล้วสั่งเริ่มอีกครั้ง" },
        ["bg.start.finishedRun"] = new() { ["en"] = "It ran to completion (exit code 0) in {1} — it is not a long-running server, so nothing is left running (bg#{0}).", ["th"] = "คำสั่งทำงานจบแล้ว (รหัส 0) ใน {1} — ไม่ใช่เซิร์ฟเวอร์ที่รันค้าง จึงไม่มีอะไรค้างอยู่ (bg#{0})" },
        ["bg.start.finished"] = new() { ["en"] = "bg#{0} ran to completion (exit code 0) in {1} — it is not a long-running process.", ["th"] = "bg#{0} ทำงานจบแล้ว (รหัส 0) ใน {1} — ไม่ใช่โปรเซสที่รันค้าง" },
        ["bg.start.failed"] = new() { ["en"] = "bg#{0} exited with code {1} before it was ready.", ["th"] = "bg#{0} จบการทำงานด้วยรหัส {1} ก่อนจะพร้อมใช้งาน" },
        ["bg.start.cancelled"] = new() { ["en"] = "Cancelled — bg#{0} was stopped.", ["th"] = "ยกเลิกแล้ว — หยุด bg#{0} แล้ว" },
        ["bg.start.hints"] = new() { ["en"] = "logs: bg_output id={0} · stop: bg_stop id={0}", ["th"] = "ดู log: bg_output id={0} · หยุด: bg_stop id={0}" },
        ["bg.start.hintOpen"] = new() { ["en"] = "open: browser_open url=bg:{0}", ["th"] = "เปิดดู: browser_open url=bg:{0}" },

        // ═══════ Task Manager page ═══════
        ["tasks.colKind"] = new() { ["en"] = "Kind", ["th"] = "ชนิด" },
        ["tasks.colPort"] = new() { ["en"] = "Port", ["th"] = "พอร์ต" },
        ["tasks.colUrl"] = new() { ["en"] = "URL", ["th"] = "URL" },
        ["tasks.colExit"] = new() { ["en"] = "Exit", ["th"] = "รหัสจบ" },
        ["tasks.stopAll"] = new() { ["en"] = "Stop All", ["th"] = "หยุดทั้งหมด" },
        ["tasks.stopAllConfirm"] = new()
        {
            ["en"] = "Stop all {0} running background process(es)? Dev servers the agent started stop too, with everything they started.",
            ["th"] = "หยุดโปรเซสเบื้องหลังที่รันอยู่ทั้งหมด {0} ตัวใช่ไหม? dev server ที่เอเจนต์เปิดไว้จะถูกหยุดด้วย พร้อมทุกอย่างที่มันเปิดไว้",
        },
        ["tasks.stopAllDone"] = new() { ["en"] = "Stopped {0} process(es).", ["th"] = "หยุดแล้ว {0} ตัว" },
        ["tasks.nothingRunning"] = new() { ["en"] = "Nothing is running.", ["th"] = "ไม่มีอะไรรันอยู่" },
        ["tasks.startFailed"] = new() { ["en"] = "Not started: {0}", ["th"] = "ไม่ได้เริ่ม: {0}" },
        ["tasks.openUrlTip"] = new() { ["en"] = "Open in your browser", ["th"] = "เปิดในเบราว์เซอร์" },
        ["tasks.openUrlFailed"] = new() { ["en"] = "Could not open {0}", ["th"] = "เปิด {0} ไม่ได้" },
        ["tasksm.kind.shell"] = new() { ["en"] = "command", ["th"] = "คำสั่ง" },
        ["tasksm.kind.static"] = new() { ["en"] = "file server", ["th"] = "เซิร์ฟเวอร์ไฟล์" },
        ["tasksm.kind.adopted"] = new() { ["en"] = "adopted", ["th"] = "รับช่วงมา" },
    };
}
