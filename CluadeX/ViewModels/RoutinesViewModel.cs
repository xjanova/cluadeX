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
    private void SetKind(string k)
    {
        _kind = k;
        OnPropertyChanged(nameof(KindDaily)); OnPropertyChanged(nameof(KindInterval)); OnPropertyChanged(nameof(KindOnce));
    }

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

    // ─── delete, with a confirm ───
    private RoutineRow? _pendingDelete;
    public RoutineRow? PendingDelete
    {
        get => _pendingDelete;
        set { if (SetProperty(ref _pendingDelete, value)) { OnPropertyChanged(nameof(IsDeleteOpen)); OnPropertyChanged(nameof(DeleteText)); } }
    }
    public bool IsDeleteOpen => _pendingDelete != null;
    public string DeleteText => _pendingDelete == null ? "" : string.Format(_loc.T("routine.page.deleteConfirm"), _pendingDelete.Name);

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

    public RoutinesViewModel(RoutineService service, ChatViewModel chat, LocalizationService loc)
    {
        _service = service;
        _chat = chat;
        _loc = loc;
        for (int i = 0; i < 7; i++) Days.Add(new DayBox { Index = i, Label = loc.T("routine.day." + i) });
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));
        _service.Changed += () => Application.Current?.Dispatcher.BeginInvoke(Refresh);

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

    /// <summary>The list again, from the service.</summary>
    public void Refresh()
    {
        Rows.Clear();
        foreach (var r in _service.All())
        {
            string last = r.LastStatus switch
            {
                "running" => L("routine.status.running"),
                "ran" => string.Format(L("routine.status.ran"), r.LastRunAt?.ToString("d MMM HH:mm") ?? ""),
                "failed" => string.Format(L("routine.status.failed"), r.LastRunAt?.ToString("d MMM HH:mm") ?? "") + (r.LastNote.Length > 0 ? " — " + r.LastNote : ""),
                "missed" => L("routine.status.missed"),
                _ => "",
            };
            if (r.LastNote == "busy") last += (last.Length > 0 ? " · " : "") + L("routine.status.busy");
            Rows.Add(new RoutineRow
            {
                R = r,
                When = RoutineSchedule.Describe(r, L),
                Next = r.Enabled && r.NextRunAt is { } n ? string.Format(L("routine.page.next"), n.ToString("ddd d MMM HH:mm")) : L("routine.page.never"),
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
        EditProject = r.Project;
        SetKind(r.Kind);
        EditTime = r.Time;
        EditMinutes = r.IntervalMinutes.ToString();
        EditOnceDate = (r.OnceAt ?? DateTime.Today.AddDays(1)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        EditOnceTime = r.OnceAt?.ToString("HH:mm") ?? r.Time;
        foreach (var d in Days) d.On = (r.Days & (1 << d.Index)) != 0;
        EditSameChat = r.SameChat;
        EditCatchUp = r.CatchUp;
        EditError = "";
        Projects.Clear();
        foreach (var p in (ProjectRegistryService.Current?.Recent(50).Select(e => e.Path) ?? Enumerable.Empty<string>())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            Projects.Add(p);
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
