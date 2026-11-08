using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// The sidebar row's run badge after a turn (failed / stopped / unread result), the runs CluadeX did not see end (shown
/// as stopped after a restart), and the mood of น้อง CluadeX in the activity bar.
/// </summary>
public partial class ChatViewModel
{
    private LiveRunLedger? _liveRuns;

    /// <summary>How the latest turn (any chat) ended, and when; and the last time anything ran — the buddy's clock.</summary>
    private string? _lastEndBadge;
    private DateTime _lastEndUtc = DateTime.MinValue;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    private string _buddyMood = BuddyMood.Idle;
    /// <summary>What the activity bar's face does now (a <see cref="Services.BuddyMood"/> value).</summary>
    public string BuddyMoodState
    {
        get => _buddyMood;
        private set { if (_buddyMood != value) { _buddyMood = value; OnPropertyChanged(); OnPropertyChanged(nameof(BuddyMoodTip)); OnPropertyChanged(nameof(BuddyTip)); } }
    }

    /// <summary>What she is up to, in the UI language.</summary>
    public string BuddyMoodTip => Loc("buddy.mood." + _buddyMood);

    /// <summary>Her tooltip in the activity bar: her mood, then what a click does.</summary>
    public string BuddyTip => BuddyMoodTip + "\n" + Loc("buddy.goHome");

    private System.Windows.Input.ICommand? _goHomeCommand;
    /// <summary>Clicking her (owner 2026-11-08: "กดที่ไอค่อนแมว หรือ หน้า น้อง cluadex ควรเป็นการกลับไปหน้า home ตอนรอเปิดโปรเจค"):
    /// the same path as the title bar's New Chat — the Chat page with an empty chat and no folder open, where the starters
    /// ask which project. A chat that is running carries on in the background (NewSession leaves its run alone); an
    /// untouched chat on screen is reused, so clicking her twice stacks nothing.</summary>
    public System.Windows.Input.ICommand GoHomeCommand => _goHomeCommand ??= new RelayCommand(GoHome);

    private void GoHome()
    {
        NudgeBuddy();   // the owner is here: she wakes up
        _ = NewChatAndShowAsync();
    }

    /// <summary>The ledger beside codex.db (made with the chat store).</summary>
    private void InitLiveRuns(string? dataRoot) => _liveRuns ??= new LiveRunLedger(dataRoot);

    /// <summary>At start-up, after the chats are listed: a chat whose run CluadeX never saw end shows as stopped, not as
    /// finished and not as working (nothing runs it any more).</summary>
    private void MarkOrphanRuns()
    {
        if (_liveRuns == null) return;
        List<string> orphans;
        try { orphans = _liveRuns.TakeOrphans(); }
        catch { return; }
        foreach (var id in orphans)
            if (Sessions.FirstOrDefault(s => s.Id == id) is { } s && RunOf(s) == null)
                s.RunStatus = RunBadge.Stopped;
    }

    private void NoteRunBegan(ChatSession session)
    {
        _lastActivityUtc = DateTime.UtcNow;
        try { _liveRuns?.Began(session.Id); } catch { }
    }

    private void NoteRunEnded(ChatSession session, string badge)
    {
        _lastActivityUtc = _lastEndUtc = DateTime.UtcNow;
        _lastEndBadge = badge;
        try { _liveRuns?.Ended(session.Id); } catch { }
    }

    /// <summary>The buddy's mood from the runs now (UI thread): on every run change and on the 5 s tick, which carries
    /// her from pleased back to idle and then to sleep.</summary>
    private void RefreshBuddy()
    {
        int working = 0, waiting = 0;
        foreach (var run in _runs)
        {
            working++;
            if (run.Session.RunStatus == RunBadge.Waiting) waiting++;
        }
        var now = DateTime.UtcNow;
        BuddyMoodState = BuddyMood.Decide(working, waiting, _lastEndBadge, now - _lastEndUtc, now - _lastActivityUtc);
    }

    /// <summary>The owner is back (typing or sending): she wakes up.</summary>
    public void NudgeBuddy()
    {
        _lastActivityUtc = DateTime.UtcNow;
        RefreshBuddy();
    }
}
