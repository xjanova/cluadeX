using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services.Mcp;

namespace CluadeX.Services.Connections;

/// <summary>Where a connection lives.</summary>
public enum ConnectorPlace
{
    None,
    /// <summary>A BrainX bridge: shared by every agent wired to the brain (the first choice).</summary>
    BrainX,
    /// <summary>CluadeX's own MCP list (no BrainX on this PC).</summary>
    CluadeX,
}

/// <summary>Everything the Setup page shows about one connection (gathered off the UI thread; fed by hand in the harness).</summary>
public sealed record ConnectorFacts
{
    public ConnectorDef Def { get; init; } = null!;
    public bool GhInstalled { get; init; }
    public bool GhSignedIn { get; init; }
    public bool ServerReady { get; init; }
    public Version? Node { get; init; }
    public bool KeySaved { get; init; }
    /// <summary>Where CluadeX's own entry is (None when it has none).</summary>
    public ConnectorPlace Place { get; init; }
    /// <summary>An entry the user set up by hand serves it already (in BrainX or in CluadeX): nothing more is added.</summary>
    public ConnectorPlace ManualPlace { get; init; }
    /// <summary>Its tools are in the AI's tool list right now.</summary>
    public bool Live { get; init; }
    /// <summary>BrainX is installed and its vault found: a new connection goes there.</summary>
    public bool BrainAvailable { get; init; }

    public bool Connected => Place != ConnectorPlace.None || ManualPlace != ConnectorPlace.None;
}

/// <summary>
/// Connections to outside services (GitHub, Stripe, Canva) through their official MCP servers — the simplest path:
/// one button on the Setup page, a sign-in or one key, and the AI has the service's tools.
///
/// BrainX first: when BrainX is installed, the connection is added as a BrainX bridge, so every agent on this PC
/// (CluadeX, Claude, Gemini, Grok) shares one connection; CluadeX's own MCP list is the fallback. Never both — adding
/// it to one side removes CluadeX's entry from the other, and a connection the user already made by hand (either
/// side) is recognised and left as the one. Config files only ever name the launcher ("CluadeX.exe --connector id"):
/// keys stay DPAPI-encrypted on this PC (<see cref="ConnectorSecrets"/>).
/// </summary>
public sealed class ConnectionService
{
    public const string ManagedBy = "cluadex-connect";

    private readonly SettingsService _settings;
    private readonly McpServerManager? _mcp;
    private readonly DebugLogService? _log;

    public ConnectionService(SettingsService settings, McpServerManager? mcp = null, DebugLogService? log = null)
    {
        _settings = settings;
        _mcp = mcp;
        _log = log;
        Secrets = new ConnectorSecrets(settings.DataRoot);
    }

    public ConnectorSecrets Secrets { get; }

    /// <summary>The exe BrainX / the MCP list start: this CluadeX (Velopack keeps %LOCALAPPDATA%\CluadeX\current\CluadeX.exe
    /// stable across updates).</summary>
    public static string LauncherExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CluadeX.exe");

    // ═══════════════════════════════════════════════════════════════════
    //  Where things are
    // ═══════════════════════════════════════════════════════════════════

    private string? BrainVaultFromEntry()
    {
        if (_mcp?.BrainServerName is { } n && _mcp.Configs.TryGetValue(n, out var cfg) && cfg.Env != null
            && cfg.Env.TryGetValue("BRAINX_VAULT", out var v)) return v;
        return null;
    }

    /// <summary>The bridges file a new connection goes to, or null (no BrainX on this PC / no vault).</summary>
    public string? BrainBridgesFile()
    {
        if (McpServerManager.FindInstalledBrainExe() == null && _mcp?.BrainServerName == null) return null;
        return BrainXBridges.ResolveVault(BrainVaultFromEntry()) is { } vault ? BrainXBridges.FileFor(vault) : null;
    }

    /// <summary>The CluadeX MCP name CluadeX's entry uses (the id, unless the user already has a different server under it).</summary>
    private string LocalName(ConnectorDef c)
    {
        if (_mcp == null || !_mcp.Configs.TryGetValue(c.Id, out var cfg)) return c.Id;
        return cfg.ManagedBy == ManagedBy ? c.Id : "cx-" + c.Id;
    }

    private (string? Name, bool Ours) FindLocal(ConnectorDef c)
    {
        if (_mcp == null) return (null, false);
        foreach (var (name, cfg) in _mcp.Configs)
        {
            if (cfg.ManagedBy == ManagedBy && (name == c.Id || name == "cx-" + c.Id)) return (name, true);
            if (cfg.Enabled && cfg.ManagedBy == null && ConnectorCatalog.Recognises(c, cfg.Command, cfg.Args, cfg.Url)) return (name, false);
        }
        return (null, false);
    }

    /// <summary>Are its tools in the AI's list now? (Through BrainX: the brain's tools named "&lt;id&gt;__…".)</summary>
    public bool IsLive(ConnectorDef c)
    {
        var reg = _mcp?.ToolRegistry;
        if (reg == null) return false;
        if (_mcp!.BrainServerName is { } brain && reg.GetToolsForServer(brain).Any(t => t.Name.StartsWith(c.Id + "__", StringComparison.OrdinalIgnoreCase)))
            return true;
        var (local, _) = FindLocal(c);
        return local != null && reg.GetToolsForServer(local).Count > 0;
    }

    /// <summary>The built-in gh-based GitHub tools step aside while a GitHub MCP connection is live — one set of
    /// GitHub tools for the model, the richer one.</summary>
    public static bool GitHubMcpLive(McpServerManager? mcp)
    {
        var reg = mcp?.ToolRegistry;
        if (reg == null) return false;
        return reg.GetAllTools().Any(t => t.Name.StartsWith("github__", StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(t.ServerName, "github", StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(t.ServerName, "cx-github", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Look at every connection (off the UI thread: file reads, `gh auth token`, `node --version`).</summary>
    public List<ConnectorFacts> Gather()
    {
        string? bridgesFile = BrainBridgesFile();
        var root = bridgesFile != null ? BrainXBridges.Load(bridgesFile) : null;
        string? gh = ConnectorLauncher.FindGh();
        bool ghSignedIn = ConnectorLauncher.GhToken(gh) != null;
        string? npx = ConnectorLauncher.FindNpx();
        Version? node = npx != null ? NodeVersion(Path.Combine(Path.GetDirectoryName(npx)!, "node.exe")) : null;

        var list = new List<ConnectorFacts>();
        foreach (var c in ConnectorCatalog.All)
        {
            var (bridge, bridgeOurs) = root != null ? BrainXBridges.Find(root, c) : (null, false);
            var (local, localOurs) = FindLocal(c);
            var place = bridge != null && bridgeOurs ? ConnectorPlace.BrainX : local != null && localOurs ? ConnectorPlace.CluadeX : ConnectorPlace.None;
            var manual = bridge != null && !bridgeOurs ? ConnectorPlace.BrainX : local != null && !localOurs ? ConnectorPlace.CluadeX : ConnectorPlace.None;
            list.Add(new ConnectorFacts
            {
                Def = c,
                GhInstalled = gh != null, GhSignedIn = ghSignedIn,
                ServerReady = File.Exists(ConnectorLauncher.GitHubServerPath(_settings.DataRoot)),
                Node = node,
                KeySaved = c.Auth == ConnectorAuth.ApiKey && Secrets.Has(c.Id),
                Place = place, ManualPlace = manual,
                Live = IsLive(c),
                BrainAvailable = bridgesFile != null && root != null,
            });
        }
        return list;
    }

    private static Version? NodeVersion(string node)
    {
        if (!File.Exists(node)) return null;
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(node, "--version")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (p == null) return null;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return ConnectorCatalog.ParseNodeVersion(o);
        }
        catch { return null; }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Connecting
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Put the connection where it belongs (BrainX first) and make its tools live. Returns where it went, or
    /// throws with an error key. Removes CluadeX's entry from the other side, so it is never in both.</summary>
    public async Task<ConnectorPlace> RegisterAsync(ConnectorDef c, CancellationToken ct = default)
    {
        string exe = LauncherExe;
        string? file = BrainBridgesFile();
        var root = file != null ? BrainXBridges.Load(file) : null;

        if (file != null && root != null && BrainXBridges.Upsert(root, c, exe))
        {
            BrainXBridges.Save(file, root);
            _log?.Info("Connections", $"{c.Id} → BrainX bridge ({file})");
            await RemoveLocalAsync(c);
            await RestartBrainAsync(ct);
            return ConnectorPlace.BrainX;
        }

        if (_mcp == null) throw new InvalidOperationException("connect.err.noMcp");
        if (_mcp.ConfigLoadFailed) throw new InvalidOperationException("connect.err.mcpUnreadable");
        string name = LocalName(c);
        _mcp.SetConfig(name, new McpServerConfig
        {
            Command = exe, Args = new List<string> { ConnectorLauncher.Flag, c.Id }, Enabled = true,
            ManagedBy = ManagedBy, StartupTimeoutSeconds = c.StartupTimeoutSeconds,
        });
        if (!_mcp.SaveConfig(name)) throw new InvalidOperationException("connect.err.mcpUnreadable");
        _log?.Info("Connections", $"{c.Id} → CluadeX MCP list as '{name}' (no BrainX)");
        try { await _mcp.StartServerAsync(name, ct); }
        catch (Exception ex) { _log?.Warn("Connections", $"{c.Id} did not start yet", ex); }
        return ConnectorPlace.CluadeX;
    }

    /// <summary>Remove CluadeX's entries for it (both sides) and its key. Entries the user made by hand are left alone.</summary>
    public async Task DisconnectAsync(ConnectorDef c, CancellationToken ct = default)
    {
        if (BrainBridgesFile() is { } file && BrainXBridges.Load(file) is { } root && BrainXBridges.Remove(root, c))
        {
            BrainXBridges.Save(file, root);
            await RestartBrainAsync(ct);
        }
        await RemoveLocalAsync(c);
        Secrets.Remove(c.Id);
        _log?.Info("Connections", $"{c.Id} disconnected");
    }

    private async Task RemoveLocalAsync(ConnectorDef c)
    {
        if (_mcp == null) return;
        foreach (var name in new[] { c.Id, "cx-" + c.Id })
        {
            if (!_mcp.Configs.TryGetValue(name, out var cfg) || cfg.ManagedBy != ManagedBy) continue;
            try { await _mcp.StopServerAsync(name); } catch { }
            _mcp.RemoveConfig(name);
            _mcp.SaveConfig(name);
        }
    }

    /// <summary>The brain reads its bridges when it starts: restart CluadeX's brain server so the new tools appear now
    /// (other agents pick them up at their next start).</summary>
    private async Task RestartBrainAsync(CancellationToken ct)
    {
        if (_mcp?.BrainServerName is not { } brain || !_mcp.IsBrainConnected) return;
        try
        {
            await _mcp.StopServerAsync(brain);
            await _mcp.StartServerAsync(brain, ct);
        }
        catch (Exception ex) { _log?.Warn("Connections", "restarting BrainX for the new bridge failed", ex); }
    }

    /// <summary>At start-up: a connection CluadeX keeps in its own list moves to BrainX once BrainX is installed (BrainX
    /// first, never both).</summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        if (_mcp == null || BrainBridgesFile() is not { } file) return;
        // CluadeX moved (a portable copy, another install folder): point CluadeX's own bridges at this exe again.
        if (BrainXBridges.Load(file) is { } root)
        {
            bool changed = false;
            foreach (var c in ConnectorCatalog.All)
                if (BrainXBridges.Find(root, c) is { Entry: { } e, Ours: true }
                    && !string.Equals(e["command"]?.GetValue<string>(), LauncherExe, StringComparison.OrdinalIgnoreCase))
                    changed |= BrainXBridges.Upsert(root, c, LauncherExe);
            if (changed)
            {
                BrainXBridges.Save(file, root);
                _log?.Info("Connections", "BrainX bridges now point at this CluadeX: " + LauncherExe);
            }
        }
        foreach (var c in ConnectorCatalog.All)
        {
            var (local, ours) = FindLocal(c);
            if (local == null || !ours) continue;
            try
            {
                if (await RegisterAsync(c, ct) == ConnectorPlace.BrainX)
                    _log?.Info("Connections", $"{c.Id} moved from CluadeX's list to BrainX");
            }
            catch (Exception ex) { _log?.Warn("Connections", $"moving {c.Id} to BrainX failed", ex); }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GitHub's server
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>"&lt;sha256&gt;  &lt;file&gt;" lines → the hash for <paramref name="asset"/> (lower case), or null. Pure.</summary>
    public static string? ChecksumFor(string checksums, string asset)
    {
        foreach (var raw in checksums.Split('\n'))
        {
            var parts = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == asset && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>Download GitHub's official server (the latest release of github/github-mcp-server), check it against
    /// the release's own checksums file, and keep only its exe. Throws with an error key.</summary>
    public async Task<string> DownloadGitHubServerAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CluadeX");
        progress?.Report("release");
        using var rel = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{ConnectorCatalog.GitHubServerRepo}/releases/latest", ct));
        string tag = rel.RootElement.GetProperty("tag_name").GetString() ?? "?";
        string? zipUrl = null, sumsUrl = null;
        foreach (var a in rel.RootElement.GetProperty("assets").EnumerateArray())
        {
            string n = a.GetProperty("name").GetString() ?? "";
            string u = a.GetProperty("browser_download_url").GetString() ?? "";
            string expectedPrefix = $"https://github.com/{ConnectorCatalog.GitHubServerRepo}/releases/download/";
            if (!u.StartsWith(expectedPrefix, StringComparison.Ordinal)) continue;   // only the repo's own release files
            if (n == ConnectorCatalog.GitHubServerAsset) zipUrl = u;
            else if (n.EndsWith("_checksums.txt", StringComparison.Ordinal)) sumsUrl = u;
        }
        if (zipUrl == null || sumsUrl == null) throw new InvalidOperationException("connect.err.noAsset");

        progress?.Report("download " + tag);
        string expected = ChecksumFor(await http.GetStringAsync(sumsUrl, ct), ConnectorCatalog.GitHubServerAsset)
                          ?? throw new InvalidOperationException("connect.err.checksum");
        byte[] zip = await http.GetByteArrayAsync(zipUrl, ct);
        if (zip.Length > 200 * 1024 * 1024) throw new InvalidOperationException("connect.err.checksum");
        string actual = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        if (actual != expected) throw new InvalidOperationException("connect.err.checksum");

        progress?.Report("install " + tag);
        string target = ConnectorLauncher.GitHubServerPath(_settings.DataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
        {
            var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, ConnectorCatalog.GitHubServerExe, StringComparison.OrdinalIgnoreCase)
                                                            && !e.FullName.Contains("..", StringComparison.Ordinal))
                        ?? throw new InvalidOperationException("connect.err.noAsset");
            string tmp = target + ".download";
            entry.ExtractToFile(tmp, overwrite: true);
            File.Move(tmp, target, overwrite: true);
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(target)!, "version.txt"), tag);
        _log?.Info("Connections", $"GitHub MCP server {tag} installed (sha256 {actual[..12]}…)");
        return tag;
    }
}
