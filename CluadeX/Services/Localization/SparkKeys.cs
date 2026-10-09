namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the NVIDIA DGX Spark window. Every key needs both "en" and "th".</summary>
internal static class SparkKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["spark.title"] = new() { ["en"] = "CluadeX on NVIDIA DGX Spark", ["th"] = "CluadeX บน NVIDIA DGX Spark" },
        ["spark.subtitle"] = new()
        {
            ["en"] = "Run 120B-class coding models on your own desk — 128 GB of memory, your code never leaves the building, no monthly API bill.",
            ["th"] = "รันโมเดลเขียนโค้ดระดับ 120B บนโต๊ะของคุณเอง — หน่วยความจำ 128 GB โค้ดไม่ออกนอกบริษัท ไม่มีค่า API รายเดือน",
        },
        ["spark.chip"] = new() { ["en"] = "DGX Spark", ["th"] = "DGX Spark" },
        ["spark.chipTip"] = new()
        {
            ["en"] = "Use an NVIDIA DGX Spark on your network as CluadeX's model",
            ["th"] = "ใช้ NVIDIA DGX Spark ในเครือข่ายของคุณเป็นโมเดลของ CluadeX",
        },
        ["spark.paletteSubtitle"] = new() { ["en"] = "Connect an NVIDIA DGX Spark", ["th"] = "เชื่อมต่อ NVIDIA DGX Spark" },
        ["spark.how"] = new()
        {
            ["en"] = "The Spark serves the model with Ollama; CluadeX on this PC drives it over your local network. Models download onto the Spark — nothing large comes to this PC.",
            ["th"] = "Spark รันโมเดลด้วย Ollama ส่วน CluadeX บนพีซีเครื่องนี้สั่งงานผ่านเครือข่ายภายใน โมเดลดาวน์โหลดลง Spark โดยตรง ไม่มีไฟล์ใหญ่มาที่พีซีนี้",
        },
        ["spark.hostTitle"] = new() { ["en"] = "CONNECT TO YOUR SPARK", ["th"] = "เชื่อมต่อกับ Spark ของคุณ" },
        ["spark.hostPlaceholder"] = new() { ["en"] = "spark-xxxx.local or 192.168.1.50", ["th"] = "spark-xxxx.local หรือ 192.168.1.50" },
        ["spark.connect"] = new() { ["en"] = "Connect", ["th"] = "เชื่อมต่อ" },
        ["spark.connecting"] = new() { ["en"] = "Looking for the Spark…", ["th"] = "กำลังค้นหา Spark…" },
        ["spark.invalidHost"] = new()
        {
            ["en"] = "That isn't a host name or IP address.",
            ["th"] = "ชื่อเครื่องหรือ IP ไม่ถูกต้อง",
        },
        ["spark.unreachable"] = new()
        {
            ["en"] = "Nothing answered at {0}. Check that the Spark is on and on this network, and that Ollama listens on the network (OLLAMA_HOST=0.0.0.0).",
            ["th"] = "ไม่มีการตอบกลับจาก {0} — ตรวจว่า Spark เปิดอยู่และอยู่ในเครือข่ายเดียวกัน และ Ollama เปิดรับจากเครือข่าย (OLLAMA_HOST=0.0.0.0)",
        },
        ["spark.found"] = new()
        {
            ["en"] = "Connected: {0} {1} at {2} · {3} model(s) installed",
            ["th"] = "เชื่อมต่อแล้ว: {0} {1} ที่ {2} · ติดตั้งโมเดลแล้ว {3} ตัว",
        },
        ["spark.notOllama"] = new()
        {
            ["en"] = "{0} answered on port {1}, but CluadeX drives the Spark through Ollama. Install Ollama with NVIDIA's Spark playbook, then connect again.",
            ["th"] = "{0} ตอบที่พอร์ต {1} แต่ CluadeX ใช้งาน Spark ผ่าน Ollama — ติดตั้ง Ollama ตามคู่มือ Spark ของ NVIDIA แล้วกดเชื่อมต่ออีกครั้ง",
        },
        ["spark.modelsTitle"] = new() { ["en"] = "MODELS FOR THE SPARK", ["th"] = "โมเดลสำหรับ Spark" },
        ["spark.recommended"] = new() { ["en"] = "Recommended", ["th"] = "แนะนำ" },
        ["spark.installed"] = new() { ["en"] = "Installed", ["th"] = "ติดตั้งแล้ว" },
        ["spark.pull"] = new() { ["en"] = "Download to Spark", ["th"] = "ดาวน์โหลดลง Spark" },
        ["spark.use"] = new() { ["en"] = "Use", ["th"] = "ใช้งาน" },
        ["spark.inUse"] = new() { ["en"] = "In use", ["th"] = "กำลังใช้" },
        ["spark.cancel"] = new() { ["en"] = "Cancel", ["th"] = "ยกเลิก" },
        ["spark.connectFirst"] = new()
        {
            ["en"] = "Connect to the Spark first — then download or use a model.",
            ["th"] = "เชื่อมต่อ Spark ก่อน แล้วจึงดาวน์โหลดหรือเลือกใช้โมเดล",
        },
        ["spark.pullDone"] = new() { ["en"] = "{0} is ready on the Spark.", ["th"] = "{0} พร้อมใช้บน Spark แล้ว" },
        ["spark.pullFailed"] = new() { ["en"] = "Download failed: {0}", ["th"] = "ดาวน์โหลดไม่สำเร็จ: {0}" },
        ["spark.pullCancelled"] = new() { ["en"] = "Download cancelled.", ["th"] = "ยกเลิกการดาวน์โหลดแล้ว" },
        ["spark.useFailed"] = new() { ["en"] = "Couldn't switch to the Spark: {0}", ["th"] = "สลับไปใช้ Spark ไม่สำเร็จ: {0}" },
        ["spark.used"] = new()
        {
            ["en"] = "CluadeX now uses {0} on the Spark ({1}) with a {2}-token window.",
            ["th"] = "CluadeX ใช้ {0} บน Spark ({1}) แล้ว หน้าต่างบริบท {2} โทเคน",
        },
        ["spark.active"] = new() { ["en"] = "In use: {0} on {1}", ["th"] = "กำลังใช้: {0} บน {1}" },
        ["spark.contextRestored"] = new()
        {
            ["en"] = "CluadeX no longer uses the Spark, so the context window went back to {0} tokens (it was {1} on the Spark) — a model on this PC never loads with the Spark's size.",
            ["th"] = "CluadeX เลิกใช้ Spark แล้ว จึงคืนหน้าต่างบริบทกลับเป็น {0} โทเคน (ตอนใช้ Spark เป็น {1}) — โมเดลบนพีซีนี้จะไม่ถูกโหลดด้วยขนาดของ Spark",
        },
        ["spark.listFailed"] = new()
        {
            ["en"] = "Ollama answered at {0}, but its list of installed models didn't come: {1} Press Connect to try again.",
            ["th"] = "Ollama ที่ {0} ตอบกลับ แต่ไม่ได้ส่งรายการโมเดลที่ติดตั้งไว้มา: {1} กด \"เชื่อมต่อ\" เพื่อลองใหม่",
        },
        // ── Why something failed (the exception itself goes to the Debug Log, never to this window) ──
        ["spark.err.network"] = new()
        {
            ["en"] = "The Spark couldn't be reached over the network.",
            ["th"] = "ติดต่อ Spark ผ่านเครือข่ายไม่ได้",
        },
        ["spark.err.timeout"] = new() { ["en"] = "The Spark took too long to answer.", ["th"] = "Spark ตอบกลับช้าเกินไป" },
        ["spark.err.server"] = new()
        {
            ["en"] = "Ollama on the Spark answered with an error.",
            ["th"] = "Ollama บน Spark ตอบกลับมาเป็นข้อผิดพลาด",
        },
        ["spark.err.badResponse"] = new()
        {
            ["en"] = "The Spark sent a reply CluadeX couldn't read.",
            ["th"] = "Spark ส่งข้อมูลที่ CluadeX อ่านไม่ออกกลับมา",
        },
        ["spark.err.generic"] = new()
        {
            ["en"] = "Something went wrong — the details are in the Debug Log.",
            ["th"] = "เกิดข้อผิดพลาด — ดูรายละเอียดได้ใน Debug Log",
        },
        ["spark.err.busy"] = new()
        {
            ["en"] = "another model switch was still in progress. Nothing was changed — try again in a moment.",
            ["th"] = "ยังมีการสลับโมเดลอื่นค้างอยู่ จึงยังไม่ได้เปลี่ยนอะไร — ลองใหม่อีกครั้งในอีกสักครู่",
        },
        ["spark.err.notReady"] = new()
        {
            ["en"] = "Ollama on the Spark didn't come up, so CluadeX stays on the model it was using.",
            ["th"] = "Ollama บน Spark ไม่พร้อมทำงาน CluadeX จึงยังใช้โมเดลเดิมต่อไป",
        },
        ["spark.err.modelMissing"] = new()
        {
            ["en"] = "{0} is no longer on the Spark — connect again to refresh the list.",
            ["th"] = "ไม่พบ {0} บน Spark แล้ว — กดเชื่อมต่ออีกครั้งเพื่อโหลดรายการใหม่",
        },
        ["spark.err.openLink"] = new()
        {
            ["en"] = "Couldn't open the browser. The page is {0}",
            ["th"] = "เปิดเบราว์เซอร์ไม่ได้ — ที่อยู่ของหน้าคือ {0}",
        },
        // ── Closing while a download runs ──
        ["spark.closeTitle"] = new() { ["en"] = "Download in progress", ["th"] = "กำลังดาวน์โหลดอยู่" },
        ["spark.closeBody"] = new()
        {
            ["en"] = "Still downloading onto the Spark:\n{0}\n\nClosing this window stops the download.",
            ["th"] = "ยังดาวน์โหลดลง Spark อยู่:\n{0}\n\nถ้าปิดหน้าต่างนี้ การดาวน์โหลดจะหยุดลง",
        },
        ["spark.closeStop"] = new() { ["en"] = "Stop download and close", ["th"] = "หยุดดาวน์โหลดแล้วปิด" },
        ["spark.closeKeep"] = new() { ["en"] = "Keep downloading", ["th"] = "ดาวน์โหลดต่อ" },
        ["spark.speedNote"] = new()
        {
            ["en"] = "Speeds are published single-request measurements on one DGX Spark (NVIDIA developer blog and forum benchmarks). Real speed depends on quantisation, context length and serving stack.",
            ["th"] = "ความเร็วเป็นค่าที่มีผู้วัดและเผยแพร่บน DGX Spark หนึ่งเครื่อง แบบคำขอเดียว (บล็อกนักพัฒนาและฟอรัมของ NVIDIA) ความเร็วจริงขึ้นกับการบีบอัดโมเดล ความยาวบริบท และซอฟต์แวร์ที่ใช้รัน",
        },
        ["spark.lanNote"] = new()
        {
            ["en"] = "Prompts travel to the Spark over your local network as plain HTTP — keep it on a network you trust.",
            ["th"] = "ข้อความที่ส่งให้โมเดลวิ่งผ่านเครือข่ายภายในแบบ HTTP ธรรมดา — ใช้บนเครือข่ายที่ไว้ใจได้เท่านั้น",
        },
        ["spark.campaignTitle"] = new() { ["en"] = "No Spark yet?", ["th"] = "ยังไม่มี Spark?" },
        ["spark.campaignBody"] = new()
        {
            ["en"] = "DGX Spark bundled with lifetime top-tier CluadeX and BrainX licenses — 20 sets only.",
            ["th"] = "DGX Spark พร้อมไลเซนส์ CluadeX และ BrainX ระดับสูงสุดแบบตลอดชีพ — จำกัดเพียง 20 ชุด",
        },
        ["spark.campaignButton"] = new() { ["en"] = "See the bundle", ["th"] = "ดูแพ็กเกจ" },
        ["spark.close"] = new() { ["en"] = "Close", ["th"] = "ปิด" },
        ["spark.pick.gptoss120"] = new() { ["en"] = "Best all-round model on one Spark", ["th"] = "เก่งรอบด้านที่สุดบน Spark เครื่องเดียว" },
        ["spark.pick.qwen3coder"] = new() { ["en"] = "Built for coding agents — strong and still fast", ["th"] = "ออกแบบมาเพื่อเอเจนต์เขียนโค้ด — เก่งและยังเร็ว" },
        ["spark.pick.gptoss20"] = new() { ["en"] = "Quick edits, light on memory", ["th"] = "แก้ไขเร็ว ใช้หน่วยความจำน้อย" },
        ["spark.pick.qwen30"] = new() { ["en"] = "Fastest — long sessions, many small steps", ["th"] = "เร็วที่สุด — งานยาว ๆ หลายขั้นตอนย่อย" },
    };
}
