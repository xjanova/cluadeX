using System.Runtime.InteropServices;

namespace CluadeX.Services;

/// <summary>
/// How long since the last mouse or keyboard input anywhere on this computer (Windows' GetLastInputInfo). One call is a
/// cheap read of a counter the system keeps anyway — nothing is hooked, nothing polls on its own. น้อง CluadeX's clock
/// asks it on its 5 s tick (she sleeps only when nobody has touched the PC for <see cref="BuddyMood.SleepAfter"/>) and
/// her away calls ask it every 30 s (Views/CluadexBuddy.Away.cs).
/// </summary>
public static class UserInputClock
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>Time since the last input; zero when Windows cannot say (so a failed read never puts her to sleep).</summary>
    public static TimeSpan Idle()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        uint ms = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(ms);
    }
}
