using System.Diagnostics;
using CluadeX.Services.Browser;

namespace CluadeX.Services.Chrome;

/// <summary>
/// Switches CluadeX's own Chrome connection on and off: setting <c>BrowserEngine</c> to "chrome" sends the browser_*
/// tools to the user's Chrome. Works with any model, with no Claude account and no claude.exe. On, the extension's
/// files and the native host are put in place and the pipe opens; the one step Chrome keeps for the user is "Load
/// unpacked", once (Chrome 137 removed --load-extension, and Windows takes no extension from outside the Web Store).
/// </summary>
public sealed class ChromeBridgeConnector : IAppShutdownHook
{
    private readonly SettingsService _settings;
    private readonly ChromeBridgeServer _bridge;

    public ChromeBridgeConnector(SettingsService settings, ChromeBridgeServer bridge)
    {
        _settings = settings;
        _bridge = bridge;
    }

    public ChromeBridgeServer Bridge => _bridge;
    public bool Enabled => BrowserService.IsChromeEngine(_settings.Settings.BrowserEngine);
    public string ExtensionFolder => ChromeBridgeInstall.ExtensionFolder(_settings.DataRoot);
    public bool ChromeFound => BrowserEngineProbe.Probe().ChromePath != null;

    /// <summary>On: install and open the pipe, and only then send the tools to Chrome. Off: back to the built-in
    /// browser, with the agent's window closed and the host removed.</summary>
    public void SetEnabled(bool on)
    {
        if (on) Open();
        _settings.UpdateSettings(s => s.BrowserEngine = on ? BrowserService.ChromeEngine : "auto");
        if (on) return;
        _bridge.Post("close");   // a lost connection only detaches: the extension keeps the window for the user
        _bridge.Stop();
        ChromeBridgeInstall.Uninstall(_settings.DataRoot);
    }

    /// <summary>At every start while it is on: the files and the host's path are written again, so an update or a
    /// moved portable copy heals itself. Throws when the data folder or the registry refuses.</summary>
    public void Ensure()
    {
        if (Enabled) Open();
    }

    private void Open()
    {
        ChromeBridgeInstall.Install(_settings.DataRoot, Environment.ProcessPath ?? throw new InvalidOperationException("No path for this program."));
        _bridge.ExpectedVersion = ChromeBridgeInstall.BundledVersion;
        _bridge.Start();
    }

    /// <summary>Open Chrome's extensions page, where the user turns on Developer mode and loads <see cref="ExtensionFolder"/>.
    /// False when Chrome is not on this PC.</summary>
    public bool OpenExtensionsPage()
    {
        string? chrome = BrowserEngineProbe.Probe().ChromePath;
        if (chrome == null) return false;
        Process.Start(new ProcessStartInfo(chrome, "chrome://extensions") { UseShellExecute = false })?.Dispose();
        return true;
    }

    public void OnAppShutdown() => _bridge.Stop();
}
