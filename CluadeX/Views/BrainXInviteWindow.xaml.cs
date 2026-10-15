using System.Windows;
using System.Windows.Input;
using CluadeX.ViewModels;

namespace CluadeX.Views;

/// <summary>The BrainX invitation — see <see cref="BrainXInviteViewModel"/>.</summary>
public partial class BrainXInviteWindow : Window
{
    public BrainXInviteWindow(BrainXInviteViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += Close;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is BrainXInviteViewModel vm)
        {
            vm.RequestClose -= Close;
            vm.Detach();
        }
        base.OnClosed(e);
    }
}
