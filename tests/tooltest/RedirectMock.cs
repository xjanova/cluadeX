using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ToolTest;

/// <summary>
/// A tiny HTTP server on the loopback interface for the web_fetch redirect checks. Raw TCP rather than
/// HttpListener so ONE server answers under two different host names — 127.0.0.1 and localhost — which is
/// what makes "a redirect to another host" testable without leaving the machine. Every request is recorded
/// as host+path in <see cref="Seen"/>, so a check can prove that nothing reached a host.
///
///   /same         302 → /final (same host)            /final     200 FINAL-SAME-HOST
///   /cross        302 → http://localhost:{port}/landing?d=secret (another host)
///   /landing      200 LANDED-OTHER-HOST               /loop/{n}  302 → /loop/{n+1} (never ends)
///   /file         302 → file:///C:/Windows/win.ini    /missing   404
///   /thai         200 Thai text (UTF-8)               /latin1    200 "café" in ISO-8859-1
///   /trickle      200, then one body byte every 200 ms, forever (until the client hangs up)
///   /firehose     200, then an endless body as fast as the client reads it
///   /slow/{n}     waits 700 ms, then 302 → /slow/{n+1}; /slow/4 waits 700 ms, then 200 SLOW-FINAL
///                 (5 slow hops ≈ 3.5 s in all, each well under a 2 s budget)
/// </summary>
internal sealed class RedirectMock : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    public int Port { get; }
    public ConcurrentQueue<string> Seen { get; private set; } = new();

    public RedirectMock()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);   // loopback only — never a firewall prompt
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public void Clear() => Seen = new ConcurrentQueue<string>();

    /// <summary>How long each /slow/{n} hop takes before it answers.</summary>
    public const int SlowHopMs = 700;

    private long _trickleSent, _firehoseSent;
    /// <summary>Bytes of /trickle and /firehose bodies written so far.</summary>
    public long TrickleSent => Interlocked.Read(ref _trickleSent);
    public long FirehoseSent => Interlocked.Read(ref _firehoseSent);
    /// <summary>The client closed the connection while the endless body was still being sent.</summary>
    public volatile bool TrickleClientLeft, FirehoseClientLeft;

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => Handle(client));
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buf = new byte[16384];
                int len = 0;
                while (len < buf.Length)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(len, buf.Length - len));
                    if (n == 0) break;
                    len += n;
                    if (Encoding.ASCII.GetString(buf, 0, len).Contains("\r\n\r\n")) break;
                }
                string head = Encoding.ASCII.GetString(buf, 0, len);
                var lines = head.Split("\r\n");
                var parts = lines[0].Split(' ');
                string path = parts.Length > 1 ? parts[1] : "/";
                string host = lines.Skip(1).FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim() ?? "";
                Seen.Enqueue(host + path);

                // Bodies that never end (no Content-Length, Connection: close — the body runs until the socket
                // closes): /trickle sends one byte every 200 ms, /firehose as fast as the client takes it.
                // Both stop only when the CLIENT hangs up; that moment is recorded.
                if (path is "/trickle" or "/firehose")
                {
                    bool fire = path == "/firehose";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nConnection: close\r\n\r\n"));
                    await stream.FlushAsync();
                    byte[] chunk = Encoding.ASCII.GetBytes(fire ? new string('a', 64 * 1024) : "x");
                    try
                    {
                        while (!_cts.IsCancellationRequested)
                        {
                            await stream.WriteAsync(chunk, _cts.Token);
                            await stream.FlushAsync(_cts.Token);
                            if (fire) Interlocked.Add(ref _firehoseSent, chunk.Length);
                            else { Interlocked.Increment(ref _trickleSent); await Task.Delay(200, _cts.Token); }
                        }
                    }
                    catch
                    {
                        if (fire) FirehoseClientLeft = true; else TrickleClientLeft = true;
                    }
                    return;
                }
                if (path == "/latin1")   // a charset other than UTF-8: "café" as ISO-8859-1 bytes
                {
                    byte[] latin = Encoding.Latin1.GetBytes("café-LATIN1");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=iso-8859-1\r\nContent-Length: {latin.Length}\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(latin);
                    await stream.FlushAsync();
                    return;
                }

                if (path.StartsWith("/slow/")) await Task.Delay(SlowHopMs, _cts.Token);
                var (status, location, body) = Route(path);
                var sb = new StringBuilder();
                sb.Append($"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 404 ? "Not Found" : "Found")}\r\n");
                sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
                if (location != null) sb.Append($"Location: {location}\r\n");
                byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                sb.Append($"Content-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
                byte[] headBytes = Encoding.ASCII.GetBytes(sb.ToString());
                await stream.WriteAsync(headBytes);
                await stream.WriteAsync(bodyBytes);
                await stream.FlushAsync();
            }
            catch { /* a client that hung up — nothing to answer */ }
        }
    }

    private (int Status, string Location, string Body) Route(string path)
    {
        if (path == "/same") return (302, "/final", "");
        if (path == "/final") return (200, null, "FINAL-SAME-HOST");
        if (path == "/thai") return (200, null, "สวัสดี-THAI");   // สวัสดี
        if (path == "/cross") return (302, $"http://localhost:{Port}/landing?d=secret", "");
        if (path.StartsWith("/landing")) return (200, null, "LANDED-OTHER-HOST");
        if (path.StartsWith("/loop/") && int.TryParse(path[6..], out int n)) return (302, $"/loop/{n + 1}", "");
        if (path == "/file") return (302, "file:///C:/Windows/win.ini", "");
        if (path.StartsWith("/slow/") && int.TryParse(path[6..], out int k))
            return k >= 4 ? (200, null, "SLOW-FINAL") : (302, $"/slow/{k + 1}", "");
        return (404, null, "nope");
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
