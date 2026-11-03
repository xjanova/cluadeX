using System.Runtime.InteropServices;
using System.Windows;

namespace CluadeX.Views.PopOut;

/// <summary>
/// Puts a remembered window back where the user can reach it. A page's own window may have been on a monitor that is
/// gone now (a laptop off its dock, a projector unplugged), or a damaged setting may hold anything: a window whose title
/// bar is on no screen could not even be dragged back. Pure geometry (<see cref="Fit"/>) plus the monitors' work areas
/// read from Windows (<see cref="WorkAreas"/>).
/// </summary>
public static class ScreenFit
{
    /// <summary>How much of the title bar must be on one screen to leave the window where it was (WPF units).</summary>
    public const double GrabWidth = 120, GrabHeight = 24;
    /// <summary>The smallest window this ever makes (the window's own MinWidth/MinHeight still apply).</summary>
    public const double MinWidth = 420, MinHeight = 300;

    /// <summary>
    /// <paramref name="wanted"/> as is when a grab-able piece of its title bar is on one of <paramref name="workAreas"/>;
    /// otherwise moved onto the screen it overlaps most (or <paramref name="fallback"/>, the main window's screen, when it
    /// overlaps none), no bigger than that screen and inside it.
    /// </summary>
    public static Rect Fit(Rect wanted, IReadOnlyList<Rect> workAreas, Rect fallback)
    {
        if (!IsFinite(wanted) || wanted.Width < 1 || wanted.Height < 1)
            wanted = new Rect(fallback.Left, fallback.Top, Math.Min(1100, fallback.Width), Math.Min(760, fallback.Height));
        var title = new Rect(wanted.Left, wanted.Top, wanted.Width, Math.Min(GrabHeight, wanted.Height));
        foreach (var area in workAreas)
        {
            var hit = Rect.Intersect(title, area);
            if (!hit.IsEmpty && hit.Width >= Math.Min(GrabWidth, wanted.Width) && hit.Height >= Math.Min(GrabHeight, wanted.Height) - 0.5)
                return wanted;
        }

        Rect target = fallback;
        double best = 0;
        foreach (var area in workAreas)
        {
            var hit = Rect.Intersect(wanted, area);
            double overlap = hit.IsEmpty ? 0 : hit.Width * hit.Height;
            if (overlap > best) { best = overlap; target = area; }
        }
        if (target.IsEmpty || target.Width < 1 || target.Height < 1) target = new Rect(0, 0, 1280, 800);

        double w = Math.Min(Math.Max(wanted.Width, Math.Min(MinWidth, target.Width)), target.Width);
        double h = Math.Min(Math.Max(wanted.Height, Math.Min(MinHeight, target.Height)), target.Height);
        double left, top;
        if (best > 0)
        {
            left = Math.Clamp(wanted.Left, target.Left, target.Right - w);
            top = Math.Clamp(wanted.Top, target.Top, target.Bottom - h);
        }
        else
        {
            left = target.Left + (target.Width - w) / 2;
            top = target.Top + (target.Height - h) / 2;
        }
        return new Rect(left, top, w, h);
    }

    private static bool IsFinite(Rect r) =>
        !r.IsEmpty && double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height);

    // ─── the monitors, from Windows ───

    /// <summary>Every monitor's work area (the screen minus the taskbar) in WPF units. The app is system-DPI aware, so
    /// one scale (the main window's) converts every monitor. Empty when Windows cannot be asked.</summary>
    public static List<Rect> WorkAreas(double scaleX, double scaleY)
    {
        var list = new List<Rect>();
        if (scaleX <= 0 || scaleY <= 0) { scaleX = 1; scaleY = 1; }
        try
        {
            MonitorEnumProc proc = (IntPtr monitor, IntPtr hdc, ref RECT rc, IntPtr data) =>
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var w = info.rcWork;
                    list.Add(new Rect(w.Left / scaleX, w.Top / scaleY, (w.Right - w.Left) / scaleX, (w.Bottom - w.Top) / scaleY));
                }
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            GC.KeepAlive(proc);
        }
        catch { /* no monitors to read: the caller falls back to the main window's screen */ }
        return list;
    }

    /// <summary>The work area of the monitor a window is on (nearest one), in WPF units; or the primary work area.</summary>
    public static Rect WorkAreaOf(IntPtr hwnd, double scaleX, double scaleY)
    {
        if (scaleX <= 0 || scaleY <= 0) { scaleX = 1; scaleY = 1; }
        try
        {
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                var w = info.rcWork;
                return new Rect(w.Left / scaleX, w.Top / scaleY, (w.Right - w.Left) / scaleX, (w.Bottom - w.Top) / scaleY);
            }
        }
        catch { }
        return SystemParameters.WorkArea;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
}
