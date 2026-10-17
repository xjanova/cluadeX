using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services.UiStudio.Sketch;

/// <summary>
/// Turns a screen's widget tree (as written in its source) into a wireframe. Flutter's own widgets map to blocks
/// (Scaffold → screen, AppBar → bar, ListView.builder → a few rows of its item, TextField → input…); the
/// project's own widgets are drawn from THEIR build() with the caller's arguments put in their place — so a
/// <c>TpPage(title: 'สมาชิก')</c> shows "สมาชิก" in whatever header TpPage draws. Where the code branches
/// (a ternary, <c>if</c> in a list, <c>asyncValue.when</c>, a switch) the branch with the content is drawn, not the
/// spinner. Widgets it can't read are labelled boxes, so the wireframe never pretends to be more than it is.
/// </summary>
internal sealed class FlutterSketcher
{
    private readonly FlutterProject _p;
    private int _nodes, _unknown;
    private const int MaxNodes = 900;
    private const int MaxDepth = 9;
    private DartFile _screenFile = null!;

    public FlutterSketcher(FlutterProject project) => _p = project;

    private sealed record Bound(DExpr? Expr, Env Env, SketchNode? Node = null);

    private sealed class Env
    {
        public DartFile File = null!;
        public DartClass? Class;
        public Dictionary<string, Bound> Args = new(StringComparer.Ordinal);
        public Dictionary<string, Bound> Vars = new(StringComparer.Ordinal);
        public Dictionary<string, int>? Locals;
        public HashSet<string> Expanding = new(StringComparer.Ordinal);
        public int Depth;
        public (string File, int Line)? Use;

        public Env Child() => new()
        {
            File = File, Class = Class, Args = Args, Locals = Locals, Expanding = Expanding, Depth = Depth, Use = Use,
            Vars = new Dictionary<string, Bound>(Vars, StringComparer.Ordinal),
        };
    }

    // ───────────────────────────── entry ─────────────────────────────

    public ScreenSketch Screen(DartClass cls, DCall? call, DartFile? callFile, FlutterProject.ShellRef? shell)
    {
        _screenFile = cls.File;
        var callerEnv = new Env { File = callFile ?? cls.File };
        var content = Expand(cls, call, callerEnv, call?.Pos ?? 0);
        SketchNode root;
        if (shell != null && shell.SlotParam != null)
        {
            var env = new Env { File = shell.File };
            env.Vars[shell.SlotParam] = new Bound(null, env, content);
            var shellNodes = Nodes(shell.Builder, env).ToList();
            root = shellNodes.Count == 1 ? shellNodes[0] : Wrap(SketchKind.Column, shellNodes, shell.Builder, env, "ShellRoute");
            if (!root.Walk().Contains(content) && content != null) root = content;   // the shell never placed the screen
        }
        else root = content ?? new SketchNode { Kind = SketchKind.Column, Widget = cls.Name, File = cls.File.Path, Line = cls.Line };

        string? title = content?.Walk().FirstOrDefault(n => n.Kind is SketchKind.AppBar && n.Text != null)?.Text
                        ?? content?.Walk().FirstOrDefault(n => n.Kind is SketchKind.Heading && n.Text != null && n.Text != "…")?.Text;
        return new ScreenSketch { Root = root, Theme = _p.Theme, Title = title, Unknown = _unknown };
    }

    // ───────────────────────────── expressions → blocks ─────────────────────────────

    private IEnumerable<SketchNode> Nodes(DExpr? e, Env env)
    {
        if (e == null || _nodes > MaxNodes) yield break;
        switch (e)
        {
            case DCall c:
                foreach (var n in Call(c, env)) yield return n;
                break;
            case DName n:
                foreach (var x in Name(n, env)) yield return x;
                break;
            case DIf iff:
            {
                // a condition the code decides (a default, a literal, an argument left out) is followed exactly
                if (Eval(iff.Cond, env) is bool known)
                {
                    foreach (var x in Nodes(known ? iff.Then : iff.Else, env)) yield return x;
                    break;
                }
                int pref = Prefer(iff.Cond);
                if (iff.Collection && iff.Else == null)
                {
                    // "if (cond) X" in a list: X is extra content — left out only when it is the loading/empty/error part
                    if (pref >= 0) foreach (var x in Nodes(iff.Then, env)) yield return x;
                    break;
                }
                var then = Nodes(iff.Then, env).ToList();
                var other = iff.Else != null ? Nodes(iff.Else, env).ToList() : new List<SketchNode>();
                var pick = pref > 0 ? (then.Count > 0 ? then : other)
                    : pref < 0 ? (other.Count > 0 ? other : then)
                    : Size(then) >= Size(other) ? then : other;
                foreach (var x in pick) yield return x;
                break;
            }
            case DList list:
                foreach (var item in list.Items)
                    foreach (var x in Nodes(item, env)) yield return x;
                break;
            case DFor f:
            {
                var body = Nodes(f.Body, env).ToList();
                int times = body.Sum(b => b.Count()) > 40 ? 2 : 3;
                for (int i = 0; i < times; i++) foreach (var x in body) yield return x;
                break;
            }
            case DSpread sp:
                foreach (var x in Nodes(sp.Inner, env)) yield return x;
                break;
            case DLambda l:
                foreach (var x in Richest(l.Returns, env)) yield return x;
                break;
            case DSwitch sw:
                foreach (var x in Richest(sw.Arms, env)) yield return x;
                break;
            case DBinary { Op: "??" } b:
            {
                var left = Nodes(b.Left, env).ToList();
                foreach (var x in left.Count > 0 ? left : Nodes(b.Right, env)) yield return x;
                break;
            }
        }
    }

    private List<SketchNode> Richest(IEnumerable<DExpr> options, Env env)
    {
        List<SketchNode> best = new();
        int bestSize = -1;
        foreach (var o in options)
        {
            var nodes = Nodes(o, env).ToList();
            int s = Size(nodes);
            if (s > bestSize) { best = nodes; bestSize = s; }
        }
        return best;
    }

    private static int Size(List<SketchNode> nodes) => nodes.Sum(n => n.Count());

    private SketchNode? One(DExpr? e, Env env, string? widget = null)
    {
        var list = Nodes(e, env).ToList();
        return list.Count switch { 0 => null, 1 => list[0], _ => Wrap(SketchKind.Column, list, e!, env, widget) };
    }

    private IEnumerable<SketchNode> Name(DName n, Env env)
    {
        if (n.Target != null) yield break;
        string path = n.Path.StartsWith("widget.", StringComparison.Ordinal) ? n.Path["widget.".Length..] : n.Path;
        if (path.Contains('.')) yield break;
        if (Lookup(path, env) is { } b)
        {
            if (b.Node != null) { yield return b.Node; yield break; }
            foreach (var x in Nodes(b.Expr, b.Env)) yield return x;
            yield break;
        }
        // a getter of the class: Widget get _header => ...
        if (env.Class != null && env.Class.Members.ContainsKey(path) && env.Depth < MaxDepth)
        {
            var returns = _p.ReturnsOf(env.Class, path, out _);
            var inner = env.Child();
            inner.Depth++;
            foreach (var x in Richest(returns, inner)) yield return x;
        }
    }

    /// <summary>What a bare name means here: a bound argument or lambda parameter, a local, or a field.</summary>
    private Bound? Lookup(string name, Env env)
    {
        if (env.Vars.TryGetValue(name, out var v)) return v;
        if (env.Args.TryGetValue(name, out var a)) return a;
        if (env.Locals != null && env.Locals.TryGetValue(name, out int at))
        {
            var e = env.File.Parser().ParseAt(at, 20_000);
            var inner = env.Child();
            // the initializer sees the other locals, never itself (final a = b; final b = a; can't loop)
            inner.Locals = env.Locals.Where(kv => kv.Key != name).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            return new Bound(e, inner);
        }
        if (env.Class != null && env.Class.Fields.TryGetValue(name, out int f))
            return new Bound(env.Class.File.Parser().ParseAt(f, 20_000), new Env { File = env.Class.File, Class = env.Class, Depth = env.Depth, Use = env.Use, Expanding = env.Expanding });
        return null;
    }

    // ───────────────────────────── calls ─────────────────────────────

    private IEnumerable<SketchNode> Call(DCall c, Env env)
    {
        if (c.Target != null)
        {
            foreach (var x in MethodOn(c, env)) yield return x;
            yield break;
        }
        string bare = c.Name.TrimStart('_');
        if (bare.Length == 0) yield break;
        // posts.when(…), items.map(…): a method on a variable (the parser reads "posts.when" as one dotted name)
        int dot = c.Name.LastIndexOf('.');
        if (dot > 0 && !char.IsUpper(bare[0]) && !c.Name.StartsWith("widget.", StringComparison.Ordinal))
        {
            var onVar = new DCall { Name = c.Name[(dot + 1)..], Target = new DName { Path = c.Name[..dot], Pos = c.Pos }, Pos = c.Pos };
            onVar.Args.AddRange(c.Args);
            foreach (var (k, v) in c.Named) onVar.Named[k] = v;
            foreach (var x in MethodOn(onVar, env)) yield return x;
            yield break;
        }
        // a function passed in as an argument: TpAsync(data: (d) => _Body(d: d)) calls data(value)
        string fn = c.Name.StartsWith("widget.", StringComparison.Ordinal) ? c.Name[7..] : c.Name;
        if (!fn.Contains('.') && Lookup(fn, env) is { Expr: { } fnExpr } fnBound && env.Depth < MaxDepth)
        {
            var target = fnExpr;
            if (target is DName && Lookup(((DName)target).Path, fnBound.Env) is { Expr: DLambda deeper }) target = deeper;
            if (target is DLambda lam)
            {
                var inner = fnBound.Env.Child();
                for (int i = 0; i < lam.Params.Count && i < c.Args.Count; i++) inner.Vars[lam.Params[i]] = new Bound(c.Args[i], env);
                foreach (var x in Richest(lam.Returns, inner)) yield return x;
                yield break;
            }
        }
        // a method of this class / a helper: _buildHeader(context)
        if (!char.IsUpper(bare[0]) || env.Class?.Members.ContainsKey(c.Name) == true)
        {
            if (env.Class != null && env.Class.Members.ContainsKey(c.Name) && env.Depth < MaxDepth)
            {
                var returns = _p.ReturnsOf(env.Class, c.Name, out var ps);
                var inner = env.Child();
                inner.Depth++;
                inner.Locals = _p.LocalsOf(env.Class, c.Name);
                for (int i = 0; i < ps.Count && i < c.Args.Count; i++) inner.Vars[ps[i]] = new Bound(c.Args[i], env);
                foreach (var (k, v) in c.Named) inner.Vars[k] = new Bound(v, env);
                foreach (var x in Richest(returns, inner)) yield return x;
            }
            yield break;
        }
        if (_p.ClassOf(c.Name) is { IsWidget: true } cls && !Known.ContainsKey(c.Name))
        {
            var n = Expand(cls, c, env, c.Pos);
            if (n != null) yield return n;
            yield break;
        }
        if (Known.TryGetValue(c.Name, out var map) || Known.TryGetValue(c.Base, out map))
        {
            foreach (var x in map(this, c, env)) yield return x;
            yield break;
        }
        if (_p.ClassOf(c.Name) is { IsWidget: true } own)
        {
            var n = Expand(own, c, env, c.Pos);
            if (n != null) yield return n;
            yield break;
        }
        if (char.IsUpper(c.Name[0]) && !LooksLikeValue(c.Name)) foreach (var x in UnknownWidget(c, env)) yield return x;
    }

    /// <summary>Constructors that are values, not widgets (EdgeInsets, TextStyle, Duration…).</summary>
    private static bool LooksLikeValue(string name) => Regex.IsMatch(name,
        @"^(EdgeInsets|EdgeInsetsDirectional|TextStyle|BoxDecoration|ShapeDecoration|BorderRadius|Radius|Border|BorderSide|BoxShadow|Color|Colors|LinearGradient|RadialGradient|SweepGradient|Duration|Offset|Size|Alignment|AlignmentDirectional|InputDecoration|ButtonStyle|RoundedRectangleBorder|StadiumBorder|CircleBorder|BoxConstraints|TextEditingController|ScrollController|FocusNode|GlobalKey|ValueKey|Key|UniqueKey|ObjectKey|Matrix4|Curves|Tween|Rect|DateTime|Uri|Future|Stream|Timer|Text(Span|Theme)?Data|ThemeData|ColorScheme|IconData|AssetImage|NetworkImage|FileImage|MemoryImage|DecorationImage|ImageFilter|Paint|Path|Shadow|FontWeight|TextAlign|MaterialStateProperty|WidgetStateProperty|SystemUiOverlayStyle|ImageProvider|SliverGridDelegate\w*|SliverChild\w*Delegate|NeverScrollableScrollPhysics|BouncingScrollPhysics|ClampingScrollPhysics|AlwaysScrollableScrollPhysics|PageController|TabController|AnimationController|ValueNotifier|ChangeNotifier|StatefulNavigationShell|GoRouterState|NavigationDestination|BottomNavigationBarItem|DataColumn|DataRow|DataCell|ButtonSegment|PopupMenuItem|DropdownMenuItem|DropdownMenuEntry)$");

    private IEnumerable<SketchNode> MethodOn(DCall c, Env env)
    {
        switch (c.Name)
        {
            case "map" or "expand" or "mapIndexed" when c.Positional(0) is DLambda l:
            {
                var item = Richest(l.Returns, env);
                int times = Size(item) > 40 ? 2 : 3;
                for (int i = 0; i < times; i++) foreach (var x in item) yield return x;
                break;
            }
            case "when" or "maybeWhen" or "whenOrNull" or "map" or "maybeMap" or "whenData" when c.Arg("data") != null || c.Arg("orElse") != null:
                foreach (var x in Nodes(c.Arg("data") ?? c.Arg("orElse"), env)) yield return x;
                break;
            case "toList" or "toSet" or "cast" or "where" or "take" or "skip" or "reversed" or "followedBy" or "sublist" or "whereType":
                foreach (var x in Nodes(c.Target, env)) yield return x;
                break;
            case "builder" or "build" when c.Target is DName:
                break;
        }
    }

    /// <summary>Draw a project widget from its own build(), with the caller's arguments in place of its parameters.</summary>
    private SketchNode? Expand(DartClass cls, DCall? call, Env caller, int callPos)
    {
        var at = (DExpr?)call ?? new DOther { Pos = callPos };
        if (caller.Depth >= MaxDepth || caller.Expanding.Contains(cls.Name) || _nodes > MaxNodes)
            return Leaf(SketchKind.Box, at, caller, cls.Name, Humanize(cls.Name));
        var buildCls = _p.BuildClassOf(cls) ?? cls;
        var env = new Env
        {
            File = buildCls.File,
            Class = buildCls,
            Depth = caller.Depth + 1,
            Expanding = new HashSet<string>(caller.Expanding, StringComparer.Ordinal) { cls.Name },
            Locals = _p.LocalsOf(buildCls, "build"),
            // a block drawn inside a shared widget opens where the SCREEN uses that widget
            Use = caller.Use ?? (caller.File == _screenFile && call != null && cls.File != _screenFile ? (caller.File.Path, caller.File.LineOf(call.Pos)) : null),
        };
        // every parameter is bound: to the caller's argument, else to its default ("this.back = false"), else null —
        // so "if (back)" or "if (bottom != null)" in the widget's build is decided, not guessed
        var defaults = new Env { File = cls.File, Class = cls, Depth = env.Depth, Expanding = env.Expanding, Use = env.Use };
        foreach (var p in cls.Params)
        {
            var arg = call == null ? null : p.Named ? call.Arg(p.Name) : call.Positional(p.Index);
            if (arg != null) env.Args[p.Name] = new Bound(arg, caller);
            else if (p.DefaultAt >= 0) env.Args[p.Name] = new Bound(cls.File.Parser().ParseAt(p.DefaultAt, 4000), defaults);
            else env.Args[p.Name] = new Bound(new DName { Path = "null", Pos = cls.File.Tokens[cls.Open].Pos }, defaults);
        }
        var returns = _p.ReturnsOf(buildCls, "build", out _);
        var nodes = Richest(returns, env);
        if (nodes.Count == 0)
            return call != null ? UnknownWidget(call, caller).FirstOrDefault() : null;
        return nodes.Count == 1 ? nodes[0] : Wrap(SketchKind.Column, nodes, at, caller, cls.Name);
    }

    private IEnumerable<SketchNode> UnknownWidget(DCall c, Env env)
    {
        // a chart (a CustomPaint or a chart package): drawn as a chart, not as an unreadable box
        if (Regex.IsMatch(c.Name, @"(?i)chart|graph|sparkline|plot|gauge"))
        {
            var chart = Leaf(SketchKind.Image, c, env, c.Name);
            chart.Variant = "chart";
            chart.Height = Number(c.Arg("height")) ?? 120;
            yield return chart;
            yield break;
        }
        string? text = null;
        foreach (var key in new[] { "title", "label", "text", "name", "message", "hint", "placeholder", "value" })
            if ((text = TextOf(c.Arg(key), env)) != null) break;
        text ??= c.Args.Count > 0 ? TextOf(c.Args[0], env) : null;
        var kids = new List<SketchNode>();
        foreach (var key in new[] { "leading", "child", "children", "body", "slivers", "sliver", "content", "header", "items", "actions", "footer", "trailing", "bottom" })
            kids.AddRange(Nodes(c.Arg(key), env));
        if (kids.Count == 0)
        {
            foreach (var a in c.Args) kids.AddRange(Nodes(a, env));
        }
        if (kids.Count == 0)
        {
            if (text == null) _unknown++;
            yield return Leaf(SketchKind.Box, c, env, c.Name, text ?? Humanize(c.Name));
            yield break;
        }
        if (text != null) kids.Insert(0, Leaf(SketchKind.Heading, c, env, c.Name, text));
        yield return Wrap(SketchKind.Column, kids, c, env, c.Name);
    }

    // ───────────────────────────── block factory ─────────────────────────────

    private SketchNode Leaf(SketchKind kind, DExpr at, Env env, string? widget, string? text = null)
    {
        _nodes++;
        return new SketchNode
        {
            Kind = kind, Widget = widget, Text = text, File = env.File.Path, Line = env.File.LineOf(at.Pos),
            UseFile = env.Use?.File, UseLine = env.Use?.Line ?? 0,
        };
    }

    private SketchNode Wrap(SketchKind kind, IEnumerable<SketchNode> children, DExpr at, Env env, string? widget)
    {
        var n = Leaf(kind, at, env, widget);
        n.Children.AddRange(children);
        return n;
    }

    // ───────────────────────────── text, icons, conditions ─────────────────────────────

    private string? TextOf(DExpr? e, Env env, int depth = 0)
    {
        if (e == null || depth > 12) return null;
        switch (e)
        {
            case DStr s:
                if (s.Interpolated && s.Value == "…" && s.Interp != null && !s.Interp.Contains(','))
                {
                    string name = s.Interp.StartsWith("widget.", StringComparison.Ordinal) ? s.Interp[7..] : s.Interp;
                    if (Regex.IsMatch(name, @"^\w+$") && Lookup(name, env) is { Expr: { } bound } b) return TextOf(bound, b.Env, depth + 1) ?? "…";
                }
                return s.Value;
            case DName n:
            {
                if (L10n(n) is { } t) return t;
                if (n.Target != null) return null;
                string path = n.Path.StartsWith("widget.", StringComparison.Ordinal) ? n.Path[7..] : n.Path;
                if (path.Contains('.')) return null;
                return Lookup(path, env) is { Expr: { } bound } b ? TextOf(bound, b.Env, depth + 1) : null;
            }
            case DCall c:
                if (c.Target == null && c.Name is "Text" or "SelectableText" or "AutoSizeText" or "Tab") return TextOf(c.Positional(0) ?? c.Arg("text"), env, depth + 1);
                if (c.Target == null && c.Name is "Text.rich" or "RichText") return SpanText(c.Positional(0) ?? c.Arg("text"), env, depth);
                if (c.Target != null && _p.L10n.TryGetValue(c.Name, out var lt)) return lt;   // AppLocalizations.of(context).greeting(name)
                if (c.Target == null && c.Name.LastIndexOf('.') is > 0 and var ld && _p.L10n.TryGetValue(c.Name[(ld + 1)..], out var lt2)) return lt2;   // l10n.greeting(name)
                if (c.Name is "tr" or "tr()" && c.Target is DStr key) return key.Value;          // 'key'.tr()
                if (c.Name is "toUpperCase" or "toLowerCase" or "trim") return TextOf(c.Target, env, depth + 1);
                return null;
            case DBinary { Op: "+" } b:
            {
                string? l = TextOf(b.Left, env, depth + 1), r = TextOf(b.Right, env, depth + 1);
                return l == null && r == null ? null : (l ?? "…") + (r ?? "…");
            }
            case DBinary { Op: "??" } b:
                return TextOf(b.Left, env, depth + 1) ?? TextOf(b.Right, env, depth + 1);
            case DIf iff:
            {
                if (Eval(iff.Cond, env) is bool known) return TextOf(known ? iff.Then : iff.Else, env, depth + 1);
                int pref = Prefer(iff.Cond);
                return pref < 0 ? TextOf(iff.Else, env, depth + 1) ?? TextOf(iff.Then, env, depth + 1)
                    : TextOf(iff.Then, env, depth + 1) ?? TextOf(iff.Else, env, depth + 1);
            }
            default:
                return null;
        }
    }

    private string? SpanText(DExpr? span, Env env, int depth)
    {
        if (span is not DCall c) return TextOf(span, env, depth + 1);
        var sb = new StringBuilder();
        if (TextOf(c.Arg("text"), env, depth + 1) is { } t) sb.Append(t);
        if (c.Arg("children") is DList kids)
            foreach (var k in kids.Items) if (SpanText(k, env, depth + 1) is { } kt) sb.Append(kt);
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private string? L10n(DName n)
    {
        string last = n.Path.Contains('.') ? n.Path[(n.Path.LastIndexOf('.') + 1)..] : n.Path;
        if (!_p.L10n.TryGetValue(last, out var v)) return null;
        bool viaOf = n.Target is DCall { Name: var on } && on.EndsWith(".of", StringComparison.Ordinal);
        return n.Path.Contains('.') || viaOf ? v : null;
    }

    private string? IconOf(DExpr? e, Env env, int depth = 0)
    {
        if (e == null || depth > 6) return null;
        switch (e)
        {
            case DName n when n.Target == null:
                if (n.Path.Contains('.')) return n.Path[(n.Path.LastIndexOf('.') + 1)..];
                return Lookup(n.Path, env) is { Expr: { } b } bound ? IconOf(b, bound.Env, depth + 1) : null;
            case DName n:
                return n.Path;
            case DCall c when c.Name is "Icon" or "PhosphorIcon" or "FaIcon" or "HugeIcon" or "Iconify" or "ImageIcon":
                return IconOf(c.Positional(0) ?? c.Arg("icon"), env, depth + 1);
            case DCall c when c.Name.Contains('.') && char.IsUpper(c.Name[0]):   // PhosphorIcons.house()
                return c.Name[(c.Name.LastIndexOf('.') + 1)..];
            case DCall c:
                return IconOf(c.Arg("icon") ?? c.Arg("child"), env, depth + 1);
            case DIf iff:
                return IconOf(iff.Then, env, depth + 1) ?? IconOf(iff.Else, env, depth + 1);
            default:
                return null;
        }
    }

    /// <summary>The value of a condition when the code fixes it (true/false literals, a parameter's default, an
    /// argument left out = null, and !, &amp;&amp;, ||, == null / != null over those) — null when it depends on runtime state.</summary>
    private bool? Eval(DExpr? e, Env env, int depth = 0)
    {
        if (e == null || depth > 10) return null;
        switch (e)
        {
            case DName { Target: null, Path: "true" }: return true;
            case DName { Target: null, Path: "false" }: return false;
            case DName n when n.Target == null:
            {
                string path = n.Path.StartsWith("widget.", StringComparison.Ordinal) ? n.Path[7..] : n.Path;
                if (path.Contains('.') || Lookup(path, env) is not { } b) return null;
                return b.Node != null ? null : Eval(b.Expr, b.Env, depth + 1);
            }
            case DBinary { Op: "!" } not:
                return Eval(not.Right, env, depth + 1) is bool inner ? !inner : null;
            case DBinary { Op: "&&" } and:
            {
                bool? l = Eval(and.Left, env, depth + 1), r = Eval(and.Right, env, depth + 1);
                return l == false || r == false ? false : l == true && r == true ? true : null;
            }
            case DBinary { Op: "||" } or:
            {
                bool? l = Eval(or.Left, env, depth + 1), r = Eval(or.Right, env, depth + 1);
                return l == true || r == true ? true : l == false && r == false ? false : null;
            }
            case DBinary { Op: "==" or "!=" } cmp:
            {
                bool? isNull = cmp.Right is DName { Path: "null" } ? IsNull(cmp.Left, env, depth) : cmp.Left is DName { Path: "null" } ? IsNull(cmp.Right, env, depth) : null;
                return isNull is bool known ? (cmp.Op == "==" ? known : !known) : null;
            }
            default:
                return null;
        }
    }

    /// <summary>Is this null here? True for null / an argument left out, false for a written value, null when unknown.</summary>
    private bool? IsNull(DExpr? e, Env env, int depth)
    {
        if (e == null || depth > 10) return null;
        switch (e)
        {
            case DName { Target: null, Path: "null" }: return true;
            case DName n when n.Target == null:
            {
                string path = n.Path.StartsWith("widget.", StringComparison.Ordinal) ? n.Path[7..] : n.Path;
                if (path.Contains('.') || Lookup(path, env) is not { } b) return null;
                if (b.Node != null) return false;
                // only an ARGUMENT's value is known here; a local or a field may be set later at runtime
                return env.Args.ContainsKey(path) || env.Vars.ContainsKey(path) ? IsNull(b.Expr, b.Env, depth + 1) : null;
            }
            case DStr or DNum or DList or DMap or DLambda: return false;
            case DCall c when c.Target == null && c.Name.Length > 0 && char.IsUpper(c.Name.TrimStart('_')[0]): return false;   // a constructor
            default: return null;
        }
    }

    /// <summary>+1: draw the "then" branch (the content), -1: the "else" branch, 0: whichever draws more.</summary>
    internal static int Prefer(DExpr? cond)
    {
        if (cond == null) return 0;
        string c = Flat(cond).Replace(" ", "");
        bool neg = c.StartsWith('!');
        bool isNull = c.Contains("==null", StringComparison.Ordinal), notNull = c.Contains("!=null", StringComparison.Ordinal);
        // the words decide first: "error != null ? Err : Content" draws Content, and so does "error == null ? Content : Err"
        bool bad = Regex.IsMatch(c, @"(?i)(isLoading|loading|isEmpty|hasError|error|isFetching|busy|refreshing|waiting|isRefreshing|isInitial|mounted|failed|failure)");
        bool good = !bad && Regex.IsMatch(c, @"(?i)(isNotEmpty|hasData|hasValue|loaded|isAuthenticated|isLoggedIn|isReady|success)");
        if (bad || good)
        {
            int v = bad ? -1 : 1;
            if (isNull) v = -v;
            if (neg) v = -v;
            return v;
        }
        if (isNull) return -1;   // "x == null ? placeholder : content"
        if (notNull) return 1;
        return 0;
    }

    /// <summary>A flat text of an expression (names, operators, numbers) — for reading conditions and styles.</summary>
    internal static string Flat(DExpr? e, int depth = 0)
    {
        if (e == null || depth > 8) return "";
        return e switch
        {
            DName n => (n.Target != null ? Flat(n.Target, depth + 1) + "." : "") + n.Path,
            DBinary { Op: "!" } b => "!" + Flat(b.Right, depth + 1),
            DBinary b => Flat(b.Left, depth + 1) + b.Op + Flat(b.Right, depth + 1),
            DCall c => (c.Target != null ? Flat(c.Target, depth + 1) + "." : "") + c.Name + "(" +
                       string.Join(",", c.Args.Select(a => Flat(a, depth + 1)).Concat(c.Named.Select(kv => kv.Key + ":" + Flat(kv.Value, depth + 1)))) + ")",
            DNum num => num.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DStr s => "'" + s.Value + "'",
            DIf iff => Flat(iff.Cond, depth + 1) + "?" + Flat(iff.Then, depth + 1) + ":" + Flat(iff.Else, depth + 1),
            _ => "",
        };
    }

    private static double? Number(DExpr? e) => e switch
    {
        DNum n => n.Value,
        DBinary { Op: "*" or "/" or "+" or "-" } => null,
        _ => null,
    };

    private static string Humanize(string widget) => Pages.WebPages.Humanize(widget.TrimStart('_'));

    // ───────────────────────────── Flutter's widgets ─────────────────────────────

    private delegate IEnumerable<SketchNode> Mapper(FlutterSketcher s, DCall c, Env env);

    private static readonly Dictionary<string, Mapper> Known = BuildKnown();

    private static Dictionary<string, Mapper> BuildKnown()
    {
        var k = new Dictionary<string, Mapper>(StringComparer.Ordinal);

        // wrappers: draw what they wrap
        foreach (var w in new[]
        {
            "Padding", "Center", "Align", "Expanded", "Flexible", "ConstrainedBox", "LimitedBox", "FittedBox", "AspectRatio", "SafeArea",
            "SliverSafeArea", "SingleChildScrollView", "Scrollbar", "RawScrollbar", "RefreshIndicator", "InkWell", "InkResponse", "GestureDetector",
            "Hero", "Semantics", "MergeSemantics", "ExcludeSemantics", "Opacity", "AnimatedOpacity", "AnimatedSize", "AnimatedSwitcher",
            "AnimatedPadding", "AnimatedAlign", "AnimatedPositioned", "FadeTransition", "SlideTransition", "ScaleTransition", "SizeTransition",
            "RotationTransition", "Transform", "RotatedBox", "Visibility", "IgnorePointer", "AbsorbPointer", "MouseRegion", "Listener", "Focus",
            "FocusScope", "Form", "Builder", "LayoutBuilder", "OrientationBuilder", "StatefulBuilder", "Consumer", "Consumer2", "ValueListenableBuilder",
            "StreamBuilder", "FutureBuilder", "AnimatedBuilder", "ListenableBuilder", "TweenAnimationBuilder", "Directionality", "MediaQuery",
            "Theme", "DefaultTextStyle", "IconTheme", "DefaultTabController", "PopScope", "WillPopScope", "KeyboardListener", "Shortcuts",
            "Actions", "ClipRRect", "ClipOval", "ClipPath", "ClipRect", "BackdropFilter", "ShaderMask",
            "IntrinsicHeight", "IntrinsicWidth", "Baseline", "FractionallySizedBox", "OverflowBox", "UnconstrainedBox", "SliverToBoxAdapter",
            "SliverPadding", "SliverFillRemaining", "SliverFillViewport", "SliverLayoutBuilder", "CustomPaint", "Tooltip", "Draggable", "DragTarget",
            "Dismissible", "ReorderableDragStartListener", "KeyedSubtree", "RepaintBoundary", "ProviderScope", "Provider", "ChangeNotifierProvider",
            "MultiProvider", "BlocProvider", "MultiBlocProvider", "BlocBuilder", "BlocConsumer", "BlocListener", "BlocSelector", "Obx", "GetBuilder",
            "Observer", "Selector", "HookBuilder", "HookConsumer", "Material", "Offstage", "TickerMode", "NotificationListener",
            "SliverAnimatedOpacity", "SliverOpacity", "SliverVisibility", "SliverIgnorePointer", "SliverOffstage", "SliverConstrainedCrossAxis",
            "SliverCrossAxisConstrained", "AnnotatedRegion", "Banner", "CupertinoTheme", "ScrollConfiguration", "PrimaryScrollController",
        })
            k[w] = (s, c, env) => s.Through(c, env);

        k["Scaffold"] = (s, c, env) => new[] { s.Scaffold(c, env) };
        k["CupertinoPageScaffold"] = k["Scaffold"];
        foreach (var w in new[] { "AppBar", "SliverAppBar", "CupertinoNavigationBar", "CupertinoSliverNavigationBar" })
            k[w] = (s, c, env) => s.AppBarOf(c, env);
        k["Column"] = (s, c, env) => s.Flow(SketchKind.Column, c, env);
        k["ListBody"] = k["Column"];
        k["Row"] = (s, c, env) => s.Flow(SketchKind.Row, c, env);
        k["Wrap"] = (s, c, env) => s.Flow(SketchKind.Wrap, c, env);
        k["OverflowBar"] = k["Wrap"];
        k["ButtonBar"] = k["Wrap"];
        k["Flex"] = (s, c, env) => s.Flow(Flat(c.Arg("direction")).Contains("horizontal") ? SketchKind.Row : SketchKind.Column, c, env);
        k["Stack"] = (s, c, env) => s.Flow(SketchKind.Stack, c, env);
        k["IndexedStack"] = (s, c, env) => s.Nodes(c.Arg("children") is DList l && l.Items.Count > 0 ? l.Items[0] : null, env);
        k["CustomScrollView"] = (s, c, env) => s.Flow(SketchKind.Column, c, env, "slivers");
        k["NestedScrollView"] = (s, c, env) =>
        {
            var kids = new List<SketchNode>();
            if (c.Arg("headerSliverBuilder") is DLambda hl) kids.AddRange(s.Richest(hl.Returns, env));
            kids.AddRange(s.Nodes(c.Arg("body"), env));
            return new[] { s.Wrap(SketchKind.Column, kids, c, env, c.Name) };
        };
        k["PageView"] = (s, c, env) => s.FirstItem(c, env);
        k["TabBarView"] = k["PageView"];
        k["SliverList"] = (s, c, env) => s.ListOf(c, env);
        k["SliverFixedExtentList"] = k["SliverList"];
        k["ListView"] = (s, c, env) => s.ListOf(c, env);
        k["ReorderableListView"] = k["ListView"];
        k["AnimatedList"] = k["ListView"];
        k["GridView"] = (s, c, env) => s.GridOf(c, env);
        k["SliverGrid"] = k["GridView"];
        k["MasonryGridView"] = k["GridView"];
        // Positioned children of a Stack take no room: a glow, a corner badge, a close button over an image
        // (Positioned.fill, or pinned to both sides, is the stack's full-size content — it keeps its room)
        k["Positioned"] = (s, c, env) =>
        {
            var kids = s.Through(c, env).ToList();
            bool full = c.Variant == "fill" || c.Arg("left") != null && c.Arg("right") != null && (c.Arg("top") != null || c.Arg("bottom") != null);
            if (!full) foreach (var n in kids) n.Overlay = true;
            return kids;
        };
        k["PositionedDirectional"] = k["Positioned"];
        k["ColoredBox"] = (s, c, env) => c.Arg("child") != null ? s.Through(c, env) : Array.Empty<SketchNode>();   // a fill: the parent's colour
        k["Container"] = (s, c, env) => s.Box(c, env);
        k["AnimatedContainer"] = k["Container"];
        k["DecoratedBox"] = k["Container"];
        k["Ink"] = k["Container"];
        k["Card"] = (s, c, env) => new[] { s.CardOf(c, env) };
        k["SizedBox"] = (s, c, env) => s.SizedBoxOf(c, env);
        k["Gap"] = (s, c, env) => new[] { s.SpacerOf(c, env, Number(c.Positional(0)) ?? 8) };
        k["SliverGap"] = k["Gap"];
        k["Spacer"] = (s, c, env) => Array.Empty<SketchNode>();
        k["Divider"] = (s, c, env) => new[] { s.Leaf(SketchKind.Divider, c, env, c.Name) };
        k["VerticalDivider"] = k["Divider"];
        k["Text"] = (s, c, env) => new[] { s.TextNode(c, env) };
        k["SelectableText"] = k["Text"];
        k["AutoSizeText"] = k["Text"];
        k["RichText"] = k["Text"];
        k["Icon"] = (s, c, env) => new[] { s.IconNode(SketchKind.Icon, c, env) };
        k["PhosphorIcon"] = k["Icon"];
        k["FaIcon"] = k["Icon"];
        k["HugeIcon"] = k["Icon"];
        k["ImageIcon"] = k["Icon"];
        k["IconButton"] = (s, c, env) => new[] { s.IconNode(SketchKind.IconButton, c, env) };
        k["BackButton"] = (s, c, env) => new[] { s.Named(SketchKind.IconButton, c, env, icon: "arrow_back") };
        k["CloseButton"] = (s, c, env) => new[] { s.Named(SketchKind.IconButton, c, env, icon: "close") };
        k["PopupMenuButton"] = (s, c, env) => new[] { s.Named(SketchKind.IconButton, c, env, icon: s.IconOf(c.Arg("icon"), env) ?? "more_vert") };
        foreach (var (w, variant) in new[] { ("ElevatedButton", "filled"), ("FilledButton", "filled"), ("OutlinedButton", "outlined"), ("TextButton", "text"),
                     ("CupertinoButton", "text"), ("MaterialButton", "filled"), ("RawMaterialButton", "filled") })
        {
            string v = variant;
            k[w] = (s, c, env) => new[] { s.ButtonOf(c, env, c.Variant == "tonal" ? "tonal" : c.Name.EndsWith(".filled", StringComparison.Ordinal) ? "filled" : v) };
        }
        k["FloatingActionButton"] = (s, c, env) =>
        {
            var n = s.ButtonOf(c, env, "fab");
            var fab = new SketchNode { Kind = SketchKind.Fab, Text = c.Variant == "extended" ? n.Text : null, Icon = n.Icon ?? s.IconOf(c.Arg("child"), env) ?? "add",
                Widget = c.Name, File = n.File, Line = n.Line, UseFile = n.UseFile, UseLine = n.UseLine, Floating = true };
            return new[] { fab };
        };
        foreach (var w in new[] { "TextField", "TextFormField", "CupertinoTextField", "CupertinoSearchTextField", "SearchBar", "DropdownButtonFormField",
                     "DropdownMenu", "DropdownButton", "Autocomplete", "SearchAnchor", "TypeAheadField", "PinCodeTextField", "Pinput", "OtpTextField" })
            k[w] = (s, c, env) => new[] { s.InputOf(c, env) };
        foreach (var w in new[] { "Switch", "CupertinoSwitch", "Checkbox", "Radio", "CupertinoCheckbox", "CupertinoRadio" })
            k[w] = (s, c, env) => new[] { s.Leaf(SketchKind.Toggle, c, env, c.Name) };
        k["Slider"] = (s, c, env) => new[] { s.Leaf(SketchKind.Slider, c, env, c.Name) };
        k["RangeSlider"] = k["Slider"];
        k["CupertinoSlider"] = k["Slider"];
        foreach (var w in new[] { "Chip", "ActionChip", "FilterChip", "ChoiceChip", "InputChip", "RawChip" })
            k[w] = (s, c, env) => new[] { s.Leaf(SketchKind.Chip, c, env, c.Name, s.TextOf(c.Arg("label"), env) ?? "…") };
        k["Badge"] = (s, c, env) => s.Through(c, env);
        foreach (var w in new[] { "Image", "CachedNetworkImage", "SvgPicture", "FadeInImage", "Lottie", "FlutterLogo", "ExtendedImage", "OctoImage" })
            k[w] = (s, c, env) =>
            {
                var n = s.Leaf(SketchKind.Image, c, env, c.Name);
                n.Height = Number(c.Arg("height")) ?? 0;
                return new[] { n };
            };
        k["CircleAvatar"] = (s, c, env) => new[] { s.Leaf(SketchKind.Avatar, c, env, c.Name, s.TextOf(c.Arg("child"), env)) };
        foreach (var w in new[] { "CircularProgressIndicator", "RefreshProgressIndicator", "CupertinoActivityIndicator" })
            k[w] = (s, c, env) => new[] { s.Leaf(SketchKind.Progress, c, env, c.Name) };
        k["LinearProgressIndicator"] = (s, c, env) => { var n = s.Leaf(SketchKind.Progress, c, env, c.Name); n.Variant = "linear"; return new[] { n }; };
        foreach (var w in new[] { "ListTile", "CheckboxListTile", "SwitchListTile", "RadioListTile", "ExpansionTile", "CupertinoListTile", "CupertinoListTile.notched" })
            k[w] = (s, c, env) => new[] { s.TileOf(c, env) };
        k["TabBar"] = (s, c, env) => new[] { s.ItemsOf(SketchKind.Tabs, c, env, "tabs") };
        k["SegmentedButton"] = (s, c, env) => new[] { s.ItemsOf(SketchKind.Tabs, c, env, "segments") };
        k["CupertinoSegmentedControl"] = (s, c, env) => new[] { s.ItemsOf(SketchKind.Tabs, c, env, "children") };
        k["CupertinoSlidingSegmentedControl"] = k["CupertinoSegmentedControl"];
        k["NavigationBar"] = (s, c, env) => new[] { s.ItemsOf(SketchKind.NavBar, c, env, "destinations") };
        k["BottomNavigationBar"] = (s, c, env) => new[] { s.ItemsOf(SketchKind.NavBar, c, env, "items") };
        k["CupertinoTabBar"] = k["BottomNavigationBar"];
        k["NavigationRail"] = (s, c, env) => new[] { s.ItemsOf(SketchKind.NavBar, c, env, "destinations") };
        k["BottomAppBar"] = (s, c, env) => s.Through(c, env);
        k["Drawer"] = (s, c, env) => Array.Empty<SketchNode>();
        k["NavigationDrawer"] = k["Drawer"];
        foreach (var w in new[] { "AlertDialog", "SimpleDialog", "Dialog", "CupertinoAlertDialog", "BottomSheet", "Dialog.fullscreen" })
            k[w] = (s, c, env) => new[] { s.DialogOf(c, env) };
        k["DataTable"] = (s, c, env) => new[] { s.TableOf(c, env) };
        k["Table"] = (s, c, env) => { var n = s.Leaf(SketchKind.Table, c, env, c.Name); n.Columns = 3; return new[] { n }; };
        k["Placeholder"] = (s, c, env) => new[] { s.Leaf(SketchKind.Box, c, env, c.Name, "Placeholder") };
        k["Stepper"] = (s, c, env) => new[] { s.Leaf(SketchKind.Box, c, env, c.Name, "Stepper") };
        k["Tab"] = (s, c, env) => new[] { s.Leaf(SketchKind.Text, c, env, c.Name, s.TextOf(c, env) ?? "…") };
        return k;
    }

    // ───────────────────────────── mappers ─────────────────────────────

    private IEnumerable<SketchNode> Through(DCall c, Env env)
    {
        foreach (var key in new[] { "child", "sliver", "builder", "children", "slivers", "body" })
            if (c.Arg(key) is { } inner)
            {
                var list = Nodes(inner, env).ToList();
                if (list.Count > 0) return list;
            }
        // Transform.scale(scale: 2, child: …)  /  Visibility(visible: x, child: …) are covered above
        return c.Args.Count > 0 ? Nodes(c.Args[^1], env).ToList() : new List<SketchNode>();
    }

    private IEnumerable<SketchNode> Flow(SketchKind kind, DCall c, Env env, string key = "children")
    {
        var kids = Nodes(c.Arg(key), env).ToList();
        if (kids.Count == 0) return kids;
        var n = Wrap(kind, kids, c, env, c.Name);
        return new[] { n };
    }

    private IEnumerable<SketchNode> FirstItem(DCall c, Env env)
    {
        if (c.Arg("children") is DList l && l.Items.Count > 0) return Nodes(l.Items[0], env);
        if (c.Arg("itemBuilder") is DLambda b) return Richest(b.Returns, env);
        return Array.Empty<SketchNode>();
    }

    private SketchNode Scaffold(DCall c, Env env)
    {
        var kids = new List<SketchNode>();
        foreach (var key in new[] { "appBar", "navigationBar" }) kids.AddRange(Nodes(c.Arg(key), env));
        kids.AddRange(Nodes(c.Arg("body") ?? c.Arg("child"), env));
        if (c.Arg("persistentFooterButtons") is { } footer)
        {
            var row = Nodes(footer, env).ToList();
            if (row.Count > 0) kids.Add(Wrap(SketchKind.Row, row, footer, env, "persistentFooterButtons"));
        }
        if (c.Arg("bottomNavigationBar") is { } bottom) kids.Add(BottomBar(bottom, env));
        if (c.Arg("floatingActionButton") is { } fab)
            foreach (var f in Nodes(fab, env)) { f.Floating = true; kids.Add(f); }
        return Wrap(SketchKind.Column, kids, c, env, c.Name);
    }

    /// <summary>A Scaffold's bottomNavigationBar: a nav bar from whatever list of items it is given (also a
    /// project's own tab bar: <c>TpTabBar(items: [TpTabItem('ภาพรวม', …)])</c>).</summary>
    private SketchNode BottomBar(DExpr bottom, Env env)
    {
        if (bottom is DCall c && c.Target == null && !Known.ContainsKey(c.Name))
            foreach (var key in new[] { "items", "destinations", "tabs", "children" })
                if (c.Arg(key) is DList list && list.Items.Count > 0)
                {
                    var bar = Leaf(SketchKind.NavBar, c, env, c.Name);
                    foreach (var item in list.Items.OfType<DCall>())
                    {
                        string label = TextOf(item.Arg("label") ?? item.Arg("title") ?? item.Arg("text"), env)
                                       ?? item.Args.Select(a => TextOf(a, env)).FirstOrDefault(t => t != null) ?? "…";
                        string? icon = IconOf(item.Arg("icon"), env) ?? item.Args.Select(a => a is DName or DCall ? IconOf(a, env) : null).FirstOrDefault(i => i != null);
                        bar.Items.Add(label);
                        bar.ItemIcons.Add(icon);
                    }
                    return bar;
                }
        var nodes = Nodes(bottom, env).ToList();
        if (nodes.Count == 1 && nodes[0].Kind == SketchKind.NavBar) return nodes[0];
        return Wrap(SketchKind.Column, nodes, bottom, env, "bottomNavigationBar");
    }

    private IEnumerable<SketchNode> AppBarOf(DCall c, Env env)
    {
        var bar = Leaf(SketchKind.AppBar, c, env, c.Name);
        var title = c.Arg("title") ?? c.Arg("largeTitle") ?? c.Arg("middle");
        bar.Text = TextOf(title, env) ?? Nodes(title, env).SelectMany(n => n.Walk()).FirstOrDefault(n => n.Text != null)?.Text;
        if (c.Arg("leading") != null || c.Arg("automaticallyImplyLeading") is DName { Path: "true" }) bar.Variant = "back";
        if (c.Arg("actions") is DList actions)
            foreach (var a in actions.Items)
            {
                bar.Items.Add("");
                bar.ItemIcons.Add(IconOf(a, env) ?? "more_vert");
            }
        var result = new List<SketchNode> { bar };
        if (c.Arg("bottom") is { } bottom) result.AddRange(Nodes(bottom, env));
        if (c.Arg("flexibleSpace") is { } flex && c.Name.StartsWith("SliverAppBar", StringComparison.Ordinal))
            result.AddRange(Nodes(flex, env).Where(n => n.Kind is not SketchKind.Box));
        return result;
    }

    private SketchNode TextNode(DCall c, Env env)
    {
        string? text = c.Name is "RichText" or "Text.rich" ? SpanText(c.Positional(0) ?? c.Arg("text"), env, 0) : TextOf(c.Positional(0), env);
        string style = Flat(c.Arg("style"));
        var kind = SketchKind.Text;
        var size = Regex.Match(style, @"fontSize:(?<n>[\d.]+)");
        double fs = size.Success ? double.Parse(size.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        if (Regex.IsMatch(style, @"(?i)display|headline|titleLarge|titleMedium|\bh[1-3]\b|heading|\.title\b|xl") || fs >= 20 || fs >= 16 && Regex.IsMatch(style, @"w[6-9]00|bold"))
            kind = SketchKind.Heading;
        else if (Regex.IsMatch(style, @"(?i)bodySmall|labelSmall|caption|small|\bxs\b|hint|muted|overline|subtle|faint") || fs > 0 && fs <= 12)
            kind = SketchKind.Caption;
        var n = Leaf(kind, c, env, c.Name, text ?? "…");
        return n;
    }

    private SketchNode IconNode(SketchKind kind, DCall c, Env env)
    {
        var n = Leaf(kind, c, env, c.Name);
        n.Icon = IconOf(kind == SketchKind.IconButton ? c.Arg("icon") : c, env) ?? "circle";
        if (kind == SketchKind.IconButton && c.Variant is "filled" or "filledTonal") n.Variant = "filled";
        return n;
    }

    private SketchNode Named(SketchKind kind, DCall c, Env env, string? icon = null, string? text = null)
    {
        var n = Leaf(kind, c, env, c.Name, text);
        n.Icon = icon;
        return n;
    }

    private SketchNode ButtonOf(DCall c, Env env, string variant)
    {
        var label = c.Arg("label") ?? c.Arg("child");
        string? text = TextOf(label, env) ?? Nodes(label, env).SelectMany(n => n.Walk()).FirstOrDefault(n => n.Text != null)?.Text;
        var n = Leaf(SketchKind.Button, c, env, c.Name, text);
        n.Variant = variant;
        n.Icon = IconOf(c.Arg("icon"), env) ?? (text == null ? IconOf(c.Arg("child"), env) : null);
        return n;
    }

    private SketchNode InputOf(DCall c, Env env)
    {
        var deco = c.Arg("decoration") as DCall;
        string? label = TextOf(deco?.Arg("labelText"), env) ?? TextOf(deco?.Arg("hintText"), env) ?? TextOf(deco?.Arg("label"), env)
                        ?? TextOf(deco?.Arg("hint"), env) ?? TextOf(c.Arg("hintText"), env) ?? TextOf(c.Arg("placeholder"), env)
                        ?? TextOf(c.Arg("label"), env) ?? TextOf(c.Arg("hint"), env) ?? TextOf(c.Arg("labelText"), env);
        var n = Leaf(SketchKind.Input, c, env, c.Name, label);
        string icons = Flat(deco?.Arg("prefixIcon")) + Flat(c.Arg("leading")) + Flat(c.Arg("prefixIcon"));
        if (c.Name.Contains("Search", StringComparison.Ordinal) || Regex.IsMatch(icons, @"(?i)search|magnifying")) n.Variant = "search";
        else if (c.Name.StartsWith("Dropdown", StringComparison.Ordinal)) n.Variant = "dropdown";
        else if (Flat(c.Arg("obscureText")) == "true") n.Variant = "password";
        return n;
    }

    private IEnumerable<SketchNode> Box(DCall c, Env env)
    {
        var inner = c.Arg("child") != null ? Nodes(c.Arg("child"), env).ToList() : new List<SketchNode>();
        bool decorated = c.Arg("decoration") != null || c.Arg("color") != null || c.Name == "Ink";
        if (!decorated) return inner.Count > 0 ? inner : SizedBoxOf(c, env);
        if (inner.Count == 0 && Number(c.Arg("height")) is not > 0) return inner;   // a fill with no size of its own: nothing to show
        var n = Wrap(SketchKind.Card, inner, c, env, c.Name);
        n.Variant = "box";
        if (inner.Count == 0) n.Height = Math.Min(Number(c.Arg("height")) ?? 24, 160);
        return new[] { n };
    }

    private SketchNode CardOf(DCall c, Env env) => Wrap(SketchKind.Card, Nodes(c.Arg("child"), env), c, env, c.Name);

    private IEnumerable<SketchNode> SizedBoxOf(DCall c, Env env)
    {
        if (c.Arg("child") is { } child) return Nodes(child, env);
        double? h = Number(c.Arg("height"));
        if (c.Variant is "square") h = Number(c.Arg("dimension"));
        if (h is > 0) return new[] { SpacerOf(c, env, h.Value) };
        return Array.Empty<SketchNode>();
    }

    private SketchNode SpacerOf(DExpr at, Env env, double h)
    {
        var n = Leaf(SketchKind.Spacer, at, env, "SizedBox");
        n.Height = Math.Clamp(h, 0, 120);
        return n;
    }

    private IEnumerable<SketchNode> ListOf(DCall c, Env env)
    {
        // a horizontal list (chips, a carousel) is a row
        bool across = Flat(c.Arg("scrollDirection")).Contains("horizontal", StringComparison.Ordinal);
        var list = Leaf(across ? SketchKind.Row : SketchKind.List, c, env, c.Name);
        if (c.Arg("children") is { } kids) list.Children.AddRange(Nodes(kids, env));
        else if (c.Arg("delegate") is DCall d)
        {
            if (d.Arg("builder") is DLambda b) Repeat(list, Richest(b.Returns, env), 3);
            else if (d.Positional(0) is { } items) list.Children.AddRange(Nodes(items, env));
        }
        else if (c.Arg("itemBuilder") is DLambda ib)
        {
            Repeat(list, Richest(ib.Returns, env), 3);
            if (c.Variant == "separated" && !across) list.Variant = "divided";
        }
        if (list.Children.Count == 0) return Array.Empty<SketchNode>();
        return new[] { list };
    }

    private static void Repeat(SketchNode into, List<SketchNode> item, int times)
    {
        if (item.Count == 0) return;
        if (item.Sum(i => i.Count()) > 40) times = 2;
        for (int i = 0; i < times; i++) into.Children.AddRange(item);
    }

    private IEnumerable<SketchNode> GridOf(DCall c, Env env)
    {
        var grid = Leaf(SketchKind.Grid, c, env, c.Name);
        string flat = Flat(c.Arg("gridDelegate")) + Flat(c.Arg("crossAxisCount"));
        var m = Regex.Match(flat, @"crossAxisCount:(?<n>\d+)");
        grid.Columns = m.Success ? int.Parse(m.Groups["n"].Value) : Number(c.Arg("crossAxisCount")) is double d ? (int)d : 2;
        grid.Columns = Math.Clamp(grid.Columns, 1, 6);
        if (c.Arg("children") is { } kids) grid.Children.AddRange(Nodes(kids, env));
        else if ((c.Arg("itemBuilder") ?? (c.Arg("delegate") as DCall)?.Arg("builder")) is DLambda b) Repeat(grid, Richest(b.Returns, env), Math.Max(4, grid.Columns * 2));
        if (grid.Children.Count == 0) return Array.Empty<SketchNode>();
        return new[] { grid };
    }

    private SketchNode TileOf(DCall c, Env env)
    {
        var tile = Leaf(SketchKind.Tile, c, env, c.Name);
        tile.Text = TextOf(c.Arg("title"), env) ?? Nodes(c.Arg("title"), env).SelectMany(n => n.Walk()).FirstOrDefault(n => n.Text != null)?.Text ?? "…";
        tile.Text2 = TextOf(c.Arg("subtitle"), env);
        var leading = c.Arg("leading") ?? c.Arg("secondary");
        if (leading is DCall { Name: "CircleAvatar" }) tile.Variant = "avatar";
        tile.Icon = IconOf(leading, env);
        if (c.Name is "SwitchListTile" or "CheckboxListTile" or "RadioListTile" || Flat(c.Arg("trailing")).Contains("Switch", StringComparison.Ordinal))
            tile.Variant = (tile.Variant ?? "") + "+toggle";
        else if (c.Arg("trailing") != null || c.Arg("onTap") != null) tile.Variant = (tile.Variant ?? "") + "+chevron";
        return tile;
    }

    private SketchNode ItemsOf(SketchKind kind, DCall c, Env env, string key)
    {
        var n = Leaf(kind, c, env, c.Name);
        if (c.Arg(key) is DList list)
            foreach (var item in list.Items)
            {
                var call = item as DCall;
                string label = call == null ? TextOf(item, env) ?? "…"
                    : TextOf(call.Arg("label") ?? call.Arg("text") ?? call.Arg("child") ?? call.Positional(0), env) ?? "…";
                n.Items.Add(label);
                n.ItemIcons.Add(call != null ? IconOf(call.Arg("icon"), env) : null);
            }
        else if (c.Arg(key) is DMap map)
            foreach (var (_, v) in map.Entries) { n.Items.Add(TextOf(v, env) ?? "…"); n.ItemIcons.Add(null); }
        if (n.Items.Count == 0) { n.Items.AddRange(new[] { "…", "…", "…" }); n.ItemIcons.AddRange(new string?[] { null, null, null }); }
        return n;
    }

    private SketchNode DialogOf(DCall c, Env env)
    {
        var d = Leaf(SketchKind.Dialog, c, env, c.Name, TextOf(c.Arg("title"), env));
        d.Children.AddRange(Nodes(c.Arg("content") ?? c.Arg("child") ?? c.Arg("children"), env));
        if (c.Arg("actions") is { } actions)
        {
            var row = Nodes(actions, env).ToList();
            if (row.Count > 0) d.Children.Add(Wrap(SketchKind.Row, row, actions, env, "actions"));
        }
        return d;
    }

    private SketchNode TableOf(DCall c, Env env)
    {
        var t = Leaf(SketchKind.Table, c, env, c.Name);
        if (c.Arg("columns") is DList cols)
            foreach (var col in cols.Items.OfType<DCall>()) t.Items.Add(TextOf(col.Arg("label"), env) ?? "…");
        t.Columns = Math.Max(1, t.Items.Count);
        return t;
    }
}
