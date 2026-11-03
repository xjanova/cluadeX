using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Agents;

/// <summary>
/// Design mode, per chat (owner 2026-11-03: "เพิ่มโหมด ดีไซด์ให้ดี เช่นถ้าให้ออกแบบ เมื่อออกแบบ ก็พาผู้ใช้ไปหน้า ui studio เพื่อให้
/// เห็นหน้าจอจริง"). On for a chat when its Design switch is on, or when the owner's own message in this turn asks for a
/// design ("ออกแบบหน้าแรก", "redesign the landing page"). A design turn takes the owner to the UI Studio to watch the real
/// screen, and tells the model how to work for it. Built like <see cref="WorkflowGate"/>: the owner's words are recorded by
/// the chat page on Send — a pipe caller's text is never the owner's and is never recorded.
/// </summary>
public static class DesignGate
{
    private static readonly HashSet<string> On = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> OwnerWords = new(StringComparer.Ordinal);
    private static readonly object Lock = new();
    private static string? _file;

    /// <summary>Words that ask for a design. English words are matched as whole Latin words — Thai has no spaces, so
    /// "แก้UIหน้าแรก" still counts and "guide" or "build" do not.</summary>
    private static readonly Regex Asked = new(
        @"ออกแบบ|ดีไซ[นด]์?|หน้าตา|หน้าจอ|เลย์เอา[ตท]์|เลย์เอ้า[ตท]์|"
        + @"(?<![A-Za-z])(re-?)?design(s|ed|ing|er)?(?![A-Za-z])|(?<![A-Za-z])mock-?ups?(?![A-Za-z])|(?<![A-Za-z])wire-?frames?(?![A-Za-z])|"
        + @"(?<![A-Za-z])landing\s+pages?(?![A-Za-z])|(?<![A-Za-z])layouts?(?![A-Za-z])|(?<![A-Za-z])ui(?![A-Za-z])|(?<![A-Za-z])ux(?![A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    /// <summary>"Design" that is not about screens: a database, an API, a schema, a system, a design pattern. These
    /// phrases are taken out before <see cref="Asked"/> looks — "ออกแบบฐานข้อมูลและหน้าจอ" still asks (หน้าจอ).</summary>
    private static readonly Regex NotScreens = new(
        @"(?<![A-Za-z])(database|db|data|api|apis|rest|graphql|schema|system|software|class|object|algorithm|backend|back-end|server|network|protocol|domain)\s*(re-?)?design(s|ed|ing)?(?![A-Za-z])|"
        + @"(?<![A-Za-z])(re-?)?design(ing)?\s+(the\s+|a\s+|an\s+|our\s+|my\s+|its\s+)?(database|db|schema|api|apis|endpoints?|data\s+model|architecture|system|backend|back-end|algorithm|class(es)?|tables?)(?![A-Za-z])|"
        + @"(?<![A-Za-z])design\s*patterns?(?![A-Za-z])|(?<![A-Za-z])design\s+docs?(?![A-Za-z])|(?<![A-Za-z])design\s+documents?(?![A-Za-z])|"
        + @"ออกแบบ\s*(ฐานข้อมูล|ดาต้าเบส|database|db|api|เอพีไอ|schema|สคีมา|ระบบ|โครงสร้างข้อมูล|ตาราง|อัลกอริทึม|คลาส|สถาปัตยกรรม|architecture)|"
        + @"ดีไซ[นด]์?\s*แพทเทิร์น|design\s*pattern|"
        // the app's own page by name ("UI Studio ค้าง" is a bug report, not a design), a screenshot, a screen that hangs
        // or goes black — "หน้าจอ" and "UI" alone would otherwise pull the window to the Studio for every such message
        + @"(?<![A-Za-z])ui[\s-]*studio(?![A-Za-z])|(ภาพ|แคป|แคปเจอร์|จับภาพ|จับ)\s*หน้าจอ|หน้าจอ\s*(ค้าง|ดำ|ขาว|กระพริบ|กะพริบ|กระตุก|เด้ง)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    /// <summary>Persist the per-chat switch here (DataRoot/design-chats.json). Null = memory only (the harness).</summary>
    public static void Load(string? dataRoot)
    {
        lock (Lock)
        {
            _file = dataRoot == null ? null : Path.Combine(dataRoot, "design-chats.json");
            On.Clear();
            try
            {
                if (_file != null && File.Exists(_file))
                    foreach (var id in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file)) ?? new()) On.Add(id);
            }
            catch { /* a damaged file = every chat off, the safe default */ }
        }
    }

    public static event Action<string>? Changed;

    public static bool IsOn(string? chat)
    {
        if (string.IsNullOrEmpty(chat)) return false;
        lock (Lock) return On.Contains(chat);
    }

    public static void Set(string chat, bool on)
    {
        if (string.IsNullOrEmpty(chat)) return;
        lock (Lock)
        {
            if (!(on ? On.Add(chat) : On.Remove(chat))) return;
            try { if (_file != null) File.WriteAllText(_file, JsonSerializer.Serialize(On.ToList())); } catch { }
        }
        Changed?.Invoke(chat);
    }

    /// <summary>The owner sent <paramref name="text"/> in <paramref name="chat"/> (this turn's words; the next turn's replace
    /// them). <paramref name="append"/>: typed into the running turn — it adds to the turn's words instead.</summary>
    public static void NoteOwnerMessage(string chat, string? text, bool append = false)
    {
        if (string.IsNullOrEmpty(chat)) return;
        lock (Lock) OwnerWords[chat] = append && OwnerWords.TryGetValue(chat, out var before) ? before + "\n" + text : text ?? "";
    }

    /// <summary>Did these words ask for a design of screens? Pure, for the harness. A regex that runs too long counts as no.</summary>
    public static bool AsksForDesign(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { return Asked.IsMatch(NotScreens.Replace(text, " ")); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>Why: "switch" (the chat's Design switch), "asked" (the owner's words this turn), or null = not a design turn.</summary>
    public static string? Why(string? chat)
    {
        if (string.IsNullOrEmpty(chat)) return null;
        if (IsOn(chat)) return "switch";
        string? words;
        lock (Lock) OwnerWords.TryGetValue(chat, out words);
        return AsksForDesign(words) ? "asked" : null;
    }

    public static bool IsDesignTurn(string? chat) => Why(chat) != null;

    /// <summary>What the model is told on a design turn (a notice in that chat's turn — never the system prompt, which is
    /// shared by every chat and cached).</summary>
    public const string Guidance =
        "Design mode is on for this turn: the user wants to see and shape the real UI. The UI Studio shows the running page "
        + "(or the app's screens) next to this chat and reloads it by itself whenever a file changes, so the user watches every "
        + "edit there. Follow the project's existing styling approach (its CSS files, Tailwind classes, component styles or "
        + "widget kit), keep the page runnable after every edit, and prefer small visible steps over one huge rewrite. While "
        + "you work the user cannot edit in the UI Studio; it unlocks when your turn ends. For a brand-new site, create its "
        + "folder with open_project (create=true) and start with plain HTML/CSS (index.html at the folder's root) or a Vite "
        + "template, so the Studio can show it at once. Do not ask the user to open a browser.";
}
