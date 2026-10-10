using System.Diagnostics;
using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace CluadeX.Services.Browser;

/// <summary>What the machine has for the built-in browser: the WebView2 runtime, and installed Edge / Chrome.</summary>
public sealed record BrowserEngineInfo
{
    /// <summary>The WebView2 runtime version CluadeX would load ("154.0.4258.62"), or null when none is installed.</summary>
    public string? WebView2Version { get; init; }
    public string? EdgePath { get; init; }
    public string? EdgeVersion { get; init; }
    public string? ChromePath { get; init; }
    public string? ChromeVersion { get; init; }
    /// <summary>Why the WebView2 runtime could not be used (for the Debug Log; never shown raw).</summary>
    public string? ProbeError { get; init; }
    public DateTime ProbedUtc { get; init; } = DateTime.UtcNow;

    public bool HasWebView2 => !string.IsNullOrEmpty(WebView2Version);

    /// <summary>One line for the Features page: "WebView2 154.0.4258.62 · Edge 154.0 · Chrome 154.0".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (HasWebView2) parts.Add("WebView2 " + WebView2Version);
            if (EdgePath != null) parts.Add("Edge" + (EdgeVersion != null ? " " + EdgeVersion : ""));
            if (ChromePath != null) parts.Add("Chrome" + (ChromeVersion != null ? " " + ChromeVersion : ""));
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Finds the browser engines the built-in browser can use, without starting any of them. The WebView2 runtime is
/// looked up the way Microsoft documents it — the per-machine key (32-bit view on 64-bit Windows), the native
/// per-machine key and the per-user key — and confirmed with GetAvailableBrowserVersionString, whose
/// WebView2RuntimeNotFoundException means "not installed" (Windows 10 LTSC, Server, stripped images).
/// When nothing is there, the refusal and the Features row show <see cref="InstallCommand"/> — never a dead button.
/// </summary>
public static class BrowserEngineProbe
{
    /// <summary>The one command that installs the runtime.</summary>
    public const string InstallCommand = "winget install Microsoft.EdgeWebView2Runtime";
    public const string InstallUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    private const string RuntimeClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private static readonly object Gate = new();
    private static BrowserEngineInfo? _cached;

    /// <summary>How long a "not installed" result is trusted before the next caller probes again — so the runtime the
    /// user installs with <see cref="InstallCommand"/> is found without restarting CluadeX.</summary>
    public static readonly TimeSpan MissingRecheck = TimeSpan.FromSeconds(10);

    /// <summary>The last probe (cached for the process; <paramref name="refresh"/> probes again). A "missing" result
    /// older than <see cref="MissingRecheck"/> is probed again automatically. Never throws.</summary>
    public static BrowserEngineInfo Probe(bool refresh = false)
    {
        lock (Gate)
        {
            if (_cached != null && !refresh
                && (_cached.HasWebView2 || DateTime.UtcNow - _cached.ProbedUtc < MissingRecheck)) return _cached;
            _cached = ProbeCore();
            return _cached;
        }
    }

    /// <summary>Harness hook: replace the cached result (e.g. an old "missing" one).</summary>
    internal static void SetCachedForTest(BrowserEngineInfo? info)
    {
        lock (Gate) _cached = info;
    }

    private static BrowserEngineInfo ProbeCore()
    {
        string? version = null, error = null;
        try
        {
            version = LoaderVersion();
        }
        catch (Exception ex) when (ex.GetType().Name == "WebView2RuntimeNotFoundException") { error = "WebView2 runtime not found: " + ex.Message; }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }   // loader DLL / assembly missing, policy, …

        // The registry is the documented second opinion — and the only one when the loader DLL itself is absent.
        string? registry = RegistryRuntimeVersion();
        if (string.IsNullOrEmpty(version) && !string.IsNullOrEmpty(registry) && error == null) version = registry;

        var (edge, edgeVer) = FindBrowser(
            @"Microsoft\Edge\Application\msedge.exe",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
        var (chrome, chromeVer) = FindBrowser(
            @"Google\Chrome\Application\chrome.exe",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");

        return new BrowserEngineInfo
        {
            WebView2Version = string.IsNullOrWhiteSpace(version) ? null : version,
            EdgePath = edge, EdgeVersion = edgeVer,
            ChromePath = chrome, ChromeVersion = chromeVer,
            ProbeError = error,
        };
    }

    /// <summary>Kept in its own method so a missing WebView2 assembly fails HERE (caught by the caller) and not
    /// while the caller itself is being compiled.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string? LoaderVersion() => CoreWebView2Environment.GetAvailableBrowserVersionString();

    /// <summary>"pv" under the three documented EdgeUpdate client keys; "0.0.0.0" or empty = not installed.</summary>
    internal static string? RegistryRuntimeVersion()
    {
        var places = new (RegistryHive Hive, RegistryView View, string Path)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry32, $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{RuntimeClientId}"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{RuntimeClientId}"),
            (RegistryHive.CurrentUser, RegistryView.Default, $@"Software\Microsoft\EdgeUpdate\Clients\{RuntimeClientId}"),
        };
        foreach (var (hive, view, path) in places)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(path);
                if (key?.GetValue("pv") is string pv && !string.IsNullOrWhiteSpace(pv) && pv != "0.0.0.0") return pv;
            }
            catch { /* no access = not this one */ }
        }
        return null;
    }

    private static (string? Path, string? Version) FindBrowser(string relative, string appPathsKey)
    {
        var candidates = new List<string>();
        foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles", "LOCALAPPDATA" })
        {
            string? root = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(root)) candidates.Add(Path.Combine(root, relative));
        }
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(appPathsKey);
                if (key?.GetValue(null) is string p && !string.IsNullOrWhiteSpace(p)) candidates.Add(p.Trim('"'));
            }
            catch { }
        }
        foreach (var c in candidates)
        {
            try
            {
                if (!File.Exists(c)) continue;
                string? ver = FileVersionInfo.GetVersionInfo(c).ProductVersion;
                return (c, string.IsNullOrWhiteSpace(ver) ? null : ver);
            }
            catch { }
        }
        return (null, null);
    }
}
