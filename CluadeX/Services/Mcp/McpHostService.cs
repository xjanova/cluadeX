using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
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
//  Security model
//  --------------
//   1. PipeSecurity ACL limits access to the current user only — no other
//      Windows user (and no remote machine) can connect.
//   2. The first message MUST be `initialize` with an auth token that
//      matches `~/.cluadex/mcp-host-token` (auto-generated at first run).
//   3. The pipe lives in the user's session — uninstalling CluadeX removes
//      it.
//
//  Wire-up
//  -------
//   - Registered as a singleton in App.ConfigureServices.
//   - Started by App.OnStartup on the same background task as
//     McpServerManager.InitializeAsync (non-blocking).
//   - Tool execution is delegated via the ToolDispatcher event so the
//     transport layer (this file) stays free of business logic.
// ════════════════════════════════════════════════════════════════════════════
public sealed class McpHostService : INotifyPropertyChanged, IDisposable
{
    /// <summary>Default per-user pipe name. Local-only by definition.</summary>
    public const string DefaultPipeName = "cluadex-mcp";

    /// <summary>JSON-RPC error: invalid request shape.</summary>
    private const int ErrInvalidRequest = -32600;
    /// <summary>JSON-RPC error: method not implemented.</summary>
    private const int ErrMethodNotFound = -32601;
    /// <summary>JSON-RPC error: parameters didn't match the schema.</summary>
    private const int ErrInvalidParams = -32602;
    /// <summary>JSON-RPC error: anything thrown by the tool handler.</summary>
    private const int ErrInternal = -32603;
    /// <summary>App-specific: caller never sent `initialize` or token didn't match.</summary>
    private const int ErrUnauthorised = -32000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly string _pipeName;
    private readonly string _logPath;
    private readonly string _tokenPath;
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

    /// <summary>
    /// Pluggable handler for inbound `tools/call` requests. Set this once
    /// from App.xaml.cs after the DI container is built — there should be
    /// exactly one dispatcher (a property, not an event, so multiple
    /// subscribers can't all try to claim the same call). The transport
    /// stays out of business logic so tools can be re-mounted without
    /// touching this file.
    /// </summary>
    public Func<McpToolInvocation, CancellationToken, Task<McpToolResult>>? ToolDispatcher { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public McpHostService(string? pipeName = null)
    {
        _pipeName = pipeName ?? DefaultPipeName;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cluadex");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, "mcp-host.log");
        _tokenPath = Path.Combine(dir, "mcp-host-token");

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

    // ─── Per-connection JSON-RPC loop ────────────────────────────────────
    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        bool authorised = false;
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonRpcRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<JsonRpcRequest>(line, JsonOpts);
                }
                catch (JsonException jx)
                {
                    await writer.WriteLineAsync(SerialiseError(0, ErrInvalidRequest, $"malformed JSON: {jx.Message}"));
                    continue;
                }
                if (request == null) continue;

                Interlocked.Increment(ref _totalRequests);
                LastRequestAt = DateTime.UtcNow;
                OnPropertyChanged(nameof(TotalRequests));

                try
                {
                    string responseJson = await DispatchAsync(request, authorise: a => authorised = a, isAuthorised: authorised, ct);
                    await writer.WriteLineAsync(responseJson);
                }
                catch (Exception ex)
                {
                    LastError = $"dispatch: {ex.Message}";
                    Log($"[dispatch-error] req-id={request.Id} method={request.Method} {ex}");
                    await writer.WriteLineAsync(SerialiseError(request.Id, ErrInternal, ex.Message));
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            Log($"[client-error] {ex}");
        }
        finally
        {
            try { pipe.Dispose(); } catch { }
            Interlocked.Decrement(ref _activeConnections);
            OnPropertyChanged(nameof(ActiveConnections));
        }
    }

    // ─── Method router ────────────────────────────────────────────────────
    private async Task<string> DispatchAsync(JsonRpcRequest req, Action<bool> authorise, bool isAuthorised, CancellationToken ct)
    {
        switch (req.Method)
        {
            case "initialize":
                {
                    // Accept the call, but require a matching token before any tool can be invoked.
                    string? presented = TryGetParam<string>(req.Params, "authToken");
                    if (presented == null || !FixedTimeEquals(presented, AuthToken))
                    {
                        return SerialiseError(req.Id, ErrUnauthorised,
                            "authToken missing or incorrect — see ~/.cluadex/mcp-host-token");
                    }
                    authorise(true);
                    return SerialiseResult(req.Id, new
                    {
                        protocolVersion = "2025-03-26",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "CluadeX-McpHost", version = ResolveVersion() }
                    });
                }

            case "tools/list":
                {
                    if (!isAuthorised) return SerialiseError(req.Id, ErrUnauthorised, "call initialize first");
                    return SerialiseResult(req.Id, new { tools = BuildToolCatalog() });
                }

            case "tools/call":
                {
                    if (!isAuthorised) return SerialiseError(req.Id, ErrUnauthorised, "call initialize first");
                    string? name = TryGetParam<string>(req.Params, "name");
                    var arguments = TryGetRawParam(req.Params, "arguments");
                    if (string.IsNullOrEmpty(name))
                        return SerialiseError(req.Id, ErrInvalidParams, "missing 'name'");

                    var dispatcher = ToolDispatcher;
                    if (dispatcher == null)
                    {
                        return SerialiseError(req.Id, ErrInternal,
                            "ToolDispatcher not wired — App.OnStartup must subscribe before pipe accepts traffic");
                    }
                    var invocation = new McpToolInvocation(name, arguments);
                    var result = await dispatcher(invocation, ct);
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
    /// dispatcher (see App.xaml.cs after Phase 1A.2 lands) — this file only
    /// describes the shape so MCP discovery works.
    /// </summary>
    private static object[] BuildToolCatalog() => new object[]
    {
        new
        {
            name = "cluadex.write_code",
            description = "Delegate a coding task to CluadeX. Spawns a visible chat session in the UI " +
                          "and runs the agentic loop with all 48 tools available. Returns a diff plus " +
                          "the full transcript so the caller can review or feed it to a reviewer.",
            inputSchema = new
            {
                type = "object",
                required = new[] { "taskId", "spec" },
                properties = new
                {
                    taskId = new { type = "string", description = "Caller-supplied id for tracing across pipeline" },
                    spec = new { type = "string", description = "Detailed task description, acceptance criteria, files in scope" },
                    contextFiles = new { type = "array", items = new { type = "string" }, description = "Optional file paths to pre-load into context" },
                    lessons = new { type = "array", items = new { type = "string" }, description = "Optional 'lessons learned' from past corrections — injected verbatim into the system prompt" },
                    workingDirectory = new { type = "string", description = "Optional cwd override — defaults to last project" }
                }
            }
        },
        new
        {
            name = "cluadex.run_skill",
            description = "Invoke a CluadeX skill (e.g. /commit, /review-pr, /simplify) on the current or " +
                          "specified working directory. Spawns a visible chat session.",
            inputSchema = new
            {
                type = "object",
                required = new[] { "taskId", "skill" },
                properties = new
                {
                    taskId = new { type = "string" },
                    skill = new { type = "string", description = "Slash-command name without the slash, e.g. 'commit'" },
                    args = new { type = "string", description = "Free-form argument string passed to the skill template" },
                    workingDirectory = new { type = "string" }
                }
            }
        },
        new
        {
            name = "cluadex.review",
            description = "Have CluadeX review a diff and produce structured feedback (severity, line refs, " +
                          "suggested fixes). Used by ObsidianX as a cheap pre-filter before sending to the " +
                          "Claude Desktop senior reviewer.",
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
                          "{ provider, model, path, ready, vramUsedMB?, vramTotalMB? }. Use this to detect " +
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
    private static string SerialiseResult(int id, object payload)
    {
        var resp = new JsonRpcResponse
        {
            Id = id,
            Result = JsonSerializer.SerializeToElement(payload, JsonOpts),
        };
        return JsonSerializer.Serialize(resp, JsonOpts);
    }

    private static string SerialiseError(int id, int code, string message)
    {
        var resp = new JsonRpcResponse
        {
            Id = id,
            Error = new JsonRpcError { Code = code, Message = message },
        };
        return JsonSerializer.Serialize(resp, JsonOpts);
    }

    private static T? TryGetParam<T>(object? rawParams, string name)
    {
        if (rawParams is null) return default;
        try
        {
            var el = rawParams is JsonElement je
                ? je
                : JsonSerializer.SerializeToElement(rawParams, JsonOpts);
            if (el.ValueKind != JsonValueKind.Object) return default;
            if (!el.TryGetProperty(name, out var v)) return default;
            if (v.ValueKind == JsonValueKind.Null) return default;
            return v.Deserialize<T>(JsonOpts);
        }
        catch { return default; }
    }

    private static JsonElement TryGetRawParam(object? rawParams, string name)
    {
        if (rawParams is null) return default;
        try
        {
            var el = rawParams is JsonElement je
                ? je
                : JsonSerializer.SerializeToElement(rawParams, JsonOpts);
            if (el.ValueKind != JsonValueKind.Object) return default;
            return el.TryGetProperty(name, out var v) ? v : default;
        }
        catch { return default; }
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

    private void Log(string msg)
    {
        try
        {
            File.AppendAllText(_logPath, $"{DateTime.Now:O} {msg}{Environment.NewLine}");
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
