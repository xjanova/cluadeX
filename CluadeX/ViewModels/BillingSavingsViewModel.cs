using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services;
using CluadeX.Services.Billing;

namespace CluadeX.ViewModels;

/// <summary>
/// The Billing page's "Savings &amp; comparison" section (it replaced the old spend window): what was paid against what
/// the same work would have cost without CluadeX's savings, over chosen dates and providers — summary cards, the
/// comparison lines (per provider, total, without savings), daily bars, where the savings came from (cache, tool
/// search, compaction, batch), monthly totals of API spend vs package fees vs the full-price baseline, and the dispatch
/// strategies. Everything is read from the day ledger (<see cref="CostTrackingService.Between"/>), never invented.
/// On a provider tab only that provider is compared; on All the provider chips choose. Nothing runs while the page is
/// hidden — new spend only marks it stale.
/// </summary>
public sealed class BillingSavingsViewModel : ViewModelBase
{
    // Comparison lines share one scale; the optional total/baseline band keeps the savings comparison.
    public const double LineWidth = 620, LineHeight = 200;

    private readonly CostTrackingService _cost;
    private readonly SettingsService _settings;
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;
    private System.Windows.Threading.DispatcherTimer? _debounce;
    private bool _visible, _stale = true;
    private string _tabKey = ProviderBrand.AllKey;

    public BillingSavingsViewModel(CostTrackingService cost, SettingsService settings)
    {
        _cost = cost;
        _settings = settings;
        _dispatcher = System.Windows.Application.Current?.Dispatcher;
        ApplyDatesCommand = new RelayCommand(ApplyDates);
        SelectAllCommand = new RelayCommand(() => SelectProviders(true));
        SelectNoneCommand = new RelayCommand(() => SelectProviders(false));
        foreach (var n in new[] { 1, 7, 14, 30, 90 })
            Ranges.Add(new RangeChoice(n.ToString(CultureInfo.InvariantCulture), n == 1 ? L("usage.today") : LF("usage.lastDays", n), n));
        Ranges.Add(new RangeChoice("month", L("usage.thisMonth"), null));
        Ranges.Add(new RangeChoice("previousMonth", L("usage.previousMonth"), null));
        Ranges.Add(new RangeChoice("all", L("usage.allHistory"), null));
        Ranges.Add(new RangeChoice("custom", L("usage.customRange"), null));
        // The page opens on 30 days; the header's 7 / 30 / 90 carry over (FollowRange).
        _selectedRange = Ranges.First(r => r.Days == 30);
        _startDate = _from = DateTime.Today.AddDays(-29);
        _endDate = _to = DateTime.Today;
        _cost.OnCostUpdated += OnCostUpdated;
    }

    private static string L(string key) => LocalizedResources.Instance[key];
    private static string LF(string key, params object[] args) => string.Format(L(key), args);
    private static string Usd(decimal v) => UsdText.Format(v);

    // ── Rows ──

    public sealed record SummaryCard(string Title, string Amount, string Detail, bool IsSaving);
    public sealed record SavingRow(string Name, string Amount, double Fraction);
    /// <summary>One dispatch strategy over the selected dates (CostTrackingService.StrategyBreakdown): runs, success share,
    /// cost per run, time per run — the numbers DispatchPolicy's thresholds are tuned from.</summary>
    public sealed record StrategyRow(string Name, string Runs, string Success, string CostPerRun, string TimePerRun, string Total, double Fraction);
    public sealed record LinePoint(double X, double Y, string Tip, Brush? Color = null);
    public sealed record ProviderLine(string Label, Brush Color, PointCollection Points, IReadOnlyList<LinePoint> Dots);
    public sealed record RangeChoice(string Key, string Label, int? Days)
    {
        public override string ToString() => Label;
    }
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

    /// <summary>One provider of the day ledger as a filter chip: its logo or colour, and its total over the dates.</summary>
    public sealed class ProviderChoice : ViewModelBase
    {
        private readonly Action _changed;
        private bool _selected = true;
        private string _totalText = "";
        public string Key { get; }
        public string Label { get; }
        public Brush Color { get; }
        public ImageSource? Logo { get; }
        public bool HasLogo => Logo != null;
        public bool IsSelected { get => _selected; set { if (SetProperty(ref _selected, value)) _changed(); } }
        public bool HasData { get; private set; }
        public decimal Total { get; private set; }
        public string TotalText { get => _totalText; private set => SetProperty(ref _totalText, value); }
        public override string ToString() => Label;
        public ProviderChoice(string key, string label, Brush color, ImageSource? logo, Action changed)
        {
            Key = key; Label = label; Color = color; Logo = logo; _changed = changed;
        }
        public void Update(IReadOnlyList<CostTrackingService.UsageDay> days)
        {
            HasData = days.Any(d => d.Requests > 0 || d.Usd != 0 || d.InputTokens != 0 || d.OutputTokens != 0);
            Total = days.Sum(d => d.Usd);
            TotalText = HasData ? (ProviderCostProjection.IsPackage(Key) ? "≈ " : "") + Usd(Total) : L("usage.noRecordedUse");
            OnPropertyChanged(nameof(HasData));
        }
    }

    public ObservableCollection<SummaryCard> Summary { get; } = new();
    public ObservableCollection<SavingRow> Savings { get; } = new();
    public ObservableCollection<StrategyRow> Strategies { get; } = new();
    public bool HasStrategies => Strategies.Count > 0;
    public ObservableCollection<ProviderChoice> Providers { get; } = new();
    public ObservableCollection<ProviderLine> ProviderLines { get; } = new();
    public ObservableCollection<MonthlyTotal> MonthlyTotals { get; } = new();
    public ObservableCollection<RangeChoice> Ranges { get; } = new();

    private IReadOnlyList<ChartBar> _dailyBars = Array.Empty<ChartBar>();
    /// <summary>Daily bars for the page's <c>SpendChart</c>: each selected provider's paid part, then the saved part on top.</summary>
    public IReadOnlyList<ChartBar> DailyBars { get => _dailyBars; private set => SetProperty(ref _dailyBars, value); }

    private PointCollection _paidLine = new(), _baseLine = new(), _gapArea = new();
    public PointCollection PaidLine { get => _paidLine; private set => SetProperty(ref _paidLine, value); }
    public PointCollection BaseLine { get => _baseLine; private set => SetProperty(ref _baseLine, value); }
    /// <summary>The band between the two lines (the savings).</summary>
    public PointCollection GapArea { get => _gapArea; private set => SetProperty(ref _gapArea, value); }

    private string _lineSaved = "", _lineScaleTop = "", _lineFirst = "", _lineLast = "", _rangeError = "", _rangeLabel = "",
        _tokensText = "", _selectionText = "", _bucketText = "", _filterNote = "";
    private bool _hasUsage, _showProviderFilter = true;
    public string LineSaved { get => _lineSaved; private set => SetProperty(ref _lineSaved, value); }
    public string LineScaleTop { get => _lineScaleTop; private set => SetProperty(ref _lineScaleTop, value); }
    public string LineFirstDay { get => _lineFirst; private set => SetProperty(ref _lineFirst, value); }
    public string LineLastDay { get => _lineLast; private set => SetProperty(ref _lineLast, value); }
    public string RangeError { get => _rangeError; private set => SetProperty(ref _rangeError, value); }
    public string RangeLabel { get => _rangeLabel; private set => SetProperty(ref _rangeLabel, value); }
    public string TokensText { get => _tokensText; private set => SetProperty(ref _tokensText, value); }
    public string SelectionText { get => _selectionText; private set => SetProperty(ref _selectionText, value); }
    public string BucketText { get => _bucketText; private set => SetProperty(ref _bucketText, value); }
    /// <summary>The selected providers have use in the dates.</summary>
    public bool HasUsage { get => _hasUsage; private set => SetProperty(ref _hasUsage, value); }
    /// <summary>On All the chips choose the providers; on a provider tab only that provider is compared.</summary>
    public bool ShowProviderFilter { get => _showProviderFilter; private set => SetProperty(ref _showProviderFilter, value); }
    public string FilterNote { get => _filterNote; private set => SetProperty(ref _filterNote, value); }

    public ICommand ApplyDatesCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }

    // ── Dates ──

    private RangeChoice? _selectedRange;
    public RangeChoice? SelectedRange
    {
        get => _selectedRange;
        set { if (SetProperty(ref _selectedRange, value) && value != null && value.Key != "custom") ApplyPreset(); }
    }
    private DateTime? _startDate, _endDate;
    private DateTime _from, _to;
    // The day the dates were worked out: the page lives as long as the app, so "last 30 days" must move on at midnight.
    private DateTime _appliedOn = DateTime.Today;
    private bool _settingDates, _selectingProviders;
    public DateTime? StartDate { get => _startDate; set { if (SetProperty(ref _startDate, value) && !_settingDates) SelectCustom(); } }
    public DateTime? EndDate { get => _endDate; set { if (SetProperty(ref _endDate, value) && !_settingDates) SelectCustom(); } }
    public DateTime Today => DateTime.Today;
    /// <summary>The applied dates (inclusive, local).</summary>
    public DateTime From => _from;
    public DateTime To => _to;
    public DateTime FirstUsageDay => (_settings.Settings.ProviderSpending?.Values
        .Where(p => p.MonthlyPackageUsd != null && p.PackageStartDate != null).Select(p => p.PackageStartDate!.Value.Date)
        ?? Enumerable.Empty<DateTime>()).Append(_cost.FirstUsageDay).Min();

    private bool _isCumulative = true, _showSavings = true;
    public bool IsCumulative { get => _isCumulative; set { if (SetProperty(ref _isCumulative, value)) RefreshIfShown(); } }
    public bool ShowSavings { get => _showSavings; set { if (SetProperty(ref _showSavings, value)) RefreshIfShown(); } }

    private double _plotWidth = LineWidth;
    private List<CostTrackingService.UsageDay> _chartDays = new();
    private List<(ProviderChoice Provider, List<CostTrackingService.UsageDay> Days)> _chartProviders = new();

    private void SelectCustom()
    {
        _selectedRange = Ranges.First(r => r.Key == "custom");
        OnPropertyChanged(nameof(SelectedRange));
    }

    /// <summary>The page header's 7 / 30 / 90 days: the comparison follows to the same preset.</summary>
    public void FollowRange(int days)
    {
        if (Ranges.FirstOrDefault(r => r.Days == days) is { } preset && !ReferenceEquals(preset, _selectedRange)) SelectedRange = preset;
    }

    private void ApplyPreset()
    {
        SetPresetDates();
        ApplyDates();
    }

    private void SetPresetDates()
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
    }

    private void ApplyDates()
    {
        if (StartDate == null || EndDate == null || StartDate.Value.Date > EndDate.Value.Date
            || EndDate.Value.Date > DateTime.Today || (EndDate.Value.Date - StartDate.Value.Date).Days >= 3660)
        { RangeError = L("usage.rangeBad"); return; }
        RangeError = "";
        _from = StartDate.Value.Date; _to = EndDate.Value.Date;
        _appliedOn = DateTime.Today;
        RefreshIfShown();
    }

    private void SelectProviders(bool selected)
    {
        _selectingProviders = true;
        try { foreach (var p in Providers) p.IsSelected = selected; }
        finally { _selectingProviders = false; }
        RefreshIfShown();
    }

    // ── Which providers: the tab, or the chips on All ──

    /// <summary>The Billing tab on screen: All lets the chips choose; a provider tab compares only that provider.</summary>
    public void FollowTab(string tabKey)
    {
        if (_tabKey == tabKey && !_stale) return;
        _tabKey = tabKey;
        bool all = tabKey == ProviderBrand.AllKey;
        ShowProviderFilter = all;
        FilterNote = all ? "" : LF("billing.savings.onlyTab", ProviderBrand.For(tabKey).Key == tabKey ? ProviderBrand.For(tabKey).Name : tabKey);
        RefreshIfShown();
    }

    /// <summary>The Billing tab a ledger key belongs to ("Google" is the Gemini API's, an unknown key is Other's).</summary>
    public static string TabOf(string ledgerKey) => ProviderBrand.FromLedgerKey(ledgerKey);

    private bool InScope(ProviderChoice p) => _tabKey == ProviderBrand.AllKey ? p.IsSelected : TabOf(p.Key) == _tabKey;

    // ── Visibility: no work while hidden ──

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (!visible) { _debounce?.Stop(); return; }
        if (_stale || _appliedOn != DateTime.Today) Refresh();
    }

    /// <summary>Something it reads changed (a plan fee, Refresh): redraw now if on screen, else when shown.</summary>
    public void Invalidate() => RefreshIfShown();

    private void RefreshIfShown()
    {
        if (_visible) Refresh(); else _stale = true;
    }

    private void OnCostUpdated(CostSummary _)
    {
        _stale = true;
        if (!_visible || _dispatcher == null) return;
        // Requests report usage several times each (start, deltas, end): one refresh per burst, a moment later.
        _dispatcher.BeginInvoke(() =>
        {
            if (!_visible) return;
            _debounce ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(2), System.Windows.Threading.DispatcherPriority.Background,
                (_, _) => { _debounce!.Stop(); if (_visible && _stale) Refresh(); }, _dispatcher);
            if (!_debounce.IsEnabled) _debounce.Start();
        });
    }

    // ── Refresh ──

    private static readonly (string Key, string Fallback)[] Known =
    {
        ("Anthropic", "#D97706"), ("OpenAI", "#0284C7"), ("Google", "#8B5CF6"), ("ClaudeDev", "#DB2777"), ("OpenAIDev", "#0D9488"),
        ("GeminiDev", "#6366F1"), ("AntigravityDev", "#C026D3"), ("BrainX", "#A16207"), ("Local", "#65A30D"),
        ("LlamaServer", "#EA580C"), ("Ollama", "#0891B2"),
    };

    private void EnsureProviders()
    {
        void Add(string key, string fallback)
        {
            if (Providers.Any(p => p.Key == key)) return;
            string tab = TabOf(key);
            var brand = ProviderBrand.For(tab);
            bool own = tab != "Other" || key == "Other";
            string label = key == CostTrackingService.UnattributedProvider ? L("usage.unattributed")
                : key == "Other" ? L("usage.providerOther") : own ? brand.Name : key;
            // The brand colour, as on the tabs; a second line in a colour already taken (Claude Dev beside the
            // Anthropic API, the Gemini CLIs) gets its own so no two lines look alike.
            string hex = own && !Providers.Any(p => SameColour(p.Color, brand.Color)) ? brand.Color : fallback;
            if (Providers.Any(p => SameColour(p.Color, hex))) hex = HashColour(key + "#");
            Providers.Add(new ProviderChoice(key, label, BillingViewModel.Brush(hex), own ? BillingViewModel.BillingTab.LoadLogo(brand.LogoUri) : null,
                () => { if (!_selectingProviders) RefreshIfShown(); }));
        }
        foreach (var (key, fallback) in Known) Add(key, fallback);
        foreach (var key in _cost.RecordedProviders.Union(_settings.Settings.ProviderSpending?.Keys.AsEnumerable() ?? Enumerable.Empty<string>())
                     .OrderBy(k => k, StringComparer.Ordinal))
            Add(key, HashColour(key));
    }

    private static bool SameColour(Brush b, string hex) => b is SolidColorBrush s && s.Color == (Color)ColorConverter.ConvertFromString(hex);

    private static string HashColour(string key)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return $"#{100 + hash[0] % 120:X2}{100 + hash[1] % 120:X2}{100 + hash[2] % 120:X2}";
    }

    private static string Tok(long n) => BillingViewModel.Tok(n);

    private static CostTrackingService.UsageDay SumDays(DateTime day, IEnumerable<CostTrackingService.UsageDay> values)
    {
        var a = values.ToList();
        return new(day, a.Sum(d => d.Usd), a.Sum(d => d.BaselineUsd), a.Sum(d => d.Requests), a.Sum(d => d.SavedCacheUsd),
            a.Sum(d => d.SavedToolSearchUsd), a.Sum(d => d.SavedCompactionUsd), a.Sum(d => d.SavedBatchUsd),
            a.Sum(d => d.InputTokens), a.Sum(d => d.CacheReadTokens), a.Sum(d => d.OutputTokens));
    }

    private Models.ProviderSpendSettings? Billing(string key) => _settings.Settings.ProviderSpending?.GetValueOrDefault(key);

    /// <summary>Re-read the ledger for the applied dates and providers (in memory, quick: no worker thread needed).</summary>
    public void Refresh()
    {
        _stale = false;
        if (_appliedOn != DateTime.Today)
        {
            // A new day since the dates were set: a preset ("last 30 days", "this month") follows the calendar, so today's
            // spend is never left out; dates the user typed stay as they are.
            if (_selectedRange is { Key: not "custom" })
            {
                SetPresetDates();
                if (StartDate is { } start && EndDate is { } end && start <= end) { _from = start.Date; _to = end.Date; }
            }
            _appliedOn = DateTime.Today;
            OnPropertyChanged(nameof(Today));
        }
        EnsureProviders();
        RangeLabel = _from.ToString("d MMM yyyy", CultureInfo.CurrentUICulture) + " – " + _to.ToString("d MMM yyyy", CultureInfo.CurrentUICulture);
        var data = Providers.Select(p => (Provider: p, Days: ProviderCostProjection.Project(p.Key, _cost.Between(_from, _to, p.Key), Billing(p.Key)).ToList())).ToList();
        foreach (var (p, days) in data) p.Update(days);
        var selected = data.Where(d => InScope(d.Provider)).ToList();
        _chartProviders = selected.Where(d => d.Provider.HasData).ToList();
        var dates = _cost.Between(_from, _to);
        _chartDays = dates.Select((d, i) => SumDays(d.Day, selected.Select(p => p.Days[i]))).ToList();
        HasUsage = selected.Any(d => d.Provider.HasData);
        SelectionText = LF("usage.selectedCount", data.Count(d => d.Provider.IsSelected), Providers.Count);
        TokensText = LF("usage.tokensRange", Tok(_chartDays.Sum(d => d.InputTokens)), Tok(_chartDays.Sum(d => d.CacheReadTokens)),
            Tok(_chartDays.Sum(d => d.OutputTokens)), _chartDays.Sum(d => d.Requests).ToString("N0", CultureInfo.InvariantCulture));

        decimal paid = _chartDays.Sum(d => d.Usd), baseline = _chartDays.Sum(d => d.BaselineUsd), saved = Math.Max(0, baseline - paid);
        bool estimated = selected.Any(p => ProviderCostProjection.IsPackage(p.Provider.Key) && p.Provider.HasData);
        Summary.Clear();
        Summary.Add(new SummaryCard(L(estimated ? "usage.costWithEstimate" : "usage.legendPaid"), (estimated ? "≈ " : "") + Usd(paid),
            LF("usage.requests", _chartDays.Sum(d => d.Requests)), false));
        Summary.Add(new SummaryCard(L("usage.lineWithout"), Usd(baseline), L("usage.estimate"), false));
        Summary.Add(new SummaryCard(L("usage.savedShort"), Usd(saved),
            (baseline > 0 ? saved / baseline * 100m : 0m).ToString("0.#", CultureInfo.InvariantCulture) + "%", saved > 0));
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

    /// <summary>The plot's width on screen (the view reports it); the lines are laid out to it.</summary>
    public void UpdateChartWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0 || Math.Abs(width - _plotWidth) < 1) return;
        _plotWidth = width;
        BuildCharts();
    }

    private static readonly Brush SavedBrush = BillingViewModel.Brush("#22C55E", 0.45);

    private void BuildCharts()
    {
        // At most 90 points: a long range is grouped into buckets of whole days, every dollar still counted.
        int bucketSize = Math.Max(1, (int)Math.Ceiling(_chartDays.Count / 90d));
        List<CostTrackingService.UsageDay> Bucket(List<CostTrackingService.UsageDay> days) => days.Chunk(bucketSize).Select(a => SumDays(a[0].Day, a)).ToList();
        var days = Bucket(_chartDays);
        var providers = _chartProviders.Select(p => (p.Provider, Days: Bucket(p.Days))).ToList();
        BucketText = bucketSize > 1 ? LF("usage.bucketDays", bucketSize) : L("usage.dailyBuckets");
        ProviderLines.Clear();
        if (days.Count == 0) { PaidLine = BaseLine = GapArea = new(); DailyBars = Array.Empty<ChartBar>(); return; }

        // Daily bars: each provider's paid part in its colour, the saved part (without savings − paid) on top.
        var bars = new List<ChartBar>(days.Count);
        decimal running = 0;
        for (int i = 0; i < days.Count; i++)
        {
            var d = days[i];
            decimal saved = Math.Max(0, d.BaselineUsd - d.Usd);
            running += d.Usd;
            var segs = providers.Where(p => p.Days[i].Usd > 0).Select(p => new ChartPart((double)p.Days[i].Usd, p.Provider.Color)).ToList();
            if (ShowSavings && saved > 0) segs.Add(new ChartPart((double)saved, SavedBrush));
            string tip = LF("usage.barTip", d.Day.ToString("d MMM yyyy", CultureInfo.CurrentUICulture), Usd(d.Usd), d.Requests, Usd(d.BaselineUsd), Usd(saved));
            if (providers.Count > 0) tip += "\n" + string.Join("\n", providers.Select(p => p.Provider.Label + ": " + Usd(p.Days[i].Usd)));
            bars.Add(new ChartBar(d.Day, segs, (double)running, tip));
        }
        DailyBars = bars;

        decimal[] Values(IEnumerable<CostTrackingService.UsageDay> source, bool baseline = false)
        {
            decimal run = 0;
            return source.Select(d => { decimal v = baseline ? Math.Max(d.BaselineUsd, d.Usd) : d.Usd; run = IsCumulative ? run + v : v; return run; }).ToArray();
        }
        var paid = Values(days); var without = Values(days, true);
        var values = providers.Select(p => (p.Provider, Values: Values(p.Days))).ToList();
        decimal scale = Math.Max(0.01m, ShowSavings ? without.Max() : values.SelectMany(p => p.Values).DefaultIfEmpty(0).Max()) * 1.08m;
        double step = days.Count > 1 ? _plotWidth / (days.Count - 1) : 0;
        double X(int i) => days.Count == 1 ? _plotWidth / 2 : step * i;
        double Y(decimal v) => LineHeight - (double)(Math.Max(0, v) / scale) * LineHeight;
        var pl = new PointCollection(); var bl = new PointCollection();
        for (int i = 0; i < days.Count; i++) { pl.Add(new System.Windows.Point(X(i), Y(paid[i]))); bl.Add(new System.Windows.Point(X(i), Y(without[i]))); }
        foreach (var (provider, amount) in values)
        {
            var points = new PointCollection(); var dots = new List<LinePoint>();
            for (int i = 0; i < amount.Length; i++)
            {
                points.Add(new System.Windows.Point(X(i), Y(amount[i])));
                dots.Add(new LinePoint(X(i), Y(amount[i]), provider.Label + " · " + days[i].Day.ToString("d MMM yyyy", CultureInfo.CurrentUICulture) + "\n" + Usd(amount[i]), provider.Color));
            }
            points.Freeze();
            ProviderLines.Add(new ProviderLine(provider.Label, provider.Color, points, dots));
        }
        var gap = new PointCollection(bl);
        for (int i = pl.Count - 1; i >= 0; i--) gap.Add(pl[i]);
        pl.Freeze(); bl.Freeze(); gap.Freeze();
        PaidLine = pl; BaseLine = bl; GapArea = gap;
        decimal totalPaid = _chartDays.Sum(d => d.Usd), totalBase = _chartDays.Sum(d => d.BaselineUsd);
        LineSaved = LF("usage.lineSaved", Usd(Math.Max(0, totalBase - totalPaid)),
            (totalBase > 0 ? Math.Max(0, totalBase - totalPaid) / totalBase * 100m : 0).ToString("0", CultureInfo.InvariantCulture));
        LineScaleTop = Usd(scale);
        LineFirstDay = _from.ToString("d MMM", CultureInfo.CurrentUICulture); LineLastDay = _to.ToString("d MMM", CultureInfo.CurrentUICulture);
    }
}
