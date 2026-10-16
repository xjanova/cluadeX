namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the chat page (ChatView / ChatViewModel). Every key needs both "en" and "th".</summary>
internal static class ChatKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ─── Status line ───
        ["chat.status.ready"] = new() { ["en"] = "Ready", ["th"] = "พร้อม" },
        ["chat.status.cancelled"] = new() { ["en"] = "Generation cancelled.", ["th"] = "หยุดการสร้างคำตอบแล้ว" },
        ["chat.status.error"] = new() { ["en"] = "The reply failed — see the message above.", ["th"] = "สร้างคำตอบไม่สำเร็จ — ดูข้อความด้านบน" },
        ["chat.status.cleared"] = new() { ["en"] = "Chat cleared.", ["th"] = "ล้างแชทแล้ว" },
        ["agentmap.main"] = new() { ["en"] = "Main agent", ["th"] = "เอเจนต์หลัก" },
        ["agentmap.starting"] = new() { ["en"] = "starting…", ["th"] = "กำลังเริ่ม…" },
        ["agentmap.stopped"] = new() { ["en"] = "stopped", ["th"] = "ถูกหยุด" },
        ["agentmap.headerRunning"] = new() { ["en"] = "Agent map · {0} sub-agents · {1} working · {2} done", ["th"] = "แผนผังเอเจนต์ · {0} ตัว · ทำงานอยู่ {1} · เสร็จ {2}" },
        ["agentmap.headerDone"] = new() { ["en"] = "Agent map · {0} sub-agents · {1} done", ["th"] = "แผนผังเอเจนต์ · {0} ตัว · เสร็จ {1}" },
        ["agentmap.hide"] = new() { ["en"] = "Hide", ["th"] = "ซ่อน" },
        ["agentmap.show"] = new() { ["en"] = "Show map", ["th"] = "ดูแผนผัง" },
        ["chat.cleanup.done"] = new() { ["en"] = "Cleaned up this chat: {0} background process(es) stopped{1}, {2} screenshot(s) deleted.", ["th"] = "เก็บกวาดแชทแล้ว: หยุดโปรเซสเบื้องหลัง {0} ตัว{1} ลบภาพหน้าจอ {2} ภาพ" },
        ["chat.cleanup.previewClosed"] = new() { ["en"] = ", preview closed", ["th"] = " ปิดหน้าพรีวิว" },
        ["chat.status.deleted"] = new() { ["en"] = "Chat deleted", ["th"] = "ลบแชทแล้ว" },
        ["chat.status.copied"] = new() { ["en"] = "Copied!", ["th"] = "คัดลอกแล้ว!" },
        ["chat.status.working"] = new() { ["en"] = "Working...", ["th"] = "กำลังทำงาน..." },
        ["chat.status.workingSecs"] = new() { ["en"] = "Working... ({0}s)", ["th"] = "กำลังทำงาน... ({0} วิ)" },
        ["chat.status.generating"] = new() { ["en"] = "Generating...", ["th"] = "กำลังเขียนคำตอบ..." },
        ["chat.status.generatingCount"] = new() { ["en"] = "Generating · {0} tokens", ["th"] = "กำลังเขียนคำตอบ · {0} โทเค็น" },
        ["chat.status.generatingRate"] = new() { ["en"] = "Generating · {0} tokens · {1:F0} tok/s", ["th"] = "กำลังเขียนคำตอบ · {0} โทเค็น · {1:F0} โทเค็น/วิ" },
        ["chat.status.generatingCountTime"] = new() { ["en"] = "Generating · {0} tokens · {1}", ["th"] = "กำลังเขียนคำตอบ · {0} โทเค็น · {1}" },
        ["chat.status.generatingRateTime"] = new() { ["en"] = "Generating · {0} tokens · {1:F0} tok/s · {2}", ["th"] = "กำลังเขียนคำตอบ · {0} โทเค็น · {1:F0} โทเค็น/วิ · {2}" },
        ["chat.status.agentDone"] = new() { ["en"] = "Agent loop finished", ["th"] = "เอเจนต์ทำงานจบแล้ว" },
        ["chat.status.noReply"] = new() { ["en"] = "No reply generated", ["th"] = "ไม่ได้รับคำตอบ" },
        ["chat.status.project"] = new() { ["en"] = "Project: {0}", ["th"] = "โปรเจกต์: {0}" },
        ["chat.status.restored"] = new() { ["en"] = "Restored chat: {0}", ["th"] = "เปิดแชทล่าสุด: {0}" },
        ["chat.status.loaded"] = new() { ["en"] = "Opened: {0} ({1} messages)", ["th"] = "เปิดแชท: {0} ({1} ข้อความ)" },
        ["chat.status.loadedEmpty"] = new() { ["en"] = "Empty chat: {0}", ["th"] = "แชทว่าง: {0}" },
        ["chat.status.sessionNotFound"] = new() { ["en"] = "Chat not found", ["th"] = "ไม่พบแชทนี้" },
        ["chat.status.sessionLoadFailed"] = new() { ["en"] = "Couldn't load this chat from the history file — it was left untouched. Try again in a moment.", ["th"] = "โหลดแชทนี้จากไฟล์ประวัติไม่ได้ — ข้อมูลเดิมไม่ถูกแตะต้อง ลองใหม่อีกครั้งในอีกสักครู่" },
        ["chat.status.connecting"] = new() { ["en"] = "Connecting to {0}…", ["th"] = "กำลังเชื่อมต่อ {0}…" },
        ["chat.status.noModel"] = new() { ["en"] = "No model loaded", ["th"] = "ยังไม่ได้โหลดโมเดล" },
        ["chat.status.providerNotReady"] = new() { ["en"] = "{0} isn't ready", ["th"] = "{0} ยังไม่พร้อม" },
        ["chat.provider.initFailed"] = new() { ["en"] = "Couldn't start {0} — details are in the Debug Log.", ["th"] = "เริ่ม {0} ไม่สำเร็จ — ดูรายละเอียดที่หน้า Debug Log" },

        // ─── Per-turn stats ───
        ["chat.stats.turn"] = new() { ["en"] = "{0:N0} tokens · {1:F1}s · {2:F1} tok/s", ["th"] = "{0:N0} โทเค็น · {1:F1} วิ · {2:F1} โทเค็น/วิ" },
        ["chat.stats.ready"] = new() { ["en"] = "Ready · {0:F1}s", ["th"] = "พร้อม · {0:F1} วิ" },
        ["chat.stats.tools"] = new() { ["en"] = " · {0} tools", ["th"] = " · ใช้เครื่องมือ {0} ครั้ง" },
        ["chat.stats.steps"] = new() { ["en"] = " · {0} steps", ["th"] = " · {0} ขั้น" },
        ["chat.stats.tokens"] = new() { ["en"] = " · {0:N0} tokens", ["th"] = " · {0:N0} โทเค็น" },

        // ─── Waiting stages ───
        ["chat.stage.building"] = new() { ["en"] = "Building context", ["th"] = "กำลังเตรียม context" },
        ["chat.stage.sending"] = new() { ["en"] = "Sending request", ["th"] = "กำลังส่งคำขอ" },
        ["chat.stage.waiting"] = new() { ["en"] = "Waiting for response", ["th"] = "รอคำตอบ" },
        ["chat.stage.stillWaiting"] = new() { ["en"] = "Still waiting", ["th"] = "ยังรออยู่" },
        ["chat.stage.slow"] = new() { ["en"] = "Taking longer than usual", ["th"] = "ใช้เวลานานกว่าปกติ" },
        ["chat.stage.localPrefill"] = new() { ["en"] = "Model is reading the prompt — the first local turn can take 1-2 minutes", ["th"] = "โมเดลกำลังอ่าน prompt — รอบแรกของโมเดลในเครื่องอาจใช้เวลา 1-2 นาที" },
        ["chat.stage.slowApi"] = new() { ["en"] = "The AI service is slow to answer — still waiting", ["th"] = "บริการ AI ตอบช้า — ยังรออยู่" },

        // ─── Errors and retry ───
        ["chat.err.timeout"] = new() { ["en"] = "The AI service took too long to answer.", ["th"] = "บริการ AI ตอบช้าเกินไป" },
        ["chat.err.rateLimited"] = new() { ["en"] = "Rate limit reached — the AI service asked us to slow down.", ["th"] = "ถึงขีดจำกัดการใช้งานชั่วคราว — บริการ AI ขอให้ส่งช้าลง" },
        ["chat.err.serverDown"] = new() { ["en"] = "The AI service had a problem on its side.", ["th"] = "บริการ AI มีปัญหาฝั่งเซิร์ฟเวอร์" },
        ["chat.err.rejected"] = new() { ["en"] = "The AI service rejected the request.", ["th"] = "บริการ AI ปฏิเสธคำขอนี้" },
        ["chat.err.network"] = new() { ["en"] = "Couldn't reach the AI service.", ["th"] = "เชื่อมต่อบริการ AI ไม่ได้" },
        ["chat.err.generic"] = new() { ["en"] = "Something went wrong while generating the reply — details are in the Debug Log.", ["th"] = "เกิดข้อผิดพลาดระหว่างสร้างคำตอบ — ดูรายละเอียดได้ที่หน้า Debug Log" },
        ["chat.err.agentLoop"] = new() { ["en"] = "The agent stopped with an error — details are in the Debug Log.", ["th"] = "เอเจนต์หยุดทำงานเพราะเกิดข้อผิดพลาด — ดูรายละเอียดที่หน้า Debug Log" },
        ["chat.err.emptyReply"] = new() { ["en"] = "(The model didn't produce a reply. Try again, reload the model, or pick another one.)", ["th"] = "(โมเดลไม่ได้ตอบกลับ ลองส่งใหม่ โหลดโมเดลใหม่ หรือเลือกโมเดลอื่น)" },
        ["chat.err.noModel"] = new() { ["en"] = "⚠ **No model loaded.** Load a GGUF model in the **Models** tab, or pick one in the box at the bottom of the chat.", ["th"] = "⚠ **ยังไม่ได้โหลดโมเดล** โหลดโมเดล GGUF ได้ที่แท็บ **โมเดล** หรือเลือกในช่องด้านล่างของแชท" },
        ["chat.err.providerNotReady"] = new() { ["en"] = "⚠ **{0} isn't ready.** {1}\nCheck the API key in **Settings ▸ AI Provider**.", ["th"] = "⚠ **{0} ยังไม่พร้อม** {1}\nตรวจสอบ API key ที่ **ตั้งค่า ▸ ผู้ให้บริการ AI**" },
        ["chat.retry.countdown"] = new() { ["en"] = "Retrying in {0}s… (attempt {1}/{2})", ["th"] = "จะลองใหม่ใน {0} วิ… (ครั้งที่ {1}/{2})" },
        ["chat.retry.retrying"] = new() { ["en"] = "*Retrying…*", ["th"] = "*กำลังลองใหม่…*" },
        ["chat.retry.nothing"] = new() { ["en"] = "Nothing to retry — there is no question above this reply.", ["th"] = "ไม่มีอะไรให้ลองใหม่ — ไม่พบคำถามก่อนหน้าคำตอบนี้" },
        ["chat.interruptedNote"] = new() { ["en"] = "Generation was interrupted.", ["th"] = "การสร้างคำตอบถูกหยุดกลางคัน" },
        ["chat.resume.note"] = new() { ["en"] = "Chat resumed — the previous reply was interrupted. Resend your last message or carry on.", ["th"] = "กลับมาที่แชทเดิม — คำตอบก่อนหน้าถูกขัดจังหวะ ส่งข้อความล่าสุดอีกครั้งหรือคุยต่อได้เลย" },
        ["chat.resume.status"] = new() { ["en"] = "Chat resumed (the previous reply was interrupted)", ["th"] = "กลับมาที่แชทเดิม (คำตอบก่อนหน้าถูกขัดจังหวะ)" },
        ["chat.agent.noSummary"] = new() { ["en"] = "(Finished {0} step(s), but the model sent no summary — type “summarize” or ask a follow-up.)", ["th"] = "(ทำงานเสร็จ {0} ขั้นแล้ว แต่โมเดลไม่ได้ส่งข้อความสรุป — พิมพ์ “สรุปให้หน่อย” หรือถามต่อได้เลยครับ)" },
        ["chat.agent.noReply"] = new() { ["en"] = "(The model sent no reply — try sending again or rephrase the question.)", ["th"] = "(โมเดลไม่ได้ส่งข้อความตอบกลับ — ลองส่งใหม่อีกครั้ง หรือปรับคำถามดูครับ)" },

        // ─── Switching chats while a reply runs ───
        ["chat.busy.title"] = new() { ["en"] = "A reply is still running", ["th"] = "ยังสร้างคำตอบอยู่" },
        ["chat.busy.stopFirst"] = new() { ["en"] = "The AI is still working on a reply in this chat.\n\nStop it and continue?", ["th"] = "AI ยังทำงานกับคำตอบในแชทนี้อยู่\n\nหยุดแล้วทำต่อเลยไหม?" },
        ["chat.busy.stillStopping"] = new() { ["en"] = "Still stopping the current reply — try again in a moment.", ["th"] = "กำลังหยุดคำตอบปัจจุบัน — ลองใหม่อีกครั้งในอีกสักครู่" },

        // ─── Delete / clear ───
        ["chat.delete.title"] = new() { ["en"] = "Delete chat", ["th"] = "ลบแชท" },
        ["chat.delete.confirm"] = new() { ["en"] = "Delete this chat and all its messages? This cannot be undone.", ["th"] = "ลบแชทนี้และข้อความทั้งหมดไหม? ย้อนกลับไม่ได้" },
        ["chat.clear.title"] = new() { ["en"] = "Clear chat", ["th"] = "ล้างแชท" },
        ["chat.clear.confirm"] = new() { ["en"] = "Clear all {0} messages from this chat?\nThis cannot be undone.", ["th"] = "ล้างข้อความทั้ง {0} รายการในแชทนี้ไหม?\nย้อนกลับไม่ได้" },

        // ─── Compaction ───
        ["chat.compact.done"] = new() { ["en"] = "Compacted: the AI now sees a summary of {0} earlier messages plus the latest ones. Nothing was deleted.", ["th"] = "บีบอัดแล้ว: AI จะเห็นสรุปของ {0} ข้อความก่อนหน้า พร้อมข้อความล่าสุด — ไม่มีข้อความใดถูกลบ" },
        ["chat.compact.nothing"] = new() { ["en"] = "Nothing to compact yet — the conversation is still short.", ["th"] = "ยังไม่มีอะไรให้บีบอัด — บทสนทนายังสั้นอยู่" },
        ["chat.compact.markerTitle"] = new() { ["en"] = "Context compacted here", ["th"] = "บีบอัด context ตรงนี้" },
        ["chat.compact.marker"] = new() { ["en"] = "The AI sees a summary of the messages above, plus everything below. The full transcript is kept.", ["th"] = "AI จะเห็นสรุปของข้อความด้านบน และข้อความด้านล่างทั้งหมด — ประวัติแชทยังอยู่ครบ" },
        ["chat.compact.showSummary"] = new() { ["en"] = "Show the summary the AI sees", ["th"] = "ดูสรุปที่ AI เห็น" },
        ["chat.toast.hotTitle"] = new() { ["en"] = "Context is getting hot", ["th"] = "context เริ่มแน่น" },
        ["chat.toast.hotReason"] = new() { ["en"] = "{0}% used. Compact now to keep the next reply sharp.", ["th"] = "ใช้ไปแล้ว {0}% — บีบอัดตอนนี้เพื่อให้คำตอบถัดไปยังคมชัด" },
        ["chat.toast.autoCompact"] = new() { ["en"] = "Compact by itself from now on — don't ask", ["th"] = "บีบอัดอัตโนมัติจากนี้ไป ไม่ต้องถาม" },
        ["chat.toast.autoCompactTip"] = new() { ["en"] = "Where this toast would ask, CluadeX compacts instead — never in the middle of a reply; it waits for the turn to end. Nothing is deleted. Untick here or in Settings.", ["th"] = "ตรงที่ป๊อปอัปนี้จะถาม CluadeX จะบีบอัดให้เลย — ไม่ตัดกลางคำตอบ จะรอให้จบรอบก่อน ไม่มีข้อความใดถูกลบ ปิดได้ที่นี่หรือในหน้าตั้งค่า" },
        ["chat.toast.warmTitle"] =new() { ["en"] = "Good moment to compact", ["th"] = "ได้เวลาบีบอัดแล้ว" },
        ["chat.toast.warmReason"] = new() { ["en"] = "You've crossed {0}% — a quick compact resets the headroom.", ["th"] = "เกิน {0}% แล้ว — บีบอัดสักครั้งจะได้พื้นที่คืนมา" },
        ["chat.toast.toolsTitle"] = new() { ["en"] = "Lots of tool calls this session", ["th"] = "เรียกใช้เครื่องมือเยอะในแชทนี้" },
        ["chat.toast.toolsReason"] = new() { ["en"] = "{0} tool calls since the last compact. A compact trims the noise.", ["th"] = "เรียกใช้เครื่องมือไปแล้ว {0} ครั้งนับจากการบีบอัดครั้งก่อน — บีบอัดเพื่อตัดส่วนที่ไม่จำเป็นออก" },

        // ─── Context meter ───
        ["chat.ctx.fresh"] = new() { ["en"] = "FRESH", ["th"] = "สด" },
        ["chat.ctx.ok"] = new() { ["en"] = "OK", ["th"] = "ปกติ" },
        ["chat.ctx.warm"] = new() { ["en"] = "WARM", ["th"] = "อุ่น" },
        ["chat.ctx.hot"] = new() { ["en"] = "HOT", ["th"] = "ร้อน" },
        ["chat.ctx.full"] = new() { ["en"] = "FULL", ["th"] = "เต็ม" },
        ["chat.ctx.hotWarning"] = new() { ["en"] = "⚠ Context is getting full — replies may lose quality. Compact the conversation or start a New Chat.", ["th"] = "⚠ context ใกล้เต็ม — คุณภาพคำตอบอาจลดลง บีบอัดบทสนทนาหรือเริ่มแชทใหม่" },
        ["chat.ctx.fullWarning"] = new() { ["en"] = "🔴 Context almost full — the AI is losing earlier messages. Compact now or start a New Chat.", ["th"] = "🔴 context เกือบเต็ม — AI เริ่มลืมข้อความก่อนหน้า บีบอัดตอนนี้หรือเริ่มแชทใหม่" },

        // ─── Permission prompts ───
        ["chat.perm.prompt"] = new() { ["en"] = "Allow {0} to {1}?", ["th"] = "อนุญาตให้ {0} ทำสิ่งนี้ไหม: {1}" },
        ["chat.perm.allowed"] = new() { ["en"] = "✓ Allowed", ["th"] = "✓ อนุญาตแล้ว" },
        ["chat.perm.denied"] = new() { ["en"] = "✗ Denied", ["th"] = "✗ ปฏิเสธแล้ว" },
        ["chat.perm.cancelled"] = new() { ["en"] = "Cancelled — the reply was stopped", ["th"] = "ยกเลิก — หยุดการตอบแล้ว" },
        ["chat.perm.dismissed"] = new() { ["en"] = "Dismissed — the chat was cleared or switched", ["th"] = "ปิดไปแล้ว — ล้างหรือสลับแชท" },
        ["chat.perm.hookBlocked"] = new() { ["en"] = "A PreToolUse hook blocked this tool: {0}", ["th"] = "ฮุก PreToolUse บล็อกเครื่องมือนี้: {0}" },
        ["chat.perm.redirect"] = new() { ["en"] = "follow a redirect from {0} to {1} (another site)", ["th"] = "ตามการเปลี่ยนเส้นทางจาก {0} ไปยัง {1} (เว็บไซต์อื่น)" },
        ["chat.perm.allowChat"] = new() { ["en"] = "Allow in this chat", ["th"] = "อนุญาตทั้งแชทนี้" },
        ["chat.perm.allowChat.tip"] = new() { ["en"] = "Let this tool run without asking again in this chat. Other chats and new chats still ask.", ["th"] = "ให้เครื่องมือนี้ทำงานต่อโดยไม่ถามอีกในแชทนี้ แชทอื่นและแชทใหม่ยังถามเหมือนเดิม" },
        ["chat.perm.allowedChat"] = new() { ["en"] = "✓ Allowed in this chat", ["th"] = "✓ อนุญาตทั้งแชทนี้แล้ว" },

        // ─── Agent tool results (web_fetch, repl) ───
        ["chat.web.invalidUrl"] = new() { ["en"] = "Only http:// and https:// URLs can be fetched: {0}", ["th"] = "ดึงข้อมูลได้เฉพาะ URL แบบ http:// และ https:// เท่านั้น: {0}" },
        ["chat.web.httpError"] = new() { ["en"] = "{0} answered HTTP {1} {2}.", ["th"] = "{0} ตอบกลับ HTTP {1} {2}" },
        ["chat.web.redirectNotFollowed"] = new() { ["en"] = "{0} redirects to {1} — another site, and following it was not allowed, so nothing was sent there. To read it, fetch that URL directly; it gets the same permission check.", ["th"] = "{0} เปลี่ยนเส้นทางไปที่ {1} ซึ่งเป็นเว็บไซต์อื่นและไม่ได้รับอนุญาตให้ตามไป จึงไม่มีการส่งคำขอไปที่นั่น ถ้าต้องการอ่าน ให้เรียก web_fetch กับ URL นั้นโดยตรง ซึ่งจะผ่านการตรวจสิทธิ์แบบเดียวกัน" },
        ["chat.web.redirectNotWeb"] = new() { ["en"] = "{0} redirects to {1}, which is not a web address — not followed.", ["th"] = "{0} เปลี่ยนเส้นทางไปที่ {1} ซึ่งไม่ใช่ที่อยู่เว็บ — ไม่ได้ตามไป" },
        ["chat.web.tooManyRedirects"] = new() { ["en"] = "Stopped after {0} redirects (last page: {1}).", ["th"] = "หยุดหลังจากเปลี่ยนเส้นทาง {0} ครั้ง (หน้าสุดท้าย: {1})" },
        ["chat.web.timedOut"] = new() { ["en"] = "Fetching {0} timed out ({1} s) — the site was too slow to send the page.", ["th"] = "ดึงข้อมูลจาก {0} หมดเวลา ({1} วินาที) — เว็บไซต์ส่งหน้าเว็บช้าเกินไป" },
        ["chat.web.failed"] = new() { ["en"] = "Couldn't fetch {0} — the site couldn't be reached (details are in the Debug Log).", ["th"] = "ดึงข้อมูลจาก {0} ไม่สำเร็จ — ติดต่อเว็บไซต์ไม่ได้ (ดูรายละเอียดที่หน้า Debug Log)" },
        ["chat.web.cancelled"] = new() { ["en"] = "The fetch was stopped.", ["th"] = "หยุดการดึงข้อมูลแล้ว" },
        ["chat.repl.unsupported"] = new() { ["en"] = "Unsupported REPL language '{0}' — use python or node. For shell commands use run_command.", ["th"] = "ไม่รองรับภาษา REPL '{0}' — ใช้ python หรือ node · สำหรับคำสั่ง shell ให้ใช้ run_command" },

        // ─── Toggles (project bar / bottom bar) ───
        ["chat.agentLabel"] = new() { ["en"] = "Agent", ["th"] = "เอเจนต์" },
        ["chat.autonomousLabel"] = new() { ["en"] = "Auto-fix loop", ["th"] = "วนแก้จนผ่าน" },
        ["chat.showThinkingLabel"] = new() { ["en"] = "Show thinking", ["th"] = "แสดงการคิด" },
        ["chat.autoRunLabel"] = new() { ["en"] = "Auto-run code", ["th"] = "รันโค้ดเอง" },
        ["chat.autoRunWarnTip"] = new() { ["en"] = "Runs code blocks the AI writes on THIS PC without asking, then sends errors back for a fix.\nOff by default — you are asked to confirm every time you turn it on.", ["th"] = "รันบล็อกโค้ดที่ AI เขียนบนเครื่องนี้ทันทีโดยไม่ถาม แล้วส่ง error กลับไปให้แก้\nปิดไว้เป็นค่าเริ่มต้น — จะถามยืนยันทุกครั้งที่เปิด" },
        ["chat.autoRun.confirmTitle"] = new() { ["en"] = "Run AI code automatically?", ["th"] = "รันโค้ดจาก AI อัตโนมัติ?" },
        ["chat.autoRun.confirm"] = new() { ["en"] = "With this on, code blocks the AI writes run on THIS computer without asking you first, and their errors are sent back to the AI to fix.\n\nOnly turn it on if you trust what the model writes. Turn it on?", ["th"] = "ถ้าเปิด บล็อกโค้ดที่ AI เขียนจะถูกรันบนเครื่องนี้ทันทีโดยไม่ถามก่อน และ error จะถูกส่งกลับให้ AI แก้\n\nเปิดเฉพาะเมื่อไว้ใจโค้ดที่โมเดลเขียนเท่านั้น ต้องการเปิดไหม?" },
        ["chat.stepsLabel"] = new() { ["en"] = "Steps", ["th"] = "ขั้นตอน" },
        ["chat.stepsTip"] = new() { ["en"] = "Show the agent's steps (tool calls, status lines) in the chat.\nOff = only questions and answers · errors, diffs and permission requests always show.", ["th"] = "แสดงขั้นตอนการทำงานของ agent (tool calls / สถานะ) ในแชท\nปิดไว้ = เห็นเฉพาะคำถามกับคำตอบ · ข้อผิดพลาด diff และคำขออนุญาตยังแสดงเสมอ" },
        ["chat.search.matchesSuffix"] = new() { ["en"] = " matches", ["th"] = " รายการที่พบ" },

        // ─── Suggested prompts (sent to the model in the UI language) ───
        ["chat.suggestExplainPrompt"] = new() { ["en"] = "Explain this codebase's architecture and key patterns", ["th"] = "อธิบายสถาปัตยกรรมและแพตเทิร์นหลักของโค้ดเบสนี้" },
        ["chat.suggestBugsPrompt"] = new() { ["en"] = "Find and fix bugs in this project", ["th"] = "หาและแก้บั๊กในโปรเจกต์นี้" },
        ["chat.suggestTestsPrompt"] = new() { ["en"] = "Write unit tests for the most critical code paths", ["th"] = "เขียน unit test ให้โค้ดส่วนที่สำคัญที่สุด" },
        ["chat.suggestReviewPrompt"] = new() { ["en"] = "Review recent changes and suggest improvements", ["th"] = "รีวิวการเปลี่ยนแปลงล่าสุดและเสนอแนวทางปรับปรุง" },

        // ─── Message role labels ───
        ["chat.role.assistant"] = new() { ["en"] = "CluadeX", ["th"] = "CluadeX" },
        ["chat.role.execution"] = new() { ["en"] = "Execution", ["th"] = "ผลการรัน" },
        ["chat.role.tool"] = new() { ["en"] = "Tool", ["th"] = "เครื่องมือ" },
        ["chat.role.agent"] = new() { ["en"] = "Agent", ["th"] = "เอเจนต์" },
        ["chat.role.system"] = new() { ["en"] = "System", ["th"] = "ระบบ" },
        ["chat.role.permission"] = new() { ["en"] = "Permission", ["th"] = "ขออนุญาต" },
        ["chat.role.thinking"] = new() { ["en"] = "Thinking", ["th"] = "การคิด" },
        ["chat.role.summary"] = new() { ["en"] = "Summary", ["th"] = "สรุป" },

        // ─── Project folder / clone ───
        ["chat.folder.pickTitle"] = new() { ["en"] = "Open Project Folder", ["th"] = "เปิดโฟลเดอร์โปรเจกต์" },
        ["chat.folder.opened"] = new() { ["en"] = "Opened project: {0}\nAgent mode is on — I can now read, edit and create files and use Git in this project.", ["th"] = "เปิดโปรเจกต์: {0}\nเปิดโหมดเอเจนต์แล้ว — อ่าน แก้ไข สร้างไฟล์ และใช้ Git ในโปรเจกต์นี้ได้" },
        ["chat.clone.title"] = new() { ["en"] = "Connect GitHub repo", ["th"] = "เชื่อม repo GitHub" },
        ["chat.clone.prompt"] = new() { ["en"] = "Paste a repository URL (https://github.com/owner/repo, or a git@ / .git URL). It will be cloned and opened as your project.", ["th"] = "วาง URL ของ repository (https://github.com/owner/repo หรือแบบ git@ / .git) ระบบจะโคลนแล้วเปิดเป็นโปรเจกต์ให้" },
        ["chat.clone.badUrl"] = new() { ["en"] = "That doesn't look like a valid repository URL: \"{0}\"", ["th"] = "URL นี้ไม่ใช่ URL ของ repository ที่ถูกต้อง: \"{0}\"" },
        ["chat.clone.noName"] = new() { ["en"] = "Couldn't read a repository name from \"{0}\". Expected something like https://github.com/owner/repo.", ["th"] = "อ่านชื่อ repository จาก \"{0}\" ไม่ได้ ควรเป็นรูปแบบ https://github.com/owner/repo" },
        ["chat.clone.pickParent"] = new() { ["en"] = "Choose where to clone \"{0}\"", ["th"] = "เลือกตำแหน่งที่จะโคลน \"{0}\"" },
        ["chat.clone.exists"] = new() { ["en"] = "\"{0}\" already exists and isn't empty.\n\nOpen it as the project instead of cloning?", ["th"] = "\"{0}\" มีอยู่แล้วและไม่ว่าง\n\nเปิดเป็นโปรเจกต์แทนการโคลนไหม?" },
        ["chat.clone.existsTitle"] = new() { ["en"] = "Folder exists", ["th"] = "มีโฟลเดอร์นี้อยู่แล้ว" },
        ["chat.clone.cloning"] = new() { ["en"] = "Cloning {0}\n→ {1}", ["th"] = "กำลังโคลน {0}\n→ {1}" },
        ["chat.clone.status"] = new() { ["en"] = "Cloning {0}…", ["th"] = "กำลังโคลน {0}…" },
        ["chat.clone.done"] = new() { ["en"] = "Cloned {0}", ["th"] = "โคลน {0} เสร็จแล้ว" },
        ["chat.clone.failed"] = new() { ["en"] = "Clone failed — check the URL and that you have access to the repository.", ["th"] = "โคลนไม่สำเร็จ — ตรวจสอบ URL และสิทธิ์การเข้าถึง repository" },
        ["chat.clone.failedStatus"] = new() { ["en"] = "Clone failed", ["th"] = "โคลนไม่สำเร็จ" },
        ["chat.clone.badTarget"] = new() { ["en"] = "The clone target folder isn't a valid path: \"{0}\"", ["th"] = "โฟลเดอร์ปลายทางของการโคลนไม่ใช่พาธที่ถูกต้อง: \"{0}\"" },

        // ─── Running code ───
        ["chat.exec.running"] = new() { ["en"] = "Running {0} code…", ["th"] = "กำลังรันโค้ด {0}…" },
        ["chat.exec.output"] = new() { ["en"] = "Output", ["th"] = "ผลลัพธ์" },
        ["chat.exec.error"] = new() { ["en"] = "Error", ["th"] = "ข้อผิดพลาด" },
        ["chat.exec.errors"] = new() { ["en"] = "Errors", ["th"] = "ข้อผิดพลาด" },
        ["chat.exec.succeeded"] = new() { ["en"] = "Execution succeeded", ["th"] = "รันสำเร็จ" },
        ["chat.exec.failed"] = new() { ["en"] = "Execution failed", ["th"] = "รันไม่สำเร็จ" },
        ["chat.exec.attempts"] = new() { ["en"] = "Attempts: {0}", ["th"] = "จำนวนครั้งที่ลอง: {0}" },
        ["chat.exec.cancelled"] = new() { ["en"] = "Execution stopped.", ["th"] = "หยุดการรันแล้ว" },
        ["chat.exec.couldNotRun"] = new() { ["en"] = "Couldn't run the code — details are in the Debug Log.", ["th"] = "รันโค้ดไม่ได้ — ดูรายละเอียดที่หน้า Debug Log" },
        ["chat.exec.hadIssues"] = new() { ["en"] = "Code execution had issues", ["th"] = "การรันโค้ดมีปัญหา" },
        ["chat.exec.blocked"] = new() { ["en"] = "Blocked for safety — this line matches the dangerous-command list, so the block was not run:\n{0}\n(To allow it, turn off Dangerous Command Blocking on the Features page.)", ["th"] = "บล็อกไว้เพื่อความปลอดภัย — บรรทัดนี้ตรงกับรายการคำสั่งอันตราย จึงไม่ได้รันโค้ดบล็อกนี้:\n{0}\n(ถ้าต้องการอนุญาต ให้ปิด \"บล็อกคำสั่งอันตราย\" ที่หน้าฟีเจอร์)" },
        ["chat.auto.verifyBlocked"] = new() { ["en"] = "The build / test command was blocked for safety, so nothing was run: {0}\nChange it in Settings (Build / test command), or turn off Dangerous Command Blocking on the Features page.", ["th"] = "คำสั่ง build / test ถูกบล็อกเพื่อความปลอดภัย จึงไม่ได้รันอะไรเลย: {0}\nแก้ได้ที่หน้าตั้งค่า (คำสั่ง build / test) หรือปิด \"บล็อกคำสั่งอันตราย\" ที่หน้าฟีเจอร์" },

        // ─── Local model picker ───
        ["chat.model.ready"] = new() { ["en"] = "Ready: {0}", ["th"] = "พร้อม: {0}" },
        ["chat.model.notLoaded"] = new() { ["en"] = "{0} (not loaded)", ["th"] = "{0} (ยังไม่ได้โหลด)" },
        ["chat.model.loadingName"] = new() { ["en"] = "Loading: {0}…", ["th"] = "กำลังโหลด: {0}…" },
        ["chat.model.loading"] = new() { ["en"] = "Loading model…", ["th"] = "กำลังโหลดโมเดล…" },
        ["chat.model.loadFailed"] = new() { ["en"] = "The model failed to load — details are in the Debug Log.", ["th"] = "โหลดโมเดลไม่สำเร็จ — ดูรายละเอียดที่หน้า Debug Log" },
        ["chat.model.autoLoadFailedShort"] = new() { ["en"] = "model failed to load", ["th"] = "โหลดโมเดลไม่สำเร็จ" },
        ["chat.model.autoLoadFailed"] = new() { ["en"] = "The saved model failed to load — open the Models tab (details are in the Debug Log).", ["th"] = "โหลดโมเดลที่บันทึกไว้ไม่สำเร็จ — เปิดแท็บโมเดล (รายละเอียดอยู่ใน Debug Log)" },
        ["chat.model.busy"] = new() { ["en"] = "A model is already loading — wait for it to finish.", ["th"] = "กำลังโหลดโมเดลอยู่ — รอให้เสร็จก่อน" },
        ["chat.model.busyTurn"] = new() { ["en"] = "Wait for the current reply to finish before switching models.", ["th"] = "รอให้คำตอบปัจจุบันเสร็จก่อนค่อยเปลี่ยนโมเดล" },
        ["chat.model.confirmTitle"] = new() { ["en"] = "Load model", ["th"] = "โหลดโมเดล" },
        ["chat.model.confirmVram"] = new() { ["en"] = "Load {0} ({1})?\n\nFree VRAM right now: {2} of {3}.", ["th"] = "โหลด {0} ({1}) ไหม?\n\nVRAM ว่างตอนนี้: {2} จาก {3}" },
        ["chat.model.confirmNoGpu"] = new() { ["en"] = "Load {0} ({1})?\n\nNo NVIDIA GPU was found — it will run on the CPU and system RAM, which is much slower.", ["th"] = "โหลด {0} ({1}) ไหม?\n\nไม่พบการ์ดจอ NVIDIA — จะรันบน CPU และ RAM ซึ่งช้ากว่ามาก" },
        ["chat.model.confirmTight"] = new() { ["en"] = "⚠ The model is larger than the free VRAM. Loading it can freeze Windows or even restart the PC — close other programs that use the GPU first, or pick a smaller model.", ["th"] = "⚠ โมเดลใหญ่กว่า VRAM ที่ว่างอยู่ การโหลดอาจทำให้ Windows ค้างหรือเครื่องรีสตาร์ทได้ — ปิดโปรแกรมอื่นที่ใช้การ์ดจอก่อน หรือเลือกโมเดลที่เล็กกว่า" },

        // ─── Misc ───
        ["chat.status.noProjectChat"] = new() { ["en"] = "This chat has no project — open a folder to tie it to one", ["th"] = "แชทนี้ยังไม่ได้ผูกกับโปรเจค — เปิดโฟลเดอร์เพื่อผูก" },
        ["chat.status.projectMissing"] = new() { ["en"] = "This chat's folder is gone: {0}", ["th"] = "ไม่พบโฟลเดอร์ของแชทนี้: {0}" },
        ["chat.side.noProjectChat"] = new() { ["en"] = "This chat is not tied to a project yet. Open a folder and the chat remembers it.", ["th"] = "แชทนี้ยังไม่ได้ผูกกับโปรเจค เปิดโฟลเดอร์แล้วแชทจะจำไว้" },
        ["chat.side.openFolder"] = new() { ["en"] = "Open folder", ["th"] = "เปิดโฟลเดอร์" },
        ["chat.plan"] = new() { ["en"] = "Plan", ["th"] = "แผนงาน" },
        ["chat.plan.allDone"] = new() { ["en"] = "All done", ["th"] = "เสร็จครบแล้ว" },
        ["chat.plan.branchParent"] = new() { ["en"] = "⤴ Branched from", ["th"] = "⤴ แยกมาจาก" },
        ["chat.plan.branchChild"] = new() { ["en"] = "⑂ Branch at step {0}", ["th"] = "⑂ แยกที่ขั้น {0}" },
        ["chat.plan.expandTip"] = new() { ["en"] = "Show / hide the whole plan", ["th"] = "แสดง / ซ่อนแผนทั้งหมด" },
        ["chat.plan.goTip"] = new() { ["en"] = "Open this chat", ["th"] = "เปิดแชทนี้" },
        ["plan.open.tip"] = new() { ["en"] = "Plan window: where this chat's plan is, add items, go back to or branch from any step", ["th"] = "หน้าต่างแผนงาน: แผนของแชทนี้อยู่ตรงไหน เพิ่มแผน ย้อนกลับหรือแยกงานจากขั้นไหนก็ได้" },
        ["plan.window.title"] = new() { ["en"] = "Plan", ["th"] = "แผนงาน" },
        ["plan.pin.tip"] = new() { ["en"] = "Keep on top", ["th"] = "ให้อยู่บนสุดเสมอ" },
        ["plan.section.items"] = new() { ["en"] = "PLAN", ["th"] = "แผน" },
        ["plan.section.steps"] = new() { ["en"] = "STEPS — GO BACK OR BRANCH", ["th"] = "ขั้นที่ทำแล้ว — ย้อนกลับ หรือ แยกงาน" },
        ["plan.items.empty"] = new() { ["en"] = "No plan yet. The agent writes one for multi-step work; you can add items below.", ["th"] = "ยังไม่มีแผน เอเจนต์จะวางแผนเมื่องานมีหลายขั้น หรือพิมพ์เพิ่มเองด้านล่างได้" },
        ["plan.steps.empty"] = new() { ["en"] = "No file changes in this chat yet. Every agent step that edits files becomes a point you can go back to.", ["th"] = "แชทนี้ยังไม่ได้แก้ไฟล์ ทุกขั้นที่เอเจนต์แก้ไฟล์จะกลายเป็นจุดที่ย้อนกลับได้" },
        ["plan.status.running"] = new() { ["en"] = "Working — step {0} · {1}/{2} done", ["th"] = "กำลังทำ — ขั้นที่ {0} · เสร็จ {1}/{2}" },
        ["plan.status.idle"] = new() { ["en"] = "Idle · {0}/{1} done", ["th"] = "ว่าง · เสร็จ {0}/{1}" },
        ["plan.status.empty"] = new() { ["en"] = "Idle", ["th"] = "ว่าง" },
        ["plan.add"] = new() { ["en"] = "Add", ["th"] = "เพิ่ม" },
        ["plan.add.tip"] = new() { ["en"] = "Add to the plan (Enter). While the agent works it picks it up at its next step.", ["th"] = "เพิ่มเข้าแผน (Enter) ถ้าเอเจนต์กำลังทำอยู่ จะรับไปทำในขั้นถัดไป" },
        ["plan.added.running"] = new() { ["en"] = "Sent — the agent adds it to its plan at its next step.", ["th"] = "ส่งแล้ว — เอเจนต์จะเพิ่มเข้าแผนในขั้นถัดไป" },
        ["plan.added.queued"] = new() { ["en"] = "Queued — press Start to send the queued items.", ["th"] = "เข้าคิวแล้ว — กด เริ่มทำ เพื่อส่งรายการในคิว" },
        ["plan.startQueued"] = new() { ["en"] = "Start the queued items", ["th"] = "เริ่มทำรายการในคิว" },
        ["plan.startMessage"] = new() { ["en"] = "Do these next, as a plan:", ["th"] = "ทำรายการนี้ต่อ ตามแผน:" },
        ["plan.rewind"] = new() { ["en"] = "Go back", ["th"] = "ย้อนกลับ" },
        ["plan.rewind.tip"] = new() { ["en"] = "Put the files back to how they were before this step", ["th"] = "คืนไฟล์ให้เป็นเหมือนก่อนขั้นนี้" },
        ["plan.branch"] = new() { ["en"] = "Branch", ["th"] = "แยกงาน" },
        ["plan.branch.tip"] = new() { ["en"] = "New chat in a copy of the project (git worktree) as it was before this step; this chat is not touched", ["th"] = "แชทใหม่ในสำเนาโปรเจค (git worktree) ที่เป็นสภาพก่อนขั้นนี้ แชทเดิมไม่ถูกแตะ" },
        ["plan.travel.busy"] = new() { ["en"] = "Pause or stop this chat first — its agent is changing files.", ["th"] = "หยุดพักหรือหยุดแชทนี้ก่อน — เอเจนต์กำลังแก้ไฟล์อยู่" },
        ["plan.rewind.title"] = new() { ["en"] = "Go back to before this step?", ["th"] = "ย้อนกลับไปก่อนขั้นนี้?" },
        ["plan.rewind.body"] = new() { ["en"] = "Step #{0} ({1}) and everything after it is undone in the files: {2}. The files as they are now are kept as a restore point, so you can come back.", ["th"] = "ขั้น #{0} ({1}) และทุกขั้นหลังจากนั้นจะถูกยกเลิกในไฟล์: {2} สภาพไฟล์ตอนนี้จะเก็บเป็นจุดย้อนกลับ กลับมาได้อีก" },
        ["plan.branch.title"] = new() { ["en"] = "Branch the work here?", ["th"] = "แยกงานตรงนี้?" },
        ["plan.branch.body"] = new() { ["en"] = "A copy of the project (git worktree, new branch) is made beside it with the files as they were before step #{0} ({1}), and a new chat opens in it. This chat and its folder stay as they are.", ["th"] = "จะสร้างสำเนาโปรเจค (git worktree กิ่งใหม่) ไว้ข้าง ๆ โดยไฟล์เป็นสภาพก่อนขั้น #{0} ({1}) แล้วเปิดแชทใหม่ในนั้น แชทนี้และโฟลเดอร์เดิมไม่เปลี่ยน" },
        ["plan.confirm"] = new() { ["en"] = "Yes, do it", ["th"] = "ยืนยัน" },
        ["plan.branch.working"] = new() { ["en"] = "Branching…", ["th"] = "กำลังแยกงาน…" },
        ["plan.branch.done"] = new() { ["en"] = "Branched — new chat opened in {0}", ["th"] = "แยกงานแล้ว — เปิดแชทใหม่ที่ {0}" },
        ["plan.branch.failed"] = new() { ["en"] = "Could not branch: {0}", ["th"] = "แยกงานไม่สำเร็จ: {0}" },
        ["plan.branch.chatTitle"] = new() { ["en"] = "Branch from step {0} — {1}", ["th"] = "แยกจากขั้น {0} — {1}" },
        ["plan.branch.note"] = new() { ["en"] = "Branched from “{0}” at step {1} ({2}). Folder: {3} · git branch {4}. The files are as they were before that step.", ["th"] = "แยกงานจาก “{0}” ที่ขั้น {1} ({2}) โฟลเดอร์: {3} · git branch {4} ไฟล์เป็นสภาพก่อนขั้นนั้น" },
        ["plan.rewind.note"] = new() { ["en"] = "Files went back to before step {0} ({1}) — {2} file(s). The previous state is a restore point in the plan window.", ["th"] = "ไฟล์ย้อนกลับไปก่อนขั้น {0} ({1}) แล้ว — {2} ไฟล์ สภาพก่อนย้อนเก็บเป็นจุดย้อนกลับในหน้าต่างแผน" },
        ["chat.tools.groupContent"] = new() { ["en"] = "{0} operations ({1})", ["th"] = "{0} การทำงาน ({1})" },
        ["chat.tools.groupName"] = new() { ["en"] = "{0} tools", ["th"] = "{0} เครื่องมือ" },
        ["chat.skill.notInvocable"] = new() { ["en"] = "Skill '/{0}' can't be run directly.", ["th"] = "สกิล '/{0}' เรียกใช้โดยตรงไม่ได้" },
        ["chat.skill.unknown"] = new() { ["en"] = "Unknown skill '/{0}'. Available: {1}", ["th"] = "ไม่รู้จักสกิล '/{0}' — ที่ใช้ได้: {1}" },
        ["chat.skill.unknownNone"] = new() { ["en"] = "Unknown skill '/{0}'. No skills are available.", ["th"] = "ไม่รู้จักสกิล '/{0}' และยังไม่มีสกิลให้ใช้" },
        ["chat.review.needProject"] = new() { ["en"] = "Open a project folder first to review code.", ["th"] = "เปิดโฟลเดอร์โปรเจกต์ก่อนจึงจะรีวิวโค้ดได้" },
        ["chat.review.running"] = new() { ["en"] = "Reviewing code…", ["th"] = "กำลังรีวิวโค้ด…" },
        ["chat.review.done"] = new() { ["en"] = "Review complete · {0} tokens · {1:F1}s", ["th"] = "รีวิวเสร็จแล้ว · {0} โทเค็น · {1:F1} วิ" },
        ["chat.review.cancelled"] = new() { ["en"] = "Review cancelled.", ["th"] = "ยกเลิกการรีวิวแล้ว" },
        ["chat.review.failed"] = new() { ["en"] = "The review failed — details are in the Debug Log.", ["th"] = "รีวิวไม่สำเร็จ — ดูรายละเอียดที่หน้า Debug Log" },

        // ─── Command palette (Ctrl+K) ───
        ["palette.kind.command"] = new() { ["en"] = "Command", ["th"] = "คำสั่ง" },
        ["palette.cmd.newChat"] = new() { ["en"] = "New chat", ["th"] = "แชทใหม่" },
        ["palette.cmd.newChatSub"] = new() { ["en"] = "Start a fresh conversation", ["th"] = "เริ่มบทสนทนาใหม่" },
        ["palette.cmd.openFolder"] = new() { ["en"] = "Open folder…", ["th"] = "เปิดโฟลเดอร์…" },
        ["palette.cmd.openFolderSub"] = new() { ["en"] = "Open a project folder for the agent", ["th"] = "เปิดโฟลเดอร์โปรเจกต์ให้เอเจนต์ทำงาน" },
        ["palette.cmd.clearChat"] = new() { ["en"] = "Clear chat", ["th"] = "ล้างแชท" },
        ["palette.cmd.clearChatSub"] = new() { ["en"] = "Remove every message from this chat (asks first)", ["th"] = "ลบข้อความทั้งหมดในแชทนี้ (ถามก่อน)" },
        ["palette.cmd.compact"] = new() { ["en"] = "Compact conversation", ["th"] = "บีบอัดบทสนทนา" },
        ["palette.cmd.compactSub"] = new() { ["en"] = "Summarize older messages for the AI — nothing is deleted", ["th"] = "สรุปข้อความเก่าให้ AI — ไม่มีข้อความใดถูกลบ" },
        ["palette.cmd.updates"] = new() { ["en"] = "Check for updates", ["th"] = "ตรวจหาอัปเดต" },
        ["palette.cmd.updatesSub"] = new() { ["en"] = "Look for a newer CluadeX version", ["th"] = "ดูว่ามี CluadeX เวอร์ชันใหม่หรือไม่" },
        ["palette.cmd.settings"] = new() { ["en"] = "Settings", ["th"] = "ตั้งค่า" },
        ["palette.cmd.settingsSub"] = new() { ["en"] = "AI providers, models, features", ["th"] = "ผู้ให้บริการ AI โมเดล และฟีเจอร์" },
        ["palette.cmd.license"] = new() { ["en"] = "License", ["th"] = "ใบอนุญาต (License)" },
        ["palette.cmd.licenseSub"] = new() { ["en"] = "View or enter your license key", ["th"] = "ดูหรือกรอกรหัส License" },
        ["palette.noMatches"] = new() { ["en"] = "No matches", ["th"] = "ไม่พบรายการ" },
        ["palette.oneResult"] = new() { ["en"] = "1 result", ["th"] = "1 รายการ" },
        ["palette.results"] = new() { ["en"] = "{0} results", ["th"] = "{0} รายการ" },

        // ─── Accept edits (status-bar switch) ───
        ["chat.acceptEdits.label"] = new() { ["en"] = "Accept edits", ["th"] = "ยอมรับการแก้ไขไฟล์" },
        ["chat.acceptEdits.tip"] = new()
        {
            ["en"] = "Accept edits: write_file, edit_file, multi_edit and create_directory run without asking — only inside the open project. These still ask: commands, MCP tools, browsing, background processes, anything outside the project, and files that run code later (.git, .husky, .vscode, .claude, CI workflows, project/build files such as *.csproj, package.json, Makefile) — and an automatic build/test after such an edit (the check after an edit, or the Autonomous loop) asks before it runs. Ends when you switch projects or restart CluadeX (unless AcceptEditsDefault is set in settings.json).",
            ["th"] = "ยอมรับการแก้ไขไฟล์: write_file, edit_file, multi_edit และ create_directory จะทำงานโดยไม่ถาม — เฉพาะภายในโปรเจกต์ที่เปิดอยู่ สิ่งที่ยังถามเหมือนเดิม: การรันคำสั่ง เครื่องมือ MCP การเปิดเว็บ โปรเซสเบื้องหลัง ทุกอย่างนอกโปรเจกต์ และไฟล์ที่จะรันโค้ดภายหลัง (.git, .husky, .vscode, .claude, CI workflow และไฟล์โปรเจกต์/build เช่น *.csproj, package.json, Makefile) — การ build/test อัตโนมัติหลังแก้ไขแบบนี้ (การตรวจหลังแก้ไข หรือโหมดทำงานอัตโนมัติ Autonomous) ก็จะถามก่อนรัน จะปิดเองเมื่อสลับโปรเจกต์หรือเปิด CluadeX ใหม่ (เว้นแต่ตั้ง AcceptEditsDefault ไว้ใน settings.json)",
        },
        ["chat.acceptEdits.confirmTitle"] = new() { ["en"] = "Accept edits without asking?", ["th"] = "ยอมรับการแก้ไขไฟล์โดยไม่ถาม?" },
        ["chat.acceptEdits.confirm"] = new()
        {
            ["en"] = "The AI will create and change files inside this project WITHOUT asking you first.\n\nStill asking: commands, MCP tools, browsing, files outside the project, and files that run code later (git hooks, .vscode, CI workflows, project/build files). An automatic build/test after an edit you did not see (the check after an edit, or the Autonomous loop) asks before it runs. This lasts until you switch projects or restart CluadeX (unless AcceptEditsDefault is set in settings.json).\n\nTurn on Accept edits?",
            ["th"] = "AI จะสร้างและแก้ไขไฟล์ภายในโปรเจกต์นี้โดยไม่ถามคุณก่อน\n\nสิ่งที่ยังถามเหมือนเดิม: การรันคำสั่ง เครื่องมือ MCP การเปิดเว็บ ไฟล์นอกโปรเจกต์ และไฟล์ที่จะรันโค้ดภายหลัง (git hook, .vscode, CI workflow, ไฟล์โปรเจกต์/build) การ build/test อัตโนมัติหลังการแก้ไขที่คุณไม่ได้ดู (การตรวจหลังแก้ไข หรือโหมดทำงานอัตโนมัติ Autonomous) จะถามก่อนรัน โหมดนี้จะอยู่จนกว่าคุณจะสลับโปรเจกต์หรือเปิด CluadeX ใหม่ (เว้นแต่ตั้ง AcceptEditsDefault ไว้ใน settings.json)\n\nเปิดโหมดยอมรับการแก้ไขไฟล์หรือไม่?",
        },
        ["chat.acceptEdits.unsafeFolder"] = new()
        {
            ["en"] = "Accept edits can't be used on {0}: it is a drive, your user folder, a system folder or CluadeX's own data. Open a project folder instead — every edit here asks.",
            ["th"] = "ใช้โหมดยอมรับการแก้ไขไฟล์กับ {0} ไม่ได้ เพราะเป็นไดรฟ์ทั้งไดรฟ์ โฟลเดอร์ผู้ใช้ โฟลเดอร์ระบบ หรือข้อมูลของ CluadeX เอง ให้เปิดโฟลเดอร์โปรเจกต์แทน — การแก้ไขทุกครั้งที่นี่จะถามก่อน",
        },
        ["chat.acceptEdits.pipeConfirmTitle"] = new() { ["en"] = "A program asks to accept edits", ["th"] = "มีโปรแกรมขอให้ยอมรับการแก้ไขไฟล์" },
        ["chat.acceptEdits.pipeConfirm"] = new()
        {
            ["en"] = "The program that sent task \"{0}\" over CluadeX's pipe asks to let the AI create and change files in\n{1}\nWITHOUT asking you. If you allow it, every task sent over the pipe for this folder may do the same until CluadeX restarts.\n\nCommands, MCP tools, files outside the folder and files that run code later still ask.\n\nAllow it?",
            ["th"] = "โปรแกรมที่ส่งงาน \"{0}\" มาทาง pipe ของ CluadeX ขอให้ AI สร้างและแก้ไขไฟล์ใน\n{1}\nโดยไม่ถามคุณ ถ้าอนุญาต งานทุกงานที่ส่งมาทาง pipe สำหรับโฟลเดอร์นี้จะทำแบบเดียวกันได้จนกว่าจะเปิด CluadeX ใหม่\n\nการรันคำสั่ง เครื่องมือ MCP ไฟล์นอกโฟลเดอร์ และไฟล์ที่จะรันโค้ดภายหลังยังถามเหมือนเดิม\n\nอนุญาตหรือไม่?",
        },
        ["chat.acceptEdits.pipeOn"] = new()
        {
            ["en"] = "The program that sent this task turned on Accept edits for {0} (you allowed it): file edits inside it run without asking during this run.",
            ["th"] = "โปรแกรมที่ส่งงานนี้เปิดโหมดยอมรับการแก้ไขไฟล์สำหรับ {0} (คุณอนุญาตแล้ว): การแก้ไขไฟล์ในโฟลเดอร์นี้จะไม่ถามระหว่างงานนี้",
        },
        ["chat.acceptEdits.pipeDeclined"] = new()
        {
            ["en"] = "The program that sent this task asked to accept edits without prompts for {0}; you declined, so every edit asks.",
            ["th"] = "โปรแกรมที่ส่งงานนี้ขอให้ยอมรับการแก้ไขไฟล์ใน {0} โดยไม่ถาม แต่คุณไม่อนุญาต การแก้ไขทุกครั้งจึงยังถามก่อน",
        },
        ["perm.autoVerify.acceptedEdits"] = new()
        {
            ["en"] = "Automatic build check after file edits that Accept edits let through without asking you. A build runs whatever the project's build files and scripts say — allow it only if you trust those edits.",
            ["th"] = "การตรวจ build อัตโนมัติหลังการแก้ไขไฟล์ที่โหมดยอมรับการแก้ไขไฟล์ปล่อยผ่านโดยไม่ได้ถามคุณ การ build จะรันทุกอย่างที่ไฟล์ build และสคริปต์ของโปรเจกต์กำหนด — อนุญาตเฉพาะเมื่อไว้ใจการแก้ไขเหล่านั้น",
        },
        ["perm.autoVerify.autonomous"] = new()
        {
            ["en"] = "The Autonomous loop's build / test command, after file edits that Accept edits let through without asking you. It runs whatever the project's build files, scripts and test files now say — allow it only if you trust those edits.",
            ["th"] = "คำสั่ง build / test ของโหมดทำงานอัตโนมัติ (Autonomous) หลังการแก้ไขไฟล์ที่โหมดยอมรับการแก้ไขไฟล์ปล่อยผ่านโดยไม่ได้ถามคุณ คำสั่งนี้จะรันทุกอย่างที่ไฟล์ build สคริปต์ และไฟล์ทดสอบของโปรเจกต์กำหนดไว้ตอนนี้ — อนุญาตเฉพาะเมื่อไว้ใจการแก้ไขเหล่านั้น",
        },
        ["chat.auto.verifyDeclined"] = new()
        {
            ["en"] = "The build / test command was not run: `{0}`. Files were edited without asking you (Accept edits) and running the build over them was not approved, so the autonomous run stopped here. Review the edits, then start the run again (it will ask again).",
            ["th"] = "ไม่ได้รันคำสั่ง build / test: `{0}` เพราะมีไฟล์ที่ถูกแก้ไขโดยไม่ได้ถามคุณ (โหมดยอมรับการแก้ไขไฟล์) และการรัน build หลังการแก้ไขนั้นไม่ได้รับอนุญาต การทำงานอัตโนมัติจึงหยุดตรงนี้ ตรวจดูการแก้ไขก่อน แล้วเริ่มใหม่อีกครั้ง (ระบบจะถามอีกครั้ง)",
        },
        ["chat.acceptEdits.pipeUserOn"] = new()
        {
            ["en"] = "Accept edits is on for {0} because you turned it on there yourself: file edits inside it run without asking during this run. The program that sent this task gets no lasting permission from this.",
            ["th"] = "โหมดยอมรับการแก้ไขไฟล์เปิดอยู่สำหรับ {0} เพราะคุณเปิดโหมดนี้ไว้เองที่โฟลเดอร์นี้ การแก้ไขไฟล์ในโฟลเดอร์นี้จะไม่ถามระหว่างงานนี้ โปรแกรมที่ส่งงานนี้ไม่ได้รับสิทธิ์ถาวรจากการนี้",
        },

        // ─── Pipe-driven runs (cluadex.write_code / run_skill / review) ───
        ["chat.pipe.callerGone"] = new()
        {
            ["en"] = "Stopped — the program that sent this task disconnected or cancelled it.",
            ["th"] = "หยุดแล้ว — โปรแกรมที่ส่งงานนี้มาตัดการเชื่อมต่อหรือยกเลิกงาน",
        },
        ["chat.pipe.backToProject"] = new()
        {
            ["en"] = "CluadeX is back on your project {0}. This task worked in {1} — a message you send here now works in {0}; to continue the task, open {1} first.",
            ["th"] = "CluadeX กลับมาที่โปรเจกต์ของคุณ {0} แล้ว งานนี้ทำใน {1} — ข้อความที่ส่งในแชทนี้ตอนนี้จะทำงานใน {0} ถ้าจะทำงานนี้ต่อ ให้เปิด {1} ก่อน",
        },
        ["chat.pipe.backToNoProject"] = new()
        {
            ["en"] = "The task's folder {0} is closed again (no project was open before). To continue the task, open {0} first.",
            ["th"] = "ปิดโฟลเดอร์ของงาน {0} แล้ว (ก่อนหน้านี้ไม่ได้เปิดโปรเจกต์ไว้) ถ้าจะทำงานนี้ต่อ ให้เปิด {0} ก่อน",
        },

        // ─── Tool-row image thumbnail ───
        ["chat.thumbnail.tip"] = new() { ["en"] = "Image the tool returned — click to open it full size", ["th"] = "ภาพที่เครื่องมือส่งกลับมา — คลิกเพื่อเปิดภาพเต็ม" },

        // ─── MCP server start (shown on the MCP Servers page and the status chip) ───
        ["mcp.handshake.timedOut"] = new()
        {
            ["en"] = "The server did not answer the initialize handshake within {0} s. A first start that downloads a package (npx, uvx) can need longer: set \"startupTimeoutSeconds\" for this server in mcp_servers.json, or install the package globally, then press Restart.",
            ["th"] = "เซิร์ฟเวอร์ไม่ตอบการเริ่มต้นเชื่อมต่อ (initialize) ภายใน {0} วินาที การเริ่มครั้งแรกที่ต้องดาวน์โหลดแพ็กเกจ (npx, uvx) อาจใช้เวลานานกว่านั้น: ตั้งค่า \"startupTimeoutSeconds\" ให้เซิร์ฟเวอร์นี้ใน mcp_servers.json หรือติดตั้งแพ็กเกจแบบ global แล้วกดปุ่ม \"เริ่มใหม่\"",
        },
        ["mcp.handshake.timedOutStderr"] = new()
        {
            ["en"] = "The server did not answer the initialize handshake within {0} s. Its last error output:\n{1}",
            ["th"] = "เซิร์ฟเวอร์ไม่ตอบการเริ่มต้นเชื่อมต่อ (initialize) ภายใน {0} วินาที ข้อความผิดพลาดล่าสุดของเซิร์ฟเวอร์:\n{1}",
        },
    };
}
