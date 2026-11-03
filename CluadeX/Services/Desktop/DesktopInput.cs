using System.Runtime.InteropServices;

namespace CluadeX.Services.Desktop;

/// <summary>One key chord: modifiers + a key ("ctrl+shift+s", "Enter", "F5").</summary>
public sealed record DesktopChord(ushort[] Modifiers, ushort Key, bool Extended, string Text, bool UsesWin)
{
    /// <summary>Would this chord put text into the focused field: a printable key with no Ctrl/Alt, the same with
    /// ctrl+alt (AltGr: '\' or '@' on many European layouts), or a paste.</summary>
    public bool PutsText =>
        (Key == 0x56 && Modifiers.Contains(DesktopInput.VK_CONTROL))          // ctrl+v
        || (Key == 0x2D && Modifiers.Contains(DesktopInput.VK_SHIFT))         // shift+insert
        || (Modifiers.Contains(DesktopInput.VK_CONTROL) == Modifiers.Contains(DesktopInput.VK_MENU) && !UsesWin && DesktopInput.IsPrintable(Key));
}

/// <summary>Real keyboard and mouse input through SendInput — reaches the foreground window only.</summary>
public static class DesktopInput
{
    public const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B;

    private static readonly Dictionary<string, (ushort Vk, bool Ext)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = (0x0D, false), ["return"] = (0x0D, false), ["tab"] = (0x09, false), ["esc"] = (0x1B, false), ["escape"] = (0x1B, false),
        ["space"] = (0x20, false), ["backspace"] = (0x08, false), ["delete"] = (0x2E, true), ["del"] = (0x2E, true),
        ["insert"] = (0x2D, true), ["home"] = (0x24, true), ["end"] = (0x23, true), ["pageup"] = (0x21, true), ["pagedown"] = (0x22, true),
        ["up"] = (0x26, true), ["down"] = (0x28, true), ["left"] = (0x25, true), ["right"] = (0x27, true),
        ["arrowup"] = (0x26, true), ["arrowdown"] = (0x28, true), ["arrowleft"] = (0x25, true), ["arrowright"] = (0x27, true),
        ["apps"] = (0x5D, true), ["menu"] = (0x5D, true), ["printscreen"] = (0x2C, false), ["pause"] = (0x13, false),
        ["capslock"] = (0x14, false), ["numlock"] = (0x90, true), ["scrolllock"] = (0x91, false),
        ["plus"] = (0xBB, false), ["minus"] = (0xBD, false), ["comma"] = (0xBC, false), ["period"] = (0xBE, false),
    };

    /// <summary>Parse "ctrl+shift+s" / "Enter" / "alt+f4" / "a". Null when a part is unknown. A single punctuation
    /// character is looked up in <paramref name="layout"/> (the target window's; this thread's when zero), so ';' is
    /// the key that types ';' there, with shift or AltGr added when that layout needs them.</summary>
    public static DesktopChord? Parse(string token, IntPtr layout = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Trim().Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (token.Trim().EndsWith("++")) parts = parts.Append("plus").ToArray();
        if (parts.Length == 0) return null;
        var mods = new List<ushort>();
        bool win = false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].Trim().ToLowerInvariant())
            {
                case "ctrl" or "control": mods.Add(VK_CONTROL); break;
                case "shift": mods.Add(VK_SHIFT); break;
                case "alt": mods.Add(VK_MENU); break;
                case "win" or "meta" or "cmd" or "super": mods.Add(VK_LWIN); win = true; break;
                default: return null;
            }
        }
        string k = parts[^1].Trim();
        ushort vk; bool ext = false;
        if (Named.TryGetValue(k, out var n)) { vk = n.Vk; ext = n.Ext; }
        else if (k.Length >= 2 && (k[0] == 'f' || k[0] == 'F') && int.TryParse(k[1..], out int f) && f is >= 1 and <= 24) vk = (ushort)(0x70 + f - 1);
        else if (k.Length == 1 && char.IsAsciiLetterOrDigit(k[0])) vk = char.ToUpperInvariant(k[0]);
        else if (k.Length == 1)
        {
            short scan = layout != IntPtr.Zero ? VkKeyScanExW(k[0], layout) : VkKeyScanW(k[0]);
            if (scan == -1) return null;
            vk = (ushort)(scan & 0xFF);
            if ((scan & 0x100) != 0 && !mods.Contains(VK_SHIFT)) mods.Add(VK_SHIFT);
            // 0x600: the layout types it with AltGr (ctrl+alt).
            if ((scan & 0x600) == 0x600)
            {
                if (!mods.Contains(VK_CONTROL)) mods.Add(VK_CONTROL);
                if (!mods.Contains(VK_MENU)) mods.Add(VK_MENU);
            }
        }
        else if (k.Equals("win", StringComparison.OrdinalIgnoreCase)) { vk = VK_LWIN; win = true; }
        else return null;
        return new DesktopChord(mods.ToArray(), vk, ext, token.Trim(), win);
    }

    internal static bool IsPrintable(ushort vk) =>
        vk == 0x20 || (vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A) || (vk >= 0x60 && vk <= 0x6F) || (vk >= 0xBA && vk <= 0xE2);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScanW(char ch);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScanExW(char ch, IntPtr layout);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[]? list);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyExW(uint code, uint mapType, IntPtr layout);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint vk, uint scan, byte[] keyState, [Out] char[] buffer, int bufferSize, uint flags, IntPtr layout);
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);

    public const ushort VK_CAPITAL = 0x14;

    /// <summary>The keyboard layout of the thread that owns <paramref name="window"/> (the foreground window when it is
    /// zero): what a virtual key types depends on it (shift+7 is '&amp;' in US English, something else in Thai).</summary>
    public static IntPtr KeyboardLayoutOf(IntPtr window)
    {
        if (window == IntPtr.Zero) window = DesktopNative.GetForegroundWindow();
        uint tid = window != IntPtr.Zero ? DesktopNative.GetWindowThreadProcessId(window, out _) : 0;
        return GetKeyboardLayout(tid);
    }

    /// <summary>The keyboard layouts loaded on this PC (the user's input languages).</summary>
    public static IReadOnlyList<IntPtr> LoadedLayouts()
    {
        int n = GetKeyboardLayoutList(0, null);
        if (n <= 0) return Array.Empty<IntPtr>();
        var list = new IntPtr[n];
        n = GetKeyboardLayoutList(n, list);
        return list.Take(Math.Max(0, n)).ToArray();
    }

    /// <summary>Whether Caps Lock is on: a plain letter key then types a capital.</summary>
    public static bool CapsLockOn()
    {
        try { return (GetKeyState(VK_CAPITAL) & 1) != 0; }
        catch { return false; }
    }

    /// <summary>What pressing <paramref name="vk"/> types in <paramref name="layout"/>, with shift / AltGr (ctrl+alt) /
    /// Caps Lock as given: the number of characters (0 = none, or only control characters), or -1 for a dead key (it
    /// combines with the next key, so what it types cannot be known alone). Leaves the keyboard state untouched.</summary>
    public static int CharsOf(ushort vk, bool shift, bool altGr, bool capsLock, IntPtr layout, out string chars)
    {
        chars = "";
        var state = new byte[256];
        if (shift) { state[VK_SHIFT] = 0x80; state[0xA0] = 0x80; }                       // VK_LSHIFT
        if (altGr) { state[VK_CONTROL] = 0x80; state[0xA2] = 0x80; state[VK_MENU] = 0x80; state[0xA4] = 0x80; }   // LCONTROL, LMENU
        if (capsLock) state[VK_CAPITAL] = 0x01;
        uint scan = MapVirtualKeyExW(vk, 0, layout);   // MAPVK_VK_TO_VSC
        var buf = new char[8];
        // Flag 4: do not change the keyboard state (Windows 10 1607+), so a dead key is not left pending.
        int n = ToUnicodeEx(vk, scan, state, buf, buf.Length, 0x4, layout);
        if (n < 0) return -1;
        string s = new string(buf, 0, Math.Min(n, buf.Length));
        if (s.All(char.IsControl)) return 0;
        chars = s;
        return s.Length;
    }

    public static void SendChord(DesktopChord c, int holdMs = 0)
    {
        var down = new List<DesktopNative.INPUT>();
        foreach (var m in c.Modifiers) down.Add(Key(m, false, m == VK_LWIN));
        down.Add(Key(c.Key, false, c.Extended));
        Send(down);
        if (holdMs > 0) Thread.Sleep(holdMs);
        var up = new List<DesktopNative.INPUT> { Key(c.Key, true, c.Extended) };
        foreach (var m in c.Modifiers.Reverse()) up.Add(Key(m, true, m == VK_LWIN));
        Send(up);
    }

    /// <summary>Unicode text as KEYEVENTF_UNICODE events (Thai, emoji: each UTF-16 unit).</summary>
    public static void SendText(string text, CancellationToken ct)
    {
        foreach (char ch in text)
        {
            ct.ThrowIfCancellationRequested();
            if (ch == '\r') continue;
            if (ch == '\n') { SendChord(Parse("Enter")!); continue; }
            Send(new[]
            {
                new DesktopNative.INPUT { type = DesktopNative.INPUT_KEYBOARD, u = new() { ki = new() { wScan = ch, dwFlags = DesktopNative.KEYEVENTF_UNICODE } } },
                new DesktopNative.INPUT { type = DesktopNative.INPUT_KEYBOARD, u = new() { ki = new() { wScan = ch, dwFlags = DesktopNative.KEYEVENTF_UNICODE | DesktopNative.KEYEVENTF_KEYUP } } },
            });
        }
    }

    /// <summary>Click at a physical screen point (thread must be per-monitor DPI aware).</summary>
    public static void Click(int x, int y, bool right = false, bool dbl = false)
    {
        DesktopNative.SetCursorPos(x, y);
        Thread.Sleep(30);
        uint d = right ? DesktopNative.MOUSEEVENTF_RIGHTDOWN : DesktopNative.MOUSEEVENTF_LEFTDOWN;
        uint u = right ? DesktopNative.MOUSEEVENTF_RIGHTUP : DesktopNative.MOUSEEVENTF_LEFTUP;
        for (int i = 0; i < (dbl ? 2 : 1); i++)
        {
            Send(new[] { Mouse(d), Mouse(u) });
            if (dbl) Thread.Sleep(40);
        }
    }

    /// <summary>Press and release a button at a physical screen point: "left", "right" or "middle", once or twice.</summary>
    public static void ClickButton(int x, int y, string button, int clicks)
    {
        (uint d, uint u) = button switch
        {
            "right" => (DesktopNative.MOUSEEVENTF_RIGHTDOWN, DesktopNative.MOUSEEVENTF_RIGHTUP),
            "middle" => (DesktopNative.MOUSEEVENTF_MIDDLEDOWN, DesktopNative.MOUSEEVENTF_MIDDLEUP),
            _ => (DesktopNative.MOUSEEVENTF_LEFTDOWN, DesktopNative.MOUSEEVENTF_LEFTUP),
        };
        DesktopNative.SetCursorPos(x, y);
        Thread.Sleep(30);
        for (int i = 0; i < clicks; i++)
        {
            Send(new[] { Mouse(d), Mouse(u) });
            if (clicks > 1) Thread.Sleep(40);
        }
    }

    /// <summary>Left-drag from one physical point to another in small steps, so apps that track the move see it.
    /// The button is always released, even when the run is stopped mid-drag.</summary>
    public static void Drag(int x0, int y0, int x1, int y1, CancellationToken ct)
    {
        DesktopNative.SetCursorPos(x0, y0);
        Thread.Sleep(40);
        Send(new[] { Mouse(DesktopNative.MOUSEEVENTF_LEFTDOWN) });
        try
        {
            int steps = Math.Clamp((int)(Math.Sqrt(Math.Pow(x1 - x0, 2) + Math.Pow(y1 - y0, 2)) / 20), 5, 60);
            for (int i = 1; i <= steps; i++)
            {
                ct.ThrowIfCancellationRequested();
                DesktopNative.SetCursorPos(x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps);
                Thread.Sleep(12);
            }
            Thread.Sleep(40);
        }
        finally { Send(new[] { Mouse(DesktopNative.MOUSEEVENTF_LEFTUP) }); }
    }

    /// <summary>Wheel notches at a physical point: positive <paramref name="notches"/> scroll up / right.</summary>
    public static void Wheel(int x, int y, int notches, bool horizontal)
    {
        DesktopNative.SetCursorPos(x, y);
        Thread.Sleep(30);
        uint flags = horizontal ? DesktopNative.MOUSEEVENTF_HWHEEL : DesktopNative.MOUSEEVENTF_WHEEL;
        for (int i = 0; i < Math.Abs(notches); i++)
        {
            Send(new[] { new DesktopNative.INPUT
            {
                type = DesktopNative.INPUT_MOUSE,
                u = new() { mi = new() { dwFlags = flags, mouseData = unchecked((uint)(Math.Sign(notches) * DesktopNative.WHEEL_DELTA)) } },
            } });
            Thread.Sleep(30);
        }
    }

    private static DesktopNative.INPUT Mouse(uint flags) =>
        new() { type = DesktopNative.INPUT_MOUSE, u = new() { mi = new() { dwFlags = flags } } };

    private static DesktopNative.INPUT Key(ushort vk, bool up, bool ext) =>
        new()
        {
            type = DesktopNative.INPUT_KEYBOARD,
            u = new() { ki = new() { wVk = vk, dwFlags = (up ? DesktopNative.KEYEVENTF_KEYUP : 0) | (ext ? DesktopNative.KEYEVENTF_EXTENDEDKEY : 0) } },
        };

    private static void Send(IList<DesktopNative.INPUT> inputs)
    {
        var arr = inputs.ToArray();
        uint sent = DesktopNative.SendInput((uint)arr.Length, arr, Marshal.SizeOf<DesktopNative.INPUT>());
        if (sent != arr.Length)
            throw new InvalidOperationException("Windows blocked the input (a window of higher integrity — an elevated app — is in front, or the desktop is locked).");
    }
}
