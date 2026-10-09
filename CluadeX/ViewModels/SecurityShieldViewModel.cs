using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// View model for the SecurityShield page — kicks off a scan against the
/// current working directory, shows live progress, and lets the user
/// filter findings by severity / category / file.
/// </summary>
public class SecurityShieldViewModel : ViewModelBase
{
    private readonly SecurityShieldService _service;
    private readonly FileSystemService _fs;
    private readonly ChatViewModel _chatVm;
    private readonly CodeEditorViewModel _editor;
    private readonly LocalizationService _loc;
    private readonly DebugLogService _log;
    private CancellationTokenSource? _cts;

    public ObservableCollection<SecurityFinding> Findings { get; } = new();

    /// <summary>
    /// MINIMUM severity, as the filter's name always claimed — "High" shows High and Critical. It
    /// used to be an exact match, and was compared as an English display string. The value is the
    /// enum name ("" = all); the label is localized.
    /// </summary>
    public ObservableCollection<FilterChoice> SeverityChoices { get; } = new();

    private string _severityFilter = "";
    public string? SeverityFilter
    {
        get => _severityFilter;
        set
        {
            // null arrives when the ComboBox loses its item (choices rebuilt) — that means "all",
            // not "nothing": the old code stored null, matched no category, and emptied the list.
            string v = value ?? "";
            if (_severityFilter == v) return;
            _severityFilter = v;
            OnPropertyChanged();
            Rebuild();
        }
    }

    public ObservableCollection<FilterChoice> CategoryChoices { get; } = new();

    private string _categoryFilter = "";
    public string? CategoryFilter
    {
        get => _categoryFilter;
        set
        {
            if (_rebuilding) return;   // the ComboBox reporting our own list rebuild, not the user
            string v = value ?? "";
            if (_categoryFilter == v) return;
            _categoryFilter = v;
            OnPropertyChanged();
            Rebuild();
        }
    }

    private string _textFilter = "";
    public string TextFilter
    {
        get => _textFilter;
        set { if (SetProperty(ref _textFilter, value)) Rebuild(); }
    }

    private SecurityFinding? _selected;
    public SecurityFinding? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) OnPropertyChanged(nameof(HasSelection)); }
    }
    public bool HasSelection => _selected != null;

    private SecurityScanResult? _lastResult;
    public int RawFindingCount => _lastResult?.Findings.Count ?? 0;
    public int VisibleFindingCount => Findings.Count;
    public int CriticalCount => _lastResult?.CriticalCount ?? 0;
    public int HighCount => _lastResult?.HighCount ?? 0;
    public int MediumCount => _lastResult?.MediumCount ?? 0;
    public int LowCount => _lastResult?.LowCount ?? 0;
    public int FilesScanned => _lastResult?.FilesScanned ?? 0;
    public string DurationDisplay => _lastResult == null ? "" : $"{_lastResult.Duration.TotalMilliseconds:F0} ms";

    private bool _isScanning;
    public bool IsScanning { get => _isScanning; set => SetProperty(ref _isScanning, value); }

    private string _statusMessage = "";
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public int RuleCount => _service.BuiltInRuleCount;
    public string ProjectName
    {
        get
        {
            if (!_fs.HasWorkingDirectory) return _loc.T("shieldx.noProjectName");
            return Path.GetFileName(_fs.WorkingDirectory.TrimEnd('\\', '/')) ?? _fs.WorkingDirectory;
        }
    }
    public bool HasProject => _fs.HasWorkingDirectory;

    public ICommand ScanCommand { get; }
    public ICommand CancelScanCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand RevealFileCommand { get; }

    public SecurityShieldViewModel(
        SecurityShieldService service, FileSystemService fs, ChatViewModel chatVm,
        CodeEditorViewModel editor, LocalizationService loc, DebugLogService log)
    {
        _service = service;
        _fs = fs;
        _chatVm = chatVm;
        _editor = editor;
        _loc = loc;
        _log = log;

        foreach (var (value, key) in new[]
                 {
                     ("", "shieldx.sevAll"),
                     (nameof(SecuritySeverity.Critical), "shieldx.sevCritical"),
                     (nameof(SecuritySeverity.High), "shieldx.sevHigh"),
                     (nameof(SecuritySeverity.Medium), "shieldx.sevMedium"),
                     (nameof(SecuritySeverity.Low), "shieldx.sevLow"),
                 })
            SeverityChoices.Add(new FilterChoice(value, key, isKey: true));
        CategoryChoices.Add(new FilterChoice("", "shieldx.catAll", isKey: true));
        _loc.LanguageChanged += () =>
        {
            foreach (var c in SeverityChoices) c.Refresh();
            foreach (var c in CategoryChoices) c.Refresh();
            OnPropertyChanged(nameof(ProjectName));
        };

        ScanCommand = new AsyncRelayCommand(RunScanAsync, () => !_isScanning && _fs.HasWorkingDirectory);
        CancelScanCommand = new RelayCommand(() => _cts?.Cancel());
        ClearCommand = new RelayCommand(() =>
        {
            Findings.Clear();
            _lastResult = null;
            Selected = null;
            RebuildCategoryChoices();
            RaiseStats();
            StatusMessage = _loc.T("shieldx.cleared");
        });

        // Open the finding IN THE BUILT-IN EDITOR at its line. It used to Process.Start the file
        // with UseShellExecute — for a .js/.vbs/.bat/.ps1/.py from the repo being audited (exactly
        // the untrusted code this page exists to inspect) that EXECUTES the file instead of
        // showing it.
        OpenFileCommand = new AsyncRelayCommand(OpenSelectedAsync);
        RevealFileCommand = new RelayCommand(() =>
        {
            var f = _selected;
            if (f == null) return;
            if (!CluadeX.Views.Editor.SafeShell.RevealInExplorer(f.FilePath))
                StatusMessage = _loc.T("shieldx.revealFailed");
        });

        // Refresh ProjectName / HasProject when the working dir changes
        _chatVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatViewModel.WorkingDirectory))
            {
                OnPropertyChanged(nameof(ProjectName));
                OnPropertyChanged(nameof(HasProject));
            }
        };
    }

    private async Task OpenSelectedAsync()
    {
        var f = _selected;
        if (f == null) return;
        try
        {
            if (!File.Exists(f.FilePath)) { StatusMessage = string.Format(_loc.T("shieldx.fileGone"), f.FileName); return; }

            // Show the Code page, then open + reveal. NavigateToCommand is MainViewModel's own public
            // navigation (the command palette uses the same path).
            if (System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel main
                && main.NavigateToCommand.CanExecute("Code"))
                main.NavigateToCommand.Execute("Code");

            bool opened = await _editor.OpenAndRevealAsync(f.FilePath, f.LineNumber);
            if (opened)
            {
                StatusMessage = string.Format(_loc.T("shieldx.opened"), f.Location);
            }
            else
            {
                // Too large / binary for the editor — point at it in Explorer, never run it.
                CluadeX.Views.Editor.SafeShell.RevealInExplorer(f.FilePath);
                StatusMessage = string.Format(_loc.T("shieldx.openedInExplorer"), f.FileName);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Security", $"Open finding failed: {f.FilePath}", ex);
            StatusMessage = string.Format(_loc.T("shieldx.openFailed"), f.FileName);
        }
    }

    private async Task RunScanAsync()
    {
        if (!_fs.HasWorkingDirectory)
        {
            StatusMessage = _loc.T("shieldx.noProject");
            return;
        }
        IsScanning = true;
        StatusMessage = string.Format(_loc.T("shieldx.scanning"), ProjectName, RuleCount);
        _cts = new CancellationTokenSource();
        try
        {
            var result = await _service.ScanFolderAsync(_fs.WorkingDirectory, _cts.Token);
            _lastResult = result;
            Rebuild();
            StatusMessage = string.Format(_loc.T("shieldx.done"),
                result.Findings.Count, result.FilesScanned, result.Duration.TotalMilliseconds.ToString("F0"))
                + (result.FilesSkipped > 0 ? " · " + string.Format(_loc.T("shieldx.unreadable"), result.FilesSkipped) : "");
        }
        catch (OperationCanceledException)
        {
            // The previous result (if any) stays on screen, but the status must not claim this run
            // finished — a cancelled scan says nothing about the files it never reached.
            StatusMessage = _loc.T("shieldx.cancelled");
        }
        catch (Exception ex)
        {
            _log.Error("Security", "Scan failed", ex);
            StatusMessage = _loc.T("shieldx.failed");
        }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool _rebuilding;

    /// <summary>
    /// Re-filter the findings. Guarded against re-entrancy: rebuilding the category list makes the
    /// bound ComboBox drop its selection and write it back through the two-way binding, which used to
    /// re-enter this method with CategoryFilter = null mid-rebuild — the list came back empty after
    /// every scan and the category filter never worked.
    /// </summary>
    private void Rebuild()
    {
        if (_rebuilding) return;
        _rebuilding = true;
        try
        {
            RebuildCategoryChoices();

            Findings.Clear();
            if (_lastResult == null) { RaiseStats(); return; }

            SecuritySeverity? minSev = Enum.TryParse<SecuritySeverity>(_severityFilter, out var s) ? s : null;
            foreach (var f in _lastResult.Findings)
            {
                if (minSev.HasValue && f.Severity < minSev.Value) continue;
                if (_categoryFilter.Length > 0 && !string.Equals(f.Category, _categoryFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(_textFilter) &&
                    !(f.LineSnippet?.Contains(_textFilter, StringComparison.OrdinalIgnoreCase) ?? false) &&
                    !(f.FilePath?.Contains(_textFilter, StringComparison.OrdinalIgnoreCase) ?? false) &&
                    !(f.RuleName?.Contains(_textFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                    continue;
                Findings.Add(f);
            }
            if (_selected != null && !Findings.Contains(_selected)) Selected = Findings.FirstOrDefault();
            RaiseStats();
        }
        finally { _rebuilding = false; }
    }

    /// <summary>Category options from the actual findings — rebuilt only when the SET changes, so a
    /// text-filter keystroke doesn't reset the ComboBox under the user.</summary>
    private void RebuildCategoryChoices()
    {
        var wanted = (_lastResult?.Findings.Select(f => f.Category) ?? Enumerable.Empty<string>())
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var current = CategoryChoices.Skip(1).Select(c => c.Value).ToList();
        if (current.SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase)) return;

        bool wasRebuilding = _rebuilding;
        _rebuilding = true;
        try
        {
            while (CategoryChoices.Count > 1) CategoryChoices.RemoveAt(CategoryChoices.Count - 1);
            foreach (var cat in wanted) CategoryChoices.Add(new FilterChoice(cat, cat, isKey: false));

            // A category that no longer exists can't stay selected.
            if (_categoryFilter.Length > 0 && !wanted.Contains(_categoryFilter, StringComparer.OrdinalIgnoreCase))
                _categoryFilter = "";
        }
        finally { _rebuilding = wasRebuilding; }
        OnPropertyChanged(nameof(CategoryFilter));   // re-select in the rebuilt list
    }

    private void RaiseStats()
    {
        OnPropertyChanged(nameof(RawFindingCount));
        OnPropertyChanged(nameof(VisibleFindingCount));
        OnPropertyChanged(nameof(CriticalCount));
        OnPropertyChanged(nameof(HighCount));
        OnPropertyChanged(nameof(MediumCount));
        OnPropertyChanged(nameof(LowCount));
        OnPropertyChanged(nameof(FilesScanned));
        OnPropertyChanged(nameof(DurationDisplay));
    }
}

/// <summary>A filter ComboBox entry: a stable <see cref="Value"/> the code compares, and a
/// <see cref="Label"/> in the UI language (re-read on a language switch).</summary>
public sealed class FilterChoice : ViewModelBase
{
    private readonly string _labelOrKey;
    private readonly bool _isKey;

    public FilterChoice(string value, string labelOrKey, bool isKey)
    {
        Value = value;
        _labelOrKey = labelOrKey;
        _isKey = isKey;
    }

    public string Value { get; }
    public string Label => _isKey ? LocalizedResources.Instance[_labelOrKey] : _labelOrKey;
    public void Refresh() => OnPropertyChanged(nameof(Label));
    public override string ToString() => Label;
}
