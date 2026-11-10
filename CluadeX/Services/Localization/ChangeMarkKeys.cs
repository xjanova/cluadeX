namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the editor's change marks (CodeEditorView.AiChanges.cs, CodeEditorViewModel.ChangeMarks.cs):
/// the user's own edits in a second colour, the [user-edit] marker comment, the before/after compare and the list of every
/// user-edited spot. Every key needs both "en" and "th".</summary>
internal static class ChangeMarkKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        // {0} how many runs of lines the user edited by hand in this file
        ["cm.user"] = new() { ["en"] = "You edited {0}", ["th"] = "คุณแก้ {0} จุด" },
        ["cm.userTip"] = new()
        {
            ["en"] = "Lines you edited by hand (orange), kept apart from the AI's changes. Click: next one, every one in the project, the [user-edit] comment",
            ["th"] = "บรรทัดที่คุณแก้เอง (สีส้ม) แยกจากที่ AI แก้ คลิก: ไปจุดถัดไป ดูทุกจุดในโปรเจกต์ หรือตั้งค่าคอมเมนต์ [user-edit]",
        },
        ["cm.compare"] = new() { ["en"] = "⇄ Before / after", ["th"] = "⇄ เทียบก่อน-หลัง" },
        ["cm.compareTip"] = new()
        {
            ["en"] = "Compare the whole file with how it was before this chat changed it (F7 / Shift+F7 step through the changes)",
            ["th"] = "เทียบทั้งไฟล์กับตอนก่อนที่แชตนี้แก้ (F7 / Shift+F7 ไปยังจุดที่เปลี่ยนถัดไป / ก่อนหน้า)",
        },
        ["cm.diffPrev"] = new() { ["en"] = "Previous change (Shift+F7)", ["th"] = "จุดที่เปลี่ยนก่อนหน้า (Shift+F7)" },
        ["cm.diffNext"] = new() { ["en"] = "Next change (F7)", ["th"] = "จุดที่เปลี่ยนถัดไป (F7)" },
        // {0} which change, {1} of how many
        ["cm.diffPos"] = new() { ["en"] = "change {0} of {1}", ["th"] = "จุดที่ {0} จาก {1}" },
        // {0} when the chat first changed the file
        ["cm.leftCaption"] = new() { ["en"] = "BEFORE THIS CHAT · {0}", ["th"] = "ก่อนแชตนี้แก้ · {0}" },
        ["cm.leftCaptionDisk"] = new() { ["en"] = "ON DISK (LAST SAVE)", ["th"] = "บนดิสก์ (บันทึกล่าสุด)" },
        ["cm.rightCaption"] = new() { ["en"] = "NOW · IN THE EDITOR", ["th"] = "ตอนนี้ · ใน editor" },
        // {0} lines added, {1} lines removed
        ["cm.diffSummary"] = new() { ["en"] = "+{0} −{1} · against before this chat", ["th"] = "+{0} −{1} · เทียบกับก่อนแชตนี้แก้" },
        ["cm.diffSummaryDisk"] = new() { ["en"] = "+{0} −{1} · not saved yet", ["th"] = "+{0} −{1} · ยังไม่ได้บันทึก" },
        ["cm.noBaseline"] = new()
        {
            ["en"] = "No chat has changed this file and nothing is unsaved: nothing to compare",
            ["th"] = "ยังไม่มีแชตไหนแก้ไฟล์นี้ และไม่มีการแก้ที่ยังไม่บันทึก จึงไม่มีอะไรให้เทียบ",
        },
        ["cm.menuNext"] = new() { ["en"] = "Next place you edited", ["th"] = "ไปจุดที่คุณแก้ถัดไป" },
        ["cm.menuPrev"] = new() { ["en"] = "Previous place you edited", ["th"] = "ไปจุดที่คุณแก้ก่อนหน้า" },
        ["cm.menuList"] = new() { ["en"] = "Show every user edit in the project", ["th"] = "แสดงจุดที่ผู้ใช้แก้ทั้งโปรเจกต์" },
        ["cm.menuMarkers"] = new() { ["en"] = "Add a [user-edit] comment when saving", ["th"] = "ใส่คอมเมนต์ [user-edit] ตอนบันทึก" },
        ["cm.menuMarkersNote"] = new()
        {
            ["en"] = "At the end of the first edited line, in the file's comment syntax; never in strings, JSON or files without comments",
            ["th"] = "ใส่ท้ายบรรทัดแรกที่แก้ ตามรูปแบบคอมเมนต์ของภาษานั้น ไม่ใส่ใน string, JSON หรือไฟล์ที่ไม่มีคอมเมนต์",
        },
        ["cm.markersOn"] = new() { ["en"] = "Saving now adds a [user-edit] comment to lines you edit", ["th"] = "ตอนบันทึกจะใส่คอมเมนต์ [user-edit] ให้บรรทัดที่คุณแก้" },
        ["cm.markersOff"] = new() { ["en"] = "No more [user-edit] comments: your edits are still remembered and coloured", ["th"] = "ไม่ใส่คอมเมนต์ [user-edit] แล้ว แต่ยังจำและระบายสีจุดที่คุณแก้เหมือนเดิม" },
        // {0} how many places, {1} the tag
        ["cm.listContext"] = new() { ["en"] = "Places the user edited by hand: {0} · tag {1}", ["th"] = "จุดที่ผู้ใช้แก้เอง: {0} จุด · แท็ก {1}" },
        ["cm.searching"] = new() { ["en"] = "Looking for the places you edited…", ["th"] = "กำลังค้นหาจุดที่คุณแก้…" },
        ["cm.searchFailed"] = new() { ["en"] = "Could not list the places you edited", ["th"] = "แสดงรายการจุดที่คุณแก้ไม่ได้" },
        ["cm.noProject"] = new() { ["en"] = "Open a project folder first", ["th"] = "เปิดโฟลเดอร์โปรเจกต์ก่อน" },
        // {0} which change, {1} of how many, {2}/{3} its lines
        ["cm.peekUser"] = new() { ["en"] = "Your edit {0} of {1} · lines {2}–{3}", ["th"] = "จุดที่คุณแก้ {0} จาก {1} · บรรทัด {2}–{3}" },
        ["cm.peekUserRemoved"] = new() { ["en"] = "Your edit {0} of {1} · lines deleted before line {2}", ["th"] = "จุดที่คุณแก้ {0} จาก {1} · ลบบรรทัดก่อนบรรทัด {2}" },
        ["cm.yourLines"] = new() { ["en"] = "Your lines", ["th"] = "บรรทัดที่คุณแก้" },
        ["cm.partial"] = new()
        {
            ["en"] = "You edited some of these lines too, so this part cannot be put back alone: use Before / after to see the whole file.",
            ["th"] = "คุณแก้บางบรรทัดในจุดนี้ด้วย จึงย้อนกลับเฉพาะส่วนนี้ไม่ได้ ใช้ เทียบก่อน-หลัง เพื่อดูทั้งไฟล์",
        },
        // {0} file name, {1} how many markers
        ["cm.saved"] = new() { ["en"] = "Saved {0} · [user-edit] added at {1} place(s)", ["th"] = "บันทึก {0} แล้ว · ใส่ [user-edit] {1} จุด" },
    };
}
