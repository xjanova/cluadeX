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
    private readonly GitService _git;

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
    public ICommand CloneRepoCommand { get; }
    public ICommand CommitCommand { get; }
    public ICommand RefreshGitCommand { get; }
    public ICommand OpenChangeCommand { get; }
    public ICommand ShowDiffCommand { get; }

    // ── Source control (real git state, not just save buttons) ──
    public ObservableCollection<GitChangeItem> GitChanges { get; } = new();

    private string _gitBranch = "";
    public string GitBranch { get => _gitBranch; set { if (SetProperty(ref _gitBranch, value)) OnPropertyChanged(nameof(HasGitRepo)); } }
    public bool HasGitRepo => !string.IsNullOrEmpty(_gitBranch);

    private string _commitMessage = "";
    public string CommitMessage { get => _commitMessage; set => SetProperty(ref _commitMessage, value); }

    private bool _isCommitting;
    public bool IsCommitting { get => _isCommitting; set => SetProperty(ref _isCommitting, value); }

    public string GitChangeCount => GitChanges.Count == 0 ? "clean" : $"{GitChanges.Count} changed";

    // ── Embedded terminal (real shell, not the old decorative strip) ──
    private readonly EmbeddedTerminal _terminal = new();

    private bool _isTerminalOpen;
    public bool IsTerminalOpen
    {
        get => _isTerminalOpen;
        set
        {
            if (!SetProperty(ref _isTerminalOpen, value)) return;
            if (value && !_terminal.IsRunning && _workspace.HasWorkingDirectory)
            {
                AppendTerminal($"— PowerShell · {_workspace.WorkingDirectory} —");
                _terminal.Start(_workspace.WorkingDirectory);
            }
        }
    }

    private string _terminalOutput = "";
    public string TerminalOutput { get => _terminalOutput; private set => SetProperty(ref _terminalOutput, value); }

    private string _terminalInput = "";
    public string TerminalInput { get => _terminalInput; set => SetProperty(ref _terminalInput, value); }

    public ICommand ToggleTerminalCommand => _toggleTerminalCommand ??= new RelayCommand(() => IsTerminalOpen = !IsTerminalOpen);
    private ICommand? _toggleTerminalCommand;

    public ICommand RunTerminalCommand => _runTerminalCommand ??= new RelayCommand(RunTerminalInput);
    private ICommand? _runTerminalCommand;

    private void RunTerminalInput()
    {
        string cmd = TerminalInput?.Trim() ?? "";
        if (cmd.Length == 0) return;
        AppendTerminal($"> {cmd}");
        _terminal.Send(cmd);
        TerminalInput = "";
    }

    private const int TerminalMaxChars = 200_000;
    private void AppendTerminal(string line)
    {
        void Apply()
        {
            string next = _terminalOutput.Length == 0 ? line : _terminalOutput + "\n" + line;
            if (next.Length > TerminalMaxChars) next = next[^TerminalMaxChars..];
            TerminalOutput = next;
        }
        if (App.Current?.Dispatcher.CheckAccess() == true) Apply();
        else App.Current?.Dispatcher.BeginInvoke(Apply);
    }

    public CodeEditorViewModel(CodeWorkspaceService workspace, FileSystemService fs, ChatViewModel chatVm, SettingsService settings, GitService git)
    {
        _workspace = workspace;
        _fs = fs;
        _settings = settings;
        _git = git;
        ChatVM = chatVm;

        RefreshTreeCommand = new AsyncRelayCommand(RefreshTreeAsync);
        OpenNodeCommand = new AsyncRelayCommand<FileTreeNode>(OpenNodeAsync);
        CloseTabCommand = new RelayCommand<OpenFileTab>(CloseTab);
        SelectTabCommand = new RelayCommand<OpenFileTab>(t => { if (t != null) ActiveTab = t; });
        SaveActiveCommand = new AsyncRelayCommand(SaveActiveAsync, () => HasActiveTab && (_activeTab?.IsDirty ?? false));
        SaveAllCommand = new AsyncRelayCommand(SaveAllAsync);
        ToggleNodeCommand = new AsyncRelayCommand<FileTreeNode>(ToggleNodeAsync);
        OpenFolderCommand = ChatVM.OpenFolderCommand;
        CloneRepoCommand = ChatVM.CloneRepoCommand;
        // No CanExecute predicate — GitChanges mutates outside the focus cycle so a predicate would go
        // stale; CommitAsync self-guards (empty message / already committing) with a clear status line.
        CommitCommand = new AsyncRelayCommand(CommitAsync);
        RefreshGitCommand = new AsyncRelayCommand(RefreshGitAsync);
        OpenChangeCommand = new AsyncRelayCommand<GitChangeItem>(OpenChangeAsync);
        ShowDiffCommand = new AsyncRelayCommand<GitChangeItem>(ShowDiffAsync);

        // Refresh tree when working directory changes via ChatVM
        ChatVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.WorkingDirectory))
            {
                _ = RefreshTreeAsync();
                if (_workspace.HasWorkingDirectory)
                    _terminal.ChangeDirectory(_workspace.WorkingDirectory);
            }
        };

        // Live-follow: when the agent edits a file, open/refresh it in the editor and scroll to the change.
        ChatVM.FileMutatedByAgent += OnAgentFileMutated;

        _terminal.OutputReceived += AppendTerminal;
        _terminal.Exited += () => AppendTerminal("[terminal exited — next command restarts it]");
    }

    // ═══════════════ Source control ═══════════════

    /// <summary>Reload branch + changed-file list from `git status --short --branch` + local branches.</summary>
    public async Task RefreshGitAsync()
    {
        if (!_workspace.HasWorkingDirectory) { GitBranch = ""; GitChanges.Clear(); OnPropertyChanged(nameof(GitChangeCount)); return; }
        try
        {
            var r = await _git.StatusAsync();
            var branches = await _git.ListBranchesAsync(includeRemote: false);
            App.Current?.Dispatcher.Invoke(() =>
            {
                GitChanges.Clear();
                if (!r.Success) { GitBranch = ""; OnPropertyChanged(nameof(GitChangeCount)); return; }

                foreach (var raw in (r.Output ?? "").Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    if (line.StartsWith("## "))
                    {
                        // "## dev...origin/dev [ahead 1]" or "## master" — branch is up to the first '.' or space
                        string b = line[3..];
                        int cut = b.IndexOfAny(new[] { '.', ' ' });
                        GitBranch = cut > 0 ? b[..cut] : b;
                        continue;
                    }
                    if (line.Length < 4) continue;
                    string status = line[..2].Trim();
                    string path = line[3..].Trim();
                    // rename: "R  old -> new" — show the new name
                    int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
                    if (arrow >= 0) path = path[(arrow + 4)..];
                    GitChanges.Add(new GitChangeItem { Status = string.IsNullOrEmpty(status) ? "?" : status, Path = path });
                }
                OnPropertyChanged(nameof(GitChangeCount));

                // Local branches → switcher dropdown; keep the selection synced WITHOUT
                // re-triggering a checkout.
                _suppressBranchSwitch = true;
                try
                {
                    GitBranches.Clear();
                    if (branches.Success)
                    {
                        foreach (var bl in (branches.Output ?? "").Split('\n'))
                        {
                            string name = bl.TrimEnd('\r').TrimStart('*', '+', ' ').Trim();
                            if (name.Length == 0 || name.Contains("HEAD")) continue;
                            GitBranches.Add(name);
                        }
                    }
                    SelectedBranch = GitBranch;
                }
                finally { _suppressBranchSwitch = false; }
            });
        }
        catch { /* git panel is best-effort */ }
    }

    /// <summary>Stage everything and commit with the panel's message — the IDE-style commit.</summary>
    private async Task CommitAsync()
    {
        string msg = CommitMessage?.Trim() ?? "";
        if (string.IsNullOrEmpty(msg)) { StatusMessage = "⚠ Type a commit message first"; return; }
        if (IsCommitting) return;

        IsCommitting = true;
        try
        {
            var add = await _git.AddAsync(".");
            if (!add.Success) { StatusMessage = $"✗ stage failed: {add.Error}".Trim(); return; }
            var commit = await _git.CommitAsync(msg);
            if (commit.Success)
            {
                CommitMessage = "";
                // "​[dev 537804b] update readme" → surface the first line
                string first = (commit.Output ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "committed";
                StatusMessage = $"✓ {first}";
            }
            else
            {
                StatusMessage = $"✗ commit failed: {(commit.Error ?? commit.Output ?? "").Split('\n').FirstOrDefault()}".Trim();
            }
        }
        catch (Exception ex) { StatusMessage = $"✗ commit failed: {ex.Message}"; }
        finally
        {
            IsCommitting = false;
            await RefreshGitAsync();
            _ = _workspace.EnrichGitStatusAsync(Tree);
        }
    }

    /// <summary>Open a changed file from the source-control list in an editor tab.</summary>
    private async Task OpenChangeAsync(GitChangeItem? item)
    {
        if (item == null || !_workspace.HasWorkingDirectory) return;
        try
        {
            string full = Path.GetFullPath(Path.Combine(_workspace.WorkingDirectory, item.Path));
            var existing = Tabs.FirstOrDefault(t => string.Equals(t.FullPath, full, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { ActiveTab = existing; return; }
            var loaded = await _workspace.OpenFileAsync(full);
            if (loaded == null) return;
            Tabs.Add(loaded);
            ActiveTab = loaded;
        }
        catch { /* best-effort */ }
    }

    /// <summary>Show a changed file's diff (git diff; untracked = whole file as additions) in a
    /// read-only-ish tab with Patch highlighting — the review step before committing.</summary>
    private async Task ShowDiffAsync(GitChangeItem? item)
    {
        if (item == null || !_workspace.HasWorkingDirectory) return;
        try
        {
            string diffText;
            if (item.Status.StartsWith("?"))
            {
                // Untracked — git diff shows nothing; present the whole file as an addition.
                string full = Path.GetFullPath(Path.Combine(_workspace.WorkingDirectory, item.Path));
                string body = File.Exists(full) ? await File.ReadAllTextAsync(full) : "";
                var lines = body.Replace("\r\n", "\n").Split('\n');
                diffText = $"--- /dev/null\n+++ b/{item.Path}\n@@ -0,0 +1,{lines.Length} @@\n"
                         + string.Join("\n", lines.Select(l => "+" + l));
            }
            else
            {
                var r = await _git.DiffAsync(item.Path);
                diffText = r.Success && !string.IsNullOrWhiteSpace(r.Output)
                    ? r.Output
                    : $"(no unstaged diff for {item.Path} — the change may already be staged)";
            }

            // Materialize as a real .diff file so the normal tab pipeline (and Patch highlighting) applies.
            string tmpDir = Path.Combine(Path.GetTempPath(), "cluadex-diffs");
            Directory.CreateDirectory(tmpDir);
            string tmp = Path.Combine(tmpDir, item.FileName + ".diff");
            await File.WriteAllTextAsync(tmp, diffText);

            var existing = Tabs.FirstOrDefault(t => string.Equals(t.FullPath, tmp, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { existing.MarkOpened(diffText); ActiveTab = existing; return; }
            var loaded = await _workspace.OpenFileAsync(tmp);
            if (loaded == null) return;
            Tabs.Add(loaded);
            ActiveTab = loaded;
        }
        catch { /* diff view is best-effort */ }
    }

    // ── Branch switcher ──
    public ObservableCollection<string> GitBranches { get; } = new();

    private bool _suppressBranchSwitch;
    private string? _selectedBranch;
    public string? SelectedBranch
    {
        get => _selectedBranch;
        set
        {
            if (!SetProperty(ref _selectedBranch, value)) return;
            if (_suppressBranchSwitch || string.IsNullOrEmpty(value) || value == GitBranch) return;
            _ = SwitchBranchAsync(value);
        }
    }

    private async Task SwitchBranchAsync(string branch)
    {
        try
        {
            var r = await _git.CheckoutAsync(branch);
            if (r.Success)
            {
                StatusMessage = $"✓ Switched to {branch}";
                await RefreshTreeAsync();   // tree content may differ on the new branch (also reloads git)
            }
            else
            {
                string err = (r.Error ?? r.Output ?? "").Split('\n').FirstOrDefault() ?? "checkout failed";
                StatusMessage = $"✗ {err}";
                // Revert the dropdown to the real branch without re-triggering a checkout.
                _suppressBranchSwitch = true;
                try { SelectedBranch = GitBranch; } finally { _suppressBranchSwitch = false; }
            }
        }
        catch (Exception ex) { StatusMessage = $"✗ checkout failed: {ex.Message}"; }
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

            // A brand-new file (write_file created it) isn't in the explorer yet — refresh the
            // deepest populated ancestor folder so the tree shows it without collapsing the rest.
            if (!TreeContains(full))
                RefreshAncestorFolder(full);

            _ = _workspace.EnrichGitStatusAsync(Tree);   // live git badges
            _ = RefreshGitAsync();                        // live source-control panel
        }
        catch { /* live-follow is best-effort — never disrupt the agent run */ }
    }

    private bool TreeContains(string fullPath)
    {
        bool Walk(FileTreeNode n)
        {
            if (string.Equals(n.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var c in n.Children) if (Walk(c)) return true;
            return false;
        }
        foreach (var r in Tree) if (Walk(r)) return true;
        return false;
    }

    /// <summary>Re-populate the deepest already-loaded folder that should contain this path, so a
    /// newly created file appears in the explorer without rebuilding (and collapsing) the whole tree.</summary>
    private void RefreshAncestorFolder(string fullPath)
    {
        try
        {
            if (Tree.Count == 0) { _ = RefreshTreeAsync(); return; }

            FileTreeNode? best = null;
            void Walk(FileTreeNode n)
            {
                if (!n.IsDirectory) return;
                if (!fullPath.StartsWith(n.FullPath, StringComparison.OrdinalIgnoreCase)) return;
                if (best == null || n.FullPath.Length > best.FullPath.Length) best = n;
                foreach (var c in n.Children) Walk(c);
            }
            foreach (var r in Tree) Walk(r);

            if (best == null) { _ = RefreshTreeAsync(); return; }
            _workspace.PopulateChildren(best);
            best.IsExpanded = true;
            _ = _workspace.EnrichGitStatusAsync(new[] { best });
            OnPropertyChanged(nameof(ProjectFileCount));
        }
        catch { /* explorer refresh is best-effort */ }
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

            // Best-effort: tag with git status badges + load the source-control panel
            _ = _workspace.EnrichGitStatusAsync(Tree);
            _ = RefreshGitAsync();

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

/// <summary>One changed file in the source-control panel (from `git status --short`).</summary>
public class GitChangeItem
{
    public string Status { get; set; } = "?";   // M / A / D / R / ?? …
    public string Path { get; set; } = "";
    public string FileName => System.IO.Path.GetFileName(Path);
    /// <summary>VS Code-style status color: modified=amber, added/untracked=green, deleted=red.</summary>
    public string StatusColor => Status switch
    {
        "A" or "??" => "#7EE0A3",
        "D" => "#FF7A93",
        "R" => "#8AB8FF",
        _ => "#FFD37E",
    };
}
