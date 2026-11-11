using System.Windows;
using System.Windows.Threading;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;
using CluadeX.Services.Tools;

namespace CluadeX.ViewModels;

/// <summary>
/// A split chat and the chat that opened it talk to each other (owner 2026-11-11: "แชทเซสชั่น หลัก กับย่อย สามารถส่ง
/// ข้อมูลงานหากันได้ … เมื่อแยกย่อยไปทำงานแล้วเสร็จก็กลับไปรายงาน แชทหลัก … ต้องใช้ได้กับทุกโมเดล"). The rules are in
/// <see cref="SplitReports"/>; this is where they meet the chats:
/// <list type="bullet">
/// <item>A split ends a turn → its report goes to the parent: a note to the parent's run, or a notice row that is saved
///   with the parent (on screen or not — it used to be dropped when the parent was neither) and, once the parent's last
///   split is done, a turn in the parent that carries on from the reports.</item>
/// <item>message_chat → a message to the parent or to a split, as a note when it works, or a turn when it is idle.</item>
/// <item>close_chat read → a split's whole last answer.</item>
/// <item>Every turn of a split chat is told who opened it and that its answer goes back there.</item>
/// </list>
/// Every turn these start is an ordinary turn of that chat (CluadeX's own loop, or the CLI the chat runs on), so it works
/// for every model.
/// </summary>
public partial class ChatViewModel
{
    private readonly SplitWakeLimit _splitWakes = new();
    /// <summary>message_chat from one chat to another: at most 10 in 30 minutes per pair (no endless back-and-forth).</summary>
    private readonly SplitWakeLimit _chatMessages = new() { MaxWakes = 10, Window = TimeSpan.FromMinutes(30) };
    /// <summary>How each chat's last turn ended — a stopped or failed parent is not woken.</summary>
    private readonly Dictionary<string, (bool Stopped, bool Failed)> _lastTurnEnd = new(StringComparer.Ordinal);
    /// <summary>Chats whose next turn an agent starts (a message_chat, a wake, a split opened in the background).</summary>
    private readonly HashSet<string> _agentDrivenNext = new(StringComparer.Ordinal);
    /// <summary>Reports that reached an idle parent since its last turn, and notes its run never took: the wake carries them.</summary>
    private readonly Dictionary<string, (int Reports, List<string> Notes)> _splitWaiting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DispatcherTimer> _splitWakeTimers = new(StringComparer.Ordinal);

    /// <summary>The tag of a note a split report or a chat message hands a run (so an untaken one is delivered again).</summary>
    private sealed record SplitNoteTag(string FromChatId);

    private static readonly string[] ParentWords = { "parent", "main", "up", "opener", "the parent", "parent chat", "หลัก", "แชทหลัก" };

    private void InitSplitReports()
    {
        // Read on the tool's own flow (its run scope): which chat is asking — before the hop to the UI thread.
        ProjectToolModule.ReadChatHandler = target =>
        {
            string? from = FlowRun?.Session.Id;
            return App.Current.Dispatcher.InvokeAsync(() => ReadSplitForAgent(from, target)).Task;
        };
        ProjectToolModule.MessageChatHandler = (target, message) =>
        {
            string? from = FlowRun?.Session.Id;
            return App.Current.Dispatcher.InvokeAsync(() => MessageChatForAgent(from, target, message)).Task;
        };
    }

    // ─── a split ends a turn ───

    /// <summary>
    /// A split chat finished its turn: the chat that opened it hears so — a notice with the report (and the branch, when
    /// it worked in its own worktree), which the parent's agent reads at its next step while it runs, else on its next
    /// turn; and the sidebar's tree updates its count. Kept for callers that only know the rows (an owner-driven turn:
    /// nothing is woken).
    /// </summary>
    private void TellParentSplitFinished(ChatSession child, IEnumerable<ChatMessage>? childMessages = null) =>
        ReportToParent(child, childMessages, stopped: false, failed: false, agentDriven: false);

    /// <summary>The report of <paramref name="child"/>'s turn to its parent, by <see cref="SplitReports.Decide"/>.</summary>
    private void ReportToParent(ChatSession child, IEnumerable<ChatMessage>? turnRows, bool stopped, bool failed, bool agentDriven)
    {
        if (child.ParentId is not { } pid) return;
        var parent = Sessions.FirstOrDefault(s => s.Id == pid);
        if (parent == null || ChatLinkStore.Current is not { } store) { RebuildVisibleSessions(); return; }

        string answer = AnswerOf(turnRows, failed);
        string title = string.IsNullOrWhiteSpace(child.Title) ? ChatRef.Of(child.Id) : child.Title;
        bool fable = DispatchPolicy.IsFable(child.OwnModel);
        var (treeShown, treeForModel) = WorktreeNoticeOf(child);
        string gist = SplitReports.Clip(answer, fable ? FableReportChars : SplitReports.ShownChars, oneLine: true);
        string tail = gist.Length > 0 ? ": " + gist : "";
        string shown = stopped ? "⏹ " + LocF("chat.split.stoppedNote", title, tail)
            : failed ? "⚠️ " + LocF("chat.split.failedNote", title, tail)
            : fable && gist.Length > 0 ? "🧠 " + LocF("fable.report", title, gist)
            : "✅ " + (gist.Length > 0 ? LocF("orch.splits.turnNote", title, gist) : LocF("chat.split.finishedNote", title));
        shown += treeShown;
        string forModel = (fable ? "Claude Fable 5.2 in s" : "S")
            + SplitReports.ReportForModel(title, child.Id, ChatRef.Of(child.Id), SplitReports.Outcome(stopped, failed), answer, treeForModel)[1..];

        var parentRun = _runs.FirstOrDefault(r => r.Session.Id == pid);
        int siblings = store.ChildrenOf(pid).Count(k => k.Id != child.Id && k.Link.ArchivedAt == null
            && Sessions.FirstOrDefault(s => s.Id == k.Id) is { } kid && RunOf(kid) != null);
        var last = _lastTurnEnd.TryGetValue(pid, out var e) ? e : (false, false);
        var d = SplitReports.Decide(parentRun != null, _settingsService.Settings.SplitAutoContinue, last.Item1, last.Item2,
            agentDriven, stopped, siblings, _splitWakes.WouldAllow(pid, DateTime.Now));
        _debugLog?.Info("Chat", $"split {child.Id} → parent {pid}: {d.Delivery} ({d.Why}) · {answer.Length} chars");

        switch (d.Delivery)
        {
            case SplitReports.Delivery.NoteToRun:
                parentRun!.Messages.Add(Notice(shown, forModel));
                parentRun.Scope?.AddUserNote(NoticePrefix + forModel, new SplitNoteTag(child.Id));
                break;
            case SplitReports.Delivery.Wake:
                PostTurnMessage(parent, Notice(shown, forModel));
                CountWaitingReport(pid);
                ScheduleSplitWake(pid);
                break;
            default:
                string why = d.Why switch
                {
                    SplitReports.Reason.SiblingsWorking => " " + LocF("chat.split.waitOthers", siblings),
                    SplitReports.Reason.AutoOff => "\n" + Loc("chat.split.noAuto"),
                    SplitReports.Reason.WakeLimit => "\n" + LocF("chat.split.wakeLimit", _splitWakes.MaxWakes, Services.Watch.RunWatchService.Elapsed(_splitWakes.Window)),
                    _ => "",
                };
                PostTurnMessage(parent, Notice(shown + why, forModel));
                if (d.Why == SplitReports.Reason.SiblingsWorking) CountWaitingReport(pid);
                // The owner's own turn in the last split to finish: reports the agent's splits left waiting still wake it.
                else if (d.Why == SplitReports.Reason.OwnerDrove && siblings == 0 && _splitWaiting.ContainsKey(pid)) ScheduleSplitWake(pid);
                break;
        }
        RebuildVisibleSessions();
    }

    /// <summary>A notice row: <paramref name="text"/> is what the owner sees; <paramref name="forModel"/> (kept in the row's
    /// ToolOutput, saved with the chat, never drawn for a notice) is what the model reads in its history instead — the whole
    /// report, where the row shows its start.</summary>
    private static ChatMessage Notice(string text, string? forModel = null) =>
        new() { Role = MessageRole.System, ToolName = ChatMessage.NoticeTag, Content = text, ToolOutput = forModel };

    /// <summary>The turn's answer: its last reply (the error, for a turn that failed with no reply after it).</summary>
    private static string AnswerOf(IEnumerable<ChatMessage>? rows, bool failed)
    {
        var list = rows?.ToList() ?? new();
        var reply = list.LastOrDefault(m => m.Role == MessageRole.Assistant && !m.HasError && !string.IsNullOrWhiteSpace(m.Content));
        if (failed && list.LastOrDefault(m => m.Role == MessageRole.Assistant && m.HasError) is { } err
            && (reply == null || list.IndexOf(err) > list.IndexOf(reply)))
            return (err.Content ?? "").Trim();
        return reply == null ? "" : StripThinkingSpans(reply.Content ?? "").Trim();
    }

    private void CountWaitingReport(string pid)
    {
        var (n, notes) = _splitWaiting.TryGetValue(pid, out var w) ? w : (0, new List<string>());
        _splitWaiting[pid] = (n + 1, notes);
    }

    /// <summary>The parent turns idle with reports and messages its run never took (it finished first, or a CLI did not
    /// call a cluadex tool after they came): they are delivered again — by a turn of their own — unless the owner stopped it.</summary>
    private void HandOnUntakenSplitNotes(ChatRun run, List<(string Text, object? Tag)> leftover, bool stopped, bool failed)
    {
        var mine = leftover.Where(n => n.Tag is SplitNoteTag).ToList();
        if (mine.Count == 0) return;
        leftover.RemoveAll(n => n.Tag is SplitNoteTag);
        string pid = run.Session.Id;
        // The rows are in the chat already: after a stop or an error its next turn (the owner's) reads them there.
        if (stopped || failed) return;
        var (n, notes) = _splitWaiting.TryGetValue(pid, out var w) ? w : (0, new List<string>());
        notes.AddRange(mine.Select(m => m.Text));
        _splitWaiting[pid] = (n, notes);
        if (!_settingsService.Settings.SplitAutoContinue) return;
        ScheduleSplitWake(pid);
    }

    /// <summary>A wake for <paramref name="pid"/> in <see cref="SplitReports.Coalesce"/>: splits that finish close
    /// together are carried on from in one turn.</summary>
    private void ScheduleSplitWake(string pid)
    {
        if (Application.Current == null) return;
        if (!_splitWakeTimers.TryGetValue(pid, out var timer))
        {
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SplitReports.Coalesce };
            timer.Tick += (_, _) => { timer.Stop(); _splitWakeTimers.Remove(pid); WakeParentNow(pid); };
            _splitWakeTimers[pid] = timer;
        }
        timer.Stop();
        timer.Start();
    }

    /// <summary>Start the parent's turn now (UI thread; public for the harness): nothing else works there, and none of
    /// its splits is still working (the last one to finish wakes it).</summary>
    public void WakeParentNow(string pid)
    {
        if (Sessions.FirstOrDefault(s => s.Id == pid) is not { } parent) { _splitWaiting.Remove(pid); return; }
        if (RunOf(parent) != null) return;   // a turn began meanwhile (the owner's): it reads the rows in the chat
        var store = ChatLinkStore.Current;
        if (store != null && store.ChildrenOf(pid).Any(k => k.Link.ArchivedAt == null
                && Sessions.FirstOrDefault(s => s.Id == k.Id) is { } kid && RunOf(kid) != null))
            return;
        if (!_splitWaiting.Remove(pid, out var waiting) || (waiting.Reports == 0 && waiting.Notes.Count == 0)) return;
        // Asked again now (the owner may have switched it off, or stopped the parent, in the meantime).
        if (!_settingsService.Settings.SplitAutoContinue) return;
        if (_lastTurnEnd.TryGetValue(pid, out var end) && (end.Stopped || end.Failed)) return;
        if (!_splitWakes.TryTake(pid, DateTime.Now))
        {
            PostTurnMessage(parent, Notice(LocF("chat.split.wakeLimit", _splitWakes.MaxWakes, Services.Watch.RunWatchService.Elapsed(_splitWakes.Window))));
            return;
        }
        int n = Math.Max(1, waiting.Reports);
        // The reports themselves are the notice rows above (their whole text is what the model reads there).
        string text = LocF("chat.split.wake", n) + "\n\n" + NoticePrefix + SplitReports.WakeForModel(n);
        _debugLog?.Info("Chat", $"split reports: starting a turn in parent {pid} ({n} report(s), {waiting.Notes.Count} untaken note(s))");
        _agentDrivenNext.Add(pid);
        Detached(() => StartInternalTurnAsync(pid, text));
    }

    /// <summary>A turn in <paramref name="chatId"/> that CluadeX starts itself (a wake, a message, a split opened in the
    /// background) — on screen through Send, else off screen in the chat's own list. Refused because a turn began there
    /// meanwhile: the text goes to that turn as a note. Refused for another reason: a notice row says so.</summary>
    private async Task StartInternalTurnAsync(string chatId, string text)
    {
        // The mark set for this start, when no turn took it: the chat's next turn is the owner's again. (Only when no turn
        // runs there now — a turn that did begin took the mark itself, and a later start may have set a new one.)
        async Task DropMark()
        {
            try { await Application.Current.Dispatcher.InvokeAsync(() => { if (!_runs.Any(r => r.Session.Id == chatId)) _agentDrivenNext.Remove(chatId); }); }
            catch { /* app closing */ }
        }
        for (int attempt = 0; attempt < 2; attempt++)
        {
            McpTaskOutcome outcome;
            try { outcome = await ContinueChatForPipeAsync(chatId, null, text, null, null, null, CancellationToken.None, fromWatch: true); }
            catch (Exception ex) { outcome = new McpTaskOutcome(ex.Message, IsError: true); }
            if (!outcome.IsError) return;                 // it ran
            if (!NotSent(outcome.Text)) { await DropMark(); return; }   // it ran and failed (its chat says so), or broke before
            bool handed = await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_runs.FirstOrDefault(r => r.Session.Id == chatId) is not { Scope: { } scope }) return false;
                scope.AddUserNote(text, new SplitNoteTag(chatId));
                _agentDrivenNext.Remove(chatId);   // that turn is someone else's; the note rides on it
                return true;
            });
            if (handed) return;
            if (attempt == 0) { await Task.Delay(1500); continue; }
            _debugLog?.Warn("Chat", $"split/message turn in {chatId} did not start: {outcome.Text}");
            await DropMark();
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (Sessions.FirstOrDefault(s => s.Id == chatId) is { } s)
                        PostTurnMessage(s, Notice(LocF("watch.notStarted", Services.Watch.RunWatchService.Clip(outcome.Text, 200))));
                });
            }
            catch { /* app closing */ }
        }
    }

    /// <summary>EndTurn: how the turn ended, for the next report's rules — and the report to its parent.</summary>
    private void SplitTurnEnded(ChatRun run, bool stopped, bool failed, List<(string Text, object? Tag)>? leftover)
    {
        _lastTurnEnd[run.Session.Id] = (stopped, failed);
        if (leftover is { Count: > 0 }) HandOnUntakenSplitNotes(run, leftover, stopped, failed);
        var turnRows = run.FirstRow >= 0 && run.FirstRow <= run.Messages.Count ? run.Messages.Skip(Math.Max(0, run.FirstRow - 1)) : run.Messages;
        ReportToParent(run.Session, turnRows, stopped, failed, run.StartedBySplit);
    }

    // ─── a split opened while the owner is in another chat ───

    /// <summary>
    /// new_chat from a chat that runs off screen (UI thread): the split chat is made beside the owner's work — in the
    /// sidebar under its parent, its rows in its own list, saved — and its first turn starts off screen, so the window
    /// stays on the chat the owner is using. Returns the tool's report.
    /// </summary>
    private string OpenSplitInBackground(string? path, string? message, string? title, string parentId, string? from,
        List<ChatMessage>? inherited, string? model, string? openedNote, ChatWorktree? worktree)
    {
        var parentChat = Sessions.First(s => s.Id == parentId);
        string folder = path
            ?? _runs.FirstOrDefault(r => r.Session.Id == parentId)?.Scope?.WorkingDirectory
            ?? (string.IsNullOrWhiteSpace(parentChat.ProjectPath) ? _fileSystemService.AppWorkingDirectory ?? "" : parentChat.ProjectPath);
        var chat = new ChatSession
        {
            ProjectPath = folder,
            // A split chat stays in its parent's project.
            ProjectId = ProjectOf(parentChat).Length > 0 ? parentChat.ProjectId : FolderProjectId(folder),
        };
        chat.Title = title ?? FirstLineOf(message) ?? chat.Title;
        if (inherited != null) chat.Messages.AddRange(inherited);
        ChatLinkStore.Current?.Link(chat.Id, parentId, title ?? FirstLineOf(message) ?? "", model);
        if (worktree != null) ChatLinkStore.Current?.SetWorktree(chat.Id, worktree);
        string shownFolder = string.IsNullOrWhiteSpace(folder) ? "(no project)" : folder;
        chat.Messages.Add(new ChatMessage { Role = MessageRole.System, Content = "💬 " + LocF("chat.newChat.byAgent", from ?? "?", shownFolder) });
        if (openedNote != null) chat.Messages.Add(new ChatMessage { Role = MessageRole.System, Content = openedNote });
        if (worktree != null)
            chat.Messages.Add(Notice("🌿 " + LocF("chat.split.worktreeOpened", worktree.Folder, worktree.Branch,
                worktree.BaseCommit[..Math.Min(9, worktree.BaseCommit.Length)])));
        Sessions.Insert(0, chat);
        _persistenceService.SaveSession(chat);
        NoteSplitChip(parentId, chat);
        RebuildVisibleSessions();
        _debugLog?.Info("Chat", $"new_chat {chat.Id} opened in the background (the owner is in another chat) · parent {parentId}");
        string where = worktree == null ? shownFolder
            : $"its own git worktree {worktree.Folder} (branch {worktree.Branch}, cut from this chat's HEAD {worktree.BaseCommit}; "
              + "uncommitted changes here are not in it)";
        if (message == null)
            return $"Opened a new empty chat (ref {chat.Ref}, id {chat.Id}) in {where}, in the sidebar under this chat (the user is in "
                 + "another chat, so it did not take the screen); the user types there.";
        _agentDrivenNext.Add(chat.Id);
        Detached(() => StartInternalTurnAsync(chat.Id, message));
        return $"Opened a new chat (ref {chat.Ref}, id {chat.Id}) in {where} and started it with your message, in the background (the "
             + "user is in another chat, so it did not take the screen; it shows under this chat in the sidebar). " + SplitStartedTail(worktree);
    }

    // ─── what a split chat is told every turn ───

    /// <summary>The note a split chat's turn carries (who opened it, that its answer goes back there), or null for a chat
    /// nobody split off.</summary>
    private string? SplitTurnInstruction(ChatSession? session)
    {
        if (session == null || ChatLinkStore.Current is not { } store) return null;
        string? own = null;
        if (store.Get(session.Id) is { ArchivedAt: null } link)
        {
            string parentTitle = Sessions.FirstOrDefault(s => s.Id == link.ParentId)?.Title ?? ChatRef.Of(link.ParentId);
            own = NoticePrefix + SplitReports.SplitInstruction(parentTitle, link.Task);
        }
        // A chat with split chats still open hears, every turn, which they are and where new work goes — so a request that
        // is one of theirs joins that chat instead of being done twice. Nothing is added for a chat with none.
        string? kids = SplitReports.ChildrenNote(OpenSplitsOf(session.Id));
        return own == null ? kids == null ? null : NoticePrefix + kids
             : kids == null ? own : own + "\n\n" + NoticePrefix + kids;
    }

    /// <summary>The split chats of <paramref name="parentId"/> not put away, with whether each is working and a short gist
    /// of a finished one's answer (only from rows already loaded — nothing is read from disk for this).</summary>
    private List<SplitReports.OpenSplit> OpenSplitsOf(string parentId)
    {
        var list = new List<SplitReports.OpenSplit>();
        foreach (var (id, link) in ChatLinkStore.Current?.ChildrenOf(parentId) ?? new())
        {
            if (link.ArchivedAt != null || Sessions.FirstOrDefault(c => c.Id == id) is not { } c) continue;
            bool working = RunOf(c) != null;
            IEnumerable<ChatMessage>? rows = working ? null : CurrentSession?.Id == id ? Messages : c.Messages.Count > 0 ? c.Messages : null;
            string title = string.IsNullOrWhiteSpace(c.Title) ? link.Task : c.Title;
            list.Add(new(id, ChatRef.Of(id), title, link.Task, working, rows == null ? null : AnswerOf(rows, failed: false)));
        }
        return list;
    }

    // ─── message_chat ───

    /// <summary>message_chat (UI thread): <paramref name="message"/> from <paramref name="fromId"/> to its parent ("parent")
    /// or to one of its split chats. Throws (the tool fails with the reason) for anything else.</summary>
    private string MessageChatForAgent(string? fromId, string target, string message)
    {
        if (fromId == null || Sessions.FirstOrDefault(s => s.Id == fromId) is not { } from)
            throw new InvalidOperationException("message_chat works from a chat's own run.");
        var store = ChatLinkStore.Current ?? throw new InvalidOperationException("Split chats are not available here.");
        target = (target ?? "").Trim().Trim('"', '\'');
        bool toParent = ParentWords.Contains(target.ToLowerInvariant());
        bool sibling = false;
        string toId;
        if (toParent)
            toId = store.Get(fromId)?.ParentId
                ?? throw new InvalidOperationException("This chat was not opened by another chat (it is not a split chat), so it has no \"parent\". "
                    + "message_chat reaches the chat that opened this one and the chats this one split off with new_chat.");
        else
        {
            var kids = store.ChildrenOf(fromId);
            var hit = kids.FirstOrDefault(k => k.Id == target || ChatRef.Matches(k.Id, target)
                || string.Equals(Sessions.FirstOrDefault(c => c.Id == k.Id)?.Title, target, StringComparison.OrdinalIgnoreCase));
            if (hit.Id == null)
            {
                // The parent named by its reference or title is still the parent.
                if (store.Get(fromId)?.ParentId is { } up && (up == target || ChatRef.Matches(up, target)
                        || string.Equals(Sessions.FirstOrDefault(c => c.Id == up)?.Title, target, StringComparison.OrdinalIgnoreCase)))
                { toId = up; toParent = true; }
                // Another split chat of the same parent: work that is its own goes straight to it (new_chat names it).
                else if (store.Get(fromId)?.ParentId is { } p && store.ChildrenOf(p).FirstOrDefault(k => k.Id != fromId
                             && (k.Id == target || ChatRef.Matches(k.Id, target)
                                 || string.Equals(Sessions.FirstOrDefault(c => c.Id == k.Id)?.Title, target, StringComparison.OrdinalIgnoreCase))) is { Id: not null } sib)
                { toId = sib.Id; sibling = true; }
                else
                    throw new InvalidOperationException($"No chat \"{target}\" to message. message_chat reaches \"parent\" (the chat that opened this one"
                        + (store.Get(fromId) == null ? " — this chat has none" : "") + ") and the chats this one split off"
                        + (kids.Count == 0 ? " (none)" : ": " + string.Join(", ", kids.Select(k => $"{ChatRef.Of(k.Id)} \"{Sessions.FirstOrDefault(c => c.Id == k.Id)?.Title ?? k.Link.Task}\""))) + ".");
            }
            else toId = hit.Id;
        }
        var to = Sessions.FirstOrDefault(s => s.Id == toId)
            ?? throw new InvalidOperationException("That chat is gone (deleted), so it cannot be messaged.");
        if (store.Get(toId)?.ArchivedAt != null)
            throw new InvalidOperationException($"\"{to.Title}\" was put away. Bring it back first (the user can), or do the work here.");
        if (!_chatMessages.TryTake(fromId + ">" + toId, DateTime.Now))
            throw new InvalidOperationException($"This chat already sent {_chatMessages.MaxWakes} messages to \"{to.Title}\" in the last "
                + $"{_chatMessages.Window.TotalMinutes:0} minutes. Stop the back-and-forth: finish here, or say in your reply what is still open.");

        string fromTitle = string.IsNullOrWhiteSpace(from.Title) ? ChatRef.Of(fromId) : from.Title;
        string toTitle = string.IsNullOrWhiteSpace(to.Title) ? ChatRef.Of(toId) : to.Title;
        string shown = LocF(toParent || sibling ? "chat.msg.fromSplit" : "chat.msg.fromParent", fromTitle, SplitReports.Clip(message, SplitReports.ShownChars, oneLine: true));
        string forModel = NoticePrefix + SplitReports.MessageForModel(!toParent && !sibling, fromTitle, ChatRef.Of(fromId), fromId, message, sibling);
        string after = toParent
            ? "Its answer arrives here as a message — end your turn now if you cannot go on without it."
            : sibling ? "It is that chat's work now: its report goes to the chat that opened you both, not here — carry on here or end your turn."
            : "Its report comes back here by itself when it ends its turn — do not poll; carry on here or end your turn.";
        var run = RunOf(to);
        var d = SplitReports.DecideMessage(run != null, _lastTurnEnd.TryGetValue(toId, out var e) && e.Stopped, _splitWakes.WouldAllow(toId, DateTime.Now));
        _debugLog?.Info("Chat", $"message_chat {fromId} → {toId}: {d.Delivery} ({d.Why}) · {message.Length} chars");
        switch (d.Delivery)
        {
            case SplitReports.Delivery.NoteToRun:
                run!.Messages.Add(Notice(shown, forModel[NoticePrefix.Length..]));
                run.Scope?.AddUserNote(forModel, new SplitNoteTag(fromId));
                return $"Delivered to \"{toTitle}\" (ref {ChatRef.Of(toId)}), which is working: it reads it at its next step. {after}";
            case SplitReports.Delivery.Wake:
                _splitWakes.TryTake(toId, DateTime.Now);
                _agentDrivenNext.Add(toId);
                Detached(() => StartInternalTurnAsync(toId, shown + "\n\n" + forModel));
                return $"Started a turn in \"{toTitle}\" (ref {ChatRef.Of(toId)}) with your message (it was idle). {after}";
            default:
                PostTurnMessage(to, Notice(shown + (d.Why == SplitReports.Reason.TargetStopped ? "\n" + Loc("chat.msg.leftForYou") : ""),
                    forModel[NoticePrefix.Length..]));
                return d.Why == SplitReports.Reason.TargetStopped
                    ? $"Not started: the user stopped \"{toTitle}\"'s last turn, so it is the user's now. The message is left in it (its next turn reads it). Carry on here, or tell the user."
                    : $"Not started: \"{toTitle}\" already had {_splitWakes.MaxWakes} turns started for it within {_splitWakes.Window.TotalHours:0} hours. The message is left in it; carry on here, or tell the user.";
        }
    }

    // ─── close_chat read ───

    /// <summary>close_chat read "true" (UI thread): the whole last answer of one of <paramref name="fromId"/>'s split chats.</summary>
    private string ReadSplitForAgent(string? fromId, string target)
    {
        if (fromId == null) return "No chat is asking (close_chat works from a chat that split work off with new_chat).";
        var store = ChatLinkStore.Current;
        var kids = store?.ChildrenOf(fromId) ?? new();
        target = (target ?? "").Trim().Trim('"', '\'');
        var hit = kids.FirstOrDefault(k => k.Id == target || ChatRef.Matches(k.Id, target)
            || string.Equals(Sessions.FirstOrDefault(c => c.Id == k.Id)?.Title, target, StringComparison.OrdinalIgnoreCase));
        if (hit.Id == null)
            return kids.Count == 0 ? "This chat has not split off any chats (new_chat), so there is nothing to read."
                : $"No split chat of this chat matches \"{target}\": " + string.Join(", ", kids.Select(k => $"{ChatRef.Of(k.Id)} \"{Sessions.FirstOrDefault(c => c.Id == k.Id)?.Title ?? k.Link.Task}\""));
        var chat = Sessions.FirstOrDefault(c => c.Id == hit.Id);
        var run = chat == null ? null : RunOf(chat);
        IEnumerable<ChatMessage>? rows = run != null ? run.Messages
            : CurrentSession?.Id == hit.Id ? Messages
            : chat is { Messages.Count: > 0 } ? chat.Messages
            : _persistenceService.LoadSession(hit.Id)?.Messages;
        string state = hit.Link.ArchivedAt != null ? "put away" : run != null ? "working (this is its last answer so far)" : "finished";
        string answer = SplitReports.Clip(AnswerOf(rows, failed: false), SplitReports.ReadChars);
        string tree = store?.WorktreeOf(hit.Id) is { } wt ? $"\nIts work is on branch {wt.Branch} (worktree {wt.Root})." : "";
        return $"Split chat \"{chat?.Title ?? "?"}\" (ref {ChatRef.Of(hit.Id)}, id {hit.Id}) — {hit.Link.Task} — {state}.{tree}\n"
             + (answer.Length > 0 ? "Its last answer:\n" + answer : "It has no answer yet.");
    }

    /// <summary>A short line of a finished split's last answer for close_chat's list (empty when none is loaded).</summary>
    private string ListGistOf(string chatId)
    {
        var chat = Sessions.FirstOrDefault(c => c.Id == chatId);
        IEnumerable<ChatMessage>? rows = CurrentSession?.Id == chatId ? Messages : chat is { Messages.Count: > 0 } ? chat.Messages : null;
        string gist = SplitReports.Clip(AnswerOf(rows, failed: false), 160, oneLine: true);
        return gist.Length == 0 ? "" : $" — last answer: {gist}";
    }
}
