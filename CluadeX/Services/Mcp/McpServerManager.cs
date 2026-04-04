using System.IO;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Mcp;

/// <summary>
/// Manages MCP server lifecycles: load config, start/stop servers,
/// discover tools, handle reconnection.
/// </summary>
public sealed class McpServerManager : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly McpToolRegistry _toolRegistry;
    private readonly Dictionary<string, McpStdioTransport> _transports = new();
    private readonly Dictionary<string, McpServerConfig> _configs = new();
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public event Action? OnToolsChanged;
    public event Action<string, string>? OnServerLog; // serverName, message

    public McpToolRegistry ToolRegistry => _toolRegistry;
    public IReadOnlyDictionary<string, McpServerConfig> Configs => _configs;

    public McpServerManager(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _toolRegistry = new McpToolRegistry();
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
            OnServerLog?.Invoke("config", $"Failed to load MCP config: {ex.Message}");
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
            OnServerLog?.Invoke("config", $"Failed to save MCP config: {ex.Message}");
        }
    }

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
            OnServerLog?.Invoke(name, "Server config not found");
            return false;
        }

        // Stop existing transport if running
        if (_transports.TryGetValue(name, out var existing))
        {
            await existing.StopAsync();
            existing.Dispose();
            _transports.Remove(name);
        }

        try
        {
            OnServerLog?.Invoke(name, $"Starting: {config.Command} {string.Join(' ', config.Args)}");

            var transport = new McpStdioTransport();
            transport.OnServerError += msg => OnServerLog?.Invoke(name, $"[stderr] {msg}");
            transport.OnNotification += notif =>
            {
                if (notif.Method == "notifications/tools/list_changed")
                    _ = RefreshToolsAsync(name, CancellationToken.None);
            };

            transport.Start(config);
            _transports[name] = transport;

            // Initialize handshake
            var initParams = new McpInitializeParams
            {
                ClientInfo = new McpClientInfo
                {
                    Name = "CluadeX",
                    Version = AutoUpdateService.CurrentVersion,
                },
            };

            var initResponse = await transport.SendRequestAsync("initialize", initParams, ct: ct);

            if (!initResponse.IsSuccess)
            {
                OnServerLog?.Invoke(name, $"Initialize failed: {initResponse.Error?.Message}");
                await transport.StopAsync();
                transport.Dispose();
                _transports.Remove(name);
                return false;
            }

            // Send initialized notification
            await transport.SendNotificationAsync("notifications/initialized", ct: ct);

            OnServerLog?.Invoke(name, "Connected. Discovering tools...");

            // Discover tools
            await RefreshToolsAsync(name, ct);

            OnServerLog?.Invoke(name, $"Ready ({_toolRegistry.GetToolsForServer(name).Count} tools)");
            return true;
        }
        catch (Exception ex)
        {
            OnServerLog?.Invoke(name, $"Failed to start: {ex.Message}");

            if (_transports.TryGetValue(name, out var failed))
            {
                await failed.StopAsync();
                failed.Dispose();
                _transports.Remove(name);
            }

            return false;
        }
    }

    /// <summary>Stop a specific MCP server.</summary>
    public async Task StopServerAsync(string name)
    {
        if (_transports.TryGetValue(name, out var transport))
        {
            await transport.StopAsync();
            transport.Dispose();
            _transports.Remove(name);
            _toolRegistry.RemoveServer(name);
            OnToolsChanged?.Invoke();
            OnServerLog?.Invoke(name, "Stopped");
        }
    }

    /// <summary>Call a tool on a specific server.</summary>
    public async Task<McpToolResult> CallToolAsync(string serverName, string toolName, Dictionary<string, string> arguments, CancellationToken ct = default)
    {
        if (!_transports.TryGetValue(serverName, out var transport) || !transport.IsAlive)
            throw new InvalidOperationException($"MCP server '{serverName}' is not running");

        // Convert string args to JsonElement for proper typing
        var argsDict = new Dictionary<string, object>();
        foreach (var (key, value) in arguments)
        {
            // Try to parse as number or bool, otherwise string
            if (int.TryParse(value, out int intVal))
                argsDict[key] = intVal;
            else if (double.TryParse(value, out double dblVal))
                argsDict[key] = dblVal;
            else if (bool.TryParse(value, out bool boolVal))
                argsDict[key] = boolVal;
            else
                argsDict[key] = value;
        }

        var callParams = new { name = toolName, arguments = argsDict };
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

                if (response.Result.Value.TryGetProperty("tools", out var toolsArray))
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
            }
        }
        catch (Exception ex)
        {
            OnServerLog?.Invoke(name, $"Failed to list tools: {ex.Message}");
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

        foreach (var (_, transport) in _transports)
        {
            transport.StopAsync().Wait(5000);
            transport.Dispose();
        }
        _transports.Clear();
    }
}
