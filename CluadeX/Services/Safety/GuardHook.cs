using System.IO;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Safety;

/// <summary>
/// <c>CluadeX.exe --cx-guard</c>: a Claude Code PreToolUse hook that every Claude Dev worker run carries
/// (<see cref="SettingsFile"/>, passed with <c>--settings</c>). Claude Code runs its own Bash; CluadeX's command
/// checks never saw those commands — that is how the 2026-10-17 <c>rd /s /q C:\</c> got through. The hook reads the
/// tool call on stdin and, for the calls below, asks the user on CluadeX's red prompt (<see cref="DangerApprovalPipe"/>):
/// a yes lets it run (exit 0); a no, no answer or no running app blocks it (exit 2, reason on stderr — shown to the
/// agent). The calls:
/// <list type="bullet">
/// <item>a shell command is on the dangerous list (always on, whatever the Features switch says);</item>
/// <item>a Write / Edit puts a delete aimed at a drive, a home or a system folder into a file;</item>
/// <item>a command runs code (a test, a build, a script) while code changed in the project holds such a delete.</item>
/// </list>
/// Headless: it runs before any WPF or Velopack start-up and never shows a window.
/// </summary>
public static class GuardHook
{
    public const string Flag = "--cx-guard";

    /// <summary><c>CluadeX.exe --cx-effort</c>: the Stop hook every Claude Dev run carries. It only reads the hook input's
    /// effort.level (Claude Code puts the level the turn REALLY ran at there — the CLI reports it nowhere else) into
    /// <see cref="EffortReportEnv"/>'s file, and never blocks.</summary>
    public const string EffortFlag = "--cx-effort";

    /// <summary>The file a run's hooks write the CLI's effort level into (ClaudeDevProvider sets it per run; the CLI passes
    /// its environment on to its hooks).</summary>
    public const string EffortReportEnv = "CLUADEX_EFFORT_REPORT";

    /// <summary>The level in a Claude Code hook input (<c>"effort":{"level":"high"}</c>), or null.</summary>
    public static string? EffortLevelOf(string hookJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(hookJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("effort", out var e)) return null;
            string? level = e.ValueKind switch
            {
                JsonValueKind.Object when e.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.String => l.GetString(),
                JsonValueKind.String => e.GetString(),
                _ => null,
            };
            level = level?.Trim().ToLowerInvariant();
            return level is { Length: > 0 and <= 16 } && level.All(char.IsLetter) ? level : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The model of the newest assistant entry in a Claude Code transcript (its last 64 KB), or null. A plain chat
    /// runs the CLI in text mode, which names no model; the transcript does.</summary>
    public static string? ModelFromTranscript(string? transcriptPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath)) return null;
            using var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - 65536);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[fs.Length - start];
            int read = 0;
            while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
            string tail = Encoding.UTF8.GetString(buf, 0, read);
            var hits = System.Text.RegularExpressions.Regex.Matches(tail, @"""model""\s*:\s*""(claude-[A-Za-z0-9.\-\[\]]{3,60})""");
            return hits.Count > 0 ? hits[^1].Groups[1].Value : null;
        }
        catch { return null; }
    }

    /// <summary>The report line for a hook input: "level" or "level model" (null when the input names no level).</summary>
    public static string? EffortReportOf(string hookJson)
    {
        if (EffortLevelOf(hookJson) is not { } level) return null;
        string? transcript = null;
        try
        {
            using var doc = JsonDocument.Parse(hookJson);
            if (doc.RootElement.TryGetProperty("transcript_path", out var tp) && tp.ValueKind == JsonValueKind.String) transcript = tp.GetString();
        }
        catch (JsonException) { }
        return ModelFromTranscript(transcript) is { } model ? level + " " + model : level;
    }

    /// <summary>Write the hook input's effort level (and the transcript's model) to the run's report file
    /// (no file named, no level: nothing).</summary>
    public static void ReportEffort(string hookJson)
    {
        try
        {
            string? path = Environment.GetEnvironmentVariable(EffortReportEnv);
            if (string.IsNullOrWhiteSpace(path) || EffortReportOf(hookJson) is not { } report) return;
            string tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, report, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* a report is never worth failing a tool call */ }
    }

    /// <summary>Read a run's report file: (level, model) — model null when the report named none.</summary>
    public static (string? Level, string? Model) ReadEffortReport(string path)
    {
        try
        {
            if (!File.Exists(path)) return (null, null);
            string[] parts = File.ReadAllText(path).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? (null, null) : (parts[0], parts.Length > 1 ? parts[1] : null);
        }
        catch { return (null, null); }
    }

    /// <summary>The Stop hook's entry point: report, then exit 0 (a Stop hook that exits 2 would make the CLI go on).</summary>
    public static int RunEffortReport()
    {
        try
        {
            using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            ReportEffort(stdin.ReadToEnd());
        }
        catch { }
        return 0;
    }

    /// <summary>Set in a Gemini Dev run that may run commands but not edit (the CLI's yolo mode allows both): the guard
    /// then refuses Gemini's file-writing tools outright.</summary>
    public const string NoEditEnv = "CLUADEX_GUARD_NO_EDIT";

    /// <summary>Gemini CLI's tool names (Gemini Dev runs carry this hook as a BeforeTool hook, same exe).</summary>
    private static bool IsGeminiTool(string tool) => tool is "run_shell_command" or "write_file" or "replace";

    /// <summary>What Claude Dev is never let run headless (ClaudeDevProvider.NeverTools, passed to the CLI as
    /// --disallowedTools). Gemini CLI has no such flag, so a Gemini Dev command is checked here: refused outright.</summary>
    public static string? NeverRun(string command)
    {
        string c = System.Text.RegularExpressions.Regex.Replace(command ?? "", @"\s+", " ").Trim().ToLowerInvariant();
        foreach (var bad in new[] { "git push --force", "git push -f", "git reset --hard", "git clean", "shutdown" })
            if (c.StartsWith(bad, StringComparison.Ordinal) || c.Contains("&& " + bad) || c.Contains("; " + bad) || c.Contains("| " + bad))
                return $"CluadeX safety: `{bad}` is never run by a headless agent — ask the user to run it themselves.";
        return null;
    }

    /// <summary>The hook's entry point. Exit 0 = allow, 2 = block.</summary>
    public static int Run()
    {
        string input;
        try
        {
            using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            input = stdin.ReadToEnd();
        }
        catch { return Block("CluadeX safety: the guard could not read the tool call — refused."); }
        ReportEffort(input);   // the level this turn really runs at, for the chat (mid-turn, before the Stop hook says it)

        // Refused outright, no prompt: what a headless agent never does (Gemini Dev has no --disallowedTools), and file
        // writes in a Gemini Dev run that was granted commands only.
        try
        {
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;
            string called = root.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
            if (called is "write_file" or "replace" && Environment.GetEnvironmentVariable(NoEditEnv) == "1")
                return Block("CluadeX safety: this run may run commands but not edit files — propose the change as a diff instead.");
            if (called == "run_shell_command" && root.TryGetProperty("tool_input", out var cmdIn) && cmdIn.ValueKind == JsonValueKind.Object
                && cmdIn.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String && NeverRun(cmd.GetString() ?? "") is { } never)
                return Block(never);
        }
        catch (JsonException) { return Block("CluadeX safety: the guard could not read the tool call — refused."); }

        string? reason;
        try { reason = Decide(input); }
        catch (Exception ex) { return Block("CluadeX safety: the guard failed (" + ex.GetType().Name + ") — refused to be safe."); }
        if (reason == null) return 0;
        // Owner's rule (2026-10-18): it may run — after the user's yes on the red prompt in the running CluadeX.
        // No app to ask, no answer in time, or a no = blocked, with the reason the agent sees.
        var (tool, action) = Describe(input);
        if (DangerApprovalPipe.Ask(tool, action, reason)) return 0;
        return Block(reason + "\nNot approved: the user did not say yes on CluadeX's red prompt (or CluadeX is not running to ask). "
                            + "Do not retry or work around this; tell the user what you wanted to run and why.");
    }

    /// <summary>Which tool and what it would do, for the prompt (the command, or the file it would write).</summary>
    private static (string Tool, string Action) Describe(string hookJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(hookJson);
            var root = doc.RootElement;
            string tool = root.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
            string who = IsGeminiTool(tool) ? "Gemini Dev " : "Claude Dev ";
            if (!root.TryGetProperty("tool_input", out var ti) || ti.ValueKind != JsonValueKind.Object) return (who + tool, "");
            string Str(string name) => ti.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string action = Str("command") is { Length: > 0 } c ? c
                : Str("file_path") is { Length: > 0 } f ? "write " + f
                : Str("notebook_path");
            return (who + tool, action.Length > 4000 ? action[..4000] + "…" : action);
        }
        catch { return ("Claude Dev", ""); }
    }

    private static int Block(string reason)
    {
        try
        {
            using var err = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
            err.WriteLine(reason);
        }
        catch { }
        return 2;
    }

    /// <summary>Null = allow; otherwise why the call is refused. Pure apart from reading the project's changed files.</summary>
    public static string? Decide(string hookJson)
    {
        using var doc = JsonDocument.Parse(hookJson);
        var root = doc.RootElement;
        string tool = root.TryGetProperty("tool_name", out var tn) ? tn.GetString() ?? "" : "";
        string cwd = root.TryGetProperty("cwd", out var c) ? c.GetString() ?? "" : "";
        if (!root.TryGetProperty("tool_input", out var ti) || ti.ValueKind != JsonValueKind.Object) return null;

        string Str(string name) => ti.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        switch (tool)
        {
            case "Bash":
            case "PowerShell":
            case "run_shell_command":   // Gemini Dev
            {
                string command = Str("command");
                var dialect = tool == "PowerShell" ? DangerousCommandCheck.Dialect.PowerShell : DangerousCommandCheck.Dialect.Unknown;
                if (DangerousCommandCheck.FindSerious(command, dialect) is { } line)
                    return "CluadeX safety: " + DangerousCommandCheck.BlockedMessage(line);
                if (DestructiveCodeScan.JudgeLine(command) is { } direct)
                    return "CluadeX safety: refused — this command " + direct + ".";
                if (DestructiveCodeScan.RunsProjectCode(command) && cwd.Length > 0)
                {
                    var findings = DestructiveCodeScan.ScanChanged(cwd);
                    if (findings.Count > 0) return DestructiveCodeScan.BlockMessage(findings);
                }
                return null;
            }
            case "Write":
            case "write_file":   // Gemini Dev
                return CheckWritten(Str("file_path"), Str("content"));
            case "Edit":
            case "replace":      // Gemini Dev
                return CheckWritten(Str("file_path"), Str("new_string"));
            case "MultiEdit":
                if (ti.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
                    foreach (var e in edits.EnumerateArray())
                        if (e.TryGetProperty("new_string", out var ns) && CheckWritten(Str("file_path"), ns.GetString() ?? "") is { } why)
                            return why;
                return null;
            case "NotebookEdit":
                return CheckWritten(Str("notebook_path"), Str("new_source"));
            default:
                return null;
        }
    }

    private static string? CheckWritten(string file, string text)
    {
        if (text.Length == 0) return null;
        var findings = DestructiveCodeScan.ScanText(text, string.IsNullOrEmpty(file) ? "(new text)" : Path.GetFileName(file));
        return findings.Count == 0 ? null
            : "CluadeX safety: refused to write this — it would put a delete aimed at a drive, a home or a system folder into the project:\n"
              + string.Join("\n", findings.Take(5).Select(f => "  " + f))
              + "\nA test of a safety check must use an inert sentinel (for example `echo CX-SENTINEL rd /s /q Z:\\cx-sentinel-nonexistent`) and assert on the check's decision, never run the real command.";
    }

    /// <summary>
    /// The settings file handed to every Claude Dev run that may run commands or edit files: one PreToolUse hook,
    /// this exe in guard mode. Rewritten on each call (cheap) so a moved or updated CluadeX is always the one called.
    /// </summary>
    /// <summary>The CluadeX.exe that sits next to this code — not whatever process happens to call (a test harness).</summary>
    public static string ExePath()
    {
        string here = Path.GetDirectoryName(typeof(GuardHook).Assembly.Location) ?? AppContext.BaseDirectory;
        return File.Exists(Path.Combine(here, "CluadeX.exe")) ? Path.Combine(here, "CluadeX.exe")
            : Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CluadeX.exe");
    }

    public static string SettingsFile() => WriteSettings(guard: true);

    /// <summary>The settings file for a Claude Dev run that may not edit or run (plain chat): the Stop hook that reports
    /// the effort the turn really ran at, nothing else.</summary>
    public static string ReportSettingsFile() => WriteSettings(guard: false);

    /// <summary>The hooks a Claude Dev run's settings carry, as JSON: the guard (workers only) and the effort report.</summary>
    public static string SettingsJson(string exe, bool guard)
    {
        var stop = new[] { new { hooks = new[] { new { type = "command", command = $"\"{exe}\" {EffortFlag}", timeout = 15 } } } };
        object hooks = guard
            ? new
            {
                PreToolUse = new[]
                {
                    new
                    {
                        matcher = "Bash|PowerShell|Write|Edit|MultiEdit|NotebookEdit",
                        // Long enough for the user to read the red prompt and answer (DangerApprovalPipe waits 280 s).
                        hooks = new[] { new { type = "command", command = $"\"{exe}\" {Flag}", timeout = 300 } },
                    },
                },
                Stop = stop,
            }
            : new { Stop = stop };
        return JsonSerializer.Serialize(new { hooks }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string WriteSettings(bool guard)
    {
        // One file per install (the installed app and a portable copy must not point each other's runs at the wrong exe).
        string exe = ExePath().Replace('\\', '/');
        string dir = Path.Combine(Path.GetTempPath(), "cluadex-guard");
        Directory.CreateDirectory(dir);
        string id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(exe.ToLowerInvariant())))[..10];
        string path = Path.Combine(dir, guard ? $"claude-dev-settings-{id}.json" : $"claude-dev-report-{id}.json");
        File.WriteAllText(path, SettingsJson(exe, guard), new UTF8Encoding(false));
        return path;
    }
}
