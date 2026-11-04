using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using CluadeX.Models;
using CluadeX.ViewModels;
using CluadeX.Views.Editor;

namespace CluadeX.Views;

/// <summary>
/// Image / audio / video tabs (CodeEditorViewModel.Media.cs): where AvalonEdit sits, the active media tab's
/// <see cref="MediaEditorHost"/> shows. One host per open media tab, kept while the tab is open (switching tabs keeps
/// each page's edits; a tab that is left is paused) and closed when the tab leaves the strip. The hosts live in this
/// view, so they move with it into the page's own window (Views/PopOut) and back.
/// The page is a native window — WPF cannot draw over it — so it is hidden while something should be on top: the
/// side-by-side diff, the command palette, the model-loading cover (the same overlays the UI Studio steps aside for).
/// </summary>
public partial class CodeEditorView
{
    private readonly Dictionary<MediaFileTab, MediaEditorHost> _mediaHosts = new();
    private CodeEditorViewModel? _mediaVm;
    private readonly List<(INotifyPropertyChanged Source, PropertyChangedEventHandler Handler)> _mediaOverlayWatch = new();
    private bool _mediaCovered;

    /// <summary>The hosts open now (for the harness).</summary>
    public IReadOnlyCollection<MediaEditorHost> MediaHosts => _mediaHosts.Values;

    /// <summary>Is the media area on screen (for the harness)?</summary>
    public bool ShowsMedia => MediaArea.Visibility == Visibility.Visible;

    private void AttachMedia(CodeEditorViewModel vm)
    {
        if (ReferenceEquals(_mediaVm, vm)) return;
        DetachMedia();
        _mediaVm = vm;
        vm.Tabs.CollectionChanged += OnTabsChangedForMedia;
        ShowMediaFor(vm.ActiveTab);
    }

    /// <summary>The view is really gone (not moved to another window): close every page. Their tabs stay open and get a
    /// new page when the editor shows them again.</summary>
    private void DetachMedia()
    {
        if (_mediaVm != null) _mediaVm.Tabs.CollectionChanged -= OnTabsChangedForMedia;
        _mediaVm = null;
        foreach (var host in _mediaHosts.Values) host.Close();
        _mediaHosts.Clear();
        MediaArea.Children.Clear();
        MediaArea.Visibility = Visibility.Collapsed;
        UnwatchMediaOverlays();
    }

    private void OnTabsChangedForMedia(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_mediaVm == null) return;
        // whatever left the strip (closed, replaced by Save As / Edit as text, a reset) takes its page with it
        var open = new HashSet<OpenFileTab>(_mediaVm.Tabs);
        foreach (var (tab, host) in _mediaHosts.Where(p => !open.Contains(p.Key)).ToList())
        {
            _mediaHosts.Remove(tab);
            MediaArea.Children.Remove(host);
            host.Close();
        }
    }

    /// <summary>The active tab changed: show its page (made the first time), hide and pause the others.</summary>
    private void ShowMediaFor(OpenFileTab? tab)
    {
        MediaEditorHost? shown = null;
        if (tab is MediaFileTab media && _mediaVm != null)
        {
            if (!_mediaHosts.TryGetValue(media, out shown))
            {
                shown = new MediaEditorHost(media, _mediaVm);
                _mediaHosts[media] = shown;
                MediaArea.Children.Add(shown);
            }
        }
        foreach (var host in _mediaHosts.Values)
        {
            if (ReferenceEquals(host, shown)) continue;
            if (host.Visibility != Visibility.Collapsed) host.Pause();
            host.Visibility = Visibility.Collapsed;
        }
        if (shown != null)
        {
            shown.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(shown.Activate));
        }
        UpdateMediaVisibility();
        UpdateTextOnlyPanels(shown != null);
    }

    private bool _findHiddenForMedia;

    /// <summary>
    /// The panels that work on the text in AvalonEdit step aside while a media tab is active and come back on the next
    /// text tab (owner 2026-11-04: "ในเครื่องมือภาพ/วีดีโอ ยังเห็นเครื่องมือของ editor อยู่ … ควรซ่อนไหม"). The find /
    /// replace / go-to bar would search the emptied editor; the Problems panel lists the bound buffer's diagnostics.
    /// The toolbar's text tools hide by binding (IsMediaTabActive), the status bar and minimap by IsTextTabActive.
    /// </summary>
    private void UpdateTextOnlyPanels(bool media)
    {
        if (media)
        {
            if (FindBar.Visibility == Visibility.Visible)
            {
                _findHiddenForMedia = true;
                FindBar.Visibility = Visibility.Collapsed;
                _countTimer?.Stop();
            }
            ProblemsPanel.Visibility = Visibility.Collapsed;   // s_problemsOpen keeps the user's choice
            CloseHoverTip();
            return;
        }
        if (_findHiddenForMedia && _boundTab != null)
        {
            _findHiddenForMedia = false;
            FindBar.Visibility = Visibility.Visible;
            RefreshFind(jump: false);
        }
        bool problems = s_problemsOpen;
        if ((ProblemsPanel.Visibility == Visibility.Visible) != problems)
        {
            ProblemsPanel.Visibility = problems ? Visibility.Visible : Visibility.Collapsed;
            if (problems) RefreshLsp();
        }
    }

    /// <summary>The media area is shown for a media tab, unless the diff covers the code area or a main-window overlay
    /// is up (Hidden, not Collapsed: the page stays alive and laid out).</summary>
    private void UpdateMediaVisibility()
    {
        bool media = _mediaVm is { IsMediaTabActive: true };
        if (!media) { MediaArea.Visibility = Visibility.Collapsed; return; }
        bool covered = _mediaCovered || _mediaVm!.IsDiffViewActive;
        MediaArea.Visibility = covered ? Visibility.Hidden : Visibility.Visible;
    }

    /// <summary>The page left the screen (another page, the window minimised away): nothing keeps playing.</summary>
    private void PauseMedia()
    {
        foreach (var host in _mediaHosts.Values) host.Pause();
    }

    private void OnMediaVmPropertyChanged(string? property)
    {
        if (property == nameof(CodeEditorViewModel.IsDiffViewActive)) UpdateMediaVisibility();
    }

    // ─── what may cover the page (the main window's overlays) ───

    private void WatchMediaOverlays()
    {
        UnwatchMediaOverlays();
        var window = Window.GetWindow(this);
        if (window?.DataContext is not INotifyPropertyChanged main) return;
        PropertyChangedEventHandler onMain = (_, e) =>
        {
            if (e.PropertyName is null or "" or "IsModelLoading" or "Palette") { WatchMediaPalette(main); UpdateMediaCovered(main); }
        };
        main.PropertyChanged += onMain;
        _mediaOverlayWatch.Add((main, onMain));
        WatchMediaPalette(main);
        UpdateMediaCovered(main);
    }

    private void WatchMediaPalette(INotifyPropertyChanged main)
    {
        for (int i = _mediaOverlayWatch.Count - 1; i >= 1; i--)
        {
            _mediaOverlayWatch[i].Source.PropertyChanged -= _mediaOverlayWatch[i].Handler;
            _mediaOverlayWatch.RemoveAt(i);
        }
        if (ReadProperty(main, "Palette") is INotifyPropertyChanged palette)
        {
            PropertyChangedEventHandler onPalette = (_, e) => { if (e.PropertyName is null or "" or "IsOpen") UpdateMediaCovered(main); };
            palette.PropertyChanged += onPalette;
            _mediaOverlayWatch.Add((palette, onPalette));
        }
    }

    private void UnwatchMediaOverlays()
    {
        foreach (var (source, handler) in _mediaOverlayWatch) source.PropertyChanged -= handler;
        _mediaOverlayWatch.Clear();
        _mediaCovered = false;
    }

    private void UpdateMediaCovered(object main)
    {
        void Apply()
        {
            _mediaCovered = ReadProperty(main, "IsModelLoading") is true || ReadProperty(ReadProperty(main, "Palette"), "IsOpen") is true;
            UpdateMediaVisibility();
        }
        if (Dispatcher.CheckAccess()) Apply();
        else Dispatcher.BeginInvoke(new Action(Apply));
    }

    private static object? ReadProperty(object? source, string property)
    {
        if (source == null) return null;
        try { return source.GetType().GetProperty(property)?.GetValue(source); }
        catch { return null; }
    }
}
