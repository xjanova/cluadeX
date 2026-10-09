using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using CluadeX.Models;

namespace CluadeX.Services;

public class TaskManagerService
{
    private int _nextId = 1;
    private readonly object _lock = new();
    private readonly FileSystemService? _fileSystem;

    /// <summary>Newest output kept per task. A long-running chatty task (a dev server, a watcher) used to grow
    /// its Output string without bound, re-concatenating the whole thing for every line.</summary>
    public const int MaxOutputChars = 256 * 1024;

    // Output is collected here and pushed to the UI in batches (~10 per second), not with one blocking
    // Dispatcher.Invoke per line — a task printing thousands of lines a second froze the whole window.
    private readonly Dictionary<TaskInfo, StringBuilder> _pending = new();
    private readonly object _pendingLock = new();
    private bool _flushScheduled;

    public ObservableCollection<TaskInfo> Tasks { get; } = new();

    public TaskManagerService(FileSystemService? fileSystem = null)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Where tasks run: the open project, so "npm test" / "dotnet build" act on it. They used to run in
    /// %USERPROFILE%, where those commands find nothing (or worse, a different project).
    /// </summary>
    public string TaskWorkingDirectory =>
        _fileSystem is { HasWorkingDirectory: true } fs && Directory.Exists(fs.WorkingDirectory)
            ? fs.WorkingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public int CreateTask(string name, string command)
    {
        var taskId = Interlocked.Increment(ref _nextId) - 1;

        var taskInfo = new TaskInfo
        {
            Id = taskId,
            Name = name,
            Command = command,
            Status = "running",
            StartedAt = DateTime.Now,
            Cts = new CancellationTokenSource()
        };

        var app = Application.Current;
        if (app != null) app.Dispatcher.Invoke(() => Tasks.Add(taskInfo));
        else lock (_lock) Tasks.Add(taskInfo);

        var cts = taskInfo.Cts;
        var token = cts.Token;
        string cwd = TaskWorkingDirectory;

        Task.Run(() => RunProcess(taskInfo, cwd, token), token);

        return taskId;
    }

    public void StopTask(int taskId)
    {
        TaskInfo? task = null;
        lock (_lock)
        {
            foreach (var t in Tasks)
            {
                if (t.Id == taskId)
                {
                    task = t;
                    break;
                }
            }
        }

        if (task == null || task.Status is not "running") return;

        try
        {
            task.Cts?.Cancel();
        }
        catch
        {
            // CancellationTokenSource may already be disposed
        }

        task.Status = "stopped";
        AppendOutput(task, "\n[Task stopped by user]");
    }

    public void ClearCompleted()
    {
        void Clear()
        {
            for (int i = Tasks.Count - 1; i >= 0; i--)
            {
                var status = Tasks[i].Status;
                if (status is "completed" or "failed" or "stopped")
                {
                    Tasks[i].Cts?.Dispose();
                    lock (_pendingLock) _pending.Remove(Tasks[i]);
                    Tasks.RemoveAt(i);
                }
            }
        }
        var app = Application.Current;
        if (app != null) app.Dispatcher.Invoke(Clear);
        else Clear();
    }

    private void RunProcess(TaskInfo taskInfo, string workingDirectory, CancellationToken token)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {taskInfo.Command}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            };

            process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    AppendOutput(taskInfo, e.Data + Environment.NewLine);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    AppendOutput(taskInfo, "[stderr] " + e.Data + Environment.NewLine);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait for exit or cancellation
            while (!process.HasExited)
            {
                if (token.IsCancellationRequested)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        try { process.Kill(); } catch { }
                    }
                    return;
                }

                Thread.Sleep(100);
            }

            process.WaitForExit(); // Ensure all async output is flushed

            if (taskInfo.Status == "running")
            {
                taskInfo.Status = process.ExitCode == 0 ? "completed" : "failed";
                if (process.ExitCode != 0)
                    AppendOutput(taskInfo, $"[Exit code: {process.ExitCode}]{Environment.NewLine}");
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation handled above
        }
        catch (Exception ex)
        {
            if (taskInfo.Status == "running")
            {
                taskInfo.Status = "failed";
                AppendOutput(taskInfo, $"[Error: {ex.Message}]{Environment.NewLine}");
            }
        }
        finally
        {
            process?.Dispose();
        }
    }

    private void AppendOutput(TaskInfo task, string text)
    {
        lock (_pendingLock)
        {
            if (!_pending.TryGetValue(task, out var sb)) _pending[task] = sb = new StringBuilder();
            sb.Append(text);
            if (sb.Length > MaxOutputChars) sb.Remove(0, sb.Length - MaxOutputChars);
            if (_flushScheduled) return;
            _flushScheduled = true;
        }
        _ = Task.Delay(100).ContinueWith(_ =>
        {
            var app = Application.Current;
            if (app != null) app.Dispatcher.BeginInvoke(new Action(FlushPendingOutput));
            else FlushPendingOutput();
        }, TaskScheduler.Default);
    }

    /// <summary>Move buffered output into the tasks (UI thread), keeping the newest <see cref="MaxOutputChars"/>.</summary>
    public void FlushPendingOutput()
    {
        List<(TaskInfo Task, string Text)> batch;
        lock (_pendingLock)
        {
            batch = _pending.Select(kv => (kv.Key, kv.Value.ToString())).ToList();
            _pending.Clear();
            _flushScheduled = false;
        }
        foreach (var (task, text) in batch)
        {
            string combined = task.Output + text;
            if (combined.Length > MaxOutputChars)
                combined = "[…]" + Environment.NewLine + combined[^MaxOutputChars..];
            task.Output = combined;
        }
    }
}
