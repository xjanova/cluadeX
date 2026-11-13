using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Agents;

namespace CluadeX.ViewModels;

/// <summary>
/// Running something in a new chat instead of the chat on screen (Services/Agents/NewChatAdvice.cs has the why and the
/// three places). Everything here is the owner's call: a button on a queued message, and two cards that ask.
/// <para>The new chat is the owner's own, not a split chat: same folder, same project, same model as the chat it came
/// from, no report back and no waking of that chat. Its first turn starts off screen; the sidebar shows it working.</para>
/// </summary>
public partial class ChatViewModel
{
    /// <summary>The card that asks where to run (or where to use the model), while it waits. A second message sent
    /// meanwhile goes out as usual: one card at a time.</summary>
    private ChatMessage? _adviceCard;

    /// <summary>Chats where the owner answered "here": not asked again in this run of the app.</summary>
    private readonly HashSet<string> _adviceDeclined = new(StringComparer.Ordinal);

    private bool AdviceCardOpen => _adviceCard is { QuestionAnswered: false } card && Messages.Contains(card);

    // ─── a message waiting for the running reply ───

    /// <summary>"Do it in a new chat" on a message typed while the reply runs: it leaves the run's queue (the agent
    /// never reads it) and starts as the first message of a new chat.</summary>
    public ICommand MoveQueuedToNewChatCommand => _moveQueuedToNewChatCommand ??= new RelayCommand<ChatMessage>(MoveQueuedToNewChat);
    private ICommand? _moveQueuedToNewChatCommand;

    private void MoveQueuedToNewChat(ChatMessage? bubble)
    {
        if (bubble is not { IsQueuedNote: true } || string.IsNullOrWhiteSpace(bubble.Content)) return;
        var run = _runs.FirstOrDefault(r => r.Messages.Contains(bubble));
        // Read already (the row changes a moment later), or its run ended: it is that chat's now.
        if (run?.Scope == null || !run.Scope.WithdrawUserNote(bubble))
        {
            StatusText = Loc("chat.newChatAdvice.alreadyRead");
            return;
        }
        string text = bubble.Content.Trim();
        bubble.IsQueuedNote = false;
        if (bubble.QueueRow != null) run.Messages.Remove(bubble.QueueRow);
        run.Messages.Remove(bubble);
        var chat = OpenOwnTaskChat(text, run.Session);
        run.Messages.Add(Notice("↗ " + LocF("chat.newChatAdvice.moved", chat.Title)));
        StatusText = LocF("chat.newChatAdvice.moved", chat.Title);
        ScrollToBottom?.Invoke();
    }

    /// <summary>A new chat of the owner's with <paramref name="text"/> as its first message, started off screen: the
    /// folder, project and model of <paramref name="from"/>. UI thread.</summary>
    private ChatSession OpenOwnTaskChat(string text, ChatSession from)
    {
        string folder = WorktreeFolderOf(from)
            ?? RunOf(from)?.Scope?.WorkingDirectory
            ?? (string.IsNullOrWhiteSpace(from.ProjectPath) ? _fileSystemService.AppWorkingDirectory ?? "" : from.ProjectPath);
        var chat = new ChatSession
        {
            ProjectPath = folder,
            ProjectId = ProjectOf(from).Length > 0 ? from.ProjectId : FolderProjectId(folder),
        };
        chat.Title = FirstLineOf(text) ?? chat.Title;
        chat.Messages.Add(Notice("↗ " + LocF("chat.newChatAdvice.cameFrom", from.Title)));
        // The same model as the chat it came from (each chat keeps its own): not whatever is on screen when it starts.
        if (ChatModelStore.Get(from.Id) is { } pick && Enum.TryParse<AiProviderType>(pick.Provider, out var type))
            ChatModelStore.Set(chat.Id, type, pick.Model);
        Sessions.Insert(0, chat);
        _persistenceService.SaveSession(chat);
        RebuildVisibleSessions();
        _debugLog?.Info("Chat", $"new chat {chat.Id} for a task moved out of {from.Id} ({text.Length} chars) · {folder}");
        Detached(() => StartInternalTurnAsync(chat.Id, text));
        return chat;
    }

    // ─── a new task sent in a chat that is already long ───

    /// <summary>The message about to go out in the chat on screen: ask where to run it? (The setting, one card at a
    /// time, not in a chat where the owner already said "here".)</summary>
    private bool AdviseNewChat(string input, out int contextTokens)
    {
        contextTokens = 0;
        if (!_settingsService.Settings.SuggestNewChat || AdviceCardOpen || CurrentSession is not { } chat || _adviceDeclined.Contains(chat.Id)) return false;
        var rows = Messages.ToList();
        contextTokens = ChatContextTokens(rows);
        var earlier = rows.Where(m => m.Role == MessageRole.User && !string.IsNullOrWhiteSpace(m.Content)).TakeLast(8).Select(m => m.Content).ToList();
        return NewChatAdvice.ForBigChat(input, contextTokens, earlier);
    }

    private int ChatContextTokens(IReadOnlyList<ChatMessage> rows) =>
        ContextUsed(rows, CurrentSession?.LastMeasuredContext, _providerManager.ActiveProviderType, _contextMemoryService.CalculateTotalTokens).Used;

    /// <summary>
    /// The card for <see cref="AdviseNewChat"/>. True = the message was dealt with here (moved to a new chat, or sent on
    /// in a chat the owner left meanwhile): the caller sends nothing. False = send it in this chat as usual.
    /// </summary>
    private async Task<bool> AskWhereToRunAsync(string input, int contextTokens)
    {
        var asked = CurrentSession!;
        string optNew = Loc("chat.newChatAdvice.optNew"), optHere = Loc("chat.newChatAdvice.optHere");
        string shown = input.Length > 600 ? input[..600] + "…" : input;
        string? answer = await AskChoiceAsync(LocF("chat.newChatAdvice.question", contextTokens, shown), optNew, optHere);
        var rows = ReferenceEquals(CurrentSession, asked) ? Messages : null;
        if (string.Equals(answer, optNew, StringComparison.Ordinal))
        {
            var chat = OpenOwnTaskChat(input, asked);
            rows?.Add(Notice("↗ " + LocF("chat.newChatAdvice.moved", chat.Title)));
            StatusText = LocF("chat.newChatAdvice.moved", chat.Title);
            return true;
        }
        _adviceDeclined.Add(asked.Id);
        // Answered from a chat that is no longer on screen, or with a reply running there by now: it still goes to the
        // chat it was typed in.
        if (rows == null) { Detached(() => StartInternalTurnAsync(asked.Id, input)); return true; }
        if (IsGenerating) { SendIntoRunningReply(input); return true; }
        return false;
    }

    // ─── another model picked in a chat that is already long ───

    private enum ModelPickPlace { Here, NewChat, Cancelled }

    /// <summary>The owner picked <paramref name="c"/> in the chat on screen. In a long chat the new model would read the
    /// whole conversation from nothing: ask whether to switch here or start a new chat with it. No card = Here.</summary>
    private async Task<ModelPickPlace> AskModelPickPlaceAsync(ChatModelChoice c)
    {
        if (!_settingsService.Settings.SuggestNewChat || AdviceCardOpen || CurrentSession is not { } asked || RunOf(asked) != null)
            return ModelPickPlace.Here;
        var rows = Messages.ToList();
        int tokens = ChatContextTokens(rows);
        if (!NewChatAdvice.ForModelSwitch(tokens, rows.Any(m => m.Role == MessageRole.Assistant))) return ModelPickPlace.Here;
        // The box goes back to the model that answers until the card is answered (it shows what runs, not a pick in waiting).
        _ = App.Current?.Dispatcher.BeginInvoke(() => { RefreshChatModelChoices(); OnPropertyChanged(nameof(SelectedChatModel)); });
        string optNew = LocF("chat.newChatAdvice.modelOptNew", c.Model), optHere = Loc("chat.newChatAdvice.modelOptHere");
        string? answer = await AskChoiceAsync(LocF("chat.newChatAdvice.modelQuestion", tokens, c.Model), optNew, optHere);
        if (!ReferenceEquals(CurrentSession, asked) || RunOf(asked) != null) return ModelPickPlace.Cancelled;
        return string.Equals(answer, optNew, StringComparison.Ordinal) ? ModelPickPlace.NewChat : ModelPickPlace.Here;
    }

    /// <summary>A two-button card in the chat on screen; the button's own text comes back (anything typed instead counts
    /// as the second). Not a run's question: nothing stops it, and after a restart it is a closed card.</summary>
    private Task<string?> AskChoiceAsync(string question, string first, string second)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var who = CharacterForBox(null);
        var card = new ChatMessage
        {
            Role = MessageRole.Question,
            Content = "❓ " + question.Trim(),
            QuestionOptions = new List<string> { first, second },
            QuestionGirl = who.Girl,
            Character = who,
            QuestionCallback = answer => tcs.TrySetResult(answer?.Trim()),
        };
        _adviceCard = card;
        Messages.Add(card);
        ScrollToBottom?.Invoke();
        return tcs.Task;
    }
}
