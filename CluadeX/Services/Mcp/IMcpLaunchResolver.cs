using CluadeX.Models;

namespace CluadeX.Services.Mcp;

/// <summary>
/// Decides how a server is really launched, right before every start — the first start, a user's Restart and
/// every supervisor respawn. Used for entries CluadeX manages itself (<see cref="McpServerConfig.ManagedBy"/>),
/// whose executable moves (an app update replaces a versioned folder) and so must be found again each time and
/// never written to mcp_servers.json. Register in DI as <c>IMcpLaunchResolver</c>.
/// </summary>
public interface IMcpLaunchResolver
{
    /// <summary>
    /// Null = not this resolver's server (or nothing to change). Otherwise the command line to spawn instead of
    /// the configured one, or a refusal that keeps the server from starting. Called with the start gate held, so
    /// keep it to a few seconds; it must not throw (a throw fails the start with a generic message).
    /// </summary>
    Task<McpLaunchResolution?> ResolveAsync(string serverName, McpServerConfig configured, CancellationToken ct);
}

/// <summary>A resolver's answer for one start.</summary>
public sealed record McpLaunchResolution
{
    /// <summary>What to spawn (command/args/env) — used for this start only, never persisted. Null = as configured.</summary>
    public McpServerConfig? Launch { get; init; }

    /// <summary>Non-null = do not spawn: the server goes to Failed with this localized, user-facing reason.</summary>
    public string? Refusal { get; init; }

    public static McpLaunchResolution Use(McpServerConfig launch) => new() { Launch = launch };
    public static McpLaunchResolution Refuse(string reason) => new() { Refusal = reason };
}
