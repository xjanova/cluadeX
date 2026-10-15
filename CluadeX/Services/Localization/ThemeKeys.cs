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
        ["theme.neon.name"] = new() { ["en"] = "Neon Cyber LED", ["th"] = "Neon Cyber LED" },
        ["theme.neon.desc"] = new()
        {
            ["en"] = "Dark and glowing — neon icons, LED-style meters.",
            ["th"] = "มืด เรืองแสง ไอคอนนีออน มาตรวัดแบบ LED",
        },
    };
}
