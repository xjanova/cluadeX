using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// View model for the Time Machine page — commit timeline with rewind,
/// branch-from, and cherry-pick. Uses TimeMachineService for all git work.
/// </summary>
public class TimeMachineViewModel : ViewModelBase
{
    private readonly TimeMachineService _tm;
    private readonly LocalizationService _loc;
    private readonly DebugLogService? _log;

    // One token per selection: clicking through commits quickly used to let an older commit's file list
    // (still loading) land after a newer one's, mixing two commits in the Changes panel.
    private CancellationTokenSource? _filesCts;
    private CancellationTokenSource? _diffCts;

    // ─── Collections shown in the timeline / detail panel ───
    public ObservableCollection<CommitInfo> Commits { get; } = new();
    public ObservableCollection<CommitInfo> VisibleCommits { get; } = new();
    public ObservableCollection<CommitFileChange> Files { get; } = new();
    public ObservableCollection<DiffLine> DiffLines { get; } = new();

    private CommitInfo? _selected;
    public CommitInfo? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedShortSha));
                OnPropertyChanged(nameof(SelectedMessage));
                OnPropertyChanged(nameof(SelectedAuthor));
                OnPropertyChanged(nameof(SelectedTime));
                OnPropertyChanged(nameof(SelectedBranch));
                OnPropertyChanged(nameof(SelectedFilesCount));
                OnPropertyChanged(nameof(SelectedAdditions));
                OnPropertyChanged(nameof(SelectedDeletions));
                OnPropertyChanged(nameof(SelectedIsAi));
                _ = LoadSelectedFilesAsync();
            }
        }
    }

    public bool HasSelection => _selected != null;
    public string SelectedShortSha => _selected?.ShortSha ?? "";
    public string SelectedMessage => _selected?.Message ?? "";
    public string SelectedAuthor => _selected?.Author ?? "";
    public string SelectedTime => _selected == null ? "" : RelativeTime(_selected.Date);
    public string SelectedBranch => _selected?.Branch ?? "";
    public int SelectedFilesCount => _selected?.FilesChanged ?? 0;
    public int SelectedAdditions => _selected?.Additions ?? 0;
    public int SelectedDeletions => _selected?.Deletions ?? 0;
    public bool SelectedIsAi => _selected?.IsAiAuthored ?? false;

    private CommitFileChange? _selectedFile;
    public CommitFileChange? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value))
            {
                _ = LoadSelectedFileDiffAsync();
            }
        }
    }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }

    private bool _isRewinding;
    public bool IsRewinding { get => _isRewinding; set => SetProperty(ref _isRewinding, value); }

    private bool _isConfirmOpen;
    public bool IsConfirmOpen { get => _isConfirmOpen; set => SetProperty(ref _isConfirmOpen, value); }

    private bool _createSnapshot = true;
    public bool CreateSnapshot { get => _createSnapshot; set => SetProperty(ref _createSnapshot, value); }

    private bool _filterAgentOnly;
    public bool FilterAgentOnly
    {
        get => _filterAgentOnly;
        set
        {
            if (SetProperty(ref _filterAgentOnly, value))
            {
                RebuildVisibleCommits();
                OnPropertyChanged(nameof(FilterAllChecked));
                OnPropertyChanged(nameof(FilterAgentChecked));
            }
        }
    }
    public bool FilterAllChecked => !_filterAgentOnly;
    public bool FilterAgentChecked => _filterAgentOnly;

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    private string _currentBranchName = "";
    public string CurrentBranchName { get => _currentBranchName; set => SetProperty(ref _currentBranchName, value); }

    public int CommitCount => Commits.Count;
    public int VisibleCount => VisibleCommits.Count;

    // ─── Commands ───
    public ICommand RefreshCommand { get; }
    public ICommand SelectCommitCommand { get; }
    public ICommand SelectFileCommand { get; }
    public ICommand OpenConfirmCommand { get; }
    public ICommand CancelConfirmCommand { get; }
    public ICommand ConfirmRewindCommand { get; }
    public ICommand BranchFromCommand { get; }
    public ICommand CherryPickCommand { get; }
    public ICommand ShowAllCommand { get; }
    public ICommand ShowAgentOnlyCommand { get; }

    public TimeMachineViewModel(TimeMachineService tm, LocalizationService loc, DebugLogService? log = null)
    {
        _tm = tm;
        _loc = loc;
        _log = log;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync());
        SelectCommitCommand = new RelayCommand<CommitInfo>(c => { if (c != null) Selected = c; });
        SelectFileCommand = new RelayCommand<CommitFileChange>(f => { if (f != null) SelectedFile = f; });
        OpenConfirmCommand = new RelayCommand(() =>
        {
            if (_selected != null) IsConfirmOpen = true;
        });
        CancelConfirmCommand = new RelayCommand(() => IsConfirmOpen = false);
        ConfirmRewindCommand = new AsyncRelayCommand(DoRewindAsync);
        BranchFromCommand = new AsyncRelayCommand(DoBranchFromAsync);
        CherryPickCommand = new AsyncRelayCommand(DoCherryPickAsync);
        ShowAllCommand = new RelayCommand(() => FilterAgentOnly = false);
        ShowAgentOnlyCommand = new RelayCommand(() => FilterAgentOnly = true);
        _loc.LanguageChanged += () => OnPropertyChanged(nameof(SelectedTime));
    }

    private string RelativeTime(DateTimeOffset date) => FormatRelativeTime(date, key => _loc.T(key));

    /// <summary>"just now" / "5m ago" / "yesterday" / a date — in the UI language (CommitInfo.RelativeTime is English-only).</summary>
    public static string FormatRelativeTime(DateTimeOffset date, Func<string, string> text)
    {
        var diff = DateTimeOffset.Now - date;
        if (diff.TotalSeconds < 60) return text("tm.justNow");
        if (diff.TotalMinutes < 60) return string.Format(text("tm.minutesAgo"), (int)diff.TotalMinutes);
        if (diff.TotalHours < 24) return string.Format(text("tm.hoursAgo"), (int)diff.TotalHours);
        if (diff.TotalDays < 2) return text("tm.yesterday");
        if (diff.TotalDays < 14) return string.Format(text("tm.daysAgo"), (int)diff.TotalDays);
        // Gregorian with the UI language's month names (th-TH alone would switch to the Buddhist-era year).
        CultureInfo culture;
        try
        {
            culture = (CultureInfo)CultureInfo.GetCultureInfo(text("tm.dateCulture")).Clone();
            culture.DateTimeFormat.Calendar = new GregorianCalendar();
        }
        catch { culture = CultureInfo.InvariantCulture; }
        return date.ToLocalTime().ToString("d MMM yyyy", culture);
    }

    // ═══════════════════════════════════════════
    // Load
    // ═══════════════════════════════════════════

    /// <summary>
    /// Reload the timeline. <paramref name="keepStatus"/> keeps the current status line: after a rewind it
    /// carries the snapshot branch and stash names — the only way back — and the reload used to wipe them.
    /// </summary>
    public async Task LoadAsync(bool keepStatus = false)
    {
        IsLoading = true;
        if (!keepStatus) StatusMessage = "";
        try
        {
            if (!await _tm.IsGitRepoAsync())
            {
                StatusMessage = _loc.T("tm.notRepoHint");
                Commits.Clear();
                VisibleCommits.Clear();
                Files.Clear();
                DiffLines.Clear();
                return;
            }

            CurrentBranchName = await _tm.GetCurrentBranchAsync();

            var loaded = await Task.Run(() => _tm.ListCommitsAsync(200));
            Commits.Clear();
            foreach (var c in loaded) Commits.Add(c);
            RebuildVisibleCommits();
            OnPropertyChanged(nameof(CommitCount));

            // auto-select HEAD (most recent commit)
            Selected = Commits.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _log?.Error("TimeMachine", "Loading commits failed", ex);
            StatusMessage = _loc.T("tm.loadFailed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RebuildVisibleCommits()
    {
        VisibleCommits.Clear();
        var src = _filterAgentOnly ? Commits.Where(c => c.IsAiAuthored) : Commits;
        foreach (var c in src) VisibleCommits.Add(c);
        OnPropertyChanged(nameof(VisibleCount));
    }

    private async Task LoadSelectedFilesAsync()
    {
        _filesCts?.Cancel();
        var cts = new CancellationTokenSource();
        _filesCts = cts;
        Files.Clear();
        DiffLines.Clear();
        var commit = _selected;
        if (commit == null) return;
        try
        {
            var files = await Task.Run(() => _tm.GetCommitFilesAsync(commit.Sha, cts.Token), cts.Token);
            // A newer selection superseded this one while git was running — drop the stale list.
            if (cts.IsCancellationRequested || !ReferenceEquals(commit, _selected)) return;
            foreach (var f in files) Files.Add(f);
            SelectedFile = Files.FirstOrDefault();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log?.Error("TimeMachine", $"Listing files of {commit.Sha} failed", ex);
            StatusMessage = _loc.T("tm.filesFailed");
        }
    }

    private async Task LoadSelectedFileDiffAsync()
    {
        _diffCts?.Cancel();
        var cts = new CancellationTokenSource();
        _diffCts = cts;
        DiffLines.Clear();
        var commit = _selected;
        var file = _selectedFile;
        if (commit == null || file == null) return;
        try
        {
            var lines = await Task.Run(() => _tm.GetFileDiffAsync(commit.Sha, file.Path, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(commit, _selected) || !ReferenceEquals(file, _selectedFile)) return;
            foreach (var l in lines) DiffLines.Add(l);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log?.Error("TimeMachine", $"Diff of {file.Path} at {commit.Sha} failed", ex);
            StatusMessage = _loc.T("tm.diffFailed");
        }
    }

    // ═══════════════════════════════════════════
    // Mutating actions
    // ═══════════════════════════════════════════

    private async Task DoRewindAsync()
    {
        var target = _selected;
        if (target == null) return;
        IsConfirmOpen = false;
        IsRewinding = true;
        StatusMessage = _loc.T("tm.rewindingStatus");
        try
        {
            var result = await Task.Run(() => _tm.RewindToCommitAsync(target.Sha, _createSnapshot));
            if (result.Success) await LoadAsync(keepStatus: true);
            // Set AFTER the reload so the snapshot branch / stash name stay on screen.
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            _log?.Error("TimeMachine", $"Rewind to {target.Sha} threw", ex);
            StatusMessage = _loc.T("tm.rewindFailed");
        }
        finally
        {
            // brief delay so the overlay animation completes nicely
            await Task.Delay(900);
            IsRewinding = false;
        }
    }

    private async Task DoBranchFromAsync()
    {
        var target = _selected;
        if (target == null) return;
        string newBranch = $"from-{target.ShortSha}-{DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture)}";
        StatusMessage = _loc.T("tm.creatingBranch", newBranch);
        try
        {
            var result = await Task.Run(() => _tm.BranchFromCommitAsync(target.Sha, newBranch));
            if (result.Success) await LoadAsync(keepStatus: true);
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            _log?.Error("TimeMachine", $"Branch from {target.Sha} threw", ex);
            StatusMessage = _loc.T("tm.branchFailed", newBranch);
        }
    }

    private async Task DoCherryPickAsync()
    {
        var target = _selected;
        if (target == null) return;
        StatusMessage = _loc.T("tm.cherryPicking");
        try
        {
            var result = await Task.Run(() => _tm.CherryPickCommitAsync(target.Sha));
            if (result.Success) await LoadAsync(keepStatus: true);
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            _log?.Error("TimeMachine", $"Cherry-pick of {target.Sha} threw", ex);
            StatusMessage = _loc.T("tm.cherryFailed");
        }
    }
}

/// <summary>Commit date → localized relative time for the timeline rows (rebuilt on every page load).</summary>
public sealed class CommitRelativeTimeConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTimeOffset d ? TimeMachineViewModel.FormatRelativeTime(d, k => LocalizedResources.Instance[k]) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
