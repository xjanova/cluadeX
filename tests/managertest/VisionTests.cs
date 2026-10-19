using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

#nullable enable
namespace ManagerTest;

/// <summary>
/// Local vision: pairing a GGUF with its projector (mmproj), the --mmproj launch line, the bundled mtmd.dll's projector
/// types, the catalog's projectors — and the load fallback driven through a REAL child process: this harness's own
/// apphost copied as llama-server.exe (it still runs managertest.dll) plays the server. It rejects --mmproj the way
/// llama-server does with a projector it cannot read, or accepts it, and logs every command line it was given.
/// No real model, no GPU.
/// </summary>
internal static partial class Program
{
    const string BundledBackend = @"E:\Code\ClaudeClient\CluadeX\llama-backend";

    /// <summary>The fake llama-server: FAKE_LLAMA_LOG gets one line per launch; --mmproj fails unless FAKE_LLAMA_MMPROJ=ok.</summary>
    static int FakeLlamaServer(string[] args)
    {
        string? log = Environment.GetEnvironmentVariable("FAKE_LLAMA_LOG");
        if (log != null) File.AppendAllText(log, string.Join(' ', args) + "\n");
        if (args.Contains("--mmproj") && Environment.GetEnvironmentVariable("FAKE_LLAMA_MMPROJ") != "ok")
        {
            Console.Error.WriteLine("clip_model_loader: unknown projector type: gemma4uv");
            Console.Error.WriteLine("mtmd_init_from_file: error: Failed to load CLIP model from the mmproj file");
            Console.Error.Flush();
            Thread.Sleep(300);
            return 1;
        }
        int port = int.Parse(args[Array.IndexOf(args, "--port") + 1]);
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.Error.WriteLine("main: server is listening on http://127.0.0.1:" + port);
        while (true)
        {
            using var client = listener.AcceptTcpClient();
            using var stream = client.GetStream();
            var buf = new byte[4096];
            try { stream.Read(buf, 0, buf.Length); } catch { continue; }
            byte[] body = Encoding.ASCII.GetBytes("{\"status\":\"ok\"}");
            byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            try { stream.Write(head); stream.Write(body); } catch { }
        }
    }

    static string NewDir(string name)
    {
        string d = Path.Combine(Tmp, "vision-" + name);
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return d;
    }

    static string Touch(string dir, string name, int bytes = 64)
    {
        string p = Path.Combine(dir, name);
        File.WriteAllBytes(p, new byte[bytes]);
        return p;
    }

    static async Task VisionTests()
    {
        CaseSync("VP1", () =>
        {
            var cases = new (string File, string Stem)[]
            {
                ("Qwen3.5-9B-Q4_K_M.gguf", "Qwen3.5-9B"), ("Qwen3.8-27B-UD-Q4_K_M.gguf", "Qwen3.8-27B"),
                ("gemma-4-12b-it-Q5_K_M.gguf", "gemma-4-12b-it"), ("NVIDIA-Nemotron-3.5-Lightning-30B-A3B-Q4_0.gguf", "NVIDIA-Nemotron-3.5-Lightning-30B-A3B"),
                ("codellama-7b-instruct.Q4_K_M.gguf", "codellama-7b-instruct"), ("Big-Model-Q4_K_M-00001-of-00003.gguf", "Big-Model"),
                ("gemma-4-E4B-it-IQ4_XS.gguf", "gemma-4-E4B-it"), ("gpt-oss-20b-F16.gguf", "gpt-oss-20b"), ("plain-name.gguf", "plain-name"),
            };
            var bad = cases.Where(c => VisionProjector.BaseStem(c.File) != c.Stem).Select(c => $"{c.File} → {VisionProjector.BaseStem(c.File)}").ToList();
            Check("VP1", "model stem: quant, UD- prefix, shard suffix and .Q4 naming are stripped; the version numbers in the name are not", bad.Count == 0, string.Join("; ", bad));
        });

        CaseSync("VP2", () =>
        {
            // CluadeX's own naming, in a shared folder with other models and other models' projectors.
            string shared = NewDir("shared");
            string qwen = Touch(shared, "Qwen3.5-9B-Q4_K_M.gguf");
            Touch(shared, VisionProjector.LocalFileName("Qwen3.5-9B-Q4_K_M.gguf", "mmproj-F16.gguf"));
            string gemma = Touch(shared, "gemma-4-12b-it-Q4_K_M.gguf");
            string text = Touch(shared, "Ornith-1.0-9B-Q4_K_M.gguf");
            string mimo = Touch(shared, "MiMo-V2.6-Distill-Qwen-9B-Q4_K_M.gguf");
            Touch(shared, "mmproj-MiMo-V2.6-Distill-Qwen-9B-f16.gguf");
            Touch(shared, "mmproj-F16.gguf");                                  // generic: whose? nobody's
            string q5 = Touch(shared, "Qwen3.5-9B-Q5_K_M.gguf");              // another quant of the same model shares it
            bool own = Path.GetFileName(VisionProjector.FindFor(qwen) ?? "") == "Qwen3.5-9B.mmproj-F16.gguf"
                       && VisionProjector.FindFor(q5) == VisionProjector.FindFor(qwen);
            bool named = Path.GetFileName(VisionProjector.FindFor(mimo) ?? "") == "mmproj-MiMo-V2.6-Distill-Qwen-9B-f16.gguf";
            bool notGuessed = VisionProjector.FindFor(gemma) == null && VisionProjector.FindFor(text) == null;

            // LM Studio layout: one model per folder with a generic projector (F16 preferred over F32).
            string lms = NewDir("lmstudio");
            string single = Touch(lms, "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf");
            Touch(lms, "mmproj-F32.gguf");
            Touch(lms, "mmproj-F16.gguf");
            bool layout = Path.GetFileName(VisionProjector.FindFor(single) ?? "") == "mmproj-F16.gguf";
            // …but CluadeX-named projector of a DIFFERENT model in that folder is not taken.
            string lone = NewDir("lone");
            string g = Touch(lone, "gemma-4-31B-it-Q4_K_M.gguf");
            Touch(lone, "Qwen3.5-9B.mmproj-F16.gguf");
            bool foreign = VisionProjector.FindFor(g) == null;
            Check("VP2", "pairing: own sidecar (shared by every quant), stem-named mmproj, one-model folder; never a generic or another model's projector in a shared folder",
                own && named && notGuessed && layout && foreign, $"own={own} named={named} notGuessed={notGuessed} layout={layout} foreign={foreign}");
        });

        CaseSync("VP3", () =>
        {
            string d = NewDir("args");
            string model = Touch(d, "Qwen3.5-4B-Q4_K_M.gguf", 4096);
            string proj = Touch(d, "Qwen3.5-4B.mmproj-F16.gguf", 2048);
            var none = LlamaServerProvider.VisionArgs(model, null, 8000, 12288, true);
            var cpu = LlamaServerProvider.VisionArgs(model, proj, 8000, 12288, allLayersOnGpu: false);
            var roomy = LlamaServerProvider.VisionArgs(model, proj, 8000, 12288, allLayersOnGpu: true);
            var unknownFree = LlamaServerProvider.VisionArgs(model, proj, 0, 12288, allLayersOnGpu: true);
            bool tight = VisionProjector.ShouldOffload(5_700_000_000, 918_000_000, 6000, 12288, true) == false
                         && VisionProjector.ShouldOffload(5_700_000_000, 918_000_000, 16000, 12288, true);
            Check("VP3", "launch line: nothing without a projector; --mmproj \"path\"; projector kept on the CPU when layers are split, free VRAM is unknown or it would not fit",
                none.Count == 0 && cpu.SequenceEqual(new[] { "--mmproj", $"\"{proj}\"", "--no-mmproj-offload" })
                && roomy.SequenceEqual(new[] { "--mmproj", $"\"{proj}\"" }) && unknownFree.Contains("--no-mmproj-offload") && tight,
                $"cpu=[{string.Join(" ", cpu)}] roomy=[{string.Join(" ", roomy)}] unknown=[{string.Join(" ", unknownFree)}] tight={tight}");
        });

        CaseSync("VP4", () =>
        {
            if (!File.Exists(Path.Combine(BundledBackend, "mtmd.dll"))) { Console.WriteLine("  SKIP VP4    no bundled mtmd.dll"); return; }
            bool ok = LlamaBackendInfo.SupportsProjectorType("qwen3vl_merger", BundledBackend) == true
                      && LlamaBackendInfo.SupportsProjectorType("pixtral", BundledBackend) == true
                      && LlamaBackendInfo.SupportsProjectorType("gemma4v", BundledBackend) == true
                      && LlamaBackendInfo.SupportsProjectorType("gemma4uv", BundledBackend) == false
                      && LlamaBackendInfo.SupportsProjectorType("qwen9vl_future", BundledBackend) == false
                      && LlamaBackendInfo.SupportsProjectorType("qwen3vl_merger", Path.Combine(Tmp, "no-backend-here")) is null or true;
            Check("VP4", "bundled mtmd.dll (b8660): reads qwen3vl_merger / pixtral / gemma4v projectors, not Gemma 4 12B's newer gemma4uv", ok);
        });

        CaseSync("VP5", () =>
        {
            var visual = HuggingFaceService.Catalog.Where(m => m.Tasks.HasFlag(ModelTasks.Vision)).ToList();
            var bad = visual.Where(m => m.Projector == null || string.IsNullOrEmpty(m.ProjectorType) || m.Projector.Name is not ("F16" or "BF16")
                                        || !m.Projector.FileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase)).Select(m => m.Id)
                            .Concat(HuggingFaceService.Catalog.Where(m => !m.Tasks.HasFlag(ModelTasks.Vision) && m.Projector != null).Select(m => m.Id + " (text-only with projector)"))
                            .ToList();
            Check("VP5", $"every vision model in the catalog ({visual.Count}) has its F16 projector + projector type; text-only models have none", bad.Count == 0, string.Join(", ", bad));
        });

        CaseSync("VP6", () =>
        {
            if (!File.Exists(Path.Combine(BundledBackend, "mtmd.dll"))) { Console.WriteLine("  SKIP VP6    no bundled mtmd.dll"); return; }
            var settings = Sp.GetRequiredService<SettingsService>();
            string? before = settings.Settings.CustomLlamaCppBackendPath;
            settings.UpdateSettings(s => s.CustomLlamaCppBackendPath = BundledBackend);
            try
            {
                var vm = Sp.GetRequiredService<ModelManagerViewModel>();
                vm.ApplyHardware(Pc24);
                vm.SelectedTask = "Vision";
                var g12 = vm.RecommendedModels.First(m => m.Id == "gemma-4-12b");
                var q9 = vm.RecommendedModels.First(m => m.Id == "qwen3.5-9b");
                bool flags = g12.VisionNeedsNewerBackend && !g12.CanSeeImages && !g12.VisionComesWithDownload
                             && q9.CanSeeImages && q9.VisionComesWithDownload && !q9.VisionNeedsNewerBackend;
                bool picks = vm.TopPicks.Count > 0 && vm.TopPicks.All(p => p.CanSeeImages);
                vm.SelectedTask = "All";
                Check("VP6", "with the bundled backend: Gemma 4 12B says its vision needs a newer llama.cpp (no projector download, never a vision pick); Qwen3.5 9B brings its projector",
                    flags && picks, $"g12 newer={g12.VisionNeedsNewerBackend} q9 can={q9.CanSeeImages} picks={string.Join(",", vm.TopPicks.Select(p => p.Id))}");
            }
            finally { settings.UpdateSettings(s => s.CustomLlamaCppBackendPath = before); }
        });

        await Case("VP7", async () =>
        {
            // The real launch path, with a fake server process.
            string exeDir = AppContext.BaseDirectory;
            string fake = Path.Combine(exeDir, "llama-server.exe");
            File.Copy(Environment.ProcessPath!, fake, overwrite: true);
            string dir = NewDir("launch");
            string model = Touch(dir, "Toy-Vision-Q4_K_M.gguf", 1024);
            string proj = Touch(dir, "Toy-Vision.mmproj-F16.gguf", 512);
            string log = Path.Combine(dir, "launches.txt");
            Environment.SetEnvironmentVariable("FAKE_LLAMA_LOG", log);
            Environment.SetEnvironmentVariable("FAKE_LLAMA_MMPROJ", null);
            var settings = Sp.GetRequiredService<SettingsService>();
            string? backendBefore = settings.Settings.CustomLlamaCppBackendPath;
            string gpuBefore = settings.Settings.GpuBackend;
            settings.UpdateSettings(s => { s.CustomLlamaCppBackendPath = exeDir; s.GpuBackend = "CPU"; });
            var server = Sp.GetRequiredService<LlamaServerProvider>();
            var local = Sp.GetRequiredService<LocalGgufProvider>();
            VisionProjector.ResetFailures();
            try
            {
                // 1. The backend rejects the projector → the model still loads, blind, with the reason.
                await local.LoadModelAsync(model);
                string[] l1 = File.ReadAllLines(log);
                bool fellBack = local.IsReady && local.LoadedProjectorPath == null && !local.SupportsToolResultImages("")
                                && l1.Length == 2 && l1[0].Contains("--mmproj") && l1[0].Contains("--no-mmproj-offload") && !l1[1].Contains("--mmproj")
                                && local.VisionFallbackReason.Contains("Toy-Vision.mmproj-F16.gguf") && local.VisionFallbackReason.Contains("projector");
                Check("VP7", "a projector the backend rejects: llama-server is started again without it — the model loads, blind, and says why",
                    fellBack, $"ready={local.IsReady} launches={l1.Length} reason='{local.VisionFallbackReason}'\n{string.Join("\n", l1)}");

                // 2. Loading it again does not retry the broken projector.
                await local.LoadModelAsync(model);
                string[] l2 = File.ReadAllLines(log);
                Check("VP7b", "…and the next load of that model does not try the rejected projector again (one launch, no --mmproj)",
                    local.IsReady && l2.Length == 3 && !l2[2].Contains("--mmproj"), string.Join("\n", l2));

                // 3. A backend that reads it: the model sees images (the agent loop then sends image_url parts).
                VisionProjector.ResetFailures();
                Environment.SetEnvironmentVariable("FAKE_LLAMA_MMPROJ", "ok");
                await local.LoadModelAsync(model);
                string[] l3 = File.ReadAllLines(log);
                bool sees = local.IsReady && local.LoadedProjectorPath == proj && local.SupportsToolResultImages("") && local.VisionFallbackReason == ""
                            && l3.Length == 4 && l3[3].Contains($"--mmproj {proj} ");   // argv: the quoted path arrived as ONE argument
                Check("VP7c", "a projector the backend reads: one launch with --mmproj, the provider now reports it can see images",
                    sees, $"projector={local.LoadedProjectorPath} images={local.SupportsToolResultImages("")}\n{l3.LastOrDefault()}");

                // 4. Unloading forgets it.
                await local.UnloadAsync();
                Check("VP7d", "after unloading, the provider is blind again", !server.SupportsToolResultImages("") && server.LoadedProjectorPath == null);
            }
            finally
            {
                try { await local.UnloadAsync(); } catch { }
                Environment.SetEnvironmentVariable("FAKE_LLAMA_LOG", null);
                Environment.SetEnvironmentVariable("FAKE_LLAMA_MMPROJ", null);
                settings.UpdateSettings(s => { s.CustomLlamaCppBackendPath = backendBefore; s.GpuBackend = gpuBefore; });
                VisionProjector.ResetFailures();
                for (int i = 0; i < 10; i++)
                {
                    try { File.Delete(fake); break; } catch { await Task.Delay(300); }
                }
            }
        });
    }
}
