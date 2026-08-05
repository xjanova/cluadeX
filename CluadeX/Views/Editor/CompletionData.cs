using System.Windows.Controls;
using System.Windows.Media;
using CluadeX.Services;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace CluadeX.Views.Editor;

/// <summary>
/// One row in the editor's completion popup. Renders the label plus a coloured kind tag, so a
/// suggestion always says where it came from (a symbol declared in the project, a word already in
/// this file, a language keyword, or the language server) instead of appearing from nowhere.
/// </summary>
public sealed class CompletionData : ICompletionData
{
    private readonly CompletionItem _item;

    public CompletionData(CompletionItem item) => _item = item;

    public System.Windows.Media.ImageSource? Image => null;

    public string Text => _item.Label;

    /// <summary>AvalonEdit sorts by descending Priority; our score is already the ranking.</summary>
    public double Priority => _item.Score;

    public object Content
    {
        get
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text = _item.Label,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            });
            if (!string.IsNullOrEmpty(_item.Kind))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = _item.Kind,
                    FontSize = 9,
                    Margin = new System.Windows.Thickness(8, 0, 0, 0),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    Foreground = KindBrush(_item.Kind),
                });
            }
            return panel;
        }
    }

    public object Description =>
        string.IsNullOrEmpty(_item.Detail) ? _item.Label : $"{_item.Label}\n{_item.Detail}";

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        => textArea.Document.Replace(completionSegment, _item.Label);

    private static Brush KindBrush(string kind) => kind switch
    {
        "lsp" => Frozen(0x7E, 0xE0, 0xA3),
        "class" or "interface" or "struct" or "enum" or "record" or "type" => Frozen(0x8A, 0xB8, 0xFF),
        "method" => Frozen(0xFF, 0xD3, 0x7E),
        "keyword" => Frozen(0xC7, 0x9A, 0xFF),
        _ => Frozen(0x8A, 0x8F, 0xB0),
    };

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
