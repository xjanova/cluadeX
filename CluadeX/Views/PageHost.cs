using System.Windows;
using System.Windows.Controls;
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

    public PageHost() => Children.Add(_rebuilt);

    /// <summary>Pages worth keeping: heavy to build, visited back and forth.</summary>
    public static bool Keeps(object? page) => page is ChatViewModel or CodeEditorViewModel;

    /// <summary>The views kept so far (for the harness).</summary>
    public IReadOnlyCollection<FrameworkElement> KeptViews => _kept.Values;

    private void Show(object? page)
    {
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
            _rebuilt.Content = null;   // the previous rebuilt page goes away, as it always did
            _rebuilt.Visibility = Visibility.Collapsed;
        }
        else
        {
            _rebuilt.Visibility = Visibility.Visible;
            _rebuilt.Content = page;
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
