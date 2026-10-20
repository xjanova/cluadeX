using System.IO;

namespace CluadeX.Services;

/// <summary>
/// Every git repository a project spans, and a push of each that works on its own (owner 2026-10-20: "การพุช หลาย repo
/// หรือเชื่อมโยงต้องดี"). The push paths knew one repo — the project root's — and passed git's raw error back: a branch
/// with no upstream failed ("use --set-upstream"), a detached HEAD pushed an empty name, a rejected push gave no advice,
/// and a repo beside the project (an allowed folder, a submodule, a nested repo) was never pushed at all.
///
/// Repos: the project's own, each folder opened beside it, its submodules (recursive) and repos nested up to three
/// levels down (not under node_modules / bin / obj / vendor). Pushed innermost first, so a parent never points at a
/// submodule commit the remote does not have yet. Per repo: detached HEAD or no remote → skipped and said why; no
/// upstream → `push -u` to origin (or the only remote); nothing ahead → up to date; else `push
/// --recurse-submodules=check`. Never --force. A rejected push says to pull --rebase first; an auth failure says to sign
/// in; a timeout says so.
/// </summary>
public static class GitMultiRepo
{
    public enum Outcome { Pushed, UpToDate, Skipped, Failed }

    public sealed record RepoState(string Path, string? Branch, bool Detached, string? Upstream, int Ahead, int Behind, IReadOnlyList<string> Remotes);

    public sealed record PushResult(string Path, string? Branch, Outcome Outcome, string Detail);

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "vendor", "build", "dist", ".dart_tool", ".venv", "venv", "__pycache__", ".next", ".nuxt",
    };

    /// <summary>The repositories of a project (top folders, deduplicated), innermost first.</summary>
    public static async Task<List<string>> DiscoverAsync(string projectRoot, IEnumerable<string>? besides = null, CancellationToken ct = default)
    {
        var tops = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            string full;
            try { full = Path.GetFullPath(p.Trim().Replace('/', '\\')).TrimEnd('\\'); } catch { return; }
            if (!tops.Contains(full, StringComparer.OrdinalIgnoreCase)) tops.Add(full);
        }

        foreach (var folder in new[] { projectRoot }.Concat(besides ?? Array.Empty<string>()))
        {
            if (!Directory.Exists(folder)) continue;
            var top = await GitService.RunIn(folder, new[] { "rev-parse", "--show-toplevel" }, 10000, ct);
            if (top.Success) Add(top.Output.Trim());
        }
        foreach (var repo in tops.ToList())
        {
            var subs = await GitService.RunIn(repo, new[] { "submodule", "status", "--recursive" }, 15000, ct);
            if (subs.Success)
                foreach (var rel in ParseSubmodulePaths(subs.Output)) Add(Path.Combine(repo, rel));
        }
        foreach (var nested in NestedRepos(projectRoot, 3)) Add(nested);
        // innermost first: deeper paths before the folders that contain them
        return tops.OrderByDescending(p => p.Count(c => c == '\\')).ToList();
    }

    /// <summary>Paths from `git submodule status --recursive` lines (" 1a2b… lib/core (v1.2)"; a leading -, + or U marks
    /// the state). Pure.</summary>
    public static List<string> ParseSubmodulePaths(string output)
    {
        var list = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length < 3) continue;
            string rest = line[1..].Trim();               // after the state column
            int sp = rest.IndexOf(' ');
            if (sp < 0) continue;
            string path = rest[(sp + 1)..];
            int paren = path.LastIndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) path = path[..paren];
            path = path.Trim();
            if (path.Length > 0) list.Add(path.Replace('/', '\\'));
        }
        return list;
    }

    /// <summary>Folders under <paramref name="root"/> (not the root itself) that have their own .git, up to
    /// <paramref name="depth"/> levels down.</summary>
    public static List<string> NestedRepos(string root, int depth)
    {
        var found = new List<string>();
        void Walk(string dir, int level)
        {
            if (level > depth) return;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir).ToList(); } catch { return; }
            foreach (var sub in subs)
            {
                string name = Path.GetFileName(sub);
                if (SkipDirs.Contains(name) || name.StartsWith('.')) continue;
                string git = Path.Combine(sub, ".git");
                if (Directory.Exists(git) || File.Exists(git)) found.Add(sub);
                Walk(sub, level + 1);
            }
        }
        if (Directory.Exists(root)) Walk(root, 1);
        return found;
    }

    /// <summary>Branch, upstream, ahead/behind from `git status --porcelain=v2 --branch` plus the remotes. Pure.</summary>
    public static RepoState ParseState(string path, string porcelain, string remotes)
    {
        string? head = null, upstream = null;
        int ahead = 0, behind = 0;
        foreach (var raw in porcelain.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("# branch.head ")) head = line["# branch.head ".Length..].Trim();
            else if (line.StartsWith("# branch.upstream ")) upstream = line["# branch.upstream ".Length..].Trim();
            else if (line.StartsWith("# branch.ab "))
            {
                foreach (var part in line["# branch.ab ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.StartsWith('+') && int.TryParse(part[1..], out int a)) ahead = a;
                    else if (part.StartsWith('-') && int.TryParse(part[1..], out int b)) behind = b;
                }
            }
        }
        bool detached = head is null or "(detached)";
        var rems = remotes.Split('\n').Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
        return new RepoState(path, detached ? null : head, detached, upstream, ahead, behind, rems);
    }

    public static async Task<RepoState> StateAsync(string repo, CancellationToken ct = default)
    {
        var st = await GitService.RunIn(repo, new[] { "status", "--porcelain=v2", "--branch" }, 20000, ct);
        var rm = await GitService.RunIn(repo, new[] { "remote" }, 10000, ct);
        return ParseState(repo, st.Output, rm.Output);
    }

    /// <summary>What to do with a repo in this state: the git arguments, or null with the reason it is skipped / done. Pure.</summary>
    public static (IReadOnlyList<string>? Args, Outcome Outcome, string Reason) Plan(RepoState s)
    {
        if (s.Detached || string.IsNullOrEmpty(s.Branch)) return (null, Outcome.Skipped, "detached HEAD — no branch to push (check out a branch first)");
        if (s.Remotes.Count == 0) return (null, Outcome.Skipped, "no remote configured (git remote add origin <url>)");
        if (string.IsNullOrEmpty(s.Upstream))
        {
            string? remote = s.Remotes.Contains("origin") ? "origin" : s.Remotes.Count == 1 ? s.Remotes[0] : null;
            if (remote == null) return (null, Outcome.Skipped, $"no upstream and several remotes ({string.Join(", ", s.Remotes)}), none named origin — say which");
            return (new[] { "push", "-u", remote, s.Branch! }, Outcome.Pushed, $"new branch → {remote}/{s.Branch}");
        }
        if (s.Ahead == 0) return (null, Outcome.UpToDate, s.Behind > 0 ? $"nothing to push ({s.Behind} behind {s.Upstream} — pull to update)" : "up to date");
        return (new[] { "push", "--recurse-submodules=check" }, Outcome.Pushed, $"{s.Ahead} commit(s) → {s.Upstream}");
    }

    /// <summary>git's push error in one actionable sentence. Pure.</summary>
    public static string Explain(GitResult r)
    {
        if (r.TimedOut) return "timed out — the remote did not answer (network, or a sign-in window waiting for you)";
        string e = (r.Error + "\n" + r.Output).Trim();
        string l = e.ToLowerInvariant();
        if (l.Contains("non-fast-forward") || l.Contains("fetch first") || l.Contains("[rejected]") && !l.Contains("protected"))
            return "rejected: the remote has commits you don't — pull --rebase first, then push again (never force)";
        if (l.Contains("protected branch") || l.Contains("gh006") || l.Contains("pre-receive hook declined"))
            return "rejected by the remote's branch protection — push a branch and open a pull request";
        if (l.Contains("authentication failed") || l.Contains("could not read username") || l.Contains("permission denied")
            || l.Contains("403") || l.Contains("terminal prompts disabled"))
            return "sign-in failed — sign in to the remote (git credential manager / gh auth login) and push again";
        if (l.Contains("could not resolve host") || l.Contains("unable to access") || l.Contains("connection timed out"))
            return "can't reach the remote — check the network / the remote URL";
        if (l.Contains("submodule") && l.Contains("not") && l.Contains("pushed"))
            return "a submodule commit is not on its remote yet — push the submodule first";
        string first = e.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "git failed";
        return first.Length > 200 ? first[..200] + "…" : first;
    }

    /// <summary>Push every repo of the project, innermost first; one result per repo.</summary>
    public static async Task<List<PushResult>> PushAllAsync(string projectRoot, IEnumerable<string>? besides = null, CancellationToken ct = default)
    {
        var results = new List<PushResult>();
        foreach (var repo in await DiscoverAsync(projectRoot, besides, ct))
            results.Add(await PushOneAsync(repo, ct));
        return results;
    }

    public static async Task<PushResult> PushOneAsync(string repo, CancellationToken ct = default)
    {
        var state = await StateAsync(repo, ct);
        var (args, outcome, reason) = Plan(state);
        if (args == null) return new PushResult(repo, state.Branch, outcome, reason);
        // A credential manager's sign-in window may need the user: two minutes, not git's 30 s default.
        var r = await GitService.RunIn(repo, args, 120_000, ct);
        return r.Success
            ? new PushResult(repo, state.Branch, Outcome.Pushed, reason)
            : new PushResult(repo, state.Branch, Outcome.Failed, Explain(r));
    }

    /// <summary>One line per repo for the model and the status line: "✓ D:\Code\App (main): 2 commit(s) → origin/main".</summary>
    public static string Summary(IReadOnlyList<PushResult> results)
    {
        if (results.Count == 0) return "No git repository in this project.";
        string Mark(Outcome o) => o switch { Outcome.Pushed => "✓", Outcome.UpToDate => "=", Outcome.Skipped => "–", _ => "✗" };
        return string.Join("\n", results.Select(r => $"{Mark(r.Outcome)} {r.Path}{(r.Branch != null ? $" ({r.Branch})" : "")}: {r.Detail}"));
    }
}
