using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.ModelPicker;
using CluadeX.Services.Providers;
using CluadeX.ViewModels;
using CluadeX.ViewModels.ModelPicker;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>PK — the full-screen model picker (owner 2026-11-06): provider first, then its models with what they can
    /// do, what they cost and whether they can be picked now; a pick goes through the old model box's own doors.</summary>
    static async Task TestModelPicker()
    {
        Log("\n## PK — the full-screen model picker");
        TestPickStatusRules();
        TestModelFacts();

        var vmType = typeof(ChatViewModel);
        var all = (IDictionary)typeof(AiProviderManager).GetField("_providers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Providers)!;
        var realOllama = all[AiProviderType.Ollama];
        var other = new FakeProvider();
        var asked = new List<(AiProviderType Provider, string Model)>();
        void Listen(AiProviderType p, string m) => asked.Add((p, m));
        Vm.ConnectionRequested += Listen;
        var picker = Vm.ModelPicker;
        var gguf = Path.Combine(Settings.DataRoot, "picker-gguf", "future-model-Q4_K_M.gguf");
        bool confirmWas = ConfirmAnswer;
        var savedConfigs = Settings.Settings.ProviderConfigs.ToDictionary(kv => kv.Key, kv => kv.Value);
        try
        {
            await FreshChat();
            Settings.UpdateSettings(s =>
            {
                s.ProviderConfigs.Remove("OpenAI");
                s.ProviderConfigs.Remove("XAI");
                s.ProviderConfigs["Anthropic"] = new ProviderConfig { ApiKey = "sk-ant-harness-not-a-key", SelectedModel = "claude-sonnet-5-5" };
                s.ActiveProvider = AiProviderType.Local;
            });
            Vm.PickerSignInLook = (t, ct) => Task.FromResult<bool?>(t != AiProviderType.ClaudeDev);
            ((IDictionary)vmType.GetField("_pickerSignIn", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Vm)!).Clear();

            // ── step 1: a card per provider ──
            picker.Open();
            await Render();
            var groups = picker.Providers.Select(p => p.Group).ToList();
            Check("PK1 step one: a card for every provider the model box knows — vendors, CLIs, Grok, this PC, the room",
                picker.IsOpen && picker.IsProviderStep
                && new[] { AiProviderType.Anthropic, AiProviderType.ClaudeDev, AiProviderType.OpenAI, AiProviderType.OpenAIDev, AiProviderType.Gemini,
                           AiProviderType.GeminiDev, AiProviderType.AntigravityDev, AiProviderType.XAI, AiProviderType.Local, AiProviderType.BrainX }
                    .All(groups.Contains),
                string.Join(",", groups));
            Check("PK1 the cards come from the model box's own list (each choice is on exactly one card)",
                Vm.ChatModelChoices.All(c => picker.Providers.Count(p => p.Group == ModelFacts.GroupOf(c.Provider)) == 1));
            Check("PK1 order: Claude first, the room last", groups[0] == AiProviderType.Anthropic && groups[^1] == AiProviderType.BrainX, string.Join(",", groups));
            var grok = picker.Providers.First(p => p.Group == AiProviderType.XAI);
            Check("PK2 every card has her girl: Grok's for xAI, Claude's, Codex's, Gemini's, CluadeX's for a GGUF on this PC",
                ReferenceEquals(grok.Mascot, ChatCharacter.Grok) && grok.Image != null
                && ReferenceEquals(Card(AiProviderType.Anthropic).Mascot, ChatCharacter.Claude)
                && ReferenceEquals(Card(AiProviderType.OpenAI).Mascot, ChatCharacter.Codex)
                && ReferenceEquals(Card(AiProviderType.Gemini).Mascot, ChatCharacter.Gemini)
                && ReferenceEquals(Card(AiProviderType.Local).Mascot, ChatCharacter.CluadeX)
                && ReferenceEquals(Card(AiProviderType.AntigravityDev).Mascot, ChatCharacter.Antigravity)
                && picker.Providers.All(p => p.Image != null));
            var local = Card(AiProviderType.Local);
            Check("PK2 whether each one answers now: Local is picked but says 'answering' only with a file loaded, Anthropic has a key, OpenAI none, Grok none",
                local.IsCurrent
                && (local.Models.Any(m => m.IsCurrent)
                    ? local.StateTone == "current"
                    : local.StateText == L(local.Models.Any(m => m.Gguf != null) ? "picker.pstate.onDisk" : "picker.pstate.noGguf"))
                && Card(AiProviderType.Anthropic).StateText == L("picker.pstate.keySaved")
                && Card(AiProviderType.OpenAI).StateText == L("picker.pstate.noKey") && grok.StateText == L("picker.pstate.noKey"),
                string.Join(" | ", picker.Providers.Select(p => p.Title + "=" + p.StateText)));
            Check("PK2 the keyboard starts on the provider that answers now", picker.Highlighted == Card(AiProviderType.Local));

            // ── step 2: OpenAI's models, GPT-6.1 Terra among them ──
            picker.PickProvider(Card(AiProviderType.OpenAI));
            await Render();
            var terra = picker.Models.FirstOrDefault(m => m.Model == "gpt-6.1-terra");
            Check("PK3 step two lists the provider's models — OpenAI's, GPT-6.1 Terra included",
                picker.IsModelStep && picker.Provider?.Group == AiProviderType.OpenAI
                && OpenAiProvider.KnownModels.All(id => picker.Models.Any(m => m.Model == id)) && terra != null,
                string.Join(",", picker.Models.Select(m => m.Model)));
            Check("PK3 Terra's card: $1 in / $5 out, cached $0.05, thinks up to max, 1.05M context, sees images, tools, everyday work",
                terra is { PriceInText: "$1", PriceOutText: "$5", HasTokenPrice: true, HasVision: true, HasTools: true }
                && terra.PriceCacheText.Contains("$0.05") && terra.ThinkingText.Contains("max") && terra.ContextText.Contains("1.05M")
                && terra.SuitsText == L("picker.suits.everyday") && terra.Title == "GPT-6.1 Terra",
                terra == null ? "no card" : $"{terra.PriceInText}/{terra.PriceOutText} {terra.PriceCacheText} {terra.ThinkingText} {terra.ContextText} v={terra.HasVision} t={terra.HasTools}");
            Check("PK4 no key: the card says so in plain words and offers to add one",
                terra is { Status: PickStatus.NeedsKey, Action: PickAction.Connect } && terra.ReasonText == LocF("picker.reason.needsKey", "OpenAI")
                && terra.ActionText == L("picker.action.needsKey"), terra?.ReasonText ?? "");
            picker.PickModel(terra!);
            await Render();
            Check("PK4 pressing it opens OpenAI's wizard with that model (the old box's door), and closes the picker",
                asked.Count == 1 && asked[0] == (AiProviderType.OpenAI, "gpt-6.1-terra") && !picker.IsOpen, string.Join(",", asked));
            asked.Clear();

            // ── a key but no Pro licence ──
            picker.OpenOn(AiProviderType.Anthropic);
            var haiku = picker.Models.First(m => m.Model == "claude-haiku-5-5");
            Check("PK5 a cloud model with a key but no Pro: 'needs Pro', why, and the press opens the wizard",
                haiku is { Status: PickStatus.NeedsPro, Action: PickAction.Connect, StatusTone: "warn" } && haiku.ReasonText == L("picker.reason.needsPro")
                && haiku.PriceInText == "$0.1" && haiku.ThinkingText.Length > 0, $"{haiku.Status} {haiku.ReasonText} {haiku.PriceInText}");
            picker.PickModel(haiku);
            await WaitUntil(() => asked.Count > 0, 2000);
            Check("PK5 …and nothing switched", asked.Count == 1 && asked[0].Provider == AiProviderType.Anthropic
                && Providers.ActiveProviderType == AiProviderType.Local && Settings.Settings.ProviderConfigs["Anthropic"].SelectedModel == "claude-sonnet-5-5",
                string.Join(",", asked));
            asked.Clear();

            // ── Grok ──
            picker.OpenOn(AiProviderType.XAI);
            var grokModel = picker.Models.Single();
            Check("PK6 Grok without a key: one card, 'needs a key', press = its settings", grokModel.Status == PickStatus.NeedsKey && grokModel.CanChoose);
            picker.Activate();
            await WaitUntil(() => asked.Count > 0, 2000);
            Check("PK6 Enter on it asks for xAI's connection", asked.Count == 1 && asked[0].Provider == AiProviderType.XAI, string.Join(",", asked));
            asked.Clear();

            // ── a CLI that is installed but signed out ──
            picker.Open();
            await WaitUntil(() => !Card(AiProviderType.ClaudeDev).StateText.Equals(L("picker.pstate.checking")), 5000);
            await Render();
            var cc = Card(AiProviderType.ClaudeDev);
            if (Vm.ChatModelChoices.Any(c => c.Provider == AiProviderType.ClaudeDev && !c.NeedsKey))
            {
                picker.PickProvider(cc);
                var ccModel = picker.Models.First();
                Check("PK7 Claude Code installed, signed out: the card says so and the models offer to sign in",
                    cc.StateText == L("picker.pstate.signedOut") && ccModel.Status == PickStatus.NeedsSignIn && ccModel.ActionText == L("picker.action.needsSignIn"),
                    cc.StateText + " / " + ccModel.Status);
                picker.PickModel(ccModel);
                Check("PK7 …and the press opens the sign-in wizard", asked.Count == 1 && asked[0].Provider == AiProviderType.ClaudeDev, string.Join(",", asked));
                asked.Clear();
            }
            else
                Check("PK7 Claude Code not installed here: 'not installed', press = install", cc.StateText == L("picker.pstate.notInstalled")
                    && cc.Models.All(m => m.Status == PickStatus.NeedsInstall));
            picker.Close();

            // ── a choice applies the model, exactly as the old box ──
            all[AiProviderType.Ollama] = other;
            vmType.GetField("_ollamaModels", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Vm, new List<string> { "picker-model", "picker-other" });
            picker.OpenOn(AiProviderType.Ollama);
            var pm = picker.Models.First(m => m.Model == "picker-model");
            Check("PK8 Ollama's models: free on this PC, available", pm is { Status: PickStatus.Ready, PriceKind: PriceKind.Free }
                && pm.PriceNote == L("picker.price.free") && pm.SuitsText == L("picker.suits.local"), pm.Status.ToString());
            picker.PickModel(pm);
            await WaitUntil(() => Providers.ActiveProviderType == AiProviderType.Ollama && Vm.SelectedChatModel?.Model == "picker-model", 5000);
            await Render();
            Check("PK8 picking it switches the chat: provider, model, saved setting, the box — and the picker closes",
                Providers.ActiveProviderType == AiProviderType.Ollama && Vm.SelectedChatModel?.Model == "picker-model"
                && Settings.Settings.ProviderConfigs["Ollama"].SelectedModel == "picker-model" && !picker.IsOpen && asked.Count == 0,
                $"{Providers.ActiveProviderType} {Vm.SelectedChatModel?.Model} {Vm.StatusText}");
            picker.OpenOn(AiProviderType.Ollama);
            var inUse = picker.Models.First(m => m.Model == "picker-model");
            Check("PK8 reopened: the model in use says so; its press only closes", inUse is { Status: PickStatus.Current, Action: PickAction.Close }
                && picker.Highlighted == inUse && Card(AiProviderType.Ollama).IsCurrent);

            // rate limit: still pickable, says until when
            var reset = DateTimeOffset.Now.AddMinutes(5);
            UsageMeter.Report(new RateLimitSnapshot("Ollama", "picker-other", null, null, null, null, null, null, 50, 0, reset, DateTime.UtcNow));
            picker.Refresh();
            var limited = picker.Models.First(m => m.Model == "picker-other");
            Check("PK9 a rate-limited model: 'rate-limited until hh:mm', still pickable",
                limited is { Status: PickStatus.RateLimited, Action: PickAction.Choose } && limited.ReasonText.Contains(reset.ToLocalTime().ToString("HH:mm")),
                limited.ReasonText);
            UsageMeter.Report(new RateLimitSnapshot("Ollama", "picker-other", null, null, null, null, null, null, 50, 50, reset, DateTime.UtcNow));
            picker.Refresh();
            Check("PK9 …and available again once the vendor reports room", picker.Models.First(m => m.Model == "picker-other").Status == PickStatus.Ready);

            // keyboard
            picker.Close();
            picker.ProviderColumns = 3;
            picker.Open();
            int start = picker.HighlightIndex;
            picker.HandleKey(Key.Right);
            bool right = picker.HighlightIndex == Math.Min(start + 1, picker.Providers.Count - 1);
            picker.HandleKey(Key.Left);
            picker.HandleKey(Key.Up);
            bool down = start >= 3 && picker.HighlightIndex == start - 3;
            picker.HandleKey(Key.Down);
            bool back = picker.HighlightIndex == start && picker.Highlighted == Card(AiProviderType.Ollama);
            picker.HandleKey(Key.Enter);
            bool models = picker.IsModelStep && picker.Provider?.Group == AiProviderType.Ollama;
            picker.HandleKey(Key.Back);
            bool stepBack = picker.IsProviderStep && picker.Highlighted == Card(AiProviderType.Ollama);
            picker.HandleKey(Key.Enter);
            picker.HandleKey(Key.Right);
            picker.HandleKey(Key.Enter);   // picker-other
            await WaitUntil(() => Vm.SelectedChatModel?.Model == "picker-other", 5000);
            Check("PK10 keys: ← → move one card, ↑ ↓ a row, Enter opens / picks, Backspace goes back",
                right && down && back && models && stepBack && Vm.SelectedChatModel?.Model == "picker-other" && !picker.IsOpen,
                $"r={right} d={down} b={back} m={models} sb={stepBack} sel={Vm.SelectedChatModel?.Model}");
            picker.Open();
            Check("PK10 Esc closes", picker.HandleKey(Key.Escape) && !picker.IsOpen && !picker.HandleKey(Key.Escape));

            // ── a GGUF on this PC ──
            Directory.CreateDirectory(Path.GetDirectoryName(gguf)!);
            File.WriteAllBytes(gguf, SyntheticGguf("zzfuture"));
            var info = new ModelInfo { Name = "future-model", FileName = Path.GetFileName(gguf), LocalPath = gguf, IsDownloaded = true, QuantizationType = "Q4_K_M", FileSize = 1234 };
            Vm.LocalModels.Add(info);
            var looks = (IDictionary)vmType.GetField("_ggufLooks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Vm)!;
            looks.Clear();
            await (Task)vmType.GetMethod("LookGgufFilesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Vm, null)!;
            var look = looks.Contains(gguf) ? looks[gguf] : null;
            string lookText = look?.ToString() ?? "";
            // the same facts the load would gather, through the same router
            string serverExe = (Providers.GetProvider(AiProviderType.LlamaServer) as LlamaServerProvider)?.ServerExePath;
            string serverDll = serverExe == null ? null : Path.Combine(Path.GetDirectoryName(serverExe)!, "llama.dll");
            var expected = LocalBackendRouter.Decide("zzfuture", LlamaBackendInfo.SupportsArchitectureInProcess("zzfuture"), serverExe != null,
                serverDll != null && File.Exists(serverDll) ? LlamaBackendInfo.SupportsArchitectureIn(serverDll, "zzfuture") : null,
                Settings.Settings.GpuLayerCount != 0 && !string.Equals(Settings.Settings.GpuBackend, "CPU", StringComparison.OrdinalIgnoreCase), false);
            Check("PK11 the GGUF's header is read off the UI thread (architecture zzfuture) and routed by LocalBackendRouter as a load would be",
                lookText.Contains("zzfuture") && lookText.Contains(", " + expected + ","), lookText + " expected " + expected);

            // Ollama answers now: a GGUF is the local setup's to load (it checks VRAM)
            picker.OpenOn(AiProviderType.Local);
            var file = picker.Models.FirstOrDefault(m => m.Gguf == info);
            looks[gguf] = (File.GetLastWriteTimeUtc(gguf), LocalBackendRoute.InProcess, "zzfuture", false);
            picker.Refresh();
            file = picker.Models.FirstOrDefault(m => m.Gguf == info);
            Check("PK12 a GGUF while another provider answers: 'on disk', loading goes through the local setup",
                file is { Status: PickStatus.LoadViaWizard, Action: PickAction.Connect, PriceKind: PriceKind.Free } && file.Subtitle.Contains("Q4_K_M"),
                file?.Status.ToString() ?? "no card");
            picker.PickModel(file!);
            Check("PK12 …its press opens the local wizard for that file", asked.Count == 1 && asked[0] == (AiProviderType.Local, "future-model"), string.Join(",", asked));
            asked.Clear();

            looks[gguf] = (File.GetLastWriteTimeUtc(gguf), LocalBackendRoute.Unsupported, "zzfuture", false);
            picker.OpenOn(AiProviderType.Local);
            file = picker.Models.First(m => m.Gguf == info);
            Check("PK13 an architecture no llama.cpp here reads: cannot be picked, and the card says why in the loader's own words",
                file is { Status: PickStatus.ArchUnsupported, CanChoose: false, StatusTone: "blocked" } && file.ReasonText.Contains("zzfuture") && file.ActionText.Length == 0,
                file.ReasonText);
            picker.PickModel(file);
            Check("PK13 pressing it does nothing but say why (the picker stays open)", picker.IsOpen && picker.Note == file.ReasonText && asked.Count == 0);
            picker.Close();

            // A GGUF answers (Local): picking a file goes through the local picker's door — it asks about VRAM first.
            all[AiProviderType.Ollama] = realOllama;
            SetField(Providers, "_activeProvider", Fake);
            Settings.UpdateSettings(s => s.ActiveProvider = AiProviderType.Local);
            looks[gguf] = (File.GetLastWriteTimeUtc(gguf), LocalBackendRoute.InProcess, "zzfuture", false);
            picker.OpenOn(AiProviderType.Local);
            file = picker.Models.First(m => m.Gguf == info);
            ConfirmTitles.Clear();
            ConfirmAnswer = false;
            picker.PickModel(file);
            await WaitUntil(() => ConfirmTitles.Count > 0, 15000);
            await Render();
            Check("PK14 a GGUF while a local model answers: the local picker's own load, which asks first (declined here — nothing loads)",
                file.Action == PickAction.Choose && ConfirmTitles.Contains(L("chat.model.confirmTitle")) && !Vm.IsLoadingModel && Vm.SelectedLocalModel != info,
                string.Join(",", ConfirmTitles));

            // no dead buttons
            picker.Open();
            var everyModel = picker.Providers.SelectMany(p => p.Models).ToList();
            var dead = everyModel.Where(m => m.CanChoose ? m.ActionText.Length == 0 : m.ReasonText.Length == 0).Select(m => m.Provider + "/" + m.Model).ToList();
            Check($"PK15 no dead card: each of {everyModel.Count} models either does something (and says what) or says why not", dead.Count == 0, string.Join(", ", dead));
            Check("PK15 every card shows a price line or a price note", everyModel.All(m => m.HasTokenPrice ? m.PriceInText.Length > 0 : m.PriceNote.Length > 0),
                string.Join(", ", everyModel.Where(m => !(m.HasTokenPrice ? m.PriceInText.Length > 0 : m.PriceNote.Length > 0)).Select(m => m.Model)));
            picker.Close();

            ModelPickerStatic();
        }
        finally
        {
            ConfirmAnswer = confirmWas;
            picker.Close();
            Vm.ConnectionRequested -= Listen;
            Vm.PickerSignInLook = null;
            all[AiProviderType.Ollama] = realOllama;
            vmType.GetField("_ollamaModels", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Vm, null);
            foreach (var m in Vm.LocalModels.Where(m => m.LocalPath == gguf).ToList()) Vm.LocalModels.Remove(m);
            SetField(Providers, "_activeProvider", Fake);
            Settings.UpdateSettings(s =>
            {
                s.ActiveProvider = AiProviderType.Local;
                s.ProviderConfigs.Clear();
                foreach (var kv in savedConfigs) s.ProviderConfigs[kv.Key] = kv.Value;
            });
            UsageMeter.Report(new RateLimitSnapshot("none", "", null, null, null, null, null, null, null, null, null, DateTime.UtcNow));
            try { Directory.Delete(Path.GetDirectoryName(gguf)!, true); } catch { }
            vmType.GetMethod("RefreshChatModelChoices", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Vm, null);
        }

        PickerProviderCard Card(AiProviderType g) => picker.Providers.First(p => p.Group == g);
    }


    /// <summary>MB — the model button (owner 2026-11-06): it names the model that really answers, glows in her colour and is
    /// locked while this chat works, and the picker says what a switch mid-chat costs (the new model re-reads the chat).</summary>
    static async Task TestModelButton()
    {
        Log("\n## MB — the model button: the real model, glow + lock while working, what a switch costs");
        Check("MB0 a switch re-reads the chat at the input price: 100K tokens at $3/M = $0.30; a plan, this PC or an empty chat has no per-token cost",
            ModelFacts.RereadCost(100_000, new PriceFacts(PriceKind.PerToken, 3m, 15m, 0.3m)) == 0.3m
            && ModelFacts.RereadCost(100_000, new PriceFacts(PriceKind.Plan, null, null, null)) == null
            && ModelFacts.RereadCost(100_000, new PriceFacts(PriceKind.Free, null, null, null)) == null
            && ModelFacts.RereadCost(0, new PriceFacts(PriceKind.PerToken, 3m, 15m, null)) == null);

        var vmType = typeof(ChatViewModel);
        var all = (IDictionary)typeof(AiProviderManager).GetField("_providers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Providers)!;
        var realOllama = all[AiProviderType.Ollama];
        var other = new FakeProvider();
        var picker = Vm.ModelPicker;
        var savedConfigs = Settings.Settings.ProviderConfigs.ToDictionary(kv => kv.Key, kv => kv.Value);
        try
        {
            await FreshChat();
            Settings.UpdateSettings(s =>
            {
                s.ProviderConfigs["Anthropic"] = new ProviderConfig { ApiKey = "sk-ant-harness-not-a-key", SelectedModel = "claude-sonnet-5-5" };
                s.ActiveProvider = AiProviderType.Local;
            });
            all[AiProviderType.Ollama] = other;
            vmType.GetField("_ollamaModels", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Vm, new List<string> { "mb-model", "mb-other" });
            picker.OpenOn(AiProviderType.Ollama);
            picker.PickModel(picker.Models.First(m => m.Model == "mb-model"));
            await WaitUntil(() => Vm.SelectedChatModel?.Model == "mb-model", 5000);
            LiveModelReport.Reset();
            await Render();
            var box = (System.Windows.Controls.Button)View.FindName("ChatModelBox");
            string BoxText() => box == null ? "" : string.Join(" ", Find<System.Windows.Controls.TextBlock>(box).Where(t => t.IsVisible).Select(t => t.Text));
            Check("MB1 picked: the button names that model (her face and colour), not the placeholder; it opens the picker",
                Vm.ModelButtonText == LiveModelReport.FriendlyModel("mb-model") && Vm.HasModelButtonText && !Vm.ShowModelPlaceholder
                && Vm.ModelButtonGlow == ChatCharacter.Mind.Accent && Vm.ModelButtonFace != null
                && box is { IsEnabled: true } && BoxText().Contains(Vm.ModelButtonText) && Vm.ModelButtonTip == L("picker.open"),
                $"text='{Vm.ModelButtonText}' box='{BoxText()}'");

            // the response names the model that really ran
            LiveModelReport.Served(AiProviderType.Ollama, "mb-model:q4_k_m");
            await WaitUntil(() => Vm.ModelButtonText == LiveModelReport.FriendlyModel("mb-model:q4_k_m"), 2000);
            await Render();
            Check("MB2 a response names the model it ran: the button shows that one",
                Vm.ModelButtonText == LiveModelReport.FriendlyModel("mb-model:q4_k_m") && BoxText().Contains(Vm.ModelButtonText), BoxText());
            LiveModelReport.Served(AiProviderType.Ollama, "someone-else");
            await Task.Delay(200);
            Check("MB2 …a report for another model than the pick (switched since) is not shown",
                Vm.ModelButtonText == LiveModelReport.FriendlyModel("mb-model"), Vm.ModelButtonText);
            LiveModelReport.Reset();

            // ── locked while this chat works ──
            other.Script.Enqueue(FakeStep.Hang());
            await Send("[MB] work for a while");
            await WaitUntil(() => Vm.IsGenerating);
            await Render();
            await Task.Delay(600);   // the glow breathes in
            var bd = box?.Template.FindName("bd", box) as System.Windows.Controls.Border;
            var glow = bd?.Effect as System.Windows.Media.Effects.DropShadowEffect;
            Check("MB3 while the chat works: locked (the command refuses, the button is disabled) and the tip says why",
                Vm.IsModelButtonLocked && !Vm.OpenModelPickerCommand.CanExecute(null) && box is { IsEnabled: false } && Vm.ModelButtonTip == L("picker.lockedTip"),
                $"locked={Vm.IsModelButtonLocked} can={Vm.OpenModelPickerCommand.CanExecute(null)} enabled={box?.IsEnabled}");
            Check("MB3 …and it glows in her colour",
                glow != null && glow.Color == ChatCharacter.Mind.Accent && glow.Opacity > 0.2, glow == null ? "no effect" : $"{glow.Color} {glow.Opacity:0.00}");
            Vm.OpenModelPickerCommand.Execute(null);
            Check("MB4 pressed anyway (keyboard, code): the picker stays shut and the status line says why",
                !picker.IsOpen && Vm.StatusText == L("picker.locked"), Vm.StatusText);
            picker.OpenOn(AiProviderType.Ollama);   // a picker left open when the run started
            picker.PickModel(picker.Models.First(m => m.Model == "mb-other"));
            await Task.Delay(300);
            Check("MB5 a pick from a picker left open does not switch under the run; the picker stays open and says why on its note line",
                Vm.SelectedChatModel?.Model == "mb-model" && Vm.IsGenerating && picker.IsOpen && picker.Note == L("picker.locked"), $"{Vm.SelectedChatModel?.Model} gen={Vm.IsGenerating} status='{Vm.StatusText}' open={picker.IsOpen} note='{picker.Note}'");
            picker.Close();
            Vm.StopGenerationCommand.Execute(null);
            await WaitIdle();
            await Task.Delay(200);
            await Render();
            Check("MB6 stopped: unlocked, and the glow goes out",
                !Vm.IsModelButtonLocked && Vm.OpenModelPickerCommand.CanExecute(null) && box is { IsEnabled: true } && Vm.ModelButtonTip == L("picker.open")
                && (glow == null || glow.Opacity < 0.05), $"enabled={box?.IsEnabled} glow={glow?.Opacity:0.00}");

            // ── what a switch costs ──
            await Send("[MB] " + string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog ", 500)));
            await WaitIdle();
            picker.Open();
            await Render();
            Check("MB7 a chat the next model must re-read: the picker warns, naming its size",
                picker.HasSwitchWarning && picker.SwitchWarning.Length > 0 && !picker.SwitchWarning.StartsWith("picker."), picker.SwitchWarning);
            picker.PickProvider(picker.Providers.First(p => p.Group == AiProviderType.Anthropic));
            var haiku = picker.Models.First(m => m.Model == "claude-haiku-5-5");
            Check("MB7 a priced model's card says what re-reading this chat costs there ($)", haiku.HasSwitchCost && haiku.SwitchCostText.Contains('$'), haiku.SwitchCostText);
            picker.OpenOn(AiProviderType.Ollama);
            var cur = picker.Models.First(m => m.Model == "mb-model");
            var free = picker.Models.First(m => m.Model == "mb-other");
            Check("MB7 the model in use says nothing; a free one on this PC says it costs time, not money",
                !cur.HasSwitchCost && free.HasSwitchCost && !free.SwitchCostText.Contains('$'), $"cur='{cur.SwitchCostText}' free='{free.SwitchCostText}'");
            picker.Close();

            await FreshChat();
            picker.Open();
            Check("MB8 a new chat has nothing to re-read: no warning, no cost lines",
                !picker.HasSwitchWarning && picker.Providers.SelectMany(p => p.Models).All(m => !m.HasSwitchCost));
            picker.Close();

            // ── MB9: the pick ran out and a run went on with another provider by itself (owner 2026-11-13: "การสลับโมเดล
            //    อัตโนมัติ ควรเปลี่ยนโมเดลในช่องกรอกแชทด้านล่างให้ตรงด้วยและขึ้นป้ายไว้") ──
            var mb9Pick = Providers.PickedProviderType;
            string mb9Before = Vm.ModelButtonText;
            try
            {
                Check("MB9 a chat on its pick shows no badge", !Vm.HasAutoSwitch && Vm.AutoSwitchBadge == "", Vm.AutoSwitchBadge);
                CluadeX.Services.Agents.QuotaBook.Shared.Note(mb9Pick, CluadeX.Services.Agents.ProviderErrorKind.QuotaExhausted, DateTime.UtcNow);
                var hop = new AgentRunScope("mb9", null);
                hop.NoteAutoSwitch(mb9Pick, "Ollama · mb-model", AiProviderType.Anthropic, "Anthropic Claude · claude-opus-5-5", "claude-opus-5-5");
                await Render();
                Check("MB9 the composer's model button names the stand-in, not the pick that ran out",
                    Vm.HasAutoSwitch && Vm.ModelButtonText == LiveModelReport.FriendlyModel("claude-opus-5-5") && Vm.ModelButtonText != mb9Before,
                    $"badge {Vm.HasAutoSwitch} · button '{Vm.ModelButtonText}' (was '{mb9Before}')");
                Check("MB9 the badge says it was switched by itself and names what it stands in for",
                    Vm.AutoSwitchBadge.Contains("mb-model") && !Vm.AutoSwitchBadge.StartsWith("chat."), Vm.AutoSwitchBadge);
                Check("MB9 its tip says from what to what, when the pick is tried again, and that the stand-in bills per token",
                    Vm.AutoSwitchTip.Contains("Ollama · mb-model") && Vm.AutoSwitchTip.Contains("Anthropic Claude · claude-opus-5-5")
                    && Vm.AutoSwitchTip.Contains("⚠") && Vm.AutoSwitchTip.Split('\n').Length >= 4 && !Vm.AutoSwitchTip.Contains("chat.autoSwitch"), Vm.AutoSwitchTip.Replace("\n", " ¦ "));
                var chip = Find<System.Windows.FrameworkElement>(View).FirstOrDefault(e => System.Windows.Automation.AutomationProperties.GetAutomationId(e) == "CxChat.AutoSwitch");
                Check("MB9 the badge is on screen in the composer, beside the model button", chip is { IsVisible: true }, chip == null ? "not found" : $"visible {chip.IsVisible}");
                var sub = new AgentRunScope("mb9", null).CreateChild();
                sub.NoteAutoSwitch(AiProviderType.Anthropic, "Anthropic Claude", AiProviderType.OpenAI, "OpenAI · gpt-6.1", "gpt-6.1");
                await Render();
                Check("MB9 a sub-agent's own hop does not change the chat's button", Vm.ModelButtonText == LiveModelReport.FriendlyModel("claude-opus-5-5"), Vm.ModelButtonText);
                hop.ClearAutoSwitch();   // the owner picked a model themselves / the run is back on the pick
                await Render();
                Check("MB9 back on the pick: the badge goes and the button names the pick again",
                    !Vm.HasAutoSwitch && Vm.AutoSwitchBadge == "" && Vm.ModelButtonText == mb9Before && chip is { IsVisible: false }, $"{Vm.HasAutoSwitch} '{Vm.ModelButtonText}'");
                hop.NoteAutoSwitch(mb9Pick, "Ollama · mb-model", AiProviderType.Anthropic, "Anthropic Claude · claude-opus-5-5", "claude-opus-5-5");
                await Render();
                CluadeX.Services.Agents.QuotaBook.Shared.Clear(mb9Pick);   // the cooldown is over
                await FreshChat();   // any redraw after that: the idle chat is on its pick again
                Check("MB9 once the pick is no longer passed over, an idle chat drops the badge by itself",
                    !Vm.HasAutoSwitch && Vm.ModelButtonText == mb9Before, $"{Vm.HasAutoSwitch} '{Vm.ModelButtonText}'");
            }
            finally { CluadeX.Services.Agents.QuotaBook.Shared.Clear(mb9Pick); }
        }
        finally
        {
            picker.Close();
            if (Vm.IsGenerating) { Vm.StopGenerationCommand.Execute(null); await WaitIdle(); }
            LiveModelReport.Reset();
            all[AiProviderType.Ollama] = realOllama;
            vmType.GetField("_ollamaModels", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Vm, null);
            SetField(Providers, "_activeProvider", Fake);
            Settings.UpdateSettings(s =>
            {
                s.ActiveProvider = AiProviderType.Local;
                s.ProviderConfigs.Clear();
                foreach (var kv in savedConfigs) s.ProviderConfigs[kv.Key] = kv.Value;
            });
            vmType.GetMethod("RefreshChatModelChoices", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Vm, null);
            await FreshChat();
        }
    }

    /// <summary>The smallest GGUF header that names an architecture (v3: magic, version, 0 tensors, 1 key).</summary>
    static byte[] SyntheticGguf(string arch)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("GGUF"));
        w.Write(3u);
        w.Write(0UL);
        w.Write(1UL);
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
        Str("general.architecture");
        w.Write(8u);   // string
        Str(arch);
        w.Write(new byte[64]);
        return ms.ToArray();
    }

    static void TestPickStatusRules()
    {
        var now = DateTimeOffset.Now;
        PickVerdict D(PickStatusInput i) => PickStatusRules.Decide(i);
        var limited = new RateLimitSnapshot("OpenAI", "m", 100, 0, null, null, null, null, null, null, now.AddMinutes(2), DateTime.UtcNow);
        var past = limited with { Reset = now.AddMinutes(-1) };
        Check("PR1 status rules: in use → close; no key / no CLI / Ollama off → open the way in",
            D(new(PickerProviderKind.Api, true, true)) == new PickVerdict(PickStatus.Current, PickAction.Close)
            && D(new(PickerProviderKind.Api, false, true)).Status == PickStatus.NeedsKey
            && D(new(PickerProviderKind.Compatible, false, true)).Status == PickStatus.NeedsKey
            && D(new(PickerProviderKind.Cli, false, true)).Status == PickStatus.NeedsInstall
            && D(new(PickerProviderKind.Ollama, false, true)).Status == PickStatus.Offline
            && D(new(PickerProviderKind.Room, false, true)).Status == PickStatus.Offline
            && D(new(PickerProviderKind.Cli, false, true)).Action == PickAction.Connect);
        Check("PR2 a signed-out CLI → sign in; signed in or unknown → ready; no Pro → Pro first",
            D(new(PickerProviderKind.Cli, false, false, SignedIn: false)).Status == PickStatus.NeedsSignIn
            && D(new(PickerProviderKind.Cli, false, false, SignedIn: null)).Status == PickStatus.Ready
            && D(new(PickerProviderKind.Cli, false, false, NeedsPro: true, SignedIn: true)).Status == PickStatus.NeedsPro
            && D(new(PickerProviderKind.Api, false, false, NeedsPro: true, Rate: limited, Now: now)).Status == PickStatus.NeedsPro);
        Check("PR3 rate-limited only while the reset is ahead, and never blocks the pick",
            D(new(PickerProviderKind.Api, false, false, Rate: limited, Now: now)) == new PickVerdict(PickStatus.RateLimited, PickAction.Choose)
            && D(new(PickerProviderKind.Api, false, false, Rate: past, Now: now)).Status == PickStatus.Ready
            && D(new(PickerProviderKind.Api, false, false, Rate: limited with { TokensRemaining = 60 }, Now: now)).Status == PickStatus.Ready);
        Check("PR4 a GGUF: unsupported architecture blocks; busy waits; another provider answering → the local setup; local answering → pick",
            D(new(PickerProviderKind.LocalGguf, false, false, Route: LocalBackendRoute.Unsupported, LocalAnswering: true)) == new PickVerdict(PickStatus.ArchUnsupported, PickAction.None)
            && D(new(PickerProviderKind.LocalGguf, false, false, Route: LocalBackendRoute.Server, LocalAnswering: true, LocalBusy: true)).Status == PickStatus.Loading
            && D(new(PickerProviderKind.LocalGguf, false, false, Route: LocalBackendRoute.InProcess)) == new PickVerdict(PickStatus.LoadViaWizard, PickAction.Connect)
            && D(new(PickerProviderKind.LocalGguf, false, false, LocalAnswering: true)) == new PickVerdict(PickStatus.Ready, PickAction.Choose)
            && !new PickVerdict(PickStatus.ArchUnsupported, PickAction.None).CanChoose);
    }

    static void TestModelFacts()
    {
        var terra = ModelFacts.PriceOf(AiProviderType.OpenAI, "gpt-6.1-terra");
        Check("PF1 prices come from CostTrackingService: Terra $1 / $5 (cached $0.05); a CLI is your plan; this PC is free; an unpriced service says so",
            terra == new PriceFacts(PriceKind.PerToken, 1m, 5m, 0.05m)
            && ModelFacts.PriceOf(AiProviderType.ClaudeDev, "opus").Kind == PriceKind.Plan
            && ModelFacts.PriceOf(AiProviderType.Local, "x").Kind == PriceKind.Free && ModelFacts.PriceOf(AiProviderType.Ollama, "x").Kind == PriceKind.Free
            && ModelFacts.PriceOf(AiProviderType.XAI, "no-such-model-xyz").Kind == PriceKind.Unknown, terra.ToString());
        Check("PF2 thinking from each provider's request rules: GPT-6.1 Terra / Opus 5.5 max, Gemini 3.8 Flash high, Gemma and gpt-4o none",
            ModelFacts.ThinkingOf(AiProviderType.OpenAI, "gpt-6.1-terra") == "max" && ModelFacts.ThinkingOf(AiProviderType.Anthropic, "claude-opus-5-5") == "max"
            && ModelFacts.ThinkingOf(AiProviderType.Gemini, "gemini-3.8-flash") == "high" && ModelFacts.ThinkingOf(AiProviderType.Gemini, "gemma-4-31b-it") == null
            && ModelFacts.ThinkingOf(AiProviderType.OpenAI, "gpt-4o") == null && ModelFacts.ThinkingOf(AiProviderType.ClaudeDev, "haiku") != null,
            ModelFacts.ThinkingOf(AiProviderType.Anthropic, "claude-opus-5-5") ?? "null");
        Check("PF3 context from ContextMemoryService: Terra 1.05M, Haiku 4.5 200K; written 1.05M / 200K / 12K",
            ModelFacts.ContextOf(AiProviderType.OpenAI, "gpt-6.1-terra", 12288) == 1_050_000
            && ModelFacts.FormatTokens(1_050_000) == "1.05M" && ModelFacts.FormatTokens(1_000_000) == "1M"
            && ModelFacts.FormatTokens(ModelFacts.ContextOf(AiProviderType.Anthropic, "claude-haiku-4-5", 12288)) == "200K"
            && ModelFacts.FormatTokens(12288) == "12K");
        Check("PF4 what each family suits: Astra / Fable top, Opus deep, Sol / Sonnet balanced, Terra everyday, Haiku / Flash fast, Luna volume, local private",
            ModelFacts.SuitsKeyOf(AiProviderType.OpenAI, "gpt-6-astra") == "picker.suits.top" && ModelFacts.SuitsKeyOf(AiProviderType.Anthropic, "claude-fable-5-1") == "picker.suits.top"
            && ModelFacts.SuitsKeyOf(AiProviderType.Anthropic, "claude-opus-5-5") == "picker.suits.deep" && ModelFacts.SuitsKeyOf(AiProviderType.ClaudeDev, "opus") == "picker.suits.deep"
            && ModelFacts.SuitsKeyOf(AiProviderType.OpenAI, "gpt-6.1-sol") == "picker.suits.balanced" && ModelFacts.SuitsKeyOf(AiProviderType.OpenAI, "gpt-6.1-terra") == "picker.suits.everyday"
            && ModelFacts.SuitsKeyOf(AiProviderType.Anthropic, "claude-haiku-5-5") == "picker.suits.fast" && ModelFacts.SuitsKeyOf(AiProviderType.Gemini, "gemini-3.8-flash") == "picker.suits.fast"
            && ModelFacts.SuitsKeyOf(AiProviderType.OpenAI, "gpt-6-luna") == "picker.suits.volume" && ModelFacts.SuitsKeyOf(AiProviderType.Local, "anything") == "picker.suits.local"
            && ModelFacts.SuitsKeyOf(AiProviderType.OpenAIDev, "default") == "picker.suits.cliDefault");
        Check("PF5 girls: xAI (and any future Grok provider, by name) is the Grok girl; a GGUF is CluadeX's, Ollama มายด์, Antigravity her own",
            ModelFacts.IsGrok(AiProviderType.XAI) && ReferenceEquals(ModelFacts.MascotOf(AiProviderType.XAI), ChatCharacter.Grok)
            && ReferenceEquals(ModelFacts.MascotOf(AiProviderType.LlamaServer), ChatCharacter.CluadeX) && ModelFacts.GroupOf(AiProviderType.LlamaServer) == AiProviderType.Local
            && ReferenceEquals(ModelFacts.MascotOf(AiProviderType.Ollama), ChatCharacter.Mind)
            && ReferenceEquals(ModelFacts.MascotOf(AiProviderType.AntigravityDev), ChatCharacter.Antigravity));
        Check("PF6 Grok Dev is a CLI on the owner's Grok plan: the Grok girl, after xAI's API card and before this PC, the effort GrokDevProvider sends",
            ModelFacts.KindOf(AiProviderType.GrokDev) == PickerProviderKind.Cli
            && ModelFacts.PriceOf(AiProviderType.GrokDev, "grok-4.7").Kind == PriceKind.Plan
            && ModelFacts.PlanKeyOf(AiProviderType.GrokDev) == "chat.model.grokPlan"
            && ReferenceEquals(ModelFacts.MascotOf(AiProviderType.GrokDev), ChatCharacter.Grok)
            && ModelFacts.RankOf(AiProviderType.XAI) < ModelFacts.RankOf(AiProviderType.GrokDev)
            && ModelFacts.RankOf(AiProviderType.GrokDev) < ModelFacts.RankOf(AiProviderType.Ollama)
            && ModelFacts.ThinkingOf(AiProviderType.GrokDev, "default") == GrokModels.MaxEffort(GrokModels.DefaultModel)
            && ModelFacts.SuitsKeyOf(AiProviderType.GrokDev, "grok-4.7-build-fast") == "picker.suits.fast"
            && ModelFacts.SuitsKeyOf(AiProviderType.GrokDev, "grok-4.7") == "picker.suits.balanced");
    }

    /// <summary>The static checks that catch what compiles but renders blank: Loc keys with Thai, resources, bindings.</summary>
    static void ModelPickerStatic()
    {
        string repo = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CluadeX", "Views", "ModelPickerOverlay.xaml"))) { repo = d.FullName; break; }
        if (repo == null) { Check("PKS repository found next to the harness", false); return; }
        string R(params string[] p) => File.ReadAllText(Path.Combine(new[] { repo, "CluadeX" }.Concat(p).ToArray()));
        string xaml = R("Views", "ModelPickerOverlay.xaml");
        string code = R("ViewModels", "ChatViewModel.ModelPicker.cs") + R("ViewModels", "ChatViewModel.ModelButton.cs") + R("Services", "ModelPicker", "ModelFacts.cs");
        string chatView = R("Views", "ChatView.xaml");
        var keys = Regex.Matches(xaml + chatView, @"\{services:Loc\s+(picker\.[\w.]+)\}").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(xaml, @"\{services:Loc\s+([\w.]+)\}").Select(m => m.Groups[1].Value))
            .Concat(Regex.Matches(code, @"""((?:picker|chat\.model)\.[\w.]+)""").Select(m => m.Groups[1].Value).Where(k => !k.EndsWith('.')))
            .Concat(Enum.GetValues<PickStatus>().Select(s => "picker.status." + char.ToLowerInvariant(s.ToString()[0]) + s.ToString()[1..]))
            .Concat(new[] { PickStatus.NeedsKey, PickStatus.NeedsInstall, PickStatus.NeedsSignIn, PickStatus.NeedsPro, PickStatus.Offline, PickStatus.LoadViaWizard }
                .Select(s => "picker.action." + char.ToLowerInvariant(s.ToString()[0]) + s.ToString()[1..]))
            .Concat(Enum.GetValues<PickerProviderKind>().Select(k => "picker.kind." + k.ToString().ToLowerInvariant()))
            .Distinct().ToList();
        LocSvc.SetLanguage("th");
        var noThai = keys.Where(k => LocSvc.T(k) == k || !Regex.IsMatch(LocSvc.T(k), "[฀-๿]")).ToList();
        Check($"PKS1 every localized key the picker uses ({keys.Count}) has a Thai value", noThai.Count == 0, string.Join(", ", noThai));
        LocSvc.SetLanguage("en");
        var noEn = keys.Where(k => LocSvc.T(k) == k).ToList();
        Check("PKS1 …and an English one", noEn.Count == 0, string.Join(", ", noEn));

        string resources = R("Resources", "Theme.xaml") + R("App.xaml") + xaml;
        var missingRes = Regex.Matches(xaml, @"\{StaticResource\s+([\w.]+)\}").Select(m => m.Groups[1].Value).Distinct()
            .Where(k => !resources.Contains($"x:Key=\"{k}\"", StringComparison.Ordinal)).ToList();
        Check("PKS2 every {StaticResource} exists", missingRes.Count == 0, string.Join(", ", missingRes));
        foreach (var theme in Directory.GetFiles(Path.Combine(repo, "CluadeX", "Resources", "Themes"), "*.xaml").Where(f => !f.EndsWith("ScrollBars.xaml")))
        {
            string t = File.ReadAllText(theme);
            var missingDyn = Regex.Matches(xaml, @"\{DynamicResource\s+([\w.]+)\}").Select(m => m.Groups[1].Value).Distinct()
                .Where(k => !t.Contains($"x:Key=\"{k}\"", StringComparison.Ordinal)).ToList();
            Check($"PKS3 every {{DynamicResource}} is a token of {Path.GetFileNameWithoutExtension(theme)}", missingDyn.Count == 0, string.Join(", ", missingDyn));
        }
        var types = new[] { typeof(ModelPickerViewModel), typeof(PickerProviderCard), typeof(PickerModelCard) };
        var bad = Regex.Matches(xaml, @"\{Binding\s+(?:Path=)?([\w.]+)").Select(m => m.Groups[1].Value)
            .Select(p => p.StartsWith("DataContext.", StringComparison.Ordinal) ? p["DataContext.".Length..] : p)
            .Distinct().Where(p => !Resolves(types, p)).ToList();
        Check("PKS4 every {Binding} in the overlay names a real property (each segment)", bad.Count == 0, string.Join(", ", bad));
        int at = chatView.IndexOf("x:Name=\"ChatModelBox\"", StringComparison.Ordinal);
        int end = at < 0 ? -1 : chatView.IndexOf("</Button>", at, StringComparison.Ordinal);
        string button = at < 0 || end < 0 ? "" : chatView[at..end];
        var badButton = Regex.Matches(button, @"\{Binding\s+(?:Path=)?([\w.]+)").Select(m => m.Groups[1].Value).Distinct()
            .Where(p => !Resolves(new[] { typeof(ChatViewModel) }, p)).ToList();
        Check("PKS6 the model button's {Binding}s name real ChatViewModel properties (name, face, glow, lock, tip)",
            button.Length > 0 && badButton.Count == 0 && button.Contains("ModelButtonText") && button.Contains("IsModelButtonLocked") && button.Contains("ModelButtonGlow"),
            button.Length == 0 ? "button not found" : string.Join(", ", badButton));
        var main = R("MainWindow.xaml");
        Check("PKS5 MainWindow hosts the overlay over everything, on the chat's picker; the chat's box opens it",
            main.Contains("<views:ModelPickerOverlay") && main.Contains("{Binding ChatVM.ModelPicker}")
            && chatView.Contains("x:Name=\"ChatModelBox\" Command=\"{Binding OpenModelPickerCommand}\""));
    }

    /// <summary>A binding path resolves when its first segment is a property of one of the DataContext types and every
    /// later segment is a property of the type before it.</summary>
    static bool Resolves(Type[] roots, string path)
    {
        var segs = path.Split('.');
        foreach (var root in roots)
        {
            Type t = root;
            bool ok = true;
            foreach (var s in segs)
            {
                var p = t.GetProperty(s);
                if (p == null) { ok = false; break; }
                t = p.PropertyType;
            }
            if (ok) return true;
        }
        return false;
    }
}
