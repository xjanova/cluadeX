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

    /// <summary>The visible (filtered) entries. UI binds here.</summary>
    public ObservableCollection<LogEntry> Entries { get; } = new();

    public IReadOnlyList<string> LevelChoices { get; } = new[] { "Trace", "Debug", "Info", "Warning", "Error", "Critical" };
    public IReadOnlyList<string> CategoryChoices => _categoriesSeen.OrderBy(s => s).Prepend("All").ToList();

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

    public string SelectedLevelName
    {
        get => _filterMinLevel.ToString();
        set
        {
            if (Enum.TryParse<LogLevel>(value, true, out var l)) FilterMinLevel = l;
        }
    }

    private string _filterCategory = "All";
    public string FilterCategory
    {
        get => _filterCategory;
        set
        {
            if (SetProperty(ref _filterCategory, value)) Rebuild();
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

    // ─── Commands ───────────────────────────────────────────────────
    public ICommand RefreshCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand CopyAllCommand { get; }
    public ICommand SaveDumpCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand TestCommand { get; }

    public DebugLogViewModel(DebugLogService log)
    {
        _log = log;
        RefreshCommand = new RelayCommand(Rebuild);
        ClearCommand = new RelayCommand(() => { _log.Clear(); Rebuild(); });
        CopyAllCommand = new RelayCommand(() =>
        {
            try
            {
                System.Windows.Clipboard.SetText(_log.DumpAsText());
                StatusMessage = $"Copied {_log.Snapshot().Count} entries to clipboard";
            }
            catch (Exception ex) { StatusMessage = $"Copy failed: {ex.Message}"; }
        });
        SaveDumpCommand = new RelayCommand(() =>
        {
            var dlg = new SaveFileDialog
            {
                Title = "Save log dump",
                Filter = "Log file|*.log|Text file|*.txt",
                FileName = $"cluadex-dump-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, _log.DumpAsText());
                StatusMessage = $"Saved to {dlg.FileName}";
            }
            catch (Exception ex) { StatusMessage = $"Save failed: {ex.Message}"; }
        });
        OpenLogFolderCommand = new RelayCommand(() =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _log.LogDirectory, UseShellExecute = true,
                });
                StatusMessage = $"Opened {_log.LogDirectory}";
            }
            catch (Exception ex) { StatusMessage = $"Open folder failed: {ex.Message}"; }
        });
        TestCommand = new RelayCommand(() =>
        {
            _log.Trace("DebugLog", "Trace test entry");
            _log.Debug("DebugLog", "Debug test entry");
            _log.Info("DebugLog", "Info test entry");
            _log.Warn("DebugLog", "Warning test entry");
            _log.Error("DebugLog", "Error test entry", new InvalidOperationException("Sample exception"));
            StatusMessage = "Emitted 5 test entries (one per level)";
        });

        // Initial fill — also seed _categoriesSeen so the Category ComboBox
        // is populated immediately (fix audit MEDIUM #12: empty filter chip
        // until the first new entry lands).
        foreach (var e in _log.Snapshot())
        {
            _categoriesSeen.Add(e.Category);
            AddIfPasses(e);
        }

        // Live tail
        _log.OnEntry += entry =>
        {
            App.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                _categoriesSeen.Add(entry.Category);
                AddIfPasses(entry);
                OnPropertyChanged(nameof(WarningCount));
                OnPropertyChanged(nameof(ErrorCount));
                OnPropertyChanged(nameof(CriticalCount));
                OnPropertyChanged(nameof(TotalCount));
            }));
        };
    }

    private void Rebuild()
    {
        Entries.Clear();
        foreach (var e in _log.Snapshot()) AddIfPasses(e);
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(CriticalCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CategoryChoices));
    }

    private void AddIfPasses(LogEntry e)
    {
        if (e.Level < _filterMinLevel) return;
        if (_filterCategory != "All" && !string.Equals(e.Category, _filterCategory, StringComparison.OrdinalIgnoreCase))
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
