using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>The spend window: today / 7 / 30 days paid against "without savings", a 14-day stacked bar chart, where the
/// savings came from, and an estimated credit balance counted down from one the user entered (owner, 2026-10-13: "ทำระบบ
/// กราฟการใช้โทเค็น ว่าประหยัดได้เท่าไหร่ ถ้าไม่ประหยัดเสียเท่าไหร่" and "ถามยอดเครดิตที่เหลือจาก provider ได้ไหม" —
/// no provider has a balance API, hence the entered anchor).</summary>
public sealed class UsageViewModel : ViewModelBase
{
    public const double ChartHeight = 150;

    private readonly CostTrackingService _cost;
    private readonly SettingsService _settings;
    private readonly Action? _openBilling;
    private readonly System.Windows.Threading.Dispatcher? _dispatcher;
    private bool _refreshQueued;

    public sealed record Period(string Title, string Paid, string Without, string Saved, bool HasUse);
    public sealed record Bar(string Label, double PaidHeight, double SavedHeight, string Tip);
    public sealed record SavingRow(string Name, string Amount, double Fraction);

    public ObservableCollection<Period> Periods { get; } = new();
    public ObservableCollection<Bar> Bars { get; } = new();
    public ObservableCollection<SavingRow> Savings { get; } = new();

    // ── Cumulative line chart: paid vs without savings, the gap between them is what the savings saved ──
    public const double LineWidth = 620, LineHeight = 170;
    public sealed record LinePoint(double X, double Y, string Tip);
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
    public bool HasBilling => _openBilling != null;

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
            _settings.UpdateSettings(s => { s.CreditBalanceUsd = null; s.CreditBalanceSetAt = null; s.CreditBalanceSpentThatDay = 0; });
            Refresh();
        });
        OpenBillingCommand = new RelayCommand(() => _openBilling?.Invoke());
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

    /// <summary>"$12.34"; under a cent "$0.004" so a cheap day is not shown as nothing.</summary>
    public static string Usd(decimal v) =>
        "$" + (Math.Abs(v) < 0.01m && v != 0 ? v.ToString("0.000", CultureInfo.InvariantCulture) : v.ToString("N2", CultureInfo.InvariantCulture));

    // ── Provider filter: all together, or one provider alone ──
    public sealed record ProviderChoice(string? Key, string Label);
    public ObservableCollection<ProviderChoice> Providers { get; } = new();
    private ProviderChoice? _selectedProvider;
    private bool _syncingProviders;
    public ProviderChoice? SelectedProvider
    {
        get => _selectedProvider;
        set { if (SetProperty(ref _selectedProvider, value) && !_syncingProviders) Refresh(); }
    }
    /// <summary>Only offered when more than one provider has been used.</summary>
    public bool HasProviderChoice => Providers.Count > 2;

    private string _tokensText = "";
    /// <summary>The 30 days' tokens: sent, read from cache, generated.</summary>
    public string TokensText { get => _tokensText; private set => SetProperty(ref _tokensText, value); }

    private static string Tok(long n) => n >= 1_000_000 ? (n / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + "M"
        : n >= 1_000 ? (n / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k" : n.ToString(CultureInfo.InvariantCulture);

    private void SyncProviders()
    {
        var seen = _cost.ProvidersIn(30);
        string? keep = _selectedProvider?.Key;
        _syncingProviders = true;
        try
        {
            if (Providers.Count != seen.Count + 1 || !seen.SequenceEqual(Providers.Skip(1).Select(p => p.Key!)))
            {
                Providers.Clear();
                Providers.Add(new ProviderChoice(null, L("usage.providerAll")));
                foreach (var p in seen) Providers.Add(new ProviderChoice(p, p == "Other" ? L("usage.providerOther") : p));
                OnPropertyChanged(nameof(HasProviderChoice));
            }
            SelectedProvider = Providers.FirstOrDefault(p => p.Key == keep) ?? Providers[0];
        }
        finally { _syncingProviders = false; }
    }

    public void Refresh()
    {
        SyncProviders();
        var month = _cost.LastDays(30, _selectedProvider?.Key);
        TokensText = LF("usage.tokens", Tok(month.Sum(d => d.InputTokens)), Tok(month.Sum(d => d.CacheReadTokens)),
            Tok(month.Sum(d => d.OutputTokens)), month.Sum(d => d.Requests).ToString("N0", CultureInfo.InvariantCulture));
        Periods.Clear();
        Periods.Add(PeriodOf(L("usage.today"), month.Skip(29)));
        Periods.Add(PeriodOf(L("usage.week"), month.Skip(23)));
        Periods.Add(PeriodOf(L("usage.month"), month));

        var fortnight = month.Skip(16).ToList();
        decimal max = fortnight.Count == 0 ? 0 : fortnight.Max(d => Math.Max(d.BaselineUsd, d.Usd));
        Bars.Clear();
        foreach (var d in fortnight)
        {
            decimal saved = Math.Max(0, d.BaselineUsd - d.Usd);
            double paidH = max > 0 ? (double)(d.Usd / max) * ChartHeight : 0;
            double savedH = max > 0 ? (double)(saved / max) * ChartHeight : 0;
            string tip = LF("usage.barTip", d.Day.ToString("ddd d MMM", CultureInfo.CurrentUICulture), Usd(d.Usd), d.Requests,
                Usd(d.BaselineUsd), Usd(saved));
            Bars.Add(new Bar(d.Day.ToString("d/M", CultureInfo.InvariantCulture), paidH, savedH, tip));
        }

        BuildLines(fortnight);

        var parts = new (string Key, decimal Usd)[]
        {
            ("usage.byCache", month.Sum(d => d.SavedCacheUsd)),
            ("usage.byToolSearch", month.Sum(d => d.SavedToolSearchUsd)),
            ("usage.byCompaction", month.Sum(d => d.SavedCompactionUsd)),
            ("usage.byBatch", month.Sum(d => d.SavedBatchUsd)),
        };
        decimal top = parts.Max(p => Math.Max(0, p.Usd));
        Savings.Clear();
        foreach (var (key, usd) in parts)
            Savings.Add(new SavingRow(L(key), Usd(usd), top > 0 ? (double)(Math.Max(0, usd) / top) : 0));

        var s = _settings.Settings;
        HasBalance = s.CreditBalanceUsd is not null && s.CreditBalanceSetAt is not null;
        if (HasBalance)
        {
            decimal spent = _cost.SpentSince(s.CreditBalanceSetAt!.Value, s.CreditBalanceSpentThatDay);
            BalanceText = LF("usage.balanceValue", Usd(s.CreditBalanceUsd!.Value - spent));
            BalanceDetail = LF("usage.balanceSince", Usd(s.CreditBalanceUsd.Value),
                s.CreditBalanceSetAt.Value.ToString("d MMM HH:mm", CultureInfo.CurrentUICulture), Usd(spent));
        }
        else
        {
            BalanceText = "";
            BalanceDetail = L("usage.balanceNone");
        }
    }

    /// <summary>Running totals over the 14 days. A day recorded before "without savings" was tracked (baseline 0) counts
    /// as no saving — the without line never dips under the paid one.</summary>
    private void BuildLines(List<CostTrackingService.UsageDay> days)
    {
        PaidDots.Clear();
        BaseDots.Clear();
        if (days.Count == 0)
        {
            PaidLine = BaseLine = GapArea = PaidArea = new();
            LineSaved = LineScaleTop = LineFirstDay = LineLastDay = "";
            return;
        }
        var paid = new List<decimal>(); var without = new List<decimal>();
        decimal p = 0, b = 0;
        foreach (var d in days)
        {
            p += d.Usd;
            b += Math.Max(d.BaselineUsd, d.Usd);
            paid.Add(p); without.Add(b);
        }
        decimal top = Math.Max(b, 0.01m) * 1.08m;   // a little headroom over the highest point
        double step = days.Count > 1 ? LineWidth / (days.Count - 1) : 0;
        double Y(decimal v) => LineHeight - (double)(v / top) * LineHeight;

        var pl = new System.Windows.Media.PointCollection();
        var bl = new System.Windows.Media.PointCollection();
        for (int i = 0; i < days.Count; i++)
        {
            double x = i * step;
            pl.Add(new System.Windows.Point(x, Y(paid[i])));
            bl.Add(new System.Windows.Point(x, Y(without[i])));
            string day = days[i].Day.ToString("ddd d MMM", CultureInfo.CurrentUICulture);
            string tip = LF("usage.lineTip", day, Usd(paid[i]), Usd(without[i]), Usd(without[i] - paid[i]));
            PaidDots.Add(new LinePoint(x, Y(paid[i]), tip));
            BaseDots.Add(new LinePoint(x, Y(without[i]), tip));
        }
        var gap = new System.Windows.Media.PointCollection(bl);
        for (int i = pl.Count - 1; i >= 0; i--) gap.Add(pl[i]);
        var area = new System.Windows.Media.PointCollection(pl)
        {
            new System.Windows.Point(pl[^1].X, LineHeight),
            new System.Windows.Point(0, LineHeight),
        };
        PaidLine = pl; BaseLine = bl; GapArea = gap; PaidArea = area;
        decimal saved = b - p;
        string pct = (b > 0 ? saved / b * 100m : 0m).ToString("0", CultureInfo.InvariantCulture);
        LineSaved = LF("usage.lineSaved", Usd(saved), pct);
        LineScaleTop = Usd(top);
        LineFirstDay = days[0].Day.ToString("d MMM", CultureInfo.CurrentUICulture);
        LineLastDay = days[^1].Day.ToString("d MMM", CultureInfo.CurrentUICulture);
    }

    private static Period PeriodOf(string title, IEnumerable<CostTrackingService.UsageDay> days)
    {
        var list = days.ToList();
        decimal paid = list.Sum(d => d.Usd), baseline = list.Sum(d => d.BaselineUsd);
        decimal saved = Math.Max(0, baseline - paid);
        string pct = (baseline > 0 ? saved / baseline * 100m : 0m).ToString("0", CultureInfo.InvariantCulture) + "%";
        bool any = list.Any(d => d.Requests > 0 || d.Usd > 0);
        return any
            ? new Period(title, LF("usage.paid", Usd(paid)), LF("usage.without", Usd(baseline)), LF("usage.saved", Usd(saved), pct), true)
            : new Period(title, L("usage.nothingYet"), "", "", false);
    }

    /// <summary>"25", "$12.50", "12,50": the balance read off the billing page.</summary>
    public static bool TryParseUsd(string? text, out decimal usd)
    {
        string t = (text ?? "").Trim().TrimStart('$').Replace(" ", "");
        if (t.Contains(',') && !t.Contains('.')) t = t.Replace(',', '.');
        else t = t.Replace(",", "");
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out usd) && usd >= 0;
    }

    private void SetBalance()
    {
        if (!TryParseUsd(BalanceInput, out decimal usd))
        {
            BalanceError = L("usage.balanceBad");
            return;
        }
        BalanceError = "";
        decimal today = _cost.TodayCostUsd;
        _settings.UpdateSettings(s => { s.CreditBalanceUsd = usd; s.CreditBalanceSetAt = DateTime.Now; s.CreditBalanceSpentThatDay = today; });
        BalanceInput = "";
        Refresh();
    }
}
