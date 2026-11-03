using System.Runtime.CompilerServices;
using CluadeX.Models;
using CluadeX.Services.Agents;

namespace CluadeX.ViewModels;

/// <summary>
/// Design mode (owner 2026-11-03: "ถ้าให้ออกแบบ เมื่อออกแบบ ก็พาผู้ใช้ไปหน้า ui studio เพื่อให้เห็นหน้าจอจริง"): a per-chat switch
/// in the composer, or the owner's own words this turn (<see cref="DesignGate"/>). The owner's Send of a design turn tells
/// the model how to work for the Studio (a notice in that turn, not the shared system prompt) and asks for the UI Studio
/// to come forward; while that turn runs, the follow tick does not pull the window to the Code page on every edit.
/// </summary>
public partial class ChatViewModel
{
    private void InitDesign()
    {
        DesignGate.Load(_settingsService.DataRoot);
        DesignGate.Changed += _ => App.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(IsDesignMode)));
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CurrentSession)) OnPropertyChanged(nameof(IsDesignMode)); };
    }

    /// <summary>The composer's Design switch, for the chat on screen.</summary>
    public bool IsDesignMode
    {
        get => DesignGate.IsOn(CurrentSession?.Id);
        set { if (CurrentSession is { } s) DesignGate.Set(s.Id, value); OnPropertyChanged(); }
    }

    /// <summary>The owner sent a design turn in this chat (its id), from the box of the chat on screen: show the UI Studio
    /// once it can show the chat's project. Raised on the UI thread, after the turn's run began.</summary>
    public event Action<string>? DesignViewRequested;

    /// <summary>The chat on screen is in a design turn: its switch is on, or its running turn was decided as one.</summary>
    public bool DesignOnScreen => InDesignTurn(CurrentSession);

    // A turn is decided as a design turn once, when it starts (the owner's Send: DesignGate.TakeOwnerTurn), and the
    // decision rides with the turn — on its run while it works, on its question for a Retry. Nothing reads the owner's
    // words again later: a pipe caller's turn, a retry or a next-step button after a design turn used to find the old
    // words still there and count as design (no Code-page follow, the Studio pulled forward).
    private readonly ConditionalWeakTable<ChatRun, object> _designRuns = new();
    private readonly ConditionalWeakTable<ChatMessage, object> _designAsks = new();
    private static readonly object DesignMark = new();

    /// <summary>The run of <paramref name="turn"/> (just begun for <paramref name="question"/>) is a design turn.</summary>
    private void MarkDesignTurn(TaskCompletionSource turn, ChatMessage question)
    {
        if (_runs.FirstOrDefault(r => ReferenceEquals(r.Done, turn)) is { } run) _designRuns.AddOrUpdate(run, DesignMark);
        _designAsks.AddOrUpdate(question, DesignMark);
    }

    /// <summary>The owner typed <paramref name="note"/> into <paramref name="run"/>: in a design turn it is that turn's
    /// question too — a Retry of the reply after it, or the task a model switch carries on from it, stays a design turn.</summary>
    private void NoteTypedIntoRun(ChatRun run, ChatMessage note)
    {
        if (WasDesignRun(run)) _designAsks.AddOrUpdate(note, DesignMark);
    }

    /// <summary>Was <paramref name="run"/> decided as a design turn? (It may have ended — a leftover note reuses it.)</summary>
    private bool WasDesignRun(ChatRun? run) => run != null && _designRuns.TryGetValue(run, out _);

    /// <summary>Was the turn that answered <paramref name="question"/> a design turn? (A Retry asks it again the same way.)</summary>
    private bool WasDesignAsk(ChatMessage? question) => question != null && _designAsks.TryGetValue(question, out _);

    /// <summary>Is <paramref name="chat"/> in a design turn now: its switch, or its running turn decided as one.</summary>
    private bool InDesignTurn(ChatSession? chat) => chat != null && (DesignGate.IsOn(chat.Id) || WasDesignRun(RunOf(chat)));

    /// <summary>Is <paramref name="chatId"/> the chat on screen?</summary>
    public bool IsChatOnScreen(string? chatId) => chatId != null && CurrentSession?.Id == chatId;

    /// <summary>Does <paramref name="chatId"/> have a run going (on screen or not)? UI thread.</summary>
    public bool IsChatRunning(string? chatId) => chatId != null && _runs.Any(r => r.Session.Id == chatId);

    /// <summary>The run of a design turn just began in <paramref name="session"/>: the row the owner sees, then the request
    /// for the Studio. The model's guidance rides on the turn's own input (see RunTurnAsync).</summary>
    private void BeginDesignTurn(ChatSession session)
    {
        var list = LiveListOf(session.Id);
        list?.Add(new ChatMessage { Role = MessageRole.System, ToolName = ChatMessage.NoticeTag, Content = "🎨 " + Loc("chat.design.notice") });
        DesignViewRequested?.Invoke(session.Id);
    }
}
