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

    static string FirstLine(string file, string probe) => File.ReadAllLines(file).FirstOrDefault(l => l.Contains(probe, StringComparison.Ordinal))?.Trim() ?? "(not found)";
    static string Clip(string s) => s.Length > 600 ? s[..600] + "…" : s;
}
