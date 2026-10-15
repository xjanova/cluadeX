using CluadeX.Models;
using CluadeX.Services.Batch;

namespace CluadeX.ViewModels;

/// <summary>
/// Batch mode in the chat: a question sent with the Batch switch on goes through the provider's batch API (half
/// price) instead of the live agent loop, and its answer is added to the chat it came from when the batch ends —
/// minutes usually, 24 hours at most, even across a restart. A batch request is single-shot, so it has no tools:
/// it suits questions, reviews and write-ups about what is already in the conversation, not edits.
/// </summary>
public partial class ChatViewModel
{
    private BatchService? _batchService;
    private readonly Services.Pet.PetService? _pet;

    /// <summary>History a batch question carries: the newest turns, at most this many characters.</summary>
    private const int BatchHistoryMaxChars = 120_000;

    private void InitBatch(BatchService? batchService)
    {
        _batchService = batchService;
        if (batchService == null) return;
        batchService.RegisterHandler(BatchKinds.Chat, DeliverChatBatchAsync);
        batchService.JobsChanged += () => App.Current?.Dispatcher.BeginInvoke(RefreshBatchState);
        _providerManager.OnProviderChanged += _ => App.Current?.Dispatcher.BeginInvoke(RefreshBatchState);
        _providerManager.OnStatusChanged += _ => App.Current?.Dispatcher.BeginInvoke(RefreshBatchState);
        RefreshBatchState();
    }

    /// <summary>The active provider has a batch API as configured (Anthropic, OpenAI's own endpoint).</summary>
    public bool CanUseBatch => _batchService?.CanBatchNow == true;

    private bool _batchMode;
    /// <summary>Send the next messages through the batch API. Session-only, off by default — left on by accident
    /// it would turn every question into a wait.</summary>
    public bool BatchMode
    {
        get => _batchMode && CanUseBatch;
        set => SetProperty(ref _batchMode, value);
    }

    private string _batchPendingText = "";
    /// <summary>"📨 2" while batch answers are still out; empty otherwise.</summary>
    public string BatchPendingText { get => _batchPendingText; private set => SetProperty(ref _batchPendingText, value); }

    private void RefreshBatchState()
    {
        OnPropertyChanged(nameof(CanUseBatch));
        OnPropertyChanged(nameof(BatchMode));
        int open = _batchService?.Jobs.Count(j => j.IsOpen) ?? 0;
        BatchPendingText = open > 0 ? LocF("chat.batch.pending", open) : "";
    }

    /// <summary>The system prompt of a batch question: it has no tools, and must say so instead of pretending to
    /// look at files. Public for the contract harness.</summary>
    public static string BatchChatSystemPrompt(string? projectPath) =>
        $"""
        You are CluadeX, an AI coding assistant. This reply goes through the batch API: in this turn you have no tools —
        you cannot read files, run commands, edit anything or browse. Answer from the conversation so far and your own
        knowledge. When the answer depends on code you have not seen, say which files to look at and what to check,
        rather than guessing their contents.{(string.IsNullOrWhiteSpace(projectPath) ? "" : $"\nThe user's project folder: {projectPath}")}
        Reply in the language the user writes in.
        """;

    /// <summary>The conversation a batch question carries: what the model would get live, without tool rows, cut
    /// from the oldest end to <see cref="BatchHistoryMaxChars"/>. Public for the contract harness.</summary>
    public static List<ChatMessage> BatchHistory(IEnumerable<ChatMessage> transcript, ChatMessage? before)
    {
        var history = BuildModelHistory(transcript, before, includeTools: false);
        int total = 0, keepFrom = history.Count;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            total += history[i].Content?.Length ?? 0;
            if (total > BatchHistoryMaxChars) break;
            keepFrom = i;
        }
        return history.Skip(keepFrom).ToList();
    }

    /// <summary>Send <paramref name="input"/> as a batch question from the open chat.</summary>
    private async Task SendAsBatchAsync(string input)
    {
        var session = CurrentSession!;
        _pet?.NoteMessage(input);
        var userMsg = new ChatMessage { Role = MessageRole.User, Content = input };
        Messages.Add(userMsg);
        UserInput = string.Empty;
        var history = BatchHistory(Messages, before: userMsg);
        SaveNow();   // the answer is delivered to this chat by id: it must exist in the database

        CanSend = false;
        StatusText = Loc("chat.batch.sending");
        try
        {
            string? project = _fileSystemService.HasWorkingDirectory ? _fileSystemService.WorkingDirectory : null;
            var job = await _batchService!.SubmitAsync(BatchKinds.Chat, Shorten(input, 80),
                [new BatchRequest("answer", BatchChatSystemPrompt(project), history, input)],
                sessionId: session.Id, projectPath: project);
            PostTurnMessage(session, new ChatMessage { Role = MessageRole.System, Content = "📨 " + LocF("chat.batch.sent", job.Model) });
            StatusText = Loc("chat.batch.sentStatus");
        }
        catch (Exception ex)
        {
            _debugLog?.Warn("Batch", "submitting a chat batch failed", ex);
            PostTurnMessage(session, new ChatMessage
            {
                Role = MessageRole.Assistant,
                Content = "⚠ " + LocF("chat.batch.sendFailed", SanitizeErrorMessage(ex.Message)),
                HasError = true,
            });
            StatusText = Loc("chat.batch.sendFailedStatus");
        }
        finally
        {
            CanSend = true;
            SaveNow();
            ScrollToBottom?.Invoke();
        }
    }

    private Task<bool> DeliverChatBatchAsync(BatchJob job)
    {
        var app = App.Current;
        if (app == null) return Task.FromResult(false);   // shutting down: deliver after the next start
        return app.Dispatcher.InvokeAsync(() => DeliverChatBatch(job)).Task;
    }

    /// <summary>Add a batch's answer to the chat it came from (UI thread). False = try again later.</summary>
    private bool DeliverChatBatch(BatchJob job)
    {
        if (string.IsNullOrEmpty(job.SessionId)) return true;
        var replies = (job.Results ?? []).Select(r => r.Ok
            ? new ChatMessage { Role = MessageRole.Assistant, Content = $"> 📨 {LocF("chat.batch.answerTo", job.Title)}\n\n{r.Text}" }
            : new ChatMessage { Role = MessageRole.Assistant, Content = "⚠ " + LocF("chat.batch.itemFailed", job.Title, r.Error ?? "?"), HasError = true })
            .ToList();
        if (replies.Count == 0)
            replies.Add(new ChatMessage { Role = MessageRole.Assistant, Content = "⚠ " + LocF("chat.batch.itemFailed", job.Title, job.Detail), HasError = true });

        if (CurrentSession?.Id == job.SessionId)
        {
            // Not into a reply that is still being written: the next poll tries again.
            if (IsGenerating) return false;
            foreach (var m in replies) Messages.Add(m);
            SaveNow();
            ScrollToBottom?.Invoke();
            StatusText = Loc("chat.batch.arrived");
            return true;
        }

        var session = _persistenceService.LoadSession(job.SessionId);
        if (session == null)
        {
            // Deleted since: nowhere to put it (Settings ▸ Batch jobs still shows the answer). A chat whose rows
            // exist but did not load (busy or damaged database) is retried instead of losing the answer.
            return !_persistenceService.SessionHasMessages(job.SessionId);
        }
        session.Messages.AddRange(replies);
        session.UpdatedAt = DateTime.Now;
        _persistenceService.SaveSession(session);
        StatusText = LocF("chat.batch.arrivedOther", session.Title);
        return true;
    }

    private static string Shorten(string s, int max)
    {
        s = (s ?? "").Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
