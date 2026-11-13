using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;

namespace CluadeX.ViewModels;

/// <summary>
/// The model CluadeX switched to by itself. Owner 2026-11-13: "การสลับโมเดลอัตโนมัติ ควรเปลี่ยนโมเดลในช่องกรอกแชท
/// ด้านล่างให้ตรงด้วยและขึ้นป้ายไว้ว่ากำลังใช้โมเดลที่เปลี่ยนมา". Before this a chat whose pick ran out (a CLI login out
/// of its usage limit, an API out of credit) went on with another provider while the composer still named the pick:
/// five chats ran on a pay-per-token API for hours under a button that said the plan-priced one.
/// <para>The run records the switch on its scope (<see cref="AgentRunScope.AutoSwitch"/>). The model button then names
/// the stand-in, with her face and colour, and a badge beside it says what it stands in for. After the run the badge
/// stays for as long as the pick is still passed over (<see cref="QuotaBook"/>): the next turn starts on the stand-in
/// again. Once the pick is back, or the owner picks a model, it goes away.</para>
/// </summary>
public partial class ChatViewModel
{
    /// <summary>The newest automatic switch of any chat: what an idle chat's next turn will start on while that pick
    /// is still cooling (the pick is one for the whole app).</summary>
    private AutoSwitchInfo? _lastAutoSwitch;
    private DispatcherTimer? _autoSwitchExpiry;

    /// <summary>Called from InitControls (the constructor).</summary>
    private void InitAutoSwitch()
    {
        AgentRunScope.AutoSwitchChanged += scope => App.Current?.Dispatcher.BeginInvoke(() => OnAutoSwitchChanged(scope));
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CurrentSession) or nameof(IsGenerating) or nameof(SelectedChatModel)) RaiseAutoSwitch();
        };
    }

    private void OnAutoSwitchChanged(AgentRunScope scope)
    {
        if (scope.Parent != null) return;   // a sub-agent's own hop is not the chat's
        // Cleared as well as set: a run back on the pick, or one the owner gave a model, means the pick (one for the
        // whole app) is no longer stood in for — an idle chat must not go on showing the badge until the cooldown ends.
        _lastAutoSwitch = scope.AutoSwitch;
        RaiseAutoSwitch();
    }

    /// <summary>The stand-in the chat on screen runs on now, or will start its next turn on. Null = on the owner's pick.</summary>
    private AutoSwitchInfo? ShownAutoSwitch
    {
        get
        {
            if (CurrentSession is not { } session) return null;
            if (RunOf(session) is { } run) return run.Scope?.AutoSwitch;   // a run on its pick shows none
            if (_lastAutoSwitch is not { } last) return null;
            if (last.From != _providerManager.PickedProviderType || !QuotaBook.Shared.IsCooling(last.From, DateTime.UtcNow))
            {
                _lastAutoSwitch = null;   // the pick is back, or the owner picked another one
                return null;
            }
            return last;
        }
    }

    /// <summary>The composer shows the badge.</summary>
    public bool HasAutoSwitch => ShownAutoSwitch != null;

    /// <summary>"⤳ สลับอัตโนมัติ · แทน GPT-6.1 Sol".</summary>
    public string AutoSwitchBadge => ShownAutoSwitch is { } sw ? LocF("chat.autoSwitch.badge", ShortModel(sw.FromLabel, "")) : "";

    /// <summary>Why, since when, when the pick is tried again, and whether the stand-in is paid per token.</summary>
    public string AutoSwitchTip
    {
        get
        {
            if (ShownAutoSwitch is not { } sw) return "";
            string tip = LocF("chat.autoSwitch.tip", sw.FromLabel, sw.ToLabel, sw.At.ToString("HH:mm"));
            if (QuotaBook.Shared.Until(sw.From, DateTime.UtcNow) is { } until)
                tip += "\n" + LocF("chat.autoSwitch.back", until.ToLocalTime().ToString("HH:mm"));
            if (IsPaidPerToken(sw.To)) tip += "\n" + LocF("chat.autoSwitch.paid", sw.ToLabel);
            return tip + "\n" + Loc("chat.autoSwitch.pick");
        }
    }

    /// <summary>A cloud API bills each token; a CLI login runs on its plan and a model on this PC costs nothing.</summary>
    private static bool IsPaidPerToken(AiProviderType t) =>
        !FailoverPolicy.IsCli(t) && t is not (AiProviderType.Local or AiProviderType.LlamaServer or AiProviderType.Ollama or AiProviderType.BrainX);

    /// <summary>The model of a "Provider · model-id" label as people say it; a label with no model keeps its name.</summary>
    private static string ShortModel(string label, string modelId)
    {
        if (modelId.Length > 0 && modelId is not ("default" or "auto")) return LiveModelReport.FriendlyModel(modelId);
        int dot = label.IndexOf(" · ", StringComparison.Ordinal);
        if (dot < 0) return label;
        string model = label[(dot + 3)..].Trim();
        return model.Length == 0 || model is "default" or "auto" ? label[..dot].Trim() : LiveModelReport.FriendlyModel(model);
    }

    /// <summary>The stand-in's name for the model button: the model its last response named, else the one configured.</summary>
    private string AutoSwitchModelText(AutoSwitchInfo sw)
    {
        var snap = LiveSnapshot();
        if (snap is { Model.Length: > 0 } && snap.Provider == sw.To) return LiveModelReport.FriendlyModel(snap.Model);
        return ShortModel(sw.ToLabel, sw.ToModel);
    }

    /// <summary>"$2 / $10" for a stand-in that is a priced API model, "" otherwise.</summary>
    private static string AutoSwitchPrice(AutoSwitchInfo sw) =>
        IsPaidPerToken(sw.To) && sw.ToModel.Length > 0 && CostTrackingService.HasRegisteredPricing(sw.ToModel) ? PriceLabelOf(sw.ToModel) : "";

    private void RaiseAutoSwitch()
    {
        OnPropertyChanged(nameof(HasAutoSwitch));
        OnPropertyChanged(nameof(AutoSwitchBadge));
        OnPropertyChanged(nameof(AutoSwitchTip));
        OnPropertyChanged(nameof(ModelButtonText));
        OnPropertyChanged(nameof(HasModelButtonText));
        OnPropertyChanged(nameof(ShowModelPlaceholder));
        OnPropertyChanged(nameof(ModelButtonFace));
        OnPropertyChanged(nameof(ModelButtonGlow));
        OnPropertyChanged(nameof(ModelButtonPrice));
        OnPropertyChanged(nameof(LiveModelEffortText));
        OnPropertyChanged(nameof(LiveEffortSuffix));
        OnPropertyChanged(nameof(LiveEffortLevel));
        OnPropertyChanged(nameof(LiveModelEffortTip));
        ScheduleAutoSwitchExpiry();
    }

    /// <summary>An idle chat's badge goes away by itself when the pick's cooldown is over (nothing else raises then).</summary>
    private void ScheduleAutoSwitchExpiry()
    {
        _autoSwitchExpiry?.Stop();
        if (_lastAutoSwitch is not { } sw || QuotaBook.Shared.Until(sw.From, DateTime.UtcNow) is not { } until) return;
        var wait = until - DateTime.UtcNow + TimeSpan.FromSeconds(1);
        if (wait <= TimeSpan.Zero) return;
        _autoSwitchExpiry ??= new DispatcherTimer();
        _autoSwitchExpiry.Interval = wait;
        _autoSwitchExpiry.Tick -= OnAutoSwitchExpiry;
        _autoSwitchExpiry.Tick += OnAutoSwitchExpiry;
        _autoSwitchExpiry.Start();
    }

    private void OnAutoSwitchExpiry(object? sender, EventArgs e)
    {
        _autoSwitchExpiry?.Stop();
        RaiseAutoSwitch();
    }
}
