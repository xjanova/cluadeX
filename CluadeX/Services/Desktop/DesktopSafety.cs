using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace CluadeX.Services.Desktop;

/// <summary>
/// What the desktop tools never touch, whatever the permission rules say:
/// <list type="bullet">
/// <item>the secure desktop (UAC's dim screen, the lock / sign-in screen) — the input desktop is not "Default";</item>
/// <item>credential and elevation windows — Windows Security, User Account Control, sign-in and Windows Hello
///   prompts (by process, window class and title);</item>
/// <item>password fields (UIA IsPassword) — never typed into, never read;</item>
/// <item>CluadeX's own permission row, question card and spend-guard prompt (elements whose AutomationId starts
///   with <see cref="GuardIdPrefix"/>) — the agent must never approve itself.</item>
/// </list>
/// </summary>
public static partial class DesktopSafety
{
    /// <summary>AutomationId prefix of CluadeX's approval UI (ChatView.xaml: CxGuard.Permission, CxGuard.Question…).</summary>
    public const string GuardIdPrefix = "CxGuard";

    private static readonly HashSet<string> CredentialProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "consent", "CredentialUIBroker", "LogonUI", "LockApp", "UserAccountBroker", "CredentialEnrollmentManager",
        "SecHealthUI", "SecurityHealthHost", "BioEnrollmentHost", "WinLogon",
    };

    private static readonly HashSet<string> CredentialClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Credential Dialog Xaml Host", "$$$Secure UAP Dummy Window Class For Interim Dialog", "NativeHWNDHost#Credential",
    };

    [GeneratedRegex(@"^(windows security|user account control|windows hello|sign in( to .*)?|credential|enter (network )?password|"
                    + @"ความปลอดภัยของ windows|การควบคุมบัญชีผู้ใช้|ลงชื่อเข้าใช้)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialTitle();

    /// <summary>Non-null (the reason) when the input desktop is not the normal one: UAC or the lock screen is up.</summary>
    public static string? SecureDesktopRefusal()
    {
        string? name;
        try { name = DesktopNative.InputDesktopName(); }
        catch { name = null; }
        if (name != null && name.Equals("Default", StringComparison.OrdinalIgnoreCase)) return null;
        return "The secure desktop is active (a User Account Control prompt, the lock screen or sign-in). Desktop tools never act there — "
             + "ask the user to answer it themselves, then try again.";
    }

    /// <summary>Non-null (the reason) when the window is a credential / elevation / Windows Security window.</summary>
    public static string? CredentialWindowRefusal(DesktopWindow w) =>
        IsCredentialWindow(w.ProcessName, w.ClassName, w.Title)
            ? $"\"{DesktopWindows.Trunc(w.Title, 60)}\" ({w.ProcessName}) is a credential, sign-in or Windows Security window. "
              + "Desktop tools never read, type into or click such windows — the user must answer it themselves."
            : null;

    public static bool IsCredentialWindow(string? processName, string? className, string? title) =>
        CredentialProcesses.Contains(processName ?? "")
        || CredentialClasses.Contains(className ?? "")
        || CredentialTitle().IsMatch((title ?? "").Trim());

    /// <summary>Non-null (the reason) when <paramref name="processName"/> matches one of the user's blocked apps
    /// (AppSettings.DesktopBlockedApps: names, '*' wildcards).</summary>
    public static string? BlockedAppRefusal(string? processName, IEnumerable<string>? blocked)
    {
        if (string.IsNullOrWhiteSpace(processName) || blocked == null) return null;
        foreach (var raw in blocked)
        {
            string pat = (raw ?? "").Trim();
            if (pat.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) pat = pat[..^4];
            if (pat.Length == 0) continue;
            bool hit = pat.Contains('*')
                ? Regex.IsMatch(processName, "^" + Regex.Escape(pat).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
                : processName.Equals(pat, StringComparison.OrdinalIgnoreCase);
            if (hit)
                return $"{processName} is on the list of apps the desktop tools never touch (Settings ▸ desktop control ▸ blocked apps — "
                     + "password managers by default). Ask the user to do this part themselves.";
        }
        return null;
    }

    /// <summary>The refusal for a password field, or null.</summary>
    public static string? PasswordRefusal(AutomationElement e)
    {
        bool pw;
        try { pw = e.Current.IsPassword; }
        catch { pw = false; }
        return pw ? "That element is a password field. Desktop tools never type into or read password fields — ask the user to fill it in." : null;
    }

    /// <summary>The processes whose elements are CluadeX's own UI: this process, and any process named CluadeX
    /// (the installed copy while a dev build drives it).</summary>
    public static bool IsCluadeXProcess(int pid)
    {
        if (pid == Environment.ProcessId) return true;
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName.StartsWith("CluadeX", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Non-null when <paramref name="e"/> is (inside) CluadeX's permission / question / spend-guard UI.</summary>
    public static string? GuardRefusal(AutomationElement? e)
    {
        if (e == null) return null;
        int pid;
        try { pid = e.Current.ProcessId; }
        catch { return null; }
        if (!IsCluadeXProcess(pid)) return null;
        var walker = TreeWalker.RawViewWalker;
        var cur = e;
        for (int depth = 0; cur != null && depth < 64; depth++)
        {
            string id;
            try { id = cur.Current.AutomationId ?? ""; }
            catch { break; }
            if (id.StartsWith(GuardIdPrefix, StringComparison.Ordinal))
                return "That element is part of CluadeX's own permission / question / spend prompt. The agent never answers its own "
                     + "approvals — the user does.";
            try { cur = walker.GetParent(cur); }
            catch { break; }
            if (cur == null || Automation.Compare(cur, AutomationElement.RootElement)) break;
        }
        return null;
    }

    // ── Command surfaces (owner 2026-11-02: computer use must be complete, and safe) ──
    // A terminal, the Run box and Task Manager's "Create new task" run whatever is typed into them. Keys or a click there
    // would run a command that never passed DangerousCommandCheck or the approval prompt run_command goes through — one
    // "allow PowerShell" was enough for `rd /s /q C:\` (the 2026-10-17 incident came through a tool, not a prompt).
    // Looking at them (screenshot, desktop_read, scrolling) stays allowed.

    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "powershell_ise", "WindowsTerminal", "wt", "OpenConsole", "conhost", "bash", "sh", "zsh",
        "wsl", "wslhost", "mintty", "ConEmu", "ConEmu64", "ConEmuC", "ConEmuC64", "Cmder", "alacritty", "wezterm", "wezterm-gui",
        "Hyper", "Tabby", "kitty", "putty", "MobaXterm", "Termius", "FluentTerminal.App", "Warp",
    };

    private static readonly HashSet<string> TerminalClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow", "mintty", "VirtualConsoleClass", "PuTTY",
    };

    [GeneratedRegex(@"^(run|เรียกใช้|create new task|สร้างงานใหม่)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RunBoxTitle();

    /// <summary>Whether a window is a terminal, by process or window class.</summary>
    public static bool IsTerminalWindow(string? processName, string? className) =>
        TerminalProcesses.Contains(processName ?? "") || TerminalClasses.Contains(className ?? "");

    // Windows Search / Start (Windows 11's SearchHost and StartMenuExperienceHost, Windows 10's SearchApp / SearchUI) and
    // launchers like them: what is typed there and entered (or a result clicked) opens the file or runs the command line it
    // names — "deploy.bat", "powershell -c …", "Run command" — without the script check desktop_launch makes.
    private static readonly HashSet<string> LauncherProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "SearchHost", "StartMenuExperienceHost", "SearchApp", "SearchUI", "PowerToys.PowerLauncher", "Microsoft.CmdPal.UI", "Flow.Launcher",
    };

    /// <summary>Whether a window is Windows Search, Start or a launcher like them (by process).</summary>
    public static bool IsLauncherWindow(string? processName) => LauncherProcesses.Contains(processName ?? "");

    /// <summary>Whether a window runs what is typed into it: a terminal (by process or window class), Windows Search /
    /// Start, the Run box, or Task Manager's Create new task.</summary>
    public static bool IsCommandSurface(string? processName, string? className, string? title)
    {
        if (IsTerminalWindow(processName, className) || IsLauncherWindow(processName)) return true;
        // The Run box and Create new task are plain dialogs (#32770) of explorer / Task Manager, known by their title.
        return string.Equals(className, "#32770", StringComparison.Ordinal)
               && (string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase) || string.Equals(processName, "Taskmgr", StringComparison.OrdinalIgnoreCase))
               && RunBoxTitle().IsMatch((title ?? "").Trim());
    }

    /// <summary>The control id of the Run box's command combo box (shell32's RunFileDlg: Win+R, Task Manager's Create
    /// new task, and rundll32 shell32,#61, which hosts it in any process).</summary>
    public const int RunBoxComboId = 12298;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dialog, int id);

    /// <summary>A #32770 dialog whose control <see cref="RunBoxComboId"/> is a combo box: the Run box in any display
    /// language, whatever its title says (a German Windows calls it "Ausführen").</summary>
    public static bool LooksLikeRunBox(string? className, string? runComboClass) =>
        string.Equals(className, "#32770", StringComparison.Ordinal)
        && (runComboClass ?? "").StartsWith("ComboBox", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="w"/> is the Run box or Create new task: by title, or by its structure.</summary>
    public static bool IsRunBox(DesktopWindow w)
    {
        if (IsTerminalWindow(w.ProcessName, w.ClassName) || IsLauncherWindow(w.ProcessName)) return false;
        if (IsCommandSurface(w.ProcessName, w.ClassName, w.Title)) return true;
        if (w.Handle == IntPtr.Zero || !string.Equals(w.ClassName, "#32770", StringComparison.Ordinal)) return false;
        IntPtr combo;
        try { combo = GetDlgItem(w.Handle, RunBoxComboId); }
        catch { return false; }
        return combo != IntPtr.Zero && LooksLikeRunBox(w.ClassName, DesktopNative.ClassName(combo));
    }

    /// <summary>Whether a window is a command surface: a terminal, Windows Search / Start, or the Run box / Create new
    /// task by title or structure.</summary>
    public static bool IsCommandSurface(DesktopWindow w) =>
        IsTerminalWindow(w.ProcessName, w.ClassName) || IsLauncherWindow(w.ProcessName) || IsRunBox(w);

    // ── command boxes inside other apps ──

    /// <summary>A part of an xterm.js terminal (VS Code, Cursor, Electron and browser terminals): Chromium reports the
    /// HTML class, and the terminal's parts ("xterm", "xterm-screen", "xterm-helper-textarea") all carry it. The point
    /// under the mouse is the canvas or rows div, never the hidden textarea, so the walk up matters.</summary>
    public static bool IsXtermPart(string? className) =>
        (className ?? "").Contains("xterm", StringComparison.OrdinalIgnoreCase)
        || (className ?? "").Contains("terminal-wrapper", StringComparison.OrdinalIgnoreCase);

    /// <summary>A part of the address bar of File Explorer or a file dialog: what is typed there and entered runs, like
    /// the Run box. "Address Band Root" / "Breadcrumb Parent" hold the classic one in any process (a file dialog's too).
    /// The bare ids (control id 41477, Windows 11's XAML PART_AutoSuggestBox) are ordinary names elsewhere — a Win32 id,
    /// WPF/WinUI template-part naming — so they count only in explorer. The file list, the tree and a dialog's file-name
    /// box are not part of it.</summary>
    public static bool IsAddressBarPart(string? className, string? automationId, string? processName = "explorer") =>
        string.Equals(className, "Address Band Root", StringComparison.OrdinalIgnoreCase)
        || string.Equals(className, "Breadcrumb Parent", StringComparison.OrdinalIgnoreCase)
        || (automationId is "41477" or "PART_AutoSuggestBox" && string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase));

    private static readonly HashSet<string> IdeProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "devenv", "idea", "idea64", "pycharm", "pycharm64", "rider", "rider64", "webstorm", "webstorm64", "clion", "clion64",
        "goland", "goland64", "phpstorm", "phpstorm64", "rubymine", "rubymine64", "datagrip", "datagrip64", "rustrover",
        "rustrover64", "studio", "studio64", "fleet",
    };

    /// <summary>Editors built on VS Code (xterm.js terminal, the quick-input command palette).</summary>
    private static readonly HashSet<string> CodeEditorProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code", "Code - Insiders", "VSCodium", "Cursor", "Windsurf", "Trae", "Kiro", "Positron", "Void", "Antigravity",
    };

    /// <summary>An app with a terminal panel and a command palette that can send text to it: VS Code and its forks,
    /// Visual Studio, JetBrains IDEs.</summary>
    public static bool IsTerminalHost(string? processName) =>
        CodeEditorProcesses.Contains(processName ?? "") || IdeProcesses.Contains(processName ?? "");

    // Anchored: the pane is named for the tool window itself ("Terminal", "Terminal: Local", "Terminal - pwsh",
    // "Developer PowerShell for VS 2022"), never a document named after a file ("TerminalHelper.cs", "Editor for
    // TerminalView.kt") or a folder called "terminal" in a tree.
    [GeneratedRegex(@"^(terminal|developer powershell|developer command prompt|package manager console)(\s*$|\s*[:\-–—(]|\s+\d|\s+for\s)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdeTerminalName();

    // Tool-window ids and JetBrains' terminal widget classes; automation ids run words together ("TerminalToolWindow").
    [GeneratedRegex(@"^(terminal|terminal(tool)?window|terminal(panel|pane|view|widget)|jediterm\w*|jbterminal\w*|shellterminal\w*)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdeTerminalId();

    [GeneratedRegex(@"\.[a-z0-9]{1,8}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EndsWithExtension();

    /// <summary>A terminal tool window of Visual Studio or a JetBrains IDE (not xterm.js: JediTerm, VS's own consoles),
    /// known by the name or automation id of a pane around the focus. Tabs, documents and items (an editor tab for
    /// terminal.py, a "terminal" folder in a tree) never count.</summary>
    public static bool IsIdeTerminalPart(string? processName, string? name, string? automationId, string? controlType = null)
    {
        if (!IdeProcesses.Contains(processName ?? "")) return false;
        string ct = (controlType ?? "").Replace("ControlType.", "");
        if (ct is "TabItem" or "Document" or "TreeItem" or "ListItem" or "DataItem" or "Edit" or "Text") return false;
        string n = (name ?? "").Trim(), id = (automationId ?? "").Trim();
        return (IdeTerminalName().IsMatch(n) && !EndsWithExtension().IsMatch(n))
               || (IdeTerminalId().IsMatch(id) && !EndsWithExtension().IsMatch(id));
    }

    /// <summary>JetBrains' Run Anything popup (ctrl twice): it runs what is typed into it.</summary>
    public static bool IsIdeRunBox(string? processName, string? name) =>
        IdeProcesses.Contains(processName ?? "") && (name ?? "").Trim().StartsWith("Run Anything", StringComparison.OrdinalIgnoreCase);

    /// <summary>What kind of command box <paramref name="e"/> sits in ("a terminal panel", "the address bar …"), or null.
    /// Walks up to the top-level window but does not judge the window's own name (a project called "Terminal" is not a
    /// terminal pane). <paramref name="addressBar"/> false leaves the address bar out: a left click there only moves the
    /// caret, and its breadcrumb buttons are how people navigate.</summary>
    public static string? CommandInputKind(AutomationElement? e, bool addressBar = true)
    {
        if (e == null) return null;
        string proc;
        try { proc = DesktopWindows.ProcessNameOf(e.Current.ProcessId); }
        catch { return null; }
        bool ide = IdeProcesses.Contains(proc);
        var walker = TreeWalker.RawViewWalker;
        var root = AutomationElement.RootElement;
        AutomationElement? cur = e;
        for (int depth = 0; cur != null && depth < 24; depth++)
        {
            string cls, id, name, type = "";
            try
            {
                cls = cur.Current.ClassName ?? ""; id = cur.Current.AutomationId ?? ""; name = cur.Current.Name ?? "";
                if (ide) type = cur.Current.ControlType?.ProgrammaticName ?? "";
            }
            catch { break; }
            AutomationElement? parent;
            try { parent = walker.GetParent(cur); }
            catch { parent = null; }
            bool topLevel = parent == null || Automation.Compare(parent, root);
            if (IsXtermPart(cls)) return "a terminal panel";
            if (addressBar && IsAddressBarPart(cls, id, proc)) return "the address bar (it runs what is entered there, like the Run box)";
            if (ide && IsIdeRunBox(proc, name)) return "the IDE's Run Anything box (it runs what is typed there)";
            if (topLevel) break;
            if (ide && IsIdeTerminalPart(proc, name, id, type)) return "a terminal panel";
            cur = parent;
        }
        return null;
    }

    /// <summary>Whether <paramref name="e"/> is an entry of an address bar's history dropdown or autocomplete list:
    /// choosing one enters it in the address bar, like typing it and pressing Enter.</summary>
    public static bool IsAddressDropdownItem(AutomationElement? e)
    {
        if (e == null) return false;
        try
        {
            var ct = e.Current.ControlType;
            bool entry = ct == ControlType.ListItem || ct == ControlType.DataItem;
            string proc = DesktopWindows.ProcessNameOf(e.Current.ProcessId);
            bool explorer = string.Equals(proc, "explorer", StringComparison.OrdinalIgnoreCase);
            var walker = TreeWalker.RawViewWalker;
            var root = AutomationElement.RootElement;
            AutomationElement? cur = e;
            for (int depth = 0; cur != null && depth < 12; depth++)
            {
                string cls = cur.Current.ClassName ?? "", id = cur.Current.AutomationId ?? "";
                // The shell's autocomplete popup. It also drops down under a file dialog's file-name box and any other
                // SHAutoComplete edit, so it counts only for an address bar: always in explorer (its address bar, the Run
                // box), elsewhere when the focus — which stays in the edit while the popup is open — is in the address band.
                if (string.Equals(cls, "Auto-Suggest Dropdown", StringComparison.OrdinalIgnoreCase)) return explorer || FocusInAddressBar(proc);
                // Windows 11's XAML address bar: its suggestion list is a popup of its own.
                if (explorer && id is "SuggestionsList" or "PART_SuggestionsList" or "SuggestionsPopup") return true;
                // The classic address combo's history list sits under the combo in the UIA tree.
                if (entry && IsAddressBarPart(cls, id, proc)) return true;
                var parent = walker.GetParent(cur);
                if (parent == null || Automation.Compare(parent, root)) break;
                cur = parent;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Whether the keyboard focus is in an address bar (or in the autocomplete popup itself). True when the focus
    /// cannot be read or is in another process: an address bar's list is the one that must not slip through.</summary>
    private static bool FocusInAddressBar(string proc)
    {
        try
        {
            var f = AutomationElement.FocusedElement;
            if (f == null || !string.Equals(DesktopWindows.ProcessNameOf(f.Current.ProcessId), proc, StringComparison.OrdinalIgnoreCase)) return true;
            var walker = TreeWalker.RawViewWalker;
            var root = AutomationElement.RootElement;
            var path = new List<(string?, string?)>();
            AutomationElement? cur = f;
            for (int depth = 0; cur != null && depth < 16; depth++)
            {
                path.Add((cur.Current.ClassName, cur.Current.AutomationId));
                var parent = walker.GetParent(cur);
                if (parent == null || Automation.Compare(parent, root)) break;
                cur = parent;
            }
            return IsAddressBarFocusPath(path, proc);
        }
        catch { return true; }
    }

    /// <summary>Whether a focused element, given as (class, automation id) from it up to its window, is in an address bar
    /// or in the autocomplete popup itself. A file dialog's file-name box (ComboBox 1148) is not.</summary>
    internal static bool IsAddressBarFocusPath(IEnumerable<(string? Class, string? Id)> path, string? proc) =>
        path.Any(p => IsAddressBarPart(p.Class, p.Id, proc) || string.Equals(p.Class, "Auto-Suggest Dropdown", StringComparison.OrdinalIgnoreCase));

    public const string AddressDropdownReason =
        "That is an entry of the address bar's list (its history or suggestions): choosing it enters it in the address bar, which "
        + "runs what it names, like the Run box. ";

    /// <summary>Whether an element is (inside) a terminal of another app: xterm.js (VS Code, Cursor, browser and Electron
    /// terminals) or an IDE's terminal tool window.</summary>
    public static bool IsEmbeddedTerminal(AutomationElement? e) => CommandInputKind(e, addressBar: false) != null;

    // ── starting a file from Explorer ──

    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".msi", ".msp", ".lnk", ".url", ".hta", ".cpl", ".msc", ".jar", ".reg", ".appref-ms",
        ".application", ".settingcontent-ms", ".diagcab", ".scf", ".wsc", ".ws",
        ".bat", ".cmd", ".ps1", ".psm1", ".py", ".pyw", ".js", ".mjs", ".cjs", ".jse", ".vbs", ".vbe", ".wsf", ".wsh",
        ".sh", ".bash", ".rb", ".pl", ".php", ".lua",
    };

    /// <summary>Whether opening a file of this name starts a program or a script (Enter or a double-click on it in
    /// Explorer) rather than a document.</summary>
    public static bool IsRunnableFileName(string? name)
    {
        string n = (name ?? "").Trim();
        if (n.Length == 0 || n.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return false;
        return RunnableExtensions.Contains(System.IO.Path.GetExtension(n));
    }

    // Explorer's item type ("Type" column, UIA ItemType) for programs and scripts, in English and Thai. Judged after the
    // folder check, so "Compressed (zipped) Folder" is a folder.
    [GeneratedRegex(@"application|batch|command script|script|shortcut|installer|installation package|registration entries|"
                    + @"screen saver|executable|python file|powershell|แอปพลิเคชัน|ทางลัด|สคริปต์|ชุดคำสั่ง|โปรแกรมติดตั้ง",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RunnableType();

    [GeneratedRegex(@"folder|^(local |removable )?dis[kc]\b|drive|โฟลเดอร์|ไดรฟ์", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FolderType();

    /// <summary>Whether Explorer's item type text names a program or script (true), a folder or a document (false), or
    /// is empty (null).</summary>
    public static bool? IsRunnableItemType(string? itemType)
    {
        string t = (itemType ?? "").Trim();
        if (t.Length == 0) return null;
        if (FolderType().IsMatch(t)) return false;
        return RunnableType().IsMatch(t);
    }

    // Context-menu commands that open the selection: Open, Run as administrator, Open with (and its submenu), Install,
    // Merge (a .reg file's default verb), in English and Thai.
    [GeneratedRegex(@"^(open|run|run as administrator|run as different user|open with|install|merge|"
                    + @"เปิด|เรียกใช้|เรียกใช้ในฐานะผู้ดูแลระบบ|เปิดด้วย|ติดตั้ง|ผสาน)\s*(\.\.\.|…)?$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpenCommandName();

    /// <summary>Whether a context-menu item's name is a command that opens what is selected (the ribbon's Open button).</summary>
    public static bool IsOpenCommand(string? menuItemName)
    {
        string n = (menuItemName ?? "").Split('\t')[0].Replace("&", "").Trim();
        return OpenCommandName().IsMatch(n);
    }

    // The context-menu commands known to leave the selection unopened, in English and Thai. Everything else in an Explorer
    // menu counts as opening it while a program or script is selected: verbs that run it come in too many forms to list
    // ("Run with PowerShell", "Open with Command Prompt", an .msi's Repair / Uninstall, the apps under Open with, a shell
    // extension's own). "Send to", "New" and "Compress to" are the submenu parents only — the entries under them have
    // other names. "Open in new tab / window" opens folders only; pinning, a shortcut, a zip and Extract all start nothing.
    [GeneratedRegex(@"^(copy|cut|paste|paste shortcut|rename|delete|properties|edit|show more options|open file location|open folder location|copy as path|copy path|"
                    + @"share|send to|refresh|view|sort by|group by|new|select all|select none|invert selection|"
                    + @"compress to|compress to zip file|zip file|7z file|tar file|additional options|create shortcut|extract all|"
                    + @"(un)?pin (to|from) (quick access|start|taskbar|home)|remove from quick access|(add to|remove from) favorites|"
                    + @"open in new tab|open in new window|restore previous versions|undo(\s+\w+)?|redo(\s+\w+)?|options|"
                    + @"คัดลอก|ตัด|วาง|วางทางลัด|เปลี่ยนชื่อ|ลบ|คุณสมบัติ|แก้ไข|แสดงตัวเลือกเพิ่มเติม|เปิดตำแหน่งไฟล์|เปิดตำแหน่งที่ตั้งไฟล์|"
                    + @"เปิดตำแหน่งที่ตั้งของไฟล์|คัดลอกเป็นเส้นทาง|คัดลอกเป็นพาธ|คัดลอกเส้นทาง|แชร์|ส่งไปยัง|รีเฟรช|มุมมอง|เรียงลำดับตาม|จัดกลุ่มตาม|ใหม่|สร้าง|"
                    + @"เลือกทั้งหมด|ไม่เลือกเลย|สลับการเลือก|บีบอัดเป็นไฟล์ zip|สร้างทางลัด|แยกทั้งหมด|แยกไฟล์ทั้งหมด|ปักหมุด.*|เลิกปักหมุด.*|"
                    + @"ยกเลิกการปักหมุด.*|เพิ่มในรายการโปรด|เปิดในแท็บใหม่|เปิดในหน้าต่างใหม่|คืนค่าเวอร์ชันก่อนหน้า|เลิกทำ.*|ทำซ้ำ.*|ตัวเลือก)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HarmlessMenuCommand();

    // The entries of the View / Sort by / Group by submenus (and Windows 11's View ▸ Show). Plain words ("Name", "Details")
    // that an app under Open with or a shortcut under Send to could be called, so they pass only in a menu that is
    // neither of those (see AppListMenu).
    [GeneratedRegex(@"^(extra large icons|large icons|medium icons|small icons|list|details|tiles|content|compact view|name|date|"
                    + @"date modified|date created|date taken|type|size|tags|ascending|descending|more|more options|none|\(none\)|"
                    + @"auto arrange icons|align icons to grid|show desktop icons|show|navigation pane|details pane|preview pane|"
                    + @"item check boxes|file name extensions|hidden items|"
                    + @"ไอคอนขนาดใหญ่พิเศษ|ไอคอนขนาดใหญ่|ไอคอนขนาดกลาง|ไอคอนขนาดเล็ก|รายการ|รายละเอียด|ไทล์|เนื้อหา|มุมมองแบบกะทัดรัด|"
                    + @"ชื่อ|วันที่|วันที่ปรับเปลี่ยน|วันที่สร้าง|ชนิด|ประเภท|ขนาด|จากน้อยไปหามาก|จากมากไปหาน้อย|เพิ่มเติม|ไม่มี|แสดง|"
                    + @"บานหน้าต่างนำทาง|บานหน้าต่างรายละเอียด|บานหน้าต่างแสดงตัวอย่าง|ส่วนขยายชื่อไฟล์|นามสกุลชื่อไฟล์|รายการที่ซ่อน)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ViewMenuEntry();

    // Entries only the Open with list (its "Choose another app", the Store search) and Send to (its desktop shortcut, mail
    // recipient) hold: every other entry of such a menu runs or hands over the selection.
    [GeneratedRegex(@"choose another app|microsoft store|\(create shortcut\)|mail recipient|fax recipient|เลือกแอปอื่น|\(สร้างทางลัด\)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AppListMenu();

    // Send to's zip folder. The New menu (the command bar's New flyout) holds one too ("Compressed (zipped) Folder", the
    // .zip ShellNew), so it marks Send to only beside another Send to entry: Documents (or one of AppListMenu's).
    [GeneratedRegex(@"\(zipped\)|\(บีบอัด", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZipFolderEntry();

    [GeneratedRegex(@"^(documents|เอกสาร)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SendToDocuments();

    // The commands a file's or folder's own menu holds (the classic one, Windows 11's, the ribbon's Open list): a menu with
    // none of them, nor Open or the Open with / Send to entries — a command-bar flyout (View, Sort, New), a breadcrumb or
    // history dropdown, the taskbar's menu — does not act on the selection.
    [GeneratedRegex(@"^(cut|copy|rename|delete|properties|show more options|send to|copy as path|open file location|share|print|edit|"
                    + @"ตัด|คัดลอก|เปลี่ยนชื่อ|ลบ|คุณสมบัติ|แสดงตัวเลือกเพิ่มเติม|ส่งไปยัง|คัดลอกเป็นเส้นทาง|คัดลอกเป็นพาธ|แชร์|พิมพ์|แก้ไข)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SelectionMenuCommand();

    // "(&C)" access-key markers (East Asian menus) and a shortcut shown in the name ("Copy (Ctrl+C)").
    [GeneratedRegex(@"\(&\w\)|\s*\((ctrl|alt|shift|del|delete|f\d{1,2})\b[^)]*\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MenuLabelNoise();

    /// <summary>A menu item's name without its shortcut, access-key marks and trailing ellipsis.</summary>
    private static string MenuLabel(string? menuItemName)
    {
        string n = MenuLabelNoise().Replace((menuItemName ?? "").Split('\t')[0], "").Replace("&", "").Trim();
        return n.TrimEnd('.', '…').Trim();
    }

    /// <summary>Whether a context-menu item's name is one of the commands known to leave the selection unopened (Copy,
    /// Rename, Properties, Show more options, Send to …). Unknown names are not.</summary>
    public static bool IsHarmlessMenuCommand(string? menuItemName)
    {
        string n = MenuLabel(menuItemName);
        return n.Length > 0 && HarmlessMenuCommand().IsMatch(n);
    }

    /// <summary><see cref="IsHarmlessMenuCommand(string?)"/>, plus the View / Sort by / Group by entries ("Details",
    /// "Name", "Ascending") when <paramref name="ownMenu"/> — the names in the item's own menu — is read and is a submenu
    /// of that kind: not the Open with list or Send to, where an app or a shortcut can go by such a name, nor a file's
    /// menu itself, where a shell extension's verb could.</summary>
    public static bool IsHarmlessMenuCommand(string? menuItemName, IEnumerable<string?> ownMenu)
    {
        if (IsHarmlessMenuCommand(menuItemName)) return true;
        string n = MenuLabel(menuItemName);
        if (n.Length == 0 || !ViewMenuEntry().IsMatch(n)) return false;
        var own = ownMenu.ToList();
        return own.Count > 0 && !IsSelectionMenu(own, languageKnown: true);
    }

    /// <summary>Whether a menu holding <paramref name="items"/> (with the menus it hangs under and the other menus open in
    /// Explorer) is a file's or folder's own menu, whose commands act on the selection: it holds Cut, Copy, Rename,
    /// Properties, Open, the Open with list's or Send to's entries … A menu with none of them (a command-bar flyout — New's
    /// too, whose zip folder counts only beside Send to's Documents —, a breadcrumb or history dropdown, the taskbar's) is not — but only when Windows shows its menus in a language these
    /// names are known in, or the menu is known to be the taskbar's (<paramref name="languageKnown"/>): in any other
    /// language every menu counts as one. An unreadable (empty) menu counts as one.</summary>
    internal static bool IsSelectionMenu(IReadOnlyCollection<string?> items, bool languageKnown) =>
        !languageKnown || items.Count == 0
        || items.Any(i => MenuLabel(i) is { Length: > 0 } n && (SelectionMenuCommand().IsMatch(n) || IsOpenCommand(n) || AppListMenu().IsMatch(n)))
        || (items.Any(i => ZipFolderEntry().IsMatch(MenuLabel(i))) && items.Any(i => SendToDocuments().IsMatch(MenuLabel(i))));

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern ushort GetUserDefaultUILanguage();

    /// <summary>Whether the user's Windows display language is one whose menu names the rules know (English, Thai).</summary>
    private static bool MenuLanguageKnown()
    {
        try { return (GetUserDefaultUILanguage() & 0x3FF) is 0x09 or 0x1E; }
        catch { return false; }
    }

    /// <summary>How an action may open what it lands on in Explorer.</summary>
    public enum OpenKind
    {
        /// <summary>A single click: opens nothing, unless it lands on a context-menu command other than the harmless
        /// ones (Copy, Rename, Properties …).</summary>
        Click,
        /// <summary>A double-click, an invoke or a drop: opens the item it lands on (and the whole selection when that
        /// item is part of it).</summary>
        Item,
        /// <summary>Enter (or space in a menu): opens the file list's whole selection, not only the focused item; in a
        /// context menu it chooses the focused command.</summary>
        Enter,
        /// <summary>A key pressed while a context menu is open: an access key ('o' for Open) can choose a command.</summary>
        MenuKey,
    }

    private const string ExplorerRunReason =
        "opening it from Explorer would start it without the script check. Start it with desktop_launch (or run_command), which "
        + "checks it first, or ask the user.";

    private static string RunRefusal(string name) =>
        $"\"{DesktopWindows.Trunc(name, 60)}\" is a program or script: " + ExplorerRunReason;

    private const string UnreadableSelection =
        "What is selected in Explorer could not be read, so it is not opened (it may be a program or script, and opening one from "
        + "Explorer would start it without the script check). Open a document with desktop_launch, or ask the user.";

    /// <summary>The refusal for opening a program or script from Explorer's file list or the desktop: that starts it
    /// without the script check desktop_launch makes. Judged by what the item really is — the Shell's path for it, the
    /// file on disk, or its Type — not by its display name, which hides known extensions (and always hides .lnk / .url).
    /// Enter and opening a selected item judge the whole selection. In a file's or folder's own menu (its submenus
    /// included, and a classic #32768 popup whose owner cannot be told) only the commands known to leave the selection
    /// unopened pass (Copy, Rename, Properties, Show more options, View ▸ Details …); any other command, or an access
    /// key, judges the selection of the window the menu belongs to. Other menus of explorer — the taskbar's, a command-bar
    /// flyout (View, Sort, New), a breadcrumb or history dropdown — act on no selection and pass (<see cref="IsSelectionMenu"/>).
    /// Folders, documents, selecting and single clicks on items stay allowed. When the selection cannot be read, opening
    /// is refused.</summary>
    public static string? OpensFileRefusal(AutomationElement? e, OpenKind kind = OpenKind.Item)
    {
        if (e == null) return null;
        try
        {
            if (!string.Equals(DesktopWindows.ProcessNameOf(e.Current.ProcessId), "explorer", StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch { return null; }
        try
        {
            if (InMenu(e, out var command, out var menuElement))
            {
                string label = command?.Current.Name ?? "";
                if (kind != OpenKind.MenuKey && command != null && IsHarmlessMenuCommand(label)) return null;   // Copy, Rename, Properties …
                if (kind == OpenKind.Click && command == null) return null;   // the menu's frame or a separator chooses nothing
                // A menu with none of a file's commands is a command-bar flyout, a breadcrumb or history dropdown, or the
                // taskbar's menu (it owns the popup, or is in front: a right-click on it puts it there), in any language.
                var (own, all) = MenuNamesAround(e, menuElement);
                if (!IsSelectionMenu(all, MenuLanguageKnown() || IsTaskbarMenu(e))) return null;
                if (kind != OpenKind.MenuKey && command != null && IsHarmlessMenuCommand(label, own)) return null;   // View ▸ Details …
                var owner = MenuOwnerWindow();
                string? why = owner == IntPtr.Zero ? UnreadableSelection : SelectionRefusal(owner, null);
                if (why == null || kind == OpenKind.MenuKey || command == null) return why;
                return $"The menu command \"{DesktopWindows.Trunc(label, 60)}\" is not one known to leave the selection unopened "
                       + "(Copy, Cut, Rename, Delete, Properties, Show more options …). " + why;
            }
            if (kind == OpenKind.MenuKey) return null;
            // The ribbon's Open button (Windows 10) opens the selection too.
            if (IsOpenButton(e)) return SelectionRefusal(DesktopAutomation.TopLevelOf(e) ?? IntPtr.Zero, null);
            if (kind == OpenKind.Click) return null;
            var item = ItemOf(e);
            var list = ListOf(item ?? e);
            if (item == null && (kind != OpenKind.Enter || list == null)) return null;   // not the file list
            IntPtr top = DesktopAutomation.TopLevelOf(e) ?? IntPtr.Zero;
            var view = ShellView(top);
            var folders = FoldersOf(top, view.Folders);
            if (item != null)
            {
                string name = item.Current.Name ?? "";
                bool? runs = ItemRunnable(item, folders, view.Selection);
                if (runs == true) return RunRefusal(name);
                if (runs == null) return UnreadableSelection;
                if (kind == OpenKind.Item && !IsSelected(item)) return null;
            }
            return SelectionRefusal(top, list, view, folders);
        }
        catch { return UnreadableSelection; }
    }

    private static bool IsOpenButton(AutomationElement e)
    {
        var ct = e.Current.ControlType;
        return (ct == ControlType.Button || ct == ControlType.SplitButton) && IsOpenCommand(e.Current.Name);
    }

    /// <summary>Whether <paramref name="e"/> is in a context menu (a menu item, a button of Windows 11's menu, or the menu
    /// itself), and the command it chooses: the nearest menu item, button or list item at or above it inside the menu
    /// (null on the menu's frame), and the menu element itself when it is that close (null otherwise).</summary>
    private static bool InMenu(AutomationElement e, out AutomationElement? command, out AutomationElement? menuElement)
    {
        command = null;
        menuElement = null;
        bool menu = false;
        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? cur = e;
        for (int depth = 0; cur != null && depth < 4; depth++)
        {
            var ct = cur.Current.ControlType;
            if (ct == ControlType.Menu) { menu = true; menuElement = cur; break; }   // a submenu's items stop at their own menu
            if (ct == ControlType.MenuItem) menu = true;
            if (command == null && (ct == ControlType.MenuItem || ct == ControlType.Button || ct == ControlType.SplitButton
                                    || ct == ControlType.ListItem)) command = cur;
            cur = walker.GetParent(cur);
        }
        if (!menu) command = null;
        return menu;
    }

    private static readonly string[] ExplorerViewClasses = { "CabinetWClass", "ExploreWClass", "Progman", "WorkerW" };
    private static readonly string[] TaskbarClasses = { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    /// <summary>Whether the menu <paramref name="e"/> is in belongs to the taskbar: the taskbar is the foreground window
    /// (a right-click on it makes it so), or owns the menu's popup window.</summary>
    private static bool IsTaskbarMenu(AutomationElement e)
    {
        try
        {
            var fg = DesktopWindows.Describe(DesktopNative.GetForegroundWindow());
            if (fg != null && string.Equals(fg.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase) && TaskbarClasses.Contains(fg.ClassName)) return true;
            var top = TopOf(e);
            if (top == null) return false;
            IntPtr h = new(top.Current.NativeWindowHandle);
            IntPtr owner = h == IntPtr.Zero ? IntPtr.Zero : DesktopNative.GetWindow(h, DesktopNative.GW_OWNER);
            if (owner == IntPtr.Zero) return false;
            IntPtr ownerRoot = DesktopNative.GetAncestor(owner, DesktopNative.GA_ROOT);
            return TaskbarClasses.Contains(DesktopNative.ClassName(ownerRoot != IntPtr.Zero ? ownerRoot : owner));
        }
        catch { return false; }
    }

    /// <summary>The top-level element (a child of the desktop) <paramref name="e"/> is in.</summary>
    private static AutomationElement? TopOf(AutomationElement e)
    {
        var walker = TreeWalker.ControlViewWalker;
        var root = AutomationElement.RootElement;
        AutomationElement? cur = e;
        for (int depth = 0; cur != null && depth < 40; depth++)
        {
            var parent = walker.GetParent(cur);
            if (parent == null || Automation.Compare(parent, root)) return cur;
            cur = parent;
        }
        return null;
    }

    private static readonly System.Windows.Automation.Condition MenuCommandCondition = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));

    /// <summary>The names of the commands in a menu or popup (its submenus' too when UIA puts them under it).</summary>
    private static List<string?> CommandNamesIn(AutomationElement container)
    {
        var names = new List<string?>();
        try
        {
            foreach (AutomationElement c in container.FindAll(TreeScope.Descendants, MenuCommandCondition))
            {
                try { names.Add(c.Current.Name); } catch (ElementNotAvailableException) { }
                if (names.Count >= 150) break;
            }
        }
        catch { }
        return names;
    }

    /// <summary>The command names around a menu command: <c>Own</c>, its own menu's (the menu element, else the popup
    /// window it is in — never a whole Explorer window or the taskbar); <c>All</c>, those plus the menus it hangs under
    /// (UIA puts a submenu under the item that opened it) and every other menu or popup explorer has open (a submenu
    /// in a popup of its own, the context menu a submenu came from).</summary>
    private static (List<string?> Own, List<string?> All) MenuNamesAround(AutomationElement e, AutomationElement? menu)
    {
        var own = new List<string?>();
        var all = new List<string?>();
        var root = AutomationElement.RootElement;
        var walker = TreeWalker.ControlViewWalker;
        var container = menu;
        if (container == null)
        {
            try
            {
                if (TopOf(e) is { } top && !ExplorerViewClasses.Contains(top.Current.ClassName ?? "") && !TaskbarClasses.Contains(top.Current.ClassName ?? ""))
                    container = top;
            }
            catch { }
        }
        if (container != null) own.AddRange(CommandNamesIn(container));
        all.AddRange(own);
        try
        {
            AutomationElement? cur = container == null ? null : walker.GetParent(container);
            for (int depth = 0; cur != null && depth < 12 && !Automation.Compare(cur, root); depth++)
            {
                if (cur.Current.ControlType == ControlType.Menu) all.AddRange(CommandNamesIn(cur));
                cur = walker.GetParent(cur);
            }
        }
        catch { }
        int pid;
        try { pid = e.Current.ProcessId; } catch { return (own, all); }
        var popups = new List<IntPtr>();
        DesktopNative.EnumWindows((h, _) =>
        {
            if (DesktopNative.IsWindowVisible(h) && DesktopNative.GetWindowThreadProcessId(h, out uint p) != 0 && p == (uint)pid)
            {
                string cls = DesktopNative.ClassName(h);
                if (cls == "#32768" || cls.Contains("Popup", StringComparison.OrdinalIgnoreCase)) popups.Add(h);
            }
            return popups.Count < 8;
        }, IntPtr.Zero);
        foreach (var h in popups)
        {
            try { all.AddRange(CommandNamesIn(AutomationElement.FromHandle(h))); }
            catch { }
        }
        return (own, all);
    }

    /// <summary>The Explorer window (or the desktop) a context menu belongs to: the foreground window when it is one,
    /// else the front-most Explorer window.</summary>
    private static IntPtr MenuOwnerWindow()
    {
        var fg = DesktopWindows.Describe(DesktopNative.GetForegroundWindow());
        if (fg != null && IsExplorerView(fg)) return fg.Handle;
        return DesktopWindows.List().FirstOrDefault(IsExplorerView)?.Handle ?? IntPtr.Zero;
    }

    private static bool IsExplorerView(DesktopWindow w) =>
        string.Equals(w.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase) && ExplorerViewClasses.Contains(w.ClassName);

    /// <summary>The list item (or data item) <paramref name="e"/> is or sits in, a few levels up at most.</summary>
    private static AutomationElement? ItemOf(AutomationElement e)
    {
        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? cur = e;
        for (int depth = 0; cur != null && depth < 4; depth++)
        {
            var ct = cur.Current.ControlType;
            if (ct == ControlType.ListItem || ct == ControlType.DataItem) return cur;
            if (ct == ControlType.List || ct == ControlType.DataGrid || ct == ControlType.Window) return null;
            cur = walker.GetParent(cur);
        }
        return null;
    }

    /// <summary>The file list (an element with the Selection pattern) <paramref name="e"/> is or sits in.</summary>
    private static AutomationElement? ListOf(AutomationElement e)
    {
        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? cur = e;
        for (int depth = 0; cur != null && depth < 5; depth++)
        {
            var ct = cur.Current.ControlType;
            if ((ct == ControlType.List || ct == ControlType.DataGrid)
                && cur.GetCurrentPropertyValue(AutomationElement.IsSelectionPatternAvailableProperty) is true) return cur;
            if (ct == ControlType.Window) return null;
            cur = walker.GetParent(cur);
        }
        return null;
    }

    private static bool IsSelected(AutomationElement item)
    {
        try { return item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p) && ((SelectionItemPattern)p).Current.IsSelected; }
        catch { return false; }
    }

    /// <summary>An item's type text: UIA ItemType, else its Type column (System.ItemTypeText) in the details view.</summary>
    private static string ItemTypeOf(AutomationElement item)
    {
        string t = item.Current.ItemType ?? "";
        if (t.Trim().Length > 0) return t;
        var walker = TreeWalker.ControlViewWalker;
        int n = 0;
        for (var c = walker.GetFirstChild(item); c != null && n < 16; c = walker.GetNextSibling(c), n++)
        {
            if (!string.Equals(c.Current.AutomationId, "System.ItemTypeText", StringComparison.Ordinal)) continue;
            if (c.TryGetCurrentPattern(ValuePattern.Pattern, out var v) && ((ValuePattern)v).Current.Value is { Length: > 0 } val) return val;
            return c.Current.Name ?? "";
        }
        return "";
    }

    /// <summary>Whether a Shell item name as Explorer shows it (extension hidden or not) is the file at <paramref name="path"/>.</summary>
    private static bool ShowsAs(string path, string displayName)
    {
        string file = System.IO.Path.GetFileName(path);
        return string.Equals(file, displayName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(System.IO.Path.GetFileNameWithoutExtension(file), displayName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathRuns(string path, bool isFolder) => !isFolder && IsRunnableFileName(System.IO.Path.GetFileName(path));

    /// <summary>Whether opening an item of the file list starts a program or script: true, false (a folder or a
    /// document), null (cannot tell). The Shell's real path first, then the file on disk in the window's folder (a
    /// hidden extension matched as name.*), then the Type, then an extension the name shows.</summary>
    private static bool? ItemRunnable(AutomationElement item, IReadOnlyList<string> folders, List<(string Path, bool IsFolder)>? selection)
    {
        string name = (item.Current.Name ?? "").Trim();
        if (selection != null && SelectedItemRuns(name, IsSelected(item), selection) is { } bySelection) return bySelection;
        string type = ItemTypeOf(item);
        bool? byType = IsRunnableItemType(type);
        if (byType == true) return true;
        if (FolderType().IsMatch(type)) return false;   // a folder named "chart.js" is still a folder
        // The type text is localized ("Fichier de commandes Windows" is a .bat): the file on disk decides the rest.
        if (DiskItemRuns(name, folders) is { } onDisk) return onDisk;
        if (byType == false) return IsRunnableFileName(name);
        return IsRunnableFileName(name) ? true : null;
    }

    /// <summary>What the Shell's selection says about an item shown as <paramref name="name"/>: true / false, or null when
    /// it cannot tell. Only an item that is itself selected, matched by exactly one selected path — with extensions
    /// hidden setup.py and setup.cfg both show as "setup", and the selected one need not be this row. Several selected
    /// files under the name: a program among them counts, else null.</summary>
    internal static bool? SelectedItemRuns(string name, bool itemSelected, IEnumerable<(string Path, bool IsFolder)> selection)
    {
        if (!itemSelected) return null;
        var mine = selection.Where(s => ShowsAs(s.Path, name)).ToList();
        if (mine.Count == 1) return PathRuns(mine[0].Path, mine[0].IsFolder);
        return mine.Any(s => PathRuns(s.Path, s.IsFolder)) ? true : null;
    }

    /// <summary>What the disk says about an item shown as <paramref name="name"/> in one of <paramref name="folders"/>:
    /// true when the file of that name, or any name.* file (a hidden extension), is a program or script; false when
    /// only documents or a folder go by that name; null when nothing does.</summary>
    internal static bool? DiskItemRuns(string name, IReadOnlyList<string> folders)
    {
        if (name.Length == 0 || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return null;
        foreach (var folder in folders)
        {
            string exact = System.IO.Path.Combine(folder, name);
            var files = new List<string>();
            if (System.IO.File.Exists(exact)) files.Add(exact);
            try
            {
                files.AddRange(System.IO.Directory.EnumerateFiles(folder, name + ".*")
                    .Where(f => string.Equals(System.IO.Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase))
                    .Take(32));
            }
            catch { }
            // Two files can show as one name ("deploy" for deploy.bat and deploy.txt): any program among them counts.
            if (files.Any(f => IsRunnableFileName(System.IO.Path.GetFileName(f)))) return true;
            if (files.Count > 0 || System.IO.Directory.Exists(exact)) return false;
        }
        return null;
    }

    /// <summary>The refusal for opening the selection of an Explorer window (or the desktop), or null when nothing selected
    /// starts a program or script.</summary>
    private static string? SelectionRefusal(IntPtr window, AutomationElement? list,
        (List<string> Folders, List<(string Path, bool IsFolder)>? Selection)? known = null, IReadOnlyList<string>? folders = null)
    {
        var view = known ?? ShellView(window);
        folders ??= FoldersOf(window, view.Folders);
        if (view.Selection != null)
        {
            string? run = view.Selection.Where(s => PathRuns(s.Path, s.IsFolder)).Select(s => s.Path).FirstOrDefault();
            return run != null ? RunRefusal(System.IO.Path.GetFileName(run)) : null;
        }
        // No Shell view (the desktop, or the Shell did not answer): the list's own selection, item by item.
        if (list == null && window != IntPtr.Zero)
            list = AutomationElement.FromHandle(window)?.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List),
                new PropertyCondition(AutomationElement.IsSelectionPatternAvailableProperty, true)));
        if (list == null || !list.TryGetCurrentPattern(SelectionPattern.Pattern, out var sp)) return UnreadableSelection;
        bool unknown = false;
        foreach (var it in ((SelectionPattern)sp).Current.GetSelection().Take(2000))
        {
            bool? runs = ItemRunnable(it, folders, null);
            if (runs == true) return RunRefusal(it.Current.Name ?? "");
            if (runs == null) unknown = true;
        }
        return unknown ? UnreadableSelection : null;
    }

    /// <summary>The folders an Explorer window shows: the Shell's paths for its tabs, or the desktop folders for the
    /// desktop.</summary>
    private static IReadOnlyList<string> FoldersOf(IntPtr window, List<string> shellFolders)
    {
        var list = new List<string>(shellFolders.Where(f => f.Length > 0 && System.IO.Directory.Exists(f)));
        string cls = window != IntPtr.Zero ? DesktopNative.ClassName(window) : "";
        if (cls is "Progman" or "WorkerW")
            foreach (var sf in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
            {
                string d = Environment.GetFolderPath(sf);
                if (d.Length > 0 && System.IO.Directory.Exists(d)) list.Add(d);
            }
        return list;
    }

    /// <summary>What the Shell says about an Explorer window (ShellWindows): the folder of each of its tabs and the
    /// selected items' real paths across them. Selection is null when it could not be read (the desktop is not in
    /// ShellWindows, or the Shell did not answer).</summary>
    private static (List<string> Folders, List<(string Path, bool IsFolder)>? Selection) ShellView(IntPtr window)
    {
        var folders = new List<string>();
        if (window == IntPtr.Zero) return (folders, null);
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return (folders, null);
            shell = Activator.CreateInstance(type);
            if (shell == null) return (folders, null);
            dynamic windows = ((dynamic)shell).Windows();
            int count = (int)windows.Count;
            List<(string, bool)>? selection = null;
            for (int i = 0; i < count && i < 64; i++)
            {
                dynamic? w;
                long hwnd;
                try
                {
                    w = windows.Item(i);
                    if (w == null) continue;
                    hwnd = Convert.ToInt64((object)w.HWND);
                }
                catch { continue; }
                if (hwnd != window.ToInt64()) continue;
                // A tabbed Explorer lists each tab with the same window handle: all of them count.
                dynamic doc = w!.Document;
                try { folders.Add((string)doc.Folder.Self.Path); } catch { }
                dynamic items = doc.SelectedItems();
                int n = (int)items.Count;
                selection ??= new List<(string, bool)>();
                for (int k = 0; k < n && k < 5000; k++)
                {
                    dynamic item = items.Item(k);
                    selection.Add(((string)item.Path, (bool)item.IsFolder));
                }
            }
            return (folders, selection);
        }
        catch { return (folders, null); }
        finally
        {
            if (shell != null) try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } catch { }
        }
    }

    // ── dismissing a Run box ──

    /// <summary>A control that only dismisses a dialog: Cancel (Win32 IDCANCEL, automation id 2) or the title bar's
    /// Close. Dismissing the Run box runs nothing (owner 2026-11-02: it must still be closable).</summary>
    public static bool IsDismissControl(string? automationId, string? name) =>
        automationId is "2" or "Close"
        || (name ?? "").Trim().ToLowerInvariant() is "cancel" or "close" or "ยกเลิก" or "ปิด";

    /// <summary>Whether <paramref name="e"/> only dismisses its dialog (see <see cref="IsDismissControl"/>).</summary>
    public static bool IsDismiss(AutomationElement? e)
    {
        if (e == null) return false;
        try { return IsDismissControl(e.Current.AutomationId, e.Current.Name); }
        catch { return false; }
    }

    /// <summary>Keys that only dismiss a dialog: every chord is Esc or alt+F4.</summary>
    public static bool IsDismissKeys(string? keys)
    {
        var tokens = (keys ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0 && tokens.All(t => t.Trim().ToLowerInvariant() is "esc" or "escape" or "alt+f4");
    }

    /// <summary>ctrl+Esc: it opens Start, like the Windows key, and Start runs what is typed into it.</summary>
    public static bool OpensStart(DesktopChord c) =>
        c.Key == 0x1B && c.Modifiers.Contains(DesktopInput.VK_CONTROL) && !c.Modifiers.Contains(DesktopInput.VK_SHIFT) && !c.Modifiers.Contains(DesktopInput.VK_MENU);

    /// <summary>Whether a chord pressed in an Explorer context menu can choose a command by itself: a printable key (an
    /// access key: 'o' for Open, also with alt) or a paste. Enter and space choose the focused command and are judged by
    /// it; Esc, the arrows, Tab and alt+F4 only move or close.</summary>
    public static bool IsMenuAccessKey(DesktopChord c) =>
        c.Key != 0x20 && (c.PutsText || (DesktopInput.IsPrintable(c.Key) && c.Modifiers.Contains(DesktopInput.VK_MENU)));

    /// <summary>A chord that only moves the focus out of the address bar (Esc, Tab, F6): allowed there, so a model whose
    /// focus landed in it is not stuck. Enter, the arrows (its history list), text and paste are not.</summary>
    public static bool IsLeaveChord(string? token) =>
        (token ?? "").Trim().ToLowerInvariant() is "esc" or "escape" or "tab" or "shift+tab" or "f6" or "shift+f6";

    public const string CommandSurfaceReason =
        "Keys, typing and clicks there would run commands without CluadeX's command guard and approval prompt, so the desktop tools "
        + "only look at it (desktop_screenshot, desktop_read and scrolling still work). Run the command with run_command instead — "
        + "it goes through the guard — or ask the user to do this part.";

    /// <summary>The refusal for input into <paramref name="w"/> (or into <paramref name="focus"/> inside it), or null.
    /// <paramref name="dismissing"/>: the input only closes the window (Esc, Cancel, Close) — allowed for a Run box and
    /// Windows Search / Start, never for a terminal.</summary>
    public static string? CommandSurfaceRefusal(DesktopWindow? w, AutomationElement? focus = null, bool dismissing = false)
    {
        if (w != null && IsTerminalWindow(w.ProcessName, w.ClassName))
            return $"\"{DesktopWindows.Trunc(w.Title, 60)}\" ({w.ProcessName}) is a terminal or a run-a-command box. " + CommandSurfaceReason;
        if (w != null && !dismissing && IsLauncherWindow(w.ProcessName))
            return $"\"{DesktopWindows.Trunc(w.Title, 60)}\" ({w.ProcessName}) is Windows Search / Start (or a launcher like it): what is typed "
                   + "or chosen there opens files and runs command lines without the script check. Start a program or open a file with "
                   + "desktop_launch instead (it checks it first), or ask the user. desktop_screenshot and desktop_read still work, and "
                   + "closing it (Esc, desktop_window close) is allowed.";
        if (w != null && !dismissing && IsRunBox(w))
            return $"\"{DesktopWindows.Trunc(w.Title, 60)}\" ({w.ProcessName}) is a terminal or a run-a-command box. " + CommandSurfaceReason
                   + " Closing it (desktop_window close, Esc, or its Cancel button) is allowed.";
        if (CommandInputKind(focus) is { } kind)
            return $"The focus is in {kind}{(w != null ? $" of {w.ProcessName}" : "")}. " + CommandSurfaceReason;
        return null;
    }

    /// <summary>The top-level window under a physical screen point (WindowFromPoint, then its root).</summary>
    public static DesktopWindow? WindowAtPoint(int x, int y)
    {
        var h = DesktopNative.WindowFromPoint(new DesktopNative.POINT { X = x, Y = y });
        if (h == IntPtr.Zero) return null;
        var root = DesktopNative.GetAncestor(h, DesktopNative.GA_ROOT);
        return DesktopWindows.Describe(root != IntPtr.Zero ? root : h);
    }

    /// <summary>The refusal for a real click (or a drop) at a screen point, judged by what is really under it: the window
    /// the click lands on may not be the one the model meant (an always-on-top terminal, an activation Windows refused).
    /// Refused: a terminal or Run box under the point (except the Run box's Cancel / Close), a terminal panel inside an
    /// app, a right / middle click into an address bar (paste, "Paste and go"), an entry of the address bar's dropdown,
    /// and opening a program or script in Explorer (a double-click or drop on it, or a click on a context-menu command
    /// such as Open). <paramref name="button"/> is "left", "right" or "middle"; <paramref name="opens"/> is a
    /// double-click or a drop.</summary>
    public static string? PointRefusal(int x, int y, AutomationElement? hit, string button, bool opens)
    {
        var w = WindowAtPoint(x, y);
        bool paste = button is "right" or "middle";
        bool dismiss = !paste && !opens && IsDismiss(hit);
        if (CommandSurfaceRefusal(w, null, dismiss) is { } cs) return cs;
        if (CommandInputKind(hit, addressBar: paste) is { } kind)
            return $"That point is on {kind}{(w != null ? $" of {w.ProcessName}" : "")}. " + CommandSurfaceReason;
        if (!paste && IsAddressDropdownItem(hit)) return AddressDropdownReason + CommandSurfaceReason;
        if (!paste && TerminalSendClickRefusal(hit) is { } sends) return sends;
        if (OpensFileRefusal(hit, opens ? OpenKind.Item : OpenKind.Click) is { } run) return run;
        return null;
    }

    // ── editor commands that run text in the editor's terminal ──

    // The full labels of palette commands that send text to a terminal or REPL, or run code there: "Terminal: Run
    // Selected Text In Active Terminal", "Run Active File", "Run Recent Command", "Python: Run Selection/Line in Python
    // Terminal", "Run Python File in Terminal", "Jupyter: Run Current File in Interactive Window", "Tasks: Run Build
    // Task", Code Runner's "Run Code", "Send Custom Sequence", notebook "Execute Cell". Also starting a launch / run
    // configuration, which F5 does (launch.json's preLaunchTask, a node-terminal "command"): "Debug: Start Debugging",
    // "Start Without Debugging", "Run Without Debugging", "Select and Start Debugging", the Run and Debug view's "Run and
    // Debug" button, JetBrains' "Run 'app'" / "Debug 'app'" and its "Run..." / "Debug..." popups. And the other ways to
    // run the project's own code in one click: the Python extension's "Python Debugger: Debug Python File" / "Debug using
    // launch.json", the Testing view's and CodeLens' "Run Test" / "Debug Test" / "Run All Tests" (Go's "run package
    // tests", rust-analyzer's "Run Doctest", a benchmark), "Test: Rerun Last Run", npm's "Run Script". Opening or creating
    // a terminal is not among them (typing into one is refused on its own), nor opening the Run and Debug view, "Go to
    // Test" or the Debug Console. A bare "Run" / "Debug" (the NPM Scripts view's buttons, CodeLens) is BareRunLabel's.
    [GeneratedRegex(@"\brun\s+(the\s+)?(selected|selection|active|recent|current|python)\b|\b(run|execute)\s+(the\s+)?(current\s+)?(selection|line)\b"
                    + @"|selection\s*/\s*line|\b(run|execute|send)\b.*\bin\s+(the\s+)?(\w+\s+)?terminal\b|\brun\s+code\b"
                    + @"|\b(re)?run\s+((build|test|last)\s+)?task\b|send\s*(custom\s*)?sequence|\b(execute|run)\s+(all\s+)?cells?\b"
                    + @"|\bstart\s+(without\s+)?debugging\b|\brun\s+without\s+debugging\b|^\s*run\s+and\s+debug\s*$"
                    + @"|^\s*(run|debug|profile)\s+'[^']+'|^\s*(run|debug)\s*(\.\.\.|…)\s*$"
                    + @"|\bdebug\s+((the\s+)?(python|current|active)\s+)?file\b|\bdebug\s+using\s+launch\.json\b"
                    + @"|\b(re)?run\s+(\w+\s+)?((doc)?tests?|bench(mark)?s?)\b|\bdebug\s+(\w+\s+)?((doc)?tests?|bench(mark)?s?)\b"
                    + @"|\brerun\s+last\s+run\b|\brun\s+(npm\s+)?script\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TerminalSendCommand();

    // A button, link or menu entry called just "Run" or "Debug" (a play glyph before it): the NPM Scripts view's inline
    // buttons and its context menu, CodeLens ("▶︎ Run | Debug" from rust-analyzer, Jest, Go). Not in NamesTerminalSend: the
    // menu bar's "Run" only opens the Run menu.
    [GeneratedRegex(@"^[\s\u25B6\u25B7\u25BA\uFE0E\uFE0F]*(run|debug)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareRunLabel();

    // What a model types into the palette to reach those commands: the palette matches fuzzily, so prefixes and
    // run-together words select them too ("run sel", "runselectedtext", "selected text active terminal", "debug start").
    [GeneratedRegex(@"\brun\s*(the\s*)?(sel|act|rec|cur|py|code|task|build|cell|test)|selected\s*text|active\s*file|selection\s*/?\s*line"
                    + @"|send\s*(custom\s*)?seq|in\s*(active\s*|python\s*)?terminal|execute\s*(sel|line|cell)"
                    + @"|start\s*(without\s*)?debug|run\s*without\s*debug|debug\s*:?\s*start|select\s*and\s*start"
                    + @"|\bdebug\s*(the\s*)?(py|test|cur|act|using)|\b(run|debug)\s*all\s*test",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TerminalSendTyped();

    // VS Code's quick-access prefixes for tasks: "task deploy" in quick open lists the tasks themselves (Enter runs one),
    // ">task" the Tasks: commands. Only in VS Code and its forks: "task service" in JetBrains' Search Everywhere finds
    // the TaskService class.
    [GeneratedRegex(@"^\s*task\s|^\s*>\s*tasks?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TaskQuickAccess();

    // VS Code's "debug " quick-access prefix: quick open lists the launch configurations, and Enter starts one.
    [GeneratedRegex(@"^\s*debug\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LaunchQuickAccess();

    // The task picker's accessible name (its placeholder).
    [GeneratedRegex(@"\btasks?\s+to\s+(run|restart)\b|\bselect\s+the\s+(build|test)\s+task\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TaskPickerName();

    // Quick open's accessible name (its placeholder): "Search files by name (append : to go to line or @ to go to symbol)".
    [GeneratedRegex(@"\bsearch\s+files\s+by\s+name\b|\bgo\s+to\s+file\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuickOpenName();

    /// <summary>Whether VS Code's quick-access prefixes ("task ", "debug ") mean anything in this app: VS Code and its
    /// forks. A null process name is judged as one of them.</summary>
    private static bool HasQuickAccessPrefixes(string? processName) => processName == null || CodeEditorProcesses.Contains(processName);

    /// <summary>Whether what is typed into a palette can pick a command, a task or a launch configuration. VS Code's quick
    /// input also serves as a plain InputBox (Git: Create Branch, a rename prompt …): it shows no list of rows, so nothing
    /// typed there selects a command and Enter only hands the text to whoever asked. It picks when it shows a list
    /// (<paramref name="showsList"/>) or its input is quick open's. Other apps' palettes (JetBrains' Search Everywhere,
    /// whose list may not be readable) always pick.</summary>
    internal static bool PalettePicks(string? processName, bool showsList, string? inputName) =>
        !HasQuickAccessPrefixes(processName) || showsList || QuickOpenName().IsMatch(inputName ?? "");

    /// <summary>Whether VS Code's quick input <paramref name="palette"/> shows a list (a quick pick) rather than being a
    /// plain InputBox, whose list is hidden. True when it cannot be read.</summary>
    private static bool PaletteShowsList(AutomationElement palette)
    {
        try
        {
            var list = new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List),
                                       new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            return palette.FindFirst(TreeScope.Descendants, list) != null;
        }
        catch { return true; }
    }

    /// <summary>Whether a palette input is VS Code's task picker: its text starts with the "task " quick-access prefix
    /// (or ">task"), or its name is the picker's placeholder ("Select the task to run"). Enter there runs a task — any
    /// shell command .vscode/tasks.json defines, whatever the row is called.</summary>
    public static bool IsTaskPicker(string? typed, string? inputName) => IsTaskPicker(typed, inputName, null);

    /// <summary><see cref="IsTaskPicker(string?, string?)"/> in the app <paramref name="processName"/>: only VS Code and its
    /// forks have one (null is judged as one of them).</summary>
    public static bool IsTaskPicker(string? typed, string? inputName, string? processName) =>
        HasQuickAccessPrefixes(processName) && (TaskQuickAccess().IsMatch(typed ?? "") || TaskPickerName().IsMatch(inputName ?? ""));

    /// <summary>Whether a palette input is VS Code's launch-configuration picker (the "debug " quick-access prefix): Enter
    /// there starts a configuration of .vscode/launch.json, whatever its row is called. Only in VS Code and its forks
    /// (null is judged as one of them).</summary>
    public static bool IsLaunchPicker(string? typed, string? processName = null) =>
        HasQuickAccessPrefixes(processName) && LaunchQuickAccess().IsMatch(typed ?? "");

    /// <summary>Whether a palette command's label is one that sends text to the editor's terminal or runs code there.</summary>
    public static bool NamesTerminalSend(string? label) => TerminalSendCommand().IsMatch(label ?? "");

    /// <summary>Whether a button, link or menu entry of <paramref name="processName"/> called just "Run" or "Debug" runs
    /// code: in VS Code and its forks (the NPM Scripts view, CodeLens), unless it only opens a menu
    /// (<paramref name="inMenuBar"/>: the menu bar's "Run", a submenu's parent).</summary>
    public static bool NamesViewRunButton(string? processName, string? label, bool inMenuBar) =>
        !inMenuBar && CodeEditorProcesses.Contains(processName ?? "") && BareRunLabel().IsMatch(label ?? "");

    /// <summary>Whether menu item <paramref name="e"/> opens a menu rather than running a command: it is in a menu bar, or
    /// it expands (a submenu's parent). False for anything else, and when it cannot be read.</summary>
    private static bool OpensMenu(AutomationElement e, TreeWalker walker)
    {
        try
        {
            if (e.Current.ControlType != ControlType.MenuItem) return false;
            if ((e.Current.ClassName ?? "").Contains("menubar", StringComparison.OrdinalIgnoreCase)) return true;
            if (walker.GetParent(e) is { } p && p.Current.ControlType == ControlType.MenuBar) return true;
            return e.GetCurrentPropertyValue(AutomationElement.IsExpandCollapsePatternAvailableProperty) is true;
        }
        catch { return false; }
    }

    /// <summary>Whether text typed into a command palette would select such a command (prefixes included), judged as in
    /// VS Code (its "task " / "debug " prefixes count).</summary>
    public static bool TypedNamesTerminalSend(string? typed) => TypedNamesTerminalSend(typed, null);

    /// <summary><see cref="TypedNamesTerminalSend(string?)"/> in the app <paramref name="processName"/>: the "task " and
    /// "debug " quick-access prefixes count only in VS Code and its forks.</summary>
    public static bool TypedNamesTerminalSend(string? typed, string? processName)
    {
        string t = typed ?? "";
        return TerminalSendCommand().IsMatch(t) || TerminalSendTyped().IsMatch(t)
               || (HasQuickAccessPrefixes(processName) && (TaskQuickAccess().IsMatch(t) || LaunchQuickAccess().IsMatch(t)));
    }

    private const string TerminalSendReason =
        "It sends text to the editor's terminal (or runs code there), which runs it while the focus stays in the editor, so the "
        + "focus check never sees a terminal. ";

    /// <summary>The command palette <paramref name="e"/> sits in — VS Code's quick input (and its forks'), JetBrains'
    /// Search Everywhere — or null. Only in apps that host a terminal (<see cref="IsTerminalHost"/>).</summary>
    public static AutomationElement? PaletteOf(AutomationElement? e)
    {
        if (e == null) return null;
        string proc;
        try { proc = DesktopWindows.ProcessNameOf(e.Current.ProcessId); }
        catch { return null; }
        if (!IsTerminalHost(proc)) return null;
        bool ide = IdeProcesses.Contains(proc);
        var walker = TreeWalker.RawViewWalker;
        var root = AutomationElement.RootElement;
        AutomationElement? cur = e;
        try
        {
            for (int depth = 0; cur != null && depth < 30; depth++)
            {
                string cls = cur.Current.ClassName ?? "";
                if (cls.Contains("quick-input-widget", StringComparison.OrdinalIgnoreCase)) return cur;
                if (ide && (cls.Contains("SearchEverywhere", StringComparison.OrdinalIgnoreCase)
                            || (cur.Current.Name ?? "").Contains("Search Everywhere", StringComparison.OrdinalIgnoreCase))) return cur;
                var parent = walker.GetParent(cur);
                if (parent == null || Automation.Compare(parent, root)) break;
                cur = parent;
            }
        }
        catch { }
        return null;
    }

    /// <summary>The label of the palette's highlighted row (VS Code marks it "monaco-list-row focused"; elsewhere the
    /// selected list item), or null when none can be read.</summary>
    public static string? PaletteRow(AutomationElement palette)
    {
        var walker = TreeWalker.RawViewWalker;
        var stack = new Stack<(AutomationElement E, int Depth)>();
        stack.Push((palette, 0));
        string? selected = null;
        int seen = 0;
        while (stack.Count > 0 && seen < 800)
        {
            var (cur, depth) = stack.Pop();
            seen++;
            try
            {
                string cls = " " + (cur.Current.ClassName ?? "") + " ";
                if (cls.Contains("monaco-list-row", StringComparison.OrdinalIgnoreCase) && cls.Contains(" focused ", StringComparison.OrdinalIgnoreCase))
                    return cur.Current.Name ?? "";
                if (selected == null && cur.Current.ControlType == ControlType.ListItem
                    && cur.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp) && ((SelectionItemPattern)sp).Current.IsSelected)
                    selected = cur.Current.Name ?? "";
                if (depth >= 18) continue;
                var kids = new List<AutomationElement>();
                for (var c = walker.GetFirstChild(cur); c != null && kids.Count < 200; c = walker.GetNextSibling(c)) kids.Add(c);
                for (int i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], depth + 1));
            }
            catch (ElementNotAvailableException) { }
        }
        return selected;
    }

    /// <summary>The palette's input box — <paramref name="focus"/> when it takes a value, else the first edit box in the
    /// palette — as (its text, its accessible name). Empty strings when it cannot be read.</summary>
    private static (string Typed, string Name) PaletteInput(AutomationElement palette, AutomationElement? focus)
    {
        try
        {
            var input = focus != null && focus.GetCurrentPropertyValue(AutomationElement.IsValuePatternAvailableProperty) is true
                ? focus
                : palette.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (input == null) return ("", "");
            string name = input.Current.Name ?? "", typed = "";
            if (input.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) typed = ((ValuePattern)vp).Current.Value ?? "";
            return (typed, name);
        }
        catch { return ("", ""); }
    }

    private static string ProcessOf(AutomationElement e)
    {
        try { return DesktopWindows.ProcessNameOf(e.Current.ProcessId); }
        catch { return ""; }
    }

    /// <summary>The refusal for typing <paramref name="typed"/> into an editor's command palette when it names a command
    /// that sends text to the terminal (prefixes count: the palette matches fuzzily). Judged on its own and, when
    /// <paramref name="appends"/>, after what the palette's input already holds: "tas" then "k " is "task " (desktop_type's
    /// Value pattern and keys both add to the end). Text typed anywhere else — prose in Word, code in an editor — is not
    /// judged.</summary>
    public static string? TerminalSendRefusal(string? typed, AutomationElement? focus, bool appends = true)
    {
        if (string.IsNullOrEmpty(typed)) return null;
        var palette = PaletteOf(focus);
        if (palette == null) return null;
        string proc = ProcessOf(palette);
        var (before, inputName) = PaletteInput(palette, focus);
        if (!PalettePicks(proc, !HasQuickAccessPrefixes(proc) || PaletteShowsList(palette), inputName)) return null;   // a plain InputBox
        bool names = TypedNamesTerminalSend(typed, proc);
        if (!names && appends) names = before.Length > 0 && TypedNamesTerminalSend(before + typed, proc);
        return names
            ? "That text in the editor's command palette selects a command that sends text to its terminal (or runs a task or a launch "
              + "configuration). " + TerminalSendReason + CommandSurfaceReason
            : null;
    }

    /// <summary>The rule for running a row of an editor's command palette — Enter, or a click / invoke on the row — given
    /// the palette's input text and name and the row's label (null when it cannot be read). In VS Code's task picker
    /// ("task " or its placeholder) and launch picker ("debug ") every row runs project-defined commands, whatever it is
    /// called; elsewhere the row's label decides, and the input's text when there is no row. A plain InputBox
    /// (<paramref name="showsList"/> false, see <see cref="PalettePicks"/>) has no row to run: its text answers a prompt.</summary>
    internal static string? PaletteRunRefusal(string? processName, string? typed, string? inputName, string? row, bool showsList = true)
    {
        if (!PalettePicks(processName, showsList, inputName)) return null;
        // The task picker's rows are named after the tasks ("deploy, Workspace"), so the row cannot tell: the picker can.
        if (IsTaskPicker(typed, inputName, processName))
            return "The command palette is the task picker: running a row runs a task, which is any shell command in .vscode/tasks.json. "
                   + TerminalSendReason + CommandSurfaceReason;
        if (IsLaunchPicker(typed, processName))
            return "The command palette is the launch-configuration picker (\"debug \"): running a row starts a configuration of "
                   + ".vscode/launch.json, which can run any command (a preLaunchTask, a node-terminal command). " + TerminalSendReason + CommandSurfaceReason;
        if (row != null)
            return NamesTerminalSend(row)
                ? $"The palette command is \"{DesktopWindows.Trunc(row, 80)}\". " + TerminalSendReason + CommandSurfaceReason
                : null;
        return TypedNamesTerminalSend(typed, processName)
            ? "The command palette's text selects a command that sends text to its terminal. " + TerminalSendReason + CommandSurfaceReason
            : null;
    }

    /// <summary>The refusal for Enter (or submit) in an editor's command palette: judged by the row that Enter would run,
    /// whatever was typed (or pasted) to reach it (<see cref="PaletteRunRefusal"/>). When the row cannot be read, the
    /// palette's text is judged instead.</summary>
    public static string? PaletteEnterRefusal(AutomationElement? focus)
    {
        var palette = PaletteOf(focus);
        if (palette == null) return null;
        string proc = ProcessOf(palette);
        var (typed, inputName) = PaletteInput(palette, focus);
        bool showsList = !HasQuickAccessPrefixes(proc) || PaletteShowsList(palette);
        if (!PalettePicks(proc, showsList, inputName)) return null;   // a plain InputBox: Enter answers its prompt
        string? row = null;
        if (!IsTaskPicker(typed, inputName, proc) && !IsLaunchPicker(typed, proc))
        {
            try { row = PaletteRow(palette); }
            catch { }
        }
        return PaletteRunRefusal(proc, typed, inputName, row, showsList);
    }

    /// <summary>The refusal for a click (or invoke) on a palette row or a menu item that sends text to the editor's
    /// terminal or runs code there ("Terminal ▸ Run Selected Text", "Run ▸ Start Debugging", a row of the command
    /// palette). A row of the palette is judged like Enter on it (<see cref="PaletteRunRefusal"/>): in the task or launch
    /// picker any row runs. Only in apps that host a terminal.</summary>
    public static string? TerminalSendClickRefusal(AutomationElement? hit)
    {
        if (hit == null) return null;
        try
        {
            string proc = ProcessOf(hit);
            if (!IsTerminalHost(proc)) return null;
            var walker = TreeWalker.RawViewWalker;
            AutomationElement? cur = hit, firstRow = null;
            string hitName = "";
            for (int depth = 0; cur != null && depth < 8; depth++)
            {
                var ct = cur.Current.ControlType;
                string cls = cur.Current.ClassName ?? "";
                bool row = cls.Contains("monaco-list-row", StringComparison.OrdinalIgnoreCase) || cls.Contains("action-menu-item", StringComparison.OrdinalIgnoreCase)
                           || ct == ControlType.MenuItem || ct == ControlType.ListItem || ct == ControlType.Button || ct == ControlType.DataItem;
                string name = cur.Current.Name ?? "";
                if (depth == 0) hitName = name;
                if (row && NamesTerminalSend(name))
                    return $"\"{DesktopWindows.Trunc(name, 80)}\" is an editor command. " + TerminalSendReason + CommandSurfaceReason;
                // A CodeLens: its text (the hit) sits in a codelens decoration, as plain text or a link.
                if (cls.Contains("codelens", StringComparison.OrdinalIgnoreCase)
                    && new[] { hitName, name }.FirstOrDefault(n => NamesTerminalSend(n) || NamesViewRunButton(proc, n, inMenuBar: false)) is { } lens)
                    return $"\"{DesktopWindows.Trunc(lens, 80)}\" is a CodeLens that runs or debugs the code above it. " + TerminalSendReason + CommandSurfaceReason;
                // A bare "Run" / "Debug" button or menu entry (the NPM Scripts view's), not one that only opens a menu (the menu bar's Run).
                bool button = ct == ControlType.Button || ct == ControlType.Hyperlink || ct == ControlType.MenuItem
                              || cls.Contains("action-label", StringComparison.OrdinalIgnoreCase) || cls.Contains("action-menu-item", StringComparison.OrdinalIgnoreCase);
                if (button && NamesViewRunButton(proc, name, inMenuBar: OpensMenu(cur, walker)))
                    return $"\"{DesktopWindows.Trunc(name, 80)}\" runs or debugs the project's code (an npm script, a test, a run configuration). "
                           + TerminalSendReason + CommandSurfaceReason;
                if (row && firstRow == null) firstRow = cur;
                cur = walker.GetParent(cur);
            }
            // A row of the command palette: what is typed there can make every row a task or a launch configuration.
            if (firstRow != null && PaletteOf(hit) is { } palette)
            {
                var (typed, inputName) = PaletteInput(palette, null);
                return PaletteRunRefusal(proc, typed, inputName, firstRow.Current.Name ?? "");
            }
        }
        catch { }
        return null;
    }

    /// <summary>The refusal for a chord an editor binds to running code: the current line or selection in its terminal or
    /// REPL (shift / ctrl / alt+Enter in VS Code's Python, Jupyter and notebook editors, the PowerShell extension's F8 —
    /// while an editor has the focus — and alt+shift+E in JetBrains' Python console), or a task, build or launch
    /// configuration wherever the focus is (see <see cref="RunChordRefusal"/>). No typed text names them.</summary>
    public static string? RunKeyRefusal(AutomationElement? focus, DesktopChord c)
    {
        if (focus == null || c.Key is not (0x0D or 0x77 or 0x45 or 0x42 or 0x74 or 0x78 or 0x79)) return null;   // no run key: skip the lookup
        string proc;
        try { proc = DesktopWindows.ProcessNameOf(focus.Current.ProcessId); }
        catch { return null; }
        return RunChordRefusal(proc, c, () => InCodeEditor(focus));
    }

    /// <summary>The rule behind <see cref="RunKeyRefusal"/>, given the focused app's process name. Besides the run-line
    /// keys: ctrl+shift+B (VS Code's default build task, Visual Studio's Build Solution with its build events), F5 /
    /// ctrl+F5 / ctrl+shift+F5 (start or restart a launch configuration) in VS Code and Visual Studio — and in Rider, whose
    /// Visual Studio keymap (the one its setup offers on Windows) binds them the same way; not in the other JetBrains IDEs,
    /// where F5 is Copy — and in JetBrains IDEs shift+F10 / ctrl+shift+F10 (run), shift+F9 / ctrl+shift+F9 (debug),
    /// alt+shift+F10 / alt+shift+F9 (the Run / Debug popup), ctrl+F5 (rerun) and ctrl+F9 (Build Project). Each runs commands
    /// the project's files define (tasks.json, launch.json, a .csproj, a run configuration), which the model can write.
    /// <paramref name="inCodeEditor"/> is asked only for the run-line keys.</summary>
    public static string? RunChordRefusal(string? processName, DesktopChord c, Func<bool> inCodeEditor)
    {
        string proc = processName ?? "";
        bool shift = c.Modifiers.Contains(DesktopInput.VK_SHIFT), ctrl = c.Modifiers.Contains(DesktopInput.VK_CONTROL), alt = c.Modifiers.Contains(DesktopInput.VK_MENU);
        bool code = CodeEditorProcesses.Contains(proc), vs = proc.Equals("devenv", StringComparison.OrdinalIgnoreCase);
        bool jet = IdeProcesses.Contains(proc) && !vs;
        bool vsKeys = vs || proc.Equals("rider", StringComparison.OrdinalIgnoreCase) || proc.Equals("rider64", StringComparison.OrdinalIgnoreCase);   // Rider's VS keymap
        bool lineKey = code && ((c.Key == 0x0D && (shift || ctrl || alt)) || (c.Key == 0x77 && !shift && !ctrl && !alt));   // Enter with a modifier, F8
        bool jetLine = jet && c.Key == 0x45 && alt && shift && !ctrl;                                                       // alt+shift+E
        if (jetLine || (lineKey && inCodeEditor()))
            return $"In {proc} \"{c.Text}\" runs the current line or selection in the editor's terminal or REPL. " + TerminalSendReason
                   + "Run code with run_command instead, or ask the user.";
        bool build = (code || vsKeys) && c.Key == 0x42 && ctrl && shift && !alt;                             // ctrl+shift+B
        bool launch = (code || vsKeys) && c.Key == 0x74 && !alt && (ctrl || !shift);                         // F5, ctrl+F5, ctrl+shift+F5 (not shift+F5: stop)
        // JetBrains: (ctrl+)shift+F9/F10 debug / run, alt+shift+F9/F10 the Debug / Run popup (Enter there starts the highlighted
        // configuration), ctrl+F5 rerun, ctrl+F9 Build Project (Gradle / Maven scripts, like Visual Studio's build events).
        bool jetRun = jet && ((shift && c.Key is 0x78 or 0x79) || (ctrl && !shift && !alt && c.Key is 0x74 or 0x78));
        if (build || launch || jetRun)
            return $"In {proc} \"{c.Text}\" runs a task, a build or a launch / run configuration: commands the project's files define "
                   + "(tasks.json, launch.json, build events, run configurations), which run without CluadeX's command guard and approval "
                   + "prompt. Run it with run_command instead, or ask the user.";
        return null;
    }

    /// <summary>Whether <paramref name="e"/> is in a VS Code editor or notebook (where the run-line keys apply).</summary>
    private static bool InCodeEditor(AutomationElement e)
    {
        var walker = TreeWalker.RawViewWalker;
        var root = AutomationElement.RootElement;
        AutomationElement? cur = e;
        try
        {
            for (int depth = 0; cur != null && depth < 30; depth++)
            {
                string cls = cur.Current.ClassName ?? "";
                if (cls.Contains("monaco-editor", StringComparison.OrdinalIgnoreCase) || cls.Contains("notebook", StringComparison.OrdinalIgnoreCase)) return true;
                var parent = walker.GetParent(cur);
                if (parent == null || Automation.Compare(parent, root)) break;
                cur = parent;
            }
        }
        catch { }
        return false;
    }

    // ── desktop_launch ──

    private static readonly HashSet<string> CommandRunners = new(StringComparer.OrdinalIgnoreCase)
    {
        "wscript", "cscript", "mshta", "rundll32", "regsvr32", "msiexec", "schtasks", "wmic", "forfiles", "certutil", "bitsadmin",
        "python", "python3", "pythonw", "py", "pyw", "node", "deno", "bun", "npm", "npx", "pnpm", "yarn", "ruby", "perl", "php",
        "java", "javaw", "dotnet", "msbuild", "installutil", "git", "make",
    };

    /// <summary>Whether starting <paramref name="path"/> runs a command line rather than opening an app. Such a launch is
    /// approved per command line, like run_command: one approval of `reg query …` (or of an app) must not cover a later
    /// `reg delete …`. That is any launch with arguments — whatever the program, its arguments are what it acts on (an
    /// AUMID like shell:AppsFolder\…WindowsTerminal…!App included) — and, without arguments, a shell or terminal, a
    /// script host or interpreter, a script file, or any console program (it opens an interactive prompt at least).
    /// A GUI app started with no arguments keeps the per-app approval. <paramref name="cwd"/> helps find the program.</summary>
    public static bool LaunchesCommands(string? path, string? args, string? cwd = null)
    {
        string p = (path ?? "").Trim().Trim('"');
        if (p.Length == 0) return false;
        if (!string.IsNullOrWhiteSpace(args)) return true;
        string ext;
        try { ext = System.IO.Path.GetExtension(p); }
        catch (ArgumentException) { return true; }
        if (ext.Length > 0 && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) && RunnableExtensions.Contains(ext)) return true;
        string name = System.IO.Path.GetFileNameWithoutExtension(p);
        if (TerminalProcesses.Contains(name) || CommandRunners.Contains(name) || name.Contains("bash", StringComparison.OrdinalIgnoreCase)) return true;
        return ResolveProgram(p, cwd) is { } exe && IsConsoleProgram(exe);
    }

    /// <summary>Where Windows would find <paramref name="program"/>: as given, in <paramref name="cwd"/>, or on PATH
    /// (".exe" / ".com" added when it has no extension). Null for shell: / URL targets and programs not found.</summary>
    private static string? ResolveProgram(string program, string? cwd)
    {
        try
        {
            if (program.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || program.Contains("://")) return null;
            var names = System.IO.Path.HasExtension(program) ? new[] { program } : new[] { program + ".exe", program + ".com", program };
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var n in names)
            {
                if (System.IO.Path.IsPathRooted(n))
                {
                    if (System.IO.File.Exists(n)) return n;
                    continue;
                }
                if (!string.IsNullOrEmpty(cwd) && System.IO.File.Exists(System.IO.Path.Combine(cwd, n))) return System.IO.Path.Combine(cwd, n);
                foreach (var d in dirs)
                {
                    string c;
                    try { c = System.IO.Path.Combine(d.Trim().Trim('"'), n); }
                    catch (ArgumentException) { continue; }
                    if (System.IO.File.Exists(c)) return c;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Whether an executable is a console program (PE subsystem IMAGE_SUBSYSTEM_WINDOWS_CUI): reg, sc, netsh,
    /// ssh, diskpart … and every shell. False when it is a GUI program or cannot be read.</summary>
    public static bool IsConsoleProgram(string path)
    {
        try
        {
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            using var br = new System.IO.BinaryReader(fs);
            if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D) return false;   // "MZ"
            fs.Position = 0x3C;
            long pe = br.ReadUInt32();
            if (pe + 4 + 20 + 70 > fs.Length) return false;
            fs.Position = pe;
            if (br.ReadUInt32() != 0x00004550) return false;                    // "PE\0\0"
            fs.Position = pe + 4 + 20 + 68;   // the optional header's Subsystem: the same offset in PE32 and PE32+
            return br.ReadUInt16() == 3;
        }
        catch { return false; }
    }

    [GeneratedRegex(@"2559a1f3-21d7-11d4-bdaf-00c04f60b9f0|shell32(\.dll)?\s*,\s*#61\b|RunFileDlg", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RunBoxLaunch();

    /// <summary>The refusal for a launch that opens the Run box (its shell folder, or shell32's RunFileDlg): Win+R is
    /// refused, and so is this back door to it.</summary>
    public static string? RunBoxLaunchRefusal(string? commandLine) =>
        RunBoxLaunch().IsMatch(commandLine ?? "")
            ? "That opens the Run box, which runs whatever is typed into it. " + CommandSurfaceReason
            : null;
}
