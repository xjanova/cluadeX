using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Services.Agents;
using CluadeX.ViewModels;
using CluadeX.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ThemeTest;

internal static partial class Program
{
    static bool ActivityOnly;

    /// <summary>
    /// The chat's activity card (owner 2026-11-10: "ทำ การ์ดอนิเมชั่นเท่ห์ๆ ในหน้าต่างแชทเพิ่ม … ว่ากำลังทำอะไร … ปิดได้"). Rendered for
    /// the eye: every state of the card in every theme (renders\activity-card\sheet-&lt;theme&gt;.png), the card in the chat
    /// window open and closed (window-&lt;theme&gt;.png, closed-&lt;theme&gt;.png). Checks: it sits at the top of the right panel,
    /// its frame loop runs only while a run is live AND it is on screen AND the window is not minimised, and lets go after
    /// the end burst; the ✕ turns it off and the panel keeps its place.
    /// </summary>
    static async Task ActivityShots(object themes)
    {
        Log("\n## The activity card (rendered for the eye)");
        var chat = MainVm.ChatVM;
        MainVm.NavigateToCommand.Execute("Chat");
        chat.ShowActivityCard = true;
        await Idle(400);
        string dir = Path.Combine(OutRoot, "renders", "activity-card");

        var card = FindAll<ActivityCardView>(MainWin).FirstOrDefault();
        Check("activity: the chat page has the card", card != null);
        if (card == null) return;
        Check("activity: it shows on a chat with rows", card.IsVisible && chat.ActivityCardVisible, $"visible={card.IsVisible} rows={chat.Messages.Count}");
        var panel = Ancestors(card).OfType<Border>().FirstOrDefault(b => b.Name == "SidePanel");
        Check("activity: it sits in the right-hand panel, at its top", panel != null && card.TranslatePoint(new Point(0, 0), panel).Y < 20,
              panel == null ? "no panel" : $"y={card.TranslatePoint(new Point(0, 0), panel).Y:0}");
        var scene = FindAll<ActivityScene>(card).First();

        // the frame loop: idle = none; live and on screen = hooked; hidden / minimised = none; after the end it lets go
        bool anim = ActivityScene.AnimationsAllowed;
        Check("activity: an idle chat asks for no frames", !scene.IsHooked);
        Invoke(chat, "ActivityBegan", chat.CurrentSession);
        await Idle(250);
        Check($"activity: a live run {(anim ? "animates" : "is one still frame (animations off)")}", scene.IsHooked == anim,
              $"hooked={scene.IsHooked} loaded={scene.IsLoaded} visible={scene.IsVisible} w={scene.ActualWidth:0} live={scene.Track?.IsLive} same={ReferenceEquals(scene.Track, chat.Activity.Track)} mood={chat.Activity.Mood} wants={scene.WantsFrames} win={MainWin.WindowState}");
        int f0 = scene.FrameCount;
        await Idle(600);
        int frames = scene.FrameCount - f0;
        Check("activity: at most ~30 frames a second", frames <= 24, $"{frames} frames in 0.6 s");
        card.Visibility = Visibility.Collapsed;
        await Idle(120);
        Check("activity: hidden, it lets go of the frames", !scene.IsHooked);
        card.ClearValue(UIElement.VisibilityProperty);
        await Idle(120);
        Check("activity: shown again, it moves again", scene.IsHooked == anim);
        var before = MainWin.WindowState;
        MainWin.WindowState = WindowState.Minimized;
        await Idle(200);
        Check("activity: minimised, no frames", !scene.IsHooked);
        MainWin.WindowState = before;
        await Idle(300);
        Invoke(chat, "ActivityEnded", chat.CurrentSession, false, false);
        await Idle(150);
        Check($"activity: the end plays its burst", scene.IsHooked == anim);
        await Idle(1500);
        Check("activity: …then lets go", !scene.IsHooked);
        await Idle(500);
        Check("activity: …and stays let go", !scene.IsHooked);

        // every state, every theme
        var all = themes == null ? new List<string> { "Midnight" }
            : ((IEnumerable)Prop(themes, "Themes")).Cast<object>().Select(t => (string)Prop(t, "Id")).ToList();
        var states = ActivityStates();
        // every theme in English, then two in Thai (a dark one and the light one)
        var loc = Sp.GetRequiredService<CluadeX.Services.LocalizationService>();
        string lang0 = loc.CurrentLanguage;
        var runs = all.Select(id => (Id: id, Lang: "en")).ToList();
        runs.AddRange(all.Where(id => id is "Midnight" or "Classic").Select(id => (Id: id, Lang: "th")));
        foreach (var (id, lang) in runs)
        {
            if (loc.CurrentLanguage != lang) { loc.SetLanguage(lang); await Idle(250); }
            if (themes != null) { Invoke(themes, "Apply", id, true); await Idle(250); }
            string tag = id.ToLowerInvariant() + (lang == "en" ? "" : "-" + lang);
            const int cols = 4, cw = 300;
            var cells = new List<BitmapSource>();
            foreach (var (name, make) in states)
            {
                var m = new ActivityCardModel();
                m.Show(make());
                cells.Add(RenderCard(m, 1.37));
            }
            int ch = cells.Max(c => c.PixelHeight) / 2 + 26;
            int rows = (cells.Count + cols - 1) / cols;
            var sheet = new RenderTargetBitmap(cols * cw * 2, rows * ch * 2, 192, 192, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle((Brush)MainWin.FindResource("Bg.Window"), null, new Rect(0, 0, cols * cw, rows * ch));
                var label = (Brush)MainWin.FindResource("Text.Muted");
                for (int i = 0; i < cells.Count; i++)
                {
                    double x = (i % cols) * cw, y = (i / cols) * ch;
                    var ft = new FormattedText(states[i].Name, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                               new Typeface("Segoe UI"), 11, label, 2);
                    dc.DrawText(ft, new Point(x + 12, y + 6));
                    dc.DrawImage(cells[i], new Rect(x, y + 22, cells[i].PixelWidth / 2.0, cells[i].PixelHeight / 2.0));
                }
            }
            sheet.Render(dv);
            SavePng(sheet, Path.Combine(dir, $"sheet-{tag}.png"));

            // the card in the window: a live run in the chat on screen, then closed
            Invoke(chat, "ActivityBegan", chat.CurrentSession);
            Invoke(chat, "NoteActivityTool", chat.CurrentSession.Id, "edit_file", "Editing src/Greeter.cs");
            await Idle(300);
            scene.DrawAt(2.1);
            await Idle(80);
            var full = Snapshot(RootVisual(MainWin), W, H);
            SavePng(new CroppedBitmap(full, new Int32Rect(W - 560, 0, 560, Math.Min(700, H))), Path.Combine(dir, $"window-{tag}.png"));
            scene.FixedTime = null;
            Invoke(chat, "ActivityEnded", chat.CurrentSession, false, false);
            // its words in this theme are readable: the headline is not the card's own background colour
            var head = FindAll<TextBlock>(card).First(t => t.Name == "HeadlineText");
            await Idle(120);
            var want = (SolidColorBrush)MainWin.FindResource("Status.Success");
            Check($"activity [{tag}]: a done turn's headline wears the theme's success colour", head.Foreground is SolidColorBrush hb && hb.Color == want.Color,
                  $"{(head.Foreground as SolidColorBrush)?.Color} vs {want.Color}");
        }

        if (loc.CurrentLanguage != lang0) { loc.SetLanguage(lang0); await Idle(200); }

        // closed: the ✕ hides it (kept off), the panel keeps the rest; the + menu brings it back
        var close = (Button)card.FindName("CloseButton");
        Check("activity: the ✕ is the close command", ReferenceEquals(close.Command, chat.CloseActivityCardCommand));
        close.Command.Execute(null);
        await Idle(250);
        Check("activity: closed, the card is gone", !card.IsVisible && !chat.ShowActivityCard);
        var shut = Snapshot(RootVisual(MainWin), W, H);
        SavePng(new CroppedBitmap(shut, new Int32Rect(W - 560, 0, 560, Math.Min(700, H))), Path.Combine(dir, "closed.png"));
        chat.ShowActivityCard = true;
        await Idle(250);
        Check("activity: the + menu turns it on again", card.IsVisible);
        Log($"  shots: {dir}");
    }

    /// <summary>The card's states, each a track fed the events a real turn would raise.</summary>
    static List<(string Name, Func<ActivityTrack> Make)> ActivityStates()
    {
        var now = DateTime.Now;
        ActivityTrack Worked(int seconds = 95)
        {
            var t = new ActivityTrack();
            t.Begin(now.AddSeconds(-seconds));
            void Did(string tool, string verb, string target, bool ok = true, string file = null)
            {
                t.ToolStarting(tool, verb + " " + target);
                t.ToolEnded(tool, ok, verb, target, file, now.AddSeconds(-seconds / 2));
            }
            Did("read_file", "Read", "Views/ChatView.xaml");
            Did("grep", "Searched", "OnToolExecuted");
            Did("read_file", "Read", "ChatViewModel.Runs.cs");
            Did("edit_file", "Edited", "ChatViewModel.cs", true, "ChatViewModel.cs");
            Did("run_build", "Built", "CluadeX.csproj");
            return t;
        }
        return new()
        {
            ("idle — no turn yet", () => new ActivityTrack()),
            ("reading the request", () => { var t = new ActivityTrack(); t.Begin(now.AddSeconds(-2)); return t; }),
            ("thinking", () => { var t = Worked(); t.Thinking(); return t; }),
            ("writing the answer", () => { var t = Worked(); t.WritingAnswer(); return t; }),
            ("exploring", () => { var t = Worked(); t.ToolStarting("read_file", "Reading file: Services/Agents/RunActivity.cs"); return t; }),
            ("editing", () => { var t = Worked(); t.ToolStarting("edit_file", "Editing Views/ActivityScene.cs"); return t; }),
            ("running", () => { var t = Worked(); t.ToolStarting("run_tests", "Running tests: chattest AC"); return t; }),
            ("sub-agents", () =>
            {
                var t = Worked();
                t.ToolStarting("agent_spawn", "Starting 4 sub-agents");
                for (int i = 1; i <= 4; i++) t.SubAgent("sa-" + i, "Sub-agent #" + i, "start", now);
                t.SubAgent("sa-2", "", "done", now);
                return t;
            }),
            ("waiting for you", () => { var t = Worked(); t.ToolStarting("ask_user", "Asking which folder"); t.SetWaiting(true); return t; }),
            ("a tool failed", () => { var t = Worked(); t.ToolStarting("run_command", "Running npm test"); t.ToolEnded("run_command", false, "Ran", "npm test", null, DateTime.Now); t.ToolStarting("read_file", "Reading the test log"); return t; }),
            ("done", () => { var t = Worked(184); t.End(false, false, now); return t; }),
            ("ended on an error", () => { var t = Worked(); t.ToolEnded("run_build", false, "Built", "CluadeX.csproj", null, now); t.End(false, true, now); return t; }),
            ("stopped", () => { var t = Worked(40); t.End(true, false, now); return t; }),
        };
    }

    /// <summary>A card on its own (the side panel's background behind it), at twice the size.</summary>
    static BitmapSource RenderCard(ActivityCardModel m, double t)
    {
        var view = new ActivityCardView { DataContext = m, Width = 280 };
        var host = new Border { Background = (Brush)MainWin.FindResource("Bg.Panel"), Padding = new Thickness(10), Child = view, Width = 300 };
        TextElement.SetFontFamily(host, MainWin.FontFamily);
        host.Measure(new Size(300, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        FindAll<ActivityScene>(view).First().DrawAt(t);
        host.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth * 2), (int)Math.Ceiling(host.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bmp.Render(host);
        bmp.Freeze();
        return bmp;
    }
}
