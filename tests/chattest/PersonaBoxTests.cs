using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Safety;
using CluadeX.ViewModels;
using CluadeX.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>PB — the boxes look like who is talking (owner 2026-10-18): the question card, its option pills and the
    /// permission prompt take the girl of the provider answering (Claude = the orange girl), the cowork room's boxes and
    /// composer frame the member answering; the red danger prompt stays red; Classic keeps its own look. Renders every
    /// theme × girl into renders\persona\ to look at.</summary>
    static async Task TestPersonaBoxes()
    {
        Log("\n## PB — boxes in the look of who is talking (girl + colour per provider / room member)");
        var themes = Sp.GetRequiredService<ThemeService>();
        var providerBefore = Settings.Settings.ActiveProvider;
        try
        {
            // PB1 — provider → girl.
            Check("PB1 Claude (API) and Claude Dev are the orange girl (claude.png)",
                ChatCharacter.ForProvider(AiProviderType.Anthropic) == ChatCharacter.Claude && ChatCharacter.ForProvider(AiProviderType.ClaudeDev) == ChatCharacter.Claude
                && ChatCharacter.Claude.Girl == "claude" && IsOrange(ChatCharacter.Claude.Accent), ChatCharacter.Claude.Accent.ToString());
            Check("PB1 OpenAI = Codex, Gemini = Gemini, BrainX = the BrainX girl",
                ChatCharacter.ForProvider(AiProviderType.OpenAI).Girl == "codex" && ChatCharacter.ForProvider(AiProviderType.Gemini).Girl == "gemini"
                && ChatCharacter.ForProvider(AiProviderType.BrainX).Girl == "brainx");
            Check("PB1 a model on this PC (GGUF, llama-server, Ollama) is มายด์ (mai.png)",
                new[] { AiProviderType.Local, AiProviderType.LlamaServer, AiProviderType.Ollama }.All(p => ChatCharacter.ForProvider(p) == ChatCharacter.Mind)
                && ChatCharacter.Mind.Girl == "mai");
            Check("PB1 every provider's girl has her picture in the build",
                Enum.GetValues<AiProviderType>().All(p => ChatCharacter.ForProvider(p).Image != null),
                string.Join(",", Enum.GetValues<AiProviderType>().Where(p => ChatCharacter.ForProvider(p).Image == null)));
            Check("PB1 the model box's portrait is the same girl as the boxes'",
                Enum.GetValues<AiProviderType>().All(p => new ChatViewModel.ChatModelChoice(p, "m", "x", "").Girl == ChatCharacter.ForProvider(p).Girl));
            Check("PB1 every girl has her own colour (no two the same)", ChatCharacter.All.Select(c => c.Accent).Distinct().Count() == ChatCharacter.All.Count);
            var badFace = ChatCharacter.All.Where(c => c.FaceBrush is not { } f || f.Viewbox.Left < 0 || f.Viewbox.Top < 0 || f.Viewbox.Right > 1 || f.Viewbox.Bottom > 0.25
                || Math.Abs(f.Viewbox.Width * ((System.Windows.Media.Imaging.BitmapSource)c.Image!).PixelWidth - f.Viewbox.Height * ((System.Windows.Media.Imaging.BitmapSource)c.Image!).PixelHeight) > 1)
                .Select(c => c.Name).ToList();
            Check("PB1 every round portrait is her face: a square crop inside the top quarter of her picture (not the whole figure)", badFace.Count == 0, string.Join(",", badFace));
            Check("PB1 the model box's round portrait is the same face crop",
                Enum.GetValues<AiProviderType>().All(p => new ChatViewModel.ChatModelChoice(p, "m", "x", "").GirlFace == ChatCharacter.ForProvider(p).FaceBrush));

            // PB2 — cowork room member → girl.
            (string id, ChatCharacter who)[] room =
            [
                ("claude", ChatCharacter.Claude), ("local-agent-mode-brainx-brain", ChatCharacter.Claude), ("codex", ChatCharacter.Codex),
                ("gemini", ChatCharacter.Gemini), ("grok", ChatCharacter.Grok), ("mind", ChatCharacter.Mind), ("broker", ChatCharacter.Mind),
                ("cluadex", ChatCharacter.CluadeX), ("Codex", ChatCharacter.Codex),
            ];
            var wrong = room.Where(r => ChatCharacter.ForRoomAgent(r.id) != r.who).Select(r => $"{r.id}→{ChatCharacter.ForRoomAgent(r.id)}").ToList();
            Check("PB2 each room member is her own girl (Claude Code's local seat is Claude, the broker is มายด์)", wrong.Count == 0, string.Join(", ", wrong));
            var stranger = ChatCharacter.ForRoomAgent("zed-bot");
            Check("PB2 someone new in the room stands as the BrainX girl under her own id", stranger.Girl == "brainx" && stranger.Name == "zed-bot");

            // PB3 — who is speaking in a room reply.
            const string reply = "📨 ส่งเข้าห้อง cowork แล้ว\n\n### 💬 claude\n\nดูแล้วค่ะ\n\n### 💬 codex\n\nแก้แล้ว\n\n### 💬 claude\n\nตรวจแล้ว";
            Check("PB3 a reply's speakers in order, and the one speaking last",
                ChatCharacter.RoomSpeakersIn(reply).SequenceEqual(["claude", "codex"]) && ChatCharacter.LastRoomSpeakerIn(reply) == "claude"
                && ChatCharacter.LastRoomSpeakerIn("### no speaker here") == null);
            var roomMsgs = new List<ChatMessage> { new() { Role = MessageRole.User, Content = "แก้ปุ่ม" }, new() { Role = MessageRole.Assistant, Content = "📨\n\n### 💬 codex\n\nทำอยู่" } };
            Check("PB3 the room's box girl is the member answering now; before anyone answers, the BrainX girl",
                ChatViewModel.CharacterFor(AiProviderType.BrainX, roomMsgs) == ChatCharacter.Codex
                && ChatViewModel.CharacterFor(AiProviderType.BrainX, roomMsgs.Take(1)) == ChatCharacter.BrainX
                && ChatViewModel.CharacterFor(AiProviderType.Anthropic, roomMsgs) == ChatCharacter.Claude);

            // PB4 — readable: text on her fill, and the theme's text on a card washed in her colour.
            var badFill = ChatCharacter.All.Where(c => c.OnAccentBrush.Color.A != 255 || ChatCharacter.Contrast(c.Accent, c.OnAccentBrush.Color) < 4.5).Select(c => c.Name).ToList();
            Check("PB4 the text on her colour (name chip, Answer/Allow) is opaque with contrast ≥ 4.5 for every girl", badFill.Count == 0, string.Join(",", badFill));
            var unreadable = new List<string>();
            foreach (var id in ThemeIds.All.Where(t => t != ThemeIds.Classic))
            {
                themes.Apply(id, persist: false);
                await Render();
                var bg = ((SolidColorBrush)View.FindResource("Bg.Elevated")).Color;
                var fg = ((SolidColorBrush)View.FindResource("Text.Primary")).Color;
                foreach (var c in ChatCharacter.All)
                {
                    var washed = Blend(bg, c.Accent, 0x2E / 255.0);   // the strongest corner of her wash
                    double k = ChatCharacter.Contrast(fg, washed);
                    if (k < 4.5) unreadable.Add($"{id}/{c.Name} {k:F1}");
                }
            }
            Check("PB4 the question text stays readable on her wash in Midnight, NeonCyber and Lavender (≥ 4.5)", unreadable.Count == 0, string.Join(", ", unreadable));

            // PB5 — live: ask_user and a permission prompt under Claude get Claude's look on screen.
            themes.Apply(ThemeIds.Midnight, persist: false);
            Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.Anthropic);
            await FreshChat();
            var askTask = (Task<string>)typeof(ChatViewModel).GetMethod("AskUserAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Vm, ["Which file?", new List<string> { "login.dart", "home.dart" }])!;
            ChatMessage q = null;
            await WaitUntil(() => (q = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Question && !m.QuestionAnswered)) != null, 5000);
            await Task.Delay(700); await Render();
            Check("PB5 ask_user under Claude: the question's girl is the orange girl (not a random one)",
                q != null && q.Character == ChatCharacter.Claude && q.QuestionGirl == "claude", $"{q?.Character} girl={q?.QuestionGirl}");
            var card = q == null ? null : ById<Border>(q, "CxGuard.QuestionCard");
            Check("PB5 …her colour on the card's border, her tag (portrait + name) on top",
                card != null && BrushIs(card.BorderBrush, ChatCharacter.Claude.Accent) && ById<StackPanel>(q, "CxGuard.QuestionWho") is { IsVisible: true },
                $"border={(card?.BorderBrush as SolidColorBrush)?.Color}");
            var pill = q == null ? null : Find<Button>(Container(q)).FirstOrDefault(b => b.Content as string == "login.dart");
            var send = q == null ? null : Find<Button>(Container(q)).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "CxGuard.QuestionSend");
            Check("PB5 …the option pills are edged in her colour and the Answer button is filled with it",
                pill != null && BrushIs(pill.BorderBrush, ChatCharacter.Claude.Accent) && send != null && BrushIs(send.Background, ChatCharacter.Claude.Accent));

            var permHandler = (Func<PermissionRequestInfo, Task<bool>>)typeof(AgentToolService)
                .GetField("OnPermissionRequired", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Sp.GetRequiredService<AgentToolService>())!;
            var permTask = permHandler(new PermissionRequestInfo { ToolName = "run_command", Detail = "npm test", AlwaysAsk = true });
            ChatMessage perm = null;
            await WaitUntil(() => (perm = Vm.Messages.LastOrDefault(m => m.Role == MessageRole.PermissionRequest && !m.PermissionAnswered)) != null, 5000);
            await Render();
            var permCard = perm == null ? null : ById<Border>(perm, "CxGuard.PermissionCard");
            Check("PB5 a permission prompt under Claude: her girl, her colour on its edge, her tag",
                perm?.Character == ChatCharacter.Claude && permCard != null && BrushIs(permCard.BorderBrush, ChatCharacter.Claude.Accent)
                && ById<StackPanel>(perm, "CxGuard.PermissionWho") is { IsVisible: true }, $"{perm?.Character} border={(permCard?.BorderBrush as SolidColorBrush)?.Color}");
            Check("PB5 …and she stands beside her permission prompt, full figure, like beside a question",
                ById<System.Windows.Controls.Image>(perm, "CxGuard.PermissionGirl") is { IsVisible: true, Source: not null, ActualHeight: > 100 },
                "girl visible=" + ById<System.Windows.Controls.Image>(perm, "CxGuard.PermissionGirl")?.IsVisible);
            SaveShot(Path.Combine(AppContext.BaseDirectory, "chat-persona-claude.png"));
            q?.AnswerQuestion("login.dart");
            perm?.ResolvePermission(PermissionOutcome.Denied);
            await Task.WhenAny(Task.WhenAll(askTask, permTask), Task.Delay(3000));

            // PB6 — the red danger prompt stays red, whoever asks.
            var dangerTask = DangerApproval.Handler!(new DangerRequest("run_command", "rd /s /q C:\\Users\\me", "deletes your home folder"));
            ChatMessage danger = null;
            await WaitUntil(() => (danger = Vm.Messages.LastOrDefault(m => m.IsPermissionDanger && !m.PermissionAnswered)) != null, 5000);
            await Render();
            var dCard = danger == null ? null : ById<Border>(danger, "CxGuard.PermissionCard");
            var red = ((SolidColorBrush)View.FindResource("Status.Danger")).Color;
            var redBg = ((SolidColorBrush)View.FindResource("Chat.Diff.Removed.Background")).Color;
            Check("PB6 the danger prompt under Claude is still red: red edge 4,2,2,2 on the red background, no girl tag",
                danger?.Character == ChatCharacter.Claude && dCard != null && BrushIs(dCard.BorderBrush, red) && BrushIs(dCard.Background, redBg)
                && dCard.BorderThickness == new Thickness(4, 2, 2, 2) && ById<StackPanel>(danger, "CxGuard.PermissionWho") is not { IsVisible: true },
                $"border={(dCard?.BorderBrush as SolidColorBrush)?.Color} bg={(dCard?.Background as SolidColorBrush)?.Color} t={dCard?.BorderThickness}");
            Check("PB6 …and no girl stands beside the red prompt",
                ById<System.Windows.Controls.Image>(danger, "CxGuard.PermissionGirl") is not { IsVisible: true });
            SaveShot(Path.Combine(AppContext.BaseDirectory, "chat-persona-danger.png"));
            danger?.ResolvePermission(PermissionOutcome.Denied);
            await Task.WhenAny(dangerTask, Task.Delay(3000));

            // PB7 — the cowork room: the composer frame and tag follow the member answering.
            Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.BrainX);
            await FreshChat();
            // The frame colour without the caret in the box (with it, the box shows her stronger accent). The chat page no
            // longer jumps to the editor on agent edits (2026-10-20), so the input box can still hold the focus here.
            System.Windows.Input.Keyboard.ClearFocus();
            await Render();
            var composer = (Border)View.FindName("ComposerCard");
            var who = (ContentControl)View.FindName("ComposerWho");
            Check("PB7 the room before anyone answers: the BrainX girl frames the composer",
                Vm.ActiveCharacter == ChatCharacter.BrainX && BrushIs(composer.BorderBrush, ChatCharacter.BrainX.FrameBrush.Color) && who.Content == ChatCharacter.BrainX);
            var live = new ChatMessage { Role = MessageRole.Assistant, IsStreaming = true };
            Vm.Messages.Add(live);
            live.Content = "📨 ส่งเข้าห้องแล้ว\n\n### 💬 claude\n\nดูให้ค่ะ";
            await Render();
            bool claudeNow = Vm.ActiveCharacter == ChatCharacter.Claude && who.Content == ChatCharacter.Claude && BrushIs(composer.BorderBrush, ChatCharacter.Claude.FrameBrush.Color);
            live.Content += "\n\n### 💬 codex\n\nแก้แล้ว [done]";
            await Render();
            Check("PB7 …then whoever is speaking: Claude, then Codex takes over the frame and the tag",
                claudeNow && Vm.ActiveCharacter == ChatCharacter.Codex && who.Content == ChatCharacter.Codex && BrushIs(composer.BorderBrush, ChatCharacter.Codex.FrameBrush.Color),
                $"claudeNow={claudeNow} active={Vm.ActiveCharacter} frame={(composer.BorderBrush as SolidColorBrush)?.Color}");
            var speakers = Find<Grid>(Container(live)).Where(g => g.Tag is string t && t.StartsWith("speaker:")).Select(g => (string)g.Tag).ToList();
            Check("PB7 the reply names each member with her own tag (💬 headings)", speakers.SequenceEqual(["speaker:claude", "speaker:codex"]), string.Join(",", speakers));
            live.IsStreaming = false;

            // PB8 — Classic keeps its own look: no girl colour, no tags.
            themes.Apply(ThemeIds.Classic, persist: false);
            Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.Anthropic);
            await FreshChat();
            var cq = AddOpenQuestion(ChatCharacter.Claude);
            System.Windows.Input.Keyboard.ClearFocus();
            await Task.Delay(700); await Render();
            var cCard = ById<Border>(cq, "CxGuard.QuestionCard");
            var accent1 = ((SolidColorBrush)View.FindResource("Accent.1")).Color;
            var border = ((SolidColorBrush)View.FindResource("Border.Default")).Color;
            Check("PB8 Classic: the question card keeps the theme's accent, no girl tag; the composer keeps its plain border",
                cCard != null && BrushIs(cCard.BorderBrush, accent1) && ById<StackPanel>(cq, "CxGuard.QuestionWho") is not { IsVisible: true }
                && !who.IsVisible && BrushIs(composer.BorderBrush, border),
                $"card={(cCard?.BorderBrush as SolidColorBrush)?.Color} accent={accent1} composer={(composer.BorderBrush as SolidColorBrush)?.Color}");
            cq.CloseQuestion("-");

            // PB9 — renders: every theme × girl (question + options + permission + composer; the room with two members).
            string dir = Path.Combine(AppContext.BaseDirectory, "renders", "persona");
            Directory.CreateDirectory(dir);
            (string name, AiProviderType p)[] cases =
            [
                ("claude", AiProviderType.Anthropic), ("codex", AiProviderType.OpenAI), ("gemini", AiProviderType.Gemini),
                ("mai-local", AiProviderType.Local), ("room", AiProviderType.BrainX),
            ];
            int shots = 0;
            foreach (var id in ThemeIds.All)
            {
                themes.Apply(id, persist: false);
                foreach (var (name, p) in cases)
                {
                    Settings.UpdateSettings(s => s.ActiveProvider = p);
                    await FreshChat();
                    Vm.Messages.Add(new ChatMessage { Role = MessageRole.User, Content = "ช่วยแก้ปุ่ม login ให้หน่อย" });
                    if (p == AiProviderType.BrainX)
                    {
                        var r = new ChatMessage { Role = MessageRole.Assistant };
                        Vm.Messages.Add(r);
                        r.Content = "📨 ส่งเข้าห้อง cowork แล้ว · ถึง: ใครก็ได้ที่ว่าง\n\n### 💬 claude\n\nดูโค้ดแล้วค่ะ ปุ่มอยู่ใน `lib/login.dart`\n\n### 💬 grok\n\nเดี๋ยวแก้ให้ค่ะ";
                    }
                    var c = ChatViewModel.CharacterFor(p, Vm.Messages);
                    var oq = AddOpenQuestion(c);
                    var op = new ChatMessage { Role = MessageRole.PermissionRequest, Content = "Allow run_command to run `flutter test`?", Character = c, PermissionCallback = _ => { } };
                    Vm.Messages.Add(op);
                    ChatMessage od = null;
                    if (name == "claude")
                        Vm.Messages.Add(od = new ChatMessage { Role = MessageRole.PermissionRequest, Content = "Allow run_command to run `rd /s /q C:\\Users\\me`?",
                            Character = c, PermissionDanger = "deletes your home folder C:\\Users\\me", PermissionCallback = _ => { } });
                    await Task.Delay(750); await Render();
                    ChatList.ScrollIntoView(Vm.Messages[^1]);
                    await Render();
                    SaveShot(Path.Combine(dir, $"{id.ToLowerInvariant()}-{name}.png"));
                    shots++;
                    oq.CloseQuestion("-");
                    op.ResolvePermission(PermissionOutcome.Denied);
                    od?.ResolvePermission(PermissionOutcome.Denied);
                }
            }
            Check($"PB9 renders: {ThemeIds.All.Count} themes × {cases.Length} girls in renders\\persona\\", shots == ThemeIds.All.Count * cases.Length, dir);
        }
        finally
        {
            themes.Apply(ThemeIds.Midnight, persist: false);
            Settings.UpdateSettings(s => s.ActiveProvider = providerBefore);
            await FreshChat();
        }
    }

    static ChatMessage AddOpenQuestion(ChatCharacter who)
    {
        var q = new ChatMessage
        {
            Role = MessageRole.Question, Content = "❓ จะให้แก้ไฟล์ไหนก่อนคะ?", QuestionOptions = ["login.dart", "home.dart", "ทั้งสองไฟล์"],
            QuestionGirl = who.Girl, Character = who, QuestionCallback = _ => { },
        };
        Vm.Messages.Add(q);
        return q;
    }

    static T ById<T>(ChatMessage m, string automationId) where T : FrameworkElement
    {
        var c = Container(m);
        return c == null ? null : Find<T>(c).FirstOrDefault(e => AutomationProperties.GetAutomationId(e) == automationId);
    }

    static bool BrushIs(Brush b, Color c) => b is SolidColorBrush s && s.Color == c;

    static bool IsOrange(Color c) => c.R > 0xD0 && c.G > 0x60 && c.G < 0xB0 && c.B < 0x70;

    static Color Blend(Color under, Color over, double alpha) => Color.FromRgb(
        (byte)Math.Round(under.R + (over.R - under.R) * alpha), (byte)Math.Round(under.G + (over.G - under.G) * alpha), (byte)Math.Round(under.B + (over.B - under.B) * alpha));
}
