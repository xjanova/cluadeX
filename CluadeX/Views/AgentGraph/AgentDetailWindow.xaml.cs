using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.ViewModels;

namespace CluadeX.Views.AgentGraph;

/// <summary>A sub-agent's own floating window (see <see cref="AgentDetailViewModel"/>). One per agent: clicking its node
/// again brings the open one forward. Owned by the main window (it closes with it, minimises with it) but never modal —
/// the chat goes on working beside it.</summary>
public partial class AgentDetailWindow : Window
{
    public AgentDetailViewModel Vm { get; }
    private bool _followTail = true;

    public AgentDetailWindow(AgentNode node)
    {
        InitializeComponent();
        Vm = new AgentDetailViewModel(node);
        DataContext = Vm;
        // New steps scroll into view while the reader is at the bottom; scrolled up to read, it stays put.
        StepScroll.ScrollChanged += (_, e) =>
        {
            if (e.ExtentHeightChange == 0) _followTail = StepScroll.VerticalOffset >= StepScroll.ScrollableHeight - 4;
            else if (_followTail) StepScroll.ScrollToEnd();
        };
        StateChanged += (_, _) => AmbientAnimation.SetMinimized(this, WindowState == WindowState.Minimized);
    }

    /// <summary>Open (or bring forward) the window for <paramref name="node"/>. Cascaded beside the owner, on screen.</summary>
    public static AgentDetailWindow Open(AgentNode node, Window? owner)
    {
        var open = Application.Current?.Windows.OfType<AgentDetailWindow>().FirstOrDefault(w => ReferenceEquals(w.Vm.Node, node));
        if (open != null)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return open;
        }
        var win = new AgentDetailWindow(node);
        if (owner != null && owner.IsLoaded && !ReferenceEquals(owner, win)) win.Owner = owner;
        int others = Application.Current?.Windows.OfType<AgentDetailWindow>().Count(w => !ReferenceEquals(w, win)) ?? 0;
        var area = SystemParameters.WorkArea;
        double step = 28 * (others % 8);
        if (owner is { WindowState: not WindowState.Maximized } o && o.Left + o.ActualWidth + win.Width <= area.Right)
        { win.Left = o.Left + o.ActualWidth - 10; win.Top = Math.Max(area.Top, o.Top) + step; }
        else { win.Left = area.Right - win.Width - 8 - step; win.Top = area.Top + 60 + step; }
        win.Top = Math.Min(win.Top, Math.Max(area.Top, area.Bottom - win.Height));
        win.Show();
        return win;
    }

    private void Step_Click(object sender, MouseButtonEventArgs e)
    {
        // a click inside the open result (selecting text) does not fold it
        if (e.OriginalSource is DependencyObject d && FindParent<TextBox>(d) != null) return;
        if ((sender as FrameworkElement)?.DataContext is AgentActivityStep step && step.HasResult) step.IsExpanded = !step.IsExpanded;
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T) d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Pin_Changed(object sender, RoutedEventArgs e) => Topmost = PinButton.IsChecked == true;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        Vm.Dispose();
        base.OnClosed(e);
    }
}
