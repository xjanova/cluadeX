namespace CluadeX.Models;

/// <summary>
/// A commit row shown in the Time Machine timeline. Parsed from
/// `git log --pretty=format:... --numstat` so we get message + +/- counts
/// + file count in one round-trip.
/// </summary>
public class CommitInfo
{
    public string Sha { get; set; } = "";
    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
    public string Message { get; set; } = "";
    public string Author { get; set; } = "";
    public string AuthorEmail { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    public string Branch { get; set; } = "";
    public int FilesChanged { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<string> Refs { get; set; } = new();

    /// <summary>True if the commit looks AI-authored (Co-Authored-By Claude, agent in message, etc).</summary>
    public bool IsAiAuthored { get; set; }

    /// <summary>True if this is the current HEAD.</summary>
    public bool IsHead { get; set; }

    /// <summary>Human-friendly relative time (e.g. "2m ago", "yesterday").</summary>
    public string RelativeTime
    {
        get
        {
            var diff = DateTimeOffset.Now - Date;
            if (diff.TotalSeconds < 60) return "just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays < 2) return "yesterday";
            if (diff.TotalDays < 14) return $"{(int)diff.TotalDays}d ago";
            return Date.ToLocalTime().ToString("MMM dd");
        }
    }
}

/// <summary>One file changed in a commit, with +/- counts.</summary>
public class CommitFileChange
{
    public string Path { get; set; } = "";
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public char Status { get; set; } = 'M'; // A=added, M=modified, D=deleted, R=renamed
}

/// <summary>One line of a unified diff (the kind we render in the diff panel).</summary>
public class DiffLine
{
    public char Kind { get; set; } = ' '; // '+', '-', ' ', '@' for hunk-header
    public string Content { get; set; } = "";
    public int? OldLineNo { get; set; }
    public int? NewLineNo { get; set; }
}

/// <summary>Result of a rewind / branch-from / cherry-pick operation.</summary>
public class TimeMachineActionResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? SnapshotBranch { get; set; }
    public string? StashRef { get; set; }
}
