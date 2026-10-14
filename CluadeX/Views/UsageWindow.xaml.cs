using System.Windows;
using System.Windows.Input;
using CluadeX.ViewModels;

namespace CluadeX.Views;

/// <summary>The spend window — see <see cref="UsageViewModel"/>.</summary>
public partial class UsageWindow : Window
{
    public UsageWindow(UsageViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as UsageViewModel)?.Detach();
        base.OnClosed(e);
    }
}
