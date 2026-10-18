using System.Text;

namespace CluadeX.Services.UiStudio.Sketch;

internal enum DTok { Ident, Str, Num, Punct, End }

/// <summary>A Dart token. For a string: <see cref="Text"/> is its value with every interpolation shown as "…"
/// (and <see cref="Interp"/> holds the interpolated names, "$title" → "title").
/// <see cref="End"/> is the character just past the token in the source (for a string: past its closing quote).</summary>
internal readonly record struct DToken(DTok Kind, string Text, int Pos, string? Interp = null, int End = 0);

/// <summary>
/// Reads Dart source into tokens: identifiers, numbers, punctuation and strings (single, double, triple-quoted,
/// raw, with <c>$name</c> and <c>${…}</c> interpolation), skipping comments. It never throws: a broken file gives
/// the tokens up to where it broke.
/// </summary>
internal static class DartLexer
{
    private static readonly string[] Ops =
    {
        "...?", "...", "??=", "~/=", ">>>=", "<<=", ">>=", "?..", "=>", "?.", "??", "==", "!=", "<=", ">=", "&&", "||", "++", "--",
        "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "..", "<<", "~/",
    };

    public static List<DToken> Lex(string s)
    {
        var list = new List<DToken>(s.Length / 4);
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < n && s[i + 1] == '/') { i = s.IndexOf('\n', i); if (i < 0) break; continue; }
            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                int depth = 1; i += 2;   // Dart block comments nest
                while (i < n && depth > 0)
                {
                    if (s[i] == '/' && i + 1 < n && s[i + 1] == '*') { depth++; i += 2; }
                    else if (s[i] == '*' && i + 1 < n && s[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                continue;
            }
            if (c is '\'' or '"' || c == 'r' && i + 1 < n && s[i + 1] is '\'' or '"')
            {
                int start = i;
                bool raw = c == 'r';
                if (raw) i++;
                var (text, interp, end) = ReadString(s, i, raw);
                list.Add(new DToken(DTok.Str, text, start, interp, end));
                i = end;
                continue;
            }
            if (char.IsLetter(c) || c is '_' or '$')
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '$')) i++;
                list.Add(new DToken(DTok.Ident, s[start..i], start, null, i));
                continue;
            }
            if (char.IsDigit(c) || c == '.' && i + 1 < n && char.IsDigit(s[i + 1]))
            {
                int start = i;
                if (c == '0' && i + 1 < n && s[i + 1] is 'x' or 'X') { i += 2; while (i < n && Uri.IsHexDigit(s[i])) i++; }
                else
                {
                    while (i < n && (char.IsDigit(s[i]) || s[i] == '.' && i + 1 < n && char.IsDigit(s[i + 1]))) i++;
                    if (i < n && s[i] is 'e' or 'E') { i++; if (i < n && s[i] is '+' or '-') i++; while (i < n && char.IsDigit(s[i])) i++; }
                }
                list.Add(new DToken(DTok.Num, s[start..i], start, null, i));
                continue;
            }
            string? op = null;
            foreach (var o in Ops)
                if (string.CompareOrdinal(s, i, o, 0, o.Length) == 0) { op = o; break; }
            op ??= c.ToString();
            list.Add(new DToken(DTok.Punct, op, i, null, i + op.Length));
            i += op.Length;
        }
        list.Add(new DToken(DTok.End, "", n, null, n));
        return list;
    }

    /// <summary>Read the string literal whose opening quote is at <paramref name="i"/>.</summary>
    private static (string Text, string? Interp, int End) ReadString(string s, int i, bool raw)
    {
        int n = s.Length;
        char q = s[i];
        bool triple = i + 2 < n && s[i + 1] == q && s[i + 2] == q;
        int k = i + (triple ? 3 : 1);
        var sb = new StringBuilder();
        var names = new List<string>();
        while (k < n)
        {
            char c = s[k];
            if (triple ? c == q && k + 2 < n && s[k + 1] == q && s[k + 2] == q : c == q)
                return (sb.ToString(), names.Count > 0 ? string.Join(",", names) : null, k + (triple ? 3 : 1));
            if (!triple && c == '\n') return (sb.ToString(), null, k);   // unterminated: stop at the line end
            if (!raw && c == '\\' && k + 1 < n)
            {
                char e = s[k + 1];
                sb.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
                k += 2;
                if (e == 'u' && k < n)   // \u{1F600} or ก
                {
                    sb.Length--;
                    int st = k;
                    if (s[k] == '{') { int close = s.IndexOf('}', k); if (close > 0) { st = k + 1; k = close + 1; } }
                    else k = Math.Min(n, k + 4);
                    string hex = s[st..(s[Math.Max(st, k - 1)] == '}' ? k - 1 : k)];
                    if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int cp) && cp is > 0 and <= 0x10FFFF)
                        sb.Append(char.ConvertFromUtf32(cp));
                }
                continue;
            }
            if (!raw && c == '$' && k + 1 < n)
            {
                if (s[k + 1] == '{')
                {
                    int depth = 1, j = k + 2;
                    int exprStart = j;
                    while (j < n && depth > 0)
                    {
                        char d = s[j];
                        if (d == '{') depth++;
                        else if (d == '}') depth--;
                        else if (d is '\'' or '"') { j = ReadString(s, j, false).End; continue; }
                        j++;
                    }
                    names.Add(s[exprStart..Math.Max(exprStart, j - 1)].Trim());
                    sb.Append('…');
                    k = j;
                    continue;
                }
                if (char.IsLetter(s[k + 1]) || s[k + 1] == '_')
                {
                    int j = k + 1;
                    while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                    names.Add(s[(k + 1)..j]);
                    sb.Append('…');
                    k = j;
                    continue;
                }
            }
            sb.Append(c);
            k++;
        }
        return (sb.ToString(), null, n);
    }

    /// <summary>For each bracket token, the index of its partner (-1 when unbalanced).</summary>
    public static int[] Brackets(List<DToken> t)
    {
        var match = new int[t.Count];
        Array.Fill(match, -1);
        var stack = new Stack<int>();
        for (int i = 0; i < t.Count; i++)
        {
            if (t[i].Kind != DTok.Punct) continue;
            string p = t[i].Text;
            if (p is "(" or "[" or "{") stack.Push(i);
            else if (p is ")" or "]" or "}")
            {
                // tolerate a stray closer: unwind to the nearest opener of the same kind
                string open = p == ")" ? "(" : p == "]" ? "[" : "{";
                if (!stack.Any(x => t[x].Text == open)) continue;
                while (stack.Count > 0)
                {
                    int o = stack.Pop();
                    if (t[o].Text == open) { match[o] = i; match[i] = o; break; }
                }
            }
        }
        return match;
    }
}

// ───────────────────────────── expressions ─────────────────────────────

internal abstract class DExpr
{
    /// <summary>Character offset in the source.</summary>
    public int Pos;
}

/// <summary>A dotted name: <c>title</c>, <c>widget.title</c>, <c>Icons.home</c>, <c>context.l10n.users</c>;
/// or a member read off another expression (<see cref="Target"/>).</summary>
internal sealed class DName : DExpr
{
    public string Path = "";
    public DExpr? Target;
}

/// <summary>A call: a constructor (<c>Text(…)</c>, <c>ListView.builder(…)</c>), a function, or a method on
/// <see cref="Target"/> (<c>items.map(…)</c>, <c>value.when(…)</c>).</summary>
internal sealed class DCall : DExpr
{
    public string Name = "";
    public DExpr? Target;
    public readonly List<DExpr> Args = new();
    public readonly Dictionary<string, DExpr> Named = new(StringComparer.Ordinal);

    public DExpr? Arg(string name) => Named.TryGetValue(name, out var e) ? e : null;
    public DExpr? Positional(int i) => i < Args.Count ? Args[i] : null;
    /// <summary>"ListView" for "ListView.builder"; "Text" for "Text".</summary>
    public string Base => Name.Contains('.') ? Name[..Name.IndexOf('.')] : Name;
    public string? Variant => Name.Contains('.') ? Name[(Name.IndexOf('.') + 1)..] : null;
}

internal sealed class DStr : DExpr
{
    public string Value = "";
    public string? Interp;
    public bool Interpolated => Interp != null;
}

internal sealed class DNum : DExpr { public double Value; }

/// <summary>A list literal; collection-if/for and spreads stay as <see cref="DIf"/>, <see cref="DFor"/>, <see cref="DSpread"/> items.</summary>
internal sealed class DList : DExpr { public readonly List<DExpr> Items = new(); }

internal sealed class DMap : DExpr { public readonly List<(DExpr Key, DExpr Value)> Entries = new(); }

/// <summary>A ternary, or a collection-if (<see cref="Collection"/>).</summary>
internal sealed class DIf : DExpr
{
    public DExpr? Cond;
    public DExpr Then = null!;
    public DExpr? Else;
    public bool Collection;
}

internal sealed class DFor : DExpr { public DExpr Body = null!; }
internal sealed class DSpread : DExpr { public DExpr Inner = null!; }

internal sealed class DLambda : DExpr
{
    public readonly List<string> Params = new();
    /// <summary>The expression body, or every <c>return</c> of a block body.</summary>
    public readonly List<DExpr> Returns = new();
}

internal sealed class DSwitch : DExpr { public readonly List<DExpr> Arms = new(); }

internal sealed class DBinary : DExpr
{
    public string Op = "";
    public DExpr Left = null!;
    public DExpr Right = null!;
}

/// <summary>Something the sketch does not need (a number expression, an index, a cast target…).</summary>
internal sealed class DOther : DExpr { public string Text = ""; }

/// <summary>
/// A forgiving Dart expression parser: enough of the grammar to read widget trees (constructor calls with named
/// arguments, list literals with if/for/spread, lambdas, ternaries, switch expressions, method chains, generic type
/// arguments). Anything it does not understand becomes <see cref="DOther"/> and parsing resumes at the next
/// comma or closing bracket — a half-finished file still gives the parts that are there.
/// </summary>
internal sealed class DartParser
{
    private readonly List<DToken> _t;
    private readonly int[] _match;
    private int _i;
    private int _budget;

    public DartParser(List<DToken> tokens, int[] brackets)
    {
        _t = tokens;
        _match = brackets;
    }

    public int Index => _i;

    /// <summary>Parse one expression starting at token <paramref name="at"/>.</summary>
    public DExpr ParseAt(int at, int budget = 60_000)
    {
        _i = at;
        _budget = budget;
        try { return Expr(); }
        catch (ParseBudget) { return new DOther { Pos = Pos }; }
    }

    private sealed class ParseBudget : Exception { }

    private DToken Cur => _t[Math.Min(_i, _t.Count - 1)];
    private DToken Peek(int k = 1) => _t[Math.Min(_i + k, _t.Count - 1)];
    private int Pos => Cur.Pos;
    private bool Is(string p) => Cur.Kind == DTok.Punct && Cur.Text == p;
    private bool IsIdent(string w) => Cur.Kind == DTok.Ident && Cur.Text == w;
    private bool AtEnd => Cur.Kind == DTok.End;

    private void Step()
    {
        if (--_budget < 0) throw new ParseBudget();
        if (_i < _t.Count - 1) _i++;
    }

    private static readonly HashSet<string> BinaryOps = new(StringComparer.Ordinal)
    {
        "??", "||", "&&", "==", "!=", "<", ">", "<=", ">=", "+", "-", "*", "/", "~/", "%", "&", "|", "^", "<<", ">>",
        "=", "+=", "-=", "*=", "/=", "??=", "%=", "&=", "|=", "^=", "~/=",
    };

    private DExpr Expr()
    {
        var left = Unary();
        while (true)
        {
            if (Cur.Kind == DTok.Punct && BinaryOps.Contains(Cur.Text))
            {
                string op = Cur.Text;
                int pos = Pos;
                Step();
                var right = Unary();
                left = new DBinary { Op = op, Left = left, Right = right, Pos = pos };
                continue;
            }
            if (IsIdent("is") || IsIdent("as"))
            {
                Step();
                if (Is("!")) Step();
                SkipType();
                continue;
            }
            break;
        }
        if (Is("?"))
        {
            int pos = Pos;
            Step();
            var then = Expr();
            DExpr? otherwise = null;
            if (Is(":")) { Step(); otherwise = Expr(); }
            return new DIf { Cond = left, Then = then, Else = otherwise, Pos = pos };
        }
        return left;
    }

    private DExpr Unary()
    {
        if (Is("!")) { int pos = Pos; Step(); return new DBinary { Op = "!", Left = new DOther { Pos = pos }, Right = Unary(), Pos = pos }; }
        if (Cur.Kind == DTok.Punct && Cur.Text is "-" or "~" or "++" or "--") { Step(); return Unary(); }
        if (IsIdent("await") || IsIdent("const") || IsIdent("new") || IsIdent("final")) { Step(); return Unary(); }
        if (IsIdent("throw")) { Step(); var e = Expr(); return new DOther { Text = "throw", Pos = e.Pos }; }
        return Postfix(Primary());
    }

    private DExpr Primary()
    {
        var tok = Cur;
        switch (tok.Kind)
        {
            case DTok.Str:
            {
                var sb = new StringBuilder();
                string? interp = null;
                while (Cur.Kind == DTok.Str)   // 'a' 'b' — adjacent literals join
                {
                    sb.Append(Cur.Text);
                    if (Cur.Interp != null) interp = interp == null ? Cur.Interp : interp + "," + Cur.Interp;
                    Step();
                }
                return new DStr { Value = sb.ToString(), Interp = interp, Pos = tok.Pos };
            }
            case DTok.Num:
                Step();
                double.TryParse(tok.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(tok.Text, 16).ToString() : tok.Text,
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v);
                return new DNum { Value = v, Pos = tok.Pos };
            case DTok.Ident:
                if (tok.Text == "switch" && Peek().Text == "(") return SwitchExpr();
                if (tok.Text is "true" or "false" or "null" or "this" or "super") { Step(); return new DName { Path = tok.Text, Pos = tok.Pos }; }
                Step();
                SkipTypeArgsIfGeneric();
                return new DName { Path = tok.Text, Pos = tok.Pos };
            case DTok.Punct:
                switch (tok.Text)
                {
                    case "[": return ListLit();
                    case "{": return MapOrSet();
                    case "(": return ParenOrLambda();
                    case "<":
                        // <Widget>[...]  /  <String, int>{...}
                        if (TrySkipTypeArgs()) return Primary();
                        break;
                    case "#": Step(); if (Cur.Kind == DTok.Ident) Step(); return new DOther { Text = "#", Pos = tok.Pos };
                }
                break;
        }
        // not an expression start: step over it so the caller can resynchronise
        int pos = Pos;
        if (!AtEnd && !IsCloser()) Step();
        return new DOther { Pos = pos };
    }

    private bool IsCloser() => Cur.Kind == DTok.Punct && Cur.Text is ")" or "]" or "}" or "," or ";" or ":";

    private DExpr Postfix(DExpr e)
    {
        while (true)
        {
            if (Is(".") || Is("?."))
            {
                Step();
                if (Cur.Kind != DTok.Ident) break;
                string member = Cur.Text;
                Step();
                SkipTypeArgsIfGeneric();
                e = e is DName { Target: null } nn
                    ? new DName { Path = nn.Path is "this" or "super" ? member : nn.Path + "." + member, Pos = e.Pos }
                    : new DName { Path = member, Target = e, Pos = e.Pos };
                continue;
            }
            if (Is("("))
            {
                var call = new DCall { Pos = e.Pos };
                if (e is DName { Target: null } n) call.Name = n.Path;
                else if (e is DName { Target: { } target } m) { call.Name = m.Path; call.Target = target; }
                else call.Target = e;
                Args(call);
                e = call;
                continue;
            }
            if (Is("!")) { Step(); continue; }   // null assertion
            if (Is("["))
            {
                int close = _match[_i];
                var target = e;
                Step();
                if (!Is("]")) Expr();
                if (Is("]")) Step(); else if (close > 0) _i = close + 1;
                e = new DName { Path = "[]", Target = target, Pos = target.Pos };
                continue;
            }
            if (Is("..") || Is("?.."))
            {
                // cascade: x..a = 1..b() — evaluate to x
                while (Is("..") || Is("?.."))
                {
                    Step();
                    if (Cur.Kind == DTok.Ident) Step();
                    while (Is(".") && Peek().Kind == DTok.Ident) { Step(); Step(); }
                    if (Is("(")) SkipBracket();
                    if (Cur.Kind == DTok.Punct && Cur.Text is "=" or "+=" or "-=") { Step(); Unary(); }
                }
                continue;
            }
            if (Is("++") || Is("--")) { Step(); continue; }
            break;
        }
        return e;
    }

    private void Args(DCall call)
    {
        int close = _match[_i];
        Step();   // (
        while (!AtEnd && !Is(")"))
        {
            int before = _i;
            if (Cur.Kind == DTok.Ident && Peek().Text == ":" && Peek().Kind == DTok.Punct)
            {
                string name = Cur.Text;
                Step(); Step();
                call.Named[name] = Expr();
            }
            else call.Args.Add(Expr());
            Sync(close);
            if (Is(",")) Step();
            if (_i == before) Step();
        }
        if (Is(")")) Step();
        else if (close > 0) _i = close + 1;
    }

    /// <summary>After an element: skip anything left over up to the next comma or the list's closer.</summary>
    private void Sync(int close)
    {
        while (!AtEnd && !Is(",") && _i != close)
        {
            if (Cur.Kind == DTok.Punct && Cur.Text is "(" or "[" or "{") { SkipBracket(); continue; }
            if (Cur.Kind == DTok.Punct && Cur.Text is ")" or "]" or "}") break;
            Step();
        }
    }

    private void SkipBracket()
    {
        int close = _match[_i];
        if (close > _i) _i = close + 1;
        else Step();
    }

    private DExpr ListLit()
    {
        var list = new DList { Pos = Pos };
        int close = _match[_i];
        Step();
        while (!AtEnd && !Is("]"))
        {
            int before = _i;
            list.Items.Add(Element());
            Sync(close);
            if (Is(",")) Step();
            if (_i == before) Step();
        }
        if (Is("]")) Step();
        else if (close > 0) _i = close + 1;
        return list;
    }

    /// <summary>A collection element: an expression, if (…) a [else b], for (…) a, or ...spread.</summary>
    private DExpr Element()
    {
        int pos = Pos;
        if (IsIdent("if") && Peek().Text == "(")
        {
            Step();
            int close = _match[_i];
            Step();
            var cond = close > 0 ? Expr() : new DOther();
            if (close > 0) _i = close + 1;
            var then = Element();
            DExpr? otherwise = null;
            if (IsIdent("else")) { Step(); otherwise = Element(); }
            return new DIf { Cond = cond, Then = then, Else = otherwise, Collection = true, Pos = pos };
        }
        if (IsIdent("for") || IsIdent("await") && Peek().Text == "for")
        {
            if (IsIdent("await")) Step();
            Step();
            if (Is("(")) SkipBracket();
            return new DFor { Body = Element(), Pos = pos };
        }
        if (Is("...") || Is("...?"))
        {
            Step();
            return new DSpread { Inner = Expr(), Pos = pos };
        }
        return Expr();
    }

    private DExpr MapOrSet()
    {
        int pos = Pos;
        int close = _match[_i];
        var map = new DMap { Pos = pos };
        Step();
        while (!AtEnd && !Is("}"))
        {
            int before = _i;
            var key = Element();
            if (Is(":")) { Step(); map.Entries.Add((key, Expr())); }
            Sync(close);
            if (Is(",")) Step();
            if (_i == before) Step();
        }
        if (Is("}")) Step();
        else if (close > 0) _i = close + 1;
        return map;
    }

    private DExpr ParenOrLambda()
    {
        int pos = Pos;
        int close = _match[_i];
        if (close > 0)
        {
            var after = _t[Math.Min(close + 1, _t.Count - 1)];
            int k = close + 1;
            if (after.Kind == DTok.Ident && after.Text is "async" or "sync") { k++; if (_t[Math.Min(k, _t.Count - 1)].Text == "*") k++; after = _t[Math.Min(k, _t.Count - 1)]; }
            if (after.Kind == DTok.Punct && after.Text is "=>" or "{")
            {
                var lambda = new DLambda { Pos = pos };
                for (int p = _i + 1; p < close; p++)
                {
                    // the last identifier before each comma at the parameter list's own level is the parameter's name
                    if (_t[p].Kind == DTok.Ident && (_t[p + 1].Text is "," or ")" or "}" or "]" or "=" or ":") && _t[p].Text is not ("required" or "this"))
                        lambda.Params.Add(_t[p].Text);
                }
                _i = k;
                if (Is("=>")) { Step(); lambda.Returns.Add(Expr()); }
                else Block(lambda.Returns);
                return lambda;
            }
        }
        Step();
        var inner = Is(")") ? new DOther { Pos = pos } : Expr();
        if (Is(")")) Step();
        else if (close > 0) _i = close + 1;
        return inner;
    }

    /// <summary>A { … } body: collect what its own <c>return</c>s give (not those of closures inside it).</summary>
    public void Block(List<DExpr> returns)
    {
        int open = _i;
        int close = _match[open];
        if (close < 0) { Step(); return; }
        CollectReturns(open, close, returns);
        _i = close + 1;
    }

    /// <summary>Every return statement of the block (open..close), skipping nested parenthesised code (closures).</summary>
    public void CollectReturns(int open, int close, List<DExpr> returns)
    {
        int p = open + 1;
        while (p < close)
        {
            var tk = _t[p];
            if (tk.Kind == DTok.Punct && tk.Text is "(" or "[" && _match[p] > p) { p = _match[p] + 1; continue; }
            if (tk.Kind == DTok.Ident && tk.Text == "return")
            {
                int save = _i;
                var e = ParseAt(p + 1, _budget > 0 ? _budget : 20_000);
                returns.Add(e);
                p = Math.Max(_i, p + 1);
                _i = save;
                continue;
            }
            p++;
        }
    }

    private DExpr SwitchExpr()
    {
        int pos = Pos;
        Step();                 // switch
        SkipBracket();          // (subject)
        var sw = new DSwitch { Pos = pos };
        if (!Is("{")) return sw;
        int close = _match[_i];
        Step();
        while (!AtEnd && _i < close)
        {
            // pattern … => expr ,
            while (!AtEnd && _i < close && !Is("=>"))
            {
                if (Cur.Kind == DTok.Punct && Cur.Text is "(" or "[" or "{") SkipBracket(); else Step();
            }
            if (!Is("=>")) break;
            Step();
            sw.Arms.Add(Expr());
            Sync(close);
            if (Is(",")) Step();
        }
        if (close > 0) _i = close + 1;
        return sw;
    }

    private void SkipType()
    {
        if (Cur.Kind == DTok.Ident) { Step(); while (Is(".") && Peek().Kind == DTok.Ident) { Step(); Step(); } }
        if (Is("<")) TrySkipTypeArgs();
        if (Is("?")) Step();
    }

    private void SkipTypeArgsIfGeneric()
    {
        if (Is("<")) { int save = _i; if (!TrySkipTypeArgs() || !(Is("(") || Is("[") || Is("{") || Is(".") || Is(")") || Is(","))) _i = save; }
    }

    /// <summary>Step over &lt;A, B&lt;C&gt;?&gt; when it is a type argument list; false (position kept) otherwise.</summary>
    private bool TrySkipTypeArgs()
    {
        int save = _i;
        if (!Is("<")) return false;
        int depth = 0;
        for (int guard = 0; guard < 64; guard++)
        {
            var tk = Cur;
            if (tk.Kind == DTok.Punct && tk.Text == "<") depth++;
            else if (tk.Kind == DTok.Punct && tk.Text == ">") { depth--; if (depth == 0) { Step(); return true; } }
            else if (tk.Kind == DTok.Punct && tk.Text == ">>") { depth -= 2; if (depth <= 0) { Step(); return true; } }
            else if (tk.Kind == DTok.Ident || tk.Kind == DTok.Punct && tk.Text is "," or "." or "?" or "(" or ")") { }
            else { _i = save; return false; }
            Step();
        }
        _i = save;
        return false;
    }
}
