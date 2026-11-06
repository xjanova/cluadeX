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
