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
    private readonly CodeIntelligenceService _intel;
    private readonly DebugAdapterService _debug;

    public ChatViewModel ChatVM { get; }

    public ObservableCollection<FileTreeNode> Tree { get; } = new();
    public ObservableCollection<OpenFileTab> Tabs { get; } = new();

    private OpenFileTab? _activeTab;
    public OpenFileTab? ActiveTab
    {
        get => _activeTab;
        set
        {
            var previous = _activeTab;
            if (SetProperty(ref _activeTab, value))
            {
                // Drive the tab-strip highlight (the XAML used to bind a property that
                // didn't exist, so no tab ever looked selected).
                if (previous != null) previous.IsActive = false;
                if (value != null) value.IsActive = true;

                // Merge-conflict banner follows the active buffer.
                if (previous != null) previous.PropertyChanged -= OnActiveTabContentChanged;
                if (value != null) value.PropertyChanged += OnActiveTabContentChanged;
                ScanConflictsNow();
                UpdateDebugTarget();

                // Switching files means you want the file, not the diff you were reading.
                if (IsDiffViewActive) CloseDiffCommand.Execute(null);

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

    public CodeEditorViewModel(CodeWorkspaceService workspace, FileSystemService fs, ChatViewModel chatVm,
        SettingsService settings, GitService git, CodeIntelligenceService intel, RepoMapService repoMap,
        DebugAdapterService debug)
    {
        _workspace = workspace;
        _fs = fs;
        _settings = settings;
        _git = git;
        _intel = intel;
        _debug = debug;
        _repoMapInvalidate = repoMap.Invalidate;
        ChatVM = chatVm;
        HookDebugEvents();

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
                // Results (and any half-built rename plan) belong to the OLD project — keeping them
                // would offer to open paths that no longer exist, or rename files in a different repo.
                ClearSearchCommand.Execute(null);
                CancelRenameCommand.Execute(null);
                _intel.InvalidateSymbolIndex();   // completions must not offer the old project's symbols
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

    /// <summary>Open any file by absolute path in an editor tab (used by the command palette).</summary>
    public async Task OpenPathAsync(string fullPath)
    {
        try
        {
            var existing = Tabs.FirstOrDefault(t => string.Equals(t.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { ActiveTab = existing; return; }
            var loaded = await _workspace.OpenFileAsync(fullPath);
            if (loaded == null)
            {
                StatusMessage = $"Can't open {Path.GetFileName(fullPath)} (binary or too large)";
                return;
            }
            Tabs.Add(loaded);
            ActiveTab = loaded;
        }
        catch (Exception ex) { StatusMessage = $"Open failed: {ex.Message}"; }
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

    /// <summary>
    /// Open a changed file in the side-by-side diff editor: HEAD on the left, working tree on the
    /// right, aligned row by row. Replaces the old approach of writing a unified-diff text file to
    /// temp and opening it as a tab — that was a patch you had to read, not a diff you could review.
    /// </summary>
    private async Task ShowDiffAsync(GitChangeItem? item)
    {
        if (item == null || !_workspace.HasWorkingDirectory) return;
        try
        {
            string full = Path.GetFullPath(Path.Combine(_workspace.WorkingDirectory, item.Path));
            string working = File.Exists(full) ? await File.ReadAllTextAsync(full) : "";

            // Untracked files have no HEAD version — everything is an addition.
            string baseline = "";
            if (!item.Status.StartsWith("?"))
            {
                var head = await _git.ShowFileAsync("HEAD", item.Path);
                // Failure here is normal for a newly added file; treat it as "no baseline".
                baseline = head.Success ? (head.Output ?? "") : "";
            }

            var diff = await Task.Run(() => DiffService.BuildSideBySide(baseline, working));

            DiffRows = diff.Rows;
            DiffTitle = item.Path;
            DiffSummary = diff.HasChanges
                ? $"+{diff.Added:N0} −{diff.Removed:N0}"
                  + (item.Status.StartsWith("?") ? " · untracked (no HEAD version)" : " · vs HEAD")
                  + (diff.Truncated ? $" · ⚠ {diff.Note}" : "")
                : diff.Note ?? "No differences against HEAD.";
            IsDiffViewActive = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ Diff failed: {ex.Message}";
        }
    }

    // ═══════════════ Side-by-side diff view ═══════════════

    private bool _isDiffViewActive;
    public bool IsDiffViewActive
    {
        get => _isDiffViewActive;
        set => SetProperty(ref _isDiffViewActive, value);
    }

    private List<DiffRow> _diffRows = new();
    public List<DiffRow> DiffRows { get => _diffRows; set => SetProperty(ref _diffRows, value); }

    private string _diffTitle = "";
    public string DiffTitle { get => _diffTitle; set => SetProperty(ref _diffTitle, value); }

    private string _diffSummary = "";
    public string DiffSummary { get => _diffSummary; set => SetProperty(ref _diffSummary, value); }

    public ICommand CloseDiffCommand => _closeDiffCommand ??= new RelayCommand(() =>
    {
        IsDiffViewActive = false;
        DiffRows = new List<DiffRow>();   // thousands of rows shouldn't sit in memory once dismissed
        DiffTitle = "";
        DiffSummary = "";
    });
    private ICommand? _closeDiffCommand;

    // ═══════════════ Merge conflicts ═══════════════

    public ObservableCollection<ConflictBlock> Conflicts { get; } = new();

    private bool _hasConflicts;
    public bool HasConflicts { get => _hasConflicts; set => SetProperty(ref _hasConflicts, value); }

    private string _conflictSummary = "";
    public string ConflictSummary { get => _conflictSummary; set => SetProperty(ref _conflictSummary, value); }

    private bool _conflictsMalformed;
    /// <summary>A conflict marker has no closing marker — auto-resolution is refused (guessing where
    /// a block ends is how a resolver silently eats code).</summary>
    public bool ConflictsMalformed { get => _conflictsMalformed; set => SetProperty(ref _conflictsMalformed, value); }

    private System.Windows.Threading.DispatcherTimer? _conflictDebounce;

    private void OnActiveTabContentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OpenFileTab.Content)) ScheduleConflictScan();
    }

    /// <summary>Re-scan the active buffer for conflict markers, coalescing keystrokes.</summary>
    private void ScheduleConflictScan()
    {
        _conflictDebounce ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _conflictDebounce.Tick -= OnConflictDebounceTick;
        _conflictDebounce.Tick += OnConflictDebounceTick;
        _conflictDebounce.Stop();
        _conflictDebounce.Start();
    }

    private void OnConflictDebounceTick(object? sender, EventArgs e)
    {
        _conflictDebounce?.Stop();
        ScanConflictsNow();
    }

    private void ScanConflictsNow()
    {
        var tab = _activeTab;
        Conflicts.Clear();

        if (tab == null)
        {
            HasConflicts = false;
            ConflictsMalformed = false;
            ConflictSummary = "";
            return;
        }

        var scan = DiffService.ScanConflicts(tab.Content);
        foreach (var c in scan.Conflicts) Conflicts.Add(c);

        HasConflicts = scan.HasConflicts || scan.Malformed;
        ConflictsMalformed = scan.Malformed;
        ConflictSummary = scan.Malformed
            ? "⚠ A conflict marker has no closing '>>>>>>>' — fix it by hand; auto-resolve is disabled."
            : scan.Conflicts.Count == 0
                ? ""
                : $"{scan.Conflicts.Count} merge conflict{(scan.Conflicts.Count == 1 ? "" : "s")} in {tab.FileName}";
    }

    public ICommand AcceptOursCommand => _acceptOursCommand ??=
        new RelayCommand<ConflictBlock>(b => ResolveConflict(b, ConflictChoice.Ours));
    private ICommand? _acceptOursCommand;

    public ICommand AcceptTheirsCommand => _acceptTheirsCommand ??=
        new RelayCommand<ConflictBlock>(b => ResolveConflict(b, ConflictChoice.Theirs));
    private ICommand? _acceptTheirsCommand;

    public ICommand AcceptBothCommand => _acceptBothCommand ??=
        new RelayCommand<ConflictBlock>(b => ResolveConflict(b, ConflictChoice.Both));
    private ICommand? _acceptBothCommand;

    public ICommand GoToConflictCommand => _goToConflictCommand ??=
        new RelayCommand<ConflictBlock>(b => { if (b != null) ScrollToLineRequested?.Invoke(b.StartLine); });
    private ICommand? _goToConflictCommand;

    public ICommand AcceptAllOursCommand => _acceptAllOursCommand ??=
        new RelayCommand(() => ResolveAllConflicts(ConflictChoice.Ours));
    private ICommand? _acceptAllOursCommand;

    public ICommand AcceptAllTheirsCommand => _acceptAllTheirsCommand ??=
        new RelayCommand(() => ResolveAllConflicts(ConflictChoice.Theirs));
    private ICommand? _acceptAllTheirsCommand;

    /// <summary>
    /// Resolve one block in the open buffer. Writes to the TAB, not to disk — the change becomes an
    /// ordinary unsaved edit the user can undo (Ctrl+Z) and must save deliberately, instead of a
    /// silent write behind their back.
    /// </summary>
    private void ResolveConflict(ConflictBlock? block, ConflictChoice choice)
    {
        var tab = _activeTab;
        if (block == null || tab == null) return;
        if (ConflictsMalformed)
        {
            StatusMessage = "⚠ Fix the unterminated conflict marker by hand first";
            return;
        }

        try
        {
            // Resolve by INDEX against a fresh scan of the current buffer — the block we were handed
            // may have been captured before another conflict was resolved or the file hand-edited.
            string updated = DiffService.ResolveConflict(tab.Content, block.Index, choice);
            if (string.Equals(updated, tab.Content, StringComparison.Ordinal))
            {
                StatusMessage = "Nothing changed — the conflict may already be resolved";
                ScanConflictsNow();
                return;
            }

            tab.Content = updated;   // marks the tab dirty; the editor picks it up incrementally
            ScanConflictsNow();
            StatusMessage = Conflicts.Count == 0
                ? $"✓ All conflicts resolved in {tab.FileName} — review and save"
                : $"✓ Conflict resolved · {Conflicts.Count} left";
        }
        catch (Exception ex) { StatusMessage = $"✗ Resolve failed: {ex.Message}"; }
    }

    private void ResolveAllConflicts(ConflictChoice choice)
    {
        var tab = _activeTab;
        if (tab == null || Conflicts.Count == 0) return;
        if (ConflictsMalformed)
        {
            StatusMessage = "⚠ Fix the unterminated conflict marker by hand first";
            return;
        }

        int count = Conflicts.Count;
        string side = choice == ConflictChoice.Ours ? Conflicts[0].OursLabel : Conflicts[0].TheirsLabel;
        var confirm = System.Windows.MessageBox.Show(
            $"Resolve all {count} conflict(s) in {tab.FileName} by keeping '{side}'?\n\n"
            + "The other side will be discarded. This edits the open tab — you can undo it (Ctrl+Z) "
            + "and nothing is written until you save.",
            "Resolve all conflicts", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            tab.Content = DiffService.ResolveAll(tab.Content, choice);
            ScanConflictsNow();
            StatusMessage = $"✓ Resolved {count} conflict(s) keeping '{side}' — review and save";
        }
        catch (Exception ex) { StatusMessage = $"✗ Resolve failed: {ex.Message}"; }
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

    // ═══════════════ Search across files + code navigation ═══════════════
    // The left panel switches between EXPLORER and SEARCH (VS Code's activity model). The SEARCH
    // side is also where go-to-definition (multiple candidates), find-references and rename preview
    // land, so there is one results list to learn instead of three.

    private WorkbenchPanel _activePanel = WorkbenchPanel.Explorer;

    /// <summary>Which of the three left-panel modes is showing (VS Code's activity-bar model).</summary>
    public WorkbenchPanel ActivePanel
    {
        get => _activePanel;
        set
        {
            if (!SetProperty(ref _activePanel, value)) return;
            OnPropertyChanged(nameof(IsExplorerPanelActive));
            OnPropertyChanged(nameof(IsSearchPanelActive));
            OnPropertyChanged(nameof(IsDebugPanelActive));
        }
    }

    public bool IsExplorerPanelActive => _activePanel == WorkbenchPanel.Explorer;
    public bool IsDebugPanelActive => _activePanel == WorkbenchPanel.Debug;

    public bool IsSearchPanelActive
    {
        get => _activePanel == WorkbenchPanel.Search;
        // Kept as a settable bool so the navigation commands can just say "show me the results".
        set { if (value) ActivePanel = WorkbenchPanel.Search; else if (IsSearchPanelActive) ActivePanel = WorkbenchPanel.Explorer; }
    }

    public ICommand ShowExplorerCommand => _showExplorerCommand ??=
        new RelayCommand(() => ActivePanel = WorkbenchPanel.Explorer);
    private ICommand? _showExplorerCommand;

    public ICommand ShowSearchCommand => _showSearchCommand ??= new RelayCommand(() =>
    {
        ActivePanel = WorkbenchPanel.Search;
        SearchFocusRequested?.Invoke();
    });
    private ICommand? _showSearchCommand;

    public ICommand ShowDebugCommand => _showDebugCommand ??= new AsyncRelayCommand(async () =>
    {
        ActivePanel = WorkbenchPanel.Debug;
        await RefreshDebugAdaptersAsync();
    });
    private ICommand? _showDebugCommand;

    /// <summary>Raised when the search box should take keyboard focus (Ctrl+Shift+F, panel switch).</summary>
    public event Action? SearchFocusRequested;

    /// <summary>Raised after a result is opened: select (1-based line, 0-based column, length).</summary>
    public event Action<int, int, int>? SelectRangeRequested;

    // ── Query + options ──

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            // Typing replaces a definition/reference/rename result set with a plain text search.
            if (_searchMode != WorkbenchSearchMode.Text) SetSearchMode(WorkbenchSearchMode.Text);
            ScheduleSearch();
        }
    }

    private bool _searchIsRegex;
    public bool SearchIsRegex { get => _searchIsRegex; set { if (SetProperty(ref _searchIsRegex, value)) ScheduleSearch(); } }

    private bool _searchMatchCase;
    public bool SearchMatchCase { get => _searchMatchCase; set { if (SetProperty(ref _searchMatchCase, value)) ScheduleSearch(); } }

    private bool _searchWholeWord;
    public bool SearchWholeWord { get => _searchWholeWord; set { if (SetProperty(ref _searchWholeWord, value)) ScheduleSearch(); } }

    private string _searchInclude = "";
    public string SearchInclude { get => _searchInclude; set { if (SetProperty(ref _searchInclude, value)) ScheduleSearch(); } }

    private string _searchExclude = "";
    public string SearchExclude { get => _searchExclude; set { if (SetProperty(ref _searchExclude, value)) ScheduleSearch(); } }

    private bool _searchFiltersExpanded;
    public bool SearchFiltersExpanded { get => _searchFiltersExpanded; set => SetProperty(ref _searchFiltersExpanded, value); }

    public ICommand ToggleSearchFiltersCommand => _toggleSearchFiltersCommand ??=
        new RelayCommand(() => SearchFiltersExpanded = !SearchFiltersExpanded);
    private ICommand? _toggleSearchFiltersCommand;

    // ── Results ──

    public ObservableCollection<SearchResultGroup> SearchResults { get; } = new();

    private string _searchSummary = "";
    public string SearchSummary { get => _searchSummary; set => SetProperty(ref _searchSummary, value); }

    private string _searchError = "";
    public string SearchError
    {
        get => _searchError;
        set
        {
            if (!SetProperty(ref _searchError, value)) return;
            OnPropertyChanged(nameof(HasSearchError));
            OnPropertyChanged(nameof(HasNoResults));   // an error replaces the "no results" line
        }
    }
    public bool HasSearchError => !string.IsNullOrEmpty(_searchError);

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        // HasNoResults is derived from this — without the extra notify, "No results." stays on
        // screen through the next scan (and after a cancel) because nothing told the UI to re-ask.
        set { if (SetProperty(ref _isSearching, value)) OnPropertyChanged(nameof(HasNoResults)); }
    }

    private bool _hasSearched;
    public bool HasSearched
    {
        get => _hasSearched;
        set { if (SetProperty(ref _hasSearched, value)) OnPropertyChanged(nameof(HasNoResults)); }
    }

    public bool HasNoResults => _hasSearched && !_isSearching && SearchResults.Count == 0 && !HasSearchError;

    private WorkbenchSearchMode _searchMode = WorkbenchSearchMode.Text;
    public WorkbenchSearchMode SearchMode => _searchMode;
    public bool IsRenameMode => _searchMode == WorkbenchSearchMode.Rename;

    /// <summary>Explains what the current result list actually is — a plain text search, a
    /// definition lookup, a reference sweep, or a pending rename.</summary>
    private string _searchContextLabel = "";
    public string SearchContextLabel
    {
        get => _searchContextLabel;
        set { if (SetProperty(ref _searchContextLabel, value)) OnPropertyChanged(nameof(HasSearchContext)); }
    }
    public bool HasSearchContext => !string.IsNullOrEmpty(_searchContextLabel);

    private void SetSearchMode(WorkbenchSearchMode mode)
    {
        if (_searchMode == mode) return;
        _searchMode = mode;
        OnPropertyChanged(nameof(SearchMode));
        OnPropertyChanged(nameof(IsRenameMode));
        if (mode != WorkbenchSearchMode.Rename) SearchContextLabel = "";
    }

    public ICommand ClearSearchCommand => _clearSearchCommand ??= new RelayCommand(() =>
    {
        _searchDebounce?.Stop();
        _searchCts?.Cancel();
        _searchText = "";
        OnPropertyChanged(nameof(SearchText));
        SetSearchMode(WorkbenchSearchMode.Text);
        SearchContextLabel = "";
        ResetResults();
        HasSearched = false;
        SearchSummary = "";
        SearchError = "";
    });
    private ICommand? _clearSearchCommand;

    public ICommand OpenMatchCommand => _openMatchCommand ??= new AsyncRelayCommand<SearchMatch>(OpenMatchAsync);
    private ICommand? _openMatchCommand;

    public ICommand ToggleResultGroupCommand => _toggleResultGroupCommand ??=
        new RelayCommand<SearchResultGroup>(g => { if (g != null) g.IsExpanded = !g.IsExpanded; });
    private ICommand? _toggleResultGroupCommand;

    // ── Debounced, cancellable execution ──

    private System.Windows.Threading.DispatcherTimer? _searchDebounce;
    private CancellationTokenSource? _searchCts;

    /// <summary>
    /// Coalesce keystrokes into one scan. Without this, typing "Service" fires seven full-workspace
    /// scans and the last one to finish (not the last one started) wins.
    /// </summary>
    private void ScheduleSearch()
    {
        _searchDebounce ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _searchDebounce.Tick -= OnSearchDebounceTick;
        _searchDebounce.Tick += OnSearchDebounceTick;
        _searchDebounce.Stop();

        if (string.IsNullOrEmpty(_searchText))
        {
            _searchCts?.Cancel();
            ResetResults();
            HasSearched = false;
            SearchSummary = "";
            SearchError = "";
            IsSearching = false;
            return;
        }
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce?.Stop();
        _ = RunSearchAsync();
    }

    public ICommand RunSearchCommand => _runSearchCommand ??= new AsyncRelayCommand(RunSearchAsync);
    private ICommand? _runSearchCommand;

    private async Task RunSearchAsync()
    {
        if (string.IsNullOrEmpty(_searchText)) return;

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        IsSearching = true;
        SearchError = "";
        try
        {
            var outcome = await _intel.SearchAsync(new SearchQuery
            {
                Text = _searchText,
                IsRegex = _searchIsRegex,
                MatchCase = _searchMatchCase,
                WholeWord = _searchWholeWord,
                Include = _searchInclude,
                Exclude = _searchExclude,
            }, cts.Token);

            // A newer search started while this one ran — its results are the stale ones, drop them.
            if (!ReferenceEquals(_searchCts, cts)) return;
            ApplyOutcome(outcome);
        }
        catch (OperationCanceledException) { /* superseded — the newer scan owns the UI */ }
        catch (Exception ex)
        {
            if (ReferenceEquals(_searchCts, cts)) SearchError = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) IsSearching = false;
        }
    }

    /// <summary>Push a scan result into the results list + summary. Caps that actually bit are
    /// stated — a silent truncation reads as "that's everything" when it isn't.</summary>
    private void ApplyOutcome(SearchOutcome outcome)
    {
        ResetResults();
        HasSearched = true;

        if (!string.IsNullOrEmpty(outcome.Error))
        {
            SearchError = outcome.Error!;
            SearchSummary = "";
            OnPropertyChanged(nameof(HasNoResults));
            return;
        }

        foreach (var g in outcome.Files)
            SearchResults.Add(new SearchResultGroup(g));

        SearchSummary = outcome.TotalMatches == 0
            ? $"No results · {outcome.FilesScanned:N0} files searched"
            : $"{outcome.TotalMatches:N0} result{(outcome.TotalMatches == 1 ? "" : "s")} in "
              + $"{outcome.Files.Count:N0} file{(outcome.Files.Count == 1 ? "" : "s")} · "
              + $"{outcome.FilesScanned:N0} searched · {outcome.Elapsed.TotalSeconds:0.00}s"
              + (outcome.Truncated ? " · ⚠ capped (results incomplete)" : "");

        OnPropertyChanged(nameof(HasNoResults));
    }

    private void ResetResults()
    {
        SearchResults.Clear();
        OnPropertyChanged(nameof(HasNoResults));
    }

    /// <summary>Open the file a match lives in and select the matched text.</summary>
    private async Task OpenMatchAsync(SearchMatch? match)
    {
        if (match == null) return;
        await OpenPathAsync(match.FullPath);
        SelectRangeRequested?.Invoke(match.Line, match.Column, match.Length);
    }

    // ── Go to definition / find references (driven from the editor, F12 / Shift+F12) ──

    /// <summary>
    /// Jump to a symbol's declaration. One hit jumps straight there; several list themselves in the
    /// search panel so the user picks. <paramref name="file"/>/<paramref name="line"/>/<paramref
    /// name="character"/> are 0-based LSP coordinates, used only when a language server is connected.
    /// </summary>
    public async Task GoToDefinitionAsync(string symbol, string? file, int line, int character)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            StatusMessage = "Put the caret on a symbol first";
            return;
        }

        StatusMessage = $"Looking up {symbol}…";
        var result = await _intel.FindDefinitionsAsync(symbol, file, line, character);

        if (!string.IsNullOrEmpty(result.Error)) { StatusMessage = $"✗ {result.Error}"; return; }
        if (result.Locations.Count == 0)
        {
            StatusMessage = $"No declaration found for '{symbol}'";
            return;
        }

        string engine = result.Engine == NavigationEngine.LanguageServer ? "language server" : "workspace index";

        if (result.Locations.Count == 1)
        {
            var only = result.Locations[0];
            await OpenPathAsync(only.FullPath);
            SelectRangeRequested?.Invoke(only.Line, 0, 0);
            StatusMessage = $"→ {only.FileName}:{only.Line} · {engine}";
            return;
        }

        // Several candidates — show them all rather than guessing.
        ShowLocations(result.Locations, WorkbenchSearchMode.Definitions,
            $"{result.Locations.Count} declarations of '{symbol}' · {engine}");
        StatusMessage = $"{result.Locations.Count} declarations of '{symbol}'";
    }

    /// <summary>Every whole-word use of a symbol, listed in the search panel.</summary>
    public async Task FindReferencesAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            StatusMessage = "Put the caret on a symbol first";
            return;
        }

        IsSearchPanelActive = true;
        SetSearchMode(WorkbenchSearchMode.References);
        IsSearching = true;
        SearchError = "";
        SearchContextLabel = $"References to '{symbol}'";

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        try
        {
            var outcome = await _intel.FindReferencesAsync(symbol, cts.Token);
            if (!ReferenceEquals(_searchCts, cts)) return;

            // Keep the box in sync so the user can tweak the query from here.
            _searchText = symbol;
            OnPropertyChanged(nameof(SearchText));
            _searchWholeWord = true; OnPropertyChanged(nameof(SearchWholeWord));
            _searchMatchCase = true; OnPropertyChanged(nameof(SearchMatchCase));
            _searchIsRegex = false; OnPropertyChanged(nameof(SearchIsRegex));

            ApplyOutcome(outcome);
            StatusMessage = $"{outcome.TotalMatches} reference(s) to '{symbol}' in {outcome.Files.Count} file(s)";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (ReferenceEquals(_searchCts, cts)) SearchError = ex.Message; }
        finally { if (ReferenceEquals(_searchCts, cts)) IsSearching = false; }
    }

    private void ShowLocations(List<CodeLocation> locations, WorkbenchSearchMode mode, string context)
    {
        IsSearchPanelActive = true;
        SetSearchMode(mode);
        SearchContextLabel = context;
        SearchError = "";
        ResetResults();
        HasSearched = true;

        foreach (var byFile in locations.GroupBy(l => l.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            var first = byFile.First();
            var group = new SearchFileGroup { RelPath = first.RelPath, FullPath = first.FullPath };
            foreach (var loc in byFile)
                group.Matches.Add(new SearchMatch
                {
                    RelPath = loc.RelPath,
                    FullPath = loc.FullPath,
                    Line = loc.Line,
                    Column = 0,
                    Length = 0,
                    LineText = loc.Snippet,
                    PreviewColumn = -1,   // no highlight span for a declaration line
                });
            SearchResults.Add(new SearchResultGroup(group));
        }

        SearchSummary = $"{locations.Count} location{(locations.Count == 1 ? "" : "s")}";
        OnPropertyChanged(nameof(HasNoResults));
    }

    // ── Rename symbol (preview → confirm → apply) ──

    private string _renameOldName = "";
    public string RenameOldName { get => _renameOldName; set => SetProperty(ref _renameOldName, value); }

    private string _renameNewName = "";
    public string RenameNewName { get => _renameNewName; set => SetProperty(ref _renameNewName, value); }

    private RenamePlan? _renamePlan;

    private bool _canApplyRename;
    public bool CanApplyRename { get => _canApplyRename; set => SetProperty(ref _canApplyRename, value); }

    private string _renameApplyLabel = "";
    public string RenameApplyLabel { get => _renameApplyLabel; set => SetProperty(ref _renameApplyLabel, value); }

    /// <summary>Enter rename mode from the editor (F2) with the symbol under the caret.</summary>
    public void BeginRename(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            StatusMessage = "Put the caret on a symbol first";
            return;
        }

        IsSearchPanelActive = true;
        SetSearchMode(WorkbenchSearchMode.Rename);
        RenameOldName = symbol;
        RenameNewName = symbol;
        _renamePlan = null;
        CanApplyRename = false;
        RenameApplyLabel = "";
        SearchContextLabel = $"Rename '{symbol}'";
        SearchError = "";
        ResetResults();
        HasSearched = false;
        SearchSummary = "";
        RenameFocusRequested?.Invoke();
    }

    /// <summary>Raised when the rename input should take focus.</summary>
    public event Action? RenameFocusRequested;

    public ICommand CancelRenameCommand => _cancelRenameCommand ??= new RelayCommand(() =>
    {
        _renamePlan = null;
        CanApplyRename = false;
        RenameApplyLabel = "";
        SetSearchMode(WorkbenchSearchMode.Text);
        SearchContextLabel = "";
        ResetResults();
        HasSearched = false;
        SearchSummary = "";
        SearchError = "";
    });
    private ICommand? _cancelRenameCommand;

    public ICommand PreviewRenameCommand => _previewRenameCommand ??= new AsyncRelayCommand(PreviewRenameAsync);
    private ICommand? _previewRenameCommand;

    private async Task PreviewRenameAsync()
    {
        _renamePlan = null;
        CanApplyRename = false;
        RenameApplyLabel = "";
        SearchError = "";
        IsSearching = true;

        try
        {
            var plan = await _intel.PrepareRenameAsync(RenameOldName, RenameNewName);
            if (!string.IsNullOrEmpty(plan.Error))
            {
                SearchError = plan.Error!;
                ResetResults();
                HasSearched = true;
                SearchSummary = "";
                OnPropertyChanged(nameof(HasNoResults));
                return;
            }

            _renamePlan = plan;
            ResetResults();
            HasSearched = true;
            foreach (var g in plan.Files) SearchResults.Add(new SearchResultGroup(g));

            SearchSummary = plan.TotalEdits == 0
                ? $"'{plan.OldName}' not found — nothing to rename"
                : $"{plan.TotalEdits:N0} occurrence{(plan.TotalEdits == 1 ? "" : "s")} in "
                  + $"{plan.Files.Count:N0} file{(plan.Files.Count == 1 ? "" : "s")}"
                  + (plan.Truncated ? " · ⚠ capped (preview incomplete — do not apply)" : "");

            // Refuse to apply a truncated plan: a partial rename leaves the repo half-renamed,
            // which is worse than not renaming at all.
            CanApplyRename = plan.TotalEdits > 0 && !plan.Truncated;
            RenameApplyLabel = CanApplyRename
                ? $"Apply to {plan.Files.Count} file{(plan.Files.Count == 1 ? "" : "s")}"
                : "";
            OnPropertyChanged(nameof(HasNoResults));
        }
        finally { IsSearching = false; }
    }

    public ICommand ApplyRenameCommand => _applyRenameCommand ??= new AsyncRelayCommand(ApplyRenameAsync);
    private ICommand? _applyRenameCommand;

    private async Task ApplyRenameAsync()
    {
        var plan = _renamePlan;
        if (plan == null || !CanApplyRename) { StatusMessage = "Preview the rename first"; return; }

        // Never write over a tab the user has unsaved edits in — the same rule the agent
        // live-follow obeys. Their typing is not ours to discard.
        var dirty = Tabs.Where(t => t.IsDirty
                        && plan.Files.Any(f => string.Equals(f.FullPath, t.FullPath, StringComparison.OrdinalIgnoreCase)))
                        .Select(t => t.FileName).ToList();
        if (dirty.Count > 0)
        {
            StatusMessage = $"⚠ Save first — unsaved changes in {string.Join(", ", dirty)}";
            System.Windows.MessageBox.Show(
                $"These open files have unsaved changes and are part of the rename:\n\n{string.Join("\n", dirty)}\n\n"
                + "Save them (or close them) first so the rename doesn't discard your edits.",
                "Rename blocked", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"Rename '{plan.OldName}' → '{plan.NewName}'\n\n"
            + $"{plan.TotalEdits} occurrence(s) across {plan.Files.Count} file(s) will be rewritten on disk.\n\n"
            + (_intel.IsLspConnected
                ? "A language server is connected, but this rename is whole-word textual.\n\n"
                : "This is a whole-word, case-sensitive TEXT rename — it does not understand scope, so "
                  + "unrelated members with the same name are also renamed. Review the list first.\n\n")
            + "This cannot be undone from inside CluadeX (use git to revert).\n\nContinue?",
            "Confirm rename", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsSearching = true;
        try
        {
            var result = await _intel.ApplyRenameAsync(plan);
            if (!string.IsNullOrEmpty(result.Error))
            {
                StatusMessage = $"✗ Rename failed: {result.Error}";
                return;
            }

            // Reload any open tab we just rewrote so the editor isn't showing stale text.
            foreach (var path in result.ChangedPaths)
            {
                var tab = Tabs.FirstOrDefault(t => string.Equals(t.FullPath, path, StringComparison.OrdinalIgnoreCase));
                if (tab == null) continue;
                var reloaded = await _workspace.OpenFileAsync(path);
                if (reloaded != null) tab.MarkOpened(reloaded.Content);
            }

            string skipped = result.Skipped.Count > 0 ? $" · {result.Skipped.Count} skipped" : "";
            StatusMessage = $"✓ Renamed {result.EditsApplied} occurrence(s) in {result.FilesChanged} file(s){skipped}";

            _renamePlan = null;
            CanApplyRename = false;
            RenameApplyLabel = "";
            // Both caches now advertise the OLD name — the agent's repo map and the completion index.
            _repoMapInvalidate?.Invoke();
            _intel.InvalidateSymbolIndex();

            await RefreshGitAsync();
            _ = _workspace.EnrichGitStatusAsync(Tree);
        }
        finally { IsSearching = false; }
    }

    /// <summary>Set by the constructor — drops the cached repo map after a rename so the agent's
    /// symbol outline doesn't keep advertising the old name.</summary>
    private Action? _repoMapInvalidate;

    // ── Autocomplete ──

    /// <summary>Completion candidates for the caret. The View owns the popup; the VM owns the source
    /// so the editor never talks to a service directly.</summary>
    public Task<List<CompletionItem>> GetCompletionsAsync(string? filePath, string documentText, int caretOffset)
        => _intel.GetCompletionsAsync(filePath, documentText, caretOffset);

    // ═══════════════ Debugger ═══════════════

    public ObservableCollection<DebugAdapterInfo> DebugAdapters { get; } = new();

    private string _debugStatus = "";
    public string DebugStatus { get => _debugStatus; set => SetProperty(ref _debugStatus, value); }

    private bool _isDebugging;
    public bool IsDebugging
    {
        get => _isDebugging;
        set { if (SetProperty(ref _isDebugging, value)) OnPropertyChanged(nameof(IsNotDebugging)); }
    }
    public bool IsNotDebugging => !_isDebugging;

    private bool _isPausedAtBreakpoint;
    public bool IsPausedAtBreakpoint { get => _isPausedAtBreakpoint; set => SetProperty(ref _isPausedAtBreakpoint, value); }

    /// <summary>Whether the ACTIVE file has an installed adapter — drives whether Start is offered
    /// at all, instead of offering it and failing.</summary>
    private bool _canDebugActiveFile;
    public bool CanDebugActiveFile { get => _canDebugActiveFile; set => SetProperty(ref _canDebugActiveFile, value); }

    private string _debugTargetLabel = "";
    public string DebugTargetLabel { get => _debugTargetLabel; set => SetProperty(ref _debugTargetLabel, value); }

    public ObservableCollection<DapStackFrame> CallStack { get; } = new();
    public ObservableCollection<DapVariable> DebugVariables { get; } = new();
    public ObservableCollection<BreakpointItem> Breakpoints { get; } = new();

    private DapStackFrame? _selectedFrame;
    public DapStackFrame? SelectedFrame
    {
        get => _selectedFrame;
        set
        {
            if (!SetProperty(ref _selectedFrame, value) || value == null) return;
            _ = LoadFrameAsync(value);
        }
    }

    private string _debugConsole = "";
    public string DebugConsole { get => _debugConsole; private set => SetProperty(ref _debugConsole, value); }

    private string _debugEvalInput = "";
    public string DebugEvalInput { get => _debugEvalInput; set => SetProperty(ref _debugEvalInput, value); }

    /// <summary>1-based line the debugger is stopped on in the active file (0 = not stopped here) —
    /// the editor paints its execution-pointer highlight from this.</summary>
    private int _executionLine;
    public int ExecutionLine { get => _executionLine; set => SetProperty(ref _executionLine, value); }

    /// <summary>Raised when the execution pointer moves, so the view repaints and scrolls to it.</summary>
    public event Action<string, int>? ExecutionPointerMoved;   // (file, 1-based line)

    // Breakpoints are keyed by absolute path; a set per file so toggling is O(1) and duplicates
    // are impossible.
    private readonly Dictionary<string, HashSet<int>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Breakpoint lines for a file — the editor margin renders from this.</summary>
    public IReadOnlyCollection<int> BreakpointsFor(string filePath)
        => _breakpoints.TryGetValue(filePath, out var set) ? set : Array.Empty<int>();

    /// <summary>Raised when a file's breakpoints change so the margin repaints.</summary>
    public event Action<string>? BreakpointsChanged;

    public void ToggleBreakpoint(string filePath, int line)
    {
        if (string.IsNullOrEmpty(filePath) || line < 1) return;

        if (!_breakpoints.TryGetValue(filePath, out var set))
            _breakpoints[filePath] = set = new HashSet<int>();

        if (!set.Add(line)) set.Remove(line);
        if (set.Count == 0) _breakpoints.Remove(filePath);

        RebuildBreakpointList();
        BreakpointsChanged?.Invoke(filePath);

        // A live session accepts breakpoint changes without restarting.
        if (_debug.IsRunning)
            _ = _debug.SetBreakpointsAsync(filePath, set.OrderBy(l => l).ToList());
    }

    public ICommand RemoveBreakpointCommand => _removeBreakpointCommand ??=
        new RelayCommand<BreakpointItem>(b => { if (b != null) ToggleBreakpoint(b.FilePath, b.Line); });
    private ICommand? _removeBreakpointCommand;

    public ICommand GoToBreakpointCommand => _goToBreakpointCommand ??=
        new AsyncRelayCommand<BreakpointItem>(async b =>
        {
            if (b == null) return;
            await OpenPathAsync(b.FilePath);
            SelectRangeRequested?.Invoke(b.Line, 0, 0);
        });
    private ICommand? _goToBreakpointCommand;

    public ICommand ClearBreakpointsCommand => _clearBreakpointsCommand ??= new RelayCommand(() =>
    {
        var files = _breakpoints.Keys.ToList();
        _breakpoints.Clear();
        RebuildBreakpointList();
        foreach (var f in files)
        {
            BreakpointsChanged?.Invoke(f);
            if (_debug.IsRunning) _ = _debug.SetBreakpointsAsync(f, new List<int>());
        }
    });
    private ICommand? _clearBreakpointsCommand;

    private void RebuildBreakpointList()
    {
        Breakpoints.Clear();
        foreach (var (file, lines) in _breakpoints.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            foreach (var line in lines.OrderBy(l => l))
                Breakpoints.Add(new BreakpointItem { FilePath = file, Line = line });
    }

    /// <summary>
    /// Re-probe what can debug on this machine and whether it covers the open file. Runs off the UI
    /// thread: detection SPAWNS PROCESSES (`python -c "import debugpy"`), which is ~a second on a
    /// cold start — long enough to visibly freeze the window if done inline.
    /// </summary>
    public async Task RefreshDebugAdaptersAsync()
    {
        DebugStatus = string.IsNullOrEmpty(DebugStatus) ? "Checking for debug adapters…" : DebugStatus;
        var adapters = await Task.Run(DebugAdapterService.DetectAdapters);

        DebugAdapters.Clear();
        foreach (var a in adapters) DebugAdapters.Add(a);
        UpdateDebugTarget(adapters);

        if (DebugStatus == "Checking for debug adapters…")
            DebugStatus = adapters.Any(a => a.IsAvailable) ? "" : "No debug adapter installed yet.";
    }

    private void UpdateDebugTarget(List<DebugAdapterInfo>? adapters = null)
    {
        adapters ??= DebugAdapters.ToList();
        var tab = _activeTab;
        if (tab == null)
        {
            CanDebugActiveFile = false;
            DebugTargetLabel = "Open a file to debug it.";
            return;
        }

        var adapter = DebugAdapterService.AdapterForFile(tab.FullPath, adapters);
        CanDebugActiveFile = adapter != null;
        DebugTargetLabel = adapter != null
            ? $"{tab.FileName} · {adapter.Language}"
            // Say WHY it can't run, not just that the button is greyed out.
            : $"{tab.FileName} — no installed debug adapter for this file type.";
    }

    public ICommand StartDebugCommand => _startDebugCommand ??= new AsyncRelayCommand(StartDebugAsync);
    private ICommand? _startDebugCommand;

    private async Task StartDebugAsync()
    {
        if (_debug.IsRunning) { DebugStatus = "Already running"; return; }
        var tab = _activeTab;
        if (tab == null) { DebugStatus = "Open the file you want to debug first"; return; }

        var adapters = await Task.Run(DebugAdapterService.DetectAdapters);
        var adapter = DebugAdapterService.AdapterForFile(tab.FullPath, adapters);
        if (adapter == null)
        {
            var known = adapters.FirstOrDefault(a => !a.IsAvailable);
            DebugStatus = known != null
                ? $"No adapter for {tab.Extension}. {known.InstallHint}"
                : $"No debug adapter available for {tab.Extension}.";
            return;
        }

        if (tab.IsDirty)
        {
            // Debugging a stale file on disk while the editor shows something else is the single
            // most confusing thing a debugger can do — breakpoints land on the wrong lines.
            var save = System.Windows.MessageBox.Show(
                $"{tab.FileName} has unsaved changes.\n\nThe debugger runs the file ON DISK, so your "
                + "edits would not be included and breakpoints could land on the wrong lines.\n\nSave first?",
                "Unsaved changes", System.Windows.MessageBoxButton.YesNoCancel,
                System.Windows.MessageBoxImage.Warning);
            if (save == System.Windows.MessageBoxResult.Cancel) return;
            if (save == System.Windows.MessageBoxResult.Yes) await _workspace.SaveTabAsync(tab);
        }

        AppendDebugConsole($"— starting {adapter.Language} debugger ({adapter.Id}) —");
        DebugStatus = "Starting…";
        IsDebugging = true;

        var byFile = _breakpoints.ToDictionary(k => k.Key, v => v.Value.OrderBy(l => l).ToList());
        if (!byFile.ContainsKey(tab.FullPath)) byFile[tab.FullPath] = new List<int>();

        var result = await _debug.StartAsync(adapter, tab.FullPath, byFile);
        if (!result.Started)
        {
            IsDebugging = false;
            DebugStatus = $"✗ {result.Error}";
            AppendDebugConsole($"[failed] {result.Error}");
            return;
        }

        foreach (var (file, bps) in result.Breakpoints)
        {
            foreach (var bp in bps.Where(b => !b.Verified))
                AppendDebugConsole($"[breakpoint not bound] {Path.GetFileName(file)}:{bp.RequestedLine}"
                                   + (string.IsNullOrEmpty(bp.Message) ? "" : $" — {bp.Message}"));
            foreach (var bp in bps.Where(b => b.Moved))
                AppendDebugConsole($"[breakpoint moved] {Path.GetFileName(file)}:{bp.RequestedLine} → line {bp.Line}");
        }

        DebugStatus = "Running";
    }

    public ICommand StopDebugCommand => _stopDebugCommand ??= new AsyncRelayCommand(async () =>
    {
        await _debug.StopAsync();
        OnDebugSessionEnded("stopped by user");
    });
    private ICommand? _stopDebugCommand;

    public ICommand ContinueDebugCommand => _continueCommand ??= new AsyncRelayCommand(() => _debug.ContinueAsync());
    private ICommand? _continueCommand;

    public ICommand StepOverCommand => _stepOverCommand ??= new AsyncRelayCommand(() => _debug.StepOverAsync());
    private ICommand? _stepOverCommand;

    public ICommand StepIntoCommand => _stepIntoCommand ??= new AsyncRelayCommand(() => _debug.StepIntoAsync());
    private ICommand? _stepIntoCommand;

    public ICommand StepOutCommand => _stepOutCommand ??= new AsyncRelayCommand(() => _debug.StepOutAsync());
    private ICommand? _stepOutCommand;

    public ICommand PauseDebugCommand => _pauseCommand ??= new AsyncRelayCommand(() => _debug.PauseAsync());
    private ICommand? _pauseCommand;

    public ICommand EvaluateDebugCommand => _evaluateCommand ??= new AsyncRelayCommand(async () =>
    {
        string expr = DebugEvalInput?.Trim() ?? "";
        if (expr.Length == 0) return;
        if (!_debug.IsPaused) { AppendDebugConsole("> (pause at a breakpoint first)"); return; }

        AppendDebugConsole($"> {expr}");
        DebugEvalInput = "";
        string value = await _debug.EvaluateAsync(expr, _selectedFrame?.Id ?? 0);
        AppendDebugConsole(value);
    });
    private ICommand? _evaluateCommand;

    private const int DebugConsoleMaxChars = 120_000;

    private void AppendDebugConsole(string text)
    {
        void Apply()
        {
            string next = _debugConsole.Length == 0 ? text : _debugConsole + "\n" + text;
            if (next.Length > DebugConsoleMaxChars) next = next[^DebugConsoleMaxChars..];
            DebugConsole = next;
        }
        if (App.Current?.Dispatcher.CheckAccess() == true) Apply();
        else App.Current?.Dispatcher.BeginInvoke(Apply);
    }

    /// <summary>Wire the adapter's events onto the UI thread. Called once from the constructor.</summary>
    private void HookDebugEvents()
    {
        _debug.Output += line => AppendDebugConsole(line);

        _debug.Stopped += (reason, _) => OnUi(async () =>
        {
            IsPausedAtBreakpoint = true;
            DebugStatus = $"Paused · {reason}";

            CallStack.Clear();
            DebugVariables.Clear();
            var frames = await _debug.GetStackTraceAsync();
            foreach (var f in frames) CallStack.Add(f);

            // Selecting the top frame loads its variables and moves the execution pointer.
            if (frames.Count > 0) SelectedFrame = frames[0];
        });

        _debug.Continued += () => OnUi(() =>
        {
            IsPausedAtBreakpoint = false;
            DebugStatus = "Running";
            ExecutionLine = 0;
            CallStack.Clear();
            DebugVariables.Clear();
            ExecutionPointerMoved?.Invoke("", 0);
        });

        _debug.Terminated += why => OnUi(() => OnDebugSessionEnded(why));
    }

    private void OnDebugSessionEnded(string why)
    {
        IsDebugging = false;
        IsPausedAtBreakpoint = false;
        DebugStatus = $"Session ended — {why}";
        ExecutionLine = 0;
        CallStack.Clear();
        DebugVariables.Clear();
        SelectedFrame = null;
        ExecutionPointerMoved?.Invoke("", 0);
        AppendDebugConsole($"— {why} —");
    }

    private async Task LoadFrameAsync(DapStackFrame frame)
    {
        try
        {
            DebugVariables.Clear();
            foreach (var v in await _debug.GetVariablesAsync(frame.Id)) DebugVariables.Add(v);

            if (!string.IsNullOrEmpty(frame.FilePath) && File.Exists(frame.FilePath))
            {
                await OpenPathAsync(frame.FilePath);
                ExecutionLine = frame.Line;
                ExecutionPointerMoved?.Invoke(frame.FilePath, frame.Line);
                ScrollToLineRequested?.Invoke(frame.Line);
            }
        }
        catch (Exception ex) { AppendDebugConsole($"[frame load failed] {ex.Message}"); }
    }

    private static void OnUi(Action action)
    {
        if (App.Current?.Dispatcher.CheckAccess() == true) action();
        else App.Current?.Dispatcher.BeginInvoke(action);
    }

    private static void OnUi(Func<Task> action)
    {
        if (App.Current?.Dispatcher.CheckAccess() == true) _ = action();
        else App.Current?.Dispatcher.BeginInvoke(new Action(() => _ = action()));
    }
}

/// <summary>The three left-panel modes (VS Code's activity bar).</summary>
public enum WorkbenchPanel { Explorer, Search, Debug }

/// <summary>One breakpoint in the flat list the debug panel shows.</summary>
public sealed class BreakpointItem
{
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public string Label => $"{FileName}:{Line}";
}

/// <summary>Which question the current result list is answering.</summary>
public enum WorkbenchSearchMode
{
    Text,
    Definitions,
    References,
    Rename,
}

/// <summary>
/// One file's worth of results in the search panel. Wraps the service's pure
/// <see cref="SearchFileGroup"/> and adds the collapse state the list binds to.
/// </summary>
public sealed class SearchResultGroup : ViewModelBase
{
    public SearchResultGroup(SearchFileGroup source)
    {
        Source = source;
        Matches = new ObservableCollection<SearchMatch>(source.Matches);
    }

    public SearchFileGroup Source { get; }
    public ObservableCollection<SearchMatch> Matches { get; }

    public string FileName => Source.FileName;
    public string Directory => Source.Directory;
    public string RelPath => Source.RelPath;
    public string FullPath => Source.FullPath;
    public int MatchCount => Source.Matches.Count;

    private bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
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
