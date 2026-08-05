using System.Diagnostics;
using System.IO;
using System.Text;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// The autonomous build-test-fix-review loop — CluadeX's "drive to green" mode and a headline selling
/// point. It wraps the existing agentic loop (CodeAgentService.ExecuteAgenticAsync, which is
/// provider-agnostic, so this works with LOCAL models too) with an OUTER supervisory loop that:
///
///   Phase 1 (fix):   implement → run the project's build/test command ON THIS MACHINE → if it fails,
///                    feed the real compiler/test output back as the next task → repeat until it passes
///                    OR AutoFixMaxIterations is hit.
///   Phase 2 (review): once green, review the changed code for hidden bugs, fix what it finds, and
///                     iterate — either "until a pass finds nothing" (capped) or a fixed number of rounds.
///                     After a review fix it re-runs the verify command so a fix can't silently break the build.
///
/// EVERY loop is hard-capped (1-25) so it can never run forever — see the brain note
/// [[TRANSLATION_INFINITE_LOOP_FIX]] on why uncapped self-correction loops are dangerous.
/// </summary>
public class AutonomousCodingService
{
    private readonly CodeAgentService _agent;
    private readonly FileSystemService _fileSystem;
    private readonly SettingsService _settings;

    /// <summary>Status/progress for the UI (iteration banners, verify pass/fail, summary).</summary>
    public event Action<string>? OnPhase;

    public AutonomousCodingService(CodeAgentService agent, FileSystemService fileSystem, SettingsService settings)
    {
        _agent = agent;
        _fileSystem = fileSystem;
        _settings = settings;
    }

    public async Task<AutonomousResult> RunAsync(List<ChatMessage> history, string goal, CancellationToken ct = default)
    {
        var s = _settings.Settings;
        var result = new AutonomousResult();

        int maxFix = Math.Clamp(s.AutoFixMaxIterations, 1, 25);
        int maxReview = Math.Clamp(s.AutoReviewMaxRounds, 1, 25);
        string verifyCmd = string.IsNullOrWhiteSpace(s.AutoVerifyCommand)
            ? DetectVerifyCommand()
            : s.AutoVerifyCommand.Trim();
        result.VerifyCommand = verifyCmd;

        // ── Phase 1: implement → verify → fix, until green ──
        string task = goal;
        for (int i = 1; i <= maxFix; i++)
        {
            ct.ThrowIfCancellationRequested();
            result.FixIterations = i;

            Report($"🔧 Iteration {i}/{maxFix} — implementing…");
            var agentRes = await _agent.ExecuteAgenticAsync(history, task, ct: ct);
            AppendTurn(history, task, agentRes.FinalResponse);
            result.FinalResponse = agentRes.FinalResponse;

            if (string.IsNullOrEmpty(verifyCmd))
            {
                // Nothing to run (no recognised build/test). Treat the implementation as done.
                result.GoalMet = true;
                Report("✓ No build/test command detected — implementation done (set a Verify command to enable the test loop).");
                break;
            }

            Report($"▶ Iteration {i} — running `{verifyCmd}`…");
            var (ok, output) = await RunCommandAsync(verifyCmd, ct);
            result.LastVerifyOutput = output;
            if (ok)
            {
                result.GoalMet = true;
                Report($"✅ Iteration {i} — verification PASSED.");
                break;
            }

            Report($"✗ Iteration {i} — verification failed; feeding the errors back.");
            task = BuildFixTask(verifyCmd, output);
        }

        // ── Phase 2: hidden-bug review loop (only once it builds/passes) ──
        if (s.AutoReviewEnabled && result.GoalMet)
        {
            bool untilClean = !string.Equals(s.AutoReviewMode, "fixed_rounds", StringComparison.OrdinalIgnoreCase);
            for (int r = 1; r <= maxReview; r++)
            {
                ct.ThrowIfCancellationRequested();
                result.ReviewRounds = r;

                Report($"🔎 Review round {r}/{maxReview} — hunting hidden bugs…");
                var reviewRes = await _agent.ExecuteAgenticAsync(history, ReviewTask, ct: ct);
                AppendTurn(history, ReviewTask, reviewRes.FinalResponse);
                result.FinalResponse = reviewRes.FinalResponse;

                if (IsReviewClean(reviewRes.FinalResponse))
                {
                    result.ReviewClean = true;
                    Report($"✅ Review round {r} — no issues found.");
                    if (untilClean) break;          // until_clean: a clean pass ends it
                }
                else
                {
                    Report($"⟳ Review round {r} — issues found and fixed; re-checking the build.");
                    // A review fix must not silently break the build — re-run verify and repair if needed.
                    if (!string.IsNullOrEmpty(verifyCmd))
                    {
                        var (ok2, out2) = await RunCommandAsync(verifyCmd, ct);
                        result.LastVerifyOutput = out2;
                        if (!ok2)
                        {
                            Report("✗ A review fix broke the build — repairing.");
                            var repair = await _agent.ExecuteAgenticAsync(history, BuildFixTask(verifyCmd, out2), ct: ct);
                            AppendTurn(history, "(repair after review)", repair.FinalResponse);
                            result.ReviewClean = false;
                        }
                    }
                }

                // fixed_rounds always runs the full N; until_clean already broke out above when clean.
            }
        }

        result.Summary = BuildSummary(result, maxFix, maxReview);
        Report(result.Summary);
        return result;
    }

    private const string ReviewTask =
        "Now switch to REVIEWER mode. Carefully review ALL the code you changed in this session for hidden bugs: " +
        "edge cases, null/None handling, off-by-one, race conditions, resource leaks, swallowed errors, and security " +
        "issues. Read the actual changed files — don't assume. If you find ANY real problem, FIX it in the files, then " +
        "end your reply with the exact marker [ISSUES_FOUND]. Only if, after a genuine thorough review, the code is " +
        "truly clean, end your reply with exactly [NO_ISSUES]. Do not claim [NO_ISSUES] unless you actually found nothing.";

    private static bool IsReviewClean(string? response)
        => response != null && response.Contains("[NO_ISSUES]") && !response.Contains("[ISSUES_FOUND]");

    private static string BuildFixTask(string cmd, string output) =>
        $"The verification command `{cmd}` FAILED. Read the output, find the ROOT cause, and fix the code so the " +
        $"command passes cleanly. Make the edits to the files now.\n\nOutput:\n```\n{Truncate(output, 6000)}\n```";

    private static void AppendTurn(List<ChatMessage> history, string user, string? assistant)
    {
        history.Add(new ChatMessage { Role = MessageRole.User, Content = user });
        history.Add(new ChatMessage { Role = MessageRole.Assistant, Content = assistant ?? "" });
    }

    private void Report(string message) => OnPhase?.Invoke(message);

    /// <summary>Auto-detect a build/test command from the project's marker files. Empty = none recognised.</summary>
    public string DetectVerifyCommand()
    {
        if (!_fileSystem.HasWorkingDirectory) return "";
        string dir = _fileSystem.WorkingDirectory;
        try
        {
            if (HasFile(dir, "*.sln") || HasFile(dir, "*.csproj"))
                return "dotnet build -clp:ErrorsOnly --nologo";
            if (File.Exists(Path.Combine(dir, "Cargo.toml")))
                return "cargo build";
            if (File.Exists(Path.Combine(dir, "go.mod")))
                return "go build ./...";
            if (File.Exists(Path.Combine(dir, "pyproject.toml")) || File.Exists(Path.Combine(dir, "pytest.ini")) || File.Exists(Path.Combine(dir, "setup.py")))
                return "pytest -q";
            if (File.Exists(Path.Combine(dir, "package.json")))
                return "npm run build --if-present";
        }
        catch { /* detection is best-effort */ }
        return "";
    }

    private static bool HasFile(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    /// <summary>Run a shell command in the working dir, capturing stdout+stderr; 10-minute hard cap.</summary>
    private async Task<(bool ok, string output)> RunCommandAsync(string command, CancellationToken ct)
    {
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
                Arguments = OperatingSystem.IsWindows() ? $"/c {command}" : $"-lc \"{command.Replace("\"", "\\\"")}\"",
                WorkingDirectory = _fileSystem.HasWorkingDirectory ? _fileSystem.WorkingDirectory : Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            proc = Process.Start(psi);
            if (proc == null) return (false, "Failed to start the verify process.");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(10));

            // Read both streams concurrently to avoid a full-pipe deadlock.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            string output = (stdout + "\n" + stderr).Trim();
            return (proc.ExitCode == 0, output);
        }
        catch (OperationCanceledException)
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            return (false, "Verify command timed out (10 min) or was cancelled.");
        }
        catch (Exception ex)
        {
            return (false, $"Verify command error: {ex.Message}");
        }
        finally
        {
            proc?.Dispose();
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n…(truncated)";

    private static string BuildSummary(AutonomousResult r, int maxFix, int maxReview)
    {
        var sb = new StringBuilder("## 🤖 Autonomous run complete\n");
        sb.AppendLine(r.GoalMet
            ? $"✅ **Goal met** after {r.FixIterations} iteration(s)."
            : $"⚠ **Goal NOT met** after {r.FixIterations}/{maxFix} iterations — manual attention needed.");
        if (r.ReviewRounds > 0)
            sb.AppendLine(r.ReviewClean
                ? $"✅ Code review clean after {r.ReviewRounds} round(s)."
                : $"⚠ Review stopped after {r.ReviewRounds}/{maxReview} round(s) — issues may remain.");
        if (!string.IsNullOrEmpty(r.VerifyCommand))
            sb.AppendLine($"Verify command: `{r.VerifyCommand}`");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Outcome of an autonomous run.</summary>
public class AutonomousResult
{
    public bool GoalMet { get; set; }
    public bool ReviewClean { get; set; }
    public int FixIterations { get; set; }
    public int ReviewRounds { get; set; }
    public string VerifyCommand { get; set; } = "";
    public string LastVerifyOutput { get; set; } = "";
    public string FinalResponse { get; set; } = "";
    public string Summary { get; set; } = "";
}
