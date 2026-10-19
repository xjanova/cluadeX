using System.IO;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

#nullable enable
namespace ManagerTest;

/// <summary>
/// The Model Manager's advisor: memory / speed estimates, the automatic quant choice, the "Best for your PC" picks and
/// the catalog itself. Made-up PCs (no nvidia-smi), the real catalog, the real view-model from the app's container.
/// </summary>
internal static partial class Program
{
    const int Ctx = 12288;

    // A GTX 1070 Ti 8 GB box with 32 GB of DDR4-3200 (the owner's machine); free VRAM unknown → the reserve applies.
    static readonly HardwareProfile Pc8 = new()
    {
        GpuName = "NVIDIA GeForce GTX 1070 Ti", GpuBrand = "NVIDIA", GpuCount = 1, VramTotalMB = 8192, CanOffload = true,
        GpuBandwidthGBs = 256, SystemRamMB = 32768, RamBandwidthGBs = 51.2, CpuThreads = 12,
    };
    static readonly HardwareProfile Pc24 = new()
    {
        GpuName = "NVIDIA GeForce RTX 4090", GpuBrand = "NVIDIA", GpuCount = 1, VramTotalMB = 24576, CanOffload = true,
        GpuBandwidthGBs = 1008, SystemRamMB = 65536, RamBandwidthGBs = 89.6, CpuThreads = 32,
    };
    static readonly HardwareProfile PcTwo12 = new()
    {
        GpuName = "NVIDIA GeForce RTX 3060", GpuBrand = "NVIDIA", GpuCount = 2, VramTotalMB = 24576, CanOffload = true,
        GpuBandwidthGBs = 360, SystemRamMB = 32768, RamBandwidthGBs = 51.2, CpuThreads = 16,
    };
    static readonly HardwareProfile PcCpu = new()
    {
        GpuName = "", GpuCount = 0, VramTotalMB = 0, CanOffload = false, SystemRamMB = 16384, RamBandwidthGBs = 38, CpuThreads = 8,
    };

    static RecommendedModel Cat(string id) => HuggingFaceService.Catalog.First(m => m.Id == id);

    /// <summary>
    /// What compiles but renders blank, for ModelManagerView.xaml: every localized key it and its view-model use has a
    /// Thai value, every {StaticResource} exists, every {DynamicResource} is a token of EVERY theme (a key missing from
    /// one theme falls back to nothing there), every {Binding} names a real property.
    /// </summary>
    static void StaticXaml()
    {
        string? repo = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CluadeX", "Views", "ModelManagerView.xaml"))) { repo = d.FullName; break; }
        if (repo == null) { Check("X1", "static: repository found next to the harness", false); return; }
        string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { repo }.Concat(parts).ToArray()));
        string xaml = Read("CluadeX", "Views", "ModelManagerView.xaml");
        string code = Read("CluadeX", "ViewModels", "ModelManagerViewModel.cs") + Read("CluadeX", "Services", "ModelAdvisor.cs");
        string shared = Read("CluadeX", "Resources", "Theme.xaml") + Read("CluadeX", "App.xaml") + xaml;
        var themes = Directory.GetFiles(Path.Combine(repo, "CluadeX", "Resources", "Themes"), "*.xaml")
                              .Where(f => !f.EndsWith("ModernScrollBars.xaml")).ToList();
        var R = (string pattern, string text) => System.Text.RegularExpressions.Regex.Matches(text, pattern).Select(m => m.Groups[1].Value);

        var loc = Sp.GetRequiredService<LocalizationService>();
        string before = loc.CurrentLanguage;
        loc.SetLanguage("th");
        var keys = R(@"\{services:Loc\s+([\w.\-]+)\}", xaml)
            .Concat(R(@"""(modelsm\.[\w.\-]+)""", code).Where(k => !k.EndsWith('.') && k is not ("modelsm.task" or "modelsm.sort" or "modelsm.taskFor")))
            .Concat(new[] { "All", "Coding", "Chat", "Reasoning", "Vision", "Thai" }.SelectMany(t => new[] { "modelsm.task" + t, "modelsm.taskFor" + t }))
            .Concat(new[] { "Best", "Newest", "Fastest", "Smallest", "Name" }.Select(s => "modelsm.sort" + s))
            .Concat(HuggingFaceService.PopularSources.Select(s => "modelsm.src." + s.Name.Replace(" ", "")))
            .Concat(HuggingFaceService.Catalog.Select(m => "modelsm.desc." + m.Id))
            .Distinct().ToList();
        // Values that are rightly the same in Thai (a number, the technical term).
        var sameInThai = new HashSet<string> { "modelsm.params", "modelsm.tipQuantTitle", "modelsm.hwVram", "modelsm.hwRam", "modelsm.progress" };
        var missing = keys.Where(k => loc.T(k) == k || (!sameInThai.Contains(k) && !System.Text.RegularExpressions.Regex.IsMatch(loc.T(k), "[฀-๿]"))).ToList();
        loc.SetLanguage("en");
        var missingEn = keys.Where(k => loc.T(k) == k).ToList();
        loc.SetLanguage(before);
        Check("X1", $"static: all {keys.Count} localized keys of the page have a Thai and an English value", missing.Count == 0 && missingEn.Count == 0,
              string.Join(", ", missing.Concat(missingEn)));

        var missingRes = R(@"\{StaticResource\s+([\w.\-]+)\}", xaml).Distinct()
            .Where(k => !shared.Contains($"x:Key=\"{k}\"", StringComparison.Ordinal)).ToList();
        Check("X2", "static: every {StaticResource} exists", missingRes.Count == 0, string.Join(", ", missingRes));

        var dyn = R(@"\{DynamicResource\s+([\w.\-]+)\}", xaml).Distinct().ToList();
        var missingDyn = themes.SelectMany(f =>
        {
            string t = File.ReadAllText(f) + shared;
            return dyn.Where(k => !t.Contains($"x:Key=\"{k}\"", StringComparison.Ordinal)).Select(k => $"{Path.GetFileNameWithoutExtension(f)}:{k}");
        }).ToList();
        Check("X3", $"static: every {{DynamicResource}} ({dyn.Count}) is a token of all {themes.Count} themes", missingDyn.Count == 0, string.Join(", ", missingDyn));

        var hardColours = R(@"(?:Background|Foreground|BorderBrush|Fill|Stroke|Color)=""(#[0-9A-Fa-f]{3,8}|White|Black|Red|Green|Blue|Gray|Yellow|Orange)""", xaml).ToList();
        Check("X4", "static: no hard-coded colour on the page (light and dark themes both come from tokens)", hardColours.Count == 0, string.Join(", ", hardColours));

        var types = new[] { typeof(ModelManagerViewModel), typeof(RecommendedModel), typeof(QuantPlan), typeof(ModelInfo), typeof(ModelSource),
                            typeof(ModelCategoryOption), typeof(System.Windows.Controls.ItemsControl), typeof(System.Windows.Controls.Primitives.ToggleButton),
                            typeof(System.Collections.ICollection) };
        var paths = R(@"\{Binding\s+(?:Path=)?([\w.]+)", xaml).Concat(R(@"<Binding\s+Path=""([\w.]+)""", xaml))
            .Select(p => p.StartsWith("DataContext.", StringComparison.Ordinal) ? p["DataContext.".Length..] : p).Distinct().ToList();
        var bad = paths.Where(p =>
        {
            // Walk every segment: "SelectedPlan.Name", "RecommendedModels.Count".
            var segs = p.Split('.');
            var candidates = types.Where(t => t.GetProperty(segs[0]) != null).ToList();
            if (candidates.Count == 0) return !(segs[0] is "Source" or "Converter");
            foreach (var t0 in candidates)
            {
                Type? t = t0;
                bool ok = true;
                foreach (var s in segs)
                {
                    var prop = t?.GetProperty(s) ?? (s == "Count" ? typeof(System.Collections.ICollection).GetProperty("Count") : null);
                    if (prop == null) { ok = false; break; }
                    t = prop.PropertyType;
                }
                if (ok) return false;
            }
            return true;
        }).ToList();
        Check("X5", $"static: every {{Binding}} path ({paths.Count}) names real properties", bad.Count == 0, string.Join(", ", bad));

        // Properties that bind TwoWay by default (Value, IsChecked, Text, SelectedItem/Value) on a read-only property throw
        // at the first layout — the render crashed exactly so on VramUsedPercent.
        var twoWay = R(@"<(?:TextBox|ComboBox|ListBox|ProgressBar|Slider|CheckBox|ToggleButton|RadioButton)\b[^>]*?\b(?:Text|SelectedItem|SelectedValue|Value|IsChecked)=""(\{Binding [^""]*\})""", xaml)
            .Where(b => !b.Contains("Mode=OneWay")).Select(b => System.Text.RegularExpressions.Regex.Match(b, @"\{Binding\s+(?:Path=)?([\w.]+)").Groups[1].Value)
            .Select(p => p.StartsWith("DataContext.", StringComparison.Ordinal) ? p["DataContext.".Length..] : p).Where(p => !p.Contains('.')).Distinct()
            .Where(p => types.Select(t => t.GetProperty(p)).FirstOrDefault(pi => pi != null) is { } pi && (pi.SetMethod == null || !pi.SetMethod.IsPublic)).ToList();
        Check("X6", "static: no two-way binding on a read-only property", twoWay.Count == 0, string.Join(", ", twoWay));
    }

    static void AdvisorTests()
    {
        var catalog = HuggingFaceService.Catalog;

        // ── The catalog itself ──
        CaseSync("C1", () =>
        {
            var problems = new List<string>();
            foreach (var m in catalog)
            {
                if (m.Quants.Count == 0) problems.Add($"{m.Id}: no quants");
                var def = m.Quants.FirstOrDefault(q => q.FileName == m.FileName);
                if (def == null) problems.Add($"{m.Id}: default file {m.FileName} is not one of its quants");
                else if (def.SizeBytes != m.ApproxSizeBytes) problems.Add($"{m.Id}: ApproxSizeBytes ≠ default quant size");
                if (m.Quants.Any(q => string.IsNullOrEmpty(q.Name))) problems.Add($"{m.Id}: a quant without a recognised name");
                if (m.Quants.Select(q => q.Name).Distinct().Count() != m.Quants.Count) problems.Add($"{m.Id}: duplicate quant");
                if (string.IsNullOrEmpty(m.Architecture)) problems.Add($"{m.Id}: no architecture");
                if (m.NativeContext <= 0 || m.TotalParamsB <= 0) problems.Add($"{m.Id}: no context / parameter count");
                if (ModelAdvisor.AgeMonths(m.Released, new DateTime(2026, 10, 19)) > 60) problems.Add($"{m.Id}: bad Released '{m.Released}'");
                if (m.Tasks == ModelTasks.None) problems.Add($"{m.Id}: no tasks");
                if (m.Tasks.HasFlag(ModelTasks.Vision) != (m.VisionScore > 0)) problems.Add($"{m.Id}: Vision flag and VisionScore disagree");
                if (m.ActiveParamsB > m.TotalParamsB) problems.Add($"{m.Id}: active > total");
            }
            if (catalog.Select(m => m.Id).Distinct().Count() != catalog.Count) problems.Add("duplicate Id");
            Check("C1", $"{catalog.Count} catalog models: quants, default file, architecture, context, tasks are consistent", problems.Count == 0, string.Join("\n", problems));
        });
        CaseSync("C2", () =>
        {
            var current = catalog.Where(m => !m.IsLegacy).ToList();
            bool fresh = current.Count(m => ModelAdvisor.AgeMonths(m.Released, new DateTime(2026, 10, 19)) <= 12) >= current.Count * 2 / 3;
            bool claude = current.Any(m => m.Id == "qwen3.8-27b") && current.Any(m => m.Id.StartsWith("gemma-4-"));
            bool thai = current.Count(m => m.Tasks.HasFlag(ModelTasks.Thai) && m.ThaiScore >= 5) >= 2;
            bool small = current.Any(m => m.ApproxSizeBytes < 1_500_000_000L);
            Check("C2", "current catalog: mostly ≤ 12 months old, has Qwen3.8 + Gemma 4, ≥ 2 Thai-first models, a < 1.5 GB model",
                fresh && claude && thai && small, $"fresh={fresh} flagships={claude} thai={thai} small={small}");
        });
        CaseSync("C3", () =>
        {
            var loc = Sp.GetRequiredService<LocalizationService>();
            var missing = catalog.Where(m => loc.T("modelsm.desc." + m.Id) == "modelsm.desc." + m.Id).Select(m => m.Id).ToList();
            Check("C3", "every catalog model has a localized description key", missing.Count == 0, string.Join(", ", missing));
        });
        CaseSync("C4", () =>
        {
            // The bundled llama.cpp of the main checkout (llama-backend is not in git). Every CURRENT model must load
            // with it — a catalog model it cannot load is a Download button that ends in an error.
            string? dll = new[]
            {
                LlamaBackendInfo.FindLlamaDll(null),
                @"E:\Code\ClaudeClient\CluadeX\llama-backend\llama.dll",
            }.FirstOrDefault(p => p != null && File.Exists(p));
            if (dll == null) { Console.WriteLine("  SKIP C4     no llama-backend\\llama.dll on this machine"); return; }
            bool knowsNew = LlamaBackendInfo.SupportsArchitectureIn(dll, "qwen35") == true
                            && LlamaBackendInfo.SupportsArchitectureIn(dll, "gemma4") == true;
            bool rejects = LlamaBackendInfo.SupportsArchitectureIn(dll, "qwen4exp") == false
                           && LlamaBackendInfo.SupportsArchitectureIn(dll, "glm5-next") == false
                           && LlamaBackendInfo.SupportsArchitectureIn(dll, "qwen3") == true    // not fooled by "qwen35"/"qwen3moe"
                           && LlamaBackendInfo.SupportsArchitectureIn(dll, "qwen2") == true;   // the linker stores it as the tail of "rwkv6qwen2"
            var unsupported = catalog.Where(m => LlamaBackendInfo.SupportsArchitectureIn(dll, m.Architecture) != true)
                                     .Select(m => $"{m.Id} ({m.Architecture})").ToList();
            Check("C4", $"llama.dll arch table read ({Path.GetFileName(Path.GetDirectoryName(dll))}): knows qwen35/gemma4, not qwen4exp/glm5-next; every catalog arch is loadable",
                knowsNew && rejects && unsupported.Count == 0, $"knowsNew={knowsNew} rejects={rejects} unsupported: {string.Join(", ", unsupported)}");
        });

        // ── Hardware profile arithmetic ──
        CaseSync("H1", () =>
        {
            var busy = new HardwareProfile { VramTotalMB = 8192, VramFreeMB = 5221, CanOffload = true, SystemRamMB = 32768 };
            var loaded = new HardwareProfile { VramTotalMB = 8192, VramFreeMB = 1200, CanOffload = true, SystemRamMB = 32768, LocalModelLoaded = true };
            var cpuForced = new HardwareProfile { VramTotalMB = 8192, CanOffload = false, SystemRamMB = 32768 };
            Check("H1", "usable VRAM: reserve when free is unknown, free VRAM when other apps hold some, ignores our own loaded model, 0 when offload is off",
                Pc8.UsableVramMB == 8192 - 1228 && busy.UsableVramMB == 5221 - 300 && loaded.UsableVramMB == 8192 - 1228 && cpuForced.UsableVramMB == 0,
                $"{Pc8.UsableVramMB} {busy.UsableVramMB} {loaded.UsableVramMB} {cpuForced.UsableVramMB}");
        });
        CaseSync("H2", () =>
        {
            bool known = GpuDetectionService.EstimateGpuBandwidthGBs("NVIDIA GeForce GTX 1070 Ti", 8192) == 256
                         && GpuDetectionService.EstimateGpuBandwidthGBs("NVIDIA GeForce RTX 4070 Ti SUPER", 16384) == 672
                         && GpuDetectionService.EstimateGpuBandwidthGBs("NVIDIA GeForce RTX 3060 Ti", 8192) == 448;
            bool guessed = GpuDetectionService.EstimateGpuBandwidthGBs("Some Future GPU", 16384) == 448
                           && GpuDetectionService.EstimateGpuBandwidthGBs("", 0) == 0;
            Check("H2", "GPU bandwidth: known cards by name (Ti / SUPER before the plain model), unknown ones by VRAM", known && guessed);
        });

        // ── Automatic quant choice ──
        CaseSync("Q1", () =>
        {
            var (pick, all) = ModelAdvisor.PickQuant(Cat("qwen3.5-9b"), Pc24, Ctx);
            Check("Q1", "24 GB card, 9B model: the best quality below Q8_0 (Q6_K), fully on the GPU",
                pick.Quant.Name == "Q6_K" && pick.Tier == ModelFitTier.Excellent && all.Count == Cat("qwen3.5-9b").Quants.Count,
                $"picked {pick.Quant.Name} {pick.Tier}");
        });
        CaseSync("Q2", () =>
        {
            var (pick, _) = ModelAdvisor.PickQuant(Cat("qwen3.5-0.8b"), Pc24, Ctx);
            Check("Q2", "small model with room: Q8_0 is allowed (cheap at 0.8B)", pick.Quant.Name == "Q8_0", pick.Quant.Name);
        });
        CaseSync("Q3", () =>
        {
            var (pick, all) = ModelAdvisor.PickQuant(Cat("qwen3.5-9b"), Pc8, Ctx);
            var q5 = all.First(e => e.Quant.Name == "Q5_K_M");
            Check("Q3", "8 GB card, 9B: Q4_K_M fits fully (not Q3_K_M just for headroom); Q5_K_M would spill",
                pick.Quant.Name == "Q4_K_M" && pick.GpuFraction >= 1 && q5.GpuFraction < 1,
                $"picked {pick.Quant.Name} {pick.Tier} gpu={pick.GpuFraction:0.00}; Q5 gpu={q5.GpuFraction:0.00}");
        });
        CaseSync("Q4", () =>
        {
            var busy = new HardwareProfile { GpuName = Pc8.GpuName, GpuCount = 1, VramTotalMB = 8192, VramFreeMB = 5221, CanOffload = true,
                                             GpuBandwidthGBs = 256, SystemRamMB = 32768, RamBandwidthGBs = 51.2 };
            var (pick, all) = ModelAdvisor.PickQuant(Cat("qwen3.5-9b"), busy, Ctx);
            Check("Q4", "8 GB card with 3 GB taken by other apps: 9B no longer fits → Q4_K_M split, not a crawling Q3_K_M",
                pick.Quant.Name == "Q4_K_M" && pick.Tier == ModelFitTier.Partial && pick.GpuFraction is > 0.5 and < 1,
                $"picked {pick.Quant.Name} {pick.Tier} gpu={pick.GpuFraction:0.00} tps={pick.TokensPerSecond:0.0}");
        });
        CaseSync("Q5", () =>
        {
            var (moe, _) = ModelAdvisor.PickQuant(Cat("qwen3.6-35b-a3b"), Pc8, Ctx);
            var (dense, _) = ModelAdvisor.PickQuant(Cat("qwen3.8-27b"), Pc8, Ctx);
            Check("Q5", "8 GB card: a 35B-A3B MoE mostly in RAM still runs (Partial, ≥ 10 tok/s); a dense 27B mostly in RAM is Poor (< 6 tok/s)",
                moe.Tier == ModelFitTier.Partial && moe.TokensPerSecond >= 10 && dense.Tier == ModelFitTier.Poor && dense.TokensPerSecond < 6,
                $"moe {moe.Quant.Name} {moe.Tier} {moe.TokensPerSecond:0.0} tok/s · dense {dense.Quant.Name} {dense.Tier} {dense.TokensPerSecond:0.0} tok/s");
        });
        CaseSync("Q6", () =>
        {
            var (pick, _) = ModelAdvisor.PickQuant(Cat("qwen3.8-27b"), PcTwo12, Ctx);
            var reasons = ModelAdvisor.Reasons(Cat("qwen3.8-27b"), pick, PcTwo12, ModelTasks.None, new DateTime(2026, 10, 19));
            Check("Q6", "two 12 GB cards add up: 27B dense runs fully on the GPUs above Q4, and the reason says it is split across them",
                pick.GpuFraction >= 1 && Array.IndexOf(ModelAdvisor.QualityOrder, pick.Quant.Name) < Array.IndexOf(ModelAdvisor.QualityOrder, "Q4_K_M")
                && reasons.Any(r => r.Key == "modelsm.why.fitsMultiGpu"),
                $"{pick.Quant.Name} gpu={pick.GpuFraction:0.00} reasons={string.Join(",", reasons.Select(r => r.Key))}");
        });
        CaseSync("Q7", () =>
        {
            var (tiny, _) = ModelAdvisor.PickQuant(Cat("qwen3.5-0.8b"), PcCpu, Ctx);
            var (mid, _) = ModelAdvisor.PickQuant(Cat("qwen3.5-4b"), PcCpu, Ctx);
            var (huge, _) = ModelAdvisor.PickQuant(Cat("gemma-4-31b"), PcCpu, Ctx);
            Check("Q7", "no GPU: 0.8B is Good on the CPU, 4B is Partial (usable), Gemma 4 31B does not fit 16 GB of RAM (TooLarge, smallest file shown)",
                tiny.Tier == ModelFitTier.Good && tiny.GpuFraction == 0 && mid.Tier is ModelFitTier.Partial or ModelFitTier.Good
                && huge.Tier == ModelFitTier.TooLarge && huge.Quant.SizeBytes == Cat("gemma-4-31b").Quants.Min(q => q.SizeBytes),
                $"tiny {tiny.Tier} {tiny.TokensPerSecond:0} · 4B {mid.Tier} {mid.TokensPerSecond:0.0} · 31B {huge.Tier} {huge.Quant.Name}");
        });
        CaseSync("Q8", () =>
        {
            // Longer context = bigger KV cache = less room; a hybrid-attention model's cache is far smaller.
            var dense = Cat("qwen2.5-coder-14b");
            var hybrid = Cat("qwen3.5-9b");
            int d8 = ModelAdvisor.KvCacheMB(dense, 8192), d32 = ModelAdvisor.KvCacheMB(dense, 32768), dCap = ModelAdvisor.KvCacheMB(dense, 1_000_000);
            int h32 = ModelAdvisor.KvCacheMB(hybrid, 32768);
            Check("Q8", "KV cache grows with context, is capped at the model's native context, and is smaller for hybrid (linear-attention) models",
                d32 > d8 * 3.5 && dCap == ModelAdvisor.KvCacheMB(dense, dense.NativeContext) && h32 < d32 / 2,
                $"14B dense: 8K={d8} 32K={d32} cap={dCap} MB · 9B hybrid 32K={h32} MB");
        });
        CaseSync("Q9", () =>
        {
            double a = ModelAdvisor.TokensPerSecond(5_000_000_000, 1, 1, Pc8);
            double b = ModelAdvisor.TokensPerSecond(5_000_000_000, 1, 0.5, Pc8);
            double c = ModelAdvisor.TokensPerSecond(5_000_000_000, 0.1, 0.5, Pc8);
            Check("Q9", "speed: full GPU > half in RAM; a MoE reading 10 % of its weights per token is much faster",
                a > b * 1.5 && c > b * 5 && a is > 15 and < 60, $"full {a:0.0} · half {b:0.0} · MoE half {c:0.0} tok/s");
        });

        // ── Picks and reasons ──
        CaseSync("P1", () =>
        {
            var today = new DateTime(2026, 10, 19);
            var ranked = catalog.Select(m => (m, e: ModelAdvisor.PickQuant(m, Pc8, Ctx).Pick))
                                .Where(x => ModelAdvisor.IsPickable(x.m, x.e, ModelTasks.Coding))
                                .OrderByDescending(x => ModelAdvisor.Score(x.m, x.e, ModelTasks.Coding, today)).ToList();
            var top = ranked.Take(3).Select(x => x.m.Id).ToList();
            bool ok = top.Count == 3 && ranked.All(x => !x.m.IsLegacy && x.m.CodingScore >= 3 && x.e.Tier != ModelFitTier.TooLarge && x.e.Tier != ModelFitTier.Poor)
                      && !top.Contains("qwen3.8-27b");
            Check("P1", "coding picks for an 8 GB card: current coding models that run here, never a legacy / poor-fit one", ok, string.Join(", ", top));
        });
        CaseSync("P2", () =>
        {
            var visionCandidates = catalog.Where(m => ModelAdvisor.IsPickable(m, ModelAdvisor.PickQuant(m, Pc24, Ctx).Pick, ModelTasks.Vision)).ToList();
            var thaiCandidates = catalog.Where(m => ModelAdvisor.IsPickable(m, ModelAdvisor.PickQuant(m, Pc24, Ctx).Pick, ModelTasks.Thai)).ToList();
            Check("P2", "vision picks only models that see images; Thai picks only models rated for Thai",
                visionCandidates.Count > 0 && visionCandidates.All(m => m.VisionScore >= 3)
                && thaiCandidates.Count > 0 && thaiCandidates.All(m => m.ThaiScore >= 3),
                $"vision: {string.Join(", ", visionCandidates.Select(m => m.Id))} · thai: {string.Join(", ", thaiCandidates.Select(m => m.Id))}");
        });
        CaseSync("P3", () =>
        {
            var today = new DateTime(2026, 10, 19);
            var m = Cat("qwen3.6-35b-a3b");
            var e = ModelAdvisor.PickQuant(m, Pc8, Ctx).Pick;
            var keys = ModelAdvisor.Reasons(m, e, Pc8, ModelTasks.Coding, today).Select(r => r.Key).ToList();
            var unsupported = new RecommendedModel { Id = "x", Architecture = "qwen4exp", ArchSupported = false, TotalParamsB = 9, Quants = Cat("qwen3.5-9b").Quants };
            var ue = ModelAdvisor.PickQuant(unsupported, Pc24, Ctx).Pick;
            var ukeys = ModelAdvisor.Reasons(unsupported, ue, Pc24, ModelTasks.None, today).Select(r => r.Key).ToList();
            Check("P3", "reasons: split + speed + MoE + coding for a MoE coder; an unknown architecture says 'needs a newer llama.cpp' and is never pickable",
                keys.Contains("modelsm.why.split") && keys.Contains("modelsm.why.speed") && keys.Contains("modelsm.why.moe") && keys.Contains("modelsm.why.coding")
                && ukeys.SequenceEqual(new[] { "modelsm.why.newerBackend" }) && !ModelAdvisor.IsPickable(unsupported, ue, ModelTasks.None),
                $"{string.Join(",", keys)} | {string.Join(",", ukeys)}");
        });

        // ── The page's view-model ──
        CaseSync("V1", () =>
        {
            var vm = Sp.GetRequiredService<ModelManagerViewModel>();
            vm.ApplyHardware(Pc8);
            vm.ShowLegacy = false;
            vm.CatalogFilter = "";
            vm.SelectedSort = "Best";
            vm.SelectedTask = "Coding";
            var picks = vm.TopPicks.ToList();
            bool picksOk = picks.Count is >= 1 and <= 3 && picks.Select((p, i) => p.PickRank == i + 1).All(b => b)
                           && picks.All(p => p.Tasks.HasFlag(ModelTasks.Coding) && p.SelectedPlan != null && p.Reasons.Count > 0);
            bool listOk = vm.RecommendedModels.Count > 0 && vm.RecommendedModels.All(m => m.Tasks.HasFlag(ModelTasks.Coding) && !m.IsLegacy);
            bool plansOk = vm.RecommendedModels.All(m => m.Plans.Count == m.AllQuants.Count && m.Plans.Count(p => p.IsAuto) == 1
                                                         && (m.IsInstalled || m.SelectedPlan!.IsAuto) && m.Plans.SequenceEqual(m.Plans.OrderBy(p => p.SizeBytes)));
            Check("V1", "Coding on an 8 GB PC: 1-3 ranked picks with reasons, list holds only current coding models, each card has every quant (smallest first) and one ★ pick",
                picksOk && listOk && plansOk, $"picks={string.Join(", ", picks.Select(p => p.Id))} list={vm.RecommendedModels.Count}");
        });
        CaseSync("V2", () =>
        {
            var vm = Sp.GetRequiredService<ModelManagerViewModel>();
            vm.SelectedTask = "All";
            int current = vm.RecommendedModels.Count;
            vm.ShowLegacy = true;
            int withLegacy = vm.RecommendedModels.Count;
            vm.CatalogFilter = "typhoon";
            bool typhoonOnly = vm.RecommendedModels.Count == 2 && vm.RecommendedModels.All(m => m.Id.StartsWith("typhoon"));
            vm.CatalogFilter = "";
            vm.SelectedSort = "Smallest";
            var sizes = vm.RecommendedModels.Select(m => m.SelectedPlan?.SizeBytes ?? 0).ToList();
            bool sorted = sizes.SequenceEqual(sizes.OrderBy(s => s));
            vm.SelectedSort = "Newest";
            var rel = vm.RecommendedModels.Select(m => m.Released).ToList();
            bool newest = rel.SequenceEqual(rel.OrderByDescending(r => r, StringComparer.Ordinal));
            vm.SelectedSort = "Best";
            vm.ShowLegacy = false;
            Check("V2", "older models hidden by default and shown on request; text filter; Smallest / Newest sorts",
                withLegacy == current + catalog.Count(m => m.IsLegacy) && typhoonOnly && sorted && newest,
                $"current={current} withLegacy={withLegacy} typhoon={typhoonOnly} smallest={sorted} newest={newest}");
        });
        CaseSync("V3", () =>
        {
            var vm = Sp.GetRequiredService<ModelManagerViewModel>();
            var card = vm.RecommendedModels.First(m => m.Plans.Count > 2);
            var other = card.Plans.First(p => !p.IsAuto);
            var seen = new List<string>();
            card.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");
            card.SelectedPlan = other;
            Check("V3", "choosing another quant updates the card (fit badge, speed, size, file the Download button fetches)",
                card.SelectedFileName == other.FileName && card.FitLabel == other.FitLabel
                && seen.Contains(nameof(RecommendedModel.FitLabel)) && seen.Contains(nameof(RecommendedModel.DownloadSizeText)) && seen.Contains(nameof(RecommendedModel.SpeedText)),
                string.Join(",", seen));
        });
        CaseSync("V4", () =>
        {
            var vm = Sp.GetRequiredService<ModelManagerViewModel>();
            var loc = Sp.GetRequiredService<LocalizationService>();
            string before = loc.CurrentLanguage;
            loc.SetLanguage("th");
            var taskList = vm.TaskOptions;
            var m = vm.RecommendedModels.First();
            bool thai = m.DescriptionText.Any(ch => ch >= '\u0E00' && ch <= '\u0E7F') && m.Reasons.Any(r => r.Any(ch => ch >= '\u0E00' && ch <= '\u0E7F'))
                        && vm.HwRamText.Contains("RAM") && !vm.HwRamText.StartsWith("modelsm.")
                        && vm.TaskOptions.All(t => !t.Label.StartsWith("modelsm."));
            bool sameLists = ReferenceEquals(taskList, vm.TaskOptions);   // replacing the lists cleared the chips' selection
            loc.SetLanguage(before);
            Check("V4", "switching to Thai re-labels descriptions, reasons, hardware card and job chips in place (no raw keys, selection kept)",
                thai && sameLists && vm.SelectedTask == "All", m.DescriptionText);
        });
        CaseSync("V6", () =>
        {
            var vm = Sp.GetRequiredService<ModelManagerViewModel>();
            vm.ApplyHardware(Pc8);
            var card = vm.RecommendedModels.First(m => m.Plans.Count > 2 && !m.IsInstalled);
            var manual = card.Plans.First(p => !p.IsAuto);
            card.SelectedPlan = manual;
            vm.ApplyHardware(Pc24);                 // a new hardware reading re-evaluates every quant
            bool kept = card.SelectedPlan?.Name == manual.Name;
            var auto = vm.RecommendedModels.First(m => m.Plans.Count > 2 && !m.IsInstalled && m != card);
            bool autoFollows = auto.SelectedPlan!.IsAuto;
            Check("V6", "a quant chosen by hand survives a refresh; untouched cards follow the new automatic pick",
                kept && autoFollows && vm.ShowNoPicks == false, $"{card.Id}: {manual.Name} → {card.SelectedPlan?.Name}");
            vm.ApplyHardware(Pc8);
        });
        CaseSync("X1", StaticXaml);
        CaseSync("V5", () =>
        {
            var vm = Sp.GetRequiredService<ModelManagerViewModel>();
            vm.ApplyHardware(PcCpu);
            bool cpu = vm.HwBackendText.Length > 0 && vm.TopPicks.All(p => p.SelectedPlan!.GpuFraction == 0)
                       && vm.RecommendedModels.Any(m => m.FitTier == ModelFitTier.TooLarge);
            vm.ApplyHardware(Pc8);
            Check("V5", "a PC without a GPU re-ranks everything for the CPU (no GPU share anywhere, big models marked too large)", cpu);
        });
    }
}
