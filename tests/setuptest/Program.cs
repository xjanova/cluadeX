using System.Diagnostics;
using System.IO;
using System.Text;
using CluadeX.Services;
using CluadeX.Services.Setup;

// The setup page ("ตัวช่วยติดตั้ง", owner 2026-10-19). Installs NOTHING: the rows are built for made-up machines, the
// console script is run hidden with harmless commands only, and the real check of this PC is read-only (file checks,
// `--version`, `claude auth status`). portable.txt keeps settings next to this exe, never the user's ~/.cluadex.
static partial class Program
{
    static int pass, fail;
    static void Check(string id, string name, bool ok, string detail = "")
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {id,-4} {name}" + (ok || detail.Length == 0 ? "" : $"\n        {detail}"));
    }

    static SetupItem Row(List<SetupItem> items, string id) => items.Single(i => i.Id == id);

    static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        Console.WriteLine("── rows for a bare PC (nothing installed, no key, no license) ──");
        var bare = SystemSetupService.BuildItems(new SetupFacts());
        Check("B1", "every piece has a row, none ready", bare.Count == 15 && bare.All(i => !i.IsReady), string.Join(",", bare.Select(i => $"{i.Id}:{i.State}")));
        var codexRow = Row(bare, "codexcli");
        Check("B1c", "Codex CLI (OpenAI Dev) without Node: Node first, then npm install -g @openai/codex by npm's full path",
            codexRow.State == SetupState.Missing && codexRow.Primary?.Commands is { Count: 2 } cc && cc[0].Contains("OpenJS.NodeJS.LTS") && cc[1].Contains("@openai/codex"),
            string.Join(" | ", codexRow.Primary?.Commands ?? Array.Empty<string>()));
        Check("B1d", "Codex installed but signed out → Sign in (codex); signed in → ready and the chat can answer",
            Row(SystemSetupService.BuildItems(new SetupFacts { CodexCli = @"C:\x\codex.exe" }), "codexcli") is { State: SetupState.NeedsSignIn, Primary: { Kind: SetupActionKind.SignIn, Target: "codex" } }
            && SystemSetupService.AnyChatModelReady(new SetupFacts { Licensed = true, CodexCli = @"C:\x\codex.exe", CodexSignedIn = true }));
        Check("B2", "the license is required and opens the license window",
            Row(bare, "license") is { Optional: false, State: SetupState.NeedsKey, Primary: { Kind: SetupActionKind.OpenPage, Target: "License" } });
        var key = Row(bare, "anthropic");
        Check("B3", "a missing API key: 'get a key' opens the provider's key page, 'enter the key' opens Settings at that provider",
            key.State == SetupState.NeedsKey && key.Primary is { Kind: SetupActionKind.OpenUrl, Target: "https://platform.claude.com/settings/keys" }
            && key.Secondary is { Kind: SetupActionKind.OpenSettings, Target: "Anthropic" });
        var gem = Row(bare, "geminicli");
        Check("B4", "Gemini CLI without Node: one click installs Node first, then the CLI with npm by its full path",
            gem.Primary?.Commands is { Count: 2 } c && c[0].Contains("OpenJS.NodeJS.LTS") && c[1].Contains(@"nodejs\npm.cmd") && c[1].Contains("@google/gemini-cli"),
            string.Join(" | ", gem.Primary?.Commands ?? Array.Empty<string>()));
        Check("B5", "Claude Code installs from winget (Anthropic.ClaudeCode)", Row(bare, "claudecode").Primary?.Commands?[0] == SystemSetupService.WingetInstall("Anthropic.ClaudeCode"));
        Check("B6", "every winget line names one exact package from the winget source and accepts its agreements (the confirm says so)",
            bare.SelectMany(i => new[] { i.Primary, i.Secondary }).Where(a => a?.Commands != null).SelectMany(a => a!.Commands!)
                .Where(l => l.StartsWith("winget")).All(l => l.Contains(" -e --source winget --accept-package-agreements --accept-source-agreements")));
        Check("B7", "BrainX missing → one-click install, the download page beside it", Row(bare, "brainx") is
            { Primary.Kind: SetupActionKind.InstallBrainX, Secondary: { Kind: SetupActionKind.OpenUrl, Target: "https://xman4289.com/brainx/download" } });
        Check("B8", "every install says roughly how much it downloads",
            bare.Select(i => i.Primary).Where(a => a?.Kind == SetupActionKind.Install).All(a => a!.SizeHint is { Length: > 0 }));
        Check("B9", "nothing can answer the chat on a bare PC", !SystemSetupService.AnyChatModelReady(new SetupFacts()));

        Console.WriteLine("── rows for a half-set-up PC ──");
        var half = new SetupFacts
        {
            Node = @"C:\Program Files\nodejs\node.exe", NodeVersion = "v24.20.0", Npm = @"C:\Program Files\nodejs\npm.cmd",
            ClaudeCli = @"C:\x\claude.exe", ClaudeSignedIn = false,
            GeminiCli = @"C:\x\gemini.js", GeminiSignedIn = false,
            Ollama = @"C:\x\ollama.exe", OllamaRunning = false,
            Python = @"C:\py\python.exe", PythonVersion = "Python 3.12.10", Debugpy = false,
            BrainExe = @"C:\Users\x\.brainx\mcp\brainx-mcp.exe", BrainConnected = false, Licensed = true,
        };
        var items = SystemSetupService.BuildItems(half);
        Check("H1", "installed but signed out → Sign in (claude / gemini)",
            Row(items, "claudecode") is { State: SetupState.NeedsSignIn, Primary: { Kind: SetupActionKind.SignIn, Target: "claude" } }
            && Row(items, "geminicli") is { State: SetupState.NeedsSignIn, Primary: { Kind: SetupActionKind.SignIn, Target: "gemini" } });
        Check("H2", "Ollama installed but not running → Start", Row(items, "ollama") is { State: SetupState.NotRunning, Primary.Kind: SetupActionKind.StartOllama });
        Check("H3", "Python without debugpy → pip install --user debugpy with that python",
            Row(items, "python").Primary?.Commands?[0] == "\"C:\\py\\python.exe\" -m pip install --user debugpy");
        Check("H4", "BrainX installed but not connected → the MCP page", Row(items, "brainx").Primary is { Kind: SetupActionKind.OpenPage, Target: "McpServers" });
        Check("H5", "licensed but every model signed out / no key → still nothing can answer", !SystemSetupService.AnyChatModelReady(half));
        var running = half with { OllamaRunning = true, OllamaModels = 0 };
        var pull = Row(SystemSetupService.BuildItems(running), "ollama");
        Check("H6", "Ollama running with no model → download the starter model (Run, ~2 GB) or browse others",
            pull.State == SetupState.NoModel && pull.Primary?.Kind == SetupActionKind.Run
            && pull.Primary.Commands![0].EndsWith("pull " + SystemSetupService.StarterOllamaModel) && pull.Secondary?.Kind == SetupActionKind.OpenUrl);
        Check("H7", "Ollama with a model answers the chat even without a license (local is free)",
            SystemSetupService.AnyChatModelReady(new SetupFacts { OllamaRunning = true, OllamaModels = 2 }));
        Check("H8", "a key without the license is not a ready chat model (cloud needs the license)",
            !SystemSetupService.AnyChatModelReady(new SetupFacts { AnthropicKey = true })
            && SystemSetupService.AnyChatModelReady(new SetupFacts { AnthropicKey = true, Licensed = true }));

        Console.WriteLine("── a complete PC ──");
        var full = half with
        {
            ClaudeSignedIn = true, GeminiSignedIn = true, CodexCli = @"C:\x\codex.exe", CodexSignedIn = true, OllamaRunning = true, OllamaModels = 3, Debugpy = true, LlamaServer = @"C:\x\llama-server.exe",
            Git = @"C:\git.exe", GitVersion = "2.55", DotnetSdk = "8.0.426", WebView2 = true, WebView2Version = "155", BrainConnected = true,
            AnthropicKey = true, OpenAiKey = true, GeminiKey = true,
        };
        var all = SystemSetupService.BuildItems(full);
        Check("C1", "everything ready", all.All(i => i.IsReady), string.Join(",", all.Where(i => !i.IsReady).Select(i => $"{i.Id}:{i.State}")));
        Check("C2", "a ready row installs nothing (only a key row keeps its Settings button)",
            all.All(i => i.Primary?.Kind is null or SetupActionKind.OpenSettings));

        Console.WriteLine("── finding a tool ──");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Users\x\AppData\Local\Microsoft\WindowsApps\python.exe", @"C:\Python312\python.exe", @"D:\tools\git.exe", @"C:\Program Files\Git\cmd\git.exe",
        };
        string path = @"C:\Users\x\AppData\Local\Microsoft\WindowsApps;C:\Python312;""D:\tools""";
        Check("F1", "the Store's python stub under WindowsApps is skipped", SystemSetupService.FindExe(path, new[] { "python.exe" }, Array.Empty<string>(), files.Contains) == @"C:\Python312\python.exe");
        Check("F2", "PATH order wins over the known place; quotes in PATH are fine",
            SystemSetupService.FindExe(path, new[] { "git.exe" }, new[] { @"C:\Program Files\Git\cmd\git.exe" }, files.Contains) == @"D:\tools\git.exe");
        Check("F3", "not on PATH → the known place", SystemSetupService.FindExe("", new[] { "git.exe" }, new[] { @"C:\Program Files\Git\cmd\git.exe" }, files.Contains) == @"C:\Program Files\Git\cmd\git.exe");
        string fresh = SystemSetupService.FreshPath();
        Check("F4", "the fresh PATH keeps every entry this process already had", (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .All(d => fresh.Split(';').Contains(d.Trim(), StringComparer.OrdinalIgnoreCase)));

        Console.WriteLine("── the console script (run hidden, harmless commands) ──");
        var (ok, okOut) = RunScript(SystemSetupService.BuildScript("test & ok", new[] { "echo CX-ONE", "echo CX-TWO" }));
        Check("S1", "all lines run, exit 0", ok == 0 && okOut.Contains("CX-ONE") && okOut.Contains("CX-TWO") && okOut.Contains("Done."), okOut);
        var (bad, badOut) = RunScript(SystemSetupService.BuildScript("test", new[] { "cmd /c exit 3", "echo CX-NEVER" }));
        Check("S2", "the first failing line stops it and its exit code comes back", bad == 3 && !badOut.Contains("CX-NEVER\r\n") && badOut.Contains("exit code 3"), $"{bad} · {badOut}");
        // The command itself is a `rem` line (inert whatever follows), so CX-INJECTED on its own line could only come from the
        // echo that shows the command — i.e. from & | not being escaped there.
        var (echo, echoOut) = RunScript(SystemSetupService.BuildScript("test", new[] { "rem a & echo CX-INJECTED | more > x.txt" }));
        Check("S3", "a shown command line is only echoed: & | > in it do not run",
            echo == 0 && !echoOut.Contains("CX-INJECTED\r\n") && echoOut.Contains("rem a & echo CX-INJECTED | more > x.txt"), echoOut);
        Check("S4", "the script carries no BOM-sensitive first line and starts with @echo off", SystemSetupService.BuildScript("t", new[] { "echo x" }).StartsWith("@echo off"));
        Check("S5", "this harness is not inside an MSIX package (a setup started from it would install for real)", !SystemSetupService.InsidePackage());

        Console.WriteLine("── this PC (read-only) ──");
        var settings = new SettingsService();
        var svc = new SystemSetupService(settings);
        var sw = Stopwatch.StartNew();
        var facts = await svc.GatherAsync();
        Check("R1", $"the check finishes in reasonable time ({sw.ElapsedMilliseconds} ms)", sw.Elapsed < TimeSpan.FromSeconds(60));
        Console.WriteLine($"        node={facts.Node} {facts.NodeVersion} · git={facts.GitVersion} · sdk={facts.DotnetSdk} · python={facts.PythonVersion} debugpy={facts.Debugpy}");
        Console.WriteLine($"        claude={facts.ClaudeCli} signedIn={facts.ClaudeSignedIn} · gemini={facts.GeminiCli} signedIn={facts.GeminiSignedIn}");
        Console.WriteLine($"        ollama={facts.Ollama} · llama-server={facts.LlamaServer} · webview2={facts.WebView2Version} · brainExe={facts.BrainExe}");
        Check("R2", "a found Node reports its version (vNN…)", facts.Node == null || facts.NodeVersion?.StartsWith("v") == true, facts.NodeVersion ?? "null");
        Check("R3", "a found Git reports its version", facts.Git == null || facts.GitVersion is { Length: > 0 }, facts.GitVersion ?? "null");
        Check("R4", "the Gemini CLI installed on this PC is found (node + its script)", facts.GeminiCli?.EndsWith("gemini.js") == true, facts.GeminiCli ?? "null");
        var rows = SystemSetupService.BuildItems(facts);
        Check("R5", "the rows for this PC build (15), each with a state", rows.Count == 15, rows.Count.ToString());

        Console.WriteLine("── every string the page shows, in Thai ──");
        var loc = new LocalizationService();
        LocalizedResources.Instance.Initialize(loc);
        loc.SetLanguage("th");
        var keys = new List<string> { "setup.title", "setup.subtitle", "setup.recheck", "setup.checking", "setup.checkedAt", "setup.checkFailed",
            "setup.summary", "setup.required", "setup.banner.noModel", "setup.banner.insidePackage", "nav.setupTip",
            "setup.detail.needsNode", "setup.detail.noDebugpy", "setup.detail.ollamaModels",
            "setup.confirm.title", "setup.confirm.body", "setup.confirm.winget", "setup.confirm.console", "setup.confirm.install", "setup.confirm.run", "setup.confirm.cancel",
            "setup.note.installing", "setup.note.installed", "setup.note.installedNotFound", "setup.note.installFailed", "setup.note.signingIn",
            "setup.note.signInFailed", "setup.note.starting", "setup.note.startFailed", "setup.note.readyNow", "setup.note.notYet", "setup.note.failed" };
        keys.AddRange(ConnectionTextKeys());
        foreach (var set in new[] { bare, items, all, SystemSetupService.BuildItems(running), SystemSetupService.BuildItems(new SetupFacts { Connections = FakeConnections() }) })
            foreach (var i in set)
            {
                keys.Add(i.TitleKey); keys.Add(i.WhyKey); keys.Add("setup.state." + i.State); keys.Add("setup.group." + i.Group);
                if (i.Primary != null) keys.Add(i.Primary.LabelKey);
                if (i.Secondary != null) keys.Add(i.Secondary.LabelKey);
            }
        static bool Thai(string s) => s.Any(ch => ch >= '\u0E00' && ch <= '\u0E7F');
        var missing = keys.Distinct().Where(k => LocalizedResources.Instance[k] == k || !Thai(LocalizedResources.Instance[k]) && !k.EndsWith(".git") && !k.EndsWith(".ollama")
            && !k.EndsWith(".node") && !k.EndsWith(".dotnet") && !k.EndsWith(".webview2") && !k.EndsWith(".brainx") && !k.EndsWith(".llamaserver")
            && !k.EndsWith(".claudecode") && !k.EndsWith(".geminicli") && !k.EndsWith(".codexcli") && !k.EndsWith(".python")
            && !k.StartsWith("setup.item.conn-")).ToList();
        Check("L1", $"{keys.Distinct().Count()} keys resolve, in Thai (product names may stay as they are)", missing.Count == 0, string.Join(", ", missing));
        loc.SetLanguage("en");
        var noEn = keys.Distinct().Where(k => LocalizedResources.Instance[k] == k).ToList();
        Check("L2", "…and in English", noEn.Count == 0, string.Join(", ", noEn));

        Console.WriteLine("── the page renders (real XAML, app resources, Thai) ──");
        loc.SetLanguage("th");
        string outDir = Path.Combine(AppContext.BaseDirectory, "renders");
        Exception? renderError = null;
        var shots = new List<string>();
        var ui = new Thread(() =>
        {
            try
            {
                var app = new CluadeX.App();
                app.InitializeComponent();
                foreach (var (name, f) in new[] { ("this-pc", facts), ("bare-pc", new SetupFacts()),
                             ("connections", new SetupFacts { BrainExe = "x", BrainConnected = true, Connections = FakeConnections() }) })
                {
                    var vm = new CluadeX.ViewModels.SetupViewModel(svc);
                    vm.Apply(f);
                    var view = new CluadeX.Views.SetupView { DataContext = vm, Width = 1100, Height = 1500 };
                    view.Measure(new System.Windows.Size(1100, 1500));
                    view.Arrange(new System.Windows.Rect(0, 0, 1100, 1500));
                    view.UpdateLayout();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(1100, 1500, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(view);
                    Directory.CreateDirectory(outDir);
                    string file = Path.Combine(outDir, name + ".png");
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using (var fs = File.Create(file)) enc.Save(fs);
                    shots.Add(file);
                }
            }
            catch (Exception ex) { renderError = ex; }
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        Check("V1", "SetupView loads and renders for this PC and a bare PC (no XAML / resource / binding crash)", renderError == null && shots.Count == 3,
            renderError?.ToString() ?? "");
        foreach (var s in shots) Console.WriteLine("        " + s);

        await ConnectionTests();

        Console.WriteLine($"\n═══ {pass} pass · {fail} FAIL ═══");
        Environment.Exit(fail == 0 ? 0 : 1);   // the app's background work must not keep the harness alive
        return fail == 0 ? 0 : 1;
    }

    /// <summary>Run a setup script hidden (stdin closed so its pause/timeout return at once) and read what it printed.</summary>
    static (int Code, string Out) RunScript(string script)
    {
        string file = Path.Combine(Path.GetTempPath(), $"cx-setuptest-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(file, script, new UTF8Encoding(false));
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c \"{file}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30000)) { p.Kill(true); return (-1, "timeout\n" + o); }
            return (p.ExitCode, o);
        }
        finally { try { File.Delete(file); } catch { } }
    }
}
