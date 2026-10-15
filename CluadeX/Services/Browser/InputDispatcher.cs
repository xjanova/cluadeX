namespace CluadeX.Services.Browser;

/// <summary>One key as CDP Input.dispatchKeyEvent wants it.</summary>
public sealed record KeySpec(string Key, string Code, int VirtualKey, string? Text, int Location = 0);

/// <summary>A key press the agent asked for: the key plus modifiers (Alt=1, Ctrl=2, Meta=4, Shift=8).</summary>
public sealed record KeyChord(KeySpec Key, int Modifiers, IReadOnlyList<KeySpec> ModifierKeys);

/// <summary>
/// Trusted input through CDP: Input.dispatchMouseEvent / dispatchTouchEvent / dispatchKeyEvent / insertText.
/// These go through the browser's real input pipeline (isTrusted = true), which canvas games and pointer-capture
/// code require — a JavaScript-dispatched MouseEvent would be ignored by many of them. Coordinates are CSS pixels
/// relative to the viewport.
/// </summary>
public static class InputDispatcher
{
    public const int Alt = 1, Ctrl = 2, Meta = 4, Shift = 8;

    // ── mouse ──

    public static object Mouse(string type, double x, double y, string button = "none", int buttons = 0, int clickCount = 0, int modifiers = 0) => new
    {
        type, x = Math.Round(x, 2), y = Math.Round(y, 2), button, buttons, clickCount, modifiers,
        pointerType = "mouse",
    };

    public static object Wheel(double x, double y, double deltaX, double deltaY) => new
    {
        type = "mouseWheel", x = Math.Round(x, 2), y = Math.Round(y, 2), deltaX, deltaY, button = "none", pointerType = "mouse",
    };

    /// <summary>The "buttons" bit for a CDP button name (left=1, right=2, middle=4).</summary>
    public static int ButtonBit(string button) => button switch { "right" => 2, "middle" => 4, "left" => 1, _ => 0 };

    public static string NormalizeButton(string? b) => (b ?? "").Trim().ToLowerInvariant() switch
    {
        "right" => "right",
        "middle" or "wheel" => "middle",
        _ => "left",
    };

    // ── touch ──

    public static object Touch(string type, double x, double y, int modifiers = 0) => type == "touchEnd"
        ? new { type, touchPoints = Array.Empty<object>(), modifiers }
        : new { type, touchPoints = new object[] { new { x = Math.Round(x, 2), y = Math.Round(y, 2), radiusX = 1, radiusY = 1, force = 1, id = 1 } }, modifiers };

    // ── keys ──

    private static readonly Dictionary<string, KeySpec> Named = BuildNamed();

    private static Dictionary<string, KeySpec> BuildNamed()
    {
        var d = new Dictionary<string, KeySpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enter"] = new("Enter", "Enter", 13, "\r"),
            ["Return"] = new("Enter", "Enter", 13, "\r"),
            ["Tab"] = new("Tab", "Tab", 9, null),
            ["Escape"] = new("Escape", "Escape", 27, null),
            ["Esc"] = new("Escape", "Escape", 27, null),
            ["Backspace"] = new("Backspace", "Backspace", 8, null),
            ["Delete"] = new("Delete", "Delete", 46, null),
            ["Del"] = new("Delete", "Delete", 46, null),
            ["Insert"] = new("Insert", "Insert", 45, null),
            ["Space"] = new(" ", "Space", 32, " "),
            [" "] = new(" ", "Space", 32, " "),
            ["ArrowLeft"] = new("ArrowLeft", "ArrowLeft", 37, null),
            ["ArrowUp"] = new("ArrowUp", "ArrowUp", 38, null),
            ["ArrowRight"] = new("ArrowRight", "ArrowRight", 39, null),
            ["ArrowDown"] = new("ArrowDown", "ArrowDown", 40, null),
            ["Left"] = new("ArrowLeft", "ArrowLeft", 37, null),
            ["Up"] = new("ArrowUp", "ArrowUp", 38, null),
            ["Right"] = new("ArrowRight", "ArrowRight", 39, null),
            ["Down"] = new("ArrowDown", "ArrowDown", 40, null),
            ["Home"] = new("Home", "Home", 36, null),
            ["End"] = new("End", "End", 35, null),
            ["PageUp"] = new("PageUp", "PageUp", 33, null),
            ["PageDown"] = new("PageDown", "PageDown", 34, null),
            ["Shift"] = new("Shift", "ShiftLeft", 16, null, 1),
            ["Control"] = new("Control", "ControlLeft", 17, null, 1),
            ["Ctrl"] = new("Control", "ControlLeft", 17, null, 1),
            ["Alt"] = new("Alt", "AltLeft", 18, null, 1),
            ["Meta"] = new("Meta", "MetaLeft", 91, null, 1),
            ["Cmd"] = new("Meta", "MetaLeft", 91, null, 1),
            ["Win"] = new("Meta", "MetaLeft", 91, null, 1),
            ["CapsLock"] = new("CapsLock", "CapsLock", 20, null),
            ["Minus"] = new("-", "Minus", 189, "-"),
            ["Equal"] = new("=", "Equal", 187, "="),
            ["Comma"] = new(",", "Comma", 188, ","),
            ["Period"] = new(".", "Period", 190, "."),
            ["Slash"] = new("/", "Slash", 191, "/"),
            ["Semicolon"] = new(";", "Semicolon", 186, ";"),
            ["Quote"] = new("'", "Quote", 222, "'"),
            ["BracketLeft"] = new("[", "BracketLeft", 219, "["),
            ["BracketRight"] = new("]", "BracketRight", 221, "]"),
            ["Backslash"] = new("\\", "Backslash", 220, "\\"),
            ["Backquote"] = new("`", "Backquote", 192, "`"),
        };
        for (int i = 1; i <= 12; i++) d["F" + i] = new("F" + i, "F" + i, 111 + i, null);
        foreach (var (ch, code, vk) in new[] { ('-', "Minus", 189), ('=', "Equal", 187), (',', "Comma", 188), ('.', "Period", 190),
                                               ('/', "Slash", 191), (';', "Semicolon", 186), ('\'', "Quote", 222), ('[', "BracketLeft", 219),
                                               (']', "BracketRight", 221), ('\\', "Backslash", 220), ('`', "Backquote", 192) })
            d[ch.ToString()] = new(ch.ToString(), code, vk, ch.ToString());
        return d;
    }

    /// <summary>Parse one key token: "ArrowLeft", "Space", "a", "7", "ctrl+a", "Shift+Tab". Null = unknown.</summary>
    public static KeyChord? Parse(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        token = token.Trim();
        var parts = token.Length > 1 ? token.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : new[] { token };
        if (token.EndsWith("++", StringComparison.Ordinal)) parts = parts.Append("+").ToArray();   // "ctrl++"
        if (parts.Length == 0) return null;
        int mods = 0;
        var modKeys = new List<KeySpec>();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Ctrl; modKeys.Add(Named["Control"]); break;
                case "shift": mods |= Shift; modKeys.Add(Named["Shift"]); break;
                case "alt" or "option": mods |= Alt; modKeys.Add(Named["Alt"]); break;
                case "meta" or "cmd" or "win" or "super": mods |= Meta; modKeys.Add(Named["Meta"]); break;
                default: return null;
            }
        }
        var key = KeyFor(parts[^1], (mods & Shift) != 0);
        return key == null ? null : new KeyChord(key, mods, modKeys);
    }

    private static KeySpec? KeyFor(string name, bool shift)
    {
        if (Named.TryGetValue(name, out var k)) return k;
        if (name.Length == 1)
        {
            char c = name[0];
            if (c is >= 'a' and <= 'z' || c is >= 'A' and <= 'Z')
            {
                char lower = char.ToLowerInvariant(c);
                bool upper = char.IsUpper(c) || shift;
                string text = upper ? char.ToUpperInvariant(c).ToString() : lower.ToString();
                return new KeySpec(text, "Key" + char.ToUpperInvariant(c), char.ToUpperInvariant(c), text);
            }
            if (c is >= '0' and <= '9') return new KeySpec(c.ToString(), "Digit" + c, c, c.ToString());
            // Any other single character (Thai, symbols): a key event with that text; code unknown.
            return new KeySpec(c.ToString(), "", 0, c.ToString());
        }
        if (name.StartsWith("Key", StringComparison.Ordinal) && name.Length == 4 && char.IsLetter(name[3]))
            return KeyFor(name[3].ToString().ToLowerInvariant(), shift);
        if (name.StartsWith("Digit", StringComparison.Ordinal) && name.Length == 6 && char.IsDigit(name[5]))
            return KeyFor(name[5].ToString(), shift);
        return null;
    }

    /// <summary>keyDown (with text for printable keys, so keypress/input fire) or rawKeyDown.</summary>
    public static object KeyDown(KeySpec k, int modifiers, bool autoRepeat = false)
    {
        bool printable = k.Text != null && (modifiers & (Ctrl | Alt | Meta)) == 0;
        return new
        {
            type = printable ? "keyDown" : "rawKeyDown",
            key = k.Key, code = k.Code, windowsVirtualKeyCode = k.VirtualKey, nativeVirtualKeyCode = k.VirtualKey,
            text = printable ? k.Text : null, unmodifiedText = printable ? k.Text : null,
            modifiers, autoRepeat, location = k.Location,
        };
    }

    public static object KeyUp(KeySpec k, int modifiers) => new
    {
        type = "keyUp", key = k.Key, code = k.Code, windowsVirtualKeyCode = k.VirtualKey, nativeVirtualKeyCode = k.VirtualKey,
        modifiers, location = k.Location,
    };

    /// <summary>Parse "ctrl+shift" modifiers for clicks.</summary>
    public static int ParseModifiers(string? s)
    {
        int m = 0;
        foreach (var part in (s ?? "").Split('+', ',', ' ').Select(p => p.Trim().ToLowerInvariant()))
            m |= part switch { "ctrl" or "control" => Ctrl, "shift" => Shift, "alt" => Alt, "meta" or "cmd" or "win" => Meta, _ => 0 };
        return m;
    }
}
