using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.ViewModels;

namespace CluadeX.Views;

public partial class PluginManagerView : UserControl
{
    public PluginManagerView()
    {
        InitializeComponent();
    }

    // Card click handlers for the neon list ─────────────────────────────
    private void OnInstalledClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is PluginInfo p
            && DataContext is PluginManagerViewModel vm)
        {
            vm.SelectedPlugin = p;
        }
    }

    private void OnCatalogClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CatalogPlugin cp
            && DataContext is PluginManagerViewModel vm)
        {
            vm.SelectedCatalogPlugin = cp;
        }
    }

    // Sync the VM's SelectedTab when the user flips the chip — keeps the
    // existing localised labels happy and feeds the filter visibility.
    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && DataContext is PluginManagerViewModel vm)
        {
            vm.SelectedTab = rb.Name == "TabCatalog" ? 1 : 0;
        }
    }
}
