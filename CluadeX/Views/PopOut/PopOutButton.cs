using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using CluadeX.Services;

namespace CluadeX.Views.PopOut;

/// <summary>
/// The small button in a page's toolbar: in the main window it opens the page in its own window; inside that window
/// (<see cref="PagePopOut.IsFloatingProperty"/>) the same button puts it back. The page is its DataContext. Styled by
/// the toolbar it sits in (StudioToolBtn, EdToolBtn); its content is one glyph.
/// </summary>
public sealed class PopOutButton : Button
{
    private readonly TextBlock _glyph = new()
    {
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    public PopOutButton()
    {
        Content = _glyph;
        SetBinding(CommandParameterProperty, new Binding());   // the page (DataContext)
        Refresh();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == PagePopOut.IsFloatingProperty) Refresh();
    }

    private void Refresh()
    {
        bool floating = PagePopOut.GetIsFloating(this);
        Command = floating ? PagePopOut.BringBackCommand : PagePopOut.PopOutCommand;
        _glyph.Text = floating ? "" : "";   // BackToWindow · OpenInNewWindow
        var tip = new Binding($"[{(floating ? "popout.back.tip" : "popout.button.tip")}]") { Source = LocalizedResources.Instance, Mode = BindingMode.OneWay };
        SetBinding(ToolTipProperty, tip);
        SetBinding(AutomationProperties.NameProperty, tip);
    }
}
