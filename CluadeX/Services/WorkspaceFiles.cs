using System.Diagnostics;
using System.IO;
using System.Text;

namespace CluadeX.Services;

/// <summary>
/// The candidate-file list every search tool starts from — one implementation instead of several
/// divergent ones.
///
/// The one it replaces for grep_search (FileSystemService.SearchFiles) stopped after the FIRST 100 files
/// it enumerated, and its ignore test was <c>rel.Contains("/.git/")</c> — which never matches a
/// top-level ".git/…" path. So those 100 slots were spent on git objects and grep never reached the
/// source tree: searching this very repository for "ExecuteToolAsync" returned TODO.md and missed
/// AgentToolService.cs, where the method is defined. A search that silently misses is worse than one
/// that fails, because the model trusts the negative.
///
/// In a git work tree the list comes from git: tracked files plus untracked-but-not-ignored ones, so
/// .gitignore is honoured with git's own semantics. Outside git (or if git is unavailable) it walks the
/// tree and prunes well-known build/vendor folders. Either way the caller is told when a cap was hit.
/// </summary>
public static class WorkspaceFiles
{
    public const int DefaultMaxFiles = 50_000;

    /// <summary>Build/vendor output: never the source, and routinely 10–100× its size.</summary>
    private static readonly HashSet<string> PrunedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".vs", ".idea", "__pycache__", ".pytest_cache",
        ".mypy_cache", ".venv", "venv", ".tox", "dist", "build", "out", "target", "packages", ".nuget",
        ".next", ".nuxt", ".gradle", ".cache", "coverage", ".terraform",
    };

    /// <summary>Hidden folders that DO hold source/config worth searching.</summary>
    private static readonly HashSet<string> KeptHiddenDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".github", ".gitlab", ".vscode", ".claude", ".cluadex", ".husky", ".config", ".devcontainer",
    };

    public sealed class Listing
    {
        /// <summary>Paths relative to the PROJECT root, forward slashes.</summary>
        public List<string> Files { get; } = new();
        /// <summary>The file cap was reached — results cover only part of the tree.</summary>
        public bool Truncated { get; set; }
        /// <summary>"git" (.gitignore honoured) or "walk".</summary>
        public string Source { get; set; } = "walk";
    }

    /// <param name="projectRoot">Absolute project root (paths are returned relative to it).</param>
    /// <param name="searchRoot">Absolute directory to list (the project root or a folder inside it).</param>
    public static Listing List(string projectRoot, string searchRoot, int maxFiles = DefaultMaxFiles)
    {
        if (!Directory.Exists(searchRoot)) return new Listing();
        return TryGit(projectRoot, searchRoot, maxFiles) ?? Walk(projectRoot, searchRoot, maxFiles);
    }

    public static bool IsPrunedDirName(string name) =>
        PrunedDirs.Contains(name) || (name.StartsWith('.') && !KeptHiddenDirs.Contains(name));

    /// <summary>
    /// <c>.claude/worktrees</c>: Claude Code's agent worktrees — each one a full checkout of the project. .claude is
    /// kept for its skills and agents, but walking into these listed every file once per worktree: a search over a
    /// repo with ten of them was ten times slower and returned every hit eleven times. (git ls-files already skips
    /// them: a nested worktree is one entry.)
    /// </summary>
    public static bool IsAgentWorktrees(string parentDir, string name) =>
        name.Equals("worktrees", StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(parentDir.TrimEnd('\\', '/')).Equals(".claude", StringComparison.OrdinalIgnoreCase);

    // ───────────────────────── git ─────────────────────────

    private static Listing? TryGit(string projectRoot, string searchRoot, int maxFiles)
    {
        // Two queries so the build-folder safety net only applies to UNTRACKED files: a tracked bin/
        // (bin/rails, a CLI package's bin/) is real source, while an untracked one in a repo with no
        // .gitignore entry is build output.
        var tracked = RunGit(searchRoot, "ls-files --cached -z");
        if (tracked == null) return null;                       // not a work tree / git unavailable
        var untracked = RunGit(searchRoot, "ls-files --others --exclude-standard -z") ?? new List<string>();

        var listing = new Listing { Source = "git" };
        foreach (var rel in tracked)
            if (!Add(listing, projectRoot, searchRoot, rel, maxFiles)) return listing;
        foreach (var rel in untracked)
        {
            var parts = rel.Split('/');
            if (parts.SkipLast(1).Any(IsPrunedDirName) || InAgentWorktrees(parts)) continue;
            if (!Add(listing, projectRoot, searchRoot, rel, maxFiles)) return listing;
        }
        return listing;
    }

    /// <summary>An untracked path under <c>.claude/worktrees/</c> (see <see cref="IsAgentWorktrees"/>): git lists a
    /// nested worktree as one entry, but anything else an agent left there — a build output folder of DLLs — was read
    /// by every search.</summary>
    private static bool InAgentWorktrees(string[] parts)
    {
        for (int i = 0; i + 2 < parts.Length; i++)
            if (parts[i].Equals(".claude", StringComparison.OrdinalIgnoreCase)
                && parts[i + 1].Equals("worktrees", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool Add(Listing listing, string projectRoot, string searchRoot, string relToSearch, int maxFiles)
    {
        if (listing.Files.Count >= maxFiles) { listing.Truncated = true; return false; }
        string full = Path.GetFullPath(Path.Combine(searchRoot, relToSearch));
        listing.Files.Add(Path.GetRelativePath(projectRoot, full).Replace('\\', '/'));
        return true;
    }

    private static List<string>? RunGit(string cwd, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "-c core.quotepath=false " + args)
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var p = Process.Start(psi);
            if (p == null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15_000)) { try { p.Kill(true); } catch { } return null; }
            if (p.ExitCode != 0) return null;
            return stdout.Result.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        catch { return null; }
    }

    // ───────────────────────── walk ─────────────────────────

    private static Listing Walk(string projectRoot, string searchRoot, int maxFiles)
    {
        var listing = new Listing { Source = "walk" };
        var stack = new Stack<string>();
        stack.Push(searchRoot);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); } catch { continue; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                if (listing.Files.Count >= maxFiles) { listing.Truncated = true; return listing; }
                listing.Files.Add(Path.GetRelativePath(projectRoot, f).Replace('\\', '/'));
            }

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); } catch { continue; }
            Array.Sort(subdirs, StringComparer.OrdinalIgnoreCase);
            for (int i = subdirs.Length - 1; i >= 0; i--)
            {
                string name = Path.GetFileName(subdirs[i]);
                if (IsPrunedDirName(name) || IsAgentWorktrees(dir, name)) continue;
                // A junction looping back to an ancestor would otherwise re-list the tree until the cap.
                try { if ((File.GetAttributes(subdirs[i]) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                stack.Push(subdirs[i]);
            }
        }
        return listing;
    }
}
