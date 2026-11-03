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
        + @"(?<![A-Za-z])landing\s+pages?(?![A-Za-z])|(?<![A-Za-z])layouts?(?![A-Za-z])|(?<![A-Za-z])ui(?![A-Za-z])|(?<![A-Za-z])ux(?![A-Za-z])|"
        // Thai web-page wording: a web page with a verb that makes or shapes it ("ทำหน้าเว็บใหม่", "ปรับสีปุ่มหน้าเว็บ" — not
        // "ทำไม/ทำให้หน้าเว็บ…", and "แก้" only right before it: "แก้บั๊กหน้าเว็บ" is a bug), "ให้สวย", a theme
        + @"(ทำ(?!ไม|ให้|งาน)|ปรับ|แต่ง|สร้าง|จัด)\S{0,10}?หน้าเว็บ|แก้\s*หน้าเว็บ|ให้สวย|ธีม",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    /// <summary>"Design" that is not about screens: a database, an API, a schema, a system, a design pattern. These
    /// phrases are taken out before <see cref="Asked"/> looks — "ออกแบบฐานข้อมูลและหน้าจอ" still asks (หน้าจอ).</summary>
    private static readonly Regex NotScreens = new(
        @"(?<![A-Za-z])(database|db|data|api|apis|rest|graphql|schema|system|software|class|object|algorithm|backend|back-end|server|network|protocol|domain)\s*(re-?)?design(s|ed|ing)?(?![A-Za-z])|"
        // …unless a screen word follows ("redesign the system settings page" is a page)
        + @"(?<![A-Za-z])(re-?)?design(ing)?\s+(the\s+|a\s+|an\s+|our\s+|my\s+|its\s+)?(database|db|schema|api|apis|endpoints?|data\s+model|architecture|system|backend|back-end|algorithm|class(es)?|tables?)(?![A-Za-z])"
        + @"(?!\s+(settings?|pages?|screens?|views?|dialogs?|forms?|ui|ux|dashboards?|menus?|panels?|windows?|tabs?)(?![A-Za-z]))|"
        + @"(?<![A-Za-z])design\s*patterns?(?![A-Za-z])|(?<![A-Za-z])design\s+docs?(?![A-Za-z])|(?<![A-Za-z])design\s+documents?(?![A-Za-z])|"
        // a table only of a database ("ออกแบบตารางราคา" is a pricing table on a page): a database word later in the line
        // before any หน้า ("ออกแบบตาราง users ในฐานข้อมูล"), or a table named in English with no page after it
        // ("ออกแบบตาราง users"); never one with a screen word next
        + @"ออกแบบ\s*(ฐานข้อมูล|ดาต้าเบส|database|db|api|เอพีไอ|schema|สคีมา|ระบบ|โครงสร้างข้อมูล|อัลกอริทึม|คลาส|สถาปัตยกรรม|architecture|"
        + @"ตาราง(?=(?:(?!หน้า)[^\n]){0,40}?(ฐานข้อมูล|ดาต้าเบส|database|(?<![A-Za-z])db(?![A-Za-z])|(?<![A-Za-z])sql|migration|คอลัมน์|(?<![A-Za-z])columns?(?![A-Za-z]))"
        + @"|\s*[A-Za-z_][A-Za-z0-9_]*(?![A-Za-z0-9_])(?![^\n]{0,30}?(หน้า|(?<![A-Za-z])(pages?|screens?|ui)(?![A-Za-z])))))"
        + @"(?!\s*(หน้า|ui|ยูไอ))|"
        + @"ดีไซ[นด]์?\s*แพทเทิร์น|design\s*pattern|"
        // the app's own page by name ("UI Studio ค้าง" is a bug report, not a design), a screenshot, a screen that hangs
        // or goes black — "หน้าจอ" and "UI" alone would otherwise pull the window to the Studio for every such message
        + @"(?<![A-Za-z])ui[\s-]*studio(?![A-Za-z])|(ภาพ|แคป|แคปเจอร์|จับภาพ|จับ)\s*หน้าจอ|หน้าจอ\s*(ค้าง|ดำ|ขาว|กระพริบ|กะพริบ|กระตุก|เด้ง)|"
        // the UI as code, not as a look: its thread, its tests, UI automation, the dispatcher ("the UI thread freezes")
        + @"(?<![A-Za-z])ui[\s-]*(threads?|tests?|testing|automation|dispatcher)(?![A-Za-z])|"
        // an error that shows on a screen is a bug report ("มี error ขึ้นที่หน้าจอ login", "มี error ที่หน้าจอ login", "error
        // แสดงบนหน้าจอ", "หน้าจอ login มี error"), and a layout that is not a page's
        + @"(error|errors|exception|เออเร่อ|เออเรอ|เอเร่อ|ข้อผิดพลาด|บั๊ก|(?<![A-Za-z])bugs?)\S{0,6}(\s*(แสดง|ขึ้น|โผล่|เด้ง)\S{0,4})?\s*(ที่|บน|ใน)?\s*หน้าจอ|"
        + @"หน้าจอ\S{0,12}\s*\S{0,12}\s*(มี|ขึ้น|แสดง|เจอ|โชว์|เด้ง)\s*(error|exception|เออเร่อ|เออเรอ|เอเร่อ|ข้อผิดพลาด|บั๊ก|(?<![A-Za-z])bugs?(?![A-Za-z]))|"
        // the code made neat, not the page ("จัดโค้ดให้สวย", "refactor ให้สวยขึ้น"), and a web page's tests or speed
        // ("ทำเทสหน้าเว็บ", "ปรับความเร็วหน้าเว็บ") — "ให้สวย" and a verb before หน้าเว็บ would count them as designs
        + @"(โค้ด|โค๊ด|ฟอร์แมต|ฟอร์แมท|รีแฟคเตอร์|รีแฟกเตอร์|(?<![A-Za-z])(code|format|refactor|indent))\S{0,12}?\s*ให้สวย|"
        + @"(เทส|ทดสอบ|ความเร็ว|ประสิทธิภาพ|(?<![A-Za-z])(tests?|testing|performance|speed|seo))\S{0,4}\s*(ของ\s*)?หน้าเว็บ|"
        + @"(?<![A-Za-z])(keyboard|memory|struct|data|file|disk)\s+layouts?(?![A-Za-z])",
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

    /// <summary>A chat that is gone (deleted, or an untouched chat dropped on the way out): its switch and words go too,
    /// so design-chats.json holds only chats that exist.</summary>
    public static void Forget(string? chat)
    {
        if (string.IsNullOrEmpty(chat)) return;
        bool was;
        lock (Lock)
        {
            OwnerWords.Remove(chat);
            was = On.Remove(chat);
            if (was) try { if (_file != null) File.WriteAllText(_file, JsonSerializer.Serialize(On.ToList())); } catch { }
        }
        if (was) Changed?.Invoke(chat);
    }

    /// <summary>The owner's Send is starting its turn: is it a design turn? Decided once, here — the words are used up, so
    /// a later turn the owner did not type (a pipe caller's, a retry, a next-step button) never reads them again.</summary>
    public static bool TakeOwnerTurn(string? chat)
    {
        bool design = IsDesignTurn(chat);
        EndOwnerTurn(chat);
        return design;
    }

    /// <summary>The turn the owner's words were typed for has ended (words typed into it while it ran included): they go,
    /// so no later turn in <paramref name="chat"/> reads them. The chat's Design switch stays as it is.</summary>
    public static void EndOwnerTurn(string? chat)
    {
        if (string.IsNullOrEmpty(chat)) return;
        lock (Lock) OwnerWords.Remove(chat);
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
        + "folder with open_project (create=true) and start with plain HTML/CSS (index.html at the folder's root), so the Studio "
        + "can show it at once; a new Vite template shows once its packages are installed. Do not ask the user to open a browser.";
}
