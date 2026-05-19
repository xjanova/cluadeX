using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using Microsoft.Win32;

namespace CluadeX.ViewModels;

/// <summary>
/// View model for the Instincts page — browse patterns the agent has
/// observed, vote on them (accept/reject), promote strong ones to skills,
/// add new ones manually, and export/import the JSON store.
/// </summary>
public class InstinctsViewModel : ViewModelBase
{
    private readonly InstinctService _service;
    private readonly BrainSyncService _brainSync;

    public ObservableCollection<Instinct> Instincts { get; } = new();

    private Instinct? _selected;
    public Instinct? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(CanPromoteSelected));
            }
        }
    }

    public bool HasSelection => _selected != null;
    public bool CanPromoteSelected => _selected?.CanPromote ?? false;

    // ─── Add-new form fields (bound to the inline composer) ──────────
    private string _newPattern = "";
    public string NewPattern { get => _newPattern; set => SetProperty(ref _newPattern, value); }

    private string _newTrigger = "";
    public string NewTrigger { get => _newTrigger; set => SetProperty(ref _newTrigger, value); }

    private string _newAction = "";
    public string NewAction { get => _newAction; set => SetProperty(ref _newAction, value); }

    private string _newTags = "";
    public string NewTags { get => _newTags; set => SetProperty(ref _newTags, value); }

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public int StrongCount => Instincts.Count(i => i.ConfidenceTier == "STRONG");
    public int EmergingCount => Instincts.Count(i => i.ConfidenceTier == "EMERGING");
    public int TotalCount => Instincts.Count;
    public string StorePath => _service.StorePath;

    // ─── Commands ────────────────────────────────────────────────────
    public ICommand RefreshCommand { get; }
    public ICommand SelectCommand { get; }
    public ICommand AcceptCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand PromoteCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand OpenStoreFolderCommand { get; }
    public ICommand SyncToBrainCommand { get; }
    public ICommand SyncAllStrongCommand { get; }

    // ─── Brain bridge state (UI bindings) ────────────────────────────
    public bool BrainAvailable => _brainSync.IsBrainAvailable;
    public string BrainStatusText => _brainSync.StatusText;

    public InstinctsViewModel(InstinctService service, BrainSyncService brainSync)
    {
        _service = service;
        _brainSync = brainSync;
        RefreshCommand = new RelayCommand(() => Refresh());
        SelectCommand = new RelayCommand<Instinct>(i => { if (i != null) Selected = i; });
        AcceptCommand = new RelayCommand(() =>
        {
            if (_selected == null) return;
            _service.Accept(_selected.Id);
            StatusMessage = $"Accepted '{Trim(_selected.Pattern)}' → confidence {_selected.Confidence:P0}";
            Refresh(preserveSelection: _selected.Id);
        });
        RejectCommand = new RelayCommand(() =>
        {
            if (_selected == null) return;
            _service.Reject(_selected.Id);
            StatusMessage = $"Rejected '{Trim(_selected.Pattern)}' → confidence {_selected.Confidence:P0}";
            Refresh(preserveSelection: _selected.Id);
        });
        DeleteCommand = new RelayCommand(() =>
        {
            if (_selected == null) return;
            var snip = Trim(_selected.Pattern);
            _service.Delete(_selected.Id);
            Selected = null;
            Refresh();
            StatusMessage = $"Deleted '{snip}'";
        });
        PromoteCommand = new RelayCommand(() =>
        {
            if (_selected == null) return;
            var path = _service.PromoteToSkill(_selected.Id);
            if (path != null)
            {
                StatusMessage = $"Promoted → {Path.GetFileName(path)}";
                Refresh(preserveSelection: _selected.Id);
            }
            else
            {
                StatusMessage = "Promote failed (confidence too low or already promoted)";
            }
        });
        AddCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(_newPattern))
            {
                StatusMessage = "Pattern is required";
                return;
            }
            var inst = new Instinct
            {
                Pattern = _newPattern.Trim(),
                Trigger = _newTrigger.Trim(),
                Action = _newAction.Trim(),
                Tags = _newTags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                              .Select(t => t.Trim()).Where(t => t.Length > 0).ToList(),
            };
            var added = _service.Add(inst);
            NewPattern = NewTrigger = NewAction = NewTags = "";
            Refresh(preserveSelection: added.Id);
            StatusMessage = $"Added '{Trim(added.Pattern)}'";
        });
        ExportCommand = new RelayCommand(() =>
        {
            var dlg = new SaveFileDialog
            {
                Title = "Export instincts to JSON",
                Filter = "JSON file|*.json",
                FileName = $"instincts-{DateTime.Now:yyyyMMdd}.json",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                _service.ExportTo(dlg.FileName);
                StatusMessage = $"Exported {Instincts.Count} instincts → {dlg.FileName}";
            }
            catch (Exception ex) { StatusMessage = $"Export failed: {ex.Message}"; }
        });
        ImportCommand = new RelayCommand(() =>
        {
            var dlg = new OpenFileDialog
            {
                Title = "Import instincts from JSON",
                Filter = "JSON file|*.json",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                int n = _service.ImportFrom(dlg.FileName);
                Refresh();
                StatusMessage = n == 0 ? "Nothing imported (duplicates skipped)" : $"Imported {n} new instinct(s)";
            }
            catch (Exception ex) { StatusMessage = $"Import failed: {ex.Message}"; }
        });
        OpenStoreFolderCommand = new RelayCommand(() =>
        {
            try
            {
                var dir = Path.GetDirectoryName(StorePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = dir, UseShellExecute = true,
                    });
                    StatusMessage = $"Opened {dir}";
                }
            }
            catch (Exception ex) { StatusMessage = $"Open folder failed: {ex.Message}"; }
        });
        SyncToBrainCommand = new AsyncRelayCommand(async () =>
        {
            // FIX (audit HIGH #6): capture id + pattern BEFORE the await so a
            // background Refresh that swaps out the Selected reference doesn't
            // cause us to persist the BrainNoteId onto a discarded instance.
            var selected = _selected;
            if (selected == null) return;
            string id = selected.Id;
            string label = Trim(selected.Pattern);
            StatusMessage = $"Syncing '{label}' to brain...";

            var result = await _brainSync.SyncInstinctAsync(selected);

            // Re-fetch the canonical instinct from the store and copy the id
            // back onto it before persisting (Refresh may have swapped the
            // in-memory reference while the await was suspended).
            if (result.Success && !string.IsNullOrEmpty(result.NoteId))
            {
                var live = _service.GetById(id);
                if (live != null)
                {
                    live.BrainNoteId = result.NoteId;
                    live.LastSeen = DateTime.UtcNow;
                    _service.Persist(live);
                }
                StatusMessage = $"✓ {result.Message}";
                Refresh(preserveSelection: id);
            }
            else
            {
                StatusMessage = $"⚠ {result.Message}";
            }
        });
        SyncAllStrongCommand = new AsyncRelayCommand(async () =>
        {
            // FIX (audit CRITICAL #2): SyncAllStrongAsync only mutates
            // BrainNoteId on instincts it actually pushed this run. We must
            // not persist EVERY SyncedToBrain instinct (which would include
            // previously-synced ones with stale in-memory state). Persist
            // only the entries the sync call modified during this batch.
            StatusMessage = "Syncing all STRONG instincts to brain...";

            // Snapshot before sync so we can diff afterward
            var beforeIds = Instincts.Where(i => i.SyncedToBrain).Select(i => i.Id).ToHashSet();

            var (synced, skipped, failed) = await _brainSync.SyncAllStrongAsync(Instincts);

            // Only persist instincts that became synced during THIS call
            foreach (var i in Instincts)
            {
                if (i.SyncedToBrain && !beforeIds.Contains(i.Id))
                    _service.Persist(i);
            }
            StatusMessage = $"Brain sync: {synced} synced · {skipped} skipped · {failed} failed";
            Refresh(preserveSelection: _selected?.Id);
        });

        Refresh();
        _service.Changed += () =>
            App.Current?.Dispatcher.Invoke(() => Refresh(preserveSelection: _selected?.Id));
    }

    public void Refresh(string? preserveSelection = null)
    {
        Instincts.Clear();
        foreach (var i in _service.GetAll()) Instincts.Add(i);
        if (preserveSelection != null)
            Selected = Instincts.FirstOrDefault(i => i.Id == preserveSelection) ?? Instincts.FirstOrDefault();
        else
            Selected ??= Instincts.FirstOrDefault();

        OnPropertyChanged(nameof(StrongCount));
        OnPropertyChanged(nameof(EmergingCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CanPromoteSelected));
        if (string.IsNullOrEmpty(_statusMessage))
            StatusMessage = $"{TotalCount} instincts · {StrongCount} strong · {EmergingCount} emerging";
    }

    private static string Trim(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
