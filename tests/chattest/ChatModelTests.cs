using System.Collections;
using System.Reflection;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.ViewModels;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>The (provider, model) the run in <paramref name="s"/> is pinned to, or null when it has no run / no pin.</summary>
    static (string Provider, string Model)? PinOf(ChatSession s)
    {
        var run = ((IEnumerable)typeof(ChatViewModel).GetField("_runs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Vm)!).Cast<object>()
            .FirstOrDefault(r => ReferenceEquals(r.GetType().GetProperty("Session")!.GetValue(r), s));
        return run?.GetType().GetProperty("Scope")!.GetValue(run) is AgentRunScope { PinnedProviderId: { } p } scope ? (p, scope.PinnedModel ?? "") : null;
    }

    static string OllamaModelInSettings() =>
        Settings.Settings.ProviderConfigs.TryGetValue("Ollama", out var cfg) ? cfg.EffectiveModelId ?? "" : "";

    /// <summary>CM — each chat keeps its own provider and model (owner 2026-11-13: "บางแชทใช้ fable บางแชทใช้ opus หรือ codex
    /// มันกลายเป็นใช้ร่วมกันทั้งหมด … แชทใครแชทมัน … เลยกลายเป็นสลับโมเดลกลางแชท"). Two chats on the same provider with a
    /// model each, a third on another provider: a pick in one never reaches another's run, a turn started off screen goes
    /// out on its chat's own pick, and the composer shows the pick of the chat on screen.</summary>
    static async Task TestChatModels()
    {
        Log("\n## CM — each chat keeps its own provider and model");
        var all = (IDictionary)typeof(AiProviderManager).GetField("_providers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Providers)!;
        var realOllama = all[AiProviderType.Ollama];
        var other = new FakeProvider();
        all[AiProviderType.Ollama] = other;
        var vmType = typeof(ChatViewModel);
        vmType.GetField("_ollamaModels", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Vm, new List<string> { "model-a", "model-b" });
        void RefreshBox() => vmType.GetMethod("RefreshChatModelChoices", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Vm, null);
        ChatViewModel.ChatModelChoice Choice(string model) => Vm.ChatModelChoices.First(c => c.Provider == AiProviderType.Ollama && c.Model == model);
        async Task Open(ChatSession chat)
        {
            Vm.GoToChatCommand.Execute(chat.Id);
            await WaitUntil(() => Vm.CurrentSession?.Id == chat.Id);
            await Render();
        }
        var gateA = new SemaphoreSlim(0);
        var gateA2 = new SemaphoreSlim(0);
        var gateA3 = new SemaphoreSlim(0);
        try
        {
            // ─── chat A picks model-a and works ───
            await FreshChat();
            RefreshBox();
            Fake.Script.Clear();
            Vm.SelectedChatModel = Choice("model-a");
            await WaitUntil(() => Providers.PickedProviderType == AiProviderType.Ollama && Vm.SelectedChatModel?.Model == "model-a", 5000);
            other.ScriptFor["[CMA]"] = new Queue<FakeStep>([FakeStep.Gated(["[CMA] working"], gateA, [" — done on model-a"])]);
            await Send("[CMA] long task in chat A");
            await WaitUntil(() => Vm.Messages.Any(m => m.Content.Contains("[CMA] working")));
            var chatA = Vm.CurrentSession!;
            Check("CM1 a chat keeps the model picked in it, and its run is pinned to it",
                ChatModelStore.Get(chatA.Id) == new ChatModelPick("Ollama", "model-a") && PinOf(chatA) == ("Ollama", "model-a"),
                $"{ChatModelStore.Get(chatA.Id)} pin={PinOf(chatA)}");

            // ─── chat B picks model-b on the same provider while A works ───
            await FreshChat();
            Vm.SelectedChatModel = Choice("model-b");
            await WaitUntil(() => Vm.SelectedChatModel?.Model == "model-b" && OllamaModelInSettings() == "model-b", 5000);
            await Render();
            int callsA = other.Calls.Count(c => c.Input.Contains("[CMA]"));
            Check("CM2 a model picked in chat B does not reach chat A's run: still pinned to model-a, not stopped, not asked again",
                RunIn(chatA) && PinOf(chatA) == ("Ollama", "model-a") && callsA == 1 && OllamaModelInSettings() == "model-b",
                $"runs={RunIn(chatA)} pin={PinOf(chatA)} calls={callsA} settings={OllamaModelInSettings()}");
            other.ScriptFor["[CMB]"] = new Queue<FakeStep>([FakeStep.Reply("[CMB] answer on model-b")]);
            await Send("[CMB] question in chat B");
            await WaitIdle();
            var chatB = Vm.CurrentSession!;
            Check("CM3 each chat has its own pick", ChatModelStore.Get(chatB.Id) == new ChatModelPick("Ollama", "model-b")
                && ChatModelStore.Get(chatA.Id) == new ChatModelPick("Ollama", "model-a") && Vm.Messages.Any(m => m.Content.Contains("answer on model-b")),
                $"a={ChatModelStore.Get(chatA.Id)} b={ChatModelStore.Get(chatB.Id)}");

            // ─── opening a chat shows its own model; nothing is stopped or re-asked ───
            await Open(chatA);
            await WaitUntil(() => Vm.SelectedChatModel?.Model == "model-a", 5000);
            Check("CM4 back in chat A the composer shows model-a (the app's pick follows the chat on screen)",
                Vm.SelectedChatModel?.Model == "model-a" && OllamaModelInSettings() == "model-a", $"{Vm.SelectedChatModel?.Model} settings={OllamaModelInSettings()}");
            Check("CM4 opening it touched neither chat: A still works on its pin, B keeps model-b",
                RunIn(chatA) && PinOf(chatA) == ("Ollama", "model-a") && other.Calls.Count(c => c.Input.Contains("[CMA]")) == 1
                && ChatModelStore.Get(chatB.Id) == new ChatModelPick("Ollama", "model-b"), $"pin={PinOf(chatA)} b={ChatModelStore.Get(chatB.Id)}");
            await Open(chatB);
            await WaitUntil(() => Vm.SelectedChatModel?.Model == "model-b", 5000);
            Check("CM5 and chat B shows model-b again", Vm.SelectedChatModel?.Model == "model-b" && ChatModelStore.Get(chatA.Id) == new ChatModelPick("Ollama", "model-a"),
                $"{Vm.SelectedChatModel?.Model}");
            gateA.Release();
            await WaitUntil(() => !RunIn(chatA), 8000);

            // ─── a chat on another provider on screen; a turn starts in chat A off screen ───
            await FreshChat();
            SetField(Providers, "_activeProvider", Fake);
            Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.Local);   // (the harness's own provider: a model on this PC)
            RefreshBox();
            Fake.ScriptFor["[CMC]"] = new Queue<FakeStep>([FakeStep.Reply("[CMC] answer from the PC's model")]);
            await Send("[CMC] question in chat C");
            await WaitIdle();
            var chatC = Vm.CurrentSession!;
            Check("CM6 a chat on a model on this PC is not pinned (one set of weights for the whole app)",
                ChatModelStore.Get(chatC.Id) == null && Vm.Messages.Any(m => m.Content.Contains("answer from the PC's model")), $"{ChatModelStore.Get(chatC.Id)}");
            int fakeBefore = Fake.Calls.Count;
            other.ScriptFor["[CMA2]"] = new Queue<FakeStep>([FakeStep.Gated(["[CMA2] working"], gateA2, [" — done"])]);
            var offScreen = Vm.ContinueChatForPipeAsync(chatA.Id, null, "[CMA2] go on in chat A", null, null, null, CancellationToken.None);
            bool reached = await WaitUntil(() => other.Calls.Any(c => c.Input.Contains("[CMA2]")), 8000);
            Check("CM7 a turn started in chat A while another chat is on screen runs on chat A's provider and model — not the one on screen",
                reached && PinOf(chatA) == ("Ollama", "model-a") && Fake.Calls.Count == fakeBefore && Vm.CurrentSession?.Id == chatC.Id
                && Providers.PickedProviderType == AiProviderType.Local,
                $"reached={reached} pin={PinOf(chatA)} fake+={Fake.Calls.Count - fakeBefore} picked={Providers.PickedProviderType}");
            gateA2.Release();
            await offScreen;
            await WaitUntil(() => !RunIn(chatA), 8000);

            // ─── from the PC's model back to chat A: provider and model both follow ───
            await Open(chatA);
            await WaitUntil(() => Providers.PickedProviderType == AiProviderType.Ollama && Vm.SelectedChatModel?.Model == "model-a", 5000);
            Check("CM8 opening chat A from a chat on another provider brings its provider and model to the composer",
                Providers.PickedProviderType == AiProviderType.Ollama && Vm.SelectedChatModel?.Model == "model-a" && ChatModelStore.Get(chatC.Id) == null,
                $"{Providers.PickedProviderType} {Vm.SelectedChatModel?.Model}");

            // ─── the pick changed without the model box (the Settings page): what the composer shows is what runs ───
            Settings.UpdateSettings(s => { s.ProviderConfigs["Ollama"].SelectedModel = "model-b"; s.ProviderConfigs["Ollama"].UseCustomModel = false; });
            RefreshBox();
            other.ScriptFor["[CMA3]"] = new Queue<FakeStep>([FakeStep.Gated(["[CMA3] working"], gateA3, [" — done"])]);
            await Send("[CMA3] after the Settings page changed the model");
            await WaitUntil(() => Vm.Messages.Any(m => m.Content.Contains("[CMA3] working")));
            Check("CM9 the chat on screen runs on what its composer shows, and keeps it from then on",
                Vm.SelectedChatModel?.Model == "model-b" && PinOf(chatA) == ("Ollama", "model-b") && ChatModelStore.Get(chatA.Id) == new ChatModelPick("Ollama", "model-b"),
                $"{Vm.SelectedChatModel?.Model} pin={PinOf(chatA)} kept={ChatModelStore.Get(chatA.Id)}");
            gateA3.Release();
            await WaitIdle();

            // ─── a chat with no pick opened: nothing is switched for it; a deleted chat's pick goes with it ───
            await Open(chatC);
            Check("CM10 a chat that keeps no pick changes nothing when opened", Providers.PickedProviderType == AiProviderType.Ollama
                && ChatModelStore.Get(chatC.Id) == null && ChatModelStore.Get(chatA.Id) == new ChatModelPick("Ollama", "model-b"), $"{Providers.PickedProviderType}");
            ConfirmAnswer = true;
            Vm.DeleteSessionCommand.Execute(chatB.Id);
            await WaitUntil(() => ChatModelStore.Get(chatB.Id) == null, 5000);
            Check("CM11 a deleted chat's pick goes with it", ChatModelStore.Get(chatB.Id) == null && ChatModelStore.Get(chatA.Id) != null, $"{ChatModelStore.Get(chatB.Id)}");
        }
        finally
        {
            gateA.Release(); gateA2.Release(); gateA3.Release();
            await WaitIdle(8000);
            all[AiProviderType.Ollama] = realOllama;
            vmType.GetField("_ollamaModels", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Vm, null);
            SetField(Providers, "_activeProvider", Fake);
            Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.Local);
            RefreshBox();
            Fake.Script.Clear();
            Fake.ScriptFor.Remove("[CMC]");
            await FreshChat();
        }
    }
}
