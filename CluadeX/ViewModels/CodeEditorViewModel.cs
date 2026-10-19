using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Views.Editor;

namespace CluadeX.ViewModels;

/// <summary>
/// View model for the Code Editor page — the central "workbench" view
/// (file tree on the left, tabbed editor in the middle, AI chat panel
/// docked on the right). Reuses the singleton ChatViewModel for the
/// embedded chat surface so the user sees the same conversation history
/// whether they're on the Chat page or the Code page.
///
/// Disposable: the DI container disposes it on exit, which kills the embedded terminal's process
/// tree (a dev server started there used to outlive the app and keep its port).
/// </summary>
public partial class CodeEditorViewModel : ViewModelBase, IDisposable
{
    private readonly CodeWorkspaceService _workspace;
    private readonly FileSystemService _fs;
    private readonly SettingsService _settings;
    private readonly GitService _git;
    private readonly CodeIntelligenceService _intel;
    private readonly DebugAdapterService _debug;
    private readonly LocalizationService _loc;
    private readonly DebugLogService? _log;

    private string T(string key) => _loc.T(key);
    private string T(string key, params object[] args) => string.Format(_loc.T(key), args);

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
    public string ActiveFileName => _activeTab?.FileName ?? T("ide.noFileOpen");
    public string ActiveLanguageLabel => _activeTab == null ? "" : _activeTab.Extension.ToUpperInvariant();

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public string ProjectName
    {
        get
        {
            if (string.IsNullOrEmpty(_workspace.WorkingDirectory)) return T("ide.noProjectName");
            return Path.GetFileName(_workspace.WorkingDirectory.TrimEnd('\\', '/')) ?? _workspace.WorkingDirectory;
        }
    }

    public string ProjectFileCount
    {
        get
        {
            if (Tree.Count == 0) return "";
            int n = CountFiles(Tree[0]);
            return T("ide.fileCount", n.ToString("N0"));
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

    public string GitChangeCount => GitChanges.Count == 0 ? T("ide.gitClean") : T("ide.gitChanged", GitChanges.Count);

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

    /// <summary>Stop whatever the terminal is running (Ctrl+C in the input line, or the stop button):
    /// kills the shell's process tree — dev servers and watchers included — and opens a fresh prompt.</summary>
    public ICommand InterruptTerminalCommand => _interruptTerminalCommand ??= new RelayCommand(() =>
    {
        if (!_terminal.IsRunning) return;
        _terminal.Interrupt();
        AppendTerminal(T("ide.termStopped"));
    });
    private ICommand? _interruptTerminalCommand;

    private void RunTerminalInput()
    {
        string cmd = TerminalInput?.Trim() ?? "";
        if (cmd.Length == 0) return;
        AppendTerminal($"> {cmd}");
        _terminal.Send(cmd);
        TerminalInput = "";
    }

    private const int TerminalMaxChars = 200_000;

    // Output arrives a line at a time on a worker thread — a build or `npm install` produces
    // thousands per second. Each line used to rebuild the whole 200 KB transcript string and push
    // it through the binding on the UI thread, which froze the window under a chatty command.
    // Lines now queue up and are applied in one batch at most every 50 ms.
    private readonly ConcurrentQueue<string> _terminalQueue = new();
    private readonly StringBuilder _terminalText = new();
    private System.Windows.Threading.DispatcherTimer? _terminalFlush;
    private int _terminalFlushScheduled;

    private void AppendTerminal(string line)
    {
        _terminalQueue.Enqueue(line);
        if (Interlocked.Exchange(ref _terminalFlushScheduled, 1) == 1) return;   // a flush is already due
        OnUi(() =>
        {
            _terminalFlush ??= CreateTerminalFlushTimer();
            _terminalFlush.Start();
        });
    }

    private System.Windows.Threading.DispatcherTimer CreateTerminalFlushTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Volatile.Write(ref _terminalFlushScheduled, 0);
            bool any = false;
            while (_terminalQueue.TryDequeue(out var l))
            {
                if (_terminalText.Length > 0) _terminalText.Append('\n');
                _terminalText.Append(l);
                any = true;
            }
            if (!any) return;
            if (_terminalText.Length > TerminalMaxChars)
            {
                // Drop the oldest text on a line boundary so the top line isn't cut mid-word.
                int cut = _terminalText.Length - TerminalMaxChars;
                while (cut < _terminalText.Length && _terminalText[cut] != '\n') cut++;
                _terminalText.Remove(0, Math.Min(cut + 1, _terminalText.Length));
            }
            TerminalOutput = _terminalText.ToString();
            // Anything enqueued while we drained (before the flag reset) gets its own flush.
            if (!_terminalQueue.IsEmpty && Interlocked.Exchange(ref _terminalFlushScheduled, 1) == 0)
                timer.Start();
        };
        return timer;
    }

    public CodeEditorViewModel(CodeWorkspaceService workspace, FileSystemService fs, ChatViewModel chatVm,
        SettingsService settings, GitService git, CodeIntelligenceService intel, RepoMapService repoMap,
        DebugAdapterService debug, LocalizationService loc, DebugLogService? log = null)
    {
        _workspace = workspace;
        _fs = fs;
        _settings = settings;
        _git = git;
        _intel = intel;
        _debug = debug;
        _loc = loc;
        _log = log;
        _repoMapInvalidate = repoMap.Invalidate;
        ChatVM = chatVm;
        HookDebugEvents();

        // Computed labels re-read the language on the next notification.
        _loc.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(ActiveFileName));
            OnPropertyChanged(nameof(ProjectName));
            OnPropertyChanged(nameof(ProjectFileCount));
            OnPropertyChanged(nameof(GitChangeCount));
            RaiseAiComment();
        };

        RefreshTreeCommand = new AsyncRelayCommand(RefreshTreeAsync);
        OpenNodeCommand = new AsyncRelayCommand<FileTreeNode>(OpenNodeAsync);
        CloseTabCommand = new AsyncRelayCommand<OpenFileTab>(CloseTabAsync);
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
                RaiseAiComment();                 // the new project may keep its own AI-comment choice
            }
        };

        // Live-follow: when the agent edits a file, open/refresh it in the editor and scroll to the change.
        ChatVM.FileMutatedByAgent += OnAgentFileMutated;

        _terminal.OutputReceived += AppendTerminal;
        _terminal.Exited += () => AppendTerminal(T("ide.termExited"));
    }

    // ═══════════════ Lifetime ═══════════════

    private bool _disposed;

    /// <summary>Kill the terminal's process tree and stop the timers. Called by the DI container on
    /// app exit (the VM is a singleton IDisposable).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _terminal.Dispose(); } catch { /* shutting down */ }
        try { _terminalFlush?.Stop(); } catch { }
        try { _typeTimer?.Stop(); } catch { }
        try { _conflictDebounce?.Stop(); } catch { }
        try { _searchDebounce?.Stop(); } catch { }
        try { _searchCts?.Cancel(); } catch { }
    }

    // ═══════════════ Source control ═══════════════

    /// <summary>Reload branch + changed-file list (porcelain -z status) + local branches.</summary>
    public async Task RefreshGitAsync()
    {
        if (!_workspace.HasWorkingDirectory)
        {
            OnUi(() => { GitBranch = ""; GitChanges.Clear(); OnPropertyChanged(nameof(GitChangeCount)); });
            return;
        }
        try
        {
            var r = await _git.StatusEntriesAsync();
            var branches = await _git.ListBranchesAsync(includeRemote: false);
            App.Current?.Dispatcher.Invoke(() =>
            {
                GitChanges.Clear();
                if (!r.Success) { GitBranch = ""; OnPropertyChanged(nameof(GitChangeCount)); return; }

                GitBranch = r.IsDetached ? T("ide.detachedHead") : r.Branch;
                foreach (var e in r.Entries)
                {
                    GitChanges.Add(new GitChangeItem
                    {
                        Status = e.Code,
                        RepoPath = e.RepoPath,
                        FullPath = e.FullPath,
                        // Shown relative to the opened folder (which may be a repo subfolder).
                        Path = DisplayPath(e.FullPath, e.RepoPath),
                    });
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
        catch (Exception ex) { _log?.Debug("Git", $"git panel refresh failed: {ex.GetType().Name}"); /* best-effort */ }
    }

    /// <summary>Path for display: relative to the opened folder when the file is inside it.</summary>
    private string DisplayPath(string fullPath, string repoPath)
    {
        try
        {
            if (!string.IsNullOrEmpty(fullPath) && _workspace.HasWorkingDirectory)
            {
                string rel = Path.GetRelativePath(_workspace.WorkingDirectory, fullPath);
                if (!rel.StartsWith("..", StringComparison.Ordinal)) return rel.Replace('\\', '/');
            }
        }
        catch { }
        return repoPath;
    }

    /// <summary>Names that are usually secrets. Committing one is the classic irreversible mistake —
    /// once pushed, the key is public even after a revert.</summary>
    private static bool LooksSecret(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        if (name is ".env.example" or ".env.sample" or ".env.template") return false;
        return name == ".env" || name.StartsWith(".env.", StringComparison.Ordinal)
            || name.EndsWith(".pem") || name.EndsWith(".key") || name.EndsWith(".pfx") || name.EndsWith(".p12")
            || name is "id_rsa" or "id_ed25519" or "id_ecdsa" or "credentials.json" or "service-account.json"
            || name.Contains("secret");
    }

    /// <summary>
    /// Stage everything and commit with the panel's message — the IDE-style commit. It runs
    /// `git add .`, so the exact list of what will be staged is shown and confirmed first; it used
    /// to commit silently, sweeping in whatever untracked file happened to be there (a fresh .env
    /// included). Secret-looking files get a separate, explicit warning.
    /// </summary>
    private async Task CommitAsync()
    {
        string msg = CommitMessage?.Trim() ?? "";
        if (string.IsNullOrEmpty(msg)) { StatusMessage = T("ide.commitNeedsMessage"); return; }
        if (IsCommitting) return;

        IsCommitting = true;
        try
        {
            var status = await _git.StatusEntriesAsync();
            if (!status.Success) { StatusMessage = T("ide.commitStatusFailed"); return; }
            if (status.Entries.Count == 0) { StatusMessage = T("ide.commitNothing"); return; }

            // Unsaved editor tabs are not on disk — they would not be in the commit.
            var dirty = Tabs.Where(t => t.IsDirty).Select(t => t.FileName).ToList();

            const int maxListed = 25;
            var lines = status.Entries.Take(maxListed)
                .Select(e => $"  {e.Code,-2}  {DisplayPath(e.FullPath, e.RepoPath)}").ToList();
            if (status.Entries.Count > maxListed) lines.Add("  " + T("ide.commitMore", status.Entries.Count - maxListed));
            var secrets = status.Entries.Where(e => !e.IsDeleted && LooksSecret(e.RepoPath))
                .Select(e => DisplayPath(e.FullPath, e.RepoPath)).ToList();

            var body = new StringBuilder();
            body.AppendLine(T("ide.commitConfirmHeader", status.Entries.Count, GitBranch));
            body.AppendLine();
            foreach (var l in lines) body.AppendLine(l);
            if (secrets.Count > 0)
            {
                body.AppendLine();
                body.AppendLine(T("ide.commitSecretsWarning", string.Join(", ", secrets)));
            }
            if (dirty.Count > 0)
            {
                body.AppendLine();
                body.AppendLine(T("ide.commitUnsavedWarning", string.Join(", ", dirty)));
            }

            int choice = ChoiceDialog.Show(T("ide.commitConfirmTitle"), body.ToString().TrimEnd(),
                new[] { T("ide.commitConfirmButton", status.Entries.Count), T("ide.cancel") },
                defaultIndex: secrets.Count > 0 ? 1 : 0);   // a secret in the list: Enter must not commit it
            if (choice != 0) { StatusMessage = T("ide.commitCancelled"); return; }

            var add = await _git.AddAsync(".");
            if (!add.Success) { StatusMessage = T("ide.stageFailed", FirstLine(add.Error)); return; }
            var commit = await _git.CommitAsync(msg);
            if (commit.Success)
            {
                CommitMessage = "";
                // "[dev 537804b] update readme" → surface the first line
                string first = FirstLine(commit.Output);
                StatusMessage = $"✓ {(first.Length > 0 ? first : T("ide.committed"))}";
            }
            else
            {
                StatusMessage = T("ide.commitFailed", FirstLine(string.IsNullOrWhiteSpace(commit.Error) ? commit.Output : commit.Error));
            }
        }
        catch (Exception ex)
        {
            _log?.Warn("Git", "Commit failed", ex);
            StatusMessage = T("ide.commitFailed", ex.GetType().Name);
        }
        finally
        {
            IsCommitting = false;
            await RefreshGitAsync();
            _ = _workspace.EnrichGitStatusAsync(Tree);
        }
    }

    private static string FirstLine(string? text)
        => (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    /// <summary>Open any file by absolute path in an editor tab (used by the command palette).</summary>
    public async Task OpenPathAsync(string fullPath) => await OpenPathCoreAsync(fullPath);

    /// <summary>Open <paramref name="fullPath"/> in an editor tab and put the caret on
    /// <paramref name="line"/> (1-based). False when the editor can't show the file (binary, over
    /// 2 MB, gone) — the caller decides what to do instead. Used by Security Shield findings.</summary>
    public async Task<bool> OpenAndRevealAsync(string fullPath, int line)
    {
        bool ok = await OpenPathCoreAsync(fullPath);
        if (!ok) return false;
        OnUi(() =>
        {
            // Coming from another page, the editor view may not exist yet (it is built when the
            // Code page is shown) — then nobody is listening, so park the line for it to pick up.
            var handler = SelectRangeRequested;
            if (handler != null) handler(Math.Max(1, line), 0, 0);
            else _pendingReveal = (fullPath, Math.Max(1, line));
        });
        return true;
    }

    private (string Path, int Line)? _pendingReveal;

    /// <summary>For the editor view when it attaches: the line a caller asked to reveal in
    /// <paramref name="path"/> before the view existed (0 = none). One-shot.</summary>
    public int ConsumePendingReveal(string? path)
    {
        if (_pendingReveal is { } p && string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            _pendingReveal = null;
            return p.Line;
        }
        return 0;
    }

    private async Task<bool> OpenPathCoreAsync(string fullPath)
    {
        try
        {
            var existing = Tabs.FirstOrDefault(t => string.Equals(t.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { OnUi(() => ActiveTab = existing); return true; }
            var loaded = await _workspace.OpenFileAsync(fullPath);
            if (loaded == null)
            {
                StatusMessage = T("ide.cantOpen", Path.GetFileName(fullPath));
                return false;
            }
            // After an await — and this is reached from search results, go-to-definition and the
            // debugger's frame selection, so it must not assume it is still on the dispatcher.
            OnUi(() =>
            {
                Tabs.Add(loaded);
                ActiveTab = loaded;
            });
            return true;
        }
        catch (Exception ex)
        {
            _log?.Warn("Editor", $"Open failed: {fullPath}", ex);
            StatusMessage = T("ide.openFailed", Path.GetFileName(fullPath));
            return false;
        }
    }

    /// <summary>Open a changed file from the source-control list in an editor tab.</summary>
    private async Task OpenChangeAsync(GitChangeItem? item)
    {
        if (item == null || !_workspace.HasWorkingDirectory) return;
        string full = !string.IsNullOrEmpty(item.FullPath)
            ? item.FullPath
            : Path.GetFullPath(Path.Combine(_workspace.WorkingDirectory, item.Path));
        if (!File.Exists(full))
        {
            // A deleted file has nothing to open — its diff is still there to review.
            StatusMessage = T("ide.changeDeleted", item.FileName);
            return;
        }
        await OpenPathCoreAsync(full);
    }

    /// <summary>
    /// Open a changed file in the side-by-side diff editor: HEAD on the left, working tree on the
    /// right, aligned row by row. Replaces the old approach of writing a unified-diff text file to
    /// temp and opening it as a tab — that was a patch you had to read, not a diff you could review.
    /// Both sides obey the editor's limits (2 MB, text only): a lockfile or an image used to be read
    /// whole and fed to the diff.
    /// </summary>
    private async Task ShowDiffAsync(GitChangeItem? item)
    {
        if (item == null || !_workspace.HasWorkingDirectory) return;
        try
        {
            string full = !string.IsNullOrEmpty(item.FullPath)
                ? item.FullPath
                : Path.GetFullPath(Path.Combine(_workspace.WorkingDirectory, item.Path));

            string working = "";
            if (File.Exists(full))
            {
                var fi = new FileInfo(full);
                if (fi.Length > CodeWorkspaceService.MaxEditableBytes) { StatusMessage = T("ide.diffTooLarge", item.FileName); return; }
                byte[] bytes = await File.ReadAllBytesAsync(full);
                for (int i = 0; i < Math.Min(bytes.Length, 8192); i++)
                    if (bytes[i] == 0) { StatusMessage = T("ide.diffBinary", item.FileName); return; }
                working = CluadeX.Helpers.TextFileCodec.Decode(bytes).Text;
            }

            // Untracked files have no HEAD version — everything is an addition.
            string baseline = "";
            if (!item.Status.StartsWith("?") && !string.IsNullOrEmpty(item.RepoPath))
            {
                var head = await _git.ReadBlobAsync("HEAD", item.RepoPath, CodeWorkspaceService.MaxEditableBytes);
                if (head.TooLarge) { StatusMessage = T("ide.diffTooLarge", item.FileName); return; }
                if (head.Binary) { StatusMessage = T("ide.diffBinary", item.FileName); return; }
                // Not in HEAD is normal for a newly added file; treat it as "no baseline".
                baseline = head.Exists ? head.Text : "";
            }

            var diff = await Task.Run(() => DiffService.BuildSideBySide(baseline, working));

            OnUi(() =>
            {
                DiffRows = diff.Rows;
                DiffTitle = item.Path;
                DiffSummary = diff.HasChanges
                    ? $"+{diff.Added:N0} −{diff.Removed:N0}"
                      + (item.Status.StartsWith("?") ? T("ide.diffUntrackedSuffix") : T("ide.diffVsHeadSuffix"))
                      + (diff.Truncated ? $" · ⚠ {diff.Note}" : "")
                    : diff.Note ?? T("ide.diffNoChanges");
                IsDiffViewActive = true;
            });
        }
        catch (Exception ex)
        {
            _log?.Warn("Git", $"Diff failed: {item.Path}", ex);
            StatusMessage = T("ide.diffFailed", item.FileName);
        }
    }

    /// <summary>Show the disk version of a tab's file against what the editor holds (the "Compare"
    /// answer when a save finds the file changed underneath it). Nothing is written.</summary>
    private async Task ShowDiskVsEditorAsync(OpenFileTab tab)
    {
        string disk = await _workspace.ReadDiskTextAsync(tab.FullPath) ?? "";
        string mine = tab.Content;
        var diff = await Task.Run(() => DiffService.BuildSideBySide(disk, mine));
        OnUi(() =>
        {
            DiffRows = diff.Rows;
            DiffTitle = tab.FileName;
            DiffSummary = T("ide.diffDiskVsEditor", diff.Added.ToString("N0"), diff.Removed.ToString("N0"));
            IsDiffViewActive = true;
        });
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
            ? T("ide.conflictMalformed")
            : scan.Conflicts.Count == 0
                ? ""
                : T("ide.conflictSummary", scan.Conflicts.Count, tab.FileName);
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
            StatusMessage = T("ide.conflictFixMarkerFirst");
            return;
        }

        try
        {
            // Resolve by INDEX against a fresh scan of the current buffer — the block we were handed
            // may have been captured before another conflict was resolved or the file hand-edited.
            string updated = DiffService.ResolveConflict(tab.Content, block.Index, choice);
            if (string.Equals(updated, tab.Content, StringComparison.Ordinal))
            {
                StatusMessage = T("ide.conflictAlreadyResolved");
                ScanConflictsNow();
                return;
            }

            tab.Content = updated;   // marks the tab dirty; the editor picks it up incrementally
            ScanConflictsNow();
            StatusMessage = Conflicts.Count == 0
                ? T("ide.conflictAllResolved", tab.FileName)
                : T("ide.conflictResolvedLeft", Conflicts.Count);
        }
        catch (Exception ex)
        {
            _log?.Warn("Editor", "Resolve conflict failed", ex);
            StatusMessage = T("ide.conflictResolveFailed");
        }
    }

    private void ResolveAllConflicts(ConflictChoice choice)
    {
        var tab = _activeTab;
        if (tab == null || Conflicts.Count == 0) return;
        if (ConflictsMalformed)
        {
            StatusMessage = T("ide.conflictFixMarkerFirst");
            return;
        }

        int count = Conflicts.Count;
        string side = choice == ConflictChoice.Ours ? Conflicts[0].OursLabel : Conflicts[0].TheirsLabel;
        int answer = ChoiceDialog.Show(T("ide.resolveAllTitle"),
            T("ide.resolveAllConfirm", count, tab.FileName, side),
            new[] { T("ide.resolveAllButton", side), T("ide.cancel") });
        if (answer != 0) return;

        try
        {
            tab.Content = DiffService.ResolveAll(tab.Content, choice);
            ScanConflictsNow();
            StatusMessage = T("ide.resolveAllDone", count, side);
        }
        catch (Exception ex)
        {
            _log?.Warn("Editor", "Resolve all conflicts failed", ex);
            StatusMessage = T("ide.conflictResolveFailed");
        }
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
            // Deferred: this runs inside the ComboBox's binding update, and the switch opens a modal
            // confirmation — not something to do in the middle of a selection change.
            string target = value;
            if (App.Current?.Dispatcher is { } d) d.BeginInvoke(new Action(() => _ = SwitchBranchAsync(target)));
            else _ = SwitchBranchAsync(target);
        }
    }

    /// <summary>Put the dropdown back on the real branch without triggering another checkout.</summary>
    private void RevertBranchSelection()
    {
        OnUi(() =>
        {
            _suppressBranchSwitch = true;
            try { SelectedBranch = GitBranch; } finally { _suppressBranchSwitch = false; }
        });
    }

    private bool _switchingBranch;

    /// <summary>
    /// Check out another branch from the dropdown. Used to run `git checkout` the instant the
    /// selection changed — arrowing through the closed ComboBox switched branches on every key — and
    /// left the open tabs showing the OLD branch's text, so the next Save silently wrote that old
    /// text over the new branch's file. Now: confirm first, refuse while any tab has unsaved edits,
    /// then reload every open tab from disk (and close tabs whose file doesn't exist on the new
    /// branch).
    /// </summary>
    private async Task SwitchBranchAsync(string branch)
    {
        if (_switchingBranch) return;
        _switchingBranch = true;
        try
        {
            var dirty = Tabs.Where(t => t.IsDirty).Select(t => t.FileName).ToList();
            if (dirty.Count > 0)
            {
                RevertBranchSelection();
                StatusMessage = T("ide.branchDirty", string.Join(", ", dirty));
                ChoiceDialog.Show(T("ide.branchTitle"), T("ide.branchDirtyBody", branch, string.Join("\n", dirty)),
                    new[] { T("ide.ok") });
                return;
            }

            int answer = ChoiceDialog.Show(T("ide.branchTitle"), T("ide.branchConfirm", GitBranch, branch),
                new[] { T("ide.branchSwitchButton", branch), T("ide.cancel") });
            if (answer != 0) { RevertBranchSelection(); return; }

            var r = await _git.CheckoutAsync(branch);
            if (!r.Success)
            {
                // git's own reason (e.g. "Your local changes would be overwritten") is the useful part.
                StatusMessage = T("ide.checkoutFailed", FirstLine(string.IsNullOrWhiteSpace(r.Error) ? r.Output : r.Error));
                RevertBranchSelection();
                return;
            }

            await ReloadTabsFromDiskAsync();
            StatusMessage = T("ide.branchSwitched", branch);
            await RefreshTreeAsync();   // tree content may differ on the new branch (also reloads git)
        }
        catch (Exception ex)
        {
            _log?.Warn("Git", $"Checkout {branch} failed", ex);
            StatusMessage = T("ide.checkoutFailed", ex.GetType().Name);
            RevertBranchSelection();
        }
        finally { _switchingBranch = false; }
    }

    /// <summary>After the files changed underneath the editor (checkout): reload every CLEAN tab from
    /// disk and close the ones whose file is gone. Dirty tabs are never touched.</summary>
    private async Task ReloadTabsFromDiskAsync()
    {
        foreach (var tab in Tabs.ToList())
        {
            if (tab.IsDirty) continue;
            if (!File.Exists(tab.FullPath))
            {
                OnUi(() => RemoveTab(tab));
                continue;
            }
            var fresh = await _workspace.OpenFileAsync(tab.FullPath);
            if (fresh == null) { OnUi(() => RemoveTab(tab)); continue; }   // now binary / too large
            OnUi(() =>
            {
                if (tab.IsDirty) return;   // typed into it while we were reading — keep the typing
                tab.MarkOpened(fresh.Content);
                _workspace.AdoptDiskState(tab, fresh);
            });
        }
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

    private async void OnAgentFileMutated(string relPath, int firstLine, EditDiffResult? diff)
    {
        try
        {
            // Raised from a run's flow: drop its scope here (restored for the caller when this async method returns), or
            // the git panel refreshed below reads the RUN's repo instead of the window's.
            AgentRunScope.Enter(null);
            if (string.IsNullOrEmpty(relPath) || !_workspace.HasWorkingDirectory) return;
            string full = Path.GetFullPath(Path.Combine(_workspace.WorkingDirectory, relPath));
            // A file of another project never opens here: the window shows one project (title, tree, tabs).
            if (!ChatViewModel.IsInsideFolder(full, _workspace.WorkingDirectory)) return;
            var perf = System.Diagnostics.Stopwatch.StartNew();
            CluadeX.Helpers.UiStallWatchdog.Note("the editor following an agent edit");
            long tOpen = 0, tShow = 0;

            var existing = Tabs.FirstOrDefault(t => string.Equals(t.FullPath, full, StringComparison.OrdinalIgnoreCase));

            // NEVER clobber unsaved human edits: the old refresh-in-place silently discarded whatever
            // the user had typed in a dirty tab the moment the agent touched the same file on disk.
            // The tab keeps the user's text; its Save now sees the disk changed and asks
            // (overwrite / reload / compare) instead of silently undoing the agent's edit.
            if (existing != null && existing.IsDirty)
            {
                StatusMessage = T("ide.agentEditedDirty", Path.GetFileName(full));
                return;
            }

            var loaded = await _workspace.OpenFileAsync(full);
            if (loaded == null) return;   // binary / too large — skip live-follow
            tOpen = perf.ElapsedMilliseconds;

            FinishTypingInstantly();      // a newer edit arrived — fast-forward any reveal still running

            if (existing == null)
            {
                Tabs.Add(loaded);
                ActiveTab = loaded;
                // Not open before: the old text is gone, but the diff says which lines are new — show the file
                // without them and type them in, as for an open tab.
                if (!_settings.Settings.LiveCodingAnimationEnabled
                    || AddedBlockOf(loaded.Content, diff, firstLine) is not { } before
                    || !TryStartTypingReveal(loaded, before, loaded.Content))
                {
                    StatusMessage = T("ide.agentEdited", Path.GetFileName(full));
                    ScrollToLineRequested?.Invoke(firstLine);
                }
            }
            else
            {
                // Re-check after the await: the user may have started typing while we read the file.
                if (existing.IsDirty)
                {
                    StatusMessage = T("ide.agentEditedDirty", Path.GetFileName(full));
                    return;
                }
                ActiveTab = existing;
                _workspace.AdoptDiskState(existing, loaded);
                if (!_settings.Settings.LiveCodingAnimationEnabled
                    || !TryStartTypingReveal(existing, existing.Content, loaded.Content))
                {
                    existing.MarkOpened(loaded.Content);   // instant refresh fallback
                    StatusMessage = T("ide.agentEdited", Path.GetFileName(full));
                    ScrollToLineRequested?.Invoke(firstLine);
                }
            }

            tShow = perf.ElapsedMilliseconds;
            // A brand-new file (write_file created it) isn't in the explorer yet — refresh the
            // deepest populated ancestor folder so the tree shows it without collapsing the rest.
            if (!TreeContains(full))
                RefreshAncestorFolder(full);
            long tTree = perf.ElapsedMilliseconds;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                _log?.Info("Perf", $"agent edit {Path.GetFileName(full)} ({loaded.Content.Length / 1024} KB): open {tOpen} · show {tShow - tOpen} · tree {tTree - tShow} · drawn at {perf.ElapsedMilliseconds} ms");
                CluadeX.Helpers.UiStallWatchdog.Note("");
            });

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
    /// <paramref name="text"/> without the first block of lines the diff marks as added (searched from
    /// <paramref name="firstLine"/>), i.e. roughly the file before the edit — what a tab that was not open yet starts
    /// from so the new lines can be typed in. Null when there is no such block or it cannot be found exactly.
    /// </summary>
    public static string? AddedBlockOf(string text, EditDiffResult? diff, int firstLine)
    {
        if (diff == null || text.Length == 0) return null;
        var added = new List<string>();
        foreach (var line in diff.Lines)
        {
            if (line.Kind == EditDiffKind.Add) added.Add(line.Text);
            else if (added.Count > 0) break;   // the first contiguous run only
        }
        if (added.Count == 0) return null;

        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        string block = string.Join(nl, added);
        int from = 0;
        for (int line = 1; line < firstLine && from >= 0; line++)
        {
            from = text.IndexOf('\n', from);
            if (from >= 0) from++;
        }
        if (from < 0) from = 0;
        int at = text.IndexOf(block, from, StringComparison.Ordinal);
        if (at < 0) at = text.IndexOf(block, StringComparison.Ordinal);
        if (at < 0) return null;
        int end = at + block.Length;
        if (end + nl.Length <= text.Length && string.CompareOrdinal(text, end, nl, 0, nl.Length) == 0) end += nl.Length;
        return text[..at] + text[end..];
    }

    /// <summary>
    /// Live-typing reveal: instantly remove the OLD changed region, then "type" the new region so the agent's edit
    /// unfolds in the editor like someone typing — about 900 characters a second, at least 0.6 s and at most 2.5 s
    /// whatever the size — with the view following the insertion point. Implemented as successive MarkOpened
    /// states — binding-friendly and never marks the tab dirty. Returns false when the change doesn't animate well
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
        if (insertLen > 20_000) return false;            // huge rewrite — instant is kinder

        string head = newText[..prefix];
        string tail = newText[(prefix + insertLen)..];
        string insert = newText.Substring(prefix, insertLen);

        _typingTab = tab;
        _typingFinal = newText;
        tab.MarkOpened(head + tail);                     // phase 1: the old region vanishes (the "delete")
        StatusMessage = T("ide.agentTyping", tab.FileName);

        int pos = 0;
        int tick = 0;
        double seconds = Math.Clamp(insertLen / 900.0, 0.6, 2.5);
        int frames = Math.Max(1, (int)(seconds * 1000 / 16));
        int chunk = Math.Max(1, (int)Math.Ceiling(insertLen / (double)frames));

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
                StatusMessage = T("ide.agentEdited", tab.FileName);
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

    /// <summary>True while the agent's typing reveal is animating <paramref name="tab"/>.</summary>
    public bool IsAgentTypingInto(OpenFileTab? tab) => tab != null && _typeTimer != null && ReferenceEquals(_typingTab, tab);

    /// <summary>
    /// The user pressed a key in a tab the reveal is animating: jump to the final text NOW, before
    /// the keystroke lands. Each reveal frame is a MarkOpened of the whole buffer every 16 ms, so a
    /// keystroke typed mid-animation was overwritten by the next frame (and the tab marked clean).
    /// </summary>
    public void FinishAgentTyping() => FinishTypingInstantly();

    public async Task RefreshTreeAsync()
    {
        Tree.Clear();
        if (!_workspace.HasWorkingDirectory)
        {
            StatusMessage = T("ide.noProjectHint");
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
            StatusMessage = T("ide.loaded", ProjectFileCount);
        }
        catch (Exception ex)
        {
            _log?.Warn("Editor", "File tree load failed", ex);
            StatusMessage = T("ide.treeLoadFailed");
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
            // Read off the UI thread, fill the bound collection on it.
            var children = await Task.Run(() => _workspace.ReadChildren(node.FullPath));
            _workspace.SetChildren(node, children);
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
            StatusMessage = T("ide.cantOpen", node.Name);
            return;
        }

        Tabs.Add(tab);
        ActiveTab = tab;
        StatusMessage = T("ide.opened", tab.FileName, tab.LineCount);
    }

    /// <summary>Take a tab out of the strip and pick a neighbour — no questions asked.</summary>
    private void RemoveTab(OpenFileTab tab)
    {
        int idx = Tabs.IndexOf(tab);
        if (idx < 0) return;
        if (ReferenceEquals(_typingTab, tab)) StopTypeTimer();
        Tabs.Remove(tab);
        if (ActiveTab == tab)
        {
            // Pick neighbour
            if (Tabs.Count == 0) ActiveTab = null;
            else if (idx >= Tabs.Count) ActiveTab = Tabs[^1];
            else ActiveTab = Tabs[idx];
        }
    }

    /// <summary>Close a tab; one with unsaved edits asks Save / Don't save / Cancel first. It used to
    /// vanish silently, edits and all — one stray click on the ✕ lost an afternoon's work.</summary>
    private async Task CloseTabAsync(OpenFileTab? tab)
    {
        if (tab == null) return;
        if (tab.IsDirty)
        {
            int answer = ChoiceDialog.Show(T("ide.unsavedTitle"), T("ide.closeDirtyBody", tab.FileName),
                new[] { T("ide.save"), T("ide.dontSave"), T("ide.cancel") });
            if (answer == 2) return;                       // Cancel / Esc: keep the tab
            if (answer == 0 && !await SaveTabInteractiveAsync(tab)) return;   // save failed or was cancelled
        }
        RemoveTab(tab);
    }

    private async Task SaveActiveAsync()
    {
        if (_activeTab == null) return;
        await SaveTabInteractiveAsync(_activeTab);
    }

    /// <summary>
    /// Save one tab and deal with every way it can not happen: a non-UTF-8 file (refused), a file
    /// that changed on disk since it was opened — the agent edited it, a branch switch, another
    /// program — (ask: overwrite / reload / compare), or an I/O failure (named, logged).
    /// Returns true only when the tab's content is now on disk.
    /// </summary>
    private async Task<bool> SaveTabInteractiveAsync(OpenFileTab tab)
    {
        var result = await _workspace.SaveTabCheckedAsync(tab, overwriteExternalChanges: false);
        if (result.Outcome == SaveOutcome.ChangedOnDisk)
        {
            int answer = ChoiceDialog.Show(T("ide.changedOnDiskTitle"),
                result.DeletedOnDisk ? T("ide.deletedOnDiskBody", tab.FileName) : T("ide.changedOnDiskBody", tab.FileName),
                new[] { T("ide.overwrite"), T("ide.reloadFromDisk"), T("ide.compare"), T("ide.cancel") },
                defaultIndex: 2);
            switch (answer)
            {
                case 0:
                    result = await _workspace.SaveTabCheckedAsync(tab, overwriteExternalChanges: true);
                    break;
                case 1:
                    // Reload = take the disk version and DROP the editor's changes — say so first.
                    int sure = ChoiceDialog.Show(T("ide.changedOnDiskTitle"), T("ide.reloadDiscardsBody", tab.FileName),
                        new[] { T("ide.reloadFromDisk"), T("ide.cancel") }, defaultIndex: 1);
                    if (sure != 0) { StatusMessage = T("ide.notSaved", tab.FileName); return false; }
                    var fresh = await _workspace.OpenFileAsync(tab.FullPath);
                    if (fresh == null) { StatusMessage = T("ide.cantOpen", tab.FileName); return false; }
                    OnUi(() => { tab.MarkOpened(fresh.Content); _workspace.AdoptDiskState(tab, fresh); });
                    StatusMessage = T("ide.reloaded", tab.FileName);
                    return false;
                case 2:
                    await ShowDiskVsEditorAsync(tab);
                    StatusMessage = T("ide.notSavedCompare", tab.FileName);
                    return false;
                default:
                    StatusMessage = T("ide.notSaved", tab.FileName);
                    return false;
            }
        }

        switch (result.Outcome)
        {
            case SaveOutcome.Saved:
                StatusMessage = T("ide.saved", tab.FileName);
                _ = _workspace.EnrichGitStatusAsync(Tree);
                return true;
            case SaveOutcome.NotUtf8:
                StatusMessage = T("ide.notUtf8", tab.FileName);
                return false;
            case SaveOutcome.CannotEncode:
                // A character the file's legacy encoding cannot hold (an emoji in a TIS-620 file): offer UTF-8.
                if (!AskConvertToUtf8(tab)) { StatusMessage = T("ide.notSaved", tab.FileName); return false; }
                return await SaveTabInteractiveAsync(tab);
            default:
                _log?.Warn("Editor", $"Save failed: {tab.FullPath} — {result.Error}");
                StatusMessage = T("ide.saveFailed", tab.FileName);
                return false;
        }
    }

    /// <summary>
    /// Save every dirty tab. Reports what did NOT save, by name — it used to count only successes,
    /// so three failed saves read "Nothing to save" and the user closed the app believing it.
    /// </summary>
    private async Task SaveAllAsync()
    {
        var dirty = Tabs.Where(t => t.IsDirty).ToList();
        if (dirty.Count == 0) { StatusMessage = T("ide.nothingToSave"); return; }

        int saved = 0;
        var failed = new List<string>();
        foreach (var t in dirty)
        {
            if (await SaveTabInteractiveAsync(t)) saved++;
            else failed.Add(t.FileName);
        }

        StatusMessage = failed.Count == 0
            ? T("ide.savedCount", saved)
            : T("ide.savedSomeFailed", saved, failed.Count, string.Join(", ", failed));
    }

    // ═══════════════ App close: unsaved tabs ═══════════════

    /// <summary>True when any open tab has edits that are not on disk.</summary>
    public bool HasUnsavedChanges => Tabs.Any(t => t.IsDirty);

    /// <summary>
    /// For MainWindow.OnClosing (synchronous by necessity): with unsaved tabs, ask Save all /
    /// Discard / Cancel. Returns true when the window may close — everything saved, or the user
    /// chose to discard; false to keep the app open (Cancel, or a save that failed). Closing the app
    /// used to drop every unsaved tab without a word.
    /// </summary>
    public bool ConfirmCloseAll()
    {
        var dirty = Tabs.Where(t => t.IsDirty).ToList();
        if (dirty.Count == 0) return true;

        int answer = ChoiceDialog.Show(T("ide.unsavedTitle"),
            T("ide.closeAppBody", dirty.Count, string.Join("\n", dirty.Select(t => "  • " + t.FileName))),
            new[] { T("ide.saveAll"), T("ide.discard"), T("ide.cancel") });
        if (answer == 2) return false;
        if (answer == 1) return true;

        var failed = new List<string>();
        foreach (var tab in dirty)
        {
            var r = _workspace.SaveTabChecked(tab, overwriteExternalChanges: false);
            if (r.Outcome == SaveOutcome.ChangedOnDisk)
            {
                int ow = ChoiceDialog.Show(T("ide.changedOnDiskTitle"),
                    r.DeletedOnDisk ? T("ide.deletedOnDiskBody", tab.FileName) : T("ide.changedOnDiskBody", tab.FileName),
                    new[] { T("ide.overwrite"), T("ide.dontSave"), T("ide.cancel") });
                if (ow == 2) return false;
                if (ow == 1) continue;
                r = _workspace.SaveTabChecked(tab, overwriteExternalChanges: true);
            }
            if (r.Outcome != SaveOutcome.Saved)
            {
                if (r.Outcome == SaveOutcome.Failed) _log?.Warn("Editor", $"Save on exit failed: {tab.FullPath} — {r.Error}");
                failed.Add(tab.FileName);
            }
        }
        if (failed.Count == 0) return true;

        // Something could not be saved — closing now would lose it. Let the user decide.
        int still = ChoiceDialog.Show(T("ide.unsavedTitle"), T("ide.closeSaveFailedBody", string.Join("\n", failed.Select(f => "  • " + f))),
            new[] { T("ide.closeAnyway"), T("ide.cancel") }, defaultIndex: 1);
        return still == 0;
    }

    /// <summary>Async twin of <see cref="ConfirmCloseAll"/> for callers that can await (e.g. an
    /// OnClosing that cancels, awaits this, then closes again).</summary>
    public Task<bool> SaveOrDiscardAllAsync() => Task.FromResult(ConfirmCloseAll());

    // ═══════════════ Preview ═══════════════

    /// <summary>
    /// Preview the active file the way its reader would see it — but ONLY for formats whose default
    /// app is a viewer (HTML/SVG/Markdown/images). Everything else is shown selected in Explorer.
    /// The old code shell-opened any file, so "Preview" on a .js/.vbs/.bat/.py from an untrusted
    /// repo EXECUTED it.
    /// </summary>
    public ICommand PreviewActiveCommand => _previewActiveCommand ??= new AsyncRelayCommand(async () =>
    {
        var tab = _activeTab;
        if (tab == null) return;
        if (tab.IsDirty && !await SaveTabInteractiveAsync(tab)) return;   // preview what the editor shows

        if (SafeShell.CanPreview(tab.FullPath))
        {
            StatusMessage = SafeShell.OpenPreview(tab.FullPath)
                ? T("ide.previewOpened", tab.FileName)
                : T("ide.previewNoViewer", tab.FileName);
        }
        else
        {
            StatusMessage = SafeShell.RevealInExplorer(tab.FullPath)
                ? T("ide.previewRevealed", tab.FileName)
                : T("ide.revealFailed");
        }
    });
    private ICommand? _previewActiveCommand;

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
            _log?.Warn("Editor", "Search failed", ex);
            if (ReferenceEquals(_searchCts, cts)) SearchError = T("ide.searchFailed");
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) IsSearching = false;
        }
    }

    /// <summary>Push a scan result into the results list + summary. Caps that actually bit are
    /// stated — a silent truncation reads as "that's everything" when it isn't.
    ///
    /// Marshalled to the UI thread: WPF refuses collection changes from any other thread, and these
    /// run after an await whose continuation is only guaranteed to be on the dispatcher when the
    /// caller had a SynchronizationContext. A command invoked from the UI does; the MCP host driving
    /// the workbench programmatically does not — and that difference used to be a hard crash.</summary>
    private void ApplyOutcome(SearchOutcome outcome) => OnUi(() => ApplyOutcomeCore(outcome));

    private void ApplyOutcomeCore(SearchOutcome outcome)
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
            ? T("ide.searchNone", outcome.FilesScanned.ToString("N0"))
            : T("ide.searchSummary", outcome.TotalMatches.ToString("N0"), outcome.Files.Count.ToString("N0"),
                  outcome.FilesScanned.ToString("N0"), outcome.Elapsed.TotalSeconds.ToString("0.00"))
              + (outcome.Truncated ? T("ide.searchCapped") : "");

        OnPropertyChanged(nameof(HasNoResults));
    }

    private void ResetResults() => OnUi(() =>
    {
        SearchResults.Clear();
        OnPropertyChanged(nameof(HasNoResults));
    });

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
            StatusMessage = T("ide.caretOnSymbol");
            return;
        }

        StatusMessage = T("ide.lookingUp", symbol);
        var result = await _intel.FindDefinitionsAsync(symbol, file, line, character);

        if (!string.IsNullOrEmpty(result.Error)) { StatusMessage = $"✗ {result.Error}"; return; }
        if (result.Locations.Count == 0)
        {
            StatusMessage = T("ide.noDeclaration", symbol);
            return;
        }

        string engine = result.Engine == NavigationEngine.LanguageServer ? T("ide.engineLsp") : T("ide.engineIndex");

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
            T("ide.declarationsOf", result.Locations.Count, symbol) + $" · {engine}");
        StatusMessage = T("ide.declarationsOf", result.Locations.Count, symbol);
    }

    /// <summary>Every whole-word use of a symbol, listed in the search panel.</summary>
    public async Task FindReferencesAsync(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            StatusMessage = T("ide.caretOnSymbol");
            return;
        }

        IsSearchPanelActive = true;
        SetSearchMode(WorkbenchSearchMode.References);
        IsSearching = true;
        SearchError = "";
        SearchContextLabel = T("ide.referencesTo", symbol);

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
            StatusMessage = T("ide.referencesSummary", outcome.TotalMatches, symbol, outcome.Files.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log?.Warn("Editor", "Find references failed", ex);
            if (ReferenceEquals(_searchCts, cts)) SearchError = T("ide.searchFailed");
        }
        finally { if (ReferenceEquals(_searchCts, cts)) IsSearching = false; }
    }

    private void ShowLocations(List<CodeLocation> locations, WorkbenchSearchMode mode, string context)
        => OnUi(() => ShowLocationsCore(locations, mode, context));

    private void ShowLocationsCore(List<CodeLocation> locations, WorkbenchSearchMode mode, string context)
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

        SearchSummary = T("ide.locations", locations.Count);
        OnPropertyChanged(nameof(HasNoResults));
    }

    // ── Rename symbol (preview → confirm → apply) ──

    private string _renameOldName = "";
    public string RenameOldName { get => _renameOldName; set => SetProperty(ref _renameOldName, value); }

    private string _renameNewName = "";
    public string RenameNewName
    {
        get => _renameNewName;
        set
        {
            if (!SetProperty(ref _renameNewName, value)) return;
            // The previewed plan was for the OLD text in this box. Applying it after the name was
            // edited renamed to the name the user had since changed their mind about — drop it and
            // make them preview again.
            if (_renamePlan != null && !string.Equals(_renamePlan.NewName, (value ?? "").Trim(), StringComparison.Ordinal))
            {
                _renamePlan = null;
                CanApplyRename = false;
                RenameApplyLabel = "";
                ResetResults();
                HasSearched = false;
                SearchSummary = T("ide.renamePreviewAgain");
            }
        }
    }

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
            StatusMessage = T("ide.caretOnSymbol");
            return;
        }

        IsSearchPanelActive = true;
        SetSearchMode(WorkbenchSearchMode.Rename);
        RenameOldName = symbol;
        _renamePlan = null;
        RenameNewName = symbol;
        CanApplyRename = false;
        RenameApplyLabel = "";
        SearchContextLabel = T("ide.renameContext", symbol);
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
            // Same rule as ApplyOutcome: this runs after an await, so it is only on the dispatcher
            // when the caller had a SynchronizationContext.
            OnUi(() => { foreach (var g in plan.Files) SearchResults.Add(new SearchResultGroup(g)); });

            SearchSummary = plan.TotalEdits == 0
                ? T("ide.renameNotFound", plan.OldName)
                : T("ide.renamePlanSummary", plan.TotalEdits.ToString("N0"), plan.Files.Count.ToString("N0"))
                  + (plan.Truncated ? T("ide.renameCapped") : "");

            // Refuse to apply a truncated plan: a partial rename leaves the repo half-renamed,
            // which is worse than not renaming at all.
            CanApplyRename = plan.TotalEdits > 0 && !plan.Truncated;
            RenameApplyLabel = CanApplyRename ? T("ide.renameApplyTo", plan.Files.Count) : "";
            OnPropertyChanged(nameof(HasNoResults));
        }
        finally { IsSearching = false; }
    }

    public ICommand ApplyRenameCommand => _applyRenameCommand ??= new AsyncRelayCommand(ApplyRenameAsync);
    private ICommand? _applyRenameCommand;

    private async Task ApplyRenameAsync()
    {
        var plan = _renamePlan;
        if (plan == null || !CanApplyRename) { StatusMessage = T("ide.renamePreviewFirst"); return; }
        // The box was edited after the preview (the setter normally clears the plan; belt and braces).
        if (!string.Equals(plan.NewName, (RenameNewName ?? "").Trim(), StringComparison.Ordinal))
        {
            StatusMessage = T("ide.renamePreviewAgain");
            return;
        }

        // Never write over a tab the user has unsaved edits in — the same rule the agent
        // live-follow obeys. Their typing is not ours to discard.
        var dirty = Tabs.Where(t => t.IsDirty
                        && plan.Files.Any(f => string.Equals(f.FullPath, t.FullPath, StringComparison.OrdinalIgnoreCase)))
                        .Select(t => t.FileName).ToList();
        if (dirty.Count > 0)
        {
            StatusMessage = T("ide.renameSaveFirst", string.Join(", ", dirty));
            ChoiceDialog.Show(T("ide.renameBlockedTitle"),
                T("ide.renameBlockedBody", string.Join("\n", dirty)), new[] { T("ide.ok") });
            return;
        }

        int confirm = ChoiceDialog.Show(T("ide.renameConfirmTitle"),
            T("ide.renameConfirmBody", plan.OldName, plan.NewName, plan.TotalEdits, plan.Files.Count)
            + "\n\n" + (_intel.IsLspConnected ? T("ide.renameLspNote") : T("ide.renameTextNote"))
            + "\n\n" + T("ide.renameNoUndo"),
            new[] { T("ide.renameButton"), T("ide.cancel") }, defaultIndex: 1);
        if (confirm != 0) return;

        IsSearching = true;
        try
        {
            var result = await _intel.ApplyRenameAsync(plan);
            if (!string.IsNullOrEmpty(result.Error))
            {
                StatusMessage = T("ide.renameFailed", result.Error!);
                return;
            }

            // Reload any open tab we just rewrote so the editor isn't showing stale text.
            foreach (var path in result.ChangedPaths)
            {
                var tab = Tabs.FirstOrDefault(t => string.Equals(t.FullPath, path, StringComparison.OrdinalIgnoreCase));
                if (tab == null) continue;
                var reloaded = await _workspace.OpenFileAsync(path);
                if (reloaded != null)
                    OnUi(() => { tab.MarkOpened(reloaded.Content); _workspace.AdoptDiskState(tab, reloaded); });
            }

            StatusMessage = T("ide.renamed", result.EditsApplied, result.FilesChanged)
                + (result.Skipped.Count > 0 ? T("ide.renameSkipped", result.Skipped.Count) : "");
            if (result.Skipped.Count > 0)
            {
                // Name what was NOT renamed — a silent partial rename is how a build breaks later.
                ChoiceDialog.Show(T("ide.renameConfirmTitle"),
                    T("ide.renameSkippedBody", string.Join("\n", result.Skipped.Select(s => "  • " + s))),
                    new[] { T("ide.ok") });
            }

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
        set
        {
            if (!SetProperty(ref _isDebugging, value)) return;
            OnPropertyChanged(nameof(IsNotDebugging));
            OnPropertyChanged(nameof(CanPauseDebug));
        }
    }
    public bool IsNotDebugging => !_isDebugging;

    private bool _isPausedAtBreakpoint;
    public bool IsPausedAtBreakpoint
    {
        get => _isPausedAtBreakpoint;
        set { if (SetProperty(ref _isPausedAtBreakpoint, value)) OnPropertyChanged(nameof(CanPauseDebug)); }
    }

    /// <summary>Pause only makes sense while the program is running free.</summary>
    public bool CanPauseDebug => _isDebugging && !_isPausedAtBreakpoint;

    /// <summary>One "session ended" per session: the user's Stop and the adapter's own
    /// terminated/exited events used to each report it, so the console logged the end twice.</summary>
    private bool _debugEndReported = true;

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
        string checking = T("ide.dbgChecking");
        DebugStatus = string.IsNullOrEmpty(DebugStatus) ? checking : DebugStatus;
        var adapters = await Task.Run(DebugAdapterService.DetectAdapters);

        OnUi(() =>
        {
            DebugAdapters.Clear();
            foreach (var a in adapters) DebugAdapters.Add(a);
            UpdateDebugTarget(adapters);
        });

        if (DebugStatus == checking)
            DebugStatus = adapters.Any(a => a.IsAvailable) ? "" : T("ide.dbgNoneInstalled");
    }

    private void UpdateDebugTarget(List<DebugAdapterInfo>? adapters = null)
    {
        adapters ??= DebugAdapters.ToList();
        var tab = _activeTab;
        if (tab == null)
        {
            CanDebugActiveFile = false;
            DebugTargetLabel = T("ide.dbgOpenFile");
            return;
        }

        var adapter = DebugAdapterService.AdapterForFile(tab.FullPath, adapters);
        CanDebugActiveFile = adapter != null;
        DebugTargetLabel = adapter != null
            ? $"{tab.FileName} · {adapter.Language}"
              // C# runs the BUILT project, not this file — say so before Start is pressed.
              + (adapter.Id == "netcoredbg" ? T("ide.dbgBuildsProject") : "")
            // Say WHY it can't run, not just that the button is greyed out.
            : T("ide.dbgNoAdapterForFile", tab.FileName);
    }

    public ICommand StartDebugCommand => _startDebugCommand ??= new AsyncRelayCommand(StartDebugAsync);
    private ICommand? _startDebugCommand;

    private async Task StartDebugAsync()
    {
        if (_debug.IsRunning) { DebugStatus = T("ide.dbgAlreadyRunning"); return; }
        var tab = _activeTab;
        if (tab == null) { DebugStatus = T("ide.dbgOpenFileFirst"); return; }

        var adapters = await Task.Run(DebugAdapterService.DetectAdapters);
        var adapter = DebugAdapterService.AdapterForFile(tab.FullPath, adapters);
        if (adapter == null)
        {
            var known = adapters.FirstOrDefault(a => !a.IsAvailable);
            DebugStatus = known != null
                ? T("ide.dbgNoAdapterHint", tab.Extension, known.InstallHint)
                : T("ide.dbgNoAdapter", tab.Extension);
            return;
        }

        if (tab.IsDirty)
        {
            // Debugging a stale file on disk while the editor shows something else is the single
            // most confusing thing a debugger can do — breakpoints land on the wrong lines.
            int save = ChoiceDialog.Show(T("ide.unsavedTitle"), T("ide.dbgUnsavedBody", tab.FileName),
                new[] { T("ide.saveAndDebug"), T("ide.debugWithoutSaving"), T("ide.cancel") });
            if (save == 2) return;
            // A save that did not happen (refused, failed, the file changed on disk) must not be
            // followed by a debug run of the stale file the user just tried to replace.
            if (save == 0 && !await SaveTabInteractiveAsync(tab)) { DebugStatus = T("ide.dbgNotStartedUnsaved"); return; }
        }

        _debugEndReported = false;
        AppendDebugConsole(T("ide.dbgStarting", adapter.Language, adapter.Id));
        DebugStatus = T("ide.dbgStartingStatus");
        IsDebugging = true;

        var byFile = _breakpoints.ToDictionary(k => k.Key, v => v.Value.OrderBy(l => l).ToList());
        if (!byFile.ContainsKey(tab.FullPath)) byFile[tab.FullPath] = new List<int>();

        var result = await _debug.StartAsync(adapter, tab.FullPath, byFile);
        if (!result.Started)
        {
            _debugEndReported = true;
            IsDebugging = false;
            DebugStatus = $"✗ {result.Error}";
            AppendDebugConsole(T("ide.dbgFailedLine", result.Error ?? ""));
            return;
        }
        if (!string.Equals(result.Program, tab.FullPath, StringComparison.OrdinalIgnoreCase) && result.Program.Length > 0)
            AppendDebugConsole(T("ide.dbgRunning", result.Program));

        foreach (var (file, bps) in result.Breakpoints)
        {
            foreach (var bp in bps.Where(b => !b.Verified))
                AppendDebugConsole(T("ide.dbgBpNotBound", Path.GetFileName(file), bp.RequestedLine)
                                   + (string.IsNullOrEmpty(bp.Message) ? "" : $" — {bp.Message}"));
            foreach (var bp in bps.Where(b => b.Moved))
                AppendDebugConsole(T("ide.dbgBpMoved", Path.GetFileName(file), bp.RequestedLine, bp.Line));
        }

        DebugStatus = T("ide.dbgRunningStatus");
    }

    public ICommand StopDebugCommand => _stopDebugCommand ??= new AsyncRelayCommand(async () =>
    {
        await _debug.StopAsync();
        OnUi(() => OnDebugSessionEnded(T("ide.dbgStoppedByUser")));
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
        if (!_debug.IsPaused) { AppendDebugConsole(T("ide.dbgPauseFirst")); return; }

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
            DebugStatus = T("ide.dbgPaused", reason);

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
            DebugStatus = T("ide.dbgRunningStatus");
            ExecutionLine = 0;
            CallStack.Clear();
            DebugVariables.Clear();
            ExecutionPointerMoved?.Invoke("", 0);
        });

        _debug.Terminated += why => OnUi(() => OnDebugSessionEnded(why));
    }

    private void OnDebugSessionEnded(string why)
    {
        if (_debugEndReported) return;
        _debugEndReported = true;
        IsDebugging = false;
        IsPausedAtBreakpoint = false;
        DebugStatus = T("ide.dbgSessionEnded", why);
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
            var variables = await _debug.GetVariablesAsync(frame.Id);
            OnUi(() =>
            {
                DebugVariables.Clear();
                foreach (var v in variables) DebugVariables.Add(v);
            });

            if (!string.IsNullOrEmpty(frame.FilePath) && File.Exists(frame.FilePath))
            {
                await OpenPathAsync(frame.FilePath);
                ExecutionLine = frame.Line;
                ExecutionPointerMoved?.Invoke(frame.FilePath, frame.Line);
                ScrollToLineRequested?.Invoke(frame.Line);
            }
        }
        catch (Exception ex)
        {
            _log?.Warn("Debugger", "Loading a stack frame failed", ex);
            AppendDebugConsole(T("ide.dbgFrameFailed"));
        }
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

/// <summary>One changed file in the source-control panel (from porcelain -z `git status`).</summary>
public class GitChangeItem
{
    public string Status { get; set; } = "?";   // M / A / D / R / ?? …
    /// <summary>Display path, relative to the opened folder.</summary>
    public string Path { get; set; } = "";
    /// <summary>Repository-root relative (what `git show HEAD:path` needs).</summary>
    public string RepoPath { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string FileName => System.IO.Path.GetFileName(Path);
    /// <summary>VS Code-style status color: modified=amber, added/untracked=green, deleted=red.</summary>
    public string StatusColor => Status switch
    {
        "A" or "??" => ThemeColors.Hex("Git.Added", "#7EE0A3"),
        "D" => ThemeColors.Hex("Git.Deleted", "#FF7A93"),
        "R" => ThemeColors.Hex("Git.Renamed", "#8AB8FF"),
        _ => ThemeColors.Hex("Git.Modified", "#FFD37E"),
    };
}
