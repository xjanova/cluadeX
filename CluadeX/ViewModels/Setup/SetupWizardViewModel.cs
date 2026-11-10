using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CluadeX.Services;
using CluadeX.Services.Account;
using CluadeX.Services.Connections;
using CluadeX.Services.Mcp;
using CluadeX.Services.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace CluadeX.ViewModels.Setup;

public enum WizardPage { Welcome, License, Components, Progress, Done }

/// <summary>FirstRun: a fresh install. Reopen: Settings › "เปิดตัวช่วยติดตั้งอีกครั้ง" (never closes the app).
/// LinkAccount: the update banner's "เชื่อมบัญชี" — only the sign-in, then the window closes.</summary>
public enum WizardMode { FirstRun, Reopen, LinkAccount }

public enum StepState { Pending, Running, Done, Skipped, Failed }

/// <summary>One row of the install progress list.</summary>
public sealed class WizardStepVm : ViewModelBase
{
    private string _label = "", _detail = "";
    private StepState _state;
    private double _progress = -1;
    public string Id { get; init; } = "";
    public string Label { get => _label; set => SetProperty(ref _label, value); }
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
    public StepState State
    {
        get => _state;
        set { if (SetProperty(ref _state, value)) { OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(ShowBar)); } }
    }
    /// <summary>0..1 while a download runs; -1 = no bar.</summary>
    public double Progress { get => _progress; set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ShowBar)); } }
    public bool IsRunning => State == StepState.Running;
    public bool ShowBar => State == StepState.Running && Progress >= 0;
}

/// <summary>One dot of the title-bar stepper (ยินดีต้อนรับ · ลงทะเบียน · ส่วนเสริม · ติดตั้ง).</summary>
public sealed record StepperItem(string Label, string Mark, bool IsNow, bool IsDone, bool ShowSep);

/// <summary>One line of the connection doctor.</summary>
public sealed record DoctorLineVm(string Label, DoctorStatus Status, string Reason, string Detail)
{
    public string Mark => Status switch { DoctorStatus.Ok => "✓", DoctorStatus.Fail => "✕", DoctorStatus.Warn => "!", DoctorStatus.Info => "i", _ => "–" };
}

/// <summary>
/// The first-run setup wizard (installer contract 2.x, design boards Main → License → Components → InstallProgress →
/// InstallDone): license by Xman Studio account (password or QR/device code), typed key or the quick free path; the
/// optional NVIDIA accelerator and BrainX; then the real install steps with their real outcome. Every failure keeps the
/// user moving and offers the connection doctor. Nothing here blocks the app: closing the window at any point starts it.
/// </summary>
public sealed class SetupWizardViewModel : ViewModelBase, IDisposable
{
    private readonly SettingsService _settings;
    private readonly LocalizationService _loc;
    private readonly XmanAccountService _account;
    private readonly ConnectionDoctor _doctor;
    private readonly ActivationService _activation;
    private readonly GpuDetectionService? _gpu;
    private readonly McpServerManager? _mcp;
    private readonly ConnectionService? _connections;
    private readonly DebugLogService? _log;
    private readonly IReadOnlyList<(string Name, int Pose)> _girls;
    private readonly CancellationTokenSource _life = new();
    private CancellationTokenSource? _qrCts, _installCts;
    private readonly DispatcherTimer _clock;

    public WizardMode Mode { get; }
    /// <summary>Where the in-app sign-in keeps its WebView2 profile.</summary>
    public string DataRoot => _settings.DataRoot;

    /// <summary>Raised when the window should close (Done, LinkAccount finished).</summary>
    public event Action? CloseRequested;
    /// <summary>The in-app sign-in window should open at this address (the view owns windows).</summary>
    public event Action<string>? OpenWebSignIn;
    /// <summary>The in-app sign-in window should close (the code was approved, or the dialog was closed).</summary>
    public event Action? CloseWebSignIn;

    /// <summary>What the app should do after the window closed: start (true) or quit (false — Done › ปิด with the
    /// "open when done" box cleared on a fresh install).</summary>
    public bool OpenAppAfter { get; private set; } = true;

    public SetupWizardViewModel(IServiceProvider sp, WizardMode mode)
    {
        Mode = mode;
        _settings = sp.GetRequiredService<SettingsService>();
        _loc = sp.GetRequiredService<LocalizationService>();
        _account = sp.GetRequiredService<XmanAccountService>();
        _doctor = sp.GetRequiredService<ConnectionDoctor>();
        _activation = sp.GetRequiredService<ActivationService>();
        _gpu = sp.GetService<GpuDetectionService>();
        _mcp = sp.GetService<McpServerManager>();
        _connections = sp.GetService<ConnectionService>();
        _log = sp.GetService<DebugLogService>();
        _girls = SetupGirls.ForThisRun(_settings.DataRoot);

        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => TickQr();

        StartCommand = new RelayCommand(() => Go(WizardPage.License));
        CancelCommand = new RelayCommand(() => { OpenAppAfter = true; CloseRequested?.Invoke(); });
        BackCommand = new RelayCommand(Back, () => !LicenseBusy);
        TermsCommand = new RelayCommand(() => OpenUrl(TermsUrl));
        PrivacyCommand = new RelayCommand(() => OpenUrl(PrivacyUrl));
        ChooseAccountCommand = new RelayCommand(() => UseQuickPath = false);
        ChooseQuickCommand = new RelayCommand(() => { if (QuickPathOffered) UseQuickPath = true; });
        LicenseNextCommand = new AsyncRelayCommand(LicenseNextAsync, () => !LicenseBusy);
        ShowKeyBoxCommand = new RelayCommand(() => { KeyError = ""; ShowKeyBox = true; });
        CloseKeyBoxCommand = new RelayCommand(() => ShowKeyBox = false, () => !KeyBusy);
        ActivateKeyCommand = new AsyncRelayCommand(ActivateKeyAsync, () => !KeyBusy);
        OpenQrCommand = new AsyncRelayCommand<string>(OpenQrAsync);
        QrTabCommand = new RelayCommand<string>(t => QrGoogle = t == "google");
        CloseQrCommand = new RelayCommand(CloseQr);
        NewCodeCommand = new AsyncRelayCommand(NewCodeAsync);
        SignInHereCommand = new RelayCommand(() => { if (_code != null) OpenWebSignIn?.Invoke(_code.VerificationUriComplete); });
        OpenStoreCommand = new RelayCommand(() => OpenUrl(_purchaseUrl));
        InstallCommand = new AsyncRelayCommand(InstallAsync);
        CancelInstallCommand = new RelayCommand(() => _installCts?.Cancel(), () => Installing);
        ContinueCommand = new RelayCommand(() => Go(WizardPage.Done));
        CopyInstallReportCommand = new RelayCommand(CopyInstallReport);
        OpenAppCommand = new RelayCommand(() => { OpenAppAfter = true; CloseRequested?.Invoke(); });
        DoneCloseCommand = new RelayCommand(() => { OpenAppAfter = Mode != WizardMode.FirstRun || OpenWhenDone; CloseRequested?.Invoke(); });
        RunDoctorCommand = new AsyncRelayCommand(RunDoctorAsync, () => !DoctorBusy);
        CopyDoctorCommand = new RelayCommand(CopyDoctor, () => _report != null);
        SendDoctorCommand = new AsyncRelayCommand(SendDoctorAsync, () => _report != null && !DoctorBusy);
        CloseDoctorCommand = new RelayCommand(() => ShowDoctor = false);
        ToggleLanguageCommand = new RelayCommand(ToggleLanguage);

        _loc.LanguageChanged += OnLanguageChanged;
        PrepareWelcome();
        Go(mode == WizardMode.LinkAccount ? WizardPage.License : WizardPage.Welcome);
    }

    private string L(string key) => _loc.T(key);
    private string LF(string key, params object[] args) => _loc.T(key, args);

    // ═══════════════════════════════════════════════════════════════════
    //  Pages, stepper, girls
    // ═══════════════════════════════════════════════════════════════════

    private WizardPage _page;
    public WizardPage Page
    {
        get => _page;
        private set
        {
            if (!SetProperty(ref _page, value)) return;
            OnPropertyChanged(nameof(IsWelcome)); OnPropertyChanged(nameof(IsLicense)); OnPropertyChanged(nameof(IsComponents));
            OnPropertyChanged(nameof(IsProgress)); OnPropertyChanged(nameof(IsDone)); OnPropertyChanged(nameof(StepIndex));
            OnPropertyChanged(nameof(ShowStepper)); OnPropertyChanged(nameof(Girl)); OnPropertyChanged(nameof(GirlGlow));
            OnPropertyChanged(nameof(Stepper)); OnPropertyChanged(nameof(GirlName)); OnPropertyChanged(nameof(DoneBody));
        }
    }
    public bool IsWelcome => Page == WizardPage.Welcome;
    public bool IsLicense => Page == WizardPage.License;
    public bool IsComponents => Page == WizardPage.Components;
    public bool IsProgress => Page == WizardPage.Progress;
    public bool IsDone => Page == WizardPage.Done;
    /// <summary>0..3 for the title-bar stepper (Done shows none).</summary>
    public int StepIndex => Page switch { WizardPage.Welcome => 0, WizardPage.License => 1, WizardPage.Components => 2, _ => 3 };
    public bool ShowStepper => Page != WizardPage.Done && Mode != WizardMode.LinkAccount;
    public bool ShowBack => Mode != WizardMode.LinkAccount;

    private static readonly string[] StepKeys = { "wiz.step.welcome", "wiz.step.license", "wiz.step.components", "wiz.step.install" };
    public IReadOnlyList<StepperItem> Stepper => StepKeys.Select((k, i) => new StepperItem(L(k), i < StepIndex ? "✓" : (i + 1).ToString(),
        i == StepIndex, i < StepIndex, i > 0)).ToList();

    private (string Name, int Pose)? GirlFor(WizardPage p) => _girls.Count == 0 ? null : _girls[(int)p % _girls.Count];
    public ImageSource? Girl => GirlFor(Page) is { } g ? GirlArt.Image(g.Name, 1180, g.Pose) : null;
    public Color GirlGlow => GirlFor(Page) is { } g ? GirlArt.Glow(g.Name) : Color.FromRgb(0xC0, 0x84, 0xFC);
    /// <summary>The girl's name on this page (for the test renders and the log).</summary>
    public string? GirlName => GirlFor(Page)?.Name;

    private void Go(WizardPage page)
    {
        Page = page;
        if (page == WizardPage.License) _ = CheckServerAsync();
        if (page == WizardPage.Components) PrepareComponents();
    }

    private void Back()
    {
        switch (Page)
        {
            case WizardPage.License: Go(WizardPage.Welcome); break;
            case WizardPage.Components: Go(WizardPage.License); break;
        }
    }

    public string LanguageToggleText => _loc.CurrentLanguage == "th" ? "EN" : "ไทย";
    private void ToggleLanguage()
    {
        string next = _loc.CurrentLanguage == "th" ? "en" : "th";
        _settings.UpdateSettings(s => s.Language = next);
        _loc.SetLanguage(next);
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(LanguageToggleText));
        OnPropertyChanged(nameof(Stepper)); OnPropertyChanged(nameof(QrHint1)); OnPropertyChanged(nameof(DoneBody)); OnPropertyChanged(nameof(ProgressTitle)); OnPropertyChanged(nameof(QuickCardText));
        PrepareWelcome();
        RefreshLicenseTexts();
        if (Page == WizardPage.Components) RefreshComponentTexts();
        foreach (var s in Steps) s.Label = L("wiz.prog." + s.Id);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Welcome
    // ═══════════════════════════════════════════════════════════════════

    public const string TermsUrl = "https://xman4289.com/terms";
    public const string PrivacyUrl = "https://xman4289.com/privacy";

    private bool _createShortcut = true, _openWhenDone = true;
    public bool CreateShortcut { get => _createShortcut; set => SetProperty(ref _createShortcut, value); }
    public bool OpenWhenDone { get => _openWhenDone; set => SetProperty(ref _openWhenDone, value); }
    public string VersionChip => LF("wiz.welcome.version", AutoUpdateService.CurrentVersion);
    private string _sizeChip = "";
    public string SizeChip { get => _sizeChip; private set => SetProperty(ref _sizeChip, value); }
    public string InstallPath { get; private set; } = "";
    private long _installBytes = -1;

    private void PrepareWelcome()
    {
        OnPropertyChanged(nameof(VersionChip));
        try
        {
            string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            string root = AutoUpdateService.CurrentBuildKind == AutoUpdateService.BuildKind.Installed ? Path.GetDirectoryName(exeDir) ?? exeDir : exeDir;
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            InstallPath = root.StartsWith(local, StringComparison.OrdinalIgnoreCase) ? "%LOCALAPPDATA%" + root[local.Length..] : root;
            OnPropertyChanged(nameof(InstallPath));
            if (_installBytes < 0)
                _ = Task.Run(() =>
                {
                    long total = 0;
                    try { foreach (var f in Directory.EnumerateFiles(exeDir, "*", SearchOption.AllDirectories)) total += new FileInfo(f).Length; } catch { }
                    return total;
                }).ContinueWith(t => { _installBytes = t.Result; Application.Current?.Dispatcher.BeginInvoke(UpdateSizeChip); }, TaskScheduler.Default);
        }
        catch { InstallPath = ""; }
        UpdateSizeChip();
    }

    private void UpdateSizeChip() => SizeChip = _installBytes > 0 ? LF("wiz.welcome.size", Math.Max(1, _installBytes / 1048576)) : L("wiz.welcome.sizeUnknown");

    // ═══════════════════════════════════════════════════════════════════
    //  License page
    // ═══════════════════════════════════════════════════════════════════

    private AppAuthHealth _health = AppAuthHealth.Unknown;
    private bool _checkingServer, _useQuick, _licenseBusy, _showKeyBox, _keyBusy;
    private string _login = "", _licenseError = "", _licenseNote = "", _licenseOk = "", _keyText = "", _keyError = "", _purchaseUrl = XmanLicenseService.DefaultPurchaseUrl;
    /// <summary>The password lives only here, in memory, until the sign-in call; never logged, never saved.</summary>
    private string _password = "";

    /// <summary>What the License page settled: "account" (signed in), "key", "have" (already licensed), "quick" (done in Progress), "none".</summary>
    private string _licenseChoice = "none";

    public bool CheckingServer { get => _checkingServer; private set { if (SetProperty(ref _checkingServer, value)) RaiseAuthFlags(); } }
    public bool AccountSignInAvailable => _health.Availability == AppAuthAvailability.Ready && _health.PasswordLogin;
    public bool SocialAvailable => _health.Availability == AppAuthAvailability.Ready && _health.DeviceLogin;
    public bool ServerNotDeployed => _health.Availability == AppAuthAvailability.NotDeployed;
    public bool ServerUnreachable => _health.Availability == AppAuthAvailability.Unreachable;
    /// <summary>The quick path is offered except in the "link my account" mode (it would not link anything).</summary>
    public bool QuickPathOffered => Mode != WizardMode.LinkAccount;
    public bool UseQuickPath { get => _useQuick; set { if (SetProperty(ref _useQuick, value && QuickPathOffered)) { OnPropertyChanged(nameof(ShowAccountForm)); OnPropertyChanged(nameof(LicenseNextText)); LicenseError = ""; } } }
    /// <summary>The password form shows when the account card is picked and the server can take it.</summary>
    public bool ShowAccountForm => !UseQuickPath && AccountSignInAvailable;
    public string Login { get => _login; set { if (SetProperty(ref _login, value)) LicenseError = ""; } }
    public void SetPassword(string pw) { _password = pw ?? ""; if (LicenseError.Length > 0) LicenseError = ""; }
    public bool LicenseBusy { get => _licenseBusy; private set { if (SetProperty(ref _licenseBusy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string LicenseError { get => _licenseError; private set { if (SetProperty(ref _licenseError, value)) OnPropertyChanged(nameof(HasLicenseError)); } }
    public bool HasLicenseError => LicenseError.Length > 0;
    /// <summary>A status line under the cards: checking / not deployed / unreachable.</summary>
    public string LicenseNote { get => _licenseNote; private set { if (SetProperty(ref _licenseNote, value)) OnPropertyChanged(nameof(HasLicenseNote)); } }
    public bool HasLicenseNote => LicenseNote.Length > 0;
    /// <summary>What this PC already has ("ไลเซ่น Pro · เชื่อมบัญชี bu***@x.com แล้ว").</summary>
    public string LicenseOk { get => _licenseOk; private set { if (SetProperty(ref _licenseOk, value)) OnPropertyChanged(nameof(HasLicenseOk)); } }
    public bool HasLicenseOk => LicenseOk.Length > 0;
    private bool _showStore;
    public bool ShowStoreLink { get => _showStore; private set => SetProperty(ref _showStore, value); }
    /// <summary>The card's own line stays short (Thai wraps badly in a narrow card); the panel under it says the rest.</summary>
    public string QuickCardText => _health.Availability == AppAuthAvailability.Ready && _health.FreeLicense ? L("wiz.lic.quickSub") : L("wiz.lic.quickCard");
    public string QuickSubText => _health.Availability == AppAuthAvailability.Ready && _health.FreeLicense ? L("wiz.lic.quickSub") : L("wiz.lic.quickSubTrial");
    public string LicenseNextText => Mode == WizardMode.LinkAccount ? L("wiz.lic.signIn")
        : UseQuickPath || !AccountSignInAvailable ? L("wiz.next") : L("wiz.lic.signInNext");

    public bool ShowKeyBox { get => _showKeyBox; set => SetProperty(ref _showKeyBox, value); }
    public string KeyText { get => _keyText; set { if (SetProperty(ref _keyText, value)) KeyError = ""; } }
    public string KeyError { get => _keyError; private set { if (SetProperty(ref _keyError, value)) OnPropertyChanged(nameof(HasKeyError)); } }
    public bool HasKeyError => KeyError.Length > 0;
    public bool KeyBusy { get => _keyBusy; private set { if (SetProperty(ref _keyBusy, value)) CommandManager.InvalidateRequerySuggested(); } }

    private void RaiseAuthFlags()
    {
        OnPropertyChanged(nameof(AccountSignInAvailable)); OnPropertyChanged(nameof(SocialAvailable)); OnPropertyChanged(nameof(ServerNotDeployed));
        OnPropertyChanged(nameof(ServerUnreachable)); OnPropertyChanged(nameof(ShowAccountForm)); OnPropertyChanged(nameof(QuickSubText)); OnPropertyChanged(nameof(QuickCardText));
        OnPropertyChanged(nameof(LicenseNextText));
    }

    private int _healthRun;
    private async Task CheckServerAsync()
    {
        RefreshLicenseOk();
        if (_health.Availability == AppAuthAvailability.Ready || CheckingServer) { RefreshLicenseTexts(); return; }
        int run = ++_healthRun;
        CheckingServer = true;
        RefreshLicenseTexts();
        AppAuthHealth h;
        try { h = await _account.HealthAsync(_life.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _log?.Warn("Setup", "health check threw", ex); h = AppAuthHealth.Unknown with { Availability = AppAuthAvailability.Unreachable, ErrorCode = "OFFLINE" }; }
        if (run != _healthRun || _life.IsCancellationRequested) return;
        _health = h;
        CheckingServer = false;
        // Nothing to sign in with (server not upgraded yet, or offline): the quick path is the sensible default.
        if (!AccountSignInAvailable && QuickPathOffered && _licenseChoice == "none") UseQuickPath = true;
        RefreshLicenseTexts();
        RaiseAuthFlags();
    }

    private void RefreshLicenseTexts()
    {
        LicenseNote = CheckingServer ? L("wiz.lic.checking")
            : ServerNotDeployed ? L("wiz.lic.notDeployed")
            : ServerUnreachable ? LF("wiz.lic.unreachable", AccountErrors.Describe(_health.ErrorCode, _loc))
            : "";
        OnPropertyChanged(nameof(QuickSubText)); OnPropertyChanged(nameof(QuickCardText));
        OnPropertyChanged(nameof(LicenseNextText));
        RefreshLicenseOk();
    }

    private void RefreshLicenseOk()
    {
        var parts = new List<string>();
        if (_activation.IsActivated) parts.Add(LF("wiz.lic.have", _activation.TierDisplayName));
        if (_account.IsLinked) parts.Add(LF("wiz.lic.linked", _account.AccountEmailMasked ?? _account.AccountName ?? ""));
        LicenseOk = string.Join(" · ", parts);
    }

    private async Task LicenseNextAsync()
    {
        LicenseError = "";
        ShowStoreLink = false;
        if (UseQuickPath)
        {
            _licenseChoice = "quick";
            Go(WizardPage.Components);
            return;
        }
        // Nothing typed and this PC is already licensed / linked (returning user, Settings › reopen): go on.
        if (string.IsNullOrWhiteSpace(Login) && _password.Length == 0 && (HasLicenseOk || !AccountSignInAvailable))
        {
            if (_licenseChoice == "none") _licenseChoice = HasLicenseOk ? "have" : "none";
            if (Mode == WizardMode.LinkAccount && !_account.IsLinked) { LicenseError = AccountSignInAvailable ? L("acct.err.emptyLogin") : L("wiz.lic.notDeployed"); return; }
            Advance();
            return;
        }
        if (!AccountSignInAvailable) { LicenseError = L("wiz.lic.notDeployed"); return; }

        LicenseBusy = true;
        try
        {
            var r = await _account.LoginAsync(Login, _password, _life.Token);
            if (!r.Success || r.Link == null)
            {
                LicenseError = AccountErrors.Describe(r.ErrorCode, _loc);
                return;
            }
            _password = "";
            PasswordCleared?.Invoke();
            if (await ApplyLinkAsync(r.Link)) Advance();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log?.Warn("Setup", "sign-in threw", ex);
            LicenseError = L("acct.err.generic");
        }
        finally { LicenseBusy = false; }
    }

    /// <summary>The view clears its PasswordBox when this fires (after a successful sign-in).</summary>
    public event Action? PasswordCleared;

    private void Advance()
    {
        if (Mode == WizardMode.LinkAccount) { OpenAppAfter = true; CloseRequested?.Invoke(); return; }
        Go(WizardPage.Components);
    }

    /// <summary>Remember the account, activate what it holds (asking before moving a key from another PC). True = go on.</summary>
    private async Task<bool> ApplyLinkAsync(LinkResult link)
    {
        var applied = await _account.ApplyAsync(link, () => Task.FromResult(AskMove()), _life.Token);
        _purchaseUrl = string.IsNullOrEmpty(applied.PurchaseUrl) ? _purchaseUrl : applied.PurchaseUrl;
        _licenseChoice = "account";
        RefreshLicenseOk();
        switch (applied.Status)
        {
            case LinkApplyStatus.Activated:
                return true;
            case LinkApplyStatus.LinkedNoLicense:
                // Linked, nothing bought: say so once with the store link; the next press goes on (free / trial).
                if (!_noLicenseShown) { _noLicenseShown = true; LicenseError = L("wiz.lic.noLicense"); ShowStoreLink = true; return false; }
                return true;
            case LinkApplyStatus.MoveDeclined:
                if (!_moveDeclinedShown) { _moveDeclinedShown = true; LicenseError = L("wiz.lic.moveDeclined"); return false; }
                return true;
            default:
                LicenseError = AccountErrors.Describe(applied.ErrorCode, _loc);
                return false;
        }
    }
    private bool _noLicenseShown, _moveDeclinedShown;

    private bool AskMove() => Views.Editor.ChoiceDialog.Show(L("wiz.lic.moveTitle"), L("wiz.lic.moveBody"),
        new[] { L("wiz.lic.moveYes"), L("wiz.lic.moveNo") }, 1) == 0;

    private async Task ActivateKeyAsync()
    {
        string key = (KeyText ?? "").Trim();
        if (key.Length == 0) { KeyError = L("acct.err.emptyKey"); return; }
        if (!ActivationService.HasValidKeyFormat(key)) { KeyError = L("acct.err.invalidLicense"); return; }
        KeyBusy = true;
        try
        {
            var r = await _activation.ActivateLinkedAsync(key, forceRebind: false, _life.Token);
            if (!r.Success && r.ErrorCode == "ALREADY_ACTIVATED_OTHER_DEVICE")
            {
                if (!AskMove()) { KeyError = L("acct.err.otherDevice"); return; }
                r = await _activation.ActivateLinkedAsync(key, forceRebind: true, _life.Token);
            }
            if (!r.Success) { KeyError = AccountErrors.Describe(r.ErrorCode ?? "INVALID_LICENSE", _loc); return; }
            _licenseChoice = "key";
            KeyText = "";
            ShowKeyBox = false;
            RefreshLicenseOk();
            Advance();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log?.Warn("Setup", "key activation threw", ex); KeyError = L("acct.err.generic"); }
        finally { KeyBusy = false; }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  QR / device code dialog
    // ═══════════════════════════════════════════════════════════════════

    private DeviceCode? _code;
    private bool _showQr, _qrGoogle, _qrExpired, _qrBusy;
    private ImageSource? _qrImage;
    private string _qrStatus = "", _qrCountdown = "", _userCode = "";

    public bool ShowQr { get => _showQr; private set => SetProperty(ref _showQr, value); }
    public bool QrGoogle { get => _qrGoogle; set { if (SetProperty(ref _qrGoogle, value)) { OnPropertyChanged(nameof(QrHint1)); OnPropertyChanged(nameof(QrLine)); } } }
    public bool QrLine => !QrGoogle;
    /// <summary>A live code is on screen and the phone has not answered yet (the small spinner).</summary>
    public bool QrWaiting => !QrExpired && !QrBusy && UserCode.Length > 0;
    public string QrHint1 => L(QrGoogle ? "wiz.qr.step1.google" : "wiz.qr.step1.line");
    public ImageSource? QrImage { get => _qrImage; private set => SetProperty(ref _qrImage, value); }
    public string UserCode { get => _userCode; private set { if (SetProperty(ref _userCode, value)) OnPropertyChanged(nameof(QrWaiting)); } }
    public string QrStatus { get => _qrStatus; private set => SetProperty(ref _qrStatus, value); }
    public string QrCountdown { get => _qrCountdown; private set => SetProperty(ref _qrCountdown, value); }
    public bool QrExpired { get => _qrExpired; private set { if (SetProperty(ref _qrExpired, value)) OnPropertyChanged(nameof(QrWaiting)); } }
    public bool QrBusy { get => _qrBusy; private set { if (SetProperty(ref _qrBusy, value)) OnPropertyChanged(nameof(QrWaiting)); } }
    private bool _qrFailed;
    /// <summary>A transport failure while asking for a code: the doctor button shows.</summary>
    public bool QrFailed { get => _qrFailed; private set => SetProperty(ref _qrFailed, value); }

    private async Task OpenQrAsync(string? tab)
    {
        QrGoogle = tab == "google";
        ShowQr = true;
        if (_code != null && _code.ExpiresUtc > DateTime.UtcNow.AddSeconds(20) && _qrCts is { IsCancellationRequested: false }) return;
        await NewCodeAsync();
    }

    private async Task NewCodeAsync()
    {
        _qrCts?.Cancel();
        _qrCts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        var ct = _qrCts.Token;
        _code = null; QrImage = null; UserCode = ""; QrExpired = false; QrFailed = false; QrCountdown = "";
        QrBusy = true;
        QrStatus = L("wiz.qr.getting");
        try
        {
            var (code, err, _, _) = await _account.StartDeviceAsync(ct);
            if (ct.IsCancellationRequested) return;
            if (code == null)
            {
                QrStatus = AccountErrors.Describe(err, _loc);
                QrFailed = AccountErrors.IsTransport(err) || err == "NOT_DEPLOYED";
                QrExpired = true;   // "ขอโค้ดใหม่" retries
                return;
            }
            _code = code;
            UserCode = code.UserCode;
            QrImage = WatchViewModel.MakeQr(code.VerificationUriComplete);
            QrStatus = L("wiz.qr.waiting");
            TickQr();
            _clock.Start();
            _ = PollLoopAsync(code, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log?.Warn("Setup", "device start threw", ex); QrStatus = L("acct.err.generic"); QrExpired = true; }
        finally { QrBusy = false; }
    }

    private void TickQr()
    {
        if (_code == null || !ShowQr) { _clock.Stop(); return; }
        var left = _code.ExpiresUtc - DateTime.UtcNow;
        if (left <= TimeSpan.Zero) { QrCountdown = ""; ExpireQr(); return; }
        QrCountdown = LF("wiz.qr.expiresIn", $"{(int)left.TotalMinutes}:{left.Seconds:00}");
    }

    private void ExpireQr()
    {
        _qrCts?.Cancel();
        _clock.Stop();
        QrExpired = true;
        QrImage = null;
        QrStatus = L("acct.err.expiredToken");
    }

    private async Task PollLoopAsync(DeviceCode code, CancellationToken ct)
    {
        int interval = code.IntervalSeconds;
        int transportFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(interval), ct); } catch (OperationCanceledException) { return; }
            if (code.ExpiresUtc <= DateTime.UtcNow) { ExpireQr(); return; }
            DevicePoll p;
            try { p = await _account.PollDeviceAsync(code, ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log?.Warn("Setup", "device poll threw", ex); continue; }
            if (ct.IsCancellationRequested) return;
            switch (p.State)
            {
                case DevicePollState.Pending: transportFailures = 0; QrStatus = L("wiz.qr.waiting"); break;
                case DevicePollState.SlowDown: interval = Math.Min(interval + 5, 30); break;
                case DevicePollState.Expired: ExpireQr(); return;
                case DevicePollState.Denied:
                    _clock.Stop(); QrExpired = true; QrImage = null; QrStatus = AccountErrors.Describe(p.ErrorCode, _loc);
                    CloseWebSignIn?.Invoke();
                    return;
                case DevicePollState.Approved when p.Link != null:
                    _clock.Stop();
                    QrStatus = L("wiz.qr.approved");
                    CloseWebSignIn?.Invoke();
                    LicenseBusy = true;
                    try
                    {
                        bool go = await ApplyLinkAsync(p.Link);
                        ShowQr = false;
                        _code = null;
                        if (go) Advance();
                    }
                    finally { LicenseBusy = false; }
                    return;
                default:
                    // A transport hiccup: keep polling a few times (the phone may still approve), then say why.
                    if (++transportFailures >= 4)
                    {
                        QrStatus = AccountErrors.Describe(p.ErrorCode, _loc);
                        QrFailed = AccountErrors.IsTransport(p.ErrorCode);
                    }
                    break;
            }
        }
    }

    private void CloseQr()
    {
        _qrCts?.Cancel();
        _clock.Stop();
        _code = null;
        ShowQr = false;
        CloseWebSignIn?.Invoke();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Components
    // ═══════════════════════════════════════════════════════════════════

    private bool _componentsReady, _nvidiaDetected, _nvidiaOn, _nvidiaInstalled, _brainXInstalled, _brainXOn = true;
    private string _gpuName = "", _nvidiaSub = "", _nvidiaSize = "", _brainXSub = "";
    private long _nvidiaBytes = -1;
    private string? _installedTag;

    public bool NvidiaDetected { get => _nvidiaDetected; private set => SetProperty(ref _nvidiaDetected, value); }
    /// <summary>The switch can be turned on only on a PC with an NVIDIA card and no accelerator installed yet.</summary>
    public bool NvidiaSelectable => NvidiaDetected && !_nvidiaInstalled;
    public bool NvidiaOn { get => _nvidiaOn; set => SetProperty(ref _nvidiaOn, value && NvidiaSelectable); }
    public string NvidiaSub { get => _nvidiaSub; private set => SetProperty(ref _nvidiaSub, value); }
    public string NvidiaSize { get => _nvidiaSize; private set => SetProperty(ref _nvidiaSize, value); }
    public bool BrainXInstalled { get => _brainXInstalled; private set => SetProperty(ref _brainXInstalled, value); }
    public bool BrainXOn { get => _brainXOn; set => SetProperty(ref _brainXOn, value); }
    public string BrainXSub { get => _brainXSub; private set => SetProperty(ref _brainXSub, value); }

    private void PrepareComponents()
    {
        if (_componentsReady) { RefreshComponentTexts(); return; }
        _componentsReady = true;
        try
        {
            var gpu = _gpu?.DetectGpu();
            NvidiaDetected = gpu != null && string.Equals(gpu.GpuBrand, "NVIDIA", StringComparison.OrdinalIgnoreCase);
            _gpuName = gpu?.Name ?? "";
        }
        catch (Exception ex) { _log?.Warn("Setup", "GPU detection threw", ex); NvidiaDetected = false; }
        var installed = LlamaCppInstaller.ReadInstalled();
        _nvidiaInstalled = installed is { Variant: LlamaCppVariant.Cuda12 or LlamaCppVariant.Cuda13 };
        _installedTag = installed?.Tag;
        OnPropertyChanged(nameof(NvidiaSelectable));
        NvidiaOn = NvidiaSelectable;
        BrainXInstalled = McpServerManager.FindInstalledBrainExe() != null;
        BrainXOn = true;
        RefreshComponentTexts();
        if (NvidiaSelectable) _ = ReadNvidiaSizeAsync();
    }

    private async Task ReadNvidiaSizeAsync()
    {
        NvidiaSize = L("wiz.comp.nv.sizeChecking");
        try
        {
            var installer = new LlamaCppInstaller(_log);
            var variant = await Task.Run(installer.AutoVariant);
            if (variant is not (LlamaCppVariant.Cuda12 or LlamaCppVariant.Cuda13)) { _nvidiaBytes = 0; RefreshComponentTexts(); return; }
            var rel = await installer.FindReleaseAsync(variant, _life.Token);
            _nvidiaBytes = rel.TotalBytes;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _log?.Debug("Setup", $"accelerator size unknown: {ex.Message}"); _nvidiaBytes = 0; }
        RefreshComponentTexts();
    }

    private void RefreshComponentTexts()
    {
        NvidiaSub = _nvidiaInstalled ? LF("wiz.comp.nv.installed", _installedTag ?? "")
            : NvidiaDetected ? LF("wiz.comp.nv.found", _gpuName) : L("wiz.comp.nv.none");
        NvidiaSize = !NvidiaSelectable ? "" : _nvidiaBytes > 0 ? LF("wiz.comp.nv.size", _nvidiaBytes / 1048576)
            : _nvidiaBytes == 0 ? L("wiz.comp.nv.sizeUnknown") : L("wiz.comp.nv.sizeChecking");
        BrainXSub = BrainXInstalled ? L("wiz.comp.bx.found") : L("wiz.comp.bx.sub");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Progress
    // ═══════════════════════════════════════════════════════════════════

    public ObservableCollection<WizardStepVm> Steps { get; } = new();
    private double _overall;
    private bool _installing, _installFinished, _anyFailed;
    private string _eta = "", _tip = "";
    public double Overall { get => _overall; private set { if (SetProperty(ref _overall, value)) OnPropertyChanged(nameof(OverallText)); } }
    public string OverallText => $"{(int)Math.Round(Overall * 100)}%";
    public string Eta { get => _eta; private set => SetProperty(ref _eta, value); }
    public string Tip { get => _tip; private set => SetProperty(ref _tip, value); }
    public bool Installing { get => _installing; private set { if (SetProperty(ref _installing, value)) CommandManager.InvalidateRequerySuggested(); } }
    /// <summary>Finished with something failed: the page stays, with the doctor, the report and "ไปต่อ".</summary>
    public bool InstallNeedsAttention { get => _installFinished && _anyFailed; }
    public bool ShowOverall => !InstallNeedsAttention;
    public string ProgressTitle => L(InstallNeedsAttention ? "wiz.prog.titlePartial" : "wiz.prog.title");

    private WizardStepVm Step(string id) => Steps.First(s => s.Id == id);

    private void Set(string id, StepState state, string detail = "", double progress = -1)
    {
        var s = Steps.FirstOrDefault(x => x.Id == id);
        if (s == null) return;
        s.State = state; s.Detail = detail; s.Progress = progress;
        RecomputeOverall();
    }

    private void RecomputeOverall()
    {
        if (Steps.Count == 0) { Overall = 0; return; }
        double sum = 0;
        foreach (var s in Steps)
            sum += s.State is StepState.Done or StepState.Skipped or StepState.Failed ? 1 : s.State == StepState.Running ? Math.Max(0.1, s.Progress) : 0;
        Overall = sum / Steps.Count;
    }

    private async Task InstallAsync()
    {
        if (Installing || _installFinished) return;
        Steps.Clear();
        foreach (var id in new[] { "program", "license", "autoupdate" }) Steps.Add(new WizardStepVm { Id = id, Label = L("wiz.prog." + id) });
        if (NvidiaOn) Steps.Add(new WizardStepVm { Id = "nvidia", Label = L("wiz.prog.nvidia") });
        if (BrainXOn) Steps.Add(new WizardStepVm { Id = "brainx", Label = L("wiz.prog.brainx") });
        Steps.Add(new WizardStepVm { Id = "shortcut", Label = L("wiz.prog.shortcut") });
        Tip = L("wiz.tip." + new Random().Next(1, 5));
        Go(WizardPage.Progress);

        _installCts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        var ct = _installCts.Token;
        Installing = true;
        try
        {
            Set("program", StepState.Done, AutoUpdateService.CurrentVersion);
            await RunLicenseStepAsync(ct);
            RunAutoUpdateStep();
            if (NvidiaOn && !ct.IsCancellationRequested) await RunNvidiaStepAsync(ct);
            if (BrainXOn && !ct.IsCancellationRequested) await RunBrainXStepAsync(ct);
            if (!ct.IsCancellationRequested) RunShortcutStep();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log?.Warn("Setup", "install threw", ex);
            var running = Steps.FirstOrDefault(s => s.State == StepState.Running);
            if (running != null) Set(running.Id, StepState.Failed, L("acct.err.generic"));
        }
        finally
        {
            foreach (var s in Steps.Where(s => s.State is StepState.Pending or StepState.Running).ToList())
                Set(s.Id, StepState.Skipped, L("wiz.prog.cancelled"));
            Installing = false;
            _installFinished = true;
            _anyFailed = Steps.Any(s => s.State == StepState.Failed);
            Eta = "";
            OnPropertyChanged(nameof(InstallNeedsAttention)); OnPropertyChanged(nameof(ShowOverall)); OnPropertyChanged(nameof(ProgressTitle));
            _settings.UpdateSettings(s => s.SetupWizardCompleted = true);
            _log?.Info("Setup", "wizard steps: " + string.Join(" ", Steps.Select(s => $"{s.Id}={s.State}")));
        }
        if (!_anyFailed && !_life.IsCancellationRequested)
        {
            try { await Task.Delay(700, _life.Token); } catch (OperationCanceledException) { return; }
            Go(WizardPage.Done);
        }
    }

    private async Task RunLicenseStepAsync(CancellationToken ct)
    {
        Set("license", StepState.Running);
        if (_licenseChoice == "quick" && !_activation.IsActivated)
        {
            // Quick path: the server's free key when it offers one, else what a fresh PC gets anyway (its held license
            // or the one-time trial). Failing here is not fatal — the app works on Free.
            string? failure = null;
            if (_health.Availability == AppAuthAvailability.Ready && _health.FreeLicense)
            {
                var r = await _account.FreeLicenseAsync(ct);
                if (r.Success && r.Link != null)
                {
                    var applied = await _account.ApplyAsync(r.Link, () => Task.FromResult(false), ct);
                    if (applied.Status != LinkApplyStatus.Activated) failure = AccountErrors.Describe(applied.ErrorCode, _loc);
                }
                else failure = AccountErrors.Describe(r.ErrorCode, _loc);
            }
            if (!_activation.IsActivated && string.IsNullOrEmpty(_settings.Settings.ActivationKey))
            {
                await _activation.ValidateOnlineAsync(ct);
                await _activation.AutoProvisionAsync(ct);
                if (_activation.IsActivated || !string.IsNullOrEmpty(_settings.Settings.ActivationKey)) failure = null;
                else failure ??= _activation.LastRegistration == null ? AccountErrors.Describe("OFFLINE", _loc) : null;
            }
            if (failure != null) { Set("license", StepState.Failed, LF("wiz.prog.lic.failed", failure)); return; }
        }
        Set("license", StepState.Done, LicenseSummary());
    }

    private string LicenseSummary()
    {
        string tier = _activation.IsTrial && _activation.ExpiresAtUtc is { } exp ? LF("wiz.prog.lic.trial", exp.ToLocalTime().ToString("d MMM yyyy", _loc.CurrentLanguage == "th" ? new System.Globalization.CultureInfo("th-TH") : System.Globalization.CultureInfo.InvariantCulture))
            : _activation.IsActivated ? LF("wiz.prog.lic.tier", _activation.TierDisplayName)
            : !string.IsNullOrEmpty(_settings.Settings.ActivationKey) ? L("wiz.prog.lic.free") : L("wiz.prog.lic.none");
        return _account.IsLinked ? tier + " · " + (_account.AccountEmailMasked ?? _account.AccountName ?? "") : tier;
    }

    private void RunAutoUpdateStep()
    {
        bool allowed = AutoUpdateEntitlement.Allowed(_activation, _settings.Settings);
        if (allowed && _settings.Settings.AutoUpdateEnabled) Set("autoupdate", StepState.Done, L("wiz.prog.au.on"));
        else if (allowed) Set("autoupdate", StepState.Skipped, L("wiz.prog.au.userOff"));
        else Set("autoupdate", StepState.Skipped, L("wiz.prog.au.off"));
    }

    private async Task RunNvidiaStepAsync(CancellationToken ct)
    {
        Set("nvidia", StepState.Running, L("llamacpp.stage.find"), 0);
        if (LlamaCppInstaller.IsBusy) { Set("nvidia", StepState.Failed, L("llamacpp.err.busy")); return; }
        var installer = new LlamaCppInstaller(_log);
        var sw = Stopwatch.StartNew();
        var progress = new Progress<LlamaCppProgress>(p =>
        {
            if (ct.IsCancellationRequested) return;
            if (p.Stage == "download" && p.Total > 0)
            {
                double f = Math.Clamp((double)p.Done / p.Total, 0, 1);
                Set("nvidia", StepState.Running, LF("wiz.prog.mb", p.Done / 1048576, p.Total / 1048576), f * 0.9);
                double rate = p.Done / Math.Max(1, sw.Elapsed.TotalSeconds);
                if (rate > 0 && p.Done > 1 << 20)
                {
                    double secs = (p.Total - p.Done) / rate;
                    Eta = secs < 60 ? L("wiz.prog.etaSec") : LF("wiz.prog.eta", (int)Math.Ceiling(secs / 60));
                }
            }
            else Set("nvidia", StepState.Running, L("llamacpp.stage." + p.Stage), Step("nvidia").Progress < 0 ? 0 : Math.Max(Step("nvidia").Progress, p.Stage == "find" ? 0 : 0.92));
        });
        try
        {
            var info = await Task.Run(() => installer.InstallAsync(null, progress, ct), ct);
            Eta = "";
            Set("nvidia", StepState.Done, LF("wiz.prog.nv.done", info.Tag));
        }
        catch (OperationCanceledException) { Eta = ""; Set("nvidia", StepState.Skipped, L("wiz.prog.cancelled")); throw; }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("llamacpp.", StringComparison.Ordinal))
        {
            Eta = "";
            Set("nvidia", StepState.Failed, L(ex.Message));
        }
        catch (Exception ex)
        {
            Eta = "";
            _log?.Warn("Setup", "accelerator install threw", ex);
            Set("nvidia", StepState.Failed, L("llamacpp.err.network"));
        }
    }

    private async Task RunBrainXStepAsync(CancellationToken ct)
    {
        Set("brainx", StepState.Running);
        string? file = null;
        try
        {
            if (McpServerManager.FindInstalledBrainExe() == null)
            {
                var installer = new BrainXInstaller(_log);
                var dl = new Progress<(long Done, long? Total)>(p =>
                {
                    if (ct.IsCancellationRequested) return;
                    double f = p.Total is { } t && t > 0 ? Math.Clamp((double)p.Done / t, 0, 1) : -1;
                    Set("brainx", StepState.Running, p.Total is { } tt && tt > 0 ? LF("wiz.prog.mb", p.Done / 1048576, tt / 1048576) : LF("wiz.prog.bx.downloading", p.Done / 1048576), f < 0 ? -1 : f * 0.6);
                });
                try { file = await installer.DownloadAsync(dl, ct); }
                catch (InvalidOperationException ex) when (ex.Message == BrainXInstaller.ComingSoon)
                {
                    Set("brainx", StepState.Skipped, L("wiz.prog.bx.comingSoon"));
                    return;
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("brainx.", StringComparison.Ordinal))
                {
                    Set("brainx", StepState.Failed, L(ex.Message));
                    return;
                }
                Set("brainx", StepState.Running, L("wiz.prog.bx.checking"), 0.65);
                var sig = await Task.Run(() => BrainXInstaller.VerifySignature(file), ct);
                if (sig == SignatureCheck.Invalid) { Set("brainx", StepState.Failed, L("brainx.err.signature")); return; }
                if (sig == SignatureCheck.Unsigned)
                {
                    string sha = await Task.Run(() => BrainXInstaller.Sha256(file), ct);
                    if (Views.Editor.ChoiceDialog.Show(L("brainx.unsigned.title"), LF("brainx.unsigned.body", sha),
                            new[] { L("setup.confirm.cancel"), L("brainx.unsigned.run") }) != 1)
                    {
                        Set("brainx", StepState.Skipped, L("wiz.prog.bx.unsignedDeclined"));
                        return;
                    }
                }
                Set("brainx", StepState.Running, L("wiz.prog.bx.running"), 0.7);
                await installer.RunAsync(file, ct);
                Set("brainx", StepState.Running, L("wiz.prog.bx.waiting"), 0.8);
                var until = DateTime.UtcNow.AddMinutes(10);
                while (DateTime.UtcNow < until && McpServerManager.FindInstalledBrainExe() == null)
                    await Task.Delay(3000, ct);
                if (McpServerManager.FindInstalledBrainExe() == null) { Set("brainx", StepState.Skipped, L("wiz.prog.bx.notYet")); return; }
            }

            Set("brainx", StepState.Running, L("wiz.prog.bx.connecting"), 0.9);
            if (_mcp == null) { Set("brainx", StepState.Failed, L("brainx.reasonStart")); return; }
            // The app's own MCP start-up runs alongside the wizard; let it finish before connecting (it may connect itself).
            await _mcp.WaitForStartupAsync(TimeSpan.FromSeconds(45), ct);
            bool ok = _mcp.IsBrainConnected || await _mcp.ConnectInstalledBrainAsync(ct);
            if (!ok)
            {
                Set("brainx", StepState.Failed, _mcp.LastBrainConnectProblem switch
                {
                    McpServerManager.BrainConnectProblem.ConfigUnreadable => L("brainx.reasonConfig"),
                    McpServerManager.BrainConnectProblem.ConfigNotSaved => L("brainx.reasonSave"),
                    McpServerManager.BrainConnectProblem.NotInstalled => L("brainx.reasonMissing"),
                    _ => L("brainx.reasonStart"),
                });
                return;
            }
            try { if (_connections != null) await _connections.ReconcileAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log?.Warn("Setup", "moving connections to BrainX failed", ex); }
            Set("brainx", StepState.Done, L("wiz.prog.bx.done"));
        }
        catch (OperationCanceledException) { Set("brainx", StepState.Skipped, L("wiz.prog.cancelled")); throw; }
        catch (Exception ex)
        {
            _log?.Warn("Setup", "BrainX step threw", ex);
            Set("brainx", StepState.Failed, L("brainx.reasonStart"));
        }
        finally { if (file != null) BrainXInstaller.TryDelete(file); }
    }

    private void RunShortcutStep()
    {
        string? launcher = DesktopShortcut.InstalledLauncher();
        if (launcher == null) { Set("shortcut", StepState.Skipped, L("wiz.prog.sc.dev")); return; }
        if (!CreateShortcut)
        {
            Set("shortcut", StepState.Skipped, DesktopShortcut.Remove() ? L("wiz.prog.sc.removed") : L("wiz.prog.sc.off"));
            return;
        }
        try
        {
            if (!DesktopShortcut.Exists()) DesktopShortcut.Create(launcher);
            Set("shortcut", StepState.Done, L("wiz.prog.sc.done"));
        }
        catch (Exception ex)
        {
            _log?.Warn("Setup", "desktop shortcut failed", ex);
            Set("shortcut", StepState.Failed, LF("wiz.prog.sc.failed", ex.GetType().Name));
        }
    }

    private void CopyInstallReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CluadeX setup report · {AutoUpdateService.CurrentVersion} · {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        foreach (var s in Steps) sb.AppendLine($"[{s.State.ToString().ToUpperInvariant(),-7}] {s.Id,-10} {s.Detail}");
        if (_report != null) { sb.AppendLine(); sb.Append(_report.Text); }
        TrySetClipboard(ConnectionDoctor.Sanitize(sb.ToString()));
        Toast = L("acct.doc.copied");
    }

    private string _toast = "";
    /// <summary>A short line after Copy / Send.</summary>
    public string Toast { get => _toast; private set => SetProperty(ref _toast, value); }

    // ═══════════════════════════════════════════════════════════════════
    //  Done
    // ═══════════════════════════════════════════════════════════════════

    public string DoneBody => AutoUpdateEntitlement.Automatic(_activation, _settings.Settings) ? L("wiz.done.body") : L("wiz.done.bodyManual");

    // ═══════════════════════════════════════════════════════════════════
    //  Connection doctor
    // ═══════════════════════════════════════════════════════════════════

    private bool _showDoctor, _doctorBusy;
    private DoctorReport? _report;
    private string _doctorCause = "", _doctorSaved = "";
    public bool ShowDoctor { get => _showDoctor; private set => SetProperty(ref _showDoctor, value); }
    public bool DoctorBusy { get => _doctorBusy; private set { if (SetProperty(ref _doctorBusy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public ObservableCollection<DoctorLineVm> DoctorLines { get; } = new();
    public string DoctorCause { get => _doctorCause; private set => SetProperty(ref _doctorCause, value); }
    public string DoctorSaved { get => _doctorSaved; private set => SetProperty(ref _doctorSaved, value); }

    private async Task RunDoctorAsync()
    {
        ShowDoctor = true;
        DoctorBusy = true;
        DoctorLines.Clear();
        DoctorCause = L("acct.doc.running");
        DoctorSaved = "";
        Toast = "";
        try
        {
            var rep = await _doctor.RunAsync(_account.LastError, _life.Token);
            _report = rep;
            foreach (var s in rep.Steps)
                DoctorLines.Add(new DoctorLineVm(L("acct.doc.step." + s.Id), s.Status, s.Status == DoctorStatus.Skip && string.IsNullOrEmpty(s.Reason) ? L("acct.doc.skipped") : s.Reason ?? "", s.Detail));
            DoctorCause = rep.AllGood ? L("acct.doc.allGood") : rep.Cause ?? "";
            DoctorSaved = rep.SavedPath != null ? LF("acct.doc.saved", rep.SavedPath) : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log?.Warn("Setup", "doctor threw", ex); DoctorCause = L("acct.err.generic"); }
        finally { DoctorBusy = false; }
    }

    private void CopyDoctor()
    {
        if (_report == null) return;
        TrySetClipboard(_report.Text);
        Toast = L("acct.doc.copied");
    }

    private async Task SendDoctorAsync()
    {
        if (_report == null) return;
        DoctorBusy = true;
        try { Toast = (await _doctor.SendToTeamAsync(_report, _life.Token)).Message; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log?.Warn("Setup", "send report threw", ex); Toast = L("acct.doc.send.fail"); }
        finally { DoctorBusy = false; }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Commands, helpers, teardown
    // ═══════════════════════════════════════════════════════════════════

    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand TermsCommand { get; }
    public ICommand PrivacyCommand { get; }
    public ICommand ChooseAccountCommand { get; }
    public ICommand ChooseQuickCommand { get; }
    public ICommand LicenseNextCommand { get; }
    public ICommand ShowKeyBoxCommand { get; }
    public ICommand CloseKeyBoxCommand { get; }
    public ICommand ActivateKeyCommand { get; }
    public ICommand OpenQrCommand { get; }
    public ICommand QrTabCommand { get; }
    public ICommand CloseQrCommand { get; }
    public ICommand NewCodeCommand { get; }
    public ICommand SignInHereCommand { get; }
    public ICommand OpenStoreCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand CancelInstallCommand { get; }
    public ICommand ContinueCommand { get; }
    public ICommand CopyInstallReportCommand { get; }
    public ICommand OpenAppCommand { get; }
    public ICommand DoneCloseCommand { get; }
    public ICommand RunDoctorCommand { get; }
    public ICommand CopyDoctorCommand { get; }
    public ICommand SendDoctorCommand { get; }
    public ICommand CloseDoctorCommand { get; }
    public ICommand ToggleLanguageCommand { get; }

    private void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { _log?.Warn("Setup", "could not open the browser", ex); }
    }

    private static void TrySetClipboard(string text)
    {
        for (int i = 0; i < 3; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(60); }
        }
    }

    /// <summary>The window is closing: stop polling, downloads and timers; record that the wizard ran.</summary>
    public void Dispose()
    {
        if (_life.IsCancellationRequested) return;
        _life.Cancel();
        _qrCts?.Cancel();
        _installCts?.Cancel();
        _clock.Stop();
        _loc.LanguageChanged -= OnLanguageChanged;
        _password = "";
        try { _settings.UpdateSettings(s => s.SetupWizardCompleted = true); } catch { }
    }

    // ── test hooks: the themetest renders put the wizard in a given state without a server ──

    /// <summary>For the renders only: show a page as it looks with the given server state.</summary>
    public void PreviewPage(WizardPage page, AppAuthHealth? health = null)
    {
        if (health != null)
        {
            _health = health; CheckingServer = false; _healthRun++;
            // as CheckServerAsync does: no sign-in on the server → the quick path is picked for the user
            if (!AccountSignInAvailable && QuickPathOffered) UseQuickPath = true;
            RaiseAuthFlags(); RefreshLicenseTexts();
        }
        if (page == WizardPage.Components) PrepareComponents();
        Page = page;
    }

    public void PreviewComponents(bool nvidia, string gpuName, long bytes, bool brainXInstalled)
    {
        _componentsReady = true;
        NvidiaDetected = nvidia; _gpuName = gpuName; _nvidiaInstalled = false; _nvidiaBytes = bytes;
        OnPropertyChanged(nameof(NvidiaSelectable));
        NvidiaOn = nvidia;
        BrainXInstalled = brainXInstalled;
        RefreshComponentTexts();
        Page = WizardPage.Components;
    }

    public void PreviewQr(string userCode, string uri, TimeSpan left, bool expired = false)
    {
        ShowQr = true;
        UserCode = userCode;
        QrImage = expired ? null : WatchViewModel.MakeQr(uri);
        QrExpired = expired;
        QrStatus = expired ? L("acct.err.expiredToken") : L("wiz.qr.waiting");
        QrCountdown = expired ? "" : LF("wiz.qr.expiresIn", $"{(int)left.TotalMinutes}:{left.Seconds:00}");
    }

    public void PreviewLicenseError(string code) => LicenseError = AccountErrors.Describe(code, _loc);

    public void PreviewKeyBox(string text, string? errorCode) { ShowKeyBox = true; KeyText = text; KeyError = errorCode == null ? "" : AccountErrors.Describe(errorCode, _loc); }

    public void PreviewProgress(IEnumerable<(string Id, StepState State, string Detail, double Progress)> steps, bool finished)
    {
        Steps.Clear();
        foreach (var (id, st, d, p) in steps) Steps.Add(new WizardStepVm { Id = id, Label = L("wiz.prog." + id), State = st, Detail = d, Progress = p });
        RecomputeOverall();
        Tip = L("wiz.tip.1");
        _installFinished = finished;
        _anyFailed = Steps.Any(s => s.State == StepState.Failed);
        OnPropertyChanged(nameof(InstallNeedsAttention)); OnPropertyChanged(nameof(ShowOverall)); OnPropertyChanged(nameof(ProgressTitle));
        Eta = finished ? "" : LF("wiz.prog.eta", 1);
        Page = WizardPage.Progress;
    }

    public void PreviewDoctor(DoctorReport rep)
    {
        ShowDoctor = true;
        _report = rep;
        DoctorLines.Clear();
        foreach (var s in rep.Steps)
            DoctorLines.Add(new DoctorLineVm(L("acct.doc.step." + s.Id), s.Status, s.Status == DoctorStatus.Skip && string.IsNullOrEmpty(s.Reason) ? L("acct.doc.skipped") : s.Reason ?? "", s.Detail));
        DoctorCause = rep.AllGood ? L("acct.doc.allGood") : rep.Cause ?? "";
        CommandManager.InvalidateRequerySuggested();
    }
}
