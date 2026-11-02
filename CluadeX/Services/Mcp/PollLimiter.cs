namespace CluadeX.Services.Mcp;

/// <summary>
/// A token bucket per key for the pipe's status calls (cluadex.task_status, cluadex.list_chats). A supervisor now polls
/// them through a long run instead of holding write_code open; a loop that polls without a pause must not keep the UI
/// thread and the chat database busy for the owner. <see cref="Burst"/> calls at once, then <see cref="PerSecond"/> a
/// second — far above what a supervisor needs (one call every few seconds). Thread-safe; the clock is injectable so the
/// harness checks it without waiting.
/// </summary>
public sealed class PollLimiter
{
    public int Burst { get; }
    public double PerSecond { get; }
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, (double Tokens, DateTime At)> _buckets = new(StringComparer.Ordinal);

    public PollLimiter(int burst = 10, double perSecond = 2, Func<DateTime>? now = null)
    {
        Burst = Math.Max(1, burst);
        PerSecond = perSecond > 0 ? perSecond : 1;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Takes one call for <paramref name="key"/>: null = go ahead; otherwise how long until the next one may.</summary>
    public TimeSpan? Take(string key)
    {
        lock (_buckets)
        {
            var now = _now();
            var (tokens, at) = _buckets.TryGetValue(key, out var b) ? b : (Burst, now);
            tokens = Math.Min(Burst, tokens + Math.Max(0, (now - at).TotalSeconds) * PerSecond);
            if (tokens >= 1)
            {
                _buckets[key] = (tokens - 1, now);
                return null;
            }
            _buckets[key] = (tokens, now);
            return TimeSpan.FromSeconds((1 - tokens) / PerSecond);
        }
    }
}
