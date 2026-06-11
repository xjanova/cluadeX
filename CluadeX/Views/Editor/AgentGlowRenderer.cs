using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace CluadeX.Views.Editor;

/// <summary>
/// Background renderer that makes agent activity visible in the editor:
///   • While the agent is "typing" (live reveal), the current insertion region glows steadily.
///   • When the reveal completes, the whole changed region flashes and fades out over ~1.6s.
/// Drawn on the Background layer so it works regardless of keyboard focus (selection-based
/// highlighting disappears the moment focus returns to the chat panel — this doesn't).
/// </summary>
public class AgentGlowRenderer : IBackgroundRenderer
{
    private static readonly Color GlowColor = Color.FromRgb(0xA6, 0x72, 0xFF);   // neon violet
    private const double FadeMs = 1600;

    private int _start = -1;
    private int _length;
    private DateTime _shownAt;
    private bool _fading;   // false = steady glow (typing in progress), true = timed fade-out

    /// <summary>Steady glow while the agent is still typing into this region.</summary>
    public void ShowTyping(int start, int length)
    {
        _start = start;
        _length = Math.Max(0, length);
        _fading = false;
    }

    /// <summary>Switch to the completion flash: full-strength highlight fading to nothing.</summary>
    public void ShowFlash(int start, int length)
    {
        _start = start;
        _length = Math.Max(0, length);
        _shownAt = DateTime.UtcNow;
        _fading = true;
    }

    public void Clear() => _start = -1;

    /// <summary>True while there is still something to draw (the View's invalidation timer polls this).</summary>
    public bool IsActive => _start >= 0;

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_start < 0 || _length <= 0) return;
        var doc = textView.Document;
        if (doc == null) return;

        double alpha = 1.0;
        if (_fading)
        {
            double age = (DateTime.UtcNow - _shownAt).TotalMilliseconds;
            if (age >= FadeMs) { _start = -1; return; }
            alpha = 1.0 - (age / FadeMs);
        }

        int start = Math.Min(_start, doc.TextLength);
        int end = Math.Min(_start + _length, doc.TextLength);
        if (end <= start) return;

        var segment = new TextSegment { StartOffset = start, EndOffset = end };
        var fill = new SolidColorBrush(Color.FromArgb((byte)(0x30 * alpha), GlowColor.R, GlowColor.G, GlowColor.B));
        var line = new SolidColorBrush(Color.FromArgb((byte)(0xA0 * alpha), GlowColor.R, GlowColor.G, GlowColor.B));
        fill.Freeze();
        line.Freeze();
        var pen = new Pen(line, 1.0);
        pen.Freeze();

        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
        {
            // Soft rounded glow band over the text…
            var r = new Rect(rect.X - 1, rect.Y, Math.Max(rect.Width + 2, 24), rect.Height);
            drawingContext.DrawRoundedRectangle(fill, null, r, 3, 3);
            // …plus a VS-style change bar on the far left edge of the view.
            drawingContext.DrawRectangle(line, null, new Rect(0, rect.Y, 3, rect.Height));
        }
    }
}
