using System.Windows;
using System.Windows.Controls;
using CluadeX.ViewModels;

namespace CluadeX.Views.Billing;

/// <summary>The Billing page's Savings &amp; comparison section; the comparison lines are laid out to the plot's width.</summary>
public partial class SavingsPanel : UserControl
{
    public SavingsPanel()
    {
        InitializeComponent();
        // A new view model (the harness swaps it) draws to the width already on screen.
        DataContextChanged += (_, e) => { if (PlotArea.ActualWidth > 0) (e.NewValue as BillingSavingsViewModel)?.UpdateChartWidth(PlotArea.ActualWidth); };
    }

    private void Plot_SizeChanged(object sender, SizeChangedEventArgs e)
        => (DataContext as BillingSavingsViewModel)?.UpdateChartWidth(e.NewSize.Width);
}
