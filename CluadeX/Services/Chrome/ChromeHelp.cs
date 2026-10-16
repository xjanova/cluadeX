using System.IO;
using System.Text.Json;

namespace CluadeX.Services.Chrome;

/// <summary>
/// Plain-language help when driving Chrome goes wrong — written for someone who has never heard of a "bridge" or a
/// "native host". A chrome tool's failure text is matched to one of the three things that actually happen:
/// the extension is not signed in to the same Claude account (or not running), Chrome asked for permission in the
/// Claude side panel and nobody answered, and Chrome's "Ask where to save each file" turning every download into a
/// Save As window the agent cannot answer. Each gets numbered steps naming the exact account and setting.
/// </summary>
public static class ChromeHelp
{
    public enum Problem { None, NotConnected, PermissionTimedOut, AsksWhereToSave }

    /// <summary>Which problem a cluadex-chrome tool's output shows (None for a tool that is not Chrome's).</summary>
    public static Problem Diagnose(string toolName, string? output, bool success)
    {
        if (!toolName.StartsWith("mcp__" + ClaudeInChromeLauncher.ServerName + "__", StringComparison.Ordinal)) return Problem.None;
        string o = output ?? "";
        if (o.Contains("extension is not connected", StringComparison.OrdinalIgnoreCase)
            || o.Contains("Claude in Chrome is not connected", StringComparison.OrdinalIgnoreCase)) return Problem.NotConnected;
        if (o.Contains("permission prompt", StringComparison.OrdinalIgnoreCase) && o.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return Problem.PermissionTimedOut;
        // Working, but every download would open a Save As window it cannot answer: say so before it happens.
        if (success && AsksWhereToSaveCached()) return Problem.AsksWhereToSave;
        return Problem.None;
    }

    /// <summary>The claude.ai account Claude Code is signed in to (the extension must use the same one), or null.</summary>
    public static string? ClaudeAccountEmail()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("oauthAccount", out var a) && a.TryGetProperty("emailAddress", out var e) ? e.GetString() : null;
        }
        catch { return null; }
    }

    private static (DateTime At, bool Value) _askCache;
    private static bool AsksWhereToSaveCached()
    {
        if (DateTime.UtcNow - _askCache.At > TimeSpan.FromMinutes(1)) _askCache = (DateTime.UtcNow, ChromeAsksWhereToSave());
        return _askCache.Value;
    }

    /// <summary>Chrome's "Ask where to save each file before downloading" is on in the profile last used.</summary>
    public static bool ChromeAsksWhereToSave()
    {
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data");
            string profile = "Default";
            string localState = Path.Combine(root, "Local State");
            if (File.Exists(localState))
            {
                using var ls = JsonDocument.Parse(File.ReadAllText(localState));
                if (ls.RootElement.TryGetProperty("profile", out var p) && p.TryGetProperty("last_used", out var lu) && lu.GetString() is { Length: > 0 } name)
                    profile = name;
            }
            string prefs = Path.Combine(root, profile, "Preferences");
            if (!File.Exists(prefs)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(prefs));
            return doc.RootElement.TryGetProperty("download", out var d) && d.TryGetProperty("prompt_for_download", out var v)
                   && v.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    /// <summary>The steps for <paramref name="problem"/>, in the UI language.</summary>
    public static string Advice(Problem problem, bool thai)
    {
        string account = ClaudeAccountEmail() ?? (thai ? "บัญชีเดียวกับที่ใช้ Claude Code" : "the account Claude Code uses");
        return problem switch
        {
            Problem.NotConnected => thai
                ? "🔌 CluadeX ยังมองไม่เห็น Chrome — ทำตามนี้ทีละข้อ:\n" +
                  $"1. เปิด Chrome แล้วกดไอคอน Claude (ในรูปจิ๊กซอว์ 🧩 มุมขวาบน) ให้แผง Claude เปิดขึ้น\n" +
                  $"2. ดูว่าแผงนั้นล็อกอินด้วย **{account}** — ถ้าเป็นบัญชีอื่น ให้ออกจากระบบแล้วเข้าใหม่ด้วยบัญชีนี้\n" +
                  "3. ยังไม่ได้: พิมพ์ chrome://extensions ในช่องที่อยู่ กดปุ่มรีโหลด ⟳ ที่การ์ด Claude (หรือปิด Chrome ทุกหน้าต่างแล้วเปิดใหม่)\n" +
                  "4. ยังไม่มี extension: ติดตั้งจาก https://claude.ai/chrome\n" +
                  "เสร็จแล้วพิมพ์ \"ต่อ\" ได้เลย"
                : "🔌 CluadeX can't see Chrome yet — step by step:\n" +
                  "1. Open Chrome and click the Claude icon (under the puzzle piece 🧩, top right) so the Claude side panel opens\n" +
                  $"2. Check that the panel is signed in as **{account}** — if not, sign out and sign in with that account\n" +
                  "3. Still not: go to chrome://extensions and press reload ⟳ on the Claude card (or close every Chrome window and reopen)\n" +
                  "4. No extension yet: install it from https://claude.ai/chrome\n" +
                  "Then type \"continue\".",
            Problem.PermissionTimedOut => thai
                ? "🙋 Chrome ขออนุญาตอยู่ในแผง Claude แต่ไม่มีใครกด จึงหมดเวลา\n" +
                  "เปิดแผง Claude ใน Chrome ค้างไว้ระหว่างที่ CluadeX ทำงาน — พอมีกล่องขออนุญาต ให้กด Allow (เลือก \"อนุญาตตลอดสำหรับเว็บนี้\" ถ้ามี จะได้ไม่ถามซ้ำ)"
                : "🙋 Chrome asked for permission in the Claude side panel and nobody answered, so it timed out.\n" +
                  "Keep the Claude panel open in Chrome while CluadeX works — when it asks, press Allow (pick \"always allow on this site\" if offered).",
            Problem.AsksWhereToSave => thai
                ? "💾 Chrome ตั้งไว้ให้ \"ถามตำแหน่งที่จะบันทึกไฟล์ทุกครั้ง\" — ทุกการดาวน์โหลดจะเปิดหน้าต่าง Save As ที่ AI กดเองไม่ได้\n" +
                  "ปิดได้ที่: chrome://settings/downloads → ปิดสวิตช์ \"ถามตำแหน่งที่จะบันทึกไฟล์แต่ละไฟล์ก่อนดาวน์โหลด\" (ไฟล์จะลงโฟลเดอร์ Downloads เอง)"
                : "💾 Chrome is set to \"Ask where to save each file\" — every download opens a Save As window the AI can't answer.\n" +
                  "Turn it off: chrome://settings/downloads → switch off \"Ask where to save each file before downloading\" (files then land in Downloads).",
            _ => "",
        };
    }
}
