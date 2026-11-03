using System.Windows;
using System.Windows.Controls;
using CluadeX.ViewModels;

namespace CluadeX.Views;

public partial class BillingView : UserControl
{
    public BillingView()
    {
        InitializeComponent();
        // The page works only while it is on screen: hidden, new usage just marks it stale.
        IsVisibleChanged += (_, e) => (DataContext as BillingViewModel)?.SetVisible((bool)e.NewValue);
        DataContextChanged += (_, e) =>
        {
            (e.OldValue as BillingViewModel)?.SetVisible(false);
            if (IsVisible) (e.NewValue as BillingViewModel)?.SetVisible(true);
        };
    }
}
