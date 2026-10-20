using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

/// <summary>
/// "openai-dev" — OpenAI's Codex CLI on this machine, exposed as a selectable CluadeX model the way Claude Dev exposes
/// Claude Code (owner 2026-10-20: "อยากให้เพิ่ม openai DEV คือติดต่อตรงผ่าน codex"). No API key: the CLI uses the user's
/// ChatGPT sign-in (`codex login`).
///
/// The same decisions as <see cref="ClaudeDevProvider"/> and <see cref="GeminiDevProvider"/>:
/// - The CLI is its own agent: one delegated call per turn (CodeAgentService.RunDelegatedTurnAsync), `codex exec --json`
///   with the whole prompt on stdin ("-"), never through a .cmd shim (cmd.exe re-parses arguments).
/// - Codex's JSONL (thread / turn / item events) is translated into Claude Code's stream-json
///   (<see cref="CodexStreamTranslator"/>), so the chat's rows, plan, diffs and steps come from the one renderer
///   (<see cref="ClaudeDevStreamTurn"/>).
/// - Safety is Codex's own sandbox, chosen from the user's rules (<see cref="ClaudeDevProvider.Grant"/>): no grant =
///   read-only; the edit or run grant = workspace-write (writes only inside the project and the allowed folders; the
///   network only with the run grant). Never danger-full-access, never the approval bypass. The installed Codex has no
///   per-turn hook point for CluadeX's red prompt, so the sandbox is the line (a command can still change files inside
///   the project — the same reach as the edit grant).
/// - Found in two places: npm's @openai/codex (its native codex.exe under vendor\) and the Codex desktop app's own copy
///   (%LOCALAPPDATA%\OpenAI\Codex\bin\&lt;build&gt;\codex.exe — the newest build folder).
/// </summary>
public class OpenAIDevProvider : IAiProvider, IDelegatedCliProvider
{
    private readonly SettingsService _settingsService;
    private readonly FileSystemService _fileSystemService;

    private string? _exe;
    private string _statusMessage = "Not initialised";
    private bool _isLoading;

    public const string Id = "OpenAIDev";
    public string ProviderId => Id;
    public string DisplayName => "OpenAI Dev (Codex CLI)";
    public bool IsReady => _exe != null;
    public bool IsLoading => _isLoading;
    public string StatusMessage => _statusMessage;
    /// <summary>codex.exe, once found.</summary>
    public string? Exe => _exe;

    public event Action<string>? OnStatusChanged;
    public event Action<bool>? OnLoadingChanged;
    public event Action<string>? OnError;

    /// <summary>The models offered in the chat's model box: "default" sends no -m (Codex's own pick from its
    /// config.toml), then the models Codex lists for a ChatGPT account (its models_cache.json, 2026-10-20).</summary>
    public static readonly string[] KnownModels = ["default", "gpt-6.1-sol", "gpt-6-astra", "gpt-6-sol", "gpt-6-luna", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna"];

    public OpenAIDevProvider(SettingsService settingsService, FileSystemService fileSystemService)
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
            SetStatus(_exe != null ? $"Ready · {_exe}" : "Codex CLI not found — install the Codex app or `npm i -g @openai/codex`");
        }
        finally { SetLoading(false); }
    }

    /// <summary>Is the Codex CLI on this machine (cheap; for the chat's model box)?</summary>
    public static bool IsInstalled() => Locate() != null;

    private static string? Locate() => FindCli(WhereLines("codex"),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        File.Exists, NewestFirst);

    private static IEnumerable<string> NewestFirst(string dir)
    {
        try { return new DirectoryInfo(dir).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).Select(d => d.FullName).ToList(); }
        catch { return Array.Empty<string>(); }
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

    private const string NpmExe = @"node_modules\@openai\codex\vendor\x86_64-pc-windows-msvc\codex\codex.exe";

    /// <summary>
    /// codex.exe: a codex.exe on PATH; else the native binary npm installed beside its codex shim (or under the default
    /// npm prefix in %APPDATA%); else the Codex desktop app's copy in the newest of
    /// %LOCALAPPDATA%\OpenAI\Codex\bin\&lt;build&gt;. Never the .cmd/.ps1 shim. Pure apart from the two functions.
    /// </summary>
    public static string? FindCli(IEnumerable<string> whereCodex, string appData, string localAppData,
        Func<string, bool> fileExists, Func<string, IEnumerable<string>> dirsNewestFirst)
    {
        var lines = whereCodex.ToList();
        foreach (var line in lines)
            if (line.EndsWith("codex.exe", StringComparison.OrdinalIgnoreCase) && fileExists(line)) return line;
        foreach (var line in lines)
        {
            string? dir = Path.GetDirectoryName(line);
            if (string.IsNullOrEmpty(dir)) continue;
            string p = Path.Combine(dir, NpmExe);
            if (fileExists(p)) return p;
        }
        string npm = Path.Combine(appData, "npm", NpmExe);
        if (fileExists(npm)) return npm;
        foreach (var build in dirsNewestFirst(Path.Combine(localAppData, "OpenAI", "Codex", "bin")))
        {
            string p = Path.Combine(build, "codex.exe");
            if (fileExists(p)) return p;
        }
        return null;
    }

    // ─── One turn ───

    /// <summary>
    /// The CLI's arguments for one call. Pure, so the harness checks them. The sandbox comes from the grant (see the
    /// class summary); the chat tools ride along as MCP servers through -c overrides; "-" reads the prompt from stdin.
    /// </summary>
    public static List<string> BuildArgs(string cwd, ClaudeDevProvider.WorkerGrant? grant, string model, string effort,
        IReadOnlyList<string>? addDirs, string? chatToolsConfigJson = null)
    {
        bool write = grant is { Edit: true } || grant is { Run: true };
        var args = new List<string>
        {
            "exec", "--json", "--skip-git-repo-check", "--ephemeral",
            "-C", cwd,
            "-s", write ? "workspace-write" : "read-only",
            // the network inside the sandbox only for a turn that may run commands (installs, builds, tests)
            "-c", $"sandbox_workspace_write.network_access={(grant is { Run: true } ? "true" : "false")}",
        };
        if (!string.IsNullOrWhiteSpace(model) && model.Trim() is not ("default" or "auto"))
        {
            args.Add("-m");
            args.Add(model.Trim());
        }
        if (effort is "low" or "medium" or "high" or "xhigh" or "max")
        {
            args.Add("-c");
            args.Add($"model_reasoning_effort=\"{effort}\"");
        }
        foreach (var dir in addDirs ?? Array.Empty<string>())
        {
            args.Add("--add-dir");
            args.Add(dir);
        }
        foreach (var o in McpOverrides(chatToolsConfigJson))
        {
            args.Add("-c");
            args.Add(o);
        }
        args.Add("-");
        return args;
    }

    /// <summary>The ChatToolBridge's mcpServers as Codex config overrides: mcp_servers.&lt;name&gt;={command=…, args=[…], env={…}}.</summary>
    public static List<string> McpOverrides(string? chatToolsConfigJson)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(chatToolsConfigJson)) return list;
        try
        {
            if (JsonNode.Parse(chatToolsConfigJson)?["mcpServers"] is not JsonObject servers) return list;
            foreach (var (name, value) in servers)
            {
                if (value is not JsonObject s || s["command"]?.GetValue<string>() is not { Length: > 0 } command) continue;
                var parts = new List<string> { "command=" + Toml(command) };
                if (s["args"] is JsonArray a)
                    parts.Add("args=[" + string.Join(", ", a.Select(x => Toml(x?.GetValue<string>() ?? ""))) + "]");
                if (s["env"] is JsonObject env && env.Count > 0)
                    parts.Add("env={" + string.Join(", ", env.Select(kv => TomlKey(kv.Key) + "=" + Toml(kv.Value?.GetValue<string>() ?? ""))) + "}");
                list.Add($"mcp_servers.{TomlKey(name)}={{{string.Join(", ", parts)}}}");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return list;
    }

    /// <summary>A TOML string: a literal string ('…', no escapes — Windows paths stay as they are) unless it holds a quote.</summary>
    public static string Toml(string s) =>
        !s.Contains('\'') && !s.Contains('\n') ? "'" + s + "'"
            : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

    private static string TomlKey(string k) =>
        k.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? k : Toml(k);

    /// <summary>The prompt on stdin: CluadeX's wrapper, the conversation so far, then the request. Pure.</summary>
    public static string BuildPrompt(string cwd, ClaudeDevProvider.WorkerGrant? grant, IReadOnlyList<string>? addDirs,
        string transcript, string userMessage, string? codeComments = null)
    {
        bool worker = grant is { Edit: true } || grant is { Run: true };
        addDirs ??= Array.Empty<string>();
        string folders = addDirs.Count == 0 ? ""
            : "The user also allowed these folders beside the project — work in them by absolute path: " + string.Join(", ", addDirs) + ". ";
        string wrapper = worker
            ? "You are 'openai-dev', the coding agent of a CluadeX chat, working directly in the project. " +
              $"Project working directory: {cwd}. " +
              (grant!.Edit ? "You may create and edit files inside it. " : "You may not edit files: propose changes as diffs. ") +
              (grant.Run ? "You may run commands (builds, tests, git) in it. " : "Run only read-only commands (reading, listing, searching). ") +
              "Keep a short plan for a task of several steps. " + folders +
              "Stay inside the project folder" + (addDirs.Count > 0 ? " and those folders" : "") + " unless the user's request names another. " +
              "Answer in Markdown (code in fenced blocks) and in the language the user writes in."
            : "You are 'openai-dev', answering inside the CluadeX desktop app. " +
              $"Project working directory: {cwd}. " +
              "You can read the project but not change it; propose file edits as diffs/snippets for the app to apply. " + folders +
              "Answer in Markdown (code in fenced blocks) and in the language the user writes in.";
        if (!string.IsNullOrWhiteSpace(codeComments)) wrapper += "\n\n" + codeComments.Trim();
        var sb = new StringBuilder();
        sb.Append("[CluadeX] ").Append(wrapper).Append("\n\n");
        if (transcript.Length > 0)
            sb.Append("<conversation_so_far>\n").Append(transcript).Append("\n</conversation_so_far>\n\n");
        sb.Append(userMessage);
        return sb.ToString();
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
            OnError?.Invoke("Codex CLI not found on this machine");
            yield return "ไม่พบ Codex CLI บนเครื่องนี้ — ติดตั้งแอป Codex หรือรัน `npm i -g @openai/codex` แล้วรัน `codex login` หนึ่งครั้งเพื่อล็อกอินด้วยบัญชี ChatGPT";
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
        var scope = AgentRunScope.Current;
        var (effort, whyKey) = ClaudeDevProvider.ResolveEffort(_settingsService.Settings.ReasoningEffort, userMessage, scope?.UserImages?.Count ?? 0);
        if (scope != null)
        {
            scope.EffortWhy = whyKey != null ? LocalizedResources.Instance["auto.why." + whyKey] : null;
            scope.LiveEffort = effort;
        }
        string? chatToolsJson = null;
        if (!string.IsNullOrWhiteSpace(extras?.ToolsConfig))
            try { chatToolsJson = await File.ReadAllTextAsync(extras.ToolsConfig, ct); } catch { chatToolsJson = null; }
        var addDirs = ClaudeDevProvider.TurnAddDirs(extras);

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
        foreach (var a in BuildArgs(cwd, grant, model, effort, addDirs, chatToolsJson)) psi.ArgumentList.Add(a);
        psi.Environment["NO_COLOR"] = "1";

        string shownModel = !string.IsNullOrWhiteSpace(model) && model is not ("default" or "auto") ? model : "";
        LiveModelReport.Sent(AiProviderType.OpenAIDev, shownModel, effort, true);
        var translator = new CodexStreamTranslator(cwd, ReadForDiff);
        SetStatus("OpenAI Dev thinking…");
        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc == null)
            {
                OnError?.Invoke("Failed to start the Codex CLI");
                yield break;
            }
            using (var stdin = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                await stdin.WriteAsync(BuildPrompt(cwd, grant, addDirs, ClaudeDevProvider.BuildTranscript(history), userMessage,
                    CodeCommentPolicy.InstructionFor(_settingsService.Settings, cwd)));
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
                    SetStatus("OpenAI Dev working…");
                    yield return text;
                }
                else if (sink != null)
                {
                    translator.TakeText();   // the rows carry it
                    if (translator.HasOutput && !any) { any = true; SetStatus("OpenAI Dev working…"); }
                }
            }
            await proc.WaitForExitAsync(timeout.Token);

            string err = (await SafeAwait(stderrTask)).Trim();
            string? refusal = translator.ResultIsError ? translator.ResultText : null;
            if (refusal == null && !any && proc.ExitCode != 0) refusal = GeminiDevProvider.LastMeaningfulLine(err);
            if (!any || (refusal != null && LooksLikeRefusal(refusal)))
            {
                string? explained = refusal != null ? await ExplainAuthFailureAsync(refusal, ct) : null;
                LastTurnAuthRefused = explained != null;
                if (sink == null || !any)
                    yield return explained
                        ?? (string.IsNullOrWhiteSpace(refusal) ? "(openai-dev returned no output)" : $"openai-dev error: {Cap(refusal, 400)}");
            }
            else if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
                OnError?.Invoke($"openai-dev exit {proc.ExitCode}: {Cap(err, 400)}");
            SetStatus("Ready");
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            proc?.Dispose();
        }
    }

    /// <summary>A file's text for an edit row's diff — small text files only (a big or binary file shows no diff).</summary>
    private static string? ReadForDiff(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > 1_000_000) return null;
            return File.ReadAllText(path);
        }
        catch { return null; }
    }

    private static string Cap(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    // ─── Sign-in ───

    public bool LastTurnAuthRefused { get; private set; }

    public bool LooksLikeRefusal(string? text) => LooksLikeAuthRefusal(text);

    /// <summary>The CLI's refusals for no sign-in, an expired sign-in, or a used-up plan limit.</summary>
    public static bool LooksLikeAuthRefusal(string? text) => text != null && (
        text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase)
        || text.Contains("codex login", StringComparison.OrdinalIgnoreCase)
        || text.Contains("401", StringComparison.Ordinal)
        || text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
        || text.Contains("refresh token", StringComparison.OrdinalIgnoreCase)
        || IsLimit(text));

    private static bool IsLimit(string text) =>
        text.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
        || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
        || text.Contains("429", StringComparison.Ordinal)
        || text.Contains("quota", StringComparison.OrdinalIgnoreCase);

    /// <summary>What to tell the user about a refusal. Pure, so the harness checks the wording. Null = not a refusal.</summary>
    public static string? AuthGuidance(string refusal, bool signedIn)
    {
        if (!LooksLikeAuthRefusal(refusal)) return null;
        string raw = Cap(refusal.Trim().Replace('\n', ' '), 300);
        if (IsLimit(refusal) && signedIn)
            return $"⚠ **OpenAI Dev ใช้โควตาของแพ็กเกจ ChatGPT หมด หรือส่งถี่เกินไป**\n\nข้อความจาก Codex CLI: `{raw}`\n\n"
                 + "รอให้โควตารีเซ็ตแล้วกด **ลองใหม่** หรือเปลี่ยนไปใช้โมเดลอื่นในกล่องเลือกโมเดลชั่วคราว (เช่น Claude Dev)";
        return $"🔑 **OpenAI Dev ยังไม่ได้ล็อกอิน (หรือการล็อกอินหมดอายุ)**\n\nข้อความจาก Codex CLI: `{raw}`\n\n"
             + "OpenAI Dev ใช้บัญชี ChatGPT ของ Codex ในเครื่องนี้ ไม่ต้องใช้ API key\n\n"
             + "**วิธีแก้ (ครั้งเดียวจบ):**\n"
             + "1. พิมพ์ `/login` ในช่องแชทนี้ — CluadeX จะเปิดหน้าต่าง `codex login` ให้\n"
             + "2. เลือก **Sign in with ChatGPT** แล้วยืนยันบัญชีในเบราว์เซอร์ที่เด้งขึ้นมา\n"
             + "3. ปิดหน้าต่างนั้นได้เลย แล้วกลับมากด **ลองใหม่** — ไม่ต้องรีสตาร์ท CluadeX";
    }

    public async Task<string?> ExplainAuthFailureAsync(string? refusal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refusal) || !LooksLikeAuthRefusal(refusal)) return null;
        return AuthGuidance(refusal, await IsLoggedInAsync(ct));
    }

    public Task<bool> IsLoggedInAsync(CancellationToken ct) => Task.FromResult(SignedIn());

    /// <summary>Signed in = Codex's stored sign-in (CODEX_HOME or ~/.codex auth.json), or an OpenAI key in its environment.</summary>
    public static bool SignedIn()
    {
        string home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h
            ? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return File.Exists(Path.Combine(home, "auth.json"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
    }

    /// <summary>A console window running `codex login`: it opens ChatGPT's sign-in in the browser — the click there is
    /// the user's. CluadeX never types a password.</summary>
    public bool OpenLoginTerminal()
    {
        try
        {
            string cmd = _exe is { } e ? $"\"{e}\" login" : "codex login";
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
        if (_exe is not { } exe) return (false, "Codex CLI not found (install the Codex app or npm i -g @openai/codex)");
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi)!;
            string v = (await p.StandardOutput.ReadToEndAsync(ct)).Trim();
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) return (false, $"exit {p.ExitCode}");
            return SignedIn()
                ? (true, v)
                : (false, $"{v} — not signed in yet: run `codex login` once in a terminal");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private void SetStatus(string s) { _statusMessage = s; OnStatusChanged?.Invoke(s); }
    private void SetLoading(bool b) { _isLoading = b; OnLoadingChanged?.Invoke(b); }

    public void Dispose() { }
}

/// <summary>
/// Codex CLI `exec --json` → Claude Code stream-json, line by line, so <see cref="ClaudeDevStreamTurn"/> renders an
/// OpenAI Dev turn exactly as it renders Claude Dev. Codex events: thread.started / turn.started / item.started /
/// item.completed (agent_message, reasoning, command_execution, file_change, mcp_tool_call, web_search, todo_list,
/// error) / turn.completed (usage) / turn.failed / error. A file_change carries only paths, so the file is read when
/// the change starts and again when it is done: the row gets a real before/after diff. Pure apart from the reader.
/// </summary>
public sealed class CodexStreamTranslator
{
    private readonly string _cwd;
    private readonly Func<string, string?> _read;
    private readonly StringBuilder _plainText = new();
    private readonly Dictionary<string, Dictionary<string, string?>> _before = new();
    private readonly HashSet<string> _started = new();
    private int _message = 1;
    private bool _afterTools;
    private string? _lastError;

    public CodexStreamTranslator(string cwd, Func<string, string?>? read = null)
    {
        _cwd = cwd;
        _read = read ?? (_ => null);
    }

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
        switch (Str(ev, "type"))
        {
            case "thread.started":
                output.Add(Json(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["model"] = "codex" }));
                break;
            case "item.started":
                if (ev["item"] is JsonObject started) OnItem(started, done: false, output);
                break;
            case "item.completed":
                if (ev["item"] is JsonObject completed) OnItem(completed, done: true, output);
                break;
            case "error":
                _lastError = ErrorText(Str(ev, "message"));
                break;
            case "turn.completed":
                Done = true;
                output.Add(Json(new JsonObject { ["type"] = "result", ["is_error"] = false }));
                break;
            case "turn.failed":
                Done = true;
                ResultIsError = true;
                ResultText = ErrorText(Str(ev["error"] as JsonObject, "message")) ?? _lastError ?? "Codex CLI error";
                output.Add(Json(new JsonObject { ["type"] = "result", ["is_error"] = true, ["result"] = ResultText }));
                break;
        }
        return output;
    }

    /// <summary>Codex wraps an API error as JSON text in "message": the inner error's own message, else the text.</summary>
    private static string? ErrorText(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        try
        {
            if (JsonNode.Parse(message) is JsonObject o && (o["error"] as JsonObject)?["message"]?.GetValue<string>() is { Length: > 0 } inner)
                return inner;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return message;
    }

    private void OnItem(JsonObject item, bool done, List<string> output)
    {
        string id = Str(item, "id") ?? Guid.NewGuid().ToString("N");
        switch (Str(item, "type"))
        {
            case "agent_message":
                if (!done || Str(item, "text") is not { Length: > 0 } text) return;
                if (_afterTools) { _message++; _afterTools = false; }
                HasOutput = true;
                _plainText.Append(_plainText.Length > 0 ? "\n\n" : "").Append(text.TrimEnd());
                Assistant(output, new JsonObject { ["type"] = "text", ["text"] = text.TrimEnd() });
                return;
            case "reasoning":
                if (done && Str(item, "text") is { Length: > 0 } thought)
                    Assistant(output, new JsonObject { ["type"] = "thinking", ["thinking"] = thought });
                return;
            case "command_execution":
                if (!done) { Start(id, output, "Bash", new JsonObject { ["command"] = Command(Str(item, "command") ?? "") }); return; }
                if (!_started.Contains(id)) Start(id, output, "Bash", new JsonObject { ["command"] = Command(Str(item, "command") ?? "") });
                int? exit = item["exit_code"] is JsonValue ev && ev.TryGetValue(out int code) ? code : null;
                Result(id, output, Str(item, "aggregated_output") ?? "", failed: exit is not (null or 0) || Str(item, "status") == "failed");
                return;
            case "file_change":
                OnFileChange(id, item, done, output);
                return;
            case "mcp_tool_call":
            {
                string name = $"mcp__{Str(item, "server") ?? "mcp"}__{Str(item, "tool") ?? "tool"}";
                var input = item["arguments"] is JsonObject a ? (JsonObject)a.DeepClone() : new JsonObject();
                if (!done) { Start(id, output, name, input); return; }
                if (!_started.Contains(id)) Start(id, output, name, input);
                bool failed = Str(item, "status") == "failed" || item["error"] is JsonObject;
                string body = failed ? Str(item["error"] as JsonObject, "message") ?? "MCP tool error" : McpResultText(item["result"]);
                Result(id, output, body, failed);
                return;
            }
            case "web_search":
                if (!done) return;
                Start(id, output, "WebSearch", new JsonObject { ["query"] = Str(item, "query") ?? "" });
                Result(id, output, "", failed: false);
                return;
            case "todo_list":
            {
                var todos = new JsonArray();
                if (item["items"] is JsonArray items)
                    foreach (var t in items.OfType<JsonObject>())
                        todos.Add(new JsonObject
                        {
                            ["content"] = Str(t, "text") ?? "",
                            ["status"] = t["completed"] is JsonValue c && c.TryGetValue(out bool fin) && fin ? "completed" : "pending",
                        });
                string todoId = id + "-" + (done ? "done" : "start");
                Start(todoId, output, "TodoWrite", new JsonObject { ["todos"] = todos });
                Result(todoId, output, "", failed: false);
                return;
            }
            case "error":
                if (done && Str(item, "message") is { Length: > 0 } warn) _plainText.Append("\n\n⚠ ").Append(warn);
                return;
        }
    }

    /// <summary>A file change: before = the file as it was when the change started; after = as it is when done.</summary>
    private void OnFileChange(string id, JsonObject item, bool done, List<string> output)
    {
        var changes = (item["changes"] as JsonArray)?.OfType<JsonObject>()
            .Select(c => (Path: Full(Str(c, "path") ?? ""), Kind: Str(c, "kind") ?? "update")).Where(c => c.Path.Length > 0).ToList() ?? new();
        if (!done)
        {
            _before[id] = changes.ToDictionary(c => c.Path, c => c.Kind == "add" ? "" : _read(c.Path), StringComparer.OrdinalIgnoreCase);
            return;
        }
        _before.Remove(id, out var before);
        bool failed = Str(item, "status") == "failed";
        int n = 0;
        foreach (var (path, kind) in changes)
        {
            string callId = changes.Count == 1 ? id : $"{id}-{++n}";
            if (kind == "delete")
            {
                Start(callId, output, "Bash", new JsonObject { ["command"] = $"Remove-Item \"{path}\"", ["description"] = "delete " + Path.GetFileName(path) });
                Result(callId, output, "", failed);
                continue;
            }
            string? after = failed ? null : _read(path);
            string? original = before != null && before.TryGetValue(path, out var b) ? b : (kind == "add" ? "" : null);
            Start(callId, output, "Write", new JsonObject { ["file_path"] = path, ["content"] = after ?? "" });
            Result(callId, output, failed ? "The change was not applied" : "", failed,
                original != null && after != null ? new JsonObject { ["originalFile"] = original } : null);
        }
    }

    private string Full(string p)
    {
        try { return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(_cwd, p)); }
        catch { return p; }
    }

    /// <summary>The command Codex ran, without the PowerShell wrapper it adds on Windows (`"…powershell.exe" -Command '…'`).</summary>
    public static string Command(string raw)
    {
        int at = raw.IndexOf(" -Command ", StringComparison.OrdinalIgnoreCase);
        if (at < 0 || !raw[..at].Contains("powershell", StringComparison.OrdinalIgnoreCase)) return raw;
        string inner = raw[(at + " -Command ".Length)..].Trim();
        if (inner.Length >= 2 && (inner[0] == '\'' && inner[^1] == '\'' || inner[0] == '"' && inner[^1] == '"'))
            inner = inner[1..^1];
        return inner.Replace("''", "'");
    }

    private static string McpResultText(JsonNode? result)
    {
        if (result is JsonObject r && r["content"] is JsonArray parts)
            return string.Join("\n", parts.OfType<JsonObject>().Select(p => Str(p, "text")).Where(t => !string.IsNullOrEmpty(t)));
        return result?.ToJsonString() ?? "";
    }

    private string MessageId => "codex-msg-" + _message;

    private void Assistant(List<string> output, JsonObject block) =>
        output.Add(Json(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["id"] = MessageId, ["content"] = new JsonArray { block } },
        }));

    private void Start(string id, List<string> output, string name, JsonObject input)
    {
        if (!_started.Add(id)) return;
        if (_afterTools) { _message++; _afterTools = false; }
        HasOutput = true;
        Assistant(output, new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input });
    }

    private void Result(string id, List<string> output, string body, bool failed, JsonObject? structured = null)
    {
        _afterTools = true;
        var line = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["content"] = new JsonArray { new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = body, ["is_error"] = failed } },
            },
        };
        if (structured != null) line["tool_use_result"] = structured;
        output.Add(Json(line));
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
