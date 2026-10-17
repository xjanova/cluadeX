namespace CluadeX.Services.Safety;

/// <summary>
/// The check every shell entry point of CluadeX makes before a command runs — run_command, run_build, run_tests,
/// powershell, bg_start, code blocks, the verify build. ALWAYS on: unlike the Features "dangerous command blocking"
/// list it cannot be switched off, because what it refuses can only ever destroy (a delete aimed at a drive, a home or
/// a system folder; a disk wipe), and before code runs it reads the code that was just changed for the same thing.
/// </summary>
public static class CodeRunGuard
{
    /// <summary>Null = may run; otherwise the refusal, worded for the agent and the user.</summary>
    public static string? Check(string? command, string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (DestructiveCodeScan.JudgeLine(command) is { } direct)
            return "CluadeX safety: refused — this command " + direct + ". Only the user may run such a command, by hand.";
        if (DestructiveCodeScan.RunsProjectCode(command) && !string.IsNullOrWhiteSpace(projectRoot))
        {
            var findings = DestructiveCodeScan.ScanChanged(projectRoot);
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
}
