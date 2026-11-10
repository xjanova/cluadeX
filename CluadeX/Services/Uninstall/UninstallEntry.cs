using System.IO;
using System.Xml.Linq;
using Microsoft.Win32;

namespace CluadeX.Services.Uninstall;

/// <summary>
/// The "CluadeX" row in Windows' Installed apps. Velopack writes it once, at install
/// (HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\&lt;package id&gt;), with an UninstallString that runs
/// <c>Update.exe --uninstall</c>: no window, no choices, and the user's data left behind in ~/.cluadex. An update does
/// NOT rewrite the row (measured 2026-11-08: DisplayVersion still 3.0.152 under an installed 3.0.271), so the app points
/// UninstallString at its own uninstall page on every start, and the page hands over to Update.exe when it is done.
/// QuietUninstallString stays Velopack's (a silent uninstall — winget, IT scripts — removes the program and keeps data).
/// </summary>
public static class UninstallEntry
{
    /// <summary>The flag that opens the uninstall page (Program.Main).</summary>
    public const string Flag = "--uninstall";

    private const string UninstallKeyBase = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";

    /// <summary>The Velopack layout this process runs from, or null for a developer / portable build.</summary>
    public sealed record Install(string Root, string CurrentDir, string PackageId, string AppExe, string UpdateExe)
    {
        public string KeyPath => UninstallKeyBase + PackageId;
        /// <summary>What Windows runs for "Uninstall": this app's page.</summary>
        public string PageCommand => $"\"{AppExe}\" {Flag}";
        /// <summary>What Velopack wrote: its own silent remover.</summary>
        public string VelopackCommand => $"\"{UpdateExe}\" --uninstall";
    }

    /// <summary>…\&lt;root&gt;\current\CluadeX.exe with sq.version beside it and Update.exe one level up.</summary>
    public static Install? Detect(string? exePath = null)
    {
        try
        {
            exePath ??= Environment.ProcessPath;
            string? current = Path.GetDirectoryName(exePath);
            if (current == null || !string.Equals(Path.GetFileName(current), "current", StringComparison.OrdinalIgnoreCase)) return null;
            string? root = Path.GetDirectoryName(current);
            if (root == null) return null;
            string update = Path.Combine(root, "Update.exe");
            string version = Path.Combine(current, "sq.version");
            if (!File.Exists(update) || !File.Exists(version)) return null;
            string? id = ReadPackageId(version);
            if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains('\\')) return null;
            return new Install(root, current, id, exePath!, update);
        }
        catch { return null; }
    }

    /// <summary>The &lt;id&gt; in Velopack's sq.version (a nuspec): the name of the Uninstall key.</summary>
    public static string? ReadPackageId(string sqVersionPath)
    {
        try
        {
            var doc = XDocument.Load(sqVersionPath);
            return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "id")?.Value.Trim();
        }
        catch { return null; }
    }

    /// <summary>
    /// Point the Installed-apps row at the uninstall page. Only a row that is really this install's (its
    /// InstallLocation is this root) is touched; nothing is created when Velopack did not write one. Returns what it
    /// did, for the log.
    /// </summary>
    public static string EnsurePointsAtPage(Install install)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(install.KeyPath, writable: true);
            if (key == null) return "no Installed-apps row (not installed by Setup.exe)";
            if (key.GetValue("InstallLocation") is not string loc || !SamePath(loc, install.Root))
                return "the Installed-apps row belongs to another copy — left alone";
            if (string.Equals(key.GetValue("UninstallString") as string, install.PageCommand, StringComparison.OrdinalIgnoreCase))
                return "already points at the uninstall page";
            key.SetValue("UninstallString", install.PageCommand, RegistryValueKind.String);
            return "now points at the uninstall page";
        }
        catch (Exception ex) { return "could not update the Installed-apps row: " + ex.Message; }
    }

    /// <summary>Put Velopack's own command back (the page could not open, so Windows' button must still work).</summary>
    public static void RestoreVelopackCommand(Install install)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(install.KeyPath, writable: true);
            if (key?.GetValue("InstallLocation") is string loc && SamePath(loc, install.Root))
                key.SetValue("UninstallString", install.VelopackCommand, RegistryValueKind.String);
        }
        catch { }
    }

    internal static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
