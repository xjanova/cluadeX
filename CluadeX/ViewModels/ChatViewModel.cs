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

public partial class ChatViewModel : ViewModelBase
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
    private readonly PermissionService? _permissionService;
    private readonly DispatcherTimer _memoryTimer;
    private readonly DispatcherTimer _autoSaveTimer;

    // ─── Running turns ───
    // Each turn is a ChatRun (ChatViewModel.Runs.cs) that writes into its own chat's live list, so switching chats
    // never stops it and never moves its output into another chat.

    private string _userInput = string.Empty;
    private bool _autoExecute;
    private bool _agenticMode;
    private bool _autonomousMode;
    private string _statusText = "";
    private ChatSession? _currentSession;
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
    private DispatcherTimer? _elapsedTimer;
    private string _chatSearchQuery = "";
    private bool _isChatSearchVisible;

    /// <summary>The chat on screen. A running turn keeps writing into its own chat's list (ChatRun.Messages) — when
    /// that chat is opened again its live list is put back on screen, so nothing is lost or duplicated.</summary>
    public ObservableCollection<ChatMessage> Messages
    {
        get => _messages;
        private set
        {
            if (ReferenceEquals(_messages, value)) return;
            _messages.CollectionChanged -= OnMessagesChanged;
            _messages = value;
            _messages.CollectionChanged += OnMessagesChanged;
            OnPropertyChanged();
        }
    }
    private ObservableCollection<ChatMessage> _messages = new();
    public ObservableCollection<ChatSession> Sessions { get; } = new();
    /// <summary>Sessions filtered by the currently-open project folder. Bound by the sidebar.
    /// When no folder is open, mirrors Sessions in full; otherwise only sessions whose
    /// ProjectPath equals the current WorkingDirectory (or have empty ProjectPath for legacy rows).</summary>
    public ObservableCollection<ChatSession> VisibleSessions { get; } = new();
    public ObservableCollection<SearchResult> SearchResults { get; } = new();
    public ObservableCollection<ModelInfo> LocalModels { get; } = new();

    public string UserInput
    {
        get => _userInput;
        set
        {
            if (!SetProperty(ref _userInput, value)) return;
            if (string.IsNullOrEmpty(value)) OnInputCleared();
            else NoteUserActivity();   // typing: the screen is in use (ScreenGuard)
        }
    }

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

    /// <summary>
    /// "Accept edits" for this session: write_file / edit_file / multi_edit / create_directory INSIDE the open
    /// project run without a prompt. Never commands, MCP tools, browsing, background processes, paths outside the
    /// project, nor files that run code later (.git, .cluadex, .husky, .vscode, .claude, CI workflows, build files —
    /// PermissionService.IsCodeRunningConfig); and the automatic build check after such an edit asks first. Off by
    /// default (Settings ▸ AcceptEditsDefault), confirmed when switched on, never for a drive root, the user folder or
    /// a system folder, and it ends on a project switch and on restart — it is bound to the folder it was switched on for.
    /// </summary>
    public bool AcceptEdits
    {
        get => _permissionService?.AcceptsEditsIn(_fileSystemService.HasWorkingDirectory ? _fileSystemService.WorkingDirectory : null) == true;
        set
        {
            if (_permissionService == null) return;
            if (value == AcceptEdits) return;
            if (value)
            {
                string folder = _fileSystemService.HasWorkingDirectory ? _fileSystemService.WorkingDirectory : "";
                bool unsafeFolder = folder.Length > 0 && PermissionService.IsUnsafeAcceptEditsRoot(folder, _settingsService.DataRoot);
                if (unsafeFolder) PostAcceptEditsRefusal(folder);
                if (folder.Length == 0 || unsafeFolder
                    || !Confirm(Loc("chat.acceptEdits.confirm"), Loc("chat.acceptEdits.confirmTitle"))
                    || !_permissionService.SetAcceptEdits(folder))
                {
                    // Declined, no project, or a folder it must not cover: the CheckBox already shows a tick — re-raise so it clears.
                    App.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(AcceptEdits)));
                    return;
                }
            }
            else
            {
                _permissionService.SetAcceptEdits(null);
            }
            OnPropertyChanged();
        }
    }

    /// <summary>"Accept edits can't be used on X: it is a drive, your user folder or a system folder."</summary>
    private void PostAcceptEditsRefusal(string folder)
    {
        Messages.Add(new ChatMessage { Role = MessageRole.System, Content = "⚠ " + LocF("chat.acceptEdits.unsafeFolder", folder) });
        StatusText = LocF("chat.acceptEdits.unsafeFolder", folder);
        ScrollToBottom?.Invoke();
    }

    /// <summary>Folders a pipe caller's "acceptEdits": true was approved for by the user in this app session.</summary>
    private readonly HashSet<string> _pipeAcceptEditsApproved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A project switch ends Accept edits; it starts on again only when the user made that the default.</summary>
    private void ResetAcceptEditsForProject(string? projectRoot)
    {
        if (_permissionService == null) return;
        bool on = _settingsService.Settings.AcceptEditsDefault && !string.IsNullOrWhiteSpace(projectRoot);
        _permissionService.SetAcceptEdits(on ? projectRoot : null);
        OnPropertyChanged(nameof(AcceptEdits));
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
    public ChatSession? CurrentSession
    {
        get => _currentSession;
        set { if (SetProperty(ref _currentSession, value)) { RaiseRunStateChanged(); RefreshStickyPlan(); OnPropertyChanged(nameof(FollowEditsHere)); } }
    }
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
        if (_bulkSessions) return;   // the start-up load adds every chat, then rebuilds once

        // No project open, or user opted to see everything → passthrough
        bool filter = HasProject && !ShowAllProjectSessions;
        string currentProject = WorkingDirectory ?? "";

        // Split chats that were put away leave the list (kept, not deleted) unless the owner shows them.
        bool Hidden(ChatSession s) => s.IsArchived && !ShowArchivedSplits && !ReferenceEquals(s, CurrentSession);

        // Chats with work going on (running, waiting for the owner, finished unseen) always show, first, whatever the
        // project filter (owner 2026-10-17: the sidebar must keep the working projects in view) — a background task in
        // another project was otherwise nowhere in the list.
        var active = Sessions.Where(IsActiveChat).ToList();
        var flat = new List<ChatSession>(active);
        _activeChatsShown = string.Join("|", active.Select(s => s.Id));

        foreach (var s in Sessions)
        {
            if (active.Contains(s) || Hidden(s)) continue;
            if (filter)
            {
                // Match sessions for this project. Legacy rows (empty ProjectPath) also pass so
                // we don't hide the user's pre-migration history. A split chat follows its parent into view.
                bool matches = string.IsNullOrEmpty(s.ProjectPath)
                    || PathEquals(s.ProjectPath, currentProject)
                    || (s.ParentId is { } pid && flat.Any(p => p.Id == pid));
                if (!matches) continue;
            }
            flat.Add(s);
        }

        // Unchanged (a chat switch, a run's state, a title typed elsewhere): the sidebar keeps its rows. Changed: only what
        // differs is touched. Clearing and refilling re-grouped and re-templated everything on the UI thread, and threw the
        // list to another scroll position (its rows are built as they come into view, so it loses its place). The view
        // groups a row when it is added, so a chat that moved to another day's group goes out and comes back in.
        var tree = SplitTree(flat).ToList();
        var wanted = tree.ToHashSet();
        for (int i = VisibleSessions.Count - 1; i >= 0; i--)
            if (!wanted.Contains(VisibleSessions[i]) || _rowGroups.GetValueOrDefault(VisibleSessions[i]) != VisibleSessions[i].DateGroupLabel)
                VisibleSessions.RemoveAt(i);
        for (int i = 0; i < tree.Count; i++)
        {
            if (i < VisibleSessions.Count && ReferenceEquals(VisibleSessions[i], tree[i])) continue;
            int at = VisibleSessions.IndexOf(tree[i]);
            if (at >= 0) VisibleSessions.Move(at, i);
            else VisibleSessions.Insert(i, tree[i]);
        }
        _rowGroups = tree.ToDictionary(s => s, s => s.DateGroupLabel);
        UpdateSplitSummaries();
        OnPropertyChanged(nameof(VisibleSessionsCount));

        int total = Sessions.Count(s => !Hidden(s));
        SidebarFilterText = total == 0 ? ""
            : filter ? LocF("sidebar.filter.project", ProjectName, VisibleSessions.Count, total)
            : LocF("sidebar.filter.all", total);
        AllProjectsTip = !HasProject ? Loc("sidebar.filter.noProjectTip")
            : ShowAllProjectSessions ? LocF("sidebar.filter.onlyTip", ProjectName)
            : LocF("nav.allProjectsTip", total - VisibleSessions.Count);
    }

    private Dictionary<ChatSession, string> _rowGroups = new();

    private string _sidebarFilterText = "";
    /// <summary>The line above the chat list: whose chats it holds and how many (owner 2026-10-31: the all-projects button
    /// looked dead, because the newest rows on top often stay the same either way).</summary>
    public string SidebarFilterText { get => _sidebarFilterText; private set => SetProperty(ref _sidebarFilterText, value); }

    private string _allProjectsTip = "";
    /// <summary>What the all-projects button does from here (it cannot do anything while no project is open).</summary>
    public string AllProjectsTip { get => _allProjectsTip; private set => SetProperty(ref _allProjectsTip, value); }

    private bool _bulkSessions;

    /// <summary>
    /// The list as a tree (owner 2026-10-18: "ทำผังว่าแยกไปที่ไหนทำอะไร"): each split chat right under the chat that
    /// opened it, indented one step, in that chat's day group. A tree comes up where its first member stood (a working
    /// split chat lifts its parent with it). A split chat whose parent is not in the list stands on its own.
    /// </summary>
    internal static List<ChatSession> SplitTree(IReadOnlyList<ChatSession> flat)
    {
        var byId = new Dictionary<string, ChatSession>(StringComparer.Ordinal);
        foreach (var s in flat) byId.TryAdd(s.Id, s);
        var kids = new Dictionary<string, List<ChatSession>>(StringComparer.Ordinal);
        foreach (var s in flat)
            if (s.ParentId is { } p && byId.ContainsKey(p) && p != s.Id)
                (kids.TryGetValue(p, out var l) ? l : kids[p] = new()).Add(s);

        var result = new List<ChatSession>(flat.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(ChatSession s, int depth, string? group, ChatSession? parent, bool last = false)
        {
            if (!seen.Add(s.Id)) return;
            s.GroupOverride = group;
            s.SplitDepth = depth;
            s.IsLastSplit = last;
            s.ProjectAsParent = parent != null
                && string.Equals((s.ProjectPath ?? "").TrimEnd('\\', '/'), (parent.ProjectPath ?? "").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            result.Add(s);
            if (kids.TryGetValue(s.Id, out var children))
                for (int i = 0; i < children.Count; i++)
                    Add(children[i], depth + 1, group ?? s.DateGroupLabel, s, i == children.Count - 1);
        }
        ChatSession RootOf(ChatSession s)
        {
            var hops = new HashSet<string>(StringComparer.Ordinal) { s.Id };
            while (s.ParentId is { } p && byId.TryGetValue(p, out var up) && hops.Add(up.Id)) s = up;
            return s;
        }
        foreach (var s in flat) Add(RootOf(s), 0, null, null);
        foreach (var s in flat) Add(s, 0, null, null);   // anything a loop left out
        return result;
    }

    /// <summary>Each chat that split work off says how its split chats stand ("↳ 3 split chats · 1 working · 2 done").</summary>
    private void UpdateSplitSummaries()
    {
        var links = ChatLinkStore.Current;
        foreach (var s in Sessions)
        {
            var kids = links?.ChildrenOf(s.Id) ?? new();
            if (kids.Count == 0) { s.SplitSummary = ""; s.HasFinishedSplits = false; s.OpenSplitCount = 0; continue; }
            int working = 0, done = 0, away = 0;
            foreach (var (id, link) in kids)
            {
                var child = Sessions.FirstOrDefault(c => c.Id == id);
                if (link.ArchivedAt != null) away++;
                else if (child != null && RunOf(child) != null) working++;
                else done++;
            }
            var parts = new List<string> { LocF("chat.split.summary", kids.Count) };
            if (working > 0) parts.Add(LocF("chat.split.working", working));
            if (done > 0) parts.Add(LocF("chat.split.done", done));
            if (away > 0) parts.Add(LocF("chat.split.away", away));
            s.SplitSummary = string.Join(" · ", parts);
            s.HasFinishedSplits = done > 0;
            s.OpenSplitCount = working + done;
        }
    }

    private bool _showArchivedSplits;
    /// <summary>Show split chats that were put away (dimmed, with "bring back").</summary>
    public bool ShowArchivedSplits
    {
        get => _showArchivedSplits;
        set { if (_showArchivedSplits != value) { _showArchivedSplits = value; OnPropertyChanged(); RebuildVisibleSessions(); } }
    }

    /// <summary>The link store changed (a split recorded, a chat put away or brought back): redraw the tree.</summary>
    private void OnSplitLinkChanged(string id)
    {
        Sessions.FirstOrDefault(s => s.Id == id)?.RaiseSplitChanged();
        RebuildVisibleSessions();
    }

    /// <summary>Put a finished split chat away (and its finished split chats). A chat still working stays. The chat on
    /// screen, when put away, hands the screen to its parent. Returns false when it was refused.</summary>
    private bool ArchiveSplit(ChatSession s)
    {
        if (!s.IsSplit || RunOf(s) != null) return false;
        foreach (var (kidId, _) in ChatLinkStore.Current?.ChildrenOf(s.Id) ?? new())
            if (Sessions.FirstOrDefault(c => c.Id == kidId) is { } kid && RunOf(kid) == null) ArchiveSplit(kid);
        ChatLinkStore.Current?.SetArchived(s.Id, true);
        s.RaiseSplitChanged();
        if (ReferenceEquals(s, CurrentSession) && s.ParentId is { } parent)
            _ = LoadSessionAsync(parent);
        return true;
    }

    /// <summary>Sidebar: put this split chat away.</summary>
    public ICommand ArchiveSplitCommand => _archiveSplitCommand ??= new RelayCommand<string>(id =>
    {
        if (Sessions.FirstOrDefault(s => s.Id == id) is not { } s) return;
        if (!ArchiveSplit(s)) StatusText = Loc("chat.split.stillWorking");
        RebuildVisibleSessions();
    });
    private ICommand? _archiveSplitCommand;

    /// <summary>Sidebar: bring a put-away split chat back into the list.</summary>
    public ICommand UnarchiveSplitCommand => _unarchiveSplitCommand ??= new RelayCommand<string>(id =>
    {
        if (string.IsNullOrEmpty(id)) return;
        ChatLinkStore.Current?.SetArchived(id, false);
        Sessions.FirstOrDefault(s => s.Id == id)?.RaiseSplitChanged();
        RebuildVisibleSessions();
    });
    private ICommand? _unarchiveSplitCommand;

    /// <summary>Sidebar, on a chat that split work off: put away every finished split chat of it.</summary>
    public ICommand ArchiveFinishedSplitsCommand => _archiveFinishedSplitsCommand ??= new RelayCommand<string>(id =>
    {
        if (string.IsNullOrEmpty(id)) return;
        foreach (var (kidId, link) in ChatLinkStore.Current?.ChildrenOf(id) ?? new())
            if (link.ArchivedAt == null && Sessions.FirstOrDefault(c => c.Id == kidId) is { } kid) ArchiveSplit(kid);
        RebuildVisibleSessions();
    });
    private ICommand? _archiveFinishedSplitsCommand;

    /// <summary>Sidebar: show / hide the split chats that were put away.</summary>
    public ICommand ToggleArchivedSplitsCommand => _toggleArchivedSplitsCommand ??= new RelayCommand(() => ShowArchivedSplits = !ShowArchivedSplits);
    private ICommand? _toggleArchivedSplitsCommand;

    /// <summary>
    /// close_chat (UI thread): the split chats of <paramref name="parentId"/> — list them (<paramref name="target"/> empty
    /// or "list"), or put away the finished ones ("done") or one by id / title. A chat still working is never closed.
    /// </summary>
    private string CloseChatsForAgent(string? parentId, string target)
    {
        if (parentId == null) return "No chat is asking (close_chat works from a chat that split work off with new_chat).";
        var kids = ChatLinkStore.Current?.ChildrenOf(parentId) ?? new();
        if (kids.Count == 0) return "This chat has not split off any chats (new_chat), so there is nothing to close.";

        string StateOf(string id, ChatLink link) =>
            link.ArchivedAt != null ? "put away"
            : Sessions.FirstOrDefault(c => c.Id == id) is { } c && RunOf(c) != null ? "working"
            : "finished";
        string Line(string id, ChatLink link)
        {
            var c = Sessions.FirstOrDefault(x => x.Id == id);
            string where = string.IsNullOrEmpty(c?.ProjectPath) ? "" : $" in {c!.ProjectPath}";
            return $"- \"{c?.Title ?? "?"}\" (id {id}){where} — {link.Task} — {StateOf(id, link)}";
        }
        string List() => "Split chats of this chat:\n" + string.Join("\n", kids.Select(k => Line(k.Id, k.Link)));

        target = (target ?? "").Trim();
        if (target.Length == 0 || target.Equals("list", StringComparison.OrdinalIgnoreCase)) return List();

        bool all = target.ToLowerInvariant() is "done" or "finished" or "all" or "all-done" or "*";
        var picked = kids.Where(k => k.Link.ArchivedAt == null && (all
            || k.Id == target
            || string.Equals(Sessions.FirstOrDefault(c => c.Id == k.Id)?.Title, target, StringComparison.OrdinalIgnoreCase))).ToList();
        if (picked.Count == 0) return $"No open split chat matches \"{target}\".\n" + List();

        var closed = new List<string>(); var busy = new List<string>();
        foreach (var (id, _) in picked)
        {
            var c = Sessions.FirstOrDefault(x => x.Id == id);
            if (c == null) { ChatLinkStore.Current?.SetArchived(id, true); closed.Add(id); continue; }
            if (ArchiveSplit(c)) closed.Add(c.Title); else busy.Add(c.Title);
        }
        RebuildVisibleSessions();   // at once: the store's own notice redraws again a moment later, harmlessly
        string said = closed.Count > 0 ? $"Put away {closed.Count}: {string.Join(", ", closed)} (kept, not deleted; the user can bring them back)." : "Nothing was put away.";
        if (busy.Count > 0) said += $" Still working, left open: {string.Join(", ", busy)}.";
        return said;
    }

    private static string? FirstLineOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string first = text.Trim().Split('\n')[0].Trim();
        return first.Length > 80 ? first[..80] + "…" : first;
    }

    public int VisibleSessionsCount => VisibleSessions.Count;

    /// <summary>A chat with a run going, waiting for the owner, or finished while the owner looked elsewhere.</summary>
    private bool IsActiveChat(ChatSession s) => RunOf(s) != null || s.RunStatus is "working" or "waiting" or "done";

    /// <summary>The active chats the sidebar was last built with: rebuilt only when that set changes.</summary>
    private string _activeChatsShown = "";

    private void RebuildSidebarIfActiveChanged()
    {
        string now = string.Join("|", Sessions.Where(IsActiveChat).Select(s => s.Id));
        if (now != _activeChatsShown) RebuildVisibleSessions();
    }

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
    public double ContextUsagePercent
    {
        get => _contextUsagePercent;
        set { if (SetProperty(ref _contextUsagePercent, value)) OnPropertyChanged(nameof(ContextFraction)); }
    }
    /// <summary>0–1 of the context window used (drives the status bar's bar).</summary>
    public double ContextFraction => Math.Clamp(_contextUsagePercent / 100.0, 0, 1);
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

    private bool _thinkToggleVisible = true;
    /// <summary>The Think box only shows where the toggle changes the request: every Claude model (on the ones that
    /// always think it asks for the reasoning text), OpenAI models with reasoning effort, Gemini 2.5+. Hidden for
    /// models on this PC, Ollama, Claude Code's own plan and non-reasoning API models — there it did nothing.</summary>
    public bool ThinkToggleVisible { get => _thinkToggleVisible; private set => SetProperty(ref _thinkToggleVisible, value); }
    private bool _effortVisible = true;
    /// <summary>The Effort box only shows where the pick reaches the request: Claude models with output_config.effort,
    /// OpenAI reasoning models, Gemini 2.5+, Claude Code (--effort). A model on this PC or Ollama ignores it.</summary>
    public bool EffortVisible { get => _effortVisible; private set => SetProperty(ref _effortVisible, value); }
    private string _reasoningEffort = EffortLevels.Auto;
    public string ReasoningEffort
    {
        get => _reasoningEffort;
        set
        {
            string v = EffortLevels.Normalize(value);
            if (SetProperty(ref _reasoningEffort, v))
            {
                _settingsService.UpdateSettings(s => s.ReasoningEffort = v);
                // The last request's effort belongs to the old pick: show the new one until a request carries it.
                LiveModelReport.Forget(_providerManager.ActiveProviderType);
                OnPropertyChanged(nameof(LiveModelEffortText)); OnPropertyChanged(nameof(LiveModelEffortTip)); OnPropertyChanged(nameof(LiveEffortSuffix)); OnPropertyChanged(nameof(LiveEffortLevel));
            }
        }
    }

    // ─── Usage strip (top of the chat): what is answering, rate limit left, what it cost ───
    private string _activeProviderLabel = "", _activeModelLabel = "", _rateLimitText = "", _rateLimitTip = "";
    private string _sessionCostText = "$0.00";
    private double _rateLimitFraction, _rateLimitUsedPercent;
    private bool _hasRateLimit;
    public string ActiveProviderLabel { get => _activeProviderLabel; set => SetProperty(ref _activeProviderLabel, value); }
    public string ActiveModelLabel
    {
        get => _activeModelLabel;
        set { if (SetProperty(ref _activeModelLabel, value)) OnPropertyChanged(nameof(LiveModelEffortText)); OnPropertyChanged(nameof(LiveEffortSuffix)); OnPropertyChanged(nameof(LiveEffortLevel)); }
    }

    /// <summary>The level "auto" is really using in this chat's run right now ("low" / "medium" / "high" / "xhigh"), "" when
    /// no run is going or the effort is not "auto". The effort box shows it after "auto".</summary>
    public string LiveEffortLevel
    {
        get
        {
            if (EffortLevels.Normalize(_settingsService.Settings.ReasoningEffort) != EffortLevels.Auto) return "";
            string? sent = LiveSnapshot()?.Effort;
            return sent switch
            {
                null => EffortShown(RunOf(CurrentSession)?.Scope?.LiveEffort) ?? "",
                LiveModelReport.NoEffort => "",   // the Effort box is not shown for such a provider; the chip says it
                _ when EffortShown(sent) is { } named && named != sent => named,
                LiveModelReport.ModelDefault => Loc("live.effort.modelDefault"),
                _ => sent,
            };
        }
    }

    /// <summary>What really answers this chat now (LiveModelReport): this chat's run's last request, else the provider's
    /// latest — or, for a provider with no effort at all (a model on this PC, Gemini CLI, the BrainX room), that fact.
    /// A report for another model than the one picked (the user switched since) is not shown.</summary>
    private LiveModelReport.Snapshot? LiveSnapshot()
    {
        var type = _providerManager.PickedProviderType;
        var run = RunOf(CurrentSession);
        var snap = LiveModelReport.For(run?.Scope, type);
        if (snap != null && run == null && !ReportMatchesPick(type, snap.Model)) snap = null;
        // A run whose loop picked a level but whose provider reports nothing (yet): that pick is what goes out.
        if (snap == null && run?.Scope?.LiveEffort is { Length: > 0 } picked)
            snap = new LiveModelReport.Snapshot(type, null, picked, false, DateTime.Now) { Why = run.Scope.EffortWhy };
        if (snap == null && type is AiProviderType.Local or AiProviderType.LlamaServer or AiProviderType.Ollama
                or AiProviderType.GeminiDev or AiProviderType.AntigravityDev or AiProviderType.BrainX)
            snap = new LiveModelReport.Snapshot(type, null, LiveModelReport.NoEffort, false, DateTime.Now);
        return snap;
    }

    private bool ReportMatchesPick(AiProviderType type, string? reported)
    {
        if (string.IsNullOrWhiteSpace(reported)) return true;
        string? configured = _settingsService.Settings.ProviderConfigs.TryGetValue(type.ToString(), out var cfg) ? cfg.EffectiveModelId?.Trim() : null;
        if (string.IsNullOrWhiteSpace(configured) || configured is "default" or "auto") return true;
        return reported.StartsWith(configured, StringComparison.OrdinalIgnoreCase)
               || LiveModelReport.FriendlyModel(reported) == LiveModelReport.FriendlyModel(configured);
    }

    /// <summary>A run's live effort in words: a level as it is; Claude Dev's "auto", which the CLI does not report as a level,
    /// as what it is — the CLI picking per turn, or the CLI's default (ClaudeDevProvider.CliAutoEffort).</summary>
    private string? EffortShown(string? live) => live switch
    {
        "per-turn" => Loc("chat.effort.cliPerTurn"),
        "cli-default" => Loc("chat.effort.cliDefault"),
        _ => live,
    };

    /// <summary>"⚡ auto → medium" — the effort this chat's requests really use, shown in the model boxes next to the
    /// selected model (the closed box only; the drop-down lists models).</summary>
    public string LiveEffortSuffix
    {
        get
        {
            var snap = LiveSnapshot();
            return "⚡ " + LiveModelReport.Describe(_settingsService.Settings.ReasoningEffort, snap is null ? null : snap with { Model = null }, null, Loc);
        }
    }

    /// <summary>"Opus 5.5 · auto → high (bug fix)" — the model that really answers (the id the response named) and the
    /// effort its request really carried (as rounded for that model, or as the Claude Code CLI says it ran), with why
    /// "auto" picked it. Before any request: what is set.</summary>
    public string LiveModelEffortText =>
        LiveModelReport.Describe(_settingsService.Settings.ReasoningEffort, LiveSnapshot(), ActiveModelLabel, Loc);

    /// <summary>The same in full words, for the chip's tooltip: which model, which effort, and whether the provider confirmed it.</summary>
    public string LiveModelEffortTip
    {
        get
        {
            var snap = LiveSnapshot();
            string basis = snap == null ? Loc("live.tip.notYet")
                : snap.Effort == LiveModelReport.NoEffort ? Loc("live.tip.noEffort")
                : snap.EffortConfirmed ? Loc("live.tip.confirmed") : Loc("live.tip.sent");
            return Loc("chat.liveEffortTip") + "\n" + basis;
        }
    }
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
        var type = _providerManager.PickedProviderType;
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
            // "default" is a real model too: the one the CLI said it runs (init / each reply), once it has run.
            AiProviderType.ClaudeDev => configured is null or "default" or "auto"
                ? ClaudeDevProvider.ReportedModel ?? Loc("chat.usage.cliDefault") : configured,
            AiProviderType.GeminiDev => configured is null or "default" or "auto" ? Loc("chat.usage.geminiCliDefault") : configured,
            AiProviderType.AntigravityDev => configured is null or "default" or "auto" ? Loc("chat.usage.agyDefault") : configured,
            AiProviderType.OpenAIDev => configured is null or "default" or "auto" ? Loc("chat.usage.codexDefault") : configured,
            _ => configured ?? Loc("chat.usage.noModel"),
        };
        ThinkToggleVisible = type switch
        {
            AiProviderType.Anthropic => true,
            AiProviderType.OpenAI => OpenAiProvider.MaxEffort(configured ?? OpenAiProvider.DefaultModel) != null,
            AiProviderType.Gemini => GeminiProvider.ThinkingOf(configured ?? GeminiProvider.DefaultModel) != GeminiProvider.ThinkingControl.None,
            _ => false,
        };
        EffortVisible = type switch
        {
            AiProviderType.Anthropic => AnthropicProvider.Traits(configured ?? AnthropicProvider.DefaultModel).EffortSupport > 0,
            AiProviderType.OpenAI => OpenAiProvider.MaxEffort(configured ?? OpenAiProvider.DefaultModel) != null,
            AiProviderType.Gemini => GeminiProvider.ThinkingOf(configured ?? GeminiProvider.DefaultModel) != GeminiProvider.ThinkingControl.None,
            AiProviderType.ClaudeDev => true,
            AiProviderType.OpenAIDev => true,   // -c model_reasoning_effort (Codex's models take low … max)
            _ => false,
        };
        OnPropertyChanged(nameof(BillingUrl));
        OnPropertyChanged(nameof(HasBillingPage));
        RefreshChatModelChoices();
        RefreshSpend();
        RefreshStatusBarFacts();

        SessionCostText = _costTracker.TotalRequests == 0 ? "$0.00" : $"${_costTracker.TotalCostUsd:0.00}";

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
                : type == AiProviderType.GeminiDev ? Loc("chat.usage.geminiCliTip")
                : type == AiProviderType.AntigravityDev ? Loc("chat.usage.agyTip")
                : type == AiProviderType.OpenAIDev ? Loc("chat.usage.codexTip")
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

    /// <summary>The toast's "compact by itself from now on" tick (Settings ▸ AutoCompactWhenSuggested). Ticking it on an
    /// open toast answers that toast too: it closes, and the compact runs now — or when the turn in progress ends.</summary>
    public bool AutoCompactWhenSuggested
    {
        get => _settingsService.Settings.AutoCompactWhenSuggested;
        set
        {
            if (value == AutoCompactWhenSuggested) return;
            _settingsService.UpdateSettings(s => s.AutoCompactWhenSuggested = value);
            OnPropertyChanged();
            if (value && _showCompactionToast)
            {
                ShowCompactionToast = false;
                if (!IsGenerating) CompactConversation();
                else _lastFiredTrigger = "";   // mid-reply: the check after the turn ends fires again and compacts then
            }
        }
    }

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
    public ICommand PauseGenerationCommand { get; }
    public ICommand ResumeGenerationCommand { get; }
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
        DebugLogService? debugLog = null,
        PermissionService? permissionService = null,
        Services.Batch.BatchService? batchService = null,
        Services.Pet.PetService? pet = null,
        ActivationService? activation = null,
        SessionCleanupService? cleanup = null)
    {
        _cleanup = cleanup;
        _debugLog = debugLog;
        _pet = pet;
        _permissionService = permissionService;
        if (_permissionService != null)
        {
            _permissionService.AcceptEditsChanged += () => App.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(AcceptEdits)));
            // The switch shows the truth for the folder that is open now, whoever changed it.
            fileSystemService.OnWorkingDirectoryChanged += () => App.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(AcceptEdits)));
        }
        // The agent opened a project itself (open_project): show it and remember it on that chat.
        Services.Tools.ProjectToolModule.ProjectOpened += (scope, path) =>
            App.Current?.Dispatcher.BeginInvoke(() => OnAgentOpenedProject(scope, path));
        void RaiseLiveModel() => App.Current?.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(LiveModelEffortText));
            OnPropertyChanged(nameof(LiveModelEffortTip));
            OnPropertyChanged(nameof(LiveEffortSuffix));
            OnPropertyChanged(nameof(LiveEffortLevel));
        });
        AgentRunScope.LiveEffortChanged += _ => RaiseLiveModel();
        LiveModelReport.Changed += RaiseLiveModel;
        Services.Tools.ProjectToolModule.RelatedFolderAdded += (scope, path) =>
            App.Current?.Dispatcher.BeginInvoke(() => OnAgentAddedFolder(scope, path));
        // The agent opens a new chat itself (new_chat), and puts finished ones away (close_chat). The asking chat is read
        // here, on the tool's own flow (its run scope), before the hop to the UI thread.
        Services.Tools.ProjectToolModule.NewChatHandler = (path, message, title) =>
        {
            string? parentId = FlowRun?.Session.Id;
            return App.Current.Dispatcher.InvokeAsync(() => OpenChatForAgent(path, message, title, parentId)).Task;
        };
        Services.Tools.ProjectToolModule.CloseChatHandler = target =>
        {
            string? parentId = FlowRun?.Session.Id;
            return App.Current.Dispatcher.InvokeAsync(() => CloseChatsForAgent(parentId, target)).Task;
        };
        // Which chat split off which, and which finished split chats were put away (chat-links.json beside codex.db).
        ChatLinkStore.Current ??= new ChatLinkStore(settingsService.DataRoot);
        ChatLinkStore.Current.Changed += id => App.Current?.Dispatcher.BeginInvoke(() => OnSplitLinkChanged(id));
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
            _autonomousService.OnPhase += msg =>
            {
                var phaseRun = Cur;   // the run raising it (captured before hopping to the UI thread)
                App.Current?.Dispatcher.Invoke(() =>
            {
                // A phase banner is the agent's own status, not something the model said: as Assistant it was
                // saved and sent back to the model as a turn, and the Steps switch could not hide it.
                (phaseRun?.Messages ?? Messages).Add(new ChatMessage { Role = MessageRole.AgentStatus, Content = msg });
                ScrollToBottom?.Invoke();
            });
            };

        // Backing field, not the property: the property asks for confirmation when switched ON.
        _autoExecute = settingsService.Settings.AutoExecuteCode;
        _extendedThinkingEnabled = settingsService.Settings.ExtendedThinkingEnabled;
        _reasoningEffort = EffortLevels.Normalize(settingsService.Settings.ReasoningEffort);
        _showThinking = settingsService.Settings.ShowThinkingSteps;
        _showAgentInternals = settingsService.Settings.ShowAgentInternals;
        _statusText = Loc("chat.status.ready");
        // API keys a failed settings load had dropped came back from its backup at this start: say which.
        if (settingsService.RecoveredKeys.Count > 0)
            _statusText = LocF("chat.keysRecovered", string.Join(", ", settingsService.RecoveredKeys));

        RefreshModelsCommand = new RelayCommand(RefreshLocalModelsList);
        // Started, not awaited: a run lasts as long as it lasts, and an awaited command stays "executing" — which
        // blocked Send in every other chat until it finished (several chats can run at once now).
        SendMessageCommand = new RelayCommand(() => Detached(SendMessage));
        StopGenerationCommand = new RelayCommand(StopGeneration);
        PauseGenerationCommand = new RelayCommand(PauseGeneration);
        ResumeGenerationCommand = new RelayCommand(ResumeGeneration);
        ClearChatCommand = new AsyncRelayCommand(ClearChatAsync);
        NewSessionCommand = new AsyncRelayCommand(NewSessionFromUserAsync);
        NewChatAndShowCommand = new AsyncRelayCommand(NewChatAndShowAsync);
        CompactCommand = new RelayCommand(CompactConversation);
        RetryMessageCommand = new RelayCommand<ChatMessage>(m => Detached(() => RetryFromAsync(m)));
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
        ExecuteCodeBlockCommand = new RelayCommand<CodeBlock>(b => Detached(() => ExecuteCodeBlock(b)));
        OpenFolderCommand = new AsyncRelayCommand(OpenFolderAsync);
        CloseFolderCommand = new AsyncRelayCommand(CloseFolderAsync);
        CloneRepoCommand = new AsyncRelayCommand(CloneRepo);
        LoadSessionCommand = new AsyncRelayCommand<string>(LoadSessionAsync);
        DeleteSessionCommand = new AsyncRelayCommand<string>(DeleteSessionAsync);
        ClearSearchCommand = new RelayCommand(ClearHistorySearch);
        LoadSearchResultCommand = new AsyncRelayCommand<string>(LoadSessionAsync);
        ReviewCodeCommand = new RelayCommand(() => Detached(RunCodeReview));

        // Listen for tool execution events
        _agentService.OnToolExecuted += OnToolExecuted;
        if (_pet != null) _agentService.OnToolExecuted += r => _pet.NoteTool(r.ToolName ?? r.Type.ToString(), r.Success);
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
        // AutoOpenEditorOnAgentEdit is the "ตามดูโค้ด" tick of every chat nobody ticked: show the new default at once.
        _settingsService.SettingsChanged += () => App.Current?.Dispatcher.BeginInvoke(() => { RefreshUsageStrip(); UpdateContextInfo(); RefreshVoiceButtons(); OnPropertyChanged(nameof(FollowEditsHere)); });
        RefreshUsageStrip();
        InitBatch(batchService);

        _providerManager.OnError += msg =>
        {
            var errRun = Cur;   // the run whose provider call failed (captured before hopping to the UI thread)
            App.Current?.Dispatcher.Invoke(() =>
            {
                string safe = SanitizeErrorMessage(string.IsNullOrWhiteSpace(msg) ? Loc("chat.err.generic") : msg);
                var list = errRun?.Messages ?? Messages;
                if (errRun?.ActiveStatus is { } st) { list.Remove(st); errRun.ActiveStatus = null; }
                // De-dupe: don't stack the identical error twice in a row.
                if (list.LastOrDefault() is { HasError: true } last && last.Content?.Contains(safe) == true)
                {
                    StatusText = safe;
                    return;
                }
                list.Add(new ChatMessage { Role = MessageRole.Assistant, Content = $"**⚠ {safe}**", HasError = true });
                StatusText = safe;
                ScrollToBottom?.Invoke();
            });
        };

        // Agent map: every sub-agent this command started, live, as one tree card per command.
        _agentService.OnSubAgentEvent += e =>
        {
            var mapRun = Cur;   // the spawning run, read in its flow
            App.Current?.Dispatcher.BeginInvoke(() => OnSubAgentEvent(mapRun, e));
        };

        // Live plan checklist (Claude Code-style TodoWrite pills). TodoChanged had ZERO subscribers —
        // the agent maintained a real plan in memory that no one could see. Render it as a single
        // checklist bubble that updates in place as items are added/completed.
        _agentToolService.TodoChanged += () =>
        {
            // The run's own plan, read in its flow (each run has one) before hopping to the UI thread.
            var todoRun = Cur;
            var items = _agentToolService.TodoItems.ToList();
            App.Current?.Dispatcher.BeginInvoke(() =>
            {
                RememberPlan(todoRun, items);   // the plan window (and a finished run's plan) read it from here
                var Messages = todoRun?.Messages ?? this.Messages;
                ChatMessage? _todoListMsg = todoRun?.TodoMsg;
                if (items.Count == 0)
                {
                    if (_todoListMsg != null) { Messages.Remove(_todoListMsg); if (todoRun != null) todoRun.TodoMsg = null; }
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
                var planItems = items.Select((t, i) => new PlanItemView(i + 1, t.Content, t.Status?.ToLowerInvariant() switch
                {
                    "completed" or "done" => "done",
                    "in_progress" or "active" => "active",
                    _ => "pending",
                })).ToList();
                int planDone = planItems.Count(p => p.IsDone);
                string planHeader = planDone == planItems.Count
                    ? LocF("chat.plan.done", planDone, planItems.Count)
                    : LocF("chat.plan.working", planDone, planItems.Count);
                var planElapsedSpan = todoRun?.Stopwatch.Elapsed ?? TimeSpan.Zero;
                string planElapsed = planElapsedSpan.TotalSeconds < 1 ? ""
                    : planElapsedSpan.TotalMinutes >= 1 ? $"{(int)planElapsedSpan.TotalMinutes}m {planElapsedSpan.Seconds}s" : $"{planElapsedSpan.Seconds}s";
                if (_todoListMsg == null || !Messages.Contains(_todoListMsg))
                {
                    _todoListMsg = new ChatMessage { Role = MessageRole.System, Content = sb.ToString().TrimEnd(),
                                                     PlanItems = planItems, PlanHeader = planHeader, PlanElapsed = planElapsed };
                    if (todoRun != null) todoRun.TodoMsg = _todoListMsg;
                    Messages.Add(_todoListMsg);
                }
                else
                {
                    _todoListMsg.Content = sb.ToString().TrimEnd();
                    _todoListMsg.PlanItems = planItems;
                    _todoListMsg.PlanHeader = planHeader;
                    _todoListMsg.PlanElapsed = planElapsed;
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

        // ask_user: the question appears in the asking run's chat with its options and an answer box. Nothing
        // subscribed to OnAskUserQuestion before, so the tool failed every time ("no UI handler connected") while the
        // model was told it could ask.
        _agentToolService.OnAskUserQuestion += AskUserAsync;

        // Wire the human-in-the-loop permission prompt. Without this, PermAction.Ask resolved against a
        // null handler → silently DENIED → users were funneled into blanket-allowing "*" (which then
        // never prompts for rm -rf / force-push / overwrite). Now an Ask shows a real confirmation.
        Func<PermissionRequestInfo, Task<bool>> askPermission = info =>
        {
            var dispatcher = App.Current?.Dispatcher;
            if (dispatcher == null) return Task.FromResult(false); // headless / no UI → fail safe (deny)

            // Inline, non-blocking prompt: render the request (with a preview diff for file edits) right in
            // the conversation, and resolve when the user clicks Allow/Deny. Replaces the old modal MessageBox
            // so the user can SEE exactly what's about to change before approving.
            var askRun = Cur;   // the run asking — its chat gets the prompt, its Stop cancels it
            // This chat allows everything (the approvals box): go ahead without a prompt — unless the request is one
            // that always asks.
            if (AutoApproves(info, askRun?.Session ?? CurrentSession)) return Task.FromResult(true);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var token = askRun?.Cts?.Token ?? CancellationToken.None;
            CancellationTokenRegistration reg = default;

            // In people's words first (what, where, the result, how risky); the real command folded underneath.
            Services.Safety.PlainPermission? plain = null;
            try
            {
                string? root = AgentRunScope.Current?.WorkingDirectory
                               ?? (_fileSystemService.HasWorkingDirectory ? _fileSystemService.WorkingDirectory : null);
                plain = Services.Safety.PermissionExplainer.Explain(info, root, Loc);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[permission] explain failed: " + ex.Message); }
            var prompt = new ChatMessage
            {
                Role = MessageRole.PermissionRequest,
                // Content carries the whole request in words (headline + why it asks) for anything that reads it.
                Content = plain == null ? LocF("chat.perm.prompt", info.ToolName, info.Detail)
                    : plain.Note.Length > 0 ? plain.Headline + "\n" + plain.Note : plain.Headline,
                PermissionPlain = plain,
                DiffLines = info.Diff?.Lines,
                DiffStat = info.Diff?.Stat,
                PermissionAlwaysAsk = info.AlwaysAsk || info.Danger != null,
                PermissionCanAllowForChat = info.ChatScopeKey != null && !info.AlwaysAsk && info.Danger == null,
                PermissionCanAllowAlways = info.AlwaysKey != null && !info.AlwaysAsk && info.Danger == null,
                PermissionDanger = info.Danger,
                Character = CharacterForBox(askRun),
            };
            prompt.PermissionCallback = allow =>
            {
                if (allow && prompt.PermissionAllowedForChat) info.AllowedForChat = true;   // read by the asking tool
                if (allow && prompt.PermissionAllowedForDay) info.AllowedForDay = true;
                if (allow && prompt.PermissionAllowedAlways) info.AllowedAlways = true;
                tcs.TrySetResult(allow);
                reg.Dispose();
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
                (askRun?.Messages ?? Messages).Add(prompt);
                RaiseRunStateChanged();   // "waiting for you" on the other-chats bar
                ScrollToBottom?.Invoke();
            });
            return tcs.Task;
        };
        _agentToolService.OnPermissionRequired += askPermission;

        // A delete aimed at a drive, the root or a home folder runs only after the user's yes on a red prompt — from
        // CluadeX's own tools and, through the pipe, from Claude Dev's shell (the --cx-guard hook).
        Services.Safety.DangerApproval.Handler = req => askPermission(new PermissionRequestInfo
        {
            ToolName = req.Tool,
            Detail = req.Action,
            AlwaysAsk = true,
            Danger = req.Reason,
        });
        Services.Safety.DangerApprovalPipe.StartServer();
        // Claude Dev's open_project / new_chat / close_chat (CluadeX.exe --cx-tools) reach this chat page through here.
        Services.Tools.ChatToolBridge.StartServer();

        // Update context info when messages change
        Messages.CollectionChanged += OnMessagesChanged;

        // Keep the sidebar-filtered VisibleSessions list in sync with Sessions automatically.
        // Any Add/Remove/Replace/Reset on Sessions triggers a rebuild filtered by current project.
        Sessions.CollectionChanged += (_, _) => RebuildVisibleSessions();
        // The sidebar groups the list by day (today / yesterday / this week / earlier), newest first.
        System.Windows.Data.CollectionViewSource.GetDefaultView(VisibleSessions)?.GroupDescriptions
            .Add(new System.Windows.Data.PropertyGroupDescription(nameof(ChatSession.DateGroupLabel)));

        // Periodic context / cost refresh every 5 seconds. (It also ran nvidia-smi here — on the UI thread,
        // blocking up to 3 s every 5 s — to fill GPU properties nothing bound; MainViewModel already polls the GPU.)
        _memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _memoryTimer.Tick += (_, _) =>
        {
            UpdateContextInfo();
            CostText = _costTracker.FormatCost();
            RefreshUsageStrip();
            ProbeOllamaForChatBox();   // throttled to 30 s: lists Ollama's models in the model box while it answers
        };
        _memoryTimer.Start();
        InitControls(activation);
        InitSidePanel();
        InitCharacter();
        HookPendingImages();

        // Auto-save every 10 seconds if dirty
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _autoSaveTimer.Tick += (_, _) => { AutoSave(); SaveBackgroundRuns(); };
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

                // Sessions — one sidebar rebuild for the lot, not one per chat (it was quadratic at start-up)
                _bulkSessions = true;
                try
                {
                    foreach (var session in savedSessions)
                        Sessions.Add(session);
                }
                finally { _bulkSessions = false; }
                RebuildVisibleSessions();
                // Split links of chats that are gone (deleted, or dropped above as empty) are forgotten.
                ChatLinkStore.Current?.Prune(savedSessions.Select(s => s.Id));
                // Projects the saved chats worked in join the project list (the agent opens them by name).
                foreach (var session in savedSessions)
                    ProjectRegistryService.Current?.Seed(session.ProjectPath, session.UpdatedAt);

                if (lastSession != null && lastSession.Messages.Count > 0)
                {
                    CurrentSession = lastSession;
                    for (int i = 0; i < Sessions.Count; i++)
                    {
                        if (Sessions[i].Id == lastSession.Id) { Sessions[i] = lastSession; break; }
                    }
                    foreach (var msg in lastSession.Messages)
                        Messages.Add(msg);
                    // Its project at once: the files panel used to keep the previous folder until the first message
                    // was sent (owner 2026-10-16: "แต่ละแชทต้องแสดงไฟล์ให้ถูกต้อง … แม้เปิดมาใหม่").
                    FollowChatProject(lastSession);

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
            // Only a chat that really got new rows moves up the history: SaveNow runs on every chat switch, and bumping
            // the time then made every chat the owner merely looked at jump to "Today" (2026-10-17: a wall of old
            // Reverse X chats on top after clicking through them).
            bool changed = CurrentSession.Messages.Count != Messages.Count
                           || !ReferenceEquals(CurrentSession.Messages.LastOrDefault(), Messages.LastOrDefault());
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

            if (changed) CurrentSession.UpdatedAt = DateTime.Now;
            CluadeX.Helpers.UiStallWatchdog.Note("saving the chat");
            _persistenceService.SaveSession(CurrentSession);
            CluadeX.Helpers.UiStallWatchdog.Note("");
            _isDirty = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AutoSave failed: {ex.Message}");
        }
    }

    /// <summary>Force save the current session immediately (and the chat of the run calling it, if not on screen).</summary>
    public void SaveNow()
    {
        _isDirty = true;
        AutoSave();
        if (FlowRun is { } run && !ReferenceEquals(run.Session, CurrentSession)) SaveRunChat(run);
    }

    private async Task LoadSessionAsync(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            System.Diagnostics.Debug.WriteLine("LoadSession: sessionId is null/empty");
            return;
        }

        if (CurrentSession?.Id == sessionId) return;
        NoteUserActivity();   // opening a chat: the screen is in use
        CluadeX.Helpers.UiStallWatchdog.Note("switching chat");
        var switchWatch = System.Diagnostics.Stopwatch.StartNew();
        long tSave = 0, tLoad = 0, tList = 0, tFollow = 0, tCtx = 0;

        // Navigate to Chat page if we're on a different page
        NavigateToChatRequested?.Invoke();

        // A reply running in the chat being left keeps running there (it writes into its own chat's list).
        bool leavingRun = RunOf(CurrentSession) != null;

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

        // A chat with a run in progress: its live list IS the chat — never reload it from the database.
        if (_runs.FirstOrDefault(r => r.Session.Id == sessionId) is { } running)
        {
            CurrentSession = running.Session;
            Messages = running.Messages;
            _isDirty = false;
            for (int i = 0; i < Sessions.Count; i++)
                if (Sessions[i].Id == sessionId) { Sessions[i] = running.Session; break; }
            StatusText = Loc("chat.status.working");
            FollowChatProject(running.Session);
            UpdateContextInfo();
            ScrollToBottom?.Invoke();
            return;
        }

        tSave = switchWatch.ElapsedMilliseconds;
        // Load from database (full session with messages)
        var session = _persistenceService.LoadSession(sessionId);
        tLoad = switchWatch.ElapsedMilliseconds - tSave;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            _debugLog?.Info("Perf", $"chat switch {switchWatch.ElapsedMilliseconds} ms (save {tSave} · load {tLoad} · {session?.Messages.Count ?? 0} rows · list@{tList} follow@{tFollow} ctx@{tCtx} · realized {Views.ChatView.RealizedRows()})");
            CluadeX.Helpers.UiStallWatchdog.Note("");
        });

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

        // The chat being left: its open prompts are answered "dismissed" — unless a run there is waiting on them.
        if (!leavingRun) ResolvePendingPermissionPrompts();
        // A list of its own (the one being left may be a running turn's live list — it must not be emptied).
        Messages = new ObservableCollection<ChatMessage>(session.Messages);

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

        tList = switchWatch.ElapsedMilliseconds;
        StatusText = session.Messages.Count > 0
            ? LocF("chat.status.loaded", session.Title, session.Messages.Count)
            : LocF("chat.status.loadedEmpty", session.Title);
        FollowChatProject(session);
        tFollow = switchWatch.ElapsedMilliseconds;

        UpdateContextInfo();
        tCtx = switchWatch.ElapsedMilliseconds;
        ScrollToBottom?.Invoke();
    }

    /// <summary>
    /// Opening a chat opens its project. A chat belongs to the folder it was started in (ChatSession.ProjectPath).
    /// After a restart — or coming from another project's chat — the app had no folder or the wrong one open, so a
    /// "carry on" in an old chat ran with no file or shell tools at all: the agent answered that it could not work
    /// and handed the job off (owner 2026-10-11: "ฉันสั่งงานต่อในแชทเก่า ทำไม มันไม่รู้เรื่อง"). A folder that no
    /// longer exists is left alone.
    /// </summary>
    private void FollowChatProject(ChatSession? session)
    {
        string? path = session?.ProjectPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            // A chat with no project (or whose folder is gone) shows no files — never the previous chat's
            // (owner 2026-10-16). The panel says so and offers Open folder.
            if (session != null && HasProject)
            {
                ApplyWorkingDirectory("", announce: false, showEditor: false);
                StatusText = string.IsNullOrWhiteSpace(path) ? Loc("chat.status.noProjectChat") : LocF("chat.status.projectMissing", path);
            }
            return;
        }
        if (HasProject && PathEquals(WorkingDirectory, path)) return;
        ApplyWorkingDirectory(path, announce: false, showEditor: false);
        StatusText = LocF("chat.status.projectFollowed", Path.GetFileName(path.TrimEnd('\\', '/')));
    }

    private readonly SessionCleanupService? _cleanup;

    /// <summary>Clean up what chat <paramref name="sessionId"/>'s agent left open (dev servers, the preview, and with
    /// <paramref name="deleteImages"/> its screenshots) and say what was closed in the status bar.</summary>
    private async Task CleanUpSessionAsync(string? sessionId, bool deleteImages)
    {
        if (_cleanup == null || string.IsNullOrEmpty(sessionId)) return;
        var r = await _cleanup.CleanAsync(sessionId, deleteImages);
        if (!r.Any) return;
        _debugLog?.Info("Chat", $"session cleanup {sessionId} · processes={r.ProcessesStopped} preview={r.PreviewClosed} images={r.ImagesDeleted}");
        StatusText = LocF("chat.cleanup.done", r.ProcessesStopped, r.PreviewClosed ? Loc("chat.cleanup.previewClosed") : "", r.ImagesDeleted);
    }

    private async Task DeleteSessionAsync(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        // Confirmation dialog for destructive operation
        if (!Confirm(Loc("chat.delete.confirm"), Loc("chat.delete.title"))) return;

        // Deleting a chat a reply is being written into (on screen or not): stop that reply first.
        if (!await StopRunOfAsync(sessionId)) return;

        // Delete from DB
        _persistenceService.DeleteSession(sessionId);
        await CleanUpSessionAsync(sessionId, deleteImages: false);   // DeleteSession already removed its screenshots

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
            Messages = new ObservableCollection<ChatMessage>();
            NewSession();
        }

        StatusText = Loc("chat.status.deleted");
    }

    // ─── Turn ownership (L2: a reply must land in the chat that asked for it) ───


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
        // A running turn's chat has a live list (on screen or not): straight in.
        if ((target == null ? Cur : RunOf(target)) is { } run)
        {
            run.Messages.Add(msg);
            return;
        }
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
        _isDirty = true;
        // Debounced: both refreshes walk the whole chat (history rebuild + token estimate over every message and tool
        // output). Run per added row, a long run did that hundreds of times — O(n²) on the UI thread, the chat slowed
        // the longer it got. Now once, 400 ms after the rows stop changing, at Background priority (below input).
        _messagesChangedTimer ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        if (!_messagesChangedHooked)
        {
            _messagesChangedHooked = true;
            _messagesChangedTimer.Tick += (_, _) =>
            {
                _messagesChangedTimer!.Stop();
                UpdateContextInfo();
                RefreshSidePanelFacts();
            };
        }
        _messagesChangedTimer.Stop();
        _messagesChangedTimer.Start();
    }
    /// <summary>A grouped tool row keeps the last 16 KB of its outputs: the group grew without bound over a long run,
    /// and every autosave and context count walked all of it.</summary>
    private static string CapGroupOutput(string text) =>
        text.Length <= 16_384 ? text : "…\n" + text[^16_384..];
    private System.Windows.Threading.DispatcherTimer? _messagesChangedTimer;
    private bool _messagesChangedHooked;

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
        bool auto = _settingsService.Settings.AutoCompactWhenSuggested;
        // Honor user setting (default true) — the auto tick works even with the toast itself switched off
        if (!_settingsService.Settings.EnableStrategicCompactionToast && !auto) return;
        // Don't fire while a toast is already showing
        if (_showCompactionToast) return;
        // Auto: never cut a turn in progress — this runs again when it ends (UpdateContextInfo).
        if (auto && IsGenerating) return;

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
        if (_settingsService.Settings.AutoCompactWhenSuggested)
        {
            // Where the toast would ask: compact instead. A conversation with nothing to compact keeps the trigger
            // marked (EvaluateCompactionTrigger sets it), so the 5-second refresh does not retry it every tick.
            CompactConversation();
            return;
        }
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
        // A reply running in the chat on screen keeps its own folder (its run pinned it); the new project gets
        // a new chat so the two never mix.
        if (IsGenerating) NewSession();

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
        ApplyWorkingDirectory(path, announce: true, showEditor: true);
        // The chat on screen now belongs to this folder: coming back to it later shows these files, not whatever was
        // open last (owner 2026-10-16: "อยู่แชทไหน ไฟล์โปรเจคต้องตรงกันทันที"). It used to be set only when a chat was made.
        // A chat that already has its own (existing) project keeps it — only an unbound chat is tied here.
        if (CurrentSession is { } chat && !string.IsNullOrWhiteSpace(path)
            && (string.IsNullOrWhiteSpace(chat.ProjectPath) || !Directory.Exists(chat.ProjectPath)))
        {
            chat.ProjectPath = path;
            if (Messages.Count > 0) { _isDirty = true; AutoSave(); }
            RebuildVisibleSessions();
        }
    }

    /// <summary>
    /// Switch the open project. <paramref name="announce"/> posts the "📁 Opened project" line in the open chat;
    /// <paramref name="showEditor"/> brings the workbench forward. A pipe run switches with announce only (the run
    /// shows in the chat, not the editor) and switches BACK with neither. An empty path closes the project.
    /// </summary>
    private void ApplyWorkingDirectory(string path, bool announce, bool showEditor)
    {
        path ??= "";
        _fileSystemService.WorkingDirectory = path;
        WorkingDirectory = path;
        OnPropertyChanged(nameof(ProjectName));
        RaiseOpenFolders();

        // Invalidate system prompt cache (project context changed)
        _agentService.InvalidateSystemPromptCache();

        // Project-scoped skills live under the PROJECT folder, but the skill list is cached on
        // first use — without this, opening or switching a project never picked them up (and a
        // previous project's skills stayed advertised).
        _skillService.ReloadSkills();

        // "Accept edits" was given for the previous folder: it ends here.
        ResetAcceptEditsForProject(path.Length > 0 ? path : null);

        if (path.Length == 0)
        {
            AgenticMode = false;
            GitBranch = "";
            StatusText = Loc("chat.status.ready");
            return;
        }

        // Auto-enable agentic mode when a project is open
        AgenticMode = true;

        StatusText = LocF("chat.status.project", ProjectName);

        // Detect git info
        _ = DetectGitInfoAsync();

        if (announce)
        {
            Messages.Add(new ChatMessage
            {
                Role = MessageRole.System,
                Content = "\U0001F4C1 " + LocF("chat.folder.opened", path),
            });
            ScrollToBottom?.Invoke();
        }

        // Land the user in the workbench: opening a project in an IDE should show the project.
        if (showEditor) NavigateToEditorRequested?.Invoke();
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
        // Cloning ends by switching the project: a reply running here keeps its folder; the clone gets a new chat.
        if (IsGenerating) NewSession();

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
        await Task.CompletedTask;
        if (IsGenerating) NewSession();   // the running reply keeps its folder

        // Mirror SetWorkingDirectory: the closed project's branch badge, its skills, the system prompt that
        // described it and its "Accept edits" all go with it.
        ApplyWorkingDirectory(string.Empty, announce: false, showEditor: false);
    }

    /// <summary>New chat from the chat's own buttons: stops a running reply first. Stays on the current page —
    /// the Code page hosts this same chat docked beside the editor.</summary>
    private Task NewSessionFromUserAsync()
    {
        NewSession(keepProject: false);   // a reply running in the chat on screen carries on in the background
        return Task.CompletedTask;
    }

    /// <summary>New chat from OUTSIDE the chat (title bar, Ctrl+N, palette): as above, then brings the Chat page
    /// forward — from another page the title-bar button created the chat out of sight, so it looked like it
    /// did nothing.</summary>
    private Task NewChatAndShowAsync()
    {
        NewSession(keepProject: false);
        NavigateToChatRequested?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Promoted from private to public so the McpHostService can spawn a
    /// fresh visible session each time ObsidianX delegates a task. UI-thread
    /// only — callers from background threads must Dispatcher.Invoke.
    /// Does not stop a running turn: UI paths go through NewSessionFromUserAsync, MCP waits for idle.
    /// </summary>
    public void NewSession() => NewSession(keepProject: true);

    /// <summary>
    /// <paramref name="keepProject"/> false = the user's own New Chat (button, Ctrl+N, palette): it starts with NO folder
    /// open (owner 2026-10-20: "โฟลเด้อเริ่มใน new chat ไม่ควรเปิดโฟลเด้อรอ ถ้าไม่ใช่เป็นการโยนงานมาจากแชทอื่น") — the
    /// project opens when the user names one (or opens a folder). A chat handed work by another chat (new_chat, a pipe
    /// task) keeps the folder it was given.
    /// </summary>
    public void NewSession(bool keepProject)
    {
        string projectPath = keepProject && _fileSystemService.HasWorkingDirectory ? _fileSystemService.WorkingDirectory : "";
        // The app's folder goes with it — after the chat is made, so the chat being left keeps its own project.
        void CloseFolderIfFresh()
        {
            if (!keepProject && _fileSystemService.HasWorkingDirectory) ApplyWorkingDirectory(string.Empty, announce: false, showEditor: false);
        }

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
            CloseFolderIfFresh();
            StatusText = Loc("chat.status.ready");
            return;
        }

        // Save current session before creating new
        bool leavingRun = RunOf(CurrentSession) != null;
        if (CurrentSession != null && Messages.Count > 0)
        {
            _isDirty = true;
            AutoSave();

            // Extract durable memories from the finishing session (fire-and-forget) — not from one still running.
            // Disabled by default — user opts in via settings.
            if (!leavingRun) _sessionMemoryService?.ExtractInBackground(CurrentSession.Messages);
        }

        // Create new session — tag with the current project path so the sidebar can
        // filter to "just this project" instead of mixing every project's sessions together.
        var session = new ChatSession { ProjectPath = projectPath };
        if (!leavingRun) ResolvePendingPermissionPrompts();
        CurrentSession = session;
        Messages = new ObservableCollection<ChatMessage>();
        Sessions.Insert(0, session);
        _isDirty = false;
        RebuildVisibleSessions();

        // Not written to the DB yet: AutoSave stores it once it has a message, so a chat that is opened
        // and never used leaves nothing behind (LoadSession falls back to the in-memory entry).
        CloseFolderIfFresh();
        StatusText = Loc("chat.status.ready");
    }

    /// <summary>
    /// new_chat (UI thread): a new chat in the sidebar — in <paramref name="path"/> (null = the project open now) — and,
    /// with <paramref name="message"/>, its first turn started as the user's message. The asking chat keeps running
    /// beside it (each run has its own folder). Not awaited: the new chat's turn is its own. Returns the tool's report.
    /// </summary>
    private string OpenChatForAgent(string? path, string? message, string? title, string? parentId = null)
    {
        parentId ??= CurrentSession?.Id;
        // A ceiling on agent splits: each one starts a run that may split again (SplitLimits). Thrown = the tool fails
        // with the reason, so the agent does the work itself instead of breeding chats.
        if (parentId != null && ChatLinkStore.Current is { } store)
        {
            bool Working(string id) => Sessions.FirstOrDefault(s => s.Id == id) is { } s && RunOf(s) != null;
            string? refusal = SplitLimits.Refusal(
                store.DepthOf(parentId),
                store.ChildrenOf(parentId).Count(k => k.Link.ArchivedAt == null && Working(k.Id)),
                Sessions.Count(s => store.Get(s.Id) != null && RunOf(s) != null),
                store.CreatedSince(DateTime.Now - SplitLimits.Window));
            if (refusal != null)
            {
                _debugLog?.Info("Chat", $"new_chat refused · parent={parentId} · {refusal}");
                throw new InvalidOperationException(refusal);
            }
        }
        string? from = Sessions.FirstOrDefault(s => s.Id == parentId)?.Title ?? CurrentSession?.Title;
        if (path != null && !string.Equals(path, _fileSystemService.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
            ApplyWorkingDirectory(path, announce: false, showEditor: false);
        NewSession();
        var chat = CurrentSession ?? throw new InvalidOperationException("no chat was created");
        if (title != null) chat.Title = title;
        // The split, for the sidebar's tree and for close_chat: what this chat was opened to do.
        if (parentId != null)
            ChatLinkStore.Current?.Link(chat.Id, parentId, title ?? FirstLineOf(message) ?? "");
        string folder = string.IsNullOrWhiteSpace(chat.ProjectPath) ? "(no project)" : chat.ProjectPath;
        Messages.Add(new ChatMessage
        {
            Role = MessageRole.System,
            Content = "💬 " + LocF("chat.newChat.byAgent", from ?? "?", folder),
        });
        RebuildVisibleSessions();
        if (message == null)
            return $"Opened a new empty chat (id {chat.Id}) in {folder}. It is on screen now; the user types there.";
        _ = StartAgentChatTurnAsync(message, chat.Id);
        return $"Opened a new chat (id {chat.Id}) in {folder} and started it with your message. It runs on its own beside "
             + "this chat; the user follows it in the sidebar. Do not wait for it — carry on here or finish your reply.";
    }

    private async Task StartAgentChatTurnAsync(string message, string chatId)
    {
        try
        {
            if (CurrentSession?.Id != chatId) return;   // the user moved on before it started
            await SendMessageCore(message, null);
        }
        catch (Exception ex) { _debugLog?.Warn("Chat", $"new_chat: the first turn of {chatId} failed: {ex.Message}"); }
    }

    // ════════════════════════════════════════════════════════════════════
    //  MCP-driven external runs (called by McpHostService → ToolDispatcher)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Run a coding task that arrived over the named-pipe MCP host, VISIBLY: a new chat for the task (the Chat page
    /// is brought forward), the spec as its user message, then the same agent loop, tools, hooks and permission
    /// prompts as a typed turn — with the reply, thinking and the harness's notices streaming in live (they were
    /// wired only for typed turns, so a pipe run showed its tool rows and then the whole answer at once).
    ///
    /// <paramref name="workingDirectory"/>: the run switches the app to that project and BACK afterwards (it used to
    /// leave the app on the caller's folder), and the chat is tagged with it so the project-filtered sidebar keeps
    /// showing it. A folder that does not exist is refused rather than silently running in whatever is open.
    /// <paramref name="maxSteps"/>: a step limit for this run. <paramref name="acceptEdits"/>: explicit opt-in to
    /// "Accept edits" for this run (default: the user's AcceptEditsDefault); the user's own setting comes back after.
    ///
    /// The outcome is an error when the run failed, ended without finishing (step limit, error stop), was stopped in
    /// CluadeX, or its caller went away — those all came back as success before. UI work happens on the dispatcher;
    /// the agent loop runs on the thread pool.
    /// </summary>
    public async Task<McpTaskOutcome> RunMcpTaskAsync(
        string taskId,
        string spec,
        IReadOnlyList<string>? lessons,
        IReadOnlyList<string>? contextFiles,
        string? workingDirectory,
        CancellationToken ct,
        int? maxSteps = null,
        bool? acceptEdits = null)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return new McpTaskOutcome("(empty spec — nothing to do)", IsError: true);

        string? runDir = null;
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            try { runDir = Path.GetFullPath(workingDirectory); } catch { runDir = null; }
            if (runDir == null || !Directory.Exists(runDir))
                return new McpTaskOutcome($"workingDirectory does not exist: {workingDirectory}", IsError: true);
        }

        // Compose the prompt. Inject "lessons learned" verbatim so the
        // agent sees them as guidance from the orchestrator.
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
        var dispatcher = Application.Current.Dispatcher;

        // "acceptEdits": true comes from a PROGRAM — a BrainX peer or an orchestrator's intern, i.e. possibly an AI that
        // read untrusted text — so it is the user's decision, not the caller's: never for a drive root, the user folder
        // or a system folder, and confirmed here in CluadeX once per folder per app session. (It used to switch the
        // prompts off for any folder that exists, with nothing in the chat saying who did it.)
        var notes = new List<string>();      // told to the caller with the result
        string? acceptLine = null;           // a system line in the task's chat
        string? acceptTarget = null;         // the folder the caller's acceptEdits was approved for
        if (acceptEdits == true && _permissionService != null)
        {
            string target = runDir ?? await dispatcher.InvokeAsync(CurrentProjectFolder);
            if (target.Length == 0)
                notes.Add("acceptEdits was ignored: no project folder is open, so every edit asks.");
            else if (PermissionService.IsUnsafeAcceptEditsRoot(target, _settingsService.DataRoot))
            {
                acceptLine = "⚠ " + LocF("chat.acceptEdits.unsafeFolder", target);
                notes.Add($"acceptEdits was refused: {target} is a drive root, the user folder, a system folder or CluadeX's own data — every edit asks.");
            }
            else
            {
                string key = target.TrimEnd('\\', '/');
                // Already approved for this folder in this session: no second question. The user has Accept edits on
                // there themselves: on for THIS task only — remembering the folder would let a later pipe task switch
                // it on unasked after the user turned it off. Otherwise the user is asked, in CluadeX.
                int decision = await dispatcher.InvokeAsync(() =>
                    _pipeAcceptEditsApproved.Contains(key) ? 1
                    : _permissionService.AcceptsEditsIn(target) ? 2
                    : Confirm(LocF("chat.acceptEdits.pipeConfirm", taskId, target), Loc("chat.acceptEdits.pipeConfirmTitle"))
                        && _pipeAcceptEditsApproved.Add(key) ? 1 : 0);
                if (decision != 0)
                {
                    acceptTarget = target;
                    acceptLine = "✏️ " + LocF(decision == 2 ? "chat.acceptEdits.pipeUserOn" : "chat.acceptEdits.pipeOn", target);
                }
                else
                {
                    acceptLine = "✋ " + LocF("chat.acceptEdits.pipeDeclined", target);
                    notes.Add("acceptEdits was declined by the user in CluadeX — every edit asks.");
                }
            }
        }
        // A step limit this build can't apply is said, not swallowed.
        if (maxSteps is int askedSteps && FindStepLimitedAgentLoop() == null)
            notes.Add($"maxSteps={Math.Clamp(askedSteps, 1, CodeAgentService.MaxIterationOverride)} was not applied: this CluadeX build has no per-run step limit, "
                + $"so the 'Max agent steps' setting ({_settingsService.Settings.MaxAgentIterations}) applied.");
        string WithNotes(string text) => notes.Count == 0 ? text : $"{text}\n\n({string.Join(" ", notes)})";

        // Claim the chat. If the user's own reply is still running, WAIT for it: this used to switch to a new
        // session underneath it, and that reply's tool rows and answer then landed in the MCP task's chat.
        // Check-and-claim happens in ONE dispatcher call, so the user (or a second pipe call) can't start a turn in between.
        TaskCompletionSource? turn = null;
        ChatSession? taskSession = null;
        AgentRunScope? taskScope = null;
        CancellationTokenSource? linked = null;
        List<ChatMessage> historySnapshot = new();
        string previousDir = "";       // the project to come back to ("" = none was open)
        string? switchedTo = null;     // the folder this run opened (null = it ran in the open project)
        string? previousAccept = null; // the user's own Accept-edits state, restored afterwards
        bool ranOffScreen = false;     // the screen was in use: the task ran in its own chat in the background
        while (true)
        {
            Task? busy = await dispatcher.InvokeAsync<Task?>(() =>
            {
                // No waiting for a reply running in another chat: this task gets its own chat and runs beside it.

                // The screen belongs to the user: busy (typing, just switched chats, watching a run) or taken over a
                // moment ago → the task runs in its own chat in the background and nothing on screen moves.
                if (!MayTakeScreen(DateTime.UtcNow, _lastUserActivityUtc, _lastScreenTakeUtc, RunOf(CurrentSession) != null))
                {
                    string folder = runDir ?? _fileSystemService.AppWorkingDirectory;
                    var bg = new ChatSession { ProjectPath = folder, Title = taskId };
                    _pipeChatIds.Add(bg.Id);
                    var bgList = new ObservableCollection<ChatMessage>();
                    var bgMsg = new ChatMessage { Role = MessageRole.User, Content = prompt };
                    bgList.Add(bgMsg);
                    Sessions.Insert(0, bg);
                    turn = BeginTurnFor(bg, bgList, folder);
                    taskSession = bg;
                    taskScope = AgentRunScope.Current;
                    if (taskScope != null)
                    {
                        taskScope.FromPipe = true;
                        // Accept edits for this run only (the user's own switch is left alone: it is for their project).
                        if (acceptTarget != null && PathEquals(folder, acceptTarget) && !PermissionService.IsUnsafeAcceptEditsRoot(folder))
                            taskScope.AcceptEditsRoot = folder;
                    }
                    if (acceptLine != null) bgList.Add(new ChatMessage { Role = MessageRole.System, Content = acceptLine });
                    ranOffScreen = true;
                    RebuildVisibleSessions();
                    RefreshSessionBadges();
                    linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    if (RunOf(bg) is { } bgRun) bgRun.Cts = linked;
                    historySnapshot = BuildModelHistory(bgList, before: bgMsg, includeTools: true);
                    _debugLog?.Info("Chat", $"pipe task {taskId} runs in the background (screen in use) in {folder}");
                    return null;
                }
                _lastScreenTakeUtc = DateTime.UtcNow;

                previousDir = _fileSystemService.HasWorkingDirectory ? _fileSystemService.WorkingDirectory : "";
                previousAccept = _permissionService?.AcceptEditsProject;

                // 1. A fresh visible session so the user's current chat isn't disturbed (NewSession saves it).
                NewSession();
                // 2. The caller's project, announced in the task's chat — not the editor (the run is watched here).
                if (runDir != null && !PathEquals(runDir, previousDir))
                {
                    ApplyWorkingDirectory(runDir, announce: true, showEditor: false);
                    switchedTo = runDir;
                }
                string project = _fileSystemService.HasWorkingDirectory ? _fileSystemService.WorkingDirectory : "";
                if (CurrentSession != null)
                {
                    CurrentSession.Title = taskId;
                    _pipeChatIds.Add(CurrentSession.Id);
                    // Tagged with the run's real project: an "obsidianx-task::" tag hid it from the sidebar's
                    // project filter, so the run the user was meant to watch was nowhere in the list.
                    CurrentSession.ProjectPath = project;
                    RebuildVisibleSessions();
                }
                // "Accept edits" for this run: the caller's request as approved above (for exactly that folder), else
                // the user's own default. SetAcceptEdits refuses a drive / user / system folder by itself.
                if (_permissionService != null)
                {
                    bool want = project.Length > 0 && (acceptEdits == true
                        ? acceptTarget != null && PathEquals(project, acceptTarget)
                        : acceptEdits ?? _settingsService.Settings.AcceptEditsDefault);
                    if (!_permissionService.SetAcceptEdits(want ? project : null) && acceptLine == null)
                        acceptLine = "⚠ " + LocF("chat.acceptEdits.unsafeFolder", project);
                    OnPropertyChanged(nameof(AcceptEdits));
                }

                // 3. The spec as a user message, visible exactly like a human-typed prompt — then, when the caller
                //    asked for Accept edits, who turned it on (or why it stayed off).
                var userMsg = new ChatMessage { Role = MessageRole.User, Content = prompt };
                Messages.Add(userMsg);
                if (acceptLine != null) Messages.Add(new ChatMessage { Role = MessageRole.System, Content = acceptLine });
                _isDirty = true;
                ScrollToBottom?.Invoke();

                // 4. Own the chat for the run. Stop works on it, and IsGenerating blocks a concurrent turn.
                turn = BeginTurn();
                taskSession = CurrentSession;
                taskScope = AgentRunScope.Current;   // entered by BeginTurn inside this UI call — re-entered below
                if (taskScope != null) taskScope.FromPipe = true;
                linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _cts = linked;
                historySnapshot = BuildModelHistory(Messages, before: userMsg, includeTools: true);
                StatusText = Loc("chat.status.working");

                // 5. Bring the Chat page forward: the run is there to be watched.
                NavigateToChatRequested?.Invoke();
                return null;
            });
            if (busy == null) break;
            await Task.WhenAny(busy, Task.Delay(Timeout.Infinite, ct));
            ct.ThrowIfCancellationRequested();
        }

        // The run's scope for THIS flow too (BeginTurn entered it inside the dispatcher call above, which does not
        // carry back here): the agent loop below and every event it raises then belong to this task's run.
        AgentRunScope.Enter(taskScope);

        // 6. Run the agentic loop — the same CodeAgentService the interactive chat uses — and show it live.
        var live = new LiveRunView(this, taskSession, taskScope);
        live.Attach(_agentService);
        try
        {
            AgentLoopResult? agentResult = null;
            Exception? failure = null;
            try
            {
                agentResult = await Task.Run(() => RunAgentLoopAsync(historySnapshot, prompt, maxSteps, linked!.Token), CancellationToken.None);
            }
            catch (OperationCanceledException) { /* decided below from the tokens */ }
            catch (Exception ex) { failure = ex; }

            if (linked!.IsCancellationRequested || (agentResult == null && failure == null))
            {
                bool callerGone = ct.IsCancellationRequested;
                await dispatcher.InvokeAsync(() =>
                {
                    live.Close();
                    PostTurnMessage(taskSession, new ChatMessage
                    {
                        Role = MessageRole.System,
                        Content = $"*{Loc(callerGone ? "chat.pipe.callerGone" : "chat.interruptedNote")}*",
                        IsInterrupted = true,
                    });
                    _isDirty = true;
                    SaveNow();
                    StatusText = Loc("chat.status.cancelled");
                });
                return new McpTaskOutcome(WithNotes(callerGone
                    ? "Cancelled: the caller disconnected or cancelled the request before the task finished."
                    : "Stopped by the user in CluadeX before the task finished."), IsError: true);
            }

            if (failure != null)
            {
                _debugLog?.Error("Chat", $"MCP task {taskId}: agent loop failed", failure);
                await dispatcher.InvokeAsync(() =>
                {
                    live.Close();
                    PostTurnMessage(taskSession, new ChatMessage { Role = MessageRole.Assistant, Content = $"❌ {Loc("chat.err.agentLoop")}", HasError = true });
                    _isDirty = true;
                    SaveNow();
                    StatusText = Loc("chat.status.error");
                });
                // The caller gets the kind of failure only: the message can hold paths, keys or a stack — those stay
                // in the Debug Log (as App.HandleMcpToolAsync does for a tool that throws).
                return new McpTaskOutcome(WithNotes(
                    $"Agent loop failed ({failure.GetType().Name}) — details are in CluadeX's Debug Log."), IsError: true);
            }

            // 7. The agent's final reply: the streamed bubble is finalized in place (no duplicate), persisted, returned.
            string? finalText = string.IsNullOrWhiteSpace(agentResult!.FinalResponse)
                ? null
                : StripThinkingSpans(_agentToolService.StripToolCalls(agentResult.FinalResponse));
            if (string.IsNullOrWhiteSpace(finalText)) finalText = null;
            bool unfinished = !agentResult.Success;
            int toolsRun = agentResult.Steps?.Sum(s => s.ToolResults?.Count ?? 0) ?? 0;
            await dispatcher.InvokeAsync(() =>
            {
                live.Finish(finalText, toolsRun);
                _isDirty = true;
                SaveNow();
                StatusText = unfinished ? Loc("chat.status.agentDone") : Loc("chat.status.ready");
            });
            string text = finalText ?? (toolsRun > 0
                ? $"(The agent ran {toolsRun} tool call(s) and ended without a written summary.)"
                : "(agent loop returned no text)");
            return unfinished
                ? new McpTaskOutcome(WithNotes($"{text}\n\n(The run did not finish: {agentResult.StopReason}.)"), IsError: true)
                : new McpTaskOutcome(WithNotes(text), IsError: false);
        }
        finally
        {
            live.Detach(_agentService);
            // The clean-up below is the app's (restore the user's project and Accept edits), not the run's: leave the
            // run's scope first, or "the project open now" would read as the run's folder.
            AgentRunScope.Enter(null);
            // Marshal the reset to the UI thread; SetProperty raises PropertyChanged, which bindings expect there.
            void Release()
            {
                live.Close();
                // The window shows the project of the chat on screen — nothing else (owner 2026-10-17: the title said
                // thaipromptadminapp while the window showed another project). The task's chat still on screen keeps its
                // project: switching the project back under it put the user's folder in the title over the task's chat.
                // Another chat on screen: its project, which opening it already set — made sure of here.
                if (!ranOffScreen && (taskSession == null || !ReferenceEquals(CurrentSession, taskSession)))
                    FollowChatProject(CurrentSession);
                // The user's own Accept-edits state comes back (the task's grant ends with it). A background task never
                // touched it: its grant lived in its own run scope.
                if (!ranOffScreen && _permissionService != null)
                {
                    _permissionService.SetAcceptEdits(previousAccept);
                    OnPropertyChanged(nameof(AcceptEdits));
                }
                if (_runs.FirstOrDefault(r => ReferenceEquals(r.Done, turn)) is { } taskRun && ReferenceEquals(taskRun.Cts, linked))
                    taskRun.Cts = null;
                linked?.Dispose();
                EndTurn(turn!);
            }
            try
            {
                if (dispatcher.CheckAccess()) Release();
                else await dispatcher.InvokeAsync(Release);
            }
            catch { /* app closing — nothing to do */ }
        }
    }

    private string CurrentProjectFolder() => _fileSystemService.HasWorkingDirectory ? _fileSystemService.WorkingDirectory : "";

    // ── A per-run step limit ──
    // CodeAgentService gains ExecuteAgenticAsync(history, userMessage, progress, ct, int? maxIterations) in the vision
    // work (team T1, merged before this one). Until it is in the build, the overload is looked up by its EXACT shape —
    // (List<ChatMessage>, string, …, CancellationToken, int? maxIterations), every other parameter optional — so this
    // compiles before and after; when the build has none, the caller is told maxSteps was not applied (never silent).
    // TODO(merge after T1): replace with _agentService.ExecuteAgenticAsync(history, prompt, ct: ct, maxIterations: limit).
    private static System.Reflection.MethodInfo? _stepLimitedLoop;
    private static bool _stepLimitedLoopProbed;

    /// <summary>The agent loop overload that takes a per-run step limit, or null when this build has none.</summary>
    internal static System.Reflection.MethodInfo? FindStepLimitedAgentLoop()
    {
        if (_stepLimitedLoopProbed) return _stepLimitedLoop;
        try
        {
            _stepLimitedLoop = typeof(CodeAgentService).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == nameof(CodeAgentService.ExecuteAgenticAsync)
                                     && m.ReturnType == typeof(Task<AgentLoopResult>) && IsStepLimitedShape(m.GetParameters()));
        }
        catch { _stepLimitedLoop = null; }
        _stepLimitedLoopProbed = true;
        return _stepLimitedLoop;
    }

    private static bool IsStepLimitParameter(System.Reflection.ParameterInfo p) =>
        p.ParameterType == typeof(int?) && p.Name == "maxIterations";

    /// <summary>(List&lt;ChatMessage&gt; history, string message, …) with exactly one CancellationToken and one
    /// "int? maxIterations", and nothing else that has to be supplied.</summary>
    private static bool IsStepLimitedShape(System.Reflection.ParameterInfo[] ps) =>
        ps.Length >= 4
        && ps[0].ParameterType == typeof(List<ChatMessage>) && ps[1].ParameterType == typeof(string)
        && ps.Count(p => p.ParameterType == typeof(CancellationToken)) == 1
        && ps.Count(IsStepLimitParameter) == 1
        && ps.Skip(2).All(p => p.ParameterType == typeof(CancellationToken) || IsStepLimitParameter(p) || p.HasDefaultValue);

    /// <summary>The agent loop for a pipe run, with the caller's step limit when this build supports one (when it
    /// does not, RunMcpTaskAsync has already put that in the caller's result).</summary>
    private Task<AgentLoopResult> RunAgentLoopAsync(List<ChatMessage> history, string prompt, int? maxSteps, CancellationToken ct)
    {
        if (maxSteps is int steps)
        {
            int limit = Math.Clamp(steps, 1, CodeAgentService.MaxIterationOverride);
            if (FindStepLimitedAgentLoop() is { } method)
            {
                var ps = method.GetParameters();
                var args = new object?[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    args[i] = i == 0 ? history
                        : i == 1 ? prompt
                        : p.ParameterType == typeof(CancellationToken) ? ct
                        : IsStepLimitParameter(p) ? (int?)limit
                        : p.DefaultValue;
                }
                return (Task<AgentLoopResult>)method.Invoke(_agentService, args)!;
            }
            _debugLog?.Warn("Chat", $"pipe run asked for maxSteps={limit}, but this build's agent loop has no per-run limit — the Max agent steps setting applies (the caller is told)");
        }
        return _agentService.ExecuteAgenticAsync(history, prompt, ct: ct);
    }

    /// <summary>
    /// Shows a pipe run in its chat as it happens: the reply streams into a bubble per step, thinking goes into that
    /// bubble's Thinking block, the harness's notices (auto-verify, compaction, recovered calls) appear inline — the
    /// same rules as a typed turn. Agent events arrive on the pool; the chat is touched on the dispatcher only.
    /// </summary>
    private sealed class LiveRunView
    {
        private readonly ChatViewModel _vm;
        private readonly ChatSession? _target;
        private readonly AgentRunScope? _scope;
        private readonly object _lock = new();
        private readonly StringBuilder _text = new();   // the current step's streamed text (agent thread, under _lock)
        private readonly CluadeX.Helpers.UiThrottle _throttle = new();
        private bool _finished;   // UI thread: set by Finish — a paced update still queued must not overwrite the final text
        private int _step = int.MinValue;
        private int _tokens;
        // UI thread only:
        private ChatMessage? _bubble;
        private int _bubbleStep = int.MinValue;
        private string _pendingThinking = "";

        public LiveRunView(ChatViewModel vm, ChatSession? target, AgentRunScope? scope) { _vm = vm; _target = target; _scope = scope; }

        /// <summary>Events of other runs (another chat working at the same time) are not this view's.</summary>
        private bool Mine => _scope == null || ReferenceEquals(AgentRunScope.Current, _scope);
        private ChatRun? Run => _scope?.Owner as ChatRun;

        public void Attach(CodeAgentService agent)
        {
            agent.OnAgenticStreamingToken += OnToken;
            agent.OnThinkingUpdate += OnThinking;
            agent.OnAgentStatus += OnStatus;
        }

        public void Detach(CodeAgentService agent)
        {
            agent.OnAgenticStreamingToken -= OnToken;
            agent.OnThinkingUpdate -= OnThinking;
            agent.OnAgentStatus -= OnStatus;
        }

        private static void Ui(Action action, bool wait = false)
        {
            var d = App.Current?.Dispatcher;
            if (d == null) return;
            if (wait && !d.CheckAccess()) d.Invoke(action);
            else if (d.CheckAccess()) action();
            else d.BeginInvoke(action);
        }

        private void OnToken(string token, int step)
        {
            if (!Mine) return;
            if (TryUnwrapThinking(token, out var thought))
            {
                if (thought.Length > 0) Ui(() => AddThinking(thought, step, replace: false));
                return;
            }
            string snapshot, previousText = "";
            bool newStep;
            int count;
            lock (_lock)
            {
                newStep = step != _step;
                // The previous step's last 1-2 tokens were never shown (updates go every 3): its bubble gets them now.
                if (newStep) { previousText = _text.ToString(); _text.Clear(); _step = step; }
                _text.Append(token);
                // Paced: the snapshot is built only when an update will go (see UiThrottle).
                snapshot = newStep || _throttle.Due ? _text.ToString() : "";
                count = ++_tokens;
            }
            if (Run is { } r) r.StreamingTokenCount++;
            if (newStep) Ui(() => OpenBubble(snapshot, step, previousText), wait: true);
            else if (snapshot.Length > 0) _throttle.Post(() => { if (!_finished && _bubble != null && _bubbleStep == step) _bubble.Content = snapshot; });
        }

        private void OpenBubble(string text, int step, string previousText)
        {
            if (Run is { ActiveStatus: { } st } run)
            {
                st.IsStreaming = false;
                run.Messages.Remove(st);
                run.ActiveStatus = null;
            }
            if (_bubble != null)
            {
                if (previousText.Length > 0) _bubble.Content = previousText;
                _bubble.IsStreaming = false;
            }
            _bubble = new ChatMessage { Role = MessageRole.Assistant, Content = text, IsStreaming = true, ThinkingContent = _pendingThinking };
            _bubbleStep = step;
            _pendingThinking = "";
            _vm.PostTurnMessage(_target, _bubble);
            _vm.ScrollToBottom?.Invoke();
        }

        /// <summary>CodeAgentService raises OnThinkingUpdate with real thinking AND with the step's visible text,
        /// which has already streamed into the bubble — that one is skipped.</summary>
        private void OnThinking(string text, int step)
        {
            if (!Mine || string.IsNullOrWhiteSpace(text)) return;
            string streamed;
            lock (_lock) streamed = _text.ToString();
            if (LooksAlreadyStreamed(text, streamed)) { Ui(() => { if (_bubble != null) _bubble.IsStreaming = false; }); return; }
            Ui(() => AddThinking(text, step, replace: true));
        }

        private void AddThinking(string text, int step, bool replace)
        {
            if (!_vm.ShowThinking) return;
            if (_bubble != null && _bubbleStep == step)
                _bubble.ThinkingContent = replace ? JoinThinking(_bubble.ThinkingContent, text) : _bubble.ThinkingContent + text;
            else
                _pendingThinking = replace ? JoinThinking(_pendingThinking, text) : _pendingThinking + text;
        }

        private void OnStatus(string status)
        {
            if (!Mine) return;
            bool notable = status.Contains('⤴') || status.Contains("Auto-verifying") || status.Contains("ตรวจ build")
                || status.Contains("Recovered tool call") || status.Contains("กู้คืน")
                || status.Contains("compacting") || status.Contains("บีบอัด")
                || status.Contains("truncated") || status.Contains("ถูกตัด");
            if (notable) _vm.OnToolStarting("agent", status);
        }

        /// <summary>UI thread: the run ended with <paramref name="finalText"/> (null = none). The last streamed bubble
        /// is finalized in place; with no bubble, the reply is added once; with neither, an honest note — the same
        /// safety net as a typed turn: tools ran but no summary is not an error (noSummary), nothing at all is.</summary>
        public void Finish(string? finalText, int toolsRun)
        {
            _finished = true;
            string streamed;
            lock (_lock) streamed = _text.ToString();
            ChatMessage final;
            if (_bubble != null)
            {
                if (finalText != null)
                {
                    _bubble.Content = finalText;
                    _bubble.CodeBlocks = _vm._codeExecutionService.ExtractCodeBlocks(finalText);
                }
                else if (_bubbleStep == _step && streamed.Length > 0 && _bubble.Content != streamed)
                {
                    _bubble.Content = streamed;
                }
                _bubble.IsStreaming = false;
                final = _bubble;
            }
            else
            {
                bool nothing = finalText == null && toolsRun == 0;
                final = new ChatMessage
                {
                    Role = MessageRole.Assistant,
                    Content = finalText
                        ?? (toolsRun > 0 ? $"*{LocF("chat.agent.noSummary", toolsRun)}*" : $"*{Loc("chat.agent.noReply")}*"),
                    HasError = nothing,
                    CodeBlocks = finalText != null ? _vm._codeExecutionService.ExtractCodeBlocks(finalText) : new(),
                };
                _vm.PostTurnMessage(_target, final);
            }
            if (_pendingThinking.Length > 0)
            {
                final.ThinkingContent = JoinThinking(final.ThinkingContent, _pendingThinking);
                _pendingThinking = "";
            }
            Close();
            _vm.ScrollToBottom?.Invoke();
        }

        /// <summary>UI thread: stop every streaming cursor this run left open.</summary>
        public void Close()
        {
            if (_bubble != null) _bubble.IsStreaming = false;
            if (Run is { ActiveStatus: { } st } run)
            {
                st.IsStreaming = false;
                run.ActiveStatus = null;
            }
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
                case MessageRole.User when m.HasAttachedImages || m.HasAttachedFiles:
                    // An earlier message's pictures and files are not resent (each costs tokens on every request): the
                    // model is told they were there and where, and can open one again (read_file / read_document).
                    result.Add(new ChatMessage
                    {
                        Role = MessageRole.User, Timestamp = m.Timestamp,
                        Content = (m.Content.Length > 0 ? m.Content + "\n" : "")
                                  + string.Join("\n", m.AttachedImages.Select(p => $"[the user attached an image: {p}]")
                                      .Concat(m.AttachedFiles.Select(EarlierAttachmentLine))),
                    });
                    break;
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
    private Task SendMessage() => SendMessageCore(UserInput?.Trim() ?? "", pipe: null);

    /// <summary>Pressing Send: <paramref name="input"/> is what the box holds. <paramref name="pipe"/> set = the same
    /// path for a message a pipe caller sent (cluadex.continue_chat): the box and its attached images are the user's
    /// draft and stay as they are, no batch (the caller waits for the answer), and the turn carries the caller's
    /// step limit and cancellation.</summary>
    private async Task SendMessageCore(string input, PipeTurn? pipe)
    {
        bool fromBox = pipe == null;
        void ClearBox() { if (fromBox) UserInput = string.Empty; }
        // Files dropped a moment ago may still be copying in (a big zip): the message waits for them rather than
        // going out without them. A second Send pressed meanwhile does nothing — the first one sends.
        if (fromBox && !IsGenerating && !_attachingFiles.IsCompleted)
        {
            if (_sendWaitsForAttachments) return;
            _sendWaitsForAttachments = true;
            try { await WaitForAttachingAsync(); }
            finally { _sendWaitsForAttachments = false; }
        }
        int images = fromBox ? PendingImages.Count : 0;
        int files = fromBox ? PendingFiles.Count : 0;
        if (string.IsNullOrEmpty(input) && images == 0 && files == 0) return;
        if (fromBox) _dictation?.Hush();   // a reply still being read aloud stops: the user has moved on
        // A reply is running in this chat: what was typed goes to it at its next step, as in Claude Code. Enter used
        // to do nothing at all (the box was even disabled) until the run ended. Attachments wait for the next turn.
        if (IsGenerating)
        {
            if (!fromBox) return;   // the pipe caller is told the chat is running (ContinueChatForPipeAsync)
            if (input.Length > 0) SendIntoRunningReply(input);
            if (images + files > 0) StatusText = Loc("chat.attach.waitForReply");
            return;
        }

        // ─── Built-in slash commands ───
        // Handled BEFORE the provider check: they act on the chat itself and need no model. Behind the check,
        // /compact, /clear and /new were refused with "not configured" whenever the provider wasn't up.
        if (input.Equals("/compact", StringComparison.OrdinalIgnoreCase))
        {
            ClearBox();
            CompactConversation();
            return;
        }
        if (input.Equals("/clear", StringComparison.OrdinalIgnoreCase))
        {
            ClearBox();
            await ClearChatAsync();
            return;
        }
        if (input.Equals("/close", StringComparison.OrdinalIgnoreCase) || input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
        {
            // Close this chat's session: stop what its agent left running, then start a fresh chat (this one is kept).
            ClearBox();
            if (!await EnsureIdleAsync()) return;
            string? closing = CurrentSession?.Id;
            NewSession();
            await CleanUpSessionAsync(closing, deleteImages: false);
            return;
        }
        if (input.Equals("/new", StringComparison.OrdinalIgnoreCase))
        {
            ClearBox();
            NewSession();
            return;
        }
        // Claude Dev's CLI is signed out: open its login in a console; the user finishes it in the browser. Before the
        // provider check on purpose — a signed-out Claude Dev is exactly when the provider is not ready.
        if (input.Equals("/login", StringComparison.OrdinalIgnoreCase))
        {
            ClearBox();
            // The CLI of the provider answering this chat: Gemini Dev signs in with Google, everything else is Claude Code.
            // Antigravity Dev signs in with Google through agy.
            var active = _providerManager.ActiveProviderType;
            bool opened = active switch
            {
                AiProviderType.GeminiDev => _providerManager.GetProvider(AiProviderType.GeminiDev) is Services.Providers.GeminiDevProvider gd && gd.OpenLoginTerminal(),
                AiProviderType.AntigravityDev => _providerManager.GetProvider(AiProviderType.AntigravityDev) is Services.Providers.AntigravityDevProvider agy && agy.OpenLoginTerminal(),
                AiProviderType.OpenAIDev => _providerManager.GetProvider(AiProviderType.OpenAIDev) is Services.Providers.OpenAIDevProvider od && od.OpenLoginTerminal(),
                _ => Services.Providers.ClaudeDevProvider.OpenLoginTerminal(),
            };
            string key = active switch { AiProviderType.GeminiDev => "gemini", AiProviderType.AntigravityDev => "agy", AiProviderType.OpenAIDev => "codex", _ => "" };
            Messages.Add(new ChatMessage
            {
                Role = MessageRole.System,
                Content = opened ? "🔑 " + Loc(key.Length > 0 ? $"chat.login.{key}Opened" : "chat.login.opened")
                                 : "⚠ " + Loc(key.Length > 0 ? $"chat.login.{key}Failed" : "chat.login.failed"),
            });
            ScrollToBottom?.Invoke();
            return;
        }

        if (!await EnsureProviderReadyAsync()) return;

        // The chat on screen since startup was never opened through LoadSessionAsync: its folder comes back here.
        FollowChatProject(CurrentSession);

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

        // Batch switch on: a plain question goes out at half price and its answer arrives later (no tools).
        // Skills stay live — they are instructions for the agent loop. A message with attachments goes the normal way:
        // a batch request carries text only, and the files would be dropped without a word.
        if (fromBox && BatchMode && activeSkill == null && CurrentSession != null && images + files == 0)
        {
            await SendAsBatchAsync(input);
            return;
        }

        _pet?.NoteMessage(input);
        _pet?.NoteTurnStarted();
        var userMsg = new ChatMessage
        {
            Role = MessageRole.User, Content = input,
            AttachedImages = fromBox ? PendingImages.ToList() : new(),
            AttachedFiles = fromBox ? PendingFiles.ToList() : new(),
        };
        if (fromBox) { PendingImages.Clear(); PendingFiles.Clear(); }
        Messages.Add(userMsg);
        ClearBox();
        // Only attachments: the model still needs a line of text with them.
        string modelInput = input.Length > 0 ? input
            : images > 0 && files == 0 ? "Look at the attached image(s)."
            : "Look at the attached file(s).";
        await RunTurnAsync(userMsg, modelInput, activeSkill, isSkill: !string.IsNullOrEmpty(skillPromptOverride), pipe);
    }

    /// <summary>
    /// Typed while the chat's reply is running: shown at once as the user's bubble and handed to the run, which reads
    /// it at its next step (AgentRunScope.AddUserNote). A slash command needs the chat idle — it stays in the box.
    /// </summary>
    private void SendIntoRunningReply(string text)
    {
        var run = RunOf(CurrentSession);
        if (run?.Scope == null) return;
        if (text.StartsWith("/")) { StatusText = Loc("chat.steer.noSlash"); return; }
        var bubble = new ChatMessage { Role = MessageRole.User, Content = text };
        run.Messages.Add(bubble);
        // Said in the chat, not only the status bar: the user sees it was taken and when it will be done.
        run.Messages.Add(new ChatMessage { Role = MessageRole.System, Content = Loc("chat.steer.queued") });
        run.Scope.AddUserNote(text, bubble);
        UserInput = string.Empty;
        StatusText = Loc("chat.steer.queued");
        _pet?.NoteMessage(text);
    }

    /// <summary>A run ended with something typed that its agent never read (it finished first, or the provider answers
    /// in one call): moved after the answer and sent as the next turn — or, after Stop, put back in the box. Nothing
    /// typed is lost either way.</summary>
    private void SendLeftoverNotes(ChatRun run, List<(string Text, object? Tag)> notes)
    {
        string text = string.Join("\n\n", notes.Select(n => n.Text));
        foreach (var n in notes) if (n.Tag is ChatMessage b) run.Messages.Remove(b);
        if (!ReferenceEquals(run.Session, CurrentSession) || run.Cts?.IsCancellationRequested == true)
        {
            if (ReferenceEquals(run.Session, CurrentSession))
                UserInput = string.IsNullOrWhiteSpace(UserInput) ? text : UserInput + "\n" + text;
            else
                _debugLog?.Warn("Chat", "a message typed during a run that is no longer on screen was not sent");
            return;
        }
        var bubble = new ChatMessage { Role = MessageRole.User, Content = text };
        Messages.Add(bubble);
        Detached(() => RunTurnAsync(bubble, text, null, false));
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
    private async Task RunTurnAsync(ChatMessage userMsg, string input, SkillDefinition? activeSkill, bool isSkill, PipeTurn? pipe = null)
    {
        await AutoOpenProjectsAsync(input);
        // Related folders ride along with the message so the model knows they are there (and how to reach them).
        if (CurrentSession is { ExtraFolders.Count: > 0 } withExtras)
            input += "\n\n[Open folders — main: " + _fileSystemService.AppWorkingDirectory + "; also open: "
                + string.Join(", ", withExtras.ExtraFolders) + ". Relative paths are in the main folder; use absolute paths for the others.]";
        var turn = BeginTurn();
        if (pipe != null && FlowRun is { } pipeRun)
        {
            // A message a pipe caller sent (cluadex.continue_chat): marked as such (the BrainX model never relays it
            // back into the room), with the caller's step limit; the caller reads its outcome from this run.
            pipeRun.Scope.FromPipe = true;
            pipeRun.MaxSteps = pipe.MaxSteps;
            pipe.Run = pipeRun;
            pipe.UserMessage = userMsg;
        }
        var session = CurrentSession;

        // ─── Session resume: persist transcript BEFORE API call ───
        // If the app crashes mid-generation, the conversation up to this point is saved.
        SaveNow();

        // A pipe caller going away (or cancelling) stops the turn like Stop does.
        _cts = pipe != null ? CancellationTokenSource.CreateLinkedTokenSource(pipe.Ct) : new CancellationTokenSource();
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
        // Attached images reach a model only through the native tool loop (the plain chat path is text-only); a
        // provider without it gets their file paths in words. Attached files go through the loop too — it has
        // read_document / copy_file / extract_archive even with no project open.
        bool nativeProvider = _providerManager.ActiveProvider.SupportsNativeToolUse;
        bool cliProvider = _providerManager.ActiveProvider is Services.Providers.IDelegatedCliProvider;
        bool hasAttachments = userMsg.HasAttachedImages || userMsg.HasAttachedFiles;
        bool imagesNeedLoop = hasAttachments && nativeProvider;
        if (userMsg.HasAttachedImages && !nativeProvider && !cliProvider)
            input += "\n\n" + CodeAgentService.AttachedImagesNote(userMsg.AttachedImages);
        // Every attachment's path, a document's text, an archive unpacked — shaped for what this provider can do.
        bool attachmentsCancelled = false;
        if (hasAttachments)
        {
            try
            {
                var digest = await BuildAttachmentDigestAsync(userMsg, nativeProvider, ct);
                input += digest.Text;
                if (AgentRunScope.Current is { } attachScope)
                {
                    attachScope.AttachmentFolders = digest.Folders.ToList();
                    if (nativeProvider)
                    {
                        // The pictures, plus the pages of a scanned PDF for a model that sees.
                        var pictures = userMsg.AttachedImages.Concat(digest.ExtraImages).ToList();
                        if (pictures.Count > 0) attachScope.UserImages = await LoadAttachedImagesAsync(pictures);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { attachmentsCancelled = true; }
        }
        Func<List<ChatMessage>, CancellationToken, Task> run;
        bool withTools = true;
        if (attachmentsCancelled)                                             // Stop while the files were being read
            run = (h, c) => Task.FromException(new OperationCanceledException(c));
        else if (isSkill && HasProject)                                       // skills always run in agentic mode
            run = (h, c) => RunAgentic(input, h, c);
        else if (AutonomousMode && HasProject && _autonomousService != null)
            run = (h, c) => RunAutonomous(input, h, c);
        else if ((AgenticMode && HasProject) || mcpOnlyTools || imagesNeedLoop)
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
                    var lastAssistant = TurnMessages.LastOrDefault(m => m.Role == MessageRole.Assistant && m.IsStreaming);
                    if (lastAssistant != null)
                    {
                        lastAssistant.IsStreaming = false;
                        lastAssistant.IsInterrupted = true;
                        if (string.IsNullOrWhiteSpace(lastAssistant.Content))
                        {
                            TurnMessages.Remove(lastAssistant);
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
            _generationStopwatch.Stop();
            StopElapsedTimer();
            _cts?.Dispose();
            _cts = null;
            UpdateContextInfo();

            // Save immediately after each completed interaction
            SaveNow();
            bool turnOk = TurnMessages.LastOrDefault(m => m.Role == MessageRole.Assistant)?.HasError != true;
            if (pipe == null) SpeakReplyIfWanted(userMsg);
            EndTurn(turn);
            _pet?.NoteTurnDone(_providerManager.ActiveProviderType.ToString(),
                _providerManager.ActiveProviderType is AiProviderType.Local or AiProviderType.LlamaServer, turnOk);
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
        TurnMessages.Add(msg);
        _activeAgentStatusMsg = msg;
        ScrollToBottom?.Invoke();
    }

    private void RemoveActiveStatus()
    {
        if (_activeAgentStatusMsg == null) return;
        TurnMessages.Remove(_activeAgentStatusMsg);
        _activeAgentStatusMsg = null;
    }

    /// <summary>After a failed attempt: a reply bubble that was mid-stream keeps what it said but stops
    /// "typing"; an empty one is removed.</summary>
    private void FinalizeDanglingBubbles()
    {
        foreach (var m in TurnMessages.Where(m => m.Role == MessageRole.Assistant && m.IsStreaming).ToList())
        {
            m.IsStreaming = false;
            if (string.IsNullOrWhiteSpace(m.Content)) TurnMessages.Remove(m);
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
        // The build/test was not run (unseen edits, not approved): said in a line that stays with the chat — the phase
        // banner is transient, and the model's reply above may read as if the work were verified.
        if (result.VerifyDeclined && !final.Content.Contains(result.LastVerifyOutput))
            PostTurnMessage(target, new ChatMessage { Role = MessageRole.System, Content = "✋ " + result.LastVerifyOutput });
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
        var run = Cur!;   // this turn's run: its list, status row, counters — whichever chat is on screen
        bool Mine() => ReferenceEquals(AgentRunScope.Current, run.Scope);   // events of other runs are not ours
        int outAtStart = _costTracker.TotalOutputTokens;

        // ─── Status bar — minimal (inline chat handles detail) ───
        var progress = new Progress<string>(status =>
        {
            run.StatusBase = status;
            if (!ReferenceEquals(run.Session, CurrentSession)) return;   // the status bar belongs to the chat on screen
            int elapsed = (int)run.Stopwatch.Elapsed.TotalSeconds;
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
        var tokenThrottle = new CluadeX.Helpers.UiThrottle();
        int agenticTokenCount = 0;
        // Thinking that arrived while no reply bubble of its step was open (a tool-only step): shown on the
        // next bubble. Touched on the UI thread only.
        string pendingThinking = "";
        // Bubbles whose text is final (their step ended, or the turn did). A batched update queued earlier can run
        // after that — Invoke outranks BeginInvoke — and must not put a shorter snapshot back. UI thread only.
        var closedBubbles = new HashSet<ChatMessage>();

        void OnStreamToken(string token, int step)
        {
            if (!Mine()) return;
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
                // The previous step's bubble gets ALL its text: the screen is only updated every 3 tokens, so its
                // last 1-2 tokens were never shown — a reply cut off when the next step began (a message typed
                // while it worked, a tool call after the text).
                string previousText = streamSb.ToString();
                streamSb.Clear();
                streamSb.Append(token);
                lastStreamStep = step;

                App.Current?.Dispatcher.Invoke(() =>
                {
                    if (run.ActiveStatus != null)
                    {
                        run.ActiveStatus.IsStreaming = false;
                        run.Messages.Remove(run.ActiveStatus);
                        run.ActiveStatus = null;
                    }

                    if (streamingMsg != null)
                    {
                        if (previousText.Length > 0) streamingMsg.Content = previousText;
                        streamingMsg.IsStreaming = false;
                        closedBubbles.Add(streamingMsg);
                    }

                    streamingMsg = new ChatMessage
                    {
                        Role = MessageRole.Assistant,
                        Content = token,
                        IsStreaming = true,
                        ThinkingContent = pendingThinking,
                    };
                    pendingThinking = "";
                    run.Messages.Add(streamingMsg);
                    ScrollToBottom?.Invoke();
                });
                run.StreamingTokenCount++;
                return;
            }

            streamSb.Append(token);
            run.StreamingTokenCount++;

            // Paced UI updates (~12/s, below keyboard input — see UiThrottle)
            if (tokenThrottle.Due)
            {
                string snapshot = streamSb.ToString();
                int capturedCount = run.StreamingTokenCount;
                var bubble = streamingMsg;   // this step's bubble — by the time the update runs, a new step may own the field
                tokenThrottle.Post(() =>
                {
                    if (bubble != null && !closedBubbles.Contains(bubble))
                        bubble.Content = snapshot;

                    // Status update (for the chat on screen only)
                    if (ReferenceEquals(run.Session, CurrentSession))
                    {
                        int elapsed = (int)run.Stopwatch.Elapsed.TotalSeconds;
                        double tps = elapsed > 0 ? capturedCount / (double)elapsed : 0;
                        StatusText = tps > 0
                            ? LocF("chat.status.generatingRate", capturedCount, tps)
                            : LocF("chat.status.generatingCount", capturedCount);
                    }
                });
            }

            // Throttle scroll-to-bottom (every 20 tokens), below input too
            if (++scrollThrottle % 20 == 0)
                App.Current?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => ScrollToBottom?.Invoke());
        }

        // The model's thinking, shown as the collapsed "Thinking" block above its reply. ThinkingContent used
        // to be assigned nowhere, so a user who ticked "Think" paid for extended thinking and never saw it.
        // OnThinkingUpdate carries two different things — CodeAgentService also raises it with the step's
        // VISIBLE text, which has already streamed into the bubble — so text the bubble already holds is skipped.
        void OnThinking(string text, int step)
        {
            if (!Mine() || string.IsNullOrWhiteSpace(text)) return;
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
            if (!Mine()) return;
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
            var result = await Task.Run(() => _agentService.ExecuteAgenticAsync(history, input, progress, ct, run.MaxSteps), CancellationToken.None);
            run.LoopResult = result;

        // Finalize any remaining inline status
        App.Current?.Dispatcher.Invoke(() =>
        {
            if (run.ActiveStatus != null)
            {
                run.ActiveStatus.IsStreaming = false;
                run.ActiveStatus = null;
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
                closedBubbles.Add(streamingMsg);
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
                var placeholder = run.Messages.LastOrDefault(m =>
                    m.Role == MessageRole.AgentStatus && (m.Content?.StartsWith("⏳") ?? false));
                if (placeholder != null) run.Messages.Remove(placeholder);

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
        var run = Cur;   // the run whose tool is starting, captured at event time (its chat may not be on screen)
        if (run == null) return;
        if (toolName != "agent") run.ToolStarts.Enqueue(System.Diagnostics.Stopwatch.GetTimestamp());
        App.Current?.Dispatcher.BeginInvoke(async () =>
        {
            DropDraft(run);   // the call it was writing is now running: its own row/status takes over
            if (run.ActiveStatus?.Content == $"⏵ {statusMessage}")
                return;

            // The running balloon (owner 2026-10-17): a background call that will fold into the work row right above
            // says what it runs ON that row — no status line of its own, so the cards stay inside one balloon.
            if (toolName != "agent" && BalloonFor(run.Messages, toolName, true) is { } work)
            {
                if (run.LiveRow != null && !ReferenceEquals(run.LiveRow, work)) run.LiveRow.IsLive = false;
                run.LiveRow = work;
                work.LiveText = statusMessage;
                work.IsLive = true;
                RefreshSessionBadges();
                ScrollToBottom?.Invoke();
                return;
            }
            if (run.LiveRow != null) { run.LiveRow.IsLive = false; run.LiveRow = null; }

            // Min display time 700ms — ensure previous status was visible long enough
            var elapsed = DateTime.UtcNow - _lastToolStatusTime;
            if (elapsed < TimeSpan.FromMilliseconds(700) && run.ActiveStatus != null)
                await Task.Delay(TimeSpan.FromMilliseconds(700) - elapsed);

            // Previous tool status stays visible (breadcrumb trail)
            if (run.ActiveStatus != null)
                run.ActiveStatus.IsStreaming = false;

            // AgentStatus, not Assistant: as an Assistant row this breadcrumb was saved, reloaded and sent to the
            // model as a turn it never wrote, and the Steps switch could not hide it.
            var statusMsg = new ChatMessage
            {
                Role = MessageRole.AgentStatus,
                Content = $"⏵ {statusMessage}",
                IsStreaming = true,
                ToolKind = toolName == "agent" ? null : toolName,   // a tool's line folds away once its row arrives
            };
            run.Messages.Add(statusMsg);
            run.ActiveStatus = statusMsg;
            RefreshSessionBadges();
            _lastToolStatusTime = DateTime.UtcNow;
            ScrollToBottom?.Invoke();
        });
    }

    // Tool categories for grouping consecutive calls
    private static readonly HashSet<string> ReadTools = new(StringComparer.OrdinalIgnoreCase)
        { "read_file", "list_files", "glob", "search_files", "search_content", "grep", "grep_search", "glob_search",
          "find_symbol", "list_symbols", "codebase_search", "lsp_diagnostics", "memory_list" };
    private static readonly HashSet<string> ShellTools = new(StringComparer.OrdinalIgnoreCase)
        { "run_command", "powershell", "run_build", "run_tests", "bg_output", "bg_list", "task_output", "task_list" };
    private static readonly HashSet<string> WebTools = new(StringComparer.OrdinalIgnoreCase)
        { "web_search", "web_fetch" };
    private static readonly HashSet<string> PlanTools = new(StringComparer.OrdinalIgnoreCase)
        { "todo_write", "memory_save", "brain_recall", "brain_search" };
    /// <summary>Rows that stay on their own: an edit (its own diff), a question to the owner, a sub-agent (its map card).</summary>
    private static readonly HashSet<string> NeverGrouped = new(StringComparer.OrdinalIgnoreCase)
        { "write_file", "edit_file", "multi_edit", "notebook_edit", "ask_user", "agent_spawn", "subagent_invoke", "plan_mode", "skill_invoke" };
    private static readonly HashSet<string> GitTools = new(StringComparer.OrdinalIgnoreCase)
        { "git_status", "git_diff", "git_log", "git_branch", "git_add", "git_stash" };

    private static string GetToolCategory(string toolName)
    {
        if (ReadTools.Contains(toolName)) return "read";
        if (GitTools.Contains(toolName)) return "git";
        if (ShellTools.Contains(toolName)) return "shell";
        if (WebTools.Contains(toolName)) return "web";
        if (PlanTools.Contains(toolName)) return "plan";
        // MCP calls to one server in a row (brain task_update, agent_inbox, agent_send …) are one row, not a wall of
        // rows (owner 2026-10-16: "รวบไว้ด้วยกันได้ไหม มันเยอะไป").
        if (toolName.StartsWith("mcp__", StringComparison.Ordinal) && toolName.Split("__") is { Length: >= 3 } mcp) return "mcp:" + mcp[1];
        return toolName; // unique category = no grouping
    }

    /// <summary>A call as one small box inside a group row.</summary>
    private ToolStepView StepOf(string toolName, Dictionary<string, string>? args, string? elapsed, string? workDir)
    {
        var (_, verbKey) = ToolLook.Of(toolName);
        string verb = verbKey == "tool.verb.mcp" ? (ToolLook.McpServer(toolName) is { } srv ? $"{srv} · {ShortToolName(toolName).Split('/').Last()}" : Loc(verbKey)) : Loc(verbKey);
        return new ToolStepView(toolName, verb, ToolTargetOf(toolName, args, workDir), elapsed);
    }

    /// <summary>The balloon the next background call folds into, if any: the last work row, looking past help notes,
    /// finished status lines and the short lines the agent wrote since (its "what I'll do now"). Those lines become
    /// captions inside the balloon (FoldedIntoBalloon) — what runs stays inside, the final answer outside.</summary>
    private ChatMessage? BalloonFor(IList<ChatMessage> list, string toolName, bool success)
    {
        bool edit = FileEditTools.Contains(toolName);
        if (!success || !edit && !Groupable(GetToolCategory(toolName))) return null;
        var narration = new List<ChatMessage>();
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var m = list[i];
            if (m.IsSideNote || m.FoldedIntoBalloon || m.Role == MessageRole.AgentStatus && !m.IsStreaming) continue;
            if (m.Role == MessageRole.Assistant && !m.HasError && (m.Content?.Length ?? 0) <= 600 && narration.Count < 4)
            {
                narration.Add(m);
                continue;
            }
            if (edit ? !FoldsEditInto(m, toolName) : !IsFoldable(m)) return null;
            if (edit) StartEditSteps(m);
            for (int k = narration.Count - 1; k >= 0; k--)
            {
                var n = narration[k];
                n.FoldedIntoBalloon = true;
                string say = NarrationText(n.Content);
                if (say.Length == 0) continue;
                m.AddGroupStep(new ToolStepView("narration", say, null, null));
                m.LiveText = say;
            }
            return m;
        }
        return null;
    }

    /// <summary>The words of a narration line, without the [ACTION:] blocks the text protocol puts in the same reply.</summary>
    private static string NarrationText(string? content)
    {
        string t = System.Text.RegularExpressions.Regex.Replace(content ?? "", @"\[ACTION:[\s\S]*?(\[/ACTION\]|$)", "").Trim();
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ");
        return t.Length > 180 ? t[..180] + "…" : t;
    }

    /// <summary>The last row a tool result could fold into: help notes and finished status lines are looked past.</summary>
    private static ChatMessage? LastWorkRow(IList<ChatMessage> list)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var m = list[i];
            if (m.IsSideNote || m.FoldedIntoBalloon || m.Role == MessageRole.AgentStatus && !m.IsStreaming) continue;
            return m;
        }
        return null;
    }

    /// <summary>A work row the next background call may fold into: a successful tool row with no diff or picture.</summary>
    private static bool IsFoldable(ChatMessage m) =>
        m.Role == MessageRole.ToolAction && m.ToolSuccess && !m.HasDiff && m.ThumbnailPath == null
        && (m.IsCollapsedGroup || m.ToolKind != null && Groupable(GetToolCategory(m.ToolKind)));

    private static readonly HashSet<string> FileEditTools = new(StringComparer.OrdinalIgnoreCase) { "edit_file", "multi_edit", "write_file" };

    /// <summary>A successful edit row the next edit folds into, whichever file it touches.</summary>
    internal static bool FoldsEditInto(ChatMessage row, string toolName) =>
        row.Role == MessageRole.ToolAction && row.ToolSuccess && row.HasDiff && !row.IsCollapsedGroup
        && row.ToolKind != null && FileEditTools.Contains(row.ToolKind) && FileEditTools.Contains(toolName);

    /// <summary>The first edit of a row becomes its first box, as a group's first call does.</summary>
    private static void StartEditSteps(ChatMessage row)
    {
        if (!row.HasGroupSteps) row.AddGroupStep(new ToolStepView(row.ToolKind, row.ToolVerb ?? "", row.ToolTarget, row.ToolElapsedTime));
    }

    /// <summary>"mcp__brainx-brain__task_update" → "brainx-brain/task_update" for a group row's list.</summary>
    private static string ShortToolName(string toolName) =>
        toolName.StartsWith("mcp__", StringComparison.Ordinal) && toolName.Split("__") is { Length: >= 3 } p
            ? p[1] + "/" + string.Join("__", p.Skip(2)) : toolName;

    /// <summary>A group row's targets: "task_update, agent_inbox ×2" — a repeat counts instead of being listed again.</summary>
    private static string JoinTargets(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(b)) return a ?? "";
        if (string.IsNullOrWhiteSpace(a)) return b;
        var parts = a.Split(", ").ToList();
        for (int i = 0; i < parts.Count; i++)
        {
            var m = System.Text.RegularExpressions.Regex.Match(parts[i], @"^(.*) ×(\d+)$");
            string name = m.Success ? m.Groups[1].Value : parts[i];
            if (name != b) continue;
            parts[i] = $"{name} ×{(m.Success ? int.Parse(m.Groups[2].Value) + 1 : 2)}";
            return string.Join(", ", parts);
        }
        return a + ", " + b;
    }

    /// <summary>Everything in a row of the same kind folds into one row (owner: "ฉลาดในการรวมกลุ่มให้มากที่สุด") —
    /// a different tool of the same family included — except the rows that must stay visible on their own.</summary>
    private static bool Groupable(string category) => !NeverGrouped.Contains(category);

    /// <summary>Raised (on the UI thread) when the agent mutates a file: (full path — or a path relative to the window's
    /// project, firstChangedLine, diff — null when none was computed). The Code Editor subscribes to open/refresh that file live, scroll to the change and
    /// type the new lines in.</summary>
    public event Action<string, int, EditDiffResult?>? FileMutatedByAgent;

    /// <summary>"chatId:problem" pairs already explained (ChromeHelp), so a run that keeps failing says it once.</summary>
    private readonly HashSet<string> _chromeHelpShown = new();

    private void OnToolExecuted(ToolResult toolResult)
    {
        // The run whose tool finished, captured at event time: the row goes into ITS chat's live list whether or not
        // that chat is on screen. Without a run (a callback after the run ended, a tool run by hand) it goes on screen.
        var run = Cur;
        ChatSession? target = run?.Session;
        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            var list = run?.Messages ?? (target == null || ReferenceEquals(target, CurrentSession) ? Messages : null);
            bool onScreen = list != null;
            if (run?.ActiveStatus != null)
            {
                run.ActiveStatus.IsStreaming = false;
                // A tool's own "⏵ Reading x" line goes once its row arrives: left in the list (folded away on screen) it
                // stood between two tool rows, so no two calls ever folded together in a real run.
                if (run.ActiveStatus.ToolKind != null) list?.Remove(run.ActiveStatus);
                run.ActiveStatus = null;
            }

            // Chrome trouble in plain words, once per chat per kind: not signed in, an unanswered permission, Save As.
            var chromeProblem = Services.Chrome.ChromeHelp.Diagnose(toolResult.ToolName, (toolResult.Output ?? "") + " " + toolResult.Error, toolResult.Success);
            if (chromeProblem != Services.Chrome.ChromeHelp.Problem.None && list != null
                && _chromeHelpShown.Add($"{target?.Id ?? CurrentSession?.Id}:{chromeProblem}"))
            {
                bool thai = (_settingsService.Settings.Language ?? "th").StartsWith("th", StringComparison.OrdinalIgnoreCase);
                list.Add(new ChatMessage { Role = MessageRole.System, IsSideNote = true, Content = Services.Chrome.ChromeHelp.Advice(chromeProblem, thai) });
            }

            if (run != null) DropDraft(run);

            // Live-follow: open/refresh the changed file in the Code Editor and scroll to the edit — for the chat on
            // screen, in the window's project; with its "ตามดูโค้ด" tick on the Code page also comes forward. Other chats
            // keep working off screen.
            string? runFolder = run?.Scope?.WorkingDirectory;
            string windowFolder = _fileSystemService.AppWorkingDirectory;
            bool ticked = FollowsEdits(target ?? CurrentSession);
            if (toolResult.Success
                && !string.IsNullOrEmpty(toolResult.FilePath)
                && ShouldFollowEdit(IsOnScreen(target), ticked || _settingsService.Settings.LiveEditFollow, runFolder, windowFolder))
            {
                string full = Path.GetFullPath(Path.Combine(runFolder ?? windowFolder, toolResult.FilePath!));
                FileMutatedByAgent?.Invoke(full, toolResult.Diff?.FirstChangedLine ?? 1, toolResult.Diff);
                // Only a file the editor can show (the window's project) is worth leaving the page for.
                if (ticked && IsInsideFolder(full, windowFolder)) ShowEditorRequested?.Invoke();
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

            string rowOutput = RowOutput(toolResult);
            int outputLines = string.IsNullOrEmpty(rowOutput)
                ? 0
                : rowOutput.Split('\n').Length;
            string? toolElapsed = null;
            if (run != null && run.ToolStarts.TryDequeue(out long started))
            {
                double sec = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
                toolElapsed = sec < 10 ? $"{sec:0.0}s" : $"{sec:0}s";
            }

            string displayName = FormatToolDisplayName(toolResult.ToolName, toolResult.Arguments);

            // A screenshot (or any image a tool returned) shows on its own row as a thumbnail — by its saved file
            // path only; the image bytes never reach the chat or codex.db. Such a row is never merged into a group.
            string? thumbnail = ToolThumbnailPath(toolResult);

            // ─── Tool call grouping: collapse consecutive same-category calls ───
            string category = GetToolCategory(toolResult.ToolName);
            var lastMsg = onScreen && thumbnail == null
                ? BalloonFor(list!, toolResult.ToolName, toolResult.Success) ?? LastWorkRow(list!)
                : null;
            if (lastMsg?.ThumbnailPath != null) lastMsg = null;   // nor is anything merged into one

            // Every successful background call in a row folds into ONE row, whatever its family (owner 2026-10-17: "ไม่ต้อง
            // แตกกล่องออกไป ให้รวมกลุ่มไว้เลย เมื่อกดแยกดูจะเห็นกล่องภายใน") — opened, it lists each call as its own box.
            // An edit (its diff), a question, a sub-agent, a picture and a failure still stand on their own.
            if (lastMsg is { Role: MessageRole.ToolAction, IsCollapsedGroup: true }
                && Groupable(category)
                && lastMsg.ToolSuccess && toolResult.Success)
            {
                if (GetToolCategory(lastMsg.CollapsedToolNames.LastOrDefault() ?? "") != category) lastMsg.ToolVerb = Loc("chat.tools.groupVerb");
                // Merge into existing group
                lastMsg.CollapsedCount++;
                lastMsg.CollapsedToolNames.Add(toolResult.ToolName);
                lastMsg.AddGroupStep(StepOf(toolResult.ToolName, toolResult.Arguments, toolElapsed, run?.Scope?.WorkingDirectory ?? WorkingDirectory));
                lastMsg.Content = LocF("chat.tools.groupContent", lastMsg.CollapsedCount, string.Join(", ", lastMsg.CollapsedToolNames.Select(ShortToolName).Distinct()));
                lastMsg.ToolName = LocF("chat.tools.groupName", lastMsg.CollapsedCount);
                lastMsg.ToolTarget = JoinTargets(lastMsg.ToolTarget, ToolTargetOf(toolResult.ToolName, toolResult.Arguments, run?.Scope?.WorkingDirectory ?? WorkingDirectory));
                lastMsg.ToolOutput = CapGroupOutput((lastMsg.ToolOutput ?? "") + "\n---\n" + (toolResult.Output ?? ""));
                lastMsg.ToolOutputLines += outputLines;
                // Don't add new message — just updated existing
                ScrollToBottom?.Invoke();
                return;
            }

            string lastCategory = lastMsg == null ? "" : GetToolCategory(lastMsg.ToolKind ?? lastMsg.CollapsedToolNames.LastOrDefault() ?? lastMsg.ToolName?.Split(' ').LastOrDefault() ?? "");
            if (lastMsg is { Role: MessageRole.ToolAction } && !lastMsg.IsCollapsedGroup && !lastMsg.HasDiff
                && lastMsg.ToolKind != null && Groupable(lastCategory)
                && lastMsg.ToolSuccess && toolResult.Success
                && Groupable(category))
            {
                if (lastCategory != category) lastMsg.ToolVerb = Loc("chat.tools.groupVerb");
                // Convert single ToolAction to group + add this one
                string prevToolRaw = lastMsg.ToolKind ?? (lastMsg.CollapsedToolNames.Count > 0 ? lastMsg.CollapsedToolNames[0] : "read_file");
                // The first call becomes the group's first box, from what its own row showed.
                lastMsg.AddGroupStep(new ToolStepView(lastMsg.ToolKind ?? prevToolRaw, lastMsg.ToolVerb ?? ShortToolName(prevToolRaw),
                    lastMsg.ToolTarget, lastMsg.ToolElapsedTime));
                lastMsg.AddGroupStep(StepOf(toolResult.ToolName, toolResult.Arguments, toolElapsed, run?.Scope?.WorkingDirectory ?? WorkingDirectory));
                lastMsg.IsCollapsedGroup = true;
                lastMsg.CollapsedCount = 2;
                lastMsg.CollapsedToolNames = new List<string> { prevToolRaw, toolResult.ToolName };
                lastMsg.Content = LocF("chat.tools.groupContent", 2, string.Join(", ", lastMsg.CollapsedToolNames.Select(ShortToolName).Distinct()));
                lastMsg.ToolTarget = JoinTargets(lastMsg.ToolTarget, ToolTargetOf(toolResult.ToolName, toolResult.Arguments, run?.Scope?.WorkingDirectory ?? WorkingDirectory));
                lastMsg.ToolName = LocF("chat.tools.groupName", 2);
                lastMsg.ToolOutput = CapGroupOutput((lastMsg.ToolOutput ?? "") + "\n---\n" + (toolResult.Output ?? ""));
                lastMsg.ToolOutputLines += outputLines;
                ScrollToBottom?.Invoke();
                return;
            }

            // Edits in a row are one row, one file or many (owner 2026-10-18: "ทำไมมันเยอะจังกล่องการแก้ไข ไม่ได้รวมไว้เหรอ",
            // 2026-10-31: "การแก้ไฟล์ก็ควรรวบเป็นหมวดไว้ด้วย"): the +/− add up, its diff shows each change in turn and each
            // file is a box when it is opened. A failure, or anything but a short line between them, starts a new row.
            string? editTarget = ToolTargetOf(toolResult.ToolName, toolResult.Arguments, run?.Scope?.WorkingDirectory ?? WorkingDirectory);
            if (lastMsg != null && toolResult.Success && toolResult.Diff is { Lines.Count: > 0 } editDiff
                && FoldsEditInto(lastMsg, toolResult.ToolName))
            {
                StartEditSteps(lastMsg);
                lastMsg.AddGroupStep(StepOf(toolResult.ToolName, toolResult.Arguments, toolElapsed, run?.Scope?.WorkingDirectory ?? WorkingDirectory));
                lastMsg.AppendEdit(editDiff, toolElapsed, toolResult.Output, outputLines, JoinTargets(lastMsg.ToolTarget, editTarget), editTarget);
                lastMsg.ToolOutput = CapGroupOutput(lastMsg.ToolOutput ?? "");
                int files = (lastMsg.ToolTarget ?? "").Split(", ").Length;
                if (files > 1) lastMsg.ToolVerb = LocF("chat.tools.editGroupVerb", files);
                ScrollToBottom?.Invoke();
                return;
            }

            // Normal: add as individual ToolAction message — the balloon above it, if any, is done.
            if (run?.LiveRow != null) { run.LiveRow.IsLive = false; run.LiveRow = null; }
            var (_, verbKey) = ToolLook.Of(toolResult.ToolName);
            string toolVerb = verbKey == "tool.verb.mcp" ? (ToolLook.McpServer(toolResult.ToolName) ?? Loc(verbKey)) : Loc(verbKey);
            var msg = new ChatMessage
            {
                ToolKind = toolResult.ToolName,
                ToolVerb = toolVerb,
                ToolTarget = ToolTargetOf(toolResult.ToolName, toolResult.Arguments, run?.Scope?.WorkingDirectory ?? WorkingDirectory),
                ToolElapsedTime = toolElapsed,
                DiffAdded = toolResult.Diff?.Added ?? 0,
                DiffRemoved = toolResult.Diff?.Removed ?? 0,
                Role = MessageRole.ToolAction,
                Content = !string.IsNullOrEmpty(toolResult.Summary) ? toolResult.Summary : displayName,
                ToolName = displayName,
                ToolSummary = !string.IsNullOrEmpty(toolResult.Summary) ? toolResult.Summary : displayName,
                ToolOutput = rowOutput,
                ToolSuccess = toolResult.Success,
                HasError = !toolResult.Success,
                ToolArguments = argsDisplay,
                ToolInputSummary = inputSummary,
                ToolOutputLines = outputLines,
                CollapsedToolNames = new List<string> { toolResult.ToolName },
                DiffLines = toolResult.Diff?.Lines,
                DiffStat = toolResult.Diff?.Stat,
                ThumbnailPath = thumbnail,
                ImageCount = toolResult.Images?.Count ?? 0,
            };
            PostTurnMessage(target, msg);
            ScrollToBottom?.Invoke();
        });
    }

    /// <summary>What a tool's row shows: its output, and for a failure the reason too. Browser, desktop and MCP tools
    /// put the reason only in Error, so their failed rows were empty (Reverse X run, 2026-10-28: browser_open failed
    /// twice with nothing to say why, while the agent read "outside the open project").</summary>
    internal static string RowOutput(ToolResult result)
    {
        string output = result.Output ?? "";
        string error = (result.Error ?? "").Trim();
        if (result.Success || error.Length == 0 || output.Contains(error, StringComparison.Ordinal)) return output;
        return string.IsNullOrWhiteSpace(output) ? error : output.TrimEnd() + "\n" + error;
    }

    /// <summary>The first image a tool saved to disk, if any (a JPEG/PNG that exists) — what its row shows.</summary>
    internal static string? ToolThumbnailPath(ToolResult result)
    {
        if (result.Images is not { Count: > 0 } images) return null;
        foreach (var image in images)
        {
            string? path = image?.FilePath;
            if (IsOpenableImage(path)) return path;
        }
        return null;
    }

    /// <summary>A local image file that exists — the only thing a thumbnail click hands to the shell.</summary>
    public static bool IsOpenableImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (!Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".gif")) return false;
            return File.Exists(path);
        }
        catch { return false; }
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

    /// <summary>What a tool acted on, for its row: the path relative to the project, the command, the URL, the query —
    /// or the MCP tool's own name.</summary>
    private static string ToolTargetOf(string toolName, Dictionary<string, string>? args, string? root)
    {
        string? target = null;
        if (args != null)
        {
            target = args.GetValueOrDefault("path") ?? args.GetValueOrDefault("file_path") ?? args.GetValueOrDefault("notebook_path")
                  ?? args.GetValueOrDefault("command") ?? args.GetValueOrDefault("url") ?? args.GetValueOrDefault("pattern")
                  ?? args.GetValueOrDefault("query") ?? args.GetValueOrDefault("skill") ?? args.GetValueOrDefault("branch")
                  ?? args.GetValueOrDefault("task") ?? args.GetValueOrDefault("question");
        }
        if (string.IsNullOrWhiteSpace(target))
        {
            if (ToolLook.McpServer(toolName) is { } server)
                return toolName[("mcp__" + server + "__").Length..];
            return toolName;
        }
        target = target.Replace('\n', ' ').Trim();
        // A shell command is the long one (ssh -i … -o … admin@… 'bash …' ran past 300 characters): the row names the
        // programs it ran; the full command is in the row's tooltip and in its expanded details.
        if (args != null && args.ContainsKey("command") && !args.ContainsKey("path") && !args.ContainsKey("file_path")
            && !args.ContainsKey("notebook_path"))
            return ShortCommand(target);
        if (!string.IsNullOrEmpty(root) && Path.IsPathRooted(target))
        {
            try
            {
                string full = Path.GetFullPath(target), r = Path.GetFullPath(root).TrimEnd('\\', '/');
                if (full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    target = Path.GetRelativePath(r, full).Replace('\\', '/');
            }
            catch { /* not a path after all */ }
        }
        return target.Length > 90 ? target[..90] + "…" : target;
    }

    /// <summary>"cd web &amp;&amp; npm run build 2&gt;&amp;1 | tail -25; ls ../site" → "npm run build · ls": each step's program, with
    /// its subcommand for tools that have one (npm run x, git push, dotnet build). cd / echo / sleep / export and what
    /// a pipe feeds into are left out. At most 3 steps, 60 characters.</summary>
    internal static string ShortCommand(string command)
    {
        var steps = new List<string>();
        foreach (var raw in System.Text.RegularExpressions.Regex.Split(command, @"&&|\|\||;"))
        {
            var words = raw.Split('|')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .SkipWhile(w => w.Contains('=') && !w.StartsWith('-'))   // FOO=1 cmd …
                .ToList();
            if (words.Count == 0) continue;
            string prog = Path.GetFileName(words[0].Trim('"', '\'', '('));
            if (prog is "" or "cd" or "echo" or "sleep" or "true" or "set" or "export" or "pushd" or "popd") continue;
            string step = prog;
            if (words.Count > 1 && !words[1].StartsWith('-') && prog is "npm" or "npx" or "pnpm" or "yarn" or "git" or "dotnet"
                    or "cargo" or "go" or "docker" or "pip" or "composer" or "flutter" or "gh" or "bun")
            {
                step += " " + words[1];
                if (words[1] == "run" && words.Count > 2) step += " " + words[2];
            }
            if (!steps.Contains(step)) steps.Add(step);
            if (steps.Count == 3) break;
        }
        string s = steps.Count == 0 ? command.Trim() : string.Join(" · ", steps);
        return s.Length > 60 ? s[..60] + "…" : s;
    }

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
        if (_elapsedTimer != null) return;   // already ticking for another run
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _elapsedTimer.Tick += (_, _) =>
        {
            if (_runs.Count == 0) { _elapsedTimer?.Stop(); _elapsedTimer = null; return; }
            // The status bar describes the chat on screen (its run, if it has one).
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
        // Another run still going: it keeps the ticker (it stops by itself once no run is left).
        if (_runs.Any(r => !ReferenceEquals(r, FlowRun))) return;
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
        var tokenThrottle = new CluadeX.Helpers.UiThrottle();
        bool replyComplete = false;   // UI thread only
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

            // Subsequent tokens: paced (~12/s, below keyboard input — see UiThrottle)
            if (tokenThrottle.Due)
            {
                int capturedCount = tokenCount;
                string snapshot = sb.ToString();
                tokenThrottle.Post(() =>
                {
                    if (!replyComplete) assistantMsg.Content = snapshot;
                    if (_activeAgentStatusMsg != null)
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

        // Final content flush — ensure last tokens are shown. A batched update still queued runs after this (Invoke
        // outranks BeginInvoke) and must not cut the reply back to an older snapshot.
        App.Current?.Dispatcher.Invoke(() =>
        {
            replyComplete = true;
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

    /// <summary>Pause the chat's run: it finishes the step in hand and stops cleanly (Resume / "continue" goes on).</summary>
    private void PauseGeneration()
    {
        if (RunOf(CurrentSession)?.Scope is not { } scope) return;
        scope.PauseRequested = true;
        StatusText = Loc("chat.pause.requested");
    }

    /// <summary>The chat's last reply ended on Pause: Resume is offered (also after a restart — the reply is stored).</summary>
    public bool CanResume => !IsGenerating && Messages.LastOrDefault(m => m.Role == MessageRole.Assistant) is { } last
                             && (last.Content ?? "").StartsWith(AgentRunScope.PausedMarker, StringComparison.Ordinal);

    private void ResumeGeneration()
    {
        if (!CanResume) return;
        UserInput = Loc("chat.pause.resumeText");
        if (SendMessageCommand.CanExecute(null)) SendMessageCommand.Execute(null);
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

    public void RefreshLocalModelsList() => _ = RescanLocalModelsAsync();

    private bool _modelScanRunning, _modelScanAgain;

    /// <summary>The model folders are walked to the bottom, so never on the UI thread: on a folder of 114,739 files with
    /// no model in it that froze the window for seconds. One scan at a time; a request during one runs once after it.</summary>
    private async Task RescanLocalModelsAsync()
    {
        if (_modelScanRunning) { _modelScanAgain = true; return; }
        _modelScanRunning = true;
        try
        {
            do
            {
                _modelScanAgain = false;
                ApplyLocalModels(await Task.Run(_huggingFaceService.GetLocalModels));
            }
            while (_modelScanAgain);
        }
        catch (Exception ex) { _debugLog?.Warn("Chat", "scanning the model folders failed", ex); }
        finally { _modelScanRunning = false; }
    }

    private void ApplyLocalModels(List<ModelInfo> models)
    {
        // The answer lands a moment later now: an unchanged list leaves the box alone (it may be open), and a model being
        // loaded stays picked (nothing counts as loaded yet, so the box would go blank).
        if (!models.Select(m => m.LocalPath).SequenceEqual(LocalModels.Select(m => m.LocalPath)))
        {
            LocalModels.Clear();
            foreach (var m in models)
                LocalModels.Add(m);
        }
        if (IsLoadingModel) return;

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
        await CleanUpSessionAsync(CurrentSession?.Id, deleteImages: true);   // the rows that showed them are gone
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

/// <summary>What a pipe run hands back to its caller: the final reply text, and whether the run failed, was
/// stopped, did not finish, or lost its caller (then the MCP result carries isError=true).</summary>
public sealed record McpTaskOutcome(string Text, bool IsError);
