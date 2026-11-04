using System.IO;

namespace CluadeX.Services;

/// <summary>What one row of the Source Control panel is about (drives the coloured badge and its tooltip).</summary>
public enum GitChangeKind { Modified, Added, Deleted, Renamed, Untracked, Conflict, Unknown }

/// <summary>One row of the Source Control panel: an entry seen from the index ("Staged") or the working tree ("Changes").
/// A file that is staged AND edited again ("MM") is two rows, exactly as git sees it. Pure data.</summary>
public sealed record GitPanelRow(GitStatusEntry Entry, bool IsStaged, GitChangeKind Kind, string Letter);

/// <summary>Why a git operation of the panel failed, so the message can be localized instead of git's raw English.</summary>
public enum GitFailure { None, TimedOut, Rejected, Protected, Auth, Network, NotFastForward, LocalChanges, Conflict, NoUpstream, Other }

/// <summary>The newest commit of the branch, for the "working tree clean" state.</summary>
public sealed record GitLastCommit(string ShortHash, string Subject, DateTimeOffset When);

/// <summary>
/// The Source Control panel's git work (owner 2026-11-04: "ทำให้ชัดเจนขึ้น เครื่องมือมากขึ้นหน่อย สมเป็นโปรแกรมชั้นเลิศ").
/// Every call here takes the REPOSITORY ROOT it acts on, captured when the user clicked: a project switch while a pull
/// runs can't redirect the second half of it to the new project. Every path goes to git as its own argv entry
/// (ArgumentList) behind <c>--literal-pathspecs</c> and <c>--</c>: a file called "-rf", "*.cs" or "a[1].txt" means
/// exactly that file. Nothing here force-pushes, resets --hard or cleans; discarding a tracked file is
/// <c>git restore --worktree</c> of the listed paths.
/// </summary>
public partial class GitService
{
    /// <summary>Line 1 of a commit message past this many characters gets a hint (git's own convention, wraps in
    /// `git log --oneline`, GitHub cuts it).</summary>
    public const int CommitSubjectSoftLimit = 72;

    /// <summary>Length of the subject (line 1) of a commit message. Pure.</summary>
    public static int CommitSubjectLength(string? message)
    {
        if (string.IsNullOrEmpty(message)) return 0;
        int nl = message.IndexOf('\n');
        string first = nl >= 0 ? message[..nl] : message;
        return first.TrimEnd('\r', ' ', '\t').Length;
    }

    /// <summary>
    /// Split porcelain entries into the two groups the panel shows, the way git sees them: index column (X) →
    /// "Staged", working-tree column (Y) → "Changes". Untracked files and conflicts are Changes (a conflict is resolved
    /// by staging it). Pure.
    /// </summary>
    public static (List<GitPanelRow> Staged, List<GitPanelRow> Changes) GroupChanges(IEnumerable<GitStatusEntry> entries)
    {
        var staged = new List<GitPanelRow>();
        var changes = new List<GitPanelRow>();
        foreach (var e in entries)
        {
            string xy = (e.XY ?? "  ").PadRight(2);
            if (e.IsUntracked) { changes.Add(new GitPanelRow(e, false, GitChangeKind.Untracked, "U")); continue; }
            if (e.IsConflict) { changes.Add(new GitPanelRow(e, false, GitChangeKind.Conflict, "!")); continue; }
            if (xy[0] is not ' ' and not '?' and not '!') staged.Add(new GitPanelRow(e, true, KindOf(xy[0]), LetterOf(xy[0])));
            if (xy[1] is not ' ' and not '?' and not '!') changes.Add(new GitPanelRow(e, false, KindOf(xy[1]), LetterOf(xy[1])));
        }
        return (staged, changes);
    }

    private static GitChangeKind KindOf(char c) => c switch
    {
        'M' or 'T' => GitChangeKind.Modified,
        'A' => GitChangeKind.Added,
        'D' => GitChangeKind.Deleted,
        'R' or 'C' => GitChangeKind.Renamed,
        _ => GitChangeKind.Unknown,
    };

    private static string LetterOf(char c) => c is 'M' or 'A' or 'D' or 'R' or 'C' or 'T' ? c.ToString() : "?";

    /// <summary>
    /// The absolute path of a repository-relative path, or null when it would land outside the repository or inside
    /// its .git folder. Porcelain paths are git's own output, but they are still checked before anything is deleted:
    /// "../x" or an absolute path must never reach a delete. Pure.
    /// </summary>
    public static string? SafeRepoPath(string repoRoot, string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(repoPath) || repoPath.Contains('\0')) return null;
        if (Path.IsPathRooted(repoPath)) return null;
        try
        {
            string root = Path.GetFullPath(repoRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, repoPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            string rel = full[root.Length..];
            if (rel.Length == 0) return null;
            string firstPart = rel.Split(Path.DirectorySeparatorChar)[0];
            if (firstPart.Equals(".git", StringComparison.OrdinalIgnoreCase)) return null;
            return full;
        }
        catch { return null; }
    }

    // ═══════════════════════════════════════════
    // Argument builders (pure — the harness checks them)
    // ═══════════════════════════════════════════

    public static class PanelArgs
    {
        /// <summary>Paths per git call stay under this many characters: Windows' command line ends at 32 767, and
        /// "Stage all" on a big checkout can name thousands of files.</summary>
        public const int MaxPathChars = 6000;

        public static IReadOnlyList<string> Fetch() => new[] { "fetch", "--all" };
        /// <summary>Fast-forward only: a pull never creates a merge commit or stops in a conflict behind the user's back.</summary>
        public static IReadOnlyList<string> PullFastForward() => new[] { "pull", "--ff-only" };
        public static IReadOnlyList<string> CommitFromFile(string messageFile) => new[] { "commit", "-F", messageFile };
        public static IReadOnlyList<string> StashPush(string message) => new[] { "stash", "push", "-m", message };
        public static IReadOnlyList<string> StashPop() => new[] { "stash", "pop" };
        public static IReadOnlyList<string> StashList() => new[] { "stash", "list" };
        public static IReadOnlyList<string> CheckRefFormat(string branch) => new[] { "check-ref-format", "--branch", branch };
        public static IReadOnlyList<string> CreateBranch(string branch) => new[] { "checkout", "-b", branch };
        public static IReadOnlyList<string> LastCommit() => new[] { "log", "-1", "--format=%h%x1f%s%x1f%ct" };
        /// <summary>The files inside an untracked folder (porcelain shows "?? dir/"), so a discard lists — and deletes —
        /// files one by one instead of a whole folder.</summary>
        public static IReadOnlyList<string> UntrackedUnder(string dir) =>
            new[] { "--literal-pathspecs", "-c", "core.quotepath=off", "ls-files", "--others", "--exclude-standard", "-z", "--", dir };

        public static IReadOnlyList<IReadOnlyList<string>> Stage(IEnumerable<string> paths) => Chunk(new[] { "add" }, paths);
        /// <summary>Take paths out of the index, keeping the working tree. In a repo with no commit yet there is no HEAD
        /// to restore from, so it is <c>rm --cached</c> (the file stays on disk).</summary>
        public static IReadOnlyList<IReadOnlyList<string>> Unstage(IEnumerable<string> paths, bool noCommits) =>
            Chunk(noCommits ? new[] { "rm", "--cached", "-q" } : new[] { "restore", "--staged" }, paths);
        /// <summary>Throw away working-tree edits of TRACKED files: back to the staged / committed version.</summary>
        public static IReadOnlyList<IReadOnlyList<string>> DiscardTracked(IEnumerable<string> paths) =>
            Chunk(new[] { "restore", "--worktree" }, paths);
        public static IReadOnlyList<IReadOnlyList<string>> DiffForDraft(bool staged, IEnumerable<string> paths) =>
            Chunk(staged ? new[] { "diff", "--cached", "--no-color", "--no-ext-diff" } : new[] { "diff", "HEAD", "--no-color", "--no-ext-diff" }, paths);

        /// <summary>"git --literal-pathspecs &lt;verb...&gt; -- p1 p2 …", split so no call's paths pass <see cref="MaxPathChars"/>.
        /// Empty / NUL-carrying paths are dropped; duplicates are sent once.</summary>
        public static IReadOnlyList<IReadOnlyList<string>> Chunk(IReadOnlyList<string> verb, IEnumerable<string> paths)
        {
            var calls = new List<IReadOnlyList<string>>();
            List<string>? current = null;
            int chars = 0;
            foreach (var p in paths.Where(p => !string.IsNullOrEmpty(p) && !p.Contains('\0')).Distinct(StringComparer.Ordinal))
            {
                if (current == null || chars + p.Length + 3 > MaxPathChars)
                {
                    if (current != null) calls.Add(current);
                    current = new List<string> { "--literal-pathspecs" };
                    current.AddRange(verb);
                    current.Add("--");
                    chars = 0;
                }
                current.Add(p);
                chars += p.Length + 3;
            }
            if (current != null) calls.Add(current);
            return calls;
        }
    }

    // ═══════════════════════════════════════════
    // Calls (each in the repository it was given)
    // ═══════════════════════════════════════════

    /// <summary>Is git on PATH at all? Asked once per run — every refresh used to be a silent "not a repository" when
    /// git simply wasn't installed.</summary>
    public static async Task<bool> IsGitAvailableAsync()
    {
        if (_gitAvailable is bool known) return known;
        var r = await RunIn(Path.GetTempPath(), new[] { "--version" }, 10000);
        // Only a definite answer is remembered: a timeout on a busy machine must not label git "missing" for good.
        // A process that could not start at all comes back with ExitCode -1 and no timeout.
        if (r.Success) _gitAvailable = true;
        else if (!r.TimedOut) _gitAvailable = r.ExitCode != -1;
        return _gitAvailable ?? true;
    }
    private static bool? _gitAvailable;

    /// <summary>Run several argv lists in order in <paramref name="repoRoot"/>; stops at the first failure.</summary>
    public static async Task<GitResult> RunAllIn(string repoRoot, IEnumerable<IReadOnlyList<string>> calls, int timeoutMs = 60000, CancellationToken ct = default)
    {
        var last = new GitResult { Success = true, ExitCode = 0 };
        foreach (var args in calls)
        {
            last = await RunIn(repoRoot, args, timeoutMs, ct);
            if (!last.Success) return last;
        }
        return last;
    }

    public static async Task<GitLastCommit?> LastCommitAsync(string repoRoot)
    {
        var r = await RunIn(repoRoot, PanelArgs.LastCommit(), 10000);
        return r.Success ? ParseLastCommit(r.Output) : null;
    }

    /// <summary>"abc1234\x1fsubject\x1f1730700000" → the record. Pure.</summary>
    public static GitLastCommit? ParseLastCommit(string? output)
    {
        var parts = (output ?? "").Trim().Split('\x1f');
        if (parts.Length < 3 || parts[0].Length == 0) return null;
        DateTimeOffset when = long.TryParse(parts[2].Trim(), out long unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.MinValue;
        return new GitLastCommit(parts[0].Trim(), parts[1].Trim(), when);
    }

    public static async Task<int> StashCountAsync(string repoRoot)
    {
        var r = await RunIn(repoRoot, PanelArgs.StashList(), 10000);
        return r.Success ? r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : 0;
    }

    /// <summary>The files git would list under an untracked folder (repository-relative, forward slashes).</summary>
    public static async Task<List<string>?> UntrackedFilesUnderAsync(string repoRoot, string dir)
    {
        var r = await RunIn(repoRoot, PanelArgs.UntrackedUnder(dir), 30000);
        if (!r.Success) return null;
        return r.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>The reason a panel operation failed, from git's own text. Pure.</summary>
    public static GitFailure ClassifyFailure(GitResult r)
    {
        if (r.Success) return GitFailure.None;
        if (r.TimedOut) return GitFailure.TimedOut;
        string l = (r.Error + "\n" + r.Output).ToLowerInvariant();
        if (l.Contains("protected branch") || l.Contains("gh006") || l.Contains("pre-receive hook declined")) return GitFailure.Protected;
        if (l.Contains("not possible to fast-forward") || l.Contains("diverging branches") || l.Contains("have diverged"))
            return GitFailure.NotFastForward;
        if (l.Contains("non-fast-forward") || l.Contains("fetch first") || l.Contains("[rejected]")) return GitFailure.Rejected;
        if (l.Contains("authentication failed") || l.Contains("could not read username") || l.Contains("permission denied")
            || l.Contains("terminal prompts disabled") || l.Contains("403")) return GitFailure.Auth;
        if (l.Contains("could not resolve host") || l.Contains("unable to access") || l.Contains("connection timed out")
            || l.Contains("could not read from remote")) return GitFailure.Network;
        if (l.Contains("would be overwritten") || l.Contains("please commit your changes or stash them")) return GitFailure.LocalChanges;
        if (l.Contains("conflict")) return GitFailure.Conflict;
        if (l.Contains("no tracking information") || l.Contains("has no upstream")) return GitFailure.NoUpstream;
        return GitFailure.Other;
    }
}
