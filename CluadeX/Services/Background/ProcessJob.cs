using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CluadeX.Services.Background;

/// <summary>
/// A Windows Job Object that owns one background process and everything it spawns.
///
/// Why: <c>Process.Kill(entireProcessTree: true)</c> walks the parent→child links that exist at the moment it is
/// called, so a grandchild whose parent already exited (a <c>nohup … &amp;</c>, an npm script that forks a server,
/// <c>Start-Process</c>) is never found and keeps its port. A job contains every descendant no matter who its
/// parent is: <see cref="Terminate"/> ends them all. The job is created with KILL_ON_JOB_CLOSE, and its handle
/// lives only in CluadeX (it is not inheritable), so when CluadeX exits, crashes or is killed from Task Manager
/// the OS closes the handle and kills every tree it held.
///
/// CluadeX itself is NEVER put in a job: the Velopack updater and games the user launches must outlive it.
///
/// Known follow-up: <see cref="Process.Start()"/> followed by <see cref="Assign"/> leaves a window of microseconds in
/// which the new shell could spawn a child before it is in the job. Closing it needs CreateProcessW with
/// PROC_THREAD_ATTRIBUTE_JOB_LIST (the process is created inside the job); not done yet — a shell needs far longer
/// than that window to start anything.
/// </summary>
public sealed class ProcessJob : IDisposable
{
    private readonly SafeJobHandle _handle;
    private int _disposed;

    private ProcessJob(SafeJobHandle handle) => _handle = handle;

    /// <summary>The job could be created on this OS (Windows only).</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// A new kill-on-close job, or null with the reason when the OS refuses (not Windows, out of handles).
    /// Never throws.
    /// </summary>
    public static ProcessJob? TryCreate(out string? error)
    {
        error = null;
        if (!IsSupported) { error = "job objects need Windows"; return null; }
        try
        {
            var handle = CreateJobObjectW(IntPtr.Zero, null);
            if (handle.IsInvalid)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                handle.Dispose();
                return null;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;
            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
                {
                    error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    handle.Dispose();
                    return null;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }

            return new ProcessJob(handle);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Put <paramref name="process"/> (and so everything it starts from now on) in this job. False when
    /// the OS refused (the process already exited, or it is in a job that forbids nesting).</summary>
    public bool Assign(Process process)
    {
        if (_disposed != 0) return false;
        try { return AssignProcessToJobObject(_handle, process.Handle); }
        catch { return false; }
    }

    /// <summary>Put the process <paramref name="pid"/> in this job after the fact (a descendant that was created
    /// outside it — see <see cref="ProcessTree"/>). False when the OS refuses (access denied, already gone).</summary>
    public bool Assign(int pid)
    {
        if (_disposed != 0) return false;
        IntPtr h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_TERMINATE, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return AssignProcessToJobObject(_handle, h); }
        catch { return false; }
        finally { CloseHandle(h); }
    }

    /// <summary>The process <paramref name="pid"/> is in this job (false when unknown).</summary>
    public bool Contains(int pid)
    {
        if (_disposed != 0) return false;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return IsProcessInJob(h, _handle, out bool inJob) && inJob; }
        catch { return false; }
        finally { CloseHandle(h); }
    }

    /// <summary>End every process in the job, whoever its parent is. Safe to call more than once.</summary>
    public bool Terminate(uint exitCode = 1)
    {
        if (_disposed != 0) return false;
        try { return TerminateJobObject(_handle, exitCode); }
        catch { return false; }
    }

    /// <summary>How many processes are alive in the job right now (0 when unknown or disposed).</summary>
    public int ActiveProcesses
    {
        get
        {
            if (_disposed != 0) return 0;
            try
            {
                int size = Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>();
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, buffer, (uint)size, out _))
                        return 0;
                    return (int)Marshal.PtrToStructure<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(buffer).ActiveProcesses;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch { return 0; }
        }
    }

    /// <summary>The ids of the processes in the job right now (empty when unknown).</summary>
    public int[] ProcessIds()
    {
        if (_disposed != 0) return Array.Empty<int>();
        int capacity = 64;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            // JOBOBJECT_BASIC_PROCESS_ID_LIST: two DWORDs (8 bytes, so the ULONG_PTR ids after them are aligned).
            const int header = 8;
            int size = header + capacity * IntPtr.Size;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (QueryInformationJobObject(_handle, JobObjectBasicProcessIdList, buffer, (uint)size, out _))
                {
                    int count = Marshal.ReadInt32(buffer, 4);
                    var ids = new int[count];
                    for (int i = 0; i < count; i++)
                        ids[i] = (int)Marshal.ReadIntPtr(buffer, header + i * IntPtr.Size).ToInt64();
                    return ids;
                }
                if (Marshal.GetLastWin32Error() != ERROR_MORE_DATA) return Array.Empty<int>();
                capacity *= 4;
            }
            catch { return Array.Empty<int>(); }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return Array.Empty<int>();
    }

    /// <summary>Close the handle — with KILL_ON_JOB_CLOSE that ends every process still in the job.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _handle.Dispose(); } catch { }
    }

    // ───────────────────────── P/Invoke ─────────────────────────

    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int ERROR_MORE_DATA = 234;

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeJobHandle hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeJobHandle hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength, out uint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeJobHandle hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_SET_QUOTA = 0x0100;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr hProcess, SafeJobHandle hJob, out bool result);
}

/// <summary>
/// Who is whose child, from a Toolhelp snapshot of the process table. Needed because a job does not always hold a
/// whole tree: a Microsoft Store app-execution alias (python.exe from the Store, winget-installed tools) starts the
/// packaged process OUTSIDE the caller's job — "python -m http.server" then outlived its bg_stop and its port.
/// The registry walks parent links from the job's processes, pulls such escapees back into the job, and kills the
/// ones that refuse explicitly.
/// </summary>
internal static class ProcessTree
{
    /// <summary>pid → parent pid for every process right now (empty on failure).</summary>
    public static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        if (!OperatingSystem.IsWindows()) return map;
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref entry)) return map;
            do map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            while (Process32NextW(snap, ref entry));
        }
        catch { }
        finally { CloseHandle(snap); }
        return map;
    }

    /// <summary>
    /// Every live descendant of <paramref name="roots"/> (roots excluded) that started no earlier than
    /// <paramref name="notBefore"/> AND no earlier than the parent it is reached through — a parent id the OS has
    /// since reused (for a process of ours, or an unrelated one) never makes an older process "ours".
    /// </summary>
    public static List<int> Descendants(IReadOnlyCollection<int> roots, DateTime notBefore) =>
        Descendants(roots, notBefore, ParentMap(), StartTime);

    /// <summary>The walk itself, over a given parent map and start-time lookup (the harness feeds it a made-up
    /// process table, since a reused pid cannot be produced on demand).</summary>
    internal static List<int> Descendants(IReadOnlyCollection<int> roots, DateTime notBefore,
        IReadOnlyDictionary<int, int> parents, Func<int, DateTime?> startTime)
    {
        var result = new List<int>();
        if (roots.Count == 0) return result;
        var children = new Dictionary<int, List<int>>();
        foreach (var (pid, parent) in parents)
        {
            if (pid == parent) continue;
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<int>();
            list.Add(pid);
        }
        var started = new Dictionary<int, DateTime?>();
        DateTime? StartOf(int pid) => started.TryGetValue(pid, out var t) ? t : started[pid] = startTime(pid);

        var seen = new HashSet<int>(roots);
        var queue = new Queue<int>(roots);
        while (queue.Count > 0)
        {
            int parent = queue.Dequeue();
            if (!children.TryGetValue(parent, out var kids)) continue;
            // A parent we cannot date (gone, or access denied) proves nothing about who its "children" are.
            if (StartOf(parent) is not { } parentStart) continue;
            foreach (int kid in kids)
            {
                if (seen.Contains(kid)) continue;
                if (StartOf(kid) is not { } kidStart || kidStart < notBefore.AddSeconds(-2) || kidStart < parentStart) continue;
                seen.Add(kid);
                result.Add(kid);
                queue.Enqueue(kid);
            }
        }
        return result;
    }

    /// <summary>When <paramref name="pid"/> started (null when it is gone or unreadable).</summary>
    public static DateTime? StartTime(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.StartTime; }
        catch { return null; }
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
