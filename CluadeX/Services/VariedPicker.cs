namespace CluadeX.Services;

/// <summary>
/// Picks one name from a list with variety (owner 2026-11-10: "idle อวาต้า น้อยไป"): never the one just picked, and not one
/// of the last few either — up to half the list, at most <see cref="MaxMemory"/> — so with 40 idle gestures she goes
/// through a dozen different ones before any comes back. The list may change between calls (lunch and night add a few
/// idle extras); what was picked lately is remembered by name, so that works. Pure and seeded for tests.
/// </summary>
public sealed class VariedPicker
{
    /// <summary>The most picks remembered (and kept from coming back).</summary>
    public const int MaxMemory = 12;

    private readonly Random _rng;
    private readonly List<string> _recent = new(MaxMemory + 1);

    public VariedPicker(Random? rng = null) => _rng = rng ?? new Random();

    /// <summary>The last picks, oldest first (tests).</summary>
    public IReadOnlyList<string> Recent => _recent;

    /// <summary>How many recent picks are kept from coming back for a list of <paramref name="count"/>: half of it, so
    /// there is always a choice left (2 → they alternate, 1 → it repeats, as it must).</summary>
    public static int MemoryFor(int count) => Math.Clamp(count / 2, 0, MaxMemory);

    /// <summary>One of <paramref name="pool"/> that <paramref name="usable"/> allows (all when null), never
    /// <paramref name="not"/> while there is another; null when none is usable.</summary>
    public string? Next(IReadOnlyList<string> pool, Func<string, bool>? usable = null, string? not = null)
    {
        var ok = new List<string>(pool.Count);
        foreach (var n in pool)
            if ((usable == null || usable(n)) && !ok.Contains(n)) ok.Add(n);
        if (ok.Count == 0) return null;

        int memory = MemoryFor(ok.Count);
        int from = Math.Max(0, _recent.Count - memory);
        var fresh = new List<string>(ok.Count);
        foreach (var n in ok)
            if (n != not && _recent.IndexOf(n, from) < 0) fresh.Add(n);
        if (fresh.Count == 0)
            foreach (var n in ok)
                if (n != not && (_recent.Count == 0 || _recent[^1] != n)) fresh.Add(n);
        var choice = fresh.Count > 0 ? fresh : ok;
        string pick = choice[_rng.Next(choice.Count)];

        _recent.Add(pick);
        if (_recent.Count > MaxMemory) _recent.RemoveAt(0);
        return pick;
    }
}
