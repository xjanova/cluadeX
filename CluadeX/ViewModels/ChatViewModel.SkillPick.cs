using CluadeX.Models;
using CluadeX.Services.Skills;

namespace CluadeX.ViewModels;

/// <summary>
/// Smart skill picking in the chat (owner 2026-11-07: "ระบบ เลือกเปิดสกิล อัตโนมัติที่สอดคล้องกับงาน ควรมี ให้เลือกปิดได้ในช่องแชท").
/// The composer's switch turns it on or off for the chat on screen (<see cref="SkillPickGate"/>); before each turn's model
/// starts, the chat shows which skills the turn was given and what that saved against listing them all.
/// </summary>
public partial class ChatViewModel
{
    private readonly Dictionary<string, SkillPick> _lastSkillPick = new(StringComparer.Ordinal);

    private void InitSkillPick()
    {
        SkillPickGate.Load(_settingsService.DataRoot);
        SkillPickGate.Changed += _ => App.Current?.Dispatcher.BeginInvoke(RaiseSkillPickChanged);
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CurrentSession)) RaiseSkillPickChanged(); };
    }

    private void RaiseSkillPickChanged()
    {
        OnPropertyChanged(nameof(IsSkillAutoPick));
        OnPropertyChanged(nameof(SkillPickTip));
    }

    /// <summary>The composer's Skill switch, for the chat on screen.</summary>
    public bool IsSkillAutoPick
    {
        get => SkillPickGate.IsOn(CurrentSession?.Id, _settingsService.Settings.SkillAutoPickEnabled);
        set
        {
            if (CurrentSession is { } s) SkillPickGate.Set(s.Id, value, _settingsService.Settings.SkillAutoPickEnabled);
            RaiseSkillPickChanged();
        }
    }

    /// <summary>The switch's tooltip: what it does, and what the last turn of this chat was given.</summary>
    public string SkillPickTip
    {
        get
        {
            string tip = Loc("chat.skillPickTip");
            if (CurrentSession is { } s && _lastSkillPick.TryGetValue(s.Id, out var last))
                tip += "\n\n" + LocF("chat.skillPick.last", PickSummary(last));
            return tip;
        }
    }

    /// <summary>"security-scan (words match), commit (you named it)" — or "none fitted (saved ≈ 1,240 tokens)".</summary>
    private static string PickSummary(SkillPick pick) =>
        pick.Picked.Count == 0
            ? LocF("chat.skillPick.none", pick.TokensAll.ToString("N0"))
            : string.Join(", ", pick.Picked.Select(p => $"{p.Skill.Name} ({Loc("skill.why." + p.Reason.ToString().ToLowerInvariant())})"));

    /// <summary>The turn's skills were picked (any thread): the chat says which, before the model's first word.</summary>
    private void OnSkillsPicked(ChatRun run, SkillPick pick)
    {
        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            _lastSkillPick[run.Session.Id] = pick;
            if (ReferenceEquals(run.Session, CurrentSession)) OnPropertyChanged(nameof(SkillPickTip));
            if (pick.Picked.Count == 0) return;   // nothing fitted: no row (the tooltip still says so)
            var row = new ChatMessage
            {
                Role = MessageRole.System,
                Content = "✦ " + LocF("chat.skillPick.row", PickSummary(pick), pick.Picked.Count, pick.Offered,
                    pick.TokensPicked.ToString("N0"), pick.TokensAll.ToString("N0")),
            };
            // Right under the owner's message: the reply's live row may already be below it.
            int at = -1;
            for (int i = run.Messages.Count - 1; i >= 0; i--)
                if (run.Messages[i].Role == MessageRole.User) { at = i; break; }
            if (at >= 0) run.Messages.Insert(at + 1, row); else run.Messages.Add(row);
        });
    }
}
