using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CluadeX.Views;

/// <summary>
/// A bar (title bar, status bar) that never cuts its items off on a narrow window: when they do not fit, the row
/// glides slowly to one end, rests, glides back, so every item comes into view in turn; with the mouse over it, the
/// row follows the pointer instead (left edge = start, right edge = end), so whatever is hidden can be brought out at
/// once. When everything fits it is an ordinary row: the content gets the full width, star columns stretch as before.
/// </summary>
public sealed class MarqueeScroller : ScrollViewer
{
    /// <summary>Gliding speed, px per second.</summary>
    public double Speed { get; set; } = 28;
    /// <summary>How long it rests at each end, seconds.</summary>
    public double RestSeconds { get; set; } = 2.2;

    private double _offset, _dir = 1, _rest;
    private TimeSpan _lastTick;
    private bool _hooked;

    public MarqueeScroller()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = false;
        Loaded += (_, _) => UpdateHook();
        Unloaded += (_, _) => Hook(false);
        SizeChanged += (_, _) => { FitContent(); UpdateHook(); };
        ScrollChanged += (_, _) => UpdateHook();
    }

    /// <summary>Animate only while the row overflows: a frame callback keeps WPF rendering every frame, which a bar
    /// that fits must not cost.</summary>
    private void UpdateHook()
    {
        bool overflow = ScrollableWidth > MinOverflow;
        if (!overflow && HorizontalOffset > 0) ScrollToHorizontalOffset(_offset = 0);
        Hook(IsLoaded && overflow);
    }
    /// <summary>A few pixels over is left clipped at the end, as before, rather than set the row drifting.</summary>
    private const double MinOverflow = 12;

    private void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        _lastTick = TimeSpan.Zero;
        if (on) CompositionTarget.Rendering += OnFrame; else CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>The row is measured at the bar's width when that is enough (star columns stretch as before) and at its
    /// natural width when it is not; only then is there anything to glide.</summary>
    private void FitContent() => (base.Content as FitPanel)?.InvalidateMeasure();

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        if (newContent is UIElement el and not FitPanel)
        {
            Content = new FitPanel(this) { Child = el };
            return;
        }
        base.OnContentChanged(oldContent, newContent);
    }

    private sealed class FitPanel : Decorator
    {
        private readonly MarqueeScroller _owner;
        public FitPanel(MarqueeScroller owner)
        {
            _owner = owner;
            // A chip that changes size or takes focus asks to be scrolled into view; the bar moves only by its own
            // glide or the mouse, never jumps.
            RequestBringIntoView += (_, e) => e.Handled = true;
        }

        protected override Size MeasureOverride(Size constraint)
        {
            if (Child == null) return default;
            double h = double.IsInfinity(constraint.Height) ? _owner.ActualHeight : constraint.Height;
            if (h <= 0 || double.IsNaN(h)) h = double.PositiveInfinity;
            // Natural width first (unbounded): a FrameworkElement clips its DesiredSize to the space it is given, so
            // measuring at 0 always answered 0 and the bar never knew it overflowed.
            Child.Measure(new Size(double.PositiveInfinity, h));
            double width = Math.Max(MinimumWidth(Child), _owner.ActualWidth);
            Child.Measure(new Size(width, h));
            return new Size(width, Child.DesiredSize.Height);
        }

        /// <summary>The row's natural width, less what its star columns asked for: those columns are meant to give way
        /// (the title's search pill), so only the fixed parts make the bar overflow.</summary>
        private static double MinimumWidth(UIElement child)
        {
            double natural = child.DesiredSize.Width;
            if (child is not Grid g) return natural;
            double give = 0;
            for (int col = 0; col < g.ColumnDefinitions.Count; col++)
            {
                var cd = g.ColumnDefinitions[col];
                if (!cd.Width.IsStar) continue;
                double widest = 0;
                foreach (UIElement c in g.Children)
                    if (c.Visibility != Visibility.Collapsed && Grid.GetColumn(c) == col && Grid.GetColumnSpan(c) == 1)
                        widest = Math.Max(widest, c.DesiredSize.Width);
                give += Math.Max(0, widest - cd.MinWidth);
            }
            return Math.Max(0, natural - give);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            Child?.Arrange(new Rect(arrangeSize));
            return arrangeSize;
        }
    }

    // A wheel over a bar would steal the page's scroll; turn it sideways here only when the row overflows.
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (ScrollableWidth <= 0) return;   // not handled: the page beneath scrolls
        _offset = Math.Clamp(_offset - e.Delta * 0.5, 0, ScrollableWidth);
        ScrollToHorizontalOffset(_offset);
        e.Handled = true;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
        double dt = _lastTick == TimeSpan.Zero ? 0 : Math.Min(0.1, (now - _lastTick).TotalSeconds);
        _lastTick = now;
        double max = ScrollableWidth;
        if (max <= MinOverflow)
        {
            if (HorizontalOffset > 0) ScrollToHorizontalOffset(_offset = 0);
            Hook(false);
            return;
        }
        if (dt <= 0) return;

        if (IsMouseOver)
        {
            // Follow the pointer, eased: the edge 15% at each side reaches the ends.
            double x = Mouse.GetPosition(this).X / Math.Max(1, ActualWidth);
            double target = Math.Clamp((x - 0.15) / 0.7, 0, 1) * max;
            _offset += (target - _offset) * Math.Min(1, dt * 8);
            _rest = RestSeconds / 2;
        }
        else if (_rest > 0)
        {
            _rest -= dt;
        }
        else
        {
            _offset += _dir * Speed * dt;
            if (_offset >= max) { _offset = max; _dir = -1; _rest = RestSeconds; }
            else if (_offset <= 0) { _offset = 0; _dir = 1; _rest = RestSeconds; }
        }
        if (Math.Abs(HorizontalOffset - _offset) > 0.2) ScrollToHorizontalOffset(_offset);
    }
}
