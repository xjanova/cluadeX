using System.IO;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Views.Editor;

namespace CluadeX.ViewModels;

/// <summary>
/// The editor toolbar's file tools (owner 2026-10-12: "หน้า editor ทำไมไม่มีเครื่องมือมาตรฐาน เซฟตรงไหน"):
/// New, Save As, close the active tab, and the file's encoding — the parts that need the workspace. The text tools
/// live in the view (they act on the editor control); the file dialogs too.
/// </summary>
public partial class CodeEditorViewModel
{
    /// <summary>Where a file dialog starts: the active file's folder, else the project, else Documents.</summary>
    public string DefaultFolder
    {
        get
        {
            string? dir = _activeTab != null ? Path.GetDirectoryName(_activeTab.FullPath) : null;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            if (_workspace.HasWorkingDirectory) return _workspace.WorkingDirectory;
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }

    /// <summary>New file: an empty file at <paramref name="path"/>, opened in a tab. An existing file is opened as it
    /// is — never emptied.</summary>
    public async Task<bool> CreateFileAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (File.Exists(path))
        {
            StatusMessage = T("ide.newExists", Path.GetFileName(path));
            return await OpenPathCoreAsync(path);
        }
        if (!CodeWorkspaceService.TryCreateEmptyFile(path, out var error))
        {
            _log?.Warn("Editor", $"New file failed: {path} — {error}");
            StatusMessage = T("ide.newFailed", Path.GetFileName(path));
            return false;
        }
        bool opened = await OpenPathCoreAsync(path);
        if (opened) StatusMessage = T("ide.newCreated", Path.GetFileName(path));
        if (_workspace.HasWorkingDirectory) _ = RefreshTreeAsync();
        return opened;
    }

    /// <summary>
    /// Save As: the active tab's text goes to <paramref name="newPath"/> and that file takes the tab's place — its
    /// edits are now saved there; the old file keeps what it had (Notepad++). Refused when <paramref name="newPath"/>
    /// is open in another tab with unsaved edits (they would be silently overwritten).
    /// </summary>
    public async Task<bool> SaveActiveAsAsync(string newPath)
    {
        var tab = _activeTab;
        if (tab == null || string.IsNullOrWhiteSpace(newPath)) return false;
        if (tab is MediaFileTab media) return await SaveMediaAsAsync(media, newPath);   // the page writes those edits
        if (string.Equals(Path.GetFullPath(newPath), Path.GetFullPath(tab.FullPath), StringComparison.OrdinalIgnoreCase))
            return await SaveTabInteractiveAsync(tab);
        var other = Tabs.FirstOrDefault(t => !ReferenceEquals(t, tab)
            && string.Equals(t.FullPath, newPath, StringComparison.OrdinalIgnoreCase));
        if (other is { IsDirty: true })
        {
            StatusMessage = T("ide.saveAsOpenDirty", other.FileName);
            return false;
        }

        var result = await _workspace.SaveCopyAsAsync(tab, newPath);
        if (result.Outcome == SaveOutcome.CannotEncode)
        {
            if (!AskConvertToUtf8(tab)) { StatusMessage = T("ide.notSaved", tab.FileName); return false; }
            result = await _workspace.SaveCopyAsAsync(tab, newPath);
        }
        if (result.Outcome != SaveOutcome.Saved)
        {
            if (result.Outcome == SaveOutcome.Failed) _log?.Warn("Editor", $"Save As failed: {newPath} — {result.Error}");
            StatusMessage = result.Outcome == SaveOutcome.NotUtf8 ? T("ide.notUtf8", tab.FileName) : T("ide.saveFailed", Path.GetFileName(newPath));
            return false;
        }

        var fresh = await _workspace.OpenFileAsync(newPath);
        if (fresh == null) { StatusMessage = T("ide.cantOpen", Path.GetFileName(newPath)); return false; }
        OnUi(() =>
        {
            if (other != null) Tabs.Remove(other);
            int idx = Tabs.IndexOf(tab);
            if (idx >= 0) Tabs[idx] = fresh; else Tabs.Add(fresh);
            ActiveTab = fresh;
        });
        StatusMessage = T("ide.savedAs", fresh.FileName);
        if (_workspace.HasWorkingDirectory) _ = RefreshTreeAsync();
        return true;
    }

    /// <summary>Close the active tab (Ctrl+W) — unsaved edits ask first, as the ✕ does.</summary>
    public void CloseActiveTab()
    {
        if (_activeTab != null && CloseTabCommand.CanExecute(_activeTab)) CloseTabCommand.Execute(_activeTab);
    }

    /// <summary>Status bar ▸ encoding: keep the text, write it in another encoding on the next save.</summary>
    public void SetActiveEncoding(int codePage, bool utf8Bom)
    {
        if (_activeTab == null) return;
        if (_activeTab is MediaFileTab) { StatusMessage = T("media.status.notText", _activeTab.FileName); return; }   // its bytes are never re-encoded as text
        if (!_activeTab.DecodedFaithfully)
        {
            StatusMessage = T("ide.notUtf8", _activeTab.FileName);
            return;
        }
        _activeTab.SetEncoding(codePage, utf8Bom);
        StatusMessage = T("ide.encodingSet", _activeTab.EncodingLabel);
    }

    // ── View toggles, remembered across restarts ──
    public bool EditorWordWrap
    {
        get => _settings.Settings.EditorWordWrap;
        set => _settings.UpdateSettings(s => s.EditorWordWrap = value);
    }
    public bool EditorShowWhitespace
    {
        get => _settings.Settings.EditorShowWhitespace;
        set => _settings.UpdateSettings(s => s.EditorShowWhitespace = value);
    }
    // Ctrl+wheel / Ctrl+Plus fire once per notch (about 30 a second on key repeat), and each settings save is a
    // full write plus every SettingsChanged listener on the UI thread, so the zoom stuttered notch by notch
    // (2026-11-02). The font size changes at once in the view; the value is written once the zooming stops.
    private double? _pendingZoom;
    private System.Windows.Threading.DispatcherTimer? _zoomSave;

    public double EditorZoom
    {
        get => _pendingZoom ?? Math.Clamp(_settings.Settings.EditorZoom is > 0 ? _settings.Settings.EditorZoom : 1.0, 0.5, 3.0);
        set
        {
            double zoom = Math.Clamp(value, 0.5, 3.0);
            if (Math.Abs(zoom - EditorZoom) < 1e-6) return;   // wheeling past 50% / 300% changes nothing
            _pendingZoom = zoom;
            if (_zoomSave == null)
            {
                _zoomSave = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _zoomSave.Tick += (_, _) => SaveZoomNow();
            }
            _zoomSave.Stop();   // restart: save once the notches stop
            _zoomSave.Start();
        }
    }

    /// <summary>Write a zoom still waiting for its debounce (the timer, and app exit via Dispose).</summary>
    private void SaveZoomNow()
    {
        _zoomSave?.Stop();
        if (_pendingZoom is not { } zoom) return;
        _pendingZoom = null;
        if (Math.Abs(zoom - _settings.Settings.EditorZoom) < 1e-6) return;
        _settings.UpdateSettings(s => s.EditorZoom = zoom);
    }

    /// <summary>The file's encoding cannot hold what was typed: convert it to UTF-8? (true = converted)</summary>
    private bool AskConvertToUtf8(OpenFileTab tab)
    {
        int answer = ChoiceDialog.Show(T("ide.cannotEncodeTitle"), T("ide.cannotEncodeBody", tab.FileName, tab.EncodingLabel),
            new[] { T("ide.convertUtf8"), T("ide.cancel") }, defaultIndex: 1);
        if (answer != 0) return false;
        tab.SetEncoding(0, false);
        return true;
    }
}
