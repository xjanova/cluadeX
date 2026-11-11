using System.Windows;
using System.Windows.Input;
using CluadeX.ViewModels;

namespace CluadeX;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    /// <summary>An activity-bar button checked without a press (UI Automation Select): navigate like a click. A click
    /// (IsPressed during the check) runs its Command itself; a check from the binding is already the current page.</summary>
    private void OnActivityChecked(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.RadioButton { GroupName: "Activity" } rb || rb.IsPressed) return;
        string? page = rb.CommandParameter?.ToString();
        if (page == null || string.Equals(page, _viewModel?.SelectedNavItem, StringComparison.Ordinal)) return;
        if (rb.Command?.CanExecute(rb.CommandParameter) == true) rb.Command.Execute(rb.CommandParameter);
    }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        // A narrow window: the title bar's extras step aside, widest-first, so nothing important is clipped. The model
        // picker and the buddy also live below (the composer, the sidebar); the caption buttons have their own column.
        SizeChanged += (_, _) => ApplyTitleBarRoom();
        Loaded += (_, _) => ApplyTitleBarRoom();
        InitBuddyHero();   // น้อง CluadeX's size, what moves out of her way, and the text size zoom (MainWindow.Buddy.cs)
        _viewModel = viewModel;
        DataContext = viewModel;
        InitBuddyBubble(); // her speech bubble: tips now and then, a word when a turn ends or waits (MainWindow.BuddyBubble.cs)
        // The chat opened a project: the UI Studio builds its page (hidden) and starts showing it, so it is ready the
        // moment it is opened — no Open / Start to press first.
        if (viewModel.UiStudioVM is { } studio)
        {
            void Prewarm() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => PageArea.Prewarm(studio));
            studio.PrewarmRequested += Prewarm;
            Loaded += (_, _) => { if (studio.HasProject && studio.WantsPrewarm) Prewarm(); };
        }
        // Activity bar uses RadioButtons bound via SelectedNavItem converter —
        // initial selection comes from MainViewModel.SelectedNavItem ("Chat" default).
        // Their Command only runs on a click; UI Automation's Select (screen readers, test and filming tools) checked
        // the button but never changed the page (phase 3 of the dictionary video, 2026-10-20). A check that did not come
        // from a press runs the same command.
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler(OnActivityChecked));
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        // The user's own key, click or wheel: follow mode must not take the page from under them (FollowBack).
        PreviewKeyDown += (_, _) => _viewModel.NoteUserInput();
        PreviewMouseDown += (_, _) => _viewModel.NoteUserInput();
        PreviewMouseWheel += (_, _) => _viewModel.NoteUserInput();
        // The pet is its own top-level window (not owned, so it stays out when CluadeX is minimized): it must go
        // with the main window, or the app would keep running with only the pet on screen.
        Closed += (_, _) => _viewModel.Pet.Shutdown();

        // Node Graph, UI Studio and Code Editor can each float in a window of their own (Views/PopOut); the chat stays
        // here. Those windows are not owned either (own taskbar button, on screen while this one is minimised): they
        // close with this one, after its Closing was answered (unsaved tabs, a Spark download).
        PopOuts = new Views.PopOut.PagePopOutManager(this, PageArea);
        viewModel.IsPagePoppedOut = PopOuts.IsPoppedOut;
        viewModel.ShowPoppedOutPage = (page, activate) => PopOuts.Show(page, activate);
        viewModel.ShowMainWindowRequested += BringToFront;
        Closed += (_, _) =>
        {
            viewModel.ShowMainWindowRequested -= BringToFront;
            viewModel.IsPagePoppedOut = null;
            viewModel.ShowPoppedOutPage = null;
            PopOuts.CloseAll();
        };

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
            string tag = CluadeX.Services.SettingsService.IsTrialCopy ? "ตัวลอง"
                : kind == CluadeX.Services.AutoUpdateService.BuildKind.Dev ? "DEV" : "PORTABLE";
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

    /// <summary>The pages that can float in a window of their own, and those windows.</summary>
    public Views.PopOut.PagePopOutManager PopOuts { get; }

    /// <summary>A page in its own window asked for a page shown here (the graph's "UI Studio", the Studio's "open the
    /// chat"): this window comes forward with it.</summary>
    private void BringToFront()
    {
        if (!IsLoaded) return;
        if (WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(this);   // (maximised again if it was)
        Activate();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // An update restart is closing the app (Application.Shutdown — Cancel would be ignored). Its questions were
        // answered before it started: nothing is asked again; the chat and any tab edited since are saved.
        if (_viewModel.IsRestartingForUpdate)
        {
            try { _viewModel.ChatVM.NoteAppClosing(); } catch { }   // a chat still working gets its recovery card after it
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

        // Chats still working or waiting on a card: say what closing cuts off (and that it can be picked up again).
        // Asked last — the answers above are harmless if this one keeps the app open.
        if (!_viewModel.ChatVM.ConfirmCloseWithRuns())
        {
            e.Cancel = true;
            return;
        }
        // Every running chat saved as it stands, and marked as cut off by the close (the restart offers Continue).
        try { _viewModel.ChatVM.NoteAppClosing(); }
        catch { /* don't prevent closing */ }

        // Force-save current session before app exits
        try { _viewModel.ChatVM.SaveNow(); }
        catch { /* don't prevent closing */ }
    }

    /// <summary>The rail's ⋯ button: its menu of the pages that are not on the rail opens beside it (a left click, not
    /// only a right click). The click checked it for the moment; the menu's close puts it back to "is one of those
    /// pages on screen".</summary>
    private void MoreToolsButton_Click(object sender, RoutedEventArgs e)
    {
        MoreToolsMenu.PlacementTarget = MoreToolsButton;
        MoreToolsMenu.IsOpen = true;
    }

    private void MoreToolsMenu_Closed(object sender, RoutedEventArgs e)
        => System.Windows.Data.BindingOperations.GetBindingExpression(MoreToolsButton,
            System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();

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

    // ─── the sidebar's chat and project rows: right-click menus, drag a chat onto a project (Views/SidebarMenus) ───

    private void ChatRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement row && row.DataContext is Models.ChatSession chat)
        {
            Views.SidebarMenus.Open(Views.SidebarMenus.ForChat(_viewModel.ChatVM, chat), row);
            e.Handled = true;
        }
    }

    private void ChatRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement row) Views.SidebarMenus.ChatRowPressed(row, e);
    }

    private void ChatRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement row) Views.SidebarMenus.ChatRowMoved(row, e);
    }

    private void ProjectRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement row && row.DataContext is Models.ChatProject project)
        {
            Views.SidebarMenus.Open(Views.SidebarMenus.ForProject(_viewModel.ChatVM, project), row);
            e.Handled = true;
        }
    }

    private void ProjectRow_DragOver(object sender, DragEventArgs e) => Views.SidebarMenus.ProjectRowDragOver(e);

    /// <summary>A chat dropped on a project row — or on the "No project" row (no project behind it: out of any).</summary>
    private void ProjectRow_Drop(object sender, DragEventArgs e) =>
        Views.SidebarMenus.ProjectRowDrop(_viewModel.ChatVM, ((sender as FrameworkElement)?.DataContext as Models.ChatProject)?.Id ?? "", e);

    /// <summary>The "today" chip: the Billing page's spend &amp; savings, the daily budget (a paid run asks before going on
    /// past it), or the provider's own billing page — the only place the account's real remaining credit is shown.</summary>
    private void TodayChip_Click(object sender, RoutedEventArgs e)
    {
        var chat = _viewModel.ChatVM;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var usage = new System.Windows.Controls.MenuItem { Header = Services.LocalizedResources.Instance["usage.menu"], FontWeight = FontWeights.SemiBold };
        // The spend & savings window became a section of the Billing page: open the page there.
        usage.Click += (_, _) =>
        {
            _viewModel.NavigateToCommand.Execute("Billing");
            _viewModel.BillingVM?.ShowSavings();
        };
        menu.Items.Add(usage);
        menu.Items.Add(new System.Windows.Controls.Separator());
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

        // Minimised: no decorative animation asks for frames; restored: each resumes by its own rule.
        bool minimized = WindowState == WindowState.Minimized;
        if (minimized != _animationsPausedForMinimize)
        {
            _animationsPausedForMinimize = minimized;
            Views.AmbientAnimation.SetMinimized(this, minimized);
        }
    }

    private bool _animationsPausedForMinimize;

    private void ApplyTitleBarRoom()
    {
        double w = ActualWidth;
        static System.Windows.Visibility V(bool show) => show ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        // Nothing else hides on a narrow window any more: the title row glides (MarqueeScroller) so every chip comes
        // into view. Only the palette pill — a star column that would be squeezed to nothing — steps aside.
        if (TitlePalette != null) TitlePalette.Visibility = V(w >= 980);
    }
}
