using System.Collections;
using System.Reflection;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.ViewModels;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>NA — a task goes to a new chat instead of the chat on screen, when the owner says so (owner 2026-11-13:
    /// "ถ้าคำสั่งที่เข้ามากลางแชท … ก็แยกแชทไปดีกว่าไหม"): the button on a message waiting for the running reply, the
    /// card for a new task in a chat that is already long, the card for another model in such a chat, and the setting
    /// that turns the two cards off.</summary>
    static async Task TestNewChatAdvice()
    {
        Log("\n## NA — a task of its own can go to a new chat (the owner's call)");
        const string Task1 = "[NA1b] เพิ่มหน้ารายงานยอดขายรายเดือนพร้อมกราฟ และปุ่ม Export เป็น CSV ในหน้า Dashboard";
        const string Task3 = "[NA3] เพิ่มหน้ารายงานสต็อกสินค้ารายสัปดาห์พร้อมกราฟ และปุ่ม Export เป็น CSV ในหน้า Dashboard";
        const string Task4 = "[NA4] เพิ่มหน้าสรุปลูกค้าใหม่รายเดือนพร้อมตาราง และปุ่มพิมพ์รายงานในหน้า Dashboard";
        const string Task4b = "[NA4b] เพิ่มหน้าสรุปยอดคืนสินค้ารายเดือนพร้อมตาราง และปุ่มพิมพ์รายงานในหน้า Dashboard";
        int bigBefore = NewChatAdvice.BigChatTokens;
        bool settingBefore = Settings.Settings.SuggestNewChat;
        var all = (IDictionary)typeof(AiProviderManager).GetField("_providers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Providers)!;
        var realOllama = all[AiProviderType.Ollama];
        var other = new FakeProvider();
        var vmType = typeof(ChatViewModel);
        void RefreshBox() => vmType.GetMethod("RefreshChatModelChoices", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Vm, null);
        ChatMessage? Card() => Vm.Messages.LastOrDefault(m => m.Role == MessageRole.Question && !m.QuestionAnswered);
        ChatSession? ChatTitled(string start) => Vm.Sessions.FirstOrDefault(s => s.Title.StartsWith(start, StringComparison.Ordinal));
        var gate = new SemaphoreSlim(0);
        try
        {
            Settings.UpdateSettings(s => s.SuggestNewChat = true);
            NewChatAdvice.BigChatTokens = bigBefore;   // a harness chat is far below it: no card until a test lowers it

            // ─── NA1: a message waiting for the running reply goes to a new chat ───
            await FreshChat();
            Fake.Script.Clear();
            Fake.ScriptFor["[NA1]"] = new Queue<FakeStep>([FakeStep.Gated(["[NA1] working"], gate, [" — done"])]);
            Fake.ScriptFor["[NA1b]"] = new Queue<FakeStep>([FakeStep.Reply("[NA1b] the report page is in")]);
            await Send("[NA1] a long task in this chat");
            await WaitUntil(() => Vm.Messages.Any(m => m.Content.Contains("[NA1] working")));
            var chatA = Vm.CurrentSession!;
            await Send(Task1);
            var queued = Vm.Messages.FirstOrDefault(m => m.IsQueuedNote && m.Content == Task1);
            Check("NA1 typed while the reply runs: it waits in this chat's queue", queued is { QueueRow: not null }, queued == null ? "no bubble" : "");
            await Render();
            if (queued != null) ChatList.ScrollIntoView(queued);
            await Render();
            var chips = Find<System.Windows.Controls.Button>(Win).Where(b => b.IsVisible && b.DataContext == queued
                && b.Command is not null && (ReferenceEquals(b.Command, Vm.MoveQueuedToNewChatCommand) || ReferenceEquals(b.Command, Vm.SendNowCommand))).ToList();
            Check("NA1 its row shows both buttons: Send now, and do it in a new chat",
                chips.Any(b => ReferenceEquals(b.Command, Vm.SendNowCommand)) && chips.Any(b => ReferenceEquals(b.Command, Vm.MoveQueuedToNewChatCommand)
                    && System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "QueuedToNewChat"),
                $"{chips.Count} button(s) on the row");
            int rowsBefore = Vm.Messages.Count;
            Vm.MoveQueuedToNewChatCommand.Execute(queued);
            await Render();
            var moved = ChatTitled("[NA1b]");
            Check("NA1 'do it in a new chat' takes it out of this chat: the bubble and its queue row are gone, a notice says where it went",
                moved != null && !Vm.Messages.Any(m => m.Content == Task1) && Vm.Messages.Count == rowsBefore - 1
                && Vm.Messages.Any(m => m.IsNotice && m.Content.Contains(moved.Title)) && ReferenceEquals(Vm.CurrentSession, chatA),
                $"moved={moved?.Title} rows {rowsBefore}→{Vm.Messages.Count}");
            bool ran = await WaitUntil(() => Fake.Calls.Any(c => c.Input.Contains("[NA1b]")), 8000);
            await WaitUntil(() => moved != null && !RunIn(moved) && (Persist.LoadSession(moved.Id)?.Messages.Any(m => m.Content.Contains("the report page is in")) ?? false), 8000);
            var stored = moved == null ? null : Persist.LoadSession(moved.Id);
            Check("NA1 the new chat starts with that message and answers it off screen, in the same folder and project",
                ran && stored != null && stored.Messages.Any(m => m.Role == MessageRole.User && m.Content == Task1)
                && stored.Messages.Any(m => m.Content.Contains("the report page is in")) && moved!.ProjectPath == (chatA.ProjectPath ?? "") && moved.ProjectId == chatA.ProjectId,
                $"ran={ran} rows={stored?.Messages.Count} folder='{moved?.ProjectPath}' vs '{chatA.ProjectPath}'");
            Check("NA1 it is the owner's own chat: no parent, so it never reports back or wakes this one", moved != null && ChatLinkStore.Current?.Get(moved.Id) == null && moved.ParentId == null);
            gate.Release();
            await WaitIdle();
            Check("NA1 this chat's agent never read it", !Fake.Calls.Any(c => c.Input.Contains("[NA1]") && c.History.Any(h => h.Content.Contains("[NA1b]")))
                && !Vm.Messages.Any(m => m.Role == MessageRole.User && m.Content.Contains("[NA1b]")));

            // ─── NA2: a message the agent has read already stays ───
            var gate2 = new SemaphoreSlim(0);
            Fake.ScriptFor["[NA2]"] = new Queue<FakeStep>([FakeStep.Gated(["[NA2] working"], gate2, [" — done"])]);
            await Send("[NA2] another long task");
            await WaitUntil(() => Vm.Messages.Any(m => m.Content.Contains("[NA2] working")));
            await Send("[NA2b] เพิ่มหน้ารายงานการเข้าใช้งานรายวันพร้อมกราฟ และปุ่ม Export เป็น CSV ในหน้า Dashboard");
            var read = Vm.Messages.First(m => m.IsQueuedNote && m.Content.StartsWith("[NA2b]"));
            var runs = (IList)vmType.GetField("_runs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Vm)!;
            ((AgentRunScope)runs[0]!.GetType().GetProperty("Scope")!.GetValue(runs[0])!).DrainUserNotes();   // the agent took it
            int chatsBefore = Vm.Sessions.Count;
            Vm.MoveQueuedToNewChatCommand.Execute(read);
            Check("NA2 a message the agent has read already is not moved: it stays, no chat is made, the status says why",
                Vm.Messages.Contains(read) && Vm.Sessions.Count == chatsBefore && Vm.StatusText == L("chat.newChatAdvice.alreadyRead"), Vm.StatusText);
            gate2.Release();
            await WaitIdle();

            // ─── NA3: a new task sent in a chat that is already long → a card; "in a new chat" ───
            NewChatAdvice.BigChatTokens = 1;   // every chat with a conversation is "long" from here on
            await FreshChat();
            Fake.Script.Clear();
            Fake.ScriptFor["[NA3a]"] = new Queue<FakeStep>([FakeStep.Reply("[NA3a] first answer")]);
            await Send("[NA3a] a first question to give this chat a conversation");
            await WaitIdle();
            var chat3 = Vm.CurrentSession!;
            int calls3 = Fake.Calls.Count;
            await Send("แก้อันเมื่อกี้ให้ปุ่มเป็นสีเขียว และเพิ่มข้อความกำกับใต้ปุ่มให้ชัดขึ้นด้วย [NA3f]");
            await WaitIdle();
            Check("NA3 a follow-up about the work in the chat is sent as usual, no card",
                Card() == null && Fake.Calls.Any(c => c.Input.Contains("[NA3f]")), $"card={(Card() != null)}");
            calls3 = Fake.Calls.Count;
            Fake.ScriptFor["[NA3]"] = new Queue<FakeStep>([FakeStep.Reply("[NA3] the stock report is in")]);
            await Send(Task3);
            await Render();
            var card3 = Card();
            Check("NA3 a task of its own in a long chat: a card asks where to run it, nothing is sent yet, the box is empty",
                card3 != null && card3.QuestionOptions.SequenceEqual(new[] { L("chat.newChatAdvice.optNew"), L("chat.newChatAdvice.optHere") })
                && card3.Content.Contains("[NA3]") && Fake.Calls.Count == calls3 && string.IsNullOrEmpty(Vm.UserInput)
                && !Vm.Messages.Any(m => m.Role == MessageRole.User && m.Content == Task3), $"card={(card3 != null)} calls {calls3}→{Fake.Calls.Count}");
            card3?.AnswerQuestion(L("chat.newChatAdvice.optNew"));
            bool ran3 = await WaitUntil(() => Fake.Calls.Any(c => c.Input.Contains("[NA3]")), 8000);
            await Render();
            var moved3 = ChatTitled("[NA3] ");
            Check("NA3 'in a new chat': the task runs in a new chat, this chat only gets a notice and stays on screen",
                ran3 && moved3 != null && ReferenceEquals(Vm.CurrentSession, chat3) && !Vm.IsGenerating
                && !Vm.Messages.Any(m => m.Role == MessageRole.User && m.Content == Task3) && Vm.Messages.Any(m => m.IsNotice && m.Content.Contains(moved3.Title)),
                $"ran={ran3} moved={moved3?.Title} generating={Vm.IsGenerating}");
            await WaitUntil(() => moved3 != null && !RunIn(moved3), 8000);

            // ─── NA4: "in this chat" sends it here, and this chat is not asked again ───
            Fake.ScriptFor["[NA4]"] = new Queue<FakeStep>([FakeStep.Reply("[NA4] the customer summary is in")]);
            Fake.ScriptFor["[NA4b]"] = new Queue<FakeStep>([FakeStep.Reply("[NA4b] the returns summary is in")]);
            await Send(Task4);
            await Render();
            var card4 = Card();
            card4?.AnswerQuestion(L("chat.newChatAdvice.optHere"));
            await WaitUntil(() => Fake.Calls.Any(c => c.Input.Contains("[NA4]")), 8000);
            await WaitIdle();
            Check("NA4 'in this chat': it goes out here as an ordinary message",
                card4 != null && Vm.Messages.Any(m => m.Role == MessageRole.User && m.Content == Task4) && Vm.Messages.Any(m => m.Content.Contains("the customer summary is in"))
                && ChatTitled("[NA4] ") == null, $"card={(card4 != null)}");
            await Send(Task4b);
            await WaitIdle();
            Check("NA4 the same chat is not asked again after 'in this chat'", Card() == null && Vm.Messages.Any(m => m.Content.Contains("the returns summary is in")));

            // ─── NA5: the setting off = no card ───
            Settings.UpdateSettings(s => s.SuggestNewChat = false);
            await FreshChat();
            Fake.ScriptFor["[NA5a]"] = new Queue<FakeStep>([FakeStep.Reply("[NA5a] first answer")]);
            Fake.ScriptFor["[NA5]"] = new Queue<FakeStep>([FakeStep.Reply("[NA5] done here")]);
            await Send("[NA5a] a first question to give this chat a conversation");
            await WaitIdle();
            await Send("[NA5] เพิ่มหน้ารายงานค่าใช้จ่ายรายเดือนพร้อมกราฟ และปุ่ม Export เป็น CSV ในหน้า Dashboard");
            await WaitIdle();
            Check("NA5 with the setting off a new task goes out in the chat on screen, no card", Card() == null && Vm.Messages.Any(m => m.Content.Contains("[NA5] done here")));
            Settings.UpdateSettings(s => s.SuggestNewChat = true);

            // ─── NA6: another model picked in a long chat → a card; here, then in a new chat ───
            all[AiProviderType.Ollama] = other;
            vmType.GetField("_ollamaModels", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Vm, new List<string> { "na-model", "na-model-2" });
            await FreshChat();
            RefreshBox();
            Fake.ScriptFor["[NA6a]"] = new Queue<FakeStep>([FakeStep.Reply("[NA6a] first answer")]);
            await Send("[NA6a] a first question to give this chat a conversation");
            await WaitIdle();
            var chat6 = Vm.CurrentSession!;
            Vm.SelectedChatModel = Vm.ChatModelChoices.First(c => c.Provider == AiProviderType.Ollama && c.Model == "na-model");
            await Render();
            var card6 = Card();
            Check("NA6 another model in a long chat: a card asks first, and nothing is switched while it waits",
                card6 != null && card6.Content.Contains("na-model") && Providers.PickedProviderType == AiProviderType.Local && ChatModelStore.Get(chat6.Id) == null,
                $"card={(card6 != null)} picked={Providers.PickedProviderType}");
            card6?.AnswerQuestion(L("chat.newChatAdvice.modelOptHere"));
            await WaitUntil(() => Providers.PickedProviderType == AiProviderType.Ollama, 5000);
            await Render();
            Check("NA6 'switch in this chat': this chat takes the model", ReferenceEquals(Vm.CurrentSession, chat6) && Providers.PickedProviderType == AiProviderType.Ollama
                && ChatModelStore.Get(chat6.Id) == new ChatModelPick("Ollama", "na-model"), $"{Providers.PickedProviderType} {ChatModelStore.Get(chat6.Id)}");
            Vm.SelectedChatModel = Vm.ChatModelChoices.First(c => c.Provider == AiProviderType.Ollama && c.Model == "na-model-2");
            await Render();
            var card6b = Card();
            card6b?.AnswerQuestion(LocF("chat.newChatAdvice.modelOptNew", "na-model-2"));
            await WaitUntil(() => !ReferenceEquals(Vm.CurrentSession, chat6) && Vm.SelectedChatModel?.Model == "na-model-2", 5000);
            await Render();
            Check("NA6 'a new chat with the model': a new chat is on screen with that model, the long chat keeps its own",
                card6b != null && !ReferenceEquals(Vm.CurrentSession, chat6) && Vm.Messages.All(m => m.Role != MessageRole.Assistant)
                && Vm.SelectedChatModel?.Model == "na-model-2" && ChatModelStore.Get(chat6.Id) == new ChatModelPick("Ollama", "na-model"),
                $"card={(card6b != null)} model={Vm.SelectedChatModel?.Model} long chat keeps {ChatModelStore.Get(chat6.Id)}");
        }
        finally
        {
            gate.Release();
            NewChatAdvice.BigChatTokens = bigBefore;
            Settings.UpdateSettings(s => s.SuggestNewChat = settingBefore);
            await WaitIdle(8000);
            all[AiProviderType.Ollama] = realOllama;
            vmType.GetField("_ollamaModels", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Vm, null);
            SetField(Providers, "_activeProvider", Fake);
            Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.Local);
            RefreshBox();
            Fake.Script.Clear();
            foreach (string key in Fake.ScriptFor.Keys.Where(k => k.StartsWith("[NA")).ToList()) Fake.ScriptFor.Remove(key);
            await FreshChat();
        }
    }
}
