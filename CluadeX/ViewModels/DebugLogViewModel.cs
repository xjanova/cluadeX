using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using Microsoft.Win32;

namespace CluadeX.ViewModels;

/// <summary>
/// View model for the Debug Log page — live tail of the in-memory ring
/// buffer + level/category filter + copy-all-to-clipboard for bug reports.
/// </summary>
public class DebugLogViewModel : ViewModelBase
{
    private readonly DebugLogService _log;
    private readonly LocalizationService _loc;

    /// <summary>The visible (filtered) entries. UI binds here.</summary>
    public ObservableCollection<LogEntry> Entries { get; } = new();

    /// <summary>Level choices: the value is the LogLevel name (what the filter compares), the label
    /// is localized. They used to be English display strings compared as text.</summary>
    public ObservableCollection<FilterChoice> LevelChoices { get; } = new();

    /// <summary>"" = all categories. Grown incrementally as categories appear — the old getter
    /// returned a brand-new list on every rebuild, so the ComboBox lost its selection and wrote
    /// null back, silently resetting the category filter after every keystroke in the text filter.</summary>
    public ObservableCollection<FilterChoice> CategoryChoices { get; } = new();

    private readonly HashSet<string> _categoriesSeen = new(StringComparer.OrdinalIgnoreCase);

    private LogLevel _filterMinLevel = LogLevel.Debug;
    public LogLevel FilterMinLevel
    {
        get => _filterMinLevel;
        set
        {
            if (SetProperty(ref _filterMinLevel, value))
            {
                OnPropertyChanged(nameof(SelectedLevelName));
                Rebuild();
            }
        }
    }

    public string? SelectedLevelName
    {
        get => _filterMinLevel.ToString();
        set
        {
            if (Enum.TryParse<LogLevel>(value, true, out var l)) FilterMinLevel = l;
        }
    }

    private string _filterCategory = "";
    public string? FilterCategory
    {
        get => _filterCategory;
        set
        {
            string v = value ?? "";   // null = the ComboBox lost its item, which means "all"
            if (_filterCategory == v) return;
            _filterCategory = v;
            OnPropertyChanged();
            Rebuild();
        }
    }

    private string _filterText = "";
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value)) Rebuild();
        }
    }

    private bool _autoScroll = true;
    public bool AutoScroll { get => _autoScroll; set => SetProperty(ref _autoScroll, value); }

    public int WarningCount => _log.WarningCount;
    public int ErrorCount => _log.ErrorCount;
    public int CriticalCount => _log.CriticalCount;
    public int TotalCount => Entries.Count;

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public string LogDirectory => _log.LogDirectory;

    // ─── Log files: how long they stay, detailed logging (owner 2026-11-09: "ผู้ใช้เครียดีบั๊กได้ หรือให้ระบบเครียออโต้") ───
    private readonly SettingsService? _settings;

    /// <summary>3 / 7 / 14 / 30 days, or forever (<see cref="DebugLogService.RetentionChoices"/>).</summary>
    public ObservableCollection<RetentionChoice> RetentionChoices { get; } = new();

    public int RetentionDays
    {
        get => _log.RetentionDays;
        set
        {
            if (_log.RetentionDays == value) return;
            _log.RetentionDays = value;
            _settings?.UpdateSettings(s => s.DebugLogRetentionDays = value);
            OnPropertyChanged();
            // the new limit applies now, off the UI thread; the file count refreshes after it
            _ = Task.Run(() => _log.PruneFiles()).ContinueWith(_ => App.Current?.Dispatcher.BeginInvoke(new Action(RefreshFileStats)));
        }
    }

    public bool Verbose
    {
        get => _log.Verbose;
        set
        {
            if (_log.Verbose == value) return;
            _log.Verbose = value;
            _settings?.UpdateSettings(s => s.DebugLogVerbose = value);
            if (value && _filterMinLevel > LogLevel.Trace) FilterMinLevel = LogLevel.Trace;   // so the new lines show
            _log.Info("DebugLog", $"detailed logging {(value ? "on" : "off")}");
            OnPropertyChanged();
        }
    }

    public string RetentionTip => _loc.T("dbglog.retentionTip", _log.MaxTotalBytes / 1048576);

    private string _fileStats = "";
    /// <summary>"12 ไฟล์ · 4.2 MB" — the log files on disk now.</summary>
    public string FileStats { get => _fileStats; private set => SetProperty(ref _fileStats, value); }

    private void RefreshFileStats()
    {
        var (n, bytes) = _log.MeasureFiles();
        FileStats = _loc.T("dbglog.files", n, FormatBytes(bytes));
    }

    public static string FormatBytes(long bytes)
        => bytes >= 1048576 ? $"{bytes / 1048576.0:0.0} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0} KB" : $"{bytes} B";

    // ─── Commands ───────────────────────────────────────────────────
    public ICommand RefreshCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand CopyAllCommand { get; }
    public ICommand SaveDumpCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand TestCommand { get; }
    /// <summary>"แจ้งปัญหา": the report window.</summary>
    public ICommand ReportCommand { get; }
    /// <summary>"ล้างไฟล์ล็อก": every log file and crash log, after the user says yes.</summary>
    public ICommand ClearFilesCommand { get; }

    /// <summary>The yes/no before the log files go (a harness swaps it). Default: a Windows message box, "No" focused.</summary>
    public Func<string, string, bool> Confirm { get; set; } = (title, text) =>
        System.Windows.MessageBox.Show(text, title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;

    public DebugLogViewModel(DebugLogService log, LocalizationService loc, SettingsService? settings = null)
    {
        _log = log;
        _loc = loc;
        _settings = settings;
        foreach (int days in DebugLogService.RetentionChoices) RetentionChoices.Add(new RetentionChoice(days));
        _loc.LanguageChanged += () =>
        {
            foreach (var c in RetentionChoices) c.Refresh();
            OnPropertyChanged(nameof(RetentionTip));
            RefreshFileStats();
        };
        RefreshFileStats();

        ReportCommand = new RelayCommand(() => App.ShowReportProblem());
        ClearFilesCommand = new RelayCommand(() =>
        {
            var (n, bytes) = _log.MeasureFiles();
            if (n == 0 && !Directory.Exists(App.CrashLogDir))
            {
                StatusMessage = _loc.T("dbglog.clearFiles.none");
                return;
            }
            if (!Confirm(_loc.T("dbglog.clearFiles.confirmTitle"), _loc.T("dbglog.clearFiles.confirm", n, FormatBytes(bytes)))) return;
            var (gone, freed) = _log.DeleteLogFiles(App.CrashLogDir);
            Rebuild();
            RefreshFileStats();
            StatusMessage = gone == 0 ? _loc.T("dbglog.clearFiles.none") : _loc.T("dbglog.clearFiles.done", gone, FormatBytes(freed));
        });

        foreach (var level in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warning, LogLevel.Error, LogLevel.Critical })
            LevelChoices.Add(new FilterChoice(level.ToString(), "dbglog.level." + level, isKey: true));
        CategoryChoices.Add(new FilterChoice("", "dbglog.allCategories", isKey: true));
        _loc.LanguageChanged += () =>
        {
            foreach (var c in LevelChoices) c.Refresh();
            foreach (var c in CategoryChoices) c.Refresh();
        };

        RefreshCommand = new RelayCommand(Rebuild);
        ClearCommand = new RelayCommand(() => { _log.Clear(); Rebuild(); });
        CopyAllCommand = new RelayCommand(() =>
        {
            try
            {
                System.Windows.Clipboard.SetText(_log.DumpAsText());
                StatusMessage = string.Format(_loc.T("dbglog.copied"), _log.Snapshot().Count);
            }
            catch { StatusMessage = _loc.T("dbglog.copyFailed"); }
        });
        SaveDumpCommand = new RelayCommand(() =>
        {
            var dlg = new SaveFileDialog
            {
                Title = _loc.T("dbglog.saveTitle"),
                Filter = _loc.T("dbglog.saveFilter"),
                FileName = $"cluadex-dump-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, _log.DumpAsText());
                StatusMessage = string.Format(_loc.T("dbglog.saved"), dlg.FileName);
            }
            catch (Exception ex)
            {
                _log.Warn("DebugLog", "Saving the log dump failed", ex);
                StatusMessage = _loc.T("dbglog.saveFailed");
            }
        });
        OpenLogFolderCommand = new RelayCommand(() =>
        {
            try
            {
                Directory.CreateDirectory(_log.LogDirectory);
                // A folder path: the shell opens it in Explorer (it can't "run" a directory).
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _log.LogDirectory, UseShellExecute = true,
                });
                StatusMessage = string.Format(_loc.T("dbglog.openedFolder"), _log.LogDirectory);
            }
            catch (Exception ex)
            {
                _log.Warn("DebugLog", "Open log folder failed", ex);
                StatusMessage = _loc.T("dbglog.openFolderFailed");
            }
        });
        // Self-test: proves the pipeline (write → ring buffer → live tail) end to end with ONE
        // Info line that says what it is. It used to emit a fake Warning, a fake Error and a fake
        // exception — they went into the day's log file and bumped the warning/error counters, so
        // a bug report later showed errors that never happened.
        TestCommand = new RelayCommand(() =>
        {
            _log.Info("DebugLog", $"[self-test] log pipeline OK · {DateTime.Now:HH:mm:ss} — this line is not an error");
            StatusMessage = _loc.T("dbglog.selfTestDone");
        });

        // Initial fill — also seed the categories so the Category ComboBox
        // is populated immediately (fix audit MEDIUM #12: empty filter chip
        // until the first new entry lands).
        foreach (var e in _log.Snapshot())
        {
            NoteCategory(e.Category);
            AddIfPasses(e);
        }

        // Live tail
        _log.OnEntry += entry =>
        {
            App.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                NoteCategory(entry.Category);
                AddIfPasses(entry);
                OnPropertyChanged(nameof(WarningCount));
                OnPropertyChanged(nameof(ErrorCount));
                OnPropertyChanged(nameof(CriticalCount));
                OnPropertyChanged(nameof(TotalCount));
            }));
        };
    }

    /// <summary>Insert a newly seen category in sorted position (after "All") without touching the
    /// selected item.</summary>
    private void NoteCategory(string? category)
    {
        if (string.IsNullOrEmpty(category) || !_categoriesSeen.Add(category)) return;
        int at = 1;
        while (at < CategoryChoices.Count
               && string.Compare(CategoryChoices[at].Value, category, StringComparison.OrdinalIgnoreCase) < 0)
            at++;
        CategoryChoices.Insert(at, new FilterChoice(category, category, isKey: false));
    }

    private void Rebuild()
    {
        Entries.Clear();
        foreach (var e in _log.Snapshot())
        {
            NoteCategory(e.Category);
            AddIfPasses(e);
        }
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(CriticalCount));
        OnPropertyChanged(nameof(TotalCount));
    }

    private void AddIfPasses(LogEntry e)
    {
        if (e.Level < _filterMinLevel) return;
        if (_filterCategory.Length > 0 && !string.Equals(e.Category, _filterCategory, StringComparison.OrdinalIgnoreCase))
            return;
        if (!string.IsNullOrEmpty(_filterText) &&
            !(e.Message?.Contains(_filterText, StringComparison.OrdinalIgnoreCase) ?? false) &&
            !(e.Exception?.Contains(_filterText, StringComparison.OrdinalIgnoreCase) ?? false))
            return;
        Entries.Add(e);
        // Hard cap visible to keep WPF list snappy
        while (Entries.Count > 1500) Entries.RemoveAt(0);
    }
}

/// <summary>One "keep logs" choice: N days, or 0 = forever. The label follows the app's language.</summary>
public sealed class RetentionChoice : ViewModelBase
{
    public RetentionChoice(int days) => Days = days;

    public int Days { get; }

    public string Label => Days > 0
        ? string.Format(LocalizedResources.Instance["dbglog.retention.days"], Days)
        : LocalizedResources.Instance["dbglog.retention.forever"];

    public void Refresh() => OnPropertyChanged(nameof(Label));
}
