using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CluadeX.Services;

namespace CluadeX.Views.PopOut;

/// <summary>What the main window shows where a page was while that page is in its own window: its name, "Open in its
/// own window", Bring back and Show the window. The page is the DataContext (the buttons' parameter).</summary>
public partial class PoppedOutPlaceholder : UserControl
{
    public PoppedOutPlaceholder()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Describe(DataContext);
    }

    private void Describe(object? page)
    {
        PageGlyph.Text = PagePopOut.GlyphOf(page);
        PageName.SetBinding(TextBlock.TextProperty,
            new Binding($"[{PagePopOut.NameKey(page)}]") { Source = LocalizedResources.Instance, Mode = BindingMode.OneWay });
    }
}
