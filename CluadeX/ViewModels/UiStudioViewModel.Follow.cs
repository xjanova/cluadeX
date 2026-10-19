using System.IO;
using System.Windows;
using System.Windows.Threading;
using CluadeX.Services.UiStudio;
using CluadeX.Services.UiStudio.Pages;

namespace CluadeX.ViewModels;

/// <summary>
/// The Studio follows the project the chat has open — the moment the chat opens it, not when the Studio page is
/// first visited — so the preview is ready the first time it is looked at. What it does while nobody looks is kept
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
        _studio.PageVisible = onScreen;
        if (onScreen) CatchUpOnShow();
    }

    private void HookAppProject()
    {
        _fs.OnWorkingDirectoryChanged += () => Application.Current?.Dispatcher.BeginInvoke(QueueFollow);
        // the project open at start-up (the last chat's): followed once the window is up, after the start-up work
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, QueueFollow);
    }

    /// <summary>Chats switch projects in bursts (the session list, a restore): follow the folder it settles on.</summary>
    private void QueueFollow()
    {
        _followTimer ??= NewFollowTimer();
        _followTimer.Stop();
        _followTimer.Start();
    }

    private DispatcherTimer NewFollowTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        t.Tick += (_, _) => { t.Stop(); FollowAppProject(); };
        return t;
    }

    /// <summary>Open the chat's project in the Studio, unless the Studio is already on it or is in the middle of
    /// something that a switch would throw away (a review round, a save check).</summary>
    internal void FollowAppProject()
    {
        OnPropertyChanged(nameof(CurrentAppProject));
        OnPropertyChanged(nameof(HasAppProject));
        string? p = _fs.AppWorkingDirectory;
        if (string.IsNullOrEmpty(p) || !Directory.Exists(p)) return;
        if (_studio.HasProject && SamePath(_studio.Root, p)) return;
        if (_session != null || IsChecking) return;

        _studio.OpenProject(p);
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
        _log?.Info("UiStudio", $"followed the chat's project {p} ({_studio.Project.Kind})");
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
