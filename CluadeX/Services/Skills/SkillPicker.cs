using System.Text;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services.Skills;

/// <summary>Why a skill was put in front of the model this turn.</summary>
public enum PickReason
{
    /// <summary>The owner named it ("/security-scan", "ช่วย commit").</summary>
    Named,
    /// <summary>Pinned on the Skills page: offered on every turn.</summary>
    Pinned,
    /// <summary>The message's words match its name / description / when-to-use.</summary>
    Matched,
    /// <summary>Words like these came with earlier turns that ran it.</summary>
    Learned,
    /// <summary>The brain's recall for this task points at it.</summary>
    Brain,
}

public sealed record SkillPickEntry(SkillDefinition Skill, double Score, PickReason Reason, IReadOnlyList<string> Matched);

/// <summary>The skills one turn offers the model, and what that saved against offering every switched-on one.</summary>
public sealed class SkillPick
{
    public SkillPick(IReadOnlyList<SkillPickEntry> picked, int offered, int tokensPicked, int tokensAll)
    {
        Picked = picked; Offered = offered; TokensPicked = tokensPicked; TokensAll = tokensAll;
    }

    public IReadOnlyList<SkillPickEntry> Picked { get; }
    /// <summary>How many switched-on skills were looked at.</summary>
    public int Offered { get; }
    /// <summary>What the picked list costs this turn.</summary>
    public int TokensPicked { get; }
    /// <summary>What listing every switched-on skill would have cost.</summary>
    public int TokensAll { get; }
    public IEnumerable<string> Names => Picked.Select(p => p.Skill.Name);

    /// <summary>The text that goes after the turn's message (empty when nothing was picked). It rides on the turn, not
    /// in the system prompt, so a different pick each turn never invalidates the prompt cache of the turns before it.</summary>
    public string BlockFor(bool isThai)
    {
        if (Picked.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine(isThai
            ? $"[Skill ที่เข้ากับงานนี้ — CluadeX เลือกให้จาก {Offered} ตัวที่เปิดไว้ เรียกด้วย skill_invoke เมื่อตัวไหนเหมาะกับงาน "
              + "(skill_invoke แบบไม่ใส่ชื่อ = ดูรายการที่เปิดไว้ทั้งหมด)]"
            : $"[Skills that fit this task — picked by CluadeX from the {Offered} switched on. Run one with skill_invoke when it fits "
              + "(skill_invoke with no name lists all of them)]");
        foreach (var p in Picked) sb.AppendLine(SkillBudget.ListingLine(p.Skill, isThai));
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Picks the switched-on skills that fit THIS turn, before the model starts (owner 2026-11-07: "ระบบ เลือกเปิดสกิล อัตโนมัติ
/// ที่สอดคล้องกับงาน … ก่อนจะทำงานก็เลือกก่อนควรใช้สกิลไหนบ้าง ให้สมองช่วยเลือกได้ยิ่งดี"). No model call, no network: the
/// owner's words, a Thai→English word table (installed skills are written in English, the owner writes Thai), words learned
/// from earlier turns that ran a skill, and the brain's recall for the task (already fetched for the turn) weigh in. Words
/// rare among the skills count more than words every skill has. Pure, for the harness.
/// </summary>
public static class SkillPicker
{
    public const int DefaultMax = 5;
    /// <summary>A skill below this score is not offered (one strong word in its name, or two in its description).</summary>
    public const double Threshold = 3.0;
    private const double BrainWeight = 0.35;
    /// <summary>A matched skill scoring under this share of the best match is dropped as noise.</summary>
    public const double RelativeFloor = 0.35;
    /// <summary>Learned words count only when at least this many of a skill's learned words are in the task.</summary>
    public const int MinLearnedHits = 2;

    public sealed record Input(
        string Message,
        string? BrainText,
        IReadOnlyList<SkillDefinition> Offered,
        IReadOnlyCollection<string> Pinned,
        Func<string, IReadOnlyDictionary<string, int>?>? Learned,
        int BudgetTokens,
        int Max,
        bool IsThai,
        IReadOnlyList<string>? BrainRanked = null);

    public static SkillPick Pick(Input input)
    {
        var offered = input.Offered;
        int tokensAll = offered.Count == 0 ? 0
            : SkillBudget.HeaderTokens + offered.Sum(s => SkillBudget.ListingTokens(s, input.IsThai));
        if (offered.Count == 0) return new SkillPick(Array.Empty<SkillPickEntry>(), 0, 0, 0);

        string message = input.Message ?? "";
        var msgTerms = Terms(message).ToHashSet(StringComparer.Ordinal);
        var brainTerms = Terms(input.BrainText).Where(t => !msgTerms.Contains(t)).Take(80).ToHashSet(StringComparer.Ordinal);

        // Each skill's words: name 3, when-to-use 1.5, description 1 (the strongest place a word appears counts).
        var docs = offered.Select(s => (Skill: s, Doc: DocTerms(s),
                Learned: input.Learned?.Invoke(s.Name) ?? new Dictionary<string, int>()))
            .ToList();
        int n = docs.Count;
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in docs)
            foreach (var t in d.Doc.Keys.Concat(d.Learned.Keys).Distinct())
                df[t] = df.TryGetValue(t, out var c) ? c + 1 : 1;
        double Idf(string t) => 1 + Math.Log((double)n / Math.Max(1, df.TryGetValue(t, out var c) ? c : 1));

        var pinned = new HashSet<string>(input.Pinned ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var scored = new List<SkillPickEntry>();
        foreach (var (skill, doc, learned) in docs)
        {
            var matched = new List<string>();
            double fromMsg = 0, fromLearned = 0, fromBrain = 0;
            int learnedHits = 0;
            foreach (var q in msgTerms)
            {
                bool hit = false;
                if (doc.TryGetValue(q, out var w)) { fromMsg += w * Idf(q); hit = true; }
                if (learned.TryGetValue(q, out var cnt)) { fromLearned += 0.6 * Math.Min(cnt, 3) * Idf(q); learnedHits++; hit = true; }
                if (hit) matched.Add(q);
            }
            // One learned word in common is a coincidence ("keep", "flow" — learned from a turn about something else): the
            // learned words count only when two of them are in the task.
            if (learnedHits < MinLearnedHits) fromLearned = 0;
            foreach (var q in brainTerms)
                if (doc.TryGetValue(q, out var w)) fromBrain += w * Idf(q);
            fromBrain = Math.Min(3.0, fromBrain * BrainWeight);
            // The brain's own ranking of the skill cards for this task (SkillBrainIndex): its first place alone is enough (+3),
            // the next ones need a word of the task too (+2.5, +2, then +1) — a semantic search always fills its list.
            if (input.BrainRanked is { Count: > 0 } ranked)
            {
                int at = ranked.ToList().FindIndex(r => r.Equals(skill.Name, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) fromBrain += at <= 2 ? 3.0 - 0.5 * at : 1.0;
            }
            double score = fromMsg + fromLearned + fromBrain;

            if (IsNamed(skill.Name, message))
                scored.Add(new SkillPickEntry(skill, 1000 + score, PickReason.Named, matched));
            else if (pinned.Contains(skill.Name))
                scored.Add(new SkillPickEntry(skill, 500 + score, PickReason.Pinned, matched));
            else if (score >= Threshold)
            {
                var reason = fromMsg >= fromLearned && fromMsg >= fromBrain ? PickReason.Matched
                    : fromLearned >= fromBrain ? PickReason.Learned : PickReason.Brain;
                scored.Add(new SkillPickEntry(skill, score, reason, matched));
            }
        }

        int max = input.Max > 0 ? input.Max : DefaultMax;
        int budget = Math.Max(0, input.BudgetTokens);
        var picked = new List<SkillPickEntry>();
        int tokens = SkillBudget.HeaderTokens;
        // A match far weaker than the best one is noise ("scan" in "Competitive scan" next to security-scan for a security
        // task): it needs at least RelativeFloor of the best matched score. Named and pinned skills do not count here.
        double best = scored.Where(e => e.Reason is not (PickReason.Named or PickReason.Pinned)).Select(e => e.Score).DefaultIfEmpty(0).Max();
        foreach (var e in scored.OrderByDescending(e => e.Score).ThenBy(e => e.Skill.Name, StringComparer.OrdinalIgnoreCase))
        {
            int cost = SkillBudget.ListingTokens(e.Skill, input.IsThai);
            bool must = e.Reason is PickReason.Named or PickReason.Pinned;
            if (!must && e.Score < best * RelativeFloor) continue;
            if (!must && (picked.Count >= max || tokens + cost > budget)) continue;
            picked.Add(e);
            tokens += cost;
        }
        return new SkillPick(picked, n, picked.Count == 0 ? 0 : tokens, tokensAll);
    }

    /// <summary>"/name", the hyphenated name itself, or a name of 6+ letters as a whole word ("ช่วย commit ให้").</summary>
    public static bool IsNamed(string name, string? message)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(message)) return false;
        string esc = Regex.Escape(name);
        if (Regex.IsMatch(message, $@"(?<![\w/-])/{esc}(?![\w-])", RegexOptions.IgnoreCase)) return true;
        if (name.Contains('-') || name.Length >= 6)
            return Regex.IsMatch(message, $@"(?<![A-Za-z0-9_-]){esc}(?![A-Za-z0-9_-])", RegexOptions.IgnoreCase);
        return false;
    }

    private static Dictionary<string, double> DocTerms(SkillDefinition s)
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        void Add(string? text, double w)
        {
            foreach (var t in Terms(text))
                if (!d.TryGetValue(t, out var old) || old < w) d[t] = w;
        }
        Add(s.Description, 1.0);
        Add(s.WhenToUse, 1.5);
        Add(s.Name.Replace('-', ' ').Replace('_', ' '), 3.0);
        return d;
    }

    // ── words ───────────────────────────────────────────────────────────

    private static readonly Regex LatinWord = new(@"[a-z][a-z0-9+#]*", RegexOptions.Compiled);

    private static readonly HashSet<string> TwoLetter = new(StringComparer.Ordinal) { "pr", "ui", "ux", "ci", "db", "qa", "js", "ts", "go" };

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "this", "that", "these", "those", "from", "into", "onto", "your", "you", "use", "used",
        "using", "uses", "when", "user", "users", "ask", "make", "made", "file", "code", "any", "all", "can", "will", "would",
        "should", "are", "have", "has", "had", "not", "its", "it's", "skill", "about", "more", "what", "how", "help", "need",
        "want", "like", "also", "them", "then", "than", "only", "each", "other", "one", "two", "new", "get", "set", "out",
        "via", "per", "but", "our", "who", "why", "where", "which", "there", "their", "they", "was", "were", "been", "being",
        "please", "just", "some", "very", "much", "many", "may", "might", "must", "let", "now", "here", "after", "before",
        "over", "under", "again", "same", "own", "well", "way", "thing", "things", "able", "etc", "e.g", "i.e", "task",
        "work", "run", "runs", "yes", "no", "ok", "okay", "thanks", "thank", "claude", "cluadex", "anthropic",
    };

    /// <summary>Thai words → the English words skills are described with. Matched as substrings (Thai has no spaces).</summary>
    private static readonly (string Thai, string[] En)[] ThaiWords =
    {
        ("ทดสอบ", new[] { "test", "testing" }), ("เทสต์", new[] { "test" }), ("เทส", new[] { "test" }),
        ("ยูนิตเทส", new[] { "unit", "test" }), ("ทีดีดี", new[] { "tdd", "test" }),
        ("ความปลอดภัย", new[] { "security", "secure" }), ("ปลอดภัย", new[] { "security" }), ("ช่องโหว่", new[] { "security", "vulnerability" }),
        ("แฮก", new[] { "security", "exploit" }), ("รหัสผ่าน", new[] { "secret", "security" }), ("ความลับ", new[] { "secret" }),
        ("คอมมิต", new[] { "commit", "git" }), ("คอมมิท", new[] { "commit", "git" }), ("กิต", new[] { "git" }),
        ("รีวิว", new[] { "review" }), ("ตรวจโค้ด", new[] { "review" }), ("ตรวจสอบโค้ด", new[] { "review" }), ("พีอาร์", new[] { "pr", "pull", "request" }),
        ("ลดความซับซ้อน", new[] { "simplify" }), ("ให้ง่ายขึ้น", new[] { "simplify" }), ("ให้สะอาด", new[] { "simplify", "quality" }),
        ("รีแฟคเตอร์", new[] { "refactor", "simplify" }), ("รีแฟกเตอร์", new[] { "refactor", "simplify" }), ("โค้ดซ้ำ", new[] { "duplication", "simplify" }),
        ("ค้นคว้า", new[] { "research" }), ("วิจัย", new[] { "research" }), ("หาข้อมูล", new[] { "research", "investigation" }), ("สืบค้น", new[] { "research" }),
        ("ต้นทุน", new[] { "cost" }), ("ค่าใช้จ่าย", new[] { "cost" }), ("ประหยัด", new[] { "cost", "cheapest" }), ("ค่าโทเค็น", new[] { "cost", "token" }),
        ("โทเค็น", new[] { "token", "context" }), ("บริบท", new[] { "context" }), ("คอนเท็กซ์", new[] { "context" }), ("หน้าต่างบริบท", new[] { "context", "window" }),
        ("คอนเทนต์", new[] { "content" }), ("บทความ", new[] { "content", "blog" }), ("โพสต์", new[] { "content", "social", "post" }), ("โพส", new[] { "content", "social", "post" }),
        ("แคปชั่น", new[] { "content", "social" }), ("อีเมล", new[] { "email" }), ("จดหมาย", new[] { "email", "letter" }),
        ("คู่แข่ง", new[] { "competitive", "competitor", "market" }), ("การตลาด", new[] { "market", "marketing" }), ("ตลาด", new[] { "market" }), ("ราคาขาย", new[] { "pricing" }),
        ("วางแผน", new[] { "plan" }), ("แผนงาน", new[] { "plan" }), ("ทางเลือก", new[] { "alternative", "plan" }), ("หลายแบบ", new[] { "alternative" }),
        ("คอมเมนต์", new[] { "comment" }), ("คอมเม้นต์", new[] { "comment" }), ("อธิบายโค้ด", new[] { "comment", "explain" }), ("ใส่คำอธิบาย", new[] { "comment" }),
        ("ประเมินผล", new[] { "eval", "evaluation" }), ("วัดผล", new[] { "eval", "metric" }),
        ("บั๊ก", new[] { "bug", "debug", "fix" }), ("ดีบัก", new[] { "debug" }), ("แก้บั๊ก", new[] { "bug", "fix" }),
        ("ค้าง", new[] { "stuck" }), ("วนลูป", new[] { "loop", "stuck" }), ("มั่ว", new[] { "hallucinating" }),
        ("อัตโนมัติ", new[] { "autonomous", "automation" }), ("ทำเองจนเสร็จ", new[] { "autonomous", "loop" }),
        ("หน้าเว็บ", new[] { "web", "html", "frontend" }), ("เว็บไซต์", new[] { "web", "website" }), ("เว็บแอป", new[] { "web", "app" }), ("เว็บ", new[] { "web" }),
        ("ออกแบบ", new[] { "design" }), ("ดีไซน์", new[] { "design" }), ("หน้าตา", new[] { "design", "ui" }), ("ธีม", new[] { "theme" }),
        ("สไลด์", new[] { "slides", "presentation", "pptx", "deck" }), ("พรีเซนต์", new[] { "presentation", "slides" }), ("นำเสนอ", new[] { "presentation" }),
        ("เอกสาร", new[] { "document", "docx" }), ("เวิร์ด", new[] { "word", "docx" }), ("สเปรดชีต", new[] { "spreadsheet", "xlsx", "excel" }),
        ("เอ็กเซล", new[] { "excel", "xlsx", "spreadsheet" }), ("ตาราง", new[] { "table", "spreadsheet" }), ("พีดีเอฟ", new[] { "pdf" }),
        ("รูปภาพ", new[] { "image" }), ("ภาพ", new[] { "image" }), ("กราฟ", new[] { "chart", "graph" }), ("ชาร์ต", new[] { "chart" }),
        ("แอนิเมชัน", new[] { "animation", "animated" }), ("ภาพเคลื่อนไหว", new[] { "animation", "gif" }), ("ศิลปะ", new[] { "art" }), ("โปสเตอร์", new[] { "poster", "design" }),
        ("แบรนด์", new[] { "brand" }), ("ปลั๊กอิน", new[] { "plugin" }), ("เซิร์ฟเวอร์", new[] { "server" }), ("เอ็มซีพี", new[] { "mcp" }),
        ("สร้างสกิล", new[] { "skill", "create" }), ("ทำสกิล", new[] { "skill", "create" }), ("ฮาร์เนส", new[] { "harness" }),
        ("ระบบทดสอบ", new[] { "harness", "test" }), ("เอนด์ทูเอนด์", new[] { "e2e", "end" }), ("สื่อสารภายใน", new[] { "internal", "communications" }),
        ("รายงาน", new[] { "report" }), ("สรุป", new[] { "summary" }), ("แปล", new[] { "translate" }),
        ("ประสิทธิภาพ", new[] { "performance", "efficiency" }), ("เร็วขึ้น", new[] { "performance" }), ("เอเจนต์", new[] { "agent" }),
        ("เวิร์กทรี", new[] { "worktree" }), ("ขนาน", new[] { "parallel" }), ("พร้อมกัน", new[] { "parallel" }),
    };

    /// <summary>The words of <paramref name="text"/> as the picker counts them: Latin words lower-cased and stemmed, stop
    /// words dropped, Thai words turned into their English words. Distinct, in order of first appearance.</summary>
    public static IReadOnlyList<string> Terms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        if (text.Length > 20_000) text = text[..20_000];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        void Put(string t) { if (seen.Add(t)) list.Add(t); }

        foreach (Match m in LatinWord.Matches(text.ToLowerInvariant()))
        {
            string w = m.Value;
            if (w.Length < 2 || (w.Length == 2 && !TwoLetter.Contains(w))) continue;
            w = Stem(w);
            if (w.Length < 2 || Stop.Contains(w)) continue;
            Put(w);
        }
        if (HasThai(text))
            foreach (var (thai, en) in ThaiWords)
                if (text.Contains(thai, StringComparison.Ordinal))
                    foreach (var e in en) Put(Stem(e));
        return list;
    }

    private static bool HasThai(string s)
    {
        foreach (char c in s) if (c >= '฀' && c <= '๿') return true;
        return false;
    }

    /// <summary>Light English stemming: tests/testing/tested → test, reviews → review, libraries → library.</summary>
    public static string Stem(string w)
    {
        if (w.Length > 4 && w.EndsWith("ies")) return w[..^3] + "y";
        if (w.Length > 5 && w.EndsWith("ing")) return Undouble(w[..^3]);
        if (w.Length > 4 && w.EndsWith("ed") && !w.EndsWith("eed")) return Undouble(w[..^2]);
        if (w.Length > 4 && (w.EndsWith("ches") || w.EndsWith("shes") || w.EndsWith("sses") || w.EndsWith("xes"))) return w[..^2];
        if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss") && !w.EndsWith("us") && !w.EndsWith("is")) return w[..^1];
        return w;
    }

    private static string Undouble(string w) =>
        w.Length > 3 && w[^1] == w[^2] && w[^1] is not ('l' or 's' or 'z') ? w[..^1] : w;
}
