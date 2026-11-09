using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services.Providers;

namespace CluadeX.Services.Tools;

/// <summary>
/// The tools an agent needs to prove and steer its own work (owner 2026-11-01: "ถ้าอยากให้มันมี ควรทำมันลงไปในโปรเจคนี้เลย"):
/// <list type="bullet">
/// <item>verify_change — evidence before "done": a build from the latest source (a locked output is rebuilt into a
///   scratch folder, never killed), a check that the binary under test is not older than its source, the tests, a
///   comparison with the last good run, and a plain list of what was NOT verified;</item>
/// <item>find_references / rename_symbol / impact_of — the IDE's CodeIntelligenceService, which the agent could not use;</item>
/// <item>checkpoint_list / checkpoint_diff / checkpoint_restore — the agent's own restore points (restore asks every time);</item>
/// <item>gh_pr_view / gh_pr_checks / gh_run_log / git_blame / git_show — read-only CI and history, argv only, with a
///   timeout and a clear answer when gh is missing or signed out;</item>
/// <item>debug — the workbench's DebugAdapterService: it probes for debugpy / netcoredbg and answers with the install
///   command when none is there (no dead buttons).</item>
/// </list>
/// The schemas are static so ChatToolBridge (a process with no services) can offer the same tools to the CLI agents.
/// </summary>
public sealed partial class EngineeringToolModule : IAgentToolModule
{
    private readonly Func<AgentToolService?> _tools;
    private readonly CodeIntelligenceService? _intel;
    private readonly CheckpointService? _checkpoints;

    /// <summary>The store agent tools capture into (AgentToolService uses CheckpointService.Current), else the injected one.</summary>
    private CheckpointService? CheckpointStore => CheckpointService.Current ?? _checkpoints;
    private readonly DebugAdapterService? _debugger;
    private readonly SettingsService? _settings;
    private readonly ActivationService? _activation;
    private readonly RepoMapService? _repoMap;

    public EngineeringToolModule(Func<AgentToolService?> tools, CodeIntelligenceService? intel, CheckpointService? checkpoints,
        DebugAdapterService? debugger, SettingsService? settings, ActivationService? activation, RepoMapService? repoMap)
    {
        _tools = tools;
        _intel = intel;
        _checkpoints = checkpoints;
        _debugger = debugger;
        _settings = settings;
        _activation = activation;
        _repoMap = repoMap;
    }

    public string Id => "engineering";

    public bool WorksWithoutProject => false;

    private static readonly Dictionary<ToolType, string> Names = new()
    {
        [ToolType.VerifyChange] = "verify_change",
        [ToolType.FindReferences] = "find_references",
        [ToolType.RenameSymbol] = "rename_symbol",
        [ToolType.ImpactOf] = "impact_of",
        [ToolType.CheckpointList] = "checkpoint_list",
        [ToolType.CheckpointDiff] = "checkpoint_diff",
        [ToolType.CheckpointRestore] = "checkpoint_restore",
        [ToolType.GhPrView] = "gh_pr_view",
        [ToolType.GhPrChecks] = "gh_pr_checks",
        [ToolType.GhRunLog] = "gh_run_log",
        [ToolType.GitBlame] = "git_blame",
        [ToolType.GitShow] = "git_show",
        [ToolType.Debug] = "debug",
        [ToolType.WatchCi] = "watch_ci",
        [ToolType.WatchProcess] = "watch_process",
        [ToolType.WatchList] = "watch_list",
        [ToolType.WatchCancel] = "watch_cancel",
    };

    public IReadOnlyCollection<ToolType> ToolTypes { get; } = Names.Keys.ToArray();

    /// <summary>Every tool's canonical name, in catalogue order (ChatToolBridge lists them for the CLI agents).</summary>
    public static IReadOnlyCollection<string> ToolNames => Names.Values;

    /// <summary>The tool type a name means — canonical or an alias, any case — or null.</summary>
    public static ToolType? TypeOf(string? toolName) => toolName?.Trim().ToLowerInvariant() switch
    {
        "verify_change" or "verify" or "verify_changes" or "verify_work" or "prove_change" or "check_change" => ToolType.VerifyChange,
        "find_references" or "find_refs" or "references" or "find_usages" or "usages" or "get_references" => ToolType.FindReferences,
        "rename_symbol" or "rename" or "rename_identifier" or "refactor_rename" => ToolType.RenameSymbol,
        "impact_of" or "impact" or "impact_analysis" or "blast_radius" or "who_uses" or "dependents" => ToolType.ImpactOf,
        "checkpoint_list" or "list_checkpoints" or "checkpoints" or "restore_points" => ToolType.CheckpointList,
        "checkpoint_diff" or "diff_checkpoint" => ToolType.CheckpointDiff,
        "checkpoint_restore" or "restore_checkpoint" => ToolType.CheckpointRestore,
        "gh_pr_view" or "pr_view" or "view_pr" => ToolType.GhPrView,
        "gh_pr_checks" or "pr_checks" or "ci_status" => ToolType.GhPrChecks,
        "gh_run_log" or "run_log" or "ci_log" or "workflow_log" or "gh_run_view" => ToolType.GhRunLog,
        "git_blame" or "blame" => ToolType.GitBlame,
        "git_show" or "show_commit" or "git_show_commit" => ToolType.GitShow,
        "debug" or "debugger" or "debug_session" or "dap" => ToolType.Debug,
        "watch_ci" or "wait_for_ci" or "watch_checks" or "ci_watch" or "monitor_ci" => ToolType.WatchCi,
        "watch_process" or "wait_for_process" or "bg_watch" or "watch_bg" or "monitor_process" => ToolType.WatchProcess,
        "watch_list" or "list_watches" or "watches" => ToolType.WatchList,
        "watch_cancel" or "cancel_watch" or "unwatch" or "stop_watch" => ToolType.WatchCancel,
        _ => null,
    };

    public ToolType? ResolveToolType(string toolName) => TypeOf(toolName);

    public IEnumerable<ToolSchema> GetToolSchemas() => Schemas();

    private static ToolSchema Schema(string name, string description, params (string Name, string Type, string Desc, bool Required)[] props)
    {
        var properties = new Dictionary<string, object>();
        foreach (var p in props)
            properties[p.Name] = new Dictionary<string, string> { ["type"] = p.Type, ["description"] = p.Desc };
        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = props.Where(p => p.Required).Select(p => p.Name).ToArray(),
        };
        return new ToolSchema
        {
            Name = name,
            Description = description,
            InputSchema = JsonDocument.Parse(JsonSerializer.Serialize(schema)).RootElement.Clone(),
        };
    }

    /// <summary>The native schemas — the very list the native loop and the CLI agents (ChatToolBridge) are offered.</summary>
    public static IEnumerable<ToolSchema> Schemas()
    {
        yield return Schema("verify_change",
            "Evidence before you say a change is done. Builds from the current source (if a running program locks the output it "
            + "rebuilds into a scratch folder and kills nothing), checks that each built binary — and any 'binary' you name, e.g. "
            + "the DLL a test harness links — is not older than its source files, runs the project's tests, compares the result "
            + "with the last passing verify of this project (new failures, fixed tests, warning count), and ends with a plain list "
            + "of what was NOT verified. Verdict: VERIFIED, VERIFIED WITH GAPS, STALE or FAILED. Asks before running, like run_build.",
            ("command", "string", "Build command (default: auto-detected, as run_build)", false),
            ("test_command", "string", "Test command (default: auto-detected, as run_tests)", false),
            ("tests", "string", "\"auto\" (default: run when a test runner is found) or \"skip\"", false),
            ("binary", "string", "Path of a binary something else loads (a harness's linked DLL, the exe you will run); checked for staleness too", false),
            ("timeout_minutes", "integer", "How long the build and the tests may each run before they are stopped (default 5, max 120)", false),
            ("tests_background", "boolean", "\"true\": after the build, start the tests as a background process and return at once; its result "
                + "(exit code, last lines) comes to this chat as a later turn. For a harness that runs longer than a few minutes", false));
        yield return Schema("find_references",
            "Every whole-word, case-sensitive use of a symbol across the project, grouped by file with line numbers; the lines "
            + "that DEFINE it are marked [definition]. Textual (no compiler): same-named members of other types show up too.",
            ("symbol", "string", "The identifier, e.g. CalculateTotal", true),
            ("include", "string", "Only files matching these globs, comma-separated (e.g. *.cs,src/**)", false),
            ("max_results", "integer", "Most matches to list (default 200)", false));
        yield return Schema("rename_symbol",
            "Rename an identifier across the project, whole-word and case-sensitive. Without apply it only PREVIEWS: every file and "
            + "line that would change, and the edit count. To apply, call again with apply=\"true\" and expected_edits = the "
            + "preview's count; it refuses if the code changed since (a different count) and asks the user first. Each changed "
            + "file is kept as a restore point (checkpoint_restore undoes it). Textual: check the preview for unrelated same-named symbols. "
            + "Logs and data files (.txt .log .trx .csv …) are left alone and listed — edit_file them if they should change.",
            ("old_name", "string", "The current identifier", true),
            ("new_name", "string", "The new identifier", true),
            ("apply", "string", "\"true\" to write the change (after a preview)", false),
            ("expected_edits", "integer", "With apply: the edit count the preview reported", false));
        yield return Schema("impact_of",
            "What a change can break, before you make it: for a symbol (or every type declared in a file), the files that use it, "
            + "which of them are tests, and which projects they belong to — so you know what to rebuild and which tests to run.",
            ("symbol", "string", "An identifier", false),
            ("path", "string", "A file: impact of the types it declares (instead of symbol)", false));
        yield return Schema("checkpoint_list",
            "This project's restore points: one per agent step that changed files (id, time, chat, plan item, files). "
            + "Newest first; points of this chat are marked *. Shell commands are not captured — git history covers commits.",
            ("all_chats", "string", "\"true\" = every chat's points, not only this chat's", false),
            ("limit", "integer", "How many points (default 15)", false));
        yield return Schema("checkpoint_diff",
            "What checkpoint_restore would change for one restore point: per file, a diff from the current content back to the "
            + "content before that step (files the step created would be removed). Read-only.",
            ("id", "string", "The restore point id from checkpoint_list", true),
            ("path", "string", "Only this file", false));
        yield return Schema("checkpoint_restore",
            "Put files back to how they were just before a restore point (every file that step or a later one changed). The user "
            + "is asked every time. The current state is kept as a new point first, so the restore can itself be undone with the "
            + "id this returns. Look at checkpoint_diff first.",
            ("id", "string", "The restore point id from checkpoint_list", true));
        yield return Schema("gh_pr_view",
            "A GitHub pull request: title, state, author, branches, body (and comments). Read-only; needs the gh CLI signed in. "
            + "Without pr: the PR of the current branch.",
            ("pr", "string", "PR number, URL or branch (default: the current branch's PR)", false),
            ("repo", "string", "owner/name (default: this repository)", false),
            ("comments", "string", "\"true\" to include the comments", false));
        yield return Schema("gh_pr_checks",
            "The CI checks of a pull request: each check's name, state (pass/fail/pending) and link. Read-only; needs gh signed in.",
            ("pr", "string", "PR number, URL or branch (default: the current branch's PR)", false),
            ("repo", "string", "owner/name (default: this repository)", false));
        yield return Schema("gh_run_log",
            "A GitHub Actions run's log for its FAILED steps (errors are at the end). Without run_id: the 10 most recent runs, to "
            + "pick one from. Read-only; needs gh signed in.",
            ("run_id", "string", "The run's number (from gh_pr_checks' links or the list)", false),
            ("repo", "string", "owner/name (default: this repository)", false));
        yield return Schema("git_blame",
            "Who last changed each line of a file, and in which commit (short hash, author, date). Use a line range for big files.",
            ("path", "string", "File inside the project", true),
            ("start_line", "integer", "First line (default 1)", false),
            ("end_line", "integer", "Last line (default: start + 199)", false),
            ("rev", "string", "Blame as of this revision (default: the working tree)", false));
        yield return Schema("git_show",
            "One commit: message, author, changed files and the patch (cut to fit). With path: the file AS IT WAS at that revision "
            + "instead. Read-only.",
            ("rev", "string", "Commit, tag or branch (default HEAD)", false),
            ("path", "string", "A file: show its content at rev", false),
            ("stat_only", "string", "\"true\" = message and file list only, no patch", false));
        foreach (var w in WatchSchemas()) yield return w;
        yield return Schema("debug",
            "Drive a debugger (Python via debugpy, C#/.NET via netcoredbg). action: adapters (what is installed — and the install "
            + "command when not), start (path of the .py or a .cs in the project to run, breakpoints \"12,40\"; it runs to the first "
            + "breakpoint or the end and reports where it stopped), stack (frames + local variables), eval (expression in the paused "
            + "frame), step (kind over|into|out), continue, stop, status. Starting asks the user — it runs the program.",
            ("action", "string", "adapters | start | stack | eval | step | continue | stop | status", true),
            ("path", "string", "start: the file to debug", false),
            ("breakpoints", "string", "start: line numbers in that file, comma-separated", false),
            ("expression", "string", "eval: the expression", false),
            ("kind", "string", "step: over (default), into or out", false),
            ("wait_seconds", "integer", "start/step/continue: how long to wait for the program to stop (default 20, max 120)", false));
    }

    // ── Gate ──

    public string? GetRefusal(ToolType type)
    {
        switch (type)
        {
            case ToolType.WatchCi or ToolType.WatchProcess or ToolType.WatchList or ToolType.WatchCancel when WatchRefusal() is { } noWatch:
                return noWatch;
            case ToolType.GhPrView or ToolType.GhPrChecks or ToolType.GhRunLog or ToolType.WatchCi:
                // Same gate as gh_pr_list: the GitHub features of the Features page and the license.
                bool on = _settings?.Settings.Features.GitHubIntegration != false
                          && _activation?.IsFeatureUnlocked("feature.github") != false;
                return on ? null : string.Format(L("eng.gh.locked"), Names[type]);
            case ToolType.GitBlame or ToolType.GitShow:
                bool git = _settings?.Settings.Features.GitIntegration != false
                           && _activation?.IsFeatureUnlocked("feature.git") != false;
                return git ? null : string.Format(L("eng.git.locked"), Names[type]);
            default:
                return null;
        }
    }

    public ToolPermission DescribePermission(ToolCall call, ToolModuleContext context)
    {
        switch (call.Type)
        {
            case ToolType.VerifyChange:
            {
                // Judged like run_build, by a command: the one the model wrote, else the detected build. A rule may answer
                // for ONE command only — with both written by the model, a "dotnet build*" Allow would also let an
                // arbitrary test_command through, so that call is always put to the user.
                var tools = _tools();
                bool skip = Arg(call, "tests").Equals("skip", StringComparison.OrdinalIgnoreCase);
                string ownBuild = Arg(call, "command"), ownTest = skip ? "" : Arg(call, "test_command");
                string build = ownBuild.Length > 0 ? ownBuild : tools?.DetectedProjectCommand(test: false) ?? "verify_change";
                string? test = skip ? null : ownTest.Length > 0 ? ownTest : tools?.DetectedProjectCommand(test: true);
                string detail = $"verify_change runs the build: {build}" + (test != null ? $"\nand the tests: {test}" : "");
                if (ownBuild.Length > 0 && ownTest.Length > 0)
                    return new ToolPermission { Scope = "execute", Resource = build, AlwaysAsk = true, Detail = detail };
                return ToolPermission.Ask("execute", ownTest.Length > 0 ? ownTest : build, detail);
            }
            case ToolType.RenameSymbol when IsTrue(Arg(call, "apply")):
                return ToolPermission.Ask("write", $"rename {Arg(call, "old_name")} → {Arg(call, "new_name")}",
                    $"Renames every whole-word {Arg(call, "old_name")} in the project ({Arg(call, "expected_edits")} edits in the preview).");
            case ToolType.CheckpointRestore:
                return new ToolPermission
                {
                    Scope = "write",
                    Resource = "checkpoint_restore " + Arg(call, "id"),
                    AlwaysAsk = true,
                    AskEvenWhenPermissionsOff = true,
                    Detail = RestoreDetail(Arg(call, "id"), context.WorkingDirectory),
                };
            case ToolType.Debug:
                return DebugPermission(call);
            case ToolType.GitBlame or ToolType.GitShow:
                // Reading inside the project; a path outside it is refused by the tool itself.
                return ToolPermission.Free("read", Arg(call, "path") is { Length: > 0 } p ? p : Names[call.Type]);
            default:
                // find_references, rename preview, impact_of, checkpoint_list/diff, gh_* reads: no side effects.
                return ToolPermission.Free("read", Names.TryGetValue(call.Type, out var n) ? n : call.ToolName);
        }
    }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolModuleContext context, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(context.WorkingDirectory) || !Directory.Exists(context.WorkingDirectory))
            return Fail(call, "No project folder is open — open one first.");
        return call.Type switch
        {
            ToolType.VerifyChange => await VerifyAsync(call, context, ct),
            ToolType.FindReferences => await FindReferencesAsync(call, context, ct),
            ToolType.RenameSymbol => await RenameAsync(call, context, ct),
            ToolType.ImpactOf => await ImpactAsync(call, context, ct),
            ToolType.CheckpointList => CheckpointList(call, context),
            ToolType.CheckpointDiff => CheckpointDiff(call, context),
            ToolType.CheckpointRestore => CheckpointRestore(call, context),
            ToolType.GhPrView => await GhPrViewAsync(call, context, ct),
            ToolType.GhPrChecks => await GhPrChecksAsync(call, context, ct),
            ToolType.GhRunLog => await GhRunLogAsync(call, context, ct),
            ToolType.GitBlame => await GitBlameAsync(call, context, ct),
            ToolType.GitShow => await GitShowAsync(call, context, ct),
            ToolType.Debug => await DebugAsync(call, context, ct),
            ToolType.WatchCi => await WatchCiAsync(call, context, ct),
            ToolType.WatchProcess => WatchProcess(call, context),
            ToolType.WatchList => WatchList(call),
            ToolType.WatchCancel => WatchCancel(call),
            _ => Fail(call, string.Format(L("tool.module.unavailable"), call.ToolName, Id)),
        };
    }

    public IReadOnlyCollection<string> LocalCoreToolNames { get; } = new[] { "verify_change" };

    public string? GetPromptSection(PromptSectionContext context)
    {
        string text =
            "Before you say a code change is done, call verify_change: it builds, checks the binary is fresh, runs the tests and "
            + "says what was NOT verified — report that list honestly. Before renaming or changing a widely used symbol, use "
            + "impact_of / find_references, and rename_symbol (preview, then apply with expected_edits) instead of editing each "
            + "file. checkpoint_list / checkpoint_diff / checkpoint_restore rewind your own edits. For CI: gh_pr_checks, then "
            + "gh_run_log on the failed run; to wait for CI or a background process, watch_ci / watch_process and end your turn "
            + "(CluadeX starts your next turn with the result — never poll or sleep); git_blame / git_show for history. debug runs a Python or C# program under a debugger.";
        if (context.NativeTools) return text;
        // The [ACTION:] protocol: the model only knows a tool's arguments from an example.
        return text + """

            [ACTION: verify_change][/ACTION]
            [ACTION: find_references]
            symbol: CalculateTotal
            [/ACTION]
            [ACTION: rename_symbol]
            old_name: CalculateTotal
            new_name: ComputeTotal
            [/ACTION]
            (then again with apply: true and expected_edits: <the preview's count>)
            [ACTION: impact_of]
            symbol: CalculateTotal
            [/ACTION]
            [ACTION: checkpoint_list][/ACTION]
            [ACTION: checkpoint_diff]
            id: <id>
            [/ACTION]
            [ACTION: checkpoint_restore]
            id: <id>
            [/ACTION]
            [ACTION: gh_pr_checks]
            pr: 42
            [/ACTION]
            [ACTION: gh_run_log]
            run_id: 123456789
            [/ACTION]
            [ACTION: git_blame]
            path: src/Foo.cs
            start_line: 10
            end_line: 40
            [/ACTION]
            [ACTION: git_show]
            rev: HEAD~1
            [/ACTION]
            [ACTION: debug]
            action: start
            path: app.py
            breakpoints: 12
            [/ACTION]
            """;
    }

    // ── Helpers ──

    private static string Arg(ToolCall call, string key) => (call.GetArg(key) ?? "").Trim();

    private static bool IsTrue(string? v) => v?.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";

    private static int IntArg(ToolCall call, string key, int fallback, int min, int max) =>
        int.TryParse(Arg(call, key), out int v) ? Math.Clamp(v, min, max) : fallback;

    private static ToolResult Fail(ToolCall call, string msg) =>
        new() { Type = call.Type, ToolName = call.ToolName, Success = false, Output = msg, Error = msg };

    private static ToolResult Ok(ToolCall call, string output, string summary) =>
        new() { Type = call.Type, ToolName = call.ToolName, Success = true, Output = output, Summary = summary };

    /// <summary>A path the model gave, resolved inside the project — null when it leaves the project.</summary>
    private static string? InsideProject(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
            string r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return (full + Path.DirectorySeparatorChar).StartsWith(r, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch { return null; }
    }

    private static string Rel(string root, string full)
    {
        try { return Path.GetRelativePath(root, full).Replace('\\', '/'); }
        catch { return full; }
    }

    private static string L(string key) => LocalizedResources.Instance[key];
}
