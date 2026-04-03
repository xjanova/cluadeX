using System.Collections.ObjectModel;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

public class TaskManagerViewModel : ViewModelBase
{
    private readonly TaskManagerService _taskManagerService;

    private TaskInfo? _selectedTask;
    private string _newTaskName = "";
    private string _newTaskCommand = "";

    public ObservableCollection<TaskInfo> Tasks => _taskManagerService.Tasks;

    public TaskInfo? SelectedTask
    {
        get => _selectedTask;
        set => SetProperty(ref _selectedTask, value);
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

    public ICommand CreateTaskCommand { get; }
    public ICommand StopTaskCommand { get; }
    public ICommand ClearCompletedCommand { get; }

    public TaskManagerViewModel(TaskManagerService taskManagerService)
    {
        _taskManagerService = taskManagerService;

        CreateTaskCommand = new RelayCommand(CreateTask, () =>
            !string.IsNullOrWhiteSpace(NewTaskName) && !string.IsNullOrWhiteSpace(NewTaskCommand));

        StopTaskCommand = new RelayCommand<TaskInfo>(task =>
        {
            if (task != null)
                _taskManagerService.StopTask(task.Id);
        });

        ClearCompletedCommand = new RelayCommand(() => _taskManagerService.ClearCompleted());
    }

    private void CreateTask()
    {
        _taskManagerService.CreateTask(NewTaskName.Trim(), NewTaskCommand.Trim());
        NewTaskName = "";
        NewTaskCommand = "";
    }
}
