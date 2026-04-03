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
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total,memory.free,driver_version --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            string[] parts = output.Trim().Split(',');
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

    public void ClearCache() => _cachedInfo = null;
}
