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
        // The page's orbs, halo and HEAD pulse run forever, and the rewind overlay's rings start on Loaded while the
        // overlay is still collapsed: idle on this page that was eight 60 fps clocks, four under a blur. Run each
        // only while it can be seen.
        IsVisibleChanged += (_, _) => GateAnimations();
        RewindOverlay.IsVisibleChanged += (_, _) => GateAnimations();
    }

    private void GateAnimations()
    {
        AmbientAnimation.SetPaused(this, !IsVisible, skip: RewindOverlay);
        AmbientAnimation.SetPaused(RewindOverlay, !(IsVisible && RewindOverlay.IsVisible));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // After every child's Loaded trigger has begun its storyboard.
        _ = Dispatcher.InvokeAsync(() => GateAnimations(), System.Windows.Threading.DispatcherPriority.Background);
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
