using System.Runtime.InteropServices;

namespace CluadeX.Services.Agents;

/// <summary>
/// How many sub-agents this machine runs at once, across every chat (owner 2026-11-01: "กระจายทรัพยากรเครื่องอย่างเหมาะสม").
/// MaxParallelAgents is per run: four chats at 3 each made 12, whatever the machine had left. Each sub-agent holds a slot
/// here while it runs; the limit is read again from the free memory at every start, so a machine that fills up takes fewer.
/// </summary>
public static class AgentLoad
{
    /// <summary>What a sub-agent that builds or tests in its own worktree may take, and what stays for the user's own work.</summary>
    public const long RamPerAgentMb = 1500, RamReservedMb = 4000;

    private static int _running;

    /// <summary>Sub-agents running now in this app.</summary>
    public static int Running => Volatile.Read(ref _running);

    /// <summary>The limit for a machine: half its logical processors, and what free memory can hold past the reserve. At
    /// least one, so a full machine slows down instead of refusing all work. Pure.</summary>
    public static int Cap(int logicalProcessors, long freeRamMb)
    {
        long byRam = (freeRamMb - RamReservedMb) / RamPerAgentMb;
        return (int)Math.Clamp(Math.Min(logicalProcessors / 2, byRam), 1, DispatchPolicy.MaxParallelCap);
    }

    public static int CurrentCap() => Cap(Environment.ProcessorCount, FreeRamMb());

    /// <summary>Take a slot unless the limit is reached. Each true needs one <see cref="Leave"/>.</summary>
    public static bool TryEnter(int cap)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref _running);
            if (seen >= cap) return false;
        }
        while (Interlocked.CompareExchange(ref _running, seen + 1, seen) != seen);
        return true;
    }

    public static void Leave() => Interlocked.Decrement(ref _running);

    /// <summary>Wait for a slot (native sub-agents queue; a CLI's spawn is refused instead, the CLI cannot be held).</summary>
    public static async Task EnterAsync(CancellationToken ct)
    {
        while (!TryEnter(CurrentCap())) await Task.Delay(500, ct);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// <summary>Free physical memory. Unknown = the machine's size, so a failed reading does not throttle anything.</summary>
    public static long FreeRamMb()
    {
        try
        {
            var st = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref st)) return (long)(st.AvailPhys / (1024 * 1024));
        }
        catch { /* not Windows */ }
        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
    }
}
