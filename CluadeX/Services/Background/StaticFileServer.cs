using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CluadeX.Services.Background;

/// <summary>
/// The built-in static file server behind bg_serve: serves a folder over http://127.0.0.1:PORT/ with nothing
/// installed. A plain TcpListener on 127.0.0.1 and [::1] only — never 0.0.0.0, so no firewall prompt and nothing
/// reachable from the network; not http.sys, so no urlacl and no admin rights.
///
/// Rules: HTTP/1.1 GET and HEAD (keep-alive), the MIME types a game or web app needs, Cache-Control: no-store (the
/// agent sees its edits on the next reload). A request is answered only for a loopback Host header (a DNS-rebinding
/// page cannot read the project). A path is refused unless it stays inside the root after decoding and
/// canonicalisation: no "..", no "%2e%2e", no backslash, no ':' (drive letters, alternate data streams), no device
/// names, no junction that leads out, no .git / .env. Every request — a 404 above all — is written to the process
/// output, so a missing sprite shows up in bg_output.
/// </summary>
public sealed class StaticFileServer : IDisposable
{
    public const int DefaultPortFrom = 8100;
    public const int DefaultPortTo = 8199;
    private const int MaxConnections = 64;
    private const int MaxHeadBytes = 16 * 1024;

    private readonly string _root;          // full path, no trailing separator
    private readonly string _rootPrefix;    // full path + separator
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private readonly ConcurrentDictionary<TcpClient, byte> _open = new();
    private TcpListener? _v4;
    private TcpListener? _v6;
    private int _stopped;
    private long _requests;

    /// <summary>The folder being served (full path).</summary>
    public string Root => _root;
    public int Port { get; }
    /// <summary>Unknown paths without a file extension get index.html (single-page apps).</summary>
    public bool Spa { get; }
    /// <summary>Also listening on [::1] (false only where IPv6 is switched off).</summary>
    public bool ListeningOnIPv6 => _v6 != null;
    public string Url => $"http://127.0.0.1:{Port}/";
    public long RequestCount => Interlocked.Read(ref _requests);
    public bool IsRunning => _stopped == 0;

    private StaticFileServer(string root, int port, bool spa, Action<string> log, TcpListener v4, TcpListener? v6)
    {
        _root = root;
        _rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        Port = port;
        Spa = spa;
        _log = log;
        _v4 = v4;
        _v6 = v6;
    }

    /// <summary>The ways a start can fail, for a localized message.</summary>
    public enum StartError { None, RootMissing, PortBusy, NoFreePort, BindFailed }

    /// <summary>
    /// Start serving <paramref name="root"/>. With <paramref name="port"/> null the first free port in
    /// 8100-8199 is used; a requested port that is busy fails (<see cref="StartError.PortBusy"/>) — the caller
    /// names its owner. Never throws.
    /// </summary>
    public static (StaticFileServer? Server, StartError Error, string? Detail) TryStart(string root, int? port, bool spa, Action<string> log)
    {
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
        catch (Exception ex) { return (null, StartError.RootMissing, ex.Message); }
        if (!Directory.Exists(full)) return (null, StartError.RootMissing, full);

        IEnumerable<int> candidates = port is { } p ? new[] { p } : Enumerable.Range(DefaultPortFrom, DefaultPortTo - DefaultPortFrom + 1);
        string? lastError = null;
        foreach (int candidate in candidates)
        {
            // A wildcard listener (python on [::]:8100) does not stop a loopback bind on Windows — but then two
            // servers answer one port. Skip anything that already listens, on any address.
            if (ReadinessProbe.IsListening(candidate))
            {
                if (port != null) return (null, StartError.PortBusy, candidate.ToString());
                continue;
            }
            TcpListener? v4 = null, v6 = null;
            try
            {
                v4 = new TcpListener(IPAddress.Loopback, candidate) { ExclusiveAddressUse = true };
                v4.Start(MaxConnections);
                try
                {
                    v6 = new TcpListener(IPAddress.IPv6Loopback, candidate) { ExclusiveAddressUse = true };
                    v6.Start(MaxConnections);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported
                                                     or SocketError.AddressNotAvailable or SocketError.ProtocolNotSupported)
                {
                    try { v6?.Stop(); } catch { }
                    v6 = null;   // no IPv6 on this machine: 127.0.0.1 alone is fine
                }
                var server = new StaticFileServer(full, candidate, spa, log, v4, v6);
                server.Begin();
                return (server, StartError.None, null);
            }
            catch (SocketException ex)
            {
                try { v4?.Stop(); } catch { }
                try { v6?.Stop(); } catch { }
                lastError = ex.Message;
                if (port != null)
                    return ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied
                        ? (null, StartError.PortBusy, candidate.ToString())
                        : (null, StartError.BindFailed, ex.Message);
            }
        }
        return (null, port == null ? StartError.NoFreePort : StartError.BindFailed, lastError);
    }

    private void Begin()
    {
        _ = AcceptLoop(_v4!);
        if (_v6 != null) _ = AcceptLoop(_v6);
    }

    private async Task AcceptLoop(TcpListener listener)
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { break; }   // stopped
            bool slot;
            try { slot = !ct.IsCancellationRequested && _slots.Wait(0); }
            catch (ObjectDisposedException) { slot = false; }   // disposed while this accept completed
            if (!slot)
            {
                // Over the connection cap (or stopping): drop it rather than queue without bound.
                try { client.Dispose(); } catch { }
                if (ct.IsCancellationRequested) break;
                continue;
            }
            _ = Task.Run(() => ServeConnectionAsync(client, ct));
        }
    }

    /// <summary>Stop listening and close every open connection. Idempotent.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        try { _cts.Cancel(); } catch { }
        try { _v4?.Stop(); } catch { }
        try { _v6?.Stop(); } catch { }
        foreach (var client in _open.Keys)
            try { client.Dispose(); } catch { }
        _open.Clear();
    }

    /// <summary>Stopped and its resources released (idempotent).</summary>
    public bool IsDisposed => _disposed != 0;
    private int _disposed;

    public void Dispose()
    {
        Stop();
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Dispose(); } catch { }
        // A connection still finishing calls Release in its finally — it is caught there.
        try { _slots.Dispose(); } catch { }
    }

    // ───────────────────────── one connection ─────────────────────────

    private async Task ServeConnectionAsync(TcpClient client, CancellationToken ct)
    {
        _open[client] = 0;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var buffer = new byte[MaxHeadBytes];
            int filled = 0;
            bool first = true;
            while (!ct.IsCancellationRequested)
            {
                int headEnd;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    // Slow or idle clients don't hold a slot forever.
                    idle.CancelAfter(first ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(5));
                    while ((headEnd = IndexOfHeadEnd(buffer, filled)) < 0)
                    {
                        if (filled == buffer.Length)
                        {
                            await WriteSimpleAsync(stream, 431, "Request Header Fields Too Large", "", keepAlive: false, head: false, ct);
                            return;
                        }
                        int n = await stream.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), idle.Token).ConfigureAwait(false);
                        if (n == 0) return;
                        filled += n;
                    }
                }
                string head = Encoding.Latin1.GetString(buffer, 0, headEnd);
                int consumed = headEnd + 4;
                Buffer.BlockCopy(buffer, consumed, buffer, 0, filled - consumed);
                filled -= consumed;

                bool keepAlive = await HandleRequestAsync(stream, head, ct).ConfigureAwait(false);
                if (!keepAlive) return;
                first = false;
            }
        }
        catch { /* client went away, timed out, or the server stopped */ }
        finally
        {
            _open.TryRemove(client, out _);
            try { client.Dispose(); } catch { }
            try { _slots.Release(); } catch { }
        }
    }

    private static int IndexOfHeadEnd(byte[] b, int length)
    {
        for (int i = 3; i < length; i++)
            if (b[i] == '\n' && b[i - 1] == '\r' && b[i - 2] == '\n' && b[i - 3] == '\r') return i - 3;
        return -1;
    }

    /// <summary>Answer one request. Returns whether the connection may carry another one.</summary>
    private async Task<bool> HandleRequestAsync(NetworkStream stream, string head, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            await WriteSimpleAsync(stream, 400, "Bad Request", "", false, false, ct);
            Log(400, "?", Printable(lines[0]), "malformed request line");
            return false;
        }
        string method = parts[0], target = parts[1];
        bool http11 = parts[2] == "HTTP/1.1";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }
        headers.TryGetValue("Connection", out var connection);
        bool keepAlive = http11
            ? !string.Equals(connection, "close", StringComparison.OrdinalIgnoreCase)
            : string.Equals(connection, "keep-alive", StringComparison.OrdinalIgnoreCase);

        bool isHead = method == "HEAD";
        if (method != "GET" && !isHead)
        {
            await WriteSimpleAsync(stream, 405, "Method Not Allowed", "Only GET and HEAD are served.", false, false, ct,
                extraHeaders: "Allow: GET, HEAD\r\n");
            Log(405, method, Printable(target), null);
            return false;
        }
        // A GET with a body is not something a browser sends; refuse instead of mis-framing the next request.
        if (headers.ContainsKey("Transfer-Encoding")
            || (headers.TryGetValue("Content-Length", out var cl) && cl != "0"))
        {
            await WriteSimpleAsync(stream, 400, "Bad Request", "A request body is not accepted.", false, isHead, ct);
            Log(400, method, Printable(target), "request body");
            return false;
        }

        // DNS rebinding guard: only a loopback Host may read the project.
        headers.TryGetValue("Host", out var host);
        if (!IsLoopbackHost(host))
        {
            await WriteSimpleAsync(stream, 403, "Forbidden", "Only http://127.0.0.1 / localhost may use this server.", false, isHead, ct);
            Log(403, method, Printable(target), $"Host '{Printable(host ?? "")}' is not this computer");
            return false;
        }

        if (!target.StartsWith('/') || target.StartsWith("//", StringComparison.Ordinal))
        {
            await WriteSimpleAsync(stream, 400, "Bad Request", "", false, isHead, ct);
            Log(400, method, Printable(target), "not a path");
            return false;
        }
        // The whole target, query included: a bare LF in "?a\nX-Injected: yes" used to reach the 301's Location
        // header as a header line of its own. A browser always percent-encodes these.
        if (target.Any(ch => ch < 0x20 || ch == 0x7F))
        {
            await WriteSimpleAsync(stream, 400, "Bad Request", "", false, isHead, ct);
            Log(400, method, Printable(target), "control character in the request target");
            return false;
        }
        int cut = target.IndexOfAny(new[] { '?', '#' });
        string rawPath = cut >= 0 ? target[..cut] : target;

        var (resolved, refusal) = Resolve(rawPath);
        if (resolved == null)
        {
            int code = refusal == "bad" ? 400 : 403;
            await WriteSimpleAsync(stream, code, code == 400 ? "Bad Request" : "Forbidden", "", keepAlive, isHead, ct);
            Log(code, method, Printable(rawPath), refusal == "bad" ? "undecodable path" : "refused: " + refusal);
            return keepAlive;
        }

        string display = Printable(SafeUnescape(rawPath) ?? rawPath);
        if (Directory.Exists(resolved))
        {
            if (!rawPath.EndsWith('/'))
            {
                // Relative URLs inside the page resolve against the folder only with the trailing slash.
                string location = rawPath + "/" + (cut >= 0 && target[cut] == '?' ? target[cut..] : "");
                await WriteSimpleAsync(stream, 301, "Moved Permanently", "", keepAlive, isHead, ct,
                    extraHeaders: $"Location: {location}\r\n");
                Log(301, method, display, null);
                return keepAlive;
            }
            string index = Path.Combine(resolved, "index.html");
            if (File.Exists(index) && !IsReparse(index))
                return await ServeFileAsync(stream, index, method, display, keepAlive, isHead, note: null, ct);
            await WriteListingAsync(stream, resolved, display, keepAlive, isHead, ct);
            Log(200, method, display, "folder listing");
            return keepAlive;
        }
        if (File.Exists(resolved))
            return await ServeFileAsync(stream, resolved, method, display, keepAlive, isHead, note: null, ct);

        if (Spa && Path.GetExtension(resolved).Length == 0)
        {
            string index = Path.Combine(_root, "index.html");
            if (File.Exists(index))
                return await ServeFileAsync(stream, index, method, display, keepAlive, isHead, note: "single-page fallback → index.html", ct);
        }

        await WriteSimpleAsync(stream, 404, "Not Found", "404 Not Found: " + display, keepAlive, isHead, ct);
        Log(404, method, display, "not found");
        return keepAlive;
    }

    // ───────────────────────── paths ─────────────────────────

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static string? SafeUnescape(string s)
    {
        try { return Uri.UnescapeDataString(s); } catch { return null; }
    }

    /// <summary>
    /// The full path a URL path names inside the root, or (null, reason). Decoded exactly once (so "%252e%252e"
    /// is the literal name "%2e%2e", never ".."), then every segment is checked before the path is built, and the
    /// built path is checked again after canonicalisation — and for junctions / symlinks that lead out.
    /// </summary>
    internal (string? Path, string? Refusal) Resolve(string rawPath)
    {
        string? decoded = SafeUnescape(rawPath);
        if (decoded == null) return (null, "bad");
        foreach (char ch in decoded)
        {
            if (ch == '\\') return (null, "backslash");
            if (ch == ':') return (null, "':' (drive letter or alternate data stream)");
            if (ch < 0x20 || ch == 0x7F) return (null, "control character");
        }
        var segments = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            if (seg is "." or "..") return (null, "path traversal");
            // Windows drops trailing dots and spaces ("..." and ".. " can mean ".."), so such names never pass.
            if (seg.EndsWith('.') || seg.EndsWith(' ')) return (null, "path traversal");
            string stem = seg.Split('.')[0];
            if (DeviceNames.Contains(stem)) return (null, "device name");
            if (seg.Equals(".git", StringComparison.OrdinalIgnoreCase) || seg.Equals(".svn", StringComparison.OrdinalIgnoreCase)
                || seg.Equals(".hg", StringComparison.OrdinalIgnoreCase) || seg.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
                return (null, "hidden");
            if (seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return (null, "invalid name");
        }

        string full;
        try { full = Path.GetFullPath(Path.Combine(new[] { _root }.Concat(segments).ToArray())); }
        catch { return (null, "bad"); }
        if (!string.Equals(full, _root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
            return (null, "outside the served folder");

        // 8.3 short names ("GIT~1", "ENV~1", "PROGRA~1") name the same file as the long name, so they would walk
        // straight past the .git / .env check above — Path.GetFullPath even expands them itself (".git\config"). A
        // browser never invents one: when the path the request spelled is not the path it really names, it is
        // refused (a real name like "v~2.png" is still served; a missing one is still a plain 404).
        if (segments.Any(s => ShortNameRx.IsMatch(s)))
        {
            string asked = string.Join(Path.DirectorySeparatorChar, segments);
            string actual = Path.GetRelativePath(LongPath(_root) ?? _root, LongPath(full) ?? full);
            if (!string.Equals(asked, actual, StringComparison.OrdinalIgnoreCase))
                return (null, "short (8.3) name — use the full name");
        }

        // A junction or symlink inside the root may point anywhere: follow it, and refuse if it leads out.
        string current = _root;
        foreach (var seg in segments)
        {
            current = Path.Combine(current, seg);
            if (!IsReparse(current)) continue;
            string? target = null;
            try
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            }
            catch { }
            if (target == null) return (null, "link");
            string t = Path.GetFullPath(target);
            if (!string.Equals(t, _root, StringComparison.OrdinalIgnoreCase) && !t.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
                return (null, "link leads outside the served folder");
        }
        return (full, null);
    }

    private static readonly System.Text.RegularExpressions.Regex ShortNameRx = new(@"~\d", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The path with every 8.3 short component expanded (GetLongPathName), or null when it does not exist
    /// or cannot be read.</summary>
    private static string? LongPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        try
        {
            var buffer = new char[1024];
            uint n = GetLongPathNameW(path, buffer, (uint)buffer.Length);
            if (n == 0) return null;
            if (n > buffer.Length)
            {
                buffer = new char[n];
                n = GetLongPathNameW(path, buffer, (uint)buffer.Length);
                if (n == 0 || n > buffer.Length) return null;
            }
            return Path.TrimEndingDirectorySeparator(new string(buffer, 0, (int)n));
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, [System.Runtime.InteropServices.Out] char[] longPath, uint bufferLength);

    private static bool IsReparse(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch { return false; }
    }

    private static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string h = host.Trim().ToLowerInvariant();
        if (h.StartsWith('['))
        {
            int close = h.IndexOf(']');
            if (close < 0) return false;
            h = h[..(close + 1)];
        }
        else
        {
            int colon = h.IndexOf(':');
            if (colon >= 0) h = h[..colon];
        }
        return h is "127.0.0.1" or "localhost" or "[::1]";
    }

    // ───────────────────────── responses ─────────────────────────

    private async Task<bool> ServeFileAsync(NetworkStream stream, string path, string method, string display,
        bool keepAlive, bool isHead, string? note, CancellationToken ct)
    {
        FileStream file;
        try
        {
            // Shared so an editor (or the agent) can save the file while a browser is loading it.
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            // It exists, but a program holds it open without sharing (an editor mid-save): "not found" would send
            // the reader hunting for a missing asset. Try again in a moment.
            await WriteSimpleAsync(stream, 503, "Service Unavailable", "503: " + display + " is in use by another program — try again.",
                keepAlive, isHead, ct, extraHeaders: "Retry-After: 1\r\n");
            Log(503, method, display, "in use by another program");
            return keepAlive;
        }
        catch (UnauthorizedAccessException)
        {
            await WriteSimpleAsync(stream, 403, "Forbidden", "403 Forbidden: " + display, keepAlive, isHead, ct);
            Log(403, method, display, "access denied");
            return keepAlive;
        }
        catch
        {
            await WriteSimpleAsync(stream, 404, "Not Found", "404 Not Found: " + display, keepAlive, isHead, ct);
            Log(404, method, display, "could not be opened");
            return keepAlive;
        }
        using (file)
        {
            long length = file.Length;
            string header =
                "HTTP/1.1 200 OK\r\n" +
                $"Content-Type: {MimeType(path)}\r\n" +
                $"Content-Length: {length}\r\n" +
                "Cache-Control: no-store\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                $"Connection: {(keepAlive ? "keep-alive" : "close")}\r\n" +
                "Server: CluadeX\r\n\r\n";
            using var write = CancellationTokenSource.CreateLinkedTokenSource(ct);
            write.CancelAfter(TimeSpan.FromSeconds(60));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), write.Token).ConfigureAwait(false);
            if (!isHead)
            {
                // Exactly Content-Length bytes, whatever happens to the file meanwhile (the agent rewriting
                // index.html while the browser reloads): bytes past it would be read as the next response; a file
                // that shrank must not leave the browser waiting for bytes that never come — close instead.
                var buffer = new byte[64 * 1024];
                long remaining = length;
                while (remaining > 0)
                {
                    int n = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), write.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    await stream.WriteAsync(buffer.AsMemory(0, n), write.Token).ConfigureAwait(false);
                    remaining -= n;
                }
                if (remaining > 0)
                {
                    Log(200, method, display, $"the file shrank while it was sent ({length - remaining:N0} of {length:N0} bytes) — connection closed");
                    return false;
                }
            }
            Log(200, method, display, note);
        }
        return keepAlive;
    }

    private static bool IsSharingViolation(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        return code is 32 /* ERROR_SHARING_VIOLATION */ or 33 /* ERROR_LOCK_VIOLATION */;
    }

    private async Task WriteListingAsync(NetworkStream stream, string folder, string display, bool keepAlive, bool isHead, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(display)).Append("</title>");
        sb.Append("<h1>").Append(WebUtility.HtmlEncode(display)).Append("</h1><ul>");
        try
        {
            foreach (var d in Directory.EnumerateDirectories(folder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(1000))
            {
                string name = Path.GetFileName(d);
                if (name.StartsWith(".git", StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append("<li><a href=\"").Append(Uri.EscapeDataString(name)).Append("/\">").Append(WebUtility.HtmlEncode(name)).Append("/</a></li>");
            }
            foreach (var f in Directory.EnumerateFiles(folder).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(1000))
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append("<li><a href=\"").Append(Uri.EscapeDataString(name)).Append("\">").Append(WebUtility.HtmlEncode(name)).Append("</a></li>");
            }
        }
        catch { }
        sb.Append("</ul>");
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        await WriteResponseAsync(stream, 200, "OK", "text/html; charset=utf-8", body, keepAlive, isHead, null, ct);
    }

    private static Task WriteSimpleAsync(NetworkStream stream, int code, string reason, string text, bool keepAlive, bool head,
        CancellationToken ct, string? extraHeaders = null)
    {
        var body = Encoding.UTF8.GetBytes(text.Length > 0 ? text + "\n" : $"{code} {reason}\n");
        return WriteResponseAsync(stream, code, reason, "text/plain; charset=utf-8", body, keepAlive, head, extraHeaders, ct);
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int code, string reason, string contentType, byte[] body,
        bool keepAlive, bool head, string? extraHeaders, CancellationToken ct)
    {
        string header =
            $"HTTP/1.1 {code} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            (extraHeaders ?? "") +
            $"Connection: {(keepAlive ? "keep-alive" : "close")}\r\n" +
            "Server: CluadeX\r\n\r\n";
        using var write = CancellationTokenSource.CreateLinkedTokenSource(ct);
        write.CancelAfter(TimeSpan.FromSeconds(15));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), write.Token).ConfigureAwait(false);
        if (!head) await stream.WriteAsync(body, write.Token).ConfigureAwait(false);
    }

    private void Log(int code, string method, string path, string? note)
    {
        try { _log($"{code} {method} {path}{(string.IsNullOrEmpty(note) ? "" : " (" + note + ")")}"); }
        catch { }
    }

    /// <summary>A request-supplied string made safe to print in one log line.</summary>
    private static string Printable(string s)
    {
        var sb = new StringBuilder(Math.Min(s.Length, 200));
        foreach (char ch in s)
        {
            if (sb.Length >= 200) { sb.Append('…'); break; }
            sb.Append(ch < 0x20 || ch == 0x7F ? '?' : ch);
        }
        return sb.ToString();
    }

    // ───────────────────────── MIME ─────────────────────────

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".htm"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8", [".mjs"] = "text/javascript; charset=utf-8", [".cjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8", [".map"] = "application/json; charset=utf-8",
        [".webmanifest"] = "application/manifest+json; charset=utf-8",
        [".wasm"] = "application/wasm",
        [".svg"] = "image/svg+xml", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".avif"] = "image/avif", [".ico"] = "image/x-icon", [".bmp"] = "image/bmp",
        [".ogg"] = "audio/ogg", [".oga"] = "audio/ogg", [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".m4a"] = "audio/mp4",
        [".flac"] = "audio/flac", [".mp4"] = "video/mp4", [".webm"] = "video/webm", [".ogv"] = "video/ogg",
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".otf"] = "font/otf",
        [".txt"] = "text/plain; charset=utf-8", [".md"] = "text/markdown; charset=utf-8", [".csv"] = "text/csv; charset=utf-8",
        [".xml"] = "application/xml", [".pdf"] = "application/pdf", [".zip"] = "application/zip",
        [".glb"] = "model/gltf-binary", [".gltf"] = "model/gltf+json",
    };

    /// <summary>The Content-Type for a file name (application/octet-stream when unknown).</summary>
    public static string MimeType(string path) =>
        Mime.TryGetValue(Path.GetExtension(path), out var type) ? type : "application/octet-stream";
}
