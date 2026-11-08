using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Background;
using CluadeX.Services.Tools;

namespace CluadeX.ViewModels;

/// <summary>
/// The Claude-Code-style views over what a chat's agent runs (docs/agent-orchestration.md): the "N running" line under
/// the plan's active step, workflow cards, split-chat chips, notices (a background command or a split chat finished),
/// the next-step box and the Ultra switch. Each view reads a source that already exists — nothing here keeps a second
/// copy of state.
/// </summary>
public partial class ChatViewModel
{
    private readonly HashSet<string> _activityDirty = new(StringComparer.Ordinal);
    private DispatcherTimer? _activityTimer;

    /// <summary>One card per workflow, shared by every row that shows it (the engine's events find it here).</summary>
    private readonly ConcurrentDictionary<string, WorkflowCard> _workflowCards = new(StringComparer.Ordinal);
    /// <summary>Workflows the owner resumed from a card: their end is told to the chat (a tool run reports its own).</summary>
    private readonly ConcurrentDictionary<string, byte> _resumedFromCard = new(StringComparer.Ordinal);
    /// <summary>wrap_up's box per chat, shown when the turn ends.</summary>
    private readonly ConcurrentDictionary<string, NextStep> _pendingNextStep = new(StringComparer.Ordinal);
    /// <summary>Background commands that ended, per chat, merged when several end within 2 s.</summary>
    private readonly Dictionary<string, List<BgSnapshot>> _bgEnded = new(StringComparer.Ordinal);
    private DispatcherTimer? _bgTimer;

    private void InitOrchestration()
    {
        RunActivity.Changed += chat => App.Current?.Dispatcher.BeginInvoke(() => QueueActivity(chat));

        WorkflowGate.Load(_settingsService.DataRoot);
        WorkflowGate.UltraChanged += _ => App.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(IsUltra)));
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CurrentSession)) OnPropertyChanged(nameof(IsUltra)); };

        var svc = WorkflowService.Current ??= new WorkflowService(_settingsService.DataRoot);
        svc.MarkInterrupted();   // a workflow the last app run left "running" is stopped (its card offers Resume)
        _ = Task.Run(svc.SweepLeftWorktreesAsync);   // … and the worktrees its agents were in go (git, off the UI thread)
        _agentService.ConnectWorkflows(svc);
        svc.Started += st => App.Current?.Dispatcher.BeginInvoke(() => OnWorkflowStarted(st));
        svc.RowChanged += (st, _) => { if (_workflowCards.TryGetValue(st.Id, out var card)) card.MarkDirty(); };
        svc.Finished += st => App.Current?.Dispatcher.BeginInvoke(() => OnWorkflowFinished(st));
        // The cards build their words in Flush: a language switch redraws them once.
        LocalizedResources.Instance.PropertyChanged += (_, _) => App.Current?.Dispatcher.BeginInvoke(() =>
        {
            foreach (var card in _workflowCards.Values) { card.MarkDirty(); card.Flush(); }
        });

        ChatMessage.WorkflowCardFor = m => m.ToolOutput is { Length: > 0 } id ? CardFor(id) : null;
        ChatMessage.SplitChipsFor = m => new ObservableCollection<ChatSession>(
            (m.ToolOutput ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(id => Sessions.FirstOrDefault(s => s.Id == id)).OfType<ChatSession>());
        ChatMessage.NextStepFor = m => NextStep.FromJson(m.ToolOutput) is { } step
            ? new NextStepView(step, m.ToolSummary, (_, label) => OnNextStepPicked(m, label))
            : null;

        OrchestrationToolModule.NextStepRequested += (chat, step) =>
        {
            if (chat != null) _pendingNextStep[chat] = step;
            // In the tool's own flow (the run's scope): wrap_up checks off the plan this run worked on.
            _agentToolService.FinishPlanAtWrapUp();
        };

        if (_cleanup?.Background is { } bg)
            bg.Ended += (snap, owner) => App.Current?.Dispatcher.BeginInvoke(() => OnBackgroundEnded(snap, owner));
    }

    // ─── the "N running" line ───

    /// <summary>The line under the active step for <paramref name="chatId"/> ("" when nothing runs).</summary>
    private static string RunningLineFor(string? chatId) => RunActivity.Describe(RunActivity.Of(chatId), Loc);

    // Sub-agents start and end in bursts (a fan-out of six): one redraw per 300 ms at most, of the chats that changed.
    private void QueueActivity(string chat)
    {
        _activityDirty.Add(chat);
        if (_activityTimer == null)
        {
            _activityTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
            _activityTimer.Tick += (_, _) => { _activityTimer!.Stop(); FlushActivity(); };
        }
        if (!_activityTimer.IsEnabled) _activityTimer.Start();
    }

    /// <summary>Apply queued activity now (UI thread). Public for the harness, which cannot wait on a timer.</summary>
    public void FlushActivity()
    {
        var chats = _activityDirty.ToList();
        _activityDirty.Clear();
        foreach (var chat in chats)
        {
            string line = RunningLineFor(chat);
            // In place, on the rows already drawn: the plan card and the bar keep their lists (no redraw, no flash).
            var run = _runs.FirstOrDefault(r => r.Session.Id == chat);
            if (run?.TodoMsg?.PlanItems is { } card)
                foreach (var item in card) item.Running = item.IsActive ? line : "";
            if (CurrentSession?.Id == chat)
                foreach (var item in StickyPlanItems) item.Running = item.IsActive ? line : "";
        }
    }

    // ─── Ultra (the per-chat workflow opt-in) ───

    /// <summary>The composer's Ultra switch, for the chat on screen.</summary>
    public bool IsUltra
    {
        get => WorkflowGate.IsUltra(CurrentSession?.Id);
        set { if (CurrentSession is { } s) WorkflowGate.SetUltra(s.Id, value); OnPropertyChanged(); }
    }

    // ─── workflow cards ───

    private WorkflowCard? CardFor(string id)
    {
        if (_workflowCards.TryGetValue(id, out var card)) return card;
        if (WorkflowService.Current is not { } svc || svc.Get(id) == null) return null;   // its state file is gone: the row's text shows
        card = _workflowCards.GetOrAdd(id, _ => new WorkflowCard(id, svc));
        card.Resumed += c => _resumedFromCard[c.Id] = 0;
        return card;
    }

    /// <summary>The chat's live list: its run's, else the one on screen when it is that chat (null = not open).</summary>
    private ObservableCollection<ChatMessage>? LiveListOf(string? chatId) =>
        chatId == null ? null
        : _runs.FirstOrDefault(r => r.Session.Id == chatId)?.Messages ?? (CurrentSession?.Id == chatId ? Messages : null);

    private void OnWorkflowStarted(WorkflowRunState st)
    {
        if (_workflowCards.TryGetValue(st.Id, out var known)) known.MarkDirty();
        var list = LiveListOf(st.SessionKey);
        // A resume shows on the card the workflow already has in this chat.
        if (list == null || list.Any(m => m.IsWorkflow && m.ToolOutput == st.Id)) return;
        list.Add(new ChatMessage
        {
            Role = MessageRole.System,
            ToolName = ChatMessage.WorkflowTag,
            ToolOutput = st.Id,
            Content = "🧩 " + st.Name,
        });
    }

    private void OnWorkflowFinished(WorkflowRunState st)
    {
        if (_workflowCards.TryGetValue(st.Id, out var card)) { card.MarkDirty(); card.Flush(); }
        var list = LiveListOf(st.SessionKey);
        // The saved chat shows this text where the card cannot be rebuilt (its state file removed).
        if (list?.FirstOrDefault(m => m.IsWorkflow && m.ToolOutput == st.Id) is { } row)
            row.Content = "🧩 " + st.Name + " — " + st.Summary;
        if (_resumedFromCard.TryRemove(st.Id, out _))
            AddNotice(st.SessionKey, "🧩 " + LocF("orch.wf.endedNote", st.Name, st.Summary),
                "Workflow \"" + st.Name + "\" (resumed by the user) ended. " + WorkflowService.Report(st, 4000));
    }

    // ─── notices: rows the owner sees and the model reads on its next step or turn ───

    /// <summary>A notice in <paramref name="chatId"/>: a row in its live list, and — while it runs — a note its agent reads
    /// at the next step. A chat that is neither open nor running gets nothing (nobody is there to read it).</summary>
    private void AddNotice(string? chatId, string shown, string? forModel = null)
    {
        var list = LiveListOf(chatId);
        if (list == null) return;
        list.Add(new ChatMessage { Role = MessageRole.System, ToolName = ChatMessage.NoticeTag, Content = shown });
        _runs.FirstOrDefault(r => r.Session.Id == chatId)?.Scope?.AddUserNote(NoticePrefix + (forModel ?? shown));
    }

    /// <summary>How a notice reads to the model (in its history and as a mid-run note).</summary>
    public const string NoticePrefix = "[CluadeX notice] ";

    private void OnBackgroundEnded(BgSnapshot snap, string? owner)
    {
        // An explicit stop (the agent's bg_stop, the owner's button, the app closing) is not news to whoever stopped it.
        if (owner == null || snap.Status == "stopped") return;
        // A process a watch waits on: the watch says it (and may start a turn) — one message, not two.
        if (Services.Watch.RunWatchService.Current?.WatchesProcess(snap.Id) == true) return;
        if (!_bgEnded.TryGetValue(owner, out var ended)) _bgEnded[owner] = ended = new();
        ended.Add(snap);
        if (_bgTimer == null)
        {
            _bgTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _bgTimer.Tick += (_, _) => { _bgTimer!.Stop(); FlushBackgroundNotices(); };
        }
        if (!_bgTimer.IsEnabled) _bgTimer.Start();
    }

    /// <summary>One notice per chat for the commands that ended in the last 2 s (UI thread; public for the harness).</summary>
    public void FlushBackgroundNotices()
    {
        foreach (var (chat, ended) in _bgEnded.ToList())
        {
            _bgEnded.Remove(chat);
            string Line(BgSnapshot s) => LocF("orch.bg.one", s.Name,
                s.ExitCode is { } code ? LocF("orch.bg.exit", code) : s.Status, WorkflowCard.FormatElapsed(s.Uptime.TotalSeconds));
            string shown = ended.Count == 1 ? Line(ended[0])
                : LocF("orch.bg.many", ended.Count) + "\n" + string.Join("\n", ended.Select(s => "• " + Line(s)));
            string forModel = string.Join("\n", ended.Select(s =>
                $"Background command #{s.Id} \"{s.Name}\" ended ({s.Status}, exit {s.ExitCode?.ToString() ?? "?"}). Log: {s.LogPath}"
                + (string.IsNullOrWhiteSpace(s.LastLine) ? "" : $" Last line: {s.LastLine}")));
            AddNotice(chat, "⏹ " + shown, forModel);
        }
    }

    // ─── split chips ───

    /// <summary>new_chat opened <paramref name="child"/> from <paramref name="parentId"/>: a chip on the asking command's
    /// row (one row per command, made with its first split).</summary>
    private void NoteSplitChip(string parentId, ChatSession child)
    {
        var run = _runs.FirstOrDefault(r => r.Session.Id == parentId);
        if (run == null) return;
        var row = run.SplitsMsg;
        if (row == null || !run.Messages.Contains(row))
        {
            row = new ChatMessage { Role = MessageRole.System, ToolName = ChatMessage.SplitsTag, ToolOutput = "" };
            run.SplitsMsg = row;
            run.Messages.Add(row);
        }
        if ((row.ToolOutput ?? "").Split('\n').Contains(child.Id)) return;
        row.ToolOutput = string.IsNullOrEmpty(row.ToolOutput) ? child.Id : row.ToolOutput + "\n" + child.Id;
        var chips = row.SplitChips;   // built from ToolOutput on first use — the new id is already in it
        if (chips != null && !chips.Contains(child)) chips.Add(child);
        row.Content = "💬 " + string.Join(", ", (chips ?? new()).Select(c => c.Title));
    }

    /// <summary>The gist of a finished split turn for its parent: the last thing it answered, short.</summary>
    private static string GistOf(IEnumerable<ChatMessage>? messages)
    {
        var last = messages?.LastOrDefault(m => m.Role == MessageRole.Assistant && !m.HasError && !string.IsNullOrWhiteSpace(m.Content));
        if (last == null) return "";
        string text = string.Join(" ", last.Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 240 ? text[..240] + "…" : text;
    }

    // ─── the plan a turn carries on from ───

    /// <summary>A new run in a chat whose last plan card still has work left: that plan comes over into the run (each
    /// step keeps its status), so the model can advance it, and its first message says so (CodeAgentService.WithCarriedPlan).
    /// A plan that was finished, or a chat with none, starts empty.</summary>
    private static void CarryPlanOver(ChatRun run)
    {
        var card = run.Messages.LastOrDefault(m => m.IsPlan);
        if (card?.PlanItems is not { Count: > 0 } rows) return;
        var rec = PlanRecord.FromCard(rows, card.PlanScope, card.PlanStopped);
        if (!rec.HasWorkLeft) return;
        run.Scope.Todos.AddRange(rec.Items);
        // Not the old scope tag: what this request is to the chat is the model's to say again (the card keeps its own).
        run.Scope.PlanCarried = true;
        run.CarriedPlanMsg = card;
    }

    // ─── the next-step box ───

    /// <summary>The turn ended: wrap_up's box goes under its closing message (not after a Stop — the owner moved on).</summary>
    private void AddNextStepBox(ChatRun run)
    {
        if (!_pendingNextStep.TryRemove(run.Session.Id, out var step)) return;
        if (run.Stopped) return;
        run.Messages.Add(new ChatMessage
        {
            Role = MessageRole.System,
            ToolName = ChatMessage.NextStepTag,
            ToolOutput = step.ToJson(),
            Content = "⏭ " + step.Recap + (step.Ask is { Length: > 0 } ask ? "\n" + ask : ""),
        });
    }

    /// <summary>A button in the box: its label goes out as the owner's message (into the running reply when one runs),
    /// once; the owner's draft in the box stays. The label is the model's wording, so it never counts as the owner asking
    /// for a workflow.</summary>
    private void OnNextStepPicked(ChatMessage row, string label)
    {
        row.ToolSummary = label;   // saved with the chat: a reopened box shows what was picked, without buttons
        string draft = UserInput ?? "";
        if (CurrentSession is { } s)
        {
            WorkflowGate.NoteOwnerMessage(s.Id, "", append: IsGenerating);
            // nor for a design: the model's label is no design words (a chat with its Design switch on still counts)
            DesignGate.NoteOwnerMessage(s.Id, "", append: IsGenerating);
        }
        if (IsGenerating) SendIntoRunningReply(label);
        else _ = SendMessageCore(label, pipe: null);
        if (draft.Length > 0 && string.IsNullOrEmpty(UserInput)) UserInput = draft;
    }
}
