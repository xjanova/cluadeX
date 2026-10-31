using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Effort "auto": the level a turn STARTS at, read from the task itself — the same policy for every provider that has an
/// effort (Anthropic effort, OpenAI reasoning_effort, Gemini thinking level / budget, the Claude Code CLI's --effort).
/// The agent loop then climbs from here when the run shows it is hard (CodeAgentService.StepEffortFor / SmartEffort).
///
/// It replaces "is there an action verb" (CodeAgentService.LooksNonTrivialTask), which made almost every Thai message
/// "real work" — "ทำไม" (why) contains "ทำ" (do) — and gave a one-line bug report and a five-part brief the same medium.
/// Owner 2026-10-19: "effort auto มันฉลาดปรับค่าเองหรือยังทุกค่าย".
///
/// Levels 0..3 = low, medium, high, xhigh. "max" is never picked automatically: it is the user's to choose.
/// </summary>
public static class AutoEffortPolicy
{
    public static readonly string[] Ladder = ["low", "medium", "high", "xhigh"];

    /// <summary>What the task looks like. <paramref name="Images"/> = pictures attached to this message;
    /// <paramref name="ContextTokens"/> = what the conversation already carries; <paramref name="Earlier"/> = the owner's
    /// earlier messages, oldest first, which is where a short "ต่อ" finds the task it carries on.</summary>
    public sealed record Input(string? Message, int Images = 0, int ContextTokens = 0, IReadOnlyList<string>? Earlier = null);

    /// <summary>The input for a turn of a chat: the same on every provider.</summary>
    public static Input ForTurn(string? message, int images, IEnumerable<ChatMessage>? history)
    {
        var turns = history?.ToList() ?? [];
        return new Input(message, images, turns.Sum(h => CluadeX.Helpers.TokenBudget.EstimateTokens(h.Content)),
            turns.Where(h => h.Role == MessageRole.User && !string.IsNullOrWhiteSpace(h.Content))
                .TakeLast(EarlierLooked).Select(h => h.Content).ToList());
    }

    /// <summary>The owner's own words: what the app put around them (the auto-recall block, its "recall was late" note) is
    /// not the task. Read as the task it made every recalled turn a "bug" (2026-10-31: a 13-letter message at xhigh).</summary>
    public static string OwnWords(string? message)
    {
        string text = RecallBlock.Replace(message ?? "", "");
        int late = text.IndexOf(RecallLateMark, StringComparison.Ordinal);
        return (late >= 0 ? text[..late] : text).Trim();
    }

    /// <summary>The level (0–3), its name, and why (a key under "auto.why.*").</summary>
    public sealed record Decision(int Level, string ReasonKey)
    {
        public string Effort => Ladder[Math.Clamp(Level, 0, Ladder.Length - 1)];
        /// <summary>A long first message is planned first: the loop's first step goes one level up (never past xhigh).</summary>
        public bool PlanFirst { get; init; }
    }

    public static Decision Pick(Input input)
    {
        string raw = OwnWords(input.Message);
        string m = raw.ToLowerInvariant();
        int len = raw.Length;
        string trouble = Without(m, NotTrouble);

        bool bug = Has(trouble, BugWords) || StackTrace.IsMatch(raw);
        bool plan = Has(m, PlanWords);
        int files = FileMentions(raw);
        bool multiFile = files >= 2 || Has(m, MultiFileWords);
        int items = ListItem.Matches(raw).Count;
        bool longSpec = len >= 1500 || items >= 5;
        bool retry = Has(trouble, RetryWords);
        bool action = Has(m, ActionWords) || files >= 1 || raw.Contains("```");
        bool question = Has(m, QuestionWords) || raw.TrimEnd().EndsWith('?');
        // "ทำไม build ช้าจัง" / "how does the build work?" ask about work; they do not ask for it. A short question with no
        // request to act ("please", "ช่วย", "แก้", "fix"…) stays a question.
        if (action && question && len <= 160 && files <= 1 && !raw.Contains("```") && !Has(m, StrongActionWords)) action = false;

        int level;
        string why;
        if (len == 0 || (len <= 60 && !action && !bug && !plan && files == 0)) { level = 0; why = question ? "question" : "chat"; }
        else if (!action && !bug && !plan && !multiFile && !longSpec) { level = len > 400 ? 1 : 0; why = "question"; }
        else { level = 1; why = "task"; }

        // The hard kinds of work start higher. The strongest reason is the one named.
        if (multiFile && action) { level = Math.Max(level, 2); why = "multiFile"; }
        if (plan) { level = Math.Max(level, 2); why = "plan"; }
        if (bug) { level = Math.Max(level, 2); why = "bug"; }
        if (longSpec && action) { if (level < 2) why = "longSpec"; level = Math.Max(level, 2); }
        if (longSpec && (bug || plan) && (len >= 4000 || items >= 8)) { level = 3; why = "bigTask"; }

        // "Still broken" after an earlier try: the last attempt was not enough.
        if (retry && (action || bug) && level < 3) { level++; why = "retry"; }

        // A picture to read (a screenshot of an error, a design to copy) and a long conversation need some thought.
        if (input.Images > 0 && level < 1) { level = 1; why = "image"; }
        if (input.ContextTokens >= 120_000 && level < 1) { level = 1; why = "bigContext"; }

        // "ต่อ" / "แก้เลย" has no task in its words: it carries on the last one (xhigh stays for what a brief says itself).
        if (IsGoAhead(m) && CarriedLevel(input.Earlier) is int carried && carried > level) { level = carried; why = "carryOn"; }

        return new Decision(Math.Clamp(level, 0, 3), why) { PlanFirst = longSpec && action && level < 3 };
    }

    /// <summary>The level of the last message that held a task, at least medium and at most high; null if there is none.</summary>
    private static int? CarriedLevel(IReadOnlyList<string>? earlier)
    {
        foreach (var message in (earlier ?? []).Reverse())
        {
            if (IsGoAhead(OwnWords(message).ToLowerInvariant())) continue;
            return Math.Clamp(Pick(new Input(message)).Level, 1, 2);
        }
        return null;
    }

    private static bool IsGoAhead(string lower)
    {
        string words = lower.Trim().TrimEnd('.', '!', ' ');
        foreach (var ending in Politeness)
            if (words.EndsWith(ending, StringComparison.Ordinal)) words = words[..^ending.Length].TrimEnd();
        return GoAheads.Contains(words);
    }

    private static string Without(string lower, string[] phrases)
    {
        foreach (var phrase in phrases) lower = lower.Replace(phrase, " ", StringComparison.Ordinal);
        return lower;
    }

    private static bool Has(string lower, string[] words)
    {
        foreach (var w in words)
        {
            int i = lower.IndexOf(w, StringComparison.Ordinal);
            while (i >= 0)
            {
                // "ทำไม" (why) is not "ทำ" (do). English words start at a word: "change" is not "hang", "report " is not
                // "port ", and "fixed width" is still "fix".
                bool startsWord = i == 0 || !char.IsAsciiLetter(w[0]) || !char.IsAsciiLetterOrDigit(lower[i - 1]);
                if (startsWord && !(w == "ทำ" && lower.AsSpan(i).StartsWith("ทำไม"))) return true;
                i = lower.IndexOf(w, i + w.Length, StringComparison.Ordinal);
            }
        }
        return false;
    }

    /// <summary>Distinct file names mentioned (foo.cs, src/app.tsx, README.md).</summary>
    private static int FileMentions(string text) =>
        FileName.Matches(text).Select(x => x.Value.ToLowerInvariant()).Distinct().Count();

    private static readonly Regex FileName = new(
        @"(?<![\w.])[\w\-/\\]+\.(cs|xaml|csproj|ts|tsx|js|jsx|mjs|py|go|rs|java|kt|swift|dart|cpp|cc|c|h|hpp|php|rb|vue|svelte|css|scss|html|json|yaml|yml|toml|sql|md|ps1|sh|gradle|xml)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const int EarlierLooked = 8;
    private const string RecallLateMark = "[The app's automatic BrainX recall did not answer in time.";

    private static readonly Regex RecallBlock = new(@"<brainx_recall_(\w+)>.*?</brainx_recall_\1>", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex ListItem = new(@"(?m)^\s*(?:\d+[.)]|[-*•])\s+\S", RegexOptions.CultureInvariant);

    private static readonly Regex StackTrace = new(
        @"(?m)(^\s+at \S+\(.*\)|Traceback \(most recent call last\)|\b\w+(Exception|Error):|error (CS|TS)\d{3,5}|panicked at|Unhandled exception)",
        RegexOptions.CultureInvariant);

    private static readonly string[] BugWords =
    [
        "bug", "error", "exception", "crash", "stack trace", "traceback", "broken", "doesn't work", "does not work",
        "not working", "fails", "failing", "failed", "regression", "debug", "freeze", "hang", "leak", "stuck", "timed out",
        "not found", "not showing", "doesn't open", "doesn't show",
        "บั๊ก", "บัก", "error", "ผิดพลาด", "พัง", "ไม่ทำงาน", "ใช้ไม่ได้", "แครช", "ค้าง", "เด้ง", "ไม่ขึ้น", "ไม่แสดง", "หาสาเหตุ",
        "ไม่เปิด", "เปิดไม่", "เข้าไม่ได้", "ล้มเหลว", "ดับไป", "ไม่ปรากฏ", "ไม่ปรากฎ", "ไม่เลื่อน", "ไม่ขยับ", "ไม่ตอบ", "หายไป",
        "ผิดปกติ", "ไม่เป็นดังต้องการ", "ไม่มีอะไรเกิดขึ้น",
    ];

    /// <summary>Read as trouble by the lists above, but they are not: "งานค้าง" is unfinished work, not a hang.</summary>
    private static readonly string[] NotTrouble =
    [
        "งานค้าง", "งานที่ค้าง", "เทสต์ที่ค้าง", "เทสที่ค้าง", "ที่ค้างอยู่", "ติดค้าง", "ไม่เปิดเผย", "ต้องไม่หาย", "ไม่ให้หาย",
    ];

    private static readonly string[] PlanWords =
    [
        "plan", "architecture", "architect", "design the", "redesign", "refactor", "migrate", "migration", "restructure",
        "audit", "security review", "whole project", "entire codebase", "end-to-end", "roadmap",
        "วางแผน", "ออกแบบ", "สถาปัตย", "รีแฟกเตอร์", "ย้ายระบบ", "ทั้งโปรเจกต์", "ทั้งโปรเจค", "ทั้งระบบ", "ตรวจความปลอดภัย",
    ];

    private static readonly string[] MultiFileWords =
    [
        "multiple files", "several files", "every file", "all files", "across the", "each page", "all pages",
        "หลายไฟล์", "ทุกไฟล์", "ทุกหน้า", "หลายหน้า", "ทุกค่าย", "ทุกจุด",
    ];

    private static readonly string[] RetryWords =
    [
        "still not", "still broken", "still fails", "still doesn't", "still the same", "same error", "again", "didn't fix", "not fixed",
        "ยังไม่ได้", "ยังพัง", "ยังไม่หาย", "ยังเหมือนเดิม", "ยังไม่ทำงาน", "ยังเป็นอยู่", "ยังไม่แก้", "ไม่หาย", "อีกแล้ว", "ยังไม่เป็น",
    ];

    private static readonly string[] ActionWords =
    [
        "implement", "add ", "create", "build", "fix", "write ", "change", "update", "rename", "move ", "delete", "remove",
        "replace", "wire ", "extract", "generate", "make ", "install", "setup", "set up", "convert", "port ", "optimi",
        "rebuild", "rewrite", "bugfix", "hotfix", "release", "deploy", "merge", "publish", "commit", "push", "run ", "test ",
        "แก้", "เพิ่ม", "สร้าง", "ทำ", "เขียน", "ย้าย", "ลบ", "ปรับ", "เปลี่ยน", "ติดตั้ง", "อัปเดต", "อัพเดต", "แปลง", "เชื่อม", "ใส่",
        "ปล่อยรุ่น", "ปล่อยเวอร์ชั", "ออกรุ่น", "ดีพลอย", "เมิร์ช", "เมิร์จ", "พุช", "คอมมิท", "คอมมิต", "รันเทส", "ตรวจ", "เช็ค", "เช็ก",
    ];

    private static readonly string[] StrongActionWords =
    [
        "please", "can you", "could you", "implement", "fix", "add ", "create", "write ", "make ", "change ", "update ", "remove ", "delete ",
        "ช่วย", "แก้", "เพิ่ม", "สร้าง", "เขียน", "ลบ", "ทำให้", "จัดการ", "ขอ",
    ];

    private static readonly string[] QuestionWords =
    [
        "what", "why", "how", "which", "explain", "is it", "can you tell",
        "อะไร", "ทำไม", "ยังไง", "อย่างไร", "ไหม", "มั้ย", "หรือเปล่า", "คืออะไร", "อธิบาย", "หรือยัง", "รึยัง", "ไหน", "เหรอ", "หรือไม่",
    ];

    private static readonly HashSet<string> GoAheads =
    [
        "ต่อ", "ต่อเลย", "ต่อไป", "ไปต่อ", "ทำต่อ", "ทำต่อเลย", "ทำเลย", "แก้ต่อ", "แก้เลย", "แก้ต่อเลย", "ลุย", "ลุยเลย", "ลุยต่อ",
        "เอาเลย", "จัดไป", "จัดเลย", "ตามนั้น", "ตามนั้นเลย", "ทำตามนั้น", "continue", "go on", "go ahead", "proceed", "do it",
        "keep going", "carry on",
    ];

    private static readonly string[] Politeness = ["ค่ะ", "คะ", "ครับ", "นะ", "สิ", "ด้วย", "หน่อย"];
}
