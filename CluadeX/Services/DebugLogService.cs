using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Central debug log for CluadeX. Two-channel design:
///
///   1. In-memory ring buffer (last 2000 entries) — drives the Debug Log
///      page so the user can see what's happening in real time without
///      opening a file.
///   2. On-disk daily rotation at %USERPROFILE%/.cluadex/logs/cluadex-YYYYMMDD.log
///      — so the user can paste the full session log back to the dev
///      when reporting a bug, including everything before the crash.
///
/// Every service that does interesting work calls Info/Warn/Error/etc with
/// a category tag so the UI can filter. Unhandled exceptions are captured
/// via the global handlers in App.xaml.cs and routed here (replaces the
/// old WriteCrashLog).
///
/// Thread-safe — append is locked, UI subscriber dispatches back to the
/// UI thread.
/// </summary>
public class DebugLogService
{
    private const int RingBufferCapacity = 2000;
    private readonly object _lock = new();
    private readonly Queue<LogEntry> _ring = new();

    private readonly string _logDir;
    private string _currentLogPath = "";
    private DateTime _currentLogDate = DateTime.MinValue;
    private LogLevel _minLevel = LogLevel.Debug;

    // Lines on their way to the file. Every entry opened, appended to and closed the log file on the thread that
    // logged — often the UI thread, dozens of times a second during an agent run. Now they gather here and go out
    // together every half second; a warning or worse goes out at once, so a crash still leaves its last lines.
    private readonly System.Text.StringBuilder _unwritten = new();
    private readonly object _fileLock = new();
    private Timer? _flushTimer;

    /// <summary>Total counts since process start, kept for the status-bar badge.</summary>
    public int WarningCount { get; private set; }
    public int ErrorCount { get; private set; }
    public int CriticalCount { get; private set; }

    /// <summary>Raised when a new entry lands. UI subscribes here.</summary>
    public event Action<LogEntry>? OnEntry;

    public LogLevel MinimumLevel
    {
        get => _minLevel;
        set => _minLevel = value;
    }

    public string LogDirectory => _logDir;

    /// <summary>Log files older than this are deleted (at start and at each day's first line); 0 = keep them all.
    /// Settings › the Debug Log page (AppSettings.DebugLogRetentionDays), 14 by default.</summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>All log files together never grow past this: the oldest go first (today's file is never deleted
    /// by the cap). 0 = no cap. AppSettings.DebugLogMaxMegabytes, 200 MB by default.</summary>
    public long MaxTotalBytes { get; set; } = 200L * 1024 * 1024;

    /// <summary>The choices the Debug Log page offers for <see cref="RetentionDays"/>.</summary>
    public static readonly int[] RetentionChoices = { 3, 7, 14, 30, 0 };

    private Timer? _resourceTimer;

    /// <summary>Verbose: every level down to Trace is kept, and every few minutes a line of the process's memory,
    /// threads and handles (a leak shows as a line that only grows). Off: Debug and up, no resource lines.</summary>
    public bool Verbose
    {
        get => _minLevel == LogLevel.Trace;
        set
        {
            _minLevel = value ? LogLevel.Trace : LogLevel.Debug;
            lock (_lock)
            {
                _resourceTimer?.Dispose();
                _resourceTimer = value
                    ? new Timer(_ => Trace("Resources", BugReports.DiagnosticsCollector.ResourceLine()), null, TimeSpan.FromSeconds(5), ResourceInterval)
                    : null;
            }
        }
    }

    public static readonly TimeSpan ResourceInterval = TimeSpan.FromMinutes(3);

    public DebugLogService() : this(Path.Combine(SettingsService.ResolveDataRoot(out _), "logs")) { }

    /// <summary>Logs into <paramref name="logDir"/> — the test harness, so clearing and pruning never touch the real logs.</summary>
    public DebugLogService(string logDir)
    {
        // The data home's logs folder: ~/.cluadex/logs, or <exe>\Data\logs for a portable copy / a test harness.
        _logDir = logDir;
        try { Directory.CreateDirectory(_logDir); } catch { }
        Info("DebugLog", $"DebugLogService started · log dir = {_logDir}");
    }

    // ─── Public API ──────────────────────────────────────────────────

    public void Trace(string category, string message) => Write(LogLevel.Trace, category, message, null);
    public void Debug(string category, string message) => Write(LogLevel.Debug, category, message, null);
    public void Info(string category, string message) => Write(LogLevel.Info, category, message, null);
    public void Warn(string category, string message, Exception? ex = null) => Write(LogLevel.Warning, category, message, ex);
    public void Error(string category, string message, Exception? ex = null) => Write(LogLevel.Error, category, message, ex);
    public void Critical(string category, string message, Exception? ex = null) => Write(LogLevel.Critical, category, message, ex);

    /// <summary>Snapshot of the current ring buffer (for UI binding on demand).</summary>
    public List<LogEntry> Snapshot()
    {
        lock (_lock) return _ring.ToList();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _ring.Clear();
            WarningCount = ErrorCount = CriticalCount = 0;
        }
        OnEntry?.Invoke(new LogEntry
        {
            Level = LogLevel.Info,
            Category = "DebugLog",
            Message = "Ring buffer cleared.",
        });
    }

    /// <summary>Build a clipboard-friendly dump of the entire ring buffer.</summary>
    public string DumpAsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"# CluadeX debug log dump · {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
        sb.AppendLine($"# Process started · ring buffer = {_ring.Count} entries");
        sb.AppendLine($"# Warnings={WarningCount} · Errors={ErrorCount} · Criticals={CriticalCount}");
        sb.AppendLine();
        foreach (var entry in Snapshot())
        {
            sb.AppendLine(entry.ToFlatString());
        }
        return sb.ToString();
    }

    /// <summary>The head of a session in the log: what this PC and this copy are, one "Session" line per section, so
    /// any log a user sends starts with the facts a developer would otherwise have to ask for.</summary>
    public void LogSessionStart(IEnumerable<(string Section, IReadOnlyDictionary<string, object?> Values)> sections)
    {
        foreach (var (section, values) in sections)
        {
            if (values.Count == 0) continue;
            var line = string.Join(" · ", values.Select(kv => $"{kv.Key}={Format(kv.Value)}"));
            Info("Session", $"{section}: {line}");
        }
        Info("Session", $"log: retention={(RetentionDays > 0 ? RetentionDays + " days" : "forever")} · cap={(MaxTotalBytes > 0 ? MaxTotalBytes / 1048576 + " MB" : "none")} · verbose={Verbose}");
    }

    private static string Format(object? v) => v switch
    {
        null => "-",
        bool b => b ? "yes" : "no",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "-",
    };

    /// <summary>The log files on disk and their total size.</summary>
    public (int Files, long Bytes) MeasureFiles()
    {
        int n = 0; long bytes = 0;
        try
        {
            foreach (var f in new DirectoryInfo(_logDir).EnumerateFiles("cluadex-*.log"))
            {
                n++;
                bytes += f.Length;
            }
        }
        catch { }
        return (n, bytes);
    }

    /// <summary>
    /// "ล้างไฟล์ล็อก": deletes every log file (today's too — the next line starts a fresh one with its header), the
    /// lines not yet written, and the in-memory list. <paramref name="alsoIn"/>: other folders of the app's own
    /// logs to empty (crash logs), only files matching <c>*.log</c> directly in them. Returns what went.
    /// The caller asks the user first.
    /// </summary>
    public (int Files, long Bytes) DeleteLogFiles(params string[] alsoIn)
    {
        int n = 0; long bytes = 0;
        lock (_fileLock)
        {
            lock (_lock) _unwritten.Clear();
            foreach (var (dir, pattern) in new[] { (_logDir, "cluadex-*.log") }.Concat(alsoIn.Select(d => (d, "*.log"))))
            {
                try
                {
                    if (!Directory.Exists(dir) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
                    {
                        if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        long len = f.Length;
                        try { f.Delete(); n++; bytes += len; } catch { }
                    }
                }
                catch { }
            }
            _currentLogDate = DateTime.MinValue;   // the next line writes today's header again
        }
        Clear();
        Info("DebugLog", $"log files cleared by the user: {n} file(s), {bytes / 1024} KB");
        return (n, bytes);
    }

    /// <summary>Apply <see cref="RetentionDays"/> and <see cref="MaxTotalBytes"/> now (start, settings change).
    /// Returns how many files went.</summary>
    public int PruneFiles()
    {
        lock (_fileLock) return PruneFilesLocked();
    }

    private int PruneFilesLocked()
    {
        int removed = 0;
        try
        {
            var todayName = $"cluadex-{DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}.log";
            var files = new DirectoryInfo(_logDir).EnumerateFiles("cluadex-*.log")
                .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();
            var cutoff = RetentionDays > 0 ? DateTime.Now.AddDays(-RetentionDays) : DateTime.MinValue;
            long total = 0;
            foreach (var f in files)
            {
                bool today = string.Equals(f.Name, todayName, StringComparison.OrdinalIgnoreCase);
                bool old = RetentionDays > 0 && f.LastWriteTime < cutoff;
                bool overCap = MaxTotalBytes > 0 && total + f.Length > MaxTotalBytes;
                if (!today && (old || overCap))
                {
                    try { f.Delete(); removed++; continue; } catch { }
                }
                total += f.Length;
            }
        }
        catch { }
        return removed;
    }

    // ─── Core write path ────────────────────────────────────────────

    private void Write(LogLevel level, string category, string message, Exception? ex)
    {
        if (level < _minLevel) return;

        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Category = string.IsNullOrEmpty(category) ? "App" : category,
            Message = message ?? "",
            Exception = ex?.ToString(),
        };

        // FIX (audit CRITICAL #3): rotation + file append + counters all
        // happen INSIDE the same lock so two threads can't race past the
        // midnight rollover and silently drop log lines (and worse, kill
        // the EnumerateFiles prune mid-iteration).
        lock (_lock)
        {
            _ring.Enqueue(entry);
            while (_ring.Count > RingBufferCapacity) _ring.Dequeue();
            if (level == LogLevel.Warning)  WarningCount++;
            if (level == LogLevel.Error)    ErrorCount++;
            if (level == LogLevel.Critical) CriticalCount++;

            _unwritten.Append(entry.ToFlatString()).Append(Environment.NewLine);
            _flushTimer ??= new Timer(_ => Flush(), null, 500, 500);
        }
        if (level >= LogLevel.Warning) Flush();

        // Fire OnEntry OUTSIDE the lock so a slow subscriber can't stall
        // other threads' Write calls. Subscribers MUST be thread-safe and
        // are expected to marshal to the UI dispatcher themselves.
        try { OnEntry?.Invoke(entry); } catch { }
    }

    /// <summary>Write the lines gathered so far to today's file. Any thread; also at exit.</summary>
    public void Flush()
    {
        // Its own lock, so a slow disk never holds up the threads that log; the text is taken inside it, so two
        // flushes cannot write their lines out of order.
        lock (_fileLock)
        {
            string text;
            lock (_lock)
            {
                if (_unwritten.Length == 0) return;
                text = _unwritten.ToString();
                _unwritten.Clear();
            }
            // Best-effort file append (don't crash the app if the disk is full): the in-memory ring is the source of truth.
            try
            {
                RotateFileIfNeeded();
                File.AppendAllText(_currentLogPath, text);
            }
            catch { }
        }
    }

    private void RotateFileIfNeeded()
    {
        var today = DateTime.Now.Date;
        if (today == _currentLogDate && !string.IsNullOrEmpty(_currentLogPath)) return;
        _currentLogDate = today;
        // InvariantCulture: on a Thai-locale machine "yyyy" renders the Buddhist year (2569) → log files
        // were named cluadex-25690606.log instead of cluadex-20260606.log (unsortable, unfindable).
        _currentLogPath = Path.Combine(_logDir, $"cluadex-{today.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}.log");

        // Header on first write of the day
        if (!File.Exists(_currentLogPath))
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly().GetName();
                File.WriteAllText(_currentLogPath,
                    $"# CluadeX log {today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}{Environment.NewLine}" +
                    $"# Version: {asm.Version?.ToString(3) ?? "?"}{Environment.NewLine}" +
                    $"# OS: {Environment.OSVersion.VersionString}{Environment.NewLine}" +
                    $"# CLR: {Environment.Version}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
        }

        // Old days and anything over the size cap go (RetentionDays / MaxTotalBytes; called under _fileLock).
        PruneFilesLocked();
    }
}
