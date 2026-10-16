using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// High-level "Time Machine" git operations: list commits with stats,
/// fetch diffs, and perform safe rewind with auto-stash + snapshot branch.
/// Wraps the git CLI directly (does not depend on GitService) so it can
/// run parallel commands without serializing through GitService's queue.
///
/// Every git call passes its arguments as a list (no hand-built command line) and runs with
/// core.quotepath=false: git used to print a Thai path as "\340\270\201…" and a rename as
/// "src/{a => b}.cs", neither of which is a real path, so the diff for those files came back empty.
/// </summary>
public class TimeMachineService
{
    private readonly FileSystemService _fileSystem;
    private readonly LocalizationService? _loc;
    private readonly DebugLogService? _log;

    /// <summary>(sha, new path) → old path, for renames seen by <see cref="GetCommitFilesAsync"/>.</summary>
    private readonly ConcurrentDictionary<(string Sha, string Path), string> _renameSources = new();

    public TimeMachineService(FileSystemService fileSystem, LocalizationService? loc = null, DebugLogService? log = null)
    {
        _fileSystem = fileSystem;
        _loc = loc;
        _log = log;
    }

    /// <summary>The project the Time Machine page is looking at — any project ever worked on, not only the open one
    /// (owner 2026-10-16). Null = the chat's open folder.</summary>
    public string? Project { get; set; }

    public string WorkingDirectory => Project ?? _fileSystem.WorkingDirectory;
    public bool HasWorkingDirectory => Project != null ? Directory.Exists(Project) : _fileSystem.HasWorkingDirectory;

    private string T(string key, params object[] args)
    {
        if (_loc == null) return args.Length == 0 ? key : $"{key} {string.Join(" ", args)}";
        return args.Length == 0 ? _loc.T(key) : _loc.T(key, args);
    }

    // ═══════════════════════════════════════════
    // Repository state
    // ═══════════════════════════════════════════

    public async Task<bool> IsGitRepoAsync(CancellationToken ct = default)
    {
        if (!HasWorkingDirectory) return false;
        var r = await RunGitAsync(ct, "rev-parse", "--is-inside-work-tree");
        return r.ExitCode == 0 && r.StdOut.Trim() == "true";
    }

    public async Task<string> GetHeadShaAsync(CancellationToken ct = default)
    {
        var r = await RunGitAsync(ct, "rev-parse", "HEAD");
        return r.ExitCode == 0 ? r.StdOut.Trim() : "";
    }

    public async Task<string> GetCurrentBranchAsync(CancellationToken ct = default)
    {
        var r = await RunGitAsync(ct, "branch", "--show-current");
        return r.ExitCode == 0 ? r.StdOut.Trim() : "";
    }

    public async Task<bool> HasUncommittedChangesAsync(CancellationToken ct = default)
    {
        var r = await RunGitAsync(ct, "status", "--porcelain");
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
        var meta = await RunGitAsync(ct, "log", "-n", maxCount.ToString(CultureInfo.InvariantCulture), $"--pretty=format:{CommitFormat}");
        if (meta.ExitCode != 0)
        {
            _log?.Warn("TimeMachine", $"git log failed: {meta.StdErr.Trim()}");
            return result;
        }

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
        var bySha = result.GroupBy(c => c.Sha).ToDictionary(g => g.Key, g => g.First());
        var stat = await RunGitAsync(ct, "log", "-n", maxCount.ToString(CultureInfo.InvariantCulture), "--numstat", "--pretty=format:COMMIT %H");
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
                    bySha.TryGetValue(line[7..].Trim(), out current);
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

    /// <summary>List files changed in a single commit (renames under their new path).</summary>
    public async Task<List<CommitFileChange>> GetCommitFilesAsync(string sha, CancellationToken ct = default)
    {
        var list = new List<CommitFileChange>();
        if (!IsValidSha(sha)) return list;

        // -z: NUL-separated, paths verbatim (no C-quoting, no "{a => b}" rename shorthand).
        var r = await RunGitAsync(ct, "show", sha, "--numstat", "-z", "-M", "--format=");
        if (r.ExitCode != 0)
        {
            _log?.Warn("TimeMachine", $"git show --numstat {sha} failed: {r.StdErr.Trim()}");
            return list;
        }
        var rs = await RunGitAsync(ct, "show", sha, "--name-status", "-z", "-M", "--format=");
        var statuses = rs.ExitCode == 0 ? ParseNameStatusZ(rs.StdOut) : new Dictionary<string, (char Status, string? OldPath)>();

        foreach (var (path, oldPath, adds, dels) in ParseNumstatZ(r.StdOut))
        {
            var change = new CommitFileChange { Path = path, Additions = adds, Deletions = dels };
            if (statuses.TryGetValue(path, out var st)) change.Status = st.Status;
            else if (oldPath != null) change.Status = 'R';
            string? from = oldPath ?? (statuses.TryGetValue(path, out var s2) ? s2.OldPath : null);
            if (from != null) _renameSources[(sha, path)] = from;
            list.Add(change);
        }
        return list;
    }

    /// <summary>
    /// Parse <c>git show --numstat -z</c>: "A\tD\tpath\0" per file, or for a rename "A\tD\t\0old\0new\0".
    /// Public static for the harness.
    /// </summary>
    public static List<(string Path, string? OldPath, int Additions, int Deletions)> ParseNumstatZ(string output)
    {
        var result = new List<(string, string?, int, int)>();
        var tokens = output.Split('\0');
        for (int i = 0; i < tokens.Length; i++)
        {
            string tok = tokens[i].TrimStart('\r', '\n');
            if (tok.Length == 0) continue;
            var parts = tok.Split('\t');
            if (parts.Length < 3) continue;
            int.TryParse(parts[0], out int a);
            int.TryParse(parts[1], out int d);
            if (parts[2].Length > 0)
            {
                result.Add((parts[2], null, a, d));
            }
            else if (i + 2 < tokens.Length)
            {
                // rename / copy: the next two NUL-separated tokens are old and new path
                string oldPath = tokens[++i];
                string newPath = tokens[++i];
                result.Add((newPath, oldPath, a, d));
            }
        }
        return result;
    }

    /// <summary>Parse <c>git show --name-status -z</c>: "M\0path\0", or "R100\0old\0new\0" for renames/copies.</summary>
    public static Dictionary<string, (char Status, string? OldPath)> ParseNameStatusZ(string output)
    {
        var map = new Dictionary<string, (char, string?)>(StringComparer.Ordinal);
        var tokens = output.Split('\0');
        for (int i = 0; i < tokens.Length; i++)
        {
            string code = tokens[i].Trim('\r', '\n');
            if (code.Length == 0) continue;
            char kind = code[0];
            if ((kind == 'R' || kind == 'C') && i + 2 < tokens.Length)
            {
                string oldPath = tokens[++i];
                string newPath = tokens[++i];
                map[newPath] = (kind, oldPath);
            }
            else if (i + 1 < tokens.Length)
            {
                map[tokens[++i]] = (kind, null);
            }
        }
        return map;
    }

    /// <summary>Get the unified diff of a single file in a commit, parsed line-by-line.</summary>
    public async Task<List<DiffLine>> GetFileDiffAsync(string sha, string filePath, CancellationToken ct = default)
    {
        var lines = new List<DiffLine>();
        if (!IsValidSha(sha)) return lines;
        if (string.IsNullOrWhiteSpace(filePath)) return lines;

        // The path goes after "--" as its own argument: no quoting, no character stripping (the old
        // EscapePath removed & $ ; | from the name, so such files never showed a diff). A renamed file is
        // shown together with its old path so git renders the rename instead of a whole-file add.
        var args = new List<string> { "show", sha, "-M", "--format=", "--" };
        if (_renameSources.TryGetValue((sha, filePath), out var oldPath)) args.Add(oldPath);
        args.Add(filePath);
        var r = await RunGitAsync(ct, args.ToArray());
        if (r.ExitCode != 0)
        {
            _log?.Warn("TimeMachine", $"git show {sha} -- {filePath} failed: {r.StdErr.Trim()}");
            return lines;
        }

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
            if (line.StartsWith("diff --git")) { inHunk = false; continue; }
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

    private static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Rewind the working tree + HEAD to a specific commit.
    /// Safety: optionally creates a snapshot branch FIRST — if that fails nothing else happens (it used to be
    /// "best-effort", and reset --hard ran anyway with no way back) — then stashes any uncommitted changes
    /// (with a recognisable message) before resetting hard.
    /// </summary>
    public async Task<TimeMachineActionResult> RewindToCommitAsync(
        string sha, bool createSnapshot = true, CancellationToken ct = default)
    {
        var result = new TimeMachineActionResult();
        if (!IsValidSha(sha))
        {
            result.Message = T("tm.invalidSha");
            return result;
        }
        if (!await IsGitRepoAsync(ct))
        {
            result.Message = T("tm.notRepo");
            return result;
        }
        string shortSha = sha[..Math.Min(7, sha.Length)];

        // 1) snapshot branch from current HEAD — before anything is changed
        if (createSnapshot)
        {
            string currentBranch = await GetCurrentBranchAsync(ct);
            string snap = SanitizeBranchName($"snapshot/{(string.IsNullOrEmpty(currentBranch) ? "detached" : currentBranch)}-{Stamp()}");
            var br = await RunGitAsync(ct, "branch", snap);
            if (br.ExitCode != 0)
            {
                _log?.Error("TimeMachine", $"git branch {snap} failed: {br.StdErr.Trim()}");
                result.Message = T("tm.snapshotFailed");
                return result;
            }
            result.SnapshotBranch = snap;
        }

        // 2) auto-stash if dirty
        if (await HasUncommittedChangesAsync(ct))
        {
            string stashMsg = $"wip/before-rewind-to-{shortSha}-{Stamp()}";
            var stash = await RunGitAsync(ct, "stash", "push", "--include-untracked", "-m", stashMsg);
            if (stash.ExitCode != 0)
            {
                _log?.Error("TimeMachine", $"git stash failed: {stash.StdErr.Trim()}");
                result.Message = T("tm.stashFailed");
                if (result.SnapshotBranch != null) result.Message += T("tm.snapshotKept", result.SnapshotBranch);
                return result;
            }
            result.StashRef = stashMsg;
        }

        // 3) reset --hard to target
        var reset = await RunGitAsync(ct, "reset", "--hard", sha);
        if (reset.ExitCode != 0)
        {
            _log?.Error("TimeMachine", $"git reset --hard {sha} failed: {reset.StdErr.Trim()}");
            result.Message = T("tm.resetFailed");
            if (result.SnapshotBranch != null) result.Message += T("tm.snapshotKept", result.SnapshotBranch);
            if (result.StashRef != null) result.Message += T("tm.stashKept", result.StashRef);
            return result;
        }

        result.Success = true;
        result.Message = T("tm.rewound", shortSha);
        if (result.SnapshotBranch != null) result.Message += T("tm.snapshotAt", result.SnapshotBranch);
        if (result.StashRef != null) result.Message += T("tm.stashAt", result.StashRef);
        return result;
    }

    /// <summary>Create a new branch starting at a specific commit and switch to it.</summary>
    public async Task<TimeMachineActionResult> BranchFromCommitAsync(
        string sha, string newBranch, CancellationToken ct = default)
    {
        var result = new TimeMachineActionResult();
        if (!IsValidSha(sha))
        {
            result.Message = T("tm.invalidSha");
            return result;
        }
        newBranch = SanitizeBranchName(newBranch);
        if (string.IsNullOrEmpty(newBranch))
        {
            result.Message = T("tm.invalidBranch");
            return result;
        }

        if (await HasUncommittedChangesAsync(ct))
        {
            result.Message = T("tm.dirty");
            return result;
        }

        var r = await RunGitAsync(ct, "checkout", "-b", newBranch, sha);
        if (r.ExitCode != 0)
        {
            _log?.Error("TimeMachine", $"git checkout -b {newBranch} {sha} failed: {r.StdErr.Trim()}");
            result.Message = T("tm.branchFailed", newBranch);
            return result;
        }
        result.Success = true;
        result.Message = T("tm.branched", newBranch, sha[..Math.Min(7, sha.Length)]);
        return result;
    }

    /// <summary>Apply the changes of a commit on top of the current branch.</summary>
    public async Task<TimeMachineActionResult> CherryPickCommitAsync(string sha, CancellationToken ct = default)
    {
        var result = new TimeMachineActionResult();
        if (!IsValidSha(sha))
        {
            result.Message = T("tm.invalidSha");
            return result;
        }
        if (await HasUncommittedChangesAsync(ct))
        {
            result.Message = T("tm.dirty");
            return result;
        }
        var r = await RunGitAsync(ct, "cherry-pick", sha);
        if (r.ExitCode != 0)
        {
            // Abort half-applied cherry-pick to avoid leaving repo in a weird state
            await RunGitAsync(ct, "cherry-pick", "--abort");
            _log?.Error("TimeMachine", $"git cherry-pick {sha} failed: {r.StdErr.Trim()}");
            bool conflict = r.StdErr.Contains("conflict", StringComparison.OrdinalIgnoreCase)
                            || r.StdOut.Contains("CONFLICT", StringComparison.Ordinal);
            result.Message = conflict ? T("tm.cherryConflict", sha[..Math.Min(7, sha.Length)]) : T("tm.cherryFailed");
            return result;
        }
        result.Success = true;
        result.Message = T("tm.cherryPicked", sha[..Math.Min(7, sha.Length)]);
        return result;
    }

    // ═══════════════════════════════════════════
    // git process plumbing
    // ═══════════════════════════════════════════

    private record GitOutput(int ExitCode, string StdOut, string StdErr);

    private async Task<GitOutput> RunGitAsync(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = HasWorkingDirectory ? WorkingDirectory : Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("i18n.logOutputEncoding=UTF-8");
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
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
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            return new GitOutput(-1, "", ex.Message);
        }
        finally
        {
            proc?.Dispose();
        }
    }

    // ═══════════════════════════════════════════
    // Validation helpers
    // ═══════════════════════════════════════════

    private static readonly Regex ShaRegex = new(@"^[a-f0-9]{4,40}$", RegexOptions.Compiled);

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
}
