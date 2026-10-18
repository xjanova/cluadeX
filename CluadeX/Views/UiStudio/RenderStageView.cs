using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services.UiStudio.Sketch;

namespace CluadeX.Views.UiStudio;

/// <summary>
/// A screen as Flutter really drew it, with the place of every widget the project wrote laid over it (from the
/// widget inspector): the widget under the pointer is outlined and named, a click picks it (its exact file:line),
/// Ctrl+click opens that line. While a new picture is being drawn the old one stays, under a light veil.
/// A widget dragged from the Studio's palette shows where it would go (<see cref="DropPlanner"/>) and is dropped there.
/// </summary>
public sealed class RenderStageView : FrameworkElement
{
    /// <summary>The drag data format of a palette widget (its key).</summary>
    public const string WidgetFormat = "CluadeX.FlutterWidget";
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(RenderStageView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BoxesProperty = DependencyProperty.Register(nameof(Boxes), typeof(IReadOnlyList<WidgetBox>), typeof(RenderStageView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(nameof(Selected), typeof(WidgetBox), typeof(RenderStageView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BusyProperty = DependencyProperty.Register(nameof(Busy), typeof(bool), typeof(RenderStageView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LogicalWidthProperty = DependencyProperty.Register(nameof(LogicalWidth), typeof(double), typeof(RenderStageView),
        new FrameworkPropertyMetadata(390.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public IReadOnlyList<WidgetBox>? Boxes { get => (IReadOnlyList<WidgetBox>?)GetValue(BoxesProperty); set => SetValue(BoxesProperty, value); }
    public WidgetBox? Selected { get => (WidgetBox?)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public bool Busy { get => (bool)GetValue(BusyProperty); set => SetValue(BusyProperty, value); }
    /// <summary>The screen's width in Flutter's logical pixels (the boxes are in those).</summary>
    public double LogicalWidth { get => (double)GetValue(LogicalWidthProperty); set => SetValue(LogicalWidthProperty, value); }

    public event Action<WidgetBox, bool>? BoxClicked;
    public event Action<WidgetBox?>? BoxHovered;
    /// <summary>A palette widget (its key) dropped at a point (logical pixels).</summary>
    public event Action<string, double, double>? Dropped;
    /// <summary>Where a widget dragged to a point (logical pixels) would go — null: nowhere.</summary>
    public Func<double, double, FlutterDropPlan?>? DropPlanner { get; set; }
    private FlutterDropPlan? _drop;

    private WidgetBox? _hover;
    private static readonly Typeface Label = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public RenderStageView()
    {
        ClipToBounds = true;
        Focusable = false;
        AllowDrop = true;
    }

    private (double X, double Y) Logical(Point p) => (p.X / Scale, p.Y / Scale);

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (!e.Data.GetDataPresent(WidgetFormat)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        var (x, y) = Logical(e.GetPosition(this));
        var plan = DropPlanner?.Invoke(x, y);
        e.Effects = plan != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (!SamePlan(plan, _drop)) { _drop = plan; InvalidateVisual(); }
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        if (_drop != null) { _drop = null; InvalidateVisual(); }
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        _drop = null;
        InvalidateVisual();
        if (e.Data.GetData(WidgetFormat) is not string key) return;
        var (x, y) = Logical(e.GetPosition(this));
        e.Handled = true;
        Dropped?.Invoke(key, x, y);
    }

    private static bool SamePlan(FlutterDropPlan? a, FlutterDropPlan? b) =>
        a == null ? b == null : b != null && a.File == b.File && a.OwnerLine == b.OwnerLine && a.OwnerColumn == b.OwnerColumn && a.Index == b.Index;

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? LogicalWidth : available.Width;
        double ratio = Source is { Width: > 0 } src ? src.Height / src.Width : 844.0 / 390.0;
        return new Size(w, w * ratio);
    }

    private double Scale => ActualWidth > 0 && LogicalWidth > 0 ? ActualWidth / LogicalWidth : 1;

    /// <summary>The innermost widget under the point (the deepest box; a tie goes to the smaller one).</summary>
    public WidgetBox? HitTestBox(Point p)
    {
        var boxes = Boxes;
        if (boxes == null || boxes.Count == 0) return null;
        double s = Scale;
        double x = p.X / s, y = p.Y / s;
        WidgetBox? best = null;
        foreach (var b in boxes)
        {
            if (x < b.X || y < b.Y || x > b.X + b.W || y > b.Y + b.H || b.W < 1 || b.H < 1) continue;
            if (best == null || b.Depth > best.Depth || b.Depth == best.Depth && b.W * b.H < best.W * best.H) best = b;
        }
        return best;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var b = HitTestBox(e.GetPosition(this));
        if (ReferenceEquals(b, _hover)) return;
        _hover = b;
        Cursor = b != null ? Cursors.Hand : null;
        BoxHovered?.Invoke(b);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover == null) return;
        _hover = null;
        BoxHovered?.Invoke(null);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (HitTestBox(e.GetPosition(this)) is { } b)
        {
            BoxClicked?.Invoke(b, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            e.Handled = true;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Black, null, rect);
        if (Source != null) dc.DrawImage(Source, rect);
        double s = Scale;
        var accent = (Brush?)TryFindResource("Accent.1") ?? Brushes.DeepSkyBlue;
        if (Busy) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, rect);
        if (_hover != null)
        {
            var r = new Rect(_hover.X * s, _hover.Y * s, _hover.W * s, _hover.H * s);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(28, 120, 180, 255)), new Pen(accent, 1.4) { DashStyle = DashStyles.Dash }, r);
            string text = $"{_hover.Widget}  ·  {System.IO.Path.GetFileName(_hover.File)}:{_hover.Line}";
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Label, 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double lw = ft.Width + 12, lx = Math.Min(Math.Max(0, r.X), Math.Max(0, ActualWidth - lw)), ly = r.Y >= 18 ? r.Y - 18 : Math.Min(ActualHeight - 18, r.Bottom + 2);
            dc.DrawRoundedRectangle(accent, null, new Rect(lx, ly, lw, 17), 4, 4);
            dc.DrawText(ft, new Point(lx + 6, ly + 1.5));
        }
        if (Selected is { W: > 0, H: > 0 } sel)
            dc.DrawRectangle(null, new Pen(accent, 2.2), new Rect(sel.X * s, sel.Y * s, sel.W * s, sel.H * s));
        if (_drop is { } d)
        {
            // where the widget goes: a bar between two children, or the box it goes into
            var mark = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
            if (d.Index < 0)
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 0x22, 0xC5, 0x5E)), new Pen(mark, 2) { DashStyle = DashStyles.Dash }, new Rect(d.X * s, d.Y * s, d.W * s, d.H * s));
            else if (d.Vertical)
                dc.DrawRoundedRectangle(mark, null, new Rect(d.X * s, d.Y * s - 2, Math.Max(8, d.W * s), 4), 2, 2);
            else
                dc.DrawRoundedRectangle(mark, null, new Rect(d.X * s - 2, d.Y * s, 4, Math.Max(8, d.H * s)), 2, 2);
            string text = "+ " + d.Owner;
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Label, 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double lx = Math.Min(Math.Max(0, d.X * s), Math.Max(0, ActualWidth - ft.Width - 12)), ly = Math.Max(0, d.Y * s - 20);
            dc.DrawRoundedRectangle(mark, null, new Rect(lx, ly, ft.Width + 12, 17), 4, 4);
            dc.DrawText(ft, new Point(lx + 6, ly + 1.5));
        }
    }
}
