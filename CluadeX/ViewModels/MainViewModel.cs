using System.Windows.Input;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Mcp;

namespace CluadeX.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly AgentToolService _agentToolService;
    private readonly GpuDetectionService _gpuDetectionService;
    private readonly AiProviderManager _providerManager;
    private readonly Services.Pet.PetService _pet;
    private readonly LocalizationService _loc;
    private readonly AutoUpdateService _autoUpdate;
    private readonly ActivationService? _activation;
    private readonly XmanLicenseService? _license;
    private Views.LicenseWindow? _licenseWindow;
    private DispatcherTimer? _licenseTimer;
    private readonly DgxSparkService? _spark;
    private Views.DgxSparkWindow? _sparkWindow;
    private Views.BrainXInviteWindow? _brainWindow;
    private DispatcherTimer? _brainInviteTimer;
    private readonly CodeWorkspaceService? _workspace;
    private readonly DebugLogService? _log;

    /// <summary>The questions before an update restart were answered and the restart is under way: the app closes
    /// without a Closing guard that can cancel (Application.Shutdown ignores Cancel), so MainWindow.OnClosing must not
    /// ask again — it only saves (<see cref="SaveForUpdateRestart"/>).</summary>
    public bool IsRestartingForUpdate { get; private set; }

    /// <summary>Editor tabs that were dirty when the restart questions were answered — the user chose to discard
    /// them. A tab edited after the answer is not in here and is saved on the way out.</summary>
    private HashSet<OpenFileTab>? _updateAnsweredDirtyTabs;

    private ViewModelBase? _currentView;
    private string _selectedNavItem = "Chat";
    private string _modelStatus = "No model loaded";
    private string _gpuStatus = "Detecting GPU...";
    private bool _isModelLoaded;
    private bool _isModelLoading;

    // ─── Live GPU monitoring (nvidia-smi poll) ───
    // Refreshed every ~2s by _gpuLiveTimer; drives the mini sparkline + temp display
    // in the sidebar status bar. All state here is UI-thread-only.
    private DispatcherTimer? _gpuLiveTimer;
    private readonly System.Collections.Generic.Queue<int> _gpuUsageHistory = new();
    private const int GpuHistoryMax = 24;   // samples retained for the sparkline
    private const double SparklineWidth = 100;
    private const double SparklineHeight = 18;

    private GpuLiveStats? _liveGpuStats;
    public GpuLiveStats? LiveGpuStats
    {
        get => _liveGpuStats;
        set
        {
            if (SetProperty(ref _liveGpuStats, value))
            {
                OnPropertyChanged(nameof(HasLiveGpuStats));
                OnPropertyChanged(nameof(ShowGpuStats));
                OnPropertyChanged(nameof(GpuTempDisplay));
                OnPropertyChanged(nameof(GpuUsageDisplay));
                OnPropertyChanged(nameof(GpuVramDisplay));
                OnPropertyChanged(nameof(GpuTempBrush));
                OnPropertyChanged(nameof(GpuSparklinePoints));
            }
        }
    }

    public bool HasLiveGpuStats => _liveGpuStats != null;
    /// <summary>The status-bar GPU readout, only while the model answering runs on this PC's GPU.</summary>
    public bool ShowGpuStats => _liveGpuStats != null && ModelRunsOnThisPc;

    /// <summary>LLamaSharp, llama-server, or an Ollama on this PC (loopback address, not the remembered DGX Spark).
    /// One rule for the GPU readout and the nvidia-smi poll — a local Ollama loads onto this GPU too.</summary>
    private bool ModelRunsOnThisPc => _settingsService?.Settings is { } s && DgxSparkService.ModelRunsOnThisPc(s);
    public string GpuTempDisplay => _liveGpuStats is null ? "" : $"{_liveGpuStats.TemperatureC}°C";
    public string GpuUsageDisplay => _liveGpuStats is null ? "" : $"{_liveGpuStats.GpuUtilization}%";
    public string GpuVramDisplay => _liveGpuStats is null ? "" : $"{_liveGpuStats.VramUsedGB:F1} / {_liveGpuStats.VramTotalGB:F1} GB";

    /// <summary>Temperature → warning color so the status bar telegraphs thermal issues at a glance.</summary>
    public System.Windows.Media.Brush GpuTempBrush
    {
        get
        {
            int t = _liveGpuStats?.TemperatureC ?? 0;
            // Theme tokens, live: the chip re-colours itself on a theme switch.
            if (t >= 85) return ThemeColors.Live("Status.Danger.Soft", System.Windows.Media.Color.FromRgb(0xF3, 0x8B, 0xA8));
            if (t >= 75) return ThemeColors.Live("Status.Caution.Soft", System.Windows.Media.Color.FromRgb(0xFA, 0xB3, 0x87));
            if (t >= 60) return ThemeColors.Live("Status.Warning.Soft", System.Windows.Media.Color.FromRgb(0xF9, 0xE2, 0xAF));
            return ThemeColors.Live("Status.Success.Soft", System.Windows.Media.Color.FromRgb(0xA6, 0xE3, 0xA1));
        }
    }

    /// <summary>Sparkline points for a Polyline. Mapped from the last N GPU utilization samples
    /// into a 100×18 area. Empty string when we have no samples yet.</summary>
    public System.Windows.Media.PointCollection GpuSparklinePoints
    {
        get
        {
            var points = new System.Windows.Media.PointCollection();
            if (_gpuUsageHistory.Count < 2) return points;

            var samples = _gpuUsageHistory.ToArray();
            int n = samples.Length;
            double stepX = n == 1 ? SparklineWidth : SparklineWidth / (n - 1);
            for (int i = 0; i < n; i++)
            {
                double x = i * stepX;
                // Invert Y so higher utilization is higher on screen.
                double y = SparklineHeight - (samples[i] / 100.0 * SparklineHeight);
                if (y < 0) y = 0;
                if (y > SparklineHeight) y = SparklineHeight;
                points.Add(new System.Windows.Point(x, y));
            }
            return points;
        }
    }

    public ViewModelBase? CurrentView { get => _currentView; set => SetProperty(ref _currentView, value); }
    public string SelectedNavItem { get => _selectedNavItem; set => SetProperty(ref _selectedNavItem, value); }

    // System menu collapse state — default collapsed so chat history dominates the sidebar.
    // Persisted via SettingsService so the user's choice survives restarts.
    private bool _isNavExpanded;
    public bool IsNavExpanded
    {
        get => _isNavExpanded;
        set
        {
            if (SetProperty(ref _isNavExpanded, value))
                _settingsService.UpdateSettings(s => s.SidebarNavExpanded = value);
        }
    }
    public ICommand ToggleNavCommand => new RelayCommand(() => IsNavExpanded = !IsNavExpanded);
    public string ModelStatus { get => _modelStatus; set => SetProperty(ref _modelStatus, value); }
    public string GpuStatus { get => _gpuStatus; set => SetProperty(ref _gpuStatus, value); }
    public bool IsModelLoaded { get => _isModelLoaded; set => SetProperty(ref _isModelLoaded, value); }
    public bool IsModelLoading { get => _isModelLoading; set => SetProperty(ref _isModelLoading, value); }

    // ─── Localized Nav Labels ───
    public string NavChat => _loc.T("nav.chat");
    public string NavModels => _loc.T("nav.models");
    public string NavSettings => _loc.T("nav.settings");
    public string NavPlugins => _loc.T("nav.plugins");
    public string NavPermissions => _loc.T("nav.permissions");
    public string NavTasks => _loc.T("nav.tasks");
    public string NavFeatures => _loc.T("nav.features");
    public string NavMcpServers => _loc.T("nav.mcpServers");
    public string TitleSubtitle => _loc.T("title.subtitle");

    private void RefreshNavLabels()
    {
        OnPropertyChanged(nameof(NavChat));
        OnPropertyChanged(nameof(NavModels));
        OnPropertyChanged(nameof(NavSettings));
        OnPropertyChanged(nameof(NavPlugins));
        OnPropertyChanged(nameof(NavPermissions));
        OnPropertyChanged(nameof(NavTasks));
        OnPropertyChanged(nameof(NavFeatures));
        OnPropertyChanged(nameof(NavMcpServers));
        OnPropertyChanged(nameof(TitleSubtitle));
    }

    // Buddy companion
    /// <summary>The desktop pet (title-bar chip).</summary>
    public Services.Pet.PetService Pet => _pet;
    public System.Windows.Media.Imaging.BitmapSource PetAvatar => _pet.Avatar;
    public string PetTitle => _pet.Title;
    public string PetSubtitle => _pet.Subtitle;
    public string PetDetails => _pet.Details;
    public bool IsBuddyEnabled => _settingsService.Settings.Features.BuddyCompanion;

    // ─── ObsidianX bridge (named-pipe MCP host) ───
    // Surfaces the in-process MCP server's status to the sidebar so the user
    // can see at a glance whether external orchestrators (ObsidianX Co-Pilot
    // Arena) can reach this CluadeX instance. The chip in the status bar
    // binds to this property.
    private McpHostService? _mcpHost;
    public McpHostService? McpHost
    {
        get => _mcpHost;
        set => SetProperty(ref _mcpHost, value);
    }

    // ─── BrainX link (outbound MCP client) ───
    // The mirror image of McpHost: that chip says "orchestrators can reach us",
    // this one says "we can reach the brain". Missing until 2026-08-04, which is
    // why a brainx-mcp that died 30s after startup was invisible for four minutes
    // while the agent kept calling brain_search into the corpse. Binds to
    // McpServerManager's observable BrainState/BrainStatusText.
    private McpServerManager? _mcpServers;
    public McpServerManager? McpServers
    {
        get => _mcpServers;
        set => SetProperty(ref _mcpServers, value);
    }

    // ─── Auto-Update ───
    private bool _showUpdateBar;
    private string _updateMessage = "";
    private double _updateProgress;
    private string _updateProgressText = "";
    private bool _isUpdating;
    private UpdateInfo? _pendingUpdate;
    public bool ShowUpdateBar { get => _showUpdateBar; set => SetProperty(ref _showUpdateBar, value); }
    public string UpdateMessage { get => _updateMessage; set => SetProperty(ref _updateMessage, value); }
    public double UpdateProgress { get => _updateProgress; set => SetProperty(ref _updateProgress, value); }
    public string UpdateProgressText { get => _updateProgressText; set => SetProperty(ref _updateProgressText, value); }
    public bool IsUpdating { get => _isUpdating; set => SetProperty(ref _isUpdating, value); }
    private bool _canInstallUpdate;
    /// <summary>The banner's Install button: only when a release was actually found (or staged).</summary>
    public bool CanInstallUpdate { get => _canInstallUpdate; set => SetProperty(ref _canInstallUpdate, value); }
    public string AppVersion => $"v{AutoUpdateService.CurrentVersion}";

    public ChatViewModel ChatVM { get; }
    public ModelManagerViewModel ModelManagerVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public PluginManagerViewModel PluginManagerVM { get; }
    public PermissionsViewModel PermissionsVM { get; }
    public TaskManagerViewModel TaskManagerVM { get; }
    public WatchViewModel? WatchVM { get; }
    public FeaturesViewModel FeaturesVM { get; }
    public McpServersViewModel McpServersVM { get; }
    public TimeMachineViewModel TimeMachineVM { get; }
    public CodeEditorViewModel CodeEditorVM { get; }
    public SubAgentsViewModel SubAgentsVM { get; }
    public SkillsViewModel SkillsVM { get; }
    public InstinctsViewModel InstinctsVM { get; }
    public DebugLogViewModel DebugLogVM { get; }
    public SecurityShieldViewModel SecurityShieldVM { get; }
    public HookLibraryViewModel HookLibraryVM { get; }
    public HexEditorViewModel HexEditorVM { get; }

    public ICommand NavigateToCommand { get; }

    // ── Command palette (Ctrl+K) ──
    public CommandPaletteViewModel Palette { get; private set; } = null!;
    public ICommand OpenPaletteCommand { get; private set; } = null!;
    public ICommand TogglePaletteCommand { get; private set; } = null!;
    public ICommand ClosePaletteCommand { get; private set; } = null!;
    public ICommand RunPaletteItemCommand { get; private set; } = null!;

    /// <summary>Everything the palette can jump to: pages, skills, and project files.</summary>
    private IEnumerable<PaletteItem> BuildPaletteItems()
    {
        var items = new List<PaletteItem>();

        // Pages — the same targets NavigateTo understands.
        (string Target, string Title, string Glyph)[] pages =
        {
            ("Chat", "Chat", ""), ("Code", "Code Editor", ""),
            ("Models", "Models", ""), ("TimeMachine", "Time Machine", ""),
            ("HexEditor", "Hex Editor", ""), ("Plugins", "Plugins", ""),
            ("McpServers", "MCP Servers", ""), ("Tasks", "Tasks", ""), ("Watch", "Watch", ""),
            ("Permissions", "Permissions", ""), ("Features", "Features", ""),
            ("SubAgents", "Subagents", ""), ("Skills", "Skills", ""),
            ("Instincts", "Instincts", ""), ("DebugLog", "Debug Log", ""),
            ("SecurityShield", "SecurityShield", ""), ("HookLibrary", "Hook Library", ""),
            ("Settings", "Settings", ""),
        };
        foreach (var (target, title, glyph) in pages)
        {
            string t = target;
            items.Add(new PaletteItem
            {
                Icon = glyph, Title = title, Subtitle = "Go to page", Kind = "Page",
                KindToken = "Accent.1",
                Haystack = (title + " page " + target).ToLowerInvariant(),
                Run = () => NavigateTo(t),
            });
        }

        // Core commands (new chat, open folder, clear, compact, updates, settings, license).
        items.AddRange(CommandPaletteViewModel.BuildCoreCommandItems(ChatVM, t => NavigateTo(t), CheckUpdateCommand, OpenLicenseCommand));

        if (_spark != null)
        {
            items.Add(new PaletteItem
            {
                Icon = "", Title = "DGX Spark", Subtitle = L("spark.paletteSubtitle"), Kind = "Page",
                KindToken = "Brand.Spark",
                Haystack = "dgx spark nvidia ollama gpu 128gb",
                Run = OpenDgxSpark,
            });
        }

        // Skills — running one drops its slash command into the chat box.
        try
        {
            foreach (var (name, desc) in _agentToolService.GetAvailableSkillNames())
            {
                string n = name;
                items.Add(new PaletteItem
                {
                    Icon = "", Title = "/" + name, Subtitle = desc, Kind = "Skill",
                    KindToken = "Accent.2",
                    Haystack = (name + " " + desc).ToLowerInvariant(),
                    Run = () =>
                    {
                        NavigateTo("Chat");
                        ChatVM.UserInput = "/" + n + " ";
                    },
                });
            }
        }
        catch { /* skills are optional */ }

        // Project files — open in the editor.
        try
        {
            items.AddRange(CommandPaletteViewModel.BuildFileItems(
                ChatVM.WorkingDirectory,
                path =>
                {
                    NavigateTo("Code");
                    _ = CodeEditorVM.OpenPathAsync(path);
                }));
        }
        catch { /* no project open */ }

        return items;
    }
    public ICommand PetBuddyCommand { get; }
    public ICommand InstallUpdateCommand { get; }
    public ICommand DismissUpdateCommand { get; }
    public ICommand CheckUpdateCommand { get; }
    public ICommand OpenLicenseCommand { get; }
    public ICommand OpenDgxSparkCommand { get; }
    /// <summary>The status-bar brain chip: the MCP page while a configured brain is up or coming up (Ready, Starting,
    /// Reconnecting), the BrainX window when there is none to manage (not installed / not configured / stopped / failed).</summary>
    public ICommand OpenBrainCommand { get; }

    /// <summary>The title-bar DGX Spark chip glows solid while CluadeX is running on a Spark.</summary>
    public bool IsSparkActive => _spark?.IsActive == true;

    // ── License chip (title bar): invites a key when there is none, counts a trial down, says Pro when bought ──
    private string _licenseChipText = "", _licenseChipTip = "", _licenseChipState = "none";
    public string LicenseChipText { get => _licenseChipText; private set => SetProperty(ref _licenseChipText, value); }
    public string LicenseChipTip { get => _licenseChipTip; private set => SetProperty(ref _licenseChipTip, value); }
    /// <summary>none | trial | active — picks the chip's look.</summary>
    public string LicenseChipState { get => _licenseChipState; private set => SetProperty(ref _licenseChipState, value); }

    public MainViewModel(
        ChatViewModel chatVM,
        ModelManagerViewModel modelManagerVM,
        SettingsViewModel settingsVM,
        PluginManagerViewModel pluginManagerVM,
        PermissionsViewModel permissionsVM,
        TaskManagerViewModel taskManagerVM,
        FeaturesViewModel featuresVM,
        McpServersViewModel mcpServersVM,
        TimeMachineViewModel timeMachineVM,
        HexEditorViewModel hexEditorVM,
        CodeEditorViewModel codeEditorVM,
        SubAgentsViewModel subAgentsVM,
        SkillsViewModel skillsVM,
        InstinctsViewModel instinctsVM,
        DebugLogViewModel debugLogVM,
        SecurityShieldViewModel securityShieldVM,
        HookLibraryViewModel hookLibraryVM,
        AgentToolService agentToolService,
        SettingsService settingsService,
        GpuDetectionService gpuDetectionService,
        AiProviderManager providerManager,
        Services.Pet.PetService pet,
        LocalizationService loc,
        AutoUpdateService autoUpdate,
        ActivationService? activation = null,
        XmanLicenseService? license = null,
        DgxSparkService? spark = null,
        CodeWorkspaceService? workspace = null,
        DebugLogService? log = null,
        WatchViewModel? watchVM = null)
    {
        _autoUpdate = autoUpdate;
        _activation = activation;
        _license = license;
        _spark = spark;
        _workspace = workspace;
        _log = log;
        ChatVM = chatVM;
#if DEBUG
        if (Environment.GetEnvironmentVariable("CLUADEX_PERF_PROBE") == "1")
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                async () => { await Task.Delay(8000); await chatVM.RunPerfProbeAsync(NavigateTo); });
#endif
        ModelManagerVM = modelManagerVM;
        SettingsVM = settingsVM;
        PluginManagerVM = pluginManagerVM;
        PermissionsVM = permissionsVM;
        TaskManagerVM = taskManagerVM;
        WatchVM = watchVM;
        FeaturesVM = featuresVM;
        McpServersVM = mcpServersVM;
        TimeMachineVM = timeMachineVM;
        HexEditorVM = hexEditorVM;
        CodeEditorVM = codeEditorVM;
        SubAgentsVM = subAgentsVM;
        SkillsVM = skillsVM;
        InstinctsVM = instinctsVM;
        DebugLogVM = debugLogVM;
        SecurityShieldVM = securityShieldVM;
        HookLibraryVM = hookLibraryVM;

        // When the AI agent invokes hex_open, switch the active page to the
        // Hex Editor so the user can see the file the agent is working on.
        _agentToolService = agentToolService;
        agentToolService.OnHexEditorRequested += _ =>
            App.Current?.Dispatcher.Invoke(() => NavigateTo("HexEditor"));
        _settingsService = settingsService;
        _gpuDetectionService = gpuDetectionService;
        _providerManager = providerManager;
        _pet = pet;
        _loc = loc;
        _isNavExpanded = settingsService.Settings.SidebarNavExpanded;

        CurrentView = chatVM;

        // Auto-navigate to Chat when user clicks a session from another page
        chatVM.NavigateToChatRequested += () => NavigateTo("Chat");

        // Opening/cloning a project lands the user in the workbench (Code page) — the IDE moment.
        chatVM.NavigateToEditorRequested += () => NavigateTo("Code");

        // Cowork follow: the agent just mutated a file — bring the Code page forward so the
        // live edit is visible (the editor embeds the same chat docked right, so the
        // conversation stays on screen). Raised on the UI thread.
        //
        // This used to also require ReferenceEquals(CurrentView, ChatVM), i.e. it only
        // followed when the user happened to be sitting on the Chat page. Anywhere else —
        // Home, Models, Tasks, Settings, or a session driven from BrainX over the MCP host
        // pipe, where the user never opened Chat at all — the agent wrote files and the UI
        // just sat there. AutoOpenEditorOnAgentEdit is already the user's switch for this
        // behaviour; gating it a second time on the current page made the setting a lie.
        // The chat's project panel: a double-clicked file opens in the code editor.
        chatVM.OpenFileRequested += path =>
        {
            NavigateTo("Code");
            _ = CodeEditorVM.OpenPathAsync(path);
        };
        chatVM.FileMutatedByAgent += (_, _, _) =>
        {
            if (_settingsService.Settings.AutoOpenEditorOnAgentEdit)
                NavigateTo("Code");
        };

        NavigateToCommand = new RelayCommand<string>(NavigateTo);

        // Real Ctrl+K command palette. The title-bar pill used to be cosmetic — it advertised
        // "Search files, symbols & commands…" and just opened Settings.
        Palette = new CommandPaletteViewModel(BuildPaletteItems);
        OpenPaletteCommand = new RelayCommand(() => Palette.Open());
        TogglePaletteCommand = new RelayCommand(() => Palette.Toggle());
        ClosePaletteCommand = new RelayCommand(() => Palette.Close());
        RunPaletteItemCommand = new RelayCommand<PaletteItem>(item =>
        {
            if (item == null) return;
            Palette.Close();
            try { item.Run(); } catch (Exception ex) { CommandErrorSink.Report("CommandPalette", ex); }
        });
        PetBuddyCommand = new RelayCommand(() => _pet.Pat());
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdate);
        DismissUpdateCommand = new RelayCommand(() => ShowUpdateBar = false);
        CheckUpdateCommand = new AsyncRelayCommand(() => CheckForUpdate(manual: true));
        OpenLicenseCommand = new RelayCommand(() => OpenLicense(null));
        OpenDgxSparkCommand = new RelayCommand(OpenDgxSpark);
        OpenBrainCommand = new RelayCommand(() =>
        {
            // A configured brain that is up or on its way up (connecting, reconnecting) is managed on the MCP page;
            // the BrainX window is for getting one: not installed, not configured, stopped or failed.
            if (McpServers is { BrainServerName: not null } mcp
                && mcp.BrainState is McpServerState.Starting or McpServerState.Reconnecting or McpServerState.Ready)
                NavigateTo("McpServers");
            else OpenBrainXInvite();
        });

        // Wire up auto-update events
        _autoUpdate.OnDownloadProgress += (percent, status) =>
            App.Current?.Dispatcher.Invoke(() =>
            {
                UpdateProgress = percent;
                UpdateProgressText = status;
            });

        // Check for updates on startup (delayed 5 seconds)
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000);
            await CheckForUpdate(manual: false);
        });

        _providerManager.OnStatusChanged += status =>
            App.Current?.Dispatcher.Invoke(() => ModelStatus = status);

        _providerManager.OnLoadingChanged += loading =>
            App.Current?.Dispatcher.Invoke(() =>
            {
                IsModelLoading = loading;
                IsModelLoaded = _providerManager.ActiveProvider.IsReady;
            });

        _providerManager.OnProviderChanged += _ =>
            App.Current?.Dispatcher.Invoke(() =>
            {
                IsModelLoaded = _providerManager.ActiveProvider.IsReady;
                ModelStatus = _providerManager.ActiveProvider.StatusMessage;
                OnPropertyChanged(nameof(IsSparkActive));
                OnPropertyChanged(nameof(ShowGpuStats));
            });

        // Initialize localization from saved setting
        _loc.SetLanguage(_settingsService.Settings.Language);

        // Refresh all nav labels when language changes
        _loc.LanguageChanged += RefreshNavLabels;

        // Settings change: buddy visibility, and whether the model answering is on this PC / on the Spark — an
        // Ollama address edited in Settings moves it without any provider switch.
        _settingsService.SettingsChanged += () =>
            App.Current?.Dispatcher.BeginInvoke(() =>
            {
                OnPropertyChanged(nameof(IsBuddyEnabled));
                if (_settingsService.Settings.Features.BuddyCompanion) _pet.Start();
                _pet.ApplyVisibility();
                OnPropertyChanged(nameof(ShowGpuStats));
                OnPropertyChanged(nameof(IsSparkActive));
            });

        // Forward pet changes (hatching, progress, name) to the title-bar chip
        _pet.Changed += () => App.Current?.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(PetAvatar));
            OnPropertyChanged(nameof(PetTitle));
            OnPropertyChanged(nameof(PetSubtitle));
            OnPropertyChanged(nameof(PetDetails));
        });

        if (_activation != null)
        {
            _activation.ActivationChanged += () => App.Current?.Dispatcher.BeginInvoke(RefreshLicenseChip);
            _activation.Notice += notice => App.Current?.Dispatcher.BeginInvoke(() => OnLicenseNotice(notice));
            _loc.LanguageChanged += RefreshLicenseChip;
            RefreshLicenseChip();
            // A trial runs out while the app is open: the chip counts down and the gate closes on time.
            _licenseTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _licenseTimer.Tick += (_, _) => { _activation.CheckExpiry(); RefreshLicenseChip(); };
            _licenseTimer.Start();
        }

        Task.Run(DetectGpu);
        InitializeBuddy();
        StartGpuLivePolling();
    }

    /// <summary>Poll nvidia-smi every 2s on a background task, marshal back to the UI thread
    /// to update the sidebar live stats + rolling sparkline history.</summary>
    private int _gpuPollBusy;

    private void StartGpuLivePolling()
    {
        _gpuLiveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _gpuLiveTimer.Tick += (_, _) =>
        {
            // No model on this PC answering → nothing on screen shows the GPU; don't spawn nvidia-smi every 2 s for it.
            if (!ModelRunsOnThisPc) return;
            // Minimized → the sidebar chart is off screen. A poll still running (a slow driver) → don't stack another.
            if (App.Current?.MainWindow?.WindowState == System.Windows.WindowState.Minimized) return;
            if (Interlocked.Exchange(ref _gpuPollBusy, 1) == 1) return;
            // Keep the UI responsive: nvidia-smi can take ~200ms on first call.
            Task.Run(() =>
            {
                GpuLiveStats? stats;
                try { stats = _gpuDetectionService.GetLiveStats(); }
                finally { Interlocked.Exchange(ref _gpuPollBusy, 0); }
                App.Current?.Dispatcher.Invoke(() =>
                {
                    if (stats == null)
                    {
                        // Driver/nvidia-smi unavailable — keep previous state silently.
                        return;
                    }
                    _gpuUsageHistory.Enqueue(stats.GpuUtilization);
                    while (_gpuUsageHistory.Count > GpuHistoryMax) _gpuUsageHistory.Dequeue();
                    LiveGpuStats = stats;
                });
            });
        };
        _gpuLiveTimer.Start();
    }

    private void InitializeBuddy()
    {
        try
        {
            // Learning runs whenever the pet is enabled; ApplyVisibility puts it on (or takes it off) the desktop.
            if (_settingsService.Settings.Features.BuddyCompanion)
                _pet.Start();
        }
        catch { /* Buddy is non-critical */ }
    }

    private void DetectGpu()
    {
        try
        {
            var gpu = _gpuDetectionService.DetectGpu();
            App.Current?.Dispatcher.Invoke(() => GpuStatus = gpu.StatusDisplay);
        }
        catch
        {
            App.Current?.Dispatcher.Invoke(() => GpuStatus = "GPU detection failed");
        }
    }

    private string L(string key) => _loc.T(key);

    private void RefreshLicenseChip()
    {
        if (_activation == null) return;
        if (_activation.IsTrial)
        {
            string left = LicenseViewModel.TimeLeft(_activation.ExpiresAtUtc, _loc);
            LicenseChipState = "trial";
            LicenseChipText = string.Format(L("license.chip.trial"), left);
            LicenseChipTip = string.Format(L("license.chip.tipTrial"), LicenseViewModel.LocalTime(_activation.ExpiresAtUtc));
        }
        else if (_activation.IsActivated)
        {
            LicenseChipState = "active";
            LicenseChipText = _activation.TierDisplayName;
            LicenseChipTip = string.Format(L("license.chip.tipActive"), _activation.TierDisplayName);
        }
        else
        {
            LicenseChipState = "none";
            LicenseChipText = L("license.chip.none");
            LicenseChipTip = L("license.chip.tipNone");
        }
    }

    private void OnLicenseNotice(LicenseNotice notice)
    {
        RefreshLicenseChip();
        if (_activation == null) return;
        switch (notice)
        {
            case LicenseNotice.TrialStarted:
                OpenLicense(string.Format(L("license.banner.trialStarted"), _activation.MaskedKey ?? "",
                    LicenseViewModel.TimeLeft(_activation.ExpiresAtUtc, _loc)));
                break;
            case LicenseNotice.TrialEnded:
                OpenLicense(L("license.banner.trialEnded"));
                break;
            case LicenseNotice.Restored:
                // Nothing to ask — the chip already shows the license is back.
                break;
        }
    }

    /// <summary>Open the license dialog (one at a time; a second call brings it forward with the new banner).</summary>
    public void OpenLicense(string? banner)
    {
        if (_activation == null || _license == null) return;
        if (_licenseWindow != null)
        {
            if (!string.IsNullOrEmpty(banner))
            {
                _licenseWindow.Close();
            }
            else
            {
                _licenseWindow.Activate();
                return;
            }
        }

        var vm = new LicenseViewModel(_activation, _license, _loc, banner);
        var win = new Views.LicenseWindow(vm);
        var owner = App.Current?.MainWindow;
        if (owner != null && owner.IsLoaded && owner != win) win.Owner = owner;
        else win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
        win.Closed += (_, _) => { if (_licenseWindow == win) _licenseWindow = null; };
        _licenseWindow = win;
        win.Show();
        win.Activate();
    }

    /// <summary>Open the BrainX invitation (one at a time) — the user asked for it (the brain chip), so it takes focus.</summary>
    public void OpenBrainXInvite() => OpenBrainXInvite(activate: true);

    /// <param name="activate">False for the scheduled first-launch invitation: it appears without taking the
    /// keyboard, so text the user is typing in the chat box keeps going there.</param>
    private void OpenBrainXInvite(bool activate)
    {
        // Opened by any path → the scheduled invitation is spent: it opens by itself at most once.
        _brainInviteSpent = true;
        StopBrainInviteTimer();

        if (McpServers is not { } mcp) return;
        if (_brainWindow != null)
        {
            if (activate) _brainWindow.Activate();
            return;
        }

        var win = new Views.BrainXInviteWindow(new BrainXInviteViewModel(mcp, _settingsService, _loc));
        var owner = App.Current?.MainWindow;
        if (owner != null && owner.IsLoaded && owner != win) win.Owner = owner;
        else win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
        win.Closed += (_, _) => { if (_brainWindow == win) _brainWindow = null; };
        _brainWindow = win;
        win.ShowActivated = activate;
        win.Show();
        if (activate) win.Activate();
    }

    /// <summary>The automatic invitation already opened (or the user opened it first).</summary>
    private bool _brainInviteSpent;

    private void StopBrainInviteTimer()
    {
        _brainInviteTimer?.Stop();
        _brainInviteTimer = null;
    }

    /// <summary>
    /// First launch without BrainX: invite once the app has settled — never on top of another dialog (the license
    /// window opens by itself when the free trial is issued). Gives up quietly after ~5 minutes of other dialogs.
    /// Opens without taking focus (see <see cref="OpenBrainXInvite(bool)"/>), and at most once.
    /// </summary>
    public void ScheduleBrainXInvite()
    {
        if (_brainInviteTimer != null || _brainInviteSpent) return;
        int tries = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _brainInviteTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_brainInviteTimer, timer)) { timer.Stop(); return; }   // stopped by an open meanwhile
            tries++;
            bool otherDialog = _licenseWindow != null || _sparkWindow != null
                || (App.Current?.Windows.OfType<System.Windows.Window>().Any(w => w != App.Current.MainWindow && w.IsVisible) ?? false);
            if (otherDialog && tries < 20) return;
            StopBrainInviteTimer();
            if (otherDialog || _brainInviteSpent || _settingsService.Settings.BrainXInviteDismissed
                || McpServers?.GetBrainInstallState() != Services.Mcp.McpServerManager.BrainInstall.NotInstalled) return;
            OpenBrainXInvite(activate: false);
        };
        timer.Start();
    }

    /// <summary>Ask before a close that would stop DGX Spark downloads in progress. True when nothing is downloading
    /// or the user agreed to stop them.</summary>
    public bool ConfirmCloseSparkWindow() => _sparkWindow?.ConfirmClose() ?? true;

    /// <summary>Open the DGX Spark window (one at a time).</summary>
    public void OpenDgxSpark()
    {
        if (_spark == null || IsRestartingForUpdate) return;   // not a new window while the app is going down for an update
        if (_sparkWindow != null)
        {
            _sparkWindow.Activate();
            return;
        }

        var win = new Views.DgxSparkWindow(new DgxSparkViewModel(_spark, _settingsService, _loc));
        var owner = App.Current?.MainWindow;
        if (owner != null && owner.IsLoaded && owner != win) win.Owner = owner;
        else win.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
        win.Closed += (_, _) =>
        {
            if (_sparkWindow == win) _sparkWindow = null;
            OnPropertyChanged(nameof(IsSparkActive));
        };
        _sparkWindow = win;
        win.Show();
        win.Activate();
    }

    private void NavigateTo(string? target)
    {
        if (target == null) return;
        CluadeX.Helpers.UiStallWatchdog.Note("opening page " + target);
        var navWatch = System.Diagnostics.Stopwatch.StartNew();
        SelectedNavItem = target;
        CurrentView = target switch
        {
            "Chat" => ChatVM,
            "Models" => ModelManagerVM,
            "Settings" => SettingsVM,
            "Plugins" => PluginManagerVM,
            "Permissions" => PermissionsVM,
            "Tasks" => TaskManagerVM,
            "Watch" => (ViewModelBase?)WatchVM ?? ChatVM,
            "Features" => FeaturesVM,
            "McpServers" => McpServersVM,
            "TimeMachine" => TimeMachineVM,
            "HexEditor" => HexEditorVM,
            "Code" => CodeEditorVM,
            "SubAgents" => SubAgentsVM,
            "Skills" => SkillsVM,
            "Instincts" => InstinctsVM,
            "DebugLog" => DebugLogVM,
            "SecurityShield" => SecurityShieldVM,
            "HookLibrary" => HookLibraryVM,
            _ => ChatVM,
        };

        // Sync model list/selection when navigating to Chat (picks up changes from Models tab)
        if (target == "Chat")
            ChatVM.SyncModelSelectionFromSettings();
        // How long until the page is drawn (Loaded priority runs after layout + render of the new page).
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            _log?.Info("Perf", $"page {target} shown in {navWatch.ElapsedMilliseconds} ms");
            CluadeX.Helpers.UiStallWatchdog.Note("");
        });
    }

    // ─── Auto-Update ───

    /// <param name="manual">Clicked by the user (bell / Settings). A manual check always answers — "you're up
    /// to date" included — so the button never looks dead; the silent startup check only speaks up when
    /// there is something to install.</param>
    private async Task CheckForUpdate(bool manual)
    {
        string L(string key) => _loc.T(key);
        // The splash already checked (and installed, if there was one) this launch.
        if (!manual && Views.SplashWindow.StartupUpdateHandled) return;
        if (manual)
            App.Current?.Dispatcher.Invoke(() =>
            {
                UpdateMessage = L("update.checking");
                CanInstallUpdate = false;
                ShowUpdateBar = true;
            });
        try
        {
            // Whatever we are running now is the verdict on the last apply attempt.
            // Must happen before any check so a package that already landed stops
            // being counted as a failure.
            _autoUpdate.NoteRunningVersion();

            // A paused updater must SAY it is paused. Silence here reads as "you are
            // up to date", which is the one message that is certainly wrong.
            string? paused = _autoUpdate.PausedReason;
            if (paused != null)
            {
                App.Current?.Dispatcher.Invoke(() =>
                {
                    UpdateMessage = "⚠ " + paused;
                    CanInstallUpdate = false;
                    ShowUpdateBar = true;
                    IsUpdating = false;
                });
            }

            // Preferred path: a real Velopack install self-updates atomically.
            string? staged = await _autoUpdate.VelopackCheckAndStageAsync();
            if (staged != null)
            {
                App.Current?.Dispatcher.Invoke(() =>
                {
                    UpdateMessage = "🔔 " + string.Format(L("update.staged"), staged);
                    CanInstallUpdate = true;
                    ShowUpdateBar = true;
                    IsUpdating = false;
                });
                return;
            }

            // Fallback for portable / dev builds, which Velopack cannot update: still
            // tell the user a newer release exists so they can install it by hand.
            var info = await _autoUpdate.CheckForUpdateAsync();
            if (info != null)
            {
                _pendingUpdate = info;
                App.Current?.Dispatcher.Invoke(() =>
                {
                    UpdateMessage = _autoUpdate.CanSelfUpdate
                        ? $"🔔 Update available: v{info.NewVersion} (current: v{info.CurrentVersion}) — {info.FileSizeDisplay}"
                        : $"🔔 v{info.NewVersion} is out (running v{info.CurrentVersion}) — this is a portable build, install via Setup.exe once to enable self-update.";
                    CanInstallUpdate = true;
                    ShowUpdateBar = true;
                    IsUpdating = false;
                });
            }
            else if (manual && paused == null)
            {
                App.Current?.Dispatcher.Invoke(() =>
                {
                    UpdateMessage = string.Format(L("update.upToDate"), AutoUpdateService.CurrentVersion)
                        + (_autoUpdate.CanSelfUpdate ? "" : " " + L("update.devBuildNote"));
                    CanInstallUpdate = false;
                    ShowUpdateBar = true;
                });
            }
        }
        catch
        {
            // The startup probe stays quiet; a check the user asked for must say it failed.
            if (manual)
                App.Current?.Dispatcher.Invoke(() =>
                {
                    UpdateMessage = L("update.checkFailed");
                    CanInstallUpdate = false;
                    ShowUpdateBar = true;
                });
        }
    }

    /// <summary>
    /// The update restarts the app without a window Closing event (Velopack: Environment.Exit; the legacy path:
    /// Application.Shutdown, which ignores Cancel), so MainWindow.OnClosing's guard never runs. Same questions here,
    /// first: unsaved editor tabs (Save all / Discard / Cancel) and DGX Spark downloads in progress; then the chat
    /// is saved. False = the user cancelled — nothing is installed.
    /// </summary>
    private bool ConfirmRestartForUpdate()
    {
        if (!CodeEditorVM.ConfirmCloseAll()) return false;
        if (!ConfirmCloseSparkWindow()) return false;
        try { ChatVM.SaveNow(); }
        catch (Exception ex) { CommandErrorSink.Report("InstallUpdate.SaveChat", ex); }
        return true;
    }

    /// <summary>The questions are answered and the restart starts now: from here MainWindow.OnClosing and the DGX
    /// Spark window close without asking (the app goes down through Application.Shutdown, which ignores Cancel, or
    /// Environment.Exit). Tabs dirty at this moment were answered "Discard"; any other dirty tab on the way out was
    /// edited after the answer and is saved.</summary>
    private void BeginRestartForUpdate()
    {
        _updateAnsweredDirtyTabs = CodeEditorVM.Tabs.Where(t => t.IsDirty).ToHashSet();
        IsRestartingForUpdate = true;
        _sparkWindow?.SetClosingForRestart(true);
    }

    /// <summary>The update did not restart the app (download failed, apply/install failed, a question was cancelled):
    /// the answers were for a close that didn't happen — the next close asks again.</summary>
    private void EndRestartForUpdate()
    {
        IsRestartingForUpdate = false;
        _updateAnsweredDirtyTabs = null;
        _sparkWindow?.ResetCloseConfirmation();
    }

    /// <summary>
    /// The app is closing for an update restart (MainWindow.OnClosing while <see cref="IsRestartingForUpdate"/>, and
    /// right before a Velopack apply, which exits without any Closing event). Nothing is asked: save the chat, and
    /// save the editor tabs edited after the questions were answered — a tab the user chose to discard stays discarded.
    /// A file that changed on disk meanwhile is not overwritten.
    /// </summary>
    public void SaveForUpdateRestart()
    {
        try { ChatVM.SaveNow(); }
        catch (Exception ex) { CommandErrorSink.Report("UpdateRestart.SaveChat", ex); }
        if (_workspace == null) return;
        var answered = _updateAnsweredDirtyTabs ?? [];
        foreach (var tab in CodeEditorVM.Tabs.Where(t => t.IsDirty && !answered.Contains(t)).ToList())
        {
            try
            {
                var r = _workspace.SaveTabChecked(tab, overwriteExternalChanges: false);
                if (r.Outcome != SaveOutcome.Saved)
                    _log?.Warn("Update", $"restart: {tab.FullPath} was edited after the update questions but not saved ({r.Outcome}) {r.Error}");
            }
            catch (Exception ex) { _log?.Warn("Update", $"restart: saving {tab.FullPath} failed", ex); }
        }
    }

    private async Task InstallUpdate()
    {
        if (IsUpdating) return;
        if (!_autoUpdate.HasStagedUpdate && _pendingUpdate == null) return;   // nothing to install — nothing to ask

        if (!ConfirmRestartForUpdate())
        {
            EndRestartForUpdate();
            IsUpdating = false;
            return;
        }

        // Velopack first: when a package is already staged this is an atomic swap
        // plus a restart, not a download. The call does not return.
        if (_autoUpdate.HasStagedUpdate)
        {
            IsUpdating = true;
            UpdateMessage = string.Format(L("update.applying"), _autoUpdate.StagedVersion);
            UpdateProgress = 100;
            BeginRestartForUpdate();
            await Task.Delay(600);
            SaveForUpdateRestart();   // Velopack exits without a Closing event — a tab edited in these 600 ms is saved here
            if (!_autoUpdate.VelopackApplyAndRestart())
            {
                EndRestartForUpdate();
                UpdateMessage = L("update.applyFailed");
                IsUpdating = false;
            }
            return;
        }

        if (_pendingUpdate == null) return;

        // Tabs still dirty now were answered "Discard". The download below takes a while — a tab edited during it
        // gets asked about again before the restart.
        var answeredDirty = CodeEditorVM.Tabs.Where(t => t.IsDirty).ToHashSet();
        string messageBefore = UpdateMessage;

        IsUpdating = true;
        UpdateMessage = L("update.downloading");
        UpdateProgress = 0;

        string? zipPath = await _autoUpdate.DownloadUpdateAsync(_pendingUpdate);
        if (zipPath == null)
        {
            EndRestartForUpdate();
            UpdateMessage = L("update.downloadFailed");
            IsUpdating = false;
            return;
        }

        // A download started on the Spark during the update's download isn't covered by the first answer — asked here.
        bool editedMeanwhile = CodeEditorVM.Tabs.Any(t => t.IsDirty && !answeredDirty.Contains(t));
        if ((editedMeanwhile && !CodeEditorVM.ConfirmCloseAll()) || !ConfirmCloseSparkWindow())
        {
            EndRestartForUpdate();
            UpdateMessage = messageBefore;
            UpdateProgress = 0;
            IsUpdating = false;
            return;
        }
        try { ChatVM.SaveNow(); }
        catch (Exception ex) { CommandErrorSink.Report("InstallUpdate.SaveChat", ex); }

        BeginRestartForUpdate();
        UpdateMessage = L("update.installing");
        UpdateProgress = 100;

        // Small delay to show the message
        await Task.Delay(1000);

        // Extracts, starts the installer script and shuts the app down (MainWindow.OnClosing only saves now).
        if (!_autoUpdate.InstallAndRestart(zipPath))
        {
            EndRestartForUpdate();
            UpdateMessage = L("update.installFailed");
            UpdateProgress = 0;
            IsUpdating = false;
        }
    }
}
