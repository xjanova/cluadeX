using System.IO;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Routines;

/// <summary>
/// The owner's scheduled tasks (<see cref="Routine"/>): kept in routines.json beside codex.db, checked every 30 seconds
/// while CluadeX is open. A routine that is due goes to <see cref="Runner"/> (the chat page: it opens or continues the
/// chat and sends the prompt), which returns when that turn has ended.
///
/// The rules a due routine meets, in <see cref="Tick"/>:
/// - its last run is still going → this one is skipped ("busy"), the next time is set;
/// - <see cref="MaxRunning"/> routines run already → it waits for the next check (nothing is skipped);
/// - it was missed by more than <see cref="MissedAfter"/> (CluadeX closed, the PC asleep) → it runs once now when
///   CatchUp is on (however many times were missed), or is skipped ("missed") when it is off;
/// - a "once" routine switches itself off after its run.
/// </summary>
public sealed class RoutineService : IDisposable
{
    /// <summary>The app's service (null in harnesses that never made one).</summary>
    public static RoutineService? Current { get; set; }

    public const int MaxRunning = 2;
    public static readonly TimeSpan MissedAfter = TimeSpan.FromMinutes(10);
    /// <summary>Routines a chat may make (schedule_task); the owner's own from the page are not counted.</summary>
    public const int MaxFromAi = 10;

    private readonly string? _path;
    private readonly object _gate = new();
    private List<Routine> _routines = new();
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private Timer? _timer;

    /// <summary>Runs a due routine (the chat page) and returns how it went: (ok, a short note, the chat it used).</summary>
    public Func<Routine, Task<(bool Ok, string Note, string? ChatId)>>? Runner { get; set; }

    /// <summary>Raised (any thread) after any routine was added, changed, removed, or ran.</summary>
    public event Action? Changed;

    /// <summary>The clock (tests set it).</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.Now;

    /// <param name="dataRoot">Folder of codex.db; null = memory only (tests).</param>
    public RoutineService(string? dataRoot)
    {
        _path = string.IsNullOrWhiteSpace(dataRoot) ? null : Path.Combine(dataRoot, "routines.json");
        Load();
    }

    /// <summary>Start checking: first after <paramref name="firstDelay"/> (the chat page must be up), then every 30 s.</summary>
    public void Start(TimeSpan firstDelay)
    {
        _timer ??= new Timer(_ => { try { Tick(); } catch { /* a check never takes the app down */ } }, null, firstDelay, TimeSpan.FromSeconds(30));
    }

    public void Dispose() { _timer?.Dispose(); _timer = null; }

    // ─── the store ───

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private void Load()
    {
        if (_path == null || !File.Exists(_path)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<Routine>>(File.ReadAllText(_path), Json);
            if (list != null) lock (_gate) _routines = list.Where(r => !string.IsNullOrWhiteSpace(r.Id)).ToList();
            // A run that was going when the app closed did not finish here.
            lock (_gate)
                foreach (var r in _routines.Where(r => r.LastStatus == "running"))
                { r.LastStatus = "failed"; r.LastNote = "CluadeX closed during the run"; }
        }
        catch { /* unreadable: start empty; the file is left for the owner */ }
    }

    private void Save()
    {
        if (_path != null)
        {
            try
            {
                string json;
                lock (_gate) json = JsonSerializer.Serialize(_routines, Json);
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
            }
            catch { /* the list stays in memory; the next change saves it again */ }
        }
        Changed?.Invoke();
    }

    /// <summary>Copies of every routine, soonest first (the page and schedule_task list read these).</summary>
    public List<Routine> All()
    {
        lock (_gate)
            return _routines.Select(r => r.Copy()).OrderBy(r => r.Enabled ? 0 : 1).ThenBy(r => r.NextRunAt ?? DateTime.MaxValue).ToList();
    }

    public Routine? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate) return _routines.FirstOrDefault(r => r.Id == id.Trim())?.Copy();
    }

    /// <summary>A routine by id, or by name (ignoring case) when no id matches.</summary>
    public Routine? Find(string? idOrName)
    {
        string k = (idOrName ?? "").Trim();
        if (k.Length == 0) return null;
        lock (_gate)
            return (_routines.FirstOrDefault(r => r.Id == k)
                    ?? _routines.FirstOrDefault(r => string.Equals(r.Name.Trim(), k, StringComparison.OrdinalIgnoreCase)))?.Copy();
    }

    public int CountFromAi() { lock (_gate) return _routines.Count(r => r.CreatedBy == "ai"); }

    /// <summary>Add or replace <paramref name="routine"/> (by id); its next time is worked out again. Returns the
    /// localization key of why it cannot be scheduled, or null.</summary>
    public string? Put(Routine routine)
    {
        if (RoutineSchedule.Problem(routine, routine.CreatedBy == "ai") is { } problem) return problem;
        var r = routine.Copy();
        r.Name = string.IsNullOrWhiteSpace(r.Name) ? FirstLine(r.Prompt) : r.Name.Trim();
        r.Prompt = r.Prompt.Trim();
        r.NextRunAt = r.Enabled ? RoutineSchedule.Next(r, Now()) : null;
        lock (_gate)
        {
            int i = _routines.FindIndex(x => x.Id == r.Id);
            if (i >= 0) _routines[i] = r; else _routines.Add(r);
        }
        Save();
        return null;
    }

    public bool Remove(string id)
    {
        bool gone;
        lock (_gate) gone = _routines.RemoveAll(r => r.Id == id) > 0;
        if (gone) Save();
        return gone;
    }

    public bool SetEnabled(string id, bool on)
    {
        lock (_gate)
        {
            if (_routines.FirstOrDefault(r => r.Id == id) is not { } r) return false;
            r.Enabled = on;
            r.NextRunAt = on ? RoutineSchedule.Next(r, Now()) : null;
        }
        Save();
        return true;
    }

    public bool IsRunning(string id) { lock (_gate) return _running.Contains(id); }

    private static string FirstLine(string text)
    {
        string line = (text ?? "").Trim().Split('\n')[0].Trim();
        return line.Length > 60 ? line[..60] + "…" : line;
    }

    // ─── the clock ───

    /// <summary>One check (the timer's, or a test's): starts every routine that is due. Returns the ids started.</summary>
    public List<string> Tick()
    {
        var now = Now();
        var start = new List<Routine>();
        bool changed = false;
        lock (_gate)
        {
            foreach (var r in _routines.Where(r => r.Enabled).OrderBy(r => r.NextRunAt ?? DateTime.MaxValue))
            {
                if (r.NextRunAt == null)
                {
                    r.NextRunAt = RoutineSchedule.Next(r, now);
                    if (r.NextRunAt == null) r.Enabled = false;   // a "once" whose time has gone
                    changed = true;
                    continue;
                }
                if (r.NextRunAt > now) continue;
                bool missed = now - r.NextRunAt.Value > MissedAfter;
                if (_running.Contains(r.Id))
                {
                    // The run stays "running" on the page; the note says a time went by while it worked.
                    Skip(r, "running", now);
                    r.LastNote = "busy";
                    changed = true;
                    continue;
                }
                if (missed && !r.CatchUp)
                {
                    Skip(r, "missed", now);
                    changed = true;
                    continue;
                }
                // waits for the next check (a run started in this loop is in _running already)
                if (Runner == null || _running.Count >= MaxRunning) continue;
                r.LastRunAt = now;
                r.LastStatus = "running";
                r.LastNote = missed ? "missed-run" : "";
                r.NextRunAt = RoutineSchedule.Next(r, now);
                if (r.Kind == RoutineSchedule.Once) { r.Enabled = false; r.NextRunAt = null; }
                _running.Add(r.Id);
                start.Add(r.Copy());
                changed = true;
            }
        }
        if (changed) Save();
        foreach (var r in start) _ = RunAsync(r);
        return start.Select(r => r.Id).ToList();
    }

    /// <summary>Run <paramref name="id"/> now (the page's ▶ or schedule_task run_now), whatever its time. False when
    /// it is running already, or there is no chat page.</summary>
    public bool RunNow(string id)
    {
        Routine copy;
        lock (_gate)
        {
            if (Runner == null || _running.Contains(id) || _routines.FirstOrDefault(x => x.Id == id) is not { } r) return false;
            r.LastRunAt = Now();
            r.LastStatus = "running";
            r.LastNote = "";
            _running.Add(id);
            copy = r.Copy();
        }
        Save();
        _ = RunAsync(copy);
        return true;
    }

    private static void Skip(Routine r, string why, DateTime now)
    {
        r.LastStatus = why;
        r.LastNote = "";
        r.NextRunAt = RoutineSchedule.Next(r, now);
        if (r.Kind == RoutineSchedule.Once) { r.Enabled = false; r.NextRunAt = null; }
    }

    private async Task RunAsync(Routine r)
    {
        (bool Ok, string Note, string? ChatId) result;
        try { result = Runner == null ? (false, "no chat page", null) : await Runner(r); }
        catch (Exception ex) { result = (false, ex.Message, null); }
        lock (_gate)
        {
            _running.Remove(r.Id);
            if (_routines.FirstOrDefault(x => x.Id == r.Id) is { } live)
            {
                live.LastStatus = result.Ok ? "ran" : "failed";
                live.LastNote = Clip(result.Note, 300);
                if (result.ChatId != null) live.ChatId = result.ChatId;
            }
        }
        Save();
    }

    private static string Clip(string? s, int n)
    {
        s = (s ?? "").Trim();
        return s.Length > n ? s[..n] + "…" : s;
    }
}
