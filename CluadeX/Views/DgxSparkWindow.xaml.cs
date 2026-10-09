using System.Windows;
using System.Windows.Input;
using CluadeX.ViewModels;

namespace CluadeX.Views;

/// <summary>The NVIDIA DGX Spark window — see <see cref="DgxSparkViewModel"/>.</summary>
public partial class DgxSparkWindow : Window
{
    public DgxSparkWindow(DgxSparkViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => { if (string.IsNullOrWhiteSpace(vm.HostInput)) HostBox.Focus(); };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as DgxSparkViewModel)?.Detach();
        base.OnClosed(e);
    }
}
