using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Services;
using CluadeX.Services.Billing;

namespace CluadeX.ViewModels;

/// <summary>
/// The Billing &amp; cost page: one tab per provider (All first), spend for today / this week / this month / all time,
/// tokens by kind, a daily chart with a running total, the models, the costliest chats and the budgets. Aggregation
/// (<see cref="UsageAggregator"/>) runs on a worker thread and is cached per log version, range and day; switching tabs
/// only re-reads the cached snapshot. Nothing runs while the page is hidden — new usage just marks it stale.
/// </summary>
public sealed class BillingViewModel : ViewModelBase
{
    private readonly CostTrackingService _cost;
    private readonly SettingsService _settings;
    private readonly DatabaseService? _db;
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;
    private System.Windows.Threading.DispatcherTimer? _debounce;

    /// <summary>"Now" and the time zone whose calendar days count — the harness pins both.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    private BillingSnapshot? _snapshot;
    private (int Version, int Days, DateTime Today, string Zone)? _snapshotKey;
    // Chat titles from the chat list; null until it has been read, so an unknown chat is never called deleted.
    private Dictionary<string, string>? _titles;
    private DateTime _titlesAt = DateTime.MinValue;
    private bool _visible, _stale = true;
    private Task? _running;
    private int _refreshAgain;

    public BillingViewModel(CostTrackingService cost, SettingsService settings, DatabaseService? db = null)
    {
        _cost = cost;
        _settings = settings;
        _db = db;
        _dispatcher = System.Windows.Application.Current?.Dispatcher;
        foreach (int n in new[] { 7, 30, 90 }) Ranges.Add(new RangeOption(n, LF("billing.range.days", n), this));
        _rangeDays = 30;
        Savings = new BillingSavingsViewModel(cost, settings);
        Account = new BillingAccountViewModel(cost, settings);
        // A credit or plan saved on the page: the budgets read it, the comparison projects plan fees with it.
        Account.Changed += () =>
        {
            if (_snapshot != null && SelectedTab != null) ShowBudgets();
            Savings.Invalidate();
        };
        RefreshCommand = new RelayCommand(() => { _ = RefreshAsync(force: true); Savings.Invalidate(); Account.Refresh(); });
        SelectTabCommand = new RelayCommand<BillingTab>(t => { if (t != null) SelectedTab = t; });
        _cost.Log.Added += OnUsageAdded;
    }

    /// <summary>Savings &amp; comparison: paid vs without savings, by provider and dates, from the day ledger.</summary>
    public BillingSavingsViewModel Savings { get; }
    /// <summary>The credit / plan-fee editor for the account on screen.</summary>
    public BillingAccountViewModel Account { get; }

    /// <summary>The title bar's "Spend &amp; savings" asked for the page: the view brings the savings section into view.</summary>
    public event Action? SavingsRequested;
    private bool _savingsWanted;
    public void ShowSavings() { _savingsWanted = true; SavingsRequested?.Invoke(); }
    /// <summary>True once per <see cref="ShowSavings"/> (the view scrolls when it is on screen) — but not while the page's
    /// numbers are still on their way: the cards above the section grow when they arrive and would push it off screen,
    /// so the request waits and <see cref="SavingsRequested"/> is raised again once they are shown.</summary>
    public bool TakeSavingsRequest()
    {
        if (!_savingsWanted || _snapshot == null || _running is { IsCompleted: false }) return false;
        _savingsWanted = false;
        return true;
    }

    private static string L(string key) => LocalizedResources.Instance[key];
    private static string LF(string key, params object[] args) => string.Format(L(key), args);

    // ── Tabs ──

    public ObservableCollection<BillingTab> Tabs { get; } = new();
    private BillingTab? _selectedTab;
    public BillingTab? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value == null || !SetProperty(ref _selectedTab, value)) return;
            foreach (var t in Tabs) t.IsSelected = ReferenceEquals(t, value);
            ShowTab();
        }
    }

    public ObservableCollection<RangeOption> Ranges { get; } = new();
    private int _rangeDays;
    public int RangeDays
    {
        get => _rangeDays;
        set
        {
            if (!Ranges.Any(r => r.Days == value) || !SetProperty(ref _rangeDays, value)) return;
            foreach (var r in Ranges) r.Refresh();
            Savings.FollowRange(value);
            _ = RefreshAsync();
        }
    }

    public ICommand RefreshCommand { get; }
    public ICommand SelectTabCommand { get; }

    // ── What the selected tab shows ──

    private string _tabTitle = "", _kindNote = "", _updatedText = "", _rangeText = "", _chartTitle = "", _chartScale = "", _chartTotal = "";
    private bool _isEmpty, _isLoading = true, _isLocal, _isEstimate, _hasModels, _hasChats, _hasData;
    private IReadOnlyList<ChartBar> _bars = Array.Empty<ChartBar>();
    public string TabTitle { get => _tabTitle; private set => SetProperty(ref _tabTitle, value); }
    /// <summary>What this tab's dollars mean (metered / an estimate at API prices / free).</summary>
    public string KindNote { get => _kindNote; private set => SetProperty(ref _kindNote, value); }
    public string UpdatedText { get => _updatedText; private set => SetProperty(ref _updatedText, value); }
    public string RangeText { get => _rangeText; private set => SetProperty(ref _rangeText, value); }
    /// <summary>No usage recorded anywhere yet.</summary>
    public bool IsEmpty { get => _isEmpty; private set => SetProperty(ref _isEmpty, value); }
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool IsLocal { get => _isLocal; private set => SetProperty(ref _isLocal, value); }
    public bool IsEstimate { get => _isEstimate; private set => SetProperty(ref _isEstimate, value); }
    public bool HasModels { get => _hasModels; private set => SetProperty(ref _hasModels, value); }
    public bool HasChats { get => _hasChats; private set => SetProperty(ref _hasChats, value); }
    /// <summary>The selected tab has use in the chart range.</summary>
    public bool HasRangeData { get => _hasData; private set => SetProperty(ref _hasData, value); }
    public IReadOnlyList<ChartBar> Bars { get => _bars; private set => SetProperty(ref _bars, value); }
    public string ChartTitle { get => _chartTitle; private set => SetProperty(ref _chartTitle, value); }
    public string ChartScale { get => _chartScale; private set => SetProperty(ref _chartScale, value); }
    public string ChartTotal { get => _chartTotal; private set => SetProperty(ref _chartTotal, value); }

    public ObservableCollection<StatCard> Periods { get; } = new();
    public ObservableCollection<TokenCell> TokenCells { get; } = new();
    public ObservableCollection<LegendItem> Legend { get; } = new();
    public ObservableCollection<ModelLine> Models { get; } = new();
    public ObservableCollection<ChatLine> Chats { get; } = new();
    public ObservableCollection<BudgetLine> Budgets { get; } = new();

    /// <summary>The last snapshot (the harness reads it).</summary>
    public BillingSnapshot? Snapshot => _snapshot;

    // ── Visibility: no work while hidden ──

    /// <summary>The page came on screen / went away. On screen: refresh if anything changed since.</summary>
    public void SetVisible(bool visible)
    {
        _visible = visible;
        Savings.SetVisible(visible);
        // Left before the page loaded: a later, ordinary visit opens at the top.
        if (!visible) { _debounce?.Stop(); _savingsWanted = false; return; }
        Account.Refresh();
        if (_stale || _snapshotKey?.Today != Today()) _ = RefreshAsync();
    }

    private void OnUsageAdded()
    {
        _stale = true;
        if (!_visible || _dispatcher == null) return;
        // A request reports usage several times; one refresh per burst, a few seconds later.
        _dispatcher.BeginInvoke(() =>
        {
            if (!_visible) return;
            _debounce ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(3), System.Windows.Threading.DispatcherPriority.Background,
                (_, _) => { _debounce!.Stop(); if (_visible) _ = RefreshAsync(); }, _dispatcher);
            if (!_debounce.IsEnabled) _debounce.Start();
        });
    }

    private DateTime Today() => TimeZoneInfo.ConvertTime(Clock(), Zone).Date;

    // ── Refresh ──

    /// <summary>Re-aggregate (on a worker thread) when the log, the range or the day changed, then show the tab.</summary>
    public Task RefreshAsync(bool force = false)
    {
        if (_running is { IsCompleted: false }) { Interlocked.Exchange(ref _refreshAgain, 1); return _running; }
        return _running = RunRefresh(force);
    }

    private async Task RunRefresh(bool force)
    {
        do
        {
            Interlocked.Exchange(ref _refreshAgain, 0);
            _stale = false;
            var now = Clock();
            var zone = Zone;
            int days = _rangeDays;
            var key = (_cost.Log.Version, days, TimeZoneInfo.ConvertTime(now, zone).Date, zone.Id);
            if (force || _snapshot == null || _snapshotKey != key)
            {
                var weekStart = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
                bool reloadTitles = force || DateTime.UtcNow - _titlesAt > TimeSpan.FromMinutes(1);
                var titles = _titles;
                var snap = await Task.Run(() =>
                {
                    var rows = _cost.Log.ReadAll();
                    DateTime? firstLogDay = rows.Count == 0 ? null : rows.Min(r => r.At).ToLocalTime().Date;
                    rows.AddRange(_cost.LegacyRows(firstLogDay));
                    if (reloadTitles && _db != null)
                    {
                        try { titles = _db.LoadSessionList().GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Title ?? ""); }
                        catch { /* the chat list is only for names */ }
                    }
                    return UsageAggregator.Build(rows, new BillingQuery(now, zone, days, weekStart), id => titles?.GetValueOrDefault(id));
                }).ConfigureAwait(true);
                if (reloadTitles) { _titles = titles; _titlesAt = DateTime.UtcNow; }
                _snapshot = snap;
                _snapshotKey = key;
                UpdatedText = LF("billing.updated", TimeZoneInfo.ConvertTime(now, zone).ToString("HH:mm", CultureInfo.InvariantCulture));
            }
            ApplySnapshot();
        }
        while (Interlocked.Exchange(ref _refreshAgain, 0) == 1);
    }

    private void ApplySnapshot()
    {
        var snap = _snapshot!;
        IsLoading = false;
        IsEmpty = snap.IsEmpty;
        string keep = SelectedTab?.Key ?? ProviderBrand.AllKey;
        // Tabs: All, then each provider with use (any time), in the brand order.
        var keys = snap.Reports.Select(r => r.Key).ToList();
        if (!Tabs.Select(t => t.Key).SequenceEqual(keys))
        {
            Tabs.Clear();
            foreach (var k in keys) Tabs.Add(new BillingTab(k));
        }
        foreach (var t in Tabs) t.Update(snap.Get(t.Key)!);
        _selectedTab = null;
        SelectedTab = Tabs.FirstOrDefault(t => t.Key == keep) ?? Tabs.First();
        var start = snap.RangeStart; var end = snap.Today;
        RangeText = start.ToString("d MMM", CultureInfo.CurrentUICulture) + " – " + end.ToString("d MMM yyyy", CultureInfo.CurrentUICulture);
        if (_savingsWanted) SavingsRequested?.Invoke();   // the numbers are in: now the section has its place
    }

    private void ShowTab()
    {
        var snap = _snapshot;
        var tab = SelectedTab;
        if (snap == null || tab == null) return;
        var r = snap.Get(tab.Key)!;
        bool all = tab.Key == ProviderBrand.AllKey;
        var brand = ProviderBrand.For(tab.Key);
        var kind = all ? BillingKind.Api : brand.Kind;
        IsLocal = kind == BillingKind.Local;
        IsEstimate = kind == BillingKind.Subscription;
        TabTitle = all ? L("billing.tab.all") : tab.Name;
        KindNote = all ? L("billing.kind.all") : kind switch
        {
            BillingKind.Subscription => L("billing.kind.subscription"),
            BillingKind.Local => L("billing.kind.local"),
            _ => L("billing.kind.api"),
        };

        Periods.Clear();
        foreach (var (title, p) in new[] { ("billing.period.today", r.Today), ("billing.period.week", r.Week),
                                           ("billing.period.month", r.Month), ("billing.period.all", r.AllTime) })
            Periods.Add(new StatCard(L(title), IsLocal ? Tok(p.Tokens.All) + " " + L("billing.tokensWord") : Money(p.Spend),
                PeriodDetail(p), p.Spend.Estimated > 0 && !IsLocal, p.HasUse));

        TokenCells.Clear();
        var t = r.Range.Tokens;
        bool Reports(Func<ProviderBrand, bool> what) => all ? snap.Reports.Skip(1).Any(x => what(ProviderBrand.For(x.Key))) : what(brand);
        TokenCells.Add(new TokenCell(L("billing.tok.input"), Tok(t.Input), true));
        TokenCells.Add(new TokenCell(L("billing.tok.output"), Tok(t.Output), true));
        TokenCells.Add(Cell("billing.tok.cacheRead", t.CacheRead, Reports(b => b.ReportsCacheRead)));
        TokenCells.Add(Cell("billing.tok.cacheWrite", t.CacheWrite, Reports(b => b.ReportsCacheWrite)));
        TokenCells.Add(Cell("billing.tok.thinking", t.Thinking, Reports(b => b.ReportsThinking)));
        if (IsLocal || t.Seconds > 0) TokenCells.Add(new TokenCell(L("billing.tok.time"), Duration(t.Seconds), true));
        TokenCells.Add(new TokenCell(L("billing.tok.requests"), t.Requests.ToString("N0", CultureInfo.InvariantCulture), true));

        BuildChart(r, all, brand);
        HasRangeData = r.Range.HasUse;

        Models.Clear();
        decimal topModel = r.Models.Count == 0 ? 0 : r.Models.Max(m => m.Spend.Total);
        long topModelTok = r.Models.Count == 0 ? 0 : r.Models.Max(m => m.Tokens.All);
        foreach (var m in r.Models.Take(12))
        {
            var mb = ProviderBrand.For(m.Provider);
            double share = topModel > 0 ? (double)(m.Spend.Total / topModel) : topModelTok > 0 ? (double)m.Tokens.All / topModelTok : 0;
            string name = m.Model.Length > 0 ? m.Model : L("billing.model.unknown");
            Models.Add(new ModelLine(name, all ? mb.Name : "", KindChip(m.Spend, mb.Kind), mb.Kind == BillingKind.Local ? Usd(0) : Money(m.Spend),
                TokLine(m.Tokens), Requests(m.Tokens.Requests) + (m.Tokens.Seconds > 0 ? " · " + Duration(m.Tokens.Seconds) : ""), share, Brush(mb.Color)));
        }
        HasModels = Models.Count > 0;

        Chats.Clear();
        decimal topChat = r.Chats.Count == 0 ? 0 : r.Chats.Max(c => c.Spend.Total);
        long topChatTok = r.Chats.Count == 0 ? 0 : r.Chats.Max(c => c.Tokens.All);
        foreach (var c in r.Chats)
        {
            double share = topChat > 0 ? (double)(c.Spend.Total / topChat) : topChatTok > 0 ? (double)c.Tokens.All / topChatTok : 0;
            string title = !string.IsNullOrWhiteSpace(c.Title) ? c.Title : L(_titles == null || _titles.ContainsKey(c.ChatId!) ? "billing.chat.untitled" : "billing.chat.gone");
            string providers = string.Join(" · ", c.Providers.Select(p => ProviderBrand.For(p).Name).Order(StringComparer.Ordinal));
            string sub = c.SubAgentUsd > 0 ? LF("billing.chat.subAgents", Usd(c.SubAgentUsd)) : "";
            Chats.Add(new ChatLine(title, providers, IsLocal ? Usd(0) : Money(c.Spend), TokLine(c.Tokens), Requests(c.Tokens.Requests), sub, share));
        }
        HasChats = Chats.Count > 0;

        BuildBudgets(tab.Key, all, kind);
        Savings.FollowTab(tab.Key);
        Account.FollowTab(tab.Key);
    }

    private void ShowBudgets()
    {
        var key = SelectedTab!.Key;
        bool all = key == ProviderBrand.AllKey;
        BuildBudgets(key, all, all ? BillingKind.Api : ProviderBrand.For(key).Kind);
    }

    private static TokenCell Cell(string key, long value, bool reported) =>
        new(L(key), reported || value > 0 ? Tok(value) : "—", reported || value > 0, reported || value > 0 ? "" : L("billing.tok.notReported"));

    private string PeriodDetail(PeriodTotal p)
    {
        if (!p.HasUse) return L("billing.noUse");
        string s = Requests(p.Tokens.Requests);
        s += IsLocal ? (p.Tokens.Seconds > 0 ? " · " + Duration(p.Tokens.Seconds) : "") : " · " + Tok(p.Tokens.All) + " " + L("billing.tokensWord");
        return s;
    }

    private string KindChip(Spend s, BillingKind kind) =>
        kind == BillingKind.Local ? L("billing.chip.free")
        : s.Estimated > 0 && s.Billed > 0 ? L("billing.chip.mixed")
        : s.Estimated > 0 || kind == BillingKind.Subscription ? L("billing.chip.estimate") : L("billing.chip.billed");

    // ── Chart: daily bars (stacked by provider on All) and the running total ──

    private void BuildChart(ProviderReport r, bool all, ProviderBrand brand)
    {
        bool tokens = IsLocal || (r.Range.Spend.Total == 0 && r.Range.Tokens.All > 0);
        ChartTitle = L(tokens ? "billing.chart.tokens" : "billing.chart.spend");
        var bars = new List<ChartBar>(r.Days.Count);
        Legend.Clear();
        var used = new List<string>();
        foreach (var d in r.Days)
        {
            var parts = new List<ChartPart>();
            if (all)
            {
                foreach (var b in ProviderBrand.All)
                {
                    double v = tokens ? d.TokensByProvider.GetValueOrDefault(b.Key) : (double)d.UsdByProvider.GetValueOrDefault(b.Key);
                    if (v <= 0) continue;
                    // Plan CLIs share their vendor's colour; drawn faint (as on their own tab) so they read as estimates.
                    parts.Add(new ChartPart(v, Brush(b.Color, b.Kind == BillingKind.Subscription ? 0.45 : 1)));
                    if (!used.Contains(b.Key)) used.Add(b.Key);
                }
            }
            else if (tokens) { if (d.Tokens > 0) parts.Add(new ChartPart(d.Tokens, Brush(brand.Color))); }
            else
            {
                if (d.Spend.Billed > 0) parts.Add(new ChartPart((double)d.Spend.Billed, Brush(brand.Color)));
                if (d.Spend.Estimated > 0) parts.Add(new ChartPart((double)d.Spend.Estimated, Brush(brand.Color, 0.45)));
            }
            string date = d.Day.ToString("ddd d MMM", CultureInfo.CurrentUICulture);
            string tip = tokens
                ? LF("billing.chart.tipTokens", date, Tok(d.Tokens), Requests(d.Requests), Tok(d.CumulativeTokens))
                : LF("billing.chart.tipSpend", date, Money(d.Spend), Requests(d.Requests), Usd(d.CumulativeUsd));
            if (all && d.UsdByProvider.Count + d.TokensByProvider.Count > 0)
                tip += "\n" + string.Join("\n", ProviderBrand.All.Where(b => d.TokensByProvider.ContainsKey(b.Key))
                    .Select(b => b.Name + ": " + (tokens ? Tok(d.TokensByProvider[b.Key]) : Usd(d.UsdByProvider.GetValueOrDefault(b.Key)))));
            bars.Add(new ChartBar(d.Day, parts, tokens ? d.CumulativeTokens : (double)d.CumulativeUsd, tip));
        }
        if (all)
            foreach (var b in used.Select(ProviderBrand.For))
            {
                bool est = b.Kind == BillingKind.Subscription;
                Legend.Add(new LegendItem((est ? "≈ " : "") + b.Name, Brush(b.Color, est ? 0.45 : 1)));
            }
        else if (!tokens)
        {
            Legend.Add(new LegendItem(L(IsEstimate ? "billing.chip.estimate" : "billing.chip.billed"), Brush(brand.Color, IsEstimate ? 0.45 : 1)));
            if (!IsEstimate && r.Range.Spend.Estimated > 0) Legend.Add(new LegendItem(L("billing.chip.estimate"), Brush(brand.Color, 0.45)));
        }
        else Legend.Add(new LegendItem(L("billing.tokensWord"), Brush(brand.Color)));
        Legend.Add(new LegendItem(L("billing.chart.cumulative"), null));
        double max = bars.Count == 0 ? 0 : bars.Max(b => b.Total);
        ChartScale = tokens ? Tok((long)max) : Usd((decimal)max);
        ChartTotal = tokens ? LF("billing.chart.totalTokens", Tok(r.Range.Tokens.All)) : LF("billing.chart.totalSpend", Money(r.Range.Spend));
        Bars = bars;
    }

    // ── Budgets and limits ──

    private void BuildBudgets(string key, bool all, BillingKind kind)
    {
        Budgets.Clear();
        var s = _settings.Settings;
        if (kind == BillingKind.Local && !all) { Budgets.Add(new BudgetLine(L("billing.budget.localTitle"), L("billing.budget.local"), -1, false)); return; }
        if (all || kind == BillingKind.Api)
        {
            if (s.DailyBudgetUsd > 0)
            {
                decimal today = _cost.TodayCostUsd;
                Budgets.Add(new BudgetLine(L("billing.budget.daily"), LF("billing.budget.ofCap", Usd(today), Usd(s.DailyBudgetUsd)),
                    (double)Math.Min(1m, today / s.DailyBudgetUsd), today >= s.DailyBudgetUsd));
            }
            if (s.RunBudgetUsd > 0)
                Budgets.Add(new BudgetLine(L("billing.budget.run"), LF("billing.budget.runAsk", Usd(s.RunBudgetUsd)), -1, false));
            // Every API account with a credit entered (Credit & plan below, or the old spend window) — a ledger key, "Google" = Gemini.
            foreach (var (ledgerKey, b) in (s.ProviderSpending ?? new()).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                string tabKey = ProviderBrand.FromLedgerKey(ledgerKey);
                if ((!all && key != tabKey) || ProviderCostProjection.IsPackage(ledgerKey)) continue;
                if (b is not { CreditBalanceUsd: { } bal, CreditSetAt: { } at }) continue;
                decimal left = bal - _cost.SpentSince(at, b.CreditSpentOnAnchorDay, ledgerKey);
                string name = tabKey == "Other" && ledgerKey != "Other" ? ledgerKey : ProviderBrand.For(tabKey).Name;
                Budgets.Add(new BudgetLine(LF("billing.budget.credit", name),
                    LF("billing.budget.creditLeft", Usd(left), Usd(bal), at.ToString("d MMM", CultureInfo.CurrentUICulture)),
                    bal > 0 ? (double)Math.Clamp((bal - left) / bal, 0, 1) : -1, left <= 0));
            }
        }
        if (all || kind == BillingKind.Subscription)
        {
            if (s.CliDailyBudgetUsd > 0)
            {
                var keys = all ? ProviderBrand.All.Where(b => b.Kind == BillingKind.Subscription).Select(b => b.Key) : new[] { key };
                foreach (var k in keys)
                {
                    decimal used = _cost.TodayPackageUsd(k);
                    if (all && used == 0) continue;
                    Budgets.Add(new BudgetLine(LF("billing.budget.cliDaily", ProviderBrand.For(k).Name), LF("billing.budget.ofCapEst", Usd(used), Usd(s.CliDailyBudgetUsd)),
                        (double)Math.Min(1m, used / s.CliDailyBudgetUsd), used >= s.CliDailyBudgetUsd));
                }
            }
            if (s.CliRunBudgetUsd > 0)
                Budgets.Add(new BudgetLine(L("billing.budget.cliRun"), LF("billing.budget.runAskEst", Usd(s.CliRunBudgetUsd)), -1, false));
            var plans = all ? ProviderBrand.All.Where(b => b.Kind == BillingKind.Subscription).Select(b => b.Key) : new[] { key };
            foreach (var k in plans)
                if (s.ProviderSpending?.GetValueOrDefault(k) is { MonthlyPackageUsd: { } fee } plan)
                    Budgets.Add(new BudgetLine(LF("billing.budget.plan", ProviderBrand.For(k).Name),
                        LF("billing.budget.planFee", Usd(fee), plan.PackageStartDate?.ToString("d MMM yyyy", CultureInfo.CurrentUICulture) ?? "—"), -1, false));
        }
        if (Budgets.Count == 0) Budgets.Add(new BudgetLine(L("billing.budget.noneTitle"), L("billing.budget.none"), -1, false));
    }

    // ── Formatting ──

    public static string Usd(decimal v) => UsdText.Format(v);

    /// <summary>"25", "$12.50", "12,50", "1,000": what the credit and plan-fee boxes accept (<see cref="UsdText.TryParse"/>).</summary>
    public static bool TryParseUsd(string? text, out decimal usd) => UsdText.TryParse(text, out usd);

    /// <summary>Billed dollars as they are; an estimate with "≈"; both side by side.</summary>
    public static string Money(Spend s) =>
        s.Estimated == 0 ? Usd(s.Billed) : s.Billed == 0 ? "≈ " + Usd(s.Estimated) : Usd(s.Billed) + " + ≈ " + Usd(s.Estimated);

    public static string Tok(long n) => n >= 1_000_000_000 ? (n / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "B"
        : n >= 1_000_000 ? (n / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M"
        : n >= 1_000 ? (n / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k" : n.ToString(CultureInfo.InvariantCulture);

    private static string TokLine(TokenTotals t) => LF("billing.tokLine", Tok(t.Input + t.CacheRead + t.CacheWrite), Tok(t.Output));
    private static string Requests(int n) => LF("billing.requests", n.ToString("N0", CultureInfo.InvariantCulture));

    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.TotalSeconds:0.#}s";
    }

    private static readonly Dictionary<(string, double), SolidColorBrush> Brushes = new();
    public static SolidColorBrush Brush(string hex, double opacity = 1)
    {
        lock (Brushes)
        {
            if (Brushes.TryGetValue((hex, opacity), out var b)) return b;
            b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) { Opacity = opacity };
            b.Freeze();
            return Brushes[(hex, opacity)] = b;
        }
    }

    // ── Rows ──

    public sealed record StatCard(string Title, string Amount, string Detail, bool IsEstimate, bool HasUse);
    public sealed record TokenCell(string Label, string Value, bool Reported, string Note = "");
    public sealed record LegendItem(string Label, Brush? Color)
    {
        public bool IsLine => Color == null;
    }
    public sealed record ModelLine(string Name, string Provider, string Kind, string Cost, string Tokens, string Requests, double Share, Brush Color)
    {
        public bool HasProvider => Provider.Length > 0;
    }
    public sealed record ChatLine(string Title, string Providers, string Cost, string Tokens, string Requests, string SubAgents, double Share)
    {
        public bool HasSubAgents => SubAgents.Length > 0;
    }
    /// <param name="Fraction">0–1 for a meter, −1 for a line without one.</param>
    public sealed record BudgetLine(string Title, string Detail, double Fraction, bool Reached)
    {
        public bool HasMeter => Fraction >= 0;
    }

    public sealed class RangeOption : ViewModelBase
    {
        private readonly BillingViewModel _owner;
        public int Days { get; }
        public string Label { get; }
        public RangeOption(int days, string label, BillingViewModel owner) { Days = days; Label = label; _owner = owner; }
        public bool IsSelected { get => _owner.RangeDays == Days; set { if (value) _owner.RangeDays = Days; } }
        public void Refresh() => OnPropertyChanged(nameof(IsSelected));
    }

    /// <summary>One provider tab: logo (or a text badge) and this month's total.</summary>
    public sealed class BillingTab : ViewModelBase
    {
        private string _monthText = "";
        private bool _selected;
        public string Key { get; }
        public string Name { get; }
        public string Badge { get; }
        public ImageSource? Logo { get; }
        public bool HasLogo => Logo != null;
        public bool IsAll => Key == ProviderBrand.AllKey;
        public bool IsBadge => !IsAll && Logo == null;
        public string MonthText { get => _monthText; private set => SetProperty(ref _monthText, value); }
        public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }

        public BillingTab(string key)
        {
            Key = key;
            var brand = ProviderBrand.For(key);
            Name = key == ProviderBrand.AllKey ? L("billing.tab.all") : brand.Key == key ? brand.Name : key;
            Badge = brand.Badge;
            Logo = key == ProviderBrand.AllKey ? null : LoadLogo(brand.LogoUri);
        }

        public void Update(ProviderReport r)
        {
            var kind = IsAll ? BillingKind.Api : ProviderBrand.For(Key).Kind;
            MonthText = !r.Month.HasUse ? L("billing.noUseMonth")
                : kind == BillingKind.Local ? Tok(r.Month.Tokens.All) + " " + L("billing.tokensWord")
                : Money(r.Month.Spend);
        }

        private static readonly Dictionary<Uri, ImageSource?> Logos = new();
        /// <summary>The bundled logo, decoded once at tab size (null when missing — the tab shows its text badge).</summary>
        public static ImageSource? LoadLogo(Uri? uri)
        {
            if (uri == null) return null;
            lock (Logos)
            {
                if (Logos.TryGetValue(uri, out var hit)) return hit;
                ImageSource? img = null;
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = uri;
                    bmp.DecodePixelWidth = 64;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    img = bmp;
                }
                catch { }
                return Logos[uri] = img;
            }
        }
    }
}

public sealed record ChartPart(double Value, Brush Color);

/// <summary>One day of the chart: its stacked parts, the running total through it, and its tooltip.</summary>
public sealed record ChartBar(DateTime Day, IReadOnlyList<ChartPart> Parts, double Cumulative, string Tip)
{
    public double Total => Parts.Sum(p => p.Value);
}
