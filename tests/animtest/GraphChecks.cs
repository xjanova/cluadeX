using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using CluadeX.Services.UiStudio;
using CluadeX.ViewModels;
using CluadeX.Views;
using CluadeX.Views.UiStudio;
using Microsoft.Extensions.DependencyInjection;

namespace AnimTest;

/// <summary>
/// The Node Graph page in the shipping window, on the Live UI view: while it sits idle it draws nothing, and the
/// overlay re-sending an unchanged element tree (it does after any change in the page — a clock ticking, a slide
/// turning; every 400 ms on a busy page) neither makes a new graph nor redraws it. Before 2026-11-03 every tree
/// message built a new graph and redrew the whole canvas, which cost a frame of 20–40 ms each time — a hitch under
/// every pan and drag. One changed element still gives a new graph.
/// </summary>
internal static class GraphChecks
{
    /// <summary>A body with <paramref name="count"/> elements in rows of ten, and a clock whose text is <paramref name="clock"/>.</summary>
    static string TreeJson(int count, string clock)
    {
        var sb = new StringBuilder("{\"type\":\"tree\",\"tree\":{\"n\":1,\"t\":\"body\",\"k\":[");
        sb.Append($"{{\"n\":2,\"t\":\"span\",\"c\":\"clock\",\"x\":\"{clock}\",\"s\":\"index.html:3\"}}");
        int id = 3;
        for (int row = 0; row < count / 10; row++)
        {
            sb.Append($",{{\"n\":{id++},\"t\":\"div\",\"c\":\"row r{row}\",\"s\":\"index.html:{10 + row}\",\"k\":[");
            for (int i = 0; i < 9; i++) sb.Append(i == 0 ? "" : ",").Append($"{{\"n\":{id++},\"t\":\"a\",\"x\":\"item {row}.{i}\",\"s\":\"index.html:{10 + row}\"}}");
            sb.Append("]}");
        }
        return sb.Append("]}}").ToString();
    }

    static IEnumerable<DependencyObject> Tree(DependencyObject d)
    {
        yield return d;
        int n = d is Visual ? VisualTreeHelper.GetChildrenCount(d) : 0;
        for (int i = 0; i < n; i++) foreach (var k in Tree(VisualTreeHelper.GetChild(d, i))) yield return k;
    }

    public static async Task Run(Window win, MainViewModel vm, IServiceProvider sp, Action<string, bool, string> check)
    {
        var studio = sp.GetRequiredService<StudioService>();
        var graph = vm.NodeGraphVM;
        if (graph == null) { check("node graph: the page exists", false, "no NodeGraphVM"); return; }
        void Send(string json) => studio.HandleOverlayMessage(OverlayMessage.Parse(json)!);

        // the page draws on the GPU when it can (a WebView2 off screen counts as hidden to Chromium unless told otherwise)
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--disable-features=CalculateNativeWinOcclusion");
        studio.NotePageLoaded();
        graph.Mode = "live";
        Send(TreeJson(300, "12:00:00"));
        vm.NavigateToCommand.Execute("NodeGraph");
        await Task.Delay(1500);
        var canvas = Tree(win).OfType<NodeGraphCanvas>().FirstOrDefault(c => c.IsVisible);
        var gpu = Tree(win).OfType<NodeGraphGpu>().FirstOrDefault(c => c.IsVisible);
        for (int i = 0; i < 100 && gpu != null && !gpu.IsReady; i++) await Task.Delay(100);
        check($"node graph: the live tree is drawn on the page ({(gpu != null ? "GPU: " + gpu.Renderer : "WPF canvas")})",
            (canvas != null || gpu is { IsReady: true }) && graph.Graph is { Nodes.Count: > 300 }, $"{graph.Graph?.Nodes.Count} nodes, gpu {gpu != null}");
        if (canvas == null && gpu is not { IsReady: true }) return;
        var framesProp = typeof(NodeGraphCanvas).GetProperty("Frames");
        var web = gpu == null ? null : (Microsoft.Web.WebView2.Wpf.WebView2?)typeof(NodeGraphGpu).GetField("_web", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(gpu);
        async Task<int> Frames()
        {
            if (web != null) return int.TryParse(await web.CoreWebView2.ExecuteScriptAsync("window.__graphStats.frames"), out int f) ? f : -1;
            return framesProp?.GetValue(canvas) is int c ? c : -1;
        }
        int Uploads() => gpu?.GraphUploads ?? 0;
        int changes = 0;
        graph.GraphChanged += () => changes++;

        await Task.Delay(500);   // the GPU view's first fit
        int f0 = await Frames();
        await Task.Delay(2000);
        int idle = await Frames() - f0;
        check("node graph: an idle page draws no frame (2 s)", f0 >= 0 && idle == 0, f0 < 0 ? "no frame counter" : $"{idle} frames");

        var before = graph.Graph;
        f0 = await Frames();
        int u0 = Uploads();
        for (int i = 0; i < 5; i++) { Send(TreeJson(300, "12:00:00")); await Task.Delay(80); }
        await Task.Delay(400);
        int same = await Frames() - f0;
        check("node graph: the same tree sent again 5× keeps the graph and redraws nothing",
            ReferenceEquals(graph.Graph, before) && changes == 0 && same == 0 && Uploads() == u0, $"same graph {ReferenceEquals(graph.Graph, before)}, {changes} changes, {same} frames, {Uploads() - u0} uploads");

        Send(TreeJson(300, "12:00:01"));
        await Task.Delay(400);
        int drawn = await Frames() - f0;
        check("node graph: one changed element gives a new graph, drawn once",
            !ReferenceEquals(graph.Graph, before) && changes >= 1 && drawn >= 1 && graph.Graph!.ById["n2"].Detail == "12:00:01",
            $"{changes} changes, {drawn} frames, clock {graph.Graph?.ById.GetValueOrDefault("n2")?.Detail}");

        graph.Mode = "project";
        vm.NavigateToCommand.Execute("Chat");
        await Task.Delay(300);
    }
}
