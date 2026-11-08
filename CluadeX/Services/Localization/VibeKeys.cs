namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the chat's vibe-coding helpers (owner 2026-11-08): the per-turn change card, the
/// error chip by the composer, "explain simply", @-mentions, click-to-ask on a picture and the context gauge.
/// Every key has "en" and "th".</summary>
internal static class VibeKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ─── the change card at the end of a turn ───
        ["changes.title"] = new() { ["en"] = "What changed this turn", ["th"] = "สิ่งที่เปลี่ยนในรอบนี้" },
        ["changes.line.edited"] = new() { ["en"] = "Changed {0} file(s)", ["th"] = "แก้ {0} ไฟล์" },
        ["changes.line.created"] = new() { ["en"] = "Made {0} new file(s)", ["th"] = "สร้างไฟล์ใหม่ {0} ไฟล์" },
        ["changes.line.both"] = new() { ["en"] = "Changed {0} file(s) and made {1} new", ["th"] = "แก้ {0} ไฟล์ และสร้างใหม่ {1} ไฟล์" },
        ["changes.area.screen"] = new() { ["en"] = "what you see ({0})", ["th"] = "หน้าตาที่เห็น ({0})" },
        ["changes.area.logic"] = new() { ["en"] = "how it works ({0})", ["th"] = "การทำงานเบื้องหลัง ({0})" },
        ["changes.area.data"] = new() { ["en"] = "settings and data ({0})", ["th"] = "การตั้งค่าและข้อมูล ({0})" },
        ["changes.area.test"] = new() { ["en"] = "the tests ({0})", ["th"] = "ชุดทดสอบ ({0})" },
        ["changes.area.docs"] = new() { ["en"] = "notes and docs ({0})", ["th"] = "เอกสาร ({0})" },
        ["changes.and"] = new() { ["en"] = " and ", ["th"] = " และ" },
        ["changes.more"] = new() { ["en"] = "+{0} more", ["th"] = "อีก {0}" },
        ["changes.moreFiles"] = new() { ["en"] = "+{0} more files", ["th"] = "อีก {0} ไฟล์" },
        ["changes.new"] = new() { ["en"] = "new", ["th"] = "ใหม่" },
        ["changes.openTip"] = new() { ["en"] = "Open this file in the code editor", ["th"] = "เปิดไฟล์นี้ในหน้าแก้โค้ด" },
        ["changes.before"] = new() { ["en"] = "Before", ["th"] = "ก่อน" },
        ["changes.after"] = new() { ["en"] = "After", ["th"] = "หลัง" },
        ["changes.picTip"] = new() { ["en"] = "The last picture of the app before the first screen change and after the last one — click to open it", ["th"] = "ภาพแอปล่าสุดก่อนแก้หน้าจอครั้งแรก และหลังแก้ครั้งสุดท้าย — คลิกเพื่อเปิดดู" },
        ["changes.totalTip"] = new() { ["en"] = "Lines added and removed this turn, all files", ["th"] = "บรรทัดที่เพิ่มและลบในรอบนี้ ทุกไฟล์รวมกัน" },

        // ─── the error chip by the composer ───
        ["vibe.errors.chip"] = new() { ["en"] = "{0} error(s) — fix", ["th"] = "พบข้อผิดพลาด {0} จุด — ให้แก้" },
        ["vibe.errors.tip"] = new() { ["en"] = "Errors the last build, test or check found in this project ({0}). Click: the AI gets the list and fixes them.", ["th"] = "ข้อผิดพลาดที่การ build / ทดสอบ / ตรวจล่าสุดเจอในโปรเจกต์นี้ ({0}) คลิก: ส่งรายการให้ AI แก้" },
        ["vibe.errors.prompt"] = new() { ["en"] = "The project has {0} error(s) from the last build/check. Please fix them, then build or check again to confirm they are gone:", ["th"] = "โปรเจกต์มีข้อผิดพลาด {0} จุดจากการ build / ตรวจล่าสุด ช่วยแก้ให้หมด แล้ว build หรือตรวจซ้ำเพื่อยืนยันว่าหายแล้ว:" },
        ["vibe.errors.more"] = new() { ["en"] = "…and {0} more — run the build/check to see them all.", ["th"] = "…และอีก {0} จุด — รัน build / ตรวจ เพื่อดูทั้งหมด" },
        ["vibe.errors.busy"] = new() { ["en"] = "Sent to the reply that is running", ["th"] = "ส่งเข้าไปในคำตอบที่กำลังทำอยู่แล้ว" },

        // ─── explain simply ───
        ["vibe.explain.button"] = new() { ["en"] = "Explain simply", ["th"] = "อธิบายง่ายๆ" },
        ["vibe.explain.tip"] = new() { ["en"] = "Ask the AI to explain this reply again in plain words, for someone who does not read code", ["th"] = "ให้ AI อธิบายคำตอบนี้ใหม่ด้วยภาษาง่ายๆ สำหรับคนที่ไม่ได้อ่านโค้ด" },
        ["vibe.explain.prompt"] = new() { ["en"] = "Explain this answer of yours again in plain everyday English, for someone who builds apps but does not read code. No code, no jargon (if a technical word is unavoidable, say what it means in a few words). Say what it means for the app and what I should do next, in short points. Do not change any files or run anything.\n\n{0}", ["th"] = "ช่วยอธิบายคำตอบนี้ของคุณใหม่เป็นภาษาไทยง่ายๆ แบบภาษาพูด สำหรับคนที่สร้างแอปแต่ไม่ได้อ่านโค้ด ไม่ใส่โค้ด ไม่ใช้ศัพท์เทคนิค (ถ้าเลี่ยงไม่ได้ให้บอกความหมายสั้นๆ) บอกว่ามันมีผลกับแอปยังไง และฉันควรทำอะไรต่อ เป็นข้อสั้นๆ ไม่ต้องแก้ไฟล์หรือรันอะไรทั้งนั้น\n\n{0}" },

        // ─── @-mentions ───
        ["vibe.mention.title"] = new() { ["en"] = "Files and names in this project", ["th"] = "ไฟล์และชื่อในโปรเจกต์นี้" },
        ["vibe.mention.none"] = new() { ["en"] = "Nothing matches", ["th"] = "ไม่พบที่ตรงกัน" },
        ["vibe.mention.noProject"] = new() { ["en"] = "Open a project to mention its files", ["th"] = "เปิดโปรเจกต์ก่อน จึงจะ @ ไฟล์ได้" },
        ["vibe.mention.loading"] = new() { ["en"] = "Reading the project…", ["th"] = "กำลังอ่านโปรเจกต์…" },
        ["vibe.mention.hint"] = new() { ["en"] = "↑↓ choose · Enter/Tab insert · Esc close", ["th"] = "↑↓ เลือก · Enter/Tab ใส่ · Esc ปิด" },

        // ─── click a picture to ask about a spot ───
        ["vibe.ask.hint"] = new() { ["en"] = "Click a spot in the picture to ask about it", ["th"] = "คลิกจุดในภาพเพื่อถามเกี่ยวกับตรงนั้น" },
        ["vibe.ask.prefill"] = new() { ["en"] = "In the attached picture, at the spot circled in red (about {0}% across, {1}% down): ", ["th"] = "ในภาพที่แนบ ตรงจุดที่วงสีแดงไว้ (ประมาณ {0}% จากซ้าย, {1}% จากบน): " },
        ["vibe.ask.failed"] = new() { ["en"] = "Could not mark the picture: {0}", ["th"] = "วงจุดบนภาพไม่ได้: {0}" },

        // ─── context gauge in the composer strip ───
        ["vibe.ctx.chip"] = new() { ["en"] = "Memory {0}%", ["th"] = "ความจำ {0}%" },
        ["vibe.ctx.tip"] = new() { ["en"] = "How full the AI's working memory (context) is for this chat: {0} tokens.\nAt {1}% CluadeX suggests a compaction (a summary replaces the old part of the chat); about {2} more turns like the last ones before then.", ["th"] = "ความจำใช้งาน (context) ของ AI ในแชทนี้เต็มแค่ไหน: {0} โทเคน\nที่ {1}% CluadeX จะชวนย่อแชท (สรุปส่วนเก่าแทนของเดิม) อีกราว {2} รอบแบบที่ผ่านมาจะถึงจุดนั้น" },
        ["vibe.ctx.tipPast"] = new() { ["en"] = "How full the AI's working memory (context) is for this chat: {0} tokens.\nPast {1}%: compact now (the box above the composer) to keep answers sharp.", ["th"] = "ความจำใช้งาน (context) ของ AI ในแชทนี้เต็มแค่ไหน: {0} โทเคน\nเกิน {1}% แล้ว: ย่อแชทตอนนี้ (กล่องเหนือช่องพิมพ์) เพื่อให้ตอบได้แม่นเหมือนเดิม" },
        ["vibe.ctx.tipFew"] = new() { ["en"] = "How full the AI's working memory (context) is for this chat: {0} tokens.\nAt {1}% CluadeX suggests a compaction (a summary replaces the old part of the chat).", ["th"] = "ความจำใช้งาน (context) ของ AI ในแชทนี้เต็มแค่ไหน: {0} โทเคน\nที่ {1}% CluadeX จะชวนย่อแชท (สรุปส่วนเก่าแทนของเดิม)" },
    };
}
