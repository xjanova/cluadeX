using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

public class ChatViewModel : ViewModelBase
{
    private readonly AiProviderManager _providerManager;
    private readonly CodeAgentService _agentService;
    private readonly CodeExecutionService _codeExecutionService;
    private readonly AgentToolService _agentToolService;
    private readonly FileSystemService _fileSystemService;
    private readonly GitService _gitService;
    private readonly GitHubService _gitHubService;
    private readonly SettingsService _settingsService;
    private readonly ContextMemoryService _contextMemoryService;
    private readonly ChatPersistenceService _persistenceService;
    private readonly HuggingFaceService _huggingFaceService;
    private readonly LlamaInferenceService _llamaService;
    private readonly GpuDetectionService _gpuDetection;
    private readonly SkillService _skillService;
    private readonly CostTrackingService _costTracker;
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _memoryTimer;
    private readonly DispatcherTimer _autoSaveTimer;

    private string _userInput = string.Empty;
    private bool _isGenerating;
    private bool _autoExecute;
    private bool _agenticMode;
    private string _statusText = "Ready";
    private ChatSession? _currentSession;
    private bool _canSend = true;
    private string _workingDirectory = string.Empty;
    private bool _hasProject;
    private string _gitBranch = string.Empty;
    private double _contextUsagePercent;
    private string _contextUsageText = "0 / 4,096 tokens";
    private string _memoryUsageText = "RAM: --";
    private string _costText = "";
    private bool _isDirty; // track unsaved changes
    private string _historySearchQuery = string.Empty;
    private bool _isSearchingHistory;
    private string _dbStatsText = "";
    private ModelInfo? _selectedLocalModel;
    private string _loadedModelName = "No model loaded";
    private bool _isLoadingModel;
    private bool _showContextWarning;
    private string _contextWarningText = "";

    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public ObservableCollection<ChatSession> Sessions { get; } = new();
    public ObservableCollection<SearchResult> SearchResults { get; } = new();
    public ObservableCollection<ModelInfo> LocalModels { get; } = new();

    public string UserInput { get => _userInput; set => SetProperty(ref _userInput, value); }
    public bool IsGenerating { get => _isGenerating; set => SetProperty(ref _isGenerating, value); }
    public bool AutoExecute
    {
        get => _autoExecute;
        set
        {
            if (SetProperty(ref _autoExecute, value))
                _settingsService.UpdateSettings(s => s.AutoExecuteCode = value);
        }
    }
    public bool AgenticMode
    {
        get => _agenticMode;
        set => SetProperty(ref _agenticMode, value);
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
                HasProject = !string.IsNullOrEmpty(value);
        }
    }
    public bool HasProject { get => _hasProject; set => SetProperty(ref _hasProject, value); }
    public string GitBranch { get => _gitBranch; set => SetProperty(ref _gitBranch, value); }

    // Context tracking
    public double ContextUsagePercent { get => _contextUsagePercent; set => SetProperty(ref _contextUsagePercent, value); }
    public string ContextUsageText { get => _contextUsageText; set => SetProperty(ref _contextUsageText, value); }
    public string MemoryUsageText { get => _memoryUsageText; set => SetProperty(ref _memoryUsageText, value); }

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

    // ─── GPU Live Stats ───
    private string _gpuStatsText = "";
    public string GpuStatsText { get => _gpuStatsText; set => SetProperty(ref _gpuStatsText, value); }
    private int _gpuTempC;
    public int GpuTempC { get => _gpuTempC; set => SetProperty(ref _gpuTempC, value); }
    private int _gpuUsagePercent;
    public int GpuUsagePercent { get => _gpuUsagePercent; set => SetProperty(ref _gpuUsagePercent, value); }

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

    // ─── Thinking Display ───
    private bool _showThinking = true;
    public bool ShowThinking
    {
        get => _showThinking;
        set
        {
            if (SetProperty(ref _showThinking, value))
                _settingsService.UpdateSettings(s => s.AutoExecuteCode = s.AutoExecuteCode); // trigger save
        }
    }

    // ─── TODO List ───
    private string _todoListText = "";
    public string TodoListText { get => _todoListText; set => SetProperty(ref _todoListText, value); }
    private bool _showTodoPanel;
    public bool ShowTodoPanel { get => _showTodoPanel; set => SetProperty(ref _showTodoPanel, value); }

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
    public string DbStatsText { get => _dbStatsText; set => SetProperty(ref _dbStatsText, value); }

    // ─── Model Selector ───
    public ModelInfo? SelectedLocalModel
    {
        get => _selectedLocalModel;
        set
        {
            if (SetProperty(ref _selectedLocalModel, value) && value != null)
                _ = LoadModelFromChat(value.LocalPath);
        }
    }
    public string LoadedModelName { get => _loadedModelName; set => SetProperty(ref _loadedModelName, value); }
    public bool IsLoadingModel { get => _isLoadingModel; set => SetProperty(ref _isLoadingModel, value); }
    public bool ShowContextWarning { get => _showContextWarning; set => SetProperty(ref _showContextWarning, value); }
    public string ContextWarningText { get => _contextWarningText; set => SetProperty(ref _contextWarningText, value); }

    public ICommand RefreshModelsCommand { get; }
    public ICommand SendMessageCommand { get; }
    public ICommand StopGenerationCommand { get; }
    public ICommand ClearChatCommand { get; }
    public ICommand NewSessionCommand { get; }
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

    public event Action? ScrollToBottom;

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
        CostTrackingService costTracker)
    {
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
        _gpuDetection = gpuDetection;
        _skillService = skillService;
        _costTracker = costTracker;

        AutoExecute = settingsService.Settings.AutoExecuteCode;
        _extendedThinkingEnabled = settingsService.Settings.ExtendedThinkingEnabled;

        RefreshModelsCommand = new RelayCommand(RefreshLocalModelsList);
        SendMessageCommand = new AsyncRelayCommand(SendMessage);
        StopGenerationCommand = new RelayCommand(StopGeneration);
        ClearChatCommand = new RelayCommand(ClearChat);
        NewSessionCommand = new RelayCommand(NewSession);
        CopyCodeCommand = new RelayCommand<string>(CopyCode);
        ExecuteCodeBlockCommand = new AsyncRelayCommand<CodeBlock>(ExecuteCodeBlock);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        CloseFolderCommand = new RelayCommand(CloseFolder);
        CloneRepoCommand = new AsyncRelayCommand(CloneRepo);
        LoadSessionCommand = new RelayCommand<string>(LoadSession);
        DeleteSessionCommand = new RelayCommand<string>(DeleteSession);
        ClearSearchCommand = new RelayCommand(ClearHistorySearch);
        LoadSearchResultCommand = new RelayCommand<string>(LoadSession);
        ReviewCodeCommand = new AsyncRelayCommand(RunCodeReview);

        // Listen for tool execution events
        _agentService.OnToolExecuted += OnToolExecuted;

        // Update context info when messages change
        Messages.CollectionChanged += OnMessagesChanged;

        // Periodic RAM/context/GPU refresh every 5 seconds
        _memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _memoryTimer.Tick += (_, _) =>
        {
            UpdateContextInfo();
            UpdateGpuStats();
            CostText = _costTracker.FormatCost();
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

        // Listen for model status changes
        _llamaService.OnStatusChanged += status =>
            App.Current?.Dispatcher.Invoke(() => LoadedModelName = status);
        _llamaService.OnLoadingChanged += loading =>
            App.Current?.Dispatcher.Invoke(() => IsLoadingModel = loading);

        // Sync model selection when settings change (e.g. model loaded from Models tab)
        _settingsService.SettingsChanged += () =>
            App.Current?.Dispatcher.Invoke(() => SyncModelSelectionFromSettings());

        // Restore previous sessions
        RestoreSessions();
        UpdateContextInfo();
        RefreshDbStats();
        RefreshLocalModelsList();

        // Set initial loaded model name & auto-load if previously selected
        if (_llamaService.IsModelLoaded)
        {
            LoadedModelName = $"Ready: {_llamaService.LoadedModelName}";
        }
        else if (!string.IsNullOrEmpty(_settingsService.Settings.SelectedModelPath)
                 && File.Exists(_settingsService.Settings.SelectedModelPath))
        {
            LoadedModelName = $"Loading: {_settingsService.Settings.SelectedModelName}...";
            // Auto-load the previously selected model in background
            string autoLoadPath = _settingsService.Settings.SelectedModelPath!;
            _ = Task.Run(async () =>
            {
                // Guard: don't auto-load if another load is already in progress
                if (_llamaService.IsLoading) return;

                try
                {
                    var progress = new Progress<string>(s =>
                        App.Current?.Dispatcher.Invoke(() =>
                        {
                            StatusText = s;
                            LoadedModelName = s;
                        }));

                    // Ensure provider is Local
                    if (_providerManager.ActiveProviderType != AiProviderType.Local)
                        await _providerManager.SwitchProviderAsync(AiProviderType.Local);

                    await _llamaService.LoadModelAsync(autoLoadPath, progress);
                    App.Current?.Dispatcher.Invoke(() =>
                    {
                        StatusText = $"Ready: {_llamaService.LoadedModelName}";
                        SyncModelSelectionFromSettings();
                    });
                }
                catch (Exception ex)
                {
                    App.Current?.Dispatcher.Invoke(() =>
                    {
                        LoadedModelName = $"Failed to auto-load: {ex.Message}";
                        StatusText = "Model failed to load — go to Models tab";
                    });
                }
            });
        }
        else if (!string.IsNullOrEmpty(_settingsService.Settings.SelectedModelName))
        {
            LoadedModelName = _settingsService.Settings.SelectedModelName;
        }
    }

    // ─── Persistence ───

    private void RestoreSessions()
    {
        try
        {
            var savedSessions = _persistenceService.LoadSessionList();
            foreach (var session in savedSessions)
            {
                Sessions.Add(session);
            }

            // Restore the most recent session, or create new
            if (savedSessions.Count > 0)
            {
                // LoadSessionList returns metadata only — load the full session with messages
                var lastId = savedSessions.First().Id;
                var lastSession = _persistenceService.LoadSession(lastId);

                if (lastSession != null && lastSession.Messages.Count > 0)
                {
                    CurrentSession = lastSession;

                    // Replace the metadata-only entry in Sessions with the full one
                    for (int i = 0; i < Sessions.Count; i++)
                    {
                        if (Sessions[i].Id == lastId)
                        {
                            Sessions[i] = lastSession;
                            break;
                        }
                    }

                    foreach (var msg in lastSession.Messages)
                        Messages.Add(msg);

                    StatusText = $"Restored session: {lastSession.Title}";
                }
                else
                {
                    // Most recent session has no messages — use it as empty current session
                    CurrentSession = lastSession ?? savedSessions.First();
                    StatusText = "Ready";
                }

                _isDirty = false;
            }
            else
            {
                NewSession();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RestoreSessions failed: {ex.Message}");
            NewSession();
        }
    }

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

    private void LoadSession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            System.Diagnostics.Debug.WriteLine("LoadSession: sessionId is null/empty");
            return;
        }

        if (CurrentSession?.Id == sessionId) return;

        // Force save current session before switching
        if (CurrentSession != null && Messages.Count > 0)
        {
            _isDirty = true;
            AutoSave();
        }

        // Load from database (full session with messages)
        var session = _persistenceService.LoadSession(sessionId);

        if (session == null)
        {
            // Fallback: find in-memory session and try to reload from DB one more time
            var inMemory = Sessions.FirstOrDefault(s => s.Id == sessionId);
            if (inMemory == null)
            {
                StatusText = "Session not found";
                return;
            }

            // Use the in-memory session but warn if it has no messages
            session = inMemory;
        }

        // Switch — set CurrentSession BEFORE clearing Messages to avoid dirty flag issues
        CurrentSession = session;

        // Temporarily stop dirty tracking during load
        Messages.CollectionChanged -= OnMessagesChanged;
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
            ? $"Loaded: {session.Title} ({session.Messages.Count} messages)"
            : $"Empty session: {session.Title}";

        UpdateContextInfo();
        ScrollToBottom?.Invoke();
    }

    private void DeleteSession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        // Confirmation dialog for destructive operation
        var result = System.Windows.MessageBox.Show(
            "Are you sure you want to delete this session? This cannot be undone.",
            "Delete Session",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        // Delete from DB
        _persistenceService.DeleteSession(sessionId);

        // Remove from UI list immediately
        App.Current?.Dispatcher.Invoke(() =>
        {
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
                Messages.Clear();
                NewSession();
            }

            StatusText = "Session deleted";
        });
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
            int used = _contextMemoryService.CalculateTotalTokens(messages);
            int max = _contextMemoryService.MaxContextTokens;
            ContextUsagePercent = max > 0 ? Math.Min(100.0, (double)used / max * 100.0) : 0;
            ContextUsageText = $"{used:N0} / {max:N0}";
            MemoryUsageText = _contextMemoryService.GetMemoryUsageDisplay();

            // Session stats
            SessionTurnCount = messages.Count(m => m.Role == MessageRole.Assistant);
            SessionTotalTokens = messages.Where(m => m.Role == MessageRole.Assistant).Sum(m => m.TokenCount);

            // LED-style context level — 5 levels with labels
            if (ContextUsagePercent < 25)
            {
                ContextLevelLabel = "FRESH";
                ContextLevelColor = "Green";
                ShowContextWarning = false;
                ContextWarningText = "";
            }
            else if (ContextUsagePercent < 50)
            {
                ContextLevelLabel = "OK";
                ContextLevelColor = "Teal";
                ShowContextWarning = false;
                ContextWarningText = "";
            }
            else if (ContextUsagePercent < 75)
            {
                ContextLevelLabel = "WARM";
                ContextLevelColor = "Yellow";
                ShowContextWarning = false;
                ContextWarningText = "";
            }
            else if (ContextUsagePercent < 90)
            {
                ContextLevelLabel = "HOT";
                ContextLevelColor = "Peach";
                ShowContextWarning = true;
                ContextWarningText = "⚠ Context getting full — responses may lose quality. Consider starting a New Chat soon.";
            }
            else
            {
                ContextLevelLabel = "FULL";
                ContextLevelColor = "Red";
                ShowContextWarning = true;
                ContextWarningText = "🔴 Context almost full! Start a New Chat NOW for best results. The AI is losing earlier context.";
            }
        }
        catch { /* ignore during initialization */ }
    }

    // ─── GPU Live Stats ───
    private void UpdateGpuStats()
    {
        try
        {
            var stats = _gpuDetection.GetLiveStats();
            if (stats != null)
            {
                GpuStatsText = stats.StatusBarDisplay;
                GpuTempC = stats.TemperatureC;
                GpuUsagePercent = stats.GpuUtilization;
            }
            else
            {
                var info = _gpuDetection.DetectGpu();
                GpuStatsText = info.VramTotalBytes > 0
                    ? $"💾 {info.VramDisplay} ({info.GpuBrand})"
                    : "CPU Mode";
            }
        }
        catch
        {
            GpuStatsText = "";
        }
    }

    // ─── Open / Close Project Folder ───
    private void OpenFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Open Project Folder",
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FolderName))
        {
            SetWorkingDirectory(dialog.FolderName);
        }
    }

    public void SetWorkingDirectory(string path)
    {
        _fileSystemService.WorkingDirectory = path;
        WorkingDirectory = path;
        OnPropertyChanged(nameof(ProjectName));

        // Auto-enable agentic mode when a project is open
        AgenticMode = true;

        StatusText = $"Project: {ProjectName}";

        // Detect git info
        _ = DetectGitInfoAsync();

        // Add system message
        Messages.Add(new ChatMessage
        {
            Role = MessageRole.System,
            Content = $"\U0001F4C1 Opened project: {path}\nAgent mode enabled \u2014 I can now read, edit, create files and use Git in this project.",
        });
        ScrollToBottom?.Invoke();
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

    private Task CloneRepo()
    {
        StatusText = "Type the GitHub URL in chat: e.g. 'clone https://github.com/owner/repo'";
        return Task.CompletedTask;
    }

    private void CloseFolder()
    {
        _fileSystemService.WorkingDirectory = string.Empty;
        WorkingDirectory = string.Empty;
        AgenticMode = false;
        OnPropertyChanged(nameof(ProjectName));
        StatusText = "Ready";
    }

    private void NewSession()
    {
        // Save current session before creating new
        if (CurrentSession != null && CurrentSession.Messages.Count > 0)
        {
            _isDirty = true;
            AutoSave();
        }

        // Create new session
        var session = new ChatSession();
        CurrentSession = session;
        Messages.Clear();
        Sessions.Insert(0, session);
        _isDirty = false;

        // Persist immediately so LoadSession can find it later
        try { _persistenceService.SaveSession(session); }
        catch { /* ignore */ }

        StatusText = "Ready";
    }

    // ─── Send Message ───
    private async Task SendMessage()
    {
        string input = UserInput?.Trim() ?? "";
        if (string.IsNullOrEmpty(input) || IsGenerating) return;

        if (!_providerManager.ActiveProvider.IsReady)
        {
            string errorText = _providerManager.ActiveProviderType == AiProviderType.Local
                ? "⚠ **No model loaded.** Please go to the **Models** tab and load a GGUF model first."
                : $"⚠ **{_providerManager.ActiveProvider.DisplayName} not configured.** Please add your API key in **Settings**.";
            StatusText = _providerManager.ActiveProviderType == AiProviderType.Local
                ? "No model loaded"
                : $"{_providerManager.ActiveProvider.DisplayName} not configured";
            var errorMsg = new ChatMessage
            {
                Role = MessageRole.Assistant,
                Content = errorText,
                HasError = true,
            };
            Messages.Add(errorMsg);
            CurrentSession?.Messages.Add(errorMsg);
            ScrollToBottom?.Invoke();
            return;
        }

        // ─── Slash Command / Skill Interception ───
        string skillPromptOverride = "";
        if (input.StartsWith("/"))
        {
            var parts = input.TrimStart('/').Split(' ', 2);
            string skillName = parts[0];
            string skillArgs = parts.Length > 1 ? parts[1] : "";
            var skill = _skillService.GetSkillByName(skillName);

            if (skill != null && skill.UserInvocable)
            {
                // Replace input with skill prompt + user args
                skillPromptOverride = skill.PromptContent;
                if (!string.IsNullOrEmpty(skillArgs))
                    skillPromptOverride += $"\n\nUser arguments: {skillArgs}";
                input = skillPromptOverride;
            }
        }

        var userMsg = new ChatMessage { Role = MessageRole.User, Content = input };
        Messages.Add(userMsg);
        CurrentSession?.Messages.Add(userMsg);
        UserInput = string.Empty;
        CanSend = false;
        IsGenerating = true;
        StatusText = "Generating...";
        ScrollToBottom?.Invoke();

        _cts = new CancellationTokenSource();

        try
        {
            // Skills always run in agentic mode
            if (!string.IsNullOrEmpty(skillPromptOverride) && HasProject)
                await RunAgentic(input, _cts.Token);
            else if (AgenticMode && HasProject)
                await RunAgentic(input, _cts.Token);
            else if (AutoExecute)
                await RunWithAutoExecution(input, _cts.Token);
            else
                await RunStreaming(input, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Generation cancelled.";
        }
        catch (Exception ex)
        {
            string safeMsg = SanitizeErrorMessage(ex.Message);
            StatusText = $"Error: {safeMsg}";
            var errorMsg = new ChatMessage
            {
                Role = MessageRole.Assistant,
                Content = $"**Error:** {safeMsg}",
                HasError = true,
            };
            Messages.Add(errorMsg);
            CurrentSession?.Messages.Add(errorMsg);
        }
        finally
        {
            IsGenerating = false;
            CanSend = true;
            _cts?.Dispose();
            _cts = null;
            UpdateContextInfo();

            // Save immediately after each completed interaction
            SaveNow();
        }
    }

    // ─── Agentic Mode (tool use loop) ───
    private async Task RunAgentic(string input, CancellationToken ct)
    {
        var progress = new Progress<string>(status =>
            App.Current?.Dispatcher.Invoke(() => StatusText = status));

        var history = CurrentSession?.Messages
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant or MessageRole.ToolAction)
            .SkipLast(1)  // Skip the user message just added — it's passed separately as `input`
            .ToList() ?? new();

        // ─── Real-time streaming message for agentic loop ───
        ChatMessage? streamingMsg = null;
        int lastStreamStep = -1;
        int scrollThrottle = 0;

        void OnStreamToken(string token, int step)
        {
            App.Current?.Dispatcher.Invoke(() =>
            {
                // New step → create a new streaming bubble
                if (step != lastStreamStep)
                {
                    // Finalize previous streaming message
                    if (streamingMsg != null)
                        streamingMsg.IsStreaming = false;

                    streamingMsg = new ChatMessage
                    {
                        Role = MessageRole.Assistant,
                        Content = "",
                        IsStreaming = true,
                    };
                    Messages.Add(streamingMsg);
                    lastStreamStep = step;
                }

                if (streamingMsg != null)
                    streamingMsg.Content += token;

                // Throttle scroll-to-bottom (every 20 tokens)
                if (++scrollThrottle % 20 == 0)
                    ScrollToBottom?.Invoke();
            });
        }

        // Listen for real-time thinking updates during agent execution
        void OnThinking(string text, int step)
        {
            // Thinking text now comes via streaming — only add if no streaming msg
            if (string.IsNullOrWhiteSpace(text) || !ShowThinking) return;

            App.Current?.Dispatcher.Invoke(() =>
            {
                // Finalize streaming message before adding thinking
                if (streamingMsg != null)
                {
                    streamingMsg.IsStreaming = false;
                    streamingMsg = null;
                }
            });
        }

        _agentService.OnThinkingUpdate += OnThinking;
        _agentService.OnAgenticStreamingToken += OnStreamToken;
        try
        {
            var result = await _agentService.ExecuteAgenticAsync(history, input, progress, ct);

        // Finalize streaming message
        App.Current?.Dispatcher.Invoke(() =>
        {
            if (streamingMsg != null)
            {
                streamingMsg.IsStreaming = false;
                // Remove the streaming msg — we'll add the final clean response below
                Messages.Remove(streamingMsg);
                CurrentSession?.Messages.Remove(streamingMsg);
                streamingMsg = null;
            }
        });

        // Add final response
        if (!string.IsNullOrWhiteSpace(result.FinalResponse))
        {
            string finalText = _agentToolService.StripToolCalls(result.FinalResponse);
            if (!string.IsNullOrWhiteSpace(finalText))
            {
                var assistantMsg = new ChatMessage
                {
                    Role = MessageRole.Assistant,
                    Content = finalText,
                    CodeBlocks = _codeExecutionService.ExtractCodeBlocks(finalText),
                };
                App.Current?.Dispatcher.Invoke(() =>
                {
                    Messages.Add(assistantMsg);
                    CurrentSession?.Messages.Add(assistantMsg);
                });
            }
        }

        StatusText = result.Success ? "Ready" : "Agent loop complete";
        ScrollToBottom?.Invoke();
        }
        finally
        {
            _agentService.OnThinkingUpdate -= OnThinking;
            _agentService.OnAgenticStreamingToken -= OnStreamToken;
        }
    }

    private void OnToolExecuted(ToolResult toolResult)
    {
        App.Current?.Dispatcher.Invoke(() =>
        {
            // Format arguments for display
            string? argsDisplay = null;
            string? inputSummary = null;
            if (toolResult.Arguments is { Count: > 0 })
            {
                // Filter out large content values for the args display
                var displayArgs = new List<string>();
                string? primaryArg = null;
                foreach (var (key, value) in toolResult.Arguments)
                {
                    // Truncate large values (file content, code blocks)
                    string displayValue = value.Length > 120
                        ? value[..120].Replace("\n", " ") + "..."
                        : value.Replace("\n", "\\n");
                    displayArgs.Add($"  {key}: {displayValue}");

                    // Pick the primary argument for the one-line summary
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

            var msg = new ChatMessage
            {
                Role = MessageRole.ToolAction,
                Content = toolResult.Summary,
                ToolName = toolResult.ToolName,
                ToolSummary = toolResult.Summary,
                ToolOutput = toolResult.Output,
                ToolSuccess = toolResult.Success,
                HasError = !toolResult.Success,
                ToolArguments = argsDisplay,
                ToolInputSummary = inputSummary,
                ToolOutputLines = outputLines,
            };
            Messages.Add(msg);
            CurrentSession?.Messages.Add(msg);
            ScrollToBottom?.Invoke();
        });
    }

    // ─── Streaming Mode ───
    private async Task RunStreaming(string input, CancellationToken ct)
    {
        var assistantMsg = new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = "",
            IsStreaming = true,
        };
        Messages.Add(assistantMsg);
        ScrollToBottom?.Invoke();

        var sb = new StringBuilder();
        int tokenCount = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var history = CurrentSession?.Messages
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant)
            .SkipLast(1)
            .ToList() ?? new();

        await foreach (var token in _agentService.ChatStreamAsync(history, input, ct))
        {
            sb.Append(token);
            tokenCount++;
            App.Current?.Dispatcher.Invoke(() =>
            {
                assistantMsg.Content = sb.ToString();
            });
            ScrollToBottom?.Invoke();
        }

        sw.Stop();

        // Finalize
        App.Current?.Dispatcher.Invoke(() =>
        {
            assistantMsg.IsStreaming = false;
            assistantMsg.TokenCount = tokenCount;
            assistantMsg.GenerationTimeMs = sw.ElapsedMilliseconds;
            string responseText = sb.ToString().Trim();

            if (string.IsNullOrWhiteSpace(responseText))
            {
                assistantMsg.Content = "⚠ No response was generated. The model may need to be reloaded, or try a different model.";
                assistantMsg.HasError = true;
                StatusText = "No response generated";
            }
            else
            {
                assistantMsg.Content = responseText;
                assistantMsg.CodeBlocks = _codeExecutionService.ExtractCodeBlocks(responseText);

                // Show per-turn stats
                double tps = assistantMsg.TokensPerSecond;
                double secs = sw.ElapsedMilliseconds / 1000.0;
                StatusText = $"Ready · {tokenCount} tokens · {secs:F1}s · {tps:F1} tok/s";
                LastTurnStats = $"{tokenCount} tokens · {secs:F1}s · {tps:F1} tok/s";
            }
        });

        CurrentSession?.Messages.Add(assistantMsg);
        ScrollToBottom?.Invoke();
    }

    // ─── Auto-Execute Mode ───
    private async Task RunWithAutoExecution(string input, CancellationToken ct)
    {
        var history = CurrentSession?.Messages
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant)
            .SkipLast(1)
            .ToList() ?? new();

        var progress = new Progress<string>(status =>
            App.Current?.Dispatcher.Invoke(() => StatusText = status));

        var result = await _agentService.ExecuteWithAutoFixAsync(history, input, progress, ct);

        var assistantMsg = new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = result.Response,
            CodeBlocks = result.CodeBlocks,
        };
        Messages.Add(assistantMsg);
        CurrentSession?.Messages.Add(assistantMsg);

        if (result.ExecutionResult != null)
        {
            var execMsg = new ChatMessage
            {
                Role = MessageRole.CodeExecution,
                Content = FormatExecutionResult(result),
                HasError = !result.ExecutionResult.Success,
            };
            Messages.Add(execMsg);
            CurrentSession?.Messages.Add(execMsg);
        }

        StatusText = result.Success ? "Ready" : "Code execution had issues";
        ScrollToBottom?.Invoke();
    }

    private static string FormatExecutionResult(AgentResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.Success ? "**Execution Successful**" : "**Execution Failed**");
        sb.AppendLine($"Attempts: {result.Attempts}");

        if (result.ExecutionResult != null)
        {
            if (!string.IsNullOrWhiteSpace(result.ExecutionResult.Output))
                sb.AppendLine($"\n**Output:**\n```\n{result.ExecutionResult.Output}\n```");
            if (!string.IsNullOrWhiteSpace(result.ExecutionResult.Error))
                sb.AppendLine($"\n**Errors:**\n```\n{result.ExecutionResult.Error}\n```");
        }
        return sb.ToString();
    }

    private void StopGeneration()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* CTS already disposed */ }
    }

    private async Task ExecuteCodeBlock(CodeBlock? block)
    {
        if (block == null) return;
        StatusText = $"Executing {block.Language} code...";
        IsGenerating = true;
        try
        {
            var result = await _codeExecutionService.ExecuteAsync(block.Code, block.Language);
            Messages.Add(new ChatMessage
            {
                Role = MessageRole.CodeExecution,
                Content = $"**{(result.Success ? "Output" : "Error")}:**\n```\n{result.FullOutput}\n```",
                HasError = !result.Success,
            });
            CurrentSession?.Messages.Add(Messages.Last());
            ScrollToBottom?.Invoke();
        }
        catch (Exception ex) { StatusText = $"Execution error: {ex.Message}"; }
        finally { IsGenerating = false; StatusText = "Ready"; }
    }

    private void CopyCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return;
        try { System.Windows.Clipboard.SetText(code); StatusText = "Copied!"; } catch { }
    }

    // ─── Model Selector ───

    /// <summary>
    /// Syncs the chat view's model selector with current settings/service state.
    /// Called when settings change (e.g. model loaded from Models tab) or on navigation.
    /// </summary>
    public void SyncModelSelectionFromSettings()
    {
        // Refresh model list to pick up newly downloaded models
        RefreshLocalModelsList();

        // Update loaded model display name
        if (_llamaService.IsModelLoaded)
            LoadedModelName = $"Ready: {_llamaService.LoadedModelName}";
        else if (!string.IsNullOrEmpty(_settingsService.Settings.SelectedModelName))
            LoadedModelName = _settingsService.Settings.SelectedModelName;
        else
            LoadedModelName = "No model loaded";
    }

    public void RefreshLocalModelsList()
    {
        var models = _huggingFaceService.GetLocalModels();
        LocalModels.Clear();
        foreach (var m in models)
            LocalModels.Add(m);

        // Pre-select the currently loaded model
        if (_llamaService.LoadedModelPath != null)
        {
            var loaded = LocalModels.FirstOrDefault(m =>
                string.Equals(m.LocalPath, _llamaService.LoadedModelPath, StringComparison.OrdinalIgnoreCase));
            if (loaded != null)
                _selectedLocalModel = loaded; // set backing field directly to avoid triggering load
        }
        else
        {
            _selectedLocalModel = null; // clear selection if no model loaded
        }
        OnPropertyChanged(nameof(SelectedLocalModel));
    }

    private async Task LoadModelFromChat(string? path)
    {
        if (string.IsNullOrEmpty(path) || _llamaService.IsLoading) return;
        if (_llamaService.LoadedModelPath == path)
        {
            // Model already loaded, just ensure provider is set to Local
            if (_providerManager.ActiveProviderType != AiProviderType.Local)
                await _providerManager.SwitchProviderAsync(AiProviderType.Local);
            return;
        }

        IsLoadingModel = true;
        StatusText = "Loading model...";
        try
        {
            // Ensure active provider is Local
            if (_providerManager.ActiveProviderType != AiProviderType.Local)
                await _providerManager.SwitchProviderAsync(AiProviderType.Local);

            var progress = new Progress<string>(s =>
                App.Current?.Dispatcher.Invoke(() => StatusText = s));
            await _llamaService.LoadModelAsync(path, progress);
            _settingsService.UpdateSettings(s =>
            {
                s.SelectedModelPath = path;
                s.SelectedModelName = Path.GetFileNameWithoutExtension(path);
            });
            StatusText = "Ready";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load: {ex.Message}";
        }
        finally
        {
            IsLoadingModel = false;
        }
    }

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

    /// <summary>Refresh database stats display.</summary>
    public void RefreshDbStats()
    {
        try
        {
            var stats = _persistenceService.GetStats();
            string sizeText = stats.SizeBytes < 1024 * 1024
                ? $"{stats.SizeBytes / 1024.0:F1} KB"
                : $"{stats.SizeBytes / (1024.0 * 1024.0):F1} MB";
            DbStatsText = $"{stats.Sessions} sessions  |  {stats.Messages} messages  |  {sizeText}";
        }
        catch { DbStatsText = ""; }
    }

    private void ClearChat()
    {
        Messages.Clear();
        CurrentSession?.Messages.Clear();
        _isDirty = true;
        StatusText = "Chat cleared.";
    }

    // ─── Code Review ───
    private async Task RunCodeReview()
    {
        if (!HasProject)
        {
            StatusText = "Open a project folder first to review code.";
            return;
        }
        if (IsGenerating) return;

        IsGenerating = true;
        CanSend = false;
        StatusText = "Reviewing code...";

        try
        {
            _cts = new CancellationTokenSource();

            var history = CurrentSession?.Messages
                .Where(m => m.Role is MessageRole.User or MessageRole.Assistant)
                .ToList() ?? new();

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

            Messages.Add(reviewMsg);
            CurrentSession?.Messages.Add(reviewMsg);
            StatusText = $"Review complete · {tokenEst} tokens · {sw.ElapsedMilliseconds / 1000.0:F1}s";
            LastTurnStats = $"{tokenEst} tokens · {sw.ElapsedMilliseconds / 1000.0:F1}s";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Review cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Review failed: {SanitizeErrorMessage(ex.Message)}";
        }
        finally
        {
            IsGenerating = false;
            CanSend = true;
            _cts?.Dispose();
            _cts = null;
            UpdateContextInfo();
            SaveNow();
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
