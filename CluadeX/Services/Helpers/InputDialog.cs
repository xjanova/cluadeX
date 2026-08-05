using System.Windows;
using System.Windows.Controls;
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
    private static Brush Res(string key, Brush fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? fallback;

    public static string? Show(string title, string prompt, string? placeholder = null, string? initialText = null)
    {
        var bg = Res("BaseBrush", new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x2e)));
        var panel = Res("MantleBrush", new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25)));
        var stroke = Res("Surface0Brush", new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)));
        var text = Res("TextBrush", new SolidColorBrush(Color.FromRgb(0xcd, 0xd6, 0xf4)));
        var subtle = Res("Overlay0Brush", new SolidColorBrush(Color.FromRgb(0x6c, 0x70, 0x86)));
        var accent = Res("MauveBrush", new SolidColorBrush(Color.FromRgb(0xcb, 0xa6, 0xf7)));

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
            Background = bg,
            BorderBrush = stroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(22),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, BlurRadius = 24, ShadowDepth = 0, Opacity = 0.55,
            },
        };

        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = title, Foreground = text, FontSize = 16, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        stack.Children.Add(new TextBlock
        {
            Text = prompt, Foreground = subtle, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        var box = new TextBox
        {
            Text = initialText ?? "",
            Background = panel,
            Foreground = text,
            CaretBrush = accent,
            BorderBrush = stroke,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            FontSize = 13,
            FontFamily = new FontFamily("Consolas, Cascadia Code, Segoe UI"),
        };

        // Placeholder overlay (shown only while empty).
        var placeholderText = new TextBlock
        {
            Text = placeholder ?? "", Foreground = subtle, FontSize = 13, IsHitTestVisible = false,
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed,
        };
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
                Foreground = primary ? Res("BaseBrush", Brushes.Black) : text,
                Background = primary ? accent : panel,
                BorderBrush = stroke,
                BorderThickness = new Thickness(primary ? 0 : 1),
                Cursor = Cursors.Hand,
                FontSize = 13,
                FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            };
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
