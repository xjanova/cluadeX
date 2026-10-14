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
        public ChatMessage? TodoMsg;
        /// <summary>This command's agent map card (made when its first sub-agent starts).</summary>
        public ChatMessage? AgentMapMsg;
        public readonly Stopwatch Stopwatch = new();
        public string StatusBase = "";
        public int StreamingTokenCount;
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
        string? folder = _fileSystemService.AppWorkingDirectory;
        run.Scope = new AgentRunScope(session.Id, string.IsNullOrWhiteSpace(folder) ? null : folder, run);
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
        foreach (var run in _runs.Where(r => !ReferenceEquals(r.Session, CurrentSession)).ToList())
            SaveRunChat(run);
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
