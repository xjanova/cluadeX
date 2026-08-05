using System.Collections.ObjectModel;
using System.IO;
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
            if (_fileInfo == null) return "No file open — click Open to begin";
            var dirty = _hex.IsDirty ? " · MODIFIED" : "";
            var ro = _hex.IsReadOnly ? " · READ-ONLY" : "";
            return $"{Path.GetFileName(_fileInfo.Path)}  ·  {_fileInfo.SizeDisplay}  ·  {_fileInfo.DetectedType}{dirty}{ro}";
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

    public HexEditorViewModel(HexEditorService hex)
    {
        _hex = hex;
        OpenCommand = new AsyncRelayCommand(OpenAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => HasFile && _hex.IsDirty && !_hex.IsReadOnly);
        SaveAsCommand = new AsyncRelayCommand(SaveAsAsync, () => HasFile);
        CloseCommand = new RelayCommand(() => { _hex.Close(); FileInfo = null; Rows.Clear(); SearchResults.Clear(); });
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
            string hex = _hex.FormatHex(_selectedOffset, Math.Max(1, _selectedLength));
            try { System.Windows.Clipboard.SetText(hex); StatusMessage = $"Copied {hex.Length / 3 + 1} bytes as hex"; }
            catch { StatusMessage = "Copy failed"; }
        });
        CopyAsciiCommand = new RelayCommand(() =>
        {
            if (!HasFile) return;
            var (_, ascii) = _hex.ReadHexAndAscii(_selectedOffset, Math.Max(1, _selectedLength));
            try { System.Windows.Clipboard.SetText(ascii); StatusMessage = $"Copied {ascii.Length} chars as ASCII"; }
            catch { StatusMessage = "Copy failed"; }
        });

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
                StatusMessage = $"Loaded {info.SizeDisplay} · {info.DetectedType} · SHA256: {info.Sha256[..Math.Min(16, info.Sha256.Length)]}…";
            });
        };
    }

    public HexEditorService Service => _hex;

    // ═══════════════════════════════════════════
    // File operations
    // ═══════════════════════════════════════════

    private async Task OpenAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open file in Hex Editor",
            Filter = "All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            StatusMessage = "Opening...";
            await _hex.OpenAsync(dlg.FileName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Open failed: {ex.Message}";
        }
    }

    public async Task OpenPathAsync(string path)
    {
        try
        {
            StatusMessage = "Opening...";
            await _hex.OpenAsync(path);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Open failed: {ex.Message}";
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            await _hex.SaveAsync(createBackup: true);
            StatusMessage = "Saved (.bak written)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    private async Task SaveAsAsync()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save As (binary)",
            Filter = "All files|*.*",
            FileName = _fileInfo == null ? "patched.bin" : Path.GetFileNameWithoutExtension(_fileInfo.Path) + ".patched" + Path.GetExtension(_fileInfo.Path),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            await _hex.SaveAsAsync(dlg.FileName);
            StatusMessage = $"Saved as {dlg.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
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

    private void DoGoto()
    {
        if (string.IsNullOrWhiteSpace(_gotoInput)) return;
        var input = _gotoInput.Trim();
        long offset;
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(input[2..], System.Globalization.NumberStyles.HexNumber, null, out offset))
            { StatusMessage = "Invalid hex offset"; return; }
        }
        else if (input.All(c => "0123456789abcdefABCDEF".Contains(c)))
        {
            if (!long.TryParse(input, System.Globalization.NumberStyles.HexNumber, null, out offset))
            { StatusMessage = "Invalid hex offset"; return; }
        }
        else if (!long.TryParse(input, out offset))
        {
            StatusMessage = "Invalid offset (use 0x... for hex, or decimal)";
            return;
        }
        JumpTo(offset);
        StatusMessage = $"Jumped to 0x{offset:X8}";
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
            StatusMessage = "Empty search pattern";
            return;
        }
        var mode = _searchModeHex ? HexEditorService.SearchMode.Hex : HexEditorService.SearchMode.Text;
        var hits = _hex.Search(_searchPattern, mode);
        foreach (var h in hits) SearchResults.Add(h);
        StatusMessage = hits.Count == 0 ? "No matches" : $"{hits.Count} match(es) found";
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
            StatusMessage = "Empty search pattern";
            return;
        }
        var mode = _searchModeHex ? HexEditorService.SearchMode.Hex : HexEditorService.SearchMode.Text;
        int n = _hex.ReplaceAll(_searchPattern, _replacePattern, mode);
        StatusMessage = n == 0 ? "Nothing replaced (length must match)" : $"Replaced {n} occurrence(s)";
    }

    private void DoApplyPatch()
    {
        if (!_hex.HasFile) return;
        if (string.IsNullOrWhiteSpace(_patchInput))
        {
            StatusMessage = "Enter bytes to patch (hex)";
            return;
        }
        if (!HexEditorService.TryParseHex(_patchInput, out var bytes))
        {
            StatusMessage = "Invalid hex input";
            return;
        }
        bool ok = _hex.Patch(_selectedOffset, bytes, "manual patch");
        StatusMessage = ok
            ? $"Patched {bytes.Length} byte(s) at {SelectedOffsetHex}"
            : "Patch failed (out of range or no change)";
    }
}
