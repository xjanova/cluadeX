namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the desktop_* tools (Services/Desktop): Settings ▸ Agent card, the gate, the
/// permission prompt. Every key needs both "en" and "th".</summary>
internal static class DesktopKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // ── Settings ▸ Agent ──
        ["settings.card.desktop"] = new() { ["en"] = "Desktop control", ["th"] = "ควบคุมโปรแกรมบนเครื่อง" },
        ["settings.desktop"] = new() { ["en"] = "Let the agent drive desktop apps", ["th"] = "ให้เอเจนต์ควบคุมโปรแกรมบนเดสก์ท็อป" },
        ["settings.desktopDesc"] = new()
        {
            ["en"] = "The desktop_* tools: list windows, take screenshots, read and click UI elements, type and launch programs — "
                   + "for testing the app or game you are building. Each app asks once per run (or \"Allow in this chat\"). "
                   + "Password fields, sign-in / UAC windows and CluadeX's own approval prompts are never touched.",
            ["th"] = "เครื่องมือ desktop_*: ดูรายการหน้าต่าง ถ่ายภาพหน้าจอ อ่านและกดปุ่มใน UI พิมพ์ข้อความ และเปิดโปรแกรม — "
                   + "สำหรับทดสอบแอปหรือเกมที่กำลังสร้าง แต่ละโปรแกรมจะถามก่อนหนึ่งครั้งต่อรอบงาน (หรือเลือก \"อนุญาตทั้งแชทนี้\") "
                   + "ช่องรหัสผ่าน หน้าต่างลงชื่อเข้าใช้ / UAC และปุ่มอนุมัติของ CluadeX เองจะไม่ถูกแตะต้องเด็ดขาด",
        },

        // ── gate ──
        ["desktop.off"] = new()
        {
            ["en"] = "Desktop control is switched off. Turn it on in Settings ▸ Agent ▸ Desktop control.",
            ["th"] = "การควบคุมโปรแกรมบนเครื่องถูกปิดอยู่ เปิดได้ที่ ตั้งค่า ▸ เอเจนต์ ▸ ควบคุมโปรแกรมบนเครื่อง",
        },
        ["desktop.windowsOnly"] = new()
        {
            ["en"] = "The desktop tools work on Windows only.",
            ["th"] = "เครื่องมือควบคุมเดสก์ท็อปใช้ได้บน Windows เท่านั้น",
        },

        // ── permission prompt ──
        ["desktop.perm.act"] = new()
        {
            ["en"] = "The agent wants to see and control the app \"{0}\" ({1}).",
            ["th"] = "เอเจนต์ขอดูและควบคุมโปรแกรม \"{0}\" ({1})",
        },
        ["desktop.perm.launch"] = new() { ["en"] = "Start a program:", ["th"] = "เปิดโปรแกรม:" },
        ["desktop.perm.clipboardGet"] = new()
        {
            ["en"] = "The agent wants to read the text on the clipboard (it may hold something you copied elsewhere).",
            ["th"] = "เอเจนต์ขออ่านข้อความในคลิปบอร์ด (อาจมีสิ่งที่คุณคัดลอกไว้จากที่อื่น)",
        },
        ["desktop.perm.clipboardSet"] = new()
        {
            ["en"] = "The agent wants to put text on the clipboard (replacing what is there).",
            ["th"] = "เอเจนต์ขอวางข้อความลงคลิปบอร์ด (แทนที่ของเดิม)",
        },

        // ── Settings ▸ Agent: blocked apps, the stop hotkey ──
        ["settings.desktopBlocked"] = new() { ["en"] = "Apps the agent never touches", ["th"] = "โปรแกรมที่เอเจนต์ห้ามแตะ" },
        ["settings.desktopBlockedDesc"] = new()
        {
            ["en"] = "Process names, comma-separated; * matches anything (KeePass*). Password managers are listed by default.",
            ["th"] = "ชื่อโปรเซส คั่นด้วยจุลภาค ใช้ * แทนอะไรก็ได้ (KeePass*) — ใส่โปรแกรมจัดการรหัสผ่านไว้ให้แล้วตั้งแต่แรก",
        },
        ["settings.desktopHotkey"] = new()
        {
            ["en"] = "Ctrl+Alt+End stops every running chat at once, from any app — use it when the agent is moving your mouse.",
            ["th"] = "กด Ctrl+Alt+End เพื่อหยุดทุกแชทที่กำลังทำงานทันที จากโปรแกรมไหนก็ได้ — ใช้ตอนเอเจนต์กำลังขยับเมาส์ของคุณ",
        },
        ["desktop.hotkeyStopped"] = new() { ["en"] = "⏹ Stopped by Ctrl+Alt+End", ["th"] = "⏹ หยุดแล้วด้วย Ctrl+Alt+End" },
    };
}
