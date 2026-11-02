using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Setup;

namespace CluadeX.ViewModels;

/// <summary>The wizard's pictures (Assets\Wizard): nine step icons and the hero, each decoded once at the size it is drawn.</summary>
internal static class WizardArt
{
    private static readonly Dictionary<string, ImageSource?> Cache = new();

    public static ImageSource? Image(string file, int width)
    {
        if (Cache.TryGetValue(file, out var known)) return known;
        ImageSource? image = null;
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri($"pack://application:,,,/CluadeX;component/Assets/Wizard/{file}");
            b.DecodePixelWidth = width;
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.EndInit();
            b.Freeze();
            image = b;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        return Cache[file] = image;
    }

    public static ImageSource? StepIcon(ConnectStepKind kind) => Image(kind switch
    {
        ConnectStepKind.License => "license.png",
        ConnectStepKind.Install => "install.png",
        ConnectStepKind.SignIn => "signin.png",
        ConnectStepKind.ApiKey => "key.png",
        ConnectStepKind.Start => "start.png",
        ConnectStepKind.GetModel => "model.png",
        ConnectStepKind.Brain => "brain.png",
        _ => "test.png",
    } , 96);
}

/// <summary>One step in the wizard's row of steps.</summary>
public sealed class WizardStepViewModel : ViewModelBase
{
    private bool _done, _current;

    public WizardStepViewModel(ConnectStepKind kind) => Kind = kind;

    public ConnectStepKind Kind { get; }
    public string Title => LocalizedResources.Instance["wizard.step." + Kind];
    public ImageSource? Icon => WizardArt.StepIcon(Kind);
    public bool IsDone { get => _done; set => SetProperty(ref _done, value); }
    public bool IsCurrent { get => _current; set => SetProperty(ref _current, value); }
    public void Relabel() => OnPropertyChanged(nameof(Title));
}

/// <summary>One provider on the page: her face, where it stands, and a click that opens its wizard.</summary>
public sealed class ConnectionCardViewModel : ViewModelBase
{
    private ProviderConnection _connection;

    public ConnectionCardViewModel(ProviderConnection connection, string name, Action<AiProviderType> open)
    {
        _connection = connection;
        Name = name;
        OpenCommand = new RelayCommand(() => open(Provider));
    }

    public AiProviderType Provider => _connection.Provider;
    public string Name { get; }
    public Brush? GirlFace => ChatCharacter.ForProvider(Provider).FaceBrush;
    public bool IsReady => _connection.IsSetUp;
    public string StateText => LocalizedResources.Instance["wizard.state." + (IsReady ? nameof(ProviderConnectionState.Ready) : _connection.State.ToString())];
    public ICommand OpenCommand { get; }

    public void Update(ProviderConnection connection)
    {
        _connection = connection;
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(StateText));
    }

    public void Relabel() => OnPropertyChanged(nameof(StateText));
}

/// <summary>
/// The connection wizard: one provider, its steps in order, one button that does the current step (the same action a
/// setup row runs, with the same confirm that shows the command), then a real test, then back to the chat. The steps
/// come from what the PC really has, so a finished install or sign-in moves on by itself and a step already done never
/// shows. Closing it never stops anything: a running chat goes on, and the message typed in the chat stays in its box.
/// </summary>
public sealed class ConnectWizardViewModel : ViewModelBase
{
    private static readonly TimeSpan TestLimit = TimeSpan.FromSeconds(45);

    private readonly SetupViewModel _owner;
    private readonly SystemSetupService _setup;
    private ProviderConnection? _connection;
    private SetupRowViewModel? _row;
    private CancellationTokenSource? _test;
    private bool _open, _tested, _testRan, _fresh, _testing, _testFailed;
    private string _testMessage = "";
    private string? _model;

    public ConnectWizardViewModel(SetupViewModel owner, SystemSetupService setup)
    {
        _owner = owner;
        _setup = setup;
        PrimaryCommand = new RelayCommand(RunPrimary, () => CanAct && (HasAction || CanRetest));
        OtherCommand = new RelayCommand(() => Run(Current?.Other ?? KeyAgain), () => CanAct && HasOther);
        CheckAgainCommand = new AsyncRelayCommand(_owner.RecheckAsync, () => !_owner.IsChecking);
        CloseCommand = new RelayCommand(Close);
        FinishCommand = new RelayCommand(Finish, () => IsDone);
        UseAntigravityCommand = new RelayCommand(() => Open(AiProviderType.AntigravityDev, null, FromChat));
    }

    private static string L(string key) => LocalizedResources.Instance[key];
    private static string LF(string key, params object[] a) => string.Format(L(key), a);

    public ObservableCollection<WizardStepViewModel> Steps { get; } = new();
    public ICommand PrimaryCommand { get; }
    public ICommand OtherCommand { get; }
    public ICommand CheckAgainCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand FinishCommand { get; }
    public ICommand UseAntigravityCommand { get; }

    public bool IsOpen { get => _open; private set => SetProperty(ref _open, value); }
    public AiProviderType Provider { get; private set; }
    /// <summary>Off in the harness, which feeds it made-up machines instead of this PC.</summary>
    internal bool CheckOnOpen { get; set; } = true;
    /// <summary>The chat sent the owner here: closing goes back to it.</summary>
    public bool FromChat { get; private set; }

    public string Title => LF("wizard.title", _setup.ProviderName(Provider));
    public string Subtitle => L("setup.why." + ProviderConnections.RowId(Provider));
    /// <summary>What is worth knowing about this provider (most have nothing: a missing key comes back as the key).</summary>
    public string Hint => L("wizard.hint." + Provider) is var hint && hint != "wizard.hint." + Provider ? hint : "";
    public bool HasHint => Hint.Length > 0;
    public bool CanUseAntigravity => Provider == AiProviderType.GeminiDev;
    public ImageSource? Hero => WizardArt.Image("hero.jpg", 960);
    public ImageSource? Girl => GirlArt.Image(ChatCharacter.ForProvider(Provider).Girl);

    private ConnectStep? Current => _connection?.Current;
    public bool IsDone => _connection is { Current: null };
    public bool IsChecking => !_fresh;
    public bool IsTesting => _testing;
    public string StepTitle => IsDone ? L("wizard.done.title") : Current is { } c ? L("wizard.step." + c.Kind) : L("setup.checking");
    public string StepBody => IsDone ? L(!ProviderConnections.HasTest(Provider) ? "wizard.done.loaded" : FromChat ? "wizard.done.chat" : "wizard.done.body")
        : _testFailed ? L("wizard.body.TestFailed") : Current is { } c ? L("wizard.body." + c.Kind) : "";
    public bool TestFailed => _testFailed;
    public string CloseLabel => L(FromChat ? "wizard.closeChat" : "wizard.close");
    public string FinishLabel => L(FromChat ? "wizard.closeChat" : "wizard.useThis");
    public ImageSource? StepIcon => IsDone ? WizardArt.Image("ready.png", 96) : Current is { } c ? WizardArt.StepIcon(c.Kind) : null;
    public string StepCount => _connection == null || IsDone ? ""
        : LF("wizard.stepOf", _connection.CurrentIndex + 1, _connection.Steps.Count);

    private bool HasAction => Current?.Action != null;
    private bool CanRetest => Current?.Kind == ConnectStepKind.Test && _testFailed;
    public bool HasPrimary => HasAction || CanRetest;
    public string PrimaryLabel => CanRetest ? L("wizard.retest") : Current?.Action is { } a
        ? L(a.LabelKey) + (a.SizeHint is { } s ? $" ({s})" : "") : "";

    /// <summary>The wrong key after a failed test: the owner can type it again from here.</summary>
    private SetupAction? KeyAgain => CanRetest && ProviderConnections.IsApiKey(Provider)
        ? new SetupAction(SetupActionKind.EnterKey, "wizard.keyAgain", Target: Provider.ToString()) : null;
    public bool HasOther => (Current?.Other ?? KeyAgain) != null;
    public string OtherLabel => (Current?.Other ?? KeyAgain) is { } a ? L(a.LabelKey) : "";

    /// <summary>What the button will run, shown before it is pressed.</summary>
    public string Commands => Current?.Action?.Commands is { Count: > 0 } lines ? string.Join("\n", lines) : "";
    public bool HasCommands => Commands.Length > 0;

    public bool IsBusy => _row?.IsBusy == true || _testing;
    public string Note => _testing ? L("wizard.testing") : _testMessage.Length > 0 ? _testMessage : _row?.Note ?? "";
    public bool HasNote => Note.Length > 0;
    private bool CanAct => !IsBusy && _fresh;

    /// <summary>Open on a provider. A wizard already open on another provider gives way (its test is cancelled).</summary>
    public void Open(AiProviderType provider, string? model, bool fromChat)
    {
        CancelTest();
        Provider = provider;
        _model = model;
        FromChat = fromChat;
        _connection = null;
        _tested = _testRan = _testFailed = _fresh = false;
        _testMessage = "";
        Steps.Clear();
        Watch(null);
        IsOpen = true;
        RaiseAll();
        // What the PC has now, not what it had when the page was last open.
        if (CheckOnOpen) _ = _owner.RecheckAsync();
    }

    /// <summary>New facts about the PC. <paramref name="fresh"/>: taken after this wizard opened.</summary>
    internal void Refresh(SetupFacts facts, bool fresh)
    {
        if (!IsOpen) return;
        _fresh |= fresh;
        var c = ProviderConnections.For(Provider, facts, _tested);
        _connection = c;
        if (Steps.Select(s => s.Kind).SequenceEqual(c.Steps.Select(s => s.Kind)) == false)
        {
            Steps.Clear();
            foreach (var s in c.Steps) Steps.Add(new WizardStepViewModel(s.Kind));
        }
        for (int i = 0; i < Steps.Count; i++)
        {
            Steps[i].IsDone = c.Steps[i].Done;
            Steps[i].IsCurrent = i == c.CurrentIndex;
        }
        Watch(c.Current is { } now ? _owner.RowOf(now.RowId) : null);
        RaiseAll();
        // The last step runs by itself, once: a failure stays on screen with a button to try again.
        if (_fresh && c.Current?.Kind == ConnectStepKind.Test && !_testRan) _ = TestAsync();
    }

    private void Watch(SetupRowViewModel? row)
    {
        if (ReferenceEquals(row, _row)) return;
        if (_row != null) _row.PropertyChanged -= OnRowChanged;
        _row = row;
        if (_row != null) _row.PropertyChanged += OnRowChanged;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SetupRowViewModel.IsBusy) or nameof(SetupRowViewModel.Note))) return;
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RunPrimary()
    {
        if (CanRetest) { _ = TestAsync(); return; }
        Run(Current?.Action);
    }

    private void Run(SetupAction? action)
    {
        if (action == null || _owner.RowOf(Current?.RowId ?? "") is not { } row) return;
        _testMessage = "";
        _owner.Do(row, action);
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
    }

    /// <summary>The provider's own test. A wizard that was closed or moved on meanwhile drops the answer.</summary>
    private async Task TestAsync()
    {
        if (_testing) return;
        _testRan = true;
        _testing = true;
        _testFailed = false;
        _testMessage = "";
        var cts = _test = new CancellationTokenSource(TestLimit);
        RaiseAll();
        bool ok = false;
        string message = "";
        try { (ok, message) = await _setup.TestProviderAsync(Provider, cts.Token); }
        catch (OperationCanceledException) { message = L("wizard.test.timeout"); }
        if (!ReferenceEquals(cts, _test)) { cts.Dispose(); return; }   // closed, or another provider now
        _test = null;
        cts.Dispose();
        _testing = false;
        _tested = ok;
        _testFailed = !ok;
        _testMessage = ok ? "" : LF("wizard.test.failed", message.Trim().Length > 0 ? Cap(message) : L("wizard.test.noDetail"));
        if (ok) SystemSetupService.RaiseToolsChanged();
        if (_owner.Facts is { } facts) Refresh(facts, fresh: true);
        else RaiseAll();
    }

    private static string Cap(string s)
    {
        s = (s ?? "").Trim();
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    private void CancelTest()
    {
        var cts = _test;
        _test = null;
        _testing = false;
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void Close()
    {
        CancelTest();
        Watch(null);
        IsOpen = false;
        _owner.OnWizardClosed(FromChat);
    }

    private void Finish()
    {
        CancelTest();
        Watch(null);
        IsOpen = false;
        _owner.OnWizardFinished(Provider, _model);
    }

    public void Relabel()
    {
        foreach (var s in Steps) s.Relabel();
        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var name in new[] { nameof(Title), nameof(Subtitle), nameof(Hint), nameof(HasHint), nameof(CanUseAntigravity), nameof(Girl), nameof(IsDone), nameof(TestFailed),
                     nameof(IsChecking), nameof(IsTesting), nameof(StepTitle), nameof(StepBody), nameof(StepIcon), nameof(StepCount), nameof(HasPrimary),
                     nameof(CloseLabel), nameof(FinishLabel), nameof(PrimaryLabel), nameof(HasOther), nameof(OtherLabel), nameof(Commands), nameof(HasCommands), nameof(IsBusy), nameof(Note), nameof(HasNote) })
            OnPropertyChanged(name);
        CommandManager.InvalidateRequerySuggested();
    }
}
