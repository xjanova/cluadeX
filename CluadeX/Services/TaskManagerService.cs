using System.Collections.ObjectModel;
using System.IO;
using CluadeX.Models;
using CluadeX.Services.Background;

namespace CluadeX.Services;

/// <summary>
/// The Task Manager page's view of the one background-process registry, <see cref="BackgroundProcessService"/>.
/// What the page starts and what the agent starts (bg_start, bg_serve, run_command handing a dev server over) are
/// the same list, each process in its own job object, all of them killed with their whole tree on stop and on
/// exit. Kept as a class of its own so the page and its view model did not have to change shape.
/// </summary>
public class TaskManagerService
{
    /// <summary>Newest output kept per process (bounded — a chatty dev server or watcher used to grow without end).</summary>
    public const int MaxOutputChars = BackgroundProcessService.MaxOutputChars;

    private readonly FileSystemService? _fileSystem;

    /// <summary>The registry behind the page (shared with the bg_* tools in the app).</summary>
    public BackgroundProcessService Background { get; }

    public ObservableCollection<TaskInfo> Tasks => Background.Items;

    /// <summary>The reason the last <see cref="CreateTask"/> did not start anything (null after a start).</summary>
    public string? LastError { get; private set; }

    public TaskManagerService(FileSystemService? fileSystem = null, BackgroundProcessService? background = null)
    {
        _fileSystem = fileSystem;
        // Standalone (a harness, a tool) it keeps its own registry; in the app DI hands it the shared one.
        Background = background ?? new BackgroundProcessService(fileSystem: fileSystem);
    }

    /// <summary>
    /// Where tasks run: the open project, so "npm test" / "dotnet build" act on it. They used to run in
    /// %USERPROFILE%, where those commands find nothing (or worse, a different project).
    /// </summary>
    public string TaskWorkingDirectory =>
        _fileSystem is { HasWorkingDirectory: true } fs && Directory.Exists(fs.WorkingDirectory)
            ? fs.WorkingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Start a command typed on the page (cmd.exe, as the page always ran). The id, or -1 — then
    /// <see cref="LastError"/> says why (the process cap, a missing folder).</summary>
    public int CreateTask(string name, string command)
    {
        var (id, error) = Background.StartFromPage(name, command, TaskWorkingDirectory);
        LastError = error;
        return id;
    }

    /// <summary>Stop one task with its whole process tree. Returns without waiting for the processes to go.</summary>
    public void StopTask(int taskId) => _ = Background.StopAsync(taskId, TimeSpan.FromSeconds(3));

    /// <summary>Stop every running task (the page asks for confirmation first). Returns how many were running.</summary>
    public Task<int> StopAllAsync() => Background.StopAllAsync(TimeSpan.FromSeconds(3));

    public int RunningCount => Background.RunningCount;

    public void ClearCompleted() => Background.ClearFinished();

    /// <summary>Push queued output into the rows now (normally done about ten times a second).</summary>
    public void FlushPendingOutput() => Background.FlushPendingOutput();
}
