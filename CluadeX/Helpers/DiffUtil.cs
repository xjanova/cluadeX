using CluadeX.Models;

namespace CluadeX.Helpers;

/// <summary>
/// Line-based diff between two file versions, used to show the user EXACTLY what an edit/write
/// changed (the trust primitive — never silently overwrite). Produces a compact hunked diff with
/// a few lines of context, colorized by the UI.
/// </summary>
public static class DiffUtil
{
    // O(n*m) LCS — cap per side so a huge file can't freeze the UI or blow memory (~16MB at the cap).
    private const int MaxLcsLines = 2000;
    // Cap how many lines we render so a whole-file rewrite doesn't flood the chat (counts stay accurate).
    private const int MaxDisplayLines = 500;
    // Lines of unchanged context kept around each change.
    private const int Context = 3;

    /// <summary>
    /// Compute a diff. Returns null when there is no change, or when the inputs are too large for an
    /// inline diff (the caller then falls back to its plain text summary — no regression).
    /// </summary>
    public static EditDiffResult? Compute(string? oldText, string? newText)
    {
        oldText ??= string.Empty;
        newText ??= string.Empty;
        if (string.Equals(oldText, newText, StringComparison.Ordinal))
            return null;

        var a = SplitLines(oldText);
        var b = SplitLines(newText);

        // New file (no prior content) → all additions. No LCS needed (and works for huge generated files).
        if (a.Length == 1 && a[0].Length == 0)
            return BuildAllOneKind(b, EditDiffKind.Add);
        // File emptied entirely → all removals.
        if (b.Length == 1 && b[0].Length == 0)
            return BuildAllOneKind(a, EditDiffKind.Remove);

        // Too large for an O(n*m) LCS — skip the inline diff rather than risk a UI stall.
        if (Math.Max(a.Length, b.Length) > MaxLcsLines)
            return null;

        return BuildHunks(LcsScript(a, b));
    }

    private readonly record struct Edit(EditDiffKind Kind, string Text);

    private static List<Edit> LcsScript(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j]
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var script = new List<Edit>(n + m);
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { script.Add(new Edit(EditDiffKind.Context, a[x])); x++; y++; }
            else if (dp[x + 1, y] >= dp[x, y + 1]) { script.Add(new Edit(EditDiffKind.Remove, a[x])); x++; }
            else { script.Add(new Edit(EditDiffKind.Add, b[y])); y++; }
        }
        while (x < n) { script.Add(new Edit(EditDiffKind.Remove, a[x])); x++; }
        while (y < m) { script.Add(new Edit(EditDiffKind.Add, b[y])); y++; }
        return script;
    }

    private static EditDiffResult BuildHunks(List<Edit> script)
    {
        int k = script.Count;

        // Keep every change, plus Context lines within `Context` of any change. Everything else is elided.
        var keep = new bool[k];
        for (int i = 0; i < k; i++)
        {
            if (script[i].Kind == EditDiffKind.Context) continue;
            int lo = Math.Max(0, i - Context);
            int hi = Math.Min(k - 1, i + Context);
            for (int j = lo; j <= hi; j++) keep[j] = true;
        }

        var result = new EditDiffResult();
        int oldNo = 1, newNo = 1;   // 1-based line numbers as we walk
        bool inHunk = false;
        bool firstHunk = true;

        for (int i = 0; i < k; i++)
        {
            var e = script[i];
            if (e.Kind == EditDiffKind.Add) result.Added++;
            else if (e.Kind == EditDiffKind.Remove) result.Removed++;

            if (keep[i] && !result.Truncated)
            {
                if (!inHunk)
                {
                    if (firstHunk) { result.FirstChangedLine = newNo; firstHunk = false; }
                    result.Lines.Add(new EditDiffLine { Kind = EditDiffKind.Hunk, Text = $"@@ -{oldNo} +{newNo} @@" });
                    inHunk = true;
                }

                if (result.Lines.Count >= MaxDisplayLines)
                    result.Truncated = true;
                else
                    result.Lines.Add(new EditDiffLine { Kind = e.Kind, Text = e.Text });
            }
            else if (!keep[i])
            {
                inHunk = false;
            }

            if (e.Kind != EditDiffKind.Add) oldNo++;
            if (e.Kind != EditDiffKind.Remove) newNo++;
        }

        if (result.Truncated)
            result.Lines.Add(new EditDiffLine { Kind = EditDiffKind.Hunk, Text = "@@ … diff truncated … @@" });

        return result;
    }

    private static EditDiffResult BuildAllOneKind(string[] lines, EditDiffKind kind)
    {
        var res = new EditDiffResult();
        foreach (var ln in lines)
        {
            if (kind == EditDiffKind.Add) res.Added++; else res.Removed++;
            if (res.Lines.Count < MaxDisplayLines)
                res.Lines.Add(new EditDiffLine { Kind = kind, Text = ln });
            else
                res.Truncated = true;
        }
        if (res.Truncated)
            res.Lines.Add(new EditDiffLine { Kind = EditDiffKind.Hunk, Text = "@@ … diff truncated … @@" });
        return res;
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
