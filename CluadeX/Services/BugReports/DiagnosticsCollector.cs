using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services.BugReports;

/// <summary>
/// What a problem report (and the head of every session's log) says about this PC and this copy of CluadeX, so a
/// problem can be read without asking the user "which version? which Windows? how much memory?" (owner 2026-11-09:
/// "เพิ่มการดีบั๊กให้ละเอียดมากขึ้นเพื่อไว้วิเคราะห์ปัญหาได้แม่นขึ้น").
///
/// Every value is plain text or a number; nothing here reads a chat, a key or a file's content. Paths and names still go
/// through <see cref="ReportScrubber"/> before a report leaves the PC. Every probe is best effort: a probe that throws
/// leaves its line out, never the report.
/// </summary>
public static class DiagnosticsCollector
{
    // ─── app ─────────────────────────────────────────────────────────

    public static Dictionary<string, object?> App(SettingsService? settings, DateTime? processStart = null)
    {
        var d = new Dictionary<string, object?>();
        Try(d, "version", () => AutoUpdateService.CurrentVersion);
        Try(d, "build", () => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "?");
        Try(d, "install", () => { SettingsService.ResolveDataRoot(out bool portable); return portable ? "portable" : "installed"; });
        Try(d, "language", () => settings?.Settings.Language);
        Try(d, "elevated", () => IsElevated());
        Try(d, "started", () => (processStart ?? Process.GetCurrentProcess().StartTime).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        Try(d, "uptime", () => FormatSpan(DateTime.Now - (processStart ?? Process.GetCurrentProcess().StartTime)));
        Try(d, "data_root", () => settings?.DataRoot);
        return d;
    }

    // ─── system ──────────────────────────────────────────────────────

    public static Dictionary<string, object?> System(GpuDetectionService? gpu = null)
    {
        var d = new Dictionary<string, object?>();
        Try(d, "os", () => WindowsName());
        Try(d, "os_version", () => Environment.OSVersion.VersionString);
        Try(d, "os_64bit", () => Environment.Is64BitOperatingSystem);
        Try(d, "process_arch", () => RuntimeInformation.ProcessArchitecture.ToString());
        Try(d, "dotnet", () => RuntimeInformation.FrameworkDescription);
        Try(d, "cpu", () => CpuName());
        Try(d, "cpu_logical_cores", () => Environment.ProcessorCount);
        if (MemoryStatus() is { } mem)
        {
            d["ram_total_gb"] = Math.Round(mem.TotalPhys / 1073741824.0, 1);
            d["ram_free_gb"] = Math.Round(mem.AvailPhys / 1073741824.0, 1);
            d["ram_load_percent"] = (int)mem.MemoryLoad;
        }
        if (gpu != null)
        {
            Try(d, "gpu", () =>
            {
                var g = gpu.DetectGpu();
                string vram = g.VramTotalBytes > 0 ? $" · {g.VramTotalBytes / 1073741824.0:0.0} GB" : "";
                string drv = string.IsNullOrEmpty(g.DriverVersion) ? "" : $" · driver {g.DriverVersion}";
                return $"{g.Name}{vram}{drv}{(g.IsCudaAvailable ? " · CUDA" : "")}";
            });
        }
        Try(d, "culture", () => $"{CultureInfo.CurrentCulture.Name} / ui {CultureInfo.CurrentUICulture.Name}");
        Try(d, "timezone", () => $"{TimeZoneInfo.Local.Id} (UTC{TimeZoneInfo.Local.GetUtcOffset(DateTime.Now):\\+hh\\:mm})");
        Try(d, "system_drive_free_gb", () =>
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            return root == null ? null : Math.Round(new DriveInfo(root).AvailableFreeSpace / 1073741824.0, 1);
        });
        Try(d, "network_available", () => global::System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable());
        Try(d, "system_proxy", () =>
        {
            var target = new Uri("https://xman4289.com/");
            var proxy = global::System.Net.WebRequest.GetSystemWebProxy().GetProxy(target);
            return proxy == null || proxy == target ? "none" : "set";
        });
        return d;
    }

    /// <summary>The screens as WPF sees them — UI thread only (the report window calls it before going async).</summary>
    public static Dictionary<string, object?> Display(global::System.Windows.Media.Visual? visual)
    {
        var d = new Dictionary<string, object?>();
        Try(d, "primary", () => $"{(global::System.Windows.SystemParameters.PrimaryScreenWidth):0}x{(global::System.Windows.SystemParameters.PrimaryScreenHeight):0} (device-independent)");
        Try(d, "virtual", () => $"{(global::System.Windows.SystemParameters.VirtualScreenWidth):0}x{(global::System.Windows.SystemParameters.VirtualScreenHeight):0}");
        if (visual != null)
            Try(d, "dpi_scale", () => $"{(global::System.Windows.Media.VisualTreeHelper.GetDpi(visual).DpiScaleX * 100):0}%");
        Try(d, "high_contrast", () => global::System.Windows.SystemParameters.HighContrast);
        Try(d, "animations", () => global::System.Windows.SystemParameters.ClientAreaAnimation);
        return d;
    }

    // ─── process ─────────────────────────────────────────────────────

    public static Dictionary<string, object?> ProcessInfo()
    {
        var d = new Dictionary<string, object?>();
        try
        {
            using var p = global::System.Diagnostics.Process.GetCurrentProcess();
            p.Refresh();
            d["working_set_mb"] = p.WorkingSet64 / 1048576;
            d["private_mb"] = p.PrivateMemorySize64 / 1048576;
            d["peak_working_set_mb"] = p.PeakWorkingSet64 / 1048576;
            d["threads"] = p.Threads.Count;
            d["handles"] = p.HandleCount;
            Try(d, "cpu_time", () => FormatSpan(p.TotalProcessorTime));
        }
        catch { }
        Try(d, "managed_heap_mb", () => GC.GetTotalMemory(false) / 1048576);
        Try(d, "gc_collections", () => $"gen0 {GC.CollectionCount(0)} · gen1 {GC.CollectionCount(1)} · gen2 {GC.CollectionCount(2)}");
        Try(d, "gc_pause_percent", () => Math.Round(GC.GetGCMemoryInfo().PauseTimePercentage, 2));
        return d;
    }

    /// <summary>One line for the log every few minutes in verbose mode: memory and handles over time show a leak.</summary>
    public static string ResourceLine()
    {
        var p = ProcessInfo();
        return string.Join(" · ", p.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    // ─── settings ────────────────────────────────────────────────────

    /// <summary>A setting whose name says it holds something personal or secret is never read, whatever its type.</summary>
    private static readonly Regex PrivateName = new(
        @"(?i)(key|token|secret|pass(word)?|license|activation|email|account|cookie|auth|credential|\bpin\b|signature|prompt|instruction|persona|memory|note|history|recent|session|user(name)?$|name$|phone|address|webhook|url$|endpoint|host$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Strings that are a choice from a list (a provider, a backend, a theme), not something a person typed.</summary>
    private static readonly Regex ChoiceName = new(
        @"(?i)(provider|backend|mode|level|language|theme|style|font|model(id)?$|format|engine|strategy|channel|policy|placement|dock|layout|tier|effort|scheme)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The settings as switches and numbers: every bool, number, enum and date, plus the strings that are a choice
    /// (provider, backend, theme, model id…). Lists and dictionaries give only their size. Anything whose name hints at a
    /// secret or something personal (key, token, license, e-mail, account, prompt, path to a person's file…) is left
    /// out by name, before its value is read.
    /// </summary>
    public static SortedDictionary<string, object?> Settings(object? settings)
    {
        var d = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        if (settings == null) return d;
        foreach (var prop in settings.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
            string name = prop.Name;
            if (PrivateName.IsMatch(name)) continue;
            var t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            object? v;
            try { v = prop.GetValue(settings); } catch { continue; }

            if (t == typeof(bool) || t.IsEnum || t == typeof(int) || t == typeof(long) || t == typeof(uint)
                || t == typeof(double) || t == typeof(float) || t == typeof(decimal) || t == typeof(short) || t == typeof(byte))
                d[name] = t.IsEnum ? v?.ToString() : v;
            else if (t == typeof(DateTime))
                d[name] = v is DateTime dt ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
            else if (t == typeof(string))
            {
                if (!ChoiceName.IsMatch(name)) { d[name] = v is string sv && sv.Length > 0 ? "(set)" : "(empty)"; continue; }
                string s = v as string ?? "";
                if (s.IndexOfAny(new[] { '\\', '/', '@', ':' }) >= 0) { d[name] = "(set)"; continue; }   // a path or an address after all
                d[name] = s.Length > 80 ? s[..80] + "…" : s;
            }
            else if (v is global::System.Collections.ICollection c)
                d[name + ".count"] = c.Count;
        }
        return d;
    }

    // ─── the log ─────────────────────────────────────────────────────

    /// <summary>The last <paramref name="max"/> warnings and errors (with their exception) from this session.</summary>
    public static List<Dictionary<string, object?>> RecentErrors(DebugLogService? log, int max = 25)
    {
        var list = new List<Dictionary<string, object?>>();
        if (log == null) return list;
        foreach (var e in log.Snapshot().Where(e => e.Level >= LogLevel.Warning).TakeLast(max))
        {
            list.Add(new Dictionary<string, object?>
            {
                ["time"] = e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["level"] = e.Level.ToString(),
                ["category"] = e.Category,
                ["message"] = Cut(e.Message, 1000),
                ["exception"] = e.Exception == null ? null : Cut(e.Exception, 6000),
            });
        }
        return list;
    }

    /// <summary>The session's log as text, newest lines kept when it is longer than <paramref name="maxChars"/>.</summary>
    public static string LogTail(DebugLogService? log, int maxChars = 300_000)
    {
        if (log == null) return "";
        var sb = new StringBuilder();
        foreach (var e in log.Snapshot()) sb.AppendLine(e.ToFlatString());
        if (sb.Length <= maxChars) return sb.ToString();
        string all = sb.ToString();
        int cut = all.IndexOf('\n', all.Length - maxChars);
        return "… (older lines left out)\n" + all[(cut < 0 ? all.Length - maxChars : cut + 1)..];
    }

    // ─── probes ──────────────────────────────────────────────────────

    private static void Try(Dictionary<string, object?> d, string key, Func<object?> probe)
    {
        try
        {
            var v = probe();
            if (v != null) d[key] = v;
        }
        catch { }
    }

    private static string Cut(string? s, int max) => s == null ? "" : s.Length <= max ? s : s[..max] + "…";

    public static string FormatSpan(TimeSpan t)
        => t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m"
         : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
         : $"{(int)t.TotalMinutes}m {t.Seconds}s";

    private static bool IsElevated()
    {
        using var id = global::System.Security.Principal.WindowsIdentity.GetCurrent();
        return new global::System.Security.Principal.WindowsPrincipal(id).IsInRole(global::System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>"Windows 11 Pro 24H2 (build 26100.2894)" from the registry; null off Windows.</summary>
    private static string? WindowsName()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (k == null) return null;
        string product = k.GetValue("ProductName") as string ?? "Windows";
        string build = k.GetValue("CurrentBuildNumber") as string ?? k.GetValue("CurrentBuild") as string ?? "";
        // Windows 11 still says "Windows 10" in ProductName; the build number tells them apart.
        if (int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
        string display = k.GetValue("DisplayVersion") as string ?? "";
        string ubr = k.GetValue("UBR") is int u ? "." + u.ToString(CultureInfo.InvariantCulture) : "";
        return $"{product}{(display.Length > 0 ? " " + display : "")} (build {build}{ubr})";
    }

    private static string? CpuName()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (k?.GetValue("ProcessorNameString") as string)?.Trim();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static MemoryStatusEx? MemoryStatus()
    {
        try
        {
            var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref m) ? m : null;
        }
        catch { return null; }
    }
}
