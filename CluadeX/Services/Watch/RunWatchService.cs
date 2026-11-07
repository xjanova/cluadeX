using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CluadeX.Services.Background;

namespace CluadeX.Services.Watch;

/// <summary>What a watch waits for.</summary>
public enum WatchKind { Ci, Process }

/// <summary>Where a watch stands. Everything but <see cref="Watching"/> is final.</summary>
public enum WatchState { Watching, Passed, Failed, Ended, TimedOut, Error, Cancelled }

/// <summary>
/// One follow-up a chat's agent asked for (owner 2026-11-07: "ตัวเฝ้าติดตามงาน … การรอ ci หรือ commit ให้ผ่าน"): CI of a
/// pull request / commit / branch, or a background process, watched off the model's clock. When it ends the chat is
/// told — and, while nobody is running there, a new turn starts with the result (ChatViewModel.Watch).
/// </summary>
public sealed class WatchJob : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(State)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
    }

    public string Id { get; set; } = "";
    public string ChatId { get; set; } = "";
    public WatchKind Kind { get; set; }
    /// <summary>Folder gh runs in (its repository decides the default repo).</summary>
    public string Cwd { get; set; } = "";
    public string? Repo { get; set; }
    public string? Pr { get; set; }
    public string? Branch { get; set; }
    public string? Commit { get; set; }
    public int? ProcessId { get; set; }
    /// <summary>What is watched, for people: "PR #12", "commit 1a2b3c4", "bg#3 npm test".</summary>
    public string Target { get; set; } = "";
    /// <summary>What the agent means to do once it ends (its own words, ≤ 500 chars) — handed back with the result.</summary>
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime Deadline { get; set; }

    private WatchState _state;
    public WatchState State { get => _state; set => Set(ref _state, value); }
    private string _summary = "";
    /// <summary>One line: "3/5 checks done, 2 running", "passed — 5 checks", "exit 1".</summary>
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    /// <summary>The facts for the model (check names, states, links, the process's last lines), ≤ 4000 chars.</summary>
    public string Detail { get; set; } = "";
    private DateTime? _lastCheckedAt;
    public DateTime? LastCheckedAt { get => _lastCheckedAt; set => Set(ref _lastCheckedAt, value); }
    public int Polls { get; set; }

    [JsonIgnore] public bool IsActive => State == WatchState.Watching;
}

/// <summary>One CI check as gh reports it. <see cref="Bucket"/>: pass, fail, pending, skipping or cancel.</summary>
public sealed record CiCheck(string Name, string Bucket, string? Link = null, string? Workflow = null);

/// <summary>One look at CI. <see cref="Fatal"/> = watching cannot work (gh missing / signed out / no such PR): stop now.</summary>
public sealed record CiProbeResult(IReadOnlyList<CiCheck> Checks, string? Error = null, bool Fatal = false)
{
    public bool Ok => Error == null;
}

/// <summary>How CI is read. The default runs gh; the harness swaps in a script.</summary>
public interface ICiProbe
{
    Task<CiProbeResult> ProbeAsync(WatchJob job, CancellationToken ct);
}

/// <summary>
/// The watches, their loops and their file (DataRoot\watch-jobs.json). A CI watch survives an app restart (it is picked up
/// again on start); a process watch cannot (background process ids restart with the app) and is closed as such, without
/// waking anyone. Limits: <see cref="MaxPerChat"/> at once per chat, <see cref="MaxTotal"/> in all, the same target twice
/// in one chat returns the watch already there, and a deadline on each (CI ≤ 6 h, process ≤ 12 h).
/// </summary>
public sealed class RunWatchService
{
    public static RunWatchService? Current { get; set; }

    public const int MaxPerChat = 3, MaxTotal = 12, MaxNoteChars = 500, MaxDetailChars = 4000, KeepFinished = 60;
    public static readonly TimeSpan DefaultCiTimeout = TimeSpan.FromMinutes(60), MaxCiTimeout = TimeSpan.FromHours(6);
    public static readonly TimeSpan DefaultProcessTimeout = TimeSpan.FromMinutes(120), MaxProcessTimeout = TimeSpan.FromHours(12);

    /// <summary>Wait between CI looks, by how many looks were made: soon after the start, then easing off to 90 s — CI
    /// takes minutes and GitHub's API is shared with everything else gh does.</summary>
    public Func<int, TimeSpan> CiInterval { get; set; } = n =>
        n == 0 ? TimeSpan.FromSeconds(15) : n < 4 ? TimeSpan.FromSeconds(30) : n < 20 ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(90);
    public TimeSpan ProcessInterval { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>A push's checks take a moment to appear: "no checks" ends the watch only after this long.</summary>
    public TimeSpan NoChecksGrace { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>gh failing (network, GitHub busy) this many looks in a row ends the watch with the error.</summary>
    public int MaxErrorsInARow { get; set; } = 5;
    /// <summary>The shortest timeout a caller may ask for (the harness lowers it).</summary>
    public TimeSpan MinTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Turns a watch may start in one chat within <see cref="WakeWindow"/> — a fix → push → watch → fail loop
    /// stops after this and waits for the owner.</summary>
    public int MaxWakes { get; set; } = 6;
    public TimeSpan WakeWindow { get; set; } = TimeSpan.FromHours(2);

    private readonly string _file;
    private readonly BackgroundProcessService? _bg;
    private readonly DebugLogService? _log;
    private readonly ICiProbe _probe;
    private readonly object _lock = new();
    private readonly List<WatchJob> _jobs = new();
    private readonly Dictionary<string, CancellationTokenSource> _loops = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTime>> _wakes = new(StringComparer.Ordinal);

    /// <summary>A watch was started (any thread).</summary>
    public event Action<WatchJob>? Started;
    /// <summary>A watch ended by itself — passed, failed, ended, timed out or errored — once (any thread). Not raised for
    /// a cancel: whoever cancelled knows.</summary>
    public event Action<WatchJob>? Finished;
    /// <summary>Anything about a watch changed (a look, an end, a cancel) (any thread).</summary>
    public event Action<WatchJob>? Changed;

    public RunWatchService(string dataRoot, BackgroundProcessService? bg = null, DebugLogService? log = null,
        ICiProbe? probe = null, bool resume = true)
    {
        _file = Path.Combine(dataRoot, "watch-jobs.json");
        _bg = bg;
        _log = log;
        _probe = probe ?? new GhCiProbe();
        Load(resume);
    }

    public IReadOnlyList<WatchJob> Jobs { get { lock (_lock) return _jobs.ToList(); } }
    public WatchJob? Get(string? id) { if (id == null) return null; lock (_lock) return _jobs.FirstOrDefault(j => j.Id == id); }
    public IReadOnlyList<WatchJob> ActiveFor(string chatId) { lock (_lock) return _jobs.Where(j => j.ChatId == chatId && j.IsActive).ToList(); }
    public bool WatchesProcess(int id) { lock (_lock) return _jobs.Any(j => j.IsActive && j.Kind == WatchKind.Process && j.ProcessId == id); }

    // ── start ─────────────────────────────────────────────────────────────

    /// <summary>The result of a start: the watch (new or the one already there), or why none was started.</summary>
    public sealed record StartResult(WatchJob? Job, string? Error, bool Existing = false);

    public StartResult StartCi(string chatId, string cwd, string? repo, string? pr, string? branch, string? commit,
        string? note, TimeSpan? timeout)
    {
        string target = !string.IsNullOrEmpty(pr) ? (pr.All(char.IsDigit) ? "PR #" + pr : "PR " + pr)
            : !string.IsNullOrEmpty(commit) ? "commit " + commit[..Math.Min(7, commit.Length)]
            : "branch " + branch;
        if (!string.IsNullOrEmpty(repo)) target += " (" + repo + ")";
        var job = new WatchJob
        {
            ChatId = chatId, Kind = WatchKind.Ci, Cwd = cwd, Repo = Nz(repo), Pr = Nz(pr), Branch = Nz(branch), Commit = Nz(commit),
            Target = target,
        };
        return Begin(job, note, Clamp(timeout, DefaultCiTimeout, MaxCiTimeout, MinTimeout));
    }

    public StartResult StartProcess(string chatId, int processId, string? note, TimeSpan? timeout)
    {
        if (_bg == null) return new(null, "Background processes are not available in this build.");
        var snap = _bg.Get(processId);
        if (snap == null) return new(null, $"There is no background process #{processId} — bg_list lists them (ids start again at 1 each time CluadeX starts).");
        var job = new WatchJob
        {
            ChatId = chatId, Kind = WatchKind.Process, Cwd = snap.Cwd, ProcessId = processId,
            Target = $"bg#{processId} {Clip(snap.Name, 60)}",
        };
        return Begin(job, note, Clamp(timeout, DefaultProcessTimeout, MaxProcessTimeout, MinTimeout));
    }

    private StartResult Begin(WatchJob job, string? note, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(job.ChatId)) return new(null, "A watch belongs to a chat, and this call came from none.");
        lock (_lock)
        {
            var same = _jobs.FirstOrDefault(j => j.IsActive && j.ChatId == job.ChatId && SameTarget(j, job));
            if (same != null)
            {
                if (!string.IsNullOrWhiteSpace(note)) same.Note = Clip(note.Trim(), MaxNoteChars);
                return new(same, null, Existing: true);
            }
            int mine = _jobs.Count(j => j.IsActive && j.ChatId == job.ChatId);
            if (mine >= MaxPerChat)
                return new(null, $"This chat already watches {mine} things (the most at once is {MaxPerChat}) — watch_cancel one first, or wait.");
            if (_jobs.Count(j => j.IsActive) >= MaxTotal)
                return new(null, $"CluadeX already watches {MaxTotal} things across all chats — try again when one has ended.");
            job.Id = NewId();
            job.Note = string.IsNullOrWhiteSpace(note) ? null : Clip(note.Trim(), MaxNoteChars);
            job.CreatedAt = DateTime.Now;
            job.Deadline = job.CreatedAt + timeout;
            job.State = WatchState.Watching;
            job.Summary = "";
            _jobs.Add(job);
            Trim();
        }
        Save();
        _log?.Info("Watch", $"{job.Id} watching {job.Target} for chat {job.ChatId} until {job.Deadline:HH:mm}");
        Raise(Started, job);
        Loop(job);
        return new(job, null);
    }

    private static bool SameTarget(WatchJob a, WatchJob b) => a.Kind == b.Kind && (a.Kind == WatchKind.Process
        ? a.ProcessId == b.ProcessId
        : Eq(a.Repo, b.Repo) && Eq(a.Pr, b.Pr) && Eq(a.Branch, b.Branch) && Eq(a.Commit, b.Commit));

    private static bool Eq(string? x, string? y) => string.Equals(x ?? "", y ?? "", StringComparison.OrdinalIgnoreCase);

    // ── cancel ────────────────────────────────────────────────────────────

    /// <summary>Cancel one watch. With <paramref name="chatId"/>, only that chat's (an agent cannot cancel another chat's).</summary>
    public bool Cancel(string id, string? chatId = null, string? why = null)
    {
        WatchJob? job;
        lock (_lock)
        {
            job = _jobs.FirstOrDefault(j => j.Id == id && j.IsActive && (chatId == null || j.ChatId == chatId));
            if (job == null) return false;
            job.State = WatchState.Cancelled;
            job.Summary = why ?? "cancelled";
            if (_loops.Remove(id, out var cts)) cts.Cancel();
        }
        Save();
        _log?.Info("Watch", $"{id} cancelled ({job.Summary})");
        Raise(Changed, job);
        return true;
    }

    public int CancelChat(string chatId, string? why = null)
    {
        int n = 0;
        foreach (var j in ActiveFor(chatId)) if (Cancel(j.Id, chatId, why)) n++;
        return n;
    }

    public int CancelAll(string? why = null)
    {
        int n = 0;
        foreach (var j in Jobs.Where(j => j.IsActive).ToList()) if (Cancel(j.Id, null, why)) n++;
        return n;
    }

    // ── wakes ─────────────────────────────────────────────────────────────

    /// <summary>May a watch start a turn in <paramref name="chatId"/> now? Counts it when yes.</summary>
    public bool TryTakeWake(string chatId)
    {
        lock (_lock)
        {
            if (!_wakes.TryGetValue(chatId, out var list)) _wakes[chatId] = list = new();
            var since = DateTime.Now - WakeWindow;
            list.RemoveAll(t => t < since);
            if (list.Count >= MaxWakes) return false;
            list.Add(DateTime.Now);
            return true;
        }
    }

    /// <summary>The owner wrote in the chat: its wake count starts again.</summary>
    public void OwnerSpoke(string chatId) { lock (_lock) _wakes.Remove(chatId); }

    // ── the loops ─────────────────────────────────────────────────────────

    private void Loop(WatchJob job)
    {
        var cts = new CancellationTokenSource();
        lock (_lock) _loops[job.Id] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                if (job.Kind == WatchKind.Process) await WatchProcessAsync(job, cts.Token).ConfigureAwait(false);
                else await WatchCiAsync(job, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log?.Warn("Watch", $"{job.Id} stopped by an error", ex);
                Finish(job, WatchState.Error, "watching stopped: " + Clip(ex.Message, 200), job.Detail);
            }
            finally
            {
                lock (_lock) { if (_loops.TryGetValue(job.Id, out var c) && ReferenceEquals(c, cts)) _loops.Remove(job.Id); }
                cts.Dispose();
            }
        });
    }

    private async Task WatchCiAsync(WatchJob job, CancellationToken ct)
    {
        int errors = 0;
        DateTime firstLook = DateTime.Now;
        while (true)
        {
            var wait = CiInterval(job.Polls);
            var left = job.Deadline - DateTime.Now;
            if (left <= TimeSpan.Zero) { Finish(job, WatchState.TimedOut, TimedOutLine(job), job.Detail); return; }
            await Task.Delay(wait < left ? wait : left, ct).ConfigureAwait(false);
            if (!job.IsActive) return;

            CiProbeResult r;
            try { r = await _probe.ProbeAsync(job, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { r = new CiProbeResult(Array.Empty<CiCheck>(), ex.Message); }
            job.Polls++;
            job.LastCheckedAt = DateTime.Now;
            if (!job.IsActive) return;

            if (!r.Ok)
            {
                if (r.Fatal || ++errors >= MaxErrorsInARow) { Finish(job, WatchState.Error, Clip(r.Error!, 300), r.Error!); return; }
                job.Summary = "gh did not answer — trying again";
                Raise(Changed, job);
                continue;
            }
            errors = 0;
            var (done, line) = EvaluateCi(r.Checks);
            string detail = DescribeChecks(r.Checks);
            if (r.Checks.Count == 0)
            {
                if (DateTime.Now - firstLook >= NoChecksGrace)
                {
                    Finish(job, WatchState.Ended, "no CI checks appeared", "No CI checks were reported for " + job.Target
                        + $" within {NoChecksGrace.TotalMinutes:0} minutes — this repository may run no CI on it, or the push did not reach GitHub.");
                    return;
                }
                job.Summary = "waiting for checks to appear";
                Raise(Changed, job);
                continue;
            }
            if (done is { } state) { Finish(job, state, line, detail); return; }
            job.Summary = line;
            job.Detail = detail;
            Raise(Changed, job);
        }
    }

    private async Task WatchProcessAsync(WatchJob job, CancellationToken ct)
    {
        while (true)
        {
            var snap = _bg?.Get(job.ProcessId ?? -1);
            job.LastCheckedAt = DateTime.Now;
            if (snap == null) { Finish(job, WatchState.Error, "the process is no longer listed", $"Background process #{job.ProcessId} is no longer in CluadeX's list."); return; }
            if (!snap.IsRunning)
            {
                var state = snap.Status == "stopped" ? WatchState.Ended : snap.ExitCode == 0 && snap.Status == "completed" ? WatchState.Passed : WatchState.Failed;
                string line = snap.Status == "stopped" ? "stopped" : $"exit {snap.ExitCode?.ToString() ?? "?"}";
                var tail = _bg!.Read(snap.Id, 40, reader: "watch");
                string detail = $"Background process #{snap.Id} \"{snap.Name}\" ({snap.Command}) ended: {snap.Status}, exit {snap.ExitCode?.ToString() ?? "?"}, "
                    + $"after {Elapsed(snap.Uptime)}. Log: {snap.LogPath}\nLast lines:\n" + string.Join("\n", tail.Lines);
                Finish(job, state, line, detail);
                return;
            }
            if (DateTime.Now >= job.Deadline) { Finish(job, WatchState.TimedOut, TimedOutLine(job), $"bg#{snap.Id} still runs (up {Elapsed(snap.Uptime)})."); return; }
            if (job.Summary.Length == 0) { job.Summary = "running"; Raise(Changed, job); }
            await Task.Delay(ProcessInterval, ct).ConfigureAwait(false);
        }
    }

    private void Finish(WatchJob job, WatchState state, string summary, string detail)
    {
        lock (_lock)
        {
            if (!job.IsActive) return;   // cancelled meanwhile: the cancel wins
            job.State = state;
            job.Summary = summary;
            job.Detail = Clip(detail, MaxDetailChars);
        }
        Save();
        _log?.Info("Watch", $"{job.Id} {state}: {summary}");
        Raise(Changed, job);
        Raise(Finished, job);
    }

    private static string TimedOutLine(WatchJob job) => $"still not finished after {Elapsed(job.Deadline - job.CreatedAt)}";

    // ── CI rules (pure) ───────────────────────────────────────────────────

    /// <summary>Done (passed / failed) or still running, and one line saying so.</summary>
    public static (WatchState? Done, string Line) EvaluateCi(IReadOnlyList<CiCheck> checks)
    {
        if (checks.Count == 0) return (null, "no checks yet");
        int pending = checks.Count(c => c.Bucket == "pending");
        int failed = checks.Count(c => c.Bucket is "fail" or "cancel");
        int passed = checks.Count - pending - failed;
        if (pending > 0) return (null, $"{checks.Count - pending}/{checks.Count} checks done, {pending} running" + (failed > 0 ? $", {failed} failed already" : ""));
        return failed > 0
            ? (WatchState.Failed, $"failed — {failed} of {checks.Count} checks: " + string.Join(", ", checks.Where(c => c.Bucket is "fail" or "cancel").Select(c => c.Name).Take(4)))
            : (WatchState.Passed, $"passed — {passed} check{(passed == 1 ? "" : "s")}");
    }

    public static string DescribeChecks(IReadOnlyList<CiCheck> checks) => string.Join("\n", checks
        .OrderBy(c => c.Bucket switch { "fail" => 0, "cancel" => 1, "pending" => 2, _ => 3 })
        .Select(c => $"{Mark(c.Bucket)} {c.Name}" + (c.Workflow is { Length: > 0 } w && w != c.Name ? $" ({w})" : "") + $" — {c.Bucket}"
                     + (c.Link is { Length: > 0 } l ? " " + l : "")));

    private static string Mark(string bucket) => bucket switch { "pass" => "✓", "fail" => "✗", "cancel" => "⊘", "pending" => "…", _ => "–" };

    /// <summary>What the model is told when the watch ends — facts, then what to do next.</summary>
    public static string Report(WatchJob job)
    {
        string head = $"[CluadeX watch {job.Id}] {job.Target}: {StateWord(job.State)} — {job.Summary}.";
        string next = job.State switch
        {
            WatchState.Failed when job.Kind == WatchKind.Ci => "Read the failing run with gh_run_log (the run number is in the check's link), fix the cause, push, and watch_ci again if you pushed.",
            WatchState.Failed => "Read its output (bg_log) to see why, fix it, and carry on.",
            WatchState.Passed => "Carry on with what was planned for after it.",
            WatchState.TimedOut => "It did not finish in time: look at it once (gh_pr_checks / bg_log) and decide — watch again only if it is still making progress.",
            WatchState.Error => "Watching could not continue: say so to the user if you cannot fix the cause.",
            _ => "Carry on.",
        };
        return head + "\n" + job.Detail + (job.Note is { Length: > 0 } n ? "\nYou planned: " + n : "") + "\n" + next;
    }

    public static string StateWord(WatchState s) => s switch
    {
        WatchState.Passed => "passed", WatchState.Failed => "failed", WatchState.Ended => "ended",
        WatchState.TimedOut => "timed out", WatchState.Error => "could not be watched", WatchState.Cancelled => "cancelled",
        _ => "watching",
    };

    // ── gh output (pure) ──────────────────────────────────────────────────

    /// <summary>`gh pr checks --json name,state,bucket,link,workflow` → checks.</summary>
    public static IReadOnlyList<CiCheck> ParsePrChecksJson(string json)
    {
        var list = new List<CiCheck>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string name = Str(e, "name"), bucket = Str(e, "bucket").ToLowerInvariant();
            if (bucket.Length == 0) bucket = BucketOfState(Str(e, "state"));
            list.Add(new CiCheck(name.Length > 0 ? name : "(unnamed)", NormalBucket(bucket), Nz(Str(e, "link")), Nz(Str(e, "workflow"))));
        }
        return list;
    }

    /// <summary>Plain `gh pr checks` (an older gh without --json): "name\tstatus\telapsed\tlink" per line.</summary>
    public static IReadOnlyList<CiCheck> ParsePrChecksText(string text)
    {
        var list = new List<CiCheck>();
        foreach (var raw in text.Split('\n'))
        {
            var cols = raw.TrimEnd('\r').Split('\t');
            if (cols.Length < 2 || cols[0].Trim().Length == 0) continue;
            string bucket = NormalBucket(cols[1].Trim().ToLowerInvariant());
            string? link = cols.Skip(2).FirstOrDefault(c => c.StartsWith("http", StringComparison.OrdinalIgnoreCase))?.Trim();
            list.Add(new CiCheck(cols[0].Trim(), bucket, link));
        }
        return list;
    }

    /// <summary>`gh run list --json databaseId,name,workflowName,status,conclusion,url,headSha` → checks. With
    /// <paramref name="newestCommitOnly"/> (a branch), only the runs of the newest commit count.</summary>
    public static IReadOnlyList<CiCheck> ParseRunListJson(string json, bool newestCommitOnly)
    {
        var list = new List<CiCheck>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        string? sha = null;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string head = Str(e, "headSha");
            if (newestCommitOnly)
            {
                sha ??= head;
                if (head != sha) continue;
            }
            string status = Str(e, "status").ToLowerInvariant(), conclusion = Str(e, "conclusion").ToLowerInvariant();
            string bucket = status != "completed" ? "pending"
                : conclusion is "success" or "neutral" ? "pass"
                : conclusion is "skipped" ? "skipping"
                : conclusion is "cancelled" ? "cancel"
                : "fail";
            string name = Str(e, "workflowName") is { Length: > 0 } wf ? wf : Str(e, "name");
            list.Add(new CiCheck(name.Length > 0 ? name : "(run)", bucket, Nz(Str(e, "url")), Nz(Str(e, "name"))));
        }
        return list;
    }

    private static string BucketOfState(string state) => state.ToUpperInvariant() switch
    {
        "SUCCESS" or "NEUTRAL" => "pass",
        "SKIPPED" => "skipping",
        "CANCELLED" => "cancel",
        "FAILURE" or "ERROR" or "TIMED_OUT" or "ACTION_REQUIRED" or "STARTUP_FAILURE" or "STALE" => "fail",
        _ => "pending",
    };

    private static string NormalBucket(string b) => b switch
    {
        "pass" or "success" => "pass",
        "fail" or "failure" or "error" => "fail",
        "skipping" or "skipped" or "neutral" => "skipping",
        "cancel" or "cancelled" => "cancel",
        _ => "pending",
    };

    private static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ── file ──────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private void Load(bool resume)
    {
        List<WatchJob>? list = null;
        try { if (File.Exists(_file)) list = JsonSerializer.Deserialize<List<WatchJob>>(File.ReadAllText(_file), Json); }
        catch (Exception ex) { _log?.Warn("Watch", "watch-jobs.json unreadable — starting empty", ex); }
        if (list == null) return;
        var again = new List<WatchJob>();
        lock (_lock)
        {
            foreach (var j in list.Where(j => !string.IsNullOrEmpty(j.Id) && !string.IsNullOrEmpty(j.ChatId)))
            {
                if (j.IsActive)
                {
                    if (j.Kind == WatchKind.Process) { j.State = WatchState.Cancelled; j.Summary = "CluadeX restarted — the process went with it"; }
                    else if (!resume) { j.State = WatchState.Cancelled; j.Summary = "not picked up again"; }
                    else if (j.Deadline <= DateTime.Now) { j.State = WatchState.Cancelled; j.Summary = "its time ran out while CluadeX was closed"; }
                    else again.Add(j);
                }
                _jobs.Add(j);
            }
        }
        foreach (var j in again) { _log?.Info("Watch", $"{j.Id} picked up again: {j.Target}"); Loop(j); }
    }

    private void Save()
    {
        try
        {
            string text;
            lock (_lock) text = JsonSerializer.Serialize(_jobs, Json);
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) { _log?.Warn("Watch", "could not save watch-jobs.json", ex); }
    }

    /// <summary>Only the newest finished watches are kept (their cards read them); active ones always stay.</summary>
    private void Trim()
    {
        var old = _jobs.Where(j => !j.IsActive).OrderByDescending(j => j.CreatedAt).Skip(KeepFinished).ToList();
        foreach (var j in old) _jobs.Remove(j);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private string NewId()
    {
        string id;
        do id = "w" + Guid.NewGuid().ToString("N")[..6]; while (_jobs.Any(j => j.Id == id));
        return id;
    }

    private void Raise(Action<WatchJob>? handler, WatchJob job)
    {
        if (handler == null) return;
        foreach (Action<WatchJob> h in handler.GetInvocationList())
        {
            try { h(job); }
            catch (Exception ex) { _log?.Warn("Watch", "a watch listener failed", ex); }
        }
    }

    private static TimeSpan Clamp(TimeSpan? t, TimeSpan def, TimeSpan max, TimeSpan min) =>
        t is not { } v || v <= TimeSpan.Zero ? def : v > max ? max : v < min ? min : v;

    private static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    internal static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public static string Elapsed(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s" : $"{Math.Max(0, (int)t.TotalSeconds)}s";
}
