using System.Runtime.InteropServices;
using System.Text;

namespace CluadeX.Services.Pet;

/// <summary>The real screen, as Win32 sees it: monitors, visible windows in Z-order, the cursor, idle time, and
/// whether a full-screen app (a game, a video, a slide show) is in front — the pet stays out of the way then.</summary>
public static class PetDesktop
{
    public static List<PetMonitor> Monitors()
    {
        var list = new List<PetMonitor>();
        EnumDisplayMonitors(0, 0, (h, _, _, _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(h, ref mi)) list.Add(new PetMonitor(mi.rcMonitor.ToPet(), mi.rcWork.ToPet()));
            return true;
        }, 0);
        return list;
    }

    static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow",
    };

    /// <summary>Visible, uncloaked, non-minimized app windows, top of the Z-order first, except <paramref name="skip"/>.</summary>
    public static List<PetWindow> Windows(ICollection<nint> skip)
    {
        var list = new List<PetWindow>();
        var cls = new StringBuilder(64);
        EnumWindows((h, _) =>
        {
            if (skip.Contains(h) || !IsWindowVisible(h) || IsIconic(h)) return true;
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0 || (ex & WS_EX_NOACTIVATE) != 0) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            if (GetWindowTextLength(h) == 0) return true;
            cls.Clear(); GetClassName(h, cls, cls.Capacity);
            if (ShellClasses.Contains(cls.ToString())) return true;
            if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(h, out r)) return true;
            if (r.Right - r.Left < 80 || r.Bottom - r.Top < 40) return true;
            list.Add(new PetWindow(h, r.ToPet()));
            return true;
        }, 0);
        return list;
    }

    public static (int x, int y) Cursor() => GetCursorPos(out POINT p) ? (p.X, p.Y) : (0, 0);

    /// <summary>The physical left mouse button is down right now (wherever the release went).</summary>
    public static bool LeftButtonDown() => (GetAsyncKeyState(GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON) & 0x8000) != 0;

    /// <summary>Seconds since the last keyboard or mouse input anywhere on the PC.</summary>
    public static double IdleSeconds()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref li) ? unchecked((uint)Environment.TickCount - li.dwTime) / 1000.0 : 0;
    }

    /// <summary>A window other than the desktop covers its whole monitor and is in front.</summary>
    public static bool FullScreenAppInFront(IReadOnlyList<PetMonitor> monitors, ICollection<nint> ours)
    {
        nint fg = GetForegroundWindow();
        if (fg == 0 || ours.Contains(fg)) return false;
        var cls = new StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (ShellClasses.Contains(cls.ToString())) return false;
        if (!GetWindowRect(fg, out RECT r)) return false;
        var rect = r.ToPet();
        return monitors.Any(m => rect.Left <= m.Bounds.Left && rect.Top <= m.Bounds.Top && rect.Right >= m.Bounds.Right && rect.Bottom >= m.Bounds.Bottom);
    }

    /// <summary>The pet window never takes focus or shows up in Alt+Tab.</summary>
    public static void MakeOverlay(nint hwnd)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new nint(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    /// <summary>Move without activating or changing the Z-order (other than staying topmost).</summary>
    public static void Move(nint hwnd, int x, int y) =>
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    public static PetRect WindowRect(nint hwnd) => GetWindowRect(hwnd, out RECT r) ? r.ToPet() : default;

    // ─── interop ──────────────────────────────────────────────────────────
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    static readonly nint HWND_TOPMOST = -1;
    const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; public PetRect ToPet() => new(Left, Top, Right, Bottom); }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    delegate bool MonitorEnumProc(nint hMonitor, nint hdc, nint lprcMonitor, nint dwData);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc cb, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hWnd, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(nint hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint hWnd, StringBuilder name, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, SM_SWAPBUTTON = 23;
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO li);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint hWnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint hWnd, int attr, out int value, int size);
}
