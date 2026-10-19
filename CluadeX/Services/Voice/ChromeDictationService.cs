using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace CluadeX.Services.Voice;

/// <summary>
/// Speak instead of typing, through the browser's own free speech-to-text (owner 2026-10-11: "ระบบรับคำสั่งด้วยไมค์
/// แม่นๆ … ใช้ของ chrome google ได้ไหม ของฟรี"). WPF and WebView2 have no speech recognition, but Google Chrome's
/// webkitSpeechRecognition does — accurate for Thai, free, no key. So the mic button opens a small Chrome window
/// (--app, its own profile under the data folder) on a page this class serves, and the page streams what it hears
/// back here; the chat puts it in the input box, where the user reads it before sending. Microsoft Edge has the same
/// API (Microsoft's service) and is the fallback when Chrome is not installed.
///
/// The bridge is a TcpListener on 127.0.0.1 only (no firewall prompt, no urlacl), on a fixed port when it is free —
/// the browser remembers the microphone permission per origin, port included, so a stable port means one "Allow"
/// ever. Every request needs a loopback Host (DNS rebinding) and the per-run random token; anything that changes the
/// chat (/text, /send) must also come from the page's own origin, so another web page open in the user's browser
/// can neither type into the box nor press Send. One request per connection; /state is a long poll (network
/// callbacks are not throttled like timers in a window the chat covers).
///
/// Voice mode (Settings › Voice, off by default) rides the same bridge: /state also carries the reply to read aloud
/// (<see cref="Speak"/>) and, for a GPT-Live session, the messages for the page's data channel; /live/offer and
/// /live/event carry the WebRTC offer and the data channel's events back. Both need the token and the page's origin,
/// and /live/offer works once, only in a session opened for live voice — it is the one call that costs money.
/// </summary>
public sealed class ChromeDictationService : IDisposable
{
    public const int PortFrom = 47830;
    public const int PortTo = 47849;
    public const string PageTitle = "CluadeX Mic";
    private const int MaxHeadBytes = 8 * 1024;
    private const int MaxBodyBytes = 64 * 1024;
    private const int MaxTextChars = 8000;
    /// <summary>The page needs two or three at a time; anything flooding the port beyond this is dropped.</summary>
    private const int MaxConnections = 16;
    private static readonly TimeSpan HoldFor = TimeSpan.FromSeconds(15);
    /// <summary>Launched but never heard from: the browser did not open (blocked, crashed, wrong profile lock).</summary>
    public static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Heard from, then silent with no poll held open: the window is gone.</summary>
    public static TimeSpan SilenceTimeout { get; set; } = TimeSpan.FromSeconds(6);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Timer _watchdog;
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _session;
    private bool _active;
    private int _sent;
    private long _version;
    private bool _heard;
    private int _holding;
    private DateTime _startedUtc;
    private DateTime _lastSeenUtc;
    private int _disposed;
    // Speaking replies aloud: the latest reply to read (by id), and how many times the page was told to stop talking.
    private long _sayId;
    private string _sayText = "";
    private string _sayLang = "";
    private int _hush;
    // Live voice: this session runs the WebRTC page; its one offer; messages waiting for the page's data channel.
    private bool _live;
    private bool _offered;
    private long _outSeq;
    private readonly List<(long N, string Msg)> _out = new();
    private const int MaxOut = 64;

    public int Port { get; }
    /// <summary>Random per instance; the page carries it on every request.</summary>
    public string Token { get; }
    public string Origin => $"http://127.0.0.1:{Port}";
    public int Session { get { lock (_gate) return _session; } }
    public bool Active { get { lock (_gate) return _active; } }

    /// <summary>Words heard: (session, newly final text, the current unfinished guess). Raised on a pool thread, in
    /// the order the page spoke them (the page sends one at a time).</summary>
    public event Action<int, string, string>? TextReceived;
    /// <summary>The page's state: (session, "listening" | "paused" | "error", detail — the language, or the error code).</summary>
    public event Action<int, string, string>? PageEvent;
    /// <summary>The page's Send button / Enter: send what is in the box.</summary>
    public event Action<int>? SendRequested;
    /// <summary>The window went away (closed by the user, or silent too long), or never opened: (session, neverOpened).</summary>
    public event Action<int, bool>? Closed;
    /// <summary>Live voice: the page's WebRTC offer (session, SDP). CluadeX makes the paid call with its own key and
    /// returns the answer; the key never goes to the page. Called at most once per live session.</summary>
    public Func<int, string, CancellationToken, Task<LiveOfferResult>>? LiveOfferHandler { get; set; }
    /// <summary>Live voice: a server event the page read off its data channel (session, event), in order.</summary>
    public event Action<int, JsonElement>? LiveEvent;

    /// <summary>The Live API server events the page may pass on — nothing else is accepted.</summary>
    public static readonly HashSet<string> LiveEventTypes = new(StringComparer.Ordinal)
    {
        "session.started", "session.input_transcript.delta", "session.output_transcript.delta", "session.delegation.created",
        "session.usage.updated", "session.closed", "session.commentary.appended", "session.thinking.appended",
        "session.instructions.appended", "session.input_audio.muted", "session.input_audio.unmuted", "error",
    };

    private ChromeDictationService(TcpListener listener, int port)
    {
        _listener = listener;
        Port = port;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _ = AcceptLoop();
        _watchdog = new Timer(_ => Watch(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>Listen on the first free port of 47830-47849 (the same one every time, normally). Never throws.</summary>
    public static ChromeDictationService? TryStart(out string? error)
    {
        error = null;
        for (int port = PortFrom; port <= PortTo; port++)
        {
            if (Background.ReadinessProbe.IsListening(port)) continue;
            var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            try
            {
                listener.Start(16);
                return new ChromeDictationService(listener, port);
            }
            catch (SocketException ex)
            {
                try { listener.Stop(); } catch { }
                error = ex.Message;
            }
        }
        error ??= $"no free port in {PortFrom}-{PortTo}";
        return null;
    }

    /// <summary>A new listening session: the URL to open. A window of an earlier session closes itself.
    /// <paramref name="speak"/>: the page reads replies aloud (<see cref="Speak"/>). <paramref name="live"/>: the page
    /// runs a GPT-Live voice session over WebRTC instead of the browser's speech-to-text.</summary>
    public string BeginSession(string speechLang, string uiLang, string engine, bool speak = false, bool live = false)
    {
        int s;
        lock (_gate)
        {
            s = ++_session;
            _active = true;
            _heard = false;
            _startedUtc = DateTime.UtcNow;
            _live = live;
            _offered = false;
            _sayText = "";
            _sayLang = "";
            _out.Clear();
            BumpLocked();
        }
        return $"{Origin}/mic?t={Token}&s={s}&lang={Uri.EscapeDataString(speechLang)}&ui={Uri.EscapeDataString(uiLang)}&eng={Uri.EscapeDataString(engine)}"
            + (speak ? "&speak=1" : "") + (live ? "&mode=live" : "");
    }

    /// <summary>Read <paramref name="text"/> aloud in the page (the browser's own free speech synthesis). A newer
    /// reply replaces one not yet finished.</summary>
    public void Speak(string text, string lang)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_gate)
        {
            if (!_active) return;
            _sayId++;
            _sayText = text.Length > MaxTextChars ? text[..MaxTextChars] : text;
            _sayLang = lang;
            BumpLocked();
        }
    }

    /// <summary>Stop reading aloud (the user sent something new, or pressed Stop).</summary>
    public void Hush()
    {
        lock (_gate)
        {
            if (!_active) return;
            _hush++;
            _sayText = "";
            BumpLocked();
        }
    }

    /// <summary>Live voice: a JSON message for the page to send on its data channel, in order.</summary>
    public void SendToPage(string json)
    {
        lock (_gate)
        {
            if (!_active || !_live) return;
            _out.Add((++_outSeq, json));
            if (_out.Count > MaxOut) _out.RemoveAt(0);
            BumpLocked();
        }
    }

    public bool IsLive { get { lock (_gate) return _active && _live; } }

    /// <summary>Stop listening: the page sees it on its open poll and closes its window.</summary>
    public void EndSession()
    {
        lock (_gate)
        {
            if (!_active) return;
            _active = false;
            BumpLocked();
        }
    }

    /// <summary>The box was sent: the page clears the words it shows.</summary>
    public void NotifySent()
    {
        lock (_gate)
        {
            if (!_active) return;
            _sent++;
            BumpLocked();
        }
    }

    private void BumpLocked()
    {
        _version++;
        var old = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.TrySetResult();
    }

    private void Watch()
    {
        int session;
        bool neverOpened;
        lock (_gate)
        {
            if (!_active) return;
            var now = DateTime.UtcNow;
            if (!_heard && now - _startedUtc > ConnectTimeout) neverOpened = true;
            else if (_heard && _holding == 0 && now - _lastSeenUtc > SilenceTimeout) neverOpened = false;
            else return;
            _active = false;
            session = _session;
            BumpLocked();
        }
        Raise(() => Closed?.Invoke(session, neverOpened));
    }

    // ───────────────────────── HTTP ─────────────────────────

    private async Task AcceptLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { break; }
            bool slot;
            try { slot = _slots.Wait(0); } catch (ObjectDisposedException) { slot = false; }
            if (!slot) { try { client.Dispose(); } catch { } continue; }
            _ = Task.Run(() => ServeAsync(client, ct));
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                using var read = CancellationTokenSource.CreateLinkedTokenSource(stop);
                read.CancelAfter(TimeSpan.FromSeconds(10));
                var buffer = new byte[MaxHeadBytes + MaxBodyBytes];
                int filled = 0, headEnd;
                while ((headEnd = IndexOfHeadEnd(buffer, filled)) < 0)
                {
                    if (filled >= MaxHeadBytes) { await WriteAsync(stream, 431, "text/plain", "header too large", stop); return; }
                    int n = await stream.ReadAsync(buffer.AsMemory(filled, MaxHeadBytes - filled), read.Token).ConfigureAwait(false);
                    if (n == 0) return;
                    filled += n;
                }
                var lines = Encoding.Latin1.GetString(buffer, 0, headEnd).Split("\r\n");
                var first = lines[0].Split(' ');
                if (first.Length != 3) { await WriteAsync(stream, 400, "text/plain", "bad request", stop); return; }
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                int length = 0;
                if (headers.TryGetValue("Content-Length", out var cl) && (!int.TryParse(cl, out length) || length < 0 || length > MaxBodyBytes))
                {
                    await WriteAsync(stream, 413, "text/plain", "too large", stop);
                    return;
                }
                if (headers.ContainsKey("Transfer-Encoding")) { await WriteAsync(stream, 411, "text/plain", "length required", stop); return; }
                int bodyStart = headEnd + 4;
                while (filled - bodyStart < length)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(filled, bodyStart + length - filled), read.Token).ConfigureAwait(false);
                    if (n == 0) return;
                    filled += n;
                }
                string body = Encoding.UTF8.GetString(buffer, bodyStart, length);
                var (code, type, text) = await HandleAsync(first[0], first[1], headers, body, stream, stop).ConfigureAwait(false);
                await WriteAsync(stream, code, type, text, stop);
            }
            catch { /* the window went away, timed out, or this stopped */ }
            finally { try { _slots.Release(); } catch { } }
        }
    }

    private async Task<(int Code, string Type, string Body)> HandleAsync(string method, string target, Dictionary<string, string> headers,
        string body, NetworkStream stream, CancellationToken stop)
    {
        if (!IsLoopbackHost(headers.GetValueOrDefault("Host"))) return (403, "text/plain", "loopback only");
        int q = target.IndexOf('?');
        string path = q >= 0 ? target[..q] : target;
        var query = ParseQuery(q >= 0 ? target[(q + 1)..] : "");

        if (method == "GET" && path == "/mic")
            return TokenOk(query.GetValueOrDefault("t")) ? (200, "text/html; charset=utf-8", MicPage.Html) : (403, "text/plain", "forbidden");

        if (method == "GET" && path == "/state")
        {
            if (!TokenOk(query.GetValueOrDefault("t"))) return (403, "text/plain", "forbidden");
            int.TryParse(query.GetValueOrDefault("s"), out int s);
            long.TryParse(query.GetValueOrDefault("since"), out long since);
            // Live: the data-channel messages the page has sent so far — those are dropped from the queue.
            long acked = long.TryParse(query.GetValueOrDefault("out"), out long a) ? a : -1;
            Task wait;
            lock (_gate)
            {
                if (s != _session || !_active) return (200, "application/json", StateJsonLocked(false));
                _heard = true;
                _lastSeenUtc = DateTime.UtcNow;
                if (acked >= 0) _out.RemoveAll(m => m.N <= acked);
                if (since != _version) return (200, "application/json", StateJsonLocked(true));
                wait = _changed.Task;
                _holding++;
            }
            try
            {
                // A window that closes while its poll is held drops the socket: the read ends at once.
                var gone = stream.ReadAsync(new byte[1], stop).AsTask();
                await Task.WhenAny(wait, gone, Task.Delay(HoldFor, stop)).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) { _holding--; _lastSeenUtc = DateTime.UtcNow; }
            }
            lock (_gate) return (200, "application/json", StateJsonLocked(s == _session && _active));
        }

        if (method != "POST") return (405, "text/plain", "method not allowed");
        JsonElement json;
        try { json = JsonDocument.Parse(body).RootElement.Clone(); }
        catch { return (400, "text/plain", "bad json"); }
        if (json.ValueKind != JsonValueKind.Object || !TokenOk(Str(json, "t"))) return (403, "text/plain", "forbidden");
        int session = json.TryGetProperty("s", out var sv) && sv.TryGetInt32(out int si) ? si : -1;

        if (path == "/bye")
        {
            // sendBeacon from the closing window. A forged one could only end dictation, so the origin is not required.
            bool was;
            lock (_gate)
            {
                was = session == _session && _active;
                if (was) { _active = false; BumpLocked(); }
            }
            if (was) Raise(() => Closed?.Invoke(session, false));
            return (204, "text/plain", "");
        }

        // From here the request changes the chat: only the page itself may send it.
        if (!string.Equals(headers.GetValueOrDefault("Origin"), Origin, StringComparison.Ordinal))
            return (403, "text/plain", "wrong origin");
        lock (_gate)
        {
            if (session != _session || !_active) return (410, "text/plain", "this session has ended");
            _heard = true;
            _lastSeenUtc = DateTime.UtcNow;
        }
        switch (path)
        {
            case "/text":
                string fin = Clean(Str(json, "final")), interim = Clean(Str(json, "interim"));
                Raise(() => TextReceived?.Invoke(session, fin, interim));
                return (204, "text/plain", "");
            case "/event":
                string type = Clean(Str(json, "type"));
                if (type.Length > 40) type = type[..40];
                string detail = Clean(Str(json, "detail"));
                if (detail.Length > 200) detail = detail[..200];
                Raise(() => PageEvent?.Invoke(session, type, detail));
                return (204, "text/plain", "");
            case "/send":
                Raise(() => SendRequested?.Invoke(session));
                return (204, "text/plain", "");
            case "/live/offer":
            {
                // The one paid call of a live session. Only a session opened for live voice may make it, and only
                // once — a page (or anything posing as it) cannot start a second billed session.
                lock (_gate)
                {
                    if (!_live) return (403, "text/plain", "not a live voice session");
                    if (_offered) return (409, "text/plain", "this live session was already started");
                    _offered = true;
                }
                var handler = LiveOfferHandler;
                if (handler == null) return (503, "application/json", JsonSerializer.Serialize(new { error = "live voice is not available" }));
                LiveOfferResult result;
                try { result = await handler(session, Str(json, "sdp"), stop).ConfigureAwait(false); }
                catch (Exception ex) { result = new LiveOfferResult(null, null, ex.Message); }
                return result.AnswerSdp != null
                    ? (200, "application/json", JsonSerializer.Serialize(new { sdp = result.AnswerSdp }))
                    : (502, "application/json", JsonSerializer.Serialize(new { error = result.Error ?? "the live session could not start" }));
            }
            case "/live/event":
            {
                lock (_gate) { if (!_live) return (403, "text/plain", "not a live voice session"); }
                if (!json.TryGetProperty("ev", out var ev) || ev.ValueKind != JsonValueKind.Object
                    || !LiveEventTypes.Contains(Str(ev, "type")))
                    return (400, "text/plain", "unknown event");
                Raise(() => LiveEvent?.Invoke(session, ev));
                return (204, "text/plain", "");
            }
            default:
                return (404, "text/plain", "not found");
        }
    }

    private string StateJsonLocked(bool active) => active
        ? JsonSerializer.Serialize(new
        {
            active, sent = _sent, v = _version,
            say = new { id = _sayId, text = _sayText, lang = _sayLang },
            hush = _hush,
            @out = _out.Select(m => new { n = m.N, m = m.Msg }).ToArray(),
        })
        : JsonSerializer.Serialize(new { active, sent = _sent, v = _version });

    private bool TokenOk(string? candidate) =>
        candidate != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(Token));

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Heard words as plain text: control characters become spaces, the length is bounded.</summary>
    internal static string Clean(string s)
    {
        if (s.Length > MaxTextChars) s = s[..MaxTextChars];
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s) sb.Append(char.IsControl(ch) ? ' ' : ch);
        return sb.ToString().Trim();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string k = eq >= 0 ? pair[..eq] : pair, v = eq >= 0 ? pair[(eq + 1)..] : "";
            try { d[Uri.UnescapeDataString(k)] = Uri.UnescapeDataString(v.Replace('+', ' ')); } catch { }
        }
        return d;
    }

    private static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        string h = host.Trim().ToLowerInvariant();
        int colon = h.LastIndexOf(':');
        if (colon >= 0 && !h.EndsWith(']')) h = h[..colon];
        return h is "127.0.0.1" or "localhost";
    }

    private static int IndexOfHeadEnd(byte[] b, int length)
    {
        for (int i = 3; i < length; i++)
            if (b[i] == '\n' && b[i - 1] == '\r' && b[i - 2] == '\n' && b[i - 3] == '\r') return i - 3;
        return -1;
    }

    private static async Task WriteAsync(NetworkStream stream, int code, string type, string text, CancellationToken stop)
    {
        var body = Encoding.UTF8.GetBytes(text);
        string reason = code switch
        {
            200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found",
            405 => "Method Not Allowed", 409 => "Conflict", 410 => "Gone", 411 => "Length Required", 413 => "Payload Too Large",
            502 => "Bad Gateway", 503 => "Service Unavailable", _ => "Error",
        };
        string head =
            $"HTTP/1.1 {code} {reason}\r\n" +
            (code == 204 ? "" : $"Content-Type: {type}\r\nContent-Length: {body.Length}\r\n") +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            (type.StartsWith("text/html") ? "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'\r\n" : "") +
            "Connection: close\r\n" +
            "Server: CluadeX\r\n\r\n";
        using var write = CancellationTokenSource.CreateLinkedTokenSource(stop);
        write.CancelAfter(TimeSpan.FromSeconds(10));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), write.Token).ConfigureAwait(false);
        if (code != 204 && body.Length > 0) await stream.WriteAsync(body, write.Token).ConfigureAwait(false);
    }

    private static void Raise(Action a)
    {
        try { a(); } catch { /* a listener's failure must not break the bridge */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _watchdog.Dispose(); } catch { }
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        lock (_gate) { _active = false; BumpLocked(); }
        try { _cts.Dispose(); } catch { }
        try { _slots.Dispose(); } catch { }
    }

    // ───────────────────────── the browser ─────────────────────────

    /// <summary>A browser with speech recognition: Google Chrome first (the owner's choice), else Microsoft Edge.</summary>
    public sealed record Browser(string Path, string Name, string Engine);

    public static Browser? FindBrowser()
    {
        string? chrome = FromAppPaths("chrome.exe") ?? FirstExisting(
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Google\Chrome\Application\chrome.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"),
            Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Google\Chrome\Application\chrome.exe"));
        if (chrome != null) return new Browser(chrome, "Google Chrome", "google");
        string? edge = FromAppPaths("msedge.exe") ?? FirstExisting(
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"));
        return edge != null ? new Browser(edge, "Microsoft Edge", "microsoft") : null;
    }

    private static string? FromAppPaths(string exe)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (key?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
            }
            catch { }
        }
        return null;
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    /// <summary>
    /// Open the mic page as a small app window with its own profile (the user's tabs, extensions and sign-ins stay
    /// out of it; the microphone permission is remembered there). Arguments go through ArgumentList — nothing is
    /// parsed by a shell. The window is then kept above other windows, so a maximized chat never hides the mic.
    /// </summary>
    public static bool Launch(Browser browser, string url, string profileDir, int? left, int? top, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(profileDir);
            var psi = new ProcessStartInfo(browser.Path) { UseShellExecute = false };
            psi.ArgumentList.Add("--app=" + url);
            psi.ArgumentList.Add("--user-data-dir=" + profileDir);
            psi.ArgumentList.Add("--no-first-run");
            psi.ArgumentList.Add("--no-default-browser-check");
            // Reading replies aloud starts from CluadeX, not from a click in this window: Chrome would refuse
            // speechSynthesis.speak() without one. Only this window's own profile runs with the flag.
            psi.ArgumentList.Add("--autoplay-policy=no-user-gesture-required");
            psi.ArgumentList.Add("--window-size=440,400");
            if (left is int x && top is int y) psi.ArgumentList.Add($"--window-position={x},{y}");
            using var p = Process.Start(psi);
            if (p == null) { error = "the browser did not start"; return false; }
            _ = Task.Run(KeepOnTopAsync);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static async Task KeepOnTopAsync()
    {
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(250).ConfigureAwait(false);
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, sb.Capacity);
                if (!sb.ToString().StartsWith(PageTitle, StringComparison.Ordinal)) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero) continue;
            SetWindowPos(found, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);
            return;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
