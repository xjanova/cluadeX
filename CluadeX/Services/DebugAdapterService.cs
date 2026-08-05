using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CluadeX.Services;

/// <summary>
/// Debug Adapter Protocol (DAP) client — the engine behind the workbench debugger.
///
/// DAP is to debuggers what LSP is to code intelligence: one protocol, many language-specific
/// adapters. Rather than embedding a .NET debugger (and only ever supporting .NET), this speaks DAP
/// over stdio with the same Content-Length framing <see cref="LspClientService"/> uses, so any
/// adapter the user has works: debugpy for Python, netcoredbg for .NET, js-debug for Node.
///
/// Adapters are NOT bundled and none is installed by default. <see cref="DetectAdapters"/> reports
/// exactly what is present so the UI can say "install netcoredbg to debug C#" instead of offering a
/// Start button that silently does nothing.
/// </summary>
public sealed class DebugAdapterService : IDisposable
{
    private readonly FileSystemService _fs;

    private Process? _adapter;
    private StreamWriter? _writer;
    private Stream? _reader;
    private int _seq;
    private bool _disposed;
    private readonly object _lock = new();
    private readonly Dictionary<int, TaskCompletionSource<DapResponse>> _pending = new();
    private CancellationTokenSource? _readCts;

    public DebugAdapterService(FileSystemService fs) => _fs = fs;

    // ─── Session state ───

    public bool IsRunning => _adapter is { HasExited: false };

    /// <summary>True between a "stopped" event and the next resume — stepping is only legal here.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>Thread the adapter last stopped on; every step/continue is scoped to it.</summary>
    public int? StoppedThreadId { get; private set; }

    public DebugAdapterInfo? ActiveAdapter { get; private set; }

    // ─── Events for the UI ───

    /// <summary>Execution stopped (breakpoint, step, exception). Payload = human-readable reason.</summary>
    public event Action<string, int>? Stopped;          // (reason, threadId)
    public event Action? Continued;
    public event Action<string>? Output;                 // program stdout/stderr + adapter messages
    public event Action<string>? Terminated;             // payload = why
    /// <summary>Adapter confirmed which breakpoints it could actually bind (a requested line can be
    /// moved or rejected — showing the requested line as "set" would be a lie).</summary>
    public event Action<string, List<DapBreakpoint>>? BreakpointsVerified;

    // ═══════════════════════════ Adapter discovery ═══════════════════════════

    /// <summary>
    /// What can actually debug on this machine right now. Runs the probe commands rather than
    /// assuming — an adapter listed in a config the user never installed is the same dead button
    /// as no adapter at all.
    /// </summary>
    public static List<DebugAdapterInfo> DetectAdapters()
    {
        var found = new List<DebugAdapterInfo>();

        // .NET — netcoredbg (MIT, the adapter VS Code's C# extension is built around).
        string? netcoredbg = FindOnPath("netcoredbg.exe") ?? FindOnPath("netcoredbg");
        found.Add(new DebugAdapterInfo
        {
            Id = "netcoredbg",
            Language = "C# / .NET",
            Executable = netcoredbg ?? "netcoredbg",
            Arguments = "--interpreter=vscode",
            IsAvailable = netcoredbg != null,
            InstallHint = "Download netcoredbg from github.com/Samsung/netcoredbg/releases and put it on PATH.",
            LaunchType = "coreclr",
        });

        // Python — debugpy, run as a module so any interpreter on PATH works.
        string? python = FindOnPath("python.exe") ?? FindOnPath("python") ?? FindOnPath("python3");
        bool hasDebugpy = python != null && ModuleExists(python, "debugpy");
        found.Add(new DebugAdapterInfo
        {
            Id = "debugpy",
            Language = "Python",
            Executable = python ?? "python",
            Arguments = "-m debugpy.adapter",
            IsAvailable = hasDebugpy,
            InstallHint = python == null
                ? "Install Python, then run: pip install debugpy"
                : "Run: pip install debugpy",
            LaunchType = "python",
        });

        return found;
    }

    private static bool ModuleExists(string python, string module)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"-c \"import {module}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (p == null) return false;
            // A missing module exits fast; a hung interpreter must not hang adapter detection.
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string? FindOnPath(string exe)
    {
        try
        {
            if (Path.IsPathRooted(exe)) return File.Exists(exe) ? exe : null;
            string paths = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim('"'), exe);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* a malformed PATH entry must not abort the scan */ }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Pick the adapter that matches a file's language, if it is actually installed.</summary>
    public static DebugAdapterInfo? AdapterForFile(string filePath, List<DebugAdapterInfo> adapters)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        string wanted = ext switch
        {
            ".py" => "debugpy",
            ".cs" or ".fs" or ".vb" => "netcoredbg",
            _ => "",
        };
        if (wanted.Length == 0) return null;
        return adapters.FirstOrDefault(a => a.Id == wanted && a.IsAvailable);
    }

    // ═══════════════════════════ Session lifecycle ═══════════════════════════

    /// <summary>
    /// Start an adapter, launch the program, install breakpoints, and run to the first stop.
    ///
    /// The ordering here is the fiddly part of DAP and is easy to get wrong: breakpoints may only be
    /// sent after the adapter emits "initialized", which it does in RESPONSE to `launch` — so the
    /// launch response must not be awaited before configuring, or the session deadlocks.
    /// </summary>
    public async Task<DebugStartResult> StartAsync(
        DebugAdapterInfo adapter, string program, IReadOnlyDictionary<string, List<int>> breakpoints,
        CancellationToken ct = default)
    {
        if (IsRunning) return new DebugStartResult { Error = "A debug session is already running." };
        if (!adapter.IsAvailable)
            return new DebugStartResult { Error = $"{adapter.Language} adapter not installed. {adapter.InstallHint}" };
        if (!File.Exists(program))
            return new DebugStartResult { Error = $"Program not found: {program}" };

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = adapter.Executable,
                Arguments = adapter.Arguments,
                WorkingDirectory = _fs.HasWorkingDirectory ? _fs.WorkingDirectory : Path.GetDirectoryName(program)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            _adapter = Process.Start(psi);
            if (_adapter == null) return new DebugStartResult { Error = "Could not start the debug adapter process." };

            ActiveAdapter = adapter;
            _writer = _adapter.StandardInput;
            _reader = _adapter.StandardOutput.BaseStream;
            _readCts = new CancellationTokenSource();
            _ = Task.Run(() => ReadLoopAsync(_readCts.Token));

            // stderr is where an adapter explains why it refused to start — surface it, don't swallow it.
            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await _adapter.StandardError.ReadLineAsync()) != null)
                        if (line.Length > 0) Output?.Invoke($"[adapter] {line}");
                }
                catch { }
            });

            var init = await SendRequestAsync("initialize", new
            {
                clientID = "cluadex",
                clientName = "CluadeX",
                adapterID = adapter.Id,
                locale = "en",
                linesStartAt1 = true,
                columnsStartAt1 = true,
                pathFormat = "path",
                supportsVariableType = true,
                supportsRunInTerminalRequest = false,
            }, ct);

            if (!init.Success)
                return new DebugStartResult { Error = $"initialize failed: {init.Message}" };

            // "initialized" arrives while `launch` is still in flight — arm the wait BEFORE launching.
            var initializedEvent = WaitForEventAsync("initialized", TimeSpan.FromSeconds(15), ct);

            // Built as a dictionary rather than an anonymous type because adapters need different
            // extra keys (debugpy wants the interpreter to run the program with; without it the
            // program silently runs under whatever interpreter the adapter itself started from).
            var launchArgs = new Dictionary<string, object?>
            {
                ["type"] = adapter.LaunchType,
                ["request"] = "launch",
                ["name"] = "CluadeX debug",
                ["program"] = program,
                ["cwd"] = psi.WorkingDirectory,
                ["console"] = "internalConsole",
                ["stopOnEntry"] = false,
                ["justMyCode"] = true,
            };
            // debugpy rejects the request outright if BOTH spellings are present
            // ("'pythonPath' is not valid if 'python' is specified") — send only the modern one.
            if (adapter.Id == "debugpy")
                launchArgs["python"] = adapter.Executable;

            var launchTask = SendRequestAsync("launch", launchArgs, ct);

            if (!await initializedEvent)
            {
                var launched = await launchTask;
                return new DebugStartResult
                {
                    Error = launched.Success
                        ? "Adapter never sent 'initialized'."
                        : $"launch failed: {launched.Message}",
                };
            }

            var verified = new Dictionary<string, List<DapBreakpoint>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (file, lines) in breakpoints)
            {
                if (lines.Count == 0) continue;
                var bps = await SetBreakpointsAsync(file, lines, ct);
                verified[file] = bps;
            }

            await SendRequestAsync("configurationDone", new { }, ct);

            var launchResult = await launchTask;
            if (!launchResult.Success)
                return new DebugStartResult { Error = $"launch failed: {launchResult.Message}" };

            return new DebugStartResult { Started = true, Breakpoints = verified };
        }
        catch (Exception ex)
        {
            StopSession();
            return new DebugStartResult { Error = ex.Message };
        }
    }

    /// <summary>
    /// Push the breakpoint set for one file. DAP is declarative here — the full list replaces
    /// whatever was set, so a removed breakpoint is expressed by simply omitting it.
    /// </summary>
    public async Task<List<DapBreakpoint>> SetBreakpointsAsync(string file, List<int> lines, CancellationToken ct = default)
    {
        var result = new List<DapBreakpoint>();
        if (!IsRunning) return result;

        var response = await SendRequestAsync("setBreakpoints", new
        {
            source = new { path = file, name = Path.GetFileName(file) },
            breakpoints = lines.Select(l => new { line = l }).ToArray(),
            lines = lines.ToArray(),
        }, ct);

        if (!response.Success || response.Body is not { } body) return result;

        if (body.TryGetProperty("breakpoints", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var bp in arr.EnumerateArray())
            {
                result.Add(new DapBreakpoint
                {
                    // A verified breakpoint may bind to a DIFFERENT line than requested (blank line,
                    // comment, optimised-away code). Report where it really landed.
                    Line = bp.TryGetProperty("line", out var l) ? l.GetInt32() : (i < lines.Count ? lines[i] : 0),
                    RequestedLine = i < lines.Count ? lines[i] : 0,
                    Verified = bp.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True,
                    Message = bp.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "",
                });
                i++;
            }
        }

        BreakpointsVerified?.Invoke(file, result);
        return result;
    }

    public Task ContinueAsync(CancellationToken ct = default) => ResumeAsync("continue", ct);
    public Task StepOverAsync(CancellationToken ct = default) => ResumeAsync("next", ct);
    public Task StepIntoAsync(CancellationToken ct = default) => ResumeAsync("stepIn", ct);
    public Task StepOutAsync(CancellationToken ct = default) => ResumeAsync("stepOut", ct);

    private async Task ResumeAsync(string command, CancellationToken ct)
    {
        if (!IsRunning || !IsPaused) return;
        int thread = StoppedThreadId ?? 1;

        IsPaused = false;
        StoppedThreadId = null;
        Continued?.Invoke();

        var response = await SendRequestAsync(command, new { threadId = thread }, ct);
        if (!response.Success)
        {
            // The resume was refused — we are still stopped. Reverting matters: leaving the UI in
            // "running" would grey out the step buttons for a session that never moved.
            IsPaused = true;
            StoppedThreadId = thread;
            Output?.Invoke($"[{command} failed] {response.Message}");
            Stopped?.Invoke("error", thread);
        }
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        if (!IsRunning || IsPaused) return;
        await SendRequestAsync("pause", new { threadId = StoppedThreadId ?? 1 }, ct);
    }

    /// <summary>Where execution is stopped, innermost frame first.</summary>
    public async Task<List<DapStackFrame>> GetStackTraceAsync(CancellationToken ct = default)
    {
        var frames = new List<DapStackFrame>();
        if (!IsRunning || !IsPaused) return frames;

        var response = await SendRequestAsync("stackTrace", new
        {
            threadId = StoppedThreadId ?? 1,
            startFrame = 0,
            levels = 40,
        }, ct);

        if (!response.Success || response.Body is not { } body) return frames;
        if (!body.TryGetProperty("stackFrames", out var arr) || arr.ValueKind != JsonValueKind.Array) return frames;

        foreach (var f in arr.EnumerateArray())
        {
            string path = "";
            if (f.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object)
                path = src.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";

            frames.Add(new DapStackFrame
            {
                Id = f.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                Name = f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                FilePath = path,
                Line = f.TryGetProperty("line", out var l) ? l.GetInt32() : 0,
                Column = f.TryGetProperty("column", out var c) ? c.GetInt32() : 0,
            });
        }
        return frames;
    }

    /// <summary>Locals/arguments for a frame, flattened one level (children fetch on expand).</summary>
    public async Task<List<DapVariable>> GetVariablesAsync(int frameId, CancellationToken ct = default)
    {
        var variables = new List<DapVariable>();
        if (!IsRunning || !IsPaused) return variables;

        var scopes = await SendRequestAsync("scopes", new { frameId }, ct);
        if (!scopes.Success || scopes.Body is not { } scopeBody) return variables;
        if (!scopeBody.TryGetProperty("scopes", out var scopeArr) || scopeArr.ValueKind != JsonValueKind.Array)
            return variables;

        foreach (var scope in scopeArr.EnumerateArray())
        {
            // Globals can be thousands of entries and are almost never what you're looking at.
            bool expensive = scope.TryGetProperty("expensive", out var e) && e.ValueKind == JsonValueKind.True;
            if (expensive) continue;

            string scopeName = scope.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "";
            int reference = scope.TryGetProperty("variablesReference", out var vr) ? vr.GetInt32() : 0;
            if (reference == 0) continue;

            foreach (var v in await GetChildVariablesAsync(reference, ct))
            {
                v.Scope = scopeName;
                variables.Add(v);
                if (variables.Count >= 300) return variables;   // a runaway scope must not freeze the panel
            }
        }
        return variables;
    }

    public async Task<List<DapVariable>> GetChildVariablesAsync(int variablesReference, CancellationToken ct = default)
    {
        var result = new List<DapVariable>();
        if (!IsRunning || variablesReference == 0) return result;

        var response = await SendRequestAsync("variables", new { variablesReference }, ct);
        if (!response.Success || response.Body is not { } body) return result;
        if (!body.TryGetProperty("variables", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;

        foreach (var v in arr.EnumerateArray())
        {
            result.Add(new DapVariable
            {
                Name = v.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Value = v.TryGetProperty("value", out var val) ? val.GetString() ?? "" : "",
                Type = v.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                VariablesReference = v.TryGetProperty("variablesReference", out var vr) ? vr.GetInt32() : 0,
            });
            if (result.Count >= 300) break;
        }
        return result;
    }

    /// <summary>Evaluate an expression in a frame — the debug console / watch window.</summary>
    public async Task<string> EvaluateAsync(string expression, int frameId, CancellationToken ct = default)
    {
        if (!IsRunning) return "(no debug session)";

        var response = await SendRequestAsync("evaluate", new
        {
            expression,
            frameId,
            context = "repl",
        }, ct);

        if (!response.Success) return $"error: {response.Message}";
        if (response.Body is not { } body) return "";
        return body.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
    }

    /// <summary>Ask the adapter to terminate the debuggee, then tear the process down regardless —
    /// a debugger that can't be stopped is worse than no debugger.</summary>
    public async Task StopAsync()
    {
        if (!IsRunning) { StopSession(); return; }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await SendRequestAsync("disconnect", new { terminateDebuggee = true }, cts.Token);
        }
        catch { /* falling through to the hard kill is the point */ }
        finally { StopSession(); }
    }

    private void StopSession()
    {
        IsPaused = false;
        StoppedThreadId = null;
        ActiveAdapter = null;

        try { _readCts?.Cancel(); } catch { }
        _readCts = null;

        try
        {
            if (_adapter is { HasExited: false })
            {
                try { _adapter.Kill(true); } catch { }
            }
            _adapter?.Dispose();
        }
        catch { }
        _adapter = null;
        _writer = null;
        _reader = null;

        lock (_lock)
        {
            foreach (var tcs in _pending.Values)
                tcs.TrySetResult(new DapResponse { Success = false, Message = "session ended" });
            _pending.Clear();
        }
    }

    // ═══════════════════════════ Protocol plumbing ═══════════════════════════

    private readonly Dictionary<string, List<TaskCompletionSource<bool>>> _eventWaiters = new();

    private Task<bool> WaitForEventAsync(string eventName, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        lock (_lock)
        {
            if (!_eventWaiters.TryGetValue(eventName, out var list))
                _eventWaiters[eventName] = list = new List<TaskCompletionSource<bool>>();
            list.Add(tcs);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                await tcs.Task.WaitAsync(cts.Token);
            }
            catch { tcs.TrySetResult(false); }
        });

        return tcs.Task;
    }

    private void SignalEvent(string eventName)
    {
        List<TaskCompletionSource<bool>>? waiters;
        lock (_lock)
        {
            if (!_eventWaiters.TryGetValue(eventName, out waiters)) return;
            _eventWaiters.Remove(eventName);
        }
        foreach (var w in waiters) w.TrySetResult(true);
    }

    private async Task<DapResponse> SendRequestAsync(string command, object arguments, CancellationToken ct)
    {
        if (_writer == null) return new DapResponse { Success = false, Message = "adapter not running" };

        int id = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<DapResponse>();
        lock (_lock) _pending[id] = tcs;

        try
        {
            await SendAsync(new { seq = id, type = "request", command, arguments }, ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (Exception ex)
        {
            return new DapResponse { Success = false, Message = ex.Message };
        }
        finally
        {
            lock (_lock) _pending.Remove(id);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private async Task SendAsync(object message, CancellationToken ct)
    {
        if (_writer == null) return;
        string json = JsonSerializer.Serialize(message, JsonOpts);
        string header = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n";

        // Requests are fired from UI handlers and background tasks alike; interleaving two writes
        // would corrupt the framing and wedge the session.
        await _writeLock.WaitAsync(ct);
        try
        {
            await _writer.WriteAsync(header.AsMemory(), ct);
            await _writer.WriteAsync(json.AsMemory(), ct);
            await _writer.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>
    /// Reads raw BYTES, not lines. Content-Length is a byte count, and a non-ASCII variable value
    /// (a Thai string in a watch) makes char counts and byte counts disagree — reading by character
    /// desynchronises the stream and every later message is garbage.
    /// </summary>
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var stream = _reader;
        if (stream == null) return;

        var buffer = new List<byte>(8192);
        var chunk = new byte[8192];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(chunk, ct);
                if (read == 0) break;                      // adapter closed stdout
                buffer.AddRange(chunk.AsSpan(0, read).ToArray());

                while (true)
                {
                    int headerEnd = IndexOfHeaderEnd(buffer);
                    if (headerEnd < 0) break;

                    string header = Encoding.ASCII.GetString(buffer.ToArray(), 0, headerEnd);
                    int contentLength = ParseContentLength(header);
                    if (contentLength < 0) { buffer.RemoveRange(0, headerEnd + 4); continue; }

                    int bodyStart = headerEnd + 4;
                    if (buffer.Count - bodyStart < contentLength) break;   // wait for the rest

                    string body = Encoding.UTF8.GetString(buffer.ToArray(), bodyStart, contentLength);
                    buffer.RemoveRange(0, bodyStart + contentLength);

                    try { Dispatch(body); }
                    catch { /* one bad message must not kill the session */ }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* stream closed */ }

        if (!ct.IsCancellationRequested)
        {
            IsPaused = false;
            Terminated?.Invoke("adapter exited");
        }
    }

    private static int IndexOfHeaderEnd(List<byte> buffer)
    {
        for (int i = 0; i + 3 < buffer.Count; i++)
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                return i;
        return -1;
    }

    private static int ParseContentLength(string header)
    {
        foreach (var line in header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
            return int.TryParse(line["Content-Length:".Length..].Trim(), out int n) ? n : -1;
        }
        return -1;
    }

    private void Dispatch(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

        if (type == "response")
        {
            int requestSeq = root.TryGetProperty("request_seq", out var rs) ? rs.GetInt32() : -1;
            var response = new DapResponse
            {
                Success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True,
                Message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "",
                Body = root.TryGetProperty("body", out var b) ? b.Clone() : null,
            };

            TaskCompletionSource<DapResponse>? tcs;
            lock (_lock) _pending.TryGetValue(requestSeq, out tcs);
            tcs?.TrySetResult(response);
            return;
        }

        if (type != "event") return;

        string name = root.TryGetProperty("event", out var e) ? e.GetString() ?? "" : "";
        root.TryGetProperty("body", out var eventBody);
        SignalEvent(name);

        switch (name)
        {
            case "stopped":
            {
                string reason = eventBody.ValueKind == JsonValueKind.Object
                    && eventBody.TryGetProperty("reason", out var r) ? r.GetString() ?? "stopped" : "stopped";
                int threadId = eventBody.ValueKind == JsonValueKind.Object
                    && eventBody.TryGetProperty("threadId", out var ti) ? ti.GetInt32() : 1;
                IsPaused = true;
                StoppedThreadId = threadId;
                Stopped?.Invoke(reason, threadId);
                break;
            }
            case "continued":
                IsPaused = false;
                StoppedThreadId = null;
                Continued?.Invoke();
                break;
            case "output":
            {
                if (eventBody.ValueKind != JsonValueKind.Object) break;
                string text = eventBody.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                if (text.Length > 0) Output?.Invoke(text.TrimEnd('\n', '\r'));
                break;
            }
            case "terminated":
                IsPaused = false;
                StoppedThreadId = null;
                Terminated?.Invoke("program terminated");
                break;
            case "exited":
            {
                int code = eventBody.ValueKind == JsonValueKind.Object
                    && eventBody.TryGetProperty("exitCode", out var c) ? c.GetInt32() : 0;
                Output?.Invoke($"[process exited with code {code}]");
                break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopSession();
        _writeLock.Dispose();
    }
}

// ═══════════════════════════ Models ═══════════════════════════

public sealed class DebugAdapterInfo
{
    public string Id { get; set; } = "";
    public string Language { get; set; } = "";
    public string Executable { get; set; } = "";
    public string Arguments { get; set; } = "";
    /// <summary>DAP "type" the adapter expects in the launch request.</summary>
    public string LaunchType { get; set; } = "";
    public bool IsAvailable { get; set; }
    public string InstallHint { get; set; } = "";

    public string StatusLabel => IsAvailable ? "installed" : "not installed";
}

public sealed class DapResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public JsonElement? Body { get; set; }
}

public sealed class DebugStartResult
{
    public bool Started { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, List<DapBreakpoint>> Breakpoints { get; set; } = new();
}

public sealed class DapBreakpoint
{
    public int Line { get; set; }
    /// <summary>Where the user clicked — may differ from <see cref="Line"/> if the adapter moved it.</summary>
    public int RequestedLine { get; set; }
    public bool Verified { get; set; }
    public string Message { get; set; } = "";
    public bool Moved => Verified && RequestedLine > 0 && Line != RequestedLine;
}

public sealed class DapStackFrame
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }

    public string FileName => string.IsNullOrEmpty(FilePath) ? "" : Path.GetFileName(FilePath);
    public string Location => string.IsNullOrEmpty(FilePath) ? "" : $"{FileName}:{Line}";
}

public sealed class DapVariable
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Type { get; set; } = "";
    public string Scope { get; set; } = "";
    public int VariablesReference { get; set; }

    public bool HasChildren => VariablesReference != 0;
    public string Display => string.IsNullOrEmpty(Type) ? Value : $"{Value}";
}
