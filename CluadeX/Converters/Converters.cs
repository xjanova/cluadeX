using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CluadeX.Models;

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

public class RoleToBrushConverter : IValueConverter
{
    // Cached frozen brushes to avoid GC pressure
    private static readonly SolidColorBrush UserBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)));
    private static readonly SolidColorBrush AssistantBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)));
    private static readonly SolidColorBrush ExecBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF)));
    private static readonly SolidColorBrush ToolBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x94, 0xE2, 0xD5)));
    private static readonly SolidColorBrush SystemBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xCB, 0xA6, 0xF7)));
    private static readonly SolidColorBrush StatusBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0x9C, 0xAC)));  // Overlay1 — muted
    private static readonly SolidColorBrush DefaultBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)));
    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            MessageRole.User => UserBrush,
            MessageRole.Assistant => AssistantBrush,
            MessageRole.CodeExecution => ExecBrush,
            MessageRole.ToolAction => ToolBrush,
            MessageRole.AgentStatus => StatusBrush,
            MessageRole.System => SystemBrush,
            _ => DefaultBrush,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class RoleToBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush UserBg = Freeze(new SolidColorBrush(Color.FromRgb(0x2F, 0x3D, 0x5C)));
    private static readonly SolidColorBrush AssistantBg = Freeze(new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)));
    private static readonly SolidColorBrush ExecBg = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x35, 0x30)));
    private static readonly SolidColorBrush ToolBg = Freeze(new SolidColorBrush(Color.FromRgb(0x2D, 0x3A, 0x3E)));
    private static readonly SolidColorBrush StatusBg = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)));  // Base — near-transparent
    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            MessageRole.User => UserBg,
            MessageRole.Assistant => AssistantBg,
            MessageRole.CodeExecution => ExecBg,
            MessageRole.ToolAction => ToolBg,
            MessageRole.AgentStatus => StatusBg,
            _ => AssistantBg,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class RoleToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            MessageRole.User => "\U0001F464 You",
            MessageRole.Assistant => "\u2728 CluadeX",
            MessageRole.CodeExecution => "\u25B6 Execution",
            MessageRole.ToolAction => "\U0001F527 Tool",
            MessageRole.AgentStatus => "\u2699 Agent",
            MessageRole.System => "\u2699 System",
            MessageRole.PermissionRequest => "\U0001F512 Permission",
            MessageRole.Thinking => "\U0001F9E0 Thinking",
            _ => "Unknown",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class RoleToAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is MessageRole.User ? HorizontalAlignment.Right : HorizontalAlignment.Left;
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
            MessageRole.User => new Thickness(100, 3, 12, 3),
            MessageRole.AgentStatus => new Thickness(12, 1, 200, 1),  // compact, narrow
            _ => new Thickness(12, 3, 100, 3),
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
            MessageRole.User => new CornerRadius(16, 16, 4, 16),    // Tail bottom-right
            _ => new CornerRadius(16, 16, 16, 4),                   // Tail bottom-left
        };
    }

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
                ? new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)) // Green
                : new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)); // Red
        }
        return new SolidColorBrush(Color.FromRgb(0xA6, 0xAD, 0xC8)); // Subtext
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
        if (percent < 50) return new SolidColorBrush(Color.FromRgb(0x94, 0xE2, 0xD5)); // Teal
        if (percent < 75) return new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF)); // Yellow
        return new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)); // Red
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
