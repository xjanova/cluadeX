using System.IO;
using System.Text.Json;

namespace CluadeX.Services;

/// <summary>
/// Tracks API usage costs per session and per model.
/// Based on Claude Code's cost-tracker.ts pattern.
/// Tracks input/output tokens, cache read/creation, and USD cost.
/// </summary>
public class CostTrackingService
{
    private readonly object _lock = new();

    // Per-session totals
    public int TotalInputTokens { get; private set; }
    public int TotalOutputTokens { get; private set; }
    public int TotalCacheReadTokens { get; private set; }
    public int TotalCacheCreationTokens { get; private set; }
    public int TotalRequests { get; private set; }
    public decimal TotalCostUsd { get; private set; }
    /// <summary>What this session's requests would have cost with no saving (see <see cref="Price"/>).</summary>
    public decimal TotalBaselineUsd { get; private set; }

    // Per-model tracking
    private readonly Dictionary<string, ModelUsage> _modelUsage = new();

    /// <summary>Fires when cost is updated.</summary>
    public event Action<CostSummary>? OnCostUpdated;

    // ── Per-day ledger (usage-daily.json in the data folder): what CluadeX spent today across restarts ──
    private readonly string? _ledgerPath;
    private readonly Dictionary<string, DailyUsage> _days = new();
    private DateTime _lastLedgerSave = DateTime.MinValue;
    private bool _ledgerDirty;
    // Monthly comparisons and long-lived credit anchors must survive beyond the old three-month window.
    private const int LedgerKeepDays = 3660;

    public CostTrackingService() { }

    /// <summary>With the app's settings: the per-day ledger lives in its data folder (portable runs keep their own).</summary>
    public CostTrackingService(SettingsService settings)
    {
        try
        {
            _ledgerPath = Path.Combine(settings.DataRoot, "usage-daily.json");
            if (File.Exists(_ledgerPath))
                foreach (var (day, u) in JsonSerializer.Deserialize<Dictionary<string, DailyUsage>>(File.ReadAllText(_ledgerPath)) ?? new())
                    _days[day] = u;
        }
        catch { /* a damaged ledger starts over; it is only a display */ }
    }

    private static string DayKey(DateTime local) => local.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Spent today (local date), all CluadeX sessions today included.</summary>
    public decimal TodayCostUsd { get { lock (_lock) return _days.TryGetValue(DayKey(DateTime.Now), out var d) ? d.Usd : 0m; } }

    /// <summary>Today's requests (all sessions today).</summary>
    public int TodayRequests { get { lock (_lock) return _days.TryGetValue(DayKey(DateTime.Now), out var d) ? d.Requests : 0; } }

    /// <summary>One day of the ledger for the spend panel (a copy; days with no use are all zero).</summary>
    public sealed record UsageDay(DateTime Day, decimal Usd, decimal BaselineUsd, int Requests, decimal SavedCacheUsd,
        decimal SavedToolSearchUsd, decimal SavedCompactionUsd, decimal SavedBatchUsd,
        long InputTokens = 0, long CacheReadTokens = 0, long OutputTokens = 0);

    /// <summary>The provider a model id belongs to, for the per-provider ledger: "Anthropic", "OpenAI", "Google", else "Other".</summary>
    public static string ProviderOf(string? model)
    {
        string m = (model ?? "").Trim().ToLowerInvariant();
        int slash = m.LastIndexOf('/');
        if (slash >= 0) m = m[(slash + 1)..];
        if (m.StartsWith("claude") || m.Contains("anthropic")) return "Anthropic";
        if (m.StartsWith("gpt") || m.StartsWith("chatgpt") || m.StartsWith("codex")
            || (m.Length > 1 && m[0] == 'o' && char.IsDigit(m[1]))) return "OpenAI";
        if (m.StartsWith("gemini") || m.StartsWith("gemma")) return "Google";
        return "Other";
    }

    /// <summary>Providers with recorded use in the last <paramref name="days"/> days (days logged before the split had none).</summary>
    public IReadOnlyList<string> ProvidersIn(int days)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        lock (_lock)
            for (int i = 0; i < days; i++)
                if (_days.TryGetValue(DayKey(DateTime.Today.AddDays(-i)), out var u) && u.ByProvider != null)
                    foreach (var (k, v) in u.ByProvider) if (v.Requests > 0 || v.Usd > 0) set.Add(k);
        return set.ToList();
    }

    /// <summary>The last <paramref name="days"/> local days, oldest first, today last; with <paramref name="provider"/>
    /// only that provider's share (days recorded before the split count as none).</summary>
    public IReadOnlyList<UsageDay> LastDays(int days, string? provider)
        => days <= 0 ? Array.Empty<UsageDay>() : Between(DateTime.Today.AddDays(1 - days), DateTime.Today, provider);

    public const string UnattributedProvider = "__unattributed";

    /// <summary>Old ledger rows have totals without provider attribution. Keep them visible exactly once.</summary>
    private static DailyUsage Unattributed(DailyUsage all)
    {
        var parts = all.ByProvider?.Values.ToList() ?? new List<DailyUsage>();
        return new DailyUsage
        {
            Usd = Math.Max(0, all.Usd - parts.Sum(p => p.Usd)),
            BaselineUsd = Math.Max(0, Math.Max(all.BaselineUsd, all.Usd) - parts.Sum(p => Math.Max(p.BaselineUsd, p.Usd))),
            Requests = Math.Max(0, all.Requests - parts.Sum(p => p.Requests)),
            InputTokens = Math.Max(0, all.InputTokens - parts.Sum(p => p.InputTokens)),
            CacheReadTokens = Math.Max(0, all.CacheReadTokens - parts.Sum(p => p.CacheReadTokens)),
            OutputTokens = Math.Max(0, all.OutputTokens - parts.Sum(p => p.OutputTokens)),
            SavedCacheUsd = all.SavedCacheUsd - parts.Sum(p => p.SavedCacheUsd),
            SavedToolSearchUsd = all.SavedToolSearchUsd - parts.Sum(p => p.SavedToolSearchUsd),
            SavedCompactionUsd = all.SavedCompactionUsd - parts.Sum(p => p.SavedCompactionUsd),
            SavedBatchUsd = all.SavedBatchUsd - parts.Sum(p => p.SavedBatchUsd),
        };
    }

    public DateTime FirstUsageDay
    {
        get
        {
            lock (_lock) return _days.Keys.Select(k => DateTime.TryParseExact(k, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d) ? d : DateTime.Today).DefaultIfEmpty(DateTime.Today).Min();
        }
    }

    public IReadOnlyList<string> RecordedProviders
    {
        get
        {
            lock (_lock)
            {
                var keys = _days.Values.SelectMany(d => d.ByProvider?.Keys.AsEnumerable() ?? Enumerable.Empty<string>()).ToHashSet(StringComparer.Ordinal);
                if (_days.Values.Any(d => { var u = Unattributed(d); return u.Usd != 0 || u.Requests != 0 || u.InputTokens != 0 || u.OutputTokens != 0; }))
                    keys.Add(UnattributedProvider);
                return keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>Inclusive local dates, zero-filled, without changing the ledger or its historical provider split.</summary>
    public IReadOnlyList<UsageDay> Between(DateTime start, DateTime end, string? provider = null)
    {
        start = start.Date; end = end.Date;
        if (end < start) return Array.Empty<UsageDay>();
        int days = (end - start).Days + 1;
        if (days > 3660) throw new ArgumentOutOfRangeException(nameof(start), "Choose at most ten years.");
        var list = new List<UsageDay>(days);
        lock (_lock)
        {
            for (int i = 0; i < days; i++)
            {
                var d = start.AddDays(i);
                DailyUsage? u = null;
                if (_days.TryGetValue(DayKey(d), out var all))
                    u = provider == null ? all : provider == UnattributedProvider ? Unattributed(all) : all.ByProvider?.GetValueOrDefault(provider);
                list.Add(u != null
                    ? new UsageDay(d, u.Usd, Math.Max(u.BaselineUsd, u.Usd), u.Requests, u.SavedCacheUsd, u.SavedToolSearchUsd,
                        u.SavedCompactionUsd, u.SavedBatchUsd, u.InputTokens, u.CacheReadTokens, u.OutputTokens)
                    : new UsageDay(d, 0, 0, 0, 0, 0, 0, 0));
            }
        }
        return list;
    }

    /// <summary>The last <paramref name="days"/> local days, oldest first, today last.</summary>
    public IReadOnlyList<UsageDay> LastDays(int days) => LastDays(days, null);

    /// <summary>Spent since a moment: the days after <paramref name="anchor"/>'s day, plus what its day spent after it
    /// (<paramref name="spentOnAnchorDayAtAnchor"/> = that day's total at the moment). Days older than the ledger keeps are gone.</summary>
    public decimal SpentSince(DateTime anchor, decimal spentOnAnchorDayAtAnchor, string? provider = null)
    {
        lock (_lock)
        {
            decimal sum = 0;
            foreach (var (key, u) in _days)
            {
                if (!DateTime.TryParseExact(key, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var day)) continue;
                var part = provider == null ? u : provider == UnattributedProvider ? Unattributed(u) : u.ByProvider?.GetValueOrDefault(provider);
                if (part == null) continue;
                if (day.Date > anchor.Date) sum += part.Usd;
                else if (day.Date == anchor.Date) sum += Math.Max(0, part.Usd - spentOnAnchorDayAtAnchor);
            }
            return sum;
        }
    }

    /// <summary>CLI usage is an estimated API-equivalent value, never an extra API charge or a budget deduction.</summary>
    public void RecordPackageUsage(string provider, decimal equivalentUsd, int inputTokens, int outputTokens, string method, int cacheReadTokens = 0)
    {
        if (!ProviderCostProjection.IsPackage(provider)) return;
        lock (_lock)
        {
            string day = DayKey(DateTime.Now);
            if (!_days.TryGetValue(day, out var all)) _days[day] = all = new DailyUsage();
            all.ByProvider ??= new();
            if (!all.ByProvider.TryGetValue(provider, out var part)) all.ByProvider[provider] = part = new DailyUsage();
            foreach (var u in new[] { all, part })
            {
                u.BaselineUsd += Math.Max(0, equivalentUsd);
                u.InputTokens += Math.Max(0, inputTokens); u.OutputTokens += Math.Max(0, outputTokens); u.Requests++;
                u.CacheReadTokens += Math.Max(0, cacheReadTokens);
            }
            part.EstimateMethod = method;
            _ledgerDirty = true;
        }
        FlushLedger();
        OnCostUpdated?.Invoke(GetSummary());
    }

    /// <summary>Write the ledger now (app exit; otherwise it is written at most every few seconds).</summary>
    public void FlushLedger()
    {
        string? json = null;
        lock (_lock)
        {
            if (!_ledgerDirty || _ledgerPath == null) return;
            foreach (var old in _days.Keys.OrderByDescending(k => k, StringComparer.Ordinal).Skip(LedgerKeepDays).ToList())
                _days.Remove(old);
            json = JsonSerializer.Serialize(_days);
            _ledgerDirty = false;
            _lastLedgerSave = DateTime.UtcNow;
        }
        try
        {
            string tmp = _ledgerPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _ledgerPath, overwrite: true);
        }
        catch { lock (_lock) _ledgerDirty = true; }
    }

    // ── What the savings saved ──
    // The baseline is the same work with none of them: every prompt token at the plain input price (no prompt cache),
    // the tokens tool search and microcompact kept out of the request sent as well, and no batch discount. The four
    // parts below add up to baseline − actual exactly (cache can be negative on a request that only writes the cache).

    /// <summary>Tokens a request did NOT send because of a saving. The agent loop sets it (AsyncLocal, so a sub-agent
    /// running beside it keeps its own) right before a request; the provider adds tool search's share; the request's
    /// first usage record consumes it.</summary>
    public sealed class RequestSavingsHint
    {
        /// <summary>Old tool results microcompact shortened in this request (estimated tokens).</summary>
        public int CompactionAvoidedTokens { get; set; }
        /// <summary>Tool definitions sent as names only (defer_loading) instead of in full, net of the names note.</summary>
        public int ToolSearchAvoidedTokens { get; set; }
        internal bool Used;
    }

    private static readonly AsyncLocal<RequestSavingsHint?> NextRequestSlot = new();

    /// <summary>The hint for the request about to be made in this flow (null = none).</summary>
    public static RequestSavingsHint? NextRequest { get => NextRequestSlot.Value; set => NextRequestSlot.Value = value; }

    /// <summary>One request's price split: what it cost, what it would have cost with no saving, and why it is lower.</summary>
    public readonly record struct CostBreakdown(decimal Actual, decimal Baseline, decimal SavedByCache,
        decimal SavedByToolSearch, decimal SavedByCompaction, decimal SavedByBatch);

    /// <summary>The price of one usage record (pure: the recorder and the tests use the same arithmetic).</summary>
    /// <param name="promptTokens">The request's whole prompt (input + cache reads + cache writes) when this record carries
    /// none of it — the output record of a streamed reply. It decides Claude Haiku 5.5's price tier.</param>
    public static CostBreakdown Price(string model, int inputTokens, int outputTokens, int cacheReadTokens = 0,
        int cacheCreationTokens = 0, int cacheWrite1hTokens = 0, decimal priceFactor = 1m, int promptTokens = 0,
        int toolSearchAvoidedTokens = 0, int compactionAvoidedTokens = 0)
    {
        const decimal M = 1_000_000m;
        int prompt = Math.Max(promptTokens, inputTokens + cacheReadTokens + cacheCreationTokens);
        var p = GetPricing(model, prompt);
        int write1h = Math.Clamp(cacheWrite1hTokens, 0, Math.Max(0, cacheCreationTokens));
        decimal promptCost =
            inputTokens * p.InputPerMToken / M
            + cacheReadTokens * p.CacheReadPerMToken / M
            + (cacheCreationTokens - write1h) * p.CacheWritePerMToken / M
            + write1h * p.InputPerMToken * 2m / M;
        decimal outputCost = outputTokens * p.OutputPerMToken / M;
        decimal full = promptCost + outputCost;                       // list price, before any batch discount
        decimal actual = full * priceFactor;
        decimal noCachePrompt = (inputTokens + cacheReadTokens + cacheCreationTokens) * p.InputPerMToken / M;
        decimal byToolSearch = Math.Max(0, toolSearchAvoidedTokens) * p.InputPerMToken / M;
        decimal byCompaction = Math.Max(0, compactionAvoidedTokens) * p.InputPerMToken / M;
        decimal byCache = noCachePrompt - promptCost;
        decimal byBatch = full - actual;
        decimal baseline = noCachePrompt + outputCost + byToolSearch + byCompaction;
        return new CostBreakdown(actual, baseline, byCache, byToolSearch, byCompaction, byBatch);
    }

    /// <summary>Record usage from an API response.</summary>
    /// <param name="isNewRequest">True only for the first usage record of a request (avoids double-counting).</param>
    /// <param name="priceFactor">Multiplier on the list price: 0.5 for a request run through a batch API.</param>
    /// <param name="cacheWrite1hTokens">Of <paramref name="cacheCreationTokens"/>, the ones written with the 1-hour TTL:
    /// billed at 2x input instead of the 5-minute write price.</param>
    /// <param name="promptTokens">The request's whole prompt, for a record that carries none of it (the output record of
    /// a streamed reply): Claude Haiku 5.5 bills every token of a request whose prompt is over 100,000 at its higher price.</param>
    public void RecordUsage(string model, int inputTokens, int outputTokens,
        int cacheReadTokens = 0, int cacheCreationTokens = 0, bool isNewRequest = true, decimal priceFactor = 1m,
        int cacheWrite1hTokens = 0, int promptTokens = 0)
    {
        decimal cost;
        bool saveLedger;
        // The request's avoided tokens ride on its first record that carries prompt tokens, once.
        int tsAvoided = 0, compAvoided = 0;
        if (NextRequest is { Used: false } hint && inputTokens + cacheReadTokens + cacheCreationTokens > 0)
        {
            hint.Used = true;
            tsAvoided = hint.ToolSearchAvoidedTokens;
            compAvoided = hint.CompactionAvoidedTokens;
        }
        var price = Price(model, inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens, cacheWrite1hTokens,
            priceFactor, promptTokens, tsAvoided, compAvoided);
        lock (_lock)
        {
            TotalInputTokens += inputTokens;
            TotalOutputTokens += outputTokens;
            TotalCacheReadTokens += cacheReadTokens;
            TotalCacheCreationTokens += cacheCreationTokens;
            if (isNewRequest) TotalRequests++;

            cost = price.Actual;
            TotalCostUsd += cost;
            TotalBaselineUsd += price.Baseline;

            string day = DayKey(DateTime.Now);
            if (!_days.TryGetValue(day, out var du)) _days[day] = du = new DailyUsage();
            // The day's total, and the same record under the model's provider (the spend window can show one alone).
            du.ByProvider ??= new();
            string prov = ProviderOf(model);
            if (!du.ByProvider.TryGetValue(prov, out var pu)) du.ByProvider[prov] = pu = new DailyUsage();
            foreach (var u in new[] { du, pu })
            {
                u.Usd += cost;
                u.BaselineUsd += price.Baseline;
                u.SavedCacheUsd += price.SavedByCache;
                u.SavedToolSearchUsd += price.SavedByToolSearch;
                u.SavedCompactionUsd += price.SavedByCompaction;
                u.SavedBatchUsd += price.SavedByBatch;
                u.InputTokens += inputTokens + cacheCreationTokens;
                u.CacheReadTokens += cacheReadTokens;
                u.OutputTokens += outputTokens;
                if (isNewRequest) u.Requests++;
            }
            _ledgerDirty = true;
            saveLedger = _ledgerPath != null && (DateTime.UtcNow - _lastLedgerSave).TotalSeconds >= 5;

            // Per-model tracking
            if (!_modelUsage.ContainsKey(model))
                _modelUsage[model] = new ModelUsage { Model = model };

            var mu = _modelUsage[model];
            mu.InputTokens += inputTokens;
            mu.OutputTokens += outputTokens;
            mu.CacheReadTokens += cacheReadTokens;
            mu.CacheCreationTokens += cacheCreationTokens;
            mu.Requests++;
            mu.CostUsd += cost;
        }

        // The run this request was made for (null outside any run: a title, a plain chat answer).
        AgentRunScope.Current?.AddUsage(cost, inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens, isNewRequest, price.Baseline);
        if (saveLedger) FlushLedger();
        OnCostUpdated?.Invoke(GetSummary());
    }

    // ── Dispatch strategy ledger: what each kind of run cost, took and achieved (tuning DispatchPolicy from real numbers) ──

    /// <summary>One finished agent run under its dispatch strategy (<see cref="Agents.DispatchPolicy.Tag"/>): its cost (the
    /// run scope's total, sub-agents included), wall time, and whether it ended successfully.</summary>
    public void RecordRun(string strategy, decimal usd, double seconds, bool success, DateTime? when = null)
    {
        if (string.IsNullOrWhiteSpace(strategy)) strategy = "single";
        bool save;
        lock (_lock)
        {
            string day = DayKey(when ?? DateTime.Now);
            if (!_days.TryGetValue(day, out var du)) _days[day] = du = new DailyUsage();
            du.ByStrategy ??= new();
            if (!du.ByStrategy.TryGetValue(strategy, out var su)) du.ByStrategy[strategy] = su = new StrategyUsage();
            su.Runs++;
            if (success) su.Successes++;
            su.Usd += usd;
            su.Seconds += Math.Max(0, seconds);
            _ledgerDirty = true;
            save = _ledgerPath != null;
        }
        if (save) FlushLedger();
    }

    /// <summary>One strategy over a span of days: runs, successes, total cost and time; averages derived.</summary>
    public sealed record StrategyRow(string Strategy, int Runs, int Successes, decimal Usd, double Seconds)
    {
        public decimal UsdPerRun => Runs == 0 ? 0 : Usd / Runs;
        public double SecondsPerRun => Runs == 0 ? 0 : Seconds / Runs;
        public double SuccessShare => Runs == 0 ? 0 : (double)Successes / Runs;
    }

    /// <summary>Every strategy with a run in the last <paramref name="days"/> local days (today included), in
    /// <see cref="Agents.DispatchPolicy.AllTags"/> order, then any other tag.</summary>
    public IReadOnlyList<StrategyRow> StrategyBreakdown(int days)
        => days <= 0 ? Array.Empty<StrategyRow>() : StrategyBreakdown(DateTime.Today.AddDays(1 - days), DateTime.Today);

    public IReadOnlyList<StrategyRow> StrategyBreakdown(DateTime start, DateTime end)
    {
        var sum = new Dictionary<string, StrategyUsage>(StringComparer.Ordinal);
        lock (_lock)
            foreach (var (key, u) in _days)
                if (DateTime.TryParseExact(key, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var day) && day >= start.Date && day <= end.Date && u.ByStrategy != null)
                    foreach (var (k, v) in u.ByStrategy)
                    {
                        if (!sum.TryGetValue(k, out var t)) sum[k] = t = new StrategyUsage();
                        t.Runs += v.Runs; t.Successes += v.Successes; t.Usd += v.Usd; t.Seconds += v.Seconds;
                    }
        return sum.OrderBy(kv => Array.IndexOf(Agents.DispatchPolicy.AllTags, kv.Key) is var at && at < 0 ? 99 : at)
                  .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                  .Select(kv => new StrategyRow(kv.Key, kv.Value.Runs, kv.Value.Successes, kv.Value.Usd, kv.Value.Seconds))
                  .ToList();
    }

    /// <summary>Get current session cost summary.</summary>
    public CostSummary GetSummary()
    {
        lock (_lock)
        {
            return new CostSummary
            {
                TotalInputTokens = TotalInputTokens,
                TotalOutputTokens = TotalOutputTokens,
                TotalCacheReadTokens = TotalCacheReadTokens,
                TotalCacheCreationTokens = TotalCacheCreationTokens,
                TotalRequests = TotalRequests,
                TotalCostUsd = TotalCostUsd,
                ModelUsage = _modelUsage.Values.ToList(),
            };
        }
    }

    /// <summary>Reset session cost tracking.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            TotalInputTokens = 0;
            TotalOutputTokens = 0;
            TotalCacheReadTokens = 0;
            TotalCacheCreationTokens = 0;
            TotalRequests = 0;
            TotalCostUsd = 0;
            TotalBaselineUsd = 0;
            _modelUsage.Clear();
        }
        OnCostUpdated?.Invoke(GetSummary());
    }

    /// <summary>Format cost as human-readable string.</summary>
    public string FormatCost()
    {
        lock (_lock)
        {
            if (TotalRequests == 0) return "No API usage";
            return $"${TotalCostUsd:F4} ({TotalInputTokens:N0} in / {TotalOutputTokens:N0} out" +
                   (TotalCacheReadTokens > 0 ? $" / {TotalCacheReadTokens:N0} cache" : "") +
                   $" / {TotalRequests} requests)";
        }
    }

    /// <summary>Get per-model pricing (USD per million tokens).</summary>
    /// <remarks>
    /// Anthropic pricing updated October 2026 (first-party API rates).
    /// Format: (InputPerMToken, OutputPerMToken, CacheReadPerMToken, CacheWritePerMToken)
    /// Cache write = 1.25x input price; cache read = 0.1x input, except where a model's row says otherwise.
    /// Order matters: "opus-5-5" must be tested before "opus-5", "fable-5-1" before "fable".
    /// </remarks>
    /// <summary>List price per MTok for a model (the same table the costs are recorded with). <paramref name="promptTokens"/>
    /// = a request's whole prompt (input + cache reads + cache writes); Claude Haiku 5.5's and GPT-6's prices depend on it.</summary>
    public static ModelPricing PricingOf(string model, int promptTokens = 0) => GetPricing(model, promptTokens);

    /// <summary>Claude Haiku 5.5 bills every token of a request whose prompt (cache reads and writes included) is over
    /// this at its higher prices.</summary>
    public const int HaikuTierPromptTokens = 100_000;

    /// <summary>GPT-6 (Astra, 6.1 Sol, 6 Sol, Luna) bills a request whose prompt is over this at 2x input / cache and
    /// 1.5x output — every token of it, not just the part past the line.</summary>
    public const int Gpt6LongContextPromptTokens = 272_000;

    private static ModelPricing GetPricing(string model, int promptTokens = 0)
    {
        string m = model.ToLowerInvariant();
        ModelPricing Gpt6(ModelPricing p) => promptTokens > Gpt6LongContextPromptTokens
            ? new(p.InputPerMToken * 2m, p.OutputPerMToken * 1.5m, p.CacheReadPerMToken * 2m, p.CacheWritePerMToken * 2m)
            : p;
        // Anthropic rows checked against platform.claude.com/docs/en/about-claude/pricing on 2026-10-13. Cache reads are
        // 0.1x input except Fable/Mythos 5.1 (0.025x) and Opus 5.5 / Sonnet 5.5 (0.05x); 5-minute writes 1.25x input,
        // 1-hour writes 2x input (RecordUsage prices those from the input column).
        return m switch
        {
            // ── Anthropic Models ── (ModelPricing = input, output, cacheRead, cacheWrite per MTok)
            // Fable 5.1 / Mythos 5.1: $10 / $50, cache reads $0.25 (0.025x)
            _ when m.Contains("fable-5-1") || m.Contains("mythos-5-1") => new(10.0m, 50.0m, 0.25m, 12.5m),
            // Fable 5 / Mythos 5: $10 in / $50 out (most capable, above Opus tier)
            _ when m.Contains("fable") || m.Contains("mythos") => new(10.0m, 50.0m, 1.0m, 12.5m),
            // Opus 5.5: $4 / $20, cache reads $0.20 (0.05x)
            _ when m.Contains("opus-5-5") => new(4.0m, 20.0m, 0.2m, 5.0m),
            // Opus 5 / 4.8 / 4.7 / 4.6 / 4.5: $5 in / $25 out (1M ctx, no long-context premium)
            _ when m.Contains("opus-5") || m.Contains("opus-4-8") || m.Contains("opus-4-7") || m.Contains("opus-4-6")
                || m.Contains("opus-4-5") => new(5.0m, 25.0m, 0.5m, 6.25m),
            // Opus 4 / 4.1 (retired except on Bedrock / Google Cloud): $15 in / $75 out
            _ when m.Contains("opus") => new(15.0m, 75.0m, 1.5m, 18.75m),
            // Sonnet 5.5: $2 / $10, cache reads $0.10 (0.05x) — it was $0.20 here, twice the bill for every cached step
            _ when m.Contains("sonnet-5-5") => new(2.0m, 10.0m, 0.1m, 2.5m),
            // Sonnet 5: $2 / $10 (the launch price is now the standard price), cache reads $0.20
            _ when m.Contains("sonnet-5") => new(2.0m, 10.0m, 0.2m, 2.5m),
            // Sonnet 4/4.5/4.6: $3 input, $15 output
            _ when m.Contains("sonnet") => new(3.0m, 15.0m, 0.3m, 3.75m),
            // Haiku 5.5: $0.10 / $0.50 for a prompt up to 100K tokens; $0.50 / $2.50 for every token of a longer one
            _ when m.Contains("haiku-5") => promptTokens > HaikuTierPromptTokens
                ? new(0.50m, 2.50m, 0.05m, 0.625m)
                : new(0.10m, 0.50m, 0.01m, 0.125m),
            // Haiku 4.5: $1 input, $5 output
            _ when m.Contains("haiku-4") => new(1.0m, 5.0m, 0.1m, 1.25m),
            // Haiku 3.5 ("claude-3-5-haiku-…", retired except on Google Cloud): $0.80 / $4
            _ when m.Contains("3-5-haiku") || m.Contains("haiku-3-5") => new(0.80m, 4.0m, 0.08m, 1.0m),
            // Haiku 3 (legacy): $0.25 input, $1.25 output
            _ when m.Contains("haiku") => new(0.25m, 1.25m, 0.025m, 0.3m),

            // ── OpenAI Models ── (October 2026, developers.openai.com/api/docs/pricing; cached input in the cacheRead
            // column, cache writes — billed from GPT-6 on, 1.25x input — in cacheWrite). GPT-6: a prompt over 272K input
            // tokens bills the whole request at 2x input/cache and 1.5x output.
            _ when m.Contains("gpt-6-astra") => Gpt6(new(10.0m, 50.0m, 1.0m, 12.5m)),
            _ when m.Contains("gpt-6.1-sol") => Gpt6(new(2.0m, 10.0m, 0.10m, 2.5m)),
            _ when m.Contains("gpt-6-sol") => Gpt6(new(2.0m, 10.0m, 0.20m, 2.5m)),
            _ when m.Contains("gpt-6-luna") => Gpt6(new(0.10m, 0.50m, 0.01m, 0.125m)),
            _ when m.Contains("gpt-5.6-sol") => new(4.0m, 20.0m, 0.40m, 0m),
            _ when m.Contains("gpt-5.6-terra") => new(2.0m, 12.0m, 0.20m, 0m),
            _ when m.Contains("gpt-5.6-luna") => new(0.20m, 1.20m, 0.02m, 0m),
            _ when m.Contains("gpt-5.5") => new(5.0m, 30.0m, 0.50m, 0m),
            _ when m.Contains("gpt-5.4-mini") => new(0.75m, 4.50m, 0.075m, 0m),
            _ when m.Contains("gpt-4.1") => new(2.0m, 8.0m, 0.50m, 0m),
            _ when m.Contains("gpt-5-nano") => new(0.05m, 0.40m, 0m, 0m),
            _ when m.Contains("gpt-5-mini") => new(0.25m, 2.0m, 0m, 0m),
            _ when m.Contains("gpt-5") => new(1.25m, 10.0m, 0m, 0m),
            _ when m.Contains("o4-mini") => new(1.10m, 4.40m, 0m, 0m),
            _ when m.Contains("o4") => new(10.0m, 40.0m, 0m, 0m),
            _ when m.Contains("o3-pro") => new(20.0m, 80.0m, 0m, 0m),
            _ when m.Contains("gpt-4o-mini") => new(0.15m, 0.6m, 0m, 0m),
            _ when m.Contains("gpt-4o") => new(2.5m, 10.0m, 0m, 0m),
            _ when m.Contains("o4-mini") => new(1.10m, 4.40m, 0m, 0m),
            _ when m.Contains("o3-mini") => new(1.10m, 4.40m, 0m, 0m),
            _ when m.Contains("o3") => new(10.0m, 40.0m, 0m, 0m),
            _ when m.Contains("o1-pro") => new(150.0m, 600.0m, 0m, 0m),
            _ when m.Contains("o1") => new(15.0m, 60.0m, 0m, 0m),

            // ── Google Gemini Models ── (paid tier, October 2026; 3.6–3.8 Flash promo price until 2026-12-31,
            // 4 Argon's until 2026-11-30, then double)
            _ when m.Contains("gemini-4-argon") => new(2.0m, 10.0m, 0.10m, 0m),
            _ when m.Contains("gemini-3.8-flash") || m.Contains("gemini-3.7-flash") || m.Contains("gemini-3.6-flash")
                => new(0.75m, 3.75m, 0.075m, 0m),
            _ when m.Contains("gemini-3.5-flash-lite") => new(0.30m, 2.50m, 0.03m, 0m),
            _ when m.Contains("gemini-3.5-flash") => new(1.50m, 9.0m, 0.15m, 0m),
            _ when m.Contains("gemini-3.1-flash-lite") => new(0.25m, 1.50m, 0.025m, 0m),
            _ when m.Contains("gemini-3.1-pro") => new(2.0m, 12.0m, 0.20m, 0m),
            _ when m.Contains("gemini-2.5-flash-lite") => new(0.10m, 0.40m, 0m, 0m),
            _ when m.Contains("gemini-3-pro") => new(2.0m, 12.0m, 0m, 0m),
            _ when m.Contains("gemini-3-flash") => new(0.50m, 3.0m, 0.05m, 0m),
            _ when m.Contains("gemini-2.5-pro") => new(1.25m, 10.0m, 0m, 0m),
            _ when m.Contains("gemini-2.5-flash") => new(0.30m, 2.50m, 0m, 0m),
            _ when m.Contains("gemini-2.0") => new(0.10m, 0.40m, 0m, 0m),
            _ when m.Contains("gemma") => new(0m, 0m, 0m, 0m), // Open-weight, free via API

            // ── Local / Ollama (free) ──
            _ when m.Contains("ollama") || m.Contains("local") || m.Contains("gguf")
                => new(0m, 0m, 0m, 0m),

            // Default to Sonnet pricing for unknown models
            _ => new(3.0m, 15.0m, 0.3m, 3.75m),
        };
    }
}

public record ModelPricing(decimal InputPerMToken, decimal OutputPerMToken,
    decimal CacheReadPerMToken, decimal CacheWritePerMToken);

/// <summary>One day of the ledger. The baseline and the saved-by parts are 0 on days recorded before they existed.</summary>
public class DailyUsage
{
    public string? EstimateMethod { get; set; }
    public decimal Usd { get; set; }
    public int Requests { get; set; }
    /// <summary>What the same work would have cost with no saving (<see cref="CostTrackingService.Price"/>).</summary>
    public decimal BaselineUsd { get; set; }
    public decimal SavedCacheUsd { get; set; }
    public decimal SavedToolSearchUsd { get; set; }
    public decimal SavedCompactionUsd { get; set; }
    public decimal SavedBatchUsd { get; set; }
    /// <summary>Prompt tokens sent at full or cache-write price, read from the cache, and generated.</summary>
    public long InputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long OutputTokens { get; set; }
    /// <summary>The same day split by provider (<see cref="CostTrackingService.ProviderOf"/>). Null on days recorded before it.</summary>
    public Dictionary<string, DailyUsage>? ByProvider { get; set; }
    /// <summary>Finished agent runs by dispatch strategy ("single", "fan-out", "workers", "speculative-fix"). Null before any.</summary>
    public Dictionary<string, StrategyUsage>? ByStrategy { get; set; }
}

/// <summary>Agent runs of one dispatch strategy on one day.</summary>
public class StrategyUsage
{
    public int Runs { get; set; }
    public int Successes { get; set; }
    public decimal Usd { get; set; }
    public double Seconds { get; set; }
}

public class ModelUsage
{
    public string Model { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheCreationTokens { get; set; }
    public int Requests { get; set; }
    public decimal CostUsd { get; set; }
}

public class CostSummary
{
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public int TotalCacheReadTokens { get; set; }
    public int TotalCacheCreationTokens { get; set; }
    public int TotalRequests { get; set; }
    public decimal TotalCostUsd { get; set; }
    public List<ModelUsage> ModelUsage { get; set; } = new();
}
