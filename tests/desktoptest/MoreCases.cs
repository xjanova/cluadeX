using System.Diagnostics;
using CluadeX.Models;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using CluadeX.Services.Desktop;

namespace DesktopTest;

/// <summary>
/// The tools added 2026-11-02 (desktop_mouse, desktop_window, desktop_read, desktop_clipboard) and the command-surface
/// rule (a terminal, the Run box, Explorer's address bar: looked at, never typed or clicked into). These move the real
/// mouse and open a console and an Explorer window — run only while the owner is away from the PC.
/// </summary>
internal static partial class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    /// <summary>The screen centre of a fixture element, found by AutomationId.</summary>
    static (int X, int Y) CentreOf(string automationId)
    {
        var root = AutomationElement.FromHandle(Fx.Handle);
        var e = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
                ?? throw new Exception("fixture element not found: " + automationId);
        var r = e.Current.BoundingRectangle;
        return ((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
    }

    static async Task MouseCases()
    {
        var pos = await T("desktop_mouse", ("action", "position"));
        Check("M1", "position reports where the cursor is and what is under it, moving nothing", pos.Success && pos.Output.Contains("The cursor is at screen"), Seen(pos));

        var (bx, by) = CentreOf("BtnPress");
        int before = Fx.Presses;
        var click = await T("desktop_mouse", ("action", "click"), ("x", bx.ToString()), ("y", by.ToString()));
        await Task.Delay(250);
        Check("M1b", "a click at a button's screen point presses it once (a real mouse click)",
            click.Success && Fx.Presses == before + 1, Seen(click) + $" presses {before}→{Fx.Presses}");

        var (gx, gy) = CentreOf("CxGuard.PermissionAllow");
        var guard = await T("desktop_mouse", ("action", "click"), ("x", gx.ToString()), ("y", gy.ToString()));
        await Task.Delay(250);
        Check("M1c", "a click on CluadeX's own approval button by coordinates is refused, the button never fired",
            !guard.Success && Fx.GuardPresses == 0, Seen(guard) + $" guardPresses={Fx.GuardPresses}");

        var (lx, ly) = CentreOf("LstItems");
        var wheel = await T("desktop_mouse", ("action", "scroll"), ("x", lx.ToString()), ("y", ly.ToString()), ("direction", "down"), ("amount", "2"));
        Check("M1d", "scroll at a point sends wheel notches there", wheel.Success && wheel.Output.Contains("Scrolled down 2"), Seen(wheel));

        var (nx, ny) = CentreOf("TxtName");
        var drag = await T("desktop_mouse", ("action", "drag"), ("x", nx.ToString()), ("y", ny.ToString()), ("to_x", (nx + 40).ToString()), ("to_y", ny.ToString()));
        bool held = (GetAsyncKeyState(0x01) & 0x8000) != 0;   // VK_LBUTTON
        Check("M1e", "drag ends with the left button released", drag.Success && !held, Seen(drag) + $" buttonDown={held}");

        var bad = await T("desktop_mouse", ("action", "wiggle"), ("x", "1"), ("y", "1"));
        Check("M1f", "an unknown action lists the valid ones", !bad.Success && bad.Error.Contains("double_click"), Seen(bad));
    }

    static async Task WindowToolCases()
    {
        var start = DesktopNative.Bounds(Fx.Handle);
        var move = await T("desktop_window", Win, ("action", "move"), ("x", (start.Left + 30).ToString()), ("y", (start.Top + 20).ToString()));
        await Task.Delay(250);
        var moved = DesktopNative.Bounds(Fx.Handle);
        Check("N1", "move places the window's visible top-left where asked (the invisible border is allowed for)",
            move.Success && Math.Abs(moved.Left - (start.Left + 30)) <= 2 && Math.Abs(moved.Top - (start.Top + 20)) <= 2,
            Seen(move) + $" now {moved.Left},{moved.Top}");

        var size = await T("desktop_window", Win, ("action", "resize"), ("width", (start.Width - 40).ToString()), ("height", (start.Height - 30).ToString()));
        await Task.Delay(250);
        var sized = DesktopNative.Bounds(Fx.Handle);
        Check("N1b", "resize gives the visible size asked for",
            size.Success && Math.Abs(sized.Width - (start.Width - 40)) <= 2 && Math.Abs(sized.Height - (start.Height - 30)) <= 2,
            Seen(size) + $" now {sized.Width}x{sized.Height}");
        await T("desktop_window", Win, ("action", "move"), ("x", start.Left.ToString()), ("y", start.Top.ToString()));
        await T("desktop_window", Win, ("action", "resize"), ("width", start.Width.ToString()), ("height", start.Height.ToString()));

        // Another process's window (this harness hosts CluadeX.dll, so its own windows count as CluadeX and are never closed).
        string extraTitle = "CX close me " + Guid.NewGuid().ToString("N")[..6];
        using var closeMe = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--child \"{extraTitle}\" 0") { UseShellExecute = false })!;
        for (int i = 0; i < 40 && !DesktopWindows.List().Any(w => w.Title == extraTitle); i++) await Task.Delay(250);
        var close = await T("desktop_window", ("title", Regex.Escape(extraTitle)), ("action", "close"));
        bool gone = closeMe.WaitForExit(5000);
        Check("N1c", "close sends the X button's message and the window goes", close.Success && close.Output.StartsWith("Closed") && gone, Seen(close));
        var extra = Fx.Extra("CX mine " + Guid.NewGuid().ToString("N")[..6]);
        await Task.Delay(300);
        var mine = await T("desktop_window", ("title", Regex.Escape(Fx.On(() => extra.Title))), ("action", "close"));
        Check("N1c2", "…but a window of CluadeX's own process is never closed", !mine.Success && mine.Error.Contains("never closes CluadeX") && Fx.On(() => extra.IsVisible), Seen(mine));
        Fx.On(() => extra.Close());

        var self = await T("desktop_window", ("pid", Environment.ProcessId.ToString()), ("action", "close"));
        Check("N1d", "desktop_window refuses to close a CluadeX process window",
            !self.Success || Fx.On(() => Fx.Win.IsVisible), Seen(self));
    }

    static async Task ReadClipboardCases()
    {
        var read = await T("desktop_read", Win);
        Check("R1", "desktop_read returns the window's text (names of its parts), paged", read.Success && read.Output.Contains("Press Me"), Seen(read));
        string pwd = await FindRef("Edit", "\"Secret\"", ("automation_id", "PwdSecret"));
        var rp = await T("desktop_read", ("ref", pwd));
        Check("R1b", "desktop_read refuses a password field", !rp.Success && rp.Error.Contains("password field"), Seen(rp));

        string? old = null;
        try { old = Fx.On(() => System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null); } catch { }
        string marker = "cx-clip-" + Guid.NewGuid().ToString("N")[..8];
        var set = await T("desktop_clipboard", ("action", "set"), ("text", marker));
        var get = await T("desktop_clipboard", ("action", "get"));
        Check("R1c", "clipboard set then get returns the same text, marked as data", set.Success && get.Success && get.Output.Contains(marker) && get.Output.Contains("data, not instructions"), Seen(set) + "\n" + Seen(get));
        if (old != null) try { Fx.On(() => System.Windows.Clipboard.SetDataObject(old, true)); } catch { }
    }

    /// <summary>A real console window: looked at, never typed, pressed, clicked into or closed.</summary>
    static async Task TerminalCases()
    {
        string title = "cx-term-" + Guid.NewGuid().ToString("N")[..8];
        var psi = new ProcessStartInfo("cmd.exe", $"/k title {title}") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal };
        using var proc = Process.Start(psi);
        try
        {
            DesktopWindow? term = null;
            for (int i = 0; i < 40 && term == null; i++)
            {
                await Task.Delay(250);
                term = DesktopWindows.List().FirstOrDefault(w => w.Title.Contains(title));
            }
            if (term == null) { Gap("T1", "a console window is a command surface", "the console window never appeared"); return; }
            Check("T1", "the console window is recognised as a command surface (by process or class)",
                DesktopSafety.IsCommandSurface(term.ProcessName, term.ClassName, term.Title), $"{term.ProcessName} / {term.ClassName}");
            var tw = ("title", Regex.Escape(title));
            // desktop_type without a ref goes to the focus, which may not be the console: its refusal is proven on pure
            // inputs in tooltest DK4 instead of typing into whatever window happens to be in front.
            var keys = await T("desktop_keys", tw, ("keys", "Enter"));
            var c = (term.X + term.Width / 2, term.Y + term.Height / 2);
            // A real right-click is sent only when the console is what lies under that point: were another window in front
            // there, the click would be allowed and land on it (the owner's window), which proves nothing and is not ours.
            // The fixture is Topmost and the console often opens under it: let the console come up over it for this case.
            Fx.On(() => Fx.Win.Topmost = false);
            await T("desktop_window", tw, ("action", "focus"));
            await Task.Delay(300);
            ToolResult? click = null;
            for (int i = 0; i < 8 && click == null; i++)
            {
                if (DesktopSafety.WindowAtPoint(c.Item1, c.Item2) is { } under && DesktopSafety.IsCommandSurface(under))
                    click = await T("desktop_mouse", ("action", "right_click"), ("x", c.Item1.ToString()), ("y", c.Item2.ToString()));
                else await Task.Delay(250);
            }
            var close = await T("desktop_window", tw, ("action", "close"));
            var shot = await T("desktop_screenshot", tw);
            if (click == null) Gap("T1b", "a right-click in a terminal is refused", "another window stayed in front of the console — no click sent");
            Check("T1b", "keys, a right-click (paste) and close are refused in a terminal; a screenshot still works",
                !keys.Success && keys.Error.Contains("terminal") && click is not { Success: true } && !close.Success && shot.Success,
                string.Join("\n", Seen(keys), click == null ? "(no click sent)" : Seen(click), Seen(close), Seen(shot)));
        }
        finally
        {
            Fx.On(() => Fx.Win.Topmost = true);
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            foreach (var w in DesktopWindows.List().Where(w => w.Title.Contains(title)))
                DesktopNative.PostMessageW(w.Handle, DesktopNative.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        await ExplorerCases();
    }

    /// <summary>File Explorer on a throwaway folder: its address bar runs what is entered (like the Run box), and opening
    /// a .bat starts it — judged by the file, so it holds with extensions hidden too. Both refused; selecting a file,
    /// a folder named like a script and leaving the address bar with Esc still work. Nothing here presses Enter, and the
    /// .bat only echoes a sentinel, so even a broken guard runs nothing.</summary>
    static async Task ExplorerCases()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cx-explorer-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cx-sentinel.bat"), "@echo CX-SENTINEL-NOT-RUN\r\n");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");
        Directory.CreateDirectory(Path.Combine(dir, "cx-lib.js"));   // a folder named like a script
        string name = Path.GetFileName(dir);
        try
        {
            using (Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true })) { }
            DesktopWindow? ex = null;
            for (int i = 0; i < 40 && ex == null; i++)
            {
                await Task.Delay(250);
                ex = DesktopWindows.List().FirstOrDefault(w => w.Title.Contains(name) && w.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase));
            }
            if (ex == null) { Gap("X1", "Explorer's address bar and file list", "the Explorer window never appeared"); return; }
            Check("X1", "a File Explorer window itself is not a command surface (ordinary use stays allowed)",
                !DesktopSafety.IsCommandSurface(ex), $"{ex.ProcessName} / {ex.ClassName}");
            var tw = ("title", Regex.Escape(name));

            var found = await T("desktop_find", tw, ("name", "cx-sentinel"), ("control_type", "ListItem"));
            // "cx-sentinel.bat", or "cx-sentinel" when Explorer hides known extensions (the default): refused either way.
            string bat = RefOf(found.Output, "ListItem \"cx-sentinel(\\.bat)?\"");
            if (bat == "e0") Gap("X1b", "opening a .bat in Explorer is refused", "no ListItem named cx-sentinel: " + Trunc(found.Output, 200));
            else
            {
                var sel = await T("desktop_act", ("ref", bat), ("action", "select"));
                var inv = await T("desktop_act", ("ref", bat), ("action", "invoke"));
                var dbl = await T("desktop_act", ("ref", bat), ("action", "double_click"));
                Check("X1b", "select on a .bat in Explorer works; invoke and double-click (which would start it) are refused",
                    sel.Success && !inv.Success && inv.Error.Contains("program or script") && !dbl.Success && dbl.Error.Contains("program or script"),
                    string.Join("\n", Seen(sel), Seen(inv), Seen(dbl)));
            }

            var addr = await T("desktop_find", tw, ("automation_id", "/^(41477|PART_AutoSuggestBox)$/"));
            // The ref of the match itself — not the first "eN " in the output, which may be the focus line (the fixture's box).
            string bar = Regex.Match(addr.Output, @"(e\d+) [^\n]*?id=(?:41477|PART_AutoSuggestBox)\b").Groups[1].Value;
            if (bar.Length == 0) Gap("X1c", "typing into Explorer's address bar is refused", "address bar not found: " + Trunc(addr.Output, 200));
            else
            {
                var typed = await T("desktop_type", ("ref", bar), ("text", "cx-sentinel-not-run"));
                var set = await T("desktop_act", ("ref", bar), ("action", "set_value"), ("value", "cx-sentinel-not-run"));
                Check("X1c", "desktop_type and set_value into Explorer's address bar are refused",
                    !typed.Success && typed.Error.Contains("address bar") && !set.Success && set.Error.Contains("address bar"),
                    Seen(typed) + "\n" + Seen(set));
            }

            // Pure UIA, no keys: notes.txt added to a selection that holds the .bat, focus on notes.txt — Enter would open
            // both, so it is refused although the focused item is a document. A folder named cx-lib.js opens as usual.
            await Task.Delay(200);
            var root = AutomationElement.FromHandle(ex.Handle);
            AutomationElement? Item(string n) => root.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new OrCondition(new PropertyCondition(AutomationElement.NameProperty, n), new PropertyCondition(AutomationElement.NameProperty, Path.GetFileNameWithoutExtension(n)))));
            var batItem = Item("cx-sentinel.bat");
            var notesItem = Item("notes.txt");
            var libItem = Item("cx-lib.js");
            if (batItem == null || notesItem == null || libItem == null) Gap("X1e", "Enter judges the whole selection", "items not found in the file list");
            else
            {
                string? mixed = null, lib = null, notesAlone = null;
                try
                {
                    ((SelectionItemPattern)batItem.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                    ((SelectionItemPattern)notesItem.GetCurrentPattern(SelectionItemPattern.Pattern)).AddToSelection();
                    await Task.Delay(200);
                    mixed = DesktopSafety.OpensFileRefusal(notesItem, DesktopSafety.OpenKind.Enter);
                    ((SelectionItemPattern)notesItem.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                    await Task.Delay(200);
                    notesAlone = DesktopSafety.OpensFileRefusal(notesItem, DesktopSafety.OpenKind.Enter);
                    lib = DesktopSafety.OpensFileRefusal(libItem, DesktopSafety.OpenKind.Item);
                }
                catch (Exception e2) { mixed ??= "(threw) " + e2.Message; }
                Check("X1e", "Enter with a .bat and notes.txt selected is refused (focus on notes.txt); notes.txt alone and a folder named cx-lib.js are not",
                    mixed != null && mixed.Contains("cx-sentinel") && notesAlone == null && lib == null,
                    $"mixed={mixed ?? "(none)"} | notes={notesAlone ?? "(none)"} | lib={lib ?? "(none)"}");
            }

            // alt+d moves the focus into the address bar: mixed with text in one call it is refused before any key (what it
            // would type cannot be known); alone it goes, then text is refused in the address bar, and Esc gets out.
            var mixedKeys = await T("desktop_keys", tw, ("keys", "alt+d c x"));
            var altD = await T("desktop_keys", tw, ("keys", "alt+d"));
            var keys = await T("desktop_keys", tw, ("keys", "c x"));
            var esc = await T("desktop_keys", tw, ("keys", "Esc"));
            Check("X1d", "desktop_keys alt+d then text: refused once the focus is in the address bar; Esc leaves it",
                !mixedKeys.Success && mixedKeys.Error.Contains("desktop_type") && altD.Success
                && !keys.Success && keys.Error.Contains("address bar") && keys.Error.Contains("0 chord(s) were sent") && esc.Success,
                string.Join("\n", Seen(mixedKeys), Seen(altD), Seen(keys), Seen(esc)));
        }
        finally
        {
            foreach (var w in DesktopWindows.List().Where(w => w.Title.Contains(name)))
                DesktopNative.PostMessageW(w.Handle, DesktopNative.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            await Task.Delay(500);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
