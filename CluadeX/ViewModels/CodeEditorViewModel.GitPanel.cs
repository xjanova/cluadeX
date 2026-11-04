using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Helpers;
using CluadeX.Views.Editor;

namespace CluadeX.ViewModels;

/// <summary>
/// The Source Control panel (owner 2026-11-04, looking at the old one: "ทำให้ชัดเจนขึ้น เครื่องมือมากขึ้นหน่อย สมเป็นโปรแกรม
/// ชั้นเลิศ"). The old panel was a 9 px list, a one-line box and two buttons; it could only "stage everything and commit".
/// Now: the branch with ↑ahead ↓behind, Fetch / Pull / Push, a ⋯ menu (new branch, switch, stash, stash pop, discard
/// all, open the folder), Staged and Changes groups with per-file stage / unstage / discard, a multi-line commit box
/// (Ctrl+Enter) that commits ONLY the staged files when something is staged, Commit &amp; Push, and an AI draft of the
/// message.
///
/// One git operation at a time (<see cref="IsGitBusy"/>): a second click says what is running instead of starting
/// another. Every operation acts on the repository root read when it started, and its result is dropped if the user
/// opened another project meanwhile. Pull is fast-forward only, push never forces, discard is always confirmed with
/// Cancel as the default, and nothing here resets --hard or cleans.
/// </summary>
public partial class CodeEditorViewModel
{
    // ═══════════════ State the panel shows ═══════════════

    /// <summary>The index side: what the next commit takes when anything is staged.</summary>
    public ObservableCollection<GitChangeItem> GitStagedChanges { get; } = new();
    /// <summary>The working-tree side: edits not staged yet, untracked files, conflicts.</summary>
    public ObservableCollection<GitChangeItem> GitUnstagedChanges { get; } = new();

    private bool _isGitMissing;
    /// <summary>git is not on PATH — the panel says so instead of a silent "no repository".</summary>
    public bool IsGitMissing { get => _isGitMissing; private set => SetProperty(ref _isGitMissing, value); }

    private bool _isNotGitRepo;
    /// <summary>A project is open but it is not inside a git repository: the panel offers to initialize one.</summary>
    public bool IsNotGitRepo { get => _isNotGitRepo; private set => SetProperty(ref _isNotGitRepo, value); }

    public bool IsGitClean => HasGitRepo && GitChanges.Count == 0;
    public bool HasStagedChanges => GitStagedChanges.Count > 0;
    public bool HasUnstagedChanges => GitUnstagedChanges.Count > 0;
    public int GitTotalChanges => GitChanges.Count;

    private string _gitUpstream = "";
    public string GitUpstream { get => _gitUpstream; private set => SetProperty(ref _gitUpstream, value); }
    private int _gitAhead, _gitBehind;
    public int GitAhead { get => _gitAhead; private set => SetProperty(ref _gitAhead, value); }
    public int GitBehind { get => _gitBehind; private set => SetProperty(ref _gitBehind, value); }
    private bool _gitUpstreamGone, _gitDetached, _gitNoCommits;

    /// <summary>"↑2 ↓1", "no upstream", "upstream gone" — or empty when the branch is level with its upstream.</summary>
    public string GitSyncText
    {
        get
        {
            if (!HasGitRepo || _gitDetached) return "";
            if (_gitUpstreamGone) return T("scm.upstreamGone");
            if (string.IsNullOrEmpty(_gitUpstream)) return T("scm.noUpstream");
            var parts = new List<string>();
            if (_gitAhead > 0) parts.Add($"↑{_gitAhead}");
            if (_gitBehind > 0) parts.Add($"↓{_gitBehind}");
            return string.Join(" ", parts);
        }
    }

    public string GitBranchTooltip
    {
        get
        {
            if (!HasGitRepo) return "";
            if (_gitDetached) return T("scm.branchTipDetached");
            if (_gitUpstreamGone) return T("scm.branchTipGone", GitBranch, _gitUpstream);
            if (string.IsNullOrEmpty(_gitUpstream)) return T("scm.branchTipNoUpstream", GitBranch);
            if (_gitAhead == 0 && _gitBehind == 0) return T("scm.branchTipLevel", GitBranch, _gitUpstream);
            return T("scm.branchTip", GitBranch, _gitUpstream, _gitAhead, _gitBehind);
        }
    }

    private GitLastCommit? _gitLastCommit;
    public bool HasLastCommit => _gitLastCommit != null;
    public string GitLastCommitHash => _gitLastCommit?.ShortHash ?? "";
    public string GitLastCommitSubject => _gitLastCommit?.Subject ?? (_gitNoCommits ? T("scm.noCommitsYet") : "");
    public string GitLastCommitWhen => _gitLastCommit == null ? "" : RelativeTime(_gitLastCommit.When);

    private int _gitStashCount;
    public int GitStashCount { get => _gitStashCount; private set { if (SetProperty(ref _gitStashCount, value)) OnPropertyChanged(nameof(GitStashPopLabel)); } }
    public string GitStashPopLabel => _gitStashCount > 0 ? T("scm.stashPopN", _gitStashCount) : T("scm.stashPop");

    private string _gitRepoRoot = "";
    /// <summary>The repository root of the last refresh (the opened folder may be a subfolder of it).</summary>
    public string GitRepoRoot { get => _gitRepoRoot; private set => SetProperty(ref _gitRepoRoot, value); }

    private bool _isStagedExpanded = true, _isChangesExpanded = true;
    public bool IsStagedExpanded { get => _isStagedExpanded; set => SetProperty(ref _isStagedExpanded, value); }
    public bool IsChangesExpanded { get => _isChangesExpanded; set => SetProperty(ref _isChangesExpanded, value); }

    // ── One operation at a time ──

    private string? _gitOp;
    /// <summary>A fetch, pull, push, commit, stash, discard … is running; every other git button waits for it.</summary>
    public bool IsGitBusy => _gitOp != null;
    public bool IsGitIdle => _gitOp == null;
    public string GitBusyText => _gitOp == null ? "" : T(_gitOp);

    private bool TryBeginGitOp(string busyKey)
    {
        if (_gitOp != null)
        {
            StatusMessage = T("scm.busy", T(_gitOp));
            return false;
        }
        _gitOp = busyKey;
        RaiseGitBusy();
        return true;
    }

    private void EndGitOp()
    {
        _gitOp = null;
        RaiseGitBusy();
    }

    private void RaiseGitBusy()
    {
        OnPropertyChanged(nameof(IsGitBusy));
        OnPropertyChanged(nameof(IsGitIdle));
        OnPropertyChanged(nameof(GitBusyText));
        CommandManager.InvalidateRequerySuggested();
    }

    // ── Commit box ──

    public int CommitSubjectLength => GitService.CommitSubjectLength(CommitMessage);
    public bool IsCommitSubjectLong => CommitSubjectLength > GitService.CommitSubjectSoftLimit;
    public string CommitSubjectHint => IsCommitSubjectLong ? T("scm.subjectLong", CommitSubjectLength, GitService.CommitSubjectSoftLimit) : "";

    /// <summary>The commit button says what it will take: the staged files, or everything.</summary>
    public string CommitButtonLabel => HasStagedChanges ? T("scm.commitStaged", GitStagedChanges.Count) : T("scm.commitAll");
    public string CommitButtonTip => HasStagedChanges
        ? T("scm.commitTipStaged", GitStagedChanges.Count, GitUnstagedChanges.Count)
        : T("scm.commitTipAll");

    private void RaiseCommitHint()
    {
        OnPropertyChanged(nameof(CommitSubjectLength));
        OnPropertyChanged(nameof(IsCommitSubjectLong));
        OnPropertyChanged(nameof(CommitSubjectHint));
    }

    private void RaiseGitPanel()
    {
        foreach (var name in new[]
                 {
                     nameof(IsGitClean), nameof(HasStagedChanges), nameof(HasUnstagedChanges), nameof(GitTotalChanges),
                     nameof(GitSyncText), nameof(GitBranchTooltip), nameof(HasLastCommit), nameof(GitLastCommitHash),
                     nameof(GitLastCommitSubject), nameof(GitLastCommitWhen), nameof(CommitButtonLabel), nameof(CommitButtonTip),
                     nameof(GitChangeCount), nameof(GitStashPopLabel), nameof(CommitSubjectHint),
                 })
            OnPropertyChanged(name);
    }

    /// <summary>Called once from the constructor.</summary>
    private void HookGitPanel()
    {
        _loc.LanguageChanged += () =>
        {
            _gitListSignature = "";   // the badge tooltips are words: rebuild the rows in the new language
            RaiseGitPanel();
            OnPropertyChanged(nameof(GitBusyText));
            _ = RefreshGitAsync();
        };
    }

    // ═══════════════ Commands ═══════════════

    private ICommand? _fetchCmd, _pullCmd, _pushCurrentCmd, _stageCmd, _unstageCmd, _stageAllCmd, _unstageAllCmd, _discardCmd,
        _discardAllCmd, _newBranchCmd, _switchBranchCmd, _stashCmd, _stashPopCmd, _openRepoFolderCmd, _initRepoCmd,
        _commitPushCmd, _draftCmd, _cancelDraftCmd, _toggleStagedCmd, _toggleChangesCmd;

    public ICommand FetchCommand => _fetchCmd ??= new AsyncRelayCommand(FetchAsync);
    public ICommand PullCommand => _pullCmd ??= new AsyncRelayCommand(PullAsync);
    /// <summary>Push THIS repository (the header button). Pushing every repo of the project stays in the ⋯ menu.</summary>
    public ICommand PushCurrentCommand => _pushCurrentCmd ??= new AsyncRelayCommand(PushCurrentAsync);
    public ICommand StageCommand => _stageCmd ??= new AsyncRelayCommand<GitChangeItem>(i => StageAsync(i, stage: true));
    public ICommand UnstageCommand => _unstageCmd ??= new AsyncRelayCommand<GitChangeItem>(i => StageAsync(i, stage: false));
    public ICommand StageAllCommand => _stageAllCmd ??= new AsyncRelayCommand(() => StageAsync(null, stage: true));
    public ICommand UnstageAllCommand => _unstageAllCmd ??= new AsyncRelayCommand(() => StageAsync(null, stage: false));
    public ICommand DiscardCommand => _discardCmd ??= new AsyncRelayCommand<GitChangeItem>(i => i == null ? Task.CompletedTask : DiscardAsync(new[] { i.RepoPath }));
    public ICommand DiscardAllCommand => _discardAllCmd ??= new AsyncRelayCommand(() => DiscardAsync(null));
    public ICommand NewBranchCommand => _newBranchCmd ??= new AsyncRelayCommand(NewBranchAsync);
    public ICommand SwitchBranchCommand => _switchBranchCmd ??= new AsyncRelayCommand<string>(b => string.IsNullOrEmpty(b) || b == GitBranch ? Task.CompletedTask : SwitchBranchAsync(b));
    public ICommand StashCommand => _stashCmd ??= new AsyncRelayCommand(() => StashAsync(pop: false));
    public ICommand StashPopCommand => _stashPopCmd ??= new AsyncRelayCommand(() => StashAsync(pop: true));
    public ICommand OpenRepoFolderCommand => _openRepoFolderCmd ??= new RelayCommand(OpenRepoFolder);
    public ICommand InitRepoCommand => _initRepoCmd ??= new AsyncRelayCommand(InitRepoAsync);
    public ICommand CommitAndPushCommand => _commitPushCmd ??= new AsyncRelayCommand(() => CommitCoreAsync(thenPush: true));
    public ICommand DraftCommitMessageCommand => _draftCmd ??= new AsyncRelayCommand(DraftCommitMessageAsync);
    public ICommand CancelDraftCommand => _cancelDraftCmd ??= new RelayCommand(() => { _draftCancelledByUser = true; _draftCts?.Cancel(); });
    public ICommand ToggleStagedGroupCommand => _toggleStagedCmd ??= new RelayCommand(() => IsStagedExpanded = !IsStagedExpanded);
    public ICommand ToggleChangesGroupCommand => _toggleChangesCmd ??= new RelayCommand(() => IsChangesExpanded = !IsChangesExpanded);

    // ═══════════════ Refresh ═══════════════

    private int _gitRefreshGen;
    private string _gitListSignature = "";

    private bool SameProject(string? project) =>
        project != null && _workspace.HasWorkingDirectory
        && string.Equals(project, _workspace.WorkingDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>Run on the UI thread and wait for it (collections bound to the view may only change there). Runs inline
    /// in a harness with no WPF application.</summary>
    private static void OnUiNow(Action action)
    {
        var d = App.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) action();
        else d.Invoke(action);
    }

    /// <summary>Reload the panel: branch + upstream, Staged / Changes, the last commit, the stash count, local branches.
    /// A refresh that finishes after a newer one started — or after another project was opened — is dropped.</summary>
    public async Task RefreshGitAsync()
    {
        int gen = Interlocked.Increment(ref _gitRefreshGen);
        string? project = _workspace.HasWorkingDirectory ? _workspace.WorkingDirectory : null;
        if (project == null)
        {
            OnUiNow(() => ApplyGit(null, null, null, 0, missing: false));
            return;
        }
        try
        {
            var r = await _git.StatusEntriesAsync();
            GitResult? branches = null;
            GitLastCommit? last = null;
            int stashes = 0;
            bool missing = false;
            if (r.Success)
            {
                branches = await _git.ListBranchesAsync(includeRemote: false);
                if (!r.NoCommits) last = await GitService.LastCommitAsync(r.RepoRoot);
                stashes = await GitService.StashCountAsync(r.RepoRoot);
            }
            else missing = !await GitService.IsGitAvailableAsync();

            if (_disposed || gen != Volatile.Read(ref _gitRefreshGen) || !SameProject(project)) return;
            OnUiNow(() => ApplyGit(r, branches, last, stashes, missing));
        }
        catch (Exception ex) { _log?.Debug("Git", $"git panel refresh failed: {ex.GetType().Name}"); /* best-effort */ }
    }

    private void ApplyGit(GitStatusSnapshot? r, GitResult? branches, GitLastCommit? last, int stashes, bool missing)
    {
        IsGitMissing = missing;
        IsNotGitRepo = r != null && !r.Success && !missing && r.Error != "no-workdir";
        if (r == null || !r.Success)
        {
            GitBranch = "";
            GitRepoRoot = "";
            GitChanges.Clear(); GitStagedChanges.Clear(); GitUnstagedChanges.Clear();
            _gitListSignature = "";
            _gitLastCommit = null;
            GitUpstream = ""; GitAhead = 0; GitBehind = 0; _gitUpstreamGone = false; _gitDetached = false; _gitNoCommits = false;
            GitStashCount = 0;
            RaiseGitPanel();
            return;
        }

        GitRepoRoot = r.RepoRoot;
        _gitDetached = r.IsDetached;
        _gitNoCommits = r.NoCommits;
        _gitUpstreamGone = r.UpstreamGone;
        GitUpstream = r.Upstream;
        GitAhead = r.Ahead;
        GitBehind = r.Behind;
        _gitLastCommit = last;
        GitStashCount = stashes;
        GitBranch = r.IsDetached ? T("ide.detachedHead") : r.Branch;

        // Rebuild the rows only when something changed: a refresh after every save used to rebuild the list, which
        // threw away the keyboard focus on a row and redrew the whole list for nothing.
        var (staged, changes) = GitService.GroupChanges(r.Entries);
        string signature = string.Join("\n", r.Entries.Select(e => e.XY + e.RepoPath + "\0" + e.OrigRepoPath));
        if (signature != _gitListSignature)
        {
            _gitListSignature = signature;
            GitChanges.Clear();
            foreach (var e in r.Entries)
                GitChanges.Add(new GitChangeItem
                {
                    Status = e.Code, RepoPath = e.RepoPath, FullPath = e.FullPath,
                    Path = DisplayPath(e.FullPath, e.RepoPath),   // relative to the opened folder (may be a repo subfolder)
                });
            GitStagedChanges.Clear();
            foreach (var row in staged) GitStagedChanges.Add(ItemFor(row));
            GitUnstagedChanges.Clear();
            foreach (var row in changes) GitUnstagedChanges.Add(ItemFor(row));
        }

        // Local branches → the branch menu; keep the selection synced WITHOUT re-triggering a checkout.
        _suppressBranchSwitch = true;
        try
        {
            GitBranches.Clear();
            if (branches?.Success == true)
            {
                foreach (var bl in (branches.Output ?? "").Split('\n'))
                {
                    string name = bl.TrimEnd('\r').TrimStart('*', '+', ' ').Trim();
                    if (name.Length == 0 || name.Contains("HEAD") || name.StartsWith('(')) continue;
                    GitBranches.Add(name);
                }
            }
            SelectedBranch = GitBranch;
        }
        finally { _suppressBranchSwitch = false; }
        RaiseGitPanel();
    }

    private GitChangeItem ItemFor(GitPanelRow row) => new()
    {
        Status = row.Entry.Code,
        RepoPath = row.Entry.RepoPath,
        OrigRepoPath = row.Entry.OrigRepoPath,
        FullPath = row.Entry.FullPath,
        Path = DisplayPath(row.Entry.FullPath, row.Entry.RepoPath),
        IsStaged = row.IsStaged,
        HasStagedPart = row.Entry.XY.Length > 0 && row.Entry.XY[0] is not ' ' and not '?',
        Kind = row.Kind,
        Letter = row.Letter,
        KindLabel = T("scm.kind." + row.Kind),
    };

    /// <summary>"just now", "5 min ago", "3 h ago", "2 d ago", else the date.</summary>
    private string RelativeTime(DateTimeOffset when)
    {
        if (when == DateTimeOffset.MinValue) return "";
        var age = DateTimeOffset.Now - when;
        if (age.TotalMinutes < 1) return T("scm.time.now");
        if (age.TotalHours < 1) return T("scm.time.min", (int)age.TotalMinutes);
        if (age.TotalDays < 1) return T("scm.time.hour", (int)age.TotalHours);
        if (age.TotalDays < 30) return T("scm.time.day", (int)age.TotalDays);
        return when.LocalDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    // ═══════════════ Operations ═══════════════

    private List<string> DirtyTabNames() => Tabs.Where(t => t.IsDirty).Select(t => t.FileName).ToList();

    /// <summary>git's failure as a localized sentence; for an unknown cause, git's own first line (never an exception).</summary>
    private string FailureText(string opKey, GitResult r)
    {
        var kind = GitService.ClassifyFailure(r);
        string reason;
        if (kind != GitFailure.Other) reason = T("scm.fail." + kind);
        // ExitCode -1 without a timeout: git never ran (not installed, refused input) — its "error" is our own text.
        else if (r.ExitCode == -1) reason = T("scm.fail.NotStarted");
        else
        {
            reason = FirstLine(string.IsNullOrWhiteSpace(r.Error) ? r.Output : r.Error);
            if (reason.Length > 200) reason = reason[..200] + "…";
            if (reason.Length == 0) reason = T("scm.fail.Other");
        }
        return T("scm.opFailed", T(opKey), reason);
    }

    /// <summary>
    /// The frame of every panel operation: one at a time, a fresh status (so the repo root and the file list are what
    /// is on disk NOW, not what the panel showed a minute ago), the result reported only if the same project is still
    /// open, then a refresh. <paramref name="filesChanged"/>: the operation rewrote files (pull, stash, discard) — clean
    /// tabs are reloaded and the tree rebuilt.
    /// </summary>
    private async Task GitOpAsync(string busyKey, string opKey, Func<string, GitStatusSnapshot, Task<string?>> body, bool filesChanged = false)
    {
        if (!_workspace.HasWorkingDirectory) return;
        if (!TryBeginGitOp(busyKey)) return;
        string project = _workspace.WorkingDirectory;
        try
        {
            var snap = await _git.StatusEntriesAsync();
            if (!snap.Success || string.IsNullOrEmpty(snap.RepoRoot)) { StatusMessage = T("scm.notRepoShort"); return; }
            string? message = await body(snap.RepoRoot, snap);
            if (_disposed) return;
            if (!SameProject(project)) { _log?.Debug("Git", $"{opKey}: finished after the project changed — result dropped"); return; }
            if (message != null) StatusMessage = message;
        }
        catch (Exception ex)
        {
            _log?.Warn("Git", $"{opKey} failed", ex);
            if (SameProject(project)) StatusMessage = T("scm.opFailed", T(opKey), T("scm.fail.Other"));
        }
        finally
        {
            EndGitOp();
            if (!_disposed)
            {
                if (filesChanged && SameProject(project))
                {
                    await ReloadTabsFromDiskAsync();
                    await RefreshTreeAsync();   // also reloads git
                }
                else
                {
                    await RefreshGitAsync();
                    if (SameProject(project)) _ = _workspace.EnrichGitStatusAsync(Tree);
                }
            }
        }
    }

    private Task FetchAsync() => GitOpAsync("scm.busy.fetch", "scm.op.fetch", async (root, _) =>
    {
        var r = await GitService.RunIn(root, GitService.PanelArgs.Fetch(), 120_000);
        return r.Success ? T("scm.fetched") : FailureText("scm.op.fetch", r);
    });

    private Task PullAsync() => GitOpAsync("scm.busy.pull", "scm.op.pull", async (root, snap) =>
    {
        // Pull rewrites files on disk: an unsaved tab would then hold text from before the pull, and its next save
        // would quietly undo the pulled change.
        var dirty = DirtyTabNames();
        if (dirty.Count > 0)
        {
            ChoiceDialog.Show(T("scm.pullTitle"), T("scm.dirtyBody", T("scm.op.pull"), string.Join("\n", dirty)), new[] { T("ide.ok") });
            return T("scm.dirtyShort", T("scm.op.pull"));
        }
        if (snap.IsDetached) return T("scm.pullDetached");
        if (string.IsNullOrEmpty(snap.Upstream)) return T("scm.pullNoUpstream", snap.Branch);

        int c = ChoiceDialog.Show(T("scm.pullTitle"), T("scm.pullConfirm", snap.Upstream, snap.Branch, snap.Behind),
            new[] { T("scm.pullButton"), T("ide.cancel") }, defaultIndex: 0);
        if (c != 0) return T("scm.cancelled", T("scm.op.pull"));

        var r = await GitService.RunIn(root, GitService.PanelArgs.PullFastForward(), 120_000);
        return r.Success ? T("scm.pulled", snap.Upstream) : FailureText("scm.op.pull", r);
    }, filesChanged: true);

    private Task PushCurrentAsync() => GitOpAsync("scm.busy.push", "scm.op.push", async (root, _) => await PushRepoAsync(root, confirm: true));

    /// <summary>Push one repository with <see cref="GitMultiRepo"/>'s plan (a new branch gets its upstream; never
    /// --force). Returns the status line.</summary>
    private async Task<string> PushRepoAsync(string root, bool confirm)
    {
        var state = await GitMultiRepo.StateAsync(root);
        var (args, outcome, _) = GitMultiRepo.Plan(state);
        if (args == null)
        {
            if (state.Detached || string.IsNullOrEmpty(state.Branch)) return T("scm.pushDetached");
            if (state.Remotes.Count == 0) return T("scm.pushNoRemote");
            if (string.IsNullOrEmpty(state.Upstream)) return T("scm.pushWhichRemote", string.Join(", ", state.Remotes));
            return state.Behind > 0 ? T("scm.pushNothingBehind", state.Upstream, state.Behind) : T("scm.pushUpToDate", state.Upstream);
        }
        bool newBranch = string.IsNullOrEmpty(state.Upstream);
        string target = newBranch ? $"{args[2]}/{state.Branch}" : state.Upstream!;
        if (confirm)
        {
            string body = newBranch
                ? T("scm.pushConfirmNew", state.Branch!, args[2])
                : T("scm.pushConfirm", state.Ahead, state.Branch!, state.Upstream!);
            int c = ChoiceDialog.Show(T("scm.pushTitle"), body, new[] { T("scm.pushButton"), T("ide.cancel") }, defaultIndex: 0);
            if (c != 0) return T("scm.cancelled", T("scm.op.push"));
        }
        // A credential manager's sign-in window may need the user: two minutes, not git's 30 s default.
        var r = await GitService.RunIn(root, args, 120_000);
        return r.Success ? T("scm.pushed", target) : FailureText("scm.op.push", r);
    }

    /// <summary>Stage / unstage one row, or (item null) everything in that group as git sees it right now.</summary>
    private Task StageAsync(GitChangeItem? item, bool stage) => GitOpAsync(
        stage ? "scm.busy.stage" : "scm.busy.unstage", stage ? "scm.op.stage" : "scm.op.unstage", async (root, snap) =>
        {
            var (staged, changes) = GitService.GroupChanges(snap.Entries);
            var paths = new List<string>();
            if (item != null)
            {
                paths.Add(item.RepoPath);
                // An unstaged rename keeps its old name in the index until both sides are named.
                if (!stage && !string.IsNullOrEmpty(item.OrigRepoPath)) paths.Add(item.OrigRepoPath);
            }
            else
            {
                foreach (var row in stage ? changes : staged)
                {
                    paths.Add(row.Entry.RepoPath);
                    if (!stage && !string.IsNullOrEmpty(row.Entry.OrigRepoPath)) paths.Add(row.Entry.OrigRepoPath!);
                }
            }
            if (paths.Count == 0) return T(stage ? "scm.nothingToStage" : "scm.nothingToUnstage");
            var calls = stage ? GitService.PanelArgs.Stage(paths) : GitService.PanelArgs.Unstage(paths, snap.NoCommits);
            var r = await GitService.RunAllIn(root, calls);
            if (!r.Success) return FailureText(stage ? "scm.op.stage" : "scm.op.unstage", r);
            return item != null ? null : T(stage ? "scm.stagedAll" : "scm.unstagedAll", paths.Count);
        });

    /// <summary>Most untracked files one discard may move to the Recycle Bin; past this it is a job for Explorer.</summary>
    public const int MaxDiscardUntracked = 300;

    /// <summary>Test seam: replaces the move to the Recycle Bin (a harness must not fill the user's bin).</summary>
    public static Func<string, bool>? RecycleOverride { get; set; }

    /// <summary>
    /// Throw away working-tree changes: tracked files go back to their staged / committed version (<c>git restore
    /// --worktree</c>), untracked files listed in the dialog go to the Recycle Bin. Staged changes are kept. Always
    /// confirmed, with every file listed and Cancel as the default (Enter and Esc both keep everything).
    /// <paramref name="onlyRepoPaths"/>: one row's path; null = every row of Changes.
    /// </summary>
    private Task DiscardAsync(IReadOnlyCollection<string>? onlyRepoPaths) => GitOpAsync("scm.busy.discard", "scm.op.discard", async (root, snap) =>
    {
        var (staged, changes) = GitService.GroupChanges(snap.Entries);
        var rows = changes.Where(r => r.Kind != GitChangeKind.Conflict
                                      && (onlyRepoPaths == null || onlyRepoPaths.Contains(r.Entry.RepoPath))).ToList();
        if (rows.Count == 0) return T("scm.discardNothing");

        var tracked = rows.Where(r => r.Kind != GitChangeKind.Untracked).Select(r => r.Entry.RepoPath).Distinct().ToList();
        var untracked = new List<string>();
        foreach (var r in rows.Where(r => r.Kind == GitChangeKind.Untracked))
        {
            if (r.Entry.RepoPath.EndsWith('/'))
            {
                // "?? folder/" — list the files inside so the dialog names every one that will go.
                var inside = await GitService.UntrackedFilesUnderAsync(root, r.Entry.RepoPath);
                if (inside == null) return T("scm.discardListFailed", r.Entry.RepoPath);
                untracked.AddRange(inside);
            }
            else untracked.Add(r.Entry.RepoPath);
        }
        if (untracked.Count > MaxDiscardUntracked) return T("scm.discardTooMany", untracked.Count, MaxDiscardUntracked);

        var deletions = new List<string>();
        foreach (var rel in untracked.Distinct())
        {
            string? full = GitService.SafeRepoPath(root, rel);
            if (full == null) return T("scm.discardUnsafe", rel);
            deletions.Add(full);
        }
        if (deletions.Count > 0 && !HasRecycleBin(root)) return T("scm.discardNoBin", deletions.Count);
        var targets = new HashSet<string>(deletions, StringComparer.OrdinalIgnoreCase);
        foreach (var rel in tracked) if (GitService.SafeRepoPath(root, rel) is { } f) targets.Add(f);
        var dirty = Tabs.Where(t => t.IsDirty && targets.Contains(SafeFull(t.FullPath))).Select(t => t.FileName).ToList();
        if (dirty.Count > 0)
        {
            ChoiceDialog.Show(T("scm.discardTitle"), T("scm.discardDirtyBody", string.Join("\n", dirty)), new[] { T("ide.ok") });
            return T("scm.dirtyShort", T("scm.op.discard"));
        }

        int total = tracked.Count + deletions.Count;
        const int maxListed = 25;
        var body = new StringBuilder();
        body.AppendLine(T("scm.discardHeader", total));
        body.AppendLine();
        var listed = tracked.Select(p => $"  {RowLetter(rows, p)}  {DisplayPath(GitService.SafeRepoPath(root, p) ?? "", p)}")
            .Concat(untracked.Distinct().Select(p => $"  U  {DisplayPath(GitService.SafeRepoPath(root, p) ?? "", p)}")).ToList();
        foreach (var l in listed.Take(maxListed)) body.AppendLine(l);
        if (listed.Count > maxListed) body.AppendLine("  " + T("ide.commitMore", listed.Count - maxListed));
        body.AppendLine();
        if (tracked.Count > 0) body.AppendLine(T("scm.discardTrackedNote", tracked.Count));
        if (deletions.Count > 0) body.AppendLine(T("scm.discardUntrackedNote", deletions.Count));
        if (staged.Count > 0) body.AppendLine(T("scm.discardStagedKept", staged.Count));
        body.AppendLine(T("scm.discardNoUndo"));

        var buttons = new[] { T("scm.discardButton", total), T("ide.cancel") };
        // Destructive: Cancel is the default, so Enter (and Esc) keep everything.
        int c = ChoiceDialog.Show(T("scm.discardTitle"), body.ToString().TrimEnd(), buttons, defaultIndex: buttons.Length - 1);
        if (c != 0) return T("scm.discardCancelled");

        if (tracked.Count > 0)
        {
            var r = await GitService.RunAllIn(root, GitService.PanelArgs.DiscardTracked(tracked));
            if (!r.Success) return FailureText("scm.op.discard", r);
        }
        int failed = await Task.Run(() =>
        {
            int bad = 0;
            foreach (var full in deletions)
            {
                try
                {
                    if (!File.Exists(full)) continue;
                    bool ok = RecycleOverride?.Invoke(full) ?? Recycle(full);
                    if (!ok) bad++;
                }
                catch { bad++; }   // never fall back to a permanent delete
            }
            return bad;
        });
        return failed == 0 ? T("scm.discarded", total) : T("scm.discardedSome", total - failed, failed);
    }, filesChanged: true);

    private static string RowLetter(List<GitPanelRow> rows, string repoPath) =>
        rows.FirstOrDefault(r => r.Entry.RepoPath == repoPath)?.Letter ?? "M";

    private static string SafeFull(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    /// <summary>
    /// Only a fixed local drive has a Recycle Bin. On a network share, a mapped drive or a USB stick Windows ignores
    /// "send to the Recycle Bin" and deletes for good, without asking — which the dialog would then have promised wrongly.
    /// </summary>
    public static bool HasRecycleBin(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            string? drive = Path.GetPathRoot(full);
            return !string.IsNullOrEmpty(drive) && new DriveInfo(drive).DriveType == DriveType.Fixed;
        }
        catch { return false; }
    }

    /// <summary>To the Recycle Bin, so a discarded new file can still be brought back.</summary>
    private static bool Recycle(string full)
    {
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(full,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        return !File.Exists(full);
    }

    /// <summary>Test seam for the new-branch name (a harness has no dialog).</summary>
    public Func<string?>? AskBranchNameOverride { get; set; }

    private Task NewBranchAsync() => GitOpAsync("scm.busy.branch", "scm.op.newBranch", async (root, _) =>
    {
        string? name = AskBranchNameOverride != null
            ? AskBranchNameOverride()
            : InputDialog.Show(T("scm.newBranchTitle"), T("scm.newBranchPrompt"), T("scm.newBranchPlaceholder"), "",
                okLabel: T("scm.newBranchButton"), cancelLabel: T("ide.cancel"));
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        // git's own rule for branch names (no spaces, "..", "~", a leading '-' …), asked of git itself.
        if (name.StartsWith('-') || !(await GitService.RunIn(root, GitService.PanelArgs.CheckRefFormat(name), 10000)).Success)
            return T("scm.branchInvalid", name);
        var r = await GitService.RunIn(root, GitService.PanelArgs.CreateBranch(name), 30000);
        return r.Success ? T("scm.branchCreated", name) : FailureText("scm.op.newBranch", r);
    });

    private Task StashAsync(bool pop) => GitOpAsync(pop ? "scm.busy.stashPop" : "scm.busy.stash", pop ? "scm.op.stashPop" : "scm.op.stash", async (root, snap) =>
    {
        string op = T(pop ? "scm.op.stashPop" : "scm.op.stash");
        var dirty = DirtyTabNames();
        if (dirty.Count > 0)
        {
            ChoiceDialog.Show(op, T("scm.dirtyBody", op, string.Join("\n", dirty)), new[] { T("ide.ok") });
            return T("scm.dirtyShort", op);
        }
        if (!pop)
        {
            if (!snap.Entries.Any(e => !e.IsUntracked)) return T("scm.stashNothing");
            var r = await GitService.RunIn(root, GitService.PanelArgs.StashPush($"CluadeX {DateTime.Now:yyyy-MM-dd HH:mm}"), 60000);
            return r.Success ? T("scm.stashed") : FailureText("scm.op.stash", r);
        }
        if (await GitService.StashCountAsync(root) == 0) return T("scm.stashEmpty");
        var p = await GitService.RunIn(root, GitService.PanelArgs.StashPop(), 60000);
        // A conflicting pop keeps the stash and leaves conflict markers — say so rather than "failed".
        return p.Success ? T("scm.stashPopped")
            : GitService.ClassifyFailure(p) == GitFailure.Conflict ? T("scm.stashPopConflict") : FailureText("scm.op.stashPop", p);
    }, filesChanged: true);

    private void OpenRepoFolder()
    {
        string root = GitRepoRoot.Length > 0 ? GitRepoRoot : _workspace.HasWorkingDirectory ? _workspace.WorkingDirectory : "";
        if (root.Length == 0 || !Directory.Exists(root)) return;
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(Path.GetFullPath(root));
            using var _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _log?.Warn("Git", "open repo folder failed", ex);
            StatusMessage = T("scm.openFolderFailed");
        }
    }

    private async Task InitRepoAsync()
    {
        if (!_workspace.HasWorkingDirectory || !IsNotGitRepo) return;
        if (!TryBeginGitOp("scm.busy.init")) return;
        string project = _workspace.WorkingDirectory;
        try
        {
            int c = ChoiceDialog.Show(T("scm.initTitle"), T("scm.initConfirm", project), new[] { T("scm.initButton"), T("ide.cancel") }, defaultIndex: 0);
            if (c != 0) return;
            var r = await GitService.RunIn(project, new[] { "init" }, 30000);
            if (SameProject(project)) StatusMessage = r.Success ? T("scm.initDone") : FailureText("scm.op.init", r);
        }
        catch (Exception ex)
        {
            _log?.Warn("Git", "git init failed", ex);
            StatusMessage = T("scm.opFailed", T("scm.op.init"), T("scm.fail.Other"));
        }
        finally
        {
            EndGitOp();
            await RefreshGitAsync();
            _ = _workspace.EnrichGitStatusAsync(Tree);
        }
    }

    // ═══════════════ Commit ═══════════════

    /// <summary>
    /// Commit with the panel's message. When something is STAGED, exactly the staged files are committed and the rest
    /// stays as it is; when nothing is, everything in the opened folder is staged first (`git add .`) — the old
    /// behaviour. Either way the list is shown and confirmed first: it used to commit silently, sweeping in whatever
    /// untracked file happened to be there (a fresh .env included). Secret-looking files get a separate warning and make
    /// Cancel the default. <paramref name="thenPush"/>: Commit &amp; Push — the same dialog says it will push.
    /// </summary>
    private async Task CommitCoreAsync(bool thenPush)
    {
        string msg = CommitMessage?.Trim() ?? "";
        if (string.IsNullOrEmpty(msg)) { StatusMessage = T("ide.commitNeedsMessage"); return; }
        if (IsCommitting || !_workspace.HasWorkingDirectory) return;
        if (!TryBeginGitOp(thenPush ? "scm.busy.commitPush" : "scm.busy.commit")) return;
        if (_draftCts != null) { _draftCancelledByUser = true; _draftCts.Cancel(); }   // a late draft must not refill the box
        IsCommitting = true;
        string project = _workspace.WorkingDirectory;
        try
        {
            var status = await _git.StatusEntriesAsync();
            if (!status.Success) { StatusMessage = T("ide.commitStatusFailed"); return; }
            if (status.Entries.Count == 0) { StatusMessage = T("ide.commitNothing"); return; }
            string root = status.RepoRoot;

            var (staged, changes) = GitService.GroupChanges(status.Entries);
            bool onlyStaged = staged.Count > 0;
            var listed = onlyStaged
                ? staged.Select(r => (Code: r.Letter, r.Entry)).ToList()
                : status.Entries.Select(e => (Code: e.Code, Entry: e)).ToList();

            // Unsaved editor tabs are not on disk — they would not be in the commit.
            var dirty = DirtyTabNames();
            const int maxListed = 25;
            var lines = listed.Take(maxListed).Select(x => $"  {x.Code,-2}  {DisplayPath(x.Entry.FullPath, x.Entry.RepoPath)}").ToList();
            if (listed.Count > maxListed) lines.Add("  " + T("ide.commitMore", listed.Count - maxListed));
            var secrets = listed.Where(x => !x.Entry.IsDeleted && LooksSecret(x.Entry.RepoPath))
                .Select(x => DisplayPath(x.Entry.FullPath, x.Entry.RepoPath)).ToList();

            var body = new StringBuilder();
            body.AppendLine(onlyStaged ? T("scm.commitStagedHeader", staged.Count, GitBranch) : T("ide.commitConfirmHeader", listed.Count, GitBranch));
            body.AppendLine();
            foreach (var l in lines) body.AppendLine(l);
            body.AppendLine();
            body.AppendLine(onlyStaged
                ? (changes.Count > 0 ? T("scm.commitStagedNote", changes.Count) : T("scm.commitStagedOnlyNote"))
                : T("scm.commitAllNote"));
            if (thenPush) body.AppendLine(T("scm.commitThenPushNote"));
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

            string button = thenPush ? T("scm.commitPushButton", listed.Count) : T("ide.commitConfirmButton", listed.Count);
            int choice = ChoiceDialog.Show(T(onlyStaged ? "scm.commitTitle" : "ide.commitConfirmTitle"), body.ToString().TrimEnd(),
                new[] { button, T("ide.cancel") },
                defaultIndex: secrets.Count > 0 ? 1 : 0);   // a secret in the list: Enter must not commit it
            if (choice != 0) { StatusMessage = T(onlyStaged ? "scm.commitCancelledStaged" : "ide.commitCancelled"); return; }

            if (!onlyStaged)
            {
                var add = await GitService.RunIn(project, new[] { "add", "--", "." }, 60000);
                if (!add.Success) { StatusMessage = T("ide.stageFailed", FirstLine(add.Error)); return; }
            }
            var commit = await CommitWithMessageAsync(root, msg);
            if (!commit.Success)
            {
                StatusMessage = T("ide.commitFailed", FirstLine(string.IsNullOrWhiteSpace(commit.Error) ? commit.Output : commit.Error));
                return;
            }
            if (SameProject(project)) CommitMessage = "";
            // "[dev 537804b] update readme" → surface the first line
            string first = FirstLine(commit.Output);
            string done = $"✓ {(first.Length > 0 ? first : T("ide.committed"))}";
            if (thenPush)
            {
                StatusMessage = done + " · " + T("scm.busy.push");
                done += " · " + await PushRepoAsync(root, confirm: false);
            }
            if (SameProject(project)) StatusMessage = done;
        }
        catch (Exception ex)
        {
            _log?.Warn("Git", "Commit failed", ex);
            StatusMessage = T("ide.commitFailed", ex.GetType().Name);
        }
        finally
        {
            IsCommitting = false;
            EndGitOp();
            await RefreshGitAsync();
            _ = _workspace.EnrichGitStatusAsync(Tree);
        }
    }

    /// <summary>`git commit -F tmp` in the repository: the message goes through a file, never the command line.</summary>
    private static async Task<GitResult> CommitWithMessageAsync(string root, string message)
    {
        string tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, message, new UTF8Encoding(false));
            return await GitService.RunIn(root, GitService.PanelArgs.CommitFromFile(tmp), 60000);
        }
        finally { try { File.Delete(tmp); } catch { /* temp */ } }
    }

    // ═══════════════ AI draft of the commit message ═══════════════

    /// <summary>Most diff text one draft sends to the model. A lockfile or a generated file must not become a huge,
    /// costly prompt; past the cap the model gets the file list and a note that the diff was cut.</summary>
    public const int MaxDraftDiffChars = 12_000;
    /// <summary>A changed file over this size is named but its diff is not read at all.</summary>
    private const long MaxDraftFileBytes = 512 * 1024;

    private CancellationTokenSource? _draftCts;
    private bool _draftCancelledByUser;
    private bool _isDraftingCommit;
    public bool IsDraftingCommit { get => _isDraftingCommit; private set { if (SetProperty(ref _isDraftingCommit, value)) OnPropertyChanged(nameof(IsNotDraftingCommit)); } }
    public bool IsNotDraftingCommit => !_isDraftingCommit;

    /// <summary>Test seam: (system, user, ct) → the model's reply, instead of the active provider.</summary>
    public Func<string, string, CancellationToken, Task<string>>? CommitDraftModelOverride { get; set; }
    /// <summary>What the last draft sent (for the harness: proves the cap).</summary>
    public string LastCommitDraftPrompt { get; private set; } = "";

    private const string DraftSystemPrompt =
        "You write git commit messages. Reply with the commit message only: no preamble, no code fences, no quotes. " +
        "Line 1: an imperative summary of at most 72 characters. Then, only if it helps, a blank line and a short body " +
        "(lines wrapped at 72) saying what changed and why. Use only the changes given; do not invent anything.";

    /// <summary>
    /// "✨" — the active AI provider drafts the message from the staged diff (or, with nothing staged, the whole diff).
    /// It only fills the box: it never commits. Cancellable; the diff is capped at <see cref="MaxDraftDiffChars"/>;
    /// secret-looking files are named but their content is never sent; a failure is a localized line.
    /// </summary>
    private async Task DraftCommitMessageAsync()
    {
        if (_draftCts != null || !_workspace.HasWorkingDirectory || !HasGitRepo) return;
        var model = CommitDraftModelOverride;
        if (model == null)
        {
            var provider = _providers?.ActiveProvider;
            if (provider == null || !provider.IsReady)
            {
                StatusMessage = T("scm.aiNotReady", provider?.DisplayName ?? T("scm.aiProvider"));
                return;
            }
            model = (system, user, ct) => provider.GenerateAsync(new List<ChatMessage>(), user, system, ct);
        }
        if (!string.IsNullOrWhiteSpace(CommitMessage))
        {
            int keep = ChoiceDialog.Show(T("scm.aiTitle"), T("scm.aiReplaceBody"), new[] { T("scm.aiReplaceButton"), T("ide.cancel") }, defaultIndex: 1);
            if (keep != 0) return;
        }

        string project = _workspace.WorkingDirectory;
        _draftCancelledByUser = false;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        _draftCts = cts;
        IsDraftingCommit = true;
        StatusMessage = T("scm.aiDrafting");
        try
        {
            var snap = await _git.StatusEntriesAsync();
            if (!snap.Success) { StatusMessage = T("scm.notRepoShort"); return; }
            string? input = await BuildDraftInputAsync(snap, cts.Token);
            if (input == null) { StatusMessage = T("scm.aiNothing"); return; }
            LastCommitDraftPrompt = input;
            string raw = await Task.Run(() => model(DraftSystemPrompt, input, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (_disposed || !SameProject(project)) return;   // another project is open now: the draft is not about it
            string text = CleanCommitDraft(raw);
            if (text.Length == 0) { StatusMessage = T("scm.aiEmpty"); return; }
            CommitMessage = text;
            StatusMessage = T("scm.aiDrafted");
        }
        catch (OperationCanceledException)
        {
            if (SameProject(project)) StatusMessage = T(_draftCancelledByUser ? "scm.aiCancelled" : "scm.aiTimedOut");
        }
        catch (Exception ex)
        {
            _log?.Warn("Git", "commit message draft failed", ex);
            if (SameProject(project)) StatusMessage = T("scm.aiFailed");
        }
        finally
        {
            if (ReferenceEquals(_draftCts, cts)) _draftCts = null;
            cts.Dispose();
            IsDraftingCommit = false;
        }
    }

    /// <summary>The model's input: the file list, then the diff, capped. Null when there is nothing to describe.</summary>
    private async Task<string?> BuildDraftInputAsync(GitStatusSnapshot snap, CancellationToken ct)
    {
        var (staged, changes) = GitService.GroupChanges(snap.Entries);
        bool useStaged = staged.Count > 0;
        var rows = useStaged ? staged : changes;
        if (rows.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine(useStaged ? "Staged changes (this is what will be committed):" : "Changes (nothing is staged; all of them will be committed):");
        var diffPaths = new List<string>();
        foreach (var row in rows)
        {
            string note = "";
            if (LooksSecret(row.Entry.RepoPath)) note = " (looks like a secret: content withheld)";
            else if (row.Kind == GitChangeKind.Untracked) note = " (new file)";
            else if (row.Kind is GitChangeKind.Conflict) note = " (merge conflict)";
            else if (File.Exists(row.Entry.FullPath) && new FileInfo(row.Entry.FullPath).Length > MaxDraftFileBytes) note = " (large file: diff omitted)";
            else diffPaths.Add(row.Entry.RepoPath);
            sb.AppendLine($"{row.Letter} {row.Entry.RepoPath}{note}");
        }
        if (diffPaths.Count > 0 && (useStaged || !snap.NoCommits))
        {
            sb.AppendLine();
            sb.AppendLine("Diff:");
            int budget = MaxDraftDiffChars;
            foreach (var call in GitService.PanelArgs.DiffForDraft(useStaged, diffPaths))
            {
                ct.ThrowIfCancellationRequested();
                var r = await GitService.RunIn(snap.RepoRoot, call, 30000, ct);
                if (!r.Success) continue;
                string d = r.Output ?? "";
                if (d.Length > budget)
                {
                    sb.Append(d.AsSpan(0, Math.Max(0, budget)));
                    sb.AppendLine();
                    sb.AppendLine("[diff cut here — too long]");
                    budget = 0;
                    break;
                }
                sb.Append(d);
                budget -= d.Length;
            }
        }
        return sb.ToString();
    }

    /// <summary>The model's reply as a commit message: fences, quotes and a "Commit message:" label removed, blank-line
    /// runs collapsed, at most 2,000 characters. Pure.</summary>
    public static string CleanCommitDraft(string? raw)
    {
        string t = (raw ?? "").Replace("\r\n", "\n").Trim();
        var lines = t.Split('\n').ToList();
        lines.RemoveAll(l => l.TrimStart().StartsWith("```", StringComparison.Ordinal));
        t = string.Join("\n", lines).Trim();
        foreach (var label in new[] { "commit message:", "commit:" })
            if (t.StartsWith(label, StringComparison.OrdinalIgnoreCase)) t = t[label.Length..].TrimStart();
        if (t.Length >= 2 && (t[0] == '"' && t[^1] == '"' || t[0] == '\'' && t[^1] == '\'' || t[0] == '`' && t[^1] == '`')) t = t[1..^1].Trim();
        while (t.Contains("\n\n\n")) t = t.Replace("\n\n\n", "\n\n");
        if (t.Length > 2000) t = t[..2000].TrimEnd();
        return t;
    }
}
