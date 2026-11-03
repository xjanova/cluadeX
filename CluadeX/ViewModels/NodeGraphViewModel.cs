using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using CluadeX.Services;
using CluadeX.Services.UiStudio;

namespace CluadeX.ViewModels;

/// <summary>A node listed in the details panel (imports / imported by / children), with its wire's colour and the
/// line that made the wire (click it to open that line).</summary>
public sealed record GraphLink(string Id, string Label, string Kind, System.Windows.Media.Brush? Color = null, GraphEdge? Edge = null)
{
    public string LineText => Edge?.First is { } e ? ":" + e.Line : "";
    public bool HasLine => Edge?.First != null;
}

/// <summary>One line of code an edge stands on: "file:line" and the line itself.</summary>
public sealed record GraphEvidenceItem(string Where, string Code, string File, int Line);

/// <summary>A kind of connection in the filter row: its colour, how many wires of it the graph has, on or off.</summary>
public sealed class EdgeKindFilter : ViewModelBase
{
    private readonly Action<EdgeKindFilter> _changed;
    public EdgeKindFilter(GraphEdgeKind kind, string label, System.Windows.Media.Brush color, int count, bool isOn, Action<EdgeKindFilter> changed)
    {
        Kind = kind; Label = label; Color = color; Count = count; _isOn = isOn; _changed = changed;
    }
    public GraphEdgeKind Kind { get; }
    public string Label { get; }
    public System.Windows.Media.Brush Color { get; }
    public int Count { get; }
    private bool _isOn;
    public bool IsOn { get => _isOn; set { if (SetProperty(ref _isOn, value)) _changed(this); } }
}

/// <summary>
/// The Node Graph page: the project drawn as nodes you can drag — files and components wired by what they really
/// import and render ("Project" view), or the running page's element tree from the UI Studio ("Live UI" view)
/// where a node is the element itself: select it, open its exact line, move, duplicate or delete it.
/// Node positions you drag are remembered per project.
/// </summary>
public sealed class NodeGraphViewModel : ViewModelBase
{
    private readonly StudioService _studio;
    private readonly UiStudioViewModel _ui;
    private readonly CodeEditorViewModel _editor;
    private readonly LocalizationService _loc;
    private readonly SettingsService _settings;
    private readonly DebugLogService? _log;
    private CancellationTokenSource? _buildCts;
    private readonly DispatcherTimer _saveTimer;
    private Dictionary<string, double[]> _saved = new();
    private string? _savedFor;

    public NodeGraphViewModel(StudioService studio, UiStudioViewModel ui, CodeEditorViewModel editor, LocalizationService loc,
        SettingsService settings, DebugLogService? log = null)
    {
        _studio = studio;
        _ui = ui;
        _editor = editor;
        _loc = loc;
        _settings = settings;
        _log = log;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveLayout(); };

        studio.TreeChanged += () => { if (IsLive) BuildLive(); };
        ui.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(UiStudioViewModel.IsLockedByAi)) OnPropertyChanged(nameof(CanEditLive)); };
        studio.SelectionChanged += () =>
        {
            if (IsLive && studio.Selection is { } s && Graph?.ById.TryGetValue("n" + s.NodeId, out var n) == true && !ReferenceEquals(n, SelectedNode))
            {
                SelectedNode = n;
                FocusRequested?.Invoke(n);
            }
            OnPropertyChanged(nameof(LiveSelectionLabel));
            OnPropertyChanged(nameof(CanEditLive));
        };
        studio.ProjectChanged += () =>
        {
            if (string.Equals(_builtFor, RootPath, StringComparison.OrdinalIgnoreCase) && Graph != null) return;   // same folder re-detected
            // Another project: a build still reading the last one stops, and a drag still waiting to be saved goes to
            // that project's file before its positions are replaced.
            _buildCts?.Cancel();
            _liveTimer!.Stop();
            FlushLayout();
            RecentlyChanged.Clear();
            _dirty = false;
            Graph = null;
            SelectedNode = null;
            _builtFor = null;
            EmptyText = "";
            OnPropertyChanged(nameof(RootName));
            if (IsVisible) _ = RefreshAsync();
            else QueuePrebuild();
        };
        studio.FilesChanged += OnFilesChanged;
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _liveTimer.Tick += (_, _) => LiveTick();
        _prebuildTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _prebuildTimer.Tick += (_, _) => { _prebuildTimer.Stop(); if (!IsVisible && Graph == null && IsProject) _ = RefreshAsync(); };

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        AutoLayoutCommand = new RelayCommand(() => { if (Graph != null) { ForgetLayout(); Layout(Graph, useSaved: false); GraphChanged?.Invoke(); FitRequested?.Invoke(); } });
        FitCommand = new RelayCommand(() => FitRequested?.Invoke());
        SetModeCommand = new RelayCommand<string>(m => Mode = m ?? "project");
        OpenFileCommand = new RelayCommand(OpenSelected, () => SelectedNode?.FullPath != null || SelectedNode?.Src != null);
        GoToLinkCommand = new RelayCommand<GraphLink>(l => { if (l != null && Graph?.ById.TryGetValue(l.Id, out var n) == true) { SelectedNode = n; FocusRequested?.Invoke(n); } });
        ShowInStudioCommand = new RelayCommand(() => { if (SelectedNode?.DomId is int id) { _studio.SelectNode(id); OpenPageRequested?.Invoke("UiStudio"); } });
        OpenStudioCommand = new RelayCommand(() => OpenPageRequested?.Invoke("UiStudio"));
        StructureCommand = new RelayCommand<string>(cmd => { if (cmd != null) _ui.Structure(cmd); }, _ => CanEditLive);
        ToggleCollapseCommand = new RelayCommand(() => { if (SelectedNode != null) ToggleCollapse(SelectedNode); });
        OpenEvidenceCommand = new RelayCommand<GraphEvidenceItem>(ev => { if (ev != null) OpenEvidence(ev.File, ev.Line); });
        OpenLinkEvidenceCommand = new RelayCommand<GraphLink>(l => { if (l?.Edge?.First is { } ev) OpenEvidence(ev.File, ev.Line); });
        AllKindsCommand = new RelayCommand(() =>
        {
            bool allOn = EdgeFilters.All(f => f.IsOn);
            _bulk = true;
            foreach (var f in EdgeFilters) f.IsOn = !allOn;
            _bulk = false;
            FiltersChanged();
        });
    }

    private string T(string key) => _loc.T(key);
    private string T(string key, params object[] args) => _loc.T(key, args);
    internal string Tr(string key) => _loc.T(key);

    public event Action? GraphChanged;
    public event Action? FitRequested;
    public event Action<GraphNode>? FocusRequested;
    public event Action<string>? OpenPageRequested;

    /// <summary>Set by the view while the page is on screen. Hidden, the graph only notes that files changed; shown,
    /// it is drawn at once as it was and brought up to date right after (positions and the view kept).</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            _studio.KeepPageAwake = value && IsLive;
            _ui.SetGraphOnScreen(value);
            if (value && _dirty && IsProject && Graph != null) LiveTick();
        }
    }
    private bool _isVisible;

    // ───────────────────────────── following the files ─────────────────────────────

    private readonly DispatcherTimer _liveTimer, _prebuildTimer;
    private bool _dirty;
    private string? _builtFor;

    /// <summary>Nodes whose file changed lately (id → when): the canvas makes them glow for a few seconds, so an edit
    /// by the AI in the chat shows where it landed.</summary>
    public Dictionary<string, DateTime> RecentlyChanged { get; } = new(StringComparer.OrdinalIgnoreCase);
    public static readonly TimeSpan ChangeGlow = TimeSpan.FromSeconds(4);

    private void OnFilesChanged(IReadOnlyCollection<string> paths)
    {
        string? root = RootPath;
        if (root == null) return;
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var now = DateTime.UtcNow;
        foreach (var p in paths)
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) RecentlyChanged[p[prefix.Length..].Replace('\\', '/')] = now;
        foreach (var old in RecentlyChanged.Where(x => now - x.Value > ChangeGlow).Select(x => x.Key).ToList()) RecentlyChanged.Remove(old);
        if (Graph == null && !IsVisible) return;   // nothing built yet: the first build reads it all anyway
        _dirty = true;
        if (!IsVisible || IsLive) return;   // (the live tree follows the page, which reloads by itself)
        // Debounced (half a second of quiet), but never put off for more than MaxLiveWait: an AI writing a file every
        // 400 ms still sees the graph follow. A build already running is not restarted — it is followed by one more.
        _pendingSince ??= now;
        _liveTimer.Stop();
        if (now - _pendingSince.Value >= MaxLiveWait) LiveTick();
        else _liveTimer.Start();
    }

    public static readonly TimeSpan MaxLiveWait = TimeSpan.FromSeconds(3);
    private DateTime? _pendingSince;
    private Task? _liveBuild;
    private bool _liveAgain;

    /// <summary>The live update fires: one build at a time; changes during it make exactly one more build after it.</summary>
    private void LiveTick()
    {
        _liveTimer.Stop();
        _pendingSince = null;
        if (_liveBuild is { IsCompleted: false }) { _liveAgain = true; return; }
        _liveBuild = RunLive();
    }

    private async Task RunLive()
    {
        do
        {
            _liveAgain = false;
            await RefreshAsync(keepView: true);
        }
        while (_liveAgain && IsVisible && IsProject);
    }

    /// <summary>How many project builds have run (the harness counts them to prove a burst of changes is one build).</summary>
    public int BuildCount { get; private set; }

    /// <summary>Build the graph once in the background after a project is opened, so the first visit shows it at once.
    /// One read of the project's files; nothing more happens while the page stays hidden.</summary>
    private void QueuePrebuild()
    {
        _prebuildTimer.Stop();
        if (RootPath != null) _prebuildTimer.Start();
    }

    // ───────────────────────────── mode ─────────────────────────────

    private string _mode = "project";
    public string Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsLive));
            OnPropertyChanged(nameof(IsProject));
            _studio.KeepPageAwake = IsVisible && IsLive;
            _builtFor = null;
            SelectedNode = null;
            _ = RefreshAsync();
        }
    }
    public bool IsLive => Mode == "live";
    public bool IsProject => !IsLive;

    private bool _showPackages;
    public bool ShowPackages { get => _showPackages; set { if (SetProperty(ref _showPackages, value) && IsProject) _ = RefreshAsync(); } }

    private string _search = "";
    public string Search
    {
        get => _search;
        set
        {
            if (!SetProperty(ref _search, value ?? "")) return;
            Matches = Graph == null || _search.Trim().Length == 0
                ? new HashSet<string>()
                : Graph.Nodes.Where(n => !n.Hidden && (n.Label.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase)
                                                       || (n.Detail?.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase) ?? false)))
                    .Select(n => n.Id).ToHashSet();
            OnPropertyChanged(nameof(Matches));
            OnPropertyChanged(nameof(MatchText));
            if (Graph != null && Matches.Count > 0 && Graph.ById.TryGetValue(Matches.First(), out var first)) FocusRequested?.Invoke(first);
            GraphChanged?.Invoke();
        }
    }
    public HashSet<string> Matches { get; private set; } = new();
    public string MatchText => Search.Trim().Length == 0 ? "" : T("graph.matches", Matches.Count);

    // ───────────────────────────── graph ─────────────────────────────

    private ProjectGraph? _graph;
    public ProjectGraph? Graph
    {
        get => _graph;
        private set
        {
            _graph = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasGraph));
            OnPropertyChanged(nameof(Summary));
            GraphChanged?.Invoke();
        }
    }
    public bool HasGraph => Graph is { Nodes.Count: > 0 };

    private bool _busy;
    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }

    private string _emptyText = "";
    public string EmptyText { get => _emptyText; private set => SetProperty(ref _emptyText, value); }

    public string Summary
    {
        get
        {
            if (Graph == null) return "";
            int nodes = Graph.Nodes.Count(n => !n.Hidden);
            string s = T("graph.summary", nodes, Graph.Edges.Count);
            int inferred = Graph.Edges.Count(e => e.Inferred);
            if (inferred > 0) s += " · " + T("graph.summaryInferred", inferred);
            if (Graph.Truncated) s += " · " + T(Graph.TruncatedWhy == "size" ? "graph.truncatedSize" : "graph.truncated", ProjectGraphBuilder.DefaultMaxFiles);
            if (Graph.Skipped > 0) s += " · " + T("graph.skipped", Graph.Skipped);
            return s;
        }
    }

    public string RootName => RootPath is { } p ? Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar)) : "";

    /// <summary>The Studio's project — the project of the chat on screen. No fallback to the app's folder: a chat
    /// without a project shows an empty graph, not whatever folder was open before it.</summary>
    private string? RootPath => _studio.HasProject ? _studio.Root : null;

    public Task RefreshAsync() => RefreshAsync(keepView: false);

    /// <summary>Read the project again. <paramref name="keepView"/>: a live update (files changed) — nodes stay where
    /// they are on screen, the selection stays, the view is not refitted and no busy text covers the graph.</summary>
    private async Task RefreshAsync(bool keepView)
    {
        OnPropertyChanged(nameof(RootName));
        if (IsLive) { BuildLive(); return; }
        string? root = RootPath;
        _buildCts?.Cancel();
        if (root == null) { IsBusy = false; Graph = null; EmptyText = T("graph.noProject"); return; }

        var cts = _buildCts = new CancellationTokenSource();
        var old = Graph;
        keepView &= old != null && string.Equals(_builtFor, root, StringComparison.OrdinalIgnoreCase);
        _dirty = false;
        if (!keepView) IsBusy = true;
        try
        {
            bool packages = ShowPackages;
            BuildCount++;
            var graph = await Task.Run(() => ProjectGraphBuilder.Build(root, packages, cts.Token), cts.Token);
            // the chat (so the project) changed while this was reading, or a newer build took over: drop it
            if (cts.IsCancellationRequested || IsLive || !string.Equals(root, RootPath, StringComparison.OrdinalIgnoreCase)) return;
            _log?.Debug("NodeGraph", $"{graph.Nodes.Count} nodes, {graph.Edges.Count} edges ({graph.Edges.Count(e => e.Inferred)} inferred) — {graph.Timings}");
            LoadLayout(root);
            Layout(graph, useSaved: true);
            if (keepView)
            {
                // where the user sees each node now wins over a fresh layout; new files appear near what imports them
                foreach (var n in graph.Nodes)
                    if (old!.ById.TryGetValue(n.Id, out var was)) { n.X = was.X; n.Y = was.Y; }
                    else if (graph.Edges.FirstOrDefault(e => e.To == n.Id && old.ById.ContainsKey(e.From)) is { } e && old.ById[e.From] is { } parent)
                    { n.X = parent.X + parent.Width + ColGap; n.Y = parent.Y + parent.Height + RowGap; }
            }
            string? keep = SelectedNode?.Id;
            var keepEdge = SelectedEdge;
            _builtFor = root;
            RebuildFilters(graph);
            Graph = graph;
            EmptyText = graph.Nodes.Count == 0 ? T("graph.empty") : "";
            if (keep != null) SelectedNode = graph.ById.TryGetValue(keep, out var again) ? again : null;
            if (keepEdge != null)
                SelectedEdge = graph.Edges.FirstOrDefault(e => e.Kind == keepEdge.Kind && string.Equals(e.From, keepEdge.From, StringComparison.OrdinalIgnoreCase)
                                                             && string.Equals(e.To, keepEdge.To, StringComparison.OrdinalIgnoreCase));
            if (!keepView) FitRequested?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log?.Warn("NodeGraph", "graph build failed", ex);
            EmptyText = T("graph.failed");
        }
        finally
        {
            if (ReferenceEquals(_buildCts, cts)) IsBusy = false;
        }
    }

    private void BuildLive()
    {
        var tree = _studio.Tree;
        if (tree == null) { Graph = null; EmptyText = T(_studio.HasProject ? "graph.liveEmpty" : "graph.noProject"); return; }
        var g = new ProjectGraph { Truncated = _studio.TreeTruncated };
        void Walk(DomNode d, string? parent, int depth)
        {
            string id = "n" + d.NodeId;
            g.Add(new GraphNode
            {
                Id = id, Label = d.Label, Detail = d.Snippet ?? (d.Src != null ? ShortSrc(d.Src) : null),
                Kind = GraphNodeKind.Element, Src = d.Src, DomId = d.NodeId, Collapsed = depth >= 3 && d.Kids.Any(),
                Width = 168, Height = 40,
            });
            if (parent != null) g.Edges.Add(new GraphEdge(parent, id, GraphEdgeKind.Contains));
            foreach (var k in d.Kids) Walk(k, id, depth + 1);
        }
        Walk(tree, null, 0);
        // keep what the user expanded / collapsed before the page changed
        if (Graph != null && IsLive)
            foreach (var n in g.Nodes)
                if (Graph.ById.TryGetValue(n.Id, out var old)) n.Collapsed = old.Collapsed;
        ApplyCollapse(g);
        TreeLayout(g);
        // The overlay sends the tree again after any change in the page (a clock ticking, a slide turning): the same
        // elements are the same graph, which is kept — no new graph to draw, nothing re-laid out under a drag.
        if (Graph != null && SameTree(Graph, g)) { EmptyText = ""; return; }
        string? keep = SelectedNode?.Id;
        Graph = g;
        EmptyText = "";
        SelectedNode = keep != null && g.ById.TryGetValue(keep, out var again) ? again : null;
    }

    /// <summary>The same elements, text, sources, collapsed state and places, wired the same way.</summary>
    private static bool SameTree(ProjectGraph a, ProjectGraph b)
    {
        if (a.Truncated != b.Truncated || a.Nodes.Count != b.Nodes.Count || a.Edges.Count != b.Edges.Count) return false;
        for (int i = 0; i < a.Nodes.Count; i++)
        {
            GraphNode x = a.Nodes[i], y = b.Nodes[i];
            if (x.Id != y.Id || x.Kind != y.Kind || x.Label != y.Label || x.Detail != y.Detail || x.Src != y.Src
                || x.Collapsed != y.Collapsed || x.Hidden != y.Hidden || x.X != y.X || x.Y != y.Y) return false;
        }
        for (int i = 0; i < a.Edges.Count; i++)
            if (a.Edges[i].From != b.Edges[i].From || a.Edges[i].To != b.Edges[i].To || a.Edges[i].Kind != b.Edges[i].Kind) return false;
        return true;
    }

    private static string ShortSrc(string src)
    {
        int slash = Math.Max(src.LastIndexOf('/'), src.LastIndexOf('\\'));
        return slash >= 0 ? src[(slash + 1)..] : src;
    }

    private void ToggleCollapse(GraphNode node)
    {
        if (Graph == null || !IsLive) return;
        node.Collapsed = !node.Collapsed;
        ApplyCollapse(Graph);
        TreeLayout(Graph);
        GraphChanged?.Invoke();
        FocusRequested?.Invoke(node);
    }

    public void NodeActivated(GraphNode node)
    {
        if (IsLive && Graph != null && Graph.Edges.Any(e => e.From == node.Id)) ToggleCollapse(node);
        else OpenSelected();
    }

    private static void ApplyCollapse(ProjectGraph g)
    {
        var children = g.Edges.GroupBy(e => e.From).ToDictionary(x => x.Key, x => x.Select(e => e.To).ToList());
        foreach (var n in g.Nodes) n.Hidden = false;
        void Hide(string id)
        {
            if (!children.TryGetValue(id, out var kids)) return;
            foreach (var k in kids) { g.ById[k].Hidden = true; Hide(k); }
        }
        foreach (var n in g.Nodes.Where(n => n.Collapsed && !n.Hidden).ToList()) Hide(n.Id);
    }

    // ───────────────────────────── layout ─────────────────────────────

    private const double ColGap = 96, RowGap = 16;
    private const int MaxColumn = 40;

    /// <summary>Layered layout (left → right along the wires): a file sits one column right of whatever uses it;
    /// within a column, a few barycenter sweeps (down and back up) order nodes by the rows of their neighbours so
    /// wires cross less, and a column taller than <see cref="MaxColumn"/> wraps into several. Files with no wires go
    /// to a grid underneath. Grouped by folder, each folder is its own block of that same order. Positions the user
    /// dragged before are kept (separately for the grouped view).</summary>
    private void Layout(ProjectGraph g, bool useSaved)
    {
        var nodes = g.Nodes.Where(n => !n.Hidden).ToList();
        var connected = new HashSet<string>(g.Edges.SelectMany(e => new[] { e.From, e.To }));
        var outs = g.Edges.GroupBy(e => e.From).ToDictionary(x => x.Key, x => x.Select(e => e.To).ToList());
        var ins = g.Edges.GroupBy(e => e.To).ToDictionary(x => x.Key, x => x.Select(e => e.From).ToList());

        // Kahn's algorithm with cycle breaking (imports can be circular)
        var indeg = nodes.Where(n => connected.Contains(n.Id)).ToDictionary(n => n.Id, n => ins.TryGetValue(n.Id, out var l) ? l.Count : 0);
        var layer = indeg.Keys.ToDictionary(k => k, _ => 0);
        var done = new HashSet<string>();
        var queue = new Queue<string>(indeg.Where(x => x.Value == 0).Select(x => x.Key).OrderBy(Rank));
        while (done.Count < indeg.Count)
        {
            if (queue.Count == 0)
            {
                var pick = indeg.Where(x => !done.Contains(x.Key)).OrderBy(x => x.Value).ThenBy(x => Rank(x.Key)).First().Key;
                queue.Enqueue(pick);
            }
            var id = queue.Dequeue();
            if (!done.Add(id)) continue;
            if (!outs.TryGetValue(id, out var targets)) continue;
            foreach (var t in targets)
            {
                if (done.Contains(t) || !indeg.ContainsKey(t)) continue;
                layer[t] = Math.Max(layer[t], layer[id] + 1);
                if (--indeg[t] == 0) queue.Enqueue(t);
            }
        }

        var columns = layer.GroupBy(x => x.Value).OrderBy(x => x.Key).Select(x => x.Select(y => g.ById[y.Key]).ToList()).ToList();
        // order: the first column by kind/name, then sweeps that sort each column by the mean row of its neighbours
        // in the column before (going right) or after (going back left)
        var row = new Dictionary<string, double>();
        void Number(List<GraphNode> col) { for (int i = 0; i < col.Count; i++) row[col[i].Id] = i; }
        double Bary(GraphNode n, Dictionary<string, List<string>> side)
            => side.TryGetValue(n.Id, out var p) && p.Any(row.ContainsKey) ? p.Where(row.ContainsKey).Average(x => row[x]) : row.GetValueOrDefault(n.Id, double.MaxValue);
        for (int c = 0; c < columns.Count; c++)
        {
            columns[c] = c == 0 ? columns[c].OrderBy(n => Rank(n.Id)).ThenBy(n => n.Id).ToList()
                                : columns[c].OrderBy(n => Bary(n, ins)).ThenBy(n => n.Label).ToList();
            Number(columns[c]);
        }
        for (int sweep = 0; sweep < 2; sweep++)
        {
            for (int c = columns.Count - 2; c >= 0; c--) { columns[c] = columns[c].OrderBy(n => Bary(n, outs)).ThenBy(n => n.Label).ToList(); Number(columns[c]); }
            for (int c = 1; c < columns.Count; c++) { columns[c] = columns[c].OrderBy(n => Bary(n, ins)).ThenBy(n => n.Label).ToList(); Number(columns[c]); }
        }

        var loose = nodes.Where(n => !connected.Contains(n.Id)).OrderBy(n => n.Kind).ThenBy(n => n.Id).ToList();
        if (GroupByFolder) LayoutGroups(columns, loose);
        else
        {
            // a very tall column wraps into side-by-side sub-columns, so a folder of 300 widgets is not a 15 000 px strip
            var placed = new List<List<GraphNode>>();
            var layerOf = new List<int>();
            for (int c = 0; c < columns.Count; c++)
                for (int i = 0; i < columns[c].Count; i += MaxColumn) { placed.Add(columns[c].Skip(i).Take(MaxColumn).ToList()); layerOf.Add(c); }
            double maxHeight = placed.Count == 0 ? 0 : placed.Max(c => c.Count * (c[0].Height + RowGap));
            for (int c = 0; c < placed.Count; c++)
            {
                var col = placed[c];
                double h = col.Count * (col[0].Height + RowGap);
                double y0 = (maxHeight - h) / 2;
                for (int i = 0; i < col.Count; i++)
                {
                    col[i].Layer = layerOf[c];
                    col[i].X = c * (col[i].Width + ColGap);
                    col[i].Y = y0 + i * (col[i].Height + RowGap);
                }
            }

            // unconnected files: a grid under the wired part
            if (loose.Count > 0)
            {
                int perRow = Math.Max(4, (int)Math.Ceiling(Math.Sqrt(loose.Count) * 1.6));
                double top = maxHeight + 80;
                for (int i = 0; i < loose.Count; i++)
                {
                    loose[i].X = (i % perRow) * (loose[i].Width + 24);
                    loose[i].Y = top + (i / perRow) * (loose[i].Height + RowGap);
                }
            }
        }

        if (useSaved)
            foreach (var n in g.Nodes)
                if (_saved.TryGetValue(SaveKey(n.Id), out var xy) && xy.Length == 2) { n.X = xy[0]; n.Y = xy[1]; }

        int Rank(string id)
        {
            var n = g.ById[id];
            return n.Kind switch { GraphNodeKind.Page => 0, GraphNodeKind.Script => 1, GraphNodeKind.Component => 2, GraphNodeKind.Style => 3, _ => 4 };
        }
    }

    /// <summary>Group by folder: each folder is a block of its own files in the layered order (one column per layer
    /// they use, wrapped when tall, unwired files last); the blocks are packed in rows, folders in path order, so a
    /// folder's frame (drawn by the canvas) never overlaps another's.</summary>
    private static void LayoutGroups(List<List<GraphNode>> columns, List<GraphNode> loose)
    {
        const double Pad = 14, Head = 30, BlockGap = 40;
        var order = new Dictionary<GraphNode, (int col, int row)>();
        for (int c = 0; c < columns.Count; c++)
            for (int i = 0; i < columns[c].Count; i++) order[columns[c][i]] = (c, i);
        int looseCol = columns.Count;
        for (int i = 0; i < loose.Count; i++) order[loose[i]] = (looseCol, i);

        var blocks = new List<(List<List<GraphNode>> cols, double w, double h)>();
        foreach (var folder in order.Keys.GroupBy(n => n.Kind == GraphNodeKind.Package ? "\u0001" : n.Folder, StringComparer.OrdinalIgnoreCase)
                                         .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var cols = new List<List<GraphNode>>();
            foreach (var layer in folder.GroupBy(n => order[n].col).OrderBy(x => x.Key))
            {
                var list = layer.OrderBy(n => order[n].row).ToList();
                int per = layer.Key == looseCol ? Math.Max(4, (int)Math.Ceiling(Math.Sqrt(list.Count))) : MaxColumn;
                for (int i = 0; i < list.Count; i += per) cols.Add(list.Skip(i).Take(per).ToList());
            }
            double w = cols.Sum(c => c[0].Width) + Math.Max(0, cols.Count - 1) * (ColGap / 2) + 2 * Pad;
            double h = cols.Max(c => c.Count * (c[0].Height + RowGap)) - RowGap + Head + Pad;
            blocks.Add((cols, w, h));
        }
        if (blocks.Count == 0) return;

        // shelf packing: rows about as wide as the square of the total area (a little wider — screens are wide)
        double shelf = Math.Max(blocks.Max(b => b.w), Math.Sqrt(blocks.Sum(b => (b.w + BlockGap) * (b.h + BlockGap))) * 1.6);
        double x = 0, y = 0, rowH = 0;
        foreach (var (cols, w, h) in blocks)
        {
            if (x > 0 && x + w > shelf) { x = 0; y += rowH + BlockGap; rowH = 0; }
            double cx = x + Pad;
            foreach (var col in cols)
            {
                for (int i = 0; i < col.Count; i++)
                {
                    col[i].Layer = 0;
                    col[i].X = cx;
                    col[i].Y = y + Head + i * (col[i].Height + RowGap);
                }
                cx += col[0].Width + ColGap / 2;
            }
            x += w + BlockGap;
            rowH = Math.Max(rowH, h);
        }
    }

    /// <summary>Tidy tree for the live element tree: depth → column, leaves stacked, parents centred on their children.</summary>
    private static void TreeLayout(ProjectGraph g)
    {
        var kids = g.Edges.GroupBy(e => e.From).ToDictionary(x => x.Key, x => x.Select(e => e.To).ToList());
        var targets = g.Edges.Select(e => e.To).ToHashSet();
        var roots = g.Nodes.Where(n => !targets.Contains(n.Id)).ToList();
        double nextY = 0;
        double Place(GraphNode n, int depth)
        {
            n.X = depth * (n.Width + 56);
            n.Layer = depth;
            var visible = !n.Collapsed && kids.TryGetValue(n.Id, out var k) ? k.Select(id => g.ById[id]).Where(c => !c.Hidden).ToList() : new List<GraphNode>();
            if (visible.Count == 0)
            {
                n.Y = nextY;
                nextY += n.Height + 10;
                return n.Y;
            }
            var ys = visible.Select(c => Place(c, depth + 1)).ToList();
            n.Y = (ys.First() + ys.Last()) / 2;
            return n.Y;
        }
        foreach (var r in roots) { Place(r, 0); nextY += 30; }
    }

    // ───────────────────────────── saved positions ─────────────────────────────

    private string LayoutFile(string root)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..16];
        return Path.Combine(_settings.DataRoot, "UiStudio", "graph", hash + ".json");
    }

    private void FlushLayout()
    {
        if (!_saveTimer.IsEnabled) return;
        _saveTimer.Stop();
        SaveLayout();
    }

    private void LoadLayout(string root)
    {
        if (string.Equals(_savedFor, root, StringComparison.OrdinalIgnoreCase)) return;
        FlushLayout();
        _savedFor = root;
        _saved = new();
        try
        {
            string f = LayoutFile(root);
            if (File.Exists(f)) _saved = JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(f)) ?? new();
        }
        catch { _saved = new(); }   // a broken layout file is just forgotten
    }

    private void SaveLayout()
    {
        if (_savedFor == null) return;
        try
        {
            string f = LayoutFile(_savedFor);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, JsonSerializer.Serialize(_saved));
        }
        catch (Exception ex) { _log?.Warn("NodeGraph", "could not save layout", ex); }
    }

    /// <summary>Where a dragged node is remembered: the grouped view keeps its own positions ("g:" + id).</summary>
    private string SaveKey(string id) => GroupByFolder ? "g:" + id : id;

    private void ForgetLayout()
    {
        foreach (var k in _saved.Keys.Where(k => k.StartsWith("g:", StringComparison.Ordinal) == GroupByFolder).ToList()) _saved.Remove(k);
        _saveTimer.Stop();
        SaveLayout();
    }

    /// <summary>The canvas moved a node (drag ended).</summary>
    public void NodeMoved(GraphNode node)
    {
        if (IsLive) return;   // the live tree is re-laid out from the page each time
        _saved[SaveKey(node.Id)] = new[] { Math.Round(node.X, 1), Math.Round(node.Y, 1) };
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ───────────────────────────── selection ─────────────────────────────

    private GraphNode? _selected;
    public GraphNode? SelectedNode
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            _selected = value;
            if (value != null && _selectedEdge != null) SelectedEdge = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAnySelection));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedPath));
            OnPropertyChanged(nameof(SelectedKind));
            Outgoing.Clear();
            Incoming.Clear();
            if (value != null && Graph != null)
            {
                foreach (var e in Graph.Edges.Where(e => e.From == value.Id))
                    if (Graph.ById.TryGetValue(e.To, out var to)) Outgoing.Add(new GraphLink(to.Id, to.Label, LinkKind(e), EdgeBrush(e.Kind), e));
                foreach (var e in Graph.Edges.Where(e => e.To == value.Id))
                    if (Graph.ById.TryGetValue(e.From, out var from)) Incoming.Add(new GraphLink(from.Id, from.Label, LinkKind(e), EdgeBrush(e.Kind), e));
                if (IsLive && value.DomId is int dom) _studio.SelectNode(dom);
            }
            GraphChanged?.Invoke();
        }
    }

    public bool HasSelection => SelectedNode != null;
    public string SelectedTitle => SelectedNode?.Label ?? "";
    public string SelectedPath => SelectedNode == null ? "" : SelectedNode.Kind == GraphNodeKind.Element ? SelectedNode.Src ?? T("graph.noSource")
        : SelectedNode.Kind == GraphNodeKind.Package ? T("graph.package") : (SelectedNode.Id + (SelectedNode.Lines > 0 ? $" · {SelectedNode.Lines} lines" : ""));
    public string SelectedKind => SelectedNode == null ? "" : T("graph.kind." + SelectedNode.Kind.ToString().ToLowerInvariant());
    public ObservableCollection<GraphLink> Outgoing { get; } = new();
    public ObservableCollection<GraphLink> Incoming { get; } = new();

    public string LiveSelectionLabel => _studio.Selection?.Label ?? "";
    /// <summary>The live view's structure buttons: an element with an exact source, while no AI run edits the project.</summary>
    public bool CanEditLive => IsLive && _ui.CanEditSource && !_ui.IsLockedByAi;

    private string KindText(GraphEdgeKind k) => T("graph.edge." + k.ToString().ToLowerInvariant());
    private string LinkKind(GraphEdge e) => KindText(e.Kind) + (e.Inferred ? " · " + T("graph.inferredShort") : "");

    // ───────────────────────────── a selected wire ─────────────────────────────

    private GraphEdge? _selectedEdge;
    /// <summary>The wire clicked on the canvas: what it is, whether the code proves it or a name matched, and the
    /// lines it stands on (each opens in the editor).</summary>
    public GraphEdge? SelectedEdge
    {
        get => _selectedEdge;
        set
        {
            if (ReferenceEquals(_selectedEdge, value)) return;
            _selectedEdge = value;
            if (value != null && _selected != null)
            {
                _selected = null;
                Outgoing.Clear();
                Incoming.Clear();
                OnPropertyChanged(nameof(SelectedNode));
                OnPropertyChanged(nameof(HasSelection));
            }
            EdgeEvidence.Clear();
            if (value != null)
                foreach (var ev in value.Evidence)
                    EdgeEvidence.Add(new GraphEvidenceItem($"{ev.File}:{ev.Line}", ev.Text, ev.File, ev.Line));
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasEdgeSelection));
            OnPropertyChanged(nameof(HasAnySelection));
            OnPropertyChanged(nameof(EdgeTitle));
            OnPropertyChanged(nameof(EdgeKindText));
            OnPropertyChanged(nameof(EdgeStatus));
            OnPropertyChanged(nameof(EdgeWhy));
            OnPropertyChanged(nameof(EdgeColor));
            GraphChanged?.Invoke();
        }
    }
    public bool HasEdgeSelection => SelectedEdge != null;
    public bool HasAnySelection => SelectedEdge != null || SelectedNode != null;
    public string EdgeTitle => SelectedEdge is { } e && Graph != null
        ? (Graph.ById.TryGetValue(e.From, out var a) ? a.Label : e.From) + "  →  " + (Graph.ById.TryGetValue(e.To, out var b) ? b.Label : e.To) : "";
    public string EdgeKindText => SelectedEdge is { } e ? KindText(e.Kind) + (e.Label is { Length: > 0 } l ? "  ·  " + l : "") : "";
    public string EdgeStatus => SelectedEdge is { } e ? T(e.Inferred ? "graph.inferred" : "graph.confirmed") + (e.Count > 1 ? " · " + T("graph.places", e.Count) : "") : "";
    public string EdgeWhy => SelectedEdge?.Why ?? "";
    public System.Windows.Media.Brush? EdgeColor => SelectedEdge is { } e ? EdgeBrush(e.Kind) : null;
    public ObservableCollection<GraphEvidenceItem> EdgeEvidence { get; } = new();

    /// <summary>A wire was double-clicked: open its first line.</summary>
    public void EdgeActivated(GraphEdge e)
    {
        SelectedEdge = e;
        if (e.First is { } ev) OpenEvidence(ev.File, ev.Line);
    }

    private void OpenEvidence(string fileId, int line)
    {
        if (Graph?.ById.TryGetValue(fileId, out var n) != true || n!.FullPath == null) return;
        _ = _editor.OpenAtLineAsync(n.FullPath, line);
        OpenPageRequested?.Invoke("Code");
    }

    // ───────────────────────────── filters ─────────────────────────────

    /// <summary>One chip per kind of connection the graph has (with its count); a kind switched off stays off across
    /// rebuilds and projects.</summary>
    public ObservableCollection<EdgeKindFilter> EdgeFilters { get; } = new();
    public HashSet<GraphEdgeKind> HiddenKinds { get; } = new();
    private bool _bulk;

    private void RebuildFilters(ProjectGraph g)
    {
        EdgeFilters.Clear();
        foreach (var k in g.Edges.GroupBy(e => e.Kind).OrderBy(x => (int)x.Key))
            EdgeFilters.Add(new EdgeKindFilter(k.Key, KindText(k.Key), EdgeBrush(k.Key), k.Count(), !HiddenKinds.Contains(k.Key), OnFilter));
        _inferredCount = g.Edges.Count(e => e.Inferred);
        OnPropertyChanged(nameof(InferredCount));
        OnPropertyChanged(nameof(HasInferred));
    }

    private void OnFilter(EdgeKindFilter f)
    {
        if (f.IsOn) HiddenKinds.Remove(f.Kind); else HiddenKinds.Add(f.Kind);
        if (!_bulk) FiltersChanged();
    }

    private void FiltersChanged()
    {
        if (SelectedEdge != null && (HiddenKinds.Contains(SelectedEdge.Kind) || SelectedEdge.Inferred && !ShowInferred)) SelectedEdge = null;
        GraphChanged?.Invoke();
    }

    private bool _showInferred = true;
    /// <summary>Show the inferred (dashed) wires — off leaves only what the code proves.</summary>
    public bool ShowInferred { get => _showInferred; set { if (SetProperty(ref _showInferred, value)) FiltersChanged(); } }
    private int _inferredCount;
    public int InferredCount => _inferredCount;
    public bool HasInferred => _inferredCount > 0;

    private bool _groupByFolder;
    /// <summary>Lay the files out in one block per folder, a frame around each. Positions dragged in this view are
    /// kept apart from the ungrouped ones.</summary>
    public bool GroupByFolder
    {
        get => _groupByFolder;
        set
        {
            if (!SetProperty(ref _groupByFolder, value) || Graph == null || IsLive) return;
            Layout(Graph, useSaved: true);
            GraphChanged?.Invoke();
            FitRequested?.Invoke();
        }
    }

    private static readonly Dictionary<GraphEdgeKind, System.Windows.Media.SolidColorBrush> EdgeBrushes = new();
    private static System.Windows.Media.Brush EdgeBrush(GraphEdgeKind k)
    {
        if (!EdgeBrushes.TryGetValue(k, out var b))
        {
            b = new System.Windows.Media.SolidColorBrush(Views.UiStudio.NodeGraphCanvas.EdgeColor(k));
            b.Freeze();
            EdgeBrushes[k] = b;
        }
        return b;
    }

    private void OpenSelected()
    {
        var n = SelectedNode;
        if (n == null) return;
        if (n.FullPath != null) { _ = _editor.OpenAtLineAsync(n.FullPath, 1); OpenPageRequested?.Invoke("Code"); return; }
        if (n.Src != null && _studio.ResolveSource(n.Src) is { } where)
        {
            _ = _editor.OpenAtLineAsync(where.FullPath, where.Line);
            OpenPageRequested?.Invoke("Code");
        }
    }

    public ICommand RefreshCommand { get; }
    public ICommand AutoLayoutCommand { get; }
    public ICommand FitCommand { get; }
    public ICommand SetModeCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand GoToLinkCommand { get; }
    public ICommand ShowInStudioCommand { get; }
    public ICommand OpenStudioCommand { get; }
    public ICommand StructureCommand { get; }
    public ICommand ToggleCollapseCommand { get; }
    public ICommand OpenEvidenceCommand { get; }
    public ICommand OpenLinkEvidenceCommand { get; }
    public ICommand AllKindsCommand { get; }
}
