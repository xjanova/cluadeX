using CluadeX.Services;

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

    // Colours are theme tokens (ThemeColors.Hex): read by the bindings of the Debug Log page, in the theme on screen.
    public string LevelColorHex => Level switch
    {
        LogLevel.Trace    => ThemeColors.Hex("Text.Faint", "#555A8C"),
        LogLevel.Debug    => ThemeColors.Hex("Text.Muted", "#8388BD"),
        LogLevel.Info     => ThemeColors.Hex("Accent.1", "#4CDFFF"),
        LogLevel.Warning  => ThemeColors.Hex("Status.Warning", "#FFD166"),
        LogLevel.Error    => ThemeColors.Hex("Status.Danger", "#FF6E6E"),
        LogLevel.Critical => ThemeColors.Hex("Accent.3", "#FF5EC4"),
        _                 => ThemeColors.Hex("Text.Secondary", "#C0C6EE"),
    };

    public string CategoryColorHex => Category switch
    {
        "Agent"    => ThemeColors.Hex("Accent.2", "#A672FF"),
        "Tool"     => ThemeColors.Hex("Accent.1", "#4CDFFF"),
        "Provider" => ThemeColors.Hex("Accent.4", "#FF8AA6"),
        "MCP"      => ThemeColors.Hex("Status.Success", "#5CFFB0"),
        "FileIO"   => ThemeColors.Hex("Status.Warning", "#FFD166"),
        "Git"      => ThemeColors.Hex("Status.Warning", "#FFD166"),
        "UI"       => ThemeColors.Hex("Accent.3", "#FF5EC4"),
        "Update"   => ThemeColors.Hex("Accent.1", "#4CDFFF"),
        _          => ThemeColors.Hex("Text.Muted", "#8388BD"),
    };

    /// <summary>Compact one-line render for "Copy all" / clipboard.</summary>
    public string ToFlatString()
    {
        // Invariant: on a Thai-culture PC the default calendar is Buddhist and the log said 2569-10-09.
        var line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff}  [{LevelDisplay,-8}] [{Category,-8}] {Message}");
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
