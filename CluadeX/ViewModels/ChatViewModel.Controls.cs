using System.Collections.ObjectModel;
using System.ComponentModel;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace CluadeX.ViewModels;

/// <summary>
/// The chat page's control strip, next to the input box instead of in Settings: which model answers, what the agent
/// may do without asking, how much a run may spend before it asks — and what it is costing, this run and today.
/// </summary>
public partial class ChatViewModel
{
    private ActivationService? _activation;

    // ─── Model ───

    /// <summary>One pick in the model box: a model of an API provider that has a key, with its list price.</summary>
    public sealed record ChatModelChoice(AiProviderType Provider, string Model, string ProviderLabel, string PriceLabel);

    /// <summary>"$2 / $10" — input / output per million tokens.</summary>
    private static string PriceLabelOf(string model)
    {
        var p = CostTrackingService.PricingOf(model);
        return $"${p.InputPerMToken:0.##} / ${p.OutputPerMToken:0.##}";
    }

    public ObservableCollection<ChatModelChoice> ChatModelChoices { get; } = new();
    public bool HasChatModelChoices => ChatModelChoices.Count > 0;

    private ChatModelChoice? _selectedChatModel;
    private bool _syncingModelChoice;

    /// <summary>The model answering this chat (null while a model on this PC answers). Setting it switches the model —
    /// a run in progress uses it from its next step — and, between runs, the provider.</summary>
    public ChatModelChoice? SelectedChatModel
    {
        get => _selectedChatModel;
        set
        {
            if (_syncingModelChoice || value == null || Equals(value, _selectedChatModel)) return;
            _ = SwitchChatModelAsync(value);
        }
    }

    private static IEnumerable<string> KnownModelsOf(AiProviderType type) => type switch
    {
        AiProviderType.Anthropic => AnthropicProvider.KnownModels,
        AiProviderType.OpenAI => OpenAiProvider.KnownModels,
        AiProviderType.Gemini => GeminiProvider.KnownModels,
        _ => [],
    };

    private static string DefaultModelOf(AiProviderType type) => type switch
    {
        AiProviderType.Anthropic => AnthropicProvider.DefaultModel,
        AiProviderType.OpenAI => OpenAiProvider.DefaultModel,
        AiProviderType.Gemini => GeminiProvider.DefaultModel,
        _ => "",
    };

    /// <summary>Rebuild the model box from the providers that have a key, and select what answers now. Only touches the
    /// list when it changed, so an open drop-down isn't reset every refresh.</summary>
    private void RefreshChatModelChoices()
    {
        var s = _settingsService.Settings;
        var list = new List<ChatModelChoice>();
        foreach (var type in new[] { AiProviderType.Anthropic, AiProviderType.OpenAI, AiProviderType.Gemini })
        {
            if (!s.ProviderConfigs.TryGetValue(type.ToString(), out var cfg) || string.IsNullOrWhiteSpace(cfg.ApiKey)) continue;
            var models = KnownModelsOf(type).ToList();
            string? saved = cfg.EffectiveModelId?.Trim();
            if (!string.IsNullOrEmpty(saved) && !models.Contains(saved)) models.Insert(0, saved);
            string label = _providerManager.GetProvider(type).DisplayName;
            list.AddRange(models.Select(m => new ChatModelChoice(type, m, label, PriceLabelOf(m))));
        }

        _syncingModelChoice = true;
        try
        {
            if (!list.SequenceEqual(ChatModelChoices))
            {
                ChatModelChoices.Clear();
                foreach (var c in list) ChatModelChoices.Add(c);
                OnPropertyChanged(nameof(HasChatModelChoices));
            }
            var active = _providerManager.ActiveProviderType;
            string activeModel = s.ProviderConfigs.TryGetValue(active.ToString(), out var ac) && !string.IsNullOrWhiteSpace(ac.EffectiveModelId)
                ? ac.EffectiveModelId!.Trim() : DefaultModelOf(active);
            var sel = ChatModelChoices.FirstOrDefault(c => c.Provider == active && c.Model == activeModel);
            if (!Equals(sel, _selectedChatModel))
            {
                _selectedChatModel = sel;
                OnPropertyChanged(nameof(SelectedChatModel));
            }
        }
        finally { _syncingModelChoice = false; }
        RefreshChatModelPlaceholder();
    }

    private async Task SwitchChatModelAsync(ChatModelChoice c)
    {
        try
        {
            bool otherProvider = _providerManager.ActiveProviderType != c.Provider;
            if (otherProvider)
            {
                // A run holds its provider for the whole loop; the model of the same provider can change mid-run.
                if (_runs.Count > 0) { StatusText = Loc("chat.model.providerBusy"); return; }
                // Cloud providers are the paid tier — the same gate as Settings ▸ AI Provider.
                if (_activation?.IsActivated != true) { StatusText = Loc("settings.activationRequired"); return; }
            }
            string key = c.Provider.ToString();
            _settingsService.UpdateSettings(s =>
            {
                if (!s.ProviderConfigs.TryGetValue(key, out var cfg)) s.ProviderConfigs[key] = cfg = new ProviderConfig();
                cfg.SelectedModel = c.Model;
                cfg.UseCustomModel = false;
            });
            if (otherProvider) await _providerManager.SwitchProviderAsync(c.Provider);
            StatusText = LocF("chat.model.switched", c.Model);
        }
        catch (Exception ex)
        {
            _debugLog?.Warn("Chat", $"switching the chat's model to {c.Provider}/{c.Model} failed", ex);
            StatusText = Loc("chat.model.switchFailed");
        }
        finally
        {
            RefreshUsageStrip();   // shows what really answers now (the old pick when the switch was refused)
        }
    }

    /// <summary>
    /// Another app picked this chat's model and/or effort — BrainX's cowork room, over the cluadex-mcp pipe. The model
    /// box's own rules: only a model the box lists (API models with a key — never a model on this PC, which would load
    /// weights into VRAM with nobody here to ask), a provider change waits for running replies, cloud needs Pro.
    /// Returns null when done, else why not, in the user's language.
    /// </summary>
    public async Task<string?> ChooseFromOutsideAsync(string? model, string? effort)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            RefreshChatModelChoices();
            var choice = ChatModelChoices.FirstOrDefault(c => c.Model == model.Trim());
            if (choice == null) return LocF("chat.model.notOffered", model.Trim());
            if (!Equals(choice, _selectedChatModel))
            {
                await SwitchChatModelAsync(choice);
                RefreshChatModelChoices();
                if (!Equals(_selectedChatModel, choice)) return StatusText;   // the refusal the box itself showed
            }
        }
        if (!string.IsNullOrWhiteSpace(effort))
        {
            string e = effort.Trim();
            if (!EffortOptions.Contains(e)) return LocF("chat.effort.notOffered", e);
            ReasoningEffort = e;
        }
        return null;
    }

    // ─── Approvals ───

    /// <summary>A pick in one of the strip's boxes. The same objects for the app's life — only the label follows the
    /// language — so a language switch doesn't swap the ItemsSource and leave the box blank.</summary>
    public sealed class StripChoice : ViewModelBase
    {
        private readonly string? _locKey;
        private readonly string? _fixedLabel;
        public StripChoice(object value, string? locKey, string? fixedLabel = null) { Value = value; _locKey = locKey; _fixedLabel = fixedLabel; }
        public object Value { get; }
        public string Key => Value as string ?? "";
        public decimal Usd => Value is decimal d ? d : 0m;
        public string Label => _locKey != null ? LocalizedResources.Instance[_locKey] : _fixedLabel ?? "";
        public void Relabel() => OnPropertyChanged(nameof(Label));
    }

    public IReadOnlyList<StripChoice> ApprovalChoices { get; } =
    [
        new("ask", "chat.approval.ask"),
        new("edits", "chat.approval.edits"),
        new("all", "chat.approval.all"),
    ];

    /// <summary>Chats (session ids) set to "allow everything". In memory only: a restart asks again.</summary>
    private readonly HashSet<string> _allowAllChats = new();
    private readonly object _allowAllLock = new();

    private bool AllowsAll(ChatSession? s)
    {
        if (s == null) return false;
        lock (_allowAllLock) return _allowAllChats.Contains(s.Id);
    }

    /// <summary>"ask" — every action that isn't free asks; "edits" — file edits in the project run without a prompt
    /// (Accept edits); "all" — nothing asks in this chat except the user's own Ask rules and code run in their browser
    /// (Deny rules still deny).</summary>
    public string ApprovalMode
    {
        get => AllowsAll(CurrentSession) ? "all" : AcceptEdits ? "edits" : "ask";
        set
        {
            if (value == null || value == ApprovalMode) return;
            var session = CurrentSession;
            switch (value)
            {
                case "ask":
                    SetAllowAll(session, false);
                    if (AcceptEdits) AcceptEdits = false;
                    break;
                case "edits":
                    SetAllowAll(session, false);
                    if (!AcceptEdits) AcceptEdits = true;   // confirms, and refuses a drive / user / system folder
                    break;
                case "all":
                    if (session != null && Confirm(Loc("chat.approval.allConfirm"), Loc("chat.approval.allConfirmTitle")))
                    {
                        SetAllowAll(session, true);
                        Messages.Add(new ChatMessage { Role = MessageRole.System, Content = "🔓 " + Loc("chat.approval.allOn") });
                        ApprovePendingPrompts(RunOf(session)?.Messages ?? Messages);
                        ScrollToBottom?.Invoke();
                    }
                    break;
            }
            // The box already shows the pick: re-raise after the binding update so a refused pick snaps back.
            App.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(ApprovalMode)));
        }
    }

    private void SetAllowAll(ChatSession? s, bool on)
    {
        if (s == null) return;
        lock (_allowAllLock)
        {
            if (on) _allowAllChats.Add(s.Id);
            else _allowAllChats.Remove(s.Id);
        }
    }

    /// <summary>Switching to "allow everything" answers the prompts already waiting in that chat — except the ones that
    /// always ask.</summary>
    private static void ApprovePendingPrompts(IEnumerable<ChatMessage> messages)
    {
        foreach (var m in messages.Where(m => m.Role == MessageRole.PermissionRequest && !m.PermissionAnswered && !m.PermissionAlwaysAsk).ToList())
            m.ResolvePermission(PermissionOutcome.Allowed);
    }

    /// <summary>The permission handler's shortcut: true when the asking run's chat allows everything and this request
    /// is not one that always asks.</summary>
    private bool AutoApproves(PermissionRequestInfo info, ChatSession? askSession) => !info.AlwaysAsk && AllowsAll(askSession);

    // ─── Spend ───

    public IReadOnlyList<StripChoice> BudgetChoices { get; } = new[] { 0.5m, 1m, 2m, 5m, 10m, 20m }
        .Select(v => new StripChoice(v, null, "$" + v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)))
        .Append(new StripChoice(0m, "chat.budget.none"))
        .ToList();

    /// <summary>Settings ▸ RunBudgetUsd from the chat page: a paid run asks before spending more than this (0 = never).</summary>
    public decimal RunBudgetUsd
    {
        get => _settingsService.Settings.RunBudgetUsd;
        set
        {
            if (value == RunBudgetUsd || value < 0) return;
            _settingsService.UpdateSettings(s => s.RunBudgetUsd = value);
            OnPropertyChanged();
            RefreshSpend();
        }
    }

    private string _runCostText = "", _todayCostText = "";
    /// <summary>"This run $0.42 / $2" for the chat on screen while it runs; empty otherwise.</summary>
    public string RunCostText { get => _runCostText; private set => SetProperty(ref _runCostText, value); }
    /// <summary>"Today $3.10" — every CluadeX session today, kept across restarts.</summary>
    public string TodayCostText { get => _todayCostText; private set => SetProperty(ref _todayCostText, value); }

    /// <summary>Dollars for the strip: cents below a dollar would read "$0.00" for a cheap step, so small sums keep 3 places.</summary>
    private static string Usd(decimal v) => v < 0.01m && v > 0 ? v.ToString("0.000") : v.ToString("0.00");

    // ─── Daily budget (title bar): today's spend against a daily cap ───

    public IReadOnlyList<StripChoice> DailyBudgetChoices { get; } = new[] { 5m, 10m, 20m, 50m, 100m }
        .Select(v => new StripChoice(v, null, "$" + v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)))
        .Append(new StripChoice(0m, "chat.budget.none"))
        .ToList();

    /// <summary>Settings ▸ DailyBudgetUsd: a paid run asks before going on once today's spend reaches it (0 = never).</summary>
    public decimal DailyBudgetUsd
    {
        get => _settingsService.Settings.DailyBudgetUsd;
        set
        {
            if (value == DailyBudgetUsd || value < 0) return;
            _settingsService.UpdateSettings(s => s.DailyBudgetUsd = value);
            OnPropertyChanged();
            RefreshSpend();
        }
    }

    private string _todayBudgetText = "", _todayBudgetPercent = "", _filesChangedText = "", _contextWindowText = "";
    private double _todayBudgetFraction;
    /// <summary>"วันนี้ $3.40 / $10" (with a daily cap) or "วันนี้ $3.40".</summary>
    public string TodayBudgetText { get => _todayBudgetText; private set => SetProperty(ref _todayBudgetText, value); }
    /// <summary>0–1 of the daily cap used (0 without a cap).</summary>
    public double TodayBudgetFraction { get => _todayBudgetFraction; private set => SetProperty(ref _todayBudgetFraction, value); }
    public string TodayBudgetPercent { get => _todayBudgetPercent; private set => SetProperty(ref _todayBudgetPercent, value); }
    public bool HasDailyBudget => DailyBudgetUsd > 0;
    /// <summary>"แก้ 3 ไฟล์" — files this chat's agent wrote or edited (status bar).</summary>
    public string FilesChangedText { get => _filesChangedText; private set => SetProperty(ref _filesChangedText, value); }
    /// <summary>"200K context" — the window of the model answering (status bar).</summary>
    public string ContextWindowText { get => _contextWindowText; private set => SetProperty(ref _contextWindowText, value); }

    private void RefreshStatusBarFacts()
    {
        int files = Messages.Where(m => m.Role == MessageRole.ToolAction && m.ToolSuccess
                                        && (m.ToolKind is "edit_file" or "multi_edit" or "write_file" || m.HasDiff)
                                        && !string.IsNullOrEmpty(m.ToolTarget))
                            .Select(m => m.ToolTarget!).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        FilesChangedText = files > 0 ? LocF("chat.files.changed", files) : "";
        int max = _contextMemoryService.MaxContextTokens;
        ContextWindowText = max >= 1000 ? LocF("chat.ctx.window", (max / 1000).ToString("N0")) : "";
    }

    private void RefreshSpend()
    {
        decimal per = _settingsService.Settings.RunBudgetUsd;
        if (RunOf(CurrentSession)?.Scope is { } scope)
        {
            int steps = scope.Requests;
            string avg = Usd(steps > 0 ? scope.CostUsd / steps : 0);
            RunCostText = per > 0
                ? LocF("chat.spend.runOf", Usd(scope.CostUsd), (per + scope.BudgetGrantedUsd).ToString("0.##"), steps, avg)
                : LocF("chat.spend.run", Usd(scope.CostUsd), steps, avg);
        }
        else
            RunCostText = "";
        decimal today = _costTracker.TodayCostUsd;
        TodayCostText = today > 0 ? LocF("chat.spend.today", today.ToString("0.00")) : "";
        decimal daily = _settingsService.Settings.DailyBudgetUsd;
        TodayBudgetText = daily > 0
            ? LocF("chat.today.of", today.ToString("0.00"), daily.ToString("0.##"))
            : LocF("chat.spend.today", today.ToString("0.00"));
        TodayBudgetFraction = daily > 0 ? (double)Math.Min(1m, today / daily) : 0;
        TodayBudgetPercent = daily > 0 ? $"{Math.Min(999m, today / daily * 100m):0}%" : "";
        OnPropertyChanged(nameof(HasDailyBudget));
        if (!IsGenerating && _lastEstimate != null) ApplyEstimateText();
        else if (IsGenerating) { EstimateText = ""; }
    }

    // ─── Estimate (before a run): what a step should cost with this model, prompt and effort ───

    private string _estimateText = "", _estimateTip = "";
    /// <summary>"≈ $0.03–0.08/step · $10 ≈ 120–330 steps" next to the budget box while nothing runs.</summary>
    public string EstimateText { get => _estimateText; private set => SetProperty(ref _estimateText, value); }
    private string _estimateShortText = "";
    /// <summary>"≈ $0.03/ขั้น" — the composer's pill (the tooltip has the range, the budget's steps and the arithmetic).</summary>
    public string EstimateShortText { get => _estimateShortText; private set => SetProperty(ref _estimateShortText, value); }
    /// <summary>How the estimate was worked out: prices, prefix size, cache, answer size.</summary>
    public string EstimateTip { get => _estimateTip; private set => SetProperty(ref _estimateTip, value); }

    private StepCostEstimate? _lastEstimate;
    private (string Model, int Deferred, bool Measured) _lastEstimateBasis;
    private int _estimateVersion;
    /// <summary>Measured by this session's last paid run (0 = not yet): output tokens and new content per step.</summary>
    private int _measuredOutPerStep, _measuredGrowthPerStep;

    /// <summary>The model a paid provider answers with now, or null (a model on this PC, Claude Code's own plan).</summary>
    private string? PaidModelNow()
    {
        var type = _providerManager.ActiveProviderType;
        if (type is not (AiProviderType.Anthropic or AiProviderType.OpenAI or AiProviderType.Gemini)) return null;
        var s = _settingsService.Settings;
        return s.ProviderConfigs.TryGetValue(type.ToString(), out var cfg) && !string.IsNullOrWhiteSpace(cfg.EffectiveModelId)
            ? cfg.EffectiveModelId!.Trim() : DefaultModelOf(type);
    }

    /// <summary>Re-measure the prompt and re-price a step (debounced; the prompt build runs off the UI thread).</summary>
    private async void RefreshEstimate()
    {
        int v = ++_estimateVersion;
        try
        {
            await Task.Delay(400);
            if (v != _estimateVersion) return;
            string? model = PaidModelNow();
            if (model == null) { _lastEstimate = null; EstimateText = EstimateTip = ""; return; }
            long historyChars = Messages
                .Where(m => m.Role is MessageRole.User or MessageRole.Assistant or MessageRole.ToolAction)
                .Sum(m => (long)(m.Content?.Length ?? 0) + Math.Min(m.ToolOutput?.Length ?? 0, 4000));
            var (sysChars, toolChars, deferred) = await Task.Run(() => _agentService.MeasurePromptAsync());
            if (v != _estimateVersion) return;
            var s = _settingsService.Settings;
            bool measured = _measuredOutPerStep > 0;
            int outPerStep = measured ? _measuredOutPerStep : CostEstimate.DefaultOutputPerStep(s.ReasoningEffort, s.ExtendedThinkingEnabled);
            int growth = _measuredGrowthPerStep > 0 ? _measuredGrowthPerStep : CostEstimate.DefaultGrowthPerStep;
            _lastEstimate = CostEstimate.Estimate(model, CostEstimate.TokensOf(sysChars + toolChars), CostEstimate.TokensOf(historyChars),
                outPerStep, growth, s.PromptCachingEnabled);
            _lastEstimateBasis = (model, deferred, measured);
            ApplyEstimateText();
        }
        catch (Exception ex)
        {
            _debugLog?.Warn("Chat", "cost estimate failed", ex);
            EstimateText = EstimateTip = "";
        }
    }

    private void ApplyEstimateText()
    {
        if (_lastEstimate is not { } e || IsGenerating) { EstimateText = EstimateShortText = ""; return; }
        var (model, deferred, measured) = _lastEstimateBasis;
        decimal budget = _settingsService.Settings.RunBudgetUsd;
        var (fewest, most) = e.StepsFor(budget);
        EstimateText = budget > 0
            ? LocF("chat.estimate.text", Usd(e.LaterLow), Usd(e.LaterHigh), budget.ToString("0.##"), fewest, most)
            : LocF("chat.estimate.textNoBudget", Usd(e.LaterLow), Usd(e.LaterHigh));
        var p = CostTrackingService.PricingOf(model);
        EstimateShortText = LocF("chat.estimate.short", Usd(e.LaterLow));
        EstimateTip = EstimateText + "\n\n" + LocF("chat.estimate.tip", model, p.InputPerMToken.ToString("0.##"), p.OutputPerMToken.ToString("0.##"),
            p.CacheReadPerMToken.ToString("0.###"), Usd(e.FirstStep), e.PrefixTokens,
            deferred > 0 ? LocF("chat.estimate.deferred", deferred) : "", e.HistoryTokens,
            Usd(e.LaterLow), Usd(e.LaterHigh), e.GrowthTokensPerStep, e.OutputTokensPerStep,
            measured ? Loc("chat.estimate.outMeasured") : LocF("chat.estimate.outEffort", _settingsService.Settings.ReasoningEffort));
    }

    /// <summary>A paid run ended: one line in its chat with what it cost (never sent to the model — system lines aren't),
    /// and its real output / growth per step feed the next estimate.</summary>
    private void PostRunCostSummary(ChatRun run)
    {
        var sc = run.Scope;
        if (sc == null || sc.Requests == 0 || sc.CostUsd <= 0) return;
        run.Messages.Add(new ChatMessage
        {
            Role = MessageRole.System,
            Content = LocF("chat.spend.summary", Usd(sc.CostUsd), sc.Requests, Usd(sc.CostUsd / sc.Requests), sc.CacheHitShare * 100, sc.OutputTokens),
        });
        _measuredOutPerStep = (int)Math.Clamp(sc.OutputTokens / sc.Requests, 100, 32000);
        if (sc.Requests > 1)
            _measuredGrowthPerStep = (int)Math.Clamp(sc.CacheWriteTokens / sc.Requests, 300, 50000);
    }

    /// <summary>The language changed: relabel the picks and re-word the spend line.</summary>
    private void RelabelControls()
    {
        foreach (var c in ApprovalChoices.Concat(BudgetChoices).Concat(DailyBudgetChoices)) c.Relabel();
        RefreshStatusBarFacts();
        RefreshSpend();
        RefreshChatModelPlaceholder();
        ApplyEstimateText();
    }

    private string _chatModelPlaceholder = "";
    /// <summary>Shown over the model box when no cloud model answers (a model on this PC does).</summary>
    public string ChatModelPlaceholder { get => _chatModelPlaceholder; private set => SetProperty(ref _chatModelPlaceholder, value); }

    private void RefreshChatModelPlaceholder() =>
        ChatModelPlaceholder = _selectedChatModel == null && ChatModelChoices.Count > 0 ? Loc("chat.model.localPlaceholder") : "";

    /// <summary>Called from the constructor.</summary>
    private void InitControls(ActivationService? activation)
    {
        _activation = activation;
        LocalizedResources.Instance.PropertyChanged += (_, _) => App.Current?.Dispatcher.BeginInvoke(RelabelControls);
        PropertyChanged += OnControlsSourceChanged;
        _costTracker.OnCostUpdated += _ => App.Current?.Dispatcher.BeginInvoke(RefreshSpend);
        _settingsService.SettingsChanged += () => App.Current?.Dispatcher.BeginInvoke(() =>
        {
            RefreshChatModelChoices();
            OnPropertyChanged(nameof(RunBudgetUsd));
            RefreshEstimate();   // model, provider, effort or thinking may have changed
        });
        _fileSystemService.OnWorkingDirectoryChanged += () => App.Current?.Dispatcher.BeginInvoke(RefreshEstimate);
        RefreshChatModelChoices();
        RefreshSpend();
        RefreshEstimate();
    }

    private void OnControlsSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AcceptEdits) or nameof(CurrentSession))
            OnPropertyChanged(nameof(ApprovalMode));
        if (e.PropertyName is nameof(CurrentSession) or nameof(IsGenerating))
            RefreshSpend();
        if (e.PropertyName is nameof(CurrentSession) || (e.PropertyName == nameof(IsGenerating) && !IsGenerating))
            RefreshEstimate();
    }
}
