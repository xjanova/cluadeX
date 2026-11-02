using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services;
using CluadeX.Services.Problems;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace CluadeX.Views.Editor;

/// <summary>
/// A thin bar left of the code on the lines the AI added (green) or changed (blue), and a red wedge where it deleted
/// lines — as in VS Code's change gutter, but against the file before the AI's last turn rather than git. Clicking a
/// mark raises <see cref="HunkClicked"/> (the editor opens the before/after peek there).
/// </summary>
public sealed class AiChangeMargin : AbstractMargin
{
    private const double BarWidth = 3, Width = 6;
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

    public static Brush BrushFor(AiChangeKind kind) => kind switch
    {
        AiChangeKind.Added => ThemeService.BrushOf("Status.Success", Brushes.SeaGreen),
        AiChangeKind.Modified => ThemeService.BrushOf("Status.Info", Brushes.SteelBlue),
        _ => ThemeService.BrushOf("Status.Danger", Brushes.IndianRed),
    };

    protected override Size MeasureOverride(Size availableSize) => new(_hunks.Count > 0 ? Width : 0, 0);

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView != null) oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView != null) newTextView.VisualLinesChanged += OnVisualLinesChanged;
        InvalidateVisual();
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent fill: the whole strip takes clicks, not only the painted bars.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var view = TextView;
        if (view == null || !view.VisualLinesValid || _hunks.Count == 0) return;
        var lines = view.VisualLines;
        if (lines.Count == 0) return;
        int first = lines[0].FirstDocumentLine.LineNumber, last = lines[^1].LastDocumentLine.LineNumber;
        int docLines = view.Document?.LineCount ?? last;
        var visible = _hunks.Where(h => h.EndLine >= first - 1 && h.StartLine <= last + 1).ToList();
        if (visible.Count == 0) return;
        foreach (var vl in lines)
        {
            int line = vl.FirstDocumentLine.LineNumber;
            double top = vl.VisualTop - view.VerticalOffset, height = vl.Height;
            foreach (var h in visible)
            {
                if (h.LineCount > 0 && line >= h.StartLine && line <= h.EndLine)
                    dc.DrawRectangle(BrushFor(h.Kind), null, new Rect(1, top, BarWidth, height));
                else if (h.LineCount == 0 && (h.StartLine == line || h.StartLine > docLines && line == docLines))
                    DrawWedge(dc, h.StartLine == line ? top : top + height);
            }
        }
    }

    private static void DrawWedge(DrawingContext dc, double y)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, y - 4), true, true);
            c.LineTo(new Point(Width, y), true, false);
            c.LineTo(new Point(0, y + 4), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(BrushFor(AiChangeKind.Removed), null, g);
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
