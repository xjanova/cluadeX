using System.Windows;
using System.Windows.Controls;
using CluadeX.Models;
using CluadeX.ViewModels;

namespace CluadeX.Views;

public partial class SkillsView : UserControl
{
    public SkillsView()
    {
        InitializeComponent();
    }

    private void OnSkillClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SkillDefinition skill
            && DataContext is SkillsViewModel vm)
        {
            vm.Selected = skill;
        }
    }
}
