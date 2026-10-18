using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services.UiStudio.Sketch;

namespace CluadeX.Views.UiStudio;

/// <summary>
/// Draws a simulated screen (<see cref="ScreenSketch"/>) as a phone-sized wireframe in the app's own colours: app
/// bar, texts, buttons, inputs, lists, tiles, tab and nav bars. It lays the blocks out itself (one pass, like a
/// column of the real app 360 logical pixels wide) and scales to fit, so the same view is a gallery thumbnail and
/// the large stage. With <see cref="Interactive"/> the block under the pointer is outlined and named, and a click
/// raises <see cref="NodeClicked"/> — the stage opens that block's line in the code editor.
/// </summary>
public sealed class SketchView : FrameworkElement
{
    public const double ScreenWidth = 360, ScreenHeight = 760;

    public static readonly DependencyProperty SketchProperty = DependencyProperty.Register(nameof(Sketch), typeof(ScreenSketch), typeof(SketchView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((SketchView)d).Relayout()));
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(nameof(Selected), typeof(SketchNode), typeof(SketchView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.Register(nameof(Interactive), typeof(bool), typeof(SketchView),
        new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty FullHeightProperty = DependencyProperty.Register(nameof(FullHeight), typeof(bool), typeof(SketchView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((SketchView)d).Relayout()));

    public ScreenSketch? Sketch { get => (ScreenSketch?)GetValue(SketchProperty); set => SetValue(SketchProperty, value); }
    public SketchNode? Selected { get => (SketchNode?)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public bool Interactive { get => (bool)GetValue(InteractiveProperty); set => SetValue(InteractiveProperty, value); }
    /// <summary>Grow to the whole content (the stage scrolls) instead of one screen.</summary>
    public bool FullHeight { get => (bool)GetValue(FullHeightProperty); set => SetValue(FullHeightProperty, value); }

    public event Action<SketchNode>? NodeClicked;
    public event Action<SketchNode?>? NodeHovered;

    private readonly List<(SketchNode Node, Rect Box)> _boxes = new();
    private readonly List<Action<DrawingContext>> _paint = new();
    private SketchNode? _hover;
    private double _contentHeight = ScreenHeight;
    private Palette _c = Palette.From(new SketchTheme());

    private const double Pad = 16, Gap = 8, StatusBar = 24;
    private static readonly Typeface Ui = new(new FontFamily("Segoe UI, Leelawadee UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface UiBold = new(new FontFamily("Segoe UI, Leelawadee UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Glyphs = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public SketchView()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Focusable = false;
    }

    private double LogicalHeight => FullHeight ? Math.Max(ScreenHeight, _contentHeight) : ScreenHeight;

    protected override Size MeasureOverride(Size available)
    {
        double ratio = LogicalHeight / ScreenWidth;
        double w = double.IsInfinity(available.Width) ? ScreenWidth : available.Width;
        if (FullHeight) return new Size(w, w * ratio);   // as tall as the content; the stage scrolls
        double h = double.IsInfinity(available.Height) ? w * ratio : available.Height;
        return new Size(Math.Min(w, h / ratio), Math.Min(h, w * ratio));
    }

    /// <summary>The scale and offset that fit the logical screen into the control.</summary>
    private (double Scale, double X, double Y) Fit()
    {
        double s = FullHeight ? ActualWidth / ScreenWidth : Math.Min(ActualWidth / ScreenWidth, ActualHeight / ScreenHeight);
        if (s <= 0 || double.IsNaN(s)) s = 1;
        double x = (ActualWidth - ScreenWidth * s) / 2;
        double y = FullHeight ? 0 : (ActualHeight - ScreenHeight * s) / 2;
        return (s, Math.Max(0, x), Math.Max(0, y));
    }

    // ───────────────────────────── input ─────────────────────────────

    private SketchNode? HitTest(Point p)
    {
        var (s, ox, oy) = Fit();
        var q = new Point((p.X - ox) / s, (p.Y - oy) / s);
        for (int i = _boxes.Count - 1; i >= 0; i--)
            if (_boxes[i].Box.Contains(q)) return _boxes[i].Node;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Interactive) return;
        var n = HitTest(e.GetPosition(this));
        if (ReferenceEquals(n, _hover)) return;
        _hover = n;
        Cursor = n != null ? Cursors.Hand : null;
        NodeHovered?.Invoke(n);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover == null) return;
        _hover = null;
        NodeHovered?.Invoke(null);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (Interactive && HitTest(e.GetPosition(this)) is { } n) { NodeClicked?.Invoke(n); e.Handled = true; }
    }

    // ───────────────────────────── layout ─────────────────────────────

    private void Relayout()
    {
        _boxes.Clear();
        _paint.Clear();
        _hover = null;
        if (Sketch is not { } sk) { InvalidateVisual(); return; }
        _c = Palette.From(sk.Theme);
        var root = sk.Root;

        // the screen's frame: things pinned to the bottom (nav bar) and floating (FAB) are taken out of the flow
        var flow = new List<SketchNode>();
        var bottom = new List<SketchNode>();
        var floating = new List<SketchNode>();
        Collect(root, flow, bottom, floating, 0);

        double y = StatusBar;
        foreach (var n in flow) y += Layout(n, 0, y, ScreenWidth, true) + (IsBand(n) ? 0 : Gap);
        _contentHeight = y + Pad + bottom.Count * 66;

        double screenBottom = LogicalHeight;
        double by = screenBottom;
        foreach (var n in Enumerable.Reverse(bottom))
        {
            double h = n.Kind == SketchKind.NavBar ? 64 : 56;
            by -= h;
            Layout(n, 0, by, ScreenWidth, true);
        }
        double fy = by - 16;
        foreach (var f in floating)
        {
            double size = f.Text != null ? 48 : 56;
            double width = f.Text != null ? Math.Min(200, Measure(f.Text, 14, true) + 64) : 56;
            fy -= size;
            var box = new Rect(ScreenWidth - 16 - width, fy, width, size);
            Box(f, box);
            var node = f;
            _paint.Add(dc => DrawFab(dc, node, box));
            fy -= 12;
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

    private static void Collect(SketchNode n, List<SketchNode> flow, List<SketchNode> bottom, List<SketchNode> floating, int depth)
    {
        // Unwrap the screen's own columns (Scaffold, page wrappers) so the app bar is at the top and the nav bar pinned.
        if (n.Kind == SketchKind.Column && depth < 4 && (n.Widget is "Scaffold" or "CupertinoPageScaffold" or null || n.Children.Any(c => c.Kind is SketchKind.AppBar or SketchKind.NavBar || c.Floating)))
        {
            foreach (var c in n.Children) Collect(c, flow, bottom, floating, depth + 1);
            return;
        }
        if (n.Floating) floating.Add(n);
        else if (n.Kind == SketchKind.NavBar && depth > 0) bottom.Add(n);
        else flow.Add(n);
    }

    private static bool IsBand(SketchNode n) => n.Kind is SketchKind.AppBar or SketchKind.Tabs;

    private void Box(SketchNode n, Rect r) => _boxes.Add((n, r));

    /// <summary>Lay a block out at (x, y) with width w; returns its height. <paramref name="edge"/>: it spans the
    /// screen (gets the screen's side padding).</summary>
    private double Layout(SketchNode n, double x, double y, double w, bool edge)
    {
        if (y > LogicalHeight + 200) return 0;   // far below the screen: not drawn
        double px = edge && !IsBand(n) && n.Kind is not (SketchKind.NavBar or SketchKind.List or SketchKind.Tile) ? Pad : 0;
        double ix = x + px, iw = Math.Max(10, w - 2 * px);
        switch (n.Kind)
        {
            case SketchKind.AppBar:
            {
                var r = new Rect(x, y, w, 56);
                Box(n, r);
                _paint.Add(dc => DrawAppBar(dc, n, r));
                return 56;
            }
            case SketchKind.Tabs:
            {
                var r = new Rect(ix, y, iw, 44);
                Box(n, r);
                _paint.Add(dc => DrawTabs(dc, n, r));
                return 44;
            }
            case SketchKind.NavBar:
            {
                var r = new Rect(x, y, w, 64);
                Box(n, r);
                _paint.Add(dc => DrawNav(dc, n, r));
                return 64;
            }
            case SketchKind.Column:
            case SketchKind.List:
            {
                double cy = y;
                bool list = n.Kind == SketchKind.List;
                int i = 0;
                foreach (var c in n.Children)
                {
                    if (c.Floating) continue;
                    double h = Layout(c, edge && list ? x : ix, cy, edge && list ? w : iw, edge && list);
                    if (h <= 0) continue;
                    cy += h;
                    if (list && n.Variant == "divided" && i++ < n.Children.Count - 1)
                    {
                        double ly = cy;
                        _paint.Add(dc => dc.DrawRectangle(_c.Outline, null, new Rect(ix, ly, iw, 1)));
                        cy += 1;
                    }
                    else cy += list && c.Kind == SketchKind.Tile ? 0 : Gap;
                }
                double total = Math.Max(0, cy - y - (n.Children.Count > 0 && !list ? Gap : 0));
                if (total > 0) Box(n, new Rect(ix, y, iw, total));
                return total;
            }
            case SketchKind.Row:
            case SketchKind.Wrap:
                return LayoutRow(n, ix, y, iw);
            case SketchKind.Stack:
            {
                // The children in the flow size the stack; Positioned ones take no room — a decoration (a glow, a
                // pattern image) is left out, a badge or a button is pinned to the top-right corner.
                double h = 0;
                // a backdrop (an image or a coloured box under the content) is left out when the stack has content
                bool anyContent = n.Children.Any(c => !c.Overlay && c.HasContent);
                foreach (var c in n.Children.Where(c => !c.Overlay && (!anyContent || c.HasContent))) h = Math.Max(h, Layout(c, ix, y, iw, false));
                foreach (var o in n.Children.Where(c => c.Overlay && c.HasContent))
                {
                    double ow = Intrinsic(o) is > 0 and var iwo ? iwo : Math.Min(iw, 140);
                    h = Math.Max(h, Layout(o, ix + iw - ow, y, ow, false) * (h > 0 ? 0 : 1));
                }
                if (h > 0) Box(n, new Rect(ix, y, iw, h));
                return h;
            }
            case SketchKind.Grid:
            {
                int cols = Math.Max(1, n.Columns);
                double cw = (iw - Gap * (cols - 1)) / cols;
                double cy = y, rowH = 0;
                for (int i = 0; i < n.Children.Count; i++)
                {
                    int col = i % cols;
                    if (col == 0 && i > 0) { cy += rowH + Gap; rowH = 0; }
                    rowH = Math.Max(rowH, Layout(n.Children[i], ix + col * (cw + Gap), cy, cw, false));
                }
                double total = cy + rowH - y;
                Box(n, new Rect(ix, y, iw, total));
                return total;
            }
            case SketchKind.Card:
            case SketchKind.Dialog:
            {
                double inner = 12;
                double cy = y + inner;
                int under = _paint.Count;   // the card is painted under everything drawn inside it
                if (n.Kind == SketchKind.Dialog && n.Text != null)
                {
                    double ty = cy;
                    _paint.Add(dc => DrawText(dc, n.Text, ix + inner, ty, iw - 2 * inner, 17, true, _c.OnBg));
                    cy += 28;
                }
                foreach (var c in n.Children) { double h = Layout(c, ix + inner, cy, iw - 2 * inner, false); if (h > 0) cy += h + Gap; }
                if (n.Children.Count > 0) cy -= Gap;
                double total = Math.Max(n.Height > 0 ? n.Height : 24, cy - y + inner);
                var r = new Rect(ix, y, iw, total);
                _paint.Insert(under, dc => dc.DrawRoundedRectangle(_c.Surface, n.Kind == SketchKind.Dialog || n.Variant != "box" ? _c.OutlinePen : null, r, 12, 12));
                Box(n, r);
                return total;
            }
            default:
                return LayoutLeaf(n, ix, y, iw);
        }
    }

    private double LayoutRow(SketchNode n, double x, double y, double w)
    {
        var kids = n.Children.Where(c => !c.Floating).ToList();
        if (kids.Count == 0) return 0;
        var widths = kids.Select(Intrinsic).ToList();
        int flex = widths.Count(v => v < 0);
        double fixedW = widths.Where(v => v >= 0).Sum() + Gap * (kids.Count - 1);
        double flexW = flex > 0 ? Math.Max(40, (w - fixedW) / flex) : 0;
        if (n.Kind == SketchKind.Wrap)
        {
            double cx = x, cy = y, lineH = 0;
            for (int i = 0; i < kids.Count; i++)
            {
                double cw = widths[i] < 0 ? Math.Min(w, 120) : Math.Min(w, widths[i]);
                if (cx + cw > x + w && cx > x) { cx = x; cy += lineH + Gap; lineH = 0; }
                lineH = Math.Max(lineH, Layout(kids[i], cx, cy, cw, false));
                cx += cw + Gap;
            }
            double total = cy + lineH - y;
            Box(n, new Rect(x, y, w, total));
            return total;
        }
        double h = 0, px = x;
        for (int i = 0; i < kids.Count; i++)
        {
            double cw = widths[i] < 0 ? flexW : widths[i];
            if (px >= x + w) break;
            cw = Math.Min(cw, x + w - px);
            h = Math.Max(h, Layout(kids[i], px, y, cw, false));
            px += cw + Gap;
        }
        if (h > 0) Box(n, new Rect(x, y, w, h));
        return h;
    }

    /// <summary>A block's natural width in a row, or -1 when it shares the row's free space.</summary>
    private double Intrinsic(SketchNode n) => n.Kind switch
    {
        SketchKind.Icon => 24,
        SketchKind.IconButton => 40,
        SketchKind.Avatar => 40,
        SketchKind.Toggle => 44,
        SketchKind.Progress when n.Variant != "linear" => 28,
        SketchKind.Chip => Math.Min(160, Measure(n.Text ?? "…", 12, false) + 26),
        SketchKind.Button => Math.Min(220, Measure(n.Text ?? "…", 13.5, true) + (n.Icon != null ? 52 : 36)),
        SketchKind.Spacer => Math.Min(24, n.Height),
        SketchKind.Badge => 24,
        _ => -1,
    };

    private double LayoutLeaf(SketchNode n, double x, double y, double w)
    {
        double h;
        Rect r;
        switch (n.Kind)
        {
            case SketchKind.Heading:
            {
                double size = 19;
                h = Lines(n.Text, w, size, true) > 1 ? 50 : 27;
                r = new Rect(x, y, w, h);
                _paint.Add(dc => DrawText(dc, n.Text ?? "…", r.X, r.Y, r.Width, size, true, _c.OnBg, maxLines: 2));
                break;
            }
            case SketchKind.Caption:
                h = 16;
                r = new Rect(x, y, w, h);
                _paint.Add(dc => DrawText(dc, n.Text ?? "…", r.X, r.Y, r.Width, 11.5, false, _c.Muted));
                break;
            case SketchKind.Text:
            {
                int lines = Lines(n.Text, w, 13.5, false);
                h = lines > 1 ? 38 : 20;
                r = new Rect(x, y, w, h);
                _paint.Add(dc => DrawText(dc, n.Text ?? "…", r.X, r.Y, r.Width, 13.5, false, _c.OnBg, maxLines: 2));
                break;
            }
            case SketchKind.Button:
                h = 42;
                r = new Rect(x, y, w, h);
                _paint.Add(dc => DrawButton(dc, n, r));
                break;
            case SketchKind.IconButton:
                h = 40;
                r = new Rect(x, y, Math.Min(w, 40), 40);
                _paint.Add(dc => DrawIconButton(dc, n, r));
                break;
            case SketchKind.Fab:
                h = 56;
                r = new Rect(x, y, Math.Min(w, 56), 56);
                _paint.Add(dc => DrawFab(dc, n, r));
                break;
            case SketchKind.Input:
                h = 50;
                r = new Rect(x, y, w, h);
                _paint.Add(dc => DrawInput(dc, n, r));
                break;
            case SketchKind.Toggle:
                h = 26;
                r = new Rect(x, y, Math.Min(w, 44), 26);
                _paint.Add(dc => DrawToggle(dc, r));
                break;
            case SketchKind.Slider:
                h = 24;
                r = new Rect(x, y, w, h);
                _paint.Add(dc =>
                {
                    dc.DrawRoundedRectangle(_c.Outline, null, new Rect(r.X, r.Y + 10, r.Width, 4), 2, 2);
                    dc.DrawRoundedRectangle(_c.Accent, null, new Rect(r.X, r.Y + 10, r.Width * 0.45, 4), 2, 2);
                    dc.DrawEllipse(_c.Accent, null, new Point(r.X + r.Width * 0.45, r.Y + 12), 9, 9);
                });
                break;
            case SketchKind.Chip:
                h = 32;
                r = new Rect(x, y, Math.Min(w, Intrinsic(n)), h);
                _paint.Add(dc => DrawChip(dc, n, r));
                break;
            case SketchKind.Icon:
                h = 24;
                r = new Rect(x, y, Math.Min(w, 24), 24);
                _paint.Add(dc => DrawGlyph(dc, n.Icon, new Point(r.X + 12, r.Y + 12), 20, _c.OnBg));
                break;
            case SketchKind.Badge:
                h = 20;
                r = new Rect(x, y, 20, 20);
                _paint.Add(dc => dc.DrawEllipse(_c.Accent, null, new Point(r.X + 10, r.Y + 10), 9, 9));
                break;
            case SketchKind.Image:
                h = n.Height > 0 ? Math.Clamp(n.Height, 24, 260) : 150;
                r = new Rect(x, y, w, h);
                if (n.Variant == "chart") _paint.Add(dc => DrawChart(dc, r));
                else _paint.Add(dc => DrawImage(dc, r));
                break;
            case SketchKind.Avatar:
                h = 40;
                r = new Rect(x, y, Math.Min(w, 40), 40);
                _paint.Add(dc =>
                {
                    dc.DrawEllipse(_c.Tint, null, new Point(r.X + 20, r.Y + 20), 20, 20);
                    if (!string.IsNullOrEmpty(n.Text)) DrawText(dc, n.Text.Length > 2 ? n.Text[..2] : n.Text, r.X, r.Y + 10, 40, 14, true, _c.Accent, center: true);
                    else DrawGlyph(dc, "person", new Point(r.X + 20, r.Y + 20), 18, _c.Accent);
                });
                break;
            case SketchKind.Tile:
                h = n.Text2 != null ? 66 : 54;
                r = new Rect(x, y, w, h);
                _paint.Add(dc => DrawTile(dc, n, r));
                break;
            case SketchKind.Table:
            {
                int cols = Math.Max(1, n.Columns);
                h = 36 * 4;
                r = new Rect(x, y, w, h);
                _paint.Add(dc => DrawTable(dc, n, r, cols));
                break;
            }
            case SketchKind.Divider:
                h = 9;
                r = new Rect(x, y, w, h);
                _paint.Add(dc => dc.DrawRectangle(_c.Outline, null, new Rect(r.X, r.Y + 4, r.Width, 1)));
                break;
            case SketchKind.Spacer:
                return Math.Max(0, n.Height - Gap);
            case SketchKind.Progress:
                if (n.Variant == "linear")
                {
                    h = 4;
                    r = new Rect(x, y, w, h);
                    _paint.Add(dc => { dc.DrawRoundedRectangle(_c.Outline, null, r, 2, 2); dc.DrawRoundedRectangle(_c.Accent, null, new Rect(r.X, r.Y, r.Width * 0.35, 4), 2, 2); });
                }
                else
                {
                    h = 28;
                    r = new Rect(x + (w - 28) / 2, y, 28, 28);
                    _paint.Add(dc => dc.DrawEllipse(null, new Pen(_c.Accent, 3), new Point(r.X + 14, r.Y + 14), 11, 11));
                }
                break;
            default:   // Box: a widget the sketch could not read
                h = 44;
                r = new Rect(x, y, w, h);
                _paint.Add(dc =>
                {
                    dc.DrawRoundedRectangle(null, _c.DashPen, r, 10, 10);
                    DrawText(dc, n.Text ?? n.Widget ?? "…", r.X + 10, r.Y + 13, r.Width - 20, 12, false, _c.Muted);
                });
                break;
        }
        Box(n, r);
        return h;
    }

    // ───────────────────────────── drawing ─────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        var (s, ox, oy) = Fit();
        dc.PushTransform(new TranslateTransform(ox, oy));
        dc.PushTransform(new ScaleTransform(s, s));
        var screen = new Rect(0, 0, ScreenWidth, LogicalHeight);
        dc.PushClip(new RectangleGeometry(screen, FullHeight ? 0 : 22, FullHeight ? 0 : 22));
        dc.DrawRectangle(_c.Bg, null, screen);
        if (Sketch != null)
        {
            // status bar
            DrawText(dc, "9:41", 18, 4, 60, 12, true, _c.OnBg);
            dc.DrawRoundedRectangle(_c.OnBg, null, new Rect(ScreenWidth - 44, 8, 22, 10), 3, 3);
            dc.DrawEllipse(_c.OnBg, null, new Point(ScreenWidth - 56, 13), 4, 4);
            foreach (var p in _paint) p(dc);
            if (Interactive)
            {
                if (_hover != null && Rect(_hover) is { } hr)
                {
                    dc.DrawRectangle(null, new Pen(_c.Accent, 1.5 / s) { DashStyle = DashStyles.Dash }, hr);
                    string label = _hover.Widget ?? _hover.Kind.ToString();
                    double lw = Measure(label, 10.5, true) + 12;
                    var lr = new Rect(Math.Min(hr.X, ScreenWidth - lw), Math.Max(0, hr.Y - 17), lw, 16);
                    dc.DrawRoundedRectangle(_c.Accent, null, lr, 4, 4);
                    DrawText(dc, label, lr.X + 6, lr.Y + 1.5, lw, 10.5, true, _c.OnAccent);
                }
                if (Selected != null && Rect(Selected) is { } sr) dc.DrawRectangle(null, new Pen(_c.Accent, 2.2 / s), sr);
            }
        }
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private Rect? Rect(SketchNode n)
    {
        foreach (var (node, box) in _boxes) if (ReferenceEquals(node, n)) return box;
        return null;
    }

    private void DrawAppBar(DrawingContext dc, SketchNode n, Rect r)
    {
        dc.DrawRectangle(_c.Bar, null, r);
        double tx = r.X + Pad;
        if (n.Variant == "back") { DrawGlyph(dc, "arrow_back", new Point(r.X + 26, r.Y + 28), 18, _c.OnBg); tx = r.X + 52; }
        double actions = n.ItemIcons.Count * 40;
        DrawText(dc, n.Text ?? "…", tx, r.Y + 15, r.Width - (tx - r.X) - actions - 12, 18, true, _c.OnBg);
        for (int i = 0; i < n.ItemIcons.Count; i++)
            DrawGlyph(dc, n.ItemIcons[i], new Point(r.Right - 24 - i * 40, r.Y + 28), 18, _c.OnBg);
    }

    private void DrawTabs(DrawingContext dc, SketchNode n, Rect r)
    {
        int count = Math.Max(1, Math.Min(n.Items.Count, 5));
        double cw = r.Width / count;
        for (int i = 0; i < count; i++)
        {
            bool sel = i == 0;
            DrawText(dc, n.Items[i], r.X + i * cw, r.Y + 12, cw, 13, sel, sel ? _c.Accent : _c.Muted, center: true);
            if (sel) dc.DrawRoundedRectangle(_c.Accent, null, new Rect(r.X + i * cw + cw * 0.2, r.Bottom - 3, cw * 0.6, 3), 1.5, 1.5);
        }
        dc.DrawRectangle(_c.Outline, null, new Rect(r.X, r.Bottom - 1, r.Width, 1));
    }

    private void DrawNav(DrawingContext dc, SketchNode n, Rect r)
    {
        dc.DrawRectangle(_c.Bar, null, r);
        dc.DrawRectangle(_c.Outline, null, new Rect(r.X, r.Y, r.Width, 1));
        int count = Math.Max(1, Math.Min(n.Items.Count, 6));
        double cw = r.Width / count;
        for (int i = 0; i < count; i++)
        {
            bool sel = i == 0;
            var color = sel ? _c.Accent : _c.Muted;
            if (sel) dc.DrawRoundedRectangle(_c.Tint, null, new Rect(r.X + i * cw + cw / 2 - 28, r.Y + 8, 56, 28), 14, 14);
            DrawGlyph(dc, n.ItemIcons.Count > i ? n.ItemIcons[i] : null, new Point(r.X + i * cw + cw / 2, r.Y + 22), 18, color);
            DrawText(dc, n.Items[i], r.X + i * cw, r.Y + 40, cw, 11, sel, color, center: true);
        }
    }

    private void DrawButton(DrawingContext dc, SketchNode n, Rect r)
    {
        var (fill, pen, fg) = n.Variant switch
        {
            "outlined" => ((Brush?)null, _c.OutlinePen, _c.Accent),
            "text" => (null, (Pen?)null, _c.Accent),
            "tonal" => (_c.Tint, null, _c.Accent),
            _ => (_c.Accent, null, _c.OnAccent),
        };
        dc.DrawRoundedRectangle(fill ?? Brushes.Transparent, pen, r, 21, 21);
        string text = n.Text ?? "";
        double tw = Measure(text, 13.5, true);
        double iconW = n.Icon != null ? 24 : 0;
        double start = r.X + Math.Max(8, (r.Width - tw - iconW) / 2);
        if (n.Icon != null) DrawGlyph(dc, n.Icon, new Point(start + 9, r.Y + r.Height / 2), 16, fg);
        if (text.Length > 0) DrawText(dc, text, start + iconW, r.Y + 11, r.Right - start - iconW - 8, 13.5, true, fg);
    }

    private void DrawIconButton(DrawingContext dc, SketchNode n, Rect r)
    {
        if (n.Variant == "filled") dc.DrawEllipse(_c.Tint, null, new Point(r.X + 20, r.Y + 20), 20, 20);
        DrawGlyph(dc, n.Icon, new Point(r.X + 20, r.Y + 20), 18, _c.OnBg);
    }

    private void DrawFab(DrawingContext dc, SketchNode n, Rect r)
    {
        dc.DrawRoundedRectangle(_c.AccentSoft, null, r, 16, 16);
        if (n.Text != null)
        {
            DrawGlyph(dc, n.Icon ?? "add", new Point(r.X + 24, r.Y + r.Height / 2), 18, _c.OnAccentSoft);
            DrawText(dc, n.Text, r.X + 42, r.Y + r.Height / 2 - 9, r.Width - 50, 14, true, _c.OnAccentSoft);
        }
        else DrawGlyph(dc, n.Icon ?? "add", new Point(r.X + r.Width / 2, r.Y + r.Height / 2), 22, _c.OnAccentSoft);
    }

    private void DrawInput(DrawingContext dc, SketchNode n, Rect r)
    {
        bool search = n.Variant == "search";
        dc.DrawRoundedRectangle(search ? _c.Surface : null, search ? null : _c.OutlinePen, r, search ? 25 : 8, search ? 25 : 8);
        double tx = r.X + 14;
        if (search) { DrawGlyph(dc, "search", new Point(r.X + 22, r.Y + r.Height / 2), 16, _c.Muted); tx = r.X + 40; }
        DrawText(dc, n.Text ?? (search ? "Search" : "…"), tx, r.Y + 15, r.Width - (tx - r.X) - 30, 13.5, false, _c.Muted);
        if (n.Variant == "dropdown") DrawGlyph(dc, "expand_more", new Point(r.Right - 18, r.Y + r.Height / 2), 14, _c.Muted);
        if (n.Variant == "password") DrawGlyph(dc, "visibility", new Point(r.Right - 20, r.Y + r.Height / 2), 14, _c.Muted);
    }

    private void DrawToggle(DrawingContext dc, Rect r)
    {
        dc.DrawRoundedRectangle(_c.Accent, null, r, 13, 13);
        dc.DrawEllipse(_c.OnAccent, null, new Point(r.Right - 13, r.Y + 13), 9, 9);
    }

    private void DrawChip(DrawingContext dc, SketchNode n, Rect r)
    {
        dc.DrawRoundedRectangle(null, _c.OutlinePen, r, 8, 8);
        DrawText(dc, n.Text ?? "…", r.X + 12, r.Y + 8, r.Width - 20, 12, false, _c.OnBg);
    }

    private void DrawImage(DrawingContext dc, Rect r)
    {
        dc.DrawRoundedRectangle(_c.Surface, null, r, 12, 12);
        DrawGlyph(dc, "image", new Point(r.X + r.Width / 2, r.Y + r.Height / 2), Math.Min(32, r.Height * 0.45), _c.Muted);
    }

    private void DrawChart(DrawingContext dc, Rect r)
    {
        dc.DrawRoundedRectangle(_c.Surface, null, r, 12, 12);
        double[] ys = { 0.62, 0.48, 0.55, 0.32, 0.4, 0.22, 0.3 };
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int i = 0; i < ys.Length; i++)
            {
                var p = new Point(r.X + 12 + i * (r.Width - 24) / (ys.Length - 1), r.Y + r.Height * ys[i]);
                if (i == 0) g.BeginFigure(p, false, false); else g.LineTo(p, true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(_c.Accent, 2.2) { LineJoin = PenLineJoin.Round }, geo);
        for (int i = 1; i <= 3; i++) dc.DrawRectangle(_c.Outline, null, new Rect(r.X + 12, r.Y + r.Height * i / 4.0, r.Width - 24, 1));
    }

    private void DrawTile(DrawingContext dc, SketchNode n, Rect r)
    {
        double tx = r.X + Pad;
        string v = n.Variant ?? "";
        if (n.Icon != null || v.StartsWith("avatar", StringComparison.Ordinal))
        {
            var c = new Point(r.X + Pad + 20, r.Y + r.Height / 2);
            dc.DrawEllipse(_c.Tint, null, c, 20, 20);
            DrawGlyph(dc, n.Icon ?? "person", c, 16, _c.Accent);
            tx = r.X + Pad + 56;
        }
        double trailing = v.Contains("+toggle", StringComparison.Ordinal) ? 60 : v.Contains("+chevron", StringComparison.Ordinal) ? 32 : 0;
        double tw = r.Right - Pad - trailing - tx;
        if (n.Text2 != null)
        {
            DrawText(dc, n.Text ?? "…", tx, r.Y + 12, tw, 14.5, false, _c.OnBg);
            DrawText(dc, n.Text2, tx, r.Y + 35, tw, 12, false, _c.Muted);
        }
        else DrawText(dc, n.Text ?? "…", tx, r.Y + r.Height / 2 - 10, tw, 14.5, false, _c.OnBg);
        if (v.Contains("+toggle", StringComparison.Ordinal)) DrawToggle(dc, new Rect(r.Right - Pad - 44, r.Y + r.Height / 2 - 13, 44, 26));
        else if (v.Contains("+chevron", StringComparison.Ordinal)) DrawGlyph(dc, "chevron_right", new Point(r.Right - Pad - 8, r.Y + r.Height / 2), 13, _c.Muted);
    }

    private void DrawTable(DrawingContext dc, SketchNode n, Rect r, int cols)
    {
        double cw = r.Width / cols;
        for (int row = 0; row < 4; row++)
        {
            double y = r.Y + row * 36;
            if (row == 0) dc.DrawRectangle(_c.Surface, null, new Rect(r.X, y, r.Width, 36));
            for (int c = 0; c < cols; c++)
            {
                string text = row == 0 ? (c < n.Items.Count ? n.Items[c] : "…") : "";
                if (row == 0) DrawText(dc, text, r.X + c * cw + 8, y + 10, cw - 12, 12, true, _c.OnBg);
                else dc.DrawRoundedRectangle(_c.Outline, null, new Rect(r.X + c * cw + 8, y + 15, Math.Max(8, cw * 0.55), 6), 3, 3);
            }
            dc.DrawRectangle(_c.Outline, null, new Rect(r.X, y + 35, r.Width, 1));
        }
    }

    // ───────────────────────────── text & glyphs ─────────────────────────────

    private double Dpi => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private FormattedText Format(string text, double size, bool bold, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? UiBold : Ui, size, brush, Dpi);

    private double Measure(string text, double size, bool bold) => Format(text, size, bold, Brushes.Black).WidthIncludingTrailingWhitespace;

    private int Lines(string? text, double width, double size, bool bold) =>
        text == null || width <= 0 ? 1 : Measure(text, size, bold) > width ? 2 : 1;

    private void DrawText(DrawingContext dc, string text, double x, double y, double width, double size, bool bold, Brush brush,
        int maxLines = 1, bool center = false)
    {
        if (width <= 4 || string.IsNullOrEmpty(text)) return;
        var ft = Format(text.Replace('\n', ' '), size, bold, brush);
        ft.MaxTextWidth = width;
        ft.MaxLineCount = maxLines;
        ft.Trimming = TextTrimming.CharacterEllipsis;
        if (center) ft.TextAlignment = TextAlignment.Center;
        dc.DrawText(ft, new Point(x, y));
    }

    private void DrawGlyph(DrawingContext dc, string? icon, Point center, double size, Brush brush)
    {
        string? g = IconGlyphs.For(icon);
        if (g == null)
        {
            dc.DrawEllipse(null, new Pen(brush, 1.6), center, size * 0.38, size * 0.38);
            return;
        }
        var ft = new FormattedText(g, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Glyphs, size, brush, Dpi);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }

    // ───────────────────────────── colours ─────────────────────────────

    private sealed class Palette
    {
        public Brush Bg = null!, Surface = null!, Bar = null!, Tint = null!, OnBg = null!, Muted = null!, Accent = null!, OnAccent = null!,
            AccentSoft = null!, OnAccentSoft = null!, Outline = null!;
        public Pen OutlinePen = null!, DashPen = null!;

        public static Palette From(SketchTheme t)
        {
            Brush B(uint argb) { var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)); b.Freeze(); return b; }
            uint muted = FlutterProject.Mix(t.OnBackground, t.Background, 0.42);
            uint outline = FlutterProject.Mix(t.OnBackground, t.Background, 0.8);
            uint soft = FlutterProject.Mix(t.Accent, t.Background, t.Dark ? 0.55 : 0.75);
            var p = new Palette
            {
                Bg = B(t.Background), Surface = B(t.Surface), Bar = B(FlutterProject.Mix(t.Background, t.Accent, t.Dark ? 0.10 : 0.06)),
                Tint = B(FlutterProject.Mix(t.Background, t.Accent, t.Dark ? 0.28 : 0.16)),
                OnBg = B(t.OnBackground), Muted = B(muted), Accent = B(t.Accent),
                OnAccent = B(FlutterProject.Luma(t.Accent) > 0.6 ? 0xFF1C1B1F : 0xFFFFFFFF),
                AccentSoft = B(soft), OnAccentSoft = B(FlutterProject.Luma(soft) > 0.55 ? 0xFF1C1B1F : 0xFFFFFFFF),
                Outline = B(outline),
            };
            p.OutlinePen = new Pen(p.Outline, 1.2);
            p.OutlinePen.Freeze();
            p.DashPen = new Pen(p.Muted, 1) { DashStyle = DashStyles.Dash };
            p.DashPen.Freeze();
            return p;
        }
    }
}

/// <summary>Icon names as code spells them (Material "arrow_back", Phosphor "magnifyingGlass", Cupertino "house")
/// → a Segoe Fluent / MDL2 glyph. Matched by the words in the name; null when nothing fits (drawn as a ring).</summary>
public static class IconGlyphs
{
    private static readonly (string[] Words, string Glyph)[] Map =
    {
        (new[] { "arrowback", "arrow_back", "backarrow", "caretleft", "chevronleft", "arrowleft", "back" }, ""),
        (new[] { "chevronright", "chevron_right", "caretright", "arrowright", "arrowforward", "arrow_forward", "forward", "next" }, ""),
        (new[] { "expandmore", "expand_more", "caretdown", "chevrondown", "arrowdropdown", "dropdown" }, ""),
        (new[] { "home", "house" }, ""),
        (new[] { "search", "magnifying", "find" }, ""),
        (new[] { "setting", "gear", "cog", "tune", "slider" }, ""),
        (new[] { "people", "group", "users", "usersthree", "team" }, ""),
        (new[] { "person", "user", "account", "profile", "face" }, ""),
        (new[] { "chat", "message", "forum", "comment", "sms", "bubble" }, ""),
        (new[] { "notification", "bell", "alarm" }, ""),
        (new[] { "add", "plus", "create", "new" }, ""),
        (new[] { "close", "clear", "cancel", "xcircle", "dismiss" }, ""),
        (new[] { "menu", "list", "hamburger", "rows" }, ""),
        (new[] { "more", "dots", "ellipsis", "overflow" }, ""),
        (new[] { "edit", "pencil", "pen", "write" }, ""),
        (new[] { "delete", "trash", "bin", "remove" }, ""),
        (new[] { "check", "done", "tick", "approve", "sealcheck" }, ""),
        (new[] { "star", "rating" }, ""),
        (new[] { "heart", "favorite", "like", "love" }, ""),
        (new[] { "camera", "photo_camera" }, ""),
        (new[] { "image", "photo", "picture", "gallery" }, ""),
        (new[] { "lock", "security", "shield", "password", "key" }, ""),
        (new[] { "qr", "scan", "barcode" }, ""),
        (new[] { "calendar", "date", "event", "schedule" }, ""),
        (new[] { "chart", "analytics", "graph", "trending", "insight", "stats" }, ""),
        (new[] { "cart", "shop", "store", "storefront", "bag", "basket", "market" }, ""),
        (new[] { "wallet", "payment", "card", "credit", "money", "cash", "coin", "bank", "receipt", "bill", "currency" }, ""),
        (new[] { "mail", "email", "envelope", "inbox", "tray" }, ""),
        (new[] { "phone", "call" }, ""),
        (new[] { "globe", "language", "web", "world", "public", "translate" }, ""),
        (new[] { "info", "about" }, ""),
        (new[] { "warning", "error", "alert", "report" }, ""),
        (new[] { "help", "question", "support" }, ""),
        (new[] { "folder", "file", "document", "description", "article", "note", "paper" }, ""),
        (new[] { "download" }, ""),
        (new[] { "upload", "share", "export" }, ""),
        (new[] { "refresh", "sync", "reload", "arrowclockwise", "replay" }, ""),
        (new[] { "play", "video", "movie" }, ""),
        (new[] { "logout", "signout", "exit", "power" }, ""),
        (new[] { "filter", "funnel", "sort" }, ""),
        (new[] { "grid", "apps", "dashboard", "squares", "category", "widget" }, ""),
        (new[] { "location", "map", "pin", "place", "navigation" }, ""),
        (new[] { "visibility", "eye", "view" }, ""),
        (new[] { "time", "clock", "history", "timer" }, ""),
        (new[] { "tag", "label", "ticket", "coupon", "discount" }, ""),
        (new[] { "gift", "reward", "prize" }, ""),
        (new[] { "sparkle", "magic", "robot", "ai", "brain" }, ""),
        (new[] { "copy", "content_copy" }, ""),
        (new[] { "send", "paperplane" }, ""),
        (new[] { "fingerprint", "biometric" }, ""),
        (new[] { "moon", "sun", "theme", "dark", "light" }, ""),
        (new[] { "truck", "delivery", "shipping", "motorcycle", "rider", "bike", "car" }, ""),
    };

    public static string? For(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        string n = icon.Replace("_", "").Replace("-", "").ToLowerInvariant();
        foreach (var (words, glyph) in Map)
            foreach (var w in words)
                if (n.Contains(w.Replace("_", ""), StringComparison.Ordinal)) return glyph;
        return null;
    }
}
