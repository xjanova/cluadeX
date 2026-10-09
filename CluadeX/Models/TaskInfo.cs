using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CluadeX.Models;

/// <summary>
/// One background process as the Task Manager page and the bg_* tools see it — a dev server the agent started,
/// the built-in static server, or a command the user started on the page. Owned and updated by
/// Services.Background.BackgroundProcessService; every property raises PropertyChanged so the page refreshes
/// row by row, never with a full reload.
/// </summary>
public class TaskInfo : INotifyPropertyChanged
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";

    /// <summary>"shell" (a command), "static" (the built-in file server) or "adopted" (found running, not started here).</summary>
    public string Kind { get; set; } = "shell";

    /// <summary>"agent" (bg_* tools, run_command) or "user" (the Task Manager page).</summary>
    public string StartedBy { get; set; } = "user";

    /// <summary>The folder it runs in — kept when the open project changes, so a server is never orphaned in the list.</summary>
    public string Cwd { get; set; } = "";

    /// <summary>The full log on disk (DataRoot\bg\&lt;yyyyMMdd-HHmmss&gt;-&lt;id&gt;.log, one name per app run), or empty.</summary>
    public string LogPath { get; set; } = "";

    public DateTime StartedAt { get; set; }

    private string _status = "pending";
    /// <summary>running · completed · failed · stopped (the keys the page colours and translates).</summary>
    public string Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsRunning)); }
    }

    public bool IsRunning => _status == "running";

    private int? _pid;
    public int? Pid
    {
        get => _pid;
        set { if (_pid == value) return; _pid = value; OnPropertyChanged(); }
    }

    private int? _port;
    public int? Port
    {
        get => _port;
        set { if (_port == value) return; _port = value; OnPropertyChanged(); OnPropertyChanged(nameof(PortText)); }
    }

    public string PortText => _port?.ToString() ?? "";

    private string? _url;
    /// <summary>Where the server answers (http://127.0.0.1:8100/) once known.</summary>
    public string? Url
    {
        get => _url;
        set { if (_url == value) return; _url = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasUrl)); }
    }

    public bool HasUrl => !string.IsNullOrEmpty(_url);

    private int? _exitCode;
    public int? ExitCode
    {
        get => _exitCode;
        set { if (_exitCode == value) return; _exitCode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExitCodeText)); }
    }

    public string ExitCodeText => _exitCode?.ToString() ?? "";

    private DateTime? _readyAt;
    /// <summary>When readiness was seen (a printed URL, the ready pattern or an open port); null until then.</summary>
    public DateTime? ReadyAt
    {
        get => _readyAt;
        set { if (_readyAt == value) return; _readyAt = value; OnPropertyChanged(); }
    }

    private string _output = "";
    /// <summary>The newest output (bounded), ANSI-free, for the page's output pane.</summary>
    public string Output
    {
        get => _output;
        set { _output = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
