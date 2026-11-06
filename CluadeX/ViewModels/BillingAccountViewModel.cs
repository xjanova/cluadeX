using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services;
using CluadeX.Services.Billing;

namespace CluadeX.ViewModels;

/// <summary>
/// The Billing page's credit &amp; plan editor (from the old spend window): an API account's prepaid credit (CluadeX counts
/// down from it — only that provider's recorded API spend is deducted), or a plan CLI's monthly fee and the day it
/// started. Edits <see cref="Models.AppSettings.ProviderSpending"/> exactly as before. On a provider tab it edits that
/// provider; on All (and Other) a picker chooses. Clearing asks first.
/// </summary>
public sealed class BillingAccountViewModel : ViewModelBase
{
    private readonly CostTrackingService _cost;
    private readonly SettingsService _settings;
    private DateTime _savedAt = DateTime.MinValue;

    /// <summary>A credit or plan was saved or cleared (the page redraws its budgets and the comparison).</summary>
    public event Action? Changed;

    /// <summary>Asks before a clear; the harness answers itself.</summary>
    public Func<string, bool> Confirm { get; set; } = text =>
        System.Windows.MessageBox.Show(text, "CluadeX", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
        == System.Windows.MessageBoxResult.Yes;

    public BillingAccountViewModel(CostTrackingService cost, SettingsService settings)
    {
        _cost = cost;
        _settings = settings;
        SetBalanceCommand = new RelayCommand(SetBalance);
        ClearBalanceCommand = new RelayCommand(ClearBalance);
        SavePackageCommand = new RelayCommand(SavePackage);
        ClearPackageCommand = new RelayCommand(ClearPackage);
        OpenSiteCommand = new RelayCommand(() =>
        {
            if (BillingSites.For(SelectedProvider?.Key) is { } url)
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { /* no browser registered */ }
        });
        EnsureProviders();
        _selected = Providers.First();
        Load();
    }

    private static string L(string key) => LocalizedResources.Instance[key];
    private static string LF(string key, params object[] args) => string.Format(L(key), args);
    private static string Usd(decimal v) => UsdText.Format(v);

    /// <summary>One account that can carry a credit or a plan fee (a ledger key: "Google" is the Gemini API).</summary>
    public sealed record AccountChoice(string Key, string Label, ImageSource? Logo)
    {
        public bool HasLogo => Logo != null;
        public override string ToString() => Label;
    }

    public ObservableCollection<AccountChoice> Providers { get; } = new();

    private AccountChoice _selected;
    public AccountChoice SelectedProvider
    {
        get => _selected;
        set { if (value != null && SetProperty(ref _selected, value)) Load(); }
    }

    private bool _canPick = true, _hasEditor = true, _hasBalance;
    private string _balanceText = "", _balanceDetail = "", _balanceInput = "", _balanceError = "", _packagePrice = "", _title = "";
    private DateTime? _packageStart = DateTime.Today;

    /// <summary>On All / Other the picker chooses the account; on a provider tab it is that provider.</summary>
    public bool CanPick { get => _canPick; private set => SetProperty(ref _canPick, value); }
    /// <summary>False on a local tab (free, nothing to enter).</summary>
    public bool HasEditor { get => _hasEditor; private set => SetProperty(ref _hasEditor, value); }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public bool IsPackageBilling => ProviderCostProjection.IsPackage(SelectedProvider.Key);
    public bool IsApiBilling => !IsPackageBilling && !IsLocal(SelectedProvider.Key);
    public bool HasSite => BillingSites.For(SelectedProvider.Key) != null;
    public bool HasBalance { get => _hasBalance; private set => SetProperty(ref _hasBalance, value); }
    public string BalanceText { get => _balanceText; private set => SetProperty(ref _balanceText, value); }
    public string BalanceDetail { get => _balanceDetail; private set => SetProperty(ref _balanceDetail, value); }
    public string BalanceInput { get => _balanceInput; set => SetProperty(ref _balanceInput, value); }
    /// <summary>A refused entry (credit or plan), in words; empty when fine.</summary>
    public string BalanceError { get => _balanceError; private set => SetProperty(ref _balanceError, value); }
    public string PackagePriceInput { get => _packagePrice; set => SetProperty(ref _packagePrice, value); }
    public DateTime? PackageStartDate { get => _packageStart; set => SetProperty(ref _packageStart, value); }
    public bool HasPackage => Billing(SelectedProvider.Key)?.MonthlyPackageUsd != null;
    public DateTime Today => DateTime.Today;
    /// <summary>The old single credit entry (before credit was kept per provider): kept, never assigned to one.</summary>
    public bool HasLegacyCredit => _settings.Settings.CreditBalanceUsd != null;
    public string LegacyCreditText => LF("usage.legacyCredit", Usd(_settings.Settings.CreditBalanceUsd ?? 0));

    public ICommand SetBalanceCommand { get; }
    public ICommand ClearBalanceCommand { get; }
    public ICommand SavePackageCommand { get; }
    public ICommand ClearPackageCommand { get; }
    public ICommand OpenSiteCommand { get; }

    private static bool IsLocal(string key) => ProviderBrand.KindOf(ProviderBrand.FromLedgerKey(key)) == BillingKind.Local && key is not ("Other" or CostTrackingService.UnattributedProvider);

    /// <summary>The ledger key of a Billing tab (the Gemini API's tab is "Gemini", its ledger key "Google").</summary>
    public static string LedgerKeyOf(string tabKey) => tabKey == "Gemini" ? "Google" : tabKey;

    private Models.ProviderSpendSettings? Billing(string key) => _settings.Settings.ProviderSpending?.GetValueOrDefault(key);

    private void EnsureProviders(string? extra = null)
    {
        void Add(string key)
        {
            if (key == CostTrackingService.UnattributedProvider || IsLocal(key) || Providers.Any(p => p.Key == key)) return;
            string tab = ProviderBrand.FromLedgerKey(key);
            var brand = ProviderBrand.For(tab);
            bool own = tab != "Other" || key == "Other";
            Providers.Add(new AccountChoice(key, key == "Other" ? L("usage.providerOther") : own ? brand.Name : key,
                own ? BillingViewModel.BillingTab.LoadLogo(brand.LogoUri) : null));
        }
        foreach (var key in new[] { "Anthropic", "OpenAI", "Google", "XAI", "ClaudeDev", "OpenAIDev", "GeminiDev", "AntigravityDev", "GrokDev", "BrainX" }) Add(key);
        foreach (var key in _cost.RecordedProviders.Union(_settings.Settings.ProviderSpending?.Keys.AsEnumerable() ?? Enumerable.Empty<string>())
                     .OrderBy(k => k, StringComparer.Ordinal))
            Add(key);
        if (extra != null) Add(extra);
    }

    /// <summary>The Billing tab on screen (also called on every refresh of the page while it is open: the same account
    /// keeps what is being typed into its boxes, and only its remaining credit is re-read).</summary>
    public void FollowTab(string tabKey)
    {
        bool pick = tabKey is ProviderBrand.AllKey or "Other";
        CanPick = pick;
        HasEditor = pick || ProviderBrand.KindOf(tabKey) != BillingKind.Local;
        if (!pick && HasEditor)
        {
            string key = LedgerKeyOf(tabKey);
            EnsureProviders(key);
            if (Providers.FirstOrDefault(p => p.Key == key) is { } p) SelectedProvider = p;   // a different account loads its own values
        }
        Refresh();
    }

    /// <summary>Re-read the settings and the ledger (the page came on screen, or spend moved on).</summary>
    public void Refresh()
    {
        EnsureProviders();
        UpdateBalance();
        OnPropertyChanged(nameof(HasLegacyCredit));
        OnPropertyChanged(nameof(LegacyCreditText));
    }

    private void Load()
    {
        var b = Billing(SelectedProvider.Key);
        BalanceInput = ""; BalanceError = "";
        PackagePriceInput = b?.MonthlyPackageUsd?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        PackageStartDate = b?.PackageStartDate ?? DateTime.Today;
        Title = LF(IsPackageBilling ? "billing.account.planTitle" : "billing.account.creditTitle", SelectedProvider.Label);
        OnPropertyChanged(nameof(IsPackageBilling)); OnPropertyChanged(nameof(IsApiBilling));
        OnPropertyChanged(nameof(HasSite)); OnPropertyChanged(nameof(HasPackage));
        Refresh();
    }

    private void UpdateBalance()
    {
        var b = Billing(SelectedProvider.Key);
        HasBalance = b?.CreditBalanceUsd != null && b.CreditSetAt != null;
        if (HasBalance)
        {
            decimal spent = _cost.SpentSince(b!.CreditSetAt!.Value, b.CreditSpentOnAnchorDay, SelectedProvider.Key);
            BalanceText = LF("usage.balanceValue", Usd(b.CreditBalanceUsd!.Value - spent));
            BalanceDetail = LF("usage.balanceSince", Usd(b.CreditBalanceUsd.Value), b.CreditSetAt.Value.ToString("d MMM HH:mm", CultureInfo.CurrentUICulture), Usd(spent));
        }
        else { BalanceText = ""; BalanceDetail = L("usage.providerBalanceNone"); }
    }

    // A second click right after a save finds the box already emptied: that is the same save, not a bad entry.
    private bool JustSaved(string? input) => string.IsNullOrWhiteSpace(input) && DateTime.UtcNow - _savedAt < TimeSpan.FromSeconds(1.5);

    private void SetBalance()
    {
        if (JustSaved(BalanceInput)) return;
        if (!IsApiBilling || !UsdText.TryParse(BalanceInput, out decimal usd))
        {
            BalanceError = L("usage.balanceBad");
            return;
        }
        BalanceError = "";
        string key = SelectedProvider.Key;
        decimal today = _cost.Between(DateTime.Today, DateTime.Today, key).Sum(d => d.Usd);
        _settings.UpdateSettings(s =>
        {
            s.ProviderSpending ??= new();
            if (!s.ProviderSpending.TryGetValue(key, out var b)) s.ProviderSpending[key] = b = new();
            b.CreditBalanceUsd = usd; b.CreditSetAt = DateTime.Now; b.CreditSpentOnAnchorDay = today;
        });
        BalanceInput = "";
        _savedAt = DateTime.UtcNow;
        UpdateBalance();
        Changed?.Invoke();
    }

    private void ClearBalance()
    {
        if (Billing(SelectedProvider.Key) is not { CreditBalanceUsd: not null } b) return;
        if (!Confirm(LF("billing.account.clearCreditAsk", SelectedProvider.Label))) return;
        _settings.UpdateSettings(_ => { b.CreditBalanceUsd = null; b.CreditSetAt = null; b.CreditSpentOnAnchorDay = 0; });
        BalanceError = "";
        UpdateBalance();
        Changed?.Invoke();
    }

    private void SavePackage()
    {
        if (!IsPackageBilling || !UsdText.TryParse(PackagePriceInput, out var fee) || PackageStartDate == null || PackageStartDate.Value.Date > DateTime.Today)
        { BalanceError = L("usage.packageBad"); return; }
        string key = SelectedProvider.Key;
        var start = PackageStartDate.Value.Date;
        if (Billing(key) is { } old && old.MonthlyPackageUsd == fee && old.PackageStartDate?.Date == start) { BalanceError = ""; return; }   // nothing changed
        _settings.UpdateSettings(s =>
        {
            s.ProviderSpending ??= new();
            if (!s.ProviderSpending.TryGetValue(key, out var b)) s.ProviderSpending[key] = b = new();
            b.MonthlyPackageUsd = fee; b.PackageStartDate = start;
        });
        BalanceError = "";
        OnPropertyChanged(nameof(HasPackage));
        Changed?.Invoke();
    }

    private void ClearPackage()
    {
        if (Billing(SelectedProvider.Key) is not { MonthlyPackageUsd: not null } b) return;
        if (!Confirm(LF("billing.account.clearPlanAsk", SelectedProvider.Label))) return;
        _settings.UpdateSettings(_ => { b.MonthlyPackageUsd = null; b.PackageStartDate = null; });
        Load();
        Changed?.Invoke();
    }
}
