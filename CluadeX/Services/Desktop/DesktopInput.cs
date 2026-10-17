using System.Runtime.InteropServices;

namespace CluadeX.Services.Desktop;

/// <summary>One key chord: modifiers + a key ("ctrl+shift+s", "Enter", "F5").</summary>
public sealed record DesktopChord(ushort[] Modifiers, ushort Key, bool Extended, string Text, bool UsesWin)
{
    /// <summary>Would this chord put text into the focused field: a printable key with no Ctrl/Alt, or a paste.</summary>
    public bool PutsText =>
        (Key == 0x56 && Modifiers.Contains(DesktopInput.VK_CONTROL))          // ctrl+v
        || (Key == 0x2D && Modifiers.Contains(DesktopInput.VK_SHIFT))         // shift+insert
        || (!Modifiers.Contains(DesktopInput.VK_CONTROL) && !Modifiers.Contains(DesktopInput.VK_MENU) && DesktopInput.IsPrintable(Key));
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

    /// <summary>Parse "ctrl+shift+s" / "Enter" / "alt+f4" / "a". Null when a part is unknown.</summary>
    public static DesktopChord? Parse(string token)
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
            short scan = VkKeyScanW(k[0]);
            if (scan == -1) return null;
            vk = (ushort)(scan & 0xFF);
            if ((scan & 0x100) != 0 && !mods.Contains(VK_SHIFT)) mods.Add(VK_SHIFT);
        }
        else if (k.Equals("win", StringComparison.OrdinalIgnoreCase)) { vk = VK_LWIN; win = true; }
        else return null;
        return new DesktopChord(mods.ToArray(), vk, ext, token.Trim(), win);
    }

    internal static bool IsPrintable(ushort vk) =>
        vk == 0x20 || (vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A) || (vk >= 0x60 && vk <= 0x6F) || (vk >= 0xBA && vk <= 0xE2);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScanW(char ch);

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
