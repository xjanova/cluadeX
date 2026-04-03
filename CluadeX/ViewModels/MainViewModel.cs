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
        OnPropertyChanged(nameof(TitleSubtitle));
    }

    // Buddy companion
    public BuddyService BuddyService => _buddyService;
    public bool IsBuddyEnabled => _settingsService.Settings.Features.BuddyCompanion;

    public ChatViewModel ChatVM { get; }
    public ModelManagerViewModel ModelManagerVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public PluginManagerViewModel PluginManagerVM { get; }
    public PermissionsViewModel PermissionsVM { get; }
    public TaskManagerViewModel TaskManagerVM { get; }
    public FeaturesViewModel FeaturesVM { get; }

    public ICommand NavigateToCommand { get; }
    public ICommand PetBuddyCommand { get; }

    public MainViewModel(
        ChatViewModel chatVM,
        ModelManagerViewModel modelManagerVM,
        SettingsViewModel settingsVM,
        PluginManagerViewModel pluginManagerVM,
        PermissionsViewModel permissionsVM,
        TaskManagerViewModel taskManagerVM,
        FeaturesViewModel featuresVM,
        SettingsService settingsService,
        GpuDetectionService gpuDetectionService,
        AiProviderManager providerManager,
        BuddyService buddyService,
        LocalizationService loc)
    {
        ChatVM = chatVM;
        ModelManagerVM = modelManagerVM;
        SettingsVM = settingsVM;
        PluginManagerVM = pluginManagerVM;
        PermissionsVM = permissionsVM;
        TaskManagerVM = taskManagerVM;
        FeaturesVM = featuresVM;
        _settingsService = settingsService;
        _gpuDetectionService = gpuDetectionService;
        _providerManager = providerManager;
        _buddyService = buddyService;
        _loc = loc;

        CurrentView = chatVM;

        NavigateToCommand = new RelayCommand<string>(NavigateTo);
        PetBuddyCommand = new RelayCommand(() => _buddyService.Pet());

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
            _ => ChatVM,
        };

        // Sync model list/selection when navigating to Chat (picks up changes from Models tab)
        if (target == "Chat")
            ChatVM.SyncModelSelectionFromSettings();
    }
}
