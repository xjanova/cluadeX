namespace CluadeX.Services.Safety;

/// <summary>
/// The check every shell entry point of CluadeX makes before a command runs — run_command, run_build, run_tests,
/// powershell, bg_start, code blocks, the verify build. ALWAYS on: unlike the Features "dangerous command blocking"
/// list it cannot be switched off, because what it refuses can only ever destroy (wiping a whole drive, a delete aimed
/// at the root or a home folder, deleting Windows' backups, wiping a database), and before code runs it reads the code
/// that was just changed — and any script the command names — for the same thing.
/// </summary>
public static class CodeRunGuard
{
    /// <summary>Null = may run; otherwise the refusal, worded for the agent and the user.</summary>
    public static string? Check(string? command, string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (DestructiveCodeScan.JudgeCommand(command) is { } direct)
            return "CluadeX safety: refused — this command " + direct + ". Only the user may run such a command, by hand.";
        if (DestructiveCodeScan.RunsProjectCode(command))
        {
            var findings = string.IsNullOrWhiteSpace(projectRoot) ? new List<DestructiveFinding>() : DestructiveCodeScan.ScanChanged(projectRoot);
            if (findings.Count == 0) findings = DestructiveCodeScan.ScanScriptsNamed(command, projectRoot);
            if (findings.Count > 0) return DestructiveCodeScan.BlockMessage(findings);
        }
        return null;
    }

    /// <summary><see cref="Check"/> off the UI thread (it may read git status and changed files).</summary>
    public static Task<string?> CheckAsync(string? command, string? projectRoot) => Task.Run(() => Check(command, projectRoot));

    /// <summary>For code that is about to run as it is (a code block, a REPL snippet): judge the code itself.</summary>
    public static string? CheckCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var findings = DestructiveCodeScan.ScanText(code, "(code)");
        return findings.Count == 0 ? null : DestructiveCodeScan.BlockMessage(findings);
    }

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd", ".ps1", ".psm1", ".py", ".pyw", ".js", ".mjs", ".cjs", ".ts", ".vbs", ".vbe", ".jse", ".wsf",
        ".wsh", ".sh", ".bash", ".rb", ".pl", ".php", ".lua",
    };

    /// <summary>For a file about to be started directly (desktop_launch of a .bat, a .ps1, a .py …): the file is code,
    /// so judge what is in it. Null for apps and documents, and for a script that reads clean.</summary>
    public static string? CheckScriptFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !ScriptExtensions.Contains(System.IO.Path.GetExtension(path))) return null;
        try
        {
            var info = new System.IO.FileInfo(path);
            if (!info.Exists) return null;
            if (info.Length > 1_000_000)
                return "CluadeX safety: refused — " + info.Name + " is a script too large to check (over 1 MB). Only the user may start it, by hand.";
            var findings = DestructiveCodeScan.ScanText(System.IO.File.ReadAllText(path), info.Name);
            return findings.Count == 0 ? null : DestructiveCodeScan.BlockMessage(findings);
        }
        catch (Exception ex) { return "CluadeX safety: refused — could not read the script to check it (" + ex.GetType().Name + ")."; }
    }
}

/// <summary>
/// Dry-run for test harnesses that call agent tools directly (no permission prompt): every tool that would start a
/// process — run_command, powershell, run_build / run_tests, bg_start, repl, code blocks, desktop_launch — makes its
/// safety checks, then reports what it WOULD start and starts nothing.
/// ON BY DEFAULT in every host that is not the CluadeX app itself (a harness, a script, any other exe that loads this
/// assembly), and with <c>CLUADEX_DRY_RUN=1</c>. Only code can switch it off (<c>ProcessLaunchPolicy.DryRun = false</c>,
/// in a harness whose purpose is real command output) — there is deliberately no environment switch to turn it off,
/// so a command line cannot. A harness that needs one real app (its own child window) names it in <see cref="Allowed"/>.
/// </summary>
public static class ProcessLaunchPolicy
{
    private static volatile bool _dryRun = DefaultDryRun();

    private static bool DefaultDryRun()
    {
        if (Environment.GetEnvironmentVariable("CLUADEX_DRY_RUN") == "1") return true;
        string? entry = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        return !string.Equals(entry, "CluadeX", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when nothing may start — shell commands, REPLs, code blocks, background jobs.</summary>
    public static bool DryRun { get => _dryRun; set => _dryRun = value; }

    /// <summary>Program file names (e.g. "notepad.exe") a dry-run harness may still really start through desktop_launch.</summary>
    public static HashSet<string> Allowed { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="program"/> must not start: dry-run is on and it is not in <see cref="Allowed"/>.</summary>
    public static bool Blocks(string program) =>
        _dryRun && !Allowed.Contains(System.IO.Path.GetFileName(program.Trim().Trim('"')));

    public static string Report(string what) => "[dry-run] would run: " + what + " — checks passed, nothing was started.";
}
