using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CluadeX.Services.Desktop;

/// <summary>A capture: PNG bytes, its size, and where it sits on the virtual screen (physical pixels).</summary>
public sealed record DesktopShot(byte[] Png, int Width, int Height, int ScreenX, int ScreenY, string Method);

/// <summary>
/// Window and region capture in physical pixels. A window is rendered with PrintWindow(PW_RENDERFULLCONTENT) —
/// right even when it is behind other windows, and for DirectX/WPF content — and falls back to a copy of the screen
/// when that comes back black (some GPU-rendered games). Must run on a per-monitor DPI aware thread.
/// </summary>
public static class DesktopCapture
{
    public const int MaxEdge = 8192;

    public static DesktopShot CaptureWindow(IntPtr hwnd)
    {
        if (DesktopNative.IsIconic(hwnd))
            throw new InvalidOperationException("The window is minimized — restore it first (desktop_act focus, or desktop_keys on it).");
        DesktopNative.GetWindowRect(hwnd, out var wr);
        var frame = DesktopNative.Bounds(hwnd);
        if (wr.Width <= 0 || wr.Height <= 0) throw new InvalidOperationException("The window has no size.");
        if (wr.Width > MaxEdge || wr.Height > MaxEdge) throw new InvalidOperationException($"The window is {wr.Width}x{wr.Height} — too large to capture.");

        byte[] px = Grab(wr.Width, wr.Height, dc => DesktopNative.PrintWindow(hwnd, dc, DesktopNative.PW_RENDERFULLCONTENT));
        // Crop the invisible resize border away: the image is what a person sees, and x,y in it map 1:1 to the frame.
        int ox = Math.Clamp(frame.Left - wr.Left, 0, wr.Width - 1), oy = Math.Clamp(frame.Top - wr.Top, 0, wr.Height - 1);
        int w = Math.Clamp(frame.Width, 1, wr.Width - ox), h = Math.Clamp(frame.Height, 1, wr.Height - oy);
        byte[] cropped = Crop(px, wr.Width, ox, oy, w, h);
        if (!IsBlank(cropped)) return new DesktopShot(Png(cropped, w, h), w, h, wr.Left + ox, wr.Top + oy, "window");
        // Black: the app draws straight to the screen (a game's swap chain). Copy what is on screen instead.
        var shot = CaptureRegion(frame.Left, frame.Top, frame.Width, frame.Height);
        return shot with { Method = "screen copy (the window rendered black off-screen; anything covering it is in the image)" };
    }

    /// <summary>A region of the virtual screen (all monitors; coordinates may be negative). Clipped to the screen.</summary>
    public static DesktopShot CaptureRegion(int x, int y, int w, int h)
    {
        int vx = DesktopNative.GetSystemMetrics(DesktopNative.SM_XVIRTUALSCREEN), vy = DesktopNative.GetSystemMetrics(DesktopNative.SM_YVIRTUALSCREEN);
        int vw = DesktopNative.GetSystemMetrics(DesktopNative.SM_CXVIRTUALSCREEN), vh = DesktopNative.GetSystemMetrics(DesktopNative.SM_CYVIRTUALSCREEN);
        int x0 = Math.Max(x, vx), y0 = Math.Max(y, vy);
        int x1 = Math.Min(x + w, vx + vw), y1 = Math.Min(y + h, vy + vh);
        if (x1 <= x0 || y1 <= y0) throw new ArgumentException($"The region {x},{y} {w}x{h} is outside the screen ({vx},{vy} {vw}x{vh}).");
        int cw = x1 - x0, ch = y1 - y0;
        if (cw > MaxEdge || ch > MaxEdge) throw new ArgumentException($"The region is {cw}x{ch} — too large (max {MaxEdge}).");
        IntPtr screen = DesktopNative.GetDC(IntPtr.Zero);
        try
        {
            byte[] px = Grab(cw, ch, dc => DesktopNative.BitBlt(dc, 0, 0, cw, ch, screen, x0, y0, DesktopNative.SRCCOPY | DesktopNative.CAPTUREBLT));
            return new DesktopShot(Png(px, cw, ch), cw, ch, x0, y0, "screen");
        }
        finally { DesktopNative.ReleaseDC(IntPtr.Zero, screen); }
    }

    /// <summary>Draw into a fresh w×h bitmap with <paramref name="draw"/> and return its BGRA pixels, top-down.</summary>
    private static byte[] Grab(int w, int h, Func<IntPtr, bool> draw)
    {
        IntPtr screen = DesktopNative.GetDC(IntPtr.Zero);
        IntPtr mem = DesktopNative.CreateCompatibleDC(screen);
        IntPtr bmp = DesktopNative.CreateCompatibleBitmap(screen, w, h);
        IntPtr old = DesktopNative.SelectObject(mem, bmp);
        try
        {
            if (!draw(mem)) throw new InvalidOperationException("The capture call failed.");
            DesktopNative.SelectObject(mem, old);
            old = IntPtr.Zero;
            var bmi = new DesktopNative.BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DesktopNative.BITMAPINFOHEADER>(),
                biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            var px = new byte[w * h * 4];
            if (DesktopNative.GetDIBits(mem, bmp, 0, (uint)h, px, ref bmi, 0) == 0) throw new InvalidOperationException("Reading the pixels failed.");
            return px;
        }
        finally
        {
            if (old != IntPtr.Zero) DesktopNative.SelectObject(mem, old);
            DesktopNative.DeleteObject(bmp);
            DesktopNative.DeleteDC(mem);
            DesktopNative.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static byte[] Crop(byte[] px, int stride, int x, int y, int w, int h)
    {
        if (x == 0 && y == 0 && w == stride && px.Length == w * h * 4) return px;
        var o = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(px, ((y + row) * stride + x) * 4, o, row * w * 4, w * 4);
        return o;
    }

    /// <summary>Every pixel black (RGB 0) — what PrintWindow returns for a window it cannot render.</summary>
    internal static bool IsBlank(byte[] px)
    {
        for (int i = 0; i < px.Length; i += 4)
            if (px[i] != 0 || px[i + 1] != 0 || px[i + 2] != 0) return false;
        return true;
    }

    /// <summary>BGRA (alpha ignored: GDI leaves it undefined) → PNG.</summary>
    internal static byte[] Png(byte[] bgra, int w, int h)
    {
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, bgra, w * 4);
        src.Freeze();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
