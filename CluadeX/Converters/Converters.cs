using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool boolValue;

        // Handle int (for collection Count bindings like Messages.Count)
        if (value is int intVal)
            boolValue = intVal > 0;
        else if (value is long longVal)
            boolValue = longVal > 0;
        else if (value is string str)
            boolValue = !string.IsNullOrEmpty(str);
        else
            boolValue = value is bool b && b;

        if (parameter?.ToString() == "Invert") boolValue = !boolValue;
        return boolValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>Message role → its label colour (Role.* tokens). The brushes are ThemeColors.Live: one per token, re-coloured
/// in place on a theme switch, so a label already on screen follows it.</summary>
public class RoleToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            MessageRole.User => ThemeColors.Live("Role.User", Color.FromRgb(0x89, 0xB4, 0xFA)),
            MessageRole.Assistant => ThemeColors.Live("Role.Assistant", Color.FromRgb(0xA6, 0xE3, 0xA1)),
            MessageRole.CodeExecution => ThemeColors.Live("Role.Exec", Color.FromRgb(0xF9, 0xE2, 0xAF)),
            MessageRole.ToolAction => ThemeColors.Live("Role.Tool", Color.FromRgb(0x94, 0xE2, 0xD5)),
            MessageRole.AgentStatus => ThemeColors.Live("Role.Status", Color.FromRgb(0x9C, 0x9C, 0xAC)),
            MessageRole.System => ThemeColors.Live("Role.System", Color.FromRgb(0xCB, 0xA6, 0xF7)),
            _ => ThemeColors.Live("Role.Default", Color.FromRgb(0xCD, 0xD6, 0xF4)),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Message role → its bubble background (Role.*.Background tokens, live — see RoleToBrushConverter).</summary>
public class RoleToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            // Only the user's own messages and run output sit on a surface. The assistant's reply is plain text on the
            // page (a bubble around every answer made the transcript a stack of heavy cards), a tool call is a thin
            // row on the sunken surface, a system line is muted text.
            MessageRole.User => ThemeColors.Live("Role.User.Background", Color.FromRgb(0x2F, 0x3D, 0x5C)),
            MessageRole.CodeExecution => ThemeColors.Live("Role.Exec.Background", Color.FromRgb(0x3A, 0x35, 0x30)),
            MessageRole.ToolAction => ThemeColors.Live("State.Fill.Faint", Color.FromArgb(0x11, 0xFF, 0xFF, 0xFF)),
            _ => Brushes.Transparent,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class RoleToLabelConverter : IValueConverter
{
    private static string L(string key) => CluadeX.Services.LocalizedResources.Instance[key];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            MessageRole.User => "\U0001F464 " + L("chat.you"),
            MessageRole.Assistant => "\u2728 " + L("chat.role.assistant"),
            MessageRole.CodeExecution => "\u25B6 " + L("chat.role.execution"),
            MessageRole.ToolAction => "\U0001F527 " + L("chat.role.tool"),
            MessageRole.AgentStatus => "\u2699 " + L("chat.role.agent"),
            MessageRole.System => "\u2699 " + L("chat.role.system"),
            MessageRole.PermissionRequest => "\U0001F512 " + L("chat.role.permission"),
            MessageRole.Thinking => "\U0001F9E0 " + L("chat.role.thinking"),
            MessageRole.ContextSummary => "\U0001F5DC " + L("chat.role.summary"),
            _ => "",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class RoleToAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            MessageRole.User => HorizontalAlignment.Right,
            MessageRole.ToolAction => HorizontalAlignment.Stretch,   // a list of equal rows, the time at the right edge
            _ => HorizontalAlignment.Left,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class RoleToMarginConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            // Inside the centred reading column: the user's message right-aligned, the reply full width, tool rows
            // indented under the reply they belong to.
            MessageRole.User => new Thickness(120, 8, 0, 4),
            MessageRole.Assistant => new Thickness(0, 8, 0, 2),
            MessageRole.ToolAction or MessageRole.AgentStatus or MessageRole.CodeExecution => new Thickness(20, 1, 0, 1),
            _ => new Thickness(0, 3, 0, 3),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class RoleToCornerRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            MessageRole.User => new CornerRadius(12, 12, 4, 12),    // tail bottom-right
            MessageRole.ToolAction or MessageRole.CodeExecution => new CornerRadius(6),
            _ => new CornerRadius(0),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>True when the two bound values are equal (the sidebar: is this session the one on screen?).</summary>
public class EqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && values[0] != null && Equals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Message role → the padding inside its body: none around the plain-text reply, a slim row for a tool.</summary>
public class RoleToPaddingConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        MessageRole.User => new Thickness(12, 8, 12, 8),
        MessageRole.ToolAction => new Thickness(10, 5, 8, 5),
        MessageRole.CodeExecution => new Thickness(10, 8, 10, 8),
        _ => new Thickness(0, 2, 0, 2),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

public class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        long bytes = System.Convert.ToInt64(value);
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {suffixes[order]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class VramFitConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int requiredMB && parameter is string vramStr && int.TryParse(vramStr, out int availMB))
        {
            return requiredMB <= availMB
                ? ThemeColors.Live("Status.Success.Soft", Color.FromRgb(0xA6, 0xE3, 0xA1))
                : ThemeColors.Live("Status.Danger.Soft", Color.FromRgb(0xF3, 0x8B, 0xA8));
        }
        return ThemeColors.Live("Text.Soft.Secondary", Color.FromRgb(0xA6, 0xAD, 0xC8));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && parameter is string options)
        {
            var parts = options.Split('|');
            return b ? parts[0] : (parts.Length > 1 ? parts[1] : "");
        }
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class ContextUsageBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double percent = value is double d ? d : 0;
        if (percent < 50) return ThemeColors.Live("Progress.Context.Fill", Color.FromRgb(0x94, 0xE2, 0xD5));
        if (percent < 75) return ThemeColors.Live("Status.Warning.Soft", Color.FromRgb(0xF9, 0xE2, 0xAF));
        return ThemeColors.Live("Status.Danger.Soft", Color.FromRgb(0xF3, 0x8B, 0xA8));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// LED segment gate converter: returns true if ContextUsagePercent >= threshold.
/// Parameter is the threshold (e.g. "0", "20", "40", "60", "80").
/// Used to light up individual LED segments in the context bar.
/// </summary>
public class ContextLedGateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double percent = value is double d ? d : 0;
        double threshold = 0;
        if (parameter is string s && double.TryParse(s, out var t)) threshold = t;
        return percent > threshold;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isNotNull = value != null;
        if (parameter?.ToString() == "Invert") isNotNull = !isNotNull;
        return isNotNull ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Visible when the bound value's text equals ConverterParameter, else Collapsed
/// (shows one settings category at a time).</summary>
public class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public class NavItemConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() == parameter?.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return parameter?.ToString() ?? "";
        return Binding.DoNothing;
    }
}

// ─── Time Machine converters ──────────────────────────────────────────
// Frozen brushes so they don't recreate per cell during diff/timeline render.
public class DiffKindToBrushConverter : IValueConverter
{
    private static Brush AddBrush => ThemeColors.Live("Diff.Added", Color.FromRgb(0x5C, 0xFF, 0xB0));
    private static Brush DelBrush => ThemeColors.Live("Diff.Deleted", Color.FromRgb(0xFF, 0x6E, 0x6E));
    private static Brush HunkBrush => ThemeColors.Live("Diff.Hunk", Color.FromRgb(0xA6, 0x72, 0xFF));
    private static Brush NeutralBrush => ThemeColors.Live("Diff.Context", Color.FromRgb(0xC0, 0xC6, 0xEE));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is char c)
        {
            return c switch
            {
                '+' => AddBrush,
                '-' => DelBrush,
                '@' => HunkBrush,
                _ => NeutralBrush,
            };
        }
        return NeutralBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class DiffKindToBackgroundConverter : IValueConverter
{
    private static Brush AddBg => ThemeColors.Live("Diff.Added.Background", Color.FromArgb(0x24, 0x5C, 0xFF, 0xB0));
    private static Brush DelBg => ThemeColors.Live("Diff.Deleted.Background", Color.FromArgb(0x24, 0xFF, 0x6E, 0x6E));
    private static Brush HunkBg => ThemeColors.Live("Diff.Hunk.Background", Color.FromArgb(0x20, 0xA6, 0x72, 0xFF));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is char c)
        {
            return c switch
            {
                '+' => AddBg,
                '-' => DelBg,
                '@' => HunkBg,
                _ => Brushes.Transparent,
            };
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>True → violet (AI), False → cyan (human). Used for commit dot + avatar accents.</summary>
public class AiBoolToBrushConverter : IValueConverter
{
    private static Brush AiBrush => ThemeColors.Live("Accent.2", Color.FromRgb(0xA6, 0x72, 0xFF));
    private static Brush HumanBrush => ThemeColors.Live("Accent.1", Color.FromRgb(0x4C, 0xDF, 0xFF));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? AiBrush : HumanBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>True → violet→cyan avatar gradient (AI); False → cyan→mint (human).</summary>
public class AiBoolToAvatarConverter : IValueConverter
{
    // Avatar.Ai / Avatar.Human — live gradients that follow a theme switch.
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b
            ? ThemeColors.LiveGradient("Avatar.Ai", Color.FromRgb(0xA6, 0x72, 0xFF), Color.FromRgb(0x4C, 0xDF, 0xFF))
            : ThemeColors.LiveGradient("Avatar.Human", Color.FromRgb(0x4C, 0xDF, 0xFF), Color.FromRgb(0x5C, 0xFF, 0xB0));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Returns "M" if Status='M', "A" if added, etc. — for file change badges.</summary>
public class FileStatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is char c ? c.ToString() : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Parse a "#RRGGBB" hex string into a Color (for binding gradients to per-item hex strings).</summary>
public class HexToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            if (value is string hex && !string.IsNullOrWhiteSpace(hex))
            {
                return (Color)ColorConverter.ConvertFromString(hex);
            }
        }
        catch { }
        return ThemeColors.Color("Text.Muted", Color.FromRgb(0x83, 0x88, 0xBD));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

