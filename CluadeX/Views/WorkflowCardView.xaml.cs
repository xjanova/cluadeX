using System.Windows;
using System.Windows.Controls;
using CluadeX.ViewModels;

namespace CluadeX.Views;

/// <summary>The workflow card. It tells its view model when it is on screen, so a card scrolled out of view (or in a chat
/// that is not shown) costs no timer and no redraw.</summary>
public partial class WorkflowCardView : UserControl
{
    public WorkflowCardView()
    {
        InitializeComponent();
        Loaded += (_, _) => Tell();
        Unloaded += (_, _) => (DataContext as WorkflowCard)?.SetVisible(false);
        IsVisibleChanged += (_, _) => Tell();
        DataContextChanged += (_, e) =>
        {
            (e.OldValue as WorkflowCard)?.SetVisible(false);
            Tell();
        };
    }

    private void Tell() => (DataContext as WorkflowCard)?.SetVisible(IsLoaded && IsVisible);
}
