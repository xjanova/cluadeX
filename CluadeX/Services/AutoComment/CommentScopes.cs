using System.Text.RegularExpressions;

namespace CluadeX.Services.AutoComment;

/// <summary>Which lines auto-comment may comment (owner 2026-11-01: every function, only what changed, only the key
/// points — plus the whole file and the selection).</summary>
public enum CommentScopeKind { Functions, Changed, KeyPoints, File, Selection }

/// <summary>One function / method / class: <see cref="Header"/> is its declaration line, <see cref="Start"/> the first
/// line of its attributes or decorators (a comment for the function goes above those), <see cref="End"/> its last line.
/// All 0-based. <see cref="IsType"/>: a class / struct / interface — only its header is in the functions scope.</summary>
public sealed record FunctionSpan(int Header, int Start, int End, bool IsType);

public static class CommentScopes
{
    private const RegexOptions O = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private const string NotKeyword =
        @"(?!(?:if|else|for|foreach|while|do|switch|case|catch|try|finally|return|throw|using|lock|fixed|checked|unchecked|new|await|yield|goto|sizeof|typeof|nameof|default|when|with|var|let|const|get|set|init|add|remove|delete|import|package|in|is|as|and|or|not)\b)";

    // A declaration: at least one type / modifier word before the name, then "(" — calls and statements don't have one.
    private static readonly Regex CLikeFunc = new(@"^\s*" + NotKeyword + @"(?:[\w<>\[\],.?*&:@]+\s+)+~?[A-Za-z_]\w*\s*(?:<[^()]*>)?\s*\(", O);
    private static readonly Regex TypeDecl = new(@"^\s*(?:[\w]+\s+)*(?:class|struct|interface|record|enum|trait|mixin|extension|namespace)\s+[A-Za-z_]", O);
    private static readonly Regex JsFunc = new(
        @"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?function\b" +
        @"|^\s*(?:export\s+)?(?:const|let|var)\s+[\w$]+\s*(?::[^=]+)?=\s*(?:async\s+)?(?:function\b|\([^)]*\)\s*(?::[^=]+)?=>|[\w$]+\s*=>)" +
        @"|^\s*" + NotKeyword + @"(?:(?:public|private|protected|static|async|readonly|override|abstract|get|set)\s+)*\*?[\w$]+\s*(?:<[^>]*>)?\s*\([^;]*\)\s*(?::\s*[^{;]+)?\{\s*$", O);
    private static readonly Regex PhpFunc = new(@"^\s*(?:(?:public|private|protected|static|abstract|final)\s+)*function\s+&?\w+", O);
    private static readonly Regex GoFunc = new(@"^\s*func\b", O);
    private static readonly Regex GoType = new(@"^\s*type\s+\w+\s+(?:struct|interface)\b", O);
    private static readonly Regex PyFunc = new(@"^\s*(?:async\s+)?def\s+\w+", O);
    private static readonly Regex PyClass = new(@"^\s*class\s+\w+", O);

    /// <summary>Functions, methods and types of a file, found from declaration shapes plus the lexer's brace depth
    /// (C-family) or indentation (Python). XML has none.</summary>
    public static List<FunctionSpan> FindFunctions(LexResult lex, CommentLanguage lang)
    {
        var list = new List<FunctionSpan>();
        if (lang.IsXml) return list;
        for (int h = 0; h < lex.Count; h++)
        {
            if (!lex.Info[h].Insertable || lex.Info[h].Blank) continue;
            string l = lex.Lines[h];
            bool isType, isFunc;
            switch (lang.Kind)
            {
                case LexKind.Python:
                    isType = PyClass.IsMatch(l); isFunc = PyFunc.IsMatch(l); break;
                case LexKind.JavaScript: case LexKind.TypeScript:
                    isType = TypeDecl.IsMatch(l); isFunc = !isType && JsFunc.IsMatch(l); break;
                case LexKind.Php:
                    isType = TypeDecl.IsMatch(l); isFunc = !isType && PhpFunc.IsMatch(l); break;
                case LexKind.Go:
                    isType = GoType.IsMatch(l); isFunc = GoFunc.IsMatch(l); break;
                default:
                    isType = TypeDecl.IsMatch(l);
                    isFunc = !isType && CLikeFunc.IsMatch(l) && !HasAssignmentBeforeParen(l);
                    break;
            }
            if (!isType && !isFunc) continue;
            if (l.TrimEnd().EndsWith(';') && !l.Contains("=>")) continue;   // a prototype or forward declaration

            int end = lang.Kind == LexKind.Python ? PythonEnd(lex, h) : BraceEnd(lex, h, l);
            if (end < 0) continue;
            list.Add(new FunctionSpan(h, AttributeStart(lex, lang, h), end, isType));
        }
        return list;
    }

    private static bool HasAssignmentBeforeParen(string l)
    {
        int p = l.IndexOf('(');
        string head = p < 0 ? l : l[..p];
        return Regex.IsMatch(head, @"(?<![=!<>])=(?!=)") || head.Contains("=>");
    }

    /// <summary>The body ends where the brace depth falls back to the header's. No body within a few lines (an
    /// abstract member, an expression body) = the declaration up to its ';'.</summary>
    private static int BraceEnd(LexResult lex, int h, string headerLine)
    {
        int d = lex.Info[h].Depth;
        int k = h + 1;
        for (; k < lex.Count && k <= h + 8; k++)
        {
            if (lex.Info[k].Depth > d) break;
            if (lex.Lines[k - 1].TrimEnd().EndsWith(';')) return k - 1;
        }
        if (k >= lex.Count || lex.Info[k].Depth <= d)
            return headerLine.TrimEnd().EndsWith(';') || headerLine.Contains("=>") ? h : -1;
        for (; k < lex.Count; k++)
            if (lex.Info[k].Depth <= d) return k - 1;
        return lex.Count - 1;
    }

    private static int Indent(string l) { int n = 0; foreach (char c in l) { if (c == ' ') n++; else if (c == '\t') n += 4; else break; } return n; }

    private static int PythonEnd(LexResult lex, int h)
    {
        int ind = Indent(lex.Lines[h]), last = h;
        for (int k = h + 1; k < lex.Count; k++)
        {
            var i = lex.Info[k];
            string t = lex.Lines[k].TrimStart();
            if (i.Blank || t.StartsWith('#')) continue;
            if (i.Insertable && i.Brackets == 0 && Indent(lex.Lines[k]) <= ind) break;
            last = k;
        }
        return last;
    }

    /// <summary>Walk up over attributes / decorators / annotations: the function's comment goes above them.</summary>
    private static int AttributeStart(LexResult lex, CommentLanguage lang, int h)
    {
        int k = h;
        while (k > 0 && lex.Info[k - 1].Insertable && !lex.Info[k - 1].Blank)
        {
            string t = lex.Lines[k - 1].TrimStart();
            bool attr = lang.Kind switch
            {
                LexKind.CSharp => t.StartsWith('[') && t.TrimEnd().EndsWith(']'),
                LexKind.Php => t.StartsWith("#["),
                LexKind.Python or LexKind.Java or LexKind.TypeScript or LexKind.JavaScript or LexKind.Dart => t.StartsWith('@'),
                _ => false,
            };
            if (!attr) break;
            k--;
        }
        return k;
    }

    /// <summary>The lines (0-based) a comment may go above for <paramref name="scope"/>: always lines the lexer calls
    /// insertable and not blank. <paramref name="selection"/> is 1-based inclusive; <paramref name="changedLines"/> are
    /// 1-based new-file lines (null for an untracked file = every line).</summary>
    public static SortedSet<int> AllowedLines(LexResult lex, CommentLanguage lang, CommentScopeKind scope,
        (int First, int Last)? selection = null, IReadOnlyCollection<int>? changedLines = null)
    {
        var set = new SortedSet<int>();
        void Add(int k) { if (k >= 0 && k < lex.Count && lex.Info[k].Insertable && !lex.Info[k].Blank) set.Add(k); }
        switch (scope)
        {
            case CommentScopeKind.File:
            case CommentScopeKind.KeyPoints:
                for (int k = 0; k < lex.Count; k++) Add(k);
                break;
            case CommentScopeKind.Selection:
                if (selection is { } s)
                    for (int k = Math.Max(1, s.First); k <= Math.Min(lex.Count, s.Last); k++) Add(k - 1);
                break;
            case CommentScopeKind.Changed:
                if (changedLines == null) { for (int k = 0; k < lex.Count; k++) Add(k); }
                else foreach (int line in changedLines) Add(line - 1);
                break;
            case CommentScopeKind.Functions:
                if (lang.IsXml) { for (int k = 0; k < lex.Count; k++) Add(k); break; }
                foreach (var f in FindFunctions(lex, lang))
                {
                    if (f.IsType) { Add(f.Start); continue; }
                    for (int k = f.Start; k <= f.End; k++) Add(k);
                }
                break;
        }
        return set;
    }

    /// <summary>Header line → where its comment really goes (above attributes / decorators).</summary>
    public static Dictionary<int, int> AnchorRemap(LexResult lex, CommentLanguage lang) =>
        FindFunctions(lex, lang).Where(f => f.Start != f.Header).ToDictionary(f => f.Header, f => f.Start);

    /// <summary>How many comments a scope asks for at most: key points stay few; the rest are bounded only so a runaway
    /// answer cannot bury the file.</summary>
    public static int MaxComments(CommentScopeKind scope, int allowedCount, int lineCount) => scope == CommentScopeKind.KeyPoints
        ? Math.Clamp(lineCount / 25, 3, 40)
        : Math.Clamp(allowedCount, 1, 600);

    private static readonly Regex Hunk = new(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", RegexOptions.Multiline | O);

    /// <summary>New-file line numbers (1-based) that a unified diff (<c>git diff -U0</c>) adds or changes. Pure deletions
    /// add nothing — there is no line left to comment.</summary>
    public static SortedSet<int> ParseChangedLines(string unifiedDiff)
    {
        var set = new SortedSet<int>();
        foreach (Match m in Hunk.Matches(unifiedDiff ?? ""))
        {
            int start = int.Parse(m.Groups[1].Value);
            int count = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
            for (int k = 0; k < count; k++) set.Add(start + k);
        }
        return set;
    }

    /// <summary>"3-10, 14, 20-31" (1-based) for the prompt.</summary>
    public static string Ranges(IEnumerable<int> zeroBased)
    {
        var parts = new List<string>();
        int? a = null, b = null;
        foreach (int z in zeroBased)
        {
            int n = z + 1;
            if (a == null) { a = b = n; continue; }
            if (n == b + 1) { b = n; continue; }
            parts.Add(a == b ? $"{a}" : $"{a}-{b}");
            a = b = n;
        }
        if (a != null) parts.Add(a == b ? $"{a}" : $"{a}-{b}");
        return string.Join(", ", parts);
    }
}
