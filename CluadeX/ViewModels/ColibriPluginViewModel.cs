using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CluadeX.Services;
using CluadeX.Services.Colibri;
using CluadeX.Services.Helpers;

namespace CluadeX.ViewModels;

/// <summary>One model row on the colibri card.</summary>
public sealed class ColibriModelItem
{
    public required ColibriModelOption Option { get; init; }
    public required string Line { get; init; }
    public required string Runs { get; init; }
    public string? NoFit { get; init; }
    public bool Recommended => Option.Recommended;
    public bool Fits => Option.Fits;
    public string Name => Option.Name;
    public string Summary => Option.Summary;
}

/// <summary>
/// The colibri card on the Plugins page: install / update / roll back the engine, pick and download a model, start and
/// stop the server, and the switch that lets read-only sub-agents run on it. All the work is in <see cref="ColibriService"/>;
/// this keeps the card's words current and polls `coli status` while something is in flight (a download, a model loading).
/// </summary>
public sealed class ColibriPluginViewModel : ViewModelBase
{
    private readonly ColibriService _svc;
    private readonly SettingsService _settings;
    private readonly LocalizationService _loc;
    private readonly DebugLogService? _log;
    private readonly DispatcherTimer _poll;
    private ColibriRelease? _latest;
    private bool _loaded, _polling, _startAfterSetup;
    private CancellationTokenSource? _cts;

    public ColibriPluginViewModel(ColibriService svc, SettingsService settings, LocalizationService loc, DebugLogService? log = null)
    {
        _svc = svc;
        _settings = settings;
        _loc = loc;
        _log = log;
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _poll.Tick += async (_, _) => await PollAsync();
        _loc.LanguageChanged += () => { RebuildModelItems(); RefreshTexts(); };

        InstallCommand = new AsyncRelayCommand(InstallAsync, () => !IsBusy && !IsInstalled);
        CheckUpdateCommand = new AsyncRelayCommand(() => CheckUpdateAsync(quiet: false), () => !IsBusy && IsInstalled);
        UpdateCommand = new AsyncRelayCommand(UpdateAsync, () => !IsBusy && UpdateAvailable);
        RollbackCommand = new AsyncRelayCommand(RollbackAsync, () => !IsBusy && CanRollback);
        LoadModelsCommand = new AsyncRelayCommand(LoadModelsAsync, () => !IsBusy && IsInstalled && !InstallRunning);
        InstallModelCommand = new AsyncRelayCommand(InstallModelAsync, () => !IsBusy && IsInstalled && !InstallRunning && SelectedModel is { Fits: true });
        ChangeDirCommand = new RelayCommand(ChangeDir, () => !IsBusy && !InstallRunning);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy && IsInstalled && Status.Configured && !Status.IsReady && !Status.IsLoading && !InstallRunning);
        StopCommand = new AsyncRelayCommand(StopAsync, () => !IsBusy && (Status.IsReady || Status.IsLoading));
        DashboardCommand = new RelayCommand(OpenDashboard, () => Status.IsReady && !string.IsNullOrEmpty(Status.DashboardUrl));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        InstallPythonCommand = new RelayCommand(InstallPython);
        UninstallCommand = new AsyncRelayCommand(UninstallAsync, () => !IsBusy && IsInstalled);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy && _cts != null);
        RefreshTexts();
    }

    public ICommand InstallCommand { get; }
    public ICommand CheckUpdateCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand RollbackCommand { get; }
    public ICommand LoadModelsCommand { get; }
    public ICommand InstallModelCommand { get; }
    public ICommand ChangeDirCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand DashboardCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand InstallPythonCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand CancelCommand { get; }

    // ─── state the card shows ───

    /// <summary>น้อง Colibri: her portrait and colour on the card.</summary>
    public ChatCharacter Mascot => ChatCharacter.ForProvider(CluadeX.Models.AiProviderType.Colibri);

    public bool IsInstalled => _svc.IsInstalled;
    public string? InstalledTag => _svc.State.InstalledTag;
    public bool CanRollback => _svc.State.PreviousTag is { } p && _svc.EngineDir(p) is { } d && File.Exists(Path.Combine(d, "coli"));
    public bool UpdateAvailable => IsInstalled && _latest != null && ColibriFacts.IsNewer(_latest.Tag, InstalledTag);
    public string UpdateLabel => _loc.T("colibri.update", _latest?.Tag ?? "");
    public string RollbackLabel => _loc.T("colibri.rollback", _svc.State.PreviousTag ?? "");

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); } }

    private double? _progress;
    /// <summary>0-1 while a download is measurable, null otherwise (the bar goes indeterminate).</summary>
    public double? Progress { get => _progress; private set { SetProperty(ref _progress, value); OnPropertyChanged(nameof(ProgressValue)); OnPropertyChanged(nameof(ProgressKnown)); OnPropertyChanged(nameof(ProgressUnknown)); } }
    public double ProgressValue => (_progress ?? 0) * 100;
    public bool ProgressKnown => _progress != null;
    public bool ProgressUnknown => _progress == null;

    private bool? _hasPython;
    /// <summary>Null until checked.</summary>
    public bool? HasPython { get => _hasPython; private set { SetProperty(ref _hasPython, value); OnPropertyChanged(nameof(NoPython)); } }
    public bool NoPython => _hasPython == false;

    private ColibriStatus _status = new();
    public ColibriStatus Status
    {
        get => _status;
        private set
        {
            _status = value;
            foreach (var n in new[] { nameof(Status), nameof(InstallRunning), nameof(IsReady), nameof(IsRunning), nameof(CanStartShown) })
                OnPropertyChanged(n);
            RefreshTexts();
        }
    }
    public bool InstallRunning => _status.InstallRunning;
    public bool IsReady => _status.IsReady;
    /// <summary>The server is up or loading: Stop shows, Start hides.</summary>
    public bool IsRunning => _status.IsReady || _status.IsLoading;
    public bool CanStartShown => IsInstalled && !IsRunning;

    private string _headline = "", _detail = "", _message = "", _updateText = "", _dirWarning = "";
    public string Headline { get => _headline; private set => SetProperty(ref _headline, value); }
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    /// <summary>The last thing that happened (an error, "up to date"…).</summary>
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string UpdateText { get => _updateText; private set => SetProperty(ref _updateText, value); }
    public string DirWarning { get => _dirWarning; private set { SetProperty(ref _dirWarning, value); OnPropertyChanged(nameof(HasDirWarning)); } }
    public bool HasDirWarning => _dirWarning.Length > 0;

    private string _modelsDir = "";
    public string ModelsDir { get => _modelsDir; private set => SetProperty(ref _modelsDir, value); }

    private List<ColibriModelOption> _options = new();
    public ObservableCollection<ColibriModelItem> Models { get; } = new();
    public bool HasModels => Models.Count > 0;
    public bool NoChatFits => _options.Count > 0 && ColibriFacts.DefaultPick(_options) == null;

    private ColibriModelItem? _selected;
    public ColibriModelItem? SelectedModel { get => _selected; set { if (SetProperty(ref _selected, value)) CommandManager.InvalidateRequerySuggested(); } }

    /// <summary>Owner's tier routing: read-only cheap (and, with a ≥30B model, mid) sub-agents run on the local model.</summary>
    public bool UseForAgents
    {
        get => _settings.Settings.SubAgentLocalCheap;
        set
        {
            if (_settings.Settings.SubAgentLocalCheap == value) return;
            _settings.Settings.SubAgentLocalCheap = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    // ─── loading ───

    /// <summary>The Plugins page opened. The first time: read the status (it runs Python, so not at app start) and, at
    /// most once a day, ask GitHub whether a newer colibri is out.</summary>
    public async void EnsureLoaded()
    {
        OnPropertyChanged(nameof(UseForAgents));
        if (_loaded) return;
        _loaded = true;
        try
        {
            await RefreshAsync();
            if (IsInstalled && (_svc.State.LastUpdateCheck is not { } last || DateTime.Now - last > TimeSpan.FromDays(1)))
                await CheckUpdateAsync(quiet: true);
        }
        catch (Exception ex) { _log?.Warn("Colibri", "first load", ex); }
    }

    private async Task RefreshAsync()
    {
        ModelsDir = await Task.Run(() => _svc.ModelsDir);
        DirWarning = await Task.Run(() => ColibriService.IsSlowDrive(ModelsDir)) ? _loc.T("colibri.dirE") : "";
        HasPython = await _svc.FindPythonAsync() != null;
        if (IsInstalled && HasPython == true)
        {
            try { Status = await _svc.StatusAsync(); _svc.ApplyProvider(Status); }
            catch (Exception ex) { Message = ErrorText(ex); }
        }
        else Status = new ColibriStatus();
        if (_latest == null && _svc.State.LatestSeenTag is { } seen && ColibriFacts.IsNewer(seen, InstalledTag))
            UpdateText = _loc.T("colibri.updateFound", seen, InstalledTag ?? "");
        Touch();
        SyncPolling();
    }

    // ─── engine ───

    private async Task InstallAsync()
    {
        if (await _svc.FindPythonAsync() == null) { HasPython = false; Message = _loc.T("colibri.noPython"); return; }
        HasPython = true;
        await Busy(async ct =>
        {
            var release = await _svc.CheckLatestAsync(ct) ?? throw new InvalidOperationException("colibri.err.noAsset");
            _latest = release;
            await _svc.InstallEngineAsync(release, ProgressSink(), ct);
            Message = _loc.T("colibri.installed", release.Tag);
        });
        if (IsInstalled) { await RefreshAsync(); await LoadModelsAsync(); }
    }

    private async Task CheckUpdateAsync(bool quiet)
    {
        try
        {
            _latest = await _svc.CheckLatestAsync();
            UpdateText = _latest == null ? "" : ColibriFacts.IsNewer(_latest.Tag, InstalledTag)
                ? _loc.T("colibri.updateFound", _latest.Tag, InstalledTag ?? "")
                : _loc.T("colibri.upToDate", InstalledTag ?? "");
        }
        catch (Exception ex)
        {
            if (!quiet) Message = ErrorText(ex);
            _log?.Warn("Colibri", "update check", ex);
        }
        Touch();
    }

    /// <summary>
    /// The update, in the order that never leaves her half-installed: check the new release's checksum while the old engine
    /// still runs, stop the server, switch the engine folder, re-run the setup for the SAME model with the new engine
    /// (colibri's config remembers the launcher path, and the model files are already there — nothing downloads again),
    /// then start her again if she was running. The old engine stays for <see cref="RollbackAsync"/>.
    /// </summary>
    private async Task UpdateAsync()
    {
        if (_latest == null) return;
        bool wasRunning = Status.IsReady || Status.IsLoading;
        var release = _latest;
        await Busy(async ct =>
        {
            await _svc.StopServerAsync(ct);
            await _svc.InstallEngineAsync(release, ProgressSink(), ct);
            await ResetupAsync(wasRunning, ct);
            Message = _loc.T("colibri.installed", release.Tag);
            UpdateText = _loc.T("colibri.upToDate", release.Tag);
        });
        await RefreshAsync();
    }

    private async Task RollbackAsync()
    {
        bool wasRunning = Status.IsReady || Status.IsLoading;
        await Busy(async ct =>
        {
            await _svc.StopServerAsync(ct);
            if (!_svc.Rollback()) return;
            await ResetupAsync(wasRunning, ct);
            Message = _loc.T("colibri.rolledBack", InstalledTag ?? "");
            UpdateText = "";
        });
        await RefreshAsync();
    }

    /// <summary>After the engine changed: the same model, set up again by the engine now in use.</summary>
    private async Task ResetupAsync(bool startAfter, CancellationToken ct)
    {
        if (_svc.State.ModelId is not { } model) return;
        await _svc.StartModelSetupAsync(model, ct);
        _startAfterSetup = startAfter;
        await Task.Delay(1500, ct);   // let the setup write its first state before the poll reads it
    }

    // ─── models ───

    private async Task LoadModelsAsync()
    {
        await Busy(async ct =>
        {
            Detail = _loc.T("colibri.loadingModels");
            _options = await _svc.ListModelsAsync(ct);
            RebuildModelItems();
            var pick = ColibriFacts.DefaultPick(_options);
            SelectedModel = Models.FirstOrDefault(m => m.Option.Id == (_svc.State.ModelId ?? pick?.Id))
                            ?? Models.FirstOrDefault(m => m.Option.Id == pick?.Id);
        });
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(NoChatFits));
        RefreshTexts();
    }

    private void RebuildModelItems()
    {
        string? keep = SelectedModel?.Option.Id;
        Models.Clear();
        foreach (var o in _options.Where(o => o.IsChat))
            Models.Add(new ColibriModelItem
            {
                Option = o,
                Line = _loc.T("colibri.modelLine", o.DownloadGb.ToString("0.#") + " GB", o.RamMinGb.ToString("0"), o.RamGoodGb.ToString("0")),
                Runs = _loc.T(o.FromRam ? "colibri.fromRam" : "colibri.streams") + (o.Recommended ? " · " + _loc.T("colibri.recommended") : ""),
                NoFit = o.Fits ? null : _loc.T("colibri.noFit", o.Reason.Length > 0 ? o.Reason : o.Status),
            });
        if (keep != null) SelectedModel = Models.FirstOrDefault(m => m.Option.Id == keep);
        OnPropertyChanged(nameof(HasModels));
    }

    private async Task InstallModelAsync()
    {
        if (SelectedModel?.Option is not { } m) return;
        string ok = _loc.T("colibri.confirmModel", m.Name, m.DownloadGb.ToString("0.#") + " GB", ModelsDir,
            m.LicenseNote.Length > 0 ? m.LicenseNote : _loc.T("colibri.licenseNone"));
        if (MessageBox.Show(ok, "Colibri", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await Busy(async ct =>
        {
            if (Status.IsReady || Status.IsLoading) await _svc.StopServerAsync(ct);
            await _svc.StartModelSetupAsync(m.Id, ct);
            _startAfterSetup = true;
            await Task.Delay(1500, ct);
        });
        await RefreshAsync();
    }

    private void ChangeDir()
    {
        string? dir = FolderPicker.ShowDialog(_loc.T("colibri.modelsDir"), Directory.Exists(ModelsDir) ? ModelsDir : null);
        if (string.IsNullOrWhiteSpace(dir)) return;
        // It goes on a cmd line: % would expand, a quote would end the argument.
        if (dir.IndexOfAny(new[] { '%', '"', '^', '!' }) >= 0) { Message = _loc.T("colibri.err.generic", dir); return; }
        _svc.State.ModelsDir = dir;
        try { _svc.SaveState(); } catch (Exception ex) { Message = ErrorText(ex); return; }
        ModelsDir = dir;
        DirWarning = ColibriService.IsSlowDrive(dir) ? _loc.T("colibri.dirE") : "";
        _options.Clear();
        Models.Clear();
        OnPropertyChanged(nameof(HasModels));
    }

    // ─── server ───

    private async Task StartAsync()
    {
        await Busy(async ct => { Status = await _svc.StartServerAsync(ct); });
        SyncPolling();
    }

    private async Task StopAsync()
    {
        _startAfterSetup = false;
        await Busy(async ct => await _svc.StopServerAsync(ct));
        await RefreshAsync();
    }

    private void OpenDashboard()
    {
        if (Status.DashboardUrl is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.IsLoopback) return;
        try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { Message = ErrorText(ex); }
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var s = await _svc.StatusAsync();
            Status = s;
            if (s.IsReady) _svc.ApplyProvider(s);
            // The setup finished (the model is in place) — start her if that is what the user was waiting for.
            if (_startAfterSetup && !s.InstallRunning && s.Configured && s.InstallPhase != "error" && s.ServerState == "stopped")
            {
                _startAfterSetup = false;
                try { Status = await _svc.StartServerAsync(); }
                catch (Exception ex) { Message = ErrorText(ex); }
            }
            else if (_startAfterSetup && !s.InstallRunning && s.InstallPhase == "error") _startAfterSetup = false;
        }
        catch (Exception ex) { _log?.Warn("Colibri", "status poll", ex); }
        finally
        {
            _polling = false;
            SyncPolling();
            Touch();
        }
    }

    private void SyncPolling()
    {
        bool want = IsInstalled && (Status.InstallRunning || Status.IsLoading || _startAfterSetup);
        if (want && !_poll.IsEnabled) _poll.Start();
        else if (!want && _poll.IsEnabled) _poll.Stop();
    }

    // ─── python, uninstall ───

    /// <summary>Opens winget in its own console so the user sees (and answers) its prompts — nothing is accepted for them.</summary>
    private void InstallPython()
    {
        try { Process.Start(new ProcessStartInfo("winget", "install -e --id Python.Python.3.12 --scope user") { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Message = ErrorText(ex);
            try { Process.Start(new ProcessStartInfo("https://www.python.org/downloads/windows/") { UseShellExecute = true }); } catch { }
        }
    }

    private async Task UninstallAsync()
    {
        var answer = MessageBox.Show(_loc.T("colibri.uninstallConfirm", ModelsDir), "Colibri",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.Cancel) return;
        _startAfterSetup = false;
        await Busy(async ct => await _svc.UninstallAsync(models: answer == MessageBoxResult.Yes, ct));
        _options.Clear();
        Models.Clear();
        _latest = null;
        UpdateText = "";
        await RefreshAsync();
    }

    // ─── plumbing ───

    private async Task Busy(Func<CancellationToken, Task> work)
    {
        IsBusy = true;
        Message = "";
        _cts = new CancellationTokenSource();
        try { await work(_cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Message = ErrorText(ex);
            _log?.Error("Colibri", "action failed", ex);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            Progress = null;
            IsBusy = false;
            Touch();
        }
    }

    private IProgress<(string Step, double? Fraction)> ProgressSink() => new Progress<(string Step, double? Fraction)>(p =>
    {
        Progress = p.Fraction;
        Detail = _loc.T("colibri.installing", p.Fraction is { } f ? $"{f:P0}" : p.Step);
    });

    private string ErrorText(Exception ex) => ex switch
    {
        InvalidOperationException { Message: var m } when m.StartsWith("colibri.err.", StringComparison.Ordinal) => _loc.T(m),
        HttpRequestException h => _loc.T("colibri.err.network", h.Message),
        _ => _loc.T("colibri.err.generic", ex.Message),
    };

    private void Touch()
    {
        foreach (var n in new[] { nameof(IsInstalled), nameof(InstalledTag), nameof(CanRollback), nameof(UpdateAvailable),
                     nameof(UpdateLabel), nameof(RollbackLabel), nameof(InstallRunning), nameof(IsReady), nameof(IsRunning), nameof(CanStartShown) })
            OnPropertyChanged(n);
        RefreshTexts();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshTexts()
    {
        var s = _status;
        Headline = !IsInstalled ? _loc.T("colibri.notInstalled") : _loc.T("colibri.installed", InstalledTag ?? "");
        if (IsBusy) return;   // the progress line owns Detail while an action runs
        string model = s.Model ?? _svc.State.ModelId ?? "";
        Detail = !IsInstalled ? "" :
            s.InstallRunning && s.InstallPhase == "download" && s.InstallTotal > 0
                ? _loc.T("colibri.state.download", ColibriFacts.Bytes(s.InstallDone), ColibriFacts.Bytes(s.InstallTotal), ColibriFacts.Bytes(s.InstallRate))
            : s.InstallRunning ? _loc.T("colibri.state.setup", s.InstallMessage ?? s.InstallPhase ?? "…")
            : s.InstallPhase == "error" ? _loc.T("colibri.state.error", s.InstallMessage ?? _svc.LogTail(_svc.SetupLogPath))
            : s.IsReady ? _loc.T("colibri.state.ready", s.ModelId ?? model)
                          + (s.TokensPerSecond is > 0 ? " · " + _loc.T("colibri.state.speed", s.TokensPerSecond.Value.ToString("0.0")) : "")
            : s.IsLoading ? _loc.T("colibri.state.loading", model)
            : s.Configured ? _loc.T("colibri.state.stopped", model)
            : _loc.T("colibri.state.none");
        if (s.InstallRunning && s.InstallFraction is { } f) Progress = f;
        else if (!IsBusy) Progress = null;
    }
}
