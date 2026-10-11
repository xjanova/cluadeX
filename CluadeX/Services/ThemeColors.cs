using System.Windows;
using System.Windows.Media;

namespace CluadeX.Services;

/// <summary>
/// Theme colours for C# that builds them — converters, view models and models — on top of
/// <see cref="ThemeService"/>.
///
/// Three shapes, one per kind of consumer:
/// <list type="bullet">
/// <item><see cref="Live"/> / <see cref="LiveGradient"/>: one brush per token that is RE-COLOURED IN PLACE when the
///   theme changes. For converters and view-model properties: the value a binding received follows a theme switch
///   without the binding having to run again.</item>
/// <item><see cref="Hex"/>: "#AARRGGBB" of the token in the theme on screen now. For models that expose a colour as a
///   hex string (bound through HexToColorConverter / a string→Brush conversion); the page that shows them is rebuilt
///   on navigation, which reads the getter again.</item>
/// <item><see cref="Color"/>: the token's colour, for code that draws.</item>
/// </list>
/// Every call takes the Midnight value as its fallback, so code running without a theme (no Application — the
/// console harnesses — or a token a theme does not define) still gets the original look.
/// Elements created in code should prefer <c>element.SetResourceReference(prop, "Token")</c>, which needs none of this.
/// </summary>
public static class ThemeColors
{
    private static readonly Dictionary<string, (SolidColorBrush Brush, Color Fallback)> _live = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (LinearGradientBrush Brush, Color From, Color To)> _liveGradients = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _hexSeen = new(StringComparer.Ordinal);
    private static ThemeService? _hooked;

    /// <summary>The token's colour in the theme on screen now (a solid brush's opacity folded into alpha, a
    /// gradient's first stop), or <paramref name="fallback"/>.</summary>
    public static Color Color(string token, Color fallback) => ThemeService.ColorOf(token, fallback);

    /// <summary>"#AARRGGBB" of the token in the theme on screen now, or <paramref name="fallbackHex"/> when there is
    /// no theme (or no such token). Any thread: off the UI thread it answers from the colours the UI thread last
    /// looked up (never a blocking Dispatcher.Invoke, which could deadlock a UI thread that waits on the caller).</summary>
    public static string Hex(string token, string fallbackHex)
    {
        try
        {
            var app = Application.Current;
            if (app == null) return fallbackHex;
            if (!app.Dispatcher.CheckAccess())
                return _hexSeen.TryGetValue(token, out var seen) ? seen : fallbackHex;
            var fb = Parse(fallbackHex);
            if (fb == null) return fallbackHex;
            Hook();
            string hex = ThemeService.ColorOf(token, fb.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _hexSeen[token] = hex;
            return hex;
        }
        catch
        {
            return fallbackHex;   // shutting down, dispatcher gone: the original colour is always a safe answer
        }
    }

    /// <summary>
    /// A solid brush that always shows <paramref name="token"/>'s colour in the current theme — the same instance is
    /// returned for the same token and is re-coloured in place on <see cref="ThemeService.ThemeChanged"/>. Off the UI
    /// thread (or with no application) a frozen snapshot is returned instead, which is safe to hand anywhere.
    /// Never modify the returned brush.
    /// </summary>
    public static Brush Live(string token, Color fallback)
    {
        var app = Application.Current;
        if (app == null || !app.Dispatcher.CheckAccess())
            return Frozen(new SolidColorBrush(Parse(Hex(token, fallback.ToString(System.Globalization.CultureInfo.InvariantCulture))) ?? fallback));

        Hook();
        if (_live.TryGetValue(token, out var hit)) return hit.Brush;
        var brush = new SolidColorBrush(ThemeService.ColorOf(token, fallback));
        _live[token] = (brush, fallback);
        return brush;
    }

    /// <summary>
    /// A two-stop linear gradient (top-left → bottom-right) that follows <paramref name="token"/>: the theme's own
    /// gradient stops when the token is a linear gradient, a flat fill of its colour otherwise. Same instance per token,
    /// re-built in place on a theme change. <paramref name="from"/> / <paramref name="to"/> are the fallback stops.
    /// </summary>
    public static Brush LiveGradient(string token, Color from, Color to)
    {
        var app = Application.Current;
        if (app == null || !app.Dispatcher.CheckAccess())
            return Frozen(new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1)));

        Hook();
        if (_liveGradients.TryGetValue(token, out var hit)) return hit.Brush;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        Fill(brush, token, from, to);
        _liveGradients[token] = (brush, from, to);
        return brush;
    }

    /// <summary>Follow theme switches from the service DI built (a new one in a test replaces the old hook).</summary>
    private static void Hook()
    {
        var current = ThemeService.Current;
        if (current == null || ReferenceEquals(current, _hooked)) return;
        if (_hooked != null) _hooked.ThemeChanged -= OnThemeChanged;
        _hooked = current;
        _hooked.ThemeChanged += OnThemeChanged;
        Refresh();   // anything handed out before the service existed may be stale
    }

    private static void OnThemeChanged(string _) => Refresh();

    /// <summary>Re-colour every live brush from the theme on screen now. ThemeChanged is raised on the UI thread,
    /// which owns these brushes.</summary>
    internal static void Refresh()
    {
        _hexSeen.Clear();
        foreach (var (token, entry) in _live)
        {
            var c = ThemeService.ColorOf(token, entry.Fallback);
            if (entry.Brush.Color != c) entry.Brush.Color = c;
        }
        foreach (var (token, entry) in _liveGradients)
            Fill(entry.Brush, token, entry.From, entry.To);
    }

    private static void Fill(LinearGradientBrush target, string token, Color from, Color to)
    {
        var src = Application.Current?.TryFindResource(token);
        var stops = new GradientStopCollection();
        if (src is LinearGradientBrush lg && lg.GradientStops.Count > 0)
        {
            target.StartPoint = lg.StartPoint;
            target.EndPoint = lg.EndPoint;
            foreach (var s in lg.GradientStops) stops.Add(new GradientStop(s.Color, s.Offset));
        }
        else if (src is GradientBrush g && g.GradientStops.Count > 0)
        {
            foreach (var s in g.GradientStops) stops.Add(new GradientStop(s.Color, s.Offset));
        }
        else if (src != null)
        {
            var c = ThemeService.ColorOf(token, from);
            stops.Add(new GradientStop(c, 0));
            stops.Add(new GradientStop(c, 1));
        }
        else
        {
            stops.Add(new GradientStop(from, 0));
            stops.Add(new GradientStop(to, 1));
        }
        target.GradientStops = stops;
    }

    private static Color? Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return null; }
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        if (f.CanFreeze) f.Freeze();
        return f;
    }
}
