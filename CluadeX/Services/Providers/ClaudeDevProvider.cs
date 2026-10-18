using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

/// <summary>
/// "claude-dev" — the Claude Code CLI already installed on this machine, exposed as a
/// selectable CluadeX model. No API key: the CLI authenticates with the user's existing
/// Claude login, so picking this provider literally is "คุยกับ Claude" inside CluadeX.
///
/// Design decisions, in order of how much they matter:
///
/// - The CLI is ITS OWN AGENT. It plans, reads files, searches the brain (the user's
///   ~/.claude.json registers brainx-brain globally) and verifies — so CluadeX must NOT
///   wrap it in either of its agent loops. SupportsNativeToolUse stays false and
///   ExecuteAgenticAsync short-circuits for this provider: one delegated call per turn.
///   Wrapping one agent in another agent's loop is how you get two planners fighting.
///
/// - The prompt travels over STDIN, not argv. Thai text, quotes and multi-line specs
///   break Windows argv quoting long before they break a UTF-8 stream, and stdin has no
///   length ceiling.
///
/// - Headless `claude -p` cannot answer permission prompts, so the tool surface is
///   pinned to a read-only allowlist (file reads + brain recall). Anything write-shaped
///   is proposed as text/diffs for CluadeX's own loop to apply — this provider advises
///   with full context; it does not mutate the disk on its own.
///
/// - History is replayed as a compact transcript inside the prompt. Each -p call is a
///   fresh CLI session; `--continue` was rejected because it binds to "the last session
///   in this cwd", which the user's own terminal sessions would silently hijack.
/// </summary>
public class ClaudeDevProvider : IAiProvider
{
    private readonly SettingsService _settingsService;
    private readonly FileSystemService _fileSystemService;

    private string? _cliPath;
    private string _statusMessage = "Not initialised";
    private bool _isLoading;

    public string ProviderId => "ClaudeDev";
    public string DisplayName => "Claude Dev (Claude Code CLI)";
    public bool IsReady => _cliPath != null;
    public bool IsLoading => _isLoading;
    public string StatusMessage => _statusMessage;

    public event Action<string>? OnStatusChanged;
    public event Action<bool>? OnLoadingChanged;
    public event Action<string>? OnError;

    /// <summary>Read-only tools the headless CLI may use without a human to approve
    /// them. Everything else is denied by the CLI's own permission layer.</summary>
    private const string AllowedTools =
        "Read,Grep,Glob," +
        "mcp__brainx-brain__brain_search,mcp__brainx-brain__brain_semantic_search," +
        "mcp__brainx-brain__brain_get_note,mcp__brainx-brain__brain_expertise";

    /// <summary>What the CLI may do in an agent turn, decided by CluadeX from the user's own rules and Accept edits
    /// for the run's folder (CodeAgentService.WorkerGrantFor). Null = read-only advice, as in a plain chat.</summary>
    public sealed record WorkerGrant(bool Edit, bool Run);

    /// <summary>The grant for the turn in flight (set by the agent loop around its one delegated call).</summary>
    public static readonly AsyncLocal<WorkerGrant?> Grant = new();

    /// <summary>Set by the agent loop around a worker turn: every stream-json line goes here (ClaudeDevStreamTurn turns
    /// it into tool rows, the plan, steps) instead of being quoted into the reply. ChatAsync then yields only what no
    /// event carries — the explained auth/billing refusal of a turn that produced nothing.</summary>
    public static readonly AsyncLocal<Action<string>?> StreamSink = new();

    /// <summary>File tools the CLI gets when the user's rules let CluadeX's own agent edit in the folder. The brain's
    /// write tools come with them: a worker that learned something must be able to save it (cx-self-2, 2026-10-17:
    /// brain_remember was refused and six lessons from the run were lost). Task* is how CLI 2.1.29x keeps its plan.</summary>
    private const string EditTools = "Edit,Write,MultiEdit,NotebookEdit,TodoWrite,TaskCreate,TaskUpdate,TaskList,TaskGet," +
        "mcp__brainx-brain__brain_remember,mcp__brainx-brain__brain_create_note,mcp__brainx-brain__brain_append_note," +
        "mcp__brainx-brain__brain_recall";

    /// <summary>Commands the CLI is never let run headless, whatever the rules say: rewriting git history and shutting
    /// the machine down. Recursive deletes are NOT here any more (owner, 2026-10-18): `rm -rf build` must just run, and a
    /// delete aimed at a drive / home / system folder is caught by the --cx-guard hook and put to the user on CluadeX's
    /// red prompt (Safety.DangerApproval) — it runs only on their yes.</summary>
    private const string NeverTools =
        "Bash(git push --force:*),Bash(git push -f:*),Bash(git reset --hard:*),Bash(git clean:*),Bash(shutdown:*)";

    /// <summary>The CLI's own sub-agent tool (named Task in older CLIs, Agent in newer ones): a worker splits independent
    /// parts across sub-agents itself. They run inside the same CLI session, so they carry the same --settings guard hook
    /// and allowed tools; their steps stream with parent_tool_use_id and land on the chat's agent map
    /// (ClaudeDevStreamTurn). Before 2026-10-18 it was not allowed, so a Claude Dev chat never fanned out.</summary>
    private const string SubAgentTools = "Task,Agent";

    /// <summary>The brain's agent tools — the task queue, the agent inbox, handing a task to another agent, the cowork
    /// room — on every turn (owner 2026-10-18: "เปิดเครื่องมือ agent ของ brainx"): a Claude Dev chat was refused
    /// task_queue and could not hand work on. They post to the owner's own agent bus, nothing outside the machine;
    /// agent_send (a direct message to any peer) is not among them.</summary>
    private const string AgentTools =
        "mcp__brainx-brain__task_queue,mcp__brainx-brain__task_handoff,mcp__brainx-brain__task_update," +
        "mcp__brainx-brain__agent_inbox,mcp__brainx-brain__agent_peers,mcp__brainx-brain__agent_activity," +
        "mcp__brainx-brain__cowork_join,mcp__brainx-brain__cowork_leave,mcp__brainx-brain__cowork_read," +
        "mcp__brainx-brain__cowork_say,mcp__brainx-brain__cowork_task,mcp__brainx-brain__cowork_who";

    /// <summary>Extra folders a turn may add with --add-dir.</summary>
    public const int MaxAddDirs = 16;

    /// <summary>What a turn adds besides the grant: CluadeX's chat tools (the --mcp-config file of
    /// Tools.ChatToolBridge, null = none) and the folders the user allowed beside the project (--add-dir).</summary>
    public sealed record TurnExtras(string? ToolsConfig, IReadOnlyList<string> AddDirs);

    /// <summary>The extras for the turn in flight (set by the agent loop around its one delegated call).</summary>
    public static readonly AsyncLocal<TurnExtras?> Extras = new();

    /// <summary>
    /// The folders a turn gets with --add-dir: the related folders open beside the project in this chat, then the folders
    /// the user answered "Always allow" for — each once, none inside the project or inside one already added, none that
    /// is missing or a drive root / profile / system folder (<paramref name="unsafeRoot"/>), at most <see cref="MaxAddDirs"/>.
    /// </summary>
    public static List<string> ExtraDirs(string cwd, IEnumerable<string?> candidates, Func<string, bool> exists, Func<string, bool> unsafeRoot)
    {
        static string Norm(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd('\\', '/'); } catch { return ""; }
        }
        static bool Inside(string root, string p) =>
            p.Equals(root, StringComparison.OrdinalIgnoreCase) || p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        string home = Norm(cwd);
        var kept = new List<string>();
        foreach (var raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string p = Norm(raw.Trim().Trim('"'));
            if (p.Length < 3 || p.EndsWith(':')) continue;   // "" or a bare drive
            if (home.Length > 0 && Inside(home, p)) continue;
            if (kept.Any(k => Inside(k, p))) continue;
            if (!exists(p) || unsafeRoot(p)) continue;
            kept.RemoveAll(k => Inside(p, k));               // a parent replaces the children added before it
            kept.Add(p);
            if (kept.Count >= MaxAddDirs) break;
        }
        return kept;
    }

    /// <summary>The arguments for one call: read-only advice, or a worker that edits/runs as the grant allows.
    /// <paramref name="toolsConfig"/>: CluadeX's chat tools; <paramref name="addDirs"/>: allowed folders beside the project.</summary>
    public static string BuildArgs(string cwd, WorkerGrant? grant, string model, string effort,
        string? toolsConfig = null, IReadOnlyList<string>? addDirs = null)
    {
        bool worker = grant is { Edit: true } || grant is { Run: true };
        addDirs ??= Array.Empty<string>();
        bool chatTools = !string.IsNullOrWhiteSpace(toolsConfig);
        string folders = addDirs.Count == 0 ? ""
            : "The user also allowed these folders beside the project — work in them by absolute path: " + string.Join(", ", addDirs) + ". ";
        string reach = (chatTools
                ? "To work in another project, call mcp__cluadex__open_project (CluadeX asks the user) or start a chat there with " +
                  "mcp__cluadex__new_chat; a new, independent request that arrives mid-task goes to its own chat with new_chat too, " +
                  "and mcp__cluadex__close_chat lists the chats you split off and puts finished ones away. Never answer that you " +
                  "cannot open a chat or a folder. "
                : "") +
            "Other agents on this machine: task_queue / agent_inbox show work waiting, task_handoff hands a task to another agent, " +
            "cowork_* is the shared room (brainx-brain tools). ";
        string wrapper = worker
            ? "You are 'claude-dev', the coding agent of a CluadeX chat, working directly in the project. " +
              $"Project working directory: {cwd}. " +
              (grant!.Edit ? "You may create and edit files inside it. " : "You may not edit files: propose changes as diffs. ") +
              (grant.Run ? "You may run commands (builds, tests, git) in it. " : "You may not run commands. ") +
              "When the task has two or more independent parts (several folders or questions to investigate, bulky reading, " +
              "separate files to change), hand them to sub-agents with the Task tool — several in one response run in parallel, " +
              "and the user watches each one on the agent map. Keep a small task to yourself. " +
              folders +
              "Stay inside the project folder" + (addDirs.Count > 0 ? " and those folders" : "") + " unless the user's request names another. " +
              reach +
              "Reply in the language the user writes in."
            : "You are 'claude-dev', answering inside the CluadeX desktop app. " +
              $"Project working directory: {cwd}. " +
              "You have READ-ONLY tools plus the BrainX brain recall tools; propose file edits as diffs/snippets for the app to apply, don't try to write. " +
              folders + reach +
              "Reply in the language the user writes in.";

        string tools = AllowedTools + "," + AgentTools;
        if (chatTools) tools += "," + string.Join(",", Tools.ChatToolBridge.QualifiedToolNames);
        if (grant is { Edit: true }) tools += "," + EditTools;
        if (grant is { Run: true }) tools += ",Bash";
        if (worker) tools += "," + SubAgentTools;

        // Worker turns stream events (stream-json) so the chat shows each step while a long task runs; text mode
        // printed nothing until the very end — a 30-minute task looked hung.
        var args = new StringBuilder(worker ? "-p --output-format stream-json --verbose" : "-p --output-format text");
        args.Append(" --append-system-prompt ").Append(Quote(wrapper));
        args.Append(" --allowedTools ").Append(Quote(tools));
        // The chat tools' server joins the user's own MCP servers (the brain stays); --add-dir lets the CLI's file tools
        // (and acceptEdits) work in each allowed folder as they do in the project. One flag per folder: both options take
        // a list, and a list must never swallow the flag after it.
        if (chatTools) args.Append(" --mcp-config ").Append(Quote(toolsConfig!));
        foreach (var dir in addDirs) args.Append(" --add-dir ").Append(Quote(dir));
        if (grant is { Run: true }) args.Append(" --disallowedTools ").Append(Quote(NeverTools));
        if (grant is { Edit: true }) args.Append(" --permission-mode acceptEdits");
        // Every worker that may edit or run carries CluadeX's PreToolUse guard (Safety.GuardHook): the CLI's own Bash
        // never passes through CluadeX's command checks otherwise — the 2026-10-17 `rd /s /q C:\` went through there.
        if (worker)
        {
            try { args.Append(" --settings ").Append(Quote(Safety.GuardHook.SettingsFile())); }
            catch { args.Append(" --disallowedTools ").Append(Quote("Bash,PowerShell")); }   // no guard → no shell
        }
        // "default"/"auto"/empty → NO --model flag. An explicit alias routed the call to
        // API-credit billing and died with "Credit balance is too low" on a machine whose
        // Claude login is subscription-based; the CLI's own default rides the subscription.
        if (!string.IsNullOrWhiteSpace(model)
            && !model.Equals("default", StringComparison.OrdinalIgnoreCase)
            && !model.Equals("auto", StringComparison.OrdinalIgnoreCase))
            args.Append(" --model ").Append(Quote(model));
        // Claude Code takes low/medium/high (newer builds also max); xhigh/max round down to high so an
        // older CLI never rejects the flag. "auto" leaves the CLI on its own setting.
        if (effort != Models.EffortLevels.Auto)
            args.Append(" --effort ").Append(effort is "xhigh" or "max" ? "high" : effort);
        return args.ToString();
    }

    /// <summary>The model the CLI said it runs (its init line) — what "default" resolved to. Null before the first run.</summary>
    public static string? ReportedModel { get; private set; }

    /// <summary>The context window the CLI reported for its last run (result.modelUsage.*.contextWindow). Null before one.</summary>
    public static int? ReportedContextWindow { get; private set; }

    /// <summary>One stream-json line → the text the chat shows (assistant text, a short line per tool call, the final
    /// result's text only when it is an error). Null = nothing to show. <paramref name="isError"/> is set by a result
    /// line that failed.</summary>
    public static string? RenderStreamLine(string line, out bool isError, out string? resultText)
    {
        isError = false;
        resultText = null;
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var root = doc.RootElement;
            string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            // The CLI says which model "default" really is (init) and how big its window is (result.modelUsage) —
            // the status bar's "of N tokens" used to be a fixed 200K for every Claude Dev model (owner 2026-10-18:
            // "ทำไมมันไม่ใช่ 1 ล้านเหรอ").
            if (type == "system" && root.TryGetProperty("subtype", out var st) && st.GetString() == "init"
                && root.TryGetProperty("model", out var im) && im.ValueKind == System.Text.Json.JsonValueKind.String
                && im.GetString() is { Length: > 0 } initModel)
                ReportedModel = initModel;
            if (type == "result" && root.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                int window = 0;
                foreach (var m in mu.EnumerateObject())
                    if (m.Value.ValueKind == System.Text.Json.JsonValueKind.Object
                        && m.Value.TryGetProperty("contextWindow", out var cw) && cw.TryGetInt32(out int w))
                        window = Math.Max(window, w);
                if (window > 0) ReportedContextWindow = window;
            }
            if (type == "result")
            {
                isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.True;
                resultText = root.TryGetProperty("result", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.String ? r.GetString() : null;
                return null;
            }
            if (type != "assistant" || !root.TryGetProperty("message", out var msg)
                || !msg.TryGetProperty("content", out var content) || content.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;
            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                string kind = part.TryGetProperty("type", out var k) ? k.GetString() ?? "" : "";
                if (kind == "text" && part.TryGetProperty("text", out var tx))
                    sb.Append(tx.GetString()).Append('\n');
                else if (kind == "tool_use")
                {
                    string name = part.TryGetProperty("name", out var n) ? n.GetString() ?? "tool" : "tool";
                    string detail = "";
                    if (part.TryGetProperty("input", out var inp) && inp.ValueKind == System.Text.Json.JsonValueKind.Object)
                        foreach (var key in new[] { "command", "file_path", "path", "pattern", "description" })
                            if (inp.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                            { detail = v.GetString() ?? ""; break; }
                    detail = detail.Replace('\n', ' ');
                    if (detail.Length > 140) detail = detail[..139] + "…";
                    sb.Append("\n> ⏵ ").Append(name).Append(detail.Length > 0 ? " · `" + detail.Replace("`", "'") + "`" : "").Append("\n\n");
                }
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public ClaudeDevProvider(SettingsService settingsService, FileSystemService fileSystemService)
    {
        _settingsService = settingsService;
        _fileSystemService = fileSystemService;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_cliPath != null) return;
        SetLoading(true);
        try
        {
            _cliPath = await ResolveCliAsync(ct);
            SetStatus(_cliPath != null
                ? $"Ready · {_cliPath}"
                : "Claude Code CLI not found — install it or add 'claude' to PATH");
        }
        finally { SetLoading(false); }
    }

    /// <summary>
    /// `where.exe claude` first (respects however the user installed it), then the known install places. Only a real
    /// .exe is ever returned: npm's extensionless `claude` shim is a bash script Process.Start refuses, and the .cmd
    /// shim would need cmd.exe (and its quoting) in between.
    /// </summary>
    private static async Task<string?> ResolveCliAsync(CancellationToken ct)
    {
        string whereOut = "";
        try
        {
            var psi = new ProcessStartInfo("where.exe", "claude")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                whereOut = await p.StandardOutput.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
            }
        }
        catch { /* fall through to known locations */ }
        return FindCli(whereOut,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            File.Exists, Directory.Exists, d => Directory.GetDirectories(d));
    }

    /// <summary>
    /// Where the CLI is, from `where.exe claude` output and the known install places, newest first:
    /// <list type="number">
    /// <item>a claude.exe on PATH;</item>
    /// <item>the exe behind npm's shims on PATH (`npm\claude.cmd` runs `node_modules\@anthropic-ai\claude-code\bin\claude.exe`);</item>
    /// <item>the Claude desktop app's copy: `%APPDATA%\Claude\claude-code\&lt;version&gt;\[&lt;hash&gt;\]claude.exe` — the hash
    /// folder is new (2.1.29x): the old flat lookup found nothing and Claude Dev said "CLI not found" on a machine with
    /// three installs (owner 2026-10-17). Versions compare as versions (2.1.299 &gt; 2.1.30), a half-written version
    /// folder without its exe is skipped;</item>
    /// <item>the native installer's `~\.local\bin\claude.exe`.</item>
    /// </list>
    /// </summary>
    public static string? FindCli(string? whereOutput, string appData, string userProfile,
        Func<string, bool> fileExists, Func<string, bool> dirExists, Func<string, IEnumerable<string>> subdirs)
    {
        var onPath = (whereOutput ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        foreach (var l in onPath)
            if (l.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && fileExists(l)) return l;
        foreach (var l in onPath)
        {
            string? dir = Path.GetDirectoryName(l);
            if (string.IsNullOrEmpty(dir)) continue;
            string exe = Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
            if (fileExists(exe)) return exe;
        }

        try
        {
            string root = Path.Combine(appData, "Claude", "claude-code");
            if (dirExists(root))
            {
                var versions = subdirs(root)
                    .Select(d => (Dir: d, Ver: Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0)))
                    .OrderByDescending(x => x.Ver);
                foreach (var (dir, _) in versions)
                {
                    string flat = Path.Combine(dir, "claude.exe");
                    if (fileExists(flat)) return flat;
                    foreach (var hash in subdirs(dir))
                    {
                        string exe = Path.Combine(hash, "claude.exe");
                        if (fileExists(exe)) return exe;
                    }
                }
            }
        }
        catch { }

        string native = Path.Combine(userProfile, ".local", "bin", "claude.exe");
        return fileExists(native) ? native : null;
    }

    public async IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        LastTurnAuthRefused = false;
        if (_cliPath == null) await InitializeAsync(ct);
        if (_cliPath == null)
        {
            OnError?.Invoke("Claude Code CLI not found on this machine");
            yield return "Claude Code CLI ไม่พบบนเครื่องนี้ — ติดตั้ง Claude Code หรือเพิ่ม 'claude' เข้า PATH ก่อน";
            yield break;
        }

        string cwd = _fileSystemService.HasWorkingDirectory
            ? _fileSystemService.WorkingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // The CLI builds its own context (CLAUDE.md, MCP, its system prompt). CluadeX's
        // big prompt would just be noise on top — send a short wrapper instead (BuildArgs).
        var grant = Grant.Value;
        bool worker = grant is { Edit: true } || grant is { Run: true };
        var sink = worker ? StreamSink.Value : null;
        string model = _settingsService.Settings.ProviderConfigs.TryGetValue(ProviderId, out var cfg)
            && !string.IsNullOrWhiteSpace(cfg.EffectiveModelId) ? cfg.EffectiveModelId : "";
        string effort = Models.EffortLevels.Normalize(_settingsService.Settings.ReasoningEffort);
        var extras = Extras.Value;
        string args = BuildArgs(cwd, grant, model, effort, extras?.ToolsConfig, extras?.AddDirs);

        var psi = new ProcessStartInfo(_cliPath, args)
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

        // No SetLoading here: "loading" drives the full-window "กำลังโหลดโมเดล" overlay, which then covered the chat for
        // the whole turn — a worker turn of half an hour hid every step it streamed (owner 2026-10-17). The chat's own
        // running state shows the turn; loading is for InitializeAsync (finding the CLI) only.
        SetStatus("Claude Dev thinking…");
        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc == null)
            {
                OnError?.Invoke("Failed to start the Claude Code CLI");
                yield break;
            }

            // Prompt = compact replayed transcript + the new message, over stdin.
            using (var stdin = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                string transcript = BuildTranscript(history);
                if (transcript.Length > 0)
                {
                    await stdin.WriteLineAsync("<conversation_so_far>");
                    await stdin.WriteLineAsync(transcript);
                    await stdin.WriteLineAsync("</conversation_so_far>");
                    await stdin.WriteLineAsync();
                }
                await stdin.WriteLineAsync(userMessage);
            }

            // Hard ceiling so a wedged CLI can never hang the chat forever. Generous,
            // because a real turn with tool use routinely takes tens of seconds — and a worker turn carrying on a
            // whole task takes as long as the task (Stop still ends it at once).
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(worker ? TimeSpan.FromMinutes(90) : TimeSpan.FromSeconds(600));

            var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
            string? line;
            bool any = false;
            string? lastLine = null;
            while ((line = await ReadLineOrNullAsync(proc.StandardOutput, timeout.Token)) != null)
            {
                if (worker)
                {
                    string? shown = RenderStreamLine(line, out bool failed, out string? result);
                    // The structured events (rows, plan, steps) — the agent loop renders them. A UI handler that throws
                    // must not end a half-hour task.
                    try { sink?.Invoke(line); } catch { }
                    if (failed)
                    {
                        // An error result (billing, auth, a limit) is the reply when nothing else was said.
                        lastLine = result ?? line;
                        if (any && sink == null && !string.IsNullOrWhiteSpace(result)) yield return "\n⚠ " + result + "\n";
                        continue;
                    }
                    if (shown == null) continue;
                    any = true;
                    SetStatus("Claude Dev working…");
                    if (sink == null) yield return shown;
                    continue;
                }
                // The CLI reports auth/billing refusals on STDOUT with a non-zero exit,
                // not on stderr — so a one-line refusal looks like a successful reply
                // unless the exit code is consulted. Hold the first line back until we
                // know; a real answer is many lines and streams normally after it.
                if (!any && LooksLikeAuthRefusal(line)) { lastLine = line; any = false; continue; }
                any = true;
                yield return line + "\n";
            }
            await proc.WaitForExitAsync(timeout.Token);

            if (!any || proc.ExitCode != 0)
            {
                string err = (await SafeAwait(stderrTask)).Trim();
                if (err.Length > 400) err = err[..400];
                if (!any)
                {
                    // "Credit balance is too low" is an ANSWER FROM ANTHROPIC — the
                    // prompt got there and came back — so it is never a plumbing
                    // problem, and printing it raw sends the user hunting in the
                    // wrong place. The CLI can say exactly what is wrong; ask it.
                    string? explained = await ExplainAuthFailureAsync(lastLine ?? err, ct);
                    LastTurnAuthRefused = explained != null;
                    yield return explained
                        ?? (string.IsNullOrWhiteSpace(err) && string.IsNullOrWhiteSpace(lastLine)
                            ? "(claude-dev returned no output)"
                            : $"claude-dev error: {(string.IsNullOrWhiteSpace(err) ? lastLine : err)}");
                }
                else if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
                    OnError?.Invoke($"claude-dev exit {proc.ExitCode}: {err}");
            }
            SetStatus("Ready");
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            proc?.Dispose();
        }
    }

    /// <summary>Newest-first token-budgeted replay, same shape as the local history
    /// fit: the CLI call is stateless, so the transcript IS its memory of the chat.</summary>
    private static string BuildTranscript(List<ChatMessage> history)
    {
        const int budget = 6000; // tokens — the CLI's window is huge; this bounds latency, not fit
        var kept = new List<ChatMessage>();
        int spent = 0;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            int cost = CluadeX.Helpers.TokenBudget.EstimateTokens(history[i].Content);
            if (spent + cost > budget && kept.Count > 0) break;
            kept.Add(history[i]);
            spent += cost;
        }
        kept.Reverse();
        var sb = new StringBuilder();
        foreach (var m in kept)
        {
            string who = m.Role == MessageRole.Assistant ? "assistant" : "user";
            sb.Append('[').Append(who).Append("] ").AppendLine(m.Content.Trim());
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Refusals the CLI prints on stdout instead of stderr.</summary>
    public static bool LooksLikeAuthRefusal(string? line) => line != null && (
        line.Contains("Credit balance", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase)
        || line.Contains("authentication", StringComparison.OrdinalIgnoreCase)
        || line.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
        || line.Contains("/login", StringComparison.OrdinalIgnoreCase)
        || line.Contains("OAuth token", StringComparison.OrdinalIgnoreCase)
        || line.Contains("rate limit", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Open a console window running `claude auth login`: the user finishes the sign-in in their browser (CluadeX
    /// never types a password). Owner 2026-10-18: "ต้องทำให้ cluadex รู้วิธีแนะนำให้ผู้ใช้แก้ได้ด้วย" — a chat that
    /// says "Not logged in" now says how, and /login does it.
    /// </summary>
    public static bool OpenLoginTerminal(string? cliPath = null)
    {
        try
        {
            string cli = string.IsNullOrWhiteSpace(cliPath) ? "claude" : Quote(cliPath);
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k {cli} auth login --claudeai")
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });
            return true;
        }
        catch { return false; }
    }

    /// <summary>The last ChatAsync ended in a sign-in / billing refusal (its text is the guidance): the agent loop heals it.</summary>
    public bool LastTurnAuthRefused { get; private set; }

    /// <summary>Open the login console with this provider's CLI.</summary>
    public bool OpenLoginTerminal() => OpenLoginTerminal(_cliPath);

    /// <summary>
    /// What to tell the user about a refusal, given `claude auth status` (null when it could not be read). Pure, so the
    /// harness checks the wording. Null = not an auth/billing refusal.
    /// </summary>
    public static string? AuthGuidance(string refusal, string? authStatusJson)
    {
        if (!LooksLikeAuthRefusal(refusal)) return null;
        string raw = refusal.Trim();
        bool known = !string.IsNullOrWhiteSpace(authStatusJson);
        bool loggedIn = known && authStatusJson!.Contains("\"loggedIn\": true", StringComparison.OrdinalIgnoreCase);
        bool noSubscription = known && authStatusJson!.Contains("\"subscriptionType\": null", StringComparison.OrdinalIgnoreCase);

        if (loggedIn && noSubscription)
            return "🔑 **Claude Dev เข้าถึง Anthropic ได้ แต่ถูกปฏิเสธเรื่องการเรียกเก็บเงิน**\n\n"
                 + $"ข้อความจาก Claude Code: `{raw}`\n\n"
                 + "`claude auth status` บอกว่า **ล็อกอินอยู่ แต่ไม่มี subscription ผูกไว้** — ทุก request จึงไปคิดเงินจาก "
                 + "Anthropic Console credits (ซึ่งหมด) แทนแพ็กเกจ Claude ของคุณ\n\n"
                 + "**วิธีแก้ (ครั้งเดียวจบ):**\n"
                 + "1. พิมพ์ `/login` ในช่องแชทนี้ — CluadeX จะเปิดหน้าต่างล็อกอินให้ (หรือเปิดเทอร์มินัลแล้วรัน `claude auth logout` ตามด้วย `claude auth login --claudeai`)\n"
                 + "2. ยืนยันบัญชีในเบราว์เซอร์ที่เด้งขึ้นมา เลือกบัญชีที่มีแพ็กเกจ Claude\n"
                 + "3. กลับมากด **ลองใหม่** — ไม่ต้องรีสตาร์ท CluadeX";

        if (known && loggedIn && !raw.Contains("log", StringComparison.OrdinalIgnoreCase))
            return $"⚠ Claude Dev ถูกปฏิเสธ: `{raw}`\n\n"
                 + "ล็อกอินยังใช้ได้อยู่ ถ้าเป็นเรื่องโควตาหรือ rate limit ให้รอสักครู่แล้วกด **ลองใหม่** "
                 + "หรือเปลี่ยนไปใช้โมเดลอื่นในกล่องเลือกโมเดลชั่วคราว";

        return "🔑 **Claude Dev ยังไม่ได้ล็อกอิน (หรือการล็อกอินหมดอายุ)**\n\n"
             + $"ข้อความจาก Claude Code: `{raw}`\n\n"
             + "Claude Dev ใช้บัญชีของ Claude Code CLI ในเครื่องนี้ การล็อกอินหลุดได้เมื่อ token หมดอายุแล้วต่ออายุไม่สำเร็จ "
             + "— มักเกิดเมื่อเปิด Claude Code หลายหน้าต่างพร้อมกันจนแย่งกันต่ออายุ หรือหลังไฟล์ `~/.claude/.credentials.json` ถูกเขียนใหม่\n\n"
             + "**วิธีแก้:**\n"
             + "1. พิมพ์ `/login` ในช่องแชทนี้ — CluadeX จะเปิดหน้าต่างล็อกอินให้ (หรือเปิดเทอร์มินัลแล้วรัน `claude auth login --claudeai`)\n"
             + "2. ยืนยันบัญชีในเบราว์เซอร์ที่เด้งขึ้นมา\n"
             + "3. กลับมากด **ลองใหม่** — ไม่ต้องรีสตาร์ท CluadeX\n\n"
             + "ระหว่างนี้ใช้ต่อได้ด้วยการเลือกโมเดลอื่นในกล่องเลือกโมเดล (เช่น Anthropic API ที่มี key หรือโมเดลในเครื่อง)";
    }

    /// <summary>
    /// Turn an auth/billing refusal into the sentence that actually fixes it, by
    /// asking `claude auth status` instead of guessing.
    ///
    /// Measured on this machine 2026-08-05: `-p` returned "Credit balance is too low"
    /// while `auth status` reported `loggedIn: true`, `authMethod: "claude.ai"` and
    /// **`subscriptionType: null`** — signed in, but on a token with no subscription
    /// attached, so every request billed to Console credits that are at zero. The raw
    /// message sends you looking for a billing page; the real fix is one re-login.
    /// </summary>
    public async Task<string?> ExplainAuthFailureAsync(string? refusal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refusal) || !LooksLikeAuthRefusal(refusal)) return null;
        return AuthGuidance(refusal, await ReadAuthStatusAsync(ct));
    }

    /// <summary>`claude auth status` as the CLI prints it (JSON), or null when the CLI could not be asked.</summary>
    public async Task<string?> ReadAuthStatusAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
        if (_cliPath == null) return null;
        try
        {
            var psi = new ProcessStartInfo(_cliPath, "auth status")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string json = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return json;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>The CLI says it is signed in right now.</summary>
    public async Task<bool> IsLoggedInAsync(CancellationToken ct) =>
        (await ReadAuthStatusAsync(ct))?.Contains("\"loggedIn\": true", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task<string?> ReadLineOrNullAsync(StreamReader reader, CancellationToken ct)
    {
        try { return await reader.ReadLineAsync(ct); }
        catch (OperationCanceledException) { return null; }
    }

    private static async Task<string> SafeAwait(Task<string> t)
    {
        try { return await t; } catch { return ""; }
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

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
        if (_cliPath == null) return (false, "Claude Code CLI not found (is 'claude' on PATH?)");
        try
        {
            var psi = new ProcessStartInfo(_cliPath, "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string v = (await p.StandardOutput.ReadToEndAsync(ct)).Trim();
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 ? (true, $"Claude Code {v}") : (false, $"exit {p.ExitCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private void SetStatus(string s) { _statusMessage = s; OnStatusChanged?.Invoke(s); }
    private void SetLoading(bool b) { _isLoading = b; OnLoadingChanged?.Invoke(b); }

    public void Dispose() { }
}
