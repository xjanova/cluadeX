using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services;
using CluadeX.Services.UiStudio;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CluadeX.Views.UiStudio;

/// <summary>
/// The Node Graph drawn on the GPU: a WebView2 running Resources/NodeGraph/graph.html (WebGL2). The graph goes over once
/// when it changes; a pick, a filter or the glow go as small messages; pan, zoom, drag and picking run in the page, so a
/// frame never waits on this thread. The page draws only while something moves and stops when hidden. If the machine
/// has no WebView2 or no WebGL2, <see cref="Unavailable"/> fires and the page falls back to <see cref="NodeGraphCanvas"/>.
/// The page is a new view on every visit, so one instance lives for the app (<see cref="Acquire"/>) and moves into the
/// page that shows it: going back to the graph keeps the browser instead of starting one each time. A second page on
/// screen at once (the graph in its own window) gets an instance of its own, closed when that page goes.
/// </summary>
public sealed class NodeGraphGpu : Border, INodeGraphSurface
{
    private static NodeGraphGpu? _shared;
    private static NodeGraphGpu Shared => _shared ??= new NodeGraphGpu();

    /// <summary>The shared view when no page on screen holds it (a page that left the screen gives it up), else a new one.</summary>
    public static NodeGraphGpu Acquire()
    {
        var s = Shared;
        switch (s.Parent)
        {
            case null: return s;
            case Decorator d when PresentationSource.FromVisual(d) == null: d.Child = null; return s;
            default: return new NodeGraphGpu();
        }
    }

    /// <summary>The page that showed it is going: take it out (the shared one waits for the next page; another closes).</summary>
    public void Release(Decorator host)
    {
        if (ReferenceEquals(host.Child, this)) host.Child = null;
        if (ReferenceEquals(this, _shared)) return;
        _ready = false;
        Child = null;
        try { _web?.Dispose(); } catch { }
        _web = null;
    }

    /// <summary>The GPU path is off: no WebView2 runtime, no WebGL2, or CLUADEX_GRAPH_GPU=0. Remembered for the app's
    /// life (a machine without it will not grow it).</summary>
    public static bool Disabled { get; private set; } = Environment.GetEnvironmentVariable("CLUADEX_GRAPH_GPU") == "0";
    public static string? DisabledWhy { get; private set; }

    public ProjectGraph? Graph { get; private set; }
    public GraphNode? Selected { get; private set; }
    public ISet<string> Highlights { get; set; } = new HashSet<string>();
    public IReadOnlyDictionary<string, DateTime>? Changed { get; set; }
    public TimeSpan ChangeGlow { get; set; } = TimeSpan.FromSeconds(4);
    public IReadOnlyDictionary<string, (DateTime At, bool Write)>? Active { get; set; }
    public TimeSpan ActivityGlow { get; set; } = TimeSpan.FromSeconds(6);
    public ISet<GraphEdgeKind> HiddenKinds { get; set; } = new HashSet<GraphEdgeKind>();
    public bool ShowInferred { get; set; } = true;
    public bool GroupByFolder { get; set; }
    public GraphEdge? SelectedEdge { get; set; }
    /// <summary>The live element tree (its legend differs from the project's).</summary>
    public bool IsLiveTree { get; set; }
    /// <summary>"Reading the project…" over a graph already on screen, or null.</summary>
    public string? BusyText { get; set; }
    /// <summary>The legend's words, in the UI language (kinds, solid / dashed, the hint).</summary>
    public Func<string, string>? Loc { get; set; }
    public Action<string>? Log { get; set; }
    public string DataFolder { get; set; } = Path.Combine(Path.GetTempPath(), "CluadeX-NodeGraph");

    public event Action<GraphNode?>? NodeSelected;
    public event Action<GraphNode>? NodeActivated;
    public event Action<GraphNode>? NodeMoved;
    public event Action<GraphNode, Point>? NodeContextMenu;
    public event Action<GraphEdge?>? EdgeSelected;
    public event Action<GraphEdge>? EdgeActivated;
    /// <summary>The GPU path cannot run here (the reason, for the Debug Log): show the WPF canvas instead.</summary>
    public event Action<string>? Unavailable;
    /// <summary>The page is up and drawing (the renderer's name, e.g. the GPU's).</summary>
    public event Action<string>? Ready;

    public bool IsReady => _ready;
    public string? Renderer { get; private set; }
    /// <summary>How many graph uploads went to the page (the harness proves a pick or a glow sends none).</summary>
    public int GraphUploads { get; private set; }

    private WebView2? _web;
    private Task? _init;
    private bool _ready, _visible;
    private static readonly Dictionary<string, Task<CoreWebView2Environment>> Envs = new(StringComparer.OrdinalIgnoreCase);

    // what the page has, so a message goes only when it would change something
    private ProjectGraph? _sentGraph;
    private int _gen;   // which graph upload the page's picks are about
    private long _sentSig;
    private string? _sentState, _sentGlow, _sentTheme;
    private bool _pendingFit, _pendingFitAnimate;
    private GraphNode? _pendingCenter;
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
    private Window? _window;
    private ThemeService? _themes;

    private NodeGraphGpu()
    {
        Focusable = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ───────────────────────────── life ─────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Disabled) return;
        _window = Window.GetWindow(this);
        if (_window != null) _window.StateChanged += OnWindowState;
        _themes = ThemeService.Current;
        if (_themes != null) _themes.ThemeChanged += OnThemeChanged;
        if (_web == null || Dead(_web)) _init = StartAsync();
        SetVisible(true);
        SendTheme();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window != null) _window.StateChanged -= OnWindowState;
        _window = null;
        if (_themes != null) _themes.ThemeChanged -= OnThemeChanged;
        _themes = null;
        SetVisible(false);
    }

    private void OnWindowState(object? sender, EventArgs e) => SetVisible(_window?.WindowState != WindowState.Minimized && IsLoaded);
    private void OnThemeChanged(string _) => Dispatcher.BeginInvoke(SendTheme);

    private void SetVisible(bool on)
    {
        _visible = on;
        Post($"{{\"t\":\"visible\",\"on\":{(on ? "true" : "false")}}}");
    }

    private static bool Dead(WebView2 web)
    {
        try { _ = web.CoreWebView2; return false; }
        catch { return true; }   // its browser process died: the getter throws until the control is replaced
    }

    private async Task StartAsync()
    {
        _ready = false;
        ResetSent();
        var web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 11, 15, 23) };
        if (TryColor("Surface.Sunken") is { } bg) web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, bg.R, bg.G, bg.B);
        _web = web;
        Child = web;
        try
        {
            Directory.CreateDirectory(DataFolder);
            Task<CoreWebView2Environment> envTask;
            lock (Envs)
                if (!Envs.TryGetValue(DataFolder, out envTask!))
                    Envs[DataFolder] = envTask = CoreWebView2Environment.CreateAsync(null, DataFolder, new CoreWebView2EnvironmentOptions());
            var env = await envTask;
            await web.EnsureCoreWebView2Async(env);
            if (!ReferenceEquals(web, _web)) return;
            var core = web.CoreWebView2;
            var s = core.Settings;
            s.IsStatusBarEnabled = false;
            s.AreDefaultContextMenusEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsPinchZoomEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsSwipeNavigationEnabled = false;
            s.AreDevToolsEnabled = System.Diagnostics.Debugger.IsAttached;
            s.IsWebMessageEnabled = true;
            core.WebMessageReceived += OnMessage;
            core.ProcessFailed += OnProcessFailed;
            core.NavigationStarting += (_, a) => { if (!a.Uri.StartsWith("data:", StringComparison.Ordinal) && a.Uri != "about:blank") a.Cancel = true; };
            core.NewWindowRequested += (_, a) => a.Handled = true;
            web.NavigateToString(Page());
        }
        catch (Exception ex)
        {
            // closed or replaced while it started (a graph window shut at once): not a reason to give up the GPU
            if (!ReferenceEquals(web, _web)) return;
            Fail("WebView2: " + ex.Message);
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log?.Invoke($"Node Graph GPU page: {e.ProcessFailedKind} ({e.Reason}) — starting it again");
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
            or CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.GpuProcessExited)
            Dispatcher.BeginInvoke(() => { if (IsLoaded) _init = StartAsync(); else _web = null; });
    }

    private void Fail(string why)
    {
        Disabled = true;
        DisabledWhy = why;
        _ready = false;
        Log?.Invoke("Node Graph GPU view off: " + why);
        Child = null;
        try { _web?.Dispose(); } catch { }
        _web = null;
        Unavailable?.Invoke(why);
    }

    private static string? _page;
    private static string Page()
    {
        if (_page != null) return _page;
        var asm = typeof(NodeGraphGpu).Assembly;
        using var s = asm.GetManifestResourceStream("NodeGraph/graph.html") ?? throw new FileNotFoundException("NodeGraph/graph.html");
        using var r = new StreamReader(s, Encoding.UTF8);
        return _page = r.ReadToEnd();
    }

    private void ResetSent()
    {
        _sentGraph = null;
        _sentSig = 0;
        _sentState = _sentGlow = _sentTheme = null;
    }

    // ───────────────────────────── from the page ─────────────────────────────

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement m;
        try { m = JsonDocument.Parse(e.WebMessageAsJson).RootElement; }
        catch { return; }
        string t = m.TryGetProperty("t", out var tp) ? tp.GetString() ?? "" : "";
        // a pick made on a graph the page has since replaced names a node by its old index: drop it
        if (t is not ("ready" or "error") && (!m.TryGetProperty("g", out var gp) || !gp.TryGetInt32(out int g) || g != _gen)) return;
        GraphNode? NodeAt(string key) => m.TryGetProperty(key, out var p) && p.TryGetInt32(out int i) && Graph != null && i >= 0 && i < Graph.Nodes.Count ? Graph.Nodes[i] : null;
        GraphEdge? EdgeAt(string key) => m.TryGetProperty(key, out var p) && p.TryGetInt32(out int i) && Graph != null && i >= 0 && i < Graph.Edges.Count ? Graph.Edges[i] : null;
        switch (t)
        {
            case "ready":
                if (m.TryGetProperty("gl", out var gl) && gl.ValueKind == JsonValueKind.True)
                {
                    _ready = true;
                    Renderer = m.TryGetProperty("renderer", out var r) ? r.GetString() : null;
                    Log?.Invoke("Node Graph GPU view ready: " + (Renderer ?? "WebGL2"));
                    Flush();
                    Ready?.Invoke(Renderer ?? "WebGL2");
                }
                else Fail(m.TryGetProperty("why", out var w) ? w.GetString() ?? "no WebGL2" : "no WebGL2");
                break;
            case "select":
                Selected = NodeAt("n");
                NodeSelected?.Invoke(Selected);
                break;
            case "activate":
                if (NodeAt("n") is { } a) NodeActivated?.Invoke(a);
                break;
            case "moved":
                if (NodeAt("n") is { } moved && m.TryGetProperty("x", out var x) && m.TryGetProperty("y", out var y))
                {
                    moved.X = x.GetDouble();
                    moved.Y = y.GetDouble();
                    _sentSig = Signature(Graph);   // the page has these positions already
                    NodeMoved?.Invoke(moved);
                }
                break;
            case "context":
                if (NodeAt("n") is { } c)
                {
                    Selected = c;
                    NodeSelected?.Invoke(c);
                    NodeContextMenu?.Invoke(c, Mouse.GetPosition(this));
                }
                break;
            case "edge":
                SelectedEdge = EdgeAt("e");
                EdgeSelected?.Invoke(SelectedEdge);
                break;
            case "edgeActivate":
                if (EdgeAt("e") is { } ea) EdgeActivated?.Invoke(ea);
                break;
            case "error":
                Log?.Invoke("Node Graph GPU page error: " + (m.TryGetProperty("why", out var why) ? why.GetString() : "?"));
                break;
        }
    }

    // ───────────────────────────── to the page ─────────────────────────────

    public void SetGraph(ProjectGraph? graph, GraphNode? selected)
    {
        Graph = graph;
        Selected = selected;
        if (!_ready) return;   // Flush sends it all once the page is up
        SendTheme();
        SendGraph();
        SendState();
        SendGlow();
    }

    public void SetActivity()
    {
        if (_ready) SendGlow();
    }

    public void FitToView()
    {
        if (!_ready) { _pendingFit = true; _pendingFitAnimate = false; return; }
        Post("{\"t\":\"fit\",\"animate\":true}");
    }

    public void CenterOn(GraphNode n)
    {
        if (!_ready) { _pendingCenter = n; return; }
        if (Graph != null && _index.TryGetValue(n.Id, out int i)) Post($"{{\"t\":\"center\",\"n\":{i}}}");
    }

    private void Flush()
    {
        ResetSent();
        SetVisible(_visible && IsLoaded);
        SendTheme();
        SendGraph();
        SendState();
        SendGlow();
        if (_pendingFit) Post($"{{\"t\":\"fit\",\"animate\":{(_pendingFitAnimate ? "true" : "false")}}}");
        else if (_pendingCenter != null) CenterOn(_pendingCenter);
        _pendingFit = false;
        _pendingCenter = null;
    }

    private void Post(string json)
    {
        if (!_ready || _web == null) return;
        try { _web.CoreWebView2?.PostWebMessageAsJson(json); }
        catch (Exception ex) { Log?.Invoke("Node Graph GPU post failed: " + ex.Message); }
    }

    private void SendGraph()
    {
        if (Graph == null) return;
        long sig = Signature(Graph);
        if (ReferenceEquals(Graph, _sentGraph) && sig == _sentSig) return;
        _index.Clear();
        for (int i = 0; i < Graph.Nodes.Count; i++) _index[Graph.Nodes[i].Id] = i;
        Post(GraphJson(Graph, _index, ++_gen));
        GraphUploads++;
        _sentGraph = Graph;
        _sentSig = sig;
        _sentState = _sentGlow = null;   // a new graph resets the page's pick and glow: send them again
    }

    private void SendState()
    {
        if (Graph == null) return;
        string json = StateJson(Graph, _index, Selected, SelectedEdge, Highlights, HiddenKinds, ShowInferred, GroupByFolder, IsLiveTree, BusyText);
        if (json == _sentState) return;
        _sentState = json;
        Post(json);
    }

    private void SendGlow()
    {
        if (Graph == null) return;
        string json = GlowJson(_index, Changed, ChangeGlow, Active, ActivityGlow, DateTime.UtcNow, out bool any);
        // ages move on, so the text always differs while something glows; nothing glowing twice is sent once
        if (!any && _sentGlow == "none") return;
        _sentGlow = any ? json : "none";
        Post(json);
    }

    private void SendTheme()
    {
        if (!_ready) return;
        string json = ThemeJson();
        if (json == _sentTheme) return;
        _sentTheme = json;
        Post(json);
        if (TryColor("Surface.Sunken") is { } bg && _web != null) _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, bg.R, bg.G, bg.B);
    }

    private Color? TryColor(string key) => (TryFindResource(key) ?? Application.Current?.TryFindResource(key)) switch
    {
        SolidColorBrush b => b.Color,
        GradientBrush g when g.GradientStops.Count > 0 => g.GradientStops[0].Color,
        Color c => c,
        _ => null,
    };

    private string ThemeJson()
    {
        string C(string key, string fallback) => TryColor(key) is { } c ? Hex(c) : fallback;
        var bg = TryColor("Surface.Sunken") ?? Color.FromRgb(11, 15, 23);
        bool light = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255 > 0.5;
        string L(string key) => Loc?.Invoke(key) ?? key;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("t", "theme");
            w.WriteString("bg", Hex(bg));
            w.WriteString("dot", C("Border.Subtle", "#2A3142"));
            w.WriteString("card", C("Surface.Raised", "#161B26"));
            w.WriteString("border", C("Border.Default", "#3A4255"));
            w.WriteString("text", C("Text.Primary", "#FFFFFF"));
            w.WriteString("muted", C("Text.Muted", "#8A93A6"));
            w.WriteString("secondary", C("Text.Secondary", "#B4BCCB"));
            w.WriteString("faint", C("Text.Faint", "#6B7385"));
            w.WriteString("accent", C("Accent.1", "#4F8CFF"));
            w.WriteString("panel", C("Surface.Panel", "#11151E"));
            w.WriteString("inset", TryColor("Surface.Inset") is { } inset ? Hex(inset) : "#00000000");
            w.WriteString("subtle", C("Border.Subtle", "#2A3142"));
            w.WriteBoolean("light", light);
            w.WriteBoolean("reduced", !SystemParameters.ClientAreaAnimation);
            w.WriteStartObject("kinds");
            foreach (GraphNodeKind k in Enum.GetValues<GraphNodeKind>()) w.WriteString(((int)k).ToString(), Hex(NodeGraphCanvas.KindColor(k)));
            w.WriteEndObject();
            w.WriteStartObject("edges");
            foreach (GraphEdgeKind k in Enum.GetValues<GraphEdgeKind>()) w.WriteString(((int)k).ToString(), Hex(NodeGraphCanvas.EdgeColor(k)));
            w.WriteEndObject();
            w.WriteStartObject("legend");
            w.WriteStartArray("kinds");
            foreach (var (k, key) in new[] { (GraphNodeKind.Page, "graph.kind.page"), (GraphNodeKind.Component, "graph.kind.component"),
                                             (GraphNodeKind.Script, "graph.kind.script"), (GraphNodeKind.Style, "graph.kind.style"), (GraphNodeKind.Code, "graph.kind.code") })
            {
                w.WriteStartObject();
                w.WriteString("name", L(key));
                w.WriteString("color", Hex(NodeGraphCanvas.KindColor(k)));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("confirmed", L("graph.confirmed"));
            w.WriteString("inferred", L("graph.inferred"));
            w.WriteString("contains", L("graph.edge.contains"));
            w.WriteString("containsColor", Hex(NodeGraphCanvas.EdgeColor(GraphEdgeKind.Contains)));
            w.WriteString("flow", L("graph.flow"));
            w.WriteString("hint", L("graph.hint"));
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ───────────────────────────── the messages (pure, for the harness) ─────────────────────────────

    public static string Hex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>What the page draws from: positions, sizes, names, kinds, hidden and collapsed. A change here means the
    /// graph goes over again; anything else (a pick, a filter, the glow) is a small message.</summary>
    public static long Signature(ProjectGraph? g)
    {
        if (g == null) return 0;
        var h = new HashCode();
        h.Add(g.Nodes.Count);
        h.Add(g.Edges.Count);
        foreach (var n in g.Nodes)
        {
            h.Add(n.X); h.Add(n.Y); h.Add(n.Width); h.Add(n.Height);
            h.Add(n.Hidden); h.Add(n.Collapsed); h.Add(n.Kind);
            h.Add(n.Label); h.Add(n.Detail);
        }
        return h.ToHashCode() | ((long)g.Nodes.Count << 32);
    }

    public static string GraphJson(ProjectGraph g, IReadOnlyDictionary<string, int> index, int gen = 0)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("t", "graph");
            w.WriteNumber("gen", gen);
            w.WriteStartArray("nodes");
            foreach (var n in g.Nodes)
            {
                w.WriteStartObject();
                w.WriteString("id", n.Id);
                w.WriteString("l", n.Label);
                if (!string.IsNullOrEmpty(n.Detail)) w.WriteString("d", n.Detail);
                w.WriteNumber("k", (int)n.Kind);
                w.WriteNumber("x", n.X);
                w.WriteNumber("y", n.Y);
                w.WriteNumber("w", n.Width);
                w.WriteNumber("h", n.Height);
                if (n.Collapsed) w.WriteBoolean("c", true);
                if (n.Hidden) w.WriteBoolean("hidden", true);
                if (n.Folder.Length > 0) w.WriteString("f", n.Folder);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("edges");
            foreach (var e in g.Edges)
            {
                w.WriteStartObject();
                w.WriteNumber("a", index.TryGetValue(e.From, out int a) ? a : -1);
                w.WriteNumber("b", index.TryGetValue(e.To, out int b) ? b : -1);
                w.WriteNumber("k", (int)e.Kind);
                if (e.Inferred) w.WriteBoolean("i", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string StateJson(ProjectGraph g, IReadOnlyDictionary<string, int> index, GraphNode? sel, GraphEdge? edge, ISet<string> highlights,
        ISet<GraphEdgeKind> hidden, bool inferred, bool group, bool live, string? busy)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("t", "state");
            w.WriteNumber("sel", sel != null && index.TryGetValue(sel.Id, out int s) ? s : -1);
            w.WriteNumber("edge", edge == null ? -1 : g.Edges.IndexOf(edge));
            w.WriteStartArray("hl");
            foreach (var id in highlights) if (index.TryGetValue(id, out int i)) w.WriteNumberValue(i);
            w.WriteEndArray();
            w.WriteStartArray("hidden");
            foreach (var k in hidden.OrderBy(k => k)) w.WriteNumberValue((int)k);
            w.WriteEndArray();
            w.WriteBoolean("inferred", inferred);
            w.WriteBoolean("group", group);
            w.WriteBoolean("live", live);
            if (!string.IsNullOrEmpty(busy)) w.WriteString("busy", busy);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>[node, age ms, kind, duration ms] for each node that glows now: 0 its file changed, 1 an agent reads it,
    /// 2 an agent writes it (a write wins over a change of the same file).</summary>
    public static string GlowJson(IReadOnlyDictionary<string, int> index, IReadOnlyDictionary<string, DateTime>? changed, TimeSpan changeFor,
        IReadOnlyDictionary<string, (DateTime At, bool Write)>? active, TimeSpan activeFor, DateTime now, out bool any)
    {
        var items = new Dictionary<int, (double Age, int Kind, double Dur)>();
        if (changed != null)
            foreach (var (id, at) in changed)
                if (now - at < changeFor && index.TryGetValue(id, out int i)) items[i] = ((now - at).TotalMilliseconds, 0, changeFor.TotalMilliseconds);
        if (active != null)
            foreach (var (id, a) in active)
                if (now - a.At < activeFor && index.TryGetValue(id, out int i)
                    && (!items.TryGetValue(i, out var had) || a.Write || had.Age > (now - a.At).TotalMilliseconds))
                    items[i] = ((now - a.At).TotalMilliseconds, a.Write ? 2 : 1, activeFor.TotalMilliseconds);
        any = items.Count > 0;
        var sb = new StringBuilder("{\"t\":\"glow\",\"items\":[");
        bool first = true;
        foreach (var (i, v) in items)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('[').Append(i).Append(',').Append(Math.Max(0, (long)v.Age)).Append(',').Append(v.Kind).Append(',').Append((long)v.Dur).Append(']');
        }
        return sb.Append("]}").ToString();
    }
}
