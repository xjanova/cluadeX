using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CluadeX.Models;

namespace CluadeX.Services.Mcp;

/// <summary>Lifecycle of one configured MCP server, as the UI should see it.</summary>
public enum McpServerState
{
    /// <summary>Configured but not started (or deliberately stopped).</summary>
    Stopped,
    /// <summary>Process spawning / handshaking.</summary>
    Starting,
    /// <summary>Handshake done, tools discovered, calls will work.</summary>
    Ready,
    /// <summary>Died on its own; the supervisor is retrying with backoff.</summary>
    Reconnecting,
    /// <summary>Retries exhausted — needs a human (MCP Servers page → Restart).</summary>
    Failed,
}

/// <summary>
/// Manages MCP server lifecycles: load config, start/stop servers,
/// discover tools, handle reconnection.
///
/// SUPERVISION (2026-08-04): servers used to be started exactly once at app
/// startup and never watched again. On 2026-08-04 the brainx-mcp child died
/// ~30s in; nothing noticed for four minutes, the registry kept advertising its
/// 83 tools, and every call came back "not running". So: a transport that exits
/// on its own now clears its tools, flips this object's observable state, and is
/// respawned with backoff. INotifyPropertyChanged so the status bar can bind to
/// the truth instead of the user having to open a page and press Refresh.
/// </summary>
public sealed partial class McpServerManager : INotifyPropertyChanged, IDisposable
{
    /// <summary>How many times to respawn a server that died on its own before giving up.</summary>
    private const int MaxReconnectAttempts = 5;

    /// <summary>
    /// Crash-loop brake. <see cref="MaxReconnectAttempts"/> bounds consecutive
    /// FAILED STARTS and nothing else, so a server that handshakes cleanly and
    /// then dies a second later resets that counter every cycle and respawns
    /// forever — a process pair per second, indefinitely.
    ///
    /// That is not hypothetical: it is the exact shape of the 2026-08-04 failure
    /// this supervisor was written for (Ready at 10:19:16, gone by 10:19:46).
    /// A server that dies this soon after reaching Ready this many times is
    /// broken, and the honest answer is a red chip, not a spawn loop.
    /// </summary>
    private const int MaxRapidCrashes = 4;
    private static readonly TimeSpan RapidCrashWindow = TimeSpan.FromSeconds(60);

    private readonly SettingsService _settingsService;
    private readonly DebugLogService? _log;
    private readonly McpToolRegistry _toolRegistry;
    // Concurrent: touched by the UI thread (start/stop), the agent thread (CallTool reads), and the
    // server read-loop/notification callback thread — plain Dictionary structural writes raced reads.
    private readonly ConcurrentDictionary<string, McpStdioTransport> _transports = new();
    private readonly ConcurrentDictionary<string, McpServerConfig> _configs = new();
    private readonly ConcurrentDictionary<string, McpServerState> _states = new();
    // Only servers that finished a full successful start are supervised. A start
    // that fails mid-handshake is handled by StartServerAsync's own catch — letting
    // the exit event ALSO fire a reconnect there would double-spawn.
    private readonly ConcurrentDictionary<string, byte> _supervised = new();
    // When each server last reached Ready, and how many times it has died inside
    // RapidCrashWindow of doing so. Feeds the crash-loop brake above.
    private readonly ConcurrentDictionary<string, DateTime> _readySinceUtc = new();
    private readonly ConcurrentDictionary<string, int> _rapidCrashes = new();
    // One start at a time per server, or a user-pressed Restart racing the
    // supervisor leaks an orphan process pair.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _startGates = new();
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    // ─── mcp_servers.json is the USER's file ──────────────────────────
    // It holds every server they configured and the API keys in each env block, and it is also written by hand
    // and by other installers (BrainX registers itself there). The old load was strict and the old save wrote
    // only what was in memory, so a trailing comma made the load fail silently, the startup BrainX auto-connect
    // then "saved" — and the file was replaced by a single brainx-brain entry: every other server and key gone.
    // Now: read the way people write JSON (trailing commas, // comments), never write while the file can't be
    // read, and write by merging into what is on disk so fields this app doesn't know survive.

    /// <summary>Reading mcp_servers.json: tolerant of what hand-edited JSON looks like.</summary>
    private static readonly JsonSerializerOptions ConfigReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonDocumentOptions ConfigDocOpts = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Writing mcp_servers.json: indented, and Thai / non-ASCII paths kept readable instead of \uXXXX.</summary>
    private static readonly JsonSerializerOptions ConfigWriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One writer at a time, and no write between another writer's read and its write.</summary>
    private readonly object _configFileLock = new();
    // The file exists but could not be understood. While set, nothing is written to it — not by the user's own
    // edits on the MCP page, not by the automatic BrainX registration. Cleared only by a successful re-read.
    private volatile bool _configLoadFailed;
    // Entries that are in the file but could not be read (wrong types). Left byte-for-byte alone on every save,
    // and their names cannot be reused, so a save never writes over one.
    private readonly ConcurrentDictionary<string, byte> _unreadableEntries = new();
    // Servers the user removed since the last load. Saving merges into the file, so a removal has to be explicit
    // — "not in memory" is not the same as "delete it": it is also what an entry another installer added looks like.
    private readonly ConcurrentDictionary<string, byte> _pendingRemovals = new();
    // Servers whose supervisor is between respawn attempts (the state flickers Starting → Failed → Starting).
    private readonly ConcurrentDictionary<string, byte> _reconnecting = new();
    // Which respawn attempt each reconnecting server is on (per server: two servers reconnecting at once used to
    // share one counter, so the brain chip could show another server's attempt number).
    private readonly ConcurrentDictionary<string, int> _reconnectAttempts = new();
    // The last error each server reported — the "not running" hint used to quote the BRAIN's error for any server.
    private readonly ConcurrentDictionary<string, string> _lastErrors = new();
    // Servers a start was attempted for this session (by startup, the page, the supervisor or the BrainX window).
    // After a re-read that recovered the file, the enabled servers NOT in here are the ones startup never got to.
    private readonly ConcurrentDictionary<string, byte> _startAttempted = new();
    // Set by a load that recovered (the file, or an entry in it, could not be read before); cleared once the
    // enabled servers it brought back have been started.
    private volatile bool _reloadPendingStart;
    // mcp_servers.json has been copied to .bak this session (guarded by _configFileLock).
    private bool _sessionBackupDone;
    // What the MCP page last heard about the file — see RaiseConfigStatusIfChanged.
    private string _lastConfigStatus = ConfigStatusKey(false, Array.Empty<string>(), false);

    private static string L(string key) => LocalizedResources.Instance[key];

    public event Action? OnToolsChanged;
    public event Action<string, string>? OnServerLog; // serverName, message
    /// <summary>serverName, new state — raised on every lifecycle transition.</summary>
    public event Action<string, McpServerState>? OnServerStateChanged;
    /// <summary>
    /// mcp_servers.json was loaded, or its state changed: it became unreadable or readable again, the set of
    /// entries that can't be read changed, or a recovered load is (no longer) waiting for its servers to be
    /// started. The MCP page is built before the startup load, so this — not its constructor — is how it learns
    /// what is in the file and whether it is bad. Raised outside the file lock, from whatever thread did the
    /// load or the write.
    /// </summary>
    public event Action? ConfigStatusChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public McpToolRegistry ToolRegistry => _toolRegistry;
    public IReadOnlyDictionary<string, McpServerConfig> Configs => _configs;

    public McpServerManager(SettingsService settingsService, DebugLogService? log = null)
    {
        _settingsService = settingsService;
        _log = log;
        _toolRegistry = new McpToolRegistry();
    }

    // ─── "Which server is the brain?" — ONE definition ────────────────
    // BrainSyncService, the auto-recall gate and the status chip all need this
    // answer. Three copies of the rule is how a UI ends up disagreeing with the
    // thing it reports on, so they all call here.

    /// <summary>True if this server name looks like an ObsidianX/BrainX brain.</summary>
    public static bool LooksLikeBrain(string name)
        => name.Contains("brain", StringComparison.OrdinalIgnoreCase)
        || name.Contains("obsidianx", StringComparison.OrdinalIgnoreCase);

    // ─── Observable status for the status-bar chip ────────────────────

    /// <summary>Name of the configured brain server, or null if none is configured.</summary>
    public string? BrainServerName
        => _configs.Keys.FirstOrDefault(LooksLikeBrain);

    /// <summary>Lifecycle state of the brain server (Stopped when none is configured). Between two respawn
    /// attempts this is Reconnecting, not Failed — see <see cref="GetServerState"/>.</summary>
    public McpServerState BrainState
    {
        get
        {
            var name = BrainServerName;
            return name != null ? GetServerState(name) : McpServerState.Stopped;
        }
    }

    /// <summary>True only when a brain call would actually reach a live process.</summary>
    public bool IsBrainConnected => BrainState == McpServerState.Ready;

    /// <summary>Tools the brain currently advertises. Zero whenever it isn't Ready.</summary>
    public int BrainToolCount
    {
        get
        {
            var name = BrainServerName;
            return name == null ? 0 : _toolRegistry.GetToolsForServer(name).Count;
        }
    }

    /// <summary>Short label for the chip: "ready · 83 tools", "reconnecting 2/5", …
    /// While the supervisor owns a respawn, every in-between state (an attempt starting, an attempt that just
    /// failed) reads "reconnecting n/5" — the chip used to say DOWN in the gap while the BrainX window, asking
    /// <see cref="IsBrainStarting"/>, said "starting".</summary>
    public string BrainStatusText
    {
        get
        {
            var name = BrainServerName;
            if (name == null) return "brain · not configured";
            if (IsReconnecting(name))
                return $"brain · reconnecting {(_reconnectAttempts.TryGetValue(name, out int a) ? a : 1)}/{MaxReconnectAttempts}";
            return GetServerState(name) switch
            {
                McpServerState.Ready    => $"brain · {BrainToolCount} tools",
                McpServerState.Starting => "brain · connecting…",
                McpServerState.Failed   => "brain · DOWN",
                _                       => "brain · stopped",
            };
        }
    }

    /// <summary>The supervisor is respawning this server and it isn't up yet.</summary>
    private bool IsReconnecting(string name)
        => _reconnecting.ContainsKey(name) && RawState(name) != McpServerState.Ready;

    /// <summary>Last error seen on the brain server — the tooltip's whole point.</summary>
    public string? BrainLastError { get; private set; }

    // ─── Is BrainX on this PC at all? ─────────────────────────────────

    /// <summary>Where BrainX stands on this PC, from best to worst.</summary>
    public enum BrainInstall
    {
        Connected,
        /// <summary>Configured and on its way up: starting, or crashed and being respawned. Not "not running" —
        /// starting it again would kill the attempt in flight.</summary>
        Starting,
        Configured,
        InstalledNotConfigured,
        NotInstalled,
    }

    /// <summary>Why the last <see cref="ConnectInstalledBrainAsync"/> did not end connected — for a localized message.</summary>
    public enum BrainConnectProblem
    {
        None,
        /// <summary>mcp_servers.json (or the brain's entry in it) could not be read, so nothing was written to it.</summary>
        ConfigUnreadable,
        /// <summary>mcp_servers.json was read fine but could not be written (locked by another program, read-only,
        /// disk full) — BrainX was not registered, and the file is as it was.</summary>
        ConfigNotSaved,
        /// <summary>BrainX's MCP exe is not on this PC.</summary>
        NotInstalled,
        /// <summary>The server was started and did not come up (the reason is in the server log).</summary>
        StartFailed,
    }

    /// <summary>Why the last write of mcp_servers.json did not happen.</summary>
    public enum ConfigWriteProblem
    {
        None,
        /// <summary>The file can't be read, so it is not being changed at all (fix it, then Refresh).</summary>
        Unreadable,
        /// <summary>The file was read but the write itself failed: locked, read-only, disk full… (details in the debug log).</summary>
        NotSaved,
    }

    /// <summary>Set by every write of mcp_servers.json: why it did not happen, or None.</summary>
    public ConfigWriteProblem LastConfigWriteProblem { get; private set; }

    /// <summary>What the startup check did about an installed-but-unlisted BrainX.</summary>
    public enum BrainAutoConnect
    {
        /// <summary>BrainX is already listed, or not installed — nothing to register.</summary>
        NotNeeded,
        Connected,
        /// <summary>Registered (or found) and started, but it did not come up.</summary>
        Failed,
        /// <summary>The user removed BrainX on the MCP page or said "Not interested" — never re-added by itself.</summary>
        SkippedDeclined,
        /// <summary>mcp_servers.json could not be read; registering would have meant writing over it.</summary>
        SkippedConfigUnreadable,
        /// <summary>mcp_servers.json could not be written (locked, read-only, disk full), so BrainX was not registered.</summary>
        SkippedConfigNotSaved,
    }

    public BrainConnectProblem LastBrainConnectProblem { get; private set; }

    /// <summary>BrainX's MCP server exe, or null when BrainX isn't installed. The stable <c>BrainX\mcp</c> shim
    /// first (it survives BrainX updates), then the Velopack <c>current</c> folder.</summary>
    public static string? FindInstalledBrainExe()
    {
#if DEBUG
        // Debug builds only: the contract harness (tests/apitest) points this at a stub so the startup
        // auto-connect path can be exercised without ever starting the real BrainX. Read on every call — never
        // cached — and when it is set it is the ONLY answer: a missing stub means "not installed", never a
        // fall-through to the real install.
        string? overridePath = Environment.GetEnvironmentVariable("CLUADEX_BRAINX_EXE");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return File.Exists(overridePath) ? overridePath : null;
#endif
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var p in new[]
        {
            Path.Combine(local, "BrainX", "mcp", "brainx-mcp.exe"),
            Path.Combine(local, "BrainX", "current", "mcp", "brainx-mcp.exe"),
        })
            if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>True while the brain is starting, or crashed and its supervisor is still respawning it.</summary>
    public bool IsBrainStarting
    {
        get
        {
            var name = BrainServerName;
            if (name == null) return false;
            var state = GetServerState(name);
            return state is McpServerState.Starting or McpServerState.Reconnecting
                || (state != McpServerState.Ready && _reconnecting.ContainsKey(name));
        }
    }

    public BrainInstall GetBrainInstallState()
    {
        if (IsBrainConnected) return BrainInstall.Connected;
        if (IsBrainStarting) return BrainInstall.Starting;
        if (BrainServerName != null) return BrainInstall.Configured;
        return FindInstalledBrainExe() != null ? BrainInstall.InstalledNotConfigured : BrainInstall.NotInstalled;
    }

    /// <summary>
    /// The startup check (App.OnStartup): BrainX installed but not in the MCP list gets registered and started
    /// without asking — that is what installing it meant — unless the user already said no (removed it on the
    /// MCP page, or "Not interested" in the BrainX window), or mcp_servers.json can't be read.
    /// </summary>
    public async Task<BrainAutoConnect> AutoConnectInstalledBrainAsync(CancellationToken ct = default)
    {
        if (GetBrainInstallState() != BrainInstall.InstalledNotConfigured) return BrainAutoConnect.NotNeeded;
        if (_settingsService.Settings.BrainXAutoConnectDeclined)
        {
            Log("brainx-brain", "BrainX is installed but not connected automatically: it was removed or declined before (connect it from the BrainX window)");
            return BrainAutoConnect.SkippedDeclined;
        }
        if (_configLoadFailed)
        {
            // Not re-read here: this runs right after the load that failed. An explicit Connect in the BrainX
            // window or Refresh on the MCP page re-reads it.
            Log("config", "BrainX is installed but not registered: mcp_servers.json could not be read, and registering it would mean writing to it");
            return BrainAutoConnect.SkippedConfigUnreadable;
        }
        bool ok = await ConnectBrainAsync(userAsked: false, ct);
        if (ok) return BrainAutoConnect.Connected;
        return LastBrainConnectProblem switch
        {
            BrainConnectProblem.ConfigUnreadable => BrainAutoConnect.SkippedConfigUnreadable,
            BrainConnectProblem.ConfigNotSaved => BrainAutoConnect.SkippedConfigNotSaved,
            _ => BrainAutoConnect.Failed,
        };
    }

    /// <summary>The user's answer to "connect BrainX by itself at startup?" — set when they remove a brain server
    /// on the MCP page or press "Not interested"; cleared only by an explicit Connect in the BrainX window.</summary>
    public void SetBrainAutoConnectDeclined(bool declined)
    {
        if (_settingsService.Settings.BrainXAutoConnectDeclined == declined) return;
        _settingsService.UpdateSettings(s => s.BrainXAutoConnectDeclined = declined);
    }

    /// <summary>
    /// Connect a BrainX that is installed but not (yet) in this app's MCP list — installed after CluadeX started,
    /// or before CluadeX existed. BrainX's installer registers itself in mcp_servers.json, so that entry is taken
    /// first; otherwise one is made from the installed exe, carrying only the BRAINX_* settings (the vault path)
    /// from Claude Code's config — never any other variable, so no key of another tool is copied along.
    /// A brain that is already starting or reconnecting is waited for, not started again.
    /// This is the EXPLICIT path (the BrainX window's Connect): a config file that could not be read is read once
    /// more first — the user may have fixed it since — and only refused if it still can't be.
    /// </summary>
    public Task<bool> ConnectInstalledBrainAsync(CancellationToken ct = default) => ConnectBrainAsync(userAsked: true, ct);

    private async Task<bool> ConnectBrainAsync(bool userAsked, CancellationToken ct)
    {
        LastBrainConnectProblem = BrainConnectProblem.None;

        // The flag used to stick for the whole session: a file fixed after startup still refused every Connect
        // until CluadeX was restarted. A successful re-read clears it (and tells the MCP page).
        if (userAsked && _configLoadFailed)
        {
            Log("config", "Connect BrainX: reading mcp_servers.json again (it could not be read before)");
            LoadConfig();
        }

        // Starting it again here would stop the start in flight and spawn a second one.
        if (IsBrainStarting)
        {
            bool up = await WaitForBrainSettledAsync(ct);
            if (!up) LastBrainConnectProblem = BrainConnectProblem.StartFailed;
            return up;
        }

        bool registered = false;
        if (BrainServerName == null)
        {
            if (!TryRegisterInstalledBrain()) return false;
            registered = true;
        }

        try
        {
            string name = BrainServerName!;
            if (IsBrainConnected) return true;
            if (_configs.TryGetValue(name, out var cfg) && !cfg.Enabled)
            {
                cfg.Enabled = true;
                // Not saved = it starts this session and stays disabled on disk; the user's file is never forced.
                if (!WriteConfig(new[] { name }))
                    Log(name, "enabled for this session only — mcp_servers.json could not be updated");
            }
            bool ok = await StartServerAsync(name, ct);
            if (!ok) LastBrainConnectProblem = BrainConnectProblem.StartFailed;
            return ok;
        }
        finally
        {
            // A server was added to the list: the MCP page shows it now, whatever state it ended in (a page
            // that only heard about state changes never listed a registered BrainX that failed to start).
            if (registered) RaiseConfigStatusIfChanged(always: true);
        }
    }

    /// <summary>Wait (bounded) for a brain that is starting or reconnecting to end up somewhere.</summary>
    private async Task<bool> WaitForBrainSettledAsync(CancellationToken ct)
    {
        // Worst case is a full respawn cycle: 1+2+4+8+16 s of backoff plus a 15 s handshake per attempt.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (IsBrainStarting && !IsBrainConnected && DateTime.UtcNow < deadline)
            await Task.Delay(200, ct);
        return IsBrainConnected;
    }

    /// <summary>
    /// Put the installed BrainX into the in-memory list: its own entry from mcp_servers.json when the installer
    /// wrote one, else a new entry merged into the file (backed up first). Refuses — and writes nothing — when the
    /// file, or a brain entry in it, can't be read.
    /// </summary>
    private bool TryRegisterInstalledBrain()
    {
        if (_configLoadFailed)
        {
            LastBrainConnectProblem = BrainConnectProblem.ConfigUnreadable;
            Log("config", "BrainX not registered: mcp_servers.json could not be read — nothing is written to it until it is fixed "
                        + "(then Connect again, or press Refresh on the MCP Servers page)");
            return false;
        }

        ConfigSnapshot? disk;
        string? error;
        lock (_configFileLock) disk = ReadConfigFile(out error);
        if (disk == null)
        {
            _configLoadFailed = true;
            RaiseConfigStatusIfChanged();
            LastBrainConnectProblem = BrainConnectProblem.ConfigUnreadable;
            Log("config", $"BrainX not registered: mcp_servers.json can no longer be read ({error}) — it is left as it is");
            return false;
        }
        if (disk.Unreadable.Any(LooksLikeBrain))
        {
            LastBrainConnectProblem = BrainConnectProblem.ConfigUnreadable;
            Log("config", "BrainX not registered: mcp_servers.json has a brain entry that could not be read — it is left as it is");
            return false;
        }

        var listed = disk.Readable.FirstOrDefault(kv => LooksLikeBrain(kv.Key));
        if (listed.Key != null)
        {
            // Already in the file (BrainX's installer put it there after this app loaded the list): nothing to write.
            SetConfig(listed.Key, listed.Value);
            return true;
        }

        string? exe = FindInstalledBrainExe();
        if (exe == null)
        {
            LastBrainConnectProblem = BrainConnectProblem.NotInstalled;
            return false;
        }
        const string brainName = "brainx-brain";
        var env = ImportBrainEnvFromClaudeConfig();
        SetConfig(brainName, new McpServerConfig { Command = exe, Env = env.Count > 0 ? env : null, Enabled = true });
        if (!WriteConfig(new[] { brainName }))
        {
            _configs.TryRemove(brainName, out _);
            // Unreadable and "read fine, write failed" are different fixes (repair the JSON vs close the program
            // holding the file / clear read-only), so they are different reasons in the BrainX window.
            LastBrainConnectProblem = LastConfigWriteProblem == ConfigWriteProblem.Unreadable
                ? BrainConnectProblem.ConfigUnreadable
                : BrainConnectProblem.ConfigNotSaved;
            Log(brainName, LastBrainConnectProblem == BrainConnectProblem.ConfigUnreadable
                ? "BrainX not registered: mcp_servers.json could not be read"
                : "BrainX not registered: mcp_servers.json could not be saved (locked, read-only or disk full?) — it is as it was");
            return false;
        }
        Log(brainName, $"registered the installed BrainX ({exe})");
        return true;
    }

    private static Dictionary<string, string> ImportBrainEnvFromClaudeConfig()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            if (!File.Exists(path)) return env;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object) return env;
            foreach (var server in servers.EnumerateObject())
            {
                if (!LooksLikeBrain(server.Name)) continue;
                if (server.Value.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object)
                    foreach (var kv in e.EnumerateObject())
                        if (kv.Name.StartsWith("BRAINX_", StringComparison.OrdinalIgnoreCase) && kv.Value.ValueKind == JsonValueKind.String)
                            env[kv.Name] = kv.Value.GetString()!;
                break;
            }
        }
        catch { /* Claude Code not installed or its config unreadable — BrainX falls back to its own default vault */ }
        return env;
    }

    private void RaiseBrainStatus()
    {
        foreach (var p in new[] { nameof(BrainState), nameof(IsBrainConnected), nameof(IsBrainStarting), nameof(BrainToolCount),
                                  nameof(BrainStatusText), nameof(BrainLastError), nameof(BrainServerName) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>Record a lifecycle transition and tell everyone who is bound to it.</summary>
    private void SetState(string name, McpServerState state, string? error = null)
    {
        _states[name] = state;
        if (error != null) _lastErrors[name] = error;
        else if (state == McpServerState.Ready) _lastErrors.TryRemove(name, out _);
        // Only the brain's own transitions: another server's failure used to land in the brain chip's tooltip.
        if (LooksLikeBrain(name) && (error != null || state == McpServerState.Ready)) BrainLastError = error;
        try { OnServerStateChanged?.Invoke(name, GetServerState(name)); } catch { }
        RaiseBrainStatus();
    }

    private McpServerState RawState(string name)
        => _states.TryGetValue(name, out var s) ? s : McpServerState.Stopped;

    /// <summary>Current state of any configured server, as the UI should show it: an attempt that failed while
    /// the supervisor still has respawns to go is Reconnecting, not Failed (the chip, the BrainX window and the
    /// MCP page all read this, so they agree during a respawn).</summary>
    public McpServerState GetServerState(string name)
    {
        var s = RawState(name);
        return s == McpServerState.Failed && _reconnecting.ContainsKey(name) ? McpServerState.Reconnecting : s;
    }

    /// <summary>Where a server is restarted from, for a localized message: the MCP Servers page via the Ctrl+K
    /// palette (its entry is "MCP Servers" in every language), plus the 🧠 chip only for the brain — the chip
    /// is about BrainX, so offering it for any other server sent people to the wrong place.</summary>
    public static string RestartRoute(string serverName)
        => L(LooksLikeBrain(serverName) ? "mcp.route.pageOrChip" : "mcp.route.page");

    // ─── Logging ──────────────────────────────────────────────────────
    // OnServerLog's only subscriber is the MCP Servers page view-model, which is
    // built lazily and holds its lines in memory. That is why the launcher stderr
    // explaining the 2026-08-04 death was gone by the time anyone looked. Mirror
    // everything into the on-disk debug log.

    private void Log(string name, string message)
    {
        _log?.Info("MCP", $"[{name}] {message}");
        try { OnServerLog?.Invoke(name, message); } catch { }
    }

    /// <summary>For the BrainX window: the details of a failure it shows as a localized sentence go here.</summary>
    public void LogDiagnostic(string message, Exception? ex = null) => _log?.Warn("MCP", message, ex);

    private void LogStderr(string name, string message)
    {
        _log?.Debug("MCP", $"[{name}] {RedactSecrets(message)}");
        try { OnServerLog?.Invoke(name, $"[stderr] {message}"); } catch { }
    }

    /// <summary>
    /// Scrub obvious credentials out of a line before it reaches the on-disk log.
    ///
    /// Mirroring MCP stderr to `~/.cluadex/logs/` is what finally made a dead
    /// server explain itself — but stderr belongs to servers this app does not
    /// control, configured with API keys in `mcp_servers.json`, and a crashing
    /// one that dumps its environment would write those keys to a plaintext file
    /// the user is then asked to paste into a bug report.
    ///
    /// Deliberately narrow: known token prefixes and KEY=value shapes only. No
    /// generic "long hex" rule — brainx-mcp logs note shas, and a redactor that
    /// eats the diagnostics is a redactor nobody keeps switched on.
    /// </summary>
    internal static string RedactSecrets(string line)
    {
        if (string.IsNullOrEmpty(line)) return line;
        line = SecretAssignment().Replace(line, "$1=«redacted»");
        line = SecretToken().Replace(line, "«redacted»");
        return line;
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?i)\b([A-Z0-9_]*(?:KEY|TOKEN|SECRET|PASSWORD|PASSWD|CREDENTIAL)[A-Z0-9_]*)\s*[=:]\s*\S+")]
    private static partial System.Text.RegularExpressions.Regex SecretAssignment();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"(?i)\b(?:Bearer\s+[\w\-.~+/]{12,}|sk-[A-Za-z0-9\-_]{16,}|gh[posur]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9\-]{10,}|AIza[A-Za-z0-9\-_]{30,})")]
    private static partial System.Text.RegularExpressions.Regex SecretToken();

    /// <summary>Path to the MCP config file.</summary>
    private string ConfigPath => Path.Combine(_settingsService.DataRoot, "mcp_servers.json");

    /// <summary>Load config and start all enabled servers.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        LoadConfig();
        await StartAllEnabledAsync(ct);
    }

    /// <summary>Where mcp_servers.json is copied before this app first changes it in a session.</summary>
    private string ConfigBackupPath => ConfigPath + ".bak";

    /// <summary>True when mcp_servers.json exists but could not be read: nothing is written to it until a
    /// successful <see cref="LoadConfig"/> clears this — the MCP page's Refresh, or Connect in the BrainX window.</summary>
    public bool ConfigLoadFailed => _configLoadFailed;

    /// <summary>Servers in mcp_servers.json that could not be read (left untouched on every save).</summary>
    public IReadOnlyList<string> UnreadableServers => _unreadableEntries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>
    /// A name the Add form must not take: a listed server, an unreadable entry, or an entry another program put
    /// in the file since it was loaded — saving under any of those would write over a server that isn't the
    /// user's new one.
    /// </summary>
    public bool IsServerNameTaken(string name)
    {
        if (_configs.ContainsKey(name) || _unreadableEntries.ContainsKey(name)) return true;
        lock (_configFileLock)
        {
            var disk = ReadConfigFile(out _);
            return disk != null && (disk.Readable.ContainsKey(name) || disk.UnreadableWhy.ContainsKey(name));
        }
    }

    /// <summary>
    /// Enabled servers that a recovered load brought back and that were never started this session — what startup
    /// would have started had the file been readable then. Empty unless a re-read recovered the file (or an entry).
    /// </summary>
    public IReadOnlyList<string> ServersAwaitingStartAfterReload
        => _reloadPendingStart && !_configLoadFailed
            ? _configs.Where(kv => kv.Value.Enabled && !_startAttempted.ContainsKey(kv.Key))
                      .Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList()
            : Array.Empty<string>();

    /// <summary>
    /// The MCP page's Refresh: re-read mcp_servers.json when it (or an entry in it) could not be read, and when
    /// that works, do what startup would have done with it — start the enabled servers it never got to, and
    /// register an installed BrainX that was skipped because the file couldn't be written to.
    /// Returns true when the file was read again (or a recovered load's servers were started).
    /// </summary>
    public async Task<bool> RecoverConfigAsync(CancellationToken ct = default)
    {
        if (_configLoadFailed || !_unreadableEntries.IsEmpty)
        {
            if (!LoadConfig()) return false;
        }
        else if (!_reloadPendingStart) return false;

        await StartServersAwaitingReloadAsync(ct);
        try { await AutoConnectInstalledBrainAsync(ct); }
        catch (Exception ex) { _log?.Warn("MCP", "BrainX registration after re-reading mcp_servers.json threw", ex); }
        return true;
    }

    /// <summary>Start <see cref="ServersAwaitingStartAfterReload"/> (never the ones already started or stopped
    /// by the user this session), then stop advertising them.</summary>
    public async Task StartServersAwaitingReloadAsync(CancellationToken ct = default)
    {
        foreach (var name in ServersAwaitingStartAfterReload)
            await StartServerAsync(name, ct);
        _reloadPendingStart = false;
        RaiseConfigStatusIfChanged();
    }

    private static string ConfigStatusKey(bool failed, IEnumerable<string> unreadable, bool pendingStart)
        => $"{failed}|{pendingStart}|{string.Join("\u0001", unreadable)}";

    /// <summary>Tell the MCP page when what it says about the file would change (<paramref name="always"/>: after
    /// a load — the server list itself may be new). Called OUTSIDE the file lock: the page's handler reads the
    /// manager back.</summary>
    private void RaiseConfigStatusIfChanged(bool always = false)
    {
        string now = ConfigStatusKey(_configLoadFailed, UnreadableServers, _reloadPendingStart);
        if (Interlocked.Exchange(ref _lastConfigStatus, now) == now && !always) return;
        try { ConfigStatusChanged?.Invoke(); }
        catch (Exception ex) { _log?.Warn("MCP", "A ConfigStatusChanged handler threw", ex); }
    }

    /// <summary>
    /// Load server configurations from disk. Returns false when the file exists but can't be read — then the
    /// servers already in memory are kept, <see cref="ConfigLoadFailed"/> is set, and the file is never written.
    /// </summary>
    public bool LoadConfig()
    {
        bool ok;
        lock (_configFileLock) ok = LoadConfigLocked();
        // Every load, not only a change of status: the page is built before the startup load, and a file that
        // loads fine with only disabled servers in it otherwise left the page empty until Refresh.
        RaiseConfigStatusIfChanged(always: true);
        return ok;
    }

    private bool LoadConfigLocked()
    {
        bool recovering = _configLoadFailed || !_unreadableEntries.IsEmpty;
        if (!File.Exists(ConfigPath))
        {
            _configs.Clear();
            _unreadableEntries.Clear();
            _pendingRemovals.Clear();
            _configLoadFailed = false;
            try
            {
                // Create default empty config — there is nothing to lose yet.
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new McpConfigFile(), ConfigWriteOpts));
            }
            catch (Exception ex) { Log("config", $"Could not create mcp_servers.json: {ex.Message}"); }
            return true;
        }

        ConfigSnapshot? snap = ReadConfigFile(out string? error);
        if (snap == null)
        {
            _configLoadFailed = true;
            Log("config", $"mcp_servers.json could not be read ({error}). It is left exactly as it is, and nothing "
                        + "is written to it until it is fixed — then press Refresh on the MCP Servers page.");
            return false;
        }

        _configs.Clear();
        _unreadableEntries.Clear();
        _pendingRemovals.Clear();
        _configLoadFailed = false;
        foreach (var (name, config) in snap.Readable)
            _configs[name] = config;
        foreach (var (name, why) in snap.UnreadableWhy)
        {
            _unreadableEntries[name] = 1;
            Log("config", $"server \"{name}\" in mcp_servers.json could not be read ({why}) — left as it is in the file");
        }
        if (recovering)
        {
            _reloadPendingStart = true;
            Log("config", "mcp_servers.json was read again");
        }
        return true;
    }

    /// <summary>
    /// Save exactly the named servers — what one action on the MCP page changed (Add / Toggle / Save: that
    /// server; Remove: that removal). Merges into the file on disk rather than replacing it: every field this app
    /// doesn't model ("type", "url", "cwd", …), top-level settings, entries it couldn't read, entries another
    /// program added AND edits another program made to any other server since the load all stay — saving every
    /// in-memory server used to put an outside edit of a server the user never touched back the way it was loaded.
    /// Returns false — and writes nothing — while the file can't be read or can't be written
    /// (<see cref="LastConfigWriteProblem"/> says which).
    /// </summary>
    public bool SaveConfig(params string[] names) => WriteConfig(names);

    /// <summary>
    /// The one writer. Each name is written if it is in memory (its known fields: command/args/env/enabled) or
    /// deleted from the file if it is a pending removal; nothing else in the file changes. Before the first write
    /// of a session — and before any write over a file that has // comments, which a re-serialized file loses —
    /// the file is copied to mcp_servers.json.bak.
    /// </summary>
    private bool WriteConfig(IReadOnlyCollection<string> names)
    {
        bool ok;
        lock (_configFileLock) ok = WriteConfigLocked(names);
        RaiseConfigStatusIfChanged();
        return ok;
    }

    private bool WriteConfigLocked(IReadOnlyCollection<string> names)
    {
        LastConfigWriteProblem = ConfigWriteProblem.None;
        if (_configLoadFailed)
        {
            LastConfigWriteProblem = ConfigWriteProblem.Unreadable;
            Log("config", "Not saved: mcp_servers.json could not be read, so it is not being changed. Fix it, then press Refresh on the MCP Servers page.");
            return false;
        }

        var upsert = names.Where(n => _configs.ContainsKey(n)).Distinct(StringComparer.Ordinal).ToList();
        var remove = names.Where(n => !_configs.ContainsKey(n) && _pendingRemovals.ContainsKey(n)).Distinct(StringComparer.Ordinal).ToList();
        if (upsert.Count == 0 && remove.Count == 0) return true;   // nothing named to change: the file is not touched

        try
        {
            bool exists = File.Exists(ConfigPath);
            ConfigSnapshot? snap = exists ? ReadConfigFile(out string? error) : new ConfigSnapshot();
            if (snap == null)
            {
                // It was fine at load and is broken now (edited by hand while the app runs). Same rule.
                _configLoadFailed = true;
                LastConfigWriteProblem = ConfigWriteProblem.Unreadable;
                Log("config", "Not saved: mcp_servers.json can no longer be read, so it is not being changed.");
                return false;
            }

            JsonObject root = snap.Root;
            JsonObject servers = snap.Servers ?? new JsonObject();
            if (snap.Servers == null)
            {
                RemoveCaseInsensitive(root, "mcpServers");
                root["mcpServers"] = servers;
            }

            foreach (var name in remove)
                servers.Remove(name);

            foreach (var name in upsert)
            {
                if (!_configs.TryGetValue(name, out var cfg)) continue;
                if (snap.UnreadableWhy.ContainsKey(name) || _unreadableEntries.ContainsKey(name))
                {
                    Log("config", $"\"{name}\" left as it is: its entry in mcp_servers.json could not be read");
                    continue;
                }
                bool isNew = servers[name] is not JsonObject;
                if (servers[name] is not JsonObject entry)
                {
                    entry = new JsonObject();
                    servers[name] = entry;
                }
                // An existing entry only gains a field it didn't have when the value says something: an
                // http-type server ("type"/"url", no command) must not come back with "command": "" and "args": [].
                void Put(string key, JsonNode? value, bool isDefault)
                {
                    if (!isNew && isDefault && FindCaseInsensitive(entry, key) == null) return;
                    SetKnown(entry, key, value);
                }
                var args = cfg.Args ?? new List<string>();
                Put("command", JsonValue.Create(cfg.Command ?? ""), string.IsNullOrEmpty(cfg.Command));
                Put("args", new JsonArray(args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()), args.Count == 0);
                if (cfg.Env is { } env)
                    Put("env", new JsonObject(env.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))), env.Count == 0);
                else
                    RemoveCaseInsensitive(entry, "env");
                Put("enabled", JsonValue.Create(cfg.Enabled), cfg.Enabled);
            }

            string json = root.ToJsonString(ConfigWriteOpts);
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            // The write re-serializes the file: // comments are gone afterwards. So the file as it was is copied
            // to .bak before this app first changes it in a session, and again whenever it has comments in it
            // (put there by hand since) — but not on every write, which would replace the pre-session copy with
            // one of this session's own versions. No backup, no write.
            if (exists && (!_sessionBackupDone || snap.HasComments))
            {
                BackUpConfigFile();
                _sessionBackupDone = true;
            }
            // Write-then-rename: a crash mid-write leaves the old file, never half a file.
            string tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigPath, overwrite: true);
            foreach (var name in remove) _pendingRemovals.TryRemove(name, out _);
            return true;
        }
        catch (Exception ex)
        {
            // Read fine, write failed: locked by another program, read-only, disk full, no permission. Not the
            // same thing as "can't be read" — nothing about the file's content is wrong — so it doesn't set
            // _configLoadFailed, and the UI says "could not be saved" instead of "could not be read".
            LastConfigWriteProblem = ConfigWriteProblem.NotSaved;
            _log?.Warn("MCP", "[config] mcp_servers.json could not be saved — it is as it was", ex);
            try { OnServerLog?.Invoke("config", "mcp_servers.json could not be saved (locked, read-only or disk full?) — details in the Debug Log"); } catch { }
            try { File.Delete(ConfigPath + ".tmp"); } catch { }
            return false;
        }
    }

    /// <summary>Copy mcp_servers.json to mcp_servers.json.bak. A read-only flag the copy inherits (or an old
    /// .bak had) is cleared, or the next backup — and so the next save — would fail on it.</summary>
    private void BackUpConfigFile()
    {
        string bak = ConfigBackupPath;
        if (File.Exists(bak))
        {
            var a = File.GetAttributes(bak);
            if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(bak, a & ~FileAttributes.ReadOnly);
        }
        File.Copy(ConfigPath, bak, overwrite: true);
        var copied = File.GetAttributes(bak);
        if ((copied & FileAttributes.ReadOnly) != 0) File.SetAttributes(bak, copied & ~FileAttributes.ReadOnly);
    }

    /// <summary>Add or update a server configuration (written by the next <see cref="SaveConfig"/> naming it).</summary>
    public void SetConfig(string name, McpServerConfig config)
    {
        config.Name = name;
        config.Args ??= new List<string>();
        _configs[name] = config;
        _pendingRemovals.TryRemove(name, out _);
    }

    /// <summary>Remove a server configuration by name (deleted from the file by the next <see cref="SaveConfig"/> naming it).</summary>
    public bool RemoveConfig(string name)
    {
        if (!_configs.TryRemove(name, out _)) return false;
        _pendingRemovals[name] = 1;
        return true;
    }

    // ─── Reading mcp_servers.json ─────────────────────────────────────

    /// <summary>The file as read: its JSON tree (to merge into), and the server entries that could / couldn't be read.</summary>
    private sealed class ConfigSnapshot
    {
        public JsonObject Root { get; init; } = new();
        /// <summary>The "mcpServers" object, or null when the file has none yet.</summary>
        public JsonObject? Servers { get; init; }
        public Dictionary<string, McpServerConfig> Readable { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> UnreadableWhy { get; } = new(StringComparer.Ordinal);
        public IEnumerable<string> Unreadable => UnreadableWhy.Keys;
        /// <summary>The text has // or /* */ comments — a re-serialized write would lose them.</summary>
        public bool HasComments { get; init; }
    }

    /// <summary>Read the file (caller holds <see cref="_configFileLock"/>). Null = exists but can't be understood.</summary>
    private ConfigSnapshot? ReadConfigFile(out string? error)
    {
        error = null;
        string text;
        try
        {
            if (!File.Exists(ConfigPath)) return new ConfigSnapshot();
            text = File.ReadAllText(ConfigPath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        return ParseConfig(text, out error);
    }

    /// <summary>
    /// Parse mcp_servers.json text the tolerant way. Null when the text isn't a JSON object at all (or
    /// "mcpServers" isn't an object) — a file in that state must not be written over. A single server entry with
    /// a wrong type only makes THAT entry unreadable; the rest still load.
    /// </summary>
    private static ConfigSnapshot? ParseConfig(string text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return new ConfigSnapshot();   // empty file: nothing in it to lose
        try
        {
            if (JsonNode.Parse(text, documentOptions: ConfigDocOpts) is not JsonObject root)
            {
                error = "the file is not a JSON object";
                return null;
            }

            JsonObject? servers = null;
            var serversProp = FindCaseInsensitive(root, "mcpServers");
            if (serversProp is { } sp && sp.Value is not null)
            {
                if (sp.Value is not JsonObject so)
                {
                    error = "\"mcpServers\" is not an object";
                    return null;
                }
                servers = so;
            }

            var snap = new ConfigSnapshot { Root = root, Servers = servers, HasComments = ContainsComments(text) };
            if (servers == null) return snap;
            foreach (var (name, value) in servers)
            {
                if (value is not JsonObject)
                {
                    snap.UnreadableWhy[name] = "not an object";
                    continue;
                }
                try
                {
                    var cfg = value.Deserialize<McpServerConfig>(ConfigReadOpts);
                    if (cfg == null) { snap.UnreadableWhy[name] = "empty"; continue; }
                    cfg.Name = name;
                    cfg.Command ??= "";
                    cfg.Args ??= new List<string>();
                    snap.Readable[name] = cfg;
                }
                catch (Exception ex)
                {
                    snap.UnreadableWhy[name] = ex.Message;
                }
            }
            return snap;
        }
        catch (Exception ex)
        {
            // JsonException for broken JSON; ArgumentException for duplicate keys in one object.
            error = ex.Message;
            return null;
        }
    }

    /// <summary>True when JSON text that already parsed has a comment token in it (a "//" inside a string — a
    /// URL — is not one). Unsure = true: a needless backup costs nothing, a missing one costs the comments.</summary>
    private static bool ContainsComments(string text)
    {
        try
        {
            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(text),
                new JsonReaderOptions { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = true });
            while (reader.Read())
                if (reader.TokenType == JsonTokenType.Comment) return true;
            return false;
        }
        catch { return true; }
    }

    private static KeyValuePair<string, JsonNode?>? FindCaseInsensitive(JsonObject obj, string name)
    {
        if (obj.TryGetPropertyValue(name, out var exact)) return KeyValuePair.Create(name, exact);
        foreach (var p in obj)
            if (string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    private static void RemoveCaseInsensitive(JsonObject obj, string name)
    {
        foreach (var key in obj.Select(p => p.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
            obj.Remove(key);
    }

    /// <summary>Set one of the fields this app models. In place when present (the file keeps its order); a
    /// differently-cased spelling the reader accepted ("Command") is replaced by the canonical one.</summary>
    private static void SetKnown(JsonObject obj, string name, JsonNode? value)
    {
        foreach (var key in obj.Select(p => p.Key)
                     .Where(k => !string.Equals(k, name, StringComparison.Ordinal)
                              && string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
            obj.Remove(key);
        obj[name] = value;
    }

    /// <summary>Start all enabled MCP servers.</summary>
    public async Task StartAllEnabledAsync(CancellationToken ct = default)
    {
        foreach (var (name, config) in _configs)
        {
            if (!config.Enabled) continue;
            await StartServerAsync(name, ct);
        }
        if (_reloadPendingStart)
        {
            _reloadPendingStart = false;
            RaiseConfigStatusIfChanged();
        }
    }

    /// <summary>Start a specific MCP server by name.</summary>
    public async Task<bool> StartServerAsync(string name, CancellationToken ct = default)
    {
        if (!_configs.TryGetValue(name, out var config))
        {
            Log(name, "Server config not found");
            return false;
        }
        _startAttempted[name] = 1;

        // Serialize starts per server: the supervisor's respawn and a user-pressed
        // Restart can arrive at the same instant, and two winners means two live
        // launcher+worker pairs with only one of them reachable.
        var gate = _startGates.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await StartServerCoreAsync(name, config, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<bool> StartServerCoreAsync(string name, McpServerConfig config, CancellationToken ct)
    {
        // Stop existing transport if running. Supervision comes off FIRST so the
        // stop we are about to perform can't be mistaken for a crash.
        _supervised.TryRemove(name, out _);
        if (_transports.TryGetValue(name, out var existing))
        {
            await existing.StopAsync();
            existing.Dispose();
            _transports.TryRemove(name, out _);
        }

        SetState(name, McpServerState.Starting);

        try
        {
            Log(name, $"Starting: {config.Command} {string.Join(' ', config.Args)}");

            var transport = new McpStdioTransport();
            transport.OnServerError += msg => LogStderr(name, msg);
            transport.OnUnexpectedExit += () => HandleTransportExit(name, transport);
            transport.OnNotification += notif =>
            {
                if (notif.Method == "notifications/tools/list_changed")
                    _ = RefreshToolsAsync(name, CancellationToken.None);
            };

            // Start process and wait for read loop to be ready (prevents race condition)
            await transport.StartAsync(config);
            _transports[name] = transport;
            Log(name, "Process started. Sending initialize handshake...");

            // Initialize handshake (use 15s timeout instead of 30s default)
            var initParams = new McpInitializeParams
            {
                ClientInfo = new McpClientInfo
                {
                    Name = "CluadeX",
                    Version = AutoUpdateService.CurrentVersion,
                },
            };

            var initResponse = await transport.SendRequestAsync("initialize", initParams, timeoutMs: 15000, ct: ct);

            if (!initResponse.IsSuccess)
            {
                string errorMsg = initResponse.Error?.Message ?? "Unknown error";
                Log(name, $"Initialize failed: {errorMsg}");
                await transport.StopAsync();
                transport.Dispose();
                _transports.TryRemove(name, out _);
                SetState(name, McpServerState.Failed, errorMsg);
                return false;
            }

            // Send initialized notification
            await transport.SendNotificationAsync("notifications/initialized", ct: ct);

            Log(name, "Connected. Discovering tools...");

            // Discover tools
            await RefreshToolsAsync(name, ct);

            int toolCount = _toolRegistry.GetToolsForServer(name).Count;
            // Arm supervision only now: everything above is covered by the catch
            // blocks, and a half-started transport must not trigger a respawn race.
            _supervised[name] = 1;
            _readySinceUtc[name] = DateTime.UtcNow;
            _reconnectAttempts.TryRemove(name, out _);
            SetState(name, McpServerState.Ready);
            Log(name, $"Ready ({toolCount} tools)");
            return true;
        }
        catch (TimeoutException)
        {
            string stderrHint = "";
            if (_transports.TryGetValue(name, out var timedOut))
            {
                stderrHint = timedOut.LastStartupError.Trim();
                await timedOut.StopAsync();
                timedOut.Dispose();
                _transports.TryRemove(name, out _);
            }
            string detail = !string.IsNullOrEmpty(stderrHint)
                ? $"Handshake timed out. Server stderr:\n{stderrHint}"
                : "Handshake timed out — server did not respond to initialize request within 15s.";
            Log(name, detail);
            SetState(name, McpServerState.Failed, detail);
            return false;
        }
        catch (Exception ex)
        {
            Log(name, $"Failed to start: {ex.Message}");

            if (_transports.TryGetValue(name, out var failed))
            {
                await failed.StopAsync();
                failed.Dispose();
                _transports.TryRemove(name, out _);
            }

            SetState(name, McpServerState.Failed, ex.Message);
            return false;
        }
    }

    /// <summary>Stop a specific MCP server.</summary>
    public async Task StopServerAsync(string name)
    {
        _supervised.TryRemove(name, out _);
        // A Stop pressed between two respawn attempts ends the respawning: the loop used to wake up and start
        // the server again behind the user's back (and the chip went on saying "reconnecting").
        _reconnecting.TryRemove(name, out _);
        _reconnectAttempts.TryRemove(name, out _);
        // An explicit stop clears the crash-loop brake: the next start is the
        // user's decision, and a server they deliberately restarted deserves the
        // full budget again rather than inheriting a verdict from before the fix.
        _rapidCrashes.TryRemove(name, out _);
        _readySinceUtc.TryRemove(name, out _);
        if (_transports.TryGetValue(name, out var transport))
        {
            await transport.StopAsync();
            transport.Dispose();
            _transports.TryRemove(name, out _);
            _toolRegistry.RemoveServer(name);
            OnToolsChanged?.Invoke();
            Log(name, "Stopped");
        }
        SetState(name, McpServerState.Stopped);
    }

    // ─── Supervision ──────────────────────────────────────────────────

    /// <summary>
    /// A server process died without being asked to. Retire its tools immediately
    /// — leaving them in the registry is what let the local model keep picking
    /// brain_search out of a dead server's catalogue — then respawn with backoff.
    /// </summary>
    private void HandleTransportExit(string name, McpStdioTransport dead)
    {
        if (_disposed) return;
        // Not supervised (start never completed), or already replaced — either way
        // someone else owns this transport's fate.
        if (!_supervised.ContainsKey(name)) return;
        if (!_transports.TryGetValue(name, out var current) || !ReferenceEquals(current, dead)) return;

        _supervised.TryRemove(name, out _);
        _transports.TryRemove(name, out _);
        _toolRegistry.RemoveServer(name);
        try { OnToolsChanged?.Invoke(); } catch { }

        string stderr = dead.LastStartupError.Trim();
        string detail = string.IsNullOrEmpty(stderr)
            ? "Server process exited unexpectedly."
            : $"Server process exited unexpectedly. Last stderr:\n{stderr}";
        _log?.Warn("MCP", $"[{name}] {detail}");
        try { dead.Dispose(); } catch { }

        // Crash-loop brake. A server that dies soon after every successful
        // handshake would otherwise reset the reconnect counter on each cycle and
        // respawn forever — see MaxRapidCrashes.
        var upFor = _readySinceUtc.TryGetValue(name, out var since)
            ? DateTime.UtcNow - since
            : TimeSpan.MaxValue;
        int rapid = upFor < RapidCrashWindow
            ? _rapidCrashes.AddOrUpdate(name, 1, (_, v) => v + 1)
            : _rapidCrashes[name] = 0;

        if (rapid >= MaxRapidCrashes)
        {
            // Shown to the user (chip tooltip, MCP page log), so localized; the debug log gets the same line.
            string give = string.Format(L("mcp.supervisor.crashLoop"), rapid, RapidCrashWindow.TotalSeconds.ToString("F0"), RestartRoute(name));
            _log?.Error("MCP", $"[{name}] {give}");
            SetState(name, McpServerState.Failed, $"{detail}\n\n{give}");
            try { OnServerLog?.Invoke(name, give); } catch { }
            return;
        }

        // Marked before the state flips, so nobody sees "Reconnecting" without the loop that owns it.
        _reconnecting[name] = 1;
        SetState(name, McpServerState.Reconnecting, detail);
        try { OnServerLog?.Invoke(name, detail); } catch { }

        _ = Task.Run(() => ReconnectLoopAsync(name));
    }

    /// <summary>
    /// Respawn a crashed server: 1s, 2s, 4s, 8s, 16s. Bounded because an endless
    /// loop against a genuinely broken binary is just a quieter kind of hang —
    /// after the last attempt the state goes Failed and the chip says DOWN.
    /// </summary>
    private async Task ReconnectLoopAsync(string name)
    {
        try
        {
            for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
            {
                _reconnectAttempts[name] = attempt;
                RaiseBrainStatus();

                try { await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1))); } catch { return; }

                if (_disposed) return;
                if (!_reconnecting.ContainsKey(name)) return;   // the user pressed Stop in the gap
                if (!_configs.TryGetValue(name, out var cfg) || !cfg.Enabled)
                {
                    _reconnecting.TryRemove(name, out _);
                    SetState(name, McpServerState.Stopped);
                    return;
                }
                if (IsServerRunning(name)) return;   // the user restarted it first

                Log(name, $"Reconnect attempt {attempt}/{MaxReconnectAttempts}…");
                bool ok;
                try { ok = await StartServerAsync(name); }
                catch (Exception ex) { Log(name, $"Reconnect attempt {attempt} threw: {ex.Message}"); ok = false; }
                if (ok)
                {
                    Log(name, $"Reconnected after {attempt} attempt(s)");
                    return;
                }
            }

            string give = string.Format(L("mcp.supervisor.gaveUp"), MaxReconnectAttempts, RestartRoute(name));
            _log?.Error("MCP", $"[{name}] {give}");
            _reconnecting.TryRemove(name, out _);
            SetState(name, McpServerState.Failed, give);
            try { OnServerLog?.Invoke(name, give); } catch { }
        }
        finally
        {
            _reconnectAttempts.TryRemove(name, out _);
            if (_reconnecting.TryRemove(name, out _)) RaiseBrainStatus();
        }
    }

    /// <summary>Call a tool on a specific server.</summary>
    public async Task<McpToolResult> CallToolAsync(string serverName, string toolName, Dictionary<string, string> arguments, CancellationToken ct = default)
    {
        // Convert string args back to typed args (int / double / bool / array / object / string).
        // The agent dispatch path flattens every tool argument to a string (ToolCall.Arguments is
        // Dictionary<string,string>), so an array/object argument arrived here as its JSON TEXT.
        // Without the array/object branch below the server received a quoted string like
        // "[\"a\",\"b\"]" where it expected a real array, and rejected the call — which made every
        // MCP tool with a non-scalar parameter unusable from the agent.
        // The tool's own schema decides first: a property declared "string" stays a string, so a search
        // for "2026" or "true" is not sent as a number / bool that the server rejects.
        JsonElement? props = null;
        var tool = _toolRegistry.ResolveTool(toolName);
        if (tool?.ServerName == serverName && tool.InputSchema is { ValueKind: JsonValueKind.Object } schema
            && schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object)
            props = p;

        var argsDict = new Dictionary<string, object>();
        foreach (var (key, value) in arguments)
        {
            string v = value ?? "";
            if (props is { } pp && pp.TryGetProperty(key, out var decl)
                && decl.TryGetProperty("type", out var dt) && dt.ValueKind == JsonValueKind.String && dt.GetString() == "string")
            {
                argsDict[key] = v;
                continue;
            }
            string t = v.TrimStart();
            if (t.Length > 0 && (t[0] == '[' || t[0] == '{'))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(v);
                    argsDict[key] = JsonElementToObject(doc.RootElement) ?? v;
                    continue;
                }
                catch { /* not valid JSON — fall through and send it as a plain string */ }
            }
            if (int.TryParse(v, out int intVal))
                argsDict[key] = intVal;
            else if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out double dblVal))
                argsDict[key] = dblVal;
            else if (bool.TryParse(v, out bool boolVal))
                argsDict[key] = boolVal;
            else
                argsDict[key] = v;
        }
        return await CallToolWithObjectArgsAsync(serverName, toolName, argsDict, ct);
    }

    /// <summary>Materialize a JsonElement into plain CLR objects so it re-serializes
    /// as real JSON (array/object/number/bool/null) rather than as a quoted string.</summary>
    private static object? JsonElementToObject(System.Text.Json.JsonElement el)
    {
        switch (el.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in el.EnumerateArray()) list.Add(JsonElementToObject(item));
                return list;
            case System.Text.Json.JsonValueKind.Object:
                var map = new Dictionary<string, object?>();
                foreach (var p in el.EnumerateObject()) map[p.Name] = JsonElementToObject(p.Value);
                return map;
            case System.Text.Json.JsonValueKind.String:
                return el.GetString();
            case System.Text.Json.JsonValueKind.Number:
                return el.TryGetInt64(out long l) ? l : el.GetDouble();
            case System.Text.Json.JsonValueKind.True: return true;
            case System.Text.Json.JsonValueKind.False: return false;
            default: return null;
        }
    }

    /// <summary>
    /// Object-typed overload — use when an argument must reach the server
    /// as a JSON array / object / strong type, not a string. Fixes the
    /// audit CRITICAL #4 issue where `Dictionary&lt;string,string&gt;` forced
    /// every value through string parsing.
    /// </summary>
    public async Task<McpToolResult> CallToolWithObjectArgsAsync(
        string serverName, string toolName, Dictionary<string, object> arguments,
        CancellationToken ct = default)
    {
        if (!_transports.TryGetValue(serverName, out var transport) || !transport.IsAlive)
        {
            // Say WHICH of the several "not running" situations this is — the bare
            // message sent the model (and the human) hunting for a config problem
            // when the real answer was "it crashed and is coming back in 4s".
            // For the model, in English. Its OWN last error (this used to quote the brain's for any server), and
            // the 🧠 chip only as a route for the brain.
            var state = GetServerState(serverName);
            string route = LooksLikeBrain(serverName) ? "Ctrl+K → MCP Servers, or click the 🧠 chip" : "Ctrl+K → MCP Servers";
            string lastError = _lastErrors.TryGetValue(serverName, out var le) ? le : "no detail";
            string hint = state switch
            {
                McpServerState.Reconnecting => "it crashed and is being restarted — retry in a few seconds",
                McpServerState.Failed       => $"it failed to start ({lastError}); restart it from MCP Servers ({route})",
                McpServerState.Starting     => "it is still starting up — retry in a few seconds",
                _                           => $"it is stopped; start it from MCP Servers ({route})",
            };
            throw new InvalidOperationException($"MCP server '{serverName}' is not running: {hint}");
        }

        var callParams = new { name = toolName, arguments = arguments };
        // 3 minutes, not the transport's 30 s default: brain_synthesize, media_generate and friends routinely run
        // past 30 s, and a timeout there reported a tool that was still working as failed. The user's Stop
        // (ct) still ends it at once.
        var response = await transport.SendRequestAsync("tools/call", callParams, timeoutMs: 180_000, ct: ct);

        if (!response.IsSuccess)
        {
            return new McpToolResult
            {
                IsError = true,
                Content = new List<McpContentItem>
                {
                    new() { Type = "text", Text = response.Error?.Message ?? "Unknown error" }
                }
            };
        }

        // Parse tool result
        try
        {
            if (response.Result.HasValue)
            {
                var result = JsonSerializer.Deserialize<McpToolResult>(response.Result.Value.GetRawText(), JsonOpts);
                return result ?? new McpToolResult { IsError = true, Content = new() { new McpContentItem { Type = "text", Text = "Empty result" } } };
            }
        }
        catch { }

        return new McpToolResult
        {
            Content = new() { new McpContentItem { Type = "text", Text = response.Result?.GetRawText() ?? "" } }
        };
    }

    /// <summary>Refresh tool list for a specific server.</summary>
    private async Task RefreshToolsAsync(string name, CancellationToken ct)
    {
        if (!_transports.TryGetValue(name, out var transport) || !transport.IsAlive) return;

        try
        {
            var tools = new List<McpTool>();
            string? cursor = null;
            bool listed = false;
            // tools/list is paginated (MCP 2025-03+): a server with many tools returns nextCursor, and reading only
            // the first page silently dropped the rest. Bounded so a server that loops its cursor can't hang us.
            for (int page = 0; page < 20; page++)
            {
                var response = await transport.SendRequestAsync("tools/list",
                    cursor == null ? new { } : (object)new { cursor }, ct: ct);
                cursor = null;
                if (!response.IsSuccess || !response.Result.HasValue) break;
                listed = true;

                // Guard: only call TryGetProperty on Object-kind JsonElements, and ensure
                // the 'tools' field is actually an array before enumerating. Malformed MCP
                // servers can return unexpected shapes — prior code could throw here.
                var result = response.Result.Value;
                if (result.ValueKind != JsonValueKind.Object) break;
                if (result.TryGetProperty("tools", out var toolsArray) && toolsArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var toolElem in toolsArray.EnumerateArray())
                    {
                        var tool = new McpTool
                        {
                            Name = toolElem.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                            Description = toolElem.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                            Title = toolElem.TryGetProperty("title", out var t) ? t.GetString() : null,
                            // Clone: the element must outlive this page's response document.
                            InputSchema = toolElem.TryGetProperty("inputSchema", out var s) ? s.Clone() : null,
                            ServerName = name,
                        };

                        if (!string.IsNullOrEmpty(tool.Name))
                            tools.Add(tool);
                    }
                }
                if (result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String
                    && next.GetString() is { Length: > 0 } nc)
                    cursor = nc;
                if (cursor == null) break;
            }

            if (listed)
            {
                _toolRegistry.UpdateServer(name, tools);
                OnToolsChanged?.Invoke();
                RaiseBrainStatus();   // the chip shows the tool count
            }
        }
        catch (Exception ex)
        {
            Log(name, $"Failed to list tools: {ex.Message}");
        }
    }

    /// <summary>Check if a server is running.</summary>
    public bool IsServerRunning(string name)
        => _transports.TryGetValue(name, out var t) && t.IsAlive;

    /// <summary>Get names of all running servers.</summary>
    public List<string> GetRunningServers()
        => _transports.Where(t => t.Value.IsAlive).Select(t => t.Key).ToList();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Disarm supervision BEFORE killing anything, or app shutdown looks like
        // five crashes and the reconnect loops race the process exit.
        _supervised.Clear();

        // Take a snapshot so concurrent mutation during shutdown is safe.
        var snapshot = _transports.Values.ToList();
        _transports.Clear();

        // Shut down all servers in parallel, bounded total timeout.
        // Previously we waited up to 5s per server sequentially — with N servers this
        // could block the UI shutdown for 5N seconds.
        try
        {
            var stopTasks = snapshot.Select(t => t.StopAsync()).ToArray();
            Task.WaitAll(stopTasks, 5000);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MCP shutdown error: {ex.Message}");
        }

        // Dispose unconditionally — Dispose() kills the process if StopAsync didn't finish.
        foreach (var t in snapshot)
        {
            try { t.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"MCP dispose error: {ex.Message}"); }
        }

        foreach (var gate in _startGates.Values)
        {
            try { gate.Dispose(); } catch { }
        }
        _startGates.Clear();
    }
}
