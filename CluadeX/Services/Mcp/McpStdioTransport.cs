using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Mcp;

/// <summary>
/// JSON-RPC 2.0 transport over stdio (newline-delimited JSON).
/// Manages a subprocess that speaks the MCP protocol.
/// </summary>
public sealed class McpStdioTransport : IDisposable
{
    private Process? _process;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private int _requestId;
    private bool _disposed;
    private readonly Dictionary<int, TaskCompletionSource<JsonRpcResponse>> _pending = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _readCts;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public bool IsAlive => _process != null && !_process.HasExited;
    public event Action<string>? OnServerError; // stderr output
    public event Action<JsonRpcNotification>? OnNotification; // server→client notifications

    /// <summary>Collected stderr lines during startup for error diagnosis.</summary>
    public string LastStartupError { get; private set; } = "";

    /// <summary>Start the MCP server process and wait until the read loop is ready.</summary>
    public async Task StartAsync(McpServerConfig config)
    {
        if (IsAlive) return;

        LastStartupError = "";

        var psi = new ProcessStartInfo
        {
            FileName = config.Command,
            Arguments = string.Join(' ', config.Args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Set environment variables
        if (config.Env != null)
        {
            foreach (var (key, value) in config.Env)
                psi.EnvironmentVariables[key] = value;
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += (_, _) => HandleProcessExit();

        try
        {
            _process.Start();
        }
        catch (Exception ex)
        {
            LastStartupError = $"Cannot start process: {ex.Message}";
            _process.Dispose();
            _process = null;
            throw new InvalidOperationException(LastStartupError, ex);
        }

        _writer = _process.StandardInput;
        _writer.AutoFlush = true;
        _reader = _process.StandardOutput;

        // Read stderr in background — capture early errors for diagnosis
        _ = Task.Run(async () =>
        {
            try
            {
                while (_process is { HasExited: false })
                {
                    string? line = await _process.StandardError.ReadLineAsync();
                    if (line != null)
                    {
                        LastStartupError += line + "\n";
                        OnServerError?.Invoke(line);
                    }
                }
            }
            catch { }
        });

        // Start reading responses — use a signal to ensure it's ready before returning
        _readCts = new CancellationTokenSource();
        var readLoopReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => ReadLoopAsync(_readCts.Token, readLoopReady));

        // Wait for read loop to be ready (max 2s) — prevents race condition
        await Task.WhenAny(readLoopReady.Task, Task.Delay(2000));

        // Verify process is still alive after starting
        if (_process.HasExited)
        {
            string errInfo = LastStartupError.Length > 0
                ? LastStartupError.Trim()
                : $"Process exited immediately with code {_process.ExitCode}";
            _process.Dispose();
            _process = null;
            throw new InvalidOperationException($"Server process died on startup: {errInfo}");
        }
    }

    /// <summary>Send a JSON-RPC request and wait for the response.</summary>
    public async Task<JsonRpcResponse> SendRequestAsync(string method, object? parameters = null, int timeoutMs = 30000, CancellationToken ct = default)
    {
        if (!IsAlive)
            throw new InvalidOperationException("MCP server not running");

        int id = Interlocked.Increment(ref _requestId);
        var tcs = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock)
            _pending[id] = tcs;

        try
        {
            var request = new JsonRpcRequest { Id = id, Method = method, Params = parameters };
            string json = JsonSerializer.Serialize(request, JsonOpts);

            await _writer!.WriteLineAsync(json.AsMemory(), ct);

            // Wait with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"MCP request '{method}' timed out after {timeoutMs}ms");
        }
        finally
        {
            lock (_lock)
                _pending.Remove(id);
        }
    }

    /// <summary>Send a JSON-RPC notification (no response expected).</summary>
    public async Task SendNotificationAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        if (!IsAlive) return;

        var notification = new JsonRpcNotification { Method = method, Params = parameters };
        string json = JsonSerializer.Serialize(notification, JsonOpts);
        await _writer!.WriteLineAsync(json.AsMemory(), ct);
    }

    /// <summary>Gracefully stop the server.</summary>
    public async Task StopAsync()
    {
        if (_process == null) return;

        _readCts?.Cancel();

        try
        {
            if (IsAlive)
            {
                // Attempt graceful shutdown
                try
                {
                    await SendNotificationAsync("shutdown");
                    await Task.Delay(500);
                    await SendNotificationAsync("exit");
                    await Task.Delay(500);
                }
                catch { }

                if (IsAlive)
                {
                    _process.StandardInput.Close();
                    if (!_process.WaitForExit(3000))
                    {
                        try { _process.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }
        }
        catch { }
        finally
        {
            _process?.Dispose();
            _process = null;
            _readCts?.Dispose();
            _readCts = null;
        }
    }

    // ─── Internal ───

    private async Task ReadLoopAsync(CancellationToken ct, TaskCompletionSource? readySignal = null)
    {
        if (_reader == null)
        {
            readySignal?.TrySetResult();
            return;
        }

        try
        {
            // Signal ready AFTER entering the read loop — prevents race condition
            // where requests are sent before the loop is actually reading.
            readySignal?.TrySetResult();

            while (!ct.IsCancellationRequested && IsAlive)
            {
                string? line = await _reader.ReadLineAsync(ct);
                if (line == null) break; // Process closed stdout
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var response = JsonSerializer.Deserialize<JsonRpcResponse>(line, JsonOpts);
                    if (response == null) continue;

                    if (response.Id.HasValue)
                    {
                        // It's a response to a request
                        TaskCompletionSource<JsonRpcResponse>? tcs;
                        lock (_lock)
                            _pending.TryGetValue(response.Id.Value, out tcs);

                        tcs?.TrySetResult(response);
                    }
                    else
                    {
                        // It's a notification from the server
                        // Parse as notification (has method but no id)
                        try
                        {
                            var notification = JsonSerializer.Deserialize<JsonRpcNotification>(line, JsonOpts);
                            if (notification != null)
                                OnNotification?.Invoke(notification);
                        }
                        catch { }
                    }
                }
                catch (JsonException)
                {
                    // Malformed JSON — log to stderr handler
                    OnServerError?.Invoke($"[malformed response]: {line[..Math.Min(200, line.Length)]}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnServerError?.Invoke($"[read error]: {ex.Message}");
        }
    }

    private void HandleProcessExit()
    {
        // Fail all pending requests
        lock (_lock)
        {
            foreach (var (_, tcs) in _pending)
                tcs.TrySetException(new IOException("MCP server process exited unexpectedly"));
            _pending.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Use async-safe pattern to avoid blocking the finalizer thread.
        // StopAsync().Wait() can deadlock if called from a sync context with SynchronizationContext.
        try
        {
            _readCts?.Cancel();
            if (_process is { HasExited: false })
            {
                try { _process.Kill(true); } catch { }
            }
            _process?.Dispose();
            _process = null;
            _readCts?.Dispose();
            _readCts = null;

            // Fail all pending requests
            lock (_lock)
            {
                foreach (var (_, tcs) in _pending)
                    tcs.TrySetCanceled();
                _pending.Clear();
            }
        }
        catch { }
    }
}
