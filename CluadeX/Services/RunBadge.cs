using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services;

/// <summary>
/// How a chat's run stands, as its sidebar row shows it (owner 2026-11-08: "เปลี่ยนให้ไอค่อนบับเบิ้ลหน้าแชท เป็น ไอค่อน ที่แสดง
/// การทำงานแบบอนิเมชั่น ถ้าอันไหนผ่านหมดแล้วก็สีเทา"). The values are <see cref="Models.ChatSession.RunStatus"/>'s strings, which
/// the row's triggers and the tests compare against, so they stay plain strings.
/// </summary>
public static class RunBadge
{
    /// <summary>Nothing runs and nothing waits to be seen: a calm grey node.</summary>
    public const string Idle = "";
    public const string Working = "working";
    /// <summary>The run asks the owner something (a question card or a permission prompt).</summary>
    public const string Waiting = "waiting";
    /// <summary>Finished while the owner was in another chat: a result not opened yet.</summary>
    public const string Done = "done";
    /// <summary>The turn ended on an error.</summary>
    public const string Failed = "failed";
    /// <summary>The owner stopped or paused it, or CluadeX closed while it ran.</summary>
    public const string Stopped = "stopped";

    /// <summary>A run goes on (the row animates).</summary>
    public static bool IsLive(string? status) => status is Working or Waiting;

    /// <summary>What the row shows once its turn ends. A failure wins over a stop (the stop came after the error was
    /// shown), and a quiet finish in the chat on screen leaves nothing to point at.</summary>
    public static string AtEnd(bool stopped, bool failed, bool onScreen)
        => failed ? Failed : stopped ? Stopped : onScreen ? Idle : Done;

    /// <summary>The chat was opened: an unread result is read now. A failure or a stop stays until the next turn begins
    /// — it says how the last turn ended, which still holds after a look.</summary>
    public static string OnOpen(string? status) => status == Done ? Idle : status ?? Idle;

    /// <summary>How the turn ended, from what it added to its chat (rows after <paramref name="firstRow"/>): failed when
    /// its last reply is an error, stopped when the owner stopped it or its last reply ends on Pause.</summary>
    public static (bool Stopped, bool Failed) Outcome(IReadOnlyList<Models.ChatMessage> rows, int firstRow, bool cancelled)
    {
        Models.ChatMessage? last = null;
        for (int i = rows.Count - 1; i >= Math.Max(0, firstRow); i--)
            if (rows[i].Role == Models.MessageRole.Assistant) { last = rows[i]; break; }
        bool failed = last is { HasError: true };
        bool paused = last != null && (last.Content ?? "").StartsWith(AgentRunScope.PausedMarker, StringComparison.Ordinal);
        bool interrupted = last is { IsInterrupted: true };
        return (cancelled || paused || interrupted, failed);
    }
}

/// <summary>
/// What น้อง CluadeX (the face at the top of the activity bar) is doing: busy while any chat works, looking up when one
/// waits for the owner, pleased for a moment when a turn ends well, sad for a moment when one fails, and asleep once
/// nothing has happened for a while (owner 2026-11-08: "หน้าของน้อง cluadex ผู้หญิงน่ารักขยับยุกยิกได้ตามงานในขณะนั้น …
/// ไม่มีไรทำก็ทำหลับ").
/// </summary>
public static class BuddyMood
{
    public const string Idle = "idle";
    public const string Working = "working";
    public const string Waiting = "waiting";
    public const string Happy = "happy";
    public const string Sad = "sad";
    public const string Sleep = "sleep";

    /// <summary>How long a finished turn keeps her pleased (or a failed one sad).</summary>
    public static readonly TimeSpan AfterglowFor = TimeSpan.FromSeconds(6);
    /// <summary>Nothing ran AND nobody touched the mouse or keyboard for this long: she dozes off. It was one minute of no
    /// AI run, mouse or not — the owner reading a reply for a minute found her asleep (owner 2026-11-10: "เธอหลับบ่อยเกินไป").
    /// Any input, any run, typing in the chat box or clicking her wakes her (ChatViewModel.Badges.cs, CluadexBuddy).</summary>
    public static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(8);

    /// <param name="working">Chats with a run that goes on.</param>
    /// <param name="waiting">Of those, the ones that wait for the owner.</param>
    /// <param name="lastEnd">How the latest turn ended (a <see cref="RunBadge"/> value), null when none has.</param>
    /// <param name="sinceLastEnd">Time since that turn ended.</param>
    /// <param name="sinceActivity">Time since anything ran or ended (or since start-up).</param>
    /// <param name="sinceInput">Time since the last mouse or keyboard input on the PC (<see cref="UserInputClock"/>);
    /// null when not known, and then only <paramref name="sinceActivity"/> counts.</param>
    public static string Decide(int working, int waiting, string? lastEnd, TimeSpan sinceLastEnd, TimeSpan sinceActivity,
                                TimeSpan? sinceInput = null)
    {
        if (waiting > 0) return Waiting;
        if (working > 0) return Working;
        if (lastEnd != null && sinceLastEnd < AfterglowFor)
        {
            if (lastEnd == RunBadge.Failed) return Sad;
            if (lastEnd is RunBadge.Done or RunBadge.Idle) return Happy;
        }
        bool quiet = sinceActivity >= SleepAfter && (sinceInput ?? SleepAfter) >= SleepAfter;
        return quiet ? Sleep : Idle;
    }
}

/// <summary>
/// The chats that have a run going, in <c>live-runs.json</c> beside codex.db, so a run that CluadeX did not see end (a
/// crash, a kill, a closed window) shows as stopped after the restart instead of looking as if it had finished — the
/// badge itself is not stored. Like chat-links.json it stays out of codex.db, which takes no schema changes. Each entry
/// names the process that ran it: a chat another CluadeX still runs is left alone.
/// </summary>
public sealed class LiveRunLedger
{
    private readonly string? _path;
    private readonly object _gate = new();
    private readonly int _pid = Environment.ProcessId;

    /// <param name="dataRoot">Folder of codex.db; null = memory only (tests).</param>
    public LiveRunLedger(string? dataRoot)
    {
        _path = string.IsNullOrWhiteSpace(dataRoot) ? null : Path.Combine(dataRoot, "live-runs.json");
    }

    /// <summary>Test seam: is the process that recorded a run still running it? Default: the pid is alive and is a
    /// CluadeX (a reused pid of another program is not).</summary>
    public Func<int, bool> IsAlive { get; set; } = DefaultIsAlive;

    private static bool DefaultIsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("CluadeX", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>One run on the ledger: the CluadeX process running it; whether that CluadeX was closed by the owner while
    /// it ran (<see cref="Closed"/>, set on the way out — a crash never sets it); and what it was waiting on then
    /// (<see cref="Waiting"/>: <see cref="WaitQuestion"/>, <see cref="WaitPermission"/>, or null while it works).</summary>
    public sealed record Entry(int Pid, bool Closed = false, string? Waiting = null);

    public const string WaitQuestion = "question", WaitPermission = "permission";

    /// <summary>A run the restart found: the chat, and how it was cut off.</summary>
    public sealed record Orphan(string ChatId, bool Closed, string? Waiting);

    // The file is {chatId: pid} — what builds before 3.0.310 write and read. An entry with more to say (closed, waiting)
    // is an object instead; an older build reading one loses only the badge.
    private Dictionary<string, Entry> Read()
    {
        var runs = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (_path == null || !File.Exists(_path)) return runs;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return runs;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int pid)) runs[p.Name] = new Entry(pid);
                else if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("pid", out var pv) && pv.TryGetInt32(out pid))
                    runs[p.Name] = new Entry(pid,
                        p.Value.TryGetProperty("closed", out var c) && c.ValueKind == JsonValueKind.True,
                        p.Value.TryGetProperty("waiting", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null);
            }
        }
        catch { /* unreadable: nothing on it */ }
        return runs;
    }

    private void Write(Dictionary<string, Entry> runs)
    {
        if (_path == null) return;
        try
        {
            if (runs.Count == 0) { if (File.Exists(_path)) File.Delete(_path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var root = new System.Text.Json.Nodes.JsonObject();
            foreach (var (id, e) in runs)
                root[id] = !e.Closed && e.Waiting == null
                    ? System.Text.Json.Nodes.JsonValue.Create(e.Pid)
                    : new System.Text.Json.Nodes.JsonObject { ["pid"] = e.Pid, ["closed"] = e.Closed, ["waiting"] = e.Waiting };
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(), new UTF8Encoding(false));
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        }
        catch { /* a failed write only loses the "stopped" badge after a crash */ }
    }

    // Memory-only copy for a ledger with no file (tests).
    private readonly Dictionary<string, Entry> _memory = new(StringComparer.Ordinal);

    private Dictionary<string, Entry> Load() => _path == null ? new(_memory, StringComparer.Ordinal) : Read();

    private void Store(Dictionary<string, Entry> runs)
    {
        if (_path == null) { _memory.Clear(); foreach (var kv in runs) _memory[kv.Key] = kv.Value; return; }
        Write(runs);
    }

    /// <summary>A run began in <paramref name="chatId"/>.</summary>
    public void Began(string chatId)
    {
        lock (_gate) { var runs = Load(); runs[chatId] = new Entry(_pid); Store(runs); }
    }

    /// <summary>The run in <paramref name="chatId"/> ended (CluadeX saw it end).</summary>
    public void Ended(string chatId)
    {
        lock (_gate) { var runs = Load(); if (runs.Remove(chatId)) Store(runs); }
    }

    /// <summary>What the run in <paramref name="chatId"/> waits on now (null: it works). Written only when it changes, so a
    /// crash while a card is open still says so after the restart.</summary>
    public void SetWaiting(string chatId, string? waiting)
    {
        lock (_gate)
        {
            var runs = Load();
            if (!runs.TryGetValue(chatId, out var e) || e.Pid != _pid || e.Waiting == waiting) return;
            runs[chatId] = e with { Waiting = waiting };
            Store(runs);
        }
    }

    /// <summary>The owner is closing CluadeX with these runs going: each is marked as cut off by the close (not a crash),
    /// with what it waits on (<paramref name="waiting"/>: chat id → kind, missing = working).</summary>
    public void MarkClosing(IReadOnlyDictionary<string, string?> waiting)
    {
        lock (_gate)
        {
            var runs = Load();
            bool changed = false;
            foreach (var (id, e) in runs.ToList())
            {
                if (e.Pid != _pid) continue;
                runs[id] = e with { Closed = true, Waiting = waiting.TryGetValue(id, out var w) ? w : e.Waiting };
                changed = true;
            }
            if (changed) Store(runs);
        }
    }

    /// <summary>At start-up: the chats whose run never ended because the CluadeX running it is gone. They are taken off
    /// the ledger (they are told once); runs of a CluadeX still alive stay.</summary>
    public List<string> TakeOrphans() => TakeOrphanRuns().Select(o => o.ChatId).ToList();

    /// <summary><see cref="TakeOrphans"/> with how each run was cut off (the owner closed CluadeX, or it ended on its own)
    /// and what it was waiting on.</summary>
    public List<Orphan> TakeOrphanRuns()
    {
        lock (_gate)
        {
            var runs = Load();
            var orphans = runs.Where(kv => kv.Value.Pid != _pid && !IsAlive(kv.Value.Pid))
                .Select(kv => new Orphan(kv.Key, kv.Value.Closed, kv.Value.Waiting)).ToList();
            if (orphans.Count == 0) return orphans;
            foreach (var o in orphans) runs.Remove(o.ChatId);
            Store(runs);
            return orphans;
        }
    }
}
