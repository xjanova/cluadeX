using System.Diagnostics;
using System.IO;
using System.Text;

namespace CluadeX.Services;

/// <summary>
/// Provides Git operations for the coding agent.
/// Wraps the git CLI for status, commit, push, pull, branch, diff, log, clone.
/// </summary>
public class GitService
{
    private readonly FileSystemService _fileSystem;

    public GitService(FileSystemService fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>Working directory for git commands.</summary>
    public string WorkingDirectory => _fileSystem.WorkingDirectory;

    // ═══════════════════════════════════════════
    // Git Detection
    // ═══════════════════════════════════════════

    /// <summary>Check if git is installed on the system.</summary>
    public async Task<bool> IsGitInstalledAsync()
    {
        try
        {
            var result = await RunGitAsync("--version");
            return result.Success;
        }
        catch { return false; }
    }

    /// <summary>Check if the current working directory is a git repository.</summary>
    public async Task<bool> IsGitRepoAsync()
    {
        if (!_fileSystem.HasWorkingDirectory) return false;
        try
        {
            var result = await RunGitAsync("rev-parse --is-inside-work-tree");
            return result.Success && result.Output.Trim() == "true";
        }
        catch { return false; }
    }

    // ═══════════════════════════════════════════
    // Basic Git Operations
    // ═══════════════════════════════════════════

    /// <summary>git status (short format)</summary>
    public async Task<GitResult> StatusAsync()
    {
        return await RunGitAsync("status --short --branch");
    }

    /// <summary>
    /// Machine-readable status for the IDE panels: <c>-c core.quotepath=off status --porcelain=v1 -z
    /// --branch -- .</c>. The human <c>--short</c> form the agent tool prints is useless to a parser:
    /// a path with a space comes back wrapped in quotes and a Thai file name as octal escapes
    /// (<c>"\340\270\201.txt"</c>), so Open did nothing and Diff showed an empty file for exactly
    /// those files. With -z nothing is quoted or escaped, and porcelain paths are always relative to
    /// the REPOSITORY ROOT — which is why every entry also carries its absolute path (the opened
    /// folder may be a subfolder of the repo). The <c>-- .</c> pathspec keeps the list to the
    /// opened folder.
    /// </summary>
    public async Task<GitStatusSnapshot> StatusEntriesAsync()
    {
        var snap = new GitStatusSnapshot();
        if (!_fileSystem.HasWorkingDirectory) { snap.Error = "no-workdir"; return snap; }

        var top = await RunGitArgsAsync(new[] { "rev-parse", "--show-toplevel" });
        if (!top.Success) { snap.Error = top.Error; return snap; }
        try { snap.RepoRoot = Path.GetFullPath(top.Output.Trim()); }
        catch { snap.RepoRoot = _fileSystem.WorkingDirectory; }

        var r = await RunGitArgsAsync(new[]
        {
            "-c", "core.quotepath=off", "status", "--porcelain=v1", "-z", "--branch", "--", ".",
        });
        if (!r.Success) { snap.Error = r.Error; return snap; }

        ParsePorcelainZ(r.Output ?? "", snap);
        snap.Success = true;
        return snap;
    }

    /// <summary>Parse <c>status --porcelain=v1 -z --branch</c>. Separate from the process call so a
    /// harness can feed it raw bytes.</summary>
    public static void ParsePorcelainZ(string output, GitStatusSnapshot snap)
    {
        var records = output.Split('\0');
        for (int i = 0; i < records.Length; i++)
        {
            string rec = records[i];
            if (rec.Length == 0) continue;

            if (rec.StartsWith("## ", StringComparison.Ordinal))
            {
                ParseBranchLine(rec[3..], snap);
                continue;
            }
            if (rec.Length < 4) continue;

            string xy = rec[..2];
            var entry = new GitStatusEntry
            {
                XY = xy,
                RepoPath = rec[3..],
            };
            // Renames/copies: "R  new\0old\0" — the ORIGINAL path is the next record.
            if ((xy[0] == 'R' || xy[0] == 'C' || xy[1] == 'R' || xy[1] == 'C') && i + 1 < records.Length)
                entry.OrigRepoPath = records[++i];

            if (!string.IsNullOrEmpty(snap.RepoRoot))
            {
                try { entry.FullPath = Path.GetFullPath(Path.Combine(snap.RepoRoot, entry.RepoPath)); }
                catch { entry.FullPath = ""; }
            }
            snap.Entries.Add(entry);
        }
    }

    /// <summary>
    /// "master...origin/master [ahead 1]", "No commits yet on master", "HEAD (no branch)".
    /// The old parser cut at the first space or '.', which showed "No" for every brand-new repo and
    /// "release/1" for a branch called "release/1.2" (branch names cannot contain spaces or "...",
    /// but they can contain single dots).
    /// </summary>
    private static void ParseBranchLine(string b, GitStatusSnapshot snap)
    {
        foreach (var prefix in new[] { "No commits yet on ", "Initial commit on " })
        {
            if (b.StartsWith(prefix, StringComparison.Ordinal))
            {
                snap.NoCommits = true;
                b = b[prefix.Length..];
                break;
            }
        }
        if (b.StartsWith("HEAD (no branch)", StringComparison.Ordinal))
        {
            snap.IsDetached = true;
            snap.Branch = "HEAD";
            return;
        }
        int dots = b.IndexOf("...", StringComparison.Ordinal);
        if (dots >= 0) b = b[..dots];
        int space = b.IndexOf(' ');
        if (space >= 0) b = b[..space];
        snap.Branch = b.Trim();
    }

    /// <summary>git status (full / long format)</summary>
    public async Task<GitResult> StatusLongAsync()
    {
        return await RunGitAsync("status");
    }

    /// <summary>git add files. Pass "." for all, or specific paths.</summary>
    public async Task<GitResult> AddAsync(string paths = ".")
    {
        // Sanitize: only allow safe path characters
        if (!IsValidGitArg(paths))
            return new GitResult { Success = false, Error = "Invalid path characters." };
        return await RunGitAsync($"add -- {paths}");
    }

    /// <summary>git commit with message.</summary>
    public async Task<GitResult> CommitAsync(string message)
    {
        // Write message to temp file to avoid shell injection
        string tmpFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmpFile, message);
            return await RunGitAsync($"commit -F \"{tmpFile}\"");
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { }
        }
    }

    /// <summary>git push (with optional remote and branch)</summary>
    public async Task<GitResult> PushAsync(string remote = "", string branch = "")
    {
        if (!string.IsNullOrEmpty(remote) && !IsValidGitArg(remote))
            return new GitResult { Success = false, Error = "Invalid remote name" };
        if (!string.IsNullOrEmpty(branch) && !IsValidGitArg(branch))
            return new GitResult { Success = false, Error = "Invalid branch name" };
        string args = "push";
        if (!string.IsNullOrEmpty(remote)) args += $" {remote}";
        if (!string.IsNullOrEmpty(branch)) args += $" {branch}";
        return await RunGitAsync(args);
    }

    /// <summary>git push with set-upstream for new branches.</summary>
    public async Task<GitResult> PushSetUpstreamAsync(string remote = "origin", string? branch = null)
    {
        if (!IsValidGitArg(remote))
            return new GitResult { Success = false, Error = "Invalid remote name" };
        branch ??= (await GetCurrentBranchAsync()).Output.Trim();
        if (!string.IsNullOrEmpty(branch) && !IsValidGitArg(branch))
            return new GitResult { Success = false, Error = "Invalid branch name" };
        return await RunGitAsync($"push -u {remote} {branch}");
    }

    /// <summary>git pull (with optional remote and branch)</summary>
    public async Task<GitResult> PullAsync(string remote = "", string branch = "")
    {
        if (!string.IsNullOrEmpty(remote) && !IsValidGitArg(remote))
            return new GitResult { Success = false, Error = "Invalid remote name" };
        if (!string.IsNullOrEmpty(branch) && !IsValidGitArg(branch))
            return new GitResult { Success = false, Error = "Invalid branch name" };
        string args = "pull";
        if (!string.IsNullOrEmpty(remote)) args += $" {remote}";
        if (!string.IsNullOrEmpty(branch)) args += $" {branch}";
        return await RunGitAsync(args);
    }

    /// <summary>git fetch (all remotes)</summary>
    public async Task<GitResult> FetchAsync(string remote = "--all")
    {
        if (!IsValidGitArg(remote))
            return new GitResult { Success = false, Error = "Invalid remote name" };
        return await RunGitAsync($"fetch {remote}");
    }

    // ═══════════════════════════════════════════
    // Branch Operations
    // ═══════════════════════════════════════════

    /// <summary>Get current branch name.</summary>
    public async Task<GitResult> GetCurrentBranchAsync()
    {
        return await RunGitAsync("branch --show-current");
    }

    /// <summary>List all branches (local + remote).</summary>
    public async Task<GitResult> ListBranchesAsync(bool includeRemote = true)
    {
        string args = includeRemote ? "branch -a" : "branch";
        return await RunGitAsync(args);
    }

    /// <summary>Create and switch to a new branch.</summary>
    public async Task<GitResult> CreateBranchAsync(string branchName)
    {
        if (!IsValidBranchName(branchName))
            return new GitResult { Success = false, Error = "Invalid branch name. Use alphanumeric, hyphens, underscores, slashes only." };
        return await RunGitAsync($"checkout -b {branchName}");
    }

    /// <summary>Switch to existing branch.</summary>
    public async Task<GitResult> CheckoutAsync(string branchName)
    {
        if (!IsValidBranchName(branchName))
            return new GitResult { Success = false, Error = "Invalid branch name." };
        return await RunGitAsync($"checkout {branchName}");
    }

    /// <summary>Merge a branch into current branch.</summary>
    public async Task<GitResult> MergeAsync(string branchName)
    {
        if (!IsValidBranchName(branchName))
            return new GitResult { Success = false, Error = "Invalid branch name." };
        return await RunGitAsync($"merge {branchName}");
    }

    /// <summary>Delete a local branch.</summary>
    public async Task<GitResult> DeleteBranchAsync(string branchName, bool force = false)
    {
        if (!IsValidBranchName(branchName))
            return new GitResult { Success = false, Error = "Invalid branch name." };
        string flag = force ? "-D" : "-d";
        return await RunGitAsync($"branch {flag} {branchName}");
    }

    // ─── Input Validation ───
    private static readonly System.Text.RegularExpressions.Regex BranchNameRegex = new(
        @"^[a-zA-Z0-9_\-/.]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsValidBranchName(string name)
        => !string.IsNullOrWhiteSpace(name) && name.Length < 256
           && BranchNameRegex.IsMatch(name) && !name.Contains("..")
           && !name.StartsWith('-'); // a leading '-' makes git parse the ref as an option

    private static bool IsValidGitArg(string arg)
        => !string.IsNullOrWhiteSpace(arg) && !arg.Contains(';') && !arg.Contains('|')
           && !arg.Contains('&') && !arg.Contains('`') && !arg.Contains('$')
           && !arg.StartsWith('-'); // reject option-injection (e.g. "-D", "--upload-pack=...")

    // ═══════════════════════════════════════════
    // Diff & Log
    // ═══════════════════════════════════════════

    /// <summary>git diff (unstaged changes).</summary>
    public async Task<GitResult> DiffAsync(string path = "")
    {
        string args = string.IsNullOrEmpty(path) ? "diff" : $"diff -- \"{path}\"";
        return await RunGitAsync(args);
    }

    /// <summary>git diff --staged (staged changes).</summary>
    public async Task<GitResult> DiffStagedAsync(string path = "")
    {
        string args = string.IsNullOrEmpty(path) ? "diff --staged" : $"diff --staged -- {path}";
        return await RunGitAsync(args);
    }

    /// <summary>git log with compact format.</summary>
    public async Task<GitResult> LogAsync(int count = 20, string format = "")
    {
        string fmt;
        if (string.IsNullOrEmpty(format))
        {
            fmt = "--oneline --graph --decorate";
        }
        else
        {
            // Validate format string — only allow git format placeholders, not shell metacharacters
            if (!IsValidGitArg(format) || format.Contains('"'))
                return new GitResult { Success = false, Error = "Invalid log format string." };
            fmt = $"--format=\"{format}\"";
        }
        return await RunGitAsync($"log {fmt} -n {count}");
    }

    /// <summary>git log for a specific file.</summary>
    public async Task<GitResult> LogFileAsync(string filePath, int count = 10)
    {
        if (!IsValidGitArg(filePath))
            return new GitResult { Success = false, Error = "Invalid file path characters." };
        return await RunGitAsync($"log --oneline -n {count} -- \"{filePath}\"");
    }

    /// <summary>
    /// Contents of one file as of a revision (<c>git show HEAD:path</c>) — the left-hand baseline
    /// for the side-by-side diff editor. Fails cleanly for a file that does not exist at that
    /// revision (newly added), which the caller renders as "everything is an addition".
    /// </summary>
    public async Task<GitResult> ShowFileAsync(string revision, string repoRelativePath)
    {
        if (!IsValidGitArg(revision))
            return new GitResult { Success = false, Error = "Invalid revision." };
        if (string.IsNullOrEmpty(repoRelativePath) || repoRelativePath.Contains('\0'))
            return new GitResult { Success = false, Error = "Invalid file path." };

        // Passed as ONE argv entry (ArgumentList), never through a quoted command line: the old
        // IsValidGitArg gate refused any path with '&' or '$' ("R&D/notes.md" could never be diffed)
        // and a path starting with "rev:" can't be mistaken for an option. Forward slashes because
        // git wants them in a rev:path spec even on Windows. The path is REPOSITORY-ROOT relative —
        // the form porcelain status reports — so a project opened in a subfolder still resolves.
        string spec = $"{revision}:{repoRelativePath.Replace('\\', '/')}";
        return await RunGitArgsAsync(new[] { "show", spec });
    }

    /// <summary>
    /// A file's content at a revision for the diff editor, with the same limits as opening a file
    /// in the editor: over <paramref name="maxBytes"/> or binary is reported instead of read (a
    /// 300 MB lockfile or a PNG used to be pulled into a string and handed to the diff).
    /// The size is asked first (<c>cat-file -s</c>) so a huge blob is never transferred at all.
    /// </summary>
    public async Task<GitBlobResult> ReadBlobAsync(string revision, string repoRelativePath, long maxBytes)
    {
        var blob = new GitBlobResult();
        if (!IsValidGitArg(revision) || string.IsNullOrEmpty(repoRelativePath) || repoRelativePath.Contains('\0'))
            return blob;

        string spec = $"{revision}:{repoRelativePath.Replace('\\', '/')}";
        var size = await RunGitArgsAsync(new[] { "cat-file", "-s", spec });
        if (!size.Success || !long.TryParse(size.Output.Trim(), out long bytes))
            return blob;   // not in that revision (newly added) — the caller treats it as "no baseline"

        blob.Exists = true;
        blob.Size = bytes;
        if (bytes > maxBytes) { blob.TooLarge = true; return blob; }

        var show = await RunGitArgsAsync(new[] { "show", spec });
        if (!show.Success) { blob.Exists = false; return blob; }
        string text = show.Output ?? "";
        int probe = Math.Min(text.Length, 8192);
        for (int i = 0; i < probe; i++)
            if (text[i] == '\0') { blob.Binary = true; return blob; }
        blob.Text = text;
        return blob;
    }

    /// <summary>git show for a specific commit.</summary>
    public async Task<GitResult> ShowAsync(string commitHash)
    {
        if (string.IsNullOrEmpty(commitHash) || !IsValidGitArg(commitHash))
            return new GitResult { Success = false, Error = "Invalid commit hash." };
        return await RunGitAsync($"show {commitHash} --stat");
    }

    // ═══════════════════════════════════════════
    // Remote Operations
    // ═══════════════════════════════════════════

    /// <summary>List remotes.</summary>
    public async Task<GitResult> ListRemotesAsync()
    {
        return await RunGitAsync("remote -v");
    }

    /// <summary>Add a remote.</summary>
    public async Task<GitResult> AddRemoteAsync(string name, string url)
    {
        if (!IsValidGitArg(name))
            return new GitResult { Success = false, Error = "Invalid remote name." };
        if (!IsValidGitArg(url))
            return new GitResult { Success = false, Error = "Invalid remote URL characters." };
        return await RunGitAsync($"remote add {name} \"{url}\"");
    }

    /// <summary>Get the URL of a remote.</summary>
    public async Task<GitResult> GetRemoteUrlAsync(string name = "origin")
    {
        return await RunGitAsync($"remote get-url {name}");
    }

    // ═══════════════════════════════════════════
    // Stash
    // ═══════════════════════════════════════════

    public async Task<GitResult> StashAsync(string message = "")
    {
        if (string.IsNullOrEmpty(message)) return await RunGitAsync("stash");
        // Sanitize message: strip shell-dangerous chars
        string safe = message.Replace("\"", "'").Replace("`", "").Replace("$", "").Replace(";", "").Replace("&", "").Replace("|", "");
        return await RunGitAsync($"stash push -m \"{safe}\"");
    }

    public async Task<GitResult> StashPopAsync()
    {
        return await RunGitAsync("stash pop");
    }

    public async Task<GitResult> StashListAsync()
    {
        return await RunGitAsync("stash list");
    }

    // ═══════════════════════════════════════════
    // Clone & Init
    // ═══════════════════════════════════════════

    /// <summary>Clone a repository to a target directory.</summary>
    public async Task<GitResult> CloneAsync(string repoUrl, string targetDir, CancellationToken ct = default)
    {
        // Clone doesn't use workdir - runs in the parent of targetDir
        string parentDir = Path.GetDirectoryName(targetDir) ?? targetDir;
        string folderName = Path.GetFileName(targetDir);

        Directory.CreateDirectory(parentDir);

        // `--` stops git option parsing so a URL starting with '-' (e.g. an "ext::" /
        // "--upload-pack=" arg-injection payload) can't smuggle extra flags into git.
        // Quote the URL too so an embedded space can't split it into a second positional arg.
        return await RunGitCommandAsync($"clone -- \"{repoUrl}\" \"{folderName}\"", parentDir, ct, 120000);
    }

    /// <summary>Initialize a new git repository in the working directory.</summary>
    public async Task<GitResult> InitAsync()
    {
        return await RunGitAsync("init");
    }

    // ═══════════════════════════════════════════
    // Reset & Restore
    // ═══════════════════════════════════════════

    /// <summary>git reset file (unstage).</summary>
    public async Task<GitResult> ResetFileAsync(string path)
    {
        return await RunGitAsync($"reset HEAD -- {path}");
    }

    /// <summary>git checkout/restore file (discard changes).</summary>
    public async Task<GitResult> RestoreFileAsync(string path)
    {
        return await RunGitAsync($"checkout -- {path}");
    }

    // ═══════════════════════════════════════════
    // Tags
    // ═══════════════════════════════════════════

    public async Task<GitResult> ListTagsAsync()
    {
        return await RunGitAsync("tag -l --sort=-version:refname");
    }

    public async Task<GitResult> CreateTagAsync(string name, string message = "")
    {
        if (!IsValidBranchName(name))
            return new GitResult { Success = false, Error = "Invalid tag name." };
        if (string.IsNullOrEmpty(message))
            return await RunGitAsync($"tag {name}");
        string safe = message.Replace("\"", "'").Replace("`", "").Replace("$", "").Replace(";", "").Replace("&", "").Replace("|", "");
        return await RunGitAsync($"tag -a {name} -m \"{safe}\"");
    }

    // ═══════════════════════════════════════════
    // Compact Info (for UI display)
    // ═══════════════════════════════════════════

    /// <summary>Get a compact summary: branch, status, remote info.</summary>
    public async Task<GitInfo> GetInfoAsync()
    {
        var info = new GitInfo();

        try
        {
            var branchResult = await GetCurrentBranchAsync();
            if (branchResult.Success)
                info.CurrentBranch = branchResult.Output.Trim();

            var statusResult = await RunGitAsync("status --porcelain");
            if (statusResult.Success)
            {
                var lines = statusResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                info.ModifiedFiles = lines.Count(l => l.StartsWith(" M") || l.StartsWith("M "));
                info.StagedFiles = lines.Count(l => l.StartsWith("A ") || l.StartsWith("M ") || l.StartsWith("D ") || l.StartsWith("R "));
                info.UntrackedFiles = lines.Count(l => l.StartsWith("??"));
                info.TotalChanges = lines.Length;
            }

            var remoteResult = await GetRemoteUrlAsync();
            if (remoteResult.Success)
                info.RemoteUrl = remoteResult.Output.Trim();

            info.IsGitRepo = true;
        }
        catch
        {
            info.IsGitRepo = false;
        }

        return info;
    }

    // ═══════════════════════════════════════════
    // Core Git Runner
    // ═══════════════════════════════════════════

    /// <summary>Run an arbitrary git command in the current working directory.</summary>
    public async Task<GitResult> RunGitAsync(string arguments, int timeoutMs = 30000)
    {
        if (!_fileSystem.HasWorkingDirectory)
            return new GitResult { Success = false, Error = "No working directory set." };

        return await RunGitCommandAsync(arguments, _fileSystem.WorkingDirectory, CancellationToken.None, timeoutMs);
    }

    /// <summary>
    /// Run git with an argv list (ProcessStartInfo.ArgumentList): each entry reaches git exactly as
    /// given, so paths with spaces, '&amp;', '$' or Thai characters need no quoting and can't be
    /// re-split. Prefer this for anything that carries a file path.
    /// </summary>
    public async Task<GitResult> RunGitArgsAsync(IReadOnlyList<string> args, int timeoutMs = 30000)
    {
        if (!_fileSystem.HasWorkingDirectory)
            return new GitResult { Success = false, Error = "No working directory set." };

        return await RunGitCommandAsync(psi => { foreach (var a in args) psi.ArgumentList.Add(a); },
            _fileSystem.WorkingDirectory, CancellationToken.None, timeoutMs);
    }

    /// <summary>git with an argv list in any folder (another repo of the project — <see cref="GitMultiRepo"/>).</summary>
    public static Task<GitResult> RunIn(string workingDir, IReadOnlyList<string> args, int timeoutMs = 30000, CancellationToken ct = default)
        => RunGitCommandAsync(psi => { foreach (var a in args) psi.ArgumentList.Add(a); }, workingDir, ct, timeoutMs);

    private static Task<GitResult> RunGitCommandAsync(
        string arguments, string workingDir, CancellationToken ct, int timeoutMs = 30000)
        => RunGitCommandAsync(psi => psi.Arguments = arguments, workingDir, ct, timeoutMs);

    private static async Task<GitResult> RunGitCommandAsync(
        Action<ProcessStartInfo> setArgs, string workingDir, CancellationToken ct, int timeoutMs)
    {
        var result = new GitResult();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            setArgs(psi);
            // No console here to type a username into: git must fail at once instead of waiting for the timeout.
            // (A credential manager's own sign-in window still opens — the user can answer that one.)
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var process = Process.Start(psi);
            if (process == null)
            {
                result.Error = "Failed to start git process";
                return result;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            try
            {
                // Read stdout/stderr concurrently to avoid pipe buffer deadlock.
                // If we await WaitForExitAsync first, the process may block filling
                // the pipe buffer before exit, causing a deadlock.
                var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token);
                result.Output = await stdoutTask;
                result.Error = await stderrTask;
                result.ExitCode = process.ExitCode;
                result.Success = process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                result.Error = "Git command timed out.";
                result.TimedOut = true;
            }
        }
        catch (Exception ex)
        {
            result.Error = $"Git error: {ex.Message}";
        }

        return result;
    }
}

// ─── Result Models ───

public class GitResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int ExitCode { get; set; } = -1;
    public bool TimedOut { get; set; }

    public string FullOutput => string.IsNullOrEmpty(Error)
        ? Output
        : string.IsNullOrEmpty(Output)
            ? Error
            : $"{Output}\n{Error}";
}

/// <summary>One changed path from <see cref="GitService.StatusEntriesAsync"/>.</summary>
public sealed class GitStatusEntry
{
    /// <summary>The raw two-letter porcelain code: index then worktree (" M", "A ", "??", "UU" …).</summary>
    public string XY { get; set; } = "  ";
    /// <summary>Path relative to the repository root, forward slashes, unquoted.</summary>
    public string RepoPath { get; set; } = "";
    /// <summary>Rename/copy source (repository-root relative), when there is one.</summary>
    public string? OrigRepoPath { get; set; }
    public string FullPath { get; set; } = "";

    /// <summary>The compact code the panels show: "??" for untracked, else the trimmed XY.</summary>
    public string Code
    {
        get
        {
            if (XY == "??") return "??";
            string t = XY.Trim();
            return t.Length == 0 ? "?" : t;
        }
    }
    public bool IsUntracked => XY == "??";
    public bool IsDeleted => XY.Contains('D');
}

public sealed class GitStatusSnapshot
{
    public bool Success { get; set; }
    public string Error { get; set; } = "";
    public string RepoRoot { get; set; } = "";
    public string Branch { get; set; } = "";
    public bool IsDetached { get; set; }
    /// <summary>A freshly initialised repo — the branch exists but has no commit yet.</summary>
    public bool NoCommits { get; set; }
    public List<GitStatusEntry> Entries { get; } = new();
}

public sealed class GitBlobResult
{
    public bool Exists { get; set; }
    public bool TooLarge { get; set; }
    public bool Binary { get; set; }
    public long Size { get; set; }
    public string Text { get; set; } = "";
}

public class GitInfo
{
    public bool IsGitRepo { get; set; }
    public string CurrentBranch { get; set; } = "";
    public string RemoteUrl { get; set; } = "";
    public int ModifiedFiles { get; set; }
    public int StagedFiles { get; set; }
    public int UntrackedFiles { get; set; }
    public int TotalChanges { get; set; }

    public string StatusSummary
    {
        get
        {
            if (!IsGitRepo) return "Not a git repo";
            var parts = new List<string>();
            parts.Add($"\U0001F33F {CurrentBranch}");
            if (TotalChanges > 0)
                parts.Add($"{TotalChanges} changes");
            else
                parts.Add("clean");
            return string.Join(" \u2022 ", parts);
        }
    }
}
