using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;

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

    private void InitOrchestration()
    {
        RunActivity.Changed += chat => App.Current?.Dispatcher.BeginInvoke(() => QueueActivity(chat));

        WorkflowGate.Load(_settingsService.DataRoot);
        WorkflowGate.UltraChanged += _ => App.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(IsUltra)));
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CurrentSession)) OnPropertyChanged(nameof(IsUltra)); };

        var svc = WorkflowService.Current ??= new WorkflowService(_settingsService.DataRoot);
        svc.MarkInterrupted();   // a workflow the last app run left "running" is stopped (its card offers Resume)
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

}
