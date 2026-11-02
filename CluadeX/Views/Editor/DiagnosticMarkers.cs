using System.Windows;
using System.Windows.Media;
using CluadeX.Services;
using CluadeX.Services.Lsp;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace CluadeX.Views.Editor;

/// <summary>A language server's problem pinned to the text: a segment that moves with the edits around it.</summary>
public sealed class DiagnosticMarker : TextSegment
{
    public DiagnosticMarker(EditorDiagnostic diagnostic) => Diagnostic = diagnostic;
    public EditorDiagnostic Diagnostic { get; }
}

/// <summary>
/// The squiggles under the language server's errors (red), warnings (amber) and notes (blue). The markers live in a
/// <see cref="TextSegmentCollection{T}"/> tied to the document, so typing above an error moves its squiggle with the
/// text until the server's next answer replaces them. Only the markers on the lines on screen are drawn. Hints are
/// not drawn (VS Code shows them as faint dots; here they would only be noise).
/// </summary>
public sealed class DiagnosticMarkerRenderer : IBackgroundRenderer
{
    private TextSegmentCollection<DiagnosticMarker>? _markers;
    private TextDocument? _document;

    public KnownLayer Layer => KnownLayer.Selection;

    public int Count => _markers?.Count ?? 0;

    public IEnumerable<DiagnosticMarker> Markers => (IEnumerable<DiagnosticMarker>?)_markers ?? Array.Empty<DiagnosticMarker>();

    /// <summary>Show <paramref name="diagnostics"/> on <paramref name="document"/>, replacing what was shown.</summary>
    public void Set(TextDocument document, IEnumerable<EditorDiagnostic> diagnostics)
    {
        if (!ReferenceEquals(_document, document) || _markers == null)
        {
            _document = document;
            _markers = new TextSegmentCollection<DiagnosticMarker>(document);
        }
        _markers.Clear();
        foreach (var d in diagnostics)
        {
            if (d.Severity == LspSeverity.Hint) continue;
            var (start, length) = Map(document, d);
            _markers.Add(new DiagnosticMarker(d) { StartOffset = start, Length = length });
        }
    }

    public void Clear() => _markers?.Clear();

    /// <summary>The markers that cover <paramref name="offset"/> (most severe first) — what a hover shows.</summary>
    public List<DiagnosticMarker> At(int offset)
    {
        if (_markers == null) return new();
        return _markers.FindSegmentsContaining(offset)
            .Concat(offset > 0 ? _markers.FindSegmentsContaining(offset - 1) : Enumerable.Empty<DiagnosticMarker>())
            .Distinct()
            .OrderBy(m => m.Diagnostic.Severity)
            .ToList();
    }

    /// <summary>
    /// An LSP range → a document segment (start offset, length). Positions past the end of a line or of the document
    /// are clamped (the server may be one edit behind); an empty range is widened to the word it touches, or to one
    /// character, so a "missing ;" at the end of a line still shows a squiggle.
    /// </summary>
    public static (int Start, int Length) Map(TextDocument document, EditorDiagnostic d)
    {
        int start = OffsetOf(document, d.StartLine, d.StartCharacter);
        int end = Math.Max(start, OffsetOf(document, d.EndLine, d.EndCharacter));
        if (end > start) return (start, end - start);

        int textLength = document.TextLength;
        if (textLength == 0) return (0, 0);
        int s = start, e = start;
        while (s > 0 && IsWordChar(document.GetCharAt(s - 1))) s--;
        while (e < textLength && IsWordChar(document.GetCharAt(e))) e++;
        if (e > s) return (s, e - s);
        // Not on a word: the character before (end of a line), else the one after.
        var line = document.GetLineByOffset(start);
        if (start > line.Offset) return (start - 1, 1);
        if (start < line.EndOffset) return (start, 1);
        return (start, 0);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int OffsetOf(TextDocument document, int line, int character)
    {
        if (document.LineCount == 0) return 0;
        if (line >= document.LineCount) return document.TextLength;
        var docLine = document.GetLineByNumber(Math.Max(0, line) + 1);
        return docLine.Offset + Math.Clamp(character, 0, docLine.Length);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_markers == null || _markers.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
        int viewStart = textView.VisualLines[0].FirstDocumentLine.Offset;
        int viewEnd = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        var pens = new Dictionary<LspSeverity, Pen>();
        foreach (var marker in _markers.FindOverlappingSegments(viewStart, viewEnd - viewStart))
        {
            if (!pens.TryGetValue(marker.Diagnostic.Severity, out var pen))
            {
                pen = new Pen(BrushFor(marker.Diagnostic.Severity), 1);
                pen.Freeze();
                pens[marker.Diagnostic.Severity] = pen;
            }
            // An empty line's problem has nothing to underline; it is still in the Problems list.
            foreach (Rect r in BackgroundGeometryBuilder.GetRectsForSegment(textView, marker))
                drawingContext.DrawGeometry(null, pen, Squiggle(r.BottomLeft, Math.Max(r.Width, 6)));
        }
    }

    public static Brush BrushFor(LspSeverity severity) => severity switch
    {
        LspSeverity.Error => ThemeService.BrushOf("Status.Danger", Brushes.Red),
        LspSeverity.Warning => ThemeService.BrushOf("Status.Warning", Brushes.Orange),
        _ => ThemeService.BrushOf("Status.Info", Brushes.DodgerBlue),
    };

    /// <summary>The zig-zag line VS Code draws: 2 px up and down every 2 px, just under the text.</summary>
    private static StreamGeometry Squiggle(Point start, double width)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            double y = start.Y - 1;
            ctx.BeginFigure(new Point(start.X, y), false, false);
            bool up = true;
            for (double x = start.X + 2; x <= start.X + width + 0.5; x += 2, up = !up)
                ctx.LineTo(new Point(x, up ? y - 2 : y), true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
