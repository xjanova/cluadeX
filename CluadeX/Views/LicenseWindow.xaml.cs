using System.Windows;
using System.Windows.Input;
using CluadeX.ViewModels;

namespace CluadeX.Views;

/// <summary>The license dialog — see <see cref="LicenseViewModel"/>.</summary>
public partial class LicenseWindow : Window
{
    public LicenseWindow(LicenseViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => { if (!vm.IsActive) KeyBox.Focus(); };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as LicenseViewModel)?.Detach();
        base.OnClosed(e);
    }
}
