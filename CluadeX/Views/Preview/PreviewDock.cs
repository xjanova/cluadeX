using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CluadeX.Views.Preview;

/// <summary>
/// The PREVIEW split of the code workbench: a <see cref="PreviewPane"/> with a grip on its left edge to resize
/// it. Self-contained, so the workbench only places it and binds its visibility.
/// </summary>
public sealed class PreviewDock : Grid
{
    private const double MinPaneWidth = 320, MaxPaneWidth = 1600;

    public PreviewDock()
    {
        // The width lives in the app-wide PreviewViewModel: the workbench view (and this dock) is rebuilt on every
        // visit to the Code page, and the user's chosen width must survive that.
        var vm = ViewModels.PreviewViewModel.Current;
        Width = Math.Clamp(vm?.DockWidth ?? 520, MinPaneWidth, MaxPaneWidth);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var grip = new Thumb
        {
            Cursor = Cursors.SizeWE,
            Template = GripTemplate(),
        };
        grip.SetResourceReference(Control.BackgroundProperty, "Border.Default");   // the theme's divider colour
        grip.DragDelta += (_, e) =>
        {
            Width = Math.Clamp(Width - e.HorizontalChange, MinPaneWidth, MaxPaneWidth);
            if (vm != null) vm.DockWidth = Width;
        };
        SetColumn(grip, 0);
        Children.Add(grip);

        var pane = new PreviewPane();
        SetColumn(pane, 1);
        Children.Add(pane);
    }

    private static ControlTemplate GripTemplate()
    {
        var t = new ControlTemplate(typeof(Thumb));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        t.VisualTree = border;
        return t;
    }
}
