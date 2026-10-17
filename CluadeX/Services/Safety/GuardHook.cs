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
            if (!root.TryGetProperty("tool_input", out var ti) || ti.ValueKind != JsonValueKind.Object) return ("Claude Dev " + tool, "");
            string Str(string name) => ti.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string action = Str("command") is { Length: > 0 } c ? c
                : Str("file_path") is { Length: > 0 } f ? "write " + f
                : Str("notebook_path");
            return ("Claude Dev " + tool, action.Length > 4000 ? action[..4000] + "…" : action);
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
                return CheckWritten(Str("file_path"), Str("content"));
            case "Edit":
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
    public static string SettingsFile()
    {
        // The CluadeX.exe that sits next to this code — not whatever process happens to call (a test harness), and one
        // file per install (the installed app and a portable copy must not point each other's runs at the wrong exe).
        string here = Path.GetDirectoryName(typeof(GuardHook).Assembly.Location) ?? AppContext.BaseDirectory;
        string exeFull = File.Exists(Path.Combine(here, "CluadeX.exe")) ? Path.Combine(here, "CluadeX.exe")
            : Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CluadeX.exe");
        string exe = exeFull.Replace('\\', '/');
        var settings = new
        {
            hooks = new
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
            },
        };
        string dir = Path.Combine(Path.GetTempPath(), "cluadex-guard");
        Directory.CreateDirectory(dir);
        string id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(exe.ToLowerInvariant())))[..10];
        string path = Path.Combine(dir, $"claude-dev-settings-{id}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return path;
    }
}
