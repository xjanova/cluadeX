namespace CluadeX.Services.Localization;

/// <summary>Localized strings for "แจ้งปัญหา" (the report window), the Debug Log page's log-file controls, and the update
/// window's release notes. Every key needs both "en" and "th".</summary>
internal static class BugReportKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ── the window ──
        ["bug.title"] = new() { ["en"] = "Report a problem", ["th"] = "แจ้งปัญหา" },
        ["bug.sub"] = new() { ["en"] = "Tell us what happened. The Xman Studio team gets your words and whatever you tick below.", ["th"] = "เล่าว่าเกิดอะไรขึ้น ทีม Xman Studio จะได้รับข้อความของคุณพร้อมข้อมูลที่ติ๊กแนบไว้ด้านล่าง" },
        ["bug.category"] = new() { ["en"] = "What kind of problem", ["th"] = "ประเภทปัญหา" },
        ["bug.cat.bug"] = new() { ["en"] = "Something works wrong", ["th"] = "ทำงานผิดพลาด" },
        ["bug.cat.crash"] = new() { ["en"] = "The app closed or froze", ["th"] = "แอปปิดเองหรือค้าง" },
        ["bug.cat.performance"] = new() { ["en"] = "Slow or heavy", ["th"] = "ช้าหรือกินเครื่อง" },
        ["bug.cat.connection"] = new() { ["en"] = "Cannot connect / sign in", ["th"] = "เชื่อมต่อหรือเข้าสู่ระบบไม่ได้" },
        ["bug.cat.feature_request"] = new() { ["en"] = "An idea or a wish", ["th"] = "ข้อเสนอแนะ / อยากให้มี" },
        ["bug.cat.other"] = new() { ["en"] = "Something else", ["th"] = "อื่นๆ" },
        ["bug.message"] = new() { ["en"] = "What happened", ["th"] = "เกิดอะไรขึ้น" },
        ["bug.message.hint"] = new() { ["en"] = "What you did, what you expected, what happened instead…", ["th"] = "ทำอะไรอยู่ คาดว่าจะเกิดอะไร แต่เกิดอะไรขึ้นแทน…" },
        ["bug.email"] = new() { ["en"] = "E-mail for a reply (optional)", ["th"] = "อีเมลสำหรับติดต่อกลับ (ไม่บังคับ)" },
        ["bug.email.invalid"] = new() { ["en"] = "That e-mail does not look right — fix it or leave it empty.", ["th"] = "อีเมลดูไม่ถูกต้อง แก้ให้ถูกหรือเว้นว่างไว้" },

        ["bug.attach"] = new() { ["en"] = "Attach", ["th"] = "แนบไปด้วย" },
        ["bug.attach.log"] = new() { ["en"] = "This session's log ({0} lines)", ["th"] = "ล็อกของรอบนี้ ({0} บรรทัด)" },
        ["bug.attach.system"] = new() { ["en"] = "Version and PC details (Windows, CPU, memory, GPU, screen)", ["th"] = "เวอร์ชันและข้อมูลเครื่อง (Windows, CPU, หน่วยความจำ, การ์ดจอ, หน้าจอ)" },
        ["bug.attach.settings"] = new() { ["en"] = "Settings summary (switches and numbers — never a key)", ["th"] = "สรุปการตั้งค่า (เปิด/ปิดและตัวเลข — ไม่มีคีย์)" },
        ["bug.attach.errors"] = new() { ["en"] = "Recent errors ({0})", ["th"] = "ข้อผิดพลาดล่าสุด ({0} รายการ)" },
        ["bug.attach.screenshot"] = new() { ["en"] = "A screenshot of the CluadeX window", ["th"] = "ภาพหน้าจอของหน้าต่าง CluadeX" },
        ["bug.attach.chat"] = new() { ["en"] = "The open chat (only if the problem is in it)", ["th"] = "แชทที่เปิดอยู่ (แนบเฉพาะถ้าปัญหาอยู่ในแชท)" },
        ["bug.attach.chat.none"] = new() { ["en"] = "No chat is open", ["th"] = "ไม่มีแชทที่เปิดอยู่" },
        ["bug.attach.license"] = new() { ["en"] = "Link it to my license ({0}) so the team knows who to answer", ["th"] = "ผูกกับใบอนุญาตของฉัน ({0}) ทีมงานจะรู้ว่าต้องตอบใคร" },
        ["bug.privacy"] = new() { ["en"] = "Before anything is sent, API keys, tokens, license keys, passwords, e-mail addresses and your user name in paths are removed.", ["th"] = "ก่อนส่ง แอปจะลบคีย์ API โทเคน คีย์ใบอนุญาต รหัสผ่าน อีเมล และชื่อผู้ใช้ที่อยู่ในพาธออกให้ทั้งหมด" },

        ["bug.preview"] = new() { ["en"] = "See what will be sent", ["th"] = "ดูสิ่งที่จะส่ง" },
        ["bug.preview.hide"] = new() { ["en"] = "Back", ["th"] = "กลับ" },
        ["bug.preview.title"] = new() { ["en"] = "Exactly what goes to the team (after the cleanup)", ["th"] = "ข้อมูลที่จะส่งถึงทีมงานจริง (หลังลบข้อมูลส่วนตัวแล้ว)" },
        ["bug.preview.screenshot"] = new() { ["en"] = "+ a screenshot ({0} KB)", ["th"] = "+ ภาพหน้าจอ ({0} KB)" },
        ["bug.preparing"] = new() { ["en"] = "Gathering the details…", ["th"] = "กำลังรวบรวมข้อมูล…" },
        ["bug.send"] = new() { ["en"] = "Send report", ["th"] = "ส่งรายงาน" },
        ["bug.sending"] = new() { ["en"] = "Sending…", ["th"] = "กำลังส่ง…" },
        ["bug.cancel"] = new() { ["en"] = "Cancel", ["th"] = "ยกเลิก" },
        ["bug.close"] = new() { ["en"] = "Close", ["th"] = "ปิด" },
        ["bug.need.message"] = new() { ["en"] = "Write a few words about the problem first.", ["th"] = "เขียนเล่าปัญหาสั้นๆ ก่อนส่ง" },

        // ── the answer ──
        ["bug.done.title"] = new() { ["en"] = "Report received", ["th"] = "ได้รับรายงานแล้ว" },
        ["bug.queued.title"] = new() { ["en"] = "Kept to send later", ["th"] = "เก็บไว้ส่งภายหลัง" },
        ["bug.failed.title"] = new() { ["en"] = "Not sent", ["th"] = "ยังส่งไม่ได้" },
        ["bug.ticket"] = new() { ["en"] = "Report number", ["th"] = "เลขที่รายงาน" },
        ["bug.copy"] = new() { ["en"] = "Copy", ["th"] = "คัดลอก" },
        ["bug.copied"] = new() { ["en"] = "Copied", ["th"] = "คัดลอกแล้ว" },
        ["bug.back"] = new() { ["en"] = "Back to the report", ["th"] = "กลับไปแก้รายงาน" },
        ["bug.result.sent"] = new() { ["en"] = "Thank you — the team has it. Quote this number if you write to us about it.", ["th"] = "ขอบคุณที่แจ้งเข้ามา ทีมงานได้รับแล้ว ถ้าติดต่อเรื่องนี้ให้แจ้งเลขนี้" },
        ["bug.result.duplicate"] = new() { ["en"] = "The team already had this report — it is counted once.", ["th"] = "ทีมงานได้รับรายงานนี้ไปแล้ว ส่งซ้ำไม่นับเพิ่ม" },
        ["bug.result.queued"] = new() { ["en"] = "It could not go right now. It is kept and will be sent by itself when the app can reach the server.", ["th"] = "ตอนนี้ยังส่งไม่ได้ เก็บรายงานไว้แล้ว แอปจะส่งให้เองเมื่อเชื่อมต่อเซิร์ฟเวอร์ได้" },
        ["bug.result.offline"] = new() { ["en"] = "No internet connection. The report is kept and will be sent by itself when you are back online.", ["th"] = "ต่ออินเทอร์เน็ตไม่ได้ เก็บรายงานไว้แล้ว จะส่งให้เองเมื่อกลับมาออนไลน์" },
        ["bug.result.timeout"] = new() { ["en"] = "The server took too long. The report is kept and will be sent again by itself.", ["th"] = "เซิร์ฟเวอร์ตอบช้าเกินไป เก็บรายงานไว้แล้ว จะลองส่งใหม่ให้เอง" },
        ["bug.result.rateLimited"] = new() { ["en"] = "Too many reports in a short time. This one is kept and will be sent a little later.", ["th"] = "ส่งถี่เกินไป เก็บรายงานนี้ไว้แล้ว จะส่งให้อีกครั้งในภายหลัง" },
        ["bug.result.tooLarge"] = new() { ["en"] = "The report is too big. Untick the screenshot or the log and send again.", ["th"] = "รายงานใหญ่เกินไป เอาภาพหน้าจอหรือล็อกออกแล้วส่งใหม่" },
        ["bug.result.badScreenshot"] = new() { ["en"] = "The screenshot could not be used. Untick it and send again.", ["th"] = "ภาพหน้าจอใช้ไม่ได้ เอาออกแล้วส่งใหม่" },
        ["bug.result.invalid"] = new() { ["en"] = "Something in the report is missing. Check the description and the e-mail, then send again.", ["th"] = "ข้อมูลในรายงานไม่ครบ ตรวจข้อความและอีเมลแล้วส่งใหม่" },
        ["bug.result.server"] = new() { ["en"] = "The server could not take the report. Please try again later.", ["th"] = "เซิร์ฟเวอร์รับรายงานไม่ได้ ลองใหม่ภายหลัง" },
        ["bug.queue.pending"] = new() { ["en"] = "{0} earlier report(s) waiting to be sent", ["th"] = "มีรายงานก่อนหน้ารอส่ง {0} ฉบับ" },
        ["bug.queue.sentLater"] = new() { ["en"] = "Earlier reports sent: {0}", ["th"] = "ส่งรายงานที่ค้างไว้แล้ว: {0}" },
        ["bug.history"] = new() { ["en"] = "Last report: {0}", ["th"] = "รายงานล่าสุด: {0}" },

        // ── the Debug Log page ──
        ["dbglog.report"] = new() { ["en"] = "Report a problem", ["th"] = "แจ้งปัญหา" },
        ["dbglog.reportTip"] = new() { ["en"] = "Send what went wrong to the Xman Studio team, with the details you choose", ["th"] = "ส่งปัญหาให้ทีม Xman Studio พร้อมข้อมูลที่คุณเลือกแนบ" },
        ["dbglog.clearFiles"] = new() { ["en"] = "Delete log files", ["th"] = "ล้างไฟล์ล็อก" },
        ["dbglog.clearFilesTip"] = new() { ["en"] = "Delete every log file and crash log on this PC (asks first)", ["th"] = "ลบไฟล์ล็อกและล็อกแอปค้างทั้งหมดในเครื่อง (ถามก่อนลบ)" },
        ["dbglog.clearFiles.confirmTitle"] = new() { ["en"] = "Delete log files", ["th"] = "ล้างไฟล์ล็อก" },
        ["dbglog.clearFiles.confirm"] = new() { ["en"] = "Delete all {0} log file(s) ({1}) and the crash logs?\n\nThey cannot be brought back. A problem you report afterwards has only what is logged from now on.", ["th"] = "ลบไฟล์ล็อกทั้งหมด {0} ไฟล์ ({1}) และล็อกตอนแอปค้าง?\n\nลบแล้วกู้คืนไม่ได้ ถ้าจะแจ้งปัญหาหลังจากนี้ จะมีเฉพาะล็อกตั้งแต่ตอนนี้ไป" },
        ["dbglog.clearFiles.done"] = new() { ["en"] = "Deleted {0} log file(s), {1} freed", ["th"] = "ลบไฟล์ล็อกแล้ว {0} ไฟล์ คืนพื้นที่ {1}" },
        ["dbglog.clearFiles.none"] = new() { ["en"] = "There are no log files to delete", ["th"] = "ไม่มีไฟล์ล็อกให้ลบ" },
        ["dbglog.verbose"] = new() { ["en"] = "Detailed logging", ["th"] = "บันทึกละเอียด" },
        ["dbglog.verboseTip"] = new() { ["en"] = "Keeps Trace lines too and notes memory, threads and handles every 3 minutes — turn it on while chasing a problem (the log grows faster)", ["th"] = "เก็บบรรทัดระดับ Trace ด้วย และจดหน่วยความจำ เธรด แฮนเดิล ทุก 3 นาที เปิดไว้ตอนตามหาปัญหา (ไฟล์ล็อกจะโตเร็วขึ้น)" },
        ["dbglog.retention"] = new() { ["en"] = "Keep logs", ["th"] = "เก็บล็อก" },
        ["dbglog.retention.days"] = new() { ["en"] = "{0} days", ["th"] = "{0} วัน" },
        ["dbglog.retention.forever"] = new() { ["en"] = "Forever", ["th"] = "ตลอด" },
        ["dbglog.retentionTip"] = new() { ["en"] = "Older log files are deleted by themselves; all of them together stay under {0} MB", ["th"] = "ไฟล์ล็อกที่เก่ากว่านี้จะถูกลบเอง และรวมกันไม่เกิน {0} MB" },
        ["dbglog.files"] = new() { ["en"] = "{0} file(s) · {1}", ["th"] = "{0} ไฟล์ · {1}" },

        // ── the update window: the notes ──
        ["upd.lessNotes"] = new() { ["en"] = "Show less", ["th"] = "ย่อ" },
        ["upd.notesLoading"] = new() { ["en"] = "Loading the details…", ["th"] = "กำลังโหลดรายละเอียด…" },
        ["upd.notesOffline"] = new() { ["en"] = "The details could not be loaded — here is what came with the update.", ["th"] = "โหลดรายละเอียดเพิ่มไม่ได้ แสดงเท่าที่มากับตัวอัปเดต" },
    };
}
