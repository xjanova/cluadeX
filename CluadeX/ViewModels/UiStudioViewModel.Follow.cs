using System.IO;
using System.Windows;
using System.Windows.Threading;
using CluadeX.Services.UiStudio;
using CluadeX.Services.UiStudio.Pages;

namespace CluadeX.ViewModels;

/// <summary>
/// The Studio shows the project of the chat on screen — from the moment the chat is opened, not when the Studio page
/// is first visited — so the preview is ready the first time it is looked at. What it does while nobody looks is kept
/// small: opening the project and starting a cheap preview (a static site, a site that already runs). A dev server
/// is started early only when its packages are already installed; an install and the Flutter screens wait for the
/// page to be shown. Drawing work for a change (re-rendering screens) waits too, and catches up on show.
/// (Owner, 2026-10-19: "ควรเปิดทันทีที่โปรเจคช่องแชทเปิด … ถ้าไม่ดูก็ไม่ต้องทำงาน เพื่อลดทรัพยากร แต่เมื่อคลิ๊กไปดูต้องพร้อมดูทันที")
/// </summary>
public sealed partial class UiStudioViewModel
{
    private DispatcherTimer? _followTimer;
    private bool _startOnShow, _pagesOnShow;
    private bool? _onScreen;

    /// <summary>Ask the window to build the Studio page now (hidden), so its browser is up and showing the project
    /// before the user first opens it.</summary>
    public event Action? PrewarmRequested;

    /// <summary>The Studio has a browser preview worth building early (a web page, not simulated screens).</summary>
    public bool WantsPrewarm => _studio.Project.Kind is StudioProjectKind.Static or StudioProjectKind.Vite or StudioProjectKind.NodeOther or StudioProjectKind.Server;

    /// <summary>True while the Studio page is on screen. Unknown (no view has reported yet — a harness drives the
    /// view model alone) counts as on screen, so nothing waits for a view that never comes.</summary>
    public bool IsOnScreen => _onScreen ?? true;

    /// <summary>The view reports whether the Studio page is on screen.</summary>
    public void SetOnScreen(bool onScreen)
    {
        _onScreen = onScreen;
        _chat.ProjectPageOnScreen = onScreen || _graphOnScreen;
        _studio.PageVisible = onScreen;
        if (onScreen) CatchUpOnShow();
    }

    /// <summary>The Node Graph's page is on screen (see <see cref="ChatViewModel.ProjectPageOnScreen"/>).</summary>
    internal void SetGraphOnScreen(bool onScreen)
    {
        _graphOnScreen = onScreen;
        _chat.ProjectPageOnScreen = _onScreen == true || _graphOnScreen;
    }
    private bool _graphOnScreen;

    private void HookAppProject()
    {
        _chat.ChatProjectChanged += OnChatProjectChanged;
        // A folder the chat opened while still empty (the AI makes the site after opening it) stayed "Unsupported" for
        // good, so the Studio never showed the page it then wrote (owner 2026-10-20). Look again when files appear.
        _studio.FilesChanged += _ => RedetectAfterFilesAppeared();
        // the chat restored at start-up: followed once the window is up, after the start-up work
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => FollowChat());
    }

    private void OnChatProjectChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) FollowChat();
        else dispatcher.BeginInvoke(() => FollowChat());
    }

    /// <summary>An "Unsupported" project that files were just written into: detect it again, and start it the way a
    /// followed project starts (a static site at once). Runs on the dispatcher (FilesChanged is raised by its timer).</summary>
    internal void RedetectAfterFilesAppeared()
    {
        if (!_studio.HasProject || !IsUnsupported || _session != null || IsChecking) return;
        if (StudioProject.Detect(_studio.Root).Kind == StudioProjectKind.Unsupported) return;
        _studio.OpenProject(_studio.Root);   // the same root: detected again
        StartFollowed();
        _log?.Info("UiStudio", $"files appeared in {_studio.Root}: now {_studio.Project.Kind}");
    }

    /// <summary>Chats switch in bursts (the arrow keys in a list, a restore): the preview starts on the project it
    /// settles on. The Studio itself is already there — see <see cref="FollowChat"/>.</summary>
    private void QueueStart()
    {
        _followTimer ??= NewFollowTimer();
        _followTimer.Stop();
        _followTimer.Start();
    }

    private DispatcherTimer NewFollowTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        t.Tick += (_, _) => { t.Stop(); if (_studio.HasProject) StartFollowed(); };
        return t;
    }

    /// <summary>Be on the project of the chat on screen, and nothing else: another chat's project is left at once,
    /// a chat without one shows the empty page. Late results of the project left are dropped by what they each hold
    /// (<see cref="_pagesCts"/>, the root they read); a review round waits for the user to come back.</summary>
    internal void FollowChat(bool startPreview = true)
    {
        OnPropertyChanged(nameof(CurrentAppProject));
        OnPropertyChanged(nameof(HasAppProject));
        string p = _chat.ChatProject;
        if (p.Length > 0 ? _studio.HasProject && SamePath(_studio.Root, p) : !_studio.HasProject)
        {
            if (!startPreview) _followTimer?.Stop();
            return;
        }
        _followTimer?.Stop();
        ParkRound();
        _startOnShow = _pagesOnShow = false;
        ShowGallery = false;
        if (p.Length == 0)
        {
            _studio.CloseProject();
            _log?.Info("UiStudio", "the chat on screen has no project");
            return;
        }
        _studio.OpenProject(p);
        TakeParkedRound();
        _log?.Info("UiStudio", $"followed the chat's project {p} ({_studio.Project.Kind})");
        if (startPreview) QueueStart();
    }

    /// <summary>Start what a followed project shows, by its kind: only the cheap and safe things run unasked.</summary>
    private void StartFollowed()
    {
        ShowGallery = false;
        _startOnShow = _pagesOnShow = false;
        switch (_studio.Project.Kind)
        {
            case StudioProjectKind.Static:
                _ = StartAsync();   // the Studio's own in-process file server: nothing of the project's is run
                break;
            case StudioProjectKind.Server:
                // a site that already runs (Laragon, artisan serve): shown at once when it is on this machine; an
                // internet APP_URL from the project's .env is never opened unasked
                if (IsLocalUrl(_studio.ServerBaseUrl ?? LaravelPages.AppUrl(_studio.Root) ?? "http://localhost:8000/")) _ = StartAsync();
                break;
            case StudioProjectKind.Vite:
            case StudioProjectKind.NodeOther:
                // `npm run dev` runs the project's own scripts: in the background only for a project whose dev server
                // the user has started from the Studio before (and whose packages are installed — no install unasked).
                // Otherwise it starts by itself on the first look, through the same safety gates as the Start button.
                _store ??= new PageShotStore(_studio.DataRoot, _studio.Root);
                if (_store.State.DevServerApproved && Directory.Exists(Path.Combine(_studio.Root, "node_modules"))) _ = StartAsync();
                else _startOnShow = true;
                break;
            case StudioProjectKind.Simulated:
                _pagesOnShow = true;   // reading and drawing the screens (and asking to render them) waits for a look
                break;
        }
        if (_onScreen == true) CatchUpOnShow();
        else if (WantsPrewarm) PrewarmRequested?.Invoke();
    }

    /// <summary>localhost, a loopback address, or a local dev domain (.test / .local / .lan / .localhost).</summary>
    public static bool IsLocalUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
        && (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith(".test", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase));

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                      b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    /// <summary>The page came on screen: do what was left for this moment.</summary>
    private void CatchUpOnShow()
    {
        if (_startOnShow && _studio.HasProject)
        {
            _startOnShow = false;
            if (!_studio.IsServing && !_studio.IsStarting) _ = StartAsync();
        }
        if (_pagesOnShow && _studio.HasProject)
        {
            _pagesOnShow = false;
            ShowPages = true;
        }
        // code changed while the screens were hidden: draw them again now
        if (_sourcesChanged && _catalog?.Flutter != null)
        {
            _renderDebounce ??= NewRenderDebounce();
            _renderDebounce.Stop();
            _renderDebounce.Start();
        }
    }
}
