using System.Reflection;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;
using CluadeX.ViewModels;

namespace ChatTest;

/// <summary>A CLI as the send path sees it: answers like the fake, and is signed out until told otherwise.</summary>
internal sealed class FakeCli : FakeProvider, IDelegatedCliProvider
{
    public bool SignedIn;
    public bool LookThrows;
    public int Looks;
    public bool LastTurnAuthRefused => false;
    public Task<bool> IsLoggedInAsync(CancellationToken ct)
    {
        Looks++;
        if (LookThrows) throw new OperationCanceledException();
        return Task.FromResult(SignedIn);
    }
    public bool OpenLoginTerminal() => false;
    public Task<string> ExplainAuthFailureAsync(string refusal, CancellationToken ct) => Task.FromResult<string>(null);
    public bool LooksLikeRefusal(string text) => false;
}

internal static partial class Program
{
    /// <summary>CW — a model that is not connected opens its wizard (owner 2026-10-31), only for the owner's own click or
    /// Send, with the message left in the box; a call from outside stays a plain "no", and a wizard closed without
    /// finishing never traps the next Send.</summary>
    static async Task TestConnectWizardJump()
    {
        Log("\n## CW — a model that is not connected opens its wizard");
        var asked = new List<(AiProviderType Provider, string Model)>();
        void Listen(AiProviderType p, string m) => asked.Add((p, m));
        Vm.ConnectionRequested += Listen;
        var active = typeof(AiProviderManager).GetField("_activeProvider", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var saved = active.GetValue(Providers);
        var signIns = (Dictionary<AiProviderType, DateTime>)typeof(ChatViewModel).GetField("_signInLooked", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Vm)!;
        void Refresh() => typeof(ChatViewModel).GetMethod("RefreshChatModelChoices", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Vm, null);
        try
        {
            await FreshChat();
            Settings.UpdateSettings(s => s.ProviderConfigs.Remove("Anthropic"));
            Refresh();
            Check("CW1 every provider is in the box, connected or not",
                new[] { AiProviderType.Anthropic, AiProviderType.OpenAI, AiProviderType.Gemini, AiProviderType.AntigravityDev, AiProviderType.BrainX, AiProviderType.Local }
                    .All(p => Vm.ChatModelChoices.Any(c => c.Provider == p)),
                string.Join(",", Vm.ChatModelChoices.Select(c => c.Provider).Distinct()));

            var noKey = Vm.ChatModelChoices.First(c => c.Provider == AiProviderType.Anthropic && c.NeedsKey);
            string why = await Vm.ChooseFromOutsideAsync(noKey.Model, null);
            Check("CW2 a pick from outside is refused and opens nothing on anyone's screen", why != null && asked.Count == 0, why);

            var before = Providers.ActiveProviderType;
            Vm.SelectedChatModel = noKey;
            await WaitUntil(() => asked.Count > 0, 2000);
            await Render();
            Check("CW3 the owner's own pick opens that provider's wizard, with the model they chose, and switches nothing",
                asked.Count == 1 && asked[0] == (AiProviderType.Anthropic, noKey.Model) && Providers.ActiveProviderType == before && !Equals(Vm.SelectedChatModel, noKey));
            asked.Clear();

            Fake.IsReady = false;
            Vm.UserInput = "[CW] build me a game";
            int messages = Vm.Messages.Count;
            Vm.SendMessageCommand.Execute(null);
            await WaitUntil(() => asked.Count > 0, 2000);
            await Render();
            Check("CW4 Send with a provider that cannot answer: its wizard opens, the message stays in the box, nothing is added to the chat",
                asked.Count == 1 && Vm.UserInput == "[CW] build me a game" && Vm.Messages.Count == messages,
                $"asked={asked.Count} box='{Vm.UserInput}' messages {messages}→{Vm.Messages.Count}");
            asked.Clear();

            Vm.ConnectionRequested -= Listen;
            Vm.SendMessageCommand.Execute(null);
            await WaitUntil(() => Vm.Messages.Any(m => m.HasError), 2000);
            Check("CW5 nobody listening (a harness, no window): the chat says so itself instead of swallowing the message",
                Vm.Messages.Any(m => m.HasError) && Vm.UserInput == "[CW] build me a game");
            Vm.ConnectionRequested += Listen;
            Vm.UserInput = "";
            Fake.IsReady = true;

            await FreshChat();
            var cli = new FakeCli();
            active.SetValue(Providers, cli);
            signIns.Clear();
            Vm.UserInput = "[CW] hello";
            messages = Vm.Messages.Count;
            Vm.SendMessageCommand.Execute(null);
            await WaitUntil(() => asked.Count > 0, 2000);
            Check("CW6 a signed-out CLI is found before the message goes, not after it fails", asked.Count == 1 && cli.Calls.Count == 0 && Vm.UserInput == "[CW] hello");
            asked.Clear();

            cli.Script.Enqueue(FakeStep.Reply("[CW] answered anyway"));
            Vm.SendMessageCommand.Execute(null);
            await WaitUntil(() => cli.Calls.Count > 0, 3000);
            await WaitIdle();
            Check("CW7 the wizard closed without signing in: the next Send goes through (the turn heals itself as before)",
                asked.Count == 0 && cli.Calls.Count == 1 && Vm.UserInput.Length == 0, $"asked={asked.Count} calls={cli.Calls.Count}");

            signIns.Clear();
            cli.LookThrows = true;
            cli.Calls.Clear();
            await Send("[CW] slow look");
            await WaitIdle();
            Check("CW8 a look that fails holds nothing up", asked.Count == 0 && cli.Calls.Count == 1);

            signIns.Clear();
            cli.LookThrows = false;
            cli.SignedIn = true;
            await Send("[CW] one");
            await WaitIdle();
            int looks = cli.Looks;
            await Send("[CW] two");
            await WaitIdle();
            Check("CW9 signed in: one look, not one per message", asked.Count == 0 && cli.Looks == looks);
        }
        finally
        {
            active.SetValue(Providers, saved);
            Fake.IsReady = true;
            Vm.ConnectionRequested -= Listen;
            Vm.UserInput = "";
        }
    }
}
