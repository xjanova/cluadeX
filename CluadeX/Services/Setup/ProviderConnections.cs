using CluadeX.Models;

namespace CluadeX.Services.Setup;

/// <summary>One thing a provider needs before it can answer.</summary>
public enum ConnectStepKind { License, Install, SignIn, ApiKey, Start, GetModel, Brain, Test }

/// <summary>Where a provider stands, in one word (the wizard's cards and the chat's error text).</summary>
public enum ProviderConnectionState { Ready, NeedsLicense, NotInstalled, SignedOut, NoKey, NotRunning, NoModel, NoBrain, NotTested }

/// <summary>One step of a provider's wizard. <paramref name="RowId"/> is the setup row that carries it: the same buttons,
/// the same confirms and the same notes as the checklist. <paramref name="Action"/> is null once the step is done.</summary>
public sealed record ConnectStep(ConnectStepKind Kind, string RowId, bool Done, SetupAction? Action = null, SetupAction? Other = null);

/// <summary>What one provider needs, in order, on this PC.</summary>
public sealed record ProviderConnection(AiProviderType Provider, IReadOnlyList<ConnectStep> Steps)
{
    /// <summary>The first step still to do; null when nothing is left.</summary>
    public ConnectStep? Current => Steps.FirstOrDefault(s => !s.Done);
    public int CurrentIndex => Current is { } c ? Steps.ToList().IndexOf(c) : Steps.Count;
    /// <summary>Everything is in place (a test may still be waiting).</summary>
    public bool IsSetUp => Steps.All(s => s.Done || s.Kind == ConnectStepKind.Test);

    public ProviderConnectionState State => Current?.Kind switch
    {
        null => ProviderConnectionState.Ready,
        ConnectStepKind.License => ProviderConnectionState.NeedsLicense,
        ConnectStepKind.Install => ProviderConnectionState.NotInstalled,
        ConnectStepKind.SignIn => ProviderConnectionState.SignedOut,
        ConnectStepKind.ApiKey => ProviderConnectionState.NoKey,
        ConnectStepKind.Start => ProviderConnectionState.NotRunning,
        ConnectStepKind.GetModel => ProviderConnectionState.NoModel,
        ConnectStepKind.Brain => ProviderConnectionState.NoBrain,
        _ => ProviderConnectionState.NotTested,
    };
}

/// <summary>
/// The connection wizard's steps for each provider (owner 2026-10-31: "เด้งไปหน้าติดตั้ง wizard เป็นขั้นตอน ทุกโมเดลเมื่อยัง
/// ไม่ได้เชื่อมต่อ"). Built from the setup rows, so a wizard and the checklist can never disagree and every command a step
/// runs is one of <see cref="SystemSetupService.BuildItems"/>'s constants. Pure: the harness feeds it made-up machines.
/// </summary>
public static class ProviderConnections
{
    /// <summary>Every provider, in the order the page offers them: sign-ins first, then keys, then this PC.</summary>
    public static readonly AiProviderType[] All =
    [
        AiProviderType.ClaudeDev, AiProviderType.OpenAIDev, AiProviderType.AntigravityDev, AiProviderType.GeminiDev,
        AiProviderType.Anthropic, AiProviderType.OpenAI, AiProviderType.Gemini,
        AiProviderType.Ollama, AiProviderType.LlamaServer, AiProviderType.Local, AiProviderType.BrainX,
    ];

    /// <summary>The setup row that says whether this provider is there.</summary>
    public static string RowId(AiProviderType provider) => provider switch
    {
        AiProviderType.ClaudeDev => "claudecode",
        AiProviderType.OpenAIDev => "codexcli",
        AiProviderType.AntigravityDev => "agycli",
        AiProviderType.GeminiDev => "geminicli",
        AiProviderType.Anthropic => "anthropic",
        AiProviderType.OpenAI => "openai",
        AiProviderType.Gemini => "gemini",
        AiProviderType.Ollama => "ollama",
        AiProviderType.LlamaServer => "llamaserver",
        AiProviderType.Local => "localgguf",
        _ => "brainx",
    };

    /// <summary>Cloud models are the paid tier (the model box's own rule); this PC and the BrainX room are not.</summary>
    public static bool NeedsLicense(AiProviderType provider) =>
        provider is not (AiProviderType.Ollama or AiProviderType.LlamaServer or AiProviderType.Local or AiProviderType.BrainX);

    /// <summary>A GGUF is loaded on the Models page, not tested here: a test must never load weights into VRAM.</summary>
    public static bool HasTest(AiProviderType provider) => provider is not (AiProviderType.LlamaServer or AiProviderType.Local);

    public static bool IsApiKey(AiProviderType provider) =>
        provider is AiProviderType.Anthropic or AiProviderType.OpenAI or AiProviderType.Gemini;

    /// <summary>The steps for <paramref name="provider"/> on the machine <paramref name="facts"/> describes.</summary>
    /// <param name="tested">The provider's own test passed since the wizard opened.</param>
    public static ProviderConnection For(AiProviderType provider, SetupFacts facts, bool tested = false)
    {
        var items = SystemSetupService.BuildItems(facts);
        var row = items.Single(i => i.Id == RowId(provider));
        var steps = new List<ConnectStep>();
        ConnectStep Step(ConnectStepKind kind, bool done, SetupAction? action = null, SetupAction? other = null) =>
            new(kind, row.Id, done, done ? null : action, done ? null : other);

        if (NeedsLicense(provider))
        {
            var license = items.Single(i => i.Id == "license");
            steps.Add(new ConnectStep(ConnectStepKind.License, license.Id, license.IsReady, license.Primary));
        }

        switch (provider)
        {
            case AiProviderType.ClaudeDev or AiProviderType.OpenAIDev or AiProviderType.AntigravityDev or AiProviderType.GeminiDev:
                steps.Add(Step(ConnectStepKind.Install, row.State != SetupState.Missing, row.Primary));
                steps.Add(Step(ConnectStepKind.SignIn, row.IsReady, row.Primary));
                break;
            case AiProviderType.Anthropic or AiProviderType.OpenAI or AiProviderType.Gemini:
                steps.Add(Step(ConnectStepKind.ApiKey, row.IsReady,
                    new SetupAction(SetupActionKind.EnterKey, "setup.act.enterKey", Target: provider.ToString()), row.Primary));
                break;
            case AiProviderType.Ollama:
                steps.Add(Step(ConnectStepKind.Install, row.State != SetupState.Missing, row.Primary));
                steps.Add(Step(ConnectStepKind.Start, row.State is SetupState.NoModel or SetupState.Ready, row.Primary));
                steps.Add(Step(ConnectStepKind.GetModel, row.IsReady, row.Primary, row.Secondary));
                break;
            case AiProviderType.LlamaServer:
                steps.Add(Step(ConnectStepKind.Install, row.IsReady, row.Primary));
                steps.Add(new ConnectStep(ConnectStepKind.GetModel, "localgguf", facts.LocalLoaded,
                    facts.LocalLoaded ? null : new SetupAction(SetupActionKind.OpenPage, "setup.act.modelsPage", Target: "Models")));
                break;
            case AiProviderType.Local:
                steps.Add(Step(ConnectStepKind.GetModel, row.IsReady, row.Primary));
                break;
            default:   // the BrainX room: the app, then the connection to its brain
                steps.Add(Step(ConnectStepKind.Install, row.State != SetupState.Missing, row.Primary, row.Secondary));
                steps.Add(Step(ConnectStepKind.Brain, row.IsReady, row.Primary));
                break;
        }

        if (HasTest(provider)) steps.Add(new ConnectStep(ConnectStepKind.Test, row.Id, tested));
        return new ProviderConnection(provider, steps);
    }
}
