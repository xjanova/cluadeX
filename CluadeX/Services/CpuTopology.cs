using System.Runtime.InteropServices;

namespace CluadeX.Services;

/// <summary>Physical CPU cores (not SMT threads): what llama-server's -t should be. Environment.ProcessorCount counts
/// logical processors — 12 on a 6-core i5-11400 — and two threads per core only fight over the memory bandwidth
/// decoding is bound by. Asked of Windows directly (GetLogicalProcessorInformationEx: one record per physical core,
/// every processor group), once per run of the app. WMI is not used: it answered 0 from a process where COM was not
/// ready for it, while this call cannot.</summary>
public static class CpuTopology
{
    private static readonly Lazy<int> _physical = new(Read, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Physical cores of every CPU package added up; 0 when Windows would not say.</summary>
    public static int PhysicalCores => _physical.Value;

    private const int RelationProcessorCore = 0;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    private static int Read()
    {
        try
        {
            uint len = 0;
            if (GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len)
                || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || len == 0 || len > 16 << 20)
                return 0;
            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len)) return 0;
                // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship (int32), Size (uint32), then the payload.
                int cores = 0;
                for (uint off = 0; off + 8 <= len;)
                {
                    int relation = Marshal.ReadInt32(buf, (int)off);
                    uint size = (uint)Marshal.ReadInt32(buf, (int)off + 4);
                    if (size == 0) break;
                    if (relation == RelationProcessorCore) cores++;
                    off += size;
                }
                return cores is > 0 and <= 4096 ? cores : 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch
        {
            return 0;
        }
    }
}
