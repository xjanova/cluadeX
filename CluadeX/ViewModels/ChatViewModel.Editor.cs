using CluadeX.Models;

namespace CluadeX.ViewModels;

/// <summary>What happened to a message the Code Editor sent to the chat.</summary>
public enum EditorSendResult
{
    /// <summary>Nothing to send, or the chat cannot take it now (a slash command into a running reply).</summary>
    Refused,
    /// <summary>Sent as a new turn of the current chat.</summary>
    Sent,
    /// <summary>Handed to the AI already working in the project: it reads it at its next step.</summary>
    Queued,
    /// <summary>Put in the chat box for the user to send (they had a draft there, or asked for it).</summary>
    InBox,
}

/// <summary>
/// The Code Editor talking to the chat in one step (owner 2026-11-03, vibe coding): "fix / explain this selection",
/// "fix this error", "add these results as context". A run already working in the project gets the message at its next
/// step — the same queue as typing while it runs — never a second agent in the same files.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>
    /// Send <paramref name="prompt"/> from the editor. Into the running reply of the current chat, or of the chat whose
    /// AI is working in <paramref name="projectFolder"/>; else as a new turn — unless the user has a draft or attachments
    /// in the box, which are theirs: then the prompt is added below the draft and nothing is sent.
    /// </summary>
    public EditorSendResult SendFromEditor(string prompt, string? projectFolder)
    {
        prompt = (prompt ?? "").Trim();
        if (prompt.Length == 0) return EditorSendResult.Refused;

        var run = RunOf(CurrentSession);
        if (run == null && !string.IsNullOrEmpty(projectFolder))
            run = _runs.FirstOrDefault(r => r.Scope?.WorkingDirectory is { Length: > 0 } wd
                                            && (IsInsideFolder(wd, projectFolder) || IsInsideFolder(projectFolder, wd)));
        if (run != null)
        {
            if (run.Scope == null || prompt.StartsWith("/")) return EditorSendResult.Refused;
            // As SendIntoRunningReply, for any run and without touching the box: the user's draft stays where it is.
            var bubble = new ChatMessage { Role = MessageRole.User, Content = prompt, IsQueuedNote = true };
            run.Messages.Add(bubble);
            bubble.QueueRow = new ChatMessage { Role = MessageRole.System, Content = Loc("chat.steer.queued") };
            run.Messages.Add(bubble.QueueRow);
            run.Scope.AddUserNote(prompt, bubble);
            StatusText = Loc("chat.steer.queued");
            _pet?.NoteMessage(prompt);
            return EditorSendResult.Queued;
        }

        if (!string.IsNullOrWhiteSpace(UserInput) || HasPendingImages || HasPendingFiles)
        {
            AddToChatBox(prompt);
            return EditorSendResult.InBox;
        }
        UserInput = prompt;
        if (!SendMessageCommand.CanExecute(null)) return EditorSendResult.InBox;
        SendMessageCommand.Execute(null);
        return EditorSendResult.Sent;
    }

    /// <summary>Add <paramref name="text"/> to the chat box below what is there, for the user to finish and send.</summary>
    public void AddToChatBox(string text)
    {
        string draft = (UserInput ?? "").TrimEnd();
        UserInput = draft.Length == 0 ? text : draft + "\n\n" + text;
    }
}
