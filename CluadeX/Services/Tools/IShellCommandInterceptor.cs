using CluadeX.Models;

namespace CluadeX.Services.Tools;

/// <summary>
/// A hook into run_command and powershell, for commands that must not run as a blocking shell call — a dev
/// server that never exits, a watcher. Register in DI as <c>IShellCommandInterceptor</c>.
///
/// <see cref="BeforeRunAsync"/> is called before the command starts — after the permission prompt, the PreToolUse
/// hooks and the dangerous-command check, so whatever it does has already been allowed exactly like the command
/// itself. The first interceptor that returns a result answers the call; null lets the next one (and finally the
/// shell) run it. <see cref="OnTimeout"/> is called when a command was stopped by its timeout, to add advice to
/// what the model sees. Neither may throw (a throw counts as "no opinion").
/// </summary>
public interface IShellCommandInterceptor
{
    /// <summary>Answer the call instead of running it (e.g. "Started in background as bg#1 → http://localhost:5173"),
    /// or refuse it with the reason, or return null to run it as usual. Must return quickly (seconds, not the
    /// command's lifetime) and honour <paramref name="ct"/>.</summary>
    Task<ToolResult?> BeforeRunAsync(ShellCommandContext context, CancellationToken ct);

    /// <summary>Text to append to a timed-out result (e.g. "this is a server — use bg_start"), or null.
    /// <paramref name="output"/> is what the command printed before it was stopped.</summary>
    string? OnTimeout(ShellCommandContext context, string output);
}

/// <summary>One shell call as the interceptors see it.</summary>
public sealed class ShellCommandContext
{
    /// <summary>The tool call (run_command or powershell) — its ToolName and Arguments as the model sent them.</summary>
    public required ToolCall Call { get; init; }

    /// <summary>The command text that would run.</summary>
    public required string Command { get; init; }

    /// <summary>The shell it would run in: Bash or Cmd for run_command, PowerShell for powershell.</summary>
    public ShellRunner.Shell Shell { get; init; }

    /// <summary>The open project folder (null when none is open).</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>Where the command would start: run_command's persistent `cd`, else the project root.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>The timeout the call asked for (or the default), in milliseconds.</summary>
    public int TimeoutMs { get; init; }
}
