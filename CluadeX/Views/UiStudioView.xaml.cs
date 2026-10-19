using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CluadeX.Services.UiStudio;
using CluadeX.ViewModels;
using CluadeX.Views.UiStudio;
using Microsoft.Web.WebView2.Core;

namespace CluadeX.Views;

/// <summary>
/// Hosts the UI Studio's page (a WebView2 — a native window, so anything WPF draws over it is hidden behind it: the
/// view hides the page while the gallery, the empty state, the Ctrl+K palette or the model-loading screen is up)
/// and wires it to the overlay script: the page's messages go to the view model,
/// the view model's script requests go to the page. The page itself is built once (PageHost keeps this view), so
/// switching to Chat and back keeps the running app, its scroll position and state.
/// </summary>
public partial class UiStudioView : UserControl
{
    private UiStudioViewModel? _vm;
    /// <summary>Where the mouse went down on the widget palette, and on which widget (a drag starts from there).</summary>
    private (Point At, WidgetPaletteItem? Item)? _paletteDown;
    private StudioService? _studio;
    private Task? _init;
    private string? _pendingUrl;
    private string? _reselectSrc;
    private string? _reselectSelector;
    private readonly List<(System.ComponentModel.INotifyPropertyChanged Source, System.ComponentModel.PropertyChangedEventHandler Handler)> _overlayWatch = new();
    private bool _covered;
    private CoreWebView2Environment? _env;
    private PageShooter? _shooter;
    private ScrollViewer? _pagesScroll;
    private bool _visibleQueued;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LastInputInfo { public uint cbSize; public uint dwTime; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    /// <summary>
    /// Messages that WRITE to the source ("text", "resize", "command") are only taken right after the user really
    /// clicked or typed — with CluadeX in front. The page's own scripts share the overlay's world and can post the
    /// same messages; they cannot make real input, so an untrusted project can't rewrite its files just by being opened.
    /// (The page is a native window: its clicks never reach WPF events, hence the OS-level input time.)
    /// </summary>
    private static bool UserJustActed
    {
        get
        {
            var info = new LastInputInfo { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return false;
            uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            return idleMs < 2000 && pid == (uint)Environment.ProcessId;
        }
    }

    public UiStudioView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as UiStudioViewModel);
        IsVisibleChanged += (_, e) => OnShownChanged(e.NewValue is true);
        Loaded += (_, _) => { Attach(DataContext as UiStudioViewModel); _init ??= InitWebAsync(); if (!IsVisible) QueueSuspend(); };
        Loaded += (_, _) => WatchOverlays();
        Unloaded += (_, _) => UnwatchOverlays();
        ((System.Collections.Specialized.INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, _) =>
        {
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        };

        // the page gallery: rows are virtualised, so only the cards on screen load their pictures and get shot
        PagesList.Loaded += (_, _) =>
        {
            PagesList.ApplyTemplate();
            if (_pagesScroll == null && PagesList.Template?.FindName("PagesScroll", PagesList) is ScrollViewer sv)
            {
                _pagesScroll = sv;
                sv.ScrollChanged += (_, _) => QueueVisible();
            }
            UpdateColumns();
        };
        PagesList.SizeChanged += (_, _) => { UpdateColumns(); QueueVisible(); };
        StageView.NodeClicked += n => _vm?.PickStageNode(n, open: System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control));
        RenderView.BoxClicked += (b, ctrl) => _vm?.PickStageBox(b, ctrl);
        RenderView.DropPlanner = (x, y) => _vm?.PlanDrop(x, y);
        RenderView.Dropped += (key, x, y) => _vm?.DropPalette(key, x, y);
        // a palette widget dragged out of the panel (a plain click still inserts it next to the picked widget)
        WidgetPalette.PreviewMouseLeftButtonDown += (_, e) => _paletteDown = (e.GetPosition(WidgetPalette), (e.OriginalSource as FrameworkElement)?.DataContext as WidgetPaletteItem);
        WidgetPalette.PreviewMouseMove += (_, e) =>
        {
            if (_paletteDown is not { Item: { } item } down || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            var p = e.GetPosition(WidgetPalette);
            if (Math.Abs(p.X - down.At.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - down.At.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _paletteDown = null;
            DragDrop.DoDragDrop(WidgetPalette, new DataObject(RenderStageView.WidgetFormat, item.Key), DragDropEffects.Copy);
        };
        WidgetPalette.PreviewMouseLeftButtonUp += (_, _) => _paletteDown = null;
    }

    private void UpdateColumns()
    {
        if (_vm == null || PagesList.ActualWidth <= 0) return;
        _vm.GalleryColumns = Math.Max(1, (int)((PagesList.ActualWidth - 18) / (_vm.CardWidth + 12)));
    }

    private void QueueVisible()
    {
        if (_visibleQueued) return;
        _visibleQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { _visibleQueued = false; ReportVisible(); }));
    }

    /// <summary>Tell the view model which cards are on screen (realised rows inside the viewport, plus a little below).</summary>
    private void ReportVisible()
    {
        if (_vm is not { ShowPages: true } || _pagesScroll == null) return;
        var cards = new List<PageCard>();
        double bottom = _pagesScroll.ViewportHeight + 240;
        for (int i = 0; i < PagesList.Items.Count; i++)
        {
            if (PagesList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row || !row.IsVisible) continue;
            var at = row.TranslatePoint(new Point(0, 0), _pagesScroll);
            if (at.Y + row.ActualHeight < -40 || at.Y > bottom) continue;
            if (row.DataContext is PageRow r) cards.AddRange(r.Cards);
        }
        _vm.NoteVisible(cards);
    }

    /// <summary>Take a page's thumbnail with a hidden browser that shares the Studio's profile.</summary>
    private async Task<PageShot?> ShootAsync(string url, int width, int height, CancellationToken ct)
    {
        _init ??= InitWebAsync();
        try { await _init; } catch { }
        if (_env == null) return null;
        _shooter ??= new PageShooter(_env);
        return await _shooter.ShootAsync(url, width, height, ct);
    }

    private void Attach(UiStudioViewModel? vm)
    {
        if (vm == null || ReferenceEquals(vm, _vm)) return;
        _vm = vm;
        _studio = vm.Studio;
        _studio.NavigateRequested += Navigate;
        _studio.ScriptRequested += Run;
        _studio.ReloadRequested += OnReloadRequested;
        _studio.WakeRequested += Wake;
        _studio.Evaluate = EvaluateAsync;
        vm.Shooter = ShootAsync;
        vm.NavigateTo += Navigate;
        vm.AddressFocusRequested += param =>
        {
            Address.Focus();
            int at = param == null ? -1 : Address.Text.IndexOf(param, StringComparison.Ordinal);
            if (at >= 0) Address.Select(at, param!.Length); else Address.SelectAll();
        };
        vm.PageRows.CollectionChanged += (_, _) => QueueVisible();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UiStudioViewModel.HasProject) or nameof(UiStudioViewModel.NoProject) or nameof(UiStudioViewModel.ShowGallery)
                or nameof(UiStudioViewModel.ShowPages) or nameof(UiStudioViewModel.IsSimulated))
                UpdateWebVisibility();
            if (e.PropertyName is nameof(UiStudioViewModel.ShowPages) && vm.ShowPages) { UpdateColumns(); QueueVisible(); }
            if (e.PropertyName is nameof(UiStudioViewModel.CardWidth)) UpdateColumns();
        };
        UpdateWebVisibility();
        vm.SetOnScreen(IsVisible);
        if (IsVisible) vm.PageShown();
    }

    // ───────────────────────────── on screen / asleep ─────────────────────────────

    private System.Windows.Threading.DispatcherTimer? _suspendTimer;

    /// <summary>A hidden page already draws nothing; after a while off screen its renderer is also suspended (no
    /// scripts, no timers, no network polling). It wakes at once when shown, or when anything needs it (a script
    /// from the Node Graph, a navigation, a reload) — its state, scroll and DOM are all kept.</summary>
    private void OnShownChanged(bool shown)
    {
        _vm?.SetOnScreen(shown);
        if (shown)
        {
            _suspendTimer?.Stop();
            Wake();
            _vm?.PageShown();
        }
        else QueueSuspend();
    }

    private void QueueSuspend()
    {
        _suspendTimer ??= NewSuspendTimer();
        _suspendTimer.Stop();
        _suspendTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer NewSuspendTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        t.Tick += async (_, _) =>
        {
            t.Stop();
            var core = Web.CoreWebView2;
            if (core == null || IsVisible || _studio?.KeepPageAwake == true || core.IsSuspended) return;
            try { await core.TrySuspendAsync(); }
            catch { /* still marked visible by the control, or the page is busy: it simply stays awake */ }
        };
        return t;
    }

    private void Wake()
    {
        try { if (Web.CoreWebView2 is { IsSuspended: true } core) core.Resume(); }
        catch { }
        if (!IsVisible && IsLoaded) QueueSuspend();   // woken while hidden (a script, a reload): back to sleep later
    }

    // ───────────────────────────── what may cover the page ─────────────────────────────

    /// <summary>The page shows only when nothing WPF draws should be on top of it.</summary>
    private void UpdateWebVisibility()
    {
        bool show = _vm is { HasProject: true, ShowGallery: false, ShowPages: false, IsSimulated: false } && !_covered;
        Frame.Visibility = show ? Visibility.Visible : Visibility.Hidden;   // Hidden keeps the page alive and laid out
    }

    private void WatchOverlays()
    {
        UnwatchOverlays();
        var window = Window.GetWindow(this);
        if (window?.DataContext is not System.ComponentModel.INotifyPropertyChanged main) return;
        System.ComponentModel.PropertyChangedEventHandler onMain = (_, e) =>
        {
            if (e.PropertyName is null or "" or "IsModelLoading" or "Palette") { WatchPalette(main); UpdateCovered(main); }
        };
        main.PropertyChanged += onMain;
        _overlayWatch.Add((main, onMain));
        WatchPalette(main);
        UpdateCovered(main);
    }

    private void WatchPalette(System.ComponentModel.INotifyPropertyChanged main)
    {
        for (int i = _overlayWatch.Count - 1; i >= 1; i--)
        {
            _overlayWatch[i].Source.PropertyChanged -= _overlayWatch[i].Handler;
            _overlayWatch.RemoveAt(i);
        }
        if (Read(main, "Palette") is System.ComponentModel.INotifyPropertyChanged palette)
        {
            System.ComponentModel.PropertyChangedEventHandler onPalette = (_, e) => { if (e.PropertyName is null or "" or "IsOpen") UpdateCovered(main); };
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
            _covered = Read(main, "IsModelLoading") is true || Read(Read(main, "Palette"), "IsOpen") is true;
            UpdateWebVisibility();
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

    // ───────────────────────────── engine ─────────────────────────────

    private async Task InitWebAsync()
    {
        if (_studio == null) return;
        try
        {
            System.IO.Directory.CreateDirectory(_studio.WebDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, _studio.WebDataFolder, new CoreWebView2EnvironmentOptions());
            _env = env;   // the page gallery's hidden browser shares it (same profile: the same logins)
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;            // F12 still works for the developer
            core.Settings.IsWebMessageEnabled = true;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(StudioTemplates.ReadAsset("overlay.js"));
            core.WebMessageReceived += OnWebMessage;
            core.SourceChanged += (_, _) => _vm?.NotePage(core.Source);
            core.NavigationCompleted += (_, e) =>
            {
                _vm?.NotePage(core.Source);
                if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    _vm?.Status(Loc("studio.err.navigate", e.WebErrorStatus.ToString()), error: true);
            };
            // Links that open a new window stay in the Studio (one page, one overlay).
            core.NewWindowRequested += (_, e) => { e.Handled = true; core.Navigate(e.Uri); };
            core.ProcessFailed += (_, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _vm?.Status(Loc("studio.err.crashed", e.ProcessFailedKind.ToString()), error: true);
                    if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited) { try { core.Reload(); } catch { } }
                });
            };
            if (_pendingUrl != null) { core.Navigate(_pendingUrl); _pendingUrl = null; }
            else if ((_studio.PageUrl ?? _studio.LastRequestedUrl) is { } url) core.Navigate(url);   // asked before this browser existed
        }
        catch (Exception ex)
        {
            // No WebView2 runtime, or the data folder is unusable: say so where the page would be.
            EngineError.Visibility = Visibility.Visible;
            EngineErrorText.Text = Loc("studio.err.engine", ex.Message);
        }
    }

    private string Loc(string key, params object[] args) => _vm?.Tr(key, args) ?? key;

    private void Navigate(string url)
    {
        if (Web.CoreWebView2 == null) { _pendingUrl = url; _init ??= InitWebAsync(); return; }
        Wake();
        try { Web.CoreWebView2.Navigate(url); }
        catch (ArgumentException) { _vm?.Status(Loc("studio.err.badUrl"), error: true); }
    }

    private void Run(string js)
    {
        var core = Web.CoreWebView2;
        if (core == null) return;
        Wake();
        _ = core.ExecuteScriptAsync(js);
    }

    private async Task<string?> EvaluateAsync(string js)
    {
        var core = Web.CoreWebView2;
        if (core == null) return null;
        Wake();
        try { return await core.ExecuteScriptAsync(js); }
        catch { return null; }
    }

    private void OnReloadRequested(bool cssOnly)
    {
        var core = Web.CoreWebView2;
        if (core == null) return;
        Wake();
        if (cssOnly) { Run("window.__cx && __cx.reloadCss(); window.__cx && setTimeout(() => __cx.refreshSelection(), 120)"); return; }
        _reselectSrc = _studio?.Selection?.Src;
        _reselectSelector = _studio?.Selection?.Selector;
        core.Reload();
    }

    // ───────────────────────────── overlay messages ─────────────────────────────

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_vm == null || _studio == null) return;
        string json;
        try { json = e.WebMessageAsJson; }
        catch { return; }
        var msg = OverlayMessage.Parse(json);
        if (msg == null) return;

        switch (msg.Type)
        {
            case "ready":
                _vm.NotePage(Web.CoreWebView2?.Source);
                if (!_vm.IsSelectMode) Run("__cx.setMode('interact')");
                if (_reselectSrc != null || _reselectSelector != null)
                {
                    // the same element again after a reload: by its source location, else by its CSS path
                    Run($"__cx.selectBySrc({JsonSerializer.Serialize(_reselectSrc ?? "")}) || __cx.selectBySelector({JsonSerializer.Serialize(_reselectSelector ?? "")})");
                    _reselectSrc = _reselectSelector = null;
                }
                break;
            case "select":
            case "deselect":
            case "tree":
                _studio.HandleOverlayMessage(msg);
                break;
            case "text" or "resize" or "command" when !UserJustActed:
                _vm.Status(Loc("studio.err.noGesture"), error: true);
                break;
            case "text" when msg.Element != null && msg.Value != null:
                _studio.HandleOverlayMessage(new OverlayMessage { Type = "select", Element = msg.Element });
                _vm.InlineText(msg.Element, msg.Value);
                break;
            case "resize" when msg.Element != null:
                _studio.HandleOverlayMessage(new OverlayMessage { Type = "select", Element = msg.Element });
                _vm.Resized(msg.Element, msg.Width, msg.Height);
                break;
            case "command" when msg.Value != null:
                _vm.Structure(msg.Value);
                break;
        }
    }

    // ───────────────────────────── layers panel ─────────────────────────────

    private void Layer_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // MouseEnter reaches the outer items first, the innermost last — so the item under the pointer wins.
        if (sender is TreeViewItem { DataContext: LayerItem item }) _vm?.HoverLayer(item);
    }

    // Selecting a deep layer must not scroll the panel sideways to its full indentation.
    private void Layer_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (sender is TreeViewItem tvi && e.TargetObject == tvi)
        {
            e.Handled = true;
            if (tvi.Template?.FindName("PART_Header", tvi) is FrameworkElement header)
                header.BringIntoView(new Rect(0, 0, 1, header.ActualHeight));
        }
    }
}
