using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Local;

/// <summary>What the tuner knows about a model file without loading it.</summary>
public sealed record ModelShape(
    string Architecture,
    long FileBytes,
    int Layers,
    int Experts,
    int ExpertsUsed,
    /// <summary>K+V width per token per layer, in elements (head_count_kv × (key_length + value_length)).</summary>
    int KvWidth,
    /// <summary>Share of the file that is NOT expert weights (attention, shared layers, embeddings). 1 for a dense model.</summary>
    double DenseShare)
{
    public bool IsMoe => Experts > 1;
}

/// <summary>The machine, as the tuner sees it.</summary>
public sealed record TuneHardware(
    int FreeVramMb,
    int TotalVramMb,
    int PhysicalCores,
    /// <summary>The llama-server here takes --n-cpu-moe.</summary>
    bool CpuMoe,
    /// <summary>The llama-server here takes "-fa on" (and so a q8_0 V cache).</summary>
    bool FlashAttn,
    int SettingsContext,
    int SettingsBatch);

/// <summary>One way to load the model: what goes on the GPU, the window, the KV cache type, threads and batches.</summary>
public sealed record TuneCandidate(
    string Label,
    /// <summary>-ngl (999 = every layer).</summary>
    int GpuLayers,
    /// <summary>--n-cpu-moe: the expert weights of the first N layers stay in system RAM (0 = none).</summary>
    int CpuMoeLayers,
    int Context,
    bool KvQ8,
    bool FlashAttn,
    int Threads,
    int Batch,
    int UBatch,
    int EstVramMb)
{
    /// <summary>The launch arguments this candidate adds or replaces (the launcher drops its own -ngl/-c/-b/-t).</summary>
    public List<string> Args()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var a = new List<string>
        {
            "-ngl", GpuLayers.ToString(inv),
            "-c", Context.ToString(inv),
            "-b", Batch.ToString(inv),
            "-ub", UBatch.ToString(inv),
            "-t", Threads.ToString(inv),
        };
        if (CpuMoeLayers > 0) a.AddRange(new[] { "--n-cpu-moe", CpuMoeLayers.ToString(inv) });
        if (FlashAttn) a.AddRange(new[] { "-fa", "on" });
        if (KvQ8) a.AddRange(new[] { "-ctk", "q8_0", "-ctv", "q8_0" });
        return a;
    }

    /// <summary>"ctx 12k · expert 20 ชั้นบน CPU · KV q8 · 6 threads".</summary>
    public string Summary(bool isThai)
    {
        var p = new List<string> { $"ctx {Context / 1024}k" };
        if (CpuMoeLayers > 0) p.Add(isThai ? $"expert {CpuMoeLayers} ชั้นไว้บน CPU" : $"experts of {CpuMoeLayers} layers on the CPU");
        else if (GpuLayers >= 999) p.Add(isThai ? "ทั้งหมดบน GPU" : "all on the GPU");
        else p.Add(isThai ? $"GPU {GpuLayers} ชั้น" : $"{GpuLayers} layers on the GPU");
        if (KvQ8) p.Add("KV q8");
        p.Add($"{Threads} threads");
        return string.Join(" · ", p);
    }
}

/// <summary>One benchmark of one candidate.</summary>
public sealed record TuneBench(bool Ok, int PromptN, double PromptTps, int GenN, double GenTps, double LoadSec, string Error = "")
{
    public static TuneBench Failed(string error, double loadSec = 0) => new(false, 0, 0, 0, 0, loadSec, error);
}

/// <summary>What one candidate came to: measured, or skipped and why.</summary>
public sealed record TuneTrial(TuneCandidate Candidate, TuneBench? Bench, string Skipped);

public sealed record TuneResult(TuneCandidate? Best, TuneBench? BestBench, bool Passed, IReadOnlyList<TuneTrial> Trials, bool OutOfTime)
{
    public int Measured => Trials.Count(t => t.Bench != null);
}

/// <summary>The tuned settings kept for a model on a card, used by every later load of it.</summary>
public sealed record TuneProfile(
    string Path,
    long FileSize,
    string Gpu,
    DateTime AtUtc,
    TuneCandidate Settings,
    double PromptTps,
    double GenTps,
    bool Passed,
    int Measured,
    string Tried);

/// <summary>
/// Load tuning: find the best way to load a model on THIS PC before calling it slow (owner 2026-11-06: "มันต้องทดลองปรับ
/// ค่าต่างๆ ให้ดีที่สุดสำหรับโมเดลที่เลือกแล้ว ก่อนปักธง"). Sudoku2's Qwen3-Coder-30B-A3B ran at ctx 16384 with plain
/// layer offload; for a MoE on 8 GB the big win is keeping attention on the GPU and the experts in system RAM.
///
/// Picking is pure (no load): the GGUF header gives layers, experts and KV width; free VRAM, physical cores and what this
/// llama-server accepts (--n-cpu-moe, -fa on) give the room. At most <see cref="MaxCandidates"/> candidates, the largest
/// window first (an agent needs context); <see cref="RunAsync"/> measures them in that order with a short fixed benchmark
/// and stops at the first that passes <see cref="ModelSpeedLog"/>'s thresholds.
///
/// SAFETY (a sustained GPU load cut this PC's power on 2026-11-01; a 7B at ctx 12288 restarted it on 2026-09-24): the
/// tune never starts by itself — only from the owner's click after a confirm that says how many loads it does; free VRAM
/// is read again before every candidate and one whose estimate does not fit with <see cref="MarginMb"/> to spare is
/// skipped; the whole tune has a time budget; a cancel is not a verdict.
/// </summary>
public static class LoadTuner
{
    public const int MaxCandidates = 5;
    /// <summary>VRAM left free for the desktop and the driver, on top of the estimate.</summary>
    public const int MarginMb = 700;
    /// <summary>The smallest window an agent step fits in (system prompt + tool schemas + a file).</summary>
    public const int MinAgentContext = 8192;
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(8);
    /// <summary>Prompt / generated tokens of the benchmark: big enough for ModelSpeedLog to judge, short enough for minutes.</summary>
    public const int BenchPromptTokens = 1500, BenchGenTokens = 96;

    // ─── the model ───

    /// <summary>The shape from the GGUF header (null when it cannot be read). <paramref name="activeShare"/> = the catalog's
    /// active ÷ total parameters, when the file is a catalog model; otherwise a MoE is assumed to be 8 % non-expert.</summary>
    public static ModelShape? ShapeOf(string path, double? activeShare = null)
    {
        var h = GgufMetadataReader.TryReadHeader(path);
        if (h == null || h.BlockCount <= 0) return null;
        long bytes;
        try { bytes = new FileInfo(path).Length; } catch { return null; }
        int kvWidth = KvWidthOf(h);
        return new ModelShape(h.Architecture, bytes, h.BlockCount, h.ExpertCount, h.ExpertUsedCount, kvWidth,
            h.ExpertCount > 1 ? DenseShareOf(activeShare, h.ExpertCount, h.ExpertUsedCount) : 1.0);
    }

    private static int KvWidthOf(GgufMetadataReader.ModelHeader h)
    {
        int heads = h.HeadCount > 0 ? h.HeadCount : 32;
        int kvHeads = h.HeadCountKv > 0 ? h.HeadCountKv : heads;
        int headDim = h.EmbeddingLength > 0 ? h.EmbeddingLength / heads : 128;
        int k = h.KeyLength > 0 ? h.KeyLength : headDim;
        int v = h.ValueLength > 0 ? h.ValueLength : headDim;
        return kvHeads * (k + v);
    }

    /// <summary>The non-expert share of a MoE: total = D + X, active = D + X·used/n  ⇒  X = (total − active)/(1 − used/n).</summary>
    public static double DenseShareOf(double? activeShare, int experts, int used)
    {
        if (activeShare is not { } a || a <= 0 || a >= 1 || experts <= 1 || used <= 0 || used >= experts) return 0.08;
        double x = (1 - a) / (1 - (double)used / experts);
        return Math.Clamp(1 - x, 0.03, 0.5);
    }

    // ─── estimates ───

    public static long KvBytes(ModelShape s, int ctx, bool q8) => (long)(s.Layers * (long)s.KvWidth * ctx * (q8 ? 1.0625 : 2.0));
    public static int ComputeMb(int ubatch) => (int)(250 + ubatch * 0.7);

    /// <summary>VRAM a candidate needs, in MB (weights on the GPU + KV cache + compute buffers).</summary>
    public static int EstimateVramMb(ModelShape s, int gpuLayers, int cpuMoeLayers, int ctx, bool kvQ8, int ubatch)
    {
        double file = s.FileBytes;
        double weights;
        if (s.IsMoe && gpuLayers >= 999)
        {
            double expertPerLayer = file * (1 - s.DenseShare) / s.Layers;
            weights = file * s.DenseShare + expertPerLayer * (s.Layers - Math.Clamp(cpuMoeLayers, 0, s.Layers));
        }
        else
        {
            double perLayer = file * 0.95 / s.Layers;
            weights = gpuLayers >= s.Layers ? file : perLayer * Math.Max(0, gpuLayers);
        }
        // The KV cache of layers on the CPU stays there too.
        double kvShare = gpuLayers >= s.Layers ? 1.0 : Math.Max(0, gpuLayers) / (double)s.Layers;
        double kv = KvBytes(s, ctx, kvQ8) * kvShare;
        int compute = gpuLayers > 0 ? ComputeMb(ubatch) : 0;
        return (int)Math.Ceiling((weights + kv) / 1048576.0) + compute;
    }

    // ─── picking ───

    /// <summary>At most <see cref="MaxCandidates"/> ways to load the model, best window first. Never one that would not
    /// fit the free VRAM with the margin (the fitting is done here, and checked again before each run).</summary>
    public static List<TuneCandidate> Pick(ModelShape s, TuneHardware hw)
    {
        int threads = Math.Max(1, hw.PhysicalCores);
        int batch = Math.Max(512, hw.SettingsBatch);
        int room = hw.FreeVramMb - MarginMb;
        bool q8 = hw.FlashAttn;   // a quantized V cache needs flash attention

        var ctxs = new List<int> { Math.Max(2048, hw.SettingsContext) };
        foreach (int c in new[] { 12288, MinAgentContext })
            if (c < ctxs[0]) ctxs.Add(c);

        var list = new List<TuneCandidate>();
        void Add(string label, int ctx, int ubatch)
        {
            var (ngl, cpuMoe) = Fit(s, hw, room, ctx, q8, ubatch);
            int est = EstimateVramMb(s, ngl, cpuMoe, ctx, q8, ubatch);
            var c = new TuneCandidate(label, ngl, cpuMoe, ctx, q8, hw.FlashAttn, threads, batch, ubatch, est);
            if (list.Any(o => o.GpuLayers == c.GpuLayers && o.CpuMoeLayers == c.CpuMoeLayers && o.Context == c.Context && o.UBatch == c.UBatch)) return;
            list.Add(c);
        }

        foreach (int ctx in ctxs)
        {
            Add($"ctx{ctx}", ctx, 512);
            if (FitsWhole(s, room, ctx, q8, 512)) break;   // all on the GPU already: a smaller window gains nothing
        }
        // Last try: smaller compute buffers at the floor window buy a few more layers / experts on the GPU.
        int floor = ctxs[^1];
        if (!FitsWhole(s, room, floor, q8, 512)) Add($"ctx{floor}-ub256", floor, 256);
        return list.Take(MaxCandidates).ToList();
    }

    private static bool FitsWhole(ModelShape s, int room, int ctx, bool q8, int ubatch)
        => EstimateVramMb(s, 999, 0, ctx, q8, ubatch) <= room;

    /// <summary>(-ngl, --n-cpu-moe) that fit <paramref name="room"/> MB. A MoE keeps every layer's attention on the GPU and
    /// moves the experts of the first layers to the CPU; a dense model (or a server without --n-cpu-moe) offloads layers.</summary>
    public static (int GpuLayers, int CpuMoeLayers) Fit(ModelShape s, TuneHardware hw, int room, int ctx, bool q8, int ubatch)
    {
        if (room <= 0) return (0, 0);
        if (FitsWhole(s, room, ctx, q8, ubatch)) return (999, 0);
        if (s.IsMoe && hw.CpuMoe)
        {
            for (int k = 1; k <= s.Layers; k++)
                if (EstimateVramMb(s, 999, k, ctx, q8, ubatch) <= room) return (999, k);
            // Even all experts on the CPU do not fit: fall through to plain layer offload.
        }
        for (int n = s.Layers - 1; n > 0; n--)
            if (EstimateVramMb(s, n, 0, ctx, q8, ubatch) <= room) return (n, 0);
        return (0, 0);
    }

    // ─── measuring ───

    /// <summary>A benchmark passes when ModelSpeedLog would not call it slow.</summary>
    public static bool Passes(TuneBench b) => b.Ok && b.GenN > 0
        && ModelSpeedLog.Judge(b.PromptN, b.PromptTps, b.GenN, b.GenTps, 0) == SlowReason.None;

    /// <summary>Measure the candidates in order: free VRAM read again before each (skip what does not fit), stop at the
    /// first that passes, stop when the time budget is spent. Cancelling throws (a cancel is not a verdict). The best
    /// candidate is the one that passed, else the fastest writer measured.</summary>
    public static async Task<TuneResult> RunAsync(
        IReadOnlyList<TuneCandidate> candidates,
        Func<TuneCandidate, CancellationToken, Task<TuneBench>> bench,
        Func<int> freeVramMb,
        IProgress<string>? progress,
        bool isThai,
        CancellationToken ct,
        TimeSpan? budget = null)
    {
        var trials = new List<TuneTrial>();
        var watch = Stopwatch.StartNew();
        TuneCandidate? best = null;
        TuneBench? bestBench = null;
        bool outOfTime = false;
        for (int i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = candidates[i];
            if (watch.Elapsed > (budget ?? Budget)) { outOfTime = true; break; }
            int free = freeVramMb();
            if (free > 0 && c.EstVramMb > free - MarginMb)
            {
                trials.Add(new TuneTrial(c, null, isThai ? $"VRAM ว่างไม่พอ ({free:N0} MB, ต้องการ ≈ {c.EstVramMb:N0} MB + เผื่อ {MarginMb})"
                                                         : $"not enough free VRAM ({free:N0} MB, needs ≈ {c.EstVramMb:N0} MB + {MarginMb} margin)"));
                continue;
            }
            progress?.Report(isThai ? $"ปรับค่า {i + 1}/{candidates.Count}: {c.Summary(true)}…" : $"Tuning {i + 1}/{candidates.Count}: {c.Summary(false)}…");
            var b = await bench(c, ct);
            trials.Add(new TuneTrial(c, b, ""));
            if (Passes(b)) { best = c; bestBench = b; break; }
            if (b.Ok && (bestBench == null || b.GenTps > bestBench.GenTps)) { best = c; bestBench = b; }
        }
        return new TuneResult(best, bestBench, bestBench != null && Passes(bestBench), trials, outOfTime);
    }

    /// <summary>"3 ชุด: ctx 16k · … → 2.1 tok/s; …" — what was tried, for the flag's tooltip and the chat warning.</summary>
    public static string TriedSummary(TuneResult r, bool isThai)
    {
        var parts = r.Trials.Select(t => t.Bench is { Ok: true } b
            ? $"{t.Candidate.Summary(isThai)} → {(isThai ? "เขียน" : "writes")} {b.GenTps:0.#} tok/s, {(isThai ? "อ่าน" : "reads")} {b.PromptTps:0} tok/s"
            : t.Bench is { } f ? $"{t.Candidate.Summary(isThai)} → {(isThai ? "ล้มเหลว" : "failed")}: {Short(f.Error)}"
            : $"{t.Candidate.Summary(isThai)} → {(isThai ? "ข้าม" : "skipped")} ({t.Skipped})");
        return string.Join("\n", parts);
    }

    private static string Short(string s) => s.Length <= 120 ? s : s[..120] + "…";

    /// <summary>What the Models page says when a tune ends.</summary>
    public static string ResultMessage(TuneResult r, string modelName, bool isThai)
    {
        if (r.Measured == 0)
            return isThai ? $"ยังปรับค่า {modelName} ไม่ได้ — ทุกชุดถูกข้ามเพราะ VRAM ว่างไม่พอ ปิดโปรแกรมที่ใช้การ์ดจอแล้วลองใหม่ (ยังไม่ตัดสินอะไร)"
                          : $"Could not tune {modelName} — every candidate was skipped for lack of free VRAM. Close GPU programs and try again (no verdict yet)";
        if (r.Passed && r.Best is { } best && r.BestBench is { } b)
            return isThai
                ? $"✅ ปรับค่า {modelName} แล้ว: {best.Summary(true)} — เขียน {b.GenTps:0.#} tok/s, อ่าน prompt {b.PromptTps:0} tok/s · โหลดครั้งต่อไปใช้ค่านี้เอง"
                : $"✅ Tuned {modelName}: {best.Summary(false)} — writes {b.GenTps:0.#} tok/s, reads the prompt at {b.PromptTps:0} tok/s · the next load uses these settings";
        string bestPart = r.BestBench is { Ok: true } bb
            ? (isThai ? $"ดีที่สุดได้ {bb.GenTps:0.#} tok/s (เขียน), {bb.PromptTps:0} tok/s (อ่าน)" : $"the best reached {bb.GenTps:0.#} tok/s writing, {bb.PromptTps:0} tok/s reading")
            : (isThai ? "ไม่มีชุดไหนรันผ่าน" : "no candidate ran");
        return isThai
            ? $"🐢 {modelName} ช้าเกินไปสำหรับงาน agent บนเครื่องนี้แม้ปรับค่าแล้ว — ลอง {r.Measured} ชุด, {bestPart}{(r.OutOfTime ? " (หมดเวลาที่ให้)" : "")} · ยังใช้คุยธรรมดาได้ แนะนำโมเดลที่เล็กพอให้อยู่ใน VRAM หรือโมเดล API"
            : $"🐢 {modelName} is too slow for agent work on this PC even tuned — tried {r.Measured}, {bestPart}{(r.OutOfTime ? " (out of time)" : "")} · fine for plain chat; use a model that fits VRAM, or an API model";
    }

    /// <summary>The "ปรับค่าแล้ว" pill's tooltip.</summary>
    public static string ProfileText(TuneProfile p, bool isThai)
    {
        string when = p.AtUtc.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.GetCultureInfo(isThai ? "th-TH" : "en-US"));
        return isThai
            ? $"ปรับค่าให้เครื่องนี้แล้ว ({when}{(p.Gpu.Length > 0 ? $", {p.Gpu}" : "")}): {p.Settings.Summary(true)}\nเขียน {p.GenTps:0.#} tok/s · อ่าน prompt {p.PromptTps:0} tok/s · โหลดครั้งต่อไปใช้ค่านี้เอง"
            : $"Tuned for this PC ({when}{(p.Gpu.Length > 0 ? $", {p.Gpu}" : "")}): {p.Settings.Summary(false)}\nWrites {p.GenTps:0.#} tok/s · reads the prompt at {p.PromptTps:0} tok/s · loads use these settings";
    }

    // ─── the machine ───

    private static int _physicalCores;
    /// <summary>Physical cores (not logical): threads past that slow llama.cpp down. WMI, cached; half the logical count if
    /// WMI cannot answer.</summary>
    public static int PhysicalCores()
    {
        if (_physicalCores > 0) return _physicalCores;
        int n = 0;
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor");
            foreach (var o in searcher.Get()) n += Convert.ToInt32(o["NumberOfCores"]);
        }
        catch { }
        return _physicalCores = n > 0 ? n : Math.Max(1, Environment.ProcessorCount / 2);
    }

    /// <summary>What a llama-server accepts, from its --help text.</summary>
    public static (bool CpuMoe, bool FlashAttnOn) ParseServerFeatures(string? help)
    {
        if (string.IsNullOrEmpty(help)) return (false, false);
        bool cpuMoe = help.Contains("--n-cpu-moe", StringComparison.Ordinal);
        bool fa = Regex.IsMatch(help, @"--flash-attn\s*\[on\|off\|auto\]");
        return (cpuMoe, fa);
    }
}

/// <summary>Tuned profiles per model file and card (model-tune.json beside the settings).</summary>
public sealed class TuneStore
{
    private readonly string _file;
    private readonly object _gate = new();
    private Dictionary<string, TuneProfile>? _entries;

    public TuneStore(string file) => _file = file;

    private static TuneStore? _default;
    public static TuneStore Default => _default ??= new TuneStore(
        System.IO.Path.Combine(SettingsService.ResolveDataRoot(out _), "model-tune.json"));

    public void Save(TuneProfile p)
    {
        lock (_gate)
        {
            var all = Load();
            all[Key(p.Path)] = p;
            Persist(all);
        }
    }

    /// <summary>The profile for this file on this card (an unknown card on either side still matches).</summary>
    public TuneProfile? Find(string? modelPath, string? gpu)
    {
        if (string.IsNullOrEmpty(modelPath)) return null;
        lock (_gate)
        {
            if (!Load().TryGetValue(Key(modelPath), out var p)) return null;
            if (p.FileSize != SizeOf(modelPath)) return null;
            if (p.Gpu.Length > 0 && !string.IsNullOrEmpty(gpu) && !string.Equals(p.Gpu, gpu, StringComparison.OrdinalIgnoreCase)) return null;
            return p;
        }
    }

    /// <summary>The settings a load should use: a profile that passed (one that failed is only a record of what was tried).</summary>
    public TuneCandidate? SettingsFor(string? modelPath, string? gpu) => Find(modelPath, gpu) is { Passed: true } p ? p.Settings : null;

    public bool Clear(string modelPath)
    {
        lock (_gate)
        {
            var all = Load();
            if (!all.Remove(Key(modelPath))) return false;
            Persist(all);
            return true;
        }
    }

    private Dictionary<string, TuneProfile> Load()
    {
        if (_entries != null) return _entries;
        _entries = new Dictionary<string, TuneProfile>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_file))
                foreach (var p in JsonSerializer.Deserialize<List<TuneProfile>>(File.ReadAllText(_file)) ?? new())
                    if (!string.IsNullOrEmpty(p.Path)) _entries[Key(p.Path)] = p;
        }
        catch { }
        return _entries;
    }

    private void Persist(Dictionary<string, TuneProfile> all)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _file, overwrite: true);
        }
        catch { }
    }

    private static string Key(string path)
    {
        try { return System.IO.Path.GetFullPath(path).TrimEnd('\\', '/'); } catch { return path; }
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; } catch { return -1; }
    }
}
