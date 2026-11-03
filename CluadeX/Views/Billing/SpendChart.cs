using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.ViewModels;

namespace CluadeX.Views.Billing;

/// <summary>
/// The Billing page's chart: one bar per day (stacked parts) and the running total as a line on its own scale. Drawn
/// in one OnRender pass — no element per bar, no animation — and only when its data, size or theme brushes change. The
/// tooltip follows the day under the pointer.
/// </summary>
public sealed class SpendChart : FrameworkElement
{
    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(nameof(Bars), typeof(IReadOnlyList<ChartBar>),
        typeof(SpendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(nameof(LineBrush), typeof(Brush),
        typeof(SpendChart), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush),
        typeof(SpendChart), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush),
        typeof(SpendChart), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartBar>? Bars { get => (IReadOnlyList<ChartBar>?)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    private const double LabelBand = 18, TopPad = 8;
    private int _hover = -1;

    public SpendChart()
    {
        SnapsToDevicePixels = true;
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTipService.SetShowDuration(this, 60000);
    }

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 600 : available.Width, double.IsInfinity(available.Height) ? 220 : available.Height);

    private (double Left, double Width, double Slot) Layout(int count)
    {
        double w = Math.Max(1, ActualWidth);
        return (0, w, count == 0 ? w : w / count);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // hit-testable everywhere (tooltip)
        var bars = Bars;
        double plotH = Math.Max(1, h - LabelBand - TopPad);
        var grid = new Pen(GridBrush, 1); grid.Freeze();
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(TopPad + plotH * i / 4) + 0.5;
            dc.DrawLine(grid, new Point(0, y), new Point(w, y));
        }
        if (bars == null || bars.Count == 0) return;

        double max = bars.Max(b => b.Total), cumMax = bars.Max(b => b.Cumulative);
        var (_, _, slot) = Layout(bars.Count);
        double barW = Math.Max(1, Math.Min(28, slot * 0.68));
        for (int i = 0; i < bars.Count; i++)
        {
            double x = slot * i + (slot - barW) / 2, bottom = TopPad + plotH;
            if (i == _hover) dc.DrawRectangle(GridBrush, null, new Rect(slot * i, TopPad, slot, plotH));
            if (max <= 0) continue;
            foreach (var part in bars[i].Parts)
            {
                double ph = part.Value / max * plotH;
                if (ph <= 0) continue;
                ph = Math.Max(ph, 1);
                dc.DrawRectangle(part.Color, null, new Rect(x, bottom - ph, barW, ph));
                bottom -= ph;
            }
        }

        // The running total, scaled to its own maximum (it ends at the top unless the range had no use at all).
        if (cumMax > 0)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                for (int i = 0; i < bars.Count; i++)
                {
                    var p = new Point(slot * i + slot / 2, TopPad + plotH - bars[i].Cumulative / cumMax * plotH);
                    if (i == 0) g.BeginFigure(p, false, false); else g.LineTo(p, true, false);
                }
            }
            geo.Freeze();
            var pen = new Pen(LineBrush, 2) { LineJoin = PenLineJoin.Round }; pen.Freeze();
            dc.DrawGeometry(null, pen, geo);
            var last = new Point(slot * (bars.Count - 1) + slot / 2, TopPad + plotH - bars[^1].Cumulative / cumMax * plotH);
            dc.DrawEllipse(LineBrush, null, last, 3, 3);
        }

        // Date labels: about eight across, the first and the last always.
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        int every = Math.Max(1, (int)Math.Ceiling(bars.Count / 8d));
        double lastRight = -1;
        for (int i = 0; i < bars.Count; i++)
        {
            if (i % every != 0 && i != bars.Count - 1) continue;
            var ft = new FormattedText(bars[i].Day.ToString("d/M", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, face, 10.5, TextBrush, dpi);
            double x = Math.Clamp(slot * i + slot / 2 - ft.Width / 2, 0, Math.Max(0, w - ft.Width));
            if (x < lastRight + 4) continue;
            dc.DrawText(ft, new Point(x, h - LabelBand + 3));
            lastRight = x + ft.Width;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var bars = Bars;
        if (bars == null || bars.Count == 0) return;
        var (_, _, slot) = Layout(bars.Count);
        int i = Math.Clamp((int)(e.GetPosition(this).X / slot), 0, bars.Count - 1);
        if (i == _hover) return;
        _hover = i;
        ToolTip = bars[i].Tip;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        InvalidateVisual();
    }
}
