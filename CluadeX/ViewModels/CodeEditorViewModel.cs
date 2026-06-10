using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// View model for the Code Editor page — the central "workbench" view
/// (file tree on the left, tabbed editor in the middle, AI chat panel
/// docked on the right). Reuses the singleton ChatViewModel for the
/// embedded chat surface so the user sees the same conversation history
/// whether they're on the Chat page or the Code page.
/// </summary>
public class CodeEditorViewModel : ViewModelBase
{
    private readonly CodeWorkspaceService _workspace;
    private readonly FileSystemService _fs;

    public ChatViewModel ChatVM { get; }

    public ObservableCollection<FileTreeNode> Tree { get; } = new();
    public ObservableCollection<OpenFileTab> Tabs { get; } = new();

    private OpenFileTab? _activeTab;
    public OpenFileTab? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (SetProperty(ref _activeTab, value))
            {
                OnPropertyChanged(nameof(HasActiveTab));
                OnPropertyChanged(nameof(ActiveFileName));
                OnPropertyChanged(nameof(ActiveLanguageLabel));
            }
        }
    }

    public bool HasActiveTab => _activeTab != null;
    public string ActiveFileName => _activeTab?.FileName ?? "(no file open)";
    public string ActiveLanguageLabel => _activeTab == null ? "" : _activeTab.Extension.ToUpperInvariant();

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public string ProjectName
    {
        get
        {
            if (string.IsNullOrEmpty(_workspace.WorkingDirectory)) return "(no project)";
            return Path.GetFileName(_workspace.WorkingDirectory.TrimEnd('\\', '/')) ?? _workspace.WorkingDirectory;
        }
    }

    public string ProjectFileCount
    {
        get
        {
            if (Tree.Count == 0) return "";
            int n = CountFiles(Tree[0]);
            return $"{n:N0} files";
        }
    }

    private static int CountFiles(FileTreeNode n)
    {
        if (!n.IsDirectory) return 1;
        int total = 0;
        foreach (var c in n.Children) total += CountFiles(c);
        return total;
    }

    public bool HasWorkingDirectory => _workspace.HasWorkingDirectory;

    public ICommand RefreshTreeCommand { get; }
    public ICommand OpenNodeCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand SelectTabCommand { get; }
    public ICommand SaveActiveCommand { get; }
    public ICommand SaveAllCommand { get; }
    public ICommand ToggleNodeCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public CodeEditorViewModel(CodeWorkspaceService workspace, FileSystemService fs, ChatViewModel chatVm)
    {
        _workspace = workspace;
        _fs = fs;
        ChatVM = chatVm;

        RefreshTreeCommand = new AsyncRelayCommand(RefreshTreeAsync);
        OpenNodeCommand = new AsyncRelayCommand<FileTreeNode>(OpenNodeAsync);
        CloseTabCommand = new RelayCommand<OpenFileTab>(CloseTab);
        SelectTabCommand = new RelayCommand<OpenFileTab>(t => { if (t != null) ActiveTab = t; });
        SaveActiveCommand = new AsyncRelayCommand(SaveActiveAsync, () => HasActiveTab && (_activeTab?.IsDirty ?? false));
        SaveAllCommand = new AsyncRelayCommand(SaveAllAsync);
        ToggleNodeCommand = new AsyncRelayCommand<FileTreeNode>(ToggleNodeAsync);
        OpenFolderCommand = ChatVM.OpenFolderCommand;

        // Refresh tree when working directory changes via ChatVM
        ChatVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.WorkingDirectory))
            {
                _ = RefreshTreeAsync();
            }
        };

        // Live-follow: when the agent edits a file, open/refresh it in the editor and scroll to the change.
        ChatVM.FileMutatedByAgent += OnAgentFileMutated;
    }

    /// <summary>Raised when a live-followed edit lands — the View scrolls to / selects this 1-based line.</summary>
    public event Action<int>? ScrollToLineRequested;

    private async void OnAgentFileMutated(string relPath, int firstLine)
    {
        try
        {
            if (string.IsNullOrEmpty(relPath) || !_workspace.HasWorkingDirectory) return;
            string full = Path.GetFullPath(Path.Combine(_workspace.WorkingDirectory, relPath));

            var existing = Tabs.FirstOrDefault(t => string.Equals(t.FullPath, full, StringComparison.OrdinalIgnoreCase));
            var loaded = await _workspace.OpenFileAsync(full);
            if (loaded == null) return;   // binary / too large — skip live-follow

            if (existing != null)
            {
                existing.MarkOpened(loaded.Content);   // refresh content in place (keep the tab object)
                ActiveTab = existing;
            }
            else
            {
                Tabs.Add(loaded);
                ActiveTab = loaded;
            }

            StatusMessage = $"● Agent edited {Path.GetFileName(full)}";
            ScrollToLineRequested?.Invoke(firstLine);
            _ = _workspace.EnrichGitStatusAsync(Tree);   // live git badges
        }
        catch { /* live-follow is best-effort — never disrupt the agent run */ }
    }

    public async Task RefreshTreeAsync()
    {
        Tree.Clear();
        if (!_workspace.HasWorkingDirectory)
        {
            StatusMessage = "No project open — click 'Open Folder' in the title bar.";
            OnPropertyChanged(nameof(ProjectName));
            OnPropertyChanged(nameof(ProjectFileCount));
            OnPropertyChanged(nameof(HasWorkingDirectory));
            return;
        }

        try
        {
            var tree = await Task.Run(() => _workspace.BuildTree());
            foreach (var n in tree) Tree.Add(n);

            // Best-effort: tag with git status badges
            _ = _workspace.EnrichGitStatusAsync(Tree);

            OnPropertyChanged(nameof(ProjectName));
            OnPropertyChanged(nameof(ProjectFileCount));
            OnPropertyChanged(nameof(HasWorkingDirectory));
            StatusMessage = $"Loaded {ProjectFileCount}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Tree load failed: {ex.Message}";
        }
    }

    private async Task ToggleNodeAsync(FileTreeNode? node)
    {
        if (node == null) return;
        if (!node.IsDirectory)
        {
            await OpenNodeAsync(node);
            return;
        }
        if (node.IsExpanded)
        {
            node.IsExpanded = false;
            return;
        }
        // Expand — populate if children are not yet loaded
        if (node.Children.Count == 0)
        {
            await Task.Run(() => _workspace.PopulateChildren(node));
            // re-tag git
            _ = _workspace.EnrichGitStatusAsync(new[] { node });
        }
        node.IsExpanded = true;
    }

    private async Task OpenNodeAsync(FileTreeNode? node)
    {
        if (node == null || node.IsDirectory) return;

        // Already open? Just switch.
        foreach (var t in Tabs)
        {
            if (string.Equals(t.FullPath, node.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                ActiveTab = t;
                return;
            }
        }

        var tab = await _workspace.OpenFileAsync(node.FullPath);
        if (tab == null)
        {
            StatusMessage = $"Cannot open '{node.Name}' — binary or too large (max 2 MB).";
            return;
        }

        Tabs.Add(tab);
        ActiveTab = tab;
        StatusMessage = $"Opened {tab.FileName} · {tab.LineCount} lines";
    }

    private void CloseTab(OpenFileTab? tab)
    {
        if (tab == null) return;
        int idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (ActiveTab == tab)
        {
            // Pick neighbour
            if (Tabs.Count == 0) ActiveTab = null;
            else if (idx >= Tabs.Count) ActiveTab = Tabs[^1];
            else ActiveTab = Tabs[idx];
        }
    }

    private async Task SaveActiveAsync()
    {
        if (_activeTab == null) return;
        bool ok = await _workspace.SaveTabAsync(_activeTab);
        StatusMessage = ok ? $"Saved {_activeTab.FileName}" : $"Save failed for {_activeTab.FileName}";
    }

    private async Task SaveAllAsync()
    {
        int n = 0;
        foreach (var t in Tabs.ToList())
        {
            if (!t.IsDirty) continue;
            if (await _workspace.SaveTabAsync(t)) n++;
        }
        StatusMessage = n == 0 ? "Nothing to save" : $"Saved {n} file(s)";
    }
}
