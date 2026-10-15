using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.Helpers;

/// <summary>
/// Tells a page's own settings write apart from changes made while it ran. A page ignores the SettingsChanged event
/// of its own save (it already shows those values) — but SettingsChanged handlers run inside that save, and one of
/// them may write in turn (leaving the DGX Spark gives the context window back), raising its event while the page
/// is still saving. Ignoring every event during the save dropped that one, and the page kept showing the old value.
/// <para>
/// Usage: the page's SettingsChanged handler calls <see cref="Observe"/> first and returns when it gives true (the
/// event is counted, not handled); the page writes through <see cref="Run"/>, which says whether anyone else wrote
/// during its save — then the page reloads from the settings itself.
/// </para>
/// </summary>
public sealed class OwnSettingsWrite
{
    private int _active;
    private int _events;

    /// <summary>A write through <see cref="Run"/> is in progress.</summary>
    public bool IsActive => Volatile.Read(ref _active) > 0;

    /// <summary>Call from the SettingsChanged handler. True = raised during an own write: counted here, so the
    /// handler does nothing now (<see cref="Run"/>'s result covers it).</summary>
    public bool Observe()
    {
        if (Volatile.Read(ref _active) == 0) return false;
        Interlocked.Increment(ref _events);
        return true;
    }

    /// <summary>Apply <paramref name="update"/> and save. <c>OthersChanged</c> is true when more SettingsChanged
    /// events arrived during the save than the save's own one (none when the save failed) — something else changed
    /// the settings meanwhile, so what the page shows may be stale.</summary>
    public (bool Ok, bool OthersChanged) Run(SettingsService settings, Action<AppSettings> update)
    {
        Interlocked.Increment(ref _active);
        int start = Volatile.Read(ref _events);
        bool ok;
        try { ok = settings.UpdateSettings(update); }
        finally { Interlocked.Decrement(ref _active); }
        int seen = Volatile.Read(ref _events) - start;
        return (ok, seen > (ok ? 1 : 0));
    }
}
