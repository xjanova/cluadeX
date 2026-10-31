using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// One chat's plan in its own window (owner 2026-10-16: "โชว์แผนที่เราวางไว้เป็นหน้าต่างอีกอัน ให้เห็นว่าอยู่ตรงไหน
/// สถานะ เพิ่มแผนได้ขณะพิมพ์ ทำหน้าที่คล้ายไทม์แมชชีน เลือกกลับไปจุดนั้น หรือแยกงานตรงนั้นได้ เฉพาะแชทนั้น"):
/// the agent's todo list live, items the owner adds (sent to the running agent, or queued for the next turn), and
/// that chat's restore points — each one can be gone back to, or branched into a new chat on a git worktree.
/// </summary>
public sealed class PlanWindowViewModel : ViewModelBase
{
    public sealed record Row(int Number, string Text, string State)
    {
        public bool IsDone => State == "done";
        public bool IsActive => State == "active";
        public bool IsPending => State == "pending";
        public bool IsQueued => State == "queued";
    }

    private readonly ChatViewModel _chat;
    private readonly CheckpointService? _checkpoints;
    private readonly DispatcherTimer _timer;
    private readonly List<string> _queued = new();
    private string _lastSignature = "";

    public string SessionId { get; }
    public ObservableCollection<Row> Rows { get; } = new();
    public ObservableCollection<CheckpointService.Point> Steps { get; } = new();

    private string _title = "";
    public string Title { get => _title; private set => SetProperty(ref _title, value); }

    private string _project = "";
    public string Project { get => _project; private set => SetProperty(ref _project, value); }

    private string _status = "";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (SetProperty(ref _isRunning, value)) { OnPropertyChanged(nameof(CanStartQueued)); OnPropertyChanged(nameof(CanTravel)); } }
    }
    /// <summary>Going back or branching changes files: only while this chat is not working on them.</summary>
    public bool CanTravel => !_isRunning;
    public bool CanStartQueued => !_isRunning && _queued.Count > 0;
    public bool HasRows => Rows.Count > 0;
    public bool HasSteps => Steps.Count > 0;

    private string _newItem = "";
    public string NewItem { get => _newItem; set => SetProperty(ref _newItem, value); }

    private string _message = "";
    public string Message { get => _message; set => SetProperty(ref _message, value); }

    // ─── Confirmation (going back and branching both change files) ───
    private CheckpointService.Point? _pending;
    private bool _pendingIsBranch;
    public CheckpointService.Point? Pending
    {
        get => _pending;
        private set { if (SetProperty(ref _pending, value)) OnPropertyChanged(nameof(IsConfirmOpen)); }
    }
    public bool IsConfirmOpen => _pending != null;
    private string _confirmTitle = "", _confirmBody = "";
    public string ConfirmTitle { get => _confirmTitle; private set => SetProperty(ref _confirmTitle, value); }
    public string ConfirmBody { get => _confirmBody; private set => SetProperty(ref _confirmBody, value); }

    public ICommand AddItemCommand { get; }
    public ICommand StartQueuedCommand { get; }
    public ICommand AskRewindCommand { get; }
    public ICommand AskBranchCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    private static string Loc(string key) => LocalizedResources.Instance[key];

    public PlanWindowViewModel(ChatViewModel chat, string sessionId, CheckpointService? checkpoints)
    {
        _chat = chat;
        _checkpoints = checkpoints;
        SessionId = sessionId;
        AddItemCommand = new RelayCommand(AddItem);
        StartQueuedCommand = new AsyncRelayCommand(StartQueuedAsync);
        AskRewindCommand = new RelayCommand<CheckpointService.Point>(p => Ask(p, branch: false));
        AskBranchCommand = new RelayCommand<CheckpointService.Point>(p => Ask(p, branch: true));
        CancelCommand = new RelayCommand(() => Pending = null);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => Refresh();
        _chat.PlanChanged += OnPlanChanged;
        Refresh();
        _timer.Start();
    }

    private void OnPlanChanged(string sessionId) { if (sessionId == SessionId) Refresh(); }

    public void Detach()
    {
        _timer.Stop();
        _chat.PlanChanged -= OnPlanChanged;
    }

    /// <summary>Re-read the plan and restore points; rebuilds the lists only when something changed (no flicker).</summary>
    public void Refresh()
    {
        var snap = _chat.PlanSnapshot(SessionId);
        Title = snap.Title;
        Project = snap.Project;
        IsRunning = snap.Running;

        var rows = snap.Items.Select((t, i) => new Row(i + 1, t.Content, t.Status?.ToLowerInvariant() switch
        {
            "completed" or "done" => "done",
            "in_progress" or "active" => "active",
            _ => "pending",
        })).ToList();
        foreach (var q in _queued) rows.Add(new Row(rows.Count + 1, q, "queued"));

        var steps = _checkpoints != null && snap.Project.Length > 0
            ? _checkpoints.PointsForSession(snap.Project, SessionId) : new List<CheckpointService.Point>();

        int done = rows.Count(r => r.IsDone);
        var active = rows.FirstOrDefault(r => r.IsActive);
        // The step the plan is on; the agent's own step count only when it has no plan.
        int onStep = (active ?? rows.FirstOrDefault(r => r.IsPending))?.Number ?? snap.Step;
        Status = snap.Running
            ? string.Format(Loc("plan.status.running"), onStep, done, rows.Count(r => !r.IsQueued)) + (active != null ? " — " + active.Text : "")
            : rows.Count == 0 ? Loc("plan.status.empty") : string.Format(Loc("plan.status.idle"), done, rows.Count(r => !r.IsQueued));

        string sig = string.Join("|", rows.Select(r => r.State + r.Text)) + "#" + string.Join("|", steps.Select(s => s.Id + s.Files.Count));
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        Steps.Clear();
        foreach (var s in steps.AsEnumerable().Reverse()) Steps.Add(s);
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(CanStartQueued));
    }

    private void AddItem()
    {
        string text = (NewItem ?? "").Trim();
        if (text.Length == 0) return;
        NewItem = "";
        if (_chat.AddPlanItem(SessionId, text))
            Message = Loc("plan.added.running");
        else
        {
            _queued.Add(text);
            Message = Loc("plan.added.queued");
        }
        _lastSignature = "";
        Refresh();
    }

    private async Task StartQueuedAsync()
    {
        if (_queued.Count == 0 || _isRunning) return;
        var items = _queued.ToList();
        _queued.Clear();
        _lastSignature = "";
        await _chat.StartPlanItemsAsync(SessionId, items);
        Refresh();
    }

    private void Ask(CheckpointService.Point? p, bool branch)
    {
        if (p == null) return;
        if (_isRunning) { Message = Loc("plan.travel.busy"); return; }
        _pendingIsBranch = branch;
        ConfirmTitle = Loc(branch ? "plan.branch.title" : "plan.rewind.title");
        ConfirmBody = string.Format(Loc(branch ? "plan.branch.body" : "plan.rewind.body"), p.Step, p.When, p.FileList);
        Pending = p;
    }

    private async Task ConfirmAsync()
    {
        var p = Pending;
        Pending = null;
        if (p == null || _checkpoints == null) return;
        if (_chat.PlanSnapshot(SessionId).Running) { Message = Loc("plan.travel.busy"); return; }
        if (_pendingIsBranch)
        {
            Message = Loc("plan.branch.working");
            Message = await _chat.BranchFromPointAsync(SessionId, p);
        }
        else
        {
            var r = await Task.Run(() => _checkpoints.RestoreTo(p.Project, p.Id));
            Message = r.Ok ? string.Format(Loc("tm.pointRestored"), r.Files) : string.Format(Loc("tm.pointFailed"), r.Message);
            _chat.NotePlanRewind(SessionId, p, r.Ok, r.Files);
        }
        _lastSignature = "";
        Refresh();
    }
}
