using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using Microsoft.Win32;

namespace CluadeX.ViewModels;

/// <summary>
/// View model for the Hex Editor — manages the visible window of rows,
/// selection, find/replace state, goto offset, and the inspector pane.
/// All file operations delegate to HexEditorService.
/// </summary>
public class HexEditorViewModel : ViewModelBase
{
    private readonly HexEditorService _hex;
    private readonly LocalizationService _loc;
    private readonly DebugLogService? _log;

    // ─── Visible rows (windowed view; not the full file) ───
    public ObservableCollection<HexRow> Rows { get; } = new();

    // ─── Search results panel ───
    public ObservableCollection<HexSearchResult> SearchResults { get; } = new();

    public HexInspector Inspector { get; } = new();

    private HexFileInfo? _fileInfo;
    public HexFileInfo? FileInfo
    {
        get => _fileInfo;
        set
        {
            if (SetProperty(ref _fileInfo, value))
            {
                OnPropertyChanged(nameof(HasFile));
                OnPropertyChanged(nameof(StatusBarText));
            }
        }
    }

    public bool HasFile => _fileInfo != null && _hex.HasFile;

    private long _selectedOffset;
    public long SelectedOffset
    {
        get => _selectedOffset;
        set
        {
            if (SetProperty(ref _selectedOffset, value))
            {
                OnPropertyChanged(nameof(SelectedOffsetHex));
                UpdateInspector();
            }
        }
    }
    public string SelectedOffsetHex => $"0x{_selectedOffset:X8}";

    private int _selectedLength = 1;
    public int SelectedLength
    {
        get => _selectedLength;
        set
        {
            if (SetProperty(ref _selectedLength, value))
                UpdateInspector();
        }
    }

    private string _gotoInput = "";
    public string GotoInput { get => _gotoInput; set => SetProperty(ref _gotoInput, value); }

    private string _searchPattern = "";
    public string SearchPattern { get => _searchPattern; set => SetProperty(ref _searchPattern, value); }

    private string _replacePattern = "";
    public string ReplacePattern { get => _replacePattern; set => SetProperty(ref _replacePattern, value); }

    private bool _searchModeHex = true;
    public bool SearchModeHex
    {
        get => _searchModeHex;
        set
        {
            if (SetProperty(ref _searchModeHex, value))
            {
                OnPropertyChanged(nameof(SearchModeText));
                OnPropertyChanged(nameof(SearchModeLabel));
            }
        }
    }
    public bool SearchModeText
    {
        get => !_searchModeHex;
        set => SearchModeHex = !value;
    }
    public string SearchModeLabel => _searchModeHex ? "HEX" : "TEXT";

    private bool _showFindPanel;
    public bool ShowFindPanel { get => _showFindPanel; set => SetProperty(ref _showFindPanel, value); }

    private bool _showInspector = true;
    public bool ShowInspector { get => _showInspector; set => SetProperty(ref _showInspector, value); }

    private string _patchInput = "";
    public string PatchInput { get => _patchInput; set => SetProperty(ref _patchInput, value); }

    private string _statusMessage = "";
    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetProperty(ref _statusMessage, value))
                OnPropertyChanged(nameof(StatusBarText));
        }
    }

    public string StatusBarText
    {
        get
        {
            if (_fileInfo == null) return _loc.T("hex.noFile");
            var dirty = _hex.IsDirty ? _loc.T("hex.modifiedTag") : "";
            var ro = _hex.IsReadOnly ? _loc.T("hex.readOnlyTag") : "";
            var cut = _hex.IsTruncated ? _loc.T("hex.truncatedTag", FormatSize(HexEditorService.InMemoryLimit)) : "";
            return $"{Path.GetFileName(_fileInfo.Path)}  ·  {_fileInfo.SizeDisplay}  ·  {_fileInfo.DetectedType}{dirty}{ro}{cut}";
        }
    }

    public bool CanUndo => _hex.CanUndo;
    public bool CanRedo => _hex.CanRedo;

    // ─── Pagination (windowed view) ───
    // We keep the rendered row count modest so the grid stays responsive even
    // on multi-MB files. User can page through with Prev/Next or scroll.
    private const int VisibleRowCount = 1024;  // ~16 KB visible at a time
    private long _windowStartOffset;
    public long WindowStartOffset
    {
        get => _windowStartOffset;
        set
        {
            if (SetProperty(ref _windowStartOffset, value))
            {
                OnPropertyChanged(nameof(WindowStartHex));
                OnPropertyChanged(nameof(WindowEndHex));
                RefreshRows();
            }
        }
    }
    public string WindowStartHex => $"0x{_windowStartOffset:X8}";
    public string WindowEndHex
    {
        get
        {
            long end = Math.Min(_hex.Size, _windowStartOffset + (long)VisibleRowCount * HexEditorService.BytesPerRow);
            return $"0x{end:X8}";
        }
    }

    public long TotalRows => _hex.TotalRows;
    public long TotalSize => _hex.Size;

    // ─── Commands ───
    public ICommand OpenCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand FindCommand { get; }
    public ICommand FindNextCommand { get; }
    public ICommand ReplaceAllCommand { get; }
    public ICommand ToggleFindCommand { get; }
    public ICommand ToggleInspectorCommand { get; }
    public ICommand GotoCommand { get; }
    public ICommand ApplyPatchCommand { get; }
    public ICommand PageNextCommand { get; }
    public ICommand PagePrevCommand { get; }
    public ICommand JumpToResultCommand { get; }
    public ICommand CopyHexCommand { get; }
    public ICommand CopyAsciiCommand { get; }

    public HexEditorViewModel(HexEditorService hex, LocalizationService loc, DebugLogService? log = null)
    {
        _hex = hex;
        _loc = loc;
        _log = log;
        OpenCommand = new AsyncRelayCommand(OpenAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => HasFile && _hex.IsDirty && !_hex.IsReadOnly);
        SaveAsCommand = new AsyncRelayCommand(SaveAsAsync, () => HasFile);
        CloseCommand = new RelayCommand(DoClose);
        UndoCommand = new RelayCommand(() => { _hex.Undo(); }, () => CanUndo);
        RedoCommand = new RelayCommand(() => { _hex.Redo(); }, () => CanRedo);
        FindCommand = new RelayCommand(DoFind);
        FindNextCommand = new RelayCommand(DoFindNext);
        ReplaceAllCommand = new RelayCommand(DoReplaceAll);
        ToggleFindCommand = new RelayCommand(() => ShowFindPanel = !ShowFindPanel);
        ToggleInspectorCommand = new RelayCommand(() => ShowInspector = !ShowInspector);
        GotoCommand = new RelayCommand(DoGoto);
        ApplyPatchCommand = new RelayCommand(DoApplyPatch);
        PageNextCommand = new RelayCommand(() =>
        {
            long step = (long)VisibleRowCount * HexEditorService.BytesPerRow;
            if (_windowStartOffset + step < _hex.Size)
                WindowStartOffset = _windowStartOffset + step;
        });
        PagePrevCommand = new RelayCommand(() =>
        {
            long step = (long)VisibleRowCount * HexEditorService.BytesPerRow;
            WindowStartOffset = Math.Max(0, _windowStartOffset - step);
        });
        JumpToResultCommand = new RelayCommand<HexSearchResult>(r =>
        {
            if (r == null) return;
            JumpTo(r.Offset);
            SelectedOffset = r.Offset;
            SelectedLength = r.Length;
        });
        CopyHexCommand = new RelayCommand(() =>
        {
            if (!HasFile) return;
            int n = Math.Max(1, _selectedLength);
            string hexText = _hex.FormatHex(_selectedOffset, n);
            try { Clipboard.SetText(hexText); StatusMessage = _loc.T("hex.copiedHex", n); }
            catch (Exception ex) { _log?.Warn("HexEditor", "Clipboard copy failed", ex); StatusMessage = _loc.T("hex.copyFailed"); }
        });
        CopyAsciiCommand = new RelayCommand(() =>
        {
            if (!HasFile) return;
            var (_, ascii) = _hex.ReadHexAndAscii(_selectedOffset, Math.Max(1, _selectedLength));
            try { Clipboard.SetText(ascii); StatusMessage = _loc.T("hex.copiedAscii", ascii.Length); }
            catch (Exception ex) { _log?.Warn("HexEditor", "Clipboard copy failed", ex); StatusMessage = _loc.T("hex.copyFailed"); }
        });

        // The service is shared with the agent's hex_* tools: an agent hex_open (or this page's Open) on top of
        // unsaved patches must ask first instead of silently replacing the buffer.
        _hex.ConfirmDiscardChanges = ConfirmDiscardAsync;

        _loc.LanguageChanged += () => OnPropertyChanged(nameof(StatusBarText));

        _hex.Changed += () =>
        {
            App.Current?.Dispatcher.Invoke(() =>
            {
                OnPropertyChanged(nameof(HasFile));
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(CanRedo));
                OnPropertyChanged(nameof(StatusBarText));
                OnPropertyChanged(nameof(TotalRows));
                OnPropertyChanged(nameof(TotalSize));
            });
        };

        _hex.BytesPatched += (offset, length) =>
        {
            App.Current?.Dispatcher.Invoke(() =>
            {
                RefreshRows();
                UpdateInspector();
            });
        };

        _hex.FileLoaded += info =>
        {
            App.Current?.Dispatcher.Invoke(() =>
            {
                FileInfo = info;
                _windowStartOffset = 0;
                OnPropertyChanged(nameof(WindowStartHex));
                OnPropertyChanged(nameof(WindowEndHex));
                RefreshRows();
                SelectedOffset = 0;
                StatusMessage = _hex.IsTruncated
                    ? _loc.T("hex.loadedTruncated", FormatSize(HexEditorService.InMemoryLimit), info.SizeDisplay)
                    : _loc.T("hex.loaded", info.SizeDisplay, info.DetectedType, info.Sha256[..Math.Min(16, info.Sha256.Length)]);
            });
        };
    }

    public HexEditorService Service => _hex;

    private static string FormatSize(long bytes) => new HexFileInfo { Size = bytes }.SizeDisplay;

    private Task<bool> ConfirmDiscardAsync(string pathWithEdits, string pathToOpen)
    {
        var app = Application.Current;
        if (app == null) return Task.FromResult(false);
        return app.Dispatcher.InvokeAsync(() => MessageBox.Show(
            _loc.T("hex.discardConfirm", Path.GetFileName(pathWithEdits), Path.GetFileName(pathToOpen)),
            "CluadeX", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes).Task;
    }

    // ═══════════════════════════════════════════
    // File operations
    // ═══════════════════════════════════════════

    private async Task OpenAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = _loc.T("hex.openDialogTitle"),
            Filter = _loc.T("hex.allFilesFilter"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        await OpenPathAsync(dlg.FileName);
    }

    public async Task OpenPathAsync(string path)
    {
        try
        {
            StatusMessage = _loc.T("hex.opening");
            await _hex.OpenAsync(path);
        }
        catch (HexUnsavedChangesException)
        {
            StatusMessage = _loc.T("hex.openKeptEdits");
        }
        catch (Exception ex)
        {
            _log?.Error("HexEditor", $"Open failed: {path}", ex);
            StatusMessage = ex switch
            {
                FileNotFoundException or DirectoryNotFoundException => _loc.T("hex.openFailedMissing"),
                UnauthorizedAccessException or IOException => _loc.T("hex.openFailedLocked"),
                OutOfMemoryException => _loc.T("hex.openFailedMemory"),
                _ => _loc.T("hex.openFailed"),
            };
        }
    }

    private async Task SaveAsync()
    {
        bool truncated = _hex.IsTruncated;
        try
        {
            await _hex.SaveAsync(createBackup: true);
            StatusMessage = truncated
                ? _loc.T("hex.savedInPlace", FormatSize(HexEditorService.InMemoryLimit))
                : _loc.T("hex.saved");
        }
        catch (Exception ex)
        {
            _log?.Error("HexEditor", $"Save failed: {_hex.CurrentPath}", ex);
            StatusMessage = SaveFailureMessage(ex);
        }
    }

    private string SaveFailureMessage(Exception ex) => ex switch
    {
        HexBackupFailedException b => _loc.T("hex.saveFailedBackup", Path.GetFileName(b.BackupPath)),
        UnauthorizedAccessException => _loc.T("hex.saveFailedLocked"),
        IOException => _loc.T("hex.saveFailedLocked"),
        InvalidOperationException => _loc.T("hex.saveFailedReadOnly"),
        _ => _loc.T("hex.saveFailed"),
    };

    private async Task SaveAsAsync()
    {
        var dlg = new SaveFileDialog
        {
            Title = _loc.T("hex.saveAsDialogTitle"),
            Filter = _loc.T("hex.allFilesFilter"),
            FileName = _fileInfo == null ? "patched.bin" : Path.GetFileNameWithoutExtension(_fileInfo.Path) + ".patched" + Path.GetExtension(_fileInfo.Path),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            StatusMessage = _loc.T("hex.saving");
            await _hex.SaveAsAsync(dlg.FileName);
            StatusMessage = _loc.T("hex.savedAs", dlg.FileName);
        }
        catch (Exception ex)
        {
            _log?.Error("HexEditor", $"Save As failed: {dlg.FileName}", ex);
            StatusMessage = SaveFailureMessage(ex);
        }
    }

    private void DoClose()
    {
        if (_hex.IsDirty && _hex.CurrentPath != null)
        {
            var answer = MessageBox.Show(_loc.T("hex.closeConfirm", Path.GetFileName(_hex.CurrentPath)), "CluadeX",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }
        _hex.Close();
        FileInfo = null;
        Rows.Clear();
        SearchResults.Clear();
    }

    // ═══════════════════════════════════════════
    // Navigation & rows
    // ═══════════════════════════════════════════

    private void RefreshRows()
    {
        Rows.Clear();
        if (!_hex.HasFile) return;
        var rows = _hex.GetRows(_windowStartOffset, VisibleRowCount);
        foreach (var r in rows) Rows.Add(r);
    }

    public void JumpTo(long offset)
    {
        if (!_hex.HasFile) return;
        if (offset < 0) offset = 0;
        if (offset >= _hex.Size) offset = _hex.Size - 1;
        // Page to that offset
        long pageSize = (long)VisibleRowCount * HexEditorService.BytesPerRow;
        long pageStart = (offset / pageSize) * pageSize;
        WindowStartOffset = pageStart;
        SelectedOffset = offset;
    }

    /// <summary>
    /// "0x…" is hex, plain digits are decimal — the tooltip promises "0x1A00 or 6656". The old parser read any
    /// all-hex-digit string as hex, and every all-digit string is one, so "100" jumped to 0x100 (256) and the
    /// decimal branch could never run.
    /// </summary>
    public static bool TryParseOffset(string input, out long offset)
    {
        offset = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim().Replace("_", "").Replace(",", "");
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return s.Length > 2 && long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out offset) && offset >= 0;
        return long.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out offset);
    }

    private void DoGoto()
    {
        if (string.IsNullOrWhiteSpace(_gotoInput)) return;
        if (!TryParseOffset(_gotoInput, out long offset))
        {
            StatusMessage = _loc.T("hex.invalidOffset");
            return;
        }
        if (!_hex.HasFile) return;
        if (offset >= _hex.Size)
        {
            StatusMessage = _loc.T("hex.offsetPastEnd", $"0x{offset:X8}", $"0x{Math.Max(0, _hex.Size - 1):X8}");
            return;
        }
        JumpTo(offset);
        StatusMessage = _loc.T("hex.jumped", $"0x{offset:X8}");
    }

    // ═══════════════════════════════════════════
    // Inspector
    // ═══════════════════════════════════════════

    private void UpdateInspector()
    {
        if (!_hex.HasFile) { Inspector.Update(Array.Empty<byte>(), 0); return; }
        int len = Math.Max(8, _selectedLength); // grab enough for int64/double
        var bytes = _hex.Read(_selectedOffset, len);
        Inspector.Update(bytes, _selectedOffset);
    }

    // ═══════════════════════════════════════════
    // Find / replace / patch
    // ═══════════════════════════════════════════

    private void DoFind()
    {
        SearchResults.Clear();
        if (!_hex.HasFile || string.IsNullOrWhiteSpace(_searchPattern))
        {
            StatusMessage = _loc.T("hex.emptyPattern");
            return;
        }
        var mode = _searchModeHex ? HexEditorService.SearchMode.Hex : HexEditorService.SearchMode.Text;
        if (mode == HexEditorService.SearchMode.Hex && !HexEditorService.TryParseHex(_searchPattern, out _))
        {
            StatusMessage = _loc.T("hex.invalidHexInput");
            return;
        }
        var hits = _hex.Search(_searchPattern, mode);
        foreach (var h in hits) SearchResults.Add(h);
        StatusMessage = hits.Count == 0 ? _loc.T("hex.noMatches") : _loc.T("hex.matchesFound", hits.Count);
        if (hits.Count > 0) JumpTo(hits[0].Offset);
    }

    private void DoFindNext()
    {
        if (SearchResults.Count == 0) { DoFind(); return; }
        // Find first result after current selection
        var next = SearchResults.FirstOrDefault(r => r.Offset > _selectedOffset) ?? SearchResults[0];
        JumpTo(next.Offset);
        SelectedOffset = next.Offset;
        SelectedLength = next.Length;
    }

    private void DoReplaceAll()
    {
        if (!_hex.HasFile) return;
        if (string.IsNullOrWhiteSpace(_searchPattern))
        {
            StatusMessage = _loc.T("hex.emptyPattern");
            return;
        }
        if (_hex.IsReadOnly) { StatusMessage = _loc.T("hex.readOnlyNoEdit"); return; }
        var mode = _searchModeHex ? HexEditorService.SearchMode.Hex : HexEditorService.SearchMode.Text;
        int n = _hex.ReplaceAll(_searchPattern, _replacePattern, mode);
        StatusMessage = n == 0 ? _loc.T("hex.nothingReplaced") : _loc.T("hex.replacedCount", n);
    }

    private void DoApplyPatch()
    {
        if (!_hex.HasFile) return;
        if (string.IsNullOrWhiteSpace(_patchInput))
        {
            StatusMessage = _loc.T("hex.enterPatchBytes");
            return;
        }
        if (!HexEditorService.TryParseHex(_patchInput, out var bytes))
        {
            StatusMessage = _loc.T("hex.invalidHexInput");
            return;
        }
        // Patch throws on a read-only file; this is a sync RelayCommand, so an escaping exception would reach
        // the global crash dialog.
        if (_hex.IsReadOnly) { StatusMessage = _loc.T("hex.readOnlyNoEdit"); return; }
        bool ok = _hex.Patch(_selectedOffset, bytes, "manual patch");
        StatusMessage = ok
            ? _loc.T("hex.patchedAt", bytes.Length, SelectedOffsetHex)
            : _loc.T("hex.patchFailed");
    }
}
