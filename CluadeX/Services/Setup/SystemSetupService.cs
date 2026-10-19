using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CluadeX.Models;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("setuptest")]

namespace CluadeX.Services.Setup;

/// <summary>Where a piece of the system stands.</summary>
public enum SetupState { Ready, Missing, NeedsKey, NeedsSignIn, NotRunning, NoModel }

/// <summary>What a row's button does.</summary>
public enum SetupActionKind { Install, SignIn, OpenUrl, OpenSettings, OpenPage, StartOllama, Run }

/// <summary>One button on a row. <paramref name="Commands"/>: the lines an Install/Run runs, shown to the user before
/// they agree; <paramref name="Target"/>: a URL, a page, or the provider for OpenSettings.</summary>
public sealed record SetupAction(SetupActionKind Kind, string LabelKey, IReadOnlyList<string>? Commands = null, string? Target = null,
    string? SizeHint = null);

/// <summary>One row: a piece CluadeX can use, its state, why it matters, and what to do next.</summary>
public sealed record SetupItem(string Id, string Group, SetupState State, string Detail, bool Optional,
    SetupAction? Primary = null, SetupAction? Secondary = null)
{
    public bool IsReady => State == SetupState.Ready;
    public string TitleKey => "setup.item." + Id;
    public string WhyKey => "setup.why." + Id;
}

/// <summary>What was found on this PC — gathered once per check (<see cref="SystemSetupService.GatherAsync"/>), turned
/// into rows by the pure <see cref="SystemSetupService.BuildItems"/> (the harness feeds it made-up machines).</summary>
public sealed record SetupFacts
{
    public string? Node { get; init; }
    public string? NodeVersion { get; init; }
    public string? Npm { get; init; }
    public string? Git { get; init; }
    public string? GitVersion { get; init; }
    public string? DotnetSdk { get; init; }
    public string? Python { get; init; }
    public string? PythonVersion { get; init; }
    public bool Debugpy { get; init; }
    public bool WebView2 { get; init; }
    public string? WebView2Version { get; init; }
    public string? Ollama { get; init; }
    public bool OllamaRunning { get; init; }
    public int OllamaModels { get; init; }
    public string? LlamaServer { get; init; }
    public string? ClaudeCli { get; init; }
    public bool ClaudeSignedIn { get; init; }
    public string? GeminiCli { get; init; }
    public bool GeminiSignedIn { get; init; }
    public bool AnthropicKey { get; init; }
    public bool OpenAiKey { get; init; }
    public bool GeminiKey { get; init; }
    public bool BrainConnected { get; init; }
    public string? BrainExe { get; init; }
    public bool Licensed { get; init; }
    /// <summary>CluadeX runs with an MSIX package identity (started from inside the Claude desktop app): an installer
    /// started from here would land in that app's private AppData and the real install would vanish (brain note
    /// 2026-10-18 "Installing CluadeX from a Claude session lands in Claude's MSIX private AppData").</summary>
    public bool InsidePackage { get; init; }
}

/// <summary>
/// "ตัวช่วยติดตั้ง" — what CluadeX can use on this PC, what is missing, and a button that gets each missing piece
/// (owner 2026-10-19: "เพิ่มช่องทางการติดตั้งโดยโปรแกรม … อะไรยังไม่ติดตั้ง ยังไม่มีคีย์ มีตัวช่วยจนครบระบบ").
///
/// - Installs run the package's own installer through winget (or npm / pip) in a console window the user watches —
///   CluadeX never installs silently, never types a password, and shows every command before the user agrees.
/// - The commands are constants in this file: nothing a model or a web page wrote is ever run here.
/// - Keys are never asked for here: the row opens the provider's key page and Settings ▸ AI Provider.
/// - A newly installed tool lands on the machine's PATH, not on this process's: after an install the process PATH is
///   rebuilt from the registry (<see cref="RefreshProcessPath"/>), so the CLIs are found without restarting CluadeX.
/// </summary>
public sealed class SystemSetupService
{
    private readonly SettingsService _settings;
    private readonly AiProviderManager? _providers;
    private readonly ActivationService? _activation;
    private readonly DebugLogService? _log;

    /// <summary>A tool was installed or signed in: the chat's model box and the providers look again.</summary>
    public static event Action? ToolsChanged;

    public SystemSetupService(SettingsService settings, AiProviderManager? providers = null, ActivationService? activation = null,
        DebugLogService? log = null)
    {
        _settings = settings;
        _providers = providers;
        _activation = activation;
        _log = log;
    }

    public const string Winget = "winget install --id {0} -e --source winget --accept-package-agreements --accept-source-agreements";

    /// <summary>winget install of one package, as the console runs it.</summary>
    public static string WingetInstall(string id) => string.Format(Winget, id);

    public static readonly string NpmExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd");

    // ═══════════════════════════════════════════════════════════════════
    //  Rows (pure)
    // ═══════════════════════════════════════════════════════════════════

    public const string GroupChat = "chat", GroupLocal = "local", GroupDev = "dev", GroupBrain = "brain";

    /// <summary>The rows for what was found. Pure: no process, no disk.</summary>
    public static List<SetupItem> BuildItems(SetupFacts f)
    {
        var items = new List<SetupItem>();
        SetupAction Settings(string provider) => new(SetupActionKind.OpenSettings, "setup.act.settings", Target: provider);

        // ── AI that answers the chat ──
        items.Add(f.Licensed
            ? new SetupItem("license", GroupChat, SetupState.Ready, "", Optional: false)
            : new SetupItem("license", GroupChat, SetupState.NeedsKey, "", Optional: false,
                new SetupAction(SetupActionKind.OpenPage, "setup.act.license", Target: "License")));
        items.Add(KeyRow("anthropic", f.AnthropicKey, "https://platform.claude.com/settings/keys", nameof(AiProviderType.Anthropic)));
        items.Add(KeyRow("openai", f.OpenAiKey, "https://platform.openai.com/api-keys", nameof(AiProviderType.OpenAI)));
        items.Add(KeyRow("gemini", f.GeminiKey, "https://aistudio.google.com/apikey", nameof(AiProviderType.Gemini)));

        if (f.ClaudeCli == null)
            items.Add(new SetupItem("claudecode", GroupChat, SetupState.Missing, "", true,
                new SetupAction(SetupActionKind.Install, "setup.act.install", new[] { WingetInstall("Anthropic.ClaudeCode") }, SizeHint: "~70 MB")));
        else if (!f.ClaudeSignedIn)
            items.Add(new SetupItem("claudecode", GroupChat, SetupState.NeedsSignIn, f.ClaudeCli, true,
                new SetupAction(SetupActionKind.SignIn, "setup.act.signin", Target: "claude")));
        else
            items.Add(new SetupItem("claudecode", GroupChat, SetupState.Ready, f.ClaudeCli, true));

        if (f.GeminiCli == null)
        {
            // npm comes with Node: no Node yet → the same click installs Node first (npm by its full path — the console's
            // PATH is from before Node arrived).
            var lines = new List<string>();
            if (f.Npm == null) lines.Add(WingetInstall("OpenJS.NodeJS.LTS"));
            lines.Add($"\"{f.Npm ?? NpmExe}\" install -g @google/gemini-cli");
            items.Add(new SetupItem("geminicli", GroupChat, SetupState.Missing, f.Npm == null ? "needs-node" : "", true,
                new SetupAction(SetupActionKind.Install, "setup.act.install", lines, SizeHint: f.Npm == null ? "~120 MB" : "~60 MB")));
        }
        else if (!f.GeminiSignedIn)
            items.Add(new SetupItem("geminicli", GroupChat, SetupState.NeedsSignIn, f.GeminiCli, true,
                new SetupAction(SetupActionKind.SignIn, "setup.act.signin", Target: "gemini")));
        else
            items.Add(new SetupItem("geminicli", GroupChat, SetupState.Ready, f.GeminiCli, true));

        // ── AI on this PC ──
        if (f.Ollama == null)
            items.Add(new SetupItem("ollama", GroupLocal, SetupState.Missing, "", true,
                new SetupAction(SetupActionKind.Install, "setup.act.install", new[] { WingetInstall("Ollama.Ollama") }, SizeHint: "~1 GB")));
        else if (!f.OllamaRunning)
            items.Add(new SetupItem("ollama", GroupLocal, SetupState.NotRunning, f.Ollama, true,
                new SetupAction(SetupActionKind.StartOllama, "setup.act.start", Target: f.Ollama)));
        else if (f.OllamaModels == 0)
            items.Add(new SetupItem("ollama", GroupLocal, SetupState.NoModel, f.Ollama, true,
                new SetupAction(SetupActionKind.Run, "setup.act.pullModel", new[] { $"\"{f.Ollama}\" pull {StarterOllamaModel}" }, SizeHint: "~2 GB"),
                new SetupAction(SetupActionKind.OpenUrl, "setup.act.browseModels", Target: "https://ollama.com/library")));
        else
            items.Add(new SetupItem("ollama", GroupLocal, SetupState.Ready, f.OllamaModels.ToString(), true));

        items.Add(f.LlamaServer != null
            ? new SetupItem("llamaserver", GroupLocal, SetupState.Ready, f.LlamaServer, true)
            : new SetupItem("llamaserver", GroupLocal, SetupState.Missing, "", true,
                new SetupAction(SetupActionKind.Install, "setup.act.install", new[] { WingetInstall("ggml.llamacpp") }, SizeHint: "~300 MB")));

        // ── Coding tools ──
        items.Add(Tool("git", GroupDev, f.Git, f.GitVersion, "Git.Git", "~350 MB", optional: false));
        items.Add(Tool("node", GroupDev, f.Node, f.NodeVersion, "OpenJS.NodeJS.LTS", "~120 MB", optional: true));
        items.Add(Tool("dotnet", GroupDev, f.DotnetSdk, f.DotnetSdk, "Microsoft.DotNet.SDK.8", "~250 MB", optional: true));
        if (f.Python == null)
            items.Add(new SetupItem("python", GroupDev, SetupState.Missing, "", true,
                new SetupAction(SetupActionKind.Install, "setup.act.install", new[] { WingetInstall("Python.Python.3.12") }, SizeHint: "~100 MB")));
        else if (!f.Debugpy)
            items.Add(new SetupItem("python", GroupDev, SetupState.Missing, (f.PythonVersion ?? f.Python) + " · no-debugpy", true,
                new SetupAction(SetupActionKind.Install, "setup.act.installDebugpy", new[] { $"\"{f.Python}\" -m pip install --user debugpy" }, SizeHint: "~5 MB")));
        else
            items.Add(new SetupItem("python", GroupDev, SetupState.Ready, f.PythonVersion ?? f.Python, true));
        items.Add(f.WebView2
            ? new SetupItem("webview2", GroupDev, SetupState.Ready, f.WebView2Version ?? "", true)
            : new SetupItem("webview2", GroupDev, SetupState.Missing, "", true,
                new SetupAction(SetupActionKind.Install, "setup.act.install", new[] { WingetInstall("Microsoft.EdgeWebView2Runtime") }, SizeHint: "~180 MB")));

        // ── The brain ──
        if (f.BrainConnected)
            items.Add(new SetupItem("brainx", GroupBrain, SetupState.Ready, f.BrainExe ?? "", true));
        else if (f.BrainExe != null)
            items.Add(new SetupItem("brainx", GroupBrain, SetupState.NotRunning, f.BrainExe, true,
                new SetupAction(SetupActionKind.OpenPage, "setup.act.mcpPage", Target: "McpServers")));
        else
            items.Add(new SetupItem("brainx", GroupBrain, SetupState.Missing, "", true,
                new SetupAction(SetupActionKind.OpenUrl, "setup.act.download", Target: "https://xman4289.com/brainx/download")));

        return items;

        static SetupItem KeyRow(string id, bool has, string keyPage, string provider) => has
            ? new SetupItem(id, GroupChat, SetupState.Ready, "", true, new SetupAction(SetupActionKind.OpenSettings, "setup.act.settings", Target: provider))
            : new SetupItem(id, GroupChat, SetupState.NeedsKey, "", true,
                new SetupAction(SetupActionKind.OpenUrl, "setup.act.getKey", Target: keyPage),
                new SetupAction(SetupActionKind.OpenSettings, "setup.act.enterKey", Target: provider));

        static SetupItem Tool(string id, string group, string? path, string? version, string wingetId, string size, bool optional) => path != null
            ? new SetupItem(id, group, SetupState.Ready, version ?? path, optional)
            : new SetupItem(id, group, SetupState.Missing, "", optional,
                new SetupAction(SetupActionKind.Install, "setup.act.install", new[] { WingetInstall(wingetId) }, SizeHint: size));
    }

    /// <summary>The small model offered when Ollama has none (~2 GB, runs on most GPUs and on the CPU).</summary>
    public const string StarterOllamaModel = "llama3.2:3b";

    /// <summary>Can the chat be answered by anything at all right now? (The page says so at the top when not.)</summary>
    public static bool AnyChatModelReady(SetupFacts f) =>
        (f.Licensed && (f.AnthropicKey || f.OpenAiKey || f.GeminiKey || f.ClaudeCli != null && f.ClaudeSignedIn || f.GeminiCli != null && f.GeminiSignedIn))
        || f.OllamaRunning && f.OllamaModels > 0;

    // ═══════════════════════════════════════════════════════════════════
    //  Finding things (pure helpers + the real probe)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>The first <paramref name="names"/> found in the PATH directories (in order), then in
    /// <paramref name="knownPaths"/>. Microsoft Store app-execution aliases under WindowsApps are skipped — python.exe
    /// there is a stub that opens the Store.</summary>
    public static string? FindExe(string path, IEnumerable<string> names, IEnumerable<string> knownPaths, Func<string, bool> fileExists)
    {
        foreach (var dir in (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (dir.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var name in names)
            {
                string p;
                try { p = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim('"')), name); } catch { continue; }
                if (fileExists(p)) return p;
            }
        }
        foreach (var p in knownPaths)
            if (!string.IsNullOrWhiteSpace(p) && fileExists(p)) return p;
        return null;
    }

    /// <summary>The machine's PATH as a new process would get it: Machine + User from the registry, then this process's
    /// own entries (nothing lost), each once.</summary>
    public static string FreshPath()
    {
        var parts = new List<string>();
        void Add(string? s)
        {
            foreach (var d in (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!parts.Contains(d, StringComparer.OrdinalIgnoreCase)) parts.Add(d);
        }
        try { Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)); } catch { }
        try { Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)); } catch { }
        Add(Environment.GetEnvironmentVariable("PATH"));
        return string.Join(';', parts);
    }

    /// <summary>After an install: this process (and every CLI it starts from now on) sees the new PATH.</summary>
    public static void RefreshProcessPath()
    {
        try { Environment.SetEnvironmentVariable("PATH", FreshPath()); } catch { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);

    /// <summary>Does this process run with a package identity (inside an MSIX app such as the Claude desktop app)?</summary>
    public static bool InsidePackage()
    {
        try
        {
            int len = 0;
            const int AppModelErrorNoPackage = 15700;
            return GetCurrentPackageFullName(ref len, null) != AppModelErrorNoPackage;
        }
        catch { return false; }
    }

    /// <summary>Look at everything (off the UI thread; read-only: file checks, `--version`, the brain/Ollama state).</summary>
    public async Task<SetupFacts> GatherAsync(CancellationToken ct = default)
    {
        string path = FreshPath();
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string? Find(string[] names, params string[] known) => FindExe(path, names, known, File.Exists);

        string? node = Find(new[] { "node.exe" }, Path.Combine(pf, "nodejs", "node.exe"));
        string? npm = Find(new[] { "npm.cmd" }, NpmExe);
        string? git = Find(new[] { "git.exe" }, Path.Combine(pf, "Git", "cmd", "git.exe"));
        string? dotnet = Find(new[] { "dotnet.exe" }, Path.Combine(pf, "dotnet", "dotnet.exe"));
        string? python = Find(new[] { "python.exe" }, Path.Combine(local, "Programs", "Python", "Python312", "python.exe"),
            Path.Combine(local, "Programs", "Python", "Python313", "python.exe"));
        string? ollama = Find(new[] { "ollama.exe" }, Path.Combine(local, "Programs", "Ollama", "ollama.exe"));
        string? llama = Find(new[] { "llama-server.exe" }, Path.Combine(AppContext.BaseDirectory, "llama-backend", "llama-server.exe"),
            string.IsNullOrWhiteSpace(_settings.Settings.CustomLlamaCppBackendPath) ? "" : Path.Combine(_settings.Settings.CustomLlamaCppBackendPath!, "llama-server.exe"));
        string? claude = Providers.ClaudeDevProvider.FindCli(string.Join("\n", path.Split(';').Select(d => SafeCombine(d, "claude.exe"))),
            appData, home, File.Exists, Directory.Exists, d => Directory.GetDirectories(d));
        var gemini = Providers.GeminiDevProvider.FindCli(path.Split(';').Select(d => SafeCombine(d, "gemini.cmd")).Where(File.Exists),
            node != null ? new[] { node } : Array.Empty<string>(), appData, pf, File.Exists);

        var nodeV = node != null ? RunAsync(node, "--version", ct) : Task.FromResult<RunResult?>(null);
        var gitV = git != null ? RunAsync(git, "--version", ct) : Task.FromResult<RunResult?>(null);
        var sdks = dotnet != null ? RunAsync(dotnet, "--list-sdks", ct) : Task.FromResult<RunResult?>(null);
        var pyV = python != null ? RunAsync(python, "--version", ct) : Task.FromResult<RunResult?>(null);
        var dbg = python != null ? RunAsync(python, "-m debugpy --version", ct) : Task.FromResult<RunResult?>(null);
        var auth = claude != null ? RunAsync(claude, "auth status", ct, TimeSpan.FromSeconds(15)) : Task.FromResult<RunResult?>(null);
        Task<List<string>?> ollamaModels = _providers?.GetProvider(AiProviderType.Ollama) is Providers.OllamaProvider op
            ? op.ProbeModelsAsync(ct) : Task.FromResult<List<string>?>(null);
        await Task.WhenAll(nodeV, gitV, sdks, pyV, dbg, auth, ollamaModels);

        var cfgs = _settings.Settings.ProviderConfigs;
        bool HasKey(AiProviderType t) => cfgs.TryGetValue(t.ToString(), out var c) && !string.IsNullOrWhiteSpace(c.ApiKey);
        string? Ok(RunResult? r) => r is { Code: 0 } x && x.Out.Trim().Length > 0 ? x.Out.Trim().Split('\n')[0].Trim() : null;
        string? sdk8 = sdks.Result is { Code: 0 } s
            ? s.Out.Split('\n').Select(l => l.Trim()).LastOrDefault(l => int.TryParse(l.Split('.')[0], out int major) && major >= 8) : null;
        var web = Browser.BrowserEngineProbe.Probe(refresh: true);
        // The row asks "is the runtime installed": the registry says so even where the loader DLL is not beside the exe.
        string? webVersion = web.WebView2Version ?? Browser.BrowserEngineProbe.RegistryRuntimeVersion();
        string brainExe = Mcp.McpServerManager.BrainMirrorExe(home);

        return new SetupFacts
        {
            Node = node, NodeVersion = Ok(nodeV.Result), Npm = npm,
            Git = git, GitVersion = Ok(gitV.Result)?.Replace("git version ", ""),
            DotnetSdk = sdk8?.Split(' ')[0],
            Python = python, PythonVersion = Ok(pyV.Result), Debugpy = dbg.Result is { Code: 0 },
            WebView2 = !string.IsNullOrWhiteSpace(webVersion), WebView2Version = webVersion,
            Ollama = ollama, OllamaRunning = ollamaModels.Result != null, OllamaModels = ollamaModels.Result?.Count ?? 0,
            LlamaServer = llama,
            ClaudeCli = claude, ClaudeSignedIn = auth.Result?.Out.Contains("\"loggedIn\": true", StringComparison.OrdinalIgnoreCase) == true,
            GeminiCli = gemini?.Script, GeminiSignedIn = Providers.GeminiDevProvider.SignedIn(),
            AnthropicKey = HasKey(AiProviderType.Anthropic), OpenAiKey = HasKey(AiProviderType.OpenAI), GeminiKey = HasKey(AiProviderType.Gemini),
            BrainConnected = BrainSyncService.Current?.IsBrainAvailable == true,
            BrainExe = File.Exists(brainExe) ? brainExe : null,
            Licensed = _activation?.IsActivated == true,
            InsidePackage = InsidePackage(),
        };
    }

    private static string SafeCombine(string dir, string name)
    {
        try { return Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')), name); } catch { return ""; }
    }

    /// <summary>Run a tool briefly and read what it printed (stdout, else stderr). Null when it could not start or did
    /// not finish in time.</summary>
    private sealed record RunResult(int Code, string Out);

    private static async Task<RunResult?> RunAsync(string exe, string args, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["PATH"] = FreshPath();
            using var p = Process.Start(psi);
            if (p == null) return null;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
            var outTask = p.StandardOutput.ReadToEndAsync(limit.Token);
            var errTask = p.StandardError.ReadToEndAsync(limit.Token);
            try { await p.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return null; }
            string o = await outTask, e = await errTask;
            return new RunResult(p.ExitCode, o.Trim().Length > 0 ? o : e);
        }
        catch { return null; }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Doing it
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>The console script for an install / run: each line echoed, then run; the first failure stops it; the
    /// exit code is the failing line's. Pure, so the harness checks it.</summary>
    public static string BuildScript(string title, IReadOnlyList<string> commands)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("title CluadeX setup - " + title.Replace('&', '+').Replace('|', '/').Replace('%', ' '));
        sb.AppendLine("echo [CluadeX] " + title.Replace('&', '+').Replace('|', '/').Replace('%', ' '));
        sb.AppendLine("echo.");
        foreach (var c in commands)
        {
            sb.AppendLine("echo ^> " + c.Replace("^", "^^").Replace("&", "^&").Replace("|", "^|").Replace("<", "^<").Replace(">", "^>").Replace("%", "%%"));
            sb.AppendLine(c);
            sb.AppendLine("if errorlevel 1 goto failed");
        }
        sb.AppendLine("echo.");
        sb.AppendLine("echo [CluadeX] Done. This window closes by itself - CluadeX checks again.");
        sb.AppendLine("timeout /t 8 >nul");
        sb.AppendLine("exit /b 0");
        sb.AppendLine(":failed");
        sb.AppendLine("set CX_EXIT=%ERRORLEVEL%");
        sb.AppendLine("echo.");
        sb.AppendLine("echo [CluadeX] It stopped with exit code %CX_EXIT%. Read the lines above, then close this window.");
        sb.AppendLine("pause");
        sb.AppendLine("exit /b %CX_EXIT%");
        return sb.ToString();
    }

    /// <summary>Run <paramref name="commands"/> in a console window the user watches; returns its exit code (0 = done).
    /// Refused inside an MSIX package (see <see cref="SetupFacts.InsidePackage"/>).</summary>
    public async Task<int> RunInConsoleAsync(string title, IReadOnlyList<string> commands, CancellationToken ct = default)
    {
        if (InsidePackage()) throw new InvalidOperationException("inside-package");
        string dir = Path.Combine(Path.GetTempPath(), "cluadex-setup");
        Directory.CreateDirectory(dir);
        string script = Path.Combine(dir, $"setup-{Guid.NewGuid():N}.cmd");
        // OEM code page is irrelevant (chcp 65001), but the file must not carry a BOM: cmd.exe reads it as a command.
        await File.WriteAllTextAsync(script, BuildScript(title, commands), new UTF8Encoding(false), ct);
        _log?.Info("Setup", $"running in a console: {title} · {string.Join(" && ", commands)}");
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });
            if (p == null) return -1;
            await p.WaitForExitAsync(ct);
            int code = p.ExitCode;
            _log?.Info("Setup", $"{title} finished · exit {code}");
            RefreshProcessPath();
            ToolsChanged?.Invoke();
            return code;
        }
        finally { try { File.Delete(script); } catch { } }
    }

    /// <summary>Open a CLI's own sign-in (its browser page; the click there is the user's).</summary>
    public bool OpenSignIn(string which, SetupFacts facts)
    {
        if (which == "claude") return Providers.ClaudeDevProvider.OpenLoginTerminal(facts.ClaudeCli);
        if (which == "gemini" && _providers?.GetProvider(AiProviderType.GeminiDev) is Providers.GeminiDevProvider gd)
            return gd.OpenLoginTerminal();
        return false;
    }

    /// <summary>Start Ollama: its tray app when installed with one (it serves in the background), else `ollama serve` in
    /// a minimized console.</summary>
    public bool StartOllama(string ollamaExe)
    {
        try
        {
            string app = Path.Combine(Path.GetDirectoryName(ollamaExe) ?? "", "ollama app.exe");
            if (File.Exists(app)) Process.Start(new ProcessStartInfo(app) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(ollamaExe, "serve") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
            return true;
        }
        catch (Exception ex) { _log?.Warn("Setup", "starting Ollama failed", ex); return false; }
    }

    /// <summary>Tell the chat's model box and the providers to look again (a sign-in finished, a key was saved).</summary>
    public static void RaiseToolsChanged() => ToolsChanged?.Invoke();
}
