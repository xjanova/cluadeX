using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

/// <summary>
/// "grok-dev" — xAI's Grok Build CLI on this machine (`grok`, %USERPROFILE%\.grok\bin\grok.exe), exposed as a selectable
/// CluadeX model the way Claude Dev exposes Claude Code (owner 2026-11-06: "อย่าลืมเชื่อมกับ grok build desktop dev ได้ด้วย").
/// No API key: the CLI uses the owner's own sign-in (`grok login`, stored in ~/.grok/auth.json), or XAI_API_KEY.
///
/// The same decisions as <see cref="OpenAIDevProvider"/> and <see cref="ClaudeDevProvider"/>:
/// - The CLI is its own agent: one delegated call per turn (CodeAgentService.RunDelegatedTurnAsync), never wrapped in
///   CluadeX's loops. `grok --prompt-file` (the prompt never goes through argv quoting — Thai and quotes survive) with
///   `--output-format streaming-messages-json`, which Grok documents as Claude Code's stream-json wire format. So the
///   chat's rows, plan, diffs and steps come from the one renderer that already works (<see cref="ClaudeDevStreamTurn"/>);
///   <see cref="GrokStreamTranslator"/> only renames Grok's tools (read_file, search_replace, run_terminal_cmd …) to the
///   names that renderer knows.
/// - Safety is Grok's own permission mode, chosen from the user's rules (<see cref="ClaudeDevProvider.Grant"/>): no grant =
///   dontAsk (only Grok's built-in read-only handling runs; every edit and every other command is refused); the edit
///   grant = acceptEdits (file edits, commands still need an approval nobody can give headless, so they are refused);
///   the run grant = bypassPermissions, the same reach as Gemini Dev's run grant — without the edit grant its file-writing
///   tools (<see cref="FileWriteTools"/>) are removed, so it runs commands but does not edit. Never --sandbox off.
/// - On top, like Claude Dev and Gemini Dev, each turn runs in its own GROK_HOME (<see cref="GrokTurnHome"/>): CluadeX's
///   guard as a PreToolUse hook on Grok's shell, file writers and sub-agent spawn (the red prompt, the never-run list, the
///   no-edit rule, the sub-agent gate), and CluadeX's chat tools as the MCP server `cluadex`. A worker turn without the
///   guard gets no shell; a turn whose first guarded call arrives before the hooks proved they loaded is stopped.
/// - Sub-agents: Grok's spawn_subagent goes through Agents.CliAgentGate (read-only explore / plan only — Grok's worktree
///   isolation is not wired); --no-subagents when the step's policy says none, in a plain chat, or when nothing gates them.
/// - Cost: the CLI's own result line (usage + total_cost_usd when the server stamps it) through
///   <see cref="DelegatedUsageEstimate"/>, filed under the GrokDev Billing tab as plan use.
/// </summary>
public class GrokDevProvider : IAiProvider, IDelegatedCliProvider
{
    private readonly SettingsService _settingsService;
    private readonly FileSystemService _fileSystemService;

    private string? _exe;
    private string _statusMessage = "Not initialised";
    private bool _isLoading;

    public const string Id = "GrokDev";
    /// <summary>Grok spawns its own sub-agents (spawn_subagent); the turn's guard hook asks Agents.CliAgentGate first.</summary>
    public bool RunsOwnSubAgents => true;

    public string ProviderId => Id;
    public string DisplayName => "Grok Dev (Grok Build CLI)";
    public bool IsReady => _exe != null;
    public bool IsLoading => _isLoading;
    public string StatusMessage => _statusMessage;
    public string? Exe => _exe;

    public event Action<string>? OnStatusChanged;
    public event Action<bool>? OnLoadingChanged;
    public event Action<string>? OnError;

    /// <summary>The chat's model box: "default" sends no -m (Grok's own default, grok-4.7 on 1.0.70), then what
    /// `grok models` lists for a signed-in account (2026-11-06).</summary>
    public static readonly string[] KnownModels = ["default", "grok-4.7", "grok-4.7-build-fast", "grok-4.6", "grok-4.5"];

    public GrokDevProvider(SettingsService settingsService, FileSystemService fileSystemService)
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
            SetStatus(_exe != null ? $"Ready · {_exe}" : "Grok Build CLI not found — install it with `irm https://x.ai/cli/install.ps1 | iex`");
        }
        finally { SetLoading(false); }
    }

    /// <summary>Is the Grok Build CLI on this machine (cheap; for the chat's model box and the Setup page)?</summary>
    public static bool IsInstalled() => Locate() != null;

    public static string? Locate() => FindCli(WhereLines("grok"), GrokHome(), File.Exists);

    /// <summary>GROK_HOME, else %USERPROFILE%\.grok — where the CLI keeps bin\, auth.json and config.toml.</summary>
    public static string GrokHome() => Environment.GetEnvironmentVariable("GROK_HOME") is { Length: > 0 } h
        ? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");

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
    /// grok.exe: the native binary the installer puts in &lt;grok home&gt;\bin (the npm package @xai-official/grok's shim
    /// launches the same one); else a grok.exe on PATH. Never a .cmd/.ps1 shim — cmd.exe re-parses arguments. Pure apart
    /// from <paramref name="fileExists"/>.
    /// </summary>
    public static string? FindCli(IEnumerable<string> whereGrok, string grokHome, Func<string, bool> fileExists)
    {
        string home = Path.Combine(grokHome, "bin", "grok.exe");
        if (fileExists(home)) return home;
        foreach (var line in whereGrok)
            if (line.EndsWith("grok.exe", StringComparison.OrdinalIgnoreCase) && fileExists(line)) return line;
        return null;
    }

    // ─── One turn ───

    /// <summary>The CLI's arguments for one call. Pure, so the harness checks them (see the class summary for the modes).</summary>
    /// <param name="guarded">The turn's guard hook is in place; a worker turn without it gets no shell.</param>
    /// <param name="noSubAgents">--no-subagents (see the class summary).</param>
    /// <param name="chatTools">CluadeX's MCP server is in the turn's config: its tools are allowed (the bridge asks the
    /// user itself where a native call would), else dontAsk / acceptEdits would refuse every one.</param>
    public static List<string> BuildArgs(string cwd, ClaudeDevProvider.WorkerGrant? grant, string model, string effort,
        IReadOnlyList<string>? addDirs, string promptFile, bool guarded = true, bool noSubAgents = false, bool chatTools = false,
        IReadOnlyList<string>? brainTools = null)
    {
        var args = new List<string>
        {
            "--prompt-file", promptFile,
            "--output-format", "streaming-messages-json",
            "--cwd", cwd,
            "--permission-mode", grant is { Run: true } ? "bypassPermissions" : grant is { Edit: true } ? "acceptEdits" : "dontAsk",
        };
        // The run grant without the edit grant: commands yes, but bypassPermissions would also let it edit — so its three
        // file-writing tools are removed (the ids Grok Build 1.0.70 ships; dontAsk and acceptEdits already hold the rest).
        var refused = new List<string>();
        if (grant is { Run: true, Edit: false }) refused.AddRange(FileWriteTools);
        // No guard → no shell (Claude Dev's rule): bypassPermissions would run any command unseen.
        if (grant is { Run: true } && !guarded) refused.Add(ShellTool);
        if (refused.Count > 0)
        {
            args.Add("--disallowed-tools");
            args.Add(string.Join(",", refused));
        }
        if (noSubAgents) args.Add("--no-subagents");
        if (chatTools)
        {
            args.Add("--allow");
            args.Add($"MCPTool({Tools.ChatToolBridge.ServerName}__*)");
        }
        // The brain tools this turn may call unasked (ClaudeDevProvider.BrainToolsFor) when its config has the brain.
        foreach (var tool in brainTools ?? Array.Empty<string>())
        {
            args.Add("--allow");
            args.Add($"MCPTool({GeminiDevProvider.BrainServerName}__{tool})");
        }
        bool chosen = !string.IsNullOrWhiteSpace(model) && model.Trim() is not ("default" or "auto");
        if (chosen)
        {
            args.Add("-m");
            args.Add(model.Trim());
        }
        // A model only takes the levels its menu lists: clamp to what xAI documents for it ("default" = grok-4.7).
        if (GrokModels.EffortFor(chosen ? model.Trim() : GrokModels.DefaultModel, effort) is { } e)
        {
            args.Add("--reasoning-effort");
            args.Add(e);
        }
        foreach (var dir in addDirs ?? Array.Empty<string>())
        {
            args.Add("--add-dir");
            args.Add(dir);
        }
        return args;
    }

    /// <summary>Grok Build's built-in tools that write files (its docs: Edit / Write / MultiEdit map to search_replace,
    /// StrReplace to search_replace and hashline_edit; write is the whole-file writer).</summary>
    public static readonly string[] FileWriteTools = ["search_replace", "hashline_edit", "write"];

    /// <summary>Grok Build's shell tool id (its docs: `--disallowed-tools "run_terminal_cmd"`).</summary>
    public const string ShellTool = "run_terminal_cmd";

    /// <summary>The wrapper's line on CluadeX's own tools: Grok reaches MCP tools through search_tool / use_tool, by their
    /// server__tool names. Null when the turn has no chat tools.</summary>
    public static string? ChatToolsHint(ClaudeDevProvider.TurnExtras? extras, bool chatTools)
    {
        if (!chatTools) return null;
        string s = Tools.ChatToolBridge.ServerName;
        return $"CluadeX's own tools are on the MCP server {s}: find them with search_tool and call them with use_tool. " +
               $"To work in another project call {s}__open_project (CluadeX asks the user) or start a chat there with {s}__new_chat; " +
               $"a new, independent request that arrives mid-task goes to its own chat with {s}__new_chat too, and {s}__close_chat " +
               "lists the chats you split off and puts finished ones away. Never answer that you cannot open a chat or a folder. " +
               Tools.ChatToolBridge.AskHint + " " +
               Tools.ChatToolBridge.ChatToolsHint(Tools.ChatToolBridge.CliToolNames.Grok, splitChats: false, relay: true) + " " +
               (ClaudeDevProvider.BrowserHintFor(extras) ?? "");
    }

    /// <summary>The prompt file: CluadeX's wrapper, the conversation so far, then the request. Pure.</summary>
    public static string BuildPrompt(string cwd, ClaudeDevProvider.WorkerGrant? grant, IReadOnlyList<string>? addDirs,
        string transcript, string userMessage, string? codeComments = null, string? toolsHint = null, string? agentLine = null,
        bool noProject = false, string? splitGuide = null)
    {
        bool worker = grant is { Edit: true } || grant is { Run: true };
        addDirs ??= Array.Empty<string>();
        string folders = addDirs.Count == 0 ? ""
            : "The user also allowed these folders beside the project — work in them by absolute path: " + string.Join(", ", addDirs) + ". ";
        string wrapper = worker
            ? "You are 'grok-dev', the coding agent of a CluadeX chat, working directly in the project. " +
              $"Project working directory: {cwd}. " +
              (grant!.Edit ? "You may create and edit files inside it. " : "You may not edit files: propose changes as diffs. ") +
              (grant.Run ? "You may run commands (builds, tests, git) in it. " : "Run only read-only commands (reading, listing, searching). ") +
              "Keep a short plan for a task of several steps. " +
              (string.IsNullOrWhiteSpace(agentLine) ? "" : agentLine.Trim() + " ") + folders +
              "Stay inside the project folder" + (addDirs.Count > 0 ? " and those folders" : "") + " unless the user's request names another. " +
              "Answer in Markdown (code in fenced blocks) and in the language the user writes in."
            : noProject
                ? "You are 'grok-dev', answering inside the CluadeX desktop app. " +
                  $"This turn runs read-only in {cwd}. " + folders +
                  "Answer in Markdown (code in fenced blocks) and in the language the user writes in."
                : "You are 'grok-dev', answering inside the CluadeX desktop app. " +
                  $"Project working directory: {cwd}. " +
                  "You can read the project but not change it; propose file edits as diffs/snippets for the app to apply. " + folders +
                  "Answer in Markdown (code in fenced blocks) and in the language the user writes in.";
        if (!string.IsNullOrWhiteSpace(toolsHint)) wrapper += " " + toolsHint.Trim();
        if (worker && !string.IsNullOrWhiteSpace(toolsHint) && !string.IsNullOrWhiteSpace(splitGuide)) wrapper += " " + splitGuide.Trim();
        if (noProject && !string.IsNullOrWhiteSpace(toolsHint))
            wrapper += " " + Tools.ChatToolBridge.OpenProjectHint(true, Tools.ChatToolBridge.ServerName + "__open_project (through use_tool)");
        wrapper += "\n\n" + AgentPersona.Section(AiProviderType.GrokDev, false);
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
            OnError?.Invoke("Grok Build CLI not found on this machine");
            yield return "ไม่พบ Grok Build CLI บนเครื่องนี้ — ติดตั้งด้วย `irm https://x.ai/cli/install.ps1 | iex` ใน PowerShell "
                       + "แล้วรัน `grok login` หนึ่งครั้งเพื่อล็อกอินด้วยบัญชี grok.com / X";
            yield break;
        }

        string cwd = _fileSystemService.HasWorkingDirectory
            ? _fileSystemService.WorkingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var grant = ClaudeDevProvider.Grant.Value;
        bool worker = grant is { Edit: true } || grant is { Run: true };
        var sink = worker ? ClaudeDevProvider.StreamSink.Value : null;
        var extras = ClaudeDevProvider.Extras.Value;
        // The chat's own model first (each chat keeps its own: Services/ChatModelStore.cs), else the one in Settings.
        string model = AgentRunScope.Current?.ModelFor(Id) is { Length: > 0 } pinned ? pinned
            : _settingsService.Settings.ProviderConfigs.TryGetValue(Id, out var cfg)
              && !string.IsNullOrWhiteSpace(cfg.EffectiveModelId) ? cfg.EffectiveModelId! : "";
        var scope = AgentRunScope.Current;
        var (effort, whyKey) = ClaudeDevProvider.ResolveEffort(_settingsService.Settings.ReasoningEffort, userMessage, scope?.UserImages?.Count ?? 0, history);
        if (scope != null)
        {
            scope.EffortWhy = whyKey != null ? LocalizedResources.Instance["auto.why." + whyKey] : null;
            scope.LiveEffort = effort;
            scope.StepEffort = effort;
        }
        var addDirs = ClaudeDevProvider.TurnAddDirs(extras);

        // The turn's own GROK_HOME: the guard hook (a worker turn) and CluadeX's chat tools. Not set up = the user's own
        // home, no guard (so no shell for a worker) and no chat tools.
        GrokTurnHome? home = null;
        bool guarded = false, chatTools = false;
        try
        {
            home = GrokTurnHome.Create();
            home.CarryUserHome(GrokHome());
            string? chatToolsJson = null;
            if (!string.IsNullOrWhiteSpace(extras?.ToolsConfig))
                try { chatToolsJson = await File.ReadAllTextAsync(extras.ToolsConfig, ct); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            // The brain the user's Claude Code has, when Grok's own config has none. Not on a run-grant turn: its
            // bypassPermissions would run every brain tool unasked (ssh_run, chrome…), not only the allowed ones.
            chatTools = home.WriteConfig(GrokHome(), chatToolsJson,
                grant is { Run: true } ? null : GeminiDevProvider.BrainServerFromClaude());
            if (worker)
            {
                home.WriteHooks(AntigravityDevProvider.ShortPath(Safety.GuardHook.ExePath()), noEdit: grant is { Run: true, Edit: false });
                guarded = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            OnError?.Invoke($"Grok Dev could not set up its turn folder ({ex.GetType().Name}) — running without CluadeX's guard and chat tools");
            home?.Delete();
            home = null;
            guarded = chatTools = false;
        }
        // Sub-agents run only where the guard gates them, in a worker turn, under a policy that allows them.
        var dispatch = extras?.Dispatch?.Invoke();
        bool gated = worker && guarded && !string.IsNullOrEmpty(extras?.GateToken);
        bool noSubAgents = !worker || (dispatch is { Legacy: false } && (!dispatch.SubAgents || !gated)) || (dispatch == null && !gated);

        // The prompt goes in a file the CLI reads (--prompt-file): no argv quoting, no length ceiling. Deleted after the turn.
        string promptDir = Path.Combine(Path.GetTempPath(), "cluadex-grok");
        string promptFile = Path.Combine(promptDir, Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(promptDir);
        PruneStalePrompts(promptDir);
        string promptText = BuildPrompt(cwd, grant, addDirs, ClaudeDevProvider.BuildTranscript(history), userMessage,
            LearnedHabits.Append(CodeCommentPolicy.InstructionFor(_settingsService.Settings, cwd), cwd), ChatToolsHint(extras, chatTools),
            noSubAgents ? null : Agents.CliAgentPolicy.PromptLine(dispatch, AiProviderType.GrokDev, machine: Agents.AgentLoad.Snapshot()),
            noProject: !_fileSystemService.HasWorkingDirectory,
            splitGuide: Agents.WorkflowGate.SplitGuideFor(Agents.WorkflowGate.CurrentChat, AiProviderType.GrokDev));

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
        foreach (var a in BuildArgs(cwd, grant, model, effort, addDirs, promptFile, guarded, noSubAgents, chatTools,
                     home?.HasBrain == true ? ClaudeDevProvider.BrainToolsFor(grant) : null)) psi.ArgumentList.Add(a);
        psi.Environment["NO_COLOR"] = "1";
        if (home != null)
        {
            psi.Environment["GROK_HOME"] = home.Home;
            if (home.UserAuthFile != null) psi.Environment["GROK_AUTH_PATH"] = home.UserAuthFile;
            // An update would land in the turn's home, not the user's install.
            psi.Environment["GROK_DISABLE_AUTOUPDATER"] = "1";
        }
        if (gated && !noSubAgents) psi.Environment[Safety.GuardHook.AgentGateTokenEnv] = extras!.GateToken;

        string shownModel = !string.IsNullOrWhiteSpace(model) && model is not ("default" or "auto") ? model : "";
        LiveModelReport.Sent(AiProviderType.GrokDev, shownModel, effort, true);
        var translator = new GrokStreamTranslator();
        SetStatus("Grok Dev thinking…");
        Process? proc = null;
        try
        {
            await File.WriteAllTextAsync(promptFile, promptText, new UTF8Encoding(false), ct);
            proc = Process.Start(psi);
            Background.AppChildren.Adopt(proc);
            if (proc == null)
            {
                OnError?.Invoke("Failed to start the Grok Build CLI");
                yield break;
            }
            proc.StandardInput.Close();   // nothing comes on stdin: a CLI that waits for it would hang the turn

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(worker ? TimeSpan.FromMinutes(90) : TimeSpan.FromSeconds(600));
            var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
            string? line;
            bool any = false, hooksProven = false, unguarded = false;
            while ((line = await ReadLineOrNullAsync(proc.StandardOutput, timeout.Token)) != null)
            {
                string? claudeLine = translator.Translate(line);
                if (claudeLine == null) continue;
                // The first shell / write / spawn call: the SessionStart hook must have left its marker by now, else Grok
                // did not load the turn's hooks and that call would run unguarded (hooks fail open). Stop the turn.
                if (guarded && !hooksProven && translator.SawGuardedTool)
                {
                    // SessionStart is an observe-only event: give a slow start a moment (a loaded guard holds the call
                    // in its PreToolUse hook meanwhile, so the wait costs nothing when the hooks are live).
                    for (int i = 0; i < 25 && !File.Exists(home!.Marker); i++) await DelayQuiet(200, timeout.Token);
                    if (File.Exists(home!.Marker)) hooksProven = true;
                    else
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        unguarded = true;
                        break;
                    }
                }
                DelegatedUsageEstimate.Current.Value?.Feed(claudeLine);
                if (sink != null)
                {
                    try { sink(claudeLine); } catch { }
                    if (translator.HasOutput && !any) { any = true; SetStatus("Grok Dev working…"); }
                }
                else if (translator.TakeText() is { Length: > 0 } text)
                {
                    any = true;
                    SetStatus("Grok Dev working…");
                    yield return text;
                }
            }
            if (unguarded)
            {
                OnError?.Invoke("Grok Dev stopped: the CLI did not load CluadeX's guard hook (no SessionStart marker)");
                SetStatus("Ready");
                yield return UnguardedMessage;
                yield break;
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
                        ?? (string.IsNullOrWhiteSpace(refusal) ? "(grok-dev returned no output)" : $"grok-dev error: {Cap(refusal, 400)}");
            }
            else if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
                OnError?.Invoke($"grok-dev exit {proc.ExitCode}: {Cap(err, 400)}");
            SetStatus("Ready");
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            proc?.Dispose();
            try { File.Delete(promptFile); } catch { }
            home?.Delete();
        }
    }

    /// <summary>What the chat shows when a turn is stopped because the guard did not load.</summary>
    public const string UnguardedMessage =
        "⚠ **Grok Dev หยุด turn นี้ไว้ก่อน** — Grok Build CLI ไม่ได้โหลด hook ป้องกันของ CluadeX (ตัวที่คอยถามก่อนรันคำสั่งอันตราย) "
        + "จึงไม่ปล่อยให้รันคำสั่งหรือแก้ไฟล์โดยไม่มีใครเฝ้า\n\n"
        + "ลองอัปเดต CLI (`grok update`) แล้วกด **ลองใหม่** หรือปิดสิทธิ์รันคำสั่ง/แก้ไฟล์ของแชทนี้เพื่อให้ Grok Dev ตอบแบบอ่านอย่างเดียว";

    private static string Cap(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    /// <summary>A prompt file left by a turn the app never finished (killed, crashed) holds a conversation: gone after a day.</summary>
    private static void PruneStalePrompts(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.txt"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(1)) try { File.Delete(f); } catch (IOException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ─── Sign-in ───

    public bool LastTurnAuthRefused { get; private set; }

    public bool LooksLikeRefusal(string? text) => LooksLikeAuthRefusal(text);

    /// <summary>The CLI's refusals for no sign-in, an expired sign-in, or a used-up plan / credit limit.</summary>
    public static bool LooksLikeAuthRefusal(string? text) => text != null && (
        text.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
        || text.Contains("grok login", StringComparison.OrdinalIgnoreCase)
        || text.Contains("sign in again", StringComparison.OrdinalIgnoreCase)
        || text.Contains("401", StringComparison.Ordinal)
        || text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
        || text.Contains("refresh token", StringComparison.OrdinalIgnoreCase)
        || IsLimit(text));

    private static bool IsLimit(string text) =>
        text.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
        || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
        || text.Contains("429", StringComparison.Ordinal)
        || text.Contains("quota", StringComparison.OrdinalIgnoreCase)
        || text.Contains("credits", StringComparison.OrdinalIgnoreCase)
        || text.Contains("spending limit", StringComparison.OrdinalIgnoreCase);

    /// <summary>What to tell the user about a refusal. Pure, so the harness checks the wording. Null = not a refusal.</summary>
    public static string? AuthGuidance(string refusal, bool signedIn)
    {
        if (!LooksLikeAuthRefusal(refusal)) return null;
        string raw = Cap(refusal.Trim().Replace('\n', ' '), 300);
        if (IsLimit(refusal) && signedIn)
            return $"⚠ **Grok Dev ใช้โควตาของแพ็กเกจ / เครดิต xAI หมด หรือส่งถี่เกินไป**\n\nข้อความจาก Grok Build CLI: `{raw}`\n\n"
                 + "รอให้โควตารีเซ็ตแล้วกด **ลองใหม่** หรือเปลี่ยนไปใช้โมเดลอื่นในกล่องเลือกโมเดลชั่วคราว";
        return $"🔑 **Grok Dev ยังไม่ได้ล็อกอิน (หรือการล็อกอินหมดอายุ)**\n\nข้อความจาก Grok Build CLI: `{raw}`\n\n"
             + "Grok Dev ใช้บัญชี grok.com / X ของ Grok Build ในเครื่องนี้ ไม่ต้องใช้ API key\n\n"
             + "**วิธีแก้ (ครั้งเดียวจบ):**\n"
             + "1. พิมพ์ `/login` ในช่องแชทนี้ — CluadeX จะเปิดหน้าต่าง `grok login` ให้\n"
             + "2. ยืนยันบัญชีในเบราว์เซอร์ที่เด้งขึ้นมา (หน้า auth.x.ai)\n"
             + "3. ปิดหน้าต่างนั้นได้เลย แล้วกลับมากด **ลองใหม่** — ไม่ต้องรีสตาร์ท CluadeX";
    }

    public async Task<string?> ExplainAuthFailureAsync(string? refusal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refusal) || !LooksLikeAuthRefusal(refusal)) return null;
        return AuthGuidance(refusal, await IsLoggedInAsync(ct));
    }

    public Task<bool> IsLoggedInAsync(CancellationToken ct) => Task.FromResult(SignedIn());

    /// <summary>Signed in = a stored sign-in in &lt;grok home&gt;\auth.json that can still be refreshed, or XAI_API_KEY.</summary>
    public static bool SignedIn() => SignedIn(ReadAuth(), Environment.GetEnvironmentVariable("XAI_API_KEY"));

    private static string? ReadAuth()
    {
        try { return File.ReadAllText(Path.Combine(GrokHome(), "auth.json")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Pure: auth.json holds at least one account with a refresh token or a key (the CLI refreshes the access
    /// token itself), or an API key is in the environment. Nothing of the file leaves this method.</summary>
    public static bool SignedIn(string? authJson, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey)) return true;
        if (string.IsNullOrWhiteSpace(authJson)) return false;
        try
        {
            if (JsonNode.Parse(authJson) is not JsonObject accounts) return false;
            foreach (var (_, value) in accounts)
                if (value is JsonObject a && (Has(a, "refresh_token") || Has(a, "key"))) return true;
        }
        catch (JsonException) { }
        return false;

        static bool Has(JsonObject a, string k) => a[k] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s);
    }

    /// <summary>A console window running `grok login`: it opens xAI's sign-in in the browser — the click there is the
    /// user's. CluadeX never types a password.</summary>
    public bool OpenLoginTerminal()
    {
        try
        {
            string cmd = (_exe ?? Locate()) is { } e ? $"\"{e}\" login" : "grok login";
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

    private static async Task DelayQuiet(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); } catch (OperationCanceledException) { }
    }

    private static async Task<string> SafeAwait(Task<string> t)
    {
        try { return await t; } catch { return ""; }
    }

    public async Task<string> GenerateAsync(List<ChatMessage> history, string userMessage, string? systemPrompt = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var t in ChatAsync(history, userMessage, systemPrompt, ct)) sb.Append(t);
        return sb.ToString();
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (_exe == null) return (false, _statusMessage);
        return SignedIn() ? (true, $"Grok Build CLI ready · {_exe}") : (false, "Grok Build CLI found but not signed in — run `grok login`");
    }

    public void Dispose() { }

    private void SetStatus(string s) { _statusMessage = s; OnStatusChanged?.Invoke(s); }
    private void SetLoading(bool b) { _isLoading = b; OnLoadingChanged?.Invoke(b); }
}

/// <summary>
/// Grok Build's streaming-messages-json → the stream-json <see cref="ClaudeDevStreamTurn"/> renders. The format is
/// already Claude Code's (Grok's docs: "the Messages API stream-json wire format"); what differs is the tool names and
/// their argument names, which are renamed here so a read, an edit or a command shows as the row it is. Anything this
/// does not know passes through unchanged (an unknown tool still shows as a generic row). Pure.
/// </summary>
public sealed class GrokStreamTranslator
{
    private readonly StringBuilder _text = new();

    public bool HasOutput { get; private set; }
    /// <summary>A tool call the turn's guard hook covers (shell, file write, sub-agent spawn) has been seen.</summary>
    public bool SawGuardedTool { get; private set; }
    public bool ResultIsError { get; private set; }
    public string? ResultText { get; private set; }

    /// <summary>Text for the plain chat since the last call (assistant text and a short line per tool).</summary>
    public string TakeText()
    {
        string t = _text.ToString();
        _text.Clear();
        return t;
    }

    /// <summary>One line in, the line for the renderer out (null = not JSON, nothing to pass on).</summary>
    public string? Translate(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{') return null;
        JsonObject? root;
        try { root = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
        if (root == null) return null;
        string type = root["type"] is JsonValue tv && tv.TryGetValue(out string? t) ? t ?? "" : "";

        if (type == "assistant" && root["message"] is JsonObject msg && msg["content"] is JsonArray content)
            foreach (var part in content.OfType<JsonObject>())
                if (part["type"] is JsonValue pt && pt.TryGetValue(out string? k) && k == "tool_use" && part["name"] is JsonValue nv
                    && nv.TryGetValue(out string? name) && name != null)
                {
                    if (IsGuarded(name)) SawGuardedTool = true;
                    var (renamed, input) = Rename(name, part["input"] as JsonObject);
                    part["name"] = renamed;
                    if (input != null) part["input"] = input;
                }

        if (type == "result")
        {
            ResultIsError = root["is_error"] is JsonValue e && e.TryGetValue(out bool b) && b;
            ResultText = root["result"] is JsonValue r && r.TryGetValue(out string? rs) ? rs : null;
        }
        string outLine = root.ToJsonString();
        // The plain chat's text, read by the same renderer Claude Dev uses (system lines skipped: their "model" would
        // otherwise be taken for Claude Dev's reported model).
        if (type is "assistant" or "result")
        {
            if (ClaudeDevProvider.RenderStreamLine(outLine, out _, out _) is { Length: > 0 } shown)
            {
                _text.Append(shown);
                HasOutput = true;
            }
        }
        if (type == "assistant") HasOutput = true;
        return outLine;
    }

    /// <summary>The tools <see cref="GrokTurnHome.GuardMatcher"/> sends through the guard (by the name the stream shows).</summary>
    public static bool IsGuarded(string name) => name is "run_terminal_cmd" or "run_terminal_command" or "bash" or "Shell"
        or "search_replace" or "hashline_edit" or "write" or "StrReplace" or "spawn_subagent" or "task";

    /// <summary>Grok's tool → Claude Code's name, with its arguments under the names the renderer reads. Grok's own docs
    /// list the ids (read_file, search_replace, run_terminal_cmd, list_dir, grep, web_search, web_fetch, todo_write, task);
    /// the argument names vary between its toolsets, so each one takes the first name it finds.</summary>
    public static (string Name, JsonObject? Input) Rename(string name, JsonObject? input)
    {
        string? S(params string[] keys)
        {
            if (input == null) return null;
            foreach (var key in keys)
                if (input[key] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrEmpty(s)) return s;
            return null;
        }
        JsonObject With(params (string Key, string? Value)[] pairs)
        {
            var o = input != null ? (JsonObject)input.DeepClone() : new JsonObject();
            foreach (var (key, value) in pairs) if (value != null) o[key] = value;
            return o;
        }
        string[] pathKeys = ["file_path", "target_file", "path", "file"];
        return name switch
        {
            "run_terminal_cmd" or "bash" or "Shell" => ("Bash", With(("command", S("command", "cmd")), ("description", S("description", "explanation")))),
            "read_file" => ("Read", With(("file_path", S(pathKeys)))),
            "search_replace" or "hashline_edit" or "StrReplace" or "edit_file" => ("Edit", With(("file_path", S(pathKeys)),
                ("old_string", S("old_string", "old_str", "search")), ("new_string", S("new_string", "new_str", "replace")))),
            "write" or "write_file" or "create_file" => ("Write", With(("file_path", S(pathKeys)), ("content", S("content", "contents", "file_text")))),
            "list_dir" => ("LS", With(("path", S("path", "target_directory", "relative_workspace_path", "directory")))),
            "grep" => ("Grep", With(("pattern", S("pattern", "query", "regex")), ("path", S("path", "target_directory")))),
            "web_search" => ("WebSearch", With(("query", S("query", "search_term")))),
            "web_fetch" => ("WebFetch", With(("url", S("url")))),
            "todo_write" => ("TodoWrite", input),
            "task" or "spawn_subagent" => ("Task", With(("description", S("description", "prompt")))),
            "Delete" or "delete_file" => ("delete_file", input),
            _ => (name, input),
        };
    }
}
