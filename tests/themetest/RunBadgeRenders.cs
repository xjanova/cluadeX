using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Views;

namespace ThemeTest;

internal static partial class Program
{
    static bool BadgesOnly;

    /// <summary>
    /// The sidebar's run badges and น้อง CluadeX in the activity bar (owner 2026-11-08: "เปลี่ยนให้ไอค่อนบับเบิ้ลหน้าแชท เป็นไอค่อน
    /// ที่แสดงการทำงานแบบอนิเมชั่น" + "ไอค่อนแมวตัวล่างควรเป็นหน้าของน้อง cluadex … ไม่มีไรทำก็ทำหลับ"). One chat per badge, every theme;
    /// then her face in every mood. Rendered for the eye: renders\run-badges\. Checks: every badge shows exactly one of
    /// its states, idle rows ask for no frames, and she takes each mood.
    /// </summary>
    static async Task RunBadgeShots()
    {
        Log("\n## Run badges in the sidebar and น้อง CluadeX (rendered for the eye)");
        var chat = MainVm.ChatVM;
        var themes = ThemeServiceOrNull();
        var states = new[] { ("", "Idle — finished long ago"), ("working", "Working — writes the parser"), ("waiting", "Waiting — asks which folder"),
                             ("done", "Done — result not opened"), ("failed", "Failed — quota ran out"), ("stopped", "Stopped — app closed mid-run") };
        var made = new List<ChatSession>();
        foreach (var (status, title) in states.Reverse())
        {
            var s = new ChatSession { Title = title, ProjectPath = FixtureProject, RunStatus = status };
            chat.Sessions.Insert(0, s);
            made.Add(s);
        }
        MainVm.NavigateToCommand.Execute("Chat");
        Invoke(chat, "RebuildVisibleSessions");
        await Idle(400);

        var icons = FindAll<RunBadgeIcon>(MainWin).Where(i => i.DataContext is ChatSession cs && made.Contains(cs)).ToList();
        Check("badges: each test chat's row has a run badge", icons.Count == made.Count, $"{icons.Count}/{made.Count}");
        foreach (var icon in icons)
        {
            var cs = (ChatSession)icon.DataContext;
            // exactly one state layer is drawn (the "done" check and its dot count as one)
            int shown = VisualChildren(icon).OfType<FrameworkElement>()
                .Count(e => e.Parent is System.Windows.Controls.Grid g && g.Parent == icon && e.IsVisible && e is not System.Windows.Shapes.Ellipse);
            Check($"badges: '{cs.RunStatus}' draws one state", shown == 1, $"{shown} shown");
            bool looping = VisualChildren(icon).Any(Motion.IsLooping);
            Check($"badges: '{cs.RunStatus}' {(RunBadge.IsLive(cs.RunStatus) ? "animates" : "is still")}", looping == RunBadge.IsLive(cs.RunStatus), $"looping={looping}");
        }

        var all = themes == null ? new List<string> { "Midnight" }
            : ((IEnumerable)Prop(themes, "Themes")).Cast<object>().Select(t => (string)Prop(t, "Id")).ToList();
        string dir = Path.Combine(OutRoot, "renders", "run-badges");
        foreach (string id in all)
        {
            if (themes != null) { Invoke(themes, "Apply", id, true); await Idle(250); }
            var full = Snapshot(RootVisual(MainWin), W, H);
            SavePng(new CroppedBitmap(full, new Int32Rect(0, 0, Math.Min(420, W), Math.Min(520, H))),
                    Path.Combine(dir, $"sidebar-{id.ToLowerInvariant()}.png"));
        }

        // Her moods, one frame each, side by side (the theme last applied).
        var buddy = FindAll<CluadexBuddy>(MainWin).FirstOrDefault();
        Check("buddy: the activity bar shows น้อง CluadeX", buddy != null);
        // A click on her goes Home (owner 2026-11-08: "กดที่ไอค่อนแมว หรือ หน้า น้อง cluadex ควรเป็นการกลับไปหน้า home"): she sits in a
        // Button bound to ChatVM.GoHomeCommand — hand cursor, a tooltip that says so, a tab stop, Enter works.
        var home = buddy == null ? null : Ancestors(buddy).OfType<System.Windows.Controls.Button>().FirstOrDefault();
        Check("buddy: she is a button", home != null);
        if (home != null)
        {
            Check("buddy: the button goes Home", ReferenceEquals(home.Command, chat.GoHomeCommand), home.Command?.GetType().Name ?? "none");
            Check("buddy: a hand cursor", home.Cursor == System.Windows.Input.Cursors.Hand, $"{home.Cursor}");
            Check("buddy: her tooltip says a click goes Home", home.ToolTip is string tip && tip.Contains(chat.BuddyMoodTip) && tip.Contains("Home"), $"{home.ToolTip}");
            Check("buddy: reachable with Tab", home.Focusable && System.Windows.Input.KeyboardNavigation.GetIsTabStop(home) && home.IsEnabled, "");
            // Enter on the focused button runs its command (a stand-in command here, so the run below keeps its project)
            int ran = 0;
            home.Command = new CluadeX.ViewModels.RelayCommand(() => ran++);
            home.Focus();
            await Idle(60);
            var src = PresentationSource.FromVisual(home);
            if (src != null)
                home.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, src, 0, System.Windows.Input.Key.Enter)
                    { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            await Idle(60);
            Check("buddy: Enter on her goes Home", ran == 1, $"{ran} (focused={home.IsKeyboardFocused})");
            home.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new System.Windows.Data.Binding("ChatVM.GoHomeCommand"));
            Check("buddy: …and the real command is back", ReferenceEquals(home.Command, chat.GoHomeCommand), "");
        }
        if (buddy != null)
        {
            var moods = new[] { BuddyMood.Idle, BuddyMood.Working, BuddyMood.Waiting, BuddyMood.Happy, BuddyMood.Sad, BuddyMood.Sleep };
            const int cell = 72, scale = 2;
            var sheet = new RenderTargetBitmap(cell * moods.Length * scale, cell * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            for (int i = 0; i < moods.Length; i++)
            {
                buddy.Mood = moods[i];
                await Idle(moods[i] == BuddyMood.Happy ? 250 : 120);
                Check($"buddy: takes the '{moods[i]}' mood", buddy.Shown == moods[i], buddy.Shown);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle((Brush)MainWin.FindResource("ActivityBar.Background"), null, new Rect(i * cell, 0, cell, cell));
                    var b = new VisualBrush(buddy) { Stretch = Stretch.None };
                    dc.DrawRectangle(b, null, new Rect(i * cell + 12, 12, 48, 48));
                }
                sheet.Render(dv);
            }
            buddy.ClearValue(CluadexBuddy.MoodProperty);
            buddy.SetBinding(CluadexBuddy.MoodProperty, new System.Windows.Data.Binding("BuddyMoodState"));
            SavePng(sheet, Path.Combine(dir, "buddy-moods.png"));
        }
        Log($"  shots: {dir}");
        foreach (var s in made) chat.Sessions.Remove(s);
    }

    static IEnumerable<DependencyObject> VisualChildren(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in VisualChildren(c)) yield return d;
        }
    }

    static IEnumerable<DependencyObject> Ancestors(DependencyObject e)
    {
        for (var p = VisualTreeHelper.GetParent(e); p != null; p = VisualTreeHelper.GetParent(p)) yield return p;
    }

    static List<T> FindAll<T>(DependencyObject root) where T : DependencyObject
        => VisualChildren(root).OfType<T>().ToList();
}
