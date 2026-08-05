namespace CluadeX.Models;

/// <summary>One entry in the in-app debug log. Both the ring buffer (for UI)
/// and the on-disk log file share this shape.</summary>
public class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public LogLevel Level { get; set; }

    /// <summary>Free-form category bucket (e.g. "Agent", "Tool", "Provider", "MCP", "FileIO", "UI").</summary>
    public string Category { get; set; } = "App";

    public string Message { get; set; } = "";

    /// <summary>Stringified exception (Type + Message + StackTrace). Null for non-error entries.</summary>
    public string? Exception { get; set; }

    // ─── UI helpers ─────────────────────────────────────────────────
    public string TimestampDisplay => Timestamp.ToString("HH:mm:ss.fff");
    public string LevelDisplay => Level.ToString().ToUpperInvariant();

    public string LevelColorHex => Level switch
    {
        LogLevel.Trace    => "#555A8C",
        LogLevel.Debug    => "#8388BD",
        LogLevel.Info     => "#4CDFFF",
        LogLevel.Warning  => "#FFD166",
        LogLevel.Error    => "#FF6E6E",
        LogLevel.Critical => "#FF5EC4",
        _                 => "#C0C6EE",
    };

    public string CategoryColorHex => Category switch
    {
        "Agent"    => "#A672FF",
        "Tool"     => "#4CDFFF",
        "Provider" => "#FF8AA6",
        "MCP"      => "#5CFFB0",
        "FileIO"   => "#FFD166",
        "Git"      => "#FFD166",
        "UI"       => "#FF5EC4",
        "Update"   => "#4CDFFF",
        _          => "#8388BD",
    };

    /// <summary>Compact one-line render for "Copy all" / clipboard.</summary>
    public string ToFlatString()
    {
        var line = $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff}  [{LevelDisplay,-8}] [{Category,-8}] {Message}";
        return Exception == null ? line : line + "\n" + Exception;
    }
}

public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warning,
    Error,
    Critical,
}
