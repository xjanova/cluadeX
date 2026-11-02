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


    private void InitOrchestration()
    {
        RunActivity.Changed += chat => App.Current?.Dispatcher.BeginInvoke(() => QueueActivity(chat));

        WorkflowGate.Load(_settingsService.DataRoot);
        WorkflowGate.UltraChanged += _ => App.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(IsUltra)));
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CurrentSession)) OnPropertyChanged(nameof(IsUltra)); };

        var svc = WorkflowService.Current ??= new WorkflowService(_settingsService.DataRoot);
        svc.MarkInterrupted();   // a workflow the last app run left "running" is stopped (its card offers Resume)
        _agentService.ConnectWorkflows(svc);
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

}
