namespace CluadeX.Services.Localization;

/// <summary>Localized strings for smart skill picking: the composer's switch and its row, the Skills page's gauge, the
/// brain's help and the per-skill cost / pin. Every key has "en" and "th".</summary>
internal static class SkillPickKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ─── the composer ───
        ["chat.skillPick"] = new() { ["en"] = "Skills", ["th"] = "Skill อัตโนมัติ" },
        ["chat.skillPickTip"] = new() { ["en"] = "Smart skills: before the AI starts, CluadeX picks the switched-on skills that fit this task (your words, the brain, what was used for tasks like it) and gives it only those — the rest cost no tokens. Off = every switched-on skill is listed on every turn. The Skills page sets the default for new chats.", ["th"] = "Skill อัตโนมัติ: ก่อน AI เริ่มทำงาน CluadeX จะเลือก skill ที่เปิดไว้และเข้ากับงานนี้ (จากคำในงาน, สมอง, และที่เคยใช้กับงานแบบเดียวกัน) แล้วส่งให้เฉพาะตัวนั้น ตัวอื่นไม่กินโทเค็น ปิด = ส่งรายการ skill ที่เปิดไว้ทั้งหมดทุกเทิร์น ค่าเริ่มต้นของแชทใหม่ตั้งได้ที่หน้า Skill" },
        ["chat.skillPick.row"] = new() { ["en"] = "Skills for this task: {0} — {1} of {2} switched on · ≈ {3} tokens instead of {4}", ["th"] = "เลือก skill ให้งานนี้: {0} — {1} จาก {2} ตัวที่เปิดไว้ · ≈ {3} โทเค็น แทน {4}" },
        ["chat.skillPick.last"] = new() { ["en"] = "Last turn: {0}", ["th"] = "เทิร์นล่าสุด: {0}" },
        ["chat.skillPick.none"] = new() { ["en"] = "no skill fitted (saved ≈ {0} tokens)", ["th"] = "ไม่มี skill ที่เข้ากับงาน (ประหยัด ≈ {0} โทเค็น)" },
        ["skill.why.named"] = new() { ["en"] = "you named it", ["th"] = "สั่งชื่อ" },
        ["skill.why.pinned"] = new() { ["en"] = "every task", ["th"] = "ใช้ทุกงาน" },
        ["skill.why.matched"] = new() { ["en"] = "fits the words", ["th"] = "ตรงกับงาน" },
        ["skill.why.learned"] = new() { ["en"] = "used for tasks like this", ["th"] = "เคยใช้กับงานแบบนี้" },
        ["skill.why.brain"] = new() { ["en"] = "the brain suggests it", ["th"] = "สมองแนะนำ" },

        // ─── the Skills page: gauge ───
        ["skills.gaugeTitle"] = new() { ["en"] = "SKILL TOKENS PER TURN", ["th"] = "โทเค็นที่ SKILL กินต่อเทิร์น" },
        ["skill.gauge.used"] = new() { ["en"] = "{0} offered ≈ {1} tokens on every turn — {2}% of {3}'s {4}-token window", ["th"] = "เปิดไว้ {0} ตัว ≈ {1} โทเค็นทุกเทิร์น — {2}% ของหน้าต่าง {4} โทเค็นของ {3}" },
        ["skill.gauge.cap"] = new() { ["en"] = "Recommended ceiling: {0} tokens (1% of the window) — the line in the middle of the bar", ["th"] = "เพดานแนะนำ: {0} โทเค็น (1% ของหน้าต่าง) — เส้นกลางแถบ" },
        ["skill.gauge.empty"] = new() { ["en"] = "No skill switched on", ["th"] = "ยังไม่ได้เปิด skill" },
        ["skill.gauge.light"] = new() { ["en"] = "Light — fine to keep them all on", ["th"] = "เบา — เปิดไว้ทั้งหมดได้" },
        ["skill.gauge.near"] = new() { ["en"] = "Near the ceiling", ["th"] = "ใกล้เพดานแล้ว" },
        ["skill.gauge.over"] = new() { ["en"] = "Over the ceiling — every turn pays for skills it does not use. Turn smart picking on, or switch some off.", ["th"] = "เกินเพดาน — ทุกเทิร์นจ่ายค่า skill ที่ไม่ได้ใช้ เปิดเลือกอัตโนมัติ หรือปิดบางตัว" },
        ["skill.gauge.overPicking"] = new() { ["en"] = "Over the ceiling as a full list — smart picking keeps each turn under it", ["th"] = "ถ้าส่งทั้งรายการจะเกินเพดาน — การเลือกอัตโนมัติทำให้แต่ละเทิร์นไม่เกิน" },
        ["skill.gauge.picking"] = new() { ["en"] = "Smart picking is on: each turn is given only the skills that fit it (at most {0}, ≤ {1} tokens). The bar is what listing them all would cost.", ["th"] = "เลือกอัตโนมัติเปิดอยู่: แต่ละเทิร์นได้เฉพาะ skill ที่เข้ากับงาน (ไม่เกิน {0} ตัว, ≤ {1} โทเค็น) แถบนี้คือราคาถ้าส่งทั้งรายการ" },
        ["skill.gauge.cli"] = new() { ["en"] = "The model picked is a CLI agent (Claude Dev, Codex, Gemini Dev…): it uses its own skills, so these cost it nothing.", ["th"] = "โมเดลที่เลือกอยู่เป็น CLI agent (Claude Dev, Codex, Gemini Dev…) ซึ่งใช้ skill ของตัวเอง skill ตรงนี้จึงไม่กินโทเค็นของมัน" },
        ["skill.gauge.local"] = new() { ["en"] = "Small local window: the compact prompt lists no skills; smart picking gives a turn at most 2.", ["th"] = "หน้าต่างของโมเดล local เล็ก: prompt แบบย่อไม่ใส่รายการ skill และเลือกอัตโนมัติให้ได้ไม่เกิน 2 ตัวต่อเทิร์น" },
        ["skill.gauge.heavy"] = new() { ["en"] = "{0} switched-on skill(s) take over a tenth of this model's window when they run (marked heavy).", ["th"] = "มี {0} skill ที่ตอนใช้งานกินเกิน 1 ใน 10 ของหน้าต่างโมเดลนี้ (ติดป้าย หนัก)" },
        ["skills.autoPick"] = new() { ["en"] = "Pick skills for each task (default for chats)", ["th"] = "เลือก skill ให้ตรงกับแต่ละงาน (ค่าเริ่มต้นของแชท)" },
        ["skills.autoPickTip"] = new() { ["en"] = "Each turn gives the AI only the switched-on skills that fit the task, attached to the message (the system prompt stays the same, so the prompt cache holds). A skill it was not given can still run: by /command, or the AI asks for the list. Each chat's composer has its own switch.", ["th"] = "แต่ละเทิร์นส่งให้ AI เฉพาะ skill ที่เปิดไว้และเข้ากับงาน แนบไปกับข้อความ (system prompt ไม่เปลี่ยน prompt cache จึงไม่หลุด) skill ที่ไม่ได้ส่งให้ยังใช้ได้ ผ่าน /คำสั่ง หรือให้ AI ขอดูรายการ ช่องแชทแต่ละแชทมีสวิตช์ของตัวเอง" },
        ["skills.brainIndex"] = new() { ["en"] = "Let the brain help pick", ["th"] = "ให้สมองช่วยเลือก" },
        ["skills.brainIndexTip"] = new() { ["en"] = "With a BrainX brain connected, CluadeX keeps a short card per skill in it (Notes/CluadeX-Skills, tag cluadex-skill — name, description, when to use; never the prompt) and asks its semantic search which cards fit each task. It reads Thai and paraphrases better. No brain = the pick uses this PC only; the prompt a skill runs always comes from its file here.", ["th"] = "เมื่อต่อสมอง BrainX อยู่ CluadeX จะเก็บการ์ดสั้นของแต่ละ skill ไว้ในสมอง (Notes/CluadeX-Skills แท็ก cluadex-skill มีชื่อ คำอธิบาย และใช้เมื่อไร ไม่มี prompt) แล้วให้สมองค้นแบบ semantic ว่าการ์ดไหนเข้ากับงาน เข้าใจภาษาไทยและคำพูดอ้อมได้ดีกว่า ถ้าไม่มีสมองจะเลือกจากในเครื่องอย่างเดียว ส่วน prompt ที่ skill ใช้จริงอ่านจากไฟล์ในเครื่องเสมอ" },
        ["skill.brain.off"] = new() { ["en"] = "Off — picking uses this PC only", ["th"] = "ปิดอยู่ — เลือกจากในเครื่องอย่างเดียว" },
        ["skill.brain.notConnected"] = new() { ["en"] = "Brain not connected — picking uses this PC only", ["th"] = "ยังไม่ได้ต่อสมอง — เลือกจากในเครื่องอย่างเดียว" },
        ["skill.brain.ready"] = new() { ["en"] = "Brain connected · cards of all {0} offered skills are in it", ["th"] = "ต่อสมองแล้ว · การ์ดของ skill ที่เปิดไว้ครบ {0} ตัว" },
        ["skill.brain.pending"] = new() { ["en"] = "Brain connected · {0} card(s) still to write — written on the next turn", ["th"] = "ต่อสมองแล้ว · ยังมีการ์ดที่ต้องเขียน {0} ใบ จะเขียนตอนเทิร์นถัดไป" },

        // ─── the Skills page: per skill ───
        ["skill.cost"] = new() { ["en"] = "≈ {0} tok per turn · ≈ {1} tok when it runs", ["th"] = "≈ {0} โทเค็น/เทิร์น · ≈ {1} โทเค็นตอนใช้งาน" },
        ["skills.costTip"] = new() { ["en"] = "Per turn: its line in the list, paid on every turn it is offered. When it runs: its whole prompt, paid only on the turn it is used.", ["th"] = "ต่อเทิร์น: บรรทัดของมันในรายการ จ่ายทุกเทิร์นที่ส่งให้ AI · ตอนใช้งาน: prompt ทั้งหมดของมัน จ่ายเฉพาะเทิร์นที่ถูกเรียกใช้" },
        ["skills.heavy"] = new() { ["en"] = "heavy", ["th"] = "หนัก" },
        ["skills.pin"] = new() { ["en"] = "Every task", ["th"] = "ใช้ทุกงาน" },
        ["skills.pinTip"] = new() { ["en"] = "Every task: offered on every turn while smart picking is on, whatever the task.", ["th"] = "ใช้ทุกงาน: ส่งให้ AI ทุกเทิร์นระหว่างที่เลือกอัตโนมัติเปิดอยู่ ไม่ว่างานจะเป็นอะไร" },
    };
}
