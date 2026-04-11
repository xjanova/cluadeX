using System.Collections.ObjectModel;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

public class FeatureItem : ViewModelBase
{
    public string Key { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Category { get; set; } = "";
    public bool RequiresApiKey { get; set; }
    public string? RequiredProvider { get; set; }
    public bool IsToggleable { get; set; } = true;

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    private bool _isLocked;
    public bool IsLocked
    {
        get => _isLocked;
        set { SetProperty(ref _isLocked, value); OnPropertyChanged(nameof(CanToggle)); }
    }

    /// <summary>Can this feature be toggled? Must be toggleable AND not locked by activation.</summary>
    public bool CanToggle => IsToggleable && !IsLocked;

    private bool _isUnlocked = true;
    public bool IsUnlocked
    {
        get => _isUnlocked;
        set => SetProperty(ref _isUnlocked, value);
    }

    // Populated by ViewModel from LocalizationService
    private string _name = "";
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    private string _description = "";
    public string Description { get => _description; set => SetProperty(ref _description, value); }

    private string _statusLabel = "";
    public string StatusLabel { get => _statusLabel; set => SetProperty(ref _statusLabel, value); }
}

public class FeaturesViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _loc;
    private readonly ActivationService _activation;

    public ObservableCollection<FeatureItem> CoreFeatures { get; } = new();
    public ObservableCollection<FeatureItem> AdvancedFeatures { get; } = new();
    public ObservableCollection<FeatureItem> ToolFeatures { get; } = new();
    public ObservableCollection<FeatureItem> SecurityFeatures { get; } = new();
    public ObservableCollection<FeatureItem> FunFeatures { get; } = new();

    // Localized labels
    private string _pageTitle = "";
    public string PageTitle { get => _pageTitle; set => SetProperty(ref _pageTitle, value); }
    private string _pageSubtitle = "";
    public string PageSubtitle { get => _pageSubtitle; set => SetProperty(ref _pageSubtitle, value); }
    private string _coreLabel = "";
    public string CoreLabel { get => _coreLabel; set => SetProperty(ref _coreLabel, value); }
    private string _coreDesc = "";
    public string CoreDesc { get => _coreDesc; set => SetProperty(ref _coreDesc, value); }
    private string _advancedLabel = "";
    public string AdvancedLabel { get => _advancedLabel; set => SetProperty(ref _advancedLabel, value); }
    private string _advancedDesc = "";
    public string AdvancedDesc { get => _advancedDesc; set => SetProperty(ref _advancedDesc, value); }
    private string _toolsLabel = "";
    public string ToolsLabel { get => _toolsLabel; set => SetProperty(ref _toolsLabel, value); }
    private string _toolsDesc = "";
    public string ToolsDesc { get => _toolsDesc; set => SetProperty(ref _toolsDesc, value); }
    private string _securityLabel = "";
    public string SecurityLabel { get => _securityLabel; set => SetProperty(ref _securityLabel, value); }
    private string _securityDesc = "";
    public string SecurityDesc { get => _securityDesc; set => SetProperty(ref _securityDesc, value); }
    private string _funLabel = "";
    public string FunLabel { get => _funLabel; set => SetProperty(ref _funLabel, value); }
    private string _funDesc = "";
    public string FunDesc { get => _funDesc; set => SetProperty(ref _funDesc, value); }

    // Activation
    private bool _isActivated;
    public bool IsActivated { get => _isActivated; set => SetProperty(ref _isActivated, value); }
    private string _activationTier = "Free";
    public string ActivationTier { get => _activationTier; set => SetProperty(ref _activationTier, value); }
    private string _activationKeyInput = "";
    public string ActivationKeyInput { get => _activationKeyInput; set => SetProperty(ref _activationKeyInput, value); }
    private string _activationMessage = "";
    public string ActivationMessage { get => _activationMessage; set => SetProperty(ref _activationMessage, value); }
    private bool _activationSuccess;
    public bool ActivationSuccess { get => _activationSuccess; set => SetProperty(ref _activationSuccess, value); }
    private bool _showForceActivate;
    public bool ShowForceActivate { get => _showForceActivate; set => SetProperty(ref _showForceActivate, value); }
    private string _pendingKey = "";

    public ICommand ToggleFeatureCommand { get; }
    public ICommand ActivateCommand { get; }
    public ICommand DeactivateCommand { get; }
    public ICommand ForceActivateCommand { get; }

    public FeaturesViewModel(SettingsService settingsService, LocalizationService loc, ActivationService activation)
    {
        _settingsService = settingsService;
        _loc = loc;
        _activation = activation;

        ToggleFeatureCommand = new RelayCommand<FeatureItem>(ToggleFeature);
        ActivateCommand = new AsyncRelayCommand(DoActivateAsync);
        DeactivateCommand = new RelayCommand(DoDeactivate);
        ForceActivateCommand = new AsyncRelayCommand(DoForceActivateAsync);

        _loc.LanguageChanged += RefreshLabels;
        _activation.ActivationChanged += () =>
        {
            RefreshActivationState();
            RefreshLockStates();
        };

        _activation.DeviceConflictDetected += (key, msg) =>
        {
            _pendingKey = key;
            ShowForceActivate = true;
        };

        BuildFeatureList();
        RefreshLabels();
        RefreshActivationState();
    }

    private void RefreshActivationState()
    {
        IsActivated = _activation.IsActivated;
        ActivationTier = _activation.TierDisplayName;
    }

    private async Task DoActivateAsync()
    {
        ShowForceActivate = false;
        ActivationMessage = "Activating...";
        ActivationSuccess = false;

        var (success, message) = await _activation.ActivateAsync(ActivationKeyInput);
        ActivationMessage = message;
        ActivationSuccess = success;
        if (success) { ActivationKeyInput = ""; ShowForceActivate = false; }
    }

    private async Task DoForceActivateAsync()
    {
        var key = !string.IsNullOrEmpty(_pendingKey) ? _pendingKey : ActivationKeyInput;
        if (string.IsNullOrWhiteSpace(key)) return;

        ActivationMessage = "Transferring license...";
        ActivationSuccess = false;

        try
        {
            var (success, message) = await _activation.ActivateForceAsync(key);
            ActivationMessage = message;
            ActivationSuccess = success;
            ShowForceActivate = false;
            if (success) { ActivationKeyInput = ""; _pendingKey = ""; }
        }
        catch (Exception ex)
        {
            ActivationMessage = $"Transfer failed: {ex.Message}";
            ActivationSuccess = false;
        }
    }

    private void DoDeactivate()
    {
        _activation.Deactivate();
        ActivationMessage = _loc.CurrentLanguage == "th"
            ? "ปิดการใช้งานแล้ว กลับสู่โหมดฟรี"
            : "Deactivated. Returned to Free tier.";
        ActivationSuccess = false;
    }

    private void RefreshLockStates()
    {
        RefreshCollectionLocks(CoreFeatures);
        RefreshCollectionLocks(AdvancedFeatures);
        RefreshCollectionLocks(ToolFeatures);
        RefreshCollectionLocks(SecurityFeatures);
        RefreshCollectionLocks(FunFeatures);
    }

    private void RefreshCollectionLocks(ObservableCollection<FeatureItem> items)
    {
        foreach (var item in items)
        {
            bool isFreeFeature = IsFreeFeature(item.Key);
            bool unlocked = isFreeFeature || _activation.IsActivated;

            item.IsLocked = !unlocked;
            item.IsUnlocked = unlocked;
        }
    }

    /// <summary>Free features are never locked regardless of activation status.</summary>
    private static bool IsFreeFeature(string key) => key switch
    {
        "feature.localInference" or "feature.chatPersistence" or "feature.markdown"
        or "feature.gpuDetection" or "feature.darkTheme" or "feature.i18n"
        or "feature.buddy" or "feature.dpapi" or "feature.pathSafety"
        or "feature.noTelemetry" or "feature.codeExecution" or "feature.fileSystem"
        or "feature.ollama" => true,
        _ => false,
    };

    private void BuildFeatureList()
    {
        var ft = _settingsService.Settings.Features;

        // ─── Core Features ───
        CoreFeatures.Add(new FeatureItem
        {
            Key = "feature.localInference", Icon = "\U0001F4BB",
            Category = "core", IsToggleable = false, IsEnabled = true,
        });
        CoreFeatures.Add(new FeatureItem
        {
            Key = "feature.multiProvider", Icon = "\U0001F500",
            Category = "core", IsToggleable = false, IsEnabled = true,
        });
        CoreFeatures.Add(new FeatureItem
        {
            Key = "feature.chatPersistence", Icon = "\U0001F4BE",
            Category = "core", IsToggleable = false, IsEnabled = true,
        });
        CoreFeatures.Add(new FeatureItem
        {
            Key = "feature.markdown", Icon = "\U0001F4DD",
            Category = "core", IsToggleable = true, IsEnabled = ft.MarkdownRendering,
        });
        CoreFeatures.Add(new FeatureItem
        {
            Key = "feature.gpuDetection", Icon = "\U0001F3AE",
            Category = "core", IsToggleable = false, IsEnabled = true,
        });
        CoreFeatures.Add(new FeatureItem
        {
            Key = "feature.darkTheme", Icon = "\U0001F3A8",
            Category = "core", IsToggleable = false, IsEnabled = true,
        });
        CoreFeatures.Add(new FeatureItem
        {
            Key = "feature.huggingface", Icon = "\U0001F917",
            Category = "core", IsToggleable = false, IsEnabled = true,
        });
        CoreFeatures.Add(new FeatureItem
        {
            Key = "feature.i18n", Icon = "\U0001F30F",
            Category = "core", IsToggleable = false, IsEnabled = true,
        });

        // ─── Advanced (Requires API Key) ───
        AdvancedFeatures.Add(new FeatureItem
        {
            Key = "feature.openai", Icon = "\U0001F7E2",
            Category = "advanced", RequiresApiKey = true, RequiredProvider = "OpenAI",
            IsToggleable = false, IsEnabled = true,
        });
        AdvancedFeatures.Add(new FeatureItem
        {
            Key = "feature.anthropic", Icon = "\U0001F7E3",
            Category = "advanced", RequiresApiKey = true, RequiredProvider = "Anthropic",
            IsToggleable = false, IsEnabled = true,
        });
        AdvancedFeatures.Add(new FeatureItem
        {
            Key = "feature.gemini", Icon = "\U0001F535",
            Category = "advanced", RequiresApiKey = true, RequiredProvider = "Gemini",
            IsToggleable = false, IsEnabled = true,
        });
        AdvancedFeatures.Add(new FeatureItem
        {
            Key = "feature.ollama", Icon = "\U0001F999",
            Category = "advanced", RequiresApiKey = false,
            IsToggleable = false, IsEnabled = true,
        });

        // ─── Agent Tools ───
        ToolFeatures.Add(new FeatureItem
        {
            Key = "feature.codeExecution", Icon = "\u25B6\uFE0F",
            Category = "tools", IsToggleable = false, IsEnabled = true,
        });
        ToolFeatures.Add(new FeatureItem
        {
            Key = "feature.fileSystem", Icon = "\U0001F4C1",
            Category = "tools", IsToggleable = false, IsEnabled = true,
        });
        ToolFeatures.Add(new FeatureItem
        {
            Key = "feature.git", Icon = "\U0001F33F",
            Category = "tools", IsToggleable = true, IsEnabled = ft.GitIntegration,
        });
        ToolFeatures.Add(new FeatureItem
        {
            Key = "feature.github", Icon = "\U0001F419",
            Category = "tools", IsToggleable = true, IsEnabled = ft.GitHubIntegration,
        });
        ToolFeatures.Add(new FeatureItem
        {
            Key = "feature.webFetch", Icon = "\U0001F310",
            Category = "tools", IsToggleable = true, IsEnabled = ft.WebFetch,
        });
        ToolFeatures.Add(new FeatureItem
        {
            Key = "feature.contextMemory", Icon = "\U0001F9E0",
            Category = "tools", IsToggleable = true, IsEnabled = ft.ContextMemory,
        });
        ToolFeatures.Add(new FeatureItem
        {
            Key = "feature.smartEditing", Icon = "\u2728",
            Category = "tools", IsToggleable = true, IsEnabled = ft.SmartEditing,
        });

        // ─── Security ───
        SecurityFeatures.Add(new FeatureItem
        {
            Key = "feature.dpapi", Icon = "\U0001F510",
            Category = "security", IsToggleable = false, IsEnabled = true,
        });
        SecurityFeatures.Add(new FeatureItem
        {
            Key = "feature.permissions", Icon = "\U0001F6E1\uFE0F",
            Category = "security", IsToggleable = true, IsEnabled = ft.PermissionSystem,
        });
        SecurityFeatures.Add(new FeatureItem
        {
            Key = "feature.commandBlock", Icon = "\U0001F6AB",
            Category = "security", IsToggleable = true, IsEnabled = ft.DangerousCommandBlocking,
        });
        SecurityFeatures.Add(new FeatureItem
        {
            Key = "feature.pathSafety", Icon = "\U0001F512",
            Category = "security", IsToggleable = false, IsEnabled = true,
        });
        SecurityFeatures.Add(new FeatureItem
        {
            Key = "feature.noTelemetry", Icon = "\U0001F648",
            Category = "security", IsToggleable = false, IsEnabled = true,
        });

        // ─── Fun & Extras ───
        FunFeatures.Add(new FeatureItem
        {
            Key = "feature.buddy", Icon = "\U0001F43E",
            Category = "fun", IsToggleable = true, IsEnabled = ft.BuddyCompanion,
        });
        FunFeatures.Add(new FeatureItem
        {
            Key = "feature.plugins", Icon = "\U0001F9E9",
            Category = "fun", IsToggleable = true, IsEnabled = ft.PluginSystem,
        });
        FunFeatures.Add(new FeatureItem
        {
            Key = "feature.taskManager", Icon = "\U0001F4CB",
            Category = "fun", IsToggleable = true, IsEnabled = ft.TaskManager,
        });
    }

    private void RefreshLabels()
    {
        PageTitle = _loc.T("features.title");
        PageSubtitle = _loc.T("features.subtitle");
        CoreLabel = _loc.T("features.core");
        CoreDesc = _loc.T("features.coreDesc");
        AdvancedLabel = _loc.T("features.advanced");
        AdvancedDesc = _loc.T("features.advancedDesc");
        ToolsLabel = _loc.T("features.tools");
        ToolsDesc = _loc.T("features.toolsDesc");
        SecurityLabel = _loc.T("features.security");
        SecurityDesc = _loc.T("features.securityDesc");
        FunLabel = _loc.T("features.fun");
        FunDesc = _loc.T("features.funDesc");

        RefreshCollection(CoreFeatures);
        RefreshCollection(AdvancedFeatures);
        RefreshCollection(ToolFeatures);
        RefreshCollection(SecurityFeatures);
        RefreshCollection(FunFeatures);
    }

    private void RefreshCollection(ObservableCollection<FeatureItem> items)
    {
        foreach (var item in items)
        {
            item.Name = _loc.T(item.Key);
            item.Description = _loc.T(item.Key + ".desc");

            bool unlocked = _activation.IsFeatureUnlocked(item.Key);
            item.IsLocked = !unlocked;
            item.IsUnlocked = unlocked;

            if (!unlocked)
                item.StatusLabel = "\U0001F512 " + _loc.T("features.requiresKey");
            else if (item.RequiresApiKey)
                item.StatusLabel = _loc.T("features.requiresKey");
            else if (!item.IsToggleable)
                item.StatusLabel = _loc.T("features.free");
            else
                item.StatusLabel = item.IsEnabled ? _loc.T("features.enabled") : _loc.T("features.disabled");
        }
    }

    private void ToggleFeature(FeatureItem? item)
    {
        if (item == null || !item.CanToggle) return;

        // Don't toggle here — the CheckBox.IsChecked two-way binding already toggled IsEnabled.
        // We only need to update the status label and sync to settings.
        item.StatusLabel = item.IsEnabled ? _loc.T("features.enabled") : _loc.T("features.disabled");

        // Sync back to settings
        var ft = _settingsService.Settings.Features;
        switch (item.Key)
        {
            case "feature.buddy": ft.BuddyCompanion = item.IsEnabled; break;
            case "feature.plugins": ft.PluginSystem = item.IsEnabled; break;
            case "feature.taskManager": ft.TaskManager = item.IsEnabled; break;
            case "feature.webFetch": ft.WebFetch = item.IsEnabled; break;
            case "feature.git": ft.GitIntegration = item.IsEnabled; break;
            case "feature.github": ft.GitHubIntegration = item.IsEnabled; break;
            case "feature.contextMemory": ft.ContextMemory = item.IsEnabled; break;
            case "feature.smartEditing": ft.SmartEditing = item.IsEnabled; break;
            case "feature.markdown": ft.MarkdownRendering = item.IsEnabled; break;
            case "feature.permissions": ft.PermissionSystem = item.IsEnabled; break;
            case "feature.commandBlock": ft.DangerousCommandBlocking = item.IsEnabled; break;
        }

        _settingsService.Save();
    }
}
