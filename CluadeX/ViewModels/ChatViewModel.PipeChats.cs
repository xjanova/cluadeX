using System.IO;
using System.Windows;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>A chat as the pipe's cluadex.list_chats reports it.</summary>
public sealed record PipeChatInfo(string Id, string Title, string Project, DateTime UpdatedAt, bool Running, bool Paused);

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
        return await Task.Run(() => picked.Select(c => c.Paused is bool p ? c.Info(p)
            : c.Info(IsPausedReply(_persistenceService.LastAssistantReply(c.Id)))).ToList());
    }

    private sealed record ChatRow(string Id, string Title, string Project, DateTime UpdatedAt, bool Running, bool? Paused)
    {
        public PipeChatInfo Info(bool paused) => new(Id, Title, Project, UpdatedAt, Running, paused);
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
                rows.Add(new ChatRow(s.Id, s.Title ?? "", s.ProjectPath ?? "", s.UpdatedAt, run != null, paused));
            }
            return rows;
        });
        var byId = stored.ToDictionary(s => s.Id, s => new ChatRow(s.Id, s.Title ?? "", s.ProjectPath ?? "", s.UpdatedAt, false, null));
        foreach (var r in live) byId[r.Id] = r;
        return byId.Values.ToList();
    }

    /// <summary>
    /// cluadex.continue_chat: open the chat <paramref name="chatId"/> (or the newest one titled exactly
    /// <paramref name="title"/>, ignoring case) as a click in the sidebar does — which opens its project too — pick the
    /// caller's model for it when one is given (choose_model's rules), send <paramref name="message"/> as the user the
    /// way Send does, wait for that turn and return its reply. An error outcome when the chat does not exist, is
    /// already running, the message started no reply, the turn failed or did not finish, or was stopped / cancelled.
    /// </summary>
    public async Task<McpTaskOutcome> ContinueChatForPipeAsync(string? chatId, string? title, string message,
        int? maxSteps, string? provider, string? model, CancellationToken ct)
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
