using System.Windows;
using System.Windows.Controls;
using CluadeX.Models;
using CluadeX.ViewModels;

namespace CluadeX.Views;

public partial class SubAgentsView : UserControl
{
    public SubAgentsView()
    {
        InitializeComponent();
    }

    private void OnAgentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SubAgentDefinition agent
            && DataContext is SubAgentsViewModel vm)
        {
            vm.Selected = agent;
        }
    }
}
