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

/// <summary>A node listed in the details panel (imports / imported by / children), with its wire's colour.</summary>
public sealed record GraphLink(string Id, string Label, string Kind, System.Windows.Media.Brush? Color = null);

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
    private readonly FileSystemService _fs;
    private readonly SettingsService _settings;
    private readonly DebugLogService? _log;
    private CancellationTokenSource? _buildCts;
    private readonly DispatcherTimer _saveTimer;
    private Dictionary<string, double[]> _saved = new();
    private string? _savedFor;

    public NodeGraphViewModel(StudioService studio, UiStudioViewModel ui, CodeEditorViewModel editor, LocalizationService loc,
        FileSystemService fs, SettingsService settings, DebugLogService? log = null)
    {
        _studio = studio;
        _ui = ui;
        _editor = editor;
        _loc = loc;
        _fs = fs;
        _settings = settings;
        _log = log;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveLayout(); };

        studio.TreeChanged += () => { if (IsLive) BuildLive(); };
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
            Graph = null;
            SelectedNode = null;
            _builtFor = null;
            if (IsVisible) _ = RefreshAsync();
            else QueuePrebuild();
        };
        studio.FilesChanged += OnFilesChanged;
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _liveTimer.Tick += (_, _) => { _liveTimer.Stop(); _ = RefreshAsync(keepView: true); };
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
            if (value && _dirty && IsProject && Graph != null) { _liveTimer.Stop(); _ = RefreshAsync(keepView: true); }
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
        _liveTimer.Stop();
        _liveTimer.Start();
    }

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
            return Graph.Truncated ? s + " · " + T("graph.truncated") : s;
        }
    }

    public string RootName => _studio.HasProject ? Path.GetFileName(_studio.Root.TrimEnd(Path.DirectorySeparatorChar))
        : _fs.AppWorkingDirectory is { Length: > 0 } p ? Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar)) : "";

    private string? RootPath => _studio.HasProject ? _studio.Root : _fs.AppWorkingDirectory is { Length: > 0 } p && Directory.Exists(p) ? p : null;

    public Task RefreshAsync() => RefreshAsync(keepView: false);

    /// <summary>Read the project again. <paramref name="keepView"/>: a live update (files changed) — nodes stay where
    /// they are on screen, the selection stays, the view is not refitted and no busy text covers the graph.</summary>
    private async Task RefreshAsync(bool keepView)
    {
        OnPropertyChanged(nameof(RootName));
        if (IsLive) { BuildLive(); return; }
        string? root = RootPath;
        if (root == null) { Graph = null; EmptyText = T("graph.noProject"); return; }

        _buildCts?.Cancel();
        var cts = _buildCts = new CancellationTokenSource();
        var old = Graph;
        keepView &= old != null && string.Equals(_builtFor, root, StringComparison.OrdinalIgnoreCase);
        _dirty = false;
        if (!keepView) IsBusy = true;
        try
        {
            bool packages = ShowPackages;
            var graph = await Task.Run(() => ProjectGraphBuilder.Build(root, packages, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || IsLive || !string.Equals(root, RootPath, StringComparison.OrdinalIgnoreCase)) return;
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
            _builtFor = root;
            Graph = graph;
            EmptyText = graph.Nodes.Count == 0 ? T("graph.empty") : "";
            if (keep != null) SelectedNode = graph.ById.TryGetValue(keep, out var again) ? again : null;
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
        if (tree == null) { Graph = null; EmptyText = T("graph.liveEmpty"); return; }
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
        string? keep = SelectedNode?.Id;
        Graph = g;
        EmptyText = "";
        SelectedNode = keep != null && g.ById.TryGetValue(keep, out var again) ? again : null;
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

    /// <summary>Layered layout (left → right along imports): a file sits one column right of whatever imports it;
    /// within a column, nodes follow the order of their parents to keep wires short. Files with no wires go to a
    /// grid underneath. Positions the user dragged before are kept.</summary>
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
        // order: first column by kind/name, later columns by the mean row of their parents (barycenter)
        var row = new Dictionary<string, double>();
        for (int c = 0; c < columns.Count; c++)
        {
            var col = c == 0
                ? columns[c].OrderBy(n => Rank(n.Id)).ToList()
                : columns[c].OrderBy(n => ins.TryGetValue(n.Id, out var p) && p.Any(row.ContainsKey) ? p.Where(row.ContainsKey).Average(x => row[x]) : double.MaxValue)
                            .ThenBy(n => n.Label).ToList();
            columns[c] = col;
            for (int i = 0; i < col.Count; i++) row[col[i].Id] = i;
        }

        double maxHeight = columns.Count == 0 ? 0 : columns.Max(c => c.Count * (c[0].Height + RowGap));
        for (int c = 0; c < columns.Count; c++)
        {
            var col = columns[c];
            double h = col.Count * (col[0].Height + RowGap);
            double y0 = (maxHeight - h) / 2;
            for (int i = 0; i < col.Count; i++)
            {
                col[i].Layer = c;
                col[i].X = c * (col[i].Width + ColGap);
                col[i].Y = y0 + i * (col[i].Height + RowGap);
            }
        }

        // unconnected files: a grid under the wired part
        var loose = nodes.Where(n => !connected.Contains(n.Id)).OrderBy(n => n.Kind).ThenBy(n => n.Id).ToList();
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

        if (useSaved)
            foreach (var n in g.Nodes)
                if (_saved.TryGetValue(n.Id, out var xy) && xy.Length == 2) { n.X = xy[0]; n.Y = xy[1]; }

        int Rank(string id)
        {
            var n = g.ById[id];
            return n.Kind switch { GraphNodeKind.Page => 0, GraphNodeKind.Script => 1, GraphNodeKind.Component => 2, GraphNodeKind.Style => 3, _ => 4 };
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

    private void LoadLayout(string root)
    {
        if (string.Equals(_savedFor, root, StringComparison.OrdinalIgnoreCase)) return;
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

    private void ForgetLayout()
    {
        _saved.Clear();
        _saveTimer.Stop();
        SaveLayout();
    }

    /// <summary>The canvas moved a node (drag ended).</summary>
    public void NodeMoved(GraphNode node)
    {
        if (IsLive) return;   // the live tree is re-laid out from the page each time
        _saved[node.Id] = new[] { Math.Round(node.X, 1), Math.Round(node.Y, 1) };
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
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedTitle));
            OnPropertyChanged(nameof(SelectedPath));
            OnPropertyChanged(nameof(SelectedKind));
            Outgoing.Clear();
            Incoming.Clear();
            if (value != null && Graph != null)
            {
                foreach (var e in Graph.Edges.Where(e => e.From == value.Id))
                    if (Graph.ById.TryGetValue(e.To, out var to)) Outgoing.Add(new GraphLink(to.Id, to.Label, KindText(e.Kind), EdgeBrush(e.Kind)));
                foreach (var e in Graph.Edges.Where(e => e.To == value.Id))
                    if (Graph.ById.TryGetValue(e.From, out var from)) Incoming.Add(new GraphLink(from.Id, from.Label, KindText(e.Kind), EdgeBrush(e.Kind)));
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
    public bool CanEditLive => IsLive && _ui.CanEditSource;

    private string KindText(GraphEdgeKind k) => T("graph.edge." + k.ToString().ToLowerInvariant());

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
}
