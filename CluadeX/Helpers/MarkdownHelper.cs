using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Input;

namespace CluadeX.Helpers;

/// <summary>
/// Attached property that renders Markdown into a StackPanel.
/// Supports: headers, bold, italic, inline code, code blocks (syntax highlighted),
/// links, lists (ordered/unordered), blockquotes, tables, horizontal rules,
/// diff highlighting (+/-), and strikethrough.
///
/// Uses stable-prefix incremental rendering during streaming:
/// only re-renders the last "unstable" block while tokens arrive.
/// </summary>
public static class MarkdownHelper
{
    // ─── Frozen Brushes (GC-friendly, allocated once) ───
    private static readonly SolidColorBrush CrustBrush = Freeze(0x11, 0x11, 0x1B);
    private static readonly SolidColorBrush Surface0Brush = Freeze(0x31, 0x32, 0x44);
    private static readonly SolidColorBrush Surface1Brush = Freeze(0x45, 0x47, 0x5A);
    private static readonly SolidColorBrush Overlay0Brush = Freeze(0x6C, 0x70, 0x86);
    private static readonly SolidColorBrush TextBrush = Freeze(0xCD, 0xD6, 0xF4);
    private static readonly SolidColorBrush TealBrush = Freeze(0x94, 0xE2, 0xD5);
    private static readonly SolidColorBrush GreenBrush = Freeze(0xA6, 0xE3, 0xA1);
    private static readonly SolidColorBrush BlueBrush = Freeze(0x89, 0xB4, 0xFA);
    private static readonly SolidColorBrush PeachBrush = Freeze(0xFA, 0xB3, 0x87);
    private static readonly SolidColorBrush MauveBrush = Freeze(0xCB, 0xA6, 0xF7);
    private static readonly SolidColorBrush RedBrush = Freeze(0xF3, 0x8B, 0xA8);
    private static readonly SolidColorBrush YellowBrush = Freeze(0xF9, 0xE2, 0xAF);
    private static readonly SolidColorBrush RosewaterBrush = Freeze(0xF5, 0xE0, 0xDC);
    private static readonly SolidColorBrush DiffAddBgBrush = FreezeA(0x30, 0xA6, 0xE3, 0xA1);
    private static readonly SolidColorBrush DiffDelBgBrush = FreezeA(0x30, 0xF3, 0x8B, 0xA8);
    private static readonly SolidColorBrush BlockquoteBgBrush = FreezeA(0x18, 0xCB, 0xA6, 0xF7);

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var b2 = new SolidColorBrush(Color.FromRgb(r, g, b));
        b2.Freeze();
        return b2;
    }

    private static SolidColorBrush FreezeA(byte a, byte r, byte g, byte b)
    {
        var b2 = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        b2.Freeze();
        return b2;
    }

    private static readonly FontFamily MonoFont = new("Cascadia Code, Consolas, Courier New");
    private static readonly FontFamily MainFont = new("Segoe UI, Arial");

    // ─── Regex Patterns ───
    private static readonly Regex CodeBlockRegex = new(
        @"```(\w*)\s*\r?\n([\s\S]*?)```",
        RegexOptions.Compiled);

    private static readonly Regex InlineRegex = new(
        @"\*\*\*(.+?)\*\*\*|" +           // ***bold italic*** (group 1)
        @"\*\*(.+?)\*\*|" +               // **bold** (group 2)
        @"\*(.+?)\*|" +                    // *italic* (group 3)
        @"~~(.+?)~~|" +                    // ~~strikethrough~~ (group 4)
        @"`([^`\n]+?)`|" +                // `inline code` (group 5)
        @"\[([^\]]+)\]\(([^)]+)\)",        // [text](url) (group 6=text, 7=url)
        RegexOptions.Compiled);

    private static readonly Regex HeaderRegex = new(
        @"^(#{1,6})\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex HrRegex = new(
        @"^(?:---|\*\*\*|___)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex UnorderedListRegex = new(
        @"^[\s]*[-*+]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex OrderedListRegex = new(
        @"^[\s]*(\d+)\.\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // ─── Stable-prefix tracking via attached DependencyProperty (no leak) ───

    private static readonly DependencyProperty StableCountProperty =
        DependencyProperty.RegisterAttached(
            "StableCount",
            typeof(int),
            typeof(MarkdownHelper),
            new PropertyMetadata(0));

    // ─── Attached Property ───

    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.RegisterAttached(
            "Markdown",
            typeof(string),
            typeof(MarkdownHelper),
            new PropertyMetadata(null, OnMarkdownChanged));

    public static string GetMarkdown(DependencyObject obj) => (string)obj.GetValue(MarkdownProperty);
    public static void SetMarkdown(DependencyObject obj, string value) => obj.SetValue(MarkdownProperty, value);

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not StackPanel panel) return;
        string text = e.NewValue as string ?? "";

        if (string.IsNullOrEmpty(text))
        {
            panel.Children.Clear();
            panel.SetValue(StableCountProperty, 0);
            return;
        }

        var segments = ParseSegments(text);
        int stableCount = (int)panel.GetValue(StableCountProperty);

        // Incremental: if we have stable children, only re-render from stable boundary
        if (stableCount > 0
            && stableCount < segments.Count
            && panel.Children.Count >= stableCount)
        {
            // Remove unstable tail
            while (panel.Children.Count > stableCount)
                panel.Children.RemoveAt(panel.Children.Count - 1);

            // Render new segments from stable boundary onward
            for (int i = stableCount; i < segments.Count; i++)
                panel.Children.Add(RenderSegment(segments[i]));
        }
        else
        {
            // Full re-render
            panel.Children.Clear();
            foreach (var seg in segments)
                panel.Children.Add(RenderSegment(seg));
        }

        // Mark all but last segment as stable (last may still be growing)
        panel.SetValue(StableCountProperty, Math.Max(0, segments.Count - 1));
    }

    // ─── Segment Parsing ───

    private static List<MarkdownSegment> ParseSegments(string text)
    {
        var segments = new List<MarkdownSegment>();
        int pos = 0;

        foreach (Match m in CodeBlockRegex.Matches(text))
        {
            if (m.Index > pos)
                AddTextSegments(segments, text[pos..m.Index]);

            segments.Add(new MarkdownSegment { Type = SegmentType.CodeBlock, Code = m.Groups[2].Value.TrimEnd(), Language = m.Groups[1].Value });
            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
            AddTextSegments(segments, text[pos..]);

        return segments;
    }

    private static void AddTextSegments(List<MarkdownSegment> segments, string text)
    {
        var lines = text.Split('\n');
        var buffer = new List<string>();
        int i = 0;

        while (i < lines.Length)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();

            // Horizontal rule
            if (HrRegex.IsMatch(line))
            {
                FlushParagraph(segments, buffer);
                segments.Add(new MarkdownSegment { Type = SegmentType.HorizontalRule });
                i++;
                continue;
            }

            // Header
            var headerMatch = HeaderRegex.Match(line);
            if (headerMatch.Success)
            {
                FlushParagraph(segments, buffer);
                segments.Add(new MarkdownSegment
                {
                    Type = SegmentType.Header,
                    Text = headerMatch.Groups[2].Value.Trim(),
                    HeaderLevel = headerMatch.Groups[1].Value.Length
                });
                i++;
                continue;
            }

            // Blockquote (collect consecutive > lines)
            if (trimmed.StartsWith("> ") || trimmed == ">")
            {
                FlushParagraph(segments, buffer);
                var quoteLines = new List<string>();
                while (i < lines.Length)
                {
                    string ql = lines[i].TrimStart();
                    if (ql.StartsWith("> "))
                        quoteLines.Add(ql[2..]);
                    else if (ql == ">")
                        quoteLines.Add("");
                    else
                        break;
                    i++;
                }
                segments.Add(new MarkdownSegment { Type = SegmentType.Blockquote, Text = string.Join("\n", quoteLines) });
                continue;
            }

            // Unordered list — use Match once, check Success (avoid double regex)
            var ulMatch = UnorderedListRegex.Match(line);
            if (ulMatch.Success)
            {
                FlushParagraph(segments, buffer);
                var items = new List<string> { ulMatch.Groups[1].Value };
                i++;
                while (i < lines.Length)
                {
                    var m = UnorderedListRegex.Match(lines[i]);
                    if (!m.Success) break;
                    items.Add(m.Groups[1].Value);
                    i++;
                }
                segments.Add(new MarkdownSegment { Type = SegmentType.UnorderedList, ListItems = items });
                continue;
            }

            // Ordered list — use Match once
            var olMatch = OrderedListRegex.Match(line);
            if (olMatch.Success)
            {
                FlushParagraph(segments, buffer);
                var items = new List<string> { olMatch.Groups[2].Value };
                i++;
                while (i < lines.Length)
                {
                    var m = OrderedListRegex.Match(lines[i]);
                    if (!m.Success) break;
                    items.Add(m.Groups[2].Value);
                    i++;
                }
                segments.Add(new MarkdownSegment { Type = SegmentType.OrderedList, ListItems = items });
                continue;
            }

            // Table (| ... | rows)
            if (trimmed.StartsWith('|') && trimmed.EndsWith('|'))
            {
                FlushParagraph(segments, buffer);
                var tableRows = new List<string[]>();
                bool headerSepSeen = false;
                while (i < lines.Length)
                {
                    string tl = lines[i].Trim();
                    if (!tl.StartsWith('|') || !tl.EndsWith('|')) break;

                    if (tl.Replace("|", "").Replace("-", "").Replace(":", "").Trim().Length == 0)
                    {
                        headerSepSeen = true;
                        i++;
                        continue;
                    }

                    var cells = tl.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                    tableRows.Add(cells);
                    i++;
                }
                segments.Add(new MarkdownSegment { Type = SegmentType.Table, TableRows = tableRows, TableHasHeader = headerSepSeen });
                continue;
            }

            buffer.Add(line);
            i++;
        }

        FlushParagraph(segments, buffer);
    }

    private static void FlushParagraph(List<MarkdownSegment> segments, List<string> buffer)
    {
        if (buffer.Count == 0) return;
        string text = string.Join("\n", buffer).Trim();
        if (!string.IsNullOrEmpty(text))
            segments.Add(new MarkdownSegment { Type = SegmentType.Paragraph, Text = text });
        buffer.Clear();
    }

    // ─── Rendering ───

    private static UIElement RenderSegment(MarkdownSegment seg)
    {
        return seg.Type switch
        {
            SegmentType.CodeBlock => CreateCodeBlock(seg.Code, seg.Language),
            SegmentType.Header => CreateHeader(seg.Text, seg.HeaderLevel),
            SegmentType.HorizontalRule => CreateHorizontalRule(),
            SegmentType.Blockquote => CreateBlockquote(seg.Text),
            SegmentType.UnorderedList => CreateList(seg.ListItems, i => "\u2022"),
            SegmentType.OrderedList => CreateList(seg.ListItems, i => $"{i + 1}."),
            SegmentType.Table => CreateTable(seg.TableRows, seg.TableHasHeader),
            _ => CreateFormattedTextBlock(seg.Text),
        };
    }

    // ─── Header ───

    private static UIElement CreateHeader(string text, int level)
    {
        var (fontSize, weight) = level switch
        {
            1 => (22.0, FontWeights.Bold),
            2 => (18.0, FontWeights.Bold),
            3 => (16.0, FontWeights.SemiBold),
            4 => (14.0, FontWeights.SemiBold),
            _ => (13.0, FontWeights.Medium),
        };

        var fg = level <= 2 ? MauveBrush : level <= 4 ? BlueBrush : TextBrush;

        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = MainFont,
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = fg,
            Margin = new Thickness(0, level <= 2 ? 10 : 6, 0, 4),
        };
        ApplyInlineFormatting(tb, text);

        if (level <= 2)
        {
            var stack = new StackPanel();
            stack.Children.Add(tb);
            stack.Children.Add(new Border
            {
                Height = 1,
                Background = Surface0Brush,
                Margin = new Thickness(0, 2, 0, 4),
            });
            return stack;
        }

        return tb;
    }

    private static UIElement CreateHorizontalRule() => new Border
    {
        Height = 1, Background = Surface1Brush,
        Margin = new Thickness(0, 8, 0, 8),
    };

    // ─── Blockquote ───

    private static UIElement CreateBlockquote(string text)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = MainFont, FontSize = 13,
            FontStyle = FontStyles.Italic,
            Foreground = Overlay0Brush, LineHeight = 22,
        };
        ApplyInlineFormatting(tb, text);

        return new Border
        {
            BorderBrush = MauveBrush,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 6, 8, 6),
            Margin = new Thickness(0, 4, 0, 4),
            Background = BlockquoteBgBrush,
            Child = tb,
        };
    }

    // ─── Unified List (ordered + unordered) ───

    private static UIElement CreateList(List<string> items, Func<int, string> bulletFn)
    {
        var stack = new StackPanel { Margin = new Thickness(8, 4, 0, 4) };
        for (int i = 0; i < items.Count; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(new TextBlock
            {
                Text = bulletFn(i),
                Foreground = MauveBrush,
                FontSize = 13, FontFamily = MonoFont,
                MinWidth = 24,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Top,
            });
            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontFamily = MainFont, FontSize = 13,
                Foreground = TextBrush,
            };
            ApplyInlineFormatting(tb, items[i]);
            row.Children.Add(tb);
            stack.Children.Add(row);
        }
        return stack;
    }

    // ─── Table ───

    private static UIElement CreateTable(List<string[]> rows, bool hasHeader)
    {
        if (rows.Count == 0) return new TextBlock();

        int cols = rows.Max(r => r.Length);
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };

        for (int c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bool isHeader = hasHeader && r == 0;

            for (int c = 0; c < cols; c++)
            {
                string cellText = c < rows[r].Length ? rows[r][c] : "";
                var cell = new Border
                {
                    BorderBrush = Surface0Brush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 4, 8, 4),
                    Background = isHeader ? Surface0Brush : Brushes.Transparent,
                };
                var tb = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = isHeader ? MainFont : MonoFont,
                    FontSize = isHeader ? 12 : 11.5,
                    FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = isHeader ? TealBrush : TextBrush,
                };
                ApplyInlineFormatting(tb, cellText);
                cell.Child = tb;
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        return new Border
        {
            BorderBrush = Surface0Brush,
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(4),
            Child = grid,
        };
    }

    // ─── Code Block ───

    private static UIElement CreateCodeBlock(string code, string language)
    {
        bool isDiff = language.Equals("diff", StringComparison.OrdinalIgnoreCase)
                      || (code.Contains("\n+") && code.Contains("\n-"));

        var outerBorder = new Border
        {
            Background = CrustBrush,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Surface0Brush,
            Margin = new Thickness(0, 6, 0, 6),
        };

        var mainStack = new StackPanel();

        // Header bar
        var header = new Grid { Background = Surface0Brush };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(language) ? "code" : language,
            FontSize = 11, FontFamily = MonoFont,
            Foreground = Overlay0Brush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 6, 0, 6),
        });

        var copyBtn = CreateCopyButton(code);
        Grid.SetColumn(copyBtn, 1);
        header.Children.Add(copyBtn);
        mainStack.Children.Add(header);

        if (isDiff)
        {
            mainStack.Children.Add(CreateDiffContent(code));
        }
        else
        {
            var codeBlock = new TextBlock
            {
                FontFamily = MonoFont, FontSize = 12.5,
                Foreground = TextBrush,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(14, 10, 14, 12),
                LineHeight = 20,
            };
            ApplySyntaxHighlighting(codeBlock, code, language);
            mainStack.Children.Add(codeBlock);
        }

        outerBorder.Child = mainStack;
        return outerBorder;
    }

    // ─── Diff ───

    private static UIElement CreateDiffContent(string code)
    {
        var stack = new StackPanel();
        foreach (var line in code.Split('\n'))
        {
            SolidColorBrush? bg = null;
            var fg = TextBrush;

            if (line.StartsWith('+') && !line.StartsWith("+++"))
            { bg = DiffAddBgBrush; fg = GreenBrush; }
            else if (line.StartsWith('-') && !line.StartsWith("---"))
            { bg = DiffDelBgBrush; fg = RedBrush; }
            else if (line.StartsWith("@@"))
            { fg = BlueBrush; }

            var tb = new TextBlock
            {
                Text = line,
                FontFamily = MonoFont, FontSize = 12,
                Foreground = fg,
                Padding = new Thickness(14, 1, 14, 1),
                TextWrapping = TextWrapping.Wrap,
            };
            if (bg != null) tb.Background = bg;
            stack.Children.Add(tb);
        }
        return stack;
    }

    // ─── Clipboard Copy Button ───

    internal static Button CreateCopyButton(string textToCopy)
    {
        var copyBtn = new Button
        {
            Content = "\U0001F4CB Copy",
            FontSize = 10, FontFamily = MainFont,
            Foreground = Overlay0Brush,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Padding = new Thickness(8, 4, 12, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };

        copyBtn.Click += (_, _) =>
        {
            try { Clipboard.SetText(textToCopy); }
            catch { return; }
            CopyButtonFeedback(copyBtn, "\u2713 Copied!", "\U0001F4CB Copy");
        };

        copyBtn.MouseEnter += (_, _) => copyBtn.Foreground = TextBrush;
        copyBtn.MouseLeave += (_, _) => copyBtn.Foreground = Overlay0Brush;
        return copyBtn;
    }

    /// <summary>Briefly swap button content to show feedback, then revert.</summary>
    internal static void CopyButtonFeedback(Button btn, string feedback, string original)
    {
        btn.Content = feedback;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { btn.Content = original; timer.Stop(); };
        timer.Start();
    }

    // ─── Formatted Text ───

    private static TextBlock CreateFormattedTextBlock(string text)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 24, FontFamily = MainFont,
            FontSize = 13, Foreground = TextBrush,
            Margin = new Thickness(0, 2, 0, 2),
        };
        ApplyInlineFormatting(tb, text);
        return tb;
    }

    private static void ApplyInlineFormatting(TextBlock tb, string text)
    {
        int pos = 0;
        foreach (Match m in InlineRegex.Matches(text))
        {
            if (m.Index > pos)
                AddTextWithLineBreaks(tb, text[pos..m.Index]);

            if (m.Groups[1].Success) // ***bold italic***
            {
                tb.Inlines.Add(new Bold(new Italic(new Run(m.Groups[1].Value) { Foreground = TextBrush })));
            }
            else if (m.Groups[2].Success) // **bold**
            {
                tb.Inlines.Add(new Bold(new Run(m.Groups[2].Value) { Foreground = TextBrush }));
            }
            else if (m.Groups[3].Success) // *italic*
            {
                tb.Inlines.Add(new Italic(new Run(m.Groups[3].Value) { Foreground = RosewaterBrush }));
            }
            else if (m.Groups[4].Success) // ~~strikethrough~~
            {
                tb.Inlines.Add(new Run(m.Groups[4].Value)
                {
                    TextDecorations = TextDecorations.Strikethrough,
                    Foreground = Overlay0Brush,
                });
            }
            else if (m.Groups[5].Success) // `inline code`
            {
                var codeBorder = new Border
                {
                    Background = Surface0Brush,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(1, 0, 1, 0),
                    Child = new TextBlock
                    {
                        Text = m.Groups[5].Value,
                        FontFamily = MonoFont, FontSize = 12,
                        Foreground = TealBrush,
                    }
                };
                tb.Inlines.Add(new InlineUIContainer(codeBorder) { BaselineAlignment = BaselineAlignment.Center });
            }
            else if (m.Groups[6].Success) // [text](url)
            {
                string linkText = m.Groups[6].Value;
                string url = m.Groups[7].Value;

                var hyperlink = new Hyperlink(new Run(linkText))
                {
                    Foreground = BlueBrush,
                    TextDecorations = null,
                    Cursor = Cursors.Hand,
                    ToolTip = url,
                };
                hyperlink.MouseEnter += (_, _) => hyperlink.TextDecorations = TextDecorations.Underline;
                hyperlink.MouseLeave += (_, _) => hyperlink.TextDecorations = null;
                hyperlink.Click += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch { /* non-critical */ }
                };
                tb.Inlines.Add(hyperlink);
            }

            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
            AddTextWithLineBreaks(tb, text[pos..]);
    }

    private static void AddTextWithLineBreaks(TextBlock tb, string text)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) tb.Inlines.Add(new LineBreak());
            if (!string.IsNullOrEmpty(lines[i]))
                tb.Inlines.Add(new Run(lines[i]));
        }
    }

    // ─── Syntax Highlighting ───

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "using", "namespace", "class", "struct", "interface", "enum", "record",
        "public", "private", "protected", "internal", "static", "readonly", "const",
        "abstract", "sealed", "virtual", "override", "new", "partial",
        "void", "string", "int", "long", "bool", "double", "float", "decimal",
        "char", "byte", "short", "object", "var", "dynamic",
        "return", "if", "else", "for", "foreach", "while", "do", "switch", "case",
        "break", "continue", "default", "try", "catch", "finally", "throw",
        "async", "await", "Task", "null", "true", "false", "this", "base",
        "in", "out", "ref", "is", "as", "typeof", "sizeof", "nameof",
        "get", "set", "value", "where", "select", "from", "yield",
    };

    private static readonly HashSet<string> PythonKeywords = new(StringComparer.Ordinal)
    {
        "def", "class", "import", "from", "return", "if", "elif", "else",
        "for", "while", "break", "continue", "pass", "try", "except", "finally",
        "raise", "with", "as", "lambda", "yield", "global", "nonlocal",
        "and", "or", "not", "in", "is", "None", "True", "False", "self", "async", "await",
    };

    private static readonly HashSet<string> JsKeywords = new(StringComparer.Ordinal)
    {
        "function", "const", "let", "var", "return", "if", "else", "for", "while",
        "do", "switch", "case", "break", "continue", "default", "try", "catch",
        "finally", "throw", "class", "extends", "import", "export", "from",
        "async", "await", "yield", "new", "this", "super", "typeof", "instanceof",
        "null", "undefined", "true", "false", "of", "in", "delete", "void",
    };

    private static readonly HashSet<string> GenericKeywords = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "while", "return", "class", "function", "import",
        "true", "false", "null", "new", "try", "catch", "throw", "public", "private",
        "void", "int", "string", "bool", "var", "const", "let",
    };

    private static readonly Regex SyntaxRegex = new(
        @"(//[^\n]*|#[^\n]*)|" +
        @"(/\*[\s\S]*?\*/)|" +
        @"(""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*')|" +
        @"(\b\d+\.?\d*\b)|" +
        @"(@?\b[A-Za-z_]\w*\b)",
        RegexOptions.Compiled);

    private static void ApplySyntaxHighlighting(TextBlock tb, string code, string language)
    {
        var keywords = GetKeywordsForLanguage(language);

        int pos = 0;
        foreach (Match m in SyntaxRegex.Matches(code))
        {
            if (m.Index > pos)
                tb.Inlines.Add(new Run(code[pos..m.Index]) { Foreground = TextBrush });

            if (m.Groups[1].Success || m.Groups[2].Success)
                tb.Inlines.Add(new Run(m.Value) { Foreground = Overlay0Brush, FontStyle = FontStyles.Italic });
            else if (m.Groups[3].Success)
                tb.Inlines.Add(new Run(m.Value) { Foreground = GreenBrush });
            else if (m.Groups[4].Success)
                tb.Inlines.Add(new Run(m.Value) { Foreground = PeachBrush });
            else if (m.Groups[5].Success)
            {
                string word = m.Value;
                if (keywords.Contains(word))
                    tb.Inlines.Add(new Run(word) { Foreground = MauveBrush, FontWeight = FontWeights.SemiBold });
                else if (word.Length > 0 && char.IsUpper(word[0]) && word.Any(char.IsLower))
                    tb.Inlines.Add(new Run(word) { Foreground = YellowBrush });
                else
                    tb.Inlines.Add(new Run(word) { Foreground = TextBrush });
            }
            else
                tb.Inlines.Add(new Run(m.Value) { Foreground = TextBrush });

            pos = m.Index + m.Length;
        }

        if (pos < code.Length)
            tb.Inlines.Add(new Run(code[pos..]) { Foreground = TextBrush });
    }

    private static HashSet<string> GetKeywordsForLanguage(string lang)
    {
        return lang.ToLowerInvariant() switch
        {
            "csharp" or "cs" or "c#" => CSharpKeywords,
            "python" or "py" => PythonKeywords,
            "javascript" or "js" or "jsx" or "typescript" or "ts" or "tsx" => JsKeywords,
            "java" or "kotlin" or "kt" => CSharpKeywords,
            "go" or "rust" or "rs" or "cpp" or "c" => GenericKeywords,
            _ => GenericKeywords,
        };
    }

    // ─── Segment Model ───

    private enum SegmentType
    {
        Paragraph, CodeBlock, Header, HorizontalRule,
        Blockquote, UnorderedList, OrderedList, Table,
    }

    private class MarkdownSegment
    {
        public SegmentType Type { get; init; }
        public string Text { get; init; } = "";
        public string Code { get; init; } = "";
        public string Language { get; init; } = "";
        public int HeaderLevel { get; init; }
        public List<string> ListItems { get; init; } = new();
        public List<string[]> TableRows { get; init; } = new();
        public bool TableHasHeader { get; init; }
    }
}
