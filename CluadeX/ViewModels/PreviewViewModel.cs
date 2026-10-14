using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CluadeX.Services;
using CluadeX.Services.Browser;
using CluadeX.Services.Tools;

namespace CluadeX.ViewModels;

/// <summary>One line of the pane's mini console.</summary>
public sealed class PreviewConsoleLine
{
    public string Text { get; init; } = "";
    public bool IsError { get; init; }
    public bool IsWarning { get; init; }
}

/// <summary>
/// State of the Preview pane(s): the address bar with its LOCAL / EXTERNAL badge, the Desktop / Mobile chip, the
/// "agent is driving" light, the console mini-panel and the empty / engine-missing / locked states. One instance
/// for the app — the PREVIEW split in the workbench and the pop-out window show the same page.
/// Refreshes on a 250 ms UI timer that runs only while a pane is on screen.
/// </summary>
public sealed class PreviewViewModel : ViewModelBase
{
    /// <summary>The app's instance (the pane is created by XAML and finds it here).</summary>
    public static PreviewViewModel? Current { get; private set; }

    private readonly BrowserService _browser;
    private readonly SettingsService _settings;
    private readonly ActivationService _activation;
    private readonly FileSystemService _fileSystem;
    private readonly LocalizationService _loc;
    private DispatcherTimer? _timer;
    private int _visiblePanes;
    private volatile bool _dirty = true;
    private long _consoleVersion = -1;

    public PreviewViewModel(BrowserService browser, SettingsService settings, ActivationService activation,
        FileSystemService fileSystem, LocalizationService loc)
    {
        _browser = browser;
        _settings = settings;
        _activation = activation;
        _fileSystem = fileSystem;
        _loc = loc;
        _browser.StateChanged += () => _dirty = true;
        _browser.NoticeRaised += OnNotice;
        // A language switch relabels everything, the console summary included.
        _loc.LanguageChanged += () => { _copiedAt = DateTime.MinValue; _consoleVersion = -1; _dirty = true; };

        NavigateCommand = new AsyncRelayCommand(NavigateAsync);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => HasPage);
        ToggleViewportCommand = new AsyncRelayCommand(ToggleViewportAsync, () => HasPage);
        CopyInstallCommand = new RelayCommand(CopyInstall);
        CheckEngineCommand = new RelayCommand(CheckEngine);
        ClosePageCommand = new AsyncRelayCommand(ClosePageAsync, () => HasPage);
        ToggleConsoleCommand = new RelayCommand(() => IsConsoleOpen = !IsConsoleOpen);
        ClearConsoleCommand = new RelayCommand(() => { _browser.Session?.Console.Clear(); _dirty = true; });
        PopOutCommand = new RelayCommand(() => PopOut?.Invoke());
        Current = this;
    }

    public BrowserService Browser => _browser;

    /// <summary>Set by the presenter: open (or raise) the pop-out window and move the page into it.</summary>
    public Action? PopOut { get; set; }

    public ICommand NavigateCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand ToggleViewportCommand { get; }
    public ICommand CopyInstallCommand { get; }
    public ICommand CheckEngineCommand { get; }
    public ICommand ClosePageCommand { get; }
    public ICommand ToggleConsoleCommand { get; }
    public ICommand ClearConsoleCommand { get; }
    public ICommand PopOutCommand { get; }

    // ─────────────────────────── workbench split ───────────────────────────

    private bool _isDockOpen;
    /// <summary>The PREVIEW split of the code workbench is open. Kept here (one instance for the app), not in the
    /// workbench view: MainWindow rebuilds that view on every visit to the Code page, and the split must stay open
    /// across page switches — including the agent's own switch to the editor — or the next browser_open would go to
    /// the pop-out window instead.</summary>
    public bool IsDockOpen { get => _isDockOpen; set => SetProperty(ref _isDockOpen, value); }

    private double _dockWidth = 520;
    /// <summary>Width of the PREVIEW split (the grip changes it), kept across page switches.</summary>
    public double DockWidth { get => _dockWidth; set => SetProperty(ref _dockWidth, Math.Clamp(value, 320, 1600)); }

    public ObservableCollection<PreviewConsoleLine> ConsoleLines { get; } = new();

    private string _address = "";
    /// <summary>What the user types; replaced by the page's URL when it changes (unless the box has focus).</summary>
    public string Address { get => _address; set => SetProperty(ref _address, value); }
    public bool AddressHasFocus { get; set; }

    private string _currentUrl = "";
    public string CurrentUrl { get => _currentUrl; private set => SetProperty(ref _currentUrl, value); }

    private string _badge = "";
    public string Badge { get => _badge; private set => SetProperty(ref _badge, value); }
    private bool _isExternal;
    public bool IsExternal { get => _isExternal; private set => SetProperty(ref _isExternal, value); }

    private bool _hasPage;
    public bool HasPage { get => _hasPage; private set { if (SetProperty(ref _hasPage, value)) { OnPropertyChanged(nameof(ShowEmpty)); CommandManager.InvalidateRequerySuggested(); } } }

    private bool _isAgentDriving;
    public bool IsAgentDriving { get => _isAgentDriving; private set => SetProperty(ref _isAgentDriving, value); }

    private string _viewportLabel = "";
    public string ViewportLabel { get => _viewportLabel; private set => SetProperty(ref _viewportLabel, value); }

    private bool _isEngineMissing;
    public bool IsEngineMissing { get => _isEngineMissing; private set { if (SetProperty(ref _isEngineMissing, value)) OnPropertyChanged(nameof(ShowEmpty)); } }
    public string InstallCommand => BrowserEngineProbe.InstallCommand;

    private string? _lockedReason;
    /// <summary>Why Dev Preview cannot be used (license / switched off), or null.</summary>
    public string? LockedReason { get => _lockedReason; private set { if (SetProperty(ref _lockedReason, value)) { OnPropertyChanged(nameof(IsLocked)); OnPropertyChanged(nameof(ShowEmpty)); } } }
    public bool IsLocked => _lockedReason != null;

    /// <summary>The "no page yet" hint (not when a stronger message is shown).</summary>
    public bool ShowEmpty => !HasPage && !IsEngineMissing && !IsLocked;

    private bool _isCrashed;
    public bool IsCrashed { get => _isCrashed; private set => SetProperty(ref _isCrashed, value); }

    private string _status = "";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    // Starts folded: a page's errors filled half the preview. The bar still shows the error / warning counts.
    private bool _isConsoleOpen = false;
    public bool IsConsoleOpen { get => _isConsoleOpen; set => SetProperty(ref _isConsoleOpen, value); }

    private string _consoleSummary = "";
    public string ConsoleSummary { get => _consoleSummary; private set => SetProperty(ref _consoleSummary, value); }

    private string _copyLabel = "";
    public string CopyLabel { get => _copyLabel; private set => SetProperty(ref _copyLabel, value); }

    // ─────────────────────────── pane lifetime ───────────────────────────

    /// <summary>A pane became visible: start refreshing (UI thread).</summary>
    public void PaneShown()
    {
        _visiblePanes++;
        _dirty = true;
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => { if (_dirty) Refresh(); };
        }
        _timer.Start();
        Refresh();
    }

    /// <summary>A pane went away: stop the timer when none is left (UI thread).</summary>
    public void PaneHidden()
    {
        _visiblePanes = Math.Max(0, _visiblePanes - 1);
        if (_visiblePanes == 0) _timer?.Stop();
    }

    /// <summary>Raised after every <see cref="Refresh"/> (UI thread) — panes re-check which of them shows the page.</summary>
    public event Action? Refreshed;

    /// <summary>Re-read everything from the browser (UI thread).</summary>
    public void Refresh()
    {
        try { RefreshCore(); }
        finally { try { Refreshed?.Invoke(); } catch { } }
    }

    private void RefreshCore()
    {
        _dirty = false;
        LockedReason = DevPreviewGate.Refusal(_settings, _activation, _loc.T("feature.devPreview"));
        IsEngineMissing = !BrowserEngineProbe.Probe().HasWebView2;
        // Follows the language, and "Copied" goes back to "Copy command" after a few seconds.
        bool recentlyCopied = DateTime.UtcNow - _copiedAt < CopiedFor;
        CopyLabel = _loc.T(recentlyCopied ? "preview.copied" : "preview.copyCommand");
        if (recentlyCopied) _dirty = true;   // come back once the moment has passed

        // Switched off or locked: the page and its engine stop too (a game would otherwise keep running at full
        // speed off-screen until CluadeX exits).
        if (LockedReason != null && _browser.Session != null && !_closingForLock)
        {
            _closingForLock = true;
            _ = CloseForLockAsync();
        }

        if (_notice is { } note)
        {
            _notice = null;
            Status = note;
        }

        var session = _browser.Session;
        HasPage = session != null;
        IsAgentDriving = _browser.IsAgentDriving;
        if (session == null)
        {
            CurrentUrl = "";
            Badge = "";
            IsExternal = false;
            ViewportLabel = _loc.T("preview.desktop");
            IsCrashed = false;
            if (ConsoleLines.Count > 0) ConsoleLines.Clear();
            ConsoleSummary = "";
            _consoleVersion = -1;
            return;
        }

        string url = session.CurrentUrl;
        if (url != CurrentUrl)
        {
            CurrentUrl = url;
            if (!AddressHasFocus) Address = url == "about:blank" ? "" : url;
        }
        var v = UrlPolicy.Classify(url, ProjectRoot);
        IsExternal = v.Kind == UrlKind.External;
        Badge = v.Kind switch
        {
            UrlKind.External => _loc.T("preview.external"),
            UrlKind.ProjectFile => _loc.T("preview.projectFile"),
            _ => url == "about:blank" ? "" : _loc.T("preview.local"),
        };
        ViewportLabel = session.Viewport switch
        {
            "mobile" => _loc.T("preview.mobile") + " 375×812",
            "tablet" => _loc.T("preview.tablet") + " 768×1024",
            "custom" => _loc.T("preview.custom"),
            _ => _loc.T("preview.desktop"),
        };
        IsCrashed = session.IsCrashed;
        if (IsCrashed) Status = _loc.T("preview.crashed");

        long version = session.Console.Version;
        if (version != _consoleVersion)
        {
            _consoleVersion = version;
            var tail = session.Console.Tail(60);
            ConsoleLines.Clear();
            foreach (var e in tail)
                ConsoleLines.Add(new PreviewConsoleLine { Text = e.Format(), IsError = e.IsError, IsWarning = e.IsWarning });
            var (errors, warnings, _) = session.Console.CountSinceNavigation(0);
            ConsoleSummary = string.Format(_loc.T("preview.consoleCounts"), errors, warnings);
        }
    }

    private string? ProjectRoot => _fileSystem.HasWorkingDirectory ? _fileSystem.WorkingDirectory : null;

    // ─────────────────────────── user actions ───────────────────────────

    /// <summary>The user pressed Enter in the address bar. The user may open any site they type (it is their
    /// browser — "example.com" gets https://); the agent's later reads and actions on a non-local site still ask
    /// once per site in each chat.</summary>
    private async Task NavigateAsync()
    {
        Status = "";
        string raw = (Address ?? "").Trim();
        if (raw.Length == 0) return;
        if (LockedReason is { } locked) { Status = locked; return; }
        var v = UrlPolicy.Classify(UrlPolicy.NormalizeTyped(raw), ProjectRoot);
        if (v.Kind == UrlKind.Refused)
        {
            Status = string.Format(_loc.T(v.RefusalKey ?? "browser.url.invalid"), v.RefusalArg ?? raw);
            return;
        }
        if (v.Kind == UrlKind.External && _browser.ExternalMode == "deny")
        {
            Status = string.Format(_loc.T("preview.externalDenied"), v.Origin);
            return;
        }
        if (!AddressHasFocus) Address = v.Url;
        await RunAsync(async () =>
        {
            Status = _loc.T("preview.starting");
            var session = await _browser.GetSessionAsync(CancellationToken.None);
            session.ProjectRoot = ProjectRoot;
            if (v.Kind == UrlKind.External) _browser.ApproveByUser(v.Origin);
            var r = await session.NavigateAsync(v.Url, "load", 30_000, false, CancellationToken.None);
            Status = r.Ok ? "" : string.Format(_loc.T("preview.openFailed"), r.Error);
        });
    }

    private async Task ReloadAsync()
    {
        var session = _browser.Session;
        if (session == null) return;
        string url = session.CurrentUrl;
        if (string.IsNullOrEmpty(url) || url == "about:blank") return;
        await RunAsync(async () =>
        {
            Status = "";
            session.ProjectRoot = ProjectRoot;
            // The user reloading the page they are looking at lets THIS load through (after a chat switch the page
            // may belong to a site only the previous chat had approved) — and approves the site for nobody: not for
            // every chat, not for the chat driving the browser now.
            var r = await session.ReloadByUserAsync(30_000, CancellationToken.None);
            if (r is { Ok: false }) Status = string.Format(_loc.T("preview.openFailed"), r.Error);
        });
    }

    /// <summary>Desktop ↔ Mobile, then reload — pages (and canvas games) size themselves at load.</summary>
    private async Task ToggleViewportAsync()
    {
        var session = _browser.Session;
        if (session == null) return;
        await RunAsync(async () =>
        {
            await session.ResizeAsync(session.Viewport == "desktop" ? "mobile" : "desktop", null, null, null, null, CancellationToken.None);
            await ReloadAsync();
        });
    }

    /// <summary>The Close button: close the page and stop the engine (its memory and GPU are freed).</summary>
    private Task ClosePageAsync() => RunAsync(async () =>
    {
        Status = "";
        await _browser.CloseSessionAsync(TimeSpan.FromSeconds(3));
    });

    private async Task CloseForLockAsync()
    {
        try { await _browser.CloseSessionAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[preview] close on lock failed: {ex.Message}"); }
        finally { _closingForLock = false; _dirty = true; }
    }

    /// <summary>Run a pane action; whatever goes wrong becomes one localized status line (never raw exception text —
    /// the details go to the Debug Log).</summary>
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (BrowserUnavailableException ex) { Status = ex.Message; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[preview] action failed: {ex}");
            Status = StatusFor(ex);
        }
        _dirty = true;
    }

    /// <summary>The localized status line for a failed pane action.</summary>
    internal string StatusFor(Exception ex) => ex switch
    {
        ObjectDisposedException or OperationCanceledException => _loc.T("preview.err.closed"),
        TimeoutException => _loc.T("preview.err.timeout"),
        _ => _loc.T("preview.err.engine"),
    };

    private void CopyInstall()
    {
        try
        {
            Clipboard.SetText(BrowserEngineProbe.InstallCommand);
            _copiedAt = DateTime.UtcNow;
            CopyLabel = _loc.T("preview.copied");
        }
        catch { /* clipboard busy — the command is on screen to select by hand */ }
        BrowserEngineProbe.Probe(refresh: true);
        _dirty = true;
    }

    /// <summary>"Check again" next to the install command: probe the engine now (the user just installed it).</summary>
    private void CheckEngine()
    {
        BrowserEngineProbe.Probe(refresh: true);
        Refresh();
    }

    private static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(3);
    private DateTime _copiedAt = DateTime.MinValue;
    private bool _closingForLock;
    private volatile string? _notice;

    /// <summary>Something the page did on its own that the user should see in the status line (a pop-up the
    /// browser did not open). Engine thread — only stored here; the UI refresh shows it.</summary>
    private void OnNotice(BrowserNotice n)
    {
        if (n.Kind == BrowserNoticeKind.PopupBlocked && !string.IsNullOrEmpty(n.Url))
        {
            _notice = string.Format(_loc.T("preview.popupBlocked"), n.Url);
            _dirty = true;
        }
    }
}
