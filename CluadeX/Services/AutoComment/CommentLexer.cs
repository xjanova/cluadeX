using System.Text.RegularExpressions;

namespace CluadeX.Services.AutoComment;

/// <summary>What the lexer knows about the start of one line (0-based index in <see cref="LexResult.Lines"/>).</summary>
/// <param name="Insertable">A whole comment line may go right before this line without changing what the code means:
/// the line starts in plain code (not inside a string, interpolation, comment, heredoc, JSX, XML tag or PHP's HTML),
/// the previous line does not continue into it with a backslash, and it is not a header that must stay first.</param>
/// <param name="Depth">Brace depth of the file's top-level code at the line start (C-family), element depth (XML).</param>
/// <param name="Brackets">Open ( [ { at the line start (Python: a line inside brackets is a continuation).</param>
/// <param name="Blank">Whitespace only.</param>
public readonly record struct LineInfo(bool Insertable, int Depth, int Brackets, bool Blank);

/// <summary>A file split into lines (each keeps its own line ending) plus the lexer's view of every line start.</summary>
public sealed class LexResult
{
    public required IReadOnlyList<string> Lines { get; init; }        // text without the ending
    public required IReadOnlyList<string> Endings { get; init; }      // "\r\n", "\n", "\r" or "" (last line)
    public required LineInfo[] Info { get; init; }
    public int Count => Lines.Count;
}

/// <summary>
/// A small lexer for auto-comment: it only has to answer "is the start of this line plain code?" for every line, so
/// a comment inserted there can never land inside a string, a comment, an interpolation or markup. It follows the
/// constructs that span lines — C# verbatim / raw / interpolated strings (with holes that hold strings of their own),
/// Python triple-quoted and f-strings, JS template literals and JSX, Dart triple and raw strings, PHP's HTML mode,
/// multi-line strings and heredocs, Java text blocks, Go raw strings, C++ raw strings, nested block comments, and
/// XML tags, comments and CDATA. When unsure it says "not insertable": fewer comments, never broken code.
/// </summary>
public static class CommentLexer
{
    public static LexResult Lex(string text, CommentLanguage lang)
    {
        var (lines, endings, starts) = Split(text);
        var info = lang.IsXml ? new XmlLexer(text, starts, lines).Run() : new CodeLexer(text, starts, lines, lang).Run();
        ApplyHeaderRules(lines, info, lang);
        return new LexResult { Lines = lines, Endings = endings, Info = info };
    }

    /// <summary>Lines with their own endings: "\r\n", "\n" or a lone "\r". A file that ends with a newline has no
    /// extra empty line; "" is zero lines.</summary>
    public static (List<string> Lines, List<string> Endings, int[] Starts) Split(string text)
    {
        var lines = new List<string>(); var endings = new List<string>(); var starts = new List<int>();
        int i = 0, n = text.Length;
        while (i < n)
        {
            int s = i;
            while (i < n && text[i] != '\n' && text[i] != '\r') i++;
            starts.Add(s);
            lines.Add(text[s..i]);
            if (i >= n) { endings.Add(""); break; }
            if (text[i] == '\r' && i + 1 < n && text[i + 1] == '\n') { endings.Add("\r\n"); i += 2; }
            else { endings.Add(text[i].ToString()); i++; }
        }
        return (lines, endings, starts.ToArray());
    }

    /// <summary>U+FEFF, written as a number so no tool can turn it into the invisible character itself.</summary>
    public const char Bom = (char)0xFEFF;

    private static readonly Regex PyCodingCookie = new(@"^[ \t\f]*#.*?coding[:=][ \t]*[-\w.]+", RegexOptions.Compiled);

    /// <summary>Lines that must stay where they are: a shebang stays line 1, a Python coding cookie stays in lines 1–2,
    /// the XML declaration stays first, and Go's cgo preamble must touch its <c>import "C"</c>.</summary>
    private static void ApplyHeaderRules(List<string> lines, LineInfo[] info, CommentLanguage lang)
    {
        if (lines.Count == 0) return;
        void Block(int k) { if (k < info.Length) info[k] = info[k] with { Insertable = false }; }
        if (lines[0].StartsWith("#!") || lines[0].StartsWith(Bom)) Block(0);
        if (lang.Kind == LexKind.Python)
            for (int k = 0; k < Math.Min(2, lines.Count); k++)
                if (PyCodingCookie.IsMatch(lines[k])) for (int j = 0; j <= k; j++) Block(j);
        if (lang.IsXml && lines[0].TrimStart(Bom).StartsWith("<?xml", StringComparison.Ordinal)) Block(0);
        if (lang.Kind == LexKind.Go)
            for (int k = 0; k < lines.Count; k++)
                if (Regex.IsMatch(lines[k], @"^\s*import\s+""C""")) Block(k);
    }

    // ═══════════════════════════════ code languages ═══════════════════════════════

    private enum FK { Code, Hole, Str, Block, LineCmt, Html, Heredoc, JsxTag, JsxChildren }

    private sealed class Frame
    {
        public FK Kind;
        public bool Root;              // the file's top-level code frame (PHP: each <?php … ?> block)
        public int Braces, Brackets;   // code / hole
        public int CloseCount = 1;     // hole: how many '}' close it (C# $$""" … """ raw strings)
        public char Quote;             // string
        public int QuoteCount = 1;     // C# raw strings: the opening run of quotes
        public bool Escapes, Interp, Verbatim, MultiLine, Triple, Raw, ContinueLine;
        public int Dollars = 1;        // C# raw interpolation: braces per hole
        public string? Delim;          // C++ raw string end / PHP heredoc id
        public int Nest;               // nested block comments (Dart)
        public bool Closing;           // JSX closing tag
        public char LastSig;           // last significant char in this code frame ('a' = a word / number)
        public string LastWord = "";
        public int WordEnd = -1;       // index right after the last word (string prefixes: f"…", r'…', R"(…)")
    }

    private sealed class CodeLexer
    {
        private readonly string T;
        private readonly int[] _starts;
        private readonly List<string> _lines;
        private readonly CommentLanguage _lang;
        private readonly LexKind K;
        private readonly List<Frame> _stack = new();
        private int i;
        private bool _continuation;    // the line just ended with a backslash in code
        private bool _halted;          // PHP __halt_compiler(): the rest is data

        public CodeLexer(string text, int[] starts, List<string> lines, CommentLanguage lang)
        { T = text; _starts = starts; _lines = lines; _lang = lang; K = lang.Kind; }

        private char At(int k) => k >= 0 && k < T.Length ? T[k] : '\0';
        private Frame Top => _stack[^1];
        private static bool IsNl(char c) => c == '\n' || c == '\r';
        private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

        public LineInfo[] Run()
        {
            if (K == LexKind.Php) _stack.Add(new Frame { Kind = FK.Html });
            else _stack.Add(new Frame { Kind = FK.Code, Root = true });

            var info = new LineInfo[_starts.Length];
            int line = 0;
            while (i < T.Length || line < _starts.Length)
            {
                while (line < _starts.Length && _starts[line] <= i)
                {
                    info[line] = Record(line);
                    line++;
                }
                if (i >= T.Length) break;
                int before = i;
                Step();
                if (i <= before) i = before + 1;   // never stall
            }
            return info;
        }

        private LineInfo Record(int line)
        {
            var root = _stack.FirstOrDefault(f => f.Root);
            bool code = !_halted && _stack.Count > 0 && Top.Kind == FK.Code && Top.Root && !_continuation;
            bool blank = string.IsNullOrWhiteSpace(_lines[line]);
            _continuation = false;
            return new LineInfo(code, root?.Braces ?? 0, root?.Brackets ?? 0, blank);
        }

        private void Push(Frame f) => _stack.Add(f);
        private void Pop() { if (_stack.Count > 1) _stack.RemoveAt(_stack.Count - 1); }

        private void Newline(Frame f)
        {
            // A backslash right before the line break joins the lines (Python, the C preprocessor): no comment between.
            if (f.Kind == FK.Code && f.Root)
            {
                int k = i - 1;
                if (k >= 0 && T[k] == '\\') _continuation = true;
            }
            i += T[i] == '\r' && At(i + 1) == '\n' ? 2 : 1;
        }

        private void Step()
        {
            var f = Top;
            switch (f.Kind)
            {
                case FK.Html: StepHtml(); break;
                case FK.Code: case FK.Hole: StepCode(f); break;
                case FK.Str: StepString(f); break;
                case FK.Block: StepBlock(f); break;
                case FK.LineCmt: StepLineComment(); break;
                case FK.Heredoc: StepHeredoc(f); break;
                case FK.JsxTag: StepJsxTag(f); break;
                case FK.JsxChildren: StepJsxChildren(); break;
            }
        }

        // ── PHP outside <?php … ?> ──
        private void StepHtml()
        {
            if (T[i] == '<' && At(i + 1) == '?')
            {
                // <?php, <?= and a short <? followed by a space open code; <?xml … ?> in a template stays HTML.
                int j = i + 2;
                if (string.Compare(T, j, "php", 0, 3, StringComparison.OrdinalIgnoreCase) == 0) j += 3;
                else if (At(j) == '=') j++;
                else if (!char.IsWhiteSpace(At(j))) { i += 2; return; }
                Push(new Frame { Kind = FK.Code, Root = true });
                i = j;
                return;
            }
            if (IsNl(T[i])) { Newline(Top); return; }
            i++;
        }

        // ── code (top level or inside an interpolation hole) ──
        private void StepCode(Frame f)
        {
            char c = T[i], n = At(i + 1);
            if (IsNl(c)) { Newline(f); return; }
            if (char.IsWhiteSpace(c)) { i++; return; }

            if (K == LexKind.Php)
            {
                if (c == '?' && n == '>') { CloseFirstPhpBlock(); i += 2; return; }
                if (c == '<' && n == '<' && At(i + 2) == '<' && TryHeredoc()) return;
            }

            // comments
            if (K == LexKind.Python || K == LexKind.Php)
            {
                if (c == '#' && !(K == LexKind.Php && n == '[')) { Push(new Frame { Kind = FK.LineCmt }); i++; return; }
            }
            if (K != LexKind.Python)
            {
                if (c == '/' && n == '/') { Push(new Frame { Kind = FK.LineCmt }); i += 2; return; }
                if (c == '/' && n == '*') { Push(new Frame { Kind = FK.Block, Nest = 1 }); i += 2; return; }
            }

            if (TryString(f)) return;

            if ((K == LexKind.JavaScript || K == LexKind.TypeScript) && c == '/' && ExpressionPosition(f))
            {
                ScanRegex();
                f.LastSig = 'a'; f.LastWord = "";
                return;
            }
            if (_lang.Jsx && c == '<' && ExpressionPosition(f) && (char.IsLetter(n) || n == '>'))
            {
                Push(new Frame { Kind = FK.JsxTag });
                f.LastSig = 'a'; f.LastWord = "";
                i++;
                return;
            }

            if (IsIdent(c) || (c == '@' && K == LexKind.CSharp && IsIdent(n)))
            {
                int s = i;
                i++;
                // C++14 digit separators (1'000) belong to the number, not to a char literal.
                bool number = char.IsDigit(c);
                while (i < T.Length && (IsIdent(T[i]) || (number && (T[i] == '\'' || T[i] == '.') && K == LexKind.CFamily)))
                    i++;
                f.LastWord = T[s..i];
                f.LastSig = 'a';
                f.WordEnd = i;
                if (K == LexKind.Php && f.LastWord.Equals("__halt_compiler", StringComparison.OrdinalIgnoreCase)) _halted = true;
                return;
            }

            switch (c)
            {
                case '{':
                    f.Braces++;
                    f.Brackets++;
                    break;
                case '}':
                    if (f.Kind == FK.Hole && f.Braces == 0)
                    {
                        int need = f.CloseCount, k = 0;
                        while (k < need && At(i) == '}') { i++; k++; }
                        Pop();
                        return;
                    }
                    f.Braces = Math.Max(0, f.Braces - 1);
                    f.Brackets = Math.Max(0, f.Brackets - 1);
                    break;
                case '(': case '[': f.Brackets++; break;
                case ')': case ']': f.Brackets = Math.Max(0, f.Brackets - 1); break;
            }
            f.LastSig = c;
            f.LastWord = "";
            i++;
        }

        /// <summary>PHP <c>?&gt;</c>: back to HTML — closes the open code block whatever is nested in it.</summary>
        private void CloseFirstPhpBlock()
        {
            int k = _stack.FindIndex(x => x.Root);
            if (k > 0) _stack.RemoveRange(k, _stack.Count - k);
        }

        private static readonly HashSet<string> ExprKeywords = new(StringComparer.Ordinal)
        { "return", "typeof", "instanceof", "in", "of", "new", "delete", "void", "throw", "case", "do", "else", "yield", "await", "default" };

        /// <summary>JS: a '/' starts a regex (and a '&lt;' starts JSX) where an expression may begin, not after a value.</summary>
        private static bool ExpressionPosition(Frame f) =>
            f.LastSig == '\0' || "(,=:[!&|?{};+-*%<>~^".IndexOf(f.LastSig) >= 0 || (f.LastSig == 'a' && ExprKeywords.Contains(f.LastWord));

        private void ScanRegex()
        {
            i++;
            bool cls = false;
            while (i < T.Length && !IsNl(T[i]))
            {
                char c = T[i];
                if (c == '\\') { i += IsNl(At(i + 1)) ? 1 : 2; continue; }
                if (c == '[') cls = true;
                else if (c == ']') cls = false;
                else if (c == '/' && !cls) { i++; break; }
                i++;
            }
            while (i < T.Length && char.IsLetter(T[i])) i++;   // flags
        }

        // ── strings ──
        private bool TryString(Frame f)
        {
            char c = T[i];
            switch (K)
            {
                case LexKind.CSharp: return TryCSharpString(f);
                case LexKind.Python:
                    if (c != '\'' && c != '"') return false;
                    {
                        string p = f.WordEnd == i ? f.LastWord.ToLowerInvariant() : "";
                        if (p.Length > 0 && !IsPythonPrefix(p)) p = "";   // an identifier right before a quote is a syntax error anyway
                        bool raw = p.Contains('r'), fs = p.Contains('f') || p.Contains('t');
                        bool triple = At(i + 1) == c && At(i + 2) == c;
                        Push(new Frame { Kind = FK.Str, Quote = c, Triple = triple, MultiLine = triple, Escapes = true, Raw = raw, Interp = fs });
                        i += triple ? 3 : 1;
                        return true;
                    }
                case LexKind.JavaScript: case LexKind.TypeScript:
                    if (c == '\'' || c == '"') { Push(new Frame { Kind = FK.Str, Quote = c, Escapes = true }); i++; return true; }
                    if (c == '`') { Push(new Frame { Kind = FK.Str, Quote = '`', Escapes = true, MultiLine = true, Interp = true }); i++; return true; }
                    return false;
                case LexKind.Dart:
                    if (c != '\'' && c != '"') return false;
                    {
                        bool raw = f.WordEnd == i && f.LastWord == "r";
                        bool triple = At(i + 1) == c && At(i + 2) == c;
                        Push(new Frame { Kind = FK.Str, Quote = c, Triple = triple, MultiLine = triple, Escapes = !raw, Raw = raw, Interp = !raw });
                        i += triple ? 3 : 1;
                        return true;
                    }
                case LexKind.Php:
                    if (c == '\'') { Push(new Frame { Kind = FK.Str, Quote = c, Escapes = true, MultiLine = true }); i++; return true; }
                    if (c == '"' || c == '`') { Push(new Frame { Kind = FK.Str, Quote = c, Escapes = true, MultiLine = true, Interp = true }); i++; return true; }
                    return false;
                case LexKind.Java:
                    if (c == '\'') { SkipCharLiteral(); return true; }
                    if (c != '"') return false;
                    if (At(i + 1) == '"' && At(i + 2) == '"')
                    { Push(new Frame { Kind = FK.Str, Quote = '"', Triple = true, MultiLine = true, Escapes = true }); i += 3; return true; }
                    Push(new Frame { Kind = FK.Str, Quote = '"', Escapes = true }); i++; return true;
                case LexKind.Go:
                    if (c == '\'') { SkipCharLiteral(); return true; }
                    if (c == '`') { Push(new Frame { Kind = FK.Str, Quote = '`', MultiLine = true }); i++; return true; }
                    if (c == '"') { Push(new Frame { Kind = FK.Str, Quote = '"', Escapes = true }); i++; return true; }
                    return false;
                case LexKind.CFamily:
                    if (c == '\'') { SkipCharLiteral(); return true; }
                    if (c != '"') return false;
                    if (f.WordEnd == i && f.LastWord is "R" or "u8R" or "uR" or "UR" or "LR")
                    {
                        int open = T.IndexOf('(', i + 1);
                        if (open > i && open - i - 1 <= 16 && !T.AsSpan(i + 1, open - i - 1).ContainsAny('\n', '\r'))
                        {
                            Push(new Frame { Kind = FK.Str, Raw = true, MultiLine = true, Delim = ")" + T[(i + 1)..open] + "\"" });
                            i = open + 1;
                            return true;
                        }
                    }
                    Push(new Frame { Kind = FK.Str, Quote = '"', Escapes = true }); i++; return true;
            }
            return false;
        }

        private static bool IsPythonPrefix(string p) =>
            p is "r" or "u" or "b" or "f" or "t" or "br" or "rb" or "fr" or "rf" or "tr" or "rt";

        private bool TryCSharpString(Frame f)
        {
            char c = T[i];
            if (c == '\'') { SkipCharLiteral(); return true; }
            if (c != '"' && c != '$' && c != '@') return false;
            int j = i, dollars = 0; bool at = false;
            while (At(j) == '$') { dollars++; j++; }
            if (At(j) == '@') { at = true; j++; while (At(j) == '$') { dollars++; j++; } }
            if (At(j) != '"') return false;   // @identifier, a lone $
            int q = 0;
            while (At(j + q) == '"') q++;
            if (!at && q >= 3)
            {
                Push(new Frame { Kind = FK.Str, Quote = '"', QuoteCount = q, Raw = true, MultiLine = true, Interp = dollars > 0, Dollars = Math.Max(1, dollars) });
                i = j + q;
                return true;
            }
            if (!at && q == 2) { i = j + 2; return true; }   // ""
            Push(new Frame { Kind = FK.Str, Quote = '"', Escapes = !at, Verbatim = at, MultiLine = at, Interp = dollars > 0 });
            i = j + 1;
            return true;
        }

        /// <summary>'x', '\n', 'A', '\'' — anything else (a Rust-style lifetime, a stray quote) is left as code.</summary>
        private void SkipCharLiteral()
        {
            if (At(i + 1) == '\\')
            {
                int j = i + 3;
                while (j < T.Length && j < i + 14 && !IsNl(T[j]) && T[j] != '\'') j++;
                i = At(j) == '\'' ? j + 1 : i + 1;
                return;
            }
            if (!IsNl(At(i + 1)) && At(i + 1) != '\0' && At(i + 2) == '\'') { i += 3; return; }
            i++;
        }

        private void StepString(Frame f)
        {
            char c = T[i], n = At(i + 1);
            if (IsNl(c))
            {
                if (f.ContinueLine) f.ContinueLine = false;   // "…\<newline>" carries on
                else if (!f.MultiLine) { Pop(); Newline(Top); return; }   // unterminated: resync on the next line
                i += c == '\r' && n == '\n' ? 2 : 1;
                return;
            }
            if (f.Escapes && c == '\\' && !(f.Raw && K != LexKind.Python))
            {
                if (IsNl(n)) { f.ContinueLine = true; i++; return; }
                i += 2;
                return;
            }
            if (f.Interp && TryOpenHole(f)) return;

            if (f.Delim != null)
            {
                if (string.CompareOrdinal(T, i, f.Delim, 0, f.Delim.Length) == 0) { i += f.Delim.Length; Pop(); }
                else i++;
                return;
            }
            if (c != f.Quote) { i++; return; }
            if (f.Raw && K == LexKind.CSharp)
            {
                int k = 0;
                while (At(i + k) == '"') k++;
                i += k;
                if (k >= f.QuoteCount) Pop();
                return;
            }
            if (f.Triple)
            {
                if (n == c && At(i + 2) == c) { i += 3; Pop(); }
                else i++;
                return;
            }
            if (f.Verbatim && n == '"') { i += 2; return; }
            i++;
            Pop();
        }

        private bool TryOpenHole(Frame f)
        {
            char c = T[i], n = At(i + 1);
            switch (K)
            {
                case LexKind.CSharp:
                    if (f.Raw)
                    {
                        if (c != '{') return false;
                        int k = 0;
                        while (At(i + k) == '{') k++;
                        i += k;
                        if (k >= f.Dollars) Push(new Frame { Kind = FK.Hole, CloseCount = f.Dollars });
                        return true;
                    }
                    if (c == '{') { if (n == '{') i += 2; else { Push(new Frame { Kind = FK.Hole }); i++; } return true; }
                    if (c == '}') { i += n == '}' ? 2 : 1; return true; }
                    return false;
                case LexKind.Python:
                    if (c == '{') { if (n == '{') i += 2; else { Push(new Frame { Kind = FK.Hole }); i++; } return true; }
                    if (c == '}') { i += n == '}' ? 2 : 1; return true; }
                    return false;
                case LexKind.JavaScript: case LexKind.TypeScript: case LexKind.Dart:
                    if (c == '$' && n == '{') { Push(new Frame { Kind = FK.Hole }); i += 2; return true; }
                    return false;
                case LexKind.Php:
                    if ((c == '{' && n == '$') || (c == '$' && n == '{')) { Push(new Frame { Kind = FK.Hole }); i += c == '{' ? 1 : 2; return true; }
                    return false;
            }
            return false;
        }

        private void StepBlock(Frame f)
        {
            char c = T[i], n = At(i + 1);
            if (IsNl(c)) { i += c == '\r' && n == '\n' ? 2 : 1; return; }
            if (c == '*' && n == '/') { i += 2; if (--f.Nest <= 0) Pop(); return; }
            if (K == LexKind.Dart && c == '/' && n == '*') { f.Nest++; i += 2; return; }
            i++;
        }

        private void StepLineComment()
        {
            char c = T[i];
            if (IsNl(c))
            {
                // C/C++: a // comment ending in a backslash swallows the next line too.
                bool spliced = K == LexKind.CFamily && i > 0 && T[i - 1] == '\\';
                if (!spliced) { Pop(); Newline(Top); return; }
                i += c == '\r' && At(i + 1) == '\n' ? 2 : 1;
                return;
            }
            if (K == LexKind.Php && c == '?' && At(i + 1) == '>') { CloseFirstPhpBlock(); i += 2; return; }   // ?> ends a PHP line comment
            i++;
        }

        // ── PHP heredoc / nowdoc ──
        private bool TryHeredoc()
        {
            var m = Regex.Match(T[i..Math.Min(T.Length, i + 200)], @"^<<<[ \t]*(['""]?)([A-Za-z_][A-Za-z0-9_]*)\1[ \t]*(\r\n|\n|\r)");
            if (!m.Success) return false;
            Push(new Frame { Kind = FK.Heredoc, Delim = m.Groups[2].Value });
            i += m.Length - m.Groups[3].Length;   // stop before the line break, so the next line records "in heredoc"
            return true;
        }

        private bool AtHeredocEnd(Frame f)
        {
            int j = i;
            while (j < T.Length && (T[j] == ' ' || T[j] == '\t')) j++;
            return string.CompareOrdinal(T, j, f.Delim, 0, f.Delim!.Length) == 0 && !IsIdent(At(j + f.Delim.Length));
        }

        private void StepHeredoc(Frame f)
        {
            bool lineStart = i == 0 || IsNl(T[i - 1]);
            if (lineStart && AtHeredocEnd(f))
            {
                while (T[i] == ' ' || T[i] == '\t') i++;
                i += f.Delim!.Length;
                Pop();
                return;
            }
            if (IsNl(T[i])) { i += T[i] == '\r' && At(i + 1) == '\n' ? 2 : 1; return; }
            i++;
        }

        // ── JSX ──
        private void StepJsxTag(Frame f)
        {
            char c = T[i], n = At(i + 1);
            if (IsNl(c)) { i += c == '\r' && n == '\n' ? 2 : 1; return; }
            if (c == '"' || c == '\'') { Push(new Frame { Kind = FK.Str, Quote = c, MultiLine = true }); i++; return; }
            if (c == '{') { Push(new Frame { Kind = FK.Hole }); i++; return; }
            if (c == '/' && n == '>') { i += 2; Pop(); return; }
            if (c == '>')
            {
                i++;
                if (f.Closing) Pop();
                else f.Kind = FK.JsxChildren;
                return;
            }
            i++;
        }

        private void StepJsxChildren()
        {
            char c = T[i], n = At(i + 1);
            if (IsNl(c)) { i += c == '\r' && n == '\n' ? 2 : 1; return; }
            if (c == '<' && n == '/') { Pop(); Push(new Frame { Kind = FK.JsxTag, Closing = true }); i += 2; return; }
            if (c == '<' && (char.IsLetter(n) || n == '>')) { Push(new Frame { Kind = FK.JsxTag }); i++; return; }
            if (c == '{') { Push(new Frame { Kind = FK.Hole }); i++; return; }
            i++;
        }
    }

    // ═══════════════════════════════ XML family ═══════════════════════════════

    private sealed class XmlLexer
    {
        private enum S { Text, Tag, Comment, CData, PI, Doctype }
        private readonly string T;
        private readonly int[] _starts;
        private readonly List<string> _lines;

        public XmlLexer(string text, int[] starts, List<string> lines) { T = text; _starts = starts; _lines = lines; }

        public LineInfo[] Run()
        {
            var info = new LineInfo[_starts.Length];
            var s = S.Text;
            char quote = '\0';
            int depth = 0, docBrackets = 0;
            bool wsOnly = true;           // the text since the last markup is whitespace only
            bool closingTag = false, selfClosing = false;
            int i = 0, line = 0;
            while (line < _starts.Length)
            {
                while (line < _starts.Length && _starts[line] <= i)
                {
                    string l = _lines[line];
                    bool startsMarkup = l.TrimStart().StartsWith('<');
                    info[line] = new LineInfo(s == S.Text && wsOnly && startsMarkup, depth, 0, string.IsNullOrWhiteSpace(l));
                    line++;
                }
                if (i >= T.Length) break;
                char c = T[i];
                switch (s)
                {
                    case S.Text:
                        if (c == '<')
                        {
                            if (Starts(i, "<!--")) { s = S.Comment; i += 4; }
                            else if (Starts(i, "<![CDATA[")) { s = S.CData; i += 9; }
                            else if (Starts(i, "<?")) { s = S.PI; i += 2; }
                            else if (Starts(i, "<!")) { s = S.Doctype; docBrackets = 0; i += 2; }
                            else { s = S.Tag; closingTag = i + 1 < T.Length && T[i + 1] == '/'; selfClosing = false; i++; }
                        }
                        else { if (!char.IsWhiteSpace(c)) wsOnly = false; i++; }
                        break;
                    case S.Tag:
                        if (quote != '\0') { if (c == quote) quote = '\0'; i++; break; }
                        if (c == '"' || c == '\'') { quote = c; i++; break; }
                        if (c == '/' && i + 1 < T.Length && T[i + 1] == '>') { selfClosing = true; i++; break; }
                        if (c == '>')
                        {
                            if (closingTag) depth = Math.Max(0, depth - 1);
                            else if (!selfClosing) depth++;
                            s = S.Text; wsOnly = true; i++;
                            break;
                        }
                        i++;
                        break;
                    case S.Comment:
                        if (Starts(i, "-->")) { s = S.Text; wsOnly = true; i += 3; } else i++;
                        break;
                    case S.CData:
                        // CDATA is character data: the text around it is no longer whitespace-only.
                        if (Starts(i, "]]>")) { s = S.Text; wsOnly = false; i += 3; } else i++;
                        break;
                    case S.PI:
                        if (Starts(i, "?>")) { s = S.Text; wsOnly = true; i += 2; } else i++;
                        break;
                    case S.Doctype:
                        if (c == '[') docBrackets++;
                        else if (c == ']') docBrackets--;
                        else if (c == '>' && docBrackets <= 0) { s = S.Text; wsOnly = true; }
                        i++;
                        break;
                }
            }
            return info;
        }

        private bool Starts(int i, string s) => string.CompareOrdinal(T, i, s, 0, s.Length) == 0;
    }
}
