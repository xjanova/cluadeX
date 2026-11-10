namespace CluadeX.Services.Gpu;

/// <summary>What a GPU share means for one model: the -ngl / num_gpu to use and how much VRAM that is.</summary>
public sealed record GpuSharePlan
{
    /// <summary>0–100, as set by the owner.</summary>
    public int Percent { get; init; }
    /// <summary>Layers to put on the GPU. -1 = every layer (no cap applies, or the layer count is unknown).</summary>
    public int Layers { get; init; }
    /// <summary>Offloadable layers of the model (block_count + 1 output layer, as llama.cpp counts them); 0 = unknown.</summary>
    public int TotalLayers { get; init; }
    /// <summary>The share of the card's VRAM this percentage stands for (MB); 0 when no GPU is known.</summary>
    public int BudgetMb { get; init; }
    /// <summary>Roughly how much of the model's weights land on the GPU (MB); 0 when the file size is unknown.</summary>
    public int WeightsOnGpuMb { get; init; }
    /// <summary>A cap was asked for (&lt; 100 %) but the model's layer count could not be read, so it cannot be applied.</summary>
    public bool CapUnknown => Percent < 100 && TotalLayers <= 0 && Layers < 0;
}

/// <summary>
/// The owner's cap on how much of the GPU a local model may take (owner 2026-11-06: "กำหนดว่าจะใช้ความสามารถของ gpu
/// กี่เปอร์เซ็นต์"). It is turned into the one knob every local backend has: how many layers go to the GPU —
/// llama-server / LLamaSharp <c>-ngl</c>, Ollama <c>num_gpu</c>. Pure, so the harness proves the numbers.
/// </summary>
public static class GpuShare
{
    public const int Default = 100;

    public static int Clamp(int percent) => Math.Clamp(percent, 0, 100);

    /// <summary>llama.cpp offloads block_count repeating layers plus the output layer ("offloaded 29/29 layers" for a
    /// 28-block 7B), and Ollama counts the same way.</summary>
    public static int TotalLayers(int blockCount) => blockCount > 0 ? blockCount + 1 : 0;

    /// <summary>
    /// The -ngl to use. <paramref name="configured"/> is AppSettings.GpuLayerCount (-1 = all, 0 = CPU, N = N layers);
    /// the percentage scales it down. Returns -1 for "all layers" (the caller maps it to its own "all" value).
    /// A share above 0 always keeps at least one layer on the GPU, so 1 % is not the same as CPU-only.
    /// </summary>
    public static int ScaleLayers(int configured, int percent, int blockCount)
    {
        percent = Clamp(percent);
        if (configured == 0 || percent == 0) return 0;
        if (percent >= 100) return configured;
        int total = TotalLayers(blockCount);
        if (total <= 0)
            // Layer count unknown: an explicit N can still be scaled; "all" cannot — left as is (the UI says so).
            return configured > 0 ? Math.Max(1, (int)Math.Floor(configured * percent / 100.0)) : -1;
        int baseLayers = configured < 0 ? total : Math.Min(configured, total);
        return Math.Max(1, (int)Math.Floor(baseLayers * percent / 100.0));
    }

    /// <summary>The -ngl value for a launch: CPU backend → 0, "all" → <paramref name="allValue"/> (999 for
    /// llama-server, 99 for LLamaSharp — what each used before the cap existed).</summary>
    public static int EffectiveNgl(int configured, string? gpuBackend, int percent, int blockCount, int allValue)
    {
        if (string.Equals(gpuBackend, "CPU", StringComparison.OrdinalIgnoreCase)) return 0;
        int n = ScaleLayers(configured, percent, blockCount);
        return n < 0 ? allValue : n;
    }

    /// <summary>True when the backend should be asked to use the GPU at all (the router's "wantGpu").</summary>
    public static bool WantsGpu(int configured, string? gpuBackend, int percent)
        => configured != 0 && Clamp(percent) > 0 && !string.Equals(gpuBackend, "CPU", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ollama's <c>num_gpu</c> option, or null to leave it out (Ollama then decides by itself, as before the cap).
    /// 100 % → null; 0 % → 0; otherwise the share of the model's layers (null when the layer count is unknown).
    /// </summary>
    public static int? OllamaNumGpu(int percent, int blockCount)
    {
        percent = Clamp(percent);
        if (percent >= 100) return null;
        if (percent == 0) return 0;
        int total = TotalLayers(blockCount);
        if (total <= 0) return null;
        return Math.Max(1, (int)Math.Floor(total * percent / 100.0));
    }

    /// <summary>What the slider position means, for the popup.</summary>
    public static GpuSharePlan Describe(int percent, int configured, string? gpuBackend, int blockCount, int vramTotalMb, long fileBytes)
    {
        percent = Clamp(percent);
        int total = TotalLayers(blockCount);
        int layers = string.Equals(gpuBackend, "CPU", StringComparison.OrdinalIgnoreCase) ? 0 : ScaleLayers(configured, percent, blockCount);
        int onGpu = layers < 0 ? total : layers;
        int weightsMb = 0;
        if (fileBytes > 0)
        {
            double fileMb = fileBytes / (1024.0 * 1024.0);
            weightsMb = total > 0 ? (int)Math.Round(fileMb * Math.Min(onGpu, total) / total)
                      : layers < 0 ? (int)Math.Round(fileMb) : 0;
        }
        return new GpuSharePlan
        {
            Percent = percent,
            Layers = layers,
            TotalLayers = total,
            BudgetMb = vramTotalMb > 0 ? (int)Math.Round(vramTotalMb * percent / 100.0) : 0,
            WeightsOnGpuMb = weightsMb,
        };
    }
}

/// <summary>
/// When the "you can cap the GPU here" hint floats up from the VRAM graph: only until the owner has used the slider
/// once, never in the first minutes, at most every few hours, and a handful of times in all — not spam.
/// </summary>
public static class GpuShareHint
{
    /// <summary>How long the graph must have been on screen this session before the first hint.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    /// <summary>Gap between hints.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(3);
    /// <summary>After this many hints it stops for good, used or not.</summary>
    public const int MaxShows = 6;

    public static bool ShouldShow(bool used, int shownCount, DateTime? lastShownUtc, TimeSpan graphVisibleFor, DateTime nowUtc)
        => !used
           && shownCount < MaxShows
           && graphVisibleFor >= FirstDelay
           && (lastShownUtc is not { } last || nowUtc - last >= Interval);
}
