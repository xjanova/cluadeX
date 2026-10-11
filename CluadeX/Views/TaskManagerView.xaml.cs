using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace CluadeX.Views;

public partial class TaskManagerView : System.Windows.Controls.UserControl
{
    public TaskManagerView()
    {
        InitializeComponent();
        // The column-header dividers are drawn by the OS header template (Aero2) with its own fixed light-grey gradient,
        // which no style setter reaches. Once the headers exist, point each divider at the theme's token instead
        // (Midnight's List.Header.Divider is that same gradient, so the default look does not move).
        Loaded += (_, _) => Dispatcher.BeginInvoke(ThemeHeaderDividers, DispatcherPriority.Background);
    }

    private void ThemeHeaderDividers()
    {
        foreach (var header in Descendants<GridViewColumnHeader>(this))
        {
            header.ApplyTemplate();
            if (header.Template?.FindName("PART_HeaderGripper", header) is Thumb gripper)
                gripper.SetResourceReference(Control.BackgroundProperty, "List.Header.Divider");
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}
