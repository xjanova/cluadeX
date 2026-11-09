namespace CluadeX.Services.Agents;

/// <summary>
/// How many sub-agents use one local server at once. A server on this PC answers a fixed number of requests together
/// (colibri: its KV slots, 1 unless started with more) and queues the rest — colibri's queue holds 8 for at most 300 s,
/// then answers 429 queue_timeout. Five parallel agents on a 1-slot server mostly waited and then failed. A sub-agent
/// takes a place here before it is routed to the local server; when none is free it goes to the API instead of the
/// queue. Keyed by the server's address (<c>LocalKeyOf</c>). Thread-safe.
/// </summary>
public static class LocalSlots
{
    private static readonly Dictionary<string, int> Busy = new(StringComparer.Ordinal);
    private static readonly object Lock = new();

    /// <summary>A place on <paramref name="key"/> when fewer than <paramref name="capacity"/> are taken (0 or less = no
    /// limit: it is still counted). Null = full. Disposing the lease gives the place back (once).</summary>
    public static IDisposable? TryTake(string key, int capacity)
    {
        lock (Lock)
        {
            int n = Busy.GetValueOrDefault(key);
            if (capacity > 0 && n >= capacity) return null;
            Busy[key] = n + 1;
        }
        return new Lease(key);
    }

    public static int InUse(string key)
    {
        lock (Lock) return Busy.GetValueOrDefault(key);
    }

    private sealed class Lease(string key) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (Lock)
            {
                int n = Busy.GetValueOrDefault(key) - 1;
                if (n <= 0) Busy.Remove(key); else Busy[key] = n;
            }
        }
    }
}
