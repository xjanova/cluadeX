using System.Diagnostics;
using System.IO;
using CluadeX.Services.UiStudio.Sketch;

namespace StudioTest;

/// <summary>
/// The UI Studio's widget tools on real Dart code (FlutterCodeEdit, through the REAL CluadeX.dll): each tool's exact
/// output on dart-format style code — text, style, colour, padding, margin, radius, size, delete, duplicate, move,
/// wrap, unwrap, insert, drop — the const rules (kept for constants, dropped for closures), CRLF files, position
/// mapping. When Flutter is on this PC every result is also put through `dart analyze` (errors only): a tool that
/// writes code that doesn't compile fails here.
/// </summary>
internal static partial class Program
{
    const string Home = @"import 'package:flutter/material.dart';

class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final name = DateTime.now().toString();
    return Scaffold(
      appBar: AppBar(title: const Text('หน้าแรก')),
      body: Column(
        children: [
          const Text('สวัสดี'),
          Container(
            height: 80,
            color: const Color(0xFF1565C0),
          ),
          ElevatedButton(onPressed: () {}, child: const Text('เริ่มเลย')),
          const Padding(
            padding: EdgeInsets.all(16),
            child: Text('ยินดีต้อนรับ'),
          ),
          Text('Hi $name'),
          const Row(children: [Icon(Icons.star), Text('ดาว')]),
        ],
      ),
    );
  }
}
";

    /// <summary>The 1-based line and column of the first character of <paramref name="marker"/> (after <paramref name="skip"/> chars).</summary>
    static (int Line, int Col) At(string text, string marker, int skip = 0)
    {
        int at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) throw new Exception("marker not found: " + marker);
        at += skip;
        int line = 1, start = 0;
        for (int i = 0; i < at; i++) if (text[i] == '\n') { line++; start = i + 1; }
        return (line, at - start + 1);
    }

    static readonly List<(string Name, string Code)> Compiled = new();

    /// <summary>Check one edit: it worked, and the new text is the old one with exactly <paramref name="from"/> → <paramref name="to"/>.</summary>
    static void Edited(string name, string before, FlutterEdit e, string from, string to)
    {
        if (!e.Ok || e.Text == null) { Check(name, false, $"failed: {e.Error}"); return; }
        int n = before.Split(from).Length - 1;
        string expected = n == 1 ? before.Replace(from, to) : "(the 'from' text is not unique: " + n + ")";
        bool ok = e.Text == expected;
        Check(name, ok, ok ? "" : "GOT:\n" + Around(e.Text, to) + "\nEXPECTED:\n" + Around(expected, to));
        if (ok) Compiled.Add((name, e.Text));
    }

    static string Around(string text, string probe)
    {
        int at = text.IndexOf(probe.Trim().Split('\n')[0].Trim(), StringComparison.Ordinal);
        if (at < 0) return text;
        int s = Math.Max(0, text.LastIndexOf('\n', Math.Max(0, at - 120))), e = Math.Min(text.Length, at + probe.Length + 160);
        return text[s..e];
    }

    static void FlutterEdits(string temp)
    {
        Console.WriteLine("UI Studio: widget tools on real Dart code");
        var (l, c) = At(Home, "Text('สวัสดี')");

        // ── reading ──
        var info = FlutterCodeEdit.Inspect(Home, l, c);
        Check("inspect: a const Text in a list — its text, a list item, const", info is { CanText: true, Text: "สวัสดี", InList: true, CanMoveUp: false, CanMoveDown: true, InConst: true, CanTextStyle: true },
              $"{info?.Text} list={info?.InList} up={info?.CanMoveUp} down={info?.CanMoveDown} const={info?.InConst}");
        var (bl, bc) = At(Home, "ElevatedButton(");
        var binfo = FlutterCodeEdit.Inspect(Home, bl, bc);
        Check("inspect: a button shows its label's text and can take a colour", binfo is { Text: "เริ่มเลย", CanText: true, CanColor: true, CanExpanded: true }, $"{binfo?.Text} {binfo?.CanColor}");
        var (hl, hc) = At(Home, "Text('Hi");
        Check("inspect: text built from data is not typed over", FlutterCodeEdit.Inspect(Home, hl, hc) is { CanText: false, TextNote: "studio.fedit.text.data" });
        var (kl, kc) = At(Home, "Container(");
        var kinfo = FlutterCodeEdit.Inspect(Home, kl, kc);
        Check("inspect: a Container — its colour and size as written", kinfo is { Color: "#1565C0", Height: 80, CanPadding: true, CanColor: true }, $"{kinfo?.Color} {kinfo?.Height}");
        var (pl, pc) = At(Home, "Padding(");
        Check("inspect: a Padding's padding", FlutterCodeEdit.Inspect(Home, pl, pc) is { Padding: 16, CanPadding: true, CanMargin: false, CanUnwrap: true });
        Check("inspect: no widget there", FlutterCodeEdit.Inspect(Home, 1, 1) == null);

        // ── text ──
        Edited("text: a literal is replaced, nothing else", Home, FlutterCodeEdit.SetText(Home, l, c, "ข้อความใหม่"), "const Text('สวัสดี')", "const Text('ข้อความใหม่')");
        Edited("text: quotes, $ and new lines are escaped", Home, FlutterCodeEdit.SetText(Home, l, c, "it's $5\nok"), "const Text('สวัสดี')", @"const Text('it\'s \$5\nok')");
        Edited("text: a button's label", Home, FlutterCodeEdit.SetText(Home, bl, bc, "ไปกัน"), "child: const Text('เริ่มเลย')", "child: const Text('ไปกัน')");
        var bad = FlutterCodeEdit.SetText(Home, hl, hc, "x");
        Check("text: interpolated text is refused (it comes from data)", !bad.Ok && bad.Error == "studio.fedit.text.data", bad.Error ?? "");
        string l10n = "class A extends StatelessWidget {\n  Widget build(BuildContext context) => Text(context.l10n.users);\n}\n";
        var (ll, lc) = At(l10n, "Text(");
        Check("text: a translation is refused with its own reason", FlutterCodeEdit.SetText(l10n, ll, lc, "x").Error == "studio.fedit.text.l10n");

        // ── text style ──
        Edited("style: a size inside a const needs no const of its own", Home, FlutterCodeEdit.SetFontSize(Home, l, c, 18),
               "const Text('สวัสดี')", "const Text('สวัสดี', style: TextStyle(fontSize: 18))");
        Edited("style: outside a const the TextStyle is made const", Home, FlutterCodeEdit.SetFontSize(Home, hl, hc, 20.5),
               "Text('Hi $name')", "Text('Hi $name', style: const TextStyle(fontSize: 20.5))");
        var sized = FlutterCodeEdit.SetFontSize(Home, l, c, 18).Text!;
        var (sl, sc) = At(sized, "Text('สวัสดี'");
        Edited("style: weight joins the same TextStyle", sized, FlutterCodeEdit.SetFontWeight(sized, sl, sc, "bold"),
               "TextStyle(fontSize: 18)", "TextStyle(fontSize: 18, fontWeight: FontWeight.bold)");
        Edited("style: text colour", sized, FlutterCodeEdit.SetTextColor(sized, sl, sc, "#E53935"),
               "TextStyle(fontSize: 18)", "TextStyle(fontSize: 18, color: Color(0xFFE53935))");
        Edited("style: clearing the size takes it out", sized, FlutterCodeEdit.SetFontSize(sized, sl, sc, null),
               "TextStyle(fontSize: 18)", "TextStyle()");
        Edited("style: a style from the theme is kept and changed with copyWith", Themed,
               FlutterCodeEdit.SetFontSize(Themed, At(Themed, "Text(").Line, At(Themed, "Text(").Col, 22),
               "style: Theme.of(context).textTheme.titleLarge)", "style: Theme.of(context).textTheme.titleLarge?.copyWith(fontSize: 22))");
        Edited("style: alignment", Home, FlutterCodeEdit.SetTextAlign(Home, l, c, "center"), "const Text('สวัสดี')", "const Text('สวัสดี', textAlign: TextAlign.center)");
        Check("style: a bad colour is refused", FlutterCodeEdit.SetTextColor(Home, l, c, "#12345").Error == "studio.fedit.color.bad");

        // ── colour ──
        Edited("colour: a Container's colour is replaced (and stays const)", Home, FlutterCodeEdit.SetColor(Home, kl, kc, "2E7D32"),
               "color: const Color(0xFF1565C0)", "color: const Color(0xFF2E7D32)");
        Edited("colour: a button gets styleFrom (not const: the button has a closure)", Home, FlutterCodeEdit.SetColor(Home, bl, bc, "#FF6F00"),
               "ElevatedButton(onPressed: () {}, child: const Text('เริ่มเลย'))",
               "ElevatedButton(onPressed: () {}, style: ElevatedButton.styleFrom(backgroundColor: const Color(0xFFFF6F00)), child: const Text('เริ่มเลย'))");
        var (al, ac) = At(Home, "AppBar(");
        Edited("colour: an AppBar's background", Home, FlutterCodeEdit.SetColor(Home, al, ac, "#FFFFFF"),
               "AppBar(title: const Text('หน้าแรก'))", "AppBar(title: const Text('หน้าแรก'), backgroundColor: const Color(0xFFFFFFFF))");
        Check("colour: a Text has no colour of its own here (its text colour is the style tool)", FlutterCodeEdit.SetColor(Home, l, c, "#000000").Error == "studio.fedit.color.unsupported");

        // ── spacing, corners, size ──
        Edited("padding: a Padding's padding (inside its const)", Home, FlutterCodeEdit.SetPadding(Home, pl, pc, 24), "padding: EdgeInsets.all(16)", "padding: EdgeInsets.all(24)");
        Edited("padding: a Container gets its own", Home, FlutterCodeEdit.SetPadding(Home, kl, kc, 12),
               "            color: const Color(0xFF1565C0),\n", "            color: const Color(0xFF1565C0),\n            padding: const EdgeInsets.all(12),\n");
        var m1 = FlutterCodeEdit.SetMargin(Home, l, c, 8);
        Edited("margin: a Text is put in a Padding", Home, m1, "const Text('สวัสดี'),", "Padding(padding: const EdgeInsets.all(8), child: const Text('สวัสดี')),");
        Check("margin: the selection stays on the Text", m1.Ok && m1.Text != null && FlutterCodeEdit.Inspect(m1.Text, m1.FocusLine, m1.FocusColumn)?.Name == "Text", $"{m1.FocusLine}:{m1.FocusColumn}");
        if (m1.Text != null)
        {
            var (ml, mc) = At(m1.Text, "Text('สวัสดี')");
            Edited("margin: again — the same Padding changes (no Padding in a Padding)", m1.Text, FlutterCodeEdit.SetMargin(m1.Text, ml, mc, 20),
                   "padding: const EdgeInsets.all(8)", "padding: const EdgeInsets.all(20)");
            Edited("margin: none — the Padding goes away", m1.Text, FlutterCodeEdit.SetMargin(m1.Text, ml, mc, null),
                   "Padding(padding: const EdgeInsets.all(8), child: const Text('สวัสดี'))", "const Text('สวัสดี')");
        }
        Edited("radius: a Container's colour moves into its BoxDecoration", Home, FlutterCodeEdit.SetRadius(Home, kl, kc, 12),
               "color: const Color(0xFF1565C0),", "decoration: const BoxDecoration(color: Color(0xFF1565C0), borderRadius: BorderRadius.all(Radius.circular(12))),");
        Edited("radius: a button's shape", Home, FlutterCodeEdit.SetRadius(Home, bl, bc, 20),
               "ElevatedButton(onPressed: () {}, child:",
               "ElevatedButton(onPressed: () {}, style: ElevatedButton.styleFrom(shape: const RoundedRectangleBorder(borderRadius: BorderRadius.all(Radius.circular(20)))), child:");
        Edited("size: a Container's height and a new width", Home, FlutterCodeEdit.SetSize(Home, kl, kc, 200, 90),
               "            height: 80,\n            color: const Color(0xFF1565C0),\n", "            height: 90,\n            color: const Color(0xFF1565C0),\n            width: 200,\n");
        Edited("size: a Text is put in a SizedBox", Home, FlutterCodeEdit.SetSize(Home, hl, hc, 120, null),
               "Text('Hi $name'),", "SizedBox(width: 120, child: Text('Hi $name')),");

        // ── structure ──
        Edited("delete: a list item's whole line goes", Home, FlutterCodeEdit.Delete(Home, l, c), "          const Text('สวัสดี'),\n", "");
        Edited("delete: a several-line item goes with all its lines", Home, FlutterCodeEdit.Delete(Home, kl, kc),
               "          Container(\n            height: 80,\n            color: const Color(0xFF1565C0),\n          ),\n", "");
        var (tl, tc) = At(Home, "Text('หน้าแรก')");
        Edited("delete: an optional slot (AppBar title) is taken out", Home, FlutterCodeEdit.Delete(Home, tl, tc), "AppBar(title: const Text('หน้าแรก'))", "AppBar()");
        var (pt, pct) = At(Home, "Text('ยินดีต้อนรับ')");
        Edited("delete: a Padding's child (optional) is taken out", Home, FlutterCodeEdit.Delete(Home, pt, pct),
               "            padding: EdgeInsets.all(16),\n            child: Text('ยินดีต้อนรับ'),\n", "            padding: EdgeInsets.all(16),\n");
        var (bt, btc) = At(Home, "Text('เริ่มเลย')");
        Edited("delete: a required child (the button's label) becomes an empty box", Home, FlutterCodeEdit.Delete(Home, bt, btc),
               "child: const Text('เริ่มเลย')", "child: const SizedBox.shrink()");
        var dup = FlutterCodeEdit.Duplicate(Home, bl, bc);
        Edited("duplicate: a copy on the next line", Home, dup,
               "          ElevatedButton(onPressed: () {}, child: const Text('เริ่มเลย')),\n",
               "          ElevatedButton(onPressed: () {}, child: const Text('เริ่มเลย')),\n          ElevatedButton(onPressed: () {}, child: const Text('เริ่มเลย')),\n");
        Check("duplicate: the copy is selected", dup.FocusLine == bl + 1 && dup.FocusColumn == bc, $"{dup.FocusLine}:{dup.FocusColumn} (button at {bl}:{bc})");
        Check("duplicate: not outside a list", FlutterCodeEdit.Duplicate(Home, al, ac).Error == "studio.fedit.notInList");
        var mv = FlutterCodeEdit.Move(Home, l, c, +1);
        Edited("move: down past a several-line sibling", Home, mv,
               "          const Text('สวัสดี'),\n          Container(\n            height: 80,\n            color: const Color(0xFF1565C0),\n          ),\n",
               "          Container(\n            height: 80,\n            color: const Color(0xFF1565C0),\n          ),\n          const Text('สวัสดี'),\n");
        Check("move: the moved widget stays selected", mv.FocusLine == l + 4 && FlutterCodeEdit.Inspect(mv.Text ?? "", mv.FocusLine, mv.FocusColumn)?.Text == "สวัสดี", $"{mv.FocusLine}:{mv.FocusColumn}");
        Check("move: not past the first", FlutterCodeEdit.Move(Home, l, c, -1).Error == "studio.fedit.move.edge");
        var (il, ic) = At(Home, "Icon(Icons.star)");
        Edited("move: in a one-line list too", Home, FlutterCodeEdit.Move(Home, il, ic, +1), "[Icon(Icons.star), Text('ดาว')]", "[Text('ดาว'), Icon(Icons.star)]");

        var w1 = FlutterCodeEdit.Wrap(Home, hl, hc, "Center");
        Edited("wrap: Center on one line", Home, w1, "Text('Hi $name'),", "Center(child: Text('Hi $name')),");
        Check("wrap: the wrapper is selected", w1.Text != null && FlutterCodeEdit.Inspect(w1.Text, w1.FocusLine, w1.FocusColumn)?.Name == "Center");
        Edited("wrap: a several-line widget is indented inside its Padding", Home, FlutterCodeEdit.Wrap(Home, kl, kc, "Padding"),
               "          Container(\n            height: 80,\n            color: const Color(0xFF1565C0),\n          ),\n",
               "          Padding(\n            padding: const EdgeInsets.all(8),\n            child: Container(\n              height: 80,\n              color: const Color(0xFF1565C0),\n            ),\n          ),\n");
        Edited("wrap: Expanded in a Column (past 80 columns: on its own lines)", Home, FlutterCodeEdit.Wrap(Home, bl, bc, "Expanded"),
               "          ElevatedButton(onPressed: () {}, child: const Text('เริ่มเลย')),\n",
               "          Expanded(\n            child: ElevatedButton(onPressed: () {}, child: const Text('เริ่มเลย')),\n          ),\n");
        Check("wrap: Expanded outside a Row / Column is refused", FlutterCodeEdit.Wrap(Home, tl, tc, "Expanded").Error == "studio.fedit.wrap.expanded");
        Edited("wrap: InkWell (a closure) drops the const around it", Home, FlutterCodeEdit.Wrap(Home, il, ic, "InkWell"),
               "const Row(children: [Icon(Icons.star), Text('ดาว')])", "Row(children: [InkWell(onTap: () {}, child: Icon(Icons.star)), Text('ดาว')])");
        Edited("unwrap: a Padding gives back its child", Home, FlutterCodeEdit.Unwrap(Home, pl, pc),
               "          const Padding(\n            padding: EdgeInsets.all(16),\n            child: Text('ยินดีต้อนรับ'),\n          ),\n", "          Text('ยินดีต้อนรับ'),\n");

        var ins = FlutterCodeEdit.Insert(Home, l, c, "const SizedBox(height: 16)", FlutterCodeEdit.Place.After);
        Edited("insert: after a list item, on its own line", Home, ins, "          const Text('สวัสดี'),\n", "          const Text('สวัสดี'),\n          const SizedBox(height: 16),\n");
        Check("insert: the new widget is selected", ins.FocusLine == l + 1 && FlutterCodeEdit.Inspect(ins.Text ?? "", ins.FocusLine, ins.FocusColumn)?.Name == "SizedBox", $"{ins.FocusLine}:{ins.FocusColumn}");
        var (cl, cc) = At(Home, "Column(");
        Edited("insert: inside a Column goes last", Home, FlutterCodeEdit.Insert(Home, cl, cc, "const Divider()", FlutterCodeEdit.Place.Inside),
               "          const Row(children: [Icon(Icons.star), Text('ดาว')]),\n", "          const Row(children: [Icon(Icons.star), Text('ดาว')]),\n          const Divider(),\n");
        Edited("insert: into a Container with no child", Home, FlutterCodeEdit.Insert(Home, kl, kc, "const Text('ใหม่')", FlutterCodeEdit.Place.Inside),
               "            color: const Color(0xFF1565C0),\n", "            color: const Color(0xFF1565C0),\n            child: const Text('ใหม่'),\n");
        Edited("insert: a constant into a const list loses its own const", Home, FlutterCodeEdit.Insert(Home, il, ic, "const Text('ใหม่')", FlutterCodeEdit.Place.After),
               "[Icon(Icons.star), Text('ดาว')]", "[Icon(Icons.star), Text('ใหม่'), Text('ดาว')]");
        Edited("insert: a button (a closure) into a const list drops the const", Home, FlutterCodeEdit.Insert(Home, il, ic, "TextButton(onPressed: () {}, child: const Text('ลิงก์'))", FlutterCodeEdit.Place.Before),
               "const Row(children: [Icon(Icons.star), Text('ดาว')])", "Row(children: [TextButton(onPressed: () {}, child: const Text('ลิงก์')), Icon(Icons.star), Text('ดาว')])");
        Edited("insert: next to a widget that is no list item puts both in a Column", Home, FlutterCodeEdit.Insert(Home, tl, tc, "const Text('ย่อย')", FlutterCodeEdit.Place.After),
               "AppBar(title: const Text('หน้าแรก'))",
               "AppBar(title: Column(\n        mainAxisSize: MainAxisSize.min,\n        children: [\n          const Text('หน้าแรก'),\n          const Text('ย่อย'),\n        ],\n      ))");

        // ── line endings, mapping ──
        string crlf = Home.Replace("\n", "\r\n");
        var (xl, xc) = At(crlf, "Text('สวัสดี')");
        var ce = FlutterCodeEdit.Insert(crlf, xl, xc, "const Divider()", FlutterCodeEdit.Place.After);
        Check("crlf: a new line uses the file's own line break", ce.Text != null && ce.Text.Contains("const Text('สวัสดี'),\r\n          const Divider(),\r\n") && !ce.Text.Replace("\r\n", "").Contains('\n'));
        var below = At(Home, "Text('Hi");
        Check("map: a widget below an inserted line moves down one line", ins.Map(below.Line, below.Col) == (below.Line + 1, below.Col), $"{ins.Map(below.Line, below.Col)}");
        var w2 = FlutterCodeEdit.Wrap(Home, kl, kc, "Padding");
        Check("map: a widget inside what was rewritten has no place until it is drawn again", w2.Map(kl, kc) == null);
        Check("map: a widget above is where it was", w2.Map(l, c) == (l, c));

        // ── const safety ──
        Check("const: literals, Colors.x, EdgeInsets.all, TextStyle are constants",
              new[] { "Color(0xFF000000)", "Colors.red", "EdgeInsets.all(8)", "TextStyle(fontSize: 18, fontWeight: FontWeight.bold)", "BorderRadius.all(Radius.circular(12))", "Text('a')" }.All(x => FlutterConstSafe(x)));
        Check("const: closures, Theme.of, variables, shades, Border.all are not",
              new[] { "() {}", "Theme.of(context).colorScheme.primary", "Text(name)", "Colors.red.shade100", "Border.all(width: 1)", "Text('$a')", "ElevatedButton.styleFrom()", "BorderRadius.circular(8)" }.All(x => !FlutterConstSafe(x)));

        // ── drop ──
        var boxes = new List<WidgetBox>
        {
            new("Column", "home.dart", cl, cc, 0, 56, 390, 400, 5),
            new("Text", "home.dart", l, c, 0, 56, 60, 20, 6),
            new("Container", "home.dart", kl, kc, 0, 76, 390, 80, 6),
            new("ElevatedButton", "home.dart", bl, bc, 110, 156, 160, 48, 6),
        };
        var plan = FlutterCodeEdit.PlanDrop(boxes, 200, 200, f => Home, f => true);
        Check("drop: below the last child of a Column goes after it", plan is { Owner: "Column", Index: 3 }, $"{plan?.Owner} #{plan?.Index}");
        var plan2 = FlutterCodeEdit.PlanDrop(boxes, 30, 70, f => Home, f => true);
        Check("drop: on the lower half of the Text goes after it", plan2 is { Owner: "Column", Index: 1 } && Math.Abs(plan2.Y - 76) < 0.1, $"{plan2?.Owner} #{plan2?.Index} y={plan2?.Y}");
        var plan3 = FlutterCodeEdit.PlanDrop(boxes, 30, 120, f => Home, f => true);
        Check("drop: on an empty Container goes inside it", plan3 is { Owner: "Container", Index: -1 }, $"{plan3?.Owner} #{plan3?.Index}");
        if (plan2 != null)
            Edited("drop: the widget lands where the plan says", Home, FlutterCodeEdit.InsertAt(Home, plan2.OwnerLine, plan2.OwnerColumn, plan2.Index, "const Text('วาง')"),
                   "          const Text('สวัสดี'),\n", "          const Text('สวัสดี'),\n          const Text('วาง'),\n");
        Check("drop: nothing under the point", FlutterCodeEdit.PlanDrop(boxes, 380, 800, f => Home, f => true) == null);
        Check("drop: boxes in files the Studio may not edit are passed over", FlutterCodeEdit.PlanDrop(boxes, 200, 200, f => Home, f => false) == null);

        ParamTexts();
        AnalyzeEdits(temp);
    }

    const string Themed = @"import 'package:flutter/material.dart';

class ThemedScreen extends StatelessWidget {
  const ThemedScreen({super.key});

  @override
  Widget build(BuildContext context) {
    return Text('หัวข้อ', style: Theme.of(context).textTheme.titleLarge);
  }
}
";

    static bool FlutterConstSafe(string code) =>
        (bool)typeof(FlutterCodeEdit).Assembly.GetType("CluadeX.Services.UiStudio.Sketch.DartDoc")!
            .GetMethod("ConstSafe", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { code })!;

    /// <summary>Every edited file through `dart analyze` in a throwaway Flutter project (errors only).</summary>
    static void AnalyzeEdits(string temp)
    {
        string? flutter = FlutterRenderer.FindFlutter();
        if (flutter == null) { Console.WriteLine("  (skipped dart analyze: Flutter is not installed)"); return; }
        string root = Path.Combine(temp, "fedit");
        W(root, "pubspec.yaml", "name: fedit\nenvironment:\n  sdk: '>=3.0.0 <4.0.0'\ndependencies:\n  flutter:\n    sdk: flutter\n");
        W(root, "lib/base.dart", Home);
        W(root, "lib/themed_base.dart", Themed);
        for (int i = 0; i < Compiled.Count; i++) W(root, $"lib/edit_{i:00}.dart", Compiled[i].Code);
        string bin = Path.GetDirectoryName(flutter)!;
        string dart = Path.Combine(bin, "dart.bat");
        if (!File.Exists(dart)) dart = Path.Combine(bin, "cache", "dart-sdk", "bin", "dart.exe");
        var (pubOk, pubOut) = RunTool(flutter, "pub get --offline", root, 180);
        if (!pubOk) { Console.WriteLine("  (skipped dart analyze: no offline package cache) " + pubOut.Trim().Split('\n').LastOrDefault()); return; }
        var (_, output) = RunTool(dart, "analyze --format=machine lib", root, 300);
        var errors = output.Split('\n').Where(x => x.StartsWith("ERROR|", StringComparison.Ordinal)).ToList();
        string Name(string line)
        {
            var f = line.Split('|');
            string file = f.Length > 3 ? Path.GetFileNameWithoutExtension(f[3]) : "";
            return file.StartsWith("edit_") && int.TryParse(file[5..], out int k) && k < Compiled.Count ? Compiled[k].Name : file;
        }
        Check($"dart analyze: all {Compiled.Count} edited files compile (no errors)", errors.Count == 0,
              string.Join("\n", errors.Take(12).Select(e => Name(e) + " → " + e.Split('|').Last())));
    }

    static (bool Ok, string Output) RunTool(string exe, string args, string cwd, int seconds)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{exe}\" {args}\"")
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(seconds * 1000)) { try { p.Kill(true); } catch { } return (false, "timeout"); }
        return (p.ExitCode == 0, o.Result + e.Result);
    }
}
