using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>Provider comparisons over selectable dates, monthly totals, independent API credit anchors and
/// package estimates. An entered package fee is allocated daily on charts and counted in full in monthly totals.</summary>
public sealed class UsageViewModel : ViewModelBase
{
    public const double ChartHeight = 150;

    private readonly CostTrackingService _cost;
    private readonly SettingsService _settings;
    private readonly Action? _openBilling;
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;
    private bool _refreshQueued;

    public sealed record Period(string Title, string Paid, string Without, string Saved, bool HasUse);
    public sealed record Bar(string Label, double PaidHeight, double SavedHeight, string Tip, IReadOnlyList<BarSegment>? Segments = null);
    public sealed record SavingRow(string Name, string Amount, double Fraction);

    public ObservableCollection<Period> Periods { get; } = new();
    public ObservableCollection<Bar> Bars { get; } = new();
    public ObservableCollection<SavingRow> Savings { get; } = new();

    /// <summary>One dispatch strategy over the selected dates (CostTrackingService.StrategyBreakdown): runs, success share,
    /// cost per run, time per run — the numbers DispatchPolicy's thresholds are tuned from.</summary>
    public sealed record StrategyRow(string Name, string Runs, string Success, string CostPerRun, string TimePerRun, string Total, double Fraction);
    public ObservableCollection<StrategyRow> Strategies { get; } = new();
    public bool HasStrategies => Strategies.Count > 0;

    // Comparison lines share one scale; the optional total/baseline band keeps the savings comparison.
    public const double LineWidth = 620, LineHeight = 200;
    public sealed record LinePoint(double X, double Y, string Tip, System.Windows.Media.Brush? Color = null);
    private System.Windows.Media.PointCollection _paidLine = new(), _baseLine = new(), _gapArea = new(), _paidArea = new();
    public System.Windows.Media.PointCollection PaidLine { get => _paidLine; private set => SetProperty(ref _paidLine, value); }
    public System.Windows.Media.PointCollection BaseLine { get => _baseLine; private set => SetProperty(ref _baseLine, value); }
    /// <summary>The band between the two lines (the savings).</summary>
    public System.Windows.Media.PointCollection GapArea { get => _gapArea; private set => SetProperty(ref _gapArea, value); }
    /// <summary>Under the paid line, down to the axis.</summary>
    public System.Windows.Media.PointCollection PaidArea { get => _paidArea; private set => SetProperty(ref _paidArea, value); }
    public ObservableCollection<LinePoint> PaidDots { get; } = new();
    public ObservableCollection<LinePoint> BaseDots { get; } = new();
    private string _lineSaved = "", _lineScaleTop = "", _lineFirst = "", _lineLast = "";
    public string LineSaved { get => _lineSaved; private set => SetProperty(ref _lineSaved, value); }
    public string LineScaleTop { get => _lineScaleTop; private set => SetProperty(ref _lineScaleTop, value); }
    public string LineFirstDay { get => _lineFirst; private set => SetProperty(ref _lineFirst, value); }
    public string LineLastDay { get => _lineLast; private set => SetProperty(ref _lineLast, value); }

    private string _balanceText = "", _balanceDetail = "", _balanceInput = "", _balanceError = "";
    private bool _hasBalance;
    public string BalanceText { get => _balanceText; private set => SetProperty(ref _balanceText, value); }
    public string BalanceDetail { get => _balanceDetail; private set => SetProperty(ref _balanceDetail, value); }
    public bool HasBalance { get => _hasBalance; private set => SetProperty(ref _hasBalance, value); }
    public string BalanceInput { get => _balanceInput; set => SetProperty(ref _balanceInput, value); }
    public string BalanceError { get => _balanceError; private set => SetProperty(ref _balanceError, value); }
    public bool HasBilling => _openBilling != null && SelectedBillingProvider?.Key ==
        (_settings.Settings.ActiveProvider == CluadeX.Models.AiProviderType.Gemini ? "Google" : _settings.Settings.ActiveProvider.ToString());

    public ICommand SetBalanceCommand { get; }
    public ICommand ClearBalanceCommand { get; }
    public ICommand OpenBillingCommand { get; }

    public UsageViewModel(CostTrackingService cost, SettingsService settings, Action? openBilling)
    {
        _cost = cost;
        _settings = settings;
        _openBilling = openBilling;
        _dispatcher = System.Windows.Application.Current?.Dispatcher;
        SetBalanceCommand = new RelayCommand(SetBalance);
        ClearBalanceCommand = new RelayCommand(() =>
        {
            if (SelectedBillingProvider is { } p && Billing(p.Key) is { } b)
                _settings.UpdateSettings(_ => { b.CreditBalanceUsd = null; b.CreditSetAt = null; b.CreditSpentOnAnchorDay = 0; });
            Refresh();
        });
        OpenBillingCommand = new RelayCommand(() => _openBilling?.Invoke());
        InitializeFilters();
        _cost.OnCostUpdated += OnCostUpdated;
        Refresh();
    }

    /// <summary>Stop following new spend (the window closed).</summary>
    public void Detach() => _cost.OnCostUpdated -= OnCostUpdated;

    private void OnCostUpdated(CostSummary _)
    {
        // Requests report usage several times each (start, deltas, end): one refresh per burst is plenty.
        if (_dispatcher == null || _refreshQueued) return;
        _refreshQueued = true;
        _dispatcher.InvokeAsync(() => { _refreshQueued = false; Refresh(); }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static string L(string key) => LocalizedResources.Instance[key];
    private static string LF(string key, params object[] args) => string.Format(L(key), args);

    /// <summary>Keep sub-cent API costs visible instead of rounding a cheap request to zero.</summary>
    public static string Usd(decimal v) =>
        "$" + (Math.Abs(v) < 0.01m && v != 0 ? v.ToString("0.########", CultureInfo.InvariantCulture) : v.ToString("N2", CultureInfo.InvariantCulture));

    public sealed class ProviderChoice : ViewModelBase
    {
        private readonly Action _changed;
        private bool _selected = true;
        private string _totalText = "";
        public string Key { get; }
        public string Label { get; }
        public System.Windows.Media.Brush Color { get; }
        public bool IsSelected { get => _selected; set { if (SetProperty(ref _selected, value)) _changed(); } }
        public bool HasData { get; private set; }
        public decimal Total { get; private set; }
        public string TotalText { get => _totalText; private set => SetProperty(ref _totalText, value); }
        public override string ToString() => Label;
        public ProviderChoice(string key, string label, string color, Action changed)
        {
            Key = key; Label = label; _changed = changed;
            var brush = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromInvariantString(color)!;
            brush.Freeze(); Color = brush;
        }
        public void Update(IReadOnlyList<CostTrackingService.UsageDay> days)
        {
            HasData = days.Any(d => d.Requests > 0 || d.Usd != 0 || d.InputTokens != 0 || d.OutputTokens != 0);
            Total = days.Sum(d => d.Usd);
            TotalText = HasData ? (ProviderCostProjection.IsPackage(Key) ? "≈ " : "") + Usd(Total) : L("usage.noRecordedUse");
            OnPropertyChanged(nameof(HasData));
        }
    }
    public sealed record RangeChoice(string Key, string Label, int? Days)
    {
        public override string ToString() => Label;
    }
    public sealed record ProviderLine(string Label, System.Windows.Media.Brush Color,
        System.Windows.Media.PointCollection Points, IReadOnlyList<LinePoint> Dots);
    public sealed record BarSegment(double Height, System.Windows.Media.Brush Color);
    public ObservableCollection<ProviderChoice> Providers { get; } = new();
    public ObservableCollection<ProviderChoice> BillingProviders { get; } = new();
    public ObservableCollection<MonthlyTotal> MonthlyTotals { get; } = new();
    public sealed record MonthlyTotal(DateTime Month, decimal ApiUsd, decimal PackageUsd, decimal BaselineUsd, bool MissingUsage, bool HasProxy)
    {
        public string Label => Month.ToString("MMM yyyy", CultureInfo.CurrentUICulture);
        public string Api => Usd(ApiUsd);
        public string Packages => "≈ " + Usd(PackageUsd);
        public string Total => "≈ " + Usd(ApiUsd + PackageUsd);
        public string Saved => MissingUsage ? L("usage.insufficientPackageUsage") : "≈ " + Usd(BaselineUsd - ApiUsd - PackageUsd);
        public bool IsSaving => BaselineUsd >= ApiUsd + PackageUsd;
        public string Note => HasProxy ? L("usage.proxyFee") : L("usage.monthlyBasis");
    }
    private ProviderChoice? _billingProvider;
    public ProviderChoice? SelectedBillingProvider
    {
        get => _billingProvider;
        set { if (SetProperty(ref _billingProvider, value)) LoadBilling(); }
    }
    public bool IsPackageBilling => SelectedBillingProvider != null && ProviderCostProjection.IsPackage(SelectedBillingProvider.Key);
    public bool IsApiBilling => SelectedBillingProvider != null && !IsPackageBilling && SelectedBillingProvider.Key is not ("Local" or "LlamaServer" or "Ollama");
    public bool HasLegacyCredit => _settings.Settings.CreditBalanceUsd != null;
    public string LegacyCreditText => LF("usage.legacyCredit", Usd(_settings.Settings.CreditBalanceUsd ?? 0));
    private string _packagePrice = "";
    private DateTime? _packageStart = DateTime.Today;
    public string PackagePriceInput { get => _packagePrice; set => SetProperty(ref _packagePrice, value); }
    public DateTime? PackageStartDate { get => _packageStart; set => SetProperty(ref _packageStart, value); }
    public ICommand SavePackageCommand { get; private set; } = null!;
    public ICommand ClearPackageCommand { get; private set; } = null!;
    private CluadeX.Models.ProviderSpendSettings? Billing(string key) => _settings.Settings.ProviderSpending?.GetValueOrDefault(key);
    private void LoadBilling()
    {
        var b = SelectedBillingProvider is { } p ? Billing(p.Key) : null;
        BalanceInput = ""; BalanceError = "";
        PackagePriceInput = b?.MonthlyPackageUsd?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        PackageStartDate = b?.PackageStartDate ?? DateTime.Today;
        OnPropertyChanged(nameof(IsPackageBilling)); OnPropertyChanged(nameof(IsApiBilling));
        OnPropertyChanged(nameof(HasBilling));
        UpdateBalance();
    }
    private void UpdateBalance()
    {
        var b = SelectedBillingProvider is { } p ? Billing(p.Key) : null;
        HasBalance = b?.CreditBalanceUsd != null && b.CreditSetAt != null;
        if (HasBalance)
        {
            decimal spent = _cost.SpentSince(b!.CreditSetAt!.Value, b.CreditSpentOnAnchorDay, SelectedBillingProvider!.Key);
            BalanceText = LF("usage.balanceValue", Usd(b.CreditBalanceUsd!.Value - spent));
            BalanceDetail = LF("usage.balanceSince", Usd(b.CreditBalanceUsd.Value), b.CreditSetAt.Value.ToString("d MMM HH:mm", CultureInfo.CurrentUICulture), Usd(spent));
        }
        else { BalanceText = ""; BalanceDetail = L("usage.providerBalanceNone"); }
    }
    private void SavePackage()
    {
        if (!IsPackageBilling || !TryParseUsd(PackagePriceInput, out var fee) || PackageStartDate == null || PackageStartDate.Value.Date > DateTime.Today)
        { BalanceError = L("usage.packageBad"); return; }
        string key = SelectedBillingProvider!.Key;
        _settings.UpdateSettings(s =>
        {
            s.ProviderSpending ??= new();
            if (!s.ProviderSpending.TryGetValue(key, out var b)) s.ProviderSpending[key] = b = new();
            b.MonthlyPackageUsd = fee; b.PackageStartDate = PackageStartDate.Value.Date;
        });
        BalanceError = "";
        Refresh();
    }
    public ObservableCollection<ProviderLine> ProviderLines { get; } = new();
    public ObservableCollection<RangeChoice> Ranges { get; } = new();
    private RangeChoice? _selectedRange;
    public RangeChoice? SelectedRange
    {
        get => _selectedRange;
        set { if (SetProperty(ref _selectedRange, value) && value != null && value.Key != "custom") ApplyPreset(); }
    }
    private DateTime? _startDate = DateTime.Today.AddDays(-13), _endDate = DateTime.Today;
    private DateTime _from = DateTime.Today.AddDays(-13), _to = DateTime.Today;
    public DateTime? StartDate { get => _startDate; set { if (SetProperty(ref _startDate, value) && !_settingDates) SelectCustom(); } }
    public DateTime? EndDate { get => _endDate; set { if (SetProperty(ref _endDate, value) && !_settingDates) SelectCustom(); } }
    public DateTime Today => DateTime.Today;
    public DateTime FirstUsageDay => (_settings.Settings.ProviderSpending?.Values
        .Where(p => p.MonthlyPackageUsd != null && p.PackageStartDate != null).Select(p => p.PackageStartDate!.Value.Date)
        ?? Enumerable.Empty<DateTime>()).Append(_cost.FirstUsageDay).Min();
    private bool _settingDates, _selectingProviders;
    private bool _isCumulative = true, _showSavings = true;
    public bool IsCumulative { get => _isCumulative; set { if (SetProperty(ref _isCumulative, value)) Refresh(); } }
    public bool ShowSavings { get => _showSavings; set { if (SetProperty(ref _showSavings, value)) Refresh(); } }
    private string _rangeError = "", _rangeLabel = "", _tokensText = "", _selectionText = "", _bucketText = "";
    private bool _hasUsage;
    public string RangeError { get => _rangeError; private set => SetProperty(ref _rangeError, value); }
    public string RangeLabel { get => _rangeLabel; private set => SetProperty(ref _rangeLabel, value); }
    public string TokensText { get => _tokensText; private set => SetProperty(ref _tokensText, value); }
    public string SelectionText { get => _selectionText; private set => SetProperty(ref _selectionText, value); }
    public string BucketText { get => _bucketText; private set => SetProperty(ref _bucketText, value); }
    public bool HasUsage { get => _hasUsage; private set => SetProperty(ref _hasUsage, value); }
    public ICommand ApplyDatesCommand { get; private set; } = null!;
    public ICommand SelectAllCommand { get; private set; } = null!;
    public ICommand SelectNoneCommand { get; private set; } = null!;
    private double _plotWidth = LineWidth;
    private List<CostTrackingService.UsageDay> _chartDays = new();
    private List<(ProviderChoice Provider, List<CostTrackingService.UsageDay> Days)> _chartProviders = new();

    private void InitializeFilters()
    {
        ApplyDatesCommand = new RelayCommand(ApplyDates);
        SavePackageCommand = new RelayCommand(SavePackage);
        ClearPackageCommand = new RelayCommand(() =>
        {
            if (SelectedBillingProvider is { } p && Billing(p.Key) is { } b)
                _settings.UpdateSettings(_ => { b.MonthlyPackageUsd = null; b.PackageStartDate = null; });
            LoadBilling(); Refresh();
        });
        SelectAllCommand = new RelayCommand(() => SelectProviders(true));
        SelectNoneCommand = new RelayCommand(() => SelectProviders(false));
        foreach (var n in new[] { 1, 7, 14, 30, 90 })
            Ranges.Add(new RangeChoice(n.ToString(CultureInfo.InvariantCulture), n == 1 ? L("usage.today") : LF("usage.lastDays", n), n));
        Ranges.Add(new RangeChoice("month", L("usage.thisMonth"), null));
        Ranges.Add(new RangeChoice("previousMonth", L("usage.previousMonth"), null));
        Ranges.Add(new RangeChoice("all", L("usage.allHistory"), null));
        Ranges.Add(new RangeChoice("custom", L("usage.customRange"), null));
        _selectedRange = Ranges.First(r => r.Days == 14);
    }
    private void SelectCustom()
    {
        _selectedRange = Ranges.First(r => r.Key == "custom");
        OnPropertyChanged(nameof(SelectedRange));
    }
    private void ApplyPreset()
    {
        _settingDates = true;
        try
        {
            EndDate = DateTime.Today;
            StartDate = SelectedRange?.Days is { } n ? DateTime.Today.AddDays(1 - n) : FirstUsageDay;
            if (SelectedRange?.Key is "month" or "previousMonth")
            {
                var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                StartDate = SelectedRange.Key == "month" ? first : first.AddMonths(-1);
                if (SelectedRange.Key == "previousMonth") EndDate = first.AddDays(-1);
            }
        }
        finally { _settingDates = false; }
        ApplyDates();
    }
    private void ApplyDates()
    {
        if (StartDate == null || EndDate == null || StartDate.Value.Date > EndDate.Value.Date
            || EndDate.Value.Date > DateTime.Today || (EndDate.Value.Date - StartDate.Value.Date).Days >= 3660)
        { RangeError = L("usage.rangeBad"); return; }
        RangeError = "";
        _from = StartDate.Value.Date; _to = EndDate.Value.Date;
        Refresh();
    }
    private void SelectProviders(bool selected)
    {
        _selectingProviders = true;
        try { foreach (var p in Providers) p.IsSelected = selected; }
        finally { _selectingProviders = false; }
        Refresh();
    }
    private void EnsureProviders()
    {
        var definitions = new[]
        {
            ("Anthropic", "Claude API", "#D97706"), ("OpenAI", "OpenAI API", "#0284C7"), ("Google", "Gemini API", "#8B5CF6"),
            ("ClaudeDev", "Claude Dev", "#DB2777"), ("OpenAIDev", "OpenAI Dev", "#0D9488"),
            ("GeminiDev", "Gemini Dev", "#6366F1"), ("AntigravityDev", "Antigravity Dev", "#C026D3"),
            ("BrainX", "BrainX", "#A16207"), ("Local", "Local GGUF", "#65A30D"),
            ("LlamaServer", "Llama Server", "#EA580C"), ("Ollama", "Ollama", "#0891B2"),
        };
        void Add(string key, string label, string color)
        {
            if (Providers.Any(p => p.Key == key)) return;
            Providers.Add(new ProviderChoice(key, label, color, () => { if (!_selectingProviders) Refresh(); }));
            if (key != CostTrackingService.UnattributedProvider) BillingProviders.Add(Providers[^1]);
        }
        foreach (var (key, label, color) in definitions) Add(key, label, color);
        foreach (var key in _cost.RecordedProviders.Union(_settings.Settings.ProviderSpending?.Keys.AsEnumerable() ?? Enumerable.Empty<string>()).OrderBy(k => k, StringComparer.Ordinal))
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
            string color = $"#{100 + hash[0] % 120:X2}{100 + hash[1] % 120:X2}{100 + hash[2] % 120:X2}";
            Add(key, key == CostTrackingService.UnattributedProvider ? L("usage.unattributed") : key == "Other" ? L("usage.providerOther") : key, color);
        }
        SelectedBillingProvider ??= BillingProviders.First();
    }
    private static string Tok(long n) => n >= 1_000_000 ? (n / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + "M"
        : n >= 1_000 ? (n / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k" : n.ToString(CultureInfo.InvariantCulture);
    private static CostTrackingService.UsageDay SumDays(DateTime day, IEnumerable<CostTrackingService.UsageDay> values)
    {
        var a = values.ToList();
        return new(day, a.Sum(d => d.Usd), a.Sum(d => d.BaselineUsd), a.Sum(d => d.Requests), a.Sum(d => d.SavedCacheUsd),
            a.Sum(d => d.SavedToolSearchUsd), a.Sum(d => d.SavedCompactionUsd), a.Sum(d => d.SavedBatchUsd),
            a.Sum(d => d.InputTokens), a.Sum(d => d.CacheReadTokens), a.Sum(d => d.OutputTokens));
    }
    public void Refresh()
    {
        EnsureProviders();
        RangeLabel = _from.ToString("d MMM yyyy", CultureInfo.CurrentUICulture) + " – " + _to.ToString("d MMM yyyy", CultureInfo.CurrentUICulture);
        var data = Providers.Select(p => (Provider: p, Days: ProviderCostProjection.Project(p.Key, _cost.Between(_from, _to, p.Key), Billing(p.Key)).ToList())).ToList();
        foreach (var (p, days) in data) p.Update(days);
        var selected = data.Where(d => d.Provider.IsSelected).ToList();
        _chartProviders = selected.Where(d => d.Provider.HasData).ToList();
        var dates = _cost.Between(_from, _to);
        _chartDays = dates.Select((d, i) => SumDays(d.Day, selected.Select(p => p.Days[i]))).ToList();
        HasUsage = selected.Any(d => d.Provider.HasData);
        SelectionText = LF("usage.selectedCount", selected.Count, Providers.Count);
        TokensText = LF("usage.tokensRange", Tok(_chartDays.Sum(d => d.InputTokens)), Tok(_chartDays.Sum(d => d.CacheReadTokens)),
            Tok(_chartDays.Sum(d => d.OutputTokens)), _chartDays.Sum(d => d.Requests).ToString("N0", CultureInfo.InvariantCulture));
        decimal paid = _chartDays.Sum(d => d.Usd), baseline = _chartDays.Sum(d => d.BaselineUsd), saved = Math.Max(0, baseline - paid);
        Periods.Clear();
        bool estimated = selected.Any(p => ProviderCostProjection.IsPackage(p.Provider.Key) && p.Provider.HasData);
        Periods.Add(new Period(L(estimated ? "usage.costWithEstimate" : "usage.legendPaid"), (estimated ? "≈ " : "") + Usd(paid), LF("usage.requests", _chartDays.Sum(d => d.Requests)), "", true));
        Periods.Add(new Period(L("usage.lineWithout"), Usd(baseline), L("usage.estimate"), "", true));
        Periods.Add(new Period(L("usage.savedShort"), Usd(saved), (baseline > 0 ? saved / baseline * 100m : 0m).ToString("0.#", CultureInfo.InvariantCulture) + "%", "", true));
        BuildCharts();
        var parts = new[] { ("usage.byCache", _chartDays.Sum(d => d.SavedCacheUsd)), ("usage.byToolSearch", _chartDays.Sum(d => d.SavedToolSearchUsd)),
            ("usage.byCompaction", _chartDays.Sum(d => d.SavedCompactionUsd)), ("usage.byBatch", _chartDays.Sum(d => d.SavedBatchUsd)) };
        decimal top = parts.Max(p => Math.Max(0, p.Item2));
        Savings.Clear();
        foreach (var (key, usd) in parts) Savings.Add(new SavingRow(L(key), Usd(usd), top > 0 ? (double)(Math.Max(0, usd) / top) : 0));
        Strategies.Clear();
        var rows = _cost.StrategyBreakdown(_from, _to);
        decimal topPerRun = rows.Count == 0 ? 0 : rows.Max(r => r.UsdPerRun);
        foreach (var r in rows)
        {
            var t = TimeSpan.FromSeconds(r.SecondsPerRun);
            string time = t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.Seconds}s";
            Strategies.Add(new StrategyRow(L("usage.strategy." + r.Strategy), r.Runs.ToString("N0", CultureInfo.InvariantCulture),
                (r.SuccessShare * 100).ToString("0", CultureInfo.InvariantCulture) + "%", Usd(r.UsdPerRun), time, Usd(r.Usd),
                topPerRun > 0 ? (double)(r.UsdPerRun / topPerRun) : 0));
        }
        OnPropertyChanged(nameof(HasStrategies));
        BuildMonthlyTotals(selected.Select(p => p.Provider).ToList());
        UpdateBalance();
        OnPropertyChanged(nameof(HasLegacyCredit));
        OnPropertyChanged(nameof(LegacyCreditText));
    }

    private void BuildMonthlyTotals(List<ProviderChoice> selected)
    {
        MonthlyTotals.Clear();
        for (var month = new DateTime(_from.Year, _from.Month, 1); month <= _to; month = month.AddMonths(1))
        {
            var end = month.AddMonths(1).AddDays(-1);
            if (end > DateTime.Today) end = DateTime.Today;
            decimal api = 0, packages = 0, baseline = 0;
            bool missing = false, proxy = false;
            foreach (var provider in selected)
            {
                var days = _cost.Between(month, end, provider.Key);
                baseline += days.Sum(d => d.BaselineUsd);
                if (!ProviderCostProjection.IsPackage(provider.Key)) { api += days.Sum(d => d.Usd); continue; }
                if (ProviderCostProjection.MonthlyFee(Billing(provider.Key), month, end) is { } fee)
                { packages += fee; missing |= fee > 0 && !days.Any(d => d.Requests > 0); }
                else { decimal equivalent = days.Sum(d => d.BaselineUsd); packages += equivalent; proxy |= equivalent > 0; }
            }
            MonthlyTotals.Add(new MonthlyTotal(month, api, packages, baseline, missing, proxy));
        }
    }
    public void UpdateChartWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0 || Math.Abs(width - _plotWidth) < 1) return;
        _plotWidth = width;
        BuildCharts();
    }
    private void BuildCharts()
    {
        int bucketSize = Math.Max(1, (int)Math.Ceiling(_chartDays.Count / 90d));
        List<CostTrackingService.UsageDay> Bucket(List<CostTrackingService.UsageDay> days) => days.Chunk(bucketSize).Select(a => SumDays(a[0].Day, a)).ToList();
        var days = Bucket(_chartDays);
        var providers = _chartProviders.Select(p => (p.Provider, Days: Bucket(p.Days))).ToList();
        BucketText = bucketSize > 1 ? LF("usage.bucketDays", bucketSize) : L("usage.dailyBuckets");
        Bars.Clear(); PaidDots.Clear(); BaseDots.Clear(); ProviderLines.Clear();
        if (days.Count == 0) { PaidLine = BaseLine = GapArea = PaidArea = new(); return; }
        decimal max = days.Max(d => ShowSavings ? Math.Max(d.BaselineUsd, d.Usd) : d.Usd);
        int labelEvery = Math.Max(1, (int)Math.Ceiling(days.Count / 12d));
        for (int i = 0; i < days.Count; i++)
        {
            var d = days[i];
            decimal saved = Math.Max(0, d.BaselineUsd - d.Usd);
            double H(decimal v) => max > 0 ? (double)(Math.Max(0, v) / max) * ChartHeight : 0;
            string tip = LF("usage.barTip", d.Day.ToString("d MMM yyyy", CultureInfo.CurrentUICulture), Usd(d.Usd), d.Requests, Usd(d.BaselineUsd), Usd(saved));
            tip += "\n" + string.Join("\n", providers.Select(p => p.Provider.Label + ": " + Usd(p.Days[i].Usd)));
            Bars.Add(new Bar(i % labelEvery == 0 || i == days.Count - 1 ? d.Day.ToString("d/M", CultureInfo.InvariantCulture) : "", H(d.Usd),
                ShowSavings ? H(saved) : 0, tip, providers.Select(p => new BarSegment(H(p.Days[i].Usd), p.Provider.Color)).Reverse().ToList()));
        }
        decimal[] Values(IEnumerable<CostTrackingService.UsageDay> source, bool baseline = false)
        {
            decimal running = 0;
            return source.Select(d => { decimal v = baseline ? Math.Max(d.BaselineUsd, d.Usd) : d.Usd; running = IsCumulative ? running + v : v; return running; }).ToArray();
        }
        var paid = Values(days); var without = Values(days, true);
        var values = providers.Select(p => (p.Provider, Values: Values(p.Days))).ToList();
        decimal scale = Math.Max(0.01m, ShowSavings ? without.Max() : values.SelectMany(p => p.Values).DefaultIfEmpty(0).Max()) * 1.08m;
        double step = days.Count > 1 ? _plotWidth / (days.Count - 1) : 0;
        double X(int i) => days.Count == 1 ? _plotWidth / 2 : step * i;
        double Y(decimal v) => LineHeight - (double)(v / scale) * LineHeight;
        var pl = new System.Windows.Media.PointCollection(); var bl = new System.Windows.Media.PointCollection();
        for (int i = 0; i < days.Count; i++)
        {
            pl.Add(new System.Windows.Point(X(i), Y(paid[i]))); bl.Add(new System.Windows.Point(X(i), Y(without[i])));
            string tip = LF("usage.lineTip", days[i].Day.ToString("d MMM yyyy", CultureInfo.CurrentUICulture), Usd(paid[i]), Usd(without[i]), Usd(without[i] - paid[i]));
            PaidDots.Add(new LinePoint(X(i), Y(paid[i]), tip)); BaseDots.Add(new LinePoint(X(i), Y(without[i]), tip));
        }
        foreach (var (provider, amount) in values)
        {
            var points = new System.Windows.Media.PointCollection(); var dots = new List<LinePoint>();
            for (int i = 0; i < amount.Length; i++)
            {
                points.Add(new System.Windows.Point(X(i), Y(amount[i])));
                dots.Add(new LinePoint(X(i), Y(amount[i]), provider.Label + " · " + days[i].Day.ToString("d MMM yyyy", CultureInfo.CurrentUICulture) + "\n" + Usd(amount[i]), provider.Color));
            }
            ProviderLines.Add(new ProviderLine(provider.Label, provider.Color, points, dots));
        }
        var gap = new System.Windows.Media.PointCollection(bl);
        for (int i = pl.Count - 1; i >= 0; i--) gap.Add(pl[i]);
        PaidLine = pl; BaseLine = bl; GapArea = gap;
        PaidArea = new System.Windows.Media.PointCollection(pl) { new(pl[^1].X, LineHeight), new(pl[0].X, LineHeight) };
        decimal totalPaid = _chartDays.Sum(d => d.Usd), totalBase = _chartDays.Sum(d => d.BaselineUsd);
        LineSaved = LF("usage.lineSaved", Usd(Math.Max(0, totalBase - totalPaid)), (totalBase > 0 ? Math.Max(0, totalBase - totalPaid) / totalBase * 100m : 0).ToString("0", CultureInfo.InvariantCulture));
        LineScaleTop = Usd(scale);
        LineFirstDay = _from.ToString("d MMM", CultureInfo.CurrentUICulture); LineLastDay = _to.ToString("d MMM", CultureInfo.CurrentUICulture);
    }
    /// <summary>"25", "$12.50", "12,50": the balance read off the billing page.</summary>
    public static bool TryParseUsd(string? text, out decimal usd)
    {
        string t = (text ?? "").Trim().TrimStart('$').Replace(" ", "");
        if (t.Contains(',') && !t.Contains('.'))
        {
            var parts = t.Split(',');
            bool grouped = parts[0].Length is >= 1 and <= 3 && parts[0] != "0" && parts.Skip(1).All(p => p.Length == 3 && p.All(char.IsDigit));
            if (grouped) t = t.Replace(",", "");
            else if (parts.Length == 2) t = t.Replace(',', '.');
            else { usd = 0; return false; }
        }
        else t = t.Replace(",", "");
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out usd) && usd >= 0;
    }

    private void SetBalance()
    {
        if (!IsApiBilling || !TryParseUsd(BalanceInput, out decimal usd))
        {
            BalanceError = L("usage.balanceBad");
            return;
        }
        BalanceError = "";
        string key = SelectedBillingProvider!.Key;
        decimal today = _cost.Between(DateTime.Today, DateTime.Today, key).Sum(d => d.Usd);
        _settings.UpdateSettings(s =>
        {
            s.ProviderSpending ??= new();
            if (!s.ProviderSpending.TryGetValue(key, out var b)) s.ProviderSpending[key] = b = new();
            b.CreditBalanceUsd = usd; b.CreditSetAt = DateTime.Now; b.CreditSpentOnAnchorDay = today;
        });
        BalanceInput = "";
        Refresh();
    }
}
