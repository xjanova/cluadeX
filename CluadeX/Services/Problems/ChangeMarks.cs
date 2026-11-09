namespace CluadeX.Services.Problems;

/// <summary>One line the user wrote by hand, as remembered: its text (<see cref="UserEditMarker.Normalize"/>d), where it
/// was when last saved (1-based, a hint: other edits move it) and the day (yyyy-MM-dd).</summary>
public sealed record UserEditLine(string Text, int Line, string Date);

/// <summary>What the user edited by hand in one file: the lines they wrote and the lines they deleted.</summary>
public sealed class UserEditRecord
{
    public string Path { get; set; } = "";
    public List<UserEditLine> Lines { get; set; } = new();
    public List<UserEditLine> Removed { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }
    public bool IsEmpty => Lines.Count == 0 && Removed.Count == 0;
}

/// <summary>
/// The change marks of one file in the editor (owner 2026-11-09: "เมื่อมีการเพิ่มหรือลบ โค๊ด ควรป้ายแถบสี … ถ้าคุณแก้โค๊ดเอง ก็ต้องระบาย
/// ไว้อีกสี ว่า user แก้"), from three texts:
///   baseline  the file before this chat first touched it (CheckpointService.BeforeSession) — null when no chat did;
///   disk      the file as last opened or saved in the editor;
///   current   what the editor shows now.
/// Lines changed against the baseline are the AI's (added / modified / a deleted-lines mark), EXCEPT lines the user wrote:
/// those carrying a <see cref="UserEditMarker.Tag"/> comment, those the <see cref="UserEditRecord"/> remembers, and
/// unsaved typing (disk → current). The user's lines are separate hunks (<see cref="AiChangeHunk.ByUser"/>), also where
/// no chat touched the file. Pure text in, hunks out — sorted by line.
/// </summary>
public static class ChangeMarks
{
    /// <summary>A remembered line this short (a brace, "end") only matches near where it was: it is everywhere.</summary>
    private const int TrivialChars = 3, TrivialReach = 20;

    public static List<AiChangeHunk> Build(string? baseline, string? disk, string current, UserEditRecord? record)
    {
        current ??= "";
        var lines = UserEditMarker.SplitLines(current);
        int n = lines.Length;
        var user = new bool[n + 2];

        for (int i = 0; i < n; i++) if (UserEditMarker.HasMarker(lines[i])) user[i + 1] = true;
        if (record != null) foreach (int l in Match(record.Lines, lines)) user[l] = true;

        var userRemoved = new HashSet<string>(StringComparer.Ordinal);
        if (record != null) foreach (var r in record.Removed) userRemoved.Add(r.Text);
        var unsavedRemovals = new List<AiChangeHunk>();
        if (disk != null && !string.Equals(disk, current, StringComparison.Ordinal))
        {
            foreach (var h in AiChanges.Hunks(disk, current))
            {
                if (h.LineCount == 0)
                {
                    foreach (var o in h.OldLines) userRemoved.Add(UserEditMarker.Normalize(o));
                    unsavedRemovals.Add(h with { ByUser = true });
                    continue;
                }
                for (int k = 0; k < h.LineCount; k++)
                {
                    int l = h.StartLine + k;
                    if (l < 1 || l > n) continue;
                    // Only a marker taken off is not an edit of the code.
                    if (h.Kind == AiChangeKind.Modified && k < h.OldLines.Count
                        && UserEditMarker.Strip(h.OldLines[k]).TrimEnd() == lines[l - 1].TrimEnd()) continue;
                    user[l] = true;
                }
            }
        }

        var result = new List<AiChangeHunk>();
        var covered = new bool[n + 2];
        if (baseline != null)
        {
            foreach (var h in AiChanges.Hunks(baseline, current))
            {
                if (h.LineCount == 0)
                {
                    var real = h.OldLines.Select(UserEditMarker.Normalize).Where(t => t.Length > 0).ToList();
                    bool byUser = real.Count > 0 ? real.All(userRemoved.Contains) : h.OldLines.Select(UserEditMarker.Normalize).All(userRemoved.Contains);
                    result.Add(h with { ByUser = byUser });
                    continue;
                }
                int end = Math.Min(h.EndLine, n);
                for (int l = h.StartLine; l <= end; l++) covered[l] = true;
                bool any = false;
                for (int l = h.StartLine; l <= end; l++) if (user[l]) { any = true; break; }
                if (!any) { result.Add(h); continue; }
                // Split the AI's change around the lines the user wrote inside it.
                foreach (var (from, count, isUser) in Runs(user, h.StartLine, end))
                    result.Add(new AiChangeHunk(isUser ? AiChangeKind.Modified : h.Kind, from, count,
                        isUser ? Array.Empty<string>() : h.OldLines, Slice(lines, from, count), ByUser: isUser, Partial: !isUser));
            }
        }
        else
        {
            result.AddRange(unsavedRemovals);
        }

        // The user's lines no AI change covers (no chat touched the file, or the edit predates it).
        for (int l = 1; l <= n; l++)
        {
            if (!user[l] || covered[l]) continue;
            int from = l;
            while (l + 1 <= n && user[l + 1] && !covered[l + 1]) l++;
            result.Add(new AiChangeHunk(AiChangeKind.Modified, from, l - from + 1, Array.Empty<string>(), Slice(lines, from, l - from + 1), ByUser: true));
        }

        result.Sort((a, b) => a.StartLine != b.StartLine ? a.StartLine.CompareTo(b.StartLine) : a.LineCount.CompareTo(b.LineCount));
        return result.Count > AiChanges.MaxHunks ? result.GetRange(0, AiChanges.MaxHunks) : result;
    }

    /// <summary>How many of <paramref name="hunks"/> are the AI's / the user's.</summary>
    public static (int Ai, int User) Count(IReadOnlyList<AiChangeHunk> hunks) => (hunks.Count(h => !h.ByUser), hunks.Count(h => h.ByUser));

    private static IEnumerable<(int From, int Count, bool User)> Runs(bool[] user, int from, int to)
    {
        int s = from;
        for (int l = from + 1; l <= to + 1; l++)
            if (l > to || user[l] != user[s]) { yield return (s, l - s, user[s]); s = l; }
    }

    private static IReadOnlyList<string> Slice(string[] lines, int from, int count)
    {
        var list = new List<string>(count);
        for (int k = 0; k < count && from - 1 + k < lines.Length; k++) list.Add(lines[from - 1 + k]);
        return list;
    }

    /// <summary>
    /// Where the remembered lines are in <paramref name="lines"/> now (1-based). Each record takes one line with its text,
    /// the free one nearest to where it was — so ten remembered braces mark ten braces, not every one; a trivial line only
    /// within <see cref="TrivialReach"/> lines of where it was.
    /// </summary>
    public static List<int> Match(IReadOnlyList<UserEditLine> records, string[] lines)
    {
        var found = new List<int>();
        if (records.Count == 0) return found;
        var byText = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
        {
            string t = UserEditMarker.Normalize(lines[i]);
            if (t.Length == 0) continue;
            if (!byText.TryGetValue(t, out var at)) byText[t] = at = new List<int>();
            at.Add(i + 1);
        }
        var used = new HashSet<int>();
        foreach (var r in records.OrderBy(r => r.Line))
        {
            if (r.Text.Length == 0 || !byText.TryGetValue(r.Text, out var at)) continue;
            int best = -1, dist = int.MaxValue;
            foreach (int l in at)
            {
                if (used.Contains(l)) continue;
                int d = Math.Abs(l - r.Line);
                if (d < dist) { dist = d; best = l; }
            }
            if (best < 0) continue;
            if (r.Text.Length <= TrivialChars && dist > TrivialReach) continue;
            used.Add(best);
            found.Add(best);
        }
        found.Sort();
        return found;
    }

    /// <summary>The runs of lines the user wrote in a save from <paramref name="disk"/> to <paramref name="saved"/>
    /// (1-based in <paramref name="saved"/>) — where markers go. Taking a marker off is not one.</summary>
    public static List<(int Start, int Count)> EditedRuns(string disk, string saved)
    {
        var runs = new List<(int, int)>();
        if (string.Equals(disk, saved, StringComparison.Ordinal)) return runs;
        var lines = UserEditMarker.SplitLines(saved);
        foreach (var h in AiChanges.Hunks(disk, saved))
        {
            if (h.LineCount == 0) continue;
            int s = -1;
            for (int k = 0; k <= h.LineCount; k++)
            {
                int l = h.StartLine + k;
                bool edited = k < h.LineCount && l <= lines.Length && lines[l - 1].Trim().Length > 0
                    && !(h.Kind == AiChangeKind.Modified && k < h.OldLines.Count
                         && UserEditMarker.Strip(h.OldLines[k]).TrimEnd() == lines[l - 1].TrimEnd());
                if (edited && s < 0) s = l;
                else if (!edited && s >= 0) { runs.Add((s, l - s)); s = -1; }
            }
        }
        return runs;
    }

    /// <summary>
    /// The record after a save from <paramref name="disk"/> to <paramref name="saved"/>: remembered lines still in the file
    /// keep their date (at their new place), lines written in this save are added with <paramref name="date"/>, lines
    /// deleted in it are remembered as deleted; remembered lines no longer in the file are dropped.
    /// </summary>
    public static UserEditRecord Record(UserEditRecord? old, string path, string disk, string saved, DateTime date)
    {
        string day = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var lines = UserEditMarker.SplitLines(saved);
        var rec = new UserEditRecord { Path = path, UpdatedUtc = DateTime.UtcNow };
        var taken = new HashSet<int>();
        if (old != null)
        {
            // The same greedy nearest-free rule as Match, record by record, so each keeps its date at its new line.
            foreach (var r in old.Lines.OrderBy(r => r.Line))
            {
                int l = NearestFree(r, lines, taken);
                if (l < 0) continue;
                taken.Add(l);
                rec.Lines.Add(r with { Line = l });
            }
            rec.Removed.AddRange(old.Removed);
        }
        if (!string.Equals(disk, saved, StringComparison.Ordinal))
        {
            foreach (var (start, count) in EditedRuns(disk, saved))
                for (int l = start; l < start + count; l++)
                {
                    if (taken.Contains(l)) continue;
                    string t = UserEditMarker.Normalize(lines[l - 1]);
                    if (t.Length == 0) continue;
                    taken.Add(l);
                    rec.Lines.Add(new UserEditLine(t, l, day));
                }
            foreach (var h in AiChanges.Hunks(disk, saved).Where(h => h.LineCount == 0))
                foreach (var o in h.OldLines)
                {
                    string t = UserEditMarker.Normalize(o);
                    if (t.Length > 0 && !rec.Removed.Any(x => x.Text == t)) rec.Removed.Add(new UserEditLine(t, h.StartLine, day));
                }
        }
        // A deleted line that is back in the file is not deleted any more.
        var present = new HashSet<string>(lines.Select(UserEditMarker.Normalize), StringComparer.Ordinal);
        rec.Removed.RemoveAll(r => present.Contains(r.Text));
        if (rec.Removed.Count > 500) rec.Removed.RemoveRange(0, rec.Removed.Count - 500);
        rec.Lines.Sort((a, b) => a.Line.CompareTo(b.Line));
        return rec;
    }

    private static int NearestFree(UserEditLine r, string[] lines, HashSet<int> taken)
    {
        int best = -1, dist = int.MaxValue;
        for (int i = 0; i < lines.Length; i++)
        {
            if (taken.Contains(i + 1) || UserEditMarker.Normalize(lines[i]) != r.Text) continue;
            int d = Math.Abs(i + 1 - r.Line);
            if (d < dist) { dist = d; best = i + 1; }
        }
        if (best > 0 && r.Text.Length <= TrivialChars && dist > TrivialReach) return -1;
        return best;
    }
}
