using System.IO;

namespace CluadeX.Services.Agents;

/// <summary>A git worktree a CLI gave a writing sub-agent.</summary>
public sealed record CliWorktree(string Path, string Branch, bool Locked);

/// <summary>
/// The worktrees Claude Code leaves under .claude/worktrees (isolation: worktree). It removes an untouched one itself, but
/// not when the process was stopped or the app closed; one with changes it leaves, and the work in it is the only copy.
/// So: remove what holds nothing, through git's own refusals (no --force, branch -d), and name what stays. Never a
/// worktree another session may be using: only this turn's, or one idle for a day.
/// </summary>
public static class CliWorktreeSweep
{
    private const string AgentFolder = "/.claude/worktrees/agent-";
    private const string AgentBranch = "refs/heads/worktree-agent-";
    public static readonly TimeSpan IdleAge = TimeSpan.FromHours(24);

    /// <summary>The sub-agent worktrees in <c>git worktree list --porcelain</c>. Pure.</summary>
    public static List<CliWorktree> Parse(string porcelain)
    {
        var found = new List<CliWorktree>();
        foreach (var block in porcelain.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string path = "", branch = "";
            bool locked = false;
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("worktree ", StringComparison.Ordinal)) path = line[9..].Trim();
                else if (line.StartsWith("branch ", StringComparison.Ordinal)) branch = line[7..].Trim();
                else if (line == "locked" || line.StartsWith("locked ", StringComparison.Ordinal)) locked = true;
            }
            if (path.Replace('\\', '/').Contains(AgentFolder, StringComparison.OrdinalIgnoreCase) && branch.StartsWith(AgentBranch, StringComparison.Ordinal))
                found.Add(new CliWorktree(path, branch["refs/heads/".Length..], locked));
        }
        return found;
    }

    /// <summary>The paths there are now. Empty when the folder is no git repository.</summary>
    public static async Task<HashSet<string>> ListAsync(string? folder, CancellationToken ct)
    {
        var root = string.IsNullOrWhiteSpace(folder) ? null : await WorktreeSandbox.RepoRootOfAsync(folder, ct);
        if (root == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var r = await WorktreeSandbox.GitAsync(root, ct, null, "worktree", "list", "--porcelain");
        return r.Code != 0 ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : Parse(r.Out).Select(w => w.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Clear out after a turn. <paramref name="before"/>: the paths from before it started. Returns this turn's
    /// worktrees that stay, because they hold work (or git would not let them go).</summary>
    public static async Task<List<CliWorktree>> SweepAsync(string? folder, HashSet<string> before, Action<string>? log, CancellationToken ct)
    {
        var kept = new List<CliWorktree>();
        var root = string.IsNullOrWhiteSpace(folder) ? null : await WorktreeSandbox.RepoRootOfAsync(folder, ct);
        if (root == null) return kept;
        var listed = await WorktreeSandbox.GitAsync(root, ct, null, "worktree", "list", "--porcelain");
        if (listed.Code != 0) return kept;
        foreach (var tree in Parse(listed.Out))
        {
            bool mine = !before.Contains(tree.Path);
            bool idle = DateTime.UtcNow - Directory.GetLastWriteTimeUtc(tree.Path) > IdleAge;
            if (!mine && !idle) continue;
            if (await HoldsWorkAsync(root, tree, ct) || !await RemoveAsync(root, tree, ct))
            {
                log?.Invoke($"kept {tree.Path} ({tree.Branch})");
                if (mine) kept.Add(tree);
                continue;
            }
            log?.Invoke($"removed {tree.Path} ({tree.Branch})");
        }
        return kept;
    }

    private static async Task<bool> HoldsWorkAsync(string root, CliWorktree tree, CancellationToken ct)
    {
        if (tree.Locked) return true;
        var status = await WorktreeSandbox.GitAsync(tree.Path, ct, null, "status", "--porcelain");
        if (status.Code != 0 || status.Out.Trim().Length > 0) return true;
        var ahead = await WorktreeSandbox.GitAsync(root, ct, null, "rev-list", "--count", "HEAD.." + tree.Branch);
        return ahead.Code != 0 || ahead.Out.Trim() != "0";
    }

    private static async Task<bool> RemoveAsync(string root, CliWorktree tree, CancellationToken ct)
    {
        var removed = await WorktreeSandbox.GitAsync(root, ct, null, "worktree", "remove", tree.Path);
        if (removed.Code != 0) return false;
        await WorktreeSandbox.GitAsync(root, ct, null, "branch", "-d", tree.Branch);
        return true;
    }
}
