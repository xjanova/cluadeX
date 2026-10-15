using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CluadeX.Services.Browser;

namespace CluadeX.Views.Preview;

/// <summary>
/// The window the page is shown in. It does not own the page: <see cref="BrowserService"/> does. When this host
/// appears (Loaded / visible) it borrows the page's WebView2 controller (re-parenting it into this child window);
/// when it goes away (Unloaded / hidden / its window destroyed) it gives the controller back to the service's
/// off-screen host FIRST — otherwise the controller would be destroyed together with this window. That is what
/// lets a running game keep its state while the user switches CluadeX pages.
///
/// Lifetime: the <see cref="PreviewPane"/> that creates a host disposes it when the pane leaves the visual tree (a
/// CluadeX page switch rebuilds the workbench view; a closed pop-out window), which destroys the child window. The
/// static <see cref="Live"/> set holds a host only while it is in a loaded tree, so a discarded view is never kept
/// alive through it.
/// </summary>
public sealed class PreviewPaneHost : HwndHost
{
    private static readonly HashSet<PreviewPaneHost> Live = new();
    private IntPtr _hwnd;
    private BrowserService? _browser;

    /// <summary>Is any preview pane on screen with a window right now? (UI thread)</summary>
    public static bool AnyVisible => Live.Any(h => h._hwnd != IntPtr.Zero && h.IsVisible);

    /// <summary>How many hosts are in a loaded tree (harness leak check). UI thread.</summary>
    internal static int LiveCount => Live.Count;

    public PreviewPaneHost()
    {
        Loaded += (_, _) => { if (_hwnd != IntPtr.Zero) Live.Add(this); Borrow(); };
        Unloaded += (_, _) => { GiveBack(); Live.Remove(this); };
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Borrow(); else GiveBack(); };
    }

    /// <summary>The browser this host shows (set by the pane before it is loaded).</summary>
    public BrowserService? Browser
    {
        get => _browser;
        set { _browser = value; if (IsLoaded && IsVisible) Borrow(); }
    }

    /// <summary>Does THIS host show the page right now?</summary>
    public bool HasThePage => _hwnd != IntPtr.Zero && _browser?.ActiveHost == _hwnd;

    /// <summary>The child window exists (built and not destroyed).</summary>
    public bool HasWindow => _hwnd != IntPtr.Zero;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
        _hwnd = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight), hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (IsLoaded) Live.Add(this);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        GiveBack();   // backstop: the controller must leave before its parent window dies
        Live.Remove(this);
        DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    /// <summary>Show the page here (or remember to, when the page does not exist yet). Never throws.</summary>
    public void Borrow()
    {
        if (_browser == null || _hwnd == IntPtr.Zero || !IsVisible) return;
        try
        {
            var (w, h, scale) = DeviceSize();
            _browser.AttachHost(_hwnd, w, h, scale);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[preview] attach failed: {ex.Message}"); }
    }

    private void GiveBack()
    {
        if (_browser == null || _hwnd == IntPtr.Zero) return;
        try { _browser.ParkHost(_hwnd); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[preview] park failed: {ex.Message}"); }
    }

    private (int W, int H, double Scale) DeviceSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return ((int)Math.Round(Math.Max(1, ActualWidth) * dpi.DpiScaleX), (int)Math.Round(Math.Max(1, ActualHeight) * dpi.DpiScaleY), dpi.DpiScaleX);
    }

    /// <summary>Layout passes must never throw: the engine can die a moment before its exit event marks it dead.</summary>
    private void ResizeSafely()
    {
        if (_browser == null || _hwnd == IntPtr.Zero) return;
        try
        {
            var (w, h, scale) = DeviceSize();
            _browser.ResizeHost(_hwnd, w, h, scale);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[preview] resize failed: {ex.Message}"); }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ResizeSafely();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ResizeSafely();
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
