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

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsProbing && !AnyPulling);
        PullCommand = new AsyncRelayCommand<SparkModelRow>(PullAsync, r => r is { CanPull: true } && IsConnected);
        CancelPullCommand = new RelayCommand<SparkModelRow>(r => r?.PullCts?.Cancel());
        UseCommand = new AsyncRelayCommand<SparkModelRow>(UseAsync, r => r is { CanUse: true } && IsConnected && !IsSwitching);
        OpenCampaignCommand = new RelayCommand(OpenCampaign);

        ShowRows([]);
        RefreshActive();
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

    public ObservableCollection<SparkModelRow> Models { get; } = [];
    private bool AnyPulling => Models.Any(m => m.IsPulling);

    public ICommand ConnectCommand { get; }
    public ICommand PullCommand { get; }
    public ICommand CancelPullCommand { get; }
    public ICommand UseCommand { get; }
    public ICommand OpenCampaignCommand { get; }

    /// <summary>The window closed: stop the lookup and every download it started.</summary>
    public void Detach()
    {
        _closed = true;
        _probeCts?.Cancel();
        foreach (var m in Models) m.PullCts?.Cancel();
    }

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

            _connectedHost = host;
            IsConnected = true;
            HostInput = host;
            ShowRows(probe.Models);
            Say(T("spark.found", probe.Backend, probe.Version ?? "", host, probe.Models.Count), error: false);
        }
        catch (OperationCanceledException) { /* a newer lookup or the window closing replaced this one */ }
        catch (Exception ex)
        {
            if (!_closed) Say(T("spark.unreachable", host) + " (" + ex.Message + ")", error: true);
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
    private static string Canonical(string tag) => tag.Contains(':') ? tag : tag + ":latest";

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
    }

    private async Task PullAsync(SparkModelRow? row)
    {
        if (row == null || !row.CanPull) return;
        if (_connectedHost is not { } host)
        {
            Say(T("spark.connectFirst"), error: true);
            return;
        }

        var cts = row.PullCts = new CancellationTokenSource();
        row.IsPulling = true;
        row.Progress = 0;
        row.PullStatus = "";
        CommandManager.InvalidateRequerySuggested();
        var progress = new Progress<(string Status, double? Fraction)>(p =>
        {
            if (p.Fraction is double f) row.Progress = f;
            row.PullStatus = p.Fraction is double pct
                ? $"{p.Status} · {pct.ToString("P0", CultureInfo.CurrentCulture)}"
                : p.Status;
        });
        try
        {
            await _spark.PullAsync(host, row.Tag, progress, cts.Token);
            row.IsInstalled = true;
            row.Progress = 1;
            if (!_closed) Say(T("spark.pullDone", row.Name), error: false);
        }
        catch (OperationCanceledException)
        {
            if (!_closed) Say(T("spark.pullCancelled"), error: false);
        }
        catch (Exception ex)
        {
            if (!_closed) Say(T("spark.pullFailed", ex.Message), error: true);
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
            await _spark.UseOllamaModelAsync(host, row.Tag);
            MarkInUse();
            RefreshActive();
            Say(T("spark.used", row.Name, host, _settings.Settings.ContextSize.ToString("N0", CultureInfo.CurrentCulture)), error: false);
        }
        catch (Exception ex)
        {
            Say(T("spark.useFailed", ex.Message), error: true);
        }
        finally
        {
            IsSwitching = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void OpenCampaign()
    {
        try { Process.Start(new ProcessStartInfo(DgxSparkService.CampaignUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Say(ex.Message, error: true); }
    }

    private void Say(string text, bool error)
    {
        StatusIsError = error;
        Status = text;
    }
}
