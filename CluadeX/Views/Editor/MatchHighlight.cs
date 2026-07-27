using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CluadeX.Views.Editor;

/// <summary>
/// Attached properties that split a TextBlock's text into three Runs — before / match / after — so a
/// search result shows WHERE in the line it matched, the way every real search panel does.
///
/// WPF can't data-bind <c>TextBlock.Inlines</c>, so the usual "bind a converter that returns runs"
/// trick doesn't work; an attached property that rebuilds the inlines is the standard fix.
/// Set <see cref="StartProperty"/> to -1 to render the text plainly (used for declaration lines,
/// which have no match span).
/// </summary>
public static class MatchHighlight
{
    private static readonly Brush HighlightBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xC5, 0x4C)));
    private static readonly Brush HighlightInk = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xD6)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(MatchHighlight),
        new PropertyMetadata("", OnChanged));

    public static readonly DependencyProperty StartProperty = DependencyProperty.RegisterAttached(
        "Start", typeof(int), typeof(MatchHighlight),
        new PropertyMetadata(-1, OnChanged));

    public static readonly DependencyProperty LengthProperty = DependencyProperty.RegisterAttached(
        "Length", typeof(int), typeof(MatchHighlight),
        new PropertyMetadata(0, OnChanged));

    public static void SetText(DependencyObject o, string v) => o.SetValue(TextProperty, v);
    public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);

    public static void SetStart(DependencyObject o, int v) => o.SetValue(StartProperty, v);
    public static int GetStart(DependencyObject o) => (int)o.GetValue(StartProperty);

    public static void SetLength(DependencyObject o, int v) => o.SetValue(LengthProperty, v);
    public static int GetLength(DependencyObject o) => (int)o.GetValue(LengthProperty);

    private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not TextBlock tb) return;

        string text = GetText(tb) ?? "";
        int start = GetStart(tb);
        int length = GetLength(tb);

        tb.Inlines.Clear();

        // No span (declaration lines), or a span that no longer fits the (windowed) preview text —
        // render plainly rather than throwing or highlighting the wrong characters.
        if (start < 0 || length <= 0 || start >= text.Length)
        {
            tb.Inlines.Add(new Run(text));
            return;
        }
        if (start + length > text.Length) length = text.Length - start;

        if (start > 0) tb.Inlines.Add(new Run(text[..start]));
        tb.Inlines.Add(new Run(text.Substring(start, length))
        {
            Background = HighlightBrush,
            Foreground = HighlightInk,
            FontWeight = FontWeights.SemiBold,
        });
        int after = start + length;
        if (after < text.Length) tb.Inlines.Add(new Run(text[after..]));
    }
}
