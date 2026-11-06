using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CluadeX.Services;

namespace ArchTest;

/// <summary>
/// LlamaCppInstaller against a localhost copy of GitHub's releases API and zip downloads — never the real GitHub.
/// The zips hold fake llama-server.exe / llama.dll byte files and the tool runner is faked, so nothing is started.
/// </summary>
internal static class InstallerTests
{
    public static void Run(Action<string, string, bool, string> check, Action<string, string> skip, Action<string, Action> run,
        Func<string, string> dir, Action<string, IEnumerable<string>> writeFakeDll, string[] newServerArchs)
    {
        Console.WriteLine("── llama.cpp download (LlamaCppInstaller, localhost GitHub) ──");

        run("I1", () =>
        {
            var p = LlamaCppInstaller.PickVariant;
            check("I1", "variant: GTX 10xx (6.1) → CUDA 12, Turing+ (7.5/8.6) → CUDA 13, other GPU → Vulkan, none → CPU",
                p(6.1, false) == LlamaCppVariant.Cuda12 && p(7.5, false) == LlamaCppVariant.Cuda13 && p(8.6, true) == LlamaCppVariant.Cuda13
                && p(null, true) == LlamaCppVariant.Vulkan && p(null, false) == LlamaCppVariant.Cpu && p(0, false) == LlamaCppVariant.Cpu, "");
        });

        run("I2", () =>
        {
            string json = ReleasesJson("https://example.invalid", withNewest: true);
            var c12 = LlamaCppInstaller.ChooseRelease(json, LlamaCppVariant.Cuda12);
            var c13 = LlamaCppInstaller.ChooseRelease(json, LlamaCppVariant.Cuda13);
            var vk = LlamaCppInstaller.ChooseRelease(json, LlamaCppVariant.Vulkan);
            var cpu = LlamaCppInstaller.ChooseRelease(json, LlamaCppVariant.Cpu);
            check("I2", "release: the source-only 'latest' (v0.9.0) and the draft are skipped; CUDA 13 skips b12145 (no cudart) for b12144; CUDA builds get their runtime zip",
                c12 is { Tag: "b12145", Runtime.Name: "cudart-llama-bin-win-cuda-12.4-x64.zip" } && c12.Main.Name == "llama-b12145-bin-win-cuda-12.4-x64.zip"
                && c13 is { Tag: "b12144", Runtime.Name: "cudart-llama-bin-win-cuda-13.4-x64.zip" }
                && vk is { Tag: "b12145", Runtime: null } && vk.Main.Sha256 != null
                && cpu is { Tag: "b12145" },
                $"c12={c12?.Tag}/{c12?.Runtime?.Name} c13={c13?.Tag}/{c13?.Runtime?.Name} vk={vk?.Tag} cpu={cpu?.Tag}");
            check("I2b", "a plain-http file URL is refused unless the list itself came from loopback",
                LlamaCppInstaller.ChooseRelease(ReleasesJson("http://127.0.0.1:9", true), LlamaCppVariant.Vulkan) == null
                && LlamaCppInstaller.ChooseRelease(ReleasesJson("http://127.0.0.1:9", true), LlamaCppVariant.Vulkan, allowLoopbackHttp: true) != null, "");
        });

        using var gh = new FakeGitHub();
        string goodZip = gh.AddZip("llama-b12145-bin-win-vulkan-x64.zip", z =>
        {
            Add(z, "llama-server.exe", Encoding.ASCII.GetBytes("MZ fake server"));
            Add(z, "llama.dll", FakeDll(writeFakeDll, newServerArchs, dir));
            Add(z, "ggml-vulkan.dll", Encoding.ASCII.GetBytes("MZ fake ggml"));
        });
        gh.AddZip("llama-b12145-bin-win-cuda-12.4-x64.zip", z =>
        {
            Add(z, "llama-server.exe", Encoding.ASCII.GetBytes("MZ fake cuda server"));
            Add(z, "llama.dll", FakeDll(writeFakeDll, newServerArchs, dir));
        });
        gh.AddZip("cudart-llama-bin-win-cuda-12.4-x64.zip", z => Add(z, "cudart64_12.dll", Encoding.ASCII.GetBytes("MZ cudart")));
        gh.AddZip("llama-b12145-bin-win-cpu-x64.zip", z =>
        {
            Add(z, "../evil.txt", Encoding.ASCII.GetBytes("outside"));     // zip-slip
            Add(z, "llama-server.exe", Encoding.ASCII.GetBytes("MZ"));
            Add(z, "llama.dll", Encoding.ASCII.GetBytes("MZ"));
        });
        gh.Releases = ReleasesJson(gh.Base, withNewest: true, digests: gh.Digests, sizes: gh.Sizes);

        string root = dir("installer");
        string target = Path.Combine(root, "llama-backend");
        var okTool = (string exe, string args, TimeSpan t, CancellationToken ct) =>
            Task.FromResult(args == "--version" ? (0, "ggml_vulkan: Found 1 Vulkan devices\nversion: 12145 (abcdef0)\nbuilt with MSVC")
                                                : (0, "Available devices:\n  Vulkan0: GTX 1070 Ti"));
        LlamaCppInstaller Make(Func<string, string, TimeSpan, CancellationToken, Task<(int, string)>>? tool = null, (double?, bool)? gpu = null)
            => new(null, null, gh.Base + "/releases", target)
            {
                RunTool = tool ?? okTool,
                DetectGpu = () => gpu ?? (null, true),
            };

        run("I3", () =>
        {
            var stages = new List<string>();
            var info = Make().InstallAsync(LlamaCppVariant.Vulkan, new SyncProgress(p => stages.Add(p.Stage))).GetAwaiter().GetResult();
            var read = LlamaCppInstaller.ReadInstalled(target);
            string emptyApp = dir("installer-noapp");
            string? found = LlamaBackendInfo.FindServerDir(null, emptyApp, managedDir: target, pathEnv: "");
            bool? knows = LlamaBackendInfo.SupportsArchitectureIn(Path.Combine(target, "llama.dll"), "qwen35");
            bool leftovers = Directory.GetDirectories(root).Any(d => Path.GetFileName(d).Contains(".new-") || Path.GetFileName(d).Contains(".old-"));
            check("I3", "install: files land in the backend folder with the note, the finder picks that folder, its llama.dll knows qwen35, no staging left, stages in order",
                info is { Tag: "b12145", Variant: LlamaCppVariant.Vulkan, Version: "version: 12145 (abcdef0)" }
                && read?.Tag == "b12145" && File.Exists(Path.Combine(target, "ggml-vulkan.dll"))
                && Same(found, target) && knows == true && !leftovers
                && string.Join(",", stages.Distinct()) == "find,download,extract,check,swap",
                $"info={info} read={read} found={found} knows={knows} leftovers={leftovers} stages={string.Join(",", stages.Distinct())}");
        });

        File.WriteAllText(Path.Combine(target, "marker.txt"), "the working backend");
        bool Intact() => File.Exists(Path.Combine(target, "marker.txt")) && LlamaCppInstaller.ReadInstalled(target)?.Tag == "b12145";

        run("I4", () =>
        {
            string saved = gh.Releases;
            gh.Releases = saved.Replace(gh.Digests["llama-b12145-bin-win-vulkan-x64.zip"], new string('0', 64));
            string? err = Error(() => Make().InstallAsync(LlamaCppVariant.Vulkan));
            gh.Releases = saved;
            check("I4", "a file whose sha256 is not the published one: refused (llamacpp.err.digest), the working backend untouched",
                err == "llamacpp.err.digest" && Intact() && NoLeftovers(root), $"err={err} intact={Intact()}");
        });

        run("I5", () =>
        {
            string? err = Error(() => Make().InstallAsync(LlamaCppVariant.Cpu));
            check("I5", "a zip entry that climbs out of the folder (../evil.txt): refused, nothing written outside, backend untouched",
                err == "llamacpp.err.zip" && !File.Exists(Path.Combine(root, "evil.txt")) && Intact() && NoLeftovers(root), $"err={err}");
        });

        run("I6", () =>
        {
            string? err = Error(() => Make(tool: (e, a, t, c) => Task.FromResult((1, "error: missing VCRUNTIME140.dll"))).InstallAsync(LlamaCppVariant.Vulkan));
            check("I6", "a new llama-server that does not start (--version fails): refused (llamacpp.err.check), backend untouched",
                err == "llamacpp.err.check" && Intact() && NoLeftovers(root), $"err={err}");
        });

        run("I7", () =>
        {
            gh.Requests.Clear();
            var noCuda = (string exe, string args, TimeSpan t, CancellationToken ct) =>
                Task.FromResult(args == "--version" ? (0, "version: 12145 (abcdef0)") : (0, "Available devices:\n  Vulkan0: x"));
            var info = Make(tool: noCuda, gpu: (6.1, false)).InstallAsync().GetAwaiter().GetResult();
            bool gotCuda = gh.Requests.Any(r => r.Contains("cuda-12.4")) && gh.Requests.Any(r => r.Contains("cudart-llama-bin-win-cuda-12.4"));
            check("I7", "automatic on a GTX 10xx: the CUDA 12 build + its runtime are tried; it lists no CUDA device → the Vulkan build is installed instead",
                info.Variant == LlamaCppVariant.Vulkan && gotCuda && !File.Exists(Path.Combine(target, "marker.txt")) && NoLeftovers(root),
                $"variant={info.Variant} requests={string.Join(" ", gh.Requests)}");
        });

        run("I8", () =>
        {
            gh.Delay = TimeSpan.FromSeconds(2);
            var first = Make().InstallAsync(LlamaCppVariant.Vulkan);
            Thread.Sleep(300);
            string? err = Error(() => Make().InstallAsync(LlamaCppVariant.Vulkan));
            bool busyDuring = LlamaCppInstaller.IsBusy;
            gh.Delay = TimeSpan.Zero;
            first.GetAwaiter().GetResult();
            check("I8", "a second install while one runs (two buttons, a double click): refused at once (llamacpp.err.busy), the first finishes",
                err == "llamacpp.err.busy" && busyDuring && !LlamaCppInstaller.IsBusy && LlamaCppInstaller.ReadInstalled(target) != null, $"err={err}");
        });

        run("I9", () =>
        {
            string custom = dir("find-custom"), managed = dir("find-managed"), app = dir("find-app"), onPath = dir("find-path");
            Directory.CreateDirectory(Path.Combine(app, "llama-backend"));
            foreach (var d in new[] { custom, managed, Path.Combine(app, "llama-backend"), onPath })
                File.WriteAllText(Path.Combine(d, "llama-server.exe"), "MZ");
            string F(string? c, string? m, string a, string p) => LlamaBackendInfo.FindServerDir(c, a, m, p) ?? "(none)";
            string empty = dir("find-empty");
            bool order = Same(F(custom, managed, app, onPath), custom) && Same(F(null, managed, app, onPath), managed)
                         && Same(F(null, empty, app, onPath), Path.Combine(app, "llama-backend"))
                         && Same(F(null, empty, empty, "C:\\nope;\"" + onPath + "\""), onPath) && F(null, empty, empty, "") == "(none)";
            check("I9", "search order: custom folder, CluadeX's download, llama-backend next to the app, then PATH (quoted entries too)", order,
                $"{F(custom, managed, app, onPath)} | {F(null, managed, app, onPath)} | {F(null, empty, app, onPath)} | {F(null, empty, empty, onPath)}");

            // A winget install puts a link on PATH: the real folder (where llama.dll is) must be found, not the link's.
            string links = dir("find-links");
            string link = Path.Combine(links, "llama-server.exe");
            try { File.CreateSymbolicLink(link, Path.Combine(onPath, "llama-server.exe")); }
            catch (Exception ex) { skip("I9b", $"cannot create a symbolic link here ({ex.GetType().Name}) — needs Developer Mode"); return; }
            check("I9b", "a llama-server.exe link on PATH (WinGet\\Links) resolves to the real folder", Same(F(null, empty, empty, links), onPath),
                F(null, empty, empty, links));
        });

        run("I10", () =>
        {
            string saved = gh.Releases;
            var doc = gh.Sizes["llama-b12145-bin-win-vulkan-x64.zip"];
            gh.Releases = saved.Replace($"\"size\":{doc}", "\"size\":100");
            string? err = Error(() => Make().InstallAsync(LlamaCppVariant.Vulkan));
            gh.Releases = saved;
            check("I10", "a download bigger than GitHub said it is: stopped (llamacpp.err.tooBig)", err == "llamacpp.err.tooBig" && NoLeftovers(root), $"err={err}");
        });

        LlamaBackendInfo.ClearCache();
    }

    /// <summary>
    /// dotnet run -- --real-install &lt;folder&gt;: the real installer against the real GitHub into that folder, then the
    /// real llama.dll asked for qwen35 and the real llama-server asked --version / --list-devices. Downloads hundreds of
    /// MB; loads no model.
    /// </summary>
    public static int RealInstall(string target)
    {
        Console.WriteLine($"── REAL llama.cpp install into {target} ──");
        var inst = new LlamaCppInstaller(null, null, null, target);
        var variant = inst.AutoVariant();
        var rel = inst.FindReleaseAsync(variant).GetAwaiter().GetResult();
        Console.WriteLine($"  variant {variant} · release {rel.Tag} · {rel.Main.Name} + {rel.Runtime?.Name ?? "-"} · {rel.TotalBytes / 1048576} MB");
        long last = -1;
        var info = inst.InstallAsync(null, new SyncProgress(p =>
        {
            long mb = p.Done / (50L << 20);
            if (p.Stage != "download") Console.WriteLine($"  {p.Stage}");
            else if (mb != last) { last = mb; Console.WriteLine($"  download {p.Done / 1048576}/{p.Total / 1048576} MB"); }
        })).GetAwaiter().GetResult();
        Console.WriteLine($"  installed {info.Tag} {info.Variant} · {info.Version}");
        string dll = Path.Combine(target, "llama.dll");
        LlamaBackendInfo.ClearCache();
        bool? q35 = LlamaBackendInfo.SupportsArchitectureIn(dll, "qwen35");
        var (_, devices) = LlamaCppInstaller.RunProcessAsync(Path.Combine(target, "llama-server.exe"), "--list-devices", TimeSpan.FromSeconds(30), default).GetAwaiter().GetResult();
        Console.WriteLine($"  qwen35 known: {q35}");
        Console.WriteLine("  devices: " + string.Join(" | ", devices.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)));
        return q35 == true ? 0 : 1;
    }

    // ── helpers ──

    sealed class SyncProgress(Action<LlamaCppProgress> on) : IProgress<LlamaCppProgress>
    {
        public void Report(LlamaCppProgress value) { lock (this) on(value); }
    }

    static bool Same(string? a, string? b)
        => a != null && b != null && string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    static bool NoLeftovers(string root)
        => !Directory.GetDirectories(root).Any(d => Path.GetFileName(d).Contains(".new-") || Path.GetFileName(d).Contains(".old-"));

    static string? Error(Func<Task> act)
    {
        try { act().GetAwaiter().GetResult(); return null; }
        catch (InvalidOperationException ex) { return ex.Message; }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }

    static void Add(ZipArchive z, string name, byte[] bytes)
    {
        using var s = z.CreateEntry(name).Open();
        s.Write(bytes);
    }

    static byte[] FakeDll(Action<string, IEnumerable<string>> write, string[] archs, Func<string, string> dir)
    {
        string p = Path.Combine(dir("fakedll-" + Guid.NewGuid().ToString("N")[..6]), "llama.dll");
        write(p, archs);
        return File.ReadAllBytes(p);
    }

    /// <summary>GitHub's releases list as of 2026-11-06: a source-only "latest", a draft, b12145 (no CUDA 13 runtime), b12144.</summary>
    static string ReleasesJson(string baseUrl, bool withNewest, IDictionary<string, string>? digests = null, IDictionary<string, long>? sizes = null)
    {
        object A(string name) => new Dictionary<string, object?>
        {
            ["name"] = name,
            ["browser_download_url"] = $"{baseUrl}/dl/{name}",
            ["size"] = sizes != null && sizes.TryGetValue(name, out long z) ? z : 1000,
            ["digest"] = "sha256:" + (digests != null && digests.TryGetValue(name, out var d) ? d : new string('a', 64)),
        };
        var list = new List<object>
        {
            new Dictionary<string, object?> { ["tag_name"] = "v0.9.0", ["draft"] = false, ["prerelease"] = false, ["assets"] = new[] { A("nightly-tag.txt") } },
            new Dictionary<string, object?> { ["tag_name"] = "b12146", ["draft"] = true, ["assets"] = new[] { A("llama-b12146-bin-win-vulkan-x64.zip") } },
        };
        if (withNewest)
            list.Add(new Dictionary<string, object?>
            {
                ["tag_name"] = "b12145", ["draft"] = false, ["prerelease"] = true,
                ["assets"] = new[]
                {
                    A("cudart-llama-bin-win-cuda-12.4-x64.zip"), A("llama-b12145-bin-win-cpu-arm64.zip"), A("llama-b12145-bin-win-cpu-x64.zip"),
                    A("llama-b12145-bin-win-cuda-12.4-x64.zip"), A("llama-b12145-bin-win-cuda-13.4-x64.zip"), A("llama-b12145-bin-win-vulkan-x64.zip"),
                },
            });
        list.Add(new Dictionary<string, object?>
        {
            ["tag_name"] = "b12144", ["draft"] = false, ["prerelease"] = true,
            ["assets"] = new[]
            {
                A("cudart-llama-bin-win-cuda-12.4-x64.zip"), A("cudart-llama-bin-win-cuda-13.4-x64.zip"), A("llama-b12144-bin-win-cuda-12.4-x64.zip"),
                A("llama-b12144-bin-win-cuda-13.4-x64.zip"), A("llama-b12144-bin-win-vulkan-x64.zip"), A("llama-b12144-bin-win-cpu-x64.zip"),
            },
        });
        return JsonSerializer.Serialize(list);
    }

    /// <summary>GitHub on 127.0.0.1: /releases and /dl/&lt;name&gt;.</summary>
    sealed class FakeGitHub : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, byte[]> _files = new();
        public readonly Dictionary<string, string> Digests = new();
        public readonly Dictionary<string, long> Sizes = new();
        public readonly List<string> Requests = new();
        public string Releases = "[]";
        public TimeSpan Delay = TimeSpan.Zero;
        public string Base { get; }

        public FakeGitHub()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            Base = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Base + "/");
            _listener.Start();
            _ = Task.Run(Loop);
        }

        public string AddZip(string name, Action<ZipArchive> fill)
        {
            using var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) fill(z);
            byte[] bytes = ms.ToArray();
            _files[name] = bytes;
            Digests[name] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Sizes[name] = bytes.Length;
            return name;
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    string path = ctx.Request.Url!.AbsolutePath;
                    lock (Requests) Requests.Add(path);
                    try
                    {
                        if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
                        byte[] body;
                        if (path == "/releases") { body = Encoding.UTF8.GetBytes(Releases); ctx.Response.ContentType = "application/json"; }
                        else if (path.StartsWith("/dl/") && _files.TryGetValue(path[4..], out var f)) body = f;
                        else { ctx.Response.StatusCode = 404; body = Array.Empty<byte>(); }
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body);
                    }
                    catch { }
                    finally { try { ctx.Response.Close(); } catch { } }
                });
            }
        }

        public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
    }
}
