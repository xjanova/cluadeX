using System.Text.RegularExpressions;
using CluadeX.Services.Tools;

namespace CluadeX.Services.Watch;

/// <summary>
/// Reads CI through the GitHub CLI, argv only (no shell): a pull request's checks (`gh pr checks --json`, or its plain
/// listing on a gh too old for --json), else the Actions runs of a commit or of a branch's newest commit (`gh run list`).
/// A missing gh, a signed-out gh or a PR that does not exist is fatal (watching cannot work); a slow network is not.
/// </summary>
public sealed partial class GhCiProbe : ICiProbe
{
    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$")] private static partial Regex SafeRepoRx();
    [GeneratedRegex(@"^(?:\d{1,9}|https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/pull/\d{1,9})$")] private static partial Regex SafePrRx();
    [GeneratedRegex(@"^(?!-)(?!.*\.\.)[A-Za-z0-9._/\-]{1,200}$")] private static partial Regex SafeBranchRx();
    [GeneratedRegex(@"^[0-9a-fA-F]{7,40}$")] private static partial Regex SafeShaRx();

    /// <summary>Null when the values are safe to hand gh as arguments; else what is wrong. Nothing may start with '-'
    /// (it would be read as an option).</summary>
    public static string? CheckArgs(string? repo, string? pr, string? branch, string? commit)
    {
        if (!string.IsNullOrEmpty(repo) && !SafeRepoRx().IsMatch(repo)) return $"repo must be owner/name, not '{RunWatchService.Clip(repo, 80)}'.";
        if (!string.IsNullOrEmpty(pr) && !SafePrRx().IsMatch(pr)) return $"pr must be a number or a github.com pull request URL, not '{RunWatchService.Clip(pr, 80)}'.";
        if (!string.IsNullOrEmpty(branch) && !SafeBranchRx().IsMatch(branch)) return $"branch must be a branch name, not '{RunWatchService.Clip(branch, 80)}'.";
        if (!string.IsNullOrEmpty(commit) && !SafeShaRx().IsMatch(commit)) return $"commit must be a commit hash (7–40 hex digits), not '{RunWatchService.Clip(commit, 80)}'.";
        int given = new[] { pr, branch, commit }.Count(s => !string.IsNullOrEmpty(s));
        if (given > 1) return "Give one of pr, branch or commit, not several.";
        return null;
    }

    private const int TimeoutMs = 30_000;

    public async Task<CiProbeResult> ProbeAsync(WatchJob job, CancellationToken ct)
    {
        if (CheckArgs(job.Repo, job.Pr, job.Branch, job.Commit) is { } bad) return new(Array.Empty<CiCheck>(), bad, Fatal: true);
        var repo = job.Repo is { Length: > 0 } r ? new[] { "--repo", r } : Array.Empty<string>();
        if (job.Pr is { Length: > 0 } pr)
        {
            var args = new List<string> { "pr", "checks", pr, "--json", "name,state,bucket,link,workflow" };
            args.AddRange(repo);
            var o = await EngineeringToolModule.RunProcessAsync("gh", args, job.Cwd, TimeoutMs, ct).ConfigureAwait(false);
            if (Problem(o) is { } p) return p;
            string err = o.Error.ToLowerInvariant();
            if (err.Contains("no checks reported")) return new(Array.Empty<CiCheck>());
            if (err.Contains("unknown flag") && err.Contains("json"))
            {
                // gh older than 2.40: the plain listing (tab-separated when not a terminal).
                var plain = new List<string> { "pr", "checks", pr };
                plain.AddRange(repo);
                o = await EngineeringToolModule.RunProcessAsync("gh", plain, job.Cwd, TimeoutMs, ct).ConfigureAwait(false);
                if (Problem(o) is { } p2) return p2;
                if (o.Error.ToLowerInvariant().Contains("no checks reported")) return new(Array.Empty<CiCheck>());
                if (o.ExitCode is not (0 or 1 or 8)) return Failed(o);
                return new(RunWatchService.ParsePrChecksText(o.Text));
            }
            // gh exits 1 when a check failed and 8 while some are pending — the listing is still the answer.
            if (o.ExitCode is not (0 or 1 or 8) || o.Text.Trim().Length == 0) return Failed(o);
            try { return new(RunWatchService.ParsePrChecksJson(o.Text)); }
            catch (System.Text.Json.JsonException) { return new(Array.Empty<CiCheck>(), "gh printed something that is not the checks list: " + RunWatchService.Clip(o.Text.Trim(), 200)); }
        }
        var list = new List<string> { "run", "list" };
        if (job.Commit is { Length: > 0 } sha)
        {
            if (sha.Length < 40)
            {
                // gh matches the full hash only: a short one would list nothing, forever.
                sha = await FullShaAsync(sha, job.Cwd, ct).ConfigureAwait(false)
                      ?? "";
                if (sha.Length == 0)
                    return new(Array.Empty<CiCheck>(), $"commit {job.Commit} is not known in {job.Cwd} — gh needs the full 40-character hash.", Fatal: true);
                job.Commit = sha;
            }
            list.AddRange(new[] { "--commit", sha });
        }
        else if (job.Branch is { Length: > 0 } b) list.AddRange(new[] { "--branch", b });
        else return new(Array.Empty<CiCheck>(), "Nothing to watch: give pr, branch or commit.", Fatal: true);
        list.AddRange(new[] { "--json", "databaseId,name,workflowName,status,conclusion,url,headSha", "--limit", "30" });
        list.AddRange(repo);
        var run = await EngineeringToolModule.RunProcessAsync("gh", list, job.Cwd, TimeoutMs, ct).ConfigureAwait(false);
        if (Problem(run) is { } pRun) return pRun;
        if (run.ExitCode != 0) return Failed(run);
        try { return new(RunWatchService.ParseRunListJson(run.Text.Trim().Length == 0 ? "[]" : run.Text, newestCommitOnly: job.Commit == null)); }
        catch (System.Text.Json.JsonException) { return new(Array.Empty<CiCheck>(), "gh printed something that is not the runs list: " + RunWatchService.Clip(run.Text.Trim(), 200)); }
    }

    /// <summary>The full hash of <paramref name="sha"/> in the repository at <paramref name="cwd"/>, or null (not a commit
    /// there, no git, not a repository).</summary>
    public static async Task<string?> FullShaAsync(string sha, string cwd, CancellationToken ct)
    {
        if (!SafeShaRx().IsMatch(sha) || string.IsNullOrEmpty(cwd)) return null;
        var o = await EngineeringToolModule.RunProcessAsync("git", new[] { "rev-parse", "--verify", "--quiet", sha + "^{commit}" }, cwd, 15_000, ct)
            .ConfigureAwait(false);
        string full = o.Text.Trim();
        return o.ExitCode == 0 && full.Length == 40 && SafeShaRx().IsMatch(full) ? full.ToLowerInvariant() : null;
    }

    /// <summary>The outcomes no retry fixes, and a timeout (retried).</summary>
    private static CiProbeResult? Problem(EngineeringToolModule.ProcessOutcome o)
    {
        if (o.NotFound)
            return new(Array.Empty<CiCheck>(), "The GitHub CLI (gh) is not installed — install it (winget install --id GitHub.cli) and run `gh auth login` once.", Fatal: true);
        if (o.TimedOut) return new(Array.Empty<CiCheck>(), "gh did not answer within 30 s");
        string low = o.Error.ToLowerInvariant();
        if (low.Contains("gh auth login") || low.Contains("not logged in") || low.Contains("http 401"))
            return new(Array.Empty<CiCheck>(), "gh is not signed in to GitHub — run `gh auth login` in a terminal.", Fatal: true);
        if (low.Contains("no pull requests found") || low.Contains("could not resolve to a pullrequest") || low.Contains("no git remotes")
            || low.Contains("none of the git remotes") || low.Contains("not a git repository") || low.Contains("could not resolve to a repository"))
            return new(Array.Empty<CiCheck>(), RunWatchService.Clip(o.Error.Trim(), 300), Fatal: true);
        return null;
    }

    private static CiProbeResult Failed(EngineeringToolModule.ProcessOutcome o) =>
        new(Array.Empty<CiCheck>(), $"gh failed (exit {o.ExitCode}): " + RunWatchService.Clip((o.Error.Trim().Length > 0 ? o.Error : o.Text).Trim(), 300));
}
