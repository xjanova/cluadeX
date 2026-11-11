namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the Telegram bridge (Services/Remote): what the bot says on the phone, and its panel on
/// the Routines page. Every key has "en" and "th".</summary>
internal static class TelegramKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ─── the bot, on the phone ───
        ["tg.help"] = new()
        {
            ["en"] = "CluadeX on your phone:\n• send a message — it goes into the Telegram chat in CluadeX, the reply comes back\n/new — the next message starts a new chat\n/projects, /use N — which project that chat works in\n/status — what runs now and waits for you\n/routines, /run NAME — the scheduled tasks\n/stop — stop the work this bot started\nPermission cards come here with Allow / Deny.",
            ["th"] = "CluadeX บนมือถือ:\n• พิมพ์ข้อความ — จะเข้าแชท Telegram ใน CluadeX แล้วส่งคำตอบกลับมา\n/new — ข้อความถัดไปเริ่มแชทใหม่\n/projects, /use N — เลือกโปรเจกต์ที่แชทนั้นทำงาน\n/status — ตอนนี้มีงานอะไรรันอยู่ และอะไรรอคุณ\n/routines, /run ชื่อ — งานตั้งเวลา\n/stop — หยุดงานที่สั่งจากบอทนี้\nการ์ดขออนุญาตจะส่งมาที่นี่ พร้อมปุ่มอนุญาต / ไม่อนุญาต",
        },
        ["tg.paired"] = new() { ["en"] = "✅ Paired with CluadeX.", ["th"] = "✅ จับคู่กับ CluadeX แล้ว" },
        ["tg.pairClosed"] = new() { ["en"] = "Pairing is not open. On the PC: Routines page → Telegram → Pair, then send /pair and the code.", ["th"] = "ยังไม่ได้เปิดการจับคู่ ที่คอม: หน้างานตั้งเวลา → Telegram → จับคู่ แล้วส่ง /pair ตามด้วยรหัส" },
        ["tg.pairWrong"] = new() { ["en"] = "That code is not right — {0} tries left.", ["th"] = "รหัสไม่ถูก — เหลืออีก {0} ครั้ง" },
        ["tg.pairLocked"] = new() { ["en"] = "Too many wrong codes: pairing is closed. Open it again on the PC.", ["th"] = "ใส่รหัสผิดหลายครั้ง ปิดการจับคู่แล้ว เปิดใหม่ที่คอม" },
        ["tg.stale"] = new() { ["en"] = "This arrived while CluadeX was closed, so it was not done. Send it again if you still want it.", ["th"] = "ข้อความนี้มาตอนปิด CluadeX อยู่ เลยไม่ได้ทำ ถ้ายังต้องการให้ส่งใหม่อีกครั้ง" },
        ["tg.unknown"] = new() { ["en"] = "Not a command I know — /help lists them.", ["th"] = "ไม่รู้จักคำสั่งนี้ — พิมพ์ /help ดูรายการ" },
        ["tg.noApp"] = new() { ["en"] = "CluadeX is not ready for that yet — try again in a moment.", ["th"] = "CluadeX ยังไม่พร้อม ลองใหม่อีกสักครู่" },
        ["tg.busy"] = new() { ["en"] = "Still working on your last message — wait for its reply, or /stop.", ["th"] = "ยังทำข้อความก่อนหน้าอยู่ — รอคำตอบก่อน หรือพิมพ์ /stop" },
        ["tg.noReply"] = new() { ["en"] = "(the turn ended without a reply)", ["th"] = "(รอบนี้จบโดยไม่มีคำตอบ)" },
        ["tg.cut"] = new() { ["en"] = "(cut here — the rest is in CluadeX)", ["th"] = "(ตัดตรงนี้ — ที่เหลืออยู่ใน CluadeX)" },
        ["tg.newChat"] = new() { ["en"] = "The next message starts a new chat.", ["th"] = "ข้อความถัดไปจะเริ่มแชทใหม่" },
        ["tg.stopped"] = new() { ["en"] = "⏹ Stopped.", ["th"] = "⏹ หยุดแล้ว" },
        ["tg.nothingToStop"] = new() { ["en"] = "Nothing from this bot is running.", ["th"] = "ไม่มีงานจากบอทนี้ที่กำลังรัน" },
        ["tg.noProjects"] = new() { ["en"] = "No recent projects in CluadeX.", ["th"] = "ยังไม่มีโปรเจกต์ที่เปิดล่าสุดใน CluadeX" },
        ["tg.projects"] = new() { ["en"] = "Recent projects — /use N to work in one:", ["th"] = "โปรเจกต์ล่าสุด — พิมพ์ /use เลข เพื่อเลือก:" },
        ["tg.useHow"] = new() { ["en"] = "/use and a number from /projects, e.g. /use 2", ["th"] = "พิมพ์ /use ตามด้วยเลขจาก /projects เช่น /use 2" },
        ["tg.useOk"] = new() { ["en"] = "Working in {0} — the next message starts a new chat there.", ["th"] = "ทำงานใน {0} — ข้อความถัดไปจะเริ่มแชทใหม่ที่นั่น" },
        ["tg.noRoutines"] = new() { ["en"] = "No scheduled tasks yet.", ["th"] = "ยังไม่มีงานตั้งเวลา" },
        ["tg.routines"] = new() { ["en"] = "Scheduled tasks (• on, ◦ off) — /run NAME starts one:", ["th"] = "งานตั้งเวลา (• เปิด, ◦ ปิด) — พิมพ์ /run ชื่อ เพื่อรันเลย:" },
        ["tg.runHow"] = new() { ["en"] = "/run and the task's name (or the code in [ ] from /routines).", ["th"] = "พิมพ์ /run ตามด้วยชื่องาน (หรือรหัสใน [ ] จาก /routines)" },
        ["tg.runNone"] = new() { ["en"] = "No scheduled task matches “{0}”.", ["th"] = "ไม่พบงานที่ตรงกับ “{0}”" },
        ["tg.runOk"] = new() { ["en"] = "▶ Started “{0}” — you get its summary here when it ends.", ["th"] = "▶ เริ่ม “{0}” แล้ว — จบเมื่อไหร่จะส่งสรุปมาที่นี่" },
        ["tg.runBusy"] = new() { ["en"] = "“{0}” is running already.", ["th"] = "“{0}” กำลังรันอยู่แล้ว" },
        ["tg.routineDone"] = new() { ["en"] = "✅ “{0}” finished: {1}", ["th"] = "✅ “{0}” เสร็จแล้ว: {1}" },
        ["tg.routineFailed"] = new() { ["en"] = "❌ “{0}” did not finish: {1}", ["th"] = "❌ “{0}” ไม่สำเร็จ: {1}" },
        ["tg.card"] = new() { ["en"] = "🔐 {0} asks:\n{1}", ["th"] = "🔐 {0} ขออนุญาต:\n{1}" },
        ["tg.cardPcOnly"] = new() { ["en"] = "This one needs a closer look: answer it on the PC.", ["th"] = "อันนี้ต้องดูให้ละเอียด: ตอบที่คอมเท่านั้น" },
        ["tg.allow"] = new() { ["en"] = "✅ Allow", ["th"] = "✅ อนุญาต" },
        ["tg.deny"] = new() { ["en"] = "❌ Deny", ["th"] = "❌ ไม่อนุญาต" },
        ["tg.cardAllowed"] = new() { ["en"] = "✅ Allowed from Telegram", ["th"] = "✅ อนุญาตจาก Telegram แล้ว" },
        ["tg.cardDenied"] = new() { ["en"] = "❌ Denied from Telegram", ["th"] = "❌ ไม่อนุญาต (จาก Telegram)" },
        ["tg.cardGone"] = new() { ["en"] = "This card is not waiting any more.", ["th"] = "การ์ดนี้ไม่ได้รอแล้ว" },
        ["tg.cardDone"] = new() { ["en"] = "Answered in CluadeX: {0}", ["th"] = "ตอบใน CluadeX แล้ว: {0}" },
        ["tg.outcome.allowed"] = new() { ["en"] = "allowed", ["th"] = "อนุญาต" },
        ["tg.outcome.denied"] = new() { ["en"] = "denied", ["th"] = "ไม่อนุญาต" },
        ["tg.outcome.cancelled"] = new() { ["en"] = "the work stopped", ["th"] = "งานหยุดไปแล้ว" },
        ["tg.status.idle"] = new() { ["en"] = "Nothing is running.", ["th"] = "ตอนนี้ไม่มีงานรันอยู่" },
        ["tg.status.running"] = new() { ["en"] = "Running now:", ["th"] = "กำลังรัน:" },
        ["tg.status.waiting"] = new() { ["en"] = "{0} card(s) wait for you.", ["th"] = "มีการ์ด {0} ใบรอคุณตอบ" },
        ["tg.chat.opened"] = new() { ["en"] = "📱 Messages from your phone (Telegram) come into this chat.", ["th"] = "📱 แชทนี้รับข้อความจากมือถือ (Telegram)" },

        // ─── what goes wrong ───
        ["tg.err.tokenShape"] = new() { ["en"] = "That does not look like a bot token (123456789:AA…). BotFather gives it when you make the bot.", ["th"] = "รูปแบบไม่ใช่ token ของบอท (123456789:AA…) — ได้จาก BotFather ตอนสร้างบอท" },
        ["tg.err.tokenWrong"] = new() { ["en"] = "Telegram does not know that token — it was mistyped or revoked. Copy it again from BotFather.", ["th"] = "Telegram ไม่รู้จัก token นี้ — อาจพิมพ์ผิดหรือถูกยกเลิก คัดลอกใหม่จาก BotFather" },
        ["tg.err.network"] = new() { ["en"] = "Telegram could not be reached — check the internet and try again.", ["th"] = "ติดต่อ Telegram ไม่ได้ — ตรวจอินเทอร์เน็ตแล้วลองใหม่" },
        ["tg.err.conflict"] = new() { ["en"] = "Another program reads this bot (another CluadeX?) — close it, or use a bot of its own.", ["th"] = "มีโปรแกรมอื่นใช้บอทนี้อยู่ (CluadeX อีกเครื่อง?) — ปิดอันนั้น หรือใช้บอทแยกกัน" },

        // ─── the panel on the Routines page ───
        ["tg.page.title"] = new() { ["en"] = "📱 Telegram — CluadeX on your phone", ["th"] = "📱 Telegram — สั่ง CluadeX จากมือถือ" },
        ["tg.page.intro"] = new() { ["en"] = "Send work from your phone, get each routine's summary, answer permission cards with a tap. Make a bot: in Telegram open @BotFather, send /newbot, then paste the token it gives here.", ["th"] = "สั่งงานจากมือถือ รับสรุปของงานตั้งเวลา และกดอนุญาตการ์ดได้จากโทรศัพท์ วิธีสร้างบอท: ใน Telegram เปิด @BotFather พิมพ์ /newbot แล้วนำ token ที่ได้มาวางที่นี่" },
        ["tg.page.token"] = new() { ["en"] = "Bot token (kept encrypted on this PC)", ["th"] = "token ของบอท (เก็บแบบเข้ารหัสในเครื่องนี้)" },
        ["tg.page.connect"] = new() { ["en"] = "Connect", ["th"] = "เชื่อมต่อ" },
        ["tg.page.connecting"] = new() { ["en"] = "Checking with Telegram…", ["th"] = "กำลังตรวจกับ Telegram…" },
        ["tg.page.disconnect"] = new() { ["en"] = "Disconnect", ["th"] = "ยกเลิกการเชื่อมต่อ" },
        ["tg.page.connected"] = new() { ["en"] = "Connected to @{0}", ["th"] = "เชื่อมต่อกับ @{0} แล้ว" },
        ["tg.page.pairedWith"] = new() { ["en"] = "Paired with {0}", ["th"] = "จับคู่กับ {0} แล้ว" },
        ["tg.page.notPaired"] = new() { ["en"] = "Not paired with a phone yet.", ["th"] = "ยังไม่ได้จับคู่กับมือถือ" },
        ["tg.page.pair"] = new() { ["en"] = "Pair a phone", ["th"] = "จับคู่มือถือ" },
        ["tg.page.unpair"] = new() { ["en"] = "Unpair", ["th"] = "ยกเลิกการจับคู่" },
        ["tg.page.pairCode"] = new() { ["en"] = "Send this to @{0} in Telegram within 10 minutes:", ["th"] = "ส่งข้อความนี้ถึง @{0} ใน Telegram ภายใน 10 นาที:" },
        ["tg.page.notifyRoutines"] = new() { ["en"] = "Send me each routine's summary when it ends", ["th"] = "ส่งสรุปมาให้ทุกครั้งที่งานตั้งเวลาเสร็จ" },
        ["tg.page.allCards"] = new() { ["en"] = "Send every chat's permission cards too (not only the Telegram chat's and the routines')", ["th"] = "ส่งการ์ดขออนุญาตของทุกแชทด้วย (ไม่ใช่แค่แชท Telegram และงานตั้งเวลา)" },
        ["tg.page.disconnectConfirm"] = new() { ["en"] = "Disconnect the bot? The token is removed from this PC; pair again after connecting.", ["th"] = "ยกเลิกการเชื่อมต่อบอทไหม? token จะถูกลบจากเครื่องนี้ ต้องจับคู่ใหม่หลังเชื่อมต่ออีกครั้ง" },
    };
}
