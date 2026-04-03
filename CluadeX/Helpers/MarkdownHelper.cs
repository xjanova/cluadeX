using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CluadeX.Helpers;

/// <summary>
/// Attached property that renders basic Markdown (code blocks, bold, inline code)
/// into a StackPanel's children as rich WPF elements.
/// </summary>
public static class MarkdownHelper
{
    // ─── Catppuccin Mocha Colors ───
    private static readonly Color CrustColor = Color.FromRgb(0x11, 0x11, 0x1B);
    private static readonly Color Surface0Color = Color.FromRgb(0x31, 0x32, 0x44);
    private static readonly Color Surface1Color = Color.FromRgb(0x45, 0x47, 0x5A);
    private static readonly Color Overlay0Color = Color.FromRgb(0x6C, 0x70, 0x86);
    private static readonly Color TextColor = Color.FromRgb(0xCD, 0xD6, 0xF4);
    private static readonly Color TealColor = Color.FromRgb(0x94, 0xE2, 0xD5);
    private static readonly Color GreenColor = Color.FromRgb(0xA6, 0xE3, 0xA1);
    private static readonly Color BlueColor = Color.FromRgb(0x89, 0xB4, 0xFA);
    private static readonly Color PeachColor = Color.FromRgb(0xFA, 0xB3, 0x87);
    private static readonly Color MauveColor = Color.FromRgb(0xCB, 0xA6, 0xF7);
    private static readonly Color RedColor = Color.FromRgb(0xF3, 0x8B, 0xA8);
    private static readonly Color YellowColor = Color.FromRgb(0xF9, 0xE2, 0xAF);

    private static readonly FontFamily MonoFont = new("Cascadia Code, Consolas, Courier New");
    private static readonly FontFamily MainFont = new("Segoe UI, Arial");

    // Code block regex: ```lang\n...\n```
    private static readonly Regex CodeBlockRegex = new(
        @"```(\w*)\s*\r?\n([\s\S]*?)```",
        RegexOptions.Compiled);

    // Inline patterns: **bold**, `code`
    private static readonly Regex InlineRegex = new(
        @"\*\*(.+?)\*\*|`([^`\n]+?)`",
        RegexOptions.Compiled);

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
        panel.Children.Clear();

        string text = e.NewValue as string ?? "";
        if (string.IsNullOrEmpty(text)) return;

        var segments = ParseSegments(text);
        foreach (var seg in segments)
        {
            if (seg.IsCodeBlock)
                panel.Children.Add(CreateCodeBlock(seg.Code, seg.Language));
            else
                panel.Children.Add(CreateFormattedTextBlock(seg.Text));
        }
    }

    // ─── Parsing ───

    private static List<MarkdownSegment> ParseSegments(string text)
    {
        var segments = new List<MarkdownSegment>();
        int pos = 0;

        foreach (Match m in CodeBlockRegex.Matches(text))
        {
            // Text before this code block
            if (m.Index > pos)
            {
                string before = text[pos..m.Index].Trim();
                if (!string.IsNullOrEmpty(before))
                    segments.Add(MarkdownSegment.CreateText(before));
            }

            segments.Add(MarkdownSegment.CreateCode(m.Groups[2].Value.TrimEnd(), m.Groups[1].Value));
            pos = m.Index + m.Length;
        }

        // Remaining text after last code block
        if (pos < text.Length)
        {
            string after = text[pos..].Trim();
            if (!string.IsNullOrEmpty(after))
                segments.Add(MarkdownSegment.CreateText(after));
        }

        return segments;
    }

    // ─── Code Block Rendering ───

    private static UIElement CreateCodeBlock(string code, string language)
    {
        var outerBorder = new Border
        {
            Background = new SolidColorBrush(CrustColor),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Surface0Color),
            Margin = new Thickness(0, 6, 0, 6),
            Padding = new Thickness(0),
        };

        var mainStack = new StackPanel();

        // ── Header bar: language label + Copy button ──
        var header = new Grid
        {
            Background = new SolidColorBrush(Surface0Color),
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Language label
        var langText = new TextBlock
        {
            Text = string.IsNullOrEmpty(language) ? "code" : language,
            FontSize = 11,
            FontFamily = MonoFont,
            Foreground = new SolidColorBrush(Overlay0Color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 6, 0, 6),
        };
        header.Children.Add(langText);

        // Copy button
        var copyBtn = new Button
        {
            Content = "\U0001F4CB Copy",
            FontSize = 10,
            FontFamily = MainFont,
            Foreground = new SolidColorBrush(Overlay0Color),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = new Thickness(8, 4, 12, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Capture code for the closure
        string codeCopy = code;
        copyBtn.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(codeCopy);
                copyBtn.Content = "\u2713 Copied!";
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2)
                };
                timer.Tick += (_, _) => { copyBtn.Content = "\U0001F4CB Copy"; timer.Stop(); };
                timer.Start();
            }
            catch { }
        };
        // Hover effect
        copyBtn.MouseEnter += (_, _) => copyBtn.Foreground = new SolidColorBrush(TextColor);
        copyBtn.MouseLeave += (_, _) => copyBtn.Foreground = new SolidColorBrush(Overlay0Color);
        Grid.SetColumn(copyBtn, 1);
        header.Children.Add(copyBtn);

        mainStack.Children.Add(header);

        // ── Code content with syntax highlighting ──
        var codeBlock = new TextBlock
        {
            FontFamily = MonoFont,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(TextColor),
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(14, 10, 14, 12),
            LineHeight = 20,
        };
        ApplySyntaxHighlighting(codeBlock, code, language);
        mainStack.Children.Add(codeBlock);

        outerBorder.Child = mainStack;
        return outerBorder;
    }

    // ─── Formatted Text Rendering (bold, inline code) ───

    private static TextBlock CreateFormattedTextBlock(string text)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 24,
            FontFamily = MainFont,
            FontSize = 13,
            Foreground = new SolidColorBrush(TextColor),
            Margin = new Thickness(0, 2, 0, 2),
        };

        int pos = 0;
        foreach (Match m in InlineRegex.Matches(text))
        {
            // Plain text before this match
            if (m.Index > pos)
            {
                string before = text[pos..m.Index];
                // Handle line breaks
                AddTextWithLineBreaks(tb, before);
            }

            if (m.Groups[1].Success)
            {
                // **bold**
                var boldRun = new Bold(new Run(m.Groups[1].Value)
                {
                    Foreground = new SolidColorBrush(TextColor),
                });
                tb.Inlines.Add(boldRun);
            }
            else if (m.Groups[2].Success)
            {
                // `inline code`
                var codeBorder = new Border
                {
                    Background = new SolidColorBrush(Surface0Color),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(1, 0, 1, 0),
                    Child = new TextBlock
                    {
                        Text = m.Groups[2].Value,
                        FontFamily = MonoFont,
                        FontSize = 12,
                        Foreground = new SolidColorBrush(TealColor),
                    }
                };
                tb.Inlines.Add(new InlineUIContainer(codeBorder)
                {
                    BaselineAlignment = BaselineAlignment.Center,
                });
            }

            pos = m.Index + m.Length;
        }

        // Remaining text
        if (pos < text.Length)
        {
            AddTextWithLineBreaks(tb, text[pos..]);
        }

        return tb;
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

    // Language keyword sets
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

    // Syntax highlight regex — matches tokens in order of priority
    private static readonly Regex SyntaxRegex = new(
        @"(//[^\n]*|#[^\n]*)|" +               // Line comments (group 1)
        @"(/\*[\s\S]*?\*/)|" +                  // Block comments (group 2)
        @"(""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*')|" + // Strings (group 3)
        @"(\b\d+\.?\d*\b)|" +                   // Numbers (group 4)
        @"(@?\b[A-Za-z_]\w*\b)",                // Identifiers/keywords (group 5)
        RegexOptions.Compiled);

    private static void ApplySyntaxHighlighting(TextBlock tb, string code, string language)
    {
        var keywords = GetKeywordsForLanguage(language);
        var commentBrush = new SolidColorBrush(Overlay0Color);
        var stringBrush = new SolidColorBrush(GreenColor);
        var numberBrush = new SolidColorBrush(PeachColor);
        var keywordBrush = new SolidColorBrush(MauveColor);
        var typeBrush = new SolidColorBrush(YellowColor);
        var defaultBrush = new SolidColorBrush(TextColor);

        int pos = 0;
        foreach (Match m in SyntaxRegex.Matches(code))
        {
            // Plain text before this match
            if (m.Index > pos)
                tb.Inlines.Add(new Run(code[pos..m.Index]) { Foreground = defaultBrush });

            if (m.Groups[1].Success || m.Groups[2].Success)
            {
                // Comment
                tb.Inlines.Add(new Run(m.Value) { Foreground = commentBrush, FontStyle = FontStyles.Italic });
            }
            else if (m.Groups[3].Success)
            {
                // String literal
                tb.Inlines.Add(new Run(m.Value) { Foreground = stringBrush });
            }
            else if (m.Groups[4].Success)
            {
                // Number
                tb.Inlines.Add(new Run(m.Value) { Foreground = numberBrush });
            }
            else if (m.Groups[5].Success)
            {
                string word = m.Value;
                if (keywords.Contains(word))
                    tb.Inlines.Add(new Run(word) { Foreground = keywordBrush, FontWeight = FontWeights.SemiBold });
                else if (word.Length > 0 && char.IsUpper(word[0]) && word.Any(char.IsLower))
                    tb.Inlines.Add(new Run(word) { Foreground = typeBrush }); // PascalCase = type
                else
                    tb.Inlines.Add(new Run(word) { Foreground = defaultBrush });
            }
            else
            {
                tb.Inlines.Add(new Run(m.Value) { Foreground = defaultBrush });
            }

            pos = m.Index + m.Length;
        }

        // Remaining text
        if (pos < code.Length)
            tb.Inlines.Add(new Run(code[pos..]) { Foreground = defaultBrush });
    }

    private static HashSet<string> GetKeywordsForLanguage(string lang)
    {
        return lang.ToLowerInvariant() switch
        {
            "csharp" or "cs" or "c#" => CSharpKeywords,
            "python" or "py" => PythonKeywords,
            "javascript" or "js" or "jsx" or "typescript" or "ts" or "tsx" => JsKeywords,
            "java" or "kotlin" or "kt" => CSharpKeywords, // close enough
            "go" or "rust" or "rs" or "cpp" or "c" => GenericKeywords,
            _ => GenericKeywords,
        };
    }

    // ─── Segment Model ───

    private class MarkdownSegment
    {
        public string Text { get; init; } = "";
        public string Code { get; init; } = "";
        public string Language { get; init; } = "";
        public bool IsCodeBlock { get; init; }

        public static MarkdownSegment CreateText(string text) => new() { Text = text };
        public static MarkdownSegment CreateCode(string code, string lang) =>
            new() { Code = code, Language = lang, IsCodeBlock = true };
    }
}
