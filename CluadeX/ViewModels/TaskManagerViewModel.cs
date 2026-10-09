using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

public class TaskManagerViewModel : ViewModelBase
{
    private readonly TaskManagerService _taskManagerService;
    private readonly LocalizationService? _loc;

    private TaskInfo? _selectedTask;
    private string _newTaskName = "";
    private string _newTaskCommand = "";
    private string _statusText = "";

    /// <summary>Every background process — the page's own tasks and the dev servers the agent started.</summary>
    public ObservableCollection<TaskInfo> Tasks => _taskManagerService.Tasks;

    public TaskInfo? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value))
                OnPropertyChanged(nameof(SelectedTaskTitle));
        }
    }

    /// <summary>The output pane's title: the selected task's name, or a localized hint.</summary>
    public string SelectedTaskTitle => _selectedTask?.Name ?? _loc?.T("tasksm.selectTask") ?? "";

    /// <summary>Where new tasks run (shown under the form so it's clear which folder "npm test" acts on).</summary>
    public string WorkingDirectoryText => _loc?.T("tasksm.runsIn", _taskManagerService.TaskWorkingDirectory) ?? "";

    /// <summary>One line of feedback under the form: why a start was refused, what "Stop all" did.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string NewTaskName
    {
        get => _newTaskName;
        set => SetProperty(ref _newTaskName, value);
    }

    public string NewTaskCommand
    {
        get => _newTaskCommand;
        set => SetProperty(ref _newTaskCommand, value);
    }

    /// <summary>Asks the user (text, title) → yes. A test replaces it; the app shows a Yes/No box.</summary>
    public Func<string, string, bool> Confirm { get; set; } = (text, title) =>
        MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    /// <summary>Opens a URL in the user's browser. A test replaces it.</summary>
    public Action<string> OpenInBrowser { get; set; } = url =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    public ICommand CreateTaskCommand { get; }
    public ICommand StopTaskCommand { get; }
    public ICommand StopAllCommand { get; }
    public ICommand ClearCompletedCommand { get; }
    public ICommand OpenUrlCommand { get; }

    public TaskManagerViewModel(TaskManagerService taskManagerService, LocalizationService? loc = null)
    {
        _taskManagerService = taskManagerService;
        _loc = loc;

        CreateTaskCommand = new RelayCommand(CreateTask, () =>
            !string.IsNullOrWhiteSpace(NewTaskName) && !string.IsNullOrWhiteSpace(NewTaskCommand));

        StopTaskCommand = new RelayCommand<TaskInfo>(task =>
        {
            if (task != null)
                _taskManagerService.StopTask(task.Id);
        });

        StopAllCommand = new RelayCommand(() => _ = StopAllAsync(), () => !IsStopping);
        ClearCompletedCommand = new RelayCommand(() => _taskManagerService.ClearCompleted());
        OpenUrlCommand = new RelayCommand<TaskInfo>(task => OpenUrl(task?.Url));

        if (_loc != null)
            _loc.LanguageChanged += () =>
            {
                OnPropertyChanged(nameof(SelectedTaskTitle));
                OnPropertyChanged(nameof(WorkingDirectoryText));
            };
    }

    private string T(string key, params object[] args)
    {
        string text = _loc?.T(key) ?? LocalizedResources.Instance[key];
        return args.Length == 0 ? text : string.Format(text, args);
    }

    private void CreateTask()
    {
        int id = _taskManagerService.CreateTask(NewTaskName.Trim(), NewTaskCommand.Trim());
        if (id < 0)
        {
            // Keep what was typed: the user fixes the cause (the process cap) and presses Start again.
            StatusText = T("tasks.startFailed", _taskManagerService.LastError ?? "");
            return;
        }
        StatusText = "";
        NewTaskName = "";
        NewTaskCommand = "";
        OnPropertyChanged(nameof(WorkingDirectoryText));
    }

    private bool _isStopping;
    /// <summary>A "Stop all" is being confirmed or carried out (up to 3 s): the button is disabled and a second click
    /// neither asks again nor starts a second stop.</summary>
    public bool IsStopping
    {
        get => _isStopping;
        private set
        {
            if (!SetProperty(ref _isStopping, value)) return;
            // Re-evaluate the button now, on the UI thread (no-op in a test without a WPF application).
            try { Application.Current?.Dispatcher?.BeginInvoke(new Action(CommandManager.InvalidateRequerySuggested)); } catch { }
        }
    }

    /// <summary>"Stop all": asks first — the agent's dev servers are in this list too. Declined = nothing happens.</summary>
    public async Task StopAllAsync()
    {
        if (IsStopping) return;
        int running = _taskManagerService.RunningCount;
        if (running == 0)
        {
            StatusText = T("tasks.nothingRunning");
            return;
        }
        IsStopping = true;
        try
        {
            if (!Confirm(T("tasks.stopAllConfirm", running), "CluadeX")) return;
            int stopped = await _taskManagerService.StopAllAsync();
            StatusText = T("tasks.stopAllDone", stopped);
        }
        finally { IsStopping = false; }
    }

    /// <summary>Open a server's URL in the user's browser — only http(s) on this computer, never anything a
    /// process printed for somewhere else.</summary>
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !uri.IsLoopback)
            return;
        try { OpenInBrowser(uri.AbsoluteUri); }
        catch { StatusText = T("tasks.openUrlFailed", uri.AbsoluteUri); }
    }
}

/// <summary>Task status key (running / completed / failed / stopped) → label in the UI language.</summary>
public sealed class TaskStatusLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string status = value as string ?? "";
        string label = LocalizedResources.Instance["tasksm.status." + status];
        return label.StartsWith("tasksm.", StringComparison.Ordinal) ? status : label;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Task kind key (shell / static / adopted) → label in the UI language.</summary>
public sealed class TaskKindLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string kind = value as string ?? "";
        string label = LocalizedResources.Instance["tasksm.kind." + kind];
        return label.StartsWith("tasksm.", StringComparison.Ordinal) ? kind : label;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
