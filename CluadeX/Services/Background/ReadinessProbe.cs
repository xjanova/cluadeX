using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Background;

/// <summary>
/// "Is the dev server up yet, and where?" — answered the way a person would check, because no single signal is
/// reliable on its own:
/// <list type="bullet">
/// <item>a TCP connect to BOTH 127.0.0.1 and [::1] (vite on Node 17+ listens on ::1 only, python's http.server on
///   a dual-stack [::]);</item>
/// <item>a localhost URL the server printed, read after ANSI colour codes are stripped — it wins over the port that
///   was asked for, because vite moves to 5174 when 5173 is taken;</item>
/// <item>an optional ready_pattern regex on the output.</item>
/// </list>
/// Also: which process already holds a port (so a start can fail fast naming it), and output clean-up shared by
/// every reader (ANSI codes, carriage-return progress bars).
/// </summary>
public static class ReadinessProbe
{
    // ───────────────────────── output text ─────────────────────────

    // CSI (colours, cursor moves), OSC (titles, hyperlinks) and the two-byte escapes.
    private static readonly Regex AnsiRx = new(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ControlRx = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.Compiled);

    /// <summary>One output line as a person would read it: ANSI codes and stray control characters removed, and a
    /// line a progress bar redrew with '\r' reduced to what is left on screen.</summary>
    public static string CleanLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return "";
        string s = line.TrimEnd('\r');
        if (s.IndexOf('\x1B') >= 0) s = AnsiRx.Replace(s, "");
        int cr = s.LastIndexOf('\r');
        if (cr >= 0)
        {
            // "50%\r100%" shows "100%"; a redraw that ended in an empty write keeps the last visible text.
            string tail = s[(cr + 1)..];
            s = tail.Trim().Length > 0 ? tail : s[..cr].Split('\r').LastOrDefault(p => p.Trim().Length > 0) ?? "";
        }
        return ControlRx.Replace(s, "");
    }

    /// <summary>Remove ANSI escape sequences from a whole text (lines kept).</summary>
    public static string StripAnsi(string text) =>
        string.IsNullOrEmpty(text) || text.IndexOf('\x1B') < 0 ? text ?? "" : AnsiRx.Replace(text, "");

    // http://localhost:5173/  http://127.0.0.1:8000  http://[::1]:3000/app  http://0.0.0.0:8080  http://[::]:8000/
    private static readonly Regex LocalUrlRx = new(
        @"\bhttps?://(?<host>localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1?\])(?::(?<port>\d{1,5}))?(?<path>/[^\s""'<>`)\]]*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The first localhost URL in <paramref name="text"/>, normalised so a browser can open it: a wildcard bind
    /// (0.0.0.0, [::]) becomes 127.0.0.1; trailing punctuation is dropped. Null when there is none.
    /// </summary>
    public static (string Url, int Port)? FindLocalUrl(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = LocalUrlRx.Match(StripAnsi(text));
        if (!m.Success) return null;
        string scheme = m.Value.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
        string host = m.Groups["host"].Value.ToLowerInvariant();
        if (host is "0.0.0.0" or "[::]") host = "127.0.0.1";
        int port = m.Groups["port"].Success && int.TryParse(m.Groups["port"].Value, out var p) ? p : (scheme == "https" ? 443 : 80);
        if (port is < 1 or > 65535) return null;
        string path = m.Groups["path"].Success ? m.Groups["path"].Value.TrimEnd('.', ',', ';', ':', '!', '?') : "/";
        if (path.Length == 0) path = "/";
        return ($"{scheme}://{host}:{port}{path}", port);
    }

    // ───────────────────────── ports ─────────────────────────

    /// <summary>Every local endpoint listening on TCP <paramref name="port"/> (any address, IPv4 and IPv6).</summary>
    public static IReadOnlyList<IPEndPoint> ListenersOn(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Where(ep => ep.Port == port).ToList();
        }
        catch { return Array.Empty<IPEndPoint>(); }
    }

    /// <summary>Something already listens on <paramref name="port"/>.</summary>
    public static bool IsListening(int port) => ListenersOn(port).Count > 0;

    /// <summary>The process listening on <paramref name="port"/> (IPv4 or IPv6), with its name when it can be read.
    /// Null when nothing listens or the table could not be read.</summary>
    public static (int Pid, string? Name)? FindListeningOwner(int port)
    {
        foreach (int family in new[] { AF_INET, AF_INET6 })
        {
            int? pid = OwnerFromTable(port, family);
            if (pid is not { } id) continue;
            string? name = null;
            try { using var p = Process.GetProcessById(id); name = p.ProcessName; } catch { }
            return (id, name);
        }
        return null;
    }

    /// <summary>The loopback address that accepted a TCP connection on <paramref name="port"/> (127.0.0.1 is tried
    /// alongside [::1]), or null when neither did within <paramref name="timeoutMs"/>.</summary>
    public static async Task<IPAddress?> TryConnectAsync(int port, int timeoutMs, CancellationToken ct)
    {
        if (port is < 1 or > 65535) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(Math.Max(50, timeoutMs));
        var v4 = ConnectOne(IPAddress.Loopback, port, linked.Token);
        var v6 = ConnectOne(IPAddress.IPv6Loopback, port, linked.Token);
        var results = await Task.WhenAll(v4, v6).ConfigureAwait(false);
        return results[0] ? IPAddress.Loopback : results[1] ? IPAddress.IPv6Loopback : null;
    }

    private static async Task<bool> ConnectOne(IPAddress address, int port, CancellationToken ct)
    {
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
            return socket.Connected;
        }
        catch { return false; }
    }

    /// <summary>The HTTP status of a HEAD request to <paramref name="url"/> (no proxy), or null when it does not
    /// answer HTTP within <paramref name="timeoutMs"/>. Only for reporting — readiness never depends on it.</summary>
    public static async Task<int?> TryHttpHeadAsync(string url, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(Math.Max(100, timeoutMs)) };
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            return (int)response.StatusCode;
        }
        catch { return null; }
    }

    /// <summary>The first port in [<paramref name="from"/>, <paramref name="to"/>] nothing listens on, or null.</summary>
    public static int? FirstFreePort(int from, int to)
    {
        HashSet<int> busy;
        try { busy = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet(); }
        catch { busy = new(); }
        for (int port = from; port <= to; port++)
            if (!busy.Contains(port)) return port;
        return null;
    }

    // ───────────────────────── GetExtendedTcpTable ─────────────────────────

    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    private static int? OwnerFromTable(int port, int family)
    {
        if (!OperatingSystem.IsWindows()) return null;
        int size = 0;
        try
        {
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (size <= 0) return null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    uint rc = GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
                    if (rc == 122 /* ERROR_INSUFFICIENT_BUFFER */) continue;
                    if (rc != 0) return null;
                    int count = Marshal.ReadInt32(buffer);
                    // MIB_TCPROW_OWNER_PID: 6 DWORDs (port at +8, pid at +20).
                    // MIB_TCP6ROW_OWNER_PID: 16-byte addr, scope, port (+20), 16-byte addr, scope, port, state, pid (+52).
                    int rowSize = family == AF_INET ? 24 : 56;
                    int portOffset = family == AF_INET ? 8 : 20;
                    int pidOffset = family == AF_INET ? 20 : 52;
                    for (int i = 0; i < count; i++)
                    {
                        int row = 4 + i * rowSize;
                        uint raw = (uint)Marshal.ReadInt32(buffer, row + portOffset);
                        int rowPort = (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
                        if (rowPort == port) return Marshal.ReadInt32(buffer, row + pidOffset);
                    }
                    return null;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        catch { }
        return null;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    // ───────────────────────── the wait ─────────────────────────

    /// <summary>What a readiness wait watches. Every member must be cheap and must not throw.</summary>
    public sealed class Target
    {
        /// <summary>The process (and everything it started) is gone.</summary>
        public required Func<bool> HasExited { get; init; }
        /// <summary>The localhost URL the process printed so far, or null.</summary>
        public required Func<string?> PrintedUrl { get; init; }
        /// <summary>The ready_pattern matched a line, when one was given.</summary>
        public Func<bool>? PatternMatched { get; init; }
        /// <summary>Port to probe with TCP connects (null = none known, or one that was busy before the start).</summary>
        public int? ProbePort { get; init; }
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
        /// <summary>Poll interval.</summary>
        public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(250);
    }

    public enum Outcome { Ready, Exited, TimedOut }

    /// <summary>The result of <see cref="WaitAsync"/>: how it ended, what signal said "ready", and the URL to open.</summary>
    public sealed record Result(Outcome Outcome, string? Signal, string? Url, TimeSpan Elapsed);

    /// <summary>
    /// Wait until the target is ready (a printed URL, the pattern, or a TCP connect on the probe port), has exited,
    /// or the timeout passes. Throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> fires.
    /// </summary>
    public static async Task<Result> WaitAsync(Target target, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int connectTimeout = (int)Math.Clamp(target.Interval.TotalMilliseconds, 100, 400);
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Gone is gone, whatever it printed on the way out ("listening on :3000", then a crash).
            if (target.HasExited())
                return new Result(Outcome.Exited, null, null, sw.Elapsed);
            if (target.PrintedUrl() is { } printed)
                return new Result(Outcome.Ready, "url", printed, sw.Elapsed);
            if (target.PatternMatched?.Invoke() == true)
                return new Result(Outcome.Ready, "pattern", null, sw.Elapsed);
            if (target.ProbePort is { } port && await TryConnectAsync(port, connectTimeout, ct).ConfigureAwait(false) is { } address)
            {
                string host = address.Equals(IPAddress.IPv6Loopback) ? "[::1]" : "127.0.0.1";
                return new Result(Outcome.Ready, "port", $"http://{host}:{port}/", sw.Elapsed);
            }
            if (sw.Elapsed >= target.Timeout)
                return new Result(Outcome.TimedOut, null, null, sw.Elapsed);
            await Task.Delay(target.Interval, ct).ConfigureAwait(false);
        }
    }
}
