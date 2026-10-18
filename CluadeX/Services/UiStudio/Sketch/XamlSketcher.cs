using System.Text.RegularExpressions;
using CluadeX.Services.UiStudio.Pages;

namespace CluadeX.Services.UiStudio.Sketch;

/// <summary>
/// Turns a XAML view into a wireframe. WPF's controls map to blocks (TextBlock → text, Button → button, TextBox →
/// input, ListBox → a few rows of its item template, TabControl → tabs, a Border with a background → card, a Grid's
/// columns → a row); the project's own UserControls are drawn from THEIR XAML, remembering where the screen uses
/// them. Text comes through the app's translations ({services:Loc key}), a bound value shows as "…", and parts the
/// app shows only while loading or empty are left out. Every block keeps its file:line, so a click opens it.
/// </summary>
internal sealed class XamlSketcher
{
    private readonly WpfProject _p;
    private int _nodes, _unknown;
    private const int MaxNodes = 900;
    private const int MaxDepth = 6;
    private XamlFile _screen = null!;

    public XamlSketcher(WpfProject project) => _p = project;

    private sealed class Env
    {
        public XamlFile File = null!;
        public int Depth;
        public HashSet<string> Expanding = new(StringComparer.Ordinal);
        /// <summary>Where the screen uses the control being drawn (a block inside it opens there too).</summary>
        public (string File, int Line)? Use;
        /// <summary>DataTemplates by data type in scope: what a content host bound to a view model shows.</summary>
        public List<(XamlFile File, XEl El)> Implicit = new();
        /// <summary>Drawing a list's item template: the item's usual content, not every state it can show.</summary>
        public bool InTemplate;
    }

    // ───────────────────────────── entry ─────────────────────────────

    public ScreenSketch Screen(XamlFile f)
    {
        _screen = f;
        var env = new Env { File = f };
        env.Expanding.Add(f.Class ?? f.Path);
        bool window = f.Kind == "window";
        var root = f.Tree;
        SketchNode top;
        if (root == null) top = new SketchNode { Kind = SketchKind.Column, Widget = f.Head.Name, File = f.Path, Line = f.Head.Line };
        else
        {
            env.Implicit.AddRange(f.Local(_p).Implicit);
            env.Implicit.AddRange(_p.Global.Implicit);
            top = Wrap(SketchKind.Column, Panel(root, env), root, env, root.Name);
        }
        var (w, h) = FrameSize(f.Head, window);
        string? title = window ? _p.WindowTitle(f.Head) : null;
        // a window with the system's title bar gets one drawn; a borderless one draws its own
        bool chrome = window && !string.Equals(f.Head.A("WindowStyle"), "None", StringComparison.OrdinalIgnoreCase);
        return new ScreenSketch
        {
            Root = top, Theme = root != null ? _p.ThemeFor(root, f) : _p.Theme, Title = title, Unknown = _unknown,
            Desktop = true, Width = w, Height = h, Caption = chrome ? title ?? WebPages.Humanize(f.ShortClass) : null,
        };
    }

    /// <summary>The view's size: its Width/Height (a SizeToContent window grows with its content: a landscape frame
    /// shows its top), the designer's d:DesignWidth/Height, else the page area of a usual desktop window.</summary>
    private static (double W, double H) FrameSize(XEl head, bool window)
    {
        double? w = XamlTree.Number(head.A("Width")) ?? XamlTree.Number(head.A("d:DesignWidth"));
        bool fitsHeight = (head.A("SizeToContent") ?? "").Contains("Height", StringComparison.OrdinalIgnoreCase);
        double? h = fitsHeight ? null : XamlTree.Number(head.A("Height")) ?? XamlTree.Number(head.A("d:DesignHeight"));
        double width = Math.Clamp(w ?? (window ? 1000 : 1200), 360, 1440);
        double height = h is > 0 ? h.Value : width * (window ? 0.68 : 0.635);
        return (width, Math.Clamp(height, 240, 1000));
    }

    // ───────────────────────────── children ─────────────────────────────

    private static readonly HashSet<string> ContentProps = new(StringComparer.Ordinal) { "Content", "Child", "Children", "Items", "Inlines", "Blocks", "Document" };

    /// <summary>What an element contains: its child elements and text, and the children of a content property
    /// element (&lt;Border.Child&gt;) — never its resources, styles, templates or triggers.</summary>
    private static IEnumerable<XEl> ContentKids(XEl el)
    {
        foreach (var k in el.Kids)
        {
            if (!k.IsProperty) { yield return k; continue; }
            if (ContentProps.Contains(k.Property))
                foreach (var kk in k.Kids) yield return kk;
        }
    }

    /// <summary>The blocks of an element's children, the ones the app hides at first left out.</summary>
    private List<SketchNode> Panel(XEl el, Env env)
    {
        var list = new List<SketchNode>();
        foreach (var k in ContentKids(el))
        {
            if (_nodes > MaxNodes) break;
            if (Hidden(k, env)) continue;
            list.AddRange(Nodes(k, env));
        }
        return list;
    }

    /// <summary>Not drawn: Visibility="Collapsed"/"Hidden"; a binding whose FallbackValue is Collapsed (what WPF shows
    /// with no data); a part shown only for a while (loading, empty, failed, updating…); a style's default Collapsed
    /// that no trigger ever shows. In a list's item template every part shown by a binding or a trigger is a state of
    /// the item (a thinking block, a confirm card): a few rows of the item's usual content read better.</summary>
    private bool Hidden(XEl k, Env env, int depth = 0)
    {
        if (k.IsText || depth > 4) return false;
        if (k.A("Visibility") is { } v)
        {
            string t = v.Trim();
            if (t is "Collapsed" or "Hidden") return true;
            var ext = XamlTree.Ext(t);
            if (ext?.Short != "Binding") return false;
            if (ext.Named.TryGetValue("FallbackValue", out var fallback) && fallback.Trim() is "Collapsed" or "Hidden") return true;
            // shown while another element is checked, expanded, hovered or open: as the screen first appears,
            // only when that element says so itself (IsChecked="True"); following another element's Visibility
            string prop = ext.Arg.Contains('.') ? ext.Arg[(ext.Arg.LastIndexOf('.') + 1)..] : ext.Arg;
            bool other = ext.Named.ContainsKey("ElementName") || ext.Named.ContainsKey("RelativeSource");
            if (other && prop == "Visibility" && ext.Named.TryGetValue("ElementName", out var source) && env.File.Named(source) is { } followed)
                return Hidden(followed, env, depth + 1);
            if (other && Regex.IsMatch(prop, "^(IsChecked|IsExpanded|IsMouseOver|IsPressed|IsFocused|IsKeyboardFocused|IsKeyboardFocusWithin|IsSelected|IsOpen|IsDropDownOpen|IsSubmenuOpen|IsDragging)$"))
            {
                bool shown = ext.Named.TryGetValue("ElementName", out var name) && env.File.Named(name)?.A(prop) == "True";
                bool invert = Regex.IsMatch((ext.Named.TryGetValue("ConverterParameter", out var cp) ? cp : "") + " " + (ext.Named.TryGetValue("Converter", out var cv) ? cv : ""),
                    @"(?i)invert|inverse|negat|\bnot\b|reverse|false");
                return shown == invert;
            }
            return env.InTemplate || VisibilityPreference(t) < 0;
        }
        if (StyleOf(k, env) is { } st && st.Setters.TryGetValue("Visibility", out var sv) && sv.Trim() is "Collapsed" or "Hidden")
            return env.InTemplate || !st.ShownByTrigger;
        return false;
    }

    /// <summary>For a bound Visibility: +1 shown with the content, -1 shown only for a while (loading, empty, failed,
    /// updating, searching, an open popup, a banner), 0 can't tell. A screen is drawn as it sits idle with its content.</summary>
    internal static int VisibilityPreference(string v)
    {
        var ext = XamlTree.Ext(v);
        if (ext == null || ext.Short != "Binding") return 0;
        string path = ext.Arg;
        string conv = ext.Named.TryGetValue("Converter", out var c) ? c : "";
        string param = ext.Named.TryGetValue("ConverterParameter", out var cp) ? cp : "";
        bool invert = Regex.IsMatch(param + " " + conv, @"(?i)invert|inverse|negat|\bnot\b|reverse|false");
        bool good = Regex.IsMatch(path, @"(?i)(notEmpty|hasItems|hasData|hasValue|isLoaded|hasResults|hasContent|hasMessages)");
        bool bad = !good && Regex.IsMatch(path, @"(?i)(loading|empty|noItems|noResults|nothing|error|failed|failure|busy|waiting|fetching|refreshing|offline|placeholder|updat|installing|downloading|restart|searching|isOpen|running|progress|toast|snack|banner|notice|notification|overlay|dialog|popup|dragging|warning|conflict|expanded|rename|editing)");
        int pref = bad ? -1 : good ? 1 : 0;
        return invert ? -pref : pref;
    }

    private static bool BoundVisibility(XEl k) => XamlTree.Ext(k.A("Visibility"))?.Short == "Binding";

    private IEnumerable<SketchNode> Nodes(XEl el, Env env)
    {
        if (_nodes > MaxNodes) return Array.Empty<SketchNode>();
        if (el.IsText)
        {
            string t = el.Text!.Trim();
            return t.Length == 0 ? Array.Empty<SketchNode>() : new[] { Leaf(SketchKind.Text, el, env, "Text", t) };
        }
        if (el.IsProperty || el.A("x:Key") != null) return Array.Empty<SketchNode>();
        var list = Map(el, env);
        if (list.Count == 1 && list[0].Width <= 0 && XamlTree.Number(el.A("Width")) is > 0 and var w) list[0].Width = w;
        return list;
    }

    private List<SketchNode> Map(XEl el, Env env)
    {
        // the project's own control, written in XAML: drawn from its file
        if (_p.ControlOf(el) is { } ctl) return Expand(ctl, el, env);
        if (Skipped.Contains(el.Name)) return new();
        if (Known.TryGetValue(el.Name, out var map)) return map(this, el, env);
        return Other(el, env);
    }

    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "Popup", "ContextMenu", "ToolTip", "Style", "ControlTemplate", "DataTemplate", "HierarchicalDataTemplate", "ItemsPanelTemplate",
        "ResourceDictionary", "Storyboard", "GridSplitter", "Thumb", "ResizeGrip", "ScrollBar", "Track", "WindowChrome",
    };

    // ───────────────────────────── the project's controls ─────────────────────────────

    /// <summary>Draw a UserControl of the project from its own XAML. A block inside it opens where it is written,
    /// and remembers where the SCREEN uses the control.</summary>
    private List<SketchNode> Expand(XamlFile ctl, XEl use, Env caller)
    {
        string id = ctl.Class ?? ctl.Path;
        if (caller.Depth >= MaxDepth || caller.Expanding.Contains(id) || _nodes > MaxNodes || ctl.Tree is not { } root)
            return new() { Leaf(SketchKind.Box, use, caller, Widget(use), WebPages.Humanize(ctl.ShortClass)) };
        var env = new Env
        {
            File = ctl, Depth = caller.Depth + 1, InTemplate = caller.InTemplate,
            Expanding = new HashSet<string>(caller.Expanding, StringComparer.Ordinal) { id },
            Use = caller.Use ?? (caller.File == _screen && ctl != _screen ? (caller.File.Path, use.Line) : null),
        };
        env.Implicit.AddRange(ctl.Local(_p).Implicit);
        env.Implicit.AddRange(caller.Implicit);
        var nodes = Panel(root, env);
        if (nodes.Count == 0) return new() { Leaf(SketchKind.Box, use, caller, Widget(use), WebPages.Humanize(ctl.ShortClass)) };
        return nodes.Count == 1 ? nodes : new() { Wrap(SketchKind.Column, nodes, root, env, ctl.ShortClass) };
    }

    /// <summary>A region the app fills at runtime (Content="{Binding CurrentView}"): drawn with the first view the
    /// screen's data templates show — a main window opens with its first page inside, as the app does.</summary>
    private List<SketchNode> Host(XEl el, Env env)
    {
        bool bound = el.Attrs.Any(a => a.Key is "Content" or "Page" or "View" or "CurrentView" or "CurrentPage" or "Child" or "ActiveItem" or "Source"
                                       && XamlTree.Ext(a.Value)?.Short == "Binding");
        if (!bound) return new();
        if (env.Depth < MaxDepth && env.Implicit.Count > 0)
        {
            var (tf, tpl) = env.Implicit[0];
            var inner = new Env
            {
                File = tf, Depth = env.Depth + 1, Expanding = env.Expanding, Implicit = env.Implicit,
                Use = env.Use ?? (env.File == _screen && tf != _screen ? (env.File.Path, el.Line) : null),
            };
            var nodes = Panel(tpl, inner);
            if (nodes.Count > 0) return nodes;
        }
        return new() { Leaf(SketchKind.Box, el, env, Widget(el), "…") };
    }

    /// <summary>An element this doesn't know: a library's or the app's code-only control. With children it is drawn
    /// through; an icon of an icon pack is an icon; anything else is a labelled box (counted as not read).</summary>
    private List<SketchNode> Other(XEl el, Env env)
    {
        var kids = Panel(el, env);
        bool wpf = el.Ns.StartsWith("http://schemas.microsoft.com/", StringComparison.Ordinal);
        if (kids.Count > 0)
            return el.Name.EndsWith("Panel", StringComparison.Ordinal) && kids.Count > 1 ? new() { Wrap(SketchKind.Wrap, kids, el, env, Widget(el)) } : kids;
        if (wpf) return new();   // a shape, a brush, an effect: nothing to read
        var hosted = Host(el, env);
        if (hosted.Count > 0) return hosted;
        if (Regex.IsMatch(el.Name, @"(Icon|Glyph|Symbol)$"))
            return new() { Glyph(el, env, el.A("Kind") ?? el.A("Icon") ?? el.A("Symbol") ?? el.A("Glyph") ?? el.Name) };
        if (Regex.IsMatch(el.Name, @"(?i)(ProgressRing|Spinner|Loading|Busy)"))
            return new() { Leaf(SketchKind.Progress, el, env, Widget(el)) };
        if (Regex.IsMatch(el.Name, @"(?i)web(view|browser)|browser"))
            return new() { Leaf(SketchKind.Box, el, env, Widget(el), "Web page") };
        _unknown++;
        return new() { Leaf(SketchKind.Box, el, env, Widget(el), WebPages.Humanize(el.Name)) };
    }

    // ───────────────────────────── WPF's controls ─────────────────────────────

    private delegate List<SketchNode> Mapper(XamlSketcher s, XEl el, Env env);

    private static readonly Dictionary<string, Mapper> Known = BuildKnown();

    private static Dictionary<string, Mapper> BuildKnown()
    {
        var k = new Dictionary<string, Mapper>(StringComparer.Ordinal);
        // wrappers: draw what they wrap
        foreach (var w in new[] { "ScrollViewer", "Viewbox", "AdornerDecorator", "Decorator", "InkCanvas", "StatusBarItem", "ListBoxItem", "ListViewItem",
                     "TreeViewItem", "TabItem", "ItemsPresenter", "ToolBarTray", "BulletDecorator", "Window", "UserControl", "Page" })
            k[w] = (s, el, env) => s.Panel(el, env);
        k["Grid"] = (s, el, env) => s.GridOf(el, env);
        k["StackPanel"] = (s, el, env) => s.Flow(s.Attr(el, s.StyleOf(el, env), "Orientation") == "Horizontal" ? SketchKind.Row : SketchKind.Column, el, env);
        k["VirtualizingStackPanel"] = k["StackPanel"];
        k["WrapPanel"] = (s, el, env) => s.Flow(SketchKind.Wrap, el, env);
        k["UniformGrid"] = (s, el, env) => s.UniformOf(el, env);
        k["DockPanel"] = (s, el, env) => s.DockOf(el, env);
        k["Canvas"] = (s, el, env) => s.Flow(SketchKind.Stack, el, env);
        k["Border"] = (s, el, env) => s.BorderOf(el, env);
        foreach (var w in new[] { "ContentControl", "ContentPresenter", "TransitioningContentControl", "Frame" })
            k[w] = (s, el, env) => s.ContentOf(el, env);
        k["GroupBox"] = (s, el, env) => s.GroupOf(el, env);
        k["HeaderedContentControl"] = k["GroupBox"];
        k["Expander"] = (s, el, env) => s.ExpanderOf(el, env);
        k["TabControl"] = (s, el, env) => s.TabsOf(el, env);
        foreach (var w in new[] { "ItemsControl", "ListBox", "ListView", "TreeView", "DataGrid" })
            k[w] = (s, el, env) => s.ListOf(el, env);
        k["Menu"] = (s, el, env) => s.MenuOf(el, env);
        k["MenuItem"] = (s, el, env) => new() { s.Leaf(SketchKind.Text, el, env, s.Widget(el), s.Str(el.A("Header"), env) ?? "…") };
        k["ToolBar"] = (s, el, env) => s.Flow(SketchKind.Row, el, env);
        k["StatusBar"] = (s, el, env) => s.Flow(SketchKind.Row, el, env);
        k["TextBlock"] = (s, el, env) => s.TextOf(el, env);
        k["AccessText"] = k["TextBlock"];
        k["Run"] = k["TextBlock"];
        k["Label"] = (s, el, env) => s.LabelOf(el, env);
        k["Button"] = (s, el, env) => s.ButtonOf(el, env);
        k["RepeatButton"] = k["Button"];
        k["ToggleButton"] = (s, el, env) => s.ToggleOf(el, env, check: false);
        k["CheckBox"] = (s, el, env) => s.ToggleOf(el, env, check: true);
        k["RadioButton"] = (s, el, env) => s.ToggleOf(el, env, check: true);
        foreach (var w in new[] { "TextBox", "RichTextBox", "PasswordBox", "ComboBox", "DatePicker" })
            k[w] = (s, el, env) => s.InputOf(el, env);
        k["Slider"] = (s, el, env) => new() { s.Leaf(SketchKind.Slider, el, env, s.Widget(el)) };
        k["ProgressBar"] = (s, el, env) => { var n = s.Leaf(SketchKind.Progress, el, env, s.Widget(el)); n.Variant = "linear"; return new() { n }; };
        k["Separator"] = (s, el, env) => s.InRow(el) ? new() : new() { s.Leaf(SketchKind.Divider, el, env, s.Widget(el)) };
        k["Image"] = (s, el, env) => s.ImageOf(el, env);
        k["MediaElement"] = k["Image"];
        k["Calendar"] = (s, el, env) => new() { s.Leaf(SketchKind.Box, el, env, s.Widget(el), "Calendar") };
        foreach (var w in new[] { "DocumentViewer", "FlowDocumentScrollViewer", "FlowDocumentReader", "FlowDocumentPageViewer" })
            k[w] = (s, el, env) => new() { s.Leaf(SketchKind.Box, el, env, s.Widget(el), "Document") };
        k["WebBrowser"] = (s, el, env) => new() { s.Leaf(SketchKind.Box, el, env, s.Widget(el), "Web page") };
        return k;
    }

    /// <summary>A separator in a toolbar, a status bar or a horizontal stack is a vertical line — not drawn.</summary>
    private bool InRow(XEl el)
    {
        var p = el.Parent;
        while (p != null && p.IsProperty) p = p.Parent;
        return p != null && (p.Name is "ToolBar" or "StatusBar" or "Menu" || p.Name is "StackPanel" or "VirtualizingStackPanel" && p.A("Orientation") == "Horizontal");
    }

    private List<SketchNode> Flow(SketchKind kind, XEl el, Env env)
    {
        var kids = Panel(el, env);
        if (kids.Count <= 1 && kind is SketchKind.Column or SketchKind.Row) return kids;
        if (kids.Count == 0) return kids;
        // a horizontal stack sizes each child to its content: a "✓" stays next to its text
        if (kind == SketchKind.Row) foreach (var n in kids) Hint(n, "Auto");
        return new() { Wrap(kind, kids, el, env, Widget(el)) };
    }

    // ── Grid: cut between rows where nothing spans across, else between columns, else the children overlap ──

    private sealed record GridKid(XEl El, List<SketchNode> Nodes, int Row, int Col, int RowEnd, int ColEnd);

    private List<SketchNode> GridOf(XEl el, Env env)
    {
        var colSpecs = el.Prop("ColumnDefinitions")?.Elements.Where(c => c.Name == "ColumnDefinition").Select(c => c.A("Width") ?? "*").ToList() ?? new List<string>();
        var mapped = new List<(XEl El, List<SketchNode> Nodes)>();
        foreach (var k in ContentKids(el))
        {
            if (_nodes > MaxNodes) break;
            if (k.IsText || Hidden(k, env)) continue;
            var nodes = Nodes(k, env).ToList();
            if (nodes.Count > 0) mapped.Add((k, nodes));
        }
        var kids = mapped.Select(m =>
        {
            int row = (int)Math.Max(0, XamlTree.Number(m.El.A("Grid.Row")) ?? 0), col = (int)Math.Max(0, XamlTree.Number(m.El.A("Grid.Column")) ?? 0);
            int rows = (int)Math.Max(1, XamlTree.Number(m.El.A("Grid.RowSpan")) ?? 1), cols = (int)Math.Max(1, XamlTree.Number(m.El.A("Grid.ColumnSpan")) ?? 1);
            return new GridKid(m.El, m.Nodes, row, col, row + rows, col + cols);
        }).ToList();
        return Region(kids, colSpecs, el, env, 0) is { } n ? new() { n } : new();
    }

    /// <summary>Part of a grid: split between rows where no child spans across (a title bar over the content, the
    /// rows of a form), else between columns (a side panel spanning every row), else its children share one cell.</summary>
    private SketchNode? Region(List<GridKid> kids, List<string> colSpecs, XEl grid, Env env, int depth)
    {
        if (kids.Count == 0) return null;
        if (kids.Count == 1) return One(kids[0].Nodes, kids[0].El, env);
        // a child the app shows by a binding over steady ones is an overlay or an alternative (a gallery over the
        // whole body, an empty state over the list): the steady layout is drawn
        var steady = kids.Where(k => !BoundVisibility(k.El)).ToList();
        if (steady.Count > 0 && steady.Count < kids.Count)
        {
            static bool Overlap(GridKid a, GridKid b) => a.Row < b.RowEnd && b.Row < a.RowEnd && a.Col < b.ColEnd && b.Col < a.ColEnd;
            var kept = new List<GridKid>();
            foreach (var k in kids)
            {
                var under = BoundVisibility(k.El) ? steady.FirstOrDefault(s => Overlap(s, k)) : null;
                if (under == null) { kept.Add(k); continue; }
                // a hint text laid over an empty input (shown until the user types) is that input's placeholder
                if (k.El.A("IsHitTestVisible") == "False" && k.Nodes is [{ Kind: SketchKind.Text or SketchKind.Caption, Text: { } hint }]
                    && under.Nodes.SelectMany(n => n.Walk()).FirstOrDefault(n => n.Kind == SketchKind.Input && n.Text == null) is { } input)
                    input.Text = hint;
            }
            if (kept.Count < kids.Count) return Region(kept, colSpecs, grid, env, depth + 1);
        }
        if (depth < 16)
        {
            var rowCuts = Cuts(kids.Select(k => (k.Row, k.RowEnd)).ToList());
            if (rowCuts.Count > 0)
            {
                var parts = Split(kids, rowCuts, k => k.Row).Select(b => Region(b, colSpecs, grid, env, depth + 1)).OfType<SketchNode>().ToList();
                return parts.Count == 1 ? parts[0] : Wrap(SketchKind.Column, parts, grid, env, Widget(grid));
            }
            var colCuts = Cuts(kids.Select(k => (k.Col, k.ColEnd)).ToList());
            if (colCuts.Count > 0)
            {
                var parts = new List<SketchNode>();
                foreach (var band in Split(kids, colCuts, k => k.Col))
                {
                    if (Region(band, colSpecs, grid, env, depth + 1) is not { } n) continue;
                    Hint(n, SpanSpec(colSpecs, band.Min(k => k.Col), band.Max(k => k.ColEnd)));
                    parts.Add(n);
                }
                return parts.Count == 1 ? parts[0] : Wrap(SketchKind.Row, parts, grid, env, Widget(grid));
            }
        }
        return Cell(kids.Select(k => (k.El, k.Nodes)).ToList(), grid, env);
    }

    /// <summary>The boundaries no child crosses, with children on both sides.</summary>
    private static List<int> Cuts(List<(int Start, int End)> spans)
    {
        int lo = spans.Min(s => s.Start), hi = spans.Max(s => s.End);
        var cuts = new List<int>();
        for (int b = lo + 1; b < hi && b < lo + 200; b++)
            if (spans.All(s => s.End <= b || s.Start >= b) && spans.Any(s => s.End <= b) && spans.Any(s => s.Start >= b)) cuts.Add(b);
        return cuts;
    }

    private static List<List<GridKid>> Split(List<GridKid> kids, List<int> cuts, Func<GridKid, int> start)
    {
        var bands = new List<List<GridKid>>();
        int from = int.MinValue;
        foreach (int to in cuts.Append(int.MaxValue))
        {
            var band = kids.Where(k => start(k) >= from && start(k) < to).ToList();
            if (band.Count > 0) bands.Add(band);
            from = to;
        }
        return bands;
    }

    /// <summary>The width spec of columns c0..c1 together: their stars added, their pixels added, else Auto.</summary>
    private static string SpanSpec(List<string> specs, int c0, int c1)
    {
        if (specs.Count == 0) return "*";
        var parts = Enumerable.Range(c0, Math.Max(1, c1 - c0)).Select(i => i < specs.Count ? specs[i].Trim() : "*").ToList();
        if (parts.Count == 1) return parts[0];
        double stars = 0, px = 0;
        bool auto = false;
        foreach (var p in parts)
        {
            if (p.EndsWith('*')) stars += XamlTree.Number(p.TrimEnd('*')) ?? 1;
            else if (XamlTree.Number(p) is { } v) px += v;
            else auto = true;
        }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return stars > 0 ? stars.ToString(inv) + "*" : !auto && px > 0 ? px.ToString(inv) : "Auto";
    }

    /// <summary>Several children in one cell overlap in WPF. The ones the app shows by a binding are alternatives
    /// (an empty state over a list): the steady ones win, else the richest. A hint text over an input is its
    /// placeholder; a title on the left and a button on the right sit side by side.</summary>
    private SketchNode? Cell(List<(XEl El, List<SketchNode> Nodes)> kids, XEl grid, Env env)
    {
        if (kids.Count == 1) return One(kids[0].Nodes, kids[0].El, env);
        var steady = kids.Where(k => !BoundVisibility(k.El)).ToList();
        // all shown by bindings: modes of one panel (explorer / search / debug) — the first declared is the default,
        // unless it is only a line next to the real content (an "open a folder" hint before the file tree)
        int richest = kids.Max(k => k.Nodes.Sum(n => n.Count()));
        var pick = steady.Count > 0 ? steady : new() { kids.First(k => k.Nodes.Sum(n => n.Count()) * 3 >= richest) };
        var input = pick.SelectMany(k => k.Nodes).FirstOrDefault(n => n.Kind == SketchKind.Input && n.Text == null);
        if (input != null && kids.FirstOrDefault(k => k.El.A("IsHitTestVisible") == "False" && k.Nodes.Count == 1 && k.Nodes[0].Kind is SketchKind.Text or SketchKind.Caption) is { El: not null } mark)
        {
            input.Text = mark.Nodes[0].Text;
            pick = pick.Where(k => !ReferenceEquals(k.El, mark.El)).ToList();
        }
        if (pick.Count == 1) return One(pick[0].Nodes, pick[0].El, env);
        bool apart = pick.Select(k => AlignOrder(k.El)).Distinct().Count() > 1;
        var nodes = pick.OrderBy(k => apart ? AlignOrder(k.El) : 0).SelectMany(k => k.Nodes).ToList();
        return Wrap(apart ? SketchKind.Row : SketchKind.Column, nodes, grid, env, Widget(grid));
    }

    private static int AlignOrder(XEl el) => el.A("HorizontalAlignment") switch { "Right" => 2, "Center" => 1, _ => 0 };

    private SketchNode? One(List<SketchNode> nodes, XEl at, Env env) => nodes.Count switch
    {
        0 => null,
        1 => nodes[0],
        _ => Wrap(SketchKind.Column, nodes, at, env, Widget(at)),
    };

    /// <summary>A column's width for the block in it: "250" fixed, "2*" a share, "Auto" as wide as a short text
    /// (a panel takes a smaller share).</summary>
    private static void Hint(SketchNode n, string spec)
    {
        if (n.Width > 0) return;
        string s = spec.Trim();
        if (s.EndsWith('*')) { n.Flex = XamlTree.Number(s.TrimEnd('*')) ?? 1; return; }
        if (XamlTree.Number(s) is > 0 and var px) { n.Width = px; return; }
        if (n.Kind is SketchKind.Text or SketchKind.Caption or SketchKind.Heading && n.Text != null)
            n.Width = Math.Clamp(n.Text.Length * (n.Kind == SketchKind.Heading ? 10.5 : 7.2) + 8, 24, 320);
        else if (n.Kind is SketchKind.Column or SketchKind.Row or SketchKind.Card or SketchKind.List or SketchKind.Stack or SketchKind.Wrap or SketchKind.Grid)
            n.Flex = 0.5;
    }

    private List<SketchNode> UniformOf(XEl el, Env env)
    {
        var kids = Panel(el, env);
        if (kids.Count == 0) return kids;
        int cols = (int)(XamlTree.Number(el.A("Columns")) ?? 0), rows = (int)(XamlTree.Number(el.A("Rows")) ?? 0);
        if (rows == 1 && cols <= 0 || cols >= kids.Count && rows <= 1) return new() { Wrap(SketchKind.Row, kids, el, env, Widget(el)) };
        if (cols <= 0) cols = rows > 0 ? (int)Math.Ceiling(kids.Count / (double)rows) : (int)Math.Ceiling(Math.Sqrt(kids.Count));
        var g = Wrap(SketchKind.Grid, kids, el, env, Widget(el));
        g.Columns = Math.Clamp(cols, 1, 6);
        return new() { g };
    }

    /// <summary>DockPanel: docked to the top, then the middle (left, the filling last child, right), then the bottom.</summary>
    private List<SketchNode> DockOf(XEl el, Env env)
    {
        var kids = new List<(XEl El, List<SketchNode> Nodes)>();
        foreach (var k in ContentKids(el))
        {
            if (k.IsText || Hidden(k, env)) continue;
            var nodes = Nodes(k, env).ToList();
            if (nodes.Count > 0) kids.Add((k, nodes));
        }
        if (kids.Count == 0) return new();
        bool fillLast = el.A("LastChildFill") != "False";
        var top = new List<SketchNode>();
        var bottom = new List<SketchNode>();
        var left = new List<SketchNode>();
        var right = new List<SketchNode>();
        var fill = new List<SketchNode>();
        for (int i = 0; i < kids.Count; i++)
        {
            var (k, nodes) = kids[i];
            if (fillLast && i == kids.Count - 1) { fill.AddRange(nodes); break; }
            string dock = k.A("DockPanel.Dock") ?? "Left";
            if (dock is "Left" or "Right") foreach (var n in nodes) Hint(n, "Auto");
            switch (dock)
            {
                case "Top": top.AddRange(nodes); break;
                case "Bottom": bottom.InsertRange(0, nodes); break;
                case "Right": right.InsertRange(0, nodes); break;
                default: left.AddRange(nodes); break;
            }
        }
        var middle = new List<SketchNode>();
        if (left.Count + right.Count > 0)
        {
            var across = new List<SketchNode>(left);
            if (fill.Count > 0) across.Add(fill.Count == 1 ? fill[0] : Wrap(SketchKind.Column, fill, el, env, Widget(el)));
            across.AddRange(right);
            middle.Add(across.Count == 1 ? across[0] : Wrap(SketchKind.Row, across, el, env, Widget(el)));
        }
        else middle.AddRange(fill);
        var all = top.Concat(middle).Concat(bottom).ToList();
        return all.Count <= 1 ? all : new() { Wrap(SketchKind.Column, all, el, env, Widget(el)) };
    }

    // ── Border: a card when it paints something ──

    private List<SketchNode> BorderOf(XEl el, Env env)
    {
        var st = StyleOf(el, env);
        uint? bg = _p.BrushColor(Attr(el, st, "Background"), env.File);
        uint? line = _p.BrushColor(Attr(el, st, "BorderBrush"), env.File);
        double thick = XamlTree.First(Attr(el, st, "BorderThickness")) ?? 0;
        bool filled = bg is { } b && (b >> 24) >= 0x10;
        bool outlined = line is { } l && (l >> 24) >= 0x10 && thick > 0;
        var inner = Panel(el, env);
        if (!filled && !outlined) return inner;
        double? h = XamlTree.Number(el.A("Height")), w = XamlTree.Number(el.A("Width"));
        if (inner.Count == 0)
        {
            if (h is > 0 and <= 3) return new() { Leaf(SketchKind.Divider, el, env, Widget(el)) };
            if (h is not > 16 || w is > 0 and <= 16) return new();   // a dot, a strip, a fill the size of its parent
            var block = Leaf(SketchKind.Card, el, env, Widget(el));
            block.Variant = "box";
            block.Height = Math.Min(h.Value, 160);
            return new() { block };
        }
        double radius = XamlTree.First(Attr(el, st, "CornerRadius")) ?? 0;
        if (inner.Count == 1)
        {
            var only = inner[0];
            // a round badge around a glyph: a filled icon
            if (only.Kind == SketchKind.Icon && w is > 0 and <= 72 && radius >= w / 2 - 1)
            {
                var badge = Leaf(SketchKind.IconButton, el, env, Widget(el));
                badge.Icon = only.Icon;
                badge.Variant = "filled";
                return new() { badge };
            }
            // a pill: a short label on a small rounded background
            if (only.Kind is SketchKind.Text or SketchKind.Caption && only.Text is { Length: > 0 and <= 28 } && (radius >= 6 || h is > 0 and <= 30) && w is not > 240)
                return new() { Leaf(SketchKind.Chip, el, env, Widget(el), only.Text) };
            // a frame around one control (an input's box, a button's backdrop): the control itself
            if (only.Kind is SketchKind.Input or SketchKind.Button or SketchKind.IconButton or SketchKind.Toggle or SketchKind.Chip or SketchKind.Progress
                or SketchKind.Slider or SketchKind.Icon or SketchKind.Avatar or SketchKind.Image or SketchKind.Card or SketchKind.Table)
                return inner;
        }
        var card = Wrap(SketchKind.Card, inner, el, env, Widget(el));
        if (!outlined) card.Variant = "box";
        return new() { card };
    }

    private List<SketchNode> ContentOf(XEl el, Env env)
    {
        string? content = el.A("Content");
        if (content != null && XamlTree.Ext(content)?.Short != "Binding")
            return Str(content, env) is { Length: > 0 } t ? new() { Leaf(SketchKind.Text, el, env, Widget(el), t) } : new();
        var kids = Panel(el, env);
        return kids.Count > 0 ? kids : Host(el, env);
    }

    private List<SketchNode> GroupOf(XEl el, Env env)
    {
        string? header = Str(el.A("Header"), env) ?? HeaderText(el.Prop("Header"), env);
        var kids = Panel(el, env);
        if (header != null) kids.Insert(0, Leaf(SketchKind.Heading, el, env, Widget(el), header));
        if (kids.Count == 0) return kids;
        return new() { Wrap(SketchKind.Card, kids, el, env, Widget(el)) };
    }

    private List<SketchNode> ExpanderOf(XEl el, Env env)
    {
        var tile = Leaf(SketchKind.Tile, el, env, Widget(el), Str(el.A("Header"), env) ?? HeaderText(el.Prop("Header"), env) ?? "…");
        tile.Variant = "+chevron";
        if (el.A("IsExpanded") != "True") return new() { tile };
        var kids = Panel(el, env);
        kids.Insert(0, tile);
        return new() { Wrap(SketchKind.Column, kids, el, env, Widget(el)) };
    }

    /// <summary>The text a header property element shows (&lt;TabItem.Header&gt;&lt;StackPanel&gt;…).</summary>
    private string? HeaderText(XEl? prop, Env env)
    {
        if (prop == null) return null;
        int before = _nodes;
        var nodes = Panel(prop, env);
        _nodes = before;   // read for its text only, not drawn
        return nodes.SelectMany(n => n.Walk()).FirstOrDefault(n => n.Text != null)?.Text;
    }

    private List<SketchNode> TabsOf(XEl el, Env env)
    {
        var items = ContentKids(el).Where(k => k.Name == "TabItem" && !Hidden(k, env)).ToList();
        var tabs = Leaf(SketchKind.Tabs, el, env, Widget(el));
        foreach (var t in items)
        {
            tabs.Items.Add(Str(t.A("Header"), env) ?? HeaderText(t.Prop("Header"), env) ?? "…");
            tabs.ItemIcons.Add(null);
        }
        if (tabs.Items.Count == 0)
        {
            tabs.Items.AddRange(new[] { "…", "…", "…" });
            tabs.ItemIcons.AddRange(new string?[] { null, null, null });
        }
        var result = new List<SketchNode> { tabs };
        var first = items.FirstOrDefault(t => t.A("IsSelected") == "True") ?? items.FirstOrDefault();
        if (first != null) result.AddRange(Panel(first, env));
        return result.Count == 1 ? result : new() { Wrap(SketchKind.Column, result, el, env, Widget(el)) };
    }

    // ── lists: the items written in the XAML, else a few rows of the item template ──

    private List<SketchNode> ListOf(XEl el, Env env)
    {
        var columns = el.Name == "DataGrid" ? el.Prop("Columns")?.Elements.Where(c => !c.IsProperty).ToList()
            : el.Name == "ListView" ? el.Prop("View")?.Descendants().Where(d => d.Name == "GridViewColumn").ToList() : null;
        if (columns is { Count: > 0 })
        {
            var t = Leaf(SketchKind.Table, el, env, Widget(el));
            foreach (var c in columns) t.Items.Add(Str(c.A("Header"), env) ?? HeaderText(c.Prop("Header"), env) ?? "…");
            t.Columns = Math.Clamp(t.Items.Count, 1, 6);
            return new() { t };
        }
        var kind = PanelKind(el, out int cols);
        var list = Leaf(kind, el, env, Widget(el));
        list.Columns = cols;
        foreach (var k in ContentKids(el))
        {
            if (_nodes > MaxNodes) break;
            if (k.IsText || Hidden(k, env)) continue;
            list.Children.AddRange(Nodes(k, env));
        }
        if (list.Children.Count == 0)
        {
            var (tf, tpl) = ItemTemplate(el, env);
            if (tf != null && tpl != null)
            {
                var inner = new Env
                {
                    File = tf, Depth = env.Depth + 1, Expanding = env.Expanding, Implicit = env.Implicit, InTemplate = true,
                    Use = env.Use ?? (env.File == _screen && tf != _screen ? (env.File.Path, el.Line) : null),
                };
                var item = Panel(tpl, inner);
                if (item.Count == 0)
                {
                    // every part of the item is conditional: draw them all rather than nothing
                    inner.InTemplate = false;
                    item = Panel(tpl, inner);
                }
                Repeat(list, item, kind == SketchKind.Grid ? Math.Max(4, cols * 2) : 3);
            }
            else if (el.Name is "ListBox" or "ListView" or "TreeView" || el.A("DisplayMemberPath") != null)
                for (int i = 0; i < 3; i++) list.Children.Add(Leaf(SketchKind.Tile, el, env, Widget(el), "…"));
        }
        return list.Children.Count == 0 ? new() : new() { list };
    }

    /// <summary>How the items are laid out: the ItemsPanel (a wrap panel, a uniform grid, a horizontal stack), else a list.</summary>
    private static SketchKind PanelKind(XEl el, out int cols)
    {
        cols = 0;
        var panel = el.Prop("ItemsPanel")?.Descendants().FirstOrDefault(d => !d.IsText && !d.IsProperty && d.Name != "ItemsPanelTemplate");
        switch (panel?.Name)
        {
            case "WrapPanel":
                return SketchKind.Wrap;
            case "UniformGrid":
            {
                int c = (int)(XamlTree.Number(panel.A("Columns")) ?? 0), r = (int)(XamlTree.Number(panel.A("Rows")) ?? 0);
                if (r == 1 && c <= 0) return SketchKind.Row;
                cols = Math.Clamp(c > 0 ? c : 3, 1, 6);
                return SketchKind.Grid;
            }
            case "StackPanel" or "VirtualizingStackPanel" when panel.A("Orientation") == "Horizontal":
                return SketchKind.Row;
            case { } custom when custom.EndsWith("Panel", StringComparison.Ordinal) || custom.EndsWith("Grid", StringComparison.Ordinal):
                cols = 3;
                return SketchKind.Grid;
        }
        return SketchKind.List;
    }

    private (XamlFile? File, XEl? El) ItemTemplate(XEl el, Env env)
    {
        var inline = el.Prop("ItemTemplate")?.Elements.FirstOrDefault(e => e.Name is "DataTemplate" or "HierarchicalDataTemplate");
        if (inline != null) return (env.File, inline);
        if (XamlTree.Ext(el.A("ItemTemplate")) is { Arg.Length: > 0 } ext && _p.TemplateOf(ext.Arg, env.File) is { } t) return (t.File, t.El);
        return (null, null);
    }

    private static void Repeat(SketchNode into, List<SketchNode> item, int times)
    {
        if (item.Count == 0) return;
        if (item.Sum(i => i.Count()) > 40) times = 2;
        for (int i = 0; i < times; i++) into.Children.AddRange(item);
    }

    private List<SketchNode> MenuOf(XEl el, Env env)
    {
        var row = Leaf(SketchKind.Row, el, env, Widget(el));
        foreach (var mi in ContentKids(el).Where(k => k.Name == "MenuItem"))
            row.Children.Add(Leaf(SketchKind.Text, mi, env, Widget(mi), Str(mi.A("Header"), env) ?? HeaderText(mi.Prop("Header"), env) ?? "…"));
        return row.Children.Count > 0 ? new() { row } : new();
    }

    // ── text ──

    private static readonly Regex IconFont = new(@"(?i)mdl2|fluent icons|segoe ui symbol|symbols?\b|icons?\b|awesome|material|glyph", RegexOptions.Compiled);

    /// <summary>A glyph of an icon font (Segoe MDL2 / Fluent Icons use the private use area).</summary>
    internal static bool IsGlyph(string? s) => s is { Length: >= 1 and <= 2 } && s.All(c => c is >= '' and <= '');

    private List<SketchNode> TextOf(XEl el, Env env)
    {
        var st = StyleOf(el, env);
        string? text = el.A("Text") is { } t ? Str(t, env) : XamlTree.InlineText(el, v => Str(v, env));
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return new();
        if (IsGlyph(text) || IconFont.IsMatch(Attr(el, st, "FontFamily") ?? "") && text.Length <= 2) return new() { Glyph(el, env, text) };
        return new() { Leaf(TextKind(el, st), el, env, Widget(el), text) };
    }

    private List<SketchNode> LabelOf(XEl el, Env env)
    {
        var st = StyleOf(el, env);
        if (el.A("Content") is { } c)
            return Str(c, env) is { Length: > 0 } t ? new() { Leaf(TextKind(el, st), el, env, Widget(el), t.Trim()) } : new();
        return Panel(el, env);
    }

    /// <summary>A heading, body text or a caption — by its size and weight, else by the name of its style or brush.</summary>
    private SketchKind TextKind(XEl el, XStyle? st)
    {
        double size = XamlTree.Number(Attr(el, st, "FontSize")) ?? 0;
        bool bold = Regex.IsMatch(Attr(el, st, "FontWeight") ?? "", "(?i)bold|black|heavy");
        string names = (st?.Chain ?? "") + " " + (XamlTree.Ext(Attr(el, st, "Foreground"))?.Arg ?? "");
        if (size > 0 && size <= 11.5) return SketchKind.Caption;
        if (size >= 17 || size >= 14.5 && bold) return SketchKind.Heading;
        if (size == 0 && Regex.IsMatch(st?.Chain ?? "", @"(?i)title|heading|header|display|\bh[1-3]\b") && !Regex.IsMatch(st?.Chain ?? "", "(?i)sub|small|caption|label|hint")) return SketchKind.Heading;
        if (size is 0 or <= 12.5 && Regex.IsMatch(names, @"(?i)muted|faint|hint|caption|subtle|disabled|tertiary|secondary|dim")) return SketchKind.Caption;
        return SketchKind.Text;
    }

    private SketchNode Glyph(XEl el, Env env, string icon)
    {
        var n = Leaf(SketchKind.Icon, el, env, Widget(el));
        n.Icon = icon;
        return n;
    }

    /// <summary>A text value: a literal, a translation ({services:Loc key}, a .resx {x:Static}), a string
    /// resource — and "…" for a value the app binds at runtime ("Plan — …" when it has a StringFormat).</summary>
    private string? Str(string? value, Env env)
    {
        if (value == null) return null;
        var ext = XamlTree.Ext(value);
        if (ext == null) return XamlTree.Literal(value);
        switch (ext.Short)
        {
            case "Loc" or "Localize" or "Translate" or "Tr" or "LocText" or "Lang" or "I18n":
                return _p.Loc(ext.Arg) ?? "…";
            case "Static":
                return _p.Resx(ext.Arg) ?? "…";
            case "StaticResource" or "DynamicResource":
                return env.File.Local(_p).Strings.TryGetValue(ext.Arg, out var ls) ? ls : _p.Global.Strings.TryGetValue(ext.Arg, out var gs) ? gs : "…";
            case "Binding" or "TemplateBinding" or "MultiBinding" when ext.Named.TryGetValue("StringFormat", out var fmt):
            {
                string f = fmt.Trim().Trim('\'');
                if (f.StartsWith("{}", StringComparison.Ordinal)) f = f[2..];
                f = Regex.Replace(f, @"\{\d+(:[^}]*)?\}", "…").Trim();
                return f.Length > 0 ? f : "…";
            }
            default:
                return "…";
        }
    }

    // ── buttons, toggles, inputs, images ──

    private List<SketchNode> ButtonOf(XEl el, Env env)
    {
        var st = StyleOf(el, env);
        var (text, icon, rich) = Content(el, env, st);
        if (rich != null) return new() { rich };
        if (text == null && icon == null) icon = TemplateGlyph(el, st);
        if (text == null)
        {
            var ib = Leaf(SketchKind.IconButton, el, env, Widget(el));
            ib.Icon = icon ?? "more";
            return new() { ib };
        }
        var b = Leaf(SketchKind.Button, el, env, Widget(el), text);
        b.Icon = icon;
        b.Variant = ButtonVariant(el, st, env);
        return new() { b };
    }

    /// <summary>What a button shows: its text and icon — or, when its content is a whole layout (a card you can
    /// press: an icon, a title and a line under it), that layout as a card.</summary>
    private (string? Text, string? Icon, SketchNode? Rich) Content(XEl el, Env env, XStyle? st)
    {
        if (el.A("Content") is { } content)
        {
            string? s = Str(content, env)?.Trim();
            if (s != null && (IsGlyph(s) || IconFont.IsMatch(Attr(el, st, "FontFamily") ?? "") && s.Length <= 2)) return (null, s, null);
            return (string.IsNullOrEmpty(s) ? null : s, null, null);
        }
        var inner = Panel(el, env);
        if (inner.Count == 0) return (null, null, null);
        var all = inner.SelectMany(n => n.Walk()).ToList();
        var texts = all.Where(n => n.Kind is SketchKind.Text or SketchKind.Heading or SketchKind.Caption or SketchKind.Chip && n.Text != null).ToList();
        if (texts.Count >= 2 || all.Any(n => n.Kind is SketchKind.Input or SketchKind.Image or SketchKind.List or SketchKind.Grid or SketchKind.Table or SketchKind.Button or SketchKind.Toggle))
        {
            var card = Wrap(SketchKind.Card, inner, el, env, Widget(el));
            card.Variant = "box";
            return (null, null, card);
        }
        return (texts.FirstOrDefault()?.Text, all.FirstOrDefault(n => n.Kind is SketchKind.Icon or SketchKind.IconButton)?.Icon, null);
    }

    /// <summary>The glyph a button's own template draws (an icon-only caption or tool button).</summary>
    private static string? TemplateGlyph(XEl el, XStyle? st)
    {
        var tpl = el.Prop("Template")?.Elements.FirstOrDefault(e => e.Name == "ControlTemplate") ?? st?.Template;
        return tpl?.Descendants().Select(d => d.IsText ? d.Text?.Trim() : d.A("Text")?.Trim()).FirstOrDefault(IsGlyph);
    }

    private string ButtonVariant(XEl el, XStyle? st, Env env)
    {
        string keys = st?.Chain ?? "";
        if (Regex.IsMatch(keys, @"(?i)primary|accent|cta|filled|submit|send|confirm|danger|success|brand")) return "filled";
        if (Regex.IsMatch(keys, @"(?i)secondary|outline|bordered")) return "outlined";
        if (Regex.IsMatch(keys, @"(?i)ghost|tool|icon|link|text|flat|subtle|clear|nav|chip|tab|menu|plain|transparent|caption|title|bar\b|pill|toggle|quiet|minimal")) return "text";
        if (el.A("IsDefault") == "True") return "filled";
        if (_p.BrushColor(Attr(el, st, "Background"), env.File) is { } bg && (bg >> 24) < 0x10) return "text";
        return "tonal";
    }

    /// <summary>CheckBox / RadioButton: a switch with its label. A nav or tab radio button and a ToggleButton are
    /// pressed like buttons: an icon button for a glyph, a chip for a word.</summary>
    private List<SketchNode> ToggleOf(XEl el, Env env, bool check)
    {
        var st = StyleOf(el, env);
        bool buttonLike = !check || Regex.IsMatch(st?.Chain ?? "", @"(?i)nav|tab|segment|chip|pill|activity|button|mode|filter|menu|tool");
        if (buttonLike)
        {
            var (text, icon, rich) = Content(el, env, st);
            if (rich != null) return new() { rich };
            if (text == null) icon ??= TemplateGlyph(el, st);
            if (text == null && icon != null)
            {
                var ib = Leaf(SketchKind.IconButton, el, env, Widget(el));
                ib.Icon = icon;
                return new() { ib };
            }
            if (text != null) return new() { Leaf(SketchKind.Chip, el, env, Widget(el), text) };
            return new() { Leaf(SketchKind.Toggle, el, env, Widget(el)) };
        }
        var toggle = Leaf(SketchKind.Toggle, el, env, Widget(el));
        var label = el.A("Content") is { } c ? (Str(c, env) is { Length: > 0 } t ? new List<SketchNode> { Leaf(SketchKind.Text, el, env, Widget(el), t.Trim()) } : new())
            : Panel(el, env);
        if (label.Count == 0) return new() { toggle };
        label.Insert(0, toggle);
        return new() { Wrap(SketchKind.Row, label, el, env, Widget(el)) };
    }

    private static readonly Regex HintAttr = new(@"(?i)(hint|placeholder|placeholdertext|watermark|hinttext|cue)$", RegexOptions.Compiled);

    private List<SketchNode> InputOf(XEl el, Env env)
    {
        var st = StyleOf(el, env);
        string? hint = null;
        foreach (var (k, v) in el.Attrs)
            if (HintAttr.IsMatch(k) && Str(v, env) is { } h && h != "…") { hint = h; break; }
        if (hint == null && Regex.IsMatch(st?.Chain ?? "", "(?i)hint|placeholder|watermark|search") && Str(el.A("Tag"), env) is { } tag && tag != "…") hint = tag;
        string? text = hint;
        if (text == null && el.Name == "TextBox" && XamlTree.Literal(el.A("Text")) is { Length: > 0 } lit) text = lit;
        if (text == null && el.Name == "ComboBox")
            text = (Str(el.A("Text"), env) is { } ct && ct != "…" ? ct : null)
                   ?? ContentKids(el).Where(k => !k.IsText).Select(k => Str(k.A("Content"), env) ?? XamlTree.InlineText(k, v => Str(v, env)).Trim()).FirstOrDefault(s => s is { Length: > 0 } && s != "…");
        var n = Leaf(SketchKind.Input, el, env, Widget(el), text);
        string what = (el.A("x:Name") ?? el.A("Name") ?? "") + " " + (st?.Chain ?? "") + " " + (hint ?? "");
        if (el.Name is "ComboBox" or "DatePicker") n.Variant = "dropdown";
        else if (el.Name == "PasswordBox") n.Variant = "password";
        else if (Regex.IsMatch(what, @"(?i)search|filter|find|query|ค้นหา")) n.Variant = "search";
        return new() { n };
    }

    private List<SketchNode> ImageOf(XEl el, Env env)
    {
        double? w = XamlTree.Number(el.A("Width")), h = XamlTree.Number(el.A("Height"));
        if (w is > 0 and <= 48 && (h ?? w) <= 48 || h is > 0 and <= 40 && w == null) return new() { Glyph(el, env, "image") };   // a logo, an avatar-sized picture
        var n = Leaf(SketchKind.Image, el, env, Widget(el));
        n.Height = h ?? (w is > 0 ? w.Value * 0.6 : 0);
        return new() { n };
    }

    // ───────────────────────────── styles ─────────────────────────────

    /// <summary>An attribute, else what the element's style sets.</summary>
    private string? Attr(XEl el, XStyle? st, string prop) => el.A(prop) ?? (st != null && st.Setters.TryGetValue(prop, out var v) ? v : null);

    /// <summary>The element's style (by key, or written inline) merged with the styles it is based on.</summary>
    private XStyle? StyleOf(XEl el, Env env)
    {
        XStyle? s = null;
        if (el.Prop("Style")?.Elements.FirstOrDefault(e => e.Name == "Style") is { } inline) s = WpfProject.ReadStyle(inline);
        else if (XamlTree.Ext(el.A("Style")) is { Arg.Length: > 0 } ext) s = _p.StyleOf(ext.Arg, env.File);
        if (s == null) return null;
        var merged = new XStyle { Key = s.Key, BasedOn = s.BasedOn, Template = s.Template, ShownByTrigger = s.ShownByTrigger };
        foreach (var (k, v) in s.Setters) merged.Setters[k] = v;
        var chain = new List<string>();
        if (s.Key != null) chain.Add(s.Key);
        string? b = s.BasedOn;
        for (int guard = 0; b != null && guard < 6 && _p.StyleOf(b, env.File) is { } bs; guard++)
        {
            chain.Add(b);
            foreach (var (k, v) in bs.Setters) merged.Setters.TryAdd(k, v);
            merged.Template ??= bs.Template;
            merged.ShownByTrigger |= bs.ShownByTrigger;
            b = bs.BasedOn;
        }
        merged.Chain = string.Join(" ", chain);
        return merged;
    }

    // ───────────────────────────── blocks ─────────────────────────────

    /// <summary>The element's name for the stage ("Button", "TextBox KeyBox" when it has an x:Name).</summary>
    private string Widget(XEl el) => (el.A("x:Name") ?? el.A("Name")) is { Length: > 0 } n ? el.Name + " " + n : el.Name;

    private SketchNode Leaf(SketchKind kind, XEl at, Env env, string? widget, string? text = null)
    {
        _nodes++;
        return new SketchNode
        {
            Kind = kind, Widget = widget, Text = text, File = env.File.Path, Line = at.Line,
            UseFile = env.Use?.File, UseLine = env.Use?.Line ?? 0,
        };
    }

    private SketchNode Wrap(SketchKind kind, IEnumerable<SketchNode> children, XEl at, Env env, string? widget)
    {
        var n = Leaf(kind, at, env, widget);
        n.Children.AddRange(children);
        return n;
    }
}
