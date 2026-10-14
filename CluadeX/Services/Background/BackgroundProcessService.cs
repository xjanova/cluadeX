using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using CluadeX.Models;

namespace CluadeX.Services.Background;

/// <summary>What a background process is.</summary>
public enum BgKind { Shell, Static, Adopted }

/// <summary>A request to start a command in the background.</summary>
public sealed class BgStartRequest
{
    public required string Command { get; init; }
    public required string Cwd { get; init; }
    public string? Name { get; init; }
    /// <summary>The shell dialect the command is written in (run_command's: Git Bash, else cmd).</summary>
    public ShellRunner.Shell Shell { get; init; } = ShellRunner.DefaultShell;
    /// <summary>The port it is expected to listen on, when known.</summary>
    public int? Port { get; init; }
    /// <summary>The port was asked for explicitly (an argument or a flag in the command): a busy port fails the start
    /// at once. An inferred default port (vite's 5173) is only probed when nothing else held it before the start.</summary>
    public bool PortExplicit { get; init; }
    /// <summary>Regex on the output that means "ready".</summary>
    public string? ReadyPattern { get; init; }
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Wait for readiness before returning (false = return right after the start, e.g. the Task Manager page).</summary>
    public bool WaitForReady { get; init; } = true;
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    /// <summary>"agent" or "user".</summary>
    public string StartedBy { get; init; } = "agent";
    /// <summary>Where the server answers according to its configuration (launch.json "url") — reported once it is
    /// ready when it printed no URL of its own. Only an http(s) URL on this computer is honoured.</summary>
    public string? Url { get; init; }
}

public enum BgStartStatus
{
    /// <summary>Running and ready (a URL was printed, the pattern matched, or the port opened).</summary>
    Ready,
    /// <summary>Running; readiness was not waited for, or not seen before the timeout.</summary>
    Running,
    /// <summary>It exited with code 0 before it was ever ready — a one-shot command, not a server.</summary>
    Finished,
    /// <summary>It could not start, or exited with a non-zero code before it was ready.</summary>
    Failed,
    /// <summary>The same command in the same folder was already running — that one is returned.</summary>
    AlreadyRunning,
    /// <summary>Not started: the cap, a busy port, a missing folder, a bad pattern.</summary>
    Refused,
    /// <summary>The wait was cancelled; the process was stopped.</summary>
    Cancelled,
}

/// <summary>A read-only picture of one background process.</summary>
public sealed record BgSnapshot(
    int Id, string Name, BgKind Kind, string Command, string Cwd, string Status, int? Pid, int? Port, string? Url,
    int? ExitCode, DateTime StartedAt, DateTime? ReadyAt, DateTime? EndedAt, string LogPath, string? LastLine,
    string StartedBy, bool InJob)
{
    public bool IsRunning => Status == "running";
    public TimeSpan Uptime => (EndedAt ?? DateTime.Now) - StartedAt;
}

public sealed class BgStartResult
{
    public BgStartStatus Status { get; init; }
    public BgSnapshot? Process { get; init; }
    public string? Url { get; init; }
    public TimeSpan? ReadyAfter { get; init; }
    /// <summary>"url", "pattern" or "port" — what said "ready".</summary>
    public string? Signal { get; init; }
    /// <summary>HTTP status of a HEAD request to the URL once ready (reporting only).</summary>
    public int? HttpStatus { get; init; }
    /// <summary>Localized reason for Refused / Failed starts (empty otherwise).</summary>
    public string Message { get; init; } = "";
    public IReadOnlyList<string> LastLines { get; init; } = Array.Empty<string>();
    /// <summary>Everything still in memory (up to <see cref="BackgroundProcessService.MaxOutputChars"/>) when it ended
    /// before it was ready — a one-shot command run_command handed over must not lose its head.</summary>
    public string? FullOutput { get; init; }
    /// <summary>Lines it printed in all (including CluadeX's own "[exited …]" line).</summary>
    public long TotalLines { get; init; }
    /// <summary>Lines no longer in memory (still in the log file).</summary>
    public long DroppedLines { get; init; }
}

public sealed class BgReadResult
{
    public BgSnapshot? Process { get; init; }
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
    /// <summary>Lines an earlier read by the same reader already returned (since=last).</summary>
    public int AlreadyShown { get; init; }
    /// <summary>Matching lines left out because of the line limit (the oldest ones).</summary>
    public int Omitted { get; init; }
    /// <summary>Lines that no longer fit the in-memory window (still in the log file).</summary>
    public long DroppedFromMemory { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// The registry of background processes: dev servers and watchers the agent started (bg_start, or run_command
/// handing a server over), the built-in static file server (bg_serve), and commands the user started on the
/// Task Manager page.
///
/// <list type="bullet">
/// <item>Every process gets its own <see cref="ProcessJob"/> the moment it starts, so stopping it — or CluadeX
///   exiting, crashing or being killed — ends its whole tree, orphaned grandchildren included.</item>
/// <item>It starts in the shell run_command uses (Git Bash, else cmd; PowerShell for the powershell tool), with
///   stdin closed and NO_COLOR=1, FORCE_COLOR=0, BROWSER=none (vite/CRA --open never launch the user's browser),
///   PYTHONUNBUFFERED=1 (python's "Serving HTTP" line arrives at once).</item>
/// <item>Output is decoded line by line as UTF-8, falling back to the console's OEM code page for a line that is
///   not UTF-8 (cmd's own echo writes CP874 even after chcp 65001) — Thai survives either way. It is stripped of
///   ANSI codes, kept as the newest 256 KB in memory and written in full to
///   DataRoot\bg\&lt;yyyyMMdd-HHmmss&gt;-&lt;id&gt;.log (one name per app run, so a restart never overwrites the last
///   run's logs). Each reader (each chat) has a cursor, so bg_output returns only new lines.</item>
/// <item>At most Settings.BgMaxProcesses (8) run at once.</item>
/// </list>
/// A process is alive as long as anything in its job is — a shell that exits after `nohup x &` leaves x running,
/// and x is still listed, read and stopped.
/// </summary>
public sealed class BackgroundProcessService : IDisposable, IAppShutdownHook
{
    /// <summary>Newest output kept in memory per process (and in the page's output pane).</summary>
    public const int MaxOutputChars = 256 * 1024;
    /// <summary>A log file stops growing here (the newest output is still in memory).</summary>
    public const long MaxLogBytes = 16L * 1024 * 1024;
    /// <summary>Finished entries kept in the list before the oldest are dropped.</summary>
    private const int MaxFinishedKept = 50;
    /// <summary>The newest finished entries keep all their output in memory; older ones are cut to
    /// <see cref="FinishedKeepChars"/> (the full text is in the log file) — 50 × 2 × 256K chars was ~50 MB.</summary>
    private const int FinishedKeptWhole = 4;
    public const int FinishedKeepChars = 16 * 1024;

    private readonly SettingsService? _settings;
    private readonly FileSystemService? _fileSystem;
    private readonly string? _logDir;
    /// <summary>Prefix of this run's log names: ids restart at 1 on every launch, and "1.log" used to overwrite the
    /// previous run's log.</summary>
    private readonly string _runStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
    private readonly object _lock = new();
    private readonly List<Entry> _entries = new();
    private int _nextId;
    private int _shutdown;

    /// <summary>The rows the Task Manager page shows (updated on the UI thread when there is one).</summary>
    public ObservableCollection<TaskInfo> Items { get; } = new();
    private readonly object _itemsLock = new();

    /// <summary>Raised (on any thread) when a process starts, becomes ready or ends.</summary>
    public event Action? Changed;

    public BackgroundProcessService(SettingsService? settings = null, FileSystemService? fileSystem = null, string? logDirectory = null)
    {
        _settings = settings;
        _fileSystem = fileSystem;
        // DataRootg in the app (portable-aware). Constructed bare (a harness, a tool) there are no log files — the
        // newest output is still kept in memory — so nothing is written anywhere unexpected.
        _logDir = logDirectory ?? (settings != null ? Path.Combine(settings.DataRoot, "bg") : null);
        if (_logDir != null)
        {
            try
            {
                Directory.CreateDirectory(_logDir);
                PruneOldFiles(_logDir);
            }
            catch { _logDir = null; /* logs are a convenience; processes still run without them */ }
        }
    }

    /// <summary>Folder holding the per-process logs (null = no log files).</summary>
    public string? LogDirectory => _logDir;

    /// <summary>Most processes running at once (Settings.BgMaxProcesses, 1-32, default 8).</summary>
    public int MaxProcesses => Math.Clamp(_settings?.Settings.BgMaxProcesses ?? 8, 1, 32);

    private static string L(string key) => LocalizedResources.Instance[key];
    private static string L(string key, params object[] args) => string.Format(L(key), args);

    // ═══════════════════════════ queries ═══════════════════════════

    public IReadOnlyList<BgSnapshot> List()
    {
        lock (_lock) return _entries.Select(e => e.Snapshot()).ToList();
    }

    public BgSnapshot? Get(int id)
    {
        lock (_lock) return _entries.FirstOrDefault(e => e.Id == id)?.Snapshot();
    }

    /// <summary>Every process id in a running process's job right now — the shell, its children and any orphans
    /// (just the shell's id when it is not in a job; empty once it ended).</summary>
    public int[] ProcessIdsOf(int id)
    {
        Entry? e;
        lock (_lock) e = _entries.FirstOrDefault(x => x.Id == id);
        if (e == null || e.Ended) return Array.Empty<int>();
        var ids = (e.Job?.ProcessIds() ?? Array.Empty<int>()).ToList();
        if (ids.Count == 0 && e.Pid is { } pid) ids.Add(pid);
        lock (e.Sync) ids.AddRange(e.Escapees.Keys.Where(k => !ids.Contains(k)).ToList());
        return ids.ToArray();
    }

    /// <summary>The URL a running process answers on — for "bg:3" style references (browser_open url=bg:3).</summary>
    public string? GetUrl(int id) => Get(id) is { IsRunning: true } s ? s.Url : null;

    /// <summary>Resolve "bg:3", "bg#3" or "3" to that running process's URL.</summary>
    public bool TryResolveBgUrl(string? reference, out string url, out string? error)
    {
        url = "";
        error = null;
        if (TryParseId(reference, out int id) == false)
        {
            error = L("bg.err.badId", reference ?? "");
            return false;
        }
        var s = Get(id);
        if (s == null) { error = L("bg.err.unknownId", id, DescribeLiveIds()); return false; }
        if (!s.IsRunning) { error = L("bg.err.notRunning", id, s.Status); return false; }
        if (string.IsNullOrEmpty(s.Url)) { error = L("bg.err.noUrl", id); return false; }
        url = s.Url;
        return true;
    }

    /// <summary>"3", "#3", "bg3", "bg#3", "bg:3", "bg_3" → 3.</summary>
    public static bool TryParseId(string? text, out int id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = Regex.Match(text.Trim(), @"^(?:bg|task)?\s*[#:_\-]?\s*(\d{1,6})$", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out id) && id > 0;
    }

    /// <summary>"bg#1, bg#3" — or a localized "none".</summary>
    public string DescribeLiveIds()
    {
        List<int> ids;
        lock (_lock) ids = _entries.Where(e => e.IsRunning).Select(e => e.Id).ToList();
        return ids.Count == 0 ? L("bg.none") : string.Join(", ", ids.Select(i => "bg#" + i));
    }

    public int RunningCount
    {
        get { lock (_lock) return _entries.Count(e => e.IsRunning); }
    }

    /// <summary>Entries in the list (running or finished).</summary>
    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    // ═══════════════════════════ starting ═══════════════════════════

    /// <summary>
    /// Start <paramref name="request"/>'s command in the background and (unless told not to) wait until it is
    /// ready, has exited, or the ready timeout passes — whichever is first. Never throws except for cancellation
    /// being reported as <see cref="BgStartStatus.Cancelled"/> (the process is then stopped).
    /// </summary>
    public async Task<BgStartResult> StartAsync(BgStartRequest request, CancellationToken ct = default)
    {
        var (entry, early) = StartCore(request);
        if (early != null) return early;
        if (!request.WaitForReady) return new BgStartResult { Status = BgStartStatus.Running, Process = entry!.Snapshot(), Url = entry.Url };
        return await WaitReadyAsync(entry!, request.ReadyTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>Start without waiting (the Task Manager page). Returns the id, or -1 with the reason.</summary>
    public (int Id, string? Error) StartFromPage(string name, string command, string cwd)
    {
        var (entry, early) = StartCore(new BgStartRequest
        {
            Command = command, Cwd = cwd, Name = name,
            // Typed on a Windows page by a person — the dialect the page has always used.
            Shell = ShellRunner.Shell.Cmd,
            WaitForReady = false, StartedBy = "user",
        });
        if (entry != null) return (entry.Id, null);
        if (early?.Status == BgStartStatus.AlreadyRunning && early.Process != null) return (early.Process.Id, null);
        return (-1, early?.Message ?? L("bg.err.startFailed", ""));
    }

    private (Entry? Entry, BgStartResult? Early) StartCore(BgStartRequest request)
    {
        if (_shutdown != 0) return (null, Refused(L("bg.err.shuttingDown")));
        string command = (request.Command ?? "").Trim();
        if (command.Length == 0) return (null, Refused(L("bg.err.noCommand")));
        string cwd;
        try { cwd = Path.GetFullPath(request.Cwd); }
        catch { return (null, Refused(L("bg.err.noFolder", request.Cwd))); }
        if (!Directory.Exists(cwd)) return (null, Refused(L("bg.err.noFolder", cwd)));

        Regex? readyRx = null;
        if (!string.IsNullOrWhiteSpace(request.ReadyPattern))
        {
            try { readyRx = new Regex(request.ReadyPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException ex) { return (null, Refused(L("bg.err.badPattern", ex.Message))); }
        }

        // A port somebody else already holds: fail now when it was asked for; otherwise never trust a connect to it.
        int? port = request.Port is { } rp && rp is > 0 and < 65536 ? rp : null;
        int? probePort = port;
        if (port is { } p && ReadinessProbe.IsListening(p))
        {
            if (request.PortExplicit) return (null, Refused(DescribeBusyPort(p)));
            probePort = null;
        }

        Entry entry;
        lock (_lock)
        {
            // Double-tap / a model repeating itself: the same command in the same folder is already up. (A person
            // pressing Start on the page twice gets what they asked for — two runs.)
            var same = request.StartedBy != "agent" ? null : _entries.FirstOrDefault(e => e.IsRunning && e.Kind == BgKind.Shell
                && string.Equals(e.Command, command, StringComparison.Ordinal)
                && string.Equals(e.Cwd, cwd, StringComparison.OrdinalIgnoreCase));
            if (same != null)
                return (null, new BgStartResult
                {
                    Status = BgStartStatus.AlreadyRunning, Process = same.Snapshot(), Url = same.Url,
                    LastLines = same.Tail(10),
                });
            if (_entries.Count(e => e.IsRunning) >= MaxProcesses)
                return (null, Refused(L("bg.err.cap", MaxProcesses, DescribeLiveIdsLocked())));

            entry = new Entry(this, ++_nextId, BgKind.Shell, command, cwd, request.StartedBy)
            {
                Name = string.IsNullOrWhiteSpace(request.Name) ? ShortName(command) : request.Name.Trim(),
                Shell = request.Shell == ShellRunner.Shell.Bash && ShellRunner.FindBash() == null ? ShellRunner.Shell.Cmd : request.Shell,
                ReadyRegex = readyRx,
                Port = port,
                ProbePort = probePort,
                ConfiguredUrl = LoopbackUrl(request.Url),
            };
            // Registered under the same lock as the cap check, so two starts at once can't both take the last slot.
            _entries.Add(entry);
            NoteOwner(entry.Id);
        }
        entry.OpenLog(_logDir, _runStamp);
        AddItem(entry);

        string? error = Launch(entry, request.Env);
        if (error != null)
        {
            entry.AppendSystem(L("bg.log.startFailed", error));
            Finish(entry, -1, startFailed: true);
            return (null, new BgStartResult
            {
                Status = BgStartStatus.Failed, Process = entry.Snapshot(),
                Message = L("bg.err.startFailed", error), LastLines = entry.Tail(20),
            });
        }
        if (Volatile.Read(ref _shutdown) != 0)
        {
            // App exit ran between the check at the top and the launch: it found no process to end, so this one
            // would outlive "shutdown". End it here, with whatever it already started.
            entry.StopRequested = true;
            TerminateTree(entry);
            Finish(entry, -1, startFailed: false);
            return (null, new BgStartResult { Status = BgStartStatus.Refused, Process = entry.Snapshot(), Message = L("bg.err.shuttingDown") });
        }
        RaiseChanged();
        return (entry, null);
    }

    /// <summary>An http(s) URL on this computer (localhost, 127.0.0.1, [::1]) — or null. A launch.json is project
    /// content; its "url" must never send the browser (or a bg:N reference) somewhere else.</summary>
    internal static string? LoopbackUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || !uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        return uri.AbsoluteUri;
    }

    private static BgStartResult Refused(string message) => new() { Status = BgStartStatus.Refused, Message = message };

    /// <summary>"Port 8123 is already in use by PID 4242 (python)."</summary>
    public static string DescribeBusyPort(int port)
    {
        var owner = ReadinessProbe.FindListeningOwner(port);
        return owner is { } o
            ? L("bg.err.portBusyPid", port, o.Pid, o.Name ?? "?")
            : L("bg.err.portBusy", port);
    }

    /// <summary>Start the process for <paramref name="e"/>; null on success, else the reason.</summary>
    private string? Launch(Entry e, IReadOnlyDictionary<string, string>? extraEnv)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = e.Cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        var env = psi.Environment;
        env["GIT_TERMINAL_PROMPT"] = "0";
        env["GIT_PAGER"] = "cat";
        env["PAGER"] = "cat";
        env["NO_COLOR"] = "1";
        env["FORCE_COLOR"] = "0";
        env["BROWSER"] = "none";             // vite / CRA / webpack --open never launch the user's browser
        env["PYTHONUNBUFFERED"] = "1";       // python's print() reaches the log at once, not at exit
        env["PYTHONUTF8"] = "1";
        env["PYTHONIOENCODING"] = "utf-8";
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        env["DOTNET_NOLOGO"] = "1";
        env["MSBUILDDISABLENODEREUSE"] = "1";
        if (extraEnv != null)
            foreach (var (k, v) in extraEnv)
                if (!string.IsNullOrWhiteSpace(k) && k.IndexOf('=') < 0) env[k] = v ?? "";

        ProcessJob? job = null;
        try
        {
            switch (e.Shell)
            {
                case ShellRunner.Shell.Bash:
                {
                    string bash = ShellRunner.FindBash() ?? throw new InvalidOperationException("Git Bash is not installed");
                    string script = Path.Combine(_logDir ?? Path.GetTempPath(), $"cluadex-bg-{e.Id}-{Environment.ProcessId}-{Guid.NewGuid():N}.sh");
                    File.WriteAllText(script,
                        $"cd {LaunchConfig.BashWord(e.Cwd.Replace('\\', '/'))} 2>/dev/null\n" +
                        "exec </dev/null\n" +
                        e.Command.Replace("\r\n", "\n") + "\n",
                        new UTF8Encoding(false));
                    e.ScriptPath = script;
                    psi.FileName = bash;
                    psi.ArgumentList.Add("--noprofile");
                    psi.ArgumentList.Add("--norc");
                    psi.ArgumentList.Add(script);
                    env["LANG"] = "C.UTF-8";
                    env["LC_ALL"] = "C.UTF-8";
                    break;
                }
                case ShellRunner.Shell.PowerShell:
                {
                    string ps = File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe") ? @"C:\Program Files\PowerShell\7\pwsh.exe" : "powershell.exe";
                    string prelude = "$ProgressPreference='SilentlyContinue';" +
                                     "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);" +
                                     "$OutputEncoding=[System.Text.UTF8Encoding]::new($false);\n";
                    psi.FileName = ps;
                    psi.Arguments = "-NoProfile -NonInteractive -NoLogo -OutputFormat Text -EncodedCommand "
                                    + Convert.ToBase64String(Encoding.Unicode.GetBytes(prelude + e.Command));
                    break;
                }
                default:
                    psi.FileName = "cmd.exe";
                    psi.Arguments = $"/d /s /c \"chcp 65001 >nul & {e.Command}\"";
                    break;
            }

            job = ProcessJob.TryCreate(out var jobError);
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start())
            {
                job?.Dispose();
                process.Dispose();
                return "the process did not start";
            }
            // Into the job before anything else — every child it starts from here on is contained.
            if (job != null && !job.Assign(process))
            {
                job.Dispose();
                job = null;
                jobError = "AssignProcessToJobObject failed";
            }
            try { process.StandardInput.Close(); } catch { }

            e.Process = process;
            e.Job = job;
            e.Pid = SafePid(process);
            if (job == null) e.AppendSystem(L("bg.log.noJob", jobError ?? "?"));
            // Raw bytes, decoded per line (UTF-8, else the OEM code page): cmd's builtins ignore chcp 65001.
            e.OutPump = Pump(e, process.StandardOutput.BaseStream, isError: false);
            e.ErrPump = Pump(e, process.StandardError.BaseStream, isError: true);
            e.Monitor = Task.Run(() => MonitorAsync(e));
            UpdateItem(e);
            return null;
        }
        catch (Exception ex)
        {
            if (e.Job == null) job?.Dispose();
            return ex.Message;
        }
    }

    private static int? SafePid(Process p)
    {
        try { return p.Id; } catch { return null; }
    }

    // ═══════════════════════════ the static server ═══════════════════════════

    /// <summary>Serve <paramref name="root"/> with the built-in static server (bg_serve). The same folder already
    /// served is returned as it is (one server per folder).</summary>
    public BgStartResult StartStatic(string root, int? port, bool spa, string startedBy = "agent")
    {
        if (_shutdown != 0) return Refused(L("bg.err.shuttingDown"));
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
        catch { return Refused(L("bg.err.noFolder", root)); }
        if (!Directory.Exists(full)) return Refused(L("bg.err.noFolder", full));

        Entry entry;
        lock (_lock)
        {
            var same = _entries.FirstOrDefault(e => e.IsRunning && e.Kind == BgKind.Static
                && string.Equals(e.Cwd, full, StringComparison.OrdinalIgnoreCase)
                && (port == null || e.Port == port));
            if (same != null)
                return new BgStartResult { Status = BgStartStatus.AlreadyRunning, Process = same.Snapshot(), Url = same.Url, LastLines = same.Tail(10) };
            if (_entries.Count(e => e.IsRunning) >= MaxProcesses)
                return Refused(L("bg.err.cap", MaxProcesses, DescribeLiveIdsLocked()));
            entry = new Entry(this, ++_nextId, BgKind.Static, $"bg_serve {full}{(spa ? " --spa" : "")}", full, startedBy)
            {
                Name = L("bg.static.name", Path.GetFileName(full) is { Length: > 0 } leaf ? leaf : full),
            };
            _entries.Add(entry);   // holds its slot while the listener binds
            NoteOwner(entry.Id);
        }

        var (server, error, detail) = StaticFileServer.TryStart(full, port, spa, line => entry.AppendLine(line, isError: false));
        if (server == null)
        {
            string message = error switch
            {
                StaticFileServer.StartError.PortBusy => DescribeBusyPort(port ?? 0),
                StaticFileServer.StartError.NoFreePort => L("bg.err.noFreePort", StaticFileServer.DefaultPortFrom, StaticFileServer.DefaultPortTo),
                StaticFileServer.StartError.RootMissing => L("bg.err.noFolder", full),
                _ => L("bg.err.startFailed", detail ?? ""),
            };
            lock (_lock) _entries.Remove(entry);
            return Refused(message);
        }

        entry.Server = server;
        entry.Port = server.Port;
        entry.Url = server.Url;
        entry.ReadyAt = DateTime.Now;
        entry.OpenLog(_logDir, _runStamp);
        entry.AppendSystem(L("bg.log.serving", full, server.Url));
        AddItem(entry);
        if (Volatile.Read(ref _shutdown) != 0)
        {
            // App exit ran while the listener was binding — it never saw this server.
            entry.StopRequested = true;
            try { server.Dispose(); } catch { }
            Finish(entry, 0, startFailed: false);
            return Refused(L("bg.err.shuttingDown"));
        }
        RaiseChanged();
        return new BgStartResult
        {
            Status = BgStartStatus.Ready, Process = entry.Snapshot(), Url = server.Url,
            ReadyAfter = TimeSpan.Zero, Signal = "listening", LastLines = entry.Tail(5),
        };
    }

    // ═══════════════════════════ readiness ═══════════════════════════

    private async Task<BgStartResult> WaitReadyAsync(Entry e, TimeSpan timeout, CancellationToken ct)
    {
        ReadinessProbe.Result r;
        try
        {
            r = await ReadinessProbe.WaitAsync(new ReadinessProbe.Target
            {
                HasExited = () => e.Ended,
                PrintedUrl = () => e.Url,
                PatternMatched = e.ReadyRegex != null ? () => e.PatternMatched : null,
                ProbePort = e.ProbePort,
                Timeout = timeout,
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled mid-start (the user pressed Stop): don't leave a half-started server behind.
            await StopAsync(e.Id, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return new BgStartResult { Status = BgStartStatus.Cancelled, Process = e.Snapshot(), LastLines = e.Tail(10) };
        }

        switch (r.Outcome)
        {
            case ReadinessProbe.Outcome.Ready:
            {
                // A URL the server printed always wins (it is in e.Url already); else the configured one (a
                // launch.json "url" with its path); else the address the port answered on.
                if (e.Url == null && (e.ConfiguredUrl ?? r.Url) is { } url) e.Url = url;
                e.ReadyAt ??= DateTime.Now;
                UpdateItem(e);
                RaiseChanged();
                int? http = null;
                if (e.Url != null && e.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    http = await ReadinessProbe.TryHttpHeadAsync(e.Url, 1500, CancellationToken.None).ConfigureAwait(false);
                return new BgStartResult
                {
                    Status = BgStartStatus.Ready, Process = e.Snapshot(), Url = e.Url, ReadyAfter = r.Elapsed,
                    Signal = r.Signal, HttpStatus = http, LastLines = e.Tail(20),
                };
            }
            case ReadinessProbe.Outcome.Exited:
            {
                var s = e.Snapshot();
                var (all, total, dropped) = e.AllOutput();
                return new BgStartResult
                {
                    Status = s.ExitCode == 0 ? BgStartStatus.Finished : BgStartStatus.Failed,
                    Process = s, LastLines = e.Tail(20),
                    Message = s.ExitCode == 0 ? "" : L("bg.err.exitedEarly", s.ExitCode?.ToString() ?? "?"),
                    FullOutput = all, TotalLines = total, DroppedLines = dropped,
                };
            }
            default:
                return new BgStartResult { Status = BgStartStatus.Running, Process = e.Snapshot(), Url = e.Url, LastLines = e.Tail(20) };
        }
    }

    // ═══════════════════════════ output ═══════════════════════════

    private async Task Pump(Entry e, Stream stream, bool isError)
    {
        var buf = new byte[8192];
        var partial = new MemoryStream();   // the bytes of the line being read
        int held = 0;                       // trailing bytes of an unfinished UTF-8 character kept back on a flush
        try
        {
            Task<int> read = stream.ReadAsync(buf, 0, buf.Length);
            while (true)
            {
                // A prompt or progress text with no newline yet is shown after a short pause, not held back.
                if (partial.Length > held && await Task.WhenAny(read, Task.Delay(300)).ConfigureAwait(false) != read)
                    held = FlushPartial(e, partial, isError, final: false);
                int n = await read.ConfigureAwait(false);
                if (n <= 0) break;
                held = 0;
                int start = 0;
                for (int i = 0; i < n; i++)
                {
                    if (buf[i] != (byte)'\n') continue;
                    partial.Write(buf, start, i - start);
                    e.AppendLine(OutputDecoder.Decode(partial.GetBuffer(), 0, (int)partial.Length), isError);
                    partial.SetLength(0);
                    start = i + 1;
                }
                if (start < n) partial.Write(buf, start, n - start);
                if (partial.Length > 16 * 1024) held = FlushPartial(e, partial, isError, final: false);
                read = stream.ReadAsync(buf, 0, buf.Length);
            }
        }
        catch { /* the pipe closed: the process ended or was killed */ }
        if (partial.Length > 0) FlushPartial(e, partial, isError, final: true);
    }

    /// <summary>Emit the unfinished line in <paramref name="partial"/>. Unless <paramref name="final"/>, the bytes of a
    /// UTF-8 character cut off by the read are kept back for the next read (returns how many).</summary>
    private static int FlushPartial(Entry e, MemoryStream partial, bool isError, bool final)
    {
        byte[] bytes = partial.GetBuffer();
        int length = (int)partial.Length;
        int keep = final ? 0 : OutputDecoder.IncompleteUtf8Tail(bytes, length);
        if (length - keep > 0) e.AppendLine(OutputDecoder.Decode(bytes, 0, length - keep), isError);
        var tail = bytes.AsSpan(length - keep, keep).ToArray();
        partial.SetLength(0);
        partial.Write(tail, 0, tail.Length);
        return keep;
    }

    private async Task MonitorAsync(Entry e)
    {
        int exitCode = -1;
        try
        {
            var p = e.Process!;
            var exited = p.WaitForExitAsync();
            // While it runs, keep its whole tree contained: a descendant created outside the job (a Store
            // app-alias python) is pulled back in. Often at first — servers spawn their workers early.
            int lap = 0;
            while (!exited.IsCompleted)
            {
                SweepTree(e);
                await Task.WhenAny(exited, Task.Delay(lap++ < 20 ? 500 : 2000)).ConfigureAwait(false);
            }
            try { exitCode = p.ExitCode; } catch { }
            // The shell is gone, but what it started may live on in the job (nohup x &, an npm script's server).
            if (e.Job != null)
            {
                while (!e.StopRequested && (e.Job.ActiveProcesses > 0 || AnyEscapeeAlive(e)))
                {
                    SweepTree(e);
                    await Task.Delay(lap++ < 20 ? 500 : 2000).ConfigureAwait(false);
                }
            }
        }
        catch { }
        // Let the pipes drain — the last lines are usually the important ones — but never wait on a stray holder.
        try
        {
            var pumps = new[] { e.OutPump ?? Task.CompletedTask, e.ErrPump ?? Task.CompletedTask };
            await Task.WhenAny(Task.WhenAll(pumps), Task.Delay(3000)).ConfigureAwait(false);
        }
        catch { }
        Finish(e, exitCode, startFailed: false);
    }

    /// <summary>
    /// Pull every descendant of the process that is not in its job back into it (Store app-execution aliases start
    /// the real process outside the caller's job). One that refuses is remembered and killed explicitly on stop.
    /// </summary>
    private static void SweepTree(Entry e)
    {
        var job = e.Job;
        if (job == null || e.Ended) return;
        try
        {
            var roots = job.ProcessIds().ToList();
            // The shell's id cannot be reused while its Process handle is held, and its start time is read from
            // that handle even after it exited (its children may outlive it).
            DateTime? shellStart = null;
            if (e.Pid is { } shell)
            {
                roots.Add(shell);
                try { shellStart = e.Process?.StartTime; } catch { }
            }
            foreach (int pid in ProcessTree.Descendants(roots, e.StartedAt, ProcessTree.ParentMap(),
                         p => p == e.Pid && shellStart != null ? shellStart : ProcessTree.StartTime(p)))
            {
                if (job.Contains(pid) || job.Assign(pid)) continue;
                if (ProcessTree.StartTime(pid) is { } started)
                    lock (e.Sync) e.Escapees.TryAdd(pid, started);
            }
        }
        catch { }
    }

    private static bool AnyEscapeeAlive(Entry e)
    {
        List<KeyValuePair<int, DateTime>> list;
        lock (e.Sync) list = e.Escapees.ToList();
        return list.Any(kv => ProcessTree.StartTime(kv.Key) == kv.Value);
    }

    /// <summary>Kill the descendants that refused to join the job — only the very processes recorded (same id AND
    /// start time), never whatever reused their id.</summary>
    private static void KillEscapees(Entry e)
    {
        List<KeyValuePair<int, DateTime>> list;
        lock (e.Sync) list = e.Escapees.ToList();
        foreach (var (pid, started) in list)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.StartTime == started) p.Kill(entireProcessTree: true);
            }
            catch { }
        }
    }

    private void Finish(Entry e, int exitCode, bool startFailed)
    {
        bool first;
        lock (e.Sync)
        {
            first = !e.Ended;
            if (first)
            {
                e.ExitCode = exitCode;
                e.Status = e.StopRequested ? "stopped" : startFailed || exitCode != 0 ? "failed" : "completed";
                e.EndedAt = DateTime.Now;
                e.Ended = true;
            }
        }
        if (first)
        {
            e.AppendSystem(e.Status == "stopped" ? L("bg.log.stopped") : L("bg.log.exited", exitCode));
            e.CloseLog();
        }
        // Always (idempotent): an entry app exit marked ended before its process was launched still holds a job.
        try { e.Job?.Dispose(); } catch { }        // anything still in the job dies with the handle
        try { e.Process?.Dispose(); } catch { }
        try { e.Server?.Dispose(); } catch { }
        if (e.ScriptPath != null) { try { File.Delete(e.ScriptPath); } catch { } }
        if (!first) return;
        UpdateItem(e);
        TrimFinished();
        RaiseChanged();
    }

    /// <summary>Read a process's output. <paramref name="fromStart"/>: the newest <paramref name="maxLines"/> of
    /// everything kept; otherwise only lines this <paramref name="reader"/> has not seen yet.</summary>
    public BgReadResult Read(int id, int maxLines = 80, bool fromStart = false, string? filter = null, bool errorsOnly = false, string reader = "agent")
    {
        Entry? e;
        lock (_lock) e = _entries.FirstOrDefault(x => x.Id == id);
        if (e == null) return new BgReadResult { Error = L("bg.err.unknownId", id, DescribeLiveIds()) };

        Regex? filterRx = null;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            try { filterRx = new Regex(filter, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException ex) { return new BgReadResult { Process = e.Snapshot(), Error = L("bg.err.badPattern", ex.Message) }; }
        }

        LogLine[] all;
        long cursor, dropped;
        lock (e.Sync)
        {
            all = e.Lines.ToArray();
            cursor = e.Cursors.TryGetValue(reader, out var c) ? c : 0;
            e.Cursors[reader] = e.NextSeq;
            dropped = e.DroppedLines;
        }
        IEnumerable<LogLine> pick = fromStart ? all : all.Where(l => l.Seq >= cursor);
        int alreadyShown = fromStart ? 0 : all.Count(l => l.Seq < cursor);
        if (errorsOnly) pick = pick.Where(l => SafeMatch(ErrorLineRx, l.Text));
        if (filterRx != null) pick = pick.Where(l => SafeMatch(filterRx, l.Text));
        var list = pick.Select(l => l.Text).ToList();
        int max = Math.Clamp(maxLines, 1, 2000);
        int omitted = Math.Max(0, list.Count - max);
        if (omitted > 0) list = list.Skip(omitted).ToList();
        return new BgReadResult
        {
            Process = e.Snapshot(), Lines = list, AlreadyShown = alreadyShown, Omitted = omitted, DroppedFromMemory = dropped,
        };
    }

    private static readonly Regex ErrorLineRx = new(
        @"error|exception|failed|fatal|traceback|ERR!|^[45]\d\d (GET|HEAD) ",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static bool SafeMatch(Regex rx, string text)
    {
        try { return rx.IsMatch(text); } catch (RegexMatchTimeoutException) { return false; }
    }

    // ═══════════════════════════ stopping ═══════════════════════════

    /// <summary>
    /// Stop one process with its whole tree (TerminateJobObject), then wait up to <paramref name="wait"/> for it to
    /// be gone and its port to close. Idempotent: stopping a process that already ended reports how it ended.
    /// Null when the id is unknown.
    /// </summary>
    public async Task<BgSnapshot?> StopAsync(int id, TimeSpan wait)
    {
        Entry? e;
        lock (_lock) e = _entries.FirstOrDefault(x => x.Id == id);
        if (e == null) return null;
        if (e.Ended) return e.Snapshot();

        RequestStop(e);
        var sw = Stopwatch.StartNew();
        while (!e.Ended && sw.Elapsed < wait)
            await Task.Delay(50).ConfigureAwait(false);
        if (!e.Ended && e.Kind != BgKind.Static)
        {
            // The monitor is still draining pipes; the job is already terminated — report it as stopped now.
            Finish(e, e.Process is { } p && SafeHasExited(p) ? SafeExit(p) : 1, startFailed: false);
        }
        // Wait for the port to close too — only a port this process owned (probed free before the start, or
        // printed by it): a port another program already held would never close.
        if (e.Kind != BgKind.Static && e.Port is { } port && (e.ProbePort == port || e.Url != null))
            while (sw.Elapsed < wait && ReadinessProbe.ListenersOn(port).Count > 0)
                await Task.Delay(100).ConfigureAwait(false);
        return e.Snapshot();
    }

    // The chat whose agent run started each process (AgentRunScope.SessionKey). Starts from the Task Manager page
    // or the user have no owner and are never stopped by a chat's cleanup. Written under _lock.
    private readonly Dictionary<int, string> _owners = new();

    private void NoteOwner(int id)
    {
        if (AgentRunScope.Current?.SessionKey is { Length: > 0 } key) _owners[id] = key;
    }

    /// <summary>Stop every running process an agent run of chat <paramref name="sessionKey"/> started (the chat was
    /// closed, cleared or deleted). Returns how many were running.</summary>
    public async Task<int> StopForSessionAsync(string sessionKey, TimeSpan wait)
    {
        if (string.IsNullOrEmpty(sessionKey)) return 0;
        List<int> ids;
        lock (_lock)
            ids = _entries.Where(e => e.IsRunning && _owners.TryGetValue(e.Id, out var k) && k == sessionKey)
                .Select(e => e.Id).ToList();
        await Task.WhenAll(ids.Select(id => StopAsync(id, wait))).ConfigureAwait(false);
        return ids.Count;
    }

    /// <summary>Stop every running process (in parallel). Returns how many were running.</summary>
    public async Task<int> StopAllAsync(TimeSpan wait)
    {
        List<int> ids;
        lock (_lock) ids = _entries.Where(e => e.IsRunning).Select(e => e.Id).ToList();
        await Task.WhenAll(ids.Select(id => StopAsync(id, wait))).ConfigureAwait(false);
        return ids.Count;
    }

    private void RequestStop(Entry e)
    {
        e.StopRequested = true;
        if (e.Kind == BgKind.Static)
        {
            try { e.Server?.Dispose(); } catch { }   // stops listening, closes connections, frees its CTS + semaphore
            Finish(e, 0, startFailed: false);
            return;
        }
        TerminateTree(e);
    }

    /// <summary>End a shell entry's whole tree now: the job (escapees pulled in first), else a tree kill, then any
    /// descendant that refused the job.</summary>
    private static void TerminateTree(Entry e)
    {
        SweepTree(e);   // anything that escaped since the last sweep joins the job before it is ended
        bool terminated = false;
        try { terminated = e.Job?.Terminate(1) == true; } catch { }
        if (!terminated)
        {
            try { e.Process?.Kill(entireProcessTree: true); } catch { }
        }
        KillEscapees(e);
    }

    private static bool SafeHasExited(Process p) { try { return p.HasExited; } catch { return true; } }
    private static int SafeExit(Process p) { try { return p.ExitCode; } catch { return 1; } }

    /// <summary>Remove finished entries from the list (the page's "Clear finished").</summary>
    public void ClearFinished()
    {
        List<Entry> gone;
        lock (_lock)
        {
            gone = _entries.Where(e => e.Ended).ToList();
            foreach (var e in gone) _entries.Remove(e);
        }
        foreach (var e in gone) RemoveItem(e);
        if (gone.Count > 0) RaiseChanged();
    }

    private void TrimFinished()
    {
        List<Entry> drop, shrink;
        lock (_lock)
        {
            var finished = _entries.Where(e => e.Ended).OrderBy(e => e.EndedAt).ToList();
            drop = finished.Take(Math.Max(0, finished.Count - MaxFinishedKept)).ToList();
            foreach (var e in drop) _entries.Remove(e);
            // The newest few keep everything (a result is still being read from the one that just ended); older
            // ones keep their last 16K chars in memory — the full text stays in the log file.
            shrink = finished.Except(drop).Take(Math.Max(0, finished.Count - drop.Count - FinishedKeptWhole))
                             .Where(e => !e.Shrunk).ToList();
        }
        foreach (var e in drop) RemoveItem(e);
        foreach (var e in shrink)
        {
            e.ShrinkTo(FinishedKeepChars);
            var info = e.Info;
            OnUi(() =>
            {
                lock (_pendingLock) _pendingUi.Remove(info);
                if (info.Output.Length > FinishedKeepChars)
                    info.Output = "[…]" + Environment.NewLine + info.Output[^FinishedKeepChars..];
            });
        }
    }

    /// <summary>App exit: end every tree now (bounded — no waiting on pipes). Also what Dispose does.</summary>
    public void OnAppShutdown()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0) return;
        List<Entry> all;
        lock (_lock) all = _entries.ToList();
        foreach (var e in all)
        {
            if (e.Ended) continue;
            e.StopRequested = true;
            try { e.Server?.Dispose(); } catch { }
            TerminateTree(e);
            try { e.Job?.Dispose(); } catch { }
            lock (e.Sync)
            {
                if (!e.Ended)
                {
                    e.Status = "stopped";
                    e.EndedAt = DateTime.Now;
                    e.Ended = true;
                }
            }
            e.CloseLog();
        }
    }

    public void Dispose() => OnAppShutdown();

    // ═══════════════════════════ the page's rows ═══════════════════════════

    private readonly Dictionary<TaskInfo, StringBuilder> _pendingUi = new();
    private readonly object _pendingLock = new();
    private bool _flushScheduled;

    private static void OnUi(Action action, bool synchronous = false)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.CheckAccess()) { action(); return; }
        if (synchronous) dispatcher.Invoke(action);
        else dispatcher.BeginInvoke(action);
    }

    private void AddItem(Entry e)
    {
        UpdateItem(e);
        OnUi(() => { lock (_itemsLock) Items.Add(e.Info); });
    }

    private void RemoveItem(Entry e)
    {
        lock (_pendingLock) _pendingUi.Remove(e.Info);
        OnUi(() => { lock (_itemsLock) Items.Remove(e.Info); });
    }

    private static void UpdateItem(Entry e)
    {
        var s = e.Snapshot();
        var i = e.Info;
        i.Pid = s.Pid;
        i.Port = s.Port;
        i.Url = s.Url;
        i.ReadyAt = s.ReadyAt;
        i.ExitCode = s.EndedAt != null ? s.ExitCode : null;
        i.Status = s.Status;
    }

    /// <summary>Queue text for a row's output pane; pushed to the UI about ten times a second, never per line.</summary>
    internal void AppendUi(TaskInfo task, string text)
    {
        lock (_pendingLock)
        {
            if (!_pendingUi.TryGetValue(task, out var sb)) _pendingUi[task] = sb = new StringBuilder();
            sb.Append(text);
            if (sb.Length > MaxOutputChars) sb.Remove(0, sb.Length - MaxOutputChars);
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        _ = Task.Delay(100).ContinueWith(_ => OnUi(FlushPendingOutput), TaskScheduler.Default);
    }

    /// <summary>Move queued output into the rows, keeping the newest <see cref="MaxOutputChars"/> of each.</summary>
    public void FlushPendingOutput()
    {
        List<(TaskInfo Task, string Text)> batch;
        lock (_pendingLock)
        {
            batch = _pendingUi.Select(kv => (kv.Key, kv.Value.ToString())).ToList();
            _pendingUi.Clear();
            _flushScheduled = false;
        }
        foreach (var (task, text) in batch)
        {
            string combined = task.Output + text;
            if (combined.Length > MaxOutputChars)
                combined = "[…]" + Environment.NewLine + combined[^MaxOutputChars..];
            task.Output = combined;
        }
    }

    internal void OnReady(Entry e)
    {
        UpdateItem(e);
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }

    private string DescribeLiveIdsLocked()
    {
        var ids = _entries.Where(e => e.IsRunning).Select(e => e.Id).ToList();
        return ids.Count == 0 ? L("bg.none") : string.Join(", ", ids.Select(i => "bg#" + i));
    }

    private static string ShortName(string command)
    {
        string one = command.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length <= 48 ? one : one[..47] + "…";
    }

    private static void PruneOldFiles(string dir)
    {
        var cutoff = DateTime.Now.AddDays(-7);
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            try
            {
                string ext = Path.GetExtension(f);
                // Scripts of a run that is over (any older than a day); logs older than a week.
                if ((ext == ".sh" && File.GetLastWriteTime(f) < DateTime.Now.AddDays(-1))
                    || (ext == ".log" && File.GetLastWriteTime(f) < cutoff))
                    File.Delete(f);
            }
            catch { }
        }
    }

    // ═══════════════════════════ one entry ═══════════════════════════

    internal readonly record struct LogLine(long Seq, string Text, bool IsError);

    internal sealed class Entry
    {
        public readonly object Sync = new();
        public int Id { get; }
        public BgKind Kind { get; }
        public string Command { get; }
        public string Cwd { get; }
        public string StartedBy { get; }
        public string Name { get; set; } = "";
        public ShellRunner.Shell Shell { get; set; } = ShellRunner.Shell.Cmd;
        public DateTime StartedAt { get; } = DateTime.Now;
        public TaskInfo Info { get; }

        public Process? Process;
        public ProcessJob? Job;
        public StaticFileServer? Server;
        public Task? OutPump, ErrPump, Monitor;
        public string? ScriptPath;
        public Regex? ReadyRegex;
        public volatile bool PatternMatched;
        public volatile bool StopRequested;
        /// <summary>Set once, when the process (and its whole job) is gone.</summary>
        public volatile bool Ended;
        public int? ProbePort;
        /// <summary>The configured URL (launch.json "url", loopback only) — used once ready when none was printed.</summary>
        public string? ConfiguredUrl;
        /// <summary>Its in-memory output was cut to the last few KB (an older finished entry).</summary>
        public volatile bool Shrunk;

        // Guarded by Sync.
        public string Status = "running";
        public int? Pid;
        public int? Port;
        private string? _url;
        public string? Url { get { lock (Sync) return _url; } set { lock (Sync) _url = value; } }
        public int? ExitCode;
        public DateTime? ReadyAt;
        public DateTime? EndedAt;
        public readonly Queue<LogLine> Lines = new();
        public long NextSeq;
        public string? LastText;
        public long DroppedLines;
        private long _chars;
        public readonly Dictionary<string, long> Cursors = new(StringComparer.Ordinal);
        /// <summary>Descendants that could not be put in the job: pid → start time (guarded by Sync).</summary>
        public readonly Dictionary<int, DateTime> Escapees = new();

        private StreamWriter? _log;
        private long _logBytes;
        private bool _logCapped;
        public string LogPath { get; private set; } = "";

        private readonly BackgroundProcessService _owner;

        public Entry(BackgroundProcessService owner, int id, BgKind kind, string command, string cwd, string startedBy)
        {
            _owner = owner;
            Id = id;
            Kind = kind;
            Command = command;
            Cwd = cwd;
            StartedBy = startedBy;
            Info = new TaskInfo
            {
                Id = id, Command = command, Cwd = cwd, StartedAt = StartedAt, StartedBy = startedBy,
                Kind = kind switch { BgKind.Static => "static", BgKind.Adopted => "adopted", _ => "shell" },
                Status = "running",
            };
        }

        public bool IsRunning => !Ended;

        public BgSnapshot Snapshot()
        {
            lock (Sync)
            {
                return new BgSnapshot(Id, Name, Kind, Command, Cwd, Status, Pid, Port, _url, ExitCode, StartedAt, ReadyAt,
                    EndedAt, LogPath, LastText, StartedBy, Job != null);
            }
        }

        public IReadOnlyList<string> Tail(int n)
        {
            lock (Sync) return Lines.Skip(Math.Max(0, Lines.Count - n)).Select(l => l.Text).ToList();
        }

        /// <summary>Everything still in memory as one text, the number of lines printed in all, and how many of
        /// them are no longer in memory.</summary>
        public (string Text, long Total, long Dropped) AllOutput()
        {
            lock (Sync) return (string.Join("\n", Lines.Select(l => l.Text)), NextSeq, DroppedLines);
        }

        /// <summary>Keep only the newest <paramref name="maxChars"/> of output in memory.</summary>
        public void ShrinkTo(int maxChars)
        {
            lock (Sync)
            {
                while (_chars > maxChars && Lines.Count > 1)
                {
                    _chars -= Lines.Dequeue().Text.Length + 1;
                    DroppedLines++;
                }
                Shrunk = true;
            }
        }

        public void OpenLog(string? dir, string runStamp)
        {
            Info.Name = Name;
            if (dir == null) return;
            // One name per app run: ids restart at 1 on every launch. CreateNew never overwrites — a second CluadeX
            // started in the same second (or a harness running two registries) gets the next name.
            foreach (string file in new[]
                     {
                         Path.Combine(dir, $"{runStamp}-{Id}.log"),
                         Path.Combine(dir, $"{runStamp}-{Id}-{Environment.ProcessId}.log"),
                         Path.Combine(dir, $"{runStamp}-{Id}-{Environment.ProcessId}-{Guid.NewGuid():N}.log"),
                     })
            {
                try
                {
                    var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
                    _log = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
                    LogPath = file;
                    Info.LogPath = file;
                    _log.WriteLine($"# {Name}");
                    _log.WriteLine($"# cwd: {Cwd}");
                    _log.WriteLine("# started: " + StartedAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
                    _log.Flush();
                    return;
                }
                catch { /* in use by another CluadeX — try the next name */ }
            }
        }

        public void CloseLog()
        {
            lock (Sync)
            {
                try { _log?.Flush(); _log?.Dispose(); } catch { }
                _log = null;
            }
        }

        /// <summary>A line CluadeX itself adds ("[stopped]") — shown and logged like output.</summary>
        public void AppendSystem(string text) => AppendLine("[" + text + "]", false);

        public void AppendLine(string raw, bool isError)
        {
            string line = ReadinessProbe.CleanLine(raw);
            bool becameReady = false;
            lock (Sync)
            {
                Lines.Enqueue(new LogLine(NextSeq++, line, isError));
                LastText = line;
                _chars += line.Length + 1;
                while (_chars > MaxOutputChars && Lines.Count > 1)
                {
                    _chars -= Lines.Dequeue().Text.Length + 1;
                    DroppedLines++;
                }
                if (_log != null && !_logCapped)
                {
                    try
                    {
                        _log.WriteLine(line);
                        _logBytes += Encoding.UTF8.GetByteCount(line) + 2;
                        if (_logBytes > MaxLogBytes)
                        {
                            _log.WriteLine("[log size limit reached — the newest output is still kept in memory]");
                            _logCapped = true;
                        }
                        if (NextSeq % 64 == 0 || isError) _log.Flush();
                    }
                    catch { _logCapped = true; }
                }
                // The printed URL is the server's own word on where it is — it beats the port that was asked for.
                if (_url == null && !Ended && ReadinessProbe.FindLocalUrl(line) is { } found)
                {
                    _url = found.Url;
                    Port = found.Port;
                    ReadyAt ??= DateTime.Now;
                    becameReady = true;
                }
                if (ReadyRegex != null && !PatternMatched)
                {
                    try
                    {
                        if (ReadyRegex.IsMatch(line))
                        {
                            PatternMatched = true;
                            ReadyAt ??= DateTime.Now;
                            becameReady = true;
                        }
                    }
                    catch (RegexMatchTimeoutException) { }
                }
            }
            _owner.AppendUi(Info, line + Environment.NewLine);
            if (becameReady) _owner.OnReady(this);
        }
    }
}

/// <summary>
/// Turns one line of a child's output bytes into text. Programs write UTF-8 here (Git Bash with LANG=C.UTF-8,
/// PowerShell with the UTF-8 prelude, node, python with PYTHONUTF8) — but cmd.exe caches the console code page when
/// it starts, so its own builtins (echo, dir, type) keep writing the OEM code page (CP874 on a Thai system) even
/// after "chcp 65001" on the same line. A line that is not valid UTF-8 is therefore read in the OEM code page.
/// </summary>
internal static class OutputDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static Encoding? _oem;

    /// <summary>The console's OEM code page (what cmd's builtins write), or lenient UTF-8 / Latin-1.</summary>
    public static Encoding Oem => _oem ??= ResolveOem();

    public static string Decode(byte[] bytes, int offset, int count)
    {
        if (count <= 0) return "";
        try { return StrictUtf8.GetString(bytes, offset, count); }
        catch (DecoderFallbackException) { return Oem.GetString(bytes, offset, count); }
    }

    /// <summary>How many bytes at the end of <paramref name="bytes"/>[0..length) are the start of a UTF-8 character
    /// whose remaining bytes have not arrived yet (0-3). Only for text that is UTF-8 so far — an OEM line is never
    /// held back.</summary>
    public static int IncompleteUtf8Tail(byte[] bytes, int length)
    {
        for (int back = 1; back <= Math.Min(3, length); back++)
        {
            byte b = bytes[length - back];
            if ((b & 0xC0) == 0x80) continue;   // a continuation byte: keep looking for the lead byte
            int need = (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : (b & 0xF8) == 0xF0 ? 4 : 1;
            if (need <= back) return 0;
            try { StrictUtf8.GetCharCount(bytes, 0, length - back); }
            catch (DecoderFallbackException) { return 0; }
            return back;
        }
        return 0;
    }

    private static Encoding ResolveOem()
    {
        try
        {
            int cp = OperatingSystem.IsWindows() ? (int)GetOEMCP() : 0;
            if (cp == 65001) return new UTF8Encoding(false);   // "Use Unicode UTF-8 for worldwide language support"
            if (cp > 0)
                return System.Text.CodePagesEncodingProvider.Instance.GetEncoding(cp) ?? Encoding.GetEncoding(cp);
        }
        catch { }
        return Encoding.Latin1;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
}
