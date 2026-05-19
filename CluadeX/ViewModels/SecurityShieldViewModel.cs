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
    private CancellationTokenSource? _cts;

    public ObservableCollection<SecurityFinding> Findings { get; } = new();

    public IReadOnlyList<string> SeverityChoices { get; } = new[] { "All", "Critical", "High", "Medium", "Low", "Info" };

    private string _severityFilter = "All";
    public string SeverityFilter
    {
        get => _severityFilter;
        set { if (SetProperty(ref _severityFilter, value)) Rebuild(); }
    }

    private string _categoryFilter = "All";
    public string CategoryFilter
    {
        get => _categoryFilter;
        set { if (SetProperty(ref _categoryFilter, value)) Rebuild(); }
    }

    private string _textFilter = "";
    public string TextFilter
    {
        get => _textFilter;
        set { if (SetProperty(ref _textFilter, value)) Rebuild(); }
    }

    public ObservableCollection<string> CategoryChoices { get; } = new() { "All" };

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
            if (!_fs.HasWorkingDirectory) return "(no project open)";
            return Path.GetFileName(_fs.WorkingDirectory.TrimEnd('\\', '/')) ?? _fs.WorkingDirectory;
        }
    }
    public bool HasProject => _fs.HasWorkingDirectory;

    public ICommand ScanCommand { get; }
    public ICommand CancelScanCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand OpenFileCommand { get; }

    public SecurityShieldViewModel(
        SecurityShieldService service, FileSystemService fs, ChatViewModel chatVm)
    {
        _service = service;
        _fs = fs;
        _chatVm = chatVm;

        ScanCommand = new AsyncRelayCommand(RunScanAsync, () => !_isScanning && _fs.HasWorkingDirectory);
        CancelScanCommand = new RelayCommand(() => _cts?.Cancel());
        ClearCommand = new RelayCommand(() =>
        {
            Findings.Clear();
            _lastResult = null;
            Selected = null;
            RaiseStats();
            StatusMessage = "Cleared.";
        });
        OpenFileCommand = new RelayCommand(() =>
        {
            if (_selected == null) return;
            try
            {
                // Open the file in the user's default editor (no syntax-jump
                // to specific line yet — that's an Avalon-Edit integration
                // for the Code Editor page in a follow-up).
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _selected.FilePath, UseShellExecute = true,
                });
                StatusMessage = $"Opened {_selected.FileName}";
            }
            catch (Exception ex) { StatusMessage = $"Open failed: {ex.Message}"; }
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

    private async Task RunScanAsync()
    {
        if (!_fs.HasWorkingDirectory)
        {
            StatusMessage = "No project open — use Open Folder in the title bar first.";
            return;
        }
        IsScanning = true;
        StatusMessage = $"Scanning {ProjectName} with {RuleCount} rules...";
        _cts = new CancellationTokenSource();
        try
        {
            var result = await _service.ScanFolderAsync(_fs.WorkingDirectory, _cts.Token);
            _lastResult = result;
            Rebuild();
            StatusMessage = $"Done: {result.Findings.Count} findings in {result.FilesScanned} files " +
                            $"({result.Duration.TotalMilliseconds:F0} ms)";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Rebuild()
    {
        Findings.Clear();
        CategoryChoices.Clear();
        CategoryChoices.Add("All");
        if (_lastResult == null) { RaiseStats(); return; }

        // Populate category options from the actual findings
        foreach (var cat in _lastResult.Findings.Select(f => f.Category).Distinct().OrderBy(c => c))
            CategoryChoices.Add(cat);

        SecuritySeverity? minSev = _severityFilter switch
        {
            "Critical" => SecuritySeverity.Critical,
            "High"     => SecuritySeverity.High,
            "Medium"   => SecuritySeverity.Medium,
            "Low"      => SecuritySeverity.Low,
            "Info"     => SecuritySeverity.Info,
            _          => null,
        };
        foreach (var f in _lastResult.Findings)
        {
            if (minSev.HasValue && f.Severity != minSev.Value) continue;
            if (_categoryFilter != "All" && !string.Equals(f.Category, _categoryFilter, StringComparison.OrdinalIgnoreCase))
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
