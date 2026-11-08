using System.Windows;
using System.Windows.Controls;
using CluadeX.ViewModels;

namespace CluadeX.Views.Plugins;

public partial class ColibriCard : UserControl
{
    public ColibriCard() => InitializeComponent();

    /// <summary>The page opened: the first time, read colibri's status (it runs Python — not at app start).</summary>
    private void OnLoaded(object sender, RoutedEventArgs e) => (DataContext as ColibriPluginViewModel)?.EnsureLoaded();
}
