namespace CluadeX.Services.Localization;

/// <summary>Localized strings for the Code Editor's live errors: the Problems panel, the status-bar item, hover and the
/// language-server install hints (EditorLanguageService). Every key needs both "en" and "th".</summary>
internal static class LspKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["lsp.problems"] = new() { ["en"] = "PROBLEMS", ["th"] = "ปัญหา" },
        ["lsp.problemsTip"] = new() { ["en"] = "Errors and warnings from builds, checks and the language server: click for the list", ["th"] = "ข้อผิดพลาดและคำเตือนจากการ build การตรวจ และ language server: คลิกเพื่อดูรายการ" },
        ["lsp.close"] = new() { ["en"] = "Hide problems", ["th"] = "ซ่อนรายการปัญหา" },
        ["lsp.copy"] = new() { ["en"] = "Copy", ["th"] = "คัดลอก" },
        ["lsp.copied"] = new() { ["en"] = "Install command copied", ["th"] = "คัดลอกคำสั่งติดตั้งแล้ว" },
        ["lsp.runInTerminal"] = new() { ["en"] = "Run in terminal", ["th"] = "รันใน Terminal" },
        ["lsp.runInTerminalTip"] = new() { ["en"] = "Type the install command into the terminal below and run it. Press Check again when it finishes.", ["th"] = "ใส่คำสั่งติดตั้งลง Terminal ด้านล่างแล้วรัน เสร็จแล้วกด ตรวจอีกครั้ง" },
        ["lsp.openPage"] = new() { ["en"] = "How to install", ["th"] = "วิธีติดตั้ง" },
        ["lsp.checkAgain"] = new() { ["en"] = "Check again", ["th"] = "ตรวจอีกครั้ง" },
        ["lsp.restart"] = new() { ["en"] = "Restart server", ["th"] = "เริ่ม server ใหม่" },
        ["lsp.askAiTip"] = new() { ["en"] = "Fix this error: sends it with the lines around it to the chat (to the running AI if one is working)", ["th"] = "แก้ข้อผิดพลาดนี้: ส่งพร้อมโค้ดรอบ ๆ เข้าแชต (ถ้า AI กำลังทำงานอยู่จะส่งให้ตัวที่ทำงานอยู่)" },
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
        ["lsp.st.notInstalled"] = new() { ["en"] = "{0}: no checks while typing (optional)", ["th"] = "{0}: ไม่มีการตรวจขณะพิมพ์ (ติดตั้งเพิ่มได้)" },
        ["lsp.st.failed"] = new() { ["en"] = "{0}: server stopped", ["th"] = "{0}: server หยุดทำงาน" },
        ["lsp.st.tooLarge"] = new() { ["en"] = "Live checks off: file too large", ["th"] = "ปิดการตรวจสด: ไฟล์ใหญ่เกิน" },
        ["lsp.st.running"] = new() { ["en"] = "{0} · {1}", ["th"] = "{0} · {1}" },
        ["lsp.st.counts"] = new() { ["en"] = "{0} errors · {1} warnings", ["th"] = "ผิด {0} · เตือน {1}" },

        ["lsp.none"] = new() { ["en"] = "No problems in this file.", ["th"] = "ไฟล์นี้ไม่มีปัญหา" },
        ["lsp.waiting"] = new() { ["en"] = "The language server is starting; errors show here as soon as it has checked the file.", ["th"] = "language server กำลังเริ่ม ข้อผิดพลาดจะขึ้นที่นี่ทันทีที่ตรวจไฟล์เสร็จ" },
        ["lsp.unsupported"] = new() { ["en"] = "No problems found. Errors from builds and checks show here for any language; checking while typing covers C#, TypeScript / JavaScript, Python and Dart.", ["th"] = "ไม่พบปัญหา ข้อผิดพลาดจากการ build และการตรวจจะขึ้นที่นี่ได้ทุกภาษา ส่วนการตรวจขณะพิมพ์รองรับ C#, TypeScript / JavaScript, Python และ Dart" },
        ["lsp.tooLargeHint"] = new() { ["en"] = "This file is over 2 million characters: sending it to the server after every pause would make typing stutter, so live checking is off for it.", ["th"] = "ไฟล์นี้ยาวเกิน 2 ล้านตัวอักษร ถ้าส่งให้ server ทุกครั้งที่หยุดพิมพ์จะทำให้พิมพ์สะดุด จึงปิดการตรวจสดสำหรับไฟล์นี้" },
        ["lsp.installHint"] = new()
        {
            ["en"] = "Errors from builds and checks already show here. Optional: a {0} language server adds errors while you type and hover info. It is free; install it, then press Check again:",
            ["th"] = "ข้อผิดพลาดจากการ build และการตรวจขึ้นที่นี่อยู่แล้ว ถ้าต้องการเพิ่ม: language server ของ {0} จะตรวจขณะพิมพ์และแสดงข้อมูลเมื่อชี้เมาส์ ติดตั้งฟรี เสร็จแล้วกด ตรวจอีกครั้ง:",
        },
        ["lsp.installHintNoCommand"] = new()
        {
            ["en"] = "Errors from builds and checks already show here. Optional: for errors while you type, install the Dart SDK (or Flutter, which includes it), put its bin folder on PATH, then press Check again.",
            ["th"] = "ข้อผิดพลาดจากการ build และการตรวจขึ้นที่นี่อยู่แล้ว ถ้าต้องการตรวจขณะพิมพ์: ติดตั้ง Dart SDK (หรือ Flutter ซึ่งมี Dart มาด้วย) เพิ่มโฟลเดอร์ bin ไว้ใน PATH แล้วกด ตรวจอีกครั้ง",
        },
        ["lsp.failedHint"] = new() { ["en"] = "{0} stopped: {1}", ["th"] = "{0} หยุดทำงาน: {1}" },
        ["lsp.failedNoDetail"] = new() { ["en"] = "it gave no reason", ["th"] = "ไม่ได้บอกสาเหตุ" },

        // Problems from builds and checks (BuildProblemsService) — {0} is a checker ("dart analyze", "dotnet build")
        ["problems.checkNow"] = new() { ["en"] = "Check now", ["th"] = "ตรวจเดี๋ยวนี้" },
        ["problems.checkStop"] = new() { ["en"] = "Stop check", ["th"] = "หยุดตรวจ" },
        ["problems.checkTip"] = new() { ["en"] = "Run {0} on the project and list what it finds", ["th"] = "รัน {0} กับโปรเจกต์แล้วแสดงสิ่งที่พบ" },
        ["problems.checking"] = new() { ["en"] = "Checking: {0}…", ["th"] = "กำลังตรวจ: {0}…" },
        // {0} checker, {1} errors, {2} warnings, {3} time
        ["problems.lastCheck"] = new() { ["en"] = "{0} · {1} errors · {2} warnings · {3}", ["th"] = "{0} · ผิด {1} · เตือน {2} · {3}" },
        ["problems.lastCheckFailed"] = new() { ["en"] = "{0} did not finish: {1}", ["th"] = "{0} ทำงานไม่จบ: {1}" },
        ["problems.lastCheckTimeout"] = new() { ["en"] = "{0} took too long and was stopped", ["th"] = "{0} ใช้เวลานานเกินจึงถูกหยุด" },
        ["problems.lastCheckCancelled"] = new() { ["en"] = "Check stopped", ["th"] = "หยุดตรวจแล้ว" },
        ["problems.stale"] = new() { ["en"] = "The file changed after this was found, so it may already be fixed. Check again to be sure.", ["th"] = "ไฟล์ถูกแก้หลังจากพบปัญหานี้ อาจแก้ไปแล้ว ตรวจอีกครั้งเพื่อความแน่ใจ" },
        ["problems.sent"] = new() { ["en"] = "Sent to the chat", ["th"] = "ส่งเข้าแชตแล้ว" },
        ["problems.queued"] = new() { ["en"] = "Sent to the AI that is working: it reads it at its next step", ["th"] = "ส่งให้ AI ที่กำลังทำงานแล้ว จะอ่านในขั้นถัดไป" },
        ["problems.inBox"] = new() { ["en"] = "Added to the chat box (it already had your draft): press Enter to send", ["th"] = "เพิ่มลงช่องแชตแล้ว (มีข้อความร่างอยู่ก่อน) กด Enter เพื่อส่ง" },
        // Editor → chat (CodeEditorView.Problems.cs). editor.ask.*: {0} file, {1} first line, {2} last line, {3} the code
        ["editor.menu.cut"] = new() { ["en"] = "Cut", ["th"] = "ตัด" },
        ["editor.menu.copy"] = new() { ["en"] = "Copy", ["th"] = "คัดลอก" },
        ["editor.menu.paste"] = new() { ["en"] = "Paste", ["th"] = "วาง" },
        ["editor.menu.askFix"] = new() { ["en"] = "Ask AI: fix this", ["th"] = "ให้ AI แก้ส่วนนี้" },
        ["editor.menu.askExplain"] = new() { ["en"] = "Ask AI: explain this", ["th"] = "ให้ AI อธิบายส่วนนี้" },
        ["editor.menu.askChange"] = new() { ["en"] = "Ask AI: change this…", ["th"] = "ให้ AI เปลี่ยนส่วนนี้…" },
        ["editor.menu.toChat"] = new() { ["en"] = "Add to chat as context", ["th"] = "เพิ่มลงแชตเป็นข้อมูลอ้างอิง" },
        ["editor.ask.fix"] = new()
        {
            ["en"] = "Fix this code in {0}, lines {1}–{2}:\n{3}\nChange only what the fix needs.",
            ["th"] = "ช่วยแก้โค้ดนี้ใน {0} บรรทัด {1}–{2}:\n{3}\nแก้เฉพาะที่จำเป็น",
        },
        ["editor.ask.explain"] = new()
        {
            ["en"] = "Explain what this code in {0}, lines {1}–{2} does. Do not change it.\n{3}",
            ["th"] = "อธิบายว่าโค้ดนี้ใน {0} บรรทัด {1}–{2} ทำอะไร ไม่ต้องแก้\n{3}",
        },
        ["editor.ask.change"] = new()
        {
            ["en"] = "Change this code in {0}, lines {1}–{2}:\n{3}\nWhat to change: ",
            ["th"] = "แก้โค้ดนี้ใน {0} บรรทัด {1}–{2}:\n{3}\nสิ่งที่ต้องการให้เปลี่ยน: ",
        },
        ["editor.ask.context"] = new() { ["en"] = "For reference, {0} lines {1}–{2}:\n{3}", ["th"] = "ข้อมูลอ้างอิง {0} บรรทัด {1}–{2}:\n{3}" },
        ["editor.askChangeHint"] = new() { ["en"] = "The code is in the chat box: type what to change at the end, then press Enter", ["th"] = "ใส่โค้ดลงช่องแชตแล้ว พิมพ์สิ่งที่ต้องการเปลี่ยนต่อท้าย แล้วกด Enter" },
        ["editor.contextAdded"] = new() { ["en"] = "Added to the chat box", ["th"] = "เพิ่มลงช่องแชตแล้ว" },
        ["search.toChat"] = new() { ["en"] = "To chat", ["th"] = "ส่งเข้าแชต" },
        ["search.toChatTip"] = new() { ["en"] = "Add these results to the chat box as context, then say what to do with them", ["th"] = "เพิ่มผลลัพธ์เหล่านี้ลงช่องแชตเป็นข้อมูลอ้างอิง แล้วพิมพ์สิ่งที่ต้องการให้ทำ" },
        // {0} what the list is ("References to Foo"), {1} how many
        ["search.toChatHeader"] = new() { ["en"] = "{0} — {1} results:", ["th"] = "{0} — {1} รายการ:" },
        ["search.toChatQuery"] = new() { ["en"] = "Search results for \"{0}\"", ["th"] = "ผลการค้นหา \"{0}\"" },
        ["search.toChatMore"] = new() { ["en"] = "… and {0} more", ["th"] = "… และอีก {0} รายการ" },
    };
}
