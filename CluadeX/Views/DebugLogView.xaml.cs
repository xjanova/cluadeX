using System.Collections.Specialized;
using System.Windows.Controls;
using CluadeX.ViewModels;

namespace CluadeX.Views;

public partial class DebugLogView : UserControl
{
    public DebugLogView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is DebugLogViewModel vm)
        {
            // Auto-scroll to bottom on new entries when toggle is enabled.
            // Hooked here (not in VM) because ListBox.ScrollIntoView is view-layer.
            vm.Entries.CollectionChanged += (_, args) =>
            {
                if (!vm.AutoScroll) return;
                if (args.Action != NotifyCollectionChangedAction.Add) return;
                if (EntriesList.Items.Count == 0) return;
                var last = EntriesList.Items[^1];
                EntriesList.ScrollIntoView(last);
            };
        }
    }
}
