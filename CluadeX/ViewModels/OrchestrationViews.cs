using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using CluadeX.Services;
using CluadeX.Services.Agents;

namespace CluadeX.ViewModels;

/// <summary>
/// The workflow card in the chat (docs/agent-orchestration.md §3): name, agent count, elapsed, one dot per agent; expands to
/// phase → agent rows; a row opens its transcript; Stop and Resume. It reads <see cref="WorkflowRunState"/> — the engine's
/// own state — and keeps no copy. Cheap: engine events only mark it dirty (any thread); one timer applies the change at
/// most four times a second, and only while the card is on screen (<see cref="SetVisible"/>). Rows are updated in place,
/// so nothing redraws that did not change.
/// </summary>
public sealed class WorkflowCard : ViewModelBase
{
    public string Id { get; }
    private readonly WorkflowService _svc;
    private readonly Dispatcher? _ui;
    private DispatcherTimer? _timer;
    private volatile bool _dirty = true;
    private bool _visible, _busy;

    /// <summary>Every agent row in run order — the dots and the phase lists share these objects.</summary>
    public ObservableCollection<WorkflowRowView> Rows { get; } = new();
    public ObservableCollection<WorkflowPhaseView> Phases { get; } = new();

    public WorkflowCard(string id, WorkflowService svc)
    {
        Id = id;
        _svc = svc;
        _ui = System.Windows.Application.Current?.Dispatcher;
        Flush();
    }

    private static string Loc(string key) => LocalizedResources.Instance[key];
    private static string LocF(string key, params object[] a) => string.Format(Loc(key), a);

    private string _name = "", _status = "", _statusKey = "", _elapsed = "", _agents = "", _spent = "", _estimate = "", _verdicts = "", _note = "";
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    /// <summary>running · done · stopped · failed (the dot colours and the buttons follow it).</summary>
    public string StatusKey { get => _statusKey; private set { if (SetProperty(ref _statusKey, value)) { OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(CanResume)); } } }
    public string StatusText { get => _status; private set => SetProperty(ref _status, value); }
    public string Elapsed { get => _elapsed; private set => SetProperty(ref _elapsed, value); }
    public string AgentsText { get => _agents; private set => SetProperty(ref _agents, value); }
    public string SpentText { get => _spent; private set => SetProperty(ref _spent, value); }
    public string EstimateText { get => _estimate; private set => SetProperty(ref _estimate, value); }
    public string VerdictsText { get => _verdicts; private set { if (SetProperty(ref _verdicts, value)) OnPropertyChanged(nameof(HasVerdicts)); } }
    public bool HasVerdicts => _verdicts.Length > 0;
    private string _merge = "";
    /// <summary>"merge: 2 merged · 1 left out · tests on the merged result ✓" (empty without a merge step).</summary>
    public string MergeText { get => _merge; private set { if (SetProperty(ref _merge, value)) OnPropertyChanged(nameof(HasMerge)); } }
    public bool HasMerge => _merge.Length > 0;
    public string Note { get => _note; private set { if (SetProperty(ref _note, value)) OnPropertyChanged(nameof(HasNote)); } }
    public bool HasNote => _note.Length > 0;
    public bool IsRunning => _statusKey == "running";
    public bool CanResume => !_busy && _statusKey is "stopped" or "failed";

    private bool _expanded;
    public bool IsExpanded { get => _expanded; set => SetProperty(ref _expanded, value); }
    public ICommand ToggleCommand => _toggle ??= new RelayCommand(() => IsExpanded = !IsExpanded);
    private ICommand? _toggle;

    /// <summary>Stop every agent. A second click finds nothing running and does nothing.</summary>
    public ICommand StopCommand => _stop ??= new RelayCommand(() => _svc.Stop(Id));
    private ICommand? _stop;

    /// <summary>Resume from the cache. Disabled at once, so a double click starts it once.</summary>
    public ICommand ResumeCommand => _resume ??= new RelayCommand(() =>
    {
        if (!CanResume || _svc.ResumeRefusal(Id) != null) return;
        _busy = true;
        OnPropertyChanged(nameof(CanResume));
        Resumed?.Invoke(this);
        _ = Task.Run(async () =>
        {
            try { await _svc.ResumeAsync(Id, CancellationToken.None); }
            finally { _ui?.BeginInvoke(() => { _busy = false; MarkDirty(); Flush(); }); }
        });
    });
    private ICommand? _resume;

    /// <summary>Raised when the owner pressed Resume (the chat page notes the result when it ends).</summary>
    public event Action<WorkflowCard>? Resumed;

    /// <summary>The engine changed something (any thread).</summary>
    public void MarkDirty()
    {
        _dirty = true;
        if (_ui != null && _visible) _ui.BeginInvoke(EnsureTimer);
    }

    /// <summary>The card came on screen or left it (its view's Loaded / Unloaded / IsVisibleChanged).</summary>
    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (visible) { Flush(); EnsureTimer(); }
        else _timer?.Stop();
    }

    private void EnsureTimer()
    {
        if (!_visible) return;
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => Tick();
        }
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void Tick()
    {
        Flush();
        // Nothing moves on a finished card: the timer stops until the engine says something changed.
        if (!IsRunning && !_dirty) _timer?.Stop();
    }

    /// <summary>Apply the state now (UI thread). Elapsed always; rows only when the engine marked a change.</summary>
    public void Flush()
    {
        var st = _svc.Get(Id);
        if (st == null) return;
        Elapsed = FormatElapsed(st.ElapsedSeconds);
        if (!_dirty) return;
        _dirty = false;
        List<WorkflowAgentState> agents;
        int kept, killed, merged, leftOut;
        List<bool?> tests;
        lock (st.Gate)
        {
            agents = st.Agents.ToList();
            kept = st.Verdicts.Count(v => !v.Killed);
            killed = st.Verdicts.Count(v => v.Killed);
            merged = st.Merges.Count(m => m.Outcome == "merged");
            leftOut = st.Merges.Count(m => m.Outcome is "conflict" or "red-after-merge" or "gate-red" or "not-run");
            tests = st.Tests.Select(t => t.Green).ToList();
            Name = st.Name;
            StatusKey = st.Status;
            EstimateText = st.EstimateAgents > 0 ? LocF("orch.wf.estimate", st.EstimateAgents, Math.Max(1, (int)(st.EstimateRamMb / AgentLoad.RamPerAgentMb)),
                st.EstimateUsd, st.EstimateRamMb / 1024.0) : "";
        }
        StatusText = Loc("orch.wf.status." + (StatusKey is "running" or "done" or "stopped" or "failed" ? StatusKey : "running"));
        Note = StatusKey == "stopped" && st.StopReason == "app closed" ? Loc("orch.wf.appClosed") : "";
        AgentsText = LocF("orch.wf.agents", agents.Count);
        SpentText = LocF("orch.wf.spent", agents.Sum(a => a.CostUsd));
        VerdictsText = kept + killed > 0 ? LocF("orch.wf.verdicts", kept, killed) : "";
        MergeText = merged + leftOut + tests.Count == 0 ? ""
            : string.Join(" · ", new[] { merged + leftOut > 0 ? LocF("orch.wf.merge", merged, leftOut) : "" }
                .Concat(tests.Where(t => t != null).Select(t => Loc(t == true ? "orch.wf.tests.green" : "orch.wf.tests.red")))
                .Where(s => s.Length > 0));

        // A resume starts the run's rows again: start the view again too (once, not per row).
        if (agents.Count < Rows.Count || agents.Where((a, i) => i < Rows.Count && Rows[i].Key != a.Key).Any())
        {
            Rows.Clear();
            Phases.Clear();
        }
        for (int i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (i < Rows.Count) { Rows[i].Apply(a); continue; }
            var row = new WorkflowRowView(a.Key);
            row.Apply(a);
            Rows.Add(row);
            var phase = Phases.FirstOrDefault(p => p.Title == a.Phase);
            if (phase == null) Phases.Add(phase = new WorkflowPhaseView(a.Phase));
            phase.Rows.Add(row);
        }
        foreach (var p in Phases) p.Refresh();
    }

    public static string FormatElapsed(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s" : $"{t.Seconds}s";
    }
}

public sealed class WorkflowPhaseView : ViewModelBase
{
    public string Title { get; }
    public ObservableCollection<WorkflowRowView> Rows { get; } = new();
    public WorkflowPhaseView(string title) => Title = title;

    private string _count = "";
    /// <summary>"3/5" — finished of all.</summary>
    public string CountText { get => _count; private set => SetProperty(ref _count, value); }
    public void Refresh() => CountText = $"{Rows.Count(r => r.IsFinished)}/{Rows.Count}";
}

/// <summary>One agent on the card: its dot, its row, its transcript.</summary>
public sealed class WorkflowRowView : ViewModelBase
{
    public string Key { get; }
    public WorkflowRowView(string key) => Key = key;

    private string _label = "", _state = "", _stateText = "", _spent = "", _prompt = "", _answer = "", _error = "";
    public string Label { get => _label; private set => SetProperty(ref _label, value); }
    /// <summary>queued · running · done · failed · stopped · skipped · cached.</summary>
    public string State { get => _state; private set { if (SetProperty(ref _state, value)) OnPropertyChanged(nameof(IsFinished)); } }
    public string StateText { get => _stateText; private set => SetProperty(ref _stateText, value); }
    public string Spent { get => _spent; private set => SetProperty(ref _spent, value); }
    public string Prompt { get => _prompt; private set => SetProperty(ref _prompt, value); }
    public string Answer { get => _answer; private set { if (SetProperty(ref _answer, value)) OnPropertyChanged(nameof(HasAnswer)); } }
    public bool HasAnswer => _answer.Length > 0;
    public string Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;
    public bool IsFinished => _state is not ("queued" or "running" or "checking");

    private bool _open;
    public bool IsOpen { get => _open; set => SetProperty(ref _open, value); }
    public ICommand ToggleCommand => _toggle ??= new RelayCommand(() => IsOpen = !IsOpen);
    private ICommand? _toggle;

    internal void Apply(WorkflowAgentState a)
    {
        Label = a.Label;
        State = a.State;
        StateText = LocalizedResources.Instance["orch.wf.row." + a.State]
                    + (a.Gate is "green" or "red" ? " · " + LocalizedResources.Instance["orch.wf.gate." + a.Gate] : "");
        Spent = a.CostUsd > 0 ? $"${a.CostUsd:0.000}" : "";
        Prompt = a.Prompt.Length > 4000 ? a.Prompt[..4000] + "…" : a.Prompt;
        Answer = a.Output is { } o ? (o.Length > 8000 ? o[..8000] + "…" : o) : "";
        Error = a.Error ?? "";
    }
}

/// <summary>The next-step box (wrap_up): recap, at most one question, one or two buttons. A button's label goes back as
/// the owner's message once; the box then says what was picked.</summary>
public sealed class NextStepView : ViewModelBase
{
    public string Recap { get; }
    public string? Ask { get; }
    public bool HasAsk => !string.IsNullOrEmpty(Ask);
    public IReadOnlyList<NextStepAction> Actions { get; }
    public bool HasActions => Actions.Count > 0 && !IsAnswered;

    private readonly Action<NextStepView, string>? _send;

    /// <summary>The box's heading: "Next step" for the model's wrap_up; CluadeX's own boxes (the recovery card after a
    /// restart) say what they are.</summary>
    public string Title { get; init; } = LocalizedResources.Instance["orch.next.title"];

    public NextStepView(NextStep step, string? picked, Action<NextStepView, string>? send)
    {
        Recap = step.Recap;
        Ask = step.Ask;
        _send = send;
        Actions = step.Actions.Select((l, i) => new NextStepAction(l, i == 0, this)).ToList();
        _picked = picked ?? "";
    }

    private string _picked;
    /// <summary>The label the owner clicked ("" = not yet).</summary>
    public string Picked
    {
        get => _picked;
        private set
        {
            if (!SetProperty(ref _picked, value)) return;
            OnPropertyChanged(nameof(IsAnswered)); OnPropertyChanged(nameof(HasActions)); OnPropertyChanged(nameof(PickedText));
        }
    }
    public bool IsAnswered => _picked.Length > 0;
    public string PickedText => _picked.Length > 0 ? string.Format(LocalizedResources.Instance["orch.next.picked"], _picked) : "";

    /// <summary>Answered some other way (the recovery card when its question card is answered, or a new message took
    /// its place): the buttons go, nothing is sent.</summary>
    internal void MarkPicked(string label)
    {
        if (!IsAnswered && label.Length > 0) Picked = label;
    }

    /// <summary>A click: once (a double click, or the other button after it, does nothing).</summary>
    internal void Choose(string label)
    {
        if (IsAnswered) return;
        Picked = label;
        _send?.Invoke(this, label);
    }
}

public sealed class NextStepAction
{
    public string Label { get; }
    public bool IsPrimary { get; }
    public ICommand Command { get; }
    public NextStepAction(string label, bool primary, NextStepView box)
    {
        Label = label;
        IsPrimary = primary;
        Command = new RelayCommand(() => box.Choose(label));
    }
}
