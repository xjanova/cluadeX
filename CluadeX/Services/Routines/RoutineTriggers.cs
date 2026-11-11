using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Services.Tools;

namespace CluadeX.Services.Routines;

/// <summary>Runs gh with an argument list (no shell) in a folder. The real one is <see cref="GhCli"/>; tests fake it.</summary>
public interface IGhRunner
{
    /// <summary>(ok, stdout, what went wrong) — never throws for a missing gh, a timeout or a non-zero exit.</summary>
    Task<(bool Ok, string Text, string Error)> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken ct);
}

/// <summary>The GitHub CLI on PATH. Never started where <see cref="Watch.GhCiProbe.GhBlocked"/> (dry-run harnesses).</summary>
public sealed class GhCli : IGhRunner
{
    public async Task<(bool Ok, string Text, string Error)> RunAsync(IReadOnlyList<string> args, string cwd, CancellationToken ct)
    {
        if (Watch.GhCiProbe.GhBlocked) return (false, "", "gh may not start here (dry-run)");
        var o = await EngineeringToolModule.RunProcessAsync("gh", args, cwd, 30_000, ct).ConfigureAwait(false);
        if (o.NotFound) return (false, "", "The GitHub CLI (gh) is not installed — winget install --id GitHub.cli, then gh auth login.");
        if (o.TimedOut) return (false, "", "gh did not answer within 30 s");
        string low = o.Error.ToLowerInvariant();
        if (low.Contains("gh auth login") || low.Contains("not logged in") || low.Contains("http 401"))
            return (false, "", "gh is not signed in to GitHub — run gh auth login in a terminal.");
        if (o.ExitCode != 0) return (false, "", RoutineTriggers.Clip($"gh failed (exit {o.ExitCode}): {(o.Error.Trim().Length > 0 ? o.Error : o.Text).Trim()}", 300));
        return (true, o.Text, "");
    }
}

/// <summary>
/// What starts an event routine (<see cref="RoutineSchedule.Event"/>) — looked at while CluadeX is open, then handed to
/// <see cref="RoutineService.Fire"/> (which keeps the cooldown and lets events wait for a busy run):
/// - GitHub (ci_failed, pr_comment, new_issue): the project's repository through gh, every <see cref="PollEvery"/>. The
///   first look only notes where things stand (<see cref="Routine.Cursor"/>), so nothing that happened before the
///   routine was made starts it. Comments and issues by the owner's own GitHub account never start it (a run that
///   replies under that account would start the next), nor bots'; and — unless the routine says "anyone" — only those
///   by people with a role in the repository (owner, member, collaborator) do.
/// - file_changed: a watcher on the project folder, outside .git, bin, obj, node_modules…; it fires once the folder has
///   been quiet for <see cref="QuietFor"/>, and never for what a run of the routine itself wrote.
/// What other people wrote reaches the run as quoted data (the chat page's RoutineMessage says so).
/// </summary>
public sealed class RoutineTriggers : IDisposable
{
    public static RoutineTriggers? Current { get; set; }

    /// <summary>Raised (any thread) when a routine's watch error came or went (the page shows it).</summary>
    public event Action? Changed;

    public static readonly TimeSpan PollEvery = TimeSpan.FromMinutes(3);
    /// <summary>After gh failed (not installed, signed out, offline) it is asked again only this much later.</summary>
    public static readonly TimeSpan PollAfterError = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan QuietFor = TimeSpan.FromSeconds(20);
    /// <summary>A file written this soon after the routine's own run ended is the run's, not news.</summary>
    public static readonly TimeSpan OwnWritesFor = TimeSpan.FromSeconds(30);
    public const int MaxBody = 1500;

    private static readonly HashSet<string> FailedConclusions = new(StringComparer.OrdinalIgnoreCase) { "failure", "timed_out", "startup_failure" };
    private static readonly HashSet<string> TrustedRoles = new(StringComparer.OrdinalIgnoreCase) { "OWNER", "MEMBER", "COLLABORATOR" };
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", ".idea", "bin", "obj", "node_modules", "dist", "build", "out", ".next", "packages", "__pycache__", ".venv", "venv", "TestResults" };

    private readonly RoutineService _routines;
    private readonly IGhRunner _gh;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _nextPoll = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _repoOf = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FolderWatch> _watches = new(StringComparer.Ordinal);
    private string? _login;
    private Timer? _timer;
    private int _polling;

    public RoutineTriggers(RoutineService routines, IGhRunner? gh = null)
    {
        _routines = routines;
        _gh = gh ?? new GhCli();
        _routines.Changed += SyncWatches;
    }

    /// <summary>Start looking: first after <paramref name="firstDelay"/>, then every 30 s (each routine at its own pace).</summary>
    public void Start(TimeSpan firstDelay)
    {
        SyncWatches();
        _timer ??= new Timer(_ => _ = PollAsync(), null, firstDelay, TimeSpan.FromSeconds(30));
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _routines.Changed -= SyncWatches;
        lock (_gate)
        {
            foreach (var w in _watches.Values) w.Dispose();
            _watches.Clear();
        }
    }

    /// <summary>Why the last look for <paramref name="id"/> did not work ("" = it did, or not looked yet) — the page shows it.</summary>
    public string ErrorOf(string id) { lock (_gate) return _errors.TryGetValue(id, out var e) ? e : ""; }

    private static bool IsGitHub(Routine r) =>
        r.Kind == RoutineSchedule.Event && r.Trigger is RoutineSchedule.CiFailed or RoutineSchedule.PrComment or RoutineSchedule.NewIssue;

    // ─── GitHub ───

    /// <summary>One round (the timer's, or a test's with <paramref name="force"/>): looks at every GitHub event routine
    /// whose time has come. Returns how many it fired. A round still going makes this one do nothing.</summary>
    public async Task<int> PollAsync(bool force = false, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return 0;
        int fired = 0;
        try
        {
            var now = _routines.Now();
            foreach (var r in _routines.All().Where(r => r.Enabled && IsGitHub(r)))
            {
                lock (_gate)
                    if (!force && _nextPoll.TryGetValue(r.Id, out var at) && at > now) continue;
                string? error = null;
                try { fired += await PollOneAsync(r, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { error = Clip(ex.Message, 300); }
                bool moved;
                lock (_gate)
                {
                    string before = _errors.TryGetValue(r.Id, out var b) ? b : "";
                    if (error is { Length: > 0 }) _errors[r.Id] = error; else _errors.Remove(r.Id);
                    moved = before != (error ?? "");
                    _nextPoll[r.Id] = now + (error is { Length: > 0 } ? PollAfterError : PollEvery);
                }
                if (moved) Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
        finally { Interlocked.Exchange(ref _polling, 0); }
        return fired;
    }

    /// <summary>Look at one routine's repository; throws with what went wrong (shown on the page).</summary>
    private async Task<int> PollOneAsync(Routine r, CancellationToken ct)
    {
        string cwd = r.Project.Trim();
        if (!Directory.Exists(cwd)) throw new InvalidOperationException($"The project folder is gone: {cwd}");
        string repo = await RepoAsync(cwd, ct).ConfigureAwait(false);
        var events = new List<string>();
        string cursor = r.Trigger switch
        {
            RoutineSchedule.CiFailed => await CiFailedAsync(r, cwd, repo, events, ct).ConfigureAwait(false),
            RoutineSchedule.PrComment => await CommentsAsync(r, cwd, repo, events, ct).ConfigureAwait(false),
            _ => await IssuesAsync(r, cwd, repo, events, ct).ConfigureAwait(false),
        };
        _routines.SetCursor(r.Id, cursor);
        if (events.Count == 0) return 0;
        _routines.Fire(r.Id, string.Join("\n\n---\n\n", events));
        return events.Count;
    }

    private async Task<string> GhAsync(string cwd, CancellationToken ct, params string[] args)
    {
        var (ok, text, error) = await _gh.RunAsync(args, cwd, ct).ConfigureAwait(false);
        if (!ok) throw new InvalidOperationException(error);
        return text;
    }

    private async Task<string> RepoAsync(string cwd, CancellationToken ct)
    {
        lock (_gate) if (_repoOf.TryGetValue(cwd, out var known)) return known;
        string repo = (await GhAsync(cwd, ct, "repo", "view", "--json", "nameWithOwner", "-q", ".nameWithOwner").ConfigureAwait(false)).Trim();
        if (!Regex.IsMatch(repo, @"^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$"))
            throw new InvalidOperationException($"{cwd} is not a GitHub repository gh knows (gh repo view said '{Clip(repo, 80)}').");
        lock (_gate) _repoOf[cwd] = repo;
        return repo;
    }

    private async Task<string> LoginAsync(string cwd, CancellationToken ct)
    {
        lock (_gate) if (_login != null) return _login;
        string login = (await GhAsync(cwd, ct, "api", "user", "-q", ".login").ConfigureAwait(false)).Trim();
        lock (_gate) _login = login;
        return login;
    }

    /// <summary>ci_failed. Cursor "baseline|seen,ids": runs up to the baseline id were there before; a failed run after it
    /// fires once (its id joins the seen list).</summary>
    private async Task<string> CiFailedAsync(Routine r, string cwd, string repo, List<string> events, CancellationToken ct)
    {
        var args = new List<string> { "run", "list", "--repo", repo, "--json", "databaseId,workflowName,displayTitle,headBranch,status,conclusion,url,event", "--limit", "30" };
        if (r.Branch.Length > 0) { args.Add("--branch"); args.Add(r.Branch); }
        string json = await GhAsync(cwd, ct, args.ToArray()).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json.Trim().Length == 0 ? "[]" : json);
        var runs = doc.RootElement.EnumerateArray().ToList();
        long newest = runs.Select(x => Long(x, "databaseId")).DefaultIfEmpty(0).Max();
        var (baseline, seen) = ReadCiCursor(r.Cursor);
        if (baseline == null) return newest + "|";   // the first look: where things stand
        foreach (var run in runs.OrderBy(x => Long(x, "databaseId")))
        {
            long id = Long(run, "databaseId");
            if (id <= baseline || seen.Contains(id)) continue;
            if (!string.Equals(Str(run, "status"), "completed", StringComparison.OrdinalIgnoreCase)) continue;
            if (!FailedConclusions.Contains(Str(run, "conclusion"))) continue;
            seen.Add(id);
            events.Add($"CI run failed in {repo}: {Str(run, "workflowName")} — “{Clip(Str(run, "displayTitle"), 200)}” "
                + $"(branch {Str(run, "headBranch")}, {Str(run, "conclusion")}, run id {id}, from {Str(run, "event")})\n{Str(run, "url")}");
        }
        return baseline + "|" + string.Join(",", seen.OrderByDescending(x => x).Take(60));
    }

    public static (long? Baseline, HashSet<long> Seen) ReadCiCursor(string cursor)
    {
        var seen = new HashSet<long>();
        var parts = (cursor ?? "").Split('|');
        if (parts.Length < 1 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long b)) return (null, seen);
        if (parts.Length > 1)
            foreach (var s in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out long id)) seen.Add(id);
        return (b, seen);
    }

    /// <summary>pr_comment: comments on pull requests (the conversation and the code review). Cursor = the newest
    /// created_at seen (UTC, ISO 8601).</summary>
    private async Task<string> CommentsAsync(Routine r, string cwd, string repo, List<string> events, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (!TryUtc(r.Cursor, out var since)) return Iso(now);   // the first look
        string login = await LoginAsync(cwd, ct).ConfigureAwait(false);
        string q = $"?since={Iso(since)}&per_page=50&sort=created&direction=asc";
        var items = new List<JsonElement>();
        var docs = new List<JsonDocument>();
        try
        {
            foreach (var path in new[] { $"repos/{repo}/issues/comments{q}", $"repos/{repo}/pulls/comments{q}" })
            {
                var d = JsonDocument.Parse(Nonempty(await GhAsync(cwd, ct, "api", path).ConfigureAwait(false)));
                docs.Add(d);
                if (d.RootElement.ValueKind == JsonValueKind.Array) items.AddRange(d.RootElement.EnumerateArray());
            }
            var newest = since;
            foreach (var c in items.OrderBy(c => Str(c, "created_at"), StringComparer.Ordinal))
            {
                if (!TryUtc(Str(c, "created_at"), out var at) || at <= since) continue;   // older, or only edited since
                if (at > newest) newest = at;
                string url = Str(c, "html_url");
                if (!url.Contains("/pull/", StringComparison.Ordinal)) continue;          // an issue's comment, not a PR's
                if (!Counts(c, login, r.FromAnyone, out string who)) continue;
                string where = c.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? $" (on {p.GetString()})" : "";
                events.Add($"New comment by {who} on {url}{where}:\n{Quote(Str(c, "body"))}");
            }
            return Iso(newest);
        }
        finally { foreach (var d in docs) d.Dispose(); }
    }

    /// <summary>new_issue: issues (not pull requests) opened after the routine began watching. Cursor = the highest
    /// number seen (issues and pull requests share the numbers).</summary>
    private async Task<string> IssuesAsync(Routine r, string cwd, string repo, List<string> events, CancellationToken ct)
    {
        string json = await GhAsync(cwd, ct, "api", $"repos/{repo}/issues?state=all&sort=created&direction=desc&per_page=30").ConfigureAwait(false);
        using var doc = JsonDocument.Parse(Nonempty(json));
        var list = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement>();
        long top = list.Select(x => Long(x, "number")).DefaultIfEmpty(0).Max();
        if (!long.TryParse(r.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out long last)) return top.ToString(CultureInfo.InvariantCulture);
        string login = list.Count > 0 ? await LoginAsync(cwd, ct).ConfigureAwait(false) : "";
        foreach (var i in list.OrderBy(x => Long(x, "number")))
        {
            long n = Long(i, "number");
            if (n <= last || i.TryGetProperty("pull_request", out _)) continue;
            if (!Counts(i, login, r.FromAnyone, out string who)) continue;
            events.Add($"New issue #{n} in {repo} by {who}: “{Clip(Str(i, "title"), 200)}”\n{Str(i, "html_url")}\n{Quote(Str(i, "body"))}");
        }
        return Math.Max(top, last).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Whether a comment / issue by its author may start the routine; <paramref name="who"/> names them.</summary>
    private static bool Counts(JsonElement item, string ownLogin, bool anyone, out string who)
    {
        string author = item.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? Str(u, "login") : "";
        string type = u.ValueKind == JsonValueKind.Object ? Str(u, "type") : "";
        string role = Str(item, "author_association");
        who = $"@{author} ({(role.Length == 0 ? "no role" : role.ToLowerInvariant())})";
        if (author.Length == 0 || string.Equals(author, ownLogin, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(type, "Bot", StringComparison.OrdinalIgnoreCase) || author.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)) return false;
        return anyone || TrustedRoles.Contains(role);
    }

    // ─── files ───

    /// <summary>Keep one watcher per file_changed routine that is on, in step with the list.</summary>
    private void SyncWatches()
    {
        var want = _routines.All().Where(r => r.Enabled && r.Kind == RoutineSchedule.Event && r.Trigger == RoutineSchedule.FileChanged
                                              && Directory.Exists(r.Project.Trim()))
            .ToDictionary(r => r.Id, r => (Folder: r.Project.Trim(), r.Files));
        var stale = new List<FolderWatch>();
        lock (_gate)
        {
            foreach (var (id, w) in _watches.ToList())
                if (!want.TryGetValue(id, out var spec) || spec.Folder != w.Folder || spec.Files != w.Files)
                { stale.Add(w); _watches.Remove(id); }
            foreach (var (id, spec) in want)
                if (!_watches.ContainsKey(id))
                {
                    try { _watches[id] = new FolderWatch(this, id, spec.Folder, spec.Files); }
                    catch (Exception ex) { _errors[id] = Clip("Cannot watch the folder: " + ex.Message, 300); }
                }
        }
        foreach (var w in stale) w.Dispose();
    }

    /// <summary>Folders the watcher never reports (build output, packages, the repository's own .git).</summary>
    public static bool IgnoredPath(string relative) =>
        relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1).Any(IgnoredDirs.Contains)
        || relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || relative.EndsWith("~", StringComparison.Ordinal);

    /// <summary>Whether <paramref name="relative"/> (to the project) matches <paramref name="patterns"/> ("" = any file):
    /// "*.cs; docs/*" — a pattern without '/' is a file name anywhere, one with '/' a path from the project folder
    /// ('*' within a folder, '**' across folders).</summary>
    public static bool MatchesFiles(string relative, string? patterns)
    {
        string rel = relative.Replace('\\', '/').TrimStart('/');
        var list = (patterns ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (list.Length == 0) return true;
        string name = rel[(rel.LastIndexOf('/') + 1)..];
        foreach (var p in list)
        {
            string pat = p.Replace('\\', '/').TrimStart('/');
            string rx = "^" + Regex.Escape(pat).Replace(@"\*\*", "\u0001").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]").Replace("\u0001", ".*") + "$";
            if (Regex.IsMatch(pat.Contains('/') ? rel : name, rx, RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Changes the watcher collected for <paramref name="id"/>, once the folder has been quiet (also a test's way in).</summary>
    public FireOutcome FilesSettled(string id, IReadOnlyCollection<string> changed)
    {
        if (changed.Count == 0) return FireOutcome.Nothing;
        if (_routines.RanWithin(id, OwnWritesFor)) return FireOutcome.OwnWrites;
        var shown = changed.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        string text = $"{changed.Count} file{(changed.Count == 1 ? "" : "s")} changed in the project:\n"
            + string.Join("\n", shown.Select(f => "- " + f)) + (changed.Count > shown.Count ? $"\n- … and {changed.Count - shown.Count} more" : "");
        return _routines.Fire(id, text) == RoutineService.FireResult.Ignored ? FireOutcome.Nothing : FireOutcome.Fired;
    }

    public enum FireOutcome { Nothing, OwnWrites, Fired }

    /// <summary>One routine's watcher: collects relative paths, and settles them after <see cref="QuietFor"/> of quiet.</summary>
    private sealed class FolderWatch : IDisposable
    {
        public string Folder { get; }
        public string Files { get; }
        private readonly RoutineTriggers _owner;
        private readonly string _id;
        private readonly FileSystemWatcher _fsw;
        private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _quiet;
        private readonly object _lock = new();

        public FolderWatch(RoutineTriggers owner, string id, string folder, string files)
        {
            _owner = owner; _id = id; Folder = folder; Files = files;
            _quiet = new Timer(_ => Settle(), null, Timeout.Infinite, Timeout.Infinite);
            _fsw = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            _fsw.Changed += (_, e) => Saw(e.FullPath);
            _fsw.Created += (_, e) => Saw(e.FullPath);
            _fsw.Deleted += (_, e) => Saw(e.FullPath);
            _fsw.Renamed += (_, e) => Saw(e.FullPath);
            _fsw.EnableRaisingEvents = true;
        }

        private void Saw(string full)
        {
            try
            {
                string rel = Path.GetRelativePath(Folder, full);
                if (rel.StartsWith("..", StringComparison.Ordinal) || IgnoredPath(rel) || !MatchesFiles(rel, Files)) return;
                if (Directory.Exists(full)) return;
                // What the routine's own run writes is not news.
                if (_owner._routines.RanWithin(_id, OwnWritesFor)) return;
                lock (_lock) { _changed.Add(rel.Replace('\\', '/')); }
                _quiet.Change(QuietFor, Timeout.InfiniteTimeSpan);
            }
            catch { /* a watcher never takes the app down */ }
        }

        private void Settle()
        {
            List<string> list;
            lock (_lock) { list = _changed.ToList(); _changed.Clear(); }
            try { _owner.FilesSettled(_id, list); } catch { }
        }

        public void Dispose()
        {
            try { _fsw.EnableRaisingEvents = false; _fsw.Dispose(); } catch { }
            _quiet.Dispose();
        }
    }

    // ─── small helpers ───

    public static string Clip(string? s, int n)
    {
        s = (s ?? "").Trim();
        return s.Length > n ? s[..n] + "…" : s;
    }

    /// <summary>Someone's text as a quote: clipped, every line marked.</summary>
    private static string Quote(string body)
    {
        string t = Clip(body.Replace("\r\n", "\n"), MaxBody);
        if (t.Length == 0) return "> (no text)";
        var sb = new StringBuilder();
        foreach (var line in t.Split('\n')) sb.Append("> ").Append(line).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : 0;

    private static string Nonempty(string json) => json.Trim().Length == 0 ? "[]" : json;

    private static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static bool TryUtc(string? s, out DateTime utc) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
}
