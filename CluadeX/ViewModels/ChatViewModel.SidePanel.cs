using System.Collections.ObjectModel;
using System.IO;
using CluadeX.Models;

namespace CluadeX.ViewModels;

/// <summary>One file or folder in the chat's project panel. Folders load their children the first time they open.</summary>
public sealed class FileNode : ViewModelBase
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "bin", "obj", "node_modules", ".vs", ".idea", "__pycache__", ".cluadex" };
    private const int MaxEntries = 400;

    public FileNode(string fullPath, bool isDirectory)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Name = Path.GetFileName(fullPath.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(Name)) Name = fullPath;
        if (isDirectory) Children.Add(Placeholder);   // shows the expander until it is opened
    }

    private FileNode() { FullPath = ""; Name = "…"; }
    private static readonly FileNode Placeholder = new();

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public ObservableCollection<FileNode> Children { get; } = new();

    private bool _isExpanded, _loaded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            if (value) Load();
            OnPropertyChanged(nameof(Glyph));
        }
    }

    /// <summary>Segoe MDL2 glyph: an open or closed folder, a document.</summary>
    public string Glyph => IsDirectory ? (_isExpanded ? "" : "") : "";

    private void Load()
    {
        if (_loaded || !IsDirectory) return;
        _loaded = true;
        Children.Clear();
        try
        {
            var dirs = Directory.EnumerateDirectories(FullPath)
                .Where(d => !Skipped.Contains(Path.GetFileName(d)))
                .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);
            var files = Directory.EnumerateFiles(FullPath)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var d in dirs) { if (n++ >= MaxEntries) break; Children.Add(new FileNode(d, true)); }
            foreach (var f in files) { if (n++ >= MaxEntries) break; Children.Add(new FileNode(f, false)); }
        }
        catch { /* unreadable folder: shown empty */ }
    }
}

/// <summary>
/// The chat page's right-hand panel: the project's files, the last image the agent looked at, and the output of its
/// last command or build — so what the agent is working on is visible without leaving the chat.
/// </summary>
public partial class ChatViewModel
{
    public ObservableCollection<FileNode> ProjectTree { get; } = new();

    private bool _showSidePanel = true;
    /// <summary>The right-hand panel is shown (Settings ▸ ChatSidePanel, toggled from the "+" menu).</summary>
    public bool ShowSidePanel
    {
        get => _showSidePanel;
        set
        {
            if (!SetProperty(ref _showSidePanel, value)) return;
            _settingsService.UpdateSettings(s => s.ChatSidePanel = value);
            OnPropertyChanged(nameof(SidePanelVisible));
        }
    }

    /// <summary>Shown when switched on AND there is something to show: a project, an image or a run. A chat without
    /// any of them gets the full width.</summary>
    public bool SidePanelVisible => _showSidePanel
        && (HasProject || !string.IsNullOrEmpty(_latestImagePath) || !string.IsNullOrEmpty(_latestRunOutput));

    private string? _latestImagePath;
    private string _latestRunTitle = "", _latestRunOutput = "";
    /// <summary>The newest image a tool returned in this chat (a screenshot the agent looked at).</summary>
    public string? LatestImagePath { get => _latestImagePath; private set => SetProperty(ref _latestImagePath, value); }
    /// <summary>The newest command / build / test the agent ran in this chat, and the end of its output.</summary>
    public string LatestRunTitle { get => _latestRunTitle; private set => SetProperty(ref _latestRunTitle, value); }
    public string LatestRunOutput { get => _latestRunOutput; private set => SetProperty(ref _latestRunOutput, value); }

    /// <summary>Raised to open a file of the project in the code editor (MainViewModel navigates and opens it).</summary>
    public event Action<string>? OpenFileRequested;
    public void RequestOpenFile(string fullPath)
    {
        if (File.Exists(fullPath)) OpenFileRequested?.Invoke(fullPath);
    }

    private string? _treeRoot;

    /// <summary>The tree follows the open project: rebuilt when the folder changes (or on Refresh).</summary>
    public void RefreshProjectTree(bool force = false)
    {
        string root = HasProject ? WorkingDirectory : "";
        if (!force && string.Equals(root, _treeRoot, StringComparison.OrdinalIgnoreCase)) return;
        _treeRoot = root;
        ProjectTree.Clear();
        if (root.Length == 0 || !Directory.Exists(root)) return;
        var top = new FileNode(root, true);
        ProjectTree.Add(top);
        top.IsExpanded = true;
    }

    private static readonly HashSet<string> RunTools = new(StringComparer.OrdinalIgnoreCase)
        { "run_command", "powershell", "run_build", "run_tests", "repl" };

    /// <summary>Latest image and latest run, from the end of the chat on screen.</summary>
    private void RefreshSidePanelFacts()
    {
        string? image = null;
        ChatMessage? run = null;
        for (int i = Messages.Count - 1; i >= 0 && (image == null || run == null); i--)
        {
            var m = Messages[i];
            if (image == null && !string.IsNullOrEmpty(m.ThumbnailPath)) image = m.ThumbnailPath;
            if (run == null && m.Role == MessageRole.ToolAction && !string.IsNullOrEmpty(m.ToolOutput))
            {
                string kind = m.ToolKind ?? (m.CollapsedToolNames.Count > 0 ? m.CollapsedToolNames[0] : "");
                if (RunTools.Contains(kind) || kind.StartsWith("bg_", StringComparison.OrdinalIgnoreCase)) run = m;
            }
        }
        LatestImagePath = image;
        LatestRunTitle = run == null ? "" : (run.ToolTarget ?? run.ToolName ?? "");
        if (run == null) LatestRunOutput = "";
        else
        {
            var lines = (run.ToolOutput ?? "").Replace("\r\n", "\n").TrimEnd().Split('\n');
            LatestRunOutput = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 40)));
        }
    }

    private void InitSidePanel()
    {
        _showSidePanel = _settingsService.Settings.ChatSidePanel;
        _fileSystemService.OnWorkingDirectoryChanged += () => App.Current?.Dispatcher.BeginInvoke(() => RefreshProjectTree());
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Messages) or nameof(CurrentSession)) RefreshSidePanelFacts();
            if (e.PropertyName is nameof(HasProject) or nameof(WorkingDirectory)) RefreshProjectTree();
            if (e.PropertyName is nameof(HasProject) or nameof(LatestImagePath) or nameof(LatestRunOutput))
                OnPropertyChanged(nameof(SidePanelVisible));
        };
        RefreshProjectTree();
        RefreshSidePanelFacts();
    }
}
