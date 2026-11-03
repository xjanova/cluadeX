using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CluadeX.ViewModels;

namespace CluadeX.Views;

/// <summary>
/// The main window's page area. A plain ContentControl rebuilt the page from its DataTemplate on every visit: going
/// Chat → Code built a whole CodeEditorView (AvalonEdit, the explorer, the minimap) plus a second ChatView for the
/// docked chat, every message re-rendered; going back built the chat page again. With a long agent chat that is
/// seconds of a frozen window each way (owner, 2026-10-13: "การสลับระหว่างแชทกับ editor ก็ช้าๆ หน่วงๆ").
/// The chat and the code editor are now built once and kept — leaving one only hides it. Every other page is still
/// rebuilt on each visit, as before (some reload their data when they are shown).
/// </summary>
public sealed class PageHost : Grid
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(object), typeof(PageHost), new PropertyMetadata(null, (d, e) => ((PageHost)d).Show(e.NewValue)));

    /// <summary>The page's view model (MainViewModel.CurrentView).</summary>
    public object? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private readonly Dictionary<Type, FrameworkElement> _kept = new();
    private readonly ContentPresenter _rebuilt = new();
    /// <summary>Pages in a window of their own (Views/PopOut): their view is there, this host shows the placeholder.</summary>
    private readonly HashSet<Type> _away = new();
    private PopOut.PoppedOutPlaceholder? _placeholder;

    public PageHost() => Children.Add(_rebuilt);

    /// <summary>Pages worth keeping: heavy to build, visited back and forth.</summary>
    public static bool Keeps(object? page) => page is ChatViewModel or CodeEditorViewModel or UiStudioViewModel;   // the Studio keeps its running page

    /// <summary>The views kept so far (for the harness).</summary>
    public IReadOnlyCollection<FrameworkElement> KeptViews => _kept.Values;

    // ─── a page in its own window (PagePopOutManager) ───

    /// <summary>Is this page's view in a window of its own?</summary>
    public bool IsAway(object? page) => page != null && _away.Contains(page.GetType());

    /// <summary>The placeholder shown for a page that is away (null until one was needed).</summary>
    public FrameworkElement? Placeholder => _placeholder;

    /// <summary>The page leaves for its own window: its kept view is taken out of this host and returned (made visible);
    /// a page this host rebuilds on every visit is let go (null: the caller builds its own). From now on the page shows
    /// the placeholder here, and is not built here again until <see cref="Adopt"/>.</summary>
    public FrameworkElement? Release(object page)
    {
        _away.Add(page.GetType());
        FrameworkElement? view = null;
        if (_kept.Remove(page.GetType(), out var kept))
        {
            Children.Remove(kept);
            kept.Visibility = Visibility.Visible;
            view = kept;
        }
        else if (ReferenceEquals(_rebuilt.Content, page))
            ClearRebuilt();   // its view unloads before the window's one loads (NodeGraphView sets IsVisible on both)
        Show(Page);
        return view;
    }

    /// <summary>The page is back from its own window, with its view (null if it had none). A kept page keeps that very
    /// view (the editor's tabs, the Studio's running browser); a rebuilt page is rebuilt here when it is shown.</summary>
    public void Adopt(object page, FrameworkElement? view)
    {
        _away.Remove(page.GetType());
        if (view != null && Keeps(page) && !_kept.ContainsKey(page.GetType()) && view.Parent == null)
        {
            view.DataContext = page;
            view.Visibility = Visibility.Collapsed;
            _kept[page.GetType()] = view;
            Children.Add(view);
        }
        Show(Page);
    }

    /// <summary>The rebuilt page's view goes now. A ContentPresenter swaps its child only when it is measured, and a
    /// collapsed one never is: setting Content to null alone left the last rebuilt page (a Node Graph) loaded, hidden
    /// and still subscribed to its view model until that presenter was shown again.</summary>
    private void ClearRebuilt()
    {
        if (_rebuilt.Content == null && VisualTreeHelper.GetChildrenCount(_rebuilt) == 0) return;
        _rebuilt.Content = null;
        _rebuilt.ApplyTemplate();
    }

    /// <summary>A fresh view of the page from its DataTemplate (for its own window), or null.</summary>
    public FrameworkElement? BuildView(object page) => Build(page);

    private void ShowPlaceholder(object page)
    {
        foreach (var view in _kept.Values) view.Visibility = Visibility.Collapsed;
        ClearRebuilt();
        _rebuilt.Visibility = Visibility.Collapsed;
        if (_placeholder == null)
        {
            _placeholder = new PopOut.PoppedOutPlaceholder();
            Children.Add(_placeholder);
        }
        _placeholder.DataContext = page;
        _placeholder.Visibility = Visibility.Visible;
    }

    private void Show(object? page)
    {
        if (page != null && _away.Contains(page.GetType())) { ShowPlaceholder(page); return; }
        if (_placeholder != null) { _placeholder.Visibility = Visibility.Collapsed; _placeholder.DataContext = null; }
        FrameworkElement? kept = null;
        if (page != null && Keeps(page) && !_kept.TryGetValue(page.GetType(), out kept))
        {
            if (!IsLoaded)
            {
                // The page templates live in the window's resources: not reachable before this host is in the window.
                // Show it the old way now and keep it from the first time the host is loaded.
                RoutedEventHandler? once = null;
                once = (_, _) => { Loaded -= once; if (ReferenceEquals(Page, page)) Show(page); };
                Loaded += once;
            }
            kept = IsLoaded ? Build(page) : null;
            if (kept != null)
            {
                _kept[page.GetType()] = kept;
                Children.Add(kept);
            }
        }
        foreach (var view in _kept.Values)
            view.Visibility = ReferenceEquals(view, kept) ? Visibility.Visible : Visibility.Collapsed;
        if (kept != null)
        {
            kept.DataContext = page;   // the same singleton VM; set again in case it was ever replaced
            ClearRebuilt();            // the previous rebuilt page goes away, as it always did
            _rebuilt.Visibility = Visibility.Collapsed;
        }
        else
        {
            _rebuilt.Visibility = Visibility.Visible;
            _rebuilt.Content = page;
        }
    }

    /// <summary>Build a kept page now without showing it, so it is ready the first time it is opened (the UI Studio's
    /// browser starts loading the project the chat just opened). It is added Hidden, not Collapsed: laid out with the
    /// real size, so a native child window (WebView2) is created and the page renders at the size it will be seen at.</summary>
    public void Prewarm(object page)
    {
        if (!IsLoaded || !Keeps(page) || _kept.ContainsKey(page.GetType()) || IsAway(page)) return;   // (away: it is in its own window)
        var view = Build(page);
        if (view == null) return;
        view.Visibility = Visibility.Hidden;
        _kept[page.GetType()] = view;
        Children.Add(view);
    }

    /// <summary>Screen readers and other UI Automation clients see only the page on screen. WPF's default peer walks
    /// every child — Collapsed ones too — and rebuilds that tree after each layout pass while a client listens
    /// (M365 Copilot, the touch keyboard host, an IME…). With the kept chat and code editor (its docked chat
    /// included) that walk ran on the UI thread non-stop: ~30% of a core at idle and stalls while typing
    /// (2026-10-14, UIElementAutomationPeer.iterate in the stack).</summary>
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new VisibleChildrenPeer(this);

    private sealed class VisibleChildrenPeer : System.Windows.Automation.Peers.FrameworkElementAutomationPeer
    {
        public VisibleChildrenPeer(PageHost owner) : base(owner) { }

        protected override List<System.Windows.Automation.Peers.AutomationPeer>? GetChildrenCore()
        {
            var host = (PageHost)Owner;
            var list = new List<System.Windows.Automation.Peers.AutomationPeer>();
            foreach (UIElement child in host.Children)
            {
                if (child.Visibility != Visibility.Visible) continue;
                var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(child);
                if (peer != null) list.Add(peer);
                else if (child is FrameworkElement fe)
                {
                    // A plain container has no peer of its own: hand on its children's, as the default walk does.
                    var inner = new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(fe).GetChildren();
                    if (inner != null) list.AddRange(inner);
                }
            }
            return list;
        }
    }

    /// <summary>The view the page's implicit DataTemplate builds, or null (then it is shown the old way).</summary>
    private FrameworkElement? Build(object page)
    {
        if (TryFindResource(new DataTemplateKey(page.GetType())) is not DataTemplate template) return null;
        if (template.LoadContent() is not FrameworkElement view) return null;
        view.DataContext = page;
        return view;
    }
}
