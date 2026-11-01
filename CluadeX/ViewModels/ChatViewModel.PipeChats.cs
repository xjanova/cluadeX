using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>A chat as the pipe's cluadex.list_chats reports it.</summary>
public sealed record PipeChatInfo(string Id, string Title, string Project, DateTime UpdatedAt, bool Running, bool Paused)
{
    /// <summary>"waiting_consent", "running", "paused" or "idle".</summary>
    public string Activity { get; init; } = "idle";
    /// <summary>What it waits for the owner on (a permission card, a question, write_code's Accept-edits dialog).</summary>
    public string? WaitingFor { get; init; }
    /// <summary>The step it is on, as the sidebar shows it.</summary>
    public string? Doing { get; init; }
}

/// <summary>A sub-agent as the pipe reports it.</summary>
public sealed record PipeAgentInfo(string Label, string Kind, string Task, string State, string Action, string Model, int ToolCalls, decimal CostUsd);

/// <summary>What a run used: kept when it ends, so a report after the turn still has its numbers.</summary>
public sealed record PipeRunStats(string StartedBy, string Provider, string? Model, int Step, long InputTokens, long OutputTokens,
    long CacheReadTokens, long CacheWriteTokens, decimal CostUsd);

/// <summary>cluadex.task_status: a chat as it stands now. Stats = null: no run of it since the app started.</summary>
public sealed record PipeTaskStatus(string ChatId, string Title, string Project, string? TaskId, string Activity, string? WaitingFor,
    string? Doing, PipeRunStats? Stats, IReadOnlyList<PipeAgentInfo> SubAgents, string? LastReply, McpTaskOutcome? Outcome);

/// <summary>
/// Carrying on an existing chat over the cluadex-mcp pipe. An orchestrator could only START chats (cluadex.write_code);
/// to go on with a task in its own chat ("hive-t2" paused at step 101) it scripted mouse clicks on the sidebar.
/// cluadex.list_chats finds the chat, cluadex.continue_chat opens it (as a click in the sidebar does) and sends the
/// message (as Send does), then waits for that turn and returns its reply.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>A message a pipe caller sent through Send: its cancellation and step limit in, its run out.</summary>
    private sealed class PipeTurn
    {
        public PipeTurn(CancellationToken ct, int? maxSteps) { Ct = ct; MaxSteps = maxSteps; }
        public CancellationToken Ct { get; }
        public int? MaxSteps { get; }
        /// <summary>The turn's run, once it started (UI thread). Null afterwards = the message started no turn.</summary>
        public ChatRun? Run;
        public ChatMessage? UserMessage;
    }

    /// <summary>Chats a continue_chat is sending into right now (UI thread): a second call for the same chat is refused
    /// before the first one's turn has begun.</summary>
    private readonly HashSet<string> _pipeSendingChats = new();

    /// <summary>A pipe task's chat, and how its pipe call ended (null = still going).</summary>
    private sealed record PipeTask(string ChatId, McpTaskOutcome? Outcome);
    private readonly ConcurrentDictionary<string, PipeTask> _pipeTasks = new(StringComparer.Ordinal);

    /// <summary>write_code's Accept-edits dialog: the task has no chat yet, and the call just hangs while it is up.</summary>
    private readonly ConcurrentDictionary<string, string> _pipeConsents = new(StringComparer.Ordinal);

    /// <summary>What each chat's last run used (UI thread). Dropped oldest first past the limit.</summary>
    private readonly Dictionary<string, PipeRunStats> _lastRunStats = new();
    private readonly Queue<string> _lastRunOrder = new();
    private const int MaxLastRunStats = 200;

    private static PipeRunStats StatsOf(AgentRunScope s) => new(s.FromPipe ? "pipe" : "user", s.ProviderType?.ToString() ?? "",
        s.ProviderType is { } p ? LiveModelReport.For(s, p)?.Model : null, s.Step, s.InputTokens, s.OutputTokens, s.CacheReadTokens, s.CacheWriteTokens, s.CostUsd);

    /// <summary>A run ended: what it used stays readable (UI thread).</summary>
    private void RememberRunStats(ChatRun run)
    {
        string id = run.Session.Id;
        if (!_lastRunStats.ContainsKey(id)) _lastRunOrder.Enqueue(id);
        _lastRunStats[id] = StatsOf(run.Scope);
        while (_lastRunOrder.Count > MaxLastRunStats) _lastRunStats.Remove(_lastRunOrder.Dequeue());
    }

    private static string? OpenPrompt(IEnumerable<ChatMessage> list)
    {
        var open = list.LastOrDefault(m => m.Role is MessageRole.PermissionRequest && !m.PermissionAnswered
                                           || m.Role == MessageRole.Question && !m.QuestionAnswered);
        if (open == null) return null;
        string text = open.Role == MessageRole.PermissionRequest ? open.PermissionHeadlineText : open.Content ?? "";
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    private (string Activity, string? Waiting, string? Doing) ActivityOf(ChatRun? run, bool paused)
    {
        if (run == null) return (paused ? "paused" : "idle", null, null);
        string? waiting = OpenPrompt(run.Messages);
        if (waiting != null) return ("waiting_consent", waiting, null);
        string doing = (run.ActiveStatus?.Content ?? "").TrimStart('⏵', ' ');
        return ("running", null, doing.Length > 0 ? doing : run.StatusBase);
    }

    /// <summary>The built-in commands that act on the chat and start no reply.</summary>
    private static readonly string[] ChatCommands = ["/compact", "/clear", "/close", "/exit", "/new", "/login"];

    /// <summary>
    /// cluadex.list_chats: stored chats and the ones only in memory (a chat not saved yet, a running chat's live list),
    /// newest first. <paramref name="project"/>: only chats of that folder; <paramref name="query"/>: a case-insensitive
    /// part of the title. Read-only: nothing on screen changes.
    /// </summary>
    public async Task<List<PipeChatInfo>> ListChatsForPipeAsync(string? project, string? query, int limit)
    {
        var all = await AllChatsForPipeAsync();
        string? folder = string.IsNullOrWhiteSpace(project) ? null : NormalisePath(project);
        string q = query?.Trim() ?? "";
        var picked = all
            .Where(c => folder == null || (c.Project.Length > 0 && PathEquals(NormalisePath(c.Project), folder!)))
            .Where(c => q.Length == 0 || c.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.UpdatedAt)
            .Take(Math.Max(1, limit))
            .ToList();
        // Paused needs the last reply: the chats with no live list read it from the database (off the UI thread).
        var rows = await Task.Run(() => picked.Select(c => c.Paused is bool p ? c.Info(p)
            : c.Info(IsPausedReply(_persistenceService.LastAssistantReply(c.Id)))).ToList());
        // A task held at its Accept-edits dialog has no chat yet: without its row the caller sees nothing at all.
        if (folder == null)
            rows.InsertRange(0, _pipeConsents.Where(c => q.Length == 0 || c.Key.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(c => new PipeChatInfo("", c.Key, c.Value, DateTime.Now, false, false)
                {
                    Activity = "waiting_consent",
                    WaitingFor = $"Accept edits in {c.Value}",
                }));
        return rows;
    }

    private sealed record ChatRow(string Id, string Title, string Project, DateTime UpdatedAt, bool Running, bool? Paused)
    {
        public string Activity { get; init; } = "";
        public string? WaitingFor { get; init; }
        public string? Doing { get; init; }
        public PipeChatInfo Info(bool paused) => new(Id, Title, Project, UpdatedAt, Running, paused)
        {
            Activity = Activity.Length > 0 ? Activity : paused ? "paused" : "idle",
            WaitingFor = WaitingFor,
            Doing = Doing,
        };
    }

    private static bool IsPausedReply(string? content) =>
        (content ?? "").StartsWith(AgentRunScope.PausedMarker, StringComparison.Ordinal);

    private static string NormalisePath(string p)
    {
        try { return Path.GetFullPath(p.Trim()).TrimEnd('\\', '/'); }
        catch { return p.Trim().TrimEnd('\\', '/'); }
    }

    /// <summary>Every chat: the database's list, with what the window holds in memory laid over it.</summary>
    private async Task<List<ChatRow>> AllChatsForPipeAsync()
    {
        var stored = await Task.Run(() => _persistenceService.LoadSessionList());
        var live = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var rows = new List<ChatRow>();
            foreach (var s in Sessions.Concat(_runs.Select(r => r.Session)))
            {
                if (rows.Any(r => r.Id == s.Id)) continue;
                var run = RunOf(s);
                // The list that holds the chat now: a run's live list, the chat on screen, or a loaded copy.
                IEnumerable<ChatMessage>? list = run != null ? run.Messages
                    : CurrentSession?.Id == s.Id ? Messages
                    : s.Messages.Count > 0 ? s.Messages : null;
                bool? paused = list == null ? null : IsPausedReply(list.LastOrDefault(m => m.Role == MessageRole.Assistant)?.Content);
                var (activity, waiting, doing) = ActivityOf(run, paused == true);
                rows.Add(new ChatRow(s.Id, s.Title ?? "", s.ProjectPath ?? "", s.UpdatedAt, run != null, paused)
                {
                    Activity = run != null ? activity : "",
                    WaitingFor = waiting,
                    Doing = doing,
                });
            }
            return rows;
        });
        var byId = stored.ToDictionary(s => s.Id, s => new ChatRow(s.Id, s.Title ?? "", s.ProjectPath ?? "", s.UpdatedAt, false, null));
        foreach (var r in live) byId[r.Id] = r;
        return byId.Values.ToList();
    }

    /// <summary>
    /// cluadex.task_status: how a chat stands, found by <paramref name="chatId"/> or by the task that started it. A turn
    /// that ended after write_code returned is here too: its reply is the chat's last one. Read-only. Null = no such chat.
    /// </summary>
    public async Task<PipeTaskStatus?> TaskStatusForPipeAsync(string? taskId, string? chatId)
    {
        string? id = chatId?.Trim();
        string? task = taskId?.Trim();
        var record = !string.IsNullOrEmpty(task) && _pipeTasks.TryGetValue(task, out var t) ? t : null;
        id = string.IsNullOrEmpty(id) ? record?.ChatId : id;
        var all = await AllChatsForPipeAsync();
        // A task from before the app restarted: its chat carries the task's id as its title.
        var chat = !string.IsNullOrEmpty(id) ? all.FirstOrDefault(c => c.Id == id)
            : string.IsNullOrEmpty(task) ? null
            : all.Where(c => c.Title == task).OrderByDescending(c => c.UpdatedAt).FirstOrDefault();
        if (chat == null)
        {
            // Held at its Accept-edits dialog, or refused before it got a chat.
            if (!string.IsNullOrEmpty(task) && _pipeConsents.TryGetValue(task, out var folder))
                return new PipeTaskStatus("", task, folder, task, "waiting_consent", $"Accept edits in {folder}", null, null, Array.Empty<PipeAgentInfo>(), null, null);
            return null;
        }
        // A chat not in a run: its rows come from the database, off the UI thread.
        var stored = chat.Running ? null : await Task.Run(() => _persistenceService.LoadSession(chat.Id));
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var run = _runs.FirstOrDefault(r => r.Session.Id == chat.Id);
            IEnumerable<ChatMessage> list = run != null ? run.Messages
                : CurrentSession?.Id == chat.Id ? Messages
                : stored?.Messages ?? Enumerable.Empty<ChatMessage>();
            var rows = list.ToList();
            string? reply = rows.LastOrDefault(m => m.Role == MessageRole.Assistant)?.Content?.Trim();
            var map = run?.AgentMapMsg ?? rows.LastOrDefault(m => m.IsAgentMap);
            var agents = map?.AgentNodes?.Select(n => new PipeAgentInfo(n.Label, n.Kind, n.Task, n.State, n.Action, n.Model, n.ToolCalls, n.CostUsd)).ToList()
                ?? new List<PipeAgentInfo>();
            var (activity, waiting, doing) = ActivityOf(run, IsPausedReply(reply));
            var stats = run != null ? StatsOf(run.Scope) : _lastRunStats.GetValueOrDefault(chat.Id);
            return new PipeTaskStatus(chat.Id, chat.Title, chat.Project, task, activity, waiting, doing, stats, agents, reply, record?.Outcome);
        });
    }

    /// <summary>
    /// cluadex.continue_chat: send <paramref name="message"/> into the chat <paramref name="chatId"/> (or the newest one
    /// titled exactly <paramref name="title"/>, ignoring case), wait for that turn and return its reply. The window is left
    /// alone: the turn runs in the chat's own list, as a task does while the owner works elsewhere. With
    /// <paramref name="focus"/> (or when the chat is the one on screen) it is opened as a click in the sidebar does — which
    /// opens its project too — the caller's model is picked (choose_model's rules) and the message goes through Send.
    /// An error outcome when the chat does not exist, is already running, the message started no reply, the turn failed
    /// or did not finish, or was stopped / cancelled.
    /// </summary>
    public async Task<McpTaskOutcome> ContinueChatForPipeAsync(string? chatId, string? title, string message,
        int? maxSteps, string? provider, string? model, CancellationToken ct, bool focus = false)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new McpTaskOutcome("'message' is required", IsError: true);
        ct.ThrowIfCancellationRequested();

        var all = await AllChatsForPipeAsync();
        ChatRow? chat = !string.IsNullOrWhiteSpace(chatId)
            ? all.FirstOrDefault(c => c.Id == chatId.Trim())
            : all.Where(c => string.Equals(c.Title.Trim(), title?.Trim(), StringComparison.OrdinalIgnoreCase))
                 .OrderByDescending(c => c.UpdatedAt).FirstOrDefault();
        if (chat == null)
            return new McpTaskOutcome(!string.IsNullOrWhiteSpace(chatId)
                ? $"No chat with id '{chatId.Trim()}' — cluadex.list_chats lists them."
                : $"No chat titled '{title?.Trim()}' — cluadex.list_chats lists them.", IsError: true);

        var dispatcher = Application.Current.Dispatcher;
        string text = message.Trim();
        bool claimed = await dispatcher.InvokeAsync(() =>
            !_runs.Any(r => r.Session.Id == chat.Id) && _pipeSendingChats.Add(chat.Id));
        if (!claimed)
            return new McpTaskOutcome($"The chat '{chat.Title}' ({chat.Id}) is already running — wait for its turn to end, or stop it in CluadeX.", IsError: true);
        _debugLog?.Info("Chat", $"pipe continue_chat {chat.Id}: opening it and sending a message ({text.Length} chars)");

        try
        {
            if (!focus && !await dispatcher.InvokeAsync(() => CurrentSession?.Id == chat.Id))
                return await ContinueOffScreenAsync(chat, text, maxSteps, model, ct);
            return await await dispatcher.InvokeAsync(async () =>
            {
                // 1. Open it, as a click in the sidebar does (that also opens the chat's project).
                // The pipe opened it, not the user: a take of the screen, not a sign someone is at the keyboard.
                var userActivity = _lastUserActivityUtc;
                await LoadSessionAsync(chat.Id);
                _lastUserActivityUtc = userActivity;
                _lastScreenTakeUtc = DateTime.UtcNow;
                if (CurrentSession?.Id != chat.Id)
                    return new McpTaskOutcome($"The chat '{chat.Title}' could not be opened: {StatusText}", IsError: true);

                // 2. The caller's model for it, by the model box's rules.
                if (!string.IsNullOrWhiteSpace(model))
                {
                    string? why = await ChooseFromOutsideAsync(model, null, provider);
                    if (why != null)
                        return new McpTaskOutcome($"The model was not picked, nothing was sent: {why}", IsError: true);
                    if (CurrentSession?.Id != chat.Id)
                        return new McpTaskOutcome("Another chat was opened while the model was being picked — nothing was sent.", IsError: true);
                }
                if (RunOf(CurrentSession) != null)
                    return new McpTaskOutcome($"The chat '{chat.Title}' is already running — nothing was sent.", IsError: true);
                ct.ThrowIfCancellationRequested();

                // 3. Send, as the Send button does — and wait for that turn.
                var sentInto = Messages;
                int rowsBefore = sentInto.Count;
                var pipe = new PipeTurn(ct, maxSteps);
                await SendMessageCore(text, pipe);
                return PipeTurnOutcome(pipe, text, sentInto, rowsBefore);
            });
        }
        finally
        {
            try { await dispatcher.InvokeAsync(() => _pipeSendingChats.Remove(chat.Id)); }
            catch { /* app closing */ }
        }
    }

    /// <summary>
    /// A turn in a chat that is not on screen (owner 2026-11-01: continue_chat took the chat the owner had open). The chat's
    /// rows go into a list of their own and the run writes there, the way a task runs beside the owner's work. Opening
    /// the chat meanwhile shows the run. A slash command acts on the chat on screen, and a model pick on the model box the
    /// owner is using: both need the caller's focus.
    /// </summary>
    private async Task<McpTaskOutcome> ContinueOffScreenAsync(ChatRow chat, string text, int? maxSteps, string? model, CancellationToken ct)
    {
        if (text.StartsWith('/'))
            return new McpTaskOutcome("A slash command acts on the chat on screen, nothing was sent: pass focus: true to open it.", IsError: true);
        if (!string.IsNullOrWhiteSpace(model))
            return new McpTaskOutcome("Picking a model changes the model box the owner is using, nothing was sent: pass focus: true to open the chat and pick it.", IsError: true);

        var stored = await Task.Run(() => _persistenceService.LoadSession(chat.Id));
        var dispatcher = Application.Current.Dispatcher;
        TaskCompletionSource? turn = null;
        ChatRun? run = null;
        ChatMessage? sent = null;
        CancellationTokenSource? linked = null;
        ObservableCollection<ChatMessage> rows = new();
        List<ChatMessage> history = new();
        string? refused = await dispatcher.InvokeAsync(() =>
        {
            var session = Sessions.FirstOrDefault(s => s.Id == chat.Id) ?? stored;
            if (session == null) return $"The chat '{chat.Title}' could not be read, nothing was sent.";
            if (RunOf(session) != null) return $"The chat '{chat.Title}' is already running, nothing was sent.";
            if (CurrentSession?.Id == chat.Id) return $"The chat '{chat.Title}' was opened just now, nothing was sent: call again to send it there.";
            string folder = string.IsNullOrWhiteSpace(session.ProjectPath) ? _fileSystemService.AppWorkingDirectory : session.ProjectPath;
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) return $"The chat's folder {folder} is gone, nothing was sent.";
            rows = new ObservableCollection<ChatMessage>(stored?.Messages ?? session.Messages);
            sent = new ChatMessage { Role = MessageRole.User, Content = text };
            rows.Add(sent);
            turn = BeginTurnFor(session, rows, folder);
            run = RunOf(session);
            if (run == null) return "The turn did not start, nothing was sent.";
            run.Scope.FromPipe = true;
            run.MaxSteps = maxSteps;
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            run.Cts = linked;
            history = BuildModelHistory(rows, before: sent, includeTools: true);
            RefreshSessionBadges();
            return null;
        });
        if (refused != null) return new McpTaskOutcome(refused, IsError: true);

        AgentRunScope.Enter(run!.Scope);
        var live = new LiveRunView(this, run.Session, run.Scope);
        live.Attach(_agentService);
        try
        {
            Exception? failure = null;
            try { run.LoopResult = await Task.Run(() => RunAgentLoopAsync(history, text, maxSteps, linked!.Token), CancellationToken.None); }
            catch (OperationCanceledException) { /* the stop line below */ }
            catch (Exception ex) { failure = ex; }

            await dispatcher.InvokeAsync(() =>
            {
                if (failure != null)
                {
                    _debugLog?.Error("Chat", $"pipe continue_chat {chat.Id}: agent loop failed", failure);
                    live.Close();
                    rows.Add(new ChatMessage { Role = MessageRole.Assistant, Content = $"❌ {Loc("chat.err.agentLoop")}", HasError = true });
                }
                else if (run.LoopResult is not { } result || linked!.IsCancellationRequested)
                {
                    live.Close();
                    rows.Add(new ChatMessage
                    {
                        Role = MessageRole.System,
                        Content = $"*{Loc(ct.IsCancellationRequested ? "chat.pipe.callerGone" : "chat.interruptedNote")}*",
                        IsInterrupted = true,
                    });
                }
                else
                {
                    string? reply = string.IsNullOrWhiteSpace(result.FinalResponse) ? null
                        : StripThinkingSpans(_agentToolService.StripToolCalls(result.FinalResponse));
                    live.Finish(string.IsNullOrWhiteSpace(reply) ? null : reply, result.Steps?.Sum(s => s.ToolResults?.Count ?? 0) ?? 0);
                }
                // Opened while it ran: the turn's end saves only a chat that is off screen.
                if (ReferenceEquals(run.Session, CurrentSession))
                {
                    _isDirty = true;
                    SaveNow();
                }
            });
        }
        finally
        {
            live.Detach(_agentService);
            AgentRunScope.Enter(null);
            try
            {
                await dispatcher.InvokeAsync(() =>
                {
                    live.Close();
                    if (ReferenceEquals(run.Cts, linked)) run.Cts = null;
                    linked?.Dispose();
                    EndTurn(turn!);
                });
            }
            catch { /* app closing */ }
        }
        var pipe = new PipeTurn(ct, maxSteps) { Run = run, UserMessage = sent };
        return await dispatcher.InvokeAsync(() => PipeTurnOutcome(pipe, text, rows, 0));
    }

    /// <summary>What the caller gets for a continue_chat turn (UI thread, after the turn ended) — write_code's rules:
    /// failed, unfinished, stopped and cancelled are errors.</summary>
    private McpTaskOutcome PipeTurnOutcome(PipeTurn pipe, string text, IList<ChatMessage> sentInto, int rowsBefore)
    {
        var run = pipe.Run;
        if (run == null)
        {
            // No turn: a chat command (it did what it does), or the message was refused before a reply started.
            if (ChatCommands.Any(c => text.Equals(c, StringComparison.OrdinalIgnoreCase)))
                return new McpTaskOutcome($"Done: {text} acts on the chat and starts no reply. {StatusText}".TrimEnd(), IsError: false);
            var refusal = sentInto.Skip(Math.Min(rowsBefore, sentInto.Count)).LastOrDefault(m => m.Role == MessageRole.Assistant && m.HasError);
            return new McpTaskOutcome($"No reply was started: {(refusal?.Content ?? StatusText)}", IsError: true);
        }

        var rows = run.Messages.ToList();
        int at = pipe.UserMessage == null ? -1 : rows.IndexOf(pipe.UserMessage);
        var after = rows.Skip(at + 1).ToList();
        var last = after.LastOrDefault(m => m.Role == MessageRole.Assistant);
        string reply = last?.Content?.Trim() ?? "";
        string Notes(string s) => pipe.MaxSteps != null && run.LoopResult == null
            ? $"{s}\n\n(maxSteps was not applied: this chat's reply did not run the agent loop.)"
            : s;

        if (pipe.Ct.IsCancellationRequested)
            return new McpTaskOutcome(Notes("Cancelled: the caller disconnected or cancelled the request before the turn finished."), IsError: true);
        if (after.Any(m => m.IsInterrupted) && (last == null || last.IsInterrupted))
            return new McpTaskOutcome(Notes("Stopped by the user in CluadeX before the turn finished."
                + (reply.Length > 0 ? $"\n\nWhat it said so far:\n{reply}" : "")), IsError: true);
        if (last == null)
            return new McpTaskOutcome(Notes("(the turn ended without a reply)"), IsError: true);
        if (last.HasError)
            return new McpTaskOutcome(Notes(reply), IsError: true);
        if (run.LoopResult is { Success: false } loop)
            return new McpTaskOutcome(Notes($"{reply}\n\n(The run did not finish: {loop.StopReason}.)"), IsError: true);
        return new McpTaskOutcome(Notes(reply), IsError: false);
    }
}
