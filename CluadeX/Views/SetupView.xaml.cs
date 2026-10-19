using System.Windows;
using System.Windows.Controls;

namespace CluadeX.Views;

public partial class SetupView : UserControl
{
    public SetupView() => InitializeComponent();

    /// <summary>The first visit checks the PC; later visits keep the last result (Re-check looks again).</summary>
    private void OnLoaded(object sender, RoutedEventArgs e) => (DataContext as ViewModels.SetupViewModel)?.OnShown();
}
