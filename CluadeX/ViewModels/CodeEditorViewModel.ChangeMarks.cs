using System.IO;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Problems;

namespace CluadeX.ViewModels;

/// <summary>
/// The editor's change marks, the parts that need the workspace (owner 2026-11-09: "มี compare ก่อนหลังได้ การบันทึก ทำได้ในโหมด
/// edit ปกติ ถ้าคุณแก้โค๊ดเอง ก็ต้องระบายไว้อีกสี … มาร์กคอมเม้นต์ไว้เอง … และสามารถนำมาค้นหาภายหลังได้"):
///   • a save remembers which lines the user wrote by hand (<see cref="UserEditStore"/>) and, when the setting is on, puts a
///     <see cref="UserEditMarker"/> comment on each run of them first — saving never touches the AI's marks, which come from
///     the restore points (CheckpointService.BeforeSession), not from the file;
///   • the before / after compare in the diff panel;
///   • "Show every user edit in the project" in the search panel.
/// </summary>
public partial class CodeEditorViewModel
{
    /// <summary>A tab's user edits were remembered (after a save): the editor repaints its marks. Arg: the file.</summary>
    public event Action<string>? ChangeMarksChanged;

    /// <summary>Settings ▸ the [user-edit] comment on save (default on).</summary>
    public bool UserEditMarkers
    {
        get => _settings.Settings.EditorUserEditMarkers;
        set
        {
            if (_settings.Settings.EditorUserEditMarkers == value) return;
            _settings.UpdateSettings(s => s.EditorUserEditMarkers = value);
            OnPropertyChanged();
            StatusMessage = T(value ? "cm.markersOn" : "cm.markersOff");
        }
    }

    /// <summary>What a save started from: the text on disk before it, and the text written.</summary>
    private sealed record UserEditSave(string Path, string Disk, string Saved, int Marked);

    private const int UserEditMaxChars = 2_000_000;

    /// <summary>
    /// Before a save of <paramref name="tab"/>: put the [user-edit] comments on the runs of lines typed since the file was
    /// last opened or saved (into the tab, so the editor shows them and Ctrl+Z takes them back). Null when nothing was typed.
    /// </summary>
    private UserEditSave? PrepareUserEditSave(OpenFileTab tab)
    {
        try
        {
            if (tab is MediaFileTab) return null;
            tab.FlushPendingEdits();
            string disk = tab.OriginalContent, now = tab.Content;
            if (string.Equals(disk, now, StringComparison.Ordinal) || now.Length > UserEditMaxChars || disk.Length > UserEditMaxChars) return null;
            int marked = 0;
            if (_settings.Settings.EditorUserEditMarkers && UserEditMarker.StyleFor(tab.FullPath) != null)
            {
                var runs = ChangeMarks.EditedRuns(disk, now);
                var (withMarkers, lines) = UserEditMarker.AddMarkers(now, tab.FullPath, runs, DateTime.Now);
                if (lines.Count > 0)
                {
                    tab.Content = withMarkers;
                    now = withMarkers;
                    marked = lines.Count;
                }
            }
            return new UserEditSave(tab.FullPath, disk, now, marked);
        }
        catch (Exception ex)
        {
            _log?.Warn("Editor", $"user-edit marks before saving {tab.FullPath}", ex);
            return null;
        }
    }

    /// <summary>After the save landed: remember the lines (dates kept for the ones already remembered).</summary>
    private void CommitUserEditSave(UserEditSave? save, OpenFileTab tab)
    {
        if (save == null) return;
        try
        {
            // The text saved is the tab's text now (a CannotEncode retry may have saved it a second time: the same text).
            UserEditStore.Shared.Remember(save.Path, save.Disk, tab.Content, DateTime.Now);
            if (save.Marked > 0) StatusMessage = T("cm.saved", tab.FileName, save.Marked);
            ChangeMarksChanged?.Invoke(save.Path);
        }
        catch (Exception ex) { _log?.Warn("Editor", $"could not remember the user's edits in {save.Path}", ex); }
    }

    /// <summary>The before / after of <paramref name="tab"/> in the diff panel: <paramref name="before"/> (the file before this
    /// chat changed it, or the disk text) against what the editor holds now.</summary>
    public async Task ShowChangesDiffAsync(OpenFileTab tab, string before, string leftCaption, bool againstDisk)
    {
        tab.FlushPendingEdits();
        string now = tab.Content;
        var diff = await Task.Run(() => DiffService.BuildSideBySide(before, now));
        OnUi(() =>
        {
            if (HasAutoCommentPreview) EndAutoCommentPreview();
            DiffRows = diff.Rows;
            DiffTitle = tab.FullPath;
            DiffSummary = diff.HasChanges
                ? T(againstDisk ? "cm.diffSummaryDisk" : "cm.diffSummary", diff.Added.ToString("N0"), diff.Removed.ToString("N0"))
                  + (diff.Truncated ? $" · ⚠ {diff.Note}" : "")
                : diff.Note ?? T("ide.diffNoChanges");
            DiffLeftCaption = leftCaption;
            DiffRightCaption = T("cm.rightCaption");
            IsDiffViewActive = true;
        });
    }

    /// <summary>The diff panel's column captions back to HEAD / working tree (another diff opens, or it closes).</summary>
    private void ResetDiffCaptions()
    {
        DiffLeftCaption = null!;
        DiffRightCaption = null!;
    }

    /// <summary>Every place the user edited by hand in the project, listed in the search panel (click = open there).</summary>
    public async Task ShowUserEditsAsync()
    {
        if (!_workspace.HasWorkingDirectory) { StatusMessage = T("cm.noProject"); return; }
        string root = _workspace.WorkingDirectory;
        // Open tabs count as they are in the editor (unsaved typing included).
        var open = Tabs.Where(t => t is not MediaFileTab)
            .GroupBy(t => Path.GetFullPath(t.FullPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Content, StringComparer.OrdinalIgnoreCase);
        StatusMessage = T("cm.searching");
        try
        {
            var spots = await Task.Run(() => UserEditStore.Shared.FindInProject(root, p => open.TryGetValue(p, out var t) ? t : null));
            var locations = spots.Select(s => new CodeLocation
            {
                RelPath = s.RelPath, FullPath = s.FullPath, Line = s.Line,
                Snippet = (s.Date.Length > 0 ? $"[{s.Date}] " : "") + s.Text,
            }).ToList();
            ShowLocations(locations, WorkbenchSearchMode.UserEdits, T("cm.listContext", locations.Count, UserEditMarker.Tag + "…]"));
            StatusMessage = T("cm.listContext", locations.Count, UserEditMarker.Tag + "…]");
        }
        catch (Exception ex)
        {
            _log?.Warn("Editor", "listing the user's edits failed", ex);
            StatusMessage = T("cm.searchFailed");
        }
    }
}
