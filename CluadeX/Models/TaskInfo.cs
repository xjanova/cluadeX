using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CluadeX.Models;

public class TaskInfo : INotifyPropertyChanged
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";

    private string _status = "pending";
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    private string _output = "";
    public string Output
    {
        get => _output;
        set { _output = value; OnPropertyChanged(); }
    }

    public DateTime StartedAt { get; set; }
    public CancellationTokenSource? Cts { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
