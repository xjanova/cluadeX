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
    private readonly SettingsService _settings;

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

    public CodeEditorViewModel(CodeWorkspaceService workspace, FileSystemService fs, ChatViewModel chatVm, SettingsService settings)
    {
        _workspace = workspace;
        _fs = fs;
        _settings = settings;
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

    /// <summary>Raised when a live-typing reveal finishes: the View flash-selects (charStart, charLength)
    /// so the freshly typed region glows for a moment.</summary>
    public event Action<int, int>? AgentEditFlashRequested;

    // ── Agent live-typing reveal state ──
    private System.Windows.Threading.DispatcherTimer? _typeTimer;
    private OpenFileTab? _typingTab;
    private string _typingFinal = "";

    private async void OnAgentFileMutated(string relPath, int firstLine)
    {
        try
        {
            if (string.IsNullOrEmpty(relPath) || !_workspace.HasWorkingDirectory) return;
            string full = Path.GetFullPath(Path.Combine(_workspace.WorkingDirectory, relPath));

            var existing = Tabs.FirstOrDefault(t => string.Equals(t.FullPath, full, StringComparison.OrdinalIgnoreCase));

            // NEVER clobber unsaved human edits: the old refresh-in-place silently discarded whatever
            // the user had typed in a dirty tab the moment the agent touched the same file on disk.
            if (existing != null && existing.IsDirty)
            {
                StatusMessage = $"⚠ Agent edited {Path.GetFileName(full)} on disk — tab kept (it has your unsaved changes)";
                return;
            }

            var loaded = await _workspace.OpenFileAsync(full);
            if (loaded == null) return;   // binary / too large — skip live-follow

            FinishTypingInstantly();      // a newer edit arrived — fast-forward any reveal still running

            if (existing == null)
            {
                Tabs.Add(loaded);
                ActiveTab = loaded;
                StatusMessage = $"● Agent edited {Path.GetFileName(full)}";
                ScrollToLineRequested?.Invoke(firstLine);
            }
            else
            {
                ActiveTab = existing;
                if (!_settings.Settings.LiveCodingAnimationEnabled
                    || !TryStartTypingReveal(existing, existing.Content, loaded.Content))
                {
                    existing.MarkOpened(loaded.Content);   // instant refresh fallback
                    StatusMessage = $"● Agent edited {Path.GetFileName(full)}";
                    ScrollToLineRequested?.Invoke(firstLine);
                }
            }

            _ = _workspace.EnrichGitStatusAsync(Tree);   // live git badges
        }
        catch { /* live-follow is best-effort — never disrupt the agent run */ }
    }

    /// <summary>
    /// Live-typing reveal: instantly remove the OLD changed region, then "type" the new region in
    /// chunks (~1s total) so the agent's edit unfolds in the editor like a human typing, with the
    /// view following the insertion point. Implemented as successive MarkOpened states — binding-
    /// friendly and never marks the tab dirty. Returns false when the change doesn't animate well
    /// (identical text, pure deletion, or a huge rewrite) — caller falls back to instant refresh.
    /// </summary>
    private bool TryStartTypingReveal(OpenFileTab tab, string oldText, string newText)
    {
        if (string.Equals(oldText, newText, StringComparison.Ordinal)) return false;
        if (newText.Length > 1_000_000) return false;    // intermediate strings would be too costly

        // Common prefix/suffix → the changed window
        int limit = Math.Min(oldText.Length, newText.Length);
        int prefix = 0;
        while (prefix < limit && oldText[prefix] == newText[prefix]) prefix++;
        int suffix = 0;
        while (suffix < limit - prefix
            && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix]) suffix++;

        int insertLen = newText.Length - prefix - suffix;
        if (insertLen <= 0) return false;                // pure deletion — nothing to "type"
        if (insertLen > 6000) return false;              // huge rewrite — instant is kinder

        string head = newText[..prefix];
        string tail = newText[(prefix + insertLen)..];
        string insert = newText.Substring(prefix, insertLen);

        _typingTab = tab;
        _typingFinal = newText;
        tab.MarkOpened(head + tail);                     // phase 1: the old region vanishes (the "delete")
        StatusMessage = $"⌨ Agent typing {tab.FileName}…";

        int pos = 0;
        int tick = 0;
        int chunk = Math.Max(4, insertLen / 56);         // ~56 frames ≈ 0.9s at 16ms/frame

        _typeTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _typeTimer.Tick += (_, _) =>
        {
            pos = Math.Min(insertLen, pos + chunk);
            tab.MarkOpened(head + insert[..pos] + tail); // phase 2: type the new region chunk by chunk

            // Follow the typing point every few frames (only while this tab is in front)
            if ((++tick % 4 == 0 || pos >= insertLen) && ReferenceEquals(ActiveTab, tab))
            {
                int line = 1;
                int upto = Math.Min(prefix + pos, tab.Content.Length);
                for (int i = 0; i < upto; i++) if (tab.Content[i] == '\n') line++;
                ScrollToLineRequested?.Invoke(line);
            }

            if (pos >= insertLen)
            {
                StopTypeTimer();
                StatusMessage = $"● Agent edited {tab.FileName}";
                AgentEditFlashRequested?.Invoke(prefix, insertLen);
            }
        };
        _typeTimer.Start();
        return true;
    }

    private void StopTypeTimer()
    {
        _typeTimer?.Stop();
        _typeTimer = null;
        _typingTab = null;
    }

    /// <summary>Fast-forward an in-flight reveal to its final content (a newer edit arrived).</summary>
    private void FinishTypingInstantly()
    {
        if (_typeTimer == null) return;
        var tab = _typingTab;
        string final = _typingFinal;
        StopTypeTimer();
        tab?.MarkOpened(final);
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
