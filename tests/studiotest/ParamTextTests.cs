using System.IO;
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

    static string Show(FlutterParamText? p) => p == null ? "null" : $"{p.Class}.{p.Param} [{string.Join(", ", p.Ctors.Select(kv => (kv.Key.Length == 0 ? "()" : kv.Key) + "=" + kv.Value))}]";

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

    /// <summary>studiotest texts &lt;flutter folder&gt; — every Text of lib/ that shows a parameter of its widget, and what
    /// each use of that widget passes it (READ-ONLY on the project): how much of an app's text the text tool types over
    /// where the widget is used, and that the reader copes with the app's real code.</summary>
    static int ParamReport(string root)
    {
        var files = Directory.EnumerateFiles(Path.Combine(root, "lib"), "*.dart", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".g.dart", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".freezed.dart", StringComparison.OrdinalIgnoreCase)).ToList();
        var source = files.ToDictionary(f => f, f => File.ReadAllText(f), StringComparer.OrdinalIgnoreCase);
        string Rel(string f) => Path.GetRelativePath(root, f).Replace('\\', '/');
        int texts = 0, literal = 0, param = 0, singleUse = 0;
        var kinds = new Dictionary<FlutterArgKind, int>();
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
                param++;
                var uses = (p.Class.StartsWith('_') ? new List<string> { f } : files)
                    .SelectMany(u => FlutterCodeEdit.CallsOf(source[u], p.Class).Select(x => (File: u, x.Line, x.Column))).ToList();
                if (uses.Count == 1) singleUse++;
                var said = new List<string>();
                foreach (var u in uses)
                {
                    var a = FlutterCodeEdit.ArgText(source[u.File], u.Line, u.Column, p);
                    kinds[a.Kind] = kinds.GetValueOrDefault(a.Kind) + 1;
                    said.Add(a.Kind switch
                    {
                        FlutterArgKind.Literal => $"'{a.Literal}'",
                        FlutterArgKind.PassedOn => $"→{a.PassedOn!.Class}.{a.PassedOn.Param}",
                        FlutterArgKind.Translation => "l10n." + a.L10nKey,
                        var k => k.ToString().ToLowerInvariant(),
                    });
                }
                Console.WriteLine($"  {Rel(f)}:{line} {p.Class}.{p.Param} · {uses.Count} use(s): {string.Join(", ", said.Take(6))}{(said.Count > 6 ? ", …" : "")}");
            }
        }
        Console.WriteLine($"\n{files.Count} files · {texts} Text calls · {literal} literal (typed over in place) · {param} show a widget's parameter ({singleUse} of those widgets are used once)");
        Console.WriteLine("their uses pass: " + string.Join(", ", kinds.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key.ToString().ToLowerInvariant()} {kv.Value}")) + $" · read in {sw.Elapsed.TotalSeconds:0.0}s");
        return 0;
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
        Check("not a param: a method's parameter of the same name", ParamAt("_row(String name) => Text(name)", 21) == null);
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
        ArgIs("use: left out — the default shows", "const _Pill()", 6, hint, FlutterArgKind.Default);
        ArgIs("use: left out — a default value written in the constructor", "const _Pill()", 6, mood, FlutterArgKind.Default);
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
        Check("refuse: the use leaves it out (nothing there to type over)", ArgEdit("const _Pill()", 6, hint, "x").Error == "studio.fedit.stale");
        Check("refuse: no use of that widget at that place", ArgEdit("const _Split('ดูดวง'", 6, tag, "x").Error == "studio.fedit.stale");
        Check("refuse: a translation at the use is not typed over in the Dart code", ArgEdit("_Tag(label: AppL10n", 0, tag, "x", l10n).Error == "studio.fedit.text.l10n");
        var (ul, uc) = At(ParamScreen, "const _Tag(label: 'ยอดขาย')", 6);
        Check("refuse: the use says something else by now than the tool showed (changed meanwhile) — nothing is overwritten",
              tag != null && FlutterCodeEdit.SetArgText(ParamScreen, ul, uc, tag, "x", expect: "ยอดเดิม").Error == "studio.fedit.changedMeanwhile");
        Edited("write: … and it is written when the use still says what the tool showed", ParamScreen,
               tag != null ? FlutterCodeEdit.SetArgText(ParamScreen, ul, uc, tag, "ยอดสุทธิ", expect: "ยอดขาย") : new FlutterEdit(), "const _Tag(label: 'ยอดขาย')", "const _Tag(label: 'ยอดสุทธิ')");
    }
}
