using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Desktop;

/// <summary>One top-level window as the tools report it. Bounds are physical screen pixels.</summary>
public sealed record DesktopWindow(IntPtr Handle, string Title, string ClassName, int ProcessId, string ProcessName,
    int X, int Y, int Width, int Height, bool IsForeground, bool IsMinimized)
{
    public string HandleText => "0x" + Handle.ToInt64().ToString("X");

    public string Format() =>
        $"{HandleText} \"{Title}\" — {ProcessName} (pid {ProcessId}) at {X},{Y} {Width}x{Height}"
        + (IsForeground ? " [foreground]" : "") + (IsMinimized ? " [minimized]" : "");
}

/// <summary>Top-level windows: list them, and find the one a call names (title regex, pid, handle).</summary>
public static class DesktopWindows
{
    /// <summary>The visible, uncloaked top-level windows that have a title — what a person sees on the taskbar and
    /// alt-tab — in Z order (front first). Must run on a per-monitor DPI aware thread for physical bounds.</summary>
    public static List<DesktopWindow> List(bool includeUntitled = false)
    {
        var handles = new List<IntPtr>();
        DesktopNative.EnumWindows((h, _) => { handles.Add(h); return true; }, IntPtr.Zero);
        IntPtr fg = DesktopNative.GetForegroundWindow();
        var names = new Dictionary<int, string>();
        var list = new List<DesktopWindow>();
        foreach (var h in handles)
        {
            if (!DesktopNative.IsWindowVisible(h) || DesktopNative.IsCloaked(h)) continue;
            string title = DesktopNative.WindowText(h);
            if (title.Length == 0 && !includeUntitled) continue;
            int ex = DesktopNative.GetWindowLongW(h, DesktopNative.GWL_EXSTYLE);
            if ((ex & DesktopNative.WS_EX_TOOLWINDOW) != 0 && title.Length == 0) continue;
            var w = Describe(h, fg, names);
            if (w == null || (w.Width <= 1 && w.Height <= 1)) continue;
            list.Add(w);
        }
        return list;
    }

    public static DesktopWindow? Describe(IntPtr h, IntPtr? foreground = null, Dictionary<int, string>? cache = null)
    {
        if (h == IntPtr.Zero || !DesktopNative.IsWindow(h)) return null;
        DesktopNative.GetWindowThreadProcessId(h, out uint pid);
        string pname;
        if (cache == null || !cache.TryGetValue((int)pid, out pname!))
        {
            pname = ProcessNameOf((int)pid);
            cache?.TryAdd((int)pid, pname);
        }
        var r = DesktopNative.Bounds(h);
        IntPtr fg = foreground ?? DesktopNative.GetForegroundWindow();
        return new DesktopWindow(h, DesktopNative.WindowText(h), DesktopNative.ClassName(h), (int)pid, pname,
            r.Left, r.Top, r.Width, r.Height, h == fg, DesktopNative.IsIconic(h));
    }

    public static string ProcessNameOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return "pid" + pid; }
    }

    /// <summary>Parse "0x1A2B" / "1234567" as a window handle.</summary>
    public static bool TryParseHandle(string? s, out IntPtr h)
    {
        h = IntPtr.Zero;
        s = (s ?? "").Trim();
        if (s.Length == 0) return false;
        long v;
        bool ok = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out v)
            : long.TryParse(s, out v);
        if (!ok || v == 0) return false;
        h = new IntPtr(v);
        return true;
    }

    /// <summary>
    /// The window a call names: handle, else pid (its frontmost titled window), else a title regex (front-most
    /// match; plain text works as a case-insensitive substring), else — when <paramref name="foregroundDefault"/> —
    /// the foreground window. Null + <paramref name="error"/> when nothing matches.
    /// </summary>
    public static DesktopWindow? Resolve(string? handle, string? pid, string? title, bool foregroundDefault, out string? error)
    {
        error = null;
        if (TryParseHandle(handle, out var h))
        {
            var w = Describe(h);
            if (w == null) error = $"No window with handle {handle} (it may have closed). desktop_windows lists the open ones.";
            return w;
        }
        var all = List();
        if (int.TryParse((pid ?? "").Trim(), out int p) && p > 0)
        {
            var w = all.FirstOrDefault(x => x.ProcessId == p);
            if (w == null) error = $"Process {p} has no visible window. desktop_windows lists the open ones.";
            return w;
        }
        if (!string.IsNullOrWhiteSpace(title))
        {
            Regex? rx = null;
            try { rx = new Regex(title.Trim(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
            catch (ArgumentException) { }
            var w = all.FirstOrDefault(x => rx != null ? SafeMatch(rx, x.Title) : x.Title.Contains(title.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? all.FirstOrDefault(x => x.Title.Contains(title.Trim(), StringComparison.OrdinalIgnoreCase));
            if (w == null) error = $"No window title matches \"{title}\". Open windows: " + string.Join(" · ", all.Take(12).Select(x => $"\"{Trunc(x.Title, 50)}\""));
            return w;
        }
        if (foregroundDefault)
        {
            var w = Describe(DesktopNative.GetForegroundWindow());
            if (w == null) error = "No foreground window.";
            return w;
        }
        error = "Name the window: title (a regex or part of the title), pid, or handle (from desktop_windows).";
        return null;
    }

    internal static bool SafeMatch(Regex rx, string s)
    {
        try { return rx.IsMatch(s); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    internal static string Trunc(string? s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";

    /// <summary>Bring a window to the front so SendInput reaches it. Windows only lets the foreground process hand
    /// the foreground over, so the input threads are attached for the call. True when it is in front afterwards.</summary>
    public static bool Activate(IntPtr h)
    {
        if (DesktopNative.GetForegroundWindow() == h) return true;
        if (DesktopNative.IsIconic(h)) DesktopNative.ShowWindow(h, DesktopNative.SW_RESTORE);
        IntPtr fg = DesktopNative.GetForegroundWindow();
        uint fgThread = fg != IntPtr.Zero ? DesktopNative.GetWindowThreadProcessId(fg, out _) : 0;
        uint me = DesktopNative.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && DesktopNative.AttachThreadInput(me, fgThread, true);
        try
        {
            DesktopNative.BringWindowToTop(h);
            DesktopNative.SetForegroundWindow(h);
        }
        finally { if (attached) DesktopNative.AttachThreadInput(me, fgThread, false); }
        for (int i = 0; i < 10 && DesktopNative.GetForegroundWindow() != h; i++) Thread.Sleep(30);
        return DesktopNative.GetForegroundWindow() == h;
    }
}
