using System.Collections.ObjectModel;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// A turn cut off by the app closing or crashing, after the restart (owner 2026-11-11: "ถ้าเราออกจากโปรแกรมในขณะที่ แชทยัง
/// ทำงาน หรือยังค้างในบล๊อกคำถาม เราต้องมีวิธีจัดการเมื่อกลับมาใหม่อย่างไรให้ดี"):
/// <list type="bullet">
/// <item>Closing: <see cref="ConfirmCloseWithRuns"/> says what will be cut off; <see cref="NoteAppClosing"/> saves every
/// running chat (an off-screen one only reached the disk at its turn's end) and marks its ledger entry "closed", with
/// what it waited on.</item>
/// <item>Start-up (<see cref="RecoverOrphanRunsInStore"/>, off the UI thread, before the chats are listed): each chat the
/// ledger says was cut off gets a recovery card at its end — why, what it waited on, Continue / Dismiss — written into
/// the stored chat, so it is there whenever the chat is opened, even days later. A split chat's parent is told too.</item>
/// <item>Opening a chat with an open card (<see cref="ArmRecoveryCards"/>): its question cards from the cut-off turn can
/// be answered again; the answer goes to the model as the owner's message and the work carries on.</item>
/// <item>A card nobody used is settled when the next turn starts there (<see cref="SettleRecoveryCards"/>): its question
/// cards say they expired, the box says a new message took its place. Never a card that looks live and is not.</item>
/// </list>
/// The policy and the card's data are <see cref="RunRecovery"/>.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>The cut-off runs taken off the ledger at start-up (their badges are set once the chats are listed).</summary>
    private List<LiveRunLedger.Orphan>? _startOrphans;

    /// <summary>The app is going down: runs that end now ended because of that, and must stay on the ledger as cut off.</summary>
    private volatile bool _appClosing;

    /// <summary>What each run's ledger entry says it waits on, so the file is written only when that changes.</summary>
    private readonly Dictionary<string, string?> _ledgerWaiting = new(StringComparer.Ordinal);

    /// <summary>A run's wait kind now (UI thread): a question card, a permission prompt, or null while it works.</summary>
    private static string? WaitKindOf(IEnumerable<ChatMessage> rows)
    {
        string? kind = null;
        foreach (var m in rows)
        {
            if (m.Role == MessageRole.Question && !m.QuestionAnswered) return LiveRunLedger.WaitQuestion;
            if (m.Role == MessageRole.PermissionRequest && !m.PermissionAnswered) kind = LiveRunLedger.WaitPermission;
        }
        return kind;
    }

    /// <summary>From RefreshSessionBadges: the ledger learns when a run starts or stops waiting on the owner — a crash
    /// while a card is open then still says so after the restart.</summary>
    private void NoteRunWaiting(ChatRun run, bool waiting)
    {
        if (_liveRuns == null || _appClosing) return;
        string? kind = waiting ? WaitKindOf(run.Messages) : null;
        if (_ledgerWaiting.TryGetValue(run.Session.Id, out var was) && was == kind) return;
        _ledgerWaiting[run.Session.Id] = kind;
        try { _liveRuns.SetWaiting(run.Session.Id, kind); } catch { }
    }

    // ─── closing ───

    /// <summary>Closing with runs going: one question that names what will be cut off (and says it can be picked up
    /// again). True = close. UI thread.</summary>
    public bool ConfirmCloseWithRuns()
    {
        if (_runs.Count == 0) return true;
        var lines = _runs.Take(6).Select(r =>
            "  • " + (string.IsNullOrWhiteSpace(r.Session.Title) ? Loc("chat.recover.untitled") : r.Session.Title)
            + (WaitKindOf(r.Messages) is { } w ? "  — " + Loc(w == LiveRunLedger.WaitQuestion ? "chat.recover.closeWaitsQ" : "chat.recover.closeWaitsP") : ""));
        string body = LocF("chat.recover.closeBody", _runs.Count) + "\n\n" + string.Join("\n", lines)
                      + (_runs.Count > 6 ? "\n  …" : "") + "\n\n" + Loc("chat.recover.closeAfter");
        int answer = Views.Editor.ChoiceDialog.Show(Loc("chat.recover.closeTitle"), body,
            new[] { Loc("chat.recover.closeAnyway"), Loc("chat.recover.closeKeep") }, defaultIndex: 1);
        return answer == 0;
    }

    /// <summary>The app is closing with runs going (the window's close, an update restart, OnExit — once is enough, again
    /// is harmless): every running chat is saved as it stands, and the ledger says the owner closed it, with what each
    /// waited on. A run that ends after this (its CLI killed on the way out) stays on the ledger. UI thread.</summary>
    public void NoteAppClosing()
    {
        if (_appClosing) return;
        _appClosing = true;
        var waiting = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var run in _runs.ToList())
        {
            waiting[run.Session.Id] = WaitKindOf(run.Messages);
            // The chat on screen is saved by SaveNow; one off screen only at its turn's end, which will not come now.
            if (!ReferenceEquals(run.Session, CurrentSession))
                try { SaveRunChat(run); } catch { }
        }
        if (waiting.Count == 0) return;
        try { _liveRuns?.MarkClosing(waiting); } catch { }
        _debugLog?.Info("Recover", $"closing with {waiting.Count} run(s) going: {string.Join(", ", waiting.Select(w => w.Key[..Math.Min(8, w.Key.Length)] + (w.Value != null ? "/" + w.Value : "")))}");
    }

    // ─── start-up ───

    /// <summary>Start-up, off the UI thread, before the chat list and the last chat are read: every run the last CluadeX
    /// did not see end gets its recovery card, written into the stored chat. Never throws.</summary>
    private void RecoverOrphanRunsInStore()
    {
        if (_liveRuns == null) return;
        List<LiveRunLedger.Orphan> orphans;
        try { orphans = _liveRuns.TakeOrphanRuns(); }
        catch { return; }
        _startOrphans = orphans;
        if (orphans.Count == 0) return;
        var cut = new List<(string Id, string Title)>();
        foreach (var o in orphans)
        {
            try
            {
                var chat = _persistenceService.LoadSession(o.ChatId);
                if (chat == null || chat.Messages.Count == 0) continue;
                if (!AddRecoveryCard(chat.Messages, o)) continue;
                _persistenceService.SaveSession(chat);
                cut.Add((o.ChatId, chat.Title));
                _debugLog?.Info("Recover", $"chat {o.ChatId[..Math.Min(8, o.ChatId.Length)]} was cut off ({(o.Closed ? "app closed" : "app ended")}{(o.Waiting != null ? ", waiting on a " + o.Waiting : "")}): recovery card added");
            }
            catch (Exception ex) { _debugLog?.Warn("Recover", $"chat {o.ChatId}: recovery card not added", ex); }
        }
        TellParentsOfCutSplits(cut);
    }

    /// <summary>Put the recovery card at the end of <paramref name="rows"/> (a chat whose run <paramref name="o"/> was cut
    /// off). False when it already ends on an open one (the same cut-off told twice).</summary>
    private static bool AddRecoveryCard(List<ChatMessage> rows, LiveRunLedger.Orphan o)
    {
        int last = rows.FindLastIndex(m => RunRecovery.Read(m) != null);
        if (last >= 0 && RunRecovery.IsOpenCard(rows, last)) return false;
        var open = RunRecovery.OpenQuestions(rows, rows.Count);
        var info = new RunRecovery.Info(o.Closed ? RunRecovery.WhyClosed : RunRecovery.WhyCrash,
            open.Count > 0 ? LiveRunLedger.WaitQuestion : o.Waiting,
            open.Select(RunRecovery.QuestionText).Where(q => q.Length > 0).ToList(), DateTime.UtcNow);
        rows.Add(RunRecovery.Card(info, Loc));
        return true;
    }

    /// <summary>A split chat cut off before it reported back: its parent hears it (a notice the model also reads on its next
    /// turn), so it neither waits for a report that will not come nor thinks the work was done.</summary>
    private void TellParentsOfCutSplits(List<(string Id, string Title)> cut)
    {
        var links = ChatLinkStore.Current;
        if (links == null || cut.Count == 0) return;
        foreach (var group in cut.Select(c => (c.Id, c.Title, Parent: links.Get(c.Id)?.ParentId))
                     .Where(c => !string.IsNullOrEmpty(c.Parent)).GroupBy(c => c.Parent!))
        {
            try
            {
                var parent = _persistenceService.LoadSession(group.Key);
                if (parent == null) continue;
                string names = string.Join(", ", group.Select(c => "“" + (string.IsNullOrWhiteSpace(c.Title) ? Loc("chat.recover.untitled") : c.Title) + "”"));
                parent.Messages.Add(new ChatMessage
                {
                    Role = MessageRole.System,
                    ToolName = ChatMessage.NoticeTag,
                    Content = "⏸ " + LocF("chat.recover.splitCut", names),
                });
                _persistenceService.SaveSession(parent);
            }
            catch (Exception ex) { _debugLog?.Warn("Recover", $"parent {group.Key}: split notice not added", ex); }
        }
    }

    /// <summary>The cut-off chats in the sidebar show "stopped" (UI thread, once they are listed). The chats taken at
    /// start-up, or — called on its own (the harness) — the ledger's orphans now, with their cards.</summary>
    private void MarkOrphanRuns()
    {
        var orphans = _startOrphans;
        _startOrphans = null;
        if (orphans == null)
        {
            if (_liveRuns == null) return;
            try { orphans = _liveRuns.TakeOrphanRuns(); }
            catch { return; }
            // A damaged chat file is not written to (start-up skipped the cards for the same reason): badges only.
            bool canWrite = _persistenceService.IsDatabaseHealthy;
            foreach (var o in canWrite ? orphans : new List<LiveRunLedger.Orphan>())
            {
                if (CurrentSession?.Id == o.ChatId && RunOf(CurrentSession) == null)
                {
                    var rows = Messages.ToList();
                    if (rows.Count > 0 && AddRecoveryCard(rows, o)) { Messages.Add(rows[^1]); _isDirty = true; SaveNow(); ArmRecoveryCards(Messages); }
                }
                else if (Sessions.Any(s => s.Id == o.ChatId))
                {
                    var chat = _persistenceService.LoadSession(o.ChatId);
                    if (chat is { Messages.Count: > 0 } && AddRecoveryCard(chat.Messages, o)) _persistenceService.SaveSession(chat);
                }
            }
        }
        foreach (var o in orphans)
            if (Sessions.FirstOrDefault(s => s.Id == o.ChatId) is { } s && RunOf(s) == null)
                s.RunStatus = RunBadge.Stopped;
    }

    // ─── opening a chat with an open card ───

    /// <summary>The rows of a chat just put on screen: if it ends on an open recovery card, its cut-off question cards are
    /// answerable again. UI thread.</summary>
    private void ArmRecoveryCards(IList<ChatMessage> rows)
    {
        int at = -1;
        for (int i = rows.Count - 1; i >= 0; i--)
            if (RunRecovery.Read(rows[i]) != null) { at = i; break; }
        var list = rows as IReadOnlyList<ChatMessage> ?? rows.ToList();
        if (at < 0 || !RunRecovery.IsOpenCard(list, at)) return;
        var card = rows[at];
        foreach (var q in RunRecovery.OpenQuestions(list, at))
        {
            if (q.QuestionCallback != null || q.StepsCallback != null) continue;   // armed already (opened twice)
            var question = q;
            string text = RunRecovery.QuestionText(q);
            var (options, steps) = RunRecovery.ReadQuestion(q);
            if (steps.Count > 0)
                q.ReopenSteps(steps, answers => OnRecoveredAnswer(card, question, "", AskStep.AnswersText(steps, answers)));   // the answers name their questions
            else
                q.ReopenQuestion(options, answer => OnRecoveredAnswer(card, question, text, answer));
        }
    }

    /// <summary>A reopened question card was answered: the box says so, and the answer goes to the model as the owner's
    /// message — with the question, since the ask_user that asked it never returned.</summary>
    private void OnRecoveredAnswer(ChatMessage card, ChatMessage question, string text, string answer)
    {
        string label = Loc("chat.recover.answered");
        card.ToolSummary = label;
        card.NextStep?.MarkPicked(label);
        var rows = Messages.ToList();
        int at = rows.IndexOf(card);
        if (at >= 0)
            foreach (var other in RunRecovery.OpenQuestions(rows, at).Where(o => !ReferenceEquals(o, question)))
                other.CloseQuestion(Loc("chat.recover.qExpired"));
        _isDirty = true;
        SendRecoveryMessage(RunRecovery.AnswerMessage(text, answer, Loc));
    }

    /// <summary>A button on the recovery card. Continue (or Ask again) sends the message that carries on; Dismiss only
    /// closes it. Either way the cut-off turn's question cards say they are over.</summary>
    private void OnRecoveryPicked(ChatMessage row, string label, RunRecovery.Info info, string go)
    {
        if (row.ToolSummary is { Length: > 0 }) return;   // picked once already
        row.ToolSummary = label;
        bool carryOn = label == go;
        var rows = Messages.ToList();
        int at = rows.IndexOf(row);
        if (at >= 0)
            foreach (var q in RunRecovery.OpenQuestions(rows, at))
                q.CloseQuestion(Loc(carryOn ? "chat.recover.qReasked" : "chat.recover.qExpired"));
        _isDirty = true;
        if (carryOn) SendRecoveryMessage(RunRecovery.ContinueMessage(info, Loc));
        else
        {
            SaveNow();
            StatusText = Loc("chat.recover.dismissed");
        }
    }

    private void SendRecoveryMessage(string text)
    {
        string draft = UserInput ?? "";
        if (CurrentSession is { } s)
        {
            Services.Agents.WorkflowGate.NoteOwnerMessage(s.Id, "", append: IsGenerating);
            Services.Agents.FableGate.NoteOwnerMessage(s.Id, "", append: IsGenerating);
            Services.Agents.DesignGate.NoteOwnerMessage(s.Id, "", append: IsGenerating);
        }
        if (IsGenerating) SendIntoRunningReply(text);
        else _ = SendMessageCore(text, pipe: null);
        if (draft.Length > 0 && string.IsNullOrEmpty(UserInput)) UserInput = draft;
    }

    /// <summary>A turn starts in a chat whose recovery card was never used (the owner typed something else): the card says a
    /// new message took its place and the cut-off questions expire — they are not left looking live. UI thread.</summary>
    private void SettleRecoveryCards(IList<ChatMessage> rows)
    {
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            var m = rows[i];
            if (RunRecovery.Read(m) == null) continue;
            if (m.ToolSummary is { Length: > 0 }) return;   // used: nothing open
            string label = Loc("chat.recover.superseded");
            m.ToolSummary = label;
            m.NextStep?.MarkPicked(label);
            var list = rows as IReadOnlyList<ChatMessage> ?? rows.ToList();
            foreach (var q in RunRecovery.OpenQuestions(list, i))
                q.CloseQuestion(Loc("chat.recover.qExpired"));
            return;
        }
    }
}
