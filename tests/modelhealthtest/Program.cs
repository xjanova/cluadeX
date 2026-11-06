// modelhealthtest — a local model that failed is remembered with its reason and not loaded again until Force load;
// a context window that overflows is rescued by cutting the long blocks, and an overflow that cannot be rescued says so.
// Offline: no model is loaded, no llama-server is started, no GPU is touched.
//
// Build CluadeX first (the app may be running: build it with -p:OutDir=<dir>\), then:
//   dotnet run --project tests/modelhealthtest -p:CluadeXBin=<dir>\

using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Local;
using CluadeX.Services.Providers;

int pass = 0, fail = 0;
void Check(string id, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"PASS {id}"); }
    else { fail++; Console.WriteLine($"FAIL {id} {detail}"); }
}

string root = Path.Combine(Path.GetTempPath(), "modelhealthtest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string FakeModel(string name, int bytes)
{
    string p = Path.Combine(root, name);
    File.WriteAllBytes(p, new byte[bytes]);
    return p;
}

try
{
    // ─── C: reading why a load failed, from real llama.cpp / Windows text ───
    (string text, int? code, ModelFailureKind want)[] lines =
    {
        ("ggml_backend_cuda_buffer_type_alloc_buffer: allocating 4512.00 MiB on device 0: cudaMalloc failed: out of memory", null, ModelFailureKind.OutOfMemory),
        ("llama_model_load: error loading model: unable to allocate CUDA0 buffer", null, ModelFailureKind.OutOfMemory),
        ("ggml_vulkan: Device memory allocation of size 2147483648 failed. vk::Device::allocateMemory: ErrorOutOfDeviceMemory", null, ModelFailureKind.OutOfMemory),
        ("gguf_init_from_file_impl: invalid magic characters: 'PK\x03\x04', expected 'GGUF'", null, ModelFailureKind.Corrupt),
        ("llama_model_load: error loading model: tensor 'blk.31.ffn_down.weight' data is not within the file bounds", null, ModelFailureKind.Corrupt),
        ("llama_model_load: error loading model: error loading model architecture: unknown model architecture: 'qwen35'", null, ModelFailureKind.Unsupported),
        ("llama-server did not become ready within 300 s", null, ModelFailureKind.Timeout),
        ("D:\\a\\llama.cpp\\ggml\\src\\ggml-cuda\\ggml-cuda.cu:75: GGML_ASSERT(ok) failed", null, ModelFailureKind.Crash),
        ("llama-server exited", unchecked((int)0xC0000005), ModelFailureKind.Crash),
        ("llama-server exited", unchecked((int)0xC000001D), ModelFailureKind.Crash),
        ("llama-server exited", unchecked((int)0xC0000017), ModelFailureKind.OutOfMemory),
        // a CUDA OOM usually ends in an abort: the fix is the memory, so memory wins
        ("cudaMalloc failed: out of memory | GGML_ASSERT(buf) failed | abort()", unchecked((int)0xC0000409), ModelFailureKind.OutOfMemory),
        ("something odd happened", null, ModelFailureKind.Unknown),
        ("", 0, ModelFailureKind.Unknown),
    };
    foreach (var (text, code, want) in lines)
    {
        var got = ModelHealthLog.Classify(text, code);
        Check($"C '{(text.Length > 50 ? text[..50] + "…" : text)}' {(code is { } c ? $"0x{c:X8} " : "")}→ {want}", got == want, $"got {got}");
    }

    {
        var inner = new InvalidOperationException("llama-server stopped (crashed) while loading");
        var ex = new Exception("Failed to load model", inner);
        string d = ModelHealthLog.Describe(ex, new[] { "load_tensors: loading model tensors", "cudaMalloc failed: out of memory", "main: exiting" });
        Check("C.D1 Describe keeps the exception chain", d.Contains("Failed to load model") && d.Contains("crashed"), d);
        Check("C.D2 Describe keeps only the backend lines that name a reason", d.Contains("cudaMalloc") && !d.Contains("load_tensors") && !d.Contains("exiting"), d);
        Check("C.D3 the described text classifies as memory", ModelHealthLog.Classify(d) == ModelFailureKind.OutOfMemory, ModelHealthLog.Classify(d).ToString());
    }

    {
        var gpus = new[] { new GpuInfo { Name = "NVIDIA GeForce GTX 1070 Ti", VramTotalBytes = 8L * 1073741824 } };
        Check("C.G1 GPU fingerprint names the card and its VRAM", ModelHealthLog.GpuFingerprint(gpus) == "NVIDIA GeForce GTX 1070 Ti 8 GB", ModelHealthLog.GpuFingerprint(gpus));
        Check("C.G2 no GPU list → empty fingerprint", ModelHealthLog.GpuFingerprint(null) == "");
    }

    // ─── H: the record ───
    string healthFile = Path.Combine(root, "data", "model-health.json");
    string model = FakeModel("Qwen3-Coder-30B-A3B-Instruct-Q4_K_M.gguf", 4096);
    string other = FakeModel("Llama-3.2-3B-Instruct-Q4_K_M.gguf", 2048);
    {
        var log = new ModelHealthLog(healthFile);
        Check("H1 a clean log knows nothing", log.Find(model) == null && log.All().Count == 0);
        var f1 = log.Record(model, ModelFailureKind.OutOfMemory, "cudaMalloc failed: out of memory", "NVIDIA GeForce GTX 1070 Ti 8 GB", duringRun: false);
        Check("H2 a failure is found again", log.Find(model) is { Kind: ModelFailureKind.OutOfMemory, Count: 1 }, log.Find(model)?.ToString() ?? "null");
        Check("H3 another model is not blocked by it", log.Find(other) == null);
        log.Record(model, ModelFailureKind.OutOfMemory, "again", "NVIDIA GeForce GTX 1070 Ti 8 GB", duringRun: false);
        Check("H4 a second failure counts up", log.Find(model)?.Count == 2, log.Find(model)?.Count.ToString() ?? "null");
        Check("H5 the record is on disk", File.Exists(healthFile));

        var reread = new ModelHealthLog(healthFile);
        Check("H6 a new instance (next start of CluadeX) reads it back", reread.Find(model) is { Count: 2, Kind: ModelFailureKind.OutOfMemory });
        Check("H7 the path is matched case-insensitively", reread.Find(model.ToUpperInvariant()) != null);

        // the file was replaced (downloaded again): an old failure must not block the new file
        File.WriteAllBytes(model, new byte[8192]);
        Check("H8 a file of another size is not blocked", new ModelHealthLog(healthFile).Find(model) == null);
        File.WriteAllBytes(model, new byte[4096]);
        Check("H9 the same size again is blocked again", new ModelHealthLog(healthFile).Find(model) != null);

        Check("H10 Clear removes it", reread.Clear(model) && reread.Find(model) == null);
        Check("H11 …and the removal is on disk", new ModelHealthLog(healthFile).Find(model) == null);
        Check("H12 clearing what is not there says false", !reread.Clear(model));
        _ = f1;
    }

    {
        // a damaged health file must not block every model, nor crash
        string bad = Path.Combine(root, "bad", "model-health.json");
        Directory.CreateDirectory(Path.GetDirectoryName(bad)!);
        File.WriteAllText(bad, "{ not json");
        var log = new ModelHealthLog(bad);
        Check("H13 a damaged file reads as empty", log.Find(model) == null && log.All().Count == 0);
        log.Record(model, ModelFailureKind.Crash, "x", "", false);
        Check("H14 …and is rewritten on the next failure", new ModelHealthLog(bad).Find(model)?.Kind == ModelFailureKind.Crash);
    }

    // ─── I: a load that never finished (CluadeX or the PC went down) ───
    {
        string file = Path.Combine(root, "int", "model-health.json");
        var log = new ModelHealthLog(file);
        log.MarkLoading(model, "NVIDIA GeForce GTX 1070 Ti 8 GB");
        // …the PC restarts here; the next start reads the marker
        var next = new ModelHealthLog(file);
        var lost = next.TakeInterruptedLoad();
        Check("I1 an unfinished load is recorded as a crash", lost is { Kind: ModelFailureKind.Crash } && next.Find(model) != null, lost?.ToString() ?? "null");
        Check("I2 the marker is taken once", next.TakeInterruptedLoad() == null);
        log.MarkLoading(other, "");
        log.EndLoading();
        Check("I3 a load that ended leaves no marker", new ModelHealthLog(file).TakeInterruptedLoad() == null && new ModelHealthLog(file).Find(other) == null);
    }

    // ─── M: what the owner is told ───
    {
        var f = new ModelFailure(model, 4096, DateTime.UtcNow, ModelFailureKind.OutOfMemory, "cudaMalloc failed: out of memory",
            "NVIDIA GeForce GTX 1070 Ti 8 GB", 2, false);
        string th = ModelHealthLog.FailedMessage(f, isThai: true);
        Check("M1 the failure says the reason in Thai", th.Contains("หน่วยความจำไม่พอ") && th.Contains("Force load") && th.Contains("Qwen3-Coder-30B-A3B"), th);
        Check("M2 …with a way out", th.Contains("GPU layers") && th.Contains("cudaMalloc"), th);

        string sameGpu = ModelHealthLog.BlockedMessage(f, "NVIDIA GeForce GTX 1070 Ti 8 GB", isThai: true, out string shortTh);
        Check("M3 the refusal names the earlier failure and Force load", sameGpu.Contains("เคยโหลดไม่สำเร็จ") && sameGpu.Contains("Force load") && sameGpu.Contains("2 ครั้ง"), sameGpu);
        Check("M4 same card → no 'the card changed' line", !sameGpu.Contains("การ์ดจอเปลี่ยน"), sameGpu);
        Check("M5 the short message fits a status line", shortTh.Length < 120 && shortTh.Contains("เคยล้มเหลว"), shortTh);

        string newGpu = ModelHealthLog.BlockedMessage(f, "NVIDIA GeForce RTX 4090 24 GB", isThai: false, out _);
        Check("M6 a new card is pointed out as a reason to try again", newGpu.Contains("graphics card has changed") && newGpu.Contains("RTX 4090") && newGpu.Contains("Force load"), newGpu);
        string unknownGpu = ModelHealthLog.BlockedMessage(f, "", isThai: false, out _);
        Check("M7 an unknown current card is not called a change", !unknownGpu.Contains("has changed"), unknownGpu);

        var run = f with { DuringRun = true, Kind = ModelFailureKind.Crash };
        Check("M8 a death mid-answer is told as such", ModelHealthLog.FailedMessage(run, true).Contains("หยุดทำงานกลางคัน")
            && ModelHealthLog.FailedMessage(run, false).Contains("stopped mid-answer"));
        foreach (ModelFailureKind k in Enum.GetValues<ModelFailureKind>())
            Check($"M9 {k} has a Thai reason and advice", ModelHealthLog.ReasonText(k, true).Any(ch => ch >= '\u0E00' && ch <= '\u0E7F')
                && ModelHealthLog.AdviceText(k, true).Length > 0 && ModelHealthLog.ReasonText(k, false) != ModelHealthLog.ReasonText(k, true));
    }

    // ─── P: the real provider refuses a model that failed, before touching any backend ───
    {
        var settings = new SettingsService();   // portable.txt keeps it next to this exe
        var server = new LlamaServerProvider(settings);
        var provider = new LocalGgufProvider(new LlamaInferenceService(settings), server, settings);
        string file = Path.Combine(root, "prov", "model-health.json");
        provider.Health = new ModelHealthLog(file);
        provider.CurrentGpu = () => "NVIDIA GeForce RTX 4090 24 GB";
        provider.Health.Record(model, ModelFailureKind.OutOfMemory, "cudaMalloc failed: out of memory", "NVIDIA GeForce GTX 1070 Ti 8 GB", false);

        var statuses = new List<string>();
        provider.OnStatusChanged += statuses.Add;
        Exception? caught = null;
        try { await provider.LoadModelAsync(model); } catch (Exception ex) { caught = ex; }
        Check("P1 a model that failed is refused", caught is ModelBlockedException, caught?.GetType().Name ?? "no exception");
        var blocked = caught as ModelBlockedException;
        Check("P2 the refusal carries the reason and the GPU change", blocked != null && blocked.Message.Contains("RTX 4090") && blocked.Failure.Kind == ModelFailureKind.OutOfMemory,
            blocked?.Message ?? "");
        Check("P3 the status line gets the short message", statuses.Any(s => s == blocked?.ShortMessage), string.Join(" / ", statuses));
        Check("P4 the refusal left no loading marker", !File.Exists(file + ".loading"));
        Check("P5 nothing was loaded", !provider.IsReady && !provider.IsLoading && !server.IsServerRunning);

        // the interrupted-load marker is reported through OnError at the next load attempt
        provider.Health.MarkLoading(other, "x");
        var errors = new List<string>();
        provider.OnError += errors.Add;
        try { await provider.LoadModelAsync(model); } catch { }
        Check("P6 a load that never finished is told at the next attempt", errors.Any(e => e.Contains("Llama-3.2-3B")), string.Join(" / ", errors));
        Check("P7 …and that model is now remembered too", provider.Health.Find(other)?.Kind == ModelFailureKind.Crash);
    }

    // ─── S: a model too slow for agent work on this PC (warned, never blocked) ───
    {
        Check("S1 2.1 tok/s over 200 tokens is slow writing", ModelSpeedLog.Judge(9000, 400, 200, 2.1, 100) == SlowReason.SlowGeneration);
        Check("S2 a 10-token answer is too small to judge the writing speed", ModelSpeedLog.Judge(300, 400, 10, 1.5, 7) == SlowReason.None);
        Check("S3 30 tok/s over a 12k prompt is slow reading", ModelSpeedLog.Judge(12000, 30, 300, 9, 400) == SlowReason.SlowPrompt);
        Check("S4 a 3½-minute step is slow", ModelSpeedLog.Judge(500, 300, 20, 8, 210) == SlowReason.SlowStep);
        Check("S5 a healthy 7B step (8k @ 600 tok/s, 300 @ 25 tok/s, 26 s) is fine", ModelSpeedLog.Judge(8000, 600, 300, 25, 26) == SlowReason.None);

        string file = Path.Combine(root, "speed", "model-speed.json");
        var log = new ModelSpeedLog(file);
        const string gtx = "NVIDIA GeForce GTX 1070 Ti 8 GB", rtx = "NVIDIA GeForce RTX 4090 24 GB";
        Check("S6 nothing measured → not slow", log.FindSlow(model, gtx) == null && log.FindSlow(null, gtx) == null);
        var untunedSlow = log.Observe(model, gtx, 12000, 31.5, 250, 2.4, 290);
        Check("S6b a slow run on untuned settings is NOT the flag — it suggests a tune", untunedSlow is { IsSlow: false, NeedsTune: true }
            && log.FindSlow(model, gtx) == null && log.FindNeedsTune(model, gtx) != null, untunedSlow?.ToString() ?? "null");
        string tuneHint = ModelSpeedLog.NeedsTuneWarning(untunedSlow!, model, isThai: true);
        Check("S6c the suggestion names the tune button and what it tries", tuneHint.Contains("ปรับค่าให้เหมาะ") && tuneHint.Contains("MoE") && tuneHint.Contains("2.4"), tuneHint);
        Check("S6d the card text for it says not tuned yet", ModelSpeedLog.Describe(untunedSlow!, true).StartsWith("ยังไม่ได้ปรับค่า"));
        var slow = log.Observe(model, gtx, 12000, 31.5, 250, 2.4, 290, tuned: true);
        Check("S7 a slow step sets the verdict", slow is { IsSlow: true, Reason: SlowReason.SlowGeneration } && log.FindSlow(model, gtx) != null, slow?.ToString() ?? "null");
        Check("S8 another card ignores it", log.FindSlow(model, rtx) == null);
        Check("S9 an unknown current card (UI before nvidia-smi) still shows it", log.FindSlow(model, "") != null);
        Check("S10 another model is untouched", log.FindSlow(other, gtx) == null);
        Check("S11 the verdict survives a restart", new ModelSpeedLog(file).FindSlow(model, gtx)?.GenTps == 2.4);
        log.Observe(model, gtx, 200, 500, 12, 30, 2);
        Check("S12 a tiny step does not clear it (too small to tell)", log.FindSlow(model, gtx) != null);
        log.Observe(model, gtx, 9000, 700, 300, 22, 25);
        Check("S13 a later fast run on the same card clears it", log.FindSlow(model, gtx) == null && log.Find(model)?.GenTps == 22);

        Check("S14a no answer on untuned settings only suggests a tune", log.RecordNoAnswer(model, gtx, 300, 12435) is { NeedsTune: true, IsSlow: false });
        var none = log.RecordNoAnswer(model, gtx, 300, 12435, tuned: true);
        Check("S14 no answer in the budget marks it slow", log.FindSlow(model, gtx) is { Reason: SlowReason.NoAnswer } && none.StepSec == 300);
        string nums = ModelSpeedLog.Numbers(none, true);
        Check("S15 the numbers say how long and how big", nums.Contains("5 นาที") && nums.Contains("12,435"), nums);
        File.WriteAllBytes(model, new byte[8192]);
        Check("S16 a replaced file (another size) is not called slow", log.FindSlow(model, gtx) == null);
        File.WriteAllBytes(model, new byte[4096]);
        Check("S17 Clear forgets it (the pill's ×, Force load)", log.Clear(model) && log.FindSlow(model, gtx) == null && !log.Clear(model));

        var rec = log.Observe(model, gtx, 12000, 31.5, 250, 2.4, 290, tuned: true)!;
        string warnTh = ModelSpeedLog.PreRunWarning(rec, model, escalationOn: false, isThai: true);
        Check("S18 the pre-run warning names the model, the numbers and what to use instead", warnTh.Contains("Qwen3-Coder-30B-A3B-Instruct-Q4_K_M")
            && warnTh.Contains("2.4 tok/s") && warnTh.Contains("VRAM") && warnTh.Contains("API") && !warnTh.Contains("ส่งต่อ"), warnTh);
        Check("S19 with escalation on it says a stronger model takes over", ModelSpeedLog.PreRunWarning(rec, model, true, false).Contains("stronger model"));
        string card = ModelSpeedLog.Describe(rec, isThai: true);
        Check("S20 the card text says it still loads, the card it was measured on, and how it clears", card.Contains("ยังโหลด") && card.Contains(gtx)
            && card.Contains("หายเอง") && card.Contains("2.4"), card);
        foreach (SlowReason r in Enum.GetValues<SlowReason>().Where(r => r != SlowReason.None))
            Check($"S21 {r} has a Thai and an English reason", ModelSpeedLog.ReasonText(r, true).Any(ch => ch >= '฀' && ch <= '๿') && ModelSpeedLog.ReasonText(r, false).Length > 0);

        // llama-server's own answer (b6xxx–b12xxx shape)
        const string answer = """{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null}}],"usage":{"prompt_tokens":12435,"completion_tokens":96},"timings":{"cache_n":0,"prompt_n":12435,"prompt_ms":402311.2,"prompt_per_token_ms":32.35,"prompt_per_second":30.91,"predicted_n":96,"predicted_ms":41203.4,"predicted_per_token_ms":429.2,"predicted_per_second":2.33}}""";
        var t = LlamaServerProvider.ParseTimings(answer);
        Check("S22 llama-server timings are read", t is { PromptN: 12435, GenN: 96 } && Math.Abs(t.Value.PromptTps - 30.91) < 0.01 && Math.Abs(t.Value.GenTps - 2.33) < 0.01, t?.ToString() ?? "null");
        Check("S23 an answer without timings, or not JSON, gives none", LlamaServerProvider.ParseTimings("""{"choices":[]}""") == null
            && LlamaServerProvider.ParseTimings("<html>") == null && LlamaServerProvider.ParseTimings(null) == null);
        Check("S24 those timings (the Sudoku2 kind of run) judge slow", ModelSpeedLog.Judge(t!.Value.PromptN, t.Value.PromptTps, t.Value.GenN, t.Value.GenTps, 443) == SlowReason.SlowGeneration);

        var settings = new SettingsService();
        var server = new LlamaServerProvider(settings);
        var provider = new LocalGgufProvider(new LlamaInferenceService(settings), server, settings);
        var mine = new ModelSpeedLog(Path.Combine(root, "speed2", "model-speed.json"));
        provider.Speed = mine;
        Check("S25 the provider and its llama-server share one speed log", ReferenceEquals(server.Speed, mine));
        Check("S26 nothing loaded → no model path, no step speed", provider.LoadedModelPath == null && provider.LastStepSpeed == null && server.LoadedModelPath == null);
        Check("S27 the cached GPU is empty before anyone asked nvidia-smi (UI thread safe)", server.CachedGpuFingerprint == "");
    }

    // ─── T: tuning the load settings before any "slow here" flag (no model is loaded: fake shapes and fake benchmarks) ───
    {
        // a real GGUF header (no tensors): Qwen3-30B-A3B's shape keys
        string gguf = Path.Combine(root, "shape-moe.gguf");
        using (var bw = new BinaryWriter(File.Create(gguf)))
        {
            void Str(string v) { var b = System.Text.Encoding.UTF8.GetBytes(v); bw.Write((ulong)b.Length); bw.Write(b); }
            var kv = new List<(string K, object V)>
            {
                ("general.architecture", "qwen3moe"), ("general.name", "Qwen3 Coder 30B A3B"),
                ("qwen3moe.block_count", 48u), ("qwen3moe.expert_count", 128u), ("qwen3moe.expert_used_count", 8u),
                ("qwen3moe.attention.head_count", 32u), ("qwen3moe.attention.head_count_kv", 4u),
                ("qwen3moe.attention.key_length", 128u), ("qwen3moe.attention.value_length", 128u), ("qwen3moe.embedding_length", 2048u),
                ("tokenizer.ggml.model", "gpt2"),
            };
            bw.Write(0x46554747u); bw.Write(3u); bw.Write(0UL); bw.Write((ulong)kv.Count);
            foreach (var (k, v) in kv)
            {
                Str(k);
                if (v is string sv) { bw.Write(8u); Str(sv); } else { bw.Write(4u); bw.Write((uint)v); }
            }
        }
        var header = CluadeX.Services.GgufMetadataReader.TryReadHeader(gguf);
        Check("T1 the GGUF header gives layers, experts and KV width", header is { Architecture: "qwen3moe", BlockCount: 48, ExpertCount: 128, ExpertUsedCount: 8, HeadCountKv: 4, KeyLength: 128 },
            header?.ToString() ?? "null");
        var shapeFile = LoadTuner.ShapeOf(gguf, 3.3 / 30.5);
        Check("T2 its shape: MoE, 48 layers, KV width 4×(128+128)", shapeFile is { IsMoe: true, Layers: 48, KvWidth: 1024 }, shapeFile?.ToString() ?? "null");
        double dense = LoadTuner.DenseShareOf(3.3 / 30.5, 128, 8);
        Check("T3 30B-A3B is ≈ 5 % non-expert (catalog active ÷ total)", dense > 0.04 && dense < 0.06, dense.ToString("0.000"));
        Check("T4 no catalog numbers → 8 % assumed", LoadTuner.DenseShareOf(null, 128, 8) == 0.08);
        Check("T5 not a GGUF → no shape", LoadTuner.ShapeOf(FakeModel("junk.gguf", 64)) == null);

        var moe30 = new ModelShape("qwen3moe", 18_556_689_568, 48, 128, 8, 1024, dense);
        var dense7 = new ModelShape("qwen2", 4_683_073_184, 28, 0, 0, 1024, 1.0);
        var dense14 = new ModelShape("qwen2", 8_988_110_976, 48, 0, 0, 1024, 1.0);
        var gtx8 = new TuneHardware(7000, 8192, 6, CpuMoe: true, FlashAttn: true, SettingsContext: 16384, SettingsBatch: 2048);
        var rtx24 = gtx8 with { FreeVramMb = 22000, TotalVramMb = 24576 };

        var c8 = LoadTuner.Pick(moe30, gtx8);
        Check("T6 30B-A3B on 8 GB: a few candidates, none over free VRAM − margin", c8.Count is >= 2 and <= LoadTuner.MaxCandidates
            && c8.All(c => c.EstVramMb <= gtx8.FreeVramMb - LoadTuner.MarginMb), string.Join(" | ", c8.Select(c => $"{c.Summary(false)} {c.EstVramMb}MB")));
        Check("T7 …attention on the GPU, experts of the first layers on the CPU", c8.All(c => c.GpuLayers == 999 && c.CpuMoeLayers > 0 && c.CpuMoeLayers < 48),
            string.Join(",", c8.Select(c => c.CpuMoeLayers)));
        Check("T8 …the owner's window first, then smaller, never below the agent floor", c8[0].Context == 16384 && c8.Any(c => c.Context == 8192)
            && c8.All(c => c.Context >= LoadTuner.MinAgentContext) && c8.Zip(c8.Skip(1)).All(p => p.Second.Context <= p.First.Context));
        Check("T9 …a smaller window puts more experts on the GPU", c8.Last(c => c.UBatch == 512).CpuMoeLayers <= c8[0].CpuMoeLayers);
        Check("T10 …KV q8 + flash attention, physical cores", c8.All(c => c.KvQ8 && c.FlashAttn && c.Threads == 6));
        var launchArgs = c8[0].Args();
        Check("T11 the launch arguments carry --n-cpu-moe, -fa on, q8_0 KV, -ub", string.Join(" ", launchArgs).Contains($"--n-cpu-moe {c8[0].CpuMoeLayers}")
            && string.Join(" ", launchArgs).Contains("-fa on") && string.Join(" ", launchArgs).Contains("-ctk q8_0 -ctv q8_0") && launchArgs.Contains("-ub"), string.Join(" ", launchArgs));

        var c24 = LoadTuner.Pick(moe30, rtx24);
        Check("T12 the same model on 24 GB: one candidate, all on the GPU", c24.Count == 1 && c24[0] is { GpuLayers: 999, CpuMoeLayers: 0, Context: 16384 },
            string.Join(" | ", c24.Select(c => c.Summary(false))));
        var c7 = LoadTuner.Pick(dense7, gtx8);
        Check("T13 a dense 7B on 8 GB fits whole: one candidate", c7.Count == 1 && c7[0].GpuLayers == 999 && c7[0].CpuMoeLayers == 0);
        var c14 = LoadTuner.Pick(dense14, gtx8);
        Check("T14 a dense 14B on 8 GB offloads layers (no --n-cpu-moe for a dense model)", c14.Count >= 2 && c14.All(c => c.CpuMoeLayers == 0 && c.GpuLayers is > 0 and < 48)
            && c14.All(c => c.EstVramMb <= gtx8.FreeVramMb - LoadTuner.MarginMb), string.Join(",", c14.Select(c => c.GpuLayers)));
        Check("T15 …more layers on the GPU as the window shrinks", c14.Zip(c14.Skip(1)).All(p => p.Second.GpuLayers >= p.First.GpuLayers), string.Join(",", c14.Select(c => c.GpuLayers)));
        var oldServer = gtx8 with { CpuMoe = false, FlashAttn = false };
        var cOld = LoadTuner.Pick(moe30, oldServer);
        Check("T16 a llama-server without --n-cpu-moe / -fa on: plain layer offload, f16 KV, no -fa", cOld.All(c => c.CpuMoeLayers == 0 && c.GpuLayers < 999 && !c.KvQ8)
            && !cOld[0].Args().Contains("-fa"));
        var tiny = LoadTuner.Pick(moe30, gtx8 with { FreeVramMb = 2000 });
        Check("T17 little free VRAM: still never over it", tiny.All(c => c.EstVramMb <= 2000 - LoadTuner.MarginMb), string.Join(",", tiny.Select(c => c.EstVramMb)));

        const string helpNew = "-fa,   --flash-attn [on|off|auto]       set Flash Attention use\n-ncmoe, --n-cpu-moe N   keep the MoE weights of the first N layers in the CPU";
        Check("T18 the server's --help is read for --n-cpu-moe and -fa on", LoadTuner.ParseServerFeatures(helpNew) == (true, true)
            && LoadTuner.ParseServerFeatures("-fa,    --flash-attn             enable Flash Attention") == (false, false)
            && LoadTuner.ParseServerFeatures(null) == (false, false));

        // ─── measuring (fake benchmarks) ───
        TuneBench Slow() => new(true, 1500, 30, 96, 2.2, 20);
        TuneBench Fast() => new(true, 1500, 300, 96, 9.5, 20);
        var cands = c8;
        int calls = 0;
        var early = await LoadTuner.RunAsync(cands, (c, t) => { calls++; return Task.FromResult(calls == 2 ? Fast() : Slow()); }, () => 7000, null, true, CancellationToken.None);
        Check("T19 it stops at the first candidate that passes", early.Passed && calls == 2 && early.Best == cands[1] && early.Measured == 2, $"calls {calls}, passed {early.Passed}");
        var allSlow = await LoadTuner.RunAsync(cands, (c, t) => Task.FromResult(c == cands[^1] ? Slow() with { GenTps = 3.1 } : Slow()), () => 7000, null, true, CancellationToken.None);
        Check("T20 nothing fast enough: every candidate measured, the best writer kept, not passed", !allSlow.Passed && allSlow.Measured == cands.Count && allSlow.Best == cands[^1]);
        Check("T21 a failed load is a failed bench, the tune goes on", (await LoadTuner.RunAsync(cands, (c, t) => Task.FromResult(c == cands[0] ? TuneBench.Failed("cudaMalloc failed: out of memory") : Fast()),
            () => 7000, null, true, CancellationToken.None)) is { Passed: true, Measured: 2 });
        var skipped = await LoadTuner.RunAsync(cands, (c, t) => Task.FromResult(Fast()), () => 1500, null, true, CancellationToken.None);
        Check("T22 free VRAM is read before every candidate: what does not fit is skipped, never loaded", skipped.Measured == 0 && skipped.Trials.All(t => t.Skipped.Contains("VRAM")));
        int timed = 0;
        var outOfTime = await LoadTuner.RunAsync(cands, async (c, t) => { timed++; await Task.Delay(60, t); return Slow(); }, () => 7000, null, true, CancellationToken.None, TimeSpan.FromMilliseconds(30));
        Check("T23 the time budget ends the tune", outOfTime.OutOfTime && timed == 1, $"ran {timed}");
        var progressLines = new List<string>();
        await LoadTuner.RunAsync(cands.Take(1).ToList(), (c, t) => Task.FromResult(Fast()), () => 7000, new SyncProgress(progressLines), true, CancellationToken.None);
        Check("T24 progress says which candidate runs", progressLines.Count == 1 && progressLines[0].StartsWith("ปรับค่า 1/1"), string.Join(" / ", progressLines));

        using (var cts = new CancellationTokenSource())
        {
            bool threw = false;
            try { await LoadTuner.RunAsync(cands, (c, t) => { cts.Cancel(); t.ThrowIfCancellationRequested(); return Task.FromResult(Fast()); }, () => 7000, null, true, cts.Token); }
            catch (OperationCanceledException) { threw = true; }
            Check("T25 cancelling throws (a cancel is not a verdict)", threw);
        }

        // ─── the provider: profile saved and reused, flag only after a tune ───
        var settings = new SettingsService();
        var server = new LlamaServerProvider(settings);
        var provider = new LocalGgufProvider(new LlamaInferenceService(settings), server, settings)
        {
            Speed = new ModelSpeedLog(Path.Combine(root, "tune", "model-speed.json")),
            Tunes = new TuneStore(Path.Combine(root, "tune", "model-tune.json")),
            FreeVramMb = () => 7000,
            CurrentGpu = () => "NVIDIA GeForce GTX 1070 Ti 8 GB",
        };
        const string gpu8 = "NVIDIA GeForce GTX 1070 Ti 8 GB";
        var plan = new LocalGgufProvider.TunePlan(model, moe30, gtx8, cands, 6, gpu8);
        int n = 0;
        var good = await provider.RunTuneAsync(plan, null, CancellationToken.None, (c, t) => { n++; return Task.FromResult(n == 2 ? Fast() : Slow()); });
        Check("T26 a tune that passes saves the profile for this file + card", good.Passed && provider.Tunes.SettingsFor(model, gpu8) == cands[1]);
        Check("T27 …which the next load uses (the launcher asks SettingsFor), not on another card", provider.Tunes.SettingsFor(model, "NVIDIA GeForce RTX 4090 24 GB") == null
            && provider.Tunes.SettingsFor(model, "") == cands[1]);
        Check("T28 …no flag, the model counts as tuned here", provider.Speed.FindSlow(model, gpu8) == null && provider.Speed.FindNeedsTune(model, gpu8) == null && provider.TunedHere(model));
        Check("T29 a re-downloaded file has no profile", WithSize(model, 8192, () => provider.Tunes.SettingsFor(model, gpu8) == null));

        var bad = await provider.RunTuneAsync(plan, null, CancellationToken.None, (c, t) => Task.FromResult(Slow()));
        var flag = provider.Speed.FindSlow(model, gpu8);
        Check("T30 even the best candidate too slow → only now the flag, with what was tried", !bad.Passed && flag is { IsSlow: true, Tuned: true } && flag.Tried.Contains("ctx 16k"),
            flag?.ToString() ?? "null");
        Check("T31 a profile that failed is not applied to loads (Settings stay)", provider.Tunes.SettingsFor(model, gpu8) == null && provider.TunedHere(model));
        string flagText = ModelSpeedLog.Describe(flag!, true);
        Check("T32 the flag's text says it was tuned and lists the tries", flagText.Contains("แม้ปรับค่าแล้ว") && flagText.Contains("ลองแล้ว") && flagText.Contains("tok/s"), flagText);
        provider.Speed.Observe(model, gpu8, 12000, 31, 200, 2.3, 300);
        Check("T33 a later slow untuned step does not turn the flag back into a suggestion", provider.Speed.FindSlow(model, gpu8) != null);
        provider.Speed.Observe(model, gpu8, 9000, 700, 300, 22, 25, tuned: true);
        Check("T34 a later fast step on this card still clears the flag", provider.Speed.FindSlow(model, gpu8) == null);

        provider.Tunes.Clear(model); provider.Speed.Clear(model);
        using (var cts = new CancellationTokenSource())
        {
            bool threw = false;
            try { await provider.RunTuneAsync(plan, null, cts.Token, (c, t) => { cts.Cancel(); t.ThrowIfCancellationRequested(); return Task.FromResult(Slow()); }); }
            catch (OperationCanceledException) { threw = true; }
            Check("T35 a cancelled tune saves nothing and flags nothing", threw && provider.Tunes.Find(model, gpu8) == null && provider.Speed.Find(model) == null && !provider.IsTuning);
        }
        var none2 = await provider.RunTuneAsync(plan with { }, null, CancellationToken.None, (c, t) => Task.FromResult(Fast()));
        provider.Tunes.Clear(model); provider.Speed.Clear(model);
        provider.FreeVramMb = () => 1000;
        var noRoom = await provider.RunTuneAsync(plan, null, CancellationToken.None, (c, t) => Task.FromResult(Fast()));
        Check("T36 all skipped for VRAM: no profile, no verdict", noRoom.Measured == 0 && provider.Tunes.Find(model, gpu8) == null && provider.Speed.Find(model) == null);
        _ = none2;

        string okMsg = LoadTuner.ResultMessage(good, "Qwen3-Coder-30B-A3B", true);
        Check("T37 the result message: tuned, the settings and the numbers", okMsg.Contains("ปรับค่า Qwen3-Coder-30B-A3B แล้ว") && okMsg.Contains("9.5 tok/s") && okMsg.Contains("โหลดครั้งต่อไป"), okMsg);
        string badMsg = LoadTuner.ResultMessage(bad, "Qwen3-Coder-30B-A3B", true);
        Check("T38 …or slow even tuned, how many tried and the best reached", badMsg.Contains("แม้ปรับค่าแล้ว") && badMsg.Contains($"ลอง {bad.Measured} ชุด") && badMsg.Contains("2.2"), badMsg);
        Check("T39 …or nothing could run for VRAM (no verdict)", LoadTuner.ResultMessage(noRoom, "X", true).Contains("ยังไม่ตัดสิน"));
        string bench = LlamaServerProvider.BenchPrompt();
        Check("T40 the benchmark prompt is fixed and about the planned size", bench == LlamaServerProvider.BenchPrompt() && bench.Length >= LoadTuner.BenchPromptTokens * 2.5 && bench.Length < LoadTuner.BenchPromptTokens * 4.5);
    }

    // ─── R: rescuing an overflowing context ───
    {
        string page = string.Concat(Enumerable.Repeat("<div class=\"cell\" data-r=\"1\"></div>\n", 400)); // ≈15 KB, the Sudoku index.html
        var input = JsonSerializer.SerializeToElement(new { path = "index.html", content = page });
        var msgs = new List<NativeMessage>
        {
            new() { Role = "user", Content = { new ContentBlock { Type = "text", Text = "สร้าง เกม sudoku แบบเล่นผ่านเว็บ" } } },
            new() { Role = "assistant", Content = { new ContentBlock { Type = "tool_use", Id = "t1", Name = "write_file", Input = input } } },
            new() { Role = "user", Content = { new ContentBlock { Type = "tool_result", ToolUseId = "t1", Content = "Wrote index.html" } } },
            new() { Role = "assistant", Content = { new ContentBlock { Type = "tool_use", Id = "t2", Name = "read_file",
                Input = JsonSerializer.SerializeToElement(new { path = "index.html" }) } } },
            new() { Role = "user", Content = { new ContentBlock { Type = "tool_result", ToolUseId = "t2", Content = page } } },
        };
        int before = msgs.Sum(m => m.Content.Sum(b => (b.Content?.Length ?? 0) + (b.Input?.GetRawText().Length ?? 0)));
        int freed = ContextRescue.ShrinkLargeBlocks(msgs, ContextRescue.FirstCut);
        var w = msgs[1].Content[0].Input!.Value;
        string cutContent = w.GetProperty("content").GetString()!;
        Check("R1 something was freed", freed > 20000, $"freed {freed} of {before}");
        Check("R2 the write_file content is cut to about the limit", cutContent.Length < ContextRescue.FirstCut + 300 && cutContent.Contains("not repeated here"), cutContent.Length.ToString());
        Check("R3 the other tool_use arguments survive", w.GetProperty("path").GetString() == "index.html");
        Check("R4 the read_file result is cut with a way to get the rest", msgs[4].Content[0].Content!.Length < ContextRescue.FirstCut + 300
            && msgs[4].Content[0].Content!.Contains("offset/limit"), msgs[4].Content[0].Content!.Length.ToString());
        Check("R5 the cut keeps the head and the tail", msgs[4].Content[0].Content!.StartsWith("<div") && msgs[4].Content[0].Content!.TrimEnd().EndsWith("</div>"));
        Check("R6 short blocks and the user's text are untouched", msgs[2].Content[0].Content == "Wrote index.html"
            && msgs[0].Content[0].Text == "สร้าง เกม sudoku แบบเล่นผ่านเว็บ" && msgs[3].Content[0].Input!.Value.GetProperty("path").GetString() == "index.html");
        Check("R7 the cut tool_use input is still valid JSON for the API", msgs[1].Content[0].Input!.Value.ValueKind == JsonValueKind.Object);
        Check("R8 tool ids are kept (pairs stay matched)", msgs[1].Content[0].Id == "t1" && msgs[4].Content[0].ToolUseId == "t2");

        int freed2 = ContextRescue.ShrinkLargeBlocks(msgs, ContextRescue.SecondCut);
        Check("R9 the second cut frees more", freed2 > 3000 && msgs[4].Content[0].Content!.Length < ContextRescue.SecondCut + 300, $"freed {freed2}");
        Check("R10 a third pass at the same limit frees nothing (the loop then stops)", ContextRescue.ShrinkLargeBlocks(msgs, ContextRescue.SecondCut) == 0);

        string stopTh = ContextRescue.OverflowStopMessage(16384, isLocal: true, steps: 2, isThai: true);
        Check("R11 an overflow that cannot be rescued is not called done", stopTh.Contains("context ของโมเดลเต็ม") && stopTh.Contains("16,384")
            && stopTh.Contains("Context Size") && stopTh.Contains("ยังไม่เสร็จ"), stopTh);
        Check("R12 for an API model it does not talk about VRAM", !ContextRescue.OverflowStopMessage(200000, false, 5, false).Contains("VRAM"));
        string silent = ContextRescue.SilentAfterCompactMessage(16384, 2, 3, isThai: true);
        string slowTh = ContextRescue.LocalTooSlowMessage(@"D:\DataAiModel\Qwen3-Coder-30B-A3B-Instruct-Q4_K_M.gguf", 300, 12435, 1, isThai: true);
        Check("R14 a local model too slow to answer says so, with the ways out", slowTh.Contains("Qwen3-Coder-30B-A3B-Instruct-Q4_K_M ตอบไม่ทัน")
            && slowTh.Contains("5 นาที") && slowTh.Contains("12,435") && slowTh.Contains("VRAM") && slowTh.Contains("หมดเวลาเมื่อไม่มีการตอบ") && !slowTh.Contains(".gguf"), slowTh);
        string slowEn = ContextRescue.LocalTooSlowMessage(null, 45, 900, 2, isThai: false);
        Check("R15 …in English, without a model name, in seconds", slowEn.Contains("the local model did not answer") && slowEn.Contains("45 s") && slowEn.Contains("Idle timeout"), slowEn);
        Check("R13 a silent end after compaction is told as maybe-unfinished", silent.Contains("อาจยังไม่เสร็จ") && silent.Contains("2 ครั้ง"), silent);
    }
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;

static bool WithSize(string path, int size, Func<bool> check)
{
    long before = new FileInfo(path).Length;
    File.WriteAllBytes(path, new byte[size]);
    try { return check(); }
    finally { File.WriteAllBytes(path, new byte[before]); }
}

/// <summary>Progress&lt;T&gt; posts to a SynchronizationContext later; the check needs the lines now.</summary>
sealed class SyncProgress : IProgress<string>
{
    private readonly List<string> _lines;
    public SyncProgress(List<string> lines) => _lines = lines;
    public void Report(string value) => _lines.Add(value);
}
