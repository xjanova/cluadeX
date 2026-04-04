using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Hook execution service modeled after Claude Code's hooks system.
/// Supports PreToolUse and PostToolUse hooks that run shell commands
/// before/after tool execution.
///
/// Hook sources:
///   - settings.json (user-configured)
///   - .cluadex/hooks.json (project-specific)
///
/// Hook config format:
/// {
///   "hooks": {
///     "PreToolUse": [
///       { "matcher": "run_command", "command": "echo 'running command'" },
///       { "matcher": "write_file", "command": "eslint --fix {path}" }
///     ],
///     "PostToolUse": [
///       { "matcher": "edit_file", "command": "prettier --write {path}" }
///     ]
///   }
/// }
/// </summary>
public class HookService
{
    private readonly FileSystemService _fileSystemService;
    private readonly SettingsService _settingsService;

    private List<HookDefinition>? _cachedHooks;

    public HookService(FileSystemService fileSystemService, SettingsService settingsService)
    {
        _fileSystemService = fileSystemService;
        _settingsService = settingsService;
    }

    /// <summary>Execute pre-tool hooks. Returns false if any hook blocks execution.</summary>
    public async Task<HookResult> ExecutePreToolHooksAsync(ToolCall call, CancellationToken ct = default)
    {
        var hooks = GetHooksForPhase("PreToolUse", call.ToolName);
        foreach (var hook in hooks)
        {
            var result = await RunHookAsync(hook, call, ct);
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
                await RunHookAsync(hook, call, ct);
            }
            catch { /* post-tool hooks are best-effort */ }
        }
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
    private async Task<HookResult> RunHookAsync(HookDefinition hook, ToolCall call, CancellationToken ct)
    {
        string command = hook.Command;

        // Variable substitution — sanitize values to prevent command injection
        command = command.Replace("{tool}", SanitizeShellArg(call.ToolName));
        command = command.Replace("{path}", SanitizeShellArg(call.GetArg("path", "")));
        command = command.Replace("{command}", SanitizeShellArg(call.GetArg("command", "")));

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

    /// <summary>Sanitize a value for safe shell argument insertion (prevent injection).</summary>
    private static string SanitizeShellArg(string value)
    {
        // Remove shell metacharacters that could allow injection
        return value
            .Replace("&", "")
            .Replace("|", "")
            .Replace(";", "")
            .Replace("`", "")
            .Replace("$(", "")
            .Replace("$", "")
            .Replace("<", "")
            .Replace(">", "")
            .Replace("\"", "")
            .Replace("'", "")
            .Replace("\n", " ")
            .Replace("\r", "");
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
