using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Background;
using CluadeX.Services.Tools;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BgTest;

/// <summary>
/// T2 contract: background processes and dev servers (bg_start / bg_serve / bg_output / bg_stop / bg_list, the
/// run_command interceptor, job objects, the static file server, launch.json, the Task Manager facade), driven
/// through the REAL built CluadeX.dll with services from App.ConfigureServices — no mocks of the code under test.
///
/// Nothing needs installing: this exe is its own child process (--serve, --spawn-orphan, --host, --sleep,
/// --chatter, --say, --lines, --pidfile). Servers bind loopback only (no firewall prompt). The data root is isolated
/// next to the exe (portable.txt) and the run aborts if it is not. No model, no network beyond 127.0.0.1 / ::1, no
/// paid API. python / npm / git cases run only when those are installed (else GAP); the git case pushes from a
/// throwaway repo that has no remote at all.
///
/// The R* cases are the T2 review's findings, each with a negative control that shows the assertion can tell the
/// fixed behaviour from the old one.
/// </summary>
internal static class Program
{
    static int Pass, Fail, Gap;
    static readonly List<string> Problems = new();
    static readonly List<string> Gaps = new();
    static readonly string Exe = Environment.ProcessPath!;
    /// <summary>This exe, quoted, with forward slashes — runs the same in Git Bash and cmd.</summary>
    static readonly string Self = "\"" + Exe.Replace('\\', '/') + "\"";

    [STAThread]
    static int Main(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal) && args[0] != "--run")
            return ChildModes.Run(args);
        try { return Run().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine("FATAL " + ex); return 2; }
    }

    static void Check(string id, string name, bool ok, string detail = "")
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS {id,-5} {name}"); return; }
        Fail++; Problems.Add($"FAIL {id} {name}");
        Console.WriteLine($"  FAIL {id,-5} {name}");
        if (detail.Length > 0) Console.WriteLine("            " + Trunc(detail, 900).Replace("\n", "\n            "));
    }

    static void GapNote(string id, string text)
    {
        Gap++; Gaps.Add($"GAP {id} {text}");
        Console.WriteLine($"  GAP  {id,-5} {text}");
    }

    static async Task Case(string id, Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex)
        {
            Fail++; Problems.Add($"FAIL {id} threw {ex.GetType().Name}");
            Console.WriteLine($"  FAIL {id,-5} harness case threw: {ex.GetType().Name}: {Trunc(ex.Message, 400)}");
        }
    }

    static string Trunc(string? s, int n) => s == null ? "" : (s.Length <= n ? s : s[..n] + $"…(+{s.Length - n})");
    static string Seen(ToolResult r) => string.Join("\n", new[] { r.Output, r.Error }.Where(s => !string.IsNullOrEmpty(s)));

    // ── wiring ──
    static AgentToolService Svc = null!;
    static BackgroundProcessService Bg = null!;
    static ServiceProvider Provider = null!;
    static readonly List<string> Prompts = new();
    static bool Answer = true;
    static readonly HashSet<int> UsedPorts = new();

    static int FreePort()
    {
        for (int p = 18100; p < 18999; p++)
        {
            if (UsedPorts.Contains(p) || ReadinessProbe.IsListening(p)) continue;
            UsedPorts.Add(p);
            return p;
        }
        throw new Exception("no free test port");
    }

    static async Task<ToolResult> T(string tool, params (string k, string v)[] args)
    {
        var type = Svc.ResolveToolTypePublic(tool) ?? throw new Exception("tool name did not resolve: " + tool);
        var call = new ToolCall { Type = type, ToolName = tool, Arguments = args.ToDictionary(a => a.k, a => a.v, StringComparer.OrdinalIgnoreCase) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        return await Svc.ExecuteToolAsync(call, cts.Token);
    }

    static int IdOf(ToolResult r)
    {
        var m = System.Text.RegularExpressions.Regex.Match(Seen(r), @"bg#(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : -1;
    }

    static void SetActivated(bool on)
    {
        var a = Provider.GetRequiredService<ActivationService>();
        var t = typeof(ActivationService);
        t.GetField("_hasKey", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(a, on);
        t.GetField("_tier", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(a, on ? "pro" : "free");
        t.GetField("_expiresUtc", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(a, null);
        t.GetField("_graceExpired", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(a, false);
        if (a.IsActivated != on) throw new Exception("activation toggle did not take");
    }

    static void SetHooks(HookService hooks, List<HookDefinition> list) =>
        typeof(HookService).GetField("_cachedHooks", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(hooks, list);

    static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    static async Task<bool> WaitUntil(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    static async Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> RawHttp(int port, string method, string target,
        string? host = null, IPAddress? address = null)
    {
        using var client = new TcpClient(address?.AddressFamily ?? AddressFamily.InterNetwork);
        await client.ConnectAsync(address ?? IPAddress.Loopback, port);
        var stream = client.GetStream();
        string head = $"{method} {target} HTTP/1.1\r\nHost: {host ?? $"127.0.0.1:{port}"}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head));
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var buf = new byte[65536];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buf, cts.Token)) > 0) ms.Write(buf, 0, n);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        var all = ms.ToArray();
        int split = IndexOf(all, "\r\n\r\n"u8.ToArray());
        if (split < 0) return (0, new(), all);
        var lines = Encoding.ASCII.GetString(all, 0, split).Split("\r\n");
        int status = int.TryParse(lines[0].Split(' ').ElementAtOrDefault(1), out var s) ? s : 0;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1)) { int c = l.IndexOf(':'); if (c > 0) headers[l[..c].Trim()] = l[(c + 1)..].Trim(); }
        return (status, headers, all[(split + 4)..]);
    }

    static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    static bool CommandWorks(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/d /c {file} {args}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>The repo (worktree) this harness was built from — where CluadeX/Resources/HooksBundled lives.</summary>
    static string? RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "CluadeX", "Resources", "HooksBundled"))) d = d.Parent;
        return d?.FullName;
    }

    static (int Code, string Output) RunProcess(string file, string args, string cwd)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch { } return (-1, output); }
        return (p.ExitCode, output + err.Result);
    }

    /// <summary>The raw bytes cmd.exe writes for <paramref name="cmdLine"/> in a fresh hidden console (CreateNoWindow).</summary>
    static byte[] RawCmdBytes(string cmdLine)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{cmdLine}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit(10000);
        return ms.ToArray();
    }

    static bool IsValidUtf8(byte[] bytes)
    {
        try { new UTF8Encoding(false, true).GetString(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    /// <summary>Send raw request bytes and read until the server closes (or <paramref name="timeoutMs"/>).
    /// <paramref name="onBytes"/> sees the running total after every read (to change the file mid-transfer).</summary>
    static async Task<(byte[] All, long ClosedAtMs)> RawExchange(int port, byte[] request, Func<long, Task>? onBytes = null,
        int readDelayMs = 0, int timeoutMs = 20000)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork) { ReceiveBufferSize = 8192 };
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(request);
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(timeoutMs);
        var sw = Stopwatch.StartNew();
        var buf = new byte[16384];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buf, cts.Token)) > 0)
            {
                ms.Write(buf, 0, n);
                if (onBytes != null) await onBytes(ms.Length);
                if (readDelayMs > 0) await Task.Delay(readDelayMs);
            }
        }
        catch (OperationCanceledException) { return (ms.ToArray(), -1); }   // never closed
        catch (IOException) { }
        return (ms.ToArray(), sw.ElapsedMilliseconds);
    }

    static (int Status, string Head, byte[] Body) SplitResponse(byte[] all)
    {
        int split = IndexOf(all, "\r\n\r\n"u8.ToArray());
        if (split < 0) return (0, Encoding.Latin1.GetString(all), Array.Empty<byte>());
        string head = Encoding.Latin1.GetString(all, 0, split);
        int status = int.TryParse(head.Split(' ').ElementAtOrDefault(1), out var s) ? s : 0;
        return (status, head, all[(split + 4)..]);
    }

    /// <summary>The StaticFileServer behind a bg_serve entry (the registry keeps it in an internal field).</summary>
    static StaticFileServer? ServerOf(BackgroundProcessService bg, int id)
    {
        var entries = (System.Collections.IEnumerable)typeof(BackgroundProcessService)
            .GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bg)!;
        foreach (var e in entries)
        {
            var t = e.GetType();
            if ((int)t.GetProperty("Id")!.GetValue(e)! == id) return t.GetField("Server")!.GetValue(e) as StaticFileServer;
        }
        return null;
    }

    static void ForceDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        try { Directory.Delete(dir, true); } catch { }
    }

    /// <summary>Pid of the program the powershell tool opened with Start-Process (R2) — checked again after app exit (B9).</summary>
    static int StartProcessPid = -1;

    // ══════════════════════════════════════════════════════════════════════

    static async Task<int> Run()
    {
        var configure = typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!;
        var services = new ServiceCollection();
        configure.Invoke(null, new object[] { services });
        Provider = services.BuildServiceProvider();

        var settings = Provider.GetRequiredService<SettingsService>();
        string exeDir = AppContext.BaseDirectory.TrimEnd('\\');
        Console.WriteLine($"data root : {settings.DataRoot}");
        if (!settings.DataRoot.StartsWith(exeDir, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("ABORT: data root is not isolated (portable.txt missing) — refusing to touch real user data.");
            return 3;
        }

        // ── the project ──
        string project = Path.Combine(exeDir, "bg-project");
        string outside = Path.Combine(exeDir, "bg-outside");
        foreach (var d in new[] { project, outside })
        {
            if (Directory.Exists(d))
            {
                // A junction from an earlier run: remove the link itself, never what it points to.
                foreach (var sub in Directory.EnumerateDirectories(d))
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) Directory.Delete(sub);
                Directory.Delete(d, true);
            }
            Directory.CreateDirectory(d);
        }
        File.WriteAllText(Path.Combine(exeDir, "secret.txt"), "TOP-SECRET-OUTSIDE");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "TOP-SECRET-JUNCTION");
        File.WriteAllText(Path.Combine(project, "index.html"), "<!doctype html><title>bg</title><h1>BG-INDEX-MARKER</h1>");
        File.WriteAllText(Path.Combine(project, "app.js"), "console.log('app');");
        File.WriteAllText(Path.Combine(project, "mod.mjs"), "export const x = 1;");
        File.WriteAllText(Path.Combine(project, "data.json"), "{\"ok\":true}");
        File.WriteAllBytes(Path.Combine(project, "x.wasm"), new byte[] { 0, 0x61, 0x73, 0x6d, 1, 0, 0, 0 });
        File.WriteAllText(Path.Combine(project, ".env"), "KEY=secret");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        File.WriteAllText(Path.Combine(project, ".git", "config"), "[core]");
        Directory.CreateDirectory(Path.Combine(project, "sub"));
        File.WriteAllText(Path.Combine(project, "sub", "page.html"), "<p>sub</p>");
        string spaDir = Path.Combine(project, "spa");
        Directory.CreateDirectory(spaDir);
        File.WriteAllText(Path.Combine(spaDir, "index.html"), "<p>SPA-SHELL</p>");

        var fs = Provider.GetRequiredService<FileSystemService>();
        var hooks = Provider.GetRequiredService<HookService>();
        var perms = Provider.GetRequiredService<PermissionService>();
        Svc = Provider.GetRequiredService<AgentToolService>();
        Bg = Provider.GetRequiredService<BackgroundProcessService>();
        Svc.OnPermissionRequired += info => { lock (Prompts) Prompts.Add(info.ToolName + " | " + info.Detail); return Task.FromResult(Answer); };
        fs.WorkingDirectory = project;
        // The real ~/.cluadex hooks are not part of this contract: keep them out (B7 injects its own).
        SetHooks(hooks, new List<HookDefinition>());
        perms.Rules.Clear();
        SetActivated(true);

        Console.WriteLine($"project   : {project}");
        Console.WriteLine($"bg logs   : {Bg.LogDirectory}");
        Console.WriteLine($"git bash  : {ShellRunner.FindBash() ?? "(none — cmd.exe)"}\n");

        // ═══════════════════ wiring ═══════════════════
        Console.WriteLine("── B0 wiring ──");
        await Case("B0", () =>
        {
            var modules = Provider.GetServices<IAgentToolModule>().ToList();
            Check("B0", "the background module and the run_command interceptor come from App.ConfigureServices, the registry is a shutdown hook",
                modules.OfType<BackgroundToolModule>().Count() == 1
                && Provider.GetServices<IShellCommandInterceptor>().OfType<LongRunningCommand>().Count() == 1
                && Provider.GetServices<CluadeX.Services.IAppShutdownHook>().Any(h => ReferenceEquals(h, Bg)));
            var tm = Provider.GetRequiredService<TaskManagerService>();
            Check("B0b", "the Task Manager page and the bg_* tools share ONE registry", ReferenceEquals(tm.Background, Bg) && ReferenceEquals(tm.Tasks, Bg.Items));
            Check("B0c", "the log folder is DataRoot\\bg (isolated)", string.Equals(Bg.LogDirectory, Path.Combine(settings.DataRoot, "bg"), StringComparison.OrdinalIgnoreCase), Bg.LogDirectory ?? "(null)");

            var names = Svc.BuildNativeToolSchemas().Select(s => s.Name).ToList();
            Check("B0d", "activated: bg_start / bg_serve / bg_output / bg_stop / bg_list are offered; the old task_* schemas are gone",
                new[] { "bg_start", "bg_serve", "bg_output", "bg_stop", "bg_list" }.All(names.Contains) && !names.Any(n => n.StartsWith("task_")),
                string.Join(",", names.Where(n => n.StartsWith("bg_") || n.StartsWith("task_"))));
            string text = Svc.GetToolDefinitionsPrompt();
            Check("B0e", "the [ACTION:] catalogue lists bg_start (and no TASK MANAGEMENT block)", text.Contains("[ACTION: bg_start]") && !text.Contains("[ACTION: task_create]"));
            SetActivated(false);
            var locked = Svc.BuildNativeToolSchemas().Select(s => s.Name).ToList();
            SetActivated(true);
            Check("B0f", "free tier: no bg_* tool is offered (never offered and then refused)", !locked.Any(n => n.StartsWith("bg_")));

            var module = modules.OfType<BackgroundToolModule>().Single();
            Check("B0g", "a project with index.html puts bg_serve/bg_start/bg_output/bg_stop in a small local model's core set",
                new[] { "bg_serve", "bg_start", "bg_output", "bg_stop" }.All(module.LocalCoreToolNames.Contains));
            string? section = module.GetPromptSection(new PromptSectionContext { OfferedToolNames = new[] { "read_file", "bg_serve", "bg_start", "bg_output" } });
            string? none = module.GetPromptSection(new PromptSectionContext { OfferedToolNames = new[] { "read_file" } });
            Check("B0h", "prompt section only when bg tools are offered, short (< 900 chars), names bg_serve",
                section != null && section.Contains("bg_serve") && section.Length < 900 && none == null, section ?? "(null)");
            return Task.CompletedTask;
        });

        // ═══════════════════ B1 bg_serve ═══════════════════
        Console.WriteLine("\n── B1 bg_serve (built-in static server) ──");
        int serveId = -1, servePort = 0;
        await Case("B1", async () =>
        {
            int prompts = Prompts.Count;
            var sw = Stopwatch.StartNew();
            var r = await T("bg_serve", ("path", "."));
            serveId = IdOf(r);
            var snap = Bg.Get(serveId);
            servePort = snap?.Port ?? 0;
            Check("B1", "bg_serve in the project: no prompt, ready at once with http://127.0.0.1:81xx/",
                r.Success && snap?.Url is { } u && u.StartsWith("http://127.0.0.1:81") && servePort is >= 8100 and <= 8199
                && Prompts.Count == prompts && sw.ElapsedMilliseconds < 3000, Seen(r));

            var root = await RawHttp(servePort, "GET", "/");
            Check("B1a", "GET / → 200 index.html, text/html, Cache-Control: no-store",
                root.Status == 200 && Encoding.UTF8.GetString(root.Body).Contains("BG-INDEX-MARKER")
                && root.Headers.GetValueOrDefault("Content-Type", "").StartsWith("text/html") && root.Headers.GetValueOrDefault("Cache-Control") == "no-store",
                $"{root.Status} {string.Join("; ", root.Headers.Select(h => h.Key + "=" + h.Value))}");
            var js = await RawHttp(servePort, "GET", "/app.js");
            var mjs = await RawHttp(servePort, "GET", "/mod.mjs");
            var wasm = await RawHttp(servePort, "GET", "/x.wasm");
            var json = await RawHttp(servePort, "GET", "/data.json");
            Check("B1b", "MIME: .js/.mjs text/javascript, .wasm application/wasm, .json application/json",
                js.Headers.GetValueOrDefault("Content-Type", "").StartsWith("text/javascript")
                && mjs.Headers.GetValueOrDefault("Content-Type", "").StartsWith("text/javascript")
                && wasm.Headers.GetValueOrDefault("Content-Type") == "application/wasm" && wasm.Body.Length == 8
                && json.Headers.GetValueOrDefault("Content-Type", "").StartsWith("application/json"),
                $"{js.Headers.GetValueOrDefault("Content-Type")} | {mjs.Headers.GetValueOrDefault("Content-Type")} | {wasm.Headers.GetValueOrDefault("Content-Type")} | {json.Headers.GetValueOrDefault("Content-Type")}");
            var head = await RawHttp(servePort, "HEAD", "/index.html");
            Check("B1c", "HEAD: 200 with the file's Content-Length and no body",
                head.Status == 200 && head.Headers.GetValueOrDefault("Content-Length") == new FileInfo(Path.Combine(project, "index.html")).Length.ToString() && head.Body.Length == 0,
                $"{head.Status} len={head.Headers.GetValueOrDefault("Content-Length")} body={head.Body.Length}");

            var refused = new List<string>();
            foreach (var target in new[] { "/../secret.txt", "/%2e%2e/secret.txt", "/%2e%2e%2fsecret.txt", "/sub/..%5c..%5csecret.txt",
                                           "/sub/%2E%2E/%2E%2E/secret.txt", "/C:/Windows/win.ini", "/index.html::$DATA", "/.git/config", "/.env",
                                           "/con", "/sub/...", "//localhost/c$/x" })
            {
                var x = await RawHttp(servePort, "GET", target);
                string body = Encoding.UTF8.GetString(x.Body);
                if (x.Status is 400 or 403 && !body.Contains("TOP-SECRET") && !body.Contains("[core]") && !body.Contains("KEY=secret")) continue;
                refused.Add($"{target} → {x.Status} {Trunc(body, 60)}");
            }
            Check("B1d", "traversal refused: .., %2e%2e, %5c, drive letters, ::$DATA, .git, .env, device names, // (400/403, nothing leaks)",
                refused.Count == 0, string.Join("\n", refused));

            var rebind = await RawHttp(servePort, "GET", "/", host: "attacker.example");
            Check("B1e", "DNS-rebinding guard: a non-loopback Host header gets 403", rebind.Status == 403, rebind.Status.ToString());

            // A junction inside the project that leads out must not be followed.
            string junction = Path.Combine(project, "jx");
            bool made = CommandWorks("mklink", $"/J \"{junction}\" \"{outside}\"");
            if (made)
            {
                var jx = await RawHttp(servePort, "GET", "/jx/secret.txt");
                Check("B1f", "a junction that leads outside the served folder is refused", jx.Status == 403 && !Encoding.UTF8.GetString(jx.Body).Contains("TOP-SECRET"), jx.Status.ToString());
                try { Directory.Delete(junction); } catch { }
            }
            else GapNote("B1f", "could not create a test junction (mklink /J) — junction escape not checked");

            var missing = await RawHttp(servePort, "GET", "/sprites/missing-ball.png");
            var outp = await T("bg_output", ("id", serveId.ToString()), ("since", "start"));
            Check("B1g", "a 404 is answered and written to the process output (bg_output shows it)",
                missing.Status == 404 && Seen(outp).Contains("404 GET /sprites/missing-ball.png"), Seen(outp));

            var listeners = ReadinessProbe.ListenersOn(servePort);
            Check("B1h", "only loopback is bound (127.0.0.1 / ::1) — no 0.0.0.0 or :: listener",
                listeners.Count > 0 && listeners.All(e => IPAddress.IsLoopback(e.Address)), string.Join(", ", listeners));
            var v6 = await RawHttp(servePort, "GET", "/", host: $"[::1]:{servePort}", address: IPAddress.IPv6Loopback);
            Check("B1i", "[::1] answers too (Host [::1]:port)", v6.Status == 200, v6.Status.ToString());

            var again = await T("bg_serve", ("path", "."));
            Check("B1j", "bg_serve twice on the same folder returns the same server (double-tap safe)",
                again.Success && IdOf(again) == serveId && Seen(again).Contains("Already running"), Seen(again));

            var spa = await T("bg_serve", ("path", "spa"), ("spa", "true"));
            int spaPort = Bg.Get(IdOf(spa))?.Port ?? 0;
            var route = await RawHttp(spaPort, "GET", "/game/level/3");
            var spaMissingFile = await RawHttp(spaPort, "GET", "/missing.png");
            Check("B1k", "spa=true: an unknown route gets index.html, an unknown file still 404s",
                route.Status == 200 && Encoding.UTF8.GetString(route.Body).Contains("SPA-SHELL") && spaMissingFile.Status == 404,
                $"{route.Status} {spaMissingFile.Status}");
            await T("bg_stop", ("id", IdOf(spa).ToString()));

            int before = Prompts.Count;
            Answer = false;
            var outsideServe = await T("bg_serve", ("path", outside));
            Answer = true;
            Check("B1l", "bg_serve OUTSIDE the project asks — and a No serves nothing",
                Prompts.Count == before + 1 && !outsideServe.Success && Bg.List().All(p => !p.IsRunning || !p.Cwd.Equals(outside, StringComparison.OrdinalIgnoreCase)),
                Seen(outsideServe));
        });

        // ═══════════════════ B2 bg_start readiness ═══════════════════
        Console.WriteLine("\n── B2 bg_start readiness ──");
        await Case("B2", async () =>
        {
            int p1 = FreePort();
            var sw = Stopwatch.StartNew();
            var r = await T("bg_start", ("command", $"{Self} --serve {p1}"), ("name", "harness server"));
            long ms = sw.ElapsedMilliseconds;
            var s = Bg.Get(IdOf(r));
            Check("B2", $"bg_start '<harness> --serve {p1}': ready in under 5 s with the printed URL",
                r.Success && ms < 5000 && s?.Url == $"http://127.0.0.1:{p1}/" && s.ReadyAt != null, $"{ms} ms\n{Seen(r)}");

            int p2 = FreePort();
            sw.Restart();
            var v6 = await T("bg_start", ("command", $"{Self} --serve {p2} --v6only --quiet"), ("port", p2.ToString()));
            var s6 = Bg.Get(IdOf(v6));
            Check("B2b", "a server listening on [::1] only (and printing nothing) is still detected by the TCP probe",
                v6.Success && s6?.Url == $"http://[::1]:{p2}/" && sw.ElapsedMilliseconds < 5000, Seen(v6));

            sw.Restart();
            var pat = await T("bg_start", ("command", $"{Self} --say \"warming up\" --say \"all systems go\" --sleep"), ("ready_pattern", "all systems GO"));
            var sp = Bg.Get(IdOf(pat));
            Check("B2c", "ready_pattern (case-insensitive) marks it ready without any port",
                pat.Success && sp?.ReadyAt != null && sp.IsRunning && sw.ElapsedMilliseconds < 5000 && Seen(pat).Contains("all systems go"), Seen(pat));

            sw.Restart();
            var boom = await T("bg_start", ("command", "echo boom-before-exit && exit 3"));
            Check("B2d", "a command that exits at once fails at once with its exit code and output",
                !boom.Success && sw.ElapsedMilliseconds < 4000 && Seen(boom).Contains("3") && Seen(boom).Contains("boom-before-exit"), $"{sw.ElapsedMilliseconds} ms\n{Seen(boom)}");

            int busy = FreePort();
            var holder = new TcpListener(IPAddress.Loopback, busy);
            holder.Start();
            try
            {
                int running = Bg.RunningCount;
                sw.Restart();
                var taken = await T("bg_start", ("command", $"{Self} --serve {busy}"), ("port", busy.ToString()));
                Check("B2e", "a busy port fails at once, naming the port and the PID that holds it (nothing started)",
                    !taken.Success && sw.ElapsedMilliseconds < 2000 && Seen(taken).Contains(busy.ToString())
                    && Seen(taken).Contains(Environment.ProcessId.ToString()) && Bg.RunningCount == running, Seen(taken));
            }
            finally { holder.Stop(); }

            var bad = await T("bg_start", ("command", $"{Self} --sleep"), ("ready_pattern", "(unclosed"));
            Check("B2f", "an invalid ready_pattern is refused with a readable message (nothing started)",
                !bad.Success && Seen(bad).Contains("regular expression") && !Seen(bad).Contains("   at "), Seen(bad));

            var dup = await T("bg_start", ("command", $"{Self} --serve {p1}"), ("name", "harness server"));
            Check("B2g", "the same command in the same folder twice returns the running one (no second process)",
                dup.Success && IdOf(dup) == IdOf(r) && Seen(dup).Contains("Already running"), Seen(dup));

            foreach (var id in new[] { IdOf(r), IdOf(v6), IdOf(pat) }) await Bg.StopAsync(id, TimeSpan.FromSeconds(3));
        });

        // ═══════════════════ B3 bg_output ═══════════════════
        Console.WriteLine("\n── B3 bg_output ──");
        await Case("B3", async () =>
        {
            var r = await T("bg_start", ("command", $"{Self} --chatter"), ("ready_pattern", "READY-CHATTER"));
            int id = IdOf(r);
            var first = await T("bg_output", ("id", $"bg#{id}"));
            string a = Seen(first);
            Check("B3", "bg_output (id as bg#N): the lines so far, Thai intact, ANSI colour codes stripped",
                first.Success && a.Contains("พร้อมแล้ว — สวัสดีครับ") && !a.Contains('\x1b') && a.Contains("line 2"), a);
            await WaitUntil(() => Bg.Read(id, 200, fromStart: true, reader: "probe").Lines.Any(l => l.Contains("later 3")), 5000);
            var second = await T("bg_output", ("id", id.ToString()));
            string b = Seen(second);
            Check("B3b", "a second read returns ONLY the new lines, with an 'earlier lines already shown' note",
                b.Contains("later 1") && b.Contains("later 3") && !b.Contains("line 2") && b.Contains("already shown"), b);
            var third = await T("bg_output", ("id", id.ToString()));
            Check("B3c", "nothing new → says so", Seen(third).Contains("no new output"), Seen(third));
            var tail = await T("bg_output", ("id", id.ToString()), ("since", "start"), ("lines", "3"));
            string t = Seen(tail);
            Check("B3d", "since=start + lines=3: the newest 3 lines of everything kept, and how many were left out",
                t.Contains("later 3") && !t.Contains("line 1") && t.Contains("left out"), t);
            var errors = await T("bg_output", ("id", id.ToString()), ("since", "start"), ("level", "error"));
            Check("B3e", "level=error keeps only error lines", Seen(errors).Contains("ERROR: disk on fire") && !Seen(errors).Contains("later 1"), Seen(errors));
            var filtered = await T("bg_output", ("id", id.ToString()), ("since", "start"), ("filter", @"later [12]"));
            Check("B3f", "filter= is a regex on the lines", Seen(filtered).Contains("later 1") && Seen(filtered).Contains("later 2") && !Seen(filtered).Contains("later 3"), Seen(filtered));
            string log = Bg.Get(id)?.LogPath ?? "";
            await Bg.StopAsync(id, TimeSpan.FromSeconds(3));
            string logText = File.Exists(log) ? File.ReadAllText(log, Encoding.UTF8) : "";
            Check("B3g", "the full log is on disk (DataRoot\\bg\\<id>.log), UTF-8, ANSI-free",
                logText.Contains("พร้อมแล้ว") && logText.Contains("later 3") && !logText.Contains('\x1b'), log);
            var unknown = await T("bg_output", ("id", "999"));
            Check("B3h", "an unknown id is an error that lists what is running", !unknown.Success && Seen(unknown).Contains("bg#999") && Seen(unknown).Contains("Running now"), Seen(unknown));
            var junk = await T("bg_output", ("id", "banana"));
            Check("B3i", "a non-numeric id gets a readable message, never an exception", !junk.Success && Seen(junk).Contains("not a background process id"), Seen(junk));
        });

        // ═══════════════════ B4 orphans ═══════════════════
        Console.WriteLine("\n── B4 bg_stop takes the whole tree, orphans included ──");
        await Case("B4", async () =>
        {
            int port = FreePort();
            var r = await T("bg_start", ("command", $"{Self} --spawn-orphan {port}"), ("port", port.ToString()));
            int id = IdOf(r);
            await WaitUntil(() => Bg.Read(id, 50, fromStart: true, reader: "probe").Lines.Any(l => l.StartsWith("ORPHAN_PID=")), 5000);
            int orphan = int.Parse(Bg.Read(id, 50, fromStart: true, reader: "probe").Lines.First(l => l.StartsWith("ORPHAN_PID="))["ORPHAN_PID=".Length..]);
            var jobPids = Bg.ProcessIdsOf(id);
            Check("B4", "the orphaned grandchild is inside the job (it is listed among the job's processes)", jobPids.Contains(orphan) && Alive(orphan),
                $"orphan {orphan}, job [{string.Join(",", jobPids)}]");
            var sw = Stopwatch.StartNew();
            var stop = await T("bg_stop", ("id", id.ToString()));
            bool gone = await WaitUntil(() => !Alive(orphan) && !ReadinessProbe.IsListening(port), 3000);
            Check("B4b", "bg_stop: the orphaned grandchild is gone and its port is free within 3 s",
                stop.Success && gone && sw.ElapsedMilliseconds < 3500, $"{sw.ElapsedMilliseconds} ms alive={Alive(orphan)} listening={ReadinessProbe.IsListening(port)}\n{Seen(stop)}");
            var twice = await T("bg_stop", ("id", id.ToString()));
            Check("B4c", "bg_stop again is idempotent: it reports how it ended", twice.Success && Seen(twice).Contains("already ended"), Seen(twice));

            // Baseline: what Kill(entireProcessTree) — the old task_stop — does to the same tree.
            int p2 = FreePort();
            var psi = new ProcessStartInfo(Exe, $"--spawn-orphan {p2}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var spawner = Process.Start(psi)!;
            string? line = await spawner.StandardOutput.ReadLineAsync();
            int orphan2 = line != null && line.StartsWith("ORPHAN_PID=") ? int.Parse(line["ORPHAN_PID=".Length..]) : -1;
            spawner.Kill(entireProcessTree: true);
            await Task.Delay(1500);
            if (orphan2 > 0 && Alive(orphan2))
            {
                GapNote("B4d", $"baseline: Process.Kill(entireProcessTree) left the orphan (pid {orphan2}) running on port {p2} — the job object is what catches it");
                try { using var o = Process.GetProcessById(orphan2); o.Kill(); } catch { }
            }
            else Check("B4d", "baseline recorded (Kill(entireProcessTree) happened to get the orphan this time)", true);
        });

        // ═══════════════════ B5 crash containment ═══════════════════
        Console.WriteLine("\n── B5 crash containment ──");
        await Case("B5", async () =>
        {
            int p1 = FreePort(), p2 = FreePort();
            string hostLogs = Path.Combine(exeDir, "host-logs");
            Directory.CreateDirectory(hostLogs);
            var psi = new ProcessStartInfo(Exe, $"--host \"{hostLogs}\" {p1} {p2}")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var host = Process.Start(psi)!;
            _ = host.StandardError.ReadToEndAsync();
            string? ready = null;
            var readTask = Task.Run(async () =>
            {
                string? l;
                while ((l = await host.StandardOutput.ReadLineAsync()) != null)
                    if (l.StartsWith("HOST_READY")) { ready = l; break; }
            });
            await Task.WhenAny(readTask, Task.Delay(30000));
            if (ready == null)
            {
                try { host.Kill(true); } catch { }
                Check("B5", "host started its children", false, "no HOST_READY within 30 s");
                return;
            }
            var pids = ready.Split(' ').First(t => t.StartsWith("pids=")).Substring(5).Split(',').Select(int.Parse).ToList();
            Check("B5", $"the host runs {pids.Count} processes in its jobs (2 servers, one behind an orphaning spawner)",
                pids.Count >= 4 && pids.All(Alive) && ReadinessProbe.IsListening(p1) && ReadinessProbe.IsListening(p2), ready);
            var sw = Stopwatch.StartNew();
            host.Kill();   // TerminateProcess on the host ONLY — a crash: no shutdown hook, no tree kill
            bool allGone = await WaitUntil(() => pids.All(p => !Alive(p)) && !ReadinessProbe.IsListening(p1) && !ReadinessProbe.IsListening(p2), 2000);
            Check("B5b", "TerminateProcess on the host kills every child and grandchild within 2 s (KILL_ON_JOB_CLOSE), ports freed",
                allGone, $"{sw.ElapsedMilliseconds} ms; alive: {string.Join(",", pids.Where(Alive))}");
            foreach (var p in pids.Where(Alive)) { try { using var x = Process.GetProcessById(p); x.Kill(); } catch { } }
        });

        // ═══════════════════ B6 bg_list + aliases + cap ═══════════════════
        Console.WriteLine("\n── B6 bg_list, task_* aliases, the process cap ──");
        await Case("B6", async () =>
        {
            Check("B6", "task_create / task_list / task_output / task_stop resolve to bg_start / bg_list / bg_output / bg_stop",
                Svc.ResolveToolTypePublic("task_create") == ToolType.BgStart && Svc.ResolveToolTypePublic("task_list") == ToolType.BgList
                && Svc.ResolveToolTypePublic("task_output") == ToolType.BgOutput && Svc.ResolveToolTypePublic("task_stop") == ToolType.BgStop
                && Svc.ResolveToolTypePublic("BG_SERVE") == ToolType.BgServe);
            var made = await T("task_create", ("command", $"{Self} --say listed --sleep"), ("name", "via task_create"), ("ready_pattern", "listed"));
            int id = IdOf(made);
            var list = await T("task_list");
            string l = Seen(list);
            var row = l.Split('\n').FirstOrDefault(x => x.StartsWith($"bg#{id} ")) ?? "";
            Check("B6b", "bg_list (via task_list): id, kind, status, pid, uptime, name, folder and last line",
                list.Success && row.Contains("[shell]") && row.Contains("running") && row.Contains("pid ") && row.Contains("up ")
                && row.Contains("via task_create") && row.Contains(project) && row.Contains("last: listed")
                && l.Contains($"bg#{serveId} [static]") && l.Contains($"http://127.0.0.1:{servePort}/"), l);
            var stopped = await T("task_stop", ("task_id", id.ToString()));
            Check("B6c", "task_stop (old argument name task_id) stops it", stopped.Success && Bg.Get(id)?.IsRunning == false, Seen(stopped));

            var s = settings.Settings;
            int keep = s.BgMaxProcesses;
            s.BgMaxProcesses = Bg.RunningCount + 2;
            var ids = new List<int>();
            try
            {
                for (int i = 0; i < 2; i++) ids.Add(IdOf(await T("bg_start", ("command", $"{Self} --sleep --tag cap{i}"), ("timeout_ms", "1000"))));
                var over = await T("bg_start", ("command", $"{Self} --sleep --tag cap-over"), ("timeout_ms", "1000"));
                Check("B6d", "the process cap (Settings.BgMaxProcesses) refuses one more, naming the cap and the running ids",
                    !over.Success && Seen(over).Contains("most allowed") && Seen(over).Contains($"bg#{ids[0]}"), Seen(over));
            }
            finally
            {
                s.BgMaxProcesses = keep;
                foreach (var i in ids) await Bg.StopAsync(i, TimeSpan.FromSeconds(3));
            }
            Check("B6e", "the default cap is 8", new AppSettings().BgMaxProcesses == 8 && Bg.MaxProcesses == 8);
        });

        // ═══════════════════ B7 through run_command ═══════════════════
        Console.WriteLine("\n── B7 run_command / powershell hand servers to the background ──");
        await Case("B7", async () =>
        {
            bool python = CommandWorks("python", "--version");
            if (python)
            {
                int port = ReadinessProbe.IsListening(8123) ? FreePort() : 8123;
                int prompts = Prompts.Count;
                var sw = Stopwatch.StartNew();
                var r = await T("run_command", ("command", $"python -m http.server {port} --bind 127.0.0.1"));
                long ms = sw.ElapsedMilliseconds;
                int id = IdOf(r);
                var resp = id > 0 ? await RawHttp(port, "GET", "/") : default;
                Check("B7", $"run_command 'python -m http.server {port}' returns in under 5 s as 'Started in background as bg#N → URL'",
                    r.Success && ms < 5000 && Seen(r).Contains("Started in background as bg#") && Seen(r).Contains(port.ToString()) && resp.Status == 200,
                    $"{ms} ms\n{Seen(r)}");
                Check("B7b", "…asking once (for run_command) — the background start asks nothing new", Prompts.Count == prompts + 1, string.Join("\n", Prompts.Skip(prompts)));
                Check("B7c", "the call stays run_command (its row, its result pairing)", r.Type == ToolType.RunCommand && r.ToolName == "run_command");
                // The Microsoft Store python.exe is an app-execution alias whose real process starts OUTSIDE the
                // caller's job — it must still be found, contained and stopped with its port.
                await Task.Delay(1200);   // a few tree sweeps
                var tree = id > 0 ? Bg.ProcessIdsOf(id) : Array.Empty<int>();
                int owner = ReadinessProbe.FindListeningOwner(port)?.Pid ?? -1;
                Check("B7c2", "the process that actually listens (the Store alias's real python, if that is the install) is inside the server's tree",
                    owner > 0 && tree.Contains(owner), $"owner {owner}, tree [{string.Join(",", tree)}]");
                var sw2 = Stopwatch.StartNew();
                if (id > 0) await Bg.StopAsync(id, TimeSpan.FromSeconds(3));
                bool freed = await WaitUntil(() => !ReadinessProbe.IsListening(port) && tree.All(p => !Alive(p)), 3000);
                Check("B7c3", "bg_stop ends that whole tree and frees the port within 3 s",
                    freed, $"{sw2.ElapsedMilliseconds} ms; alive [{string.Join(",", tree.Where(Alive))}] listening={ReadinessProbe.IsListening(port)}");
                // Only ever this server's own processes — never anything else named python on the machine.
                foreach (var p in tree.Where(Alive)) { try { using var x = Process.GetProcessById(p); x.Kill(true); } catch { } }
            }
            else GapNote("B7", "python is not installed — 'python -m http.server' not run");

            if (CommandWorks("npm", "--version"))
            {
                int port = FreePort();
                File.WriteAllText(Path.Combine(project, "package.json"),
                    "{\"name\":\"bgproj\",\"private\":true,\"scripts\":{\"dev\":\"\\\"" + Exe.Replace("\\", "\\\\") + "\\\" --serve " + port + "\"}}");
                var sw = Stopwatch.StartNew();
                var r = await T("run_command", ("command", "npm run dev"));
                long ms = sw.ElapsedMilliseconds;
                Check("B7d", "run_command 'npm run dev' (a dev script) returns in under 5 s as started bg#N with the printed URL",
                    r.Success && ms < 5000 && Seen(r).Contains("Started in background as bg#") && Seen(r).Contains($"127.0.0.1:{port}"), $"{ms} ms\n{Seen(r)}");
                int id = IdOf(r);
                if (id > 0) await Bg.StopAsync(id, TimeSpan.FromSeconds(3));
                File.Delete(Path.Combine(project, "package.json"));
                var noScript = await T("run_command", ("command", "npm run dev"));
                Check("B7e", "'npm run dev' with no package.json fails fast with npm's own error (no 120 s hang)",
                    !noScript.Success && Seen(noScript).Contains("exited with code"), Seen(noScript));
            }
            else GapNote("B7d", "npm is not installed — 'npm run dev' not run");

            var vsw = Stopwatch.StartNew();
            var vitest = await T("run_command", ("command", "vitest watch"));
            Check("B7f", "'vitest watch' (an explicit watch) is refused at once with the 'vitest run' hint (nothing started)",
                !vitest.Success && vsw.ElapsedMilliseconds < 1000 && Seen(vitest).Contains("vitest run"), Seen(vitest));
            // Review P2c: vitest watches only when stdin is a terminal (watch = !CI && stdin.isTTY) — run_command's is
            // not, so plain "vitest" runs once and must not be refused. An explicit --watch / -w still is.
            var plainVitest = LongRunningCommand.Classify("vitest", ShellRunner.Shell.Bash, project);
            var npxVitest = LongRunningCommand.Classify("npx vitest --reporter=dot", ShellRunner.Shell.Bash, project);
            var flagVitest = LongRunningCommand.Classify("npx vitest --watch", ShellRunner.Shell.Bash, project);
            var shortFlag = LongRunningCommand.Classify("vitest -w", ShellRunner.Shell.Bash, project);
            Check("B7f2", "plain 'vitest' / 'npx vitest' is an ordinary one-shot command; control: 'vitest --watch' and 'vitest -w' are still watchers",
                plainVitest.Kind == LongRunningCommand.CommandKind.None && npxVitest.Kind == LongRunningCommand.CommandKind.None
                && flagVitest.Kind == LongRunningCommand.CommandKind.Watcher && shortFlag.Kind == LongRunningCommand.CommandKind.Watcher,
                $"vitest={plainVitest.Kind} npx={npxVitest.Kind} --watch={flagVitest.Kind} -w={shortFlag.Kind}");
            var tsc = await T("run_command", ("command", "cd sub && npx tsc -w -p ."));
            Check("B7g", "'npx tsc -w' (after a cd) is refused with 'tsc --noEmit'", !tsc.Success && Seen(tsc).Contains("tsc --noEmit"), Seen(tsc));
            var plain = await T("run_command", ("command", "echo vite is just a word here"));
            Check("B7h", "an ordinary command mentioning a server's name runs normally", plain.Success && Seen(plain).Contains("vite is just a word"), Seen(plain));

            SetActivated(false);
            try
            {
                int running = Bg.RunningCount;
                var lsw = Stopwatch.StartNew();
                var locked = await T("run_command", ("command", "python -m http.server 8124"));
                Check("B7i", "Dev Preview locked: a server command fails in under 1 s with the license reason and the Terminal-drawer alternative",
                    !locked.Success && lsw.ElapsedMilliseconds < 1000 && Seen(locked).Contains("Dev Preview") && Seen(locked).Contains("Terminal")
                    && Bg.RunningCount == running, $"{lsw.ElapsedMilliseconds} ms\n{Seen(locked)}");
                var lockedTool = await T("bg_serve", ("path", "."));
                Check("B7j", "Dev Preview locked: bg_serve is refused with the exact reason (the free trial)", !lockedTool.Success && Seen(lockedTool).Contains("trial"), Seen(lockedTool));
            }
            finally { SetActivated(true); }

            // The dangerous-command blocklist and PreToolUse shell hooks fire for bg_start as for run_command.
            var danger = await T("bg_start", ("command", "icacls ."));
            Check("B7k", "bg_start of a blocklisted command is refused ('Command blocked for safety')", !danger.Success && Seen(danger).Contains("blocked for safety"), Seen(danger));
            SetHooks(hooks, new List<HookDefinition>
            {
                new() { Phase = "PreToolUse", Matcher = HookBundleService.ShellCommandTools, Command = "echo blocked-by-bgtest-hook %CLUADEX_HOOK_TOOL% 1>&2 & exit 2", TimeoutMs = 15000 },
            });
            try
            {
                int running = Bg.RunningCount;
                var hooked = await T("bg_start", ("command", $"{Self} --sleep --tag hooked"));
                var hookedAlias = await T("task_create", ("command", $"{Self} --sleep --tag hooked2"));
                Check("B7l", "a PreToolUse shell hook (the shell-safety matcher) blocks bg_start and task_create; nothing starts",
                    !hooked.Success && Seen(hooked).Contains("blocked-by-bgtest-hook") && !hookedAlias.Success && Bg.RunningCount == running,
                    Seen(hooked) + "\n" + Seen(hookedAlias));
            }
            finally { SetHooks(hooks, new List<HookDefinition>()); }

            // No new prompt for a command the user already allowed under 'execute' — also a run_command-scoped rule.
            int before = Prompts.Count;
            var asked = await T("bg_start", ("command", $"{Self} --sleep --tag ask"), ("timeout_ms", "1000"));
            Check("B7m", "with no rule, bg_start asks once, showing the command and the folder",
                Prompts.Count == before + 1 && Prompts.Last().Contains("--tag ask") && Prompts.Last().Contains(project), string.Join("\n", Prompts.Skip(before)));
            await Bg.StopAsync(IdOf(asked), TimeSpan.FromSeconds(3));
            perms.AddRule(new PermissionRule { Scope = "execute", ToolName = "run_command", Pattern = "**--tag allowed**", Action = PermAction.Allow });
            before = Prompts.Count;
            var allowed = await T("bg_start", ("command", $"{Self} --sleep --tag allowed"), ("timeout_ms", "1000"));
            Check("B7n", "a run_command Allow rule for the command means bg_start of it asks nothing", allowed.Success && Prompts.Count == before, Seen(allowed));
            await Bg.StopAsync(IdOf(allowed), TimeSpan.FromSeconds(3));
            perms.AddRule(new PermissionRule { Scope = "execute", ToolName = "run_command", Pattern = "**--tag denied**", Action = PermAction.Deny });
            var denied = await T("bg_start", ("command", $"{Self} --sleep --tag denied"), ("timeout_ms", "1000"));
            Check("B7o", "a run_command Deny rule for the command denies bg_start of it too", !denied.Success && Seen(denied).Contains("denied"), Seen(denied));
            perms.Rules.Clear();

            var timedOut = Provider.GetServices<IShellCommandInterceptor>().OfType<LongRunningCommand>().Single()
                .OnTimeout(new ShellCommandContext { Call = new ToolCall { Type = ToolType.RunCommand, ToolName = "run_command" }, Command = "node server.js" },
                           "\x1b[32mApp listening at http://localhost:4000\x1b[0m");
            Check("B7p", "a timed-out command that printed a localhost URL is called a server, with bg_start named",
                timedOut != null && timedOut.Contains("http://localhost:4000/") && timedOut.Contains("bg_start"), timedOut ?? "(null)");

            var cls = new (string Cmd, LongRunningCommand.CommandKind Kind, int? Port)[]
            {
                ("npx vite --port 5175", LongRunningCommand.CommandKind.Server, 5175),
                ("PORT=4001 node server.js &", LongRunningCommand.CommandKind.Background, null),
                ("php -S localhost:8090 -t public", LongRunningCommand.CommandKind.Server, 8090),
                ("python manage.py runserver 0.0.0.0:8002", LongRunningCommand.CommandKind.Server, 8002),
                ("docker compose up", LongRunningCommand.CommandKind.Server, null),
                ("docker compose up -d", LongRunningCommand.CommandKind.None, null),
                ("jest --watchAll=false", LongRunningCommand.CommandKind.None, null),
                ("vitest run", LongRunningCommand.CommandKind.None, null),
                ("git commit -m \"run vite and vitest\"", LongRunningCommand.CommandKind.None, null),
                ("tail -f build.log", LongRunningCommand.CommandKind.Watcher, null),
                ("nohup python3 -m http.server 9001 > out.log 2>&1 &", LongRunningCommand.CommandKind.Server, 9001),
            };
            var wrong = cls.Select(c => (c, got: LongRunningCommand.Classify(c.Cmd, ShellRunner.Shell.Bash, project)))
                           .Where(x => x.got.Kind != x.c.Kind || (x.c.Port != null && x.got.Port != x.c.Port))
                           .Select(x => $"{x.c.Cmd} → {x.got.Kind} {x.got.Port}").ToList();
            // Review P1: a plain Start-Process detaches by itself (ShellExecute) — what it opens must outlive CluadeX, so
            // it is NOT backgrounded into a job. Only -NoNewWindow / -Redirect* without -Wait inherits the pipes.
            var ps = LongRunningCommand.Classify("Start-Process python -ArgumentList '-m','http.server'", ShellRunner.Shell.PowerShell, project);
            var psHidden = LongRunningCommand.Classify("Start-Process -WindowStyle Hidden -FilePath notepad.exe", ShellRunner.Shell.PowerShell, project);
            var psNoNew = LongRunningCommand.Classify("Start-Process -NoNewWindow node server.js", ShellRunner.Shell.PowerShell, project);
            var psNoNewWait = LongRunningCommand.Classify("Start-Process -NoNewWindow -Wait node build.js", ShellRunner.Shell.PowerShell, project);
            var psAlias = LongRunningCommand.Classify("start -nnw node server.js", ShellRunner.Shell.PowerShell, project);
            var psRedirect = LongRunningCommand.Classify("saps node -ArgumentList server.js -RedirectStandardOutput out.log", ShellRunner.Shell.PowerShell, project);
            var psWaitFalse = LongRunningCommand.Classify("Start-Process -nonew -Wait:$false node server.js", ShellRunner.Shell.PowerShell, project);
            Check("B7q", "classifier: servers with their port, watchers, explicit backgrounding, and look-alikes left alone; a plain Start-Process is left alone, " +
                         "-NoNewWindow / -nnw / -RedirectStandardOutput without -Wait is backgrounded, with -Wait it is not",
                wrong.Count == 0 && ps.Kind == LongRunningCommand.CommandKind.None && psHidden.Kind == LongRunningCommand.CommandKind.None
                && psNoNew.Kind == LongRunningCommand.CommandKind.Background && psNoNewWait.Kind == LongRunningCommand.CommandKind.None
                && psAlias.Kind == LongRunningCommand.CommandKind.Background && psRedirect.Kind == LongRunningCommand.CommandKind.Background
                && psWaitFalse.Kind == LongRunningCommand.CommandKind.Background,
                string.Join("\n", wrong) + $"\nStart-Process → {ps.Kind}, hidden → {psHidden.Kind}, -NoNewWindow → {psNoNew.Kind}, " +
                $"-NoNewWindow -Wait → {psNoNewWait.Kind}, start -nnw → {psAlias.Kind}, -Redirect → {psRedirect.Kind}, -Wait:$false → {psWaitFalse.Kind}");
        });

        // ═══════════════════ B8 launch.json ═══════════════════
        Console.WriteLine("\n── B8 launch.json (.cluadex first, then Claude Code's .claude) ──");
        await Case("B8", async () =>
        {
            int pClaude = FreePort(), pCluadex = FreePort();
            string Json(int port) =>
                "{\"version\":\"0.0.1\",\"configurations\":[{\"name\":\"snooker-test\",\"runtimeExecutable\":\"" + Exe.Replace("\\", "\\\\")
                + "\",\"runtimeArgs\":[\"--serve\",\"" + port + "\",\"--quiet\"],\"port\":" + port + "}]}";
            Directory.CreateDirectory(Path.Combine(project, ".claude"));
            File.WriteAllText(Path.Combine(project, ".claude", "launch.json"), Json(pClaude));
            int before = Prompts.Count;
            var r = await T("bg_start", ("config", "snooker-test"));
            int id = IdOf(r);
            Check("B8", "config=snooker-test from .claude/launch.json (Claude Code's schema) starts and is ready on its port",
                r.Success && Bg.Get(id)?.Url?.Contains($":{pClaude}/") == true, Seen(r));
            Check("B8b", "the permission prompt shows the command the configuration builds",
                Prompts.Count == before + 1 && Prompts.Last().Contains("--serve") && Prompts.Last().Contains("launch.json"), string.Join("\n", Prompts.Skip(before)));
            await Bg.StopAsync(id, TimeSpan.FromSeconds(3));

            Directory.CreateDirectory(Path.Combine(project, ".cluadex"));
            File.WriteAllText(Path.Combine(project, ".cluadex", "launch.json"), Json(pCluadex));
            var r2 = await T("bg_start", ("config", "SNOOKER-TEST"));
            int id2 = IdOf(r2);
            Check("B8c", ".cluadex/launch.json wins over .claude/launch.json (and names match case-insensitively)",
                r2.Success && Bg.Get(id2)?.Url?.Contains($":{pCluadex}/") == true, Seen(r2));
            await Bg.StopAsync(id2, TimeSpan.FromSeconds(3));

            var missing = await T("bg_start", ("config", "nope"));
            Check("B8d", "an unknown configuration lists the ones defined", !missing.Success && Seen(missing).Contains("snooker-test"), Seen(missing));
            File.WriteAllText(Path.Combine(project, ".cluadex", "launch.json"), "{ not json");
            var broken = await T("bg_start", ("config", "snooker-test"));
            Check("B8e", "a broken .cluadex/launch.json is reported and .claude/launch.json still works",
                broken.Success && Bg.Get(IdOf(broken))?.Url?.Contains($":{pClaude}/") == true, Seen(broken));
            await Bg.StopAsync(IdOf(broken), TimeSpan.FromSeconds(3));
            Directory.Delete(Path.Combine(project, ".cluadex"), true);
            Directory.Delete(Path.Combine(project, ".claude"), true);
        });

        // ═══════════════════ B11-B13 scenarios ═══════════════════
        Console.WriteLine("\n── scenarios: project switch, Thai, cancel ──");
        await Case("B11", async () =>
        {
            int port = FreePort();
            var r = await T("bg_start", ("command", $"{Self} --serve {port}"));
            int id = IdOf(r);
            string other = Path.Combine(exeDir, "bg-project-2");
            Directory.CreateDirectory(other);
            fs.WorkingDirectory = other;
            try
            {
                var list = await T("bg_list");
                Check("B11", "a server keeps running across a project switch and stays listed with its own folder",
                    Seen(list).Contains($"bg#{id} ") && Seen(list).Contains(project) && Bg.Get(id)?.IsRunning == true, Seen(list));
                var stop = await T("bg_stop", ("id", id.ToString()));
                Check("B11b", "…and can still be stopped from the other project", stop.Success && !ReadinessProbe.IsListening(port), Seen(stop));
            }
            finally { fs.WorkingDirectory = project; }
        });
        await Case("B12", async () =>
        {
            var loc = Provider.GetRequiredService<LocalizationService>();
            LocalizedResources.Instance.Initialize(loc);
            loc.SetLanguage("th");
            try
            {
                var r = await T("run_command", ("command", "vitest watch"));
                var u = await T("bg_output", ("id", "999"));
                Check("B12", "Thai UI: refusals and errors come in Thai (tool names and hints intact)",
                    Seen(r).Contains("ไม่ได้รัน") && Seen(r).Contains("vitest run") && Seen(u).Contains("ไม่มีโปรเซสเบื้องหลัง"), Seen(r) + "\n" + Seen(u));
            }
            finally { loc.SetLanguage("en"); }

            var keysType = typeof(CluadeX.App).Assembly.GetType("CluadeX.Services.Localization.BackgroundKeys")!;
            var keys = (Dictionary<string, Dictionary<string, string>>)keysType.GetField("Keys", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var missing = keys.Where(k => !k.Value.TryGetValue("en", out var en) || string.IsNullOrWhiteSpace(en)
                                          || !k.Value.TryGetValue("th", out var th) || string.IsNullOrWhiteSpace(th)).Select(k => k.Key).ToList();
            var badFormat = keys.Where(k => k.Value.Count == 2 && CountHoles(k.Value["en"]) != CountHoles(k.Value["th"])).Select(k => k.Key).ToList();
            Check("B12b", $"every one of the {keys.Count} background strings has en + th, with the same {{n}} placeholders",
                missing.Count == 0 && badFormat.Count == 0, string.Join(", ", missing.Concat(badFormat)));
        });
        await Case("B13", async () =>
        {
            int port = FreePort();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
            var call = new ToolCall { Type = ToolType.BgStart, ToolName = "bg_start", Arguments = new() { ["command"] = $"{Self} --serve {port} --delay 5000", ["port"] = port.ToString() } };
            var sw = Stopwatch.StartNew();
            var r = await Svc.ExecuteToolAsync(call, cts.Token);
            int id = IdOf(r);
            var snap = id > 0 ? Bg.Get(id) : null;
            Check("B13", "cancelled while waiting for readiness: returns at once and the half-started server is stopped (no orphan)",
                !r.Success && sw.ElapsedMilliseconds < 4000 && snap?.IsRunning == false && snap.Status == "stopped",
                $"{sw.ElapsedMilliseconds} ms status={snap?.Status}\n{Seen(r)}");
        });

        // ═══════════════════ R: the T2 review's findings (each with a negative control) ═══════════════════
        Console.WriteLine("\n── R1 hooks see the command a launch.json configuration builds ──");
        await Case("R1", async () =>
        {
            string? repoRoot = RepoRoot();
            if (repoRoot == null || !CommandWorks("git", "--version"))
            {
                GapNote("R1", "git or the repo's HooksBundled folder is missing — the push-gate case was not run");
                return;
            }
            string gate = Path.Combine(repoRoot, "CluadeX", "Resources", "HooksBundled", "pretool-git-push-confirm.ps1");
            string repo = Path.Combine(exeDir, "push-repo");
            ForceDelete(repo);
            Directory.CreateDirectory(repo);
            // A throwaway repo with NO remote: even a push that got past the gate could not reach anything.
            RunProcess("git", "init -q -b main", repo);
            RunProcess("git", "-c user.email=bg@test.invalid -c user.name=bgtest commit -q --allow-empty -m init", repo);
            bool noRemote = RunProcess("git", "remote", repo).Output.Trim().Length == 0;
            Directory.CreateDirectory(Path.Combine(repo, ".claude"));
            File.WriteAllText(Path.Combine(repo, ".claude", "launch.json"),
                "{\"version\":\"0.0.1\",\"configurations\":[" +
                "{\"name\":\"pushmain\",\"runtimeExecutable\":\"git\",\"runtimeArgs\":[\"push\",\"origin\",\"main\"]}," +
                "{\"name\":\"pushfeature\",\"runtimeExecutable\":\"git\",\"runtimeArgs\":[\"push\",\"origin\",\"feature-x\"]}]}");
            string fakeProfile = Path.Combine(exeDir, "fake-profile");   // no ~/.cluadex/allow-push-main marker here
            Directory.CreateDirectory(fakeProfile);
            string oldProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
            Environment.SetEnvironmentVariable("USERPROFILE", fakeProfile);
            fs.WorkingDirectory = repo;
            SetHooks(hooks, new List<HookDefinition>
            {
                new() { Phase = "PreToolUse", Matcher = HookBundleService.ShellCommandTools,
                        Command = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{gate}\"", TimeoutMs = 20000 },
            });
            try
            {
                var viaRun = await T("run_command", ("command", "git push origin main"));
                var viaConfig = await T("bg_start", ("config", "pushmain"), ("timeout_ms", "5000"));
                var control = await T("bg_start", ("config", "pushfeature"), ("timeout_ms", "5000"));
                Check("R1", "the REAL bundled push gate blocks 'git push origin main' started as bg_start config=<launch.json entry> (the hook payload carries the built command) — as it does for run_command",
                    noRemote && Seen(viaRun).Contains("protected branch") && !viaConfig.Success && Seen(viaConfig).Contains("protected branch")
                    && !Seen(viaConfig).Contains("does not appear to be a git repository"),
                    $"remotes none={noRemote}\nrun_command: {Trunc(Seen(viaRun), 200)}\nconfig=pushmain: {Trunc(Seen(viaConfig), 400)}");
                Check("R1b", "control: a configuration pushing a non-protected branch passes the same hook and fails only at git (no remote)",
                    !Seen(control).Contains("protected branch") && System.Text.RegularExpressions.Regex.IsMatch(Seen(control), "origin|repository|exited with code"),
                    Trunc(Seen(control), 400));
            }
            finally
            {
                SetHooks(hooks, new List<HookDefinition>());
                Environment.SetEnvironmentVariable("USERPROFILE", oldProfile);
                fs.WorkingDirectory = project;
            }

            // The command that runs is the one the hooks judged: a launch.json rewritten after the check (here by a
            // hook itself) is not started.
            string Cfg(string word) => "{\"version\":\"0.0.1\",\"configurations\":[{\"name\":\"swap\",\"runtimeExecutable\":\"" + Exe.Replace("\\", "\\\\")
                + "\",\"runtimeArgs\":[\"--say\",\"" + word + "\",\"--sleep\"]}]}";
            Directory.CreateDirectory(Path.Combine(project, ".claude"));
            string launch = Path.Combine(project, ".claude", "launch.json");
            string evil = Path.Combine(exeDir, "swap-evil.json");
            File.WriteAllText(launch, Cfg("APPROVED-CMD"));
            File.WriteAllText(evil, Cfg("SWAPPED-CMD"));
            SetHooks(hooks, new List<HookDefinition>
            {
                new() { Phase = "PreToolUse", Matcher = HookBundleService.ShellCommandTools,
                        Command = $"copy /y \"{evil}\" \"{launch}\" >nul & exit 0", TimeoutMs = 15000 },
            });
            int running = Bg.RunningCount;
            ToolResult swapped;
            try { swapped = await T("bg_start", ("config", "swap"), ("timeout_ms", "1500")); }
            finally { SetHooks(hooks, new List<HookDefinition>()); }
            bool swappedOnDisk = File.ReadAllText(launch).Contains("SWAPPED-CMD");
            File.WriteAllText(launch, Cfg("APPROVED-CMD"));
            var approved = await T("bg_start", ("config", "swap"), ("timeout_ms", "1500"), ("ready_pattern", "APPROVED-CMD"));
            Check("R1c", "a launch.json changed between the check and the start (a hook rewrote it) is refused, nothing started; control: unchanged, the same config starts",
                swappedOnDisk && !swapped.Success && Seen(swapped).Contains("changed after it was checked") && Bg.RunningCount == running + (approved.Success ? 1 : 0)
                && approved.Success && Seen(approved).Contains("bg#"), $"on disk swapped={swappedOnDisk}\n{Seen(swapped)}\n--\n{Seen(approved)}");
            if (IdOf(approved) > 0) await Bg.StopAsync(IdOf(approved), TimeSpan.FromSeconds(3));
            Directory.Delete(Path.Combine(project, ".claude"), true);
        });

        Console.WriteLine("\n── R2 Start-Process is left to detach by itself ──");
        await Case("R2", async () =>
        {
            string pidFile = Path.Combine(exeDir, "start-process.pid");
            try { File.Delete(pidFile); } catch { }
            int entries = Bg.Count;
            var sw = Stopwatch.StartNew();
            var r = await T("powershell", ("command", $"Start-Process -WindowStyle Hidden -FilePath '{Exe}' -ArgumentList '--pidfile','{pidFile}'"));
            long ms = sw.ElapsedMilliseconds;
            bool wrote = await WaitUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0, 10000);
            StartProcessPid = wrote && int.TryParse(File.ReadAllText(pidFile), out var spPid) ? spPid : -1;
            bool inAnyJob = Bg.List().Any(p => Bg.ProcessIdsOf(p.Id).Contains(StartProcessPid));
            Check("R2", "powershell 'Start-Process …' runs as written and returns (it detaches by itself): no bg entry, the program is in no CluadeX job",
                r.Success && ms < 15000 && Bg.Count == entries && StartProcessPid > 0 && Alive(StartProcessPid) && !inAnyJob,
                $"{ms} ms, entries {entries}→{Bg.Count}, pid {StartProcessPid} alive={StartProcessPid > 0 && Alive(StartProcessPid)} inJob={inAnyJob}\n{Seen(r)}");
            // (R2b — it outlives App.RunShutdownHooks — is checked in B9.)
        });

        Console.WriteLine("\n── R3 a one-shot program handed to the background keeps ALL its output ──");
        await Case("R3", async () =>
        {
            var viaRun = await T("run_command", ("command", $"{Self} --lines 60 &"));
            string s = Seen(viaRun);
            bool hasRow1 = System.Text.RegularExpressions.Regex.IsMatch(s, @"(?m)^row1\r?$");
            Check("R3", "run_command of a backgrounded command that turns out to be a one-shot returns its WHOLE output (row1 … row60), not the last 20 lines",
                viaRun.Success && s.Contains("ran to completion") && hasRow1 && s.Contains("row60") && s.Contains("row30"), s);
            var viaBg = await T("bg_start", ("command", $"{Self} --lines 60"));
            string b = Seen(viaBg);
            int id = IdOf(viaBg);
            Check("R3b", "control: bg_start of the same shows the last lines — and now says how to read all of it (bg_output id=N since=start)",
                viaBg.Success && !System.Text.RegularExpressions.Regex.IsMatch(b, @"(?m)^row1\r?$") && b.Contains("row60")
                && b.Contains($"bg_output id={id} since=start"), b);
            var all = await T("bg_output", ("id", id.ToString()), ("since", "start"), ("lines", "2000"));
            Check("R3c", "…and that hint works: bg_output since=start lines=2000 has row1", System.Text.RegularExpressions.Regex.IsMatch(Seen(all), @"(?m)^row1\r?$"), Trunc(Seen(all), 300));
        });

        Console.WriteLine("\n── R4 npm scripts: judged by what they run; a name-only guess never blocks a locked user ──");
        await Case("R4", async () =>
        {
            string pkg = Path.Combine(project, "package.json");
            int devPort = FreePort();
            File.WriteAllText(pkg, "{\"name\":\"r4\",\"private\":true,\"scripts\":{" +
                "\"start\":\"\\\"" + Exe.Replace("\\", "\\\\") + "\\\" --lines 60\"," +
                "\"test\":\"vitest\",\"test:watch\":\"vitest --watch\"," +
                "\"serve\":\"vite --port " + devPort + "\"}}");
            try
            {
                var start = LongRunningCommand.Classify("npm start", ShellRunner.Shell.Bash, project);
                var test = LongRunningCommand.Classify("npm test", ShellRunner.Shell.Bash, project);
                var testWatch = LongRunningCommand.Classify("npm run test:watch", ShellRunner.Shell.Bash, project);
                var serve = LongRunningCommand.Classify("npm run serve", ShellRunner.Shell.Bash, project);
                Check("R4", "package.json scripts: 'npm test' → vitest runs once (None), 'npm run test:watch' → vitest --watch (Watcher), " +
                            "'npm run serve' → vite (Server, port from the script, not a guess); 'npm start' running an unknown program is only a name GUESS",
                    test.Kind == LongRunningCommand.CommandKind.None && testWatch.Kind == LongRunningCommand.CommandKind.Watcher
                    && serve.Kind == LongRunningCommand.CommandKind.Server && serve.Port == devPort && !serve.Guessed
                    && start.Kind == LongRunningCommand.CommandKind.Server && start.Guessed,
                    $"start={start.Kind}/{start.Guessed} test={test.Kind} test:watch={testWatch.Kind} serve={serve.Kind}:{serve.Port}/{serve.Guessed}");

                var lrc = Provider.GetServices<IShellCommandInterceptor>().OfType<LongRunningCommand>().Single();
                ShellCommandContext Ctx(string cmd) => new()
                {
                    Call = new ToolCall { Type = ToolType.RunCommand, ToolName = "run_command" }, Command = cmd, Shell = ShellRunner.Shell.Bash,
                    ProjectDirectory = project, WorkingDirectory = project, TimeoutMs = 120_000,
                };
                SetActivated(false);
                ToolResult? guessLocked, realLocked;
                try
                {
                    guessLocked = await lrc.BeforeRunAsync(Ctx("npm start"), CancellationToken.None);
                    realLocked = await lrc.BeforeRunAsync(Ctx("python -m http.server 8126"), CancellationToken.None);
                }
                finally { SetActivated(true); }
                Check("R4b", "Dev Preview locked: 'npm start' (a guess) is NOT refused — it runs as it always did; control: a recognised server still fails fast with the license reason",
                    guessLocked == null && realLocked != null && !realLocked.Success && Seen(realLocked).Contains("Dev Preview"),
                    $"guess → {(guessLocked == null ? "runs normally" : Seen(guessLocked))}\nreal → {(realLocked == null ? "(null)" : Seen(realLocked))}");

                if (CommandWorks("npm", "--version"))
                {
                    var activated = await T("run_command", ("command", "npm start"));
                    SetActivated(false);
                    ToolResult locked;
                    try { locked = await T("run_command", ("command", "npm start")); }
                    finally { SetActivated(true); }
                    bool Row1(ToolResult x) => System.Text.RegularExpressions.Regex.IsMatch(Seen(x), @"(?m)^row1\r?$");
                    Check("R4c", "'npm start' of a one-shot script, end to end: activated → finished in the background with ALL 60 rows; locked → runs normally with all 60 rows (no 'dev server' refusal)",
                        activated.Success && Row1(activated) && Seen(activated).Contains("row60")
                        && locked.Success && Row1(locked) && Seen(locked).Contains("row60") && !Seen(locked).Contains("dev server"),
                        $"activated:\n{Trunc(Seen(activated), 300)}\nlocked:\n{Trunc(Seen(locked), 300)}");
                }
                else GapNote("R4c", "npm is not installed — 'npm start' end to end not run");
            }
            finally { try { File.Delete(pkg); } catch { } }
        });

        Console.WriteLine("\n── R5 Thai from cmd.exe (Task Manager page tasks) ──");
        await Case("R5", async () =>
        {
            var page = new TaskManagerService(fs);
            try
            {
                int echo = page.CreateTask("thai echo", "echo สวัสดีชาวโลก");
                int prog = page.CreateTask("thai program", $"\"{Exe}\" --say ไทยจากโปรแกรม");
                await WaitUntil(() => page.Background.Get(echo)?.IsRunning == false && page.Background.Get(prog)?.IsRunning == false, 10000);
                var echoLines = page.Background.Read(echo, 50, fromStart: true, reader: "r5").Lines;
                var progLines = page.Background.Read(prog, 50, fromStart: true, reader: "r5").Lines;
                Check("R5", "a page task (cmd.exe) echoing Thai keeps it intact — and a program writing UTF-8 Thai under cmd too",
                    echoLines.Any(l => l.Contains("สวัสดีชาวโลก")) && progLines.Any(l => l.Contains("ไทยจากโปรแกรม")),
                    string.Join(" | ", echoLines) + " ‖ " + string.Join(" | ", progLines));
                byte[] raw = RawCmdBytes("chcp 65001 >nul & echo สวัสดีชาวโลก");
                if (IsValidUtf8(raw) && !Encoding.UTF8.GetString(raw).Contains("สวัสดี"))
                    GapNote("R5b", "cmd wrote neither UTF-8 Thai nor OEM bytes here — the control could not be made");
                else if (IsValidUtf8(raw))
                    GapNote("R5b", "this PC's console code page is UTF-8 already — cmd's echo is UTF-8, the OEM fallback is not exercised");
                else
                    Check("R5b", "control: what cmd's own echo really writes after 'chcp 65001 &' on the same line is NOT UTF-8 (the OEM code page) — so the per-line fallback is what saved it",
                        true, BitConverter.ToString(raw.Take(12).ToArray()));
            }
            finally { page.Background.OnAppShutdown(); }
        });

        Console.WriteLine("\n── R6 a lapsed license still lets the agent clean up ──");
        await Case("R6", async () =>
        {
            var r = await T("bg_start", ("command", $"{Self} --sleep --tag lapse"), ("timeout_ms", "1000"));
            int id = IdOf(r);
            SetActivated(false);
            try
            {
                var names = Svc.BuildNativeToolSchemas().Select(s => s.Name).ToList();
                var list = await T("bg_list");
                var outp = await T("bg_output", ("id", id.ToString()), ("since", "start"));
                var startAgain = await T("bg_start", ("command", $"{Self} --sleep --tag lapse2"), ("timeout_ms", "1000"));
                var serve = await T("bg_serve", ("path", "sub"));
                var stop = await T("bg_stop", ("id", id.ToString()));
                Check("R6", "trial lapsed while a process runs: bg_list / bg_output / bg_stop are still offered and work — it is stopped",
                    id > 0 && names.Contains("bg_stop") && names.Contains("bg_list") && names.Contains("bg_output")
                    && list.Success && Seen(list).Contains($"bg#{id} ") && outp.Success && stop.Success && Bg.Get(id)?.IsRunning == false,
                    $"offered: {string.Join(",", names.Where(n => n.StartsWith("bg_")))}\nlist: {Trunc(Seen(list), 200)}\nstop: {Trunc(Seen(stop), 200)}");
                Check("R6b", "control: starting is still gated — bg_start / bg_serve are neither offered nor run (the license reason)",
                    !names.Contains("bg_start") && !names.Contains("bg_serve") && !startAgain.Success && Seen(startAgain).Contains("trial")
                    && !serve.Success && Seen(serve).Contains("trial"), Trunc(Seen(startAgain), 200));
            }
            finally { SetActivated(true); }
        });

        Console.WriteLine("\n── R7 bg_start validates port= like bg_serve ──");
        await Case("R7", async () =>
        {
            int entries = Bg.Count;
            var bad = await T("bg_start", ("command", $"{Self} --sleep --tag badport"), ("port", "99999"));
            var badServe = await T("bg_serve", ("path", "."), ("port", "99999"));
            Check("R7", "bg_start port=99999 is refused naming the value (nothing started); control: bg_serve says the same",
                !bad.Success && Seen(bad).Contains("\"99999\" is not a port number") && Bg.Count == entries
                && !badServe.Success && Seen(badServe).Contains("\"99999\" is not a port number"), Seen(bad) + "\n" + Seen(badServe));
        });

        Console.WriteLine("\n── R8 per-process logs survive the next app run ──");
        await Case("R8", async () =>
        {
            string logDir = Path.Combine(exeDir, "log-runs");
            ForceDelete(logDir);
            var first = new BackgroundProcessService(null, null, logDir);
            var a = await first.StartAsync(new BgStartRequest { Command = $"{Self} --say FIRST-RUN", Cwd = project, ReadyTimeout = TimeSpan.FromSeconds(5) });
            await WaitUntil(() => first.Get(a.Process!.Id)?.IsRunning == false, 5000);
            string pathA = first.Get(a.Process!.Id)!.LogPath;
            first.OnAppShutdown();
            var second = new BackgroundProcessService(null, null, logDir);   // the next launch: ids start at 1 again
            var b = await second.StartAsync(new BgStartRequest { Command = $"{Self} --say SECOND-RUN", Cwd = project, ReadyTimeout = TimeSpan.FromSeconds(5) });
            await WaitUntil(() => second.Get(b.Process!.Id)?.IsRunning == false, 5000);
            string pathB = second.Get(b.Process!.Id)!.LogPath;
            second.OnAppShutdown();
            string textA = File.Exists(pathA) ? File.ReadAllText(pathA) : "", textB = File.Exists(pathB) ? File.ReadAllText(pathB) : "";
            Check("R8", "the next run's bg#1 writes its own log: the previous run's bg#1 log is kept (FIRST-RUN still there)",
                a.Process.Id == 1 && b.Process!.Id == 1 && !string.Equals(pathA, pathB, StringComparison.OrdinalIgnoreCase)
                && textA.Contains("FIRST-RUN") && textB.Contains("SECOND-RUN") && !textB.Contains("FIRST-RUN"),
                $"{Path.GetFileName(pathA)} / {Path.GetFileName(pathB)}");
            Check("R8b", "log names carry the run stamp: <yyyyMMdd-HHmmss>-<id>[-pid[-guid]].log",
                System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(pathA), @"^\d{8}-\d{6}-1(-\d+(-[0-9a-f]{32})?)?\.log$"), Path.GetFileName(pathA));
        });

        Console.WriteLine("\n── R9 launch.json \"url\" (Claude Code's schema) ──");
        await Case("R9", async () =>
        {
            int pUrl = FreePort(), pPlain = FreePort(), pExt = FreePort(), pUrlOnly = FreePort();
            string Entry(string name, int port, string? url, bool withPort = true) =>
                "{\"name\":\"" + name + "\",\"runtimeExecutable\":\"" + Exe.Replace("\\", "\\\\") + "\",\"runtimeArgs\":[\"--serve\",\"" + port + "\",\"--quiet\"]"
                + (withPort ? ",\"port\":" + port : "") + (url != null ? ",\"url\":\"" + url + "\"" : "") + "}";
            Directory.CreateDirectory(Path.Combine(project, ".claude"));
            File.WriteAllText(Path.Combine(project, ".claude", "launch.json"), "{\"version\":\"0.0.1\",\"configurations\":[" + string.Join(",",
                Entry("with-url", pUrl, $"http://localhost:{pUrl}/game/index.html"),
                Entry("no-url", pPlain, null),
                Entry("ext-url", pExt, "https://example.com/"),
                Entry("url-only", pUrlOnly, $"http://127.0.0.1:{pUrlOnly}/play/", withPort: false)) + "]}");
            var ids = new List<int>();
            try
            {
                async Task<string?> UrlOf(string config)
                {
                    var r = await T("bg_start", ("config", config));
                    ids.Add(IdOf(r));
                    return IdOf(r) > 0 ? Bg.Get(IdOf(r))?.Url : null;
                }
                string? withUrl = await UrlOf("with-url"), urlOnly = await UrlOf("url-only"), plain = await UrlOf("no-url"), ext = await UrlOf("ext-url");
                Check("R9", "a server that prints no URL reports the configured \"url\" (with its path) once its port answers; a url without port= gives the port to probe",
                    withUrl == $"http://localhost:{pUrl}/game/index.html" && urlOnly == $"http://127.0.0.1:{pUrlOnly}/play/", $"{withUrl} | {urlOnly}");
                Check("R9b", "control: without a url it is http://127.0.0.1:PORT/; a url that is not this computer is ignored",
                    plain == $"http://127.0.0.1:{pPlain}/" && ext == $"http://127.0.0.1:{pExt}/", $"{plain} | {ext}");
            }
            finally
            {
                foreach (var id in ids.Where(i => i > 0)) await Bg.StopAsync(id, TimeSpan.FromSeconds(3));
                Directory.Delete(Path.Combine(project, ".claude"), true);
            }
        });

        Console.WriteLine("\n── R10 no project open: the interceptor stays out of it ──");
        await Case("R10", async () =>
        {
            var lrc = Provider.GetServices<IShellCommandInterceptor>().OfType<LongRunningCommand>().Single();
            var call = new ToolCall { Type = ToolType.RunCommand, ToolName = "run_command" };
            var none = await lrc.BeforeRunAsync(new ShellCommandContext { Call = call, Command = "npx tsc -w", Shell = ShellRunner.Shell.Bash, TimeoutMs = 120_000 }, CancellationToken.None);
            var with = await lrc.BeforeRunAsync(new ShellCommandContext { Call = call, Command = "npx tsc -w", Shell = ShellRunner.Shell.Bash,
                ProjectDirectory = project, WorkingDirectory = project, TimeoutMs = 120_000 }, CancellationToken.None);
            int running = Bg.RunningCount;
            fs.WorkingDirectory = "";
            ToolResult viaRun;
            try { viaRun = await T("run_command", ("command", "python -m http.server 8127")); }
            finally { fs.WorkingDirectory = project; }
            Check("R10", "no project: the interceptor answers nothing and run_command's own 'No project folder is open' does (no server starts in the home folder)",
                none == null && !viaRun.Success && Seen(viaRun).Contains("No project folder is open") && Bg.RunningCount == running, Seen(viaRun));
            Check("R10b", "control: with a project the same watcher is refused by the interceptor", with != null && !with.Success && Seen(with).Contains("tsc --noEmit"),
                with == null ? "(null)" : Seen(with));
        });

        Console.WriteLine("\n── R11 the process-tree walk never adopts a process through a reused parent id ──");
        await Case("R11", () =>
        {
            var pt = typeof(BackgroundProcessService).Assembly.GetType("CluadeX.Services.Background.ProcessTree")!;
            var walk = pt.GetMethod("Descendants", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(IReadOnlyCollection<int>), typeof(DateTime), typeof(IReadOnlyDictionary<int, int>), typeof(Func<int, DateTime?>) }, null)!;
            var t0 = new DateTime(2026, 10, 10, 12, 0, 0);
            // 100 = a process in the job. 200 claims 100 as its parent but is OLDER: its real parent died and 100 got
            // that id later. 300 → 400 are real descendants. 500 claims 300 but started before it. 600's parent is not ours.
            var parents = new Dictionary<int, int> { [200] = 100, [210] = 200, [300] = 100, [400] = 300, [500] = 300, [600] = 999 };
            var starts = new Dictionary<int, DateTime>
            {
                [100] = t0, [200] = t0.AddSeconds(-30), [210] = t0.AddSeconds(5), [300] = t0.AddSeconds(1), [400] = t0.AddSeconds(2),
                [500] = t0.AddSeconds(0.5), [600] = t0.AddSeconds(3),
            };
            Func<int, DateTime?> startOf = p => starts.TryGetValue(p, out var t) ? t : null;
            var notBefore = t0.AddMinutes(-5);
            var got = ((List<int>)walk.Invoke(null, new object[] { new[] { 100 }, notBefore, parents, startOf })!).OrderBy(x => x).ToList();
            Check("R11", "a 'child' older than the parent it is reached through (a reused id) — and everything under it — is never pulled into the job",
                got.SequenceEqual(new[] { 300, 400 }), string.Join(",", got));
            Check("R11b", "control: by the old rule (started after the bg start, 5 min ago) pid 200 would have qualified",
                starts[200] >= notBefore.AddSeconds(-2), starts[200].ToString("HH:mm:ss"));
            return Task.CompletedTask;
        });

        Console.WriteLine("\n── R12 app exit landing in the middle of a start ──");
        await Case("R12", async () =>
        {
            var race = new BackgroundProcessService(null, null, null);
            bool fired = false;
            // AddItem runs between the registration and the launch (synchronously without a WPF dispatcher): exit there.
            race.Items.CollectionChanged += (_, e) =>
            {
                if (fired || e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) return;
                fired = true;
                race.OnAppShutdown();
            };
            var rr = await race.StartAsync(new BgStartRequest { Command = $"{Self} --sleep --tag race", Cwd = project, ReadyTimeout = TimeSpan.FromSeconds(3) });
            int? pid = rr.Process?.Pid;
            bool dead = pid != null && await WaitUntil(() => !Alive(pid.Value), 3000);
            Check("R12", "the process launched after app exit began is ended at once and the start reports that CluadeX is closing",
                fired && dead && rr.Status == BgStartStatus.Refused && rr.Message.Contains("closing") && race.Get(rr.Process!.Id)?.IsRunning == false,
                $"fired={fired} pid={pid} dead={dead} status={rr.Status} msg={rr.Message}");
            Check("R12b", "control: the race really happened — a process was launched after the exit flag was set (it has a pid)", fired && pid != null);
            if (pid != null && Alive(pid.Value)) { try { using var x = Process.GetProcessById(pid.Value); x.Kill(true); } catch { } }
        });

        Console.WriteLine("\n── R13 a stopped static server is disposed ──");
        await Case("R13", async () =>
        {
            var st = await T("bg_serve", ("path", "sub"));
            int sid = IdOf(st);
            var server = ServerOf(Bg, sid);
            bool liveBefore = server != null && server.IsRunning && !server.IsDisposed;
            await T("bg_stop", ("id", sid.ToString()));
            Check("R13", "bg_stop disposes the static server (listener, connections, CTS and semaphore released), not just stops it",
                server != null && server.IsDisposed && !server.IsRunning, $"server={(server != null)} disposed={server?.IsDisposed}");
            Check("R13b", "control: before the stop it was running and not disposed", liveBefore);
        });

        Console.WriteLine("\n── R14 static server: header injection, Content-Length, locked files, 8.3 names ──");
        await Case("R14", async () =>
        {
            string site = Path.Combine(exeDir, "site");
            ForceDelete(site);
            Directory.CreateDirectory(Path.Combine(site, "sub"));
            File.WriteAllText(Path.Combine(site, "index.html"), "<p>home</p>");
            File.WriteAllText(Path.Combine(site, "sub", "page.html"), "<p>sub</p>");
            File.WriteAllText(Path.Combine(site, "locked.txt"), "locked");
            File.WriteAllText(Path.Combine(site, "v~2.txt"), "TILDE-OK");
            string big = Path.Combine(site, "big.bin");
            const int Mb8 = 8 * 1024 * 1024;
            File.WriteAllBytes(big, new byte[Mb8]);
            var st = Bg.StartStatic(site, null, false);
            int sid = st.Process!.Id, port = st.Process.Port ?? 0;
            try
            {
                var inj = SplitResponse((await RawExchange(port, Encoding.Latin1.GetBytes(
                    $"GET /sub?a\nX-Injected:yes HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n"))).All);
                var redirect = await RawHttp(port, "GET", "/sub?a=1");
                Check("R14", "a control character anywhere in the request target (a bare LF in the query) gets 400 — no injected header line",
                    inj.Status == 400 && !inj.Head.Contains("X-Injected"), inj.Head.Replace("\r\n", " ⏎ "));
                Check("R14b", "control: a normal query still gets its 301 with Location /sub/?a=1",
                    redirect.Status == 301 && redirect.Headers.GetValueOrDefault("Location") == "/sub/?a=1", $"{redirect.Status} {redirect.Headers.GetValueOrDefault("Location")}");

                bool grown = false;
                var grow = await RawExchange(port, Encoding.ASCII.GetBytes($"GET /big.bin HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n"),
                    async received =>
                    {
                        if (grown || received < 65536) return;
                        grown = true;
                        using var f = new FileStream(big, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        await f.WriteAsync(new byte[1024 * 1024]);
                    }, readDelayMs: 2);
                var g = SplitResponse(grow.All);
                long declared = long.TryParse(System.Text.RegularExpressions.Regex.Match(g.Head, @"Content-Length: (\d+)").Groups[1].Value, out var cl) ? cl : -1;
                Check("R14c", "a file that grows while it is sent: exactly Content-Length bytes go out (keep-alive framing intact)",
                    g.Status == 200 && declared == Mb8 && g.Body.Length == Mb8, $"declared {declared}, body {g.Body.Length}");
                Check("R14d", "control: the file really grew during the transfer", grown && new FileInfo(big).Length > Mb8, $"now {new FileInfo(big).Length}");

                File.WriteAllBytes(big, new byte[Mb8]);
                bool shrunk = false;
                var sw = new Stopwatch();
                var shrink = await RawExchange(port, Encoding.ASCII.GetBytes($"GET /big.bin HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n"),   // keep-alive
                    received =>
                    {
                        if (shrunk || received < 65536) return Task.CompletedTask;
                        shrunk = true;
                        using var f = new FileStream(big, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        f.SetLength(1024);
                        sw.Start();
                        return Task.CompletedTask;
                    }, readDelayMs: 2, timeoutMs: 12000);
                long afterShrinkMs = sw.ElapsedMilliseconds;
                var sb = SplitResponse(shrink.All);
                var serverLog = Bg.Read(sid, 400, fromStart: true, reader: "r14").Lines;
                Check("R14e", "a file that shrinks while it is sent (keep-alive): the server closes the connection at once instead of leaving the browser waiting for bytes that never come",
                    shrunk && shrink.ClosedAtMs >= 0 && afterShrinkMs < 3000 && sb.Body.Length < Mb8 && serverLog.Any(l => l.Contains("shrank while it was sent")),
                    $"closed after {afterShrinkMs} ms (-1 = never), body {sb.Body.Length}");

                (int Status, Dictionary<string, string> Headers, byte[] Body) lk;
                using (new FileStream(Path.Combine(site, "locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    lk = await RawHttp(port, "GET", "/locked.txt");
                var released = await RawHttp(port, "GET", "/locked.txt");
                serverLog = Bg.Read(sid, 400, fromStart: true, reader: "r14").Lines;
                Check("R14f", "a file held open without sharing (an editor mid-save) gets 503 + Retry-After and 'in use by another program' in bg_output — not 404",
                    lk.Status == 503 && lk.Headers.ContainsKey("Retry-After") && serverLog.Any(l => l.Contains("503 GET /locked.txt (in use by another program)")),
                    $"{lk.Status} retry={lk.Headers.GetValueOrDefault("Retry-After")}");
                Check("R14g", "control: released, the same file is served (200)", released.Status == 200 && Encoding.UTF8.GetString(released.Body) == "locked", released.Status.ToString());

                var tilde = await RawHttp(port, "GET", "/v~2.txt");
                if (Directory.Exists(@"C:\PROGRA~1") && Directory.Exists(@"C:\Program Files"))
                {
                    var (cServer, _, _) = StaticFileServer.TryStart(@"C:\", null, false, _ => { });
                    try
                    {
                        var shortName = cServer == null ? default : await RawHttp(cServer.Port, "GET", "/PROGRA~1/");
                        var longName = cServer == null ? default : await RawHttp(cServer.Port, "GET", "/Program%20Files/");
                        Check("R14h", "an 8.3 short name (/PROGRA~1/ = Program Files) is refused, so /GIT~1/config or /ENV~1 cannot slip past the .git/.env filter",
                            cServer != null && shortName.Status == 403, $"short {shortName.Status}");
                        Check("R14i", "control: the long name is served (200), and a real file named v~2.txt still is",
                            longName.Status == 200 && tilde.Status == 200 && Encoding.UTF8.GetString(tilde.Body) == "TILDE-OK", $"long {longName.Status}, v~2.txt {tilde.Status}");
                    }
                    finally { cServer?.Dispose(); }
                }
                else
                {
                    GapNote("R14h", "C: has no 8.3 short names on this PC — the short-name case was not checked");
                    Check("R14i", "a real file named v~2.txt is still served", tilde.Status == 200, tilde.Status.ToString());
                }
            }
            finally { await Bg.StopAsync(sid, TimeSpan.FromSeconds(3)); }
        });

        Console.WriteLine("\n── R15 finished entries do not keep 512K chars each ──");
        await Case("R15", async () =>
        {
            var reg = new BackgroundProcessService(null, null, null);
            var ids = new List<int>();
            try
            {
                for (int i = 0; i < 6; i++)
                {
                    var x = await reg.StartAsync(new BgStartRequest { Command = $"{Self} --lines 3000 --tag t{i}", Cwd = project, ReadyTimeout = TimeSpan.FromSeconds(15) });
                    ids.Add(x.Process!.Id);
                    await WaitUntil(() => reg.Get(x.Process.Id)?.IsRunning == false, 15000);
                }
                await Task.Delay(300);   // the page's output flush (every 100 ms)
                reg.FlushPendingOutput();
                var oldest = reg.Read(ids[0], 2000, fromStart: true, reader: "r15");
                var newest = reg.Read(ids[^1], 2000, fromStart: true, reader: "r15");
                long oldestChars = oldest.Lines.Sum(l => l.Length + 1);
                string oldestPane = reg.Items.First(t => t.Id == ids[0]).Output;
                Check("R15", "older finished entries keep only their last 16K chars in memory and in the page's pane (the log keeps the rest)",
                    oldestChars <= BackgroundProcessService.FinishedKeepChars + 64 && oldest.DroppedFromMemory > 0
                    && oldestPane.Length <= BackgroundProcessService.FinishedKeepChars + 16 && oldest.Lines.Last().Contains("exited"),
                    $"oldest {oldestChars} chars in memory, {oldest.DroppedFromMemory} dropped, pane {oldestPane.Length}");
                Check("R15b", "control: the newest finished entries keep everything (3000 rows + the exit line)",
                    newest.Lines.Count + newest.Omitted == 3001 && newest.DroppedFromMemory == 0, $"{newest.Lines.Count}+{newest.Omitted}, dropped {newest.DroppedFromMemory}");
            }
            finally { reg.OnAppShutdown(); }
        });

        Console.WriteLine("\n── R16 bg_output keeps one cursor per chat ──");
        await Case("R16", async () =>
        {
            var r = await T("bg_start", ("command", $"{Self} --say chat-line-1 --say chat-line-2 --sleep"), ("ready_pattern", "chat-line-2"));
            int id = IdOf(r);
            string keep = Svc.SessionKey;
            try
            {
                Svc.SessionKey = "chat-A";
                var a1 = await T("bg_output", ("id", id.ToString()));
                var a2 = await T("bg_output", ("id", id.ToString()));
                Svc.SessionKey = "chat-B";
                var b1 = await T("bg_output", ("id", id.ToString()));
                Check("R16", "reading in chat A does not mark the lines as seen in chat B", Seen(a1).Contains("chat-line-1") && Seen(b1).Contains("chat-line-1"), Seen(b1));
                Check("R16b", "control: within chat A a second read has nothing new", Seen(a2).Contains("no new output"), Seen(a2));
            }
            finally
            {
                Svc.SessionKey = keep;
                await Bg.StopAsync(id, TimeSpan.FromSeconds(3));
            }
        });

        Console.WriteLine("\n── R17 Stop All while a stop is in flight ──");
        await Case("R17", async () =>
        {
            var page = new TaskManagerService(fs);
            try
            {
                int a = page.CreateTask("in flight", $"\"{Exe}\" --sleep");
                var vm = new TaskManagerViewModel(page);
                int asked = 0;
                vm.Confirm = (_, _) => { asked++; return true; };
                var first = vm.StopAllAsync();
                bool disabledDuring = vm.IsStopping && !vm.StopAllCommand.CanExecute(null);
                var second = vm.StopAllAsync();
                bool secondReturnedAtOnce = second.IsCompleted;
                await Task.WhenAll(first, second);
                Check("R17", "a second 'Stop all' while the first is still stopping: the button is disabled, it neither asks again nor stops twice",
                    first.IsCompletedSuccessfully && disabledDuring && secondReturnedAtOnce && asked == 1,
                    $"inFlight={!first.IsCompleted} disabled={disabledDuring} secondAtOnce={secondReturnedAtOnce} asked={asked}");
                Check("R17b", "control: when it is done the button is enabled again and the task is stopped",
                    !vm.IsStopping && vm.StopAllCommand.CanExecute(null) && page.Background.Get(a)?.IsRunning == false, $"status={page.Background.Get(a)?.Status}");
            }
            finally { page.Background.OnAppShutdown(); }
        });

        // ═══════════════════ B10 Task Manager page ═══════════════════
        Console.WriteLine("\n── B10 Task Manager page (facade + view model) ──");
        await Case("B10", async () =>
        {
            var page = new TaskManagerService(fs);   // a standalone registry: B9 below shuts the app's down
            int a = page.CreateTask("sleeper a", $"\"{Exe}\" --sleep");
            int b = page.CreateTask("sleeper b", $"\"{Exe}\" --sleep");
            var vm = new TaskManagerViewModel(page);
            int asked = 0;
            vm.Confirm = (text, title) => { asked++; return false; };
            await vm.StopAllAsync();
            await Task.Delay(300);
            Check("B10", "the page starts what it is asked to (the same command twice = two tasks); 'Stop all' asks first and does nothing when declined",
                a > 0 && b > 0 && a != b && asked == 1 && page.Background.Get(a)?.IsRunning == true && page.Background.Get(b)?.IsRunning == true,
                $"a={a} b={b} asked={asked}");
            string lastAsk = "";
            vm.Confirm = (text, title) => { asked++; lastAsk = text; return true; };
            await vm.StopAllAsync();
            Check("B10b", "…and stops every running task (whole trees) when confirmed, naming how many",
                asked == 2 && lastAsk.Contains("2") && page.Background.Get(a)?.IsRunning == false && page.Background.Get(b)?.IsRunning == false && vm.StatusText.Contains("2"),
                $"asked={asked} ask='{lastAsk}' status='{vm.StatusText}' a={page.Background.Get(a)?.Status} b={page.Background.Get(b)?.Status}");
            await vm.StopAllAsync();
            Check("B10c", "with nothing running it says so without asking", asked == 2 && vm.StatusText.Length > 0, $"asked={asked} status='{vm.StatusText}'");
            var row = page.Tasks.FirstOrDefault(t => t.Id == a);
            Check("B10d", "rows carry kind, cwd, status, exit code and pid for the page's columns",
                row != null && row.Kind == "shell" && row.Cwd == project && row.Status == "stopped" && row.Pid != null && row.ExitCodeText.Length > 0,
                $"{row?.Kind} {row?.Cwd} {row?.Status} {row?.Pid} {row?.ExitCodeText}");
            string? opened = null;
            vm.OpenInBrowser = u => opened = u;
            vm.OpenUrlCommand.Execute(new TaskInfo { Url = "http://127.0.0.1:8100/" });
            vm.OpenUrlCommand.Execute(new TaskInfo { Url = "https://attacker.example/" });
            Check("B10e", "the URL column opens only loopback URLs", opened == "http://127.0.0.1:8100/", opened ?? "(null)");
            page.Background.OnAppShutdown();
        });

        // ═══════════════════ B9 exit ═══════════════════
        Console.WriteLine("\n── B9 app exit ──");
        await Case("B9", async () =>
        {
            int p1 = FreePort(), p2 = FreePort();
            var r1 = await T("bg_start", ("command", $"{Self} --serve {p1}"));
            var r2 = await T("bg_start", ("command", $"{Self} --spawn-orphan {p2}"), ("port", p2.ToString()));
            var pids = Bg.ProcessIdsOf(IdOf(r1)).Concat(Bg.ProcessIdsOf(IdOf(r2))).ToList();
            bool staticUp = ReadinessProbe.IsListening(servePort);
            Check("B9", "before exit: two servers (one behind an orphaning spawner) and the static server are up",
                pids.Count >= 4 && pids.All(Alive) && staticUp && ReadinessProbe.IsListening(p1) && ReadinessProbe.IsListening(p2), string.Join(",", pids));
            var run = typeof(CluadeX.App).GetMethod("RunShutdownHooks", BindingFlags.NonPublic | BindingFlags.Static)!;
            var sw = Stopwatch.StartNew();
            run.Invoke(null, new object[] { Provider });
            long hookMs = sw.ElapsedMilliseconds;
            bool gone = await WaitUntil(() => pids.All(p => !Alive(p)) && !ReadinessProbe.IsListening(p1) && !ReadinessProbe.IsListening(p2)
                                              && !ReadinessProbe.IsListening(servePort), 3000);
            Check("B9b", "App.RunShutdownHooks (OnExit) ends every tree and closes the static server, quickly",
                gone && hookMs < 2000, $"hook {hookMs} ms; alive: {string.Join(",", pids.Where(Alive))}");
            if (StartProcessPid > 0)
            {
                // Review P1b: what the agent opened with a plain Start-Process belongs to the user — CluadeX closing
                // must not take it (and the editor's unsaved work) along.
                await Task.Delay(500);
                Check("R2b", "the program the agent opened with Start-Process outlives App.RunShutdownHooks (control: the bg trees above are gone)",
                    Alive(StartProcessPid) && gone, $"pid {StartProcessPid} alive={Alive(StartProcessPid)}");
                try { using var sp = Process.GetProcessById(StartProcessPid); sp.Kill(); } catch { }
            }
            var after = await T("bg_start", ("command", $"{Self} --sleep"));
            Check("B9c", "after shutdown nothing new starts", !after.Success && Seen(after).Contains("closing"), Seen(after));
        });

        // ═══════════════════ leak check ═══════════════════
        await Task.Delay(500);
        var leaked = Process.GetProcessesByName("bgtest").Where(p => p.Id != Environment.ProcessId).ToList();
        Check("Z1", "no harness child process is left running", leaked.Count == 0, string.Join(",", leaked.Select(p => p.Id)));
        foreach (var p in leaked) { try { p.Kill(true); } catch { } }

        Provider.Dispose();
        foreach (var d in new[] { project, outside, Path.Combine(exeDir, "bg-project-2"), Path.Combine(exeDir, "push-repo"),
                                  Path.Combine(exeDir, "fake-profile"), Path.Combine(exeDir, "site"), Path.Combine(exeDir, "log-runs") })
            ForceDelete(d);
        foreach (var f in new[] { "secret.txt", "swap-evil.json", "start-process.pid" })
            try { File.Delete(Path.Combine(exeDir, f)); } catch { }

        Console.WriteLine($"\n═══ {Pass} pass · {Fail} FAIL · {Gap} GAP ═══");
        foreach (var p in Problems) Console.WriteLine("   " + p);
        foreach (var g in Gaps) Console.WriteLine("   " + g);
        return Fail == 0 ? 0 : 1;
    }

    static int CountHoles(string s) => System.Text.RegularExpressions.Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).Distinct().Count();
}

/// <summary>The child processes the harness runs as (no installs needed). Every mode ends itself after 5 minutes, so
/// a bug in the code under test can never leave one running for long.</summary>
internal static class ChildModes
{
    static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public static int Run(string[] args)
    {
        var watchdog = new Thread(() => { Thread.Sleep(Lifetime); Environment.Exit(99); }) { IsBackground = true };
        watchdog.Start();
        string mode = args[0];
        string Arg(string name, string fallback = "") { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        bool Flag(string name) => args.Contains(name);

        // --say "text" (repeatable): print lines first.
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--say") Console.WriteLine(args[i + 1]);

        switch (mode)
        {
            case "--serve": return Serve(int.Parse(args[1]), Flag("--v6only"), int.Parse(Arg("--delay", "0")), Flag("--quiet"));
            case "--sleep": Thread.Sleep(Lifetime); return 0;
            case "--say": if (Flag("--sleep")) Thread.Sleep(Lifetime); return 0;
            case "--lines":
                // A one-shot program with a lot to say: row1 … rowN, then it exits 0.
                for (int i = 1, n = int.Parse(args[1]); i <= n; i++) Console.WriteLine("row" + i);
                return 0;
            case "--pidfile":
                // Write my pid, then live (the Start-Process case: does what it opened outlive CluadeX?).
                File.WriteAllText(args[1], Environment.ProcessId.ToString());
                Thread.Sleep(Lifetime);
                return 0;
            case "--chatter":
                Console.WriteLine("\x1b[32mพร้อมแล้ว — สวัสดีครับ\x1b[0m");
                Console.WriteLine("line 1");
                Console.WriteLine("line 2");
                Console.WriteLine("\x1b[31mERROR: disk on fire (simulated)\x1b[0m");
                Console.WriteLine("READY-CHATTER");
                Thread.Sleep(1500);
                Console.WriteLine("later 1");
                Console.WriteLine("later 2");
                Console.WriteLine("later 3");
                Thread.Sleep(Lifetime);
                return 0;
            case "--spawn-orphan":
            {
                // Spawner → mid → grandchild server; mid exits at once, so the server is an orphan whose parent is gone.
                var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--spawn-mid {args[1]}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using var mid = Process.Start(psi)!;
                string? line = mid.StandardOutput.ReadLine();
                mid.WaitForExit(10000);
                Console.WriteLine(line != null && line.StartsWith("GRANDCHILD_PID=") ? "ORPHAN_PID=" + line["GRANDCHILD_PID=".Length..] : "ORPHAN_PID=-1");
                Thread.Sleep(Lifetime);
                return 0;
            }
            case "--spawn-mid":
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--serve {args[1]} --quiet") { UseShellExecute = false, CreateNoWindow = true };
                var child = Process.Start(psi)!;
                Console.WriteLine("GRANDCHILD_PID=" + child.Id);
                // Leave only once the grandchild listens, so the spawner's port is really served by an orphan.
                int port = int.Parse(args[1]);
                for (int i = 0; i < 100 && !ReadinessProbe.IsListening(port); i++) Thread.Sleep(50);
                return 0;
            }
            case "--host": return Host(args[1], int.Parse(args[2]), int.Parse(args[3])).GetAwaiter().GetResult();
            default: Console.Error.WriteLine("unknown mode " + mode); return 64;
        }
    }

    static int Serve(int port, bool v6only, int delayMs, bool quiet)
    {
        if (delayMs > 0) Thread.Sleep(delayMs);
        var listener = new TcpListener(v6only ? IPAddress.IPv6Loopback : IPAddress.Loopback, port);
        listener.Start();
        if (!quiet) Console.WriteLine($"listening on http://127.0.0.1:{port}/");
        while (true)
        {
            using var c = listener.AcceptTcpClient();
            try
            {
                var s = c.GetStream();
                s.ReadTimeout = 2000;
                var buf = new byte[4096];
                try { s.Read(buf, 0, buf.Length); } catch { }
                var body = "ok"u8.ToArray();
                s.Write(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                s.Write(body);
            }
            catch { }
        }
    }

    /// <summary>A stand-in for CluadeX: its own BackgroundProcessService with two servers, then it waits to be killed.</summary>
    static async Task<int> Host(string logDir, int p1, int p2)
    {
        var bg = new BackgroundProcessService(null, null, logDir);
        string self = "\"" + Environment.ProcessPath!.Replace('\\', '/') + "\"";
        var r1 = await bg.StartAsync(new BgStartRequest { Command = $"{self} --serve {p1} --quiet", Cwd = logDir, Port = p1, PortExplicit = true, ReadyTimeout = TimeSpan.FromSeconds(20) });
        var r2 = await bg.StartAsync(new BgStartRequest { Command = $"{self} --spawn-orphan {p2}", Cwd = logDir, Port = p2, PortExplicit = true, ReadyTimeout = TimeSpan.FromSeconds(20) });
        if (r1.Process == null || r2.Process == null) { Console.WriteLine("HOST_FAILED " + r1.Message + " " + r2.Message); return 1; }
        for (int i = 0; i < 100 && !bg.Read(r2.Process.Id, 50, fromStart: true, reader: "host").Lines.Any(l => l.StartsWith("ORPHAN_PID=")); i++)
            await Task.Delay(50);
        var pids = bg.ProcessIdsOf(r1.Process.Id).Concat(bg.ProcessIdsOf(r2.Process.Id)).Distinct().ToList();
        Console.WriteLine($"HOST_READY pids={string.Join(",", pids)} ports={p1},{p2}");
        await Task.Delay(TimeSpan.FromMinutes(5));
        return 0;
    }
}
