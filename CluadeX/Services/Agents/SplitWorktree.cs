using System.IO;

namespace CluadeX.Services.Agents;

/// <summary>A split chat's own git worktree (new_chat isolation "worktree"): where it is, the branch it is on, and the
/// commit it was cut from. <see cref="Folder"/> is where the chat works (the parent's folder's counterpart inside it —
/// the parent may work in a subfolder of its repository); <see cref="ParentFolder"/> is the checkout it must stay out of.</summary>
public sealed record ChatWorktree(string RepoRoot, string Root, string Folder, string Branch, string BaseCommit, string ParentFolder);

/// <summary>
/// Git worktrees for split chats. A chat opened with new_chat in the SAME project as the chat that opened it used to
/// share that chat's checkout, so two agents edited and built the same files at once; with isolation "worktree" the split
/// chat gets a checkout of its own, on a branch of its own, cut from the asking chat's HEAD. Unlike the throwaway
/// <see cref="WorktreeSandbox"/> of workers and workflow agents (a detached seed commit whose diff is applied back), this
/// one is a real branch: the parent merges it or opens a PR from it, and close_chat removes the worktree only once
/// nothing on it would be lost (<see cref="HeldWorkAsync"/>).
///
/// It lives where every CluadeX worktree lives — <see cref="WorktreeSandbox.HomeFor"/>, a sibling of the repository,
/// so the project's own scans and git status never see it — named <c>&lt;repo&gt;-split-&lt;slug&gt;-&lt;id&gt;</c>.
/// </summary>
public static class SplitWorktree
{
    /// <summary>Branches of split chats start with this.</summary>
    public const string BranchPrefix = "cluadex/split-";

    /// <summary>A new worktree for a split chat of the chat working in <paramref name="parentFolder"/>, on a new branch
    /// cut from that checkout's HEAD. <paramref name="title"/> names the branch (ASCII letters and digits only). Throws
    /// with the reason when the folder is not a git repository with a commit, or git refuses.</summary>
    public static async Task<ChatWorktree> CreateAsync(string parentFolder, string? title, CancellationToken ct = default)
    {
        if (!await WorktreeSandbox.IsGitRepoAsync(parentFolder, ct))
            throw new InvalidOperationException($"{parentFolder} is not a git repository with a commit, so the split chat cannot get a worktree");
        string top = await WorktreeSandbox.RepoRootOfAsync(parentFolder, ct)
            ?? throw new InvalidOperationException($"{parentFolder} is not in a git repository");
        string rel = Path.GetRelativePath(top, Path.GetFullPath(parentFolder));
        if (rel == ".") rel = "";
        string main = await MainRepoRootAsync(top, ct);
        string head = (await WorktreeSandbox.GitAsync(top, ct, null, "rev-parse", "HEAD")).Out.Trim();
        if (head.Length == 0) throw new InvalidOperationException("git could not read HEAD of " + top);

        string id = Guid.NewGuid().ToString("N")[..6];
        string slug = Slug(title);
        string branch = BranchPrefix + (slug.Length > 0 ? slug + "-" : "") + id;
        string root = Path.Combine(WorktreeSandbox.HomeFor(main), $"{Path.GetFileName(main)}-split-{(slug.Length > 0 ? slug + "-" : "")}{id}");
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        var add = await WorktreeSandbox.GitAsync(top, ct, null, "worktree", "add", "-b", branch, root, head);
        if (add.Code != 0) throw new InvalidOperationException("git worktree add failed: " + add.Out.Trim());
        return new ChatWorktree(main, root, rel.Length == 0 ? root : Path.Combine(root, rel), branch, head,
            Path.GetFullPath(parentFolder).TrimEnd('\\', '/'));
    }

    /// <summary>What would be lost if the worktree went now, or null when nothing would: uncommitted changes in it, or
    /// commits on its branch that no other local branch holds (merged = another branch has them). A worktree that is
    /// already gone holds nothing; a branch git cannot read is treated as holding work (never removed by guesswork).</summary>
    public static async Task<string?> HeldWorkAsync(ChatWorktree wt, CancellationToken ct = default)
    {
        if (!Directory.Exists(wt.Root)) return null;
        var status = await WorktreeSandbox.GitAsync(wt.Root, ct, null, "status", "--porcelain");
        if (status.Code != 0) return "git status failed in the worktree: " + status.Out.Trim();
        int dirty = status.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        // Commits on the branch that neither the commit it was cut from (a detached parent has no branch) nor any other
        // local branch holds. --exclude for --branches takes the name without refs/heads/.
        var ahead = await WorktreeSandbox.GitAsync(wt.RepoRoot, ct, null, "rev-list", "--count", "refs/heads/" + wt.Branch,
            "--not", wt.BaseCommit, "--exclude=" + wt.Branch, "--branches");
        if (ahead.Code != 0) return $"git could not read branch {wt.Branch}: " + ahead.Out.Trim();
        int commits = int.TryParse(ahead.Out.Trim(), out int n) ? n : 1;
        if (dirty == 0 && commits == 0) return null;
        var parts = new List<string>();
        if (commits > 0) parts.Add($"{commits} commit(s) on branch {wt.Branch} that no other branch has (not merged)");
        if (dirty > 0) parts.Add($"{dirty} uncommitted change(s) in {wt.Root}");
        return string.Join(" and ", parts);
    }

    /// <summary>Remove the worktree and its branch, but only when <see cref="HeldWorkAsync"/> finds nothing to lose.
    /// Null = removed (or already gone); otherwise why it was kept.</summary>
    public static async Task<string?> RemoveIfMergedAsync(ChatWorktree wt, CancellationToken ct = default)
    {
        if (await HeldWorkAsync(wt, ct) is { } held) return held;
        if (Directory.Exists(wt.Root))
        {
            // No --force: git itself refuses a worktree with changes, a second guard behind the check above.
            var removed = await WorktreeSandbox.GitAsync(wt.RepoRoot, ct, null, "worktree", "remove", wt.Root);
            if (removed.Code != 0) return "git worktree remove refused: " + removed.Out.Trim();
        }
        await WorktreeSandbox.GitAsync(wt.RepoRoot, ct, null, "worktree", "prune");
        // -D, not -d: another branch holds every commit (checked above), but -d only accepts a branch merged into HEAD.
        await WorktreeSandbox.GitAsync(wt.RepoRoot, ct, null, "branch", "-D", wt.Branch);
        DropEmptyHome(wt.RepoRoot);
        return null;
    }

    /// <summary>Remove it whatever it holds — only for a worktree made a moment ago whose chat never opened.</summary>
    public static async Task DiscardAsync(ChatWorktree wt)
    {
        await WorktreeSandbox.RemoveAsync(wt.RepoRoot, wt.Root);
        try { await WorktreeSandbox.GitAsync(wt.RepoRoot, CancellationToken.None, null, "branch", "-D", wt.Branch); } catch { }
    }

    /// <summary>The worktree note the parent chat's agent reads when the split chat ends a turn.</summary>
    public static string NoticeFor(ChatWorktree wt) =>
        $" Its work is on branch {wt.Branch} in its own worktree {wt.Root} (cut from {Short(wt.BaseCommit)}): review it there, "
        + $"then merge it (git merge {wt.Branch}) or open a PR from it. close_chat removes the worktree once nothing on it is unmerged.";

    /// <summary>What the split chat's agent is told with every turn: where it works, and that the parent's checkout is
    /// not its own.</summary>
    public static string InstructionFor(ChatWorktree wt) =>
        $"This chat works in its own git worktree {wt.Folder}, on branch {wt.Branch}, cut from the asking chat's HEAD "
        + $"({Short(wt.BaseCommit)}). Read, edit, build and run commands only there — never in {wt.ParentFolder}, which another "
        + "chat is working in. Commit your finished work on this branch; the asking chat merges it or opens a PR.";

    /// <summary>The repository's main checkout (a worktree's own top folder is not it): HomeFor keys off this, so a split
    /// of a split puts its worktree beside the others, not in a folder inside the home.</summary>
    private static async Task<string> MainRepoRootAsync(string top, CancellationToken ct)
    {
        var common = await WorktreeSandbox.GitAsync(top, ct, null, "rev-parse", "--path-format=absolute", "--git-common-dir");
        string dir = common.Code == 0 ? common.Out.Trim() : "";
        if (dir.Length > 0 && Path.GetFileName(dir.TrimEnd('\\', '/')).Equals(".git", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(Path.GetFullPath(dir.TrimEnd('\\', '/'))) is { } parent)
            return parent;
        return top;
    }

    private static string Slug(string? title)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in (title ?? "").ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 24) break;
        }
        return sb.ToString().Trim('-');
    }

    private static string Short(string sha) => sha.Length > 9 ? sha[..9] : sha;

    private static void DropEmptyHome(string repoRoot)
    {
        try
        {
            string home = WorktreeSandbox.HomeFor(repoRoot);
            if (Directory.Exists(home) && !Directory.EnumerateFileSystemEntries(home).Any()) Directory.Delete(home);
        }
        catch { }
    }
}
