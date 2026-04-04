using System.Collections.ObjectModel;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;
using Microsoft.Win32;

namespace CluadeX.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly GpuDetectionService _gpuDetectionService;
    private readonly AiProviderManager _providerManager;
    private readonly LocalizationService _localizationService;

    private string _modelDirectory = string.Empty;
    private string _cacheDirectory = string.Empty;
    private string _logDirectory = string.Empty;
    private string _tempDirectory = string.Empty;
    private uint _contextSize;
    private int _gpuLayerCount;
    private float _temperature;
    private float _topP;
    private int _maxTokens;
    private float _repeatPenalty;
    private bool _autoExecuteCode;
    private int _maxAutoFixAttempts;
    private string _preferredLanguage = "C#";
    private double _fontSize;
    private bool _streamOutput;
    private string _huggingFaceToken = string.Empty;
    private string _gpuInfoDisplay = "Detecting...";
    private string _vramRecommendation = "";
    private string _saveStatus = "";
    private string _gpuBackend = "Auto";
    private int _batchSize = 512;
    private int _threadCount = 0;
    private string _uiLanguage = "English";

    // AI Provider fields
    private AiProviderType _selectedProvider;
    private string _providerApiKey = string.Empty;
    private string _providerBaseUrl = string.Empty;
    private string _providerModel = string.Empty;
    private string _providerCustomModel = string.Empty;
    private bool _useCustomModel;
    private string _connectionTestResult = string.Empty;
    private bool _isTestingConnection;

    public string ModelDirectory { get => _modelDirectory; set => SetProperty(ref _modelDirectory, value); }
    public string CacheDirectory { get => _cacheDirectory; set => SetProperty(ref _cacheDirectory, value); }
    public string LogDirectory { get => _logDirectory; set => SetProperty(ref _logDirectory, value); }
    public string TempDirectory { get => _tempDirectory; set => SetProperty(ref _tempDirectory, value); }
    public uint ContextSize { get => _contextSize; set => SetProperty(ref _contextSize, value); }
    public int GpuLayerCount { get => _gpuLayerCount; set => SetProperty(ref _gpuLayerCount, value); }
    public float Temperature { get => _temperature; set => SetProperty(ref _temperature, value); }
    public float TopP { get => _topP; set => SetProperty(ref _topP, value); }
    public int MaxTokens { get => _maxTokens; set => SetProperty(ref _maxTokens, value); }
    public float RepeatPenalty { get => _repeatPenalty; set => SetProperty(ref _repeatPenalty, value); }
    // Anthropic Advanced
    private bool _extendedThinkingEnabled;
    private int _thinkingBudgetTokens = 10000;
    private bool _promptCachingEnabled = true;
    public bool ExtendedThinkingEnabled { get => _extendedThinkingEnabled; set => SetProperty(ref _extendedThinkingEnabled, value); }
    public int ThinkingBudgetTokens { get => _thinkingBudgetTokens; set => SetProperty(ref _thinkingBudgetTokens, value); }
    public bool PromptCachingEnabled { get => _promptCachingEnabled; set => SetProperty(ref _promptCachingEnabled, value); }

    public bool AutoExecuteCode { get => _autoExecuteCode; set => SetProperty(ref _autoExecuteCode, value); }
    public int MaxAutoFixAttempts { get => _maxAutoFixAttempts; set => SetProperty(ref _maxAutoFixAttempts, value); }
    public string PreferredLanguage { get => _preferredLanguage; set => SetProperty(ref _preferredLanguage, value); }
    public double FontSize { get => _fontSize; set => SetProperty(ref _fontSize, value); }
    public bool StreamOutput { get => _streamOutput; set => SetProperty(ref _streamOutput, value); }
    public string HuggingFaceToken { get => _huggingFaceToken; set => SetProperty(ref _huggingFaceToken, value); }
    public string GpuInfoDisplay { get => _gpuInfoDisplay; set => SetProperty(ref _gpuInfoDisplay, value); }
    public string VramRecommendation { get => _vramRecommendation; set => SetProperty(ref _vramRecommendation, value); }
    public string SaveStatus { get => _saveStatus; set => SetProperty(ref _saveStatus, value); }
    public string GpuBackend { get => _gpuBackend; set => SetProperty(ref _gpuBackend, value); }
    public int BatchSize { get => _batchSize; set => SetProperty(ref _batchSize, value); }
    public int ThreadCount { get => _threadCount; set => SetProperty(ref _threadCount, value); }

    /// <summary>UI language: "English" or "ไทย". Changes LocalizationService and saves to settings.</summary>
    public string UiLanguage
    {
        get => _uiLanguage;
        set
        {
            if (SetProperty(ref _uiLanguage, value))
            {
                string lang = value == "ไทย" ? "th" : "en";
                _localizationService.SetLanguage(lang);
                _settingsService.UpdateSettings(s => s.Language = lang);
            }
        }
    }

    public List<string> UiLanguageOptions { get; } = new() { "English", "ไทย" };

    // AI Provider properties
    public AiProviderType SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                LoadProviderConfig(value);
                OnPropertyChanged(nameof(NeedsApiKey));
                OnPropertyChanged(nameof(SupportsBaseUrl));
                OnPropertyChanged(nameof(IsOllamaSelected));
                OnPropertyChanged(nameof(IsLocalSelected));
                OnPropertyChanged(nameof(ApiKeyLink));
                OnPropertyChanged(nameof(ApiKeyLinkText));
            }
        }
    }

    public string ProviderApiKey { get => _providerApiKey; set => SetProperty(ref _providerApiKey, value); }
    public string ProviderBaseUrl { get => _providerBaseUrl; set => SetProperty(ref _providerBaseUrl, value); }
    public string ProviderModel { get => _providerModel; set => SetProperty(ref _providerModel, value); }
    public string ProviderCustomModel { get => _providerCustomModel; set => SetProperty(ref _providerCustomModel, value); }
    public bool UseCustomModel { get => _useCustomModel; set => SetProperty(ref _useCustomModel, value); }
    public string ConnectionTestResult { get => _connectionTestResult; set => SetProperty(ref _connectionTestResult, value); }
    public bool IsTestingConnection { get => _isTestingConnection; set => SetProperty(ref _isTestingConnection, value); }

    public ObservableCollection<string> AvailableModels { get; } = new();

    public bool NeedsApiKey => SelectedProvider is AiProviderType.OpenAI
        or AiProviderType.Anthropic or AiProviderType.Gemini;
    public bool SupportsBaseUrl => SelectedProvider is not AiProviderType.Local;
    public bool IsOllamaSelected => SelectedProvider == AiProviderType.Ollama;
    public bool IsLocalSelected => SelectedProvider == AiProviderType.Local;

    public string ApiKeyLink => SelectedProvider switch
    {
        AiProviderType.OpenAI => "https://platform.openai.com/api-keys",
        AiProviderType.Anthropic => "https://console.anthropic.com/settings/keys",
        AiProviderType.Gemini => "https://aistudio.google.com/apikey",
        _ => "",
    };

    public string ApiKeyLinkText => SelectedProvider switch
    {
        AiProviderType.OpenAI => "Get your API key at platform.openai.com",
        AiProviderType.Anthropic => "Get your API key at console.anthropic.com",
        AiProviderType.Gemini => "Get your API key at aistudio.google.com",
        _ => "",
    };

    public bool IsPortable => _settingsService.IsPortable;
    public string DataRootDisplay => _settingsService.DataRoot;

    public List<AiProviderType> ProviderOptions { get; } = Enum.GetValues<AiProviderType>().ToList();

    public List<string> LanguageOptions { get; } = new()
    {
        "C#", "Python", "JavaScript", "TypeScript", "Java", "C++", "Go", "Rust", "PHP", "Ruby"
    };

    public List<string> BackendOptions { get; } = new()
    {
        "Auto", "CUDA12", "CPU"
    };

    public List<uint> ContextSizeOptions { get; } = new()
    {
        2048, 4096, 8192, 16384, 32768
    };

    public List<int> BatchSizeOptions { get; } = new()
    {
        128, 256, 512, 1024, 2048
    };

    public ICommand BrowseModelDirCommand { get; }
    public ICommand BrowseCacheDirCommand { get; }
    public ICommand BrowseLogDirCommand { get; }
    public ICommand BrowseTempDirCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ResetDefaultsCommand { get; }
    public ICommand RefreshGpuCommand { get; }
    public ICommand OpenDirectoryCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand ApplyProviderCommand { get; }
    public ICommand RefreshOllamaModelsCommand { get; }

    public SettingsViewModel(
        SettingsService settingsService,
        GpuDetectionService gpuDetectionService,
        AiProviderManager providerManager,
        LocalizationService localizationService)
    {
        _settingsService = settingsService;
        _gpuDetectionService = gpuDetectionService;
        _providerManager = providerManager;
        _localizationService = localizationService;

        BrowseModelDirCommand = new RelayCommand(() => { var p = BrowseFolder("Model Directory", ModelDirectory); if (p != null) ModelDirectory = p; });
        BrowseCacheDirCommand = new RelayCommand(() => { var p = BrowseFolder("Cache Directory", CacheDirectory); if (p != null) CacheDirectory = p; });
        BrowseLogDirCommand = new RelayCommand(() => { var p = BrowseFolder("Log Directory", LogDirectory); if (p != null) LogDirectory = p; });
        BrowseTempDirCommand = new RelayCommand(() => { var p = BrowseFolder("Temp Directory", TempDirectory); if (p != null) TempDirectory = p; });
        SaveCommand = new RelayCommand(Save);
        ResetDefaultsCommand = new RelayCommand(ResetDefaults);
        RefreshGpuCommand = new RelayCommand(RefreshGpu);
        OpenDirectoryCommand = new RelayCommand<string>(OpenDirectory);
        TestConnectionCommand = new AsyncRelayCommand(TestConnection);
        ApplyProviderCommand = new AsyncRelayCommand(ApplyProvider);
        RefreshOllamaModelsCommand = new AsyncRelayCommand(RefreshOllamaModels);

        LoadFromSettings();
        DetectGpuInfo();
    }

    private void LoadProviderConfig(AiProviderType type)
    {
        var configs = _settingsService.Settings.ProviderConfigs;
        var key = type.ToString();

        if (configs.TryGetValue(key, out var config))
        {
            ProviderApiKey = config.ApiKey ?? "";
            ProviderBaseUrl = config.BaseUrl ?? "";
            ProviderModel = config.SelectedModel ?? "";
            ProviderCustomModel = config.CustomModelId ?? "";
            UseCustomModel = config.UseCustomModel;
        }
        else
        {
            ProviderApiKey = "";
            ProviderBaseUrl = "";
            ProviderModel = "";
            ProviderCustomModel = "";
            UseCustomModel = false;
        }

        // Populate model dropdown
        AvailableModels.Clear();
        var models = type switch
        {
            AiProviderType.OpenAI => OpenAiProvider.KnownModels,
            AiProviderType.Anthropic => AnthropicProvider.KnownModels,
            AiProviderType.Gemini => GeminiProvider.KnownModels,
            _ => Array.Empty<string>(),
        };
        foreach (var m in models)
            AvailableModels.Add(m);

        // For Ollama, show cached models then auto-detect fresh ones
        if (type == AiProviderType.Ollama)
        {
            var ollamaProvider = _providerManager.GetProvider(AiProviderType.Ollama) as OllamaProvider;
            if (ollamaProvider != null)
            {
                foreach (var m in ollamaProvider.AvailableModels)
                    AvailableModels.Add(m);

                // Auto-detect: fetch fresh models in background
                _ = AutoDetectOllamaAsync(ollamaProvider);
            }
        }

        // Auto-select first model if none selected
        if (string.IsNullOrEmpty(ProviderModel) && AvailableModels.Count > 0)
            ProviderModel = AvailableModels[0];

        ConnectionTestResult = "";
    }

    private void SaveProviderConfig()
    {
        var key = SelectedProvider.ToString();
        var config = new ProviderConfig
        {
            ApiKey = string.IsNullOrWhiteSpace(ProviderApiKey) ? null : ProviderApiKey,
            BaseUrl = string.IsNullOrWhiteSpace(ProviderBaseUrl) ? null : ProviderBaseUrl,
            SelectedModel = ProviderModel,
            CustomModelId = ProviderCustomModel,
            UseCustomModel = UseCustomModel,
        };

        _settingsService.UpdateSettings(s =>
        {
            s.ProviderConfigs[key] = config;
            s.ActiveProvider = SelectedProvider;
        });
    }

    private async Task TestConnection()
    {
        IsTestingConnection = true;
        ConnectionTestResult = "Testing...";
        try
        {
            // Apply config to provider without persisting to disk (Save button does that)
            ApplyProviderConfigInMemory();
            var provider = _providerManager.GetProvider(SelectedProvider);
            var (success, message) = await provider.TestConnectionAsync();
            ConnectionTestResult = success ? $"Connected: {message}" : $"Failed: {message}";
        }
        catch (Exception ex)
        {
            ConnectionTestResult = $"Error: {ex.Message}";
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    /// <summary>
    /// Apply current UI values to the in-memory config so TestConnection works
    /// without triggering a disk save.
    /// </summary>
    private void ApplyProviderConfigInMemory()
    {
        var key = SelectedProvider.ToString();
        var config = new ProviderConfig
        {
            ApiKey = string.IsNullOrWhiteSpace(ProviderApiKey) ? null : ProviderApiKey,
            BaseUrl = string.IsNullOrWhiteSpace(ProviderBaseUrl) ? null : ProviderBaseUrl,
            SelectedModel = ProviderModel,
            CustomModelId = ProviderCustomModel,
            UseCustomModel = UseCustomModel,
        };
        _settingsService.Settings.ProviderConfigs[key] = config;
    }

    private async Task ApplyProvider()
    {
        SaveProviderConfig();
        ConnectionTestResult = "Switching provider...";
        try
        {
            await _providerManager.SwitchProviderAsync(SelectedProvider);
            ConnectionTestResult = $"Active: {_providerManager.ActiveProvider.DisplayName}";
            if (_providerManager.ActiveProvider.IsReady)
                ConnectionTestResult += " (ready)";
            SaveStatus = "Provider applied!";
            _ = Task.Delay(2000).ContinueWith(_ => App.Current?.Dispatcher.Invoke(() => SaveStatus = ""));
        }
        catch (Exception ex)
        {
            ConnectionTestResult = $"Error: {ex.Message}";
        }
    }

    private async Task RefreshOllamaModels()
    {
        var ollamaProvider = _providerManager.GetProvider(AiProviderType.Ollama) as OllamaProvider;
        if (ollamaProvider == null) return;

        ConnectionTestResult = "Fetching models...";
        try
        {
            // Apply base URL in memory so the provider can reach the right host
            ApplyProviderConfigInMemory();
            var models = await ollamaProvider.GetAvailableModelsAsync();

            AvailableModels.Clear();
            foreach (var m in models)
                AvailableModels.Add(m);

            if (AvailableModels.Count > 0 && string.IsNullOrEmpty(ProviderModel))
                ProviderModel = AvailableModels[0];

            ConnectionTestResult = $"Found {models.Count} model(s)";
        }
        catch (Exception ex)
        {
            ConnectionTestResult = $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Auto-detect Ollama running locally — silently fetch models when user selects Ollama provider.
    /// </summary>
    private async Task AutoDetectOllamaAsync(OllamaProvider ollamaProvider)
    {
        try
        {
            ApplyProviderConfigInMemory();
            ConnectionTestResult = "Detecting Ollama...";
            var models = await ollamaProvider.GetAvailableModelsAsync();

            App.Current?.Dispatcher.Invoke(() =>
            {
                AvailableModels.Clear();
                foreach (var m in models)
                    AvailableModels.Add(m);

                if (models.Count > 0)
                {
                    if (string.IsNullOrEmpty(ProviderModel))
                        ProviderModel = AvailableModels[0];
                    ConnectionTestResult = $"Ollama detected — {models.Count} model(s) available";
                }
                else
                {
                    ConnectionTestResult = "Ollama connected but no models found. Run: ollama pull llama3";
                }
            });
        }
        catch
        {
            App.Current?.Dispatcher.Invoke(() =>
            {
                ConnectionTestResult = "Ollama not detected. Install from ollama.com and run 'ollama serve'";
            });
        }
    }

    private void LoadFromSettings()
    {
        var s = _settingsService.Settings;
        ModelDirectory = s.ModelDirectory;
        CacheDirectory = s.CacheDirectory;
        LogDirectory = s.LogDirectory;
        TempDirectory = s.TempDirectory;
        ContextSize = s.ContextSize;
        GpuLayerCount = s.GpuLayerCount;
        Temperature = s.Temperature;
        TopP = s.TopP;
        MaxTokens = s.MaxTokens;
        RepeatPenalty = s.RepeatPenalty;
        AutoExecuteCode = s.AutoExecuteCode;
        MaxAutoFixAttempts = s.MaxAutoFixAttempts;
        PreferredLanguage = s.PreferredLanguage;
        _uiLanguage = s.Language == "th" ? "ไทย" : "English"; // don't trigger setter during load
        OnPropertyChanged(nameof(UiLanguage));
        FontSize = s.FontSize;
        StreamOutput = s.StreamOutput;
        HuggingFaceToken = s.HuggingFaceToken ?? "";
        GpuBackend = s.GpuBackend;
        BatchSize = s.BatchSize;
        ThreadCount = s.ThreadCount;
        ExtendedThinkingEnabled = s.ExtendedThinkingEnabled;
        ThinkingBudgetTokens = s.ThinkingBudgetTokens;
        PromptCachingEnabled = s.PromptCachingEnabled;

        // Load provider settings
        SelectedProvider = s.ActiveProvider;
    }

    private void DetectGpuInfo()
    {
        Task.Run(() =>
        {
            var gpu = _gpuDetectionService.DetectGpu();
            var rec = gpu.GetRecommendation();
            App.Current?.Dispatcher.Invoke(() =>
            {
                if (gpu.VramTotalBytes > 0)
                {
                    GpuInfoDisplay = $"{gpu.Name}\n"
                        + $"Brand: {gpu.GpuBrand}\n"
                        + $"VRAM: {gpu.VramTotalGB:F1} GB total / {gpu.VramFreeGB:F1} GB free\n"
                        + $"Driver: {gpu.DriverVersion}\n"
                        + $"CUDA: {(gpu.IsCudaAvailable ? "Available" : "Not available")}";
                }
                else
                {
                    GpuInfoDisplay = "No dedicated GPU detected.\nModels will run on CPU (slower but works with any system).";
                }

                VramRecommendation = rec.Description
                    + $"\nRecommended: up to {rec.RecommendedParameterBillions}B parameters"
                    + $"\nQuantization: {rec.RecommendedQuantization}";
            });
        });
    }

    private static string? BrowseFolder(string title, string currentPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = System.IO.Directory.Exists(currentPath) ? currentPath : "",
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private void Save()
    {
        // Save everything in a single UpdateSettings call to avoid double-write
        _settingsService.UpdateSettings(s =>
        {
            s.ModelDirectory = ModelDirectory;
            s.CacheDirectory = CacheDirectory;
            s.LogDirectory = LogDirectory;
            s.TempDirectory = TempDirectory;
            s.ContextSize = ContextSize;
            s.GpuLayerCount = GpuLayerCount;
            s.Temperature = Temperature;
            s.TopP = TopP;
            s.MaxTokens = MaxTokens;
            s.RepeatPenalty = RepeatPenalty;
            s.AutoExecuteCode = AutoExecuteCode;
            s.MaxAutoFixAttempts = MaxAutoFixAttempts;
            s.PreferredLanguage = PreferredLanguage;
            s.FontSize = FontSize;
            s.StreamOutput = StreamOutput;
            s.HuggingFaceToken = string.IsNullOrWhiteSpace(HuggingFaceToken) ? null : HuggingFaceToken;
            s.GpuBackend = GpuBackend;
            s.BatchSize = BatchSize;
            s.ThreadCount = ThreadCount;
            s.ExtendedThinkingEnabled = ExtendedThinkingEnabled;
            s.ThinkingBudgetTokens = ThinkingBudgetTokens;
            s.PromptCachingEnabled = PromptCachingEnabled;

            // Provider config in same save
            s.ActiveProvider = SelectedProvider;
            s.ProviderConfigs[SelectedProvider.ToString()] = new ProviderConfig
            {
                ApiKey = string.IsNullOrWhiteSpace(ProviderApiKey) ? null : ProviderApiKey,
                BaseUrl = string.IsNullOrWhiteSpace(ProviderBaseUrl) ? null : ProviderBaseUrl,
                SelectedModel = ProviderModel,
                CustomModelId = ProviderCustomModel,
                UseCustomModel = UseCustomModel,
            };
        });

        SaveStatus = "Settings saved!";
        _ = Task.Delay(2000).ContinueWith(_ => App.Current?.Dispatcher.Invoke(() => SaveStatus = ""));
    }

    private void ResetDefaults()
    {
        var defaults = new AppSettings();
        _settingsService.UpdateSettings(s =>
        {
            s.ContextSize = defaults.ContextSize;
            s.GpuLayerCount = defaults.GpuLayerCount;
            s.Temperature = defaults.Temperature;
            s.TopP = defaults.TopP;
            s.MaxTokens = defaults.MaxTokens;
            s.RepeatPenalty = defaults.RepeatPenalty;
            s.AutoExecuteCode = defaults.AutoExecuteCode;
            s.MaxAutoFixAttempts = defaults.MaxAutoFixAttempts;
            s.FontSize = defaults.FontSize;
            s.StreamOutput = defaults.StreamOutput;
            s.GpuBackend = defaults.GpuBackend;
            s.BatchSize = defaults.BatchSize;
            s.ThreadCount = defaults.ThreadCount;
        });
        LoadFromSettings();
        SaveStatus = "Settings reset to defaults.";
    }

    private void RefreshGpu()
    {
        _gpuDetectionService.ClearCache();
        GpuInfoDisplay = "Detecting...";
        VramRecommendation = "";
        DetectGpuInfo();
    }

    private void OpenDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.Directory.Exists(path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch { }
    }
}
