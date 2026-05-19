using System.Windows;
using System.Windows.Controls;
using CluadeX.Models;
using CluadeX.ViewModels;

namespace CluadeX.Views;

public partial class HookLibraryView : UserControl
{
    public HookLibraryView()
    {
        InitializeComponent();
    }

    private void OnHookClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HookBundle bundle
            && DataContext is HookLibraryViewModel vm)
        {
            vm.Selected = bundle;
        }
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        // CheckBox click swaps the bound IsEnabled, but we want the
        // VM to own the projection-to-config step. Route through the
        // VM command so HookBundleService.SetEnabled fires.
        if (sender is CheckBox cb && cb.Tag is HookBundle bundle
            && DataContext is HookLibraryViewModel vm)
        {
            // Bail the WPF default toggle so we don't double-flip the bound value.
            // We invert the *current* state via SetEnabled.
            cb.IsChecked = bundle.IsEnabled;
            vm.ToggleCommand.Execute(bundle);
            e.Handled = true;
        }
    }
}
