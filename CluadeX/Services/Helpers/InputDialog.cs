using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CluadeX.Services.Helpers;

/// <summary>
/// Lightweight, code-built modal text-input dialog themed to match the app.
/// Used for the "Connect GitHub repo" flow (paste a URL) without needing a
/// dedicated XAML window. Returns the entered text, or null if cancelled.
/// </summary>
public static class InputDialog
{
    // Theme tokens (Tokens.md): every colour is a resource reference, so the dialog is drawn in the theme on screen
    // and follows a switch while it is open. Midnight values = the Catppuccin colours this dialog always had.
    private const string Bg = "Dialog.Simple.Background";       // #1E1E2E
    private const string Field = "Dialog.Simple.Field";         // #181825 (text box, secondary button)
    private const string Stroke = "Dialog.Simple.Border";       // #313244
    private const string Text = "Text.Soft";                    // #CDD6F4
    private const string Subtle = "Text.Soft.Muted";            // #6C7086
    private const string Accent = "Accent.2.Soft";              // #CBA6F7 (caret, primary button)
    private const string OnAccent = "Text.Inverse";             // #1E1E2E (primary button text)

    private static T Tok<T>(T e, DependencyProperty p, string token) where T : FrameworkElement
    {
        e.SetResourceReference(p, token);
        return e;
    }

    public static string? Show(string title, string prompt, string? placeholder = null, string? initialText = null)
    {

        var win = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Owner = Application.Current?.MainWindow,
        };

        var outer = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(22),
        };
        Tok(outer, Border.BackgroundProperty, Bg);
        Tok(outer, Border.BorderBrushProperty, Stroke);
        Tok(outer, UIElement.EffectProperty, "Shadow.Dialog.Simple");

        var stack = new StackPanel();

        stack.Children.Add(Tok(new TextBlock
        {
            Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        }, TextBlock.ForegroundProperty, Text));
        stack.Children.Add(Tok(new TextBlock
        {
            Text = prompt, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        }, TextBlock.ForegroundProperty, Subtle));

        var box = new TextBox
        {
            Text = initialText ?? "",
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            FontSize = 13,
            FontFamily = new FontFamily("Consolas, Cascadia Code, Segoe UI"),
        };
        Tok(box, Control.BackgroundProperty, Field);
        Tok(box, Control.ForegroundProperty, Text);
        Tok(box, TextBoxBase.CaretBrushProperty, Accent);
        Tok(box, Control.BorderBrushProperty, Stroke);

        // Placeholder overlay (shown only while empty).
        var placeholderText = new TextBlock
        {
            Text = placeholder ?? "", FontSize = 13, IsHitTestVisible = false,
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed,
        };
        Tok(placeholderText, TextBlock.ForegroundProperty, Subtle);
        box.TextChanged += (_, _) =>
            placeholderText.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;

        // Build the overlaid grid FIRST, then add it once — never parent `box` to
        // two containers (that throws "already the logical child of another element").
        var boxGrid = new Grid();
        boxGrid.Children.Add(box);
        boxGrid.Children.Add(placeholderText);
        stack.Children.Add(boxGrid);

        string? result = null;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };

        Button MakeButton(string label, bool primary)
        {
            var b = new Button
            {
                Content = label,
                Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(8, 0, 0, 0),
                BorderThickness = new Thickness(primary ? 0 : 1),
                Cursor = Cursors.Hand,
                FontSize = 13,
                FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            };
            Tok(b, Control.ForegroundProperty, primary ? OnAccent : Text);
            Tok(b, Control.BackgroundProperty, primary ? Accent : Field);
            Tok(b, Control.BorderBrushProperty, Stroke);
            return b;
        }

        var cancel = MakeButton("Cancel", false);
        cancel.Click += (_, _) => { result = null; win.Close(); };

        var ok = MakeButton("Connect", true);
        void Confirm()
        {
            var v = box.Text?.Trim();
            if (string.IsNullOrEmpty(v)) { result = null; win.Close(); return; }
            result = v; win.Close();
        }
        ok.Click += (_, _) => Confirm();

        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);

        outer.Child = stack;
        win.Content = outer;

        win.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { result = null; win.Close(); }
            else if (e.Key == Key.Enter) Confirm();
        };
        // Let the user drag the borderless dialog by its body.
        win.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        win.ShowDialog();
        return result;
    }
}
