using System.Windows;
using System.Windows.Controls;

namespace CluadeX.Views;

/// <summary>The Routines page — see <see cref="ViewModels.RoutinesViewModel"/>.</summary>
public partial class RoutinesView : UserControl
{
    public RoutinesView() => InitializeComponent();

    /// <summary>Telegram's Connect: the token goes from the PasswordBox straight to the bridge (never bound, never kept
    /// in the view model), and the box is cleared.</summary>
    private async void TgConnect_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.RoutinesViewModel vm) return;
        string token = TgToken.Password;
        TgToken.Clear();
        await vm.ConnectTelegramAsync(token);
    }
}
