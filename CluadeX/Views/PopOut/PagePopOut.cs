using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Models;
using CluadeX.ViewModels;

namespace CluadeX.Views.PopOut;

/// <summary>
/// "Open in its own window" for the Node Graph, the UI Studio and the Code Editor: the owner watches the AI work in
/// several pages at once (another monitor) while the chat stays in the main window.
///
/// The page MOVES, it is never shown twice: the views set state on their view model when they load and unload
/// (NodeGraphView.IsVisible, the Studio's on-screen flag), and the UI Studio owns one WebView2. The main window's
/// <see cref="PageHost"/> hands its view over (<see cref="PageHost.Release"/>) and shows a placeholder in its place;
/// closing the window or pressing Bring back hands the same view back (<see cref="PageHost.Adopt"/>).
/// <see cref="PagePopOutManager"/> does the moving; this class holds what the pages and windows share.
/// </summary>
public static class PagePopOut
{
    /// <summary>Open the page (CommandParameter, else the sender's DataContext) in its own window; handled by the main window.</summary>
    public static readonly RoutedUICommand PopOutCommand = new("Open in its own window", "PopOut", typeof(PagePopOut));
    /// <summary>Put the page back in the main window (its own window closes).</summary>
    public static readonly RoutedUICommand BringBackCommand = new("Bring back", "BringBack", typeof(PagePopOut));
    /// <summary>Bring the page's own window forward (from the placeholder).</summary>
    public static readonly RoutedUICommand ShowWindowCommand = new("Show the window", "ShowWindow", typeof(PagePopOut));

    /// <summary>True inside a page's own window (inherited): the pop-out button turns into "put back", and the Code
    /// Editor leaves its docked chat out (the owner types in the main window's chat).</summary>
    public static readonly DependencyProperty IsFloatingProperty = DependencyProperty.RegisterAttached(
        "IsFloating", typeof(bool), typeof(PagePopOut), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsFloating(DependencyObject d) => (bool)d.GetValue(IsFloatingProperty);
    public static void SetIsFloating(DependencyObject d, bool value) => d.SetValue(IsFloatingProperty, value);

    /// <summary>Where a window's placement is remembered (wired to the settings in App.OnStartup, like ResizeGrip).
    /// Unset (a harness): nothing is remembered.</summary>
    public static Func<string, PopOutPlacement?>? LoadPlacement;
    public static Action<string, PopOutPlacement>? SavePlacement;

    /// <summary>The pages that can float.</summary>
    public static bool CanPopOut(object? page) => page is NodeGraphViewModel or UiStudioViewModel or CodeEditorViewModel;

    /// <summary>The page's navigation key (MainViewModel.NavigateTo), also its settings key.</summary>
    public static string? KeyOf(object? page) => page switch
    {
        NodeGraphViewModel => "NodeGraph",
        UiStudioViewModel => "UiStudio",
        CodeEditorViewModel => "Code",
        _ => null,
    };

    /// <summary>The localised window title ("Node Graph — CluadeX").</summary>
    public static string TitleKey(object? page) => page switch
    {
        NodeGraphViewModel => "popout.title.graph",
        UiStudioViewModel => "popout.title.studio",
        _ => "popout.title.editor",
    };

    /// <summary>The page's localised name (the placeholder's heading).</summary>
    public static string NameKey(object? page) => page switch
    {
        NodeGraphViewModel => "popout.name.graph",
        UiStudioViewModel => "popout.name.studio",
        _ => "popout.name.editor",
    };

    /// <summary>The page's glyph (Segoe MDL2), the same as its toolbar's.</summary>
    public static string GlyphOf(object? page) => page switch
    {
        NodeGraphViewModel => "",
        UiStudioViewModel => "",
        _ => "",
    };

    private static bool _graphHooked;

    /// <summary>The Node Graph page has no pop-out button of its own in this build (its view is being rewritten
    /// elsewhere): one is added to the right-hand end of its toolbar when it loads — unless the view already has one,
    /// which is how it should end up (a <c>&lt;popout:PopOutButton Style="{StaticResource StudioToolBtn}"/&gt;</c> in
    /// NodeGraphView.xaml makes this a no-op).</summary>
    public static void HookGraphButton()
    {
        if (_graphHooked) return;
        _graphHooked = true;
        EventManager.RegisterClassHandler(typeof(NodeGraphView), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is NodeGraphView view) AddGraphButton(view);
        }));
    }

    /// <summary>Add the button to the graph's toolbar (its first right-docked stack of buttons). True when the view has
    /// one now.</summary>
    internal static bool AddGraphButton(FrameworkElement view)
    {
        try
        {
            if (Descendants(view).OfType<PopOutButton>().Any()) return true;
            var stack = Descendants(view).OfType<StackPanel>()
                .FirstOrDefault(p => p.Orientation == Orientation.Horizontal && p.Parent is DockPanel && DockPanel.GetDock(p) == Dock.Right
                                     && p.Children.OfType<Button>().Any());
            if (stack == null) return false;
            var button = new PopOutButton();
            if (view.TryFindResource("StudioToolBtn") is Style style) button.Style = style;
            stack.Children.Add(button);
            return true;
        }
        catch { return false; }   // a button is not worth breaking the page for
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = root is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
