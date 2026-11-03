using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// The plan window (<see cref="PlanWindowViewModel"/>): one per chat, reading that chat's plan whether or not it is on
/// screen, sending items the owner adds to its running agent, and going back to / branching from its restore points.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>The last plan each chat's agent wrote (a finished run's plan stays readable). UI thread only.</summary>
    private readonly Dictionary<string, List<TodoItem>> _lastPlans = new();
    /// <summary>Who last wrote each chat's plan. Not the chat's model now: a provider switched after the plan was written
    /// must not repaint it as the new model's work (owner 2026-10-31: the bar has the colour of the model writing it).</summary>
    private readonly Dictionary<string, ChatCharacter> _planAuthors = new();
    private readonly Dictionary<string, Views.PlanWindow> _planWindows = new();

    /// <summary>A chat's plan changed (session id). Raised on the UI thread.</summary>
    public event Action<string>? PlanChanged;

    public ICommand OpenPlanWindowCommand => _openPlanWindowCommand ??= new RelayCommand(() => OpenPlanWindow(CurrentSession));
    private ICommand? _openPlanWindowCommand;

    private void RememberPlan(ChatRun? run, List<TodoItem> items)
    {
        string? id = run?.Session.Id ?? CurrentSession?.Id;
        if (id == null) return;
        _lastPlans[id] = items.Select(t => new TodoItem { Content = t.Content, Status = t.Status, CreatedAt = t.CreatedAt }).ToList();
        _planAuthors[id] = CharacterForBox(run);
        PlanChanged?.Invoke(id);
        if (id == CurrentSession?.Id) RefreshStickyPlan();
    }

    // ─── The plan bar pinned to the top of the chat (owner 2026-10-16: "ลอยค้างอยู่ด้านบนสุดของแชท … ถ้ามีการแตกลาย
    //     ไปไหนก็กดที่แผนวาปไปมาระหว่างแชทได้หมด") ───

    /// <summary>A chat this one branched from, or one branched from it — a chip on the plan bar that opens it.</summary>
    public sealed record BranchLink(string SessionId, string Label, string Title, bool IsParent);

    private sealed record BranchRecord(string Child, string Parent, int Step, DateTime When);
    private List<BranchRecord>? _branches;

    private string BranchesFile => Path.Combine(_settingsService.DataRoot, "chat-branches.json");

    private List<BranchRecord> Branches()
    {
        if (_branches != null) return _branches;
        try
        {
            _branches = File.Exists(BranchesFile)
                ? System.Text.Json.JsonSerializer.Deserialize<List<BranchRecord>>(File.ReadAllText(BranchesFile)) ?? new()
                : new();
        }
        catch (Exception ex) { _debugLog?.Warn("Plan", "chat-branches.json unreadable — starting empty", ex); _branches = new(); }
        return _branches;
    }

    private void RecordBranch(string child, string parent, int step)
    {
        var list = Branches();
        list.Add(new BranchRecord(child, parent, step, DateTime.Now));
        try { File.WriteAllText(BranchesFile, System.Text.Json.JsonSerializer.Serialize(list)); }
        catch (Exception ex) { _debugLog?.Warn("Plan", "could not save chat-branches.json", ex); }
    }

    private IReadOnlyList<PlanItemView> _stickyPlanItems = Array.Empty<PlanItemView>();
    public IReadOnlyList<PlanItemView> StickyPlanItems { get => _stickyPlanItems; private set => SetProperty(ref _stickyPlanItems, value); }

    private IReadOnlyList<BranchLink> _stickyBranches = Array.Empty<BranchLink>();
    public IReadOnlyList<BranchLink> StickyBranches { get => _stickyBranches; private set => SetProperty(ref _stickyBranches, value); }

    private string _stickyPlanHeader = "", _stickyPlanNow = "";
    /// <summary>"แผนงาน 2/5": on the second of five.</summary>
    public string StickyPlanHeader { get => _stickyPlanHeader; private set => SetProperty(ref _stickyPlanHeader, value); }
    /// <summary>The item in progress (or the next one).</summary>
    public string StickyPlanNow { get => _stickyPlanNow; private set => SetProperty(ref _stickyPlanNow, value); }

    private double _stickyPlanProgress;
    /// <summary>0..1, for the bar's progress line.</summary>
    public double StickyPlanProgress { get => _stickyPlanProgress; private set => SetProperty(ref _stickyPlanProgress, value); }

    /// <summary>The model writing the plan on screen, whose colour and name the bar wears; the chat's model until one has.</summary>
    public ChatCharacter StickyPlanCharacter =>
        CurrentSession != null && _planAuthors.TryGetValue(CurrentSession.Id, out var who) ? who : ActiveCharacter;

    /// <summary>Shown while the plan has work left and the owner has not closed it: it goes by itself once every item is
    /// done, and ✕ closes it mid-way until the agent writes a different plan (owner 2026-10-17).</summary>
    public bool HasStickyPlan => _stickyPlanItems.Count > 0 && !_stickyPlanItems.All(p => p.IsDone) && _stickyPlanSignature != _stickyPlanDismissed;
    private string _stickyPlanSignature = "", _stickyPlanDismissed = "";

    public ICommand DismissStickyPlanCommand => _dismissStickyPlanCommand ??= new RelayCommand(() =>
    {
        _stickyPlanDismissed = _stickyPlanSignature;
        RaiseFloatingPlan();
    });
    private ICommand? _dismissStickyPlanCommand;
    public bool HasStickyBranches => _stickyBranches.Count > 0;
    /// <summary>The bar over the chat: the floating plan card while it shows, and the branch chips (always).</summary>
    public bool HasStickyBar => IsFloatingPlanShown || HasStickyBranches;

    // ─── The floating plan card (owner 2026-11-03: "เมื่อมีการสร้างแผนงาน ให้ลอย สักครู่แล้วหายไป และถ้าแชทสกรอจนแผนงาน
    //     ในแชทหลุดจากจอ การ์ดแผนงานลอยจะแสดงขึ้น"): a new plan floats for a few seconds, then the card shows only
    //     while the plan card in the chat is off screen. A step moving on is not a new plan and does not bring it back. ───

    /// <summary>How long a new plan floats before it goes (it stays while the mouse is over it).</summary>
    public static readonly TimeSpan PlanIntroLength = TimeSpan.FromSeconds(4);
    /// <summary>After the mouse leaves a card still in its intro, it goes this long later.</summary>
    public static readonly TimeSpan PlanIntroAfterHover = TimeSpan.FromSeconds(1.5);

    /// <summary>The rule, pure: no plan (none, all done, or closed) → no card; a new plan floats until its intro time is
    /// up; after that the card shows only while the chat's own plan card is off screen.</summary>
    public static bool FloatingPlanShows(bool hasPlan, bool inlineCardVisible, bool planIsNew, bool introElapsed)
        => hasPlan && ((planIsNew && !introElapsed) || !inlineCardVisible);

    /// <summary>Is a plan written now new, against the one this chat showed before (step texts)? A different list is;
    /// the same list, or the same list with items added after it (the owner's additions), is not.</summary>
    public static bool IsNewPlan(IReadOnlyList<string>? before, IReadOnlyList<string> now)
    {
        if (now.Count == 0) return false;
        if (before == null || before.Count == 0 || before.Count > now.Count) return true;
        for (int i = 0; i < before.Count; i++)
            if (!string.Equals(before[i], now[i], StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Does the chat's plan card count as on screen? <paramref name="top"/>/<paramref name="bottom"/> are its
    /// edges in the viewport's coordinates (0..<paramref name="viewport"/>). Hysteresis: it has to show a good part of
    /// itself to count as seen, and to be nearly gone to count as gone, so a card at the edge does not flicker.</summary>
    public static bool InlinePlanSeen(bool wasSeen, double top, double bottom, double viewport)
    {
        double height = Math.Max(0, bottom - top);
        if (height <= 0 || viewport <= 0) return false;
        double shown = Math.Max(0, Math.Min(bottom, viewport) - Math.Max(top, 0));
        return wasSeen ? shown >= Math.Min(20, height * 0.25) : shown >= Math.Min(56, height * 0.6);
    }

    /// <summary>The plan words each chat last showed (session id → step texts): a chat switched back to does not replay
    /// the intro of a plan already seen there.</summary>
    private readonly Dictionary<string, List<string>> _planIntroduced = new();
    private string _floatSession = "";
    private bool _planIsNew, _planIntroElapsed = true;
    private System.Windows.Threading.DispatcherTimer? _planIntroTimer;

    private bool _isInlinePlanVisible;
    /// <summary>Set by the chat view: the chat's own plan card is on screen (ChatView.xaml.cs, throttled, with hysteresis).</summary>
    public bool IsInlinePlanVisible
    {
        get => _isInlinePlanVisible;
        set { if (SetProperty(ref _isInlinePlanVisible, value)) RaiseFloatingPlan(); }
    }

    private bool _isFloatingPlanHovered;
    /// <summary>Set by the chat view: the mouse is over the floating card — a new plan stays while it is.</summary>
    public bool IsFloatingPlanHovered
    {
        get => _isFloatingPlanHovered;
        set
        {
            if (!SetProperty(ref _isFloatingPlanHovered, value) || _planIntroElapsed) return;
            if (value) _planIntroTimer?.Stop();
            else StartPlanIntroTimer(PlanIntroAfterHover);
        }
    }

    /// <summary>A new plan's intro is on screen (its few seconds, or held by the mouse).</summary>
    public bool IsPlanIntroOn => _planIsNew && !_planIntroElapsed;

    public bool IsFloatingPlanShown => FloatingPlanShows(HasStickyPlan, _isInlinePlanVisible, _planIsNew, _planIntroElapsed);

    /// <summary>The bar's plan part: with branch chips the bar stays and the plan part follows the floating rule; alone,
    /// the whole bar comes and goes (the view fades it), so the plan part stays drawn while it fades.</summary>
    public bool StickyBarShowsPlan => HasStickyPlan && (IsFloatingPlanShown || !HasStickyBranches);

    private void RaiseFloatingPlan()
    {
        OnPropertyChanged(nameof(HasStickyPlan));
        OnPropertyChanged(nameof(IsPlanIntroOn));
        OnPropertyChanged(nameof(IsFloatingPlanShown));
        OnPropertyChanged(nameof(StickyBarShowsPlan));
        OnPropertyChanged(nameof(HasStickyBar));
    }

    /// <summary>The plan on screen was rebuilt: a different chat resets the card's state; a new plan starts its intro.</summary>
    private void TrackFloatingPlan(string? sessionId, IReadOnlyList<PlanItemView> items)
    {
        if ((sessionId ?? "") != _floatSession)
        {
            _floatSession = sessionId ?? "";
            EndPlanIntro();
            _isFloatingPlanHovered = false;
            // The view measures the new chat's card after layout; until then it counts as on screen, so a chat opened
            // with its plan card in view does not flash the floating one first.
            _isInlinePlanVisible = true;
            OnPropertyChanged(nameof(IsInlinePlanVisible));
        }
        if (sessionId == null) return;
        var words = items.Select(p => p.Text).ToList();
        _planIntroduced.TryGetValue(sessionId, out var before);
        if (words.Count > 0) _planIntroduced[sessionId] = words;
        if (!IsNewPlan(before, words)) return;
        _planIsNew = true;
        _planIntroElapsed = false;
        if (!_isFloatingPlanHovered) StartPlanIntroTimer(PlanIntroLength);
    }

    private void StartPlanIntroTimer(TimeSpan after)
    {
        if (_planIntroTimer == null)
        {
            _planIntroTimer = new System.Windows.Threading.DispatcherTimer();
            _planIntroTimer.Tick += (_, _) =>
            {
                _planIntroTimer!.Stop();
                if (_isFloatingPlanHovered) return;   // the leave restarts it
                _planIntroElapsed = true;
                RaiseFloatingPlan();
            };
        }
        _planIntroTimer.Stop();
        _planIntroTimer.Interval = after;
        _planIntroTimer.Start();
    }

    /// <summary>Stop a running intro now (a chat switch, the chat view going away).</summary>
    public void EndPlanIntro()
    {
        _planIntroTimer?.Stop();
        _planIsNew = false;
        _planIntroElapsed = true;
        RaiseFloatingPlan();
    }

    /// <summary>Tests: the intro's time is up now (as the timer would).</summary>
    public void ElapsePlanIntroForTest()
    {
        _planIntroTimer?.Stop();
        _planIntroElapsed = true;
        RaiseFloatingPlan();
    }

    private bool _stickyLangHooked;
    private bool _stickyPlanExpanded;
    public bool IsStickyPlanExpanded { get => _stickyPlanExpanded; set => SetProperty(ref _stickyPlanExpanded, value); }

    public ICommand ToggleStickyPlanCommand => _toggleStickyPlanCommand ??= new RelayCommand(() => IsStickyPlanExpanded = !IsStickyPlanExpanded);
    private ICommand? _toggleStickyPlanCommand;

    /// <summary>Open another chat (a branch chip).</summary>
    public ICommand GoToChatCommand => _goToChatCommand ??= new RelayCommand<string>(id =>
    {
        if (!string.IsNullOrEmpty(id) && id != CurrentSession?.Id) _ = GoToChatAsync(id);
    });
    private ICommand? _goToChatCommand;

    private async Task GoToChatAsync(string id)
    {
        await LoadSessionAsync(id);
        NavigateToChatRequested?.Invoke();
        RefreshStickyPlan();
    }

    /// <summary>Rebuild the plan bar for the chat on screen (UI thread).</summary>
    public void RefreshStickyPlan()
    {
        if (!_stickyLangHooked)
        {
            _stickyLangHooked = true;   // the bar's words are built here, so a language switch rebuilds them
            LocalizedResources.Instance.PropertyChanged += (_, _) => App.Current?.Dispatcher.BeginInvoke(RefreshStickyPlan);
        }
        var session = CurrentSession;
        if (session == null)
        {
            StickyPlanItems = Array.Empty<PlanItemView>(); StickyBranches = Array.Empty<BranchLink>();
            TrackFloatingPlan(null, StickyPlanItems);
        }
        else
        {
            var snap = PlanSnapshot(session.Id);
            var items = snap.Items.Select((t, i) => new PlanItemView(i + 1, t.Content, t.Status?.ToLowerInvariant() switch
            {
                "completed" or "done" => "done",
                "in_progress" or "active" => "active",
                _ => "pending",
            }) { IsLive = snap.Running }).ToList();
            int done = items.Count(p => p.IsDone);
            if (items.FirstOrDefault(p => p.IsActive) is { } activeStep) activeStep.Running = RunningLineFor(session.Id);
            // The same plan a step further keeps its rows (state set in place): the bar plays the change, no redraw.
            if (_stickyPlanSignature.StartsWith(session.Id + "|", StringComparison.Ordinal))
                items = PlanItemView.Reconcile(_stickyPlanItems, items);
            StickyPlanItems = items;
            // The plan's identity: its chat and its items' words (a status change keeps it; a new plan has new words).
            _stickyPlanSignature = session.Id + "|" + string.Join("|", items.Select(p => p.Text));
            TrackFloatingPlan(session.Id, items);
            // The number is the step it is on, not how many are finished: the third item running reads 3/5, never 2/5.
            var now = items.FirstOrDefault(p => p.IsActive) ?? items.FirstOrDefault(p => p.IsPending);
            StickyPlanHeader = items.Count == 0 ? "" : $"{Loc("chat.plan")} {now?.Number ?? items.Count}/{items.Count}";
            StickyPlanNow = now?.Text ?? (items.Count > 0 ? Loc("chat.plan.allDone") : "");
            StickyPlanProgress = items.Count == 0 ? 0 : (double)done / items.Count;

            string TitleOf(string id) => Sessions.FirstOrDefault(s => s.Id == id)?.Title ?? "";
            var links = new List<BranchLink>();
            foreach (var b in Branches().Where(b => b.Child == session.Id && Sessions.Any(s => s.Id == b.Parent)))
                links.Add(new BranchLink(b.Parent, Loc("chat.plan.branchParent"), TitleOf(b.Parent), true));
            foreach (var b in Branches().Where(b => b.Parent == session.Id && Sessions.Any(s => s.Id == b.Child)))
                links.Add(new BranchLink(b.Child, LocF("chat.plan.branchChild", b.Step), TitleOf(b.Child), false));
            StickyBranches = links;
        }
        OnPropertyChanged(nameof(StickyPlanCharacter));
        OnPropertyChanged(nameof(HasStickyBranches));
        RaiseFloatingPlan();
    }

    public void OpenPlanWindow(ChatSession? session)
    {
        if (session == null) return;
        if (_planWindows.TryGetValue(session.Id, out var open)) { open.Activate(); return; }
        var vm = new PlanWindowViewModel(this, session.Id, CheckpointService.Current);
        var win = new Views.PlanWindow(vm);
        var owner = App.Current?.MainWindow;
        if (owner != null && owner.IsLoaded && owner != win) win.Owner = owner;
        win.Closed += (_, _) => _planWindows.Remove(session.Id);
        _planWindows[session.Id] = win;
        win.Show();
    }

    public sealed record PlanState(string Title, string Project, bool Running, int Step, IReadOnlyList<TodoItem> Items);

    /// <summary>What the plan window shows for one chat: its running agent's live plan, else the last one written.</summary>
    public PlanState PlanSnapshot(string sessionId)
    {
        var run = _runs.FirstOrDefault(r => r.Session.Id == sessionId);
        var session = run?.Session ?? Sessions.FirstOrDefault(s => s.Id == sessionId)
                      ?? (CurrentSession?.Id == sessionId ? CurrentSession : null);
        List<TodoItem> items;
        if (run != null)
        {
            // The run's own thread edits this list; a copy that loses a race is simply taken again next tick.
            try { items = run.Scope.Todos.ToList(); } catch { items = new(); }
            if (items.Count == 0 && _lastPlans.TryGetValue(sessionId, out var kept)) items = kept;
        }
        else items = _lastPlans.TryGetValue(sessionId, out var kept) ? kept : new();
        string project = run?.Scope.WorkingDirectory ?? session?.ProjectPath ?? "";
        return new PlanState(session?.Title ?? "", project, run != null, run?.Scope.Step ?? 0, items);
    }

    /// <summary>An item the owner added in the plan window. Running → the agent gets it at its next step and adds it to
    /// its plan (true). Idle → false: the window keeps it for "start". It shows in the chat as a waiting message, like
    /// one typed in the box: a run that ends before its agent reads it sends it as the next turn, or keeps it in the chat
    /// marked not sent — an untagged note used to vanish when its chat was off screen.</summary>
    public bool AddPlanItem(string sessionId, string text)
    {
        var run = _runs.FirstOrDefault(r => r.Session.Id == sessionId);
        if (run == null) return false;
        var bubble = new ChatMessage { Role = MessageRole.User, Content = Loc("plan.added.bubble") + " " + text, IsQueuedNote = true };
        bubble.QueueRow = new ChatMessage { Role = MessageRole.System, Content = Loc("chat.steer.queued") };
        run.Messages.Add(bubble);
        run.Messages.Add(bubble.QueueRow);
        run.Scope.AddUserNote("Add this to your plan (todo_write / update_plan), after the items already there, and do it in this turn: " + text, bubble);
        return true;
    }

    /// <summary>Queued plan items → the chat's next turn (the chat is brought on screen first).</summary>
    public async Task StartPlanItemsAsync(string sessionId, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        if (CurrentSession?.Id != sessionId) await LoadSessionAsync(sessionId);
        if (CurrentSession?.Id != sessionId) return;
        NavigateToChatRequested?.Invoke();
        UserInput = Loc("plan.startMessage") + "\n" + string.Join("\n", items.Select(i => "- " + i));
        if (SendMessageCommand.CanExecute(null)) SendMessageCommand.Execute(null);
    }

    /// <summary>
    /// The Watch page's "ask the AI": open the newest chat of <paramref name="projectPath"/> (or a new chat there) and
    /// put <paramref name="text"/> in the input box — not sent; the owner reads it and presses Send (report, then wait).
    /// </summary>
    public async Task PrepareMessageInProjectAsync(string projectPath, string text)
    {
        string path = projectPath.TrimEnd('\\', '/');
        var chat = Sessions.Where(s => string.Equals((s.ProjectPath ?? "").TrimEnd('\\', '/'), path, StringComparison.OrdinalIgnoreCase))
                           .OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
        if (chat != null && chat.Id != CurrentSession?.Id) await LoadSessionAsync(chat.Id);
        else
        {
            // The project's chat is on screen (or a new one is made): a sidebar chat still loading must not land over
            // it with the text in the box.
            CancelPendingChatLoad();
            if (chat == null)
            {
                if (Directory.Exists(path)) ApplyWorkingDirectory(path, announce: false, showEditor: false);
                NewSession();
            }
        }
        NavigateToChatRequested?.Invoke();
        UserInput = text;
    }

    /// <summary>A line in the chat saying the files went back (so the agent's next turn knows too).</summary>
    public void NotePlanRewind(string sessionId, CheckpointService.Point p, bool ok, int files)
    {
        if (!ok || CurrentSession?.Id != sessionId) return;
        Messages.Add(new ChatMessage { Role = MessageRole.System, Content = "⏪ " + LocF("plan.rewind.note", p.Step, p.When, files) });
    }

    /// <summary>
    /// Branch: a git worktree beside the project (from the current work, uncommitted changes included), with the files
    /// put back to how they were before step <paramref name="p"/>, opened in a new chat. The original is not touched.
    /// </summary>
    public async Task<string> BranchFromPointAsync(string sessionId, CheckpointService.Point p)
    {
        var cp = CheckpointService.Current;
        string project = p.Project.TrimEnd('\\', '/');
        if (cp == null || !Directory.Exists(project)) return LocF("plan.branch.failed", "project folder not found");
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string target = Path.Combine(Path.GetDirectoryName(project) ?? project, Path.GetFileName(project) + "-branch-" + stamp);
        string branch = "cluadex/branch-" + stamp;
        try
        {
            var result = await Task.Run(() =>
            {
                var inside = Git(project, "rev-parse", "--is-inside-work-tree");
                if (inside.Code != 0 || inside.Out.Trim() != "true") return (false, "not a git repository");
                // `stash create` makes a commit of the working tree without touching it (empty when nothing changed).
                string from = Git(project, "stash", "create").Out.Trim();
                if (from.Length == 0) from = "HEAD";
                var add = Git(project, "worktree", "add", "-b", branch, target, from);
                if (add.Code != 0) return (false, add.Err.Trim());
                var r = cp.RestoreInto(project, p.Id, target);
                return r.Ok ? (true, "") : (false, r.Message);
            });
            if (!result.Item1) return LocF("plan.branch.failed", result.Item2);

            string title = CurrentSession?.Id == sessionId ? CurrentSession.Title
                : Sessions.FirstOrDefault(s => s.Id == sessionId)?.Title ?? "";
            CancelPendingChatLoad();
            ApplyWorkingDirectory(target, announce: false, showEditor: false);
            NewSession();
            if (CurrentSession != null)
            {
                CurrentSession.Title = LocF("plan.branch.chatTitle", p.Step, title);
                RecordBranch(CurrentSession.Id, sessionId, p.Step);
            }
            Messages.Add(new ChatMessage { Role = MessageRole.System,
                Content = "🌿 " + LocF("plan.branch.note", title, p.Step, p.When, target, branch) });
            NavigateToChatRequested?.Invoke();
            RefreshStickyPlan();
            return LocF("plan.branch.done", target);
        }
        catch (Exception ex)
        {
            _debugLog?.Warn("Plan", "branch failed", ex);
            return LocF("plan.branch.failed", ex.Message);
        }
    }

    private static (int Code, string Out, string Err) Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true,
                                                UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var err = proc.StandardError.ReadToEndAsync();
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60_000);
        return (proc.ExitCode, output, err.Result);
    }
}
