using System.Windows;
using System.Windows.Controls;
using CluadeX.Services.UiStudio;
using CluadeX.ViewModels;

namespace CluadeX.Views;

/// <summary>The Node Graph page: binds the drawing canvas to <see cref="NodeGraphViewModel"/>.</summary>
public partial class NodeGraphView : UserControl
{
    private NodeGraphViewModel? _vm;

    public NodeGraphView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Canvas.NodeSelected += n => { if (_vm != null) _vm.SelectedNode = n; };
        Canvas.NodeActivated += n => _vm?.NodeActivated(n);
        Canvas.NodeMoved += n => _vm?.NodeMoved(n);
        Canvas.NodeContextMenu += (n, p) => ShowMenu(n);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as NodeGraphViewModel;
        if (_vm == null) return;
        _vm.GraphChanged += Redraw;
        _vm.FitRequested += Fit;
        _vm.FocusRequested += Focus;
        _vm.IsVisible = true;
        Redraw();
        if (_vm.Graph == null) _ = _vm.RefreshAsync();
        else Fit();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.GraphChanged -= Redraw;
        _vm.FitRequested -= Fit;
        _vm.FocusRequested -= Focus;
        _vm.IsVisible = false;
    }

    private void Redraw()
    {
        if (_vm == null) return;
        Canvas.Highlights = _vm.Matches;
        Canvas.Changed = _vm.RecentlyChanged;
        Canvas.ChangeGlow = NodeGraphViewModel.ChangeGlow;
        Canvas.SetGraph(_vm.Graph, _vm.SelectedNode);
    }

    // Layout first (the canvas may not have its size yet on the first frame).
    private void Fit() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => Canvas.FitToView());

    private void Focus(GraphNode n) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => Canvas.CenterOn(n));

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
        menu.PlacementTarget = Canvas;
        menu.IsOpen = true;
    }
}
