using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CluadeX.Services.UiStudio;
using CluadeX.ViewModels;
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
    private StudioService? _studio;
    private Task? _init;
    private string? _pendingUrl;
    private string? _reselectSrc;
    private string? _reselectSelector;
    private readonly List<(System.ComponentModel.INotifyPropertyChanged Source, System.ComponentModel.PropertyChangedEventHandler Handler)> _overlayWatch = new();
    private bool _covered;

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
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) _vm?.PageShown(); };
        Loaded += (_, _) => { Attach(DataContext as UiStudioViewModel); _init ??= InitWebAsync(); };
        Loaded += (_, _) => WatchOverlays();
        Unloaded += (_, _) => UnwatchOverlays();
        ((System.Collections.Specialized.INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, _) =>
        {
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }

    private void Attach(UiStudioViewModel? vm)
    {
        if (vm == null || ReferenceEquals(vm, _vm)) return;
        _vm = vm;
        _studio = vm.Studio;
        _studio.NavigateRequested += Navigate;
        _studio.ScriptRequested += Run;
        _studio.ReloadRequested += OnReloadRequested;
        _studio.Evaluate = EvaluateAsync;
        vm.NavigateTo += Navigate;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UiStudioViewModel.HasProject) or nameof(UiStudioViewModel.NoProject) or nameof(UiStudioViewModel.ShowGallery))
                UpdateWebVisibility();
        };
        UpdateWebVisibility();
        if (IsVisible) vm.PageShown();
    }

    // ───────────────────────────── what may cover the page ─────────────────────────────

    /// <summary>The page shows only when nothing WPF draws should be on top of it.</summary>
    private void UpdateWebVisibility()
    {
        bool show = _vm is { HasProject: true, ShowGallery: false } && !_covered;
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
            else if (_studio.PageUrl != null) core.Navigate(_studio.PageUrl);
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
        try { Web.CoreWebView2.Navigate(url); }
        catch (ArgumentException) { _vm?.Status(Loc("studio.err.badUrl"), error: true); }
    }

    private void Run(string js)
    {
        var core = Web.CoreWebView2;
        if (core == null) return;
        _ = core.ExecuteScriptAsync(js);
    }

    private async Task<string?> EvaluateAsync(string js)
    {
        var core = Web.CoreWebView2;
        if (core == null) return null;
        try { return await core.ExecuteScriptAsync(js); }
        catch { return null; }
    }

    private void OnReloadRequested(bool cssOnly)
    {
        var core = Web.CoreWebView2;
        if (core == null) return;
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
