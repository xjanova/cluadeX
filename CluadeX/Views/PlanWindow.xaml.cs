using System.Windows;
using System.Windows.Input;
using CluadeX.ViewModels;

namespace CluadeX.Views;

/// <summary>One chat's plan window — see <see cref="PlanWindowViewModel"/>. Opens beside the main window.</summary>
public partial class PlanWindow : Window
{
    public PlanWindow(PlanWindowViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) =>
        {
            // Right edge of the owner (or the screen), so the chat stays visible beside it.
            var area = SystemParameters.WorkArea;
            if (Owner is { } o && o.WindowState != WindowState.Maximized && o.Left + o.ActualWidth + Width <= area.Right)
            { Left = o.Left + o.ActualWidth - 10; Top = o.Top; }
            else { Left = area.Right - Width - 8; Top = area.Top + 60; }
            NewItemBox.Focus();
        };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Pin_Changed(object sender, RoutedEventArgs e) => Topmost = PinButton.IsChecked == true;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as PlanWindowViewModel)?.Detach();
        base.OnClosed(e);
    }
}
