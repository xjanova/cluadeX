using System.IO;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// Carrying on a task in its own folder. A task sent over the pipe (or paused) works in its folder; when it ends the
/// window goes back to the user's project, and a "ทำต่อ" typed in the task's chat then ran in THAT project
/// (owner 2026-10-17: HIVE // BREACH paused at step 101, the chat said messages would now work in MV_assets).
/// The task's chat keeps its folder: a turn there runs in the chat's own project.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>The folder a turn in <paramref name="session"/> works in when it is not the window's: the chat's own
    /// project, for a pipe task's chat or a chat whose last reply was a pause. Null = the window's project.</summary>
    private string? TaskFolderFor(ChatSession session)
    {
        string own = session.ProjectPath ?? "";
        string window = _fileSystemService.AppWorkingDirectory ?? "";
        bool paused = Messages.LastOrDefault(m => m.Role == MessageRole.Assistant) is { } last
                      && (last.Content ?? "").StartsWith(AgentRunScope.PausedMarker, StringComparison.Ordinal);
        return PickTaskFolder(own, window, session.Title ?? "", paused, Directory.Exists);
    }

    public static string? PickTaskFolder(string chatProject, string windowProject, string chatTitle, bool lastReplyPaused,
        Func<string, bool> dirExists)
    {
        if (string.IsNullOrWhiteSpace(chatProject) || !dirExists(chatProject)) return null;
        if (!string.IsNullOrWhiteSpace(windowProject) && PathEquals(chatProject, windowProject)) return null;
        bool taskChat = chatTitle.StartsWith("[ObsidianX]", StringComparison.Ordinal) || lastReplyPaused;
        return taskChat ? chatProject : null;
    }
}
