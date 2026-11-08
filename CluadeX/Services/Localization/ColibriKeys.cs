namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the colibri plugin (Plugins page card) and the on-PC savings chart (Billing ▸ savings).
/// Every key needs both "en" and "th".</summary>
internal static class ColibriKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ── the card ──
        ["colibri.title"] = new() { ["en"] = "On-PC AI · Colibri", ["th"] = "AI ในเครื่อง · น้อง Colibri" },
        ["colibri.tagline"] = new()
        {
            ["en"] = "Runs big mixture-of-experts models on a small GPU: the core in RAM, the experts streamed from the SSD, the busiest ones on the card. "
                   + "Read-and-search sub-agents use her instead of the API — no tokens paid.",
            ["th"] = "รันโมเดล MoE ตัวใหญ่ด้วยการ์ดจอเล็ก: ส่วนหลักอยู่ใน RAM ผู้เชี่ยวชาญอ่านจาก SSD ตัวที่ใช้บ่อยขึ้นไปอยู่บนการ์ด "
                   + "เอเจนต์ย่อยงานอ่าน/ค้นหาจะมาใช้น้องแทน API — ไม่เสียค่าโทเค็น",
        },
        ["colibri.notInstalled"] = new() { ["en"] = "Not installed", ["th"] = "ยังไม่ได้ติดตั้ง" },
        ["colibri.installed"] = new() { ["en"] = "Installed {0}", ["th"] = "ติดตั้งแล้ว {0}" },
        ["colibri.install"] = new() { ["en"] = "Install Colibri", ["th"] = "ติดตั้ง Colibri" },
        ["colibri.installing"] = new() { ["en"] = "Installing the engine… {0}", ["th"] = "กำลังติดตั้งเครื่องยนต์… {0}" },
        ["colibri.checkUpdate"] = new() { ["en"] = "Check for updates", ["th"] = "ตรวจหาอัปเดต" },
        ["colibri.update"] = new() { ["en"] = "Update to {0}", ["th"] = "อัปเดตเป็น {0}" },
        ["colibri.upToDate"] = new() { ["en"] = "Up to date ({0})", ["th"] = "เป็นรุ่นล่าสุดแล้ว ({0})" },
        ["colibri.updateFound"] = new() { ["en"] = "{0} is out — you have {1}", ["th"] = "มีรุ่นใหม่ {0} — ตอนนี้ใช้ {1}" },
        ["colibri.rollback"] = new() { ["en"] = "Back to {0}", ["th"] = "ย้อนกลับไป {0}" },
        ["colibri.rolledBack"] = new() { ["en"] = "Back on {0}. Start her again to use it.", ["th"] = "ย้อนกลับไปใช้ {0} แล้ว กดเปิดน้องอีกครั้งเพื่อใช้งาน" },
        ["colibri.updateHow"] = new()
        {
            ["en"] = "An update downloads the new release, checks its checksum, stops her, switches the engine and sets the same model up again (nothing is downloaded twice). The old engine is kept for a rollback; your models stay.",
            ["th"] = "อัปเดตจะดาวน์โหลดรุ่นใหม่ ตรวจ checksum หยุดน้อง สลับเครื่องยนต์ แล้วตั้งค่าโมเดลเดิมให้ใหม่ (ไม่ดาวน์โหลดโมเดลซ้ำ) รุ่นเก่าเก็บไว้ให้ย้อนกลับได้ โมเดลที่โหลดไว้อยู่ครบ",
        },

        ["colibri.noPython"] = new()
        {
            ["en"] = "Colibri's launcher needs Python 3.10 or newer (the engines are C). Install it, then press Refresh.",
            ["th"] = "ตัวเปิดของ Colibri ต้องใช้ Python 3.10 ขึ้นไป (ตัวเครื่องยนต์เป็น C) ติดตั้งก่อน แล้วกดรีเฟรช",
        },
        ["colibri.installPython"] = new() { ["en"] = "Install Python (winget)", ["th"] = "ติดตั้ง Python (winget)" },

        ["colibri.modelsDir"] = new() { ["en"] = "Models folder", ["th"] = "ที่เก็บโมเดล" },
        ["colibri.changeDir"] = new() { ["en"] = "Change", ["th"] = "เปลี่ยน" },
        ["colibri.dirE"] = new()
        {
            ["en"] = "That drive is removable or slow — the experts are read from it every token. Pick an internal NVMe drive if you can.",
            ["th"] = "ไดรฟ์นี้เป็นแบบถอดได้หรือช้า — โมเดลต้องอ่านจากไดรฟ์นี้ทุก token เลือก NVMe ภายในเครื่องจะดีกว่า",
        },
        ["colibri.loadModels"] = new() { ["en"] = "Models this PC can run", ["th"] = "ดูโมเดลที่เครื่องนี้รันได้" },
        ["colibri.loadingModels"] = new() { ["en"] = "Measuring this PC…", ["th"] = "กำลังวัดสเปกเครื่อง…" },
        ["colibri.recommended"] = new() { ["en"] = "recommended", ["th"] = "แนะนำ" },
        ["colibri.fromRam"] = new() { ["en"] = "runs from RAM", ["th"] = "รันจาก RAM (เร็ว)" },
        ["colibri.streams"] = new() { ["en"] = "streams from the SSD (slow)", ["th"] = "อ่านจาก SSD (ช้า)" },
        ["colibri.modelLine"] = new() { ["en"] = "{0} download · RAM {1}–{2} GB", ["th"] = "ดาวน์โหลด {0} · ใช้ RAM {1}–{2} GB" },
        ["colibri.noFit"] = new() { ["en"] = "Doesn't fit: {0}", ["th"] = "รันไม่ได้: {0}" },
        ["colibri.noChatFits"] = new()
        {
            ["en"] = "No chat model fits this PC's RAM or free disk. Free some disk, or choose a bigger drive for the models.",
            ["th"] = "ไม่มีโมเดลแชทที่ RAM หรือพื้นที่ดิสก์ของเครื่องนี้พอ ลองเคลียร์ดิสก์ หรือเลือกไดรฟ์ที่ใหญ่กว่าเก็บโมเดล",
        },
        ["colibri.installModel"] = new() { ["en"] = "Download and set up", ["th"] = "ดาวน์โหลดและตั้งค่า" },
        ["colibri.confirmModel"] = new()
        {
            ["en"] = "Download {0} ({1}) to {2}?\n\nIt can take a long time; you can close CluadeX — the download goes on and resumes if stopped.\nModel licence: {3}",
            ["th"] = "ดาวน์โหลด {0} ({1}) ไปที่ {2} ไหม?\n\nอาจใช้เวลานาน ปิด CluadeX ได้ — การดาวน์โหลดยังเดินต่อ และต่อจากเดิมได้ถ้าหยุดไป\nสัญญาอนุญาตของโมเดล: {3}",
        },
        ["colibri.licenseNone"] = new() { ["en"] = "see the model's page", ["th"] = "ดูที่หน้าของโมเดล" },

        ["colibri.start"] = new() { ["en"] = "Start", ["th"] = "เปิดน้อง" },
        ["colibri.stop"] = new() { ["en"] = "Stop", ["th"] = "หยุด" },
        ["colibri.dashboard"] = new() { ["en"] = "Dashboard", ["th"] = "แดชบอร์ด" },
        ["colibri.refresh"] = new() { ["en"] = "Refresh", ["th"] = "รีเฟรช" },
        ["colibri.uninstall"] = new() { ["en"] = "Uninstall", ["th"] = "ถอนการติดตั้ง" },
        ["colibri.uninstallConfirm"] = new()
        {
            ["en"] = "Remove the Colibri engine and its settings?\n\nYes = also delete the downloaded models in {0}\nNo = keep the models (a reinstall uses them)\nCancel = do nothing",
            ["th"] = "ลบเครื่องยนต์ Colibri และการตั้งค่าไหม?\n\nใช่ = ลบโมเดลที่ดาวน์โหลดไว้ใน {0} ด้วย\nไม่ = เก็บโมเดลไว้ (ติดตั้งใหม่แล้วใช้ต่อได้)\nยกเลิก = ไม่ทำอะไร",
        },

        ["colibri.state.none"] = new() { ["en"] = "Installed — choose a model", ["th"] = "ติดตั้งแล้ว — เลือกโมเดลได้เลย" },
        ["colibri.state.stopped"] = new() { ["en"] = "Stopped · {0}", ["th"] = "หยุดอยู่ · {0}" },
        ["colibri.state.loading"] = new() { ["en"] = "Loading {0}…", ["th"] = "กำลังโหลด {0}…" },
        ["colibri.state.ready"] = new() { ["en"] = "Ready · {0}", ["th"] = "พร้อมใช้ · {0}" },
        ["colibri.state.speed"] = new() { ["en"] = "{0} tokens/s last answer", ["th"] = "คำตอบล่าสุด {0} token/วินาที" },
        ["colibri.state.download"] = new() { ["en"] = "Downloading {0} / {1} · {2}/s", ["th"] = "กำลังดาวน์โหลด {0} / {1} · {2}/วินาที" },
        ["colibri.state.setup"] = new() { ["en"] = "Setting up: {0}", ["th"] = "กำลังตั้งค่า: {0}" },
        ["colibri.state.error"] = new() { ["en"] = "Setup failed: {0}", ["th"] = "ตั้งค่าไม่สำเร็จ: {0}" },
        ["colibri.state.configured"] = new() { ["en"] = "Set up — press Start", ["th"] = "ตั้งค่าเสร็จแล้ว — กดเปิดน้อง" },

        ["colibri.useForAgents"] = new() { ["en"] = "Let sub-agents use her when they can", ["th"] = "ให้เอเจนต์ย่อยใช้น้องเมื่อทำได้" },
        ["colibri.useForAgentsDesc"] = new()
        {
            ["en"] = "Read-only sub-agents (search, read, explore) on the cheap tier run here instead of the API; with a model of 30B or more the mid tier too. "
                   + "Editing stays on the main model. A weak answer climbs to the API — never to the same local model twice.",
            ["th"] = "เอเจนต์ย่อยที่อ่านอย่างเดียว (ค้น อ่าน สำรวจ) ระดับประหยัดจะมาใช้ที่นี่แทน API ถ้าโมเดลใหญ่ 30B ขึ้นไปจะรับระดับกลางด้วย "
                   + "งานแก้ไฟล์ยังใช้โมเดลหลัก ถ้าตอบอ่อนจะส่งต่อขึ้น API — ไม่วนกลับมาโมเดลในเครื่องตัวเดิมซ้ำ",
        },
        ["colibri.license"] = new()
        {
            ["en"] = "colibri by JustVugg · Apache-2.0 · each model keeps its own licence",
            ["th"] = "colibri โดย JustVugg · Apache-2.0 · แต่ละโมเดลมีสัญญาอนุญาตของตัวเอง",
        },
        ["colibri.cudaNote"] = new()
        {
            ["en"] = "NVIDIA cards before the RTX 30 series use Vulkan (colibri's CUDA build needs compute 8.0+).",
            ["th"] = "การ์ด NVIDIA ที่เก่ากว่า RTX 30 จะใช้ Vulkan แทน (CUDA ของ colibri ต้องการ compute 8.0 ขึ้นไป)",
        },

        ["colibri.err.checksum"] = new() { ["en"] = "The download did not match the release checksum — nothing was installed.", ["th"] = "ไฟล์ที่ดาวน์โหลดไม่ตรงกับ checksum ของรุ่นนี้ — ยังไม่ได้ติดตั้งอะไร" },
        ["colibri.err.noAsset"] = new() { ["en"] = "The release has no Windows build.", ["th"] = "รุ่นนี้ไม่มีไฟล์สำหรับ Windows" },
        ["colibri.err.tooBig"] = new() { ["en"] = "The release file is far bigger than expected — stopped.", ["th"] = "ไฟล์รุ่นนี้ใหญ่ผิดปกติ — หยุดไว้ก่อน" },
        ["colibri.err.noPython"] = new() { ["en"] = "Python 3.10 or newer was not found.", ["th"] = "ไม่พบ Python 3.10 ขึ้นไปในเครื่อง" },
        ["colibri.err.notInstalled"] = new() { ["en"] = "Colibri is not installed.", ["th"] = "ยังไม่ได้ติดตั้ง Colibri" },
        ["colibri.err.start"] = new() { ["en"] = "Could not start the setup.", ["th"] = "เริ่มการตั้งค่าไม่ได้" },
        ["colibri.err.network"] = new() { ["en"] = "Could not reach GitHub: {0}", ["th"] = "ติดต่อ GitHub ไม่ได้: {0}" },
        ["colibri.err.generic"] = new() { ["en"] = "Colibri: {0}", ["th"] = "Colibri: {0}" },

        // ── Billing ▸ savings: the on-PC chart ──
        ["usage.localTitle"] = new() { ["en"] = "Saved by AI on this PC", ["th"] = "AI ในเครื่องช่วยประหยัด" },
        ["usage.localDesc"] = new()
        {
            ["en"] = "Sub-agent work a local model (Colibri, llama.cpp, Ollama, LM Studio…) did instead of the API, priced at the run's own model. Redone work is subtracted.",
            ["th"] = "งานเอเจนต์ย่อยที่โมเดลในเครื่อง (Colibri, llama.cpp, Ollama, LM Studio…) ทำแทน API คิดราคาตามโมเดลหลักของงานนั้น หักงานที่ต้องทำซ้ำแล้ว",
        },
        ["usage.localTotals"] = new() { ["en"] = "{0} saved · {1} requests · {2} tokens on this PC", ["th"] = "ประหยัด {0} · {1} คำขอ · {2} token ที่เครื่องนี้ทำเอง" },
        ["usage.localEmpty"] = new()
        {
            ["en"] = "Nothing ran on this PC in place of the API in these dates.",
            ["th"] = "ช่วงนี้ยังไม่มีงานที่ AI ในเครื่องทำแทน API",
        },
        ["usage.localTip"] = new() { ["en"] = "{0}\nsaved {1} · {2} requests · {3} tokens\nrunning total {4}", ["th"] = "{0}\nประหยัด {1} · {2} คำขอ · {3} token\nรวมสะสม {4}" },
        ["usage.byLocal"] = new() { ["en"] = "of which on this PC", ["th"] = "ในนั้นทำในเครื่อง" },
    };
}
