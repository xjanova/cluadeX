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
        IsVisibleChanged += (_, e) =>
        {
            (DataContext as BillingViewModel)?.SetVisible((bool)e.NewValue);
            if ((bool)e.NewValue) ScheduleSavings();
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is BillingViewModel old) { old.SetVisible(false); old.SavingsRequested -= ScheduleSavings; }
            if (e.NewValue is BillingViewModel vm)
            {
                vm.SavingsRequested += ScheduleSavings;
                if (IsVisible) vm.SetVisible(true);
                ScheduleSavings();
            }
        };
        // The page is rebuilt on every visit: a view left behind must not stay subscribed to the page's (singleton) model.
        Unloaded += (_, _) => { if (DataContext is BillingViewModel vm) vm.SavingsRequested -= ScheduleSavings; };
        Loaded += (_, _) =>
        {
            if (DataContext is not BillingViewModel vm) return;
            vm.SavingsRequested -= ScheduleSavings;
            vm.SavingsRequested += ScheduleSavings;
            ScheduleSavings();
        };
    }

    /// <summary>The title bar's "Spend &amp; savings" menu opened the page: scroll to that section once the page is on screen
    /// and laid out with its numbers (the request waits in the view model until then). The section's top goes to the top
    /// of the page — BringIntoView scrolls only as far as needed, which leaves a section below at the very bottom edge.</summary>
    private void ScheduleSavings() =>
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible || DataContext is not BillingViewModel vm || !vm.TakeSavingsRequest()) return;
            if (!SavingsSection.IsVisible) return;   // nothing recorded yet: the empty state has no savings section
            double y = SavingsSection.TranslatePoint(new Point(0, 0), PageScroll).Y + PageScroll.VerticalOffset;
            PageScroll.ScrollToVerticalOffset(Math.Max(0, y));
        }, System.Windows.Threading.DispatcherPriority.Loaded);
}
