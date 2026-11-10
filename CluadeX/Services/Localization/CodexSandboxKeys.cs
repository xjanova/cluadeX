namespace CluadeX.Services.Localization;

/// <summary>Localized strings for OpenAI Dev's (Codex CLI) sandbox notices. Every key needs both "en" and "th".</summary>
internal static class CodexSandboxKeys
{
    internal static readonly Dictionary<string, Dictionary<string, string>> Keys = new()
    {
        ["codex.sandbox.blocked"] = new()
        {
            ["en"] = "CluadeX: this failure comes from Codex's sandbox ({0}), not from the code. The sandbox runs commands as a separate restricted Windows account. Run builds and tests through CluadeX's verify_change, which runs as you.",
            ["th"] = "CluadeX: ล้มเหลวเพราะ sandbox ของ Codex ({0}) ไม่ใช่เพราะโค้ด sandbox รันคำสั่งด้วยบัญชี Windows แยกที่ถูกจำกัดสิทธิ์ ให้ build และเทสต์ผ่าน verify_change ของ CluadeX ซึ่งรันด้วยบัญชีของคุณ",
        },
        ["codex.sandbox.why.nuget"] = new() { ["en"] = "it cannot read your NuGet.Config", ["th"] = "อ่าน NuGet.Config ของคุณไม่ได้" },
        ["codex.sandbox.why.dpapi"] = new() { ["en"] = "it cannot use your DPAPI keys", ["th"] = "ใช้กุญแจ DPAPI ของคุณไม่ได้" },
        ["codex.sandbox.why.profile"] = new() { ["en"] = "it cannot write to your user profile", ["th"] = "เขียนโฟลเดอร์โปรไฟล์ของคุณไม่ได้" },
        ["codex.sandbox.why.wmi"] = new() { ["en"] = "it cannot start processes through WMI", ["th"] = "สร้างโปรเซสผ่าน WMI ไม่ได้" },
        ["codex.sandbox.repaired"] = new()
        {
            ["en"] = "CluadeX: Codex's sandbox could not start because {0} belonged to the sandbox's own account (a git init run inside the sandbox does that). CluadeX rebuilt it as yours; the next command will run.",
            ["th"] = "CluadeX: sandbox ของ Codex เริ่มไม่ได้ เพราะ {0} เป็นของบัญชี sandbox เอง (เกิดจาก git init ที่รันใน sandbox) CluadeX สร้างใหม่ให้เป็นของคุณแล้ว คำสั่งถัดไปจะรันได้",
        },
        ["codex.sandbox.repairFailed"] = new()
        {
            ["en"] = "CluadeX: Codex's sandbox cannot start because {0} belongs to the sandbox's own account, and CluadeX could not rebuild it ({1}). Close what uses that folder and send the message again, or delete {0} and run git init yourself.",
            ["th"] = "CluadeX: sandbox ของ Codex เริ่มไม่ได้ เพราะ {0} เป็นของบัญชี sandbox และ CluadeX สร้างใหม่ไม่สำเร็จ ({1}) ปิดโปรแกรมที่ใช้โฟลเดอร์นั้นแล้วส่งข้อความอีกครั้ง หรือลบ {0} แล้ว git init เอง",
        },
        ["codex.sandbox.setupFailed"] = new()
        {
            ["en"] = "CluadeX: Codex's sandbox could not start (\"setup refresh had errors\"), so nothing ran. The reason is in ~/.codex/.sandbox/sandbox.<date>.log.",
            ["th"] = "CluadeX: sandbox ของ Codex เริ่มไม่ได้ (\"setup refresh had errors\") จึงไม่มีคำสั่งไหนได้รัน ดูสาเหตุได้ใน ~/.codex/.sandbox/sandbox.<วันที่>.log",
        },
        ["codex.sandbox.preflight"] = new()
        {
            ["en"] = "CluadeX rebuilt {0} before this turn: it belonged to Codex's sandbox account, which stops the sandbox from starting.",
            ["th"] = "ก่อนเริ่มเทิร์นนี้ CluadeX สร้าง {0} ใหม่ให้เป็นของคุณ เพราะมันเป็นของบัญชี sandbox ของ Codex ซึ่งทำให้ sandbox เริ่มไม่ได้",
        },
        ["codex.sandbox.folderRepaired"] = new()
        {
            ["en"] = "CluadeX: Codex's sandbox could not write in {0}: the folder belonged to the sandbox's own account and missed its current write permission. CluadeX rebuilt it as yours (same files); the next edit will work.",
            ["th"] = "CluadeX: sandbox ของ Codex เขียนใน {0} ไม่ได้ เพราะโฟลเดอร์เป็นของบัญชี sandbox เองและไม่มีสิทธิ์เขียนชุดปัจจุบัน CluadeX สร้างใหม่ให้เป็นของคุณแล้ว (ไฟล์เหมือนเดิม) แก้ไฟล์ครั้งถัดไปจะได้",
        },
        ["codex.sandbox.folderRepairFailed"] = new()
        {
            ["en"] = "CluadeX: Codex's sandbox cannot write in {0} (the folder belongs to the sandbox's own account and misses its write permission), and CluadeX could not rebuild it ({1}). Close what uses that folder and send the message again.",
            ["th"] = "CluadeX: sandbox ของ Codex เขียนใน {0} ไม่ได้ (โฟลเดอร์เป็นของบัญชี sandbox และไม่มีสิทธิ์เขียน) และ CluadeX สร้างใหม่ไม่สำเร็จ ({1}) ปิดโปรแกรมที่ใช้โฟลเดอร์นั้นแล้วส่งข้อความอีกครั้ง",
        },
        ["codex.sandbox.folderPreflight"] = new()
        {
            ["en"] = "CluadeX rebuilt {0} before this turn: it belonged to Codex's sandbox account and missed the sandbox's write permission, so every edit in it would have failed.",
            ["th"] = "ก่อนเริ่มเทิร์นนี้ CluadeX สร้าง {0} ใหม่ให้เป็นของคุณ เพราะมันเป็นของบัญชี sandbox ของ Codex และไม่มีสิทธิ์เขียนของ sandbox ทำให้แก้ไฟล์ในนั้นไม่ได้ทุกครั้ง",
        },
        ["codex.mcp.failedNoMessage"] = new()
        {
            ["en"] = "The {0} tool reported a failure without a message.",
            ["th"] = "เครื่องมือ {0} แจ้งว่าล้มเหลวแต่ไม่ได้ส่งข้อความมา",
        },
    };
}
