using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CluadeX.Services.AutoComment;

/// <summary>One comment the model proposes: the 1-based line it goes above, and plain text (no comment markers).</summary>
public sealed record ProposedComment(int Line, string Text);

/// <summary>The model's answer read back. <see cref="Refused"/>: the answer tried to change code (or was not in the
/// format at all) and nothing from it is used.</summary>
public sealed record AnswerParse(bool Refused, string? RefuseKey, string? RefuseDetail, List<ProposedComment> Comments);

/// <summary>A proposal that was not placed, and why (a <c>edt.acm.skip.*</c> localization key).</summary>
public sealed record SkippedComment(int Line, string Text, string ReasonKey);

/// <summary>What inserting the comments produced: the new text, which of its lines are the inserted ones (0-based in
/// <see cref="Result"/>), what was placed and what was skipped.</summary>
public sealed class CommentPlan
{
    public required string Original { get; init; }
    public required string Result { get; init; }
    public required List<int> Inserted { get; init; }
    public required List<ProposedComment> Placed { get; init; }
    public required List<SkippedComment> Skipped { get; init; }
    public int Count => Placed.Count;
}

/// <summary>
/// The only code that writes comments into a file. The model never edits: it names a line and gives text; this class
/// renders the comment in the language's own syntax, indents it like the line it explains, and inserts it as whole
/// lines. <see cref="Verify"/> then proves the code did not change — stripping exactly those lines gives the original
/// back character for character, and the lexer sees every original line start in the same state as before.
/// </summary>
public static class CommentPlanner
{
    public const int MaxTextChars = 600, MaxTextLines = 8;

    /// <summary>Keys that mean "change the code" — an answer carrying any of them is refused whole.</summary>
    private static readonly HashSet<string> EditKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "new_code", "newCode", "replace", "replacement", "with", "old", "new", "old_string", "new_string", "patch",
        "diff", "edit", "edits", "content", "new_content", "file", "file_content", "insert_code", "delete", "remove",
        "before", "after", "source", "rewrite", "fixed_code",
    };

    private static readonly Regex Fence = new(@"^```[\w-]*\s*|\s*```\s*$", RegexOptions.Multiline);

    /// <summary>Read the model's answer: <c>{"comments":[{"line":N,"text":"…"}]}</c> (a bare array works too, and the
    /// JSON may sit in a code fence or after some prose). Refuses an answer that carries code edits or text that would
    /// break out of a comment in <paramref name="lang"/>.</summary>
    public static AnswerParse ParseAnswer(string? raw, CommentLanguage lang)
    {
        var list = new List<ProposedComment>();
        AnswerParse Refuse(string key, string? detail = null) => new(true, key, detail, new List<ProposedComment>());

        string s = Fence.Replace((raw ?? "").Trim(), "").Trim();
        int o = s.IndexOfAny(new[] { '{', '[' });
        if (o < 0) return Refuse("edt.acm.refuse.format");
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(s[o..], new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Prose after the JSON: cut at the last closing bracket and try once more.
            int c = Math.Max(s.LastIndexOf('}'), s.LastIndexOf(']'));
            try
            {
                using var doc = JsonDocument.Parse(s[o..(c + 1)]);
                root = doc.RootElement.Clone();
            }
            catch (Exception) { return Refuse("edt.acm.refuse.format"); }
        }

        JsonElement items;
        if (root.ValueKind == JsonValueKind.Array) items = root;
        else if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in root.EnumerateObject())
                if (EditKeys.Contains(p.Name)) return Refuse("edt.acm.refuse.edit", p.Name);
            if (!root.TryGetProperty("comments", out items) || items.ValueKind != JsonValueKind.Array)
                return Refuse("edt.acm.refuse.format");
        }
        else return Refuse("edt.acm.refuse.format");

        foreach (var it in items.EnumerateArray())
        {
            if (it.ValueKind != JsonValueKind.Object) return Refuse("edt.acm.refuse.format");
            int line = 0; string? text = null;
            foreach (var p in it.EnumerateObject())
            {
                if (EditKeys.Contains(p.Name)) return Refuse("edt.acm.refuse.edit", p.Name);
                if (p.NameEquals("line"))
                {
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int n)) line = n;
                    else if (p.Value.ValueKind == JsonValueKind.String && int.TryParse(p.Value.GetString(), out int m)) line = m;
                }
                else if (p.NameEquals("text") || p.NameEquals("comment")) text = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
            }
            if (text == null) continue;
            string? escape = EscapeAttempt(text, lang);
            if (escape != null) return Refuse("edt.acm.refuse.escape", escape);
            list.Add(new ProposedComment(line, text));
        }
        return new AnswerParse(false, null, null, list);
    }

    /// <summary>Text that would end the comment early and turn the rest of the line into code — or hide code from a
    /// reader (bidi controls, the "Trojan Source" trick). Returns what was found, or null.</summary>
    public static string? EscapeAttempt(string text, CommentLanguage lang)
    {
        foreach (char c in text)
        {
            if (c is (char)0x2028 or (char)0x2029 or (char)0x85) return $"U+{(int)c:X4} (line separator)";
            if (c is >= (char)0x202A and <= (char)0x202E or >= (char)0x2066 and <= (char)0x2069) return $"U+{(int)c:X4} (bidi control)";
            if (char.IsControl(c) && c is not ('\n' or '\r' or '\t')) return $"U+{(int)c:X4} (control character)";
        }
        if (lang.IsXml && text.Contains("-->")) return "-->";
        if (lang.Kind == LexKind.Php && text.Contains("?>")) return "?>";
        return null;
    }

    /// <summary>Words that tools read out of comments (linters, type checkers, coverage, Go's build constraints, a
    /// Python coding cookie). A comment carrying one would change what the tools do with the code, so it is skipped.</summary>
    private static readonly string[] Directives =
    {
        "noqa", "type: ignore", "type:ignore", "pragma", "pylint:", "mypy:", "fmt: off", "fmt: on", "fmt: skip", "isort:",
        "eslint", "prettier-ignore", "@ts-", "@flow", "@jsx", "istanbul ignore", "c8 ignore", "nolint", "+build", "go:build",
        "go:generate", "go:embed", "go:linkname", "resharper", "<auto-generated", "nosonar", "@phpstan", "@psalm",
        "ignore_for_file", "ignore:", "coverage:ignore", "biome-ignore", "deno-lint-ignore", "coding:", "coding=", "<reference",
    };

    public static string? DirectiveIn(string text)
    {
        foreach (var d in Directives)
            if (text.Contains(d, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    private static readonly Regex LeadingMarker = new(@"^\s*(?://+|#+|/\*+|\*+/?|<!--)\s?", RegexOptions.Compiled);

    /// <summary>The comment's lines, ready to render: split on line breaks, markers the model added removed, tabs to spaces,
    /// XML's forbidden "--" softened, a trailing backslash (which would splice the next line in C) defused.</summary>
    public static List<string> CleanLines(string text, CommentLanguage lang)
    {
        if (text.Length > MaxTextChars) text = text[..MaxTextChars].TrimEnd() + "…";
        var lines = new List<string>();
        foreach (var raw in Regex.Split(text.Trim(), "\r\n|\n|\r"))
        {
            string t = LeadingMarker.Replace(raw, "");
            t = t.Replace("*/", "* /").Replace('\t', ' ').TrimEnd();
            if (t.EndsWith("-->")) t = t[..^3].TrimEnd();
            if (lang.IsXml) while (t.Contains("--")) t = t.Replace("--", "—");   // Render pads with spaces, so a final "-" is safe
            if (t.EndsWith('\\')) t += ".";
            if (t.Trim().Length == 0) continue;
            lines.Add(t.Trim());
            if (lines.Count == MaxTextLines) break;
        }
        return lines;
    }

    private static string IndentOf(string line)
    {
        int n = 0;
        while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
        return line[..n];
    }

    /// <summary>The line ending the file mostly uses ("\n" when it has none).</summary>
    public static string DominantEnding(IReadOnlyList<string> endings)
    {
        var g = endings.Where(e => e.Length > 0).GroupBy(e => e).OrderByDescending(x => x.Count()).FirstOrDefault();
        return g?.Key ?? "\n";
    }

    /// <summary>Place <paramref name="proposals"/> into <paramref name="original"/>. Only lines in <paramref name="allowed"/>
    /// (0-based, already insertable) take a comment; everything else is skipped with a reason.</summary>
    public static CommentPlan Plan(string original, CommentLanguage lang, LexResult lex, IEnumerable<ProposedComment> proposals,
        ISet<int> allowed, IReadOnlyDictionary<int, int>? anchorRemap, int maxComments)
    {
        var skipped = new List<SkippedComment>();
        var byLine = new SortedDictionary<int, List<(ProposedComment P, List<string> Lines)>>();
        var placed = new List<ProposedComment>();

        foreach (var p in proposals)
        {
            int k = p.Line - 1;
            if (k < 0 || k >= lex.Count) { skipped.Add(new(p.Line, p.Text, "edt.acm.skip.range")); continue; }
            if (anchorRemap != null && anchorRemap.TryGetValue(k, out int moved)) k = moved;
            if (!lex.Info[k].Insertable) { skipped.Add(new(p.Line, p.Text, "edt.acm.skip.notCode")); continue; }
            if (lex.Info[k].Blank) { skipped.Add(new(p.Line, p.Text, "edt.acm.skip.blank")); continue; }
            if (!allowed.Contains(k)) { skipped.Add(new(p.Line, p.Text, "edt.acm.skip.scope")); continue; }
            if (DirectiveIn(p.Text) != null) { skipped.Add(new(p.Line, p.Text, "edt.acm.skip.directive")); continue; }
            var lines = CleanLines(p.Text, lang);
            if (lines.Count == 0) { skipped.Add(new(p.Line, p.Text, "edt.acm.skip.empty")); continue; }

            if (!byLine.TryGetValue(k, out var at)) byLine[k] = at = new();
            string joined = string.Join("\n", lines);
            bool dup = at.Any(x => string.Join("\n", x.Lines) == joined)
                       || (k > 0 && lines.Count == 1 && lex.Lines[k - 1].Trim() == lang.Render(lines[0]).Trim());
            if (dup) { skipped.Add(new(p.Line, p.Text, "edt.acm.skip.duplicate")); continue; }
            if (placed.Count >= maxComments) { skipped.Add(new(p.Line, p.Text, "edt.acm.skip.limit")); continue; }
            at.Add((p, lines));
            placed.Add(p);
        }

        string eol = DominantEnding(lex.Endings);
        var sb = new StringBuilder(original.Length + placed.Count * 80);
        var inserted = new List<int>();
        int outLine = 0;
        for (int k = 0; k < lex.Count; k++)
        {
            if (byLine.TryGetValue(k, out var at) && at.Count > 0)
            {
                string indent = IndentOf(lex.Lines[k]);
                foreach (var (_, lines) in at)
                    foreach (var l in lines)
                    {
                        sb.Append(indent).Append(lang.Render(l)).Append(eol);
                        inserted.Add(outLine++);
                    }
            }
            sb.Append(lex.Lines[k]).Append(lex.Endings[k]);
            outLine++;
        }
        return new CommentPlan { Original = original, Result = sb.ToString(), Inserted = inserted, Placed = placed, Skipped = skipped };
    }

    /// <summary>Remove the lines at <paramref name="inserted"/> (0-based) from <paramref name="text"/>.</summary>
    public static string Strip(string text, IReadOnlyCollection<int> inserted)
    {
        var (lines, endings, _) = CommentLexer.Split(text);
        var drop = inserted as ISet<int> ?? new HashSet<int>(inserted);
        var sb = new StringBuilder(text.Length);
        for (int k = 0; k < lines.Count; k++)
            if (!drop.Contains(k)) sb.Append(lines[k]).Append(endings[k]);
        return sb.ToString();
    }

    /// <summary>
    /// Proof the code is untouched (null = proven, else what failed):
    /// 1. stripping the inserted lines gives the original back, character for character;
    /// 2. every inserted line is a single comment of the language, starting in plain code;
    /// 3. lexed again, every original line starts in exactly the state it did before (same insertability, brace depth
    ///    and bracket depth) — so no comment opened or closed a string, a block or a comment;
    /// 4. no inserted line became a header the language reads (a Python coding cookie, a shebang).
    /// </summary>
    public static string? Verify(CommentPlan plan, CommentLanguage lang)
    {
        if (!string.Equals(Strip(plan.Result, plan.Inserted), plan.Original, StringComparison.Ordinal))
            return "strip-mismatch";
        var before = CommentLexer.Lex(plan.Original, lang);
        var after = CommentLexer.Lex(plan.Result, lang);
        var ins = new HashSet<int>(plan.Inserted);
        if (after.Count != before.Count + ins.Count) return "line-count";
        int o = 0;
        for (int k = 0; k < after.Count; k++)
        {
            if (ins.Contains(k))
            {
                string t = after.Lines[k].TrimStart();
                bool shaped = lang.IsXml ? t.StartsWith("<!--") && t.EndsWith("-->") && !t[4..^3].Contains("--")
                                         : t.StartsWith(lang.LinePrefix + " ");
                if (!shaped) return $"not-a-comment@{k + 1}";
                if (!after.Info[k].Insertable) return $"inserted-outside-code@{k + 1}";
                continue;
            }
            var a = before.Info[o]; var b = after.Info[k];
            if (a.Insertable != b.Insertable || a.Depth != b.Depth || a.Brackets != b.Brackets)
                return $"lexer-state-changed@{o + 1}";
            o++;
        }
        return null;
    }
}
