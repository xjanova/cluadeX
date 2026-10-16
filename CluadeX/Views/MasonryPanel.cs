using System.Windows;
using System.Windows.Controls;

namespace CluadeX.Views;

/// <summary>
/// Cards in as many columns as fit (each at least <see cref="MinColumnWidth"/> wide), every card dropped into the
/// shortest column — the Settings page fills a wide window instead of one 780 px strip (owner 2026-10-16:
/// "การ์ดเรียงสวยกว่านี้ เต็มพื้นที่ใช้งาน"). A narrow window gets one column, exactly as before.
/// </summary>
public sealed class MasonryPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(440.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    private int Columns(double width) =>
        double.IsInfinity(width) ? 1 : Math.Max(1, (int)((width + Gap) / (MinColumnWidth + Gap)));

    private double ColumnWidth(double width, int cols) => Math.Max(0, (width - Gap * (cols - 1)) / cols);

    protected override Size MeasureOverride(Size available)
    {
        int cols = Columns(available.Width);
        double cw = double.IsInfinity(available.Width) ? MinColumnWidth : ColumnWidth(available.Width, cols);
        var heights = new double[cols];
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(cw, double.PositiveInfinity));
            int c = Array.IndexOf(heights, heights.Min());
            heights[c] += child.DesiredSize.Height + Gap;
        }
        double h = heights.Max();
        return new Size(double.IsInfinity(available.Width) ? cw : available.Width, Math.Max(0, h - Gap));
    }

    protected override Size ArrangeOverride(Size final)
    {
        int cols = Columns(final.Width);
        double cw = ColumnWidth(final.Width, cols);
        var heights = new double[cols];
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) { child.Arrange(new Rect()); continue; }
            int c = Array.IndexOf(heights, heights.Min());
            child.Arrange(new Rect(c * (cw + Gap), heights[c], cw, child.DesiredSize.Height));
            heights[c] += child.DesiredSize.Height + Gap;
        }
        return final;
    }
}
