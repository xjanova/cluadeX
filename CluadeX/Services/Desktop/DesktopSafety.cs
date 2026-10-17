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
}
