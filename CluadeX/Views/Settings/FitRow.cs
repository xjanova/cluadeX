using System.Windows;
using System.Windows.Controls;

namespace CluadeX.Views.Settings;

/// <summary>
/// A setting's label and its controls: on one line when the label keeps at least <see cref="MinLabelWidth"/>,
/// otherwise the controls go under the label (2026-11-09). Settings › Voice sits in a narrow card of the
/// settings grid, where a label column next to a 260 px picker got squeezed to one letter per line and the
/// picker was clipped off the right edge. Child 0 is the label; the others run left to right at their own width.
/// </summary>
public sealed class FitRow : Panel
{
    public static readonly DependencyProperty MinLabelWidthProperty = DependencyProperty.Register(
        nameof(MinLabelWidth), typeof(double), typeof(FitRow),
        new FrameworkPropertyMetadata(180.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The narrowest the label may get before the controls move under it.</summary>
    public double MinLabelWidth
    {
        get => (double)GetValue(MinLabelWidthProperty);
        set => SetValue(MinLabelWidthProperty, value);
    }

    /// <summary>Space between the label and the controls under it.</summary>
    public double Gap { get; set; } = 6;

    /// <summary>True when the controls sit under the label (for the layout test).</summary>
    public bool Wrapped { get; private set; }

    protected override Size MeasureOverride(Size available)
    {
        var kids = InternalChildren;
        if (kids.Count == 0) return default;
        var (tailW, tailH) = MeasureTail(kids);
        var head = kids[0];
        if (double.IsInfinity(available.Width))
        {
            head.Measure(available);
            return new Size(head.DesiredSize.Width + tailW, Math.Max(head.DesiredSize.Height, tailH));
        }
        double width = available.Width;
        if (!WrapsAt(width, tailW))
        {
            head.Measure(new Size(width - tailW, available.Height));
            return new Size(width, Math.Max(head.DesiredSize.Height, tailH));
        }
        head.Measure(new Size(width, double.PositiveInfinity));
        return new Size(width, head.DesiredSize.Height + Gap + tailH);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var kids = InternalChildren;
        if (kids.Count == 0) return final;
        var (tailW, tailH) = TailSize(kids);
        var head = kids[0];
        Wrapped = WrapsAt(final.Width, tailW);
        if (!Wrapped)
        {
            head.Arrange(new Rect(0, 0, Math.Max(0, final.Width - tailW), final.Height));
            double x = final.Width - tailW;
            for (int i = 1; i < kids.Count; i++)
            {
                var c = kids[i];
                c.Arrange(new Rect(x, 0, c.DesiredSize.Width, final.Height));
                x += c.DesiredSize.Width;
            }
            return final;
        }
        double headH = head.DesiredSize.Height;
        head.Arrange(new Rect(0, 0, final.Width, headH));
        // Under the label the controls start at its left edge: the first one's left margin (the gap it kept
        // from the label) is taken back.
        double left = 0;
        for (int i = 1; i < kids.Count; i++)
            if (kids[i].Visibility != Visibility.Collapsed) { left = (kids[i] as FrameworkElement)?.Margin.Left ?? 0; break; }
        double cx = -left, y = headH + Gap;
        for (int i = 1; i < kids.Count; i++)
        {
            var c = kids[i];
            c.Arrange(new Rect(cx, y, c.DesiredSize.Width, tailH));
            cx += c.DesiredSize.Width;
        }
        return final;
    }

    bool WrapsAt(double width, double tailW) => tailW > 0 && width - tailW < MinLabelWidth;

    static (double W, double H) MeasureTail(UIElementCollection kids)
    {
        for (int i = 1; i < kids.Count; i++) kids[i].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return TailSize(kids);
    }

    static (double W, double H) TailSize(UIElementCollection kids)
    {
        double w = 0, h = 0;
        for (int i = 1; i < kids.Count; i++)
        {
            w += kids[i].DesiredSize.Width;
            h = Math.Max(h, kids[i].DesiredSize.Height);
        }
        return (w, h);
    }
}
