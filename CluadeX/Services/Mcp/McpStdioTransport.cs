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
public sealed class McpStdioTransport : IMcpTransport
{
    private Process? _process;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private int _requestId;
    private bool _disposed;
    private readonly Dictionary<int, TaskCompletionSource<JsonRpcResponse>> _pending = new();
    private readonly object _lock = new();
    // Serializes writes to the server's stdin. StreamWriter is NOT safe for concurrent WriteLineAsync,
    // and a notification-triggered tools/list can overlap an in-flight tools/call → corrupt JSON-RPC
    // framing → mystery request timeouts.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _readCts;
    // Set by StopAsync/Dispose so the exit handler can tell "we asked it to stop"
    // from "it died on us". Without this a supervisor would try to resurrect a
    // server the user deliberately stopped.
    private volatile bool _stopRequested;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public bool IsAlive => _process != null && !_process.HasExited;
    public event Action<string>? OnServerError; // stderr output
    public event Action<JsonRpcNotification>? OnNotification; // server→client notifications

    /// <summary>
    /// The server process exited without us asking. Nobody listened for this before,
    /// so a brainx-mcp that died 30s after startup left the app offering its 83 tool
    /// schemas to the model for the next four minutes and answering every call with
    /// "not running" — with no log line, no retry and no UI. Subscribe BEFORE
    /// <see cref="StartAsync"/>; it never fires for a stop we initiated.
    /// </summary>
    public event Action? OnUnexpectedExit;

    // Last stderr lines, bounded: a chatty server writing stderr for hours used to grow this string forever.
    private const int StderrKeepLines = 20;
    private const int StderrKeepChars = 4096;
    private readonly Queue<string> _stderrTail = new();
    private readonly object _stderrLock = new();
    private string _startError = "";

    /// <summary>Why the start failed, or the last stderr lines (≤ 20 lines / 4 KB) for error diagnosis.</summary>
    public string LastStartupError
    {
        get
        {
            lock (_stderrLock)
                return _startError.Length > 0 ? _startError : string.Join("\n", _stderrTail);
        }
    }

    /// <summary>
    /// Environment variables a child may inherit from CluadeX. Everything else (API keys for Anthropic / OpenAI /
    /// GitHub, tokens a user exported in the shell that launched the app) used to be handed to EVERY MCP server,
    /// including third-party npx packages. What a server needs beyond this goes in its own "env" block.
    /// The system/identity variables keep runtimes working (Python needs SystemRoot, npm needs APPDATA, Node's
    /// homedir needs USERPROFILE); the proxy variables keep npx able to download packages behind a proxy.
    /// </summary>
    public static readonly string[] InheritedEnvironment =
    {
        "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "windir", "COMSPEC", "TEMP", "TMP",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "APPDATA", "ProgramData",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)",
        "CommonProgramW6432", "USERNAME", "USERDOMAIN", "COMPUTERNAME", "OS", "PROCESSOR_ARCHITECTURE",
        "NUMBER_OF_PROCESSORS", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY",
    };

    /// <summary>Start the MCP server process and wait until the read loop is ready.</summary>
    public async Task StartAsync(McpServerConfig config)
    {
        if (IsAlive) return;

        lock (_stderrLock) { _stderrTail.Clear(); _startError = ""; }

        ProcessStartInfo psi;
        try
        {
            psi = BuildStartInfo(config);
        }
        catch (Exception ex)
        {
            lock (_stderrLock) _startError = ex.Message;
            throw new InvalidOperationException(ex.Message, ex);
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += (_, _) => HandleProcessExit();

        try
        {
            _process.Start();
        }
        catch (Exception ex)
        {
            string msg = $"Cannot start process '{psi.FileName}': {ex.Message}";
            lock (_stderrLock) _startError = msg;
            _process.Dispose();
            _process = null;
            throw new InvalidOperationException(msg, ex);
        }

        _writer = _process.StandardInput;
        _writer.AutoFlush = true;
        _reader = _process.StandardOutput;

        // Read stderr in background — capture early errors for diagnosis. Read to EOF (not "while not exited")
        // so the lines a server prints right before dying — the ones that explain why — are not dropped.
        var stderr = _process.StandardError;
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await stderr.ReadLineAsync()) != null)
                {
                    RememberStderr(line);
                    OnServerError?.Invoke(line);
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

    private void RememberStderr(string line)
    {
        lock (_stderrLock)
        {
            _stderrTail.Enqueue(line.Length > StderrKeepChars ? line[..StderrKeepChars] : line);
            int total = _stderrTail.Sum(l => l.Length + 1);
            while (_stderrTail.Count > StderrKeepLines || (total > StderrKeepChars && _stderrTail.Count > 1))
                total -= _stderrTail.Dequeue().Length + 1;
        }
    }

    // ─── Process start info (public static so the harness can check it without spawning) ───

    /// <summary>
    /// Build the ProcessStartInfo for a server config.
    ///
    /// With UseShellExecute=false, CreateProcess only appends ".exe" when looking a bare name up, so "npx" /
    /// "npm" / "pnpm" — which on Windows are .cmd shims, and by far the most common MCP setup — never started
    /// ("The system cannot find the file specified"). The command is now resolved through PATH + PATHEXT, and a
    /// .cmd/.bat target runs through cmd.exe with every argument escaped for cmd. Arguments to a real
    /// executable go through ArgumentList: the old hand-quoting broke on embedded quotes and trailing
    /// backslashes ("C:\dir\" became C:\dir").
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(McpServerConfig config, IDictionary<string, string?>? parentEnv = null)
    {
        if (string.IsNullOrWhiteSpace(config.Command))
            throw new InvalidOperationException("No command configured for this MCP server.");

        var env = parentEnv ?? ReadProcessEnvironment();
        string resolved = ResolveCommand(config.Command.Trim(), env)
            ?? throw new InvalidOperationException(
                $"Command '{config.Command}' was not found. Install it, or put its full path in the Command box " +
                $"(searched PATH with extensions {GetEnv(env, "PATHEXT") ?? DefaultPathExt}).");

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var args = config.Args ?? new List<string>();
        string ext = Path.GetExtension(resolved);
        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // Same scheme as Node's cross-spawn: cmd.exe /d /s /c "<escaped command> <escaped args>". Shims under
            // node_modules\.bin re-parse their arguments through cmd once more, so their metacharacters need a
            // second ^-escape.
            bool doubleEscape = IsNodeModulesBinShim(resolved);
            var line = new StringBuilder(EscapeCmdCommand(resolved));
            foreach (var a in args)
                line.Append(' ').Append(EscapeCmdArgument(a, doubleEscape));
            psi.FileName = GetEnv(env, "COMSPEC") is { Length: > 0 } comspec ? comspec : "cmd.exe";
            psi.Arguments = $"/d /s /c \"{line}\"";
        }
        else
        {
            psi.FileName = resolved;
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        psi.Environment.Clear();
        foreach (var name in InheritedEnvironment)
            if (GetEnv(env, name) is { } v) psi.Environment[name] = v;
        if (config.Env != null)
            foreach (var (key, value) in config.Env)
                if (!string.IsNullOrWhiteSpace(key)) psi.Environment[key] = value;

        return psi;
    }

    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    private static IDictionary<string, string?> ReadProcessEnvironment()
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            d[(string)e.Key] = e.Value as string;
        return d;
    }

    private static string? GetEnv(IDictionary<string, string?> env, string name)
    {
        if (env.TryGetValue(name, out var v)) return v;
        foreach (var (k, val) in env)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return val;
        return null;
    }

    /// <summary>
    /// Resolve a command the way cmd.exe would: a path is used as given (trying PATHEXT when it has no
    /// extension); a bare name is looked up in each PATH directory, trying PATHEXT extensions first. Returns
    /// null when nothing is found. An extension-less file is never picked — on Windows that is the POSIX `npx`
    /// shell script next to npx.cmd, which CreateProcess cannot run.
    /// </summary>
    public static string? ResolveCommand(string command, IDictionary<string, string?> env)
    {
        var exts = (GetEnv(env, "PATHEXT") is { Length: > 0 } pe ? pe : DefaultPathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool hasExt = Path.HasExtension(command) && exts.Contains(Path.GetExtension(command), StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> Candidates(string basePath) =>
            hasExt ? new[] { basePath } : exts.Select(e => basePath + e.ToLowerInvariant());

        bool isPath = Path.IsPathRooted(command) || command.Contains('\\') || command.Contains('/');
        if (isPath)
        {
            if (File.Exists(command) && (hasExt || Path.HasExtension(command))) return Path.GetFullPath(command);
            return Candidates(command).FirstOrDefault(File.Exists) is { } p ? Path.GetFullPath(p) : null;
        }

        foreach (var rawDir in (GetEnv(env, "PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string dir = rawDir.Trim().Trim('"');
            if (dir.Length == 0) continue;
            string basePath;
            try { basePath = Path.Combine(dir, command); } catch { continue; }
            foreach (var c in Candidates(basePath))
                if (File.Exists(c)) return c;
        }
        return null;
    }

    private static bool IsNodeModulesBinShim(string path) =>
        System.Text.RegularExpressions.Regex.IsMatch(path, @"node_modules[\\/]\.bin[\\/][^\\/]+\.cmd$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex CmdMetaChars =
        new(@"([()\][%!^""`<>&|;, *?])", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>^-escape cmd metacharacters in the command path (cross-spawn's escapeCommand).</summary>
    public static string EscapeCmdCommand(string command) => CmdMetaChars.Replace(command, "^$1");

    /// <summary>
    /// Quote one argument for the MSVC argv parser, then ^-escape cmd metacharacters (cross-spawn's
    /// escapeArgument, after https://qntm.org/cmd): backslashes before a quote or the end are doubled,
    /// embedded quotes become \".
    /// </summary>
    public static string EscapeCmdArgument(string arg, bool doubleEscapeMetaChars)
    {
        arg = System.Text.RegularExpressions.Regex.Replace(arg, @"(\\*)""", m => m.Groups[1].Value + m.Groups[1].Value + "\\\"");
        arg = System.Text.RegularExpressions.Regex.Replace(arg, @"(\\*)\z", m => m.Groups[1].Value + m.Groups[1].Value);
        arg = $"\"{arg}\"";
        arg = CmdMetaChars.Replace(arg, "^$1");
        if (doubleEscapeMetaChars) arg = CmdMetaChars.Replace(arg, "^$1");
        return arg;
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

            await _writeLock.WaitAsync(ct);
            try { await _writer!.WriteLineAsync(json.AsMemory(), ct); }
            finally { _writeLock.Release(); }

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
        await _writeLock.WaitAsync(ct);
        try { await _writer!.WriteLineAsync(json.AsMemory(), ct); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Gracefully stop the server.</summary>
    public async Task StopAsync()
    {
        _stopRequested = true;
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

        // Tell the supervisor. Outside the lock, and swallowing subscriber
        // exceptions, because this runs on the Process.Exited callback thread.
        if (!_stopRequested)
        {
            try { OnUnexpectedExit?.Invoke(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopRequested = true;
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
