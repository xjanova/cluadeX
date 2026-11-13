using System.Diagnostics;
using CluadeX.Services.Browser;

namespace CluadeX.Services.Chrome;

/// <summary>
/// Switches CluadeX's own Chrome / Edge connection on and off: setting <c>BrowserEngine</c> to "chrome" sends the
/// browser_* tools to the user's Chrome or Edge (whichever has the extension and connects first — the pipe takes one).
/// Works with any model, with no Claude account and no claude.exe. On, the extension's files and the native host are
/// put in place and the pipe opens; the one step the browser keeps for the user is "Load unpacked", once (Chrome 137
/// removed --load-extension, and Windows takes no extension from outside the store).
/// </summary>
public sealed class ChromeBridgeConnector : IAppShutdownHook
{
    private readonly SettingsService _settings;
    private readonly ChromeBridgeServer _bridge;

    public ChromeBridgeConnector(SettingsService settings, ChromeBridgeServer bridge)
    {
        _settings = settings;
        _bridge = bridge;
        _bridge.AcceptBrowser = AcceptHello;
    }

    // ─── which browser, when both have the extension (BridgeBrowserChoice) ───

    private DateTime _preferUntilUtc = DateTime.MinValue;
    private int _refusedInWindow;

    /// <summary>The browser the owner chose for the agent: "" (the first to connect), "chrome" or "edge".</summary>
    public string Preferred => BridgeBrowserChoice.Normalize(_settings.Settings.BridgeBrowser);

    /// <summary>The browser connected now ("chrome" / "edge"), or "" when none is.</summary>
    public string Connected => _bridge.IsConnected ? BridgeBrowserChoice.Of(_bridge.UserAgent) : "";

    /// <summary>A browser was chosen, and the one connected is the other: the chosen one did not come (closed, or the
    /// extension is not in it yet).</summary>
    public bool PreferredNotConnected => Preferred != BridgeBrowserChoice.Any && _bridge.IsConnected && Connected != Preferred;

    private bool AcceptHello(string? userAgent)
    {
        bool welcome = BridgeBrowserChoice.Accept(Preferred, userAgent, DateTime.UtcNow, _preferUntilUtc, _refusedInWindow);
        if (!welcome) Interlocked.Increment(ref _refusedInWindow);
        return welcome;
    }

    private void OpenWindow()
    {
        Interlocked.Exchange(ref _refusedInWindow, 0);
        _preferUntilUtc = DateTime.UtcNow + BridgeBrowserChoice.Window;
    }

    /// <summary>The owner chose a browser ("chrome", "edge", or "" for the first to connect): kept, and acted on at once —
    /// when the other one is connected it lets go, and for a short while only the chosen one is let in.</summary>
    public void Use(string browser)
    {
        string wanted = BridgeBrowserChoice.Normalize(browser);
        _settings.UpdateSettings(s => s.BridgeBrowser = wanted);
        if (wanted == BridgeBrowserChoice.Any || !Enabled) return;
        OpenWindow();
        if (_bridge.IsConnected && Connected != wanted)
        {
            _bridge.Post("close");   // the agent's window in the browser it leaves
            _bridge.DropConnection();
        }
    }

    public ChromeBridgeServer Bridge => _bridge;
    public bool Enabled => BrowserService.IsChromeEngine(_settings.Settings.BrowserEngine);
    public string ExtensionFolder => ChromeBridgeInstall.ExtensionFolder(_settings.DataRoot);
    public bool ChromeFound => BrowserEngineProbe.Probe().ChromePath != null;
    public bool EdgeFound => BrowserEngineProbe.Probe().EdgePath != null;

    /// <summary>The connected extension runs in Microsoft Edge (its user agent carries "Edg/"), not Chrome.</summary>
    public bool ConnectedIsEdge => _bridge.IsConnected && IsEdgeUserAgent(_bridge.UserAgent);

    /// <summary>Edge names itself with an "Edg/" token after Chrome's; Chrome never has it.</summary>
    public static bool IsEdgeUserAgent(string? userAgent) =>
        userAgent != null && userAgent.Contains(" Edg/", StringComparison.Ordinal);

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
        if (Enabled && !SettingsService.IsTrialCopy) Open();
    }

    private void Open()
    {
        // A trial copy must not take Chrome's host registration (one per Windows user) away from the installed CluadeX.
        if (SettingsService.IsTrialCopy) throw new InvalidOperationException("A trial copy does not connect Chrome — the installed CluadeX keeps that connection.");
        ChromeBridgeInstall.Install(_settings.DataRoot, Environment.ProcessPath ?? throw new InvalidOperationException("No path for this program."));
        _bridge.ExpectedVersion = ChromeBridgeInstall.BundledVersion;
        OpenWindow();   // at the start the chosen browser goes first, whichever says hello first
        _bridge.Start();
    }

    /// <summary>Open the browser's extensions page (Chrome's, or Edge's with <paramref name="edge"/>), where the user
    /// turns on Developer mode and loads <see cref="ExtensionFolder"/>. False when that browser is not on this PC.</summary>
    public bool OpenExtensionsPage(bool edge = false)
    {
        var found = BrowserEngineProbe.Probe();
        string? exe = edge ? found.EdgePath : found.ChromePath;
        if (exe == null) return false;
        Process.Start(new ProcessStartInfo(exe, edge ? "edge://extensions" : "chrome://extensions") { UseShellExecute = false })?.Dispose();
        return true;
    }

    public void OnAppShutdown() => _bridge.Stop();
}
