using System.IO;
using System.Windows;
using System.Windows.Automation;
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
    /// <summary>
    /// The chat's boxes in the look of who is talking (ChatCharacter), on the real main window in every theme:
    /// renders\persona\&lt;theme&gt;-claude.png (Claude's question, options, permission prompt and the red danger prompt) and
    /// &lt;theme&gt;-room.png (the cowork room, Claude then Grok answering). Checked: her colour on the cards in every theme
    /// but Classic, the theme's own look in Classic, and the danger prompt red everywhere.
    /// </summary>
    static async Task PersonaRenders()
    {
        Log("\n## Persona boxes — the question / permission boxes and the composer in the look of who is talking");
        var themes = Sp.GetRequiredService<ThemeService>();
        var vm = Sp.GetRequiredService<ChatViewModel>();
        var providerBefore = Settings.Settings.ActiveProvider;
        string dir = Path.Combine(OutRoot, "renders", "persona");
        Directory.CreateDirectory(dir);
        var open = new List<ChatMessage>();
        try
        {
            MainVm.NavigateToCommand.Execute("Chat");
            foreach (var id in ThemeIds.All)
            {
                if (OnlyThemes != null && !OnlyThemes.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
                themes.Apply(id, persist: false);
                bool lookOn = id != ThemeIds.Classic;

                // Claude: question + options, a permission prompt, the red danger prompt.
                Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.Anthropic);
                vm.NewSession();
                var who = ChatViewModel.CharacterFor(AiProviderType.Anthropic, vm.Messages);
                vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "ช่วยแก้ปุ่ม login ให้หน่อย" });
                var q = Track(open, new ChatMessage { Role = MessageRole.Question, Content = "❓ จะให้แก้ไฟล์ไหนก่อนคะ?", QuestionOptions = ["login.dart", "home.dart"],
                    QuestionGirl = who.Girl, Character = who, QuestionCallback = _ => { } });
                vm.Messages.Add(q);
                var p = Track(open, new ChatMessage { Role = MessageRole.PermissionRequest, Content = "Allow run_command to run `flutter test`?", Character = who, PermissionCallback = _ => { } });
                vm.Messages.Add(p);
                var d = Track(open, new ChatMessage { Role = MessageRole.PermissionRequest, Content = "Allow run_command to run `rd /s /q C:\\Users\\me`?", Character = who,
                    PermissionDanger = "deletes your home folder C:\\Users\\me", PermissionCallback = _ => { } });
                vm.Messages.Add(d);
                await Idle(900);
                var view = Find<ChatView>(MainWin).FirstOrDefault(v => v.IsVisible);
                var list = view?.FindName("ChatList") as ListBox;
                list?.ScrollIntoView(d);
                await Idle(300);
                SavePng(Snapshot(RootVisual(MainWin), W, H), Path.Combine(dir, $"{id.ToLowerInvariant()}-claude.png"));

                Border Card(ChatMessage m, string aid) => list?.ItemContainerGenerator.ContainerFromItem(m) is DependencyObject c
                    ? Find<Border>(c).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == aid) : null;
                Color Res(string key) => ((SolidColorBrush)view!.FindResource(key)).Color;
                var qCard = Card(q, "CxGuard.QuestionCard");
                var pCard = Card(p, "CxGuard.PermissionCard");
                var dCard = Card(d, "CxGuard.PermissionCard");
                Color want = lookOn ? who.Accent : Res("Accent.1");
                Check($"PB {id}: the question card's edge is {(lookOn ? "Claude's orange" : "the theme accent (Classic keeps its look)")}",
                    qCard?.BorderBrush is SolidColorBrush qb && qb.Color == want, $"{(qCard?.BorderBrush as SolidColorBrush)?.Color} want {want}");
                Color wantP = lookOn ? who.Accent : Res("Status.Warning");
                Check($"PB {id}: the permission prompt's edge is {(lookOn ? "Claude's orange" : "the theme's warning colour")}",
                    pCard?.BorderBrush is SolidColorBrush pb && pb.Color == wantP, $"{(pCard?.BorderBrush as SolidColorBrush)?.Color} want {wantP}");
                Check($"PB {id}: the danger prompt stays red",
                    dCard?.BorderBrush is SolidColorBrush db && db.Color == Res("Status.Danger")
                    && dCard.Background is SolidColorBrush dbg && dbg.Color == Res("Chat.Diff.Removed.Background"));

                // The cowork room: Claude, then Grok answering — the composer wears Grok's colour.
                foreach (var m in open) { m.CloseQuestion("-"); m.ResolvePermission(PermissionOutcome.Denied); }
                open.Clear();
                Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.BrainX);
                vm.NewSession();
                vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "ช่วยแก้ปุ่ม login ให้หน่อย" });
                var r = new ChatMessage { Role = MessageRole.Assistant };
                vm.Messages.Add(r);
                r.Content = "📨 ส่งเข้าห้อง cowork แล้ว · ถึง: ใครก็ได้ที่ว่าง\n\n### 💬 claude\n\nดูโค้ดแล้วค่ะ ปุ่มอยู่ใน `lib/login.dart`\n\n### 💬 grok\n\nเดี๋ยวแก้ให้ค่ะ";
                await Idle(900);
                view = Find<ChatView>(MainWin).FirstOrDefault(v => v.IsVisible);
                SavePng(Snapshot(RootVisual(MainWin), W, H), Path.Combine(dir, $"{id.ToLowerInvariant()}-room.png"));
                var frame = view?.FindName("ComposerCard") as Border;
                bool focused = frame?.IsKeyboardFocusWithin == true;   // the input box has the focus: the full colour
                Color wantFrame = lookOn ? (focused ? ChatCharacter.Grok.Accent : ChatCharacter.Grok.FrameBrush.Color)
                                         : Res(focused ? "Accent.1" : "Border.Default");
                Check($"PB {id}: the room's composer is framed in {(lookOn ? "Grok's colour (the member answering)" : "the theme's own border")}",
                    vm.ActiveCharacter == ChatCharacter.Grok && frame?.BorderBrush is SolidColorBrush fb && fb.Color == wantFrame,
                    $"{vm.ActiveCharacter} {(frame?.BorderBrush as SolidColorBrush)?.Color} want {wantFrame} focused={focused}");
            }
        }
        finally
        {
            foreach (var m in open) { m.CloseQuestion("-"); m.ResolvePermission(PermissionOutcome.Denied); }
            Settings.UpdateSettings(s => s.ActiveProvider = providerBefore);
            vm.NewSession();
            themes.Apply(ThemeIds.Midnight, persist: false);
            await Idle(300);
        }
    }

    static ChatMessage Track(List<ChatMessage> open, ChatMessage m) { open.Add(m); return m; }
}
