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

    public ICommand ToggleFeatureCommand { get; }

    public FeaturesViewModel(SettingsService settingsService, LocalizationService loc)
    {
        _settingsService = settingsService;
        _loc = loc;

        ToggleFeatureCommand = new RelayCommand<FeatureItem>(ToggleFeature);

        _loc.LanguageChanged += RefreshLabels;

        BuildFeatureList();
        RefreshLabels();
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

            if (item.RequiresApiKey)
                item.StatusLabel = _loc.T("features.requiresKey");
            else if (!item.IsToggleable)
                item.StatusLabel = _loc.T("features.free");
            else
                item.StatusLabel = item.IsEnabled ? _loc.T("features.enabled") : _loc.T("features.disabled");
        }
    }

    private void ToggleFeature(FeatureItem? item)
    {
        if (item == null || !item.IsToggleable) return;

        item.IsEnabled = !item.IsEnabled;
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
