using CluadeX.Services.Media;

namespace CluadeX.Models;

/// <summary>
/// An editor tab showing an image, a song or a clip in the media editor (Views/Editor/MediaEditorHost) instead of
/// AvalonEdit. It is an <see cref="OpenFileTab"/> so the tab strip, close, Ctrl+W and the unsaved-changes questions
/// treat it like any file — but it has no text: <see cref="OpenFileTab.Content"/> stays empty, and the workspace's
/// text save refuses it (writing "" over a photo would destroy it). The edits live in the page; the page reports
/// whether there are unsaved ones (<see cref="OpenFileTab.IsDirty"/>) and the host saves them.
/// </summary>
public sealed class MediaFileTab : OpenFileTab
{
    public MediaKind Kind { get; init; }

    /// <summary>The file's size when it was opened (bytes).</summary>
    public long ByteLength { get; set; }

    /// <summary>The page showing this tab, while there is one (set by the editor view; null in a harness without a view).</summary>
    public IMediaEditorSession? Session { get; set; }

    private bool _isBusy;
    /// <summary>A save or an ffmpeg cut is running for this tab: closing it asks first, and a second save waits.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set { if (_isBusy != value) { _isBusy = value; Raise(nameof(IsBusy)); } }
    }
}

/// <summary>What the editor's view model asks of the page showing a media tab.</summary>
public interface IMediaEditorSession
{
    /// <summary>Save the edits as a new file next to the original (Ctrl+S, "Save" in the close question). True when
    /// they are on disk (or there was nothing to save).</summary>
    Task<bool> SaveCopyAsync();

    /// <summary>Save the edits to <paramref name="path"/> (Save As — the file dialog already asked about replacing).</summary>
    Task<bool> SaveToAsync(string path);

    /// <summary>Stop playback (the tab was left, the page hidden).</summary>
    void Pause();

    /// <summary>The file changed on disk and the tab has no edits: show the new one.</summary>
    void ReloadFromDisk();

    /// <summary>Stop a running save or cut (its partial file is removed).</summary>
    void CancelWork();
}
