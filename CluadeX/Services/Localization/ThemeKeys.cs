namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the theme picker (Settings › General › Appearance). Every key needs both "en" and "th".</summary>
internal static class ThemeKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["settings.card.appearance"] = new() { ["en"] = "Appearance", ["th"] = "หน้าตาโปรแกรม" },
        ["settings.theme"] = new() { ["en"] = "Choose a theme", ["th"] = "เลือกธีม" },
        ["settings.themeDesc"] = new()
        {
            ["en"] = "Changes the whole app right away — no restart — and is remembered next time.",
            ["th"] = "เปลี่ยนหน้าตาทั้งโปรแกรมทันทีโดยไม่ต้องรีสตาร์ท และจำไว้ใช้ครั้งต่อไป",
        },
        ["settings.themeInProgress"] = new() { ["en"] = "In progress", ["th"] = "กำลังทำ" },
        ["settings.themeInProgressTip"] = new()
        {
            ["en"] = "This theme is still being painted — parts of the app keep the Midnight look for now.",
            ["th"] = "ธีมนี้ยังทำไม่เสร็จ — บางส่วนของโปรแกรมจะยังเป็นหน้าตาแบบ Midnight ไปก่อน",
        },

        ["theme.classic.name"] = new() { ["en"] = "Classic — XP Silver", ["th"] = "คลาสสิก — XP Silver" },
        ["theme.classic.desc"] = new()
        {
            ["en"] = "Silver, bevelled, three-dimensional — easy to read in daylight.",
            ["th"] = "สีเงิน ปุ่มนูน มีมิติ อ่านง่ายตอนกลางวัน",
        },
        ["theme.midnight.name"] = new() { ["en"] = "Midnight (default)", ["th"] = "Midnight (ค่าเริ่มต้น)" },
        ["theme.midnight.desc"] = new()
        {
            ["en"] = "CluadeX's original dark theme in violet and blue.",
            ["th"] = "ธีมมืดแบบเดิมของ CluadeX โทนม่วง-ฟ้า",
        },
        ["settings.palette.title"] = new() { ["en"] = "Lavender Night colours", ["th"] = "สีของธีม Lavender Night" },
        ["settings.palette.desc"] = new() { ["en"] = "Pick a background and an accent — the whole theme follows, status colours keep their meaning.", ["th"] = "เลือกสีพื้นหลังและสีเน้น — ทั้งธีมจะเปลี่ยนตาม ส่วนสีสถานะ (เขียว/เหลือง/แดง) คงความหมายเดิม" },
        ["settings.palette.background"] = new() { ["en"] = "Background", ["th"] = "สีพื้นหลัง" },
        ["settings.palette.accent"] = new() { ["en"] = "Accent", ["th"] = "สีเน้น" },
        ["settings.palette.own"] = new() { ["en"] = "Your own (#hex):", ["th"] = "สีของคุณเอง (#hex):" },
        ["settings.palette.bgHex"] = new() { ["en"] = "Background, e.g. #13061A", ["th"] = "พื้นหลัง เช่น #13061A" },
        ["settings.palette.acHex"] = new() { ["en"] = "Accent, e.g. #A44CFF", ["th"] = "สีเน้น เช่น #A44CFF" },
        ["settings.palette.apply"] = new() { ["en"] = "Apply", ["th"] = "ใช้สีนี้" },
        ["settings.palette.reset"] = new() { ["en"] = "Reset", ["th"] = "คืนค่าเดิม" },
        ["theme.lavender.name"] = new() { ["en"] = "Lavender Night", ["th"] = "ลาเวนเดอร์ราตรี" },
        ["theme.lavender.desc"] = new() { ["en"] = "The CluadeX mascot's room: deep violet night, lavender glass, pink glow and gold", ["th"] = "ห้องของมาสคอต CluadeX: ม่วงราตรี กระจกลาเวนเดอร์ แสงชมพู และทอง" },
        ["theme.neon.name"] = new() { ["en"] = "Neon Cyber LED", ["th"] = "Neon Cyber LED" },
        ["theme.neon.desc"] = new()
        {
            ["en"] = "Dark and glowing — neon icons, LED-style meters.",
            ["th"] = "มืด เรืองแสง ไอคอนนีออน มาตรวัดแบบ LED",
        },
    };
}
