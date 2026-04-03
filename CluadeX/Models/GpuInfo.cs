namespace CluadeX.Models;

public class GpuInfo
{
    public string Name { get; set; } = "Unknown";
    public long VramTotalBytes { get; set; }
    public long VramFreeBytes { get; set; }
    public string DriverVersion { get; set; } = string.Empty;
    public bool IsCudaAvailable { get; set; }

    /// <summary>
    /// GPU brand: "NVIDIA", "AMD", "Intel", or "Unknown".
    /// </summary>
    public string GpuBrand { get; set; } = "Unknown";

    /// <summary>
    /// True if this is an AMD Radeon GPU.
    /// </summary>
    public bool IsAmdGpu => string.Equals(GpuBrand, "AMD", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True if this is an Intel Arc or UHD GPU.
    /// </summary>
    public bool IsIntelGpu => string.Equals(GpuBrand, "Intel", StringComparison.OrdinalIgnoreCase);

    public int VramTotalMB => (int)(VramTotalBytes / (1024 * 1024));
    public int VramFreeMB => (int)(VramFreeBytes / (1024 * 1024));
    public double VramTotalGB => Math.Round(VramTotalBytes / (1024.0 * 1024 * 1024), 1);
    public double VramFreeGB => Math.Round(VramFreeBytes / (1024.0 * 1024 * 1024), 1);

    public string VramDisplay => $"{VramTotalGB:F1} GB";

    public string StatusDisplay
    {
        get
        {
            if (VramTotalBytes <= 0)
                return "No dedicated GPU detected (CPU-only mode)";

            string backendNote = GpuBrand switch
            {
                "NVIDIA" => IsCudaAvailable ? "CUDA" : "CUDA unavailable",
                "AMD" => "Vulkan/CPU fallback",
                "Intel" => "CPU fallback",
                _ => "CPU fallback"
            };

            return $"{Name} ({VramDisplay}) [{backendNote}]";
        }
    }

    public ModelSizeRecommendation GetRecommendation()
    {
        int vramMB = VramTotalMB;

        // CPU-only: no VRAM, rely on system RAM, recommend small models
        if (vramMB <= 0)
        {
            return new ModelSizeRecommendation
            {
                MaxParameterBillions = 7,
                RecommendedParameterBillions = 3,
                RecommendedQuantization = "Q4_K_S",
                Description = "No dedicated GPU detected. Using CPU-only mode with system RAM. Small 1.5B-3B models recommended for acceptable performance."
            };
        }

        return vramMB switch
        {
            >= 24576 => new ModelSizeRecommendation // 24GB+
            {
                MaxParameterBillions = 70,
                RecommendedParameterBillions = 34,
                RecommendedQuantization = "Q5_K_M",
                Description = "Excellent! You can run large 34B-70B coding models."
            },
            >= 16384 => new ModelSizeRecommendation // 16GB+
            {
                MaxParameterBillions = 34,
                RecommendedParameterBillions = 14,
                RecommendedQuantization = "Q5_K_M",
                Description = "Great! 14B-34B coding models work well on your GPU."
            },
            >= 12288 => new ModelSizeRecommendation // 12GB+
            {
                MaxParameterBillions = 14,
                RecommendedParameterBillions = 14,
                RecommendedQuantization = "Q4_K_M",
                Description = "Good! 7B-14B models run smoothly on your GPU."
            },
            >= 8192 => new ModelSizeRecommendation // 8GB+
            {
                MaxParameterBillions = 14,
                RecommendedParameterBillions = 7,
                RecommendedQuantization = "Q4_K_M",
                Description = "Suitable for 7B-13B quantized coding models."
            },
            >= 6144 => new ModelSizeRecommendation // 6GB+
            {
                MaxParameterBillions = 7,
                RecommendedParameterBillions = 7,
                RecommendedQuantization = "Q4_K_S",
                Description = "6GB VRAM supports 7B models with Q4 quantization."
            },
            >= 4096 => new ModelSizeRecommendation // 4GB+
            {
                MaxParameterBillions = 7,
                RecommendedParameterBillions = 3,
                RecommendedQuantization = "Q4_K_S",
                Description = "Limited VRAM. Best with 1.5B-3B small models."
            },
            _ => new ModelSizeRecommendation
            {
                MaxParameterBillions = 3,
                RecommendedParameterBillions = 1,
                RecommendedQuantization = "Q4_0",
                Description = "Very limited VRAM. Only small 1-3B models will work."
            }
        };
    }
}

public class ModelSizeRecommendation
{
    public int MaxParameterBillions { get; set; }
    public int RecommendedParameterBillions { get; set; }
    public string RecommendedQuantization { get; set; } = "Q4_K_M";
    public string Description { get; set; } = string.Empty;
}
