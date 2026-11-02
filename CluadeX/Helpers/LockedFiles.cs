using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Helpers;

/// <summary>
/// A .NET build whose output file is held open by a running program: MSBuild retries the copy and fails with MSB3027
/// ("Could not copy … The file is locked by: …") or MSB3021 ("Unable to copy file …"). CluadeX building itself while
/// it runs is the everyday case. This finds the files, asks Windows (Restart Manager) which processes hold them, and
/// says how to build anyway into a scratch output folder — nothing is ever killed.
/// </summary>
public static class LockedFiles
{
    /// <summary>One process that holds a file open.</summary>
    public sealed record Holder(int Pid, string Name);

    /// <summary>One output file a build could not write, with the error code and who holds it.</summary>
    public sealed record LockedOutput(string Code, string Path, IReadOnlyList<Holder> Holders);

    // MSB3027: Could not copy "src" to "dest". Exceeded retry count of 10. Failed. The file is locked by: "CluadeX (1234)"
    // MSB3021: Unable to copy file "src" to "dest". The process cannot access the file 'dest' because it is being used…
    private static readonly Regex CopyError = new(
        @"\b(MSB302[17])\b[^\r\n]*?(?:copy|copy file)\s+""(?<src>[^""]+)""\s+to\s+""(?<dest>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProjectSuffix = new(@"\[(?<proj>[^\[\]]+\.\w*proj)(?:::[^\]]*)?\]\s*$", RegexOptions.Compiled);

    private static readonly Regex LockedBy = new(@"locked by:\s*""(?<name>[^""(]+?)\s*\((?<pid>\d+)\)""", RegexOptions.Compiled);

    /// <summary>True when <paramref name="buildOutput"/> failed only because an output file is in use.</summary>
    public static bool IsLockedOutput(string? buildOutput) =>
        !string.IsNullOrEmpty(buildOutput) && CopyError.IsMatch(buildOutput);

    /// <summary>The locked output files named in <paramref name="buildOutput"/> (each once) and the processes holding
    /// them: Restart Manager first, the PID MSBuild printed when Windows names none (another user's process).</summary>
    public static List<LockedOutput> Find(string? buildOutput, string? workingDirectory = null)
    {
        var list = new List<LockedOutput>();
        if (string.IsNullOrEmpty(buildOutput)) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in CopyError.Matches(buildOutput))
        {
            string dest = m.Groups["dest"].Value;
            // The rest of the line: MSBuild names the holder there (MSB3027) and ends with "[C:\…\X.csproj]" — the
            // folder its relative paths ("bin\Debug\…") start from.
            int end = buildOutput.IndexOf('\n', m.Index);
            string line = (end < 0 ? buildOutput[m.Index..] : buildOutput[m.Index..end]).TrimEnd('\r');
            try
            {
                if (!System.IO.Path.IsPathRooted(dest))
                {
                    var proj = ProjectSuffix.Match(line);
                    string? baseDir = proj.Success ? System.IO.Path.GetDirectoryName(proj.Groups["proj"].Value) : workingDirectory;
                    if (!string.IsNullOrEmpty(baseDir)) dest = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, dest));
                }
            }
            catch { /* keep the path as printed */ }
            if (!seen.Add(dest)) continue;

            var holders = WhoHolds(dest);
            if (holders.Count == 0)
            {
                foreach (Match h in LockedBy.Matches(line))
                    if (int.TryParse(h.Groups["pid"].Value, out int pid)) holders.Add(new Holder(pid, h.Groups["name"].Value.Trim()));
            }
            list.Add(new LockedOutput(m.Groups[1].Value.ToUpperInvariant(), dest, holders));
        }
        return list;
    }

    /// <summary>The processes that hold <paramref name="path"/> open, from the Windows Restart Manager. Empty when none
    /// (or the API is unavailable) — never throws.</summary>
    public static List<Holder> WhoHolds(string path)
    {
        var result = new List<Holder>();
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path) || !File.Exists(path)) return result;
        uint session;
        var key = new StringBuilder(CchRmSessionKey + 1);
        try
        {
            if (RmStartSession(out session, 0, key) != 0) return result;
        }
        catch { return result; }   // rstrtmgr.dll missing (Server Core, Wine)
        try
        {
            if (RmRegisterResources(session, 1, new[] { path }, 0, null, 0, null) != 0) return result;
            uint needed = 0, count = 0, reasons = 0;
            int rc = RmGetList(session, out needed, ref count, null, ref reasons);
            if (rc == ErrorMoreData && needed > 0)
            {
                var infos = new RmProcessInfo[needed];
                count = needed;
                rc = RmGetList(session, out needed, ref count, infos, ref reasons);
                if (rc == 0)
                    for (int i = 0; i < count; i++)
                    {
                        int pid = infos[i].Process.dwProcessId;
                        string name = infos[i].strAppName ?? "";
                        try { using var p = Process.GetProcessById(pid); name = p.ProcessName + ".exe"; }
                        catch { /* gone, or another user's: keep Windows' app name */ }
                        result.Add(new Holder(pid, name));
                    }
            }
        }
        catch { /* best effort */ }
        finally
        {
            try { RmEndSession(session); } catch { }
        }
        return result;
    }

    /// <summary>True for a command that builds through MSBuild and so takes <c>-p:OutDir=</c>
    /// (dotnet build / msbuild / dotnet msbuild / dotnet test / dotnet publish).</summary>
    public static bool AcceptsOutDir(string command) =>
        Regex.IsMatch(command ?? "", @"^\s*(?:&\s*)?(?:""[^""]*\\)?(?:dotnet(?:\.exe)?""?\s+(?:build|msbuild|test|publish)\b|msbuild(?:\.exe)?""?\s)",
            RegexOptions.IgnoreCase);

    /// <summary>A scratch output folder for <paramref name="projectRoot"/>, outside the project (nothing to commit or
    /// clean up there): %TEMP%\cluadex-scratch-build\&lt;project slug&gt;\. Ends with a separator, as OutDir must.</summary>
    public static string ScratchOutDir(string projectRoot)
    {
        string slug = Services.MemoryService.ProjectSlug(projectRoot);
        return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cluadex-scratch-build", slug) + System.IO.Path.DirectorySeparatorChar;
    }

    /// <summary><paramref name="command"/> with its output sent to <paramref name="outDir"/>. Forward slashes and a
    /// trailing slash: a quoted Windows path ending in a backslash would escape its own closing quote.</summary>
    public static string WithOutDir(string command, string outDir)
    {
        string dir = outDir.Replace('\\', '/');
        if (!dir.EndsWith('/')) dir += "/";
        return command.TrimEnd() + $" \"-p:OutDir={dir}\"";
    }

    /// <summary>The text the model reads after a locked build: who holds what, that nothing was killed, and the
    /// scratch rebuild that gets a build anyway.</summary>
    public static string Explain(IReadOnlyList<LockedOutput> locked, string command, string scratchDir, bool canScratch)
    {
        var sb = new StringBuilder();
        sb.AppendLine("LOCKED OUTPUT — the code compiled, but the build could not overwrite these files because a running program holds them:");
        foreach (var l in locked)
        {
            string who = l.Holders.Count == 0 ? "a process Windows did not name"
                : string.Join(", ", l.Holders.Select(h => $"PID {h.Pid} ({h.Name})"));
            sb.AppendLine($"  {l.Code}: {l.Path} — held by {who}");
        }
        sb.AppendLine("Nothing was killed. Do not kill the process yourself (it may be the app the user is working in, or CluadeX itself).");
        if (canScratch)
            sb.AppendLine($"To build anyway, call run_build again with scratch=\"true\": it builds into {scratchDir} instead. "
                + "Anything that links the normal output folder (a test harness, a running app) still sees the OLD binary until "
                + "the program is closed and you build normally.");
        else
            sb.AppendLine($"'{command}' does not take an output folder, so a scratch build is not possible: ask the user to close "
                + "the program, then build again.");
        return sb.ToString().TrimEnd();
    }

    // ── Restart Manager (rstrtmgr.dll) ──

    private const int CchRmSessionKey = 32;          // CCH_RM_SESSION_KEY: a GUID's 32 hex digits
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;
    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RmUniqueProcess[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RmProcessInfo[]? rgAffectedApps, ref uint lpdwRebootReasons);
}
