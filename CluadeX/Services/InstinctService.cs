using System.IO;
using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Backend for the Instinct System (Sprint 2 #1 — ECC parity).
///
/// Sprint 2 #1 scope (v1):
///   - JSON-backed store at ~/.cluadex/instincts.json
///   - CRUD + accept/reject voting + confidence (computed on the model)
///   - PromoteToSkill — writes ~/.cluadex/skills/promoted-{name}.md so the
///     existing SkillService picks it up on next scan
///   - RecordObservation — bumps occurrence + LastSeen (called by Stop hook
///     or by the AI itself via the upcoming `instinct_record` tool)
///   - Export / Import (a single JSON snapshot — for team sharing)
///
/// v2 (2026-11-10 — the loop actually closes):
///   - SessionMemoryService extracts from finished chats (idle, switched away, new chat; split chats too) and shows
///     the extractor the existing instincts, so a reworded sighting lands on the same one ("same_as"); a word match
///     (<see cref="LooksSame"/>) catches the rest without an embedding model
///   - a chat counts once per instinct; two chats, one verified user correction, or the owner's ✓ put it in the prompt
///   - instincts belong to a project (<see cref="ProjectKey"/>) or are global; the prompt gets global + this project's
/// </summary>
public class InstinctService
{
    private readonly string _storePath;
    private readonly string _inboxPath;
    private readonly object _lock = new();
    private InstinctStore? _cache;

    /// <summary>Raised when the store mutates (UI re-binds).</summary>
    public event Action? Changed;

    /// <summary>The app's own store (the one DI made) — for the CLI providers, which build their prompt themselves
    /// (<see cref="LearnedHabits"/>). Null in a process that never made one.</summary>
    public static InstinctService? Current { get; private set; }

    /// <summary>The app's store, in the data root (<see cref="SettingsService.ResolveDataRoot"/>): ~/.cluadex, or
    /// &lt;exe&gt;\Data in a portable copy. It used to be ~/.cluadex always, so a test harness built on the app's DI
    /// graph read and wrote the owner's real instincts.</summary>
    public InstinctService() : this(null) { }

    /// <summary>A store in <paramref name="storeDir"/> (tests); null = the data root, and this becomes <see cref="Current"/>.</summary>
    public InstinctService(string? storeDir)
    {
        string dir = storeDir ?? SettingsService.ResolveDataRoot(out _);
        Directory.CreateDirectory(dir);
        _storePath = Path.Combine(dir, "instincts.json");
        _inboxPath = Path.Combine(dir, InboxFileName);
        if (storeDir == null) Current = this;
    }

    public string StorePath => _storePath;

    /// <summary>JSONL a script may append observations to: one object per line with "pattern" (and optionally
    /// "trigger", "action", "project", "session"). Read and emptied on load and before each extraction.</summary>
    public const string InboxFileName = "instincts-inbox.jsonl";
    public string InboxPath => _inboxPath;

    // ─── Persistence ─────────────────────────────────────────────────

    private InstinctStore Load()
    {
        lock (_lock)
        {
            if (_cache != null) return _cache;
            try
            {
                if (File.Exists(_storePath))
                {
                    var json = File.ReadAllText(_storePath);
                    _cache = JsonSerializer.Deserialize<InstinctStore>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? new InstinctStore();
                    bool dirty = RemoveUntouchedDemoSeeds(_cache) > 0;
                    if (_cache.Version < InstinctStore.CurrentVersion)
                    {
                        Migrate(_cache, _storePath);
                        dirty = true;
                    }
                    if (dirty) Save(_cache);
                }
                else
                {
                    // A fresh install starts EMPTY. It used to be seeded with three made-up
                    // "learned" instincts (fake occurrence counts and dates) that went straight into
                    // every system prompt — including "always commit + push without asking".
                    _cache = new InstinctStore();
                }
            }
            catch
            {
                // Corrupt file — start fresh, don't crash. Backup the bad file.
                try { File.Move(_storePath, _storePath + ".bad-" + DateTime.Now.Ticks); } catch { }
                _cache = new InstinctStore();
            }
            if (DrainInboxLocked(_cache) > 0) Save(_cache);
            return _cache;
        }
    }

    private void Save(InstinctStore store)
    {
        lock (_lock)
        {
            store.LastModified = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(store, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(_storePath, json);
            _cache = store;
        }
        RaiseChangedDetached();
    }

    /// <summary>
    /// Fire <see cref="Changed"/> on a pool thread instead of the caller's.
    ///
    /// DEADLOCK FIX. Save() is deliberately called from INSIDE a mutator's
    /// lock (the Load → mutate → Save round-trip is one critical section), so
    /// raising the event inline ran subscribers while _lock was still held.
    /// InstinctsViewModel's subscriber does Dispatcher.Invoke(Refresh) —
    /// blocking — and Refresh calls GetAll(), which wants that same _lock.
    /// Result: the background thread waited on the UI thread while the UI
    /// thread waited on the background thread's lock. Captured live with
    /// dotnet-stack on 2026-07-29: three threads, one in Save/RecordObservation
    /// (from SessionMemoryService.ExtractInstinctsAsync), two parked in
    /// GetAll() — one of them the UI thread, one building the system prompt.
    ///
    /// The app froze for 11 minutes, WPF then failed to allocate render
    /// resources ("Not enough quota is available"), queued MCP work never
    /// started, and the window would not close.
    ///
    /// Detaching means subscribers always run with no lock held by us. The
    /// notification is inherently async anyway — nobody may assume it has
    /// been delivered by the time Save() returns.
    /// </summary>
    private void RaiseChangedDetached()
    {
        var handler = Changed;
        if (handler == null) return;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try { handler(); } catch { /* a UI refresh must never kill a save */ }
        });
    }

    public List<Instinct> GetAll()
    {
        // Snapshot under the same lock that protects mutations to avoid
        // reading a half-mutated list. Returned list is detached from the
        // store so callers can sort/enumerate without holding the lock.
        lock (_lock)
        {
            var store = Load();
            return store.Instincts.OrderByDescending(i => i.Confidence).ToList();
        }
    }

    public Instinct? GetById(string id)
    {
        lock (_lock)
        {
            var store = Load();
            return store.Instincts.FirstOrDefault(i => i.Id == id);
        }
    }

    // ─── Mutations ───────────────────────────────────────────────────
    // FIX (audit CRITICAL #1): every public mutator now wraps the
    // Load → mutate → Save round-trip in a single lock so a concurrent
    // UI vote + background brain-sync persist can't silently drop one
    // another's writes. Save() does NOT take the lock again — it relies
    // on the caller already holding it.

    public Instinct Add(Instinct instinct)
    {
        lock (_lock)
        {
            var store = Load();
            if (string.IsNullOrEmpty(instinct.Id))
                instinct.Id = Guid.NewGuid().ToString("N")[..8];
            if (instinct.FirstSeen == default) instinct.FirstSeen = DateTime.UtcNow;
            instinct.LastSeen = DateTime.UtcNow;
            store.Instincts.Add(instinct);
            Save(store);
            return instinct;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var store = Load();
            var hit = store.Instincts.FirstOrDefault(i => i.Id == id);
            if (hit == null) return false;
            store.Instincts.Remove(hit);
            Save(store);
            return true;
        }
    }

    /// <summary>User confirmed this is a real pattern → boosts confidence.</summary>
    public void Accept(string id)
    {
        lock (_lock)
        {
            var store = Load();
            var hit = store.Instincts.FirstOrDefault(i => i.Id == id);
            if (hit == null) return;
            hit.AcceptCount++;
            hit.LastSeen = DateTime.UtcNow;
            Save(store);
        }
    }

    /// <summary>User said no → drops confidence so it stops surfacing.</summary>
    public void Reject(string id)
    {
        lock (_lock)
        {
            var store = Load();
            var hit = store.Instincts.FirstOrDefault(i => i.Id == id);
            if (hit == null) return;
            hit.RejectCount++;
            hit.LastSeen = DateTime.UtcNow;
            Save(store);
        }
    }

    /// <summary>A pattern seen again, with nothing known about where (a script, the old callers). Counted every call.</summary>
    public void RecordObservation(string pattern, string? trigger = null, string? action = null) =>
        RecordSighting(new Sighting(pattern, trigger, action));

    /// <summary>One sighting of a pattern in a chat. <see cref="SameAsId"/>: the extractor said it is this existing
    /// instinct reworded. <see cref="ProjectScoped"/>: it is a rule of this project, not of every project.
    /// <see cref="Correction"/>: the user corrected the assistant into it, and their words were found in the chat.</summary>
    public sealed record Sighting(string Pattern, string? Trigger = null, string? Action = null, string? SameAsId = null,
        string? SessionId = null, string? ProjectKey = null, string? ProjectName = null, bool ProjectScoped = false,
        bool Correction = false);

    /// <summary>What a sighting did: the instinct it landed on, whether that is a new one, and whether it counted
    /// (a chat already counted for this instinct does not count again).</summary>
    public sealed record SightingResult(Instinct Instinct, bool IsNew, bool Counted);

    /// <summary>Record one sighting: onto the instinct the extractor named, else onto one worded nearly the same
    /// (<see cref="LooksSame"/>) in the same scope, else as a new instinct. Null for an empty pattern.</summary>
    public SightingResult? RecordSighting(Sighting sighting)
    {
        if (string.IsNullOrWhiteSpace(sighting.Pattern)) return null;
        lock (_lock)
        {
            var store = Load();
            var result = ApplySighting(store, sighting, DateTime.UtcNow);
            Save(store);
            return result;
        }
    }

    private const int MaxSessionsKept = 50, MaxAliasesKept = 8;

    private static SightingResult ApplySighting(InstinctStore store, Sighting s, DateTime now)
    {
        string pattern = s.Pattern.Trim();
        string? key = s.ProjectKey;
        Instinct? hit = null;
        if (!string.IsNullOrWhiteSpace(s.SameAsId))
            hit = store.Instincts.FirstOrDefault(i => i.Id == s.SameAsId!.Trim() && VisibleIn(i, key));
        hit ??= store.Instincts.FirstOrDefault(i => VisibleIn(i, key)
            && (string.Equals(i.Pattern, pattern, StringComparison.OrdinalIgnoreCase)
                || i.Aliases.Any(a => string.Equals(a, pattern, StringComparison.OrdinalIgnoreCase))));
        hit ??= store.Instincts.Where(i => VisibleIn(i, key)).FirstOrDefault(i => LooksSame(i.Pattern, pattern)
            || i.Aliases.Any(a => LooksSame(a, pattern)));

        if (hit == null)
        {
            var fresh = new Instinct
            {
                Pattern = pattern,
                Trigger = s.Trigger?.Trim() ?? "",
                Action = s.Action?.Trim() ?? "",
                ProjectKey = s.ProjectScoped ? key : null,
                ProjectName = s.ProjectScoped && key != null ? s.ProjectName : null,
                CorrectionCount = s.Correction ? 1 : 0,
                FirstSeen = now,
                LastSeen = now,
            };
            if (!string.IsNullOrEmpty(s.SessionId)) fresh.SeenInSessions.Add(s.SessionId);
            store.Instincts.Add(fresh);
            return new SightingResult(fresh, IsNew: true, Counted: true);
        }

        bool counted = string.IsNullOrEmpty(s.SessionId) || !hit.SeenInSessions.Contains(s.SessionId);
        if (counted)
        {
            hit.OccurrenceCount++;
            if (s.Correction) hit.CorrectionCount++;
            if (!string.IsNullOrEmpty(s.SessionId))
            {
                hit.SeenInSessions.Add(s.SessionId);
                if (hit.SeenInSessions.Count > MaxSessionsKept) hit.SeenInSessions.RemoveAt(0);
            }
        }
        hit.LastSeen = now;
        if (!string.IsNullOrWhiteSpace(s.Trigger) && string.IsNullOrWhiteSpace(hit.Trigger)) hit.Trigger = s.Trigger.Trim();
        if (!string.IsNullOrWhiteSpace(s.Action) && string.IsNullOrWhiteSpace(hit.Action)) hit.Action = s.Action.Trim();
        if (!string.Equals(hit.Pattern, pattern, StringComparison.OrdinalIgnoreCase)
            && !hit.Aliases.Any(a => string.Equals(a, pattern, StringComparison.OrdinalIgnoreCase)))
        {
            hit.Aliases.Add(pattern);
            if (hit.Aliases.Count > MaxAliasesKept) hit.Aliases.RemoveAt(0);
        }

        // Scope: an instinct from before projects existed takes the project of its first scoped sighting; one rule
        // seen in a second project and called general there becomes global.
        if (hit.LegacyUnscoped)
        {
            hit.LegacyUnscoped = false;
            if (s.ProjectScoped && key != null) { hit.ProjectKey = key; hit.ProjectName = s.ProjectName; }
        }
        else if (hit.ProjectKey != null && key != null && !s.ProjectScoped
                 && !string.Equals(hit.ProjectKey, key, StringComparison.OrdinalIgnoreCase))
        {
            hit.ProjectKey = null;
            hit.ProjectName = null;
        }
        return new SightingResult(hit, IsNew: false, Counted: counted);
    }

    /// <summary>An instinct applies in a project: it is global, or it is that project's own.</summary>
    public static bool VisibleIn(Instinct i, string? projectKey) =>
        i.ProjectKey == null || (projectKey != null && string.Equals(i.ProjectKey, projectKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>Of <paramref name="ids"/>, the ones to ask the owner about now — still there, un-voted, not asked in
    /// the last <see cref="AskAgainAfter"/> — at most <paramref name="max"/>, marked as asked.</summary>
    public List<string> TakeToAsk(IEnumerable<string> ids, int max)
    {
        lock (_lock)
        {
            var store = Load();
            var now = DateTime.UtcNow;
            var take = new List<string>();
            foreach (var id in ids)
            {
                if (take.Count >= max) break;
                var i = store.Instincts.FirstOrDefault(x => x.Id == id);
                if (i == null || i.IsVoted || take.Contains(id)) continue;
                if (i.LastAskedUtc is { } at && now - at < AskAgainAfter) continue;
                i.LastAskedUtc = now;
                take.Add(id);
            }
            if (take.Count > 0) Save(store);
            return take;
        }
    }

    public static readonly TimeSpan AskAgainAfter = TimeSpan.FromDays(7);

    // ─── Read-back: what goes into the prompt ────────────────────────

    /// <summary>The instincts strong enough for the prompt in <paramref name="projectFolder"/> (null = no project:
    /// global ones only), best first: confidence ≥ <see cref="Instinct.InjectThreshold"/>, not rejected.</summary>
    public List<Instinct> ForPrompt(string? projectFolder, int max = 5)
    {
        string? key = ProjectKey(projectFolder);
        return GetAll()
            .Where(i => VisibleIn(i, key) && !i.IsRejected && !string.IsNullOrWhiteSpace(i.Pattern)
                        && i.Confidence >= Instinct.InjectThreshold)
            .Take(Math.Max(0, max))
            .ToList();
    }

    /// <summary>"- pattern → action" lines for the prompt, or null when none qualifies. Each one line, at most
    /// <see cref="MaxPromptLineChars"/> characters.</summary>
    public string? HabitsBlock(string? projectFolder, int max = 5)
    {
        var top = ForPrompt(projectFolder, max);
        if (top.Count == 0) return null;
        var sb = new StringBuilder();
        foreach (var i in top)
        {
            string line = OneLine(i.Pattern);
            if (!string.IsNullOrWhiteSpace(i.Action)) line += " → " + OneLine(i.Action);
            if (line.Length > MaxPromptLineChars) line = line[..(MaxPromptLineChars - 1)] + "…";
            sb.AppendLine("- " + line);
        }
        return sb.ToString().TrimEnd();
    }

    public const int MaxPromptLineChars = 220;

    private static string OneLine(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ");

    /// <summary>The instincts the extractor is shown so it can say "same as &lt;id&gt;" instead of rewording one:
    /// those that apply in this project (rejected ones too, so a "no" is matched, not re-learned), newest first.</summary>
    public List<Instinct> ForExtraction(string? projectKey, int max = 80) =>
        GetAll().Where(i => VisibleIn(i, projectKey)).OrderByDescending(i => i.LastSeen).Take(max).ToList();

    // ─── Project key ─────────────────────────────────────────────────

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ProjectKeyCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The project a folder belongs to: the root of its git checkout — a linked worktree counts as its main checkout
    /// (a split chat in D:\Code\X-worktrees\a learns for D:\Code\X) — else the folder itself. Lower-case full path
    /// without a trailing separator; null for no folder.
    /// </summary>
    public static string? ProjectKey(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        string full;
        try { full = Path.GetFullPath(folder).TrimEnd('\\', '/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (full.Length == 0) return null;
        return ProjectKeyCache.GetOrAdd(full, ResolveProjectKey);
    }

    private static string ResolveProjectKey(string full)
    {
        try
        {
            for (var d = new DirectoryInfo(full); d != null; d = d.Parent)
            {
                string git = Path.Combine(d.FullName, ".git");
                if (Directory.Exists(git)) return Norm(d.FullName);
                if (!File.Exists(git)) continue;
                // A linked worktree: ".git" is a file "gitdir: <main>/.git/worktrees/<name>".
                string line = File.ReadLines(git).FirstOrDefault() ?? "";
                if (line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                {
                    string gitdir = line[7..].Trim();
                    if (!Path.IsPathRooted(gitdir)) gitdir = Path.Combine(d.FullName, gitdir);
                    gitdir = Path.GetFullPath(gitdir).TrimEnd('\\', '/');
                    var parent = Directory.GetParent(gitdir);   // .../.git/worktrees
                    if (parent != null && parent.Name.Equals("worktrees", StringComparison.OrdinalIgnoreCase)
                        && parent.Parent is { } dotGit && dotGit.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                        && dotGit.Parent is { } main)
                        return Norm(main.FullName);
                }
                return Norm(d.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return Norm(full);
    }

    private static string Norm(string path) => path.TrimEnd('\\', '/').ToLowerInvariant();

    /// <summary>The folder name shown for a project key.</summary>
    public static string? ProjectNameOf(string? key) =>
        string.IsNullOrEmpty(key) ? null : Path.GetFileName(key.TrimEnd('\\', '/'));

    // ─── Word match (the no-Ollama dedupe) ───────────────────────────

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "on", "in", "of", "to", "for", "with", "at", "by", "from", "as", "into",
        "is", "are", "be", "been", "was", "were", "it", "its", "this", "that", "these", "those", "all", "only", "then",
        "than", "so", "do", "does", "when", "while", "any", "every", "each", "there", "here", "which", "who", "what",
        "must", "should", "always", "just", "very", "also", "your", "you", "user", "users", "their", "them", "they",
    };

    /// <summary>
    /// Two wordings of one rule, by words alone (no embedding model needed): the same content words — Jaccard ≥ 0.6,
    /// or the shorter one's words ≥ 85 % inside the longer (at least 4 words). Short or unspaced text (Thai) compares
    /// character pairs instead (Dice ≥ 0.8). "Never …" and "Always …" are never the same rule.
    /// "Never use the E: drive; work only on D:" ≈ "Never use or reference the E: drive; all work happens on D:".
    /// </summary>
    public static bool LooksSame(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        var ra = RawWords(a);
        var rb = RawWords(b);
        bool neverA = ra.Contains("never"), neverB = rb.Contains("never");
        bool alwaysA = ra.Contains("always"), alwaysB = rb.Contains("always");
        if ((neverA && alwaysB && !neverB) || (neverB && alwaysA && !neverA)) return false;
        var ta = Stems(ra);
        var tb = Stems(rb);
        if (ta.Count < 3 || tb.Count < 3) return Dice(Compact(a), Compact(b)) >= 0.8;
        int inter = ta.Count(tb.Contains);
        int union = ta.Count + tb.Count - inter;
        int min = Math.Min(ta.Count, tb.Count);
        double jaccard = (double)inter / union;
        double overlap = (double)inter / min;
        return jaccard >= 0.6 || (overlap >= 0.85 && min >= 4);
    }

    private static List<string> RawWords(string s)
    {
        var words = new List<string>();
        var cur = new StringBuilder();
        foreach (char raw in s.ToLowerInvariant())
        {
            if (raw is '\'' or '\u2019') continue;   // don't → dont
            var cat = char.GetUnicodeCategory(raw);
            if (char.IsLetterOrDigit(raw) || cat is System.Globalization.UnicodeCategory.NonSpacingMark
                    or System.Globalization.UnicodeCategory.SpacingCombiningMark)
                cur.Append(raw);
            else if (cur.Length > 0) { words.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) words.Add(cur.ToString());
        return words;
    }

    private static HashSet<string> Stems(List<string> words)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w0 in words)
        {
            if (StopWords.Contains(w0)) continue;
            string w = w0;
            if (w.Length > 5 && w.EndsWith("ing", StringComparison.Ordinal)) w = w[..^3];
            else if (w.Length > 4 && w.EndsWith("ed", StringComparison.Ordinal)) w = w[..^2];
            else if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal)) w = w[..^1];
            if (w.Length > 4 && w.EndsWith('e')) w = w[..^1];
            set.Add(w);
        }
        return set;
    }

    private static string Compact(string s) =>
        new string(s.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray());

    private static double Dice(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2) return a == b ? 1 : 0;
        var pa = new Dictionary<string, int>();
        for (int i = 0; i < a.Length - 1; i++) { string g = a.Substring(i, 2); pa[g] = pa.GetValueOrDefault(g) + 1; }
        int inter = 0;
        for (int i = 0; i < b.Length - 1; i++)
        {
            string g = b.Substring(i, 2);
            if (pa.TryGetValue(g, out int n) && n > 0) { inter++; pa[g] = n - 1; }
        }
        return 2.0 * inter / (a.Length - 1 + b.Length - 1);
    }

    // ─── Merge / migrate / inbox ─────────────────────────────────────

    /// <summary>Fold <paramref name="loser"/> into <paramref name="winner"/>, losing no learning: counts and votes add
    /// up, chats/tags/aliases join, the earliest first-seen and latest last-seen win, and the loser's wording is kept
    /// as an alias.</summary>
    internal static void MergeInto(Instinct winner, Instinct loser)
    {
        winner.OccurrenceCount += loser.OccurrenceCount;
        winner.AcceptCount += loser.AcceptCount;
        winner.RejectCount += loser.RejectCount;
        winner.CorrectionCount += loser.CorrectionCount;
        if (loser.LastSeen > winner.LastSeen) winner.LastSeen = loser.LastSeen;
        if (loser.FirstSeen < winner.FirstSeen) winner.FirstSeen = loser.FirstSeen;
        foreach (var t in loser.Tags) if (!winner.Tags.Contains(t)) winner.Tags.Add(t);
        foreach (var sid in loser.SeenInSessions) if (!winner.SeenInSessions.Contains(sid)) winner.SeenInSessions.Add(sid);
        foreach (var alias in new[] { loser.Pattern }.Concat(loser.Aliases))
            if (!string.IsNullOrWhiteSpace(alias) && !string.Equals(alias, winner.Pattern, StringComparison.OrdinalIgnoreCase)
                && !winner.Aliases.Any(a => string.Equals(a, alias, StringComparison.OrdinalIgnoreCase)))
                winner.Aliases.Add(alias);
        if (string.IsNullOrWhiteSpace(winner.Trigger)) winner.Trigger = loser.Trigger;
        if (string.IsNullOrWhiteSpace(winner.Action)) winner.Action = loser.Action;
        if (string.IsNullOrEmpty(winner.BrainNoteId)) winner.BrainNoteId = loser.BrainNoteId;
    }

    /// <summary>Merge instincts the word match says are one rule, within one scope. Promoted ones stay as they are
    /// (they have a skill file). Returns how many were folded away.</summary>
    internal static int MergeNearDuplicates(List<Instinct> list)
    {
        int removed = 0;
        for (int a = 0; a < list.Count; a++)
        {
            for (int b = a + 1; b < list.Count; b++)
            {
                var x = list[a];
                var y = list[b];
                if (x.Promoted || y.Promoted) continue;
                if (!string.Equals(x.ProjectKey, y.ProjectKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (!LooksSame(x.Pattern, y.Pattern)) continue;
                bool yWins = y.OccurrenceCount > x.OccurrenceCount
                             || (y.OccurrenceCount == x.OccurrenceCount && y.FirstSeen < x.FirstSeen);
                var (winner, loser) = yWins ? (y, x) : (x, y);
                MergeInto(winner, loser);
                list.Remove(loser);
                removed++;
                if (ReferenceEquals(loser, x)) { a--; break; }   // x is gone: look again at what took its slot
                b--;
            }
        }
        return removed;
    }

    /// <summary>
    /// Store v1 → v2: copy the file aside first (instincts.json.v1-backup, never overwritten), mark every instinct
    /// "project unknown" (<see cref="Instinct.LegacyUnscoped"/>), and merge rewordings of one rule — the extractor
    /// reworded every sighting, so "Never use the E: drive; work only on D:" and its twin each sat at one sighting
    /// for ever. Nothing is deleted: a merged instinct keeps the other wording as an alias and both counts.
    /// </summary>
    internal static int Migrate(InstinctStore store, string storePath)
    {
        try
        {
            string backup = storePath + ".v1-backup";
            if (File.Exists(storePath) && !File.Exists(backup)) File.Copy(storePath, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        foreach (var i in store.Instincts)
        {
            i.SeenInSessions ??= new();
            i.Aliases ??= new();
            i.Tags ??= new();
            if (i.ProjectKey == null) i.LegacyUnscoped = true;
        }
        int merged = MergeNearDuplicates(store.Instincts);
        store.Version = InstinctStore.CurrentVersion;
        return merged;
    }

    /// <summary>Read the inbox into the store now (lines with a "pattern"), then empty it. Returns how many were fed.</summary>
    public int DrainInbox()
    {
        lock (_lock)
        {
            var store = Load();
            int fed = DrainInboxLocked(store);
            if (fed > 0) Save(store);
            return fed;
        }
    }

    /// <summary>
    /// Every line with a "pattern" becomes a sighting; a line without one is dropped. The file is then deleted.
    /// The bundled stop-pattern-extract hook wrote only {model, costUsd, tokens} rows here — over a thousand, never
    /// read — so the first run of this build drops them, and the hook is gone from the Hook Library.
    /// </summary>
    private int DrainInboxLocked(InstinctStore store)
    {
        if (!File.Exists(_inboxPath)) return 0;
        string[] lines;
        try { lines = File.ReadAllLines(_inboxPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        int fed = 0;
        var now = DateTime.UtcNow;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                string? Str(string name) => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
                string? pattern = Str("pattern");
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                string? project = ProjectKey(Str("project"));
                ApplySighting(store, new Sighting(pattern, Str("trigger"), Str("action"), SessionId: Str("session"),
                    ProjectKey: project, ProjectName: ProjectNameOf(project), ProjectScoped: project != null), now);
                fed++;
            }
            catch (JsonException) { }
        }
        try { File.Delete(_inboxPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return fed;
    }

    // ─── Promote → Skill ─────────────────────────────────────────────

    /// <summary>
    /// Write a Skill .md file based on this instinct, then mark Promoted=true.
    /// The next SkillService.ReloadSkills() picks it up automatically.
    /// </summary>
    public string? PromoteToSkill(string id)
    {
        var instinct = GetById(id);
        if (instinct == null) return null;
        if (!instinct.CanPromote && !instinct.Promoted) return null;

        string skillsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cluadex", "skills");
        Directory.CreateDirectory(skillsDir);

        string safeName = SanitizeFileName(instinct.Pattern);
        if (string.IsNullOrEmpty(safeName)) safeName = $"instinct-{instinct.Id}";
        // FIX (audit LOW #13): always append the instinct id so two patterns
        // that sanitize to the same filename can coexist instead of one
        // silently overwriting the other.
        string fileName = $"promoted-{safeName}-{instinct.Id}.md";
        string filePath = Path.Combine(skillsDir, fileName);

        var md = new StringBuilder();
        md.AppendLine("---");
        md.AppendLine($"name: {safeName}");
        md.AppendLine($"description: {EscapeYamlValue(instinct.Pattern)}");
        if (instinct.Tags.Count > 0)
            md.AppendLine($"tags: [{string.Join(", ", instinct.Tags.Select(t => $"\"{t}\""))}]");
        md.AppendLine($"source: promoted-instinct ({instinct.Id})");
        md.AppendLine($"confidence: {instinct.Confidence:F2}");
        md.AppendLine($"occurrences: {instinct.OccurrenceCount}");
        md.AppendLine("---");
        md.AppendLine();
        md.AppendLine($"# {instinct.Pattern}");
        md.AppendLine();
        if (!string.IsNullOrWhiteSpace(instinct.Trigger))
        {
            md.AppendLine("## When");
            md.AppendLine(instinct.Trigger);
            md.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(instinct.Action))
        {
            md.AppendLine("## Do");
            md.AppendLine(instinct.Action);
            md.AppendLine();
        }
        md.AppendLine("---");
        md.AppendLine();
        md.AppendLine($"_Promoted automatically from observed instinct on " +
                      $"{DateTime.Now:yyyy-MM-dd HH:mm}. " +
                      $"Observed {instinct.OccurrenceCount}× · " +
                      $"accepted {instinct.AcceptCount}× · " +
                      $"rejected {instinct.RejectCount}×._");

        File.WriteAllText(filePath, md.ToString());

        // Mark as promoted (also under lock, audit CRITICAL #1)
        lock (_lock)
        {
            var store = Load();
            var live = store.Instincts.FirstOrDefault(i => i.Id == id);
            if (live != null)
            {
                live.Promoted = true;
                live.PromotedSkillPath = filePath;
                Save(store);
            }
        }
        return filePath;
    }

    /// <summary>
    /// Persist the BrainNoteId (and any other field) that another service
    /// updated on a live instinct reference. Used by BrainSyncService after
    /// pushing a note to the brain.
    /// FIX (audit CRITICAL #2 / HIGH #9): only writes BrainNoteId when caller
    /// provides a non-null value, so a failed sync doesn't wipe a previously-
    /// good id from disk.
    /// </summary>
    public void Persist(Instinct mutated)
    {
        if (mutated == null || string.IsNullOrEmpty(mutated.Id)) return;
        lock (_lock)
        {
            var store = Load();
            var hit = store.Instincts.FirstOrDefault(i => i.Id == mutated.Id);
            if (hit == null) return;
            if (!string.IsNullOrEmpty(mutated.BrainNoteId))
                hit.BrainNoteId = mutated.BrainNoteId;
            hit.LastSeen = mutated.LastSeen;
            Save(store);
        }
    }

    // ─── Evolve — semantic clustering / dedup (Wave 5 #2) ────────────

    /// <summary>
    /// Merge near-duplicate instincts using embedding similarity. Embeds each instinct's
    /// pattern+trigger+action via the supplied EmbeddingService (OUTSIDE the lock — it's async),
    /// greedily clusters by cosine ≥ threshold, then UNDER the lock merges each cluster into its
    /// highest-confidence member (summing occurrence/accept/reject counts and unioning tags, so no
    /// learning is lost) and deletes the rest. Fully graceful: if embeddings are unavailable nothing
    /// changes. Returns a human-readable summary.
    /// </summary>
    public async Task<string> EvolveAsync(EmbeddingService embed, double threshold = 0.86, CancellationToken ct = default)
    {
        if (embed == null || !embed.Enabled)
            return "Semantic evolve needs an embedding model (enable SemanticSearchEnabled and `ollama pull nomic-embed-text`).";

        var all = GetAll();
        if (all.Count < 2) return "Nothing to evolve (need at least 2 instincts).";

        // Embed every instinct OUTSIDE the lock (async HTTP — can't await under a lock).
        var vecs = new Dictionary<string, float[]>();
        foreach (var i in all)
        {
            ct.ThrowIfCancellationRequested();
            var v = await embed.EmbedAsync($"{i.Pattern}\n{i.Trigger}\n{i.Action}".Trim(), ct);
            if (v != null) vecs[i.Id] = v;
        }
        if (vecs.Count < 2)
            return "Could not compute embeddings — is Ollama running with the embedding model pulled?";

        // Greedy clustering by cosine similarity.
        var ids = vecs.Keys.ToList();
        var used = new HashSet<string>();
        var clusters = new List<List<string>>();
        for (int a = 0; a < ids.Count; a++)
        {
            if (!used.Add(ids[a])) continue;
            var cluster = new List<string> { ids[a] };
            for (int b = a + 1; b < ids.Count; b++)
            {
                if (used.Contains(ids[b])) continue;
                if (EmbeddingService.Cosine(vecs[ids[a]], vecs[ids[b]]) >= threshold)
                {
                    cluster.Add(ids[b]);
                    used.Add(ids[b]);
                }
            }
            if (cluster.Count > 1) clusters.Add(cluster);
        }
        if (clusters.Count == 0)
            return $"No near-duplicate instincts found (≥{threshold:P0} similar). Nothing merged.";

        // Merge under the lock against a freshly-loaded store (an instinct may have changed since the snapshot).
        int mergedClusters = 0, removed = 0;
        lock (_lock)
        {
            var store = Load();
            foreach (var cluster in clusters)
            {
                var members = cluster
                    .Select(id => store.Instincts.FirstOrDefault(x => x.Id == id))
                    .Where(x => x != null)
                    .Cast<Instinct>()
                    .ToList();
                if (members.Count < 2) continue;

                var winner = members
                    .OrderByDescending(m => m.Confidence)
                    .ThenByDescending(m => m.OccurrenceCount)
                    .First();

                foreach (var loser in members.Where(m => m.Id != winner.Id))
                {
                    MergeInto(winner, loser);
                    store.Instincts.Remove(loser);
                    removed++;
                }
                mergedClusters++;
            }
            if (removed > 0) Save(store);
        }

        return $"Evolved: merged {removed} near-duplicate instinct(s) into {mergedClusters} cluster(s).";
    }

    // ─── Export / Import ─────────────────────────────────────────────

    public void ExportTo(string path)
    {
        lock (_lock)
        {
            var store = Load();
            var json = JsonSerializer.Serialize(store, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(path, json);
        }
    }

    /// <summary>Returns count of imported instincts. Skips duplicates by Pattern.</summary>
    public int ImportFrom(string path)
    {
        if (!File.Exists(path)) return 0;
        var imported = JsonSerializer.Deserialize<InstinctStore>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (imported == null) return 0;

        lock (_lock)
        {
            var store = Load();
            int added = 0;
            foreach (var inc in imported.Instincts)
            {
                if (store.Instincts.Any(i =>
                    string.Equals(i.Pattern, inc.Pattern, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (string.IsNullOrEmpty(inc.Id)) inc.Id = Guid.NewGuid().ToString("N")[..8];
                store.Instincts.Add(inc);
                added++;
            }
            if (added > 0) Save(store);
            return added;
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static string SanitizeFileName(string name)
    {
        var clean = new StringBuilder(name.Length);
        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) clean.Append(ch);
            else if (ch == ' ' || ch == '-' || ch == '_') clean.Append('-');
            // skip everything else
        }
        var s = clean.ToString();
        while (s.Contains("--")) s = s.Replace("--", "-");
        s = s.Trim('-');
        if (s.Length > 60) s = s[..60];
        return s;
    }

    private static string EscapeYamlValue(string s)
    {
        if (s.Contains(':') || s.Contains('#') || s.Contains('"'))
            return $"\"{s.Replace("\"", "\\\"")}\"";
        return s;
    }

    /// <summary>
    /// The three fabricated instincts earlier builds seeded into every new store, with the exact
    /// counts they were seeded with. They were never observed — they claimed 8/15/5 sightings on day
    /// one and were injected into every system prompt as "learned" behaviour.
    /// </summary>
    private static readonly (string Pattern, int Occurrences, int Accepts, int Rejects)[] LegacyDemoSeeds =
    {
        ("Run dotnet build after every C# edit before declaring done", 8, 3, 0),
        ("User prefers Thai status reports with English code/file paths", 15, 6, 0),
        ("Always commit + push when user says 'push' (no further confirmation)", 5, 2, 0),
    };

    /// <summary>
    /// Drop those seeds from an existing store — but ONLY the ones the user never touched: same
    /// pattern, counts exactly as seeded, never promoted or synced. One the user accepted, rejected,
    /// promoted, or that was genuinely observed again (count moved) is theirs now and stays.
    /// </summary>
    private static int RemoveUntouchedDemoSeeds(InstinctStore store)
    {
        int removed = store.Instincts.RemoveAll(i =>
            !i.Promoted
            && string.IsNullOrEmpty(i.BrainNoteId)
            && LegacyDemoSeeds.Any(s =>
                string.Equals(s.Pattern, i.Pattern, StringComparison.Ordinal)
                && s.Occurrences == i.OccurrenceCount
                && s.Accepts == i.AcceptCount
                && s.Rejects == i.RejectCount));
        return removed;
    }
}
