using System.Collections.ObjectModel;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace CluadeX.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly GpuDetectionService _gpuDetectionService;
    private readonly AiProviderManager _providerManager;
    private readonly LocalizationService _localizationService;
    private readonly MemoryService _memoryService;
    private readonly ActivationService _activationService;
    private readonly DebugLogService? _log;
    private readonly System.Windows.Threading.DispatcherTimer _autoSaveDebounce;

    private string T(string key) => _localizationService.T(key);

    // ═══════════════════════════════════════════════════════════════════════════════════════
    //  Page navigation — one category at a time
    // ═══════════════════════════════════════════════════════════════════════════════════════

    private string _selectedCategory = "provider";
    /// <summary>provider · model · agent · autonomous · memory · local · general</summary>
    public string SelectedCategory { get => _selectedCategory; set => SetProperty(ref _selectedCategory, value); }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    //  Persisted settings
    //  Auto-save writes ONLY what the user changed on this page since the last save. Writing the
    //  whole page back used to (a) rewrite settings.json every ~2 s forever — the save-status text
    //  restarted its own debounce — and (b) overwrite changes made elsewhere (chat-bar Think /
    //  Auto-run, GPU layers from the Models page, MCP model switches) with this page's stale copy.
    // ═══════════════════════════════════════════════════════════════════════════════════════

    private static readonly Dictionary<string, Action<SettingsViewModel, AppSettings>> Writers = new()
    {
        // Model & output
        [nameof(MaxTokens)] = (vm, s) => s.MaxTokens = Math.Clamp(vm.MaxTokens, 256, 128000),
        [nameof(Temperature)] = (vm, s) => s.Temperature = Math.Clamp(vm.Temperature, 0f, 2f),
        [nameof(TopP)] = (vm, s) => s.TopP = Math.Clamp(vm.TopP, 0f, 1f),
        [nameof(PromptCachingEnabled)] = (vm, s) => s.PromptCachingEnabled = vm.PromptCachingEnabled,
        [nameof(ThinkingBudgetTokens)] = (vm, s) => s.ThinkingBudgetTokens = Math.Clamp(vm.ThinkingBudgetTokens, 1024, 64000),
        // Agent
        [nameof(MaxAgentIterations)] = (vm, s) => s.MaxAgentIterations = Math.Clamp(vm.MaxAgentIterations, 1, 100),
        [nameof(InteractiveRequestTimeoutSeconds)] = (vm, s) => s.InteractiveRequestTimeoutSeconds = Math.Clamp(vm.InteractiveRequestTimeoutSeconds, 0, 3600),
        [nameof(EnforceReadBeforeEdit)] = (vm, s) => s.EnforceReadBeforeEdit = vm.EnforceReadBeforeEdit,
        [nameof(AutoVerifyAfterEditEnabled)] = (vm, s) => s.AutoVerifyAfterEditEnabled = vm.AutoVerifyAfterEditEnabled,
        [nameof(AutoVerifyCommand)] = (vm, s) => s.AutoVerifyCommand = vm.AutoVerifyCommand?.Trim() ?? "",
        [nameof(MaxAutoFixAttempts)] = (vm, s) => s.MaxAutoFixAttempts = Math.Clamp(vm.MaxAutoFixAttempts, 0, 20),
        [nameof(EscalationEnabled)] = (vm, s) =>
        {
            s.EscalationEnabled = vm.EscalationEnabled;
            s.EscalationProviderName = nameof(AiProviderType.Anthropic);   // the only provider the native loop can hand off to
        },
        [nameof(BrainAutoRecallEnabled)] = (vm, s) => s.BrainAutoRecallEnabled = vm.BrainAutoRecallEnabled,
        [nameof(SemanticSearchEnabled)] = (vm, s) => s.SemanticSearchEnabled = vm.SemanticSearchEnabled,
        [nameof(EmbeddingModel)] = (vm, s) => s.EmbeddingModel = vm.EmbeddingModel?.Trim() ?? "",
        [nameof(InstinctLearningEnabled)] = (vm, s) => s.InstinctLearningEnabled = vm.InstinctLearningEnabled,
        // Autonomous loop
        [nameof(AutoFixMaxIterations)] = (vm, s) => s.AutoFixMaxIterations = Math.Clamp(vm.AutoFixMaxIterations, 1, 50),
        [nameof(AutoReviewEnabled)] = (vm, s) => s.AutoReviewEnabled = vm.AutoReviewEnabled,
        [nameof(AutoReviewMode)] = (vm, s) => s.AutoReviewMode = vm.AutoReviewMode is "fixed_rounds" ? "fixed_rounds" : "until_clean",
        [nameof(AutoReviewMaxRounds)] = (vm, s) => s.AutoReviewMaxRounds = Math.Clamp(vm.AutoReviewMaxRounds, 1, 20),
        // Context & memory
        [nameof(MicrocompactEnabled)] = (vm, s) => s.MicrocompactEnabled = vm.MicrocompactEnabled,
        [nameof(MicrocompactKeepRecentTurns)] = (vm, s) => s.MicrocompactKeepRecentTurns = Math.Clamp(vm.MicrocompactKeepRecentTurns, 1, 100),
        [nameof(MicrocompactMaxOldResultChars)] = (vm, s) => s.MicrocompactMaxOldResultChars = Math.Clamp(vm.MicrocompactMaxOldResultChars, 50, 20000),
        [nameof(SessionMemoryEnabled)] = (vm, s) => s.SessionMemoryEnabled = vm.SessionMemoryEnabled,
        [nameof(EnableStrategicCompactionToast)] = (vm, s) => s.EnableStrategicCompactionToast = vm.EnableStrategicCompactionToast,
        // Local models
        [nameof(GpuBackend)] = (vm, s) => s.GpuBackend = vm.GpuBackend,
        [nameof(GpuLayerCount)] = (vm, s) => s.GpuLayerCount = Math.Clamp(vm.GpuLayerCount, -1, 1000),
        [nameof(BatchSize)] = (vm, s) => s.BatchSize = vm.BatchSize,
        [nameof(ThreadCount)] = (vm, s) => s.ThreadCount = Math.Clamp(vm.ThreadCount, 0, 512),
        [nameof(ContextSize)] = (vm, s) => s.ContextSize = vm.ContextSize,
        [nameof(RepeatPenalty)] = (vm, s) => s.RepeatPenalty = Math.Clamp(vm.RepeatPenalty, 1f, 2f),
        [nameof(LocalNativeToolUseEnabled)] = (vm, s) => s.LocalNativeToolUseEnabled = vm.LocalNativeToolUseEnabled,
        [nameof(LocalToolCallRepairEnabled)] = (vm, s) => s.LocalToolCallRepairEnabled = vm.LocalToolCallRepairEnabled,
        [nameof(PlanFirstForceToolEnabled)] = (vm, s) => s.PlanFirstForceToolEnabled = vm.PlanFirstForceToolEnabled,
        [nameof(MidLoopReminderEnabled)] = (vm, s) => s.MidLoopReminderEnabled = vm.MidLoopReminderEnabled,
        [nameof(MidLoopReminderEvery)] = (vm, s) => s.MidLoopReminderEvery = Math.Clamp(vm.MidLoopReminderEvery, 2, 50),
        [nameof(TopK)] = (vm, s) => s.TopK = Math.Clamp(vm.TopK, 0, 200),
        [nameof(MinP)] = (vm, s) => s.MinP = Math.Clamp(vm.MinP, 0f, 1f),
        [nameof(RepeatPenaltyTokens)] = (vm, s) => s.RepeatPenaltyTokens = Math.Clamp(vm.RepeatPenaltyTokens, 0, 2048),
        [nameof(ToolCallTemperature)] = (vm, s) => s.ToolCallTemperature = Math.Clamp(vm.ToolCallTemperature, 0f, 2f),
        [nameof(CustomLlamaCppBackendPath)] = (vm, s) => s.CustomLlamaCppBackendPath = vm.CustomLlamaCppBackendPath?.Trim() ?? "",
        [nameof(ModelDirectory)] = (vm, s) => s.ModelDirectory = vm.ModelDirectory,
        [nameof(HuggingFaceToken)] = (vm, s) => s.HuggingFaceToken = string.IsNullOrWhiteSpace(vm.HuggingFaceToken) ? null : vm.HuggingFaceToken.Trim(),
        // General
        [nameof(LiveEditFollow)] = (vm, s) => s.LiveEditFollow = vm.LiveEditFollow,
        [nameof(AutoOpenEditorOnAgentEdit)] = (vm, s) => s.AutoOpenEditorOnAgentEdit = vm.AutoOpenEditorOnAgentEdit,
        [nameof(LiveCodingAnimationEnabled)] = (vm, s) => s.LiveCodingAnimationEnabled = vm.LiveCodingAnimationEnabled,
        [nameof(TempDirectory)] = (vm, s) => s.TempDirectory = vm.TempDirectory,
    };

    /// <summary>The provider card's fields — saved together as ProviderConfigs[SelectedProvider].</summary>
    private static readonly HashSet<string> ProviderFields =
    [
        nameof(ProviderApiKey), nameof(ProviderBaseUrl), nameof(ProviderModel),
        nameof(ProviderCustomModel), nameof(UseCustomModel), nameof(ProviderWorkspaceId),
    ];

    private readonly HashSet<string> _dirty = new();
    private bool _loading;   // values are being loaded from settings, not edited by the user
    private bool _saving;    // our own write — don't reload from it

    // ── Model & output ──
    private int _maxTokens;
    private float _temperature, _topP;
    private bool _promptCachingEnabled = true;
    private int _thinkingBudgetTokens = 10000;
    public int MaxTokens { get => _maxTokens; set => SetProperty(ref _maxTokens, value); }
    public float Temperature { get => _temperature; set => SetProperty(ref _temperature, value); }
    public float TopP { get => _topP; set => SetProperty(ref _topP, value); }
    public bool PromptCachingEnabled { get => _promptCachingEnabled; set => SetProperty(ref _promptCachingEnabled, value); }
    public int ThinkingBudgetTokens { get => _thinkingBudgetTokens; set => SetProperty(ref _thinkingBudgetTokens, value); }

    // ── Agent ──
    private int _maxAgentIterations = 25, _interactiveTimeoutSeconds = 120, _maxAutoFixAttempts;
    private bool _enforceReadBeforeEdit = true, _autoVerifyAfterEdit = true, _escalationEnabled;
    private bool _brainAutoRecallEnabled = true, _semanticSearchEnabled, _instinctLearningEnabled;
    private string _autoVerifyCommand = "", _embeddingModel = "";
    public int MaxAgentIterations { get => _maxAgentIterations; set => SetProperty(ref _maxAgentIterations, value); }
    public int InteractiveRequestTimeoutSeconds { get => _interactiveTimeoutSeconds; set => SetProperty(ref _interactiveTimeoutSeconds, value); }
    public bool EnforceReadBeforeEdit { get => _enforceReadBeforeEdit; set => SetProperty(ref _enforceReadBeforeEdit, value); }
    public bool AutoVerifyAfterEditEnabled { get => _autoVerifyAfterEdit; set => SetProperty(ref _autoVerifyAfterEdit, value); }
    public string AutoVerifyCommand { get => _autoVerifyCommand; set => SetProperty(ref _autoVerifyCommand, value); }
    public int MaxAutoFixAttempts { get => _maxAutoFixAttempts; set => SetProperty(ref _maxAutoFixAttempts, value); }
    public bool EscalationEnabled { get => _escalationEnabled; set => SetProperty(ref _escalationEnabled, value); }
    public bool BrainAutoRecallEnabled { get => _brainAutoRecallEnabled; set => SetProperty(ref _brainAutoRecallEnabled, value); }
    public bool SemanticSearchEnabled { get => _semanticSearchEnabled; set => SetProperty(ref _semanticSearchEnabled, value); }
    public string EmbeddingModel { get => _embeddingModel; set => SetProperty(ref _embeddingModel, value); }
    public bool InstinctLearningEnabled { get => _instinctLearningEnabled; set => SetProperty(ref _instinctLearningEnabled, value); }

    // ── Autonomous loop ──
    private int _autoFixMaxIterations = 5, _autoReviewMaxRounds = 3;
    private bool _autoReviewEnabled = true;
    private string _autoReviewMode = "until_clean";
    public int AutoFixMaxIterations { get => _autoFixMaxIterations; set => SetProperty(ref _autoFixMaxIterations, value); }
    public bool AutoReviewEnabled { get => _autoReviewEnabled; set => SetProperty(ref _autoReviewEnabled, value); }
    public string AutoReviewMode { get => _autoReviewMode; set => SetProperty(ref _autoReviewMode, value); }
    public int AutoReviewMaxRounds { get => _autoReviewMaxRounds; set => SetProperty(ref _autoReviewMaxRounds, value); }

    // ── Context & memory ──
    private bool _microcompactEnabled = true, _sessionMemoryEnabled, _compactionToast = true;
    private int _microcompactKeepRecentTurns = 3, _microcompactMaxOldResultChars = 800;
    public bool MicrocompactEnabled { get => _microcompactEnabled; set => SetProperty(ref _microcompactEnabled, value); }
    public int MicrocompactKeepRecentTurns { get => _microcompactKeepRecentTurns; set => SetProperty(ref _microcompactKeepRecentTurns, value); }
    public int MicrocompactMaxOldResultChars { get => _microcompactMaxOldResultChars; set => SetProperty(ref _microcompactMaxOldResultChars, value); }
    public bool SessionMemoryEnabled { get => _sessionMemoryEnabled; set => SetProperty(ref _sessionMemoryEnabled, value); }
    public bool EnableStrategicCompactionToast { get => _compactionToast; set => SetProperty(ref _compactionToast, value); }

    // ── Local models ──
    private string _gpuBackend = "Auto", _customLlamaCppBackendPath = "", _modelDirectory = "", _huggingFaceToken = "";
    private int _gpuLayerCount, _batchSize = 512, _threadCount, _topK, _repeatPenaltyTokens = 64, _midLoopReminderEvery = 4;
    private uint _contextSize;
    private float _repeatPenalty, _minP, _toolCallTemperature = 0.2f;
    private bool _localNativeToolUse = true, _localToolCallRepair = true, _planFirstForceTool = true, _midLoopReminder = true;
    public string GpuBackend { get => _gpuBackend; set => SetProperty(ref _gpuBackend, value); }
    public int GpuLayerCount { get => _gpuLayerCount; set => SetProperty(ref _gpuLayerCount, value); }
    public int BatchSize { get => _batchSize; set => SetProperty(ref _batchSize, value); }
    public int ThreadCount { get => _threadCount; set => SetProperty(ref _threadCount, value); }
    public uint ContextSize { get => _contextSize; set => SetProperty(ref _contextSize, value); }
    public float RepeatPenalty { get => _repeatPenalty; set => SetProperty(ref _repeatPenalty, value); }
    public bool LocalNativeToolUseEnabled { get => _localNativeToolUse; set => SetProperty(ref _localNativeToolUse, value); }
    public bool LocalToolCallRepairEnabled { get => _localToolCallRepair; set => SetProperty(ref _localToolCallRepair, value); }
    public bool PlanFirstForceToolEnabled { get => _planFirstForceTool; set => SetProperty(ref _planFirstForceTool, value); }
    public bool MidLoopReminderEnabled { get => _midLoopReminder; set => SetProperty(ref _midLoopReminder, value); }
    public int MidLoopReminderEvery { get => _midLoopReminderEvery; set => SetProperty(ref _midLoopReminderEvery, value); }
    public int TopK { get => _topK; set => SetProperty(ref _topK, value); }
    public float MinP { get => _minP; set => SetProperty(ref _minP, value); }
    public int RepeatPenaltyTokens { get => _repeatPenaltyTokens; set => SetProperty(ref _repeatPenaltyTokens, value); }
    public float ToolCallTemperature { get => _toolCallTemperature; set => SetProperty(ref _toolCallTemperature, value); }
    public string CustomLlamaCppBackendPath { get => _customLlamaCppBackendPath; set => SetProperty(ref _customLlamaCppBackendPath, value); }
    public string ModelDirectory { get => _modelDirectory; set => SetProperty(ref _modelDirectory, value); }
    public string HuggingFaceToken
    {
        get => _huggingFaceToken;
        set { if (SetProperty(ref _huggingFaceToken, value)) OnPropertyChanged(nameof(MaskedHuggingFaceToken)); }
    }
    public string MaskedHuggingFaceToken => Mask(_huggingFaceToken);
    private bool _showHuggingFaceToken;
    public bool ShowHuggingFaceToken { get => _showHuggingFaceToken; set => SetProperty(ref _showHuggingFaceToken, value); }

    // ── General ──
    private bool _liveEditFollow = true, _autoOpenEditor = true, _liveCodingAnimation = true;
    private string _tempDirectory = "";
    public bool LiveEditFollow { get => _liveEditFollow; set => SetProperty(ref _liveEditFollow, value); }
    public bool AutoOpenEditorOnAgentEdit { get => _autoOpenEditor; set => SetProperty(ref _autoOpenEditor, value); }
    public bool LiveCodingAnimationEnabled { get => _liveCodingAnimation; set => SetProperty(ref _liveCodingAnimation, value); }
    public string TempDirectory { get => _tempDirectory; set => SetProperty(ref _tempDirectory, value); }

    private string _uiLanguage = "English";
    /// <summary>UI language: "English" or "ไทย". Applied and saved immediately.</summary>
    public string UiLanguage
    {
        get => _uiLanguage;
        set
        {
            if (SetProperty(ref _uiLanguage, value))
            {
                string lang = value == "ไทย" ? "th" : "en";
                _localizationService.SetLanguage(lang);
                _saving = true;
                try { _settingsService.UpdateSettings(s => s.Language = lang); }
                finally { _saving = false; }
                OnPropertyChanged(nameof(ApiKeyLinkText));
                OnPropertyChanged(nameof(LicenseStatus));
            }
        }
    }
    public List<string> UiLanguageOptions { get; } = ["English", "ไทย"];

    /// <summary>Cloud providers (Anthropic / OpenAI / Gemini) need an activated license; the key itself is
    /// entered on the Features page (it handles moving a license from another device).</summary>
    public bool IsLicenseActivated => _activationService.IsActivated;
    public string LicenseStatus => _activationService.IsActivated
        ? string.Format(T("settings.licenseActive"), _activationService.TierDisplayName)
        : T("settings.licenseInactive");
    /// <summary>Show the "enter license key" shortcut on the provider card: a cloud provider without a license.</summary>
    public bool ShowLicenseShortcut => NeedsApiKey && !_activationService.IsActivated;

    public bool IsPortable => _settingsService.IsPortable;
    public string DataRootDisplay => _settingsService.DataRoot;

    // ── Display-only state (never saved) ──
    private string _gpuInfoDisplay = "", _vramRecommendation = "", _saveStatus = "";
    public string GpuInfoDisplay { get => _gpuInfoDisplay; set => SetProperty(ref _gpuInfoDisplay, value); }
    public string VramRecommendation { get => _vramRecommendation; set => SetProperty(ref _vramRecommendation, value); }
    public string SaveStatus { get => _saveStatus; set => SetProperty(ref _saveStatus, value); }
    private bool _saveStatusIsError;
    /// <summary>The status line shows a problem (red) rather than "Saved ✓" (green).</summary>
    public bool SaveStatusIsError { get => _saveStatusIsError; set => SetProperty(ref _saveStatusIsError, value); }

    public List<string> BackendOptions { get; } = ["Auto", "CUDA12", "CPU"];
    public List<int> BatchSizeOptions { get; } = [128, 256, 512, 1024, 2048];
    /// <summary>12K is the floor: the agent's prompt + tool schemas alone are ~8.2K tokens.</summary>
    public ObservableCollection<uint> ContextSizeOptions { get; } = [12288, 16384, 24576, 32768, 49152, 65536, 98304, 131072];

    // ─── Memory Management ───
    public ObservableCollection<MemoryEntry> Memories { get; } = new();
    private MemoryEntry? _selectedMemory;
    public MemoryEntry? SelectedMemory
    {
        get => _selectedMemory;
        set
        {
            if (SetProperty(ref _selectedMemory, value))
                OnPropertyChanged(nameof(SelectedMemoryContent));
        }
    }
    public string SelectedMemoryContent => _selectedMemory?.Content ?? "";
    private string _memoryStatusText = "";
    public string MemoryStatusText { get => _memoryStatusText; set => SetProperty(ref _memoryStatusText, value); }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    //  AI provider card
    // ═══════════════════════════════════════════════════════════════════════════════════════

    private AiProviderType _selectedProvider;
    private string _providerApiKey = "", _providerBaseUrl = "", _providerModel = "", _providerCustomModel = "";
    private string _providerWorkspaceId = "", _connectionTestResult = "";
    private bool _useCustomModel, _isTestingConnection, _showApiKey;

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
                OnPropertyChanged(nameof(IsAnthropicSelected));
                OnPropertyChanged(nameof(CanRefreshModels));
                OnPropertyChanged(nameof(IsLocalSelected));
                OnPropertyChanged(nameof(CanTestConnection));
                OnPropertyChanged(nameof(ShowLicenseShortcut));
                OnPropertyChanged(nameof(ApiKeyLink));
                OnPropertyChanged(nameof(ApiKeyLinkText));
            }
        }
    }

    public string ProviderApiKey
    {
        get => _providerApiKey;
        set { if (SetProperty(ref _providerApiKey, value)) OnPropertyChanged(nameof(MaskedApiKey)); }
    }

    /// <summary>Masked display of the API key — dots + last 4 chars for verification.</summary>
    public string MaskedApiKey => Mask(_providerApiKey);

    private static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        if (secret.Length <= 4) return new string('•', secret.Length);
        return new string('•', Math.Max(8, secret.Length - 4)) + secret[^4..];
    }

    /// <summary>When true, the API key TextBox is shown; otherwise only the masked text.</summary>
    public bool ShowApiKey { get => _showApiKey; set => SetProperty(ref _showApiKey, value); }
    public string ProviderBaseUrl { get => _providerBaseUrl; set => SetProperty(ref _providerBaseUrl, value); }
    public string ProviderModel { get => _providerModel; set => SetProperty(ref _providerModel, value); }
    public string ProviderCustomModel { get => _providerCustomModel; set => SetProperty(ref _providerCustomModel, value); }
    /// <summary>Anthropic anthropic-workspace-id — required by keys that aren't scoped to one workspace.</summary>
    public string ProviderWorkspaceId { get => _providerWorkspaceId; set => SetProperty(ref _providerWorkspaceId, value); }
    public bool UseCustomModel { get => _useCustomModel; set => SetProperty(ref _useCustomModel, value); }
    public string ConnectionTestResult { get => _connectionTestResult; set => SetProperty(ref _connectionTestResult, value); }
    public bool IsTestingConnection { get => _isTestingConnection; set => SetProperty(ref _isTestingConnection, value); }

    /// <summary>The provider chat is using right now (may differ from the one being configured).</summary>
    public string ActiveProviderName => _providerManager.ActiveProvider.DisplayName;

    public ObservableCollection<string> AvailableModels { get; } = new();

    public bool NeedsApiKey => SelectedProvider is AiProviderType.OpenAI
        or AiProviderType.Anthropic or AiProviderType.Gemini;
    /// <summary>Only providers that actually read BaseUrl (llama-server uses its own port, ClaudeDev runs the CLI).</summary>
    public bool SupportsBaseUrl => SelectedProvider is AiProviderType.OpenAI or AiProviderType.Anthropic
        or AiProviderType.Gemini or AiProviderType.Ollama;
    public bool IsOllamaSelected => SelectedProvider == AiProviderType.Ollama;
    public bool IsAnthropicSelected => SelectedProvider == AiProviderType.Anthropic;
    /// <summary>Providers whose model list can be fetched live (Ollama: local tags; cloud: the key's model list).</summary>
    public bool CanRefreshModels => SelectedProvider is AiProviderType.Ollama or AiProviderType.Anthropic
        or AiProviderType.OpenAI or AiProviderType.Gemini;
    public bool CanTestConnection => SelectedProvider != AiProviderType.Local;
    public string WorkspacesLink => "https://platform.claude.com/settings/workspaces";
    public bool IsLocalSelected => SelectedProvider == AiProviderType.Local;

    public string ApiKeyLink => SelectedProvider switch
    {
        AiProviderType.OpenAI => "https://platform.openai.com/api-keys",
        AiProviderType.Anthropic => "https://platform.claude.com/settings/keys",
        AiProviderType.Gemini => "https://aistudio.google.com/apikey",
        _ => "",
    };

    public string ApiKeyLinkText => ApiKeyLink.Length == 0 ? "" : $"{T("settings.apiKey")} → {ApiKeyLink.Replace("https://", "")}";

    public List<AiProviderType> ProviderOptions { get; } = Enum.GetValues<AiProviderType>().ToList();

    // ═══════════════════════════════════════════════════════════════════════════════════════
    //  Commands
    // ═══════════════════════════════════════════════════════════════════════════════════════

    public ICommand BrowseModelDirCommand { get; }
    public ICommand BrowseTempDirCommand { get; }
    public ICommand BrowseBackendDirCommand { get; }
    public ICommand ResetDefaultsCommand { get; }
    public ICommand RefreshGpuCommand { get; }
    public ICommand OpenDirectoryCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand ApplyProviderCommand { get; }
    public ICommand RefreshModelsCommand { get; }
    public ICommand RefreshMemoriesCommand { get; }
    public ICommand DeleteMemoryCommand { get; }

    public SettingsViewModel(
        SettingsService settingsService,
        GpuDetectionService gpuDetectionService,
        AiProviderManager providerManager,
        LocalizationService localizationService,
        MemoryService memoryService,
        ActivationService activationService,
        DebugLogService? log = null)
    {
        _settingsService = settingsService;
        _gpuDetectionService = gpuDetectionService;
        _providerManager = providerManager;
        _localizationService = localizationService;
        _memoryService = memoryService;
        _activationService = activationService;
        _log = log;

        BrowseModelDirCommand = new RelayCommand(() => { var p = BrowseFolder(T("settings.modelStorageDir"), ModelDirectory); if (p != null) ModelDirectory = p; });
        BrowseTempDirCommand = new RelayCommand(() => { var p = BrowseFolder(T("settings.tempDir"), TempDirectory); if (p != null) TempDirectory = p; });
        BrowseBackendDirCommand = new RelayCommand(() => { var p = BrowseFolder(T("settings.customBackend"), CustomLlamaCppBackendPath); if (p != null) CustomLlamaCppBackendPath = p; });
        ResetDefaultsCommand = new RelayCommand(ResetDefaults);
        RefreshGpuCommand = new RelayCommand(RefreshGpu);
        OpenDirectoryCommand = new RelayCommand<string>(OpenDirectory);
        TestConnectionCommand = new AsyncRelayCommand(TestConnection);
        ApplyProviderCommand = new AsyncRelayCommand(ApplyProvider);
        RefreshModelsCommand = new AsyncRelayCommand(RefreshModels);
        RefreshMemoriesCommand = new RelayCommand(RefreshMemories);
        DeleteMemoryCommand = new RelayCommand(DeleteSelectedMemory);

        // Debounced auto-save, 1 s after the user's last edit — and only for user edits.
        _autoSaveDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoSaveDebounce.Tick += (_, _) => { _autoSaveDebounce.Stop(); Save(); };
        PropertyChanged += (_, e) =>
        {
            if (_loading || e.PropertyName == null) return;
            if (!Writers.ContainsKey(e.PropertyName) && !ProviderFields.Contains(e.PropertyName)) return;   // display-only
            _dirty.Add(e.PropertyName);
            _autoSaveDebounce.Stop();
            _autoSaveDebounce.Start();
        };

        // Settings changed somewhere else (chat bar, Models page, MCP host) → show it here, unless the
        // user has unsaved edits on this page (theirs win once the debounce fires).
        _settingsService.SettingsChanged += () =>
        {
            if (_saving) return;
            App.Current?.Dispatcher.BeginInvoke(() => { if (_dirty.Count == 0) LoadPageValues(_settingsService.Settings); });
        };
        _activationService.ActivationChanged += () => App.Current?.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(IsLicenseActivated));
            OnPropertyChanged(nameof(LicenseStatus));
            OnPropertyChanged(nameof(ShowLicenseShortcut));
        });
        _providerManager.OnProviderChanged += _ =>
            App.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(ActiveProviderName)));
        _localizationService.LanguageChanged += () => OnPropertyChanged(nameof(SecretsNotice));

        LoadPageValues(_settingsService.Settings);
        _uiLanguage = _settingsService.Settings.Language == "th" ? "ไทย" : "English";
        _loading = true;
        try { SelectedProvider = _settingsService.Settings.ActiveProvider; }
        finally { _loading = false; }
        DetectGpuInfo();
        RefreshMemories();
    }

    /// <summary>Show <paramref name="src"/>'s values on the page without marking anything as edited.
    /// The provider card is not touched (it has its own load).</summary>
    private void LoadPageValues(AppSettings src)
    {
        bool wasLoading = _loading;
        _loading = true;
        try { ApplyPageValues(src, includeStorage: true); }
        finally { _loading = wasLoading; }
    }

    /// <summary>Copy <paramref name="src"/> into the page's properties. Under <c>_loading</c> this is a
    /// display refresh; outside it (Reset) every change becomes an edit that auto-save writes.</summary>
    private void ApplyPageValues(AppSettings src, bool includeStorage)
    {
        MaxTokens = src.MaxTokens;
        Temperature = src.Temperature;
        TopP = src.TopP;
        PromptCachingEnabled = src.PromptCachingEnabled;
        ThinkingBudgetTokens = src.ThinkingBudgetTokens;

        MaxAgentIterations = src.MaxAgentIterations;
        InteractiveRequestTimeoutSeconds = src.InteractiveRequestTimeoutSeconds;
        EnforceReadBeforeEdit = src.EnforceReadBeforeEdit;
        AutoVerifyAfterEditEnabled = src.AutoVerifyAfterEditEnabled;
        AutoVerifyCommand = src.AutoVerifyCommand ?? "";
        MaxAutoFixAttempts = src.MaxAutoFixAttempts;
        EscalationEnabled = src.EscalationEnabled;
        BrainAutoRecallEnabled = src.BrainAutoRecallEnabled;
        SemanticSearchEnabled = src.SemanticSearchEnabled;
        EmbeddingModel = src.EmbeddingModel ?? "";
        InstinctLearningEnabled = src.InstinctLearningEnabled;

        AutoFixMaxIterations = src.AutoFixMaxIterations;
        AutoReviewEnabled = src.AutoReviewEnabled;
        AutoReviewMode = src.AutoReviewMode;
        AutoReviewMaxRounds = src.AutoReviewMaxRounds;

        MicrocompactEnabled = src.MicrocompactEnabled;
        MicrocompactKeepRecentTurns = src.MicrocompactKeepRecentTurns;
        MicrocompactMaxOldResultChars = src.MicrocompactMaxOldResultChars;
        SessionMemoryEnabled = src.SessionMemoryEnabled;
        EnableStrategicCompactionToast = src.EnableStrategicCompactionToast;

        GpuBackend = src.GpuBackend;
        GpuLayerCount = src.GpuLayerCount;
        BatchSize = src.BatchSize;
        ThreadCount = src.ThreadCount;
        if (!ContextSizeOptions.Contains(src.ContextSize))   // a hand-picked size still shows in the box
        {
            int at = 0;
            while (at < ContextSizeOptions.Count && ContextSizeOptions[at] < src.ContextSize) at++;
            ContextSizeOptions.Insert(at, src.ContextSize);
        }
        ContextSize = src.ContextSize;
        RepeatPenalty = src.RepeatPenalty;
        LocalNativeToolUseEnabled = src.LocalNativeToolUseEnabled;
        LocalToolCallRepairEnabled = src.LocalToolCallRepairEnabled;
        PlanFirstForceToolEnabled = src.PlanFirstForceToolEnabled;
        MidLoopReminderEnabled = src.MidLoopReminderEnabled;
        MidLoopReminderEvery = src.MidLoopReminderEvery;
        TopK = src.TopK;
        MinP = src.MinP;
        RepeatPenaltyTokens = src.RepeatPenaltyTokens;
        ToolCallTemperature = src.ToolCallTemperature;
        CustomLlamaCppBackendPath = src.CustomLlamaCppBackendPath ?? "";

        LiveEditFollow = src.LiveEditFollow;
        AutoOpenEditorOnAgentEdit = src.AutoOpenEditorOnAgentEdit;
        LiveCodingAnimationEnabled = src.LiveCodingAnimationEnabled;

        if (includeStorage)
        {
            ModelDirectory = src.ModelDirectory;
            TempDirectory = src.TempDirectory;
            HuggingFaceToken = src.HuggingFaceToken ?? "";
        }
    }

    /// <summary>Folder settings that are validated before they are written.</summary>
    private static readonly string[] PathFields = [nameof(ModelDirectory), nameof(TempDirectory)];

    private string PathValue(string field) => field switch
    {
        nameof(ModelDirectory) => ModelDirectory,
        nameof(TempDirectory) => TempDirectory,
        _ => "",
    };

    /// <summary>
    /// A folder path is accepted when it is a full path (drive or UNC) with legal characters on a drive that
    /// exists. It does not have to exist yet — the folder is created when it is first used, never by Save
    /// (the page saves on its own, and creating each typed value left folders like "D:\Mod" behind).
    /// Returns a localized problem description, or null when the path is fine.
    /// </summary>
    public string? ValidateFolderPath(string? path, bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(path)) return allowEmpty ? null : T("settingsm.pathEmpty");
        string p = path.Trim();
        if (p.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0 || p.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0)
            return string.Format(T("settingsm.pathInvalidChars"), p);
        if (!System.IO.Path.IsPathFullyQualified(p))
            return string.Format(T("settingsm.pathNotFull"), p);
        try
        {
            string? root = System.IO.Path.GetPathRoot(p);
            if (string.IsNullOrEmpty(root) || (!root.StartsWith(@"\\") && !System.IO.Directory.Exists(root)))
                return string.Format(T("settingsm.pathNoDrive"), root ?? p);
            if (System.IO.File.Exists(p)) return string.Format(T("settingsm.pathIsFile"), p);
        }
        catch { return string.Format(T("settingsm.pathInvalidChars"), p); }
        return null;
    }

    private void Save()
    {
        if (_dirty.Count == 0) return;
        var changed = _dirty.ToList();
        _dirty.Clear();

        // Folder fields: a path that isn't usable is NOT written; the page keeps showing it with the reason.
        string? pathProblem = null;
        foreach (var field in PathFields)
        {
            if (!changed.Contains(field)) continue;
            string? problem = ValidateFolderPath(PathValue(field), allowEmpty: false);
            if (problem == null) continue;
            changed.Remove(field);
            pathProblem ??= problem;
        }
        if (changed.Count == 0)
        {
            if (pathProblem != null) { SaveStatusIsError = true; SaveStatus = pathProblem; }
            return;
        }
        bool providerChanged = changed.Any(ProviderFields.Contains);

        bool ok;
        _saving = true;
        try
        {
            ok = _settingsService.UpdateSettings(s =>
            {
                foreach (var name in changed)
                    if (Writers.TryGetValue(name, out var write)) write(this, s);
                // Never ActiveProvider: AiProviderManager.SwitchProviderAsync owns "which provider is in use".
                if (providerChanged) s.ProviderConfigs[SelectedProvider.ToString()] = BuildProviderConfig();
            });
        }
        finally { _saving = false; }

        if (!ok)
        {
            // Keep the edits pending so the next change (or reopening the page) tries again — and say so instead
            // of the "Saved ✓" this used to show for a write that never happened.
            foreach (var name in changed) _dirty.Add(name);
            _log?.Error("Settings", $"Saving settings.json failed: {_settingsService.LastSaveError}");
            SaveStatusIsError = true;
            SaveStatus = T("settingsm.saveFailed");
            return;
        }

        if (pathProblem != null) { SaveStatusIsError = true; SaveStatus = pathProblem; return; }
        SaveStatusIsError = false;
        SaveStatus = T("settings.savedTick");
        _ = Task.Delay(1500).ContinueWith(_ =>
        {
            try { App.Current?.Dispatcher.BeginInvoke(() => { if (SaveStatus == T("settings.savedTick")) SaveStatus = ""; }); }
            catch { /* app may be shutting down */ }
        });
    }

    /// <summary>
    /// Shown on the provider card when stored API keys could not be decrypted on this PC (portable copy moved
    /// to another computer / Windows account). The encrypted values are kept unchanged until a new key is typed.
    /// </summary>
    public string SecretsNotice => _settingsService.UndecryptableSecrets.Count == 0
        ? ""
        : string.Format(T("settingsm.secretsUnreadable"), string.Join(", ",
            _settingsService.UndecryptableSecrets.Select(s => s.StartsWith("Provider:") ? s["Provider:".Length..] : "Hugging Face")));

    private void ResetDefaults()
    {
        var confirm = System.Windows.MessageBox.Show(
            T("settings.resetConfirm"), T("settings.resetDefaults"),
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        // Outside _loading, every value that differs becomes an edit — then write them all now.
        ApplyPageValues(new AppSettings(), includeStorage: false);
        _autoSaveDebounce.Stop();
        Save();
        if (!SaveStatusIsError) SaveStatus = T("settings.resetDone");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    //  Provider card logic
    // ═══════════════════════════════════════════════════════════════════════════════════════

    private void LoadProviderConfig(AiProviderType type)
    {
        bool wasLoading = _loading;
        _loading = true;   // showing a saved config is not an edit
        try
        {
            string savedModel = "";
            if (_settingsService.Settings.ProviderConfigs.TryGetValue(type.ToString(), out var config))
            {
                ProviderApiKey = config.ApiKey ?? "";
                ProviderBaseUrl = config.BaseUrl ?? "";
                savedModel = config.SelectedModel ?? "";
                ProviderCustomModel = config.CustomModelId ?? "";
                ProviderWorkspaceId = config.WorkspaceId ?? "";
                UseCustomModel = config.UseCustomModel;
            }
            else
            {
                ProviderApiKey = "";
                ProviderBaseUrl = "";
                ProviderCustomModel = "";
                ProviderWorkspaceId = "";
                UseCustomModel = false;
            }

            // Populate model dropdown (Ollama: the cached tags now, fresh ones from the auto-detect below)
            var ollamaProvider = type == AiProviderType.Ollama
                ? _providerManager.GetProvider(AiProviderType.Ollama) as OllamaProvider : null;
            IEnumerable<string> models = type switch
            {
                AiProviderType.OpenAI => OpenAiProvider.KnownModels,
                AiProviderType.Anthropic => AnthropicProvider.KnownModels,
                AiProviderType.Gemini => GeminiProvider.KnownModels,
                AiProviderType.Ollama => ollamaProvider?.AvailableModels ?? new List<string>(),
                _ => Array.Empty<string>(),
            };
            SetModelList(models, savedModel);
            ConnectionTestResult = "";

            if (ollamaProvider != null)
                _ = AutoDetectOllamaAsync(ollamaProvider);
        }
        finally { _loading = wasLoading; }
    }

    /// <summary>Swap the model dropdown's items without losing the pick. Clearing the list makes the
    /// ComboBox push null into ProviderModel, which silently replaced the user's model with the first
    /// entry. A saved model missing from the list (a custom or retired ID) is kept at the top so the box
    /// shows what will actually be used.</summary>
    private void SetModelList(IEnumerable<string> models, string? desired)
    {
        var list = models.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToList();
        if (!string.IsNullOrWhiteSpace(desired) && !list.Contains(desired)) list.Insert(0, desired);
        AvailableModels.Clear();
        foreach (var m in list)
            AvailableModels.Add(m);
        ProviderModel = !string.IsNullOrWhiteSpace(desired) ? desired : list.FirstOrDefault() ?? "";
    }

    private ProviderConfig BuildProviderConfig() => new()
    {
        ApiKey = string.IsNullOrWhiteSpace(ProviderApiKey) ? null : ProviderApiKey.Trim(),
        BaseUrl = string.IsNullOrWhiteSpace(ProviderBaseUrl) ? null : ProviderBaseUrl.Trim(),
        SelectedModel = ProviderModel,
        CustomModelId = ProviderCustomModel,
        UseCustomModel = UseCustomModel,
        WorkspaceId = string.IsNullOrWhiteSpace(ProviderWorkspaceId) ? null : ProviderWorkspaceId.Trim(),
    };

    /// <summary>Write the provider card now — a test or switch must use exactly what is on screen.</summary>
    private void SaveProviderConfig()
    {
        var key = SelectedProvider.ToString();
        var config = BuildProviderConfig();
        foreach (var f in ProviderFields) _dirty.Remove(f);
        _saving = true;
        try { _settingsService.UpdateSettings(s => s.ProviderConfigs[key] = config); }
        finally { _saving = false; }
    }

    private async Task TestConnection()
    {
        IsTestingConnection = true;
        ConnectionTestResult = T("settings.testing");
        try
        {
            SaveProviderConfig();
            var testedType = SelectedProvider;
            var provider = _providerManager.GetProvider(testedType);
            var (success, message) = await provider.TestConnectionAsync();
            if (SelectedProvider != testedType) return;   // user switched provider while we waited
            ConnectionTestResult = $"{T(success ? "settings.connectedPrefix" : "settings.failedPrefix")}: {message}";

            // Same key + workspace → also load the models this key can actually call. Done even when the
            // test failed: a "model not available" failure is exactly when the user needs the real list.
            if (provider is IModelLister lister)
            {
                int found = await LoadLiveModelsAsync(lister, testedType);
                if (found > 0 && SelectedProvider == testedType)
                    ConnectionTestResult += "\n" + string.Format(T("settings.modelsFound"), found);
            }
        }
        catch (Exception ex)
        {
            _log?.Error("Settings", $"Test connection ({SelectedProvider}) threw", ex);
            ConnectionTestResult = $"{T("settings.failedPrefix")}: {DescribeFailure(ex)}";
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    /// <summary>A short localized reason for an exception (details go to the Debug Log, never the page).</summary>
    private string DescribeFailure(Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException => T("settingsm.errNetwork"),
        TaskCanceledException or TimeoutException => T("settingsm.errTimeout"),
        UnauthorizedAccessException => T("settingsm.errAccess"),
        System.IO.IOException => T("settingsm.errIo"),
        _ => T("settingsm.errGeneric"),
    };

    private async Task ApplyProvider()
    {
        // Cloud providers are part of the paid tier
        bool requiresActivation = SelectedProvider is AiProviderType.OpenAI
            or AiProviderType.Anthropic or AiProviderType.Gemini;
        if (requiresActivation && !_activationService.IsActivated)
        {
            ConnectionTestResult = T("settings.activationRequired");
            return;
        }

        SaveProviderConfig();
        ConnectionTestResult = T("settings.switching");
        try
        {
            // Already the active provider (e.g. after fixing its key): switching is a no-op, so
            // re-initialise instead — otherwise the new key/workspace is never tested.
            if (_providerManager.ActiveProviderType == SelectedProvider
                && ReferenceEquals(_providerManager.ActiveProvider, _providerManager.GetProvider(SelectedProvider)))
                await _providerManager.ReinitializeActiveAsync();
            else
                await _providerManager.SwitchProviderAsync(SelectedProvider);

            var active = _providerManager.ActiveProvider;
            ConnectionTestResult = string.Format(T("settings.nowUsing"), active.DisplayName)
                + (active.IsReady ? T("settings.readySuffix") : $"\n{active.StatusMessage}");
            OnPropertyChanged(nameof(ActiveProviderName));
        }
        catch (Exception ex)
        {
            _log?.Error("Settings", $"Switching to {SelectedProvider} threw", ex);
            ConnectionTestResult = $"{T("settings.failedPrefix")}: {DescribeFailure(ex)}";
        }
    }

    /// <summary>Replace the dropdown with the models this key can call, keeping the current pick.
    /// Returns how many were found; 0 = the listing failed and the built-in list stays.</summary>
    private async Task<int> LoadLiveModelsAsync(IModelLister lister, AiProviderType forType)
    {
        string keep = ProviderModel;
        var models = await lister.ListModelsAsync();
        if (models == null || models.Count == 0 || SelectedProvider != forType) return 0;
        SetModelList(models, string.IsNullOrEmpty(ProviderModel) ? keep : ProviderModel);
        return models.Count;
    }

    private async Task RefreshModels()
    {
        var forType = SelectedProvider;
        if (forType != AiProviderType.Ollama && _providerManager.GetProvider(forType) is IModelLister lister)
        {
            SaveProviderConfig();
            ConnectionTestResult = T("settings.fetchingModels");
            int found = await LoadLiveModelsAsync(lister, forType);
            if (SelectedProvider != forType) return;
            if (found > 0)
            {
                ConnectionTestResult = string.Format(T("settings.modelsFound"), found);
                return;
            }
            // The listing failed — Test Connection says why (workspace, key, credit…) in words.
            var (_, why) = await _providerManager.GetProvider(forType).TestConnectionAsync();
            if (SelectedProvider == forType) ConnectionTestResult = $"{T("settings.failedPrefix")}: {why}";
            return;
        }

        if (_providerManager.GetProvider(AiProviderType.Ollama) is not OllamaProvider ollamaProvider) return;
        ConnectionTestResult = T("settings.fetchingModels");
        try
        {
            SaveProviderConfig();   // the base URL decides which host is asked
            string keep = ProviderModel;
            var models = await ollamaProvider.GetAvailableModelsAsync();
            if (SelectedProvider != AiProviderType.Ollama) return;
            SetModelList(models, keep);
            ConnectionTestResult = string.Format(T("settings.modelsFound"), models.Count);
        }
        catch (Exception ex)
        {
            _log?.Warn("Settings", "Listing Ollama models failed", ex);
            ConnectionTestResult = $"{T("settings.failedPrefix")}: {T("settingsm.ollamaNotDetected")}";
        }
    }

    /// <summary>Auto-detect Ollama running locally — silently fetch models when Ollama is selected.</summary>
    private async Task AutoDetectOllamaAsync(OllamaProvider ollamaProvider)
    {
        string keep = ProviderModel;
        try
        {
            ConnectionTestResult = T("settings.detecting");
            var models = await ollamaProvider.GetAvailableModelsAsync();
            App.Current?.Dispatcher.Invoke(() =>
            {
                if (SelectedProvider != AiProviderType.Ollama) return;   // user moved on while we probed
                bool wasLoading = _loading;
                _loading = true;
                try { SetModelList(models, keep); }
                finally { _loading = wasLoading; }
                ConnectionTestResult = models.Count > 0
                    ? string.Format(T("settings.modelsFound"), models.Count)
                    : T("settingsm.ollamaNoModels");
            });
        }
        catch
        {
            App.Current?.Dispatcher.Invoke(() =>
            {
                if (SelectedProvider == AiProviderType.Ollama)
                    ConnectionTestResult = T("settingsm.ollamaNotDetected");
            });
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    //  GPU, folders, memories
    // ═══════════════════════════════════════════════════════════════════════════════════════

    private void DetectGpuInfo()
    {
        Task.Run(() =>
        {
            var gpu = _gpuDetectionService.DetectGpu();
            var rec = gpu.GetRecommendation();
            App.Current?.Dispatcher.Invoke(() =>
            {
                GpuInfoDisplay = gpu.VramTotalBytes > 0
                    ? string.Format(T("settings.gpuDetails"), gpu.Name, gpu.VramTotalGB, gpu.VramFreeGB,
                        gpu.DriverVersion, T(gpu.IsCudaAvailable ? "settings.yes" : "settings.no"))
                    : T("settings.gpuNone");
                VramRecommendation = string.Format(T("settings.gpuRecommend"),
                    rec.RecommendedParameterBillions, rec.RecommendedQuantization);
            });
        });
    }

    private void RefreshGpu()
    {
        _gpuDetectionService.ClearCache();
        GpuInfoDisplay = T("settings.detecting");
        VramRecommendation = "";
        DetectGpuInfo();
    }

    private static string? BrowseFolder(string title, string currentPath)
    {
        // Owner-aware COM picker — a plain OpenFolderDialog with no owner opens behind the borderless
        // (WindowStyle=None) main window and the app looks frozen. Mirrors ModelManager/PluginManager.
        return CluadeX.Services.Helpers.FolderPicker.ShowDialog(title, currentPath);
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

    private void RefreshMemories()
    {
        try
        {
            Memories.Clear();
            var all = _memoryService.ListAllMemories();
            foreach (var m in all)
                Memories.Add(m);
            MemoryStatusText = all.Count > 0 ? string.Format(T("settings.memCount"), all.Count) : T("settings.memNone");
        }
        catch (Exception ex)
        {
            _log?.Error("Settings", "Listing memories failed", ex);
            MemoryStatusText = $"{T("settings.failedPrefix")}: {DescribeFailure(ex)}";
        }
    }

    private void DeleteSelectedMemory()
    {
        if (SelectedMemory is not { } mem) return;

        var confirm = System.Windows.MessageBox.Show(
            string.Format(T("settings.memDeleteConfirm"), mem.Name), T("settings.memDeleteTitle"),
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            // The entry's own file first: its display name may come from front-matter and not match the
            // file name, which made RemoveMemory(name) report failure for a memory that exists.
            bool deleted = false;
            if (!string.IsNullOrEmpty(mem.FilePath) && System.IO.File.Exists(mem.FilePath))
            {
                string stem = System.IO.Path.GetFileNameWithoutExtension(mem.FilePath);
                deleted = _memoryService.RemoveMemory(stem, mem.Scope == "project");
                if (!deleted && System.IO.File.Exists(mem.FilePath))
                {
                    System.IO.File.Delete(mem.FilePath);
                    deleted = true;
                }
            }
            if (!deleted) deleted = _memoryService.RemoveMemory(mem.Name, mem.Scope == "project");

            MemoryStatusText = string.Format(T(deleted ? "settings.memDeleted" : "settings.memDeleteFailed"), mem.Name);
            if (deleted) RefreshMemories();
        }
        catch (Exception ex)
        {
            _log?.Error("Settings", $"Deleting memory {mem.Name} failed", ex);
            MemoryStatusText = $"{T("settings.failedPrefix")}: {DescribeFailure(ex)}";
        }
    }
}
