namespace CluadeX.Services.Media;

/// <summary>
/// The fonts the media editor's text layers may use: the ones installed on this PC (a web page cannot list them; WPF
/// can — <c>Fonts.SystemFontFamilies</c>), read once, off the UI thread, and handed to the page with the file.
/// </summary>
public static class MediaFonts
{
    // Fonts that draw Thai well, best first (Windows ships the first three).
    private static readonly string[] ThaiFirst = { "Leelawadee UI", "Leelawadee", "Tahoma", "Noto Sans Thai", "Sarabun", "Cordia New", "Angsana New" };
    private static readonly string[] LatinFirst = { "Segoe UI", "Arial", "Calibri", "Tahoma" };

    private static Task<IReadOnlyList<string>>? _installed;

    /// <summary>Installed font family names, sorted, without duplicates. Computed once per run.</summary>
    public static Task<IReadOnlyList<string>> InstalledAsync() => _installed ??= Task.Run(ReadInstalled);

    private static IReadOnlyList<string> ReadInstalled()
    {
        var names = new List<string>();
        try
        {
            foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
            {
                string? name = null;
                try
                {
                    var en = System.Windows.Markup.XmlLanguage.GetLanguage("en-us");
                    if (!family.FamilyNames.TryGetValue(en, out name)) name = family.FamilyNames.Values.FirstOrDefault();
                }
                catch { /* a font with a broken name table */ }
                name ??= family.Source;
                if (Clean(name) is { } ok) names.Add(ok);
            }
        }
        catch { /* the font cache unreadable: the page still has its own fallbacks */ }
        return Normalize(names);
    }

    /// <summary>Sorted (culture-independent), unique ignoring case, cleaned (see <see cref="Clean"/>).</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?> names) =>
        names.Select(Clean).OfType<string>()
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
             .ToList();

    /// <summary>A family name safe to put in a CSS font list: no quotes, backslashes, semicolons or control
    /// characters, at most 100 characters; null when nothing is left (or it is a file path, as some Source values are).</summary>
    public static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.Contains("file:", StringComparison.OrdinalIgnoreCase) || name.Contains('#')) return null;
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            if (!char.IsControl(c) && c is not ('"' or '\'' or '\\' or ';' or '{' or '}' or '<' or '>')) sb.Append(c);
        string s = sb.ToString().Trim();
        if (s.Length > 100) s = s[..100].Trim();
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// The font a new text layer starts with: one that has Thai letters when the app speaks Thai (Leelawadee UI, else
    /// Tahoma, …), else Segoe UI. Picked from <paramref name="installed"/> only; "Tahoma" when none of them is there
    /// (the page's own fallback list still draws Thai then).
    /// </summary>
    public static string DefaultFont(IEnumerable<string> installed, string? uiLanguage)
    {
        var set = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        var order = string.Equals(uiLanguage, "th", StringComparison.OrdinalIgnoreCase) ? ThaiFirst : LatinFirst;
        foreach (var f in order) if (set.Contains(f)) return set.First(s => string.Equals(s, f, StringComparison.OrdinalIgnoreCase));
        foreach (var f in ThaiFirst.Concat(LatinFirst)) if (set.Contains(f)) return set.First(s => string.Equals(s, f, StringComparison.OrdinalIgnoreCase));
        return "Tahoma";
    }
}
