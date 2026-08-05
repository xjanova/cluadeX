using System.Text;

namespace CluadeX.Helpers;

/// <summary>
/// Distills a test runner's raw stdout/stderr into a compact, actionable signal: a pass/fail SUMMARY line
/// plus only the FAILING tests / assertions — not the giant blob a weak model would have to re-triage itself.
/// Runner-agnostic + best-effort: when nothing matches, it returns the tail of the raw output so the model
/// still has something to work with.
/// </summary>
public static class TestOutputDistiller
{
    // Tokens that mark a line as worth keeping (a failure, error, or assertion detail).
    private static readonly string[] FailMarkers =
    {
        "fail", "error", "assert", "✗", "✘", "expected", "--- fail", "panicked", "exception",
        "not ok", "✕", "did not", "but was", "but got", "❌",
    };

    public static string Distill(string stdout, string stderr, string cmd)
    {
        string combined = ((stdout ?? "") + "\n" + (stderr ?? "")).Replace("\r\n", "\n");
        var lines = combined.Split('\n');

        // 1. Best summary line = the last line that reports counts (e.g. "5 passed, 1 failed",
        //    "Passed!  - Failed: 0, Passed: 12", "test result: ok. 3 passed; 0 failed").
        string? summary = null;
        foreach (var l in lines)
        {
            var low = l.ToLowerInvariant();
            bool looksLikeSummary =
                (low.Contains("passed") || low.Contains("test result") || low.Contains(" tests") || low.Contains("ok."))
                && (low.Contains("fail") || low.Contains("passed") || low.Contains("error") || low.Contains("ok"));
            if (looksLikeSummary && l.Trim().Length > 0) summary = l.Trim();
        }

        // 2. Collect failing / error / assertion lines (capped, deduped on trimmed text).
        var fails = new List<string>();
        var seen = new HashSet<string>();
        foreach (var l in lines)
        {
            if (summary != null && l.Trim() == summary.Trim()) continue; // already shown as the summary line
            var low = l.ToLowerInvariant();
            // "zero failures" count lines must NOT be mistaken for failures (an all-pass run says "Failed: 0").
            if (low.Contains("failed: 0") || low.Contains("0 failed") || low.Contains("failures: 0")) continue;
            bool isFail = false;
            foreach (var m in FailMarkers) { if (low.Contains(m)) { isFail = true; break; } }
            if (!isFail) continue;
            string t = l.TrimEnd();
            if (t.Trim().Length == 0) continue;
            if (!seen.Add(t.Trim())) continue;
            fails.Add(t);
            if (fails.Count >= 40) break;
        }

        var sb = new StringBuilder();
        if (summary != null) sb.AppendLine(summary);
        if (fails.Count > 0)
        {
            if (summary != null) sb.AppendLine();
            sb.AppendLine("Failing / relevant lines:");
            foreach (var f in fails) sb.AppendLine(f);
        }

        string outp = sb.ToString().Trim();
        if (outp.Length == 0)
        {
            // Nothing recognisable — hand back the tail so the model still has the runner's last words.
            outp = combined.Trim();
            if (outp.Length > 1500) outp = "... (earlier output trimmed)\n" + outp[^1500..];
        }
        else if (outp.Length > 2200)
        {
            outp = outp[..2200] + "\n... (truncated)";
        }
        return outp;
    }
}
