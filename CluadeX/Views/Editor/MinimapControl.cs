using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;

namespace CluadeX.Views.Editor;

/// <summary>
/// A real minimap for the AvalonEdit editor (replaces the old decorative gradient stripe):
/// one indent-aware bar per document line (comments dimmed, blanks skipped), the current
/// viewport drawn as a draggable window, the caret line marked, and agent-edited regions
/// flagged on the left rail so you can see at a glance WHERE the AI touched the file.
///
/// Rendering reads a plain string snapshot. Text changes are throttled (typing / typewriter
/// reveal would otherwise rebuild per keystroke); scrolling repaints IMMEDIATELY, because a
/// viewport box that lags the scrollbar reads as broken.
/// </summary>
public class MinimapControl : FrameworkElement
{
    private TextEditor? _editor;
    private System.Windows.Threading.DispatcherTimer? _throttle;
    private bool _rebuildPending;
    private bool _dragging;
    private double _hoverY = -1;

    /// <summary>Per-line render metrics: normalized length, indent fraction, comment/blank flags.</summary>
    private readonly List<LineMetric> _lines = new();
    private readonly record struct LineMetric(float Len, float Indent, bool Comment, bool Blank);

    /// <summary>Line ranges the agent recently changed (1-based, inclusive) — drawn on the left rail.</summary>
    private readonly List<(int From, int To)> _changed = new();

    private static readonly Brush BarBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x82, 0x9A, 0xA5, 0xE8)));
    private static readonly Brush CommentBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x7C, 0x85, 0xA0)));
    private static readonly Brush ViewportBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen ViewportPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0x4C, 0xDF, 0xFF)), 1));
    private static readonly Brush HoverBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush CaretBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xD3, 0x7E)));
    private static readonly Brush ChangeBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xE0, 0x5C, 0xFF, 0xB0)));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public MinimapControl()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        ToolTip = "Minimap — click or drag to jump";
    }

    // A FrameworkElement has no visual by default; force the whole strip to be hit-testable
    // so clicks land even on empty rows between bars.
    protected override HitTestResult HitTestCore(PointHitTestParameters p)
        => new PointHitTestResult(this, p.HitPoint);

    /// <summary>Attach to the editor whose document + scroll state this minimap mirrors.</summary>
    public void Attach(TextEditor editor)
    {
        Detach();
        _editor = editor;
        editor.TextChanged += OnEditorChanged;
        editor.TextArea.TextView.ScrollOffsetChanged += OnScrollChanged;
        editor.TextArea.Caret.PositionChanged += OnScrollChanged;
        RebuildLines();
        InvalidateVisual();
    }

    /// <summary>Unhook from the current editor (safe to call when never attached).</summary>
    public void Detach()
    {
        if (_editor == null) return;
        _editor.TextChanged -= OnEditorChanged;
        _editor.TextArea.TextView.ScrollOffsetChanged -= OnScrollChanged;
        _editor.TextArea.Caret.PositionChanged -= OnScrollChanged;
        _editor = null;
    }

    /// <summary>Mark a line range as agent-changed (drawn on the left rail). Newest 40 kept.</summary>
    public void AddChangeMarker(int fromLine, int toLine)
    {
        if (fromLine <= 0) return;
        _changed.Add((fromLine, Math.Max(fromLine, toLine)));
        if (_changed.Count > 40) _changed.RemoveRange(0, _changed.Count - 40);
        InvalidateVisual();
    }

    /// <summary>Drop all change markers (called when the editor switches file).</summary>
    public void ClearChangeMarkers()
    {
        if (_changed.Count == 0) return;
        _changed.Clear();
        InvalidateVisual();
    }

    /// <summary>Scroll the editor so <paramref name="fraction"/> (0..1) of the document is centred.
    /// Public so the click/drag path is exercisable by tests, not only by real mouse input.</summary>
    public void ScrollToFraction(double fraction)
    {
        if (_editor == null) return;
        var view = _editor.TextArea.TextView;
        double docH = Math.Max(view.DocumentHeight, 1);
        double target = Math.Clamp(fraction, 0, 1) * docH - view.ActualHeight / 2;
        _editor.ScrollToVerticalOffset(Math.Max(0, target));
    }

    // ── Change plumbing ──

    private void OnEditorChanged(object? sender, EventArgs e)
    {
        // Text edits are expensive to re-measure — coalesce bursts.
        _rebuildPending = true;
        if (_throttle == null)
        {
            _throttle = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(90),
            };
            _throttle.Tick += (_, _) =>
            {
                _throttle!.Stop();
                if (!_rebuildPending) return;
                _rebuildPending = false;
                RebuildLines();
                InvalidateVisual();
            };
        }
        if (!_throttle.IsEnabled) _throttle.Start();
    }

    // Scroll/caret only moves the overlay — repaint NOW so the box tracks the scrollbar 1:1.
    private void OnScrollChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void RebuildLines()
    {
        _lines.Clear();
        string text = _editor?.Text ?? "";
        if (text.Length == 0) return;

        var raw = new List<(int Len, int Indent, bool Comment)>(512);
        int maxLen = 1;
        int start = 0;

        for (int i = 0; i <= text.Length; i++)
        {
            if (i != text.Length && text[i] != '\n') continue;

            int end = i;
            if (end > start && text[end - 1] == '\r') end--;   // CRLF
            int len = end - start;

            // Leading whitespace (tab = 4 cols) + cheap comment sniff on the first glyph pair.
            int indent = 0;
            bool comment = false;
            int j = start;
            for (; j < end; j++)
            {
                char c = text[j];
                if (c == ' ') { indent++; continue; }
                if (c == '\t') { indent += 4; continue; }
                comment = c == '#'
                    || (c == '/' && j + 1 < end && (text[j + 1] == '/' || text[j + 1] == '*'))
                    || (c == '*' && j + 1 < end && text[j + 1] != '/')
                    || (c == '<' && j + 3 < end && text[j + 1] == '!' && text[j + 2] == '-');
                break;
            }
            if (j >= end) { indent = 0; }   // whitespace-only line counts as blank

            raw.Add((len, indent, comment));
            if (len > maxLen) maxLen = len;
            start = i + 1;
        }

        // Normalize against the p95-ish width so a couple of monster lines don't squash everything.
        int scale = Math.Min(maxLen, 110);
        foreach (var (len, indent, comment) in raw)
        {
            float ind = Math.Min(0.55f, indent / 60f);
            float ln = (float)Math.Min(1.0 - ind, Math.Max(len - indent, 0) / (double)scale);
            _lines.Add(new LineMetric(ln, ind, comment, len == 0 || ln <= 0.001f));
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Invisible backplate keeps the whole strip hit-testable.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_editor == null || _lines.Count == 0 || ActualHeight < 10 || ActualWidth < 8) return;

        const double railW = 3;          // left rail for change markers
        double x0 = railW + 3;
        double usableW = Math.Max(4, ActualWidth - x0 - 3);
        int count = _lines.Count;
        double yPerLine = ActualHeight / count;
        double barH = Math.Clamp(yPerLine * 0.75, 0.6, 3.0);

        // When lines outnumber pixel rows, sample so we draw at most one bar per row.
        int step = Math.Max(1, (int)Math.Ceiling(count / Math.Max(1.0, ActualHeight)));

        for (int i = 0; i < count; i += step)
        {
            var m = _lines[i];
            if (m.Blank) continue;
            double y = i * yPerLine;
            double bx = x0 + m.Indent * usableW;
            double bw = Math.Max(1.5, m.Len * usableW);
            if (bx + bw > ActualWidth - 2) bw = Math.Max(1.5, ActualWidth - 2 - bx);
            dc.DrawRectangle(m.Comment ? CommentBrush : BarBrush, null, new Rect(bx, y, bw, barH));
        }

        // Agent-changed ranges on the left rail
        foreach (var (from, to) in _changed)
        {
            double y = (from - 1) * yPerLine;
            double h = Math.Max(2, (to - from + 1) * yPerLine);
            dc.DrawRectangle(ChangeBrush, null, new Rect(0, y, railW, Math.Min(h, ActualHeight - y)));
        }

        // Caret line
        int caretLine = _editor.TextArea.Caret.Line;
        if (caretLine >= 1 && caretLine <= count)
            dc.DrawRectangle(CaretBrush, null, new Rect(x0 - 2, (caretLine - 1) * yPerLine, 2, Math.Max(1.5, barH)));

        // Hover band
        if (_hoverY >= 0)
            dc.DrawRectangle(HoverBrush, null, new Rect(0, Math.Max(0, _hoverY - 6), ActualWidth, 12));

        // Viewport window — mapped through the SAME doc-height scale as the bars so the box
        // lines up with the code it represents.
        var view = _editor.TextArea.TextView;
        double docH = Math.Max(view.DocumentHeight, 1);
        double vpY = view.ScrollOffset.Y / docH * ActualHeight;
        double vpH = Math.Max(10, view.ActualHeight / docH * ActualHeight);
        if (vpY > ActualHeight - 4) vpY = ActualHeight - 4;
        dc.DrawRoundedRectangle(ViewportBrush, ViewportPen,
            new Rect(1, Math.Max(0, vpY), Math.Max(2, ActualWidth - 2), Math.Min(vpH, ActualHeight - Math.Max(0, vpY))), 2, 2);
    }

    // ── Click / drag to jump ──

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        CaptureMouse();
        ScrollToFraction(e.GetPosition(this).Y / Math.Max(1, ActualHeight));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hoverY = e.GetPosition(this).Y;
        if (_dragging) ScrollToFraction(_hoverY / Math.Max(1, ActualHeight));
        else InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverY = -1;
        InvalidateVisual();
    }

    /// <summary>Wheel over the minimap scrolls the editor, like the real scrollbar.</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_editor == null) return;
        _editor.ScrollToVerticalOffset(
            Math.Max(0, _editor.TextArea.TextView.ScrollOffset.Y - e.Delta));
        e.Handled = true;
    }
}
