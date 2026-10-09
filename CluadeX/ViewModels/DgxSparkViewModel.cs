using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>One model row on the DGX Spark window — a recommended pick, or something already on the Spark.</summary>
public sealed class SparkModelRow : ViewModelBase
{
    public string Tag { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>"≈ 65 GB · ≈ 39–55 tok/s" for a recommended pick, empty for a model we know nothing about.</summary>
    public string Facts { get; init; } = "";
    public string UseText { get; init; } = "";
    public bool IsRecommended { get; init; }

    private bool _isInstalled, _isPulling, _isInUse;
    private double _progress;
    private string _pullStatus = "";
    internal CancellationTokenSource? PullCts;
    /// <summary>Which download this row is running (new for every start) — a "stop it" answer covers these ids only.</summary>
    internal long PullId;

    public bool IsInstalled { get => _isInstalled; set { if (SetProperty(ref _isInstalled, value)) Notify(); } }
    public bool IsPulling { get => _isPulling; set { if (SetProperty(ref _isPulling, value)) Notify(); } }
    public bool IsInUse { get => _isInUse; set { if (SetProperty(ref _isInUse, value)) Notify(); } }
    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }
    public string PullStatus { get => _pullStatus; set => SetProperty(ref _pullStatus, value); }

    public bool CanPull => !IsInstalled && !IsPulling;
    public bool CanUse => IsInstalled && !IsPulling && !IsInUse;

    private void Notify()
    {
        OnPropertyChanged(nameof(CanPull));
        OnPropertyChanged(nameof(CanUse));
    }
}

/// <summary>
/// The DGX Spark window: find the Spark on the network, download a model onto it, make it CluadeX's model.
/// The only purchase link is the XMAN Studio campaign page (<see cref="DgxSparkService.CampaignUrl"/>).
/// </summary>
public class DgxSparkViewModel : ViewModelBase
{
    private readonly DgxSparkService _spark;
    private readonly SettingsService _settings;
    private readonly LocalizationService _loc;
    private CancellationTokenSource? _probeCts;
    private bool _closed;

    public DgxSparkViewModel(DgxSparkService spark, SettingsService settings, LocalizationService loc)
    {
        _spark = spark;
        _settings = settings;
        _loc = loc;
        _hostInput = settings.Settings.DgxSparkHost ?? "";

        // Connecting again rebuilds the rows, which would orphan a running download — so not while one runs.
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsProbing && !IsAnyPulling);
        // One download per row, several rows at once: the row's own IsPulling is the guard (CanPull). An
        // AsyncRelayCommand here was a single command for every row, and its "executing" flag disabled them all.
        PullCommand = new RelayCommand<SparkModelRow>(r => _ = PullAsync(r), r => r is { CanPull: true } && IsConnected && !IsProbing);
        CancelPullCommand = new RelayCommand<SparkModelRow>(r => r?.PullCts?.Cancel());
        UseCommand = new AsyncRelayCommand<SparkModelRow>(UseAsync, r => r is { CanUse: true } && IsConnected && !IsSwitching);
        OpenCampaignCommand = new RelayCommand(OpenCampaign);

        ShowRows([]);
        RefreshActive();
        // CluadeX leaving the Spark shrinks the context window again — say so here, also when it happened before
        // this window opened.
        _spark.ContextRestored += OnContextRestored;
        if (!_spark.IsActive && _spark.LastContextRestore is { } earlier) ContextNote = DescribeRestore(earlier);
        // A remembered Spark is looked up straight away, so the window opens on its real state.
        if (!string.IsNullOrWhiteSpace(_hostInput)) _ = ConnectAsync();
    }

    private string T(string key) => _loc.T(key);
    private string T(string key, params object[] args) => _loc.T(key, args);

    // ── State ──
    private string _hostInput, _status = "", _activeLine = "";
    private bool _isProbing, _isConnected, _statusIsError, _isSwitching;
    private string? _connectedHost;

    public string HostInput { get => _hostInput; set => SetProperty(ref _hostInput, value); }
    public string Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(_status);
    public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }
    public bool IsProbing { get => _isProbing; private set => SetProperty(ref _isProbing, value); }
    public bool IsConnected { get => _isConnected; private set => SetProperty(ref _isConnected, value); }
    public bool IsSwitching { get => _isSwitching; private set => SetProperty(ref _isSwitching, value); }
    public string ActiveLine { get => _activeLine; private set { if (SetProperty(ref _activeLine, value)) OnPropertyChanged(nameof(HasActiveLine)); } }
    public bool HasActiveLine => !string.IsNullOrEmpty(_activeLine);

    private string _contextNote = "";
    /// <summary>Why the context window is smaller again: CluadeX left the Spark and gave the old size back.
    /// Empty while on the Spark.</summary>
    public string ContextNote { get => _contextNote; private set { if (SetProperty(ref _contextNote, value)) OnPropertyChanged(nameof(HasContextNote)); } }
    public bool HasContextNote => !string.IsNullOrEmpty(_contextNote);

    public ObservableCollection<SparkModelRow> Models { get; } = [];
    /// <summary>A download onto the Spark is running (closing the window would stop it).</summary>
    public bool IsAnyPulling => Models.Any(m => m.IsPulling);

    private long _pullSeq;
    /// <summary>The downloads the user already agreed to stop (by id). A download started after that answer is not
    /// covered — closing asks again.</summary>
    private readonly HashSet<long> _stopAgreed = [];
    private bool _closingForRestart;

    /// <summary>What to ask before the window closes while downloads run; null when nothing is downloading, when the
    /// user already agreed to stop every download that is running, or while the app restarts for an update.</summary>
    public (string Title, string Body, string Stop, string Keep)? CloseQuestion()
    {
        if (_closingForRestart) return null;
        var running = Models.Where(m => m.IsPulling).ToList();
        if (running.Count == 0 || running.All(m => _stopAgreed.Contains(m.PullId))) return null;
        var names = running.Select(m => "  • " + m.Name);
        return (T("spark.closeTitle"), T("spark.closeBody", string.Join("\n", names)), T("spark.closeStop"), T("spark.closeKeep"));
    }

    /// <summary>The user answered "stop and close": that covers the downloads running now — not later ones.</summary>
    public void AgreeToStopRunningDownloads()
    {
        foreach (var m in Models)
            if (m.IsPulling) _stopAgreed.Add(m.PullId);
    }

    /// <summary>The close the answer was for didn't happen (an update that failed or was cancelled): the next close asks again.</summary>
    public void ResetCloseConfirmation()
    {
        _stopAgreed.Clear();
        _closingForRestart = false;
    }

    /// <summary>The app is shutting down for an update right now (Application.Shutdown ignores Cancel): the user has
    /// answered for the downloads already, so closing must not ask again.</summary>
    public void SetClosingForRestart(bool restarting) => _closingForRestart = restarting;

    public ICommand ConnectCommand { get; }
    public ICommand PullCommand { get; }
    public ICommand CancelPullCommand { get; }
    public ICommand UseCommand { get; }
    public ICommand OpenCampaignCommand { get; }

    /// <summary>The window closed: stop the lookup and every download it started.</summary>
    public void Detach()
    {
        _closed = true;
        _spark.ContextRestored -= OnContextRestored;
        _probeCts?.Cancel();
        foreach (var m in Models) m.PullCts?.Cancel();
    }

    /// <summary>Raised on whatever thread left the Spark; the window's state is updated on the UI thread.</summary>
    private void OnContextRestored(SparkContextRestore restore)
    {
        void Apply()
        {
            if (_closed) return;
            MarkInUse();
            RefreshActive();
            ContextNote = DescribeRestore(restore);
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) Apply();
        else dispatcher.BeginInvoke(Apply);
    }

    private string DescribeRestore(SparkContextRestore r) => T("spark.contextRestored",
        r.To.ToString("N0", CultureInfo.CurrentCulture), r.From.ToString("N0", CultureInfo.CurrentCulture));

    private async Task ConnectAsync()
    {
        string? host = DgxSparkService.NormalizeHost(HostInput);
        if (host == null)
        {
            Say(T("spark.invalidHost"), error: true);
            return;
        }

        _probeCts?.Cancel();
        var cts = _probeCts = new CancellationTokenSource();
        IsProbing = true;
        IsConnected = false;
        _connectedHost = null;
        Say(T("spark.connecting"), error: false);
        try
        {
            var probe = await _spark.ProbeAsync(host, cts.Token);
            if (_closed || cts.IsCancellationRequested) return;

            if (!probe.Reachable)
            {
                ShowRows([]);
                Say(T("spark.unreachable", host), error: true);
                return;
            }
            if (probe.Backend != "Ollama")
            {
                ShowRows([]);
                Say(T("spark.notOllama", probe.Backend!, probe.Port ?? 0), error: true);
                return;
            }
            if (probe.ModelListFailed)
            {
                // Ollama answered, but what it has installed is unknown — rows marked "not installed" would be a guess.
                ShowRows([]);
                Say(T("spark.listFailed", host, T("spark.err.server")), error: true);
                return;
            }

            _connectedHost = host;
            IsConnected = true;
            HostInput = host;
            ShowRows(probe.Models);
            Say(T("spark.found", probe.Backend, probe.Version ?? "", host, probe.Models.Count), error: false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { /* a newer lookup or the window closing replaced this one */ }
        catch (Exception ex)
        {
            _spark.LogFailure($"looking up {host} failed", ex);
            if (!_closed) Say(T("spark.unreachable", host), error: true);
        }
        finally
        {
            if (ReferenceEquals(_probeCts, cts)) IsProbing = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Recommended picks first (marked installed when the Spark has them), then whatever else is on it.</summary>
    private void ShowRows(IReadOnlyList<string> installed)
    {
        var have = new HashSet<string>(installed.Select(Canonical), StringComparer.OrdinalIgnoreCase);
        Models.Clear();
        foreach (var pick in DgxSparkService.RecommendedModels)
        {
            Models.Add(new SparkModelRow
            {
                Tag = pick.OllamaTag,
                Name = pick.Name,
                Facts = $"{pick.Size} · {pick.Speed}",
                UseText = T(pick.UseKey),
                IsRecommended = true,
                IsInstalled = have.Contains(Canonical(pick.OllamaTag)),
            });
        }
        var picked = new HashSet<string>(DgxSparkService.RecommendedModels.Select(p => Canonical(p.OllamaTag)), StringComparer.OrdinalIgnoreCase);
        foreach (var name in installed.Where(n => !picked.Contains(Canonical(n))).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            Models.Add(new SparkModelRow { Tag = name, Name = name, UseText = T("spark.installed"), IsInstalled = true });
        MarkInUse();
    }

    /// <summary>"llama3" and "llama3:latest" are the same Ollama model.</summary>
    private static string Canonical(string tag) => DgxSparkService.CanonicalTag(tag);

    private void MarkInUse()
    {
        var s = _settings.Settings;
        string? model = _spark.IsActive && s.ProviderConfigs.TryGetValue("Ollama", out var cfg) ? cfg?.EffectiveModelId : null;
        bool sameHost = _connectedHost != null && string.Equals(_connectedHost, s.DgxSparkHost, StringComparison.OrdinalIgnoreCase);
        foreach (var m in Models)
            m.IsInUse = sameHost && model != null && string.Equals(Canonical(m.Tag), Canonical(model), StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshActive()
    {
        var s = _settings.Settings;
        ActiveLine = _spark.IsActive && s.ProviderConfigs.TryGetValue("Ollama", out var cfg) && cfg?.EffectiveModelId is { } model
            ? T("spark.active", model, s.DgxSparkHost ?? "")
            : "";
        if (_spark.IsActive) ContextNote = "";   // back on the Spark: the old note no longer applies
    }

    /// <summary>Started fire-and-forget by <see cref="PullCommand"/>, one per row — so it must never throw.</summary>
    private async Task PullAsync(SparkModelRow? row)
    {
        if (row == null || !row.CanPull) return;
        if (_connectedHost is not { } host)
        {
            Say(T("spark.connectFirst"), error: true);
            return;
        }

        var cts = row.PullCts = new CancellationTokenSource();
        row.PullId = ++_pullSeq;
        row.IsPulling = true;
        row.Progress = 0;
        row.PullStatus = "";
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var progress = new Progress<(string Status, double? Fraction)>(p =>
            {
                if (p.Fraction is double f) row.Progress = f;
                row.PullStatus = p.Fraction is double pct
                    ? $"{p.Status} · {pct.ToString("P0", CultureInfo.CurrentCulture)}"
                    : p.Status;
            });
            await _spark.PullAsync(host, row.Tag, progress, cts.Token);
            row.IsInstalled = true;
            row.Progress = 1;
            if (!_closed) Say(T("spark.pullDone", row.Name), error: false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (!_closed) Say(T("spark.pullCancelled"), error: false);
        }
        catch (OllamaErrorException ex)
        {
            // Ollama's own sentence ("pull model manifest: file does not exist") — the one text shown as is.
            _spark.LogFailure($"{host}: Ollama refused to pull {row.Tag}", ex);
            if (!_closed) Say(T("spark.pullFailed", ex.Message), error: true);
        }
        catch (Exception ex)
        {
            _spark.LogFailure($"{host}: pulling {row.Tag} failed", ex);
            if (!_closed) Say(T("spark.pullFailed", Describe(DgxSparkService.Classify(ex))), error: true);
        }
        finally
        {
            row.IsPulling = false;
            row.PullStatus = "";
            row.PullCts = null;
            cts.Dispose();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task UseAsync(SparkModelRow? row)
    {
        if (row == null || !row.CanUse) return;
        if (_connectedHost is not { } host)
        {
            Say(T("spark.connectFirst"), error: true);
            return;
        }

        IsSwitching = true;
        try
        {
            var result = await _spark.UseOllamaModelAsync(host, row.Tag);
            MarkInUse();
            RefreshActive();
            if (result.Ok)
                Say(T("spark.used", row.Name, host, _settings.Settings.ContextSize.ToString("N0", CultureInfo.CurrentCulture)), error: false);
            else if (result.Failure == SparkFailure.Unreachable)
                Say(T("spark.unreachable", host), error: true);
            else
                Say(T("spark.useFailed", result.Failure == SparkFailure.ModelMissing
                    ? T("spark.err.modelMissing", row.Name) : Describe(result.Failure)), error: true);
        }
        catch (Exception ex)
        {
            _spark.LogFailure($"{host}: using {row.Tag} failed", ex);
            Say(T("spark.useFailed", Describe(DgxSparkService.Classify(ex))), error: true);
        }
        finally
        {
            IsSwitching = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>A failure in the UI language. The exception text never reaches this window (it is in the Debug Log).</summary>
    private string Describe(SparkFailure failure) => failure switch
    {
        SparkFailure.Network or SparkFailure.Unreachable => T("spark.err.network"),
        SparkFailure.Timeout => T("spark.err.timeout"),
        SparkFailure.Server or SparkFailure.Ollama => T("spark.err.server"),
        SparkFailure.BadResponse => T("spark.err.badResponse"),
        SparkFailure.Busy => T("spark.err.busy"),
        SparkFailure.NotReady => T("spark.err.notReady"),
        _ => T("spark.err.generic"),
    };

    private void OpenCampaign()
    {
        try { Process.Start(new ProcessStartInfo(DgxSparkService.CampaignUrl) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            _spark.LogFailure("opening the campaign page failed", ex);
            Say(T("spark.err.openLink", DgxSparkService.CampaignUrl), error: true);
        }
    }

    private void Say(string text, bool error)
    {
        StatusIsError = error;
        Status = text;
    }
}
