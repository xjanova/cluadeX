using System.Windows;
using System.Windows.Input;

namespace CluadeX.Views.PopOut;

/// <summary>
/// Moves a page between the main window and a window of its own (see <see cref="PagePopOut"/>). One per main window.
///   · Pop out: the page host gives up its view (the kept Code Editor / UI Studio view itself; the Node Graph, which the
///     host rebuilds on every visit anyway, is built fresh for the window), the window opens with it, and the host shows
///     the placeholder wherever that page is asked for.
///   · Bring back (the window's close box, its own "put back" button, the placeholder's button): the window closes and
///     hands the same view back to the host.
///   · The main window closing closes them all (the app would otherwise keep running with only these on screen).
/// A second pop-out of a page already out only brings its window forward.
/// </summary>
public sealed class PagePopOutManager
{
    private readonly Window _main;
    private readonly PageHost _host;
    private readonly Dictionary<Type, PopOutWindow> _open = new();
    private bool _busy, _mainClosed;

    /// <summary>A page went out or came back (the page's view model).</summary>
    public event Action<object>? Changed;

    /// <summary>A page's window, placed but not shown yet (a harness keeps it off screen and unfocused).</summary>
    public event Action<PopOutWindow>? Opening;

    public PagePopOutManager(Window main, PageHost host)
    {
        _main = main;
        _host = host;
        PagePopOut.HookGraphButton();
        main.CommandBindings.Add(new CommandBinding(PagePopOut.PopOutCommand,
            (_, e) => { e.Handled = true; PopOut(PageOf(e.Parameter, e.OriginalSource)); },
            (_, e) => { e.Handled = true; e.CanExecute = !_mainClosed && PagePopOut.CanPopOut(PageOf(e.Parameter, e.OriginalSource)); }));
        main.CommandBindings.Add(new CommandBinding(PagePopOut.BringBackCommand,
            (_, e) => { e.Handled = true; if (PageOf(e.Parameter, e.OriginalSource) is { } p) BringBack(p); }));
        main.CommandBindings.Add(new CommandBinding(PagePopOut.ShowWindowCommand,
            (_, e) => { e.Handled = true; if (PageOf(e.Parameter, e.OriginalSource) is { } p) Show(p, activate: true); }));
    }

    private static object? PageOf(object? parameter, object? source) =>
        parameter ?? (source as FrameworkElement)?.DataContext;

    /// <summary>Is the page in a window of its own?</summary>
    public bool IsPoppedOut(object? page) => page != null && _open.ContainsKey(page.GetType());

    /// <summary>The page's own window, while it is open.</summary>
    public PopOutWindow? WindowOf(object? page) => page != null && _open.TryGetValue(page.GetType(), out var w) ? w : null;

    /// <summary>The windows open now (harness).</summary>
    public IReadOnlyCollection<PopOutWindow> Windows => _open.Values;

    /// <summary>Open the page in its own window, or bring that window forward when it is already out. Null when the
    /// page cannot float, or the window could not be opened (the page then stays where it was).</summary>
    public PopOutWindow? PopOut(object? page)
    {
        if (page == null || !PagePopOut.CanPopOut(page) || _mainClosed) return null;
        if (WindowOf(page) is { } open) { open.Bring(activate: true); return open; }
        if (_busy) return null;   // (a second click while the first one still opens the window)
        _busy = true;
        FrameworkElement? view = null;
        try
        {
            view = _host.Release(page) ?? _host.BuildView(page);
            if (view == null) { _host.Adopt(page, null); return null; }
            var window = new PopOutWindow(page, view, _main);
            window.Returning += OnReturning;
            _open[page.GetType()] = window;
            Opening?.Invoke(window);
            window.Show();
            Changed?.Invoke(page);
            return window;
        }
        catch (Exception ex)
        {
            // The page goes back where it was; the reason goes to the debug output, not onto the screen.
            System.Diagnostics.Debug.WriteLine($"[popout] could not open {PagePopOut.KeyOf(page)}: {ex.Message}");
            if (_open.TryGetValue(page.GetType(), out var half))
            {
                _open.Remove(page.GetType());
                try { half.Returning -= OnReturning; half.Close(); } catch { }
            }
            if (view != null && view.Parent is System.Windows.Controls.Border b) b.Child = null;
            _host.Adopt(page, view);
            ReportFailure();
            return null;
        }
        finally { _busy = false; }
    }

    /// <summary>Put the page back in the main window (its own window closes).</summary>
    public void BringBack(object page)
    {
        if (WindowOf(page) is { } w) w.Close();   // its Closing hands the view back (OnReturning)
    }

    /// <summary>Bring the page's own window forward (it stays where it is if <paramref name="activate"/> is false and it
    /// is not minimised). False when the page is not out.</summary>
    public bool Show(object page, bool activate)
    {
        if (WindowOf(page) is not { } w) return false;
        w.Bring(activate);
        return true;
    }

    /// <summary>The main window closed: every page window closes with it (each hands its view back first).</summary>
    public void CloseAll()
    {
        _mainClosed = true;
        foreach (var w in _open.Values.ToList())
        {
            try { w.Close(); } catch { }
        }
        _open.Clear();
    }

    private void OnReturning(PopOutWindow window, FrameworkElement? view)
    {
        window.Returning -= OnReturning;
        if (_open.TryGetValue(window.Page.GetType(), out var w) && ReferenceEquals(w, window)) _open.Remove(window.Page.GetType());
        _host.Adopt(window.Page, view);
        if (!_mainClosed)
        {
            try { AmbientAnimation.SetMinimized(_main, _main.WindowState == WindowState.Minimized); } catch { }
        }
        Changed?.Invoke(window.Page);
    }

    private static void ReportFailure()
    {
        try
        {
            MessageBox.Show(CluadeX.Services.LocalizedResources.Instance["popout.failed"], "CluadeX",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
    }
}
