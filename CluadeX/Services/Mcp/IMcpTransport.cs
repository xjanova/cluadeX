using CluadeX.Models;

namespace CluadeX.Services.Mcp;

/// <summary>
/// One live connection to an MCP server, whatever carries it: a child process on stdio
/// (<see cref="McpStdioTransport"/>) or a server on the network over Streamable HTTP (<see cref="McpHttpTransport"/>).
/// The manager drives both the same way — start, initialize, requests, stop — and supervises both.
/// </summary>
public interface IMcpTransport : IDisposable
{
    bool IsAlive { get; }

    /// <summary>Why the start failed, or the server's last words (stderr for a process, the last error for HTTP).</summary>
    string LastStartupError { get; }

    /// <summary>Diagnostic lines (a process's stderr, an HTTP transport's failures).</summary>
    event Action<string>? OnServerError;

    /// <summary>Server → client notifications.</summary>
    event Action<JsonRpcNotification>? OnNotification;

    /// <summary>The connection was lost without being asked to stop. Never fires for a stop we initiated.</summary>
    event Action? OnUnexpectedExit;

    Task StartAsync(McpServerConfig config);
    Task<JsonRpcResponse> SendRequestAsync(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken ct = default);
    Task SendNotificationAsync(string method, object? parameters = null, CancellationToken ct = default);
    Task StopAsync();
}
