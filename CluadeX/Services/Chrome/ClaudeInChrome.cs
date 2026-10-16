using System.IO;
using System.Text.RegularExpressions;
using CluadeX.Models;
using CluadeX.Services.Mcp;

namespace CluadeX.Services.Chrome;

/// <summary>
/// "Claude in Chrome" for CluadeX's agent: the user's own Chrome — signed in where they are signed in — driven
/// through the extension Claude Code installs. CluadeX starts Claude Code's own MCP server for it
/// (<c>claude.exe --claude-in-chrome-mcp</c>, stdio) as a managed MCP entry; that server talks to the extension's
/// native host, which serves several clients at once, so CluadeX and Claude Code can both use it.
/// claude.exe lives in a versioned folder that an update replaces, so it is found again before every start.
/// </summary>
public sealed class ClaudeInChromeLauncher : IMcpLaunchResolver
{
    public const string ServerName = "cluadex-chrome";
    public const string ManagedTag = "claude-in-chrome";

    private readonly SettingsService _settings;

    public ClaudeInChromeLauncher(SettingsService settings) => _settings = settings;

    /// <summary>The native-host launcher Claude Code writes when the extension is set up.</summary>
    public static string NativeHostLauncher =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "chrome", "chrome-native-host.bat");

    /// <summary>Claude Code to run: the user's override, else the one the extension's native host runs — unless an
    /// update removed it (the host script keeps naming the old versioned folder) — else the newest claude.exe the
    /// desktop app keeps under %APPDATA%\Claude\claude-code\&lt;version&gt;\, else one on PATH, else the npm install's
    /// cli.js (run with node). Null when Claude Code is not on this PC.</summary>
    public static string? FindClaudeExe(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;
        try
        {
            if (File.Exists(NativeHostLauncher))
                foreach (Match m in Regex.Matches(File.ReadAllText(NativeHostLauncher), "\"([^\"]+?claude\\.exe)\"", RegexOptions.IgnoreCase))
                    if (File.Exists(m.Groups[1].Value)) return m.Groups[1].Value;
        }
        catch { /* unreadable: look elsewhere */ }
        try
        {
            string versions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code");
            if (Directory.Exists(versions))
            {
                var newest = Directory.GetDirectories(versions)
                    .Select(d => (dir: d, ver: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
                    .Where(x => x.ver != null).OrderByDescending(x => x.ver)
                    .Select(x => Directory.GetFiles(x.dir, "claude.exe", SearchOption.AllDirectories).FirstOrDefault())
                    .FirstOrDefault(f => f != null);
                if (newest != null) return newest;
            }
        }
        catch { }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string exe = Path.Combine(dir.Trim(), "claude.exe");
                if (File.Exists(exe)) return exe;
            }
            catch { }
        }
        string cli = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@anthropic-ai", "claude-code", "cli.js");
        return File.Exists(cli) ? cli : null;
    }

    /// <summary>The extension has been connected to Claude Code at least once (its native host is registered).</summary>
    public static bool ExtensionSetUp => File.Exists(NativeHostLauncher);

    /// <summary>An update removed the claude.exe the native-host script names: Chrome then cannot start the host, so
    /// the extension never reaches <c>--claude-in-chrome-mcp</c> ("Browser extension is not connected") although its
    /// side panel looks connected (that is the desktop app's own host). Point the script at <paramref name="exe"/>,
    /// keeping a .bak of the old one. True when the script was rewritten.</summary>
    public static bool RepairNativeHostLauncher(string exe)
    {
        try
        {
            if (!File.Exists(NativeHostLauncher) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            string text = File.ReadAllText(NativeHostLauncher);
            var named = Regex.Match(text, "\"([^\"]+?claude\\.exe)\"", RegexOptions.IgnoreCase);
            if (!named.Success || File.Exists(named.Groups[1].Value)) return false;
            File.Copy(NativeHostLauncher, NativeHostLauncher + ".bak", overwrite: true);
            File.WriteAllText(NativeHostLauncher, text.Replace(named.Groups[1].Value, exe));
            return true;
        }
        catch { return false; }   // read-only or locked: the start goes on, the tool error explains the rest
    }

    public Task<McpLaunchResolution?> ResolveAsync(string serverName, McpServerConfig configured, CancellationToken ct)
    {
        if (serverName != ServerName && configured.ManagedBy != ManagedTag) return Task.FromResult<McpLaunchResolution?>(null);
        string? exe = FindClaudeExe(_settings.Settings.ClaudeCodePathOverride);
        if (exe == null)
            return Task.FromResult<McpLaunchResolution?>(McpLaunchResolution.Refuse(LocalizedResources.Instance["chrome.noClaudeCode"]));
        bool script = exe.EndsWith(".js", StringComparison.OrdinalIgnoreCase);   // the npm install: node cli.js …
        if (!script) RepairNativeHostLauncher(exe);
        return Task.FromResult<McpLaunchResolution?>(McpLaunchResolution.Use(new McpServerConfig
        {
            Command = script ? "node" : exe,
            Args = script ? [exe, "--claude-in-chrome-mcp"] : ["--claude-in-chrome-mcp"],
            Env = configured.Env,
            Enabled = true,
            ManagedBy = ManagedTag,
            StartupTimeoutSeconds = configured.StartupTimeoutSeconds ?? 45,
        }));
    }
}

/// <summary>Switches the connector on and off: the managed MCP entry, the setting, and starting/stopping it.</summary>
public sealed class ClaudeInChromeConnector
{
    private readonly SettingsService _settings;
    private readonly McpServerManager _mcp;

    public ClaudeInChromeConnector(SettingsService settings, McpServerManager mcp)
    {
        _settings = settings;
        _mcp = mcp;
    }

    public bool Enabled => _settings.Settings.ChromeConnectorEnabled;
    public bool ClaudeCodeFound => ClaudeInChromeLauncher.FindClaudeExe(_settings.Settings.ClaudeCodePathOverride) != null;
    public bool ExtensionSetUp => ClaudeInChromeLauncher.ExtensionSetUp;

    /// <summary>On: the managed entry is (re)written enabled and started. Off: stopped and written disabled — the
    /// entry stays so the MCP page shows it, managed by CluadeX.</summary>
    public async Task<bool> SetEnabledAsync(bool on, CancellationToken ct = default)
    {
        _settings.UpdateSettings(s => s.ChromeConnectorEnabled = on);
        _mcp.SetConfig(ClaudeInChromeLauncher.ServerName, new McpServerConfig
        {
            Command = "claude",   // replaced at every start by ClaudeInChromeLauncher
            Args = ["--claude-in-chrome-mcp"],
            Enabled = on,
            ManagedBy = ClaudeInChromeLauncher.ManagedTag,
            StartupTimeoutSeconds = 45,
        });
        _mcp.SaveConfig(ClaudeInChromeLauncher.ServerName);
        if (!on)
        {
            await _mcp.StopServerAsync(ClaudeInChromeLauncher.ServerName);
            return true;
        }
        return await _mcp.StartServerAsync(ClaudeInChromeLauncher.ServerName, ct);
    }

    /// <summary>Startup: the setting is on but the entry is missing (a fresh mcp_servers.json) — put it back.</summary>
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        if (!Enabled || _mcp.Configs.ContainsKey(ClaudeInChromeLauncher.ServerName)) return;
        await SetEnabledAsync(true, ct);
    }
}
