using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Account;

public enum DoctorStatus { Ok, Info, Warn, Fail, Skip }

/// <summary>One check: its id (network, proxy, dns, tcp, tls, api, clock, updates), the verdict, the reason in the UI's
/// language (null when there is nothing to say) and an English technical detail for the team.</summary>
public sealed record DoctorStep(string Id, DoctorStatus Status, string? Reason, string Detail);

/// <summary>A finished run: the steps, the reason that matters most, and the report text (secrets already masked).</summary>
public sealed record DoctorReport(string Id, DateTimeOffset Time, IReadOnlyList<DoctorStep> Steps, string? Cause, string Text)
{
    /// <summary>Nothing failed or warned.</summary>
    public bool AllGood => Steps.All(s => s.Status is DoctorStatus.Ok or DoctorStatus.Info);
    /// <summary>Where <see cref="ConnectionDoctor.Save"/> wrote it, once saved.</summary>
    public string? SavedPath { get; init; }
}

/// <summary>
/// "ตรวจการเชื่อมต่อ" (installer contract 2.4): every failure path in the setup wizard and the update screens offers it.
/// network → proxy → dns → tcp → tls → api → clock → updates, each Ok/Warn/Fail with a Thai reason and an English detail;
/// it stops at the first hard failure among network/dns/tcp/tls (the rest "ข้าม"), and still checks the clock when the
/// API answered. The report never holds a license key (masked ABCD…WXYZ), a password, a device code, a token or the full
/// machine id. Saved to &lt;data&gt;\logs\connection-report-&lt;id&gt;.txt; "ส่งให้ทีมงาน" goes through <see cref="BugReportService"/>.
/// </summary>
public sealed class ConnectionDoctor
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(5);

    private readonly SettingsService _settings;
    private readonly XmanAccountService _account;
    private readonly LocalizationService _loc;
    private readonly BugReportService? _bugs;
    private readonly DebugLogService? _log;

    /// <summary>The updates step's HTTP handler (a harness swaps it).</summary>
    public Func<HttpMessageHandler> UpdatesHandler { get; set; } = () => new HttpClientHandler();
    /// <summary>The update feed the updates step asks — the one the updater uses, on xman4289.com (a harness points it
    /// elsewhere).</summary>
    public string UpdatesUrl { get; set; } = AutoUpdateService.FeedUrl;

    public ConnectionDoctor(SettingsService settings, XmanAccountService account, LocalizationService loc,
        BugReportService? bugs = null, DebugLogService? log = null)
    {
        _settings = settings;
        _account = account;
        _loc = loc;
        _bugs = bugs;
        _log = log;
    }

    private string L(string key) => _loc.T(key);
    private string LF(string key, params object[] args) => _loc.T(key, args);

    /// <summary>Run every check. <paramref name="lastError"/>: the failure the user is looking at (else the account
    /// service's last one). Never throws but for cancellation.</summary>
    public async Task<DoctorReport> RunAsync(AccountCallError? lastError = null, CancellationToken ct = default)
    {
        var steps = new List<DoctorStep>();
        var api = new Uri(_account.BaseUrl);
        string host = api.Host;
        int port = api.Port;
        bool hardFail = false;
        void Add(DoctorStep s) { steps.Add(s); if (s.Status == DoctorStatus.Fail && s.Id is "network" or "dns" or "tcp" or "tls") hardFail = true; }
        DoctorStep Skipped(string id) => new(id, DoctorStatus.Skip, null, "");

        // network
        bool net;
        try { net = NetworkInterface.GetIsNetworkAvailable(); } catch { net = true; }
        Add(net ? new DoctorStep("network", DoctorStatus.Ok, null, "")
                : new DoctorStep("network", DoctorStatus.Fail, L("acct.doc.network.fail"), "no network interface is up"));

        // proxy (information only)
        string proxyLine = "none";
        bool viaProxy = false;
        try
        {
            var p = HttpClient.DefaultProxy.GetProxy(api);
            if (p != null && p != api && !HttpClient.DefaultProxy.IsBypassed(api))
            {
                viaProxy = true;
                proxyLine = $"{p.Host}:{p.Port}";
            }
        }
        catch { /* no proxy information */ }
        Add(viaProxy ? new DoctorStep("proxy", DoctorStatus.Info, LF("acct.doc.proxy.used", proxyLine), proxyLine)
                     : new DoctorStep("proxy", DoctorStatus.Ok, null, "none"));

        // dns
        IPAddress? address = null;
        if (hardFail) Add(Skipped("dns"));
        else if (IPAddress.TryParse(host, out var literal)) { address = literal; Add(new DoctorStep("dns", DoctorStatus.Ok, null, host)); }
        else
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(StepTimeout);
                var all = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
                address = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault();
                Add(address != null
                    ? new DoctorStep("dns", DoctorStatus.Ok, null, $"{host} -> {address} ({sw.ElapsedMilliseconds} ms)")
                    : new DoctorStep("dns", DoctorStatus.Fail, LF("acct.doc.dns.fail", host), $"{host}: no address"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Add(new DoctorStep("dns", DoctorStatus.Fail, LF("acct.doc.dns.fail", host),
                    ex is OperationCanceledException ? $"{host}: no answer in {StepTimeout.TotalSeconds:0} s" : $"{host}: {Tech(ex)}"));
            }
        }

        // tcp + tls (direct; through a proxy they say nothing about the real path, so they are left to the api step)
        if (hardFail || address == null) { Add(Skipped("tcp")); Add(Skipped("tls")); }
        else if (viaProxy)
        {
            Add(new DoctorStep("tcp", DoctorStatus.Skip, L("acct.doc.viaProxy"), "via proxy"));
            Add(new DoctorStep("tls", DoctorStatus.Skip, L("acct.doc.viaProxy"), "via proxy"));
        }
        else
        {
            using var tcp = new TcpClient();
            var sw = Stopwatch.StartNew();
            bool connected = false;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(StepTimeout);
                await tcp.ConnectAsync(address, port, cts.Token).ConfigureAwait(false);
                connected = true;
                Add(new DoctorStep("tcp", DoctorStatus.Ok, null, $"{address}:{port} ({sw.ElapsedMilliseconds} ms)"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Add(new DoctorStep("tcp", DoctorStatus.Fail, LF("acct.doc.tcp.fail", port),
                    ex is OperationCanceledException ? $"{address}:{port}: no answer in {StepTimeout.TotalSeconds:0} s" : $"{address}:{port}: {Tech(ex)}"));
            }

            if (!connected) Add(Skipped("tls"));
            else if (api.Scheme != Uri.UriSchemeHttps) Add(new DoctorStep("tls", DoctorStatus.Skip, null, $"{api.Scheme}, not https"));
            else Add(await TlsAsync(tcp, host, ct).ConfigureAwait(false));
        }

        // api
        AppAuthHealth? health = null;
        if (hardFail) Add(Skipped("api"));
        else
        {
            var sw = Stopwatch.StartNew();
            health = await _account.HealthAsync(ct).ConfigureAwait(false);
            var (status, key) = ClassifyApi(health);
            string detail = $"GET app-auth/health -> {(health.HttpStatus is int h ? "HTTP " + h.ToString(CultureInfo.InvariantCulture) : "no answer")}"
                            + (health.ErrorCode != null ? $" {health.ErrorCode}" : "") + $" ({sw.ElapsedMilliseconds} ms)"
                            + (health.TechDetail != null ? $"; {health.TechDetail}" : "");
            Add(new DoctorStep("api", status, key != null ? L(key) : null, detail));
        }

        // clock — whenever the server answered with a time
        if (health?.ServerTimeUtc is { } serverTime)
        {
            double minutes = ClockSkewMinutes(serverTime, DateTime.UtcNow);
            Add(Math.Abs(minutes) > 5
                ? new DoctorStep("clock", DoctorStatus.Warn, LF("acct.doc.clock.off", Math.Round(Math.Abs(minutes)).ToString(CultureInfo.InvariantCulture)),
                    $"server {serverTime:u}, local {DateTime.UtcNow:u}, skew {minutes:0.#} min")
                : new DoctorStep("clock", DoctorStatus.Ok, null, $"skew {minutes:0.#} min"));
        }
        else Add(Skipped("clock"));

        // updates
        if (hardFail) Add(Skipped("updates"));
        else Add(await UpdatesAsync(ct).ConfigureAwait(false));

        var id = NewReportId(DateTime.Now, Random.Shared);
        var time = DateTimeOffset.Now;
        string? cause = steps.FirstOrDefault(s => s.Status == DoctorStatus.Fail)?.Reason
                        ?? steps.FirstOrDefault(s => s.Status == DoctorStatus.Warn)?.Reason;
        var err = lastError ?? _account.LastError;
        string text = BuildText(id, time, steps, proxyLine, err, cause, _loc.T("acct.doc.cause"));
        var report = new DoctorReport(id, time, steps, cause, text);
        _log?.Info("Doctor", $"{id}: " + string.Join(" ", steps.Select(s => $"{s.Id}={s.Status}")));
        try { return report with { SavedPath = Save(report) }; }
        catch (Exception ex) { _log?.Warn("Doctor", $"report not saved: {ex.Message}"); return report; }
    }

    private async Task<DoctorStep> TlsAsync(TcpClient tcp, string host, CancellationToken ct)
    {
        SslPolicyErrors errors = SslPolicyErrors.None;
        string? issuer = null;
        try
        {
            using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, e) =>
            {
                errors = e;
                issuer = cert?.Issuer;
                return true;   // inspected, never used to send anything
            });
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(StepTimeout);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token).ConfigureAwait(false);
            var (key, interceptor) = ClassifyTls(errors, issuer);
            string detail = errors == SslPolicyErrors.None ? $"{ssl.SslProtocol}; issuer \"{issuer}\"" : $"{errors}; issuer \"{issuer}\"";
            if (key == null) return new DoctorStep("tls", DoctorStatus.Ok, null, detail);
            string reason = interceptor != null ? LF("acct.doc.tls.interceptor", interceptor) : L(key);
            return new DoctorStep("tls", DoctorStatus.Fail, reason, detail);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new DoctorStep("tls", DoctorStatus.Fail, L("acct.doc.tls.fail"),
                ex is OperationCanceledException ? $"handshake: no answer in {StepTimeout.TotalSeconds:0} s" : $"handshake: {Tech(ex)}");
        }
    }

    private async Task<DoctorStep> UpdatesAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient(UpdatesHandler(), true) { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"CluadeX/{AutoUpdateService.CurrentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            using var resp = await http.GetAsync(UpdatesUrl, ct).ConfigureAwait(false);
            int code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                string? latest = AutoUpdateService.LatestFeedVersion(body);
                // a 200 that is not the feed (a captive portal, a proxy's page) is not a working update path
                if (latest == null) return new DoctorStep("updates", DoctorStatus.Warn, L("acct.doc.updates.fail"), $"HTTP {code}; not an update feed");
                return new DoctorStep("updates", DoctorStatus.Ok, null, $"HTTP {code}; latest v{latest}");
            }
            return new DoctorStep("updates", code is 403 or 429 ? DoctorStatus.Warn : DoctorStatus.Fail, L("acct.doc.updates.fail"), $"HTTP {code}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new DoctorStep("updates", DoctorStatus.Fail, L("acct.doc.updates.fail"), Tech(ex)); }
    }

    // ─── saving and sending ───────────────────────────────────────────────────────────────

    /// <summary>Write the report to &lt;data&gt;\logs\connection-report-&lt;id&gt;.txt. Returns the path.</summary>
    public string Save(DoctorReport report)
    {
        string dir = Path.Combine(_settings.DataRoot, "logs");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"connection-report-{report.Id}.txt");
        File.WriteAllText(path, report.Text, new UTF8Encoding(false));
        return path;
    }

    /// <summary>"ส่งให้ทีมงาน": the report goes to the team's bug inbox. The message is the UI's own sentence, never the
    /// transport's exception text.</summary>
    public async Task<(bool Sent, string Message)> SendToTeamAsync(DoctorReport report, CancellationToken ct = default)
    {
        if (_bugs == null) return (false, L("acct.doc.send.fail"));
        // the report text never holds a key or a password (see the class summary); the scrubber runs on it again anyway
        var result = await _bugs.SubmitTextAsync("connection", $"Connection report {report.Id}\n\n{report.Text}",
            new Dictionary<string, object?> { ["kind"] = "connection-report", ["report_id"] = report.Id, ["version"] = AutoUpdateService.CurrentVersion },
            ct).ConfigureAwait(false);
        bool ok = result.Outcome == BugReportOutcome.Sent;
        _log?.Info("Doctor", $"{report.Id} sent to the team: {result.Outcome} {result.Ticket ?? result.ErrorCode}");
        if (result.Outcome == BugReportOutcome.Queued) return (false, L("bug.result.queued"));
        return ok ? (true, LF("acct.doc.send.ok", report.Id)) : (false, L("acct.doc.send.fail"));
    }

    // ─── pure rules (public for the harness) ────────────────────────────────────────────

    /// <summary>The api step's verdict and reason key from health (contract 2.4's table).</summary>
    public static (DoctorStatus Status, string? ReasonKey) ClassifyApi(AppAuthHealth h) => h.Availability switch
    {
        AppAuthAvailability.Ready => (DoctorStatus.Ok, null),
        AppAuthAvailability.NotDeployed => (DoctorStatus.Warn, "acct.doc.api.notDeployed"),
        _ => h.ErrorCode switch
        {
            "SERVER_ERROR" => (DoctorStatus.Fail, "acct.doc.api.serverError"),
            "RATE_LIMIT_EXCEEDED" or "TOO_MANY_ATTEMPTS" => (DoctorStatus.Warn, "acct.doc.api.rateLimit"),
            "NOT_JSON" => (DoctorStatus.Fail, "acct.doc.api.notJson"),
            "PRODUCT_NOT_FOUND" => (DoctorStatus.Fail, "acct.err.productNotFound"),
            _ => (DoctorStatus.Fail, "acct.doc.api.offline"),
        },
    };

    /// <summary>Programs known to put their own certificate in front of every HTTPS site.</summary>
    private static readonly string[] Interceptors =
    {
        "Kaspersky", "ESET", "Avast", "AVG", "Bitdefender", "Fortinet", "FortiGate", "Zscaler", "Sophos", "Norton", "McAfee",
        "Cisco Umbrella", "Netskope", "Palo Alto", "Trend Micro", "Check Point", "WatchGuard", "Fiddler", "Charles Proxy", "mitmproxy",
    };

    /// <summary>TLS verdict: null key = fine. The interceptor's name when the issuer gives it away.</summary>
    public static (string? ReasonKey, string? Interceptor) ClassifyTls(SslPolicyErrors errors, string? issuer)
    {
        if (errors == SslPolicyErrors.None) return (null, null);
        string? who = issuer == null ? null : Interceptors.FirstOrDefault(n => issuer.Contains(n, StringComparison.OrdinalIgnoreCase));
        return ("acct.doc.tls.fail", who);
    }

    /// <summary>Server minus local, in minutes.</summary>
    public static double ClockSkewMinutes(DateTime serverUtc, DateTime localUtc) => (serverUtc - localUtc).TotalMinutes;

    /// <summary>"CX-20261108-7F3A": the date and 4 hex digits.</summary>
    public static string NewReportId(DateTime local, Random rng)
        => $"CX-{local.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{rng.Next(0, 0x10000):X4}";

    /// <summary>ABCD-EFGH-IJKL-WXYZ → ABCD…WXYZ (contract 2.4).</summary>
    public static string MaskKey(string key)
    {
        string k = key.Trim();
        string compact = k.Replace("-", "");
        if (compact.Length <= 8) return new string('•', Math.Max(4, compact.Length));
        return compact[..4] + "…" + compact[^4..];
    }

    private static readonly Regex KeyLike = new(@"\b[A-Z0-9]{3,}(?:-[A-Z0-9]{3,}){2,}\b", RegexOptions.Compiled);
    private static readonly Regex LongHex = new(@"\b[0-9a-fA-F]{32,}\b", RegexOptions.Compiled);
    private static readonly Regex SecretField = new(@"(?i)(password|device_code|token|secret|authorization)(\""?\s*[:=]\s*\""?)[^\s\"",;&]+", RegexOptions.Compiled);

    /// <summary>Mask anything that looks like a license key, a device code / machine id (long hex) or a secret field.</summary>
    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = SecretField.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + "•••");
        text = LongHex.Replace(text, m => m.Value[..8] + "…");
        text = KeyLike.Replace(text, m => MaskKey(m.Value));
        return text;
    }

    /// <summary>The report text (contract 2.4's layout), masked.</summary>
    public static string BuildText(string id, DateTimeOffset time, IReadOnlyList<DoctorStep> steps, string proxy,
        AccountCallError? lastError, string? cause, string causeLabel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CluadeX connection report");
        sb.AppendLine($"Report ID: {id}");
        sb.AppendLine($"Time: {time.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"App: CluadeX {AutoUpdateService.CurrentVersion} ({AutoUpdateService.CurrentBuildKind.ToString().ToLowerInvariant()})   OS: {OsLine()}");
        sb.AppendLine($"Device: {XmanLicenseService.GetMachineFingerprint()[..8]}");
        sb.AppendLine($"Proxy: {proxy}");
        foreach (var s in steps)
        {
            string tag = s.Status switch
            {
                DoctorStatus.Ok => "[OK]  ", DoctorStatus.Info => "[INFO]", DoctorStatus.Warn => "[WARN]",
                DoctorStatus.Fail => "[FAIL]", _ => "[SKIP]",
            };
            sb.AppendLine($"{tag} {s.Id,-8} {s.Detail}".TrimEnd());
        }
        if (lastError != null)
            sb.AppendLine($"Last error: {lastError.Code}{(lastError.HttpStatus is int h ? $" (HTTP {h})" : "")} at {lastError.Endpoint}");
        if (!string.IsNullOrEmpty(cause)) sb.AppendLine($"{causeLabel}: {cause}");
        return Sanitize(sb.ToString());
    }

    private static string OsLine()
    {
        var v = Environment.OSVersion.Version;
        string name = v.Major == 10 && v.Build >= 22000 ? "Windows 11" : v.Major == 10 ? "Windows 10" : "Windows";
        return $"{name} {v.Major}.{v.Minor}.{v.Build} {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
    }

    private static string Tech(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        string msg = inner.Message;
        int i = msg.IndexOfAny(['\r', '\n']);
        if (i > 0) msg = msg[..i];
        return $"{inner.GetType().Name}: {(msg.Length > 160 ? msg[..160] : msg)}" + (inner is SocketException se ? $" ({se.SocketErrorCode})" : "");
    }
}
