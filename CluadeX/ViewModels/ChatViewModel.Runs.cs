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
        public CancellationTokenSource? Cts;
        public ChatMessage? ActiveStatus;
        /// <summary>The running balloon: the background-work row the current calls fold into (IsLive while they run).</summary>
        public ChatMessage? LiveRow;
        public ChatMessage? TodoMsg;
        /// <summary>This command's agent map card (made when its first sub-agent starts).</summary>
        public ChatMessage? AgentMapMsg;
        public readonly Stopwatch Stopwatch = new();
        public string StatusBase = "";
        public int StreamingTokenCount;
        /// <summary>Rows in its chat at the last background save (skip a save when nothing was added).</summary>
        public int SavedCount = -1;
        /// <summary>When each running tool started (Stopwatch timestamps, in call order): its row shows how long it took.</summary>
        public readonly System.Collections.Concurrent.ConcurrentQueue<long> ToolStarts = new();
    }

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
            run.Session.RunStep = waiting ? Loc("chat.runs.waitingForYou") : (run.ActiveStatus?.Content ?? "").TrimStart('⏵', ' ');
        }
        if (CurrentSession is { RunStatus: "done" } open) open.RunStatus = "";
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
        _debugLog?.Info("Chat", $"agent opened related folder {path}");
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
                ApplyWorkingDirectory(named[0], announce: false, showEditor: false);
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
        var msg = new ChatMessage
        {
            Role = MessageRole.Question,
            Content = "❓ " + question.Trim(),
            QuestionOptions = options,
            QuestionGirl = Services.GirlArt.PickName(),
            QuestionCallback = answer => tcs.TrySetResult(answer),
        };
        var token = run?.Cts?.Token ?? CancellationToken.None;
        var reg = token.Register(() =>
        {
            if (tcs.TrySetCanceled()) dispatcher.InvokeAsync(() => { msg.CloseQuestion(Loc("chat.ask.cancelled")); RaiseRunStateChanged(); });
        });
        _ = tcs.Task.ContinueWith(_ => { reg.Dispose(); dispatcher.InvokeAsync(RaiseRunStateChanged); }, TaskScheduler.Default);
        dispatcher.InvokeAsync(() =>
        {
            (run?.Messages ?? Messages).Add(msg);
            RaiseRunStateChanged();   // "waiting for you" on the other-chats bar when it is not on screen
            ScrollToBottom?.Invoke();
        });
        return tcs.Task;
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
        var run = new ChatRun { Session = session, Messages = Messages };
        string? taskFolder = TaskFolderFor(session);
        string? folder = taskFolder ?? _fileSystemService.AppWorkingDirectory;
        if (taskFolder != null)
        {
            Messages.Add(new ChatMessage { Role = MessageRole.System, Content = "📁 " + LocF("chat.resume.inTaskFolder", taskFolder) });
            _debugLog?.Info("Chat", $"turn runs in the chat's own folder {taskFolder} (window: {_fileSystemService.AppWorkingDirectory})");
        }
        run.Scope = new AgentRunScope(session.Id, string.IsNullOrWhiteSpace(folder) ? null : folder, run);
        foreach (var extra in session.ExtraFolders.ToList())
            if (System.IO.Directory.Exists(extra)) run.Scope.AddExtraFolder(extra);
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
        if (run != null)
        {
            PostRunCostSummary(run);
            leftover = run.Scope?.DrainUserNotes();
            _runs.Remove(run);
            if (run.LiveRow != null) { run.LiveRow.IsLive = false; run.LiveRow = null; }
            // Finished in a chat on screen: no badge. Elsewhere: "done" until the owner opens it.
            run.Session.RunStatus = ReferenceEquals(run.Session, CurrentSession) ? "" : "done";
            run.Session.RunStep = "";
            // Its chat is not on screen: the live list is that chat now — store it.
            if (!ReferenceEquals(run.Session, CurrentSession)) SaveRunChat(run);
            if (ReferenceEquals(AgentRunScope.Current, run.Scope)) AgentRunScope.Enter(null);
        }
        done.TrySetResult();
        RaiseRunStateChanged();
        if (run != null && leftover is { Count: > 0 }) SendLeftoverNotes(run, leftover);
        // A stop/finish elsewhere frees nothing on screen, but the bar text changes.
    }

    private void OnSubAgentEvent(ChatRun? run, CodeAgentService.SubAgentEvent e)
    {
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
        var took = DateTime.Now - node.StartedAt;
        node.Elapsed = took.TotalMinutes >= 1 ? $"{(int)took.TotalMinutes}m {took.Seconds}s" : $"{Math.Max(0, took.Seconds)}s";
        if (e.Steps > 0) node.Steps = e.Steps;
        switch (e.Phase)
        {
            case "start": node.Action = Loc("agentmap.starting"); break;
            case "tool": node.ToolCalls++; node.Action = e.Detail; break;
            case "done": node.State = "done"; node.Action = e.Detail; break;
            case "failed": node.State = "failed"; node.Action = e.Detail; break;
            case "stopped": node.State = "stopped"; node.Action = Loc("agentmap.stopped"); break;
        }
        int running = nodes.Count(x => x.IsRunning), done = nodes.Count(x => x.IsDone);
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
            _debugLog?.Info("Chat", $"agent opened project {path}");
        }
        catch (Exception ex)
        {
            _debugLog?.Warn("Chat", "applying a project the agent opened failed", ex);
        }
    }

    /// <summary>Store a run's chat from its live list (it may not be the one on screen).</summary>
    private void SaveRunChat(ChatRun run)
    {
        try
        {
            run.Session.Messages = run.Messages.ToList();
            if (run.Session.Title == "New Chat" && run.Session.Messages.Count > 0)
                run.Session.Title = ChatPersistenceService.GenerateTitle(run.Session.Messages);
            run.Session.UpdatedAt = DateTime.Now;
            _persistenceService.SaveSession(run.Session);
        }
        catch (Exception ex)
        {
            _debugLog?.Warn("Chat", $"saving the chat of a background run failed", ex);
        }
    }

    /// <summary>Every run whose chat is not on screen, stored (autosave tick, app closing).</summary>
    private void SaveBackgroundRuns()
    {
        // Only chats that gained rows since the last tick: each save rewrites the whole chat on the UI thread.
        foreach (var run in _runs.Where(r => !ReferenceEquals(r.Session, CurrentSession) && r.Messages.Count != r.SavedCount).ToList())
        {
            SaveRunChat(run);
            run.SavedCount = run.Messages.Count;
        }
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
