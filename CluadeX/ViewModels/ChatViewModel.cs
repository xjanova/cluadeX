using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace CluadeX.ViewModels;

public class ChatViewModel : ViewModelBase
{
    private readonly AiProviderManager _providerManager;
    private readonly CodeAgentService _agentService;
    private readonly CodeExecutionService _codeExecutionService;
    private readonly AgentToolService _agentToolService;
    private readonly AutonomousCodingService? _autonomousService;
    private readonly FileSystemService _fileSystemService;
    private readonly GitService _gitService;
    private readonly GitHubService _gitHubService;
    private readonly SettingsService _settingsService;
    private readonly ContextMemoryService _contextMemoryService;
    private readonly ChatPersistenceService _persistenceService;
    private readonly HuggingFaceService _huggingFaceService;
    private readonly LlamaInferenceService _llamaService;
    private readonly Services.Providers.LocalGgufProvider? _localGgufProvider;
    private readonly GpuDetectionService _gpuDetection;
    private readonly SkillService _skillService;
    private readonly CostTrackingService _costTracker;
    private readonly SessionMemoryService? _sessionMemoryService;
    private readonly DebugLogService? _debugLog;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _memoryTimer;
    private readonly DispatcherTimer _autoSaveTimer;

    // ─── The running turn ───
    // The chat a turn was started in, and a task that completes once the turn has fully unwound. Switching
    // chats mid-turn (New chat, another chat in the sidebar, Delete, Clear, an MCP task) used to leave the turn
    // running while its tool rows and final answer were written into whatever chat was CURRENT by then — chat
    // A's answer landed in chat B. Those actions now stop the turn and await this first, and late callbacks
    // compare against _turnSession before touching the screen.
    private ChatSession? _turnSession;
    private TaskCompletionSource? _turnDone;

    private string _userInput = string.Empty;
    private bool _isGenerating;
    private bool _autoExecute;
    private bool _agenticMode;
    private bool _autonomousMode;
    private string _statusText = "";
    private ChatSession? _currentSession;
    private bool _canSend = true;
    private string _workingDirectory = string.Empty;
    private bool _hasProject;
    private string _gitBranch = string.Empty;
    private double _contextUsagePercent;
    private string _contextUsageText = "";
    private string _costText = "";
    private bool _isDirty; // track unsaved changes
    private string _historySearchQuery = string.Empty;
    private bool _isSearchingHistory;
    private ModelInfo? _selectedLocalModel;
    private string _loadedModelName = "";
    private bool _isLoadingModel;
    private bool _showContextWarning;
    private string _contextWarningText = "";
    private readonly Stopwatch _generationStopwatch = new();
    private DispatcherTimer? _elapsedTimer;
    private string _lastStatusBase = "";  // status without elapsed time suffix
    private ChatMessage? _activeAgentStatusMsg;  // tracks the inline agent status for OnToolExecuted cleanup
    private ChatMessage? _todoListMsg;           // the live plan-checklist bubble (updated in place)
    private int _streamingTokenCount;
    private string _chatSearchQuery = "";
    private bool _isChatSearchVisible;

    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public ObservableCollection<ChatSession> Sessions { get; } = new();
    /// <summary>Sessions filtered by the currently-open project folder. Bound by the sidebar.
    /// When no folder is open, mirrors Sessions in full; otherwise only sessions whose
    /// ProjectPath equals the current WorkingDirectory (or have empty ProjectPath for legacy rows).</summary>
    public ObservableCollection<ChatSession> VisibleSessions { get; } = new();
    public ObservableCollection<SearchResult> SearchResults { get; } = new();
    public ObservableCollection<ModelInfo> LocalModels { get; } = new();

    public string UserInput { get => _userInput; set => SetProperty(ref _userInput, value); }
    public bool IsGenerating { get => _isGenerating; set => SetProperty(ref _isGenerating, value); }

    /// <summary>Run code blocks the AI writes, on this PC, without asking. Security-relevant: off by default and
    /// confirmed every time it is switched on. (It shared the label "Auto" with the autonomous fix loop, so a
    /// user ticking "Auto" could not tell which of the two they had enabled.)</summary>
    public bool AutoExecute
    {
        get => _autoExecute;
        set
        {
            if (value && !_autoExecute && !Confirm(Loc("chat.autoRun.confirm"), Loc("chat.autoRun.confirmTitle")))
            {
                // Declined: the CheckBox already shows a tick — re-raise after the binding update so it clears.
                App.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(AutoExecute)));
                return;
            }
            if (SetProperty(ref _autoExecute, value))
                _settingsService.UpdateSettings(s => s.AutoExecuteCode = value);
        }
    }

    /// <summary>Yes/No confirmation for destructive or risky actions (title, text). A seam so the chat harness
    /// can answer without a modal dialog; the app always uses the real MessageBox.</summary>
    public Func<string, string, bool> Confirm { get; set; } = (text, title) =>
        MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    public bool AgenticMode
    {
        get => _agenticMode;
        set => SetProperty(ref _agenticMode, value);
    }
    /// <summary>Autonomous "drive to green": implement → run tests → fix → loop, then review-iterate for hidden bugs.</summary>
    public bool AutonomousMode
    {
        get => _autonomousMode;
        // Persisted like Think / Auto-run: this switch is the only control for it (Settings has none).
        set { if (SetProperty(ref _autonomousMode, value)) _settingsService.UpdateSettings(s => s.AutonomousLoopEnabled = value); }
    }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public ChatSession? CurrentSession { get => _currentSession; set => SetProperty(ref _currentSession, value); }
    public bool CanSend { get => _canSend; set => SetProperty(ref _canSend, value); }
    public string WorkingDirectory
    {
        get => _workingDirectory;
        set
        {
            if (SetProperty(ref _workingDirectory, value))
            {
                HasProject = !string.IsNullOrEmpty(value);
                RebuildVisibleSessions();
            }
        }
    }
    public bool HasProject { get => _hasProject; set => SetProperty(ref _hasProject, value); }

    /// <summary>When true, the sidebar shows sessions from ALL projects. When false (default)
    /// and a folder is open, it only shows sessions tagged with the current project's path.
    /// Bound to a toggle in the sidebar header.</summary>
    private bool _showAllProjectSessions;
    public bool ShowAllProjectSessions
    {
        get => _showAllProjectSessions;
        set { if (SetProperty(ref _showAllProjectSessions, value)) RebuildVisibleSessions(); }
    }

    /// <summary>Rebuild VisibleSessions from Sessions, applying the project filter.</summary>
    private void RebuildVisibleSessions()
    {
        // Capture on UI thread — all callers should already be on the dispatcher, but be defensive.
        VisibleSessions.Clear();

        // No project open, or user opted to see everything → passthrough
        bool filter = HasProject && !ShowAllProjectSessions;
        string currentProject = WorkingDirectory ?? "";

        foreach (var s in Sessions)
        {
            if (filter)
            {
                // Match sessions for this project. Legacy rows (empty ProjectPath) also pass so
                // we don't hide the user's pre-migration history.
                bool matches = string.IsNullOrEmpty(s.ProjectPath)
                    || PathEquals(s.ProjectPath, currentProject);
                if (!matches) continue;
            }
            VisibleSessions.Add(s);
        }

        OnPropertyChanged(nameof(VisibleSessionsCount));
    }

    public int VisibleSessionsCount => VisibleSessions.Count;

    private static bool PathEquals(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return a == b;
        return string.Equals(
            a.TrimEnd('\\', '/'),
            b.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }
    public string GitBranch { get => _gitBranch; set => SetProperty(ref _gitBranch, value); }

    // Context tracking
    public double ContextUsagePercent { get => _contextUsagePercent; set => SetProperty(ref _contextUsagePercent, value); }
    public string ContextUsageText { get => _contextUsageText; set => SetProperty(ref _contextUsageText, value); }

    // ─── API Cost Tracking ───
    public string CostText { get => _costText; set => SetProperty(ref _costText, value); }

    // ─── Per-Turn Stats ───
    private string _lastTurnStats = "";
    public string LastTurnStats { get => _lastTurnStats; set => SetProperty(ref _lastTurnStats, value); }

    // ─── Session Token Total ───
    private int _sessionTotalTokens;
    public int SessionTotalTokens { get => _sessionTotalTokens; set => SetProperty(ref _sessionTotalTokens, value); }
    private int _sessionTurnCount;
    public int SessionTurnCount { get => _sessionTurnCount; set => SetProperty(ref _sessionTurnCount, value); }

    // ─── Context Level for LED bar ───
    private string _contextLevelLabel = "OK";
    public string ContextLevelLabel { get => _contextLevelLabel; set => SetProperty(ref _contextLevelLabel, value); }
    private string _contextLevelColor = "Green";
    public string ContextLevelColor { get => _contextLevelColor; set => SetProperty(ref _contextLevelColor, value); }

    // ─── Plan Mode ───
    private bool _isPlanMode;
    public bool IsPlanMode { get => _isPlanMode; set => SetProperty(ref _isPlanMode, value); }

    // ─── Extended Thinking Toggle (Anthropic) ───
    private bool _extendedThinkingEnabled;
    public bool ExtendedThinkingEnabled
    {
        get => _extendedThinkingEnabled;
        set
        {
            if (SetProperty(ref _extendedThinkingEnabled, value))
                _settingsService.UpdateSettings(s => s.ExtendedThinkingEnabled = value);
        }
    }

    // ─── Effort (how hard the model works per reply — see EffortLevels) ───
    public IReadOnlyList<string> EffortOptions { get; } = EffortLevels.All;
    private string _reasoningEffort = EffortLevels.Auto;
    public string ReasoningEffort
    {
        get => _reasoningEffort;
        set
        {
            string v = EffortLevels.Normalize(value);
            if (SetProperty(ref _reasoningEffort, v))
                _settingsService.UpdateSettings(s => s.ReasoningEffort = v);
        }
    }

    // ─── Usage strip (top of the chat): what is answering, rate limit left, what it cost ───
    private string _activeProviderLabel = "", _activeModelLabel = "", _rateLimitText = "", _rateLimitTip = "";
    private string _sessionCostText = "$0.00";
    private double _rateLimitFraction, _rateLimitUsedPercent;
    private bool _hasRateLimit;
    public string ActiveProviderLabel { get => _activeProviderLabel; set => SetProperty(ref _activeProviderLabel, value); }
    public string ActiveModelLabel { get => _activeModelLabel; set => SetProperty(ref _activeModelLabel, value); }
    /// <summary>0–1 of the rate limit still available this minute (drives the gauge's fill).</summary>
    public double RateLimitFraction { get => _rateLimitFraction; set => SetProperty(ref _rateLimitFraction, value); }
    /// <summary>100 − percent left: feeds the same green→red brush the context bar uses.</summary>
    public double RateLimitUsedPercent { get => _rateLimitUsedPercent; set => SetProperty(ref _rateLimitUsedPercent, value); }
    public bool HasRateLimit { get => _hasRateLimit; set => SetProperty(ref _hasRateLimit, value); }
    public string RateLimitText { get => _rateLimitText; set => SetProperty(ref _rateLimitText, value); }
    public string RateLimitTip { get => _rateLimitTip; set => SetProperty(ref _rateLimitTip, value); }
    public string SessionCostText { get => _sessionCostText; set => SetProperty(ref _sessionCostText, value); }
    /// <summary>The provider's billing / usage page — the only place an account's credit balance is shown.</summary>
    public string? BillingUrl => _providerManager.ActiveProviderType switch
    {
        AiProviderType.Anthropic => "https://platform.claude.com/settings/billing",
        AiProviderType.OpenAI => "https://platform.openai.com/settings/organization/billing",
        AiProviderType.Gemini => "https://aistudio.google.com/usage",
        AiProviderType.ClaudeDev => "https://claude.ai/settings/usage",
        _ => null,
    };
    public bool HasBillingPage => BillingUrl != null;
    public ICommand OpenBillingCommand { get; private set; } = null!;

    private static string Loc(string key) => LocalizedResources.Instance[key];
    private static string LocF(string key, params object[] args) => string.Format(Loc(key), args);

    /// <summary>True when the chat is answered by a model on this PC (GGUF / llama-server). The chat's model
    /// picker is only shown then: picking a GGUF while on Claude used to silently switch the provider to Local
    /// and start loading weights nobody asked for.</summary>
    public bool ShowLocalModelPicker => _providerManager.ActiveProviderType is AiProviderType.Local or AiProviderType.LlamaServer;

    /// <summary>The model label the status bar shows (MainWindow binds LoadedModelName). It used to show the
    /// local GGUF — or "Ready: …" from the in-process engine — even while Claude was answering.</summary>
    private void RefreshLoadedModelName()
    {
        OnPropertyChanged(nameof(ShowLocalModelPicker));
        if (!ShowLocalModelPicker)
        {
            LoadedModelName = ActiveModelLabel;
            return;
        }
        if (IsLoadingModel) return;   // the load's progress text owns the slot until it finishes
        string name = _settingsService.Settings.SelectedModelName ?? "";
        if (_providerManager.ActiveProvider.IsReady)
            LoadedModelName = LocF("chat.model.ready", string.IsNullOrEmpty(name) ? _llamaService.LoadedModelName ?? "" : name);
        else if (_llamaService.IsModelLoaded)
            LoadedModelName = LocF("chat.model.ready", _llamaService.LoadedModelName ?? name);
        else
            LoadedModelName = string.IsNullOrEmpty(name) ? Loc("chat.usage.noModel") : LocF("chat.model.notLoaded", name);
    }

    /// <summary>Recompute the usage strip from the active provider, its saved model, the latest
    /// rate-limit snapshot and the session's cost. Cheap; called on every change and every 5 s.</summary>
    private void RefreshUsageStrip()
    {
        var type = _providerManager.ActiveProviderType;
        var s = _settingsService.Settings;
        string? configured = s.ProviderConfigs.TryGetValue(type.ToString(), out var cfg) && !string.IsNullOrWhiteSpace(cfg.EffectiveModelId)
            ? cfg.EffectiveModelId!.Trim() : null;
        ActiveProviderLabel = _providerManager.ActiveProvider.DisplayName;
        ActiveModelLabel = type switch
        {
            AiProviderType.Local or AiProviderType.LlamaServer =>
                string.IsNullOrWhiteSpace(s.SelectedModelName) ? Loc("chat.usage.noModel") : s.SelectedModelName,
            AiProviderType.Anthropic => configured ?? AnthropicProvider.DefaultModel,
            AiProviderType.OpenAI => configured ?? OpenAiProvider.DefaultModel,
            AiProviderType.Gemini => configured ?? GeminiProvider.DefaultModel,
            AiProviderType.ClaudeDev => configured is null or "default" or "auto" ? Loc("chat.usage.cliDefault") : configured,
            _ => configured ?? Loc("chat.usage.noModel"),
        };
        OnPropertyChanged(nameof(BillingUrl));
        OnPropertyChanged(nameof(HasBillingPage));

        SessionCostText = _costTracker.TotalRequests == 0 ? "$0.00" : $"${_costTracker.TotalCostUsd:0.0000}";

        bool local = type is AiProviderType.Local or AiProviderType.LlamaServer or AiProviderType.Ollama;
        var snap = UsageMeter.Latest;
        if (snap != null && snap.Provider == type.ToString() && snap.PercentLeft is { } left)
        {
            HasRateLimit = true;
            RateLimitFraction = left / 100.0;
            RateLimitUsedPercent = 100 - left;
            RateLimitText = $"{left:0}%";
            var lines = new List<string>();
            void Line(string label, long? rem, long? lim)
            {
                if (rem is { } r && lim is { } l) lines.Add($"  {label}: {r:N0} / {l:N0}");
            }
            Line(Loc("chat.usage.tokens"), snap.TokensRemaining, snap.TokensLimit);
            Line(Loc("chat.usage.inputTokens"), snap.InputTokensRemaining, snap.InputTokensLimit);
            Line(Loc("chat.usage.outputTokens"), snap.OutputTokensRemaining, snap.OutputTokensLimit);
            Line(Loc("chat.usage.requests"), snap.RequestsRemaining, snap.RequestsLimit);
            string reset = snap.Reset is { } r2 ? r2.ToLocalTime().ToString("HH:mm:ss") : "—";
            RateLimitTip = string.Format(Loc("chat.usage.tip"), snap.Provider, snap.Model,
                string.Join("\n", lines), reset, snap.At.ToString("HH:mm:ss"));
        }
        else
        {
            HasRateLimit = false;
            RateLimitFraction = 0;
            RateLimitUsedPercent = 0;
            RateLimitText = local ? Loc("chat.usage.local") : Loc("chat.usage.unknown");
            RateLimitTip = local ? Loc("chat.usage.localTip")
                : type == AiProviderType.ClaudeDev ? Loc("chat.usage.cliTip")
                : Loc("chat.usage.unknownTip");
        }

        RefreshLoadedModelName();
    }

    // ─── Thinking Display ───
    private bool _showThinking = true;
    public bool ShowThinking
    {
        get => _showThinking;
        set
        {
            if (SetProperty(ref _showThinking, value))
                _settingsService.UpdateSettings(s => s.ShowThinkingSteps = value);
        }
    }

    // ─── Agent internals in the transcript ───
    // Bound from the ListBox's ItemContainerStyle, so flipping it re-evaluates
    // every row's visibility with no list rebuild.
    private bool _showAgentInternals;
    public bool ShowAgentInternals
    {
        get => _showAgentInternals;
        set
        {
            if (SetProperty(ref _showAgentInternals, value))
                _settingsService.UpdateSettings(s => s.ShowAgentInternals = value);
        }
    }

    /// <summary>Short display name for the project folder.</summary>
    public string ProjectName => HasProject ? Path.GetFileName(WorkingDirectory) : "";

    // ─── History Search ───
    public string HistorySearchQuery
    {
        get => _historySearchQuery;
        set
        {
            if (SetProperty(ref _historySearchQuery, value))
                PerformHistorySearch(value);
        }
    }
    public bool IsSearchingHistory { get => _isSearchingHistory; set => SetProperty(ref _isSearchingHistory, value); }

    // ─── Model Selector ───
    /// <summary>Picking a model here loads it — after a confirmation that shows its size against free VRAM
    /// (loading a 7B next to ~3 GB of desktop VRAM use once restarted the owner's PC). Declining puts the
    /// previous selection back.</summary>
    public ModelInfo? SelectedLocalModel
    {
        get => _selectedLocalModel;
        set
        {
            var previous = _selectedLocalModel;
            if (SetProperty(ref _selectedLocalModel, value) && value != null)
                _ = LoadModelFromChat(value, previous);
        }
    }
    public string LoadedModelName { get => _loadedModelName; set => SetProperty(ref _loadedModelName, value); }
    public bool IsLoadingModel { get => _isLoadingModel; set => SetProperty(ref _isLoadingModel, value); }
    public bool ShowContextWarning { get => _showContextWarning; set => SetProperty(ref _showContextWarning, value); }
    public string ContextWarningText { get => _contextWarningText; set => SetProperty(ref _contextWarningText, value); }

    // ─── Strategic Compaction Toast (Sprint 2 #2) ─────────────────────
    // Nudges the user to /compact at logical breakpoints — BEFORE the
    // context-full alarm fires. Three triggers:
    //   1. 50+ tool calls accumulated since last compaction
    //   2. Context crosses 60% (proactive)
    //   3. Context crosses 75% (urgent — overlaps with ContextWarning)
    private bool _showCompactionToast;
    private string _compactionToastTitle = "";
    private string _compactionToastReason = "";
    private string _compactionToastSeverity = "info"; // info | warn | critical
    private int _toolActionsAtLastCompact;
    private int _toastSnoozeUntilTurn = -1;
    private string _lastFiredTrigger = ""; // dedupe so the same trigger doesn't re-fire each turn

    public bool ShowCompactionToast { get => _showCompactionToast; set => SetProperty(ref _showCompactionToast, value); }
    public string CompactionToastTitle { get => _compactionToastTitle; set => SetProperty(ref _compactionToastTitle, value); }
    public string CompactionToastReason { get => _compactionToastReason; set => SetProperty(ref _compactionToastReason, value); }

    /// <summary>"info" → cyan, "warn" → amber, "critical" → red. Used by XAML data triggers.</summary>
    public string CompactionToastSeverity { get => _compactionToastSeverity; set => SetProperty(ref _compactionToastSeverity, value); }

    public ICommand AcceptCompactionToastCommand { get; private set; } = null!;
    public ICommand SnoozeCompactionToastCommand { get; private set; } = null!;
    public ICommand DismissCompactionToastCommand { get; private set; } = null!;

    // ─── In-chat search (Ctrl+F) ───
    public string ChatSearchQuery
    {
        get => _chatSearchQuery;
        set
        {
            if (SetProperty(ref _chatSearchQuery, value))
                HighlightSearchMatches(value);
        }
    }
    public bool IsChatSearchVisible
    {
        get => _isChatSearchVisible;
        set => SetProperty(ref _isChatSearchVisible, value);
    }

    public ICommand ToggleChatSearchCommand { get; }
    public ICommand CloseChatSearchCommand { get; }

    public ICommand RefreshModelsCommand { get; }
    public ICommand SendMessageCommand { get; }
    public ICommand StopGenerationCommand { get; }
    public ICommand ClearChatCommand { get; }
    public ICommand NewSessionCommand { get; }
    /// <summary>New chat AND bring the Chat page forward — for callers outside the chat page (title bar, Ctrl+N,
    /// command palette). NewSessionCommand stays put: the Code page hosts the chat docked.</summary>
    public ICommand NewChatAndShowCommand { get; }
    public ICommand CompactCommand { get; }
    public ICommand CopyCodeCommand { get; }
    public ICommand ExecuteCodeBlockCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CloseFolderCommand { get; }
    public ICommand CloneRepoCommand { get; }
    public ICommand LoadSessionCommand { get; }
    public ICommand DeleteSessionCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand LoadSearchResultCommand { get; }
    public ICommand ReviewCodeCommand { get; }
    /// <summary>Hover "Retry" on an error bubble — parameter is that bubble.</summary>
    public ICommand RetryMessageCommand { get; }

    public event Action? ScrollToBottom;
    /// <summary>Raised when ChatVM needs to be the active view (e.g. user clicked chat history from another page).</summary>
    public event Action? NavigateToChatRequested;
    /// <summary>Raised when the workbench (Code Editor page) should come forward — after a project is
    /// opened/cloned, so the user lands in the IDE view with the file tree loaded.</summary>
    public event Action? NavigateToEditorRequested;

    public ChatViewModel(
        AiProviderManager providerManager,
        CodeAgentService agentService,
        CodeExecutionService codeExecutionService,
        AgentToolService agentToolService,
        FileSystemService fileSystemService,
        GitService gitService,
        GitHubService gitHubService,
        SettingsService settingsService,
        ContextMemoryService contextMemoryService,
        ChatPersistenceService persistenceService,
        HuggingFaceService huggingFaceService,
        LlamaInferenceService llamaService,
        GpuDetectionService gpuDetection,
        SkillService skillService,
        CostTrackingService costTracker,
        SessionMemoryService? sessionMemoryService = null,
        Services.Providers.LocalGgufProvider? localGgufProvider = null,
        AutonomousCodingService? autonomousService = null,
        DebugLogService? debugLog = null)
    {
        _debugLog = debugLog;
        _providerManager = providerManager;
        _agentService = agentService;
        _codeExecutionService = codeExecutionService;
        _agentToolService = agentToolService;
        _fileSystemService = fileSystemService;
        _gitService = gitService;
        _gitHubService = gitHubService;
        _settingsService = settingsService;
        _contextMemoryService = contextMemoryService;
        _persistenceService = persistenceService;
        _huggingFaceService = huggingFaceService;
        _llamaService = llamaService;
        _localGgufProvider = localGgufProvider;
        _gpuDetection = gpuDetection;
        _skillService = skillService;
        _costTracker = costTracker;
        _sessionMemoryService = sessionMemoryService;
        _autonomousService = autonomousService;
        _autonomousMode = settingsService.Settings.AutonomousLoopEnabled;
        if (_autonomousService != null)
            _autonomousService.OnPhase += msg => App.Current?.Dispatcher.Invoke(() =>
            {
                // A phase banner is the agent's own status, not something the model said: as Assistant it was
                // saved and sent back to the model as a turn, and the Steps switch could not hide it.
                Messages.Add(new ChatMessage { Role = MessageRole.AgentStatus, Content = msg });
                ScrollToBottom?.Invoke();
            });

        // Backing field, not the property: the property asks for confirmation when switched ON.
        _autoExecute = settingsService.Settings.AutoExecuteCode;
        _extendedThinkingEnabled = settingsService.Settings.ExtendedThinkingEnabled;
        _reasoningEffort = EffortLevels.Normalize(settingsService.Settings.ReasoningEffort);
        _showThinking = settingsService.Settings.ShowThinkingSteps;
        _showAgentInternals = settingsService.Settings.ShowAgentInternals;
        _statusText = Loc("chat.status.ready");

        RefreshModelsCommand = new RelayCommand(RefreshLocalModelsList);
        SendMessageCommand = new AsyncRelayCommand(SendMessage);
        StopGenerationCommand = new RelayCommand(StopGeneration);
        ClearChatCommand = new AsyncRelayCommand(ClearChatAsync);
        NewSessionCommand = new AsyncRelayCommand(NewSessionFromUserAsync);
        NewChatAndShowCommand = new AsyncRelayCommand(NewChatAndShowAsync);
        CompactCommand = new RelayCommand(CompactConversation);
        RetryMessageCommand = new AsyncRelayCommand<ChatMessage>(RetryFromAsync);
        AcceptCompactionToastCommand = new RelayCommand(() =>
        {
            CompactConversation();
            HideCompactionToast();
        });
        SnoozeCompactionToastCommand = new RelayCommand(() =>
        {
            _toastSnoozeUntilTurn = SessionTurnCount + 10;
            HideCompactionToast();
        });
        DismissCompactionToastCommand = new RelayCommand(HideCompactionToast);
        ToggleChatSearchCommand = new RelayCommand(() => { IsChatSearchVisible = !IsChatSearchVisible; if (!IsChatSearchVisible) ChatSearchQuery = ""; });
        CloseChatSearchCommand = new RelayCommand(() => { IsChatSearchVisible = false; ChatSearchQuery = ""; });
        CopyCodeCommand = new RelayCommand<string>(CopyCode);
        ExecuteCodeBlockCommand = new AsyncRelayCommand<CodeBlock>(ExecuteCodeBlock);
        OpenFolderCommand = new AsyncRelayCommand(OpenFolderAsync);
        CloseFolderCommand = new AsyncRelayCommand(CloseFolderAsync);
        CloneRepoCommand = new AsyncRelayCommand(CloneRepo);
        LoadSessionCommand = new AsyncRelayCommand<string>(LoadSessionAsync);
        DeleteSessionCommand = new AsyncRelayCommand<string>(DeleteSessionAsync);
        ClearSearchCommand = new RelayCommand(ClearHistorySearch);
        LoadSearchResultCommand = new AsyncRelayCommand<string>(LoadSessionAsync);
        ReviewCodeCommand = new AsyncRelayCommand(RunCodeReview);

        // Listen for tool execution events
        _agentService.OnToolExecuted += OnToolExecuted;
        _agentService.OnToolStarting += OnToolStarting;

        // Surface provider errors that are raised mid-stream (Ollama down, llama-server not running,
        // API auth/rate errors). These used to be raised into the void — NOTHING subscribed — so the
        // user just saw "no response" with no clue why. Show a clean, sanitized message and clear any
        // dangling thinking indicator.
        OpenBillingCommand = new RelayCommand(() =>
        {
            if (BillingUrl is { } url)
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { /* no browser registered */ }
        });
        UsageMeter.Updated += _ => App.Current?.Dispatcher.BeginInvoke(RefreshUsageStrip);
        // A provider/model change changes the context window: re-measure now, not on the next 5-s tick.
        _providerManager.OnProviderChanged += _ => App.Current?.Dispatcher.BeginInvoke(() => { RefreshUsageStrip(); UpdateContextInfo(); });
        _settingsService.SettingsChanged += () => App.Current?.Dispatcher.BeginInvoke(() => { RefreshUsageStrip(); UpdateContextInfo(); });
        RefreshUsageStrip();

        _providerManager.OnError += msg =>
        {
            App.Current?.Dispatcher.Invoke(() =>
            {
                string safe = SanitizeErrorMessage(string.IsNullOrWhiteSpace(msg) ? Loc("chat.err.generic") : msg);
                if (_activeAgentStatusMsg != null) { Messages.Remove(_activeAgentStatusMsg); _activeAgentStatusMsg = null; }
                // De-dupe: don't stack the identical error twice in a row.
                if (Messages.LastOrDefault() is { HasError: true } last && last.Content?.Contains(safe) == true)
                {
                    StatusText = safe;
                    return;
                }
                Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = $"**⚠ {safe}**", HasError = true });
                StatusText = safe;
                ScrollToBottom?.Invoke();
            });
        };

        // Live plan checklist (Claude Code-style TodoWrite pills). TodoChanged had ZERO subscribers —
        // the agent maintained a real plan in memory that no one could see. Render it as a single
        // checklist bubble that updates in place as items are added/completed.
        _agentToolService.TodoChanged += () =>
        {
            App.Current?.Dispatcher.BeginInvoke(() =>
            {
                var items = _agentToolService.TodoItems;
                if (items.Count == 0)
                {
                    if (_todoListMsg != null) { Messages.Remove(_todoListMsg); _todoListMsg = null; }
                    return;
                }
                var sb = new System.Text.StringBuilder($"📋 **{Loc("chat.plan")}**\n");
                foreach (var t in items)
                {
                    string mark = t.Status?.ToLowerInvariant() switch
                    {
                        "completed" or "done" => "✅",
                        "in_progress" or "active" => "🔄",
                        _ => "⬜",
                    };
                    sb.Append(mark).Append(' ').Append(t.Content).Append('\n');
                }
                if (_todoListMsg == null || !Messages.Contains(_todoListMsg))
                {
                    _todoListMsg = new ChatMessage { Role = MessageRole.System, Content = sb.ToString().TrimEnd() };
                    Messages.Add(_todoListMsg);
                }
                else
                {
                    _todoListMsg.Content = sb.ToString().TrimEnd();
                    // Keep the plan near the action: if newer messages buried it, move it to the bottom.
                    if (Messages.IndexOf(_todoListMsg) < Messages.Count - 3)
                    {
                        Messages.Remove(_todoListMsg);
                        Messages.Add(_todoListMsg);
                    }
                }
                ScrollToBottom?.Invoke();
            });
        };

        // Wire the human-in-the-loop permission prompt. Without this, PermAction.Ask resolved against a
        // null handler → silently DENIED → users were funneled into blanket-allowing "*" (which then
        // never prompts for rm -rf / force-push / overwrite). Now an Ask shows a real confirmation.
        _agentToolService.OnPermissionRequired += info =>
        {
            var dispatcher = App.Current?.Dispatcher;
            if (dispatcher == null) return Task.FromResult(false); // headless / no UI → fail safe (deny)

            // Inline, non-blocking prompt: render the request (with a preview diff for file edits) right in
            // the conversation, and resolve when the user clicks Allow/Deny. Replaces the old modal MessageBox
            // so the user can SEE exactly what's about to change before approving.
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var token = _cts?.Token ?? CancellationToken.None;
            CancellationTokenRegistration reg = default;

            var prompt = new ChatMessage
            {
                Role = MessageRole.PermissionRequest,
                Content = LocF("chat.perm.prompt", info.ToolName, info.Detail),
                DiffLines = info.Diff?.Lines,
                DiffStat = info.Diff?.Stat,
                PermissionCallback = allow => { tcs.TrySetResult(allow); reg.Dispose(); },
            };

            // If the user hits Stop (cancels the turn) while this prompt is still open, resolve it as DENIED so
            // the agent loop unwinds immediately — otherwise it parks forever on an unanswered prompt, leaving
            // IsGenerating stuck on (and a late "Allow" could even apply an edit after the turn was cancelled).
            // The task is resolved right here (the dispatcher may be busy); the prompt's buttons are retired on
            // the UI thread, where ResolvePermission's second resolve of the task is a no-op.
            reg = token.Register(() =>
            {
                if (tcs.TrySetResult(false))
                    dispatcher.InvokeAsync(() => prompt.ResolvePermission(PermissionOutcome.Cancelled));
                reg.Dispose();
            });

            dispatcher.InvokeAsync(() =>
            {
                Messages.Add(prompt);
                ScrollToBottom?.Invoke();
            });
            return tcs.Task;
        };

        // Update context info when messages change
        Messages.CollectionChanged += OnMessagesChanged;

        // Keep the sidebar-filtered VisibleSessions list in sync with Sessions automatically.
        // Any Add/Remove/Replace/Reset on Sessions triggers a rebuild filtered by current project.
        Sessions.CollectionChanged += (_, _) => RebuildVisibleSessions();

        // Periodic context / cost refresh every 5 seconds. (It also ran nvidia-smi here — on the UI thread,
        // blocking up to 3 s every 5 s — to fill GPU properties nothing bound; MainViewModel already polls the GPU.)
        _memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _memoryTimer.Tick += (_, _) =>
        {
            UpdateContextInfo();
            CostText = _costTracker.FormatCost();
            RefreshUsageStrip();
        };
        _memoryTimer.Start();

        // Auto-save every 10 seconds if dirty
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _autoSaveTimer.Tick += (_, _) => AutoSave();
        _autoSaveTimer.Start();

        // Reusable debounce timer for history search
        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ExecuteHistorySearch(_pendingSearchQuery);
        };

        // Listen for model status changes — they describe the in-process engine, so they only own the model
        // label while a local provider is answering.
        _llamaService.OnStatusChanged += status =>
            App.Current?.Dispatcher.Invoke(() => { if (ShowLocalModelPicker) LoadedModelName = status; });
        _llamaService.OnLoadingChanged += loading =>
            App.Current?.Dispatcher.Invoke(() => IsLoadingModel = loading);

        // Sync model selection when settings change (e.g. model loaded from Models tab). Only a changed model
        // path rescans the model folders: every UpdateSettings (any checkbox) used to rescan them on the UI thread.
        _settingsService.SettingsChanged += () =>
            App.Current?.Dispatcher.Invoke(() =>
            {
                if (!string.Equals(_syncedModelPath, _settingsService.Settings.SelectedModelPath, StringComparison.OrdinalIgnoreCase))
                    SyncModelSelectionFromSettings();
            });

        // Defer heavy I/O work to background — keep UI thread free during startup
        StatusText = Loc("chat.loading");
        _ = Task.Run(async () =>
        {
            // 1. Scan model directories (file I/O — can be slow on HDD/network drives)
            var models = _huggingFaceService.GetLocalModels();

            // 2. Load sessions from SQLite — first dropping chats that never got a message. Older builds
            //    saved every "New Chat" the moment it was opened, and those piled up in the sidebar.
            _persistenceService.DeleteEmptySessions();   // no-op when the DB fails its integrity check
            bool dbHealthy = _persistenceService.IsDatabaseHealthy;
            if (!dbHealthy) Debug.WriteLine("codex.db failed PRAGMA quick_check — empty-chat cleanup disabled");
            List<ChatSession> savedSessions;
            try { savedSessions = _persistenceService.LoadSessionList(); }
            catch { savedSessions = new(); }

            ChatSession? lastSession = null;
            if (savedSessions.Count > 0)
            {
                try { lastSession = _persistenceService.LoadSession(savedSessions.First().Id); }
                catch { }
            }

            // Push everything to UI thread in one batch
            App.Current?.Dispatcher.Invoke(() =>
            {
                // Models
                LocalModels.Clear();
                foreach (var m in models)
                    LocalModels.Add(m);
                if (_llamaService.LoadedModelPath != null)
                {
                    _selectedLocalModel = LocalModels.FirstOrDefault(m =>
                        string.Equals(m.LocalPath, _llamaService.LoadedModelPath, StringComparison.OrdinalIgnoreCase));
                }
                OnPropertyChanged(nameof(SelectedLocalModel));

                // Sessions
                foreach (var session in savedSessions)
                    Sessions.Add(session);

                if (lastSession != null && lastSession.Messages.Count > 0)
                {
                    CurrentSession = lastSession;
                    for (int i = 0; i < Sessions.Count; i++)
                    {
                        if (Sessions[i].Id == lastSession.Id) { Sessions[i] = lastSession; break; }
                    }
                    foreach (var msg in lastSession.Messages)
                        Messages.Add(msg);

                    // ─── Crash recovery: detect if last session was interrupted ───
                    var lastMsg = lastSession.Messages.LastOrDefault();
                    if (lastMsg is { Role: MessageRole.User })
                    {
                        // Last message is user's question with no response — app crashed during generation
                        var recoveryMsg = new ChatMessage
                        {
                            Role = MessageRole.System,
                            Content = $"*{Loc("chat.resume.note")}*",
                            IsInterrupted = true,
                        };
                        Messages.Add(recoveryMsg);
                        StatusText = Loc("chat.resume.status");
                    }
                    else
                    {
                        StatusText = LocF("chat.status.restored", lastSession.Title);
                    }
                }
                else
                {
                    // Nothing to restore, or the last session failed to load. Never adopt that session
                    // as the open chat: its messages are still in the DB but not on screen, and the next
                    // save would overwrite them with whatever the user types now.
                    NewSession();
                }
                _isDirty = false;
                if (!dbHealthy) StatusText = Loc("chat.dbDamaged");

                UpdateContextInfo();
            });

            // 3. Bring the persisted provider up. Nothing did this at startup, so after every restart an API
            //    provider sat "not configured" until the user re-picked it in Settings. Runs after the batch
            //    above: by then the main window exists and MainViewModel hears the result (IsModelLoaded).
            await _providerManager.InitializeActiveOnceAsync();
            App.Current?.Dispatcher.BeginInvoke(RefreshUsageStrip);
        });

        // Set initial loaded model name & auto-load if previously selected
        if (_llamaService.IsModelLoaded)
        {
            RefreshLoadedModelName();
        }
        else if (_settingsService.Settings.ActiveProvider == AiProviderType.Local
                 && !string.IsNullOrEmpty(_settingsService.Settings.SelectedModelPath)
                 && File.Exists(_settingsService.Settings.SelectedModelPath))
        {
            // Gated on the PERSISTED provider being Local. This auto-load used to run
            // whenever a GGUF path existed and then force-switched the active provider
            // to Local (below) — silently stomping whatever provider the user had
            // actually selected. On this machine a .gguf path always exists, so a
            // persisted ClaudeDev/Ollama/Anthropic choice never survived a restart.
            // If the user picked a non-local provider, we also should NOT spend 5 GB
            // of VRAM loading weights nobody asked for.
            LoadedModelName = LocF("chat.model.loadingName", _settingsService.Settings.SelectedModelName ?? "");
            string autoLoadPath = _settingsService.Settings.SelectedModelPath!;

            // Auto-load routes through LocalGgufProvider so architecture detection kicks in —
            // without this, a Gemma 4 / Llama 4 / Qwen 3 model silently fails with
            // UnsupportedModelArchitectureException and the UI stays at "Loading…" forever.
            // If the DI graph somehow didn't inject LocalGgufProvider (tests, unusual boot),
            // we fall back to the legacy direct LLamaSharp call.
            _ = Task.Run(async () =>
            {
                // Guard: don't auto-load if another load is already in progress
                if (_llamaService.IsLoading) return;
                if (_localGgufProvider != null && _localGgufProvider.IsLoading) return;

                try
                {
                    var progress = new Progress<string>(s =>
                        App.Current?.Dispatcher.Invoke(() =>
                        {
                            StatusText = s;
                            LoadedModelName = s;
                        }));

                    // Ensure provider is Local so status events flow to MainViewModel
                    if (_providerManager.ActiveProviderType != AiProviderType.Local)
                        await _providerManager.SwitchProviderAsync(AiProviderType.Local);

                    if (_localGgufProvider != null)
                    {
                        await _localGgufProvider.LoadModelAsync(autoLoadPath, progress);
                    }
                    else
                    {
                        await _llamaService.LoadModelAsync(autoLoadPath, progress);
                    }

                    App.Current?.Dispatcher.Invoke(() =>
                    {
                        StatusText = LocF("chat.model.ready", _settingsService.Settings.SelectedModelName ?? "");
                        SyncModelSelectionFromSettings();
                    });
                }
                catch (Exception ex)
                {
                    // The model slot is a 160-px label in the status bar: it gets a short sentence, the reason
                    // goes to the debug log (it used to print "Failed to auto-load: <raw exception>" there).
                    _debugLog?.Error("Chat", $"Auto-load of {autoLoadPath} failed", ex);
                    App.Current?.Dispatcher.Invoke(() =>
                    {
                        LoadedModelName = Loc("chat.model.autoLoadFailedShort");
                        StatusText = Loc("chat.model.autoLoadFailed");
                    });
                }
            });
        }
        else
        {
            RefreshLoadedModelName();
        }
    }

    // ─── Persistence ───

    private void AutoSave()
    {
        if (!_isDirty || CurrentSession == null) return;

        try
        {
            // Sync CurrentSession.Messages from the UI Messages collection
            // to ensure they are never out of sync when saving to DB
            CurrentSession.Messages = Messages.ToList();

            // Auto-generate title from first user message
            if (CurrentSession.Title == "New Chat" && CurrentSession.Messages.Count > 0)
            {
                CurrentSession.Title = ChatPersistenceService.GenerateTitle(CurrentSession.Messages);
                // Update in the Sessions list
                int idx = -1;
                for (int i = 0; i < Sessions.Count; i++)
                {
                    if (Sessions[i].Id == CurrentSession.Id) { idx = i; break; }
                }
                if (idx >= 0)
                {
                    Sessions[idx] = CurrentSession;
                }
            }

            CurrentSession.UpdatedAt = DateTime.Now;
            _persistenceService.SaveSession(CurrentSession);
            _isDirty = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AutoSave failed: {ex.Message}");
        }
    }

    /// <summary>Force save the current session immediately.</summary>
    public void SaveNow()
    {
        _isDirty = true;
        AutoSave();
    }

    private async Task LoadSessionAsync(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            System.Diagnostics.Debug.WriteLine("LoadSession: sessionId is null/empty");
            return;
        }

        if (CurrentSession?.Id == sessionId) return;

        // Navigate to Chat page if we're on a different page
        NavigateToChatRequested?.Invoke();

        // A reply still running here would keep writing into whichever chat is current once it finishes.
        if (!await EnsureIdleAsync()) return;

        // Force save current session before switching
        if (CurrentSession != null && Messages.Count > 0)
        {
            _isDirty = true;
            AutoSave();
        }
        // Leaving an untouched chat: drop it from the sidebar (and the DB, if a cleared chat was saved
        // empty) instead of leaving a stray "New Chat". The DB check keeps a session whose messages
        // exist but aren't on screen.
        else if (CurrentSession != null && !_persistenceService.SessionHasMessages(CurrentSession.Id))
        {
            string emptyId = CurrentSession.Id;
            _persistenceService.DeleteEmptySessions(emptyId);
            for (int i = Sessions.Count - 1; i >= 0; i--)
                if (Sessions[i].Id == emptyId) Sessions.RemoveAt(i);
        }

        // Load from database (full session with messages)
        var session = _persistenceService.LoadSession(sessionId);

        if (session == null)
        {
            // Fallback: the in-memory entry — a chat that was opened but never saved has no DB row yet.
            var inMemory = Sessions.FirstOrDefault(s => s.Id == sessionId);
            if (inMemory == null)
            {
                StatusText = Loc("chat.status.sessionNotFound");
                return;
            }

            // The sidebar's entries are metadata only. If the DB HAS messages for this chat, the load failed
            // (busy/damaged file): opening the empty copy would let the next autosave — which rewrites the
            // stored chat to what is on screen — erase them.
            if (inMemory.Messages.Count == 0 && _persistenceService.SessionHasMessages(sessionId))
            {
                StatusText = Loc("chat.status.sessionLoadFailed");
                return;
            }
            session = inMemory;
        }

        // Switch — set CurrentSession BEFORE clearing Messages to avoid dirty flag issues
        CurrentSession = session;

        // Temporarily stop dirty tracking during load
        Messages.CollectionChanged -= OnMessagesChanged;
        ResolvePendingPermissionPrompts();
        Messages.Clear();
        foreach (var msg in session.Messages)
            Messages.Add(msg);
        Messages.CollectionChanged += OnMessagesChanged;

        // Sync CurrentSession.Messages to point to the same data as what's displayed
        CurrentSession.Messages = Messages.ToList();

        _isDirty = false;

        // Update the Sessions list entry to reference the full loaded session
        for (int i = 0; i < Sessions.Count; i++)
        {
            if (Sessions[i].Id == sessionId)
            {
                Sessions[i] = session;
                break;
            }
        }

        StatusText = session.Messages.Count > 0
            ? LocF("chat.status.loaded", session.Title, session.Messages.Count)
            : LocF("chat.status.loadedEmpty", session.Title);

        UpdateContextInfo();
        ScrollToBottom?.Invoke();
    }

    private async Task DeleteSessionAsync(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        // Confirmation dialog for destructive operation
        if (!Confirm(Loc("chat.delete.confirm"), Loc("chat.delete.title"))) return;

        // Deleting the chat a reply is being written into: stop that reply first.
        if (CurrentSession?.Id == sessionId && !await EnsureIdleAsync()) return;

        // Delete from DB
        _persistenceService.DeleteSession(sessionId);

        for (int i = Sessions.Count - 1; i >= 0; i--)
        {
            if (Sessions[i].Id == sessionId)
            {
                Sessions.RemoveAt(i);
                break;
            }
        }

        // If we deleted the current session, create a new one
        if (CurrentSession?.Id == sessionId)
        {
            CurrentSession = null;
            ResolvePendingPermissionPrompts();
            Messages.Clear();
            NewSession();
        }

        StatusText = Loc("chat.status.deleted");
    }

    // ─── Turn ownership (L2: a reply must land in the chat that asked for it) ───

    /// <summary>Claim the chat for a turn: remembers which chat it belongs to and returns the handle that
    /// <see cref="EndTurn"/> completes once the turn has fully unwound.</summary>
    private TaskCompletionSource BeginTurn()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _turnSession = CurrentSession;
        _turnDone = done;
        return done;
    }

    private void EndTurn(TaskCompletionSource done)
    {
        if (ReferenceEquals(_turnDone, done))
        {
            _turnDone = null;
            _turnSession = null;
        }
        done.TrySetResult();
    }

    /// <summary>True when nothing is generating — or the user agreed to stop the running reply and it has
    /// unwound. Called before anything that swaps or empties the open chat.</summary>
    private async Task<bool> EnsureIdleAsync()
    {
        if (!IsGenerating) return true;
        if (!Confirm(Loc("chat.busy.stopFirst"), Loc("chat.busy.title"))) return false;

        StopGeneration();
        var done = _turnDone?.Task;
        if (done != null && await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(20))) != done)
        {
            StatusText = Loc("chat.busy.stillStopping");
            return false;
        }
        return !IsGenerating;
    }

    /// <summary>Add a message produced by the running turn to the chat that started it. If that chat is no
    /// longer on screen (a callback queued before a switch), the message goes into that chat's own list and is
    /// saved there, instead of into whatever chat is open now.</summary>
    private void PostTurnMessage(ChatSession? target, ChatMessage msg)
    {
        if (target == null || ReferenceEquals(target, CurrentSession))
        {
            Messages.Add(msg);
            return;
        }
        if (msg.Role is MessageRole.AgentStatus or MessageRole.PermissionRequest) return;   // transient rows
        target.Messages.Add(msg);
        _persistenceService.SaveSession(target);
    }

    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateContextInfo();
        _isDirty = true;
    }

    // ─── Context Tracking ───
    private void UpdateContextInfo()
    {
        try
        {
            var messages = Messages.ToList();
            // Measure what the MODEL is sent (after the last compaction marker, minus status/UI rows) against
            // the window of the model actually answering — not every row on screen against a local setting.
            var modelView = BuildModelHistory(messages);
            int used = _contextMemoryService.CalculateTotalTokens(modelView);
            int max = _contextMemoryService.MaxContextTokens;
            ContextUsagePercent = max > 0 ? Math.Min(100.0, (double)used / max * 100.0) : 0;
            ContextUsageText = $"{used:N0} / {max:N0}";

            // Session stats
            SessionTurnCount = messages.Count(m => m.Role == MessageRole.Assistant);
            SessionTotalTokens = messages.Where(m => m.Role == MessageRole.Assistant).Sum(m => m.TokenCount);

            // LED-style context level — 5 levels with labels
            if (ContextUsagePercent < 25)
            {
                ContextLevelLabel = Loc("chat.ctx.fresh");
                ContextLevelColor = "Green";
                ShowContextWarning = false;
                ContextWarningText = "";
            }
            else if (ContextUsagePercent < 50)
            {
                ContextLevelLabel = Loc("chat.ctx.ok");
                ContextLevelColor = "Teal";
                ShowContextWarning = false;
                ContextWarningText = "";
            }
            else if (ContextUsagePercent < 75)
            {
                ContextLevelLabel = Loc("chat.ctx.warm");
                ContextLevelColor = "Yellow";
                ShowContextWarning = false;
                ContextWarningText = "";
            }
            else if (ContextUsagePercent < 90)
            {
                ContextLevelLabel = Loc("chat.ctx.hot");
                ContextLevelColor = "Peach";
                ShowContextWarning = true;
                ContextWarningText = Loc("chat.ctx.hotWarning");
            }
            else
            {
                ContextLevelLabel = Loc("chat.ctx.full");
                ContextLevelColor = "Red";
                ShowContextWarning = true;
                ContextWarningText = Loc("chat.ctx.fullWarning");
            }

            // Sprint 2 #2 — Strategic Compaction Toast trigger check.
            // Runs AFTER ContextUsagePercent + SessionTurnCount are fresh.
            EvaluateCompactionTrigger(messages);
        }
        catch { /* ignore during initialization */ }
    }

    // ─── Strategic Compaction Toast logic ─────────────────────────────

    /// <summary>
    /// Decide whether to fire the strategic-compaction nudge. Three triggers,
    /// each fires AT MOST ONCE per crossing — once dismissed/snoozed, the
    /// same trigger won't re-spam every turn.
    /// </summary>
    private void EvaluateCompactionTrigger(List<ChatMessage> messages)
    {
        // Honor snooze
        if (SessionTurnCount < _toastSnoozeUntilTurn) return;
        // Honor user setting (default true)
        if (!_settingsService.Settings.EnableStrategicCompactionToast) return;
        // Don't fire while a toast is already showing
        if (_showCompactionToast) return;

        int toolActionsTotal = CountToolActionsSinceMarker(messages);
        int toolActionsSince = Math.Max(0, toolActionsTotal - _toolActionsAtLastCompact);

        // Trigger priority (highest fires first):
        //   1. Context ≥ 75% (urgent)
        //   2. Context ≥ 60% (proactive)
        //   3. 50+ tool calls since last compact
        string trigger = "";
        if (ContextUsagePercent >= 75 && _lastFiredTrigger != "ctx75")
        {
            trigger = "ctx75";
            ShowToast("critical", Loc("chat.toast.hotTitle"), LocF("chat.toast.hotReason", ContextUsagePercent.ToString("F0")));
        }
        else if (ContextUsagePercent >= 60 && _lastFiredTrigger != "ctx60" && _lastFiredTrigger != "ctx75")
        {
            trigger = "ctx60";
            ShowToast("warn", Loc("chat.toast.warmTitle"), LocF("chat.toast.warmReason", ContextUsagePercent.ToString("F0")));
        }
        else if (toolActionsSince >= 50 && _lastFiredTrigger != "tools50")
        {
            trigger = "tools50";
            ShowToast("info", Loc("chat.toast.toolsTitle"), LocF("chat.toast.toolsReason", toolActionsSince));
        }

        if (!string.IsNullOrEmpty(trigger))
        {
            _lastFiredTrigger = trigger;
        }
        else if (ContextUsagePercent < 55)
        {
            // Reset the trigger memory once the user dropped well below 60%,
            // so the next crossing fires the toast again.
            _lastFiredTrigger = "";
        }
    }

    private void ShowToast(string severity, string title, string reason)
    {
        CompactionToastSeverity = severity;
        CompactionToastTitle = title;
        CompactionToastReason = reason;
        ShowCompactionToast = true;
    }

    private void HideCompactionToast()
    {
        ShowCompactionToast = false;
        // Reset baseline so the next 50-tool-call cycle starts fresh
        // (whether the user accepted, snoozed, or dismissed)
        _toolActionsAtLastCompact = CountToolActionsSinceMarker(Messages);
    }

    private static int CountToolActionsSinceMarker(IList<ChatMessage> messages)
    {
        int count = 0;
        for (int i = messages.Count - 1; i >= 0 && messages[i].Role != MessageRole.ContextSummary; i--)
            if (messages[i].Role == MessageRole.ToolAction) count++;
        return count;
    }

    // ─── Open / Close Project Folder ───
    private async Task OpenFolderAsync()
    {
        // Switching the project mid-reply would move the agent's file tools to another folder.
        if (!await EnsureIdleAsync()) return;

        // Use the owner-aware COM picker (it passes the MainWindow hwnd). A plain
        // Microsoft.Win32.OpenFolderDialog with NO owner opened its modal BEHIND our borderless
        // (WindowStyle=None) main window — the dialog was invisible and the app looked frozen, so
        // "Open Folder did nothing". FolderPicker is already what ModelManager/PluginManager use.
        string? folder = CluadeX.Services.Helpers.FolderPicker.ShowDialog(Loc("chat.folder.pickTitle"), WorkingDirectory);
        if (!string.IsNullOrEmpty(folder))
            SetWorkingDirectory(folder);
    }

    public void SetWorkingDirectory(string path)
    {
        _fileSystemService.WorkingDirectory = path;
        WorkingDirectory = path;
        OnPropertyChanged(nameof(ProjectName));

        // Invalidate system prompt cache (project context changed)
        _agentService.InvalidateSystemPromptCache();

        // Project-scoped skills live under the PROJECT folder, but the skill list is cached on
        // first use — without this, opening or switching a project never picked them up (and a
        // previous project's skills stayed advertised).
        _skillService.ReloadSkills();

        // Auto-enable agentic mode when a project is open
        AgenticMode = true;

        StatusText = LocF("chat.status.project", ProjectName);

        // Detect git info
        _ = DetectGitInfoAsync();

        // Add system message
        Messages.Add(new ChatMessage
        {
            Role = MessageRole.System,
            Content = "\U0001F4C1 " + LocF("chat.folder.opened", path),
        });
        ScrollToBottom?.Invoke();

        // Land the user in the workbench: opening a project in an IDE should show the project.
        NavigateToEditorRequested?.Invoke();
    }

    private async Task DetectGitInfoAsync()
    {
        try
        {
            if (await _gitService.IsGitRepoAsync())
            {
                var info = await _gitService.GetInfoAsync();
                App.Current?.Dispatcher.Invoke(() =>
                {
                    GitBranch = info.CurrentBranch;
                });
            }
            else
            {
                App.Current?.Dispatcher.Invoke(() => GitBranch = "");
            }
        }
        catch
        {
            App.Current?.Dispatcher.Invoke(() => GitBranch = "");
        }
    }

    private async Task CloneRepo()
    {
        // Cloning ends by switching the project — not while a reply is still using the current one.
        if (!await EnsureIdleAsync()) return;

        // 1) Ask for the repo URL.
        string? url = CluadeX.Services.Helpers.InputDialog.Show(
            Loc("chat.clone.title"),
            Loc("chat.clone.prompt"),
            placeholder: "https://github.com/owner/repo");
        if (string.IsNullOrWhiteSpace(url)) return;
        url = url.Trim();
        // A URL can carry a token (https://user:TOKEN@github.com/…): never echo it into the transcript.
        string shownUrl = RedactUrlCredentials(url);

        // Reject anything that isn't a plain repo URL — a quote/newline could break the
        // quoting in `git clone -- "<url>"`, and a leading '-' is an arg-injection smell.
        if (url.Contains('"') || url.Contains('\n') || url.Contains('\r') || url.StartsWith("-"))
        {
            Messages.Add(new ChatMessage { Role = MessageRole.System, Content = "⚠ " + LocF("chat.clone.badUrl", shownUrl) });
            ScrollToBottom?.Invoke();
            return;
        }

        string repoName = DeriveRepoFolderName(url);
        if (string.IsNullOrWhiteSpace(repoName))
        {
            Messages.Add(new ChatMessage { Role = MessageRole.System, Content = "⚠ " + LocF("chat.clone.noName", shownUrl) });
            ScrollToBottom?.Invoke();
            return;
        }

        // 2) Choose where to put it (default to the last working dir's parent, else Documents).
        string? initial = !string.IsNullOrEmpty(WorkingDirectory)
            ? (Path.GetDirectoryName(WorkingDirectory) ?? WorkingDirectory)
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string? parent = CluadeX.Services.Helpers.FolderPicker.ShowDialog(LocF("chat.clone.pickParent", repoName), initial);
        if (string.IsNullOrWhiteSpace(parent)) return;

        string targetDir = Path.Combine(parent, repoName);
        if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
        {
            if (Confirm(LocF("chat.clone.exists", targetDir), Loc("chat.clone.existsTitle")))
                SetWorkingDirectory(targetDir);
            return;
        }

        // 3) Clone with a live status line.
        Messages.Add(new ChatMessage
        {
            Role = MessageRole.System,
            Content = "⬇ " + LocF("chat.clone.cloning", shownUrl, targetDir),
        });
        ScrollToBottom?.Invoke();
        StatusText = LocF("chat.clone.status", repoName);

        GitResult result;
        try
        {
            result = await _gitService.CloneAsync(url, targetDir);
        }
        catch (Exception ex)
        {
            _debugLog?.Error("Chat", "git clone threw", ex);
            result = new GitResult { Success = false, Error = "" };
        }

        if (result.Success)
        {
            StatusText = LocF("chat.clone.done", repoName);
            // Opening the folder emits its own "Opened project" system message + enables agent mode.
            SetWorkingDirectory(targetDir);
        }
        else
        {
            // git's own last line ("Repository not found.", "Authentication failed …") is the useful part; the
            // full stderr goes to the log. Both are redacted — git echoes the URL, token included.
            string raw = (result.Error ?? result.Output ?? "").Trim();
            _debugLog?.Warn("Chat", "git clone failed: " + RedactUrlCredentials(raw));
            string reason = RedactUrlCredentials(raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault() ?? "");
            if (reason.Length > 300) reason = reason[..300] + "…";
            Messages.Add(new ChatMessage
            {
                Role = MessageRole.System,
                Content = "❌ " + Loc("chat.clone.failed") + (reason.Length > 0 ? $"\n`{reason}`" : ""),
            });
            ScrollToBottom?.Invoke();
            StatusText = Loc("chat.clone.failedStatus");
        }
    }

    /// <summary>"https://user:TOKEN@host/…" → "https://***@host/…" (anywhere in the text).</summary>
    public static string RedactUrlCredentials(string text)
        => string.IsNullOrEmpty(text) ? text
            : System.Text.RegularExpressions.Regex.Replace(text, @"(?<=[a-zA-Z][a-zA-Z0-9+.-]*://)[^/@\s""']+@", "***@");

    /// <summary>
    /// Pull the repo folder name out of a git URL. Handles https://host/owner/repo(.git),
    /// git@host:owner/repo.git, and bare owner/repo forms.
    /// </summary>
    private static string DeriveRepoFolderName(string url)
    {
        string s = url.Trim().TrimEnd('/');
        // git@github.com:owner/repo.git → take the part after the last '/' or ':'
        int slash = s.LastIndexOfAny(new[] { '/', ':' });
        string last = slash >= 0 ? s[(slash + 1)..] : s;
        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            last = last[..^4];
        // Strip anything unsafe for a folder name.
        foreach (char c in Path.GetInvalidFileNameChars())
            last = last.Replace(c, '-');
        return last.Trim();
    }

    private async Task CloseFolderAsync()
    {
        if (!await EnsureIdleAsync()) return;

        _fileSystemService.WorkingDirectory = string.Empty;
        WorkingDirectory = string.Empty;
        AgenticMode = false;
        OnPropertyChanged(nameof(ProjectName));
        // Mirror SetWorkingDirectory: the closed project's branch badge, its skills and the system prompt that
        // described it all stayed behind before.
        GitBranch = "";
        _agentService.InvalidateSystemPromptCache();
        _skillService.ReloadSkills();
        StatusText = Loc("chat.status.ready");
    }

    /// <summary>New chat from the chat's own buttons: stops a running reply first. Stays on the current page —
    /// the Code page hosts this same chat docked beside the editor.</summary>
    private async Task NewSessionFromUserAsync()
    {
        if (!await EnsureIdleAsync()) return;
        NewSession();
    }

    /// <summary>New chat from OUTSIDE the chat (title bar, Ctrl+N, palette): as above, then brings the Chat page
    /// forward — from another page the title-bar button created the chat out of sight, so it looked like it
    /// did nothing.</summary>
    private async Task NewChatAndShowAsync()
    {
        if (!await EnsureIdleAsync()) return;
        NewSession();
        NavigateToChatRequested?.Invoke();
    }

    /// <summary>
    /// Promoted from private to public so the McpHostService can spawn a
    /// fresh visible session each time ObsidianX delegates a task. UI-thread
    /// only — callers from background threads must Dispatcher.Invoke.
    /// Does not stop a running turn: UI paths go through NewSessionFromUserAsync, MCP waits for idle.
    /// </summary>
    public void NewSession()
    {
        string projectPath = _fileSystemService.HasWorkingDirectory ? _fileSystemService.WorkingDirectory : "";

        // Already on an untouched chat → reuse it instead of stacking another empty "New Chat".
        if (CurrentSession != null && Messages.Count == 0
            && !_persistenceService.SessionHasMessages(CurrentSession.Id))
        {
            CurrentSession.ProjectPath = projectPath;
            int at = -1;
            for (int i = 0; i < Sessions.Count; i++)
                if (Sessions[i].Id == CurrentSession.Id) { at = i; break; }
            if (at < 0) Sessions.Insert(0, CurrentSession);
            else if (at > 0) Sessions.Move(at, 0);
            RebuildVisibleSessions();   // the project tag may have changed
            StatusText = Loc("chat.status.ready");
            return;
        }

        // Save current session before creating new
        if (CurrentSession != null && Messages.Count > 0)
        {
            _isDirty = true;
            AutoSave();

            // Extract durable memories from the finishing session (fire-and-forget).
            // Disabled by default — user opts in via settings.
            _sessionMemoryService?.ExtractInBackground(CurrentSession.Messages);
        }

        // Create new session — tag with the current project path so the sidebar can
        // filter to "just this project" instead of mixing every project's sessions together.
        var session = new ChatSession { ProjectPath = projectPath };
        CurrentSession = session;
        ResolvePendingPermissionPrompts();
        Messages.Clear();
        Sessions.Insert(0, session);
        _isDirty = false;
        RebuildVisibleSessions();

        // Not written to the DB yet: AutoSave stores it once it has a message, so a chat that is opened
        // and never used leaves nothing behind (LoadSession falls back to the in-memory entry).
        StatusText = Loc("chat.status.ready");
    }

    // ════════════════════════════════════════════════════════════════════
    //  MCP-driven external runs (called by McpHostService → ToolDispatcher)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Run a coding task that arrived over the named-pipe MCP host. Spawns
    /// a NEW visible chat session tagged with the caller's task id, posts
    /// the spec as the user message, runs the agentic loop, and posts the
    /// agent's reply. The transcript persists in codex.db like any normal
    /// session — user can scroll back to it anytime.
    ///
    /// All UI-touching work happens on the dispatcher thread. Network/agent
    /// work is awaited so the caller (the named-pipe handler) can serialise
    /// the final response back to ObsidianX.
    /// </summary>
    public async Task<string> RunMcpTaskAsync(
        string taskId,
        string spec,
        IReadOnlyList<string>? lessons,
        IReadOnlyList<string>? contextFiles,
        string? workingDirectory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return "(empty spec — nothing to do)";

        // Compose the prompt. Inject "lessons learned" verbatim so the
        // agent sees them as system-level guidance from the orchestrator.
        var sb = new StringBuilder();
        sb.AppendLine(spec);
        if (lessons is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Lessons learned from previous reviews");
            sb.AppendLine("Pay attention to these — they describe mistakes the senior reviewer flagged before:");
            foreach (var l in lessons)
                sb.AppendLine($"- {l}");
        }
        if (contextFiles is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Files in scope");
            foreach (var f in contextFiles)
                sb.AppendLine($"- `{f}`");
        }
        string prompt = sb.ToString();

        // Claim the chat. If the user's own reply is still running, WAIT for it: this used to switch to a new
        // session underneath it, and that reply's tool rows and answer then landed in the MCP task's chat.
        // Check-and-claim happens in ONE dispatcher call, so the user can't start a turn in between.
        var dispatcher = Application.Current.Dispatcher;
        TaskCompletionSource? turn = null;
        ChatSession? taskSession = null;
        CancellationTokenSource? linked = null;
        List<ChatMessage> historySnapshot = new();
        while (true)
        {
            Task? busy = await dispatcher.InvokeAsync<Task?>(() =>
            {
                if (IsGenerating) return _turnDone?.Task ?? Task.Delay(250);

                // 1. A fresh visible session so the user's current chat isn't disturbed. Tagged with the
                //    caller's id so the sidebar filter and history are easy to find later.
                NewSession();
                if (CurrentSession != null)
                {
                    CurrentSession.Title = $"[ObsidianX] {taskId}";
                    CurrentSession.ProjectPath = $"obsidianx-task::{taskId}";
                }
                // If the caller pinned a working directory, switch to it so all
                // file ops in this session are scoped correctly.
                if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
                    SetWorkingDirectory(workingDirectory!);

                // 2. The spec as a user message, visible exactly like a human-typed prompt.
                var userMsg = new ChatMessage { Role = MessageRole.User, Content = prompt };
                Messages.Add(userMsg);
                _isDirty = true;
                ScrollToBottom?.Invoke();

                // 3. Own the chat for the run. Stop works on it (the button showed during MCP runs but
                //    cancelled nothing — there was no _cts), and IsGenerating blocks a concurrent user turn.
                IsGenerating = true;
                CanSend = false;
                turn = BeginTurn();
                taskSession = CurrentSession;
                linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _cts = linked;
                historySnapshot = BuildModelHistory(Messages, before: userMsg, includeTools: true);
                return null;
            });
            if (busy == null) break;
            await Task.WhenAny(busy, Task.Delay(Timeout.Infinite, ct));
            ct.ThrowIfCancellationRequested();
        }

        // 4. Run the agentic loop. Use the same CodeAgentService instance
        //    the interactive chat uses — same provider, same tool budget,
        //    same hooks/permissions/skills.
        try
        {
            AgentLoopResult agentResult;
            try
            {
                agentResult = await _agentService.ExecuteAgenticAsync(historySnapshot, prompt, ct: linked!.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Stopped from the chat's Stop button (the caller's token is still live).
                await dispatcher.InvokeAsync(() => PostTurnMessage(taskSession,
                    new ChatMessage { Role = MessageRole.System, Content = $"*{Loc("chat.interruptedNote")}*", IsInterrupted = true }));
                return "Stopped by the user in CluadeX before the task finished.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _debugLog?.Error("Chat", $"MCP task {taskId}: agent loop failed", ex);
                string shown = $"❌ {Loc("chat.err.agentLoop")}";
                await dispatcher.InvokeAsync(() => PostTurnMessage(taskSession,
                    new ChatMessage { Role = MessageRole.Assistant, Content = shown, HasError = true }));
                return $"Agent loop failed: {SanitizeErrorMessage(ex.Message)}";   // to the MCP caller, not the screen
            }

            // 5. Post the agent's final response to the chat so the user sees
            //    what was returned to ObsidianX, then persist + return.
            string finalText = agentResult.FinalResponse ?? "(agent loop returned no text)";
            await dispatcher.InvokeAsync(() =>
            {
                PostTurnMessage(taskSession, new ChatMessage { Role = MessageRole.Assistant, Content = finalText });
                _isDirty = true;
                SaveNow();
            });
            return finalText;
        }
        finally
        {
            // Marshal the reset to the UI thread; SetProperty raises
            // PropertyChanged which any binding consumers expect on the
            // dispatcher.
            // Only release what this run claimed: cancelled while still WAITING for the user's own turn, it owns
            // nothing — resetting IsGenerating/_cts then would knock over the user's running reply.
            void Release()
            {
                IsGenerating = false;
                CanSend = true;
                if (ReferenceEquals(_cts, linked)) _cts = null;
                linked?.Dispose();
                EndTurn(turn!);
            }
            try
            {
                if (turn != null)
                {
                    if (dispatcher.CheckAccess()) Release();
                    else await dispatcher.InvokeAsync(Release);
                }
            }
            catch { /* app closing — nothing to do */ }
        }
    }

    // ─── Compaction ───

    /// <summary>What the model is told in place of the messages above a compaction marker.</summary>
    private const string CompactionPreamble =
        "[Summary of the earlier part of this conversation — those messages were compacted out of your context. "
        + "Treat it as background; the conversation continues below.]\n";

    /// <summary>
    /// The conversation as the MODEL should receive it — never the raw transcript:
    /// starts after the last <see cref="MessageRole.ContextSummary"/> marker (whose summary is sent first, as a
    /// user turn so every provider and both agent paths keep it), stops before <paramref name="before"/> (the
    /// message being answered, which travels separately), and keeps only User, Assistant and — for tool-using
    /// modes — ToolAction rows. Status lines, permission prompts, UI notices and error bubbles are dropped:
    /// they are things the app said, not the model, and sending them back made the model answer them.
    /// </summary>
    public static List<ChatMessage> BuildModelHistory(IEnumerable<ChatMessage> transcript, ChatMessage? before = null, bool includeTools = true)
    {
        var all = transcript as IList<ChatMessage> ?? transcript.ToList();
        int end = all.Count;
        if (before != null)
        {
            for (int i = 0; i < all.Count; i++)
                if (ReferenceEquals(all[i], before)) { end = i; break; }
        }

        int start = 0;
        ChatMessage? marker = null;
        for (int i = end - 1; i >= 0; i--)
        {
            if (all[i].Role == MessageRole.ContextSummary) { marker = all[i]; start = i + 1; break; }
        }

        var result = new List<ChatMessage>();
        if (marker != null && !string.IsNullOrWhiteSpace(marker.Content))
            result.Add(new ChatMessage { Role = MessageRole.User, Content = CompactionPreamble + marker.Content, Timestamp = marker.Timestamp });

        for (int i = start; i < end; i++)
        {
            var m = all[i];
            switch (m.Role)
            {
                case MessageRole.User:
                    result.Add(m);
                    break;
                case MessageRole.Assistant when !m.HasError && !ChatMessage.IsLegacyStatusLine(m.Content):
                    // Replies saved by older builds can still carry <thinking> spans inline.
                    result.Add(m.Content.Contains("<thinking>", StringComparison.Ordinal)
                        ? new ChatMessage { Role = MessageRole.Assistant, Content = StripThinkingSpans(m.Content), Timestamp = m.Timestamp }
                        : m);
                    break;
                case MessageRole.ToolAction when includeTools:
                    result.Add(m);
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// "Compact now" (button, toast, /compact): shrink what the MODEL is sent, never the transcript. A
    /// ContextSummary marker is inserted before the last ~10 model-visible messages; everything above it is
    /// replaced, in the requests, by the marker's summary. It used to delete every message but the last 10 —
    /// from the screen AND (via the next save) from the database — with no confirmation and no summary.
    /// Nothing is deleted now, so there is nothing to confirm.
    /// </summary>
    private void CompactConversation()
    {
        const int keepRecent = 10;

        int lastMarker = -1;
        for (int i = Messages.Count - 1; i >= 0; i--)
            if (Messages[i].Role == MessageRole.ContextSummary) { lastMarker = i; break; }

        var visible = new List<int>();
        for (int i = lastMarker + 1; i < Messages.Count; i++)
            if (Messages[i].Role is MessageRole.User or MessageRole.Assistant or MessageRole.ToolAction)
                visible.Add(i);

        if (visible.Count <= keepRecent)
        {
            StatusText = Loc("chat.compact.nothing");
            ShowCompactionToast = false;
            return;
        }

        // Cut before the last keepRecent model-visible messages — moved back to the user message that
        // started that exchange, so a kept tool row is never separated from the request that caused it.
        int cut = visible[visible.Count - keepRecent];
        for (int i = cut; i > lastMarker + 1; i--)
        {
            if (Messages[i].Role == MessageRole.User) { cut = i; break; }
        }
        if (cut <= lastMarker + 1)
        {
            StatusText = Loc("chat.compact.nothing");
            ShowCompactionToast = false;
            return;
        }

        var toSummarize = BuildModelHistory(Messages.Take(cut).ToList());
        string? previousSummary = lastMarker >= 0 ? Messages[lastMarker].Content : null;
        // The previous summary is already the first entry of toSummarize (as a user turn) — summarise the rest
        // and carry the old summary forward verbatim.
        if (previousSummary != null && toSummarize.Count > 0 && toSummarize[0].Content.StartsWith(CompactionPreamble, StringComparison.Ordinal))
            toSummarize.RemoveAt(0);

        var marker = new ChatMessage
        {
            Role = MessageRole.ContextSummary,
            Content = _contextMemoryService.BuildCompactionSummary(toSummarize, previousSummary),
            Timestamp = DateTime.Now,
        };
        Messages.Insert(cut, marker);
        _isDirty = true;
        SaveNow();

        StatusText = LocF("chat.compact.done", toSummarize.Count);

        // Reset Sprint 2 #2 trigger state so the strategic toast can fire
        // again the next time the conversation grows past a threshold.
        _toolActionsAtLastCompact = CountToolActionsSinceMarker(Messages);
        _lastFiredTrigger = "";
        ShowCompactionToast = false;

        UpdateContextInfo();
    }

    private int _searchMatchCount;
    public int SearchMatchCount { get => _searchMatchCount; set => SetProperty(ref _searchMatchCount, value); }

    private void HighlightSearchMatches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchMatchCount = 0;
            return;
        }
        int count = 0;
        foreach (var msg in Messages)
        {
            if (msg.Content?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                count++;
        }
        SearchMatchCount = count;
    }

    // ─── Send Message ───
    private async Task SendMessage()
    {
        string input = UserInput?.Trim() ?? "";
        if (string.IsNullOrEmpty(input) || IsGenerating) return;

        // ─── Built-in slash commands ───
        // Handled BEFORE the provider check: they act on the chat itself and need no model. Behind the check,
        // /compact, /clear and /new were refused with "not configured" whenever the provider wasn't up.
        if (input.Equals("/compact", StringComparison.OrdinalIgnoreCase))
        {
            UserInput = string.Empty;
            CompactConversation();
            return;
        }
        if (input.Equals("/clear", StringComparison.OrdinalIgnoreCase))
        {
            UserInput = string.Empty;
            await ClearChatAsync();
            return;
        }
        if (input.Equals("/new", StringComparison.OrdinalIgnoreCase))
        {
            UserInput = string.Empty;
            NewSession();
            return;
        }

        if (!await EnsureProviderReadyAsync()) return;

        // ─── Slash Command / Skill Interception ───
        string skillPromptOverride = "";
        SkillDefinition? activeSkill = null;
        if (input.StartsWith("/"))
        {
            var parts = input.TrimStart('/').Split(' ', 2);
            string skillName = parts[0];
            string skillArgs = parts.Length > 1 ? parts[1] : "";
            var skill = _skillService.GetSkillByName(skillName);

            if (skill != null && skill.UserInvocable)
            {
                activeSkill = skill;
                // Replace input with skill prompt + user args
                skillPromptOverride = skill.PromptContent;
                if (!string.IsNullOrEmpty(skillArgs))
                    skillPromptOverride += $"\n\nUser arguments: {skillArgs}";
                input = skillPromptOverride;
            }
            else if (skill != null && !skill.UserInvocable)
            {
                // Skill exists but not user-invocable — give feedback
                StatusText = LocF("chat.skill.notInvocable", skillName);
                return;
            }
            else if (skill == null && skillName.Length > 0)
            {
                // Skill not found — show available skills
                var available = _skillService.GetAllSkills()
                    .Where(s => s.UserInvocable)
                    .Select(s => $"/{s.Name}")
                    .ToList();
                StatusText = available.Count > 0
                    ? LocF("chat.skill.unknown", skillName, string.Join(", ", available))
                    : LocF("chat.skill.unknownNone", skillName);
                return;
            }
        }

        var userMsg = new ChatMessage { Role = MessageRole.User, Content = input };
        Messages.Add(userMsg);
        UserInput = string.Empty;
        await RunTurnAsync(userMsg, input, activeSkill, isSkill: !string.IsNullOrEmpty(skillPromptOverride));
    }

    /// <summary>True when the active provider can answer. Waits for (or retries) its initialise — after a
    /// restart an API provider was never initialised and every first message failed. When it still can't,
    /// posts one honest bubble: no model loaded, or the provider's own reason (bad key, no credit, …).</summary>
    private async Task<bool> EnsureProviderReadyAsync()
    {
        var provider = _providerManager.ActiveProvider;
        if (provider.IsReady) return true;

        StatusText = LocF("chat.status.connecting", provider.DisplayName);
        bool ready;
        try { ready = await _providerManager.EnsureActiveReadyAsync(); }
        catch (Exception ex)
        {
            _debugLog?.Warn("Chat", $"{provider.DisplayName}: readiness check failed", ex);
            ready = false;
        }
        if (ready)
        {
            StatusText = Loc("chat.status.ready");
            RefreshUsageStrip();
            return true;
        }

        provider = _providerManager.ActiveProvider;
        bool local = _providerManager.ActiveProviderType is AiProviderType.Local or AiProviderType.LlamaServer;
        string reason = SanitizeErrorMessage(string.IsNullOrWhiteSpace(provider.StatusMessage) ? "" : provider.StatusMessage);
        Messages.Add(new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = local ? Loc("chat.err.noModel") : LocF("chat.err.providerNotReady", provider.DisplayName, reason),
            HasError = true,
        });
        StatusText = local ? Loc("chat.status.noModel") : LocF("chat.status.providerNotReady", provider.DisplayName);
        ScrollToBottom?.Invoke();
        return false;
    }

    private const int MaxAutoRetries = 3;

    /// <summary>
    /// Run one turn for <paramref name="userMsg"/> (already in the transcript). Auto-retries a TRANSIENT failure
    /// (429/5xx/timeout/dropped connection — decided from the status code and exception type) up to 3 times
    /// inside the same turn: same user message, same history snapshot. The old retry re-sent the message
    /// through SendMessage, adding the user bubble (and history entry) again on every attempt, chose retries
    /// by substring — so "prompt is too long: 215000 tokens" matched "500" and was resent 3× with a growing
    /// prompt — and kept counting down after Stop.
    /// </summary>
    private async Task RunTurnAsync(ChatMessage userMsg, string input, SkillDefinition? activeSkill, bool isSkill)
    {
        var turn = BeginTurn();
        var session = CurrentSession;
        CanSend = false;
        IsGenerating = true;

        // ─── Session resume: persist transcript BEFORE API call ───
        // If the app crashes mid-generation, the conversation up to this point is saved.
        SaveNow();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Tools are worth running the agentic loop for in TWO cases: a project
        // is open (file/build tools, gated by the user's AgenticMode switch),
        // or an MCP server is connected — those need no project at all.
        //
        // Only the first case used to route here, so a plain chat with the
        // brain connected got ZERO tools while the system prompt still told
        // the model it had them. That contradiction is what makes a weak model
        // narrate ("I will use agent_inbox — go ahead") instead of calling, and
        // it is why the agent bus sat at calls: 0. There is also no switch to
        // flip in this case: AgenticMode is auto-enabled by opening a project,
        // so with no project it is always false.
        bool mcpOnlyTools = !HasProject && _agentToolService.HasProjectIndependentTools;
        Func<List<ChatMessage>, CancellationToken, Task> run;
        bool withTools = true;
        if (isSkill && HasProject)                                            // skills always run in agentic mode
            run = (h, c) => RunAgentic(input, h, c);
        else if (AutonomousMode && HasProject && _autonomousService != null)
            run = (h, c) => RunAutonomous(input, h, c);
        else if ((AgenticMode && HasProject) || mcpOnlyTools)
            run = (h, c) => RunAgentic(input, h, c);
        else if (AutoExecute)
        { run = (h, c) => RunWithAutoExecution(input, h, c); withTools = false; }
        else
        { run = (h, c) => RunStreaming(input, h, c); withTools = false; }

        // Snapshot taken once, before the user message: every retry sends the same history.
        var history = BuildModelHistory(Messages, before: userMsg, includeTools: withTools);

        string initVerb = _agentService.GetRandomSpinnerVerb();
        StatusText = $"{initVerb}...";
        _lastStatusBase = $"{initVerb}...";
        _generationStopwatch.Restart();
        StartElapsedTimer();

        try
        {
            for (int attempt = 1; ; attempt++)
            {
                // Inline "working" indicator (Claude Code-style). An AgentStatus row: it is not something the
                // model said, so it is never saved or sent back — and it stays visible while it is live even
                // with Steps off (only finished status rows are hidden).
                ShowWaitingIndicator(initVerb);
                try
                {
                    // Set AllowedTools constraint for skill execution
                    if (activeSkill?.AllowedTools is { Count: > 0 })
                        _agentToolService.ActiveSkillAllowedTools = activeSkill.AllowedTools;

                    await run(history, ct);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    StatusText = Loc("chat.status.cancelled");
                    RemoveActiveStatus();

                    // Mark the last assistant message as interrupted (don't delete what it said)
                    var lastAssistant = Messages.LastOrDefault(m => m.Role == MessageRole.Assistant && m.IsStreaming);
                    if (lastAssistant != null)
                    {
                        lastAssistant.IsStreaming = false;
                        lastAssistant.IsInterrupted = true;
                        if (string.IsNullOrWhiteSpace(lastAssistant.Content))
                        {
                            Messages.Remove(lastAssistant);
                            lastAssistant = null;
                        }
                    }
                    if (lastAssistant == null)
                    {
                        // A notice, not an assistant turn — it must not be sent back to the model.
                        PostTurnMessage(session, new ChatMessage
                        {
                            Role = MessageRole.System,
                            Content = $"*{Loc("chat.interruptedNote")}*",
                            IsInterrupted = true,
                        });
                    }
                    break;
                }
                catch (Exception ex)
                {
                    RemoveActiveStatus();
                    FinalizeDanglingBubbles();

                    var errorMsg = new ChatMessage
                    {
                        Role = MessageRole.Assistant,
                        Content = DescribeTurnError(ex),
                        HasError = true,
                        CanRetry = true,
                    };
                    PostTurnMessage(session, errorMsg);

                    if (attempt <= MaxAutoRetries && IsTransientError(ex) && !ct.IsCancellationRequested)
                    {
                        int delaySec = attempt * 3; // 3s, 6s, 9s
                        bool stopped = false;
                        for (int i = delaySec; i > 0 && !stopped; i--)
                        {
                            errorMsg.RetryCountdown = LocF("chat.retry.countdown", i, attempt, MaxAutoRetries);
                            try { await Task.Delay(1000, ct); }
                            catch (OperationCanceledException) { stopped = true; }
                        }
                        errorMsg.RetryCountdown = null;
                        if (stopped)
                        {
                            StatusText = Loc("chat.status.cancelled");
                            break;
                        }
                        errorMsg.CanRetry = false;   // this attempt is being retried right now
                        errorMsg.Content += "\n" + Loc("chat.retry.retrying");
                        continue;
                    }

                    StatusText = Loc("chat.status.error");
                    break;
                }
                finally
                {
                    // Clear skill AllowedTools constraint
                    _agentToolService.ActiveSkillAllowedTools = null;
                }
            }
        }
        finally
        {
            IsGenerating = false;
            CanSend = true;
            _generationStopwatch.Stop();
            StopElapsedTimer();
            _cts?.Dispose();
            _cts = null;
            UpdateContextInfo();

            // Save immediately after each completed interaction
            SaveNow();
            EndTurn(turn);
        }
    }

    /// <summary>Hover "Retry" on a failed reply: re-run the question THAT reply answered. The button used to
    /// resend whatever the latest user message was — from any bubble, tool rows included — and add it as a
    /// duplicate. For the latest exchange the turn runs again in place; an older one is asked again at the end.</summary>
    private async Task RetryFromAsync(ChatMessage? failed)
    {
        if (failed == null || IsGenerating) return;
        int idx = Messages.IndexOf(failed);
        if (idx < 0) return;

        ChatMessage? user = null;
        for (int i = idx - 1; i >= 0; i--)
        {
            if (Messages[i].Role == MessageRole.User) { user = Messages[i]; break; }
        }
        if (user == null)
        {
            StatusText = Loc("chat.retry.nothing");
            return;
        }
        if (!await EnsureProviderReadyAsync()) return;

        bool latestExchange = !Messages.Skip(idx + 1).Any(m => m.Role == MessageRole.User);
        if (latestExchange)
        {
            Messages.Remove(failed);
            await RunTurnAsync(user, user.Content, activeSkill: null, isSkill: false);
        }
        else
        {
            var again = new ChatMessage { Role = MessageRole.User, Content = user.Content };
            Messages.Add(again);
            await RunTurnAsync(again, again.Content, activeSkill: null, isSkill: false);
        }
    }

    /// <summary>
    /// Worth retrying automatically? Decided from the HTTP status code and the exception TYPE — never from
    /// message text, where "215000 tokens" contains "500". Retries: 408/429/500/502/503/504/529, a dropped or
    /// refused connection, a timeout. Not: 4xx request errors (prompt too long, bad key, no credit), local
    /// engine failures, missing files.
    /// </summary>
    public static bool IsTransientError(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            switch (e)
            {
                case TimeoutException:
                case TaskCanceledException:   // HttpClient's own timeout — a user Stop never reaches this check
                    return true;
                case System.Net.Http.HttpRequestException { StatusCode: { } code }:
                    return (int)code is 408 or 429 or 500 or 502 or 503 or 504 or 529;
                case System.Net.Http.HttpRequestException h:
                    // No status: the connection failed, or the provider sent an error event mid-stream.
                    if (h.InnerException is System.Net.Sockets.SocketException or IOException) return true;
                    return h.Message.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
                        || h.Message.Contains("rate_limit", StringComparison.OrdinalIgnoreCase);
                case System.Net.Sockets.SocketException:
                    return true;
                case IOException io when io is not (FileNotFoundException or DirectoryNotFoundException
                                                    or PathTooLongException or DriveNotFoundException or FileLoadException):
                    return true;
            }
        }
        return false;
    }

    /// <summary>The error bubble's text: a localized headline the user can act on. A provider's own HTTP
    /// explanation (written for users — "credit balance too low", "workspace ID missing") is kept, redacted;
    /// anything else — a raw .NET exception message — goes to the debug log only.</summary>
    private string DescribeTurnError(Exception ex)
    {
        _debugLog?.Error("Chat", "Turn failed", ex);
        string headline = ex switch
        {
            TimeoutException or TaskCanceledException => Loc("chat.err.timeout"),
            System.Net.Http.HttpRequestException { StatusCode: (System.Net.HttpStatusCode)429 } => Loc("chat.err.rateLimited"),
            System.Net.Http.HttpRequestException { StatusCode: { } c } when (int)c >= 500 => Loc("chat.err.serverDown"),
            System.Net.Http.HttpRequestException { StatusCode: { } } => Loc("chat.err.rejected"),
            System.Net.Http.HttpRequestException => Loc("chat.err.network"),
            _ => Loc("chat.err.generic"),
        };
        string detail = ex is System.Net.Http.HttpRequestException ? SanitizeErrorMessage(ex.Message).Trim() : "";
        if (detail.Length > 600) detail = detail[..600] + "…";
        return detail.Length > 0 ? $"**{headline}**\n{detail}" : $"**{headline}**";
    }

    private void ShowWaitingIndicator(string verb)
    {
        RemoveActiveStatus();
        var msg = new ChatMessage { Role = MessageRole.AgentStatus, Content = $"⏳ {verb}...", IsStreaming = true };
        Messages.Add(msg);
        _activeAgentStatusMsg = msg;
        ScrollToBottom?.Invoke();
    }

    private void RemoveActiveStatus()
    {
        if (_activeAgentStatusMsg == null) return;
        Messages.Remove(_activeAgentStatusMsg);
        _activeAgentStatusMsg = null;
    }

    /// <summary>After a failed attempt: a reply bubble that was mid-stream keeps what it said but stops
    /// "typing"; an empty one is removed.</summary>
    private void FinalizeDanglingBubbles()
    {
        foreach (var m in Messages.Where(m => m.Role == MessageRole.Assistant && m.IsStreaming).ToList())
        {
            m.IsStreaming = false;
            if (string.IsNullOrWhiteSpace(m.Content)) Messages.Remove(m);
            else m.IsInterrupted = true;
        }
    }

    /// <summary>Per-turn numbers for the status bar (MainWindow binds LastTurnStats). Output tokens come from
    /// the provider's usage when it reports them (API), else from the streamed chunk count (local).</summary>
    private void SetTurnStats(int tokens, double secs)
    {
        double tps = secs > 0 ? tokens / secs : 0;
        LastTurnStats = LocF("chat.stats.turn", tokens, secs, tps);
    }

    private int OutputTokensSince(int outputTokensAtStart, int fallback)
    {
        int delta = _costTracker.TotalOutputTokens - outputTokensAtStart;
        return delta > 0 ? delta : fallback;
    }

    // ─── Agentic Mode (tool use loop) ───
    /// <summary>
    /// Autonomous "drive to green": run the build-test-fix loop then the hidden-bug review loop via
    /// AutonomousCodingService. Tool activity streams to the chat through the existing agent events;
    /// phase banners arrive via OnPhase (wired in the ctor); the final response is posted + saved here.
    /// </summary>
    private async Task RunAutonomous(string input, List<ChatMessage> history, CancellationToken ct)
    {
        if (_autonomousService == null) { await RunAgentic(input, history, ct); return; }
        var target = _turnSession;
        int outAtStart = _costTracker.TotalOutputTokens;
        _streamingTokenCount = 0;

        // Clear the generic "thinking" placeholder — the autonomous loop posts its own phase banners.
        RemoveActiveStatus();

        var result = await _autonomousService.RunAsync(history, input, ct);

        var final = new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = string.IsNullOrWhiteSpace(result.FinalResponse) ? result.Summary : result.FinalResponse,
        };
        PostTurnMessage(target, final);
        _isDirty = true;
        ScrollToBottom?.Invoke();
        SaveNow();

        double secs = _generationStopwatch.Elapsed.TotalSeconds;
        SetTurnStats(OutputTokensSince(outAtStart, _streamingTokenCount), secs);
        StatusText = LocF("chat.stats.ready", secs);
    }

    private async Task RunAgentic(string input, List<ChatMessage> history, CancellationToken ct)
    {
        // Stopwatch & timer already started in RunTurnAsync
        _streamingTokenCount = 0;
        var target = _turnSession;
        int outAtStart = _costTracker.TotalOutputTokens;

        // ─── Status bar — minimal (inline chat handles detail) ───
        var progress = new Progress<string>(status =>
        {
            _lastStatusBase = status;
            int elapsed = (int)_generationStopwatch.Elapsed.TotalSeconds;
            // Keep status bar minimal — tool details shown inline in chat
            if (status is "Done" or "Ready")
                StatusText = Loc("chat.status.ready");
            else
                StatusText = elapsed > 0 ? LocF("chat.status.workingSecs", elapsed) : Loc("chat.status.working");
        });

        // ─── Real-time streaming message for agentic loop ───
        ChatMessage? streamingMsg = null;
        var streamSb = new System.Text.StringBuilder(4096);
        int lastStreamStep = -1;
        int scrollThrottle = 0;
        int agenticTokenCount = 0;
        // Thinking that arrived while no reply bubble of its step was open (a tool-only step): shown on the
        // next bubble. Touched on the UI thread only.
        string pendingThinking = "";

        void OnStreamToken(string token, int step)
        {
            // A thinking chunk from a plain-streaming provider (the [ACTION:] path): into the thinking block, not
            // the reply text.
            if (TryUnwrapThinking(token, out var thought))
            {
                if (thought.Length > 0)
                    App.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (!ShowThinking) return;
                        if (streamingMsg != null && step == lastStreamStep) streamingMsg.ThinkingContent += thought;
                        else pendingThinking += thought;
                    });
                return;
            }

            agenticTokenCount++;
            bool needsNewBubble = step != lastStreamStep;

            // First token or new step: use sync Invoke to create the bubble
            if (needsNewBubble)
            {
                streamSb.Clear();
                streamSb.Append(token);
                lastStreamStep = step;

                App.Current?.Dispatcher.Invoke(() =>
                {
                    if (_activeAgentStatusMsg != null)
                    {
                        _activeAgentStatusMsg.IsStreaming = false;
                        Messages.Remove(_activeAgentStatusMsg);
                        _activeAgentStatusMsg = null;
                    }

                    if (streamingMsg != null)
                        streamingMsg.IsStreaming = false;

                    streamingMsg = new ChatMessage
                    {
                        Role = MessageRole.Assistant,
                        Content = token,
                        IsStreaming = true,
                        ThinkingContent = pendingThinking,
                    };
                    pendingThinking = "";
                    Messages.Add(streamingMsg);
                    ScrollToBottom?.Invoke();
                });
                _streamingTokenCount++;
                return;
            }

            streamSb.Append(token);
            _streamingTokenCount++;

            // Batch UI updates every 3 tokens (non-blocking BeginInvoke)
            if (agenticTokenCount % 3 == 0)
            {
                string snapshot = streamSb.ToString();
                int capturedCount = _streamingTokenCount;
                App.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (streamingMsg != null)
                        streamingMsg.Content = snapshot;

                    // Status update every 15 tokens
                    if (capturedCount % 15 == 0)
                    {
                        int elapsed = (int)_generationStopwatch.Elapsed.TotalSeconds;
                        double tps = elapsed > 0 ? capturedCount / (double)elapsed : 0;
                        StatusText = tps > 0
                            ? LocF("chat.status.generatingRate", capturedCount, tps)
                            : LocF("chat.status.generatingCount", capturedCount);
                    }
                });
            }

            // Throttle scroll-to-bottom (every 20 tokens)
            if (++scrollThrottle % 20 == 0)
                App.Current?.Dispatcher.BeginInvoke(() => ScrollToBottom?.Invoke());
        }

        // The model's thinking, shown as the collapsed "Thinking" block above its reply. ThinkingContent used
        // to be assigned nowhere, so a user who ticked "Think" paid for extended thinking and never saw it.
        // OnThinkingUpdate carries two different things — CodeAgentService also raises it with the step's
        // VISIBLE text, which has already streamed into the bubble — so text the bubble already holds is skipped.
        void OnThinking(string text, int step)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string streamedNow = streamSb.ToString();   // same thread as the token appends
            bool alreadyShown = LooksAlreadyStreamed(text, streamedNow);
            App.Current?.Dispatcher.Invoke(() =>
            {
                // Stop the streaming cursor on the current text bubble, but KEEP the reference so the
                // end-of-turn finalizer updates it in place instead of adding a duplicate. (The old code
                // nulled streamingMsg here, which double-posted the final answer whenever extended
                // thinking fired an OnThinkingUpdate after the text had already streamed.)
                if (streamingMsg != null)
                    streamingMsg.IsStreaming = false;

                if (alreadyShown || !ShowThinking) return;
                if (streamingMsg != null && step == lastStreamStep)
                    streamingMsg.ThinkingContent = JoinThinking(streamingMsg.ThinkingContent, text);
                else
                    pendingThinking = JoinThinking(pendingThinking, text);
            });
        }

        // Surface the harness's own notices (auto-verify, escalation, tool-call salvage, compaction)
        // inline in chat. These fire only via OnAgentStatus — with no subscriber they were invisible,
        // so a 60s auto-verify build or a silent provider escalation looked exactly like a hang.
        // Tool statuses are excluded: OnToolStarting already renders those (avoids double bubbles).
        void OnAgentStatusNotice(string status)
        {
            bool notable = status.Contains('⤴') || status.Contains("Auto-verifying") || status.Contains("ตรวจ build")
                || status.Contains("Recovered tool call") || status.Contains("กู้คืน")
                || status.Contains("compacting") || status.Contains("บีบอัด")
                || status.Contains("truncated") || status.Contains("ถูกตัด");
            if (notable) OnToolStarting("agent", status);
        }
        _agentService.OnThinkingUpdate += OnThinking;
        _agentService.OnAgenticStreamingToken += OnStreamToken;
        _agentService.OnAgentStatus += OnAgentStatusNotice;
        try
        {
            // Task.Run: the agentic loop interleaves async I/O with plenty of SYNCHRONOUS work (tool
            // implementations, repo scans, JSON/token math, in-proc inference). Awaited directly from
            // this UI-thread context, every one of those chunks ran ON the dispatcher thread — the
            // window froze for their combined duration. All UI updates inside the loop's event
            // handlers already marshal via Dispatcher, so running the loop on the pool is safe.
            var result = await Task.Run(() => _agentService.ExecuteAgenticAsync(history, input, progress, ct), CancellationToken.None);

        // Finalize any remaining inline status
        App.Current?.Dispatcher.Invoke(() =>
        {
            if (_activeAgentStatusMsg != null)
            {
                _activeAgentStatusMsg.IsStreaming = false;
                _activeAgentStatusMsg = null;
            }
        });

        // Finalize streaming message — update in-place to avoid flash, and GUARANTEE the turn leaves
        // something visible. The old code could finish with no open bubble AND an empty final response
        // and add nothing at all → the "ไม่ตอบไปเลย" (no response) report. The else branch closes that hole.
        App.Current?.Dispatcher.Invoke(() =>
        {
            string? finalText = !string.IsNullOrWhiteSpace(result.FinalResponse)
                ? StripThinkingSpans(_agentToolService.StripToolCalls(result.FinalResponse))
                : null;
            if (string.IsNullOrWhiteSpace(finalText)) finalText = null;

            ChatMessage? finalBubble;
            if (streamingMsg != null)
            {
                // A streamed text bubble is open — it already holds the reply. Finalize it in place:
                // prefer the clean final text; otherwise keep whatever streamed.
                string buffered = streamSb.ToString();
                if (!string.IsNullOrWhiteSpace(finalText))
                {
                    streamingMsg.Content = finalText;
                    streamingMsg.CodeBlocks = _codeExecutionService.ExtractCodeBlocks(finalText);
                }
                else if (!string.IsNullOrEmpty(buffered) && streamingMsg.Content != buffered)
                {
                    streamingMsg.Content = buffered;
                }
                streamingMsg.IsStreaming = false;
                finalBubble = streamingMsg;
                streamingMsg = null;
            }
            else if (!string.IsNullOrWhiteSpace(finalText))
            {
                // No open bubble (answer arrived whole / wasn't streamed) — add it once.
                finalBubble = new ChatMessage
                {
                    Role = MessageRole.Assistant,
                    Content = finalText,
                    CodeBlocks = _codeExecutionService.ExtractCodeBlocks(finalText),
                };
                PostTurnMessage(target, finalBubble);
            }
            else
            {
                // ─── Safety net: the loop produced NO visible assistant text at all ───
                // (model ended after a tool with nothing to say, returned only thinking, or hit the
                // iteration cap). Never leave the user staring at a vanished spinner — surface an honest,
                // actionable note. Also clear any lingering "⏳ thinking" placeholder that never got
                // replaced by streamed text.
                var placeholder = Messages.LastOrDefault(m =>
                    m.Role == MessageRole.AgentStatus && (m.Content?.StartsWith("⏳") ?? false));
                if (placeholder != null) Messages.Remove(placeholder);

                int toolsRun = result.Steps?.Sum(s => s.ToolResults?.Count ?? 0) ?? 0;
                string note = toolsRun > 0
                    ? $"*{LocF("chat.agent.noSummary", toolsRun)}*"
                    : $"*{Loc("chat.agent.noReply")}*";

                finalBubble = new ChatMessage
                {
                    Role = MessageRole.Assistant,
                    Content = note,
                    HasError = toolsRun == 0,
                    CanRetry = toolsRun == 0,
                };
                PostTurnMessage(target, finalBubble);
            }

            // Thinking from a final tool-only step has no later bubble to sit on — put it on the answer.
            if (!string.IsNullOrEmpty(pendingThinking) && finalBubble != null)
            {
                finalBubble.ThinkingContent = JoinThinking(finalBubble.ThinkingContent, pendingThinking);
                pendingThinking = "";
            }
        });

        // Show completion stats with elapsed time and token count
        _generationStopwatch.Stop();
        StopElapsedTimer();
        double totalSecs = _generationStopwatch.Elapsed.TotalSeconds;
        int turns = result.TurnCount;
        int toolCount = result.Steps.Sum(s => s.ToolResults?.Count ?? 0);
        int tokens = OutputTokensSince(outAtStart, _streamingTokenCount);
        string statsLine = LocF("chat.stats.ready", totalSecs);
        if (toolCount > 0) statsLine += LocF("chat.stats.tools", toolCount);
        if (turns > 1) statsLine += LocF("chat.stats.steps", turns);
        if (tokens > 0) statsLine += LocF("chat.stats.tokens", tokens);
        StatusText = result.Success ? statsLine : Loc("chat.status.agentDone");
        SetTurnStats(tokens, totalSecs);
        ScrollToBottom?.Invoke();
        }
        finally
        {
            _agentService.OnThinkingUpdate -= OnThinking;
            _agentService.OnAgenticStreamingToken -= OnStreamToken;
            _agentService.OnAgentStatus -= OnAgentStatusNotice;
            StopElapsedTimer();
            // Ensure inline status is finalized on cancel/error
            App.Current?.Dispatcher.Invoke(() =>
            {
                if (_activeAgentStatusMsg != null)
                {
                    _activeAgentStatusMsg.IsStreaming = false;
                    _activeAgentStatusMsg = null;
                }
            });
        }
    }

    /// <summary>
    /// The plain streaming calls of Anthropic and Gemini (ChatAsync) interleave the model's thinking with the
    /// reply as chunks wrapped in &lt;thinking&gt;…&lt;/thinking&gt;. They were appended to the reply verbatim: raw tags
    /// on screen, and the thinking saved and sent back to the model as part of its own answer.
    /// </summary>
    public static bool TryUnwrapThinking(string token, out string thought)
    {
        const string open = "<thinking>", close = "</thinking>";
        if (token.Length >= open.Length + close.Length
            && token.StartsWith(open, StringComparison.Ordinal) && token.EndsWith(close, StringComparison.Ordinal))
        {
            thought = token[open.Length..^close.Length];
            return true;
        }
        thought = "";
        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex ThinkingSpan =
        new(@"<thinking>.*?</thinking>", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>The reply without any &lt;thinking&gt; spans a provider left in it.</summary>
    public static string StripThinkingSpans(string text) =>
        string.IsNullOrEmpty(text) || !text.Contains("<thinking>", StringComparison.Ordinal) ? text : ThinkingSpan.Replace(text, "").Trim();

    private static string JoinThinking(string existing, string more)
    {
        more = more.Trim();
        if (string.IsNullOrEmpty(existing)) return more;
        if (existing.Contains(more, StringComparison.Ordinal)) return existing;
        return existing + "\n\n" + more;
    }

    /// <summary>Is <paramref name="candidate"/> text that already streamed into the reply bubble (as opposed to
    /// thinking)? Whitespace-insensitive; matching the first 60 characters is enough — the legacy path reports
    /// the reply with its tool-call markup stripped, so the whole string need not appear verbatim.</summary>
    public static bool LooksAlreadyStreamed(string candidate, string streamed)
    {
        static string Norm(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();
        string c = Norm(candidate);
        if (c.Length == 0) return true;
        string st = Norm(streamed);
        if (st.Length == 0) return false;
        if (st.Contains(c, StringComparison.Ordinal)) return true;
        return st.Contains(c.Length > 60 ? c[..60] : c, StringComparison.Ordinal);
    }

    /// <summary>
    /// Called when a tool is ABOUT to execute. Shows inline status in chat.
    /// Claude Code-style: "⏵ Reading src/main.ts" stays visible as breadcrumb.
    /// </summary>
    private DateTime _lastToolStatusTime = DateTime.MinValue;

    private void OnToolStarting(string toolName, string statusMessage)
    {
        var target = _turnSession;   // the chat the running turn belongs to, captured at event time
        App.Current?.Dispatcher.BeginInvoke(async () =>
        {
            if (_activeAgentStatusMsg?.Content == $"⏵ {statusMessage}")
                return;

            // Min display time 700ms — ensure previous status was visible long enough
            var elapsed = DateTime.UtcNow - _lastToolStatusTime;
            if (elapsed < TimeSpan.FromMilliseconds(700) && _activeAgentStatusMsg != null)
                await Task.Delay(TimeSpan.FromMilliseconds(700) - elapsed);

            // A status line is only worth showing in the chat it describes.
            if (target != null && !ReferenceEquals(target, CurrentSession)) return;

            // Previous tool status stays visible (breadcrumb trail)
            if (_activeAgentStatusMsg != null)
                _activeAgentStatusMsg.IsStreaming = false;

            // AgentStatus, not Assistant: as an Assistant row this breadcrumb was saved, reloaded and sent to the
            // model as a turn it never wrote, and the Steps switch could not hide it.
            var statusMsg = new ChatMessage
            {
                Role = MessageRole.AgentStatus,
                Content = $"⏵ {statusMessage}",
                IsStreaming = true,
            };
            Messages.Add(statusMsg);
            _activeAgentStatusMsg = statusMsg;
            _lastToolStatusTime = DateTime.UtcNow;
            ScrollToBottom?.Invoke();
        });
    }

    // Tool categories for grouping consecutive calls
    private static readonly HashSet<string> ReadTools = new(StringComparer.OrdinalIgnoreCase)
        { "read_file", "list_files", "glob", "search_files", "search_content" };
    private static readonly HashSet<string> GitTools = new(StringComparer.OrdinalIgnoreCase)
        { "git_status", "git_diff", "git_log", "git_branch", "git_add", "git_stash" };

    private static string GetToolCategory(string toolName)
    {
        if (ReadTools.Contains(toolName)) return "read";
        if (GitTools.Contains(toolName)) return "git";
        return toolName; // unique category = no grouping
    }

    /// <summary>Raised (on the UI thread) when the agent mutates a file: (relativePath, firstChangedLine).
    /// The Code Editor subscribes to open/refresh that file live and scroll to the change.</summary>
    public event Action<string, int>? FileMutatedByAgent;

    private void OnToolExecuted(ToolResult toolResult)
    {
        var target = _turnSession;   // the chat the running turn belongs to, captured at event time
        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            // This callback is queued; if the user has switched chats since, the row belongs to the turn's chat,
            // not to whatever is on screen now (it used to land in the newly opened chat).
            bool onScreen = target == null || ReferenceEquals(target, CurrentSession);
            if (onScreen && _activeAgentStatusMsg != null)
            {
                _activeAgentStatusMsg.IsStreaming = false;
                _activeAgentStatusMsg = null;
            }

            // Live-follow: open/refresh the changed file in the Code Editor and scroll to the edit.
            if (toolResult.Success
                && _settingsService.Settings.LiveEditFollow
                && !string.IsNullOrEmpty(toolResult.FilePath))
            {
                FileMutatedByAgent?.Invoke(toolResult.FilePath!, toolResult.Diff?.FirstChangedLine ?? 1);
            }

            // Format arguments for display
            string? argsDisplay = null;
            string? inputSummary = null;
            if (toolResult.Arguments is { Count: > 0 })
            {
                var displayArgs = new List<string>();
                string? primaryArg = null;
                foreach (var (key, value) in toolResult.Arguments)
                {
                    string displayValue = value.Length > 120
                        ? value[..120].Replace("\n", " ") + "..."
                        : value.Replace("\n", "\\n");
                    displayArgs.Add($"  {key}: {displayValue}");

                    if (primaryArg == null && key is "path" or "command" or "query" or "pattern" or "url"
                        or "name" or "skill" or "question" or "branch" or "task" or "notebook_path")
                        primaryArg = $"{key}: {(value.Length > 80 ? value[..80] + "..." : value)}";
                }
                argsDisplay = string.Join("\n", displayArgs);
                inputSummary = primaryArg;
            }

            int outputLines = string.IsNullOrEmpty(toolResult.Output)
                ? 0
                : toolResult.Output.Split('\n').Length;

            string displayName = FormatToolDisplayName(toolResult.ToolName, toolResult.Arguments);

            // ─── Tool call grouping: collapse consecutive same-category calls ───
            string category = GetToolCategory(toolResult.ToolName);
            var lastMsg = onScreen ? Messages.LastOrDefault() : null;

            if (lastMsg is { Role: MessageRole.ToolAction, IsCollapsedGroup: true }
                && GetToolCategory(lastMsg.CollapsedToolNames.LastOrDefault() ?? "") == category
                && lastMsg.ToolSuccess && toolResult.Success)
            {
                // Merge into existing group
                lastMsg.CollapsedCount++;
                lastMsg.CollapsedToolNames.Add(toolResult.ToolName);
                lastMsg.Content = LocF("chat.tools.groupContent", lastMsg.CollapsedCount, string.Join(", ", lastMsg.CollapsedToolNames.Distinct()));
                lastMsg.ToolName = LocF("chat.tools.groupName", lastMsg.CollapsedCount);
                lastMsg.ToolOutput = (lastMsg.ToolOutput ?? "") + "\n---\n" + (toolResult.Output ?? "");
                lastMsg.ToolOutputLines += outputLines;
                // Don't add new message — just updated existing
                ScrollToBottom?.Invoke();
                return;
            }

            if (lastMsg is { Role: MessageRole.ToolAction } && !lastMsg.IsCollapsedGroup
                && GetToolCategory(lastMsg.CollapsedToolNames.LastOrDefault() ?? lastMsg.ToolName?.Split(' ').LastOrDefault() ?? "") == category
                && lastMsg.ToolSuccess && toolResult.Success
                && category is "read" or "git")
            {
                // Convert single ToolAction to group + add this one
                string prevToolRaw = lastMsg.CollapsedToolNames.Count > 0
                    ? lastMsg.CollapsedToolNames[0]
                    : "read_file";
                lastMsg.IsCollapsedGroup = true;
                lastMsg.CollapsedCount = 2;
                lastMsg.CollapsedToolNames = new List<string> { prevToolRaw, toolResult.ToolName };
                lastMsg.Content = LocF("chat.tools.groupContent", 2, string.Join(", ", lastMsg.CollapsedToolNames.Distinct()));
                lastMsg.ToolName = LocF("chat.tools.groupName", 2);
                lastMsg.ToolOutput = (lastMsg.ToolOutput ?? "") + "\n---\n" + (toolResult.Output ?? "");
                lastMsg.ToolOutputLines += outputLines;
                ScrollToBottom?.Invoke();
                return;
            }

            // Normal: add as individual ToolAction message
            var msg = new ChatMessage
            {
                Role = MessageRole.ToolAction,
                Content = !string.IsNullOrEmpty(toolResult.Summary) ? toolResult.Summary : displayName,
                ToolName = displayName,
                ToolSummary = !string.IsNullOrEmpty(toolResult.Summary) ? toolResult.Summary : displayName,
                ToolOutput = toolResult.Output,
                ToolSuccess = toolResult.Success,
                HasError = !toolResult.Success,
                ToolArguments = argsDisplay,
                ToolInputSummary = inputSummary,
                ToolOutputLines = outputLines,
                CollapsedToolNames = new List<string> { toolResult.ToolName },
                DiffLines = toolResult.Diff?.Lines,
                DiffStat = toolResult.Diff?.Stat,
            };
            PostTurnMessage(target, msg);
            ScrollToBottom?.Invoke();
        });
    }

    // ─── Tool Display Name Formatting ───
    // Maps tool names to past-tense verbs for chat bubble display
    private static readonly Dictionary<string, string> ToolDisplayVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read_file"] = "Read",
        ["write_file"] = "Wrote",
        ["edit_file"] = "Edited",
        ["list_files"] = "Listed",
        ["search_files"] = "Searched",
        ["search_content"] = "Searched",
        ["glob"] = "Found",
        ["grep"] = "Searched",
        ["run_command"] = "Ran",
        ["powershell"] = "Ran PowerShell",
        ["git_status"] = "git status",
        ["git_diff"] = "git diff",
        ["git_log"] = "git log",
        ["git_commit"] = "Committed",
        ["git_push"] = "Pushed",
        ["git_pull"] = "Pulled",
        ["git_clone"] = "Cloned",
        ["git_init"] = "Initialized",
        ["git_checkout"] = "Checked out",
        ["git_merge"] = "Merged",
        ["git_branch"] = "Branched",
        ["git_add"] = "Staged",
        ["git_stash"] = "Stashed",
        ["web_fetch"] = "Fetched",
        ["web_search"] = "Web search",
        ["notebook_edit"] = "Edited notebook",
        ["memory_save"] = "Saved memory",
        ["memory_list"] = "Listed memories",
        ["memory_delete"] = "Deleted memory",
        ["skill_invoke"] = "Invoked skill",
        ["ask_user"] = "Asked user",
        ["create_directory"] = "Created directory",
        ["repl"] = "REPL",
        ["todo_write"] = "Updated tasks",
        ["agent_spawn"] = "Spawned agent",
    };

    private static string FormatToolDisplayName(string toolName, Dictionary<string, string>? args)
    {
        string verb = ToolDisplayVerbs.GetValueOrDefault(toolName, toolName);

        string? target = null;
        if (args != null)
        {
            target = args.GetValueOrDefault("path")
                  ?? args.GetValueOrDefault("file_path")
                  ?? args.GetValueOrDefault("pattern")
                  ?? args.GetValueOrDefault("url")
                  ?? args.GetValueOrDefault("query")
                  ?? args.GetValueOrDefault("skill")
                  ?? args.GetValueOrDefault("branch");

            if (target == null && args.TryGetValue("command", out var cmd))
                target = cmd.Length > 50 ? cmd[..50] + "..." : cmd;
        }

        if (!string.IsNullOrEmpty(target))
        {
            if (target.Contains('/') || target.Contains('\\'))
            {
                string fileName = Path.GetFileName(target);
                if (!string.IsNullOrEmpty(fileName))
                    target = fileName;
            }
            return $"{verb} {target}";
        }
        return verb;
    }

    // ─── Elapsed Time Timer ───
    private void StartElapsedTimer()
    {
        StopElapsedTimer();
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _elapsedTimer.Tick += (_, _) =>
        {
            if (!_generationStopwatch.IsRunning || !IsGenerating) return;
            double elapsed = _generationStopwatch.Elapsed.TotalSeconds;
            string elapsedText = elapsed < 10 ? $"{elapsed:F1}s" : $"{(int)elapsed}s";

            // Stage-based status so user knows what's happening. The long-wait stage must explain
            // WHY (local prompt processing) — a bare "taking longer than usual" reads as a hang, and
            // users killed the app during a perfectly healthy 1-2 minute first prefill.
            string stage = elapsed switch
            {
                < 1.0 => Loc("chat.stage.building"),
                < 3.0 => Loc("chat.stage.sending"),
                < 8.0 => Loc("chat.stage.waiting"),
                < 15.0 => Loc("chat.stage.stillWaiting"),
                < 45.0 => Loc("chat.stage.slow"),
                _ => ShowLocalModelPicker ? Loc("chat.stage.localPrefill") : Loc("chat.stage.slowApi"),
            };

            // If we already got tokens, show token count instead of stages
            if (_streamingTokenCount > 0)
            {
                double tps = elapsed > 0 ? _streamingTokenCount / elapsed : 0;
                StatusText = tps > 1
                    ? LocF("chat.status.generatingRateTime", _streamingTokenCount, tps, elapsedText)
                    : LocF("chat.status.generatingCountTime", _streamingTokenCount, elapsedText);
            }
            else
            {
                StatusText = $"{stage}... ({elapsedText})";
            }

            // Update inline thinking indicator in chat (an AgentStatus row: plain text, no markdown)
            if (_activeAgentStatusMsg is { IsStreaming: true } msg && msg.Content.StartsWith("⏳"))
                _activeAgentStatusMsg.Content = $"⏳ {stage}... ({elapsedText})";
        };
        _elapsedTimer.Start();
    }

    private void StopElapsedTimer()
    {
        _elapsedTimer?.Stop();
        _elapsedTimer = null;
    }

    // ─── Streaming Mode ───
    private async Task RunStreaming(string input, List<ChatMessage> history, CancellationToken ct)
    {
        // DON'T remove thinking indicator here — it's sync and runs before UI renders.
        // Instead, remove it when the first token arrives (after async await).
        var target = _turnSession;

        var assistantMsg = new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = "",
            IsStreaming = true,
        };
        // Don't add assistant msg yet — wait for first token so thinking indicator stays visible

        var sb = new StringBuilder();
        var thinkSb = new StringBuilder();
        int tokenCount = 0;
        bool firstToken = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await foreach (var token in _agentService.ChatStreamAsync(history, input, ct))
        {
            // Thinking chunks go to the reply's collapsed Thinking block — never into the reply text.
            if (TryUnwrapThinking(token, out var thought))
            {
                if (thought.Length == 0 || !ShowThinking) continue;
                bool firstThought = thinkSb.Length == 0;
                thinkSb.Append(thought);
                string thinkSnap = thinkSb.ToString();
                App.Current?.Dispatcher.BeginInvoke(() =>
                {
                    assistantMsg.ThinkingContent = thinkSnap;
                    if (firstThought && firstToken && _activeAgentStatusMsg != null)
                        _activeAgentStatusMsg.Content = "💭 " + Loc("chat.thinkingInline").Trim();
                });
                continue;
            }

            sb.Append(token);
            tokenCount++;
            _streamingTokenCount = tokenCount;

            // First token: sync update to guarantee bubble visible before more tokens arrive
            if (firstToken)
            {
                firstToken = false;
                App.Current?.Dispatcher.Invoke(() =>
                {
                    if (_activeAgentStatusMsg != null)
                        _activeAgentStatusMsg.Content = "⚡ " + Loc("chat.status.generating");
                    PostTurnMessage(target, assistantMsg);
                    assistantMsg.Content = sb.ToString();
                    ScrollToBottom?.Invoke();
                });
                continue;
            }

            // Subsequent tokens: async fire-and-forget (non-blocking)
            // Batch updates every 3 tokens to reduce UI thrash without feeling laggy
            if (tokenCount % 3 == 0)
            {
                int capturedCount = tokenCount;
                string snapshot = sb.ToString();
                App.Current?.Dispatcher.BeginInvoke(() =>
                {
                    assistantMsg.Content = snapshot;
                    if (capturedCount % 15 == 0 && _activeAgentStatusMsg != null)
                    {
                        int elapsed = (int)sw.Elapsed.TotalSeconds;
                        double tps = elapsed > 0 ? capturedCount / (double)elapsed : 0;
                        _activeAgentStatusMsg.Content = "⚡ " + (tps > 0
                            ? LocF("chat.status.generatingRate", capturedCount, tps)
                            : LocF("chat.status.generatingCount", capturedCount));
                    }
                });
            }
        }

        // Final content flush — ensure last tokens are shown
        App.Current?.Dispatcher.Invoke(() =>
        {
            assistantMsg.Content = sb.ToString();
            if (thinkSb.Length > 0) assistantMsg.ThinkingContent = thinkSb.ToString();
        });

        sw.Stop();

        // Finalize — use Ollama's accurate token counts when available
        App.Current?.Dispatcher.Invoke(() =>
        {
            // If no tokens were received, assistantMsg was never added — add it now
            if (firstToken)
            {
                // Remove thinking indicator, replace with assistant message
                RemoveActiveStatus();
                PostTurnMessage(target, assistantMsg);
            }

            assistantMsg.IsStreaming = false;

            // Prefer provider's token counts (Ollama returns exact counts)
            int finalTokenCount = tokenCount;
            if (_providerManager.ActiveProvider is Services.Providers.OllamaProvider ollama
                && ollama.LastCompletionTokens > 0)
            {
                finalTokenCount = ollama.LastCompletionTokens;
            }

            assistantMsg.TokenCount = finalTokenCount;
            assistantMsg.GenerationTimeMs = sw.ElapsedMilliseconds;
            string responseText = sb.ToString().Trim();

            if (string.IsNullOrWhiteSpace(responseText))
            {
                assistantMsg.Content = Loc("chat.err.emptyReply");
                assistantMsg.HasError = true;
                assistantMsg.CanRetry = true;
                StatusText = Loc("chat.status.noReply");
                // Remove inline status on error
                RemoveActiveStatus();
            }
            else
            {
                assistantMsg.Content = responseText;
                assistantMsg.CodeBlocks = _codeExecutionService.ExtractCodeBlocks(responseText);

                // Show per-turn stats
                double tps = assistantMsg.TokensPerSecond;
                double secs = sw.ElapsedMilliseconds / 1000.0;
                StatusText = LocF("chat.stats.ready", secs) + LocF("chat.stats.tokens", finalTokenCount);
                SetTurnStats(finalTokenCount, secs);

                // Finalize inline status with completion stats (an AgentStatus row: shown with Steps on)
                if (_activeAgentStatusMsg != null)
                {
                    _activeAgentStatusMsg.Content = "✓ " + LocF("chat.stats.turn", finalTokenCount, secs, tps);
                    _activeAgentStatusMsg.IsStreaming = false;
                    _activeAgentStatusMsg = null;
                }
            }
        });

        ScrollToBottom?.Invoke();
    }

    // ─── Auto-Execute Mode ───
    private async Task RunWithAutoExecution(string input, List<ChatMessage> history, CancellationToken ct)
    {
        // Don't remove thinking indicator here (sync) — remove after async completes
        var target = _turnSession;
        int outAtStart = _costTracker.TotalOutputTokens;

        var progress = new Progress<string>(status =>
            App.Current?.Dispatcher.Invoke(() => StatusText = status));

        var result = await _agentService.ExecuteWithAutoFixAsync(history, input, progress, ct);

        // Remove thinking indicator after async completes
        RemoveActiveStatus();

        PostTurnMessage(target, new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = result.Response,
            CodeBlocks = result.CodeBlocks,
        });

        if (result.ExecutionResult != null)
        {
            PostTurnMessage(target, new ChatMessage
            {
                Role = MessageRole.CodeExecution,
                Content = FormatExecutionResult(result),
                HasError = !result.ExecutionResult.Success,
            });
        }

        StatusText = result.Success ? Loc("chat.status.ready") : Loc("chat.exec.hadIssues");
        SetTurnStats(OutputTokensSince(outAtStart, 0), _generationStopwatch.Elapsed.TotalSeconds);
        ScrollToBottom?.Invoke();
    }

    private static string FormatExecutionResult(AgentResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.Success ? $"**{Loc("chat.exec.succeeded")}**" : $"**{Loc("chat.exec.failed")}**");
        sb.AppendLine(LocF("chat.exec.attempts", result.Attempts));

        if (result.ExecutionResult != null)
        {
            if (!string.IsNullOrWhiteSpace(result.ExecutionResult.Output))
                sb.AppendLine($"\n**{Loc("chat.exec.output")}:**\n```\n{result.ExecutionResult.Output}\n```");
            if (!string.IsNullOrWhiteSpace(result.ExecutionResult.Error))
                sb.AppendLine($"\n**{Loc("chat.exec.errors")}:**\n```\n{result.ExecutionResult.Error}\n```");
        }
        return sb.ToString();
    }

    private void StopGeneration()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* CTS already disposed */ }
    }

    /// <summary>Run a code block from a reply. Stop cancels it (there was no token, so Stop did nothing), and
    /// an error stays in the status bar — the finally block used to overwrite it with "Ready" at once.</summary>
    private async Task ExecuteCodeBlock(CodeBlock? block)
    {
        if (block == null || IsGenerating) return;
        var turn = BeginTurn();
        var target = _turnSession;
        StatusText = LocF("chat.exec.running", block.Language);
        IsGenerating = true;
        CanSend = false;
        _cts = new CancellationTokenSource();
        string? outcome = null;
        try
        {
            var token = _cts.Token;
            var result = await _codeExecutionService.ExecuteAsync(block.Code, block.Language, token);
            // CodeExecutionService kills the process on cancel and RETURNS a failed result rather than throwing.
            token.ThrowIfCancellationRequested();
            PostTurnMessage(target, new ChatMessage
            {
                Role = MessageRole.CodeExecution,
                Content = $"**{(result.Success ? Loc("chat.exec.output") : Loc("chat.exec.error"))}:**\n```\n{result.FullOutput}\n```",
                HasError = !result.Success,
            });
            ScrollToBottom?.Invoke();
            outcome = result.Success ? Loc("chat.status.ready") : Loc("chat.exec.failed");
        }
        catch (OperationCanceledException)
        {
            outcome = Loc("chat.exec.cancelled");
        }
        catch (Exception ex)
        {
            _debugLog?.Error("Chat", $"Running a {block.Language} block failed", ex);
            outcome = Loc("chat.exec.couldNotRun");
        }
        finally
        {
            IsGenerating = false;
            CanSend = true;
            _cts?.Dispose();
            _cts = null;
            StatusText = outcome ?? Loc("chat.status.ready");
            EndTurn(turn);
        }
    }

    private void CopyCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return;
        try { System.Windows.Clipboard.SetText(code); StatusText = Loc("chat.status.copied"); } catch { }
    }

    // ─── Model Selector ───

    /// <summary>
    /// Syncs the chat view's model selector with current settings/service state.
    /// Called when settings change (e.g. model loaded from Models tab) or on navigation.
    /// </summary>
    public void SyncModelSelectionFromSettings()
    {
        _syncedModelPath = _settingsService.Settings.SelectedModelPath;

        // Refresh model list to pick up newly downloaded models
        RefreshLocalModelsList();
        RefreshLoadedModelName();
    }

    /// <summary>The SelectedModelPath the picker was last synced to — a settings change only rescans the model
    /// folders when this differs.</summary>
    private string? _syncedModelPath;

    public void RefreshLocalModelsList()
    {
        var models = _huggingFaceService.GetLocalModels();
        LocalModels.Clear();
        foreach (var m in models)
            LocalModels.Add(m);

        // Pre-select the currently loaded model. With the llama-server backend (Gemma 4 / Llama 4 / Qwen 3) the
        // in-process engine has no path, so a ready local provider means the saved model is the loaded one.
        string? loadedPath = _llamaService.LoadedModelPath
            ?? (ShowLocalModelPicker && _providerManager.ActiveProvider.IsReady ? _settingsService.Settings.SelectedModelPath : null);
        _selectedLocalModel = loadedPath == null
            ? null
            : LocalModels.FirstOrDefault(m => string.Equals(m.LocalPath, loadedPath, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(SelectedLocalModel));   // backing field set directly: must not trigger a load
    }

    /// <summary>
    /// Load the GGUF picked in the chat's model box. Routed through LocalGgufProvider (it picks LLamaSharp or
    /// llama-server by architecture — the direct LLamaSharp call this used made Gemma 4 / Llama 4 / Qwen 3 fail),
    /// only while a local provider is answering (it used to switch a Claude chat to Local silently), and only
    /// after the user confirmed the size against free VRAM.
    /// </summary>
    private async Task LoadModelFromChat(ModelInfo model, ModelInfo? previous)
    {
        void Revert()
        {
            _selectedLocalModel = previous;
            App.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(SelectedLocalModel)));
        }

        string? path = model.LocalPath;
        if (string.IsNullOrEmpty(path) || !ShowLocalModelPicker) { Revert(); return; }

        string? loadedPath = _llamaService.LoadedModelPath
            ?? (_providerManager.ActiveProvider.IsReady ? _settingsService.Settings.SelectedModelPath : null);
        if (_providerManager.ActiveProvider.IsReady && string.Equals(loadedPath, path, StringComparison.OrdinalIgnoreCase))
            return;   // already the loaded model

        if (IsLoadingModel || _llamaService.IsLoading || (_localGgufProvider?.IsLoading ?? false))
        {
            StatusText = Loc("chat.model.busy");
            Revert();
            return;
        }
        if (IsGenerating)
        {
            StatusText = Loc("chat.model.busyTurn");
            Revert();
            return;
        }

        // How much VRAM is free right now (nvidia-smi can take seconds — never on the UI thread).
        GpuLiveStats? gpu = null;
        try { gpu = await Task.Run(() => _gpuDetection.GetLiveStats()); }
        catch { /* no NVIDIA GPU / no nvidia-smi */ }

        long sizeBytes = model.FileSize > 0 ? model.FileSize : FileLengthOrZero(path);
        string text = gpu is { VramTotalMB: > 0 }
            ? LocF("chat.model.confirmVram", model.Name, FormatGb(sizeBytes),
                   FormatGb(gpu.VramFreeMB * 1024L * 1024L), FormatGb(gpu.VramTotalMB * 1024L * 1024L))
            : LocF("chat.model.confirmNoGpu", model.Name, FormatGb(sizeBytes));
        if (gpu is { VramTotalMB: > 0 } && sizeBytes > gpu.VramFreeMB * 1024L * 1024L)
            text += "\n\n" + Loc("chat.model.confirmTight");
        if (!Confirm(text, Loc("chat.model.confirmTitle")))
        {
            Revert();
            return;
        }

        IsLoadingModel = true;
        StatusText = Loc("chat.model.loading");
        try
        {
            // Llama-server → Local: both run on this PC; LocalGgufProvider is the router over the two.
            if (_providerManager.ActiveProviderType != AiProviderType.Local)
                await _providerManager.SwitchProviderAsync(AiProviderType.Local);

            var progress = new Progress<string>(s => App.Current?.Dispatcher.BeginInvoke(() => StatusText = s));
            if (_localGgufProvider != null)
                await _localGgufProvider.LoadModelAsync(path, progress);
            else
                await _llamaService.LoadModelAsync(path, progress);

            string name = Path.GetFileNameWithoutExtension(path);
            _settingsService.UpdateSettings(s =>
            {
                s.SelectedModelPath = path;
                s.SelectedModelName = name;
            });
            StatusText = LocF("chat.model.ready", name);
        }
        catch (Exception ex)
        {
            _debugLog?.Error("Chat", $"Loading {path} from the chat picker failed", ex);
            StatusText = Loc("chat.model.loadFailed");
            Revert();
        }
        finally
        {
            IsLoadingModel = false;
            RefreshLoadedModelName();
        }
    }

    private static long FileLengthOrZero(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static string FormatGb(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";

    // ─── History Search ───

    private readonly DispatcherTimer _searchDebounceTimer;
    private string _pendingSearchQuery = "";

    private void PerformHistorySearch(string query)
    {
        // Debounce: wait 300ms after last keystroke
        _searchDebounceTimer.Stop();

        if (string.IsNullOrWhiteSpace(query))
        {
            IsSearchingHistory = false;
            SearchResults.Clear();
            return;
        }

        _pendingSearchQuery = query;
        _searchDebounceTimer.Start();
    }

    private void ExecuteHistorySearch(string query)
    {
        try
        {
            var results = _persistenceService.SearchSessions(query);
            SearchResults.Clear();
            foreach (var r in results)
                SearchResults.Add(r);
            IsSearchingHistory = true;
        }
        catch
        {
            SearchResults.Clear();
            IsSearchingHistory = false;
        }
    }

    private void ClearHistorySearch()
    {
        HistorySearchQuery = string.Empty;
        IsSearchingHistory = false;
        SearchResults.Clear();
    }

    private async Task ClearChatAsync()
    {
        // A reply still running would keep adding to the chat being cleared.
        if (!await EnsureIdleAsync()) return;

        // Destructive action — confirm unless already empty.
        if (Messages.Count > 0 && !Confirm(LocF("chat.clear.confirm", Messages.Count), Loc("chat.clear.title")))
            return;

        ResolvePendingPermissionPrompts();
        Messages.Clear();
        CurrentSession?.Messages.Clear();
        _isDirty = true;
        StatusText = Loc("chat.status.cleared");
    }

    /// <summary>Auto-deny any still-unanswered permission prompts before the message list is cleared
    /// or swapped (session switch / new chat / delete / clear). The agent loop awaits the prompt's
    /// TaskCompletionSource — destroying the bubble without resolving it parked the loop forever and
    /// left IsGenerating stuck on, which the user experienced as a hard hang.</summary>
    private void ResolvePendingPermissionPrompts()
    {
        foreach (var m in Messages)
        {
            if (m.Role == MessageRole.PermissionRequest && !m.PermissionAnswered)
                m.ResolvePermission(PermissionOutcome.Dismissed);
        }
    }

    // ─── Code Review ───
    private async Task RunCodeReview()
    {
        if (!HasProject)
        {
            StatusText = Loc("chat.review.needProject");
            return;
        }
        if (IsGenerating) return;

        var turn = BeginTurn();
        var target = _turnSession;
        IsGenerating = true;
        CanSend = false;
        StatusText = Loc("chat.review.running");

        try
        {
            _cts = new CancellationTokenSource();

            var history = BuildModelHistory(Messages, includeTools: false);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var review = await _agentService.ReviewCodeAsync(history, null, _cts.Token);
            sw.Stop();

            int tokenEst = review.Length / 4; // rough estimate
            var reviewMsg = new ChatMessage
            {
                Role = MessageRole.Assistant,
                Content = review,
                TokenCount = tokenEst,
                GenerationTimeMs = sw.ElapsedMilliseconds,
            };

            PostTurnMessage(target, reviewMsg);
            StatusText = LocF("chat.review.done", tokenEst, sw.ElapsedMilliseconds / 1000.0);
            SetTurnStats(tokenEst, sw.ElapsedMilliseconds / 1000.0);
        }
        catch (OperationCanceledException)
        {
            StatusText = Loc("chat.review.cancelled");
        }
        catch (Exception ex)
        {
            _debugLog?.Error("Chat", "Code review failed", ex);
            StatusText = Loc("chat.review.failed");
        }
        finally
        {
            IsGenerating = false;
            CanSend = true;
            _cts?.Dispose();
            _cts = null;
            UpdateContextInfo();
            SaveNow();
            EndTurn(turn);
        }
    }

    /// <summary>Strip raw exception prefixes and sensitive info from error messages.</summary>
    private string SanitizeErrorMessage(string message)
    {
        // Remove common .NET exception prefixes
        message = System.Text.RegularExpressions.Regex.Replace(
            message, @"^(System\.\w+Exception|Exception):\s*", "");
        // Redact any API keys that might appear in error messages
        foreach (var kvp in _settingsService.Settings.ProviderConfigs)
        {
            if (!string.IsNullOrEmpty(kvp.Value.ApiKey) && message.Contains(kvp.Value.ApiKey))
                message = message.Replace(kvp.Value.ApiKey, "[REDACTED]");
        }
        if (!string.IsNullOrEmpty(_settingsService.Settings.HuggingFaceToken)
            && message.Contains(_settingsService.Settings.HuggingFaceToken))
            message = message.Replace(_settingsService.Settings.HuggingFaceToken, "[REDACTED]");
        return message;
    }
}
