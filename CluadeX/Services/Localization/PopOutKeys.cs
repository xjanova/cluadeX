namespace CluadeX.Services.Localization;

/// <summary>Localized strings for a page in a window of its own (Views/PopOut: Node Graph, UI Studio, Code Editor).
/// Every key needs both "en" and "th". Product names stay in English in Thai too.</summary>
internal static class PopOutKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["popout.button.tip"] = new()
        {
            ["en"] = "Open in its own window — put it on another screen; the chat stays here",
            ["th"] = "เปิดในหน้าต่างแยก — ย้ายไปไว้อีกจอได้ แชทยังอยู่ที่หน้าต่างหลัก",
        },
        ["popout.back.tip"] = new() { ["en"] = "Put back in the main window", ["th"] = "ย้ายกลับไปไว้ในหน้าต่างหลัก" },

        ["popout.title.graph"] = new() { ["en"] = "Node Graph — CluadeX", ["th"] = "Node Graph — CluadeX" },
        ["popout.title.studio"] = new() { ["en"] = "UI Studio — CluadeX", ["th"] = "UI Studio — CluadeX" },
        ["popout.title.editor"] = new() { ["en"] = "Code Editor — CluadeX", ["th"] = "อีดิเตอร์โค้ด — CluadeX" },

        ["popout.name.graph"] = new() { ["en"] = "Node Graph", ["th"] = "Node Graph" },
        ["popout.name.studio"] = new() { ["en"] = "UI Studio", ["th"] = "UI Studio" },
        ["popout.name.editor"] = new() { ["en"] = "Code Editor", ["th"] = "อีดิเตอร์โค้ด" },

        ["popout.placeholder.title"] = new() { ["en"] = "Open in its own window", ["th"] = "เปิดอยู่ในหน้าต่างแยก" },
        ["popout.placeholder.hint"] = new()
        {
            ["en"] = "Watch it there while you chat here. Close that window, or press Bring back, to put it in this window again.",
            ["th"] = "ดูงานที่หน้าต่างนั้นไปพร้อมกับแชทที่นี่ได้ ปิดหน้าต่างนั้นหรือกด ดึงกลับมา เพื่อย้ายกลับมาไว้ที่นี่",
        },
        ["popout.placeholder.back"] = new() { ["en"] = "Bring back", ["th"] = "ดึงกลับมา" },
        ["popout.placeholder.show"] = new() { ["en"] = "Show the window", ["th"] = "ไปที่หน้าต่างนั้น" },

        ["popout.failed"] = new()
        {
            ["en"] = "The page could not be opened in its own window. It stays in the main window.",
            ["th"] = "เปิดหน้านี้ในหน้าต่างแยกไม่สำเร็จ หน้านี้ยังอยู่ในหน้าต่างหลักตามเดิม",
        },
    };
}
