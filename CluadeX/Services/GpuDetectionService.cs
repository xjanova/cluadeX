using System.Diagnostics;
using System.Management;
using CluadeX.Models;

namespace CluadeX.Services;

public class GpuDetectionService
{
    private GpuInfo? _cachedInfo;

    public GpuInfo DetectGpu()
    {
        if (_cachedInfo != null) return _cachedInfo;

        var info = new GpuInfo();

        try
        {
            // Try nvidia-smi first for accurate NVIDIA VRAM info
            info = TryNvidiaSmi() ?? TryWmi() ?? info;
        }
        catch
        {
            info = TryWmi() ?? info;
        }

        _cachedInfo = info;
        return info;
    }

    private GpuInfo? TryNvidiaSmi()
    {
        try
        {
            string? output = RunNvidiaSmi(
                "--query-gpu=name,memory.total,memory.free,driver_version --format=csv,noheader,nounits", 5000);
            if (string.IsNullOrWhiteSpace(output))
                return null;

            // One line per GPU: the first is the main one. Splitting the whole output on ',' (as before) glued
            // GPU 0's driver version to GPU 1's name on a multi-GPU PC.
            string[] parts = output.Trim().Split('\n')[0].Trim().Split(',');
            if (parts.Length >= 4)
            {
                return new GpuInfo
                {
                    Name = parts[0].Trim(),
                    VramTotalBytes = long.Parse(parts[1].Trim()) * 1024 * 1024,
                    VramFreeBytes = long.Parse(parts[2].Trim()) * 1024 * 1024,
                    DriverVersion = parts[3].Trim(),
                    IsCudaAvailable = true,
                    GpuBrand = "NVIDIA",
                };
            }
        }
        catch { }

        return null;
    }

    private GpuInfo? TryWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
            GpuInfo? bestGpu = null;

            foreach (ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                long adapterRam = Convert.ToInt64(obj["AdapterRAM"] ?? 0);
                string driver = obj["DriverVersion"]?.ToString() ?? "";

                string brand = DetectGpuBrand(name);

                // Skip integrated GPUs unless we have nothing better
                bool isIntegrated = name.Contains("UHD Graphics", StringComparison.OrdinalIgnoreCase)
                                 || name.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase)
                                 || name.Contains("Iris", StringComparison.OrdinalIgnoreCase)
                                 || name.Contains("Vega", StringComparison.OrdinalIgnoreCase) && !name.Contains("RX", StringComparison.OrdinalIgnoreCase);

                bool isDedicatedGpu = brand == "NVIDIA"
                                   || (brand == "AMD" && !isIntegrated)
                                   || (brand == "Intel" && name.Contains("Arc", StringComparison.OrdinalIgnoreCase));

                if (isDedicatedGpu || adapterRam > 2L * 1024 * 1024 * 1024)
                {
                    // WMI AdapterRAM caps at ~4GB for 32-bit field, estimate from name
                    long estimatedVram = EstimateVramFromName(name, adapterRam, brand);
                    bool isCuda = brand == "NVIDIA";

                    var candidate = new GpuInfo
                    {
                        Name = name,
                        VramTotalBytes = estimatedVram,
                        VramFreeBytes = (long)(estimatedVram * 0.85),
                        DriverVersion = driver,
                        IsCudaAvailable = isCuda,
                        GpuBrand = brand,
                    };

                    // Prefer dedicated GPUs with CUDA, then most VRAM
                    if (bestGpu == null
                        || (isCuda && !bestGpu.IsCudaAvailable)
                        || (isCuda == bestGpu.IsCudaAvailable && estimatedVram > bestGpu.VramTotalBytes))
                    {
                        bestGpu = candidate;
                    }
                }
                else if (bestGpu == null && brand != "Unknown")
                {
                    // Integrated GPU as last resort
                    long estimatedVram = EstimateVramFromName(name, adapterRam, brand);
                    bestGpu = new GpuInfo
                    {
                        Name = name,
                        VramTotalBytes = estimatedVram,
                        VramFreeBytes = (long)(estimatedVram * 0.85),
                        DriverVersion = driver,
                        IsCudaAvailable = false,
                        GpuBrand = brand,
                    };
                }
            }

            return bestGpu;
        }
        catch { }

        return null;
    }

    private static string DetectGpuBrand(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
            || name.Contains("RTX", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GTX", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Quadro", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Tesla", StringComparison.OrdinalIgnoreCase))
        {
            return "NVIDIA";
        }

        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            || name.Contains("RX ", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }

        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Arc ", StringComparison.OrdinalIgnoreCase)
            || name.Contains("UHD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Iris", StringComparison.OrdinalIgnoreCase))
        {
            return "Intel";
        }

        return "Unknown";
    }

    private static long EstimateVramFromName(string name, long wmiReported, string brand)
    {
        // WMI AdapterRAM is unreliable for >4GB GPUs. Estimate from model name.
        var estimates = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        if (brand == "NVIDIA")
        {
            // NVIDIA GeForce / Professional
            estimates["4090"] = 24L * 1024 * 1024 * 1024;
            estimates["4080 SUPER"] = 16L * 1024 * 1024 * 1024;
            estimates["4080"] = 16L * 1024 * 1024 * 1024;
            estimates["4070 Ti SUPER"] = 16L * 1024 * 1024 * 1024;
            estimates["4070 Ti"] = 12L * 1024 * 1024 * 1024;
            estimates["4070 SUPER"] = 12L * 1024 * 1024 * 1024;
            estimates["4070"] = 12L * 1024 * 1024 * 1024;
            estimates["4060 Ti"] = 8L * 1024 * 1024 * 1024;
            estimates["4060"] = 8L * 1024 * 1024 * 1024;
            estimates["3090 Ti"] = 24L * 1024 * 1024 * 1024;
            estimates["3090"] = 24L * 1024 * 1024 * 1024;
            estimates["3080 Ti"] = 12L * 1024 * 1024 * 1024;
            estimates["3080"] = 10L * 1024 * 1024 * 1024;
            estimates["3070 Ti"] = 8L * 1024 * 1024 * 1024;
            estimates["3070"] = 8L * 1024 * 1024 * 1024;
            estimates["3060 Ti"] = 8L * 1024 * 1024 * 1024;
            estimates["3060"] = 12L * 1024 * 1024 * 1024;
            estimates["2080 Ti"] = 11L * 1024 * 1024 * 1024;
            estimates["2080 SUPER"] = 8L * 1024 * 1024 * 1024;
            estimates["2080"] = 8L * 1024 * 1024 * 1024;
            estimates["2070 SUPER"] = 8L * 1024 * 1024 * 1024;
            estimates["2070"] = 8L * 1024 * 1024 * 1024;
            estimates["2060 SUPER"] = 8L * 1024 * 1024 * 1024;
            estimates["2060"] = 6L * 1024 * 1024 * 1024;
            estimates["1080 Ti"] = 11L * 1024 * 1024 * 1024;
            estimates["1080"] = 8L * 1024 * 1024 * 1024;
            estimates["1070"] = 8L * 1024 * 1024 * 1024;
            estimates["1060"] = 6L * 1024 * 1024 * 1024;
            estimates["A100"] = 80L * 1024 * 1024 * 1024;
            estimates["A6000"] = 48L * 1024 * 1024 * 1024;
            estimates["A5000"] = 24L * 1024 * 1024 * 1024;
        }
        else if (brand == "AMD")
        {
            // AMD Radeon RX 7000 series
            estimates["7900 XTX"] = 24L * 1024 * 1024 * 1024;
            estimates["7900 XT"] = 20L * 1024 * 1024 * 1024;
            estimates["7900 GRE"] = 16L * 1024 * 1024 * 1024;
            estimates["7800 XT"] = 16L * 1024 * 1024 * 1024;
            estimates["7700 XT"] = 12L * 1024 * 1024 * 1024;
            estimates["7600 XT"] = 16L * 1024 * 1024 * 1024;
            estimates["7600"] = 8L * 1024 * 1024 * 1024;
            // AMD Radeon RX 6000 series
            estimates["6950 XT"] = 16L * 1024 * 1024 * 1024;
            estimates["6900 XT"] = 16L * 1024 * 1024 * 1024;
            estimates["6800 XT"] = 16L * 1024 * 1024 * 1024;
            estimates["6800"] = 16L * 1024 * 1024 * 1024;
            estimates["6750 XT"] = 12L * 1024 * 1024 * 1024;
            estimates["6700 XT"] = 12L * 1024 * 1024 * 1024;
            estimates["6700"] = 10L * 1024 * 1024 * 1024;
            estimates["6650 XT"] = 8L * 1024 * 1024 * 1024;
            estimates["6600 XT"] = 8L * 1024 * 1024 * 1024;
            estimates["6600"] = 8L * 1024 * 1024 * 1024;
            estimates["6500 XT"] = 4L * 1024 * 1024 * 1024;
            // AMD Radeon RX 5000 series
            estimates["5700 XT"] = 8L * 1024 * 1024 * 1024;
            estimates["5700"] = 8L * 1024 * 1024 * 1024;
            estimates["5600 XT"] = 6L * 1024 * 1024 * 1024;
            estimates["5500 XT"] = 8L * 1024 * 1024 * 1024;
        }
        else if (brand == "Intel")
        {
            // Intel Arc series
            estimates["A770"] = 16L * 1024 * 1024 * 1024;
            estimates["A750"] = 8L * 1024 * 1024 * 1024;
            estimates["A580"] = 8L * 1024 * 1024 * 1024;
            estimates["A380"] = 6L * 1024 * 1024 * 1024;
            estimates["A310"] = 4L * 1024 * 1024 * 1024;
            // Intel Arc B-series
            estimates["B580"] = 12L * 1024 * 1024 * 1024;
            estimates["B570"] = 10L * 1024 * 1024 * 1024;
        }

        foreach (var kv in estimates)
        {
            if (name.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }

        return wmiReported > 0 ? wmiReported : 4L * 1024 * 1024 * 1024;
    }

    public void ClearCache() { _cachedInfo = null; _cachedRam = null; }

    // ─── Whole-PC profile for the model advisor ───────────────────────

    private (int TotalMB, double BandwidthGBs)? _cachedRam;

    /// <summary>
    /// GPU(s), system RAM and memory bandwidth for <see cref="ModelAdvisor"/>. Several NVIDIA cards count together
    /// (llama-server splits a model across them with --tensor-split). The bundled llama.cpp is a CUDA build, so only
    /// NVIDIA cards take layers; with "CPU" chosen in Settings nothing does.
    /// </summary>
    public HardwareProfile DetectHardware(string gpuBackendSetting = "Auto", bool localModelLoaded = false)
    {
        var main = DetectGpu();
        var nvidia = DetectAllNvidiaGpus();
        bool cuda = main.IsCudaAvailable || nvidia.Count > 0;
        bool canOffload = cuda && !string.Equals(gpuBackendSetting, "CPU", StringComparison.OrdinalIgnoreCase);
        int count = nvidia.Count > 0 ? nvidia.Count : (main.VramTotalBytes > 0 ? 1 : 0);
        int total = nvidia.Count > 0 ? nvidia.Sum(g => g.VramTotalMB) : main.VramTotalMB;
        int free = nvidia.Count > 0 ? nvidia.Sum(g => g.VramFreeMB) : 0;   // WMI has no real free figure
        var (ramMB, ramBw) = _cachedRam ??= ReadSystemRam();
        return new HardwareProfile
        {
            GpuName = main.VramTotalBytes > 0 ? main.Name : "",
            GpuBrand = main.GpuBrand,
            GpuCount = count,
            VramTotalMB = total,
            VramFreeMB = free,
            CanOffload = canOffload && total > 0,
            GpuBandwidthGBs = EstimateGpuBandwidthGBs(main.Name, main.VramTotalMB),
            SystemRamMB = ramMB,
            RamBandwidthGBs = ramBw,
            CpuThreads = Environment.ProcessorCount,
            LocalModelLoaded = localModelLoaded,
        };
    }

    /// <summary>
    /// Memory bandwidth (GB/s) by GPU name — token generation speed is bound by it. Unknown cards are guessed from
    /// their VRAM size (bigger cards have wider buses).
    /// </summary>
    public static double EstimateGpuBandwidthGBs(string name, int vramMB)
    {
        (string Key, double GBs)[] known =
        {
            ("5090", 1792), ("5080", 960), ("5070 Ti", 896), ("5070", 672), ("5060 Ti", 448), ("5060", 448),
            ("4090", 1008), ("4080 SUPER", 736), ("4080", 717), ("4070 Ti SUPER", 672), ("4070 Ti", 504), ("4070 SUPER", 504),
            ("4070", 504), ("4060 Ti", 288), ("4060", 272),
            ("3090 Ti", 1008), ("3090", 936), ("3080 Ti", 912), ("3080", 760), ("3070 Ti", 608), ("3070", 448),
            ("3060 Ti", 448), ("3060", 360), ("3050", 224),
            ("2080 Ti", 616), ("2080 SUPER", 496), ("2080", 448), ("2070 SUPER", 448), ("2070", 448), ("2060 SUPER", 448),
            ("2060", 336), ("1660 Ti", 288), ("1660 SUPER", 336), ("1660", 192), ("1650", 128),
            ("1080 Ti", 484), ("1080", 320), ("1070 Ti", 256), ("1070", 256), ("1060", 192), ("1050 Ti", 112),
            ("RTX PRO 6000", 1792), ("A6000", 768), ("A5000", 768), ("A4000", 448), ("A100", 1935), ("H100", 3350),
            ("L40", 864), ("TITAN RTX", 672), ("TITAN V", 653), ("GB10", 273),
        };
        foreach (var (key, gbs) in known)
            if (!string.IsNullOrEmpty(name) && name.Contains(key, StringComparison.OrdinalIgnoreCase)) return gbs;
        return vramMB switch
        {
            <= 0 => 0,
            <= 4096 => 128,
            <= 6144 => 192,
            <= 8192 => 256,
            <= 12288 => 360,
            <= 16384 => 448,
            <= 24576 => 900,
            _ => 1000,
        };
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// <summary>Total RAM (MB) and its bandwidth: DIMM speed (MT/s) × 8 bytes × channels (2 when two or more sticks).</summary>
    private static (int TotalMB, double BandwidthGBs) ReadSystemRam()
    {
        int totalMB = 0;
        try
        {
            var st = new MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref st)) totalMB = (int)(st.TotalPhys / (1024 * 1024));
        }
        catch { /* not Windows */ }
        if (totalMB <= 0)
            try { totalMB = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)); } catch { }

        double bandwidth = 38;   // dual-channel DDR4 at a realistic rate
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
            int sticks = 0, speed = 0;
            foreach (ManagementObject o in searcher.Get())
            {
                sticks++;
                int s = Convert.ToInt32(o["ConfiguredClockSpeed"] ?? 0);
                if (s <= 0) s = Convert.ToInt32(o["Speed"] ?? 0);
                if (s > speed) speed = s;
            }
            if (speed > 0 && sticks > 0)
                bandwidth = speed * 8.0 * Math.Min(sticks, 2) / 1000.0;
        }
        catch { /* WMI unavailable: keep the default */ }
        return (totalMB, bandwidth);
    }

    /// <summary>
    /// Detect ALL NVIDIA GPUs so llama.cpp can split a large model across multiple cards.
    /// Returns an empty list if nvidia-smi isn't available or no NVIDIA GPUs present.
    /// Each entry has raw VRAM so callers can compute a --tensor-split weighting.
    /// </summary>
    public List<GpuInfo> DetectAllNvidiaGpus()
    {
        var gpus = new List<GpuInfo>();
        try
        {
            string? output = RunNvidiaSmi(
                "--query-gpu=index,name,memory.total,memory.free,driver_version --format=csv,noheader,nounits", 5000);
            if (string.IsNullOrWhiteSpace(output)) return gpus;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',');
                if (parts.Length < 4) continue;
                if (!int.TryParse(parts[0].Trim(), out var _idx)) continue;
                gpus.Add(new GpuInfo
                {
                    Name = parts[1].Trim(),
                    VramTotalBytes = long.Parse(parts[2].Trim()) * 1024 * 1024,
                    VramFreeBytes = long.Parse(parts[3].Trim()) * 1024 * 1024,
                    DriverVersion = parts.Length > 4 ? parts[4].Trim() : "",
                    IsCudaAvailable = true,
                    GpuBrand = "NVIDIA",
                });
            }
        }
        catch { /* nvidia-smi missing = no multi-GPU support */ }
        return gpus;
    }

    // ─── Real-Time GPU Monitoring ─────────────────────────────────────
    // Uses nvidia-smi for NVIDIA GPUs. Returns null for AMD/Intel (not supported).

    /// <summary>Get real-time GPU stats: temperature, utilization, VRAM used/free, power.</summary>
    public GpuLiveStats? GetLiveStats()
    {
        try
        {
            string? output = RunNvidiaSmi(
                "--query-gpu=temperature.gpu,utilization.gpu,utilization.memory,memory.used,memory.free,memory.total,power.draw,power.limit,fan.speed --format=csv,noheader,nounits", 3000);
            if (string.IsNullOrWhiteSpace(output))
                return null;

            string[] parts = output.Trim().Split(',');
            if (parts.Length >= 6)
            {
                return new GpuLiveStats
                {
                    TemperatureC = int.TryParse(parts[0].Trim(), out var t) ? t : 0,
                    GpuUtilization = int.TryParse(parts[1].Trim(), out var gu) ? gu : 0,
                    MemoryUtilization = int.TryParse(parts[2].Trim(), out var mu) ? mu : 0,
                    VramUsedMB = int.TryParse(parts[3].Trim(), out var vu) ? vu : 0,
                    VramFreeMB = int.TryParse(parts[4].Trim(), out var vf) ? vf : 0,
                    VramTotalMB = int.TryParse(parts[5].Trim(), out var vt) ? vt : 0,
                    PowerDrawW = parts.Length > 6 && double.TryParse(parts[6].Trim(), out var pw) ? pw : 0,
                    PowerLimitW = parts.Length > 7 && double.TryParse(parts[7].Trim(), out var pl) ? pl : 0,
                    FanSpeedPercent = parts.Length > 8 && int.TryParse(parts[8].Trim(), out var fs) ? fs : -1,
                };
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Run nvidia-smi with a hard timeout and return stdout, or null on any failure. Reads stdout
    /// asynchronously and kills the child if it overruns, so a hung nvidia-smi (bad driver state)
    /// can never block the caller — the old ReadToEnd()+WaitForExit pattern would hang forever if
    /// the process never closed its stdout, and GetLiveStats runs every 2s on a timer.
    /// </summary>
    private static string? RunNvidiaSmi(string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return null;

            var readTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            string output = readTask.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }
}
