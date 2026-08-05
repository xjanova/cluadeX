using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace CluadeX.Views.Editor;

/// <summary>
/// The clickable gutter strip left of the line numbers: click to toggle a breakpoint, and see where
/// execution is currently stopped. Breakpoint state lives in the view model — this margin only
/// asks and reports, so the set survives closing and reopening the file.
/// </summary>
public sealed class BreakpointMargin : AbstractMargin
{
    private static readonly Brush BreakpointFill = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x53, 0x70)));
    private static readonly Brush BreakpointHalo = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0x53, 0x70)));
    private static readonly Brush HoverFill = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0x53, 0x70)));
    private static readonly Brush ExecutionFill = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD3, 0x7E)));
    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x11, 0x15, 0x2E)));

    private const double MarginWidth = 18;

    private int _hoverLine = -1;

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    /// <summary>Asks the view model whether a 1-based line carries a breakpoint.</summary>
    public Func<int, bool>? IsBreakpoint { get; set; }

    /// <summary>Reports a click on a 1-based line.</summary>
    public Action<int>? ToggleRequested { get; set; }

    private int _executionLine;
    /// <summary>1-based line the debugger is stopped on (0 = not stopped in this file).</summary>
    public int ExecutionLine
    {
        get => _executionLine;
        set { if (_executionLine == value) return; _executionLine = value; InvalidateVisual(); }
    }

    public BreakpointMargin() => Cursor = Cursors.Hand;

    protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView != null)
        {
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            oldTextView.ScrollOffsetChanged -= OnVisualLinesChanged;
        }
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView != null)
        {
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
            newTextView.ScrollOffsetChanged += OnVisualLinesChanged;
        }
        InvalidateVisual();
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>Repaint after the breakpoint set changed elsewhere (cleared from the debug panel).</summary>
    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var view = TextView;
        if (view == null || !view.VisualLinesValid) return;

        foreach (var visualLine in view.VisualLines)
        {
            int lineNumber = visualLine.FirstDocumentLine.LineNumber;
            double top = visualLine.VisualTop - view.VerticalOffset;
            double centre = top + visualLine.Height / 2;
            if (centre < -10 || centre > ActualHeight + 10) continue;

            if (lineNumber == _executionLine)
            {
                // Execution pointer — a right-facing triangle, the universal "you are here".
                var arrow = new StreamGeometry();
                using (var ctx = arrow.Open())
                {
                    ctx.BeginFigure(new Point(4, centre - 5), true, true);
                    ctx.LineTo(new Point(13, centre), true, false);
                    ctx.LineTo(new Point(4, centre + 5), true, false);
                }
                arrow.Freeze();
                dc.DrawGeometry(ExecutionFill, null, arrow);
                continue;   // the pointer wins the slot when both land on one line
            }

            if (IsBreakpoint?.Invoke(lineNumber) == true)
            {
                dc.DrawEllipse(BreakpointHalo, null, new Point(9, centre), 7, 7);
                dc.DrawEllipse(BreakpointFill, null, new Point(9, centre), 4.5, 4.5);
            }
            else if (lineNumber == _hoverLine)
            {
                // Ghost dot on hover, so the gutter advertises that it is clickable.
                dc.DrawEllipse(HoverFill, null, new Point(9, centre), 4.5, 4.5);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int line = LineAt(e.GetPosition(this).Y);
        if (line == _hoverLine) return;
        _hoverLine = line;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverLine == -1) return;
        _hoverLine = -1;
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Left) return;

        int line = LineAt(e.GetPosition(this).Y);
        if (line <= 0) return;

        ToggleRequested?.Invoke(line);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Map a Y offset in the margin to a 1-based document line, or -1 outside the text.</summary>
    private int LineAt(double y)
    {
        var view = TextView;
        if (view == null || !view.VisualLinesValid) return -1;

        double absoluteY = y + view.VerticalOffset;
        foreach (var visualLine in view.VisualLines)
        {
            if (absoluteY >= visualLine.VisualTop && absoluteY < visualLine.VisualTop + visualLine.Height)
                return visualLine.FirstDocumentLine.LineNumber;
        }
        return -1;
    }
}

/// <summary>
/// Paints the band behind the line the debugger is stopped on. A gutter arrow alone is easy to miss
/// when you are reading the code rather than the margin.
/// </summary>
public sealed class ExecutionLineRenderer : IBackgroundRenderer
{
    private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xD3, 0x7E)));
    private static readonly Pen Edge = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xD3, 0x7E)), 1));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }

    /// <summary>1-based; 0 clears the highlight.</summary>
    public int Line { get; set; }

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (Line <= 0 || textView?.Document == null) return;
        if (Line > textView.Document.LineCount) return;

        textView.EnsureVisualLines();
        var documentLine = textView.Document.GetLineByNumber(Line);

        foreach (var rect in ICSharpCode.AvalonEdit.Rendering.BackgroundGeometryBuilder
                     .GetRectsForSegment(textView, documentLine))
        {
            var full = new Rect(0, rect.Top, Math.Max(textView.ActualWidth, rect.Right), rect.Height);
            drawingContext.DrawRectangle(Fill, Edge, full);
        }
    }
}
