using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Helpers;

/// <summary>
/// Turns a raw build log into what a model needs to fix it: each distinct error once, with a
/// project-relative path, line and code — and a one-line verdict when the build is clean.
///
/// MSBuild prints every error twice (inline, then again in the summary), appends the absolute project
/// path to each one, and surrounds them with restore/progress chatter. A small local model handed that
/// log spends its context on noise; handed the head of it (which is what the 5,000-char cut did) it
/// often never saw the errors at all.
/// </summary>
public static class BuildOutputDistiller
{
    // MSBuild / dotnet / tsc:  path(12,5): error CS1002: ; expected [C:\repo\App\App.csproj]
    private static readonly Regex ParenStyle = new(
        @"^\s*(?<file>[^\s(][^(]*?)\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\)\s*:\s*(?<kind>error|warning)\s+(?<code>[A-Za-z]+\d+)?\s*:?\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // gcc / clang / go / rustc-compact / eslint-compact:  path:12:5: error: message
    private static readonly Regex ColonStyle = new(
        @"^\s*(?<file>(?:[A-Za-z]:)?[^:\s][^:]*):(?<line>\d+):(?:(?<col>\d+):)?\s*(?<kind>error|warning|fatal error)(?:\[(?<code>[^\]]+)\])?\s*:\s*(?<msg>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private sealed record Diagnostic(string File, string Line, string Col, string Kind, string Code, string Message)
    {
        public string Render() =>
            $"{File}({Line}{(Col.Length > 0 ? "," + Col : "")}): {Kind}{(Code.Length > 0 ? " " + Code : "")}: {Message}";
    }

    /// <param name="raw">Combined stdout + stderr of the build.</param>
    /// <param name="succeeded">The build's exit code was 0.</param>
    /// <param name="projectRoot">Absolute paths under this root are shown relative to it.</param>
    /// <param name="maxChars">Hard cap on the returned text.</param>
    public static string Distill(string raw, bool succeeded, string projectRoot, int maxChars)
    {
        raw ??= "";
        var errors = new List<Diagnostic>();
        var warnings = new List<Diagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var m = ParenStyle.Match(rawLine);
            if (!m.Success) m = ColonStyle.Match(rawLine);
            if (!m.Success) continue;

            var d = new Diagnostic(
                Relativize(m.Groups["file"].Value.Trim(), projectRoot),
                m.Groups["line"].Value,
                m.Groups["col"].Value,
                m.Groups["kind"].Value.ToLowerInvariant().Contains("error") ? "error" : "warning",
                m.Groups["code"].Value,
                m.Groups["msg"].Value.Trim());
            if (!seen.Add(d.Render())) continue;          // MSBuild repeats every diagnostic in its summary
            (d.Kind == "error" ? errors : warnings).Add(d);
        }

        var sb = new StringBuilder();
        if (succeeded)
        {
            sb.Append("Build succeeded");
            sb.Append(warnings.Count == 0 ? "." : $" with {warnings.Count} warning(s):");
            sb.Append('\n');
            foreach (var w in warnings.Take(5)) sb.Append(w.Render()).Append('\n');
            if (warnings.Count > 5) sb.Append($"… {warnings.Count - 5} more warning(s)\n");
        }
        else if (errors.Count > 0)
        {
            sb.Append($"Build FAILED — {errors.Count} error(s)");
            if (warnings.Count > 0) sb.Append($", {warnings.Count} warning(s)");
            sb.Append(":\n");
            foreach (var e in errors.Take(25)) sb.Append(e.Render()).Append('\n');
            if (errors.Count > 25) sb.Append($"… {errors.Count - 25} more error(s) — fix these first and rebuild\n");
        }
        else
        {
            // Failed, but nothing parsed as a diagnostic (restore failure, missing SDK, a script error):
            // the useful part is almost always at the end of the log.
            sb.Append("Build FAILED (no compiler diagnostics found — last lines of the log):\n");
            sb.Append(OutputBudget.HeadAndTail(raw.TrimEnd(), Math.Max(400, maxChars - 80)));
        }

        string text = sb.ToString().TrimEnd();
        return text.Length <= maxChars ? text : text[..maxChars] + "\n… (truncated)";
    }

    private static string Relativize(string file, string root)
    {
        if (string.IsNullOrEmpty(root)) return file.Replace('\\', '/');
        try
        {
            if (System.IO.Path.IsPathRooted(file))
            {
                string rel = System.IO.Path.GetRelativePath(root, file);
                if (!rel.StartsWith("..")) file = rel;
            }
        }
        catch { /* keep the path as printed */ }
        return file.Replace('\\', '/');
    }
}

/// <summary>Fitting long tool output into a budget without losing the part that matters.</summary>
public static class OutputBudget
{
    /// <summary>
    /// Keep the beginning AND the end, dropping the middle. Build, test and install logs put their
    /// verdict and errors at the END — the old "first 5,000 characters" cut threw exactly that away.
    /// </summary>
    public static string HeadAndTail(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text ?? "";
        int head = maxChars * 2 / 5;
        int tail = maxChars - head - 60;
        if (tail < 100) return text[^maxChars..];

        // Cut on line boundaries so no half-line reaches the model.
        int headEnd = text.LastIndexOf('\n', Math.Min(head, text.Length - 1));
        if (headEnd < head / 2) headEnd = head;
        int tailStart = text.IndexOf('\n', text.Length - tail);
        if (tailStart < 0 || tailStart - (text.Length - tail) > tail / 2) tailStart = text.Length - tail;

        int omitted = tailStart - headEnd;
        return text[..headEnd].TrimEnd() + $"\n\n… [{omitted:N0} characters omitted] …\n\n" + text[tailStart..].TrimStart('\n');
    }
}
