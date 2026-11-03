using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.Views.PopOut;

/// <summary>
/// A page of the main window in a window of its own (Node Graph, UI Studio, Code Editor). It holds the page's one view
/// while it is open and gives it back as it closes — in Closing, while this window still exists, so a native child
/// (the Studio's WebView2) is parked by WPF instead of being destroyed with this window's HWND.
///
/// Its own taskbar button, the app icon, a localised title, the app's theme (DynamicResource, and the caption bar
/// follows dark / light on a theme switch), maximise / snap / any monitor. Its bounds are remembered per page and put
/// back on a screen that exists (<see cref="ScreenFit"/>).
/// </summary>
public partial class PopOutWindow : Window
{
    /// <summary>The page's view model.</summary>
    public object Page { get; }

    /// <summary>The page's settings / navigation key ("NodeGraph", "UiStudio", "Code").</summary>
    public string PageKey { get; }

    /// <summary>The page's view while it is in this window (null once it went back).</summary>
    public FrameworkElement? View { get; private set; }

    /// <summary>Raised once as the window closes, with the view already taken out of it (null if it had none).</summary>
    public event Action<PopOutWindow, FrameworkElement?>? Returning;

    private WindowState _beforeMinimize = WindowState.Normal;
    private bool _paused;
    private ThemeService? _themes;

    public PopOutWindow(object page, FrameworkElement view, Window? near)
    {
        InitializeComponent();
        Page = page;
        PageKey = PagePopOut.KeyOf(page) ?? page.GetType().Name;
        SetBinding(TitleProperty, new Binding($"[{PagePopOut.TitleKey(page)}]") { Source = LocalizedResources.Instance, Mode = BindingMode.OneWay });
        Place(near);

        View = view;
        view.Visibility = Visibility.Visible;
        Slot.Child = view;

        // The page's own "put back" button (PopOutButton turns into it here) and the placeholder's buttons close this window.
        CommandBindings.Add(new CommandBinding(PagePopOut.BringBackCommand, (_, e) => { e.Handled = true; Close(); }));
        Closing += OnClosing;
        Closed += OnClosed;
        StateChanged += OnStateChanged;
        SourceInitialized += (_, _) => ApplyCaptionTheme();
        _themes = ThemeService.Current;
        if (_themes != null) _themes.ThemeChanged += OnThemeChanged;
    }

    /// <summary>Bring the window forward: restored if minimised; <paramref name="activate"/> gives it the focus.</summary>
    public void Bring(bool activate)
    {
        if (WindowState == WindowState.Minimized) WindowState = _beforeMinimize;
        if (activate) Activate();
    }

    // ─── where it opens ───

    private void Place(Window? near)
    {
        double sx = 1, sy = 1;
        IntPtr nearHwnd = IntPtr.Zero;
        if (near != null && PresentationSource.FromVisual(near) != null)
        {
            var dpi = VisualTreeHelper.GetDpi(near);
            sx = dpi.DpiScaleX;
            sy = dpi.DpiScaleY;
            nearHwnd = new WindowInteropHelper(near).Handle;
        }
        var areas = ScreenFit.WorkAreas(sx, sy);
        Rect fallback = nearHwnd != IntPtr.Zero ? ScreenFit.WorkAreaOf(nearHwnd, sx, sy) : SystemParameters.WorkArea;

        PopOutPlacement? saved = null;
        try { saved = PagePopOut.LoadPlacement?.Invoke(PageKey); } catch { }
        Rect wanted;
        if (saved is { IsUsable: true }) wanted = new Rect(saved.Left, saved.Top, saved.Width, saved.Height);
        else
        {
            // First time: a little smaller than the main window, over it and slightly offset (it reads as "a new window").
            double w = Width, h = Height;
            Rect over = fallback;
            if (near is { WindowState: WindowState.Normal } && double.IsFinite(near.Left) && near.ActualWidth > 0)
            {
                w = Math.Max(MinWidth, near.ActualWidth * 0.85);
                h = Math.Max(MinHeight, near.ActualHeight * 0.85);
                over = new Rect(near.Left, near.Top, near.ActualWidth, near.ActualHeight);
            }
            wanted = new Rect(over.Left + (over.Width - w) / 2 + 36, over.Top + (over.Height - h) / 2 + 36, w, h);
        }
        var fit = ScreenFit.Fit(wanted, areas, fallback);
        Left = fit.Left;
        Top = fit.Top;
        Width = fit.Width;
        Height = fit.Height;
        if (saved is { IsUsable: true, Maximized: true }) WindowState = WindowState.Maximized;
    }

    private void Remember()
    {
        try
        {
            if (PagePopOut.SavePlacement == null) return;
            Rect b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            bool maximized = WindowState == WindowState.Maximized || (WindowState == WindowState.Minimized && _beforeMinimize == WindowState.Maximized);
            var place = new PopOutPlacement { Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height, Maximized = maximized };
            if (!b.IsEmpty && place.IsUsable) PagePopOut.SavePlacement(PageKey, place);
        }
        catch { /* a window position is not worth an error */ }
    }

    // ─── closing: the page goes back ───

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Remember();
        var view = View;
        View = null;
        Slot.Child = null;   // out of this window while its HWND still exists
        var returning = Returning;
        Returning = null;
        returning?.Invoke(this, view);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closing -= OnClosing;
        Closed -= OnClosed;
        StateChanged -= OnStateChanged;
        if (_themes != null) _themes.ThemeChanged -= OnThemeChanged;
        _themes = null;
        Returning = null;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        bool minimized = WindowState == WindowState.Minimized;
        if (!minimized) _beforeMinimize = WindowState;
        // Minimised: the page's decorative animations stop asking for frames, as in the main window.
        if (minimized != _paused)
        {
            _paused = minimized;
            try { AmbientAnimation.SetMinimized(this, minimized); } catch { }
        }
    }

    // ─── the caption bar follows the theme (Windows 11; Windows 10 ignores the colours) ───

    private void OnThemeChanged(string _) => Dispatcher.BeginInvoke(ApplyCaptionTheme);

    private void ApplyCaptionTheme()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var caption = (TryFindResource("Page.Toolbar.Background") as SolidColorBrush)?.Color
                          ?? (TryFindResource("Bg.Window") as SolidColorBrush)?.Color ?? Colors.Black;
            var text = (TryFindResource("Text.Primary") as SolidColorBrush)?.Color ?? Colors.White;
            int dark = IsDark(caption) ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int cap = ColorRef(Opaque(caption)), txt = ColorRef(text);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref cap, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref txt, sizeof(int));
        }
        catch { /* an older Windows: the standard caption */ }
    }

    /// <summary>A translucent toolbar brush is drawn over the window background: blend it the same way for the caption.</summary>
    private Color Opaque(Color c)
    {
        if (c.A == 255) return c;
        var under = (TryFindResource("Bg.Window") as SolidColorBrush)?.Color ?? Colors.Black;
        double a = c.A / 255.0;
        return Color.FromRgb((byte)(c.R * a + under.R * (1 - a)), (byte)(c.G * a + under.G * (1 - a)), (byte)(c.B * a + under.B * (1 - a)));
    }

    internal static bool IsDark(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B < 128;

    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
