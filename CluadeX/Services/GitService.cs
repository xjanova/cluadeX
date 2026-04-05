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
           && BranchNameRegex.IsMatch(name) && !name.Contains("..");

    private static bool IsValidGitArg(string arg)
        => !string.IsNullOrWhiteSpace(arg) && !arg.Contains(';') && !arg.Contains('|')
           && !arg.Contains('&') && !arg.Contains('`') && !arg.Contains('$');

    // ═══════════════════════════════════════════
    // Diff & Log
    // ═══════════════════════════════════════════

    /// <summary>git diff (unstaged changes).</summary>
    public async Task<GitResult> DiffAsync(string path = "")
    {
        string args = string.IsNullOrEmpty(path) ? "diff" : $"diff -- {path}";
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

        return await RunGitCommandAsync($"clone {repoUrl} \"{folderName}\"", parentDir, ct, 120000);
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

    private static async Task<GitResult> RunGitCommandAsync(
        string arguments, string workingDir, CancellationToken ct, int timeoutMs = 30000)
    {
        var result = new GitResult();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

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
