using System.IO;

namespace CluadeX.Services.Setup;

/// <summary>
/// The desktop shortcut the setup wizard's "สร้างทางลัดบนเดสก์ท็อป" box controls. Velopack already puts one on the
/// desktop and one in Start when it installs; this only makes sure the desktop one is there (or removes it when the box
/// was cleared). Its target is Velopack's launcher (%LOCALAPPDATA%\CluadeX\CluadeX.exe), which survives updates — never
/// the versioned …\current\ exe. A developer or portable build has no such launcher, so nothing is touched.
/// </summary>
public static class DesktopShortcut
{
    public static string LinkPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "CluadeX.lnk");

    /// <summary>The installed launcher, or null for a developer / portable build.</summary>
    public static string? InstalledLauncher()
    {
        if (AutoUpdateService.CurrentBuildKind != AutoUpdateService.BuildKind.Installed) return null;
        try
        {
            string? current = Path.GetDirectoryName(Environment.ProcessPath);            // …\CluadeX\current
            string? root = current != null ? Path.GetDirectoryName(current) : null;    // …\CluadeX
            string launcher = root != null ? Path.Combine(root, "CluadeX.exe") : "";
            return File.Exists(launcher) ? launcher : null;
        }
        catch { return null; }
    }

    public static bool Exists() { try { return File.Exists(LinkPath); } catch { return false; } }

    /// <summary>Create (or repair) the shortcut pointing at <paramref name="target"/>. Throws on a COM / IO failure.</summary>
    public static void Create(string target)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic link = shell.CreateShortcut(LinkPath);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.IconLocation = target + ",0";
            link.Description = "CluadeX";
            link.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    /// <summary>Remove the shortcut; true when one was there.</summary>
    public static bool Remove()
    {
        try { if (!File.Exists(LinkPath)) return false; File.Delete(LinkPath); return true; }
        catch { return false; }
    }
}
