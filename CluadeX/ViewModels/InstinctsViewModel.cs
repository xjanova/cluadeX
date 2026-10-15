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
    private readonly SkillService _skillService;
    private readonly LocalizationService _loc;
    private readonly DebugLogService _log;
    private readonly System.Windows.Threading.DispatcherTimer _brainPoll;
    private bool _lastBrainAvailable;
    private string _lastBrainText = "";

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

    public InstinctsViewModel(InstinctService service, BrainSyncService brainSync, SkillService skillService,
        LocalizationService loc, DebugLogService log)
    {
        _service = service;
        _brainSync = brainSync;
        _skillService = skillService;
        _loc = loc;
        _log = log;

        // The BRAIN/OFFLINE chip and the brain-sync buttons read expression-bodied pass-throughs
        // (no backing field), so they latched to whatever was true when the page was first built —
        // starting the brain server afterwards left the UI saying OFFLINE forever. BrainSyncService
        // exposes no change event and its check is a cheap in-memory lookup, so poll it.
        _brainPoll = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _brainPoll.Tick += (_, _) =>
        {
            bool nowAvailable = _brainSync.IsBrainAvailable;
            string nowText = _brainSync.StatusText;
            if (nowAvailable == _lastBrainAvailable && nowText == _lastBrainText) return;
            _lastBrainAvailable = nowAvailable;
            _lastBrainText = nowText;
            OnPropertyChanged(nameof(BrainAvailable));
            OnPropertyChanged(nameof(BrainStatusText));
        };
        _brainPoll.Start();
        RefreshCommand = new RelayCommand(() => Refresh());
        SelectCommand = new RelayCommand<Instinct>(i => { if (i != null) Selected = i; });

        // Every store mutation is file I/O (instincts.json) — a locked or read-only file used to
        // throw straight out of a button handler. Each command now reports a localized failure and
        // logs the details.
        AcceptCommand = new RelayCommand(() =>
        {
            var sel = _selected;
            if (sel == null) return;
            string id = sel.Id, label = Trim(sel.Pattern);
            if (!Try("accept", () => _service.Accept(id))) return;
            Refresh(preserveSelection: id);
            var live = Instincts.FirstOrDefault(i => i.Id == id);
            StatusMessage = string.Format(T("inst.accepted"), label, (live ?? sel).Confidence.ToString("P0"));
        });
        RejectCommand = new RelayCommand(() =>
        {
            var sel = _selected;
            if (sel == null) return;
            string id = sel.Id, label = Trim(sel.Pattern);
            if (!Try("reject", () => _service.Reject(id))) return;
            Refresh(preserveSelection: id);
            var live = Instincts.FirstOrDefault(i => i.Id == id);
            StatusMessage = string.Format(T("inst.rejected"), label, (live ?? sel).Confidence.ToString("P0"));
        });
        DeleteCommand = new RelayCommand(() =>
        {
            var sel = _selected;
            if (sel == null) return;
            var snip = Trim(sel.Pattern);
            // Permanent: the instinct, its votes and its observation history are gone from the store.
            var confirm = System.Windows.MessageBox.Show(
                string.Format(T("inst.deleteConfirm"), sel.Pattern),
                T("inst.deleteTitle"),
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            if (!Try("delete", () => _service.Delete(sel.Id))) return;
            Selected = null;
            Refresh();
            StatusMessage = string.Format(T("inst.deleted"), snip);
        });
        PromoteCommand = new RelayCommand(() =>
        {
            var sel = _selected;
            if (sel == null) return;
            string id = sel.Id;
            string? path = null;
            if (!Try("promote", () => path = _service.PromoteToSkill(id))) return;
            if (path != null)
            {
                // The skill file is on disk, but SkillService caches its list forever — without
                // this the new skill stayed invisible to the agent and to /-completion until restart.
                _skillService.ReloadSkills();
                StatusMessage = string.Format(T("inst.promoted"), Path.GetFileName(path));
                Refresh(preserveSelection: id);
            }
            else
            {
                StatusMessage = T("inst.promoteRefused");
            }
        });
        AddCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(_newPattern))
            {
                StatusMessage = T("inst.patternRequired");
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
            Instinct? added = null;
            if (!Try("add", () => added = _service.Add(inst)) || added == null) return;
            NewPattern = NewTrigger = NewAction = NewTags = "";
            Refresh(preserveSelection: added.Id);
            StatusMessage = string.Format(T("inst.added"), Trim(added.Pattern));
        });
        ExportCommand = new RelayCommand(() =>
        {
            var dlg = new SaveFileDialog
            {
                Title = T("inst.exportTitle"),
                Filter = T("inst.jsonFilter"),
                FileName = $"instincts-{DateTime.Now:yyyyMMdd}.json",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                _service.ExportTo(dlg.FileName);
                StatusMessage = string.Format(T("inst.exported"), Instincts.Count, dlg.FileName);
            }
            catch (Exception ex)
            {
                _log.Warn("Instincts", "Export failed", ex);
                StatusMessage = T("inst.exportFailed");
            }
        });
        ImportCommand = new RelayCommand(() =>
        {
            var dlg = new OpenFileDialog
            {
                Title = T("inst.importTitle"),
                Filter = T("inst.jsonFilter"),
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                int n = _service.ImportFrom(dlg.FileName);
                Refresh();
                StatusMessage = n == 0 ? T("inst.importNone") : string.Format(T("inst.imported"), n);
            }
            catch (Exception ex)
            {
                // Usually a file that isn't an instincts export (JsonException) — say so plainly.
                _log.Warn("Instincts", "Import failed", ex);
                StatusMessage = T("inst.importFailed");
            }
        });
        OpenStoreFolderCommand = new RelayCommand(() =>
        {
            try
            {
                var dir = Path.GetDirectoryName(StorePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    // A folder path: the shell opens it in Explorer (it can't "run" a directory).
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = dir, UseShellExecute = true,
                    });
                    StatusMessage = string.Format(T("inst.openedFolder"), dir);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("Instincts", "Open store folder failed", ex);
                StatusMessage = T("inst.openFolderFailed");
            }
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
            StatusMessage = string.Format(T("inst.syncing"), label);

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
                    if (!Try("persist", () => _service.Persist(live))) return;
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
            StatusMessage = T("inst.syncingAll");

            // SNAPSHOT, not the live collection: the sync awaits between instincts, and any store
            // change meanwhile (an Accept, the Stop hook recording an observation) fires Changed →
            // Refresh → Instincts.Clear(). Enumerating the live ObservableCollection then threw
            // "Collection was modified", or re-added items and synced them twice.
            var batch = Instincts.ToList();
            var beforeIds = batch.Where(i => i.SyncedToBrain).Select(i => i.Id).ToHashSet();

            var (synced, skipped, failed) = await _brainSync.SyncAllStrongAsync(batch);

            // Only persist instincts that became synced during THIS call
            foreach (var i in batch)
            {
                if (i.SyncedToBrain && !beforeIds.Contains(i.Id))
                    Try("persist", () => _service.Persist(i));
            }
            StatusMessage = string.Format(T("inst.syncAllResult"), synced, skipped, failed);
            Refresh(preserveSelection: _selected?.Id);
        });

        Refresh();
        // BeginInvoke, not Invoke: a blocking hop from whatever thread saved an
        // instinct is half of the deadlock documented in
        // InstinctService.RaiseChangedDetached — the writer would wait on the UI
        // thread while the UI thread waited on the writer's lock. The service
        // now detaches the event too, so this is belt-and-braces: no future
        // subscriber can re-create the cycle from this side.
        _service.Changed += () =>
            App.Current?.Dispatcher.BeginInvoke(() => Refresh(preserveSelection: _selected?.Id));
    }

    private string T(string key) => _loc.T(key);

    /// <summary>Run a store mutation; on failure log the details and show a localized line.</summary>
    private bool Try(string what, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("Instincts", $"Instinct {what} failed", ex);
            StatusMessage = T("inst.saveFailed");
            return false;
        }
    }

    public void Refresh(string? preserveSelection = null)
    {
        Instincts.Clear();
        List<Instinct> all;
        try { all = _service.GetAll(); }
        catch (Exception ex)
        {
            _log.Warn("Instincts", "Loading instincts failed", ex);
            all = new List<Instinct>();
        }
        foreach (var i in all) Instincts.Add(i);
        if (preserveSelection != null)
            Selected = Instincts.FirstOrDefault(i => i.Id == preserveSelection) ?? Instincts.FirstOrDefault();
        else
            Selected = _selected == null ? Instincts.FirstOrDefault()
                : Instincts.FirstOrDefault(i => i.Id == _selected.Id) ?? Instincts.FirstOrDefault();

        OnPropertyChanged(nameof(StrongCount));
        OnPropertyChanged(nameof(EmergingCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CanPromoteSelected));
        if (string.IsNullOrEmpty(_statusMessage))
            StatusMessage = TotalCount == 0
                ? T("inst.emptyStore")
                : string.Format(T("inst.summary"), TotalCount, StrongCount, EmergingCount);
    }

    private static string Trim(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
