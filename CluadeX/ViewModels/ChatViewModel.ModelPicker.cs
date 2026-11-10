using System.IO;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.ModelPicker;
using CluadeX.Services.Providers;
using CluadeX.ViewModels.ModelPicker;

namespace CluadeX.ViewModels;

/// <summary>
/// The full-screen model picker (ModelPickerOverlay) — built from the same list the old model box showed
/// (<see cref="ChatModelChoices"/>, plus the GGUF files of the local picker), so the two can never disagree about what
/// exists, and a pick goes through the same doors: <see cref="SelectedChatModel"/> for a model of a provider,
/// <see cref="SelectedLocalModel"/> for a GGUF while one answers (it asks about VRAM first), the provider's wizard for
/// anything not connected.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>Providers that always get a card even when nothing of theirs is set up, so the owner sees they exist.
    /// The xAI API (a compatible preset, so it is not in the model box until set up); Grok Dev has its own row there.</summary>
    private static readonly AiProviderType[] PickerAlwaysListed = [AiProviderType.XAI];

    private ModelPickerViewModel? _modelPicker;
    public ModelPickerViewModel ModelPicker => _modelPicker ??= MakeModelPicker();

    private ICommand? _openModelPicker;
    /// <summary>The model button. Locked while this chat's run works (owner 2026-11-06: a switch mid-answer throws away what
    /// that turn already spent and re-sends the whole chat to the new model) — press Stop first.</summary>
    public ICommand OpenModelPickerCommand => _openModelPicker ??= new RelayCommand(
        () => { if (IsGenerating) StatusText = Loc("picker.locked"); else ModelPicker.Open(); },
        () => !IsGenerating);

    private ModelPickerViewModel MakeModelPicker()
    {
        var vm = new ModelPickerViewModel(BuildPickerCards, ApplyPick, () => IsGenerating ? Loc("picker.locked") : null);
        vm.Opened += () => _ = LookAroundForPickerAsync();
        return vm;
    }

    // ─── slow looks, made when the picker opens ───

    /// <summary>Each installed CLI's last sign-in look (null = the look failed), and when.</summary>
    private readonly Dictionary<AiProviderType, (bool? SignedIn, DateTime At)> _pickerSignIn = new();
    private readonly HashSet<AiProviderType> _pickerLooking = new();
    private static readonly TimeSpan PickerSignInEvery = TimeSpan.FromMinutes(10);

    /// <summary>How a CLI's sign-in is looked at; null = the provider's own check (capped at 8 s). A harness sets it.</summary>
    public Func<AiProviderType, CancellationToken, Task<bool?>>? PickerSignInLook { get; set; }

    /// <summary>A GGUF file's route (where it would load, or that nothing here can) and projector, by path + write time.</summary>
    private readonly Dictionary<string, (DateTime Stamp, LocalBackendRoute Route, string? Arch, bool Vision)> _ggufLooks =
        new(StringComparer.OrdinalIgnoreCase);

    private async Task LookAroundForPickerAsync()
    {
        try
        {
            await ProbeCliForChatBoxAsync();
            ProbeOllamaForChatBox();
            var looks = new List<Task>();
            foreach (var type in new[] { AiProviderType.ClaudeDev, AiProviderType.OpenAIDev, AiProviderType.GeminiDev, AiProviderType.AntigravityDev, AiProviderType.GrokDev })
            {
                if (!ChatModelChoices.Any(c => c.Provider == type && !c.NeedsKey)) continue;   // not installed: nothing to sign in to
                if (_pickerLooking.Contains(type)
                    || _pickerSignIn.TryGetValue(type, out var last) && DateTime.UtcNow - last.At < PickerSignInEvery) continue;
                _pickerLooking.Add(type);
                looks.Add(LookSignInAsync(type));
            }
            looks.Add(LookGgufFilesAsync());
            ModelPicker.Refresh();   // "checking…" on the CLIs being looked at
            await Task.WhenAll(looks);
        }
        catch (Exception ex) { _debugLog?.Warn("Chat", "the model picker's looks failed", ex); }
    }

    private async Task LookSignInAsync(AiProviderType type)
    {
        bool? signedIn = null;
        try
        {
            using var limit = new CancellationTokenSource(SignInLookLimit);
            signedIn = await (PickerSignInLook ?? ProviderSignInLook)(type, limit.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
        catch (Exception ex) { _debugLog?.Warn("Chat", $"looking whether {type} is signed in failed", ex); }
        _pickerSignIn[type] = (signedIn, DateTime.UtcNow);
        _pickerLooking.Remove(type);
        ModelPicker.Refresh();
    }

    private async Task<bool?> ProviderSignInLook(AiProviderType type, CancellationToken ct) =>
        _providerManager.GetProvider(type) is IDelegatedCliProvider cli ? await cli.IsLoggedInAsync(ct) : null;

    /// <summary>Read each GGUF's architecture and ask the llama.cpp files of this copy whether they can run it
    /// (LocalBackendRouter — the decision the load itself makes), off the UI thread, once per file version.</summary>
    private async Task LookGgufFilesAsync()
    {
        var files = LocalModels.Where(m => !string.IsNullOrEmpty(m.LocalPath)).Select(m => m.LocalPath!).ToList();
        if (files.Count == 0) return;
        var s = _settingsService.Settings;
        bool wantGpu = Services.Gpu.GpuShare.WantsGpu(s.GpuLayerCount, s.GpuBackend, s.GpuUsagePercent);
        string? serverExe = (_providerManager.GetProvider(AiProviderType.LlamaServer) as LlamaServerProvider)?.ServerExePath;
        var found = await Task.Run(() =>
        {
            var list = new List<(string Path, DateTime Stamp, LocalBackendRoute Route, string? Arch, bool Vision)>();
            foreach (var path in files)
            {
                try
                {
                    var stamp = File.GetLastWriteTimeUtc(path);
                    lock (_ggufLooks) if (_ggufLooks.TryGetValue(path, out var had) && had.Stamp == stamp) continue;
                    string? arch = GgufMetadataReader.TryReadArchitecture(path);
                    bool? inProcess = string.IsNullOrEmpty(arch) ? null : LlamaBackendInfo.SupportsArchitectureInProcess(arch);
                    string? serverDll = serverExe == null ? null : Path.Combine(Path.GetDirectoryName(serverExe)!, "llama.dll");
                    bool? server = serverDll != null && File.Exists(serverDll) && !string.IsNullOrEmpty(arch)
                        ? LlamaBackendInfo.SupportsArchitectureIn(serverDll, arch) : null;
                    bool projector = VisionProjector.FindFor(path) != null;
                    var route = LocalBackendRouter.Decide(arch, inProcess, serverExe != null, server, wantGpu, projector && serverExe != null);
                    list.Add((path, stamp, route, arch, projector));
                }
                catch { /* unreadable file: no verdict, the load says why */ }
            }
            return list;
        });
        if (found.Count == 0) return;
        lock (_ggufLooks) foreach (var f in found) _ggufLooks[f.Path] = (f.Stamp, f.Route, f.Arch, f.Vision);
        ModelPicker.Refresh();
    }

    // ─── the cards ───

    private IReadOnlyList<PickerProviderCard> BuildPickerCards()
    {
        RefreshChatModelChoices();
        var s = _settingsService.Settings;
        var picked = ModelFacts.GroupOf(_providerManager.PickedProviderType);
        _pickerChatTokens = PickerChatTokens();
        if (_modelPicker != null)
            _modelPicker.SwitchWarning = _pickerChatTokens >= SwitchWarnFrom
                ? LocF("picker.switch.warning", ModelFacts.FormatTokens(_pickerChatTokens)) : "";
        var choices = ChatModelChoices.ToList();
        foreach (var t in PickerAlwaysListed)
            if (!choices.Any(c => ModelFacts.GroupOf(c.Provider) == t)) choices.Add(NotSetUp(t));

        var cards = new List<PickerProviderCard>();
        foreach (var g in choices.GroupBy(c => ModelFacts.GroupOf(c.Provider))
                                 .OrderBy(g => ModelFacts.RankOf(g.Key)).ThenBy(g => g.Key.ToString()))
        {
            var type = g.Key;
            var kind = ModelFacts.KindOf(type);
            bool setUp = g.Any(c => !c.NeedsKey);
            var models = type == AiProviderType.Local ? GgufCards(g.ToList(), s) : g.Select(c => ModelCard(c, s, picked)).ToList();
            var (state, tone) = ProviderState(type, kind, setUp, type == picked, models);
            string title = type == AiProviderType.Local ? Loc("picker.provider.local") : g.First().ProviderLabel;
            cards.Add(new PickerProviderCard(type, title, kind, Loc("picker.kind." + kind.ToString().ToLowerInvariant()),
                state, tone, type == picked, models)
            {
                ModelCountText = type == AiProviderType.Local && models.All(m => m.Gguf == null)
                    ? Loc("picker.count.noGguf")
                    : LocF(models.Count == 1 ? "picker.count.one" : "picker.count.many", models.Count),
            });
        }
        return cards;
    }

    private (string Text, string Tone) ProviderState(AiProviderType type, PickerProviderKind kind, bool setUp, bool current,
                                                     IReadOnlyList<PickerModelCard> models)
    {
        // GGUF answers only once a file is loaded: picked-but-empty says what is really on disk, not "answering"
        if (kind == PickerProviderKind.LocalGguf && !models.Any(m => m.IsCurrent))
            return models.Any(m => m.Gguf != null) ? (Loc("picker.pstate.onDisk"), "ok") : (Loc("picker.pstate.noGguf"), "off");
        if (current) return (Loc("picker.pstate.current"), "current");
        if (!setUp)
            return kind switch
            {
                PickerProviderKind.Cli => (Loc("picker.pstate.notInstalled"), "off"),
                PickerProviderKind.Ollama => (Loc("picker.pstate.ollamaOff"), "off"),
                PickerProviderKind.Room => (Loc("picker.pstate.brainOff"), "off"),
                PickerProviderKind.LocalGguf => models.Any(m => m.Gguf != null)
                    ? (Loc("picker.pstate.onDisk"), "ok") : (Loc("picker.pstate.noGguf"), "off"),
                _ => (Loc("picker.pstate.noKey"), "off"),
            };
        switch (kind)
        {
            case PickerProviderKind.Cli:
                if (_pickerLooking.Contains(type)) return (Loc("picker.pstate.checking"), "busy");
                if (_pickerSignIn.TryGetValue(type, out var look) && look.SignedIn is { } signedIn)
                    return signedIn ? (Loc("picker.pstate.signedIn"), "ok") : (Loc("picker.pstate.signedOut"), "warn");
                return (Loc("picker.pstate.installed"), "ok");
            case PickerProviderKind.Ollama: return (Loc("picker.pstate.running"), "ok");
            case PickerProviderKind.Room: return (Loc("picker.pstate.connected"), "ok");
            case PickerProviderKind.LocalGguf: return (Loc("picker.pstate.onDisk"), "ok");
            default:
                return CompatiblePresets.For(type)?.Local == true ? (Loc("picker.pstate.running"), "ok") : (Loc("picker.pstate.keySaved"), "ok");
        }
    }

    /// <summary>A model of a provider (one entry of the old model box).</summary>
    private PickerModelCard ModelCard(ChatModelChoice c, AppSettings s, AiProviderType picked)
    {
        var type = c.Provider;
        var kind = ModelFacts.KindOf(type);
        bool other = ModelFacts.GroupOf(type) != picked;
        // The same licence gate SwitchChatModelAsync applies to a provider change.
        bool needsPro = !c.NeedsKey && other && type is not (AiProviderType.BrainX or AiProviderType.Ollama)
                        && CompatiblePresets.For(type)?.Local != true && _activation?.IsActivated != true
                        && kind != PickerProviderKind.LocalGguf;
        bool? signedIn = kind == PickerProviderKind.Cli && _pickerSignIn.TryGetValue(type, out var look) ? look.SignedIn : null;
        var rate = UsageMeter.Latest is { } snap && string.Equals(snap.Provider, type.ToString(), StringComparison.OrdinalIgnoreCase)
                   && (snap.Model.StartsWith(c.Model, StringComparison.OrdinalIgnoreCase) || c.Model.StartsWith(snap.Model, StringComparison.OrdinalIgnoreCase))
            ? snap : null;
        bool current = Equals(c, _selectedChatModel);
        var verdict = PickStatusRules.Decide(new PickStatusInput(kind, current, c.NeedsKey, needsPro, signedIn, rate,
            LocalAnswering: ShowLocalModelPicker, LocalBusy: LocalBusy()));
        // A GGUF that answers is listed by the box as a choice too; on this card it stays what it is.
        if (kind == PickerProviderKind.LocalGguf && c.NeedsKey) verdict = new(PickStatus.LoadViaWizard, PickAction.Connect);

        bool cliDefault = c.Model is "default" or "auto";
        var provider = _providerManager.GetProvider(type);
        bool tools = kind switch
        {
            PickerProviderKind.Cli => true,   // the CLI runs its own tools
            PickerProviderKind.Room => false,
            _ => CompatibleProvider.InfoOf(type, c.Model)?.Tools ?? SafeBool(() => provider.SupportsNativeToolUse),
        };
        bool vision = kind is PickerProviderKind.Api or PickerProviderKind.Compatible or PickerProviderKind.Ollama
                      && SafeBool(() => provider.SupportsToolResultImages(c.Model));
        string? thinking = ModelFacts.ThinkingOf(type, c.Model);
        var price = ModelFacts.PriceOf(type, c.Model);
        int ctx = ModelFacts.ContextOf(type, c.Model, s.ContextSize);

        return new PickerModelCard
        {
            Provider = type,
            Model = c.Model,
            Title = cliDefault ? Loc("picker.model.cliDefault") : kind == PickerProviderKind.Room ? Loc("picker.model.room") : LiveModelReport.FriendlyModel(c.Model),
            Subtitle = c.Model,
            Choice = c,
            HasTools = tools,
            HasVision = vision,
            ThinkingText = thinking == null ? "" : thinking == "on" ? Loc("picker.cap.thinks") : LocF("picker.cap.thinksUpTo", thinking),
            ContextText = kind == PickerProviderKind.Room ? "" : LocF("picker.cap.context", ModelFacts.FormatTokens(ctx)),
            SuitsText = Loc(ModelFacts.SuitsKeyOf(type, c.Model)),
            PriceKind = price.Kind,
            PriceInText = price.InputPerM is { } i ? ModelFacts.Usd(i) : "",
            PriceOutText = price.OutputPerM is { } o ? ModelFacts.Usd(o) : "",
            PriceCacheText = price.CacheReadPerM is { } cr && cr > 0 ? LocF("picker.price.cached", ModelFacts.Usd(cr)) : "",
            PriceNote = PriceNoteOf(type, price.Kind),
            Status = verdict.Status,
            Action = verdict.Action,
            StatusText = Loc("picker.status." + Camel(verdict.Status)),
            StatusTone = ToneOf(verdict.Status),
            ReasonText = ReasonOf(verdict.Status, c.ProviderLabel, rate, null),
            ActionText = ActionTextOf(verdict),
            SwitchCostText = current || verdict.Action == PickAction.None ? "" : SwitchCostOf(price),
            Mascot = ModelFacts.MascotOf(type),
        };
    }

    // ─── what a switch costs in a chat that already has history ───

    /// <summary>The chat on screen, in tokens, as the model would be sent it (0 before the first answer).</summary>
    private int _pickerChatTokens;
    /// <summary>Below this a re-read costs too little to talk about.</summary>
    private const int SwitchWarnFrom = 1_000;

    private int PickerChatTokens()
    {
        try
        {
            var messages = Messages.ToList();
            if (!messages.Any(m => m.Role == MessageRole.Assistant)) return 0;
            return ContextUsed(messages, CurrentSession?.LastMeasuredContext, _providerManager.ActiveProviderType,
                _contextMemoryService.CalculateTotalTokens).Used;
        }
        catch { return 0; }
    }

    /// <summary>"First message ≈ $0.42 to re-read this chat (85K tokens)" — per token, from the plan, or free.</summary>
    private string SwitchCostOf(PriceFacts price)
    {
        int t = _pickerChatTokens;
        if (t < SwitchWarnFrom) return "";
        string tokens = ModelFacts.FormatTokens(t);
        return price.Kind switch
        {
            PriceKind.PerToken when ModelFacts.RereadCost(t, price) is { } usd => LocF("picker.switch.cost", ModelFacts.Usd(usd), tokens),
            PriceKind.Plan => LocF("picker.switch.plan", tokens),
            PriceKind.Free => LocF("picker.switch.free", tokens),
            _ => "",
        };
    }

    /// <summary>The GGUF files on this PC (the local picker's list); when there are none, the box's "set up" entry.</summary>
    private List<PickerModelCard> GgufCards(List<ChatModelChoice> choices, AppSettings s)
    {
        var files = LocalModels.ToList();
        if (files.Count == 0)
            return choices.Select(c => ModelCard(c, s, ModelFacts.GroupOf(_providerManager.PickedProviderType))).ToList();

        bool answering = ShowLocalModelPicker;
        string? loadedPath = LoadedGgufPath();
        bool busy = LocalBusy();
        var list = new List<PickerModelCard>();
        foreach (var m in files)
        {
            (DateTime Stamp, LocalBackendRoute Route, string? Arch, bool Vision) look = default;
            bool looked;
            lock (_ggufLooks) looked = m.LocalPath != null && _ggufLooks.TryGetValue(m.LocalPath, out look);
            bool current = answering && loadedPath != null && string.Equals(loadedPath, m.LocalPath, StringComparison.OrdinalIgnoreCase);
            var verdict = PickStatusRules.Decide(new PickStatusInput(PickerProviderKind.LocalGguf, current, false,
                Route: looked ? look.Route : null, LocalAnswering: answering, LocalBusy: busy));
            string reason = verdict.Status == PickStatus.ArchUnsupported && look.Arch != null
                ? LocalBackendRouter.Refusal(look.Arch, m.LocalPath!,
                    (_providerManager.GetProvider(AiProviderType.LlamaServer) as LlamaServerProvider)?.ServerExePath, s.Language).ShortMessage
                : ReasonOf(verdict.Status, Loc("picker.provider.local"), null, m.Name);
            list.Add(new PickerModelCard
            {
                Provider = AiProviderType.Local,
                Model = m.Name,
                Title = m.Name,
                Subtitle = string.Join(" · ", new[] { m.QuantizationType, m.FileSizeDisplay, look.Arch ?? "" }.Where(x => !string.IsNullOrWhiteSpace(x))),
                Gguf = m,
                HasTools = SafeBool(() => _providerManager.GetProvider(AiProviderType.LlamaServer).SupportsNativeToolUse),
                HasVision = looked ? look.Vision : m.HasVision,
                ContextText = LocF("picker.cap.context", ModelFacts.FormatTokens(ModelFacts.ContextOf(AiProviderType.Local, m.Name, s.ContextSize))),
                SuitsText = Loc("picker.suits.local"),
                PriceKind = PriceKind.Free,
                PriceNote = PriceNoteOf(AiProviderType.Local, PriceKind.Free),
                Status = verdict.Status,
                Action = verdict.Action,
                StatusText = Loc("picker.status." + Camel(verdict.Status)),
                StatusTone = ToneOf(verdict.Status),
                ReasonText = reason,
                ActionText = ActionTextOf(verdict),
                SwitchCostText = current || verdict.Action == PickAction.None ? "" : SwitchCostOf(new PriceFacts(PriceKind.Free, null, null, null)),
                Mascot = ModelFacts.MascotOf(AiProviderType.Local),
            });
        }
        return list;
    }

    private bool LocalBusy() => IsLoadingModel || IsGenerating || _llamaService.IsLoading || (_localGgufProvider?.IsLoading ?? false);

    private static bool SafeBool(Func<bool> read)
    {
        try { return read(); } catch { return false; }
    }

    private static string Camel(PickStatus s) => char.ToLowerInvariant(s.ToString()[0]) + s.ToString()[1..];

    private static string ToneOf(PickStatus s) => s switch
    {
        PickStatus.Current => "current",
        PickStatus.Ready => "ok",
        PickStatus.RateLimited or PickStatus.NeedsPro or PickStatus.NeedsSignIn => "warn",
        PickStatus.Loading => "busy",
        PickStatus.ArchUnsupported => "blocked",
        _ => "off",
    };

    private static string PriceNoteOf(AiProviderType type, PriceKind kind) => kind switch
    {
        PriceKind.Free => Loc("picker.price.free"),
        PriceKind.Plan => ModelFacts.PlanKeyOf(type) is { } k ? Loc(k) : Loc("picker.price.plan"),
        PriceKind.Room => Loc("picker.price.room"),
        PriceKind.Unknown => Loc("picker.price.unknown"),
        _ => "",
    };

    /// <summary>Why a card cannot be picked as it is, or what a press does instead (empty for a plain pick).</summary>
    private static string ReasonOf(PickStatus status, string providerLabel, RateLimitSnapshot? rate, string? gguf) => status switch
    {
        PickStatus.NeedsKey => LocF("picker.reason.needsKey", providerLabel),
        PickStatus.NeedsInstall => LocF("picker.reason.needsInstall", providerLabel),
        PickStatus.NeedsSignIn => LocF("picker.reason.needsSignIn", providerLabel),
        PickStatus.NeedsPro => Loc("picker.reason.needsPro"),
        PickStatus.Offline => LocF("picker.reason.offline", providerLabel),
        PickStatus.LoadViaWizard => LocF("picker.reason.loadViaWizard", gguf ?? providerLabel),
        PickStatus.Loading => Loc("picker.reason.loading"),
        PickStatus.ArchUnsupported => Loc("picker.reason.archUnsupported"),
        PickStatus.RateLimited => LocF("picker.reason.rateLimited", rate?.Reset?.ToLocalTime().ToString("HH:mm") ?? "—"),
        _ => "",
    };

    private static string ActionTextOf(PickVerdict v) => v.Action switch
    {
        PickAction.Choose => Loc("picker.action.choose"),
        PickAction.Close => Loc("picker.action.inUse"),
        PickAction.Connect => Loc("picker.action." + Camel(v.Status)),
        _ => "",
    };

    // ─── a pick ───

    /// <summary>What a card does, through the doors the old pickers used.</summary>
    private void ApplyPick(PickerModelCard card)
    {
        // The button is locked while this chat works; a picker left open when a run started must not switch under it.
        if (IsGenerating) { StatusText = Loc("picker.locked"); return; }
        if (card.Gguf != null)
        {
            if (card.Action == PickAction.Choose) SelectedLocalModel = card.Gguf;   // asks about VRAM, then loads
            else if (!RequestConnection(AiProviderType.Local, card.Gguf.Name)) StatusText = card.ReasonText;
            return;
        }
        if (card.Choice == null) return;
        if (card.Status == PickStatus.NeedsSignIn)
        {
            if (!RequestConnection(card.Provider, card.Model)) StatusText = card.ReasonText;
            return;
        }
        if (card.Choice.Provider is AiProviderType.Local or AiProviderType.LlamaServer && card.Choice.NeedsKey)
        {
            if (!RequestConnection(AiProviderType.Local, null)) StatusText = card.ReasonText;
            return;
        }
        SelectedChatModel = card.Choice;   // the old box: switch now (a run carries on), or open the wizard
    }
}
