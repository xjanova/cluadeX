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
        ["codex.mcp.failedNoMessage"] = new()
        {
            ["en"] = "The {0} tool reported a failure without a message.",
            ["th"] = "เครื่องมือ {0} แจ้งว่าล้มเหลวแต่ไม่ได้ส่งข้อความมา",
        },
    };
}
