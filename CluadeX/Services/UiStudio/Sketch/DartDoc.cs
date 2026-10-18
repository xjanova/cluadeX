namespace CluadeX.Services.UiStudio.Sketch;

/// <summary>A replacement of the characters [Start, End) of a file with <see cref="Text"/>.</summary>
internal readonly record struct Replace(int Start, int End, string Text);

/// <summary>One item of a comma-separated run: an argument (<see cref="Name"/> set for a named one) or a list element.</summary>
internal sealed class DartItem
{
    public string? Name;
    /// <summary>First character: the name of a named argument, else the value.</summary>
    public int Start;
    public int ValueStart, ValueEnd;
    public int FirstTok, LastTok;
    /// <summary>The comma after it (-1: none).</summary>
    public int CommaTok = -1;
}

/// <summary>A constructor (or method) call in the source, with the exact spans an edit needs.</summary>
internal sealed class DartCall
{
    /// <summary>"Text", "Image.asset", "ElevatedButton.icon" (an import prefix is left out).</summary>
    public string Name = "";
    /// <summary>First character: its const / new keyword, else its prefix, else its name.</summary>
    public int Start;
    /// <summary>Where Flutter's widget inspector points: the class name (the constructor's name for a named one).</summary>
    public int NameAt;
    public int Open, Close;
    public int StartTok, NameTok, OpenTok, CloseTok;
    /// <summary>Its own const keyword (-1: none).</summary>
    public int ConstTok = -1;
    public readonly List<DartItem> Args = new();

    public int End => Close + 1;
    public string ClassName => Name.Contains('.') ? Name[..Name.IndexOf('.')] : Name;
    public DartItem? Named(string name) => Args.FirstOrDefault(a => a.Name == name);
    public DartItem? Positional(int i)
    {
        int k = 0;
        foreach (var a in Args)
            if (a.Name == null && k++ == i) return a;
        return null;
    }
}

/// <summary>A list literal: <c>[a, b]</c>, <c>const [ … ]</c>, <c>&lt;Widget&gt;[ … ]</c>.</summary>
internal sealed class DartList
{
    public int OpenTok, CloseTok;
    public readonly List<DartItem> Items = new();
}

internal enum SlotKind
{
    /// <summary>An element of a list (children: [ … ]).</summary>
    ListItem,
    /// <summary>The whole value of an argument of another call (child: X).</summary>
    Arg,
    /// <summary>What a function gives back (return X; / => X).</summary>
    Root,
    /// <summary>Inside something else (a ternary branch, a lambda, a method chain).</summary>
    Other,
}

/// <summary>Where a call sits.</summary>
internal sealed class DartSlot
{
    public SlotKind Kind;
    /// <summary>The call whose argument holds it (for a list element: the call whose argument is the list).</summary>
    public DartCall? Owner;
    /// <summary>That argument's name (null for a positional one).</summary>
    public string? ArgName;
    public DartList? List;
    public int Index = -1;
}

/// <summary>
/// A Dart file read for editing: tokens with exact spans, bracket partners, line starts. It never throws on a broken
/// file — whatever cannot be read simply isn't found.
/// </summary>
internal sealed class DartDoc
{
    public string Text { get; }
    public List<DToken> T { get; }
    public int[] Match { get; }
    /// <summary>The file's own line break ("\r\n" or "\n").</summary>
    public string Nl { get; }
    private readonly List<int> _lineStarts = new() { 0 };

    private static readonly HashSet<string> NotCalls = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "return", "assert", "super", "this", "on", "when", "await", "throw", "do", "else",
    };

    public DartDoc(string text)
    {
        Text = text;
        T = DartLexer.Lex(text);
        Match = DartLexer.Brackets(T);
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') _lineStarts.Add(i + 1);
        Nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    // ───────────────────────────── positions ─────────────────────────────

    /// <summary>The offset of a 1-based line and column (-1 outside the file).</summary>
    public int Offset(int line, int column)
    {
        if (line < 1 || line > _lineStarts.Count || column < 1) return -1;
        int at = _lineStarts[line - 1] + column - 1;
        return at <= Text.Length ? at : -1;
    }

    public (int Line, int Column) LineCol(int offset)
    {
        int lo = 0, hi = _lineStarts.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_lineStarts[mid] <= offset) lo = mid; else hi = mid - 1;
        }
        return (lo + 1, offset - _lineStarts[lo] + 1);
    }

    /// <summary>The token that starts exactly at <paramref name="offset"/> (-1: none).</summary>
    public int TokenStartingAt(int offset)
    {
        int lo = 0, hi = T.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (T[mid].Pos == offset) return mid;
            if (T[mid].Pos < offset) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }

    public int LineStart(int pos)
    {
        if (pos <= 0) return 0;
        int i = Text.LastIndexOf('\n', Math.Min(pos, Text.Length) - 1);
        return i < 0 ? 0 : i + 1;
    }

    /// <summary>The offset just past the line's break (the next line's start), or the file's end.</summary>
    public int NextLineStart(int pos)
    {
        int i = Text.IndexOf('\n', Math.Min(pos, Text.Length));
        return i < 0 ? Text.Length : i + 1;
    }

    public string IndentOf(int pos)
    {
        int s = LineStart(pos), e = s;
        while (e < Text.Length && Text[e] is ' ' or '\t') e++;
        return Text[s..e];
    }

    public bool OnlySpaceBefore(int pos)
    {
        for (int i = LineStart(pos); i < pos; i++)
            if (Text[i] is not (' ' or '\t')) return false;
        return true;
    }

    public bool OnlySpaceAfter(int pos)
    {
        for (int i = pos; i < Text.Length && Text[i] != '\n'; i++)
            if (Text[i] is not (' ' or '\t' or '\r')) return false;
        return true;
    }

    public bool Multiline(int from, int to) => to > from && Text.IndexOf('\n', from, Math.Min(to, Text.Length) - from) >= 0;

    // ───────────────────────────── tokens ─────────────────────────────

    public bool Is(int i, string punct) => i >= 0 && i < T.Count && T[i].Kind == DTok.Punct && T[i].Text == punct;
    public bool IsOpener(int i) => i >= 0 && i < T.Count && T[i].Kind == DTok.Punct && T[i].Text is "(" or "[" or "{";
    public bool IsIdent(int i, string word) => i >= 0 && i < T.Count && T[i].Kind == DTok.Ident && T[i].Text == word;

    /// <summary>Step over a type-argument list &lt;A, B&lt;C&gt;?&gt; starting at <paramref name="i"/>; the token after it, or -1.</summary>
    public int SkipAngles(int i)
    {
        int depth = 0;
        for (int k = i; k < T.Count && k < i + 96; k++)
        {
            var tk = T[k];
            if (tk.Kind == DTok.Punct)
            {
                switch (tk.Text)
                {
                    case "<": depth++; break;
                    case ">": if (--depth == 0) return k + 1; break;
                    case ">>": depth -= 2; if (depth <= 0) return depth == 0 ? k + 1 : -1; break;
                    case "," or "." or "?" or "(" or ")": break;
                    default: return -1;
                }
            }
            else if (tk.Kind != DTok.Ident) return -1;
        }
        return -1;
    }

    /// <summary>From the &gt; that closes a type-argument list, back to its &lt; (-1: not one).</summary>
    public int AngleOpenBefore(int close)
    {
        int depth = 0;
        for (int k = close; k >= 0 && k > close - 96; k--)
        {
            var tk = T[k];
            if (tk.Kind == DTok.Punct)
            {
                switch (tk.Text)
                {
                    case ">": depth++; break;
                    case ">>": depth += 2; break;
                    case "<": if (--depth == 0) return k; break;
                    case "," or "." or "?" or "(" or ")": break;
                    default: return -1;
                }
            }
            else if (tk.Kind != DTok.Ident) return -1;
        }
        return -1;
    }

    /// <summary>The innermost bracket that encloses token <paramref name="tok"/> (-1: top level).</summary>
    public int EnclosingOpener(int tok)
    {
        for (int j = tok - 1; j >= 0; j--)
        {
            var tk = T[j];
            if (tk.Kind != DTok.Punct) continue;
            if (tk.Text is ")" or "]" or "}") { if (Match[j] >= 0 && Match[j] < j) j = Match[j]; continue; }
            if (tk.Text is "(" or "[" or "{") return j;
        }
        return -1;
    }

    private static bool IsTypeName(string s) => s.Length > 0 && (char.IsUpper(s[0]) || s.Length > 1 && s[0] == '_' && char.IsUpper(s[1]));

    // ───────────────────────────── calls ─────────────────────────────

    /// <summary>The call whose name is at a 1-based line and column (what Flutter's widget inspector reports).</summary>
    public DartCall? CallAt(int line, int column)
    {
        int at = Offset(line, column);
        return at < 0 ? null : CallAtOffset(at);
    }

    public DartCall? CallAtOffset(int offset)
    {
        int k = TokenStartingAt(offset);
        if (k < 0 || T[k].Kind != DTok.Ident) return null;
        int j = k + 1, name = k;
        // pointed at the class of a named constructor: Image . asset (
        if (Is(j, ".") && j + 1 < T.Count && T[j + 1].Kind == DTok.Ident && (Is(j + 2, "(") || Is(j + 2, "<"))) { name = j + 1; j += 2; }
        if (Is(j, "<")) { j = SkipAngles(j); if (j < 0) return null; }
        if (!Is(j, "(") || Match[j] < 0) return null;
        return Build(name, j);
    }

    /// <summary>The call that a ( opens (null when it is not a call: a parenthesised expression, a lambda's parameters, if (…)).</summary>
    public DartCall? CallFromOpen(int open)
    {
        if (!Is(open, "(") || Match[open] < 0) return null;
        int j = open - 1;
        if (Is(j, ">")) { int lt = AngleOpenBefore(j); if (lt < 0) return null; j = lt - 1; }
        if (j < 0 || T[j].Kind != DTok.Ident || NotCalls.Contains(T[j].Text)) return null;
        return Build(j, open);
    }

    private DartCall Build(int nameTok, int open)
    {
        var c = new DartCall { NameTok = nameTok, OpenTok = open, CloseTok = Match[open] };
        int first = nameTok;
        string name = T[nameTok].Text;
        if (first >= 2 && Is(first - 1, ".") && T[first - 2].Kind == DTok.Ident && IsTypeName(T[first - 2].Text) && IsTypeName(name) == false)
        {
            name = T[first - 2].Text + "." + name;   // Image.asset
            first -= 2;
        }
        if (first >= 2 && Is(first - 1, ".") && T[first - 2].Kind == DTok.Ident && !IsTypeName(T[first - 2].Text) && IsTypeName(name.Split('.')[0]))
            first -= 2;                              // an import prefix: m.Text
        c.Name = name;
        if (first >= 1 && T[first - 1].Kind == DTok.Ident && T[first - 1].Text is "const" or "new")
        {
            if (T[first - 1].Text == "const") c.ConstTok = first - 1;
            first--;
        }
        c.StartTok = first;
        c.Start = T[first].Pos;
        c.NameAt = T[nameTok].Pos;
        c.Open = T[open].Pos;
        c.Close = T[c.CloseTok].Pos;
        ReadItems(open, c.CloseTok, c.Args, named: true);
        return c;
    }

    /// <summary>Split the run between an opener and its closer into its comma-separated items.</summary>
    private void ReadItems(int open, int close, List<DartItem> into, bool named)
    {
        int i = open + 1;
        while (i < close)
        {
            int startTok = i;
            string? name = null;
            if (named && T[i].Kind == DTok.Ident && Is(i + 1, ":") && i + 1 < close) { name = T[i].Text; i += 2; }
            int v = i;
            while (i < close && !Is(i, ","))
            {
                if (IsOpener(i) && Match[i] > i) { i = Match[i] + 1; continue; }
                // <Widget>[…] and Map<String, int>{…} — type arguments hold commas too
                if (Is(i, "<") && (i == v || T[i - 1].Kind == DTok.Ident && IsTypeName(T[i - 1].Text)))
                {
                    int after = SkipAngles(i);
                    if (after > 0 && after <= close) { i = after; continue; }
                }
                i++;
            }
            if (v < i)
                into.Add(new DartItem
                {
                    Name = name, Start = T[startTok].Pos, FirstTok = v, LastTok = i - 1,
                    ValueStart = T[v].Pos, ValueEnd = T[i - 1].End, CommaTok = i < close ? i : -1,
                });
            if (i < close) i++;   // the comma
        }
    }

    public DartList? ListAt(int open)
    {
        if (!Is(open, "[") || Match[open] < 0) return null;
        var l = new DartList { OpenTok = open, CloseTok = Match[open] };
        ReadItems(open, l.CloseTok, l.Items, named: false);
        return l;
    }

    /// <summary>The call a value is (the value is exactly one call), e.g. the TextStyle(…) of style: TextStyle(…).</summary>
    public DartCall? CallIs(DartItem value)
    {
        int last = value.LastTok;
        if (!Is(last, ")") || Match[last] < 0) return null;
        var c = CallFromOpen(Match[last]);
        return c != null && c.StartTok == value.FirstTok ? c : null;
    }

    /// <summary>The call a value ends with: the copyWith(…) of x.copyWith(…), or the value itself when it is a call.</summary>
    public DartCall? TrailingCall(DartItem value)
    {
        int last = value.LastTok;
        if (!Is(last, ")") || Match[last] < 0) return null;
        return CallFromOpen(Match[last]);
    }

    public DartSlot SlotOf(DartCall call)
    {
        int opener = EnclosingOpener(call.StartTok);
        if (opener < 0) return new DartSlot { Kind = SlotKind.Other };
        string br = T[opener].Text;
        if (br == "[")
        {
            var list = ListAt(opener);
            if (list == null) return new DartSlot { Kind = SlotKind.Other };
            int index = list.Items.FindIndex(e => e.ValueStart <= call.Start && call.End <= e.ValueEnd);
            // the call whose argument the list is:  name: const <Widget>[
            int j = opener - 1;
            if (Is(j, ">")) { int lt = AngleOpenBefore(j); j = lt > 0 ? lt - 1 : j; }
            if (IsIdent(j, "const")) j--;
            string? argName = Is(j, ":") && j >= 1 && T[j - 1].Kind == DTok.Ident ? T[j - 1].Text : null;
            int ownerOpen = EnclosingOpener(opener);
            var owner = ownerOpen >= 0 && T[ownerOpen].Text == "(" ? CallFromOpen(ownerOpen) : null;
            if (owner != null && !owner.Args.Any(a => a.ValueStart <= T[opener].Pos && T[list.CloseTok].End <= a.ValueEnd)) owner = null;
            return new DartSlot { Kind = index >= 0 ? SlotKind.ListItem : SlotKind.Other, List = list, Index = index, Owner = owner, ArgName = argName };
        }
        if (br == "(")
        {
            var owner = CallFromOpen(opener);
            if (owner == null) return new DartSlot { Kind = SlotKind.Other };
            var arg = owner.Args.FirstOrDefault(a => a.ValueStart <= call.Start && call.End <= a.ValueEnd);
            if (arg == null) return new DartSlot { Kind = SlotKind.Other, Owner = owner };
            bool whole = arg.ValueStart == call.Start && arg.ValueEnd == call.End;
            return new DartSlot { Kind = whole ? SlotKind.Arg : SlotKind.Other, Owner = owner, ArgName = arg.Name };
        }
        // a block or a class body: return X;  /  => X;
        int before = call.StartTok - 1;
        bool root = IsIdent(before, "return") || Is(before, "=>");
        return new DartSlot { Kind = root ? SlotKind.Root : SlotKind.Other };
    }

    // ───────────────────────────── const ─────────────────────────────

    /// <summary>The const keywords that make <paramref name="pos"/> part of a constant: an enclosing const call or list,
    /// and a const declaration whose value holds it. (Value, IsDeclaration) per keyword.</summary>
    public List<(int Tok, bool Declaration)> ConstsAround(int pos)
    {
        var found = new List<(int, bool)>();
        for (int k = 0; k < T.Count; k++)
        {
            if (T[k].Pos >= pos) break;
            if (!IsIdent(k, "const")) continue;
            int j = k + 1;
            while (j < T.Count && (T[j].Kind == DTok.Ident || Is(j, "."))) j++;
            if (Is(j, "<")) { int a = SkipAngles(j); if (a > 0) j = a; }
            if (IsOpener(j) && Match[j] > j)
            {
                if (T[j].Pos < pos && pos < T[Match[j]].End) found.Add((k, false));
            }
            else if (Is(j, "=") && j > k + 1)
            {
                int end = j + 1;
                while (end < T.Count && !Is(end, ";"))
                {
                    if (IsOpener(end) && Match[end] > end) end = Match[end];
                    end++;
                }
                if (end < T.Count && T[j].Pos < pos && pos < T[end].Pos) found.Add((k, true));
            }
        }
        return found;
    }

    /// <summary>Changes that make <paramref name="pos"/> no longer constant: an enclosing const keyword is removed
    /// (a const declaration becomes final), so a value that isn't a constant may go there.</summary>
    public List<Replace> DropConsts(int pos)
    {
        var changes = new List<Replace>();
        foreach (var (tok, decl) in ConstsAround(pos))
        {
            var t = T[tok];
            if (decl) { changes.Add(new Replace(t.Pos, t.End, "final")); continue; }
            int e = t.End;
            while (e < Text.Length && Text[e] is ' ' or '\t') e++;
            if (e < Text.Length && Text[e] is '\r' or '\n') e = t.End;   // "const\n  Text(" keeps its line break
            changes.Add(new Replace(t.Pos, e, ""));
        }
        return changes;
    }

    public bool InConst(int pos) => ConstsAround(pos).Count > 0;

    /// <summary>Constructors that may be called inside a constant (anything else is treated as not constant).</summary>
    internal static readonly HashSet<string> ConstClasses = new(StringComparer.Ordinal)
    {
        "Color", "TextStyle", "EdgeInsets", "EdgeInsetsDirectional", "BorderRadius", "BorderRadiusDirectional", "Radius", "BoxDecoration",
        "RoundedRectangleBorder", "StadiumBorder", "CircleBorder", "BeveledRectangleBorder", "BorderSide", "BoxShadow", "Shadow", "Offset", "Size",
        "BoxConstraints", "Alignment", "AlignmentDirectional", "LinearGradient", "RadialGradient", "Duration", "Text", "Icon", "Padding", "Center",
        "Align", "SizedBox", "Expanded", "Flexible", "Spacer", "Divider", "VerticalDivider", "Card", "Column", "Row", "Wrap", "Stack", "Positioned",
        "ListTile", "Chip", "CircularProgressIndicator", "LinearProgressIndicator", "FlutterLogo", "Placeholder", "Opacity", "ClipRRect", "ClipOval",
        "DecoratedBox", "ColoredBox", "ConstrainedBox", "AspectRatio", "FittedBox", "SafeArea", "InputDecoration", "OutlineInputBorder",
        "UnderlineInputBorder", "TextField", "CircleAvatar", "IconThemeData", "Visibility", "AppBar", "Scaffold", "Tooltip", "Baseline", "IntrinsicHeight",
    };

    /// <summary>Named constructors that are const (any other one is treated as not constant).</summary>
    internal static readonly HashSet<string> ConstNamed = new(StringComparer.Ordinal)
    {
        "EdgeInsets.all", "EdgeInsets.symmetric", "EdgeInsets.only", "EdgeInsets.fromLTRB", "EdgeInsetsDirectional.only",
        "EdgeInsetsDirectional.symmetric", "EdgeInsetsDirectional.fromSTEB", "BorderRadius.all", "BorderRadius.only", "BorderRadius.vertical",
        "BorderRadius.horizontal", "BorderRadiusDirectional.all", "BorderRadiusDirectional.only", "Radius.circular", "Radius.elliptical",
        "Color.fromARGB", "Color.fromRGBO", "SizedBox.shrink", "SizedBox.expand", "SizedBox.square", "BoxConstraints.tightFor",
        "BoxConstraints.expand", "Size.square", "Size.fromWidth", "Size.fromHeight", "Text.rich",
    };

    /// <summary>True when <paramref name="code"/> is a constant expression (only literals, Class.constant names and
    /// constructors from <see cref="ConstClasses"/>) — so it may go inside a const without dropping it.</summary>
    public static bool ConstSafe(string code)
    {
        var t = DartLexer.Lex(code);
        for (int i = 0; i < t.Count; i++)
        {
            var tk = t[i];
            if (tk.Kind == DTok.Str && tk.Interp != null) return false;
            if (tk.Kind == DTok.Punct && tk.Text is "=>" or "{" or "?." or "!") return false;
            if (tk.Kind != DTok.Ident) continue;
            if (tk.Text is "context" or "widget" or "this" or "setState" or "Theme" or "MediaQuery") return false;
            if (tk.Text is "const" or "true" or "false" or "null") continue;
            // a dotted run: Colors.red · FontWeight.bold · EdgeInsets.all( · Colors.red.shade100 (not constant)
            int j = i, parts = 1;
            while (j + 2 < t.Count && t[j + 1].Kind == DTok.Punct && t[j + 1].Text == "." && t[j + 2].Kind == DTok.Ident) { j += 2; parts++; }
            bool call = j + 1 < t.Count && t[j + 1].Kind == DTok.Punct && t[j + 1].Text is "(" or "<";
            string head = tk.Text;
            if (call)
            {
                if (parts > 2) return false;
                // a named constructor is constant only when it is one of these (BorderRadius.circular and Border.all are not)
                if (parts == 2 ? !ConstNamed.Contains(head + "." + t[j].Text) : !ConstClasses.Contains(head)) return false;
            }
            else
            {
                bool argName = parts == 1 && j + 1 < t.Count && t[j + 1].Kind == DTok.Punct && t[j + 1].Text == ":";
                if (!argName)
                {
                    if (parts != 2) return false;            // a variable, or Colors.red.shade100
                    if (!IsTypeName(head)) return false;     // theme.primary — a field of an object
                }
            }
            i = j;
        }
        return true;
    }
}
