using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

namespace CluadeX.Views.Editor;

/// <summary>
/// Maps file extensions to AvalonEdit highlighting definitions and re-tints the built-in
/// (light-background) color schemes to CluadeX's neon-on-dark palette. Built-in xshd colors
/// like keyword #0000FF / string #A31515 are unreadable on the editor's #06071A background.
/// Definitions are singletons inside HighlightingManager, so each one is re-tinted once.
/// </summary>
public static class NeonHighlighting
{
    private static readonly HashSet<string> Tinted = new(StringComparer.OrdinalIgnoreCase);

    // Neon palette (matches Theme.xaml's neon brushes)
    private static readonly Color Violet = Color.FromRgb(0xA6, 0x72, 0xFF);   // keywords
    private static readonly Color Cyan   = Color.FromRgb(0x4C, 0xDF, 0xFF);   // types / methods
    private static readonly Color Mint   = Color.FromRgb(0x5C, 0xFF, 0xB0);   // strings
    private static readonly Color Amber  = Color.FromRgb(0xFF, 0xC6, 0x6D);   // numbers
    private static readonly Color Pink   = Color.FromRgb(0xFF, 0x5E, 0xC4);   // preprocessor / regex
    private static readonly Color Muted  = Color.FromRgb(0x7E, 0x8A, 0xA8);   // comments

    /// <summary>Resolve (and neon-tint) the definition for a file extension; null = plain text.</summary>
    public static IHighlightingDefinition? ForExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        string ext = extension.TrimStart('.').ToLowerInvariant();

        // Aliases the built-in manager doesn't know (it has no TS/JSON/YAML definitions —
        // JavaScript is the closest readable fallback for those).
        string? name = ext switch
        {
            "cs" or "csx" => "C#",
            "js" or "jsx" or "ts" or "tsx" or "mjs" or "cjs" or "json" or "jsonc" => "JavaScript",
            "xml" or "xaml" or "csproj" or "props" or "targets" or "config" or "svg" or "resx" or "plist" => "XML",
            "html" or "htm" or "cshtml" or "razor" => "HTML",
            "css" or "scss" or "less" => "CSS",
            "md" or "markdown" => "MarkDown",
            "py" or "pyw" => "Python",
            "ps1" or "psm1" or "psd1" => "PowerShell",
            "cpp" or "cc" or "cxx" or "h" or "hpp" or "c" => "C++",
            "java" or "kt" => "Java",
            "sql" => "TSQL",
            "vb" => "VB",
            "php" => "PHP",
            "patch" or "diff" => "Patch",
            _ => null,
        };

        var def = name != null
            ? HighlightingManager.Instance.GetDefinition(name)
            : HighlightingManager.Instance.GetDefinitionByExtension("." + ext);
        if (def == null) return null;

        if (Tinted.Add(def.Name)) Retint(def);
        return def;
    }

    private static void Retint(IHighlightingDefinition def)
    {
        foreach (var color in def.NamedHighlightingColors)
        {
            var neon = PickNeon(color.Name);
            if (neon != null)
            {
                color.Foreground = new SimpleHighlightingBrush(neon.Value);
                continue;
            }

            // Unknown name: if its configured foreground is too dark for the dark background,
            // brighten it in place so nothing renders as ink-on-ink.
            var current = (color.Foreground as SimpleHighlightingBrush)?.GetBrush(null) as SolidColorBrush;
            if (current == null) continue;
            var c = current.Color;
            double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            if (luminance < 0.45)
            {
                static byte Lift(byte v) => (byte)Math.Min(255, v + 110);
                color.Foreground = new SimpleHighlightingBrush(Color.FromRgb(Lift(c.R), Lift(c.G), Lift(c.B)));
            }
        }
    }

    private static Color? PickNeon(string colorName)
    {
        string n = colorName.ToLowerInvariant();
        if (n.Contains("comment") || n.Contains("documentation")) return Muted;
        if (n.Contains("string") || n.Contains("char")) return Mint;
        if (n.Contains("number") || n.Contains("digit")) return Amber;
        if (n.Contains("preprocessor") || n.Contains("regex")) return Pink;
        if (n.Contains("method") || n.Contains("function") || n.Contains("class") || n.Contains("type")
            || n.Contains("interface") || n.Contains("entity") || n.Contains("tagname")) return Cyan;
        if (n.Contains("keyword") || n.Contains("modifier") || n.Contains("statement") || n.Contains("visibility")
            || n.Contains("truefalse") || n.Contains("this") || n.Contains("null") || n.Contains("operator")
            || n.Contains("reference") || n.Contains("valuetype") || n.Contains("namespace") || n.Contains("goto")
            || n.Contains("exception") || n.Contains("checked") || n.Contains("unsafe") || n.Contains("param")
            || n.Contains("getset") || n.Contains("contextkeyword") || n.Contains("attribute")) return Violet;
        return null;
    }
}
