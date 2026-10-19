using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using CluadeX.Services;
using CluadeX.Services.Setup;

namespace CluadeX.ViewModels;

/// <summary>One row of the setup page.</summary>
public sealed class SetupRowViewModel : ViewModelBase
{
    private readonly SetupViewModel _owner;
    private SetupItem _item;
    private bool _busy;
    private string _note = "";

    public SetupRowViewModel(SetupViewModel owner, SetupItem item)
    {
        _owner = owner;
        _item = item;
        PrimaryCommand = new RelayCommand(() => _owner.Do(this, _item.Primary), () => !_busy && _item.Primary != null);
        SecondaryCommand = new RelayCommand(() => _owner.Do(this, _item.Secondary), () => !_busy && _item.Secondary != null);
    }

    public SetupItem Item => _item;
    public string Id => _item.Id;

    private static string L(string key) => LocalizedResources.Instance[key];

    public string Title => L(_item.TitleKey);
    public string Why => L(_item.WhyKey);
    public string StateText => L("setup.state." + _item.State) + (_item.Optional || _item.IsReady ? "" : " · " + L("setup.required"));
    /// <summary>ok / warn / info — the row's colour (DataTriggers in the view).</summary>
    public string StateKind => _item.State switch
    {
        SetupState.Ready => "ok",
        SetupState.Missing when !_item.Optional => "danger",
        SetupState.NeedsKey when !_item.Optional => "danger",
        SetupState.Missing or SetupState.NeedsKey => "warn",
        _ => "info",
    };
    public string StateGlyph => _item.State switch
    {
        SetupState.Ready => "",        // check
        SetupState.NeedsKey => "",     // key / permissions
        SetupState.NeedsSignIn => "",  // contact
        SetupState.NotRunning => "",   // play
        SetupState.NoModel => "",      // download
        _ => "",                       // warning
    };
    /// <summary>What was found (a version, a path) or why it is not usable yet.</summary>
    public string Detail => _item.Detail switch
    {
        "" => "",
        "needs-node" => L("setup.detail.needsNode"),
        var d when d.EndsWith(" · no-debugpy", StringComparison.Ordinal) => d.Replace(" · no-debugpy", "") + " · " + L("setup.detail.noDebugpy"),
        var d when _item.Id == "ollama" && _item.IsReady => string.Format(L("setup.detail.ollamaModels"), d),
        var d => d,
    };
    public bool HasDetail => Detail.Length > 0;
    public string PrimaryLabel => _item.Primary is { } a ? L(a.LabelKey) + (a.SizeHint is { } s ? $" ({s})" : "") : "";
    public string SecondaryLabel => _item.Secondary is { } a ? L(a.LabelKey) : "";
    public bool HasPrimary => _item.Primary != null;
    public bool HasSecondary => _item.Secondary != null;
    public bool IsReady => _item.IsReady;

    public bool IsBusy
    {
        get => _busy;
        set
        {
            if (SetProperty(ref _busy, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>What is happening on this row now ("installing — watch the console window").</summary>
    public string Note { get => _note; set { if (SetProperty(ref _note, value)) OnPropertyChanged(nameof(HasNote)); } }
    public bool HasNote => _note.Length > 0;

    public ICommand PrimaryCommand { get; }
    public ICommand SecondaryCommand { get; }

    public void Update(SetupItem item)
    {
        _item = item;
        foreach (var name in new[] { nameof(Item), nameof(Title), nameof(Why), nameof(StateText), nameof(StateKind), nameof(StateGlyph),
                     nameof(Detail), nameof(HasDetail), nameof(PrimaryLabel), nameof(SecondaryLabel), nameof(HasPrimary), nameof(HasSecondary), nameof(IsReady) })
            OnPropertyChanged(name);
        CommandManager.InvalidateRequerySuggested();
    }

    public void Relabel() => Update(_item);
}

/// <summary>A group of rows ("AI that answers the chat", "AI on this PC", …).</summary>
public sealed class SetupGroupViewModel : ViewModelBase
{
    public SetupGroupViewModel(string key) => Key = key;
    public string Key { get; }
    public string Title => LocalizedResources.Instance["setup.group." + Key];
    public ObservableCollection<SetupRowViewModel> Rows { get; } = new();
    public void Relabel() => OnPropertyChanged(nameof(Title));
}

/// <summary>
/// The setup page ("ตัวช่วยติดตั้ง"): every piece CluadeX can use, whether this PC has it, and one button that gets it —
/// install (in a console the user watches, after a confirm that shows the exact commands), sign in, get a key, start.
/// Re-checks by itself after each action.
/// </summary>
public sealed class SetupViewModel : ViewModelBase
{
    private readonly SystemSetupService _setup;
    private SetupFacts? _facts;
    private bool _checking;
    private string _summary = "", _banner = "", _status = "";
    private CancellationTokenSource? _wait;

    /// <summary>A row wants another page: ("Settings", provider) · ("McpServers", null) · ("License", null).</summary>
    public event Action<string, string?>? NavigateRequested;

    public ObservableCollection<SetupGroupViewModel> Groups { get; } = new();

    public SetupViewModel(SystemSetupService setup)
    {
        _setup = setup;
        RecheckCommand = new AsyncRelayCommand(RecheckAsync);
        LocalizedResources.Instance.PropertyChanged += (_, _) => App.Current?.Dispatcher.BeginInvoke(Relabel);
    }

    private static string L(string key) => LocalizedResources.Instance[key];
    private static string LF(string key, params object[] a) => string.Format(L(key), a);

    public ICommand RecheckCommand { get; }
    public bool IsChecking { get => _checking; private set => SetProperty(ref _checking, value); }
    /// <summary>"พร้อม 9 จาก 15".</summary>
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    /// <summary>The one thing to know first (nothing can answer the chat yet; CluadeX is inside an MSIX package).</summary>
    public string Banner { get => _banner; private set { if (SetProperty(ref _banner, value)) OnPropertyChanged(nameof(HasBanner)); } }
    public bool HasBanner => _banner.Length > 0;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>The page opened: check once (later visits keep the last result; Re-check looks again).</summary>
    public void OnShown()
    {
        if (_facts == null && !_checking) _ = RecheckAsync();
    }

    public async Task RecheckAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        Status = L("setup.checking");
        try
        {
            var facts = await Task.Run(() => _setup.GatherAsync());
            Apply(facts);
            Status = LF("setup.checkedAt", DateTime.Now.ToString("HH:mm:ss"));
        }
        catch (Exception ex)
        {
            Status = L("setup.checkFailed");
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally { IsChecking = false; }
    }

    /// <summary>Show these facts (internal: the harness renders the page for a made-up machine).</summary>
    internal void Apply(SetupFacts facts)
    {
        _facts = facts;
        var items = SystemSetupService.BuildItems(facts);
        foreach (var item in items)
        {
            var group = Groups.FirstOrDefault(g => g.Key == item.Group);
            if (group == null) Groups.Add(group = new SetupGroupViewModel(item.Group));
            var row = group.Rows.FirstOrDefault(r => r.Id == item.Id);
            if (row == null) group.Rows.Add(new SetupRowViewModel(this, item));
            else row.Update(item);
        }
        int ready = items.Count(i => i.IsReady);
        Summary = LF("setup.summary", ready, items.Count);
        Banner = facts.InsidePackage ? L("setup.banner.insidePackage")
            : !SystemSetupService.AnyChatModelReady(facts) ? L("setup.banner.noModel")
            : "";
    }

    private void Relabel()
    {
        foreach (var g in Groups)
        {
            g.Relabel();
            foreach (var r in g.Rows) r.Relabel();
        }
        if (_facts != null) Apply(_facts);
    }

    /// <summary>A row's button.</summary>
    public async void Do(SetupRowViewModel row, SetupAction? action)
    {
        if (action == null || row.IsBusy || _facts == null) return;
        try
        {
            switch (action.Kind)
            {
                case SetupActionKind.Install:
                case SetupActionKind.Run:
                    await InstallAsync(row, action);
                    break;
                case SetupActionKind.SignIn:
                    if (!_setup.OpenSignIn(action.Target ?? "", _facts)) { row.Note = L("setup.note.signInFailed"); return; }
                    await WaitUntilReadyAsync(row, L("setup.note.signingIn"), TimeSpan.FromMinutes(5));
                    break;
                case SetupActionKind.StartOllama:
                    if (!_setup.StartOllama(action.Target ?? "ollama")) { row.Note = L("setup.note.startFailed"); return; }
                    await WaitUntilReadyAsync(row, L("setup.note.starting"), TimeSpan.FromSeconds(45));
                    break;
                case SetupActionKind.OpenUrl:
                    if (action.Target is { } url && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    break;
                case SetupActionKind.OpenSettings:
                    NavigateRequested?.Invoke("Settings", action.Target);
                    break;
                case SetupActionKind.OpenPage:
                    NavigateRequested?.Invoke(action.Target ?? "Chat", null);
                    break;
            }
        }
        catch (Exception ex)
        {
            row.IsBusy = false;
            row.Note = ex is InvalidOperationException { Message: "inside-package" } ? L("setup.banner.insidePackage") : L("setup.note.failed");
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async Task InstallAsync(SetupRowViewModel row, SetupAction action)
    {
        if (_facts!.InsidePackage) { row.Note = L("setup.banner.insidePackage"); return; }
        var commands = action.Commands ?? Array.Empty<string>();
        string message = LF("setup.confirm.body", row.Title, action.SizeHint ?? "?")
                         + "\n\n" + string.Join("\n", commands.Select(c => "  " + c))
                         + "\n\n" + L(commands.Any(c => c.StartsWith("winget", StringComparison.Ordinal)) ? "setup.confirm.winget" : "setup.confirm.console");
        int choice = Views.Editor.ChoiceDialog.Show(LF("setup.confirm.title", row.Title), message,
            new[] { L(action.Kind == SetupActionKind.Run ? "setup.confirm.run" : "setup.confirm.install"), L("setup.confirm.cancel") });
        if (choice != 0) return;

        row.IsBusy = true;
        row.Note = L("setup.note.installing");
        int code = await _setup.RunInConsoleAsync(row.Title, commands);
        row.IsBusy = false;
        await RecheckAsync();
        row.Note = code == 0
            ? (row.IsReady || row.Item.State is not SetupState.Missing ? L("setup.note.installed") : L("setup.note.installedNotFound"))
            : LF("setup.note.installFailed", code);
    }

    /// <summary>After a sign-in or a start: look again every few seconds until the row is ready (or time is up).</summary>
    private async Task WaitUntilReadyAsync(SetupRowViewModel row, string note, TimeSpan limit)
    {
        _wait?.Cancel();
        var cts = _wait = new CancellationTokenSource(limit);
        row.IsBusy = true;
        row.Note = note;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), cts.Token);
                var facts = await Task.Run(() => _setup.GatherAsync(cts.Token));
                Apply(facts);
                // Rows are updated in place: `row` is the live row. Done when it is no longer waiting on the sign-in / start.
                if (row.Item.State is not (SetupState.NeedsSignIn or SetupState.NotRunning))
                {
                    SystemSetupService.RaiseToolsChanged();
                    row.IsBusy = false;
                    row.Note = row.IsReady ? L("setup.note.readyNow") : "";
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        row.IsBusy = false;
        row.Note = L("setup.note.notYet");
    }
}
