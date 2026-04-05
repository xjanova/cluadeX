using System.Windows.Input;
using CluadeX.Services;

namespace CluadeX.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly GpuDetectionService _gpuDetectionService;
    private readonly AiProviderManager _providerManager;
    private readonly BuddyService _buddyService;
    private readonly LocalizationService _loc;
    private readonly AutoUpdateService _autoUpdate;

    private ViewModelBase? _currentView;
    private string _selectedNavItem = "Chat";
    private string _modelStatus = "No model loaded";
    private string _gpuStatus = "Detecting GPU...";
    private bool _isModelLoaded;
    private bool _isModelLoading;

    public ViewModelBase? CurrentView { get => _currentView; set => SetProperty(ref _currentView, value); }
    public string SelectedNavItem { get => _selectedNavItem; set => SetProperty(ref _selectedNavItem, value); }
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
    public BuddyService BuddyService => _buddyService;
    public bool IsBuddyEnabled => _settingsService.Settings.Features.BuddyCompanion;

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
    public string AppVersion => $"v{AutoUpdateService.CurrentVersion}";

    public ChatViewModel ChatVM { get; }
    public ModelManagerViewModel ModelManagerVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public PluginManagerViewModel PluginManagerVM { get; }
    public PermissionsViewModel PermissionsVM { get; }
    public TaskManagerViewModel TaskManagerVM { get; }
    public FeaturesViewModel FeaturesVM { get; }
    public McpServersViewModel McpServersVM { get; }

    public ICommand NavigateToCommand { get; }
    public ICommand PetBuddyCommand { get; }
    public ICommand InstallUpdateCommand { get; }
    public ICommand DismissUpdateCommand { get; }
    public ICommand CheckUpdateCommand { get; }

    public MainViewModel(
        ChatViewModel chatVM,
        ModelManagerViewModel modelManagerVM,
        SettingsViewModel settingsVM,
        PluginManagerViewModel pluginManagerVM,
        PermissionsViewModel permissionsVM,
        TaskManagerViewModel taskManagerVM,
        FeaturesViewModel featuresVM,
        McpServersViewModel mcpServersVM,
        SettingsService settingsService,
        GpuDetectionService gpuDetectionService,
        AiProviderManager providerManager,
        BuddyService buddyService,
        LocalizationService loc,
        AutoUpdateService autoUpdate)
    {
        _autoUpdate = autoUpdate;
        ChatVM = chatVM;
        ModelManagerVM = modelManagerVM;
        SettingsVM = settingsVM;
        PluginManagerVM = pluginManagerVM;
        PermissionsVM = permissionsVM;
        TaskManagerVM = taskManagerVM;
        FeaturesVM = featuresVM;
        McpServersVM = mcpServersVM;
        _settingsService = settingsService;
        _gpuDetectionService = gpuDetectionService;
        _providerManager = providerManager;
        _buddyService = buddyService;
        _loc = loc;

        CurrentView = chatVM;

        // Auto-navigate to Chat when user clicks a session from another page
        chatVM.NavigateToChatRequested += () => NavigateTo("Chat");

        NavigateToCommand = new RelayCommand<string>(NavigateTo);
        PetBuddyCommand = new RelayCommand(() => _buddyService.Pet());
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdate);
        DismissUpdateCommand = new RelayCommand(() => ShowUpdateBar = false);
        CheckUpdateCommand = new AsyncRelayCommand(CheckForUpdate);

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
            await CheckForUpdate();
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
            });

        // Initialize localization from saved setting
        _loc.SetLanguage(_settingsService.Settings.Language);

        // Refresh all nav labels when language changes
        _loc.LanguageChanged += RefreshNavLabels;

        // Refresh buddy visibility when settings change
        _settingsService.SettingsChanged += () =>
            App.Current?.Dispatcher.Invoke(() => OnPropertyChanged(nameof(IsBuddyEnabled)));

        // Forward buddy changes to UI
        _buddyService.BuddyChanged += () =>
            App.Current?.Dispatcher.Invoke(() => OnPropertyChanged(nameof(BuddyService)));

        Task.Run(DetectGpu);
        InitializeBuddy();
    }

    private void InitializeBuddy()
    {
        try
        {
            if (_settingsService.Settings.Features.BuddyCompanion)
                _buddyService.Initialize();
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

    private void NavigateTo(string? target)
    {
        if (target == null) return;
        SelectedNavItem = target;
        CurrentView = target switch
        {
            "Chat" => ChatVM,
            "Models" => ModelManagerVM,
            "Settings" => SettingsVM,
            "Plugins" => PluginManagerVM,
            "Permissions" => PermissionsVM,
            "Tasks" => TaskManagerVM,
            "Features" => FeaturesVM,
            "McpServers" => McpServersVM,
            _ => ChatVM,
        };

        // Sync model list/selection when navigating to Chat (picks up changes from Models tab)
        if (target == "Chat")
            ChatVM.SyncModelSelectionFromSettings();
    }

    // ─── Auto-Update ───

    private async Task CheckForUpdate()
    {
        try
        {
            var info = await _autoUpdate.CheckForUpdateAsync();
            if (info != null)
            {
                _pendingUpdate = info;
                App.Current?.Dispatcher.Invoke(() =>
                {
                    UpdateMessage = $"🔔 Update available: v{info.NewVersion} (current: v{info.CurrentVersion}) — {info.FileSizeDisplay}";
                    ShowUpdateBar = true;
                    IsUpdating = false;
                });
            }
        }
        catch { /* silently fail */ }
    }

    private async Task InstallUpdate()
    {
        if (_pendingUpdate == null || IsUpdating) return;

        IsUpdating = true;
        UpdateMessage = "Downloading update...";
        UpdateProgress = 0;

        string? zipPath = await _autoUpdate.DownloadUpdateAsync(_pendingUpdate);
        if (zipPath != null)
        {
            UpdateMessage = "Installing update... App will restart.";
            UpdateProgress = 100;

            // Small delay to show the message
            await Task.Delay(1000);

            _autoUpdate.InstallAndRestart(zipPath);
        }
        else
        {
            UpdateMessage = "Download failed. Try again later.";
            IsUpdating = false;
        }
    }
}
