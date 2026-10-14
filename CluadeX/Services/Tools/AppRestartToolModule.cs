using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services.Providers;

namespace CluadeX.Services.Tools;

/// <summary>
/// restart_app: the agent closes CluadeX and starts it again — optionally from another exe, which is how a freshly
/// built copy replaces the running one (the agent cannot kill CluadeX from the shell: the process it would end is
/// the one it runs in, and the single-instance mutex refuses a second copy while the first is alive).
/// A hidden PowerShell helper waits for this process to exit, then launches the exe, so the mutex is free by then.
/// </summary>
public sealed class AppRestartToolModule : IAgentToolModule
{
    public const string ToolName = "restart_app";

    // One restart per process: a model that keeps calling the tool during the 2 s grace would spawn a helper each time,
    // and every helper starts another CluadeX once this one exits.
    private static int _restartScheduled;

    public string Id => "app-restart";

    public IReadOnlyCollection<ToolType> ToolTypes { get; } = new[] { ToolType.RestartApp };

    public bool WorksWithoutProject => true;

    public ToolType? ResolveToolType(string toolName) => toolName?.Trim().ToLowerInvariant() switch
    {
        "restart_app" or "restart_cluadex" or "relaunch_app" or "restart_self" => ToolType.RestartApp,
        _ => null,
    };

    public IEnumerable<ToolSchema> GetToolSchemas()
    {
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["exe_path"] = new Dictionary<string, string>
                {
                    ["type"] = "string",
                    ["description"] = "Absolute path of the CluadeX.exe to start after closing (e.g. a fresh build in "
                        + "CluadeX\\bin\\DebugBuild\\net8.0-windows\\CluadeX.exe). Default: the exe that is running now.",
                },
            },
            ["required"] = Array.Empty<string>(),
        };
        yield return new ToolSchema
        {
            Name = ToolName,
            Description = "Close CluadeX itself and start it again (optionally from a newly built exe). The app exits about 2 seconds "
                + "after this call returns, so the run and this chat turn end there — call it LAST, after the build succeeded, "
                + "and tell the user before calling. Asks the user for permission every time.",
            InputSchema = JsonDocument.Parse(JsonSerializer.Serialize(schema)).RootElement.Clone(),
        };
    }

    public string? GetRefusal(ToolType type) => null;

    public ToolPermission DescribePermission(ToolCall call, ToolModuleContext context) =>
        new()
        {
            Scope = "execute",
            Resource = ToolName + " " + call.GetArg("exe_path"),
            AlwaysAsk = true,
            Detail = "Closes CluadeX and starts it again: " + (ResolveExe(call.GetArg("exe_path"), out var exe, out _) ? exe : "(invalid path)"),
        };

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        ToolResult Fail(string msg) => new() { Type = call.Type, ToolName = call.ToolName, Success = false, Output = msg, Error = msg };

        if (!ResolveExe(call.GetArg("exe_path"), out var exe, out var error))
            return Task.FromResult(Fail(error!));

        if (Volatile.Read(ref _restartScheduled) == 1)
            return Task.FromResult(new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true,
                Output = "A restart is ALREADY scheduled; CluadeX is closing. Do NOT call restart_app again and do not call any other tool — just end your reply.",
                Summary = "🔄 restart already scheduled",
            });

        try
        {
            int pid = Environment.ProcessId;
            string script = $"try {{ Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue }} catch {{}}; "
                + "Start-Sleep -Milliseconds 500; "
                + "Start-Process -FilePath $env:CLUADEX_RESTART_EXE -WorkingDirectory (Split-Path -Parent $env:CLUADEX_RESTART_EXE)";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-WindowStyle");
            psi.ArgumentList.Add("Hidden");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);
            psi.Environment["CLUADEX_RESTART_EXE"] = exe;
            using var helper = Process.Start(psi);
            if (helper == null) return Task.FromResult(Fail("Could not start the restart helper. The app was NOT closed."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail($"Could not start the restart helper: {ex.Message}. The app was NOT closed."));
        }

        Interlocked.Exchange(ref _restartScheduled, 1);

        // Let this tool result reach the model and the chat before the window goes away.
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000).ConfigureAwait(false);
            var app = System.Windows.Application.Current;
            if (app != null) await app.Dispatcher.InvokeAsync(() => app.Shutdown());
        });

        return Task.FromResult(new ToolResult
        {
            Type = call.Type, ToolName = call.ToolName, Success = true,
            Output = $"CluadeX will close in ~2 seconds and {exe} will start. Do NOT call restart_app again or any other tool — reply with one short sentence and stop.",
            Summary = "🔄 restarting → " + Path.GetFileName(exe),
        });
    }

    private static bool ResolveExe(string raw, out string exe, out string? error)
    {
        exe = "";
        error = null;
        raw = (raw ?? "").Trim().Trim('"', '\'');
        if (raw.Length == 0)
        {
            string? self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self) || !File.Exists(self)
                || !string.Equals(Path.GetFileName(self), "CluadeX.exe", StringComparison.OrdinalIgnoreCase))
            {
                error = "Cannot find the running CluadeX.exe (maybe started via dotnet); pass exe_path.";
                return false;
            }
            exe = self;
            return true;
        }
        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(raw);
            if (!Path.IsPathFullyQualified(expanded)) { error = "exe_path must be an absolute path."; return false; }
            exe = Path.GetFullPath(expanded);
        }
        catch (Exception ex) { error = $"'{raw}' is not a valid path: {ex.Message}"; return false; }
        if (!string.Equals(Path.GetFileName(exe), "CluadeX.exe", StringComparison.OrdinalIgnoreCase)) { error = $"{exe} is not CluadeX.exe; restart_app only starts CluadeX."; return false; }
        if (!File.Exists(exe)) { error = $"No file at {exe}. Build first."; return false; }
        return true;
    }

    public IReadOnlyCollection<string> LocalCoreToolNames { get; } = Array.Empty<string>();

    public string? GetPromptSection(PromptSectionContext context) =>
        "restart_app closes CluadeX and starts it again (pass exe_path to start a freshly built exe). The app exits right after, " +
        "so call it last, only when the user asked to restart/relaunch.";
}
