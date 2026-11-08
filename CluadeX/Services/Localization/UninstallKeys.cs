namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the uninstall page (the same look as the setup wizard). Every key needs both "en" and
/// "th".</summary>
internal static class UninstallKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["un.titleBar"] = new() { ["en"] = "Uninstall CluadeX", ["th"] = "ถอนการติดตั้ง CluadeX" },
        ["un.step.choose"] = new() { ["en"] = "Choose", ["th"] = "เลือกสิ่งที่เก็บ" },
        ["un.step.remove"] = new() { ["en"] = "Remove", ["th"] = "ถอนการติดตั้ง" },
        ["un.step.done"] = new() { ["en"] = "Done", ["th"] = "เสร็จสิ้น" },

        // ── choose ──
        ["un.choose.title"] = new() { ["en"] = "Uninstall CluadeX", ["th"] = "ถอนการติดตั้ง CluadeX" },
        ["un.choose.sub"] = new() { ["en"] = "Tick what to keep on this PC. Everything left unticked is deleted for good — files, settings and registry entries.", ["th"] = "ติ๊กสิ่งที่อยากเก็บไว้ในเครื่อง ส่วนที่ไม่ติ๊กจะถูกลบถาวร ทั้งไฟล์ การตั้งค่า และค่าในรีจิสทรี" },
        ["un.choose.keepAll"] = new() { ["en"] = "Keep everything", ["th"] = "เก็บทั้งหมด" },
        ["un.choose.removeAll"] = new() { ["en"] = "Remove everything", ["th"] = "ลบทั้งหมด" },
        ["un.choose.measuring"] = new() { ["en"] = "measuring…", ["th"] = "กำลังวัด…" },
        ["un.choose.empty"] = new() { ["en"] = "nothing", ["th"] = "ไม่มีข้อมูล" },
        ["un.choose.always"] = new() { ["en"] = "Always removed: the program, its shortcuts, its Installed-apps row, the Chrome connection, its temporary files and its entries in BrainX.", ["th"] = "ลบเสมอ: ตัวโปรแกรม ทางลัด รายการในแอปที่ติดตั้ง การเชื่อมกับ Chrome ไฟล์ชั่วคราว และรายการที่ฝากไว้ใน BrainX" },
        ["un.choose.dataAt"] = new() { ["en"] = "Your data is in", ["th"] = "ข้อมูลของคุณอยู่ที่" },
        ["un.choose.running"] = new() { ["en"] = "CluadeX is open — it will be closed first (it saves your chats as it closes).", ["th"] = "CluadeX เปิดอยู่ จะปิดให้ก่อนเริ่ม (แชตจะถูกบันทึกตอนปิด)" },
        ["un.choose.go"] = new() { ["en"] = "Uninstall", ["th"] = "ถอนการติดตั้ง" },
        ["un.choose.goKeepAll"] = new() { ["en"] = "Uninstall and keep my data", ["th"] = "ถอนการติดตั้ง เก็บข้อมูลไว้" },

        // ── the groups ──
        ["un.g.settings"] = new() { ["en"] = "Settings and AI providers", ["th"] = "การตั้งค่าและผู้ให้บริการ AI" },
        ["un.g.settings.d"] = new() { ["en"] = "Theme, language, API keys, permissions and the other app files", ["th"] = "ธีม ภาษา คีย์ API สิทธิ์ต่างๆ และไฟล์อื่นของแอป" },
        ["un.g.chats"] = new() { ["en"] = "Chat history and projects", ["th"] = "ประวัติแชตและโปรเจกต์" },
        ["un.g.chats.d"] = new() { ["en"] = "Every chat, checkpoint, attachment and memory — your code folders are never touched", ["th"] = "แชต จุดย้อนกลับ ไฟล์แนบ และความจำทั้งหมด (โฟลเดอร์โค้ดของคุณไม่ถูกแตะ)" },
        ["un.g.models"] = new() { ["en"] = "Downloaded models and the GPU runtime", ["th"] = "โมเดลที่ดาวน์โหลดและตัวเร่งการ์ดจอ" },
        ["un.g.models.d"] = new() { ["en"] = "Kept, a reinstall does not download them again", ["th"] = "ถ้าเก็บไว้ ติดตั้งใหม่ไม่ต้องดาวน์โหลดซ้ำ" },
        ["un.g.ext"] = new() { ["en"] = "Skills, plugins, agents and MCP servers", ["th"] = "สกิล ปลั๊กอิน เอเจนต์ และ MCP" },
        ["un.g.ext.d"] = new() { ["en"] = "What you installed or wrote for CluadeX", ["th"] = "ส่วนที่คุณติดตั้งหรือเขียนเพิ่มให้ CluadeX" },
        ["un.g.studio"] = new() { ["en"] = "Studio work (UI Studio, node graphs, media)", ["th"] = "งานในสตูดิโอ (UI Studio, โหนด, สื่อ)" },
        ["un.g.studio.d"] = new() { ["en"] = "Designs, graphs and media projects made in CluadeX", ["th"] = "งานออกแบบ กราฟโหนด และโปรเจกต์สื่อที่ทำใน CluadeX" },
        ["un.g.license"] = new() { ["en"] = "License and sign-in", ["th"] = "ไลเซ่นและการเข้าสู่ระบบ" },
        ["un.g.license.d"] = new() { ["en"] = "Kept, a reinstall is registered straight away", ["th"] = "ถ้าเก็บไว้ ติดตั้งใหม่ใช้ได้ทันทีไม่ต้องลงทะเบียนซ้ำ" },
        ["un.g.logs"] = new() { ["en"] = "Logs and cache", ["th"] = "ไฟล์ log และแคช" },
        ["un.g.logs.d"] = new() { ["en"] = "Safe to delete — rebuilt when needed", ["th"] = "ลบได้ปลอดภัย สร้างใหม่เองเมื่อต้องใช้" },

        // ── folders the user chose outside the data folder ──
        ["un.c.models"] = new() { ["en"] = "Models in a folder you chose", ["th"] = "โมเดลในโฟลเดอร์ที่คุณเลือกเอง" },
        ["un.c.chats"] = new() { ["en"] = "Chats in a folder you chose", ["th"] = "แชตในโฟลเดอร์ที่คุณเลือกเอง" },
        ["un.c.logs"] = new() { ["en"] = "Cache or logs in a folder you chose", ["th"] = "แคชหรือ log ในโฟลเดอร์ที่คุณเลือกเอง" },
        ["un.c.note"] = new() { ["en"] = "Untick only if nothing else uses this folder", ["th"] = "ไม่ติ๊กเมื่อแน่ใจว่าไม่มีโปรแกรมอื่นใช้โฟลเดอร์นี้" },

        // ── confirm ──
        ["un.confirm.title"] = new() { ["en"] = "Delete for good?", ["th"] = "ลบถาวรเลยไหม" },
        ["un.confirm.body"] = new() { ["en"] = "These are deleted and cannot be brought back:", ["th"] = "รายการนี้จะถูกลบและกู้คืนไม่ได้" },
        ["un.confirm.yes"] = new() { ["en"] = "Delete and uninstall", ["th"] = "ลบและถอนการติดตั้ง" },
        ["un.confirm.no"] = new() { ["en"] = "Back to choosing", ["th"] = "กลับไปเลือก" },

        // ── progress ──
        ["un.prog.title"] = new() { ["en"] = "Uninstalling CluadeX", ["th"] = "กำลังถอนการติดตั้ง CluadeX" },
        ["un.prog.sub"] = new() { ["en"] = "Just a moment — big folders take a little longer.", ["th"] = "รอสักครู่ โฟลเดอร์ใหญ่อาจใช้เวลานานกว่านิดหน่อย" },
        ["un.prog.close"] = new() { ["en"] = "Close CluadeX", ["th"] = "ปิด CluadeX ที่เปิดอยู่" },
        ["un.prog.closed"] = new() { ["en"] = "closed", ["th"] = "ปิดแล้ว" },
        ["un.prog.notRunning"] = new() { ["en"] = "was not open", ["th"] = "ไม่ได้เปิดอยู่" },
        ["un.prog.delete"] = new() { ["en"] = "Delete: {0}", ["th"] = "ลบ{0}" },
        ["un.prog.kept"] = new() { ["en"] = "Keep: {0}", ["th"] = "เก็บ{0}" },
        ["un.prog.freed"] = new() { ["en"] = "{0} freed", ["th"] = "คืนพื้นที่ {0}" },
        ["un.prog.left"] = new() { ["en"] = "{0} files are in use and were left — reason: {1}", ["th"] = "ลบไม่ได้ {0} ไฟล์ เพราะ {1}" },
        ["un.prog.links"] = new() { ["en"] = "Clear the registry, temporary files and entries in other programs", ["th"] = "ล้างรีจิสทรี ไฟล์ชั่วคราว และรายการในโปรแกรมอื่น" },
        ["un.prog.linksDone"] = new() { ["en"] = "{0} removed", ["th"] = "ล้าง {0} รายการ" },
        ["un.prog.linksNone"] = new() { ["en"] = "nothing to remove", ["th"] = "ไม่มีให้ล้าง" },
        ["un.prog.program"] = new() { ["en"] = "Remove the program", ["th"] = "ลบตัวโปรแกรม" },
        ["un.prog.programAfter"] = new() { ["en"] = "right after this window closes", ["th"] = "ทันทีที่ปิดหน้าต่างนี้" },
        ["un.prog.programDev"] = new() { ["en"] = "not an installed copy — the program files stay", ["th"] = "ไม่ใช่ตัวที่ติดตั้ง ไฟล์โปรแกรมคงไว้" },

        // ── done ──
        ["un.done.title"] = new() { ["en"] = "CluadeX is uninstalled", ["th"] = "ถอนการติดตั้งเรียบร้อย" },
        ["un.done.body"] = new() { ["en"] = "Thank you for working with us. The program is removed the moment you close this window.", ["th"] = "ขอบคุณที่ใช้งานด้วยกันมา ตัวโปรแกรมจะถูกลบทันทีที่ปิดหน้าต่างนี้" },
        ["un.done.kept"] = new() { ["en"] = "Kept on this PC — a reinstall picks it all up again:", ["th"] = "เก็บไว้ในเครื่อง ติดตั้งใหม่เมื่อไหร่ใช้ต่อได้ทันที" },
        ["un.done.nothingKept"] = new() { ["en"] = "Nothing of CluadeX is left on this PC.", ["th"] = "ไม่มีอะไรของ CluadeX เหลือในเครื่องแล้ว" },
        ["un.done.problems"] = new() { ["en"] = "Some files could not be deleted. The list and the reasons are in the report — send it to the team if you need help.", ["th"] = "มีบางไฟล์ลบไม่ได้ รายการและสาเหตุอยู่ในรายงาน ส่งให้ทีมงานได้ถ้าต้องการความช่วยเหลือ" },
        ["un.done.openFolder"] = new() { ["en"] = "Open the kept folder", ["th"] = "เปิดโฟลเดอร์ที่เก็บไว้" },
        ["un.done.openReport"] = new() { ["en"] = "Open the report", ["th"] = "เปิดรายงาน" },
        ["un.done.retry"] = new() { ["en"] = "Try again", ["th"] = "ลองลบอีกครั้ง" },
        ["un.done.close"] = new() { ["en"] = "Close", ["th"] = "ปิด" },

        // ── errors ──
        ["un.err.inUse"] = new() { ["en"] = "another program is using them", ["th"] = "มีโปรแกรมอื่นใช้อยู่" },
        ["un.err.denied"] = new() { ["en"] = "Windows denied access", ["th"] = "Windows ไม่อนุญาตให้เข้าถึง" },
        ["un.err.other"] = new() { ["en"] = "{0}", ["th"] = "{0}" },
        ["un.err.closeFailed"] = new() { ["en"] = "CluadeX did not close — close it yourself, then press Try again.", ["th"] = "ปิด CluadeX ไม่ได้ ปิดเองแล้วกดลองอีกครั้ง" },
        ["un.err.handoff"] = new() { ["en"] = "The program remover did not start: {0}. Use Installed apps in Windows Settings once more.", ["th"] = "เริ่มตัวลบโปรแกรมไม่ได้: {0} ลองกดถอนการติดตั้งใน Windows อีกครั้ง" },
    };
}
