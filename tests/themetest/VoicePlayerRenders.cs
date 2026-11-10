using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CluadeX.Helpers;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Voice.Tts;

namespace ThemeTest;

internal static partial class Program
{
    static bool VoicePlayerOnly;

    /// <summary>Plays nothing: the chat's voice queue is pointed here before anything is asked to play, so a render run
    /// is never heard (2026-11-09: a harness that played the buddy's voice was taken for the app).</summary>
    sealed class SilentVoice : IReplyVoiceOutput
    {
        public SpokenItem Playing;
        public event Action<SpokenItem, string> Ended;
        public bool CanPause => Playing != null;
        public Task PrepareAsync(SpokenItem item, CancellationToken ct)
        {
            item.Audio ??= new SpeechAudio([0], "audio/mpeg", ".mp3");
            return Task.CompletedTask;
        }
        public bool Start(SpokenItem item) { Playing = item; return true; }
        public void Pause() { }
        public void Resume() { }
        public void Stop() => Playing = null;
        public void Finish() { var p = Playing; Playing = null; if (p != null) Ended?.Invoke(p, null); }
    }

    /// <summary>
    /// Reading replies aloud in the real chat page (owner 2026-11-10: a queue that never cuts, a player box to go back and
    /// forward, a click on a bubble or its ▶ to hear it), in every theme:
    ///   - the ▶ on a reply's hover strip plays it: the player box shows above the composer, clear of the card, with
    ///     her name, "reading", 1 / 1; the ▶ turns to pause and the bubble is the queue's current one;
    ///   - a second reply that comes while she reads waits ("1 waiting") — the first is not cut;
    ///   - the box shows the girl the chat shows answering (her face where the theme shows the girls, the logo otherwise);
    ///   - a click or a double click on a bubble's words plays nothing (owner 2026-11-11): only ▶ does.
    /// Saved to renders\voice-player\&lt;theme&gt;.png (the bottom of the chat page) for the eye.
    ///
    ///   dotnet run -- --voice-player --out D:\...\themes
    /// </summary>
    static async Task VoicePlayerShots(object themes)
    {
        Log("\n## Reading replies aloud — the player box, the ▶, and a click on the bubble that plays nothing, every theme");
        string dir = Path.Combine(OutRoot, "renders", "voice-player");
        Directory.CreateDirectory(dir);
        var chat = MainVm.ChatVM;
        var silent = new SilentVoice();
        chat.VoiceQueue.Output = silent;
        bool was = chat.SpeakReplies;
        chat.SpeakReplies = true;

        var first = chat.Messages.First(m => m.Role == MessageRole.Assistant);
        var second = new ChatMessage { Role = MessageRole.Assistant, Timestamp = FixedTime.AddMinutes(2),
            Content = "เพิ่มปุ่มสลับธีมในหน้าตั้งค่าให้แล้วค่ะ ลองเปิดดูได้เลยนะคะ" };
        MainVm.NavigateToCommand.Execute("Permissions");
        await Idle();
        MainVm.NavigateToCommand.Execute("Chat");
        await Idle(200);

        var ids = themes == null ? new List<string> { "current" }
            : ((IEnumerable)Prop(themes, "Themes")).Cast<object>().Select(t => (string)Prop(t, "Id")).ToList();
        foreach (string id in ids)
        {
            if (themes != null) { Invoke(themes, "Apply", id, true); await Idle(200); }
            chat.VoiceQueue.Clear();
            chat.Messages.Remove(second);
            await Idle(100);
            var root = (FrameworkElement)RootVisual(MainWin);
            var bar = Find<CluadeX.Views.VoicePlayerBar>(root).FirstOrDefault();
            Check($"voice {id}: the player box is on the chat page, hidden while empty", bar != null && !bar.IsVisible, $"{bar != null} · visible {bar?.IsVisible}");
            if (bar == null) continue;

            // 1. The ▶ on the reply's hover strip.
            first.IsHovered = true;
            await Idle(120);
            var listen = Find<Button>(root).FirstOrDefault(b => AutomationIdOf(b) == "CxChat.ListenReply" && ReferenceEquals(b.DataContext, first) && b.IsVisible);
            Check($"voice {id}: the reply's hover strip has ▶", listen != null, "");
            if (listen == null) continue;
            listen.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Idle(150);
            var q = chat.VoiceQueue;
            Check($"voice {id}: ▶ plays that reply", q.IsPlaying && ReferenceEquals(q.Current?.Message, first) && ReferenceEquals(silent.Playing?.Message, first) && first.IsVoicePlaying,
                $"{q.State} · current {(q.Current?.Message == first ? "first" : "?")}");
            Check($"voice {id}: the ▶ turns to pause", (string)listen.Content == "\uE769", "");
            // The box shows who the chat shows answering (the AI avatar beside the bubble), not the reply's default girl.
            var active = chat.ActiveCharacter;
            Check($"voice {id}: the box's girl is the one the chat shows", q.Current?.Character?.Own == active.Own && q.Current?.Girl == active.Own,
                $"box {q.Current?.Character?.Own} · voice {q.Current?.Girl} · chat {active.Own}");
            var faceDisc = (FrameworkElement)bar.FindName("FaceDisc");
            bool girlsShown = CluadeX.Views.CharacterLook.GetShow(bar) == Visibility.Visible;
            Check($"voice {id}: her face where the theme shows the girls, the logo otherwise", faceDisc != null && faceDisc.IsVisible == (girlsShown && active.FaceBrush != null),
                $"face {faceDisc?.IsVisible} · girls shown {girlsShown}");
            var card = Find<Border>(root).FirstOrDefault(b => b.Name == "ComposerCard");
            Check($"voice {id}: the box shows above the composer, clear of it", bar.IsVisible && card != null && Bottom(bar, root) <= Top(card, root) + 1,
                $"box bottom {Bottom(bar, root):0} · card top {(card == null ? -1 : Top(card, root)):0}");

            // 2. A new reply while she reads: it waits, the first is not cut.
            chat.Messages.Add(second);
            q.Enqueue(new SpokenItem(second.Content, true, second.Character.Own, second));
            await Idle(200);
            first.IsHovered = false;
            Check($"voice {id}: a new reply waits — the first is not cut", ReferenceEquals(silent.Playing?.Message, first) && q.Waiting == 1, $"waiting {q.Waiting}");
            var full = await Stable(() => (root, W, H), $"voice-player/{id}");
            SavePng(new CroppedBitmap(full, new Int32Rect(0, H - 320, W, 320)), Path.Combine(dir, id.ToLowerInvariant() + ".png"));

            // 3. A click or a double click on the second bubble's words plays nothing.
            silent.Finish();   // first done → second plays by itself
            await Idle(100);
            Check($"voice {id}: when the first ends, the waiting one plays", ReferenceEquals(silent.Playing?.Message, second), "");
            q.Stop();
            await Idle(60);
            var words = Find<SelectableTextBlock>(root).FirstOrDefault(t => ReferenceEquals(t.DataContext, second) || ReferenceEquals(Up<ListBoxItem>(t)?.DataContext, second));
            Check($"voice {id}: the second reply's words are on screen", words != null, "");
            if (words == null) continue;
            Press(words, 1); Release(words);
            Press(words, 2); Release(words);   // a double click (selects a word): nothing plays
            await Task.Delay(800); await Idle();
            Check($"voice {id}: a double click on the words plays nothing", silent.Playing == null, $"{q.State}");
            // A click on a reply's words reads nothing (owner 2026-11-11: it read a reply already heard again): only ▶.
            Press(words, 1); Release(words);
            await Task.Delay(800); await Idle();
            Check($"voice {id}: a click on the bubble's words plays nothing", silent.Playing == null && !second.IsVoicePlaying, $"{q.State}");
            q.Clear();
            await Idle(60);
            Check($"voice {id}: ✕ empties and hides the box", !bar.IsVisible && !second.IsVoiceCurrent, "");
        }
        chat.Messages.Remove(second);
        chat.VoiceQueue.Clear();
        chat.SpeakReplies = was;
        if (themes != null) { Invoke(themes, "Apply", "Midnight", true); await Idle(200); }
    }

    static string AutomationIdOf(DependencyObject d) => System.Windows.Automation.AutomationProperties.GetAutomationId(d);
    static double Top(FrameworkElement e, System.Windows.Media.Visual root) => e.TransformToAncestor(root).Transform(new Point(0, 0)).Y;
    static double Bottom(FrameworkElement e, System.Windows.Media.Visual root) => e.TransformToAncestor(root).Transform(new Point(0, e.ActualHeight)).Y;

    static T Up<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = d; p != null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }

    /// <summary>A left press on <paramref name="on"/> as the mouse makes it: PreviewMouseDown tunnels from the window down,
    /// and WPF turns it into PreviewMouseLeftButtonDown on every element on the way (that one is a direct event — raised
    /// on the words alone, the bubble's handler never saw it).</summary>
    static void Press(UIElement on, int clicks)
    {
        var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = on };
        typeof(MouseButtonEventArgs).GetProperty("ClickCount")!.GetSetMethod(true)!.Invoke(e, [clicks]);
        on.RaiseEvent(e);
    }

    static void Release(UIElement on)
    {
        var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent, Source = on };
        on.RaiseEvent(e);
    }
}
