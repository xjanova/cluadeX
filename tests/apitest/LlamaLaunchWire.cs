// llama-server's launch line for memory and speed (owner 2026-11-10, after the llama-modchip / Colibri study: "เอามา
// เฉพาะส่วนดี"): a MoE bigger than the card keeps the experts of its first layers in RAM (--n-cpu-moe) instead of -ngl
// 999 running out of VRAM; the KV cache is q8_0 (same type for K and V) with flash attention; -t is the physical core
// count; requests keep their prompt cache and the server reuses chunks after a compaction cut. Checked offline against
// the REAL builder and reader: synthetic GGUF files on disk, the exact argument lists. No server is started, no model
// is loaded (owner rule: no long GPU runs on this PC). The one process run is the installed build's --help, if present.
using System.IO;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;
using CluadeX.ViewModels;

namespace ApiTest;

internal static class LlamaLaunchWire
{
    const long MiB = 1024 * 1024;

    // Lines as llama-server build 12144 (0.9.0-dev, 2026-11) prints them.
    const string NewHelp = """
        -t,    --threads N                      number of CPU threads to use during generation (default: -1)
        -fa,   --flash-attn [on|off|auto]       set Flash Attention use ('on', 'off', or 'auto', default: 'auto')
        -ctk,  --cache-type-k TYPE              KV cache data type for K
        -ctv,  --cache-type-v TYPE              KV cache data type for V
        -ncmoe, --n-cpu-moe N                   keep the Mixture of Experts (MoE) weights of the first N layers in the
        -fit,  --fit [on|off]                   whether to adjust unset arguments to fit in device memory ('on' or
        -fitt, --fit-target MiB0,MiB1,MiB2,...
        --cache-reuse N                         min chunk size to attempt reusing from the cache via KV shifting,
        """;
    // A 2024 build: -fa took no value, no --n-cpu-moe, no --fit.
    const string OldHelp = """
        -t,    --threads N                      number of threads to use during generation (default: -1)
        -fa,   --flash-attn                     enable Flash Attention (default: disabled)
        -ctk,  --cache-type-k TYPE              KV cache data type for K (default: f16)
        -ctv,  --cache-type-v TYPE              KV cache data type for V (default: f16)
        """;

    static readonly LlamaServerFeatures All = LlamaServerFeatures.Parse(NewHelp);

    /// <summary>A Qwen3-30B-A3B-shaped MoE: 48 layers, 360 MiB of routed experts each (~17 GiB), 1.2 GiB besides, 4 KV heads of 128.</summary>
    static GgufMoeFacts Moe30B(int blocks = 48, long expertMiB = 360, long nonExpertMiB = 1200) =>
        new("qwen3moe", blocks, 128, Enumerable.Repeat(expertMiB * MiB, blocks).ToArray(), nonExpertMiB * MiB, blocks * 4 * 128, blocks * 4 * 128);

    static LlamaLaunchInput Gtx1070Ti(GgufMoeFacts m) => new()
    {
        GpuLayerSetting = -1, FreeVramMB = 7600, ContextTokens = 12288, BatchSize = 512,
        PhysicalCores = 6, LogicalCores = 12, KvCacheSetting = "auto", Features = All, Model = m,
    };

    static string Line(LlamaLaunchPlan p) => string.Join(' ', p.Args);

    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · llama-server launch line (MoE experts to RAM, KV q8_0, physical cores, prompt cache)");
        Features(check);
        Reader(check);
        Plans(check);
        KvAndThreads(check);
        Fallback(check);
        RequestAndSettings(check);
        InstalledBuild(check);
        RealFile(check);
        return Task.CompletedTask;
    }

    /// <summary>CLUADEX_GGUF=&lt;a real model&gt;: its header and tensor table (no weights) through the reader and the
    /// builder, for an 8 GB card — the line this PC would get. Skipped when unset.</summary>
    static void RealFile(Action<string, string, bool, string> check)
    {
        string path = Environment.GetEnvironmentVariable("CLUADEX_GGUF");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var f = GgufMoeReader.TryRead(path);
        long file = new FileInfo(path).Length;
        string facts = f == null ? "null" : $"{f.Architecture} {f.BlockCount} layers, {f.ExpertCount} experts, experts {f.ExpertBytesTotal / MiB} MiB, "
            + $"rest {f.NonExpertGpuBytes / MiB} MiB, file {file / MiB} MiB, KV {f.KElemsPerToken + f.VElemsPerToken} elems/token, read in {watch.ElapsedMilliseconds} ms";
        check("gguf-real", $"{Path.GetFileName(path)}: read without loading weights, sizes add up to the file",
            f != null && f.ExpertBytesTotal + f.NonExpertGpuBytes <= file && f.ExpertBytesTotal + f.NonExpertGpuBytes > file * 0.9, facts);
        if (f == null) return;
        var plan = LlamaServerArgs.Build(Gtx1070Ti(f));
        check("gguf-real", "the line it gets on an 8 GB card (7600 MiB free, 12k window)", plan.Args.Count > 0, Line(plan) + " · " + plan.Summary);
    }

    static void Features(Action<string, string, bool, string> check)
    {
        check("llama-features", "build 12144's --help: --n-cpu-moe, -fa on|off|auto, -ctk/-ctv, --cache-reuse, --fit, --fit-target",
            All is { NCpuMoe: true, FlashAttnOnOff: true, CacheTypes: true, CacheReuse: true, Fit: true, FitTarget: true }, All.ToString());
        var old = LlamaServerFeatures.Parse(OldHelp);
        check("llama-features", "a 2024 build: -fa takes no value (\"-fa on\" would stop it), no --n-cpu-moe, no --fit",
            old is { NCpuMoe: false, FlashAttnOnOff: false, CacheTypes: true, CacheReuse: false, Fit: false }, old.ToString());
        check("llama-features", "no --help text (a build that would not answer) = nothing new is sent",
            LlamaServerFeatures.Parse("") == LlamaServerFeatures.None && LlamaServerFeatures.Parse(null) == LlamaServerFeatures.None, "");
    }

    // ─── GGUF reader on real files ───

    static void Str(BinaryWriter w, string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }

    /// <summary>A GGUF v3 with these values and tensors (sizes multiples of 32, f32 type, data zero-filled).</summary>
    static void WriteGguf(string path, List<(string Key, object Value)> kv, List<(string Name, int Bytes)> tensors)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(0x46554747u); w.Write(3u); w.Write((ulong)tensors.Count); w.Write((ulong)kv.Count);
        foreach (var (key, value) in kv)
        {
            Str(w, key);
            switch (value)
            {
                case string s: w.Write(8u); Str(w, s); break;
                case int n: w.Write(4u); w.Write((uint)n); break;
                case float f: w.Write(6u); w.Write(f); break;
                case int[] arr: w.Write(9u); w.Write(5u); w.Write((ulong)arr.Length); foreach (int x in arr) w.Write(x); break;
                case string[] arr: w.Write(9u); w.Write(8u); w.Write((ulong)arr.Length); foreach (var x in arr) Str(w, x); break;
                default: throw new ArgumentException(value.GetType().Name);
            }
        }
        ulong off = 0;
        foreach (var (name, bytes) in tensors)
        {
            Str(w, name); w.Write(1u); w.Write((ulong)(bytes / 4)); w.Write(0u); w.Write(off);
            off += (ulong)bytes;
        }
        while (w.BaseStream.Position % 32 != 0) w.Write((byte)0);
        w.Write(new byte[off]);
    }

    static List<(string, object)> MoeKv(object headCountKv, int? splitCount = null)
    {
        var kv = new List<(string, object)>
        {
            ("general.architecture", "qwen3moe"),
            ("general.name", "Synthetic MoE"),
            ("tokenizer.ggml.tokens", Enumerable.Range(0, 3000).Select(i => "tok" + i).ToArray()),   // skipped, not kept
            ("qwen3moe.block_count", 4),
            ("qwen3moe.expert_count", 8),
            ("qwen3moe.embedding_length", 64),
            ("qwen3moe.attention.head_count", 8),
            ("qwen3moe.attention.head_count_kv", headCountKv),
            ("qwen3moe.attention.key_length", 16),
            ("qwen3moe.attention.value_length", 16),
            ("qwen3moe.rope.freq_base", 1_000_000f),
        };
        if (splitCount is int n) kv.Add(("split.count", n));
        return kv;
    }

    static List<(string, int)> MoeTensors(int fromLayer, int toLayer, bool embd, bool output)
    {
        var t = new List<(string, int)>();
        if (embd) t.Add(("token_embd.weight", 1024));
        for (int i = fromLayer; i < toLayer; i++)
        {
            t.Add(($"blk.{i}.attn_q.weight", 128));
            t.Add(($"blk.{i}.ffn_gate_inp.weight", 32));           // the router stays on the GPU
            t.Add(($"blk.{i}.ffn_gate_exps.weight", 512));
            t.Add(($"blk.{i}.ffn_up_exps.weight", 512));
            t.Add(($"blk.{i}.ffn_down_exps.weight", 512 + 32 * i));  // a different size per layer, to see each counted
            t.Add(($"blk.{i}.ffn_up_shexp.weight", 64));            // a shared expert is not routed: GPU
        }
        if (output) { t.Add(("output_norm.weight", 32)); t.Add(("output.weight", 256)); }
        return t;
    }

    static void Reader(Action<string, string, bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cluadex-llamalaunch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "moe.gguf");
            WriteGguf(a, MoeKv(2), MoeTensors(0, 4, embd: true, output: true));
            var f = GgufMoeReader.TryRead(a);
            bool perLayer = f != null && f.ExpertBytesPerLayer.SequenceEqual(new long[] { 1536, 1568, 1600, 1632 });
            check("gguf-moe", "routed experts are summed per layer (gate+up+down _exps), from the tensor offsets",
                f is { IsMoE: true, BlockCount: 4, ExpertCount: 8 } && perLayer, f == null ? "null" : string.Join(",", f.ExpertBytesPerLayer));
            // per layer: attn_q 128 + router 32 + shared 64 = 224 → 896, + output_norm 32 + output 256 = 1184; token_embd stays in RAM
            check("gguf-moe", "attention, router, shared experts and the head go to the GPU; the input embedding does not",
                f?.NonExpertGpuBytes == 1184, $"{f?.NonExpertGpuBytes}");
            check("gguf-moe", "KV per token = layers × KV heads × head size (K and V): 4 × 2 × 16",
                f is { KElemsPerToken: 128, VElemsPerToken: 128 }, $"{f?.KElemsPerToken}/{f?.VElemsPerToken}");

            string tied = Path.Combine(dir, "tied.gguf");
            WriteGguf(tied, MoeKv(2), MoeTensors(0, 4, embd: true, output: false));
            var t = GgufMoeReader.TryRead(tied);
            check("gguf-moe", "no output.weight (tied head): the embedding IS the output layer and is counted on the GPU",
                t?.NonExpertGpuBytes == 896 + 1024, $"{t?.NonExpertGpuBytes}");

            string hybrid = Path.Combine(dir, "hybrid.gguf");
            WriteGguf(hybrid, MoeKv(new[] { 0, 2, 0, 2 }), MoeTensors(0, 4, embd: true, output: true));
            var h = GgufMoeReader.TryRead(hybrid);
            check("gguf-moe", "head_count_kv per layer (a hybrid with recurrent layers at 0): only attention layers cost KV",
                h is { KElemsPerToken: 64, VElemsPerToken: 64 }, $"{h?.KElemsPerToken}");

            string p1 = Path.Combine(dir, "big-00001-of-00002.gguf"), p2 = Path.Combine(dir, "big-00002-of-00002.gguf");
            WriteGguf(p1, MoeKv(2, splitCount: 2), MoeTensors(0, 2, embd: true, output: false));
            WriteGguf(p2, new() { ("split.count", 2) }, MoeTensors(2, 4, embd: false, output: true));
            var s = GgufMoeReader.TryRead(p1);
            check("gguf-moe", "a split model is read across its parts (experts of layers 2-3 live in part 2)",
                s != null && s.ExpertBytesPerLayer.SequenceEqual(new long[] { 1536, 1568, 1600, 1632 }) && s.NonExpertGpuBytes == 1184,
                s == null ? "null" : string.Join(",", s.ExpertBytesPerLayer));
            File.Delete(p2);
            check("gguf-moe", "a split model with a part missing gives no facts (the launch line of before)", GgufMoeReader.TryRead(p1) == null, "");

            string dense = Path.Combine(dir, "dense.gguf");
            WriteGguf(dense, new() { ("general.architecture", "llama"), ("llama.block_count", 2), ("llama.attention.head_count", 4),
                ("llama.embedding_length", 64) }, new() { ("blk.0.attn_q.weight", 64), ("blk.0.ffn_up.weight", 64), ("blk.1.ffn_up.weight", 64) });
            var d = GgufMoeReader.TryRead(dense);
            check("gguf-moe", "a dense model is not a MoE (no _exps tensors, no expert_count)", d is { IsMoE: false } && d.ExpertBytesPerLayer.Length == 0, $"{d}");

            string junk = Path.Combine(dir, "junk.gguf");
            File.WriteAllBytes(junk, Encoding.ASCII.GetBytes("GGUF\x03\0\0\0 truncated"));
            check("gguf-moe", "a truncated / non-GGUF file gives null, never throws", GgufMoeReader.TryRead(junk) == null
                && GgufMoeReader.TryRead(Path.Combine(dir, "missing.gguf")) == null, "");

            // The reader and the builder together: this little model on a "card" of 2 MiB + margins.
            var plan = LlamaServerArgs.Build(Gtx1070Ti(f!) with
            {
                ContextTokens = 0,
                FreeVramMB = LlamaServerArgs.SafetyMB + LlamaServerArgs.ComputeMB(512) + 1,   // 1 MiB of room
            });
            check("gguf-moe", "file → plan: everything fits 1 MiB, so all layers go to the GPU with no --n-cpu-moe",
                plan.CpuMoeLayers == null && Line(plan).StartsWith("-ngl 999 -fa on"), Line(plan));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static void Plans(Action<string, string, bool, string> check)
    {
        var m = Moe30B();
        var p = LlamaServerArgs.Build(Gtx1070Ti(m));
        // 7600 − 550 margin − 612 KV (12k ctx, q8_0) − 512 compute = 5926 MiB; − 1200 besides the experts = 4726 → 13 layers × 360
        check("llama-plan", "30B-A3B on an 8 GB card: all layers on the GPU, experts of the first 35/48 layers in RAM",
            Line(p) == "-ngl 999 --n-cpu-moe 35 -fa on -ctk q8_0 -ctv q8_0 -t 6 --cache-reuse 256" && p.CpuMoeLayers == 35, Line(p));
        check("llama-plan", "the status line says where the experts went", p.Summary.Contains("experts of 35/48 layers in RAM") && p.Summary.Contains("KV q8_0"), p.Summary);

        var f16 = LlamaServerArgs.Build(Gtx1070Ti(m) with { KvCacheSetting = "f16" });
        check("llama-plan", "with an f16 KV cache two more layers' experts must leave the card (q8_0 is what makes room)",
            f16.CpuMoeLayers == 37 && !f16.Args.Contains("-ctk") && !f16.Args.Contains("-fa"), Line(f16));

        var big = LlamaServerArgs.Build(Gtx1070Ti(m) with { FreeVramMB = 23_000 });
        check("llama-plan", "the same model on a 24 GB card fits whole: -ngl 999 and no --n-cpu-moe (as before)",
            big.CpuMoeLayers == null && Line(big).StartsWith("-ngl 999 -fa on") && !big.Args.Contains("--n-cpu-moe"), Line(big));

        int last = int.MaxValue;
        bool monotonic = true;
        for (int free = 3000; free <= 24_000; free += 500)
        {
            int n = LlamaServerArgs.Build(Gtx1070Ti(m) with { FreeVramMB = free }).CpuMoeLayers ?? 0;
            if (n > last) monotonic = false;
            last = n;
        }
        check("llama-plan", "more free VRAM never sends more experts to RAM", monotonic, "");

        var ctx = LlamaServerArgs.Build(Gtx1070Ti(m) with { ContextTokens = 65536 });
        check("llama-plan", "a bigger window takes VRAM from the experts (the KV is counted)", ctx.CpuMoeLayers > p.CpuMoeLayers, $"{ctx.CpuMoeLayers}");

        var own = LlamaServerArgs.Build(Gtx1070Ti(m) with { GpuLayerSetting = 20 });
        check("llama-plan", "the owner's own GPU layer count is never changed (-ngl 20, no --n-cpu-moe)",
            own.Args.Take(2).SequenceEqual(new[] { "-ngl", "20" }) && !own.Args.Contains("--n-cpu-moe"), Line(own));

        var cpu = LlamaServerArgs.Build(Gtx1070Ti(m) with { CpuOnly = true });
        check("llama-plan", "Backend = CPU: -ngl 0, no experts plan", cpu.Args.Take(2).SequenceEqual(new[] { "-ngl", "0" }) && cpu.CpuMoeLayers == null, Line(cpu));

        var dense = LlamaServerArgs.Build(Gtx1070Ti(new GgufMoeFacts("llama", 32, 0, [], 4000 * MiB, 32 * 8 * 128, 32 * 8 * 128)));
        check("llama-plan", "a dense model launches as before: -ngl 999", dense.Args.Take(2).SequenceEqual(new[] { "-ngl", "999" }) && dense.CpuMoeLayers == null, Line(dense));
        var unread = LlamaServerArgs.Build(Gtx1070Ti(null!) with { Model = null });
        check("llama-plan", "a file that could not be read launches as before: -ngl 999", unread.Args.Take(2).SequenceEqual(new[] { "-ngl", "999" }), Line(unread));

        var amd = LlamaServerArgs.Build(Gtx1070Ti(m) with { FreeVramMB = 0 });
        check("llama-plan", "a MoE with no free-VRAM reading (AMD / Intel): the server's own fitter sizes it, -ngl left unset",
            Line(amd).StartsWith("--fit on --fit-target 550 ") && !amd.Args.Contains("-ngl") && amd.ServerFits, Line(amd));
        var amdOld = LlamaServerArgs.Build(Gtx1070Ti(m) with { FreeVramMB = 0, Features = All with { Fit = false, FitTarget = false } });
        check("llama-plan", "…and on a build without --fit, as before: -ngl 999", amdOld.Args.Take(2).SequenceEqual(new[] { "-ngl", "999" }), Line(amdOld));

        var tiny = LlamaServerArgs.Build(Gtx1070Ti(m) with { FreeVramMB = 2500 });
        check("llama-plan", "a card too small even for attention + shared weights: handed to --fit (it can move layers too)",
            tiny.ServerFits && !tiny.Args.Contains("--n-cpu-moe") && !tiny.Args.Contains("-ngl"), Line(tiny));
        var tinyOld = LlamaServerArgs.Build(Gtx1070Ti(m) with { FreeVramMB = 2500, Features = All with { Fit = false } });
        check("llama-plan", "…without --fit: every layer's experts in RAM (the most that can leave the card)",
            tinyOld.CpuMoeLayers == 48 && Line(tinyOld).StartsWith("-ngl 999 --n-cpu-moe 48"), Line(tinyOld));

        var noMoeFlag = LlamaServerArgs.Build(Gtx1070Ti(m) with { Features = All with { NCpuMoe = false } });
        check("llama-plan", "a build without --n-cpu-moe never gets it", !noMoeFlag.Args.Contains("--n-cpu-moe"), Line(noMoeFlag));

        // Dense-first MoE (DeepSeek: the first layers have no routed experts) — those layers cost nothing to "move".
        var dsv = new GgufMoeFacts("deepseek2", 6, 64, [0, 0, 100 * MiB, 100 * MiB, 100 * MiB, 100 * MiB], 500 * MiB, 6 * 576, 6 * 512);
        long budget = 500 * MiB + 250 * MiB;
        check("llama-plan", "experts on the card are the LAST layers' (--n-cpu-moe takes the first N): 2 of 4 fit → N = 4",
            LlamaServerArgs.CpuMoeLayersFor(dsv, budget) == 4 && LlamaServerArgs.CpuMoeLayersFor(dsv, 500 * MiB + 400 * MiB) == 0
            && LlamaServerArgs.CpuMoeLayersFor(dsv, 499 * MiB) == null,
            $"{LlamaServerArgs.CpuMoeLayersFor(dsv, budget)}");
    }

    static void KvAndThreads(Action<string, string, bool, string> check)
    {
        var old = LlamaServerFeatures.Parse(OldHelp);
        check("llama-kv", "auto = q8_0 on a build with -fa on|off|auto; f16 on one without (a quantised V needs flash attention)",
            LlamaServerArgs.ResolveKvType("auto", All) == "q8_0" && LlamaServerArgs.ResolveKvType("q8_0", old) == "f16", "");
        check("llama-kv", "f16 / q4_0 as chosen; an unknown hand-edited value falls back to q8_0",
            LlamaServerArgs.ResolveKvType("f16", All) == "f16" && LlamaServerArgs.ResolveKvType(" Q4_0 ", All) == "q4_0"
            && LlamaServerArgs.ResolveKvType("q5_1x", All) == "q8_0" && LlamaServerArgs.ResolveKvType(null, All) == "q8_0", "");
        var q4 = LlamaServerArgs.Build(Gtx1070Ti(Moe30B()) with { KvCacheSetting = "q4_0" });
        int ik = q4.Args.IndexOf("-ctk"), iv = q4.Args.IndexOf("-ctv");
        check("llama-kv", "K and V always get the SAME type (release CUDA builds have no mixed-type flash-attention kernels)",
            ik >= 0 && iv >= 0 && q4.Args[ik + 1] == "q4_0" && q4.Args[iv + 1] == "q4_0", Line(q4));
        var m = Moe30B();
        int f16 = LlamaServerArgs.KvCacheMB(m, 32768, "f16"), q8 = LlamaServerArgs.KvCacheMB(m, 32768, "q8_0");
        check("llama-kv", "q8_0 is ~53 % of f16 (32k window of a 30B-A3B: 3 GiB → 1.6 GiB)", f16 == 3072 && q8 == 1632, $"f16 {f16} MiB, q8_0 {q8} MiB");

        check("llama-threads", "auto = physical cores (6 on a 6-core / 12-thread i5-11400), not logical",
            LlamaServerArgs.ThreadsFor(0, 6, 12) == 6, "");
        check("llama-threads", "the owner's thread count wins; no physical count read → logical",
            LlamaServerArgs.ThreadsFor(8, 6, 12) == 8 && LlamaServerArgs.ThreadsFor(0, 0, 12) == 12 && LlamaServerArgs.ThreadsFor(0, 0, 0) == 1, "");
        int phys = CpuTopology.PhysicalCores;
        check("llama-threads", "this PC's physical cores read from Windows (1..logical)",
            phys >= 1 && phys <= Environment.ProcessorCount, $"{phys} physical / {Environment.ProcessorCount} logical");
    }

    static void Fallback(Action<string, string, bool, string> check)
    {
        var p = LlamaServerArgs.Build(Gtx1070Ti(Moe30B()));
        var legacy = LlamaServerArgs.Build(Gtx1070Ti(Moe30B()) with { Legacy = true });
        check("llama-fallback", "the new line carries flags an old build may refuse → a failed start is retried without them",
            p.UsesNewFlags, Line(p));
        check("llama-fallback", "the retry is the line of before (-ngl 999 and threads only), with no new flag",
            Line(legacy) == "-ngl 999 -t 6" && !legacy.UsesNewFlags, Line(legacy));
        var oldBuild = LlamaServerArgs.Build(Gtx1070Ti(Moe30B()) with { Features = LlamaServerFeatures.Parse(OldHelp) });
        check("llama-fallback", "an old build is never sent a flag its --help does not list", Line(oldBuild) == "-ngl 999 -t 6", Line(oldBuild));
    }

    static void RequestAndSettings(Action<string, string, bool, string> check)
    {
        var svc = new SettingsService();   // portable — never the user's settings
        var llama = new LlamaServerProvider(svc);
        var msgs = new List<NativeMessage> { new() { Role = "user", Content = { new ContentBlock { Type = "text", Text = "hi" } } } };
        var tools = new List<ToolSchema> { new() { Name = "read_file", Description = "read", InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement } };
        var body = JsonSerializer.Serialize(llama.BuildToolRequest(msgs, "sys", tools));
        using var doc = JsonDocument.Parse(body);
        check("llama-cache", "every tool-calling request asks the server to keep its prompt cache (cache_prompt: true)",
            doc.RootElement.TryGetProperty("cache_prompt", out var cp) && cp.GetBoolean(), Trunc(body, 200));
        check("llama-cache", "…and the body is otherwise the same contract (tools, tool_choice, max_tokens, stream false)",
            doc.RootElement.TryGetProperty("tools", out _) && doc.RootElement.TryGetProperty("tool_choice", out _)
            && doc.RootElement.TryGetProperty("max_tokens", out _) && !doc.RootElement.GetProperty("stream").GetBoolean(), "");

        check("llama-settings", "a new settings file has KV cache \"auto\"", new AppSettings().LocalKvCacheType == "auto", "");
        check("llama-settings", "every choice on the Settings page means something to the builder",
            SettingsViewModel.KvCacheChoices.SequenceEqual(new[] { "auto", "f16", "q8_0", "q4_0" })
            && SettingsViewModel.KvCacheChoices.All(c => LlamaServerArgs.ResolveKvType(c, All) is "q8_0" or "f16" or "q4_0"), "");
    }

    /// <summary>The build CluadeX installed on this PC, if there is one: its --help (no model loaded) must parse to every flag.</summary>
    static void InstalledBuild(Action<string, string, bool, string> check)
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex", "llama-backend", "llama-server.exe");
        if (!File.Exists(exe))
        {
            check("llama-features", "installed llama-server: none on this PC (skipped)", true, exe);
            return;
        }
        var f = LlamaServerProvider.ServerFeatures(exe);
        check("llama-features", "the llama-server CluadeX installed here answers --help with all the flags the plan uses",
            f is { NCpuMoe: true, FlashAttnOnOff: true, CacheTypes: true, CacheReuse: true, Fit: true }, f.ToString());
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
