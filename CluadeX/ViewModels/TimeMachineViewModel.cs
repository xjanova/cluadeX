using System.Collections.ObjectModel;
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
    public string SelectedTime => _selected?.RelativeTime ?? "";
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

    public TimeMachineViewModel(TimeMachineService tm)
    {
        _tm = tm;
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
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
    }

    // ═══════════════════════════════════════════
    // Load
    // ═══════════════════════════════════════════

    public async Task LoadAsync()
    {
        IsLoading = true;
        StatusMessage = "";
        try
        {
            if (!await _tm.IsGitRepoAsync())
            {
                StatusMessage = "Not a git repository — open a project folder to see commit history.";
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
            StatusMessage = $"Failed to load commits: {ex.Message}";
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
        Files.Clear();
        DiffLines.Clear();
        if (_selected == null) return;
        try
        {
            var files = await Task.Run(() => _tm.GetCommitFilesAsync(_selected.Sha));
            foreach (var f in files) Files.Add(f);
            SelectedFile = Files.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Diff load failed: {ex.Message}";
        }
    }

    private async Task LoadSelectedFileDiffAsync()
    {
        DiffLines.Clear();
        if (_selected == null || _selectedFile == null) return;
        try
        {
            var lines = await Task.Run(() => _tm.GetFileDiffAsync(_selected.Sha, _selectedFile.Path));
            foreach (var l in lines) DiffLines.Add(l);
        }
        catch (Exception ex)
        {
            StatusMessage = $"File diff failed: {ex.Message}";
        }
    }

    // ═══════════════════════════════════════════
    // Mutating actions
    // ═══════════════════════════════════════════

    private async Task DoRewindAsync()
    {
        if (_selected == null) return;
        IsConfirmOpen = false;
        IsRewinding = true;
        StatusMessage = "Rewinding...";
        try
        {
            var result = await Task.Run(() => _tm.RewindToCommitAsync(_selected.Sha, _createSnapshot));
            StatusMessage = result.Message;
            if (result.Success) await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Rewind failed: {ex.Message}";
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
        if (_selected == null) return;
        string newBranch = $"from-{_selected.ShortSha}-{DateTime.Now:HHmmss}";
        StatusMessage = $"Creating {newBranch}...";
        try
        {
            var result = await Task.Run(() => _tm.BranchFromCommitAsync(_selected.Sha, newBranch));
            StatusMessage = result.Message;
            if (result.Success) await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Branch failed: {ex.Message}";
        }
    }

    private async Task DoCherryPickAsync()
    {
        if (_selected == null) return;
        StatusMessage = "Cherry-picking...";
        try
        {
            var result = await Task.Run(() => _tm.CherryPickCommitAsync(_selected.Sha));
            StatusMessage = result.Message;
            if (result.Success) await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Cherry-pick failed: {ex.Message}";
        }
    }
}
