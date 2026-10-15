namespace CluadeX.Helpers;

/// <summary>
/// Re-indents an edit's replacement text to the file's own indentation.
///
/// edit_file's whitespace-tolerant fallback already finds the right block when the model got the
/// indentation wrong (it routinely drops the base indent, or writes 2-space levels into a 4-space file).
/// But it then spliced the replacement in verbatim, so the new code kept the MODEL's indentation:
/// ragged in C#, and a real bug in Python or YAML where indentation is syntax.
///
/// The matched block gives a line-by-line correspondence between what the model wrote and what the
/// file actually contains. From those pairs this builds the model→file indent mapping and applies it to
/// the replacement. When the pairs disagree with each other it changes nothing — guessing an indent
/// would be worse than leaving the model's.
/// </summary>
public static class IndentAdapter
{
    /// <param name="modelLines">The lines of the model's 'find' text.</param>
    /// <param name="fileLines">The file lines the tolerant match landed on (same count).</param>
    /// <param name="replacement">The model's replacement text (\n line endings).</param>
    public static string Adapt(IReadOnlyList<string> modelLines, IReadOnlyList<string> fileLines, string replacement)
    {
        if (modelLines.Count != fileLines.Count || string.IsNullOrEmpty(replacement)) return replacement;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var pairs = new List<(int model, int file)>();
        bool anyDifference = false;
        bool spacesOnly = true;

        for (int i = 0; i < modelLines.Count; i++)
        {
            if (modelLines[i].Trim().Length == 0 || fileLines[i].Trim().Length == 0) continue;
            string m = Lead(modelLines[i]);
            string f = Lead(fileLines[i]);
            if (map.TryGetValue(m, out var seen) && seen != f) return replacement;   // inconsistent: don't guess
            map[m] = f;
            if (m != f) anyDifference = true;
            if (m.Contains('\t') || f.Contains('\t')) spacesOnly = false;
            pairs.Add((m.Length, f.Length));
        }
        if (!anyDifference || pairs.Count == 0) return replacement;

        // A linear rule (file = a·model + b) covers the two common mistakes — a dropped base indent
        // (a = 1) and 2-space levels in a 4-space file (a = 2) — for replacement lines whose indent
        // never appeared in the matched block (e.g. a newly added nested line).
        (int a, int b)? rule = spacesOnly ? FitLinear(pairs) : null;
        char indentChar = map.Values.Any(v => v.Contains('\t')) ? '\t' : ' ';

        var lines = replacement.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0) continue;
            string lead = Lead(line);
            string body = line[lead.Length..];

            if (map.TryGetValue(lead, out var mapped))
                lines[i] = mapped + body;
            else if (rule is { } r && !lead.Contains('\t'))
                lines[i] = new string(indentChar, Math.Max(0, r.a * lead.Length + r.b)) + body;
        }
        return string.Join("\n", lines);
    }

    private static string Lead(string line)
    {
        int n = 0;
        while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
        return line[..n];
    }

    /// <summary>Integer a ∈ {1,2,3,4} and b such that every pair satisfies file = a·model + b, or null.</summary>
    private static (int a, int b)? FitLinear(List<(int model, int file)> pairs)
    {
        foreach (int a in new[] { 1, 2, 3, 4 })
        {
            int b = pairs[0].file - a * pairs[0].model;
            if (pairs.All(p => p.file == a * p.model + b)) return (a, b);
        }
        return null;
    }
}
