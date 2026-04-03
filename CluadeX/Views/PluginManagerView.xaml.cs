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

    private void CatalogCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is CatalogPlugin cp
            && DataContext is PluginManagerViewModel vm)
        {
            vm.SelectedCatalogPlugin = cp;
        }
    }
}
