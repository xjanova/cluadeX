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
/// window from opening by itself again.
/// </summary>
public class BrainXInviteViewModel : ViewModelBase
{
    public const string DownloadUrl = "https://xman4289.com/brainx/download";
    public const string ProductUrl = "https://xman4289.com/products/brainx";

    private readonly McpServerManager _mcp;
    private readonly SettingsService _settings;
    private readonly LocalizationService _loc;
    private DispatcherTimer? _installWatch;
    private DateTime _installWatchUntil;

    public BrainXInviteViewModel(McpServerManager mcp, SettingsService settings, LocalizationService loc)
    {
        _mcp = mcp;
        _settings = settings;
        _loc = loc;
        PrimaryCommand = new AsyncRelayCommand(PrimaryAsync, () => !IsBusy && State != McpServerManager.BrainInstall.Connected);
        ReadFirstCommand = new RelayCommand(() => Open(ProductUrl));
        NotInterestedCommand = new RelayCommand(() =>
        {
            _settings.UpdateSettings(s => s.BrainXInviteDismissed = true);
            RequestClose?.Invoke();
        });
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
            }
        }
    }
    public bool IsConnected => State == McpServerManager.BrainInstall.Connected;
    public bool ShowPrimary => !IsConnected;

    public string Subtitle => State switch
    {
        McpServerManager.BrainInstall.Connected => T("brainx.subConnected"),
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
        Refresh();
        if (State == McpServerManager.BrainInstall.NotInstalled)
        {
            Open(DownloadUrl);
            Say(T("brainx.waitingInstall"), error: false);
            WatchForInstall();
            return;
        }
        await ConnectAsync();
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
                Say(T("brainx.connectFailed", _mcp.BrainLastError ?? _mcp.BrainStatusText), error: true);
        }
        catch (Exception ex)
        {
            Say(T("brainx.connectFailed", ex.Message), error: true);
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

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

    /// <summary>The window closed: stop watching, keep "don't show again".</summary>
    public void Detach()
    {
        StopWatch();
        if (DontShowAgain) _settings.UpdateSettings(s => s.BrainXInviteDismissed = true);
    }

    private void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Say(ex.Message, error: true); }
    }

    private void Say(string text, bool error)
    {
        StatusIsError = error;
        Status = text;
    }
}
