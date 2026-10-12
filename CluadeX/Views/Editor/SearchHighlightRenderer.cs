using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using CluadeX.Services;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace CluadeX.Views.Editor;

/// <summary>
/// Paints the find bar's matches (Editor.Match) and, Notepad++'s "smart highlighting", every other occurrence of
/// the word that is selected (an outline in the accent colour). Only the lines on screen are searched, so it costs
/// the same in a 50 MB file as in a 50-line one; a single line longer than <see cref="MaxLineChars"/> (minified
/// code) is skipped rather than searched on every repaint.
/// </summary>
public sealed class SearchHighlightRenderer : IBackgroundRenderer
{
    public const int MaxLineChars = 20_000;

    /// <summary>The find bar's pattern while it is open (null = nothing to mark).</summary>
    public Regex? Find { get; set; }
    /// <summary>The selected word, matched whole and case-sensitively (null = none).</summary>
    public Regex? Smart { get; set; }

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if ((Find == null && Smart == null) || textView.Document == null || !textView.VisualLinesValid) return;
        var findBrush = ThemeService.BrushOf("Editor.Match");
        var smartPen = new Pen(ThemeService.BrushOf("Accent.1"), 1);
        var smartFill = ThemeService.BrushOf("Accent.1.Tint");
        var doc = textView.Document;
        foreach (var visual in textView.VisualLines)
        {
            int start = visual.FirstDocumentLine.Offset;
            int end = visual.LastDocumentLine.EndOffset;
            if (end - start > MaxLineChars) continue;
            string text = doc.GetText(start, end - start);
            if (Find != null) Paint(textView, drawingContext, Find, text, start, findBrush, null);
            if (Smart != null) Paint(textView, drawingContext, Smart, text, start, smartFill, smartPen);
        }
    }

    private static void Paint(TextView view, DrawingContext dc, Regex rx, string text, int offset, Brush fill, Pen? pen)
    {
        try
        {
            for (var m = rx.Match(text); m.Success; m = m.NextMatch())
            {
                if (m.Length == 0) continue;
                var segment = new TextSegment { StartOffset = offset + m.Index, Length = m.Length };
                foreach (Rect r in BackgroundGeometryBuilder.GetRectsForSegment(view, segment))
                    dc.DrawRoundedRectangle(fill, pen, r, 2, 2);
            }
        }
        catch (RegexMatchTimeoutException) { /* a pathological pattern: leave this line unmarked */ }
    }
}
