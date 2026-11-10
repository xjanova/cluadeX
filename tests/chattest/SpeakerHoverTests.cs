using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using CluadeX.Models;
using CluadeX.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>SPK — the speaker under her face on a reply (read replies aloud on / off). Owner 2026-11-10: "เม้าชี้รูปลำโพง
    /// ตรงอวาต้า เพื่อปิดเสียง มันกระพริบไม่หยุด". Pointing at a row shows its Copy / Rewind strip; that strip grew the row,
    /// a chat that follows its end scrolled up by the difference, the speaker slid out from under the pointer, the row
    /// lost the hover, shrank, scrolled back — over and over. The mouse is played here the way WPF decides it: each pass
    /// the row is hovered only if an input hit test at the pointer lands inside it, then layout runs and the chat follows
    /// its end, as in the app.</summary>
    static async Task TestSpeakerHover()
    {
        Log("\n## SPK — the speaker under her face holds still while pointed at, and switches reading aloud");
        var themes = Sp.GetRequiredService<ThemeService>();
        bool speakBefore = Settings.Settings.VoiceSpeakReplies;
        try
        {
            foreach (var theme in themes.Themes.Where(t => t.IsReady).Select(t => t.Id))
            {
                themes.Apply(theme, persist: false);
                // the strip's words in both languages: Thai on Midnight and Lavender (marks above and below the line)
                bool thai = theme is ThemeIds.Midnight or ThemeIds.Lavender;
                LocSvc.SetLanguage(thai ? "th" : "en");
                await FreshChat();
                // long enough to scroll, and following its end — the case the owner hit (the latest reply)
                for (int i = 0; i < 12; i++)
                {
                    Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = $"question {i}" });
                    Vm.Messages.Add(new ChatMessage { Role = MessageRole.Assistant, Content = $"answer {i}\n\nwith a second line" });
                }
                var reply = new ChatMessage { Role = MessageRole.Assistant, Content = "ok" };
                Vm.Messages.Add(reply);
                ChatList.ScrollIntoView(reply);
                await Render();
                await Render();

                Button Speaker() => Container(reply) is { } c
                    ? Find<Button>(c).FirstOrDefault(b => ReferenceEquals(b.Command, Vm.ToggleSpeakRepliesCommand))
                    : null;
                var spk = Speaker();
                Check($"SPK1 [{theme}] the latest reply shows the speaker under her face", spk is { IsVisible: true });
                if (spk == null) continue;

                // the pointer rests on the speaker's centre and does not move
                var p = spk.TransformToAncestor(View).Transform(new Point(spk.ActualWidth / 2, spk.ActualHeight / 2));
                double rowH = Container(reply).ActualHeight;
                var trail = new List<string>();
                int flips = 0, offSpeaker = 0;
                double maxDrift = 0;
                for (int pass = 0; pass < 8; pass++)
                {
                    var c = Container(reply);
                    var hit = View.InputHitTest(p) as DependencyObject;
                    bool overRow = c != null && IsUnder(hit, RowRoot(c));
                    bool overSpeaker = (spk = Speaker()) != null && IsUnder(hit, spk);
                    if (!overSpeaker) offSpeaker++;
                    if (overRow != reply.IsHovered) { if (pass > 0) flips++; reply.IsHovered = overRow; }
                    trail.Add($"{(overRow ? "in" : "out")}/{(overSpeaker ? "spk" : hit?.GetType().Name ?? "none")}");
                    await Render();
                    if ((spk = Speaker()) != null)
                    {
                        var now = spk.TransformToAncestor(View).Transform(new Point(spk.ActualWidth / 2, spk.ActualHeight / 2));
                        maxDrift = Math.Max(maxDrift, (now - p).Length);
                    }
                }
                Check($"SPK2 [{theme}] pointing at it: the row stays hovered (no flicker loop)", flips == 0 && reply.IsHovered, string.Join(" ", trail));
                Check($"SPK2 [{theme}] …the speaker stays under the pointer every pass", offSpeaker == 0, string.Join(" ", trail));
                Check($"SPK2 [{theme}] …and does not move (the hover strip changes no height)", maxDrift < 0.5 && Math.Abs(Container(reply).ActualHeight - rowH) < 0.5,
                    $"drift {maxDrift:0.#} px, row {rowH:0.#} → {Container(reply).ActualHeight:0.#} px");
                Check($"SPK2 [{theme}] the hover strip (Copy) is showing", Find<Button>(Container(reply)).Any(b => Equals(b.Content, "\uE8C8") && b.IsVisible));

                // proof pictures: the latest reply hovered, and a question of the user's hovered (its strip sits across
                // the bubble's top edge, there being no label line there)
                var clipped = Find<Button>(Container(reply)).Where(b => b.IsVisible && b.Content is string w && w.Length > 1).Select(b =>
                {
                    var probe = new TextBlock { Text = (string)b.Content, FontFamily = b.FontFamily, FontSize = b.FontSize };
                    probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    return (b, need: probe.DesiredSize.Height);
                }).Where(x => x.need > x.b.ActualHeight - 2 + 0.01).Select(x => $"{x.b.Content} needs {x.need:0.#} of {x.b.ActualHeight - 2:0.#} px").ToList();
                Check($"SPK2 [{theme}{(thai ? " th" : "")}] the strip's words fit its 18 px buttons", clipped.Count == 0, string.Join(", ", clipped));
                SaveRow(Container(reply), $"spk-{theme}-reply.png");
                var question = Vm.Messages.Last(m => m.Role == MessageRole.User);
                question.IsHovered = true;
                await Render();
                if (Container(question) is { } qc)
                {
                    var copy = Find<Button>(qc).FirstOrDefault(b => Equals(b.Content, "\uE8C8"));
                    Check($"SPK2 [{theme}] a question's hover strip can be clicked (Copy is reached by a hit test)",
                        copy != null && copy.IsVisible && HitReaches(copy, out _));
                    SaveRow(qc, $"spk-{theme}-question.png");
                }
                question.IsHovered = false;
                await Render();

                // every kind of row keeps its height when pointed at (a question, a reply, a tool row with its label line)
                var tool = new ChatMessage { Role = MessageRole.ToolAction, Content = "read_file a.cs", ToolName = "Read a.cs", ToolSuccess = true };
                Vm.Messages.Add(tool);
                ChatList.ScrollIntoView(tool);
                await Render();
                var grew = new List<string>();
                foreach (var m in new[] { question, reply, tool })
                {
                    ChatList.ScrollIntoView(m);
                    await Render();
                    double h0 = Container(m)?.ActualHeight ?? -1;
                    m.IsHovered = true;
                    await Render();
                    double h1 = Container(m)?.ActualHeight ?? -1;
                    m.IsHovered = false;
                    await Render();
                    if (h0 < 0 || Math.Abs(h1 - h0) >= 0.5) grew.Add($"{m.Role} {h0:0.#}→{h1:0.#}");
                }
                Check($"SPK2 [{theme}] a question, a reply and a tool row keep their height when pointed at", grew.Count == 0, string.Join(", ", grew));
                Vm.Messages.Remove(tool);
                ChatList.ScrollIntoView(reply);
                await Render();

                // click it: reading aloud flips, the icon and the tip follow, the setting is kept
                reply.IsHovered = true;     // the pointer is on it when it is clicked
                await Render();
                spk = Speaker();
                p = spk.TransformToAncestor(View).Transform(new Point(spk.ActualWidth / 2, spk.ActualHeight / 2));
                bool before = Vm.SpeakReplies;
                Invoke(spk);
                await Render();
                spk = Speaker();
                Check($"SPK3 [{theme}] a click switches reading aloud and keeps it in the settings",
                    Vm.SpeakReplies == !before && Settings.Settings.VoiceSpeakReplies == !before);
                Check($"SPK3 [{theme}] …the icon shows the new state (speaker on / muted)",
                    Equals(spk?.Content, Vm.SpeakReplies ? "\uE767" : "\uE74F")
                    && Equals(spk?.ToolTip, L(Vm.SpeakReplies ? "voice.chat.toggleOn" : "voice.chat.toggleOff")), $"{spk?.Content} {spk?.ToolTip}");
                Invoke(spk);
                await Render();
                spk = Speaker();
                Check($"SPK3 [{theme}] a second click switches it back", Vm.SpeakReplies == before && Equals(spk?.Content, before ? "\uE767" : "\uE74F"));
                Check($"SPK3 [{theme}] …and the click did not move the row either", spk != null
                    && (spk.TransformToAncestor(View).Transform(new Point(spk.ActualWidth / 2, spk.ActualHeight / 2)) - p).Length < 0.5);
                reply.IsHovered = false;
            }
        }
        finally
        {
            Settings.UpdateSettings(s => s.VoiceSpeakReplies = speakBefore);
            LocSvc.SetLanguage("en");
            themes.Apply(ThemeIds.Midnight, persist: false);
        }

        static void SaveRow(FrameworkElement row, string name)
        {
            // the row and a strip above it (a question's hover strip rises over the bubble's top edge)
            var top = Math.Max(0, row.TransformToAncestor(View).Transform(new Point(0, -12)).Y);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)View.ActualWidth, (int)View.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(View);
            var crop = new System.Windows.Media.Imaging.CroppedBitmap(rtb, new Int32Rect(0, (int)top,
                (int)View.ActualWidth, (int)Math.Min(row.ActualHeight + 12, View.ActualHeight - top)));
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(crop));
            using var f = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, name));
            enc.Save(f);
        }

        static void Invoke(Button b) => ((IInvokeProvider)new ButtonAutomationPeer(b).GetPattern(PatternInterface.Invoke)).Invoke();

        // the item template's root: the reading column whose MouseEnter / MouseLeave set IsHovered
        static FrameworkElement RowRoot(ListBoxItem c) =>
            Find<ContentPresenter>(c).Select(cp => VisualTreeHelper.GetChildrenCount(cp) > 0 ? VisualTreeHelper.GetChild(cp, 0) as Grid : null)
                .First(g => g != null && g.DataContext is ChatMessage);

        static bool IsUnder(DependencyObject d, DependencyObject target)
        {
            for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d == target) return true;
            return false;
        }
    }
}
