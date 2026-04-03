using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CluadeX.Models;

namespace CluadeX.Services;

public class TaskManagerService
{
    private int _nextId = 1;
    private readonly object _lock = new();

    public ObservableCollection<TaskInfo> Tasks { get; } = new();

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

        Application.Current?.Dispatcher.Invoke(() => Tasks.Add(taskInfo));

        var cts = taskInfo.Cts;
        var token = cts.Token;

        Task.Run(() => RunProcess(taskInfo, token), token);

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
        Application.Current?.Dispatcher.Invoke(() =>
        {
            for (int i = Tasks.Count - 1; i >= 0; i--)
            {
                var status = Tasks[i].Status;
                if (status is "completed" or "failed" or "stopped")
                {
                    Tasks[i].Cts?.Dispose();
                    Tasks.RemoveAt(i);
                }
            }
        });
    }

    private void RunProcess(TaskInfo taskInfo, CancellationToken token)
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
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
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
        lock (_lock)
        {
            task.Output += text;
        }
    }
}
