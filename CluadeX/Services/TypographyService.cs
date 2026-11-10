using System.Windows;
using System.Windows.Media;

namespace CluadeX.Services;

/// <summary>
/// The owner's text size and font (Settings › General › Text size and font), applied live and remembered.
/// <list type="bullet">
/// <item><b>Size</b> — a zoom of the whole main window, 80–160 %: MainWindow puts a LayoutTransform on its shell and
/// scales the drag strip of the title bar with it (<see cref="Changed"/>). Text, icons and น้อง CluadeX grow together,
/// so nothing that was laid out to fit stops fitting; WPF draws the text again at the new size, so it stays sharp.</item>
/// <item><b>Font</b> — "" follows the theme's own Font.Ui; <see cref="BundledFont"/> is Noto Sans shipped in Assets/Fonts
/// (OFL 1.1) with Thai drawn by Windows' Leelawadee UI (see <see cref="BundledFamily"/>); anything else must be the name of
/// a font installed on this PC. The choice is put in
/// Application.Resources itself as Font.Ui and Font.Ui.Display, which win over the theme's merged dictionary, so it
/// stays when the theme changes; "" takes the keys out again.</item>
/// </list>
/// A value read from settings.json is never used as it is: the zoom is clamped, and a font that is not installed (or a
/// path or URI written there by hand) is ignored — a FontFamily can name a file anywhere.
/// </summary>
public sealed class TypographyService
{
    public const double MinScale = 0.8, MaxScale = 1.6, Step = 0.05;

    /// <summary>The setting value for "the theme's own font" (the default).</summary>
    public const string ThemeFont = "";

    /// <summary>The setting value for the font shipped with CluadeX.</summary>
    public const string BundledFont = "bundled";

    /// <summary>Where the bundled fonts are (CluadeX.csproj: Assets\Fonts\*.ttf as Resource).</summary>
    public static readonly Uri FontsBase = new("pack://application:,,,/CluadeX;component/Assets/Fonts/");

    /// <summary>
    /// Latin in four real weights from Noto Sans CX (Noto Sans Thai's Latin with the Thai block taken out), Thai from
    /// Leelawadee UI, then Noto Sans for Greek, Cyrillic and Vietnamese, then the system fallback (CJK, emoji …).
    /// <para>Why Thai is not Noto: WPF's own text shaping mis-places Thai vowels and tone marks in modern OpenType Thai
    /// fonts — Noto Sans Thai and IBM Plex Sans Thai both draw "ปั๊ม", "น้ำ", "ก่" with the marks off their letters (rendered
    /// and compared 2026-11-08: tests/themetest --hero). Leelawadee UI comes with every Windows 10/11, stacks them right in
    /// WPF, and CluadeX does not ship it.</para>
    /// </summary>
    public static FontFamily BundledFamily { get; } = new(FontsBase, "./#Noto Sans CX, Leelawadee UI, ./#Noto Sans, Segoe UI");

    /// <summary>The instance DI built (null before startup, and in tests that build no services).</summary>
    public static TypographyService? Current { get; private set; }

    private readonly SettingsService _settings;
    private readonly DebugLogService? _log;

    /// <summary>The zoom on screen now (1 = 100 %).</summary>
    public double Scale { get; private set; } = 1.0;

    /// <summary>The font on screen now: <see cref="ThemeFont"/>, <see cref="BundledFont"/> or an installed family.</summary>
    public string FontId { get; private set; } = ThemeFont;

    /// <summary>Raised on the UI thread after the zoom or the font changed (static: a window built before the service
    /// still hears it).</summary>
    public static event Action? Changed;

    public TypographyService(SettingsService settings, DebugLogService? log = null)
    {
        _settings = settings;
        _log = log;
        Current = this;
    }

    /// <summary>A zoom that can go on screen: finite, 80–160 %, on a 5 % step. Anything else is 100 %.</summary>
    public static double ClampScale(double s)
    {
        if (!double.IsFinite(s) || s <= 0) return 1.0;
        s = Math.Clamp(s, MinScale, MaxScale);
        return Math.Round(Math.Round(s / Step) * Step, 2);
    }

    /// <summary>The canonical id for <paramref name="id"/>, or <see cref="ThemeFont"/> when it is not one CluadeX can use:
    /// empty, the bundled font, or the exact name of a font installed here (matched case-insensitively).</summary>
    public static string NormalizeFont(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return ThemeFont;
        string t = id.Trim();
        if (string.Equals(t, BundledFont, StringComparison.OrdinalIgnoreCase)) return BundledFont;
        foreach (var name in InstalledFonts())
            if (string.Equals(name, t, StringComparison.OrdinalIgnoreCase)) return name;
        return ThemeFont;
    }

    private static IReadOnlyList<string>? _installed;

    /// <summary>The text fonts installed on this PC, A–Z (symbol and icon fonts left out), read once.</summary>
    public static IReadOnlyList<string> InstalledFonts()
    {
        if (_installed != null) return _installed;
        var list = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Fonts.SystemFontFamilies)
            {
                string name = f.Source;
                if (string.IsNullOrWhiteSpace(name) || name.Contains('#') || name.Contains('/') || name.Contains('\\')) continue;
                if (IsSymbolFont(name)) continue;
                list.Add(name);
            }
        }
        catch (Exception) { /* a broken font cache: offer the theme and the bundled font only */ }
        return _installed = list.ToList();
    }

    private static bool IsSymbolFont(string name) =>
        name.Contains("MDL2", StringComparison.OrdinalIgnoreCase) || name.Contains("Fluent Icons", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Symbol", StringComparison.OrdinalIgnoreCase) || name.Contains("Webdings", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Wingdings", StringComparison.OrdinalIgnoreCase) || name.Contains("Marlett", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Emoji", StringComparison.OrdinalIgnoreCase) || name.Contains("HoloLens", StringComparison.OrdinalIgnoreCase);

    /// <summary>The family a font id draws with (for the picker's preview too). Installed fonts keep Thai on a proper
    /// Thai font when they have none of their own.</summary>
    public static FontFamily FamilyFor(string id) => id switch
    {
        BundledFont => BundledFamily,
        ThemeFont => ThemeFamily(),
        _ => new FontFamily(id + ", Leelawadee UI, Segoe UI"),
    };

    /// <summary>The theme's own Font.Ui — from the theme dictionary, not the override this service puts on top of it.</summary>
    public static FontFamily ThemeFamily()
    {
        var merged = Application.Current?.Resources.MergedDictionaries;
        if (merged != null)
            for (int i = merged.Count - 1; i >= 0; i--)
                if (merged[i].Contains("Font.Ui") && merged[i]["Font.Ui"] is FontFamily f) return f;
        return new FontFamily("Segoe UI");
    }

    /// <summary>Put the saved size and font on screen — once at startup, before the main window exists. A damaged value
    /// is replaced by the default and written back. Never throws.</summary>
    public void ApplySaved()
    {
        double savedScale = _settings.Settings.UiScale;
        string? savedFont = _settings.Settings.UiFont;
        double scale = ClampScale(savedScale);
        string font = NormalizeFont(savedFont);
        if (scale != savedScale || !string.Equals(font, savedFont ?? "", StringComparison.Ordinal))
        {
            if (!string.Equals(font, savedFont ?? "", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(savedFont))
                _log?.Warn("Typography", $"settings.json names a font that is not installed ({Clip(savedFont)}) — the theme's font is used");
            try { _settings.UpdateSettings(s => { s.UiScale = scale; s.UiFont = font; }); }
            catch (Exception ex) { _log?.Warn("Typography", "could not save the text settings", ex); }
        }
        Scale = scale;
        FontId = font;
        ApplyFont();
    }

    /// <summary>A new zoom, now; remembered when <paramref name="persist"/>.</summary>
    public void SetScale(double scale, bool persist = true)
    {
        scale = ClampScale(scale);
        if (scale == Scale) return;
        Scale = scale;
        if (persist) Save(s => s.UiScale = scale);
        Changed?.Invoke();
    }

    /// <summary>A new font, now; remembered when <paramref name="persist"/>. An id that is not usable is the theme's font.</summary>
    public void SetFont(string? id, bool persist = true)
    {
        string font = NormalizeFont(id);
        if (font == FontId) return;
        FontId = font;
        ApplyFont();
        if (persist) Save(s => s.UiFont = font);
        Changed?.Invoke();
    }

    private void ApplyFont()
    {
        var res = Application.Current?.Resources;
        if (res == null) return;
        if (FontId == ThemeFont)
        {
            res.Remove("Font.Ui");
            res.Remove("Font.Ui.Display");
            return;
        }
        var family = FamilyFor(FontId);
        res["Font.Ui"] = family;
        res["Font.Ui.Display"] = family;
    }

    private void Save(Action<Models.AppSettings> change)
    {
        try { _settings.UpdateSettings(change); }
        catch (Exception ex) { _log?.Warn("Typography", "could not save the text settings", ex); }
    }

    private static string Clip(string s) => s.Length > 60 ? s[..60] + "…" : s;
}
