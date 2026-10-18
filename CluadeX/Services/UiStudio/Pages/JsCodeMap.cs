namespace CluadeX.Services.UiStudio.Pages;

/// <summary>
/// The bracket structure of a JS/TS file, read the way the language does: strings, template literals (with their
/// <c>${…}</c> holes), comments and regex literals are not code, so a <c>{</c> inside them never counts. For every
/// position it knows the innermost open bracket around it — enough to tell which object a <c>path:</c> key belongs
/// to and which <c>children: [</c> array that object sits in, without a full parser.
///
/// Two tolerances keep a JSX file readable: a quote that does not close on its own line is a plain character
/// (an apostrophe in JSX text: "Don't"), and <c>&lt;/</c> starts a closing tag, never a regex.
/// With <c>php: true</c> it reads PHP instead (Laravel route files): strings may span lines, <c>#</c> starts a
/// comment (not <c>#[</c>, an attribute), and there are no template or regex literals.
/// </summary>
internal sealed class JsCodeMap
{
    public string Text { get; }
    /// <summary>False inside strings, template text, comments and regex literals.</summary>
    public bool[] IsCode { get; }
    /// <summary>The innermost open bracket around each position (-1: top level). For a bracket character itself:
    /// the bracket around it.</summary>
    public int[] Inner { get; }
    /// <summary>Open bracket → its closing bracket (missing when unbalanced).</summary>
    public Dictionary<int, int> Close { get; } = new();

    public JsCodeMap(string text, bool php = false)
    {
        Text = text;
        int n = text.Length;
        IsCode = new bool[n];
        Inner = new int[n];
        var stack = new Stack<int>();
        // Template literals nest: `a ${ `b ${c}` } d`. Each entry is the bracket-stack depth where a ${ hole opened.
        var holes = new Stack<int>();
        bool inTemplate = false;
        var templates = new Stack<bool>();   // the inTemplate state to return to when a hole closes
        char lastSig = '\0';
        string lastWord = "";

        int Top() => stack.Count > 0 ? stack.Peek() : -1;

        int i = 0;
        while (i < n)
        {
            char c = text[i];
            if (inTemplate)
            {
                Inner[i] = Top();
                if (c == '\\') { Mark(i, 2, false); i += 2; continue; }
                if (c == '`') { inTemplate = false; i++; lastSig = '`'; continue; }
                if (c == '$' && i + 1 < n && text[i + 1] == '{')
                {
                    Inner[i + 1] = Top();
                    IsCode[i + 1] = true;
                    stack.Push(i + 1);
                    holes.Push(stack.Count);
                    templates.Push(true);
                    inTemplate = false;
                    i += 2;
                    lastSig = '{';
                    continue;
                }
                i++;
                continue;
            }

            // ── code ──
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                int e = text.IndexOf('\n', i);
                if (e < 0) e = n;
                Mark(i, e - i, false);
                i = e;
                continue;
            }
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                int e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                e = e < 0 ? n : e + 2;
                Mark(i, e - i, false);
                i = e;
                continue;
            }
            if (php && c == '#' && !(i + 1 < n && text[i + 1] == '['))
            {
                int e = text.IndexOf('\n', i);
                if (e < 0) e = n;
                Mark(i, e - i, false);
                i = e;
                continue;
            }
            if (c is '\'' or '"')
            {
                int e = StringEnd(text, i, c, multiLine: php);
                if (e > 0)
                {
                    Mark(i, e - i, false);
                    i = e;
                    lastSig = c;
                    continue;
                }
                // no closing quote on this line: an apostrophe in JSX text
            }
            if (c == '`' && !php)
            {
                Inner[i] = Top();
                inTemplate = true;
                i++;
                continue;
            }
            if (c == '/' && !php && IsRegexStart(lastSig, lastWord))
            {
                int e = RegexEnd(text, i);
                if (e > 0)
                {
                    Mark(i, e - i, false);
                    i = e;
                    lastSig = '/';
                    continue;
                }
            }

            Inner[i] = Top();
            IsCode[i] = true;
            if (c is '(' or '[' or '{')
            {
                stack.Push(i);
            }
            else if (c is ')' or ']' or '}')
            {
                if (stack.Count > 0)
                {
                    int open = stack.Pop();
                    Close[open] = i;
                    Inner[i] = Top();
                    if (c == '}' && holes.Count > 0 && holes.Peek() == stack.Count + 1)
                    {
                        holes.Pop();
                        inTemplate = templates.Pop();
                    }
                }
            }

            if (char.IsLetterOrDigit(c) || c is '_' or '$')
            {
                int s = i;
                while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$')) { Inner[i] = Top(); IsCode[i] = true; i++; }
                lastWord = text[s..i];
                lastSig = 'a';
                continue;
            }
            if (!char.IsWhiteSpace(c)) { lastSig = c; lastWord = ""; }
            i++;
        }

        void Mark(int start, int len, bool code)
        {
            int top = Top();
            for (int k = start; k < start + len && k < n; k++) { IsCode[k] = code; Inner[k] = top; }
        }
    }

    /// <summary>Index just past the closing quote (on the same line unless <paramref name="multiLine"/>), or -1.</summary>
    private static int StringEnd(string t, int start, char q, bool multiLine)
    {
        for (int k = start + 1; k < t.Length; k++)
        {
            char c = t[k];
            if (c == '\\') { k++; continue; }
            if (c == q) return k + 1;
            if (c == '\n' && !multiLine) return -1;
        }
        return -1;
    }

    private static readonly HashSet<string> RegexKeywords = new(StringComparer.Ordinal)
        { "return", "typeof", "case", "do", "else", "in", "of", "yield", "await", "void", "delete", "new", "throw" };

    private static bool IsRegexStart(char lastSig, string lastWord)
    {
        if (lastSig == '<') return false;                 // "</div>" — a closing tag
        if (lastSig == 'a') return RegexKeywords.Contains(lastWord);
        return lastSig is '\0' or '(' or ',' or '=' or ':' or '[' or '!' or '&' or '|' or '?' or '{' or '}' or ';' or '+' or '-' or '*' or '%' or '~' or '^' or '>';
    }

    /// <summary>Index just past a regex literal starting at <paramref name="start"/> (same line), or -1.</summary>
    private static int RegexEnd(string t, int start)
    {
        if (start + 1 >= t.Length || t[start + 1] is '/' or '*' or ' ' or '\n') return -1;
        bool inClass = false;
        for (int k = start + 1; k < t.Length; k++)
        {
            char c = t[k];
            if (c == '\n') return -1;
            if (c == '\\') { k++; continue; }
            if (c == '[') inClass = true;
            else if (c == ']') inClass = false;
            else if (c == '/' && !inClass)
            {
                k++;
                while (k < t.Length && char.IsLetter(t[k])) k++;   // flags
                return k;
            }
        }
        return -1;
    }

    /// <summary>The open bracket around <paramref name="open"/> (-1 at the top).</summary>
    public int Parent(int open) => open >= 0 && open < Inner.Length ? Inner[open] : -1;

    public int CloseOf(int open) => Close.TryGetValue(open, out var c) ? c : Text.Length;

    /// <summary>The key written right before the bracket at <paramref name="open"/> ("children" in
    /// <c>children: [</c>), or null.</summary>
    public string? KeyBefore(int open)
    {
        int k = open - 1;
        while (k >= 0 && char.IsWhiteSpace(Text[k])) k--;
        if (k < 0 || Text[k] != ':') return null;
        k--;
        while (k >= 0 && char.IsWhiteSpace(Text[k])) k--;
        int end = k + 1;
        if (k >= 0 && Text[k] is '\'' or '"') { k--; end--; }   // 'children': [
        while (k >= 0 && (char.IsLetterOrDigit(Text[k]) || Text[k] is '_' or '$')) k--;
        return end > k + 1 ? Text[(k + 1)..end] : null;
    }

    /// <summary>A string literal ('…', "…" or a `…` with no holes) starting at <paramref name="at"/> (after
    /// whitespace), or null.</summary>
    public string? LiteralAt(int at, out int end)
    {
        end = at;
        while (at < Text.Length && char.IsWhiteSpace(Text[at])) at++;
        if (at >= Text.Length) return null;
        char q = Text[at];
        if (q is not ('\'' or '"' or '`')) return null;
        var sb = new System.Text.StringBuilder();
        for (int k = at + 1; k < Text.Length; k++)
        {
            char c = Text[k];
            if (c == '\\' && k + 1 < Text.Length) { sb.Append(Text[++k]); continue; }
            if (c == q) { end = k + 1; return sb.ToString(); }
            if (q == '`' && c == '$' && k + 1 < Text.Length && Text[k + 1] == '{') return null;
            if (c == '\n' && q != '`') return null;
            sb.Append(c);
        }
        return null;
    }
}
