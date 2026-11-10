namespace CluadeX.Services.Problems;

public enum AiChangeKind { Added, Modified, Removed }

/// <summary>
/// One run of lines the AI changed. <see cref="StartLine"/> is 1-based in the text as it is NOW; <see cref="LineCount"/>
/// lines from there are the AI's (0 for <see cref="AiChangeKind.Removed"/>: the AI deleted <see cref="OldLines"/>, which
/// stood just before line <see cref="StartLine"/> — one past the last line when they were at the end).
/// </summary>
/// <param name="ByUser">The user wrote these lines by hand (or deleted these), not the AI: drawn in the user's colour
/// (<see cref="ChangeMarks"/>).</param>
/// <param name="Partial">Part of an AI change the user edited inside: putting it back alone would not restore the AI's
/// before-text, so the peek offers no Revert for it.</param>
public sealed record AiChangeHunk(AiChangeKind Kind, int StartLine, int LineCount, IReadOnlyList<string> OldLines, IReadOnlyList<string> NewLines,
    bool ByUser = false, bool Partial = false)
{
    /// <summary>The peek may put this change back: the AI's own, whole, with its before and after lines known.</summary>
    public bool CanRevert => !ByUser && !Partial && OldLines.Count + NewLines.Count > 0;

    /// <summary>The line the gutter mark and "next change" point at.</summary>
    public int AnchorLine => StartLine;
    public int EndLine => LineCount > 0 ? StartLine + LineCount - 1 : StartLine;
    public bool Covers(int line) => LineCount > 0 ? line >= StartLine && line <= EndLine : line == StartLine || line == StartLine - 1;
}

/// <summary>
/// What the AI changed in a file: the line diff between the file before the AI's latest turn that touched it
/// (<see cref="CheckpointService.BeforeLatestTurn"/>) and the text now (<see cref="DiffService"/>), as hunks the editor's
/// gutter marks, steps through and can put back one at a time. Pure text in, text out.
/// </summary>
public static class AiChanges
{
    /// <summary>Above this many hunks the gutter shows only the first ones (a file the AI rewrote whole).</summary>
    public const int MaxHunks = 2000;

    public static List<AiChangeHunk> Hunks(string? before, string? after)
    {
        var hunks = new List<AiChangeHunk>();
        before ??= "";
        after ??= "";
        if (before == after) return hunks;
        var diff = DiffService.BuildSideBySide(before, after);
        var rows = diff.Rows;
        if (rows.Count == 0)
        {
            // Too large to align: one hunk over the whole file says what is true (it changed) without guessing where.
            int lines = after.Length == 0 ? 0 : after.Replace("\r\n", "\n").Split('\n').Length;
            if (lines > 0) hunks.Add(new AiChangeHunk(AiChangeKind.Modified, 1, lines, Array.Empty<string>(), Array.Empty<string>()));
            return hunks;
        }

        int lastRight = 0;
        for (int i = 0; i < rows.Count && hunks.Count < MaxHunks;)
        {
            if (!rows[i].IsChange)
            {
                lastRight = rows[i].RightLine ?? lastRight;
                i++;
                continue;
            }
            var oldLines = new List<string>();
            var newLines = new List<string>();
            int? firstRight = null;
            for (; i < rows.Count && rows[i].IsChange; i++)
            {
                var r = rows[i];
                if (r.LeftLine != null) oldLines.Add(r.LeftText);
                if (r.RightLine != null)
                {
                    newLines.Add(r.RightText);
                    firstRight ??= r.RightLine;
                    lastRight = r.RightLine.Value;
                }
            }
            if (newLines.Count == 0)
                hunks.Add(new AiChangeHunk(AiChangeKind.Removed, lastRight + 1, 0, oldLines, newLines));
            else
                hunks.Add(new AiChangeHunk(oldLines.Count == 0 ? AiChangeKind.Added : AiChangeKind.Modified,
                    firstRight!.Value, newLines.Count, oldLines, newLines));
        }
        return hunks;
    }

    /// <summary>
    /// Where in <paramref name="text"/> putting <paramref name="hunk"/> back happens, and with what: replace
    /// <c>Length</c> characters at <c>Offset</c> by <c>Replacement</c> (one edit, so it is one undo step). Null when
    /// the text no longer has the hunk's lines (it changed since the marks were made).
    /// </summary>
    public static (int Offset, int Length, string Replacement)? RevertEdit(string text, AiChangeHunk hunk)
    {
        if (hunk.OldLines.Count == 0 && hunk.NewLines.Count == 0) return null;   // the "too large to align" hunk
        string eol = text.Contains("\r\n") ? "\r\n" : "\n";
        var starts = LineStarts(text);
        int n = starts.Count;   // lines, as the editor counts them ("a\n" is two: "a" and "")
        int s = hunk.StartLine, count = hunk.LineCount;
        if (s < 1 || s > n + 1 || s + count - 1 > n) return null;

        // The hunk's lines must still be what the AI wrote: never put text back over something else.
        for (int k = 0; k < count; k++)
            if (LineText(text, starts, s + k) != hunk.NewLines[k]) return null;

        string old = string.Join(eol, hunk.OldLines);
        if (count == 0)
        {
            // Put deleted lines back before line s — or after the last line, which has no line end of its own.
            if (s <= n) return (starts[s - 1], 0, old + eol);
            return (text.Length, 0, eol + old);
        }
        int from = starts[s - 1];
        bool toEnd = s + count - 1 == n;   // the region runs to the end of the text (its last line has no line end)
        int to = toEnd ? text.Length : starts[s + count - 1];
        if (hunk.OldLines.Count == 0)
        {
            // Lines the AI added: remove them with ONE line end — theirs, or the one before them at the end of the text.
            if (toEnd && s > 1)
            {
                int lineStart = starts[s - 1];
                from = lineStart - (lineStart >= 2 && text[lineStart - 2] == '\r' && text[lineStart - 1] == '\n' ? 2 : 1);
            }
            return (from, to - from, "");
        }
        return (from, to - from, toEnd ? old : old + eol);
    }

    /// <summary>Apply <see cref="RevertEdit"/> to a string (the harness; the editor applies it to its document).</summary>
    public static string? Revert(string text, AiChangeHunk hunk)
        => RevertEdit(text, hunk) is { } e ? text[..e.Offset] + e.Replacement + text[(e.Offset + e.Length)..] : null;

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { starts.Add(i + 2); i++; }
            else if (text[i] == '\n' || text[i] == '\r') starts.Add(i + 1);
        }
        return starts;
    }

    private static string LineText(string text, List<int> starts, int line)
    {
        int from = starts[line - 1];
        int to = line < starts.Count ? starts[line] : text.Length;
        return text[from..to].TrimEnd('\n').TrimEnd('\r');
    }

    /// <summary>The next / previous hunk from <paramref name="line"/>, wrapping around.</summary>
    public static AiChangeHunk? Step(IReadOnlyList<AiChangeHunk> hunks, int line, bool forward)
    {
        if (hunks.Count == 0) return null;
        if (forward) return hunks.FirstOrDefault(h => h.AnchorLine > line) ?? hunks[0];
        return hunks.LastOrDefault(h => h.AnchorLine < line) ?? hunks[^1];
    }
}
