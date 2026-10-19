using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

/// <summary>
/// A complete agent CluadeX hands the whole turn to (Claude Dev, Gemini Dev): what the agent loop needs to heal a
/// signed-out CLI by itself — the refusal, "is it signed in now", and opening the sign-in.
/// </summary>
public interface IDelegatedCliProvider
{
    /// <summary>The last ChatAsync ended in a sign-in / quota refusal (its text is the guidance).</summary>
    bool LastTurnAuthRefused { get; }
    Task<bool> IsLoggedInAsync(CancellationToken ct);
    bool OpenLoginTerminal();
    /// <summary>The sentence that fixes a refusal, or null when <paramref name="refusal"/> is not one.</summary>
    Task<string?> ExplainAuthFailureAsync(string? refusal, CancellationToken ct);
    bool LooksLikeRefusal(string? text);
}

/// <summary>
/// "gemini-dev" — the Gemini CLI installed on this machine (npm @google/gemini-cli), exposed as a selectable CluadeX
/// model the way Claude Dev exposes Claude Code (owner 2026-10-19: "พัฒนาให้ใช้กับ Gemini ได้ด้วย … ทำให้รองรับให้หมด").
/// No API key: the CLI signs in with the user's Google account (run `gemini` once in a terminal).
///
/// The same decisions as <see cref="ClaudeDevProvider"/>, which this mirrors:
/// - The CLI is its own agent: one delegated call per turn (CodeAgentService.RunDelegatedTurnAsync), never wrapped in
///   CluadeX's loops. The edit/run grant comes from the user's rules (<see cref="ClaudeDevProvider.Grant"/> — shared),
///   and maps onto the CLI's --approval-mode: none = default (headless, so every tool that needs approval is refused —
///   read-only advice), edit = auto_edit, run = yolo.
/// - Started as node + gemini.js, never through npm's gemini.cmd: a .cmd runs under cmd.exe, which re-parses the prompt
///   (BrainX's broker learned this, 2026-10-16). Arguments go through ArgumentList; the conversation goes over stdin.
/// - Gemini's stream-json (init / message / tool_use / tool_result / error / result) is translated into Claude Code's
///   stream-json shape (<see cref="GeminiStreamTranslator"/>), so the chat's tool rows, plan, diffs and steps come from
///   the one renderer that already works (<see cref="ClaudeDevStreamTurn"/>) — not a second, half-tested copy.
/// - CluadeX's safety guard rides along as a BeforeTool hook (<c>CluadeX.exe --cx-guard</c>, the same exe Claude Dev
///   uses) through a per-turn system settings file (GEMINI_CLI_SYSTEM_SETTINGS_PATH), together with CluadeX's chat
///   tools and the BrainX brain as MCP servers. --skip-trust: an untrusted folder runs without its MCP servers.
/// </summary>
public class GeminiDevProvider : IAiProvider, IDelegatedCliProvider
{
    private readonly SettingsService _settingsService;
    private readonly FileSystemService _fileSystemService;

    private (string Node, string Script)? _cli;
    private string _statusMessage = "Not initialised";
    private bool _isLoading;

    public const string Id = "GeminiDev";
    public string ProviderId => Id;
    public string DisplayName => "Gemini Dev (Gemini CLI)";
    public bool IsReady => _cli != null;
    /// <summary>node.exe and the CLI's script, once found.</summary>
    public (string Node, string Script)? Cli => _cli;
    public bool IsLoading => _isLoading;
    public string StatusMessage => _statusMessage;

    public event Action<string>? OnStatusChanged;
    public event Action<bool>? OnLoadingChanged;
    public event Action<string>? OnError;

    /// <summary>The models offered in the chat's model box. "default" sends no -m (the CLI's own pick, which follows
    /// what the signed-in account may use); the aliases are the CLI's own.</summary>
    public static readonly string[] KnownModels = ["default", "pro", "flash", "flash-lite"];

    public GeminiDevProvider(SettingsService settingsService, FileSystemService fileSystemService)
    {
        _settingsService = settingsService;
        _fileSystemService = fileSystemService;
    }

    // ─── Finding the CLI ───

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_cli != null) return;
        SetLoading(true);
        try
        {
            _cli = await ResolveCliAsync(ct);
            SetStatus(_cli is { } c ? $"Ready · {c.Script}" : "Gemini CLI not found — install it with `npm i -g @google/gemini-cli`");
        }
        finally { SetLoading(false); }
    }

    /// <summary>Is the Gemini CLI on this machine (cheap; for the chat's model box)?</summary>
    public static bool IsInstalled() => FindCli(WhereLines("gemini"), WhereLines("node"),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), File.Exists) != null;

    private static async Task<(string Node, string Script)?> ResolveCliAsync(CancellationToken ct)
    {
        var gemini = await Task.Run(() => WhereLines("gemini"), ct);
        var node = await Task.Run(() => WhereLines("node"), ct);
        return FindCli(gemini, node, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), File.Exists);
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

    /// <summary>
    /// node.exe and the CLI's script, from `where gemini` / `where node` and the known places: the package folder next to
    /// npm's gemini shim (newer builds ship bundle\gemini.js, older ones dist\index.js), else the default npm prefix under
    /// %APPDATA%; node from PATH, else Program Files\nodejs. Pure apart from <paramref name="fileExists"/>.
    /// </summary>
    public static (string Node, string Script)? FindCli(IEnumerable<string> whereGemini, IEnumerable<string> whereNode,
        string appData, string programFiles, Func<string, bool> fileExists)
    {
        string? ScriptIn(string npmDir)
        {
            foreach (var rel in new[] { @"node_modules\@google\gemini-cli\bundle\gemini.js", @"node_modules\@google\gemini-cli\dist\index.js" })
            {
                string p = Path.Combine(npmDir, rel);
                if (fileExists(p)) return p;
            }
            return null;
        }

        string? script = null;
        foreach (var line in whereGemini)
        {
            string? dir = Path.GetDirectoryName(line);
            if (!string.IsNullOrEmpty(dir) && ScriptIn(dir) is { } s) { script = s; break; }
        }
        script ??= ScriptIn(Path.Combine(appData, "npm"));
        if (script == null) return null;

        string? node = whereNode.FirstOrDefault(l => l.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase) && fileExists(l));
        if (node == null)
        {
            string pf = Path.Combine(programFiles, "nodejs", "node.exe");
            if (fileExists(pf)) node = pf;
        }
        return node == null ? null : (node, script);
    }

    // ─── One turn ───

    /// <summary>The CLI's arguments for one call (after the script path). Pure, so the harness checks them.</summary>
    public static List<string> BuildArgs(string cwd, ClaudeDevProvider.WorkerGrant? grant, string model, IReadOnlyList<string>? addDirs,
        string? codeComments = null)
    {
        bool worker = grant is { Edit: true } || grant is { Run: true };
        addDirs ??= Array.Empty<string>();
        string folders = addDirs.Count == 0 ? ""
            : "The user also allowed these folders beside the project — work in them by absolute path: " + string.Join(", ", addDirs) + ". ";
        string wrapper = worker
            ? "You are 'gemini-dev', the coding agent of a CluadeX chat, working directly in the project. " +
              $"Project working directory: {cwd}. " +
              (grant!.Edit ? "You may create and edit files inside it. " : "You may not edit files: propose changes as diffs. ") +
              (grant.Run ? "You may run commands (builds, tests, git) in it. " : "You may not run commands. ") +
              "Keep a short plan with write_todos for a task of several steps. " + folders +
              "Stay inside the project folder" + (addDirs.Count > 0 ? " and those folders" : "") + " unless the user's request names another. " +
              "Other agents on this machine are reachable through the brainx-brain tools (task_queue, task_handoff, cowork_*). " +
              "Answer in Markdown (code in fenced blocks) and in the language the user writes in."
            : "You are 'gemini-dev', answering inside the CluadeX desktop app. " +
              $"Project working directory: {cwd}. " +
              "You have read-only tools; propose file edits as diffs/snippets for the app to apply, don't try to write. " + folders +
              "Answer in Markdown (code in fenced blocks) and in the language the user writes in.";
        // The user's AI-comment instruction (CodeCommentPolicy); ArgumentList carries the line breaks as they are.
        if (!string.IsNullOrWhiteSpace(codeComments)) wrapper += "\n\n" + codeComments.Trim();

        var args = new List<string>
        {
            "--output-format", "stream-json",
            "--skip-trust",
            "--approval-mode", grant is { Run: true } ? "yolo" : grant is { Edit: true } ? "auto_edit" : "default",
        };
        if (!string.IsNullOrWhiteSpace(model)
            && !model.Equals("default", StringComparison.OrdinalIgnoreCase)
            && !model.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--model");
            args.Add(model.Trim());
        }
        if (addDirs.Count > 0)
        {
            args.Add("--include-directories");
            args.Add(string.Join(",", addDirs));
        }
        // -p is appended to what arrives on stdin (the conversation): the wrapper ends the prompt.
        args.Add("-p");
        args.Add("\n\n[CluadeX] " + wrapper);
        return args;
    }

    /// <summary>
    /// The system-scope settings for one turn (GEMINI_CLI_SYSTEM_SETTINGS_PATH): CluadeX's guard as a BeforeTool hook on
    /// the shell and file-writing tools, and the MCP servers — CluadeX's chat tools (from the ChatToolBridge config) and
    /// the BrainX brain when the user's own Gemini settings do not already have it (copied from Claude Code's
    /// registration). Pure: the caller writes it.
    /// </summary>
    public static JsonObject BuildSystemSettings(string guardExe, string? chatToolsConfigJson, string? brainServerJson, bool guard)
    {
        var settings = new JsonObject();
        if (guard)
        {
            settings["hooksConfig"] = new JsonObject { ["enabled"] = true };
            settings["hooks"] = new JsonObject
            {
                ["BeforeTool"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["matcher"] = "run_shell_command|write_file|replace",
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["name"] = "cluadex-guard",
                                ["type"] = "command",
                                ["command"] = $"\"{guardExe}\" {Safety.GuardHook.Flag}",
                                // Milliseconds here (Claude Code counts seconds): long enough to answer the red prompt.
                                ["timeout"] = 300_000,
                            },
                        },
                    },
                },
            };
        }
        var servers = new JsonObject();
        if (!string.IsNullOrWhiteSpace(chatToolsConfigJson))
        {
            try
            {
                if (JsonNode.Parse(chatToolsConfigJson)?["mcpServers"] is JsonObject fromBridge)
                    foreach (var kv in fromBridge.ToList())
                        if (kv.Value != null) servers[kv.Key] = kv.Value.DeepClone();
            }
            catch (JsonException) { }
        }
        if (!string.IsNullOrWhiteSpace(brainServerJson))
        {
            try
            {
                if (JsonNode.Parse(brainServerJson) is JsonObject brain)
                {
                    brain.Remove("type");   // Claude Code's "stdio" marker; Gemini infers the transport from command
                    servers[BrainServerName] = brain;
                }
            }
            catch (JsonException) { }
        }
        if (servers.Count > 0) settings["mcpServers"] = servers;
        return settings;
    }

    public const string BrainServerName = "brainx-brain";

    /// <summary>The MCP server names this turn gives the CLI (for turning mcp_server_tool back into mcp__server__tool).</summary>
    private static IReadOnlyList<string> ServerNamesOf(JsonObject settings, IEnumerable<string> userServers) =>
        (settings["mcpServers"] as JsonObject)?.Select(kv => kv.Key).Concat(userServers).Distinct().ToList()
        ?? userServers.Distinct().ToList();

    /// <summary>~/.gemini/settings.json (the user's own Gemini CLI settings), or null.</summary>
    private static JsonObject? ReadUserSettings()
    {
        try
        {
            string path = Path.Combine(GeminiHome(), "settings.json");
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch { return null; }
    }

    private static string GeminiHome() =>
        Environment.GetEnvironmentVariable("GEMINI_CLI_HOME") is { Length: > 0 } h
            ? Path.Combine(h, ".gemini")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");

    /// <summary>brainx-brain as Claude Code registers it in ~/.claude.json, or null.</summary>
    private static string? BrainServerFromClaude()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            if (!File.Exists(path)) return null;
            return (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?["mcpServers"]?[BrainServerName]?.ToJsonString();
        }
        catch { return null; }
    }

    public async IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        LastTurnAuthRefused = false;
        if (_cli == null) await InitializeAsync(ct);
        if (_cli is not { } cli)
        {
            OnError?.Invoke("Gemini CLI not found on this machine");
            yield return "ไม่พบ Gemini CLI บนเครื่องนี้ — ติดตั้งด้วย `npm i -g @google/gemini-cli` แล้วรัน `gemini` หนึ่งครั้งเพื่อล็อกอินด้วยบัญชี Google";
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

        // The turn's system settings: the guard (worker turns), CluadeX's chat tools, the brain.
        var user = ReadUserSettings();
        var userServers = (user?["mcpServers"] as JsonObject)?.Select(kv => kv.Key).ToList() ?? new List<string>();
        string? chatToolsJson = null;
        if (!string.IsNullOrWhiteSpace(extras?.ToolsConfig))
            try { chatToolsJson = await File.ReadAllTextAsync(extras.ToolsConfig, ct); } catch { chatToolsJson = null; }
        string? brain = userServers.Contains(BrainServerName) ? null : BrainServerFromClaude();
        var systemSettings = BuildSystemSettings(Safety.GuardHook.ExePath(), chatToolsJson, brain, guard: worker);
        string? settingsPath = null;
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "cluadex-guard");
            Directory.CreateDirectory(dir);
            settingsPath = Path.Combine(dir, $"gemini-dev-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(settingsPath, systemSettings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false), ct);
        }
        catch { settingsPath = null; }
        if (worker && settingsPath == null)
        {
            // No settings file = no guard: the turn runs without the shell rather than unguarded.
            grant = grant! with { Run = false };
        }

        var psi = new ProcessStartInfo(cli.Node)
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
        psi.ArgumentList.Add(cli.Script);
        foreach (var a in BuildArgs(cwd, grant, model, extras?.AddDirs,
                     CodeCommentPolicy.InstructionFor(_settingsService.Settings, cwd))) psi.ArgumentList.Add(a);
        if (settingsPath != null) psi.Environment["GEMINI_CLI_SYSTEM_SETTINGS_PATH"] = settingsPath;
        if (grant is { Run: true, Edit: false }) psi.Environment[Safety.GuardHook.NoEditEnv] = "1";
        psi.Environment["NO_COLOR"] = "1";

        var translator = new GeminiStreamTranslator(ServerNamesOf(systemSettings, userServers));
        SetStatus("Gemini Dev thinking…");
        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc == null)
            {
                OnError?.Invoke("Failed to start the Gemini CLI");
                yield break;
            }

            using (var stdin = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                string transcript = ClaudeDevProvider.BuildTranscript(history);
                if (transcript.Length > 0)
                {
                    await stdin.WriteLineAsync("<conversation_so_far>");
                    await stdin.WriteLineAsync(transcript);
                    await stdin.WriteLineAsync("</conversation_so_far>");
                    await stdin.WriteLineAsync();
                }
                await stdin.WriteLineAsync(userMessage);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(worker ? TimeSpan.FromMinutes(90) : TimeSpan.FromSeconds(600));
            var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
            string? line;
            bool any = false;
            while ((line = await ReadLineOrNullAsync(proc.StandardOutput, timeout.Token)) != null)
            {
                foreach (var claudeLine in translator.Translate(line))
                {
                    try { sink?.Invoke(claudeLine); } catch { }
                }
                if (sink == null && translator.TakeText() is { Length: > 0 } text)
                {
                    any = true;
                    SetStatus("Gemini Dev working…");
                    yield return text;
                }
                else if (sink != null)
                {
                    translator.TakeText();   // the rows carry it
                    if (translator.HasOutput && !any) { any = true; SetStatus("Gemini Dev working…"); }
                }
            }
            foreach (var claudeLine in translator.Flush())
            {
                try { sink?.Invoke(claudeLine); } catch { }
            }
            if (sink == null && translator.TakeText() is { Length: > 0 } rest) { any = true; yield return rest; }
            await proc.WaitForExitAsync(timeout.Token);

            string err = (await SafeAwait(stderrTask)).Trim();
            string? refusal = translator.ResultIsError ? translator.ResultText : null;
            if (refusal == null && !any && proc.ExitCode != 0) refusal = LastMeaningfulLine(err);
            if (!any || (refusal != null && LooksLikeRefusal(refusal)))
            {
                string? explained = refusal != null ? await ExplainAuthFailureAsync(refusal, ct) : null;
                LastTurnAuthRefused = explained != null;
                if (sink == null || !any)
                    yield return explained
                        ?? (string.IsNullOrWhiteSpace(refusal) ? "(gemini-dev returned no output)" : $"gemini-dev error: {Cap(refusal, 400)}");
            }
            else if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
                OnError?.Invoke($"gemini-dev exit {proc.ExitCode}: {Cap(err, 400)}");
            SetStatus("Ready");
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            proc?.Dispose();
            if (settingsPath != null) try { File.Delete(settingsPath); } catch { }
        }
    }

    private static string Cap(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    /// <summary>The CLI's last line on stderr that says something (it prints a stack under the message).</summary>
    private static string? LastMeaningfulLine(string stderr) =>
        stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("at ", StringComparison.Ordinal));

    // ─── Sign-in ───

    public bool LastTurnAuthRefused { get; private set; }

    public bool LooksLikeRefusal(string? text) => LooksLikeAuthRefusal(text);

    /// <summary>The CLI's refusals for no sign-in, an expired sign-in, or a used-up quota.</summary>
    public static bool LooksLikeAuthRefusal(string? text) => text != null && (
        text.Contains("Auth method", StringComparison.OrdinalIgnoreCase)
        || text.Contains("GEMINI_API_KEY", StringComparison.Ordinal)
        || text.Contains("authenticat", StringComparison.OrdinalIgnoreCase)
        || text.Contains("login", StringComparison.OrdinalIgnoreCase)
        || text.Contains("OAuth", StringComparison.OrdinalIgnoreCase)
        || text.Contains("credentials", StringComparison.OrdinalIgnoreCase)
        || text.Contains("RESOURCE_EXHAUSTED", StringComparison.Ordinal)
        || text.Contains("quota", StringComparison.OrdinalIgnoreCase)
        || text.Contains("429", StringComparison.Ordinal));

    /// <summary>What to tell the user about a refusal. Pure, so the harness checks the wording. Null = not a refusal.</summary>
    public static string? AuthGuidance(string refusal, bool signedIn)
    {
        if (!LooksLikeAuthRefusal(refusal)) return null;
        string raw = Cap(refusal.Trim().Replace('\n', ' '), 300);
        bool quota = refusal.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || refusal.Contains("RESOURCE_EXHAUSTED", StringComparison.Ordinal) || refusal.Contains("429", StringComparison.Ordinal);
        if (quota && signedIn)
            return $"⚠ **Gemini Dev ใช้โควตาหมด หรือโดนจำกัดความถี่ชั่วคราว**\n\nข้อความจาก Gemini CLI: `{raw}`\n\n"
                 + "บัญชี Google ที่ล็อกอินอยู่ใช้โควตาฟรีของวันนี้หมด หรือส่งถี่เกินไป — รอสักครู่แล้วกด **ลองใหม่** "
                 + "หรือเปลี่ยนไปใช้โมเดลอื่นในกล่องเลือกโมเดลชั่วคราว (เช่น Gemini API ที่มีคีย์ หรือ Claude Dev)";
        return $"🔑 **Gemini Dev ยังไม่ได้ล็อกอิน (หรือการล็อกอินหมดอายุ)**\n\nข้อความจาก Gemini CLI: `{raw}`\n\n"
             + "Gemini Dev ใช้บัญชี Google ของ Gemini CLI ในเครื่องนี้ ไม่ต้องใช้ API key\n\n"
             + "**วิธีแก้ (ครั้งเดียวจบ):**\n"
             + "1. พิมพ์ `/login` ในช่องแชทนี้ — CluadeX จะเปิดหน้าต่าง Gemini CLI ให้ (หรือเปิดเทอร์มินัลแล้วรัน `gemini`)\n"
             + "2. เลือก **Sign in with Google** แล้วยืนยันบัญชีในเบราว์เซอร์ที่เด้งขึ้นมา\n"
             + "3. ปิดหน้าต่างนั้นได้เลย แล้วกลับมากด **ลองใหม่** — ไม่ต้องรีสตาร์ท CluadeX";
    }

    public async Task<string?> ExplainAuthFailureAsync(string? refusal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refusal) || !LooksLikeAuthRefusal(refusal)) return null;
        return AuthGuidance(refusal, await IsLoggedInAsync(ct));
    }

    /// <summary>Signed in = the CLI's Google sign-in is cached, or an API key is in its environment.</summary>
    public Task<bool> IsLoggedInAsync(CancellationToken ct) => Task.FromResult(
        File.Exists(Path.Combine(GeminiHome(), "oauth_creds.json"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_API_KEY")));

    /// <summary>A console window running the CLI interactively: it asks how to sign in and opens Google's page in the
    /// browser — the click there is the user's. CluadeX never types a password.</summary>
    public bool OpenLoginTerminal()
    {
        try
        {
            var cli = _cli;
            string cmd = cli is { } c ? $"\"{c.Node}\" \"{c.Script}\"" : "gemini";
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
        if (_cli is not { } cli) return (false, "Gemini CLI not found (npm i -g @google/gemini-cli)");
        try
        {
            var psi = new ProcessStartInfo(cli.Node) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(cli.Script);
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi)!;
            string v = (await p.StandardOutput.ReadToEndAsync(ct)).Trim();
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) return (false, $"exit {p.ExitCode}");
            bool signedIn = await IsLoggedInAsync(ct);
            return signedIn
                ? (true, $"Gemini CLI {v}")
                : (false, $"Gemini CLI {v} — not signed in yet: run `gemini` once in a terminal and choose Sign in with Google");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private void SetStatus(string s) { _statusMessage = s; OnStatusChanged?.Invoke(s); }
    private void SetLoading(bool b) { _isLoading = b; OnLoadingChanged?.Invoke(b); }

    public void Dispose() { }
}

/// <summary>
/// Gemini CLI stream-json → Claude Code stream-json, line by line, so <see cref="ClaudeDevStreamTurn"/> renders a Gemini
/// Dev turn exactly as it renders Claude Dev: assistant text (buffered — Gemini streams deltas, the renderer joins
/// separate text blocks with a blank line, so text is released at paragraph breaks and before each tool call), tool_use
/// with Claude's tool names and argument names, tool_result, and the final result. Pure: the harness feeds it recordings.
/// </summary>
public sealed class GeminiStreamTranslator
{
    private readonly IReadOnlyList<string> _servers;
    private readonly StringBuilder _pendingText = new();
    private readonly StringBuilder _plainText = new();
    private int _message = 1;
    private bool _afterTools;

    public GeminiStreamTranslator(IEnumerable<string>? mcpServers = null) =>
        _servers = (mcpServers ?? Array.Empty<string>()).OrderByDescending(s => s.Length).ToList();

    public string? Model { get; private set; }
    public bool ResultIsError { get; private set; }
    public string? ResultText { get; private set; }
    public bool Done { get; private set; }
    public bool HasOutput { get; private set; }

    /// <summary>Plain reply text not yet taken (for a read-only turn, which streams text instead of rows).</summary>
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
        string type = ev["type"]?.GetValue<string>() ?? "";
        switch (type)
        {
            case "init":
                Model = Str(ev, "model");
                output.Add(Json(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["model"] = Model ?? "" }));
                break;
            case "message":
                if (Str(ev, "role") != "assistant") break;
                string text = Str(ev, "content") ?? "";
                if (text.Length == 0) break;
                if (_afterTools) { _message++; _afterTools = false; }
                HasOutput = true;
                _plainText.Append(text);
                _pendingText.Append(text);
                ReleaseParagraphs(output, final: false);
                break;
            case "tool_use":
            {
                ReleaseParagraphs(output, final: true);
                if (_afterTools) { _message++; _afterTools = false; }
                HasOutput = true;
                string id = Str(ev, "tool_id") ?? Guid.NewGuid().ToString("N");
                var (name, input) = MapTool(Str(ev, "tool_name") ?? "tool", ev["parameters"] as JsonObject);
                output.Add(Json(new JsonObject
                {
                    ["type"] = "assistant",
                    ["message"] = new JsonObject
                    {
                        ["id"] = MessageId,
                        ["content"] = new JsonArray { new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input } },
                    },
                }));
                break;
            }
            case "tool_result":
            {
                _afterTools = true;
                bool failed = Str(ev, "status") == "error";
                string body = failed ? Str(ev["error"] as JsonObject, "message") ?? Str(ev, "output") ?? "Tool error" : Str(ev, "output") ?? "";
                output.Add(Json(new JsonObject
                {
                    ["type"] = "user",
                    ["message"] = new JsonObject
                    {
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = Str(ev, "tool_id") ?? "", ["content"] = body, ["is_error"] = failed },
                        },
                    },
                }));
                break;
            }
            case "error":
                // A warning mid-stream (a loop detected, a blocked recitation): shown under the reply, the run goes on.
                if (Str(ev, "message") is { Length: > 0 } warn)
                {
                    _pendingText.Append("\n\n⚠ ").Append(warn);
                    _plainText.Append("\n\n⚠ ").Append(warn);
                }
                break;
            case "result":
            {
                ReleaseParagraphs(output, final: true);
                Done = true;
                ResultIsError = Str(ev, "status") == "error";
                ResultText = ResultIsError ? Str(ev["error"] as JsonObject, "message") ?? "Gemini CLI error" : null;
                var result = new JsonObject { ["type"] = "result", ["is_error"] = ResultIsError };
                if (ResultText != null) result["result"] = ResultText;
                if (ev["stats"] is JsonObject stats && stats["tool_calls"] is JsonValue calls) result["num_turns"] = calls.DeepClone();
                output.Add(Json(result));
                break;
            }
        }
        return output;
    }

    /// <summary>End of stream: whatever text is still held.</summary>
    public List<string> Flush()
    {
        var output = new List<string>();
        ReleaseParagraphs(output, final: true);
        return output;
    }

    private string MessageId => "gemini-msg-" + _message;

    /// <summary>Emit the held text up to its last paragraph break (all of it when <paramref name="final"/>): the renderer
    /// puts a blank line between text blocks, so a cut at "\n\n" joins back exactly.</summary>
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

    /// <summary>A Gemini tool call as the Claude Code tool the renderer knows (name and argument names).</summary>
    public (string Name, JsonObject Input) MapTool(string geminiName, JsonObject? p)
    {
        p ??= new JsonObject();
        string? S(string k) => Str(p, k);
        JsonObject Obj(params (string Key, string? Value)[] kv)
        {
            var o = new JsonObject();
            foreach (var (k, v) in kv) if (!string.IsNullOrEmpty(v)) o[k] = v;
            return o;
        }
        switch (geminiName)
        {
            case "run_shell_command": return ("Bash", Obj(("command", S("command")), ("description", S("description"))));
            case "read_file": return ("Read", Obj(("file_path", S("file_path") ?? S("absolute_path")), ("offset", S("start_line") ?? S("offset")), ("limit", S("limit"))));
            case "write_file": return ("Write", Obj(("file_path", S("file_path")), ("content", S("content"))));
            case "replace":
            {
                var o = Obj(("file_path", S("file_path")), ("old_string", S("old_string")), ("new_string", S("new_string")));
                if (p["allow_multiple"] is JsonValue am && am.TryGetValue(out bool all) && all) o["replace_all"] = true;
                return ("Edit", o);
            }
            case "glob": return ("Glob", Obj(("pattern", S("pattern")), ("path", S("dir_path") ?? S("path"))));
            case "grep_search": return ("Grep", Obj(("pattern", S("pattern")), ("path", S("dir_path") ?? S("path")), ("glob", S("include_pattern") ?? S("include"))));
            case "list_directory": return ("LS", Obj(("path", S("dir_path") ?? S("path"))));
            case "web_fetch": return ("WebFetch", Obj(("url", S("url")), ("prompt", S("prompt"))));
            case "google_web_search": return ("WebSearch", Obj(("query", S("query"))));
            case "ask_user": return ("AskUserQuestion", Obj(("question", S("question") ?? FirstQuestion(p))));
            case "invoke_agent": return ("Agent", Obj(("description", S("description") ?? S("prompt") ?? S("task")), ("subagent_type", S("agent_name") ?? S("name"))));
            case "write_todos":
            {
                var todos = new JsonArray();
                if (p["todos"] is JsonArray items)
                    foreach (var t in items.OfType<JsonObject>())
                        todos.Add(new JsonObject { ["content"] = Str(t, "description") ?? Str(t, "content") ?? "", ["status"] = Str(t, "status") ?? "pending" });
                return ("TodoWrite", new JsonObject { ["todos"] = todos });
            }
        }
        // mcp_<server>_<tool> → mcp__server__tool, for the servers this turn knows (longest name first: "brainx-brain"
        // before a hypothetical "brainx").
        if (geminiName.StartsWith("mcp_", StringComparison.Ordinal))
            foreach (var server in _servers)
            {
                string prefix = "mcp_" + server + "_";
                if (geminiName.StartsWith(prefix, StringComparison.Ordinal) && geminiName.Length > prefix.Length)
                    return ($"mcp__{server}__{geminiName[prefix.Length..]}", (JsonObject)p.DeepClone());
            }
        return (geminiName, (JsonObject)p.DeepClone());
    }

    private static string? FirstQuestion(JsonObject p) =>
        p["questions"] is JsonArray qs && qs.FirstOrDefault() is JsonObject q ? Str(q, "question") : null;

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
