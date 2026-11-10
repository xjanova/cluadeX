namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the learning loop's card in the chat ("remember this next time?") and the
/// Instincts page's project label. Every key has "en" and "th".</summary>
internal static class InstinctKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["instinct.card.title"] = new() { ["en"] = "Remember this for next time?", ["th"] = "จำสิ่งนี้ไว้ใช้ครั้งหน้าไหม" },
        ["instinct.card.from"] = new() { ["en"] = "Learned from the chat “{0}”", ["th"] = "เรียนรู้จากแชท “{0}”" },
        ["instinct.card.yes"] = new() { ["en"] = "✓ Remember", ["th"] = "✓ จำไว้" },
        ["instinct.card.no"] = new() { ["en"] = "✗ No", ["th"] = "✗ ไม่ต้อง" },
        ["instinct.card.yesTip"] = new() { ["en"] = "Yes, this is how I work — the AI gets it in every chat it applies to from now on.", ["th"] = "ใช่ ทำแบบนี้ — AI จะได้รับข้อนี้ในทุกแชทที่เกี่ยวข้องตั้งแต่ตอนนี้" },
        ["instinct.card.noTip"] = new() { ["en"] = "No, that was a one-off or wrong — it never goes into the AI's instructions.", ["th"] = "ไม่ใช่ เป็นแค่ครั้งเดียวหรือเข้าใจผิด — จะไม่ใส่ลงในคำสั่งของ AI อีก" },
        ["instinct.card.accepted"] = new() { ["en"] = "remembered", ["th"] = "จำไว้แล้ว" },
        ["instinct.card.rejected"] = new() { ["en"] = "won't be used", ["th"] = "จะไม่ใช้" },
        ["instinct.card.gone"] = new() { ["en"] = "deleted", ["th"] = "ถูกลบแล้ว" },
        ["instinct.card.scopeGlobal"] = new() { ["en"] = "every project", ["th"] = "ทุกโปรเจกต์" },
        ["instinct.card.scopeProject"] = new() { ["en"] = "only in {0}", ["th"] = "เฉพาะโปรเจกต์ {0}" },

        // BrainX is the one memory (InstinctService.PausedForBrain): the Instincts page and Settings say why it is off.
        ["instincts.paused.title"] = new()
        {
            ["en"] = "Instinct learning is off — BrainX is this PC's memory",
            ["th"] = "ปิดการเรียนรู้ Instinct ไว้ — เครื่องนี้ใช้ BrainX เป็นความจำ",
        },
        ["instincts.paused.body"] = new()
        {
            ["en"] = "One memory, not two. BrainX remembers lessons better: you can search them, they follow you to every machine, it warns before a file with a past lesson is edited, and it keeps playbooks. With both on, the same rule lives in two places, goes into the prompt twice every turn, costs an extra model call per chat, and the two copies can end up saying different things. So CluadeX learns nothing from chats, puts no instincts into prompts and shows no ✓/✗ card. The instincts already saved stay as they are. Remove BrainX and instinct learning comes back by itself.",
            ["th"] = "ใช้ความจำชุดเดียว ไม่ใช่สองชุด BrainX จำบทเรียนได้ดีกว่า: ค้นได้ ใช้ได้ทุกเครื่อง เตือนก่อนแก้ไฟล์ที่เคยมีบทเรียน และมี playbook ถ้าเปิดทั้งสองระบบ กฎข้อเดียวกันจะอยู่สองที่ ถูกใส่ในพรอมต์ซ้ำทุกเทิร์น ต้องเรียกโมเดลเพิ่มทุกแชท และวันหนึ่งสองที่อาจพูดไม่ตรงกัน ตอนนี้ CluadeX จึงไม่ดึงบทเรียนจากแชท ไม่ใส่ Instinct ในพรอมต์ และไม่ถามด้วยการ์ด ✓/✗ ข้อที่เก็บไว้แล้วยังอยู่ครบ ถ้าเอา BrainX ออก การเรียนรู้ Instinct จะกลับมาทำงานเอง",
        },
        ["settings.learnInstinctsPaused"] = new()
        {
            ["en"] = "Not active now: BrainX is this PC's one memory — with both on, every rule would be kept twice. It comes back by itself if BrainX is removed.",
            ["th"] = "ไม่ทำงานตอนนี้: เครื่องนี้ใช้ BrainX เป็นความจำชุดเดียว — ถ้าเปิดทั้งสองระบบ กฎทุกข้อจะซ้ำกันสองที่ เอา BrainX ออกแล้วจะกลับมาทำงานเอง",
        },
    };
}
