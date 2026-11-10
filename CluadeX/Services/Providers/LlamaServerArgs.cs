using System.Globalization;

namespace CluadeX.Services.Providers;

/// <summary>What this llama-server build accepts, read from its <c>--help</c>. An unknown flag stops the server at
/// startup, and an older or user-supplied exe does not have what the bundled one has, so nothing new is sent blind.</summary>
public sealed record LlamaServerFeatures(bool NCpuMoe, bool FlashAttnOnOff, bool CacheTypes, bool CacheReuse, bool Fit, bool FitTarget)
{
    public static readonly LlamaServerFeatures None = new(false, false, false, false, false, false);

    public static LlamaServerFeatures Parse(string? help)
    {
        if (string.IsNullOrWhiteSpace(help)) return None;
        bool Has(string flag) => help.Contains(flag, StringComparison.Ordinal);
        // "-fa,   --flash-attn [on|off|auto]" (builds since 2025-09); the older "-fa, --flash-attn" took no value,
        // so "-fa on" there would read "on" as a stray argument and stop the server.
        bool faTri = help.Split('\n').Any(l => l.Contains("--flash-attn", StringComparison.Ordinal)
                                               && l.Contains("on|off", StringComparison.Ordinal));
        return new LlamaServerFeatures(
            NCpuMoe: Has("--n-cpu-moe"),
            FlashAttnOnOff: faTri,
            CacheTypes: Has("--cache-type-k") && Has("--cache-type-v"),
            CacheReuse: Has("--cache-reuse"),
            Fit: help.Split('\n').Any(l => l.Contains("--fit ", StringComparison.Ordinal) || l.TrimEnd().EndsWith("--fit", StringComparison.Ordinal)),
            FitTarget: Has("--fit-target"));
    }
}

/// <summary>Everything the launch decides from: the Settings page, this PC, the file, the server build.</summary>
public sealed record LlamaLaunchInput
{
    /// <summary>Settings → GPU layers: -1 = auto, 0 = none, N = exactly N (the owner's choice is never changed).</summary>
    public int GpuLayerSetting { get; init; } = -1;
    public bool CpuOnly { get; init; }
    /// <summary>Free VRAM of the NVIDIA cards right now (nvidia-smi); 0 = unknown (no NVIDIA card, or no reading).</summary>
    public int FreeVramMB { get; init; }
    public int ContextTokens { get; init; } = 12288;
    public int BatchSize { get; init; } = 512;
    /// <summary>Settings → CPU threads: 0 = auto.</summary>
    public int ThreadSetting { get; init; }
    public int PhysicalCores { get; init; }
    public int LogicalCores { get; init; } = Environment.ProcessorCount;
    /// <summary>Settings → KV cache: "auto" | "f16" | "q8_0" | "q4_0".</summary>
    public string KvCacheSetting { get; init; } = "auto";
    public LlamaServerFeatures Features { get; init; } = LlamaServerFeatures.None;
    public GgufMoeFacts? Model { get; init; }
    /// <summary>The vision projector's size when one is loaded (it goes to the GPU too).</summary>
    public long ProjectorBytes { get; init; }
    /// <summary>Start exactly as before this change — after a start with the new flags failed on this exe.</summary>
    public bool Legacy { get; init; }
}

/// <summary>The part of the llama-server command line that decides memory and speed, and a line saying what it did.</summary>
public sealed record LlamaLaunchPlan(List<string> Args, int GpuLayers, int? CpuMoeLayers, bool ServerFits, string KvType, int Threads, string Summary)
{
    /// <summary>Flags an older build might not know were sent: a failed start is then retried without them.</summary>
    public bool UsesNewFlags => Args.Any(a => a is "--n-cpu-moe" or "-fa" or "-ctk" or "-ctv" or "--cache-reuse" or "--fit" or "--fit-target");
}

/// <summary>
/// Builds llama-server's offload / KV / thread arguments. Pure: the harness checks the exact command line.
/// <list type="bullet">
/// <item><b>MoE that does not fit the card</b> (owner 2026-11-10, after the llama-modchip study): all layers on the GPU
/// with <c>--n-cpu-moe N</c> keeping the routed experts of the first N layers in RAM, N the fewest that leave the rest
/// inside free VRAM. Attention and the shared experts stay on the GPU, so a 30B-A3B on an 8 GB card answers at a
/// usable rate where <c>-ngl 999</c> ran out of memory or spilled to shared memory.</item>
/// <item><b>KV cache q8_0</b> (auto): about half of f16, so the same card holds about twice the window. K and V get the
/// SAME type on purpose: the CUDA flash-attention kernels of the release builds pair only equal types, a q8_0/q4_0
/// mix falls back to the CPU. A quantised V needs flash attention, hence <c>-fa on</c>.</item>
/// <item><b>Threads</b> = physical cores. Decoding is bound by memory bandwidth; a second thread per core (SMT) only
/// competes for it.</item>
/// <item><b>--cache-reuse</b>: when compaction cuts the middle of a long agent conversation, the server reuses the
/// unchanged chunks after the cut instead of reading the whole tail again.</item>
/// </list>
/// </summary>
public static class LlamaServerArgs
{
    /// <summary>VRAM kept free: Windows (WDDM) and the desktop need it, and the driver spills past it.</summary>
    public const int SafetyMB = 550;
    /// <summary>The prompt chunk below which --cache-reuse does not bother shifting.</summary>
    public const int CacheReuseChunk = 256;

    public static string ResolveKvType(string? setting, LlamaServerFeatures f)
    {
        if (!f.CacheTypes || !f.FlashAttnOnOff) return "f16";   // a quantised V cannot start without flash attention
        return (setting ?? "auto").Trim().ToLowerInvariant() switch
        {
            "f16" => "f16",
            "q4_0" => "q4_0",
            _ => "q8_0",   // "auto", "q8_0", and anything unknown from a hand-edited settings file
        };
    }

    /// <summary>Bytes one KV element takes: q8_0 = 34 bytes per 32, q4_0 = 18 per 32.</summary>
    public static double BytesPerElem(string kvType) => kvType switch
    {
        "q8_0" => 34.0 / 32,
        "q4_0" => 18.0 / 32,
        _ => 2.0,
    };

    public static int KvCacheMB(GgufMoeFacts m, int contextTokens, string kvType) =>
        (int)Math.Ceiling(contextTokens * (double)(m.KElemsPerToken + m.VElemsPerToken) * BytesPerElem(kvType) / (1024 * 1024));

    /// <summary>The compute buffers llama.cpp allocates on the card beside the weights (grows with the batch).</summary>
    public static int ComputeMB(int batchSize) => 512 + Math.Max(0, batchSize / 512 - 1) * 256;

    public static int ThreadsFor(int setting, int physicalCores, int logicalCores)
    {
        if (setting > 0) return setting;
        if (physicalCores > 0) return physicalCores;
        return Math.Max(1, logicalCores);
    }

    /// <summary>
    /// The fewest layers whose experts must stay in RAM so the rest fits <paramref name="budgetBytes"/> (free VRAM
    /// minus the margin, the KV cache and the compute buffers): 0 = the whole model fits, null = not even the part
    /// besides the experts fits (the experts are not the problem). --n-cpu-moe takes the FIRST N layers, so the
    /// experts on the card are those of the last layers.
    /// </summary>
    public static int? CpuMoeLayersFor(GgufMoeFacts m, long budgetBytes)
    {
        if (!m.IsMoE) return null;
        long left = budgetBytes - m.NonExpertGpuBytes;
        if (left < 0) return null;
        long onGpu = 0;
        int n = m.ExpertBytesPerLayer.Length;
        for (int layer = m.ExpertBytesPerLayer.Length - 1; layer >= 0; layer--)
        {
            if (onGpu + m.ExpertBytesPerLayer[layer] > left) break;
            onGpu += m.ExpertBytesPerLayer[layer];
            n = layer;
        }
        return n;
    }

    public static LlamaLaunchPlan Build(LlamaLaunchInput i)
    {
        var f = i.Legacy ? LlamaServerFeatures.None : i.Features;
        var args = new List<string>();
        int threads = ThreadsFor(i.ThreadSetting, i.PhysicalCores, i.LogicalCores);
        string kv = ResolveKvType(i.KvCacheSetting, f);

        int gpuLayers = i.CpuOnly ? 0 : i.GpuLayerSetting == -1 ? 999 : i.GpuLayerSetting;
        int? cpuMoe = null;
        bool serverFits = false;
        string place;

        if (i.CpuOnly || i.GpuLayerSetting != -1 || i.Model is not { IsMoE: true } moe)
        {
            // CPU, the owner's own layer count, or a dense model: exactly as before.
            place = gpuLayers == 0 ? "CPU" : i.GpuLayerSetting == -1 ? "GPU (all layers)" : $"GPU ({gpuLayers} layers)";
        }
        else if (i.FreeVramMB > 0 && f.NCpuMoe)
        {
            long budget = (long)(i.FreeVramMB - SafetyMB - KvCacheMB(moe, i.ContextTokens, kv) - ComputeMB(i.BatchSize)) * 1024 * 1024
                          - i.ProjectorBytes;
            cpuMoe = CpuMoeLayersFor(moe, budget);
            if (cpuMoe is null && f.Fit)
            {
                // Not even attention + shared weights fit: the server's own fitter measures the real buffers and
                // decides layers as well. -ngl left unset is what lets it.
                serverFits = true;
                place = "GPU (fitted by llama-server)";
            }
            else
            {
                // Without a fitter, every expert in RAM is the most that can be taken off the card.
                cpuMoe ??= moe.BlockCount;
                place = cpuMoe == 0 ? "GPU (all layers)" : $"GPU (all layers, experts of {cpuMoe}/{moe.BlockCount} layers in RAM)";
                if (cpuMoe == 0) cpuMoe = null;
            }
        }
        else if (f.Fit)
        {
            // No free-VRAM reading (AMD / Intel / no nvidia-smi): the server sizes it — not blindly all layers.
            serverFits = true;
            place = "GPU (fitted by llama-server)";
        }
        else
        {
            place = "GPU (all layers)";
        }

        if (serverFits)
        {
            args.AddRange(["--fit", "on"]);
            if (f.FitTarget) args.AddRange(["--fit-target", SafetyMB.ToString(CultureInfo.InvariantCulture)]);
            gpuLayers = -1;
        }
        else
        {
            args.AddRange(["-ngl", gpuLayers.ToString(CultureInfo.InvariantCulture)]);
            if (cpuMoe is int n && n > 0) args.AddRange(["--n-cpu-moe", n.ToString(CultureInfo.InvariantCulture)]);
        }

        if (kv != "f16") args.AddRange(["-fa", "on", "-ctk", kv, "-ctv", kv]);
        args.AddRange(["-t", threads.ToString(CultureInfo.InvariantCulture)]);
        if (f.CacheReuse) args.AddRange(["--cache-reuse", CacheReuseChunk.ToString(CultureInfo.InvariantCulture)]);

        string summary = $"{place} · KV {kv} · {threads} threads";
        return new LlamaLaunchPlan(args, gpuLayers, cpuMoe, serverFits, kv, threads, summary);
    }
}
