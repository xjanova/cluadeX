using System.Windows;
using System.Windows.Controls;
using CluadeX.Models;
using CluadeX.ViewModels;

namespace CluadeX.Views;

public partial class TimeMachineView : UserControl
{
    public TimeMachineView()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is TimeMachineViewModel vm)
        {
            // Auto-load commits each time the view is shown — keeps the
            // timeline fresh if commits happened in another tab/CLI.
            await vm.LoadAsync();
        }
    }

    private void OnCommitClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is CommitInfo c
            && DataContext is TimeMachineViewModel vm)
        {
            vm.Selected = c;
        }
    }

    private void OnFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is CommitFileChange f
            && DataContext is TimeMachineViewModel vm)
        {
            vm.SelectedFile = f;
        }
    }
}
