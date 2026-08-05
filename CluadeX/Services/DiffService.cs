using System.Text;

namespace CluadeX.Services;

/// <summary>
/// Side-by-side diffing and merge-conflict resolution for the workbench.
///
/// Separate from <see cref="CluadeX.Helpers.DiffUtil"/> on purpose: that one produces a compact
/// UNIFIED diff for the chat's edit-approval card (hunks, ±3 context lines, capped at 500 rendered
/// lines). A diff *editor* needs the opposite — every line, aligned into left/right row pairs, with
/// filler rows so the two panes scroll together. Sharing one implementation would compromise both.
///
/// Uses Myers O(ND) rather than the O(n·m) LCS table, because a diff editor gets pointed at whole
/// files: two 5,000-line files would need a 100 MB DP table, while Myers only pays for the lines
/// that actually differ (D), which for a normal edit is tiny.
/// </summary>
public static class DiffService
{
    /// <summary>Beyond this the panes are unusable anyway, and the trace memory stops being free.</summary>
    private const int MaxDiffLines = 60_000;

    /// <summary>
    /// Edit-distance ceiling. Real edits have a small D; a D this large means the two files are
    /// essentially unrelated, where "replace everything" is both cheaper and more truthful than a
    /// line-by-line alignment nobody would read.
    /// </summary>
    private const int MaxEditDistance = 2_000;

    // ═══════════════════════════ Side-by-side ═══════════════════════════

    public static SideBySideDiff BuildSideBySide(string? oldText, string? newText)
    {
        var result = new SideBySideDiff();
        oldText ??= "";
        newText ??= "";

        // An empty file has ZERO lines. Splitting "" yields one empty line, which would render a
        // brand-new file as "2 added, 1 removed" — a phantom deletion of a line that never existed.
        var a = oldText.Length == 0 ? Array.Empty<string>() : SplitLines(oldText);
        var b = newText.Length == 0 ? Array.Empty<string>() : SplitLines(newText);

        if (a.Length > MaxDiffLines || b.Length > MaxDiffLines)
        {
            result.Truncated = true;
            result.Note = $"File too large to align ({Math.Max(a.Length, b.Length):N0} lines).";
            return result;
        }

        if (oldText.Length == 0 && newText.Length == 0)
        {
            result.Note = "Both sides are empty.";
            return result;
        }

        var script = Diff(a, b, out bool hitCap);
        result.Truncated = hitCap;
        if (hitCap)
            result.Note = "The two versions differ too much to align line by line — showing them as a full replacement.";

        BuildRows(script, result);
        return result;
    }

    /// <summary>Pair runs of removals with runs of additions so a changed line shows old on the left
    /// and new on the right, instead of as an unrelated delete followed by an unrelated insert.</summary>
    private static void BuildRows(List<DiffOp> script, SideBySideDiff result)
    {
        int leftNo = 1, rightNo = 1;
        int i = 0;

        while (i < script.Count)
        {
            var op = script[i];

            if (op.Kind == DiffOpKind.Equal)
            {
                result.Rows.Add(new DiffRow
                {
                    Kind = DiffRowKind.Unchanged,
                    LeftLine = leftNo++,
                    LeftText = op.Text,
                    RightLine = rightNo++,
                    RightText = op.Text,
                });
                i++;
                continue;
            }

            // Collect the whole run of removals then the whole run of additions that follows it.
            var removed = new List<string>();
            while (i < script.Count && script[i].Kind == DiffOpKind.Delete) { removed.Add(script[i].Text); i++; }
            var added = new List<string>();
            while (i < script.Count && script[i].Kind == DiffOpKind.Insert) { added.Add(script[i].Text); i++; }

            int pairs = Math.Min(removed.Count, added.Count);
            for (int p = 0; p < pairs; p++)
            {
                result.Rows.Add(new DiffRow
                {
                    Kind = DiffRowKind.Modified,
                    LeftLine = leftNo++,
                    LeftText = removed[p],
                    RightLine = rightNo++,
                    RightText = added[p],
                });
            }
            for (int p = pairs; p < removed.Count; p++)
            {
                result.Rows.Add(new DiffRow
                {
                    Kind = DiffRowKind.Removed,
                    LeftLine = leftNo++,
                    LeftText = removed[p],
                    RightLine = null,
                    RightText = "",
                });
            }
            for (int p = pairs; p < added.Count; p++)
            {
                result.Rows.Add(new DiffRow
                {
                    Kind = DiffRowKind.Added,
                    LeftLine = null,
                    LeftText = "",
                    RightLine = rightNo++,
                    RightText = added[p],
                });
            }

            result.Removed += removed.Count;
            result.Added += added.Count;
        }
    }

    // ═══════════════════════════ Myers diff ═══════════════════════════

    private enum DiffOpKind { Equal, Delete, Insert }

    private readonly record struct DiffOp(DiffOpKind Kind, string Text);

    /// <summary>
    /// Myers greedy forward pass with a compact per-D trace, plus common prefix/suffix trimming
    /// (which is what makes a one-line change in a 10,000-line file cost almost nothing).
    /// </summary>
    private static List<DiffOp> Diff(string[] a, string[] b, out bool hitCap)
    {
        hitCap = false;
        var script = new List<DiffOp>();

        // Trim the identical head.
        int start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;

        // Trim the identical tail.
        int endA = a.Length, endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }

        for (int i = 0; i < start; i++) script.Add(new DiffOp(DiffOpKind.Equal, a[i]));

        int n = endA - start, m = endB - start;
        if (n > 0 || m > 0)
        {
            var midA = new string[n];
            Array.Copy(a, start, midA, 0, n);
            var midB = new string[m];
            Array.Copy(b, start, midB, 0, m);

            var middle = MyersMiddle(midA, midB, out hitCap);
            script.AddRange(middle);
        }

        for (int i = endA; i < a.Length; i++) script.Add(new DiffOp(DiffOpKind.Equal, a[i]));
        return script;
    }

    private static List<DiffOp> MyersMiddle(string[] a, string[] b, out bool hitCap)
    {
        hitCap = false;
        int n = a.Length, m = b.Length;

        if (n == 0)
        {
            var onlyAdds = new List<DiffOp>(m);
            foreach (var line in b) onlyAdds.Add(new DiffOp(DiffOpKind.Insert, line));
            return onlyAdds;
        }
        if (m == 0)
        {
            var onlyDels = new List<DiffOp>(n);
            foreach (var line in a) onlyDels.Add(new DiffOp(DiffOpKind.Delete, line));
            return onlyDels;
        }

        int max = Math.Min(n + m, MaxEditDistance);
        int offset = n + m;                 // k can range over [-(n+m), n+m]
        var v = new int[2 * (n + m) + 1];
        var trace = new List<int[]>();

        for (int d = 0; d <= max; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset]))
                    x = v[k + 1 + offset];          // came from above (an insert)
                else
                    x = v[k - 1 + offset] + 1;      // came from the left (a delete)

                int y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }   // slide down the diagonal
                v[k + offset] = x;

                if (x >= n && y >= m)
                {
                    trace.Add((int[])v.Clone());
                    return Backtrack(a, b, trace, offset, d);
                }
            }
            trace.Add((int[])v.Clone());
        }

        // Past the ceiling — say so and degrade to a wholesale replacement rather than lying.
        hitCap = true;
        var replace = new List<DiffOp>(n + m);
        foreach (var line in a) replace.Add(new DiffOp(DiffOpKind.Delete, line));
        foreach (var line in b) replace.Add(new DiffOp(DiffOpKind.Insert, line));
        return replace;
    }

    private static List<DiffOp> Backtrack(string[] a, string[] b, List<int[]> trace, int offset, int finalD)
    {
        var reversed = new List<DiffOp>();
        int x = a.Length, y = b.Length;

        for (int d = finalD; d > 0; d--)
        {
            var v = trace[d - 1];
            int k = x - y;

            int prevK = (k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset])) ? k + 1 : k - 1;
            int prevX = v[prevK + offset];
            int prevY = prevX - prevK;

            while (x > prevX && y > prevY) { reversed.Add(new DiffOp(DiffOpKind.Equal, a[--x])); y--; }

            if (d > 0)
            {
                if (x == prevX) reversed.Add(new DiffOp(DiffOpKind.Insert, b[--y]));
                else reversed.Add(new DiffOp(DiffOpKind.Delete, a[--x]));
            }
        }
        while (x > 0 && y > 0) { reversed.Add(new DiffOp(DiffOpKind.Equal, a[--x])); y--; }
        while (y > 0) reversed.Add(new DiffOp(DiffOpKind.Insert, b[--y]));
        while (x > 0) reversed.Add(new DiffOp(DiffOpKind.Delete, a[--x]));

        reversed.Reverse();
        return reversed;
    }

    // ═══════════════════════════ Merge conflicts ═══════════════════════════

    private const string OursMarker = "<<<<<<<";
    private const string BaseMarker = "|||||||";
    private const string SplitMarker = "=======";
    private const string TheirsMarker = ">>>>>>>";

    /// <summary>
    /// Find every conflict block git left in a file. Handles the diff3 form (with a <c>|||||||</c>
    /// base section) as well as the default two-way form. An unterminated block is reported rather
    /// than silently dropped — a half-parsed conflict is how a resolver eats your code.
    /// </summary>
    public static ConflictScan ScanConflicts(string? content)
    {
        var scan = new ConflictScan();
        if (string.IsNullOrEmpty(content)) return scan;
        if (!content.Contains(OursMarker, StringComparison.Ordinal)) return scan;

        var lines = SplitLines(content);
        int i = 0;

        while (i < lines.Length)
        {
            if (!lines[i].StartsWith(OursMarker, StringComparison.Ordinal)) { i++; continue; }

            var block = new ConflictBlock
            {
                StartLine = i + 1,
                OursLabel = LabelFrom(lines[i], OursMarker, "current"),
            };

            int j = i + 1;
            var ours = new List<string>();
            var baseLines = new List<string>();
            var theirs = new List<string>();
            int section = 0;   // 0 = ours, 1 = base, 2 = theirs
            bool closed = false;

            for (; j < lines.Length; j++)
            {
                string line = lines[j];

                if (line.StartsWith(OursMarker, StringComparison.Ordinal))
                    break;                                  // a new block started — this one is unterminated

                if (line.StartsWith(BaseMarker, StringComparison.Ordinal) && section == 0)
                {
                    section = 1; block.HasBase = true; continue;
                }
                if (line.StartsWith(SplitMarker, StringComparison.Ordinal) && section < 2)
                {
                    section = 2; continue;
                }
                if (line.StartsWith(TheirsMarker, StringComparison.Ordinal))
                {
                    block.TheirsLabel = LabelFrom(line, TheirsMarker, "incoming");
                    block.EndLine = j + 1;
                    closed = true;
                    j++;
                    break;
                }

                switch (section)
                {
                    case 0: ours.Add(line); break;
                    case 1: baseLines.Add(line); break;
                    default: theirs.Add(line); break;
                }
            }

            if (!closed)
            {
                scan.Malformed = true;
                i = j;                                      // don't re-enter the same broken block
                continue;
            }

            block.Index = scan.Conflicts.Count;
            block.OurLines = ours;
            block.BaseLines = baseLines;
            block.TheirLines = theirs;
            scan.Conflicts.Add(block);
            i = j;
        }

        return scan;
    }

    private static string LabelFrom(string markerLine, string marker, string fallback)
    {
        string label = markerLine.Length > marker.Length ? markerLine[marker.Length..].Trim() : "";
        return label.Length == 0 ? fallback : label;
    }

    /// <summary>
    /// Rewrite the file with one conflict resolved, leaving every other conflict untouched.
    ///
    /// Re-scans the content it is handed rather than trusting line numbers captured earlier: the
    /// user may have hand-edited, or resolved another conflict, since the block was found — and
    /// applying stale offsets to a merge is how a resolver corrupts a file.
    /// </summary>
    public static string ResolveConflict(string content, int conflictIndex, ConflictChoice choice)
    {
        var scan = ScanConflicts(content);
        if (conflictIndex < 0 || conflictIndex >= scan.Conflicts.Count) return content;

        var block = scan.Conflicts[conflictIndex];
        var lines = SplitLines(content);
        string eol = DetectEol(content);

        var replacement = choice switch
        {
            ConflictChoice.Ours => block.OurLines,
            ConflictChoice.Theirs => block.TheirLines,
            ConflictChoice.Both => block.OurLines.Concat(block.TheirLines).ToList(),
            ConflictChoice.Base => block.BaseLines,
            _ => block.OurLines,
        };

        var output = new List<string>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNo = i + 1;
            if (lineNo < block.StartLine || lineNo > block.EndLine) { output.Add(lines[i]); continue; }
            if (lineNo == block.StartLine) output.AddRange(replacement);
            // every other line inside the block (including the markers) is dropped
        }

        return string.Join(eol, output);
    }

    /// <summary>Resolve every remaining conflict the same way — the "take all mine / all theirs"
    /// shortcut. Applied one at a time against freshly rescanned content so indices never go stale.</summary>
    public static string ResolveAll(string content, ConflictChoice choice)
    {
        string current = content;
        for (int guard = 0; guard < 1000; guard++)
        {
            var scan = ScanConflicts(current);
            if (scan.Conflicts.Count == 0) break;
            current = ResolveConflict(current, 0, choice);
        }
        return current;
    }

    /// <summary>Preserve the file's dominant line ending — rewriting a CRLF file with LF shows the
    /// whole file as changed in git.</summary>
    private static string DetectEol(string content)
    {
        int crlf = 0, lf = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n') continue;
            if (i > 0 && content[i - 1] == '\r') crlf++; else lf++;
        }
        return crlf >= lf && crlf > 0 ? "\r\n" : "\n";
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}

// ═══════════════════════════ Models ═══════════════════════════

public enum DiffRowKind { Unchanged, Added, Removed, Modified }

public sealed class DiffRow
{
    public DiffRowKind Kind { get; set; }
    /// <summary>1-based line in the old file; null when this row is filler on the left.</summary>
    public int? LeftLine { get; set; }
    public string LeftText { get; set; } = "";
    /// <summary>1-based line in the new file; null when this row is filler on the right.</summary>
    public int? RightLine { get; set; }
    public string RightText { get; set; } = "";

    public bool IsChange => Kind != DiffRowKind.Unchanged;
}

public sealed class SideBySideDiff
{
    public List<DiffRow> Rows { get; } = new();
    public int Added { get; set; }
    public int Removed { get; set; }
    /// <summary>A limit was hit — the alignment is not the full story and the UI must say so.</summary>
    public bool Truncated { get; set; }
    public string? Note { get; set; }

    public bool HasChanges => Added > 0 || Removed > 0;
}

public enum ConflictChoice { Ours, Theirs, Both, Base }

public sealed class ConflictBlock
{
    public int Index { get; set; }
    /// <summary>1-based line of the <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> marker.</summary>
    public int StartLine { get; set; }
    /// <summary>1-based line of the <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> marker.</summary>
    public int EndLine { get; set; }
    public string OursLabel { get; set; } = "current";
    public string TheirsLabel { get; set; } = "incoming";
    public List<string> OurLines { get; set; } = new();
    public List<string> BaseLines { get; set; } = new();
    public List<string> TheirLines { get; set; } = new();
    public bool HasBase { get; set; }

    public string Title => $"Conflict {Index + 1} · line {StartLine}";
    public string OursSummary => $"{OursLabel} ({OurLines.Count} line{(OurLines.Count == 1 ? "" : "s")})";
    public string TheirsSummary => $"{TheirsLabel} ({TheirLines.Count} line{(TheirLines.Count == 1 ? "" : "s")})";
    public string OursPreview => Preview(OurLines);
    public string TheirsPreview => Preview(TheirLines);

    private static string Preview(List<string> lines)
    {
        if (lines.Count == 0) return "(empty)";
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(6, lines.Count); i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(lines[i].Length > 160 ? lines[i][..160] + "…" : lines[i]);
        }
        if (lines.Count > 6) sb.Append($"\n… +{lines.Count - 6} more");
        return sb.ToString();
    }
}

public sealed class ConflictScan
{
    public List<ConflictBlock> Conflicts { get; } = new();
    /// <summary>At least one block had no closing marker — the file is not safely resolvable
    /// automatically and the UI must refuse rather than guess.</summary>
    public bool Malformed { get; set; }
    public bool HasConflicts => Conflicts.Count > 0;
}
