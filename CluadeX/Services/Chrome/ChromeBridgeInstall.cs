using System.IO;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace CluadeX.Services.Chrome;

/// <summary>
/// What CluadeX puts on this PC for its own Chrome / Edge connection: the extension's files (a folder the user loads
/// once with "Load unpacked") and the native-messaging host registration (per user, no admin rights), which names
/// CluadeX.exe itself. Written again at every start, so an update or a moved portable copy can never leave the browser
/// pointing at a program that is gone. Edge is Chromium: the same folder, the same extension id (it follows from the
/// manifest's "key") and the same <c>chrome-extension://</c> origin, with its own registry key for the host.
/// </summary>
public static class ChromeBridgeInstall
{
    /// <summary>The native-messaging host name; the extension connects to it by this name.</summary>
    public const string HostName = "com.xmanstudio.cluadex_chrome";

    /// <summary>Follows from the "key" in the extension's manifest.json: the same on every PC.</summary>
    public const string ExtensionId = "dcgjpajonelpldhmhealmiipjgkliglm";

    private const string ResourcePrefix = "ChromeExtension/";
    private const string ChromeHostsKey = @"Software\Google\Chrome\NativeMessagingHosts\";
    private const string EdgeHostsKey = @"Software\Microsoft\Edge\NativeMessagingHosts\";

    /// <summary>Every per-user key (HKCU) the host is registered under, Chrome's first. Edge also reads Chrome's key
    /// when its own is missing, but only its own is certain on a PC that never had Chrome.</summary>
    public static IReadOnlyList<string> HostKeys { get; } = new[] { ChromeHostsKey + HostName, EdgeHostsKey + HostName };

    /// <summary>Per Windows user: the extension of one user can never reach the CluadeX of another.</summary>
    public static string PipeName =>
        "cluadex-chrome-" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

    public static string ExtensionFolder(string dataRoot) => Path.Combine(dataRoot, "chrome", "extension");

    private static string HostManifestPath(string dataRoot) => Path.Combine(dataRoot, "chrome", HostName + ".json");

    /// <summary>The version in the extension CluadeX carries, or "" when it cannot be read. Raise it in manifest.json
    /// with every change to the extension: Chrome keeps running the old code until a different version reloads it.</summary>
    public static string BundledVersion
    {
        get
        {
            try
            {
                using var manifest = typeof(ChromeBridgeInstall).Assembly.GetManifestResourceStream(ResourcePrefix + "manifest.json");
                if (manifest == null) return "";
                using var doc = JsonDocument.Parse(manifest);
                return doc.RootElement.GetProperty("version").GetString() ?? "";
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { return ""; }
        }
    }

    /// <summary>The host is registered for this user and its manifest is on disk.</summary>
    public static bool IsRegistered
    {
        get
        {
            foreach (string name in HostKeys)
            {
                using var key = Registry.CurrentUser.OpenSubKey(name);
                if (key?.GetValue(null) is string path && File.Exists(path)) return true;
            }
            return false;
        }
    }

    /// <summary>Write the extension's files and register the host, leaving unchanged files alone (Chrome holds the
    /// folder open). Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the data
    /// folder or the registry refuses.</summary>
    public static void Install(string dataRoot, string hostExe)
    {
        string folder = ExtensionFolder(dataRoot);
        Directory.CreateDirectory(folder);
        var assembly = typeof(ChromeBridgeInstall).Assembly;
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var source = assembly.GetManifestResourceStream(name)!;
            using var bytes = new MemoryStream();
            source.CopyTo(bytes);
            WriteIfChanged(Path.Combine(folder, name[ResourcePrefix.Length..]), bytes.ToArray());
        }

        string manifest = HostManifestPath(dataRoot);
        WriteIfChanged(manifest, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["name"] = HostName,
            ["description"] = "CluadeX Chrome / Edge connection",
            ["path"] = hostExe,
            ["type"] = "stdio",
            ["allowed_origins"] = new[] { $"chrome-extension://{ExtensionId}/" },
        }, new JsonSerializerOptions { WriteIndented = true }));

        foreach (string name in HostKeys)
        {
            using var key = Registry.CurrentUser.CreateSubKey(name);
            key.SetValue(null, manifest);
        }
    }

    /// <summary>Remove the host: the extension then finds nothing to connect to. Its files stay, because Chrome still
    /// lists the folder until the user removes the extension there.</summary>
    public static void Uninstall(string dataRoot)
    {
        Unregister();
        try { File.Delete(HostManifestPath(dataRoot)); }
        catch (IOException) { /* a leftover file names nothing once the key is gone */ }
    }

    /// <summary>The registry half of <see cref="Uninstall"/> (the app's own uninstaller has no data folder to hand).</summary>
    public static void Unregister()
    {
        foreach (string name in HostKeys) Registry.CurrentUser.DeleteSubKey(name, throwOnMissingSubKey: false);
    }

    /// <summary>
    /// The registry half for one install being removed (Velopack's uninstall hook): only when the host names a program
    /// inside <paramref name="installRoot"/>, or a program / manifest that no longer exists. The key is per user and
    /// shared by every copy on the PC, so removing a second copy (a test install, an old one) must not cut the Chrome
    /// connection of the copy still in use. Returns true when the key was removed.
    /// </summary>
    public static bool UnregisterIfFor(string installRoot)
    {
        bool removed = false;
        foreach (string name in HostKeys) removed |= UnregisterIfFor(name, installRoot);
        return removed;
    }

    private static bool UnregisterIfFor(string hostKey, string installRoot)
    {
        try
        {
            string? manifest;
            using (var key = Registry.CurrentUser.OpenSubKey(hostKey))
            {
                if (key == null) return false;
                manifest = key.GetValue(null) as string;
            }
            bool dead = string.IsNullOrWhiteSpace(manifest) || !File.Exists(manifest);
            if (!dead)
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest!));
                string? exe = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() : null;
                dead = string.IsNullOrWhiteSpace(exe) || !File.Exists(exe);
                if (!dead && !Safety.ProtectedPaths.IsInside(Path.GetFullPath(exe!), installRoot)) return false;
            }
            Registry.CurrentUser.DeleteSubKey(hostKey, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    private static void WriteIfChanged(string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return;
        File.WriteAllBytes(path, content);
    }
}
