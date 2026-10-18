using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Hook execution service modeled after Claude Code's hooks system.
/// Supports five phases:
///   - PreToolUse — before a tool runs (the only phase that can block a call)
///   - PostToolUse — after a tool runs (informational, never blocks)
///   - Stop — after the agentic loop returns its final response
///   - SessionStart — once at app startup after services initialize
///   - PreCompact — right before microcompaction strips/truncates history
///
/// Hook sources:
///   - .cluadex/hooks.json (project-specific)
///   - ~/.cluadex/hooks.json (user-global)
///   - ~/.cluadex/hooks-bundled.json (managed by HookBundleService — toggleable library)
///   - Enabled plugin hooks.json files
///
/// Hook config format:
/// {
///   "hooks": {
///     "PreToolUse":  [ { "matcher": "run_command", "command": "powershell -File ..." } ],
///     "PostToolUse": [ { "matcher": "edit_file",   "command": "prettier --write {path}" } ],
///     "Stop":        [ { "matcher": "*", "command": "powershell -File stop-pattern-extract.ps1 -Cost {cost}" } ],
///     "SessionStart":[ { "matcher": "*", "command": "powershell -File sessionstart-load-handoff.ps1" } ],
///     "PreCompact":  [ { "matcher": "*", "command": "powershell -File precompact-save-snapshot.ps1" } ]
///   }
/// }
///
/// EXIT CODES (the Claude Code contract):
///   - 0  allow. stdout is the hook's message.
///   - 2  DELIBERATE BLOCK. stderr is the reason handed to the model. Never second-guessed — whatever
///        stderr says (a hook may quote the command it refused, and the command is model-written).
///   - anything else: a block too (so an older hook that refuses with exit 1 keeps working), UNLESS the
///        hook itself is broken — recognized only by its interpreter's OWN error at the START of stderr:
///        cmd's "'x' is not recognized…" / "… was unexpected at this time.", PowerShell's
///        "X : The term 'X' is not recognized…", a parse error of THIS hook's .ps1 ("At C:\…\hook.ps1:…"),
///        or PowerShell refusing to load THIS hook's .ps1 under the execution policy (PowerShell 7's
///        ConciseView forms too: "X: The term 'X'…", "x.ps1: File … cannot be loaded",
///        "ParserError: C:\…\hook.ps1:3"). A broken hook never
///        decided anything, so it does not block; the failure goes to the Debug Log.
///   A PreToolUse hook that cannot be run at all (its command line is too long even without the tool's
///   values, the arguments file cannot be written, the process cannot start) BLOCKS the call: a safety
///   gate that cannot look must not wave the call through. So does a PreToolUse hook that puts a tool
///   value ({command} {path} {arguments} {tool}) on its command line when that value had to be left out
///   (too long for the line) — unless the hook reads the exact values itself (its command, or a script it
///   names, mentions CLUADEX_HOOK_PAYLOAD_FILE or CLUADEX_HOOK_OMITTED). A legacy hook that reads only
///   {command} would otherwise judge an empty string and let a padded `rm -rf /` through. Other phases log
///   it and carry on.
///
/// Hooks are started as <c>cmd.exe /v:off /s /c "&lt;command&gt;"</c>: with /s cmd strips only the outer
/// pair of quotes we add, so a template that starts with a quoted exe ("C:\Program Files\...\node.exe" …
/// "{path}") keeps the quoting it was written with (plain /c dropped the first and last quote of such a
/// line, which put a quoted {path} outside quotes and let "a &amp; calc" run calc). A template written in
/// the classic plain-/c idiom — the whole line wrapped in one more pair of quotes,
/// <c>""C:\Program Files\x\tool.exe" "{path}""</c> — has that extra pair removed first (under /s it broke the
/// line, and the hook was classified as broken = not blocking).
///
/// Every hook process gets these environment variables (the preferred, exact interface — no
/// command-line quoting, no length limit):
///   - CLUADEX_HOOK_TOOL          tool name ("" outside tool phases)
///   - CLUADEX_HOOK_PATH          the call's "path" argument ("" when it has none)
///   - CLUADEX_HOOK_CWD           the project folder
///   - CLUADEX_HOOK_PAYLOAD_FILE  a temp UTF-8 JSON file holding ALL the tool arguments; deleted
///                                once the hook exits ("" outside tool phases)
///   - CLUADEX_HOOK_PHASE         PreToolUse / PostToolUse / Stop / SessionStart / PreCompact
///   - CLUADEX_HOOK_OMITTED       comma list of command-line tokens left empty because the value was too
///                                long for the command line (e.g. "command") — read the payload file instead
///
/// Substitution tokens still work for existing hooks.json files. Tool values are UNTRUSTED (the model
/// writes them) and are sanitized for where they land (see <see cref="ExpandTemplate"/>): outside quotes
/// every cmd metacharacter is dropped — a caret escape only survives cmd's FIRST parse, and the left side
/// of a pipe or a for /f ('…') command is parsed again by a child cmd after the carets are gone:
///   - {tool} {path} {command} {arguments}          — PreToolUse / PostToolUse ({command} is the call's
///                                                    "command", or a powershell call's "script")
///   - {model} {cost} {tokens} {turn_count}         — Stop
///   - {cwd}                                        — SessionStart / any
///   - {message_count} {token_estimate}             — PreCompact
/// %CLUADEX_HOOK_TOOL%, %CLUADEX_HOOK_PATH% and %CLUADEX_HOOK_CWD% in a command are treated as {tool} /
/// {path} / {cwd}: cmd expands %VAR% BEFORE it parses &amp; | &lt; &gt;, so a raw expansion of a tool value
/// was an injection. A hook that needs the exact value reads $env:CLUADEX_HOOK_PATH inside its script
/// (a .bat/.cmd hook: "!CLUADEX_HOOK_PATH!" under setlocal EnableDelayedExpansion — never %CLUADEX_HOOK_PATH%,
/// which the batch parser expands before it parses &amp; and |).
/// {arguments} longer than <see cref="MaxInlineArgumentsChars"/> becomes "{}" — read the payload file
/// instead. It used to inline the whole JSON (a write_file's entire file content): past cmd.exe's
/// 8,191-char limit the hook died with "The command line is too long." (exit 1 = "blocked"), so every
/// write over ~8 KB was refused by the Secret Scan hook. In PreToolUse such an omitted {arguments} blocks
/// the call like any other omitted tool value, unless the hook reads the payload file (see above).
///
/// Matchers: "*", an exact tool name, a wildcard ("git_*"), or alternatives "write_file|edit_file".
/// </summary>
public class HookService
{
    private readonly FileSystemService _fileSystemService;
    private readonly SettingsService _settingsService;
    private readonly PluginService _pluginService;
    private readonly DebugLogService? _log;

    private List<HookDefinition>? _cachedHooks;
    private HashSet<string>? _trustedFolders;
    private readonly object _gate = new(); // guards _trustedFolders + _cachedHooks across UI/agent/read-loop threads

    /// <summary>Raised when the open project defines .cluadex/hooks.json but the folder isn't trusted yet.
    /// The UI should offer to trust it before those auto-run scripts are enabled.</summary>
    public event Action<string>? OnUntrustedProjectHooks;

    public HookService(FileSystemService fileSystemService, SettingsService settingsService, PluginService pluginService,
        DebugLogService? debugLog = null)
    {
        _fileSystemService = fileSystemService;
        _settingsService = settingsService;
        _pluginService = pluginService;
        _log = debugLog;
        _ = Task.Run(SweepStalePayloadFolders);

        // Reload hooks when plugins change OR the open project changes — the latter so workspace trust
        // is re-evaluated for the new folder and a previous project's hooks don't linger.
        _pluginService.PluginsChanged += ReloadHooks;
        _fileSystemService.OnWorkingDirectoryChanged += ReloadHooks;
    }

    // ─── Workspace trust (project-local hooks come from the opened repo, which may be hostile) ───

    private string TrustFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex", "trusted-hook-folders.json");

    private static string NormalizeFolder(string folder)
    {
        try
        {
            string full = Path.GetFullPath(folder);
            // Resolve junctions/symlinks to the REAL target so trust is keyed on actual content, not on a
            // name that can be re-pointed after trust (trust-then-swap: trust C:\repo, later make it a
            // junction to attacker content). Lexical GetFullPath alone doesn't resolve reparse points.
            try
            {
                var target = new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true);
                if (target != null) full = target.FullName;
            }
            catch { /* not a link / inaccessible → fall back to the lexical full path */ }
            return full.TrimEnd('\\', '/').ToLowerInvariant();
        }
        catch { return folder.ToLowerInvariant(); }
    }

    private HashSet<string> LoadTrustedFolders()
    {
        if (_trustedFolders != null) return _trustedFolders;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(TrustFilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(TrustFilePath));
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var el in doc.RootElement.EnumerateArray())
                        if (el.GetString() is { Length: > 0 } p) set.Add(NormalizeFolder(p));
            }
        }
        catch { /* corrupt trust file → trust nothing (safe default) */ }
        _trustedFolders = set;
        return set;
    }

    /// <summary>True if the user has explicitly trusted project-local hooks for this folder.</summary>
    public bool IsProjectHooksTrusted(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return false;
        string key = NormalizeFolder(folder);
        lock (_gate) return LoadTrustedFolders().Contains(key);
    }

    /// <summary>Persist trust for a folder's project-local hooks, then reload so they take effect.</summary>
    public void TrustProjectHooks(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        string key = NormalizeFolder(folder);
        lock (_gate)
        {
            var set = LoadTrustedFolders();
            if (!set.Add(key)) return; // already trusted
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TrustFilePath)!);
                File.WriteAllText(TrustFilePath, JsonSerializer.Serialize(set.ToList()));
            }
            catch { /* best-effort persist */ }
        }
        ReloadHooks();
    }

    /// <summary>Execute pre-tool hooks. Returns false if any hook blocks execution.</summary>
    public async Task<HookResult> ExecutePreToolHooksAsync(ToolCall call, CancellationToken ct = default)
    {
        var hooks = GetHooksForPhase("PreToolUse", call);
        foreach (var hook in hooks)
        {
            var result = await RunHookAsync(hook, BuildToolContext(call), ct);
            // A timeout is not a verdict. On a loaded machine (the agent's own build/test running) a PowerShell hook's
            // cold start alone passed 10 s and the bundled dangerous-command check BLOCKED an ordinary command
            // (Reverse X run, 2026-10-16). Run it once more, warm, with twice the time; only a second timeout blocks.
            if (!result.Success && result.TimedOut && !ct.IsCancellationRequested)
            {
                _log?.Warn("Hooks", $"PreToolUse hook timed out — retrying once with {hook.TimeoutMs * 2} ms: {Describe(hook)}");
                int was = hook.TimeoutMs;
                hook.TimeoutMs = was * 2;
                try { result = await RunHookAsync(hook, BuildToolContext(call), ct); }
                finally { hook.TimeoutMs = was; }
            }
            if (!result.Success)
                return result; // Block tool execution
        }
        return new HookResult { Success = true };
    }

    /// <summary>Execute post-tool hooks (informational, doesn't block).</summary>
    public async Task ExecutePostToolHooksAsync(ToolCall call, ToolResult result, CancellationToken ct = default)
    {
        var hooks = GetHooksForPhase("PostToolUse", call);
        foreach (var hook in hooks)
        {
            try
            {
                await RunHookAsync(hook, BuildToolContext(call), ct);
            }
            catch (Exception ex) { _log?.Warn("Hooks", $"PostToolUse hook threw: {Describe(hook)}", ex); } // best-effort
        }
    }

    /// <summary>
    /// Execute Stop hooks after the agentic loop returns. Best-effort — never throws.
    /// Pattern-extract hooks feed InstinctService.RecordObservation via stdout JSON.
    /// </summary>
    public async Task ExecuteStopHooksAsync(HookSessionContext context, CancellationToken ct = default)
    {
        var hooks = GetHooksForPhase("Stop", "*");
        foreach (var hook in hooks)
        {
            try { await RunHookAsync(hook, context, ct); }
            catch (Exception ex) { _log?.Warn("Hooks", $"Stop hook threw: {Describe(hook)}", ex); }
        }
    }

    /// <summary>Execute SessionStart hooks. Fired once on app startup after services build.</summary>
    public async Task ExecuteSessionStartHooksAsync(HookSessionContext context, CancellationToken ct = default)
    {
        var hooks = GetHooksForPhase("SessionStart", "*");
        foreach (var hook in hooks)
        {
            try { await RunHookAsync(hook, context, ct); }
            catch (Exception ex) { _log?.Warn("Hooks", $"SessionStart hook threw: {Describe(hook)}", ex); }
        }
    }

    /// <summary>Execute PreCompact hooks. Fired right before microcompaction trims history.</summary>
    public async Task ExecutePreCompactHooksAsync(HookSessionContext context, CancellationToken ct = default)
    {
        var hooks = GetHooksForPhase("PreCompact", "*");
        foreach (var hook in hooks)
        {
            try { await RunHookAsync(hook, context, ct); }
            catch (Exception ex) { _log?.Warn("Hooks", $"PreCompact hook threw: {Describe(hook)}", ex); }
        }
    }

    private HookSessionContext BuildToolContext(ToolCall call)
    {
        var ctx = new HookSessionContext { ToolCall = call };
        return ctx;
    }

    /// <summary>Get hooks matching a phase and tool call.</summary>
    private List<HookDefinition> GetHooksForPhase(string phase, ToolCall call)
    {
        string canonical = CanonicalToolName(call);
        return LoadHooks()
            .Where(h => h.Phase.Equals(phase, StringComparison.OrdinalIgnoreCase))
            .Where(h => MatchesToolName(h.Matcher, call.ToolName)
                        || (canonical.Length > 0 && MatchesToolName(h.Matcher, canonical)))
            .ToList();
    }

    /// <summary>Get hooks matching a phase and tool name.</summary>
    private List<HookDefinition> GetHooksForPhase(string phase, string toolName)
    {
        return LoadHooks()
            .Where(h => h.Phase.Equals(phase, StringComparison.OrdinalIgnoreCase))
            .Where(h => MatchesToolName(h.Matcher, toolName))
            .ToList();
    }

    /// <summary>
    /// The snake_case name of the built-in tool behind a call (WriteFile → "write_file"). The text
    /// protocol accepts aliases ("writefile", "apply_edits") and keeps the alias as ToolName, so a
    /// matcher written against the documented name used to miss those calls entirely.
    /// </summary>
    private static string CanonicalToolName(ToolCall call)
    {
        if (call.Type == ToolType.McpTool) return "";
        // The documented name is not the enum's snake_case ("power_shell"): without this a call made
        // through the "pwsh" / "ps" aliases skipped every hook matched on "powershell".
        if (call.Type == ToolType.PowerShell) return "powershell";
        string name = call.Type.ToString();
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    /// <summary>Check if a hook matcher matches the tool name. Supports wildcards and "a|b"
    /// alternatives (the Claude Code form, e.g. "write_file|edit_file|multi_edit").</summary>
    public static bool MatchesToolName(string matcher, string toolName)
    {
        if (string.IsNullOrEmpty(matcher) || matcher == "*") return true;

        if (matcher.Contains('|'))
        {
            foreach (var alt in matcher.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (MatchesToolName(alt, toolName)) return true;
            return false;
        }

        // Exact match
        if (matcher.Equals(toolName, StringComparison.OrdinalIgnoreCase)) return true;

        // Wildcard match: "git_*" matches every git tool
        if (matcher.Contains('*'))
        {
            string pattern = Regex.Escape(matcher).Replace("\\*", ".*");
            return Regex.IsMatch(toolName, $"^{pattern}$", RegexOptions.IgnoreCase);
        }

        return false;
    }

    /// <summary>{arguments} larger than this is not put on the command line (see class summary).</summary>
    public const int MaxInlineArgumentsChars = 2000;

    /// <summary>The exit code a hook uses to block on purpose (Claude Code's contract). Never reclassified.</summary>
    public const int BlockExitCode = 2;

    /// <summary>cmd.exe refuses a longer line ("The command line is too long."), and that refusal
    /// arrives as exit code 1 — indistinguishable from a hook deliberately blocking.</summary>
    private const int MaxCmdLineChars = 8000;

    /// <summary>A hook's stderr handed to the model is capped at this many characters.</summary>
    private const int MaxMessageChars = 2000;

    /// <summary>Tokens whose values come from the tool call — i.e. from the model. Untrusted.</summary>
    private static readonly string[] ToolTokens = { "{path}", "{command}", "{arguments}", "{tool}" };

    private static string L(string key) => LocalizedResources.Instance[key];

    /// <summary>Run one hook definition and return its result. Public so the Hook Library and the
    /// contract harness can exercise the exact code path the agent uses.</summary>
    public async Task<HookResult> RunHookAsync(HookDefinition hook, HookSessionContext context, CancellationToken ct)
    {
        var call = context.ToolCall;
        // PreToolUse is the only phase whose verdict matters: a hook there that cannot run must not
        // turn into "allowed" — that is exactly what padding / a NUL byte / a full temp folder exploited.
        bool gate = hook.Phase.Equals("PreToolUse", StringComparison.OrdinalIgnoreCase);
        string cwd = _fileSystemService.HasWorkingDirectory ? _fileSystemService.WorkingDirectory : Environment.CurrentDirectory;

        // ── Values for the substitution tokens ──────────────────────────
        string argsJson = "{}";
        if (call != null)
        {
            try
            {
                argsJson = JsonSerializer.Serialize(call.Arguments, new JsonSerializerOptions
                {
                    // Thai / emoji stay readable for a hook that greps the payload.
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            }
            catch { argsJson = "{}"; }
        }

        string template = StripExtraOuterQuotes(RewriteHookEnvReferences(hook.Command ?? ""));
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{tool}"] = call?.ToolName ?? "",
            ["{path}"] = call?.GetArg("path", "") ?? "",
            ["{command}"] = call == null ? "" : CommandText(call),
            ["{arguments}"] = call != null && argsJson.Length <= MaxInlineArgumentsChars ? argsJson : "{}",
            ["{model}"] = context.Model ?? "",
            ["{cost}"] = context.SessionCostUsd?.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) ?? "0",
            ["{tokens}"] = context.SessionTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0",
            ["{turn_count}"] = context.TurnCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0",
            ["{message_count}"] = context.MessageCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0",
            ["{token_estimate}"] = context.TokenEstimate?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0",
            ["{cwd}"] = cwd,
        };
        var omitted = new List<string>();
        if (call != null && argsJson.Length > MaxInlineArgumentsChars && template.Contains("{arguments}", StringComparison.Ordinal))
            omitted.Add("arguments");

        string command = ExpandTemplate(template, values);

        // A tool value too long for cmd.exe used to skip the hook outright ("could not run — not
        // blocking"), so padding a command past 8,000 chars walked past every user PreToolUse hook that
        // put {command} on its command line. Drop the longest tool-controlled value(s) instead and still
        // run the hook; it can read the exact value from CLUADEX_HOOK_PAYLOAD_FILE.
        if (command.Length > MaxCmdLineChars && call != null)
        {
            var present = ToolTokens.Where(t => template.Contains(t, StringComparison.Ordinal))
                                    .OrderByDescending(t => values[t].Length).ToList();
            foreach (var tok in present)
            {
                string empty = tok == "{arguments}" ? "{}" : "";
                if (values[tok] == empty) continue;
                values[tok] = empty;
                string name = tok.Trim('{', '}');
                if (!omitted.Contains(name)) omitted.Add(name);
                command = ExpandTemplate(template, values);
                if (command.Length <= MaxCmdLineChars) break;
            }
        }
        if (command.Length > MaxCmdLineChars)
            return CouldNotRun(hook, gate, "hook.blockedTooLong",
                $"command line is {command.Length} chars (limit {MaxCmdLineChars}) even without the tool's values");

        // …but a PreToolUse hook that decides on a value it was NOT given would judge an empty string: a
        // legacy hook reading only {command} allowed a padded `rm -rf / <9,000 spaces>`. Unless the hook
        // reads the exact values itself, a gate that cannot see the call stops it.
        if (gate && omitted.Count > 0 && !ReadsExactValues(hook.Command ?? "", cwd))
            return CouldNotRun(hook, true, "hook.blockedTooLong",
                $"the tool value(s) {string.Join(",", omitted)} did not fit on the hook's command line and the hook "
                + "does not read CLUADEX_HOOK_PAYLOAD_FILE / CLUADEX_HOOK_OMITTED");

        Process? proc = null;
        string? payloadFile = null;
        try
        {
            // ── The hook contract: exact values in the environment, full arguments in a file ──
            if (call != null)
            {
                payloadFile = await WritePayloadAsync(argsJson, ct);
                if (payloadFile == null)
                {
                    if (gate)
                        return CouldNotRun(hook, true, "hook.blockedPayload", "the arguments file could not be written to the temp folder");
                    _log?.Warn("Hooks", $"{hook.Phase} hook runs WITHOUT CLUADEX_HOOK_PAYLOAD_FILE (temp folder not writable): {Describe(hook)}");
                }
            }

            var psi = new ProcessStartInfo(CmdExePath(), "/v:off /s /c \"" + command + "\"")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // A NUL can't live in an environment block — Process.Start threw on it, and a hook that never
            // started used to mean "allowed". Strip it (the payload file keeps the value, JSON-escaped).
            psi.Environment["CLUADEX_HOOK_TOOL"] = EnvSafe(call?.ToolName);
            psi.Environment["CLUADEX_HOOK_PATH"] = EnvSafe(call?.GetArg("path", ""));
            psi.Environment["CLUADEX_HOOK_CWD"] = EnvSafe(cwd);
            psi.Environment["CLUADEX_HOOK_PAYLOAD_FILE"] = payloadFile ?? "";
            psi.Environment["CLUADEX_HOOK_PHASE"] = EnvSafe(hook.Phase);
            psi.Environment["CLUADEX_HOOK_OMITTED"] = string.Join(",", omitted);

            try { proc = Process.Start(psi); }
            catch (Exception ex)
            {
                return CouldNotRun(hook, gate, "hook.blockedStart", "the hook process could not be started", ex);
            }
            if (proc == null)
                return CouldNotRun(hook, gate, "hook.blockedStart", "Process.Start returned no process");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(hook.TimeoutMs);

            // Read stdout/stderr concurrently to avoid pipe deadlock
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            return Classify(hook, proc.ExitCode, stdout, stderr, cwd);
        }
        catch (OperationCanceledException)
        {
            try { proc?.Kill(true); } catch { }
            bool cancelled = ct.IsCancellationRequested;
            _log?.Warn("Hooks", $"{hook.Phase} hook {(cancelled ? "cancelled" : $"timed out after {hook.TimeoutMs} ms")}: {Describe(hook)}");
            return new HookResult
            {
                Success = false,
                ExitCode = -1,
                TimedOut = !cancelled,
                Message = cancelled ? L("hook.cancelled") : string.Format(L("hook.timedOut"), hook.TimeoutMs),
            };
        }
        catch (Exception ex)
        {
            _log?.Error("Hooks", $"{hook.Phase} hook failed: {Describe(hook)}", ex);
            return new HookResult { Success = false, ExitCode = -1, Message = L("hook.failed") };
        }
        finally
        {
            proc?.Dispose();
            if (payloadFile != null) TryDelete(payloadFile);
        }
    }

    /// <summary>Exit 0 = allow, exit 2 = deliberate block (never reclassified), other = block unless
    /// the hook itself is broken (see class summary).</summary>
    private HookResult Classify(HookDefinition hook, int exitCode, string stdout, string stderr, string cwd)
    {
        if (exitCode == 0)
            return new HookResult { Success = true, ExitCode = 0, Message = stdout.Trim() };

        // Exit 2 is the hook saying "no". Its stderr may quote the refused command — which the model
        // wrote — so nothing in it may turn the block into an allow.
        if (exitCode != BlockExitCode && LooksLikeBrokenHook(hook.Command ?? "", stderr, cwd))
        {
            // A hook that CRASHES (script parse error, missing exe, a PC whose execution policy refuses the
            // script) never decided anything. A single mis-encoded bundled .ps1 used to parse-fail on every
            // run and silently brick EVERY write_file. Report it, do NOT block.
            _log?.Warn("Hooks", $"{hook.Phase} hook script error (exit {exitCode}) — not blocking: "
                                + $"{Trunc(FirstLine(stderr), 300)} · hook: {Describe(hook)}");
            return new HookResult { Success = true, ExitCode = 0, Message = string.Format(L("hook.scriptError"), ErrorExcerpt(stderr)) };
        }

        string msg = CleanHookMessage(stderr);
        if (msg.Length == 0) msg = string.Format(L("hook.refused"), exitCode);
        if (hook.Phase.Equals("PreToolUse", StringComparison.OrdinalIgnoreCase))
            _log?.Info("Hooks", $"PreToolUse hook refused the call (exit {exitCode}): {Trunc(msg, 300)} · hook: {Describe(hook)}");
        else
            _log?.Debug("Hooks", $"{hook.Phase} hook exited {exitCode}: {Trunc(msg, 300)} · hook: {Describe(hook)}");
        return new HookResult { Success = false, ExitCode = exitCode, Message = msg };
    }

    /// <summary>The hook never ran. In PreToolUse that blocks (localized reason); elsewhere it is logged
    /// and the app carries on.</summary>
    private HookResult CouldNotRun(HookDefinition hook, bool gate, string blockKey, string detail, Exception? ex = null)
    {
        if (gate)
        {
            _log?.Warn("Hooks", $"PreToolUse hook could not run — call BLOCKED: {detail} · hook: {Describe(hook)}", ex);
            return new HookResult { Success = false, ExitCode = -1, Message = L(blockKey) };
        }
        _log?.Warn("Hooks", $"{hook.Phase} hook could not run — not blocking: {detail} · hook: {Describe(hook)}", ex);
        return new HookResult { Success = true, ExitCode = 0, Message = L("hook.notRun") };
    }

    // ─── Broken-hook detection (only for exit codes other than 0 and 2) ───

    // A broken hook is recognized only by the error its OWN interpreter prints, at the very START of
    // stderr — never by a phrase anywhere in it. The old check searched the whole stderr, and hooks echo
    // model-written text there (a refused command, a rejected path): a path such as
    // ...\Startup\ParserError.bat turned a block into an allow. PowerShell wraps its messages at the
    // console width (sometimes inside a path), so its forms are matched with whitespace removed.

    /// <summary>cmd.exe's own errors, as the whole first line of stderr (cmd does not wrap).</summary>
    private static readonly Regex[] CmdBrokenFirstLine =
    {
        // The hook's program does not exist (node / python not installed).
        new(@"^'[^'\r\n]+' is not recognized as an internal or external command", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        // Batch-only syntax (e.g. `%%b` outside a .bat) and other parse errors.
        new(@"^\S[^\r\n]{0,200} was unexpected at this time\.\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^The syntax of the command is incorrect\.\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        // A nested command line we didn't build — cmd never ran it.
        new(@"^The command line is too long\.\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    };

    /// <summary>PowerShell: a command the hook calls does not exist — the FIRST line of stderr starts
    /// "X : The term 'X'" (Windows PowerShell) or "X: The term 'X'" (PowerShell 7's ConciseView), X a single
    /// token (no whitespace, ':' or '\', at most 100 chars). The rest of the message ("… is not recognized as
    /// the name / a name of a cmdlet") is checked on whitespace-free text, because PowerShell wraps it at the
    /// console width.</summary>
    private static readonly Regex PsMissingHeadRx = new(@"^(?<n>[^\s:\\'""&|<>]{1,100}) ?: The term '",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>PowerShell 7's ConciseView for an error raised INSIDE a script names its position on line 1:
    /// "&lt;reason&gt;: C:\…\x.ps1:3", then a "Line |" box with the message ("ParserError: …" for a parse error,
    /// "Get-Foo: …" for a missing command).</summary>
    private static readonly Regex PsConciseAtRx = new(@"^(?<n>[^\s:\\'""&|<>]{1,100}): (?<p>.+):\d+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>.ps1 paths in a hook command: "double-quoted", 'single-quoted' (a -Command "&amp; 'C:\x.ps1'"
    /// hook) or bare.</summary>
    private static readonly Regex Ps1PathRx = new(@"""([^""]+?\.ps1)""|'([^']+?\.ps1)'|([^\s""']+?\.ps1)(?=[\s""']|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Script files a hook command names (the hook's own code, read to see what it calls / reads).</summary>
    private static readonly Regex ScriptPathRx = new(
        @"""([^""]+?\.(?:ps1|psm1|bat|cmd|js|mjs|cjs|ts|py|rb|pl|sh))""|'([^']+?\.(?:ps1|psm1|bat|cmd|js|mjs|cjs|ts|py|rb|pl|sh))'"
        + @"|([^\s""']+?\.(?:ps1|psm1|bat|cmd|js|mjs|cjs|ts|py|rb|pl|sh))(?=[\s""']|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A hook script larger than this is not read for the checks above.</summary>
    private const long MaxScannedScriptBytes = 2 * 1024 * 1024;

    private static bool LooksLikeBrokenHook(string template, string stderr, string cwd)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return false;
        string first = FirstLine(stderr.TrimStart('\uFEFF'));
        foreach (var rx in CmdBrokenFirstLine)
            if (rx.IsMatch(first)) return true;

        string head = Squash(stderr);

        // A command THIS hook calls does not exist. The name must be on the first line, one token, and appear
        // in the hook's own command or script — a hook that echoes model text first ("blocked: Get-Foo Bar :
        // The term …", or a name the hook never calls) is not mistaken for a broken one.
        var missing = PsMissingHeadRx.Match(first);
        if (missing.Success)
        {
            string name = missing.Groups["n"].Value;
            string term = name + ":Theterm'" + name + "'isnotrecognizedas";
            if ((head.StartsWith(term + "thenameofacmdlet", StringComparison.OrdinalIgnoreCase)       // Windows PowerShell
                 || head.StartsWith(term + "anameofacmdlet", StringComparison.OrdinalIgnoreCase))     // PowerShell 7
                && HookCodeMentions(template, cwd, name))
                return true;
        }
        // PowerShell 7's positioned form is "<reason>: <script>:<line>" followed directly by its "Line |" box.
        var conciseAt = PsConciseAtRx.Match(first);
        if (conciseAt.Success)
        {
            string[] lines = stderr.TrimStart('\uFEFF').Trim().Split('\n', 2);
            if (lines.Length < 2 || !Squash(lines[1]).StartsWith("Line|", StringComparison.OrdinalIgnoreCase))
                conciseAt = Match.Empty;
        }

        foreach (var (raw, full) in HookScriptPaths(Ps1PathRx, template, cwd))
        {
            string s = Squash(full);
            string refused = "File" + s + "cannotbeloaded";
            // Execution policy refused THIS hook's script: "File C:\…\x.ps1 cannot be loaded because running
            // scripts is disabled" / "… is not digitally signed". (Was: "running scripts is disabled" anywhere.)
            if (head.StartsWith(refused, StringComparison.OrdinalIgnoreCase)) return true;
            // PowerShell names the invocation first: "& : File …", ". : File …", the script path as it was
            // called (PowerShell 7's ConciseView: "&: File …", "x.ps1: File …", "SecurityError: File …"). Only
            // those prefixes — on the first line — count; anything else in front ("blocked: x : File …") is the
            // hook talking, not PowerShell.
            foreach (string prefix in new[] { "&", ".", raw.Trim(), full, Path.GetFileName(full), "SecurityError" })
            {
                if (prefix.Length == 0) continue;
                if ((first.StartsWith(prefix + " : ", StringComparison.OrdinalIgnoreCase)
                     || first.StartsWith(prefix + ": ", StringComparison.OrdinalIgnoreCase))
                    && head.StartsWith(Squash(prefix) + ":" + refused, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            // THIS hook's script does not parse (a mis-encoded .ps1 once bricked every write_file):
            // "At C:\…\x.ps1:3 char:5 … ParserError".
            if (head.StartsWith("At" + s + ":", StringComparison.OrdinalIgnoreCase)
                && (head.Contains("ParserError", StringComparison.OrdinalIgnoreCase)
                    || head.Contains("ParseException", StringComparison.OrdinalIgnoreCase)))
                return true;
            // PowerShell 7 (ConciseView), positioned in THIS hook's script: "ParserError: C:\…\x.ps1:3", or
            // "Get-Foo: C:\…\x.ps1:3" whose "Line |" box says the term 'Get-Foo' is not recognized — Get-Foo
            // being a command the hook's own code calls.
            if (conciseAt.Success)
            {
                string at = conciseAt.Groups["p"].Value.Trim();
                if (at.Equals(full, StringComparison.OrdinalIgnoreCase) || at.Equals(raw.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    string n = conciseAt.Groups["n"].Value;
                    if (n.Equals("ParserError", StringComparison.OrdinalIgnoreCase)) return true;
                    if (head.Replace("|", "").Contains("Theterm'" + n + "'isnotrecognizedasanameofacmdlet", StringComparison.OrdinalIgnoreCase)
                        && HookCodeMentions(template, cwd, n))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>The scripts named in the hook's CONFIGURED command (never a tool value): the text as
    /// written, and its full path.</summary>
    private static IEnumerable<(string Raw, string Full)> HookScriptPaths(Regex rx, string template, string cwd)
    {
        foreach (Match m in rx.Matches(template))
        {
            string raw = MatchedPath(m);
            if (raw.Contains('{')) continue; // a substitution token, not a fixed script
            if (ResolveScriptPath(raw, cwd) is { } full) yield return (raw, full);
        }
    }

    private static string MatchedPath(Match m) =>
        m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;

    private static string? ResolveScriptPath(string raw, string cwd)
    {
        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(raw);
            return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(cwd, expanded));
        }
        catch { return null; /* not a usable path */ }
    }

    /// <summary>A script's text, or null when it is missing, unreadable or over <see cref="MaxScannedScriptBytes"/>.</summary>
    private static string? ReadScript(string full)
    {
        try
        {
            var fi = new FileInfo(full);
            return fi.Exists && fi.Length <= MaxScannedScriptBytes ? File.ReadAllText(full) : null;
        }
        catch { return null; /* unreadable — treated as not mentioning anything */ }
    }

    /// <summary>The text of the script the hook command itself RUNS: the FIRST script path in its configured
    /// command (the launcher's script argument). Null when there is none, or when that first path is built from
    /// a substitution token. Later script-looking paths are arguments — e.g. a project file the model can write.</summary>
    private static string? HookOwnScriptText(string template, string cwd)
    {
        var m = ScriptPathRx.Match(template);
        if (!m.Success) return null;
        string raw = MatchedPath(m);
        if (raw.Contains('{')) return null;
        return ResolveScriptPath(raw, cwd) is { } full ? ReadScript(full) : null;
    }

    /// <summary>True when <paramref name="name"/> is a whole word of the hook's command or of the script it RUNS
    /// (<see cref="HookOwnScriptText"/> — not any other script-looking path on its line, which can be a project
    /// file the model writes). Anchored like <see cref="ReadsExactValues"/>.</summary>
    private static bool HookCodeMentions(string template, string cwd, string name)
    {
        var rx = new Regex(@"(?<![\w-])" + Regex.Escape(name) + @"(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (rx.IsMatch(template)) return true;
        return HookOwnScriptText(template, cwd) is { } own && rx.IsMatch(own);
    }

    private static readonly string[] ExactValueInterfaces = { "CLUADEX_HOOK_PAYLOAD_FILE", "CLUADEX_HOOK_OMITTED" };

    /// <summary>True when the hook reads the call's exact values itself: its command, or the script it runs
    /// (<see cref="HookOwnScriptText"/> — not any other script-looking path on its line), mentions
    /// CLUADEX_HOOK_PAYLOAD_FILE or CLUADEX_HOOK_OMITTED.</summary>
    private static bool ReadsExactValues(string command, string cwd)
    {
        static bool Mentions(string text) =>
            ExactValueInterfaces.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
        return Mentions(command) || HookOwnScriptText(command, cwd) is { } own && Mentions(own);
    }

    /// <summary>A short, single-line, control-free excerpt of the hook's first stderr line for the "script
    /// error" note (the full text is in the Debug Log). The user's profile folder is shown as ~.</summary>
    private static string ErrorExcerpt(string stderr)
    {
        string line = FirstLine(stderr.TrimStart('\uFEFF'));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 3) line = line.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder(Math.Min(line.Length, MaxExcerptChars + 1));
        bool gap = false;
        foreach (char c in line)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)
                || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
            {
                gap = sb.Length > 0;
                continue;
            }
            if (gap) { sb.Append(' '); gap = false; }
            sb.Append(c);
        }
        string s = sb.ToString();
        return s.Length <= MaxExcerptChars ? s : s[..MaxExcerptChars] + "…";
    }

    private const int MaxExcerptChars = 160;

    private static string Squash(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (!char.IsWhiteSpace(c) && c != '\uFEFF') sb.Append(c);
        return sb.ToString();
    }

    private static string FirstLine(string s) =>
        s.Trim().Split('\n', 2)[0].Trim();

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>The hook's own refusal text for the model: trimmed, control characters flattened, capped.</summary>
    private static string CleanHookMessage(string stderr)
    {
        var sb = new StringBuilder(Math.Min(stderr.Length, MaxMessageChars + 1));
        foreach (char c in stderr.Trim())
        {
            if (sb.Length >= MaxMessageChars) { sb.Append('…'); break; }
            if (c == '\r') continue;
            sb.Append(c != '\n' && c != '\t' && char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString();
    }

    private static string Describe(HookDefinition hook) =>
        $"[{hook.Phase} · {hook.Matcher}] {Trunc((hook.Command ?? "").Replace('\n', ' ').Replace('\r', ' '), 160)}";

    /// <summary>The shell text of a call: "command", or a powershell call's "script".</summary>
    private static string CommandText(ToolCall call)
    {
        string c = call.GetArg("command", "");
        return c.Length > 0 ? c : call.GetArg("script", "");
    }

    private static string EnvSafe(string? value) => (value ?? "").Replace("\0", "");

    /// <summary>The real cmd.exe — never whatever "cmd" a search of the current folder turns up.</summary>
    private static string CmdExePath()
    {
        string sys = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        return File.Exists(sys) ? sys : "cmd.exe";
    }

    // ─── Payload file ───

    /// <summary>One random folder per process under %TEMP%. The fixed %TEMP%\cluadex-hooks could be
    /// pre-empted by a FILE of that name (or be unwritable), and the exception used to come back as
    /// "hook could not run — not blocking", i.e. every PreToolUse hook silently passed.</summary>
    private static readonly string PayloadFolderName = $"cluadex-hooks-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private static readonly ConcurrentDictionary<string, byte> CreatedPayloadFolders = new(StringComparer.OrdinalIgnoreCase);
    private static int _cleanupRegistered;

    private async Task<string?> WritePayloadAsync(string json, CancellationToken ct)
    {
        byte[] bytes = new UTF8Encoding(false).GetBytes(json);
        Exception? folderError;
        string? file = null;
        try
        {
            // Read per call: the temp root can change (a harness, a user fixing %TEMP%).
            string dir = Path.Combine(Path.GetTempPath(), PayloadFolderName);
            Directory.CreateDirectory(dir);
            if (CreatedPayloadFolders.TryAdd(dir, 0)) RegisterFolderCleanup();
            file = Path.Combine(dir, $"payload-{Guid.NewGuid():N}.json");
            await WriteFileAsync(file, bytes, FileMode.CreateNew, ct);
            return file;
        }
        catch (OperationCanceledException) { if (file != null) TryDelete(file); throw; }
        catch (Exception ex) { folderError = ex; if (file != null) TryDelete(file); }

        string? tmp = null;
        try
        {
            tmp = Path.GetTempFileName();
            await WriteFileAsync(tmp, bytes, FileMode.Truncate, ct);
            _log?.Warn("Hooks", "hook payload folder unavailable — wrote the payload to a temp file instead", folderError);
            return tmp;
        }
        catch (OperationCanceledException) { if (tmp != null) TryDelete(tmp); throw; }
        catch (Exception ex)
        {
            if (tmp != null) TryDelete(tmp);
            _log?.Error("Hooks", "the hook payload could not be written — folder: "
                                 + $"{folderError?.GetType().Name}: {folderError?.Message}; temp file: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task WriteFileAsync(string path, byte[] bytes, FileMode mode, CancellationToken ct)
    {
        await using var fs = new FileStream(path, mode, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await fs.WriteAsync(bytes, ct);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* temp folder — the OS cleans it eventually */ }
    }

    private static void RegisterFolderCleanup()
    {
        if (Interlocked.Exchange(ref _cleanupRegistered, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var dir in CreatedPayloadFolders.Keys)
                try { Safety.SafeDelete.Directory(dir, dir); } catch { /* in use / gone / refused */ }
        };
    }

    /// <summary>
    /// Payload folders left by CluadeX processes that are gone: an app closed by the installer or killed never runs
    /// ProcessExit, and the old cleanup only removed an EMPTY folder — %TEMP% filled with cluadex-hooks-* (owner
    /// 2026-10-17: "เก็บกวาดให้หมด อย่าให้คั่งค้างเป็นขยะ temp"). A folder of a still-running CluadeX is left alone.
    /// </summary>
    internal static int SweepStalePayloadFolders()
    {
        int removed = 0;
        try
        {
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "cluadex-hooks-*"))
            {
                string rest = Path.GetFileName(dir)["cluadex-hooks-".Length..];
                string pidText = rest.Split('-')[0];
                if (!int.TryParse(pidText, out int pid) || pid == Environment.ProcessId) continue;
                if (IsCluadeXProcess(pid)) continue;
                try { if (Safety.SafeDelete.Directory(dir, dir)) removed++; } catch { }
            }
        }
        catch { }
        return removed;
    }

    private static bool IsCluadeXProcess(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("CluadeX", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }   // no such process
    }

    // ─── Substitution ───

    private static readonly Regex HookEnvRefRx = new(@"%CLUADEX_HOOK_(TOOL|PATH|CWD)(?::[^%]*)?%",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>%CLUADEX_HOOK_PATH% etc. in a hook command become {path} etc. — cmd expands %VAR% before
    /// it parses its metacharacters, so the raw expansion of a tool value was a command injection.</summary>
    private static string RewriteHookEnvReferences(string template) =>
        template.Contains('%')
            ? HookEnvRefRx.Replace(template, m => "{" + m.Groups[1].Value.ToLowerInvariant() + "}")
            : template;

    /// <summary>
    /// Put a value into a hook command line in place of <paramref name="token"/>, escaped for where
    /// the token sits (see <see cref="ExpandTemplate"/>). Kept for the contract harness and callers that
    /// substitute one token.
    /// </summary>
    public static string SubstituteToken(string command, string token, string value)
    {
        if (string.IsNullOrEmpty(command) || !command.Contains(token, StringComparison.Ordinal)) return command;
        return ExpandTemplate(command, new Dictionary<string, string>(StringComparer.Ordinal) { [token] = value });
    }

    /// <summary>
    /// Replace every token in ONE pass (a value that happens to contain "{command}" is not expanded
    /// again), tracking cmd.exe's quote state exactly as cmd does: a " toggles it, except a ^" outside
    /// quotes, which is a literal quote. Every value is untrusted:
    ///  • outside quotes every cmd metacharacter is DROPPED (&amp; | &lt; &gt; ( ) ^ " plus % ` $ ;). They used
    ///    to be caret-escaped, but a caret only survives cmd's FIRST parse: the left side of a pipe and the
    ///    command of a for /f ('…') are handed to a child cmd after the carets are gone, and
    ///    "a &amp; type nul &gt; x" then ran the second command;
    ///  • inside a "quoted" span cmd takes &amp; | &lt; &gt; ^ ( ) literally — a pipe's child cmd keeps the
    ///    quotes too — so only what could end the span or expand is removed (" % ` $): "R&amp;D; notes" reaches
    ///    the hook as "R&amp;D; notes". EXCEPT when the line hands itself to another cmd that strips quotes
    ///    (a nested <c>cmd /c "…"</c>, or a for /f command that starts with a quote): there the quoted value
    ///    loses &amp; | &lt; &gt; ^ ( ) as well;
    ///  • a template with a ^" ANYWHERE treats every token as unquoted (a pipe's child cmd reads that ^" as a
    ///    real quote, so its quote parity differs from ours);
    ///  • newlines and control characters (a NUL used to make Process.Start throw) become spaces.
    /// When the token is PowerShell CODE (its command's first word is powershell/pwsh, run with -Command or
    /// without -File), PowerShell's own metacharacters (' ( ) ; &amp; | &lt; &gt; { } @ # and the curly quotes
    /// it treats as quotes) are removed too: inside '{path}' a path like  a'+(Start-Process calc)+'  was code.
    /// </summary>
    private static string ExpandTemplate(string template, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(template) || template.IndexOf('{') < 0) return template;

        bool reparsed = IsReparsedByCmd(template);
        bool[] quotedAt = QuoteMap(template);
        var hosts = PowerShellHosts(template, quotedAt);
        // A ^" outside quotes is a literal quote for THIS cmd, but a child cmd that parses the text again (the
        // left side of a pipe) gets it without its caret — a real quote — so its quote parity is flipped and a
        // token we see as "quoted" is bare there. With a ^" anywhere the quote map cannot be trusted: every
        // token is sanitized as unquoted, and as PowerShell code too if the line mentions powershell/pwsh.
        // The ^" can also appear only AT RUN TIME, when a token between a template ^ and a " expands to nothing
        // (e.g.  ^{path}"  with an empty path): decide on the expanded shape, not just the template.
        bool caretQuote = template.Contains("^\"", StringComparison.Ordinal)
                          || ExpandedHasCaretQuote(template, values);
        bool caretQuotePs = caretQuote && PsHostRx.IsMatch(template);

        var sb = new StringBuilder(template.Length + 64);
        bool inQuotes = false;
        bool caretPending = false; // the previous template char was an escaping ^ (outside quotes)
        int i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{' && MatchToken(template, i, values) is { } tok)
            {
                string raw = values[tok];
                bool psCode = IsPowerShellCodeAt(template, i, hosts, quotedAt);
                string v = caretQuote ? SanitizeShellArg(raw, psCode || caretQuotePs)
                         : inQuotes ? SanitizeQuotedArg(raw, psCode, reparsed) : SanitizeShellArg(raw, psCode);
                // "…\" would read as an escaped quote to the hook program — double trailing backslashes.
                int next = i + tok.Length;
                if (next < template.Length && template[next] == '"') v = DoubleTrailingBackslashes(v);
                sb.Append(v);
                // A value never contains " so it cannot flip the quote state; a template ^ right before it
                // escapes its (harmless) first character — or, when it is empty, the template char after it.
                if (v.Length > 0) caretPending = false;
                i = next;
                continue;
            }

            char c = template[i];
            if (caretPending) caretPending = false;          // escaped: literal, never toggles quotes
            else if (c == '"') inQuotes = !inQuotes;
            else if (c == '^' && !inQuotes) caretPending = true;
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>True when the EXPANDED line would contain a ^" that the template does not: a template ^ and a
    /// template " made adjacent by a token that expands to nothing. A token is treated as empty when its most
    /// aggressively sanitized form (every cmd + PowerShell metacharacter dropped) is empty — the superset so
    /// the test does not depend on the quote state it is helping to decide. Values never add ^ or ", so only
    /// template characters can form the ^".</summary>
    private static bool ExpandedHasCaretQuote(string template, IReadOnlyDictionary<string, string> values)
    {
        if (template.IndexOf('^') < 0 || template.IndexOf('"') < 0) return false;
        var sb = new StringBuilder(template.Length);
        int i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{' && MatchToken(template, i, values) is { } tok)
            {
                if (SanitizeShellArg(values[tok], true).Length > 0) sb.Append('x');
                i += tok.Length;
                continue;
            }
            sb.Append(template[i]);
            i++;
        }
        return sb.ToString().Contains("^\"", StringComparison.Ordinal);
    }

    private static string? MatchToken(string template, int index, IReadOnlyDictionary<string, string> values)
    {
        // Every token ends in '}', so none is a prefix of another — the first hit is the only hit.
        foreach (var tok in values.Keys)
            if (tok.Length > 0 && string.CompareOrdinal(template, index, tok, 0, tok.Length) == 0)
                return tok;
        return null;
    }

    /// <summary>
    /// The classic plain-<c>cmd /c</c> idiom wraps the whole line in one more pair of quotes —
    /// <c>""C:\Program Files\x\tool.exe" "{path}""</c> — because /c strips the first and last quote. Under
    /// /s that pair stays, the line reads as  "" C:\Program …  and the hook was classified as broken (= not
    /// blocking). A line that starts with "" and ends with " has that extra pair removed.
    /// </summary>
    internal static string StripExtraOuterQuotes(string template)
    {
        string t = template.Trim();
        return t.Length >= 4 && t[0] == '"' && t[1] == '"' && t[2] != '"' && t[^1] == '"' ? t[1..^1] : template;
    }

    /// <summary>A nested cmd (cmd /c "…" strips the quotes we relied on) or a for /f command (run by a
    /// child cmd /c, which strips them when the command starts with one). <c>%ComSpec%</c> expands to
    /// cmd.exe, so <c>%ComSpec% /c "…"</c> / <c>"%COMSPEC%" /d /s /c "…"</c> re-parse a quoted value exactly
    /// like a literal cmd (any case, quoted or not). (call / start that LAUNCH powershell are caught instead
    /// as hosts by <see cref="PowerShellHosts"/>; they do not strip the inner quote pair.)</summary>
    private static readonly Regex NestedCmdRx = new(
        @"(?<![\w.-])cmd(\.exe)?(?![\w.-])|""?%comspec%""?|(?<![\w-])for\s+/f(?![\w-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsReparsedByCmd(string template) => NestedCmdRx.IsMatch(template);

    /// <summary>cmd's quote state at every index of the TEMPLATE (a ^" outside quotes is a literal quote).</summary>
    private static bool[] QuoteMap(string t)
    {
        var map = new bool[t.Length + 1];
        bool q = false, caret = false;
        for (int i = 0; i < t.Length; i++)
        {
            map[i] = q;
            char c = t[i];
            if (caret) caret = false;
            else if (c == '"') q = !q;
            else if (c == '^' && !q) caret = true;
        }
        map[t.Length] = q;
        return map;
    }

    private static readonly Regex PsHostRx = new(@"(?<![\w-])(powershell|pwsh)(\.exe)?(?![\w-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PsCodeSwitchRx = new(@"\s[-/](c(o(m(m(a(n(d)?)?)?)?)?)?|e|ec|enc\w*)(?=[\s:""]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PsFileSwitchRx = new(@"\s[-/]f(i(le?)?)?(?=[\s""]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What may stand in front of a command's first word besides & | ( : a launcher that runs the
    /// next word as a command — cmd /c (any switches), call, start (its "title" and /switches in ANY order;
    /// /d /node /affinity take a value), do, else, an if condition, or a wrapper that runs the rest of its line
    /// as a command (sudo, gsudo, runas …) with ANY arguments of its own in between: when unsure, the next
    /// powershell/pwsh counts as a host, i.e. its tokens are sanitized as PowerShell code.</summary>
    private static readonly Regex LauncherTailRx = new(
        @"(?:^|[\s&|()@""])(?:"
        + @"(?:""[^""]*[\\/]|[^\s""&|()]*[\\/])?cmd(?:\.exe)?""?(?:\s*/[a-z](?::\S*)?)*?\s*/[ckr]"
        + @"|call|do|else"
        + @"|start(?:\s*(?:""[^""]*""|/(?:d|node|affinity)\s+(?:""[^""]*""|[^\s""/&|()][^\s""&|()]*)|/[^\s""&|()/]+))*"
        + @"|(?:""[^""]*[\\/]|[^\s""&|()]*[\\/])?(?:sudo|gsudo|runas|psexec(?:64)?|paexec|elevate|nircmdc?|conhost)(?:\.exe|\.cmd|\.bat)?""?"
        + @"(?:\s+(?:""[^""]*""|[^\s""&|()]+))*"
        + @"|if\s+(?:/i\s+)?(?:not\s+)?(?:exist\s+\S+|errorlevel\s+\S+|defined\s+\S+|cmdextversion\s+\S+|\S+?\s*==\s*\S+|\S+\s+(?:equ|neq|lss|leq|gtr|geq)\s+\S+)"
        + @")""?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The powershell/pwsh words of the template that START a command (a host). "node x.js --shell
    /// powershell {path}" names PowerShell as an argument; stripping PowerShell syntax from that {path}
    /// only mangled it.</summary>
    private static List<Match> PowerShellHosts(string t, bool[] quotedAt)
    {
        var hosts = new List<Match>();
        foreach (Match m in PsHostRx.Matches(t))
            if (IsCommandWord(t, m, quotedAt)) hosts.Add(m);
        return hosts;
    }

    private static bool IsCommandWord(string t, Match m, bool[] quotedAt)
    {
        int start;
        bool commandStartSpan = false; // the word begins a quoted span → it is itself a command-start position
        if (quotedAt[m.Index])
        {
            int open = t.LastIndexOf('"', m.Index);
            if (open < 0) open = 0;
            // "C:\…\pwsh.exe" (the quoted span is the program path) or "powershell … (the span's first word)
            bool pathSpan = m.Index + m.Length < t.Length && t[m.Index + m.Length] == '"';
            bool firstWord = !t.AsSpan(open + 1, m.Index - open - 1).ContainsAny(' ', '\t');
            if (pathSpan || firstWord) { start = open; commandStartSpan = true; }
            else
            {
                start = m.Index;
                while (start > open + 1 && !char.IsWhiteSpace(t[start - 1]) && t[start - 1] is not ('&' or '|' or '(' or ')')) start--;
            }
        }
        else
        {
            start = m.Index;
            while (start > 0 && !char.IsWhiteSpace(t[start - 1]) && t[start - 1] is not ('&' or '|' or '(' or ')' or '"')) start--;
        }

        string pre = t[..start].TrimEnd();
        while (pre.Length > 0 && pre[^1] == '@') pre = pre[..^1].TrimEnd();
        if (pre.Length == 0) return true;
        if (pre[^1] is '&' or '|' or '(') return true;
        if (LauncherTailRx.IsMatch(pre)) return true;
        // Reversed default (round 3, fail toward sanitizing): the text before is not a recognized launcher, but
        // a bare launcher we have not enumerated (%ComSpec% /c, a leading redirection `2>nul`, `if not exist
        // "a spaced path"`, bash -c, env FOO=1 …) still runs the next powershell/pwsh as a host. So treat the
        // word as a host UNLESS it is CLEARLY an argument, and when it begins a quoted span (a nested
        // something -c "pwsh …" or "C:\…\pwsh.exe") it is a command start, never an argument.
        if (commandStartSpan) return true;
        return !IsArgumentPosition(t, m, start);
    }

    /// <summary>True when the powershell/pwsh word is CLEARLY an argument rather than a command: glued to a
    /// non-path prefix in its own token (--shell=powershell, /engine:pwsh — but not a path …\pwsh), or a
    /// separate token whose immediate predecessor is a real option flag (-x, --shell, /x — but not cmd's
    /// launcher switches /c /k /r).</summary>
    private static bool IsArgumentPosition(string t, Match m, int wordStart)
    {
        if (m.Index > wordStart)                 // a prefix sits inside the word
            return t[m.Index - 1] is not ('\\' or '/'); // a path to the shell is a host; = : etc. make it an arg

        string pre = t[..wordStart].TrimEnd();   // the separate token just before
        int s = pre.Length;
        while (s > 0 && !char.IsWhiteSpace(pre[s - 1]) && pre[s - 1] is not ('&' or '|' or '(' or ')' or '"')) s--;
        string prev = pre[s..];
        if (prev.Length < 2) return false;       // too short to be a real flag → treat as a host
        if (prev[0] == '-') return true;
        if (prev[0] == '/' && !prev.Equals("/c", StringComparison.OrdinalIgnoreCase)
            && !prev.Equals("/k", StringComparison.OrdinalIgnoreCase)
            && !prev.Equals("/r", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>True when the token at <paramref name="index"/> is handed to PowerShell as CODE: the nearest
    /// PowerShell host before it (a command whose first word is powershell/pwsh), with no cmd &amp; or | in
    /// between, runs -Command (or powershell.exe's default, no -File). With -File the arguments are bound as
    /// literal strings — and everything after the script path is the SCRIPT's parameters (a bundled hook's
    /// "-Cost" is not -Command).</summary>
    private static bool IsPowerShellCodeAt(string template, int index, List<Match> hosts, bool[] quotedAt)
    {
        Match? host = null;
        foreach (var m in hosts)
        {
            if (m.Index >= index) break;
            host = m;
        }
        if (host == null) return false;
        int from = host.Index + host.Length;
        for (int k = from; k < index; k++)
        {
            // An unquoted, unescaped & or | ends the host's command: the token belongs to a later one.
            if (quotedAt[k] || (template[k] != '&' && template[k] != '|')) continue;
            if (k > 0 && template[k - 1] == '^' && !quotedAt[k - 1]) continue;
            return false;
        }
        string between = template.Substring(from, index - from);
        var file = PsFileSwitchRx.Match(between);
        if (!file.Success) return true;
        var code = PsCodeSwitchRx.Match(between);
        return code.Success && code.Index < file.Index;
    }

    private static bool IsPowerShellMeta(char c) => c is '\'' or '(' or ')' or ';' or '&' or '|' or '<' or '>'
        or '{' or '}' or '@' or '#' or '\u2018' or '\u2019' or '\u201A' or '\u201B' or '\u201C' or '\u201D' or '\u201E';

    /// <summary>cmd's command separators, redirections, block parentheses, escape and quote.</summary>
    private static bool IsCmdMeta(char c) => c is '&' or '|' or '<' or '>' or '(' or ')' or '^' or '"';

    /// <summary>A value inside a "quoted" span. <paramref name="reparsed"/>: the line hands itself to a cmd
    /// that may strip those quotes (see <see cref="ExpandTemplate"/>).</summary>
    private static string SanitizeQuotedArg(string value, bool psCode, bool reparsed)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c is '"' or '%' or '`' or '$' or '\r') continue; // " would close the span; % $ ` expand
            if (psCode && IsPowerShellMeta(c)) continue;          // ; ' ( ) … are PowerShell syntax
            if (reparsed && IsCmdMeta(c)) continue;
            sb.Append(c == '\n' || (char.IsControl(c) && c != '\t') ? ' ' : c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A value at an unquoted spot on the cmd line: every cmd metacharacter is dropped, never caret-escaped
    /// (see <see cref="ExpandTemplate"/>) — "C:\R&amp;D\a.md" arrives as "C:\RD\a.md". A hook that needs the
    /// exact value reads CLUADEX_HOOK_PATH / CLUADEX_HOOK_PAYLOAD_FILE, or puts the token in quotes.
    /// </summary>
    private static string SanitizeShellArg(string value, bool psCode)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (IsCmdMeta(c) || c is '`' or '$' or '%' or ';' or '\r') continue;
            if (psCode && IsPowerShellMeta(c)) continue;
            sb.Append(c == '\n' || (char.IsControl(c) && c != '\t') ? ' ' : c);
        }
        return sb.ToString();
    }

    private static string DoubleTrailingBackslashes(string v)
    {
        int n = 0;
        while (n < v.Length && v[v.Length - 1 - n] == '\\') n++;
        return n == 0 ? v : v + new string('\\', n);
    }

    /// <summary>Load hooks from config files.</summary>
    private List<HookDefinition> LoadHooks()
    {
        lock (_gate) { if (_cachedHooks != null) return _cachedHooks; }

        var hooks = new List<HookDefinition>();
        string? untrustedProjectFolder = null;

        // Project hooks (.cluadex/hooks.json) — WORKSPACE TRUST. These commands come from the OPENED
        // repo, which may be hostile (clone → open → SessionStart hook auto-runs `cmd /c …` = RCE).
        // Only load them if the user has explicitly trusted this folder; otherwise skip and let the UI
        // offer to trust. User-global / bundled / plugin hooks below are the user's OWN config → trusted.
        if (_fileSystemService.HasWorkingDirectory)
        {
            string dir = _fileSystemService.WorkingDirectory;
            string projectHooksFile = Path.Combine(dir, ".cluadex", "hooks.json");
            if (File.Exists(projectHooksFile))
            {
                if (IsProjectHooksTrusted(dir))
                    hooks.AddRange(ParseHooksFile(projectHooksFile));
                else
                    untrustedProjectFolder = dir; // raise the event AFTER the lock is released (below)
            }
        }

        // Global hooks (~/.cluadex/hooks.json)
        string globalHooksFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cluadex", "hooks.json");
        if (File.Exists(globalHooksFile))
            hooks.AddRange(ParseHooksFile(globalHooksFile));

        // Bundled hooks projected by HookBundleService (~/.cluadex/hooks-bundled.json)
        // Sprint 3 #2: enabled entries from the Hook Library catalog.
        string bundledHooksFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cluadex", "hooks-bundled.json");
        if (File.Exists(bundledHooksFile))
        {
            // The projection may have been written by an OLDER CluadeX on this PC (the installed app and a dev build
            // share ~/.cluadex) with 3000 ms PreToolUse timeouts. A PreToolUse timeout BLOCKS the tool, and a bundled
            // hook's PowerShell cold start alone can pass 3 s on a loaded machine — so a bundled PreToolUse hook never
            // gets less than the catalog's floor, whoever wrote the file.
            foreach (var bundled in ParseHooksFile(bundledHooksFile))
            {
                if (string.Equals(bundled.Phase, "PreToolUse", StringComparison.OrdinalIgnoreCase)
                    && bundled.TimeoutMs < HookBundleService.PreToolUseTimeoutMs)
                    bundled.TimeoutMs = HookBundleService.PreToolUseTimeoutMs;
                hooks.Add(bundled);
            }
        }

        // Enabled plugin hooks (from {plugins_dir}/{plugin_id}/hooks.json)
        try
        {
            var enabledPlugins = new HashSet<string>(_pluginService.GetEnabledPlugins(), StringComparer.OrdinalIgnoreCase);
            string pluginsDir = Path.Combine(_settingsService.DataRoot, "plugins");
            if (Directory.Exists(pluginsDir))
            {
                foreach (var dir in Directory.GetDirectories(pluginsDir))
                {
                    string pluginHooksFile = Path.Combine(dir, "hooks.json");
                    string manifestFile = Path.Combine(dir, "manifest.json");
                    if (!File.Exists(pluginHooksFile) || !File.Exists(manifestFile)) continue;

                    try
                    {
                        var manifestJson = File.ReadAllText(manifestFile);
                        using var doc = System.Text.Json.JsonDocument.Parse(manifestJson);
                        string name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (enabledPlugins.Contains(name))
                            hooks.AddRange(ParseHooksFile(pluginHooksFile));
                    }
                    catch { /* skip malformed manifests */ }
                }
            }
        }
        catch { /* plugin loading is best-effort */ }

        lock (_gate) { _cachedHooks = hooks; }
        if (untrustedProjectFolder != null) OnUntrustedProjectHooks?.Invoke(untrustedProjectFolder);
        return hooks;
    }

    /// <summary>Parse a hooks.json config file.</summary>
    private static List<HookDefinition> ParseHooksFile(string filePath)
    {
        var hooks = new List<HookDefinition>();

        try
        {
            string json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("hooks", out var hooksObj))
                return hooks;

            foreach (var phase in hooksObj.EnumerateObject())
            {
                foreach (var hookEl in phase.Value.EnumerateArray())
                {
                    hooks.Add(new HookDefinition
                    {
                        Phase = phase.Name,
                        Matcher = hookEl.TryGetProperty("matcher", out var m) ? m.GetString() ?? "*" : "*",
                        Command = hookEl.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "",
                        TimeoutMs = hookEl.TryGetProperty("timeout", out var t) ? t.GetInt32() : 10000,
                    });
                }
            }
        }
        catch { /* skip malformed config */ }

        return hooks;
    }

    /// <summary>Clear cached hooks (for reload).</summary>
    public void ReloadHooks() { lock (_gate) _cachedHooks = null; }
}

public class HookDefinition
{
    public string Phase { get; set; } = ""; // PreToolUse, PostToolUse
    public string Matcher { get; set; } = "*"; // Tool name pattern (* = all)
    public string Command { get; set; } = ""; // Shell command to run
    public int TimeoutMs { get; set; } = 10000; // Max execution time
}

public class HookResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int ExitCode { get; set; }
    /// <summary>The hook ran past its time limit (not a verdict of the hook itself).</summary>
    public bool TimedOut { get; set; }
}

/// <summary>
/// Context passed to a hook when it runs. Different phases populate different
/// fields. The {token} substitutions in HookDefinition.Command pull from here.
/// </summary>
public class HookSessionContext
{
    // Tool-phase fields (PreToolUse / PostToolUse)
    public ToolCall? ToolCall { get; set; }

    // Stop-phase fields
    public string? Model { get; set; }
    public double? SessionCostUsd { get; set; }
    public int? SessionTokens { get; set; }
    public int? TurnCount { get; set; }

    // PreCompact-phase fields
    public int? MessageCount { get; set; }
    public int? TokenEstimate { get; set; }
}
