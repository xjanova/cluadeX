using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CluadeX.Models;
using CluadeX.Services.Safety;

namespace CluadeX.Services.Providers;

/// <summary>
/// "antigravity-dev" — Google's Antigravity CLI (<c>agy</c>, a single Go binary in %LOCALAPPDATA%\agy\bin) as a CluadeX
/// model. Since 2026-06-18 the Gemini CLI refuses personal Google accounts ("IneligibleTierError … migrate to
/// Antigravity"), paid Google AI Pro included; agy is where those accounts go. No API key: agy signs in with the Google
/// account in a browser once (run <c>agy</c> in a terminal) and keeps the token in Windows Credential Manager.
///
/// The same decisions as <see cref="GeminiDevProvider"/> and <see cref="ClaudeDevProvider"/>, which this mirrors:
/// - One delegated call per turn; the conversation goes over stdin as agy's stream-json input
///   (<c>{"event":"user","message":{…}}</c>), the reply comes back as agy's stream-json (init / step_update / result),
///   translated by <see cref="AntigravityStreamTranslator"/> into Claude Code's stream-json for the one renderer that
///   already works (<see cref="ClaudeDevStreamTurn"/>).
/// - Safety: agy's headless mode cannot prompt and refuses every tool needing a permission (on 1.2.7 even reading the
///   project — CRT spike), and a PreToolUse hook's "allow" does not lift that. So every turn runs with
///   --dangerously-skip-permissions and CluadeX's guard is the permission system: a per-turn plugin (a folder passed
///   with --add-dir: .agents/plugins/cluadex/{plugin.json, hooks.json, mcp_config.json}) whose PreToolUse hook on every
///   tool is <c>CluadeX.exe --cx-agy-guard</c> (<see cref="AntigravityGuard"/>) — reads pass, writes need the edit grant,
///   commands the run grant, dangerous ones the red prompt. Its PreInvocation hook writes a marker; a turn whose model
///   starts before the marker exists (agy did not load the hooks) is killed before its first tool (fail closed).
/// - CluadeX's chat tools and the BrainX brain ride along as the plugin's MCP servers (agy names them
///   <c>cluadex_&lt;server&gt;</c> and calls them through call_mcp_tool).
/// The agy facts here are from Google's docs (antigravity.google/docs/cli/headless, /hooks, /permissions, /install) and
/// the CRT project's recorded spike on agy 1.2.7, Windows (github.com/simv/crt docs/spikes/antigravity-2026-09.md).
/// </summary>
public class AntigravityDevProvider : IAiProvider, IDelegatedCliProvider
{
    private readonly SettingsService _settingsService;
    private readonly FileSystemService _fileSystemService;

    private string? _exe;
    private string _statusMessage = "Not initialised";
    private bool _isLoading;

    public const string Id = "AntigravityDev";
    public string ProviderId => Id;
    public string DisplayName => "Antigravity Dev (agy)";
    public bool IsReady => _exe != null;
    /// <summary>agy.exe, once found.</summary>
    public string? Exe => _exe;
    public bool IsLoading => _isLoading;
    public string StatusMessage => _statusMessage;

    public event Action<string>? OnStatusChanged;
    public event Action<bool>? OnLoadingChanged;
    public event Action<string>? OnError;

    /// <summary>"default" sends no --model (agy's own pick for the signed-in plan). The slugs <c>agy models</c> lists are
    /// added by <see cref="ListModelsAsync"/> (<see cref="DiscoveredModels"/>): an unknown slug ends the run with an
    /// error, so none is hard-coded.</summary>
    public static readonly string[] KnownModels = ["default"];

    /// <summary>What <c>agy models</c> listed last time it was asked (empty until then).</summary>
    public static IReadOnlyList<string> DiscoveredModels { get; private set; } = Array.Empty<string>();

    /// <summary>The plugin's name — agy prefixes the plugin's MCP servers with it (<c>cluadex_brainx-brain</c>).</summary>
    public const string PluginName = "cluadex";

    public const string InstallCommand = "irm https://antigravity.google/cli/install.ps1 | iex";

    public AntigravityDevProvider(SettingsService settingsService, FileSystemService fileSystemService)
    {
        _settingsService = settingsService;
        _fileSystemService = fileSystemService;
    }

    // ─── Finding the CLI ───

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_exe != null) return;
        SetLoading(true);
        try
        {
            _exe = await Task.Run(Locate, ct);
            SetStatus(_exe != null ? $"Ready · {_exe}" : $"Antigravity CLI not found — install it in PowerShell: {InstallCommand}");
        }
        finally { SetLoading(false); }
    }

    /// <summary>Is agy on this machine (cheap; for the chat's model box)?</summary>
    public static bool IsInstalled() => Locate() != null;

    private static string? Locate() => FindCli(WhereLines("agy"),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), File.Exists);

    /// <summary>agy.exe from `where agy`, else the installer's place (%LOCALAPPDATA%\agy\bin). Never a .cmd/.ps1 alias:
    /// a .cmd runs under cmd.exe, which re-parses the arguments. Pure apart from <paramref name="fileExists"/>.</summary>
    public static string? FindCli(IEnumerable<string> whereAgy, string localAppData, Func<string, bool> fileExists)
    {
        foreach (var line in whereAgy)
            if (line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && fileExists(line)) return line;
        string installed = Path.Combine(localAppData, "agy", "bin", "agy.exe");
        return fileExists(installed) ? installed : null;
    }

    private static List<string> WhereLines(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("where.exe", name)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return new();
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }
        catch { return new(); }
    }

    /// <summary>The model slugs in <c>agy models</c>'s output ("Fetching available models..." first, then the slugs —
    /// gemini-3.8-flash-high, claude-opus-4-6-thinking, gpt-oss-120b-medium on 1.2.7). Pure.</summary>
    public static List<string> ParseModels(string output) =>
        output.Split('\n').Select(l => l.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
            .Where(t => Regex.IsMatch(t, @"^[a-z0-9]+(?:[.\-][a-z0-9]+)+$") && t.Any(char.IsDigit))
            .Distinct().ToList();

    /// <summary>Ask agy which models the signed-in account may use (a network call, ~5 s; nothing on failure).</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        string? exe = await Task.Run(Locate, ct);
        if (exe == null) return DiscoveredModels;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("models");
            psi.Environment["NO_COLOR"] = "1";
            using var p = Process.Start(psi);
            if (p == null) return DiscoveredModels;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            string text = await p.StandardOutput.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            var models = ParseModels(text);
            if (p.ExitCode == 0 && models.Count > 0) DiscoveredModels = models;
        }
        catch { }
        return DiscoveredModels;
    }

    // ─── One turn ───

    /// <summary>agy's arguments for one turn. Pure, so the harness checks them. The plugin's folder goes last among the
    /// --add-dir folders: if a build keeps only one, it keeps the guard (the user's extra folders are lost, not the guard).</summary>
    public static List<string> BuildArgs(string turnDir, bool worker, string model, IReadOnlyList<string>? addDirs)
    {
        var args = new List<string>
        {
            "--output-format", "stream-json",
            "--input-format", "stream-json",
            // A string flag: in stream-input mode the prompt is empty and the turn comes over stdin.
            "--print", "",
            // The guard hook decides every call (headless agy would refuse even reads otherwise).
            "--dangerously-skip-permissions",
            "--print-timeout", worker ? "90m" : "10m",
        };
        if (!string.IsNullOrWhiteSpace(model)
            && !model.Equals("default", StringComparison.OrdinalIgnoreCase)
            && !model.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--model");
            args.Add(model.Trim());
        }
        foreach (var d in addDirs ?? Array.Empty<string>())
        {
            args.Add("--add-dir");
            args.Add(d);
        }
        args.Add("--add-dir");
        args.Add(turnDir);
        return args;
    }

    /// <summary>The turn's one stdin message: the conversation so far, the user's message, then what CluadeX tells the
    /// agent about this chat (the grant, the folders, the language). Pure.</summary>
    public static string BuildPrompt(string cwd, ClaudeDevProvider.WorkerGrant? grant, IReadOnlyList<string>? addDirs,
        string transcript, string userMessage, string? codeComments = null)
    {
        bool worker = grant is { Edit: true } || grant is { Run: true };
        addDirs ??= Array.Empty<string>();
        string folders = addDirs.Count == 0 ? ""
            : "The user also allowed these folders beside the project — work in them by absolute path: " + string.Join(", ", addDirs) + ". ";
        string wrapper = worker
            ? "You are 'antigravity-dev', the coding agent of a CluadeX chat, working directly in the project. " +
              $"Project working directory: {cwd}. " +
              (grant!.Edit ? "You may create and edit files inside it. " : "You may not edit files: propose changes as diffs. ") +
              (grant.Run ? "You may run commands (builds, tests, git) in it. " : "You may not run commands. ") +
              folders +
              "Stay inside the project folder" + (addDirs.Count > 0 ? " and those folders" : "") + " unless the user's request names another. " +
              "CluadeX's safety hook checks every tool call; when it refuses one, do not retry or work around it — tell the user. " +
              "Other agents on this machine are reachable through the brainx-brain MCP tools (task_queue, task_handoff, cowork_*). " +
              "Answer in Markdown (code in fenced blocks) and in the language the user writes in."
            : "You are 'antigravity-dev', answering inside the CluadeX desktop app. " +
              $"Project working directory: {cwd}. " +
              "This chat is read-only: you may read files and search, but CluadeX's safety hook refuses writes and commands — " +
              "propose file edits as diffs/snippets for the app to apply. " + folders +
              "Answer in Markdown (code in fenced blocks) and in the language the user writes in.";
        wrapper += "\n\n" + AgentPersona.Section(AiProviderType.AntigravityDev, false);
        if (!string.IsNullOrWhiteSpace(codeComments)) wrapper += "\n\n" + codeComments.Trim();

        var sb = new StringBuilder();
        if (transcript.Length > 0)
            sb.Append("<conversation_so_far>\n").Append(transcript).Append("\n</conversation_so_far>\n\n");
        sb.Append(userMessage).Append("\n\n[CluadeX] ").Append(wrapper);
        return sb.ToString();
    }

    /// <summary>One line of agy's stream-json input (text content only — agy refuses any other block).</summary>
    public static string StdinLine(string content) => new JsonObject
    {
        ["event"] = "user",
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
    }.ToJsonString();

    /// <summary>
    /// The turn's plugin, relative path → text: plugin.json, hooks.json (the guard on every tool + the load marker, both
    /// through .cmd wrappers), guard.json (the grant the hook enforces) and mcp_config.json (CluadeX's chat tools from the
    /// ChatToolBridge config, and the brain when the user's own agy config does not have it). Pure: the caller writes it.
    /// <paramref name="pluginDir"/> is where the files go (the hooks are named by absolute path).
    /// </summary>
    public static Dictionary<string, string> BuildPlugin(string pluginDir, string guardExe, ClaudeDevProvider.WorkerGrant? grant,
        string cwd, string? chatToolsConfigJson, string? brainServerJson)
    {
        var indented = new JsonSerializerOptions { WriteIndented = true };
        var files = new Dictionary<string, string>
        {
            ["plugin.json"] = new JsonObject { ["name"] = PluginName }.ToJsonString(indented),
            ["guard.cmd"] = AntigravityGuard.GuardCmd(guardExe),
            ["loaded.cmd"] = AntigravityGuard.LoadedCmd(guardExe),
            ["hooks.json"] = AntigravityGuard.HooksJson(Path.Combine(pluginDir, "guard.cmd"), Path.Combine(pluginDir, "loaded.cmd")).ToJsonString(indented),
            ["guard.json"] = JsonSerializer.Serialize(new AntigravityGuard.Config(grant?.Edit == true, grant?.Run == true, cwd, pluginDir), indented),
        };
        var servers = new JsonObject();
        if (!string.IsNullOrWhiteSpace(chatToolsConfigJson))
        {
            try
            {
                if (JsonNode.Parse(chatToolsConfigJson)?["mcpServers"] is JsonObject fromBridge)
                    foreach (var kv in fromBridge.ToList())
                        if (kv.Value is JsonObject s)
                        {
                            var copy = (JsonObject)s.DeepClone();
                            copy.Remove("type");
                            servers[kv.Key] = copy;
                        }
            }
            catch (JsonException) { }
        }
        if (!string.IsNullOrWhiteSpace(brainServerJson))
        {
            try
            {
                if (JsonNode.Parse(brainServerJson) is JsonObject brain)
                {
                    brain.Remove("type");   // Claude Code's "stdio" marker; agy infers the transport from command
                    servers[GeminiDevProvider.BrainServerName] = brain;
                }
            }
            catch (JsonException) { }
        }
        if (servers.Count > 0) files["mcp_config.json"] = new JsonObject { ["mcpServers"] = servers }.ToJsonString(indented);
        return files;
    }

    /// <summary>The MCP servers in the user's own agy config (~/.gemini/config/mcp_config.json).</summary>
    private static List<string> UserServers()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "config", "mcp_config.json");
            if (!File.Exists(path)) return new();
            return (JsonNode.Parse(File.ReadAllText(path))?["mcpServers"] as JsonObject)?.Select(kv => kv.Key).ToList() ?? new();
        }
        catch { return new(); }
    }

    private static string? BrainServerFromClaude()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            if (!File.Exists(path)) return null;
            return (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?["mcpServers"]?[GeminiDevProvider.BrainServerName]?.ToJsonString();
        }
        catch { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint size);

    /// <summary>The 8.3 form of an existing path (no spaces, ASCII) when Windows has one, else the path itself: agy runs
    /// hook commands through cmd /c, and a .cmd is read in the OEM code page.</summary>
    public static string ShortPath(string path)
    {
        try
        {
            var sb = new StringBuilder(1024);
            uint n = GetShortPathNameW(path, sb, (uint)sb.Capacity);
            return n > 0 && n < sb.Capacity ? sb.ToString() : path;
        }
        catch { return path; }
    }

    public async IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        LastTurnAuthRefused = false;
        if (_exe == null) await InitializeAsync(ct);
        if (_exe is not { } exe)
        {
            OnError?.Invoke("Antigravity CLI not found on this machine");
            yield return "ไม่พบ Antigravity CLI (`agy`) บนเครื่องนี้ — ติดตั้งใน PowerShell ด้วย\n\n```powershell\n" + InstallCommand
                + "\n```\n\nแล้วเปิดเทอร์มินัลใหม่ รัน `agy` หนึ่งครั้งเพื่อล็อกอินด้วยบัญชี Google (Google AI Pro ใช้ได้)";
            yield break;
        }

        string cwd = _fileSystemService.HasWorkingDirectory
            ? _fileSystemService.WorkingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var grant = ClaudeDevProvider.Grant.Value;
        bool worker = grant is { Edit: true } || grant is { Run: true };
        var sink = worker ? ClaudeDevProvider.StreamSink.Value : null;
        var extras = ClaudeDevProvider.Extras.Value;
        string model = _settingsService.Settings.ProviderConfigs.TryGetValue(Id, out var cfg)
            && !string.IsNullOrWhiteSpace(cfg.EffectiveModelId) ? cfg.EffectiveModelId! : "";

        // The turn's plugin: the guard (every turn — it is the permission system), the chat tools, the brain.
        var userServers = UserServers();
        string? chatToolsJson = null;
        if (!string.IsNullOrWhiteSpace(extras?.ToolsConfig))
            try { chatToolsJson = await File.ReadAllTextAsync(extras.ToolsConfig, ct); } catch { chatToolsJson = null; }
        string? brain = userServers.Contains(GeminiDevProvider.BrainServerName) ? null : BrainServerFromClaude();
        string? turnDir = null, pluginDir = null, pluginError = null;
        try
        {
            turnDir = Path.Combine(Path.GetTempPath(), "cluadex-guard", "agy-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(Path.Combine(turnDir, ".agents", "plugins", PluginName));
            turnDir = ShortPath(turnDir);
            pluginDir = Path.Combine(turnDir, ".agents", "plugins", PluginName);
            foreach (var (name, text) in BuildPlugin(pluginDir, ShortPath(GuardHook.ExePath()), grant, cwd, chatToolsJson, brain))
                await File.WriteAllTextAsync(Path.Combine(pluginDir, name), text, new UTF8Encoding(false), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { pluginError = ex.Message; }
        if (pluginError != null || turnDir == null || pluginDir == null)
        {
            // No plugin = no guard: the turn does not run at all (agy would run every tool unchecked).
            OnError?.Invoke("Antigravity Dev: could not write the turn's guard: " + pluginError);
            TryDeleteDir(turnDir);
            yield return "⛔ Antigravity Dev ไม่ได้รัน — เขียนไฟล์ตัวกันความปลอดภัยของเทิร์นนี้ไม่สำเร็จ (" + Cap(pluginError ?? "", 200) + ") จึงไม่ปล่อยให้ agy ทำงานโดยไม่มีตัวกัน";
            yield break;
        }
        string marker = Path.Combine(pluginDir, "hooks.loaded");
        bool hooksSeen = false;

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in BuildArgs(turnDir, worker, model, ClaudeDevProvider.TurnAddDirs(extras))) psi.ArgumentList.Add(a);
        psi.Environment["NO_COLOR"] = "1";

        var translator = new AntigravityStreamTranslator(PluginName, ServerNames(chatToolsJson, brain != null, userServers));
        SetStatus("Antigravity Dev thinking…");
        Process? proc = null;
        string? hooksMissing = null;
        try
        {
            proc = Process.Start(psi);
            if (proc == null)
            {
                OnError?.Invoke("Failed to start the Antigravity CLI");
                yield break;
            }

            using (var stdin = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                string prompt = BuildPrompt(cwd, grant, ClaudeDevProvider.TurnAddDirs(extras), ClaudeDevProvider.BuildTranscript(history),
                    userMessage, CodeCommentPolicy.InstructionFor(_settingsService.Settings, cwd));
                await stdin.WriteLineAsync(StdinLine(prompt));
            }   // closing stdin ends the session after this one turn

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(worker ? TimeSpan.FromMinutes(95) : TimeSpan.FromMinutes(12));
            var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
            string? line;
            bool any = false;
            while ((line = await ReadLineOrNullAsync(proc.StandardOutput, timeout.Token)) != null)
            {
                var claudeLines = translator.Translate(line);
                // Fail closed: the model is starting and agy never ran the PreInvocation hook = the guard is not loaded.
                if (translator.ModelStarted && !hooksSeen && !(hooksSeen = File.Exists(marker)))
                {
                    hooksMissing = "agy did not load CluadeX's guard hooks";
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    break;
                }
                foreach (var claudeLine in claudeLines)
                {
                    try { sink?.Invoke(claudeLine); } catch { }
                }
                if (sink == null && translator.TakeText() is { Length: > 0 } text)
                {
                    any = true;
                    SetStatus("Antigravity Dev working…");
                    yield return text;
                }
                else if (sink != null)
                {
                    translator.TakeText();   // the rows carry it
                    if (translator.HasOutput && !any) { any = true; SetStatus("Antigravity Dev working…"); }
                }
            }
            if (hooksMissing != null)
            {
                OnError?.Invoke("Antigravity Dev: " + hooksMissing);
                SetStatus("Ready");
                yield return "⛔ **หยุด Antigravity Dev ก่อนเริ่มทำงาน** — agy ไม่ได้โหลดตัวกันความปลอดภัยของ CluadeX (hook) "
                    + "จึงไม่ปล่อยให้รันต่อโดยไม่มีตัวกัน\n\nลองอัปเดต agy (รันคำสั่งติดตั้งซ้ำใน PowerShell: `" + InstallCommand + "`) "
                    + "แล้วกด **ลองใหม่** ระหว่างนี้ใช้โมเดลอื่นในกล่องเลือกโมเดลได้";
                yield break;
            }
            foreach (var claudeLine in translator.Flush())
            {
                try { sink?.Invoke(claudeLine); } catch { }
            }
            if (sink == null && translator.TakeText() is { Length: > 0 } rest) { any = true; yield return rest; }
            await proc.WaitForExitAsync(timeout.Token);

            string err = (await SafeAwait(stderrTask)).Trim();
            string? refusal = translator.ResultIsError ? translator.ResultText : null;
            if (refusal == null && !any && proc.ExitCode != 0) refusal = GeminiDevProvider.LastMeaningfulLine(err);
            if (refusal == null && !any && LooksLikeAuthRefusal(err)) refusal = GeminiDevProvider.LastMeaningfulLine(err);
            if (!any || (refusal != null && LooksLikeRefusal(refusal)))
            {
                string? explained = refusal != null ? await ExplainAuthFailureAsync(refusal, ct) : null;
                LastTurnAuthRefused = explained != null;
                if (sink == null || !any)
                    yield return explained
                        ?? (string.IsNullOrWhiteSpace(refusal) ? "(antigravity-dev returned no output)" : $"antigravity-dev error: {Cap(refusal, 400)}");
            }
            else if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
                OnError?.Invoke($"antigravity-dev exit {proc.ExitCode}: {Cap(err, 400)}");
            SetStatus("Ready");
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            proc?.Dispose();
            TryDeleteDir(turnDir);
        }
    }

    /// <summary>The MCP servers this turn knows by name (the plugin's own, then the user's), for call_mcp_tool rows.</summary>
    private static List<string> ServerNames(string? chatToolsJson, bool brain, IEnumerable<string> userServers)
    {
        var names = new List<string>();
        try
        {
            if (!string.IsNullOrWhiteSpace(chatToolsJson) && JsonNode.Parse(chatToolsJson)?["mcpServers"] is JsonObject o)
                names.AddRange(o.Select(kv => kv.Key));
        }
        catch (JsonException) { }
        if (brain) names.Add(GeminiDevProvider.BrainServerName);
        return names;
    }

    private static void TryDeleteDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private static string Cap(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    // ─── Sign-in ───

    public bool LastTurnAuthRefused { get; private set; }

    public bool LooksLikeRefusal(string? text) => LooksLikeAuthRefusal(text);

    /// <summary>agy's refusals for no sign-in, an expired one, or a used-up plan limit. The sign-in strings are agy
    /// 1.2.7's own ("Print mode: not logged in and no controlling terminal; cannot complete interactive login",
    /// "You are not logged into Antigravity", "Print mode: auth error/auth timed out") and the docs' "authentication
    /// required".</summary>
    public static bool LooksLikeAuthRefusal(string? text) => text != null && (
        text.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
        || text.Contains("interactive login", StringComparison.OrdinalIgnoreCase)
        || text.Contains("auth error", StringComparison.OrdinalIgnoreCase)
        || text.Contains("auth timed out", StringComparison.OrdinalIgnoreCase)
        || text.Contains("authenticat", StringComparison.OrdinalIgnoreCase)
        || text.Contains("OAuth", StringComparison.OrdinalIgnoreCase)
        || IsLimitRefusal(text));

    private static bool IsLimitRefusal(string text) =>
        text.Contains("RESOURCE_EXHAUSTED", StringComparison.Ordinal)
        || text.Contains("quota", StringComparison.OrdinalIgnoreCase)
        || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
        || text.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
        || text.Contains("weekly limit", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(text, @"\b429\b");

    /// <summary>What to tell the user about a refusal. Pure, so the harness checks the wording. Null = not a refusal.</summary>
    public static string? AuthGuidance(string refusal, bool signedIn)
    {
        if (!LooksLikeAuthRefusal(refusal)) return null;
        string raw = Cap(refusal.Trim().Replace('\n', ' '), 300);
        if (IsLimitRefusal(refusal) && signedIn)
            return $"⚠ **Antigravity Dev ใช้โควตาของแพ็กเกจหมด หรือโดนจำกัดความถี่ชั่วคราว**\n\nข้อความจาก agy: `{raw}`\n\n"
                 + "บัญชี Google ที่ล็อกอินใน agy ใช้สิทธิ์ของรอบนี้หมด (Google AI Pro มีโควตารายสัปดาห์ — ดูได้ด้วย `/usage` ในหน้าต่าง agy) "
                 + "หรือส่งถี่เกินไป — รอสักครู่แล้วกด **ลองใหม่** หรือเปลี่ยนไปใช้โมเดลอื่นในกล่องเลือกโมเดลชั่วคราว";
        return $"🔑 **Antigravity Dev ยังไม่ได้ล็อกอิน (หรือการล็อกอินหมดอายุ)**\n\nข้อความจาก agy: `{raw}`\n\n"
             + "Antigravity Dev ใช้บัญชี Google ที่ล็อกอินใน Antigravity CLI (`agy`) บนเครื่องนี้ ไม่ต้องใช้ API key — บัญชีส่วนตัวที่สมัคร Google AI Pro ใช้ได้\n\n"
             + "**วิธีแก้ (ครั้งเดียวจบ):**\n"
             + "1. พิมพ์ `/login` ในช่องแชทนี้ — CluadeX จะเปิดหน้าต่าง agy ให้ (หรือเปิดเทอร์มินัลแล้วรัน `agy`)\n"
             + "2. เบราว์เซอร์จะเปิดหน้าล็อกอินของ Google — เลือกบัญชีที่สมัคร Google AI Pro แล้วยืนยัน\n"
             + "3. เมื่อ agy ขึ้นหน้าแชทแล้ว ปิดหน้าต่างนั้นได้เลย กลับมากด **ลองใหม่** — ไม่ต้องรีสตาร์ท CluadeX";
    }

    public async Task<string?> ExplainAuthFailureAsync(string? refusal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refusal) || !LooksLikeAuthRefusal(refusal)) return null;
        return AuthGuidance(refusal, await IsLoggedInAsync(ct));
    }

    private DateTime _signedInAt = DateTime.MinValue;

    /// <summary>agy keeps its token in Windows Credential Manager — no file says "signed in". The check is agy's own
    /// <c>--print /usage</c> (answers without a model turn, ~6 s): SUCCESS = signed in. A yes is remembered for a minute.</summary>
    public async Task<bool> IsLoggedInAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _signedInAt < TimeSpan.FromMinutes(1)) return true;
        if (_exe == null) await InitializeAsync(ct);
        if (_exe is not { } exe) return false;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetTempPath(),
            };
            foreach (var a in new[] { "--output-format", "json", "--print", "/usage" }) psi.ArgumentList.Add(a);
            psi.Environment["NO_COLOR"] = "1";
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardInput.Close();   // no terminal: a signed-out agy refuses instead of waiting for a login
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            string text = await p.StandardOutput.ReadToEndAsync(timeout.Token);
            try { await p.WaitForExitAsync(timeout.Token); } finally { try { if (!p.HasExited) p.Kill(true); } catch { } }
            bool ok = UsageSaysSignedIn(text);
            if (ok) _signedInAt = DateTime.UtcNow;
            return ok;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    /// <summary>agy's <c>--output-format json --print /usage</c> answer means signed in. Pure.</summary>
    public static bool UsageSaysSignedIn(string json)
    {
        try
        {
            return JsonNode.Parse(json.Trim()) is JsonObject o && o["status"]?.GetValue<string>() == "SUCCESS"
                && !LooksLikeAuthRefusal(o["error"]?.ToString());
        }
        catch { return false; }
    }

    /// <summary>A console window running agy interactively: with no saved sign-in it opens Google's page in the browser —
    /// the click there is the user's. CluadeX never types a password.</summary>
    public bool OpenLoginTerminal()
    {
        try
        {
            _signedInAt = DateTime.MinValue;
            string cmd = _exe is { } e ? $"\"{e}\"" : "agy";
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k {cmd}")
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });
            return true;
        }
        catch { return false; }
    }

    // ─── The rest of IAiProvider ───

    private static async Task<string?> ReadLineOrNullAsync(StreamReader reader, CancellationToken ct)
    {
        try { return await reader.ReadLineAsync(ct); }
        catch (OperationCanceledException) { return null; }
    }

    private static async Task<string> SafeAwait(Task<string> t)
    {
        try { return await t; } catch { return ""; }
    }

    public async Task<string> GenerateAsync(
        List<ChatMessage> history, string userMessage, string? systemPrompt = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in ChatAsync(history, userMessage, systemPrompt, ct))
            sb.Append(chunk);
        return sb.ToString().Trim();
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (_exe is not { } exe) return (false, $"Antigravity CLI not found (PowerShell: {InstallCommand})");
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi)!;
            string v = (await p.StandardOutput.ReadToEndAsync(ct)).Trim();
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) return (false, $"exit {p.ExitCode}");
            bool signedIn = await IsLoggedInAsync(ct);
            if (signedIn) _ = ListModelsAsync(CancellationToken.None);
            return signedIn
                ? (true, $"Antigravity CLI {v}")
                : (false, $"Antigravity CLI {v} — not signed in yet: run `agy` once in a terminal and sign in with Google");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private void SetStatus(string s) { _statusMessage = s; OnStatusChanged?.Invoke(s); }
    private void SetLoading(bool b) { _isLoading = b; OnLoadingChanged?.Invoke(b); }

    public void Dispose() { }
}

/// <summary>
/// agy stream-json → Claude Code stream-json, line by line, so <see cref="ClaudeDevStreamTurn"/> renders an Antigravity
/// Dev turn exactly as it renders Claude Dev. agy's events (docs/cli/headless, recorded on 1.2.7): <c>init</c>
/// {conversation_id, init:{cwd, tools, permission_mode, model?}}; <c>step_update</c> {step_update:{step_index, state
/// ACTIVE|DONE|ERROR, step_type user_input|agent_response|tool|checkpoint|system_message, text_delta?, tool_name?,
/// tool_info:{name, parameters, output?, error?:{type,message}}}}; <c>result</c> {result:{status, response, error?,
/// num_turns, usage, denied_actions?}}. Text arrives as deltas and is released at paragraph breaks and before each tool
/// call (the renderer joins text blocks with a blank line). Pure: the harness feeds it recordings.
/// </summary>
public sealed class AntigravityStreamTranslator
{
    private readonly string _plugin;
    private readonly IReadOnlyList<string> _servers;
    private readonly StringBuilder _pendingText = new();
    private readonly StringBuilder _plainText = new();
    private readonly HashSet<int> _startedTools = new();
    private readonly HashSet<int> _doneTools = new();
    private int _message = 1;
    private bool _afterTools;

    public AntigravityStreamTranslator(string plugin = AntigravityDevProvider.PluginName, IEnumerable<string>? pluginServers = null)
    {
        _plugin = plugin;
        _servers = (pluginServers ?? Array.Empty<string>()).OrderByDescending(s => s.Length).ToList();
    }

    public string? Model { get; private set; }
    public string? ConversationId { get; private set; }
    public bool ResultIsError { get; private set; }
    public string? ResultText { get; private set; }
    public bool Done { get; private set; }
    public bool HasOutput { get; private set; }
    /// <summary>The model has begun a step (text or a tool) — from here on agy's hooks must have run.</summary>
    public bool ModelStarted { get; private set; }

    public string TakeText()
    {
        string s = _plainText.ToString();
        _plainText.Clear();
        return s;
    }

    public List<string> Translate(string line)
    {
        var output = new List<string>();
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{') return output;
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return output; }
        if (node is not JsonObject ev) return output;
        switch (Str(ev, "event"))
        {
            case "init":
            {
                var init = ev["init"] as JsonObject;
                ConversationId = Str(ev, "conversation_id");
                Model = Str(init, "model");
                // agy names the model only when --model picked one; its effort is part of the slug (no --effort sent).
                LiveModelReport.Sent(AiProviderType.AntigravityDev, Model, null, false);
                output.Add(Json(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["model"] = Model ?? "" }));
                break;
            }
            case "step_update":
                if (ev["step_update"] is JsonObject step) OnStep(step, output);
                break;
            case "result":
            {
                var r = ev["result"] as JsonObject;
                ReleaseParagraphs(output, final: true);
                Done = true;
                string status = Str(r, "status") ?? "";
                ResultIsError = status != "SUCCESS";
                ResultText = ResultIsError ? Str(r, "error") is { Length: > 0 } e ? e : $"agy ended with status {status}" : null;
                var result = new JsonObject { ["type"] = "result", ["is_error"] = ResultIsError };
                if (ResultText != null) result["result"] = ResultText;
                if (r?["num_turns"] is JsonValue turns) result["num_turns"] = turns.DeepClone();
                output.Add(Json(result));
                break;
            }
        }
        return output;
    }

    private void OnStep(JsonObject step, List<string> output)
    {
        string type = Str(step, "step_type") ?? "";
        string state = Str(step, "state") ?? "";
        int index = step["step_index"] is JsonValue iv && iv.TryGetValue(out int i) ? i : -1;
        switch (type)
        {
            case "agent_response":
            {
                ModelStarted = true;
                string text = Str(step, "text_delta") ?? "";
                if (text.Length == 0) break;
                if (_afterTools) { _message++; _afterTools = false; }
                HasOutput = true;
                _plainText.Append(text);
                _pendingText.Append(text);
                ReleaseParagraphs(output, final: false);
                break;
            }
            case "tool":
            {
                ModelStarted = true;
                var info = step["tool_info"] as JsonObject;
                string name = Str(step, "tool_name") ?? Str(info, "name") ?? "tool";
                string id = "agy-step-" + index;
                if (_startedTools.Add(index))
                {
                    ReleaseParagraphs(output, final: true);
                    if (_afterTools) { _message++; _afterTools = false; }
                    HasOutput = true;
                    var (mapped, input) = MapTool(name, info?["parameters"] as JsonObject);
                    output.Add(Json(new JsonObject
                    {
                        ["type"] = "assistant",
                        ["message"] = new JsonObject
                        {
                            ["id"] = MessageId,
                            ["content"] = new JsonArray { new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = mapped, ["input"] = input } },
                        },
                    }));
                }
                if (state is "DONE" or "ERROR" && _doneTools.Add(index))
                {
                    _afterTools = true;
                    string? error = Str(info?["error"] as JsonObject, "message");
                    bool failed = state == "ERROR" || error != null;
                    string body = failed ? error ?? Str(info, "output") ?? "Tool error" : Str(info, "output") ?? "";
                    output.Add(Json(new JsonObject
                    {
                        ["type"] = "user",
                        ["message"] = new JsonObject
                        {
                            ["content"] = new JsonArray
                            {
                                new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = body, ["is_error"] = failed },
                            },
                        },
                    }));
                }
                break;
            }
        }
    }

    public List<string> Flush()
    {
        var output = new List<string>();
        ReleaseParagraphs(output, final: true);
        return output;
    }

    private string MessageId => "agy-msg-" + _message;

    private void ReleaseParagraphs(List<string> output, bool final)
    {
        string held = _pendingText.ToString();
        int cut = final ? held.Length : held.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (cut <= 0) { if (final) _pendingText.Clear(); return; }
        string release = held[..cut];
        string keep = final ? "" : held[(cut + 2)..];
        _pendingText.Clear().Append(keep);
        foreach (var para in release.Split("\n\n"))
        {
            if (string.IsNullOrWhiteSpace(para)) continue;
            output.Add(Json(new JsonObject
            {
                ["type"] = "assistant",
                ["message"] = new JsonObject
                {
                    ["id"] = MessageId,
                    ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = para } },
                },
            }));
        }
    }

    /// <summary>An agy tool call as the Claude Code tool the renderer knows. Argument names: view_file AbsolutePath,
    /// write_to_file TargetFile, run_command CommandLine, call_mcp_tool ServerName/ToolName/Arguments (recorded on
    /// 1.2.7); the rest (list_dir DirectoryPath, grep_search Query/SearchPath, find_by_name Pattern/SearchDirectory,
    /// replace_file_content TargetContent/ReplacementContent, read_url_content Url) as Antigravity names them, with
    /// fallbacks — an unknown argument only costs the row its detail.</summary>
    public (string Name, JsonObject Input) MapTool(string agyName, JsonObject? p)
    {
        p ??= new JsonObject();
        string? S(params string[] keys)
        {
            foreach (var k in keys) if (Str(p, k) is { Length: > 0 } v) return v;
            return null;
        }
        JsonObject Obj(params (string Key, string? Value)[] kv)
        {
            var o = new JsonObject();
            foreach (var (k, v) in kv) if (!string.IsNullOrEmpty(v)) o[k] = v;
            return o;
        }
        string? file = S("TargetFile", "AbsolutePath", "FilePath", "file_path");
        switch (agyName)
        {
            case "run_command": return ("Bash", Obj(("command", S("CommandLine", "command")), ("description", S("Description", "description"))));
            case "view_file": return ("Read", Obj(("file_path", file), ("offset", S("StartLine", "offset")), ("limit", S("Limit", "limit"))));
            case "write_to_file": return ("Write", Obj(("file_path", file), ("content", S("CodeContent", "Content", "content"))));
            case "replace_file_content" or "multi_replace_file_content":
                return ("Edit", Obj(("file_path", file), ("old_string", S("TargetContent", "old_string")), ("new_string", S("ReplacementContent", "new_string"))));
            case "list_dir": return ("LS", Obj(("path", S("DirectoryPath", "path"))));
            case "grep_search": return ("Grep", Obj(("pattern", S("Query", "pattern")), ("path", S("SearchPath", "SearchDirectory", "path"))));
            case "find_by_name": return ("Glob", Obj(("pattern", S("Pattern", "pattern")), ("path", S("SearchDirectory", "path"))));
            case "read_url_content": return ("WebFetch", Obj(("url", S("Url", "url"))));
            case "search_web": return ("WebSearch", Obj(("query", S("query", "Query"))));
            case "ask_question": return ("AskUserQuestion", Obj(("question", S("Question", "question"))));
            case AntigravityGuard.McpTool:
            {
                string server = S("ServerName") ?? "mcp";
                // The plugin's servers come back as <plugin>_<server>: the chat knows them by their own names.
                foreach (var known in _servers)
                    if (server == _plugin + "_" + known) { server = known; break; }
                var input = p["Arguments"] is JsonObject a ? (JsonObject)a.DeepClone() : new JsonObject();
                return ($"mcp__{server}__{S("ToolName") ?? "tool"}", input);
            }
        }
        return (agyName, (JsonObject)p.DeepClone());
    }

    private static string? Str(JsonObject? o, string key)
    {
        if (o == null || !o.TryGetPropertyValue(key, out var v) || v == null) return null;
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue(out string? s)) return s;
            return jv.ToJsonString();
        }
        return v.ToJsonString();
    }

    private static string Json(JsonObject o) => o.ToJsonString();
}
