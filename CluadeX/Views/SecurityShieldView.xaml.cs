using System.Windows;
using System.Windows.Controls;
using CluadeX.Models;
using CluadeX.ViewModels;

namespace CluadeX.Views;

public partial class SecurityShieldView : UserControl
{
    public SecurityShieldView()
    {
        InitializeComponent();
    }

    private void OnFindingClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SecurityFinding f
            && DataContext is SecurityShieldViewModel vm)
        {
            vm.Selected = f;
        }
    }
}
