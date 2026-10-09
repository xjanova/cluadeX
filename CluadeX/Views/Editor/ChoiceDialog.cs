using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CluadeX.Views.Editor;

/// <summary>
/// A modal question with NAMED buttons ("Save" / "Don't save" / "Cancel", "Overwrite" / "Reload" /
/// "Compare"). MessageBox only offers Yes/No/Cancel, which forces a sentence like "Yes = overwrite,
/// No = reload" — and a destructive choice hiding behind "No" is how people lose work.
///
/// Built in code (no XAML) so it can be raised from a view model the same way MessageBox is.
/// Escape and the window's close box always mean the LAST button, which callers make the safe one
/// (Cancel).
/// </summary>
public static class ChoiceDialog
{
    /// <summary>Test seam: when set, called instead of showing a window (harnesses have no UI).
    /// Receives (title, message, button labels) and returns the chosen index.</summary>
    public static Func<string, string, IReadOnlyList<string>, int>? Override { get; set; }

    /// <returns>The index of the clicked button; the last index on Escape / close.</returns>
    public static int Show(string title, string message, IReadOnlyList<string> buttons, int defaultIndex = 0)
    {
        if (buttons.Count == 0) throw new ArgumentException("at least one button", nameof(buttons));
        if (Override != null) return Override(title, message, buttons);

        int cancelIndex = buttons.Count - 1;
        int result = cancelIndex;

        var app = Application.Current;
        if (app == null) return cancelIndex;
        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => Show(title, message, buttons, defaultIndex));

        Brush Res(string key, Color fallback) =>
            app.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            MinWidth = 380,
            MaxWidth = 640,
            Background = Res("NeonBg0Brush", Color.FromRgb(0x0B, 0x0D, 0x22)),
        };
        var owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
        if (owner != null && owner != window && owner.IsLoaded) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };
        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = Res("NeonInk0Brush", Color.FromRgb(0xEE, 0xF0, 0xFF)),
            Margin = new Thickness(0, 0, 0, 18),
            MaxWidth = 590,
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Count; i++)
        {
            int index = i;
            var b = new Button
            {
                Content = buttons[i],
                MinWidth = 96,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = i == defaultIndex,
                IsCancel = i == cancelIndex,
            };
            b.Click += (_, _) => { result = index; window.Close(); };
            row.Children.Add(b);
        }
        root.Children.Add(row);
        window.Content = root;
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { result = cancelIndex; window.Close(); e.Handled = true; }
        };

        window.ShowDialog();
        return result;
    }
}
