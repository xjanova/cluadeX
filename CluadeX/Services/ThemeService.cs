using System.Windows;
using System.Windows.Media;

namespace CluadeX.Services;

/// <summary>The themes CluadeX ships. The id is the file name under Resources/Themes/ and the value stored in
/// AppSettings.Theme.</summary>
public static class ThemeIds
{
    public const string Classic = "Classic";
    public const string Midnight = "Midnight";
    public const string NeonCyber = "NeonCyber";

    /// <summary>The look CluadeX had before themes existed — what a fresh install, an unknown or a damaged
    /// settings value gets.</summary>
    public const string Default = Midnight;

    public static IReadOnlyList<string> All { get; } = [Classic, Midnight, NeonCyber];
}

/// <summary>One entry of the theme picker. <paramref name="IsReady"/> false shows an "in progress" badge on the
/// card: the theme dictionary exists and applies, but its team has not finished painting it yet.</summary>
public sealed record ThemeInfo(string Id, string NameKey, string DescKey, bool IsReady);

/// <summary>
/// Swaps the app's look at runtime.
///
/// A theme is ONE ResourceDictionary (Resources/Themes/&lt;Id&gt;.xaml) holding every colour, brush, effect and
/// font token (Tokens.md). App.xaml merges the default (Midnight) FIRST in Application.Resources; <see cref="Apply"/>
/// replaces that entry, so everything that references a token with {DynamicResource} — the shared styles in
/// Resources/Theme.xaml, the main window, converted views, and code that called SetResourceReference — restyles
/// live, without a restart. Views still using {StaticResource} pick the new theme up the next time they are built.
///
/// C# that needs a colour asks here instead of building one: <see cref="GetBrush"/> / <see cref="GetColor"/> (or
/// the static <see cref="BrushOf"/> / <see cref="ColorOf"/> where there is no DI), and listens to
/// <see cref="ThemeChanged"/> to refresh a value it handed to a binding. Better still, an element property set from
/// code uses <c>element.SetResourceReference(property, "Token.Key")</c>, which follows theme switches by itself.
/// </summary>
public sealed class ThemeService
{
    /// <summary>The string resource every theme dictionary carries with its own id — how the theme slot is found
    /// in Application.Resources and how a loaded dictionary is checked before it goes live.</summary>
    public const string IdKey = "Theme.Id";

    private readonly SettingsService _settings;
    private readonly DebugLogService? _log;

    /// <summary>The instance DI built (null before startup). For converters and models that are not built by DI.</summary>
    public static ThemeService? Current { get; private set; }

    public IReadOnlyList<ThemeInfo> Themes { get; } =
    [
        new(ThemeIds.Classic, "theme.classic.name", "theme.classic.desc", IsReady: false),
        new(ThemeIds.Midnight, "theme.midnight.name", "theme.midnight.desc", IsReady: true),
        new(ThemeIds.NeonCyber, "theme.neon.name", "theme.neon.desc", IsReady: false),
    ];

    /// <summary>The theme on screen now. Midnight until <see cref="ApplySaved"/> / <see cref="Apply"/> says otherwise
    /// (App.xaml merges Midnight).</summary>
    public string CurrentThemeId { get; private set; } = ThemeIds.Default;

    /// <summary>Raised on the UI thread after a different theme went live, with its id.</summary>
    public event Action<string>? ThemeChanged;

    public ThemeService(SettingsService settings, DebugLogService? log = null)
    {
        _settings = settings;
        _log = log;
        Current = this;
    }

    /// <summary>The canonical id for <paramref name="id"/> (case-insensitive), or null when it names no theme —
    /// a value read from settings.json is never trusted to be a path or anything else.</summary>
    public static string? Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var known in ThemeIds.All)
            if (string.Equals(known, id.Trim(), StringComparison.OrdinalIgnoreCase)) return known;
        return null;
    }

    /// <summary>Where a theme's dictionary lives (a known id only — see <see cref="Normalize"/>).</summary>
    public static Uri DictionaryUri(string id)
    {
        string known = Normalize(id) ?? throw new ArgumentException($"unknown theme '{id}'", nameof(id));
        return new Uri($"pack://application:,,,/CluadeX;component/Resources/Themes/{known}.xaml", UriKind.Absolute);
    }

    /// <summary>
    /// Put the saved theme on screen — once at startup, before the main window exists. An empty, unknown or
    /// tampered value starts the default theme and is written back as the default, so a bad value cannot stick.
    /// Returns the theme now active. Never throws: a theme that fails to load leaves the default in place.
    /// </summary>
    public string ApplySaved()
    {
        string? saved = _settings.Settings.Theme;
        string? known = Normalize(saved);
        string id = known ?? ThemeIds.Default;
        if (known == null && !string.IsNullOrEmpty(saved))
            _log?.Warn("Theme", $"settings.json names an unknown theme ({Clip(saved)}) — starting {id}");
        if (!string.Equals(saved, id, StringComparison.Ordinal))
        {
            // Unknown, damaged or merely mis-cased: store the canonical id so the bad value cannot stick.
            try { _settings.UpdateSettings(s => s.Theme = id); } catch (Exception ex) { _log?.Warn("Theme", "could not save the theme", ex); }
        }
        Apply(id, persist: false);
        return CurrentThemeId;
    }

    /// <summary>
    /// Make <paramref name="themeId"/> the look of the whole app, now. True when a different theme went live
    /// (and <see cref="ThemeChanged"/> was raised); false for an unknown id, a theme that failed to load (the
    /// current one stays) or the theme already on screen. <paramref name="persist"/> remembers the choice in
    /// settings.json. Any thread; the swap itself runs on the UI thread.
    /// </summary>
    public bool Apply(string? themeId, bool persist = true)
    {
        var app = Application.Current;
        if (app == null) return false;
        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => Apply(themeId, persist));

        string? id = Normalize(themeId);
        if (id == null)
        {
            _log?.Warn("Theme", $"ignored a request for an unknown theme ({Clip(themeId)})");
            return false;
        }

        var merged = app.Resources.MergedDictionaries;
        int slot = FindSlot(merged);
        if (slot >= 0 && merged[slot][IdKey] as string == id)
        {
            CurrentThemeId = id;
            if (persist) Persist(id);
            return false;
        }

        ResourceDictionary dict;
        try
        {
            dict = new ResourceDictionary { Source = DictionaryUri(id) };
            if (dict[IdKey] as string != id)
                throw new InvalidOperationException($"{id}.xaml does not name itself '{id}' in {IdKey}");
        }
        catch (Exception ex)
        {
            _log?.Error("Theme", $"theme {id} could not be loaded — keeping {CurrentThemeId}", ex);
            return false;
        }

        // Replacing the entry (not Remove + Add) keeps the theme ahead of the shared styles, and WPF re-resolves
        // every DynamicResource in every open window in one pass.
        if (slot >= 0) merged[slot] = dict;
        else merged.Insert(0, dict);

        CurrentThemeId = id;
        _log?.Info("Theme", $"theme {id} applied");
        if (persist) Persist(id);
        ThemeChanged?.Invoke(id);
        return true;
    }

    /// <summary>The current theme's brush for <paramref name="token"/>; <paramref name="fallback"/> (or transparent)
    /// when no theme defines it. Frozen brushes from a theme are shared — never modify one, Clone() it.</summary>
    public Brush GetBrush(string token, Brush? fallback = null) => BrushOf(token, fallback);

    /// <summary>The current theme's colour for <paramref name="token"/>: a Color token as is, a solid brush's colour
    /// with its opacity folded into alpha, a gradient's first stop. <paramref name="fallback"/> otherwise.</summary>
    public Color GetColor(string token, Color fallback) => ColorOf(token, fallback);

    /// <summary><see cref="GetBrush"/> for code that has no ThemeService instance (converters, models).</summary>
    public static Brush BrushOf(string token, Brush? fallback = null)
        => Lookup(token) as Brush ?? fallback ?? Brushes.Transparent;

    /// <summary><see cref="GetColor"/> for code that has no ThemeService instance (converters, models).</summary>
    public static Color ColorOf(string token, Color fallback) => Lookup(token) switch
    {
        Color c => c,
        SolidColorBrush b => Color.FromArgb((byte)Math.Round(b.Color.A * Math.Clamp(b.Opacity, 0, 1)), b.Color.R, b.Color.G, b.Color.B),
        GradientBrush g when g.GradientStops.Count > 0 => g.GradientStops[0].Color,
        _ => fallback,
    };

    private static object? Lookup(string token)
    {
        var app = Application.Current;
        if (app == null || string.IsNullOrEmpty(token)) return null;
        return app.Dispatcher.CheckAccess()
            ? app.TryFindResource(token)
            : app.Dispatcher.Invoke(() => app.TryFindResource(token));
    }

    /// <summary>The merged dictionary that is a theme (carries <see cref="IdKey"/> itself), or -1.</summary>
    private static int FindSlot(System.Collections.ObjectModel.Collection<ResourceDictionary> merged)
    {
        for (int i = 0; i < merged.Count; i++)
            if (merged[i].Contains(IdKey)) return i;
        return -1;
    }

    private void Persist(string id)
    {
        if (string.Equals(_settings.Settings.Theme, id, StringComparison.Ordinal)) return;
        try { _settings.UpdateSettings(s => s.Theme = id); }
        catch (Exception ex) { _log?.Warn("Theme", "could not save the theme choice", ex); }
    }

    private static string Clip(string? s) => s == null ? "null" : s.Length <= 40 ? s : s[..40] + "…";
}
