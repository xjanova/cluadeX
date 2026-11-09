using System.Windows;
using System.Windows.Media;
using CluadeX.Services.Problems;
using ICSharpCode.AvalonEdit.Rendering;

namespace CluadeX.Views.Editor;

/// <summary>
/// The change marks across the code itself (owner 2026-11-09: "อยากให้ชัดเจน เมื่อเปิดในหน้า editor"): each run of lines
/// the AI added or changed gets a tint of its colour that is strongest by the gutter and fades across the code, a run the
/// user edited by hand the user's tint, and where lines were deleted a dashed rule in the deleted colour runs between the
/// lines that stayed. A new mark sweeps in from the gutter and the one you land on flashes (<see cref="ChangeMarkFx"/>).
/// Only the lines on screen are painted, one rectangle per run. Colours are the Editor.Change.* theme tokens.
/// </summary>
public sealed class ChangeLineRenderer : IBackgroundRenderer
{
    private IReadOnlyList<AiChangeHunk> _hunks = Array.Empty<AiChangeHunk>();
    private static readonly DashStyle s_dash = Frozen(new DashStyle(new double[] { 3, 2 }, 0));

    public ChangeMarkFx? Fx { get; set; }

    public KnownLayer Layer => KnownLayer.Background;

    public IReadOnlyList<AiChangeHunk> Hunks => _hunks;

    public void Set(IReadOnlyList<AiChangeHunk> hunks) => _hunks = hunks;

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (_hunks.Count == 0 || textView.Document == null || !textView.VisualLinesValid) return;
        var lines = textView.VisualLines;
        if (lines.Count == 0) return;
        double width = textView.ActualWidth;   // the view draws in its own (unscrolled) coordinates
        foreach (var span in Spans(textView, _hunks))
        {
            var h = span.Hunk;
            double p = Fx?.Appear(h) ?? 1, flash = Fx?.Flash(h) ?? 0;
            var mark = ChangeMarkPaint.ColorFor(h.Kind, h.ByUser);
            if (h.LineCount == 0)
            {
                // A deletion: the dashed rule runs across as it appears, a soft band under it while it flashes.
                double y = Math.Round(span.Top) + 0.5;
                if (flash > 0) dc.DrawRectangle(ChangeMarkPaint.Solid(ChangeMarkPaint.Alpha(mark, 0.28 * flash)), null, new Rect(0, y - 3, width, 6));
                var pen = new Pen(ChangeMarkPaint.Solid(mark), 1.5) { DashStyle = s_dash };
                pen.Freeze();
                dc.DrawLine(pen, new Point(0, y), new Point(Math.Max(1, width * p), y));
                continue;
            }
            var rect = new Rect(0, span.Top, width, Math.Max(0, span.Bottom - span.Top));
            var tint = ChangeMarkPaint.TintColorFor(h);
            if (h.ByUser)
            {
                // The user's own: no sweep (they are typing there), it fades in.
                dc.PushOpacity(p);
                dc.DrawRectangle(ChangeMarkPaint.Tint(tint), null, rect);
                dc.Pop();
            }
            else if (p >= 1) dc.DrawRectangle(ChangeMarkPaint.Tint(tint), null, rect);
            else
            {
                double front = width * p;
                dc.DrawRectangle(ChangeMarkPaint.Tint(tint), null, new Rect(0, rect.Top, front, rect.Height));
                double edge = Math.Min(28, front);
                if (edge > 1)
                {
                    dc.PushOpacity(1 - p);
                    dc.DrawRectangle(ChangeMarkPaint.Edge(mark), null, new Rect(front - edge, rect.Top, edge, rect.Height));
                    dc.Pop();
                }
            }
            if (flash > 0) dc.DrawRectangle(ChangeMarkPaint.Solid(ChangeMarkPaint.Alpha(mark, 0.30 * flash)), null, rect);
        }
    }

    /// <summary>A mark's part on screen: the top and bottom of its first / last visible line (a deletion: where it sits).</summary>
    internal readonly record struct Span(AiChangeHunk Hunk, double Top, double Bottom);

    internal static IEnumerable<Span> Spans(TextView view, IReadOnlyList<AiChangeHunk> hunks)
    {
        var lines = view.VisualLines;
        int first = lines[0].FirstDocumentLine.LineNumber, last = lines[^1].LastDocumentLine.LineNumber;
        int docLines = view.Document?.LineCount ?? last;
        foreach (var h in hunks)
        {
            if (h.EndLine < first - 1 || h.StartLine > last + 1) continue;
            double top = double.NaN, bottom = double.NaN;
            foreach (var vl in lines)
            {
                int line = vl.FirstDocumentLine.LineNumber;
                double y = vl.VisualTop - view.VerticalOffset;
                if (h.LineCount > 0)
                {
                    if (line < h.StartLine || line > h.EndLine) continue;
                    if (double.IsNaN(top)) top = y;
                    bottom = y + vl.Height;
                }
                else if (h.StartLine == line) { top = bottom = y; break; }
                else if (h.StartLine > docLines && line == docLines) { top = bottom = y + vl.Height; break; }
            }
            if (!double.IsNaN(top)) yield return new Span(h, top, bottom);
        }
    }

    private static DashStyle Frozen(DashStyle d) { d.Freeze(); return d; }
}
