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

    public DebugLogService()
    {
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cluadex", "logs");
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
        sb.AppendLine($"# CluadeX debug log dump · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"# Process started · ring buffer = {_ring.Count} entries");
        sb.AppendLine($"# Warnings={WarningCount} · Errors={ErrorCount} · Criticals={CriticalCount}");
        sb.AppendLine();
        foreach (var entry in Snapshot())
        {
            sb.AppendLine(entry.ToFlatString());
        }
        return sb.ToString();
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

            // Best-effort file append (don't crash the app if the disk is full).
            // Inside the lock so the daily rotation + AppendAllText is atomic.
            try
            {
                RotateFileIfNeeded();
                File.AppendAllText(_currentLogPath, entry.ToFlatString() + Environment.NewLine);
            }
            catch { /* in-memory ring is still the source of truth */ }
        }

        // Fire OnEntry OUTSIDE the lock so a slow subscriber can't stall
        // other threads' Write calls. Subscribers MUST be thread-safe and
        // are expected to marshal to the UI dispatcher themselves.
        try { OnEntry?.Invoke(entry); } catch { }
    }

    private void RotateFileIfNeeded()
    {
        var today = DateTime.Now.Date;
        if (today == _currentLogDate && !string.IsNullOrEmpty(_currentLogPath)) return;
        _currentLogDate = today;
        _currentLogPath = Path.Combine(_logDir, $"cluadex-{today:yyyyMMdd}.log");

        // Header on first write of the day
        if (!File.Exists(_currentLogPath))
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly().GetName();
                File.WriteAllText(_currentLogPath,
                    $"# CluadeX log {today:yyyy-MM-dd}{Environment.NewLine}" +
                    $"# Version: {asm.Version?.ToString(3) ?? "?"}{Environment.NewLine}" +
                    $"# OS: {Environment.OSVersion.VersionString}{Environment.NewLine}" +
                    $"# CLR: {Environment.Version}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
        }

        // Prune logs older than 30 days
        try
        {
            foreach (var f in Directory.EnumerateFiles(_logDir, "cluadex-*.log"))
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-30))
                    File.Delete(f);
            }
        }
        catch { }
    }
}
