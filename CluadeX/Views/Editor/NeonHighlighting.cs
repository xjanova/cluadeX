using System.Diagnostics;
using System.Xml;
using System.Windows.Media;
using CluadeX.Services;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace CluadeX.Views.Editor;

/// <summary>
/// Maps file extensions to AvalonEdit highlighting definitions and re-tints the built-in
/// (light-background) color schemes to the current theme's syntax palette (the Syntax.* tokens). Built-in
/// xshd colors like keyword #0000FF / string #A31515 are unreadable on a dark editor background.
///
/// Definitions are singletons inside HighlightingManager, shared by every editor. Each one is tinted for a
/// palette and re-tinted — always from its ORIGINAL xshd colours — the next time it is asked for after the
/// theme changed, so switching themes back and forth never compounds a tint.
///
/// AvalonEdit ships nothing for GDScript, Godot shaders / scenes, YAML, TOML, shell or Dart, so those files rendered
/// as plain white text. Our own definitions (Resources/Syntax/*.xshd, embedded) fill the gap; every colour they
/// name lands in a Syntax.* token through <see cref="PickThemed"/>, so the xshd colours are only fallbacks.
/// </summary>
public static class NeonHighlighting
{
    /// <summary>The palette each definition (by name) was last tinted with.</summary>
    private static readonly Dictionary<string, Palette> Tinted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bundled definitions: embedded file under Resources/Syntax, the name it registers as (= its
    /// xshd name), and the extensions HighlightingManager learns for it. Names must not collide with a built-in
    /// one case-insensitively (<see cref="Tinted"/> is keyed that way) — hence "JSONC", not AvalonEdit's "Json".</summary>
    private static readonly (string File, string Name, string[] Extensions)[] Bundled =
    [
        ("GDScript.xshd", "GDScript", [".gd"]),
        ("GodotShader.xshd", "Godot Shader", [".gdshader", ".gdshaderinc"]),
        ("GodotResource.xshd", "Godot Resource", [".tscn", ".tres", ".godot", ".import", ".cfg"]),
        ("Jsonc.xshd", "JSONC", [".json", ".jsonc"]),
        ("Yaml.xshd", "YAML", [".yml", ".yaml"]),
        ("Toml.xshd", "TOML", [".toml"]),
        ("Shell.xshd", "Shell", [".sh", ".bash", ".zsh"]),
        ("Dart.xshd", "Dart", [".dart"]),
    ];

    /// <summary>Registers <see cref="Bundled"/> once, on the first lookup from any thread.</summary>
    private static readonly Lazy<bool> Registered = new(RegisterBundled, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The xshd foreground of every colour we have touched, captured before the first tint.
    /// Keyed by reference: HighlightingColor has value equality.</summary>
    private static readonly Dictionary<HighlightingColor, HighlightingBrush?> Originals =
        new(ReferenceEqualityComparer.Instance);

    // Midnight's palette — used only when no theme dictionary answers (never in the running app).
    private static readonly Color Violet = Color.FromRgb(0xA6, 0x72, 0xFF);   // keywords
    private static readonly Color Cyan   = Color.FromRgb(0x4C, 0xDF, 0xFF);   // types
    private static readonly Color Mint   = Color.FromRgb(0x5C, 0xFF, 0xB0);   // strings
    private static readonly Color Amber  = Color.FromRgb(0xFF, 0xC6, 0x6D);   // numbers
    private static readonly Color Pink   = Color.FromRgb(0xFF, 0x5E, 0xC4);   // preprocessor / regex
    private static readonly Color Muted  = Color.FromRgb(0x7E, 0x8A, 0xA8);   // comments
    private static readonly Color Gray   = Color.FromRgb(0x80, 0x80, 0x80);   // /// and <summary> (XmlDoc)
    private static readonly Color Lemon  = Color.FromRgb(0xEC, 0xEF, 0x9A);   // functions / methods
    private static readonly Color Orchid = Color.FromRgb(0xFF, 0xB0, 0xF0);   // control flow
    private static readonly Color Sky    = Color.FromRgb(0x86, 0xB0, 0xFF);   // members / keys / variables
    private static readonly Color Night  = Color.FromRgb(0x06, 0x07, 0x1A);   // editor background

    /// <summary>The syntax colours of one theme, plus whether its code surface is light (which way an unknown
    /// colour must be pushed to stay readable).</summary>
    private readonly record struct Palette(Color Keyword, Color Type, Color String, Color Number,
                                           Color Preprocessor, Color Comment, Color DocComment,
                                           Color Function, Color Control, Color Variable, bool LightSurface)
    {
        public static Palette Current()
        {
            // The code surface: the editor's own background when the theme paints one, else the window behind it.
            var surface = ThemeService.ColorOf("Editor.Background", Colors.Transparent);
            if (surface.A < 0x80) surface = ThemeService.ColorOf("Bg.Window", Night);
            return new Palette(
                ThemeService.ColorOf("Syntax.Keyword", Violet),
                ThemeService.ColorOf("Syntax.Type", Cyan),
                ThemeService.ColorOf("Syntax.String", Mint),
                ThemeService.ColorOf("Syntax.Number", Amber),
                ThemeService.ColorOf("Syntax.Preprocessor", Pink),
                ThemeService.ColorOf("Syntax.Comment", Muted),
                ThemeService.ColorOf("Syntax.DocComment", Gray),
                ThemeService.ColorOf("Syntax.Function", Lemon),
                ThemeService.ColorOf("Syntax.Control", Orchid),
                ThemeService.ColorOf("Syntax.Variable", Sky),
                Luminance(surface) > 0.5);
        }
    }

    /// <summary>Resolve (and tint for the current theme) the definition for a file extension; null = plain text.</summary>
    public static IHighlightingDefinition? ForExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        string ext = extension.TrimStart('.').ToLowerInvariant();
        _ = Registered.Value;

        // Aliases the built-in manager doesn't know (it has no TypeScript definition — JavaScript is the closest
        // readable fallback), plus our bundled definitions. JSON goes to JSONC, not AvalonEdit's own "Json": that
        // one colours keys and values alike and knows no comments.
        string? name = ext switch
        {
            "cs" or "csx" => "C#",
            "js" or "jsx" or "ts" or "tsx" or "mjs" or "cjs" => "JavaScript",
            "json" or "jsonc" => "JSONC",
            "gd" => "GDScript",
            "gdshader" or "gdshaderinc" => "Godot Shader",
            "tscn" or "tres" or "godot" or "import" or "cfg" => "Godot Resource",
            "yml" or "yaml" => "YAML",
            "toml" => "TOML",
            "sh" or "bash" or "zsh" => "Shell",
            "dart" => "Dart",
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

        var palette = Palette.Current();
        EnsureTinted(def, palette);
        // C# and VB import AvalonEdit's XmlDoc definition for /// comments: those colours live in XmlDoc, not in
        // the language's own definition, so it is tinted alongside.
        var xmlDoc = HighlightingManager.Instance.GetDefinition("XmlDoc");
        if (xmlDoc != null && !ReferenceEquals(xmlDoc, def)) EnsureTinted(xmlDoc, palette);
        return def;
    }

    /// <summary>
    /// Load every <see cref="Bundled"/> definition from the assembly and register it with HighlightingManager.
    /// A definition that fails to load is skipped (its files open as plain text, as before) instead of taking
    /// the editor down — the loader validates the xshd against AvalonEdit's schema, so a typo fails here, and
    /// tests/syntaxtest catches it before a user does.
    /// </summary>
    private static bool RegisterBundled()
    {
        var assembly = typeof(NeonHighlighting).Assembly;
        foreach (var (file, name, extensions) in Bundled)
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream("CluadeX.Resources.Syntax." + file)
                                   ?? throw new InvalidOperationException("resource not embedded");
                using var reader = XmlReader.Create(stream);
                var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                HighlightingManager.Instance.RegisterHighlighting(name, extensions, definition);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NeonHighlighting] {file} not loaded: {ex.Message}");
            }
        }
        return true;
    }

    private static void EnsureTinted(IHighlightingDefinition def, Palette palette)
    {
        if (Tinted.TryGetValue(def.Name, out var tintedWith) && tintedWith == palette) return;
        Retint(def, palette);
        Tinted[def.Name] = palette;
    }

    private static void Retint(IHighlightingDefinition def, Palette palette)
    {
        foreach (var color in def.NamedHighlightingColors)
        {
            if (!Originals.TryGetValue(color, out var original))
                Originals[color] = original = color.Foreground;

            var themed = PickThemed(color.Name, palette);
            if (themed != null)
            {
                color.Foreground = new SimpleHighlightingBrush(themed.Value);
                continue;
            }

            // Unknown name: start from the xshd colour; if it is too close to the code surface (too dark on a
            // dark theme, too light on a light one), push it away so nothing renders as ink-on-ink.
            color.Foreground = original;
            var current = (original as SimpleHighlightingBrush)?.GetBrush(null) as SolidColorBrush;
            if (current == null) continue;
            var c = current.Color;
            double luminance = Luminance(c);
            if (!palette.LightSurface && luminance < 0.45)
            {
                static byte Lift(byte v) => (byte)Math.Min(255, v + 110);
                color.Foreground = new SimpleHighlightingBrush(Color.FromRgb(Lift(c.R), Lift(c.G), Lift(c.B)));
            }
            else if (palette.LightSurface && luminance > 0.6)
            {
                static byte Sink(byte v) => (byte)Math.Max(0, v - 110);
                color.Foreground = new SimpleHighlightingBrush(Color.FromRgb(Sink(c.R), Sink(c.G), Sink(c.B)));
            }
        }
    }

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    /// <summary>
    /// The theme colour for an xshd colour, chosen by its NAME (the only thing built-in and bundled definitions
    /// share); null = no category, keep the xshd colour. Order matters: "ControlKeyword" must be Control before
    /// the keyword test sees it, "MethodCall" Function before the type test. Function / Control / Variable are
    /// the VS Code split — calls in yellow, control flow apart from declarations, members and keys in light blue.
    /// </summary>
    private static Color? PickThemed(string colorName, Palette p)
    {
        string n = colorName.ToLowerInvariant();
        if (n == "doccomment") return p.DocComment;          // XmlDoc: the /// prefix and the <tags>
        if (n == "xmlstring") return null;                    // XmlDoc: "quoted" attribute — xshd silver, contrast-checked
        if (n.Contains("comment") || n.Contains("documentation")) return p.Comment;
        if (n.Contains("string") || n.Contains("char")) return p.String;
        if (n.Contains("number") || n.Contains("digit")) return p.Number;
        if (n.Contains("preprocessor") || n.Contains("regex") || n.Contains("annotation")) return p.Preprocessor;
        if (n.Contains("method") || n.Contains("function")) return p.Function;
        if (n.Contains("class") || n.Contains("type")
            || n.Contains("interface") || n.Contains("entity") || n.Contains("tagname")) return p.Type;
        if (n.Contains("variable") || n.Contains("property")) return p.Variable;
        if (n.Contains("control") || n.Contains("goto")) return p.Control;
        if (n.Contains("keyword") || n.Contains("modifier") || n.Contains("statement") || n.Contains("visibility")
            || n.Contains("truefalse") || n.Contains("this") || n.Contains("null") || n.Contains("operator")
            || n.Contains("reference") || n.Contains("valuetype") || n.Contains("namespace")
            || n.Contains("exception") || n.Contains("checked") || n.Contains("unsafe") || n.Contains("param")
            || n.Contains("getset") || n.Contains("contextkeyword") || n.Contains("attribute")) return p.Keyword;
        return null;
    }
}
