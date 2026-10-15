using System.Collections.Concurrent;
using System.Windows;
using CluadeX.Services.Browser;
using CluadeX.ViewModels;

namespace CluadeX.Views.Preview;

/// <summary>
/// Puts the page in front of the user: after the agent opens a page, if no Preview pane is on screen the pop-out
/// window opens (owned by the main window) — without taking the keyboard focus from the chat box, once per chat,
/// and never again in a chat where the user closed it (an edit → reload loop must not keep popping a window over
/// the user's typing). The pane's pop-out button always opens / raises it and moves the page into it. Without an
/// Application (a harness) it does nothing.
/// </summary>
public sealed class PreviewPresenter : IPreviewPresenter
{
    private readonly PreviewViewModel _vm;
    private PreviewWindow? _window;
    private string _windowChat = "";
    /// <summary>Chats in which the user closed the pop-out window (it stays closed for the agent in them).</summary>
    private readonly ConcurrentDictionary<string, bool> _dismissed = new(StringComparer.Ordinal);
    /// <summary>Chats in which the window was already popped out once for the agent.</summary>
    private readonly ConcurrentDictionary<string, bool> _shownFor = new(StringComparer.Ordinal);

    public PreviewPresenter(PreviewViewModel vm)
    {
        _vm = vm;
        _vm.PopOut = () => ShowWindow(claim: true, activate: true);
    }

    public bool EnsureVisible(string chatKey)
    {
        chatKey ??= "";
        if (_dismissed.ContainsKey(chatKey)) return false;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return true;
        dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (PreviewPaneHost.AnyVisible) return;
                if (_window != null) return;                       // open (maybe minimized or behind): leave it be
                if (!_shownFor.TryAdd(chatKey, true)) return;      // popped out once in this chat already
                _windowChat = chatKey;
                ShowWindow(claim: true, activate: false);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[preview] could not show the window: {ex.Message}"); }
        });
        return true;
    }

    /// <summary>Open the pop-out window, or bring it forward. <paramref name="claim"/>: move the page into it.
    /// <paramref name="activate"/>: give it the focus (only when the user asked for it). UI thread.</summary>
    public void ShowWindow(bool claim, bool activate)
    {
        if (Application.Current == null) return;
        if (_window != null)
        {
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            if (claim) _window.Pane.ClaimPage();
            if (activate) _window.Activate();
            return;
        }
        var w = new PreviewWindow { ShowActivated = activate };
        var owner = Application.Current.MainWindow;
        if (owner != null && owner.IsLoaded && !ReferenceEquals(owner, w)) w.Owner = owner;
        w.Closed += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_windowChat)) _dismissed[_windowChat] = true;
            _window = null;
            _windowChat = "";
        };
        _window = w;
        if (activate) _windowChat = "";   // the user opened it: closing it is not a "keep it closed for the agent"
        w.Show();
    }

    /// <summary>Harness / app: chats where the user dismissed the window.</summary>
    internal bool IsDismissed(string chatKey) => _dismissed.ContainsKey(chatKey);

    /// <summary>The pop-out window while it is open (harness).</summary>
    internal PreviewWindow? Window => _window;
}
