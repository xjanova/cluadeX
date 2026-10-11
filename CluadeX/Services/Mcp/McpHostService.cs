using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CluadeX.Models;

namespace CluadeX.Services.Mcp;

// ════════════════════════════════════════════════════════════════════════════
//  McpHostService — runs INSIDE the CluadeX WPF process and exposes the
//  agent's coding capabilities as an MCP server (JSON-RPC 2.0) over a
//  per-user named pipe.
//
//  Why named pipe instead of stdio?
//  --------------------------------
//  ObsidianX delegates each task to CluadeX expecting a *visible* chat
//  session per task. That requires CluadeX's WPF UI to be running, which
//  rules out stdio (subprocess-only headless mode). Named pipe lets the
//  running WPF app handle requests on a background thread and dispatch
//  visible session-spawn work onto the UI thread.
//
//  Wire protocol
//  -------------
//   - Newline-delimited JSON-RPC 2.0, UTF-8, one object per line (no batches).
//   - An id may be a string or a number; it is echoed back exactly as sent.
//   - A message without an id is a notification and is NEVER answered
//     (notifications/initialized included). notifications/cancelled with
//     params.requestId cancels that request if it is the one running, and
//     drops it if it is still waiting in the queue (it never starts).
//   - Requests on ONE connection run one at a time, in order (up to
//     MaxQueuedRequests wait; more are refused with "busy"). Use a second
//     connection for ping / get_active_model while a long run is going.
//   - The client closing the pipe cancels the request that is running.
//
//  Security model
//  --------------
//   1. PipeSecurity ACL limits access to the current user only — no other
//      Windows user (and no remote machine) can connect.
//   2. Tools need `initialize` with the auth token from <data root>\mcp-host-token
//      (~/.cluadex for an installed app, <exe>\Data for a portable copy or a test
//      harness). Five wrong tokens close the connection.
//   3. Lines are capped at MaxLineBytes; the queue per connection is bounded.
//
//  Wire-up
//  -------
//   - Registered as a singleton in App.ConfigureServices.
//   - Started by App.OnStartup on the same background task as
//     McpServerManager.InitializeAsync (non-blocking).
//   - Tool execution is delegated via the ToolDispatcher property so the
//     transport layer (this file) stays free of business logic.
// ════════════════════════════════════════════════════════════════════════════
public sealed class McpHostService : INotifyPropertyChanged, IDisposable
{
    /// <summary>Default per-user pipe name. Local-only by definition.</summary>
    public const string DefaultPipeName = "cluadex-mcp";

    /// <summary>Requests waiting behind the running one on a single connection.</summary>
    public const int MaxQueuedRequests = 8;
    /// <summary>Longest accepted line (one JSON-RPC message).</summary>
    public const int MaxLineBytes = 16 * 1024 * 1024;
    /// <summary>Wrong tokens tolerated on one connection before it is closed.</summary>
    public const int MaxFailedAuth = 5;

    private const int ErrParse = -32700;
    private const int ErrInvalidRequest = -32600;
    private const int ErrMethodNotFound = -32601;
    private const int ErrInvalidParams = -32602;
    private const int ErrInternal = -32603;
    /// <summary>App-specific: caller never sent `initialize` or the token didn't match.</summary>
    private const int ErrUnauthorised = -32000;
    /// <summary>App-specific: too many requests waiting on this connection.</summary>
    private const int ErrBusy = -32001;
    /// <summary>LSP/MCP convention: the request was cancelled.</summary>
    private const int ErrRequestCancelled = -32800;
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly string _pipeName;
    private readonly string _logPath;
    private readonly string _tokenPath;
    private readonly object _logLock = new();
    private CancellationTokenSource? _acceptCts;
    private bool _disposed;

    // ─── Observable status (for binding to a status indicator in MainWindow) ───
    private bool _isListening;
    private int _activeConnections;
    private int _totalRequests;
    private DateTime? _lastRequestAt;
    private string? _lastError;

    public bool IsListening { get => _isListening; private set => Set(ref _isListening, value); }
    public int ActiveConnections { get => _activeConnections; private set => Set(ref _activeConnections, value); }
    public int TotalRequests { get => _totalRequests; private set => Set(ref _totalRequests, value); }
    public DateTime? LastRequestAt { get => _lastRequestAt; private set => Set(ref _lastRequestAt, value); }
    public string? LastError { get => _lastError; private set => Set(ref _lastError, value); }
    public string PipeName => _pipeName;
    public string AuthToken { get; private set; } = "";

    /// <summary>Where the token file and mcp-host.log live: the app's data root (~/.cluadex installed,
    /// &lt;exe&gt;\Data portable — so a test harness never writes into the user's profile).</summary>
    public string DataDirectory { get; }
    /// <summary>The auth token file (what install.json points a launcher at).</summary>
    public string TokenFilePath => _tokenPath;

    /// <summary>
    /// Pluggable handler for inbound `tools/call` requests. Set this once
    /// from App.xaml.cs after the DI container is built — there should be
    /// exactly one dispatcher (a property, not an event, so multiple
    /// subscribers can't all try to claim the same call). The token it gets is
    /// cancelled when the caller disconnects or sends notifications/cancelled.
    /// </summary>
    public Func<McpToolInvocation, CancellationToken, Task<McpToolResult>>? ToolDispatcher { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <param name="settings">Supplies the data root; null = ~/.cluadex (the installed layout).</param>
    /// <param name="pipeName">A private pipe name (harnesses); null = <see cref="DefaultPipeName"/>.</param>
    public McpHostService(SettingsService? settings = null, string? pipeName = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName;

        DataDirectory = !string.IsNullOrWhiteSpace(settings?.DataRoot)
            ? settings!.DataRoot
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex");
        Directory.CreateDirectory(DataDirectory);
        _logPath = Path.Combine(DataDirectory, "mcp-host.log");
        _tokenPath = Path.Combine(DataDirectory, "mcp-host-token");

        AuthToken = LoadOrCreateToken();
    }

    /// <summary>Begin accepting pipe connections in the background.</summary>
    public Task StartAsync()
    {
        if (IsListening) return Task.CompletedTask;
        _acceptCts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_acceptCts.Token));
        IsListening = true;
        Log($"started on pipe={_pipeName}");
        return Task.CompletedTask;
    }

    /// <summary>Stop accepting new connections and close any in-flight ones.</summary>
    public void Stop()
    {
        _acceptCts?.Cancel();
        _acceptCts?.Dispose();
        _acceptCts = null;
        IsListening = false;
        Log("stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // ─── Install marker ───────────────────────────────────────────────────

    /// <summary>
    /// Is this process the CluadeX app (CluadeX is the entry assembly) — as CluadeX.exe or as "dotnet CluadeX.dll" —
    /// rather than a test harness that links CluadeX.dll? What App.OnStartup checks before starting the app; the
    /// stricter <see cref="IsCluadeXHost"/> (CluadeX.exe only) decides who may write install.json.
    /// </summary>
    public static bool IsCluadeXApp(string? entryAssemblyName = null)
    {
        entryAssemblyName ??= System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        return string.Equals(entryAssemblyName, "CluadeX", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Is this process the CluadeX app itself (CluadeX.dll as the entry assembly, started from an .exe — not a
    /// test harness that links CluadeX.dll, not "dotnet CluadeX.dll")? Only the app may advertise itself in
    /// install.json, or a launcher ends up starting a harness (it did: ~/.cluadex/install.json pointed at chattest.exe).
    /// </summary>
    public static bool IsCluadeXHost(string? exePath = null, string? entryAssemblyName = null)
    {
        entryAssemblyName ??= System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        exePath ??= Environment.ProcessPath;
        if (!string.Equals(entryAssemblyName, "CluadeX", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        return !Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Write &lt;data root&gt;\install.json describing THIS running install (exe path, version, pipe name, token file)
    /// so a launcher/orchestrator can discover and start CluadeX and connect to its pipe without hard-coding paths.
    /// Returns false — and touches nothing — when this process is not the CluadeX app (<see cref="IsCluadeXHost"/>).
    /// An installed app's start rewrites the marker, which also repairs one a harness wrote before this check
    /// existed. Atomic (temp file + replace); best-effort, never throws.
    /// </summary>
    public static bool WriteInstallMarker(string dataDirectory, string pipeName, string tokenFile,
        string? exePath = null, string? entryAssemblyName = null)
    {
        exePath ??= Environment.ProcessPath;
        if (!IsCluadeXHost(exePath, entryAssemblyName)) return false;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var marker = new
            {
                exePath,
                installDir = Path.GetDirectoryName(exePath) ?? "",
                version = ResolveVersion(),
                pipeName,
                tokenFile,
                updatedAt = DateTime.UtcNow.ToString("o"),
            };
            string path = Path.Combine(dataDirectory, "install.json");
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    // ─── Accept loop ──────────────────────────────────────────────────────
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreateServerPipe();
                await pipe.WaitForConnectionAsync(ct);
                Interlocked.Increment(ref _activeConnections);
                OnPropertyChanged(nameof(ActiveConnections));
                _ = HandleClientAsync(pipe, ct);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                LastError = $"accept: {ex.Message}";
                Log($"[accept-error] {ex}");
                pipe?.Dispose();
                // brief backoff to avoid CPU spin if the pipe layer is failing fast
                try { await Task.Delay(500, ct); } catch { }
            }
        }
    }

    /// <summary>
    /// Create a per-user-only named pipe. Windows ACL guarantees no other
    /// account on the box (and no remote connection) can read or write.
    /// </summary>
    private NamedPipeServerStream CreateServerPipe()
    {
        var ps = new PipeSecurity();
        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Cannot resolve current Windows SID");
        ps.AddAccessRule(new PipeAccessRule(
            sid,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            pipeSecurity: ps);
    }

    // ─── Per-connection JSON-RPC ─────────────────────────────────────────

    private sealed record PendingRequest(JsonElement Id, string Method, JsonElement Params);

    /// <summary>One client: its writer (shared by the reader's immediate replies and the request loop), its auth
    /// state and the request that is running.</summary>
    private sealed class Connection
    {
        public readonly Stream Pipe;
        public readonly CancellationTokenSource Closed;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly object _gate = new();
        private string? _inFlightId;
        private CancellationTokenSource? _inFlightCts;
        private bool _inFlightCancelledByClient;
        public volatile bool Authorised;
        public int FailedAuth;

        public Connection(Stream pipe, CancellationTokenSource closed) { Pipe = pipe; Closed = closed; }

        public async Task<bool> WriteLineAsync(string json)
        {
            if (Closed.IsCancellationRequested) return false;
            byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
            // A client that stops reading must not hang the server: the write gives up after WriteTimeout.
            using var timeout = new CancellationTokenSource(WriteTimeout);
            try
            {
                await _writeLock.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) { Closed.Cancel(); return false; }
            try
            {
                await Pipe.WriteAsync(bytes, timeout.Token);
                await Pipe.FlushAsync(timeout.Token);
                return true;
            }
            catch
            {
                Closed.Cancel();   // broken pipe / timed out: the connection is done
                return false;
            }
            finally { _writeLock.Release(); }
        }

        public void Begin(string idKey, CancellationTokenSource cts)
        {
            lock (_gate) { _inFlightId = idKey; _inFlightCts = cts; _inFlightCancelledByClient = false; }
        }

        /// <summary>Returns whether the client cancelled it (then no response is sent).</summary>
        public bool End()
        {
            lock (_gate)
            {
                bool cancelled = _inFlightCancelledByClient;
                _inFlightId = null; _inFlightCts = null; _inFlightCancelledByClient = false;
                return cancelled;
            }
        }

        // Requests waiting in the queue (by raw id), and those of them the client cancelled before they started.
        private readonly Dictionary<string, int> _queued = new(StringComparer.Ordinal);
        private readonly HashSet<string> _cancelledQueued = new(StringComparer.Ordinal);

        public void Queued(string idKey)
        {
            lock (_gate) _queued[idKey] = _queued.TryGetValue(idKey, out int n) ? n + 1 : 1;
        }

        /// <summary>The request left the queue: true = the client cancelled it while it waited (skip it, no reply).</summary>
        public bool Dequeued(string idKey)
        {
            lock (_gate)
            {
                if (_queued.TryGetValue(idKey, out int n))
                {
                    if (n <= 1) _queued.Remove(idKey); else _queued[idKey] = n - 1;
                }
                return _cancelledQueued.Remove(idKey);
            }
        }

        public void CancelByClient(string idKey)
        {
            lock (_gate)
            {
                if (_inFlightId != null && _inFlightId == idKey && _inFlightCts != null)
                {
                    _inFlightCancelledByClient = true;
                    try { _inFlightCts.Cancel(); } catch (ObjectDisposedException) { }
                    return;
                }
                // Still waiting behind the running one: it must not start later (it used to run anyway).
                if (_queued.ContainsKey(idKey)) _cancelledQueued.Add(idKey);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken serverCt)
    {
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        var conn = new Connection(pipe, closed);
        var queue = Channel.CreateBounded<PendingRequest>(new BoundedChannelOptions(MaxQueuedRequests)
        {
            SingleReader = true,
            SingleWriter = true,
        });
        Task reader = Task.Run(() => ReadLoopAsync(conn, queue.Writer));
        try
        {
            // One request at a time, in arrival order; ends when the reader completes the queue (client gone).
            await foreach (var request in queue.Reader.ReadAllAsync())
            {
                if (closed.IsCancellationRequested) break;
                // Cancelled by the client while it waited: never started, and (like a cancelled run) never answered.
                if (conn.Dequeued(request.Id.GetRawText())) continue;
                await ServeAsync(conn, request);
            }
        }
        catch (Exception ex)
        {
            Log($"[client-error] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { closed.Cancel(); } catch { }
            try { await reader; } catch { }
            try { pipe.Dispose(); } catch { }
            Interlocked.Decrement(ref _activeConnections);
            OnPropertyChanged(nameof(ActiveConnections));
        }
    }

    /// <summary>
    /// Reads every line the client sends, also WHILE a request is running — that is how a disconnect (or
    /// notifications/cancelled) reaches the running request. Notifications are handled here and never answered;
    /// requests are queued for <see cref="HandleClientAsync"/>.
    /// </summary>
    private async Task ReadLoopAsync(Connection conn, ChannelWriter<PendingRequest> queue)
    {
        var reader = new LineReader(conn.Pipe);
        try
        {
            while (!conn.Closed.IsCancellationRequested)
            {
                (string? Line, bool TooLong) read;
                try { read = await reader.ReadLineAsync(conn.Closed.Token); }
                catch { break; }   // broken pipe, or the server is stopping

                if (read.TooLong)
                {
                    await conn.WriteLineAsync(SerialiseError(null, ErrInvalidRequest, $"message is larger than {MaxLineBytes / (1024 * 1024)} MB"));
                    continue;
                }
                if (read.Line == null) break;   // the client closed the pipe
                if (string.IsNullOrWhiteSpace(read.Line)) continue;

                JsonElement message;
                try
                {
                    using var doc = JsonDocument.Parse(read.Line, new JsonDocumentOptions { MaxDepth = 64 });
                    message = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    await conn.WriteLineAsync(SerialiseError(null, ErrParse, "parse error: not valid JSON"));
                    continue;
                }
                if (message.ValueKind != JsonValueKind.Object)
                {
                    await conn.WriteLineAsync(SerialiseError(null, ErrInvalidRequest, "expected one JSON-RPC object per line (batches are not supported)"));
                    continue;
                }

                string? method = message.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                JsonElement parameters = message.TryGetProperty("params", out var p) ? p : default;

                if (!message.TryGetProperty("id", out var id))
                {
                    // A notification: never answered (JSON-RPC 2.0 §4.1) — notifications/initialized included.
                    if (method == "notifications/cancelled" && parameters.ValueKind == JsonValueKind.Object
                        && parameters.TryGetProperty("requestId", out var rid) && rid.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                        conn.CancelByClient(rid.GetRawText());
                    continue;
                }
                if (id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                {
                    await conn.WriteLineAsync(SerialiseError(null, ErrInvalidRequest, "'id' must be a string or a number"));
                    continue;
                }
                if (method == null)
                {
                    // A response to a request this server never sends: nothing to do. Anything else is malformed.
                    if (message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _)) continue;
                    await conn.WriteLineAsync(SerialiseError(id, ErrInvalidRequest, "missing 'method'"));
                    continue;
                }

                string idKey = id.GetRawText();
                conn.Queued(idKey);
                if (!queue.TryWrite(new PendingRequest(id.Clone(), method, parameters.ValueKind == JsonValueKind.Undefined ? default : parameters.Clone())))
                {
                    conn.Dequeued(idKey);
                    await conn.WriteLineAsync(SerialiseError(id, ErrBusy,
                        $"busy: {MaxQueuedRequests} requests are already waiting on this connection — requests run one at a time; open another connection"));
                }
            }
        }
        finally
        {
            // Client gone (or server stopping): cancel what is running and end the request loop.
            try { conn.Closed.Cancel(); } catch { }
            queue.TryComplete();
        }
    }

    private async Task ServeAsync(Connection conn, PendingRequest request)
    {
        Interlocked.Increment(ref _totalRequests);
        LastRequestAt = DateTime.UtcNow;
        OnPropertyChanged(nameof(TotalRequests));

        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(conn.Closed.Token);
        conn.Begin(request.Id.GetRawText(), requestCts);
        string response;
        try
        {
            response = await DispatchAsync(conn, request, requestCts.Token);
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
            response = SerialiseError(request.Id, ErrRequestCancelled,
                conn.Closed.IsCancellationRequested ? "cancelled: the client disconnected" : "cancelled by the client");
        }
        catch (Exception ex)
        {
            LastError = $"dispatch: {ex.GetType().Name}";
            Log($"[dispatch-error] method={request.Method} {ex}");
            response = SerialiseError(request.Id, ErrInternal, $"internal error ({ex.GetType().Name}) — details are in mcp-host.log");
        }
        bool cancelledByClient = conn.End();
        // A request the client cancelled gets no response (MCP cancellation); a vanished client can't read one.
        if (!cancelledByClient && !conn.Closed.IsCancellationRequested)
            await conn.WriteLineAsync(response);
    }

    // ─── Method router ────────────────────────────────────────────────────
    private async Task<string> DispatchAsync(Connection conn, PendingRequest req, CancellationToken ct)
    {
        switch (req.Method)
        {
            case "initialize":
                {
                    // Accept the call, but require a matching token before any tool can be invoked.
                    string? presented = TryGetString(req.Params, "authToken");
                    if (presented == null || !FixedTimeEquals(presented, AuthToken))
                    {
                        int failures = Interlocked.Increment(ref conn.FailedAuth);
                        Log($"initialize refused: wrong or missing token (attempt {failures})");
                        if (failures >= MaxFailedAuth)
                        {
                            // Reply first, then hang up: guessing tokens on one connection ends here.
                            await conn.WriteLineAsync(SerialiseError(req.Id, ErrUnauthorised, "too many wrong tokens — connection closed"));
                            conn.Closed.Cancel();
                            throw new OperationCanceledException();
                        }
                        return SerialiseError(req.Id, ErrUnauthorised,
                            $"authToken missing or incorrect — see {Path.GetFileName(_tokenPath)} in the CluadeX data folder");
                    }
                    conn.Authorised = true;
                    return SerialiseResult(req.Id, new
                    {
                        protocolVersion = "2025-03-26",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "CluadeX-McpHost", version = ResolveVersion() },
                        instructions = "Requests on one connection run one at a time, in order. A coding run "
                            + "(cluadex.write_code / run_skill / review) holds its connection until it finishes: use a "
                            + "second connection for ping or cluadex.get_active_model meanwhile. Closing the connection, "
                            + "or notifications/cancelled with the request's id, stops the run.",
                    });
                }

            case "tools/list":
                {
                    if (!conn.Authorised) return SerialiseError(req.Id, ErrUnauthorised, "call initialize first");
                    return SerialiseResult(req.Id, new { tools = BuildToolCatalog() });
                }

            case "tools/call":
                {
                    if (!conn.Authorised) return SerialiseError(req.Id, ErrUnauthorised, "call initialize first");
                    string? name = TryGetString(req.Params, "name");
                    if (string.IsNullOrEmpty(name))
                        return SerialiseError(req.Id, ErrInvalidParams, "missing 'name'");
                    JsonElement arguments = req.Params.ValueKind == JsonValueKind.Object && req.Params.TryGetProperty("arguments", out var a)
                        ? a.Clone() : default;

                    var dispatcher = ToolDispatcher;
                    if (dispatcher == null)
                    {
                        return SerialiseError(req.Id, ErrInternal,
                            "ToolDispatcher not wired — App.OnStartup must subscribe before pipe accepts traffic");
                    }
                    var result = await dispatcher(new McpToolInvocation(name, arguments), ct);
                    return SerialiseResult(req.Id, result);
                }

            case "ping":
                return SerialiseResult(req.Id, new { ok = true, ts = DateTime.UtcNow });

            default:
                return SerialiseError(req.Id, ErrMethodNotFound, $"method '{req.Method}' is not supported");
        }
    }

    /// <summary>
    /// The catalog returned to ObsidianX. Tool implementations live in the
    /// dispatcher (App.xaml.cs) — this file only describes the shape so MCP
    /// discovery works. It must describe what is really returned.
    /// </summary>
    private static object[] BuildToolCatalog() => new object[]
    {
        new
        {
            name = "cluadex.write_code",
            description = "Delegate a coding task to CluadeX. Opens a new chat in the CluadeX window (brought to the " +
                          "front) where the run streams live, and runs the same agent loop, tools and permission " +
                          "prompts as a typed chat in that project. Returns the agent's final reply as text. isError is " +
                          "true when the run failed, hit its step limit, was stopped in CluadeX, or the caller " +
                          "disconnected. Requests on one connection run one at a time; the call blocks until the run ends.",
            inputSchema = new
            {
                type = "object",
                required = new[] { "taskId", "spec" },
                properties = new
                {
                    taskId = new { type = "string", description = "Caller-supplied id for tracing across pipeline" },
                    spec = new { type = "string", description = "Detailed task description, acceptance criteria, files in scope" },
                    contextFiles = new { type = "array", items = new { type = "string" }, description = "Optional file paths in scope — listed in the task message" },
                    lessons = new { type = "array", items = new { type = "string" }, description = "Optional 'lessons learned' from past corrections — appended to the task message" },
                    workingDirectory = new { type = "string", description = "Optional project folder for this run — CluadeX switches to it for the run and back afterwards. Default: the open project" },
                    maxSteps = new { type = "integer", description = "Optional step limit for this run (1-200). Default: the Max agent steps setting. When this build cannot apply a per-run limit, the result says so" },
                    acceptEdits = new { type = "boolean", description = "Optional. true = ask the user in CluadeX (once per folder per session) to let file edits inside the project run without a prompt for this run. Never for a drive root, the user folder or a system folder; never covers commands, MCP tools, paths outside, or build/hook/CI files; the automatic build check after such edits still asks. The result says when it was declined or refused. Default: the user's Accept-edits default (off)" },
                }
            }
        },
        new
        {
            name = "cluadex.run_skill",
            description = "Invoke a CluadeX skill (e.g. /commit, /review-pr, /simplify) on the current or " +
                          "specified working directory, in a new visible chat. Returns the final reply; isError on failure or stop.",
            inputSchema = new
            {
                type = "object",
                required = new[] { "taskId", "skill" },
                properties = new
                {
                    taskId = new { type = "string" },
                    skill = new { type = "string", description = "Slash-command name without the slash, e.g. 'commit'" },
                    args = new { type = "string", description = "Free-form argument string passed to the skill template" },
                    workingDirectory = new { type = "string" },
                    maxSteps = new { type = "integer", description = "Optional step limit for this run (1-200); the result says when it could not be applied" },
                }
            }
        },
        new
        {
            name = "cluadex.review",
            description = "Have CluadeX review a diff and produce structured feedback (severity, line refs, " +
                          "suggested fixes). Used by ObsidianX as a cheap pre-filter before sending to the " +
                          "Claude Desktop senior reviewer. Returns the review text; isError on failure or stop.",
            inputSchema = new
            {
                type = "object",
                required = new[] { "taskId", "diff" },
                properties = new
                {
                    taskId = new { type = "string" },
                    diff = new { type = "string", description = "Unified diff to review" },
                    intent = new { type = "string", description = "Original task spec for context" }
                }
            }
        },
        new
        {
            name = "cluadex.get_active_model",
            description = "Report which AI provider + model CluadeX has loaded right now, so the orchestrator " +
                          "can verify model alignment before delegating tasks. Returns " +
                          "{ provider, model, path, ready, vramUsedMB?, vramTotalMB?, effort, options[], efforts[] } — options and " +
                          "efforts are what choose_model accepts. Use this to detect " +
                          "the case where ObsidianX's intern uses one model and CluadeX's worker uses " +
                          "another — running both at once on a single GPU is a recipe for VRAM OOM.",
            inputSchema = new
            {
                type = "object",
                properties = new { },
            }
        },
        new
        {
            name = "cluadex.choose_model",
            description = "Pick the model and/or effort CluadeX's chat answers with — what the owner does in BrainX's " +
                          "cowork room. Only a model from get_active_model's `options` (API models with a key) is " +
                          "accepted, never a model on this PC; a provider change waits until no reply is running. " +
                          "Returns { ok, model, effort, reason }.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    model = new { type = "string", description = "A model id from get_active_model's options" },
                    effort = new { type = "string", description = "One of get_active_model's efforts" },
                },
            }
        },
        new
        {
            name = "cluadex.set_model",
            description = "Force CluadeX to switch its active provider + model. Used by the orchestrator " +
                          "to align CluadeX with ObsidianX's intern model so they share GPU memory instead " +
                          "of fighting for it. The user explicitly asked for this — running two models on " +
                          "one GPU is the canonical way to OOM. After switching, CluadeX persists the " +
                          "choice and re-initialises the provider so the next tools/call uses the right model. " +
                          "If the previous provider was Local GGUF / LlamaServer, the loaded weights are " +
                          "released so the new model has VRAM headroom.",
            inputSchema = new
            {
                type = "object",
                required = new[] { "provider", "model" },
                properties = new
                {
                    provider = new
                    {
                        type = "string",
                        description = "Target provider: 'Ollama', 'Local', 'LlamaServer', 'Anthropic', 'OpenAI', 'Gemini'. " +
                                      "Most common alignment case is Ollama — share the daemon with ObsidianX intern."
                    },
                    model = new
                    {
                        type = "string",
                        description = "Model name as that provider expects it (e.g. 'qwen2.5:7b' for Ollama, " +
                                      "'gemma-4-31B-it' or a full .gguf path for Local)."
                    },
                    path = new
                    {
                        type = "string",
                        description = "Optional GGUF file path. Required when provider=Local and the model name " +
                                      "isn't already in CluadeX's catalogue."
                    },
                }
            }
        },
    };

    // ─── JSON helpers ────────────────────────────────────────────────────

    /// <summary>{"jsonrpc":"2.0","id":&lt;the id exactly as the client sent it&gt;,"result":…}</summary>
    private static string SerialiseResult(JsonElement id, object payload)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            id.WriteTo(w);
            w.WritePropertyName("result");
            JsonSerializer.Serialize(w, payload, payload.GetType(), JsonOpts);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>An error response; a null id (parse error, unreadable id) is written as JSON null.</summary>
    private static string SerialiseError(JsonElement? id, int code, string message)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            if (id is { } i && i.ValueKind is JsonValueKind.String or JsonValueKind.Number) i.WriteTo(w);
            else w.WriteNullValue();
            w.WritePropertyName("error");
            w.WriteStartObject();
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string? TryGetString(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        return v.GetString();
    }

    /// <summary>
    /// Constant-time string comparison so a remote attacker can't deduce the
    /// auth token by measuring the length of the prefix that matched.
    /// (Practically moot on a per-user named pipe, but cheap insurance.)
    /// </summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private string LoadOrCreateToken()
    {
        try
        {
            if (File.Exists(_tokenPath))
            {
                var t = File.ReadAllText(_tokenPath).Trim();
                if (t.Length >= 32) return t;
            }
        }
        catch { /* fall through */ }

        // 32 bytes of cryptographic randomness, base64-url style — keeps the file
        // ascii-safe so users can copy it into ObsidianX settings if they wish.
        var bytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        try { File.WriteAllText(_tokenPath, token); } catch { /* best-effort */ }
        return token;
    }

    /// <summary>Append to mcp-host.log (rotated to .1 past 2 MB). Never the token, never request bodies.</summary>
    private void Log(string msg)
    {
        try
        {
            lock (_logLock)
            {
                var info = new FileInfo(_logPath);
                if (info.Exists && info.Length > MaxLogBytes)
                    File.Move(_logPath, _logPath + ".1", overwrite: true);
                File.AppendAllText(_logPath, $"{DateTime.Now:O} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private static string ResolveVersion() =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    // ─── INotifyPropertyChanged plumbing ─────────────────────────────────
    private void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string prop = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(prop);
    }

    /// <summary>Newline-delimited reader with a size cap: a line longer than <see cref="MaxLineBytes"/> is skipped
    /// (reported as TooLong) instead of growing memory without bound.</summary>
    private sealed class LineReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _start, _end;

        public LineReader(Stream stream) { _stream = stream; }

        /// <summary>(line, false); (null, true) for a line that was too long; (null, false) at end of stream.</summary>
        public async Task<(string? Line, bool TooLong)> ReadLineAsync(CancellationToken ct)
        {
            var acc = new MemoryStream();
            bool tooLong = false;
            while (true)
            {
                int newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                if (newline >= 0)
                {
                    int len = newline - _start;
                    if (!tooLong && acc.Length + len <= MaxLineBytes) acc.Write(_buffer, _start, len);
                    else tooLong = true;
                    _start = newline + 1;
                    if (tooLong) return (null, true);
                    return (Decode(acc), false);
                }
                int rest = _end - _start;
                if (!tooLong)
                {
                    if (acc.Length + rest > MaxLineBytes) { tooLong = true; acc.SetLength(0); }
                    else acc.Write(_buffer, _start, rest);
                }
                _start = _end = 0;
                int n = await _stream.ReadAsync(_buffer.AsMemory(0, _buffer.Length), ct);
                if (n <= 0)
                {
                    if (tooLong) return (null, true);
                    return acc.Length > 0 ? (Decode(acc), false) : (null, false);
                }
                _end = n;
            }
        }

        private static string Decode(MemoryStream acc)
        {
            var bytes = acc.GetBuffer().AsSpan(0, (int)acc.Length);
            if (bytes.Length > 0 && bytes[^1] == (byte)'\r') bytes = bytes[..^1];
            return Encoding.UTF8.GetString(bytes);
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  Surface types that the dispatcher (App.xaml.cs) consumes. Kept in this
//  file so the wire-up code only needs one using.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// One inbound `tools/call` request. The dispatcher inspects ToolName,
/// reads its parameters out of <see cref="Arguments"/>, runs the work,
/// and returns an <see cref="McpToolResult"/>.
/// </summary>
public sealed record McpToolInvocation(string ToolName, JsonElement Arguments);
