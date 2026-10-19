using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>What this PC has for running a local model. Built by <see cref="GpuDetectionService.DetectHardware"/>.</summary>
public sealed class HardwareProfile
{
    /// <summary>"NVIDIA GeForce GTX 1070 Ti" — the first (main) GPU, or "" without one.</summary>
    public string GpuName { get; init; } = "";
    public string GpuBrand { get; init; } = "Unknown";
    public int GpuCount { get; init; }
    /// <summary>VRAM of every usable GPU added up (llama-server splits a model across them).</summary>
    public int VramTotalMB { get; init; }
    /// <summary>VRAM free when the hardware was read; 0 = unknown.</summary>
    public int VramFreeMB { get; init; }
    /// <summary>True when the local backend can put layers on the GPU (NVIDIA/CUDA in this build, not forced to CPU).</summary>
    public bool CanOffload { get; init; }
    /// <summary>Memory bandwidth of the GPU in GB/s (estimated from its name).</summary>
    public double GpuBandwidthGBs { get; init; }
    public int SystemRamMB { get; init; }
    /// <summary>System memory bandwidth in GB/s (from the DIMM speed, or a desktop default).</summary>
    public double RamBandwidthGBs { get; init; } = 38;
    public int CpuThreads { get; init; } = Environment.ProcessorCount;
    /// <summary>A model is loaded by CluadeX right now: its VRAM would come back, so free VRAM is not the limit.</summary>
    public bool LocalModelLoaded { get; init; }

    public bool HasGpu => VramTotalMB > 0;

    /// <summary>
    /// VRAM a model may use. The display, browser and other apps already hold some: when the current free VRAM is
    /// known (and is not low because of our own model) it is the limit, otherwise a reserve is kept back.
    /// </summary>
    public int UsableVramMB
    {
        get
        {
            if (!CanOffload || VramTotalMB <= 0) return 0;
            int reserve = Math.Clamp(VramTotalMB * 15 / 100, 600, 1500);
            int byTotal = VramTotalMB - reserve;
            if (VramFreeMB > 0 && !LocalModelLoaded)
                return Math.Max(0, Math.Min(byTotal, VramFreeMB - 300));
            return Math.Max(0, byTotal);
        }
    }

    /// <summary>System RAM a model may spill into: what is left after Windows and the apps (≈ 25 % + 2 GB).</summary>
    public int RamBudgetMB => Math.Max(0, SystemRamMB * 3 / 4 - 2048);
}

/// <summary>One quant evaluated on one PC.</summary>
public sealed class QuantEstimate
{
    public required QuantOption Quant { get; init; }
    /// <summary>Weights + KV cache + compute buffers, MB.</summary>
    public int NeedMB { get; init; }
    public int KvCacheMB { get; init; }
    /// <summary>Share of the weights that sits in VRAM (0 = CPU only, 1 = all on the GPU).</summary>
    public double GpuFraction { get; init; }
    public double TokensPerSecond { get; init; }
    public ModelFitTier Tier { get; init; }
    /// <summary>-1 = every layer on the GPU, otherwise the -ngl to use.</summary>
    public int SuggestedGpuLayers { get; init; } = -1;
}

/// <summary>A reason shown under a recommendation: a localization key + its arguments.</summary>
public sealed record AdviceReason(string Key, params object[] Args);

public enum SpeedTier { VeryFast, Fast, Ok, Slow, VerySlow }

/// <summary>
/// Picks local models for the hardware and the job. Pure arithmetic over the catalog metadata — no I/O — so the
/// harness can drive it with any made-up PC.
///
/// The estimates are deliberately simple and explained in the UI as estimates:
///   memory  = GGUF file size + KV cache for the context + ~300 MB compute buffers;
///   speed   = token generation is memory-bandwidth bound: every token reads the active weights once, so
///             tok/s ≈ 1 / (bytes on GPU / GPU bandwidth + bytes in RAM / RAM bandwidth), at ~55-60 % efficiency.
/// </summary>
public static class ModelAdvisor
{
    /// <summary>Quality order of the quants the catalog offers, best first.</summary>
    public static readonly string[] QualityOrder = ["Q8_0", "Q6_K", "Q5_K_M", "Q4_K_M", "Q4_0", "IQ4_XS", "Q3_K_M"];

    private static int QualityRank(string quant)
    {
        int i = Array.IndexOf(QualityOrder, quant);
        return i < 0 ? QualityOrder.Length : i;
    }

    /// <summary>Architectures where most layers use linear attention / state space: a much smaller KV cache.</summary>
    private static readonly HashSet<string> HybridArchs = new(StringComparer.OrdinalIgnoreCase)
        { "qwen35", "qwen35moe", "qwen3next", "nemotron_h", "nemotron_h_moe", "lfm2", "granitehybrid", "jamba", "mamba", "mamba2", "falcon-h1" };

    /// <summary>KV cache in MB for <paramref name="contextTokens"/> (capped at the model's native context).</summary>
    public static int KvCacheMB(RecommendedModel m, int contextTokens)
    {
        int ctx = m.NativeContext > 0 ? Math.Min(contextTokens, m.NativeContext) : contextTokens;
        // Effective size for attention: a MoE's attention is closer to its active size than its total.
        double p = m.IsMoE ? Math.Sqrt(m.TotalParamsB * m.ActiveParamsB) : Math.Max(m.TotalParamsB, m.ParameterBillions);
        if (p <= 0) p = 7;
        double perK = 40 * Math.Pow(p, 0.55);                      // fp16 KV, GQA: ~60 MB/1K tokens at 2B, ~120 at 8B, ~270 at 32B
        string arch = m.Architecture ?? "";
        if (HybridArchs.Contains(arch)) perK *= 0.35;
        else if (arch.StartsWith("gemma", StringComparison.OrdinalIgnoreCase)) perK *= 0.5;   // sliding-window layers
        return (int)Math.Round(perK * ctx / 1024.0);
    }

    /// <summary>Evaluate one quant of a catalog model.</summary>
    public static QuantEstimate Estimate(RecommendedModel m, QuantOption q, HardwareProfile hw, int contextTokens)
    {
        double total = m.TotalParamsB > 0 ? m.TotalParamsB : Math.Max(1, m.ParameterBillions);
        double activeShare = m.IsMoE ? Math.Clamp(m.ActiveParamsB / total, 0.02, 1) : 1;
        return Estimate(q, hw, KvCacheMB(m, contextTokens), activeShare, total);
    }

    /// <summary>Evaluate a GGUF file the catalog does not know (search results, the user's own files): dense.</summary>
    public static QuantEstimate EstimateFile(long fileBytes, double paramsB, HardwareProfile hw, int contextTokens)
    {
        var m = new RecommendedModel { TotalParamsB = paramsB > 0 ? paramsB : GuessParamsFromSize(fileBytes) };
        var q = new QuantOption("", "", fileBytes);
        return Estimate(m, q, hw, contextTokens);
    }

    /// <summary>~0.6 bytes per parameter at 4-5 bits.</summary>
    private static double GuessParamsFromSize(long bytes) => Math.Max(0.5, bytes / 1e9 / 0.6);

    private static QuantEstimate Estimate(QuantOption q, HardwareProfile hw, int kvMB, double activeShare, double totalParamsB)
    {
        int weightsMB = (int)(q.SizeBytes / (1024 * 1024));
        int overheadMB = 300 + weightsMB * 3 / 100;
        int need = weightsMB + kvMB + overheadMB;

        int usable = hw.UsableVramMB;
        int ram = hw.RamBudgetMB;
        double gpuFraction;
        ModelFitTier tier;

        if (usable > 0)
        {
            if (need <= usable * 0.85) { gpuFraction = 1; tier = ModelFitTier.Excellent; }
            else if (need <= usable) { gpuFraction = 1; tier = ModelFitTier.Good; }
            else
            {
                // KV cache and buffers stay on the GPU first; the layers that do not fit go to system RAM.
                gpuFraction = Math.Clamp((usable - kvMB - overheadMB) / (double)Math.Max(1, weightsMB), 0, 0.99);
                int spill = (int)(weightsMB * (1 - gpuFraction));
                tier = spill > ram ? ModelFitTier.TooLarge : ModelFitTier.Unknown;   // speed decides below
            }
        }
        else
        {
            gpuFraction = 0;
            tier = need > ram ? ModelFitTier.TooLarge : ModelFitTier.Unknown;   // speed decides below
        }

        double tps = TokensPerSecond(q.SizeBytes, activeShare, gpuFraction, hw);
        // Split between GPU and RAM (or CPU only): what matters is the speed that leaves, not the share on the GPU —
        // a 35B-A3B MoE with three quarters in RAM still answers at a usable rate, a dense 27B does not.
        if (tier == ModelFitTier.Unknown)
            tier = usable <= 0
                ? (tps >= 12 ? ModelFitTier.Good : tps >= 5 ? ModelFitTier.Partial : ModelFitTier.Poor)
                : (tps >= 6 ? ModelFitTier.Partial : ModelFitTier.Poor);

        int layers = LayerGuess(totalParamsB);
        int ngl = gpuFraction >= 1 ? -1 : (int)Math.Floor(layers * gpuFraction);

        return new QuantEstimate
        {
            Quant = q,
            NeedMB = need,
            KvCacheMB = kvMB,
            GpuFraction = gpuFraction,
            TokensPerSecond = tps,
            Tier = tier,
            SuggestedGpuLayers = usable <= 0 ? 0 : ngl,
        };
    }

    /// <summary>Generation speed in tokens/s (bandwidth bound).</summary>
    public static double TokensPerSecond(long fileBytes, double activeShare, double gpuFraction, HardwareProfile hw)
    {
        double gbPerToken = fileBytes / 1e9 * activeShare;
        if (gbPerToken <= 0) return 0;
        double gpuBw = Math.Max(1, hw.GpuBandwidthGBs) * 0.6;
        double ramBw = Math.Max(1, hw.RamBandwidthGBs) * 0.55;
        double seconds = gbPerToken * gpuFraction / gpuBw + gbPerToken * (1 - gpuFraction) / ramBw + 0.002;
        return Math.Min(200, 1 / seconds);
    }

    public static SpeedTier SpeedTierOf(double tps) => tps switch
    {
        >= 40 => SpeedTier.VeryFast,
        >= 18 => SpeedTier.Fast,
        >= 8 => SpeedTier.Ok,
        >= 3 => SpeedTier.Slow,
        _ => SpeedTier.VerySlow,
    };

    /// <summary>Rough transformer block count, only for a suggested -ngl when the model is split.</summary>
    private static int LayerGuess(double paramsB) => paramsB switch
    {
        <= 1.5 => 28,
        <= 4.5 => 36,
        <= 10 => 40,
        <= 16 => 48,
        <= 36 => 64,
        _ => 80,
    };

    /// <summary>
    /// Every quant evaluated, plus the one to use:
    ///   1. the best quality that runs fully on the GPU — but never below Q4 while a Q4 still runs split, and Q8_0
    ///      only for small models (above ~4B it doubles the memory traffic for a gain nobody notices);
    ///   2. nothing fits in VRAM: Q4_K_M if it still runs at a usable speed, else the fastest usable one;
    ///   3. nothing usable: the smallest file (shown as too large / slow, with the reason).
    /// </summary>
    public static (QuantEstimate Pick, List<QuantEstimate> All) PickQuant(RecommendedModel m, HardwareProfile hw, int contextTokens)
    {
        var all = m.AllQuants.Select(q => Estimate(m, q, hw, contextTokens)).ToList();
        double total = m.TotalParamsB > 0 ? m.TotalParamsB : m.ParameterBillions;
        var byQuality = all.OrderBy(e => QualityRank(e.Quant.Name)).ThenByDescending(e => e.Quant.SizeBytes).ToList();

        bool OnGpu(QuantEstimate e) => e.Tier is ModelFitTier.Excellent or ModelFitTier.Good;
        bool Allowed(QuantEstimate e) => e.Quant.Name != "Q8_0" || total <= 4.5 || byQuality.All(x => x.Quant.Name == "Q8_0");
        bool BelowQ4(QuantEstimate e) => QualityRank(e.Quant.Name) > QualityRank("IQ4_XS");

        var q4Split = byQuality.FirstOrDefault(e => e.Quant.Name is "Q4_K_M" or "Q4_0" && e.Tier == ModelFitTier.Partial);
        var pick = byQuality.FirstOrDefault(e => OnGpu(e) && Allowed(e) && !(BelowQ4(e) && q4Split != null));
        pick ??= q4Split
              ?? byQuality.Where(e => e.Tier == ModelFitTier.Partial).OrderByDescending(e => e.TokensPerSecond).FirstOrDefault()
              ?? byQuality.Where(e => e.Tier == ModelFitTier.Poor).OrderByDescending(e => e.TokensPerSecond).FirstOrDefault()
              ?? all.OrderBy(e => e.Quant.SizeBytes).First();
        return (pick, all);
    }

    /// <summary>0-5 strength for a job. "All" mixes chat, coding and reasoning.</summary>
    public static double TaskScore(RecommendedModel m, ModelTasks task) => task switch
    {
        ModelTasks.Coding => m.CodingScore,
        ModelTasks.Chat => m.ChatScore,
        ModelTasks.Reasoning => m.ReasoningScore,
        ModelTasks.Vision => m.VisionScore,
        ModelTasks.Thai => m.ThaiScore,
        _ => 0.4 * m.ChatScore + 0.35 * m.CodingScore + 0.25 * m.ReasoningScore,
    };

    /// <summary>Months between a "yyyy-MM" release and <paramref name="today"/> (999 when unknown).</summary>
    public static int AgeMonths(string released, DateTime today)
    {
        if (string.IsNullOrEmpty(released) || released.Length < 7
            || !int.TryParse(released.AsSpan(0, 4), out int y) || !int.TryParse(released.AsSpan(5, 2), out int mo)) return 999;
        return Math.Max(0, (today.Year - y) * 12 + today.Month - mo);
    }

    /// <summary>How good a pick this is for this PC and job (higher is better).</summary>
    public static double Score(RecommendedModel m, QuantEstimate e, ModelTasks task, DateTime today)
    {
        double s = TaskScore(m, task) * 2;
        s += e.Tier switch
        {
            ModelFitTier.Excellent => 3,
            ModelFitTier.Good => 2.5,
            ModelFitTier.Partial => 1,
            ModelFitTier.Poor => -1.5,
            ModelFitTier.TooLarge => -8,
            _ => 0,
        };
        s += SpeedTierOf(e.TokensPerSecond) switch
        {
            SpeedTier.VeryFast => 1.5,
            SpeedTier.Fast => 1,
            SpeedTier.Ok => 0.4,
            SpeedTier.Slow => -0.5,
            _ => -2,
        };
        int age = AgeMonths(m.Released, today);
        s += age <= 6 ? 1 : age <= 12 ? 0.5 : 0;
        s += e.Quant.Name switch
        {
            "Q8_0" or "Q6_K" => 0.4,
            "Q5_K_M" => 0.3,
            "Q4_K_M" or "Q4_0" => 0.1,
            "Q3_K_M" => -0.6,
            _ => 0,
        };
        if (m.IsLegacy) s -= 1.5;
        if (m.ArchSupported == false) s -= 10;
        return s;
    }

    /// <summary>A model that can be put forward as a pick at all (for the job, on this PC).</summary>
    public static bool IsPickable(RecommendedModel m, QuantEstimate e, ModelTasks task) =>
        !m.IsLegacy && m.ArchSupported != false
        && e.Tier is ModelFitTier.Excellent or ModelFitTier.Good or ModelFitTier.Partial
        && (task == ModelTasks.None ? TaskScore(m, task) >= 3 : (m.Tasks.HasFlag(task) && TaskScore(m, task) >= 3))
        // A vision pick must be able to see here: a projector the backend can read.
        && (task != ModelTasks.Vision || m.CanSeeImages);

    /// <summary>Why this model, best reason first.</summary>
    public static List<AdviceReason> Reasons(RecommendedModel m, QuantEstimate e, HardwareProfile hw, ModelTasks task, DateTime today)
    {
        var r = new List<AdviceReason>();
        if (m.ArchSupported == false) { r.Add(new("modelsm.why.newerBackend", m.Architecture)); return r; }

        double vramGb = Math.Round(hw.VramTotalMB / 1024.0, 1);
        int tps = (int)Math.Round(e.TokensPerSecond);
        switch (e.Tier)
        {
            case ModelFitTier.Excellent:
            case ModelFitTier.Good:
                r.Add(hw.GpuCount > 1 ? new("modelsm.why.fitsMultiGpu", hw.GpuCount, vramGb, e.Quant.Name)
                                      : new("modelsm.why.fitsVram", vramGb, e.Quant.Name));
                break;
            case ModelFitTier.Partial:
            case ModelFitTier.Poor:
                r.Add(hw.UsableVramMB > 0 ? new("modelsm.why.split", (int)Math.Round(e.GpuFraction * 100), e.Quant.Name)
                                          : new("modelsm.why.cpu", e.Quant.Name));
                break;
            case ModelFitTier.TooLarge:
                r.Add(new("modelsm.why.tooLarge", Math.Round(e.NeedMB / 1024.0, 1)));
                break;
        }
        if (e.Tier != ModelFitTier.TooLarge) r.Add(new("modelsm.why.speed", tps));

        int rank = QualityRank(e.Quant.Name), q4 = QualityRank("Q4_K_M");
        if (m.AllQuants.Count > 1)
        {
            if (rank < q4) r.Add(new("modelsm.why.quantUp", e.Quant.Name));
            else if (rank > QualityRank("Q4_0") && e.Tier != ModelFitTier.TooLarge) r.Add(new("modelsm.why.quantDown", e.Quant.Name));
        }

        if (m.IsMoE) r.Add(new("modelsm.why.moe", Fmt(m.ActiveParamsB), Fmt(m.TotalParamsB)));

        // Strengths: the job first, then the others worth knowing.
        var strengths = new List<(ModelTasks Task, int Score, string Key)>
        {
            (ModelTasks.Coding, m.CodingScore, "modelsm.why.coding"),
            (ModelTasks.Thai, m.ThaiScore, "modelsm.why.thai"),
            (ModelTasks.Reasoning, m.ReasoningScore, "modelsm.why.reasoning"),
            (ModelTasks.Vision, m.CanSeeImages ? m.VisionScore : 0, "modelsm.why.vision"),
            (ModelTasks.Chat, m.ChatScore, "modelsm.why.chat"),
        };
        foreach (var s in strengths.OrderByDescending(s => s.Task == task).ThenByDescending(s => s.Score))
            if (s.Score >= 4 || (s.Task == task && s.Score >= 3)) r.Add(new(s.Key));

        int age = AgeMonths(m.Released, today);
        if (age <= 6) r.Add(new("modelsm.why.new", m.Released));
        if (m.NativeContext >= 200_000) r.Add(new("modelsm.why.longContext", ContextShort(m.NativeContext)));
        return r;
    }

    private static string Fmt(double b) => b.ToString(b >= 10 || b == Math.Floor(b) ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"256K", "1M", "40K".</summary>
    public static string ContextShort(int tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_048_576.0:0.#}M".Replace(".0M", "M")
        : $"{Math.Round(tokens / 1024.0):0}K";
}
