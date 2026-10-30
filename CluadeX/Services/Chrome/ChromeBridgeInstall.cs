using System.IO;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace CluadeX.Services.Chrome;

/// <summary>
/// What CluadeX puts on this PC for its own Chrome connection: the extension's files (a folder the user loads once
/// with "Load unpacked") and the native-messaging host registration (per user, no admin rights), which names
/// CluadeX.exe itself. Written again at every start, so an update or a moved portable copy can never leave Chrome
/// pointing at a program that is gone.
/// </summary>
public static class ChromeBridgeInstall
{
    /// <summary>The native-messaging host name; the extension connects to it by this name.</summary>
    public const string HostName = "com.xmanstudio.cluadex_chrome";

    /// <summary>Follows from the "key" in the extension's manifest.json: the same on every PC.</summary>
    public const string ExtensionId = "dcgjpajonelpldhmhealmiipjgkliglm";

    private const string ResourcePrefix = "ChromeExtension/";
    private const string HostsKey = @"Software\Google\Chrome\NativeMessagingHosts\";

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
            using var key = Registry.CurrentUser.OpenSubKey(HostsKey + HostName);
            return key?.GetValue(null) is string path && File.Exists(path);
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
            ["description"] = "CluadeX Chrome connection",
            ["path"] = hostExe,
            ["type"] = "stdio",
            ["allowed_origins"] = new[] { $"chrome-extension://{ExtensionId}/" },
        }, new JsonSerializerOptions { WriteIndented = true }));

        using var key = Registry.CurrentUser.CreateSubKey(HostsKey + HostName);
        key.SetValue(null, manifest);
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
    public static void Unregister() => Registry.CurrentUser.DeleteSubKey(HostsKey + HostName, throwOnMissingSubKey: false);

    private static void WriteIfChanged(string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return;
        File.WriteAllBytes(path, content);
    }
}
