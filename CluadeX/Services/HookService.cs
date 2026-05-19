using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Hook execution service modeled after Claude Code's hooks system.
/// Supports five phases:
///   - PreToolUse — before a tool runs (can block by returning non-zero)
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
/// Available substitution tokens (sanitized to prevent shell injection):
///   - {tool} {path} {command} {arguments}          — PreToolUse / PostToolUse
///   - {model} {cost} {tokens} {turn_count}         — Stop
///   - {cwd}                                        — SessionStart / any
///   - {message_count} {token_estimate}             — PreCompact
/// </summary>
public class HookService
{
    private readonly FileSystemService _fileSystemService;
    private readonly SettingsService _settingsService;
    private readonly PluginService _pluginService;

    private List<HookDefinition>? _cachedHooks;

    public HookService(FileSystemService fileSystemService, SettingsService settingsService, PluginService pluginService)
    {
        _fileSystemService = fileSystemService;
        _settingsService = settingsService;
        _pluginService = pluginService;

        // Auto-reload hooks when plugins change
        _pluginService.PluginsChanged += ReloadHooks;
    }

    /// <summary>Execute pre-tool hooks. Returns false if any hook blocks execution.</summary>
    public async Task<HookResult> ExecutePreToolHooksAsync(ToolCall call, CancellationToken ct = default)
    {
        var hooks = GetHooksForPhase("PreToolUse", call.ToolName);
        foreach (var hook in hooks)
        {
            var result = await RunHookAsync(hook, BuildToolContext(call), ct);
            if (!result.Success)
                return result; // Block tool execution
        }
        return new HookResult { Success = true };
    }

    /// <summary>Execute post-tool hooks (informational, doesn't block).</summary>
    public async Task ExecutePostToolHooksAsync(ToolCall call, ToolResult result, CancellationToken ct = default)
    {
        var hooks = GetHooksForPhase("PostToolUse", call.ToolName);
        foreach (var hook in hooks)
        {
            try
            {
                await RunHookAsync(hook, BuildToolContext(call), ct);
            }
            catch { /* post-tool hooks are best-effort */ }
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
            catch { /* best-effort */ }
        }
    }

    /// <summary>Execute SessionStart hooks. Fired once on app startup after services build.</summary>
    public async Task ExecuteSessionStartHooksAsync(HookSessionContext context, CancellationToken ct = default)
    {
        var hooks = GetHooksForPhase("SessionStart", "*");
        foreach (var hook in hooks)
        {
            try { await RunHookAsync(hook, context, ct); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Execute PreCompact hooks. Fired right before microcompaction trims history.</summary>
    public async Task ExecutePreCompactHooksAsync(HookSessionContext context, CancellationToken ct = default)
    {
        var hooks = GetHooksForPhase("PreCompact", "*");
        foreach (var hook in hooks)
        {
            try { await RunHookAsync(hook, context, ct); }
            catch { /* best-effort */ }
        }
    }

    private HookSessionContext BuildToolContext(ToolCall call)
    {
        var ctx = new HookSessionContext { ToolCall = call };
        return ctx;
    }

    /// <summary>Get hooks matching a phase and tool name.</summary>
    private List<HookDefinition> GetHooksForPhase(string phase, string toolName)
    {
        return LoadHooks()
            .Where(h => h.Phase.Equals(phase, StringComparison.OrdinalIgnoreCase))
            .Where(h => MatchesToolName(h.Matcher, toolName))
            .ToList();
    }

    /// <summary>Check if a hook matcher matches the tool name. Supports wildcards.</summary>
    private static bool MatchesToolName(string matcher, string toolName)
    {
        if (string.IsNullOrEmpty(matcher) || matcher == "*") return true;

        // Exact match
        if (matcher.Equals(toolName, StringComparison.OrdinalIgnoreCase)) return true;

        // Wildcard match: "run_command:npm*" matches "run_command" when args start with npm
        if (matcher.Contains('*'))
        {
            string pattern = matcher.Replace("*", ".*");
            return System.Text.RegularExpressions.Regex.IsMatch(
                toolName, $"^{pattern}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return false;
    }

    /// <summary>Run a hook command and return result.</summary>
    private async Task<HookResult> RunHookAsync(HookDefinition hook, HookSessionContext context, CancellationToken ct)
    {
        string command = hook.Command;
        var call = context.ToolCall;

        // ── Tool-context substitutions (PreToolUse / PostToolUse) ───────
        if (call != null)
        {
            command = command.Replace("{tool}", SanitizeShellArg(call.ToolName));
            command = command.Replace("{path}", SanitizeShellArg(call.GetArg("path", "")));
            command = command.Replace("{command}", SanitizeShellArg(call.GetArg("command", "")));
            if (command.Contains("{arguments}"))
            {
                try
                {
                    string argsJson = System.Text.Json.JsonSerializer.Serialize(call.Arguments);
                    command = command.Replace("{arguments}", SanitizeShellArg(argsJson));
                }
                catch { command = command.Replace("{arguments}", "{}"); }
            }
        }
        else
        {
            // Clear unsubstituted tool tokens so a hook config that references them
            // in the wrong phase doesn't leak the literal placeholder into the shell.
            command = command.Replace("{tool}", "").Replace("{path}", "")
                             .Replace("{command}", "").Replace("{arguments}", "{}");
        }

        // ── Session-context substitutions (Stop / SessionStart / PreCompact) ─
        command = command.Replace("{model}", SanitizeShellArg(context.Model ?? ""));
        command = command.Replace("{cost}", SanitizeShellArg(context.SessionCostUsd?.ToString("F4") ?? "0"));
        command = command.Replace("{tokens}", SanitizeShellArg(context.SessionTokens?.ToString() ?? "0"));
        command = command.Replace("{turn_count}", SanitizeShellArg(context.TurnCount?.ToString() ?? "0"));
        command = command.Replace("{message_count}", SanitizeShellArg(context.MessageCount?.ToString() ?? "0"));
        command = command.Replace("{token_estimate}", SanitizeShellArg(context.TokenEstimate?.ToString() ?? "0"));
        command = command.Replace("{cwd}", SanitizeShellArg(
            _fileSystemService.HasWorkingDirectory ? _fileSystemService.WorkingDirectory : Environment.CurrentDirectory));

        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo("cmd", $"/c {command}")
            {
                WorkingDirectory = _fileSystemService.HasWorkingDirectory
                    ? _fileSystemService.WorkingDirectory
                    : Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            proc = Process.Start(psi);
            if (proc == null) return new HookResult { Success = false, Message = "Failed to start hook process" };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(hook.TimeoutMs);

            // Read stdout/stderr concurrently to avoid pipe deadlock
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            return new HookResult
            {
                Success = proc.ExitCode == 0,
                Message = proc.ExitCode == 0 ? stdout.Trim() : stderr.Trim(),
                ExitCode = proc.ExitCode,
            };
        }
        catch (OperationCanceledException)
        {
            try { proc?.Kill(true); } catch { }
            return new HookResult { Success = false, Message = $"Hook timed out after {hook.TimeoutMs}ms" };
        }
        catch (Exception ex)
        {
            return new HookResult { Success = false, Message = $"Hook failed: {ex.Message}" };
        }
        finally
        {
            proc?.Dispose();
        }
    }

    /// <summary>Load hooks from config files.</summary>
    private List<HookDefinition> LoadHooks()
    {
        if (_cachedHooks != null) return _cachedHooks;

        var hooks = new List<HookDefinition>();

        // Project hooks (.cluadex/hooks.json)
        if (_fileSystemService.HasWorkingDirectory)
        {
            string projectHooksFile = Path.Combine(_fileSystemService.WorkingDirectory, ".cluadex", "hooks.json");
            if (File.Exists(projectHooksFile))
                hooks.AddRange(ParseHooksFile(projectHooksFile));
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
            hooks.AddRange(ParseHooksFile(bundledHooksFile));

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

        _cachedHooks = hooks;
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
    public void ReloadHooks() => _cachedHooks = null;

    /// <summary>
    /// Sanitize a value for safe shell argument insertion (prevent injection).
    /// Uses proper escaping instead of stripping — preserves paths with spaces.
    /// </summary>
    private static string SanitizeShellArg(string value)
    {
        // Remove dangerous metacharacters that enable injection
        var sanitized = value
            .Replace("`", "")       // Backtick execution
            .Replace("$(", "")      // Subshell expansion
            .Replace("$", "")       // Variable expansion
            .Replace("%", "")       // Windows env var expansion (%PATH%)
            .Replace("^", "")       // Windows cmd escape character
            .Replace("\n", " ")     // Newline injection
            .Replace("\r", "");

        // Escape remaining metacharacters instead of removing them
        // This preserves paths with spaces while blocking injection
        sanitized = sanitized
            .Replace("\"", "\\\"")  // Escape double quotes
            .Replace("&", "^&")     // Escape ampersand (Windows)
            .Replace("|", "^|")     // Escape pipe (Windows)
            .Replace("<", "^<")     // Escape redirect
            .Replace(">", "^>")     // Escape redirect
            .Replace(";", "");      // Remove semicolons (cmd chaining)

        return sanitized;
    }
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
