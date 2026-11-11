using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// Agent runs that belong to their chat, not to the screen. Each turn (typed, pipe task, code review, code block)
/// is a <see cref="ChatRun"/>: the chat it writes into (its own live message list), its Stop token, its status row,
/// its plan, its stopwatch, and an <see cref="AgentRunScope"/> that pins its project folder and per-run tool state.
/// The user can open another chat — or start another run there — while one runs; switching only changes which
/// list is on screen. Before this, switching chats meant stopping the reply ("stop it first?").
/// </summary>
public partial class ChatViewModel
{
    private sealed class ChatRun
    {
        public required ChatSession Session { get; init; }
        public required ObservableCollection<ChatMessage> Messages { get; init; }
        public AgentRunScope Scope { get; set; } = null!;
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>The turn's Stop. Its cancel is remembered in <see cref="Stopped"/>: the source is disposed before the turn
        /// ends, and the end needs to know (no next-step box after a Stop).</summary>
        public CancellationTokenSource? Cts
        {
            get => _cts;
            set
            {
                _cts = value;
                try { value?.Token.Register(() => Stopped = true); } catch (ObjectDisposedException) { }
            }
        }
        private CancellationTokenSource? _cts;
        public volatile bool Stopped;
        public ChatMessage? ActiveStatus;
        /// <summary>The running balloon: the background-work row the current calls fold into (IsLive while they run).</summary>
        public ChatMessage? LiveRow;
        /// <summary>"⏵ writing file x — 3,200 chars…" while the model still streams a long tool input.</summary>
        public ChatMessage? DraftRow;
        public ChatMessage? TodoMsg;
        /// <summary>The chat's last plan card with work left when this run began: its plan came over into the run
        /// (PlanRecord.CarryNote), and the card carries on in place unless the model starts a new task.</summary>
        public ChatMessage? CarriedPlanMsg;
        /// <summary>This command's agent map card (made when its first sub-agent starts).</summary>
        public ChatMessage? AgentMapMsg;
        /// <summary>This command's split-chat chips (made with its first new_chat).</summary>
        public ChatMessage? SplitsMsg;
        /// <summary>The files this turn wrote and its pictures: the change card at its end (ChatViewModel.Vibe).</summary>
        public readonly Services.Agents.TurnChanges Changes = new();
        /// <summary>The tests this turn ran and what the model said it skipped: the test card at its end (TurnTests).</summary>
        public readonly Services.Agents.TurnTests Tests = new();
        public readonly Stopwatch Stopwatch = new();
        public string StatusBase = "";
        public int StreamingTokenCount;
        /// <summary>Rows in its chat at the last background save (skip a save when nothing was added).</summary>
        public int SavedCount = -1;
        /// <summary>When each running tool started (Stopwatch timestamps, in call order): its row shows how long it took.</summary>
        public readonly System.Collections.Concurrent.ConcurrentQueue<long> ToolStarts = new();
        /// <summary>A step limit for this run only (a pipe caller's maxSteps); null = the setting.</summary>
        public int? MaxSteps;
        /// <summary>The agent loop's result when this turn ran it (null = it answered another way).</summary>
        public AgentLoopResult? LoopResult;
        /// <summary>Send now on a run that cannot stop one reply (a CLI agent, the [ACTION:] loop): it was stopped, and
        /// what was typed goes out as the next turn instead of back into the box.</summary>
        public bool SendNotesAfterStop;
        /// <summary>Rows its chat had when the turn began: how it ended is read from the rows after it.</summary>
        public int FirstRow;
        /// <summary>The activity card already knows the model is writing its answer (reset when a tool starts).</summary>
        public volatile bool ActivityWriting;
        /// <summary>Puts all the text the open reply bubble has streamed into it — the screen shows it a few tokens late,
        /// and with no next token nothing catches it up. Read-aloud calls it before it reads a bubble. Set by the agent
        /// loop while it runs; UI thread.</summary>
        public Action? FlushReplyText;

        /// <summary>Who started the turn, for a supervisor: "pipe" (a program over the pipe), "split_parent" (new_chat from
        /// another chat's agent) or "user". The pipe flag is read live: it is set right after the run begins.</summary>
        public string StartedBy => Scope?.FromPipe == true ? "pipe" : StartedBySplit ? "split_parent" : "user";
        public bool StartedBySplit;
        /// <summary>The last tool it started (its name only — never its arguments).</summary>
        public volatile string? LastTool;
        private long _lastProgressTicks = DateTime.UtcNow.Ticks;
        /// <summary>The last sign of work: a token, a tool starting or ending (UTC).</summary>
        public DateTime LastProgressUtc => new(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);
        public void Touch() => Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>new_chat's first turn is about to begin in this chat (UI thread): its run is marked as started by the
    /// asking chat's agent.</summary>
    private string? _splitTurnPending;

    /// <summary>Runs in progress (UI thread only).</summary>
    private readonly List<ChatRun> _runs = new();

    /// <summary>The run whose async flow is executing this code (null on the UI's own events).</summary>
    private static ChatRun? FlowRun => AgentRunScope.Current?.Owner as ChatRun;

    private ChatRun? RunOf(ChatSession? s) =>
        s == null ? null : _runs.FirstOrDefault(r => ReferenceEquals(r.Session, s) || r.Session.Id == s.Id);

    /// <summary>The run this code is about: the one running it, else the run of the chat on screen.</summary>
    private ChatRun? Cur => FlowRun ?? RunOf(CurrentSession);

    /// <summary>Where a running turn writes: its own chat's live list (on screen or not).</summary>
    private ObservableCollection<ChatMessage> TurnMessages => Cur?.Messages ?? Messages;

    // The former app-wide turn fields, now per run (same names, so the turn code reads as before).
    private CancellationTokenSource? _cts { get => Cur?.Cts; set { if (Cur is { } r) r.Cts = value; } }
    private ChatSession? _turnSession => Cur?.Session;
    private TaskCompletionSource? _turnDone => Cur?.Done;
    private ChatMessage? _activeAgentStatusMsg { get => Cur?.ActiveStatus; set { if (Cur is { } r) r.ActiveStatus = value; } }
    private ChatMessage? _todoListMsg { get => Cur?.TodoMsg; set { if (Cur is { } r) r.TodoMsg = value; } }
    private readonly Stopwatch _idleStopwatch = new();
    private Stopwatch _generationStopwatch => Cur?.Stopwatch ?? _idleStopwatch;
    private int _streamingTokenCount { get => Cur?.StreamingTokenCount ?? 0; set { if (Cur is { } r) r.StreamingTokenCount = value; } }
    private string _lastStatusBase { get => Cur?.StatusBase ?? ""; set { if (Cur is { } r) r.StatusBase = value; } }

    // ─── what the screen shows ───

    /// <summary>The chat on screen is generating (Stop is offered, its input waits). Other chats can still send.</summary>
    public bool IsGenerating => RunOf(CurrentSession) != null;

    private bool _sendBlocked;
    /// <summary>Send is possible in the chat on screen: it has no run of its own (and no batch is being sent).</summary>
    public bool CanSend
    {
        get => !IsGenerating && !_sendBlocked;
        set { _sendBlocked = !value; OnPropertyChanged(); }
    }

    /// <summary>"⏳ 2 other chats are working: …" while runs go on in chats that are not on screen.</summary>
    public string RunningElsewhereText
    {
        get
        {
            var others = _runs.Where(r => !ReferenceEquals(r.Session, CurrentSession)).ToList();
            if (others.Count == 0) return "";
            string first = others[0].Session.Title;
            bool waiting = others.Any(r => r.Messages.Any(m => m.Role is MessageRole.PermissionRequest && !m.PermissionAnswered
                                                                || m.Role == MessageRole.Question && !m.QuestionAnswered));
            string text = others.Count == 1 ? LocF("chat.runs.elsewhereOne", first) : LocF("chat.runs.elsewhereMany", others.Count, first);
            return waiting ? text + " · " + Loc("chat.runs.waitingForYou") : text;
        }
    }

    public bool HasRunningElsewhere => _runs.Any(r => !ReferenceEquals(r.Session, CurrentSession));

    /// <summary>A run (any chat) is working in <paramref name="folder"/> or a folder inside it — its main folder, or one
    /// it opened beside it (open_project add=true) — the editor and the UI Studio are read-only then. UI thread.</summary>
    public bool IsAgentWorkingIn(string folder) =>
        !string.IsNullOrEmpty(folder) && _runs.Any(r => RunTouches(r, folder));

    /// <summary>Is a run working in <paramref name="folder"/> waiting for the owner (a permission card, an ask_user
    /// question)? UI thread.</summary>
    public bool IsAgentWaitingIn(string folder) =>
        !string.IsNullOrEmpty(folder) && _runs.Any(r => RunTouches(r, folder) && RunWaits(r));

    /// <summary>Open the chat of the run working in <paramref name="folder"/> (one that waits for the owner first). False
    /// when no run works there.</summary>
    public async Task<bool> ShowRunChatInAsync(string folder)
    {
        var here = _runs.Where(r => RunTouches(r, folder)).ToList();
        var pick = here.FirstOrDefault(RunWaits) ?? here.FirstOrDefault();
        if (pick == null) return false;
        if (!ReferenceEquals(pick.Session, CurrentSession)) await LoadSessionAsync(pick.Session.Id);
        return true;
    }

    private static bool RunTouches(ChatRun r, string folder)
    {
        if (r.Scope is not { } scope) return false;
        bool Overlaps(string? wd) => !string.IsNullOrEmpty(wd) && (IsInsideFolder(wd, folder) || IsInsideFolder(folder, wd));
        return Overlaps(scope.WorkingDirectory) || scope.ExtraFolders.Any(Overlaps);
    }

    private static bool RunWaits(ChatRun r) =>
        r.Messages.Any(m => m.Role is MessageRole.PermissionRequest && !m.PermissionAnswered || m.Role == MessageRole.Question && !m.QuestionAnswered);

    /// <summary>Open the chat of a run that is going on elsewhere (the one waiting for an answer first).</summary>
    public ICommand ShowRunningChatCommand => _showRunningChatCommand ??= new AsyncRelayCommand(async () =>
    {
        var others = _runs.Where(r => !ReferenceEquals(r.Session, CurrentSession)).ToList();
        var pick = others.FirstOrDefault(r => r.Messages.Any(m => m.Role is MessageRole.PermissionRequest && !m.PermissionAnswered
                                                                   || m.Role == MessageRole.Question && !m.QuestionAnswered))
                   ?? others.FirstOrDefault();
        if (pick != null) await LoadSessionAsync(pick.Session.Id);
    });
    private ICommand? _showRunningChatCommand;

    private void RaiseRunStateChanged()
    {
        OnPropertyChanged(nameof(IsGenerating));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(RunningElsewhereText));
        OnPropertyChanged(nameof(HasRunningElsewhere));
        OnPropertyChanged(nameof(LiveModelEffortText)); OnPropertyChanged(nameof(LiveEffortSuffix)); OnPropertyChanged(nameof(LiveEffortLevel));
        OnPropertyChanged(nameof(CanResume));
        RaiseOpenFolders();
        RefreshSessionBadges();
        RebuildSidebarIfActiveChanged();
    }

    /// <summary>Each chat in the sidebar shows how its run stands: working (with the step), waiting for the owner, or
    /// finished while the owner looked elsewhere. UI thread.</summary>
    private void RefreshSessionBadges()
    {
        foreach (var run in _runs)
        {
            bool waiting = run.Messages.Any(m => m.Role is MessageRole.PermissionRequest && !m.PermissionAnswered
                                                 || m.Role == MessageRole.Question && !m.QuestionAnswered);
            run.Session.RunStatus = waiting ? "waiting" : "working";
            ActivityWaiting(run.Session.Id, waiting);
            NoteRunWaiting(run, waiting);
            run.Session.RunStep = waiting ? Loc("chat.runs.waitingForYou") : (run.ActiveStatus?.Content ?? "").TrimStart('⏵', ' ');
        }
        if (CurrentSession is { } open) open.RunStatus = RunBadge.OnOpen(open.RunStatus);
        RefreshBuddy();
    }

    // ─── open folders (the project + related ones) ───

    /// <summary>Every folder the chat on screen works in: the project first, then related ones.</summary>
    private List<string> OpenFolders()
    {
        var list = new List<string>();
        string main = _fileSystemService.AppWorkingDirectory;
        if (!string.IsNullOrWhiteSpace(main)) list.Add(main);
        if (CurrentSession != null)
            foreach (var f in CurrentSession.ExtraFolders)
                if (!list.Any(x => PathEquals(x, f))) list.Add(f);
        return list;
    }

    /// <summary>"📁 2 โฟลเดอร์ · CluadeX, Atmos" — shown when more than the project is open.</summary>
    public string OpenFoldersText
    {
        get
        {
            var all = OpenFolders();
            if (all.Count < 2) return "";
            return "📁 " + LocF("chat.folders.count", all.Count) + " · "
                + string.Join(", ", all.Select(f => System.IO.Path.GetFileName(f.TrimEnd('\\', '/'))));
        }
    }

    public string OpenFoldersTip => string.Join("\n", OpenFolders());
    public bool HasExtraFolders => OpenFolders().Count > 1;

    private void RaiseOpenFolders()
    {
        OnPropertyChanged(nameof(OpenFoldersText));
        OnPropertyChanged(nameof(OpenFoldersTip));
        OnPropertyChanged(nameof(HasExtraFolders));
    }

    /// <summary>A run opened a related folder beside its project (open_project add=true): the chat keeps it.</summary>
    private void OnAgentAddedFolder(AgentRunScope? scope, string path)
    {
        var session = (scope?.Owner as ChatRun)?.Session ?? CurrentSession;
        if (session == null) return;
        if (!session.ExtraFolders.Any(f => PathEquals(f, path)) && !PathEquals(session.ProjectPath, path))
            session.ExtraFolders.Add(path);
        RaiseOpenFolders();
        RaiseRunScopeChanged();   // the run works in that folder too now (the UI Studio locks when it shows it)
        _debugLog?.Info("Chat", $"agent opened related folder {path}");
    }

    /// <summary>A run's folders changed while it runs (open_project, with add=true or without): no run started or
    /// ended, yet <see cref="IsAgentWorkingIn"/> may answer differently now. UI thread.</summary>
    public event Action? RunScopeChanged;

    private void RaiseRunScopeChanged()
    {
        if (RunScopeChanged == null) return;
        foreach (var d in RunScopeChanged.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch (Exception ex) { _debugLog?.Warn("Chat", "a run-folders listener failed", ex); }
        }
    }

    /// <summary>
    /// Before a turn: the projects the message names are opened at once — a new chat about "atmos" lands in
    /// D:\Code\Atmos before the model even starts. Known projects (the registry of folders opened before) match by
    /// name; failing that, a new chat asks BrainX, whose notes record where each project lives. The first named
    /// project becomes the chat's project (only when none is open or the chat is new); the others open beside it.
    /// Announced in one line: how many folders and which.
    /// </summary>
    private async Task AutoOpenProjectsAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.StartsWith("/")) return;
        var session = CurrentSession;
        if (session == null) return;
        bool newChat = !Messages.Any(m => m.Role == MessageRole.Assistant);
        try
        {
            var named = MatchKnownProjects(input);
            if (named.Count == 0 && newChat && BrainSyncService.Current is { IsBrainAvailable: true } brain)
                named = await FindProjectsInBrainAsync(brain, input);
            if (named.Count == 0) return;

            var opened = new List<string>();
            string main = _fileSystemService.AppWorkingDirectory;
            bool mayMoveMain = newChat || string.IsNullOrWhiteSpace(main);
            if (mayMoveMain && !named.Any(n => PathEquals(n, main)))
            {
                // (the brain was asked first: the user may be in another chat by now, and that chat keeps its folder)
                if (ReferenceEquals(session, CurrentSession)) ApplyWorkingDirectory(named[0], announce: false, showEditor: false);
                ProjectRegistryService.Current?.Record(named[0]);
                session.ProjectPath = named[0];
                session.ExtraFolders.Clear();
                opened.Add(named[0]);
                main = named[0];
                RebuildVisibleSessions();
            }
            foreach (var p in named)
            {
                if (PathEquals(p, main) || session.ExtraFolders.Any(f => PathEquals(f, p))) continue;
                session.ExtraFolders.Add(p);
                ProjectRegistryService.Current?.Record(p);
                opened.Add(p);
            }
            if (opened.Count == 0) return;
            var all = OpenFolders();
            Messages.Add(new ChatMessage
            {
                Role = MessageRole.System,
                Content = "📁 " + LocF("chat.folders.opened", all.Count) + "\n"
                    + string.Join("\n", all.Select((f, i) => (i == 0 ? "• " : "• + ") + f)),
            });
            RaiseOpenFolders();
            _debugLog?.Info("Chat", $"auto-opened {string.Join(", ", opened)} from the message");
        }
        catch (Exception ex)
        {
            _debugLog?.Warn("Chat", "auto-opening the projects a message names failed", ex);
        }
    }

    /// <summary>Generic folder names that would match ordinary words — never matched by name alone.</summary>
    private static readonly HashSet<string> TooGenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "src", "app", "apps", "code", "test", "tests", "demo", "temp", "tmp", "web", "api", "lib", "docs", "data",
        "main", "new", "old", "project", "projects", "server", "client", "site", "game", "games", "tools", "bin",
    };

    /// <summary>Where the text names a folder: "Thaiprompt-Affiliate" also matches "thaiprompt affiliate". -1 = not named.</summary>
    private static int NamedAt(string text, string folder)
    {
        string name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (name.Length < 4 || TooGenericNames.Contains(name)) return -1;
        var parts = System.Text.RegularExpressions.Regex.Split(name, @"[-_. ]+").Where(x => x.Length > 0)
            .Select(System.Text.RegularExpressions.Regex.Escape);
        var m = System.Text.RegularExpressions.Regex.Match(text,
            @"(?<![A-Za-z0-9])" + string.Join(@"[-_. ]?", parts) + @"(?![A-Za-z0-9])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Index : -1;
    }

    /// <summary>Known projects (the registry) the text names, in the order they appear.</summary>
    private static List<string> MatchKnownProjects(string text)
    {
        var hits = new List<(int At, string Path)>();
        foreach (var e in ProjectRegistryService.Current?.Recent(200) ?? [])
        {
            if (!System.IO.Directory.Exists(e.Path)) continue;
            int at = NamedAt(text, e.Path);
            if (at >= 0 && !hits.Any(h => PathEquals(h.Path, e.Path))) hits.Add((at, e.Path));
        }
        return hits.OrderBy(h => h.At).Select(h => h.Path).Take(4).ToList();
    }

    /// <summary>Project folders BrainX notes give for the text: absolute folder paths in the top hits whose folder name
    /// the text names. 5 s at most — the turn never waits long on the brain.</summary>
    private static async Task<List<string>> FindProjectsInBrainAsync(BrainSyncService brain, string text)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string q = Services.Mcp.McpServerManager.RedactSecrets(text.Length > 160 ? text[..160] : text) + " project path";
        string raw = await brain.SearchAsync(q, limit: 5, semantic: true, cts.Token);
        if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("(")) return new();
        raw = raw.Replace("\\\\", "\\");   // JSON-escaped backslashes
        var hits = new List<(int At, string Path)>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(raw, @"[A-Za-z]:\\[^\s""'`<>|*?]+"))
        {
            string p = m.Value.TrimEnd('.', ',', ';', ')', ']', '\\', '/');
            try
            {
                if (System.IO.File.Exists(p)) p = System.IO.Path.GetDirectoryName(p) ?? p;
                if (!System.IO.Directory.Exists(p) || PermissionService.IsUnsafeAcceptEditsRoot(p)) continue;
            }
            catch { continue; }
            int at = NamedAt(text, p);
            if (at >= 0 && !hits.Any(h => PathEquals(h.Path, p))) hits.Add((at, p));
        }
        return hits.OrderBy(h => h.At).Select(h => h.Path).Take(4).ToList();
    }

    /// <summary>Start a command's work and return at once; a failure is reported like an async command's.</summary>
    private static void Detached(Func<Task> work)
    {
        _ = Run();
        async Task Run()
        {
            try { await work(); }
            catch (Exception ex) { CommandErrorSink.Report(nameof(ChatViewModel), ex); }
        }
    }

    // ─── ask_user ───

    private Task<string> AskUserAsync(string question, List<string> options)
    {
        var run = Cur;   // the run asking (its chat gets the question; its Stop withdraws it)
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return Task.FromResult("(no answer: CluadeX has no window open)");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var who = CharacterForBox(run);   // the girl who asks: the provider's, or the room member answering
        var msg = new ChatMessage
        {
            Role = MessageRole.Question,
            Content = "❓ " + question.Trim(),
            QuestionOptions = options,
            // kept with the row: a card the app closed on can be answered again after the restart (RunRecovery)
            ToolOutput = RunRecovery.QuestionJson(options, null),
            QuestionGirl = who.Girl,
            Character = who,
            QuestionCallback = answer => tcs.TrySetResult(answer),
        };
        var token = run?.Cts?.Token ?? CancellationToken.None;
        var reg = token.Register(() =>
        {
            // (not while the app closes: the card stays open in the saved chat for the restart to offer again)
            if (tcs.TrySetCanceled()) dispatcher.InvokeAsync(() => { if (!_appClosing) msg.CloseQuestion(Loc("chat.ask.cancelled")); RaiseRunStateChanged(); });
        });
        _ = tcs.Task.ContinueWith(_ => { reg.Dispose(); dispatcher.InvokeAsync(RaiseRunStateChanged); }, TaskScheduler.Default);
        dispatcher.InvokeAsync(() =>
        {
            (run?.Messages ?? Messages).Add(msg);
            RaiseRunStateChanged();   // "waiting for you" on the other-chats bar when it is not on screen
            ChimeForBox(Services.Voice.ChimeKind.Ask);
            ScrollToBottom?.Invoke();
        });
        return tcs.Task;
    }

    /// <summary>Several questions on one card, a step each, with Back / Next and a review before they go (or one
    /// multi-select question with tick buttons). One answer per step; the run's Stop withdraws it like a single one.</summary>
    private Task<IReadOnlyList<string>> AskUserStepsAsync(IReadOnlyList<AskStep> steps)
    {
        var run = Cur;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return Task.FromResult<IReadOnlyList<string>>(steps.Select(_ => "(no answer: CluadeX has no window open)").ToList());
        var tcs = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var who = CharacterForBox(run);
        var msg = new ChatMessage { Role = MessageRole.Question, QuestionGirl = who.Girl, Character = who };
        msg.SetSteps(steps, answers => tcs.TrySetResult(answers));
        msg.ToolOutput = RunRecovery.QuestionJson(null, steps);
        var token = run?.Cts?.Token ?? CancellationToken.None;
        var reg = token.Register(() =>
        {
            // (not while the app closes: the card stays open in the saved chat for the restart to offer again)
            if (tcs.TrySetCanceled()) dispatcher.InvokeAsync(() => { if (!_appClosing) msg.CloseQuestion(Loc("chat.ask.cancelled")); RaiseRunStateChanged(); });
        });
        _ = tcs.Task.ContinueWith(_ => { reg.Dispose(); dispatcher.InvokeAsync(RaiseRunStateChanged); }, TaskScheduler.Default);
        dispatcher.InvokeAsync(() =>
        {
            (run?.Messages ?? Messages).Add(msg);
            RaiseRunStateChanged();
            ChimeForBox(Services.Voice.ChimeKind.Ask);
            ScrollToBottom?.Invoke();
        });
        return tcs.Task;
    }

    /// <summary>A box now waits for the owner's answer — her question, a permission prompt, the red prompt: the chime
    /// (AppSettings.AttentionChime), so a turn waiting in the background or behind another window is heard.</summary>
    private void ChimeForBox(Services.Voice.ChimeKind kind)
    {
        if (!_settingsService.Settings.AttentionChime) return;
        if (Services.Voice.AttentionChime.Play(kind))
            _debugLog?.Info("Chime", kind == Services.Voice.ChimeKind.Danger ? "the red prompt waits for the owner" : "a box waits for the owner's answer");
    }

    // ─── start / end ───

    /// <summary>Start a run in the chat on screen: it gets that chat's live list and a scope that pins the project
    /// folder open now. The scope is entered for the calling flow (the turn's own async method).</summary>
    private TaskCompletionSource BeginTurn()
    {
        var session = CurrentSession;
        if (session == null)
        {
            NewSession();
            session = CurrentSession!;
        }
        string? taskFolder = TaskFolderFor(session);
        string? folder = taskFolder ?? _fileSystemService.AppWorkingDirectory;
        // A split chat in its own worktree said where it works when it opened (its 🌿 notice): no row every turn.
        if (taskFolder != null && WorktreeFolderOf(session) == null)
        {
            Messages.Add(new ChatMessage { Role = MessageRole.System, Content = "📁 " + LocF("chat.resume.inTaskFolder", taskFolder) });
            _debugLog?.Info("Chat", $"turn runs in the chat's own folder {taskFolder} (window: {_fileSystemService.AppWorkingDirectory})");
        }
        return BeginTurnFor(session, Messages, folder);
    }

    /// <summary>Start a run in <paramref name="session"/> writing into <paramref name="messages"/> (its live list, on
    /// screen or not), working in <paramref name="folder"/>. Enters the run's scope for the calling flow.</summary>
    private TaskCompletionSource BeginTurnFor(ChatSession session, ObservableCollection<ChatMessage> messages, string? folder)
    {
        SettleRecoveryCards(messages);   // a recovery card not used: a new message took its place
        var run = new ChatRun { Session = session, Messages = messages, FirstRow = messages.Count };
        NoteRunBegan(session);
        ActivityBegan(session);
        CancelLearning(session);   // it is working again: read it when it is quiet
        if (_splitTurnPending == session.Id) { run.StartedBySplit = true; _splitTurnPending = null; }
        // A turn CluadeX starts for another chat's agent (a message_chat, a split's report waking it, a split opened in
        // the background) counts as the agent's: its report goes up and may wake the chat above it.
        if (_agentDrivenNext.Remove(session.Id)) run.StartedBySplit = true;
        // Reports waiting for this chat are in its rows: this turn reads them, no later wake needs to.
        _splitWaiting.Remove(session.Id);
        run.Scope = new AgentRunScope(session.Id, string.IsNullOrWhiteSpace(folder) ? null : folder, run);
        run.Scope.Provider = _providerManager.ActiveProvider;
        run.Scope.ProviderType = _providerManager.ActiveProviderType;
        ApplyChatModel(run, session, messages);
        run.Scope.ToolDrafting = (tool, chars, path) => OnToolDrafting(run, tool, chars, path);
        run.Scope.PromptSized = tokens => OnPromptSized(run, tokens);
        run.Scope.SkillsPicked = pick => OnSkillsPicked(run, pick);
        run.Scope.UserNotesTaken += tags => OnUserNotesTaken(run, tags);
        // The chat's first request in full — the model's view may hold only a compaction summary of it (AgentHandoff).
        run.Scope.TaskRequest = messages.FirstOrDefault(m => m.Role == MessageRole.User && !string.IsNullOrWhiteSpace(m.Content))?.Content;
        foreach (var extra in session.ExtraFolders.ToList())
            if (System.IO.Directory.Exists(extra)) run.Scope.AddExtraFolder(extra);
        run.Changes.SeedBefore(NewestPicture(messages));
        CarryPlanOver(run);
        _runs.Add(run);
        AgentRunScope.Enter(run.Scope);
        // The app-wide key too (code that runs outside the scope — a UI-thread callback — keys by the latest chat).
        _agentToolService.SessionKey = session.Id;
        RaiseRunStateChanged();
        return run.Done;
    }

    private void EndTurn(TaskCompletionSource done)
    {
        var run = _runs.FirstOrDefault(r => ReferenceEquals(r.Done, done));
        List<(string Text, object? Tag)>? leftover = null;
        bool stopped = false, failed = false;
        if (run != null)
        {
            Services.Agents.RunActivity.ClearRun(run.Session.Id);
            PostRunCostSummary(run);
            RememberRunStats(run);
            AddTurnChangesCard(run);
            AddTurnTestsCard(run);
            AddNextStepBox(run);
            MaybeOfferFable(run);
            // A CLI that died mid-question: nobody is listening any more, so its card must not stay open. Not when the
            // app is closing: the card stays open in the saved chat, and the restart offers it again (RunRecovery).
            if (!_appClosing)
                foreach (var open in run.Messages.Where(m => m.Role == MessageRole.Question && !m.QuestionAnswered).ToList())
                    open.CloseQuestion(Loc("chat.ask.cancelled"));
            leftover = run.Scope?.DrainUserNotes();
            // the owner's words typed into this turn are used up with it (a leftover note keeps the turn's decision instead)
            Services.Agents.DesignGate.EndOwnerTurn(run.Session.Id);
            // A step the run left "in progress" keeps its colour but stops turning: nothing works on it any more. The
            // header says the run ended and the clock shows its whole time.
            ClosePlanCard(run);
            _runs.Remove(run);
            DropDraft(run);
            if (run.LiveRow != null) { run.LiveRow.IsLive = false; run.LiveRow = null; }
            // Finished in a chat on screen: no badge. Elsewhere: "done" until the owner opens it. An error or a stop says
            // so (red) until the next turn begins, on screen or not.
            (stopped, failed) = RunBadge.Outcome(run.Messages, run.FirstRow, run.Stopped);
            run.Session.RunStatus = RunBadge.AtEnd(stopped, failed, ReferenceEquals(run.Session, CurrentSession));
            NoteRunEnded(run.Session, run.Session.RunStatus);
            ActivityEnded(run.Session, stopped, failed);
            run.Session.RunStep = "";
            // Its chat is not on screen: the live list is that chat now — store it.
            if (!ReferenceEquals(run.Session, CurrentSession)) SaveRunChat(run);
            if (ReferenceEquals(AgentRunScope.Current, run.Scope))
            {
                AgentRunScope.Enter(null);
                // ...and a Fable chat's model and task budget end with its run: a later request on this flow (a title,
                // a summary) goes out on the app's pick.
                Services.Agents.SubAgentModel.UseChat(null);
                Services.Agents.SubAgentModel.UseTaskBudget(null);
            }
        }
        done.TrySetResult();
        RaiseRunStateChanged();
        if (run != null && ReferenceEquals(run.Session, CurrentSession)) RefreshStickyPlan();   // its rows stop moving too
        if (run != null)
        {
            // Any chat — on screen or not, a split or a pipe task — is read for what it taught once it sits quiet.
            ScheduleLearning(run.Session, run.Messages);
            if (ReferenceEquals(run.Session, CurrentSession)) FlushInstinctCards();
        }
        // Its report to the chat that opened it, and reports / messages it never took (SplitReports).
        if (run != null) SplitTurnEnded(run, stopped, failed, leftover);
        if (run != null && leftover is { Count: > 0 }) HandOnUntakenWatchNotes(run, leftover);
        if (run != null && leftover is { Count: > 0 }) SendLeftoverNotes(run, leftover);
        // A stop/finish elsewhere frees nothing on screen, but the bar text changes.
    }

    private void OnSubAgentEvent(ChatRun? run, CodeAgentService.SubAgentEvent e)
    {
        NoteActivityAgent(run?.Session.Id ?? CurrentSession?.Id, e);
        if (e.WorkflowId != null) return;   // a workflow's agents are on its card
        var list = run?.Messages ?? Messages;
        var map = run?.AgentMapMsg;
        if (map == null || !list.Contains(map))
        {
            if (e.Phase != "start") return;
            map = new ChatMessage { Role = MessageRole.System, AgentNodes = new() };
            if (run != null) run.AgentMapMsg = map;
            list.Add(map);
        }
        var nodes = map.AgentNodes!;
        var node = nodes.FirstOrDefault(x => x.Id == e.Id);
        if (node == null)
        {
            node = new AgentNode { Id = e.Id, Label = e.Label, Kind = e.Kind, Task = e.Task };
            nodes.Add(node);
        }
        if (e.Phase == "start" && e.Control != null) node.Control = e.Control;   // its window can stop it / write to it
        var took = DateTime.Now - node.StartedAt;
        node.Elapsed = took.TotalMinutes >= 1 ? $"{(int)took.TotalMinutes}m {took.Seconds}s" : $"{Math.Max(0, took.Seconds)}s";
        if (e.Steps > 0) node.Steps = e.Steps;
        if (!string.IsNullOrEmpty(e.Model)) node.Model = e.Model;
        if (e.CostUsd > node.CostUsd) node.CostUsd = e.CostUsd;
        if (!string.IsNullOrEmpty(e.Tier)) node.Tier = e.Tier;     // its routing tier (deep once the owner upgraded it)
        if (!string.IsNullOrEmpty(e.Color)) node.Color = e.Color;
        if (Enum.TryParse<AiProviderType>(e.Provider, out var runsOn)) node.Mascot = ChatCharacter.ForProvider(runsOn);
        node.RefreshUpgrade();
        switch (e.Phase)
        {
            case "start": node.Action = Loc("agentmap.starting"); break;
            case "tool":
                // a tool call: the avatar shows that tool; a note from the loop itself (the upgrade) is a step, not a call
                if (!string.IsNullOrEmpty(e.Tool)) { node.ToolCalls++; node.CurrentTool = e.Tool; }
                node.Action = e.Detail; node.BeginStep(e.CallId, e.Detail);
                break;
            // A tool came back: its row in the agent's window gets the result; the card's line keeps what runs now.
            case "result": node.EndStep(e.CallId, e.Ok, e.Result); break;
            case "done": node.State = "done"; node.Action = e.Detail; node.CloseSteps(); break;
            case "failed": node.State = "failed"; node.Action = e.Detail; node.CloseSteps(); break;
            case "stopped":
                node.StoppedByOwner = node.Control?.StopRequested == true && node.Control.ParentCancelled == false;
                node.State = "stopped";
                node.Action = Loc(node.StoppedByOwner ? "agentmap.stoppedByYou" : "agentmap.stopped");
                node.CloseSteps();
                break;
        }
        int running = nodes.Count(x => x.IsRunning), done = nodes.Count(x => x.IsDone);
        map.NotifyAgentMap();
        map.AgentMapHeader = running > 0
            ? LocF("agentmap.headerRunning", nodes.Count, running, done)
            : LocF("agentmap.headerDone", nodes.Count, done);
        // Plain text for the saved chat (a reloaded chat shows this instead of the card).
        map.Content = "🧭 " + map.AgentMapHeader + "\n" + string.Join("\n", nodes.Select(x =>
            $"{(x.IsDone ? "✅" : x.IsFailed ? "⛔" : "🔄")} {x.Label} — {x.Task}"));
    }

    /// <summary>A run opened a project folder with open_project. Its tools already work there (the run's scope moved);
    /// this makes the app agree: the chat records the folder as its project, and when that chat is the one on screen
    /// the app opens the folder too (announced in the chat, the editor stays where it is — the run is watched here).</summary>
    private void OnAgentOpenedProject(AgentRunScope? scope, string path)
    {
        try
        {
            var run = scope?.Owner as ChatRun;
            var session = run?.Session ?? CurrentSession;
            if (session != null) session.ProjectPath = path;
            if (run == null || ReferenceEquals(run.Session, CurrentSession))
            {
                if (!PathEquals(_fileSystemService.AppWorkingDirectory, path))
                    ApplyWorkingDirectory(path, announce: true, showEditor: false);
            }
            RebuildVisibleSessions();
            RaiseRunScopeChanged();   // the run's main folder moved (a background chat's raises nothing else)
            _debugLog?.Info("Chat", $"agent opened project {path}");
        }
        catch (Exception ex)
        {
            _debugLog?.Warn("Chat", "applying a project the agent opened failed", ex);
        }
    }

    /// <summary>Store a run's chat from its live list (it may not be the one on screen).</summary>
    private void SaveRunChat(ChatRun run, bool background = false)
    {
        try
        {
            // Moves up the history only with new rows: this runs on every autosave tick (as AutoSave does).
            bool changed = run.Session.Messages.Count != run.Messages.Count
                           || !ReferenceEquals(run.Session.Messages.LastOrDefault(), run.Messages.LastOrDefault());
            run.Session.Messages = run.Messages.ToList();
            if (run.Session.Title == "New Chat" && !run.Session.TitleIsCustom && run.Session.Messages.Count > 0)
                run.Session.Title = ChatPersistenceService.GenerateTitle(run.Session.Messages);
            if (changed) TouchSession(run.Session);
            if (background) _persistenceService.SaveSessionInBackground(run.Session);
            else _persistenceService.SaveSession(run.Session);
        }
        catch (Exception ex)
        {
            _debugLog?.Warn("Chat", $"saving the chat of a background run failed", ex);
        }
    }

    /// <summary>Every run whose chat is not on screen, stored (autosave tick, app closing).</summary>
    private void SaveBackgroundRuns(bool background = false)
    {
        // Only chats that gained rows since the last tick: each save rewrites the whole chat.
        foreach (var run in _runs.Where(r => !ReferenceEquals(r.Session, CurrentSession) && r.Messages.Count != r.SavedCount).ToList())
        {
            SaveRunChat(run, background);
            run.SavedCount = run.Messages.Count;
        }
    }

    /// <summary>Ctrl+Alt+End (MainWindow.Hotkey): cancel every chat's run at once, without asking — the way out when an
    /// agent is driving the user's mouse and keyboard. Returns how many runs were told to stop.</summary>
    public int StopAllRuns()
    {
        int n = 0;
        foreach (var run in _runs.ToList())
        {
            try { if (run.Cts is { } cts) { cts.Cancel(); n++; } }
            catch (ObjectDisposedException) { }
        }
        // …and every watch: nothing starts a new turn behind the owner's back after they pulled the brake.
        int watches = _watch?.CancelAll(Loc("watch.cancelledByHotkey")) ?? 0;
        if (n > 0 || watches > 0) StatusText = Loc("desktop.hotkeyStopped");
        _debugLog?.Info("Chat", $"Ctrl+Alt+End stopped {n} run(s) and {watches} watch(es)");
        return n;
    }

    /// <summary>Stop the run of <paramref name="sessionId"/> (asks first) and wait for it to unwind. True when
    /// there is none, or it stopped.</summary>
    private async Task<bool> StopRunOfAsync(string sessionId)
    {
        var run = _runs.FirstOrDefault(r => r.Session.Id == sessionId);
        if (run == null) return true;
        if (!Confirm(Loc("chat.busy.stopFirst"), Loc("chat.busy.title"))) return false;
        try { run.Cts?.Cancel(); } catch (ObjectDisposedException) { }
        if (await Task.WhenAny(run.Done.Task, Task.Delay(TimeSpan.FromSeconds(20))) != run.Done.Task)
        {
            StatusText = Loc("chat.busy.stillStopping");
            return false;
        }
        return true;
    }
}
