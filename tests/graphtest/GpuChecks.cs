using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CluadeX.Services.UiStudio;
using CluadeX.Views.UiStudio;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GraphTest;

/// <summary>
/// The Node Graph on the GPU (the real NodeGraphGpu: WebView2 + Resources/NodeGraph/graph.html, WebGL2) on the same
/// 600-file / 2800-wire graph as the canvas checks. The owner (2026-11-03): "ใช้การ์ดจอช่วยแสดงผล และ ถ้าไม่ได้ดูก็ไม่ต้อง
/// เรนเดอ" — so: it starts on WebGL2, a pan frame is cheap, an idle graph draws no frame, a minimized one none even
/// while it glows, a pick sends no new graph, an agent's write runs data along the file's wires, and the view survives
/// moving into another window. Screenshots (dark, light, data running) land next to the report for a look.
/// The window sits off screen, so Chromium's occlusion check (which stops a covered page from drawing — wanted in the
/// app) is turned off for this process only.
/// </summary>
internal static partial class Program
{
    static void Gpu()
    {
        Section("gpu: the Node Graph on the GPU (WebView2 + WebGL2)");
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-features=CalculateNativeWinOcclusion");
        Exception failure = null;
        var t = new Thread(() =>
        {
            var d = Dispatcher.CurrentDispatcher;
            d.BeginInvoke(async () =>
            {
                try { await GpuOnSta(); }
                catch (Exception ex) { failure = ex; }
                finally { d.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) Check("gpu: the checks ran", false, failure.ToString());
    }

    static async Task GpuOnSta()
    {
        var g = BigGraph();
        var gpu = NodeGraphGpu.Acquire();
        gpu.DataFolder = Path.Combine(Path.GetTempPath(), "cx-graphtest-wv2");
        var logs = new List<string>();
        gpu.Log = logs.Add;
        var ready = new TaskCompletionSource<string>();
        gpu.Ready += r => ready.TrySetResult("ok:" + r);
        gpu.Unavailable += why => ready.TrySetResult("off:" + why);
        var host = new Border();
        var win = new Window { Width = 1140, Height = 820, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, Content = host };
        host.Child = gpu;
        win.Show();
        Window win2 = null;
        try
        {
            var first = await Task.WhenAny(ready.Task, Task.Delay(30000));
            string r = first == ready.Task ? ready.Task.Result : "timeout";
            Check($"the page starts on WebGL2 ({r})", r.StartsWith("ok:"), string.Join("\n", logs));
            if (!r.StartsWith("ok:")) return;
            var web = (WebView2)typeof(NodeGraphGpu).GetField("_web", Any)!.GetValue(gpu)!;
            async Task<JsonElement> Js(string expr) => JsonDocument.Parse(await web.CoreWebView2.ExecuteScriptAsync(expr)).RootElement;
            async Task<JsonElement> Stats() => await Js("window.__graphStats");
            async Task<bool> Until(Func<JsonElement, bool> ok, int ms = 5000)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < ms) { if (ok(await Stats())) return true; await Task.Delay(50); }
                return false;
            }
            string dir = AppContext.BaseDirectory;
            async Task Shot(string name)
            {
                await Task.Delay(250);
                using var fs = File.Create(Path.Combine(dir, name));
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
                Report.AppendLine($"  - screenshot: {name}");
            }

            gpu.SetGraph(g, null);
            gpu.FitToView();
            bool drawn = await Until(s => s.GetProperty("nodes").GetInt32() == 600 && s.GetProperty("frames").GetInt32() > 0);
            var st = await Stats();
            Check($"the graph is on the GPU: {st.GetProperty("nodes")} nodes, {st.GetProperty("edges")} wire instances", drawn && st.GetProperty("edges").GetInt32() >= 2000, st.ToString());
            Check("one graph upload for the first graph", gpu.GraphUploads == 1, $"{gpu.GraphUploads}");
            Check("no page error so far", !logs.Any(l => l.Contains("error")), string.Join("\n", logs));
            await Shot("gpu-dark.png");

            // idle: nothing moves, nothing is drawn
            await Task.Delay(700);
            int f1 = (await Stats()).GetProperty("frames").GetInt32();
            await Task.Delay(1500);
            int f2 = (await Stats()).GetProperty("frames").GetInt32();
            Check($"an idle graph draws no frame (1.5 s: {f2 - f1} frames)", f2 == f1);

            // a pan: 90 view moves, one per animation frame, as a drag does
            await Js(@"(async () => { const s = __graph.S, st = __graphStats; const f0 = st.frames, m0 = st.sumMs; st.maxMs = 0; const t0 = performance.now();
                for (let i = 0; i < 90; i++) { __graph.handle({ t: 'view', zoom: s.zoom, x: s.panX + 3, y: s.panY + 1 }); await new Promise(r => requestAnimationFrame(r)); }
                window.__pan = { frames: st.frames - f0, ms: st.sumMs - m0, max: st.maxMs, wall: performance.now() - t0 }; })(), 0");
            JsonElement pan = default;
            for (int i = 0; i < 100; i++) { pan = await Js("window.__pan || null"); if (pan.ValueKind == JsonValueKind.Object) break; await Task.Delay(50); }
            if (pan.ValueKind == JsonValueKind.Object)
            {
                int frames = pan.GetProperty("frames").GetInt32();
                double ms = pan.GetProperty("ms").GetDouble(), max = pan.GetProperty("max").GetDouble(), wall = pan.GetProperty("wall").GetDouble();
                double fps = frames / (wall / 1000);
                Report.AppendLine($"  - pan: {frames} frames in {wall:0} ms ({fps:0} fps), page work {ms / Math.Max(1, frames):0.00} ms a frame, worst {max:0.00} ms");
                Check($"a pan draws every frame at speed ({frames} frames, {fps:0} fps; {ms / Math.Max(1, frames):0.00} ms a frame of page work, worst {max:0.0} ms)",
                    frames >= 80 && ms / Math.Max(1, frames) < 6 && max < 30);
            }
            else Check("the pan ran", false, pan.ToString());
            Check("a pan sends nothing new from the app", gpu.GraphUploads == 1);

            // a pick: a small message, the graph stays where it is
            var hub = g.Nodes.OrderByDescending(n => g.Edges.Count(e => e.From == n.Id || e.To == n.Id)).First();
            int hubIx = g.Nodes.IndexOf(hub);
            gpu.SetGraph(g, hub);
            await Task.Delay(200);
            Check("selecting a node sends no new graph, and the page has the pick",
                gpu.GraphUploads == 1 && (await Js("window.__graph.S.sel")).GetInt32() == hubIx);
            GraphNode picked = null;
            int picks = 0;
            gpu.NodeSelected += n => { picked = n; picks++; };
            var other0 = g.Nodes[(hubIx + 1) % g.Nodes.Count];
            await Js($"chrome.webview.postMessage({{ t: 'select', n: {hubIx + 1}, g: __graph.S.gen }}), 0");
            await Task.Delay(250);
            Check("a pick in the page reaches the app as that node", ReferenceEquals(picked, other0) && picks == 1);
            await Js("chrome.webview.postMessage({ t: 'select', n: 3, g: __graph.S.gen - 1 }), 0");
            await Task.Delay(250);
            Check("a pick about a graph the page has since replaced is dropped", picks == 1);
            gpu.SetGraph(g, null);

            // an agent writes the hub's file: it glows green and data runs along its wires
            await Js($"window.__graph.handle({{ t: 'view', zoom: 0.9, x: 0, y: 0 }}), 0");
            gpu.CenterOn(hub);
            gpu.Active = new Dictionary<string, (DateTime, bool)> { [hub.Id] = (DateTime.UtcNow, true) };
            gpu.SetActivity();
            bool dots = await Until(s => s.GetProperty("particles").GetInt32() > 0, 2000);
            int a1 = (await Stats()).GetProperty("frames").GetInt32();
            await Task.Delay(600);
            int a2 = (await Stats()).GetProperty("frames").GetInt32();
            st = await Stats();
            Check($"an agent's write runs data along the file's wires ({st.GetProperty("particles")} dots, {a2 - a1} frames in 0.6 s)", dots && a2 - a1 >= 8);
            Check("…and still no new graph", gpu.GraphUploads == 1);
            await Shot("gpu-flow.png");

            // minimized while it glows: no frame at all
            win.WindowState = WindowState.Minimized;
            await Task.Delay(300);
            int h1 = (await Stats()).GetProperty("frames").GetInt32();
            await Task.Delay(1000);
            int h2 = (await Stats()).GetProperty("frames").GetInt32();
            Check($"minimized, a glowing graph draws no frame (1 s: {h2 - h1} frames)", h2 == h1);
            win.WindowState = WindowState.Normal;
            gpu.Active = new Dictionary<string, (DateTime, bool)> { [hub.Id] = (DateTime.UtcNow, false) };
            gpu.SetActivity();
            await Task.Delay(500);
            int h3 = (await Stats()).GetProperty("frames").GetInt32();
            Check($"shown again, it draws again ({h3 - h2} frames)", h3 > h2);

            // the page goes: the shared view waits; another page on screen at once would get its own
            gpu.Release(host);
            var held = new Border();
            win2 = new Window { Width = 900, Height = 640, Left = -20000, Top = -19000, ShowActivated = false, ShowInTaskbar = false, Content = held };
            win2.Show();
            var again = NodeGraphGpu.Acquire();
            Check("the next page gets the same view back (no new browser)", ReferenceEquals(again, gpu));
            held.Child = again;
            await Task.Delay(600);
            var web2 = (WebView2)typeof(NodeGraphGpu).GetField("_web", Any)!.GetValue(gpu)!;
            int m1 = (await Stats()).GetProperty("frames").GetInt32();
            gpu.FitToView();
            await Task.Delay(700);
            int m2 = (await Stats()).GetProperty("frames").GetInt32();
            Check($"moved into another window it keeps its browser and its graph, and draws ({m2 - m1} frames)",
                ReferenceEquals(web, web2) && gpu.IsReady && (await Stats()).GetProperty("nodes").GetInt32() == 600 && m2 > m1);
            var other = NodeGraphGpu.Acquire();
            Check("while one page holds it, a second page gets a view of its own", !ReferenceEquals(other, gpu));

            // light theme: the window's resources say so, the page follows
            var light = new Dictionary<string, string>
            {
                ["Surface.Sunken"] = "#F3F5F9", ["Surface.Raised"] = "#FFFFFF", ["Border.Default"] = "#C9D1DD", ["Border.Subtle"] = "#E0E5EC",
                ["Text.Primary"] = "#0F172A", ["Text.Muted"] = "#64748B", ["Text.Secondary"] = "#334155", ["Text.Faint"] = "#94A3B8",
                ["Accent.1"] = "#2563EB", ["Surface.Panel"] = "#FFFFFF",
            };
            foreach (var (k, v) in light) win2.Resources[k] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(v));
            typeof(NodeGraphGpu).GetMethod("SendTheme", Any)!.Invoke(gpu, null);
            await Task.Delay(400);
            Check("a light theme reaches the page", (await Js("window.__graph.S.light")).ValueKind == JsonValueKind.True);
            await Shot("gpu-light.png");
            Check("no page error at the end", !logs.Any(l => l.Contains("error")), string.Join("\n", logs));

            // a real project (CluadeX's own source), dark, with an agent reading two files and writing one: for a look
            foreach (var k in light.Keys) win2.Resources.Remove(k);
            typeof(NodeGraphGpu).GetMethod("SendTheme", Any)!.Invoke(gpu, null);
            string src = Path.GetFullPath(Path.Combine(Fixtures, "..", "..", "..", "CluadeX"));
            if (Directory.Exists(src))
            {
                var real = Build(src);
                var vm = (CluadeX.ViewModels.NodeGraphViewModel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(CluadeX.ViewModels.NodeGraphViewModel));
                typeof(CluadeX.ViewModels.NodeGraphViewModel).GetMethod("Layout", Any)!.Invoke(vm, new object[] { real, false });
                gpu.SetGraph(real, null);
                gpu.FitToView();
                await Task.Delay(900);
                Report.AppendLine($"  - CluadeX itself: {real.Nodes.Count} nodes, {real.Edges.Count} wires");
                await Shot("gpu-real.png");
                var busy = real.Nodes.OrderByDescending(n => real.Edges.Count(e => e.From == n.Id || e.To == n.Id)).Take(3).ToList();
                gpu.Active = new Dictionary<string, (DateTime, bool)>
                {
                    [busy[0].Id] = (DateTime.UtcNow, true), [busy[1].Id] = (DateTime.UtcNow, false), [busy[2].Id] = (DateTime.UtcNow, false),
                };
                gpu.SetActivity();
                await Js("window.__graph.handle({ t: 'view', zoom: 0.55, x: 0, y: 0 }), 0");
                gpu.CenterOn(busy[0]);
                await Task.Delay(1200);
                await Shot("gpu-real-flow.png");
            }
            gpu.Release(held);
        }
        finally
        {
            win.Close();
            win2?.Close();
        }
    }
}
