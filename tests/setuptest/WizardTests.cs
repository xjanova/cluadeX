using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Setup;
using CluadeX.ViewModels;

// The connection wizard (owner 2026-10-31): the steps for every provider on made-up machines, the wizard driven through
// them, and its screens rendered. Installs nothing, signs in nowhere, loads no model: the tests run on facts, and the
// provider test in W20-W23 runs with no providers behind it, so it fails without touching the network.
static partial class Program
{
    static ProviderConnection Conn(AiProviderType p, SetupFacts f, bool tested = false) => ProviderConnections.For(p, f, tested);
    static string Kinds(ProviderConnection c) => string.Join(",", c.Steps.Select(s => s.Kind));

    static readonly SetupFacts Licensed = new() { Licensed = true };

    /// <summary>Every string the wizard can show.</summary>
    static IEnumerable<string> WizardTextKeys()
    {
        foreach (var k in new[] { "hub", "hubSub", "title", "stepOf", "close", "closeChat", "useThis", "retest", "keyAgain", "useAgy", "testing", "test.failed",
                     "test.timeout", "test.noDetail", "body.TestFailed", "key.prompt", "key.bad", "done.title", "done.body", "done.chat", "done.loaded",
                     "hint.GeminiDev", "hint.AntigravityDev", "hint.Gemini" })
            yield return "wizard." + k;
        foreach (var s in Enum.GetValues<ConnectStepKind>()) { yield return "wizard.step." + s; yield return "wizard.body." + s; }
        foreach (var s in Enum.GetValues<ProviderConnectionState>()) yield return "wizard.state." + s;
        foreach (var k in new[] { "chat.model.needsSetupLabel", "chat.model.opensWizard", "chat.err.notReady.cli", "chat.err.notReady.ollama", "chat.err.notReady.brainx" })
            yield return k;
    }

    static void WizardSteps()
    {
        Console.WriteLine("── the wizard's steps ──");
        Check("W1", "every provider CluadeX has is offered",
            Enum.GetValues<AiProviderType>().All(ProviderConnections.All.Contains) && ProviderConnections.All.Distinct().Count() == Enum.GetValues<AiProviderType>().Length);
        Check("W2", "every provider has a setup row on a bare PC",
            ProviderConnections.All.All(p => SystemSetupService.BuildItems(new SetupFacts()).Any(i => i.Id == ProviderConnections.RowId(p))));
        Check("W3", "a cloud provider starts with the license; this PC and the BrainX room do not need one",
            Conn(AiProviderType.ClaudeDev, new SetupFacts()).Current?.Kind == ConnectStepKind.License
            && Conn(AiProviderType.Anthropic, new SetupFacts()).Current?.Kind == ConnectStepKind.License
            && Conn(AiProviderType.Ollama, new SetupFacts()).Steps.All(s => s.Kind != ConnectStepKind.License)
            && Conn(AiProviderType.BrainX, new SetupFacts()).Steps.All(s => s.Kind != ConnectStepKind.License)
            && Conn(AiProviderType.Local, new SetupFacts()).Steps.All(s => s.Kind != ConnectStepKind.License));
        Check("W4", "the license step opens the license window",
            Conn(AiProviderType.ClaudeDev, new SetupFacts()).Current?.Action is { Kind: SetupActionKind.OpenPage, Target: "License" });

        var agy = Conn(AiProviderType.AntigravityDev, Licensed);
        Check("W5", "Antigravity: install, sign in, test, in that order", Kinds(agy) == "License,Install,SignIn,Test", Kinds(agy));
        Check("W6", "its install is Google's script, in quotes so cmd hands the | to PowerShell, and it says what it runs",
            agy.Current?.Action is { Kind: SetupActionKind.Install, Commands: { Count: 1 } a } && a[0] == SystemSetupService.AgyInstall
            && a[0].StartsWith("powershell ") && a[0].Contains("\"irm https://antigravity.google/cli/install.ps1 | iex\""), agy.Current?.Action?.Commands?[0] ?? "");
        var agyOut = Conn(AiProviderType.AntigravityDev, Licensed with { AgyCli = @"C:\x\agy.exe" });
        Check("W7", "installed but signed out: the step is the sign-in, through agy", agyOut.Current is { Kind: ConnectStepKind.SignIn, Action: { Kind: SetupActionKind.SignIn, Target: "agy" } });
        var agyIn = Licensed with { AgyCli = @"C:\x\agy.exe", AgySignedIn = true };
        Check("W8", "signed in: only the test is left, and nothing is called connected before it passes",
            Conn(AiProviderType.AntigravityDev, agyIn) is { Current.Kind: ConnectStepKind.Test, IsSetUp: true, State: ProviderConnectionState.NotTested });
        Check("W9", "the test passed: nothing is left", Conn(AiProviderType.AntigravityDev, agyIn, tested: true) is { Current: null, State: ProviderConnectionState.Ready });
        Check("W10", "agy signed in counts as a model that can answer", SystemSetupService.AnyChatModelReady(agyIn));

        var key = Conn(AiProviderType.Gemini, Licensed);
        Check("W11", "an API provider: enter the key here, with the provider's key page beside it",
            key.Current is { Kind: ConnectStepKind.ApiKey, Action: { Kind: SetupActionKind.EnterKey, Target: "Gemini" }, Other: { Kind: SetupActionKind.OpenUrl } });
        Check("W12", "with a key only the test is left", Conn(AiProviderType.Gemini, Licensed with { GeminiKey = true }).Current?.Kind == ConnectStepKind.Test);

        Check("W13", "Ollama: install, start, get a model, test",
            Kinds(Conn(AiProviderType.Ollama, new SetupFacts())) == "Install,Start,GetModel,Test"
            && Conn(AiProviderType.Ollama, new SetupFacts { Ollama = "o" }).Current?.Kind == ConnectStepKind.Start
            && Conn(AiProviderType.Ollama, new SetupFacts { Ollama = "o", OllamaRunning = true }).Current?.Action?.Kind == SetupActionKind.Run);
        var gguf = Conn(AiProviderType.Local, new SetupFacts { LocalModel = "qwen.gguf" });
        Check("W14", "a GGUF: no test step, and the only action is the Models page, so nothing here loads weights into VRAM",
            Kinds(gguf) == "GetModel" && gguf.Current?.Action is { Kind: SetupActionKind.OpenPage, Target: "Models" }
            && Conn(AiProviderType.LlamaServer, new SetupFacts { LlamaServer = "s" }).Current?.Action is { Kind: SetupActionKind.OpenPage, Target: "Models" }
            && Conn(AiProviderType.Local, new SetupFacts { LocalLoaded = true }).Current == null);
        Check("W15", "the BrainX room: install BrainX, connect it, test",
            Conn(AiProviderType.BrainX, new SetupFacts()).Current?.Action?.Kind == SetupActionKind.InstallBrainX
            && Conn(AiProviderType.BrainX, new SetupFacts { BrainExe = "b" }).Current is { Kind: ConnectStepKind.Brain, Action: { Kind: SetupActionKind.OpenPage, Target: "McpServers" } });
        Check("W16", "a finished step keeps no button",
            ProviderConnections.All.SelectMany(p => Conn(p, new SetupFacts()).Steps.Concat(Conn(p, agyIn).Steps)).Where(s => s.Done).All(s => s.Action == null && s.Other == null));
        Check("W17", "the agy install line is only echoed by the console script: its | does not run in the echo",
            SystemSetupService.BuildScript("t", new[] { SystemSetupService.AgyInstall }).Contains("^| iex"));
    }

    /// <summary>On the thread that owns the app's resources (Program.Main's render thread).</summary>
    static void WizardDriven(SystemSetupService svc)
    {
        Console.WriteLine("── the wizard driven ──");
        var agyIn = Licensed with { AgyCli = @"C:\x\agy.exe", AgySignedIn = true };
        var shots = new List<string>();
        {
            {
                var vm = new SetupViewModel(svc);
                var w = vm.Wizard;
                w.CheckOnOpen = false;

                vm.Apply(Licensed);
                bool closedChat = false; (AiProviderType, string?)? finished = null;
                vm.ConnectCancelled += () => closedChat = true;
                vm.ConnectFinished += (p, m) => finished = (p, m);

                w.Open(AiProviderType.AntigravityDev, "default", fromChat: true);
                Check("W18", "open: nothing can be pressed until the PC has been looked at again", w.IsOpen && w.IsChecking && !w.PrimaryCommand.CanExecute(null));
                vm.Apply(Licensed);
                Check("W19", "the install step shows the command before the button is pressed",
                    !w.IsChecking && w.StepTitle.Length > 0 && w.HasCommands && w.Commands.Contains("install.ps1") && w.PrimaryLabel.Contains("~100 MB") && w.PrimaryCommand.CanExecute(null));
                shots.Add(Shot(vm, "wizard-install"));

                vm.Apply(Licensed with { AgyCli = "a" });
                Check("W20a", "an install that finished moves on by itself", w.Steps[1].IsDone && w.Steps[2].IsCurrent && w.Commands.Length == 0, w.StepTitle);
                vm.Apply(agyIn);
                Spin(() => !w.IsTesting);
                Check("W20", "signed in: the test starts by itself and a failure stays on screen, with a button to try again",
                    !w.IsDone && w.HasPrimary && w.Note.Length > 0 && w.PrimaryCommand.CanExecute(null), w.Note);
                shots.Add(Shot(vm, "wizard-failed"));

                w.CloseCommand.Execute(null);
                Check("W21", "closing a wizard the chat opened goes back to the chat, and switches nothing", !w.IsOpen && closedChat && finished == null);

                closedChat = false;
                w.Open(AiProviderType.Gemini, null, fromChat: false);
                vm.Apply(Licensed with { GeminiKey = true });
                Spin(() => !w.IsTesting);
                Check("W22", "a wrong key can be typed again from the failed test", w.HasOther && w.OtherLabel.Length > 0);
                w.CloseCommand.Execute(null);
                Check("W23", "closing one opened from the page stays on the page", !w.IsOpen && !closedChat);

                w.Open(AiProviderType.Local, "x.gguf", fromChat: true);
                vm.Apply(new SetupFacts { LocalLoaded = true, LocalModel = "x.gguf" });
                Check("W24", "done: one button back, with the provider and the model the owner picked", w.IsDone && w.FinishCommand.CanExecute(null));
                shots.Add(Shot(vm, "wizard-done"));
                w.FinishCommand.Execute(null);
                Check("W25", "finish hands back the provider and model", !w.IsOpen && finished == (AiProviderType.Local, "x.gguf"));

                w.Open(AiProviderType.GeminiDev, null, fromChat: false);
                vm.Apply(Licensed);
                Check("W26", "Gemini Dev says where personal accounts went, with a button there", w.HasHint && w.CanUseAntigravity);
                shots.Add(Shot(vm, "wizard-gemini"));
                w.UseAntigravityCommand.Execute(null);
                Check("W27", "that button moves the wizard to Antigravity", w.Provider == AiProviderType.AntigravityDev && w.IsOpen);
                w.CloseCommand.Execute(null);

                vm.Apply(agyIn with { AnthropicKey = true });
                Check("W28", "the cards: one per provider, connected ones say so, the rest say what is missing",
                    vm.Connections.Count == ProviderConnections.All.Length
                    && vm.Connections.First(c => c.Provider == AiProviderType.AntigravityDev).IsReady
                    && !vm.Connections.First(c => c.Provider == AiProviderType.OpenAIDev).IsReady
                    && vm.Connections.All(c => c.StateText.Length > 0 && !c.StateText.StartsWith("wizard.")));
                shots.Add(Shot(vm, "hub"));
            }
        }
        Check("V2", "the wizard and the cards render (no XAML / resource / binding crash)", shots.Count == 5);
        foreach (var s in shots) Console.WriteLine("        " + s);
    }

    static void WizardAssets()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "CluadeX", "Assets", "Wizard");
        var names = new[] { "install", "signin", "key", "test", "ready", "license", "start", "model", "brain" };
        bool alpha = names.All(n =>
        {
            var d = new PngBitmapDecoder(new Uri(Path.GetFullPath(Path.Combine(assets, n + ".png"))), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var px = new FormatConvertedBitmap(d, PixelFormats.Bgra32, null, 0);
            var corner = new byte[4];
            px.CopyPixels(new Int32Rect(0, 0, 1, 1), corner, 4, 0);
            return corner[3] == 0;
        });
        Check("A1", "the nine step icons have real transparent corners", alpha);
    }

    /// <summary>Let the wizard's awaits come back (this thread has a dispatcher only while it pumps).</summary>
    static void Spin(Func<bool> until)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!until() && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Thread.Sleep(20);
        }
    }

    static string Shot(SetupViewModel vm, string name)
    {
        var view = new CluadeX.Views.SetupView { DataContext = vm, Width = 1100, Height = 720 };
        view.Measure(new Size(1100, 720));
        view.Arrange(new Rect(0, 0, 1100, 720));
        view.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var rtb = new RenderTargetBitmap(1100, 720, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(view);
        string outDir = Path.Combine(AppContext.BaseDirectory, "renders");
        Directory.CreateDirectory(outDir);
        string file = Path.Combine(outDir, name + ".png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(file)) enc.Save(fs);
        return file;
    }
}
