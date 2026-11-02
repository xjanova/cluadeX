namespace CluadeX.Services.Billing;

/// <summary>What the Billing page asks for: "now", the time zone whose calendar days count, how many days the chart
/// shows, and the first day of the week.</summary>
public sealed record BillingQuery(DateTimeOffset Now, TimeZoneInfo Zone, int ChartDays = 30, DayOfWeek WeekStart = DayOfWeek.Monday,
    int TopChats = 8);

/// <summary>Spend split by how it was arrived at. <see cref="Total"/> = billed + estimated (free rows are $0).</summary>
public readonly record struct Spend(decimal Billed, decimal Estimated)
{
    public decimal Total => Billed + Estimated;
    public static Spend operator +(Spend a, Spend b) => new(a.Billed + b.Billed, a.Estimated + b.Estimated);
}

public sealed class TokenTotals
{
    public long Input, Output, CacheRead, CacheWrite, Thinking;
    public int Requests;
    public double Seconds;
    public long All => Input + Output + CacheRead + CacheWrite;

    internal void Add(UsageEntry e)
    {
        Input += e.Input; Output += e.Output; CacheRead += e.CacheRead; CacheWrite += e.CacheWrite; Thinking += e.Thinking;
        Requests += e.Requests; Seconds += e.Seconds;
    }
}

/// <summary>One calendar period (today, this week …): spend, requests, tokens.</summary>
public sealed class PeriodTotal
{
    public Spend Spend;
    public readonly TokenTotals Tokens = new();
    public bool HasUse => Tokens.Requests > 0 || Tokens.All > 0 || Spend.Total != 0;
}

/// <summary>One local calendar day of the chart.</summary>
public sealed class DayPoint
{
    public DateTime Day { get; init; }
    public Spend Spend;
    public long Tokens;
    public int Requests;
    public double Seconds;
    /// <summary>Spend from the first chart day through this one.</summary>
    public decimal CumulativeUsd;
    public long CumulativeTokens;
    /// <summary>The All tab's bar split by provider (empty on a provider tab).</summary>
    public readonly Dictionary<string, decimal> UsdByProvider = new(StringComparer.Ordinal);
    public readonly Dictionary<string, long> TokensByProvider = new(StringComparer.Ordinal);
}

public sealed class ModelRow
{
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public Spend Spend;
    public readonly TokenTotals Tokens = new();
}

public sealed class ChatRow
{
    public string? ChatId { get; init; }
    public string Title { get; set; } = "";
    public Spend Spend;
    public decimal SubAgentUsd;
    public readonly TokenTotals Tokens = new();
    public readonly HashSet<string> Providers = new(StringComparer.Ordinal);
}

/// <summary>One tab of the Billing page.</summary>
public sealed class ProviderReport
{
    public string Key { get; init; } = "";
    public readonly PeriodTotal Today = new(), Week = new(), Month = new(), AllTime = new(), Range = new();
    public List<DayPoint> Days { get; } = new();
    public List<ModelRow> Models { get; } = new();
    public List<ChatRow> Chats { get; } = new();
    public DateTime? FirstUse { get; set; }
    public DateTime? LastUse { get; set; }
    public bool HasUse => AllTime.HasUse;
}

public sealed class BillingSnapshot
{
    public BillingQuery Query { get; init; } = null!;
    public DateTime Today { get; init; }
    public DateTime WeekStart { get; init; }
    public DateTime MonthStart { get; init; }
    public DateTime RangeStart { get; init; }
    /// <summary>"All" first, then every provider with recorded use, in <see cref="ProviderBrand.All"/> order.</summary>
    public List<ProviderReport> Reports { get; } = new();
    public ProviderReport? Get(string key) => Reports.FirstOrDefault(r => r.Key == key);
    public bool IsEmpty => !Reports[0].HasUse;
}

/// <summary>
/// Turns usage rows into the Billing page: per provider (plus "All"), per model, per local day, billed vs estimated.
/// Pure — no clock, no files, no UI — so the harness proves it on fixture rows. A row's day is its UTC moment seen in
/// <see cref="BillingQuery.Zone"/>; today / this week / this month are calendar periods in that zone.
/// </summary>
public static class UsageAggregator
{
    public static BillingSnapshot Build(IReadOnlyList<UsageEntry> rows, BillingQuery q, Func<string, string?>? chatTitle = null)
    {
        int chartDays = Math.Clamp(q.ChartDays, 1, 366);
        var nowLocal = TimeZoneInfo.ConvertTime(q.Now, q.Zone);
        var today = nowLocal.Date;
        int back = ((int)today.DayOfWeek - (int)q.WeekStart + 7) % 7;
        var snap = new BillingSnapshot
        {
            Query = q,
            Today = today,
            WeekStart = today.AddDays(-back),
            MonthStart = new DateTime(today.Year, today.Month, 1),
            RangeStart = today.AddDays(1 - chartDays),
        };

        var all = NewReport(ProviderBrand.AllKey, snap, chartDays);
        var byProvider = new Dictionary<string, ProviderReport>(StringComparer.Ordinal);
        var modelIndex = new Dictionary<(string, string, string), ModelRow>();
        var chatIndex = new Dictionary<(string, string), ChatRow>();

        foreach (var e in rows)
        {
            if (e == null) continue;
            string provider = string.IsNullOrWhiteSpace(e.Provider) ? "Other" : e.Provider;
            if (!byProvider.TryGetValue(provider, out var rep))
                byProvider[provider] = rep = NewReport(provider, snap, chartDays);
            var day = TimeZoneInfo.ConvertTime(e.At, q.Zone).Date;
            var spend = SpendOf(e);
            foreach (var r in new[] { all, rep })
            {
                Count(r.AllTime, e, spend);
                if (day == today) Count(r.Today, e, spend);
                if (day >= snap.WeekStart && day <= today) Count(r.Week, e, spend);
                if (day >= snap.MonthStart && day <= today) Count(r.Month, e, spend);
                if (r.FirstUse == null || day < r.FirstUse) r.FirstUse = day;
                if (r.LastUse == null || day > r.LastUse) r.LastUse = day;
                if (day < snap.RangeStart || day > today) continue;   // the rest is the chart range only
                Count(r.Range, e, spend);
                var point = r.Days[(day - snap.RangeStart).Days];
                point.Spend += spend; point.Tokens += e.Tokens; point.Requests += e.Requests; point.Seconds += e.Seconds;
                if (ReferenceEquals(r, all))
                {
                    point.UsdByProvider[provider] = point.UsdByProvider.GetValueOrDefault(provider) + spend.Total;
                    point.TokensByProvider[provider] = point.TokensByProvider.GetValueOrDefault(provider) + e.Tokens;
                }
                var mk = (r.Key, provider, e.Model ?? "");
                if (!modelIndex.TryGetValue(mk, out var m))
                    r.Models.Add(modelIndex[mk] = m = new ModelRow { Provider = provider, Model = e.Model ?? "" });
                m.Spend += spend; m.Tokens.Add(e);
                if (e.Chat == null) continue;
                var ck = (r.Key, e.Chat);
                if (!chatIndex.TryGetValue(ck, out var c))
                    r.Chats.Add(chatIndex[ck] = c = new ChatRow { ChatId = e.Chat });
                c.Spend += spend; c.Tokens.Add(e); c.Providers.Add(provider);
                if (e.SubAgent) c.SubAgentUsd += spend.Total;
            }
        }

        snap.Reports.Add(all);
        foreach (var b in ProviderBrand.All)
            if (byProvider.Remove(b.Key, out var r)) snap.Reports.Add(r);
        foreach (var r in byProvider.Values.OrderBy(r => r.Key, StringComparer.Ordinal)) snap.Reports.Add(r);   // unknown keys

        foreach (var r in snap.Reports)
        {
            decimal usd = 0; long tok = 0;
            foreach (var d in r.Days) { usd += d.Spend.Total; tok += d.Tokens; d.CumulativeUsd = usd; d.CumulativeTokens = tok; }
            r.Models.Sort((a, b) => b.Spend.Total != a.Spend.Total ? b.Spend.Total.CompareTo(a.Spend.Total)
                : b.Tokens.All != a.Tokens.All ? b.Tokens.All.CompareTo(a.Tokens.All) : string.CompareOrdinal(a.Model, b.Model));
            r.Chats.Sort((a, b) => b.Spend.Total != a.Spend.Total ? b.Spend.Total.CompareTo(a.Spend.Total)
                : b.Tokens.All != a.Tokens.All ? b.Tokens.All.CompareTo(a.Tokens.All) : string.CompareOrdinal(a.ChatId, b.ChatId));
            if (r.Chats.Count > q.TopChats) r.Chats.RemoveRange(q.TopChats, r.Chats.Count - q.TopChats);
            foreach (var c in r.Chats) c.Title = chatTitle?.Invoke(c.ChatId!) ?? "";
        }
        return snap;
    }

    /// <summary>A row's dollars under its kind: billed, an API-price estimate, or nothing (local).</summary>
    public static Spend SpendOf(UsageEntry e) => e.Kind switch
    {
        UsageCostKind.Billed => new Spend(e.Usd, 0),
        UsageCostKind.Estimated => new Spend(0, e.Usd),
        _ => default,
    };

    private static ProviderReport NewReport(string key, BillingSnapshot snap, int days)
    {
        var r = new ProviderReport { Key = key };
        for (int i = 0; i < days; i++) r.Days.Add(new DayPoint { Day = snap.RangeStart.AddDays(i) });
        return r;
    }

    private static void Count(PeriodTotal p, UsageEntry e, Spend s) { p.Spend += s; p.Tokens.Add(e); }
}
