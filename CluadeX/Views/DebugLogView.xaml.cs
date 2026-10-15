using System.Collections.Specialized;
using System.Windows.Controls;
using CluadeX.ViewModels;

namespace CluadeX.Views;

public partial class DebugLogView : UserControl
{
    /// <summary>The VM we are attached to. The VM is a singleton that outlives this view; a fresh
    /// view is built on every page visit, and each one used to add another CollectionChanged handler
    /// that was never removed — every old, invisible view kept scrolling its own list on every new
    /// log line, forever.</summary>
    private DebugLogViewModel? _vm;

    public DebugLogView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not DebugLogViewModel vm || ReferenceEquals(vm, _vm)) return;
        Detach();
        _vm = vm;
        // Auto-scroll to bottom on new entries when toggle is enabled.
        // Hooked here (not in VM) because ListBox.ScrollIntoView is view-layer.
        vm.Entries.CollectionChanged += OnEntriesChanged;
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (_vm != null) _vm.Entries.CollectionChanged -= OnEntriesChanged;
        _vm = null;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_vm == null || !_vm.AutoScroll) return;
        if (args.Action != NotifyCollectionChangedAction.Add) return;
        if (EntriesList.Items.Count == 0) return;
        var last = EntriesList.Items[^1];
        EntriesList.ScrollIntoView(last);
    }
}
