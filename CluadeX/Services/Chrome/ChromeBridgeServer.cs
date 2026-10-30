using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using CluadeX.Services.Browser;

namespace CluadeX.Services.Chrome;

/// <summary>
/// The app's end of the Chrome connection: a pipe the native host (<see cref="ChromeNativeHost"/>) connects to, one
/// extension at a time. Messages are JSON with a 4-byte little-endian length, the framing Chrome uses for native
/// messaging. The app asks with {id, cmd, …} and gets {id, ok, result | error}; the extension also sends {ev, …}.
///
/// Threading: every member may be called from any thread. <see cref="MessageReceived"/> runs on the read loop, in
/// order: handlers must not block, and the element is valid only during the call.
/// </summary>
public sealed class ChromeBridgeServer : IDisposable
{
    /// <summary>Chrome drops a host that sends more than 1 MB in one message, and the whole connection with it, so a
    /// larger command is refused here instead.</summary>
    public const int MaxCommandBytes = 1_000_000;
    private const int MaxFrameBytes = 64 * 1024 * 1024;   // Chrome's own limit in the other direction

    private readonly string _pipeName;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task? _serving;
    private Channel<byte[]>? _outbox;
    private Task? _writing;
    private long _nextId;
    private volatile bool _connected;
    private volatile bool _paused;
    private volatile string? _version;
    private volatile string? _userAgent;
    private volatile string? _lastError;
    private bool _reloadAsked;

    public ChromeBridgeServer(string pipeName) => _pipeName = pipeName;

    /// <summary>The extension said hello on the open pipe.</summary>
    public bool IsConnected => _connected;
    /// <summary>The user paused CluadeX from the icon in Chrome (or pressed Cancel on the debugging bar).</summary>
    public bool Paused => _paused;
    public string? ExtensionVersion => _version;
    public string? UserAgent => _userAgent;
    /// <summary>Why the pipe could not be served, for the settings row and the log.</summary>
    public string? LastError => _lastError;

    /// <summary>The version CluadeX carries. An extension running another one (CluadeX updated its files) is told once
    /// to reload itself; an unpacked extension never does that on its own.</summary>
    public string? ExpectedVersion { get; set; }

    /// <summary>Connected, disconnected or paused. Any thread: UI listeners marshal.</summary>
    public event Action? StateChanged;
    /// <summary>The connection ended: whatever was open in Chrome is out of reach.</summary>
    public event Action? Disconnected;
    /// <summary>Anything the extension sends other than an answer. See the threading note on the class.</summary>
    public event Action<JsonElement>? MessageReceived;

    public void Start()
    {
        lock (_gate)
        {
            if (_serving != null) return;
            _stop = new CancellationTokenSource();
            _serving = Task.Run(() => ServeAsync(_stop.Token));
        }
    }

    public void Stop()
    {
        // What was posted last still goes out: closing the agent's window is the last thing the app says at exit.
        _outbox?.Writer.TryComplete();
        try { _writing?.Wait(TimeSpan.FromMilliseconds(500)); }
        catch (AggregateException ex) { Debug.WriteLine($"[chrome] stop: {ex.InnerException?.Message}"); }

        Task? serving;
        lock (_gate)
        {
            _stop?.Cancel();
            serving = _serving;
            _serving = null;
            _stop = null;
        }
        try { serving?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException ex) { Debug.WriteLine($"[chrome] stop: {ex.InnerException?.Message}"); }
    }

    public void Dispose() => Stop();

    /// <summary>Wait until an extension is connected. True when one is.</summary>
    public async Task<bool> WaitForConnectionAsync(TimeSpan timeout, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (!_connected && clock.Elapsed < timeout) await Task.Delay(100, ct);
        return _connected;
    }

    /// <summary>Send a command and wait for its answer. Throws <see cref="CdpException"/> when the extension refuses or
    /// the connection is gone, <see cref="TimeoutException"/> after <paramref name="timeout"/>.</summary>
    public async Task<JsonElement> RequestAsync(string cmd, Action<Utf8JsonWriter>? fields, TimeSpan timeout, CancellationToken ct)
    {
        long id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            Enqueue(Encode(id, cmd, fields));
            return await answer.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { throw new TimeoutException($"{cmd} did not answer within {timeout.TotalSeconds:0.#} s."); }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>Send a command without waiting for an answer; commands keep their order. A refusal or a lost
    /// connection is dropped: callers use this for what has no one to report to.</summary>
    public void Post(string cmd, Action<Utf8JsonWriter>? fields = null)
    {
        try { Enqueue(Encode(null, cmd, fields)); }
        catch (CdpException ex) { Debug.WriteLine($"[chrome] {cmd} not sent: {ex.Message}"); }
    }

    private void Enqueue(byte[] frame)
    {
        if (_outbox?.Writer.TryWrite(frame) != true) throw new CdpException("Chrome is not connected.");
    }

    private static byte[] Encode(long? id, string cmd, Action<Utf8JsonWriter>? fields)
    {
        using var body = new MemoryStream();
        body.Write(new byte[4]);
        using (var w = new Utf8JsonWriter(body))
        {
            w.WriteStartObject();
            if (id is long i) w.WriteNumber("id", i);
            w.WriteString("cmd", cmd);
            fields?.Invoke(w);
            w.WriteEndObject();
        }
        byte[] frame = body.ToArray();
        int length = frame.Length - 4;
        if (length > MaxCommandBytes) throw new CdpException($"{cmd} is too large for the Chrome connection ({length / 1024} KB, the limit is 1 MB).");
        BitConverter.TryWriteBytes(frame, length);
        return frame;
    }

    // ─────────────────────────── the pipe ───────────────────────────

    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                _lastError = null;
                await pipe.WaitForConnectionAsync(ct);
                await RunConnectionAsync(pipe, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                // The usual end of a connection is an IOException too, so only a pipe that could not be made is an error.
                if (pipe == null) _lastError = ex.Message;
                Debug.WriteLine($"[chrome] pipe: {ex.Message}");
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
            }
            finally
            {
                pipe?.Dispose();
                EndConnection();
            }
        }
    }

    /// <summary>This user only, one client, and only if no other process holds the name: whoever serves this pipe
    /// drives the user's Chrome.</summary>
    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        var sid = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("No Windows user to give the pipe to.");
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            _pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 64 * 1024, outBufferSize: 64 * 1024, pipeSecurity: security);
    }

    private async Task RunConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        using var ending = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writing = WriteLoopAsync(pipe, outbox.Reader, ending.Token);
        _writing = writing;
        _outbox = outbox;
        try { await ReadLoopAsync(pipe, ending.Token); }
        finally
        {
            _outbox = null;
            _writing = null;
            outbox.Writer.TryComplete();
            ending.Cancel();
            try { await writing; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private static async Task WriteLoopAsync(Stream pipe, ChannelReader<byte[]> outbox, CancellationToken ct)
    {
        await foreach (byte[] frame in outbox.ReadAllAsync(ct))
        {
            await pipe.WriteAsync(frame, ct);
            await pipe.FlushAsync(ct);
        }
    }

    private async Task ReadLoopAsync(Stream pipe, CancellationToken ct)
    {
        var header = new byte[4];
        while (true)
        {
            try { await pipe.ReadExactlyAsync(header, ct); }
            catch (EndOfStreamException) { return; }
            int length = BitConverter.ToInt32(header);
            if (length <= 0 || length > MaxFrameBytes) throw new InvalidDataException($"Bad frame length {length}.");
            var body = new byte[length];
            try { await pipe.ReadExactlyAsync(body, ct); }
            catch (EndOfStreamException) { return; }
            Dispatch(body);
        }
    }

    private void Dispatch(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var m = doc.RootElement;
        if (m.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out long id))
        {
            if (!_pending.TryRemove(id, out var waiter)) return;   // it timed out
            if (m.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                waiter.TrySetResult(m.TryGetProperty("result", out var result) ? result.Clone() : default);
            else
                waiter.TrySetException(new CdpException(WebView2Channel.CleanCdpError(m.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "")));
            return;
        }
        switch (m.TryGetProperty("ev", out var ev) ? ev.GetString() : null)
        {
            case "hello":
                OnHello(m);
                break;
            case "state":
                _paused = m.TryGetProperty("paused", out var paused) && paused.GetBoolean();
                RaiseStateChanged();
                break;
            default:
                try { MessageReceived?.Invoke(m); }
                catch (Exception ex) { Debug.WriteLine($"[chrome] message handler threw: {ex.Message}"); }
                break;
        }
    }

    private void OnHello(JsonElement m)
    {
        _version = m.TryGetProperty("version", out var v) ? v.GetString() : null;
        _userAgent = m.TryGetProperty("userAgent", out var ua) ? ua.GetString() : null;
        _paused = m.TryGetProperty("paused", out var p) && p.GetBoolean();
        Post("welcome");
        if (!string.IsNullOrEmpty(ExpectedVersion) && _version != ExpectedVersion && !_reloadAsked)
        {
            _reloadAsked = true;   // once: a build whose files did not reach the folder must not reload it forever
            Post("reload");
        }
        _connected = true;
        RaiseStateChanged();
    }

    private void EndConnection()
    {
        bool was = _connected;
        _connected = false;
        foreach (long id in _pending.Keys)
            if (_pending.TryRemove(id, out var waiter)) waiter.TrySetException(new CdpException("Chrome disconnected."));
        if (!was) return;
        try { Disconnected?.Invoke(); }
        catch (Exception ex) { Debug.WriteLine($"[chrome] disconnect handler threw: {ex.Message}"); }
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { Debug.WriteLine($"[chrome] state handler threw: {ex.Message}"); }
    }
}
