using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace CluadeX.Services;

/// <summary>Rate-limit headroom one response reported, from the provider's response headers.</summary>
/// <remarks>Limits are per minute. A null field = the provider didn't send that header.</remarks>
public sealed record RateLimitSnapshot(
    string Provider,
    string Model,
    long? TokensLimit, long? TokensRemaining,
    long? InputTokensLimit, long? InputTokensRemaining,
    long? OutputTokensLimit, long? OutputTokensRemaining,
    long? RequestsLimit, long? RequestsRemaining,
    DateTimeOffset? Reset,
    DateTime At)
{
    /// <summary>Headroom left, 0–100, from the tightest bucket the provider reported; null if none.</summary>
    public double? PercentLeft
    {
        get
        {
            double? best = null;
            foreach (var (left, limit) in new[]
                     {
                         (TokensRemaining, TokensLimit), (InputTokensRemaining, InputTokensLimit),
                         (OutputTokensRemaining, OutputTokensLimit), (RequestsRemaining, RequestsLimit),
                     })
            {
                if (left is not { } l || limit is not { } m || m <= 0) continue;
                double pct = Math.Clamp(100.0 * l / m, 0, 100);
                best = best is null ? pct : Math.Min(best.Value, pct);
            }
            return best;
        }
    }
}

/// <summary>
/// Where providers drop the rate-limit headers of each response, and where the chat's usage strip reads
/// them. Static so providers built by AiProviderManager don't need another constructor argument.
/// Note what this is NOT: an account's prepaid credit balance — no provider exposes that to a normal API
/// key (Anthropic and OpenAI need an Admin key), so the strip links to the billing page for it instead.
/// </summary>
public static class UsageMeter
{
    public static event Action<RateLimitSnapshot>? Updated;
    public static RateLimitSnapshot? Latest { get; private set; }

    public static void Report(RateLimitSnapshot? snapshot)
    {
        if (snapshot == null) return;
        Latest = snapshot;
        Updated?.Invoke(snapshot);
    }

    /// <summary>anthropic-ratelimit-{tokens,input-tokens,output-tokens,requests}-{limit,remaining,reset}.</summary>
    public static RateLimitSnapshot? FromAnthropic(HttpResponseMessage response, string model)
    {
        long? N(string name) => Long(response, "anthropic-ratelimit-" + name);
        var snap = new RateLimitSnapshot("Anthropic", model,
            N("tokens-limit"), N("tokens-remaining"),
            N("input-tokens-limit"), N("input-tokens-remaining"),
            N("output-tokens-limit"), N("output-tokens-remaining"),
            N("requests-limit"), N("requests-remaining"),
            Date(response, "anthropic-ratelimit-tokens-reset") ?? Date(response, "anthropic-ratelimit-input-tokens-reset")
                ?? Date(response, "anthropic-ratelimit-requests-reset"),
            DateTime.Now);
        return snap.PercentLeft == null ? null : snap;
    }

    /// <summary>x-ratelimit-{limit,remaining}-{tokens,requests}, x-ratelimit-reset-tokens ("6m0s").</summary>
    public static RateLimitSnapshot? FromOpenAi(HttpResponseMessage response, string model)
    {
        long? N(string name) => Long(response, "x-ratelimit-" + name);
        TimeSpan? resetIn = Duration(Header(response, "x-ratelimit-reset-tokens") ?? Header(response, "x-ratelimit-reset-requests"));
        var snap = new RateLimitSnapshot("OpenAI", model,
            N("limit-tokens"), N("remaining-tokens"),
            null, null, null, null,
            N("limit-requests"), N("remaining-requests"),
            resetIn is { } d ? DateTimeOffset.Now + d : null,
            DateTime.Now);
        return snap.PercentLeft == null ? null : snap;
    }

    private static string? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    private static long? Long(HttpResponseMessage r, string name) =>
        long.TryParse(Header(r, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static DateTimeOffset? Date(HttpResponseMessage r, string name) =>
        DateTimeOffset.TryParse(Header(r, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>OpenAI's "1s", "6m0s", "1h2m3.5s", "250ms".</summary>
    internal static TimeSpan? Duration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = Regex.Matches(text, @"(\d+(?:\.\d+)?)(ms|h|m|s)");
        if (parts.Count == 0) return null;
        double seconds = 0;
        foreach (Match p in parts)
        {
            double v = double.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture);
            seconds += p.Groups[2].Value switch { "h" => v * 3600, "m" => v * 60, "ms" => v / 1000, _ => v };
        }
        return TimeSpan.FromSeconds(seconds);
    }
}
