using System.IO;
using CluadeX.Services.UiStudio;
using CluadeX.Services.UiStudio.Sketch;

namespace StudioTest;

/// <summary>
/// The text tool on text a widget gets through its constructor (through the REAL CluadeX.dll): <c>Text(label)</c> in a
/// widget class, <c>Text(widget.label)</c> in its State, <c>label ?? 'x'</c> are that class's parameter — a loop
/// variable, a closure's or a method's parameter of the same name is not; what one use of the widget passes is read
/// (a literal, data, nothing, the caller's own parameter passed on) and typed over at that use only, named or
/// positional, through a named constructor, in a const call or list (a string literal is a constant: the const
/// stays), never adding a const; the uses of a widget in a file, its constructors' declarations left out. The edited
/// files join FlutterEdits' `dart analyze` pass.
/// </summary>
internal static partial class Program
{
    const string ParamScreen = @"import 'package:flutter/material.dart';

class SummaryScreen extends StatelessWidget {
  const SummaryScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final total = DateTime.now().day;
    return Column(
      children: [
        const _Tag(label: 'ยอดขาย'),
        _Tag(label: 'วันที่ $total'),
        const _Split('ดูดวง', 0.5),
        _Split('ร้านค้า', total / 2, divider: true),
        _MiniBtn(label: 'รับช่วงต่อ', onTap: () {}),
        const _Pill(),
        const _Section(title: 'สรุปวันนี้'),
        const Row(children: [_Tag(label: ""ในแถว"")]),
        const _Tag.small('เล็ก'),
        const _Local(name: 'ไม่ใช้'),
      ],
    );
  }
}

class _Tag extends StatelessWidget {
  const _Tag({required this.label});
  const _Tag.small(this.label);

  final String label;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(8),
      child: Text(label),
    );
  }
}

class _Split extends StatelessWidget {
  const _Split(this.caption, this.ratio, {this.divider = false});

  final String caption;
  final double ratio;
  final bool divider;

  @override
  Widget build(BuildContext context) => Row(children: [Text(caption), Text('${ratio * 100}%')]);
}

class _MiniBtn extends StatefulWidget {
  const _MiniBtn({required this.label, required this.onTap});

  final String label;
  final VoidCallback onTap;

  @override
  State<_MiniBtn> createState() => _MiniBtnState();
}

class _MiniBtnState extends State<_MiniBtn> {
  @override
  Widget build(BuildContext context) => TextButton(onPressed: widget.onTap, child: Text(widget.label));
}

class _Pill extends StatelessWidget {
  const _Pill({this.mood = 'ปกติ', this.hint});

  final String mood;
  final String? hint;

  @override
  Widget build(BuildContext context) => Column(children: [Text(mood), Text(hint ?? 'ไม่มีคำใบ้')]);
}

class _Section extends StatelessWidget {
  const _Section({required this.title});

  final String title;

  @override
  Widget build(BuildContext context) => _Tag(label: title);
}

class _Local extends StatelessWidget {
  const _Local({required this.name});

  final String name;

  @override
  Widget build(BuildContext context) {
    const names = ['a', 'b'];
    return Column(children: [
      for (final name in names) Text(name),
      ...names.map((name) => Text(name)),
      _row('x'),
      Text(this.name),
    ]);
  }

  Widget _row(String name) => Text(name);
}
";

    /// <summary>The parameter the Text (or button) whose name is at <paramref name="marker"/> + <paramref name="skip"/> shows.</summary>
    static FlutterParamText? ParamAt(string marker, int skip = 0)
    {
        var (l, c) = At(ParamScreen, marker, skip);
        return FlutterCodeEdit.Inspect(ParamScreen, l, c)?.ParamText;
    }

    static string Show(FlutterParamText? p) => p == null ? "null"
        : $"{(p.Helper is { } h ? $"{h.Kind} {h.Name}() in " : "")}{p.Class}.{p.Param} [{string.Join(", ", p.Ctors.Select(kv => (kv.Key.Length == 0 ? "()" : kv.Key) + "=" + kv.Value))}]"
          + (p.Defaults.Count > 0 ? " defaults " + string.Join(", ", p.Defaults.Select(kv => (kv.Key.Length == 0 ? "()" : kv.Key) + "=" + (kv.Value ?? "(not a literal)"))) : "")
          + (p.Fallback != null ? " ?? " + p.Fallback : "");

    static FlutterArgText ArgAt(string marker, int skip, FlutterParamText p, string? text = null)
    {
        var (l, c) = At(text ?? ParamScreen, marker, skip);
        return FlutterCodeEdit.ArgText(text ?? ParamScreen, l, c, p);
    }

    static void ArgIs(string name, string marker, int skip, FlutterParamText? p, FlutterArgKind kind, string? literal = null)
    {
        if (p == null) { Check(name, false, "no parameter to follow"); return; }
        var a = ArgAt(marker, skip, p);
        Check(name, a.Kind == kind && a.Literal == literal, $"{a.Kind} '{a.Literal}'");
    }

    static FlutterEdit ArgEdit(string marker, int skip, FlutterParamText? p, string value, string? text = null)
    {
        if (p == null) return new FlutterEdit();
        var (l, c) = At(text ?? ParamScreen, marker, skip);
        return FlutterCodeEdit.SetArgText(text ?? ParamScreen, l, c, p, value);
    }

    /// <summary>studiotest texts &lt;flutter folder&gt; — every Text of lib/ that shows a parameter of its widget or of a
    /// helper function, and what each use of that widget (each call of that helper) passes it, a text passed on followed
    /// to where it comes from (READ-ONLY on the project): how much of an app's text the text tool types over where the
    /// widget is used, and that the reader copes with the app's real code.</summary>
    static int ParamReport(string root)
    {
        var files = Directory.EnumerateFiles(Path.Combine(root, "lib"), "*.dart", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".g.dart", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".freezed.dart", StringComparison.OrdinalIgnoreCase)).ToList();
        var source = files.ToDictionary(f => f, f => File.ReadAllText(f), StringComparer.OrdinalIgnoreCase);
        string Rel(string f) => Path.GetRelativePath(root, f).Replace('\\', '/');
        int texts = 0, literal = 0, param = 0, helper = 0, singleUse = 0;
        var widgetKinds = new Dictionary<string, int>();
        var helperKinds = new Dictionary<string, int>();
        var onKinds = new Dictionary<string, int>();
        var intoHelpers = new Dictionary<string, int>();
        // a widget's uses, a helper's calls (as the tool finds them from the code)
        List<(string File, int Line, int Column)> UsesOf(FlutterParamText p, string file) => p.Helper != null
            ? FlutterCodeEdit.HelperCalls(source[file], p).Select(x => (file, x.Line, x.Column)).ToList()
            : (p.Class.StartsWith('_') ? new List<string> { file } : files)
                .SelectMany(u => FlutterCodeEdit.CallsOf(source[u], p.Class).Select(x => (u, x.Line, x.Column))).ToList();
        string Name(FlutterParamText p) => (p.Helper != null ? p.Helper.Name + "()" : p.Class) + "." + p.Param;
        string KindOf(FlutterArgText a) => a.Kind == FlutterArgKind.Default && a.Literal != null && a.Addable ? "default-literal" : a.Kind.ToString().ToLowerInvariant();
        // a text passed on, followed to where it comes from (every use: the code alone can't say which one drew it)
        void Follow(FlutterParamText p, string file, int hops)
        {
            foreach (var u in UsesOf(p, file))
            {
                var a = FlutterCodeEdit.ArgText(source[u.File], u.Line, u.Column, p);
                if (a.Kind == FlutterArgKind.PassedOn && hops < 6) { Follow(a.PassedOn!, u.File, hops + 1); continue; }
                onKinds[KindOf(a)] = onKinds.GetValueOrDefault(KindOf(a)) + 1;
            }
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var f in files)
        {
            string text = source[f];
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\b(Text|SelectableText)\("))
            {
                int line = 1, start = 0;
                for (int i = 0; i < m.Index; i++) if (text[i] == '\n') { line++; start = i + 1; }
                var info = FlutterCodeEdit.Inspect(text, line, m.Index - start + 1);
                if (info == null) continue;
                texts++;
                if (info.CanText) literal++;
                if (info.ParamText is not { } p) continue;
                if (p.Helper != null) helper++; else param++;
                var uses = UsesOf(p, f);
                if (p.Helper == null && uses.Count == 1) singleUse++;
                var kinds = p.Helper != null ? helperKinds : widgetKinds;
                var said = new List<string>();
                foreach (var u in uses)
                {
                    var a = FlutterCodeEdit.ArgText(source[u.File], u.Line, u.Column, p);
                    kinds[KindOf(a)] = kinds.GetValueOrDefault(KindOf(a)) + 1;
                    if (a.Kind == FlutterArgKind.PassedOn) Follow(a.PassedOn!, u.File, 1);
                    if (a.PassedOn?.Helper != null) intoHelpers[Name(a.PassedOn)] = intoHelpers.GetValueOrDefault(Name(a.PassedOn)) + 1;
                    said.Add(a.Kind switch
                    {
                        FlutterArgKind.Literal => $"'{a.Literal}'",
                        FlutterArgKind.PassedOn => "→" + Name(a.PassedOn!),
                        FlutterArgKind.Translation => "l10n." + a.L10nKey,
                        FlutterArgKind.Default when a.Literal != null => $"default '{a.Literal}'{(a.Addable ? "" : " (gap)")}",
                        var k => k.ToString().ToLowerInvariant(),
                    });
                }
                Console.WriteLine($"  {Rel(f)}:{line} {(p.Helper != null ? $"{p.Helper.Kind.ToString().ToLowerInvariant()} " : "")}{Name(p)} · {uses.Count} {(p.Helper != null ? "call(s)" : "use(s)")}: {string.Join(", ", said.Take(6))}{(said.Count > 6 ? ", …" : "")}");
            }
        }
        string Line(Dictionary<string, int> k) => k.Count == 0 ? "-" : string.Join(", ", k.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));
        Console.WriteLine($"\n{files.Count} files · {texts} Text calls · {literal} literal (typed over in place) · {param} show a widget's parameter ({singleUse} of those widgets are used once) · {helper} a helper function's parameter");
        Console.WriteLine("their uses pass: " + Line(widgetKinds));
        Console.WriteLine("the helpers' calls pass: " + Line(helperKinds));
        Console.WriteLine("passed on into a helper's parameter: " + Line(intoHelpers));
        Console.WriteLine("passed on, followed to where it comes from: " + Line(onKinds) + $" · read in {sw.Elapsed.TotalSeconds:0.0}s");
        return 0;
    }

    /// <summary>studiotest picks &lt;flutter folder&gt; &lt;out folder&gt; [route…] — draw screens with `flutter test` (the app's own
    /// code runs in Flutter's test renderer: no device, no network; point it at a COPY of an app that must stay
    /// untouched — the renderer writes its test into the app's .dart_tool), then for every Text box whose text a helper
    /// function passes in (directly, or through a widget's use inside a helper), follow it as the text tool does — a
    /// widget's use by the nearest box of that class around it, a helper's call by <see cref="FlutterCodeEdit.HelperUse"/>
    /// — and say what would be typed over, or why not.</summary>
    static int HelperPickReport(string root, string outDir, HashSet<string> wanted)
    {
        CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun = false;
        var found = CluadeX.Services.UiStudio.Pages.PageCatalog.Discover(StudioProject.Detect(root), "th");
        var pages = found.Pages.Where(p => wanted.Count == 0 || wanted.Contains(p.Route ?? "") || wanted.Contains(p.Detail ?? "")).ToList();
        var run = FlutterRenderer.RunAsync(found.Flutter!, pages, outDir, "th", CancellationToken.None, line => { if (line != "·") Console.WriteLine("  | " + line); }).GetAwaiter().GetResult();
        Console.WriteLine($"drawn: ok={run.Ok} {run.Screens.Count(s => s.PngPath != null)}/{pages.Count} screens in {run.Took.TotalSeconds:0}s{(run.Error != null ? " ERROR " + run.Error : "")}"
                          + (run.CompileError != null ? " COMPILE " + run.CompileError : "") + " · not drawn: " + string.Join(", ", run.Screens.Zip(pages).Where(x => x.First.PngPath == null).Select(x => $"{x.Second.Route ?? x.Second.Detail}({x.First.Skipped})")));
        var texts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? Read(string f) => texts.TryGetValue(f, out var t) ? t : texts[f] = File.Exists(f) ? File.ReadAllText(f) : null;
        string Rel(string f) => Path.GetRelativePath(root, f).Replace('\\', '/');
        var tally = new Dictionary<string, int>();
        foreach (var (s, pg) in run.Screens.Zip(pages))
        {
            var boxes = s.Boxes;
            var byId = boxes.Where(b => b.Id >= 0).GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
            WidgetBox? Parent(WidgetBox b) => b.Parent >= 0 && byId.TryGetValue(b.Parent, out var p) ? p : null;
            foreach (var b in boxes.Where(b => b.Widget.StartsWith("Text", StringComparison.Ordinal)))
            {
                if (Read(b.File) is not { } text || FlutterCodeEdit.Inspect(text, b.Line, b.Column)?.ParamText is not { } p) continue;
                var chain = new List<string>();
                bool viaHelper = false;
                string file = b.File, result = "";
                WidgetBox? from = b;
                for (int hop = 0; hop < 6 && result.Length == 0; hop++)
                {
                    (string File, int Line, int Column)? use = null;
                    if (p.Helper is { } h)
                    {
                        viaHelper = true;
                        chain.Add(h.Name + "()");
                        var u = FlutterCodeEdit.HelperUse(Read(file) ?? "", file, p, boxes, from);
                        if (u.Line == 0) { result = $"refused ({u.Why}, {u.Calls} calls)"; break; }
                        use = (file, u.Line, u.Column);
                        from = u.Anchor ?? from;
                    }
                    else
                    {
                        chain.Add(p.Class);
                        WidgetBox? around = null;
                        for (var x = from == null ? null : Parent(from); x != null && around == null; x = Parent(x))
                            if (x.Widget.Split('-')[0].Split('<')[0].Trim() == p.Class) around = x;
                        if (around == null) { result = "no box of the widget around it"; break; }
                        use = (around.File, around.Line, around.Column);
                        from = around;
                    }
                    var a = FlutterCodeEdit.ArgText(Read(use.Value.File) ?? "", use.Value.Line, use.Value.Column, p);
                    string where = $"{Rel(use.Value.File)}:{use.Value.Line}";
                    switch (a.Kind)
                    {
                        case FlutterArgKind.PassedOn: p = a.PassedOn!; file = use.Value.File; continue;
                        case FlutterArgKind.Literal: result = $"'{a.Literal}' at {where}"; break;
                        case FlutterArgKind.Default when a.Literal != null && a.Addable: result = $"default '{a.Literal}' — added at {where}"; break;
                        default: result = $"{a.Kind.ToString().ToLowerInvariant()} at {where}"; break;
                    }
                }
                if (!viaHelper) continue;
                string kind = result.StartsWith('\'') ? "typed over" : result.StartsWith("refused") ? result[..result.IndexOf(',')] + ")" : result.Split(' ')[0];
                tally[kind] = tally.GetValueOrDefault(kind) + 1;
                Console.WriteLine($"  {pg.Route ?? pg.Detail}: {Rel(b.File)}:{b.Line} @{b.X:0},{b.Y:0} {string.Join(" → ", chain)} · {result}");
            }
        }
        Console.WriteLine("\nTexts drawn through a helper: " + (tally.Count == 0 ? "none" : string.Join(", ", tally.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))));
        return run.Ok ? 0 : 1;
    }

    static void ParamTexts()
    {
        Console.WriteLine("UI Studio: text a widget gets through its constructor, typed over where the widget is used");
        Compiled.Add(("params: the fixture itself", ParamScreen));

        // ── which texts are a parameter of their widget ──
        var tag = ParamAt("child: Text(label)", 7);
        Check("param: Text(label) in _Tag — named in _Tag(), the first positional one in _Tag.small()",
              tag is { Class: "_Tag", Param: "label" } && tag.Ctors.Count == 2 && tag.Ctors[""] == -1 && tag.Ctors["small"] == 0, Show(tag));
        var split = ParamAt("[Text(caption)", 1);
        Check("param: a positional parameter (_Split(this.caption, this.ratio, {…}))", split is { Class: "_Split", Param: "caption" } && split.Ctors.Count == 1 && split.Ctors[""] == 0, Show(split));
        var btn = ParamAt("Text(widget.label)");
        Check("param: widget.label in a State class is its widget's parameter", btn is { Class: "_MiniBtn", Param: "label" } && btn.Ctors[""] == -1, Show(btn));
        Check("param: a button's label Text the same way", ParamAt("TextButton(") is { Class: "_MiniBtn", Param: "label" }, Show(ParamAt("TextButton(")));
        var mood = ParamAt("[Text(mood)", 1);
        var hint = ParamAt("Text(hint ??");
        Check("param: label ?? 'x' — what the use passes is what shows", hint is { Class: "_Pill", Param: "hint" } && mood is { Param: "mood" }, Show(hint));
        Check("param: this.name too", ParamAt("Text(this.name)") is { Class: "_Local", Param: "name" }, Show(ParamAt("Text(this.name)")));
        Check("not a param: a loop variable of the same name", ParamAt("names) Text(name)", 7) == null, Show(ParamAt("names) Text(name)", 7)));
        Check("not a param: a closure's parameter of the same name", ParamAt("(name) => Text(name)", 11) == null);
        var rowOfLocal = ParamAt("_row(String name) => Text(name)", 21);
        Check("param: a method's parameter of the same name is the method's, not the field (followed to its call)",
              rowOfLocal is { Class: "_Local", Param: "name", Helper: { Name: "_row", Kind: FlutterHelperKind.Method } } && rowOfLocal.Ctors[""] == 0, Show(rowOfLocal));
        Check("not a param: text built from a parameter", ParamAt("Text('${ratio", 0) == null);
        var (tl, tc) = At(ParamScreen, "child: Text(label)", 7);
        Check("inspect: the Text itself is not typed over in its widget's code (its use is)",
              FlutterCodeEdit.Inspect(ParamScreen, tl, tc) is { CanText: false, TextNote: "studio.fedit.text.data", ParamText: not null });
        Check("inspect: SetText on the widget's code still refuses it", FlutterCodeEdit.SetText(ParamScreen, tl, tc, "x").Error == "studio.fedit.text.data");

        // ── what one use passes ──
        ArgIs("use: a named literal", "const _Tag(label: 'ยอดขาย')", 6, tag, FlutterArgKind.Literal, "ยอดขาย");
        ArgIs("use: an interpolation is data", "_Tag(label: 'วันที่", 0, tag, FlutterArgKind.Data);
        ArgIs("use: a positional literal", "const _Split('ดูดวง'", 6, split, FlutterArgKind.Literal, "ดูดวง");
        ArgIs("use: … of the other use", "_Split('ร้านค้า'", 0, split, FlutterArgKind.Literal, "ร้านค้า");
        ArgIs("use: the widget of a State class", "_MiniBtn(label:", 0, btn, FlutterArgKind.Literal, "รับช่วงต่อ");
        ArgIs("use: left out — no default, so the Text's own ?? literal shows", "const _Pill()", 6, hint, FlutterArgKind.Default, "ไม่มีคำใบ้");
        ArgIs("use: left out — the default value written in the constructor shows", "const _Pill()", 6, mood, FlutterArgKind.Default, "ปกติ");
        ArgIs("use: a named constructor (the inspector points at its name)", "_Tag.small(", 5, tag, FlutterArgKind.Literal, "เล็ก");
        ArgIs("use: … or at its class", "_Tag.small(", 0, tag, FlutterArgKind.Literal, "เล็ก");
        ArgIs("use: in a const list, double-quoted", "_Tag(label: \"ในแถว\")", 0, tag, FlutterArgKind.Literal, "ในแถว");
        var on = tag != null ? ArgAt("_Tag(label: title)", 0, tag) : new FlutterArgText();
        Check("use: passed on — the caller's own parameter (_Section's title)", on is { Kind: FlutterArgKind.PassedOn, PassedOn: { Class: "_Section", Param: "title" } }, $"{on.Kind} {Show(on.PassedOn)}");
        ArgIs("use: … followed to where _Section is used", "const _Section(", 6, on.PassedOn, FlutterArgKind.Literal, "สรุปวันนี้");
        ArgIs("use: no call of that widget there", "const _Split('ดูดวง'", 6, tag, FlutterArgKind.Stale);
        string l10n = ParamScreen.Replace("const _Tag(label: 'ยอดขาย')", "_Tag(label: AppL10n.of(context).salesTitle)");
        var tr = tag != null ? ArgAt("_Tag(label: AppL10n", 0, tag, l10n) : new FlutterArgText();
        Check("use: a translation — its key (typed over in the .arb file)", tr is { Kind: FlutterArgKind.Translation, L10nKey: "salesTitle", L10nCall: false }, $"{tr.Kind} {tr.L10nKey}");

        // ── the uses of a widget in a file ──
        var uses = FlutterCodeEdit.CallsOf(ParamScreen, "_Tag");
        var want = new[] { "const _Tag(label: 'ยอดขาย')", "_Tag(label: 'วันที่", "_Tag(label: \"ในแถว\")", "_Tag.small('เล็ก')", "_Tag(label: title)" }
            .Select(m => At(ParamScreen, m, m.StartsWith("const ") ? 6 : m.StartsWith("_Tag.small") ? 5 : 0)).ToList();
        Check("uses: every call of _Tag, at the place the inspector gives (its constructors' declarations are not uses)", uses.SequenceEqual(want),
              string.Join(" ", uses.Select(u => $"{u.Line}:{u.Column}")) + " want " + string.Join(" ", want.Select(u => $"{u.Line}:{u.Col}")));
        Check("uses: a widget used once / twice / nowhere", FlutterCodeEdit.CallsOf(ParamScreen, "_Section").Count == 1 && FlutterCodeEdit.CallsOf(ParamScreen, "_Split").Count == 2
              && FlutterCodeEdit.CallsOf(ParamScreen, "_Nowhere").Count == 0);

        // ── typing over it at that use ──
        Edited("write: a named literal in a const call — a string literal is a constant, the const stays", ParamScreen,
               ArgEdit("const _Tag(label: 'ยอดขาย')", 6, tag, "ยอดรวม"), "const _Tag(label: 'ยอดขาย')", "const _Tag(label: 'ยอดรวม')");
        Edited("write: a positional literal, the other arguments untouched", ParamScreen,
               ArgEdit("const _Split('ดูดวง'", 6, split, "ดวงชะตา"), "const _Split('ดูดวง', 0.5)", "const _Split('ดวงชะตา', 0.5)");
        Edited("write: the other use of the same widget — only that one changes", ParamScreen,
               ArgEdit("_Split('ร้านค้า'", 0, split, "ตลาด"), "_Split('ร้านค้า', total / 2, divider: true)", "_Split('ตลาด', total / 2, divider: true)");
        Edited("write: a call that isn't const stays so (no const is added)", ParamScreen,
               ArgEdit("_MiniBtn(label:", 0, btn, "ส่งต่อ"), "_MiniBtn(label: 'รับช่วงต่อ', onTap: () {})", "_MiniBtn(label: 'ส่งต่อ', onTap: () {})");
        Edited("write: inside a const list, in the quotes it was written with", ParamScreen,
               ArgEdit("_Tag(label: \"ในแถว\")", 0, tag, "แถวใหม่"), "const Row(children: [_Tag(label: \"ในแถว\")])", "const Row(children: [_Tag(label: \"แถวใหม่\")])");
        Edited("write: quotes, $ and new lines are escaped", ParamScreen,
               ArgEdit("const _Tag(label: 'ยอดขาย')", 6, tag, "it's $5\nok"), "const _Tag(label: 'ยอดขาย')", @"const _Tag(label: 'it\'s \$5\nok')");
        Edited("write: a named constructor's positional argument", ParamScreen,
               ArgEdit("_Tag.small(", 5, tag, "ย่อ"), "const _Tag.small('เล็ก')", "const _Tag.small('ย่อ')");
        Edited("write: the use further up when the text is passed on", ParamScreen,
               ArgEdit("const _Section(", 6, on.PassedOn, "สรุปเมื่อวาน"), "const _Section(title: 'สรุปวันนี้')", "const _Section(title: 'สรุปเมื่อวาน')");
        var focus = ArgEdit("const _Tag(label: 'ยอดขาย')", 6, tag, "ยอดรวม");
        var site = At(ParamScreen, "const _Tag(label: 'ยอดขาย')", 6);
        Check("write: the edit points at the use it changed", focus.FocusLine == site.Line && focus.FocusColumn == site.Col, $"{focus.FocusLine}:{focus.FocusColumn}");
        var below = At(ParamScreen, "child: Text(label)", 7);
        Check("write: the widget's own Text keeps its place (its box follows)", focus.Map(below.Line, below.Col) == (below.Line, below.Col));
        string crlf = ParamScreen.Replace("\n", "\r\n");
        var ce = ArgEdit("const _Tag(label: 'ยอดขาย')", 6, tag, "ยอดรวม", crlf);
        Check("write: a CRLF file keeps its line breaks", ce.Ok && ce.Text == crlf.Replace("const _Tag(label: 'ยอดขาย')", "const _Tag(label: 'ยอดรวม')"));

        // ── refusals ──
        Check("refuse: the use builds it from data", ArgEdit("_Tag(label: 'วันที่", 0, tag, "x").Error == "studio.fedit.text.data");
        Edited("write: a use that leaves it out gets the argument (the ?? fallback showed)", ParamScreen,
               ArgEdit("const _Pill()", 6, hint, "คำใบ้ใหม่"), "const _Pill()", "const _Pill(hint: 'คำใบ้ใหม่')");
        Check("refuse: no use of that widget at that place", ArgEdit("const _Split('ดูดวง'", 6, tag, "x").Error == "studio.fedit.stale");
        Check("refuse: a translation at the use is not typed over in the Dart code", ArgEdit("_Tag(label: AppL10n", 0, tag, "x", l10n).Error == "studio.fedit.text.l10n");
        var (ul, uc) = At(ParamScreen, "const _Tag(label: 'ยอดขาย')", 6);
        Check("refuse: the use says something else by now than the tool showed (changed meanwhile) — nothing is overwritten",
              tag != null && FlutterCodeEdit.SetArgText(ParamScreen, ul, uc, tag, "x", expect: "ยอดเดิม").Error == "studio.fedit.changedMeanwhile");
        Edited("write: … and it is written when the use still says what the tool showed", ParamScreen,
               tag != null ? FlutterCodeEdit.SetArgText(ParamScreen, ul, uc, tag, "ยอดสุทธิ", expect: "ยอดขาย") : new FlutterEdit(), "const _Tag(label: 'ยอดขาย')", "const _Tag(label: 'ยอดสุทธิ')");

        // ── the method's parameter (not the field) is written at its one call ──
        if (rowOfLocal != null)
        {
            var lc = FlutterCodeEdit.HelperCalls(ParamScreen, rowOfLocal);
            Check("helper: _Local's _row is called once, with 'x'", lc.Count == 1 && FlutterCodeEdit.ArgText(ParamScreen, lc[0].Line, lc[0].Column, rowOfLocal) is { Kind: FlutterArgKind.Literal, Literal: "x" },
                  string.Join(" ", lc.Select(u => $"{u.Line}:{u.Column}")));
            Edited("write: … typed over at that call", ParamScreen, lc.Count == 1 ? FlutterCodeEdit.SetArgText(ParamScreen, lc[0].Line, lc[0].Column, rowOfLocal, "y") : new FlutterEdit(),
                   "_row('x')", "_row('y')");
        }

        DefaultTexts();
        HelperTexts();
    }

    // ═══════════════════════════ a use that leaves the text out: the default shows ═══════════════════════════

    const string DefaultScreen = @"import 'package:flutter/material.dart';

class DefaultScreen extends StatelessWidget {
  const DefaultScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final note = DateTime.now().toString();
    return Column(
      children: [
        const _Mood(),
        _Mood(hint: note),
        const _Mood(
          hint: 'บอกใบ้',
        ),
        const _Chip(),
        const _Chip('มีแล้ว'),
        const _Pair(),
        const _Pair('ก่อน'),
        const _Pair(
          'บรรทัดแรก',
        ),
        const _Tone(),
      ],
    );
  }
}

class _Mood extends StatelessWidget {
  const _Mood({this.mood = 'ปกติ', this.hint});

  final String mood;
  final String? hint;

  @override
  Widget build(BuildContext context) => Column(children: [Text(mood), Text(hint ?? 'ไม่มีคำใบ้')]);
}

class _Chip extends StatelessWidget {
  const _Chip([this.label = 'ชิป']);

  final String label;

  @override
  Widget build(BuildContext context) => Text(label);
}

class _Pair extends StatelessWidget {
  const _Pair([this.first = 'หนึ่ง', this.second = 'สอง']);

  final String first;
  final String second;

  @override
  Widget build(BuildContext context) => Row(children: [Text(first), Text(second)]);
}

const _toneText = 'เงียบ';

class _Tone extends StatelessWidget {
  const _Tone({this.tone = _toneText});

  final String tone;

  @override
  Widget build(BuildContext context) => Text(tone);
}
";

    static FlutterParamText? DParam(string marker, int skip = 0)
    {
        var (l, c) = At(DefaultScreen, marker, skip);
        return FlutterCodeEdit.Inspect(DefaultScreen, l, c)?.ParamText;
    }

    static void DefaultIs(string name, string marker, int skip, FlutterParamText? p, string? shown, bool addable)
    {
        if (p == null) { Check(name, false, "no parameter to follow"); return; }
        var (l, c) = At(DefaultScreen, marker, skip);
        var a = FlutterCodeEdit.ArgText(DefaultScreen, l, c, p);
        Check(name, a.Kind == FlutterArgKind.Default && a.Literal == shown && a.Addable == addable, $"{a.Kind} '{a.Literal}' addable={a.Addable}");
    }

    static FlutterEdit DEdit(string marker, int skip, FlutterParamText? p, string value) => ArgEdit(marker, skip, p, value, DefaultScreen);

    /// <summary>A use that leaves a text parameter out shows its default: the constructor's default when it is a plain
    /// literal (<c>this.mood = 'ปกติ'</c>, <c>[this.label = 'ชิป']</c>), else the Text's own <c>?? 'x'</c>. Typing over
    /// it writes the argument at that use only — named where SetNamed puts arguments, positional right after the ones
    /// before it (never when those are left out too) — keeping a const call const and adding no const.</summary>
    static void DefaultTexts()
    {
        Console.WriteLine("UI Studio: a use that leaves the text out — its default, typed over by adding the argument at that use");
        Compiled.Add(("defaults: the fixture itself", DefaultScreen));
        var mood = DParam("[Text(mood)", 1);
        var hint = DParam("Text(hint ??");
        var chip = DParam("=> Text(label)", 3);
        var first = DParam("[Text(first)", 1);
        var second = DParam("Text(second)");
        var tone = DParam("=> Text(tone)", 3);
        Check("default: this.mood = 'ปกติ' is read", mood is { Param: "mood" } && mood.Defaults.TryGetValue("", out var md) && md == "ปกติ", Show(mood));
        Check("default: none — the Text's ?? literal is what shows", hint is { Param: "hint", Fallback: "ไม่มีคำใบ้" } && !hint.Defaults.ContainsKey(""), Show(hint));
        Check("default: an optional positional one [this.label = 'ชิป']", chip is { Class: "_Chip" } && chip.Ctors[""] == 0 && chip.Defaults[""] == "ชิป", Show(chip));
        Check("default: the second optional positional one", second is { Class: "_Pair" } && second.Ctors[""] == 1 && second.Defaults[""] == "สอง", Show(second));
        Check("default: one that isn't a plain literal is known, with no text", tone is { } && tone.Defaults.ContainsKey("") && tone.Defaults[""] == null, Show(tone));

        // ── what each use shows ──
        DefaultIs("use: const _Mood() shows mood's default", "const _Mood()", 6, mood, "ปกติ", addable: true);
        DefaultIs("use: … and hint's ?? literal", "const _Mood()", 6, hint, "ไม่มีคำใบ้", addable: true);
        DefaultIs("use: an optional positional one left out (nothing before it)", "const _Chip()", 6, chip, "ชิป", addable: true);
        DefaultIs("use: the second positional one left out with the first — it can't be added on its own", "const _Pair()", 6, second, "สอง", addable: false);
        DefaultIs("use: … the first one can", "const _Pair()", 6, first, "หนึ่ง", addable: true);
        DefaultIs("use: the second one, after a first that is passed", "const _Pair('ก่อน')", 6, second, "สอง", addable: true);
        DefaultIs("use: a default that isn't a plain literal has no text to show", "const _Tone()", 6, tone, null, addable: true);
        var (pl, pc) = At(DefaultScreen, "const _Chip('มีแล้ว')", 6);
        Check("use: a positional one that is passed is its literal", chip != null && FlutterCodeEdit.ArgText(DefaultScreen, pl, pc, chip) is { Kind: FlutterArgKind.Literal, Literal: "มีแล้ว" });

        // ── writing it at that use ──
        Edited("add: a named argument into an empty const call — the const stays", DefaultScreen, DEdit("const _Mood()", 6, mood, "สบายดี"), "const _Mood()", "const _Mood(mood: 'สบายดี')");
        Edited("add: after the other arguments of a call that isn't const — no const added, even with ( ) in the text", DefaultScreen,
               DEdit("_Mood(hint: note)", 0, mood, "ยอด (บาท)"), "_Mood(hint: note)", "_Mood(hint: note, mood: 'ยอด (บาท)')");
        Edited("add: on its own line when the arguments are written one per line", DefaultScreen, DEdit("const _Mood(\n", 6, mood, "เหนื่อย"),
               "const _Mood(\n          hint: 'บอกใบ้',\n        ),", "const _Mood(\n          hint: 'บอกใบ้',\n          mood: 'เหนื่อย',\n        ),");
        Edited("add: the ?? fallback's parameter", DefaultScreen, DEdit("const _Mood()", 6, hint, "ใบ้ใหม่"), "const _Mood()", "const _Mood(hint: 'ใบ้ใหม่')");
        Edited("add: an optional positional one", DefaultScreen, DEdit("const _Chip()", 6, chip, "ป้าย"), "const _Chip()", "const _Chip('ป้าย')");
        Edited("add: a positional one right after the one before it", DefaultScreen, DEdit("const _Pair('ก่อน')", 6, second, "หลัง"), "const _Pair('ก่อน')", "const _Pair('ก่อน', 'หลัง')");
        Edited("add: … on its own line in a call written one per line", DefaultScreen, DEdit("const _Pair(\n", 6, second, "บรรทัดสอง"),
               "const _Pair(\n          'บรรทัดแรก',\n        ),", "const _Pair(\n          'บรรทัดแรก',\n          'บรรทัดสอง',\n        ),");
        Edited("add: quotes, $ and new lines are escaped", DefaultScreen, DEdit("const _Chip()", 6, chip, "it's $5\nok"), "const _Chip()", @"const _Chip('it\'s \$5\nok')");
        Check("refuse: the second positional one while the first is left out too", DEdit("const _Pair()", 6, second, "x").Error == "studio.fedit.stale");
        var (ml, mc) = At(DefaultScreen, "const _Mood()", 6);
        Check("refuse: a use that doesn't show the default the tool showed any more", mood != null && FlutterCodeEdit.SetArgText(DefaultScreen, ml, mc, mood, "x", expect: "อื่น").Error == "studio.fedit.changedMeanwhile");
        var (cl, cc) = At(DefaultScreen, "const _Chip()", 6);
        Check("refuse: a use whose literal went away meanwhile isn't given one", chip != null && FlutterCodeEdit.SetArgText(DefaultScreen, cl, cc, chip, "x", expect: "มีแล้ว").Error == "studio.fedit.changedMeanwhile");
        Edited("add: … and it is added when the use still shows what the tool showed", DefaultScreen,
               mood != null ? FlutterCodeEdit.SetArgText(DefaultScreen, ml, mc, mood, "ร่าเริง", expect: "ปกติ") : new FlutterEdit(), "const _Mood()", "const _Mood(mood: 'ร่าเริง')");
        var crlf = DefaultScreen.Replace("\n", "\r\n");
        var (rl, rc) = At(crlf, "const _Pair(\r\n", 6);
        var ce = second != null ? FlutterCodeEdit.SetArgText(crlf, rl, rc, second, "สอง") : new FlutterEdit();
        Check("add: a CRLF file keeps its line breaks", ce.Ok && ce.Text == crlf.Replace("          'บรรทัดแรก',\r\n", "          'บรรทัดแรก',\r\n          'สอง',\r\n"));
    }

    // ═══════════════════════════ a parameter of a helper function ═══════════════════════════

    const string HelperScreen = @"import 'package:flutter/material.dart';

class HelperScreen extends StatelessWidget {
  const HelperScreen({super.key, this.items = const []});

  final List<String> items;

  @override
  Widget build(BuildContext context) {
    final total = DateTime.now().day;
    Widget line(IconData icon, String text) => Padding(
          padding: const EdgeInsets.only(top: 8),
          child: Row(children: [
            Icon(icon, size: 14),
            const SizedBox(width: 8),
            Expanded(child: Text(text)),
          ]),
        );
    Widget tile({required String title, String value = '-'}) => Card(
          child: Column(children: [Text(title), Text(value)]),
        );
    return Column(
      children: [
        _row('ชื่อ'),
        _row('อายุ $total'),
        line(Icons.store, 'ยอดขาย'),
        line(Icons.scale, 'เฉลี่ย'),
        const SizedBox(height: 8),
        line(Icons.money, 'ค่าคอม'),
        Row(children: [
          Expanded(child: tile(title: 'AI Pool', value: '$total คีย์')),
          Expanded(child: tile(title: 'LINE push')),
        ]),
        _section('ข้อมูล'),
        for (final i in items) _row(i),
        this._pill('ไม่มีสิทธิ์'),
        _footer('ท้ายหน้า'),
      ],
    );
  }

  Widget _row(String label) => Text(label);

  Widget _section(String title) => _Header(caption: title);

  Widget _pill(String label) => Chip(label: Text(label));
}

class _Header extends StatelessWidget {
  const _Header({required this.caption});

  final String caption;

  @override
  Widget build(BuildContext context) => Text(caption);
}

Widget _footer(String note) => Text(note);

Widget publicFooter(String note) => Text(note);
";

    const string HelperFile = @"C:\cx-fixture\lib\helper_screen.dart";

    static FlutterParamText? HParam(string marker, int skip = 0, string? text = null)
    {
        string t = text ?? HelperScreen;
        var (l, c) = At(t, marker, skip);
        return FlutterCodeEdit.Inspect(t, l, c)?.ParamText;
    }

    static FlutterArgText HArgText(string marker, FlutterParamText p)
    {
        var (l, c) = At(HelperScreen, marker);
        return FlutterCodeEdit.ArgText(HelperScreen, l, c, p);
    }

    static void HArg(string name, string marker, FlutterParamText? p, FlutterArgKind kind, string? literal = null)
    {
        if (p == null) { Check(name, false, "no parameter to follow"); return; }
        var a = HArgText(marker, p);
        Check(name, a.Kind == kind && a.Literal == literal, $"{a.Kind} '{a.Literal}'");
    }

    static FlutterEdit HEdit(string marker, FlutterParamText? p, string value)
    {
        if (p == null) return new FlutterEdit();
        var (l, c) = At(HelperScreen, marker);
        return FlutterCodeEdit.SetArgText(HelperScreen, l, c, p, value);
    }

    static void CallsAre(string name, FlutterParamText? p, params string[] markers)
    {
        if (p == null) { Check(name, false, "no parameter"); return; }
        var got = FlutterCodeEdit.HelperCalls(HelperScreen, p);
        var want = markers.Select(m => At(HelperScreen, m)).ToList();
        Check(name, got.SequenceEqual(want), string.Join(" ", got.Select(u => $"{u.Line}:{u.Column}")) + " want " + string.Join(" ", want.Select(u => $"{u.Line}:{u.Col}")));
    }

    /// <summary>A Text that shows a parameter of a helper function — a method (<c>Widget _row(String label) =&gt; Text(label)</c>),
    /// a function declared in build (<c>Widget line(…)</c>, the shape thaipromptadminapp uses), a private function of
    /// the file — is followed to the helper's calls: what each call passes, typed over at that call (positional, named,
    /// a named one it left out, <c>this._pill(…)</c>, and a widget's use that passes a helper's parameter on). Not
    /// followed: a public top-level function (its calls may be in any file), one a loop variable inside it hides, a
    /// name declared twice in the class. Which call drew a picked widget is told from the inspector's boxes
    /// (<see cref="HelperPicks"/>).</summary>
    static void HelperTexts()
    {
        Console.WriteLine("UI Studio: text a helper function gets through its parameter, typed over at the call that drew it");
        Compiled.Add(("helpers: the fixture itself", HelperScreen));
        var row = HParam("=> Text(label)", 3);
        var line = HParam("Expanded(child: Text(text))", 16);
        var title = HParam("[Text(title)", 1);
        var value = HParam("Text(value)");
        var caption = HParam("=> Text(caption)", 3);
        var pill = HParam("Chip(label: Text(label))");
        var footer = HParam("_footer(String note) => Text(note)", 24);
        var pub = HParam("publicFooter(String note) => Text(note)", 29);
        Check("helper: a method's parameter (_row(String label) => Text(label))",
              row is { Class: "HelperScreen", Param: "label", Helper: { Name: "_row", Kind: FlutterHelperKind.Method } } && row.Ctors[""] == 0, Show(row));
        Check("helper: a function declared in build, its second positional parameter", line is { Param: "text", Helper: { Name: "line", Kind: FlutterHelperKind.Local } } && line.Ctors[""] == 1, Show(line));
        Check("helper: a named parameter", title is { Param: "title", Helper.Name: "tile" } && title.Ctors[""] == -1, Show(title));
        Check("helper: … one with a default value", value is { Param: "value", Helper.Name: "tile" } && value.Defaults[""] == "-", Show(value));
        Check("helper: a chip's label Text in a method", pill is { Param: "label", Helper: { Name: "_pill", Kind: FlutterHelperKind.Method } }, Show(pill));
        Check("helper: a private function of the file", footer is { Class: "", Param: "note", Helper: { Name: "_footer", Kind: FlutterHelperKind.TopLevel } }, Show(footer));
        Check("not followed: a public top-level function (its calls may be in any file)", pub == null, Show(pub));
        Check("widget parameter as before: _Header.caption", caption is { Class: "_Header", Param: "caption", Helper: null }, Show(caption));
        var (tl, tc) = At(HelperScreen, "=> Text(label)", 3);
        Check("inspect: the Text in the helper is not typed over in the helper's code (its call is)",
              FlutterCodeEdit.Inspect(HelperScreen, tl, tc) is { CanText: false, TextNote: "studio.fedit.text.data", ParamText.Helper: not null });

        // ── where it is called ──
        CallsAre("calls: a method's, its own declaration left out", row, "_row('ชื่อ')", "_row('อายุ", "_row(i)");
        CallsAre("calls: a local function's, in its block", line, "line(Icons.store", "line(Icons.scale", "line(Icons.money");
        CallsAre("calls: named arguments", title, "tile(title: 'AI Pool'", "tile(title: 'LINE push'");
        CallsAre("calls: this._pill(…)", pill, "_pill('ไม่มีสิทธิ์')");
        CallsAre("calls: a private top-level function's, in its file", footer, "_footer('ท้ายหน้า')");

        // ── what each call passes ──
        HArg("arg: a literal", "_row('ชื่อ')", row, FlutterArgKind.Literal, "ชื่อ");
        HArg("arg: an interpolation is data", "_row('อายุ", row, FlutterArgKind.Data);
        HArg("arg: a loop variable is data", "_row(i)", row, FlutterArgKind.Data);
        HArg("arg: the second positional one", "line(Icons.scale", line, FlutterArgKind.Literal, "เฉลี่ย");
        HArg("arg: named", "tile(title: 'AI Pool'", title, FlutterArgKind.Literal, "AI Pool");
        HArg("arg: a named one the call leaves out shows its default", "tile(title: 'LINE push'", value, FlutterArgKind.Default, "-");
        HArg("arg: this._pill(…)", "_pill('ไม่มีสิทธิ์')", pill, FlutterArgKind.Literal, "ไม่มีสิทธิ์");
        var sect = caption != null ? HArgText("_Header(caption: title)", caption) : new FlutterArgText();
        Check("arg: a widget's use that passes a helper's parameter — passed on to the helper's call",
              sect is { Kind: FlutterArgKind.PassedOn, PassedOn.Helper.Name: "_section" }, $"{sect.Kind} {Show(sect.PassedOn)}");
        HArg("arg: … where that call passes a literal", "_section('ข้อมูล')", sect.PassedOn, FlutterArgKind.Literal, "ข้อมูล");

        // ── typing over it at that call ──
        Edited("write: a method's call", HelperScreen, HEdit("_row('ชื่อ')", row, "ชื่อเต็ม"), "_row('ชื่อ')", "_row('ชื่อเต็ม')");
        Edited("write: a local function's call, the other arguments untouched", HelperScreen, HEdit("line(Icons.scale", line, "ค่าเฉลี่ย"),
               "line(Icons.scale, 'เฉลี่ย')", "line(Icons.scale, 'ค่าเฉลี่ย')");
        Edited("write: a call that leaves a named one out gets it", HelperScreen, HEdit("tile(title: 'LINE push'", value, "ไม่มีข้อมูล"),
               "tile(title: 'LINE push')", "tile(title: 'LINE push', value: 'ไม่มีข้อมูล')");
        Edited("write: this._pill(…)", HelperScreen, HEdit("_pill('ไม่มีสิทธิ์')", pill, "ห้ามแก้"), "this._pill('ไม่มีสิทธิ์')", "this._pill('ห้ามแก้')");
        Edited("write: the helper's call further up, when a widget's use passes its parameter on", HelperScreen, HEdit("_section('ข้อมูล')", sect.PassedOn, "ข้อมูลผู้ใช้"),
               "_section('ข้อมูล')", "_section('ข้อมูลผู้ใช้')");
        Edited("write: a private top-level function's call", HelperScreen, HEdit("_footer('ท้ายหน้า')", footer, "ปิดท้าย"), "_footer('ท้ายหน้า')", "_footer('ปิดท้าย')");
        Check("refuse: data at the call", HEdit("_row('อายุ", row, "x").Error == "studio.fedit.text.data");
        Check("refuse: no call of that helper there", HEdit("line(Icons.store", row, "x").Error == "studio.fedit.stale");

        // ── not followed ──
        string hidden = HelperScreen.Replace("  Widget _row(String label) => Text(label);",
                                             "  Widget _row(String label) {\n    for (final label in items) {\n      debugPrint(label);\n    }\n    return Text(label);\n  }");
        Check("not followed: a loop variable of that name inside the helper hides its parameter", HParam("return Text(label)", 7, hidden) == null, Show(HParam("return Text(label)", 7, hidden)));
        string twice = HelperScreen.Replace("  Widget _section(String title)", "  Widget other() {\n    Widget _row(String x) => Text(x);\n    return _row('อื่น');\n  }\n\n  Widget _section(String title)");
        Check("not followed: another function of that name inside the class (which call is whose can't be told)", HParam("=> Text(label)", 3, twice) == null, Show(HParam("=> Text(label)", 3, twice)));
        var inner = HParam("_row(String x) => Text(x)", 18, twice);
        Check("… the inner one is followed in its own block", inner is { Helper: { Name: "_row", Kind: FlutterHelperKind.Local } } && FlutterCodeEdit.HelperCalls(twice, inner).Count == 1, Show(inner));

        HelperPicks(row, line, title, value, pill, footer, sect.PassedOn);
    }

    /// <summary>The inspector's boxes for <see cref="HelperScreen"/> as Flutter draws it (as the renderer records them: each
    /// box's parent), <paramref name="items"/> rows from the loop; without ids (an older picture) parents come from
    /// the boxes' order, depth and place. Variants of the screen swap one element of the Column's list.</summary>
    static (List<WidgetBox> All, Dictionary<string, WidgetBox> Named) HelperBoxes(string text, int items = 0, bool ids = true, string? swapped = null)
    {
        var all = new List<WidgetBox>();
        var named = new Dictionary<string, WidgetBox>();
        WidgetBox B(string widget, string marker, int skip, int parent, int depth, double x, double y, double w, double h, string? name = null)
        {
            var (l, c) = At(text, marker, skip);
            var b = new WidgetBox(widget, HelperFile, l, c, x, y, w, h, depth, ids ? all.Count : -1, ids ? parent : -1);
            all.Add(b);
            if (name != null) named[name] = b;
            return b;
        }
        int Id(WidgetBox b) => all.IndexOf(b);
        var col = B("Column", "return Column(", 7, -1, 0, 0, 0, 360, 800, "col");
        B("Text", "=> Text(label)", 3, 0, 1, 0, 0, 100, 20, "r1");
        B("Text", "=> Text(label)", 3, 0, 1, 0, 20, 100, 20, "r2");
        void Line(double y, string name)
        {
            int pad = Id(B("Padding", "Padding(\n          padding: const EdgeInsets.only(top: 8)", 0, 0, 1, 0, y, 360, 30));
            int row = Id(B("Row", "child: Row(children: [\n            Icon", 7, pad, 2, 0, y + 8, 360, 22));
            B("Icon", "Icon(icon, size: 14)", 0, row, 3, 0, y + 10, 14, 14);
            B("SizedBox", "const SizedBox(width: 8)", 6, row, 3, 14, y + 10, 8, 14);
            int exp = Id(B("Expanded", "Expanded(child: Text(text))", 0, row, 3, 22, y + 8, 338, 22));
            B("Text", "Expanded(child: Text(text))", 16, exp, 4, 22, y + 8, 200, 20, name);
        }
        Line(40, "l1");
        Line(70, "l2");
        B("SizedBox", "const SizedBox(height: 8)", 6, 0, 1, 0, 100, 360, 8);
        Line(108, "l3");
        int tiles = Id(B("Row", "Row(children: [\n          Expanded(child: tile", 0, 0, 1, 0, 138, 360, 60));
        void Tile(string marker, double x, string name)
        {
            int e = Id(B("Expanded", marker, 0, tiles, 2, x, 138, 180, 60, name + ".exp"));
            int card = Id(B("Card", "=> Card(", 3, e, 3, x, 138, 180, 60));
            int c2 = Id(B("Column", "child: Column(children: [Text(title)", 7, card, 4, x + 4, 142, 172, 52));
            B("Text", "[Text(title)", 1, c2, 5, x + 4, 142, 100, 20, name + ".title");
            B("Text", "Text(value)", 0, c2, 5, x + 4, 162, 100, 20, name + ".value");
        }
        Tile("Expanded(child: tile(title: 'AI Pool'", 0, "t1");
        Tile("Expanded(child: tile(title: 'LINE push'", 180, "t2");
        if (swapped == "listTile")
        {
            int lt = Id(B("ListTile", "ListTile(title: _row(", 0, 0, 1, 0, 198, 360, 40, "tile"));
            B("Text", "=> Text(label)", 3, lt, 2, 16, 200, 100, 20, "lt1");
            B("Text", "=> Text(label)", 3, lt, 2, 16, 220, 100, 16, "lt2");
        }
        else
        {
            int hd = Id(B("_Header", "=> _Header(caption: title)", 3, 0, 1, 0, 198, 360, 20, "header"));
            B("Text", "=> Text(caption)", 3, hd, 2, 0, 198, 100, 20, "caption");
        }
        for (int i = 0; i < items; i++) B("Text", "=> Text(label)", 3, 0, 1, 0, 240 + 20 * i, 100, 20, "x" + i);
        double y2 = 240 + 20 * items;
        int chip = Id(B("Chip", "Chip(label: Text(label))", 0, 0, 1, 0, y2, 100, 30));
        B("Text", "Chip(label: Text(label))", 12, chip, 2, 8, y2 + 5, 60, 20, "pill");
        B("Text", "_footer(String note) => Text(note)", 24, 0, 1, 0, y2 + 30, 100, 20, "footer");
        return (all, named);
    }

    /// <summary>Which call of a helper drew a picked widget, told from the inspector's boxes — several calls in one list
    /// (the k-th box is the k-th call), one call per holder (its own Expanded), a loop element beside them, an older
    /// picture without parents, no picture at all (a wireframe pick), and the shapes where the picture can't be sure:
    /// a call kept in a variable, two calls in two arguments of one widget.</summary>
    static void HelperPicks(FlutterParamText? row, FlutterParamText? line, FlutterParamText? title, FlutterParamText? value, FlutterParamText? pill, FlutterParamText? footer,
                            FlutterParamText? section)
    {
        var (boxes, n) = HelperBoxes(HelperScreen);
        void Picks(string name, FlutterParamText? p, WidgetBox from, string? callMarker, IReadOnlyList<WidgetBox>? list = null, string? text = null, WidgetBox? anchor = null)
        {
            if (p == null) { Check(name, false, "no parameter"); return; }
            var u = FlutterCodeEdit.HelperUse(text ?? HelperScreen, HelperFile, p, list ?? boxes, from);
            if (callMarker == null) { Check(name, u.Line == 0 && u.Why == "unsure", $"{u.Line}:{u.Column} {u.Why}"); return; }
            var want = At(text ?? HelperScreen, callMarker);
            Check(name, (u.Line, u.Column) == want && (anchor == null || ReferenceEquals(u.Anchor, anchor)), $"{u.Line}:{u.Column} {u.Why} want {want.Line}:{want.Col} anchor {u.Anchor?.Widget}");
        }
        Picks("pick: the first of three calls in one list — the first of their boxes under that Column", line, n["l1"], "line(Icons.store", anchor: n["col"]);
        Picks("pick: the second — a widget between the calls changes nothing", line, n["l2"], "line(Icons.scale");
        Picks("pick: the third", line, n["l3"], "line(Icons.money");
        Picks("pick: a call held by its own Expanded", title, n["t1.title"], "tile(title: 'AI Pool'", anchor: n["t1.exp"]);
        Picks("pick: … the other Expanded's", value, n["t2.value"], "tile(title: 'LINE push'", anchor: n["t2.exp"]);
        Picks("pick: two whole elements before a loop element — still told by their order", row, n["r1"], "_row('ชื่อ')");
        Picks("pick: … the second", row, n["r2"], "_row('อายุ");
        Picks("pick: a helper called once", pill, n["pill"], "_pill('ไม่มีสิทธิ์')");
        Picks("pick: a private top-level function called once", footer, n["footer"], "_footer('ท้ายหน้า')");
        Picks("pick: from a widget's box in the helper (the hop after _Header's use)", section, n["header"], "_section('ข้อมูล')");
        var wire = line != null ? FlutterCodeEdit.HelperUse(HelperScreen, HelperFile, line, boxes, null) : new FlutterHelperUse();
        Check("pick: no picture (a wireframe pick) — several calls can't be told", wire is { Line: 0, Why: "many", Calls: 3 }, $"{wire.Why} {wire.Calls}");

        var (old, on) = HelperBoxes(HelperScreen, ids: false);
        Picks("pick: an older picture without parents — its boxes' order, depth and place give them", line, on["l2"], "line(Icons.scale", old);
        Picks("pick: … a call in its own Expanded", title, on["t2.title"], "tile(title: 'LINE push'", old);

        var (looped, ln) = HelperBoxes(HelperScreen, items: 2);
        Picks("pick: the loop drew two more boxes — the calls before it are still told", row, ln["r2"], "_row('อายุ", looped);
        Picks("unsure: a box the loop drew (it isn't one of the calls before the loop)", row, ln["x0"], null, looped);

        string kept = HelperScreen.Replace("        _row('ชื่อ'),\n", "        header,\n").Replace("    return Column(", "    final header = _row('หัว');\n    return Column(");
        var (kb, kn) = HelperBoxes(kept);
        var keptRow = HParam("=> Text(label)", 3, kept);
        Picks("unsure: a call kept in a variable could be any box in that list", keptRow, kn["r2"], null, kb, kept);

        string args = HelperScreen.Replace("        _section('ข้อมูล'),\n", "        ListTile(title: _row('หัวข้อ'), subtitle: _row('รอง')),\n");
        var (ab, an) = HelperBoxes(args, swapped: "listTile");
        var argsRow = HParam("=> Text(label)", 3, args);
        Picks("unsure: two calls in two arguments of one widget (its children aren't drawn in the arguments' order)", argsRow, an["lt1"], null, ab, args);
        Picks("pick: … while the calls in the Column's list are still told (the ListTile's have their own box)", argsRow, an["r1"], "_row('ชื่อ')", ab, args);
    }
}
