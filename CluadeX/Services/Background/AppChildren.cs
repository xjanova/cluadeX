using System.Diagnostics;

namespace CluadeX.Services.Background;

/// <summary>
/// One kill-on-close job for the long-lived helpers CluadeX starts for itself — llama-server, MCP servers, the CLI
/// agents' turns, language servers, the REPL, debug adapters. When CluadeX exits, crashes or is ended from Task Manager,
/// Windows closes the job's handle and ends them and everything they started: an orphaned llama-server kept its
/// gigabytes of VRAM and an MCP server its port until the next reboot (owner 2026-11-02: "จัดการทรัพยากรได้ดี มีการล้าง
/// เก็บกวาด"). Never for what must outlive CluadeX — the programs desktop_launch opens, the updater, a background
/// process (those have their own job, <see cref="BackgroundProcessService"/>).
/// </summary>
public static class AppChildren
{
    private static readonly Lazy<ProcessJob?> Job = new(() => ProcessJob.TryCreate(out _), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Put a just-started helper in the job. Best effort: false when the OS refuses (it already exited, or
    /// it is in a job that forbids nesting) — the helper then runs exactly as before.</summary>
    public static bool Adopt(Process? process)
    {
        if (process == null) return false;
        try { return Job.Value?.Assign(process) == true; }
        catch { return false; }
    }
}
