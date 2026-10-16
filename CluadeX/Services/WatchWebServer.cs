using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services;

/// <summary>
/// The Watch page as a web page for a phone on the same Wi-Fi (owner 2026-10-16: "ในบ้าน/วง Wi-Fi เดียวกัน").
/// Read-only: GET / (the page) and GET /api (the apps as JSON), both only with the secret key in the address
/// (?k=…, also in the QR code). Anything else, or a wrong key, gets 403/404 and nothing about the projects. The key
/// and the on/off switch live in DataRoot\watch-web.json; "new key" invalidates every address handed out before.
/// The page sends Referrer-Policy: no-referrer so the key never leaves in a Referer header.
/// </summary>
public sealed class WatchWebServer : IDisposable
{
    public const int DefaultPort = 8787;

    private readonly WatchService _watch;
    private readonly string _file;
    private readonly DebugLogService? _log;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public sealed class Config
    {
        public bool Enabled { get; set; }
        public int Port { get; set; } = DefaultPort;
        public string Key { get; set; } = "";
    }

    public Config Settings { get; private set; } = new();
    public bool IsRunning => _listener != null;
    public int Port { get; private set; }
    public string? LastError { get; private set; }

    public WatchWebServer(WatchService watch, SettingsService settings, DebugLogService? log = null, string? file = null)
    {
        _watch = watch;
        _log = log;
        _file = file ?? Path.Combine(settings.DataRoot, "watch-web.json");
        try { if (File.Exists(_file)) Settings = JsonSerializer.Deserialize<Config>(File.ReadAllText(_file)) ?? new(); } catch { Settings = new(); }
        if (Settings.Key.Length < 32) NewKey(save: false);
    }

    private void SaveConfig()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(_file)!); File.WriteAllText(_file, JsonSerializer.Serialize(Settings)); }
        catch (Exception ex) { _log?.Warn("WatchWeb", "could not save watch-web.json", ex); }
    }

    /// <summary>A new secret key: addresses (and QR codes) handed out before stop working.</summary>
    public void NewKey(bool save = true)
    {
        Settings.Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        if (save) SaveConfig();
    }

    /// <summary>The Wi-Fi / LAN addresses of this PC (private IPv4 first).</summary>
    public static List<string> LanAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address)) list.Add(ua.Address.ToString());
            }
        }
        catch { }
        static int Rank(string ip) => ip.StartsWith("192.168.") ? 0 : ip.StartsWith("10.") ? 1 : ip.StartsWith("172.") ? 2 : ip.StartsWith("169.254.") ? 9 : 5;
        return list.Distinct().OrderBy(Rank).ToList();
    }

    /// <summary>The address to open on the phone (first LAN address), or null when the PC has none.</summary>
    public string? PhoneUrl => LanAddresses().FirstOrDefault() is { } ip && IsRunning ? $"http://{ip}:{Port}/?k={Settings.Key}" : null;

    public bool Start()
    {
        if (IsRunning) return true;
        LastError = null;
        foreach (int port in new[] { Settings.Port, Settings.Port + 1, Settings.Port + 2 })
        {
            try
            {
                var l = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true };
                l.Start();
                _listener = l;
                Port = port;
                _cts = new CancellationTokenSource();
                _ = AcceptLoop(l, _cts.Token);
                Settings.Enabled = true;
                SaveConfig();
                _log?.Info("WatchWeb", $"watch page on port {port}");
                return true;
            }
            catch (SocketException ex) { LastError = ex.Message; }
        }
        return false;
    }

    public void Stop(bool remember = true)
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        if (remember) { Settings.Enabled = false; SaveConfig(); }
    }

    public void Dispose() => Stop(remember: false);

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => Serve(client), ct);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            string? requestLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            for (int i = 0; i < 100; i++) { var h = await reader.ReadLineAsync().ConfigureAwait(false); if (string.IsNullOrEmpty(h)) break; }
            var (status, type, body) = Handle(requestLine ?? "");
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            string head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\n"
                          + "X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nX-Frame-Options: DENY\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
            await stream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch { /* a dropped phone connection is not worth a log line */ }
    }

    /// <summary>Route one request line ("GET /api?k=… HTTP/1.1") — public so the harness can check it without sockets.</summary>
    public (string Status, string Type, string Body) Handle(string requestLine)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 2 || parts[0] != "GET") return ("405 Method Not Allowed", "text/plain", "GET only");
        string target = parts[1];
        int q = target.IndexOf('?');
        string path = q < 0 ? target : target[..q];
        string key = "";
        if (q >= 0)
            foreach (var kv in target[(q + 1)..].Split('&'))
                if (kv.StartsWith("k=")) key = Uri.UnescapeDataString(kv[2..]);
        if (path == "/favicon.ico") return ("404 Not Found", "text/plain", "");
        bool ok = key.Length == Settings.Key.Length
                  && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(key), Encoding.ASCII.GetBytes(Settings.Key));
        if (!ok) return ("403 Forbidden", "text/plain; charset=utf-8", "CluadeX: this address needs the key from the QR code.");
        return path switch
        {
            "/" => ("200 OK", "text/html; charset=utf-8", WatchWebPage.Html),
            "/api" => ("200 OK", "application/json; charset=utf-8", Json()),
            _ => ("404 Not Found", "text/plain", "not found"),
        };
    }

    /// <summary>The apps as the page shows them (no commands' secrets: commands are shown as typed by the owner).</summary>
    public string Json()
    {
        var apps = OnUi(() => _watch.Apps.Select(a => new
        {
            name = a.Name,
            project = a.ProjectPath,
            problem = a.HasProblem,
            test = new { state = a.TestState, summary = a.TestSummary, when = a.TestWhen, tail = Tail(a.TestTail, a.TestState == "failed" ? 1500 : 0) },
            run = new { state = a.RunState, errors = Tail(a.RunErrors, 1200) },
            web = new { url = a.Url, state = a.UrlState, detail = a.UrlDetail },
        }).ToList());
        return JsonSerializer.Serialize(new { machine = Environment.MachineName, at = DateTime.Now, apps });
    }

    private static string Tail(string s, int max) => max <= 0 || string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[^max..];

    private static T OnUi<T>(Func<T> f) =>
        System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess() ? d.Invoke(f) : f();
}
