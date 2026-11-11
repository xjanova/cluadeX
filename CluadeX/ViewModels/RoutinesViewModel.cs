using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CluadeX.Services;
using CluadeX.Services.Routines;

namespace CluadeX.ViewModels;

/// <summary>One routine as the Routines page shows it (rebuilt whenever the service changes).</summary>
public sealed class RoutineRow
{
    public required Routine R { get; init; }
    public string Id => R.Id;
    public string Name => R.Name;
    public string When { get; init; } = "";
    public string Next { get; init; } = "";
    public string Last { get; init; } = "";
    public string Project => R.Project.Length == 0 ? "—" : R.Project;
    public bool Enabled => R.Enabled;
    public bool ByAi => R.CreatedBy == "ai";
    public bool HasChat => R.ChatId != null;
    public bool IsRunning { get; init; }
}

/// <summary>The Routines page — see <see cref="RoutineService"/>.</summary>
public sealed class RoutinesViewModel : ViewModelBase
{
    private readonly RoutineService _service;
    private readonly ChatViewModel _chat;
    private readonly LocalizationService _loc;

    public ObservableCollection<RoutineRow> Rows { get; } = new();
    public bool HasRows => Rows.Count > 0;

    private string _status = "";
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    // ─── the editor ───
    private Routine? _editing;
    public bool IsEditing => _editing != null;

    private string _name = "", _prompt = "", _project = "", _time = "09:00", _minutes = "60", _onceTime = "09:00", _error = "";
    public string EditName { get => _name; set => SetProperty(ref _name, value); }
    public string EditPrompt { get => _prompt; set => SetProperty(ref _prompt, value); }
    public string EditProject { get => _project; set => SetProperty(ref _project, value); }
    public string EditTime { get => _time; set => SetProperty(ref _time, value); }
    public string EditMinutes { get => _minutes; set => SetProperty(ref _minutes, value); }
    public string EditOnceTime { get => _onceTime; set => SetProperty(ref _onceTime, value); }
    public string EditError { get => _error; set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasEditError)); } }
    public bool HasEditError => _error.Length > 0;

    private string _onceDate = "";
    /// <summary>"Once": the day, yyyy-MM-dd (a text box: the app has no themed date picker).</summary>
    public string EditOnceDate { get => _onceDate; set => SetProperty(ref _onceDate, value); }

    private string _kind = RoutineSchedule.Daily;
    public bool KindDaily { get => _kind == RoutineSchedule.Daily; set { if (value) SetKind(RoutineSchedule.Daily); } }
    public bool KindInterval { get => _kind == RoutineSchedule.Interval; set { if (value) SetKind(RoutineSchedule.Interval); } }
    public bool KindOnce { get => _kind == RoutineSchedule.Once; set { if (value) SetKind(RoutineSchedule.Once); } }
    public bool KindEvent { get => _kind == RoutineSchedule.Event; set { if (value) SetKind(RoutineSchedule.Event); } }
    /// <summary>A time kind (catching up a missed time means something only there).</summary>
    public bool KindTimed => _kind != RoutineSchedule.Event;
    private void SetKind(string k)
    {
        _kind = k;
        OnPropertyChanged(nameof(KindDaily)); OnPropertyChanged(nameof(KindInterval)); OnPropertyChanged(nameof(KindOnce));
        OnPropertyChanged(nameof(KindEvent)); OnPropertyChanged(nameof(KindTimed));
    }

    // ─── "when something happens" ───
    private string _trigger = RoutineSchedule.CiFailed;
    public bool TrigCi { get => _trigger == RoutineSchedule.CiFailed; set { if (value) SetTrigger(RoutineSchedule.CiFailed); } }
    public bool TrigComment { get => _trigger == RoutineSchedule.PrComment; set { if (value) SetTrigger(RoutineSchedule.PrComment); } }
    public bool TrigIssue { get => _trigger == RoutineSchedule.NewIssue; set { if (value) SetTrigger(RoutineSchedule.NewIssue); } }
    public bool TrigFiles { get => _trigger == RoutineSchedule.FileChanged; set { if (value) SetTrigger(RoutineSchedule.FileChanged); } }
    /// <summary>Comments and issues: whose may start it.</summary>
    public bool TrigByPeople => _trigger is RoutineSchedule.PrComment or RoutineSchedule.NewIssue;
    public bool TrigGitHub => _trigger != RoutineSchedule.FileChanged;
    private void SetTrigger(string t)
    {
        _trigger = t;
        foreach (var n in new[] { nameof(TrigCi), nameof(TrigComment), nameof(TrigIssue), nameof(TrigFiles), nameof(TrigByPeople), nameof(TrigGitHub) })
            OnPropertyChanged(n);
    }

    private string _branch = "", _files = "", _cooldown = "15";
    private bool _fromAnyone;
    public string EditBranch { get => _branch; set => SetProperty(ref _branch, value); }
    public string EditFiles { get => _files; set => SetProperty(ref _files, value); }
    public string EditCooldown { get => _cooldown; set => SetProperty(ref _cooldown, value); }
    public bool EditFromAnyone { get => _fromAnyone; set => SetProperty(ref _fromAnyone, value); }

    private bool _sameChat, _catchUp = true;
    public bool EditSameChat { get => _sameChat; set => SetProperty(ref _sameChat, value); }
    public bool EditCatchUp { get => _catchUp; set => SetProperty(ref _catchUp, value); }

    /// <summary>The seven day boxes, Sunday first (bit i of Routine.Days).</summary>
    public ObservableCollection<DayBox> Days { get; } = new();
    public sealed class DayBox : ViewModelBase
    {
        public int Index { get; init; }
        public string Label { get; init; } = "";
        private bool _on = true;
        public bool On { get => _on; set => SetProperty(ref _on, value); }
    }

    /// <summary>Projects for the folder box: the ones the app knows.</summary>
    public ObservableCollection<string> Projects { get; } = new();
    public bool HasProjects => Projects.Count > 0;

    private string? _pick;
    /// <summary>A pick from the recent projects: fills the folder box.</summary>
    public string? PickProject
    {
        get => _pick;
        set { if (SetProperty(ref _pick, value) && !string.IsNullOrEmpty(value)) EditProject = value; }
    }

    // ─── delete, with a confirm ───
    private RoutineRow? _pendingDelete;
    public RoutineRow? PendingDelete
    {
        get => _pendingDelete;
        set { if (SetProperty(ref _pendingDelete, value)) { OnPropertyChanged(nameof(IsDeleteOpen)); OnPropertyChanged(nameof(DeleteText)); } }
    }
    public bool IsDeleteOpen => _pendingDelete != null;
    public string DeleteText => _pendingDelete == null ? "" : string.Format(_loc.T("routine.page.deleteConfirm"), _pendingDelete.Name);

    // ─── Telegram (Services/Remote/TelegramBridge) ───
    private static Services.Remote.TelegramBridge? _tg => Services.Remote.TelegramBridge.Current;
    public bool TgAvailable => _tg != null;
    public bool TgHasToken => _tg?.HasToken == true;
    public bool TgNoToken => _tg != null && !_tg.HasToken;
    public bool TgPaired => _tg?.IsPaired == true;
    public string TgConnectedText => _tg == null ? "" : string.Format(L("tg.page.connected"), _tg.BotName);
    public string TgPairedText => _tg == null ? "" : _tg.IsPaired ? string.Format(L("tg.page.pairedWith"), _tg.PairedName) : L("tg.page.notPaired");
    public string? TgPairCode => _tg?.PairCode;
    public bool TgPairing => _tg?.PairCode != null;
    public string TgPairMessage => _tg?.PairCode is { } code ? "/pair " + code : "";
    public string TgPairHint => _tg == null ? "" : string.Format(L("tg.page.pairCode"), _tg.BotName);
    public string TgError => _tg?.LastError ?? "";
    public bool TgHasError => TgError.Length > 0;
    private bool _tgBusy;
    public bool TgBusy { get => _tgBusy; private set { if (SetProperty(ref _tgBusy, value)) OnPropertyChanged(nameof(TgIdle)); } }
    public bool TgIdle => !_tgBusy;
    private string _tgConnectError = "";
    public string TgConnectError { get => _tgConnectError; private set { if (SetProperty(ref _tgConnectError, value)) OnPropertyChanged(nameof(TgHasConnectError)); } }
    public bool TgHasConnectError => _tgConnectError.Length > 0;
    public bool TgNotifyRoutines
    {
        get => _tg?.NotifyRoutines == true;
        set { if (_tg != null && value != _tg.NotifyRoutines) _tg.SetOptions(value, _tg.ForwardAllCards); }
    }
    public bool TgAllCards
    {
        get => _tg?.ForwardAllCards == true;
        set { if (_tg != null && value != _tg.ForwardAllCards) _tg.SetOptions(_tg.NotifyRoutines, value); }
    }
    private bool _tgDisconnectOpen;
    public bool TgDisconnectOpen { get => _tgDisconnectOpen; set => SetProperty(ref _tgDisconnectOpen, value); }

    public ICommand TgPairCommand { get; }
    public ICommand TgUnpairCommand { get; }
    public ICommand TgAskDisconnectCommand { get; }
    public ICommand TgConfirmDisconnectCommand { get; }
    public ICommand TgCancelDisconnectCommand { get; }

    /// <summary>The page's Connect (code-behind passes the PasswordBox's text, then clears the box). Never logged.</summary>
    public async Task ConnectTelegramAsync(string token)
    {
        if (_tg == null || _tgBusy) return;
        TgBusy = true;
        TgConnectError = "";
        try
        {
            if (await _tg.ConnectAsync(token) is { } problem) TgConnectError = L(problem);
        }
        finally { TgBusy = false; RefreshTelegram(); }
    }

    private void RefreshTelegram()
    {
        foreach (var n in new[] { nameof(TgAvailable), nameof(TgHasToken), nameof(TgNoToken), nameof(TgPaired), nameof(TgConnectedText), nameof(TgPairedText),
                     nameof(TgPairCode), nameof(TgPairing), nameof(TgPairMessage), nameof(TgPairHint), nameof(TgError), nameof(TgHasError),
                     nameof(TgNotifyRoutines), nameof(TgAllCards) })
            OnPropertyChanged(n);
    }

    public ICommand NewCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ToggleCommand { get; }
    public ICommand RunNowCommand { get; }
    public ICommand OpenChatCommand { get; }
    public ICommand AskDeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }

    /// <param name="telegram">Asked for so the bridge exists when the page does (it is <see cref="Services.Remote.TelegramBridge.Current"/>).</param>
    public RoutinesViewModel(RoutineService service, ChatViewModel chat, LocalizationService loc, Services.Remote.TelegramBridge telegram)
    {
        _service = service;
        _chat = chat;
        _loc = loc;
        for (int i = 0; i < 7; i++) Days.Add(new DayBox { Index = i, Label = loc.T("routine.day." + i) });
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));
        Projects.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasProjects));
        _service.Changed += () => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        if (RoutineTriggers.Current is { } triggers) triggers.Changed += () => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        if (_tg != null) _tg.Changed += () => Application.Current?.Dispatcher.BeginInvoke(RefreshTelegram);
        TgPairCommand = new RelayCommand(() => { _tg?.StartPairing(); RefreshTelegram(); });
        TgUnpairCommand = new RelayCommand(() => { _tg?.Unpair(); RefreshTelegram(); });
        TgAskDisconnectCommand = new RelayCommand(() => TgDisconnectOpen = true);
        TgCancelDisconnectCommand = new RelayCommand(() => TgDisconnectOpen = false);
        TgConfirmDisconnectCommand = new RelayCommand(() => { TgDisconnectOpen = false; _tg?.Disconnect(); RefreshTelegram(); });

        NewCommand = new RelayCommand(() => Open(new Routine { Project = chat.ProjectFolderNow() }));
        EditCommand = new RelayCommand<RoutineRow>(row => { if (row != null) Open(row.R.Copy()); });
        CancelCommand = new RelayCommand(Close);
        SaveCommand = new RelayCommand(Save);
        ToggleCommand = new RelayCommand<RoutineRow>(row => { if (row != null) _service.SetEnabled(row.Id, !row.Enabled); });
        RunNowCommand = new RelayCommand<RoutineRow>(row =>
        {
            if (row == null) return;
            Status = string.Format(_loc.T(_service.RunNow(row.Id) ? "routine.page.started" : "routine.page.alreadyRunning"), row.Name);
        });
        OpenChatCommand = new AsyncRelayCommand<RoutineRow>(async row =>
        {
            if (row?.R.ChatId is { } id) await _chat.OpenChatByIdAsync(id);
        });
        AskDeleteCommand = new RelayCommand<RoutineRow>(row => PendingDelete = row);
        CancelDeleteCommand = new RelayCommand(() => PendingDelete = null);
        ConfirmDeleteCommand = new RelayCommand(() =>
        {
            if (_pendingDelete is { } row) _service.Remove(row.Id);
            PendingDelete = null;
        });
        Refresh();
    }

    private string L(string key) => _loc.T(key);
    private System.Globalization.CultureInfo Ui => RoutineSchedule.Culture(_loc.CurrentLanguage);

    /// <summary>The list again, from the service.</summary>
    public void Refresh()
    {
        Rows.Clear();
        foreach (var r in _service.All())
        {
            string last = r.LastStatus switch
            {
                "running" => L("routine.status.running"),
                "ran" => string.Format(L("routine.status.ran"), r.LastRunAt?.ToString("d MMM HH:mm", Ui) ?? ""),
                "failed" => string.Format(L("routine.status.failed"), r.LastRunAt?.ToString("d MMM HH:mm", Ui) ?? "") + (r.LastNote.Length > 0 ? " — " + r.LastNote : ""),
                "missed" => L("routine.status.missed"),
                _ => "",
            };
            if (r.LastNote == "busy") last += (last.Length > 0 ? " · " : "") + L("routine.status.busy");
            bool isEvent = r.Kind == RoutineSchedule.Event;
            if (isEvent && r.Enabled && RoutineTriggers.Current?.ErrorOf(r.Id) is { Length: > 0 } watchError)
                last += (last.Length > 0 ? " · " : "") + string.Format(L("routine.page.watchError"), watchError);
            Rows.Add(new RoutineRow
            {
                R = r,
                When = RoutineSchedule.Describe(r, L, Ui),
                Next = isEvent
                    ? (!r.Enabled ? L("routine.page.never") : r.PendingEvent.Length > 0 ? L("routine.page.eventWaiting") : L("routine.page.waitsEvent"))
                    : r.Enabled && r.NextRunAt is { } n ? string.Format(L("routine.page.next"), n.ToString("ddd d MMM HH:mm", Ui)) : L("routine.page.never"),
                Last = last,
                IsRunning = _service.IsRunning(r.Id),
            });
        }
    }

    private void Open(Routine r)
    {
        _editing = r;
        EditName = r.Name;
        EditPrompt = r.Prompt;
        SetKind(r.Kind);
        EditTime = r.Time;
        EditMinutes = r.IntervalMinutes.ToString();
        EditOnceDate = (r.OnceAt ?? DateTime.Today.AddDays(1)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        EditOnceTime = r.OnceAt?.ToString("HH:mm") ?? r.Time;
        foreach (var d in Days) d.On = (r.Days & (1 << d.Index)) != 0;
        EditSameChat = r.SameChat;
        EditCatchUp = r.CatchUp;
        SetTrigger(RoutineSchedule.Triggers.Contains(r.Trigger) ? r.Trigger : RoutineSchedule.CiFailed);
        EditBranch = r.Branch;
        EditFiles = r.Files;
        EditCooldown = r.CooldownMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        EditFromAnyone = r.FromAnyone;
        EditError = "";
        Projects.Clear();
        foreach (var p in (ProjectRegistryService.Current?.Recent(50).Select(e => e.Path) ?? Enumerable.Empty<string>())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            Projects.Add(p);
        EditProject = r.Project;
        PickProject = null;
        OnPropertyChanged(nameof(IsEditing));
    }

    private void Close()
    {
        _editing = null;
        EditError = "";
        OnPropertyChanged(nameof(IsEditing));
    }

    private void Save()
    {
        if (_editing is not { } r) return;
        r.Name = EditName.Trim();
        r.Prompt = EditPrompt;
        r.Project = EditProject.Trim().Trim('"');
        r.Kind = _kind;
        r.Time = EditTime.Trim();
        r.IntervalMinutes = int.TryParse(EditMinutes.Trim(), out var m) ? m : 0;
        r.Days = Days.Where(d => d.On).Aggregate(0, (acc, d) => acc | (1 << d.Index));
        r.OnceAt = DateTime.TryParseExact(EditOnceDate.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.None, out var day) && RoutineSchedule.TimeOf(EditOnceTime) is { } t
            ? day.Date + t : null;
        r.SameChat = EditSameChat;
        r.CatchUp = EditCatchUp;
        r.Trigger = _kind == RoutineSchedule.Event ? _trigger : r.Trigger;
        r.Branch = EditBranch.Trim();
        r.Files = EditFiles.Trim();
        r.CooldownMinutes = int.TryParse(EditCooldown.Trim(), out var cool) ? cool : 0;
        r.FromAnyone = EditFromAnyone;
        if (r.Project.Length > 0 && !System.IO.Directory.Exists(r.Project))
        {
            EditError = string.Format(L("routine.err.noFolder"), r.Project);
            return;
        }
        if (_service.Put(r) is { } problem) { EditError = L(problem); return; }
        Status = L("routine.page.saved");
        Close();
    }
}
