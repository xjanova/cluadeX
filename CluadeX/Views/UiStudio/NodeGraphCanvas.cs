using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services.UiStudio;

namespace CluadeX.Views.UiStudio;

/// <summary>
/// The Node Graph's drawing surface: nodes and wires drawn straight in OnRender (no element per node, so a few
/// thousand nodes stay smooth), culled to what is on screen, with pan (drag the background / middle button),
/// zoom at the cursor (wheel), node drag, a minimap and fit-to-view. Colours come from the theme tokens, so it
/// follows the app's theme.
/// </summary>
public sealed class NodeGraphCanvas : FrameworkElement
{
    public ProjectGraph? Graph { get; private set; }
    public GraphNode? Selected { get; private set; }
    public ISet<string> Highlights { get; set; } = new HashSet<string>();
    /// <summary>Nodes whose file changed lately (id → when, UTC): drawn with a fading glow.</summary>
    public IReadOnlyDictionary<string, DateTime>? Changed { get; set; }
    public TimeSpan ChangeGlow { get; set; } = TimeSpan.FromSeconds(4);
    private System.Windows.Threading.DispatcherTimer? _glowTimer;

    public event Action<GraphNode?>? NodeSelected;
    public event Action<GraphNode>? NodeActivated;
    public event Action<GraphNode>? NodeMoved;
    public event Action<GraphNode, Point>? NodeContextMenu;

    private Vector _pan = new(40, 40);
    private double _zoom = 1;
    private Point _lastMouse;
    private GraphNode? _dragNode;
    private Vector _dragOffset;
    private bool _panning, _moved;
    private GraphNode? _hover;
    private readonly Dictionary<string, (string Text, Brush Brush, FormattedText Ft)> _labels = new();
    private readonly Dictionary<string, (string Text, Brush Brush, FormattedText Ft)> _details = new();
    private const double MiniW = 180, MiniH = 120;

    public NodeGraphCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    public void SetGraph(ProjectGraph? graph, GraphNode? selected)
    {
        if (!ReferenceEquals(Graph, graph))
        {
            _labels.Clear();
            _details.Clear();
            _hover = null;
            // the graph was read again (files changed) while a node is being dragged: carry on with its new copy, where
            // the pointer has it now, instead of letting it jump back
            if (_dragNode != null)
                if (graph != null && graph.ById.TryGetValue(_dragNode.Id, out var again)) { again.X = _dragNode.X; again.Y = _dragNode.Y; _dragNode = again; }
                else _dragNode = null;
        }
        Graph = graph;
        Selected = selected;
        InvalidateVisual();
        if (AnyGlow()) StartGlow();
    }

    private bool AnyGlow()
    {
        if (Changed is not { Count: > 0 } changed || Graph == null) return false;
        var now = DateTime.UtcNow;
        return changed.Any(x => now - x.Value < ChangeGlow && Graph.ById.ContainsKey(x.Key));
    }

    /// <summary>Redraw at ~30 fps only while a glow is fading; the timer stops by itself after that.</summary>
    private void StartGlow()
    {
        if (_glowTimer == null)
        {
            _glowTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _glowTimer.Tick += (_, _) =>
            {
                if (!IsVisible || !AnyGlow()) _glowTimer!.Stop();
                InvalidateVisual();
            };
        }
        if (!_glowTimer.IsEnabled) _glowTimer.Start();
    }

    // ───────────────────────────── view transforms ─────────────────────────────

    private Point ToScreen(double x, double y) => new(x * _zoom + _pan.X, y * _zoom + _pan.Y);
    private Point ToWorld(Point p) => new((p.X - _pan.X) / _zoom, (p.Y - _pan.Y) / _zoom);

    private IEnumerable<GraphNode> Visible => Graph?.Nodes.Where(n => !n.Hidden) ?? Enumerable.Empty<GraphNode>();

    private Rect WorldBounds()
    {
        var nodes = Visible.ToList();
        if (nodes.Count == 0) return Rect.Empty;
        double l = nodes.Min(n => n.X), t = nodes.Min(n => n.Y), r = nodes.Max(n => n.X + n.Width), b = nodes.Max(n => n.Y + n.Height);
        return new Rect(l, t, r - l, b - t);
    }

    public void FitToView()
    {
        var b = WorldBounds();
        if (b.IsEmpty || ActualWidth < 10 || ActualHeight < 10) return;
        const double margin = 40;
        _zoom = Math.Clamp(Math.Min((ActualWidth - margin * 2) / Math.Max(1, b.Width), (ActualHeight - margin * 2) / Math.Max(1, b.Height)), 0.08, 1.4);
        _pan = new Vector(margin - b.X * _zoom + (ActualWidth - margin * 2 - b.Width * _zoom) / 2,
                          margin - b.Y * _zoom + (ActualHeight - margin * 2 - b.Height * _zoom) / 2);
        InvalidateVisual();
    }

    public void CenterOn(GraphNode n)
    {
        if (_zoom < 0.5) _zoom = 0.8;
        _pan = new Vector(ActualWidth / 2 - (n.X + n.Width / 2) * _zoom, ActualHeight / 2 - (n.Y + n.Height / 2) * _zoom);
        InvalidateVisual();
    }

    private GraphNode? HitNode(Point screen)
    {
        var w = ToWorld(screen);
        // last drawn is on top
        return Visible.LastOrDefault(n => w.X >= n.X && w.X <= n.X + n.Width && w.Y >= n.Y && w.Y <= n.Y + n.Height);
    }

    private Rect MiniRect => new(ActualWidth - MiniW - 12, ActualHeight - MiniH - 12, MiniW, MiniH);

    // ───────────────────────────── input ─────────────────────────────

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        var before = ToWorld(p);
        double factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        _zoom = Math.Clamp(_zoom * factor, 0.06, 3);
        _pan = new Vector(p.X - before.X * _zoom, p.Y - before.Y * _zoom);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        _lastMouse = p;
        _moved = false;

        if (e.ChangedButton == MouseButton.Left && Graph != null && MiniRect.Contains(p) && !WorldBounds().IsEmpty)
        {
            JumpFromMinimap(p);
            e.Handled = true;
            return;
        }

        var hit = HitNode(p);
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && hit != null)
        {
            NodeActivated?.Invoke(hit);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Right && hit != null)
        {
            Select(hit);
            NodeContextMenu?.Invoke(hit, p);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && hit != null)
        {
            Select(hit);
            _dragNode = hit;
            var w = ToWorld(p);
            _dragOffset = new Vector(w.X - hit.X, w.Y - hit.Y);
            CaptureMouse();
        }
        else if (e.ChangedButton is MouseButton.Left or MouseButton.Middle)
        {
            _panning = true;
            Cursor = Cursors.SizeAll;
            CaptureMouse();
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var delta = p - _lastMouse;
        if (_dragNode != null && e.LeftButton == MouseButtonState.Pressed)
        {
            if (delta.Length > 0.5) _moved = true;
            var w = ToWorld(p);
            _dragNode.X = Math.Round((w.X - _dragOffset.X) / 4) * 4;
            _dragNode.Y = Math.Round((w.Y - _dragOffset.Y) / 4) * 4;
            InvalidateVisual();
        }
        else if (_panning)
        {
            _pan += delta;
            _moved = true;
            InvalidateVisual();
        }
        else
        {
            var hit = HitNode(p);
            if (!ReferenceEquals(hit, _hover))
            {
                _hover = hit;
                Cursor = hit != null ? Cursors.Hand : null;
                InvalidateVisual();
            }
        }
        _lastMouse = p;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_dragNode != null && _moved) NodeMoved?.Invoke(_dragNode);
        if (_panning && !_moved && e.ChangedButton == MouseButton.Left) Select(null);   // click on empty space
        _dragNode = null;
        _panning = false;
        Cursor = _hover != null ? Cursors.Hand : null;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F) { FitToView(); e.Handled = true; }
        else if (e.Key == Key.Enter && Selected != null) { NodeActivated?.Invoke(Selected); e.Handled = true; }
        else if (e.Key == Key.Escape) { Select(null); e.Handled = true; }
    }

    private void Select(GraphNode? n)
    {
        if (ReferenceEquals(Selected, n)) return;
        Selected = n;
        NodeSelected?.Invoke(n);
        InvalidateVisual();
    }

    private void JumpFromMinimap(Point p)
    {
        var b = WorldBounds();
        var mr = MiniRect;
        double s = Math.Min((mr.Width - 8) / Math.Max(1, b.Width), (mr.Height - 8) / Math.Max(1, b.Height));
        double wx = b.X + (p.X - mr.X - 4) / s, wy = b.Y + (p.Y - mr.Y - 4) / s;
        _pan = new Vector(ActualWidth / 2 - wx * _zoom, ActualHeight / 2 - wy * _zoom);
        InvalidateVisual();
    }

    // ───────────────────────────── drawing ─────────────────────────────

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private static readonly Dictionary<GraphNodeKind, Color> KindColors = new()
    {
        [GraphNodeKind.Page] = Color.FromRgb(0xF5, 0x9E, 0x0B),
        [GraphNodeKind.Component] = Color.FromRgb(0x7C, 0x5C, 0xFF),
        [GraphNodeKind.Script] = Color.FromRgb(0x22, 0xD3, 0xEE),
        [GraphNodeKind.Style] = Color.FromRgb(0xF4, 0x72, 0xB6),
        [GraphNodeKind.Asset] = Color.FromRgb(0x94, 0xA3, 0xB8),
        [GraphNodeKind.Package] = Color.FromRgb(0x64, 0x74, 0x8B),
        [GraphNodeKind.Element] = Color.FromRgb(0x34, 0xD3, 0x99),
    };

    /// <summary>One colour per kind of connection — the wire keeps it whatever happens (selected, dragged, dimmed),
    /// so what links two nodes reads at a glance. The legend and the details panel use the same colours.</summary>
    public static readonly IReadOnlyDictionary<GraphEdgeKind, Color> EdgeColors = new Dictionary<GraphEdgeKind, Color>
    {
        [GraphEdgeKind.Imports] = Color.FromRgb(0x38, 0xBD, 0xF8),    // sky
        [GraphEdgeKind.Renders] = Color.FromRgb(0xA7, 0x8B, 0xFA),    // violet
        [GraphEdgeKind.Loads] = Color.FromRgb(0xFB, 0xBF, 0x24),      // amber
        [GraphEdgeKind.Links] = Color.FromRgb(0xA3, 0xE6, 0x35),      // lime
        [GraphEdgeKind.Contains] = Color.FromRgb(0x2D, 0xD4, 0xBF),   // teal
    };

    public static Color EdgeColor(GraphEdgeKind kind) => EdgeColors.TryGetValue(kind, out var c) ? c : Colors.Gray;

    private static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();
    private static SolidColorBrush Solid(Color c, byte alpha = 255)
    {
        var key = Color.FromArgb(alpha, c.R, c.G, c.B);
        if (!BrushCache.TryGetValue(key, out var b)) { b = new SolidColorBrush(key); b.Freeze(); BrushCache[key] = b; }
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bg = Res("Surface.Sunken", Brushes.Black);
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
        DrawGrid(dc);
        if (Graph == null) return;

        var text = Res("Text.Primary", Brushes.White);
        var muted = Res("Text.Muted", Brushes.Gray);
        var card = Res("Surface.Raised", Brushes.DimGray);
        var border = Res("Border.Default", Brushes.Gray);
        var accent = Res("Accent.1", Brushes.DeepSkyBlue);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var view = new Rect(ToWorld(new Point(0, 0)), ToWorld(new Point(ActualWidth, ActualHeight)));
        view.Inflate(40, 40);
        bool showText = _zoom >= 0.32;
        string? sel = Selected?.Id;

        // ── wires: coloured by the kind of connection ──
        // Wires of the selected (or dragged) node come forward — brighter, thicker, with a soft glow under them — in
        // their own colour; the rest fade back but keep theirs.
        var pens = new Dictionary<(GraphEdgeKind, int), Pen>();
        Pen PenFor(GraphEdgeKind kind, int state)   // 0 normal · 1 faded · 2 hot · 3 glow under hot
        {
            if (pens.TryGetValue((kind, state), out var p)) return p;
            var c = EdgeColor(kind);
            p = state switch
            {
                1 => new Pen(Solid(c, 50), Math.Max(1, 1 * _zoom)),
                2 => new Pen(Solid(c), Math.Max(1.6, (_dragNode != null ? 2.8 : 2.3) * _zoom)),
                3 => new Pen(Solid(c, 70), Math.Max(4, 7 * _zoom)),
                _ => new Pen(Solid(c, 165), Math.Max(1, 1.3 * _zoom)),
            };
            p.StartLineCap = p.EndLineCap = PenLineCap.Round;
            p.Freeze();
            pens[(kind, state)] = p;
            return p;
        }
        // the hot wires last, so they are drawn on top of the faded ones
        IEnumerable<GraphEdge> ordered = sel == null ? Graph.Edges : Graph.Edges.OrderBy(e => e.From == sel || e.To == sel);
        foreach (var e in ordered)
        {
            if (!Graph.ById.TryGetValue(e.From, out var a) || !Graph.ById.TryGetValue(e.To, out var b) || a.Hidden || b.Hidden) continue;
            var box = Rect.Union(new Rect(a.X, a.Y, a.Width, a.Height), new Rect(b.X, b.Y, b.Width, b.Height));
            if (!box.IntersectsWith(view)) continue;
            bool touches = sel != null && (e.From == sel || e.To == sel);
            var pen = PenFor(e.Kind, touches ? 2 : sel != null ? 1 : 0);

            var p0 = ToScreen(a.X + a.Width, a.Y + a.Height / 2);
            var p3 = ToScreen(b.X, b.Y + b.Height / 2);
            if (b.X < a.X + a.Width) { p0 = ToScreen(a.X + a.Width / 2, a.Y + a.Height); p3 = ToScreen(b.X + b.Width / 2, b.Y); }
            double dx = Math.Max(30 * _zoom, Math.Abs(p3.X - p0.X) * 0.5);
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(p0, false, false);
                bool vertical = Math.Abs(p3.X - p0.X) < 1 || b.X < a.X + a.Width;
                if (vertical)
                    ctx.BezierTo(new Point(p0.X, p0.Y + dx), new Point(p3.X, p3.Y - dx), p3, true, false);
                else
                    ctx.BezierTo(new Point(p0.X + dx, p0.Y), new Point(p3.X - dx, p3.Y), p3, true, false);
            }
            geo.Freeze();
            if (touches) dc.DrawGeometry(null, PenFor(e.Kind, 3), geo);
            dc.DrawGeometry(null, pen, geo);
            // arrow head
            if (_zoom > 0.25)
            {
                double s = 5 * Math.Max(0.6, _zoom);
                var head = new StreamGeometry();
                using (var ctx = head.Open())
                {
                    bool vertical = b.X < a.X + a.Width;
                    if (vertical)
                    {
                        ctx.BeginFigure(p3, true, true);
                        ctx.LineTo(new Point(p3.X - s, p3.Y - s * 1.6), false, false);
                        ctx.LineTo(new Point(p3.X + s, p3.Y - s * 1.6), false, false);
                    }
                    else
                    {
                        ctx.BeginFigure(p3, true, true);
                        ctx.LineTo(new Point(p3.X - s * 1.6, p3.Y - s), false, false);
                        ctx.LineTo(new Point(p3.X - s * 1.6, p3.Y + s), false, false);
                    }
                }
                head.Freeze();
                dc.DrawGeometry(pen.Brush, null, head);
            }
        }

        // ── nodes ──
        var selPen = new Pen(accent, 2); selPen.Freeze();
        var hlPen = new Pen(Solid(Color.FromRgb(0xFA, 0xCC, 0x15)), 2); hlPen.Freeze();
        var borderPen = new Pen(border, 1); borderPen.Freeze();
        var changeColor = Color.FromRgb(0x4A, 0xDE, 0x80);
        var now = DateTime.UtcNow;
        foreach (var n in Graph.Nodes)
        {
            if (n.Hidden) continue;
            var world = new Rect(n.X, n.Y, n.Width, n.Height);
            if (!world.IntersectsWith(view)) continue;
            var r = new Rect(ToScreen(n.X, n.Y), new Size(n.Width * _zoom, n.Height * _zoom));
            double radius = 8 * _zoom;
            var kindColor = KindColors.TryGetValue(n.Kind, out var kc) ? kc : Colors.Gray;
            bool isSel = n.Id == sel, isHl = Highlights.Contains(n.Id), isHover = ReferenceEquals(n, _hover);
            // just changed on disk (the AI, the editor): a green halo that fades out
            if (Changed != null && Changed.TryGetValue(n.Id, out var at) && now - at < ChangeGlow)
            {
                double t = 1 - (now - at).TotalMilliseconds / ChangeGlow.TotalMilliseconds;   // 1 → 0
                double pulse = 0.65 + 0.35 * Math.Sin((now - at).TotalMilliseconds / 140.0);
                var halo = r;
                halo.Inflate(5 * _zoom, 5 * _zoom);
                var haloPen = new Pen(Solid(changeColor, (byte)Math.Clamp(255 * t * pulse, 0, 255)), Math.Max(2, 3 * _zoom));
                dc.DrawRoundedRectangle(Solid(changeColor, (byte)Math.Clamp(40 * t, 0, 255)), haloPen, halo, radius + 5 * _zoom, radius + 5 * _zoom);
            }
            var fill = isHover ? Solid(kindColor, 38) : card;
            dc.DrawRoundedRectangle(fill, isSel ? selPen : isHl ? hlPen : borderPen, r, radius, radius);
            // kind stripe
            var stripe = new Rect(r.X, r.Y, Math.Max(2, 4 * _zoom), r.Height);
            dc.PushClip(new RectangleGeometry(r, radius, radius));
            dc.DrawRectangle(Solid(kindColor), null, stripe);
            dc.Pop();
            if (n.Collapsed)
            {
                double cs = 7 * _zoom;
                dc.DrawEllipse(Solid(kindColor), null, new Point(r.Right - cs * 1.6, r.Y + r.Height / 2), cs, cs);
            }

            if (!showText) continue;
            dc.PushClip(new RectangleGeometry(new Rect(r.X + 8 * _zoom, r.Y, Math.Max(1, r.Width - 16 * _zoom - (n.Collapsed ? 14 * _zoom : 0)), r.Height)));
            var label = Label(_labels, n.Id, n.Label, 12.5, FontWeights.SemiBold, text, dpi);
            var detail = n.Detail is { Length: > 0 } d ? Label(_details, n.Id, d, 10.5, FontWeights.Normal, muted, dpi) : null;
            dc.PushTransform(new TranslateTransform(r.X + 10 * _zoom, r.Y + (detail != null ? 5 : 12) * _zoom));
            dc.PushTransform(new ScaleTransform(_zoom, _zoom));
            dc.DrawText(label, new Point(0, 0));
            if (detail != null) dc.DrawText(detail, new Point(0, 18));
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }

        DrawMinimap(dc, accent, border);
    }

    private FormattedText Label(Dictionary<string, (string Text, Brush Brush, FormattedText Ft)> cache, string id, string s, double size, FontWeight weight, Brush brush, double dpi)
    {
        if (cache.TryGetValue(id, out var c) && c.Text == s && ReferenceEquals(c.Brush, brush)) return c.Ft;   // a theme switch hands new brushes
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Leelawadee UI"), FontStyles.Normal, weight, FontStretches.Normal), size, brush, dpi)
        { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, MaxTextWidth = 150 };
        cache[id] = (s, brush, ft);
        return ft;
    }

    private void DrawGrid(DrawingContext dc)
    {
        double step = 24 * _zoom;
        if (step < 9) return;
        var dot = Res("Border.Subtle", Brushes.DimGray);
        double ox = _pan.X % step, oy = _pan.Y % step;
        int count = 0;
        for (double x = ox; x < ActualWidth && count < 6000; x += step)
            for (double y = oy; y < ActualHeight && count < 6000; y += step, count++)
                dc.DrawRectangle(dot, null, new Rect(x, y, 1.2, 1.2));
    }

    private void DrawMinimap(DrawingContext dc, Brush accent, Brush border)
    {
        var b = WorldBounds();
        if (b.IsEmpty || ActualWidth < MiniW * 2.2) return;
        var mr = MiniRect;
        var panel = Res("Surface.Panel", Brushes.Black);
        dc.PushOpacity(0.92);
        dc.DrawRoundedRectangle(panel, new Pen(border, 1), mr, 8, 8);
        double s = Math.Min((mr.Width - 8) / Math.Max(1, b.Width), (mr.Height - 8) / Math.Max(1, b.Height));
        foreach (var n in Visible)
        {
            var kindColor = KindColors.TryGetValue(n.Kind, out var kc) ? kc : Colors.Gray;
            dc.DrawRectangle(Solid(kindColor, n.Id == Selected?.Id ? (byte)255 : (byte)150), null,
                new Rect(mr.X + 4 + (n.X - b.X) * s, mr.Y + 4 + (n.Y - b.Y) * s, Math.Max(1.5, n.Width * s), Math.Max(1.5, n.Height * s)));
        }
        var tl = ToWorld(new Point(0, 0));
        var br = ToWorld(new Point(ActualWidth, ActualHeight));
        var vr = new Rect(mr.X + 4 + (tl.X - b.X) * s, mr.Y + 4 + (tl.Y - b.Y) * s, (br.X - tl.X) * s, (br.Y - tl.Y) * s);
        vr.Intersect(mr);
        if (!vr.IsEmpty) dc.DrawRectangle(null, new Pen(accent, 1.2), vr);
        dc.Pop();
    }
}
