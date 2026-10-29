using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;

namespace CluadeX.Helpers;

/// <summary>
/// Attached property that renders Markdown into a StackPanel.
/// Supports: headers, bold, italic, inline code, code blocks (syntax highlighted),
/// links, pictures (local files), bare web addresses, lists (ordered/unordered), blockquotes, tables, horizontal rules,
/// diff highlighting (+/-), and strikethrough.
///
/// Uses stable-prefix incremental rendering during streaming:
/// only re-renders the last "unstable" block while tokens arrive.
/// </summary>
public static class MarkdownHelper
{
    // ─── Theme tokens (Resources/Themes/Tokens.md › Markdown.*) ───
    // Every colour and font is a resource REFERENCE (SetResourceReference), not a brush: rendered markdown follows a
    // theme switch live, and Midnight keeps the exact Catppuccin colours it always had.
    private const string TextKey = "Markdown.Text";
    private const string MutedKey = "Markdown.Muted";
    private const string HeadingKey = "Markdown.Heading";
    private const string HeadingMinorKey = "Markdown.Heading.Minor";
    private const string EmphasisKey = "Markdown.Emphasis";
    private const string LinkKey = "Markdown.Link";
    private const string RuleKey = "Markdown.Rule";
    private const string QuoteBgKey = "Markdown.Quote.Background";
    private const string QuoteBorderKey = "Markdown.Quote.Border";
    private const string CodeKey = "Markdown.Code";
    private const string CodeBgKey = "Markdown.Code.Background";
    private const string BorderKey = "Markdown.Border";
    private const string CodeBlockBgKey = "Markdown.CodeBlock.Background";
    private const string MainFontKey = "Font.Ui";
    private const string MonoFontKey = "Font.Mono";

    /// <summary>Inline code longer than this is set as a breakable run instead of an unbreakable chip.</summary>
    private const int LongInlineCode = 32;

    /// <summary>Point a property of an element built here at a theme token (follows theme switches by itself).</summary>
    private static T Tok<T>(T element, DependencyProperty property, string token) where T : DependencyObject
    {
        switch (element)
        {
            case FrameworkElement fe: fe.SetResourceReference(property, token); break;
            case FrameworkContentElement fce: fce.SetResourceReference(property, token); break;
        }
        return element;
    }

    private static T Fg<T>(T element, string token) where T : DependencyObject
        => Tok(element, TextElement.ForegroundProperty, token);

    private static TextBlock Font(TextBlock tb, bool mono) => Tok(tb, TextBlock.FontFamilyProperty, mono ? MonoFontKey : MainFontKey);

    private static Run TokRun(string text, string token) => Fg(new Run(text), token);

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
        @"\[([^\]]+)\]\(([^)]+)\)|" +       // [text](url) (group 6=text, 7=url)
        @"!\[([^\]]*)\]\(([^)]+)\)|" +      // ![alt](path) (group 8=alt, 9=path) — matched at the "!", before [text](url)
        @"(?<![\w/@])(https?://[^\s<>""'`)\]]*[^\s<>""'`)\].,;:!?])",   // a bare web address (group 10), not its last full stop
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

    /// <summary>The text last rendered into a panel (what its children show).</summary>
    private static readonly DependencyProperty RenderedTextProperty =
        DependencyProperty.RegisterAttached("RenderedText", typeof(string), typeof(MarkdownHelper), new PropertyMetadata(null));

    /// <summary>A panel that is waiting to be shown before it renders (see <see cref="OnMarkdownChanged"/>).</summary>
    private static readonly DependencyProperty WaitingProperty =
        DependencyProperty.RegisterAttached("Waiting", typeof(bool), typeof(MarkdownHelper), new PropertyMetadata(false));

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not StackPanel panel) return;
        // A row of a chat that is not on screen (the chat page while the editor shows, a streamed reply in a hidden
        // view) renders when it is shown, once, with the text it has then — not on every streamed token for nobody.
        if (panel.IsLoaded && !panel.IsVisible)
        {
            if (!(bool)panel.GetValue(WaitingProperty))
            {
                panel.SetValue(WaitingProperty, true);
                panel.IsVisibleChanged += RenderWhenShown;
            }
            return;
        }
        Render(panel, e.NewValue as string ?? "");
    }

    private static void RenderWhenShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not StackPanel panel || e.NewValue is not true) return;
        panel.IsVisibleChanged -= RenderWhenShown;
        panel.SetValue(WaitingProperty, false);
        Render(panel, GetMarkdown(panel) ?? "");
    }

    private static void Render(StackPanel panel, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            panel.Children.Clear();
            panel.SetValue(StableCountProperty, 0);
            panel.SetValue(RenderedTextProperty, null);
            return;
        }

        var segments = ParseSegments(text);
        // The stable prefix is only this message's prefix when the new text grows the one rendered: a recycled row
        // that now shows ANOTHER message kept the previous message's first blocks.
        string? rendered = (string?)panel.GetValue(RenderedTextProperty);
        int stableCount = rendered != null && text.StartsWith(rendered, StringComparison.Ordinal)
            ? (int)panel.GetValue(StableCountProperty) : 0;
        panel.SetValue(RenderedTextProperty, text);

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
        // A BrainX cowork-room reply names each member before her lines ("### 💬 codex", BrainXRoomProvider).
        if (level == 3 && SpeakerHeading.Match(text) is { Success: true } who) return CreateSpeakerHeader(text, who.Groups[1].Value);
        var (fontSize, weight) = level switch
        {
            1 => (22.0, FontWeights.Bold),
            2 => (18.0, FontWeights.Bold),
            3 => (16.0, FontWeights.SemiBold),
            4 => (14.0, FontWeights.SemiBold),
            _ => (13.0, FontWeights.Medium),
        };

        string fg = level <= 2 ? HeadingKey : level <= 4 ? HeadingMinorKey : TextKey;

        var tb = Fg(Font(new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            FontWeight = weight,
            Margin = new Thickness(0, level <= 2 ? 10 : 6, 0, 4),
        }, mono: false), fg);
        ApplyInlineFormatting(tb, text);

        if (level <= 2)
        {
            var stack = new StackPanel();
            stack.Children.Add(tb);
            stack.Children.Add(Tok(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 2, 0, 4),
            }, Border.BackgroundProperty, BorderKey));
            return stack;
        }

        return tb;
    }

    private static readonly Regex SpeakerHeading = new(@"^💬\s*(\S+)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// The room member speaking from here on: her round portrait, her name on a chip of her colour and a line of it
    /// (ChatCharacter.ForRoomAgent), so each part of a cowork-room reply shows who wrote it. Where the chat's boxes keep
    /// the theme's own look (views:CharacterLook.Show is not Visible — Classic, or outside the chat) it stays the plain
    /// heading it always was.
    /// </summary>
    private static UIElement CreateSpeakerHeader(string text, string agent)
    {
        var who = Services.ChatCharacter.ForRoomAgent(agent);
        var grid = new Grid { Tag = "speaker:" + agent };

        var plain = Fg(Font(new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4),
        }, mono: false), HeadingMinorKey);
        ApplyInlineFormatting(plain, text);
        plain.Style = ShowWhen(typeof(TextBlock), lookOn: false);
        grid.Children.Add(plain);

        var row = new Grid { Margin = new Thickness(0, 12, 0, 6), Style = ShowWhen(typeof(Grid), lookOn: true) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var face = new Grid { Width = 34, Height = 34, VerticalAlignment = VerticalAlignment.Center };
        face.Children.Add(Tok(new System.Windows.Shapes.Ellipse(), System.Windows.Shapes.Shape.FillProperty, "Bg.Elevated"));
        face.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Stroke = who.AccentBrush, StrokeThickness = 2,
            Fill = (Brush?)who.FaceBrush ?? who.TintBrush,
        });
        Panel.SetZIndex(face, 1);
        row.Children.Add(face);
        var chip = new Border
        {
            Background = who.AccentBrush, CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 3, 12, 4),
            Margin = new Thickness(-9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = Font(new TextBlock { Text = who.Name, Foreground = who.OnAccentBrush, FontSize = 12.5, FontWeight = FontWeights.SemiBold }, mono: false),
        };
        Grid.SetColumn(chip, 1);
        row.Children.Add(chip);
        var line = new Border { Height = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush(who.FrameBrush.Color, Color.FromArgb(0, who.Accent.R, who.Accent.G, who.Accent.B), 0) };
        Grid.SetColumn(line, 2);
        row.Children.Add(line);
        grid.Children.Add(row);
        return grid;
    }

    /// <summary>A style that shows its element only when the chat's character look is on (or only when it is off).</summary>
    private static Style ShowWhen(Type type, bool lookOn)
    {
        var style = new Style(type);
        style.Setters.Add(new Setter(UIElement.VisibilityProperty, lookOn ? Visibility.Collapsed : Visibility.Visible));
        var on = new Trigger { Property = Views.CharacterLook.ShowProperty, Value = Visibility.Visible };
        on.Setters.Add(new Setter(UIElement.VisibilityProperty, lookOn ? Visibility.Visible : Visibility.Collapsed));
        style.Triggers.Add(on);
        return style;
    }

    private static UIElement CreateHorizontalRule() => Tok(new Border
    {
        Height = 1,
        Margin = new Thickness(0, 8, 0, 8),
    }, Border.BackgroundProperty, RuleKey);

    // ─── Blockquote ───

    private static UIElement CreateBlockquote(string text)
    {
        var tb = Fg(Font(new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            FontStyle = FontStyles.Italic,
            LineHeight = 22,
        }, mono: false), MutedKey);
        ApplyInlineFormatting(tb, text);

        var quote = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 6, 8, 6),
            Margin = new Thickness(0, 4, 0, 4),
            Child = tb,
        };
        Tok(quote, Border.BorderBrushProperty, QuoteBorderKey);
        return Tok(quote, Border.BackgroundProperty, QuoteBgKey);
    }

    // ─── Unified List (ordered + unordered) ───

    private static UIElement CreateList(List<string> items, Func<int, string> bulletFn)
    {
        var stack = new StackPanel { Margin = new Thickness(8, 4, 0, 4) };
        for (int i = 0; i < items.Count; i++)
        {
            // A Grid, not a horizontal StackPanel: a StackPanel measures its children at infinite width, so a list
            // item never wrapped and ran off the edge of a narrow chat (owner 2026-10-18, after paragraphs were fixed).
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(Fg(Font(new TextBlock
            {
                Text = bulletFn(i),
                FontSize = 13,
                MinWidth = 24,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Top,
            }, mono: true), HeadingKey));
            var tb = Fg(Font(new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
            }, mono: false), TextKey);
            ApplyInlineFormatting(tb, items[i]);
            Grid.SetColumn(tb, 1);
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
                var cell = Tok(new Border
                {
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(8, 4, 8, 4),
                    Background = Brushes.Transparent,
                }, Border.BorderBrushProperty, BorderKey);
                if (isHeader) Tok(cell, Border.BackgroundProperty, CodeBgKey);
                var tb = Fg(Font(new SelectableTextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = isHeader ? 12 : 11.5,
                    FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
                }, mono: !isHeader), isHeader ? CodeKey : TextKey);
                ApplyInlineFormatting(tb, cellText);
                cell.Child = tb;
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        return Tok(new Border
        {
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(4),
            Child = grid,
        }, Border.BorderBrushProperty, BorderKey);
    }

    // ─── Code Block ───

    private static UIElement CreateCodeBlock(string code, string language)
    {
        bool isDiff = language.Equals("diff", StringComparison.OrdinalIgnoreCase)
                      || (code.Contains("\n+") && code.Contains("\n-"));

        var outerBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 6, 0, 6),
        };
        Tok(outerBorder, Border.BackgroundProperty, CodeBlockBgKey);
        Tok(outerBorder, Border.BorderBrushProperty, BorderKey);

        var mainStack = new StackPanel();

        // Header bar
        var header = Tok(new Grid(), Panel.BackgroundProperty, CodeBgKey);
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(Fg(Font(new TextBlock
        {
            Text = string.IsNullOrEmpty(language) ? "code" : language,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 6, 0, 6),
        }, mono: true), MutedKey));

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
            var codeBlock = Fg(Font(new SelectableTextBlock
            {
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(14, 10, 14, 12),
                LineHeight = 20,
            }, mono: true), "Markdown.Syntax.Text");
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
            string? bg = null;
            string fg = "Markdown.Syntax.Text";

            if (line.StartsWith('+') && !line.StartsWith("+++"))
            { bg = "Markdown.Diff.Added.Background"; fg = "Markdown.Diff.Added"; }
            else if (line.StartsWith('-') && !line.StartsWith("---"))
            { bg = "Markdown.Diff.Deleted.Background"; fg = "Markdown.Diff.Deleted"; }
            else if (line.StartsWith("@@"))
            { fg = "Markdown.Diff.Hunk"; }

            var tb = Fg(Font(new SelectableTextBlock
            {
                Text = line,
                FontSize = 12,
                Padding = new Thickness(14, 1, 14, 1),
                TextWrapping = TextWrapping.Wrap,
            }, mono: true), fg);
            if (bg != null) Tok(tb, TextBlock.BackgroundProperty, bg);
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
            FontSize = 10,
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

        Tok(copyBtn, Control.FontFamilyProperty, MainFontKey);
        Fg(copyBtn, MutedKey);
        copyBtn.MouseEnter += (_, _) => Fg(copyBtn, TextKey);
        copyBtn.MouseLeave += (_, _) => Fg(copyBtn, MutedKey);
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
        var tb = Fg(Font(new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 24,
            FontSize = 13,
            Margin = new Thickness(0, 2, 0, 2),
        }, mono: false), TextKey);
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
                tb.Inlines.Add(new Bold(new Italic(TokRun(m.Groups[1].Value, TextKey))));
            }
            else if (m.Groups[2].Success) // **bold**
            {
                tb.Inlines.Add(new Bold(TokRun(m.Groups[2].Value, TextKey)));
            }
            else if (m.Groups[3].Success) // *italic*
            {
                tb.Inlines.Add(new Italic(TokRun(m.Groups[3].Value, EmphasisKey)));
            }
            else if (m.Groups[4].Success) // ~~strikethrough~~
            {
                tb.Inlines.Add(Fg(new Run(m.Groups[4].Value)
                {
                    TextDecorations = TextDecorations.Strikethrough,
                }, MutedKey));
            }
            else if (m.Groups[5].Success && m.Groups[5].Value.Length > LongInlineCode) // a long `path or command`
            {
                // A chip (a Border in an InlineUIContainer) is one unbreakable box: a long path in one ran off a narrow
                // chat. A Run breaks with the line; it keeps the code font and tint, without the rounded corners.
                var run = Fg(new Run(m.Groups[5].Value) { FontSize = 12 }, CodeKey);
                Tok(run, TextElement.FontFamilyProperty, MonoFontKey);
                Tok(run, TextElement.BackgroundProperty, CodeBgKey);
                tb.Inlines.Add(run);
            }
            else if (m.Groups[5].Success) // `inline code`
            {
                var codeBorder = Tok(new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(1, 0, 1, 0),
                    Child = Fg(Font(new TextBlock
                    {
                        Text = m.Groups[5].Value,
                        FontSize = 12,
                    }, mono: true), CodeKey),
                }, Border.BackgroundProperty, CodeBgKey);
                tb.Inlines.Add(new InlineUIContainer(codeBorder) { BaselineAlignment = BaselineAlignment.Center });
            }
            else if (m.Groups[6].Success) // [text](url)
            {
                tb.Inlines.Add(CreateLink(m.Groups[6].Value, m.Groups[7].Value));
            }
            else if (m.Groups[8].Success) // ![alt](path)
            {
                tb.Inlines.Add(CreatePicture(m.Groups[8].Value, m.Groups[9].Value));
            }
            else if (m.Groups[10].Success) // https://…
            {
                tb.Inlines.Add(CreateLink(m.Groups[10].Value, m.Groups[10].Value));
            }

            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
            AddTextWithLineBreaks(tb, text[pos..]);
    }

    /// <summary>
    /// A link. Its click goes through ChatLinks (owner 2026-10-29: links in the chat did nothing): the agents' local
    /// paths (`/D:/x/a.png`, `&lt;…&gt;`, `app.js:12`) are read into real files, and a click that cannot open says why.
    /// </summary>
    private static Hyperlink CreateLink(string text, string url)
    {
        var target = new ChatLinkTarget(url);
        var hyperlink = Fg(new Hyperlink(new Run(text))
        {
            TextDecorations = null,
            Cursor = Cursors.Hand,
            Tag = target,
            ToolTip = Shown(url),
        }, LinkKey);
        // What it points at is read off the disk when its tip is about to show, not while the reply renders: a link to
        // a network drive that dropped must not stall the chat.
        hyperlink.ToolTipOpening += (_, _) => hyperlink.ToolTip = LinkTip(url);
        hyperlink.MouseEnter += (_, _) => hyperlink.TextDecorations = TextDecorations.Underline;
        hyperlink.MouseLeave += (_, _) => hyperlink.TextDecorations = null;
        hyperlink.Click += (_, _) => target.Open(OwnerOf(hyperlink));
        return hyperlink;
    }

    private static string Shown(string url) => url.Trim().Trim('<', '>');

    private static string LinkTip(string url)
    {
        var link = ChatLinks.Resolve(url, ChatLinks.ProjectFolder?.Invoke());
        string key = link.Kind switch
        {
            ChatLinkKind.RevealFile => "chat.link.revealTip",
            ChatLinkKind.Missing => "chat.link.missing",
            ChatLinkKind.Refused => "chat.link.refused",
            _ => "",
        };
        return key.Length == 0 ? link.Target : string.Format(Services.LocalizedResources.Instance[key], link.Target);
    }

    private static FrameworkElement? OwnerOf(FrameworkContentElement element)
    {
        DependencyObject? d = element;
        while (d is FrameworkContentElement fce) d = fce.Parent;
        return d as FrameworkElement;
    }

    /// <summary>Pictures larger than this are not drawn in the chat (a click still opens them).</summary>
    private const long MaxPictureBytes = 40L * 1024 * 1024;
    private const int PictureDecodeWidth = 720;

    /// <summary>
    /// `![alt](path)`: a picture on this computer is drawn in the reply (the mockups and generated images the agents send),
    /// and a click opens it full size. Until it is read — off the UI thread — and if it cannot be (missing, not a picture,
    /// too big) the frame shows its alt text, and a click says why. A web picture is only a link: an address in a reply
    /// must not make the app call out.
    /// </summary>
    private static Inline CreatePicture(string alt, string url)
    {
        string shown = Shown(url);
        string label = "🖼 " + (alt.Trim().Length > 0 ? alt.Trim() : Path.GetFileName(shown.Replace('/', '\\')));
        if (shown.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || shown.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return CreateLink(label, url);

        var target = new ChatLinkTarget(url);
        var placeholder = Fg(Font(new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(6, 2, 6, 2) }, mono: false), MutedKey);
        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2),
            Margin = new Thickness(0, 4, 0, 4),
            Cursor = Cursors.Hand,
            Tag = target,
            ToolTip = shown,
            Child = placeholder,
        };
        Tok(frame, Border.BorderBrushProperty, BorderKey);
        System.Windows.Automation.AutomationProperties.SetName(frame, label);
        frame.ToolTipOpening += (_, _) => frame.ToolTip = LinkTip(url);
        frame.MouseLeftButtonUp += (_, _) => target.Open(frame);
        ShowPicture(frame, placeholder, url, ChatLinks.ProjectFolder?.Invoke());
        return new InlineUIContainer(frame) { BaselineAlignment = BaselineAlignment.Bottom };
    }

    /// <summary>Decoded pictures by file + size + time written (a picture the agent writes again is read again).</summary>
    private static readonly Dictionary<string, Task<BitmapSource?>> Pictures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The picture each link showed last: a streamed reply re-renders per token, and must not blink to its alt text.</summary>
    private static readonly Dictionary<string, BitmapSource> LastPictures = new(StringComparer.OrdinalIgnoreCase);

    private static void ShowPicture(Border frame, UIElement placeholder, string url, string? folder)
    {
        string linkKey = folder + "|" + url;
        BitmapSource? before;
        lock (Pictures) LastPictures.TryGetValue(linkKey, out before);
        if (before != null) frame.Child = PictureOf(before);
        Task.Run(() => LoadPictureAsync(url, folder)).ContinueWith(t =>
        {
            var picture = t.Result;   // never throws: an unreadable picture is null
            if (ReferenceEquals(picture, before)) return;
            lock (Pictures)
            {
                if (picture == null) LastPictures.Remove(linkKey);
                else
                {
                    if (LastPictures.Count >= 64) LastPictures.Clear();
                    LastPictures[linkKey] = picture;
                }
            }
            frame.Dispatcher.BeginInvoke(() => frame.Child = picture == null ? placeholder : PictureOf(picture));
        }, TaskScheduler.Default);
    }

    private static Image PictureOf(BitmapSource picture) => new()
    {
        Source = picture,
        Stretch = Stretch.Uniform,
        StretchDirection = StretchDirection.DownOnly,
        MaxWidth = 360,
        MaxHeight = 260,
    };

    private static async Task<BitmapSource?> LoadPictureAsync(string url, string? folder)
    {
        try
        {
            var link = ChatLinks.Resolve(url, folder);
            if (link.Kind != ChatLinkKind.ViewFile || !ChatLinks.IsImagePath(link.Target)) return null;
            var file = new FileInfo(link.Target);
            if (file.Length > MaxPictureBytes) return null;
            string key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            Task<BitmapSource?> decode;
            lock (Pictures)
            {
                if (!Pictures.TryGetValue(key, out decode!))
                {
                    if (Pictures.Count >= 32) Pictures.Clear();   // a long chat of pictures: drop the lot rather than grow
                    decode = Task.Run(() => DecodePicture(file.FullName));
                    Pictures[key] = decode;
                }
            }
            return await decode.ConfigureAwait(false);
        }
        catch (Exception) { return null; }
    }

    private static BitmapSource? DecodePicture(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int width = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).PixelWidth;
            stream.Position = 0;
            var picture = new BitmapImage();
            picture.BeginInit();
            picture.CacheOption = BitmapCacheOption.OnLoad;   // read now: the file is not held open
            picture.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            picture.DecodePixelWidth = Math.Clamp(width, 1, PictureDecodeWidth);
            picture.StreamSource = stream;
            picture.EndInit();
            picture.Freeze();
            return picture;
        }
        catch (Exception) { return null; }
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
                tb.Inlines.Add(TokRun(code[pos..m.Index], "Markdown.Syntax.Text"));

            if (m.Groups[1].Success || m.Groups[2].Success)
                tb.Inlines.Add(Fg(new Run(m.Value) { FontStyle = FontStyles.Italic }, "Markdown.Syntax.Comment"));
            else if (m.Groups[3].Success)
                tb.Inlines.Add(TokRun(m.Value, "Markdown.Syntax.String"));
            else if (m.Groups[4].Success)
                tb.Inlines.Add(TokRun(m.Value, "Markdown.Syntax.Number"));
            else if (m.Groups[5].Success)
            {
                string word = m.Value;
                if (keywords.Contains(word))
                    tb.Inlines.Add(Fg(new Run(word) { FontWeight = FontWeights.SemiBold }, "Markdown.Syntax.Keyword"));
                else if (word.Length > 0 && char.IsUpper(word[0]) && word.Any(char.IsLower))
                    tb.Inlines.Add(TokRun(word, "Markdown.Syntax.Type"));
                else
                    tb.Inlines.Add(TokRun(word, "Markdown.Syntax.Text"));
            }
            else
                tb.Inlines.Add(TokRun(m.Value, "Markdown.Syntax.Text"));

            pos = m.Index + m.Length;
        }

        if (pos < code.Length)
            tb.Inlines.Add(TokRun(code[pos..], "Markdown.Syntax.Text"));
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
