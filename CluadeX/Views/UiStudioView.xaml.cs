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
    /// <summary>The browser exists AND the overlay script is registered: only then may it navigate (a page opened
    /// before that would load without the overlay).</summary>
    private bool _engineReady;
    private string? _pendingUrl;
    /// <summary>The page the Studio asked for last — where a Reload on a blank or failed page goes.</summary>
    private string? _wantedUrl;
    /// <summary><see cref="StudioService.PageGeneration"/> when that page was asked for: documents of navigations
    /// started under it are this project's.</summary>
    private int _navGeneration;
    private readonly PageLoadTracker _load = new();
    private System.Windows.Threading.DispatcherTimer? _loadTimer;
    /// <summary>The overlay's "ready" was handled for the document shown now.</summary>
    private bool _readyHandled;
    private readonly List<DateTime> _rebuilds = new();
    /// <summary>The rebuilds were given up (a browser that keeps dying): only the error card's Try again starts it again —
    /// not a navigation, a reload or the gallery, which would bring the loop back.</summary>
    private bool _engineGaveUp;
    /// <summary>The browser core whose events are hooked: a start retried on the same control must not hook them twice
    /// (every overlay message would be handled twice — an in-place edit written twice).</summary>
    private CoreWebView2? _hookedCore;
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
        Loaded += (_, _) => { Attach(DataContext as UiStudioViewModel); StartEngine(); if (!IsVisible) { QueueSuspend(); _load.Hidden(Now); } };
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
        var init = StartEngine();
        try { if (init != null) await init; } catch { }
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
        _studio.PageReloadRequested += OnPageReloadRequested;
        _studio.WakeRequested += OnWakeRequested;
        _studio.Evaluate = EvaluateAsync;
        _load.Changed += () => _studio?.SetPageLoad(_load.Info);
        _load.Command += OnLoadCommand;
        vm.Shooter = ShootAsync;
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
                or nameof(UiStudioViewModel.ShowPages) or nameof(UiStudioViewModel.IsSimulated) or nameof(UiStudioViewModel.IsUnsupported)
                or nameof(UiStudioViewModel.PageLoadFailed))
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
            LoadShown();
        }
        else
        {
            QueueSuspend();
            if (_studio?.KeepPageAwake != true) { _load.Hidden(Now); ArmLoadTimer(); }   // no retry counted against an unseen page
        }
    }

    /// <summary>The Node Graph's live view needs the page: awake, and its load followed as if it were on screen.</summary>
    private void OnWakeRequested()
    {
        Wake();
        LoadShown();
    }

    /// <summary>On screen again: the load is checked once more (a hidden page may have been suspended in the middle
    /// of anything), and a blank or failed page goes to the wanted page.</summary>
    private void LoadShown()
    {
        if (_studio == null) return;
        // (also before the engine is up: the tracker must not stay paused; a navigation it asks for waits for the engine)
        _load.Shown(_studio.IsServing, SafeSource(), Now);
        ArmLoadTimer();
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
            var core = CurrentCore();
            if (core == null || IsVisible || _studio?.KeepPageAwake == true) return;
            try { if (!core.IsSuspended) await core.TrySuspendAsync(); }
            catch { /* still marked visible by the control, or the page is busy: it simply stays awake */ }
        };
        return t;
    }

    private void Wake()
    {
        try { if (CurrentCore() is { IsSuspended: true } core) core.Resume(); }
        catch { }
        if (!IsVisible && IsLoaded) QueueSuspend();   // woken while hidden (a script, a reload): back to sleep later
    }

    // ───────────────────────────── what may cover the page ─────────────────────────────

    /// <summary>The page shows only when nothing WPF draws should be on top of it.</summary>
    private void UpdateWebVisibility()
    {
        // (a load that failed shows its card in the page's place — the browser has to step aside for it to be seen)
        bool show = _vm is { HasProject: true, ShowGallery: false, ShowPages: false, IsSimulated: false, IsUnsupported: false, PageLoadFailed: false } && !_covered;
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

    /// <summary>Start the browser when it is not up (again: after a failed start, or after it was rebuilt). Returns
    /// the start that runs now, if any.</summary>
    private Task? StartEngine()
    {
        if (_studio == null || _engineGaveUp) return _init;
        if (_init == null || _init.IsCompleted && !_engineReady) _init = InitWebAsync();
        return _init;
    }

    private async Task InitWebAsync()
    {
        if (_studio == null) return;
        _engineReady = false;
        EngineError.Visibility = Visibility.Collapsed;
        var web = Web;
        try
        {
            System.IO.Directory.CreateDirectory(_studio.WebDataFolder);
            var env = _env ?? await CoreWebView2Environment.CreateAsync(null, _studio.WebDataFolder, new CoreWebView2EnvironmentOptions());
            _env = env;   // the page gallery's hidden browser shares it (same profile: the same logins)
            await web.EnsureCoreWebView2Async(env);
            if (!ReferenceEquals(web, Web)) return;   // rebuilt meanwhile: the new one starts on its own
            var core = web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = true;            // F12 still works for the developer
            core.Settings.IsWebMessageEnabled = true;
            // every handler before the next await: nothing the browser raises while the script registers is missed
            // (once per core: a start retried after the script failed to register reuses this control and its core)
            if (!ReferenceEquals(_hookedCore, core)) { HookCore(core); _hookedCore = core; }
            await core.AddScriptToExecuteOnDocumentCreatedAsync(StudioTemplates.ReadAsset("overlay.js"));
            if (!ReferenceEquals(web, Web)) return;
            _engineReady = true;
            if (!IsVisible && !_studio.KeepPageAwake) _load.Hidden(Now);
            // asked before this browser could go anywhere: the latest ask wins
            string? pending = _pendingUrl;
            _pendingUrl = null;
            if (pending != null) GoCore(pending);
            else if (_studio.LastRequestedUrl is { } url) Navigate(url);   // asked before this view existed
        }
        catch (Exception ex)
        {
            // No WebView2 runtime, or the data folder is unusable (locked by another copy, a transient error): say so
            // where the page would be. Try again — or the next navigation — starts it again.
            // (A start for a control since replaced says nothing: the new control's start may already have succeeded.)
            if (!ReferenceEquals(web, Web)) return;
            _engineReady = false;
            EngineError.Visibility = Visibility.Visible;
            EngineErrorText.Text = Loc("studio.err.engine", ex.Message);
        }
    }

    private void HookCore(CoreWebView2 core)
    {
        core.WebMessageReceived += OnWebMessage;
        core.SourceChanged += (_, _) => Safe(() => _vm?.NotePage(core.Source));
        core.NavigationStarting += (_, e) =>
        {
            if (!ReferenceEquals(core, CurrentCore())) return;
            Safe(() => _load.Started(e.NavigationId, e.Uri ?? "", Now));
            ArmLoadTimer();
        };
        core.ContentLoading += (_, e) =>
        {
            if (!ReferenceEquals(core, CurrentCore())) return;
            Safe(() =>
            {
                // The new document replaces the old one before any of its scripts runs: from here on its picks and its
                // tree are this project's. (Its first tree and "ready" come at DOMContentLoaded, long before
                // NavigationCompleted — which is when this used to happen, so they were dropped and Layers stayed empty.)
                if (_load.ContentLoading(e.NavigationId, e.IsErrorPage, Now)) _studio?.NoteDocumentLoaded(_navGeneration);
                _readyHandled = false;
            });
            ArmLoadTimer();
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (!ReferenceEquals(core, CurrentCore())) return;
            int status = 0;
            try { status = e.HttpStatusCode; } catch { }
            Safe(() => _vm?.NotePage(core.Source));
            Safe(() => _load.Completed(e.NavigationId, e.IsSuccess, e.WebErrorStatus.ToString(), status, Now));
            ArmLoadTimer();
        };
        // Links that open a new window stay in the Studio (one page, one overlay).
        core.NewWindowRequested += (_, e) => { e.Handled = true; Safe(() => core.Navigate(e.Uri)); };
        core.ProcessFailed += (_, e) =>
        {
            var kind = e.ProcessFailedKind;
            Dispatcher.BeginInvoke(() =>
            {
                // Compared with the core this handler hooked, never by reading Web.CoreWebView2: once the browser process
                // is gone the control's own handler (it runs first) marks it crashed, and from then on that getter
                // throws — which used to end this handler before the rebuild, leaving the Studio dead for good.
                if (!ReferenceEquals(core, _hookedCore)) return;   // a browser since replaced
                CoreOf(Web, out bool dead);
                if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited || dead)
                {
                    // the whole browser is gone: this control cannot be used again, only replaced
                    _vm?.Status(Loc("studio.err.browserGone", kind.ToString()), error: true);
                    RebuildEngine();
                    return;
                }
                _vm?.Status(Loc("studio.err.crashed", kind.ToString()), error: true);
                if (kind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                {
                    _load.Reloading(Now);
                    if (!Safe(core.Reload)) RebuildEngine();
                    ArmLoadTimer();
                }
            });
        };
    }

    /// <summary>A new browser in place of a dead one (its process exited): a fresh control in the frame, a fresh
    /// environment (the old one's browser process is gone, the gallery's hidden browser with it), then the page that
    /// was shown. A fourth rebuild within a minute stops: the error is shown with Try again instead of a loop.</summary>
    private void RebuildEngine()
    {
        var now = Now;
        _rebuilds.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
        _rebuilds.Add(now);
        string? url = _studio?.PageUrl ?? _wantedUrl ?? _studio?.LastRequestedUrl;
        _engineReady = false;
        _hookedCore = null;   // (a late failure of the old browser rebuilds nothing more)
        var old = Web;
        var fresh = new Microsoft.Web.WebView2.Wpf.WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
        Frame.Child = fresh;
        Web = fresh;
        try { old.Dispose(); } catch { }
        try { _shooter?.Dispose(); } catch { }
        _shooter = null;
        _env = null;
        _init = null;
        if (url != null && url != StudioService.BlankPage) { _pendingUrl = url; _load.Restart(url, now); }
        if (_rebuilds.Count > 3)
        {
            // given up until Try again: nothing loads meanwhile (no progress bar left running for a page that never comes)
            _engineGaveUp = true;
            _load.Cancel(now);
            EngineError.Visibility = Visibility.Visible;
            EngineErrorText.Text = Loc("studio.err.engine", Loc("studio.err.browserGone", "×" + _rebuilds.Count));
            return;
        }
        StartEngine();
    }

    private void EngineRetry_Click(object sender, RoutedEventArgs e)
    {
        _rebuilds.Clear();
        _engineGaveUp = false;
        _pendingUrl ??= _wantedUrl ?? _studio?.LastRequestedUrl;
        if (_pendingUrl is { } url && url != StudioService.BlankPage) _load.Restart(url, Now);
        StartEngine();
    }

    private string Loc(string key, params object[] args) => _vm?.Tr(key, args) ?? key;

    private static DateTime Now => DateTime.UtcNow;

    /// <summary>Run a call into the browser; false when it threw. Nothing a browser call throws (a dead browser throws
    /// from everything) may reach a caller: these are event handlers of the Studio's service.</summary>
    private static bool Safe(Action call)
    {
        try { call(); return true; }
        catch { return false; }
    }

    private string? SafeSource()
    {
        try { return CurrentCore()?.Source; }
        catch { return null; }
    }

    /// <summary>The browser of the control in the frame, or null — before it started, and after its browser process
    /// died (the control's getter throws then, until the control is replaced). Every read of the core goes through
    /// here: the callers are the service's event handlers and timers, and a throw from them reached the chat's run
    /// start and end (the AI lock runs a script on every run).</summary>
    private CoreWebView2? CurrentCore() => CoreOf(Web, out _);

    private static CoreWebView2? CoreOf(Microsoft.Web.WebView2.Wpf.WebView2 web, out bool dead)
    {
        dead = false;
        try { return web.CoreWebView2; }
        catch { dead = true; return null; }
    }

    // ───────────────────────────── navigation & reload ─────────────────────────────

    private void Navigate(string url)
    {
        if (url == StudioService.BlankPage) _reselectSrc = _reselectSelector = null;   // the project is gone: so is its picked element
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) { _vm?.Status(Loc("studio.err.badUrl"), error: true); return; }
        _wantedUrl = url;
        _navGeneration = _studio?.PageGeneration ?? 0;
        _load.Want(url, Now);
        if (!_engineReady) { Defer(url); return; }   // (the latest ask wins)
        GoCore(url);
    }

    /// <summary>The browser cannot go anywhere yet: it goes there once it is up. Given up (Try again not pressed yet):
    /// nothing is on its way, so nothing shows as loading.</summary>
    private void Defer(string url)
    {
        _pendingUrl = url;
        StartEngine();
        if (_engineGaveUp) _load.Cancel(Now);
    }

    /// <summary>Send the browser there (the tracker already knows).</summary>
    private void GoCore(string url)
    {
        var core = CurrentCore();
        if (!_engineReady || core == null) { Defer(url); return; }
        Wake();
        try { core.Navigate(url); }
        catch (ArgumentException) { _vm?.Status(Loc("studio.err.badUrl"), error: true); _load.Cancel(Now); }
        catch (Exception) { RebuildEngine(); }   // the browser is gone
        ArmLoadTimer();
    }

    /// <summary>The user's Reload, or the failure card's Try again.</summary>
    private void OnPageReloadRequested(bool hard)
    {
        if (!_engineReady) { _pendingUrl ??= _wantedUrl ?? _studio?.LastRequestedUrl; StartEngine(); return; }
        Reload(hard, keepSelection: true);
    }

    /// <summary>A real reload — or, on a blank page or the browser's error page, the wanted page again (reloading those
    /// only shows them again, which is what location.reload() used to do).</summary>
    private void Reload(bool hard, bool keepSelection)
    {
        var core = CurrentCore();
        if (core == null) return;
        if (_load.ReloadTarget(SafeSource()) is { } target)
        {
            _load.Restart(target, Now);
            GoCore(target);
            return;
        }
        if (keepSelection)
        {
            _reselectSrc = _studio?.Selection?.Src;
            _reselectSelector = _studio?.Selection?.Selector;
        }
        _load.Reloading(Now);
        ReloadCore(core, hard);
    }

    private void ReloadCore(CoreWebView2 core, bool hard)
    {
        Wake();
        if (hard) _ = HardReloadAsync(core);
        else if (!Safe(core.Reload)) RebuildEngine();
        ArmLoadTimer();
    }

    /// <summary>A reload that ignores the cache (Shift+Reload) — for a server-rendered site; the Studio's own server
    /// sends no-store anyway.</summary>
    private async Task HardReloadAsync(CoreWebView2 core)
    {
        try { await core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}"); }
        catch { if (!Safe(core.Reload)) RebuildEngine(); }
    }

    private void Run(string js)
    {
        var core = CurrentCore();
        if (core == null || !_engineReady) return;
        Wake();
        _ = RunAsync(core, js);
    }

    private static async Task RunAsync(CoreWebView2 core, string js)
    {
        try { await core.ExecuteScriptAsync(js); }
        catch { /* the page is gone or the browser with it: a script for it means nothing now */ }
    }

    private async Task<string?> EvaluateAsync(string js)
    {
        var core = CurrentCore();
        if (core == null || !_engineReady) return null;
        Wake();
        try { return await core.ExecuteScriptAsync(js); }
        catch { return null; }
    }

    /// <summary>The file watcher: a file the page shows changed on disk.</summary>
    private void OnReloadRequested(bool cssOnly)
    {
        var core = CurrentCore();
        if (core == null || !_engineReady) return;   // (the first navigation loads fresh files anyway)
        Wake();
        if (cssOnly) { Run("window.__cx && __cx.reloadCss(); window.__cx && setTimeout(() => __cx.refreshSelection(), 120)"); return; }
        Reload(hard: false, keepSelection: true);
    }

    // ───────────────────────────── page loading ─────────────────────────────

    private void OnLoadCommand(PageLoadCommand c)
    {
        var core = CurrentCore();
        switch (c.Kind)
        {
            case PageLoadCommandKind.Navigate when c.Url != null:
                GoCore(c.Url);
                break;
            case PageLoadCommandKind.Reload when core != null:
                ReloadCore(core, hard: false);
                break;
            case PageLoadCommandKind.InjectOverlay:
                Run(StudioTemplates.ReadAsset("overlay.js"));   // (it guards itself: a page that has it keeps the one it has)
                break;
            case PageLoadCommandKind.RequestTree:
                // verified: the tree once more (one sent before the page counted as this project's was dropped), and
                // the "ready" work if that message never reached us
                if (!_readyHandled) OnOverlayReady();
                _studio?.RequestTree();
                break;
            case PageLoadCommandKind.Probe:
                _ = ProbeAsync(c.Token);
                break;
        }
    }

    private async Task ProbeAsync(long token)
    {
        var core = CurrentCore();
        string? json = null;
        if (core != null && _engineReady)
        {
            Wake();
            try { json = await core.ExecuteScriptAsync(PageProbe.Script); }
            catch { }
        }
        Safe(() => _load.Probed(token, PageProbe.Parse(json), Now));
        ArmLoadTimer();
    }

    /// <summary>One timer for the tracker: armed for its next due time, stopped while nothing is due.</summary>
    private void ArmLoadTimer()
    {
        if (_load.NextDue is not { } due) { _loadTimer?.Stop(); return; }
        if (_loadTimer == null)
        {
            _loadTimer = new System.Windows.Threading.DispatcherTimer();
            _loadTimer.Tick += (_, _) =>
            {
                _loadTimer!.Stop();
                Safe(() => _load.Tick(Now));
                ArmLoadTimer();
            };
        }
        var wait = due - Now;
        _loadTimer.Stop();
        _loadTimer.Interval = wait > TimeSpan.FromMilliseconds(10) ? wait : TimeSpan.FromMilliseconds(10);
        _loadTimer.Start();
    }

    // ───────────────────────────── overlay messages ─────────────────────────────

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!ReferenceEquals(sender, CurrentCore()) || e.Source == StudioService.BlankPage) return;   // (a browser since replaced)
        string json;
        try { json = e.WebMessageAsJson; }
        catch { return; }
        var msg = OverlayMessage.Parse(json);
        if (msg == null) return;
        // the overlay of the document being loaded is up (the tracker itself knows whether it is the current document)
        if (msg.Type == "ready") Safe(() => _load.OverlayReady(Now));
        // (the last project's page, still talking while the browser leaves it: its locators mean another project's files)
        if (_vm == null || _studio == null || _studio.PageIsOld) return;

        switch (msg.Type)
        {
            case "ready":
                OnOverlayReady();
                break;
            case "select":
            case "deselect":
            case "tree":
                _studio.HandleOverlayMessage(msg);
                // the page changed by itself: a build error Vite's hot update fixed is no longer reported
                if (msg.Type == "tree") { Safe(() => _load.PageChanged(Now)); ArmLoadTimer(); }
                break;
            case "text" or "resize" or "command" when _vm.IsLockedByAi:
                // (the overlay takes no edit while locked; a message already on its way when the lock came is refused here)
                _vm.Status(Loc("studio.err.aiWorking"), error: true);
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

    /// <summary>The overlay of the page shown now is up — its "ready" message, or a verified load whose message never
    /// reached us: the page follows the Studio's mode, and the element picked before a reload is picked again. The one
    /// place the "ready" work is done.</summary>
    private void OnOverlayReady()
    {
        _readyHandled = true;
        if (_vm == null) return;
        _vm.NotePage(SafeSource());
        if (!_vm.IsSelectMode) Run("__cx.setMode('interact')");
        // every load starts the overlay unlocked: an AI run editing the project locks the new page too
        if (_vm.IsLockedByAi) Run("__cx.setLocked && __cx.setLocked(true)");
        if (_reselectSrc != null || _reselectSelector != null)
        {
            // the same element again after a reload: by its source location, else by its CSS path
            Run($"__cx.selectBySrc({JsonSerializer.Serialize(_reselectSrc ?? "")}) || __cx.selectBySelector({JsonSerializer.Serialize(_reselectSelector ?? "")})");
            _reselectSrc = _reselectSelector = null;
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
