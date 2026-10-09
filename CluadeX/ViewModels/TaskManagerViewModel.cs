using System.Collections.ObjectModel;
using System.Globalization;
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

    public ICommand CreateTaskCommand { get; }
    public ICommand StopTaskCommand { get; }
    public ICommand ClearCompletedCommand { get; }

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

        ClearCompletedCommand = new RelayCommand(() => _taskManagerService.ClearCompleted());

        if (_loc != null)
            _loc.LanguageChanged += () =>
            {
                OnPropertyChanged(nameof(SelectedTaskTitle));
                OnPropertyChanged(nameof(WorkingDirectoryText));
            };
    }

    private void CreateTask()
    {
        _taskManagerService.CreateTask(NewTaskName.Trim(), NewTaskCommand.Trim());
        NewTaskName = "";
        NewTaskCommand = "";
        OnPropertyChanged(nameof(WorkingDirectoryText));
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
