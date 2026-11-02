using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CluadeX.Services.Lsp;

/// <summary>
/// One language-server process and the JSON-RPC conversation with it over stdio (Content-Length framing).
///
/// Framing is done on BYTES: Content-Length counts UTF-8 bytes, so reading it as a count of characters (what
/// <see cref="LspClientService"/> does) cuts a message that holds a Thai comment or an emoji in the middle and every
/// later message is read from the wrong place. Every outgoing message goes through one queue drained by one writer,
/// so a didChange can never overtake the didChange before it. Requests the server sends us (workspace/configuration,
/// client/registerCapability, …) are answered — a server that waits for an answer we never give stops publishing.
/// </summary>
public sealed class LspConnection : IDisposable
{
    /// <summary>A message larger than this is not an LSP message but a broken stream — stop rather than allocate it.</summary>
    private const int MaxMessageBytes = 64 * 1024 * 1024;

    private readonly Process _process;
    private readonly Stream _stdout;
    private readonly Stream _stdin;
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly StringBuilder _stderrTail = new();
    private long _nextId;
    private int _exitedRaised;
    private bool _disposed;

    /// <summary>A server → client notification: (method, params). Raised on the reader thread.</summary>
    public event Action<string, JsonElement>? Notification;
    /// <summary>The process ended or its stdout closed. Raised once, on a background thread.</summary>
    public event Action? Exited;

    /// <summary>What workspace/workspaceFolders answers: the project folder the server was started for.</summary>
    public IReadOnlyList<string> WorkspaceFolderUris { get; set; } = Array.Empty<string>();

    public int ProcessId { get; }
    public bool IsAlive => !_disposed && Volatile.Read(ref _exitedRaised) == 0 && !HasExited(_process);

    /// <summary>The last lines the server wrote to stderr — the reason it gives when it will not start.</summary>
    public string StderrTail { get { lock (_stderrTail) return _stderrTail.ToString().Trim(); } }

    private LspConnection(Process process)
    {
        _process = process;
        ProcessId = process.Id;
        _stdout = process.StandardOutput.BaseStream;
        _stdin = process.StandardInput.BaseStream;
        _stderrDrained = Task.Run(DrainStderrAsync);
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(WriteLoopAsync);
    }

    private readonly Task _stderrDrained;

    /// <summary>Start <paramref name="exe"/> and connect to its stdio. A .cmd / .bat shim (what npm installs on
    /// Windows) runs through cmd.exe, which is how Windows runs those anyway — said explicitly so the arguments reach
    /// the script unchanged.</summary>
    public static LspConnection Start(string exe, string args, string workingDirectory)
    {
        string ext = Path.GetExtension(exe).ToLowerInvariant();
        bool script = ext is ".cmd" or ".bat";
        var psi = new ProcessStartInfo
        {
            FileName = script ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : exe,
            Arguments = script ? $"/d /s /c \"\"{exe}\" {args}\"" : args,
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = Encoding.UTF8,
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("The language server did not start.");
        // Dies with CluadeX even on a crash (job object) — a language server left behind holds a whole solution in memory.
        Background.AppChildren.Adopt(process);
        return new LspConnection(process);
    }

    // ═══════════════════════════ Sending ═══════════════════════════

    public void Notify(string method, object? parameters)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters != null) message["params"] = JsonSerializer.SerializeToNode(parameters);
        Enqueue(message);
    }

    /// <summary>Send a request and wait for its answer (default = an "undefined" element when the server answered
    /// null). Throws <see cref="TimeoutException"/> after <paramref name="timeout"/>, and when the server is gone.</summary>
    public async Task<JsonElement> RequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken ct = default)
    {
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters != null) message["params"] = JsonSerializer.SerializeToNode(parameters);
            Enqueue(message);
            if (!IsAlive) throw new IOException("The language server is not running.");
            try { return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                // Tell the server to drop the work instead of finishing an answer nobody reads.
                Notify("$/cancelRequest", new { id });
                throw;
            }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private void Enqueue(JsonObject message)
    {
        if (_disposed) return;
        byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var frame = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, frame, 0, header.Length);
        Buffer.BlockCopy(body, 0, frame, header.Length, body.Length);
        _outgoing.Writer.TryWrite(frame);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            var reader = _outgoing.Reader;
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                // Everything queued goes out, then one flush: a burst costs one round trip, not one per message.
                while (reader.TryRead(out var frame))
                    await _stdin.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
                await _stdin.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
        }
        catch { /* the server's stdin closed — the reader side reports the exit */ }
    }

    // ═══════════════════════════ Receiving ═══════════════════════════

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int length = await ReadHeaderAsync().ConfigureAwait(false);
                if (length < 0) break;                       // end of stream
                if (length == 0) continue;                   // a header block without a length: skip it
                if (length > MaxMessageBytes) break;
                var body = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = await _stdout.ReadAsync(body.AsMemory(read, length - read), _cts.Token).ConfigureAwait(false);
                    if (n == 0) { read = -1; break; }
                    read += n;
                }
                if (read < 0) break;
                Dispatch(body);
            }
        }
        catch { /* stream closed or cancelled */ }
        finally
        {
            foreach (var p in _pending.Values) p.TrySetException(new IOException("The language server stopped."));
            // A crashing server says why on stderr just before it goes: let that last line arrive before reporting.
            try { await _stderrDrained.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
            RaiseExited();
        }
    }

    private readonly byte[] _one = new byte[1];

    /// <summary>Read header lines up to the blank line; the Content-Length, 0 when absent, -1 at end of stream.</summary>
    private async Task<int> ReadHeaderAsync()
    {
        int length = 0;
        var line = new StringBuilder();
        while (true)
        {
            int n = await _stdout.ReadAsync(_one, _cts.Token).ConfigureAwait(false);
            if (n == 0) return -1;
            char c = (char)_one[0];
            if (c == '\r') continue;
            if (c != '\n')
            {
                if (line.Length < 1024) line.Append(c);
                continue;
            }
            if (line.Length == 0) return length;             // the blank line that ends the headers
            string h = line.ToString();
            line.Clear();
            int colon = h.IndexOf(':');
            if (colon > 0 && h[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(h[(colon + 1)..].Trim(), out int len))
                length = len;
        }
    }

    private void Dispatch(byte[] body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch { return; /* not JSON: ignore this one message */ }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            bool hasId = root.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
            if (root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String)
            {
                string method = m.GetString() ?? "";
                var p = root.TryGetProperty("params", out var pe) ? pe.Clone() : default;
                if (hasId) Answer(id, method, p);
                else
                {
                    try { Notification?.Invoke(method, p); }
                    catch { /* a listener's bug must not stop the reader */ }
                }
                return;
            }
            if (hasId && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long key)
                && _pending.TryGetValue(key, out var tcs))
            {
                if (root.TryGetProperty("error", out var error))
                    tcs.TrySetException(new LspRequestException(error.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : error.ToString()));
                else
                    tcs.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            }
        }
    }

    /// <summary>Answer a request the server sent us. Unknown methods get MethodNotFound, which every server
    /// handles; the ones below are those servers really wait on.</summary>
    private void Answer(JsonElement id, string method, JsonElement p)
    {
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()) };
        switch (method)
        {
            case "workspace/configuration":
                // One entry per item asked; null = "no settings, use your defaults" (pyright waits on this).
                int count = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("items", out var items)
                    && items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;
                var nulls = new JsonArray();
                for (int i = 0; i < count; i++) nulls.Add(null);
                reply["result"] = nulls;
                break;
            case "workspace/workspaceFolders":
                var folders = new JsonArray();
                foreach (var uri in WorkspaceFolderUris)
                    folders.Add(new JsonObject { ["uri"] = uri, ["name"] = Path.GetFileName(LspPaths.UriToPath(uri).TrimEnd('\\')) });
                reply["result"] = folders;
                break;
            case "client/registerCapability":
            case "client/unregisterCapability":
            case "window/workDoneProgress/create":
            case "window/showMessageRequest":
            case "workspace/semanticTokens/refresh":
            case "workspace/inlayHint/refresh":
            case "workspace/codeLens/refresh":
            case "workspace/diagnostic/refresh":
                reply["result"] = null;
                break;
            default:
                reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found: " + method };
                break;
        }
        Enqueue(reply);
    }

    private async Task DrainStderrAsync()
    {
        // Read it always: a server that fills the stderr pipe while nobody reads blocks on its next log line.
        try
        {
            var reader = _process.StandardError;
            var buffer = new char[1024];
            int n;
            while ((n = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                lock (_stderrTail)
                {
                    _stderrTail.Append(buffer, 0, n);
                    if (_stderrTail.Length > 4000) _stderrTail.Remove(0, _stderrTail.Length - 2000);
                }
            }
        }
        catch { }
    }

    private void RaiseExited()
    {
        if (Interlocked.Exchange(ref _exitedRaised, 1) != 0) return;
        try { Exited?.Invoke(); } catch { }
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    // ═══════════════════════════ Stopping ═══════════════════════════

    /// <summary>The polite way out: shutdown request, exit notification, then wait. Whatever is still running after
    /// <paramref name="budget"/> is killed with its whole process tree (an npm shim's node.exe included).</summary>
    public async Task StopAsync(TimeSpan budget)
    {
        if (_disposed) return;
        var deadline = DateTime.UtcNow + budget;
        if (IsAlive)
        {
            try { await RequestAsync("shutdown", null, Half(budget)).ConfigureAwait(false); } catch { }
            try
            {
                Notify("exit", null);
                var left = deadline - DateTime.UtcNow;
                if (left > TimeSpan.Zero)
                {
                    using var wait = new CancellationTokenSource(left);
                    await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                }
            }
            catch { }
        }
        Dispose();
    }

    private static TimeSpan Half(TimeSpan t) => TimeSpan.FromMilliseconds(Math.Max(50, t.TotalMilliseconds / 2));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        try { _cts.Cancel(); } catch { }
        _outgoing.Writer.TryComplete();
        try { _process.Dispose(); } catch { }
        RaiseExited();
    }
}

public sealed class LspRequestException(string message) : Exception(message);

/// <summary>file:// URIs ↔ Windows paths. Servers write the same file several ways (file:///C:/a, file:///c%3A/a,
/// file:///c:/a), so a received URI is decoded and compared as a full path, case-insensitively.</summary>
public static class LspPaths
{
    public static string PathToUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    public static string UriToPath(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return "";
        string s;
        try { s = Uri.UnescapeDataString(uri); } catch { s = uri; }
        if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            s = s[5..];
            if (s.StartsWith("//")) s = s[2..];
            // "/C:/x" → "C:/x"; a UNC host ("//server/share") keeps its two slashes.
            if (s.Length >= 3 && s[0] == '/' && char.IsLetter(s[1]) && s[2] == ':') s = s[1..];
            else if (s.Length > 0 && s[0] != '/' && !(s.Length >= 2 && s[1] == ':')) s = "//" + s;
        }
        s = s.Replace('/', '\\');
        try { return Path.GetFullPath(s); } catch { return s; }
    }

    public static bool SamePath(string? a, string? b)
        => a != null && b != null && string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); } catch { return path; }
    }
}
