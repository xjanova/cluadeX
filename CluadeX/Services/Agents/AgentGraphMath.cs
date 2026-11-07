using System.Windows;
using System.Windows.Media;

namespace CluadeX.Services.Agents;

/// <summary>
/// The agent map as a node graph (owner 2026-11-07: "เส้นอนิเมชั่นยังไม่สวย ทำเป็นโหนด ซูมเข้าออกได้ไหม ลากวางได้"): where the
/// main agent and each sub-agent sit on the canvas, and the curve that joins them. Pure — no visuals — so the harness
/// proves the rules: nodes never overlap, an edge never crosses a node it does not end at, edges sharing a gap run in
/// lanes of their own.
/// <para>The first row hangs off the main agent on S-curves (vertical at both ends, so a curve stays above its row).
/// A deeper row's edge curves down to a lane in the gap beside its node — the gaps between columns are clear on every
/// row — runs straight down it and turns into the node's side. A node the owner dragged gets a direct curve instead:
/// the rules hold for the automatic layout, a moved node is where the owner wants it.</para>
/// </summary>
public static class AgentGraphLayout
{
    public const double NodeWidth = 232, NodeHeight = 118;
    public const double RootWidth = 168, RootHeight = 42;
    public const double ColGap = 44, RowGap = 52, RootGap = 64;
    /// <summary>The key of the main agent in a map of manual positions.</summary>
    public const int RootId = int.MinValue;
    /// <summary>Lanes that share a gap stay at least this far apart (px, world).</summary>
    public const double MinLaneSpacing = 4;

    /// <summary>How many columns the sub-agents take: one row up to five, then a grid about twice as wide as tall.</summary>
    public static int Columns(int n) => n <= 5 ? Math.Max(1, n) : Math.Max(4, (int)Math.Ceiling(Math.Sqrt(2.0 * n)));

    /// <summary>Node <paramref name="i"/>'s place in the automatic grid (top-left, world px). The last row is centred by
    /// whole columns, so its nodes stay on the grid and the gaps stay clear.</summary>
    public static Rect AutoRect(int i, int n)
    {
        int cols = Columns(n);
        int row = i / cols, col = i % cols;
        int inLastRow = n - (n - 1) / cols * cols;
        if (row == (n - 1) / cols && inLastRow < cols) col += (cols - inLastRow) / 2;
        double top = RootHeight + RootGap + row * (NodeHeight + RowGap);
        return new Rect(col * (NodeWidth + ColGap), top, NodeWidth, NodeHeight);
    }

    public static double GridWidth(int n)
    {
        int cols = Columns(n);
        return cols * NodeWidth + (cols - 1) * ColGap;
    }

    public static Rect AutoRoot(int n) => new((Math.Max(GridWidth(n), RootWidth) - RootWidth) / 2, 0, RootWidth, RootHeight);

    /// <summary>The whole scene: the main agent, every node (its manual place when it has one), every edge.</summary>
    public static AgentGraphScene Build(IReadOnlyList<int> ids, IReadOnlyDictionary<int, Point>? manual = null)
    {
        int n = ids.Count;
        manual ??= new Dictionary<int, Point>();
        bool rootMoved = manual.TryGetValue(RootId, out var rp);
        var root = rootMoved ? new Rect(rp, new Size(RootWidth, RootHeight)) : AutoRoot(n);
        var nodes = new List<Rect>(n);
        var moved = new List<bool>(n);
        for (int i = 0; i < n; i++)
        {
            bool m = manual.TryGetValue(ids[i], out var p);
            nodes.Add(m ? new Rect(p, new Size(NodeWidth, NodeHeight)) : AutoRect(i, n));
            moved.Add(m);
        }

        int cols = Columns(n);
        var edges = new PathGeometry[n];
        // Lanes: every routed edge (row ≥ 1, neither end moved) claims a slot in the gap beside its node — the gap on the
        // main agent's side. A node left of its gap turns left out of it (port on its right), one right of it turns right.
        var lanes = new Dictionary<int, List<int>>();   // gap index (0 = left of column 0) → node indexes
        var gapOf = new int[n];
        var turnsLeft = new bool[n];
        double rootCentre = root.X + RootWidth / 2;
        for (int i = 0; i < n; i++)
        {
            if (rootMoved || moved[i] || i / cols == 0) continue;
            int col = (int)Math.Round(nodes[i].X / (NodeWidth + ColGap));
            turnsLeft[i] = nodes[i].X + NodeWidth / 2 < rootCentre;
            int gap = turnsLeft[i] ? col + 1 : col;
            gapOf[i] = gap;
            if (!lanes.TryGetValue(gap, out var list)) lanes[gap] = list = new List<int>();
            list.Add(i);
        }
        // Left to right in a gap: the left-turning edges, shallowest row first (nearest their side), then the right-turning
        // ones, deepest first. A turn then crosses only lanes whose edge already ended higher up — never one going down.
        foreach (var list in lanes.Values)
        {
            var left = list.Where(i => turnsLeft[i]).OrderBy(i => nodes[i].Top);
            var right = list.Where(i => !turnsLeft[i]).OrderByDescending(i => nodes[i].Top);
            var ordered = left.Concat(right).ToList();
            list.Clear();
            list.AddRange(ordered);
        }
        var start = new Point(rootCentre, root.Bottom);
        double firstRowTop = root.Bottom + RootGap;
        for (int i = 0; i < n; i++)
        {
            var r = nodes[i];
            if (rootMoved || moved[i]) { edges[i] = Direct(root, r); continue; }
            if (i / cols == 0) { edges[i] = SCurve(start, new Point(r.X + NodeWidth / 2, r.Top)); continue; }
            var inGap = lanes[gapOf[i]];
            int slot = inGap.IndexOf(i), m = inGap.Count;
            double gapCentre = gapOf[i] * (NodeWidth + ColGap) - ColGap / 2;
            double spacing = m <= 1 ? 0 : Math.Min(8, (ColGap - 8) / (m - 1));
            double laneX = gapCentre - (m - 1) * spacing / 2 + slot * spacing;
            edges[i] = Routed(start, laneX, firstRowTop, r, enterRight: turnsLeft[i]);
        }

        var bounds = root;
        foreach (var r in nodes) bounds.Union(r);
        return new AgentGraphScene(root, nodes, moved, rootMoved, edges, bounds);
    }

    /// <summary>Vertical at both ends; with the handles at half the drop the curve's height only ever grows, so it never
    /// rises into the row it hangs from nor dips into the one it lands on.</summary>
    public static PathGeometry SCurve(Point a, Point b)
    {
        double k = (b.Y - a.Y) / 2;
        var f = new PathFigure { StartPoint = a, IsFilled = false };
        f.Segments.Add(new BezierSegment(new Point(a.X, a.Y + k), new Point(b.X, b.Y - k), b, true));
        return Freeze(f);
    }

    /// <summary>Down to the lane at the top of the first row, straight down the gap, a rounded turn into the node's side.</summary>
    private static PathGeometry Routed(Point a, double laneX, double firstRowTop, Rect r, bool enterRight)
    {
        double y = r.Top + NodeHeight / 2;
        double portX = enterRight ? r.Right : r.Left;
        double radius = Math.Min(10, Math.Abs(portX - laneX));
        var f = new PathFigure { StartPoint = a, IsFilled = false };
        double k = (firstRowTop - a.Y) / 2;
        var laneTop = new Point(laneX, firstRowTop);
        f.Segments.Add(new BezierSegment(new Point(a.X, a.Y + k), new Point(laneX, firstRowTop - k), laneTop, true));
        f.Segments.Add(new LineSegment(new Point(laneX, y - radius), true));
        f.Segments.Add(new QuadraticBezierSegment(new Point(laneX, y), new Point(laneX + (portX > laneX ? radius : -radius), y), true));
        f.Segments.Add(new LineSegment(new Point(portX, y), true));
        return Freeze(f);
    }

    /// <summary>A moved node: one curve between the nearest ports (bottom → top, or side → side when they sit level).</summary>
    public static PathGeometry Direct(Rect from, Rect to)
    {
        var f = new PathFigure { IsFilled = false };
        if (to.Top >= from.Bottom + 12)
        {
            var a = new Point(from.X + from.Width / 2, from.Bottom);
            var b = new Point(to.X + to.Width / 2, to.Top);
            double k = Math.Max(24, (b.Y - a.Y) / 2);
            f.StartPoint = a;
            f.Segments.Add(new BezierSegment(new Point(a.X, a.Y + k), new Point(b.X, b.Y - k), b, true));
        }
        else if (to.Bottom <= from.Top - 12)
        {
            var a = new Point(from.X + from.Width / 2, from.Top);
            var b = new Point(to.X + to.Width / 2, to.Bottom);
            double k = Math.Max(24, (a.Y - b.Y) / 2);
            f.StartPoint = a;
            f.Segments.Add(new BezierSegment(new Point(a.X, a.Y - k), new Point(b.X, b.Y + k), b, true));
        }
        else
        {
            bool right = to.X + to.Width / 2 >= from.X + from.Width / 2;
            var a = new Point(right ? from.Right : from.Left, from.Top + from.Height / 2);
            var b = new Point(right ? to.Left : to.Right, to.Top + to.Height / 2);
            double k = Math.Max(24, Math.Abs(b.X - a.X) / 2) * (right ? 1 : -1);
            f.StartPoint = a;
            f.Segments.Add(new BezierSegment(new Point(a.X + k, a.Y), new Point(b.X - k, b.Y), b, true));
        }
        return Freeze(f);
    }

    private static PathGeometry Freeze(PathFigure f)
    {
        var g = new PathGeometry();
        g.Figures.Add(f);
        g.Freeze();
        return g;
    }

    /// <summary>The curve as a polyline, evaluated from its own segments (the ones this class makes: lines, quadratic and
    /// cubic Béziers). WPF's flattening drops a figure that is not filled, so it is not used here.</summary>
    public static List<Point> Polyline(PathGeometry g, int perCurve = 48)
    {
        var pts = new List<Point>();
        foreach (var fig in g.Figures)
        {
            var p = fig.StartPoint;
            pts.Add(p);
            foreach (var seg in fig.Segments)
            {
                switch (seg)
                {
                    case LineSegment l: pts.Add(l.Point); p = l.Point; break;
                    case BezierSegment c:
                        for (int i = 1; i <= perCurve; i++)
                        {
                            double t = (double)i / perCurve, u = 1 - t;
                            pts.Add(new Point(
                                u * u * u * p.X + 3 * u * u * t * c.Point1.X + 3 * u * t * t * c.Point2.X + t * t * t * c.Point3.X,
                                u * u * u * p.Y + 3 * u * u * t * c.Point1.Y + 3 * u * t * t * c.Point2.Y + t * t * t * c.Point3.Y));
                        }
                        p = c.Point3;
                        break;
                    case QuadraticBezierSegment q:
                        for (int i = 1; i <= perCurve / 2; i++)
                        {
                            double t = (double)i / (perCurve / 2), u = 1 - t;
                            pts.Add(new Point(u * u * p.X + 2 * u * t * q.Point1.X + t * t * q.Point2.X,
                                u * u * p.Y + 2 * u * t * q.Point1.Y + t * t * q.Point2.Y));
                        }
                        p = q.Point2;
                        break;
                }
            }
        }
        return pts;
    }

    /// <summary>A curve's length in px.</summary>
    public static double Length(PathGeometry g)
    {
        var pts = Polyline(g);
        double len = 0;
        for (int i = 1; i < pts.Count; i++) len += (pts[i] - pts[i - 1]).Length;
        return len;
    }

    /// <summary>Points along a curve, about every <paramref name="step"/> px (the harness walks them).</summary>
    public static List<Point> Sample(PathGeometry g, double step = 2)
    {
        var poly = Polyline(g);
        var pts = new List<Point>();
        if (poly.Count == 0) return pts;
        pts.Add(poly[0]);
        for (int i = 1; i < poly.Count; i++)
        {
            var a = poly[i - 1]; var b = poly[i];
            double d = (b - a).Length;
            for (double t = step; t < d; t += step) pts.Add(a + (b - a) * (t / d));
            pts.Add(b);
        }
        return pts;
    }
}

/// <summary>One drawn state of the graph (world coordinates).</summary>
public sealed record AgentGraphScene(Rect Root, IReadOnlyList<Rect> Nodes, IReadOnlyList<bool> Moved, bool RootMoved,
    IReadOnlyList<PathGeometry> Edges, Rect Bounds);

/// <summary>
/// Zoom and pan of the agent map: screen = world × Zoom + Offset. Zoom stays in [<see cref="MinZoom"/>,
/// <see cref="MaxZoom"/>], a zoom keeps the world point under the cursor where it was, and a pan never loses the graph
/// (some of it always stays on screen). Kept on the chat row (<see cref="AgentGraphState"/>) so a row the list recycles
/// comes back as the owner left it.
/// </summary>
public sealed class GraphViewport
{
    public const double MinZoom = 0.2, MaxZoom = 2.5;
    /// <summary>How much of the graph a pan must leave on screen (px, or all of it when it is smaller).</summary>
    public const double KeepVisible = 48;
    public const double WheelStep = 1.15;

    public double Zoom { get; private set; } = 1;
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }
    public Size ViewSize { get; set; }

    public Point ToScreen(Point w) => new(w.X * Zoom + OffsetX, w.Y * Zoom + OffsetY);
    public Point ToWorld(Point s) => new((s.X - OffsetX) / Zoom, (s.Y - OffsetY) / Zoom);
    public Matrix Matrix => new(Zoom, 0, 0, Zoom, OffsetX, OffsetY);

    public static double ClampZoom(double z) => double.IsFinite(z) ? Math.Clamp(z, MinZoom, MaxZoom) : 1;

    /// <summary>Zoom by <paramref name="factor"/> about a screen point. False when the zoom is already at its limit.</summary>
    public bool ZoomAt(Point screen, double factor, Rect content)
    {
        double z = ClampZoom(Zoom * factor);
        if (Math.Abs(z - Zoom) < 1e-9) return false;
        var w = ToWorld(screen);
        Zoom = z;
        OffsetX = screen.X - w.X * z;
        OffsetY = screen.Y - w.Y * z;
        Clamp(content);
        return true;
    }

    /// <summary>One wheel notch (or several: delta 120 = one) about the cursor.</summary>
    public bool Wheel(Point screen, int delta, Rect content) => ZoomAt(screen, Math.Pow(WheelStep, delta / 120.0), content);

    public void PanBy(double dx, double dy, Rect content)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;
        OffsetX += dx;
        OffsetY += dy;
        Clamp(content);
    }

    /// <summary>The whole graph in view, centred, never larger than 100 % (a fit does not blow two cards up).</summary>
    public void Fit(Rect content, double pad = 24)
    {
        if (content.IsEmpty || ViewSize.Width <= 2 * pad || ViewSize.Height <= 2 * pad || content.Width <= 0 || content.Height <= 0)
        {
            Zoom = 1; OffsetX = 0; OffsetY = 0;
            if (!content.IsEmpty && ViewSize.Width > 0) { OffsetX = (ViewSize.Width - content.Width) / 2 - content.X; OffsetY = pad - content.Y; }
            return;
        }
        double z = Math.Min((ViewSize.Width - 2 * pad) / content.Width, (ViewSize.Height - 2 * pad) / content.Height);
        Zoom = Math.Min(1, ClampZoom(z));
        OffsetX = ViewSize.Width / 2 - (content.X + content.Width / 2) * Zoom;
        OffsetY = ViewSize.Height / 2 - (content.Y + content.Height / 2) * Zoom;
    }

    /// <summary>100 %, the graph's top centred (the reset button).</summary>
    public void Reset(Rect content, double pad = 24)
    {
        Zoom = 1;
        OffsetX = ViewSize.Width / 2 - (content.X + content.Width / 2);
        OffsetY = pad - content.Y;
        Clamp(content);
    }

    public void Set(double zoom, double offsetX, double offsetY, Rect content)
    {
        Zoom = ClampZoom(zoom);
        OffsetX = double.IsFinite(offsetX) ? offsetX : 0;
        OffsetY = double.IsFinite(offsetY) ? offsetY : 0;
        Clamp(content);
    }

    /// <summary>Some of the graph stays on screen: its screen box keeps <see cref="KeepVisible"/> px (or all of it) inside
    /// the view on each axis.</summary>
    public void Clamp(Rect content)
    {
        if (content.IsEmpty || ViewSize.Width <= 0 || ViewSize.Height <= 0) return;
        double left = content.X * Zoom + OffsetX, top = content.Y * Zoom + OffsetY;
        double w = content.Width * Zoom, h = content.Height * Zoom;
        double kx = Math.Min(KeepVisible, w), ky = Math.Min(KeepVisible, h);
        if (left + w < kx) OffsetX += kx - (left + w);
        else if (left > ViewSize.Width - kx) OffsetX -= left - (ViewSize.Width - kx);
        if (top + h < ky) OffsetY += ky - (top + h);
        else if (top > ViewSize.Height - ky) OffsetY -= top - (ViewSize.Height - ky);
    }
}

/// <summary>The view of one agent map card that outlives its row: zoom, pan, the nodes the owner dragged, and whether
/// they touched the view at all (until then every new node re-fits the graph).</summary>
public sealed class AgentGraphState
{
    public GraphViewport Viewport { get; } = new();
    public Dictionary<int, Point> Manual { get; } = new();
    public bool UserMoved { get; set; }
}
