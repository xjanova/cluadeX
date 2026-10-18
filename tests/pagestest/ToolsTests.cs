using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CluadeX.Services.UiStudio;
using CluadeX.Services.UiStudio.Pages;
using CluadeX.Services.UiStudio.Sketch;
using CluadeX.ViewModels;
using CluadeX.Views;

namespace PagesTest;

/// <summary>
/// The widget tools and the review round on a REAL Flutter app (the flutter-real fixture), every step drawn again by
/// Flutter and checked on the new picture: text, size, colours, corners (a Container's colour moved into its
/// decoration, no assertion), margin, duplicate, move, wrap, palette insert, a drop on the picture, undo; then the
/// round: the AI's change drawn live while it works, before/after, undo all; and the save check — dart analyze,
/// every screen drawn, a compile error handed to the AI and fixed, the tools' placements looked over once.
/// The AI is a scripted stand-in (no model is loaded): it edits the files the way the chat would.
/// </summary>
internal static partial class Program
{
    /// <summary>An AI that edits files by script: busy while its script runs, like the chat while it writes.</summary>
    sealed class ScriptedAi : IStudioAi
    {
        public readonly List<string> Prompts = new();
        public Func<string, Task>? Script;
        bool _busy;
        public bool IsBusy => _busy;
        public event Action? BusyChanged;

        public bool Send(string prompt)
        {
            if (_busy || Script == null) return false;
            Prompts.Add(prompt);
            Set(true);
            Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
            {
                try { await Script(prompt); }
                catch (Exception ex) { Log("  (scripted AI failed: " + ex.Message + ")"); }
                finally { Set(false); }
            });
            return true;
        }

        void Set(bool v) { _busy = v; BusyChanged?.Invoke(); }
    }

    static WidgetBox? Box(UiStudioViewModel vm, string widget, int line) =>
        vm.StageBoxes.FirstOrDefault(b => b.Widget == widget && b.Line == line);

    /// <summary>Until the stage screen's kept picture is newer than <paramref name="after"/> and nothing is drawing.</summary>
    static async Task<bool> Redrawn(UiStudioViewModel vm, PageShotStore store, string pageId, DateTime after, int timeoutMs = 240000) =>
        await WaitUntil(() => store.Get(pageId, "app")?.CapturedUtc > after && !vm.IsRendering, timeoutMs);

    static Color Pixel(BitmapSource src, double fx, double fy)
    {
        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        conv.CopyPixels(new Int32Rect((int)(conv.PixelWidth * fx), (int)(conv.PixelHeight * fy), 1, 1), px, 4, 0);
        return Color.FromRgb(px[2], px[1], px[0]);
    }

    static async Task FlutterTools(UiStudioViewModel vm, UiStudioView view, string fx)
    {
        Log("\n## Widget tools on the real picture, and the review round");
        string root = Path.Combine(fx, "flutter-real");
        if (!File.Exists(Path.Combine(root, ".dart_tool", "package_config.json"))) { Log("  (skipped: the real Flutter fixture is not set up)"); return; }
        CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Add("flutter.bat");
        CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Add("dart.bat");
        vm.RenderConsentOverride = true;
        vm.ToolConfirmOverride = true;
        var ai = new ScriptedAi();
        var chatAi = vm.StudioAi;
        vm.StudioAi = ai;
        string file = Path.Combine(root, "lib", "home_screen.dart");
        try
        {
            File.WriteAllText(file, RealHome);
            var store = (PageShotStore)GetField(vm, "_store")!;
            var home = Card(vm, "/")!;
            await WaitUntil(() => !vm.IsRendering, 120000);
            DateTime t0 = DateTime.UtcNow;
            vm.OpenCardCommand.Execute(home);
            vm.RenderStageCommand.Execute(null);
            Check("tools: the stage is drawn", await Redrawn(vm, store, home.Page.Id, t0), $"{home.State} {vm.CompileError}");

            // ── text ──
            var text = Box(vm, "Text", 13);
            if (text == null) { Check("tools: the welcome text has a box", false, string.Join(", ", vm.StageBoxes.Select(b => $"{b.Widget}:{b.Line}"))); return; }
            vm.PickStageBox(text, open: false);
            Check("tools: a picked Text offers its text", vm.HasTools && vm.CanToolText && vm.ToolText == "ยินดีต้อนรับกลับมา" && vm.CanToolTextStyle, $"{vm.HasTools} '{vm.ToolText}'");
            double oldW = text.W, oldH = text.H;
            vm.ToolText = "สวัสดีครับ";
            DateTime t1 = DateTime.UtcNow;
            vm.ApplyToolTextCommand.Execute(null);
            Check("tools: the new text is in the code", File.ReadAllText(file).Contains("Text('สวัสดีครับ')"));
            Check("tools: the text stays picked while Flutter draws", vm.StageBox?.Line == 13 && vm.HasTools);
            bool drawn = await Redrawn(vm, store, home.Page.Id, t1);
            var text2 = Box(vm, "Text", 13);
            Check("tools: drawn again — the shorter text has a narrower box", drawn && text2 != null && text2.W < oldW - 4, $"{oldW:0} → {text2?.W:0}");
            Check("tools: the same widget is picked on the new picture", ReferenceEquals(vm.StageBox, text2));

            DateTime t2 = DateTime.UtcNow;
            vm.StepToolCommand.Execute("fontSize:+10");
            Check("tools: + on the size writes a TextStyle", File.ReadAllText(file).Contains("TextStyle(fontSize: 24)"), FirstLine(file, "สวัสดีครับ"));
            drawn = await Redrawn(vm, store, home.Page.Id, t2);
            var text3 = Box(vm, "Text", 13);
            Check("tools: drawn again — bigger text, taller box", drawn && text3 != null && text3.H > oldH + 6, $"{oldH:0} → {text3?.H:0}");
            vm.SetToolTextColorCommand.Execute("#E53935");
            Check("tools: a text colour joins the same style", File.ReadAllText(file).Contains("TextStyle(fontSize: 24, color: Color(0xFFE53935))"), FirstLine(file, "สวัสดีครับ"));

            // ── colour and corners on the Container ──
            await WaitUntil(() => !vm.IsRendering, 120000);
            DateTime t3 = DateTime.UtcNow;
            await Redrawn(vm, store, home.Page.Id, t3.AddSeconds(-30));
            var band = Box(vm, "Container", 12);
            if (band == null) { Check("tools: the band has a box", false); return; }
            vm.PickStageBox(band, open: false);
            Check("tools: a Container offers colour, padding, corners", vm.CanToolColor && vm.CanToolPadding && vm.ToolColor == "#1565C0", vm.ToolColor);
            DateTime t4 = DateTime.UtcNow;
            vm.SetToolColorCommand.Execute("#2E7D32");
            drawn = await Redrawn(vm, store, home.Page.Id, t4);
            var green = drawn ? JpegAt(store.ImagePath(store.Get(home.Page.Id, "app")!), 0.5, 0.17).C : Colors.Black;
            Check("tools: a colour swatch repaints the band on the real picture", drawn && Near(green, Color.FromRgb(0x2E, 0x7D, 0x32)), $"{green}");
            DateTime t5 = DateTime.UtcNow;
            vm.ToolRadius = "16";
            vm.ApplyToolNumberCommand.Execute("radius");
            string code = File.ReadAllText(file);
            Check("tools: corners move the colour into a BoxDecoration (a Container can't have both)", code.Contains("decoration: const BoxDecoration(color: Color(0xFF2E7D32), borderRadius: BorderRadius.all(Radius.circular(16)))")
                  && !code.Contains("color: const Color(0xFF2E7D32),"), FirstLine(file, "BoxDecoration"));
            drawn = await Redrawn(vm, store, home.Page.Id, t5);
            var corner = JpegAt(store.ImagePath(store.Get(home.Page.Id, "app")!), 0.004, 0.068).C;
            var middle = JpegAt(store.ImagePath(store.Get(home.Page.Id, "app")!), 0.5, 0.17).C;
            Check("tools: drawn again — round corners, still green, no problem", drawn && Near(middle, Color.FromRgb(0x2E, 0x7D, 0x32)) && !Near(corner, Color.FromRgb(0x2E, 0x7D, 0x32), 30) && !vm.HasStageProblems,
                  $"corner {corner} middle {middle} problems={string.Join(" | ", vm.StageProblems)}");

            // ── the button: margin, duplicate, move, wrap ──
            var button = Box(vm, "ElevatedButton", 14);
            if (button == null) { Check("tools: the button has a box", false); return; }
            vm.PickStageBox(button, open: false);
            DateTime t6 = DateTime.UtcNow;
            vm.ToolMargin = "24";
            vm.ApplyToolNumberCommand.Execute("margin");
            Check("tools: margin puts the button in a Padding", File.ReadAllText(file).Contains("padding: const EdgeInsets.all(24),\n            child: ElevatedButton(") || File.ReadAllText(file).Contains("padding: const EdgeInsets.all(24),\r\n            child: ElevatedButton("),
                  FirstLine(file, "EdgeInsets.all(24)"));
            drawn = await Redrawn(vm, store, home.Page.Id, t6);
            var button2 = vm.StageBoxes.FirstOrDefault(b => b.Widget == "ElevatedButton");
            Check("tools: drawn again — the button moved down by its margin, and is still picked", drawn && button2 != null && button2.Y > button.Y + 20 && vm.StageBox?.Widget == "ElevatedButton",
                  $"y {button.Y:0} → {button2?.Y:0}, picked {vm.StageBox?.Widget}");
            vm.ToolMargin = "32";
            vm.ApplyToolNumberCommand.Execute("margin");
            Check("tools: margin again changes the same Padding", File.ReadAllText(file).Contains("padding: const EdgeInsets.all(32),") && !File.ReadAllText(file).Contains("EdgeInsets.all(24)")
                  && !File.ReadAllText(file).Contains("child: Padding("), FirstLine(file, "EdgeInsets.all(3"));

            await WaitUntil(() => !vm.IsRendering, 120000);
            await Redrawn(vm, store, home.Page.Id, DateTime.UtcNow.AddSeconds(-1));
            var welcome = vm.StageBoxes.FirstOrDefault(b => b.Widget == "Padding" && b.Line == 13);
            if (welcome != null)
            {
                vm.PickStageBox(welcome, open: false);
                DateTime t7 = DateTime.UtcNow;
                vm.ToolDuplicateCommand.Execute(null);
                Check("tools: duplicate puts a copy right after it", System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "Text\\('สวัสดีครับ'").Count == 2);
                drawn = await Redrawn(vm, store, home.Page.Id, t7);
                Check("tools: drawn again — two welcome texts", drawn && vm.StageBoxes.Count(b => b.Widget == "Text" && b.Line is 13 or 14) == 2,
                      string.Join(", ", vm.StageBoxes.Where(b => b.Widget == "Text").Select(b => $"{b.Line}")));
                // the first of the two goes up, before the band
                if (vm.StageBoxes.FirstOrDefault(b => b.Widget == "Padding" && b.Line == 13) is { } first) vm.PickStageBox(first, open: false);
                vm.ToolMoveCommand.Execute("-1");
                string moved = File.ReadAllText(file);
                Check("tools: move up puts it before the band", moved.IndexOf("สวัสดีครับ", StringComparison.Ordinal) < moved.IndexOf("BoxDecoration", StringComparison.Ordinal));
                DateTime t8 = DateTime.UtcNow;
                vm.ToolWrapCommand.Execute("Center");
                Check("tools: wrap in Center", File.ReadAllText(file).Contains("Center(") && File.ReadAllText(file).Contains("child: const Padding("), FirstLine(file, "Center("));
                drawn = await Redrawn(vm, store, home.Page.Id, t8);
                Check("tools: drawn again with the Center, no problem", drawn && vm.StageBoxes.Any(b => b.Widget == "Center") && !vm.HasStageProblems && !vm.HasCompileError, string.Join(" | ", vm.StageProblems));
                vm.ToolUnwrapCommand.Execute(null);
                Check("tools: unwrap takes the Center away again", !File.ReadAllText(file).Contains("Center("));
                vm.ToolDeleteCommand.Execute(null);
                Check("tools: delete (confirmed) takes it out", System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "Text\\('สวัสดีครับ'").Count == 1);
            }
            else Check("tools: the welcome Padding has a box", false);

            // ── the palette: a click, then a drop on the picture ──
            await WaitUntil(() => !vm.IsRendering, 120000);
            DateTime t9 = DateTime.UtcNow;
            await Redrawn(vm, store, home.Page.Id, t9.AddSeconds(-2));
            var column = vm.StageBoxes.FirstOrDefault(b => b.Widget == "Column");
            if (column != null)
            {
                vm.PickStageBox(column, open: false);
                var divider = vm.Palette.First(p => p.Key == "divider");
                DateTime t10 = DateTime.UtcNow;
                vm.InsertPaletteCommand.Execute(divider);
                Check("palette: a click puts a Divider at the end of the picked Column", File.ReadAllText(file).Contains("const Divider(),"), FirstLine(file, "Divider"));
                drawn = await Redrawn(vm, store, home.Page.Id, t10);
                Check("palette: drawn — the Divider is on the picture", drawn && vm.StageBoxes.Any(b => b.Widget == "Divider"));
                var last = vm.StageBoxes.Where(b => b.Widget != "Column" && b.File.EndsWith("home_screen.dart") && b.Depth > column.Depth).OrderByDescending(b => b.Y + b.H).First();
                var plan = vm.PlanDrop(195, last.Y + last.H + 30);
                Check("drop: below the last child the mark is under it, in the Column", plan is { Owner: "Column" } && plan.Index >= 0 && plan.Y >= last.Y + last.H - 1, $"{plan?.Owner} #{plan?.Index} y={plan?.Y:0} last={last.Y + last.H:0}");
                DateTime t11 = DateTime.UtcNow;
                vm.DropPalette("text", 195, last.Y + last.H + 30);
                string dropped = File.ReadAllText(file);
                Check("drop: the new Text is written where it was dropped (after the Divider)", dropped.IndexOf("const Divider()", StringComparison.Ordinal) < dropped.IndexOf("const Text('ข้อความใหม่')", StringComparison.Ordinal)
                      && dropped.Contains("const Text('ข้อความใหม่')"), FirstLine(file, "ข้อความใหม่"));
                drawn = await Redrawn(vm, store, home.Page.Id, t11);
                Check("drop: drawn — the dropped widget is on the picture and picked", drawn && vm.StageBox?.Widget == "Text" && vm.StageBox.W > 0, $"{vm.StageBox?.Widget} {vm.StageBox?.W:0}");
                SavePng((FrameworkElement)view.FindName("Stage"), "stage-tools.png");
                SavePng(MainWin.Content as FrameworkElement ?? view, "studio-tools.png");
                int before = File.ReadAllText(file).Length;
                vm.UndoCommand.Execute(null);
                Check("undo: the last tool edit (the drop) is taken back", !File.ReadAllText(file).Contains("ข้อความใหม่") && File.ReadAllText(file).Length < before);
            }
            else Check("palette: the Column has a box", false);

            // ── the round: the tools' edits wait for review; the save check looks them over ──
            await WaitUntil(() => !vm.IsRendering, 120000);
            Check("round: the tools' edits are a round to review", vm.HasSession && vm.SessionReview && vm.SessionText.Contains("1 ไฟล์"), vm.SessionText);
            string polishedMark = "// tidied by the AI";
            ai.Script = async p =>
            {
                await Task.Delay(600);
                // the AI looks the changes over and saves (here: a harmless mark, like a const it added)
                File.WriteAllText(file, File.ReadAllText(file).Replace("import 'package:flutter/material.dart';", "import 'package:flutter/material.dart';\n" + polishedMark));
                await Task.Delay(800);
            };
            vm.KeepChangesCommand.Execute(null);
            bool checkDone = await WaitUntil(() => vm.HasCheckResult, 420000);
            Check("save check: it ends", checkDone, vm.CheckStatus);
            string checkPrompt = ai.Prompts.LastOrDefault() ?? "";
            Check("save check: the AI is asked to fit what the tools placed, with the round's diff",
                  checkPrompt.Contains("[UI Studio — save check]") && checkPrompt.Contains("+") && checkPrompt.Contains("home_screen.dart") && checkPrompt.Contains("390-wide"), Clip(checkPrompt));
            Check("save check: no problem left — passed, the round is over", vm.CheckOk && !vm.HasSession, $"{vm.CheckStatus} | {string.Join(" | ", vm.CheckFindings)}");
            Check("save check: the AI's tidy-up is kept", File.ReadAllText(file).Contains(polishedMark));
            vm.DismissCheckCommand.Execute(null);

            // ── an AI change drawn live, before / after, undo all ──
            await WaitUntil(() => !vm.IsRendering, 120000);
            string beforeRound = File.ReadAllText(file);
            var liveSeen = false;
            ai.Script = async p =>
            {
                await Task.Delay(400);
                File.WriteAllText(file, File.ReadAllText(file).Replace("color: Color(0xFF2E7D32)", "color: Color(0xFF6A1B9A)"));
                // still "writing": the Studio draws the saved step meanwhile
                DateTime saved = DateTime.UtcNow;
                await WaitUntil(() => store.Get(home.Page.Id, "app")?.CapturedUtc > saved && !vm.IsRendering, 240000);
                liveSeen = vm.AiWorking && Near(JpegAt(store.ImagePath(store.Get(home.Page.Id, "app")!), 0.5, 0.17).C, Color.FromRgb(0x6A, 0x1B, 0x9A));
                await Task.Delay(300);
            };
            vm.StageRequest = "เปลี่ยนสีแถบเป็นสีม่วง";
            vm.AskAiStageCommand.Execute(null);
            Check("live: the request goes to the AI with the screen and the save-as-you-go note", ai.Prompts.Last().Contains("เปลี่ยนสีแถบเป็นสีม่วง") && ai.Prompts.Last().Contains("save each step"), Clip(ai.Prompts.Last()));
            Check("live: the stage says the AI is working", vm.AiWorking && vm.ShowSessionBar);
            await WaitUntil(() => !ai.IsBusy, 300000);
            Check("live: the AI's saved step was drawn while it was still working", liveSeen);
            Check("live: then the change waits for review", vm.SessionReview && vm.HasBeforeImage, vm.SessionText);
            vm.ToggleBeforeCommand.Execute(null);
            var beforeImg = vm.StageRenderShown as BitmapSource;
            var beforeColor = beforeImg != null ? Pixel(beforeImg, 0.5, 0.17) : Colors.Black;
            Check("before/after: the stage shows the screen as it was (green), without boxes", vm.ShowBefore && Near(beforeColor, Color.FromRgb(0x2E, 0x7D, 0x32)) && vm.StageBoxesShown.Count == 0, $"{beforeColor}");
            vm.ToggleBeforeCommand.Execute(null);
            Check("before/after: and back to now", !vm.ShowBefore && vm.StageBoxesShown.Count > 0);
            DateTime t12 = DateTime.UtcNow;
            vm.UndoAllCommand.Execute(null);
            Check("undo all: the file is back as it was before the round", File.ReadAllText(file) == beforeRound && !vm.HasSession);
            drawn = await Redrawn(vm, store, home.Page.Id, t12);
            Check("undo all: drawn again green", drawn && Near(JpegAt(store.ImagePath(store.Get(home.Page.Id, "app")!), 0.5, 0.17).C, Color.FromRgb(0x2E, 0x7D, 0x32)));

            // ── the save check finds a compile error, the AI fixes it ──
            await WaitUntil(() => !vm.IsRendering, 120000);
            string good = File.ReadAllText(file);
            ai.Script = async p =>
            {
                await Task.Delay(300);
                if (p.Contains("[UI Studio — save check]")) File.WriteAllText(file, good.Replace("color: Color(0xFF2E7D32)", "color: Color(0xFFF9A825)"));   // the fix
                else File.WriteAllText(file, good.Replace("color: Color(0xFF2E7D32)", "color: Colours.amber"));                         // a mistake
                await Task.Delay(500);
            };
            vm.StageRequest = "แถบสีเหลือง";
            vm.AskAiStageCommand.Execute(null);
            await WaitUntil(() => !ai.IsBusy, 60000);
            await WaitUntil(() => vm.SessionReview, 60000);
            vm.KeepChangesCommand.Execute(null);
            checkDone = await WaitUntil(() => vm.HasCheckResult, 600000);
            string fixPrompt = ai.Prompts.LastOrDefault(p => p.Contains("[UI Studio — save check]")) ?? "";
            Check("save check: the compile error is found and handed to the AI with the analyzer's words", fixPrompt.Contains("Colours") && fixPrompt.Contains("home_screen.dart:"), Clip(fixPrompt));
            Check("save check: after the AI's fix it passes", checkDone && vm.CheckOk && File.ReadAllText(file).Contains("0xFFF9A825"), $"{vm.CheckStatus} | {string.Join(" | ", vm.CheckFindings)}");
            SavePng((FrameworkElement)view.FindName("Stage"), "stage-checked.png");
        }
        finally
        {
            vm.RenderConsentOverride = null;
            vm.ToolConfirmOverride = null;
            vm.StudioAi = chatAi;   // the next test talks to the real chat again
            CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Remove("flutter.bat");
            CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Remove("dart.bat");
        }
    }

    // ═══════════════════════════════════════════ text passed in through a parameter ═══════════════════════════════════════════

    const string ShopTagDart = @"import 'package:flutter/material.dart';

class ShopTag extends StatelessWidget {
  const ShopTag({super.key, required this.label});

  final String label;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(8),
      child: Text(label),
    );
  }
}
";

    const string ParamHome = @"import 'package:flutter/material.dart';
import 'shop_tag.dart';

class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('หน้าแรก')),
      body: const Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          ShopTag(label: 'ป้ายแรก'),
          ShopTag(label: 'ป้ายที่สอง'),
          _Badge(text: 'ใหม่'),
          _Badge(text: 'ขายดี'),
        ],
      ),
    );
  }
}

class _Badge extends StatelessWidget {
  const _Badge({required this.text});

  final String text;

  @override
  Widget build(BuildContext context) => Text(text);
}
";

    /// <summary>A shared widget (ShopTag, in its own file) used twice with different labels, and a private one (_Badge)
    /// used twice in the screen's own file, on a REAL Flutter app (the flutter-real fixture): picking either use's Text
    /// offers the label THAT use passes and says where it is written; Enter changes only that use's literal (the const
    /// Column stays const), the Text stays picked, Flutter draws again — that use's box gets wider, the other's doesn't
    /// — and Undo puts it back. On the wireframe the shared widget's use is told by the screen line its block names; the
    /// private widget's two uses, with nothing to tell them apart, are refused with the reason.</summary>
    static async Task FlutterParams(UiStudioViewModel vm, UiStudioView view, string fx)
    {
        Log("\n## Text passed in through a widget's parameter, typed over where the widget is used");
        string root = Path.Combine(fx, "flutter-real");
        if (!File.Exists(Path.Combine(root, ".dart_tool", "package_config.json"))) { Log("  (skipped: the real Flutter fixture is not set up)"); return; }
        CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Add("flutter.bat");
        CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Add("dart.bat");
        vm.RenderConsentOverride = true;
        vm.ToolConfirmOverride = true;
        string home = Path.Combine(root, "lib", "home_screen.dart"), tag = Path.Combine(root, "lib", "shop_tag.dart");
        int textLine = LineOf(ShopTagDart, "Text(label)"), badgeLine = LineOf(ParamHome, "Text(text)");
        int use1 = LineOf(ParamHome, "ShopTag(label: 'ป้ายแรก')"), use2 = LineOf(ParamHome, "ShopTag(label: 'ป้ายที่สอง')");
        List<WidgetBox> Tags() => vm.StageBoxes.Where(b => b.Widget == "Text" && b.Line == textLine && b.File.EndsWith("shop_tag.dart", StringComparison.OrdinalIgnoreCase)).OrderBy(b => b.Y).ToList();
        List<WidgetBox> Badges() => vm.StageBoxes.Where(b => b.Widget == "Text" && b.Line == badgeLine && b.File.EndsWith("home_screen.dart", StringComparison.OrdinalIgnoreCase)).OrderBy(b => b.Y).ToList();
        string W0(List<WidgetBox> l, int i) => l.Count > i ? l[i].W.ToString("0") : "-";
        try
        {
            await WaitUntil(() => !vm.IsRendering, 120000);
            var store = (PageShotStore)GetField(vm, "_store")!;
            var card = Card(vm, "/")!;
            File.WriteAllText(tag, ShopTagDart);
            File.WriteAllText(home, ParamHome);
            DateTime t0 = DateTime.UtcNow;
            vm.OpenCardCommand.Execute(card);
            vm.RenderStageCommand.Execute(null);
            bool drawn = await Redrawn(vm, store, card.Page.Id, t0);
            // (the watcher's own redraw of the two saved files may follow: let it end)
            await Idle(1800);
            await WaitUntil(() => !vm.IsRendering, 240000);
            var tags = Tags();
            Check("params: the shared widget is drawn twice from its one Text line", drawn && tags.Count == 2,
                  $"{drawn} {vm.CompileError} · " + string.Join(", ", vm.StageBoxes.Select(b => $"{b.Widget}:{b.Line}")));
            if (tags.Count != 2) return;

            // ── each use offers its own label ──
            vm.PickStageBox(tags[0], open: false);
            Check("params: the first use's Text offers the label that use passes", vm.HasTools && vm.CanToolText && vm.ToolText == "ป้ายแรก", $"can={vm.CanToolText} '{vm.ToolText}' '{vm.ToolTextNote}'");
            Check("params: it says the text is set where the widget is used (file:line)", vm.ToolTextNote.Contains($"lib/home_screen.dart:{use1}"), vm.ToolTextNote);
            await Idle(300);
            SavePng(MainWin.Content as FrameworkElement ?? view, "studio-params.png");
            vm.PickStageBox(tags[1], open: false);
            Check("params: the second use offers its own label and line", vm.CanToolText && vm.ToolText == "ป้ายที่สอง" && vm.ToolTextNote.Contains($"lib/home_screen.dart:{use2}"),
                  $"'{vm.ToolText}' '{vm.ToolTextNote}'");
            var badges = Badges();
            if (badges.Count == 2)
            {
                vm.PickStageBox(badges[1], open: false);
                Check("params: a private widget used twice in the screen's own file — its box tells the uses apart", vm.CanToolText && vm.ToolText == "ขายดี", $"'{vm.ToolText}' '{vm.ToolTextNote}'");
            }
            else Check("params: the private widget is drawn twice", false, $"{badges.Count}");

            // ── the wireframe has no boxes ──
            var sketchTag = vm.StageSketch?.Root.Walk().FirstOrDefault(n => n.Text == "ป้ายที่สอง");
            var sketchBadge = vm.StageSketch?.Root.Walk().FirstOrDefault(n => n.Text == "ใหม่");
            if (sketchTag != null && sketchBadge != null)
            {
                vm.PickStageNode(sketchTag, open: false);
                Check("params (wireframe): the shared widget's use is the screen line its block names", vm.CanToolText && vm.ToolText == "ป้ายที่สอง",
                      $"can={vm.CanToolText} '{vm.ToolText}' '{vm.ToolTextNote}' use={sketchTag.UseFile}:{sketchTag.UseLine}");
                vm.PickStageNode(sketchBadge, open: false);
                Check("params (wireframe): two uses with nothing to tell them apart are refused, with the reason",
                      vm.HasTools && !vm.CanToolText && vm.ToolTextNote == T(vm, "studio.fedit.text.paramMany", 2), $"can={vm.CanToolText} '{vm.ToolTextNote}'");
            }
            else Check("params (wireframe): the blocks are drawn", false, string.Join(", ", vm.StageSketch?.Root.Walk().Select(n => n.Text).Where(t => t != null) ?? Array.Empty<string>()));

            // ── typing over the first use ──
            vm.PickStageBox(Tags()[0], open: false);
            double w1 = Tags()[0].W, w2 = Tags()[1].W;
            string longer = "ป้ายแรกที่ยาวขึ้นอีกหลายคำ";
            vm.ToolText = longer;
            DateTime t1 = DateTime.UtcNow;
            vm.ApplyToolTextCommand.Execute(null);
            Check("params: only that use's literal changed (the const Column stays const)", File.ReadAllText(home) == ParamHome.Replace("ShopTag(label: 'ป้ายแรก')", $"ShopTag(label: '{longer}')"),
                  FirstLine(home, "ShopTag("));
            Check("params: the shared widget's own file is untouched", File.ReadAllText(tag) == ShopTagDart);
            Check("params: the Text stays picked, showing what was written", vm.StageBox?.Line == textLine && vm.CanToolText && vm.ToolText == longer,
                  $"{vm.StageBox?.Widget}:{vm.StageBox?.Line} '{vm.ToolText}'");
            Check("params: the change joins the review round", vm.HasSession && vm.SessionReview, vm.SessionText);
            drawn = await Redrawn(vm, store, card.Page.Id, t1);
            var after = Tags();
            Check("params: drawn again — the edited use's text is wider, the other use's is as it was", drawn && after.Count == 2 && after[0].W > w1 + 40 && Math.Abs(after[1].W - w2) < 1,
                  $"{w1:0} → {W0(after, 0)}, {w2:0} → {W0(after, 1)} {vm.CompileError}");
            Check("params: the same use stays picked on the new picture", after.Count == 2 && ReferenceEquals(vm.StageBox, after[0]) && vm.ToolText == longer,
                  $"picked y={vm.StageBox?.Y:0} '{vm.ToolText}'");
            SavePng((FrameworkElement)view.FindName("Stage"), "stage-params.png");

            DateTime t2 = DateTime.UtcNow;
            vm.UndoCommand.Execute(null);
            Check("undo: the use's literal is back", File.ReadAllText(home) == ParamHome);
            drawn = await Redrawn(vm, store, card.Page.Id, t2);
            var back = Tags();
            Check("undo: drawn again — the first use is as narrow as before", drawn && back.Count == 2 && Math.Abs(back[0].W - w1) < 1, $"{w1:0} → {W0(back, 0)}");
            vm.UndoAllCommand.Execute(null);
            Check("round: undo all ends it with the screen as it began", !vm.HasSession && File.ReadAllText(home) == ParamHome, vm.SessionText);
        }
        finally
        {
            vm.RenderConsentOverride = null;
            vm.ToolConfirmOverride = null;
            CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Remove("flutter.bat");
            CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Remove("dart.bat");
        }
    }

    // ═══════════════════════════════════════════ text passed in through a helper function ═══════════════════════════════════════════

    const string HelperHome = @"import 'package:flutter/material.dart';

class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context) {
    Widget line(String text) => Padding(
          padding: const EdgeInsets.all(4),
          child: Text(text),
        );
    return Scaffold(
      appBar: AppBar(title: const Text('หน้าแรก')),
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          line('บรรทัดแรก'),
          line('บรรทัดที่สอง'),
          line('บรรทัดที่สาม'),
          Row(children: [_cell('ซ้าย'), _cell('ขวา')]),
          const _Mood(),
          const _Mood(mood: 'ดีใจ'),
        ],
      ),
    );
  }

  Widget _cell(String label) => Padding(padding: const EdgeInsets.all(4), child: Text(label));
}

class _Mood extends StatelessWidget {
  const _Mood({this.mood = 'ปกติ'});

  final String mood;

  @override
  Widget build(BuildContext context) => Text(mood);
}
";

    /// <summary>Text a helper function gets through its parameter, and a widget's default, on a REAL Flutter app (the
    /// flutter-real fixture): a function declared in build called three times in one Column and a method called twice in
    /// one Row draw their Text from one line each — the boxes carry the widget tree, and picking any of those Texts
    /// offers the literal THAT call passes and says where it is written. Enter changes only that call, the Text stays
    /// picked, Flutter draws again — that call's box gets wider, the others don't — and Undo puts it back. A widget used
    /// without its text shows its default; typing over it adds the argument at that use only (the const stays).</summary>
    static async Task FlutterHelpers(UiStudioViewModel vm, UiStudioView view, string fx)
    {
        Log("\n## Text passed in through a helper function, and a widget's default, typed over at the call that drew it");
        string root = Path.Combine(fx, "flutter-real");
        if (!File.Exists(Path.Combine(root, ".dart_tool", "package_config.json"))) { Log("  (skipped: the real Flutter fixture is not set up)"); return; }
        CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Add("flutter.bat");
        CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Add("dart.bat");
        vm.RenderConsentOverride = true;
        vm.ToolConfirmOverride = true;
        string home = Path.Combine(root, "lib", "home_screen.dart");
        int lineText = LineOf(HelperHome, "child: Text(text)"), cellText = LineOf(HelperHome, "child: Text(label)"), moodText = LineOf(HelperHome, "=> Text(mood)");
        int call2 = LineOf(HelperHome, "line('บรรทัดที่สอง')"), rowLine = LineOf(HelperHome, "Row(children: [_cell("), mood1 = LineOf(HelperHome, "const _Mood(),");
        List<WidgetBox> Texts(int line) => vm.StageBoxes.Where(b => b.Widget == "Text" && b.Line == line && b.File.EndsWith("home_screen.dart", StringComparison.OrdinalIgnoreCase))
                                                        .OrderBy(b => b.Y).ThenBy(b => b.X).ToList();
        string W0(List<WidgetBox> l, int i) => l.Count > i ? l[i].W.ToString("0") : "-";
        try
        {
            await WaitUntil(() => !vm.IsRendering, 120000);
            var store = (PageShotStore)GetField(vm, "_store")!;
            var card = Card(vm, "/")!;
            File.WriteAllText(home, HelperHome);
            DateTime t0 = DateTime.UtcNow;
            vm.OpenCardCommand.Execute(card);
            vm.RenderStageCommand.Execute(null);
            bool drawn = await Redrawn(vm, store, card.Page.Id, t0);
            // (the watcher's own redraw of the saved file may follow: let it end)
            await Idle(1800);
            await WaitUntil(() => !vm.IsRendering, 240000);
            var lines = Texts(lineText);
            var cells = Texts(cellText);
            var moods = Texts(moodText);
            Check("helpers: a function declared in build draws its Text three times, a method twice — one line of code each",
                  drawn && lines.Count == 3 && cells.Count == 2 && moods.Count == 2, $"{drawn} {vm.CompileError} · {lines.Count} {cells.Count} {moods.Count}");
            if (lines.Count != 3 || cells.Count != 2 || moods.Count != 2) return;
            Check("helpers: every box knows the box around it (the renderer records Flutter's widget tree)",
                  vm.StageBoxes.All(b => b.Id >= 0) && lines.All(b => b.Parent >= 0) && lines.Select(b => b.Parent).Distinct().Count() == 3,
                  string.Join(", ", lines.Select(b => $"{b.Id}<{b.Parent}")));

            // ── each call offers its own text ──
            vm.PickStageBox(lines[1], open: false);
            Check("helpers: the second line() call's Text offers what that call passes", vm.HasTools && vm.CanToolText && vm.ToolText == "บรรทัดที่สอง",
                  $"can={vm.CanToolText} '{vm.ToolText}' '{vm.ToolTextNote}'");
            Check("helpers: it says the text is set where line() is called (file:line)", vm.ToolTextNote == T(vm, "studio.fedit.text.helperParam", "line", $"lib/home_screen.dart:{call2}"), vm.ToolTextNote);
            await Idle(300);
            SavePng(MainWin.Content as FrameworkElement ?? view, "studio-helpers.png");
            vm.PickStageBox(lines[2], open: false);
            Check("helpers: the third call's Text offers its own text", vm.CanToolText && vm.ToolText == "บรรทัดที่สาม", $"'{vm.ToolText}' '{vm.ToolTextNote}'");
            vm.PickStageBox(cells[1], open: false);
            Check("helpers: a method called twice in one Row — the right-hand call's text", vm.CanToolText && vm.ToolText == "ขวา" && vm.ToolTextNote.Contains($"lib/home_screen.dart:{rowLine}"),
                  $"'{vm.ToolText}' '{vm.ToolTextNote}'");
            vm.PickStageBox(moods[0], open: false);
            Check("default: a use that leaves the text out offers the widget's default", vm.CanToolText && vm.ToolText == "ปกติ", $"can={vm.CanToolText} '{vm.ToolText}' '{vm.ToolTextNote}'");
            Check("default: it says Enter adds it at that use", vm.ToolTextNote == T(vm, "studio.fedit.text.paramAdd", $"lib/home_screen.dart:{mood1}"), vm.ToolTextNote);
            vm.PickStageBox(moods[1], open: false);
            Check("default: … the use that passes one offers that", vm.CanToolText && vm.ToolText == "ดีใจ", vm.ToolText);

            // ── typing over the second call ──
            vm.PickStageBox(Texts(lineText)[1], open: false);
            double w0 = Texts(lineText)[0].W, w1 = Texts(lineText)[1].W, w2 = Texts(lineText)[2].W;
            string longer = "บรรทัดที่สองที่ยาวขึ้นอีกหลายคำ";
            vm.ToolText = longer;
            DateTime t1 = DateTime.UtcNow;
            vm.ApplyToolTextCommand.Execute(null);
            Check("helpers: only that call's literal changed", File.ReadAllText(home) == HelperHome.Replace("line('บรรทัดที่สอง')", $"line('{longer}')"), FirstLine(home, "line('บรรทัดที่ส"));
            Check("helpers: the Text stays picked, showing what was written", vm.StageBox?.Line == lineText && vm.CanToolText && vm.ToolText == longer,
                  $"{vm.StageBox?.Widget}:{vm.StageBox?.Line} '{vm.ToolText}'");
            Check("helpers: the change joins the review round", vm.HasSession && vm.SessionReview, vm.SessionText);
            // typed again at once, before Flutter has drawn it: the tools read the call again (not refused as changed meanwhile)
            string longest = longer + "จริงๆ";
            vm.ToolText = longest;
            DateTime t1b = DateTime.UtcNow;
            vm.ApplyToolTextCommand.Execute(null);
            Check("helpers: a second change before the picture is drawn again is written too", File.ReadAllText(home) == HelperHome.Replace("line('บรรทัดที่สอง')", $"line('{longest}')"),
                  $"{FirstLine(home, "line('บรรทัดที่ส")} · {vm.StatusText}");
            drawn = await Redrawn(vm, store, card.Page.Id, t1b);
            await WaitUntil(() => !vm.IsRendering, 240000);
            var after = Texts(lineText);
            Check("helpers: drawn again — that call's text is wider, the other two are as they were",
                  drawn && after.Count == 3 && after[1].W > w1 + 40 && Math.Abs(after[0].W - w0) < 1 && Math.Abs(after[2].W - w2) < 1,
                  $"{w0:0}/{w1:0}/{w2:0} → {W0(after, 0)}/{W0(after, 1)}/{W0(after, 2)} {vm.CompileError}");
            Check("helpers: the same call's Text stays picked on the new picture", after.Count == 3 && ReferenceEquals(vm.StageBox, after[1]) && vm.ToolText == longest,
                  $"picked y={vm.StageBox?.Y:0} '{vm.ToolText}'");
            SavePng((FrameworkElement)view.FindName("Stage"), "stage-helpers.png");

            vm.UndoCommand.Execute(null);
            Check("undo: one step back — the first change", File.ReadAllText(home) == HelperHome.Replace("line('บรรทัดที่สอง')", $"line('{longer}')"), FirstLine(home, "line('บรรทัดที่ส"));
            DateTime t2 = DateTime.UtcNow;
            vm.UndoCommand.Execute(null);
            Check("undo: the call's literal is back", File.ReadAllText(home) == HelperHome);
            drawn = await Redrawn(vm, store, card.Page.Id, t2);
            var back = Texts(lineText);
            Check("undo: drawn again — the second line is as narrow as before", drawn && back.Count == 3 && Math.Abs(back[1].W - w1) < 1, $"{w1:0} → {W0(back, 1)}");

            // ── a default: typing over it adds the argument at that use ──
            vm.PickStageBox(Texts(moodText)[0], open: false);
            double m0 = Texts(moodText)[0].W, m1 = Texts(moodText)[1].W;
            string mood = "อารมณ์ดีมากเป็นพิเศษวันนี้";
            vm.ToolText = mood;
            DateTime t3 = DateTime.UtcNow;
            vm.ApplyToolTextCommand.Execute(null);
            Check("default: the argument is added at that use only — the const stays", File.ReadAllText(home) == HelperHome.Replace("const _Mood(),", $"const _Mood(mood: '{mood}'),"),
                  FirstLine(home, "const _Mood("));
            Check("default: the Text stays picked, its use now passing what was written", vm.CanToolText && vm.ToolText == mood && vm.ToolTextNote == T(vm, "studio.fedit.text.param", $"lib/home_screen.dart:{mood1}"),
                  $"'{vm.ToolText}' '{vm.ToolTextNote}'");
            drawn = await Redrawn(vm, store, card.Page.Id, t3);
            var moodsAfter = Texts(moodText);
            Check("default: drawn again — that use's text is wider, the other use's isn't", drawn && moodsAfter.Count == 2 && moodsAfter[0].W > m0 + 40 && Math.Abs(moodsAfter[1].W - m1) < 1,
                  $"{m0:0}/{m1:0} → {W0(moodsAfter, 0)}/{W0(moodsAfter, 1)} {vm.CompileError}");
            DateTime t4 = DateTime.UtcNow;
            vm.UndoCommand.Execute(null);
            Check("undo: the use leaves it out again", File.ReadAllText(home) == HelperHome);
            drawn = await Redrawn(vm, store, card.Page.Id, t4);
            var moodsBack = Texts(moodText);
            Check("undo: drawn again with the default", drawn && moodsBack.Count == 2 && Math.Abs(moodsBack[0].W - m0) < 1, $"{m0:0} → {W0(moodsBack, 0)}");
            vm.UndoAllCommand.Execute(null);
            Check("round: undo all ends it with the screen as it began", !vm.HasSession && File.ReadAllText(home) == HelperHome, vm.SessionText);
        }
        finally
        {
            vm.RenderConsentOverride = null;
            vm.ToolConfirmOverride = null;
            CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Remove("flutter.bat");
            CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Remove("dart.bat");
        }
    }

    // ═══════════════════════════════════════════ a screen that loads data ═══════════════════════════════════════════

    const string ShopApiDart = @"import 'dart:convert';
import 'dart:io';

Map<String, dynamic> _m(dynamic v) => v is Map ? v.cast<String, dynamic>() : const {};

/// The app's client: GETs a path and gives back what the reply's envelope carries ({ success, data }).
class Api {
  Api(this.base);
  final String base;

  Future<dynamic> get(String path) async {
    final client = HttpClient();
    final req = await client.getUrl(Uri.parse('$base$path'));
    final res = await req.close();
    final body = _m(json.decode(await res.transform(utf8.decoder).join()));
    if (res.statusCode != 200 || body['success'] != true) {
      throw Exception('โหลดไม่สำเร็จ (${res.statusCode})');
    }
    return body['data'];
  }
}

class ShopStats {
  const ShopStats(this.orders, this.revenue, this.items);
  final int orders;
  final double revenue;
  final List<ShopItem> items;

  factory ShopStats.fromJson(Map<String, dynamic> j) => ShopStats(
        (j['orders_count'] as num? ?? 0).toInt(),
        (j['revenue_thb'] as num? ?? 0).toDouble(),
        [for (final e in (j['items'] as List? ?? const [])) ShopItem.fromJson(_m(e))],
      );
}

class ShopItem {
  const ShopItem(this.title, this.sold);
  final String title;
  final int sold;

  factory ShopItem.fromJson(Map<String, dynamic> j) => ShopItem(j['title'] as String? ?? '', (j['sold'] as num? ?? 0).toInt());
}

class ShopRepository {
  final _api = Api('https://shop.example.test/api');

  Future<ShopStats> stats() async {
    final data = await _api.get('/shop/stats');
    return ShopStats.fromJson(_m(data));
  }
}
";

    const string ShopScreenDart = @"import 'package:flutter/material.dart';
import 'shop_api.dart';

class ShopScreen extends StatefulWidget {
  const ShopScreen({super.key});

  @override
  State<ShopScreen> createState() => _ShopScreenState();
}

class _ShopScreenState extends State<ShopScreen> {
  late final Future<ShopStats> _stats = ShopRepository().stats();

  @override
  Widget build(BuildContext context) {
    Widget line(String label, String value) => Padding(
          padding: const EdgeInsets.all(4),
          child: Row(children: [Text(label), const SizedBox(width: 8), Text(value)]),
        );
    return Scaffold(
      appBar: AppBar(title: const Text('ร้านค้า')),
      body: FutureBuilder<ShopStats>(
        future: _stats,
        builder: (context, snap) {
          if (snap.hasError) return Center(child: Text('${snap.error}'));
          final s = snap.data;
          if (s == null) return const Center(child: Text('กำลังโหลด'));
          return Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              line('ออเดอร์', '${s.orders}'),
              line('ยอดขาย', s.revenue.toStringAsFixed(2)),
              for (final it in s.items) Text(it.title),
            ],
          );
        },
      ),
    );
  }
}
";

    /// <summary>A screen that loads its data over HTTP — through the app's own client and envelope, the way
    /// thaipromptadminapp does — drawn for real: the renderer feeds the request the sample reply read from the app's
    /// code (sample_data.json beside the generated test), so the screen draws its loaded state (a row per sample item,
    /// the amounts) instead of its error; the card says it was drawn with sample data; and a helper's text on it can be
    /// picked and typed over like any other.</summary>
    static async Task FlutterSampleScreens(UiStudioViewModel vm, UiStudioView view, string fx)
    {
        Log("\n## A screen that loads data, drawn with sample replies read from the app's own code");
        string? flutter = FlutterRenderer.FindFlutter();
        if (flutter == null) { Log("  (skipped: Flutter is not installed on this PC)"); return; }
        string root = Path.Combine(fx, "flutter-data");
        W(root, "pubspec.yaml", "name: cxdata\nenvironment:\n  sdk: '>=3.0.0 <4.0.0'\ndependencies:\n  flutter:\n    sdk: flutter\ndev_dependencies:\n  flutter_test:\n    sdk: flutter\nflutter:\n  uses-material-design: true\n");
        W(root, "lib/main.dart", "import 'package:flutter/material.dart';\nimport 'shop_screen.dart';\n\nvoid main() => runApp(const MaterialApp(home: ShopScreen()));\n");
        W(root, "lib/shop_api.dart", ShopApiDart);
        W(root, "lib/shop_screen.dart", ShopScreenDart);
        var pub = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{flutter}\" pub get --offline\"")
        { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var pubOut = pub.StandardOutput.ReadToEndAsync();
        var pubErr = pub.StandardError.ReadToEndAsync();
        await pub.WaitForExitAsync();
        if (pub.ExitCode != 0 || !File.Exists(Path.Combine(root, ".dart_tool", "package_config.json")))
        {
            Log("  (skipped: `flutter pub get --offline` could not resolve the SDK's packages from the cache) " + (await pubErr).Trim().Split('\n').LastOrDefault());
            return;
        }
        _ = await pubOut;

        CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Add("flutter.bat");
        CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Add("dart.bat");
        vm.RenderConsentOverride = true;
        vm.ToolConfirmOverride = true;
        int titleLine = LineOf(ShopScreenDart, "Text(it.title)"), errorLine = LineOf(ShopScreenDart, "Text('${snap.error}')"), labelLine = LineOf(ShopScreenDart, "Row(children: [Text(label)");
        int revenueCall = LineOf(ShopScreenDart, "line('ยอดขาย'");
        // (Text(label) and Text(value) share that line: the label's column tells them apart)
        int at = ShopScreenDart.IndexOf("[Text(label)", StringComparison.Ordinal) + 1;
        int labelCol = at - ShopScreenDart.LastIndexOf('\n', at);
        try
        {
            vm.OpenProject(root, autoStart: true);
            bool drawn = await WaitUntil(() => Cards(vm).Count == 1 && Cards(vm).All(c => c.State == "done" && c.HasThumb), 300000);
            Check("sample: Flutter draws the data screen", drawn, string.Join(", ", Cards(vm).Select(c => $"{c.Page.Detail}:{c.State} '{c.StateText}'")) + (vm.CompileError != null ? " COMPILE " + vm.CompileError : ""));
            if (!drawn) return;
            var card = Cards(vm)[0];
            string samples = Path.Combine(root, ".dart_tool", "cluadex_studio", FlutterSampleData.FileName);
            string fed = File.Exists(samples) ? File.ReadAllText(samples) : "";
            Check("sample: the reply for /shop/stats was read from the app's code, in its envelope, beside the generated test",
                  fed.Contains("\"pattern\":\"(^|/)shop/stats/?$\"", StringComparison.Ordinal) && fed.Contains("\"success\":true", StringComparison.Ordinal) && fed.Contains("\"items\":[", StringComparison.Ordinal),
                  fed.Length > 400 ? fed[..400] : fed);
            Check("sample: the card says the screen was drawn with sample data", card.StateText.Contains(T(vm, "studio.render.drawnSample", "", 1).Split('·')[1].Trim(), StringComparison.Ordinal), card.StateText);
            vm.OpenCardCommand.Execute(card);
            await Idle(400);
            var titles = vm.StageBoxes.Where(b => b.Widget == "Text" && b.Line == titleLine).ToList();
            Check("sample: the screen drew its loaded state — a row for each of the three sample items", titles.Count == 3,
                  $"{titles.Count} · " + string.Join(", ", vm.StageBoxes.Where(b => b.File.EndsWith("shop_screen.dart", StringComparison.OrdinalIgnoreCase)).Select(b => $"{b.Widget}:{b.Line}")));
            Check("sample: … not its error", !vm.StageBoxes.Any(b => b.Line == errorLine && b.File.EndsWith("shop_screen.dart", StringComparison.OrdinalIgnoreCase)));
            var store = (PageShotStore)GetField(vm, "_store")!;
            string side = store.ReadSidecar(store.Get(card.Page.Id, "app")!) ?? "";
            Check("sample: the screen's request was answered (kept with the picture)", side.Contains("\"GET /api/shop/stats\"", StringComparison.Ordinal), side.Length > 300 ? side[^300..] : side);
            await Idle(300);
            SavePng((FrameworkElement)view.FindName("Stage"), "stage-sample.png");

            // a helper's text on a data screen is picked and offered like any other
            var labels = vm.StageBoxes.Where(b => b.Widget == "Text" && b.Line == labelLine && b.Column == labelCol).OrderBy(b => b.Y).ToList();
            if (labels.Count == 2)
            {
                vm.PickStageBox(labels[1], open: false);
                Check("sample: a helper's label on the data screen offers what its call passes", vm.CanToolText && vm.ToolText == "ยอดขาย" && vm.ToolTextNote.Contains($"lib/shop_screen.dart:{revenueCall}"),
                      $"'{vm.ToolText}' '{vm.ToolTextNote}'");
            }
            else Check("sample: the helper drew its two labels", false, $"{labels.Count}");

            await SampleSwitchAndAi(vm, view, root, card, store, samples, titleLine, errorLine);
        }
        finally
        {
            vm.RenderConsentOverride = null;
            vm.ToolConfirmOverride = null;
            CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Remove("flutter.bat");
            CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Remove("dart.bat");
        }
    }

    /// <summary>The owner's control over the sample data, on the same real data screen: the stage's switch
    /// ("ข้อมูลตัวอย่าง: เปิด/ปิด", kept in the project's PageShotStore state) — off draws the screen's error text, on its
    /// rows again; then "ให้ AI ทำข้อมูลให้สมจริง" with a scripted AI: it gets the requests, their models' files and the
    /// sample replies, writes realistic Thai values into the Studio's own store (never the app), and the screen is drawn
    /// again with them merged in (four rows, the AI's titles); asked again it asks first; and the AI's values can be
    /// deleted (asked first) to draw the made-up ones again.</summary>
    static async Task SampleSwitchAndAi(UiStudioViewModel vm, UiStudioView view, string root, PageCard card, PageShotStore store, string samples, int titleLine, int errorLine)
    {
        var stage = (FrameworkElement)view.FindName("Stage");
        bool Screen(string file) => file.EndsWith("shop_screen.dart", StringComparison.OrdinalIgnoreCase);
        int Rows() => vm.StageBoxes.Count(b => b.Widget == "Text" && b.Line == titleLine && Screen(b.File));
        bool ErrorShown() => vm.StageBoxes.Any(b => b.Line == errorLine && Screen(b.File));

        // ── the switch ──
        Check("switch: the sample-data bar shows for an app whose code makes requests — on by default",
              await WaitUntil(() => vm.ShowSampleBar, 30000) && vm.SampleDataOn && vm.SampleRequestCount == 1 && !store.State.SampleDataOff, $"{vm.ShowSampleBar} {vm.SampleRequestCount}");
        await Idle(200);
        var bar = (FrameworkElement)view.FindName("SampleBar");
        var texts = Find<System.Windows.Controls.TextBlock>(bar).Select(t => t.Text).ToList();
        Check("switch: the bar reads ข้อมูลตัวอย่าง: เปิด | ปิด, with the AI's button and what the requests get, in Thai",
              bar.IsVisible && texts.Contains("ข้อมูลตัวอย่าง:") && texts.Contains("เปิด") && texts.Contains("ปิด") && texts.Contains("ให้ AI ทำข้อมูลให้สมจริง") && texts.Contains("ตอบ 1 คำขอด้วยข้อมูลตัวอย่าง"),
              string.Join(" | ", texts));
        var radios = Find<System.Windows.Controls.RadioButton>(bar).ToList();
        if (radios.Count != 2) { Check("switch: two segments", false, $"{radios.Count}"); return; }
        // (the helper's label picked above is a row of data: it won't be on the error screen)
        var picked = vm.StageBox;
        DateTime t1 = DateTime.UtcNow;
        radios[1].IsChecked = true;   // the ปิด segment, as a click sets it
        Check("switch: ปิด turns it off — kept in the project's PageShotStore state",
              !vm.SampleDataOn && store.State.SampleDataOff && File.ReadAllText(Path.Combine(store.Folder, "index.json")).Contains("\"SampleDataOff\":true", StringComparison.Ordinal));
        bool drawn = await Redrawn(vm, store, card.Page.Id, t1);
        await Idle(300);
        string fed = File.Exists(samples) ? File.ReadAllText(samples) : "";
        Check("switch: off — the screen is drawn again, the generated test is given no sample reply", drawn && fed == "[]", fed.Length > 120 ? fed[..120] : fed);
        Check("switch: off — the screen draws its error text, no sample row", ErrorShown() && Rows() == 0,
              string.Join(", ", vm.StageBoxes.Where(b => Screen(b.File)).Select(b => $"{b.Widget}:{b.Line}")));
        Check("switch: off — the label picked on the data rows isn't drawn now: no pick is left marking another place", picked != null && vm.StageBox == null && !vm.HasTools,
              $"{picked?.Widget}:{picked?.Line} → {vm.StageBox?.Widget}:{vm.StageBox?.Line}");
        string side = store.ReadSidecar(store.Get(card.Page.Id, "app")!) ?? "";
        Check("switch: off — the request went unanswered; the card and the note under the stage say sample data is off",
              side.Contains("\"GET /api/shop/stats -\"", StringComparison.Ordinal) && side.Contains("\"samples\":\"off\"", StringComparison.Ordinal)
              && card.StateText.Contains("ปิดข้อมูลตัวอย่าง", StringComparison.Ordinal) && Find<System.Windows.Controls.TextBlock>(stage).Any(t => t.Text == T(vm, "studio.render.realNoteNoSample"))
              && vm.SampleSummary == T(vm, "studio.samples.summaryOff") && radios[1].IsChecked == true && radios[0].IsChecked == false,
              $"'{card.StateText}' · {(side.Length > 200 ? side[^200..] : side)}");
        SavePng(stage, "stage-sample-off.png");
        Check("switch: the AI's button waits while sample data is off", !vm.MakeSamplesRealCommand.CanExecute(null));

        DateTime t2 = DateTime.UtcNow;
        vm.SampleDataOn = true;
        drawn = await Redrawn(vm, store, card.Page.Id, t2);
        await Idle(300);
        Check("switch: on — drawn again with the three sample rows, no error text", drawn && Rows() == 3 && !ErrorShown() && !store.State.SampleDataOff && radios[0].IsChecked == true,
              $"{Rows()} rows, error={ErrorShown()}");

        // ── realistic values by the AI (a scripted stand-in: it writes the file the way the chat's AI would) ──
        var ai = new ScriptedAi();
        var chatAi = vm.StudioAi;
        vm.StudioAi = ai;
        string apiFile = Path.Combine(root, "lib", "shop_api.dart"), screenFile = Path.Combine(root, "lib", "shop_screen.dart");
        string api = File.ReadAllText(apiFile), screen = File.ReadAllText(screenFile);
        string? wrote = null;
        try
        {
            ai.Script = async p =>
            {
                await Task.Delay(800);
                wrote = System.Text.RegularExpressions.Regex.Match(p, @"Write exactly one file: (?<f>[^\r\n]+)").Groups["f"].Value.Trim();
                File.WriteAllText(wrote, "{\n  \"GET /shop/stats\": {\"data\": {\"orders_count\": 48, \"revenue_thb\": 15890, \"items\": [" +
                                         "{\"title\": \"ข้าวมันไก่ตอน\", \"sold\": 31}, {\"title\": \"ชาไทยเย็น\", \"sold\": 27}, {\"title\": \"ขนมครก\", \"sold\": 12}, {\"title\": \"ผัดไทยกุ้งสด\", \"sold\": \"สิบ\"}]}}\n}\n");
                await Task.Delay(400);
            };
            Check("ai: the button is ready — sample data on, a request to make realistic", vm.MakeSamplesRealCommand.CanExecute(null));
            DateTime t3 = DateTime.UtcNow;
            vm.MakeSamplesRealCommand.Execute(null);
            await WaitUntil(() => ai.Prompts.Count > 0, 20000);
            string prompt = ai.Prompts.LastOrDefault() ?? "";
            Check("ai: the AI gets the requests, their models' files, the sample replies and the one file to write — in the Studio's store",
                  prompt.Contains("[UI Studio — sample data]", StringComparison.Ordinal) && prompt.Contains("- GET /shop/stats → ShopStats · lib/shop_api.dart · lib/shop_api.dart:", StringComparison.Ordinal)
                  && prompt.Contains("\"GET /shop/stats\": {\"success\":true,\"data\":{\"orders_count\":12,", StringComparison.Ordinal)
                  && prompt.Contains("Write exactly one file: " + store.SampleOverridesPath, StringComparison.Ordinal) && prompt.Contains("Do not create or change any file of the app", StringComparison.Ordinal),
                  Clip(prompt));
            Check("ai: the stage says the AI is writing the sample data", vm.AiWorking && vm.IsMakingSamples && vm.SessionText == T(vm, "studio.samples.working") && vm.SampleSummary == T(vm, "studio.samples.working"),
                  $"{vm.AiWorking} {vm.IsMakingSamples} '{vm.SessionText}'");
            bool done = await WaitUntil(() => vm.SampleOverrideCount == 1 && !vm.IsMakingSamples, 60000);
            Check("ai: when it is done the file is checked — one request made realistic, one value of another kind kept its sample",
                  done && vm.StatusText == T(vm, "studio.samples.doneKept", 1, 1) && vm.HasSampleOverrides, $"{vm.SampleOverrideCount} '{vm.StatusText}'");
            Check("ai: the file is in the Studio's own store, not in the app",
                  wrote == store.SampleOverridesPath && File.Exists(wrote) && !wrote!.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.Combine(root, PageShotStore.SampleOverridesFile)), wrote ?? "(none)");
            drawn = await Redrawn(vm, store, card.Page.Id, t3);
            await Idle(300);
            fed = File.Exists(samples) ? File.ReadAllText(samples) : "";
            Check("ai: drawn again with the AI's values merged in — four rows now", drawn && Rows() == 4, $"{Rows()} rows");
            Check("ai: what the test fed is the merged reply — the AI's Thai titles, the whole amount as a fraction, the word for a number left out",
                  fed.Contains("ข้าวมันไก่ตอน", StringComparison.Ordinal) && fed.Contains("\"revenue_thb\":15890.0", StringComparison.Ordinal) && !fed.Contains("สิบ", StringComparison.Ordinal)
                  && fed.Contains("\"sold\":22", StringComparison.Ordinal), fed.Length > 400 ? fed[..400] : fed);
            Check("ai: the bar counts the AI's values; the card still says sample data",
                  vm.SampleSummary == T(vm, "studio.samples.summaryAi", 1, 1) && card.StateText.Contains("ใช้ข้อมูลตัวอย่าง", StringComparison.Ordinal), $"'{vm.SampleSummary}' '{card.StateText}'");
            Check("ai: the app's files are untouched and no round is left to review", File.ReadAllText(apiFile) == api && File.ReadAllText(screenFile) == screen && !vm.HasSession && !vm.ShowSessionBar);
            SavePng(stage, "stage-sample-ai.png");
            SavePng(MainWin.Content as FrameworkElement ?? view, "studio-sample-ai.png");

            // asked again: the AI's values are there — it asks before they are written over
            vm.ToolConfirmOverride = false;
            int asked = ai.Prompts.Count;
            vm.MakeSamplesRealCommand.Execute(null);
            await Idle(1500);
            Check("ai: asked again it asks first — declined, nothing goes to the AI", ai.Prompts.Count == asked && File.Exists(store.SampleOverridesPath));

            // back to the made-up values: asked first, then the file goes and the screen is drawn again
            vm.ClearSampleOverridesCommand.Execute(null);
            Check("clear: declined — the AI's values stay", File.Exists(store.SampleOverridesPath) && vm.HasSampleOverrides);
            vm.ToolConfirmOverride = true;
            DateTime t4 = DateTime.UtcNow;
            vm.ClearSampleOverridesCommand.Execute(null);
            Check("clear: confirmed — the AI's file is deleted", !File.Exists(store.SampleOverridesPath) && !vm.HasSampleOverrides && vm.StatusText == T(vm, "studio.samples.cleared"));
            drawn = await Redrawn(vm, store, card.Page.Id, t4);
            await Idle(300);
            Check("clear: drawn again with the made-up values — three rows", drawn && Rows() == 3 && vm.SampleOverrideCount == 0, $"{Rows()} rows");
        }
        finally { vm.StudioAi = chatAi; }
    }

    // ═══════════════════════════════════════════ translated text ═══════════════════════════════════════════

    const string L10nThArb = "{\n  \"@@locale\": \"th\",\n  \"welcome\": \"ยินดีต้อนรับสู่แอปพลิเคชันของเรา\",\n  \"@welcome\": { \"description\": \"The greeting on the home screen\" },\n\n" +
                             "  \"greeting\": \"สวัสดี {name}\",\n  \"@greeting\": { \"placeholders\": { \"name\": {} } }\n}\n";
    const string L10nEnArb = "{\n  \"@@locale\": \"en\",\n  \"welcome\": \"Welcome to our application\",\n  \"greeting\": \"Hello {name}\"\n}\n";

    const string L10nHome = @"import 'package:flutter/material.dart';
import 'l10n/app_localizations.dart';

extension L10nX on BuildContext {
  AppL10n get l10n => AppL10n.of(this);
}

class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      body: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const SizedBox(height: 40),
          Text(context.l10n.welcome),
          Text(context.l10n.greeting('Ann')),
        ],
      ),
    );
  }
}
";

    /// <summary>A real Flutter app whose texts come from gen-l10n (l10n.yaml, app_th.arb as the template, app_en.arb):
    /// the text tool shows a translated Text's Thai message, writes the new one into app_th.arb only (exactly that
    /// string), Flutter draws the screen again after `flutter gen-l10n` (the box gets narrower), gen-l10n's own output
    /// doesn't draw it once more, a placeholder message is refused, and Undo puts the .arb back.</summary>
    static async Task FlutterTranslations(UiStudioViewModel vm, UiStudioView view, string fx)
    {
        Log("\n## Translated text (.arb) edited with the text tool, drawn for real");
        string? flutter = FlutterRenderer.FindFlutter();
        if (flutter == null) { Log("  (skipped: Flutter is not installed on this PC)"); return; }
        string root = Path.Combine(fx, "flutter-l10n");
        W(root, "pubspec.yaml", "name: cxl10n\nenvironment:\n  sdk: '>=3.0.0 <4.0.0'\ndependencies:\n  flutter:\n    sdk: flutter\n  flutter_localizations:\n    sdk: flutter\n  intl: any\n" +
                                "dev_dependencies:\n  flutter_test:\n    sdk: flutter\nflutter:\n  uses-material-design: true\n  generate: true\n");
        // no output-dir: Flutter 3.32+ writes the code next to the .arb files (lib/l10n/app_localizations.dart)
        W(root, "l10n.yaml", "arb-dir: lib/l10n\ntemplate-arb-file: app_th.arb\noutput-localization-file: app_localizations.dart\noutput-class: AppL10n\nnullable-getter: false\n");
        W(root, "lib/l10n/app_th.arb", L10nThArb);
        W(root, "lib/l10n/app_en.arb", L10nEnArb);
        W(root, "lib/main.dart", "import 'package:flutter/material.dart';\nimport 'l10n/app_localizations.dart';\nimport 'home_screen.dart';\n\n" +
                                 "void main() => runApp(const MaterialApp(\n      localizationsDelegates: AppL10n.localizationsDelegates,\n      supportedLocales: AppL10n.supportedLocales,\n      home: HomeScreen(),\n    ));\n");
        W(root, "lib/home_screen.dart", L10nHome);
        var pub = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{flutter}\" pub get --offline\"")
        { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var pubOut = pub.StandardOutput.ReadToEndAsync();
        var pubErr = pub.StandardError.ReadToEndAsync();
        await pub.WaitForExitAsync();
        if (pub.ExitCode != 0 || !File.Exists(Path.Combine(root, ".dart_tool", "package_config.json")))
        {
            Log("  (skipped: `flutter pub get --offline` could not resolve flutter_localizations / intl from the cache) " + (await pubErr).Trim().Split('\n').LastOrDefault());
            return;
        }
        _ = await pubOut;

        CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Add("flutter.bat");
        CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Add("dart.bat");
        vm.RenderConsentOverride = true;
        vm.ToolConfirmOverride = true;
        string th = Path.Combine(root, "lib", "l10n", "app_th.arb"), en = Path.Combine(root, "lib", "l10n", "app_en.arb");
        int welcomeLine = LineOf(L10nHome, "Text(context.l10n.welcome)"), greetingLine = LineOf(L10nHome, "Text(context.l10n.greeting");
        try
        {
            vm.OpenProject(root, autoStart: true);
            bool drawn = await WaitUntil(() => Cards(vm).Count == 1 && Cards(vm).All(c => c.State == "done" && c.HasThumb), 300000);
            Check("l10n: Flutter draws the app with its translations", drawn,
                  string.Join(", ", Cards(vm).Select(c => $"{c.Page.Route}:{c.State} '{c.StateText}'")) + (vm.CompileError != null ? " COMPILE " + vm.CompileError : ""));
            if (!drawn) return;
            var store = (PageShotStore)GetField(vm, "_store")!;
            // (home: with no routes table — the gallery lists it by its class, without a route)
            var home = Cards(vm).FirstOrDefault(c => c.Page.Detail == "HomeScreen");
            if (home == null) { Check("l10n: the home screen has a card", false, string.Join(", ", Cards(vm).Select(c => c.Page.Id))); return; }
            vm.OpenCardCommand.Execute(home);
            await Idle(400);
            var welcome = Box(vm, "Text", welcomeLine);
            if (welcome == null) { Check("l10n: the welcome text has a box", false, string.Join(", ", vm.StageBoxes.Select(b => $"{b.Widget}:{b.Line}"))); return; }

            // a placeholder message is not typed over
            if (Box(vm, "Text", greetingLine) is { } greeting)
            {
                vm.PickStageBox(greeting, open: false);
                Check("l10n: a message with a placeholder is refused, with its reason", vm.HasTools && !vm.CanToolText && vm.ToolTextNote == T(vm, "studio.fedit.text.l10nPlaceholder"), $"can={vm.CanToolText} '{vm.ToolTextNote}'");
            }
            else Check("l10n: the greeting text has a box", false);

            vm.PickStageBox(welcome, open: false);
            Check("l10n: a translated Text offers its Thai message to type over", vm.HasTools && vm.CanToolText && vm.ToolText == "ยินดีต้อนรับสู่แอปพลิเคชันของเรา", $"can={vm.CanToolText} '{vm.ToolText}'");
            Check("l10n: it says where the change goes (the key, app_th.arb)", vm.ToolTextNote.Contains("welcome") && vm.ToolTextNote.Contains("app_th.arb"), vm.ToolTextNote);
            double oldW = welcome.W;
            string dart = File.ReadAllText(Path.Combine(root, "lib", "home_screen.dart"));
            vm.ToolText = "สวัสดี";
            DateTime t1 = DateTime.UtcNow;
            vm.ApplyToolTextCommand.Execute(null);
            string thNow = File.ReadAllText(th);
            Check("l10n: exactly that message changed in app_th.arb", thNow == L10nThArb.Replace("\"welcome\": \"ยินดีต้อนรับสู่แอปพลิเคชันของเรา\"", "\"welcome\": \"สวัสดี\""), thNow);
            Check("l10n: the file is still strict JSON", StrictJsonOk(thNow));
            Check("l10n: app_en.arb and the screen's code are untouched", File.ReadAllText(en) == L10nEnArb && File.ReadAllText(Path.Combine(root, "lib", "home_screen.dart")) == dart);
            Check("l10n: the box shows what was written, the Text stays picked", vm.ToolText == "สวัสดี" && vm.StageBox?.Line == welcomeLine && vm.CanToolText, $"'{vm.ToolText}' line {vm.StageBox?.Line}");
            Check("l10n: the change joins the review round", vm.HasSession && vm.SessionReview, vm.SessionText);
            drawn = await Redrawn(vm, store, home.Page.Id, t1);
            var welcome2 = Box(vm, "Text", welcomeLine);
            Check("l10n: drawn again after gen-l10n — the shorter text has a narrower box", drawn && welcome2 != null && welcome2.W < oldW - 20, $"{oldW:0} → {welcome2?.W:0} {vm.CompileError}");
            Check("l10n: the same Text is picked on the new picture", welcome2 != null && ReferenceEquals(vm.StageBox, welcome2) && vm.ToolText == "สวัสดี");
            string generated = Path.Combine(root, "lib", "l10n", "app_localizations_th.dart");
            Check("l10n: flutter gen-l10n made the Thai class again", File.Exists(generated) && File.ReadAllText(generated).Contains("'สวัสดี'"));
            if (!drawn) return;
            var at = store.Get(home.Page.Id, "app")!.CapturedUtc;
            await Task.Delay(4000);
            Check("l10n: gen-l10n's own output doesn't draw the screen once more", !vm.IsRendering && store.Get(home.Page.Id, "app")!.CapturedUtc == at);
            SavePng((FrameworkElement)view.FindName("Stage"), "stage-l10n.png");

            // braces would turn the text into a placeholder: refused, nothing written
            vm.ToolText = "สวัสดี {name}";
            vm.ApplyToolTextCommand.Execute(null);
            Check("l10n: new text with { } is refused — the .arb stays as it was", File.ReadAllText(th) == thNow);

            DateTime t2 = DateTime.UtcNow;
            vm.UndoCommand.Execute(null);
            Check("undo: the .arb is back exactly as it was", File.ReadAllText(th) == L10nThArb);
            drawn = await Redrawn(vm, store, home.Page.Id, t2);
            var welcome3 = Box(vm, "Text", welcomeLine);
            Check("undo: drawn again with the long text", drawn && welcome3 != null && Math.Abs(welcome3.W - oldW) < 2, $"{oldW:0} → {welcome3?.W:0}");
            vm.UndoAllCommand.Execute(null);
            Check("round: undo all ends it with app_th.arb as it began", !vm.HasSession && File.ReadAllText(th) == L10nThArb);
        }
        finally
        {
            vm.RenderConsentOverride = null;
            vm.ToolConfirmOverride = null;
            CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Remove("flutter.bat");
            CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Remove("dart.bat");
        }
    }

    static string T(UiStudioViewModel vm, string key, params object[] args)
    {
        var loc = (CluadeX.Services.LocalizationService)GetField(vm, "_loc")!;
        return args.Length == 0 ? loc.T(key) : loc.T(key, args);
    }

    static bool StrictJsonOk(string text)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text, new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = System.Text.Json.JsonCommentHandling.Disallow });
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch { return false; }
    }

    static string FirstLine(string file, string probe) => File.ReadAllLines(file).FirstOrDefault(l => l.Contains(probe, StringComparison.Ordinal))?.Trim() ?? "(not found)";
    static string Clip(string s) => s.Length > 600 ? s[..600] + "…" : s;
}
