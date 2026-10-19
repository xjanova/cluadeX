using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using CluadeX.Services;
using CluadeX.Services.Connections;
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
    public bool IsConnection => _item.Group == SystemSetupService.GroupConnect;
    public string StateText => (IsConnection && _item.IsReady ? L("connect.state.connected") : L("setup.state." + _item.State))
                               + (_item.Optional || _item.IsReady ? "" : " · " + L("setup.required"));
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
        // a connection: where it lives, in words ("place:brainx|notLive")
        var d when d.StartsWith("place:", StringComparison.Ordinal) =>
            L("connect.place." + d[6..].Split('|')[0]) + (d.EndsWith("|notLive", StringComparison.Ordinal) ? L("connect.place.notLive") : ""),
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

    private readonly ConnectionService? _connections;
    private readonly BrainXInstaller _brainInstaller;
    private readonly Services.Mcp.McpServerManager? _mcp;

    public SetupViewModel(SystemSetupService setup, ConnectionService? connections = null, Services.Mcp.McpServerManager? mcp = null,
        DebugLogService? log = null)
    {
        _setup = setup;
        _connections = connections;
        _mcp = mcp;
        _brainInstaller = new BrainXInstaller(log);
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
                case SetupActionKind.Guide:
                    if (ConnectorCatalog.Find(action.Target) is { } g) ShowGuide(g);
                    break;
                case SetupActionKind.Connect:
                    if (ConnectorCatalog.Find(action.Target) is { } c) await ConnectAsync(row, c);
                    break;
                case SetupActionKind.Disconnect:
                    if (ConnectorCatalog.Find(action.Target) is { } d) await DisconnectAsync(row, d);
                    break;
                case SetupActionKind.InstallBrainX:
                    await InstallBrainXAsync(row);
                    break;
            }
        }
        catch (Exception ex)
        {
            row.IsBusy = false;
            row.Note = ex switch
            {
                InvalidOperationException { Message: "inside-package" } => L("setup.banner.insidePackage"),
                // an error key from the connection / installer services: their own words
                InvalidOperationException { Message: var key } when key.StartsWith("connect.", StringComparison.Ordinal)
                                                                  || key.StartsWith("brainx.", StringComparison.Ordinal) => L(key),
                System.Net.Http.HttpRequestException or TaskCanceledException => L("connect.err.download"),
                _ => L("setup.note.failed"),
            };
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Connections
    // ═══════════════════════════════════════════════════════════════════

    private static string Title(ConnectorDef c) => L(c.TitleKey);

    /// <summary>"How it works": the plain steps, and a button to the service's page (its keys / sign-in).</summary>
    private static void ShowGuide(ConnectorDef c)
    {
        int choice = Views.Editor.ChoiceDialog.Show(LF("connect.guide.title", Title(c)), L(c.GuideKey),
            new[] { L("connect.guide.ok"), L("connect.guide.open") });
        if (choice == 1) OpenHttps(c.Auth == ConnectorAuth.ApiKey ? c.KeyPage : c.Docs);
    }

    private static void OpenHttps(string url)
    {
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private ConnectorFacts? FactsFor(ConnectorDef c) => _facts?.Connections.FirstOrDefault(x => x.Def.Id == c.Id);

    /// <summary>Everything a connection needs, in order — install a missing tool, sign in / take the key, download the
    /// server — then place it (BrainX first). Each step that runs something shows it first and waits for a yes.</summary>
    private async Task ConnectAsync(SetupRowViewModel row, ConnectorDef c)
    {
        if (_connections == null) { row.Note = L("connect.err.noMcp"); return; }
        var f = FactsFor(c);
        if (f == null) return;

        // the steps in plain words, and where it will go — one yes before anything happens
        string where = L(f.BrainAvailable ? "connect.place.willBrainx" : "connect.place.willCluadex");
        if (Views.Editor.ChoiceDialog.Show(LF("connect.guide.title", Title(c)), L(c.GuideKey) + "\n\n" + where,
                new[] { L("connect.confirm.go"), L("connect.confirm.cancel") }) != 0) return;

        switch (c.Auth)
        {
            case ConnectorAuth.GhCli:
                if (!f.GhInstalled)
                {
                    if (!await RunStepAsync(row, "GitHub CLI", new[] { SystemSetupService.WingetInstall("GitHub.cli") }, "connect.note.ghInstall")) return;
                    if (ConnectorLauncher.FindGh() == null) { row.Note = L("connect.note.stillNoGh"); return; }
                }
                if (!f.GhSignedIn)
                {
                    string gh = ConnectorLauncher.FindGh()!;
                    if (!await RunStepAsync(row, "GitHub sign-in", new[] { $"\"{gh}\" auth login --web --hostname github.com --git-protocol https" }, "connect.note.ghSignIn"))
                        return;
                    if (await Task.Run(() => ConnectorLauncher.GhToken(gh)) == null) { row.Note = L("connect.note.stillNoSignIn"); return; }
                }
                if (!f.ServerReady)
                {
                    row.IsBusy = true;
                    row.Note = L("connect.note.download");
                    await _connections.DownloadGitHubServerAsync();
                }
                break;

            case ConnectorAuth.ApiKey:
                if (f.Node == null)
                {
                    if (!await RunStepAsync(row, "Node.js", new[] { SystemSetupService.WingetInstall("OpenJS.NodeJS.LTS") }, "connect.note.nodeInstall")) return;
                    if (ConnectorLauncher.FindNpx() == null) { row.Note = L("connect.note.stillNoNode"); return; }
                }
                string? key = Services.Helpers.InputDialog.Show(LF("connect.key.title", Title(c)), L("connect.key.prompt"),
                    placeholder: string.Join(" / ", c.KeyPrefixes.Take(2)) + "…", secret: true,
                    okLabel: L("connect.key.save"), cancelLabel: L("connect.confirm.cancel"));
                if (key == null) return;
                if (!ConnectorCatalog.LooksLikeKey(c, key))
                {
                    row.Note = LF("connect.key.bad", Title(c), string.Join(", ", c.KeyPrefixes));
                    return;
                }
                if (ConnectorCatalog.IsLiveSecretKey(key.Trim())
                    && Views.Editor.ChoiceDialog.Show(L("connect.key.liveTitle"), L("connect.key.liveBody"),
                        new[] { L("connect.confirm.cancel"), L("connect.key.useAnyway") }) != 1) return;
                _connections.Secrets.Set(c.Id, key.Trim());
                break;

            case ConnectorAuth.Browser:
                if (f.Node == null || f.Node < ConnectorCatalog.CanvaMinNode)
                {
                    string cmd = f.Node == null ? SystemSetupService.WingetInstall("OpenJS.NodeJS.LTS")
                        : "winget upgrade --id OpenJS.NodeJS.LTS -e --source winget --accept-package-agreements --accept-source-agreements";
                    if (!await RunStepAsync(row, "Node.js", new[] { cmd }, "connect.note.nodeInstall")) return;
                    var npx = ConnectorLauncher.FindNpx();
                    if (npx == null) { row.Note = L("connect.note.stillNoNode"); return; }
                }
                break;
        }

        row.IsBusy = true;
        row.Note = L("connect.note.registering");
        var place = await _connections.RegisterAsync(c);
        row.IsBusy = false;
        await RecheckAsync();
        SystemSetupService.RaiseToolsChanged();
        row.Note = L(place == ConnectorPlace.BrainX ? "connect.note.brainx" : "connect.note.cluadex")
                   + (c.Auth == ConnectorAuth.Browser ? L("connect.note.canvaFirst") : "");
    }

    /// <summary>One install / sign-in step in the console window (shown first, then confirmed). False when it did not finish.</summary>
    private async Task<bool> RunStepAsync(SetupRowViewModel row, string title, IReadOnlyList<string> commands, string noteKey)
    {
        if (_facts!.InsidePackage) { row.Note = L("setup.banner.insidePackage"); return false; }
        string message = LF("setup.confirm.body", title, "?") + "\n\n" + string.Join("\n", commands.Select(x => "  " + x))
                         + "\n\n" + L(commands.Any(x => x.StartsWith("winget", StringComparison.Ordinal)) ? "setup.confirm.winget" : "setup.confirm.console");
        if (Views.Editor.ChoiceDialog.Show(LF("setup.confirm.title", title), message, new[] { L("setup.confirm.run"), L("setup.confirm.cancel") }) != 0)
            return false;
        row.IsBusy = true;
        row.Note = L(noteKey);
        int code = await _setup.RunInConsoleAsync(title, commands);
        row.IsBusy = false;
        if (code != 0) { row.Note = LF("setup.note.installFailed", code); return false; }
        return true;
    }

    private async Task DisconnectAsync(SetupRowViewModel row, ConnectorDef c)
    {
        if (_connections == null) return;
        if (Views.Editor.ChoiceDialog.Show(LF("connect.disconnect.title", Title(c)), LF("connect.disconnect.body", Title(c)),
                new[] { L("connect.confirm.cancel"), L("setup.act.disconnect") }) != 1) return;
        row.IsBusy = true;
        await _connections.DisconnectAsync(c);
        row.IsBusy = false;
        await RecheckAsync();
        SystemSetupService.RaiseToolsChanged();
        row.Note = L("connect.note.disconnected");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BrainX in one click
    // ═══════════════════════════════════════════════════════════════════

    private async Task InstallBrainXAsync(SetupRowViewModel row)
    {
        if (_facts!.InsidePackage) { row.Note = L("setup.banner.insidePackage"); return; }
        if (Views.Editor.ChoiceDialog.Show(L("brainx.confirm.title"), L("brainx.confirm.body"),
                new[] { L("brainx.confirm.go"), L("setup.confirm.cancel") }) != 0) return;

        row.IsBusy = true;
        string? file = null;
        try
        {
            var progress = new Progress<(long Done, long? Total)>(p =>
                row.Note = LF("brainx.note.downloading", p.Total is { } t && t > 0 ? $"{p.Done * 100 / t}%" : $"{p.Done / 1048576} MB"));
            try { file = await _brainInstaller.DownloadAsync(progress); }
            catch (InvalidOperationException ex) when (ex.Message == BrainXInstaller.ComingSoon)
            {
                row.Note = L("brainx.note.comingSoon");   // closed on purpose: say so — no page, no error
                return;
            }
            catch (InvalidOperationException ex) when (ex.Message is "brainx.err.host" or "brainx.err.notFile")
            {
                row.Note = L(ex.Message);
                OpenHttps(BrainXInstaller.DownloadUrl);   // the page, for the user to take it from there
                return;
            }

            row.Note = L("brainx.note.checking");
            var sig = await Task.Run(() => BrainXInstaller.VerifySignature(file));
            if (sig == SignatureCheck.Invalid) { row.Note = L("brainx.err.signature"); return; }
            if (sig == SignatureCheck.Unsigned)
            {
                string sha = await Task.Run(() => BrainXInstaller.Sha256(file));
                if (Views.Editor.ChoiceDialog.Show(L("brainx.unsigned.title"), LF("brainx.unsigned.body", sha),
                        new[] { L("setup.confirm.cancel"), L("brainx.unsigned.run") }) != 1) return;
            }

            row.Note = L("brainx.note.running");
            await _brainInstaller.RunAsync(file);

            // BrainX starts after its installer; it puts brainx-mcp.exe in place on its first run. Then connect, and move
            // the connections CluadeX kept in its own list into BrainX (BrainX first).
            row.Note = L("brainx.note.waiting");
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            while (!limit.IsCancellationRequested && Services.Mcp.McpServerManager.FindInstalledBrainExe() == null)
                await Task.Delay(3000, limit.Token).ContinueWith(_ => { });
            if (Services.Mcp.McpServerManager.FindInstalledBrainExe() == null) { row.Note = L("brainx.note.notYet"); return; }
            if (_mcp != null && !_mcp.IsBrainConnected) await _mcp.ConnectInstalledBrainAsync();
            if (_connections != null) await _connections.ReconcileAsync();
            await RecheckAsync();
            SystemSetupService.RaiseToolsChanged();
            row.Note = L("brainx.note.done");
        }
        finally
        {
            row.IsBusy = false;
            if (file != null) BrainXInstaller.TryDelete(file);
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
