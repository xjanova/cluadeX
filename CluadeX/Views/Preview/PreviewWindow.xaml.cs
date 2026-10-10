using System.Windows;

namespace CluadeX.Views.Preview;

/// <summary>The pop-out Preview window — opens on the agent's first browser_open when no Preview pane is on
/// screen, or from the pane's pop-out button. Closing it only hides the page (the page keeps running off-screen).</summary>
public partial class PreviewWindow : Window
{
    public PreviewWindow()
    {
        InitializeComponent();
        Pane.IsInWindow = true;
    }
}
