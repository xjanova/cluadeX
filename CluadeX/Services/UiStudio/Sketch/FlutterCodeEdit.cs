using System.Globalization;
using System.Text;

namespace CluadeX.Services.UiStudio.Sketch;

/// <summary>The result of one edit of a Dart file by the UI Studio's tools.</summary>
public sealed class FlutterEdit
{
    public bool Ok { get; init; }
    /// <summary>Why nothing changed: a localization key (studio.fedit.*).</summary>
    public string? Error { get; init; }
    /// <summary>The whole new file text (null when nothing changed).</summary>
    public string? Text { get; init; }
    /// <summary>The widget to select after the edit: its name's line and column in the new text (0: none).</summary>
    public int FocusLine { get; init; }
    public int FocusColumn { get; init; }
    internal string Old = "";
    internal List<Replace> Changes = new();

    internal static FlutterEdit Fail(string why) => new() { Ok = false, Error = "studio.fedit." + why };

    /// <summary>Where a place of the old text is in the new one (null: the edit replaced it).</summary>
    public (int Line, int Column)? Map(int line, int column)
    {
        if (Text == null) return (line, column);
        int old = OffsetIn(Old, line, column);
        if (old < 0) return null;
        int shift = 0;
        foreach (var c in Changes)
        {
            if (c.Start == c.End) { if (c.Start <= old) shift += c.Text.Length; continue; }   // an insertion before it
            if (c.End <= old) { shift += c.Text.Length - (c.End - c.Start); continue; }
            if (c.Start <= old) return null;                                                 // replaced
        }
        return LineColIn(Text, old + shift);
    }

    internal static int OffsetIn(string text, int line, int column)
    {
        int at = 0;
        for (int l = 1; l < line; l++)
        {
            at = text.IndexOf('\n', at);
            if (at < 0) return -1;
            at++;
        }
        at += column - 1;
        return at >= 0 && at <= text.Length ? at : -1;
    }

    internal static (int Line, int Column) LineColIn(string text, int offset)
    {
        int line = 1, start = 0;
        for (int i = 0; i < offset && i < text.Length; i++)
            if (text[i] == '\n') { line++; start = i + 1; }
        return (line, offset - start + 1);
    }
}

/// <summary>What the inspector can change on one widget, and what its code says now.</summary>
public sealed class FlutterWidgetInfo
{
    public string Name { get; init; } = "";
    /// <summary>The literal text it shows (its own, or the label of a button / tile).</summary>
    public string? Text { get; init; }
    /// <summary>Why the text can't be typed in here (a key): it comes from data or the app's translations.</summary>
    public string? TextNote { get; init; }
    /// <summary>The translation key its text reads (<c>context.l10n.title</c> → "title"); the text tool changes it in the .arb file.</summary>
    public string? L10nKey { get; init; }
    /// <summary>The key is called with values (<c>l10n.greeting(name)</c>): a message with placeholders.</summary>
    public bool L10nCall { get; init; }
    /// <summary>Its text is a parameter of the widget class it is written in (<c>Text(label)</c> in class _Tag): the
    /// text tool types over what the widget's use passes (<c>_Tag(label: 'ยอดขาย')</c>).</summary>
    public FlutterParamText? ParamText { get; init; }
    public bool CanText { get; init; }
    public bool CanTextStyle { get; init; }
    public double? FontSize { get; init; }
    public string? FontWeight { get; init; }
    public string? TextColor { get; init; }
    public string? TextAlign { get; init; }
    public bool CanColor { get; init; }
    public string? Color { get; init; }
    public bool CanPadding { get; init; }
    public double? Padding { get; init; }
    public bool CanMargin { get; init; } = true;
    public double? Margin { get; init; }
    public bool CanRadius { get; init; } = true;
    public double? Radius { get; init; }
    public bool CanSize { get; init; } = true;
    public double? Width { get; init; }
    public double? Height { get; init; }
    public bool InList { get; init; }
    public bool CanMoveUp { get; init; }
    public bool CanMoveDown { get; init; }
    public bool CanDelete { get; init; } = true;
    public bool CanUnwrap { get; init; }
    public bool CanExpanded { get; init; }
    /// <summary>It holds a list of widgets (Column, Row…): an inserted widget goes at its end.</summary>
    public bool HasChildren { get; init; }
    /// <summary>Its own const keyword or an enclosing one.</summary>
    public bool InConst { get; init; }
}

/// <summary>A widget's text that is one of its own constructor's parameters — <c>Text(label)</c> in class _Tag,
/// <c>Text(widget.label)</c> in its State: what shows is what each use passes, so the text is written there (the
/// <c>label:</c> argument of the <c>_Tag(…)</c> call that drew it). Or a parameter of a helper function that builds
/// widgets (<see cref="Helper"/>): <c>Text(label)</c> in <c>Widget _row(String label)</c>, written at the
/// <c>_row('ชื่อ')</c> call that drew it.</summary>
public sealed class FlutterParamText
{
    /// <summary>The widget class whose constructor takes the text ("_Tag"); for a helper, the class it is written in
    /// ("" at the top of a file).</summary>
    public string Class { get; init; } = "";
    /// <summary>The parameter ("label").</summary>
    public string Param { get; init; } = "";
    /// <summary>Each constructor that takes it as <c>this.label</c> ("" for the unnamed one, "small" for _Tag.small) →
    /// its place among that constructor's positional parameters (-1: a named parameter). A helper has one, "".</summary>
    public IReadOnlyDictionary<string, int> Ctors { get; init; } = new Dictionary<string, int>();
    /// <summary>The constructors (as in <see cref="Ctors"/>) that give the parameter a default value → that default when
    /// it is a plain string literal (<c>this.mood = 'ปกติ'</c>, <c>[this.label = 'x']</c>), null when it is something
    /// else. What shows where a use leaves the parameter out.</summary>
    public IReadOnlyDictionary<string, string?> Defaults { get; init; } = new Dictionary<string, string?>();
    /// <summary>What the Text shows when the parameter is null: its own fallback literal (<c>Text(hint ?? 'ไม่มีคำใบ้')</c>).</summary>
    public string? Fallback { get; init; }
    /// <summary>The text is a parameter of a helper function, not of a widget's constructor (null: a widget's).</summary>
    public FlutterHelper? Helper { get; init; }

    /// <summary>What shows where a use of <paramref name="ctor"/> leaves the parameter out (null: not a plain literal).</summary>
    public string? DefaultShown(string ctor) => Defaults.TryGetValue(ctor, out var d) ? d : Fallback;
}

public enum FlutterHelperKind
{
    /// <summary>A method of the class (<c>Widget _row(String label) =&gt; …</c>): called in that class.</summary>
    Method,
    /// <summary>A function declared inside another one (<c>Widget line(…) =&gt; …</c> in build): called in that block.</summary>
    Local,
    /// <summary>A private function at the top of its file: called in that file.</summary>
    TopLevel,
}

/// <summary>A function that builds widgets out of its parameters — a method of a class, a function declared inside
/// build, a private function of its file. A call of it creates no widget of its own, so Flutter's inspector has no box
/// that says which call drew a widget it built: every <c>_row(…)</c>'s Text has the same file, line and column.</summary>
public sealed class FlutterHelper
{
    public string Name { get; init; } = "";
    public FlutterHelperKind Kind { get; init; }
    /// <summary>Where its name is written in its declaration (1-based).</summary>
    public int Line { get; init; }
    public int Column { get; init; }
}

/// <summary>Which call of a helper function drew a widget: its name's line and column (0: not known, see
/// <see cref="Why"/>), and the box it was told apart by (the widget whose argument holds that call).</summary>
public sealed class FlutterHelperUse
{
    public int Line { get; init; }
    public int Column { get; init; }
    public WidgetBox? Anchor { get; init; }
    /// <summary>How many calls of it there are.</summary>
    public int Calls { get; init; }
    /// <summary>Why no call is given: "none" (no call of it), "many" (several, and no picture to tell them apart),
    /// "unsure" (several, and the picture doesn't tell which one for certain).</summary>
    public string? Why { get; init; }
}

/// <summary>What a use of a widget passes for one of its text parameters.</summary>
public enum FlutterArgKind
{
    /// <summary>A plain string literal: the text tool types over it.</summary>
    Literal,
    /// <summary>A translation (<c>context.l10n.title</c>): its message in the .arb file is what to type over.</summary>
    Translation,
    /// <summary>A parameter of the widget the use is written in (<c>_Tag(label: title)</c> inside _Section): the text
    /// comes from further up, from where _Section is used.</summary>
    PassedOn,
    /// <summary>Built from data: an interpolation, a variable, a call.</summary>
    Data,
    /// <summary>The use leaves it out: the parameter's default shows (<see cref="FlutterArgText.Literal"/> when that is a
    /// plain literal — typing over it writes the argument at that use).</summary>
    Default,
    /// <summary>No call of that widget there (the code changed under the picture).</summary>
    Stale,
}

public sealed class FlutterArgText
{
    public FlutterArgKind Kind { get; init; }
    /// <summary>The literal the use passes — or for <see cref="FlutterArgKind.Default"/>, the default that shows.</summary>
    public string? Literal { get; init; }
    /// <summary>A use that leaves it out can be given it: a named parameter, or a positional one whose earlier
    /// positional arguments the use passes.</summary>
    public bool Addable { get; init; }
    public string? L10nKey { get; init; }
    public bool L10nCall { get; init; }
    public FlutterParamText? PassedOn { get; init; }
}

/// <summary>Where a widget dragged onto the real picture would go.</summary>
public sealed class FlutterDropPlan
{
    public string File { get; init; } = "";
    /// <summary>The widget that takes it: a list parent (Column, Row…) or a widget with an empty child slot.</summary>
    public int OwnerLine { get; init; }
    public int OwnerColumn { get; init; }
    public string Owner { get; init; } = "";
    /// <summary>The place in the owner's list (-1: as its child).</summary>
    public int Index { get; init; } = -1;
    /// <summary>Where to draw the insertion mark (logical pixels): a line for a list, the owner's box for a child slot.</summary>
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }
    public bool Vertical { get; init; } = true;
}

/// <summary>
/// The UI Studio's widget tools on real Dart code. A widget is found by the place Flutter's widget inspector gives
/// (the line and column of its constructor's name); an edit replaces only the characters it must and keeps the
/// file's formatting. Inside a const the tools keep it when the new value is a constant, and drop the enclosing
/// const keyword when it is not (dropping one is always valid code; the save check may add it back).
/// </summary>
public static class FlutterCodeEdit
{
    private static readonly HashSet<string> TextLike = new(StringComparer.Ordinal) { "Text", "SelectableText" };
    private static readonly HashSet<string> Buttons = new(StringComparer.Ordinal) { "ElevatedButton", "FilledButton", "OutlinedButton", "TextButton" };
    private static readonly HashSet<string> SingleChild = new(StringComparer.Ordinal)
    {
        "Container", "Padding", "Center", "Align", "SizedBox", "Card", "DecoratedBox", "ColoredBox", "Material", "InkWell", "GestureDetector",
        "ConstrainedBox", "AnimatedContainer", "ClipRRect", "ClipOval", "Opacity", "FittedBox", "AspectRatio", "SingleChildScrollView",
        "LimitedBox", "FractionallySizedBox", "Transform", "AnimatedOpacity", "AnimatedPadding", "Ink", "UnconstrainedBox", "Expanded", "Flexible",
        "SafeArea", "Positioned", "Hero", "Tooltip", "Semantics", "IntrinsicHeight", "IntrinsicWidth", "Baseline",
    };
    /// <summary>Single-child widgets whose child may be left out (deleting their child removes the argument).</summary>
    private static readonly HashSet<string> ChildOptional = new(StringComparer.Ordinal)
    {
        "Container", "Padding", "Center", "Align", "SizedBox", "Card", "DecoratedBox", "ColoredBox", "Material", "InkWell", "GestureDetector",
        "ConstrainedBox", "AnimatedContainer", "ClipRRect", "ClipOval", "Opacity", "FittedBox", "AspectRatio", "SingleChildScrollView",
        "LimitedBox", "FractionallySizedBox", "Transform", "AnimatedOpacity", "AnimatedPadding", "Ink", "UnconstrainedBox", "Tooltip", "Semantics",
    };
    private static readonly Dictionary<string, string[]> OptionalSlots = new(StringComparer.Ordinal)
    {
        ["Scaffold"] = new[] { "appBar", "body", "floatingActionButton", "drawer", "endDrawer", "bottomNavigationBar", "bottomSheet" },
        ["AppBar"] = new[] { "title", "leading", "actions", "bottom", "flexibleSpace" },
        ["SliverAppBar"] = new[] { "title", "leading", "actions", "bottom", "flexibleSpace" },
        ["ListTile"] = new[] { "leading", "title", "subtitle", "trailing" },
        ["InputDecoration"] = new[] { "prefixIcon", "suffixIcon", "prefix", "suffix", "icon", "label" },
        ["Card"] = new[] { "child" },
    };
    private static readonly HashSet<string> ListParents = new(StringComparer.Ordinal)
    {
        "Column", "Row", "Wrap", "Stack", "ListView", "Flex", "IndexedStack", "GridView", "ExpansionTile", "OverflowBar", "ButtonBar",
    };
    private static readonly HashSet<string> Flex = new(StringComparer.Ordinal) { "Column", "Row", "Flex" };

    // ───────────────────────────── reading ─────────────────────────────

    /// <summary>What the tools can do with the widget whose name is at line:column (null: no widget call there).</summary>
    public static FlutterWidgetInfo? Inspect(string text, int line, int column)
    {
        var d = new DartDoc(text);
        var c = d.CallAt(line, column);
        if (c == null) return null;
        var slot = d.SlotOf(c);
        string cls = c.ClassName;
        var tc = TextCallFor(d, c);
        string? textNote = null, literal = null, l10nKey = null;
        bool l10nCall = false;
        FlutterParamText? param = null;
        if (tc != null)
        {
            var a = tc.Positional(0);
            if (a != null && LiteralOf(d, a) is { } s) literal = s;
            else if (a != null && TranslationKey(d, a, out l10nCall) is { } key) { l10nKey = key; textNote = "studio.fedit.text.l10n"; }
            else
            {
                textNote = a != null && IsL10n(d.Text[a.ValueStart..a.ValueEnd]) ? "studio.fedit.text.l10n" : "studio.fedit.text.data";
                // (the reason stays until the use is found: only the call that drew this widget knows its text)
                if (a != null) param = ParamOf(d, a);
            }
        }
        var style = tc?.Named("style");
        DartCall? styleCall = style != null ? d.CallIs(style) ?? d.TrailingCall(style) : null;
        var deco = c.Named("decoration") is { } dv ? d.CallIs(dv) : null;
        var owner = slot.Kind == SlotKind.Arg && slot.ArgName == "child" ? slot.Owner : null;
        return new FlutterWidgetInfo
        {
            Name = c.Name,
            Text = literal,
            TextNote = textNote,
            L10nKey = l10nKey,
            L10nCall = l10nCall,
            ParamText = param,
            CanText = literal != null,
            CanTextStyle = tc != null,
            FontSize = Num(d, styleCall?.Named("fontSize")),
            FontWeight = styleCall?.Named("fontWeight") is { } fw ? Src(d, fw).Replace("FontWeight.", "") : null,
            TextColor = styleCall?.Named("color") is { } tcol ? ColorText(d, tcol) : null,
            TextAlign = tc?.Named("textAlign") is { } ta ? Src(d, ta).Replace("TextAlign.", "") : null,
            CanColor = ColorSlot(cls) != null,
            Color = ColorValue(d, c),
            CanPadding = PaddingArg(cls) != null,
            Padding = PaddingArg(cls) is { } pa ? Num(d, c.Named(pa)) : null,
            CanMargin = cls != "Padding",
            Margin = cls is "Container" or "AnimatedContainer" or "Card" ? Num(d, c.Named("margin")) : owner?.ClassName == "Padding" ? Num(d, owner.Named("padding")) : null,
            Radius = cls switch
            {
                "Container" or "AnimatedContainer" => Num(d, deco?.Named("borderRadius")),
                "ClipRRect" => Num(d, c.Named("borderRadius")),
                _ => owner?.ClassName == "ClipRRect" ? Num(d, owner.Named("borderRadius")) : null,
            },
            Width = cls is "SizedBox" or "Container" or "AnimatedContainer" ? Num(d, c.Named("width")) : owner?.ClassName == "SizedBox" ? Num(d, owner.Named("width")) : null,
            Height = cls is "SizedBox" or "Container" or "AnimatedContainer" ? Num(d, c.Named("height")) : owner?.ClassName == "SizedBox" ? Num(d, owner.Named("height")) : null,
            InList = slot.Kind == SlotKind.ListItem,
            CanMoveUp = slot.Kind == SlotKind.ListItem && slot.Index > 0,
            CanMoveDown = slot.Kind == SlotKind.ListItem && slot.Index < slot.List!.Items.Count - 1,
            CanUnwrap = UnwrapTarget(d, c) != null,
            CanExpanded = slot.Kind == SlotKind.ListItem && slot.Owner != null && Flex.Contains(slot.Owner.ClassName),
            HasChildren = ChildrenList(d, c) != null,
            InConst = d.InConst(c.Open + 1),
        };
    }

    /// <summary>The column where a call of <paramref name="widget"/> starts on a line (the inspector's convention: a
    /// named constructor's name, "asset" of Image.asset) — for a block picked on the wireframe, which knows only its
    /// line. 0: none there.</summary>
    public static int ColumnOf(string text, int line, string widget)
    {
        var d = new DartDoc(text);
        int from = d.Offset(line, 1);
        if (from < 0) return 0;
        int to = d.NextLineStart(from);
        string[] parts = widget.Split('.');
        for (int i = 0; i < d.T.Count && d.T[i].Pos < to; i++)
        {
            if (d.T[i].Pos < from || d.T[i].Kind != DTok.Ident || d.T[i].Text != parts[0]) continue;
            int k = i;
            if (parts.Length == 2)
            {
                if (!d.Is(i + 1, ".") || !d.IsIdent(i + 2, parts[1])) continue;
                k = i + 2;
            }
            if (d.CallAtOffset(d.T[k].Pos) is { } c) return d.LineCol(c.NameAt).Column;
        }
        return 0;
    }

    /// <summary>The Text call whose words a widget shows: itself, or the label of a button, chip or tile.</summary>
    internal static DartCall? TextCallFor(DartDoc d, DartCall c)
    {
        if (TextLike.Contains(c.ClassName) && c.Name is "Text" or "SelectableText") return c;
        foreach (var key in new[] { "child", "label", "title", "text" })
            if (c.Named(key) is { } v && d.CallIs(v) is { } inner && inner.Name is "Text" or "SelectableText") return inner;
        return null;
    }

    /// <summary>The value of a string literal (adjacent literals joined); null when it is built from data.</summary>
    internal static string? LiteralOf(DartDoc d, DartItem a)
    {
        var sb = new StringBuilder();
        for (int i = a.FirstTok; i <= a.LastTok; i++)
        {
            var t = d.T[i];
            if (t.Kind != DTok.Str || t.Interp != null) return null;
            sb.Append(t.Text);
        }
        return sb.ToString();
    }

    private static bool IsL10n(string src) =>
        src.Contains("l10n", StringComparison.OrdinalIgnoreCase) || src.Contains("AppLocalizations", StringComparison.Ordinal)
        || System.Text.RegularExpressions.Regex.IsMatch(src, @"^\s*(S|L10n|AppL10n|Strings|tr)\b") || src.Contains(".tr(", StringComparison.Ordinal);

    /// <summary>The translation key a value reads — <c>context.l10n.title</c>, <c>AppL10n.of(context).title</c>,
    /// <c>AppLocalizations.of(context)!.title</c>, <c>l10n.title</c> (a variable or parameter that holds the app's
    /// translations), <c>… ?? 'fallback'</c> — and whether it is called with values (<c>l10n.greeting(name)</c>: a
    /// message with placeholders). Null for anything else, data that merely ends in a name included (<c>stats.total</c>).</summary>
    internal static string? TranslationKey(DartDoc d, DartItem a, out bool call)
    {
        call = false;
        int first = a.FirstTok, last = a.LastTok;
        for (int k = first; k <= last; k++)
        {
            if (d.IsOpener(k) && d.Match[k] > k) { k = d.Match[k]; continue; }
            if (d.Is(k, "??")) { last = k - 1; break; }
        }
        bool called = d.Is(last, ")") && d.Match[last] > first;
        if (called) last = d.Match[last] - 1;
        if (last - 2 < first || d.T[last].Kind != DTok.Ident || !(d.Is(last - 1, ".") || d.Is(last - 1, "?."))) return null;
        string key = d.T[last].Text;
        // gen-l10n's keys are Dart identifiers that start with a lowercase letter
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z][A-Za-z0-9_]*$")) return null;
        int end = last - 2;
        if (d.Is(end, "!")) end--;
        if (!TranslationsAt(d, first, end, 0)) return null;
        call = called;
        return key;
    }

    /// <summary>The tokens [from, to] are the app's translations object.</summary>
    private static bool TranslationsAt(DartDoc d, int from, int to, int depth)
    {
        if (depth > 3 || to < from) return false;
        if (d.Is(to, ")") && d.Match[to] > from)
        {
            // AppL10n.of(context) — and Localizations.of<AppL10n>(context, AppL10n)
            int j = d.Match[to] - 1;
            if (d.Is(j, ">")) j = d.AngleOpenBefore(j) - 1;
            return j - 2 == from && d.IsIdent(j, "of") && d.Is(j - 1, ".") && d.T[from].Kind == DTok.Ident && L10nClass(d.T[from].Text);
        }
        if (d.T[to].Kind != DTok.Ident) return false;
        string name = d.T[to].Text;
        // context.l10n, widget.l10n — and S.current
        if (to - 2 >= from && d.Is(to - 1, "."))
            return TrustedName(name) || name == "current" && to - 2 == from && L10nClass(d.T[from].Text);
        if (to != from) return false;
        // a variable or parameter: what it is declared as decides; a name like l10n speaks for itself
        return DeclaredTranslations(d, name, d.T[from].Pos, depth) ?? TrustedName(name);
    }

    /// <summary>What <paramref name="name"/> is declared as, nearest before <paramref name="pos"/>: true when it holds the
    /// app's translations (<c>final t = AppL10n.of(context);</c>, <c>AppL10n l10n</c>), false when it holds something
    /// else (<c>final s = ref.watch(stats);</c>), null when the file doesn't say.</summary>
    private static bool? DeclaredTranslations(DartDoc d, string name, int pos, int depth)
    {
        int startTok = d.T.FindIndex(t => t.Pos >= pos);
        for (int i = (startTok < 0 ? d.T.Count : startTok) - 1; i >= 1; i--)
        {
            if (d.T[i].Kind != DTok.Ident || d.T[i].Text != name || d.Is(i - 1, ".")) continue;
            if (d.Is(i + 1, "="))
            {
                // its value, up to the ; that ends the statement
                int e = i + 2;
                while (e < d.T.Count && !d.Is(e, ";") && !d.Is(e, ",") && !(d.T[e].Kind == DTok.Punct && d.T[e].Text is ")" or "]" or "}"))
                    e = d.IsOpener(e) && d.Match[e] > e ? d.Match[e] + 1 : e + 1;
                int v = e - 1;
                if (d.Is(v, "!")) v--;
                return TranslationsAt(d, i + 2, v, depth + 1);
            }
            // a typed declaration without a value: a parameter (AppL10n l10n) or a field (final AppL10n t;)
            int type = d.Is(i - 1, "?") ? i - 2 : i - 1;
            if (type >= 0 && d.T[type].Kind == DTok.Ident && char.IsUpper(d.T[type].Text[0])
                && (d.Is(i + 1, ",") || d.Is(i + 1, ")") || d.Is(i + 1, ";") || d.Is(i + 1, "}")))
                return L10nClass(d.T[type].Text);
        }
        return null;
    }

    /// <summary>A class that holds an app's translations: gen-l10n's (AppLocalizations, AppL10n, any output-class
    /// named so), flutter_intl's S, Flutter's own Localizations.</summary>
    private static bool L10nClass(string cls) =>
        cls is "S" or "L10n" or "Localizations" || cls.EndsWith("L10n", StringComparison.Ordinal) || cls.EndsWith("Localizations", StringComparison.Ordinal)
        || cls.EndsWith("Localization", StringComparison.Ordinal) || cls.EndsWith("Translations", StringComparison.Ordinal);

    /// <summary>A name only translations go by (l10n, appL10n, loc, localizations, i18n, translations).</summary>
    private static bool TrustedName(string name)
    {
        string n = name.TrimStart('_').ToLowerInvariant();
        return n.Contains("l10n", StringComparison.Ordinal) || n is "loc" or "i18n" || n.Contains("localization", StringComparison.Ordinal) || n.Contains("translation", StringComparison.Ordinal);
    }

    // ───────────────────────────── text passed in through a parameter ─────────────────────────────

    /// <summary>A class declaration: its name, the { of its body, and for a State class the widget it belongs to.</summary>
    private sealed record ClassDecl(string Name, int Open, string? StateOf);

    /// <summary>The constructor parameter of the widget around it that a value is: <c>label</c> or <c>this.label</c> in a
    /// widget class whose constructor takes <c>this.label</c>, <c>widget.label</c> in its State class — also
    /// <c>label!</c> and <c>label ?? 'x'</c> (what the use passes is what shows; the literal after ?? shows when it
    /// passes nothing). Or the parameter of the helper function the value is written in (<c>Text(label)</c> in
    /// <c>Widget _row(String label) =&gt; …</c>, in <c>Widget line(…)</c> declared inside build): what its call passes
    /// shows. Null for anything else: a local, a loop variable or a closure's parameter of that name, a State's own
    /// field, a getter, a call on it (<c>label.toUpperCase()</c>).</summary>
    internal static FlutterParamText? ParamOf(DartDoc d, DartItem a)
    {
        int first = a.FirstTok, last = a.LastTok;
        string? fallback = null;
        for (int k = first; k <= last; k++)
        {
            if (d.IsOpener(k) && d.Match[k] > k) { k = d.Match[k]; continue; }
            if (d.Is(k, "??")) { fallback = LiteralIn(d, k + 1, last); last = k - 1; break; }
        }
        if (last > first && d.Is(last, "!")) last--;
        bool viaWidget = false, viaThis = false;
        if (last == first + 2 && d.Is(first + 1, ".") && (d.IsIdent(first, "widget") || d.IsIdent(first, "this")))
        {
            viaWidget = d.IsIdent(first, "widget");
            viaThis = !viaWidget;
        }
        else if (last != first) return null;
        var t = d.T[last];
        if (t.Kind != DTok.Ident || t.Text is "this" or "widget" or "super" or "null" or "true" or "false") return null;
        string name = t.Text;
        if (!viaWidget && !viaThis)
        {
            // a parameter of the function it is written in hides the field (the nearest declaration of the name decides)
            var (helper, decided) = HelperParam(d, first, name, fallback);
            if (decided) return helper;
        }
        var around = ClassAround(d, first);
        if (around == null) return null;
        var owner = around;
        if (viaWidget)
        {
            // State<Foo>: widget.label is Foo's parameter (Foo is written next to its State)
            if (around.StateOf == null || FindClass(d, around.StateOf) is not { } w) return null;
            owner = w;
        }
        else if (!viaThis && Shadowed(d, around.Open, first, name)) return null;
        var ctors = CtorsTaking(d, owner, name, out var defaults);
        return ctors.Count == 0 ? null : new FlutterParamText { Class = owner.Name, Param = name, Ctors = ctors, Defaults = defaults, Fallback = fallback };
    }

    /// <summary>The value of the tokens [from, to] when they are only plain string literals (adjacent ones joined).</summary>
    private static string? LiteralIn(DartDoc d, int from, int to)
    {
        if (to < from) return null;
        var sb = new StringBuilder();
        for (int i = from; i <= to; i++)
        {
            if (d.T[i].Kind != DTok.Str || d.T[i].Interp != null) return null;
            sb.Append(d.T[i].Text);
        }
        return sb.ToString();
    }

    /// <summary>The class whose body holds token <paramref name="tok"/> (classes don't nest in Dart).</summary>
    private static ClassDecl? ClassAround(DartDoc d, int tok)
    {
        for (int o = d.EnclosingOpener(tok); o >= 0; o = d.EnclosingOpener(o))
            if (d.Is(o, "{") && ClassHeader(d, o) is { } c) return c;
        return null;
    }

    /// <summary>The class a { opens the body of: <c>class Name&lt;T&gt; extends State&lt;W&gt; with M implements I {</c>
    /// (null: a method body, a block, an extension, a mixin).</summary>
    private static ClassDecl? ClassHeader(DartDoc d, int open)
    {
        for (int k = open - 1; k >= 0 && k > open - 80; k--)
        {
            if (d.IsIdent(k, "class"))
            {
                if (k + 1 >= open || d.T[k + 1].Kind != DTok.Ident) return null;
                string? stateOf = null;
                for (int j = k + 2; j < open; j++)
                {
                    if (!d.IsIdent(j, "extends")) continue;
                    int s = j + 1;
                    if (d.Is(s + 1, ".")) s += 2;   // a prefixed base: m.State<Foo>
                    if (s + 2 < open && d.T[s].Kind == DTok.Ident && d.T[s].Text.EndsWith("State", StringComparison.Ordinal) && d.Is(s + 1, "<") && d.T[s + 2].Kind == DTok.Ident)
                        stateOf = d.T[s + 2].Text;
                    break;
                }
                return new ClassDecl(d.T[k + 1].Text, open, stateOf);
            }
            var tk = d.T[k];
            if (tk.Kind == DTok.Ident || tk.Kind == DTok.Punct && tk.Text is "<" or ">" or ">>" or "," or "." or "?") continue;
            return null;
        }
        return null;
    }

    private static ClassDecl? FindClass(DartDoc d, string name)
    {
        for (int i = 0; i + 1 < d.T.Count; i++)
        {
            if (!d.IsIdent(i, "class") || !d.IsIdent(i + 1, name)) continue;
            for (int k = i + 2; k < d.T.Count && k < i + 80; k++)
            {
                if (d.Is(k, ";")) break;
                if (d.Is(k, "{") && d.Match[k] > k) return ClassHeader(d, k);
            }
        }
        return null;
    }

    /// <summary>The constructors of a class that take <paramref name="name"/> as <c>this.name</c>: the constructor's name
    /// ("" for the unnamed one) → the parameter's place among its positional ones (-1: named); and those that give it a
    /// default → that default when it is a plain string literal (else null).</summary>
    private static Dictionary<string, int> CtorsTaking(DartDoc d, ClassDecl c, string name, out Dictionary<string, string?> defaults)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        defaults = new Dictionary<string, string?>(StringComparer.Ordinal);
        int close = d.Match[c.Open];
        for (int k = c.Open + 1; k < close; k++)
        {
            if (!d.IsOpener(k) || d.Match[k] <= k) continue;
            if (d.Is(k, "(") && CtorNameAt(d, k, c.Name) is { } ctor && ParamPlace(d, k, name, field: true) is { } at && map.TryAdd(ctor, at.At) && at.HasDefault)
                defaults[ctor] = at.Default;
            k = d.Match[k];
        }
        return map;
    }

    /// <summary>The ( opens the parameters of a constructor of <paramref name="cls"/> (<c>Name(</c>, <c>const Name(</c>,
    /// <c>Name.named(</c> at the start of a member of its class body — not a factory, not a call): its name ("" for
    /// the unnamed one).</summary>
    private static string? CtorNameAt(DartDoc d, int open, string cls)
    {
        int j = open - 1;
        string ctor = "";
        if (j >= 2 && d.T[j].Kind == DTok.Ident && d.Is(j - 1, ".") && d.IsIdent(j - 2, cls)) { ctor = d.T[j].Text; j -= 2; }
        else if (!d.IsIdent(j, cls)) return null;
        int before = j - 1;
        if (d.IsIdent(before, "const")) before--;
        // after the body's {, a member's ; or }, or an annotation's )
        return before < 0 || d.Is(before, "{") || d.Is(before, ";") || d.Is(before, "}") || d.Is(before, ")") ? ctor : null;
    }

    /// <summary>One parameter of a function or constructor: its place among the positional ones (-1: named), whether it
    /// has a default value and that default when it is a plain string literal, and its name's token.</summary>
    private sealed record ParamAt(int At, bool HasDefault, string? Default, int Tok);

    /// <summary>Where a parameter is among a parameter list's: <c>this.name</c> (<paramref name="field"/>: a constructor's)
    /// or a plain <c>String name</c> / <c>name</c> (a function's). Null when the list has no such parameter.</summary>
    private static ParamAt? ParamPlace(DartDoc d, int open, string name, bool field)
    {
        int close = d.Match[open];
        bool named = false;
        int index = 0, start = open + 1;
        for (int k = open + 1; k <= close; k++)
        {
            bool end = k == close || d.Is(k, ",") || d.Is(k, "}") || d.Is(k, "]");
            if (!end && d.IsOpener(k) && d.Match[k] > k)
            {
                // the {…} of the named parameters, the […] of the optional ones: their parameters are read one by one;
                // any other bracket belongs to one parameter (a default value, a function type)
                if (k == start && (d.Is(k, "{") || d.Is(k, "["))) { named = d.Is(k, "{"); start = k + 1; continue; }
                k = d.Match[k];
                continue;
            }
            if (!end) continue;
            if (k > start && ParamName(d, start, k, field) is int tok && d.T[tok].Text == name)
            {
                // a default value: what follows its = (only a plain literal is something to show)
                int eq = -1;
                for (int j = tok + 1; j < k; j++)
                {
                    if (d.IsOpener(j) && d.Match[j] > j && d.Match[j] < k) { j = d.Match[j]; continue; }
                    if (d.Is(j, "=")) { eq = j; break; }
                }
                return new ParamAt(named ? -1 : index, eq > 0, eq > 0 ? LiteralIn(d, eq + 1, k - 1) : null, tok);
            }
            if (k > start && !named) index++;
            start = k + 1;
        }
        return null;
    }

    /// <summary>The name token of one parameter written as the tokens [start, end): <c>this.x</c> for a constructor's field
    /// parameter, else the last name before its = (<c>required String title</c>, <c>String? sub = 'x'</c>, an untyped
    /// <c>label</c>) — never <c>this.x</c> or <c>super.x</c> then. Null: none.</summary>
    private static int? ParamName(DartDoc d, int start, int end, bool field)
    {
        int stop = end;
        for (int j = start; j < end; j++)
        {
            if (d.IsOpener(j) && d.Match[j] > j && d.Match[j] < end) { j = d.Match[j]; continue; }
            if (d.Is(j, "=")) { stop = j; break; }
        }
        if (field)
        {
            for (int j = start; j + 2 < stop; j++)
                if (d.IsIdent(j, "this") && d.Is(j + 1, ".") && d.T[j + 2].Kind == DTok.Ident) return j + 2;
            return null;
        }
        int n = stop - 1;
        return n >= start && d.T[n].Kind == DTok.Ident && !d.Is(n - 1, ".") && d.T[n].Text is not ("required" or "final" or "covariant") ? n : null;
    }

    /// <summary>A declaration of <paramref name="name"/> before token <paramref name="use"/> in the class member that holds
    /// it — a local (<c>final label = …</c>, <c>for (final label in …)</c>), a parameter of the method or of a closure
    /// (<c>(label) =&gt; …</c>): there the name isn't the field. Any such declaration counts, wherever its scope ends:
    /// when in doubt, it isn't the field.</summary>
    private static bool Shadowed(DartDoc d, int classOpen, int use, string name)
    {
        int start = classOpen + 1;
        for (int k = classOpen + 1; k < use; k++)
        {
            if (d.IsOpener(k) && d.Match[k] > k)
            {
                if (d.Match[k] > use) break;            // the member that holds the use
                k = d.Match[k];
                if (d.Is(k, "}")) start = k + 1;        // a member's body ended
                continue;
            }
            if (d.Is(k, ";")) start = k + 1;
        }
        return DeclaredIn(d, Math.Max(start, 1), use, name);
    }

    /// <summary>A declaration of <paramref name="name"/> among the tokens [from, use): a local, a loop variable, a parameter
    /// of a closure or of a function declared there.</summary>
    private static bool DeclaredIn(DartDoc d, int from, int use, string name)
    {
        for (int k = Math.Max(from, 1); k < use; k++)
        {
            if (d.T[k].Kind != DTok.Ident || d.T[k].Text != name) continue;
            if (d.Is(k - 1, ".") || d.Is(k - 1, "?.") || d.Is(k + 1, ":")) continue;   // x.label · label: (an argument's name)
            var prev = d.T[k - 1];
            if (prev.Kind == DTok.Ident && prev.Text is "final" or "var" or "const" or "late") return true;
            bool typed = prev.Kind == DTok.Ident && (char.IsUpper(prev.Text.TrimStart('_').FirstOrDefault()) || prev.Text is "int" or "double" or "num" or "bool" or "dynamic")
                         || d.Is(k - 1, "?") || d.Is(k - 1, ">") || d.Is(k - 1, ")");
            if (typed && (d.Is(k + 1, "=") || d.Is(k + 1, ",") || d.Is(k + 1, ")") || d.Is(k + 1, ";") || d.Is(k + 1, "}") || d.Is(k + 1, "=>") || d.IsIdent(k + 1, "in")))
                return true;
            // an untyped parameter — (label) => …, (context, label) { … }, row(label) { … } — or a pattern: final (label, n) = …
            int o = d.EnclosingOpener(k);
            if (o >= 0 && d.Is(o, "(") && (d.Is(k - 1, "(") || d.Is(k - 1, ",")) && (d.Is(k + 1, ",") || d.Is(k + 1, ")")))
            {
                int after = d.Match[o] + 1;
                if (d.Is(after, "=>") || d.Is(after, "{") || d.IsIdent(after, "async") || d.IsIdent(o - 1, "final") || d.IsIdent(o - 1, "var")) return true;
            }
        }
        return false;
    }

    // ───────────────────────────── text passed in through a helper function ─────────────────────────────

    /// <summary>A function written in the code: its name's token (-1: a closure), the ( of its parameters, its body (the
    /// expression after =>, or the { … } block: first and last token), and which of the two it is.</summary>
    private sealed record FuncDecl(int NameTok, int Open, int BodyFrom, int BodyTo, bool Arrow);

    /// <summary>The function whose parameter list a ( opens — <c>Widget _row(String label) =&gt; …</c>,
    /// <c>row(label) { … }</c>, a closure <c>(context) =&gt; …</c> (no name) — when a body follows it. Null for a call,
    /// <c>if (…) {</c> and the like, a constructor's initializer list.</summary>
    private static FuncDecl? FuncFromParams(DartDoc d, int open)
    {
        if (!d.Is(open, "(") || d.Match[open] <= open) return null;
        int after = d.Match[open] + 1;
        if (d.IsIdent(after, "async") || d.IsIdent(after, "sync")) { after++; if (d.Is(after, "*")) after++; }
        bool arrow = d.Is(after, "=>");
        if (!arrow && !(d.Is(after, "{") && d.Match[after] > after)) return null;
        int j = open - 1;
        if (d.Is(j, ">")) { int lt = d.AngleOpenBefore(j); if (lt < 0) return null; j = lt - 1; }
        if (j >= 0 && d.T[j].Kind == DTok.Ident && d.T[j].Text is "if" or "for" or "while" or "switch" or "catch" or "assert" or "super" or "this" or "when" or "on") return null;
        // a named function — not a named constructor or factory (Foo.of(…) => …), not a closure after return / => / an argument
        int nameTok = j >= 0 && d.T[j].Kind == DTok.Ident && !d.Is(j - 1, ".") && d.T[j].Text is not ("return" or "await" or "throw" or "else" or "do" or "yield") ? j : -1;
        int from, to;
        if (arrow)
        {
            from = after + 1;
            int k = from;
            while (k < d.T.Count && d.T[k].Kind != DTok.End)
            {
                if (d.IsOpener(k) && d.Match[k] > k) { k = d.Match[k] + 1; continue; }
                if (d.T[k].Kind == DTok.Punct && d.T[k].Text is ";" or "," or ")" or "]" or "}") break;
                k++;
            }
            to = k - 1;
            if (to < from) return null;
        }
        else { from = after; to = d.Match[after]; }
        return new FuncDecl(nameTok, open, from, to, arrow);
    }

    /// <summary>The function whose parameter the name token <paramref name="k"/> declares (null: it declares none).</summary>
    private static FuncDecl? FunctionOfParam(DartDoc d, int k, string name)
    {
        int o = d.EnclosingOpener(k);
        if (o < 0) return null;
        int open = o;
        if (d.Is(o, "{") || d.Is(o, "["))
        {
            // in the {…} of named parameters or the […] of optional ones
            open = d.EnclosingOpener(o);
            if (open < 0 || !d.Is(open, "(") || !(d.Is(o - 1, "(") || d.Is(o - 1, ","))) return null;
        }
        else if (!d.Is(o, "(")) return null;
        var fn = FuncFromParams(d, open);
        return fn != null && ParamPlace(d, open, name, field: false) is { } p && p.Tok == k ? fn : null;
    }

    /// <summary>When the nearest declaration of <paramref name="name"/> before token <paramref name="use"/> is a parameter
    /// of a function whose body holds the use, that parameter is what the name means (Decided): the parameter of a helper
    /// whose calls can be followed — a method of the class, a function declared inside a block, a private function of
    /// a file without part files — or nothing to follow (null): a closure's, a public top-level function's (its calls
    /// may be anywhere), one a local or a loop variable inside the function hides, one of a name declared twice there.</summary>
    private static (FlutterParamText? Param, bool Decided) HelperParam(DartDoc d, int use, string name, string? fallback)
    {
        for (int k = use - 1; k >= 0; k--)
        {
            if (d.T[k].Kind != DTok.Ident || d.T[k].Text != name || d.Is(k - 1, ".")) continue;
            var fn = FunctionOfParam(d, k, name);
            if (fn == null || use < fn.BodyFrom || use > fn.BodyTo) continue;
            if (fn.NameTok < 0 || DeclaredIn(d, fn.BodyFrom, use, name)) return (null, true);
            var helper = HelperAt(d, fn);
            if (helper == null || HelperCallsIn(d, fn) == null) return (null, true);
            var place = ParamPlace(d, fn.Open, name, field: false)!;
            var defaults = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (place.HasDefault) defaults[""] = place.Default;
            var cls = d.EnclosingOpener(fn.NameTok) is int eo && eo >= 0 && d.Is(eo, "{") ? ClassHeader(d, eo) ?? ClassAround(d, fn.NameTok) : null;
            return (new FlutterParamText
            {
                Class = cls?.Name ?? "", Param = name, Ctors = new Dictionary<string, int>(StringComparer.Ordinal) { [""] = place.At },
                Defaults = defaults, Fallback = fallback, Helper = helper,
            }, true);
        }
        return (null, false);
    }

    /// <summary>What kind of helper a named function is, or null when its calls can't all be found: a constructor, a
    /// method of a mixin or an extension, a public top-level function, a private one in a library with part files.</summary>
    private static FlutterHelper? HelperAt(DartDoc d, FuncDecl fn)
    {
        string name = d.T[fn.NameTok].Text;
        int eo = d.EnclosingOpener(fn.NameTok);
        FlutterHelperKind kind;
        if (eo < 0)
        {
            if (!name.StartsWith('_') || HasParts(d)) return null;
            kind = FlutterHelperKind.TopLevel;
        }
        else if (d.Is(eo, "{") && ClassHeader(d, eo) is { } cls)
        {
            if (name == cls.Name) return null;
            kind = FlutterHelperKind.Method;
        }
        else if (d.Is(eo, "{") && InFunctionBlock(d, eo)) kind = FlutterHelperKind.Local;
        else return null;
        var (line, col) = d.LineCol(d.T[fn.NameTok].Pos);
        return new FlutterHelper { Name = name, Kind = kind, Line = line, Column = col };
    }

    /// <summary>A { that opens a block of statements (a function's body, an if's, a loop's) — not a class, mixin, extension
    /// or enum body, not a map or set literal.</summary>
    private static bool InFunctionBlock(DartDoc d, int open) =>
        d.Is(open - 1, ")") || d.Is(open - 1, "*") || d.Is(open - 1, "{") || d.Is(open - 1, ";") || d.Is(open - 1, "}")
        || open > 0 && d.T[open - 1] is { Kind: DTok.Ident, Text: "async" or "else" or "try" or "finally" or "do" };

    private static bool HasParts(DartDoc d)
    {
        for (int i = 0; i + 1 < d.T.Count; i++)
            if (d.IsIdent(i, "part") && (d.T[i + 1].Kind == DTok.Str || d.IsIdent(i + 1, "of")) && (i == 0 || d.Is(i - 1, ";"))) return true;
        return false;
    }

    private static bool IsClassName(string s) => s.Length > 0 && (char.IsUpper(s[0]) || s.Length > 1 && s[0] == '_' && char.IsUpper(s[1]));

    /// <summary>The helper declared at <paramref name="h"/>'s place in the code (null: not there any more).</summary>
    private static FuncDecl? FuncNamed(DartDoc d, FlutterHelper h)
    {
        int at = d.Offset(h.Line, h.Column);
        int k = at < 0 ? -1 : d.TokenStartingAt(at);
        if (k < 0 || !d.IsIdent(k, h.Name)) return null;
        int open = k + 1;
        if (d.Is(open, "<")) { open = d.SkipAngles(open); if (open < 0) return null; }
        var fn = FuncFromParams(d, open);
        return fn != null && fn.NameTok == k ? fn : null;
    }

    /// <summary>One call of a helper: the call, the widget whose argument holds it (null: none — it is returned, kept in
    /// a variable, passed to a function), the list it is an element of (its [ token; -1: none) and whether it is that
    /// whole element (not inside an if, a for or a spread).</summary>
    private sealed record HelperCall(DartCall Call, DartCall? Holder, int List, bool Direct);

    /// <summary>The calls of a helper where it can be called — a method's in its class, a local function's in its block, a
    /// private top-level function's in its file — leaving out its own body. Null when that code declares another
    /// function of the same name (which call means which can't be told).</summary>
    private static List<HelperCall>? HelperCallsIn(DartDoc d, FuncDecl fn)
    {
        string name = d.T[fn.NameTok].Text;
        int eo = d.EnclosingOpener(fn.NameTok);
        int from = eo < 0 ? 0 : eo + 1, to = eo < 0 ? d.T.Count - 1 : d.Match[eo] - 1;
        string? cls = eo >= 0 && d.Is(eo, "{") ? ClassHeader(d, eo)?.Name : null;
        var calls = new List<HelperCall>();
        for (int i = from; i <= to; i++)
        {
            if (!d.IsIdent(i, name) || i == fn.NameTok || i >= fn.BodyFrom && i <= fn.BodyTo) continue;
            // this._row(…) and a static Screen._row(…) are its calls; other._row(…) is someone else's method
            if (d.Is(i - 1, ".") && !d.IsIdent(i - 2, "this") && !(cls != null && d.IsIdent(i - 2, cls))) continue;
            int open = i + 1;
            if (d.Is(open, "<")) { open = d.SkipAngles(open); if (open < 0) continue; }
            if (!d.Is(open, "(")) continue;
            if (FuncFromParams(d, open) is { } other && other.NameTok == i) return null;
            if (d.CallAtOffset(d.T[i].Pos) is not { } c) continue;
            var slot = d.SlotOf(c);
            var holder = slot.Owner != null && IsClassName(slot.Owner.ClassName) ? slot.Owner : null;
            int list = -1;
            bool direct = false;
            if (slot.Kind == SlotKind.ListItem && slot.List != null && slot.Index >= 0)
            {
                list = slot.List.OpenTok;
                var it = slot.List.Items[slot.Index];
                direct = it.ValueStart == c.Start && it.ValueEnd == c.End;
            }
            calls.Add(new HelperCall(c, holder, list, direct));
        }
        return calls;
    }

    /// <summary>Where a helper is called (the line and column of each call's name), for the parameter
    /// <paramref name="p"/> read from this file.</summary>
    public static List<(int Line, int Column)> HelperCalls(string text, FlutterParamText p)
    {
        var d = new DartDoc(text);
        if (p.Helper == null || FuncNamed(d, p.Helper) is not { } fn) return new();
        return HelperCallsIn(d, fn)?.Select(c => d.LineCol(c.Call.NameAt)).ToList() ?? new();
    }

    /// <summary>The widget <paramref name="c"/> (inside a helper's body) is drawn exactly once by every call of the
    /// helper: from it up to what the helper gives back there are only arguments and whole list elements — no if, no
    /// loop, no ternary, no closure, and the helper has one return.</summary>
    private static bool OncePerCall(DartDoc d, FuncDecl fn, DartCall c)
    {
        for (int guard = 0; guard < 200; guard++)
        {
            var slot = d.SlotOf(c);
            if (slot.Kind == SlotKind.Arg && slot.Owner != null) { c = slot.Owner; continue; }
            if (slot.Kind == SlotKind.ListItem && slot.Owner != null && slot.List != null && slot.Index >= 0)
            {
                var it = slot.List.Items[slot.Index];
                // the element is the widget, and the list is a whole argument (children: [ … ], children: const <Widget>[ … ])
                var arg = slot.Owner.Args.FirstOrDefault(x => x.ValueStart <= d.T[slot.List.OpenTok].Pos && d.T[slot.List.CloseTok].End <= x.ValueEnd);
                if (it.ValueStart != c.Start || it.ValueEnd != c.End || arg == null || arg.LastTok != slot.List.CloseTok) return false;
                c = slot.Owner;
                continue;
            }
            if (slot.Kind != SlotKind.Root) return false;
            int before = c.StartTok - 1;
            if (fn.Arrow) return before == fn.BodyFrom - 1 && c.CloseTok == fn.BodyTo;
            if (!d.IsIdent(before, "return") || d.EnclosingOpener(before) != fn.BodyFrom) return false;
            // its only return (one inside an if is another way out); closures' returns aren't its own
            int returns = 0;
            for (int k = fn.BodyFrom + 1; k < fn.BodyTo; k++)
            {
                if (d.Is(k, "(") && FuncFromParams(d, k) is { } inner) { k = inner.BodyTo; continue; }
                if (d.IsIdent(k, "return")) returns++;
            }
            return returns == 1;
        }
        return false;
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// Which call of a helper function drew the widget whose box is <paramref name="from"/> (the helper is written in
    /// <paramref name="text"/>, the file <paramref name="file"/>). A call creates no widget, so the picture can't say it
    /// directly; it is told this way:
    /// <list type="bullet">
    /// <item>one call: that one, whatever the picture.</item>
    /// <item>The anchor: the nearest box around <paramref name="from"/> (in Flutter's widget tree) of a widget whose
    /// argument holds a call of the helper (<c>Column(children: [line(…), line(…)])</c> → that Column;
    /// <c>Expanded(child: tile(…))</c> → that Expanded). The calls it holds are the candidates.</item>
    /// <item>The boxes of <paramref name="from"/>'s place under that anchor (one line of code, a box per call) are
    /// counted. As many as candidates: one candidate → that one; several, all whole elements of one list, and the
    /// widget drawn exactly once per call (no if, loop, ternary or closure on its way out of the helper) → the k-th
    /// box is the k-th call — a list draws its children in order, and each call drew exactly one of those boxes.</item>
    /// <item>More boxes than calls, because a loop or an if element in that list calls it too: still the k-th call when
    /// the calls up to it are whole elements, every call written inside the anchor's widget is one of its candidates,
    /// and every call of the helper sits in a widget's argument (none kept in a variable, whose widget could land in
    /// that list as well).</item>
    /// </list>
    /// Anything else is "unsure" — refused rather than guessed. Without a picture (a wireframe pick) only a single call
    /// can be told ("many").
    /// </summary>
    public static FlutterHelperUse HelperUse(string text, string file, FlutterParamText p, IReadOnlyList<WidgetBox> boxes, WidgetBox? from)
    {
        var d = new DartDoc(text);
        if (p.Helper is not { } h || FuncNamed(d, h) is not { } fn || HelperCallsIn(d, fn) is not { } calls || calls.Count == 0)
            return new FlutterHelperUse { Why = "none" };
        FlutterHelperUse Use(HelperCall c, WidgetBox? anchor)
        {
            var (l, col) = d.LineCol(c.Call.NameAt);
            return new FlutterHelperUse { Line = l, Column = col, Anchor = anchor, Calls = calls.Count };
        }
        if (calls.Count == 1) return Use(calls[0], null);
        if (from == null) return new FlutterHelperUse { Calls = calls.Count, Why = "many" };
        var unsure = new FlutterHelperUse { Calls = calls.Count, Why = "unsure" };
        var tree = new BoxTree(boxes);
        var holders = calls.Where(c => c.Holder != null).Select(c => d.LineCol(c.Holder!.NameAt)).ToHashSet();
        WidgetBox? AnchorOf(WidgetBox b)
        {
            for (var x = tree.Parent(b); x != null; x = tree.Parent(x))
                if (holders.Contains((x.Line, x.Column)) && SamePath(x.File, file)) return x;
            return null;
        }
        var anchor = AnchorOf(from);
        if (anchor == null || d.CallAt(anchor.Line, anchor.Column) is not { } held) return unsure;
        var cands = calls.Where(c => c.Holder != null && c.Holder.NameAt == held.NameAt).ToList();
        if (cands.Count == 0) return unsure;
        var group = boxes.Where(b => b.Line == from.Line && b.Column == from.Column && SamePath(b.File, from.File) && ReferenceEquals(AnchorOf(b), anchor)).ToList();
        int k = group.FindIndex(b => ReferenceEquals(b, from));
        if (k < 0) k = group.FindIndex(b => from.Id >= 0 ? b.Id == from.Id : b.Equals(from));   // a copy of the picked box
        if (k < 0) return unsure;
        if (cands.Count == 1 && group.Count == 1) return Use(cands[0], anchor);
        bool once = SamePath(from.File, file) && d.CallAt(from.Line, from.Column) is { } fc && fc.Start >= d.T[fn.BodyFrom].Pos && fc.End <= d.T[fn.BodyTo].End && OncePerCall(d, fn, fc);
        bool oneList = cands.All(c => c.List >= 0 && c.List == cands[0].List);
        if (!once || !oneList || k >= cands.Count) return unsure;
        if (group.Count == cands.Count && cands.All(c => c.Direct)) return Use(cands[k], anchor);
        // a loop or an if element calls it too (the extra boxes): the whole elements before it still come first — when
        // no other box of that place can be among them: every other call inside the anchor's widget sits in a widget
        // whose own box (under the anchor) holds what it draws, and no call is kept in a variable
        bool Under(WidgetBox b)
        {
            for (var x = tree.Parent(b); x != null; x = tree.Parent(x))
                if (ReferenceEquals(x, anchor)) return true;
            return false;
        }
        bool HeldApart(HelperCall c) => c.Holder != null && d.LineCol(c.Holder.NameAt) is var hl
                                        && boxes.Any(b => b.Line == hl.Line && b.Column == hl.Column && SamePath(b.File, file) && Under(b));
        bool alone = calls.Where(c => c.Call.Start >= held.Start && c.Call.End <= held.End && !cands.Contains(c)).All(HeldApart) && calls.All(c => c.Holder != null);
        if (alone && h.Kind != FlutterHelperKind.TopLevel && cands.Any(c => !c.Direct) && cands.Take(k + 1).All(c => c.Direct)) return Use(cands[k], anchor);
        return unsure;
    }

    /// <summary>The call of <paramref name="p"/>'s class (or helper) at a line and column — the widget's use that the
    /// inspector says drew it, the helper's call — and its argument for the text parameter (null: left out).
    /// <paramref name="known"/> is false when that constructor doesn't take the parameter as this.x.</summary>
    private static DartItem? ArgFor(DartDoc d, int line, int column, FlutterParamText p, out DartCall? call, out bool known)
    {
        known = false;
        call = d.CallAt(line, column);
        if (p.Helper is { } h)
        {
            // _row(…), this._row(…), Screen._row(…)
            if (call == null || call.Name != h.Name && !call.Name.EndsWith("." + h.Name, StringComparison.Ordinal)) { call = null; return null; }
        }
        else if (call == null || call.ClassName != p.Class) { call = null; return null; }
        if (!p.Ctors.TryGetValue(CtorKey(call, p), out int index)) return null;
        known = true;
        return index < 0 ? call.Named(p.Param) : call.Positional(index);
    }

    /// <summary>Which of <paramref name="p"/>'s constructors a call is ("" for the unnamed one; a helper's call: "").</summary>
    private static string CtorKey(DartCall call, FlutterParamText p) =>
        p.Helper == null && call.Name.Contains('.') ? call.Name[(call.Name.IndexOf('.') + 1)..] : "";

    /// <summary>What the use of a widget at line:column (its call, as the widget inspector gives it) — or a helper's call
    /// — passes for the text parameter <paramref name="p"/>: a literal to type over, a translation, the caller's own
    /// parameter (followed on from where that widget is used or that helper is called), data, nothing (the default
    /// shows: <see cref="FlutterArgText.Literal"/> when it is a plain literal).</summary>
    public static FlutterArgText ArgText(string text, int line, int column, FlutterParamText p)
    {
        var d = new DartDoc(text);
        var a = ArgFor(d, line, column, p, out var call, out bool known);
        if (call == null) return new FlutterArgText { Kind = FlutterArgKind.Stale };
        if (!known) return new FlutterArgText { Kind = FlutterArgKind.Data };
        if (a == null)
        {
            string ctor = CtorKey(call, p);
            int index = p.Ctors[ctor];
            return new FlutterArgText { Kind = FlutterArgKind.Default, Literal = p.DefaultShown(ctor), Addable = index < 0 || call.Args.Count(x => x.Name == null) == index };
        }
        if (LiteralOf(d, a) is { } s) return new FlutterArgText { Kind = FlutterArgKind.Literal, Literal = s };
        if (TranslationKey(d, a, out bool l10nCall) is { } key) return new FlutterArgText { Kind = FlutterArgKind.Translation, L10nKey = key, L10nCall = l10nCall };
        if (ParamOf(d, a) is { } up) return new FlutterArgText { Kind = FlutterArgKind.PassedOn, PassedOn = up };
        return new FlutterArgText { Kind = FlutterArgKind.Data };
    }

    /// <summary>Where <paramref name="cls"/> is used in a file: the line and column of each call of it (as the widget
    /// inspector gives them — a named constructor's name), its own constructors' declarations left out.</summary>
    public static List<(int Line, int Column)> CallsOf(string text, string cls)
    {
        var found = new List<(int, int)>();
        if (!text.Contains(cls, StringComparison.Ordinal)) return found;
        var d = new DartDoc(text);
        int lastAt = -1;
        for (int i = 0; i < d.T.Count; i++)
        {
            if (!d.IsIdent(i, cls) || d.CallAtOffset(d.T[i].Pos) is not { } c || c.ClassName != cls || c.NameAt == lastAt) continue;
            lastAt = c.NameAt;
            bool declared = d.IsIdent(c.StartTok - 1, "factory")
                            || ClassAround(d, c.OpenTok) is { } owner && owner.Name == cls && d.EnclosingOpener(c.StartTok) == owner.Open && CtorNameAt(d, c.OpenTok, cls) != null;
            if (!declared) found.Add(d.LineCol(c.NameAt));
        }
        return found;
    }

    private static string Src(DartDoc d, DartItem v) => d.Text[v.ValueStart..v.ValueEnd].Trim();

    /// <summary>A number the code gives directly: 16, 16.0, EdgeInsets.all(16), BorderRadius.circular(12).</summary>
    private static double? Num(DartDoc d, DartItem? v)
    {
        if (v == null) return null;
        string s = Src(d, v);
        if (s.StartsWith("const ", StringComparison.Ordinal)) s = s[6..].Trim();
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^(?:EdgeInsets\.all|BorderRadius\.circular|Radius\.circular)\(\s*([0-9.]+)\s*\)$|^([0-9.]+)$|^BorderRadius\.all\(\s*Radius\.circular\(\s*([0-9.]+)\s*\)\s*\)$");
        if (!m.Success) return null;
        string n = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        return double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : null;
    }

    /// <summary>A colour as the inspector shows it: #RRGGBB for a literal, else what the code says.</summary>
    private static string ColorText(DartDoc d, DartItem v)
    {
        string s = Src(d, v);
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^(?:const\s+)?Color\(\s*0x([0-9A-Fa-f]{2})([0-9A-Fa-f]{6})\s*\)$");
        if (m.Success) return "#" + (m.Groups[1].Value.Equals("FF", StringComparison.OrdinalIgnoreCase) ? "" : m.Groups[1].Value.ToUpperInvariant()) + m.Groups[2].Value.ToUpperInvariant();
        return s.Length > 48 ? s[..47] + "…" : s;
    }

    private static string? ColorValue(DartDoc d, DartCall c)
    {
        switch (ColorSlot(c.ClassName))
        {
            case null: return null;
            case "style":
                return c.Named("style") is { } st && d.CallIs(st) is { } sf && sf.Named("backgroundColor") is { } bg ? ColorText(d, bg) : null;
            case "color" when c.ClassName is "Container" or "AnimatedContainer":
                if (c.Named("color") is { } cc) return ColorText(d, cc);
                return c.Named("decoration") is { } dv && d.CallIs(dv) is { } bd && bd.Named("color") is { } dc ? ColorText(d, dc) : null;
            case var arg:
                return c.Named(arg) is { } v ? ColorText(d, v) : null;
        }
    }

    /// <summary>The argument that holds a widget's own colour ("style" for a button: its styleFrom(backgroundColor:)).</summary>
    private static string? ColorSlot(string cls) => cls switch
    {
        "Container" or "AnimatedContainer" or "Card" or "Material" or "ColoredBox" or "Icon" or "Divider" or "VerticalDivider" or "Ink" => "color",
        "Scaffold" or "AppBar" or "SliverAppBar" or "CircleAvatar" or "Chip" or "FloatingActionButton" or "BottomNavigationBar" or "NavigationBar"
            or "SnackBar" or "Drawer" or "BottomAppBar" => "backgroundColor",
        "ListTile" => "tileColor",
        _ when Buttons.Contains(cls) => "style",
        _ => null,
    };

    private static string? PaddingArg(string cls) => cls switch
    {
        "Padding" or "Container" or "AnimatedContainer" or "AnimatedPadding" => "padding",
        "ListTile" => "contentPadding",
        _ => null,
    };

    private static DartList? ChildrenList(DartDoc d, DartCall c) =>
        ListParents.Contains(c.ClassName) && c.Named("children") is { } ch && d.Is(ch.LastTok, "]") ? d.ListAt(d.Match[ch.LastTok]) : null;

    /// <summary>The child a wrapper holds (Padding(child: X) → X), when unwrapping it leaves valid code.</summary>
    private static DartItem? UnwrapTarget(DartDoc d, DartCall c)
    {
        if (c.Named("child") is { } child && d.CallIs(child) != null && (SingleChild.Contains(c.ClassName) || c.Args.Count <= 3)) return child;
        if (ChildrenList(d, c) is { Items.Count: 1 } one && d.CallIs(one.Items[0]) != null) return one.Items[0];
        return null;
    }

    // ───────────────────────────── values ─────────────────────────────

    public static FlutterEdit SetText(string text, int line, int column, string value)
    {
        var d = new DartDoc(text);
        var c = d.CallAt(line, column);
        if (c == null) return FlutterEdit.Fail("stale");
        var tc = TextCallFor(d, c);
        if (tc == null) return FlutterEdit.Fail("text.none");
        var a = tc.Positional(0);
        if (a == null) return FlutterEdit.Fail("text.none");
        return SetLiteral(d, a, value, c.NameAt);
    }

    /// <summary>Type over a text that a widget gets through its parameter <paramref name="p"/>, where the widget is used
    /// (or the helper is called): only the literal the call at line:column passes changes. A call that leaves the
    /// parameter out (the default showed) gets it — named where <c>SetNamed</c> puts arguments, positional right after
    /// the positional arguments before it. A string literal is a constant, so a const call or list around it stays
    /// const, and no const is added. With <paramref name="expect"/> (the text the tool showed), a literal that says
    /// something else by now — the AI or the editor changed it meanwhile — is not overwritten, nor is an argument added
    /// to a call that no longer shows that default.</summary>
    public static FlutterEdit SetArgText(string text, int line, int column, FlutterParamText p, string value, string? expect = null)
    {
        var d = new DartDoc(text);
        var a = ArgFor(d, line, column, p, out var call, out bool known);
        if (call == null || !known) return FlutterEdit.Fail("stale");
        if (a == null) return AddArg(d, call, p, value, expect);
        if (expect != null && LiteralOf(d, a) is { } now && now != expect) return FlutterEdit.Fail("changedMeanwhile");
        return SetLiteral(d, a, value, call.NameAt);
    }

    /// <summary>Give a call that leaves the text parameter out the argument <paramref name="value"/>.</summary>
    private static FlutterEdit AddArg(DartDoc d, DartCall call, FlutterParamText p, string value, string? expect)
    {
        string ctor = CtorKey(call, p);
        int index = p.Ctors[ctor];
        if (expect != null && p.DefaultShown(ctor) != expect) return FlutterEdit.Fail("changedMeanwhile");
        string lit = Quote(value);
        if (index < 0) return Make(d, SetNamed(d, call, p.Param, lit), call.NameAt);
        var positional = call.Args.Where(x => x.Name == null).ToList();
        // (the positional arguments before it are all there, or it can't go in on its own: the code changed under the pick)
        if (positional.Count != index) return FlutterEdit.Fail("stale");
        return Make(d, InsertPositional(d, call, index == 0 ? null : positional[index - 1], lit), call.NameAt);
    }

    /// <summary>Put a positional argument right after <paramref name="after"/> (null: first) — on its own line when the
    /// call's arguments are written one per line.</summary>
    private static List<Replace> InsertPositional(DartDoc d, DartCall call, DartItem? after, string code)
    {
        if (call.Args.Count == 0) return new() { new Replace(call.Open + 1, call.Close, code) };
        string nl = d.Nl;
        bool multi = d.OnlySpaceBefore(call.Args[0].Start);
        if (after == null)
        {
            var first = call.Args[0];
            if (multi) { int at = d.LineStart(first.Start); return new() { new Replace(at, at, d.IndentOf(first.Start) + code + "," + nl) }; }
            return new() { new Replace(first.Start, first.Start, code + ", ") };
        }
        if (after.CommaTok < 0) return new() { new Replace(after.ValueEnd, after.ValueEnd, ", " + code) };
        int end = d.T[after.CommaTok].End;
        if (multi && d.OnlySpaceAfter(end)) { int at = d.NextLineStart(end); return new() { new Replace(at, at, d.IndentOf(after.Start) + code + "," + nl) }; }
        return new() { new Replace(end, end, " " + code + ",") };
    }

    /// <summary>Replace a string literal with <paramref name="value"/>, in the quotes it was written with.</summary>
    private static FlutterEdit SetLiteral(DartDoc d, DartItem a, string value, int focus)
    {
        if (LiteralOf(d, a) == null) return FlutterEdit.Fail(IsL10n(Src(d, a)) ? "text.l10n" : "text.data");
        char q = d.Text[a.ValueStart] == 'r' ? d.Text[a.ValueStart + 1] : d.Text[a.ValueStart];
        if (q != '"') q = '\'';
        return Make(d, new List<Replace> { new(a.ValueStart, a.ValueEnd, Quote(value, q)) }, focus);
    }

    /// <summary>A Dart string literal of <paramref name="s"/> ('…', with \, ', $ and new lines escaped).</summary>
    public static string QuoteText(string s) => Quote(s);

    /// <summary>#RRGGBB / #AARRGGBB / RRGGBB is a colour the tools can write.</summary>
    public static bool IsColor(string hex) => ColorCode(hex) != null;

    internal static string Quote(string s, char q = '\'')
    {
        var sb = new StringBuilder();
        sb.Append(q);
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '$': sb.Append("\\$"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch == q) sb.Append('\\');
                    sb.Append(ch);
                    break;
            }
        }
        sb.Append(q);
        return sb.ToString();
    }

    /// <summary>Text size in logical pixels (null: the theme's).</summary>
    public static FlutterEdit SetFontSize(string text, int line, int column, double? size) =>
        TextStyleEdit(text, line, column, "fontSize", size is { } s ? Number(s) : null);

    /// <summary>Weight: normal, w500, w600, bold, w800 (null: the theme's).</summary>
    public static FlutterEdit SetFontWeight(string text, int line, int column, string? weight) =>
        TextStyleEdit(text, line, column, "fontWeight", weight is { Length: > 0 } w ? "FontWeight." + w : null);

    public static FlutterEdit SetTextColor(string text, int line, int column, string? hex)
    {
        if (hex != null && ColorCode(hex) == null) return FlutterEdit.Fail("color.bad");
        return TextStyleEdit(text, line, column, "color", hex != null ? ColorCode(hex) : null);
    }

    /// <summary>left / center / right / justify (null: the default).</summary>
    public static FlutterEdit SetTextAlign(string text, int line, int column, string? align)
    {
        var d = new DartDoc(text);
        var c = d.CallAt(line, column);
        if (c == null) return FlutterEdit.Fail("stale");
        var tc = TextCallFor(d, c);
        if (tc == null) return FlutterEdit.Fail("text.none");
        return Make(d, SetOrRemove(d, tc, "textAlign", align is { Length: > 0 } a ? "TextAlign." + a : null), c.NameAt);
    }

    private static FlutterEdit TextStyleEdit(string text, int line, int column, string prop, string? code)
    {
        var d = new DartDoc(text);
        var c = d.CallAt(line, column);
        if (c == null) return FlutterEdit.Fail("stale");
        var tc = TextCallFor(d, c);
        if (tc == null) return FlutterEdit.Fail("text.none");
        var style = tc.Named("style");
        List<Replace> changes;
        if (style == null)
            changes = code == null ? new() : SetNamed(d, tc, "style", $"TextStyle({prop}: {code})");
        else if (d.CallIs(style) is { Name: "TextStyle" } ts)
            changes = SetOrRemove(d, ts, prop, code);
        else if (d.TrailingCall(style) is { Name: "copyWith" } cw)
            changes = SetOrRemove(d, cw, prop, code);
        else if (code == null)
            changes = new();
        else
        {
            // a style from the theme or a helper: keep it and change one thing
            string src = Src(d, style);
            bool nullable = src.Contains("textTheme.", StringComparison.Ordinal) && !src.EndsWith('!');
            changes = new List<Replace> { new(style.ValueStart, style.ValueEnd, $"{src}{(nullable ? "?" : "")}.copyWith({prop}: {code})") };
            changes.AddRange(d.DropConsts(style.ValueStart));
        }
        return Make(d, changes, c.NameAt);
    }

    /// <summary>The widget's own colour (#RRGGBB, null: none) — a Container's (in its BoxDecoration when it has one),
    /// a Card's, a Scaffold's / AppBar's background, a button's styleFrom(backgroundColor:).</summary>
    public static FlutterEdit SetColor(string text, int line, int column, string? hex)
    {
        string? code = hex != null ? ColorCode(hex) : null;
        if (hex != null && code == null) return FlutterEdit.Fail("color.bad");
        var d = new DartDoc(text);
        var c = d.CallAt(line, column);
        if (c == null) return FlutterEdit.Fail("stale");
        switch (ColorSlot(c.ClassName))
        {
            case null:
                return FlutterEdit.Fail("color.unsupported");
            case "style":
                return Make(d, StyleFrom(d, c, "backgroundColor", code, out var why) ?? new(), c.NameAt, why);
            case "color" when c.ClassName is "Container" or "AnimatedContainer" && c.Named("decoration") is { } deco:
                if (d.CallIs(deco) is not { Name: "BoxDecoration" } bd) return FlutterEdit.Fail("color.decoration");
                return Make(d, SetOrRemove(d, bd, "color", code), c.NameAt);
            case var arg:
                return Make(d, SetOrRemove(d, c, arg, code), c.NameAt);
        }
    }

    /// <summary>A button's ElevatedButton.styleFrom(prop: value) — made when the button has no style.</summary>
    private static List<Replace>? StyleFrom(DartDoc d, DartCall c, string prop, string? code, out string? why)
    {
        why = null;
        var st = c.Named("style");
        // styleFrom(…) is never constant, so a constant value in it gets its own const
        if (st == null)
            return code == null ? new() : SetNamed(d, c, "style", $"{c.ClassName}.styleFrom({prop}: {(code.Contains('(') && DartDoc.ConstSafe(code) ? "const " : "")}{code})");
        if (d.CallIs(st) is { } sf && sf.Name.EndsWith(".styleFrom", StringComparison.Ordinal)) return SetOrRemove(d, sf, prop, code);
        why = "style.custom";
        return null;
    }

    /// <summary>Space inside the widget (Padding / Container / ListTile), in logical pixels on every side.</summary>
    public static FlutterEdit SetPadding(string text, int line, int column, double? all)
    {
        var d = new DartDoc(text);
        var c = d.CallAt(line, column);
        if (c == null) return FlutterEdit.Fail("stale");
        string? code = all is { } v ? $"EdgeInsets.all({Number(v)})" : null;
        if (PaddingArg(c.ClassName) is { } arg)
        {
            if (c.ClassName == "Padding" && code == null) return Unwrap(text, line, column);   // no padding at all: no Padding
            return Make(d, SetOrRemove(d, c, arg, code), c.NameAt);
        }
        return Around(d, c, "Padding", "padding", code);
    }

    /// <summary>Space around the widget: a Container's / Card's margin, else the Padding it sits in (made when needed).</summary>
    public static FlutterEdit SetMargin(string text, int line, int column, double? all)
    {
        var d = new DartDoc(text);
        var c = d.CallAt(line, column);
        if (c == null) return FlutterEdit.Fail("stale");
        string? code = all is { } v ? $"EdgeInsets.all({Number(v)})" : null;
        if (c.ClassName is "Container" or "AnimatedContainer" or "Card") return Make(d, SetOrRemove(d, c, "margin", code), c.NameAt);
        return Around(d, c, "Padding", "padding", code);
    }

    /// <summary>Round corners (logical pixels; null: square).</summary>
    public static FlutterEdit SetRadius(string text, int line, int column, double? radius)
    {
        var d = new DartDoc(text);
        var c = d.CallAt(line, column);
        if (c == null) return FlutterEdit.Fail("stale");
        string? br = radius is { } r ? $"BorderRadius.all(Radius.circular({Number(r)}))" : null;
        switch (c.ClassName)
        {
            case "Container" or "AnimatedContainer":
            {
                if (c.Named("decoration") is { } deco)
                {
                    if (d.CallIs(deco) is not { Name: "BoxDecoration" } bd) return FlutterEdit.Fail("radius.decoration");
                    return Make(d, SetOrRemove(d, bd, "borderRadius", br), c.NameAt);
                }
                if (br == null) return Make(d, new(), c.NameAt);
                // a Container can't have both color and decoration: the colour moves into the BoxDecoration
                if (c.Named("color") is { } col)
                {
                    string colSrc = Src(d, col);
                    if (colSrc.StartsWith("const ", StringComparison.Ordinal)) colSrc = colSrc[6..];
                    string deco2 = $"BoxDecoration(color: {colSrc}, borderRadius: {br})";
                    var changes = new List<Replace>();
                    if (!DartDoc.ConstSafe(deco2)) changes.AddRange(d.DropConsts(c.Open + 1));
                    else if (!d.InConst(c.Open + 1)) deco2 = "const " + deco2;
                    changes.Add(new Replace(col.Start, col.ValueEnd, "decoration: " + deco2));
                    return Make(d, changes, c.NameAt);
                }
                return Make(d, SetNamed(d, c, "decoration", $"BoxDecoration(borderRadius: {br})"), c.NameAt);
            }
            case "Card":
                return Make(d, SetOrRemove(d, c, "shape", br != null ? $"RoundedRectangleBorder(borderRadius: {br})" : null), c.NameAt);
            case "ClipRRect":
                return Make(d, SetOrRemove(d, c, "borderRadius", br), c.NameAt);
            case var b when Buttons.Contains(b):
                return Make(d, StyleFrom(d, c, "shape", br != null ? $"RoundedRectangleBorder(borderRadius: {br})" : null, out var why) ?? new(), c.NameAt, why);
            default:
                return Around(d, c, "ClipRRect", "borderRadius", br);
        }
    }

    /// <summary>Width / height (logical pixels; null leaves that one out).</summary>
    public static FlutterEdit SetSize(string text, int line, int column, double? width, double? height)
    {
        var d = new DartDoc(text);
        var c = d.CallAt(line, column);
        if (c == null) return FlutterEdit.Fail("stale");
        var target = c.ClassName is "SizedBox" or "Container" or "AnimatedContainer" ? c : OwnerWrapper(d, c, "SizedBox");
        if (target != null)
        {
            var changes = SetOrRemove(d, target, "width", width is { } w ? Number(w) : null);
            var d2 = Apply(d, changes);
            var t2 = d2.CallAtOffset(MapOffset(changes, target.NameAt));
            if (t2 == null) return FlutterEdit.Fail("stale");
            return Chain(d, d2, SetOrRemove(d2, t2, "height", height is { } h ? Number(h) : null), MapOffset(changes, c.NameAt));
        }
        if (width == null && height == null) return Make(d, new(), c.NameAt);
        string args = string.Join(", ", new[] { width is { } ww ? "width: " + Number(ww) : null, height is { } hh ? "height: " + Number(hh) : null }.Where(x => x != null));
        return WrapCall(d, c, "SizedBox", args);
    }

    /// <summary>A setting that lives on a wrapper around the widget (its Padding, ClipRRect): changed when the widget is
    /// already the child of one, made when it isn't (and removed with the wrapper when the value goes away).</summary>
    private static FlutterEdit Around(DartDoc d, DartCall c, string wrapper, string arg, string? code)
    {
        var owner = OwnerWrapper(d, c, wrapper);
        if (owner != null)
        {
            if (code == null)
            {
                // the wrapper only held this: take it away
                if (owner.Args.Count(a => a.Name != "key") <= 2) return UnwrapCall(d, owner, focusChild: true);
                return Make(d, RemoveNamed(d, owner, arg), c.NameAt);
            }
            return Make(d, SetNamed(d, owner, arg, code), c.NameAt);
        }
        if (code == null) return Make(d, new(), c.NameAt);
        return WrapCall(d, c, wrapper, $"{arg}: {code}", focusInner: true);
    }

    /// <summary>The wrapper of one class the widget is the direct child of (Padding(child: c)).</summary>
    private static DartCall? OwnerWrapper(DartDoc d, DartCall c, string cls)
    {
        var slot = d.SlotOf(c);
        return slot.Kind == SlotKind.Arg && slot.ArgName == "child" && slot.Owner?.ClassName == cls ? slot.Owner : null;
    }

    internal static string Number(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>#RRGGBB / #AARRGGBB / RRGGBB → Color(0xAARRGGBB) (null: not a colour).</summary>
    internal static string? ColorCode(string hex)
    {
        string h = hex.Trim().TrimStart('#');
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        if (h.Length == 6) h = "FF" + h;
        if (h.Length != 8 || !h.All(Uri.IsHexDigit)) return null;
        return $"Color(0x{h.ToUpperInvariant()})";
    }

    // ───────────────────────────── structure ─────────────────────────────

    /// <summary>Remove the widget: from its list, or its optional slot; anywhere else it becomes an empty box.</summary>
    public static FlutterEdit Delete(string text, int line, int column)
    {
        var d = new DartDoc(text);
        var c = d.CallAt(line, column);
        if (c == null) return FlutterEdit.Fail("stale");
        var slot = d.SlotOf(c);
        int focus = slot.Owner?.NameAt ?? -1;
        if (slot.Kind == SlotKind.ListItem) return Make(d, RemoveItem(d, slot.List!.Items, slot.Index), focus);
        if (slot.Kind == SlotKind.Arg && slot.Owner != null && slot.ArgName != null
            && (slot.ArgName == "child" && ChildOptional.Contains(slot.Owner.ClassName)
                || OptionalSlots.TryGetValue(slot.Owner.ClassName, out var opt) && opt.Contains(slot.ArgName)))
            return Make(d, RemoveNamed(d, slot.Owner, slot.ArgName), focus);
        // a required place (Expanded(child:), return …): an empty box keeps the code valid
        bool inConst = d.InConst(c.Start);
        return Make(d, new List<Replace> { new(c.Start, c.End, (inConst ? "" : "const ") + "SizedBox.shrink()") }, focus);
    }

    /// <summary>A copy right after it in its list.</summary>
    public static FlutterEdit Duplicate(string text, int line, int column)
    {
        var d = new DartDoc(text);
        var c = d.CallAt(line, column);
        if (c == null) return FlutterEdit.Fail("stale");
        var slot = d.SlotOf(c);
        if (slot.Kind != SlotKind.ListItem) return FlutterEdit.Fail("notInList");
        var item = slot.List!.Items[slot.Index];
        string copy = d.Text[item.ValueStart..item.ValueEnd];
        var (changes, at) = InsertItem(d, slot.List, slot.Index + 1, copy);
        // the copy's own widget (the element may be "if (x) Foo()": the call sits inside it)
        return Make(d, changes, -1, focusNew: at + (c.NameAt - item.ValueStart));
    }

    /// <summary>Swap it with the one before (-1) or after (+1) it in its list.</summary>
    public static FlutterEdit Move(string text, int line, int column, int delta)
    {
        var d = new DartDoc(text);
        var c = d.CallAt(line, column);
        if (c == null) return FlutterEdit.Fail("stale");
        var slot = d.SlotOf(c);
        if (slot.Kind != SlotKind.ListItem) return FlutterEdit.Fail("notInList");
        int other = slot.Index + Math.Sign(delta);
        if (other < 0 || other >= slot.List!.Items.Count) return FlutterEdit.Fail("move.edge");
        var a = slot.List.Items[slot.Index];
        var b = slot.List.Items[other];
        string ta = d.Text[a.ValueStart..a.ValueEnd], tb = d.Text[b.ValueStart..b.ValueEnd];
        var changes = new List<Replace> { new(a.ValueStart, a.ValueEnd, tb), new(b.ValueStart, b.ValueEnd, ta) };
        int inner = c.NameAt - a.ValueStart;
        int focus = delta > 0 ? b.ValueStart + (tb.Length - ta.Length) + inner : b.ValueStart + inner;
        return Make(d, changes, -1, focusNew: focus);
    }

    /// <summary>Wrap it: Padding, Center, Card, Expanded, SizedBox, Align, InkWell, ClipRRect.</summary>
    public static FlutterEdit Wrap(string text, int line, int column, string wrapper)
    {
        var d = new DartDoc(text);
        var c = d.CallAt(line, column);
        if (c == null) return FlutterEdit.Fail("stale");
        if (wrapper == "Expanded")
        {
            var slot = d.SlotOf(c);
            if (!(slot.Kind == SlotKind.ListItem && slot.Owner != null && Flex.Contains(slot.Owner.ClassName))) return FlutterEdit.Fail("wrap.expanded");
        }
        string args = wrapper switch
        {
            "Padding" => "padding: EdgeInsets.all(8)",
            "SizedBox" => "width: 120",
            "Align" => "alignment: Alignment.centerLeft",
            "InkWell" => "onTap: () {}",
            "ClipRRect" => "borderRadius: BorderRadius.all(Radius.circular(12))",
            "Center" or "Card" or "Expanded" => "",
            _ => "\u0000",
        };
        if (args == "\u0000") return FlutterEdit.Fail("wrap.unknown");
        return WrapCall(d, c, wrapper, args);
    }

    /// <summary>Take the wrapper away and keep what it holds.</summary>
    public static FlutterEdit Unwrap(string text, int line, int column)
    {
        var d = new DartDoc(text);
        var c = d.CallAt(line, column);
        if (c == null) return FlutterEdit.Fail("stale");
        return UnwrapCall(d, c, focusChild: true);
    }

    private static FlutterEdit UnwrapCall(DartDoc d, DartCall c, bool focusChild)
    {
        var inner = UnwrapTarget(d, c);
        if (inner == null) return FlutterEdit.Fail("unwrap.none");
        string body = d.Text[inner.ValueStart..inner.ValueEnd];
        if (d.Multiline(c.Start, c.End)) body = Shift(body, -2, d.Nl);
        // (a child that was constant only because of the wrapper stays valid: const never has to be added)
        var changes = new List<Replace> { new(c.Start, c.End, body) };
        int focus = c.Start + (d.CallIs(inner) is { } ic ? ic.NameAt - inner.ValueStart : 0);
        return Make(d, changes, -1, focusNew: focusChild ? focus : c.Start);
    }

    private static FlutterEdit WrapCall(DartDoc d, DartCall c, string wrapper, string args, bool focusInner = false)
    {
        bool inConst = d.InConst(c.Start);
        string inner = d.Text[c.Start..c.End];
        string code = args.Length > 0 ? args + ", " : "";
        var changes = new List<Replace>();
        bool safe = DartDoc.ConstSafe(wrapper + "(" + code + "child: Text(''))");
        if (!safe) changes.AddRange(d.DropConsts(c.Start));
        // a constant value outside a const gets its own const (as dart fix would write it)
        if (!inConst && args.Length > 0) code = ConstArgs(args) + ", ";
        string ind = d.IndentOf(c.Start);
        string text;
        int innerAt;
        if (!d.Multiline(c.Start, c.End) && ind.Length + wrapper.Length + code.Length + inner.Length < 76)
        {
            text = $"{wrapper}({code}child: {inner})";
            innerAt = wrapper.Length + 1 + code.Length + "child: ".Length;
        }
        else
        {
            string nl = d.Nl;
            string head = $"{wrapper}({nl}" + (code.Length > 0 ? $"{ind}  {code.TrimEnd().TrimEnd(',')},{nl}" : "") + $"{ind}  child: ";
            text = head + Shift(inner, 2, nl) + $",{nl}{ind})";
            innerAt = head.Length;
        }
        changes.Add(new Replace(c.Start, c.End, text));
        int focusNew = MapOffset(changes, c.Start) + (focusInner ? innerAt + (c.NameAt - c.Start) : 0);
        return Make(d, changes, -1, focusNew: focusNew);
    }

    /// <summary>padding: EdgeInsets.all(8) → padding: const EdgeInsets.all(8) (for code outside a const).</summary>
    private static string ConstArgs(string args)
    {
        var parts = args.Split(new[] { ", " }, StringSplitOptions.None);
        for (int i = 0; i < parts.Length; i++)
        {
            int colon = parts[i].IndexOf(':');
            if (colon < 0) continue;
            string v = parts[i][(colon + 1)..].Trim();
            if (v.Contains('(') && DartDoc.ConstSafe(v) && !v.StartsWith("const ", StringComparison.Ordinal))
                parts[i] = parts[i][..(colon + 1)] + " const " + v;
        }
        return string.Join(", ", parts);
    }

    /// <summary>Indent every line after the first by <paramref name="by"/> spaces (negative: take them away).</summary>
    internal static string Shift(string s, int by, string nl)
    {
        if (!s.Contains('\n')) return s;
        var lines = s.Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            if (by > 0) { if (lines[i].Trim().Length > 0) lines[i] = new string(' ', by) + lines[i]; }
            else
            {
                int k = 0;
                while (k < -by && k < lines[i].Length && lines[i][k] == ' ') k++;
                lines[i] = lines[i][k..];
            }
        }
        return string.Join("\n", lines);
    }

    public enum Place { Before, After, Inside }

    /// <summary>Put a new widget before / after the picked one (in its list; anywhere else the two are put in a
    /// Column), or inside it (at the end of its children, or as its child).</summary>
    public static FlutterEdit Insert(string text, int line, int column, string code, Place where)
    {
        var d = new DartDoc(text);
        var c = d.CallAt(line, column);
        if (c == null) return FlutterEdit.Fail("stale");
        if (where == Place.Inside)
        {
            if (ChildrenList(d, c) is { } list) return InsertInto(d, list, list.Items.Count, code);
            if (SingleChild.Contains(c.ClassName) && c.Named("child") == null) return InsertChild(d, c, code);
            if (c.Named("child") is { } ch && d.CallIs(ch) is { } inner) { c = inner; where = Place.After; }
            else return FlutterEdit.Fail("insert.noRoom");
        }
        var slot = d.SlotOf(c);
        if (slot.Kind == SlotKind.ListItem) return InsertInto(d, slot.List!, slot.Index + (where == Place.After ? 1 : 0), code);
        // not in a list: the two go in a Column
        string snippet = Fit(d, c.Start, code);
        string inner2 = d.Text[c.Start..c.End];
        string ind = d.IndentOf(c.Start);
        string nl = d.Nl;
        string first = where == Place.After ? inner2 : snippet, second = where == Place.After ? snippet : inner2;
        string col = $"Column({nl}{ind}  mainAxisSize: MainAxisSize.min,{nl}{ind}  children: [{nl}{ind}    {Shift(first, 4, nl)},{nl}{ind}    {Shift(second, 4, nl)},{nl}{ind}  ],{nl}{ind})";
        var changes = new List<Replace>();
        if (!DartDoc.ConstSafe(code)) changes.AddRange(d.DropConsts(c.Start));
        changes.Add(new Replace(c.Start, c.End, col));
        int colAt = MapOffset(changes, c.Start);
        int newAt = colAt + col.IndexOf(where == Place.After ? Shift(second, 4, nl) : Shift(first, 4, nl), StringComparison.Ordinal);
        return Make(d, changes, -1, focusNew: newAt);
    }

    /// <summary>Put a new widget at a place of a list parent's children (what a drop on the picture does).</summary>
    public static FlutterEdit InsertAt(string text, int ownerLine, int ownerColumn, int index, string code)
    {
        var d = new DartDoc(text);
        var c = d.CallAt(ownerLine, ownerColumn);
        if (c == null) return FlutterEdit.Fail("stale");
        if (index < 0)
        {
            if (SingleChild.Contains(c.ClassName) && c.Named("child") == null) return InsertChild(d, c, code);
            return FlutterEdit.Fail("insert.noRoom");
        }
        var list = ChildrenList(d, c);
        if (list == null) return FlutterEdit.Fail("insert.noRoom");
        return InsertInto(d, list, Math.Min(index, list.Items.Count), code);
    }

    private static FlutterEdit InsertChild(DartDoc d, DartCall c, string code)
    {
        var changes = SetNamed(d, c, "child", Fit(d, c.Open + 1, code));
        var d2 = Apply(d, changes);
        var owner = d2.CallAtOffset(MapOffset(changes, c.NameAt));
        int at = owner?.Named("child") is { } ch ? ch.ValueStart : -1;
        return Make(d, changes, -1, focusNew: at, newDoc: d2);
    }

    private static FlutterEdit InsertInto(DartDoc d, DartList list, int index, string code)
    {
        var (changes, at) = InsertItem(d, list, index, Fit(d, d.T[list.OpenTok].End, code));
        if (!DartDoc.ConstSafe(code))
        {
            var drops = d.DropConsts(d.T[list.OpenTok].End);
            int before = drops.Where(r => r.End <= d.T[list.OpenTok].Pos).Sum(r => r.Text.Length - (r.End - r.Start));
            changes.AddRange(drops);
            at += before;
        }
        return Make(d, changes, -1, focusNew: at);
    }

    /// <summary>A snippet for a place: its leading const goes away inside a const (it is already constant there).</summary>
    private static string Fit(DartDoc d, int at, string code) =>
        code.StartsWith("const ", StringComparison.Ordinal) && d.InConst(at) ? code[6..] : code;

    /// <summary>The changes that put <paramref name="code"/> at <paramref name="index"/> of a list, and where it starts
    /// in the new text — one element per line when the list is written that way.</summary>
    private static (List<Replace> Changes, int At) InsertItem(DartDoc d, DartList list, int index, string code)
    {
        var items = list.Items;
        string nl = d.Nl;
        int open = d.T[list.OpenTok].End, close = d.T[list.CloseTok].Pos;
        var changes = new List<Replace>();
        if (items.Count == 0)
        {
            if (d.Multiline(open, close))
            {
                string ind = d.IndentOf(close) + "  ";
                string t = nl + ind + code + "," + nl + d.IndentOf(close);
                changes.Add(new Replace(open, close, t));
                return (changes, open + nl.Length + ind.Length);
            }
            changes.Add(new Replace(open, close, code));
            return (changes, open);
        }
        bool multi = d.OnlySpaceBefore(items[0].Start);
        if (multi)
        {
            string ind = d.IndentOf(items[0].Start);
            if (index < items.Count)
            {
                int at = d.LineStart(items[index].Start);
                changes.Add(new Replace(at, at, ind + code + "," + nl));
                return (changes, at + ind.Length);
            }
            var last = items[^1];
            int end = last.CommaTok >= 0 ? d.T[last.CommaTok].End : last.ValueEnd;
            if (d.OnlySpaceAfter(end))
            {
                int at = d.NextLineStart(end);
                if (last.CommaTok < 0) changes.Add(new Replace(last.ValueEnd, last.ValueEnd, ","));
                string t = ind + code + "," + nl;
                if (at == d.Text.Length && !d.Text.EndsWith('\n')) t = nl + ind + code + ",";
                changes.Add(new Replace(at, at, t));
                return (changes, at + (t.StartsWith(nl, StringComparison.Ordinal) ? nl.Length : 0) + ind.Length + (last.CommaTok < 0 ? 1 : 0));
            }
        }
        // written on one line: [a, b]
        if (index < items.Count)
        {
            int at = items[index].Start;
            changes.Add(new Replace(at, at, code + ", "));
            return (changes, at);
        }
        var l = items[^1];
        if (l.CommaTok >= 0)
        {
            int at = d.T[l.CommaTok].End;
            changes.Add(new Replace(at, at, " " + code));
            return (changes, at + 1);
        }
        changes.Add(new Replace(l.ValueEnd, l.ValueEnd, ", " + code));
        return (changes, l.ValueEnd + 2);
    }

    // ───────────────────────────── drop on the picture ─────────────────────────────

    /// <summary>Where a widget dropped at (x, y) on the picture goes: into the innermost list parent under the point
    /// (between the children it falls between), else into an empty child slot. <paramref name="read"/> gives a file's
    /// text (null: not readable); only boxes in <paramref name="editable"/> files are used.</summary>
    public static FlutterDropPlan? PlanDrop(IReadOnlyList<WidgetBox> boxes, double x, double y, Func<string, string?> read, Func<string, bool> editable)
    {
        var docs = new Dictionary<string, DartDoc?>(StringComparer.OrdinalIgnoreCase);
        DartDoc? Doc(string f)
        {
            if (!docs.TryGetValue(f, out var doc)) docs[f] = doc = read(f) is { } t ? new DartDoc(t) : null;
            return doc;
        }
        var under = boxes.Where(b => editable(b.File) && x >= b.X && y >= b.Y && x <= b.X + b.W && y <= b.Y + b.H)
                         .OrderByDescending(b => b.Depth).ThenBy(b => b.W * b.H).ToList();
        foreach (var b in under)
        {
            var d = Doc(b.File);
            var c = d?.CallAt(b.Line, b.Column);
            if (d == null || c == null) continue;
            if (ChildrenList(d, c) is { } list)
            {
                bool vertical = c.ClassName switch
                {
                    "Row" or "Wrap" => false,
                    "Flex" => !(c.Named("direction") is { } dir && Src(d, dir).EndsWith("horizontal", StringComparison.Ordinal)),
                    "ListView" => !(c.Named("scrollDirection") is { } sd && Src(d, sd).EndsWith("horizontal", StringComparison.Ordinal)),
                    _ => true,
                };
                // each child's box: the outermost box written inside that element
                var kids = new List<(int Index, WidgetBox Box)>();
                for (int i = 0; i < list.Items.Count; i++)
                {
                    var it = list.Items[i];
                    var kb = boxes.Where(k => string.Equals(k.File, b.File, StringComparison.OrdinalIgnoreCase) && k.Depth > b.Depth)
                                  .Where(k => d.Offset(k.Line, k.Column) is var o && o >= it.ValueStart && o < it.ValueEnd)
                                  .OrderBy(k => k.Depth).FirstOrDefault();
                    if (kb != null) kids.Add((i, kb));
                }
                // past the last child on the picture: right after it (children not drawn — a false if — are passed over)
                int index = kids.Count > 0 ? kids[^1].Index + 1 : list.Items.Count;
                WidgetBox? next = null, prev = null;

                foreach (var (i, kb) in kids)
                {
                    bool after = vertical ? y < kb.Y + kb.H / 2
                        : c.ClassName == "Wrap" ? y < kb.Y || y < kb.Y + kb.H && x < kb.X + kb.W / 2
                        : x < kb.X + kb.W / 2;
                    if (after) { index = i; next = kb; break; }
                    prev = kb;
                }
                double lx, ly, lw, lh;
                if (vertical)
                {
                    ly = next != null && prev != null ? (prev.Y + prev.H + next.Y) / 2 : next != null ? next.Y : prev != null ? prev.Y + prev.H : b.Y + 4;
                    lx = b.X; lw = b.W; lh = 0;
                }
                else
                {
                    var r = next ?? prev;
                    lx = next != null && prev != null ? (prev.X + prev.W + next.X) / 2 : next != null ? next.X : prev != null ? prev.X + prev.W : b.X + 4;
                    ly = r?.Y ?? b.Y; lh = r?.H ?? b.H; lw = 0;
                }
                return new FlutterDropPlan
                {
                    File = b.File, OwnerLine = b.Line, OwnerColumn = b.Column, Owner = c.Name, Index = index,
                    X = lx, Y = ly, W = lw, H = lh, Vertical = vertical,
                };
            }
            if (SingleChild.Contains(c.ClassName) && c.Named("child") == null)
                return new FlutterDropPlan { File = b.File, OwnerLine = b.Line, OwnerColumn = b.Column, Owner = c.Name, Index = -1, X = b.X, Y = b.Y, W = b.W, H = b.H };
        }
        return null;
    }

    // ───────────────────────────── the edit primitives ─────────────────────────────

    private static List<Replace> SetOrRemove(DartDoc d, DartCall c, string name, string? code) =>
        code == null ? RemoveNamed(d, c, name) : SetNamed(d, c, name, code);

    /// <summary>Set a named argument (replacing its value, or adding it before child / children, as the lint wants).</summary>
    private static List<Replace> SetNamed(DartDoc d, DartCall c, string name, string code)
    {
        var changes = new List<Replace>();
        bool safe = DartDoc.ConstSafe(code), inConst = d.InConst(c.Open + 1);
        if (!safe) changes.AddRange(d.DropConsts(c.Open + 1));
        // a constant call outside a const gets its own const (as the lints and dart fix write it); inside one it needs none
        // — a string literal is a constant already, whatever it says ('ยอด (บาท)')
        bool literal = code.Length > 0 && (code[0] is '\'' or '"' || code.Length > 1 && code[0] == 'r' && code[1] is '\'' or '"');
        if (safe && !inConst && !literal && code.Contains('(') && !code.StartsWith("const ", StringComparison.Ordinal)) code = "const " + code;
        else if (safe && inConst && code.StartsWith("const ", StringComparison.Ordinal)) code = code[6..];
        var a = c.Named(name);
        if (a != null) { changes.Add(new Replace(a.ValueStart, a.ValueEnd, code)); return changes; }
        string nl = d.Nl;
        if (c.Args.Count == 0)
        {
            changes.Add(new Replace(c.Open + 1, c.Close, $"{name}: {code}"));
            return changes;
        }
        bool multi = d.OnlySpaceBefore(c.Args[0].Start);
        string ind = multi ? d.IndentOf(c.Args[0].Start) : "";
        var before = c.Args.FirstOrDefault(x => x.Name is "child" or "children");
        if (before != null)
        {
            if (multi && d.OnlySpaceBefore(before.Start)) changes.Add(new Replace(before.Start, before.Start, $"{name}: {code},{nl}{ind}"));
            else changes.Add(new Replace(before.Start, before.Start, $"{name}: {code}, "));
            return changes;
        }
        var last = c.Args[^1];
        if (last.CommaTok >= 0)
        {
            int after = d.T[last.CommaTok].End;
            changes.Add(new Replace(after, after, multi ? $"{nl}{ind}{name}: {code}," : $" {name}: {code}"));
        }
        else changes.Add(new Replace(last.ValueEnd, last.ValueEnd, $", {name}: {code}"));
        return changes;
    }

    private static List<Replace> RemoveNamed(DartDoc d, DartCall c, string name)
    {
        int i = c.Args.FindIndex(a => a.Name == name);
        return i < 0 ? new List<Replace>() : RemoveItem(d, c.Args, i);
    }

    /// <summary>Take one item out of a comma-separated run — its whole lines when it stands on its own lines.</summary>
    private static List<Replace> RemoveItem(DartDoc d, List<DartItem> items, int i)
    {
        var it = items[i];
        int s = it.Start, e = it.CommaTok >= 0 ? d.T[it.CommaTok].End : it.ValueEnd;
        if (d.OnlySpaceBefore(s) && d.OnlySpaceAfter(e)) return new() { new Replace(d.LineStart(s), d.NextLineStart(e), "") };
        if (it.CommaTok >= 0)
        {
            int k = e;
            while (k < d.Text.Length && d.Text[k] == ' ') k++;
            return new() { new Replace(s, k, "") };
        }
        if (i > 0 && items[i - 1].CommaTok >= 0) return new() { new Replace(d.T[items[i - 1].CommaTok].Pos, e, "") };
        return new() { new Replace(s, e, "") };
    }

    private static DartDoc Apply(DartDoc d, List<Replace> changes) => new(ApplyText(d.Text, changes));

    private static string ApplyText(string text, List<Replace> changes)
    {
        var sb = new StringBuilder(text);
        foreach (var c in changes.OrderByDescending(c => c.Start).ThenByDescending(c => c.End))
            sb.Remove(c.Start, c.End - c.Start).Insert(c.Start, c.Text);
        return sb.ToString();
    }

    /// <summary>Where an offset of the old text is after the changes (a change starting exactly there: its start).</summary>
    private static int MapOffset(List<Replace> changes, int old)
    {
        int shift = 0;
        foreach (var c in changes)
        {
            if (c.End <= old && !(c.Start == c.End && c.Start == old)) shift += c.Text.Length - (c.End - c.Start);
            else if (c.Start == c.End && c.Start < old) shift += c.Text.Length;
        }
        return old + shift;
    }

    private static FlutterEdit Make(DartDoc d, List<Replace> changes, int focusOld, string? why = null, int focusNew = -1, DartDoc? newDoc = null)
    {
        if (why != null) return FlutterEdit.Fail(why);
        if (changes.Count == 0) return new FlutterEdit { Ok = true, Text = null, Old = d.Text, FocusLine = focusOld >= 0 ? d.LineCol(focusOld).Line : 0, FocusColumn = focusOld >= 0 ? d.LineCol(focusOld).Column : 0 };
        var sorted = changes.OrderBy(c => c.Start).ThenBy(c => c.End).ToList();
        for (int i = 1; i < sorted.Count; i++)
            if (sorted[i].Start < sorted[i - 1].End) return FlutterEdit.Fail("conflict");
        string text = ApplyText(d.Text, sorted);
        var nd = newDoc ?? new DartDoc(text);
        int focus = focusNew >= 0 ? focusNew : focusOld >= 0 ? MapOffset(sorted, focusOld) : -1;
        int line = 0, col = 0;
        if (focus >= 0 && focus <= text.Length)
        {
            int tok = nd.T.FindIndex(t => t.Pos >= focus);
            while (tok >= 0 && tok < nd.T.Count && nd.T[tok].Kind == DTok.Ident && nd.T[tok].Text is "const" or "new") tok++;
            DartCall? fc = tok >= 0 && tok < nd.T.Count ? nd.CallAtOffset(nd.T[tok].Pos) : null;
            // a named constructor's class (Image of Image.asset) resolves to its constructor's name
            var (l, cc) = fc != null ? nd.LineCol(fc.NameAt) : nd.LineCol(focus);
            line = l; col = cc;
        }
        return new FlutterEdit { Ok = true, Text = text, Old = d.Text, Changes = sorted, FocusLine = line, FocusColumn = col };
    }

    /// <summary>Two rounds of changes (the second made on the first's result <paramref name="d2"/>) as one edit;
    /// <paramref name="focus2"/> is an offset of <paramref name="d2"/>.</summary>
    private static FlutterEdit Chain(DartDoc d, DartDoc d2, List<Replace> second, int focus2)
    {
        var r2 = Make(d2, second, focus2);

        if (!r2.Ok) return r2;
        string text = r2.Text ?? d2.Text;
        if (text == d.Text) return new FlutterEdit { Ok = true, Old = d.Text, FocusLine = r2.FocusLine, FocusColumn = r2.FocusColumn };
        // positions of the old text map through both rounds: keep one change covering the edited region
        int start = 0;
        while (start < d.Text.Length && start < text.Length && d.Text[start] == text[start]) start++;
        int endOld = d.Text.Length, endNew = text.Length;
        while (endOld > start && endNew > start && d.Text[endOld - 1] == text[endNew - 1]) { endOld--; endNew--; }
        return new FlutterEdit
        {
            Ok = true, Text = text, Old = d.Text, FocusLine = r2.FocusLine, FocusColumn = r2.FocusColumn,
            Changes = new List<Replace> { new(start, endOld, text[start..endNew]) },
        };
    }
}

/// <summary>The inspector's boxes as a tree: a box's parent is the nearest box around it in Flutter's widget tree, as the
/// renderer recorded it — for an older picture without that, the nearest earlier box of a smaller depth that holds it
/// on the picture (the boxes come in the tree's order).</summary>
internal sealed class BoxTree
{
    private readonly IReadOnlyList<WidgetBox> _boxes;
    private readonly Dictionary<int, WidgetBox> _byId = new();
    private readonly Dictionary<WidgetBox, int> _at = new(ReferenceEqualityComparer.Instance);

    public BoxTree(IReadOnlyList<WidgetBox> boxes)
    {
        _boxes = boxes;
        for (int i = 0; i < boxes.Count; i++)
        {
            _at.TryAdd(boxes[i], i);
            if (boxes[i].Id >= 0) _byId.TryAdd(boxes[i].Id, boxes[i]);
        }
    }

    public WidgetBox? Parent(WidgetBox b)
    {
        if (b.Id >= 0) return b.Parent >= 0 && _byId.TryGetValue(b.Parent, out var p) ? p : null;
        if (!_at.TryGetValue(b, out int i)) return null;
        for (int k = i - 1; k >= 0; k--)
        {
            var x = _boxes[k];
            if (x.Depth < b.Depth && x.X <= b.X + 0.5 && x.Y <= b.Y + 0.5 && x.X + x.W >= b.X + b.W - 0.5 && x.Y + x.H >= b.Y + b.H - 0.5) return x;
        }
        return null;
    }
}
