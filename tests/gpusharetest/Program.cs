using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CluadeX.Services;
using CluadeX.Services.Gpu;
using CluadeX.Services.Pet;
using CluadeX.Services.Providers;
using CluadeX.ViewModels;

namespace GpuShareTest;

/// <summary>
/// Contract harness for the GPU share slider (owner 2026-11-06: "กำหนดว่าจะใช้ความสามารถของ gpu กี่เปอร์เซ็นต์").
///
/// Links the BUILT CluadeX.dll. No model is loaded, no process is started, the GPU is never touched: the -ngl /
/// num_gpu numbers are pure math, the layer count comes from synthetic GGUF headers, Ollama's answer is a JSON string,
/// and settings live next to this exe (portable.txt).
///
///   dotnet build ../../CluadeX/CluadeX.csproj      (or -p:OutDir=&lt;dir&gt;\ while the app runs)
///   dotnet run                                      (or dotnet run -p:CluadeXBin=&lt;dir&gt;\)
/// </summary>
internal static class Program
{
    static int Pass, Fail;
    static readonly List<string> Problems = new();
    static string Tmp = "";

    [STAThread]
    static int Main()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        Tmp = Path.Combine(Path.GetTempPath(), "cluadex-gpusharetest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Tmp);
        try
        {
            Console.WriteLine("── layers from a percentage (llama-server / LLamaSharp -ngl)");
            Layers();
            Console.WriteLine("── Ollama num_gpu");
            Ollama();
            Console.WriteLine("── what the popup says (MB / layers)");
            Describe();
            Console.WriteLine("── layer count from the GGUF header");
            Gguf();
            Console.WriteLine("── settings persist");
            Persist();
            Console.WriteLine("── first-use hint: shown now and then, then never again");
            Hint();
            Console.WriteLine("── the view model drives it the same way");
            ViewModel();
            Console.WriteLine("── every string in English and Thai; the pet has its lines");
            Strings();
            Console.WriteLine("── the real XAML renders in every theme");
            string shots = Path.Combine(AppContext.BaseDirectory, "renders");
            Directory.CreateDirectory(shots);
            Render.Run(Check, shots);
        }
        catch (Exception ex) { Fail++; Problems.Add("FATAL " + ex); Console.WriteLine("FATAL " + ex); }
        finally { try { Directory.Delete(Tmp, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine($"gpusharetest: {Pass} passed, {Fail} failed");
        foreach (var p in Problems) Console.WriteLine("  " + p);
        return Fail == 0 ? 0 : 1;
    }

    static void Check(string id, string name, bool ok, string detail = "")
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS {id,-5} {name}"); return; }
        Fail++; Problems.Add($"FAIL {id} {name} {detail}");
        Console.WriteLine($"  FAIL {id,-5} {name}" + (detail.Length > 0 ? "  → " + detail : ""));
    }

    static void Eq<T>(string id, string name, T actual, T expected)
        => Check(id, name, EqualityComparer<T>.Default.Equals(actual, expected), $"got {actual}, expected {expected}");

    // ─────────────────────────────────────────────────────────────────────────

    static void Layers()
    {
        // A 7B Qwen2.5: 28 blocks → 29 offloadable layers (llama.cpp: "offloaded 29/29 layers").
        Eq("L1", "TotalLayers = blocks + output layer", GpuShare.TotalLayers(28), 29);
        Eq("L2", "100 % leaves 'all' alone (-1)", GpuShare.ScaleLayers(-1, 100, 28), -1);
        Eq("L3", "100 % leaves an explicit -ngl alone", GpuShare.ScaleLayers(20, 100, 28), 20);
        Eq("L4", "50 % of all 29 layers = 14", GpuShare.ScaleLayers(-1, 50, 28), 14);
        Eq("L5", "75 % of 29 = 21", GpuShare.ScaleLayers(-1, 75, 28), 21);
        Eq("L6", "0 % = CPU only", GpuShare.ScaleLayers(-1, 0, 28), 0);
        Eq("L7", "an explicit 0 stays 0 at any share", GpuShare.ScaleLayers(0, 80, 28), 0);
        Eq("L8", "50 % of an explicit 20 = 10", GpuShare.ScaleLayers(20, 50, 28), 10);
        Eq("L9", "an explicit 999 is capped to the model's 29 first", GpuShare.ScaleLayers(999, 50, 28), 14);
        Eq("L10", "1 % still keeps one layer on the GPU", GpuShare.ScaleLayers(-1, 1, 28), 1);
        Eq("L11", "unknown layer count: 'all' cannot be scaled", GpuShare.ScaleLayers(-1, 50, 0), -1);
        Eq("L12", "unknown layer count: an explicit N still scales", GpuShare.ScaleLayers(20, 50, 0), 10);
        Eq("L13", "out-of-range share is clamped (150 → 100)", GpuShare.ScaleLayers(-1, 150, 28), -1);
        Eq("L14", "out-of-range share is clamped (-5 → 0)", GpuShare.ScaleLayers(-1, -5, 28), 0);
        // Qwen3-Coder-30B-A3B: 48 blocks → 49 layers — the owner's 7.3/8.2 GB case.
        Eq("L15", "Qwen3-Coder-30B (48 blocks) at 60 % = 29 layers", GpuShare.ScaleLayers(-1, 60, 48), 29);

        Eq("E1", "llama-server: 'all' maps to 999 at 100 %", GpuShare.EffectiveNgl(-1, "Auto", 100, 28, 999), 999);
        Eq("E2", "llama-server: 50 % → -ngl 14", GpuShare.EffectiveNgl(-1, "Auto", 50, 28, 999), 14);
        Eq("E3", "LLamaSharp: 'all' maps to 99", GpuShare.EffectiveNgl(-1, null, 100, 28, 99), 99);
        Eq("E4", "CPU backend → 0 whatever the share", GpuShare.EffectiveNgl(-1, "CPU", 100, 28, 999), 0);
        Eq("E5", "unknown layer count at 50 % → 'all' (cap cannot apply)", GpuShare.EffectiveNgl(-1, "Auto", 50, 0, 999), 999);

        Check("W1", "router wants the GPU at 50 %", GpuShare.WantsGpu(-1, "Auto", 50));
        Check("W2", "router: 0 % = CPU route", !GpuShare.WantsGpu(-1, "Auto", 0));
        Check("W3", "router: CPU backend = CPU route", !GpuShare.WantsGpu(-1, "CPU", 100));
        Check("W4", "router: GpuLayerCount 0 = CPU route", !GpuShare.WantsGpu(0, "Auto", 100));
    }

    static void Ollama()
    {
        Eq("O1", "100 % → num_gpu left out", GpuShare.OllamaNumGpu(100, 48), (int?)null);
        Eq("O2", "0 % → num_gpu 0", GpuShare.OllamaNumGpu(0, 48), (int?)0);
        Eq("O3", "50 % of 49 layers → 24", GpuShare.OllamaNumGpu(50, 48), (int?)24);
        Eq("O4", "unknown layer count → left out", GpuShare.OllamaNumGpu(50, 0), (int?)null);
        Eq("O5", "0 % works even without a layer count", GpuShare.OllamaNumGpu(0, 0), (int?)0);

        // /api/show answer shape (model_info keyed by "<arch>.block_count").
        using var show = JsonDocument.Parse("""
            {"capabilities":["completion","tools"],
             "model_info":{"general.architecture":"qwen3moe","general.parameter_count":30532122624,
                           "qwen3moe.block_count":48,"qwen3moe.context_length":262144,"clip.block_count":27}}
            """);
        Eq("O6", "block_count read from /api/show model_info (arch-matched, clip ignored)", OllamaProvider.BlockCountFromShow(show.RootElement), 48);
        using var none = JsonDocument.Parse("""{"capabilities":["completion"]}""");
        Eq("O7", "no model_info → 0", OllamaProvider.BlockCountFromShow(none.RootElement), 0);
    }

    static void Describe()
    {
        long fourGb = 4L * 1024 * 1024 * 1024;
        var p = GpuShare.Describe(50, -1, "Auto", 28, 8192, fourGb);
        Eq("D1", "50 % of an 8 GB card = 4096 MB budget", p.BudgetMb, 4096);
        Eq("D2", "14 / 29 layers", (p.Layers, p.TotalLayers), (14, 29));
        Eq("D3", "weights on the GPU ≈ 4096 × 14 / 29 MB", p.WeightsOnGpuMb, (int)Math.Round(4096.0 * 14 / 29));
        var full = GpuShare.Describe(100, -1, "Auto", 28, 8192, fourGb);
        Check("D4", "100 %: all layers, the whole file on the GPU", full.Layers == -1 && full.WeightsOnGpuMb == 4096 && !full.CapUnknown);
        var unknown = GpuShare.Describe(50, -1, "Auto", 0, 8192, fourGb);
        Check("D5", "unknown layer count at 50 % is flagged CapUnknown", unknown.CapUnknown);
        var cpu = GpuShare.Describe(60, -1, "CPU", 28, 8192, fourGb);
        Check("D6", "CPU backend: 0 layers, 0 MB of weights", cpu.Layers == 0 && cpu.WeightsOnGpuMb == 0);
        Eq("D7", "no GPU reading → budget 0", GpuShare.Describe(50, -1, "Auto", 28, 0, fourGb).BudgetMb, 0);
    }

    // ─── synthetic GGUF ──────────────────────────────────────────────────────

    static void Str(BinaryWriter w, string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }

    static string WriteGguf(string name, Action<BinaryWriter> kvs, ulong kvCount)
    {
        string path = Path.Combine(Tmp, name);
        using var w = new BinaryWriter(File.Create(path));
        w.Write(0x46554747u); w.Write(3u); w.Write(0UL); w.Write(kvCount);
        kvs(w);
        return path;
    }

    static void Gguf()
    {
        // general.architecture, a string, an array of strings, then qwen3.block_count (uint32) — the order llama.cpp writes.
        string ok = WriteGguf("qwen3.gguf", w =>
        {
            Str(w, "general.architecture"); w.Write(8u); Str(w, "qwen3");
            Str(w, "general.name"); w.Write(8u); Str(w, "Qwen3 8B");
            Str(w, "general.tags"); w.Write(9u); w.Write(8u); w.Write(2UL); Str(w, "a"); Str(w, "b");
            Str(w, "qwen3.context_length"); w.Write(4u); w.Write(40960u);
            Str(w, "qwen3.block_count"); w.Write(4u); w.Write(36u);
        }, 5);
        Eq("G1", "qwen3.block_count = 36", GgufMetadataReader.TryReadBlockCount(ok), 36);

        // A block_count of another prefix (e.g. a vision tower) before the model's own: ignored.
        string mixed = WriteGguf("mixed.gguf", w =>
        {
            Str(w, "general.architecture"); w.Write(8u); Str(w, "gemma3");
            Str(w, "clip.block_count"); w.Write(4u); w.Write(27u);
            Str(w, "gemma3.block_count"); w.Write(10u); w.Write(62UL);   // uint64 form
        }, 3);
        Eq("G2", "other prefixes ignored, uint64 value read (gemma3 = 62)", GgufMetadataReader.TryReadBlockCount(mixed), 62);

        string missing = WriteGguf("noblocks.gguf", w => { Str(w, "general.architecture"); w.Write(8u); Str(w, "llama"); }, 1);
        Eq("G3", "no block_count → 0", GgufMetadataReader.TryReadBlockCount(missing), 0);

        string junk = Path.Combine(Tmp, "junk.gguf");
        File.WriteAllBytes(junk, Encoding.ASCII.GetBytes("not a gguf at all"));
        Eq("G4", "not a GGUF → 0", GgufMetadataReader.TryReadBlockCount(junk), 0);
        Eq("G5", "missing file → 0", GgufMetadataReader.TryReadBlockCount(Path.Combine(Tmp, "nope.gguf")), 0);
        Eq("G6", "the existing arch reader still works on the same header", GgufMetadataReader.TryReadArchitecture(ok), "qwen3");

        // Real files, read-only, when present: the header only (a few KB), never loaded.
        foreach (var real in new[] { @"D:\DataAiModel\Qwen3.5-4B-Q6_K.gguf" })
            if (File.Exists(real))
            {
                int n = GgufMetadataReader.TryReadBlockCount(real);
                Check("G7", $"real header {Path.GetFileName(real)}: block_count {n}", n is > 0 and < 200);
            }
    }

    // ─── settings ────────────────────────────────────────────────────────────

    static SettingsService FreshSettings()
    {
        var s = new SettingsService();
        s.UpdateSettings(a => { a.GpuUsagePercent = 100; a.GpuShareHintDone = false; a.GpuShareHintShown = 0; a.GpuShareHintLastUtc = null; });
        return s;
    }

    static void Persist()
    {
        var s = FreshSettings();
        Check("P0", "harness runs portable (not the real ~/.cluadex)", s.IsPortable, s.DataRoot);
        var store = new GpuShareSettings(s);
        Eq("P1", "default is 100 % (no cap)", store.Percent, 100);
        store.SetPercent(40);
        var reread = new SettingsService();
        Eq("P2", "40 % survives a restart (read back from disk)", reread.Settings.GpuUsagePercent, 40);
        Check("P3", "moving the slider counts as using it", reread.Settings.GpuShareHintDone);
        store.SetPercent(150);
        Eq("P4", "150 is stored as 100", new SettingsService().Settings.GpuUsagePercent, 100);
        store.SetPercent(-3);
        Eq("P5", "-3 is stored as 0", new SettingsService().Settings.GpuUsagePercent, 0);
    }

    static void Hint()
    {
        var t0 = new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);
        Check("H1", "not in the first minute", !GpuShareHint.ShouldShow(false, 0, null, TimeSpan.FromMinutes(1), t0));
        Check("H2", "due after 2 minutes on screen", GpuShareHint.ShouldShow(false, 0, null, TimeSpan.FromMinutes(2), t0));
        Check("H3", "never once used", !GpuShareHint.ShouldShow(true, 0, null, TimeSpan.FromHours(5), t0));
        Check("H4", "not again within 3 hours", !GpuShareHint.ShouldShow(false, 1, t0, TimeSpan.FromHours(1), t0.AddHours(2)));
        Check("H5", "again after 3 hours", GpuShareHint.ShouldShow(false, 1, t0, TimeSpan.FromHours(1), t0.AddHours(3)));
        Check("H6", "stops for good after 6 shows", !GpuShareHint.ShouldShow(false, GpuShareHint.MaxShows, t0, TimeSpan.FromHours(9), t0.AddDays(2)));

        var s = FreshSettings();
        var store = new GpuShareSettings(s);
        Check("H7", "due on a fresh install after 2 min", store.HintDue(TimeSpan.FromMinutes(2), t0));
        store.NoteHintShown(t0);
        var back = new SettingsService().Settings;
        Check("H8", "shown count and time are remembered", back.GpuShareHintShown == 1 && back.GpuShareHintLastUtc == t0,
              $"{back.GpuShareHintShown} {back.GpuShareHintLastUtc:o}");
        Check("H9", "suppressed right after showing", !store.HintDue(TimeSpan.FromMinutes(30), t0.AddMinutes(10)));
        store.MarkUsed();
        Check("H10", "suppressed for good once used (\"Got it\" / popup opened)", !store.HintDue(TimeSpan.FromHours(9), t0.AddDays(3)));

        Eq("C1", "pet out and talking → the pet says it", GpuShareSettings.ChooseChannel(true, true, true), GpuShareHintChannel.Pet);
        Eq("C2", "pet put away → tooltip", GpuShareSettings.ChooseChannel(true, false, true), GpuShareHintChannel.Tooltip);
        Eq("C3", "pet told to be quiet → tooltip", GpuShareSettings.ChooseChannel(true, true, false), GpuShareHintChannel.Tooltip);
        Eq("C4", "pet off → tooltip", GpuShareSettings.ChooseChannel(false, false, false), GpuShareHintChannel.Tooltip);
    }

    static void ViewModel()
    {
        var s = FreshSettings();
        var loc = new LocalizationService();
        var vm = new GpuShareViewModel(s, loc, null!, null, () => null, null);
        var t = new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);
        // The status-bar poll ticks every 2 s.
        for (int i = 0; i < 50; i++) vm.Tick(true, t = t.AddSeconds(2));         // 100 s visible
        Check("V1", "no hint after 100 s of graph", !vm.HintOpen);
        for (int i = 0; i < 15; i++) vm.Tick(false, t = t.AddSeconds(2));        // hidden time does not count
        Check("V2", "hidden time does not count", !vm.HintOpen);
        for (int i = 0; i < 15; i++) vm.Tick(true, t = t.AddSeconds(2));         // 130 s visible
        Check("V3", "the hint floats up after 2 min of graph (no pet → tooltip)", vm.HintOpen);
        Eq("V4", "counted once", new SettingsService().Settings.GpuShareHintShown, 1);
        vm.DismissHintCommand.Execute(null);
        Check("V5", "\"Got it\" closes it and remembers", !vm.HintOpen && new SettingsService().Settings.GpuShareHintDone);
        for (int i = 0; i < 200; i++) vm.Tick(true, t = t.AddSeconds(2));
        Check("V6", "never again after that", !vm.HintOpen);

        vm.IsOpen = true;
        vm.Percent = 35;   // snaps are the slider's job; any int is clamped
        vm.FlushSave();
        Eq("V7", "the slider value is saved (flushed on close)", new SettingsService().Settings.GpuUsagePercent, 35);
        loc.SetLanguage("th");
        Check("V8", "the popup text is Thai in Thai", vm.ModeText.Any(c => c >= '\u0E00' && c <= '\u0E7F'), vm.ModeText);
        Check("V9", "Ollama/unloaded: 'applies on next load' note present", vm.ApplyNote.Length > 0);
        vm.Percent = 100;
        vm.FlushSave();
        Check("V10", "no reload offered without a loaded model", !vm.ShowReload);
        loc.SetLanguage("en");
    }

    static void Strings()
    {
        var type = typeof(SettingsService).Assembly.GetType("CluadeX.Services.Localization.GpuShareKeys")!;
        var keys = (Dictionary<string, Dictionary<string, string>>)type.GetField("Keys", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var bad = keys.Where(k => !k.Value.TryGetValue("en", out var en) || en.Length == 0
                                  || !k.Value.TryGetValue("th", out var th) || th.Length == 0
                                  || !(th.Any(c => c >= '\u0E00' && c <= '\u0E7F') || th == en)).Select(k => k.Key).ToList();
        Check("S1", $"{keys.Count} gpushare.* keys each have English and a Thai value", bad.Count == 0, string.Join(", ", bad));
        var loc = new LocalizationService();
        loc.SetLanguage("th");
        var unresolved = keys.Keys.Where(k => loc.T(k) == k).ToList();
        Check("S2", "the app's LocalizationService resolves them", unresolved.Count == 0, string.Join(", ", unresolved));
        loc.SetLanguage("en");

        // The XAML uses these keys: each must exist (a missing key renders as the raw key).
        foreach (var k in new[] { "gpushare.title", "gpushare.graphTip", "gpushare.close", "gpushare.reload", "gpushare.hint", "gpushare.hintDismiss" })
            Check("S3", $"XAML key {k} defined", keys.ContainsKey(k));

        string th = PetLines.Say(PetMoment.GpuShareHint, null, true, "x", 0);
        string en = PetLines.Say(PetMoment.GpuShareHint, null, false, "x", 1);
        Check("S4", "the pet has a Thai line for the hint", th.Contains("VRAM") && th.Any(c => c >= '\u0E00' && c <= '\u0E7F'), th);
        Check("S5", "and an English one", en.Contains("VRAM"), en);
        foreach (PetPersonality p in Enum.GetValues<PetPersonality>())
            Check("S6", $"a {p} pet says it too", PetLines.Say(PetMoment.GpuShareHint, p, true, "x", 2).Length > 0);
    }
}
