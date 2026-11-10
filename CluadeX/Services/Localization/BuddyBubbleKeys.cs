namespace CluadeX.Services.Localization;

/// <summary>น้อง CluadeX's speech bubble (MainWindow.Buddy.cs): the tips she offers now and then, and her word when a turn
/// ends or waits. Every tip names a feature that exists — check the code before adding one. Every key needs "en" and "th".</summary>
internal static class BuddyBubbleKeys
{
    /// <summary>The tip keys, in the order she offers them (one at a time, round and round).</summary>
    internal static readonly string[] Tips =
    {
        "buddy.tip.palette", "buddy.tip.compact", "buddy.tip.textSize", "buddy.tip.home", "buddy.tip.projects", "buddy.tip.theme",
    };

    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["buddy.bubble.closeTip"] = new() { ["en"] = "Click to close", ["th"] = "คลิกเพื่อปิด" },
        ["buddy.sound.muteTip"] = new() { ["en"] = "Mute CluadeX", ["th"] = "ปิดเสียงน้อง CluadeX" },
        ["buddy.sound.unmuteTip"] = new() { ["en"] = "Unmute CluadeX", ["th"] = "เปิดเสียงน้อง CluadeX" },
        ["buddy.menu.sound"] = new() { ["en"] = "Her sound", ["th"] = "เสียงของน้อง" },
        ["buddy.menu.clips"] = new() { ["en"] = "Moving clips (normal)", ["th"] = "คลิปเคลื่อนไหว (ปกติ)" },
        ["buddy.menu.stills"] = new() { ["en"] = "Still picture only", ["th"] = "ภาพนิ่งอย่างเดียว" },
        ["buddy.menu.slides"] = new() { ["en"] = "Slideshow of the girls (waist up)", ["th"] = "สไลด์สาวๆ ทีละคน (ครึ่งตัว)" },

        ["buddy.tip.palette"] = new()
        {
            ["en"] = "Tip: Ctrl+K opens the command list — a new chat, open a folder, Settings and more, by typing.",
            ["th"] = "เคล็ดลับ: กด Ctrl+K เปิดรายการคำสั่ง พิมพ์หาได้เลย ทั้งแชทใหม่ เปิดโฟลเดอร์ ตั้งค่า และอื่น ๆ ค่ะ",
        },
        ["buddy.tip.compact"] = new()
        {
            ["en"] = "Long chat? Ctrl+K → \"Compact conversation\" sums up the older messages for the AI — nothing is deleted.",
            ["th"] = "แชทยาวแล้วใช่ไหมคะ? Ctrl+K → \"บีบอัดบทสนทนา\" สรุปข้อความเก่าให้ AI อ่านสั้นลง โดยไม่ลบอะไรเลย",
        },
        ["buddy.tip.textSize"] = new()
        {
            ["en"] = "Text too small or too big? Settings › General › Text size and font zooms the whole window.",
            ["th"] = "ตัวหนังสือเล็กหรือใหญ่ไปไหมคะ? ไปที่ ตั้งค่า › ทั่วไป › ขนาดตัวอักษรและฟอนต์ ซูมได้ทั้งหน้าต่างเลย",
        },
        ["buddy.tip.home"] = new()
        {
            ["en"] = "Click me any time to go back Home — chats that are running keep going.",
            ["th"] = "คลิกหนูเมื่อไหร่ก็ได้เพื่อกลับหน้า Home นะคะ แชทที่รันอยู่ยังทำงานต่อ",
        },
        ["buddy.tip.projects"] = new()
        {
            ["en"] = "Keep chats tidy: drag one onto a project, or right-click it, to file it there.",
            ["th"] = "จัดแชทให้เป็นระเบียบได้นะคะ ลากแชทไปวางบนโปรเจกต์ หรือคลิกขวาที่แชท เพื่อย้ายเข้าโปรเจกต์",
        },
        ["buddy.tip.theme"] = new()
        {
            ["en"] = "Want a different look? Settings › General › Appearance has four themes, light and dark.",
            ["th"] = "อยากเปลี่ยนบรรยากาศไหมคะ? ตั้งค่า › ทั่วไป › หน้าตาโปรแกรม มีธีมให้เลือก 4 แบบ ทั้งสว่างและมืด",
        },

        ["buddy.say.happy"] = new() { ["en"] = "All done! ✨", ["th"] = "เสร็จแล้วค่ะ ✨" },
        ["buddy.say.sad"] = new()
        {
            ["en"] = "Something went wrong — the chat says what.",
            ["th"] = "มีบางอย่างผิดพลาดค่ะ ดูรายละเอียดในแชทได้เลย",
        },
        ["buddy.say.waiting"] = new() { ["en"] = "I need your answer to go on.", ["th"] = "รอคุณตอบอยู่นะคะ ตอบแล้วหนูทำต่อเลย" },

        ["buddy.bubbles"] = new()
        {
            ["en"] = "น้อง CluadeX speaks up — a short tip now and then, and a word when a chat finishes or waits for you",
            ["th"] = "ให้น้อง CluadeX ทักเป็นบับเบิ้ล — เคล็ดลับสั้น ๆ เป็นครั้งคราว และบอกเมื่อแชทเสร็จหรือรอคุณตอบ",
        },
        ["buddy.sounds"] = new()
        {
            ["en"] = "น้อง CluadeX's voice — little sounds as she moves, and a short word (in English) when a chat finishes, needs you, or you have been away a while",
            ["th"] = "เสียงน้อง CluadeX — เสียงเล็ก ๆ ตอนขยับ และพูดสั้น ๆ (ภาษาอังกฤษ) เมื่อแชทเสร็จ ต้องให้คุณตัดสินใจ หรือคุณไม่อยู่หน้าจอนาน",
        },
    };
}
