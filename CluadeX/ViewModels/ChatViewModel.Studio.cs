using System.ComponentModel;
using System.IO;

namespace CluadeX.ViewModels;

/// <summary>The UI Studio's way into the chat: the project they show, switching the app's project without leaving
/// the Studio, and handing the AI a request about one element.</summary>
public partial class ChatViewModel
{
    /// <summary>The project of the chat on screen ("" = it has none, or its folder is gone). The UI Studio and the Node
    /// Graph show this and nothing else — not the app's working folder, which a run or a pipe task may move for a
    /// moment. Owner 2026-11-01: "เราอยู่แชทไหนต้องแสดงของแชทนั้น จะเปลี่ยนต่อเมื่อสลับไปแชทใหม่ทันที".</summary>
    public string ChatProject { get; private set; } = "";

    /// <summary>The chat on screen became another project's, or its own project changed. A run in any other chat
    /// never raises this: only the chat on screen is watched.</summary>
    public event Action? ChatProjectChanged;

    /// <summary>The UI Studio or the Node Graph is on screen: the user is looking at this chat's project, so a pipe task
    /// runs in its own chat in the background instead of bringing another project up under them.</summary>
    public bool ProjectPageOnScreen { get; set; }

    private void WatchChatProject(Models.ChatSession? left, Models.ChatSession? now)
    {
        if (left != null) left.PropertyChanged -= OnChatOnScreenChanged;
        if (now != null) now.PropertyChanged += OnChatOnScreenChanged;
        NoteChatProject();
    }

    private void OnChatOnScreenChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Models.ChatSession.ProjectPath)) NoteChatProject();
    }

    private void NoteChatProject()
    {
        string? path = CurrentSession?.ProjectPath;
        string now = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : "";
        if (PathEquals(now, ChatProject)) return;
        ChatProject = now;
        OnPropertyChanged(nameof(ChatProject));
        ChatProjectChanged?.Invoke();
    }

    /// <summary>Make <paramref name="path"/> the open project (announced in the chat) without bringing the code
    /// editor forward — the user stays on the page that opened it. It becomes the project of the chat on screen; a
    /// chat that is running, or that already holds work on another project, keeps its own and the new project gets a
    /// chat of its own, so the two never mix.</summary>
    public void OpenProjectQuietly(string path)
    {
        if (PathEquals(ChatProject, path) && PathEquals(WorkingDirectory, path)) return;
        bool holdsOtherProject = Messages.Count > 0 && ChatProject.Length > 0 && !PathEquals(ChatProject, path);
        if (IsGenerating || holdsOtherProject) NewSession();
        ApplyWorkingDirectory(path, announce: true, showEditor: false);
        if (CurrentSession is { } chat && !PathEquals(chat.ProjectPath ?? "", path))
        {
            chat.ProjectPath = path;
            if (Messages.Count > 0) { _isDirty = true; AutoSave(); }
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
        _studioChat = CurrentSession;
        SendMessageCommand.Execute(null);
        return true;
    }

    /// <summary>The chat the Studio's last request went to.</summary>
    private Models.ChatSession? _studioChat;

    /// <summary>The reply to the Studio's last request is still being written — in that chat, whichever chat is on
    /// screen now (the user may switch chats while the AI edits the screen).</summary>
    public bool IsStudioRunGoing
    {
        get
        {
            var s = _studioChat;
            return s != null && _runs.Any(r => ReferenceEquals(r.Session, s) || r.Session.Id == s.Id);
        }
    }
}
