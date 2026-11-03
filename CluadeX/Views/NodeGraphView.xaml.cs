using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CluadeX.Services.Browser;
using CluadeX.Services.UiStudio;
using CluadeX.ViewModels;
using CluadeX.Views.UiStudio;

namespace CluadeX.Views;

/// <summary>The Node Graph page: binds the drawing surface to <see cref="NodeGraphViewModel"/>. The surface is the GPU
/// view (<see cref="NodeGraphGpu"/>) when the machine has WebView2 and WebGL2, else the WPF canvas.</summary>
public partial class NodeGraphView : UserControl
{
    private NodeGraphViewModel? _vm;
    private INodeGraphSurface _surface;
    private NodeGraphGpu? _gpu;
    private bool _hooked;

    public NodeGraphView()
    {
        InitializeComponent();
        _surface = Canvas;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hooked) return;   // Loaded again without an Unloaded (a re-parent): already wired
        _vm = DataContext as NodeGraphViewModel;
        if (_vm == null) return;
        _hooked = true;
        if (!NodeGraphGpu.Disabled && BrowserEngineProbe.Probe().HasWebView2) UseGpu();
        Hook(_surface);
        _vm.GraphChanged += Redraw;
        _vm.FitRequested += Fit;
        _vm.FocusRequested += Focus;
        _vm.ActivityChanged += OnActivity;
        _vm.PropertyChanged += OnVmChanged;
        _vm.IsVisible = true;
        Redraw();
        if (_vm.Graph == null) _ = _vm.RefreshAsync();
        else Fit();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked) return;
        _hooked = false;
        Unhook(_surface);
        if (_gpu != null)
        {
            _gpu.Unavailable -= OnGpuUnavailable;
            _gpu.Release(GpuHost);
            _gpu = null;
            _surface = Canvas;
        }
        if (_vm == null) return;
        _vm.GraphChanged -= Redraw;
        _vm.FitRequested -= Fit;
        _vm.FocusRequested -= Focus;
        _vm.ActivityChanged -= OnActivity;
        _vm.PropertyChanged -= OnVmChanged;
        _vm.IsVisible = false;
    }

    private void UseGpu()
    {
        var vm = _vm!;
        _gpu = NodeGraphGpu.Acquire();
        _gpu.Loc = vm.Tr;
        _gpu.Log = vm.Log;
        _gpu.DataFolder = vm.WebDataFolder;
        _gpu.Unavailable += OnGpuUnavailable;
        GpuHost.Child = _gpu;
        _surface = _gpu;
        Canvas.Visibility = Visibility.Collapsed;
        Legend.Visibility = Visibility.Collapsed;   // the page draws its own (WPF cannot draw over a WebView2)
    }

    /// <summary>No WebGL2 or no WebView2 after all: the WPF canvas takes over, with what the GPU view had.</summary>
    private void OnGpuUnavailable(string why)
    {
        if (_gpu == null) return;
        Unhook(_gpu);
        _gpu.Unavailable -= OnGpuUnavailable;
        _gpu.Release(GpuHost);
        _gpu = null;
        _surface = Canvas;
        Hook(Canvas);
        Canvas.Visibility = Visibility.Visible;
        GpuHost.Visibility = Visibility.Collapsed;
        Legend.Visibility = Visibility.Visible;
        Redraw();
        Fit();
    }

    private void Hook(INodeGraphSurface s)
    {
        s.NodeSelected += OnNodeSelected;
        s.NodeActivated += OnNodeActivated;
        s.NodeMoved += OnNodeMoved;
        s.NodeContextMenu += OnNodeContextMenu;
        s.EdgeSelected += OnEdgeSelected;
        s.EdgeActivated += OnEdgeActivated;
    }

    private void Unhook(INodeGraphSurface s)
    {
        s.NodeSelected -= OnNodeSelected;
        s.NodeActivated -= OnNodeActivated;
        s.NodeMoved -= OnNodeMoved;
        s.NodeContextMenu -= OnNodeContextMenu;
        s.EdgeSelected -= OnEdgeSelected;
        s.EdgeActivated -= OnEdgeActivated;
    }

    private void OnNodeSelected(GraphNode? n) { if (_vm != null) _vm.SelectedNode = n; }
    private void OnNodeActivated(GraphNode n) => _vm?.NodeActivated(n);
    private void OnNodeMoved(GraphNode n) => _vm?.NodeMoved(n);
    private void OnNodeContextMenu(GraphNode n, Point p) => ShowMenu(n);
    private void OnEdgeSelected(GraphEdge? e) { if (_vm != null) _vm.SelectedEdge = e; }
    private void OnEdgeActivated(GraphEdge e) => _vm?.EdgeActivated(e);

    private void OnActivity()
    {
        if (_vm == null) return;
        _surface.Active = _vm.RecentlyActive;
        _surface.SetActivity();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NodeGraphViewModel.IsBusy) && _gpu != null) Redraw();
    }

    private void Redraw()
    {
        if (_vm == null) return;
        _surface.Highlights = _vm.Matches;
        _surface.Changed = _vm.RecentlyChanged;
        _surface.ChangeGlow = NodeGraphViewModel.ChangeGlow;
        _surface.Active = _vm.RecentlyActive;
        // the filters and the grouping apply to the project graph only; the live tree shows everything
        _surface.HiddenKinds = _vm.IsProject ? _vm.HiddenKinds : new HashSet<GraphEdgeKind>();
        _surface.ShowInferred = !_vm.IsProject || _vm.ShowInferred;
        _surface.GroupByFolder = _vm.IsProject && _vm.GroupByFolder;
        _surface.SelectedEdge = _vm.SelectedEdge;
        if (_gpu != null)
        {
            _gpu.IsLiveTree = _vm.IsLive;
            // the page covers the WPF text under it, so "reading…" over a graph goes in the page; with no graph the
            // page steps aside and the WPF empty / busy text shows
            _gpu.BusyText = _vm.IsBusy && _vm.Graph != null ? _vm.Tr("graph.busy") : null;
            GpuHost.Visibility = _vm.Graph != null ? Visibility.Visible : Visibility.Collapsed;
        }
        _surface.SetGraph(_vm.Graph, _vm.SelectedNode);
    }

    // Layout first (the canvas may not have its size yet on the first frame).
    private void Fit() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => _surface.FitToView());

    private void Focus(GraphNode n) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => _surface.CenterOn(n));

    private void ShowMenu(GraphNode node)
    {
        if (_vm == null) return;
        var menu = new ContextMenu();
        void Add(string key, System.Windows.Input.ICommand cmd, object? param = null)
        {
            var item = new MenuItem { Header = _vm.Tr(key), Command = cmd, CommandParameter = param };
            menu.Items.Add(item);
        }
        Add("graph.openCode", _vm.OpenFileCommand);
        if (_vm.IsLive)
        {
            Add("graph.showInStudio", _vm.ShowInStudioCommand);
            Add("graph.collapse", _vm.ToggleCollapseCommand);
            menu.Items.Add(new Separator());
            Add("studio.moveUpTip", _vm.StructureCommand, "moveUp");
            Add("studio.moveDownTip", _vm.StructureCommand, "moveDown");
            Add("studio.duplicateTip", _vm.StructureCommand, "duplicate");
            Add("studio.deleteTip", _vm.StructureCommand, "delete");
        }
        menu.PlacementTarget = _gpu != null ? GpuHost : Canvas;
        menu.IsOpen = true;
    }
}
