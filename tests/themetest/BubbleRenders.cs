using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.ViewModels;
using CluadeX.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ThemeTest;

internal static partial class Program
{
    static bool BubblesOnly;

    /// <summary>
    /// The chat as two people talking (owner 2026-10-29): the user's bubble on the left by their avatar, the AI's on the
    /// right with its tail pointing at her avatar, and her avatar busy while she works — renders\bubbles\&lt;theme&gt;-done.png
    /// (a finished exchange, then her thinking over a book) and &lt;theme&gt;-working.png (a tool running in the live row,
    /// then her reply being written at the laptop). Checked: which side each bubble is on and what her avatar is doing.
    /// </summary>
    static async Task BubbleRenders()
    {
        Log("\n## Speech bubbles — the user on the left, the AI on the right by her avatar, the avatar busy while she works");
        var themes = Sp.GetRequiredService<ThemeService>();
        var vm = Sp.GetRequiredService<ChatViewModel>();
        var providerBefore = Settings.Settings.ActiveProvider;
        string dir = Path.Combine(OutRoot, "renders", "bubbles");
        Directory.CreateDirectory(dir);
        try
        {
            MainVm.NavigateToCommand.Execute("Chat");
            Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.OpenAIDev);
            foreach (var id in ThemeIds.All)
            {
                if (OnlyThemes != null && !OnlyThemes.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
                themes.Apply(id, persist: false);

                // 1. A finished exchange, then a new question she is thinking about.
                vm.NewSession();
                var ask = new ChatMessage { Role = MessageRole.User, Content = "ช่วยทำหน้าเว็บร้านนารวยให้หน่อย" };
                var done = new ChatMessage { Role = MessageRole.Assistant,
                    Content = "ได้เลยค่ะ ทำหน้าแรกให้แล้ว มีสินค้า 8 รายการ ค้นหาภาษาไทยได้ และตะกร้าสินค้าค่ะ\n\nเปิดดูได้ที่ UI Studio เลยนะคะ" };
                var again = new ChatMessage { Role = MessageRole.User, Content = "เพิ่มหน้าติดต่อเราด้วย" };
                var think = new ChatMessage { Role = MessageRole.AgentStatus, Content = "⏳ กำลังคิด...", IsStreaming = true };
                foreach (var m in new[] { ask, done, again, think }) vm.Messages.Add(m);
                await Idle(900);
                var view = Find<ChatView>(MainWin).FirstOrDefault(v => v.IsVisible);
                var list = view?.FindName("ChatList") as ListBox;
                list?.ScrollIntoView(think);
                await Idle(400);
                SavePng(Snapshot(RootVisual(MainWin), W, H), Path.Combine(dir, $"{id.ToLowerInvariant()}-done.png"));

                AiAvatar? Avatar(ChatMessage m) => list?.ItemContainerGenerator.ContainerFromItem(m) is DependencyObject c
                    ? Find<AiAvatar>(c).FirstOrDefault(a => a.IsVisible) : null;
                Border? Bubble(ChatMessage m) => list?.ItemContainerGenerator.ContainerFromItem(m) is DependencyObject c
                    ? Find<Border>(c).FirstOrDefault(b => b.IsVisible && b.Background is SolidColorBrush sb && sb.Color.A > 0
                        && Math.Abs(b.CornerRadius.TopLeft - b.CornerRadius.TopRight) > 5) : null;
                double X(FrameworkElement? e) => e != null && list != null ? e.TranslatePoint(new Point(0, 0), list).X : double.NaN;
                double Right(FrameworkElement? e) => e != null && list != null ? e.TranslatePoint(new Point(e.ActualWidth, 0), list).X : double.NaN;

                var userBubble = Bubble(ask);
                var replyBubble = Bubble(done);
                double mid = (list?.ActualWidth ?? 0) / 2;
                Check($"BB {id}: the user's bubble sits on the left", userBubble != null && X(userBubble) < mid && Right(userBubble) < (list?.ActualWidth ?? 0) - 100,
                    $"x={X(userBubble):0} right={Right(userBubble):0} list={list?.ActualWidth:0}");
                Check($"BB {id}: the user's row has no AI avatar", Avatar(ask) == null);
                var doneAvatar = Avatar(done);
                Check($"BB {id}: the reply's bubble is on the right, her avatar right of it",
                    replyBubble != null && doneAvatar != null && Right(replyBubble) > mid && X(doneAvatar) >= Right(replyBubble),
                    $"bubble right={Right(replyBubble):0} avatar x={X(doneAvatar):0}");
                Check($"BB {id}: a finished reply's avatar is still", doneAvatar?.Mode == AiAvatarMode.Idle, $"{doneAvatar?.Mode}");
                Check($"BB {id}: while she waits on the model she reads a book", Avatar(think)?.Mode == AiAvatarMode.Reading, $"{Avatar(think)?.Mode}");

                // 2. A tool running in the live row, then her reply being written.
                think.IsStreaming = false;
                var work = new ChatMessage { Role = MessageRole.ToolAction, Content = "Wrote index.html", ToolName = "Write", IsLive = true, LiveText = "Writing contact.html" };
                var writing = new ChatMessage { Role = MessageRole.Assistant, Content = "กำลังเพิ่มหน้าติดต่อเราให้ค่ะ", IsStreaming = true };
                vm.Messages.Add(work);
                vm.Messages.Add(writing);
                await Idle(700);
                list?.ScrollIntoView(writing);
                await Idle(400);
                SavePng(Snapshot(RootVisual(MainWin), W, H), Path.Combine(dir, $"{id.ToLowerInvariant()}-working.png"));
                Check($"BB {id}: the row a tool runs in shows her at the laptop", Avatar(work)?.Mode == AiAvatarMode.Typing, $"{Avatar(work)?.Mode}");
                Check($"BB {id}: a reply being written shows her at the laptop", Avatar(writing)?.Mode == AiAvatarMode.Typing, $"{Avatar(writing)?.Mode}");
                Check($"BB {id}: a finished status line has no avatar", Avatar(think) == null);
                work.IsLive = false;
                writing.IsStreaming = false;
                await Idle(200);
                Check($"BB {id}: once done, the tool row drops the avatar and the reply's goes still",
                    Avatar(work) == null && Avatar(writing)?.Mode == AiAvatarMode.Idle, $"{Avatar(work)?.Mode} / {Avatar(writing)?.Mode}");
            }
        }
        finally
        {
            Settings.UpdateSettings(s => s.ActiveProvider = providerBefore);
            vm.NewSession();
            themes.Apply(ThemeIds.Midnight, persist: false);
            await Idle(300);
        }
    }
}
