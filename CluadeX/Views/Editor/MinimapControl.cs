using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;

namespace CluadeX.Views.Editor;

/// <summary>
/// A REAL minimap for the AvalonEdit editor (replaces the old decorative gradient stripe):
/// draws one bar per document line (width ∝ line length, comments dimmed), overlays the
/// current viewport as a draggable window, and click/drag jumps the editor there.
/// Renders from a plain string snapshot on a throttle timer, so even large files stay cheap.
/// </summary>
public class MinimapControl : FrameworkElement
{
    private TextEditor? _editor;
    private System.Windows.Threading.DispatcherTimer? _throttle;
    private bool _dirty;
    private bool _dragging;

    // Line metrics snapshot (rebuilt on text change): per-line (lengthFraction, isComment, isBlank)
    private readonly List<(float Len, bool Comment, bool Blank)> _lines = new();

    private static readonly Brush BarBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x6E, 0x9A, 0xA5, 0xE8)));
    private static readonly Brush CommentBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0x6C, 0x70, 0x86)));
    private static readonly Brush ViewportBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen ViewportPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x4C, 0xDF, 0xFF)), 1));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public MinimapControl()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        // A transparent background so hit-testing covers the whole strip, not just drawn bars.
    }

    protected override HitTestResult HitTestCore(PointHitTestParameters p)
        => new PointHitTestResult(this, p.HitPoint);

    /// <summary>Attach to the editor whose document + scroll state this minimap mirrors.</summary>
    public void Attach(TextEditor editor)
    {
        if (_editor != null)
        {
            _editor.TextChanged -= OnEditorChanged;
            _editor.TextArea.TextView.ScrollOffsetChanged -= OnScrollChanged;
        }
        _editor = editor;
        editor.TextChanged += OnEditorChanged;
        editor.TextArea.TextView.ScrollOffsetChanged += OnScrollChanged;
        RebuildLines();
        InvalidateVisual();
    }

    private void OnEditorChanged(object? sender, EventArgs e) => MarkDirty(rebuild: true);
    private void OnScrollChanged(object? sender, EventArgs e) => MarkDirty(rebuild: false);

    private bool _needRebuild;

    /// <summary>Coalesce bursts (typing / typewriter reveal) into ~10 fps redraws.</summary>
    private void MarkDirty(bool rebuild)
    {
        _needRebuild |= rebuild;
        if (_throttle == null)
        {
            _throttle = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100),
            };
            _throttle.Tick += (_, _) =>
            {
                if (!_dirty) { _throttle!.Stop(); return; }
                _dirty = false;
                if (_needRebuild) { _needRebuild = false; RebuildLines(); }
                InvalidateVisual();
            };
        }
        _dirty = true;
        if (!_throttle.IsEnabled) _throttle.Start();
    }

    private void RebuildLines()
    {
        _lines.Clear();
        string text = _editor?.Text ?? "";
        if (text.Length == 0) return;

        int maxLen = 1;
        int start = 0;
        var raw = new List<(int Len, bool Comment)>(256);
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n')
            {
                int len = i - start;
                // Detect a comment line cheaply: first two non-space chars.
                bool comment = false;
                for (int j = start; j < i; j++)
                {
                    char c = text[j];
                    if (c is ' ' or '\t') continue;
                    comment = c == '#'
                        || (c == '/' && j + 1 < i && (text[j + 1] == '/' || text[j + 1] == '*'))
                        || (c == '<' && j + 3 < i && text[j + 1] == '!' && text[j + 2] == '-');
                    break;
                }
                raw.Add((len, comment));
                if (len > maxLen) maxLen = len;
                start = i + 1;
            }
        }

        foreach (var (len, comment) in raw)
            _lines.Add(((float)Math.Min(1.0, len / (double)Math.Min(maxLen, 120)), comment, len == 0));
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Invisible-but-hit-testable backplate
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_editor == null || _lines.Count == 0 || ActualHeight < 10) return;

        double usableW = Math.Max(4, ActualWidth - 10);
        int count = _lines.Count;
        double yPerLine = ActualHeight / count;
        double barH = Math.Clamp(yPerLine * 0.72, 0.5, 3.0);

        // When lines outnumber pixels, sample so we draw ≤ one bar per pixel row.
        int step = Math.Max(1, (int)Math.Ceiling(count / Math.Max(1.0, ActualHeight)));
        for (int i = 0; i < count; i += step)
        {
            var (len, comment, blank) = _lines[i];
            if (blank) continue;
            double y = i * yPerLine;
            double w = Math.Max(2, len * usableW);
            dc.DrawRectangle(comment ? CommentBrush : BarBrush, null, new Rect(5, y, w, barH));
        }

        // Viewport window
        var view = _editor.TextArea.TextView;
        double docH = Math.Max(view.DocumentHeight, 1);
        double ratio = ActualHeight / Math.Max(docH, view.ActualHeight);
        double vpY = view.ScrollOffset.Y * ratio;
        double vpH = Math.Max(12, view.ActualHeight * ratio);
        dc.DrawRoundedRectangle(ViewportBrush, ViewportPen, new Rect(1, vpY, Math.Max(2, ActualWidth - 2), Math.Min(vpH, ActualHeight)), 2, 2);
    }

    // ── Click / drag to jump ──

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        CaptureMouse();
        JumpTo(e.GetPosition(this).Y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) JumpTo(e.GetPosition(this).Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void JumpTo(double y)
    {
        if (_editor == null || ActualHeight < 1) return;
        var view = _editor.TextArea.TextView;
        double docH = Math.Max(view.DocumentHeight, 1);
        double target = (y / ActualHeight) * docH - view.ActualHeight / 2;
        _editor.ScrollToVerticalOffset(Math.Max(0, target));
    }
}
