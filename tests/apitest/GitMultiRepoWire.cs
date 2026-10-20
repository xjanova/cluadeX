// Pushing every repo of a project (owner 2026-10-20: "การพุช หลาย repo หรือเชื่อมโยงต้องดี"). Real git in a temp folder:
// a bare "remote" per repo, a project whose branch has no upstream yet, a repo nested inside it, a repo opened beside it,
// a detached HEAD, and a clone that fell behind (its push is rejected). Nothing leaves the temp folder.
using System.Diagnostics;
using System.IO;
using CluadeX.Services;

namespace ApiTest;

internal static class GitMultiRepoWire
{
    static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        return o;
    }

    static string Repo(string dir, string remote)
    {
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        Git(dir, "config", "user.email", "t@t"); Git(dir, "config", "user.name", "t");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "1");
        Git(dir, "add", "."); Git(dir, "commit", "-q", "-m", "one");
        Directory.CreateDirectory(remote);
        Git(remote, "init", "-q", "--bare", "-b", "main");
        Git(dir, "remote", "add", "origin", remote);
        return dir;
    }

    public static async Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · push every repo of a project (git, temp folder)");

        // ─── pure parts ───
        var st = GitMultiRepo.ParseState(@"C:\r", "# branch.oid abc\n# branch.head feat\n# branch.upstream origin/feat\n# branch.ab +2 -1\n", "origin\nbackup\n");
        check("gm-parse", "porcelain v2: branch, upstream, ahead/behind, remotes", st.Branch == "feat" && st.Upstream == "origin/feat" && st.Ahead == 2 && st.Behind == 1 && st.Remotes.Count == 2, $"{st}");
        var det = GitMultiRepo.Plan(GitMultiRepo.ParseState(@"C:\r", "# branch.head (detached)\n", "origin\n"));
        check("gm-plan", "a detached HEAD is skipped with the reason", det.Args == null && det.Outcome == GitMultiRepo.Outcome.Skipped && det.Reason.Contains("detached"), det.Reason);
        var fresh = GitMultiRepo.Plan(GitMultiRepo.ParseState(@"C:\r", "# branch.head feat/x\n", "upstream\norigin\n"));
        check("gm-plan", "a new branch → push -u origin <branch> (origin preferred over other remotes)",
            fresh.Args != null && string.Join(" ", fresh.Args) == "push -u origin feat/x", string.Join(" ", fresh.Args ?? Array.Empty<string>()));
        var many = GitMultiRepo.Plan(GitMultiRepo.ParseState(@"C:\r", "# branch.head x\n", "a\nb\n"));
        check("gm-plan", "no upstream and several remotes without origin → skipped, asks which", many.Args == null && many.Reason.Contains("several remotes"), many.Reason);
        check("gm-plan", "nothing ahead → up to date, no push", GitMultiRepo.Plan(st with { Ahead = 0 }).Outcome == GitMultiRepo.Outcome.UpToDate, "");
        check("gm-parse", "submodule status lines → paths (state column and (describe) dropped)",
            GitMultiRepo.ParseSubmodulePaths(" 1a2b3c lib/core (v1.2)\n-9f8e7d vendor/ui\n+abc123 tools/gen (heads/main)\n")
                .SequenceEqual(new[] { @"lib\core", @"vendor\ui", @"tools\gen" }), "");
        check("gm-explain", "a rejected push says pull --rebase (never force)",
            GitMultiRepo.Explain(new GitResult { Error = " ! [rejected]        main -> main (fetch first)\nerror: failed to push some refs" }).Contains("pull --rebase"), "");
        check("gm-explain", "an auth failure says to sign in", GitMultiRepo.Explain(new GitResult { Error = "fatal: could not read Username for 'https://github.com': terminal prompts disabled" }).Contains("sign in"), "");

        // ─── real repos ───
        string tmp = Path.Combine(Path.GetTempPath(), "cx-gitmulti-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string project = Repo(Path.Combine(tmp, "app"), Path.Combine(tmp, "remotes", "app.git"));
            Git(project, "checkout", "-q", "-b", "feature/login");               // a new branch: no upstream yet
            File.WriteAllText(Path.Combine(project, "b.txt"), "2"); Git(project, "add", "."); Git(project, "commit", "-q", "-m", "two");
            string nested = Repo(Path.Combine(project, "packages", "sdk"), Path.Combine(tmp, "remotes", "sdk.git"));
            Git(nested, "push", "-q", "-u", "origin", "main");
            File.WriteAllText(Path.Combine(nested, "c.txt"), "3"); Git(nested, "add", "."); Git(nested, "commit", "-q", "-m", "three");   // 1 ahead
            string beside = Repo(Path.Combine(tmp, "api"), Path.Combine(tmp, "remotes", "api.git"));
            Git(beside, "push", "-q", "-u", "origin", "main");                    // up to date
            string detached = Repo(Path.Combine(tmp, "tool"), Path.Combine(tmp, "remotes", "tool.git"));
            Git(detached, "checkout", "-q", "--detach");

            var repos = await GitMultiRepo.DiscoverAsync(project, new[] { beside, detached });
            check("gm-discover", "the project's repo, the repo nested in it and the folders opened beside it — innermost first",
                repos.Count == 4 && repos.IndexOf(nested) < repos.IndexOf(project) && repos.Contains(beside) && repos.Contains(detached), string.Join(" | ", repos));

            var results = await GitMultiRepo.PushAllAsync(project, new[] { beside, detached });
            GitMultiRepo.PushResult R(string p) => results.First(r => string.Equals(r.Path, p, StringComparison.OrdinalIgnoreCase));
            check("gm-push", "the new branch is pushed with -u (its upstream set)",
                R(project).Outcome == GitMultiRepo.Outcome.Pushed && Git(project, "rev-parse", "--abbrev-ref", "@{u}").Trim() == "origin/feature/login", R(project).Detail);
            check("gm-push", "the nested repo's new commit is pushed", R(nested).Outcome == GitMultiRepo.Outcome.Pushed
                && Git(nested, "status", "-sb").Contains("## main...origin/main") && !Git(nested, "status", "-sb").Contains("ahead"), R(nested).Detail);
            check("gm-push", "a repo with nothing to push says up to date", R(beside).Outcome == GitMultiRepo.Outcome.UpToDate, R(beside).Detail);
            check("gm-push", "a detached HEAD is skipped, not pushed as an empty branch", R(detached).Outcome == GitMultiRepo.Outcome.Skipped, R(detached).Detail);

            // A clone that fell behind: its push is rejected → the advice, never a force.
            string clone = Path.Combine(tmp, "clone");
            Git(tmp, "clone", "-q", Path.Combine(tmp, "remotes", "api.git"), clone);
            Git(clone, "config", "user.email", "t@t"); Git(clone, "config", "user.name", "t");
            File.WriteAllText(Path.Combine(beside, "d.txt"), "4"); Git(beside, "add", "."); Git(beside, "commit", "-q", "-m", "four"); Git(beside, "push", "-q");
            File.WriteAllText(Path.Combine(clone, "e.txt"), "5"); Git(clone, "add", "."); Git(clone, "commit", "-q", "-m", "five");
            var behind = await GitMultiRepo.PushOneAsync(clone);
            check("gm-push", "a push the remote rejects (it has commits we don't) → failed with 'pull --rebase first'",
                behind.Outcome == GitMultiRepo.Outcome.Failed && behind.Detail.Contains("pull --rebase"), behind.Detail);
            check("gm-summary", "one line per repo with ✓ / = / – / ✗", GitMultiRepo.Summary(results).Split('\n').Length == 4
                && GitMultiRepo.Summary(results).Contains("✓") && GitMultiRepo.Summary(results).Contains("–"), GitMultiRepo.Summary(results));
        }
        finally
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(tmp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(tmp, true);
            }
            catch { }
        }
    }
}
