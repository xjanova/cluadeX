using System.Diagnostics;
using System.IO;
using System.Text;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace ArchTest;

/// <summary>
/// Contract harness: does CluadeX tell the user, before and during load, that a GGUF's architecture is newer than
/// the llama.cpp it would load it with — instead of a generic "format / memory / corrupt file" error?
///
/// Links the BUILT CluadeX.dll. Everything runs on synthetic files: a GGUF header with no tensors, and fake
/// llama.dll / llama-server.exe byte files. No model is loaded, no native llama.dll is loaded, no process is
/// started, the GPU is never touched. portable.txt keeps settings next to this exe.
///
///   dotnet build ../../CluadeX/CluadeX.csproj      (or -p:OutDir=&lt;dir&gt;\ while the app runs)
///   dotnet run                                      (or dotnet run -p:CluadeXBin=&lt;dir&gt;\)
///
/// The X* cases also read real files when they exist (read-only): the installed app's LLamaSharp runtimes and
/// D:\DataAiModel\Qwen3.5-4B-Q6_K.gguf — the owner's real failing case.
/// </summary>
internal static class Program
{
    static int Pass, Fail, Skip;
    static readonly List<string> Problems = new();
    static string Tmp = "";

    // What the fake in-process llama.dll knows: the LLamaSharp 0.26 situation — Qwen 3 / 3-Next / 3-VL, no Qwen 3.5.
    static readonly string[] InProcessArchs = { "llama", "rwkv6qwen2", "qwen3", "qwen3moe", "qwen3next", "qwen3vl", "gemma3" };
    // What a newer llama-server's llama.dll knows.
    static readonly string[] NewServerArchs = { "llama", "qwen3", "qwen35", "qwen35moe", "gemma4" };

    [STAThread]
    static int Main(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        try { return args.Length == 2 && args[0] == "--real-install" ? InstallerTests.RealInstall(args[1]) : Run(); }
        catch (Exception ex) { Console.WriteLine("FATAL " + ex); return 2; }
        finally { try { if (Tmp.Length > 0) Directory.Delete(Tmp, true); } catch { } }
    }

    static void Check(string id, string name, bool ok, string detail = "")
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS {id,-5} {name}"); return; }
        Fail++; Problems.Add($"FAIL {id} {name}");
        Console.WriteLine($"  FAIL {id,-5} {name}");
        if (detail.Length > 0) Console.WriteLine("            " + detail.Replace("\n", "\n            "));
    }

    static void SkipCase(string id, string why) { Skip++; Console.WriteLine($"  SKIP {id,-5} {why}"); }

    static void Case(string id, Action body)
    {
        try { body(); }
        catch (Exception ex)
        {
            Fail++; Problems.Add($"FAIL {id} threw {ex.GetType().Name}");
            Console.WriteLine($"  FAIL {id,-5} harness case threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    static int Run()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!File.Exists(Path.Combine(appDir, "portable.txt"))) { Console.WriteLine("FATAL portable.txt missing next to the exe"); return 2; }
        Tmp = Path.Combine(Path.GetTempPath(), "cx-archtest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Tmp);

        // The in-process dll the real code finds: <this exe>\runtimes\win-x64\native\avx2\llama.dll — a fake one.
        string fakeInProc = Path.Combine(appDir, "runtimes", "win-x64", "native", "avx2", "llama.dll");
        if (File.Exists(fakeInProc) && new FileInfo(fakeInProc).Length > 64 * 1024)
        {
            Console.WriteLine($"FATAL {fakeInProc} looks like a REAL llama.dll — this harness must not reference LLamaSharp.Backend.*");
            return 2;
        }
        WriteFakeDll(fakeInProc, InProcessArchs);
        LlamaBackendInfo.ClearCache();

        string emptyApp = Dir("empty-app");                       // an app folder with no llama.cpp at all
        string newServer = Dir("server-new");                     // custom backend: new llama-server
        WriteFakeDll(Path.Combine(newServer, "llama.dll"), NewServerArchs);
        File.WriteAllBytes(Path.Combine(newServer, "llama-server.exe"), Encoding.ASCII.GetBytes("MZ fake, never started"));
        string oldServer = Dir("server-old");                     // custom backend: llama-server as old as LLamaSharp
        WriteFakeDll(Path.Combine(oldServer, "llama.dll"), InProcessArchs);
        File.WriteAllBytes(Path.Combine(oldServer, "llama-server.exe"), Encoding.ASCII.GetBytes("MZ fake, never started"));
        string dllOnly = Dir("dll-no-exe");                       // a llama.dll with no llama-server.exe beside it
        WriteFakeDll(Path.Combine(dllOnly, "llama.dll"), NewServerArchs);

        string qwen35 = WriteGguf("Qwen3.5-4B-fake.gguf", "qwen35");
        string llama = WriteGguf("Llama-fake.gguf", "llama");

        Console.WriteLine("── GGUF header ──");
        Case("G1", () =>
        {
            string? a = GgufMetadataReader.TryReadArchitecture(qwen35);
            string? b = GgufMetadataReader.TryReadArchitecture(llama);
            string junk = Path.Combine(Tmp, "not.gguf");
            File.WriteAllBytes(junk, Encoding.ASCII.GetBytes("this is not a gguf file at all"));
            Check("G1", "general.architecture read past u32/string/array keys; a non-GGUF file reads as null",
                a == "qwen35" && b == "llama" && GgufMetadataReader.TryReadArchitecture(junk) == null, $"qwen35={a} llama={b}");
        });

        Console.WriteLine("── Finding the llama.dll the in-process path loads ──");
        Case("D1", () =>
        {
            string? found = LlamaBackendInfo.FindInProcessLlamaDll(appDir);
            string? deflt = LlamaBackendInfo.FindInProcessLlamaDll();
            Check("D1", "runtimes\\win-x64\\native\\<variant>\\llama.dll is found (explicit app dir and the default one)",
                Same(found, fakeInProc) && Same(deflt, fakeInProc), $"found={found} default={deflt}");
        });
        Case("D2", () =>
        {
            Check("D2", "no runtimes and no llama-backend → no dll (null), and FindLlamaDll falls back to the in-process one",
                LlamaBackendInfo.FindInProcessLlamaDll(emptyApp) == null
                && LlamaBackendInfo.FindServerLlamaDll(null, emptyApp) == null
                && Same(LlamaBackendInfo.FindLlamaDll(null), fakeInProc),
                $"FindLlamaDll(null)={LlamaBackendInfo.FindLlamaDll(null)}");
        });
        Case("D3", () =>
        {
            bool ok = LlamaBackendInfo.SupportsArchitectureInProcess("llama", appDir) == true
                      && LlamaBackendInfo.SupportsArchitectureInProcess("qwen3", appDir) == true
                      && LlamaBackendInfo.SupportsArchitectureInProcess("qwen35", appDir) == false
                      && LlamaBackendInfo.SupportsArchitectureInProcess("qwen2", appDir) == true      // tail of "rwkv6qwen2"
                      && LlamaBackendInfo.SupportsArchitectureInProcess("qwen35", emptyApp) == null;  // cannot tell
            Check("D3", "in-process scan: llama/qwen3/qwen2 yes, qwen35 no (not fooled by qwen3next), unknown without a dll", ok);
        });

        Console.WriteLine("── The real dll beats the hardcoded list ──");
        Case("L1", () =>
        {
            bool newArch = GgufMetadataReader.RequiresLlamaServerFallback("glm9next");   // on no list, not in the dll
            bool listedButKnown = GgufMetadataReader.RequiresLlamaServerFallback("qwen3"); // on the list, in the dll
            bool qwen35Needs = GgufMetadataReader.InspectModel(qwen35).NeedsFallback;
            bool llamaFine = !GgufMetadataReader.InspectModel(llama).NeedsFallback;
            Check("L1", "fallback decided by the dll: an unlisted new arch needs the server, a listed arch the dll knows does not",
                newArch && !listedButKnown && qwen35Needs && llamaFine,
                $"glm9next={newArch} qwen3={listedButKnown} qwen35={qwen35Needs} llama ok={llamaFine}");
        });
        Case("L2", () =>
        {
            bool ok = LocalBackendRouter.Decide("gemma4", null, false, null, false, false) == LocalBackendRoute.Unsupported
                      && LocalBackendRouter.Decide("llama", null, false, null, false, false) == LocalBackendRoute.InProcess
                      && GgufMetadataReader.IsOnFallbackList("qwen35");
            Check("L2", "no dll to scan → the list still decides (gemma4 refused, llama in-process), and the list knows qwen35", ok);
        });

        Console.WriteLine("── Model Manager badge (before download) ──");
        Case("B1", () =>
        {
            bool? noServer = LlamaBackendInfo.SupportsArchitecture("qwen35", null);
            bool? withNew = LlamaBackendInfo.SupportsArchitecture("qwen35", newServer);
            bool? withOld = LlamaBackendInfo.SupportsArchitecture("qwen35", oldServer);
            bool? dllNoExe = LlamaBackendInfo.SupportsArchitecture("qwen35", dllOnly);
            bool? known = LlamaBackendInfo.SupportsArchitecture("llama", null);
            bool? nothing = LlamaBackendInfo.SupportsArchitecture("qwen35", null, emptyApp);
            Check("B1", "ArchSupported: qwen35 false with only the bundled dll (the installed app's case — used to be null = no warning), " +
                        "true with a newer llama-server, false with an old one or a dll with no exe, llama true, null when nothing can be read",
                noServer == false && withNew == true && withOld == false && dllNoExe == false && known == true && nothing == null,
                $"noServer={noServer} new={withNew} old={withOld} dllNoExe={dllNoExe} llama={known} nothing={nothing}");
        });

        Console.WriteLine("── Backend decision ──");
        Case("R1", () =>
        {
            var r = new[]
            {
                ("supported, CPU, no server → in-process", LocalBackendRouter.Decide("llama", true, false, null, false, false), LocalBackendRoute.InProcess),
                ("supported, GPU, server → prefer server", LocalBackendRouter.Decide("llama", true, true, true, true, false), LocalBackendRoute.PreferServer),
                ("supported, CPU, projector + server → prefer server", LocalBackendRouter.Decide("llama", true, true, null, false, true), LocalBackendRoute.PreferServer),
                ("supported, GPU, server too old for it → in-process", LocalBackendRouter.Decide("llama", true, true, false, true, false), LocalBackendRoute.InProcess),
                ("unsupported + server that knows it → server", LocalBackendRouter.Decide("qwen35", false, true, true, false, false), LocalBackendRoute.Server),
                ("unsupported + PATH server (dll unknown) → server", LocalBackendRouter.Decide("qwen35", false, true, null, false, false), LocalBackendRoute.Server),
                ("unsupported + no server → refused", LocalBackendRouter.Decide("qwen35", false, false, null, true, false), LocalBackendRoute.Unsupported),
                ("unsupported + server as old → refused", LocalBackendRouter.Decide("qwen35", false, true, false, true, true), LocalBackendRoute.Unsupported),
                ("unreadable header → in-process (loader's own error)", LocalBackendRouter.Decide(null, null, false, null, false, false), LocalBackendRoute.InProcess),
            };
            var bad = r.Where(x => x.Item2 != x.Item3).Select(x => $"{x.Item1}: got {x.Item2}").ToList();
            Check("R1", $"{r.Length} routing rules (in-process / prefer server / must server / refuse)", bad.Count == 0, string.Join("\n", bad));
        });

        Console.WriteLine("── The message ──");
        Case("M1", () =>
        {
            var en = LocalBackendRouter.Refusal("qwen35", @"D:\m\Qwen3.5-4B-Q6_K.gguf", null, "en");
            var th = LocalBackendRouter.Refusal("qwen35", @"D:\m\Qwen3.5-4B-Q6_K.gguf", null, "th");
            var sv = LocalBackendRouter.Refusal("qwen35", @"D:\m\Qwen3.5-4B-Q6_K.gguf", @"C:\llama\llama-server.exe", "en");
            bool Ways(string m) => m.Contains("Ollama") && m.Contains("update-llama-backend.ps1") && m.Contains("llama-server")
                && (m.Contains("Get the latest llama.cpp") || m.Contains("ดาวน์โหลด llama.cpp ล่าสุด"));   // the one-click way first
            bool enOk = en.Message.Contains("'qwen35'") && en.Message.Contains("Qwen3.5-4B-Q6_K.gguf") && Ways(en.Message)
                        && en.Message.Contains("newer llama.cpp") && en.Message.Contains("Custom llama.cpp backend folder")
                        && !en.Message.Contains("corrupt") && !en.Message.Contains("{");
            bool thOk = th.Message.Contains("'qwen35'") && Ways(th.Message) && th.Message.Contains("สถาปัตยกรรม")
                        && th.Message.Contains("โฟลเดอร์ llama.cpp ที่สร้างเอง") && !th.Message.Contains("{")
                        && th.ShortMessage.Contains("ใหม่กว่า");
            bool svOk = sv.Message.Contains(@"C:\llama\llama-server.exe") && Ways(sv.Message);
            bool typed = en is InvalidOperationException && en.Architecture == "qwen35" && en.ShortMessage.Length < 70;
            Check("M1", "refusal names the arch, the file, newer llama.cpp and the three ways out — English and Thai, server variant names the exe",
                enOk && thOk && svOk && typed, $"EN: {en.Message}\nTH: {th.Message}\nSV: {sv.Message}\nshort: {en.ShortMessage}");
        });

        Console.WriteLine("── The real LocalGgufProvider (fails before any load) ──");
        var settings = new SettingsService();
        var inference = new LlamaInferenceService(settings);
        var server = new LlamaServerProvider(settings);
        var provider = new LocalGgufProvider(inference, server, settings);
        settings.Settings.GpuBackend = "CPU";
        settings.Settings.GpuLayerCount = 0;

        Case("P1", () =>
        {
            // No llama-server anywhere: an empty custom folder, no llama-backend next to this exe, and a PATH
            // without one (FindServerExe asks `where`).
            settings.Settings.CustomLlamaCppBackendPath = Dir("no-server");
            string oldPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", Environment.GetFolderPath(Environment.SpecialFolder.System));
            try
            {
                if (server.ServerExePath is { } p) { SkipCase("P1", $"a llama-server is still found ({p})"); return; }
                foreach (var lang in new[] { "en", "th" })
                {
                    settings.Settings.Language = lang;
                    var errors = new List<string>();
                    Action<string> onError = errors.Add;
                    provider.OnError += onError;
                    var sw = Stopwatch.StartNew();
                    Exception? caught = null;
                    try { provider.LoadModelAsync(qwen35).GetAwaiter().GetResult(); }
                    catch (Exception ex) { caught = ex; }
                    finally { provider.OnError -= onError; }
                    sw.Stop();
                    var refusal = caught as ModelArchitectureNotSupportedException;
                    bool ok = refusal != null && refusal.Architecture == "qwen35"
                              && refusal.Message.Contains("Ollama") && refusal.Message.Contains("update-llama-backend.ps1")
                              && (lang == "en" ? refusal.Message.Contains("newer llama.cpp") : refusal.Message.Contains("ต้องใช้ llama.cpp"))
                              && errors.Count == 1 && errors[0] == refusal.Message
                              && !inference.IsModelLoaded && !inference.IsLoading && !provider.IsReady && !provider.IsLoading
                              && sw.ElapsedMilliseconds < 10_000;
                    Check("P1" + lang, $"[{lang}] qwen35 + no llama-server → ModelArchitectureNotSupportedException at once, nothing loaded, OnError carries it",
                        ok, $"caught={caught?.GetType().Name}: {caught?.Message}\nerrors={errors.Count} {sw.ElapsedMilliseconds} ms");
                }
            }
            finally { Environment.SetEnvironmentVariable("PATH", oldPath); settings.Settings.Language = "en"; }
        });
        Case("P2", () =>
        {
            // A llama-server exists but its llama.dll is as old as LLamaSharp's: still refused, and the exe (a fake)
            // is never started — starting it would throw a Win32Exception, not this.
            settings.Settings.CustomLlamaCppBackendPath = oldServer;
            Exception? caught = null;
            try { provider.LoadModelAsync(qwen35).GetAwaiter().GetResult(); }
            catch (Exception ex) { caught = ex; }
            var refusal = caught as ModelArchitectureNotSupportedException;
            Check("P2", "qwen35 + a llama-server whose llama.dll lacks it → refused naming that server, nothing started",
                refusal != null && refusal.Message.Contains(Path.Combine(oldServer, "llama-server.exe")) && !server.IsServerRunning,
                $"caught={caught?.GetType().Name}: {caught?.Message}");
        });

        Console.WriteLine("── Real files on this machine (read-only) ──");
        Case("X1", () =>
        {
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CluadeX", "current");
            string? dll = LlamaBackendInfo.FindInProcessLlamaDll(installed);
            if (dll == null) { SkipCase("X1", "no installed CluadeX with LLamaSharp runtimes"); return; }
            bool? q35 = LlamaBackendInfo.SupportsArchitectureInProcess("qwen35", installed);
            bool? q3n = LlamaBackendInfo.SupportsArchitectureInProcess("qwen3next", installed);
            bool? lla = LlamaBackendInfo.SupportsArchitectureInProcess("llama", installed);
            bool? badge = LlamaBackendInfo.SupportsArchitecture("qwen35", null, installed);
            // Only claims what this dll says: qwen35 missing is the LLamaSharp 0.26 fact; a newer LLamaSharp may know it.
            Check("X1", $"installed app's in-process dll ({Path.GetFileName(Path.GetDirectoryName(dll))}) is read: llama + qwen3next known; qwen35={q35}, badge={badge}",
                lla == true && q3n == true && q35 != null
                && (LlamaBackendInfo.FindServerLlamaDll(null, installed) != null || badge == q35),   // no llama-backend: the badge is the in-process answer
                $"dll={dll} qwen35={q35} qwen3next={q3n} llama={lla} badge={badge}");
        });
        Case("X2", () =>
        {
            const string real = @"D:\DataAiModel\Qwen3.5-4B-Q6_K.gguf";
            if (!File.Exists(real)) { SkipCase("X2", $"{real} not on this machine"); return; }
            string? a = GgufMetadataReader.TryReadArchitecture(real);
            Check("X2", "the owner's real Qwen3.5-4B-Q6_K.gguf header reads 'qwen35'", a == "qwen35", $"arch={a}");
        });

        InstallerTests.Run((id, name, ok, detail) => Check(id, name, ok, detail), SkipCase, Case, Dir, WriteFakeDll, NewServerArchs);

        Console.WriteLine();
        Console.WriteLine($"archtest: {Pass} passed, {Fail} failed, {Skip} skipped");
        foreach (var p in Problems) Console.WriteLine("  " + p);
        return Fail == 0 ? 0 : 1;
    }

    static string Dir(string name) { string d = Path.Combine(Tmp, name); Directory.CreateDirectory(d); return d; }

    static bool Same(string? a, string? b)
        => a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>A byte file shaped like the bits of llama.dll the scanner reads: NUL-terminated names among junk.</summary>
    static void WriteFakeDll(string path, IEnumerable<string> archs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("MZ\x90\0fake llama.dll for archtest\0"));
        ms.Write(new byte[] { 0x48, 0x8B, 0x05, 0xFF, 0x00 });
        foreach (var a in archs) { ms.Write(Encoding.ASCII.GetBytes(a)); ms.WriteByte(0); }
        ms.Write(Encoding.ASCII.GetBytes("unknown model architecture: '%s'\0"));
        File.WriteAllBytes(path, ms.ToArray());
    }

    /// <summary>A GGUF v3 header with no tensors: a u32, a string and a string array before general.architecture.</summary>
    static string WriteGguf(string name, string arch)
    {
        string path = Path.Combine(Tmp, name);
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(0x46554747u);   // "GGUF"
        bw.Write(3u);            // version
        bw.Write(0UL);           // tensor_count
        bw.Write(4UL);           // metadata_kv_count
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); bw.Write((ulong)b.Length); bw.Write(b); }
        Str("general.quantization_version"); bw.Write(4u); bw.Write(2u);                  // UInt32
        Str("general.name"); bw.Write(8u); Str(Path.GetFileNameWithoutExtension(name));   // String
        Str("general.tags"); bw.Write(9u); bw.Write(8u); bw.Write(2UL); Str("a"); Str("b"); // Array<String>
        Str("general.architecture"); bw.Write(8u); Str(arch);
        return path;
    }
}
