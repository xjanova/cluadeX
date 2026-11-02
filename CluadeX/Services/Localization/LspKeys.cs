namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the Code Editor's live errors: the Problems panel, the status-bar item, hover and the
/// language-server install hints (EditorLanguageService). Every key needs both "en" and "th".</summary>
internal static class LspKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["lsp.problems"] = new() { ["en"] = "PROBLEMS", ["th"] = "ปัญหา" },
        ["lsp.problemsTip"] = new() { ["en"] = "Errors and warnings from the language server: click for the list", ["th"] = "ข้อผิดพลาดและคำเตือนจาก language server: คลิกเพื่อดูรายการ" },
        ["lsp.close"] = new() { ["en"] = "Hide problems", ["th"] = "ซ่อนรายการปัญหา" },
        ["lsp.copy"] = new() { ["en"] = "Copy", ["th"] = "คัดลอก" },
        ["lsp.copied"] = new() { ["en"] = "Install command copied", ["th"] = "คัดลอกคำสั่งติดตั้งแล้ว" },
        ["lsp.runInTerminal"] = new() { ["en"] = "Run in terminal", ["th"] = "รันใน Terminal" },
        ["lsp.runInTerminalTip"] = new() { ["en"] = "Type the install command into the terminal below and run it. Press Check again when it finishes.", ["th"] = "ใส่คำสั่งติดตั้งลง Terminal ด้านล่างแล้วรัน เสร็จแล้วกด ตรวจอีกครั้ง" },
        ["lsp.openPage"] = new() { ["en"] = "How to install", ["th"] = "วิธีติดตั้ง" },
        ["lsp.checkAgain"] = new() { ["en"] = "Check again", ["th"] = "ตรวจอีกครั้ง" },
        ["lsp.restart"] = new() { ["en"] = "Restart server", ["th"] = "เริ่ม server ใหม่" },
        ["lsp.askAiTip"] = new() { ["en"] = "Ask AI to fix this: puts the error and the lines around it in the chat box", ["th"] = "ให้ AI แก้: ใส่ข้อผิดพลาดพร้อมโค้ดรอบ ๆ ลงช่องแชต" },
        ["lsp.askAiDone"] = new() { ["en"] = "The error is in the chat box: check it and press Enter to send", ["th"] = "ใส่ข้อผิดพลาดลงช่องแชตแล้ว: ตรวจดูแล้วกด Enter เพื่อส่ง" },
        // {0} severity word, {1} who reported it, {2} file, {3} line, {4} message, {5} the numbered lines around it
        ["lsp.askAiPrompt"] = new()
        {
            ["en"] = "Fix this {0} that {1} reports in {2}, line {3}:\n{4}\n\nThe code around it (\">\" marks the line):\n```\n{5}\n```\nChange only what the fix needs.",
            ["th"] = "ช่วยแก้{0}ที่ {1} แจ้งใน {2} บรรทัด {3}:\n{4}\n\nโค้ดรอบ ๆ (\">\" คือบรรทัดที่มีปัญหา):\n```\n{5}\n```\nแก้เฉพาะที่จำเป็นต่อการแก้ปัญหานี้",
        },
        ["lsp.word.error"] = new() { ["en"] = "error", ["th"] = "ข้อผิดพลาด" },
        ["lsp.word.warning"] = new() { ["en"] = "warning", ["th"] = "คำเตือน" },
        ["lsp.word.info"] = new() { ["en"] = "note", ["th"] = "ข้อสังเกต" },
        ["lsp.theServer"] = new() { ["en"] = "the language server", ["th"] = "language server" },

        // Status bar / panel header — {0} is the language ("C#"), {1} the server ("csharp-ls")
        ["lsp.st.starting"] = new() { ["en"] = "{0}: starting checks…", ["th"] = "{0}: กำลังเริ่มตรวจ…" },
        ["lsp.st.notInstalled"] = new() { ["en"] = "{0}: no language server", ["th"] = "{0}: ไม่มี language server" },
        ["lsp.st.failed"] = new() { ["en"] = "{0}: server stopped", ["th"] = "{0}: server หยุดทำงาน" },
        ["lsp.st.tooLarge"] = new() { ["en"] = "Live checks off: file too large", ["th"] = "ปิดการตรวจสด: ไฟล์ใหญ่เกิน" },
        ["lsp.st.running"] = new() { ["en"] = "{0} · {1}", ["th"] = "{0} · {1}" },
        ["lsp.st.counts"] = new() { ["en"] = "{0} errors · {1} warnings", ["th"] = "ผิด {0} · เตือน {1}" },

        ["lsp.none"] = new() { ["en"] = "No problems in this file.", ["th"] = "ไฟล์นี้ไม่มีปัญหา" },
        ["lsp.waiting"] = new() { ["en"] = "The language server is starting; errors show here as soon as it has checked the file.", ["th"] = "language server กำลังเริ่ม ข้อผิดพลาดจะขึ้นที่นี่ทันทีที่ตรวจไฟล์เสร็จ" },
        ["lsp.unsupported"] = new() { ["en"] = "Live errors are available for C#, TypeScript / JavaScript, Python and Dart files.", ["th"] = "การตรวจข้อผิดพลาดสดใช้ได้กับไฟล์ C#, TypeScript / JavaScript, Python และ Dart" },
        ["lsp.tooLargeHint"] = new() { ["en"] = "This file is over 2 million characters: sending it to the server after every pause would make typing stutter, so live checking is off for it.", ["th"] = "ไฟล์นี้ยาวเกิน 2 ล้านตัวอักษร ถ้าส่งให้ server ทุกครั้งที่หยุดพิมพ์จะทำให้พิมพ์สะดุด จึงปิดการตรวจสดสำหรับไฟล์นี้" },
        ["lsp.installHint"] = new()
        {
            ["en"] = "No {0} language server was found, so there are no live errors or hover for this file. Install one (it is free), then press Check again:",
            ["th"] = "ไม่พบ language server ของ {0} จึงยังไม่มีการตรวจข้อผิดพลาดสดและ hover สำหรับไฟล์นี้ ติดตั้งได้ฟรี แล้วกด ตรวจอีกครั้ง:",
        },
        ["lsp.installHintNoCommand"] = new()
        {
            ["en"] = "No {0} language server was found. Install the Dart SDK (or Flutter, which includes it) and put its bin folder on PATH, then press Check again.",
            ["th"] = "ไม่พบ language server ของ {0} ติดตั้ง Dart SDK (หรือ Flutter ซึ่งมี Dart มาด้วย) แล้วเพิ่มโฟลเดอร์ bin ไว้ใน PATH จากนั้นกด ตรวจอีกครั้ง",
        },
        ["lsp.failedHint"] = new() { ["en"] = "{0} stopped: {1}", ["th"] = "{0} หยุดทำงาน: {1}" },
        ["lsp.failedNoDetail"] = new() { ["en"] = "it gave no reason", ["th"] = "ไม่ได้บอกสาเหตุ" },
    };
}
