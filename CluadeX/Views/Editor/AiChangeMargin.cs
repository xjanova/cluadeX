using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services;
using CluadeX.Services.Problems;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace CluadeX.Views.Editor;

/// <summary>
/// A bar left of the code on the lines the AI added (green) or changed (blue), a red wedge where it deleted lines, and
/// the lines the user edited by hand in their own colour (orange) — as in VS Code's change gutter, but against the file
/// before this chat touched it rather than git. Colours are theme tokens (Editor.Change.*), so every theme, light or
/// dark, picks its own. Each run of lines is one rounded pill with a sheen (the user's with a pin head, so they read by
/// shape too); new marks grow in and the one you land on flashes (<see cref="ChangeMarkFx"/>). Clicking a mark raises
/// <see cref="HunkClicked"/> (the editor opens the before/after peek there).
/// </summary>
public sealed class AiChangeMargin : AbstractMargin
{
    private const double BarWidth = 4, Width = 8;
    private IReadOnlyList<AiChangeHunk> _hunks = Array.Empty<AiChangeHunk>();

    public event Action<AiChangeHunk>? HunkClicked;

    public IReadOnlyList<AiChangeHunk> Hunks => _hunks;

    public void Set(IReadOnlyList<AiChangeHunk> hunks)
    {
        bool resize = (_hunks.Count > 0) != (hunks.Count > 0);
        _hunks = hunks;
        Cursor = hunks.Count > 0 ? Cursors.Hand : null;
        if (resize) InvalidateMeasure();
        InvalidateVisual();
    }

    public static Brush BrushFor(AiChangeKind kind, bool byUser = false) => byUser
        ? ThemeService.BrushOf("Editor.Change.User", Brushes.DarkOrange)
        : kind switch
        {
            AiChangeKind.Added => ThemeService.BrushOf("Editor.Change.Added", Brushes.SeaGreen),
            AiChangeKind.Modified => ThemeService.BrushOf("Editor.Change.Modified", Brushes.SteelBlue),
            _ => ThemeService.BrushOf("Editor.Change.Removed", Brushes.IndianRed),
        };

    /// <summary>The background of a changed line (null for a deleted-lines mark: it has no line).</summary>
    public static Brush? TintFor(AiChangeHunk h) => h.LineCount == 0 ? null : h.ByUser
        ? ThemeService.BrushOf("Editor.Change.User.Tint", null)
        : h.Kind == AiChangeKind.Added
            ? ThemeService.BrushOf("Editor.Change.Added.Tint", null)
            : ThemeService.BrushOf("Editor.Change.Modified.Tint", null);

    protected override Size MeasureOverride(Size availableSize) => new(_hunks.Count > 0 ? Width : 0, 0);

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView != null) oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView != null) newTextView.VisualLinesChanged += OnVisualLinesChanged;
        InvalidateVisual();
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>The motion (shared with the line renderer).</summary>
    public ChangeMarkFx? Fx { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent fill: the whole strip takes clicks, not only the painted bars.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var view = TextView;
        if (view == null || !view.VisualLinesValid || _hunks.Count == 0 || view.VisualLines.Count == 0) return;
        foreach (var span in ChangeLineRenderer.Spans(view, _hunks))
        {
            var h = span.Hunk;
            double p = Fx?.Appear(h) ?? 1, flash = Fx?.Flash(h) ?? 0;
            var c = ChangeMarkPaint.ColorFor(h.Kind, h.ByUser);
            if (h.LineCount == 0) { DrawWedge(dc, span.Top, c, p * (1 + 0.35 * flash)); continue; }
            // One pill per run of lines (not a bar per line): it grows from its middle as it appears, widens as it flashes.
            double full = Math.Max(2, span.Bottom - span.Top - 2), height = full * p;
            double mid = (span.Top + span.Bottom) / 2, w = BarWidth + 2 * flash;
            var rect = new Rect(4 - w / 2, mid - height / 2, w, height);
            if (flash > 0) dc.DrawRoundedRectangle(ChangeMarkPaint.Solid(ChangeMarkPaint.Alpha(c, 0.35 * flash)), null, Inflate(rect, 2), 4, 4);
            dc.DrawRoundedRectangle(ChangeMarkPaint.Pill(c), null, rect, 2, 2);
            // The user's own carry a pin head: told apart by shape as well as colour.
            if (h.ByUser && p > 0.6) dc.DrawEllipse(ChangeMarkPaint.Solid(c), null, new Point(4, rect.Top + 2.5), 3 * p, 3 * p);
        }
    }

    private static Rect Inflate(Rect r, double by) { r.Inflate(by, by); return r; }

    private static void DrawWedge(DrawingContext dc, double y, Color c, double scale)
    {
        if (scale <= 0) return;
        double half = 5 * scale, len = Width * Math.Min(1.2, scale);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0, y - half), true, true);
            ctx.LineTo(new Point(len, y), true, false);
            ctx.LineTo(new Point(0, y + half), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(ChangeMarkPaint.Solid(c), null, g);
    }

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        => new PointHitTestResult(this, hitTestParameters.HitPoint);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var view = TextView;
        if (view == null || _hunks.Count == 0) return;
        var vl = view.GetVisualLineFromVisualTop(e.GetPosition(view).Y + view.VerticalOffset);
        if (vl == null) return;
        int line = vl.FirstDocumentLine.LineNumber;
        var hit = _hunks.FirstOrDefault(h => h.Covers(line));
        if (hit == null) return;
        e.Handled = true;
        HunkClicked?.Invoke(hit);
    }
}
