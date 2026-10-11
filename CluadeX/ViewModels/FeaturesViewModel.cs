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
        set { if (SetProperty(ref _isEnabled, value)) OnPropertyChanged(nameof(CanToggle)); }
    }

    private bool _isLocked;
    public bool IsLocked
    {
        get => _isLocked;
        set { SetProperty(ref _isLocked, value); OnPropertyChanged(nameof(CanToggle)); }
    }

    /// <summary>
    /// Can this feature be toggled? A locked feature can still be switched OFF (never on) — before, a free user
    /// could not even turn off something that was already running.
    /// </summary>
    public bool CanToggle => IsToggleable && (!IsLocked || IsEnabled);

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

    /// <summary>An extra line under the description (Dev Preview: the browser engine found, or how to install it).</summary>
    private string _detail = "";
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }

    /// <summary>An optional setting shown under the description (Dev Preview: what to do with sites outside this
    /// PC — ask / allow / block). Empty = none.</summary>
    public ObservableCollection<FeatureChoice> Choices { get; } = new();
    public bool HasChoices => Choices.Count > 0;

    private string _choiceLabel = "";
    public string ChoiceLabel { get => _choiceLabel; set => SetProperty(ref _choiceLabel, value); }

    private string _choiceTip = "";
    public string ChoiceTip { get => _choiceTip; set => SetProperty(ref _choiceTip, value); }

    private string? _selectedChoice;
    /// <summary>The chosen <see cref="FeatureChoice.Value"/>; a change is saved through <see cref="ChoiceChanged"/>.</summary>
    public string? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (string.IsNullOrEmpty(value)) return;   // the list being relabelled never clears the setting
            if (SetProperty(ref _selectedChoice, value)) ChoiceChanged?.Invoke(value);
        }
    }

    public Action<string>? ChoiceChanged { get; set; }
}

/// <summary>One option of a <see cref="FeatureItem"/> setting (the label follows the language).</summary>
public class FeatureChoice : ViewModelBase
{
    public string Value { get; init; } = "";
    private string _label = "";
    public string Label { get => _label; set => SetProperty(ref _label, value); }
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

    /// <summary>
    /// Deactivate is offered for a PAID key only. During a trial it released the demo key on the server, and
    /// the user could not get it back (the key is shown masked, so they could not even re-type it).
    /// </summary>
    private bool _canDeactivate;
    public bool CanDeactivate { get => _canDeactivate; set => SetProperty(ref _canDeactivate, value); }

    /// <summary>
    /// Features the app ACTUALLY gates behind activation today (checked where the feature runs): the cloud
    /// providers (Settings ▸ Apply refuses them unactivated), and the agent's GitHub, web-fetch and background
    /// task tools (AgentToolService). Everything else is not enforced anywhere, so the page must not show a
    /// lock on it — HuggingFace, Plugins, Context memory, Smart editing, and above all the security toggles
    /// (permissions, dangerous-command blocking), which a free user must always be able to switch.
    /// ActivationService.IsFeatureUnlocked should be made to agree with this list.
    /// </summary>
    public static readonly HashSet<string> GatedFeatures = new(StringComparer.Ordinal)
    {
        "feature.openai", "feature.anthropic", "feature.gemini", "feature.multiProvider",
        "feature.github", "feature.webFetch", "feature.devPreview",
    };

    private bool IsUnlockedForDisplay(string key) => !GatedFeatures.Contains(key) || _activation.IsFeatureUnlocked(key);
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

    private readonly DebugLogService? _log;

    public FeaturesViewModel(SettingsService settingsService, LocalizationService loc, ActivationService activation,
        DebugLogService? log = null)
    {
        _settingsService = settingsService;
        _loc = loc;
        _activation = activation;
        _log = log;

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
        CanDeactivate = _activation.IsActivated && !_activation.IsTrial;
        ActivationTier = _activation.TierDisplayName;
    }

    private async Task DoActivateAsync()
    {
        ShowForceActivate = false;
        ActivationMessage = _loc.T("featuresm.activating");
        ActivationSuccess = false;

        try
        {
            var (success, message, code) = await _activation.ActivateAsync(ActivationKeyInput);
            // Offline: the message is "Connection failed: " + a raw exception text — say it in words instead.
            ActivationMessage = code == XmanLicenseService.ErrorOffline || string.IsNullOrWhiteSpace(message)
                ? _loc.T("license.offline")
                : message;
            ActivationSuccess = success;
            if (success) { ActivationKeyInput = ""; ShowForceActivate = false; }
        }
        catch (Exception ex)
        {
            _log?.Error("License", "Activation threw", ex);
            ActivationMessage = _loc.T("featuresm.activateFailed");
            ActivationSuccess = false;
        }
    }

    private async Task DoForceActivateAsync()
    {
        var key = !string.IsNullOrEmpty(_pendingKey) ? _pendingKey : ActivationKeyInput;
        if (string.IsNullOrWhiteSpace(key)) return;

        ActivationMessage = _loc.T("featuresm.transferring");
        ActivationSuccess = false;

        try
        {
            var (success, message) = await _activation.ActivateForceAsync(key);
            ActivationMessage = !success && (string.IsNullOrWhiteSpace(message) || message.StartsWith("Connection failed:", StringComparison.Ordinal))
                ? _loc.T("license.offline")
                : message;
            ActivationSuccess = success;
            ShowForceActivate = false;
            if (success) { ActivationKeyInput = ""; _pendingKey = ""; }
        }
        catch (Exception ex)
        {
            _log?.Error("License", "License transfer threw", ex);
            ActivationMessage = _loc.T("featuresm.transferFailed");
            ActivationSuccess = false;
        }
    }

    private void DoDeactivate()
    {
        if (!CanDeactivate) return;   // never release a trial key — see CanDeactivate
        // Destructive: Pro switches off on this PC — ask first.
        var answer = System.Windows.MessageBox.Show(_loc.T("license.deactivateConfirm"), "CluadeX",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        _activation.Deactivate();
        ActivationMessage = _loc.T("featuresm.deactivated");
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
            // ActivationService is the authority for what is gated, intersected with GatedFeatures (what is
            // actually enforced) so the page never shows a lock that nothing behind it honours.
            bool unlocked = IsUnlockedForDisplay(item.Key);

            item.IsLocked = !unlocked;
            item.IsUnlocked = unlocked;
        }
        RefreshCollection(items);
    }


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
        // Markdown is always rendered: the MarkdownRendering setting this toggle wrote is read by nothing, so it
        // is shown as a built-in instead of a switch that does nothing.
        CoreFeatures.Add(new FeatureItem
        {
            Key = "feature.markdown", Icon = "\U0001F4DD",
            Category = "core", IsToggleable = false, IsEnabled = true,
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
        // Dev Preview: background dev servers + the built-in browser the agent drives (Detail = engine probe;
        // the choice = what the built-in browser does with sites outside this PC).
        var devPreview = new FeatureItem
        {
            Key = "feature.devPreview", Icon = "\U0001F441\ufe0f",
            Category = "tools", IsToggleable = true, IsEnabled = ft.DevPreview,
        };
        foreach (var value in new[] { "ask", "allow", "deny" }) devPreview.Choices.Add(new FeatureChoice { Value = value });
        string mode = (_settingsService.Settings.BrowserAllowExternal ?? "ask").Trim().ToLowerInvariant();
        devPreview.SelectedChoice = mode is "allow" or "deny" ? mode : "ask";
        devPreview.ChoiceChanged = SaveBrowserExternalMode;
        ToolFeatures.Add(devPreview);

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

            bool unlocked = IsUnlockedForDisplay(item.Key);
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

            if (item.Key == "feature.devPreview")
            {
                item.Detail = DevPreviewEngineLine();
                item.ChoiceLabel = _loc.T("devPreview.externalLabel");
                item.ChoiceTip = _loc.T("devPreview.externalTip");
                foreach (var c in item.Choices) c.Label = _loc.T("devPreview.external." + c.Value);
            }
        }
    }

    /// <summary>Settings ▸ Features ▸ Dev Preview ▸ Sites outside this PC: "ask", "allow" or "deny".</summary>
    private void SaveBrowserExternalMode(string value)
    {
        if (value is not ("ask" or "allow" or "deny")) return;
        if (string.Equals(_settingsService.Settings.BrowserAllowExternal, value, StringComparison.OrdinalIgnoreCase)) return;
        _settingsService.Settings.BrowserAllowExternal = value;
        if (!_settingsService.Save())
        {
            _log?.Error("Features", $"Saving the external-sites choice failed: {_settingsService.LastSaveError}");
            ActivationSuccess = false;
            ActivationMessage = _loc.T("featuresm.saveFailed");
        }
    }

    /// <summary>"Engine: WebView2 154.0… · Edge … · Chrome …" or the command that installs the runtime.</summary>
    private string DevPreviewEngineLine()
    {
        try
        {
            var info = Services.Browser.BrowserEngineProbe.Probe();
            return info.HasWebView2
                ? string.Format(_loc.T("devPreview.engineFound"), info.Summary)
                : string.Format(_loc.T("devPreview.engineMissing"), Services.Browser.BrowserEngineProbe.InstallCommand);
        }
        catch (Exception ex)
        {
            _log?.Error("Features", "Browser engine probe failed", ex);
            return string.Format(_loc.T("devPreview.engineMissing"), Services.Browser.BrowserEngineProbe.InstallCommand);
        }
    }

    private void ToggleFeature(FeatureItem? item)
    {
        if (item == null || !item.IsToggleable) return;

        // Don't toggle here — the CheckBox.IsChecked two-way binding already toggled IsEnabled.
        // A locked feature may only be switched OFF: undo an attempt to switch it on.
        if (item.IsLocked && item.IsEnabled)
        {
            item.IsEnabled = false;
            return;
        }
        item.StatusLabel = item.IsLocked
            ? "\U0001F512 " + _loc.T("features.requiresKey")
            : item.IsEnabled ? _loc.T("features.enabled") : _loc.T("features.disabled");

        // Sync back to settings
        var ft = _settingsService.Settings.Features;
        switch (item.Key)
        {
            case "feature.buddy": ft.BuddyCompanion = item.IsEnabled; break;
            case "feature.plugins": ft.PluginSystem = item.IsEnabled; break;
            case "feature.devPreview": ft.DevPreview = item.IsEnabled; break;
            case "feature.webFetch": ft.WebFetch = item.IsEnabled; break;
            case "feature.git": ft.GitIntegration = item.IsEnabled; break;
            case "feature.github": ft.GitHubIntegration = item.IsEnabled; break;
            case "feature.contextMemory": ft.ContextMemory = item.IsEnabled; break;
            case "feature.smartEditing": ft.SmartEditing = item.IsEnabled; break;
            case "feature.permissions": ft.PermissionSystem = item.IsEnabled; break;
            case "feature.commandBlock": ft.DangerousCommandBlocking = item.IsEnabled; break;
        }

        if (!_settingsService.Save())
        {
            _log?.Error("Features", $"Saving the {item.Key} toggle failed: {_settingsService.LastSaveError}");
            ActivationSuccess = false;
            ActivationMessage = _loc.T("featuresm.saveFailed");
        }
    }
}
