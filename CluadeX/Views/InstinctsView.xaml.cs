using System.Windows;
using System.Windows.Controls;
using CluadeX.Models;
using CluadeX.ViewModels;

namespace CluadeX.Views;

public partial class InstinctsView : UserControl
{
    public InstinctsView()
    {
        InitializeComponent();
    }

    private void OnInstinctClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Instinct i
            && DataContext is InstinctsViewModel vm)
        {
            vm.Selected = i;
        }
    }
}
