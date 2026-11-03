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

        studio.NotePageLoaded();
        graph.Mode = "live";
        Send(TreeJson(300, "12:00:00"));
        vm.NavigateToCommand.Execute("NodeGraph");
        await Task.Delay(1500);
        var canvas = Tree(win).OfType<NodeGraphCanvas>().FirstOrDefault(c => c.IsVisible);
        check("node graph: the live tree is drawn on the page", canvas != null && graph.Graph is { Nodes.Count: > 300 }, $"{graph.Graph?.Nodes.Count} nodes");
        if (canvas == null) return;
        var framesProp = typeof(NodeGraphCanvas).GetProperty("Frames");
        int Frames() => framesProp?.GetValue(canvas) is int f ? f : -1;
        int changes = 0;
        graph.GraphChanged += () => changes++;

        int f0 = Frames();
        await Task.Delay(2000);
        int idle = Frames() - f0;
        check("node graph: an idle page draws no frame (2 s)", framesProp != null && idle == 0, framesProp == null ? "no frame counter" : $"{idle} frames");

        var before = graph.Graph;
        f0 = Frames();
        for (int i = 0; i < 5; i++) { Send(TreeJson(300, "12:00:00")); await Task.Delay(80); }
        await Task.Delay(400);
        check("node graph: the same tree sent again 5× keeps the graph and redraws nothing",
            ReferenceEquals(graph.Graph, before) && changes == 0 && Frames() == f0, $"same graph {ReferenceEquals(graph.Graph, before)}, {changes} changes, {Frames() - f0} frames");

        Send(TreeJson(300, "12:00:01"));
        await Task.Delay(400);
        check("node graph: one changed element gives a new graph, drawn once",
            !ReferenceEquals(graph.Graph, before) && changes >= 1 && Frames() - f0 >= 1 && graph.Graph!.ById["n2"].Detail == "12:00:01",
            $"{changes} changes, {Frames() - f0} frames, clock {graph.Graph?.ById.GetValueOrDefault("n2")?.Detail}");

        graph.Mode = "project";
        vm.NavigateToCommand.Execute("Chat");
        await Task.Delay(300);
    }
}
