using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Services.UiStudio;
using CluadeX.ViewModels;
using CluadeX.Views.UiStudio;

namespace GraphTest;

/// <summary>
/// The Node Graph's drawing surface (the real NodeGraphCanvas) on a project-sized graph: a frame that only moves the
/// view — a pan, a zoom step, a frame of the change glow — must not lay the wires out again. It did (owner, 2026-11-03:
/// "หน้าโหนด กระตุกมาก"): every frame sorted and rebuilt every wire, 20–40 ms a frame on a 600-file project, so panning,
/// zooming, dragging and the 30 fps glow while the AI edits all stuttered. Measured by what a frame allocates (the
/// same on any machine) and by what it draws: the kept wires must draw exactly what a fresh canvas draws.
/// </summary>
internal static partial class Program
{
    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    static void Canvas()
    {
        Section("canvas: a frame that only moves the view does not lay the wires out again");
        Exception failure = null;
        var t = new Thread(() =>
        {
            try { CanvasOnSta(); }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) Check("canvas: the checks ran", false, failure.ToString());
    }

    /// <summary>A project-sized graph: 600 files in 30 folders, ~2800 wires of several kinds, some inferred, laid out by
    /// the view model's own layered layout.</summary>
    static ProjectGraph BigGraph()
    {
        var rnd = new Random(7);
        var g = new ProjectGraph();
        var kinds = new[] { GraphNodeKind.Page, GraphNodeKind.Component, GraphNodeKind.Script, GraphNodeKind.Style, GraphNodeKind.Code };
        for (int i = 0; i < 600; i++)
            g.Add(new GraphNode { Id = $"src/f{i % 30}/file{i}.ts", Label = $"file{i}.ts", Detail = $"src/f{i % 30}", Kind = kinds[i % kinds.Length], Width = 168, Height = 44 });
        var edgeKinds = new[] { GraphEdgeKind.Imports, GraphEdgeKind.Renders, GraphEdgeKind.Calls, GraphEdgeKind.References, GraphEdgeKind.Inherits };
        var seen = new HashSet<(int, int, GraphEdgeKind)>();
        while (g.Edges.Count < 2800)
        {
            int a = rnd.Next(600), b = rnd.Next(600);
            var k = edgeKinds[rnd.Next(edgeKinds.Length)];
            if (a == b || !seen.Add((a, b, k))) continue;
            g.Edges.Add(new GraphEdge(g.Nodes[a].Id, g.Nodes[b].Id, k) { Inferred = rnd.Next(5) == 0 });
        }
        var vm = (NodeGraphViewModel)RuntimeHelpers.GetUninitializedObject(typeof(NodeGraphViewModel));
        typeof(NodeGraphViewModel).GetMethod("Layout", Any)!.Invoke(vm, new object[] { g, false });
        return g;
    }

    static void CanvasOnSta()
    {
        var g = BigGraph();
        var canvas = new NodeGraphCanvas();
        var win = new Window { Width = 1140, Height = 820, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, Content = canvas };
        win.Show();
        try
        {
            canvas.SetGraph(g, null);
            win.UpdateLayout();
            canvas.FitToView();
            var pan = typeof(NodeGraphCanvas).GetField("_pan", Any)!;
            var zoom = typeof(NodeGraphCanvas).GetField("_zoom", Any)!;
            var onRender = typeof(NodeGraphCanvas).GetMethod("OnRender", Any)!;
            (long Bytes, double Ms) Frame()
            {
                var dv = new DrawingVisual();
                long before = GC.GetAllocatedBytesForCurrentThread();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using (var dc = dv.RenderOpen()) onRender.Invoke(canvas, new object[] { dc });
                return (GC.GetAllocatedBytesForCurrentThread() - before, sw.Elapsed.TotalMilliseconds);
            }
            void Pan(double dx, double dy) => pan.SetValue(canvas, (Vector)pan.GetValue(canvas)! + new Vector(dx, dy));
            string Kb(long b) => $"{b / 1024.0:N0} KB";

            Frame();   // warm-up: brushes, label texts
            canvas.SetGraph(g, null);
            var changed = Frame();
            Pan(13, -7);
            var panned = Frame();
            Pan(-5, 9);
            var panned2 = Frame();
            Report.AppendLine($"  - fit zoom {(double)zoom.GetValue(canvas)!:0.000}: a frame after the graph changed {Kb(changed.Bytes)} / {changed.Ms:0.0} ms, a pan frame {Kb(panned2.Bytes)} / {panned2.Ms:0.0} ms");
            Check($"a pan frame allocates under a quarter of a frame after the graph changed ({Kb(panned2.Bytes)} vs {Kb(changed.Bytes)}; {panned2.Ms:0.0} vs {changed.Ms:0.0} ms)",
                panned.Bytes * 4 < changed.Bytes && panned2.Bytes * 4 < changed.Bytes);

            // zoomed in, with the busiest node selected: its wires are re-read every frame, the rest are kept
            var hub = g.Nodes.OrderByDescending(n => g.Edges.Count(e => e.From == n.Id || e.To == n.Id)).First();
            zoom.SetValue(canvas, 1.0);
            canvas.CenterOn(hub);
            canvas.SetGraph(g, hub);
            var selChanged = Frame();
            Pan(20, 4);
            var selPanned = Frame();
            Check($"zoomed in with a node selected, a pan frame allocates under a quarter too ({Kb(selPanned.Bytes)} vs {Kb(selChanged.Bytes)}; {selPanned.Ms:0.0} vs {selChanged.Ms:0.0} ms)",
                selPanned.Bytes * 4 < selChanged.Bytes);
            zoom.SetValue(canvas, 1.12);
            var zoomed = Frame();
            Check($"a zoom step (arrow heads at their fixed size) allocates under a quarter ({Kb(zoomed.Bytes)})", zoomed.Bytes * 4 < selChanged.Bytes);
            canvas.Changed = new Dictionary<string, DateTime> { [hub.Id] = DateTime.UtcNow };
            var glow = Frame();
            Check($"a frame of the change glow allocates under a quarter ({Kb(glow.Bytes)})", glow.Bytes * 4 < selChanged.Bytes);
            canvas.Changed = null;
            if (typeof(NodeGraphCanvas).GetProperty("WireLayouts") is { } layouts)
                Report.AppendLine($"  - wire layouts so far: {layouts.GetValue(canvas)} (one per graph change)");

            // what the kept wires draw is what a fresh canvas draws, after every kind of change
            var fresh = new NodeGraphCanvas();
            var win2 = new Window { Width = 1140, Height = 820, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, Content = fresh };
            win2.Show();
            try
            {
                byte[] Pixels(NodeGraphCanvas c)
                {
                    c.InvalidateVisual();   // as a real pan or zoom does (here the view was moved through its fields)
                    c.UpdateLayout();
                    var bmp = new RenderTargetBitmap((int)c.ActualWidth, (int)c.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(c);
                    var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
                    bmp.CopyPixels(px, bmp.PixelWidth * 4, 0);
                    return px;
                }
                bool Same(string what)
                {
                    pan.SetValue(fresh, pan.GetValue(canvas));
                    zoom.SetValue(fresh, zoom.GetValue(canvas));
                    fresh.HiddenKinds = new HashSet<GraphEdgeKind>(canvas.HiddenKinds);
                    fresh.ShowInferred = canvas.ShowInferred;
                    fresh.SelectedEdge = canvas.SelectedEdge;
                    fresh.SetGraph(null, null);   // a canvas that keeps nothing from before
                    Pixels(fresh);
                    fresh.SetGraph(canvas.Graph, canvas.Selected);
                    var a = Pixels(canvas);
                    var b = Pixels(fresh);
                    int diff = 0;
                    for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) diff++;
                    Check($"kept wires draw what a fresh canvas draws: {what}", diff == 0, $"{diff} bytes differ");
                    return diff == 0;
                }
                zoom.SetValue(canvas, 0.45);
                canvas.CenterOn(hub);
                canvas.SetGraph(g, null);
                Pixels(canvas);
                Pan(31, -12);
                Same("after a pan");
                zoom.SetValue(canvas, 0.45 * 1.12);
                Same("after a zoom step (arrow heads resized)");
                canvas.SetGraph(g, hub);
                Pixels(canvas);
                Pan(-40, 10);
                Same("after selecting a node and panning");
                hub.X += 96; hub.Y += 48;   // a node moved (as the layout or a drop does), the view model redraws
                canvas.SetGraph(g, hub);
                Same("after a node moved");
                canvas.HiddenKinds = new HashSet<GraphEdgeKind> { GraphEdgeKind.Calls };
                canvas.ShowInferred = false;
                canvas.SetGraph(g, null);
                Same("after a filter hid a kind and the inferred wires");
                canvas.HiddenKinds = new HashSet<GraphEdgeKind>();
                canvas.ShowInferred = true;
                canvas.SelectedEdge = g.Edges[5];
                canvas.SetGraph(g, null);
                Pixels(canvas);
                Pan(7, 7);
                Same("after selecting a wire and panning");
                canvas.SelectedEdge = null;
            }
            finally { win2.Close(); }

            // a wire is still clicked where it is drawn, after a pan and a zoom
            var two = new ProjectGraph();
            two.Add(new GraphNode { Id = "a.ts", Label = "a.ts", Kind = GraphNodeKind.Script, Width = 168, Height = 40, X = 0, Y = 0 });
            two.Add(new GraphNode { Id = "b.ts", Label = "b.ts", Kind = GraphNodeKind.Script, Width = 168, Height = 40, X = 400, Y = 0 });
            var wire = new GraphEdge("a.ts", "b.ts", GraphEdgeKind.Imports);
            two.Edges.Add(wire);
            canvas.SetGraph(two, null);
            var hitEdge = typeof(NodeGraphCanvas).GetMethod("HitEdge", Any)!;
            bool HitsAt(double wx, double wy, GraphEdge want)
            {
                Frame();   // as drawn
                double z = (double)zoom.GetValue(canvas)!;
                var p = (Vector)pan.GetValue(canvas)!;
                return ReferenceEquals(hitEdge.Invoke(canvas, new object[] { new Point(wx * z + p.X, wy * z + p.Y) }), want);
            }
            zoom.SetValue(canvas, 1.0);
            pan.SetValue(canvas, new Vector(40, 40));
            Check("a click on a wire hits it, a click beside it does not", HitsAt(284, 20, wire) && HitsAt(284, 60, null));
            zoom.SetValue(canvas, 0.5);
            pan.SetValue(canvas, new Vector(-120, 300));
            Check("…after a zoom and a pan too", HitsAt(284, 20, wire) && HitsAt(284, 60, null));
        }
        finally { win.Close(); }
    }
}
