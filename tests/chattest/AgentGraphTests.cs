using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.ViewModels;
using CluadeX.Views;
using CluadeX.Views.AgentGraph;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

/// <summary>
/// AG — the agent map as a node canvas (owner 2026-11-07: "คลิกดูได้ว่ามันกำลังรันอะไร เป็นหน้าต่างลอย · ทำเป็นโหนด ·
/// ซูมเข้าออก ลากวางได้"): the layout and edge rules as pure math (no overlap, an edge never crosses a node it does not
/// end at, lanes apart), the zoom / pan / fit limits, then the real chat page fed by the real sub-agent event handler —
/// a click opens that agent's window, which follows it live; drag, pan, wheel, fit and reset on the real canvas; forty
/// agents; an agent that finishes and one that fails. Renders the card and the window (CHATTEST_RENDERS = where).
/// CHATTEST_ONLY=AG runs just these.
/// </summary>
internal static partial class Program
{
    static async Task<int> RunAgentGraphOnly()
    {
        await TestAgentGraph();
        Log($"\n## Result (CHATTEST_ONLY=AG): {Pass} pass / {Fail} FAIL");
        foreach (var f in Failures) Log("  - " + f);
        return Fail == 0 ? 0 : 1;
    }

    static async Task TestAgentGraph()
    {
        Log("\n## AG — the agent map as a node canvas, and each agent's own window");
        TestAgentGraphLayout();
        TestAgentGraphViewport();
        await TestAgentGraphOnPage();
        await TestAgentSteer();
    }

    // ───────────────────────────── layout and edges (pure) ─────────────────────────────

    static void TestAgentGraphLayout()
    {
        int[] sizes = { 1, 2, 3, 5, 6, 7, 9, 12, 20, 40, 60 };
        var overlapBad = new List<string>();
        var joinBad = new List<string>();
        var crossBad = new List<string>();
        var laneBad = new List<string>();
        foreach (int n in sizes)
        {
            var ids = Enumerable.Range(1, n).ToList();
            var s = AgentGraphLayout.Build(ids);
            // nodes: none overlaps another or the main agent, and same-row neighbours keep the full gap
            var all = s.Nodes.Append(s.Root).ToList();
            for (int a = 0; a < all.Count; a++)
                for (int b = a + 1; b < all.Count; b++)
                {
                    var ra = all[a]; ra.Inflate(AgentGraphLayout.ColGap / 2 - 0.5, 0);
                    if (ra.IntersectsWith(all[b]) && Math.Abs(all[a].Y - all[b].Y) < 1)
                        overlapBad.Add($"n={n}: {a}~{b} too close");
                    var ia = all[a]; ia.Intersect(all[b]);
                    if (!ia.IsEmpty && ia.Width > 0 && ia.Height > 0) overlapBad.Add($"n={n}: {a}×{b} overlap");
                }
            var port = new Point(s.Root.X + s.Root.Width / 2, s.Root.Bottom);
            double firstRowTop = s.Root.Bottom + AgentGraphLayout.RootGap;
            var samples = new List<List<Point>>();
            for (int i = 0; i < n; i++)
            {
                var g = s.Edges[i];
                var pts = AgentGraphLayout.Sample(g, 2);
                samples.Add(pts);
                var r = s.Nodes[i];
                var end = pts[^1];
                bool onPort = Near(end, new Point(r.X + r.Width / 2, r.Top)) || Near(end, new Point(r.Left, r.Top + r.Height / 2))
                              || Near(end, new Point(r.Right, r.Top + r.Height / 2));
                if (!Near(pts[0], port) || !onPort) joinBad.Add($"n={n} edge {i}: {pts[0]} → {end}");
                // it never goes through a node — not even its own (it stops at its border) — nor the main agent
                foreach (var p in pts)
                {
                    int hit = -1;
                    for (int k = 0; k < n && hit < 0; k++) { var inner = s.Nodes[k]; inner.Inflate(-1, -1); if (inner.Contains(p)) hit = k; }
                    var rootIn = s.Root; rootIn.Inflate(-1, -1);
                    if (hit >= 0 || rootIn.Contains(p)) { crossBad.Add($"n={n} edge {i} through {(hit >= 0 ? "node " + hit : "root")} at {p:0.0}"); break; }
                }
            }
            // below the fan (where all edges leave the main agent's one port) no two edges touch: lanes are apart, turns
            // cross no lane still going down
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    double min = double.MaxValue;
                    foreach (var p in samples[i]) if (p.Y >= firstRowTop)
                        foreach (var q in samples[j]) if (q.Y >= firstRowTop)
                        {
                            double d = (p - q).Length;
                            if (d < min) min = d;
                        }
                    if (min < 2) laneBad.Add($"n={n}: edges {i},{j} touch ({min:0.00} px)");
                }
        }
        Check("AG1 layout: no two nodes overlap and row neighbours keep the gap, for 1…60 agents", overlapBad.Count == 0, string.Join("; ", overlapBad.Take(4)));
        Check("AG1 edges: every edge starts at the main agent's port and ends on its own node's port", joinBad.Count == 0, string.Join("; ", joinBad.Take(4)));
        Check("AG1 edges: no edge passes through a node or the main agent", crossBad.Count == 0, string.Join("; ", crossBad.Take(4)));
        Check("AG1 edges: below the fan no two edges touch (lanes ≥ 2 px apart, no turn across a live lane)", laneBad.Count == 0, string.Join("; ", laneBad.Take(4)));
        Check("AG1 one row up to five agents, a grid after (6 → 4 columns, 40 → 9)",
            AgentGraphLayout.Columns(5) == 5 && AgentGraphLayout.Columns(6) == 4 && AgentGraphLayout.Columns(40) == 9,
            $"{AgentGraphLayout.Columns(5)}/{AgentGraphLayout.Columns(6)}/{AgentGraphLayout.Columns(40)}");

        // a node the owner dragged: its own place, a direct curve to it; the others keep the layout and their routes
        var ids7 = Enumerable.Range(1, 7).ToList();
        var auto = AgentGraphLayout.Build(ids7);
        var moved = AgentGraphLayout.Build(ids7, new Dictionary<int, Point> { [7] = new Point(1500, 600) });
        var mEnd = AgentGraphLayout.Sample(moved.Edges[6])[^1];
        Check("AG2 a dragged node sits where it was dropped and its edge follows it (ends on its top port)",
            moved.Nodes[6].TopLeft == new Point(1500, 600) && moved.Moved[6] && Near(mEnd, new Point(1500 + AgentGraphLayout.NodeWidth / 2, 600)), $"{moved.Nodes[6]} end {mEnd}");
        Check("AG2 …the other nodes do not move", Enumerable.Range(0, 6).All(i => auto.Nodes[i] == moved.Nodes[i]));
        Check("AG2 …and the bounds grow to hold it", moved.Bounds.Contains(moved.Nodes[6]) && moved.Bounds.Width > auto.Bounds.Width);
        var level = AgentGraphLayout.Build(new List<int> { 1 }, new Dictionary<int, Point> { [1] = new Point(600, 0) });
        var lEnd = AgentGraphLayout.Sample(level.Edges[0])[^1];
        Check("AG2 a node dropped level with the main agent is joined side to side",
            Near(lEnd, new Point(600, AgentGraphLayout.NodeHeight / 2)), lEnd.ToString());
    }

    static bool Near(Point a, Point b, double tol = 0.6) => (a - b).Length <= tol;

    // ───────────────────────────── zoom / pan / fit (pure) ─────────────────────────────

    static void TestAgentGraphViewport()
    {
        var content = AgentGraphLayout.Build(Enumerable.Range(1, 12).ToList()).Bounds;
        var vp = new GraphViewport { ViewSize = new Size(800, 320) };
        vp.Fit(content, 24);
        var tl = vp.ToScreen(content.TopLeft);
        var br = vp.ToScreen(content.BottomRight);
        Check("AG3 fit: the whole graph is inside the view with its padding, at ≤ 100 % and ≥ the minimum",
            tl.X >= 23.5 && tl.Y >= 23.5 && br.X <= 776.5 && br.Y <= 296.5 && vp.Zoom <= 1 && vp.Zoom >= GraphViewport.MinZoom,
            $"zoom {vp.Zoom:0.000} · {tl:0.0} – {br:0.0}");

        var cursor = new Point(530, 140);
        var under = vp.ToWorld(cursor);
        vp.Wheel(cursor, 120, content);
        var after = vp.ToWorld(cursor);
        Check("AG3 the wheel zooms toward the cursor (the point under it stays under it)", (under - after).Length < 1e-6 && vp.Zoom > 0.3,
            $"{under:0.000} vs {after:0.000}");
        for (int i = 0; i < 100; i++) vp.Wheel(cursor, 120, content);
        bool moreIn = vp.Wheel(cursor, 120, content);
        Check("AG3 zooming in stops at the maximum (and says so: the chat may scroll)", Math.Abs(vp.Zoom - GraphViewport.MaxZoom) < 1e-9 && !moreIn, vp.Zoom.ToString());
        for (int i = 0; i < 100; i++) vp.Wheel(cursor, -240, content);
        Check("AG3 zooming out stops at the minimum", Math.Abs(vp.Zoom - GraphViewport.MinZoom) < 1e-9 && !vp.Wheel(cursor, -120, content), vp.Zoom.ToString());

        vp.Fit(content, 24);
        vp.PanBy(1e6, -1e6, content);
        var sb = new Rect(vp.ToScreen(content.TopLeft), vp.ToScreen(content.BottomRight));
        var seen = Rect.Intersect(sb, new Rect(vp.ViewSize));
        Check("AG3 a pan far away still leaves the graph on screen (≥ 48 px of it on each axis)",
            !seen.IsEmpty && seen.Width >= GraphViewport.KeepVisible - 0.01 && seen.Height >= GraphViewport.KeepVisible - 0.01, seen.ToString());
        double ox = vp.OffsetX;
        vp.PanBy(double.NaN, 5, content);
        Check("AG3 a NaN pan is ignored", vp.OffsetX == ox && double.IsFinite(vp.OffsetY));

        var small = AgentGraphLayout.Build(new List<int> { 1 }).Bounds;
        vp.Fit(small, 24);
        Check("AG3 fitting a small graph does not blow it up past 100 %", Math.Abs(vp.Zoom - 1) < 1e-9, vp.Zoom.ToString());
        var zero = new GraphViewport { ViewSize = new Size(0, 0) };
        zero.Fit(content, 24);
        zero.Wheel(new Point(0, 0), 120, content);
        zero.PanBy(10, 10, content);
        var empty = new GraphViewport { ViewSize = new Size(500, 300) };
        empty.Fit(Rect.Empty, 24);
        Check("AG3 a view not laid out yet (0×0) or an empty graph never makes NaN",
            double.IsFinite(zero.Zoom) && double.IsFinite(zero.OffsetX) && double.IsFinite(zero.OffsetY) && empty.Zoom == 1 && double.IsFinite(empty.OffsetX),
            $"{zero.Zoom} {zero.OffsetX} {zero.OffsetY} · {empty.Zoom}");
        Check("AG3 the zoom clamp refuses NaN / ∞", GraphViewport.ClampZoom(double.NaN) == 1 && GraphViewport.ClampZoom(double.PositiveInfinity) == 1
            && GraphViewport.ClampZoom(0) == GraphViewport.MinZoom && GraphViewport.ClampZoom(99) == GraphViewport.MaxZoom);
    }

    // ───────────────────────────── the real chat page ─────────────────────────────

    static async Task TestAgentGraphOnPage()
    {
        string renders = Environment.GetEnvironmentVariable("CHATTEST_RENDERS") is { Length: > 0 } r ? r : AppContext.BaseDirectory;
        Directory.CreateDirectory(renders);
        var themes = Sp.GetRequiredService<ThemeService>();
        LocSvc.SetLanguage("th");
        var handler = typeof(ChatViewModel).GetMethod("OnSubAgentEvent", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var runType = typeof(ChatViewModel).GetNestedType("ChatRun", BindingFlags.NonPublic)!;
        try
        {
            await FreshChat();
            Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "ช่วยสำรวจโค้ดฟิสิกส์ แล้วหาเครื่องมือ REA ที่ต้องใช้ Linux" });
            // A run like the one the chat makes for a command: its live list is the chat on screen.
            var run = Activator.CreateInstance(runType)!;
            runType.GetProperty("Session")!.SetValue(run, Vm.CurrentSession);
            runType.GetProperty("Messages")!.SetValue(run, Vm.Messages);
            void Ev(int id, string phase, string detail = "", string? callId = null, string? result = null, bool ok = true, string kind = "explore",
                string label = "Explore", string task = "", string? model = "claude-haiku-5-5")
                => handler.Invoke(Vm, [run, new CodeAgentService.SubAgentEvent(id, label, kind, task, phase, detail, 1, model, 0.004m, null, callId, result, ok)]);

            Ev(1, "start", task: "Map ReverseX analysis pipeline");
            Ev(2, "start", task: "REA tools needing Linux");
            Ev(3, "start", kind: "general", label: "General", task: "Fix friction in main.gd", model: "claude-sonnet-5-5");
            var map = (ChatMessage)runType.GetField("AgentMapMsg")!.GetValue(run)!;
            await Render(); await Task.Delay(150); await Render();
            var card = Container(map);
            var graph = card == null ? null : Find<AgentGraphView>(card).FirstOrDefault();
            Check("AG4 the agent map row draws a node canvas (not the old line tree)", graph != null && graph.IsVisible, card == null ? "no row" : "no canvas");
            if (graph == null) return;
            var nodes = map.AgentNodes!;
            Check("AG4 every sub-agent is a node with an edge, and the main agent is a node",
                nodes.All(n => graph.ElementFor(n) != null && graph.EdgeFor(n)?.Data != null) && graph.RootElement != null);
            Check("AG4 a running agent's edge carries a moving light (its Motion loop runs)",
                nodes.All(n => graph.PulseFor(n) is { IsVisible: true } p && Motion.IsLooping(p)));

            // ── a click on a node opens its window ── The press goes through the node's real mouse handler; the release is
            // the same call the canvas's handler makes, at the press point (a synthetic event's GetPosition reads a stale
            // cursor, and the owner's real mouse moves while this runs).
            var n1 = nodes[0];
            var el1 = graph.ElementFor(n1)!;
            var vpEl = (FrameworkElement)graph.FindName("Viewport");
            object F(string f) => typeof(AgentGraphView).GetField(f, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(graph);
            el1.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            Check("AG5 pressing a node's card is a press on that node (its handler, the mouse captured for a drag)",
                ReferenceEquals(F("_pressed"), n1) && F("_drag").ToString() == "Node" && ReferenceEquals(Mouse.Captured, vpEl), F("_drag").ToString());
            // Capturing makes WPF send a move at the REAL cursor (anywhere on the owner's screen): end that press, then click
            // through the same calls without a capture.
            Mouse.Capture(null);
            graph.ResetLayout();
            await Render();
            graph.PointerDown(Centre(graph, el1), n1);
            bool wasClick = graph.PointerUp(Centre(graph, el1));
            Check("AG5 released where it was pressed: a click (not a drag, nothing moved)", wasClick && map.AgentGraph.Manual.Count == 0);
            await Render(); await Task.Delay(100);
            var wins = Application.Current.Windows.OfType<AgentDetailWindow>().ToList();
            var win = wins.FirstOrDefault();
            Check("AG5 a click on an agent opens one floating window, bound to that agent",
                wins.Count == 1 && ReferenceEquals(win!.Vm.Node, n1) && win.IsVisible, $"{wins.Count} windows");
            if (win == null) return;
            win.Left = -12000;
            bool modal = (bool)typeof(Window).GetField("_showingAsDialog", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(win)!;
            Check("AG5 …it is not modal (the chat keeps working beside it) and belongs to the chat's window",
                !modal && ReferenceEquals(win.Owner, Win) && Win.IsEnabled, $"modal={modal} owner={win.Owner?.GetType().Name}");
            graph.PointerDown(Centre(graph, el1), n1);
            graph.PointerUp(Centre(graph, el1));
            Check("AG5 clicking it again brings the same window forward (no second one)", Application.Current.Windows.OfType<AgentDetailWindow>().Count() == 1);
            Check("AG5 the window shows its task, kind and state", win.Vm.Title == "Explore" && win.Vm.StateText == L("agentdetail.state.running")
                && Find<TextBox>((DependencyObject)win.Content).Any(t => t.Text == "Map ReverseX analysis pipeline"));

            // ── it follows the agent live, through the chat's real event handler ──
            Ev(1, "tool", "grep_search pattern=Ghidra", callId: "c1");
            await Render(); win.UpdateLayout();
            Check("AG6 a tool that starts shows up at once as a running step, and on the card",
                win.Vm.Activity.Count == 1 && win.Vm.Activity[0].IsRunning && n1.Action == "grep_search pattern=Ghidra" && win.Vm.ToolsText == "1"
                && Find<TextBlock>((DependencyObject)win.Content).Any(t => t.Text == "grep_search pattern=Ghidra"), $"{win.Vm.Activity.Count} steps");
            Ev(1, "result", callId: "c1", result: "tools/rea/ghidra.sh:3: analyzeHeadless\nREADME.md:12: Ghidra 11");
            await Render(); win.UpdateLayout();
            var s1 = win.Vm.Activity[0];
            Check("AG6 its result lands on the same step (✓, first line shown, the whole on a click)",
                s1.IsOk && s1.ResultPreview == "tools/rea/ghidra.sh:3: analyzeHeadless" && s1.Result.Contains("Ghidra 11") && s1.DurationText.Length > 0
                && Find<TextBlock>((DependencyObject)win.Content).Any(t => t.Text == s1.ResultPreview), s1.ResultPreview);
            s1.IsExpanded = true;
            await Render(); win.UpdateLayout();
            Check("AG6 an opened step shows its whole result", Find<TextBox>((DependencyObject)win.Content).Any(t => t.IsVisible && t.Text.Contains("Ghidra 11")));
            Ev(1, "tool", "read_file tools/rea/ghidra.sh", callId: "c2");
            Ev(1, "tool", "⏳ thinking");   // a progress line with no call id
            Ev(1, "result", callId: "c2", result: "Error: file is locked", ok: false);
            Ev(1, "tool", "list_files tools/");
            await Render(); win.UpdateLayout();
            Check("AG6 a failed tool reads as an error; a step with no id ends when the next starts",
                win.Vm.Activity.Count == 4 && win.Vm.Activity[1].IsError && win.Vm.Activity[2].IsOk && win.Vm.Activity[3].IsRunning && win.Vm.ToolsText == "4",
                string.Join(" | ", win.Vm.Activity.Select(a => a.State)));
            Check("AG6 a result with an unknown id changes nothing", !n1.EndStep("nope", true, "x") && win.Vm.Activity.Count == 4);
            string t0 = win.Vm.ElapsedText;
            await Task.Delay(1300);
            Check("AG6 its clock ticks while it works", win.Vm.ClockRunning && win.Vm.ElapsedText != t0, $"{t0} → {win.Vm.ElapsedText}");

            // ── it finishes: calm state everywhere ──
            Ev(1, "done", "Found 3 tools that need Linux: Ghidra headless, radare2 r2pipe, angr");
            await Render(); win.UpdateLayout();
            var edge1 = graph.EdgeFor(n1)!;
            Check("AG7 a finished agent: window says done, its clock stops, no step is left running",
                win.Vm.StateText == L("agentdetail.state.done") && !win.Vm.ClockRunning && win.Vm.Activity.All(a => !a.IsRunning)
                && win.Vm.NowLabel == L("agentdetail.report"), win.Vm.StateText);
            Check("AG7 …its edge turns a calm solid green and its light stops",
                ReferenceEquals(edge1.Stroke, View.FindResource("Status.Success")) && edge1.StrokeDashArray == null
                && !Motion.IsLooping(graph.PulseFor(n1)!) && !graph.PulseFor(n1)!.IsVisible);
            Ev(2, "tool", "run_command wsl --list", callId: "w1");
            Ev(2, "failed", "WSL is not installed on this PC");
            await Render();
            var n2 = nodes[1];
            var edge2 = graph.EdgeFor(n2)!;
            Check("AG7 a failed agent: a dashed red edge, no light, its open step stopped",
                ReferenceEquals(edge2.Stroke, View.FindResource("Status.Danger")) && edge2.StrokeDashArray is { Count: 2 }
                && !Motion.IsLooping(graph.PulseFor(n2)!) && n2.Activity.Single().State == "stopped");
            var win2 = AgentDetailWindow.Open(n2, Win);
            win2.Left = -12000;
            await Render(); win2.UpdateLayout();
            Check("AG7 …its window says it failed and why", win2.Vm.StateText == L("agentdetail.state.failed") && win2.Vm.NowLabel == L("agentdetail.error")
                && Find<TextBox>((DependencyObject)win2.Content).Any(t => t.Text == "WSL is not installed on this PC"));

            // ── the canvas: drag, pan, wheel, fit, reset ──
            var st = map.AgentGraph;
            var n3 = nodes[2];
            var el3 = graph.ElementFor(n3)!;
            var before3 = new Point(Canvas.GetLeft(el3), Canvas.GetTop(el3));
            double z = st.Viewport.Zoom;
            var c3 = Centre(graph, el3);
            graph.PointerDown(c3, n3);
            graph.PointerMove(c3 + new Vector(60, 30));
            bool clicked = graph.PointerUp(c3 + new Vector(120, 60));
            await Render();
            var after3 = new Point(Canvas.GetLeft(el3), Canvas.GetTop(el3));
            Check("AG8 dragging a node moves it by the mouse's distance ÷ zoom, and is not a click",
                !clicked && Math.Abs(after3.X - before3.X - 120 / z) < 0.5 && Math.Abs(after3.Y - before3.Y - 60 / z) < 0.5
                && st.Manual.ContainsKey(n3.Id) && Application.Current.Windows.OfType<AgentDetailWindow>().Count() == 2,
                $"{before3:0.0} → {after3:0.0} at zoom {z:0.00}");
            var e3 = AgentGraphLayout.Sample((PathGeometry)graph.EdgeFor(n3)!.Data)[^1];
            Check("AG8 …its edge follows it", Near(e3, new Point(after3.X + AgentGraphLayout.NodeWidth / 2, after3.Y), 1), e3.ToString());

            graph.ResetLayout();
            await Render();
            var bgPoint = new Point(8, graph.ActualHeight - 30);
            double ox = st.Viewport.OffsetX, oy = st.Viewport.OffsetY;
            graph.PointerDown(bgPoint, null);
            graph.PointerUp(bgPoint + new Vector(40, 10));
            await Render();
            Check("AG8 dragging the background pans the view by the mouse's distance",
                Math.Abs(st.Viewport.OffsetX - ox - 40) < 0.01 && Math.Abs(st.Viewport.OffsetY - oy - 10) < 0.01 && st.UserMoved,
                $"{ox:0.0},{oy:0.0} → {st.Viewport.OffsetX:0.0},{st.Viewport.OffsetY:0.0}");
            var world = (Canvas)graph.FindName("World");
            var cur = new Point(graph.ActualWidth / 2, 60);
            var w0 = st.Viewport.ToWorld(cur);
            double zb = st.Viewport.Zoom;
            bool zoomed = graph.WheelAt(cur, 120);
            await Render();
            Check("AG8 the wheel zooms the canvas toward the cursor (the drawn transform is the view's)",
                zoomed && st.Viewport.Zoom > zb && (st.Viewport.ToWorld(cur) - w0).Length < 1e-6 && world.RenderTransform.Value == st.Viewport.Matrix,
                $"{zb:0.00} → {st.Viewport.Zoom:0.00}");
            graph.FitView();
            await Render();
            Check("AG8 fit puts every node on screen inside the canvas", AllOnScreen(graph, nodes) && !st.UserMoved);
            // a row the chat list recycles comes back as the owner left it
            graph.ZoomBy(1.25);
            double kept = st.Viewport.Zoom;
            graph.DataContext = null; await Render();
            graph.DataContext = map; await Render();
            Check("AG8 the view lives on the row: rebinding keeps its zoom", Math.Abs(st.Viewport.Zoom - kept) < 1e-9 && graph.ElementFor(n1) != null, $"{kept} vs {st.Viewport.Zoom}");
            graph.FitView();

            // ── the pictures: the canvas and the window, in a dark and a light theme ──
            await Task.Delay(400);
            foreach (var id in new[] { ThemeIds.Midnight, ThemeIds.Lavender })
            {
                themes.Apply(id, persist: false);
                await Render(); await Task.Delay(150); await Render();
                SaveCrop(Container(map), Path.Combine(renders, $"agent-graph-{id}.png"));
                win.UpdateLayout();
                SaveWindow(win, Path.Combine(renders, $"agent-window-{id}.png"));
            }
            themes.Apply(ThemeIds.Midnight, persist: false);
            Ev(3, "done", "friction 0.96");
            win.Close(); win2.Close();
            await Render();
            Check("AG9 closing a window lets go of its agent (clock off, no handler left)", !win.Vm.ClockRunning && !win2.Vm.ClockRunning);

            // ── forty agents ──
            await FreshChat();
            var run40 = Activator.CreateInstance(runType)!;
            runType.GetProperty("Session")!.SetValue(run40, Vm.CurrentSession);
            runType.GetProperty("Messages")!.SetValue(run40, Vm.Messages);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 1; i <= 40; i++)
                handler.Invoke(Vm, [run40, new CodeAgentService.SubAgentEvent(100 + i, $"Worker #{i}", "worker", $"Part {i} of the sweep", "start", "", 0)]);
            var map40 = (ChatMessage)runType.GetField("AgentMapMsg")!.GetValue(run40)!;
            await Render(); await Task.Delay(150); await Render();
            sw.Stop();
            var card40 = Container(map40);
            var g40 = card40 == null ? null : Find<AgentGraphView>(card40).FirstOrDefault();
            Check("AG10 forty agents: forty nodes on the canvas, fitted, all on screen, the card stays its maximum height",
                g40 != null && map40.AgentNodes!.All(n => g40.ElementFor(n) != null) && AllOnScreen(g40, map40.AgentNodes!)
                && g40.ActualHeight <= AgentGraphView.MaxCanvasHeight + 0.5 && map40.AgentGraph.Viewport.Zoom >= GraphViewport.MinZoom,
                $"zoom {map40.AgentGraph.Viewport.Zoom:0.00} · height {g40?.ActualHeight:0} · {sw.ElapsedMilliseconds} ms");
            Log($"  (forty agents drawn in {sw.ElapsedMilliseconds} ms)");
            Check("AG10 …each running one has its light", g40 != null && map40.AgentNodes!.Count(n => Motion.IsLooping(g40.PulseFor(n)!)) == 40);
            if (g40 != null) SaveCrop(card40, Path.Combine(renders, "agent-graph-40.png"));
            for (int i = 1; i <= 40; i++)
                handler.Invoke(Vm, [run40, new CodeAgentService.SubAgentEvent(100 + i, $"Worker #{i}", "worker", $"Part {i} of the sweep", i == 7 ? "failed" : "done", i == 7 ? "boom" : "ok", 1)]);
            await Render();
            Check("AG10 all forty back (one failed): no light left, the header counts them",
                g40 != null && map40.AgentNodes!.All(n => !Motion.IsLooping(g40.PulseFor(n)!)) && !map40.AgentMapRunning && map40.AgentMapHasFailures
                && map40.AgentMapHeader == LocF("agentmap.headerDone", 40, 39), map40.AgentMapHeader);
        }
        finally
        {
            foreach (var w in Application.Current.Windows.OfType<AgentDetailWindow>().ToList()) w.Close();
            themes.Apply(ThemeIds.Midnight, persist: false);
            LocSvc.SetLanguage("en");
        }
    }

    /// <summary>An element's centre in the canvas viewport's coordinates (what the mouse handlers pass).</summary>
    static Point Centre(AgentGraphView graph, FrameworkElement el)
    {
        var vp = (FrameworkElement)graph.FindName("Viewport");
        return el.TransformToAncestor(vp).Transform(new Point(el.ActualWidth / 2, el.ActualHeight / 2));
    }

    static bool AllOnScreen(AgentGraphView graph, IEnumerable<AgentNode> nodes)
    {
        var vp = (FrameworkElement)graph.FindName("Viewport");
        var view = new Rect(0, 0, vp.ActualWidth, vp.ActualHeight);
        view.Inflate(0.5, 0.5);
        foreach (var n in nodes)
        {
            var el = graph.ElementFor(n);
            if (el == null) return false;
            var r = el.TransformToAncestor(vp).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            if (!view.Contains(r)) return false;
        }
        return true;
    }
}
