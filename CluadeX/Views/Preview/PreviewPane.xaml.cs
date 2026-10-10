using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CluadeX.ViewModels;

namespace CluadeX.Views.Preview;

/// <summary>
/// The built-in browser's pane: address bar, viewport chip, "agent is driving" light, close button, the page itself
/// and a console mini-panel. Several panes may exist (the workbench split and the pop-out window); the page is shown
/// in one of them at a time — the others offer "Show it here".
///
/// The page's native window (<see cref="PreviewPaneHost"/>) exists only while this pane is in a loaded tree: it is
/// created on Loaded and destroyed on Unloaded (after giving the page back to the off-screen host), so a workbench
/// view that a CluadeX page switch discards — or a closed pop-out window — never keeps a window or a view tree alive.
/// </summary>
public partial class PreviewPane : UserControl
{
    private readonly PreviewViewModel? _vm;
    private PreviewPaneHost? _host;
    private bool _elsewhere;
    private bool _shown;
    private bool _claim;
    private bool _covered;
    private readonly List<(INotifyPropertyChanged Source, PropertyChangedEventHandler Handler)> _overlayWatch = new();

    public PreviewPane()
    {
        InitializeComponent();
        _vm = PreviewViewModel.Current;
        DataContext = _vm;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, e) =>
        {
            if (_vm == null) return;
            if (e.NewValue is true && !_shown) { _shown = true; _vm.PaneShown(); }
            else if (e.NewValue is false && _shown) { _shown = false; _vm.PaneHidden(); }
        };
    }

    /// <summary>The page's window while this pane is loaded (null before Loaded / after Unloaded).</summary>
    public PreviewPaneHost? Host => _host;

    /// <summary>A full-window overlay of the main window covers the page right now (the page stepped aside).</summary>
    public bool IsCovered => _covered;

    /// <summary>True in the pop-out window (no "pop out" button there). A pane put in a window takes the page as
    /// soon as it is shown — that is what the pop-out button asked for.</summary>
    public bool IsInWindow
    {
        get => PopOutButton.Visibility != Visibility.Visible;
        set
        {
            PopOutButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            if (value) ClaimPage();
        }
    }

    /// <summary>Take the page into this pane as soon as its window exists — even while another pane shows it.</summary>
    public void ClaimPage()
    {
        _claim = true;
        _elsewhere = false;
        if (IsLoaded) ScheduleClaim();
    }

    private void ScheduleClaim(int attempt = 0)
    {
        // After the host's own Loaded (its window exists then) and after this pane's first refresh; while the
        // host has no window yet (or is not visible yet), try again a little later.
        Dispatcher.BeginInvoke(attempt == 0 ? DispatcherPriority.Loaded : DispatcherPriority.Background, new Action(() =>
        {
            if (!_claim || _host == null || !IsLoaded) return;
            _elsewhere = false;
            ElsewhereState.Visibility = Visibility.Collapsed;
            if (!_covered && _vm is { HasPage: true, IsLocked: false }) _host.Visibility = Visibility.Visible;
            if (!_host.HasWindow || !_host.IsVisible)
            {
                if (attempt < 40) ScheduleClaim(attempt + 1);
                else _claim = false;   // no page to show: the normal refresh decides later
                return;
            }
            _claim = false;
            _host.Borrow();
        }));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        if (_host == null)
        {
            _host = new PreviewPaneHost { Browser = _vm.Browser, Visibility = Visibility.Collapsed };
            HostSlot.Children.Add(_host);
        }
        _vm.Refreshed += OnRefreshed;
        if (IsVisible && !_shown) { _shown = true; _vm.PaneShown(); }
        WatchOverlays();
        OnRefreshed();
        if (_claim) ScheduleClaim();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.Refreshed -= OnRefreshed;
        if (_shown) { _shown = false; _vm.PaneHidden(); }
        UnwatchOverlays();
        // Give the page back to the off-screen host, then destroy this pane's window (DestroyWindowCore parks
        // first as well) — nothing static keeps the discarded view alive.
        var host = _host;
        _host = null;
        if (host != null)
        {
            HostSlot.Children.Remove(host);
            try { host.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[preview] host dispose failed: {ex.Message}"); }
        }
        _elsewhere = false;
    }

    /// <summary>Show the page here, hide it (another pane has it / an overlay covers it), or show nothing.</summary>
    private void OnRefreshed()
    {
        if (_vm == null || _host == null) return;
        bool canShow = _vm.HasPage && !_vm.IsLocked;
        CoveredState.Visibility = canShow && _covered ? Visibility.Visible : Visibility.Collapsed;
        if (!canShow || _covered)
        {
            if (!canShow) _elsewhere = false;
            _host.Visibility = Visibility.Collapsed;
            ElsewhereState.Visibility = Visibility.Collapsed;
            return;
        }
        IntPtr active = _vm.Browser.ActiveHost;
        if (_claim)
        {
            _elsewhere = false;   // this pane is about to take the page (ScheduleClaim)
        }
        else if (_elsewhere)
        {
            if (active == IntPtr.Zero) _elsewhere = false;   // the other window gave it back — take it
        }
        else if (active != IntPtr.Zero && !_host.HasThePage)
        {
            _elsewhere = true;                                 // another pane has it (a newer pane defers unless it claims)
        }
        _host.Visibility = _elsewhere ? Visibility.Hidden : Visibility.Visible;
        ElsewhereState.Visibility = _elsewhere ? Visibility.Visible : Visibility.Collapsed;
        if (!_elsewhere && !_claim && _host.IsVisible && !_host.HasThePage) _host.Borrow();
        if (_vm.IsConsoleOpen) ConsoleScroll.ScrollToEnd();
    }

    private void OnBringHere(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        _elsewhere = false;
        ElsewhereState.Visibility = Visibility.Collapsed;
        _host.Visibility = Visibility.Visible;
        _host.Borrow();
    }

    // ─────────────────────────── airspace ───────────────────────────

    /// <summary>The page is a native child window: WPF content drawn over its area (the main window's command
    /// palette, the model-loading overlay) would be hidden under it. While one of them is open the page steps
    /// aside — parked off-screen, still running — and a placeholder takes its place. Only the main window has such
    /// overlays; the pop-out window is its own top-level window.</summary>
    private void WatchOverlays()
    {
        UnwatchOverlays();
        var window = Window.GetWindow(this);
        if (window == null || !ReferenceEquals(window, Application.Current?.MainWindow)) return;
        if (window.DataContext is not INotifyPropertyChanged main) return;
        PropertyChangedEventHandler onMain = (_, e) =>
        {
            if (e.PropertyName is null or "" or "IsModelLoading" or "Palette") { WatchPalette(main); UpdateCovered(main); }
        };
        main.PropertyChanged += onMain;
        _overlayWatch.Add((main, onMain));
        WatchPalette(main);
        UpdateCovered(main);
    }

    private void WatchPalette(INotifyPropertyChanged main)
    {
        for (int i = _overlayWatch.Count - 1; i >= 1; i--)
        {
            _overlayWatch[i].Source.PropertyChanged -= _overlayWatch[i].Handler;
            _overlayWatch.RemoveAt(i);
        }
        if (Read(main, "Palette") is INotifyPropertyChanged palette)
        {
            PropertyChangedEventHandler onPalette = (_, e) => { if (e.PropertyName is null or "" or "IsOpen") UpdateCovered(main); };
            palette.PropertyChanged += onPalette;
            _overlayWatch.Add((palette, onPalette));
        }
    }

    private void UnwatchOverlays()
    {
        foreach (var (source, handler) in _overlayWatch) source.PropertyChanged -= handler;
        _overlayWatch.Clear();
        _covered = false;
    }

    private void UpdateCovered(object main)
    {
        void Apply()
        {
            bool covered = Read(main, "IsModelLoading") is true || Read(Read(main, "Palette"), "IsOpen") is true;
            if (covered == _covered) return;
            _covered = covered;
            OnRefreshed();
        }
        if (Dispatcher.CheckAccess()) Apply();
        else Dispatcher.BeginInvoke(new Action(Apply));
    }

    private static object? Read(object? source, string property)
    {
        if (source == null) return null;
        try { return source.GetType().GetProperty(property)?.GetValue(source); }
        catch { return null; }
    }

    // ─────────────────────────── address bar ───────────────────────────

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _vm == null) return;
        e.Handled = true;
        if (_vm.NavigateCommand.CanExecute(null)) _vm.NavigateCommand.Execute(null);
    }

    private void OnAddressFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm != null) _vm.AddressHasFocus = true;
    }

    private void OnAddressBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm != null) _vm.AddressHasFocus = false;
    }
}
