using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using CluadeX.Services;
using CluadeX.Services.Mcp;

namespace CluadeX.ViewModels;

/// <summary>
/// The BrainX invitation: what BrainX adds, and one button that does the right next thing for this PC —
/// install (opens the download page, then connects by itself once BrainX appears), connect an installed BrainX,
/// or nothing when it is already connected. "Read more first" opens the product page; "Not interested" stops the
/// window from opening by itself again AND stops CluadeX from registering an installed BrainX by itself at launch.
/// </summary>
public class BrainXInviteViewModel : ViewModelBase
{
    public const string DownloadUrl = "https://xman4289.com/brainx/download";
    public const string ProductUrl = "https://xman4289.com/products/brainx";

    private readonly McpServerManager _mcp;
    private readonly SettingsService _settings;
    private readonly LocalizationService _loc;
    private readonly Dispatcher _dispatcher;
    private readonly PropertyChangedEventHandler _brainChanged;
    private DispatcherTimer? _installWatch;
    private DateTime _installWatchUntil;

    public BrainXInviteViewModel(McpServerManager mcp, SettingsService settings, LocalizationService loc)
    {
        _mcp = mcp;
        _settings = settings;
        _loc = loc;
        _dispatcher = Dispatcher.CurrentDispatcher;
        PrimaryCommand = new AsyncRelayCommand(PrimaryAsync, () => !IsBusy && State != McpServerManager.BrainInstall.Connected);
        ReadFirstCommand = new RelayCommand(() => Open(ProductUrl));
        NotInterestedCommand = new RelayCommand(() =>
        {
            // "Not interested" means BrainX too, not just this window: an installed BrainX is no longer
            // registered and started by itself at launch (only an explicit Connect here undoes that).
            _settings.UpdateSettings(s =>
            {
                s.BrainXInviteDismissed = true;
                s.BrainXAutoConnectDeclined = true;
            });
            RequestClose?.Invoke();
        });

        // The brain moves on its own (starting → ready, a crash → reconnecting): keep the subtitle and the button
        // honest while the window is open. Raised from any thread, so hop to the window's dispatcher.
        _brainChanged = (_, e) =>
        {
            if (e.PropertyName != nameof(McpServerManager.BrainState)) return;
            _dispatcher.BeginInvoke(new Action(Refresh));
        };
        _mcp.PropertyChanged += _brainChanged;
        Refresh();
    }

    /// <summary>The window closes itself on this.</summary>
    public event Action? RequestClose;

    private string T(string key) => _loc.T(key);
    private string T(string key, params object[] args) => _loc.T(key, args);

    private McpServerManager.BrainInstall _state;
    private string _status = "";
    private bool _isBusy, _statusIsError, _dontShowAgain;

    public McpServerManager.BrainInstall State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(PrimaryText));
                OnPropertyChanged(nameof(ShowPrimary));
                OnPropertyChanged(nameof(IsConnected));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public bool IsConnected => State == McpServerManager.BrainInstall.Connected;
    public bool ShowPrimary => !IsConnected;

    public string Subtitle => State switch
    {
        McpServerManager.BrainInstall.Connected => T("brainx.subConnected"),
        // Starting / reconnecting is not "set up but not running" — and Connect now waits for it instead of
        // restarting it.
        McpServerManager.BrainInstall.Starting => T("brainx.subStarting"),
        McpServerManager.BrainInstall.Configured => T("brainx.subConfigured"),
        McpServerManager.BrainInstall.InstalledNotConfigured => T("brainx.subInstalled"),
        _ => T("brainx.subNotInstalled"),
    };
    public string PrimaryText => State == McpServerManager.BrainInstall.NotInstalled ? T("brainx.install") : T("brainx.connect");

    public string Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(_status);
    public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    /// <summary>"Don't show this again" — applied when the window closes.</summary>
    public bool DontShowAgain { get => _dontShowAgain; set => SetProperty(ref _dontShowAgain, value); }

    public ICommand PrimaryCommand { get; }
    public ICommand ReadFirstCommand { get; }
    public ICommand NotInterestedCommand { get; }

    private void Refresh() => State = _mcp.GetBrainInstallState();

    private async Task PrimaryAsync()
    {
        // The user asked for BrainX here, explicitly: the only thing that lifts an earlier "Not interested" or a
        // removal on the MCP page.
        _mcp.SetBrainAutoConnectDeclined(false);
        Refresh();
        if (State == McpServerManager.BrainInstall.NotInstalled)
        {
            // One click: download from xman4289.com, check it, run its installer — the browser page only when the site
            // does not hand over the installer (or CluadeX runs inside another app's package).
            if (!await InstallInOneClickAsync()) Open(DownloadUrl);
            Say(T("brainx.waitingInstall"), error: false);
            WatchForInstall();
            return;
        }
        await ConnectAsync();
    }

    /// <summary>Download, verify and run the BrainX installer. False when the page should be opened instead.</summary>
    private async Task<bool> InstallInOneClickAsync()
    {
        if (Services.Setup.SystemSetupService.InsidePackage()) return false;
        var installer = new Services.Connections.BrainXInstaller();
        string? file = null;
        IsBusy = true;
        try
        {
            var progress = new Progress<(long Done, long? Total)>(p =>
                Say(T("brainx.note.downloading", p.Total is { } t && t > 0 ? $"{p.Done * 100 / t}%" : $"{p.Done / 1048576} MB"), error: false));
            file = await installer.DownloadAsync(progress);
            Say(T("brainx.note.checking"), error: false);
            var sig = await Task.Run(() => Services.Connections.BrainXInstaller.VerifySignature(file));
            if (sig == Services.Connections.SignatureCheck.Invalid) { Say(T("brainx.err.signature"), error: true); return true; }
            if (sig == Services.Connections.SignatureCheck.Unsigned)
            {
                string sha = await Task.Run(() => Services.Connections.BrainXInstaller.Sha256(file));
                if (Views.Editor.ChoiceDialog.Show(T("brainx.unsigned.title"), T("brainx.unsigned.body", sha),
                        new[] { T("setup.confirm.cancel"), T("brainx.unsigned.run") }) != 1) return true;
            }
            Say(T("brainx.note.running"), error: false);
            await installer.RunAsync(file);
            return true;
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("brainx.err.", StringComparison.Ordinal))
        {
            Say(T(ex.Message), error: true);
            return ex.Message is not ("brainx.err.host" or "brainx.err.notFile");
        }
        catch (Exception ex)
        {
            _mcp.LogDiagnostic("BrainX window: one-click install failed", ex);
            return false;
        }
        finally
        {
            IsBusy = false;
            if (file != null) Services.Connections.BrainXInstaller.TryDelete(file);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task ConnectAsync()
    {
        IsBusy = true;
        Say(T("brainx.connecting"), error: false);
        try
        {
            bool ok = await _mcp.ConnectInstalledBrainAsync();
            Refresh();
            if (ok && _mcp.IsBrainConnected)
                Say(T("brainx.connected", _mcp.BrainToolCount), error: false);
            else
                Say(T("brainx.connectFailed", ReasonText(_mcp.LastBrainConnectProblem)), error: true);
        }
        catch (Exception ex)
        {
            // Unexpected: the details go to the debug log, the window says a sentence.
            _mcp.LogDiagnostic("BrainX window: connect threw", ex);
            Say(T("brainx.connectFailed", T("brainx.reasonStart")), error: true);
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string ReasonText(McpServerManager.BrainConnectProblem problem) => problem switch
    {
        McpServerManager.BrainConnectProblem.ConfigUnreadable => T("brainx.reasonConfig"),
        // Read fine, could not be written (locked / read-only / disk full): a different fix than a broken file.
        McpServerManager.BrainConnectProblem.ConfigNotSaved => T("brainx.reasonSave"),
        McpServerManager.BrainConnectProblem.NotInstalled => T("brainx.reasonMissing"),
        _ => T("brainx.reasonStart"),
    };

    /// <summary>After "Install": look for BrainX every few seconds (for up to 15 minutes) and connect the moment
    /// its exe appears, so the user never has to come back and press anything.</summary>
    private void WatchForInstall()
    {
        _installWatchUntil = DateTime.UtcNow.AddMinutes(15);
        if (_installWatch != null) return;
        _installWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _installWatch.Tick += async (_, _) =>
        {
            if (DateTime.UtcNow > _installWatchUntil) { StopWatch(); return; }
            if (IsBusy || McpServerManager.FindInstalledBrainExe() == null) return;
            StopWatch();
            await Task.Delay(TimeSpan.FromSeconds(3));   // let the installer finish writing its files
            await ConnectAsync();
        };
        _installWatch.Start();
    }

    private void StopWatch()
    {
        _installWatch?.Stop();
        _installWatch = null;
    }

    /// <summary>The window closed: stop watching, stop listening, keep "don't show again".</summary>
    public void Detach()
    {
        StopWatch();
        _mcp.PropertyChanged -= _brainChanged;
        if (DontShowAgain) _settings.UpdateSettings(s => s.BrainXInviteDismissed = true);
    }

    private void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            _mcp.LogDiagnostic($"BrainX window: could not open {url}", ex);
            Say(T("brainx.openFailed", url), error: true);
        }
    }

    private void Say(string text, bool error)
    {
        StatusIsError = error;
        Status = text;
    }
}
