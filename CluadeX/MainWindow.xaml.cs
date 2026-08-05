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
        // Activity bar uses RadioButtons bound via SelectedNavItem converter —
        // initial selection comes from MainViewModel.SelectedNavItem ("Chat" default).
        StateChanged += OnStateChanged;
        Closing += OnClosing;

        // Focus the palette box as soon as it opens — a palette you have to click into first
        // is not a keyboard palette.
        viewModel.Palette.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen)) OnPaletteOpenChanged();
        };

        // Version + commit + build time — visible in the custom title bar (VersionChip) AND the
        // taskbar Title, so "which build am I actually running?" is answerable at a glance.
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        string version = asm.GetName().Version?.ToString(3) ?? "?";

        // InformationalVersion is "<Version>+<git short hash>" (embedded by the EmbedGitCommitHash
        // msbuild target); strip anything after a second '+' just in case the SDK appends its own.
        string info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "";
        string commit = "";
        int plus = info.IndexOf('+');
        if (plus >= 0 && plus < info.Length - 1)
        {
            // Defensive: some SDK paths join extra revision metadata with '.' or '+' — keep only
            // our short hash segment, capped to 9 chars.
            commit = info[(plus + 1)..].Split('+', '.', ';')[0].Trim();
            if (commit.Length > 9) commit = commit[..9];
        }

        string buildTime = "";
        try
        {
            string exe = Environment.ProcessPath ?? asm.Location;
            if (!string.IsNullOrEmpty(exe))
                buildTime = System.IO.File.GetLastWriteTime(exe).ToString("d MMM HH:mm");
        }
        catch { /* cosmetic */ }

        string chip = $"v{version}";
        if (commit.Length > 0 && commit != "local") chip += $" · {commit}";
        if (buildTime.Length > 0) chip += $" · {buildTime}";
        VersionChip.Text = chip;
        Title = $"CluadeX {chip} — AI Coding Assistant";

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

    // ── Command palette (Ctrl+K) ──

    /// <summary>Arrow keys move the highlight, Enter runs it, Esc closes.</summary>
    private void PaletteBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var palette = _viewModel.Palette;
        switch (e.Key)
        {
            case Key.Down:
                palette.MoveSelection(1);
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                palette.MoveSelection(-1);
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                palette.RunSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                palette.Close();
                e.Handled = true;
                break;
        }
    }

    private void PaletteList_Click(object sender, MouseButtonEventArgs e)
        => _viewModel.Palette.RunSelected();

    /// <summary>Put the caret in the palette box the moment it opens.</summary>
    private void OnPaletteOpenChanged()
    {
        if (!_viewModel.Palette.IsOpen) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PaletteBox.Focus();
            Keyboard.Focus(PaletteBox);
        }), System.Windows.Threading.DispatcherPriority.Input);
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
