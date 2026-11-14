using System.IO;
using CluadeX.Models;
using CluadeX.Services.Media;
using CluadeX.Views.Editor;

namespace CluadeX.ViewModels;

/// <summary>
/// Images, audio and video in the editor (owner, 2026-11-03: open a work file that is a picture → show it and allow
/// basic edits; audio plays with a waveform and can be cut; video the same). A media file opens as a
/// <see cref="MediaFileTab"/> — a tab like any other in the strip — and the view shows the media editor page where
/// AvalonEdit would be. The page holds the edits; this side decides what a save may do: a copy never takes an
/// existing name, and overwriting the original always asks first (owner rule: destructive actions need a confirm).
/// </summary>
public partial class CodeEditorViewModel
{
    /// <summary>The active tab is text (AvalonEdit, minimap, the text status bar show).</summary>
    public bool IsTextTabActive => _activeTab != null && _activeTab is not MediaFileTab;

    /// <summary>The active tab is an image / audio / video (the media editor shows).</summary>
    public bool IsMediaTabActive => _activeTab is MediaFileTab;

    /// <summary>Where the media editor's WebView2 keeps its profile (beside the Node Graph's and the Studio's).</summary>
    public string MediaWebDataFolder => Path.Combine(_settings.DataRoot, "MediaEditor", "WebView2");

    /// <summary>The data folder (the privacy blur keeps its two detector models under Models\privacy).</summary>
    public string DataRoot => _settings.DataRoot;

    /// <summary>For the media host: localized text (the page gets all of its words from here), and the service whose
    /// LanguageChanged tells it to send them again.</summary>
    public string Loc(string key) => T(key);
    public string Loc(string key, params object[] args) => T(key, args);
    public CluadeX.Services.LocalizationService Localization => _loc;

    /// <summary>For the media host: something worth a line in the Debug Log.</summary>
    public void LogMedia(string message, Exception? ex = null)
    {
        if (ex == null) _log?.Info("Media", message);
        else _log?.Warn("Media", message, ex);
    }

    /// <summary>
    /// Open <paramref name="fullPath"/> as a media tab when its extension is an image, audio or video (null: not
    /// media — the caller opens it as text). Nothing is read here: the page streams the file itself (a 2 GB video
    /// opens without being loaded into memory). An empty file still opens; the page says it is empty.
    /// </summary>
    private async Task<bool?> TryOpenMediaAsync(string fullPath)
    {
        var kind = MediaFiles.KindOf(fullPath);
        if (kind == MediaKind.None) return null;
        long length;
        try
        {
            length = await Task.Run(() => File.Exists(fullPath) ? new FileInfo(fullPath).Length : -1);
        }
        catch (Exception ex)
        {
            _log?.Warn("Media", $"Open failed: {fullPath}", ex);
            length = -1;
        }
        if (length < 0)
        {
            StatusMessage = T("media.status.gone", Path.GetFileName(fullPath));
            return false;
        }
        OnUi(() =>
        {
            // Opened meanwhile (a double-click, the palette and the tree at once): one tab per file.
            var existing = Tabs.FirstOrDefault(t => string.Equals(t.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { ActiveTab = existing; return; }
            var tab = new MediaFileTab { FullPath = fullPath, Kind = kind, ByteLength = length };
            tab.MarkOpened("");
            Tabs.Add(tab);
            ActiveTab = tab;
            StatusMessage = T("media.status.opened", tab.FileName, MediaFiles.SizeText(length));
        });
        return true;
    }

    /// <summary>Ctrl+S / "Save" in the close question on a media tab: the edits go to a new file next to the original.</summary>
    private async Task<bool> SaveMediaInteractiveAsync(MediaFileTab tab)
    {
        if (!tab.IsDirty) { StatusMessage = T("media.status.nothingToSave", tab.FileName); return true; }
        if (tab.Session == null) { StatusMessage = T("media.status.noEditor", tab.FileName); return false; }
        if (tab.IsBusy) { StatusMessage = T("media.status.busy", tab.FileName); return false; }
        return await tab.Session.SaveCopyAsync();
    }

    /// <summary>Save As on a media tab: the edits go to the path the file dialog chose (it already asked about replacing).</summary>
    private async Task<bool> SaveMediaAsAsync(MediaFileTab tab, string newPath)
    {
        if (tab.Session == null) { StatusMessage = T("media.status.noEditor", tab.FileName); return false; }
        if (tab.IsBusy) { StatusMessage = T("media.status.busy", tab.FileName); return false; }
        var other = Tabs.FirstOrDefault(t => !ReferenceEquals(t, tab) && string.Equals(t.FullPath, newPath, StringComparison.OrdinalIgnoreCase));
        if (other is { IsDirty: true }) { StatusMessage = T("ide.saveAsOpenDirty", other.FileName); return false; }
        return await tab.Session.SaveToAsync(newPath);
    }

    /// <summary>
    /// Overwrite the original file with the edits? Always asked — the answer defaults to Cancel, and Esc is Cancel.
    /// (The owner's rule for anything destructive: a confirmation first, every time.)
    /// </summary>
    public bool ConfirmMediaOverwrite(MediaFileTab tab)
    {
        int answer = ChoiceDialog.Show(T("media.overwrite.title"), T("media.overwrite.body", tab.FileName),
            new[] { T("media.overwrite.yes"), T("ide.cancel") }, defaultIndex: 1);
        return answer == 0;
    }

    /// <summary>A media tab is closing while its save or cut still runs: stop it and close, or keep the tab.</summary>
    private bool ConfirmStopMediaWork(MediaFileTab tab)
    {
        int answer = ChoiceDialog.Show(T("media.busyClose.title"), T("media.busyClose.body", tab.FileName),
            new[] { T("media.busyClose.stop"), T("ide.cancel") }, defaultIndex: 1);
        if (answer != 0) return false;
        tab.Session?.CancelWork();
        return true;
    }

    /// <summary>
    /// The host saved <paramref name="tab"/>'s edits to <paramref name="savedPath"/>. The tree shows the new file at
    /// once; a saved copy of the edits opens in its own tab (the original tab is clean again — its edits are kept
    /// in the copy), a snapshot of a frame just says where it went.
    /// </summary>
    public void MediaSaved(MediaFileTab tab, string savedPath, bool overwrote, bool openCopy)
    {
        StatusMessage = overwrote ? T("media.status.overwritten", tab.FileName) : T("media.status.savedCopy", Path.GetFileName(savedPath));
        if (!TreeContains(savedPath)) RefreshAncestorFolder(savedPath);
        QueueGitRefresh();
        if (!overwrote && openCopy && MediaFiles.IsMedia(savedPath))
            _ = OpenPathCoreAsync(savedPath);
    }

    /// <summary>A save did not happen; <paramref name="key"/> says why in the UI language.</summary>
    public void MediaSaveFailed(MediaFileTab tab, string key) => StatusMessage = T(key, tab.FileName);

    /// <summary>The page asked for the SVG's source: open it as text in place of the picture (unsaved picture edits —
    /// none are possible on an SVG — are not a concern).</summary>
    public async Task OpenMediaAsTextAsync(MediaFileTab tab)
    {
        var text = await _workspace.OpenFileAsync(tab.FullPath);
        if (text == null) { StatusMessage = T("ide.cantOpen", tab.FileName); return; }
        OnUi(() =>
        {
            int idx = Tabs.IndexOf(tab);
            if (idx >= 0) Tabs[idx] = text; else Tabs.Add(text);
            ActiveTab = text;
        });
    }

    /// <summary>The file under a media tab changed on disk: a tab without edits shows the new file, one with edits keeps
    /// them (its save goes to a copy anyway) and says so.</summary>
    private void NoteMediaChangedOnDisk(MediaFileTab tab)
    {
        if (tab.IsBusy) return;   // our own save landing
        if (tab.IsDirty) { StatusMessage = T("ide.changedOnDiskDirty", tab.FileName); return; }
        tab.Session?.ReloadFromDisk();
        if (ReferenceEquals(tab, ActiveTab)) StatusMessage = T("ide.changedOnDisk", tab.FileName);
    }
}
