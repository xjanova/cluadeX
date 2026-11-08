using CluadeX.Models;
using CluadeX.Services.Agents;
using CluadeX.Services.Providers;

namespace CluadeX.Services;

/// <summary>
/// Changing provider or model in the middle of a run (owner 2026-11-08: "เปลี่ยนโมเดลเจ้าใหม่กลางงานในทุกกรณีให้มันสมูธ
/// ที่สุด … หมดโควต้าก็เปลี่ยนกันได้ทันที โดยที่เราไม่ต้องมาสั่ง"). The decisions are pure (Agents/ProviderFailover.cs); this
/// is where they meet the providers: who is set up, getting one ready, the target a conversation is translated for.
/// </summary>
public partial class CodeAgentService
{
    /// <summary>The provider type behind a provider instance.</summary>
    private AiProviderType TypeOf(IAiProvider p)
    {
        if (Enum.TryParse<AiProviderType>(p.ProviderId, ignoreCase: true, out var t) && ReferenceEquals(_providerManager.GetProvider(t), p))
            return t;
        foreach (var type in Enum.GetValues<AiProviderType>())
            if (ReferenceEquals(_providerManager.GetProvider(type), p)) return type;
        return _providerManager.ActiveProviderType;
    }

    private bool HasKey(IAiProvider p) =>
        _settingsService.Settings.ProviderConfigs.TryGetValue(p.ProviderId, out var cfg) && !string.IsNullOrWhiteSpace(cfg.ApiKey);

    private static bool IsLocalType(AiProviderType t) =>
        t is AiProviderType.Local or AiProviderType.LlamaServer or AiProviderType.Ollama || CompatiblePresets.For(t)?.Local == true;

    /// <summary>Every provider the run could go to, and whether it is set up. A cloud API counts with its key (it is
    /// checked once when picked); a CLI or a model on this PC only when it already answers — nothing is started or
    /// loaded behind the owner's back.</summary>
    internal List<FailoverCandidate> FailoverCandidates()
    {
        var list = new List<FailoverCandidate>();
        foreach (var t in Enum.GetValues<AiProviderType>())
        {
            if (t == AiProviderType.BrainX) continue;
            IAiProvider p;
            try { p = _providerManager.GetProvider(t); } catch { continue; }
            if (p == null || TypeOf(p) != t) continue;
            bool cli = FailoverPolicy.IsCli(t);
            bool local = IsLocalType(t);
            bool ready;
            try
            {
                ready = cli ? p.IsReady
                    : local ? p.IsReady && p.SupportsNativeToolUse
                    : (p.IsReady || HasKey(p)) && p.SupportsNativeToolUse;
            }
            catch { ready = false; }
            list.Add(new FailoverCandidate(t, ModelIdFor(p), ready, cli ? CandidateKind.Cli : local ? CandidateKind.Local : CandidateKind.Api));
        }
        return list;
    }

    /// <summary>Where a run on <paramref name="failed"/> goes next, best first.</summary>
    internal List<FailoverCandidate> FailoverChain(AiProviderType failed, bool nativeOnly = false) =>
        FailoverPolicy.Chain(failed, FailoverCandidates(), _settingsService.Settings.FailoverOrder,
            t => QuotaBook.Shared.IsCooling(t, DateTime.UtcNow), nativeOnly);

    /// <summary>The candidate's provider, ready to answer (checked once, at most 20 s), or null — then it is passed
    /// over for a while (a key that does not work will not work on the next step either).</summary>
    private async Task<IAiProvider?> ReadyProviderAsync(FailoverCandidate c, CancellationToken ct)
    {
        var p = _providerManager.GetProvider(c.Type);
        if (p == null) return null;
        if (!p.IsReady)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            try { await p.InitializeAsync(cts.Token); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _debugLog?.Warn("Failover", $"{c.Type} did not start: {ex.Message}"); }
        }
        if (p.IsReady) return p;
        QuotaBook.Shared.Note(c.Type, ProviderErrorKind.Auth, DateTime.UtcNow);
        _debugLog?.Warn("Failover", $"{c.Type} is not ready — passed over for now");
        return null;
    }

    /// <summary>What the next provider's model can take, for the conversation's translation.</summary>
    private SwitchTarget TargetFor(AiProviderType fromType, string? fromModel, IAiProvider to, AiProviderType toType, int reserveTokens)
    {
        string toModel = ModelIdFor(to);
        var s = _settingsService.Settings;
        int window = ContextMemoryService.ContextWindowFor(toType, toModel, s.ContextSize);
        bool perMessage = toType == AiProviderType.Anthropic
            && AnthropicProvider.PerMessageEffortOk(toModel.Length > 0 ? toModel : AnthropicProvider.DefaultModel, s);
        return new SwitchTarget(fromType, fromModel, toType, toModel, ProviderSeesImages(to), window, perMessage, reserveTokens);
    }

    /// <summary>One line the owner reads when the run changes provider by itself.</summary>
    private static string FailoverLine(bool isThai, ProviderErrorKind why, string fromLabel, string toLabel, bool handoff) => why switch
    {
        ProviderErrorKind.QuotaExhausted => isThai
            ? $"⤳ {fromLabel} หมดโควต้า/เครดิต — สลับไป {toLabel} {(handoff ? "รับงานต่อ" : "ทำต่อจากขั้นเดิม")} อัตโนมัติ"
            : $"⤳ {fromLabel} is out of quota/credit — {toLabel} {(handoff ? "takes the task over" : "carries on from the same step")}",
        ProviderErrorKind.Auth => isThai
            ? $"⤳ {fromLabel} ไม่รับคีย์/การล็อกอิน — สลับไป {toLabel} ทำต่ออัตโนมัติ"
            : $"⤳ {fromLabel} refused the key/sign-in — {toLabel} carries on",
        ProviderErrorKind.None => isThai
            ? $"🔄 สลับไป {toLabel} — ทำต่อจากขั้นเดิม ไม่เริ่มใหม่"
            : $"🔄 Switched to {toLabel} — carrying on from the same step",
        _ => isThai
            ? $"⤳ {fromLabel} ไม่ตอบ (ติด rate limit/ล่ม) — สลับไป {toLabel} ทำต่ออัตโนมัติ"
            : $"⤳ {fromLabel} is not answering (rate-limited or down) — {toLabel} carries on",
    };

    /// <summary>A turn about to start on a provider that ran out a short while ago: the run starts on the next one
    /// instead of spending a request on a refusal. Once the cooldown is over the owner's pick is tried again — the
    /// switch back is automatic. Sets the run's provider (everything in the turn reads it); false = nothing changed.</summary>
    private async Task<bool> RouteAroundCoolingProviderAsync(CancellationToken ct)
    {
        if (!_settingsService.Settings.AutoFailoverOnQuota || AgentRunScope.Current is not { } scope) return false;
        var picked = _providerManager.ActiveProviderType;
        var now = DateTime.UtcNow;
        if (!QuotaBook.Shared.IsCooling(picked, now)) return false;
        var why = QuotaBook.Shared.KindOf(picked, now);
        foreach (var c in FailoverChain(picked))
        {
            var p = await ReadyProviderAsync(c, ct);
            if (p == null) continue;
            string from = ModelLabelFor(_providerManager.ActiveProvider);
            scope.Provider = p;
            scope.ProviderType = c.Type;
            bool isThai = _localizationService.CurrentLanguage == "th";
            string line = isThai
                ? $"⤳ {from} ยังหมดโควต้าอยู่ — เทิร์นนี้ใช้ {ModelLabelFor(p)} (จะกลับไปใช้ตัวที่เลือกไว้เองเมื่อโควต้ากลับมา)"
                : $"⤳ {from} is still out of quota — this turn runs on {ModelLabelFor(p)} (back to your pick once it recovers)";
            OnAgentStatus?.Invoke(line);
            OnAgenticStreamingToken?.Invoke("> " + line + "\n\n", 1);
            _debugLog?.Info("Failover", $"turn start: {picked} cooling ({why}) — routed to {c.Type}");
            return true;
        }
        return false;
    }
}
