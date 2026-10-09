using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>One plan card on the license dialog ("Yearly ฿199 per year [Buy]").</summary>
public sealed class LicensePlanCard
{
    public string Name { get; init; } = "";
    public string Price { get; init; } = "";
    public string Per { get; init; } = "";
    public bool IsBest { get; init; }
    public string BestLabel { get; init; } = "";
}

/// <summary>
/// The license dialog: invites a purchase when there is no key, counts a trial down, takes a key, offers the
/// one free trial when the server would grant it, and removes a key (after confirming).
/// </summary>
public class LicenseViewModel : ViewModelBase
{
    private readonly ActivationService _activation;
    private readonly XmanLicenseService _license;
    private readonly LocalizationService _loc;
    private string _purchaseUrl = XmanLicenseService.DefaultPurchaseUrl;
    private string _pendingTransferKey = "";

    public LicenseViewModel(ActivationService activation, XmanLicenseService license, LocalizationService loc,
        string? banner = null)
    {
        _activation = activation;
        _license = license;
        _loc = loc;
        _banner = banner ?? "";

        ActivateCommand = new AsyncRelayCommand(ActivateAsync, () => !IsBusy);
        TransferCommand = new AsyncRelayCommand(TransferAsync, () => !IsBusy);
        StartTrialCommand = new AsyncRelayCommand(StartTrialAsync, () => !IsBusy);
        DeactivateCommand = new AsyncRelayCommand(DeactivateAsync, () => !IsBusy);
        BuyCommand = new RelayCommand(OpenStore);

        _activation.ActivationChanged += OnActivationChanged;
        _activation.DeviceConflictDetected += OnDeviceConflict;

        ShowPlans(PricingInfo.Fallback());
        Refresh();
        _ = LoadPricingAsync();
    }

    /// <summary>The window calls this when it closes, so a closed dialog stops listening.</summary>
    public void Detach()
    {
        _activation.ActivationChanged -= OnActivationChanged;
        _activation.DeviceConflictDetected -= OnDeviceConflict;
    }

    private string T(string key) => _loc.T(key);
    private string T(string key, params object[] args) => _loc.T(key, args);

    // ── State ──
    private string _title = "", _subtitle = "", _banner, _keyInput = "", _message = "", _statusKey = "";
    private bool _isActive, _isTrial, _isBusy, _messageIsError, _showTransfer, _canStartTrial;

    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => SetProperty(ref _subtitle, value); }
    /// <summary>Why the dialog opened by itself (trial issued, trial over, license restored) — empty otherwise.</summary>
    public string Banner { get => _banner; private set { if (SetProperty(ref _banner, value)) OnPropertyChanged(nameof(HasBanner)); } }
    public bool HasBanner => !string.IsNullOrEmpty(_banner);
    /// <summary>A license is active (trial included).</summary>
    public bool IsActive { get => _isActive; private set => SetProperty(ref _isActive, value); }
    public bool IsTrial { get => _isTrial; private set => SetProperty(ref _isTrial, value); }
    /// <summary>A bought key is active — no plans, no key box, only its details and "remove".</summary>
    public bool IsPaid => IsActive && !IsTrial;
    /// <summary>Plans and the key box: anyone without a bought key.</summary>
    public bool ShowPurchase => !IsPaid;
    public string StatusKey { get => _statusKey; private set => SetProperty(ref _statusKey, value); }
    public bool CanStartTrial { get => _canStartTrial; private set => SetProperty(ref _canStartTrial, value); }
    public string KeyInput { get => _keyInput; set => SetProperty(ref _keyInput, value); }
    public string Message { get => _message; private set { if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrEmpty(_message);
    public bool MessageIsError { get => _messageIsError; private set => SetProperty(ref _messageIsError, value); }
    public bool ShowTransfer { get => _showTransfer; private set => SetProperty(ref _showTransfer, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public ObservableCollection<LicensePlanCard> Plans { get; } = new();
    public string DeviceId => T("license.deviceId", XmanLicenseService.GetMachineFingerprint()[..12]);

    public ICommand ActivateCommand { get; }
    public ICommand TransferCommand { get; }
    public ICommand StartTrialCommand { get; }
    public ICommand DeactivateCommand { get; }
    public ICommand BuyCommand { get; }

    private void OnActivationChanged() => Application.Current?.Dispatcher.BeginInvoke(Refresh);

    private void OnDeviceConflict(string key, string _)
        => Application.Current?.Dispatcher.BeginInvoke(() => { _pendingTransferKey = key; ShowTransfer = true; });

    private void Refresh()
    {
        IsActive = _activation.IsActivated;
        IsTrial = _activation.IsTrial;
        OnPropertyChanged(nameof(IsPaid));
        OnPropertyChanged(nameof(ShowPurchase));
        CanStartTrial = _activation.CanRequestTrial;

        var masked = _activation.MaskedKey;
        StatusKey = IsActive && masked != null ? T("license.keyLabel", masked) : "";

        if (IsTrial)
        {
            Title = T("license.title.trial");
            Subtitle = T("license.sub.trial", TimeLeft(_activation.ExpiresAtUtc, _loc), LocalTime(_activation.ExpiresAtUtc));
        }
        else if (IsActive)
        {
            Title = T("license.title.active");
            Subtitle = _activation.ExpiresAtUtc is { } until
                ? T("license.sub.active", _activation.TierDisplayName, LocalTime(until))
                : T("license.sub.lifetime", _activation.TierDisplayName);
        }
        else
        {
            Title = T("license.title.none");
            Subtitle = T("license.sub.none");
        }
    }

    private async Task LoadPricingAsync()
    {
        var info = await _license.GetPricingAsync();
        Application.Current?.Dispatcher.BeginInvoke(() => ShowPlans(info));
    }

    private void ShowPlans(PricingInfo info)
    {
        _purchaseUrl = XmanLicenseService.SafeStoreUrl(info.PurchaseUrl);
        Plans.Clear();
        foreach (var p in info.Plans)
        {
            string type = p.Type.ToLowerInvariant();
            string name = type is "monthly" or "yearly" or "lifetime" ? T("license.plan." + type) : p.Type;
            string per = type is "monthly" or "yearly" or "lifetime" ? T("license.plan.per." + type) : "";
            string symbol = p.Currency.Equals("THB", StringComparison.OrdinalIgnoreCase) ? "฿" : p.Currency + " ";
            Plans.Add(new LicensePlanCard
            {
                Name = name,
                Price = symbol + p.Price.ToString("#,0.##", CultureInfo.InvariantCulture),
                Per = per,
                IsBest = type == "lifetime",
                BestLabel = T("license.plan.best"),
            });
        }
    }

    // ── Actions ──

    private async Task ActivateAsync()
    {
        var key = (KeyInput ?? "").Trim();
        if (key.Length == 0) { Say(T("license.err.empty"), error: true); return; }

        IsBusy = true;
        ShowTransfer = false;
        Say(T("license.activating"), error: false);
        try
        {
            var (ok, message, code) = await _activation.ActivateAsync(key);
            if (ok)
            {
                KeyInput = "";
                Banner = "";
                Say(T("license.activated", _activation.TierDisplayName), error: false);
            }
            else if (code == "ALREADY_ACTIVATED_OTHER_DEVICE")
            {
                _pendingTransferKey = key;
                ShowTransfer = true;
                Say(T("license.conflict"), error: true);
            }
            else Say(Explain(code, message), error: true);
        }
        finally { IsBusy = false; }
    }

    private async Task TransferAsync()
    {
        var key = _pendingTransferKey.Length > 0 ? _pendingTransferKey : (KeyInput ?? "").Trim();
        if (key.Length == 0) return;

        IsBusy = true;
        try
        {
            var (ok, message) = await _activation.ActivateForceAsync(key);
            if (ok)
            {
                KeyInput = "";
                _pendingTransferKey = "";
                ShowTransfer = false;
                Say(T("license.moved", _activation.TierDisplayName), error: false);
            }
            else Say(string.IsNullOrEmpty(message) ? T("license.offline") : message, error: true);
        }
        finally { IsBusy = false; }
    }

    private async Task StartTrialAsync()
    {
        IsBusy = true;
        Say(T("license.trialStarting"), error: false);
        try
        {
            var (ok, code, message) = await _activation.StartTrialAsync();
            if (ok)
            {
                Banner = "";
                Say(T("license.activated", _activation.TierDisplayName), error: false);
            }
            else Say(Explain(code, message), error: true);
            CanStartTrial = _activation.CanRequestTrial;
        }
        finally { IsBusy = false; }
    }

    private async Task DeactivateAsync()
    {
        var answer = MessageBox.Show(T("license.deactivateConfirm"), "CluadeX",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            await _activation.DeactivateAsync();
            Say(T("license.deactivated"), error: false);
        }
        finally { IsBusy = false; }
    }

    private void OpenStore()
    {
        try { Process.Start(new ProcessStartInfo(XmanLicenseService.SafeStoreUrl(_purchaseUrl)) { UseShellExecute = true }); }
        catch (Exception ex) { Say(ex.Message, error: true); }
    }

    private void Say(string text, bool error)
    {
        MessageIsError = error;
        Message = text;
    }

    /// <summary>A server error code in the user's language; the server's own (Thai) message when we have no
    /// better words for it.</summary>
    private string Explain(string? code, string serverMessage) => code switch
    {
        XmanLicenseService.ErrorOffline => T("license.offline"),
        "EMPTY" => T("license.err.empty"),
        "INVALID_LICENSE" => T("license.err.invalid"),
        "LICENSE_REVOKED" => T("license.err.revoked"),
        "LICENSE_EXPIRED" => T("license.err.expired"),
        "TRIAL_NOT_AVAILABLE" or "TOO_MANY_ATTEMPTS" or "TRIAL_USED_ON_THIS_HARDWARE" => T("license.trialUnavailable"),
        "TRIAL_ABUSE_DETECTED" or "DEVICE_BLOCKED" => T("license.trialBlocked"),
        _ => string.IsNullOrWhiteSpace(serverMessage) ? T("license.offline") : serverMessage,
    };

    // ── Formatting shared with the title-bar chip ──

    /// <summary>"23 h", "5 d", "40 min" until <paramref name="untilUtc"/>.</summary>
    public static string TimeLeft(DateTime? untilUtc, LocalizationService loc)
    {
        if (untilUtc is not { } until) return "";
        var left = until - DateTime.UtcNow;
        if (left <= TimeSpan.Zero) return loc.T("license.time.minutes", 0);
        if (left.TotalHours >= 48) return loc.T("license.time.days", (int)Math.Ceiling(left.TotalDays));
        if (left.TotalMinutes >= 90) return loc.T("license.time.hours", (int)Math.Ceiling(left.TotalHours));
        return loc.T("license.time.minutes", (int)Math.Ceiling(left.TotalMinutes));
    }

    public static string LocalTime(DateTime? utc)
        => utc is { } u ? u.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture) : "";
}
