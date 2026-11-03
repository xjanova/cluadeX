using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services.UiStudio;

namespace CluadeX.Views.UiStudio;

/// <summary>
/// The Node Graph's drawing surface: nodes and wires drawn straight in OnRender (no element per node, so a few
/// thousand nodes stay smooth) — nodes culled to what is on screen, wires and node texts laid out once and kept
/// between frames (see <see cref="Wires"/>) — with pan (drag the background / middle button),
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
    /// <summary>A wire was clicked (null: clicked off every wire).</summary>
    public event Action<GraphEdge?>? EdgeSelected;
    /// <summary>A wire was double-clicked: open the line that made it.</summary>
    public event Action<GraphEdge>? EdgeActivated;

    /// <summary>Kinds of connection the user switched off in the filter.</summary>
    public ISet<GraphEdgeKind> HiddenKinds { get; set; } = new HashSet<GraphEdgeKind>();
    /// <summary>Draw the inferred (matched by name, dashed) wires too.</summary>
    public bool ShowInferred { get; set; } = true;
    /// <summary>Draw a frame behind the nodes of each folder.</summary>
    public bool GroupByFolder { get; set; }
    public GraphEdge? SelectedEdge { get; set; }

    // The wires, laid out in world space and kept between frames: a pan, a zoom step, a hover or a frame of the change
    // glow only moves the view, so it draws them under a transform. They are laid out again when the graph, a filter
    // or the selection changes, or a drag starts or ends (laying out ~2800 wires for every frame cost 20–40 ms a frame,
    // so panning, zooming and the glow stuttered). The wires of the selected or dragged node are re-read every frame.
    private WireSet? _wireSet;
    private bool _wiresStale = true;
    private readonly List<Wire> _hotWires = new();
    /// <summary>How many times the wires were laid out (the harness proves a pan, a zoom step or a glow frame lays out none).</summary>
    public int WireLayouts { get; private set; }
    /// <summary>How many frames were drawn (the harness proves an idle page draws none).</summary>
    public int Frames { get; private set; }

    private Vector _pan = new(40, 40);
    private double _zoom = 1;
    private Point _lastMouse;
    private GraphNode? _dragNode;
    private Vector _dragOffset;
    private bool _panning, _moved;
    private GraphNode? _hover;
    private readonly Dictionary<string, (string Text, Brush Brush, Drawing Ink)> _labels = new();
    private readonly Dictionary<string, (string Text, Brush Brush, Drawing Ink)> _details = new();
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
        _wiresStale = true;   // the same graph object may have new positions (layout, collapse) or filters
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

    private GraphEdge? HitEdge(Point screen)
    {
        if (_wireSet == null) return null;
        var w = ToWorld(screen);
        var pen = new Pen(Brushes.Black, 9 / _zoom);
        double slack = 5 / _zoom;
        // last drawn is on top; the curve's hull first (thousands of wires: the exact stroke test only where it can hit)
        bool Hit(Wire wire)
        {
            var box = wire.Hull;
            box.Inflate(slack, slack);
            if (!box.Contains(w)) return false;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open()) wire.Trace(ctx);
            return geo.StrokeContains(pen, w);
        }
        for (int i = _hotWires.Count - 1; i >= 0; i--) if (Hit(_hotWires[i])) return _hotWires[i].Edge;
        for (int g = _wireSet.Groups.Count - 1; g >= 0; g--)
        {
            var list = _wireSet.Groups[g].Wires;
            for (int i = list.Count - 1; i >= 0; i--) if (Hit(list[i])) return list[i].Edge;
        }
        return null;
    }

    /// <summary>The wires the filters let through, between two visible nodes.</summary>
    private IEnumerable<GraphEdge> VisibleEdges()
    {
        if (Graph == null) yield break;
        foreach (var e in Graph.Edges)
        {
            if (HiddenKinds.Contains(e.Kind) || e.Inferred && !ShowInferred) continue;
            if (!Graph.ById.TryGetValue(e.From, out var a) || !Graph.ById.TryGetValue(e.To, out var b) || a.Hidden || b.Hidden) continue;
            yield return e;
        }
    }

    /// <summary>
    /// Where each wire leaves and enters its nodes (0..1 along the side): the wires of one node are spread over its
    /// side in the order of the nodes at their other end, so they leave side by side instead of piling into one point,
    /// and two kinds between the same pair run as parallel wires.
    /// </summary>
    private Dictionary<GraphEdge, (double From, double To)> Ports(List<GraphEdge> edges)
    {
        var result = new Dictionary<GraphEdge, (double, double)>(edges.Count);
        var outs = new Dictionary<string, List<GraphEdge>>();
        var ins = new Dictionary<string, List<GraphEdge>>();
        foreach (var e in edges)
        {
            (outs.TryGetValue(e.From, out var o) ? o : outs[e.From] = new()).Add(e);
            (ins.TryGetValue(e.To, out var i) ? i : ins[e.To] = new()).Add(e);
            result[e] = (0.5, 0.5);
        }
        foreach (var list in outs.Values.Where(l => l.Count > 1))
        {
            var sorted = list.OrderBy(e => Graph!.ById[e.To].Y).ThenBy(e => e.To).ThenBy(e => e.Kind).ToList();
            for (int k = 0; k < sorted.Count; k++) result[sorted[k]] = ((k + 1.0) / (sorted.Count + 1), result[sorted[k]].Item2);
        }
        foreach (var list in ins.Values.Where(l => l.Count > 1))
        {
            var sorted = list.OrderBy(e => Graph!.ById[e.From].Y).ThenBy(e => e.From).ThenBy(e => e.Kind).ToList();
            for (int k = 0; k < sorted.Count; k++) result[sorted[k]] = (result[sorted[k]].Item1, (k + 1.0) / (sorted.Count + 1));
        }
        return result;
    }

    /// <summary>A wire's curve in world space: where it leaves, where it enters, and whether it runs down or across.</summary>
    private readonly record struct Wire(GraphEdge Edge, Point From, Point To, bool Vertical)
    {
        private double Bend => Math.Max(30, Math.Abs(To.X - From.X) * 0.5);
        private Point C1 => Vertical ? new Point(From.X, From.Y + Bend) : new Point(From.X + Bend, From.Y);
        private Point C2 => Vertical ? new Point(To.X, To.Y - Bend) : new Point(To.X - Bend, To.Y);

        public void Trace(StreamGeometryContext ctx)
        {
            ctx.BeginFigure(From, false, false);
            ctx.BezierTo(C1, C2, To, true, false);
        }

        /// <summary>The arrow head at the far end, <paramref name="s"/> world units wide.</summary>
        public void Head(StreamGeometryContext ctx, double s)
        {
            ctx.BeginFigure(To, true, true);
            if (Vertical)
            {
                ctx.LineTo(new Point(To.X - s, To.Y - s * 1.6), false, false);
                ctx.LineTo(new Point(To.X + s, To.Y - s * 1.6), false, false);
            }
            else
            {
                ctx.LineTo(new Point(To.X - s * 1.6, To.Y - s), false, false);
                ctx.LineTo(new Point(To.X - s * 1.6, To.Y + s), false, false);
            }
        }

        /// <summary>The curve lies inside the box of its four control points.</summary>
        public Rect Hull
        {
            get
            {
                var r = new Rect(From, To);
                r.Union(C1);
                r.Union(C2);
                return r;
            }
        }
    }

    /// <summary>One kind of wire (solid or dashed) drawn as a single geometry, with its arrow heads for one head size.</summary>
    private sealed class WireGroup(GraphEdgeKind kind, bool dashed, StreamGeometry lines, List<Wire> wires)
    {
        public GraphEdgeKind Kind { get; } = kind;
        public bool Dashed { get; } = dashed;
        public StreamGeometry Lines { get; } = lines;
        public List<Wire> Wires { get; } = wires;
        public StreamGeometry? Heads;
        public double HeadSize = double.NaN;
    }

    /// <summary>The wires as laid out for one graph, filter and selection: the cold ones in groups, the hot ones (on
    /// the selected or dragged node, or the selected wire) by edge, and where every wire meets its nodes.</summary>
    private sealed class WireSet
    {
        public ProjectGraph? Graph;
        public string? Sel, Drag;
        public GraphEdge? SelEdge;
        public bool Inferred;
        public HashSet<GraphEdgeKind> Hidden = new();
        public Dictionary<GraphEdge, (double From, double To)> Ports = new();
        public readonly List<WireGroup> Groups = new();
        public readonly List<GraphEdge> Hot = new();
    }

    private Wire MakeWire(GraphEdge e, (double From, double To) port)
    {
        var a = Graph!.ById[e.From];
        var b = Graph.ById[e.To];
        bool vertical = b.X < a.X + a.Width;
        var p0 = vertical ? new Point(a.X + a.Width * port.From, a.Y + a.Height) : new Point(a.X + a.Width, a.Y + a.Height * port.From);
        var p3 = vertical ? new Point(b.X + b.Width * port.To, b.Y) : new Point(b.X, b.Y + b.Height * port.To);
        return new Wire(e, p0, p3, vertical);
    }

    /// <summary>The wires for this frame: the kept ones unless what they depend on changed since they were laid out.
    /// A drag keeps the ports where they were until the node is dropped (then they are laid out again).</summary>
    private WireSet Wires(string? sel, GraphEdge? selEdge)
    {
        string? drag = _dragNode?.Id;
        var set = _wireSet;
        if (set != null && !_wiresStale && ReferenceEquals(set.Graph, Graph) && set.Sel == sel && set.Drag == drag && ReferenceEquals(set.SelEdge, selEdge)
            && set.Inferred == ShowInferred && set.Hidden.SetEquals(HiddenKinds))
            return set;
        _wiresStale = false;
        WireLayouts++;
        set = _wireSet = new WireSet { Graph = Graph, Sel = sel, Drag = drag, SelEdge = selEdge, Inferred = ShowInferred, Hidden = new(HiddenKinds) };
        var edges = VisibleEdges().ToList();
        set.Ports = Ports(edges);
        var groups = new Dictionary<(GraphEdgeKind, bool), List<Wire>>();
        foreach (var e in edges)
        {
            if (ReferenceEquals(e, selEdge) || sel != null && (e.From == sel || e.To == sel) || drag != null && (e.From == drag || e.To == drag)) { set.Hot.Add(e); continue; }
            (groups.TryGetValue((e.Kind, e.Inferred), out var list) ? list : groups[(e.Kind, e.Inferred)] = new()).Add(MakeWire(e, set.Ports[e]));
        }
        foreach (var ((kind, dashed), list) in groups)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open()) foreach (var w in list) w.Trace(ctx);
            geo.Freeze();
            set.Groups.Add(new WireGroup(kind, dashed, geo, list));
        }
        return set;
    }

    /// <summary>A soft frame around the nodes of each folder, the folder's name on top.</summary>
    private void DrawGroups(DrawingContext dc, Brush muted, Brush border, double dpi)
    {
        var fill = Res("Surface.Inset", Brushes.Transparent);
        var pen = new Pen(border, 1) { DashStyle = DashStyles.Dash };
        pen.Freeze();
        foreach (var group in Visible.Where(n => n.Kind != GraphNodeKind.Package).GroupBy(n => n.Folder))
        {
            var nodes = group.ToList();
            double l = nodes.Min(n => n.X) - 14, t = nodes.Min(n => n.Y) - 30, r = nodes.Max(n => n.X + n.Width) + 14, b = nodes.Max(n => n.Y + n.Height) + 14;
            var rect = new Rect(ToScreen(l, t), new Size((r - l) * _zoom, (b - t) * _zoom));
            if (rect.Right < 0 || rect.Bottom < 0 || rect.Left > ActualWidth || rect.Top > ActualHeight) continue;
            dc.DrawRoundedRectangle(fill, pen, rect, 10 * _zoom, 10 * _zoom);
            if (_zoom < 0.25) continue;
            var ft = new FormattedText(group.Key.Length == 0 ? "/" : group.Key + "/", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI, Leelawadee UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 11 * _zoom, muted, dpi)
            { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, MaxTextWidth = Math.Max(10, rect.Width - 16 * _zoom) };
            dc.DrawText(ft, new Point(rect.X + 10 * _zoom, rect.Y + 7 * _zoom));
        }
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
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && HitEdge(p) is { } wire)
        {
            EdgeActivated?.Invoke(wire);
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
        if (_dragNode != null) InvalidateVisual();   // dropped: the wires are laid out again around where it landed
        if (_panning && !_moved && e.ChangedButton == MouseButton.Left)
        {
            // a click (no drag) off the nodes: on a wire selects the wire, on empty space clears the selection
            var wire = HitEdge(e.GetPosition(this));
            Select(null);
            SelectedEdge = wire;
            EdgeSelected?.Invoke(wire);
            InvalidateVisual();
        }
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
        [GraphNodeKind.Code] = Color.FromRgb(0x60, 0xA5, 0xFA),
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
        [GraphEdgeKind.Calls] = Color.FromRgb(0xFB, 0x92, 0x3C),      // orange
        [GraphEdgeKind.Inherits] = Color.FromRgb(0xF4, 0x3F, 0x5E),   // rose
        [GraphEdgeKind.Implements] = Color.FromRgb(0xE8, 0x79, 0xF9), // fuchsia
        [GraphEdgeKind.Routes] = Color.FromRgb(0x22, 0xC5, 0x5E),     // green
        [GraphEdgeKind.Registers] = Color.FromRgb(0xFD, 0xE0, 0x47),  // yellow
        [GraphEdgeKind.Binds] = Color.FromRgb(0x81, 0x8C, 0xF8),      // indigo
        [GraphEdgeKind.CodeBehind] = Color.FromRgb(0xE2, 0xE8, 0xF0), // pale slate
        [GraphEdgeKind.References] = Color.FromRgb(0x94, 0xA3, 0xB8), // slate
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
        Frames++;
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

        // ── folder groups: a frame behind the nodes of each folder ──
        if (GroupByFolder) DrawGroups(dc, muted, border, dpi);

        // ── wires: coloured by the kind of connection, dashed when inferred ──
        // Wires of the selected (or dragged) node, or the selected wire, come forward — brighter, thicker, with a soft
        // glow under them — in their own colour; the rest fade back but keep theirs. They are drawn in world space under
        // the view's transform, so a pen's thickness is divided by the zoom to stay the same on screen.
        var pens = new Dictionary<(GraphEdgeKind, int, bool), Pen>();
        Pen PenFor(GraphEdgeKind kind, int state, bool dashed)   // 0 normal · 1 faded · 2 hot · 3 glow under hot
        {
            if (pens.TryGetValue((kind, state, dashed), out var p)) return p;
            var c = EdgeColor(kind);
            p = state switch
            {
                1 => new Pen(Solid(c, 50), Math.Max(1, 1 * _zoom) / _zoom),
                2 => new Pen(Solid(c), Math.Max(1.6, (_dragNode != null ? 2.8 : 2.3) * _zoom) / _zoom),
                3 => new Pen(Solid(c, 70), Math.Max(4, 7 * _zoom) / _zoom),
                _ => new Pen(Solid(c, 165), Math.Max(1, 1.3 * _zoom) / _zoom),
            };
            p.StartLineCap = p.EndLineCap = PenLineCap.Round;
            if (dashed && state != 3) p.DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0);
            p.Freeze();
            pens[(kind, state, dashed)] = p;
            return p;
        }
        GraphEdge? selEdge = SelectedEdge;
        bool anyFocus = sel != null || selEdge != null;
        var wires = Wires(sel, selEdge);
        bool heads = _zoom > 0.25;
        double headSize = 5 * Math.Max(0.6, _zoom) / _zoom;
        _hotWires.Clear();
        foreach (var e in wires.Hot) _hotWires.Add(MakeWire(e, wires.Ports[e]));
        dc.PushTransform(new MatrixTransform(_zoom, 0, 0, _zoom, _pan.X, _pan.Y));
        foreach (var group in wires.Groups)
        {
            var pen = PenFor(group.Kind, anyFocus ? 1 : 0, group.Dashed);
            dc.DrawGeometry(null, pen, group.Lines);
            if (!heads) continue;
            if (group.Heads == null || group.HeadSize != headSize)   // the heads keep their size on screen below zoom 0.6
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open()) foreach (var w in group.Wires) w.Head(ctx, headSize);
                geo.Freeze();
                group.Heads = geo;
                group.HeadSize = headSize;
            }
            dc.DrawGeometry(pen.Brush, null, group.Heads);
        }
        // the hot wires last, so they are drawn on top of the faded ones
        foreach (var w in _hotWires)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open()) w.Trace(ctx);
            geo.Freeze();
            var pen = PenFor(w.Edge.Kind, 2, w.Edge.Inferred);
            dc.DrawGeometry(null, PenFor(w.Edge.Kind, 3, false), geo);
            dc.DrawGeometry(null, pen, geo);
            if (!heads) continue;
            var head = new StreamGeometry();
            using (var ctx = head.Open()) w.Head(ctx, headSize);
            head.Freeze();
            dc.DrawGeometry(pen.Brush, null, head);
        }
        dc.Pop();

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
            var label = Label(_labels, n.Id, n.Label, 12.5, FontWeights.SemiBold, text, dpi, 0);
            var detail = n.Detail is { Length: > 0 } d ? Label(_details, n.Id, d, 10.5, FontWeights.Normal, muted, dpi, 18) : null;
            dc.PushTransform(new TranslateTransform(r.X + 10 * _zoom, r.Y + (detail != null ? 5 : 12) * _zoom));
            dc.PushTransform(new ScaleTransform(_zoom, _zoom));
            dc.DrawDrawing(label);
            if (detail != null) dc.DrawDrawing(detail);
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }

        DrawMinimap(dc, accent, border);
    }

    /// <summary>A node's text, laid out once and kept as glyphs: drawing a FormattedText lays its line out again every
    /// time, which cost more than the whole rest of a zoomed-in frame.</summary>
    private Drawing Label(Dictionary<string, (string Text, Brush Brush, Drawing Ink)> cache, string id, string s, double size, FontWeight weight, Brush brush, double dpi, double y)
    {
        if (cache.TryGetValue(id, out var c) && c.Text == s && ReferenceEquals(c.Brush, brush)) return c.Ink;   // a theme switch hands new brushes
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Leelawadee UI"), FontStyles.Normal, weight, FontStretches.Normal), size, brush, dpi)
        { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, MaxTextWidth = 150 };
        var ink = new DrawingGroup();
        using (var dc = ink.Open()) dc.DrawText(ft, new Point(0, y));
        ink.Freeze();
        cache[id] = (s, brush, ink);
        return ink;
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
