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
        LlamaInferenceService llamaService)
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

        AutoExecute = settingsService.Settings.AutoExecuteCode;

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

        // Listen for tool execution events
        _agentService.OnToolExecuted += OnToolExecuted;

        // Update context info when messages change
        Messages.CollectionChanged += (_, _) =>
        {
            UpdateContextInfo();
            _isDirty = true;
        };

        // Periodic RAM/context refresh every 5 seconds
        _memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _memoryTimer.Tick += (_, _) => UpdateContextInfo();
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
                // LoadSessionList returns metadata only, load full session with messages
                var lastId = savedSessions.First().Id;
                var lastSession = _persistenceService.LoadSession(lastId) ?? savedSessions.First();
                CurrentSession = lastSession;

                foreach (var msg in lastSession.Messages)
                    Messages.Add(msg);

                StatusText = $"Restored session: {lastSession.Title}";
                _isDirty = false;
            }
            else
            {
                NewSession();
            }
        }
        catch
        {
            NewSession();
        }
    }

    private void AutoSave()
    {
        if (!_isDirty || CurrentSession == null) return;

        try
        {
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
        catch { /* ignore save errors silently */ }
    }

    /// <summary>Force save the current session immediately.</summary>
    public void SaveNow()
    {
        _isDirty = true;
        AutoSave();
    }

    private void LoadSession(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        // Save current first
        AutoSave();

        var session = _persistenceService.LoadSession(sessionId);
        if (session == null) return;

        CurrentSession = session;
        Messages.Clear();
        foreach (var msg in session.Messages)
            Messages.Add(msg);

        StatusText = $"Loaded: {session.Title}";
        _isDirty = false;
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

        _persistenceService.DeleteSession(sessionId);

        // Remove from list
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
            NewSession();
        }
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

            // Context warning levels
            if (ContextUsagePercent >= 90)
            {
                ShowContextWarning = true;
                ContextWarningText = "⚠ Context almost full! Start a New Chat to avoid losing context.";
            }
            else if (ContextUsagePercent >= 75)
            {
                ShowContextWarning = true;
                ContextWarningText = "Context getting full — consider starting a New Chat soon.";
            }
            else
            {
                ShowContextWarning = false;
                ContextWarningText = "";
            }
        }
        catch { /* ignore during initialization */ }
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
        AutoSave();

        var session = new ChatSession();
        Sessions.Insert(0, session);
        CurrentSession = session;
        Messages.Clear();
        _isDirty = false;
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
            if (AgenticMode && HasProject)
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

        var result = await _agentService.ExecuteAgenticAsync(history, input, progress, ct);

        // Add step-by-step results to chat
        foreach (var step in result.Steps)
        {
            if (!string.IsNullOrWhiteSpace(step.ThinkingText))
            {
                var thinkingMsg = new ChatMessage
                {
                    Role = MessageRole.Assistant,
                    Content = step.ThinkingText,
                };
                App.Current?.Dispatcher.Invoke(() =>
                {
                    Messages.Add(thinkingMsg);
                    CurrentSession?.Messages.Add(thinkingMsg);
                });
            }
        }

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

    private void OnToolExecuted(ToolResult toolResult)
    {
        App.Current?.Dispatcher.Invoke(() =>
        {
            var msg = new ChatMessage
            {
                Role = MessageRole.ToolAction,
                Content = toolResult.Summary,
                ToolName = toolResult.ToolName,
                ToolSummary = toolResult.Summary,
                ToolOutput = toolResult.Output,
                ToolSuccess = toolResult.Success,
                HasError = !toolResult.Success,
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
        var history = CurrentSession?.Messages
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant)
            .SkipLast(1)
            .ToList() ?? new();

        await foreach (var token in _agentService.ChatStreamAsync(history, input, ct))
        {
            sb.Append(token);
            // ChatMessage implements INotifyPropertyChanged —
            // setting Content fires PropertyChanged, so the UI updates in-place.
            // No need to replace the item in the ObservableCollection.
            App.Current?.Dispatcher.Invoke(() =>
            {
                assistantMsg.Content = sb.ToString();
            });
            ScrollToBottom?.Invoke();
        }

        // Finalize
        App.Current?.Dispatcher.Invoke(() =>
        {
            assistantMsg.IsStreaming = false;
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
                StatusText = "Ready";
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
