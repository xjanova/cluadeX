using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
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
public sealed class McpServerManager : INotifyPropertyChanged, IDisposable
{
    /// <summary>How many times to respawn a server that died on its own before giving up.</summary>
    private const int MaxReconnectAttempts = 5;

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
    // One start at a time per server, or a user-pressed Restart racing the
    // supervisor leaks an orphan process pair.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _startGates = new();
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public event Action? OnToolsChanged;
    public event Action<string, string>? OnServerLog; // serverName, message
    /// <summary>serverName, new state — raised on every lifecycle transition.</summary>
    public event Action<string, McpServerState>? OnServerStateChanged;
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

    /// <summary>Lifecycle state of the brain server (Stopped when none is configured).</summary>
    public McpServerState BrainState
    {
        get
        {
            var name = BrainServerName;
            return name != null && _states.TryGetValue(name, out var s) ? s : McpServerState.Stopped;
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

    /// <summary>Short label for the chip: "ready · 83 tools", "reconnecting 2/5", …</summary>
    public string BrainStatusText => BrainState switch
    {
        McpServerState.Ready        => $"brain · {BrainToolCount} tools",
        McpServerState.Starting     => "brain · connecting…",
        McpServerState.Reconnecting => $"brain · reconnecting {_reconnectAttempt}/{MaxReconnectAttempts}",
        McpServerState.Failed       => "brain · DOWN",
        _                           => BrainServerName == null ? "brain · not configured" : "brain · stopped",
    };

    /// <summary>Last error seen on the brain server — the tooltip's whole point.</summary>
    public string? BrainLastError { get; private set; }

    private int _reconnectAttempt;

    private void RaiseBrainStatus()
    {
        foreach (var p in new[] { nameof(BrainState), nameof(IsBrainConnected), nameof(BrainToolCount),
                                  nameof(BrainStatusText), nameof(BrainLastError), nameof(BrainServerName) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>Record a lifecycle transition and tell everyone who is bound to it.</summary>
    private void SetState(string name, McpServerState state, string? error = null)
    {
        _states[name] = state;
        if (error != null || state == McpServerState.Ready) BrainLastError = error;
        try { OnServerStateChanged?.Invoke(name, state); } catch { }
        RaiseBrainStatus();
    }

    /// <summary>Current state of any configured server.</summary>
    public McpServerState GetServerState(string name)
        => _states.TryGetValue(name, out var s) ? s : McpServerState.Stopped;

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

    private void LogStderr(string name, string message)
    {
        _log?.Debug("MCP", $"[{name}] {message}");
        try { OnServerLog?.Invoke(name, $"[stderr] {message}"); } catch { }
    }

    /// <summary>Path to the MCP config file.</summary>
    private string ConfigPath => Path.Combine(_settingsService.DataRoot, "mcp_servers.json");

    /// <summary>Load config and start all enabled servers.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        LoadConfig();
        await StartAllEnabledAsync(ct);
    }

    /// <summary>Load server configurations from disk.</summary>
    public void LoadConfig()
    {
        _configs.Clear();

        if (!File.Exists(ConfigPath))
        {
            // Create default empty config
            var defaultConfig = new McpConfigFile();
            string json = JsonSerializer.Serialize(defaultConfig, JsonOpts);
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, json);
            return;
        }

        try
        {
            string json = File.ReadAllText(ConfigPath);
            var configFile = JsonSerializer.Deserialize<McpConfigFile>(json, JsonOpts);
            if (configFile?.McpServers != null)
            {
                foreach (var (name, config) in configFile.McpServers)
                {
                    config.Name = name;
                    _configs[name] = config;
                }
            }
        }
        catch (Exception ex)
        {
            Log("config", $"Failed to load MCP config: {ex.Message}");
        }
    }

    /// <summary>Save current config to disk.</summary>
    public void SaveConfig()
    {
        try
        {
            var configFile = new McpConfigFile();
            foreach (var (name, config) in _configs)
                configFile.McpServers[name] = config;

            string json = JsonSerializer.Serialize(configFile, JsonOpts);
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            Log("config", $"Failed to save MCP config: {ex.Message}");
        }
    }

    /// <summary>Add or update a server configuration.</summary>
    public void SetConfig(string name, McpServerConfig config)
    {
        config.Name = name;
        _configs[name] = config;
    }

    /// <summary>Remove a server configuration by name.</summary>
    public bool RemoveConfig(string name) => _configs.TryRemove(name, out _);

    /// <summary>Start all enabled MCP servers.</summary>
    public async Task StartAllEnabledAsync(CancellationToken ct = default)
    {
        foreach (var (name, config) in _configs)
        {
            if (!config.Enabled) continue;
            await StartServerAsync(name, ct);
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
            _reconnectAttempt = 0;
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
        SetState(name, McpServerState.Reconnecting, detail);
        try { OnServerLog?.Invoke(name, detail); } catch { }
        try { dead.Dispose(); } catch { }

        _ = Task.Run(() => ReconnectLoopAsync(name));
    }

    /// <summary>
    /// Respawn a crashed server: 1s, 2s, 4s, 8s, 16s. Bounded because an endless
    /// loop against a genuinely broken binary is just a quieter kind of hang —
    /// after the last attempt the state goes Failed and the chip says DOWN.
    /// </summary>
    private async Task ReconnectLoopAsync(string name)
    {
        for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            _reconnectAttempt = attempt;
            RaiseBrainStatus();

            try { await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1))); } catch { return; }

            if (_disposed) return;
            if (!_configs.TryGetValue(name, out var cfg) || !cfg.Enabled)
            {
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

        string give = $"Gave up after {MaxReconnectAttempts} reconnect attempts — restart it from MCP Servers (Ctrl+5).";
        _log?.Error("MCP", $"[{name}] {give}");
        SetState(name, McpServerState.Failed, give);
        try { OnServerLog?.Invoke(name, give); } catch { }
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
        var argsDict = new Dictionary<string, object>();
        foreach (var (key, value) in arguments)
        {
            string v = value ?? "";
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
            var state = GetServerState(serverName);
            string hint = state switch
            {
                McpServerState.Reconnecting => "it crashed and is being restarted — retry in a few seconds",
                McpServerState.Failed       => $"it failed to start ({BrainLastError ?? "no detail"}); restart it from MCP Servers (Ctrl+5)",
                McpServerState.Starting     => "it is still starting up — retry in a few seconds",
                _                           => "it is stopped; start it from MCP Servers (Ctrl+5)",
            };
            throw new InvalidOperationException($"MCP server '{serverName}' is not running: {hint}");
        }

        var callParams = new { name = toolName, arguments = arguments };
        var response = await transport.SendRequestAsync("tools/call", callParams, ct: ct);

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
            var response = await transport.SendRequestAsync("tools/list", new { }, ct: ct);

            if (response.IsSuccess && response.Result.HasValue)
            {
                var tools = new List<McpTool>();

                // Guard: only call TryGetProperty on Object-kind JsonElements, and ensure
                // the 'tools' field is actually an array before enumerating. Malformed MCP
                // servers can return unexpected shapes — prior code could throw here.
                if (response.Result.Value.ValueKind == JsonValueKind.Object
                    && response.Result.Value.TryGetProperty("tools", out var toolsArray)
                    && toolsArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var toolElem in toolsArray.EnumerateArray())
                    {
                        var tool = new McpTool
                        {
                            Name = toolElem.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                            Description = toolElem.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                            Title = toolElem.TryGetProperty("title", out var t) ? t.GetString() : null,
                            InputSchema = toolElem.TryGetProperty("inputSchema", out var s) ? s : null,
                            ServerName = name,
                        };

                        if (!string.IsNullOrEmpty(tool.Name))
                            tools.Add(tool);
                    }
                }

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
