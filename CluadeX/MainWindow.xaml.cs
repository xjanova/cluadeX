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
        // The pet is its own top-level window (not owned, so it stays out when CluadeX is minimized): it must go
        // with the main window, or the app would keep running with only the pet on screen.
        Closed += (_, _) => _viewModel.Pet.Shutdown();

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

        // A dev build and the installed app look identical otherwise — and both can sit on the desktop.
        var kind = CluadeX.Services.AutoUpdateService.CurrentBuildKind;
        if (kind != CluadeX.Services.AutoUpdateService.BuildKind.Installed)
        {
            string tag = kind == CluadeX.Services.AutoUpdateService.BuildKind.Dev ? "DEV" : "PORTABLE";
            chip = $"{tag} · {chip}";
            // A dev build's chip is the theme's danger colour — a resource reference, so a theme switch recolours it.
            if (kind == CluadeX.Services.AutoUpdateService.BuildKind.Dev)
                VersionChip.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Status.Danger");
            VersionChip.FontWeight = FontWeights.Bold;
        }
        VersionChip.Text = chip;
        Title = kind == CluadeX.Services.AutoUpdateService.BuildKind.Installed
            ? $"CluadeX {chip} — AI Coding Assistant"
            : $"CluadeX {chip}";
        // Keyboard shortcuts (Ctrl+N = new chat and show it, …) live in MainWindow.xaml's InputBindings only — a
        // second binding added here took precedence and pointed Ctrl+N at a different command.
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // An update restart is closing the app (Application.Shutdown — Cancel would be ignored). Its questions were
        // answered before it started: nothing is asked again; the chat and any tab edited since are saved.
        if (_viewModel.IsRestartingForUpdate)
        {
            _viewModel.SaveForUpdateRestart();
            return;
        }

        // Unsaved editor tabs: Save all / Discard / Cancel. Cancel (or a failed save) keeps the app open.
        if (!_viewModel.CodeEditorVM.ConfirmCloseAll())
        {
            e.Cancel = true;
            return;
        }

        // A model still downloading onto the DGX Spark stops when its window closes with this one — ask first.
        // Asked after the editor (saving there is harmless if this answer keeps the app open).
        if (!_viewModel.ConfirmCloseSparkWindow())
        {
            e.Cancel = true;
            return;
        }

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
        _viewModel.Pet.Pat();
        e.Handled = true;
    }

    /// <summary>Right-click the chip: put the pet away, or call it back to the desktop.</summary>
    private void Buddy_Menu(object sender, MouseButtonEventArgs e)
    {
        var pet = _viewModel.Pet;
        bool th = pet.Thai;
        var menu = new System.Windows.Controls.ContextMenu();
        var item = new System.Windows.Controls.MenuItem
        {
            Header = pet.IsOut ? (th ? "เก็บเข้าบ้าน (ซ่อน)" : "Put away (hide)") : (th ? "ปล่อยออกมาเดินเล่น" : "Let it out on the desktop"),
        };
        item.Click += (_, _) => { if (pet.IsOut) pet.PutAway(); else pet.CallBack(); };
        menu.Items.Add(item);
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>The "today" chip: pick the daily budget (a paid run asks before going on past it), or open the billing
    /// page — the only place the account's remaining credit is shown.</summary>
    private void TodayChip_Click(object sender, RoutedEventArgs e)
    {
        var chat = _viewModel.ChatVM;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = Services.LocalizedResources.Instance["chat.daily.menu"], IsEnabled = false, FontWeight = FontWeights.SemiBold,
        });
        foreach (var choice in chat.DailyBudgetChoices)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = choice.Label,
                IsCheckable = true,
                IsChecked = choice.Usd == chat.DailyBudgetUsd,
            };
            decimal usd = choice.Usd;
            item.Click += (_, _) => chat.DailyBudgetUsd = usd;
            menu.Items.Add(item);
        }
        if (chat.HasBillingPage)
        {
            menu.Items.Add(new System.Windows.Controls.Separator());
            var billing = new System.Windows.Controls.MenuItem { Header = Services.LocalizedResources.Instance["chat.daily.billing"] };
            billing.Click += (_, _) => chat.OpenBillingCommand.Execute(null);
            menu.Items.Add(billing);
        }
        menu.IsOpen = true;
    }

    /// <summary>The title bar's theme button: every theme, the one on screen ticked; a click applies it at once and
    /// remembers it (same as Settings › Appearance).</summary>
    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var themes = Services.ThemeService.Current;
        if (themes == null) return;
        var loc = Services.LocalizedResources.Instance;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = loc["settings.theme"], IsEnabled = false, FontWeight = FontWeights.SemiBold });
        foreach (var theme in themes.Themes)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = theme.IsReady ? loc[theme.NameKey] : $"{loc[theme.NameKey]}  ·  {loc["settings.themeInProgress"]}",
                ToolTip = loc[theme.DescKey],
                IsCheckable = true,
                IsChecked = theme.Id == themes.CurrentThemeId,
            };
            string id = theme.Id;
            item.Click += (_, _) => themes.Apply(id);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
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
