using System.Diagnostics;
using System.IO;
using System.Text;

namespace CluadeX.Services.Agents;

/// <summary>
/// A throwaway git worktree for one worker or fixer: a detached checkout of HEAD next to the repository, seeded with the
/// run folder's uncommitted state (tracked changes and untracked files) and committed as a "seed" so the sub-agent's own
/// work is exactly <c>git diff seed</c>. The diff applies back to the run folder with <see cref="ApplyAsync"/>; dispose
/// removes the worktree. The run's branch and index are never touched — the seed commit is on a detached HEAD.
/// </summary>
public sealed class WorktreeSandbox : IAsyncDisposable
{
    /// <summary>The repository's top folder (the run folder may be a subfolder of it).</summary>
    public string RepoRoot { get; }
    /// <summary>The worktree's top folder.</summary>
    public string Root { get; }
    /// <summary>The run folder's counterpart inside the worktree — where the sub-agent works.</summary>
    public string WorkingDirectory { get; }
    public string SeedCommit { get; private set; } = "";
    private bool _disposed;

    private WorktreeSandbox(string repoRoot, string root, string workingDirectory)
    {
        RepoRoot = repoRoot; Root = root; WorkingDirectory = workingDirectory;
    }

    /// <summary>Where worktrees go: a sibling of the repository (same drive, outside the project so its git status and
    /// file scans never see them) — <c>&lt;parent&gt;/.cluadex-worktrees/&lt;repo&gt;-&lt;label&gt;-&lt;id&gt;</c>.</summary>
    public static string HomeFor(string repoRoot)
    {
        string full = Path.GetFullPath(repoRoot).TrimEnd('\\', '/');
        string parent = Path.GetDirectoryName(full) ?? full;
        return Path.Combine(parent, ".cluadex-worktrees");
    }

    public static async Task<string?> RepoRootOfAsync(string folder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        var r = await GitAsync(folder, ct, null, "rev-parse", "--show-toplevel");
        return r.Code == 0 && r.Out.Trim().Length > 0 ? Path.GetFullPath(r.Out.Trim()) : null;
    }

    public static async Task<bool> IsGitRepoAsync(string? folder, CancellationToken ct = default) =>
        folder != null && await RepoRootOfAsync(folder, ct) != null
        && (await GitAsync(folder, ct, null, "rev-parse", "--verify", "HEAD")).Code == 0;

    /// <summary>A new worktree seeded with <paramref name="runFolder"/>'s current state.</summary>
    public static async Task<WorktreeSandbox> CreateAsync(string runFolder, string label, CancellationToken ct)
    {
        string repo = await RepoRootOfAsync(runFolder, ct) ?? throw new InvalidOperationException($"{runFolder} is not in a git repository");
        string rel = Path.GetRelativePath(repo, Path.GetFullPath(runFolder));
        if (rel == ".") rel = "";
        string safe = new string(label.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray());
        string root = Path.Combine(HomeFor(repo), $"{Path.GetFileName(repo)}-{safe}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);

        var add = await GitAsync(repo, ct, null, "worktree", "add", "--detach", root, "HEAD");
        if (add.Code != 0) throw new InvalidOperationException("git worktree add failed: " + add.Out.Trim());
        var box = new WorktreeSandbox(repo, root, rel.Length == 0 ? root : Path.Combine(root, rel));
        try
        {
            // Tracked changes (staged and not) …
            var diff = await GitAsync(repo, ct, null, "diff", "HEAD", "--binary");
            if (diff.Code == 0 && diff.Out.Trim().Length > 0)
            {
                var ap = await GitAsync(root, ct, diff.Out, "apply", "--binary", "--whitespace=nowarn", "-");
                if (ap.Code != 0) throw new InvalidOperationException("seeding the worktree failed: " + ap.Out.Trim());
            }
            // … and untracked files the user's .gitignore does not hide.
            var others = await GitAsync(repo, ct, null, "ls-files", "--others", "--exclude-standard", "-z");
            foreach (var f in others.Out.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                string src = Path.Combine(repo, f), dst = Path.Combine(root, f);
                if (!File.Exists(src)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
            }
            await GitAsync(root, ct, null, "add", "-A");
            var commit = await GitAsync(root, ct, null, "-c", "user.name=CluadeX", "-c", "user.email=cluadex@localhost",
                "-c", "commit.gpgsign=false", "commit", "-q", "--no-verify", "--allow-empty", "-m", "cluadex seed");
            if (commit.Code != 0) throw new InvalidOperationException("seed commit failed: " + commit.Out.Trim());
            box.SeedCommit = (await GitAsync(root, ct, null, "rev-parse", "HEAD")).Out.Trim();
            return box;
        }
        catch
        {
            await box.DisposeAsync();
            throw;
        }
    }

    /// <summary>What the sub-agent changed since the seed, as a binary-safe patch (paths relative to the repository).</summary>
    public async Task<string> DiffAsync(CancellationToken ct = default)
    {
        await GitAsync(Root, ct, null, "add", "-A");
        var d = await GitAsync(Root, ct, null, "diff", "--cached", "--binary", SeedCommit);
        return d.Code == 0 ? d.Out : "";
    }

    /// <summary>Files changed since the seed (repository-relative, '/').</summary>
    public async Task<List<string>> ChangedFilesAsync(CancellationToken ct = default)
    {
        await GitAsync(Root, ct, null, "add", "-A");
        var d = await GitAsync(Root, ct, null, "diff", "--cached", "--name-only", SeedCommit);
        return d.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>Apply a sandbox's patch to the run folder's working tree (checked first; nothing changes when it would
    /// not apply cleanly). Null = applied; otherwise git's reason.</summary>
    public static async Task<string?> ApplyAsync(string runFolder, string patch, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(patch)) return null;
        string repo = await RepoRootOfAsync(runFolder, ct) ?? runFolder;
        var check = await GitAsync(repo, ct, patch, "apply", "--check", "--binary", "--whitespace=nowarn", "-");
        if (check.Code != 0) return check.Out.Trim();
        var ap = await GitAsync(repo, ct, patch, "apply", "--binary", "--whitespace=nowarn", "-");
        return ap.Code == 0 ? null : ap.Out.Trim();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            var r = await GitAsync(RepoRoot, CancellationToken.None, null, "worktree", "remove", "--force", Root);
            if (r.Code != 0 && Directory.Exists(Root))
            {
                ClearReadOnly(Root);
                Directory.Delete(Root, recursive: true);
            }
        }
        catch { /* best effort: prune below forgets a folder that is gone */ }
        try { await GitAsync(RepoRoot, CancellationToken.None, null, "worktree", "prune"); } catch { }
        try
        {
            string home = HomeFor(RepoRoot);
            if (Directory.Exists(home) && !Directory.EnumerateFileSystemEntries(home).Any()) Directory.Delete(home);
        }
        catch { }
    }

    private static void ClearReadOnly(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
    }

    /// <summary>git with arguments passed as a list (no shell, no quoting), stdout and stderr read together, optional stdin.</summary>
    public static async Task<(int Code, string Out)> GitAsync(string cwd, CancellationToken ct, string? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_PAGER"] = "cat";
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("core.quotepath=false");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (stdin != null)
        {
            var bytes = new UTF8Encoding(false).GetBytes(stdin);
            await p.StandardInput.BaseStream.WriteAsync(bytes, ct);
        }
        p.StandardInput.Close();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        string o = await outTask, e = await errTask;
        return (p.ExitCode, p.ExitCode == 0 ? o : (o + "\n" + e).Trim());
    }
}
