using System.IO;
using System.Text;
using CluadeX.Models;

namespace CluadeX.Services.Tools;

/// <summary>
/// debug — the workbench's DebugAdapterService for the agent. The same one session the DEBUG panel shows, so the user
/// sees what the agent does. No adapter installed = the install command, never a start that cannot work.
/// </summary>
public sealed partial class EngineeringToolModule
{
    private readonly object _debugLock = new();
    private readonly StringBuilder _debugOutput = new();
    private bool _debugHooked;
    private bool _agentSession;               // the running session was started by the agent (its later calls go free)
    private TaskCompletionSource<string>? _debugWait;

    private void HookDebugger()
    {
        if (_debugger == null) return;
        lock (_debugLock)
        {
            if (_debugHooked) return;
            _debugHooked = true;
        }
        _debugger.Output += text =>
        {
            lock (_debugLock)
            {
                _debugOutput.Append(text);
                if (!text.EndsWith('\n')) _debugOutput.Append('\n');
                if (_debugOutput.Length > 16_000) _debugOutput.Remove(0, _debugOutput.Length - 12_000);
            }
        };
        _debugger.Stopped += (reason, _) => { lock (_debugLock) _debugWait?.TrySetResult("stopped: " + reason); };
        _debugger.Terminated += why =>
        {
            lock (_debugLock)
            {
                _agentSession = false;
                _debugWait?.TrySetResult("terminated" + (string.IsNullOrWhiteSpace(why) ? "" : ": " + why));
            }
        };
    }

    private ToolPermission DebugPermission(ToolCall call)
    {
        string action = Arg(call, "action").ToLowerInvariant();
        if (action is "adapters" or "status" or "") return ToolPermission.Free("read", "debug " + action);
        if (action == "start")
        {
            // No adapter for the file = nothing can run: the call answers with the install command, unasked.
            if (AdapterFor(Arg(call, "path")) is not { IsAvailable: true })
                return ToolPermission.Free("read", "debug " + Arg(call, "path"));
            return ToolPermission.Ask("execute", "debug " + Arg(call, "path"),
                $"Runs {Arg(call, "path")} under the debugger" + (Arg(call, "breakpoints") is { Length: > 0 } b ? $", breakpoints at line(s) {b}." : "."));
        }
        // stack / eval / step / continue / stop in a session the agent started (and the user approved): no new prompt.
        bool mine;
        lock (_debugLock) mine = _agentSession && _debugger?.IsRunning == true;
        return mine ? ToolPermission.Free("execute", "debug " + action) : ToolPermission.Ask("execute", "debug " + action,
            "The debug session was not started by the agent: this acts on the user's own session.");
    }

    private async Task<ToolResult> DebugAsync(ToolCall call, ToolModuleContext ctx, CancellationToken ct)
    {
        if (_debugger == null) return Fail(call, string.Format(L("tool.module.unavailable"), call.ToolName, Id));
        HookDebugger();
        string action = Arg(call, "action").ToLowerInvariant();
        int wait = IntArg(call, "wait_seconds", 20, 1, 120);
        switch (action)
        {
            case "adapters":
            {
                var adapters = Adapters();
                var sb = new StringBuilder("Debug adapters on this PC:\n");
                foreach (var a in adapters)
                    sb.AppendLine($"  {a.Language} ({a.Id}): {(a.IsAvailable ? "installed" : "NOT installed — " + a.InstallHint)}");
                return Ok(call, sb.ToString().TrimEnd(), $"{adapters.Count(a => a.IsAvailable)} of {adapters.Count} adapters installed");
            }
            case "status":
                return Ok(call, StatusText(), _debugger.IsRunning ? (_debugger.IsPaused ? "debugger paused" : "debugger running") : "no debug session");
            case "start":
                return await DebugStartAsync(call, ctx, wait, ct);
            case "stack":
                if (!_debugger.IsRunning) return Fail(call, "No debug session is running — debug action=start first.");
                if (!_debugger.IsPaused) return Fail(call, "The program is running, not paused: there is no stack to read. Wait for a breakpoint (continue with wait_seconds) or stop.");
                return Ok(call, await WhereText(ct, frames: 15), "debugger stack");
            case "eval":
            {
                if (!_debugger.IsRunning || !_debugger.IsPaused) return Fail(call, "eval needs a paused debug session (stopped at a breakpoint).");
                string expr = Arg(call, "expression");
                if (expr.Length == 0) return Fail(call, "eval needs 'expression'.");
                var frames = await _debugger.GetStackTraceAsync(ct);
                if (frames.Count == 0) return Fail(call, "The adapter reported no stack frame to evaluate in.");
                string value = await _debugger.EvaluateAsync(expr, frames[0].Id, ct);
                return Ok(call, $"{expr} = {value}", $"eval {Clip(expr, 40)}");
            }
            case "step" or "next" or "continue":
            {
                if (!_debugger.IsRunning) return Fail(call, "No debug session is running — debug action=start first.");
                if (!_debugger.IsPaused) return Fail(call, "The program is already running (not paused).");
                var waiter = ArmWait();
                string kind = action == "continue" ? "continue" : Arg(call, "kind").ToLowerInvariant() is { Length: > 0 } k ? k : "over";
                switch (kind)
                {
                    case "continue": await _debugger.ContinueAsync(ct); break;
                    case "into": await _debugger.StepIntoAsync(ct); break;
                    case "out": await _debugger.StepOutAsync(ct); break;
                    default: await _debugger.StepOverAsync(ct); break;
                }
                string what = await WaitAsync(waiter, wait, ct);
                return Ok(call, $"{kind}: {what}\n{await AfterStopText(ct)}", $"debugger {kind}: {what}");
            }
            case "stop":
                if (!_debugger.IsRunning) return Ok(call, "No debug session was running.", "no debug session");
                await _debugger.StopAsync();
                lock (_debugLock) _agentSession = false;
                return Ok(call, "The debug session was stopped and the program ended.\n" + TakeOutput(), "debugger stopped");
            default:
                return Fail(call, "debug needs action: adapters | start | stack | eval | step | continue | stop | status.");
        }
    }

    private async Task<ToolResult> DebugStartAsync(ToolCall call, ToolModuleContext ctx, int wait, CancellationToken ct)
    {
        string root = ctx.WorkingDirectory!;
        string? file = InsideProject(root, Arg(call, "path"));
        if (file == null)
            return Fail(call, Arg(call, "path").Length == 0 ? "debug start needs 'path': the .py file, or a .cs file of the project to run." : $"'{Arg(call, "path")}' is outside the project.");
        var adapter = AdapterFor(file);
        if (adapter == null)
            return Fail(call, $"No debugger for {Path.GetExtension(file)} files: debug runs Python (.py, via debugpy) and C#/.NET (.cs, via netcoredbg).");
        if (!adapter.IsAvailable)
            return Fail(call, $"{adapter.Language} debugging needs {adapter.Id}, which is not installed on this PC, so nothing was started. "
                + $"To install: {adapter.InstallHint} — then call debug again. Tell the user if you cannot install it yourself.");
        if (!File.Exists(file)) return Fail(call, $"No file at {Arg(call, "path")}.");
        if (_debugger!.IsRunning) return Fail(call, "A debug session is already running — debug action=stop first (or work in it: stack / eval / step).");

        // The program runs for real, so the same guard as run_command: the script itself, and the project's changed code.
        string asCommand = adapter.Id == "debugpy" ? $"python \"{file}\"" : "dotnet run";
        string? finding = Safety.CodeRunGuard.CheckScriptFile(file) ?? await Safety.CodeRunGuard.CheckAsync(asCommand, root);
        if (await Safety.DangerApproval.GateAsync("debug", asCommand, finding) is { } refused) return Fail(call, refused);
        if (Safety.ProcessLaunchPolicy.Blocks(adapter.Executable))
            return Ok(call, Safety.ProcessLaunchPolicy.Report($"{adapter.Id} debugging {Rel(root, file)}"), "debug (dry-run)");

        var lines = new List<int>();
        foreach (var part in Arg(call, "breakpoints").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part.Trim(), out int line) && line > 0) lines.Add(line);
        var breakpoints = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        if (lines.Count > 0) breakpoints[file] = lines;

        lock (_debugLock) _debugOutput.Clear();
        var waiter = ArmWait();
        var started = await _debugger.StartAsync(adapter, file, breakpoints, ct);
        if (!started.Started) return Fail(call, "The debugger did not start: " + started.Error);
        lock (_debugLock) _agentSession = true;

        var sb = new StringBuilder($"Debugging {Rel(root, file)} with {adapter.Id}.");
        foreach (var (f, bps) in started.Breakpoints)
            foreach (var bp in bps)
                sb.Append($"\n  breakpoint line {bp.RequestedLine}: {(bp.Verified ? (bp.Moved ? $"set (moved to line {bp.Line})" : "set") : "NOT set " + bp.Message)}");
        string what = await WaitAsync(waiter, wait, ct);
        sb.Append($"\n{what}\n{await AfterStopText(ct)}");
        return Ok(call, sb.ToString().TrimEnd(), "debugger: " + what);
    }

    private (DateTime At, string Path, List<DebugAdapterInfo> List)? _adapterCache;

    /// <summary>The installed-adapter probe (it starts python to look for debugpy), kept 10 s for the same PATH so the
    /// permission check and the call that follows probe once.</summary>
    private List<DebugAdapterInfo> Adapters()
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        lock (_debugLock)
            if (_adapterCache is { } c && c.Path == path && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(10)) return c.List;
        var list = DebugAdapterService.DetectAdapters();
        lock (_debugLock) _adapterCache = (DateTime.UtcNow, path, list);
        return list;
    }

    /// <summary>The adapter for a file's language (installed or not), or null for a language debug does not cover.</summary>
    private DebugAdapterInfo? AdapterFor(string file)
    {
        string wanted = Path.GetExtension(file ?? "").ToLowerInvariant() switch { ".py" => "debugpy", ".cs" or ".fs" or ".vb" => "netcoredbg", _ => "" };
        return wanted.Length == 0 ? null : Adapters().FirstOrDefault(a => a.Id == wanted);
    }

    private TaskCompletionSource<string> ArmWait()
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_debugLock) _debugWait = tcs;
        return tcs;
    }

    private async Task<string> WaitAsync(TaskCompletionSource<string> tcs, int seconds, CancellationToken ct)
    {
        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(seconds), ct));
        lock (_debugLock) if (_debugWait == tcs) _debugWait = null;
        return done == tcs.Task ? tcs.Task.Result : $"still running after {seconds} s (no breakpoint hit yet) — debug action=status, continue with a longer wait_seconds, or stop.";
    }

    /// <summary>After a stop: where it is and the locals; after the end: the program's output.</summary>
    private async Task<string> AfterStopText(CancellationToken ct)
    {
        if (_debugger!.IsRunning && _debugger.IsPaused) return await WhereText(ct, frames: 5);
        if (!_debugger.IsRunning) return "Program output:\n" + TakeOutput();
        return "";
    }

    private async Task<string> WhereText(CancellationToken ct, int frames)
    {
        var sb = new StringBuilder();
        var stack = await _debugger!.GetStackTraceAsync(ct);
        if (stack.Count == 0) return "(the adapter reported no stack)";
        sb.AppendLine("Stack (innermost first):");
        foreach (var f in stack.Take(frames))
            sb.AppendLine($"  #{f.Id} {f.Name}  {f.Location}");
        var vars = await _debugger.GetVariablesAsync(stack[0].Id, ct);
        if (vars.Count > 0)
        {
            sb.AppendLine("Variables in the top frame:");
            foreach (var v in vars.Take(40))
                sb.AppendLine($"  {v.Name}{(string.IsNullOrEmpty(v.Type) ? "" : " : " + v.Type)} = {Clip(v.Value, 200)}");
            if (vars.Count > 40) sb.AppendLine($"  … {vars.Count - 40} more");
        }
        return sb.ToString().TrimEnd();
    }

    private string StatusText()
    {
        if (!_debugger!.IsRunning) return "No debug session is running.";
        string who;
        lock (_debugLock) who = _agentSession ? "started by the agent" : "started from the DEBUG panel";
        return $"A {_debugger.ActiveAdapter?.Id} session is {(_debugger.IsPaused ? "PAUSED" : "running")} ({who}).";
    }

    private string TakeOutput()
    {
        lock (_debugLock)
        {
            string text = _debugOutput.ToString().TrimEnd();
            _debugOutput.Clear();
            return text.Length == 0 ? "(no output)" : CluadeX.Helpers.OutputBudget.HeadAndTail(text, 8000);
        }
    }
}
