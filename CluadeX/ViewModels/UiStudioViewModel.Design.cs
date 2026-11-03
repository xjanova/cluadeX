using System.Windows;
using CluadeX.Services.UiStudio;

namespace CluadeX.ViewModels;

/// <summary>
/// Design mode's way to the Studio (see <see cref="ChatViewModel.DesignViewRequested"/>): the owner sent a design turn,
/// so the Studio asks to come forward — at once when it can show the chat's project, else as soon as it can (a brand-new
/// site: the AI opens an empty folder, then writes its index.html). The wish lasts for that turn of that chat on screen;
/// switching chats or the turn ending drops it, so nothing pulls the window later.
/// </summary>
public sealed partial class UiStudioViewModel
{
    private string? _designChat;

    /// <summary>The Studio can show the design turn's project now: the window should bring this page forward (MainViewModel
    /// does, unless the user went to another page meanwhile). Raised on the UI thread.</summary>
    public event Action? DesignViewReady;

    /// <summary>A design turn is waiting for its project to become something the Studio can show.</summary>
    public bool DesignViewPending => _designChat != null;

    /// <summary>Something the Studio shows: a web preview or an app's screens of the chat's own project.</summary>
    internal bool CanShowForDesign =>
        _studio.HasProject && SamePath(_studio.Root, _chat.ChatProject)
        && _studio.Project.Kind is StudioProjectKind.Static or StudioProjectKind.Vite or StudioProjectKind.NodeOther
            or StudioProjectKind.Server or StudioProjectKind.Simulated;

    private void HookDesignView()
    {
        _chat.DesignViewRequested += chatId =>
        {
            _designChat = chatId;
            // after the window noted the page it was asked from (MainViewModel listens to the same event)
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) TryDesignView();
            else dispatcher.BeginInvoke(TryDesignView);
        };
        // the project became showable: the chat opened one (open_project), files appeared in an empty folder (detected
        // again), the AI turned it into another kind
        _studio.ProjectChanged += TryDesignView;
        _chat.PropertyChanged += (_, e) =>
        {
            if (_designChat == null) return;
            if (e.PropertyName is nameof(ChatViewModel.IsGenerating) or nameof(ChatViewModel.HasRunningElsewhere) or nameof(ChatViewModel.CurrentSession))
                DropDesignWishIfGone();
        };
    }

    private void DropDesignWishIfGone()
    {
        if (_designChat is { } id && (!_chat.IsChatOnScreen(id) || !_chat.IsChatRunning(id))) _designChat = null;
    }

    private void TryDesignView()
    {
        DropDesignWishIfGone();
        if (_designChat == null || !CanShowForDesign) return;
        _designChat = null;
        _log?.Info("UiStudio", "design turn: showing the Studio");
        DesignViewReady?.Invoke();
    }
}
