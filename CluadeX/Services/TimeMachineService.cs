using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// High-level "Time Machine" git operations: list commits with stats,
/// fetch diffs, and perform safe rewind with auto-stash + snapshot branch.
/// Wraps the git CLI directly (does not depend on GitService) so it can
/// run parallel commands without serializing through GitService's queue.
/// </summary>
public class TimeMachineService
{
    private readonly FileSystemService _fileSystem;

    public TimeMachineService(FileSystemService fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public string WorkingDirectory => _fileSystem.WorkingDirectory;
    public bool HasWorkingDirectory => _fileSystem.HasWorkingDirectory;

    // ═══════════════════════════════════════════
    // Repository state
    // ═══════════════════════════════════════════

    public async Task<bool> IsGitRepoAsync(CancellationToken ct = default)
    {
        if (!HasWorkingDirectory) return false;
        var r = await RunGitAsync("rev-parse --is-inside-work-tree", ct);
        return r.ExitCode == 0 && r.StdOut.Trim() == "true";
    }

    public async Task<string> GetHeadShaAsync(CancellationToken ct = default)
    {
        var r = await RunGitAsync("rev-parse HEAD", ct);
        return r.ExitCode == 0 ? r.StdOut.Trim() : "";
    }

    public async Task<string> GetCurrentBranchAsync(CancellationToken ct = default)
    {
        var r = await RunGitAsync("branch --show-current", ct);
        return r.ExitCode == 0 ? r.StdOut.Trim() : "";
    }

    public async Task<bool> HasUncommittedChangesAsync(CancellationToken ct = default)
    {
        var r = await RunGitAsync("status --porcelain", ct);
        return r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.StdOut);
    }

    // ═══════════════════════════════════════════
    // Commit listing
    // ═══════════════════════════════════════════

    private const string CommitFormat = "%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%D%x1f%s%x1f%b%x1e";
    // %H=sha, %P=parents, %an=author, %ae=email, %aI=iso date, %D=refs, %s=subject, %b=body
    // %x1f = unit separator, %x1e = record separator

    /// <summary>
    /// List recent commits with subject, author, refs, and aggregated +/- counts.
    /// Uses two git invocations (log + numstat) to keep output parseable.
    /// </summary>
    public async Task<List<CommitInfo>> ListCommitsAsync(int maxCount = 200, CancellationToken ct = default)
    {
        var result = new List<CommitInfo>();
        if (!await IsGitRepoAsync(ct)) return result;

        // 1) commit metadata
        var meta = await RunGitAsync($"log -n {maxCount} --pretty=format:\"{CommitFormat}\"", ct);
        if (meta.ExitCode != 0) return result;

        string headSha = await GetHeadShaAsync(ct);

        var records = meta.StdOut.Split('\x1e', StringSplitOptions.RemoveEmptyEntries);
        foreach (var rec in records)
        {
            var parts = rec.Trim('\r', '\n').Split('\x1f');
            if (parts.Length < 7) continue;
            var info = new CommitInfo
            {
                Sha = parts[0].Trim(),
                Author = parts[2],
                AuthorEmail = parts[3],
                Message = parts[6].Trim(),
            };
            if (DateTimeOffset.TryParse(parts[4], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var dt))
                info.Date = dt;

            // Parse refs (HEAD -> main, origin/main, tag: v1.0)
            var refs = parts[5];
            if (!string.IsNullOrWhiteSpace(refs))
            {
                foreach (var r in refs.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var rt = r.Trim();
                    if (rt.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
                        info.Tags.Add(rt[4..].Trim());
                    else
                        info.Refs.Add(rt);
                }
            }

            // Detect AI authorship — Co-Authored-By Claude in body, or specific patterns
            string body = parts.Length > 7 ? parts[7] : "";
            string combined = (info.Message + "\n" + body + "\n" + info.Author + "\n" + info.AuthorEmail).ToLowerInvariant();
            info.IsAiAuthored = combined.Contains("claude") ||
                                combined.Contains("co-authored-by: claude") ||
                                combined.Contains("anthropic") ||
                                combined.Contains("co-authored-by: anthropic") ||
                                (info.AuthorEmail.Contains("noreply@anthropic", StringComparison.OrdinalIgnoreCase));

            info.IsHead = info.Sha == headSha;
            if (info.Refs.Count > 0)
            {
                // Branch hint = first non-HEAD ref
                var firstBranch = info.Refs.FirstOrDefault(r => !r.StartsWith("HEAD"));
                if (firstBranch != null) info.Branch = firstBranch;
            }

            result.Add(info);
        }

        // 2) numstat — get +/- per commit (in same order as log)
        var stat = await RunGitAsync($"log -n {maxCount} --numstat --pretty=format:\"COMMIT %H\"", ct);
        if (stat.ExitCode == 0)
        {
            CommitInfo? current = null;
            int adds = 0, dels = 0, files = 0;
            foreach (var raw in stat.StdOut.Split('\n'))
            {
                var line = raw.Trim('\r', '\t', ' ');
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("COMMIT "))
                {
                    if (current != null)
                    {
                        current.Additions = adds;
                        current.Deletions = dels;
                        current.FilesChanged = files;
                    }
                    string sha = line[7..].Trim();
                    current = result.FirstOrDefault(c => c.Sha == sha);
                    adds = dels = files = 0;
                }
                else if (current != null)
                {
                    // numstat: "additions\tdeletions\tpath" — binary shows "-\t-\t..."
                    var ns = line.Split('\t');
                    if (ns.Length >= 3)
                    {
                        files++;
                        if (int.TryParse(ns[0], out int a)) adds += a;
                        if (int.TryParse(ns[1], out int d)) dels += d;
                    }
                }
            }
            if (current != null)
            {
                current.Additions = adds;
                current.Deletions = dels;
                current.FilesChanged = files;
            }
        }

        return result;
    }

    // ═══════════════════════════════════════════
    // Diff
    // ═══════════════════════════════════════════

    /// <summary>List files changed in a single commit.</summary>
    public async Task<List<CommitFileChange>> GetCommitFilesAsync(string sha, CancellationToken ct = default)
    {
        var list = new List<CommitFileChange>();
        if (!IsValidSha(sha)) return list;

        // --numstat gives +/-; --name-status gives A/M/D/R; combine both
        var r = await RunGitAsync($"show {sha} --numstat --format=", ct);
        if (r.ExitCode != 0) return list;

        var statuses = new Dictionary<string, char>();
        var rs = await RunGitAsync($"show {sha} --name-status --format=", ct);
        if (rs.ExitCode == 0)
        {
            foreach (var raw in rs.StdOut.Split('\n'))
            {
                var line = raw.Trim('\r');
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length >= 2 && parts[0].Length > 0)
                    statuses[parts[parts.Length - 1]] = parts[0][0];
            }
        }

        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.Trim('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            var change = new CommitFileChange { Path = parts[2] };
            if (int.TryParse(parts[0], out int a)) change.Additions = a;
            if (int.TryParse(parts[1], out int d)) change.Deletions = d;
            if (statuses.TryGetValue(change.Path, out char s)) change.Status = s;
            list.Add(change);
        }
        return list;
    }

    /// <summary>Get the unified diff of a single file in a commit, parsed line-by-line.</summary>
    public async Task<List<DiffLine>> GetFileDiffAsync(string sha, string filePath, CancellationToken ct = default)
    {
        var lines = new List<DiffLine>();
        if (!IsValidSha(sha)) return lines;
        if (string.IsNullOrWhiteSpace(filePath)) return lines;

        // Use show with -- to safely pass file path
        var r = await RunGitAsync($"show {sha} -- \"{EscapePath(filePath)}\"", ct);
        if (r.ExitCode != 0) return lines;

        int oldNo = 0, newNo = 0;
        bool inHunk = false;
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@"))
            {
                inHunk = true;
                // Parse @@ -OLD,COUNT +NEW,COUNT @@
                var m = Regex.Match(line, @"@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");
                if (m.Success)
                {
                    oldNo = int.Parse(m.Groups[1].Value);
                    newNo = int.Parse(m.Groups[2].Value);
                }
                lines.Add(new DiffLine { Kind = '@', Content = line });
                continue;
            }
            if (!inHunk) continue;
            if (line.StartsWith("+++") || line.StartsWith("---")) continue;

            if (line.StartsWith("+"))
            {
                lines.Add(new DiffLine { Kind = '+', Content = line[1..], NewLineNo = newNo++ });
            }
            else if (line.StartsWith("-"))
            {
                lines.Add(new DiffLine { Kind = '-', Content = line[1..], OldLineNo = oldNo++ });
            }
            else if (line.StartsWith(" "))
            {
                lines.Add(new DiffLine { Kind = ' ', Content = line[1..], OldLineNo = oldNo++, NewLineNo = newNo++ });
            }
        }
        return lines;
    }

    // ═══════════════════════════════════════════
    // Mutations — REWIND, BRANCH, CHERRY-PICK
    // ═══════════════════════════════════════════

    /// <summary>
    /// Rewind the working tree + HEAD to a specific commit.
    /// Safety: optionally creates a snapshot branch first, and ALWAYS
    /// stashes any uncommitted changes (with a recognisable message)
    /// before resetting hard.
    /// </summary>
    public async Task<TimeMachineActionResult> RewindToCommitAsync(
        string sha, bool createSnapshot = true, CancellationToken ct = default)
    {
        var result = new TimeMachineActionResult();
        if (!IsValidSha(sha))
        {
            result.Message = "Invalid commit SHA.";
            return result;
        }
        if (!await IsGitRepoAsync(ct))
        {
            result.Message = "Not a git repository.";
            return result;
        }

        // 1) auto-stash if dirty
        if (await HasUncommittedChangesAsync(ct))
        {
            string stashMsg = $"wip/before-rewind-to-{sha[..Math.Min(7, sha.Length)]}-{DateTime.Now:yyyyMMdd-HHmmss}";
            var stash = await RunGitAsync($"stash push --include-untracked -m \"{EscapeArg(stashMsg)}\"", ct);
            if (stash.ExitCode != 0)
            {
                result.Message = $"Auto-stash failed: {stash.StdErr}";
                return result;
            }
            result.StashRef = stashMsg;
        }

        // 2) optional snapshot branch from current HEAD
        if (createSnapshot)
        {
            string currentBranch = await GetCurrentBranchAsync(ct);
            string snap = $"snapshot/{(string.IsNullOrEmpty(currentBranch) ? "detached" : currentBranch)}-{DateTime.Now:yyyyMMdd-HHmmss}";
            // sanitize
            snap = SanitizeBranchName(snap);
            var br = await RunGitAsync($"branch {snap}", ct);
            if (br.ExitCode == 0) result.SnapshotBranch = snap;
            // best-effort — failure shouldn't block the rewind
        }

        // 3) reset --hard to target
        var reset = await RunGitAsync($"reset --hard {sha}", ct);
        if (reset.ExitCode != 0)
        {
            result.Message = $"Reset failed: {reset.StdErr}";
            return result;
        }

        result.Success = true;
        result.Message = $"Rewound to {sha[..Math.Min(7, sha.Length)]}";
        if (result.SnapshotBranch != null) result.Message += $" · snapshot at {result.SnapshotBranch}";
        if (result.StashRef != null) result.Message += $" · stash: {result.StashRef}";
        return result;
    }

    /// <summary>Create a new branch starting at a specific commit and switch to it.</summary>
    public async Task<TimeMachineActionResult> BranchFromCommitAsync(
        string sha, string newBranch, CancellationToken ct = default)
    {
        var result = new TimeMachineActionResult();
        if (!IsValidSha(sha))
        {
            result.Message = "Invalid commit SHA.";
            return result;
        }
        newBranch = SanitizeBranchName(newBranch);
        if (string.IsNullOrEmpty(newBranch))
        {
            result.Message = "Invalid branch name.";
            return result;
        }

        if (await HasUncommittedChangesAsync(ct))
        {
            result.Message = "You have uncommitted changes. Stash or commit first.";
            return result;
        }

        var r = await RunGitAsync($"checkout -b {newBranch} {sha}", ct);
        if (r.ExitCode != 0)
        {
            result.Message = $"checkout -b failed: {r.StdErr}";
            return result;
        }
        result.Success = true;
        result.Message = $"Branched to {newBranch} at {sha[..Math.Min(7, sha.Length)]}";
        return result;
    }

    /// <summary>Apply the changes of a commit on top of the current branch.</summary>
    public async Task<TimeMachineActionResult> CherryPickCommitAsync(string sha, CancellationToken ct = default)
    {
        var result = new TimeMachineActionResult();
        if (!IsValidSha(sha))
        {
            result.Message = "Invalid commit SHA.";
            return result;
        }
        if (await HasUncommittedChangesAsync(ct))
        {
            result.Message = "You have uncommitted changes. Stash or commit first.";
            return result;
        }
        var r = await RunGitAsync($"cherry-pick {sha}", ct);
        if (r.ExitCode != 0)
        {
            // Abort half-applied cherry-pick to avoid leaving repo in a weird state
            await RunGitAsync("cherry-pick --abort", ct);
            result.Message = $"cherry-pick failed: {r.StdErr.Trim()}";
            return result;
        }
        result.Success = true;
        result.Message = $"Cherry-picked {sha[..Math.Min(7, sha.Length)]}";
        return result;
    }

    // ═══════════════════════════════════════════
    // git process plumbing
    // ═══════════════════════════════════════════

    private record GitOutput(int ExitCode, string StdOut, string StdErr);

    private async Task<GitOutput> RunGitAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = HasWorkingDirectory ? WorkingDirectory : Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return new GitOutput(-1, "", "git failed to start");

            // Concurrent reads — avoid the classic dead-lock when both streams fill
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            return new GitOutput(proc.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            return new GitOutput(-1, "", "cancelled");
        }
        catch (Exception ex)
        {
            return new GitOutput(-1, "", ex.Message);
        }
    }

    // ═══════════════════════════════════════════
    // Validation helpers
    // ═══════════════════════════════════════════

    private static readonly Regex ShaRegex = new(@"^[a-f0-9]{4,40}$", RegexOptions.Compiled);
    private static readonly Regex BranchRegex = new(@"^[a-zA-Z0-9_\-/.]+$", RegexOptions.Compiled);

    private static bool IsValidSha(string sha) => !string.IsNullOrWhiteSpace(sha) && ShaRegex.IsMatch(sha);

    private static string SanitizeBranchName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        // Allow only safe characters
        var cleaned = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '/' || ch == '.')
                cleaned.Append(ch);
        }
        var result = cleaned.ToString();
        if (result.Contains("..")) result = result.Replace("..", "");
        if (result.Length > 255) result = result[..255];
        return result;
    }

    private static string EscapePath(string path) =>
        path.Replace("\"", "").Replace("`", "").Replace("$", "").Replace(";", "").Replace("&", "").Replace("|", "");

    private static string EscapeArg(string arg) =>
        arg.Replace("\"", "'").Replace("`", "").Replace("$", "").Replace(";", "").Replace("&", "").Replace("|", "");
}
