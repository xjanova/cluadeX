using System.Windows;
using System.Windows.Input;
using CluadeX.ViewModels;

namespace CluadeX;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        NavChat.IsChecked = true;
        StateChanged += OnStateChanged;
        Closing += OnClosing;

        // Keyboard shortcut: Ctrl+N = New Chat
        InputBindings.Add(new KeyBinding(viewModel.ChatVM.NewSessionCommand, Key.N, ModifierKeys.Control));
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Force-save current session before app exits
        try { _viewModel.ChatVM.SaveNow(); }
        catch { /* don't prevent closing */ }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void BtnClose_Click(object sender, RoutedEventArgs e)
        => Close();

    private void Buddy_Pet(object sender, MouseButtonEventArgs e)
    {
        _viewModel.BuddyService.Pet();
        e.Handled = true;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            RootBorder.Padding = new Thickness(7);
        else
            RootBorder.Padding = new Thickness(0);

        BtnMaximize.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }
}
