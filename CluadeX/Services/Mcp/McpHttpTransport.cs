using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services.Mcp;

/// <summary>
/// An MCP server on the network, reached over Streamable HTTP — an mcp_servers.json entry with "url" instead of
/// "command" (the shape Claude Code and Cursor write: <c>{"type": "http", "url": …, "headers": {…}}</c>).
///
/// Unlike a pipe, HTTP exchanges are self-delimiting: a timeout throws and leaves the connection usable. Only proof
/// that the session itself is gone — the 404 the spec reserves for an expired session id — counts as the connection
/// dying, and hands it to the manager's supervisor, which starts it again (a fresh initialize, a fresh session).
/// </summary>
public sealed class McpHttpTransport : IMcpTransport
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Regex Placeholder = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    private readonly HttpMessageHandler? _handler;
    private McpHttpClient? _client;
    private int _requestId;
    private volatile bool _alive;
    private volatile bool _stopRequested;
    private string _lastError = "";

    /// <param name="handler">For the harness: a fake server. Null = the real network.</param>
    public McpHttpTransport(HttpMessageHandler? handler = null) => _handler = handler;

    public bool IsAlive => _alive;
    public string LastStartupError => _lastError;
    public event Action<string>? OnServerError;
    public event Action<JsonRpcNotification>? OnNotification;
    public event Action? OnUnexpectedExit;

    /// <summary>
    /// The headers to send: each value with <c>${NAME}</c> replaced from the entry's "env" block, then from this
    /// process's environment — so a key can stay out of the file. A placeholder that resolves to nothing is an error
    /// (sending "Bearer " would only earn a confusing 401).
    /// </summary>
    public static Dictionary<string, string> ResolveHeaders(McpServerConfig config, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, raw) in config.Headers ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string value = Placeholder.Replace(raw ?? "", m =>
            {
                string key = m.Groups[1].Value;
                string? v = config.Env != null && config.Env.TryGetValue(key, out var fromEntry) ? fromEntry : environment(key);
                if (string.IsNullOrEmpty(v)) throw new InvalidOperationException($"header '{name}' needs ${{{key}}}, which is not set (add it to the entry's \"env\" or to Windows' environment variables)");
                return v;
            });
            if (value.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new InvalidOperationException($"header '{name}' contains a line break");
            resolved[name.Trim()] = value;
        }
        return resolved;
    }

    public Task StartAsync(McpServerConfig config)
    {
        _stopRequested = false;
        _lastError = "";
        try
        {
            if (!Uri.TryCreate(config.Url?.Trim(), UriKind.Absolute, out var uri) || !McpHttpClient.IsAllowedEndpoint(uri, out _))
            {
                McpHttpClient.IsAllowedEndpoint(uri, out string? why);
                throw new InvalidOperationException($"Cannot use url '{config.Url}': {why}");
            }
            _client?.Dispose();
            _client = new McpHttpClient(uri, ResolveHeaders(config), _handler);
            _alive = true;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            throw new InvalidOperationException(ex.Message, ex);
        }
        return Task.CompletedTask;
    }

    public async Task<JsonRpcResponse> SendRequestAsync(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken ct = default)
    {
        var client = _client;
        if (!_alive || client == null) throw new InvalidOperationException("MCP server not running");

        int id = Interlocked.Increment(ref _requestId);
        string json = JsonSerializer.Serialize(new JsonRpcRequest { Id = id, Method = method, Params = parameters }, JsonOpts);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        IReadOnlyList<string> messages;
        try
        {
            messages = await client.SendAsync(json, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"MCP request '{method}' timed out after {timeoutMs}ms");
        }
        catch (McpSessionExpiredException)
        {
            Lost("the server's session expired — reconnecting");
            throw new IOException("MCP server session expired");
        }
        catch (Exception ex) when (ex is HttpRequestException or McpUnauthorizedException)
        {
            _lastError = ex.Message;
            OnServerError?.Invoke($"[{method}] {ex.Message}");
            throw new InvalidOperationException(ex.Message, ex);
        }

        JsonRpcResponse? answer = null;
        foreach (var m in messages)
        {
            try
            {
                using var doc = JsonDocument.Parse(m);
                var root = doc.RootElement;
                bool hasMethod = root.TryGetProperty("method", out _);
                if (!hasMethod && root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                    && idEl.TryGetInt32(out int got) && got == id)
                {
                    answer = JsonSerializer.Deserialize<JsonRpcResponse>(m, JsonOpts);
                }
                else if (hasMethod && !root.TryGetProperty("id", out _))
                {
                    var n = JsonSerializer.Deserialize<JsonRpcNotification>(m, JsonOpts);
                    if (n != null) OnNotification?.Invoke(n);
                }
            }
            catch (JsonException) { OnServerError?.Invoke($"[malformed response]: {m[..Math.Min(200, m.Length)]}"); }
        }
        return answer ?? throw new IOException($"MCP server answered '{method}' without a response for id {id}");
    }

    public async Task SendNotificationAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        var client = _client;
        if (!_alive || client == null) return;
        string json = JsonSerializer.Serialize(new JsonRpcNotification { Method = method, Params = parameters }, JsonOpts);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(15_000);
            await client.SendAsync(json, cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A notification has no answer to wait for; failing to deliver one is worth a log line, nothing more.
            OnServerError?.Invoke($"[{method}] not delivered: {ex.Message}");
        }
    }

    private void Lost(string why)
    {
        if (!_alive) return;
        _alive = false;
        _lastError = why;
        OnServerError?.Invoke(why);
        if (!_stopRequested)
        {
            try { OnUnexpectedExit?.Invoke(); } catch { }
        }
    }

    public async Task StopAsync()
    {
        _stopRequested = true;
        _alive = false;
        if (_client is { } c)
        {
            await c.CloseAsync();
            c.Dispose();
            _client = null;
        }
    }

    public void Dispose()
    {
        _stopRequested = true;
        _alive = false;
        try { _client?.Dispose(); } catch { }
        _client = null;
    }
}
