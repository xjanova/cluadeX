using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using CluadeX.Services.UiStudio.Pages;

namespace CluadeX.Services.UiStudio.Sketch;

/// <summary>A style as written: its setters (property → value), what it is based on, and its control template.</summary>
internal sealed class XStyle
{
    public string? Key { get; init; }
    public string? BasedOn { get; init; }
    public Dictionary<string, string> Setters { get; } = new(StringComparer.Ordinal);
    /// <summary>The ControlTemplate a Template setter gives (where an icon-only button keeps its glyph).</summary>
    public XEl? Template { get; set; }
    /// <summary>The keys of the style and the ones it is based on ("SecondaryButton BaseButton") — their names tell
    /// what kind of button or text it is.</summary>
    public string Chain { get; set; } = "";
    /// <summary>A trigger of the style sets Visibility="Visible" (a part collapsed by default, shown in some state).</summary>
    public bool ShownByTrigger { get; set; }
}

/// <summary>Resources by key: colours (brushes resolved to one ARGB), styles, data templates, strings.</summary>
internal sealed class XResources
{
    public Dictionary<string, uint> Colors { get; } = new(StringComparer.Ordinal);
    /// <summary>A brush whose colour is another resource ({StaticResource AccentColor}).</summary>
    public Dictionary<string, string> ColorAliases { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, XStyle> Styles { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, (XamlFile File, XEl El)> Templates { get; } = new(StringComparer.Ordinal);
    /// <summary>DataTemplates with a DataType and no key — what a ContentControl bound to a view model shows.</summary>
    public List<(XamlFile File, XEl El)> Implicit { get; } = new();
    public Dictionary<string, string> Strings { get; } = new(StringComparer.Ordinal);
}

/// <summary>One XAML file of the project: its root element as read on its own, and the whole tree once needed.</summary>
internal sealed class XamlFile
{
    private readonly object _gate = new();
    private XEl? _tree;
    private bool _parsed, _complete;
    private XResources? _local;

    public string Path { get; init; } = "";
    public string Text { get; init; } = "";
    public XEl Head { get; init; } = null!;
    /// <summary>x:Class ("App.Views.ChatView"), null for a plain dictionary.</summary>
    public string? Class => Head.A("x:Class");
    public string ShortClass => Class is { } c ? c[(c.LastIndexOf('.') + 1)..] : System.IO.Path.GetFileNameWithoutExtension(Path);
    /// <summary>"window", "view" (UserControl / Page), "app", "dictionary" or "control" (another root with an x:Class).</summary>
    public string Kind { get; init; } = "";
    public bool IsScreen => Kind is "window" or "view";

    public XEl? Tree
    {
        get
        {
            lock (_gate)
            {
                if (!_parsed) { _tree = XamlTree.Parse(Text, out _complete); _parsed = true; }
                return _tree;
            }
        }
    }

    /// <summary>The file was read to its end (a half-written file still draws what came before the error).</summary>
    public bool Complete { get { _ = Tree; return _complete; } }

    private Dictionary<string, XEl>? _names;

    /// <summary>An element of the file by its x:Name (what an ElementName binding points at).</summary>
    public XEl? Named(string name)
    {
        var tree = Tree;
        lock (_gate)
        {
            if (_names == null)
            {
                _names = new Dictionary<string, XEl>(StringComparer.Ordinal);
                if (tree != null)
                    foreach (var d in tree.Descendants())
                        if ((d.A("x:Name") ?? d.A("Name")) is { Length: > 0 } n) _names.TryAdd(n, d);
            }
            return _names.TryGetValue(name, out var el) ? el : null;
        }
    }

    /// <summary>The resources the file declares itself (&lt;UserControl.Resources&gt; and any nested .Resources).</summary>
    public XResources Local(WpfProject p)
    {
        lock (_gate)
        {
            if (_local != null) return _local;
        }
        var res = new XResources();
        if (Tree is { } root)
            foreach (var r in root.Descendants().Where(d => d.IsProperty && d.Property == "Resources"))
                p.IndexResources(r, this, res, overwrite: false, depth: 0);
        lock (_gate) _local ??= res;
        return _local;
    }
}

/// <summary>
/// A WPF project read for the UI Studio: every XAML view (Window, UserControl, Page), the resources the app merges
/// (theme brushes, styles, data templates), and its translations — what's needed to list its views and draw each one
/// as a wireframe without running anything. Desktop windows can't be shown in the Studio's browser, so like Flutter
/// screens they are simulated from the source, and a click on a block opens its XAML line.
/// </summary>
public sealed class WpfProject : IScreenSketcher
{
    public string Root { get; }
    public string Language { get; }
    public SketchTheme Theme { get; private set; } = new();
    public bool Truncated { get; private set; }

    internal List<XamlFile> Files { get; } = new();
    internal XResources Global { get; } = new();
    private readonly Dictionary<string, XamlFile> _byClass = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<XamlFile>> _byShort = new(StringComparer.Ordinal);
    /// <summary>Translations the project writes itself (CluadeX-style key files), for keys the running app doesn't know.</summary>
    private readonly Dictionary<string, string> _projectLoc = new(StringComparer.Ordinal);
    /// <summary>.resx strings in the UI language (over the neutral ones).</summary>
    private readonly Dictionary<string, string> _resx = new(StringComparer.Ordinal);
    private readonly List<string> _projectDirs = new();
    private string? _startupUri;

    private WpfProject(string root, string language)
    {
        Root = root;
        Language = language;
    }

    // ───────────────────────────── finding the project ─────────────────────────────

    private static readonly Regex SlnProject = new(@"Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""(?<p>[^""]+\.csproj)""", RegexOptions.Compiled);

    /// <summary>The folders of the WPF projects here: a .csproj at the root, else the projects a solution at the root
    /// lists, else a .csproj one folder down. Cheap — it reads a few small files.</summary>
    public static List<string> ProjectDirs(string root)
    {
        var dirs = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return dirs;
        string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (FilesIn(root, "*.csproj").Any(IsWpfProject)) dirs.Add(root);
        if (dirs.Count == 0)
            foreach (var sln in FilesIn(root, "*.sln"))
                foreach (Match m in SlnProject.Matches(WebPages.SafeRead(sln, 2_000_000)))
                {
                    string proj;
                    try { proj = Path.GetFullPath(Path.Combine(root, m.Groups["p"].Value.Replace('\\', Path.DirectorySeparatorChar))); }
                    catch { continue; }
                    if (proj.StartsWith(full, StringComparison.OrdinalIgnoreCase) && File.Exists(proj) && IsWpfProject(proj)) dirs.Add(Path.GetDirectoryName(proj)!);
                }
        if (dirs.Count == 0)
        {
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(root).ToList(); } catch { subs = Array.Empty<string>(); }
            foreach (var d in subs)
            {
                string name = Path.GetFileName(d);
                if (name.StartsWith('.') || SourceSearch.SkipDirs.Contains(name)) continue;
                if (FilesIn(d, "*.csproj").Any(IsWpfProject)) dirs.Add(d);
            }
        }
        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> FilesIn(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>An SDK project with UseWPF, or an old-style one referencing PresentationFramework / the WPF project type.</summary>
    internal static bool IsWpfProject(string csproj)
    {
        string t = WebPages.SafeRead(csproj, 1_000_000);
        return Regex.IsMatch(t, @"<UseWPF>\s*true\s*</UseWPF>", RegexOptions.IgnoreCase)
               || t.Contains("PresentationFramework", StringComparison.OrdinalIgnoreCase)
               || t.Contains("60dc8134-eba5-43b8-bcc9-bb4bc16c2548", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWpf(string root) => ProjectDirs(root).Count > 0;

    // ───────────────────────────── loading ─────────────────────────────

    public static WpfProject Load(string root, string language, CancellationToken ct = default, int maxFiles = 1500)
    {
        var p = new WpfProject(root, language);
        p._projectDirs.AddRange(ProjectDirs(root));
        var xaml = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".xaml" };
        int count = 0;
        foreach (var dir in p._projectDirs)
            foreach (var f in SourceSearch.ProjectFiles(dir, xaml, maxFiles + 1))
            {
                ct.ThrowIfCancellationRequested();
                if (++count > maxFiles) { p.Truncated = true; break; }
                string text = WebPages.SafeRead(f, 3_000_000);
                if (text.Length == 0 || XamlTree.ReadRoot(text) is not { } head) continue;
                var file = new XamlFile { Path = f, Text = text, Head = head, Kind = KindOf(head) };
                p.Files.Add(file);
                if (file.Class is { } cls)
                {
                    p._byClass.TryAdd(cls, file);
                    if (!p._byShort.TryGetValue(file.ShortClass, out var list)) p._byShort[file.ShortClass] = list = new List<XamlFile>();
                    list.Add(file);
                }
            }
        ct.ThrowIfCancellationRequested();
        p.LoadResources();
        p.LoadTranslations(ct);
        p.Theme = p.ReadTheme();
        return p;
    }

    private static string KindOf(XEl head)
    {
        string n = head.Name;
        if (n == "Application") return "app";
        if (n == "ResourceDictionary") return "dictionary";
        if (head.A("x:Class") == null) return "other";
        if (n.EndsWith("Window", StringComparison.Ordinal)) return "window";
        if (n is "UserControl" or "Page" || n.EndsWith("UserControl", StringComparison.Ordinal) || n.EndsWith("Page", StringComparison.Ordinal)) return "view";
        return "control";
    }

    internal XamlFile? FileOfClass(string fullName) => _byClass.TryGetValue(fullName, out var f) ? f : null;

    /// <summary>The project's XAML control an element names: by its clr-namespace, else by its name when only one
    /// class of the project has it.</summary>
    internal XamlFile? ControlOf(XEl el)
    {
        if (el.IsText || el.IsProperty) return null;
        if (XamlTree.ClrNamespace(el.Ns) is { } ns && FileOfClass(ns + "." + el.Name) is { } exact) return exact;
        if (el.Ns.StartsWith("http://schemas.microsoft.com/", StringComparison.Ordinal)) return null;   // WPF's own
        return _byShort.TryGetValue(el.Name, out var list) && list.Count == 1 ? list[0] : null;
    }

    // ───────────────────────────── resources ─────────────────────────────

    /// <summary>App.xaml and what it merges come first (later merged dictionaries win over earlier ones, as in WPF);
    /// the other dictionaries of the project only fill in keys still missing (a theme the app doesn't load is never
    /// the one drawn).</summary>
    private void LoadResources()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in Files.Where(f => f.Kind == "app"))
        {
            visited.Add(app.Path);
            _startupUri ??= app.Head.A("StartupUri");
            if (app.Tree?.Prop("Resources") is { } res) IndexResources(res, app, Global, overwrite: true, depth: 0, visited);
        }
        var rest = new XResources();
        foreach (var dict in Files.Where(f => f.Kind == "dictionary" && !visited.Contains(f.Path)).ToList())
            if (dict.Tree is { } root) IndexResources(root, dict, rest, overwrite: false, depth: 0, visited);
        Merge(rest, Global);
    }

    private static void Merge(XResources from, XResources into)
    {
        // a key is a colour OR an alias: one the app already has (either way) is never replaced
        foreach (var (k, v) in from.Colors) if (!into.ColorAliases.ContainsKey(k)) into.Colors.TryAdd(k, v);
        foreach (var (k, v) in from.ColorAliases) if (!into.Colors.ContainsKey(k)) into.ColorAliases.TryAdd(k, v);
        foreach (var (k, v) in from.Styles) into.Styles.TryAdd(k, v);
        foreach (var (k, v) in from.Templates) into.Templates.TryAdd(k, v);
        foreach (var (k, v) in from.Strings) into.Strings.TryAdd(k, v);
    }

    internal void IndexResources(XEl container, XamlFile file, XResources into, bool overwrite, int depth, HashSet<string>? visited = null)
    {
        if (depth > 6) return;
        void Put<T>(Dictionary<string, T> d, string key, T v) { if (overwrite) d[key] = v; else d.TryAdd(key, v); }
        // a brush key holds a colour or an alias of another key, never both: a later dictionary replaces either
        void PutColor(string key, uint v)
        {
            if (overwrite) { into.Colors[key] = v; into.ColorAliases.Remove(key); }
            else if (!into.ColorAliases.ContainsKey(key)) into.Colors.TryAdd(key, v);
        }
        void PutAlias(string key, string of)
        {
            if (overwrite) { into.ColorAliases[key] = of; into.Colors.Remove(key); }
            else if (!into.Colors.ContainsKey(key)) into.ColorAliases.TryAdd(key, of);
        }
        foreach (var r in container.Elements)
        {
            if (r.IsProperty)
            {
                if (r.Property == "MergedDictionaries")
                    foreach (var md in r.Elements)
                        if (md.A("Source") is { } src && ResolveSource(src, file) is { } merged && (visited == null || visited.Add(merged.Path)) && merged.Tree is { } mroot)
                            IndexResources(mroot, merged, into, overwrite, depth + 1, visited);
                continue;
            }
            if (r.Name == "ResourceDictionary")
            {
                if (r.A("Source") is { } src && ResolveSource(src, file) is { } merged && (visited == null || visited.Add(merged.Path)) && merged.Tree is { } mroot)
                    IndexResources(mroot, merged, into, overwrite, depth + 1, visited);
                IndexResources(r, file, into, overwrite, depth + 1, visited);
                continue;
            }
            string? key = r.A("x:Key");
            switch (r.Name)
            {
                case "SolidColorBrush" when key != null:
                {
                    string? c = r.A("Color");
                    double opacity = XamlTree.Number(r.A("Opacity")) ?? 1;
                    if (ParseColor(c) is { } argb) PutColor(key, WithOpacity(argb, opacity));
                    else if (XamlTree.Ext(c) is { Arg.Length: > 0 } ext) PutAlias(key, ext.Arg);
                    else if (r.Prop("Color")?.Descendants().FirstOrDefault(k => k.IsText)?.Text is { } inner && ParseColor(inner) is { } ia)
                        PutColor(key, ia);
                    break;
                }
                case "Color" when key != null:
                    if (ParseColor(r.Kids.FirstOrDefault(k => k.IsText)?.Text) is { } col) PutColor(key, col);
                    break;
                case "LinearGradientBrush" or "RadialGradientBrush" when key != null:
                {
                    var stop = r.Descendants().FirstOrDefault(d => d.Name == "GradientStop");
                    if (ParseColor(stop?.A("Color")) is { } g) PutColor(key, g);
                    else if (XamlTree.Ext(stop?.A("Color")) is { Arg.Length: > 0 } ext) PutAlias(key, ext.Arg);
                    break;
                }
                case "Style":
                {
                    var st = ReadStyle(r);
                    if (key != null) Put(into.Styles, key, st);
                    break;
                }
                case "DataTemplate" or "HierarchicalDataTemplate" or "ItemContainerTemplate":
                    if (key != null) Put(into.Templates, key, (file, r));
                    else if (r.A("DataType") != null) into.Implicit.Add((file, r));
                    break;
                case "String" when key != null:
                    Put(into.Strings, key, r.Kids.FirstOrDefault(k => k.IsText)?.Text?.Trim() ?? "");
                    break;
            }
        }
    }

    internal static XStyle ReadStyle(XEl r)
    {
        var st = new XStyle
        {
            Key = r.A("x:Key"), BasedOn = XamlTree.Ext(r.A("BasedOn"))?.Arg,
            ShownByTrigger = r.Prop("Triggers")?.Descendants().Any(d => d.Name == "Setter" && d.A("Property") == "Visibility" && d.A("Value") == "Visible") == true,
        };
        foreach (var s in r.Elements.Where(e => e.Name == "Setter"))
        {
            if (s.A("Property") is not { } prop) continue;
            if (s.A("Value") is { } v) st.Setters.TryAdd(prop, v);
            else if (prop == "Template" && s.Prop("Value")?.Elements.FirstOrDefault(e => e.Name == "ControlTemplate") is { } tpl) st.Template = tpl;
        }
        return st;
    }

    /// <summary>A merged dictionary's Source as a file of this project ("Themes/Dark.xaml",
    /// "/App;component/Themes/Dark.xaml", "pack://application:,,,/App;component/Themes/Dark.xaml").</summary>
    private XamlFile? ResolveSource(string src, XamlFile from)
    {
        string s = src.Trim();
        int comp = s.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
        bool fromProject = comp >= 0 || s.StartsWith('/');
        if (comp >= 0) s = s[(comp + ";component/".Length)..];
        s = s.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var candidates = new List<string>();
        if (!fromProject) candidates.Add(Path.Combine(Path.GetDirectoryName(from.Path)!, s));
        foreach (var d in _projectDirs) candidates.Add(Path.Combine(d, s));
        foreach (var c in candidates)
        {
            string full;
            try { full = Path.GetFullPath(c); } catch { continue; }
            if (Files.FirstOrDefault(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit;
        }
        return null;
    }

    /// <summary>A style by key: the file's own resources first, then the app's.</summary>
    internal XStyle? StyleOf(string key, XamlFile file) =>
        file.Local(this).Styles.TryGetValue(key, out var s) ? s : Global.Styles.TryGetValue(key, out var g) ? g : null;

    internal (XamlFile File, XEl El)? TemplateOf(string key, XamlFile file) =>
        file.Local(this).Templates.TryGetValue(key, out var t) ? t : Global.Templates.TryGetValue(key, out var g) ? g : null;

    /// <summary>A colour resource by key (its aliases followed).</summary>
    internal uint? ColorOf(string key, XamlFile? file, int depth = 0)
    {
        if (depth > 6) return null;
        // the file's own resources first (a colour or an alias), then the app's
        if (file?.Local(this) is { } local)
        {
            if (local.Colors.TryGetValue(key, out var lc)) return lc;
            if (local.ColorAliases.TryGetValue(key, out var la)) return ColorOf(la, file, depth + 1);
        }
        if (Global.Colors.TryGetValue(key, out var gc)) return gc;
        return Global.ColorAliases.TryGetValue(key, out var ga) ? ColorOf(ga, file, depth + 1) : null;
    }

    /// <summary>A brush value as one colour: a literal (#AARRGGBB, a colour name) or a resource; null for
    /// Transparent, {x:Null}, a binding or anything it can't tell.</summary>
    internal uint? BrushColor(string? value, XamlFile? file)
    {
        if (value == null) return null;
        if (XamlTree.Ext(value) is { } ext)
            return ext.Short is "StaticResource" or "DynamicResource" or "ThemeResource" && ext.Arg.Length > 0 ? ColorOf(ext.Arg, file) : null;
        return ParseColor(value);
    }

    internal static uint? ParseColor(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        string s = v.Trim();
        if (s.StartsWith('#'))
        {
            string h = s[1..];
            if (!Regex.IsMatch(h, "^[0-9A-Fa-f]+$")) return null;
            if (h.Length == 3) h = "FF" + string.Concat(h.Select(c => $"{c}{c}"));
            else if (h.Length == 4) h = string.Concat(h.Select(c => $"{c}{c}"));
            else if (h.Length == 6) h = "FF" + h;
            if (h.Length != 8) return null;
            return Convert.ToUInt32(h, 16);
        }
        if (s.Equals("Transparent", StringComparison.OrdinalIgnoreCase)) return 0x00FFFFFF;
        try
        {
            if (System.Windows.Media.ColorConverter.ConvertFromString(s) is System.Windows.Media.Color c)
                return (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);
        }
        catch { /* not a colour name */ }
        return null;
    }

    private static uint WithOpacity(uint argb, double opacity)
    {
        if (opacity >= 1) return argb;
        uint a = (uint)Math.Round(((argb >> 24) & 0xFF) * Math.Clamp(opacity, 0, 1));
        return (a << 24) | (argb & 0xFFFFFF);
    }

    /// <summary>A colour laid over a background (its alpha blended away) — what the eye sees.</summary>
    internal static uint Over(uint background, uint color)
    {
        double a = ((color >> 24) & 0xFF) / 255.0;
        return FlutterProject.Mix(background | 0xFF000000, color | 0xFF000000, a);
    }

    // ───────────────────────────── colours of the app ─────────────────────────────

    private uint? FirstColor(params string[] keys)
    {
        foreach (var k in keys)
            if (ColorOf(k, null) is { } c) return c;
        return null;
    }

    private uint? MatchColor(string pattern, Func<uint, bool>? ok = null)
    {
        var rx = new Regex(pattern, RegexOptions.IgnoreCase);
        foreach (var k in Global.Colors.Keys.Concat(Global.ColorAliases.Keys))
            if (rx.IsMatch(k) && ColorOf(k, null) is { } c && (ok == null || ok(c))) return c;
        return null;
    }

    private SketchTheme ReadTheme()
    {
        static bool Opaque(uint c) => (c >> 24) >= 0xC0;
        uint? bg = FirstColor("Page.Background", "Bg.Window", "Window.Background", "WindowBackground", "AppBackground", "ApplicationBackground",
                       "BackgroundBrush", "Background", "Bg")
                   ?? MatchColor(@"^(window|page|app|main|application)\.?(background|bg)", Opaque) ?? MatchColor(@"background", Opaque);
        uint accent = FirstColor("Accent.1", "Accent", "AccentBrush", "AccentColor", "Primary", "PrimaryBrush", "PrimaryColor", "Brand", "BrandBrush", "SystemAccentColor")
                      ?? MatchColor(@"accent|primary|brand", c => (c >> 24) >= 0x80) ?? 0xFF6750A4;
        accent |= 0xFF000000;
        if (bg is not { } b) return new SketchTheme { Accent = accent };
        uint background = Over(0xFFFFFFFF, b);
        bool dark = FlutterProject.Luma(background) < 0.45;
        var theme = dark ? SketchTheme.DarkDefault(accent) : new SketchTheme { Accent = accent };
        theme.Background = background;
        theme.Dark = dark;
        theme.OnBackground = dark ? 0xFFE6E0E9 : 0xFF1C1B1F;
        theme.Surface = FlutterProject.Mix(background, dark ? 0xFFFFFFFF : 0xFF000000, 0.07);
        if ((FirstColor("Text.Primary", "TextPrimary", "Foreground", "ForegroundBrush", "TextBrush", "TextColor", "Text") ?? MatchColor(@"^text|foreground")) is { } fg
            && Math.Abs(FlutterProject.Luma(Over(background, fg)) - FlutterProject.Luma(background)) > 0.4)
            theme.OnBackground = Over(background, fg);
        if ((FirstColor("Surface.Card", "Surface", "Card.Background", "CardBackground", "Surface.Panel", "Panel.Background") ?? MatchColor(@"surface|card")) is { } surface)
        {
            uint s = Over(background, surface);
            if (Math.Abs(FlutterProject.Luma(s) - FlutterProject.Luma(background)) < 0.4 && s != background) theme.Surface = s;
        }
        return theme;
    }

    /// <summary>The colours of one screen: the app's, on the screen's own background when it sets one (a dialog on
    /// a darker card, a light settings page).</summary>
    internal SketchTheme ThemeFor(XEl root, XamlFile file)
    {
        uint? Bg(XEl e)
        {
            if (BrushColor(e.A("Background"), file) is { } c && (c >> 24) >= 0x80) return c;
            return null;
        }
        uint? bg = Bg(root);
        // a borderless window draws its own background on its first panel or border
        for (var e = root.Elements.FirstOrDefault(k => !k.IsProperty); bg == null && e != null; e = e.Elements.FirstOrDefault(k => !k.IsProperty))
            if ((bg = Bg(e)) != null || e.Name is not ("Border" or "Grid" or "DockPanel" or "StackPanel" or "AdornerDecorator" or "ScrollViewer" or "Viewbox")) break;
        if (bg is not { } b) return Theme;
        uint background = Over(Theme.Background, b);
        if (background == Theme.Background) return Theme;
        bool dark = FlutterProject.Luma(background) < 0.45;
        return new SketchTheme
        {
            Accent = Theme.Accent, Background = background, Dark = dark,
            OnBackground = Math.Abs(FlutterProject.Luma(Theme.OnBackground) - FlutterProject.Luma(background)) > 0.4 ? Theme.OnBackground : dark ? 0xFFE6E0E9 : 0xFF1C1B1F,
            Surface = Math.Abs(FlutterProject.Luma(Theme.Surface) - FlutterProject.Luma(background)) < 0.3 && Theme.Surface != background
                ? Theme.Surface : FlutterProject.Mix(background, dark ? 0xFFFFFFFF : 0xFF000000, 0.07),
        };
    }

    // ───────────────────────────── translations ─────────────────────────────

    // ["key"] = E("English", "ไทย")   and   ["key"] = new() { ["en"] = "English", ["th"] = "ไทย" }
    private static readonly Regex KeyE = new(@"\[""(?<k>[\w.\-]+)""\]\s*=\s*E\(\s*""(?<en>(?:[^""\\]|\\.)*)""\s*,\s*""(?<th>(?:[^""\\]|\\.)*)""\s*\)", RegexOptions.Compiled);
    private static readonly Regex KeyNew = new(@"\[""(?<k>[\w.\-]+)""\]\s*=\s*new(?:\s*\(\))?\s*\{\s*\[""en""\]\s*=\s*""(?<en>(?:[^""\\]|\\.)*)""\s*,\s*\[""th""\]\s*=\s*""(?<th>(?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    private void LoadTranslations(CancellationToken ct)
    {
        var cs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" };
        var resx = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".resx" };
        foreach (var dir in _projectDirs)
        {
            // the project's own key files (a CluadeX checkout that has keys newer than the running app)
            foreach (var f in SourceSearch.ProjectFiles(dir, cs, 3000))
            {
                ct.ThrowIfCancellationRequested();
                string name = Path.GetFileName(f);
                if (!f.Contains("Locali", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("Keys.cs", StringComparison.Ordinal)) continue;
                string text = WebPages.SafeRead(f, 3_000_000);
                foreach (var rx in new[] { KeyE, KeyNew })
                    foreach (Match m in rx.Matches(text))
                        _projectLoc.TryAdd(m.Groups["k"].Value, Unescape(m.Groups[Language == "th" ? "th" : "en"].Value));
            }
            // .resx: neutral first, then the UI language over it (Strings.resx, Strings.th.resx / Strings.th-TH.resx)
            var files = SourceSearch.ProjectFiles(dir, resx, 400).ToList();
            foreach (var f in files.Where(f => !Regex.IsMatch(Path.GetFileName(f), @"\.[a-z]{2}(-[A-Za-z]{2,4})?\.resx$")))
                ReadResx(f, overwrite: false);
            foreach (var f in files.Where(f => Regex.IsMatch(Path.GetFileName(f), @"\." + Regex.Escape(Language) + @"(-[A-Za-z]{2,4})?\.resx$", RegexOptions.IgnoreCase)))
                ReadResx(f, overwrite: true);
        }
    }

    private void ReadResx(string path, bool overwrite)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        try
        {
            if (new FileInfo(path).Length > 4_000_000) return;
            using var r = XmlReader.Create(path, settings);
            string? name = null;
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element) continue;
                if (r.LocalName == "data") name = r.GetAttribute("type") == null ? r.GetAttribute("name") : null;
                else if (r.LocalName == "value" && name != null)
                {
                    string v = r.ReadElementContentAsString();
                    if (overwrite) _resx[name] = v; else _resx.TryAdd(name, v);
                    name = null;
                }
            }
        }
        catch { /* a broken .resx: its strings stay unresolved */ }
    }

    private static string Unescape(string s) => s.Replace("\\\"", "\"").Replace("\\n", " ").Replace("\\t", " ").Replace("\\\\", "\\");

    /// <summary>A {services:Loc key} text in the UI language: the running app's translations (LocalizationService),
    /// then the project's own key files, then its .resx strings. Null when nobody knows the key.</summary>
    internal string? Loc(string key)
    {
        if (key.Length == 0) return null;
        return LocalizationService.Lookup(key, Language)
               ?? (_projectLoc.TryGetValue(key, out var p) ? p : null)
               ?? Resx(key);
    }

    /// <summary>A .resx string ({x:Static p:Resources.Title}, {lex:Loc App:Strings:Title}) — by its last part.</summary>
    internal string? Resx(string reference)
    {
        string k = reference;
        int cut = Math.Max(k.LastIndexOf('.'), k.LastIndexOf(':'));
        if (cut >= 0) k = k[(cut + 1)..];
        return _resx.TryGetValue(k, out var v) ? v : null;
    }

    // ───────────────────────────── pages ─────────────────────────────

    /// <summary>The main window (App.xaml's StartupUri, else the window called MainWindow, else the first window).</summary>
    internal XamlFile? MainWindow()
    {
        var windows = Files.Where(f => f.Kind == "window").ToList();
        if (_startupUri is { Length: > 0 } uri)
        {
            string tail = uri.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            if (windows.FirstOrDefault(w => w.Path.EndsWith(Path.DirectorySeparatorChar + tail, StringComparison.OrdinalIgnoreCase)) is { } s) return s;
        }
        return windows.FirstOrDefault(w => w.ShortClass == "MainWindow") ?? windows.OrderBy(w => w.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    /// <summary>One page per view: the main window first, then the views it shows (its data templates and the
    /// project controls it places), then every other view and window.</summary>
    public List<StudioPage> Pages()
    {
        var screens = Files.Where(f => f.IsScreen).ToList();
        var ordered = new List<XamlFile>();
        var main = MainWindow();
        if (main != null)
        {
            ordered.Add(main);
            if (main.Tree is { } mt)
                foreach (var el in mt.Descendants())
                    if (ControlOf(el) is { IsScreen: true } shown && !ordered.Contains(shown)) ordered.Add(shown);
        }
        foreach (var f in screens.Where(f => f.Kind == "view").OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)) if (!ordered.Contains(f)) ordered.Add(f);
        foreach (var f in screens.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)) if (!ordered.Contains(f)) ordered.Add(f);

        var pages = new List<StudioPage>();
        foreach (var f in ordered)
        {
            pages.Add(new StudioPage
            {
                Id = "screen:" + (f.Class ?? f.Path), Kind = StudioPageKind.Screen, Route = null,
                Title = f.Kind == "window" && WindowTitle(f.Head) is { } t ? t : WebPages.Humanize(f.ShortClass),
                SourcePath = f.Path, SourceLine = f.Head.Line, Detail = f.ShortClass, Group = f.Kind,
            });
        }
        return pages;
    }

    /// <summary>A window's Title as the user reads it (a literal or a translation key; null when bound).</summary>
    internal string? WindowTitle(XEl head)
    {
        string? title = head.A("Title");
        if (title == null) return null;
        if (XamlTree.Literal(title) is { Length: > 0 } lit) return lit.Trim();
        var ext = XamlTree.Ext(title);
        if (ext?.Short is "Loc" && Loc(ext.Arg) is { Length: > 0 } loc) return loc;
        if (ext?.Name is "x:Static" && Resx(ext.Arg) is { Length: > 0 } rs) return rs;
        return null;
    }

    /// <summary>Draw a view as a wireframe (null when it is not one of this project's views).</summary>
    public ScreenSketch? Sketch(StudioPage page)
    {
        var file = Files.FirstOrDefault(f => f.IsScreen && string.Equals(f.Path, page.SourcePath, StringComparison.OrdinalIgnoreCase));
        if (file == null) return null;
        return new XamlSketcher(this).Screen(file);
    }
}
