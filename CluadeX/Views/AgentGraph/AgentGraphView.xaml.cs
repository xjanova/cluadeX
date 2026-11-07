using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using CluadeX.Models;
using CluadeX.Services.Agents;

namespace CluadeX.Views.AgentGraph;

/// <summary>
/// The agent map card's canvas (DataContext = the map's <see cref="ChatMessage"/>). Where things sit comes from
/// <see cref="AgentGraphLayout"/>, the zoom and pan from the row's <see cref="AgentGraphState"/>; this class only makes
/// the visuals and turns the mouse into those calls. The pointer methods are public so the harness drives the same
/// path the mouse does (tests/chattest AgentGraphTests).
/// </summary>
public partial class AgentGraphView : UserControl
{
    public const double Pad = 24, MinCanvasHeight = 210, MaxCanvasHeight = 400;
    /// <summary>A press that moves less than this (screen px) is a click, not a drag.</summary>
    public const double ClickSlop = 4;
    private const double PulseThickness = 3, PulseLength = 30;

    private ChatMessage? _map;
    private ObservableCollection<AgentNode>? _nodes;
    private readonly List<NodeVisual> _visuals = new();
    private ContentControl? _rootEl;
    private Ellipse? _rootPort;
    private AgentGraphScene? _scene;

    private enum DragKind { None, Pan, Node, Root }
    private DragKind _drag;
    private Point _down, _last, _dragFrom;
    private AgentNode? _pressed;
    private bool _moved;

    /// <summary>A node was clicked (after the window opened for it).</summary>
    public event Action<AgentNode>? NodeClicked;

    public AgentGraphView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as ChatMessage);
        Loaded += (_, _) => Bind(DataContext as ChatMessage);
        Unloaded += (_, _) => Bind(null);
        Viewport.SizeChanged += (_, e) => OnViewSize(e.NewSize);
        Viewport.MouseLeftButtonDown += (_, e) =>
        {
            PointerDown(e.GetPosition(Viewport), null);
            Viewport.CaptureMouse();
            e.Handled = true;
        };
        Viewport.MouseMove += (_, e) => { if (_drag != DragKind.None) PointerMove(e.GetPosition(Viewport)); };
        Viewport.MouseLeftButtonUp += (_, e) =>
        {
            if (_drag == DragKind.None) return;
            // the click is decided before the capture goes (losing it cancels a press)
            PointerUp(e.GetPosition(Viewport));
            Viewport.ReleaseMouseCapture();
            e.Handled = true;
        };
        Viewport.LostMouseCapture += (_, _) => { if (_drag != DragKind.None) PointerCancel(); };
        Viewport.MouseWheel += (_, e) => { if (WheelAt(e.GetPosition(Viewport), e.Delta)) e.Handled = true; };
        Viewport.Background = DotGrid();
    }

    // ─── what the harness (and the mouse) calls ───

    public AgentGraphState? State => _map?.AgentGraph;
    public GraphViewport? View => _map?.AgentGraph.Viewport;
    public AgentGraphScene? Scene => _scene;
    /// <summary>A node's element on the canvas (null: not on this canvas).</summary>
    public FrameworkElement? ElementFor(AgentNode node) => _visuals.FirstOrDefault(v => ReferenceEquals(v.Node, node))?.Element;
    public Path? EdgeFor(AgentNode node) => _visuals.FirstOrDefault(v => ReferenceEquals(v.Node, node))?.Edge;
    public Path? PulseFor(AgentNode node) => _visuals.FirstOrDefault(v => ReferenceEquals(v.Node, node))?.Pulse;
    public FrameworkElement? RootElement => _rootEl;

    /// <summary>Press at a viewport point: on a node (drag it, or a click), on the main agent (drag it), else the background (pan).</summary>
    public void PointerDown(Point screen, AgentNode? node, bool onRoot = false)
    {
        if (_map == null || _scene == null) return;
        _down = _last = screen;
        _moved = false;
        _pressed = node;
        if (node != null)
        {
            int i = IndexOf(node);
            if (i < 0) { _drag = DragKind.None; return; }
            _drag = DragKind.Node;
            _dragFrom = _scene.Nodes[i].TopLeft;
        }
        else if (onRoot) { _drag = DragKind.Root; _dragFrom = _scene.Root.TopLeft; }
        else _drag = DragKind.Pan;
    }

    public void PointerMove(Point screen)
    {
        if (_drag == DragKind.None || _map == null || _scene == null) return;
        if (!_moved && (screen - _down).Length < ClickSlop) return;
        _moved = true;
        var st = _map.AgentGraph;
        var vp = st.Viewport;
        switch (_drag)
        {
            case DragKind.Pan:
                vp.PanBy(screen.X - _last.X, screen.Y - _last.Y, _scene.Bounds);
                st.UserMoved = true;
                ApplyView();
                break;
            case DragKind.Node when _pressed != null:
                st.Manual[_pressed.Id] = _dragFrom + (screen - _down) / vp.Zoom;
                st.UserMoved = true;
                Relayout();
                break;
            case DragKind.Root:
                st.Manual[AgentGraphLayout.RootId] = _dragFrom + (screen - _down) / vp.Zoom;
                st.UserMoved = true;
                Relayout();
                break;
        }
        _last = screen;
    }

    /// <summary>Release. True when it was a click on a node — its window opened.</summary>
    public bool PointerUp(Point screen)
    {
        PointerMove(screen);
        bool click = !_moved && _drag == DragKind.Node && _pressed != null;
        var node = _pressed;
        _drag = DragKind.None;
        _pressed = null;
        if (click && node != null) Open(node);
        return click;
    }

    private void PointerCancel() { _drag = DragKind.None; _pressed = null; }

    /// <summary>The wheel at a viewport point (delta 120 = one notch). False at the zoom's limit: the chat scrolls instead.</summary>
    public bool WheelAt(Point screen, int delta)
    {
        if (_map == null || _scene == null || delta == 0) return false;
        if (!_map.AgentGraph.Viewport.Wheel(screen, delta, _scene.Bounds)) return false;
        _map.AgentGraph.UserMoved = true;
        ApplyView();
        return true;
    }

    public void ZoomBy(double factor)
    {
        if (_map == null || _scene == null) return;
        var vp = _map.AgentGraph.Viewport;
        if (vp.ZoomAt(new Point(vp.ViewSize.Width / 2, vp.ViewSize.Height / 2), factor, _scene.Bounds)) _map.AgentGraph.UserMoved = true;
        ApplyView();
    }

    /// <summary>Everything in view; new nodes keep being fitted until the owner moves the view again.</summary>
    public void FitView()
    {
        if (_map == null || _scene == null) return;
        _map.AgentGraph.Viewport.Fit(_scene.Bounds, Pad);
        _map.AgentGraph.UserMoved = false;
        ApplyView();
    }

    /// <summary>The automatic layout again: dragged nodes go back, then a fit.</summary>
    public void ResetLayout()
    {
        if (_map == null) return;
        _map.AgentGraph.Manual.Clear();
        _map.AgentGraph.UserMoved = false;
        Relayout();
    }

    public void Open(AgentNode node)
    {
        AgentDetailWindow.Open(node, Window.GetWindow(this));
        NodeClicked?.Invoke(node);
    }

    // ─── binding ───

    private void Bind(ChatMessage? map)
    {
        if (map != null && !IsLoaded) map = null;   // made but not on screen yet: Loaded binds
        if (ReferenceEquals(map, _map) && ReferenceEquals(map?.AgentNodes, _nodes)) return;
        if (_nodes != null) _nodes.CollectionChanged -= OnNodesChanged;
        foreach (var v in _visuals) v.Node.PropertyChanged -= OnNodeChanged;
        _visuals.Clear();
        NodeLayer.Children.Clear();
        EdgeLayer.Children.Clear();
        _rootEl = null;
        _rootPort = null;
        _scene = null;
        _drag = DragKind.None;
        _map = map;
        _nodes = map?.AgentNodes;
        if (_map == null || _nodes == null) return;
        _nodes.CollectionChanged += OnNodesChanged;

        _rootPort = new Ellipse { Width = 7, Height = 7, IsHitTestVisible = false };
        _rootPort.SetResourceReference(Shape.FillProperty, "Accent.1");
        _rootEl = new ContentControl
        {
            Content = _map, ContentTemplate = (DataTemplate)FindResource("RootCard"), Focusable = false, Cursor = Cursors.SizeAll,
        };
        _rootEl.MouseLeftButtonDown += (_, e) =>
        {
            PointerDown(e.GetPosition(Viewport), null, onRoot: true);
            Viewport.CaptureMouse();
            e.Handled = true;
        };
        foreach (var n in _nodes) AddVisual(n);
        NodeLayer.Children.Add(_rootEl);
        EdgeLayer.Children.Add(_rootPort);
        _map.AgentGraph.Viewport.ViewSize = Viewport.RenderSize;
        Relayout();
    }

    private void OnNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            foreach (AgentNode n in e.NewItems) AddVisual(n);
            Relayout();
            return;
        }
        var map = _map;
        Bind(null);
        Bind(map);
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AgentNode.State) || sender is not AgentNode node) return;
        var v = _visuals.FirstOrDefault(x => ReferenceEquals(x.Node, node));
        if (v != null) StyleEdge(v);
    }

    private void AddVisual(AgentNode node)
    {
        var el = new ContentControl
        {
            Content = node, ContentTemplate = (DataTemplate)FindResource("NodeCard"), Focusable = true, Cursor = Cursors.Hand,
            FocusVisualStyle = null,
        };
        AutomationProperties.SetName(el, node.Label);
        el.MouseLeftButtonDown += (_, e) =>
        {
            // no Focus() here: focusing a row makes the chat list scroll it into view, and the press would read as a drag
            PointerDown(e.GetPosition(Viewport), node);
            Viewport.CaptureMouse();
            e.Handled = true;
        };
        el.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space) { Open(node); e.Handled = true; }
        };
        var edge = new Path { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var pulse = new Path
        {
            StrokeThickness = PulseThickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeDashCap = PenLineCap.Round,
        };
        pulse.SetResourceReference(Shape.StrokeProperty, "Accent.1");
        // The light running from the main agent to it: only while it works and the canvas is on screen (Motion).
        pulse.SetBinding(VisibilityProperty, new Binding(nameof(AgentNode.IsRunning)) { Source = node, Converter = new BooleanToVisibilityConverter() });
        pulse.SetBinding(Motion.LoopOnProperty, new Binding(nameof(AgentNode.IsRunning)) { Source = node });
        Motion.SetLoop(pulse, PulseLoop);
        var port = new Ellipse { Width = 7, Height = 7, IsHitTestVisible = false };
        var v = new NodeVisual(node, el, edge, pulse, port);
        _visuals.Add(v);
        EdgeLayer.Children.Add(edge);
        EdgeLayer.Children.Add(pulse);
        EdgeLayer.Children.Add(port);
        NodeLayer.Children.Add(el);
        node.PropertyChanged += OnNodeChanged;
        StyleEdge(v);
    }

    /// <summary>Its edge in the colour of its state: the theme's accent, faint, while it works (the pulse is the bright
    /// part); a calm solid green once done; a dashed red when it failed, muted when it was stopped.</summary>
    private static void StyleEdge(NodeVisual v)
    {
        var (key, opacity, dash) = v.Node.State switch
        {
            "done" => ("Status.Success", 0.6, (DoubleCollection?)null),
            "failed" => ("Status.Danger", 0.75, new DoubleCollection { 2.5, 2 }),
            "stopped" => ("Text.Muted", 0.6, new DoubleCollection { 2.5, 2 }),
            _ => ("Accent.1", 0.35, null),
        };
        v.Edge.SetResourceReference(Shape.StrokeProperty, key);
        v.Edge.Opacity = opacity;
        v.Edge.StrokeDashArray = dash;
        v.Port.SetResourceReference(Shape.FillProperty, key);
        v.Port.Opacity = Math.Min(1, opacity + 0.3);
    }

    private int IndexOf(AgentNode node) => _visuals.FindIndex(v => ReferenceEquals(v.Node, node));

    // ─── layout and view ───

    private void Relayout()
    {
        if (_map == null || _rootEl == null) return;
        var st = _map.AgentGraph;
        _scene = AgentGraphLayout.Build(_visuals.Select(v => v.Node.Id).ToList(), st.Manual);
        Canvas.SetLeft(_rootEl, _scene.Root.X);
        Canvas.SetTop(_rootEl, _scene.Root.Y);
        if (_rootPort != null)
        {
            Canvas.SetLeft(_rootPort, _scene.Root.X + _scene.Root.Width / 2 - 3.5);
            Canvas.SetTop(_rootPort, _scene.Root.Bottom - 3.5);
            _rootPort.Visibility = _scene.RootMoved || _visuals.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        for (int i = 0; i < _visuals.Count; i++)
        {
            var v = _visuals[i];
            var r = _scene.Nodes[i];
            Canvas.SetLeft(v.Element, r.X);
            Canvas.SetTop(v.Element, r.Y);
            var g = _scene.Edges[i];
            v.Edge.Data = g;
            v.Pulse.Data = g;
            double len = AgentGraphLayout.Length(g);
            SetPulseLength(v.Pulse, len);
            // the pattern: one light the length of PulseLength, then a gap longer than the curve (one light at a time)
            double d = PulseLength / PulseThickness, gap = len / PulseThickness + d;
            if (v.Pulse.StrokeDashArray is not { Count: 2 } da || Math.Abs(da[0] - d) > 0.01 || Math.Abs(da[1] - gap) > 0.5)
                v.Pulse.StrokeDashArray = new DoubleCollection { d, gap };
            UpdatePulse(v.Pulse);
            var end = g.Figures[0].Segments.Count > 0 ? EndOf(g) : r.TopLeft;
            Canvas.SetLeft(v.Port, end.X - 3.5);
            Canvas.SetTop(v.Port, end.Y - 3.5);
        }
        // The card is as tall as the graph at 100 % (within limits); taller graphs are fitted into it.
        Height = Math.Clamp(_scene.Bounds.Height + 2 * Pad + 16, MinCanvasHeight, MaxCanvasHeight);
        var vp = st.Viewport;
        if (vp.ViewSize.Width > 0)
        {
            if (!st.UserMoved) vp.Fit(_scene.Bounds, Pad);
            else vp.Clamp(_scene.Bounds);
        }
        ApplyView();
    }

    private static Point EndOf(PathGeometry g)
    {
        var fig = g.Figures[0];
        return fig.Segments[^1] switch
        {
            LineSegment l => l.Point,
            BezierSegment b => b.Point3,
            QuadraticBezierSegment q => q.Point2,
            _ => fig.StartPoint,
        };
    }

    private void OnViewSize(Size size)
    {
        if (_map == null || _scene == null) return;
        var st = _map.AgentGraph;
        st.Viewport.ViewSize = size;
        if (!st.UserMoved) st.Viewport.Fit(_scene.Bounds, Pad);
        else st.Viewport.Clamp(_scene.Bounds);
        ApplyView();
    }

    private void ApplyView()
    {
        if (_map == null) return;
        var vp = _map.AgentGraph.Viewport;
        World.RenderTransform = new MatrixTransform(vp.Matrix);
        ZoomText.Text = $"{vp.Zoom * 100:0}%";
        ZoomInButton.Opacity = vp.Zoom >= GraphViewport.MaxZoom - 1e-6 ? 0.4 : 1;
        ZoomOutButton.Opacity = vp.Zoom <= GraphViewport.MinZoom + 1e-6 ? 0.4 : 1;
        if (Viewport.Background is TileBrush dots)
        {
            double s = DotStep * vp.Zoom;
            double ox = ((vp.OffsetX % s) + s) % s, oy = ((vp.OffsetY % s) + s) % s;
            dots.Viewport = new Rect(ox, oy, s, s);
        }
    }

    // ─── the dot grid ───

    private const double DotStep = 22;

    private static VisualBrush DotGrid()
    {
        var dot = new Ellipse { Width = 2, Height = 2, Opacity = 0.35 };
        dot.SetResourceReference(Shape.FillProperty, "Text.Muted");
        var cell = new Canvas { Width = DotStep, Height = DotStep, Background = Brushes.Transparent };
        Canvas.SetLeft(dot, 0); Canvas.SetTop(dot, 0);
        cell.Children.Add(dot);
        return new VisualBrush(cell)
        {
            TileMode = TileMode.Tile, Stretch = Stretch.None,
            Viewbox = new Rect(0, 0, DotStep, DotStep), ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, DotStep, DotStep), ViewportUnits = BrushMappingMode.Absolute,
            AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
        };
    }

    // ─── the pulse: one storyboard for every edge (0 → 1), each edge maps it onto its own length ───

    /// <summary>0..1 along the edge: where its light is (a Motion loop animates it; the dash offset follows).</summary>
    public static readonly DependencyProperty PulseProgressProperty = DependencyProperty.RegisterAttached(
        "PulseProgress", typeof(double), typeof(AgentGraphView), new PropertyMetadata(0.0, (d, _) => UpdatePulse((Shape)d)));

    private static readonly DependencyProperty PulseLengthProperty = DependencyProperty.RegisterAttached(
        "PulseLength", typeof(double), typeof(AgentGraphView), new PropertyMetadata(0.0));

    private static void SetPulseLength(DependencyObject d, double len) => d.SetValue(PulseLengthProperty, len);

    private static void UpdatePulse(Shape s)
    {
        double len = (double)s.GetValue(PulseLengthProperty);
        if (len <= 0) return;
        double t = (double)s.GetValue(PulseProgressProperty);
        double d = PulseLength / PulseThickness;
        // dash covers [-offset, -offset + d] (in thicknesses): from just before the start to just past the end
        s.StrokeDashOffset = d - t * (len / PulseThickness + d);
    }

    private static readonly Storyboard PulseLoop = MakePulseLoop();

    private static Storyboard MakePulseLoop()
    {
        var a = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.7)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTargetProperty(a, new PropertyPath(PulseProgressProperty));
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(sb, 30);
        sb.Children.Add(a);
        sb.Freeze();
        return sb;
    }

    // ─── toolbar ───

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomBy(1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.25);
    private void Fit_Click(object sender, RoutedEventArgs e) => FitView();
    private void Reset_Click(object sender, RoutedEventArgs e) => ResetLayout();

    private sealed record NodeVisual(AgentNode Node, ContentControl Element, Path Edge, Path Pulse, Ellipse Port);
}
