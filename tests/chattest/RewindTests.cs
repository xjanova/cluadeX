using System.Collections;
using System.IO;
using System.Reflection;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.ViewModels;

namespace ChatTest;

internal static partial class Program
{
    static string[] RowsOf(params MessageRole[] roles) =>
        Vm.Messages.Where(m => roles.Contains(m.Role)).Select(m => m.Content).ToArray();

    static List<ChatMessage> Conversation() => Vm.Messages.Where(m => m.Role is MessageRole.User or MessageRole.Assistant).ToList();

    /// <summary>RW — any point of a chat can be gone back to: Rewind cuts this chat there, Fork opens a new chat from there.
    /// What was cut never reaches the model again, and does not come back with the stored chat.</summary>
    static async Task TestRewindAndFork()
    {
        Log("\n## RW — Rewind to here / Fork from here on any message");
        await FreshChat();
        Fake.Script.Clear();
        foreach (var r in new[] { "[RW] answer one", "[RW] answer two", "[RW] answer three" }) Fake.Script.Enqueue(FakeStep.Reply(r));
        foreach (var q in new[] { "[RW] question one", "[RW] question two", "[RW] question three" })
        {
            await Send(q);
            await WaitIdle();
        }
        var chat = Vm.CurrentSession!;
        var rows = Conversation();
        Check("RW setup: three exchanges", rows.Count == 6, string.Join(" | ", rows.Select(m => m.Content)));
        var q2 = rows[2];

        var confirmsBefore = ConfirmTitles.Count;
        ConfirmAnswer = false;
        Vm.RewindToMessageCommand.Execute(q2);
        await WaitUntil(() => ConfirmTitles.Count > confirmsBefore);
        await Render();
        Check("RW1 rewinding asks first, and a no leaves the chat whole", Conversation().Count == 6);

        ConfirmAnswer = true;
        Vm.UserInput = "";
        Vm.RewindToMessageCommand.Execute(q2);
        await WaitUntil(() => Conversation().Count == 2);
        await Render();
        Check("RW2 rewinding to a question cuts it and all that followed", Conversation().Count == 2
            && Conversation()[1].Content.Contains("answer one"), string.Join(" | ", Conversation().Select(m => m.Content)));
        Check("RW2 and the question comes back to the input to be changed", Vm.UserInput == q2.Content, Vm.UserInput);

        int calls = Fake.Calls.Count;
        Fake.Script.Enqueue(FakeStep.Reply("[RW] answer two again"));
        Vm.UserInput = "[RW] question two, asked differently";
        Vm.SendMessageCommand.Execute(null);
        await WaitUntil(() => Fake.Calls.Count > calls);
        await WaitIdle();
        var sent = Fake.Calls[^1];
        Check("RW3 the model's next turn does not see what was cut",
            !sent.History.Any(h => h.Content.Contains("answer two") || h.Content.Contains("question three") || h.Content.Contains("question two"))
            && sent.History.Any(h => h.Content.Contains("answer one")) && sent.Input.Contains("asked differently"),
            string.Join(" | ", sent.History.Select(h => h.Content)));

        await FreshChat();
        Vm.GoToChatCommand.Execute(chat.Id);
        await WaitUntil(() => Vm.CurrentSession?.Id == chat.Id);
        await Render();
        Check("RW4 what was cut does not come back when the chat is opened again", Conversation().Count == 4
            && !Vm.Messages.Any(m => m.Content.Contains("question three")), string.Join(" | ", Conversation().Select(m => m.Content)));

        var a1 = Conversation()[1];
        Vm.UserInput = "a draft";
        Vm.RewindToMessageCommand.Execute(a1);
        await WaitUntil(() => Conversation().Count == 2);
        await Render();
        Check("RW5 rewinding to a reply keeps that reply and leaves the input alone",
            Conversation().Count == 2 && ReferenceEquals(Conversation()[^1], a1) && Vm.UserInput == "a draft", Vm.UserInput);
        Vm.RewindToMessageCommand.Execute(a1);
        await WaitUntil(() => Vm.StatusText == L("chat.rewind.nothing"));
        Check("RW6 nothing after the point: says so, asks nothing", Vm.StatusText == L("chat.rewind.nothing"), Vm.StatusText);

        // Fork: a new chat up to the point; the original keeps everything.
        Fake.Script.Enqueue(FakeStep.Reply("[RW] answer four"));
        Vm.UserInput = "[RW] question four";
        Vm.SendMessageCommand.Execute(null);
        await WaitIdle();
        var parentRows = Conversation().Select(m => m.Content).ToList();
        var parentIds = Conversation().Select(m => m.Id).ToHashSet();
        Vm.UserInput = "";
        Vm.ForkFromMessageCommand.Execute(Conversation()[1]);
        await WaitUntil(() => Vm.CurrentSession?.Id != chat.Id);
        await Render();
        var fork = Vm.CurrentSession!;
        Check("RW7 a fork from a reply opens a new chat holding what came up to it",
            fork.Id != chat.Id && Conversation().Count == 2 && Conversation()[1].Content.Contains("answer one")
            && Conversation().All(m => !parentIds.Contains(m.Id)),
            string.Join(" | ", Conversation().Select(m => m.Content)));
        Check("RW7 it has a chip back to the chat it came from", Vm.StickyBranches.Any(b => b.IsParent && b.SessionId == chat.Id),
            string.Join(" | ", Vm.StickyBranches.Select(b => b.Label)));
        calls = Fake.Calls.Count;
        Fake.Script.Enqueue(FakeStep.Reply("[RW] fork answer"));
        Vm.UserInput = "[RW] fork question";
        Vm.SendMessageCommand.Execute(null);
        await WaitUntil(() => Fake.Calls.Count > calls);
        await WaitIdle();
        Check("RW8 the fork's model sees only what the fork holds",
            !Fake.Calls[^1].History.Any(h => h.Content.Contains("question four")) && Fake.Calls[^1].History.Any(h => h.Content.Contains("answer one")),
            string.Join(" | ", Fake.Calls[^1].History.Select(h => h.Content)));

        Vm.GoToChatCommand.Execute(chat.Id);
        await WaitUntil(() => Vm.CurrentSession?.Id == chat.Id);
        await Render();
        Check("RW9 the chat it came from is as it was, with a chip to the fork",
            Conversation().Select(m => m.Content).SequenceEqual(parentRows) && Vm.StickyBranches.Any(b => !b.IsParent && b.SessionId == fork.Id),
            string.Join(" | ", Conversation().Select(m => m.Content)));

        var firstQuestion = Conversation()[0];
        Vm.UserInput = "";
        Vm.ForkFromMessageCommand.Execute(firstQuestion);
        await WaitUntil(() => Vm.CurrentSession?.Id != chat.Id);
        await Render();
        Check("RW10 a fork from the first question is an empty chat with that question ready to change",
            Vm.Messages.Count == 0 && Vm.UserInput == firstQuestion.Content, $"{Vm.Messages.Count} · {Vm.UserInput}");
        Vm.UserInput = "";

        // Going back with the files: only what the chat changed after the point.
        await FreshChat();
        string proj = Path.Combine(Path.GetTempPath(), "cluadex-rewind-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(proj);
        string file = Path.Combine(proj, "main.txt");
        File.WriteAllText(file, "before\n");
        var session = Vm.CurrentSession!;
        session.ProjectPath = proj;
        var cp = new CheckpointService(null, Path.Combine(proj, ".store"));
        Fake.Script.Enqueue(FakeStep.Reply("[RWF] one"));
        await Send("[RWF] first");
        await WaitIdle();
        Fake.Script.Enqueue(FakeStep.Reply("[RWF] two"));
        await Send("[RWF] change the file");
        await WaitIdle();
        var ask = Conversation()[2];
        await Task.Delay(30);
        var scope = new AgentRunScope(session.Id, proj);
        AgentRunScope.Enter(scope);
        scope.Step = 1; cp.CaptureBeforeChange(file); File.WriteAllText(file, "after\n");
        AgentRunScope.Enter(null);

        string asked = null;
        Vm.AskRewindFiles = (text, title) => { asked = text; return null; };
        Vm.RewindToMessageCommand.Execute(ask);
        await WaitUntil(() => asked != null);
        await Render();
        Check("RW11 with files changed after the point it asks about them; cancel changes nothing",
            asked != null && asked.Contains('1') && Conversation().Count == 4 && File.ReadAllText(file) == "after\n", asked);

        asked = null;
        Vm.AskRewindFiles = (text, title) => { asked = text; return false; };
        Vm.RewindToMessageCommand.Execute(Conversation()[3]);
        await Task.Delay(150);
        var a2 = Conversation()[3];
        Fake.Script.Enqueue(FakeStep.Reply("[RWF] three"));
        await Send("[RWF] one more");
        await WaitIdle();
        Vm.RewindToMessageCommand.Execute(a2);
        await WaitUntil(() => Conversation().Count == 4);
        Check("RW12 chat only leaves the files as they are", Conversation().Count == 4 && File.ReadAllText(file) == "after\n");

        Vm.AskRewindFiles = (text, title) => true;
        Vm.UserInput = "";
        Vm.RewindToMessageCommand.Execute(ask);
        await WaitUntil(() => Conversation().Count == 2);
        await Render();
        Check("RW13 chat and files puts the file back too, and the chat says so",
            Conversation().Count == 2 && File.ReadAllText(file) == "before\n" && Vm.Messages.Any(m => m.Role == MessageRole.System && m.Content.StartsWith("⏪")),
            File.ReadAllText(file));
        Vm.AskRewindFiles = (text, title) => false;

        // A rewind during a run: the stop is asked for first, then the cut.
        await FreshChat();
        Fake.Script.Enqueue(FakeStep.Reply("[RWR] one"));
        await Send("[RWR] first");
        await WaitIdle();
        var keep = Conversation()[1];
        Fake.Script.Enqueue(FakeStep.Hang());
        await Send("[RWR] second");
        await WaitUntil(() => Vm.IsGenerating);
        ConfirmAnswer = true;
        Vm.RewindToMessageCommand.Execute(keep);
        await WaitUntil(() => !Vm.IsGenerating && Conversation().Count == 2);
        await Render();
        Check("RW14 rewinding during a run stops it, then cuts", !Vm.IsGenerating && Conversation().Count == 2,
            string.Join(" | ", Conversation().Select(m => m.Content)));
        Fake.Script.Clear();
        ConfirmAnswer = true;
    }

    /// <summary>MS — a model picked mid-way answers at once: the chat's run stops and carries on with it; a run in another
    /// chat keeps the provider it started on and is left alone.</summary>
    static async Task TestModelSwitchNow()
    {
        Log("\n## MS — the model box takes effect at once, mid-run too");
        var all = (IDictionary)typeof(AiProviderManager).GetField("_providers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Providers)!;
        var realOllama = all[AiProviderType.Ollama];
        var other = new FakeProvider();
        all[AiProviderType.Ollama] = other;
        var vmType = typeof(ChatViewModel);
        vmType.GetField("_ollamaModels", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Vm, new List<string> { "new-model", "another-model" });
        void RefreshBox() => vmType.GetMethod("RefreshChatModelChoices", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Vm, null);
        try
        {
            await FreshChat();
            RefreshBox();
            Fake.Script.Clear();
            Fake.Script.Enqueue(FakeStep.Reply("[MS] first answer"));
            await Send("[MS] first question");
            await WaitIdle();

            var gate = new SemaphoreSlim(0);
            Fake.Script.Enqueue(FakeStep.Gated(["[MS] half of a reply"], gate, ["[MS] the rest"]));
            await Send("[MS] long task");
            await WaitUntil(() => Vm.Messages.Any(m => m.Content.Contains("half of a reply")));

            string outside = await Vm.ChooseFromOutsideAsync("no-such-model", null);
            Check("MS1 a pick from outside still changes nothing mid-run", Vm.IsGenerating && outside != null, outside);

            other.Script.Enqueue(FakeStep.Reply("[MS] carried on by the new model"));
            Vm.SelectedChatModel = Vm.ChatModelChoices.First(c => c.Provider == AiProviderType.Ollama && c.Model == "new-model");
            bool answered = await WaitUntil(() => other.Calls.Count > 0, 8000);
            await WaitIdle();
            await Render();
            Check("MS2 picking another provider's model mid-run stops the run and the new model answers at once",
                answered && Vm.Messages.Any(m => m.Content.Contains("carried on by the new model")), Vm.StatusText);
            Check("MS2 the old model did not finish its reply", !Vm.Messages.Any(m => m.Content.Contains("[MS] the rest")));
            Check("MS3 the new model is told to go on, with what was said so far",
                answered && other.Calls[0].Input == L("chat.pause.resumeText") && other.Calls[0].History.Any(h => h.Content.Contains("long task")),
                answered ? other.Calls[0].Input : "no call");
            Check("MS3 the chat says what happened", Vm.Messages.Any(m => m.Role == MessageRole.System && m.Content.Contains("new-model")));
            Check("MS4 the box shows the model that answers", Vm.SelectedChatModel?.Model == "new-model" && Providers.ActiveProviderType == AiProviderType.Ollama,
                $"{Vm.SelectedChatModel?.Model} {Providers.ActiveProviderType}");

            // Between runs: the very next message goes to the picked model.
            int otherCalls = other.Calls.Count;
            Vm.SelectedChatModel = Vm.ChatModelChoices.First(c => c.Model == "another-model");
            await WaitUntil(() => Vm.SelectedChatModel?.Model == "another-model", 3000);
            Check("MS5 a pick between runs starts nothing by itself", !Vm.IsGenerating && other.Calls.Count == otherCalls);

            // A run in another chat keeps its provider: leaving it running does not block the pick, nor does the pick touch it.
            await FreshChat();
            Fake.Script.Clear();
            SetField(Providers, "_activeProvider", Fake);
            Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.Local);
            RefreshBox();
            var gate2 = new SemaphoreSlim(0);
            Fake.Script.Enqueue(FakeStep.Gated(["[MS2] working"], gate2, [" and done on the first provider"]));
            await Send("[MS2] long task elsewhere");
            await WaitUntil(() => Vm.Messages.Any(m => m.Content.Contains("[MS2] working")));
            var busyChat = Vm.CurrentSession!;
            await FreshChat();
            other.Script.Enqueue(FakeStep.Reply("[MS2] answer from the new provider"));
            Vm.SelectedChatModel = Vm.ChatModelChoices.First(c => c.Model == "new-model");
            await WaitUntil(() => Providers.ActiveProviderType == AiProviderType.Ollama, 5000);
            await Render();
            Check("MS6 a run in another chat does not block the pick", Providers.ActiveProviderType == AiProviderType.Ollama && RunIn(busyChat), Vm.StatusText);
            await Send("[MS2] question for the new provider");
            await WaitIdle();
            Check("MS6 this chat answers with the new provider", Vm.Messages.Any(m => m.Content.Contains("answer from the new provider")));
            gate2.Release();
            await WaitUntil(() => !RunIn(busyChat), 8000);
            await Render();
            Vm.GoToChatCommand.Execute(busyChat.Id);
            await WaitUntil(() => Vm.CurrentSession?.Id == busyChat.Id);
            await Render();
            Check("MS7 the run left behind finished on the provider it started on",
                Vm.Messages.Any(m => m.Content.Contains("and done on the first provider")) && !other.Calls.Any(c => c.Input.Contains("[MS2] long task")),
                string.Join(" | ", Conversation().Select(m => m.Content)));
        }
        finally
        {
            all[AiProviderType.Ollama] = realOllama;
            vmType.GetField("_ollamaModels", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Vm, null);
            SetField(Providers, "_activeProvider", Fake);
            Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.Local);
            RefreshBox();
            Fake.Script.Clear();
        }
    }

    static bool RunIn(ChatSession s) =>
        ((IEnumerable)typeof(ChatViewModel).GetField("_runs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Vm)!).Cast<object>()
        .Any(r => ReferenceEquals(r.GetType().GetProperty("Session")!.GetValue(r), s));
}
