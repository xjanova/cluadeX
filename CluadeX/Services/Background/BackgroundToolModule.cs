using System.IO;
using System.Text;
using System.Text.Json;
using CluadeX.Helpers;
using CluadeX.Models;
using CluadeX.Services.Providers;
using CluadeX.Services.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace CluadeX.Services.Background;

/// <summary>
/// The bg_* tools: bg_start, bg_serve, bg_output, bg_stop, bg_list — and the old task_create / task_list /
/// task_output / task_stop names, which now resolve to them.
///
/// Permissions: bg_start is judged exactly like run_command — the "execute" scope on its command (a launch.json
/// configuration is judged by the command it builds — which is also put in the call's "command" for the PreToolUse
/// hooks), the user's rules (a run_command-scoped Allow or Deny counts too), the dangerous-command blocklist and the
/// PreToolUse shell hooks. bg_serve is free inside the project and asks outside it. Reading output and listing are
/// free; stopping only ever touches processes CluadeX started.
/// Starting is behind Dev Preview (feature.devPreview), and a refusal names the exact reason; listing, reading and
/// stopping what is already in the list never are (a lapsed trial must not strand a running server).
/// </summary>
public sealed class BackgroundToolModule : IAgentToolModule
{
    private readonly BackgroundProcessService _bg;
    private readonly SettingsService _settings;
    private readonly ActivationService _activation;
    private readonly PermissionService _permissions;
    private readonly FileSystemService _fileSystem;
    private readonly IServiceProvider? _services;

    public BackgroundToolModule(BackgroundProcessService background, SettingsService settings, ActivationService activation,
        PermissionService permissions, FileSystemService fileSystem, IServiceProvider? services = null)
    {
        _bg = background;
        _settings = settings;
        _activation = activation;
        _permissions = permissions;
        _fileSystem = fileSystem;
        _services = services;
    }

    private static string L(string key) => LocalizedResources.Instance[key];
    private static string L(string key, params object[] args) => string.Format(L(key), args);

    public string Id => "background";

    public IReadOnlyCollection<ToolType> ToolTypes { get; } =
        new[] { ToolType.BgStart, ToolType.BgServe, ToolType.BgOutput, ToolType.BgStop, ToolType.BgList };

    public bool WorksWithoutProject => false;

    public ToolType? ResolveToolType(string toolName) => (toolName ?? "").Trim().ToLowerInvariant() switch
    {
        "bg_start" or "bgstart" or "task_create" or "taskcreate" or "create_task" or "start_background" => ToolType.BgStart,
        "bg_serve" or "bgserve" or "serve_static" or "static_server" => ToolType.BgServe,
        "bg_output" or "bgoutput" or "bg_logs" or "bg_log" or "task_output" or "taskoutput" or "get_task_output" => ToolType.BgOutput,
        "bg_stop" or "bgstop" or "bg_kill" or "task_stop" or "taskstop" or "stop_task" => ToolType.BgStop,
        "bg_list" or "bglist" or "bg_ps" or "task_list" or "tasklist" or "list_tasks" => ToolType.BgList,
        _ => null,
    };

    private static string CanonicalName(ToolType type) => type switch
    {
        ToolType.BgStart => "bg_start",
        ToolType.BgServe => "bg_serve",
        ToolType.BgOutput => "bg_output",
        ToolType.BgStop => "bg_stop",
        _ => "bg_list",
    };

    // ═══════════════════════════ schemas ═══════════════════════════

    public IEnumerable<ToolSchema> GetToolSchemas()
    {
        yield return Schema("bg_start",
            "Start a long-running command in the background (dev server, watcher) in the same shell as run_command, and wait until it is ready: " +
            "a printed localhost URL, ready_pattern, or the port accepting connections. Returns its id (bg#N) and URL; it keeps running until bg_stop. " +
            "A process that exits early fails at once with its output. Give command, or config = a name from .cluadex/launch.json or .claude/launch.json.",
            ("command", "string", "Shell command to run (omit when using config)", false),
            ("config", "string", "Name of a launch.json configuration (Claude Code schema: runtimeExecutable, runtimeArgs, port)", false),
            ("name", "string", "Short label for the process", false),
            ("cwd", "string", "Folder to run in, relative to the project (default: where run_command's cd is)", false),
            ("port", "integer", "Port it should listen on — the start fails at once, naming the owner, if it is taken", false),
            ("ready_pattern", "string", "Regex on the output that means ready", false),
            ("timeout_ms", "integer", "How long to wait for ready (default 60000, max 300000); it keeps running either way", false),
            ("env", "string", "Extra environment variables as a JSON object, e.g. {\"PORT\":\"3001\"}", false));
        yield return Schema("bg_serve",
            "Serve a folder over http://127.0.0.1:PORT/ with the built-in static file server — nothing to install, loopback only, " +
            "Cache-Control: no-store (reload shows your edits), correct MIME types for .js/.mjs/.wasm/.json/.svg/.webp/.ogg/.mp3/.woff2. " +
            "404s appear in bg_output. Prefer this over python -m http.server for a static page or canvas game.",
            ("path", "string", "Folder to serve (default '.', the project root)", false),
            ("port", "integer", "Port (default: the first free one in 8100-8199)", false),
            ("spa", "boolean", "Serve index.html for unknown paths without an extension (single-page apps)", false));
        yield return Schema("bg_output",
            "Read a background process's output (ANSI-free). By default only lines you have not seen yet; since='start' gives the newest lines of everything kept.",
            ("id", "string", "Process id: 3 or bg#3", true),
            ("lines", "integer", "Most lines to return (default 80)", false),
            ("since", "string", "'last' (default: new lines only) or 'start'", false),
            ("filter", "string", "Regex — only matching lines", false),
            ("level", "string", "'all' (default) or 'error' (error / exception / failed / fatal / ERR! / 4xx-5xx requests)", false));
        yield return Schema("bg_stop",
            "Stop a background process together with everything it started (its whole process tree, orphans included) and wait until its port is free. " +
            "Stopping one that already ended just reports how it ended.",
            ("id", "string", "Process id (3 or bg#3), or 'all'", true));
        yield return Schema("bg_list",
            "List background processes: id, kind, status, pid, port, URL, uptime, folder and last output line.");
    }

    private static ToolSchema Schema(string name, string description, params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new Dictionary<string, object>();
        foreach (var p in props)
            properties[p.Name] = new Dictionary<string, string> { ["type"] = p.Type, ["description"] = p.Description };
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = props.Where(p => p.Required).Select(p => p.Name).ToList(),
        };
        return new ToolSchema
        {
            Name = name,
            Description = description,
            InputSchema = JsonDocument.Parse(JsonSerializer.Serialize(schema)).RootElement.Clone(),
        };
    }

    // ═══════════════════════════ gate + permission ═══════════════════════════

    public string? GetRefusal(ToolType type)
    {
        string? refusal = DevPreviewGate.Refusal(_settings, _activation, CanonicalName(type));
        if (refusal == null) return null;
        // Cleaning up never needs the license: what was started before the trial lapsed (or Dev Preview was switched
        // off) can still be listed, read and stopped — otherwise it runs on until CluadeX exits. Starting is gated.
        if (type is ToolType.BgStop or ToolType.BgList or ToolType.BgOutput && _bg.Count > 0) return null;
        return refusal;
    }

    public ToolPermission DescribePermission(ToolCall call, ToolModuleContext context)
    {
        switch (call.Type)
        {
            case ToolType.BgStart:
            {
                var plan = PlanStart(call, context);
                if (plan.Error != null) return ToolPermission.Free("execute", call.ToolName);   // nothing will run
                if (plan.ConfigName != null)
                {
                    // The PreToolUse hooks (the git-push gate, the dangerous-command check) read the call's "command"
                    // — a launch.json configuration has none, so the command it builds is put there for them. This
                    // runs before the hooks even with the permission system off; StartAsync refuses to run anything
                    // but this exact command.
                    call.Arguments["command"] = plan.Command;
                    call.Arguments["cwd"] = plan.Cwd;
                }
                var detail = new StringBuilder();
                detail.Append(L("bg.perm.runsIn", plan.Cwd));
                if (plan.ConfigName != null) detail.Append('\n').Append(L("bg.perm.fromConfig", plan.ConfigName, plan.ConfigFile ?? ""));
                if (plan.Env is { Count: > 0 })
                    detail.Append('\n').Append("env: ").Append(string.Join(" ", plan.Env.Select(kv => $"{kv.Key}={kv.Value}")));
                // A rule written for run_command decides for the same command started here too: an Allow needs no
                // prompt, and a Deny must not ask either — ExecuteAsync refuses it (a rule naming bg_start still
                // comes first, in the gate).
                var asRunCommand = _permissions.FindRule(plan.Command, "execute", "run_command");
                return new ToolPermission
                {
                    Scope = "execute", Resource = plan.Command, Detail = detail.ToString(),
                    IsFree = asRunCommand is PermAction.Allow or PermAction.Deny,
                };
            }
            case ToolType.BgServe:
            {
                string full = ResolveFolder(call.GetArg("path", "."), context.WorkingDirectory);
                return context.WorkingDirectory != null && IsUnder(context.WorkingDirectory, full)
                    ? ToolPermission.Free("read", full)
                    : ToolPermission.Ask("read", full, L("bg.perm.serveOutside", full));
            }
            case ToolType.BgStop:
                return ToolPermission.Free("execute", "bg_stop " + call.GetArg("id"));
            default:
                return ToolPermission.Free("read", CanonicalName(call.Type));
        }
    }

    // ═══════════════════════════ execution ═══════════════════════════

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        return call.Type switch
        {
            ToolType.BgStart => await StartAsync(call, context, ct).ConfigureAwait(false),
            ToolType.BgServe => Serve(call, context),
            ToolType.BgOutput => Output(call, context),
            ToolType.BgStop => await StopAsync(call).ConfigureAwait(false),
            ToolType.BgList => List(call),
            _ => Fail(call, L("bg.err.unknownTool", call.ToolName)),
        };
    }

    private sealed record StartPlan(string Command, string Cwd, int? Port, bool PortExplicit, string? ConfigName, string? ConfigFile,
        IReadOnlyDictionary<string, string>? Env, string? Error, string? Url = null);

    /// <summary>What bg_start would run, where — the same answer for the permission prompt and the execution.</summary>
    private StartPlan PlanStart(ToolCall call, ToolModuleContext context)
    {
        string root = context.WorkingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string baseDir = context.ShellDirectory ?? root;

        Dictionary<string, string>? env = null;
        string envText = call.GetArg("env").Trim();
        if (envText.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(envText);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in doc.RootElement.EnumerateObject())
                    env[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
            }
            catch (JsonException)
            {
                return new StartPlan("", root, null, false, null, null, null, L("bg.err.badEnv"));
            }
        }

        string portText = call.GetArg("port").Trim().Trim('"');
        int? port = ParsePort(portText);
        // Same answer as bg_serve: a port that cannot exist is an error, never silently dropped.
        if (portText.Length > 0 && port == null)
            return new StartPlan("", root, null, false, null, null, env, L("bg.err.badPort", portText));
        string configName = call.GetArg("config").Trim();
        if (configName.Length > 0)
        {
            var (entries, problems) = LaunchConfig.ReadAll(root);
            var entry = entries.FirstOrDefault(e => e.Name.Equals(configName, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                string known = entries.Count == 0 ? L("bg.none") : string.Join(", ", entries.Select(e => e.Name));
                string why = problems.Count > 0 ? "\n" + string.Join("\n", problems) : "";
                return new StartPlan("", root, null, false, configName, null, env, L("bg.err.noConfig", configName, known) + why);
            }
            string cwd = string.IsNullOrWhiteSpace(entry.Cwd) ? root : ResolveFolder(entry.Cwd, root);
            var merged = new Dictionary<string, string>(entry.Env, StringComparer.OrdinalIgnoreCase);
            if (env != null) foreach (var (k, v) in env) merged[k] = v;
            // Claude Code's "url": where the preview opens — reported (and probed, for its port) when the server
            // prints no URL of its own. Loopback only.
            string? url = BackgroundProcessService.LoopbackUrl(entry.Url);
            int? urlPort = url != null && Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Port : null;
            return new StartPlan(LaunchConfig.BuildCommand(entry, ShellRunner.DefaultShell), cwd, port ?? entry.Port ?? urlPort,
                port != null || entry.Port != null, entry.Name, entry.SourceFile, merged, null, url);
        }

        string command = call.GetArg("command").Trim();
        if (command.Length == 0) return new StartPlan("", root, null, false, null, null, env, L("bg.err.noCommand"));
        string dir = call.GetArg("cwd").Trim() is { Length: > 0 } c ? ResolveFolder(c, root) : baseDir;
        return new StartPlan(command, dir, port, port != null, null, null, env, null);
    }

    private async Task<ToolResult> StartAsync(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        var plan = PlanStart(call, context);
        if (plan.Error != null) return Fail(call, plan.Error);
        // What the prompt showed and the hooks judged (DescribePermission put it in "command") is what runs: a
        // launch.json edited in between is not started.
        if (plan.ConfigName != null && call.Arguments.TryGetValue("command", out var approved)
            && !string.Equals(approved?.Trim(), plan.Command.Trim(), StringComparison.Ordinal))
            return Fail(call, L("bg.err.configChanged", plan.ConfigName));

        // A Deny written for run_command denies the same command here (an Allow is honoured in DescribePermission).
        if (_permissions.FindRule(plan.Command, "execute", "run_command") == PermAction.Deny)
            return Fail(call, $"Permission denied for execute: {plan.Command}");
        // Same blocklist as run_command — switching tools must not step around it.
        if (_settings.Settings.Features.DangerousCommandBlocking
            && DangerousCommandCheck.FindBlocked(plan.Command, DangerousCommandCheck.DialectOf(ShellRunner.DefaultShell)) is { } blockedLine)
            return Fail(call, DangerousCommandCheck.BlockedMessage(blockedLine));
        // Always on, whatever the Features switch: Safety.CodeRunGuard.
        if (await Safety.DangerApproval.GateAsync("bg_start", plan.Command, await Safety.CodeRunGuard.CheckAsync(plan.Command, plan.Cwd)) is { } unsafeRun)
            return Fail(call, unsafeRun);
        if (Safety.ProcessLaunchPolicy.DryRun)
            return new ToolResult { Type = call.Type, ToolName = call.ToolName, Success = true, Output = Safety.ProcessLaunchPolicy.Report(plan.Command), Summary = "bg_start (dry-run)" };

        var kind = LongRunningCommand.Classify(plan.Command, ShellRunner.DefaultShell, plan.Cwd);
        int? port = plan.Port ?? kind.Port;
        bool portExplicit = plan.PortExplicit || (plan.Port == null && kind.PortExplicit);
        string pattern = call.GetArg("ready_pattern");

        TimeSpan timeout;
        if (ParseInt(call.GetArg("timeout_ms")) is { } ms) timeout = TimeSpan.FromMilliseconds(Math.Clamp(ms, 1_000, 300_000));
        else if (port != null || pattern.Length > 0) timeout = TimeSpan.FromSeconds(60);
        else if (kind.Kind == LongRunningCommand.CommandKind.Server) timeout = TimeSpan.FromSeconds(15);
        else timeout = TimeSpan.FromSeconds(3);   // nothing to wait for (a watcher, a worker): just catch an instant failure

        var result = await _bg.StartAsync(new BgStartRequest
        {
            Command = plan.Command,
            Cwd = plan.Cwd,
            Name = call.GetArg("name") is { Length: > 0 } n ? n : plan.ConfigName,
            Shell = ShellRunner.DefaultShell,
            Port = port,
            PortExplicit = portExplicit,
            ReadyPattern = pattern.Length > 0 ? pattern : null,
            ReadyTimeout = timeout,
            Env = plan.Env,
            StartedBy = "agent",
            Url = plan.Url,
        }, ct).ConfigureAwait(false);
        return FormatStart(result, call, BrowserOpenAvailable(), fromRunCommand: false, context.OutputBudgetChars);
    }

    private ToolResult Serve(ToolCall call, ToolModuleContext context)
    {
        string full = ResolveFolder(call.GetArg("path", "."), context.WorkingDirectory);
        int? port = ParsePort(call.GetArg("port"));
        if (call.GetArg("port").Trim().Length > 0 && port == null)
            return Fail(call, L("bg.err.badPort", call.GetArg("port")));
        bool spa = IsTrue(call.GetArg("spa"));
        var result = _bg.StartStatic(full, port, spa, "agent");
        return FormatStart(result, call, BrowserOpenAvailable(), fromRunCommand: false, context.OutputBudgetChars);
    }

    private ToolResult Output(ToolCall call, ToolModuleContext context)
    {
        string idText = call.GetArg("id", call.GetArg("task_id"));
        if (!BackgroundProcessService.TryParseId(idText, out int id))
            return Fail(call, L("bg.err.badId", idText) + " " + L("bg.err.liveIds", _bg.DescribeLiveIds()));
        int lines = ParseInt(call.GetArg("lines")) ?? 80;
        bool fromStart = call.GetArg("since").Trim().Equals("start", StringComparison.OrdinalIgnoreCase)
                         || call.GetArg("since").Trim().Equals("all", StringComparison.OrdinalIgnoreCase);
        bool errors = call.GetArg("level").Trim().Equals("error", StringComparison.OrdinalIgnoreCase)
                      || call.GetArg("level").Trim().Equals("errors", StringComparison.OrdinalIgnoreCase);
        // One cursor per chat: reading in one chat must not mark the lines as seen in another.
        var r = _bg.Read(id, lines, fromStart, call.GetArg("filter"), errors, reader: "agent:" + context.SessionKey);
        if (r.Process == null) return Fail(call, r.Error ?? L("bg.err.unknownId", id, _bg.DescribeLiveIds()));
        if (r.Error != null) return Fail(call, r.Error);

        var sb = new StringBuilder();
        sb.AppendLine(Header(r.Process));
        if (r.AlreadyShown > 0 && !fromStart) sb.AppendLine(L("bg.out.alreadyShown", r.AlreadyShown));
        if (r.Omitted > 0) sb.AppendLine(L("bg.out.omitted", r.Omitted));
        if (r.Lines.Count == 0)
            sb.AppendLine(fromStart ? L("bg.out.empty") : L("bg.out.noNew"));
        else
            foreach (var line in r.Lines) sb.AppendLine(line);
        if (r.DroppedFromMemory > 0 && fromStart && !string.IsNullOrEmpty(r.Process.LogPath))
            sb.AppendLine(L("bg.out.inLog", r.DroppedFromMemory, r.Process.LogPath));
        string text = Budget(sb.ToString().TrimEnd(), context.OutputBudgetChars);
        return new ToolResult
        {
            Type = call.Type, ToolName = call.ToolName, Success = true, Output = text,
            Summary = L("bg.sum.output", r.Process.Id, r.Lines.Count),
        };
    }

    private async Task<ToolResult> StopAsync(ToolCall call)
    {
        string idText = call.GetArg("id", call.GetArg("task_id")).Trim();
        if (idText.Equals("all", StringComparison.OrdinalIgnoreCase) || idText == "*")
        {
            int running = _bg.RunningCount;
            if (running == 0)
                return new ToolResult { Type = call.Type, ToolName = call.ToolName, Success = true, Output = L("bg.stop.noneRunning"), Summary = L("bg.stop.noneRunning") };
            int n = await _bg.StopAllAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            return new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true,
                Output = L("bg.stop.all", n) + "\n" + ListText(), Summary = L("bg.stop.all", n),
            };
        }
        if (!BackgroundProcessService.TryParseId(idText, out int id))
            return Fail(call, L("bg.err.badId", idText) + " " + L("bg.err.liveIds", _bg.DescribeLiveIds()));
        bool wasRunning = _bg.Get(id)?.IsRunning == true;
        var s = await _bg.StopAsync(id, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        if (s == null) return Fail(call, L("bg.err.unknownId", id, _bg.DescribeLiveIds()));

        var sb = new StringBuilder();
        sb.AppendLine(wasRunning ? L("bg.stop.done", id) : L("bg.stop.already", id, StatusLabel(s.Status), s.ExitCode?.ToString() ?? "?"));
        sb.AppendLine(Header(s));
        var tail = _bg.Read(id, 10, fromStart: true, reader: "stop").Lines;
        if (tail.Count > 0)
        {
            sb.AppendLine(L("bg.out.lastLines"));
            foreach (var line in tail) sb.AppendLine(line);
        }
        if (s.Port is { } port && ReadinessProbe.IsListening(port) && s.Kind != BgKind.Static)
            sb.AppendLine(L("bg.stop.portStillBusy", port, BackgroundProcessService.DescribeBusyPort(port)));
        return new ToolResult
        {
            Type = call.Type, ToolName = call.ToolName, Success = true, Output = sb.ToString().TrimEnd(),
            Summary = wasRunning ? L("bg.stop.done", id) : L("bg.stop.already", id, StatusLabel(s.Status), s.ExitCode?.ToString() ?? "?"),
        };
    }

    private ToolResult List(ToolCall call)
    {
        var all = _bg.List();
        return new ToolResult
        {
            Type = call.Type, ToolName = call.ToolName, Success = true, Output = ListText(),
            Summary = L("bg.sum.list", all.Count(p => p.IsRunning), all.Count),
        };
    }

    private string ListText()
    {
        var all = _bg.List();
        if (all.Count == 0) return L("bg.list.empty");
        var sb = new StringBuilder();
        foreach (var p in all)
        {
            sb.Append(Header(p));
            sb.Append(" · ").Append(L("bg.list.cwd", p.Cwd));
            if (!string.IsNullOrEmpty(p.LastLine)) sb.Append(" · ").Append(L("bg.list.last", Trunc(p.LastLine, 120)));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ═══════════════════════════ text ═══════════════════════════

    /// <summary>"bg#3 [shell] running · pid 1234 · :8123 · http://127.0.0.1:8123/ · up 2m 03s · npm run dev"</summary>
    internal static string Header(BgSnapshot p)
    {
        var parts = new List<string>
        {
            $"bg#{p.Id} [{KindLabel(p.Kind)}] {StatusLabel(p.Status)}" + (p.EndedAt != null && p.ExitCode is { } code && p.Status != "stopped" ? $" ({L("bg.exitCode", code)})" : ""),
        };
        if (p.Pid is { } pid) parts.Add($"pid {pid}");
        else if (p.Kind == BgKind.Static) parts.Add(L("bg.inProcess"));
        if (p.Url != null) parts.Add(p.Url);
        else if (p.Port is { } port) parts.Add($":{port}");
        parts.Add(L(p.IsRunning ? "bg.up" : "bg.ran", FormatSpan(p.Uptime)));
        parts.Add(Trunc(p.Name.Length > 0 ? p.Name : p.Command, 80));
        if (!p.InJob && p.Kind == BgKind.Shell && p.IsRunning) parts.Add(L("bg.noJobShort"));
        return string.Join(" · ", parts);
    }

    private static string KindLabel(BgKind kind) => kind switch
    {
        BgKind.Static => "static",
        BgKind.Adopted => "adopted",
        _ => "shell",
    };

    private static string StatusLabel(string status)
    {
        string label = L("tasksm.status." + status);
        return label.StartsWith("tasksm.", StringComparison.Ordinal) ? status : label;
    }

    private static string FormatSpan(TimeSpan t)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (t.TotalHours >= 1) return string.Create(inv, $"{(int)t.TotalHours}h {t.Minutes:00}m");
        if (t.TotalMinutes >= 1) return string.Create(inv, $"{(int)t.TotalMinutes}m {t.Seconds:00}s");
        return string.Create(inv, $"{t.TotalSeconds:0.0}s");
    }

    /// <summary>
    /// The tool result for a start — shared by bg_start, bg_serve and run_command handing a server over
    /// (<paramref name="fromRunCommand"/>), so the model always reads the same shape.
    /// </summary>
    internal static ToolResult FormatStart(BgStartResult r, ToolCall call, bool browserOpen, bool fromRunCommand, int budget = 30_000)
    {
        var p = r.Process;
        var sb = new StringBuilder();
        bool success;
        string summary;
        switch (r.Status)
        {
            case BgStartStatus.Ready:
            {
                var info = new List<string>();
                if (r.ReadyAfter is { } after && r.ReadyAfter > TimeSpan.Zero)
                    info.Add(L("bg.start.readyIn", after.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));
                if (r.HttpStatus is { } code) info.Add($"HTTP {code}");
                sb.AppendLine(L("bg.start.ready", p!.Id, r.Url ?? p.Url ?? "?", info.Count > 0 ? " (" + string.Join(", ", info) + ")" : ""));
                sb.AppendLine(Hints(p.Id, browserOpen && (r.Url ?? p.Url) != null));
                success = true;
                summary = $"bg#{p.Id} → {r.Url ?? p.Url}";
                break;
            }
            case BgStartStatus.Running:
            {
                sb.AppendLine(L("bg.start.notReady", p!.Id, FormatSpan(p.Uptime)));
                if (p.Port is { } port && p.Url == null) sb.AppendLine(L("bg.start.portNotOpen", port));
                sb.AppendLine(Hints(p.Id, browserOpen && p.Url != null));
                success = true;
                summary = $"bg#{p.Id} " + L("tasksm.status.running");
                break;
            }
            case BgStartStatus.AlreadyRunning:
            {
                sb.AppendLine(L("bg.start.already", p!.Id, p.Url ?? (p.Port is { } port ? ":" + port : "-"), p.StartedAt.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)));
                sb.AppendLine(Hints(p.Id, browserOpen && p.Url != null));
                success = true;
                summary = $"bg#{p.Id} " + L("bg.sum.already");
                break;
            }
            case BgStartStatus.Finished:
            {
                sb.AppendLine(L(fromRunCommand ? "bg.start.finishedRun" : "bg.start.finished", p!.Id, FormatSpan(p.Uptime)));
                success = true;
                summary = L("bg.sum.finished", p.Id);
                break;
            }
            case BgStartStatus.Failed:
            {
                string head = p != null ? L("bg.start.failed", p.Id, p.ExitCode?.ToString() ?? "?") : r.Message;
                sb.AppendLine(head);
                if (p != null && r.Message.Length > 0 && !head.Contains(r.Message, StringComparison.Ordinal)) sb.AppendLine(r.Message);
                success = false;
                summary = head;
                break;
            }
            case BgStartStatus.Cancelled:
                sb.AppendLine(L("bg.start.cancelled", p?.Id ?? 0));
                success = false;
                summary = L("bg.start.cancelled", p?.Id ?? 0);
                break;
            default:
                sb.AppendLine(r.Message);
                success = false;
                summary = r.Message.Split('\n')[0];
                break;
        }
        bool ended = r.Status is BgStartStatus.Finished or BgStartStatus.Failed && p != null;
        int cap = Math.Min(budget, 8_000);
        if (ended && fromRunCommand && !string.IsNullOrEmpty(r.FullOutput))
        {
            // run_command handed over something that turned out to be a one-shot program: the model gets ALL of its
            // output, cut like any run_command result (head and tail), not just the last 20 lines.
            sb.AppendLine(L("bg.out.output"));
            sb.AppendLine(OutputBudget.HeadAndTail(r.FullOutput.TrimEnd(), budget));
            if (r.DroppedLines > 0 && !string.IsNullOrEmpty(p!.LogPath)) sb.AppendLine(L("bg.out.inLog", r.DroppedLines, p.LogPath));
            cap = budget + 2_000;
        }
        else if (r.LastLines.Count > 0)
        {
            sb.AppendLine(L("bg.out.lastLines"));
            foreach (var line in r.LastLines) sb.AppendLine(line);
            if (ended && r.TotalLines > r.LastLines.Count)
                sb.AppendLine(L("bg.out.fullHint", p!.Id, r.LastLines.Count, r.TotalLines));
        }
        string text = Budget(sb.ToString().TrimEnd(), cap);
        return new ToolResult
        {
            Type = call.Type, ToolName = call.ToolName, Success = success,
            Output = success ? text : "", Error = success ? "" : text,
            Summary = Trunc(summary, 140),
        };
    }

    private static string Hints(int id, bool browserOpen) =>
        L("bg.start.hints", id) + (browserOpen ? " · " + L("bg.start.hintOpen", id) : "");

    // ═══════════════════════════ small-context + prompt ═══════════════════════════

    private (string Dir, DateTime At, bool Web) _webCache;

    /// <summary>bg_serve / bg_start / bg_output / bg_stop join a small local model's core tools only in a web
    /// project (an index.html, a package.json dev/start script) or while a background process runs.</summary>
    public IReadOnlyCollection<string> LocalCoreToolNames =>
        IsWebContext() ? new[] { "bg_serve", "bg_start", "bg_output", "bg_stop" } : Array.Empty<string>();

    private bool IsWebContext()
    {
        try
        {
            if (_bg.RunningCount > 0) return true;
            if (!_fileSystem.HasWorkingDirectory) return false;
            string dir = _fileSystem.WorkingDirectory;
            var cache = _webCache;
            if (cache.Dir == dir && DateTime.UtcNow - cache.At < TimeSpan.FromSeconds(10)) return cache.Web;
            bool web = File.Exists(Path.Combine(dir, "index.html"))
                       || File.Exists(Path.Combine(dir, "public", "index.html"))
                       || HasDevScript(Path.Combine(dir, "package.json"));
            _webCache = (dir, DateTime.UtcNow, web);
            return web;
        }
        catch { return false; }
    }

    private static bool HasDevScript(string packageJson)
    {
        try
        {
            if (!File.Exists(packageJson) || new FileInfo(packageJson).Length > 1024 * 1024) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.TryGetProperty("scripts", out var s) && s.ValueKind == JsonValueKind.Object
                   && (s.TryGetProperty("dev", out _) || s.TryGetProperty("start", out _) || s.TryGetProperty("serve", out _));
        }
        catch { return false; }
    }

    public string? GetPromptSection(PromptSectionContext context)
    {
        var offered = context.OfferedToolNames;
        if (!offered.Any(n => n.StartsWith("bg_", StringComparison.Ordinal))) return null;
        // Dev Preview locked, but processes from earlier are still listed: only the clean-up tools are offered.
        if (!offered.Contains("bg_start") && !offered.Contains("bg_serve"))
            return "Background processes started earlier can still be listed (bg_list), read (bg_output id=N) and stopped " +
                   "(bg_stop id=N|all); starting new ones needs Dev Preview.";
        var sb = new StringBuilder();
        sb.Append("Background processes: never run a dev server or watcher as a blocking command. ");
        sb.Append("run_command hands recognised servers (npm run dev, vite, python -m http.server …) to the background itself and returns their URL. ");
        if (offered.Contains("bg_serve"))
            sb.Append("bg_serve serves a static folder at http://127.0.0.1:81xx with nothing installed (loopback only, no cache). ");
        sb.Append("bg_start runs any long-lived command or launch.json config; bg_output id=N reads only new lines; ");
        sb.Append("bg_stop id=N|all ends the whole process tree; bg_list shows ids, ports and URLs.");
        if (offered.Contains("browser_open")) sb.Append(" Open a server's page with browser_open url=bg:N.");
        return sb.ToString();
    }

    // ═══════════════════════════ helpers ═══════════════════════════

    private bool BrowserOpenAvailable()
    {
        if (_services == null) return false;
        try
        {
            foreach (var m in _services.GetServices<IAgentToolModule>())
            {
                if (ReferenceEquals(m, this)) continue;
                try
                {
                    if (m.ResolveToolType("browser_open") is { } t && m.ToolTypes.Contains(t) && m.GetRefusal(t) == null) return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    private static string ResolveFolder(string path, string? root)
    {
        string p = string.IsNullOrWhiteSpace(path) ? "." : path.Trim().Trim('"');
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.IsPathRooted(p)
                ? Path.GetFullPath(p)
                : Path.GetFullPath(Path.Combine(root ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p)));
        }
        catch { return p; }
    }

    private static bool IsUnder(string root, string path)
    {
        try
        {
            string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return f.Equals(r, StringComparison.OrdinalIgnoreCase)
                   || f.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static int? ParseInt(string? text) =>
        int.TryParse((text ?? "").Trim().Trim('"'), out var n) ? n : null;

    private static int? ParsePort(string? text) => ParseInt(text) is { } p && p is > 0 and < 65536 ? p : null;

    private static bool IsTrue(string? text) => (text ?? "").Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static string Budget(string text, int budget)
    {
        int max = Math.Max(1_000, budget);
        if (text.Length <= max) return text;
        // Keep the head (status) and the newest tail.
        int head = max / 4;
        return text[..head] + $"\n… [{text.Length - max:N0} characters left out] …\n" + text[^(max - head)..];
    }

    private static ToolResult Fail(ToolCall call, string error) => new()
    {
        Type = call.Type, ToolName = call.ToolName, Success = false, Error = error, Summary = Trunc(error.Split('\n')[0], 140),
    };
}
