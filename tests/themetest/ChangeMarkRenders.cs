using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Services;
using Microsoft.Extensions.DependencyInjection;
using CluadeX.Services.Problems;
using CluadeX.ViewModels;

namespace ThemeTest;

internal static partial class Program
{
    static bool ChangeMarksOnly;

    /// <summary>
    /// The editor's change marks, rendered in every theme (owner 2026-11-09: "ป้ายแถบสีที่มีการเปลี่ยนแปลง … อยากให้ชัดเจน …
    /// ถ้าคุณแก้โค๊ดเอง ก็ต้องระบายไว้อีกสี"): a chat changes a file for real (a restore point, then the write), the user edits and
    /// saves it in the editor, and the page shows AI-added / AI-changed / AI-deleted marks and the user's own in a second
    /// colour. Checks, per theme: the gutter bar at a user line and at an AI line is drawn in that theme's Editor.Change.*
    /// colour, and the line tint stands out from an unchanged line. Then the before / after compare (F7 steps) and the
    /// project-wide list of user edits. Shots: renders\change-marks\.
    /// </summary>
    static async Task ChangeMarkShots()
    {
        Log("\n## Change marks in the editor (rendered for the eye + pixel checks)");
        var vm = MainVm.CodeEditorVM;
        var themes = ThemeServiceOrNull();
        var cp = CheckpointService.Current;
        Check("marks: the app has a restore-point store", cp != null);
        if (cp == null) return;
        MainVm.NavigateToCommand.Execute("Code");
        await Idle(300);

        string file = Path.Combine(FixtureProject, "src", "Cart.cs");
        string[] before =
        {
            "namespace Demo;",
            "",
            "/// <summary>A shopping cart.</summary>",
            "public sealed class Cart",
            "{",
            "    private readonly List<decimal> _items = new();",
            "",
            "    public void Add(decimal price) => _items.Add(price);",
            "",
            "    public decimal Total()",
            "    {",
            "        decimal sum = 0;",
            "        foreach (var p in _items) sum += p;",
            "        return sum;",
            "    }",
            "",
            "    // Old discount code, no longer used.",
            "    public decimal Legacy() => 0m;",
            "",
            "    public int Count => _items.Count;",
            "}",
        };
        File.WriteAllText(file, string.Join("\n", before) + "\n");

        // The chat's turn: restore point first (as every agent write does), then the AI's version of the file.
        var ai = before.ToList();
        ai[11] = "        decimal sum = 0m;   // decimal literal";                       // changed
        ai.Insert(14, "        return Math.Round(sum, 2);");                              // added (and the old return removed below)
        ai.RemoveAt(13);
        ai.Insert(8, "    public void Clear() => _items.Clear();");                       // added
        ai.Insert(9, "");
        int legacy = ai.IndexOf("    // Old discount code, no longer used.");
        ai.RemoveRange(legacy, 3);                                                         // deleted (comment, method, blank)
        var prior = AgentRunScope.Current;
        var scope = new AgentRunScope("themetest-marks", FixtureProject) { Step = 1 };
        AgentRunScope.Enter(scope);
        cp.CaptureBeforeChange(file);
        AgentRunScope.Enter(prior);
        File.WriteAllText(file, string.Join("\n", ai) + "\n");

        await vm.OpenPathAsync(file);
        await Idle(300);
        var view = Find<CluadeX.Views.CodeEditorView>((DependencyObject)RootVisual(MainWin)).FirstOrDefault();
        Check("marks: the code page is on screen", view != null);
        if (view == null) return;
        T El<T>(string name) where T : class => view.FindName(name) as T;
        var editor = El<ICSharpCode.AvalonEdit.TextEditor>("Editor")!;

        // The user edits by hand and saves (the editor's real save: a [user-edit] comment goes on, the lines are remembered).
        var tab = vm.ActiveTab!;
        tab.Content = tab.Content.Replace("    public int Count => _items.Count;", "    public int Count => _items.Count;\n    public bool IsEmpty => _items.Count == 0;");
        var save = typeof(CodeEditorViewModel).GetMethod("SaveTabInteractiveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        bool saved = await (Task<bool>)save.Invoke(vm, new object[] { tab })!;
        string onDisk = File.ReadAllText(file);
        Check("marks: the user's save in edit mode wrote the file with a [user-edit] comment", saved && onDisk.Contains("IsEmpty => _items.Count == 0; // [user-edit "), onDisk.Split('\n').FirstOrDefault(l => l.Contains("IsEmpty")) ?? "");

        var hunksProp = typeof(CluadeX.Views.CodeEditorView).GetProperty("ChangeHunks", BindingFlags.NonPublic | BindingFlags.Instance)!;
        IReadOnlyList<AiChangeHunk> Hunks() => (IReadOnlyList<AiChangeHunk>)hunksProp.GetValue(view);
        bool ready = await WaitUntil(() => Hunks().Any(h => h.ByUser) && Hunks().Any(h => !h.ByUser && h.Kind == AiChangeKind.Removed), 8000);
        var hs = Hunks();
        string shown = string.Join(" | ", hs.Select(h => $"{(h.ByUser ? "USER" : "AI")}-{h.Kind}@{h.StartLine}+{h.LineCount}"));
        Check("marks: AI-added, AI-changed, AI-deleted and the user's line all marked, after the save", ready
            && hs.Any(h => !h.ByUser && h.Kind == AiChangeKind.Added) && hs.Any(h => !h.ByUser && h.Kind == AiChangeKind.Modified)
            && hs.Any(h => !h.ByUser && h.Kind == AiChangeKind.Removed) && hs.Count(h => h.ByUser) == 1, shown);
        Check("marks: the status bar shows the AI count, the user count and Before / after",
            El<Button>("StAiChanges")!.IsVisible && El<Button>("StUserEdits")!.IsVisible && El<Button>("StCompare")!.IsVisible,
            $"{El<Button>("StAiChanges")!.Content} · {El<Button>("StUserEdits")!.Content}");
        editor.ScrollToHome();
        await Idle(200);

        var userHunk = hs.First(h => h.ByUser);
        var aiAdded = hs.First(h => !h.ByUser && h.Kind == AiChangeKind.Added);
        int plainLine = 1;   // "namespace Demo;" — unchanged
        var all = themes == null ? new List<string> { "Midnight" }
            : ((IEnumerable)Prop(themes, "Themes")).Cast<object>().Select(t => (string)Prop(t, "Id")).ToList();
        string dir = Path.Combine(OutRoot, "renders", "change-marks");
        var root = (UIElement)RootVisual(MainWin);
        var textView = editor.TextArea.TextView;
        var margin = editor.TextArea.LeftMargins.OfType<CluadeX.Views.Editor.AiChangeMargin>().First();
        var loc = Sp.GetRequiredService<LocalizationService>();
        string langBefore = loc.CurrentLanguage;
        // Every theme in English (the checks), then the light and a dark theme again with the UI in Thai (the owner's view).
        foreach (var (lang, ids) in new[] { ("en", all), ("th", all.Where(t => t is "Classic" or "Midnight").ToList()) })
        {
        loc.SetLanguage(lang);
        Invoke(view, "ScheduleAiChanges");
        await Idle(700);
        string shotPrefix = lang == "th" ? "editor-th-" : "editor-";
        foreach (string id in ids)
        {
            if (themes != null) { Invoke(themes, "Apply", id, true); await Idle(300); }
            textView.Redraw();
            await Idle(150);
            var full = Snapshot(RootVisual(MainWin), W, H);
            var px = Pixels(full);
            Color At(double x, double y)
            {
                int xi = Math.Clamp((int)x, 0, W - 1), yi = Math.Clamp((int)y, 0, H - 1), o = (yi * W + xi) * 4;
                return Color.FromArgb(px[o + 3], px[o + 2], px[o + 1], px[o]);
            }
            double LineMid(int line)
            {
                textView.EnsureVisualLines();
                var vl = textView.GetVisualLine(line);
                if (vl == null) return -1;
                return textView.TranslatePoint(new Point(0, vl.VisualTop - textView.VerticalOffset + vl.Height / 2), root).Y;
            }
            double barX = margin.TranslatePoint(new Point(4, 0), root).X;
            double tintX = textView.TranslatePoint(new Point(textView.ActualWidth - 30, 0), root).X;
            Color want = ThemeService.ColorOf("Editor.Change.User", Colors.Magenta), wantAi = ThemeService.ColorOf("Editor.Change.Added", Colors.Magenta);
            Color gotUser = At(barX, LineMid(userHunk.StartLine)), gotAi = At(barX, LineMid(aiAdded.StartLine));
            static int Dist(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
            string tag = lang == "th" ? $"{id}/th" : id;
            Check($"marks/{tag}: the gutter bar of the user's line is the theme's user colour", Dist(gotUser, want) <= 30, $"{gotUser} vs {want}");
            Check($"marks/{tag}: the gutter bar of an AI-added line is the theme's added colour", Dist(gotAi, wantAi) <= 30, $"{gotAi} vs {wantAi}");
            Check($"marks/{tag}: the user's and the AI's colours are told apart", Dist(want, wantAi) >= 120, $"{want} vs {wantAi}");
            Color tintUser = At(tintX, LineMid(userHunk.StartLine)), tintAi = At(tintX, LineMid(aiAdded.StartLine)), plain = At(tintX, LineMid(plainLine));
            Check($"marks/{tag}: the changed lines are tinted across the code (user and AI lines differ from an unchanged one)",
                Dist(tintUser, plain) >= 12 && Dist(tintAi, plain) >= 12, $"user {tintUser} · ai {tintAi} · plain {plain}");

            // The editor and its status bar.
            var status = (FrameworkElement)VisualTreeHelper.GetParent(El<Button>("StAiChanges")!);
            var e0 = editor.TranslatePoint(new Point(0, 0), root);
            var s1 = status.TranslatePoint(new Point(0, status.ActualHeight), root);
            int x0 = Math.Max(0, (int)e0.X - 2), y0 = Math.Max(0, (int)e0.Y - 4);
            int wCrop = Math.Min(W - x0, (int)editor.ActualWidth + 4), hCrop = Math.Min(H - y0, (int)(s1.Y - y0) + 2);
            SavePng(new CroppedBitmap(full, new Int32Rect(x0, y0, Math.Max(1, wCrop), Math.Max(1, hCrop))), Path.Combine(dir, $"{shotPrefix}{id.ToLowerInvariant()}.png"));
        }
        }

        // The motion: a file's marks draw themselves in (a cascade), the one you land on flashes — and then nothing runs.
        await MotionChecks(view, editor, dir, themes);

        // Before / after: one click, the panel lands on the first change, F7 steps to the next.
        loc.SetLanguage(langBefore);
        Invoke(view, "ScheduleAiChanges");
        await Idle(500);
        string headCaption = vm.DiffLeftCaption;
        loc.SetLanguage("th");
        Press(El<Button>("StCompare")!);
        bool diffShown = await WaitUntil(() => vm.IsDiffViewActive && vm.DiffRows.Count > 0, 5000);
        await Idle(300);
        var cursor = typeof(CluadeX.Views.CodeEditorView).GetProperty("DiffCursor", BindingFlags.NonPublic | BindingFlags.Instance)!;
        int c0 = (int)cursor.GetValue(view);
        Invoke(view, "StepDiff", true);
        int c1 = (int)cursor.GetValue(view);
        Check("compare: Before / after opens the whole file against the chat's baseline and lands on change 1",
            diffShown && c0 == 0 && vm.DiffLeftCaption.Length > 0 && vm.DiffRows.Any(r => r.IsChange), $"cursor {c0} · {vm.DiffLeftCaption} · {vm.DiffSummary}");
        Check("compare: next steps to change 2 and says so", c1 == 1 && (El<TextBlock>("DiffPos")?.Text ?? "").Contains('2'), El<TextBlock>("DiffPos")?.Text ?? "");
        foreach (string id in all.Where(t => t is "Midnight" or "Classic"))
        {
            if (themes != null) { Invoke(themes, "Apply", id, true); await Idle(300); }
            var full = Snapshot(RootVisual(MainWin), W, H);
            var e0 = editor.TranslatePoint(new Point(0, 0), root);
            int x0 = Math.Max(0, (int)e0.X - 2), y0 = Math.Max(0, (int)e0.Y - 40);
            SavePng(new CroppedBitmap(full, new Int32Rect(x0, y0, Math.Min(W - x0, (int)editor.ActualWidth + 4), Math.Min(H - y0, (int)editor.ActualHeight + 40))),
                Path.Combine(dir, $"compare-th-{id.ToLowerInvariant()}.png"));
        }
        loc.SetLanguage(langBefore);
        vm.CloseDiffCommand.Execute(null);
        await Idle(100);
        Check("compare: closing it puts the captions back for the next diff", vm.DiffLeftCaption == headCaption, $"{vm.DiffLeftCaption} vs {headCaption}");

        // Every user edit in the project, in the search panel.
        await vm.ShowUserEditsAsync();
        await WaitUntil(() => vm.SearchResults.Count > 0, 4000);
        Check("search: 'Show every user edit in the project' lists the saved [user-edit] line in the search panel",
            vm.IsSearchPanelActive && vm.SearchResults.Count >= 1 && vm.SearchContextLabel.Contains("[user-edit"), $"{vm.SearchResults.Count} · {vm.SearchContextLabel}");
        if (themes != null) Invoke(themes, "Apply", "Midnight", true);
        Log($"  shots: {dir}");
    }

    static async Task MotionChecks(CluadeX.Views.CodeEditorView view, ICSharpCode.AvalonEdit.TextEditor editor, string dir, object themes)
    {
        var fx = (CluadeX.Views.Editor.ChangeMarkFx)typeof(CluadeX.Views.CodeEditorView)
            .GetProperty("ChangeMarksFx", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
        if (!CluadeX.Views.Editor.ChangeMarkFx.MotionOn)
        {
            Log($"  SKIP  motion: this machine has animations off (ClientAreaAnimation {SystemParameters.ClientAreaAnimation}, render tier {RenderCapability.Tier >> 16})");
            return;
        }
        if (themes != null) { Invoke(themes, "Apply", "Midnight", true); await Idle(300); }
        Check("motion: at rest nothing is hooked to the render loop", !fx.IsHooked && !fx.IsAnimating);

        // Reopen-like: the marks are new to this view again → the cascade plays.
        SetField(view, "_marksFxPath", null!);
        Invoke(view, "ScheduleAiChanges");
        bool started = await WaitUntil(() => fx.IsAnimating, 3000);
        var hs = (IReadOnlyList<AiChangeHunk>)typeof(CluadeX.Views.CodeEditorView).GetProperty("ChangeHunks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
        double first = hs.Count > 0 ? fx.Appear(hs[0]) : 1, lastOne = hs.Count > 0 ? fx.Appear(hs[^1]) : 1;
        Check("motion: opening the file's marks plays the draw-in, top to bottom (the first is ahead of the last)", started && first < 1 && first >= lastOne, $"first {first:0.00} · last {lastOne:0.00}");
        await Idle(140);
        var root = (UIElement)RootVisual(MainWin);
        var e0 = editor.TranslatePoint(new Point(0, 0), root);
        int x0 = Math.Max(0, (int)e0.X - 2), y0 = Math.Max(0, (int)e0.Y - 4);
        var mid = Snapshot(RootVisual(MainWin), W, H);
        SavePng(new CroppedBitmap(mid, new Int32Rect(x0, y0, Math.Min(W - x0, (int)editor.ActualWidth + 4), Math.Min(H - y0, 360))), Path.Combine(dir, "motion-midframe-midnight.png"));
        bool let = await WaitUntil(() => !fx.IsHooked, 2500);
        Check("motion: the draw-in ends by itself and lets go of the render loop (< 1 s)", let && !fx.IsAnimating && hs.All(h => fx.Appear(h) == 1));
        await Idle(500);
        Check("motion: …and stays let go (no timer, nothing drawn between changes)", !fx.IsHooked);

        // The same marks again (a refresh, a language switch): no replay.
        Invoke(view, "ScheduleAiChanges");
        await Idle(700);
        Check("motion: a refresh with the same marks does not play them again", !fx.IsHooked && !fx.IsAnimating);

        // Landing on a mark flashes it once.
        hs = (IReadOnlyList<AiChangeHunk>)typeof(CluadeX.Views.CodeEditorView).GetProperty("ChangeHunks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
        var target = hs.First(h => !h.ByUser && h.LineCount > 0);
        Invoke(view, "ShowAiPeek", target);
        await Idle(60);
        double flash = fx.Flash(target);
        Check("motion: the mark you land on flashes", flash > 0 && fx.IsHooked, $"{flash:0.00}");
        await Idle(120);
        var fl = Snapshot(RootVisual(MainWin), W, H);
        SavePng(new CroppedBitmap(fl, new Int32Rect(x0, y0, Math.Min(W - x0, (int)editor.ActualWidth + 4), Math.Min(H - y0, 360))), Path.Combine(dir, "motion-flash-midnight.png"));
        bool calm = await WaitUntil(() => !fx.IsHooked, 2500);
        Check("motion: …once, then lets go", calm && fx.Flash(target) == 0);
        Invoke(view, "CloseAiPeek");

        // Animations off (Windows setting / the harness switch): the marks are drawn whole at once.
        CluadeX.Views.Editor.ChangeMarkFx.Allowed = false;
        SetField(view, "_marksFxPath", null!);
        Invoke(view, "ScheduleAiChanges");
        await Idle(700);
        Check("motion: with animations off the marks appear whole, nothing hooked", !fx.IsHooked && hs.All(h => fx.Appear(h) == 1));
        CluadeX.Views.Editor.ChangeMarkFx.Allowed = true;
    }
}
