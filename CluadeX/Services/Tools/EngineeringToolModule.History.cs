using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services.Tools;

/// <summary>
/// Read-only CI and history: gh_pr_view, gh_pr_checks, gh_run_log, git_blame, git_show. Every call runs the program
/// directly with an argument list (no shell), every model-given value is checked first (no value may start with "-",
/// so none is read as an option), stdin is closed and prompts are switched off, and a timeout kills the whole tree —
/// a missing or signed-out gh answers in words, never hangs.
/// </summary>
public sealed partial class EngineeringToolModule
{
    /// <summary>One finished process: exit code (-1 = did not start / timed out), stdout, stderr.</summary>
    public sealed record ProcessOutcome(int ExitCode, string Text, string Error, bool NotFound, bool TimedOut);

    private static readonly Regex SafeRev = new(@"^(?!-)[A-Za-z0-9._/~^@{}\-]{1,200}$", RegexOptions.Compiled);
    private static readonly Regex SafeRepo = new(@"^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$", RegexOptions.Compiled);
    private static readonly Regex SafePr = new(@"^(?:\d{1,9}|https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/pull/\d{1,9}|(?!-)[A-Za-z0-9._/\-]{1,200})$", RegexOptions.Compiled);
    private static readonly Regex SafeRunId = new(@"^\d{1,20}$", RegexOptions.Compiled);

    private const int MaxProcessOutput = 4 * 1024 * 1024;

    /// <summary>
    /// Run <paramref name="exe"/> (found on PATH) with <paramref name="args"/> as an argument list in <paramref name="cwd"/>.
    /// Never throws for a missing program, a timeout or a cancel: the outcome says which. Output is capped at 4 MB.
    /// </summary>
    public static async Task<ProcessOutcome> RunProcessAsync(string exe, IEnumerable<string> args, string cwd, int timeoutMs, CancellationToken ct)
    {
        string? path = FindExecutable(exe);
        if (path == null) return new ProcessOutcome(-1, "", "", NotFound: true, TimedOut: false);
        var psi = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // No prompt can wait on a closed stdin: gh and git are told not to ask, page or colour.
        psi.Environment["GH_PROMPT_DISABLED"] = "1";
        psi.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        psi.Environment["GH_PAGER"] = "cat";
        psi.Environment["GIT_PAGER"] = "cat";
        psi.Environment["PAGER"] = "cat";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["CLICOLOR"] = "0";

        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex) { return new ProcessOutcome(-1, "", ex.Message, NotFound: ex is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }, TimedOut: false); }
        if (p == null) return new ProcessOutcome(-1, "", "the process did not start", false, false);
        using (p)
        {
            Background.AppChildren.Adopt(p);
            try { p.StandardInput.Close(); } catch { }
            var stdout = ReadCappedAsync(p.StandardOutput);
            var stderr = ReadCappedAsync(p.StandardError);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            bool timedOut = false;
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                try { p.Kill(entireProcessTree: true); } catch { }
            }
            string o = await stdout, e = await stderr;
            if (timedOut || ct.IsCancellationRequested) return new ProcessOutcome(-1, o, e, false, TimedOut: true);
            return new ProcessOutcome(p.ExitCode, o, e, false, false);
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader)
    {
        var sb = new StringBuilder();
        var buf = new char[8192];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
                if (sb.Length < MaxProcessOutput) sb.Append(buf, 0, Math.Min(n, MaxProcessOutput - sb.Length));
        }
        catch { /* the process was killed */ }
        return sb.ToString().Replace("\r\n", "\n");
    }

    /// <summary>The full path of <paramref name="exe"/> on PATH (".exe" added on Windows), or null.</summary>
    private static string? FindExecutable(string exe)
    {
        string[] names = OperatingSystem.IsWindows() && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { exe + ".exe", exe + ".cmd" } : new[] { exe };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names)
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim().Trim('"'), n);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* a malformed PATH entry */ }
            }
        return null;
    }

    // ── gh ──

    private const string GhMissing = "The GitHub CLI (gh) is not installed or not on PATH, so nothing was asked of GitHub. "
        + "Install it (winget install --id GitHub.cli), run `gh auth login` once in a terminal, then try again — or tell the user.";

    /// <summary>Runs gh with the checked arguments; a missing gh, a sign-in problem, no repository or a timeout come
    /// back as a plain explanation.</summary>
    private static async Task<ToolResult> RunGhAsync(ToolCall call, ToolModuleContext ctx, List<string> args, int timeoutMs,
        string summary, CancellationToken ct, Func<ProcessOutcome, bool>? okWhen = null)
    {
        if (Safety.ProcessLaunchPolicy.DryRun && FindExecutable("gh") != null)
            return Ok(call, Safety.ProcessLaunchPolicy.Report("gh " + string.Join(" ", args)), "gh (dry-run)");
        var r = await RunProcessAsync("gh", args, ctx.WorkingDirectory!, timeoutMs, ct);
        if (r.NotFound) return Fail(call, GhMissing);
        if (r.TimedOut)
            return Fail(call, ct.IsCancellationRequested ? "Stopped." : $"gh did not answer within {timeoutMs / 1000} s (network or GitHub slow) — it was stopped; nothing changed. Try again later.");
        string err = r.Error.Trim();
        string low = err.ToLowerInvariant();
        bool ok = okWhen?.Invoke(r) ?? r.ExitCode == 0;
        if (!ok)
        {
            if (low.Contains("gh auth login") || low.Contains("not logged in") || low.Contains("authentication") || low.Contains("http 401"))
                return Fail(call, "gh is not signed in to GitHub (or its token expired). Ask the user to run `gh auth login` in a terminal. " + Clip(err, 300));
            if (low.Contains("not a git repository") || low.Contains("no git remotes") || low.Contains("could not determine") || low.Contains("none of the git remotes"))
                return Fail(call, "This project has no GitHub remote gh can use — pass repo=\"owner/name\". " + Clip(err, 300));
            if (low.Contains("no pull requests found") || low.Contains("could not resolve to a pullrequest") || low.Contains("no checks reported"))
                return Fail(call, Clip(err, 600));
            return Fail(call, $"gh failed (exit {r.ExitCode}): {Clip(err.Length > 0 ? err : r.Text.Trim(), 1500)}");
        }
        string text = r.Text.Trim();
        if (text.Length == 0) text = err.Length > 0 ? err : "(gh printed nothing)";
        return Ok(call, CluadeX.Helpers.OutputBudget.HeadAndTail(text, Math.Max(4000, ctx.OutputBudgetChars)), summary);
    }

    /// <summary>The checked --repo option, or an error.</summary>
    private static (List<string>? Args, string? Error) RepoArgs(ToolCall call)
    {
        string repo = Arg(call, "repo");
        if (repo.Length == 0) return (new List<string>(), null);
        if (!SafeRepo.IsMatch(repo)) return (null, $"repo must be owner/name (letters, digits, . _ -), not '{Clip(repo, 80)}'.");
        return (new List<string> { "--repo", repo }, null);
    }

    private static string? CheckPr(string pr) =>
        pr.Length == 0 || SafePr.IsMatch(pr) ? null : $"pr must be a number, a github.com pull request URL or a branch name, not '{Clip(pr, 80)}'.";

    private Task<ToolResult> GhPrViewAsync(ToolCall call, ToolModuleContext ctx, CancellationToken ct)
    {
        string pr = Arg(call, "pr");
        if (CheckPr(pr) is { } bad) return Task.FromResult(Fail(call, bad));
        var (repo, err) = RepoArgs(call);
        if (err != null) return Task.FromResult(Fail(call, err));
        var args = new List<string> { "pr", "view" };
        if (pr.Length > 0) args.Add(pr);
        args.AddRange(repo!);
        if (IsTrue(Arg(call, "comments"))) args.Add("--comments");
        return RunGhAsync(call, ctx, args, 30_000, "PR " + (pr.Length > 0 ? pr : "of this branch"), ct);
    }

    private Task<ToolResult> GhPrChecksAsync(ToolCall call, ToolModuleContext ctx, CancellationToken ct)
    {
        string pr = Arg(call, "pr");
        if (CheckPr(pr) is { } bad) return Task.FromResult(Fail(call, bad));
        var (repo, err) = RepoArgs(call);
        if (err != null) return Task.FromResult(Fail(call, err));
        var args = new List<string> { "pr", "checks" };
        if (pr.Length > 0) args.Add(pr);
        args.AddRange(repo!);
        // gh exits 1 when a check failed and 8 while some are pending — the listing is still the answer.
        return RunGhAsync(call, ctx, args, 30_000, "CI checks of PR " + (pr.Length > 0 ? pr : "of this branch"), ct,
            okWhen: r => r.ExitCode == 0 || (r.ExitCode is 1 or 8 && r.Text.Trim().Length > 0));
    }

    private async Task<ToolResult> GhRunLogAsync(ToolCall call, ToolModuleContext ctx, CancellationToken ct)
    {
        string runId = Arg(call, "run_id");
        var (repo, err) = RepoArgs(call);
        if (err != null) return Fail(call, err);
        if (runId.Length == 0)
        {
            var list = new List<string> { "run", "list", "--limit", "10" };
            list.AddRange(repo!);
            return await RunGhAsync(call, ctx, list, 30_000, "recent workflow runs", ct);
        }
        if (!SafeRunId.IsMatch(runId)) return Fail(call, $"run_id must be the run's number, not '{Clip(runId, 80)}'.");
        var args = new List<string> { "run", "view", runId, "--log-failed" };
        args.AddRange(repo!);
        var failed = await RunGhAsync(call, ctx, args, 90_000, $"failed-step log of run {runId}", ct);
        if (!failed.Success || failed.Output.Trim() is not ("" or "(gh printed nothing)")) return failed;
        // No failed step: the run's summary instead.
        var view = new List<string> { "run", "view", runId };
        view.AddRange(repo!);
        var summary = await RunGhAsync(call, ctx, view, 30_000, $"run {runId}", ct);
        if (summary.Success) summary.Output = "No failed step in this run (nothing in --log-failed). Its summary:\n" + summary.Output;
        return summary;
    }

    // ── git ──

    private static async Task<ToolResult> RunGitAsync(ToolCall call, ToolModuleContext ctx, List<string> args, string summary, CancellationToken ct)
    {
        // A repository's own config must not run programs for a read: no fsmonitor hook, no pager.
        var full = new List<string> { "-c", "core.fsmonitor=false", "--no-pager" };
        full.AddRange(args);
        var r = await RunProcessAsync("git", full, ctx.WorkingDirectory!, 30_000, ct);
        if (r.NotFound) return Fail(call, "git is not installed or not on PATH.");
        if (r.TimedOut) return Fail(call, ct.IsCancellationRequested ? "Stopped." : "git did not answer within 30 s — it was stopped.");
        if (r.ExitCode != 0)
        {
            string e = r.Error.Trim();
            if (e.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)) return Fail(call, "This project is not a git repository.");
            return Fail(call, $"git failed (exit {r.ExitCode}): {Clip(e.Length > 0 ? e : r.Text.Trim(), 1500)}");
        }
        string text = r.Text.TrimEnd();
        return Ok(call, CluadeX.Helpers.OutputBudget.HeadAndTail(text.Length > 0 ? text : "(empty)", Math.Max(4000, ctx.OutputBudgetChars)), summary);
    }

    private Task<ToolResult> GitBlameAsync(ToolCall call, ToolModuleContext ctx, CancellationToken ct)
    {
        string root = ctx.WorkingDirectory!;
        string? file = InsideProject(root, Arg(call, "path"));
        if (file == null) return Task.FromResult(Fail(call, Arg(call, "path").Length == 0 ? "git_blame needs 'path'." : $"'{Arg(call, "path")}' is outside the project."));
        string rev = Arg(call, "rev");
        if (rev.Length > 0 && !SafeRev.IsMatch(rev)) return Task.FromResult(Fail(call, $"rev must be a commit, tag or branch name, not '{Clip(rev, 80)}'."));
        if (rev.Length == 0 && !File.Exists(file)) return Task.FromResult(Fail(call, $"No file at {Arg(call, "path")}."));
        int start = IntArg(call, "start_line", 1, 1, 10_000_000);
        int end = IntArg(call, "end_line", start + 199, start, start + 1999);
        var args = new List<string> { "blame", "--date=short", "-L", $"{start},{end}" };
        if (rev.Length > 0) args.Add(rev);
        args.Add("--");
        args.Add(Rel(root, file));
        return RunGitAsync(call, ctx, args, $"blame {Rel(root, file)}:{start}-{end}", ct);
    }

    private Task<ToolResult> GitShowAsync(ToolCall call, ToolModuleContext ctx, CancellationToken ct)
    {
        string root = ctx.WorkingDirectory!;
        string rev = Arg(call, "rev") is { Length: > 0 } r ? r : "HEAD";
        if (!SafeRev.IsMatch(rev)) return Task.FromResult(Fail(call, $"rev must be a commit, tag or branch name, not '{Clip(rev, 80)}'."));
        if (Arg(call, "path") is { Length: > 0 } p)
        {
            string? file = InsideProject(root, p);
            if (file == null) return Task.FromResult(Fail(call, $"'{p}' is outside the project."));
            // "./" = relative to the project folder, which may sit below the repository's root.
            return RunGitAsync(call, ctx, new List<string> { "show", "--no-textconv", $"{rev}:./{Rel(root, file)}" },
                $"{Rel(root, file)} at {rev}", ct);
        }
        var args = new List<string> { "show", "--no-ext-diff", "--no-textconv", "--stat", "--format=fuller" };
        if (!IsTrue(Arg(call, "stat_only"))) args.Add("--patch");
        args.Add(rev);
        args.Add("--");
        return RunGitAsync(call, ctx, args, $"commit {rev}", ct);
    }
}
