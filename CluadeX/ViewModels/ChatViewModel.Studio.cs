using System.IO;

namespace CluadeX.ViewModels;

/// <summary>The UI Studio's way into the chat: switch the app's project without leaving the Studio, and hand the
/// AI a request about one element.</summary>
public partial class ChatViewModel
{
    /// <summary>Make <paramref name="path"/> the open project (announced in the chat) without bringing the code
    /// editor forward — the user stays on the page that opened it.</summary>
    public void OpenProjectQuietly(string path)
    {
        if (string.Equals(WorkingDirectory, path, StringComparison.OrdinalIgnoreCase)) return;
        if (IsGenerating) NewSession();   // a running reply keeps its own folder; the new project gets its own chat
        ApplyWorkingDirectory(path, announce: true, showEditor: false);
        if (CurrentSession is { } chat && (string.IsNullOrWhiteSpace(chat.ProjectPath) || !Directory.Exists(chat.ProjectPath)))
        {
            chat.ProjectPath = path;
            RebuildVisibleSessions();
        }
    }

    /// <summary>Send <paramref name="prompt"/> as the user's next message. False when a reply is already running in
    /// the chat on screen (the Studio says so instead of queueing behind it).</summary>
    public bool SendFromStudio(string prompt)
    {
        if (IsGenerating || string.IsNullOrWhiteSpace(prompt)) return false;
        UserInput = prompt;
        if (!SendMessageCommand.CanExecute(null)) return false;
        SendMessageCommand.Execute(null);
        return true;
    }
}
