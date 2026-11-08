using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Account;

/// <summary>Can this copy sign in to an Xman Studio account? From <c>app-auth/health</c>.</summary>
public enum AppAuthAvailability
{
    /// <summary>Not asked yet.</summary>
    Unknown,
    /// <summary>The server answered health: sign-in works (see the capabilities).</summary>
    Ready,
    /// <summary>The route is missing (404 without an error code): the server is not upgraded yet. Not the user's fault —
    /// account sign-in is hidden; keys and the trial still work.</summary>
    NotDeployed,
    /// <summary>No usable answer (offline, DNS, TLS, 5xx, 429, a captive-portal page): the connection doctor says why.</summary>
    Unreachable,
}

/// <summary>What <c>app-auth/health</c> said.</summary>
public sealed record AppAuthHealth(AppAuthAvailability Availability, bool PasswordLogin, bool DeviceLogin, bool FreeLicense,
    DateTime? ServerTimeUtc, int? HttpStatus, string? ErrorCode, string? TechDetail)
{
    public static readonly AppAuthHealth Unknown = new(AppAuthAvailability.Unknown, false, false, false, null, null, null, null);
}

/// <summary>The license a sign-in found for this account (installer contract 1.5). <see cref="Source"/>: purchased | trial | free.</summary>
public sealed record LinkedLicense(string Key, string Type, DateTime? ExpiresUtc, string Source, bool BoundToOtherDevice)
{
    /// <summary>A bought key (yearly, lifetime, …): it goes through /activate, which may ask to move it from another PC.
    /// A free or trial key the server already tied to this PC is taken as it is.</summary>
    public bool IsPaid => Source.Equals("purchased", StringComparison.OrdinalIgnoreCase)
                          || (!Source.Equals("free", StringComparison.OrdinalIgnoreCase)
                              && !Source.Equals("trial", StringComparison.OrdinalIgnoreCase)
                              && ActivationService.TierFor(Type) is "pro" or "lifetime");
}

/// <summary>The answer to a sign-in (password or an approved device code) — installer contract 1.5.</summary>
public sealed record LinkResult(string? AccountName, string? EmailMasked, bool AccountLinked, LinkedLicense? License, string PurchaseUrl);

/// <summary>One call's result. <see cref="ErrorCode"/> is the server's error_code, or one of ours: OFFLINE (no answer),
/// SERVER_ERROR (5xx), NOT_JSON (a page instead of the API), NOT_DEPLOYED (route missing), EMPTY_LOGIN, EMPTY_PASSWORD.</summary>
public sealed record AccountCall(bool Success, LinkResult? Link, string? ErrorCode, int? HttpStatus, string? TechDetail);

/// <summary>A device code to show as a QR (installer contract 1.3). <see cref="Secret"/> is never shown, logged or reported.</summary>
public sealed record DeviceCode(string Secret, string UserCode, string VerificationUri, string VerificationUriComplete,
    int ExpiresInSeconds, int IntervalSeconds, DateTime IssuedUtc)
{
    public DateTime ExpiresUtc => IssuedUtc.AddSeconds(ExpiresInSeconds);
    public override string ToString() => $"DeviceCode {{ UserCode = {UserCode}, ExpiresIn = {ExpiresInSeconds} }}";   // no secret in a log
}

public enum DevicePollState { Pending, SlowDown, Approved, Denied, Expired, Failed }

public sealed record DevicePoll(DevicePollState State, LinkResult? Link, string? ErrorCode, int? HttpStatus, string? TechDetail);

/// <summary>The last call that went wrong, for the connection report: the code, the HTTP status and which endpoint.</summary>
public sealed record AccountCallError(string Code, int? HttpStatus, string Endpoint, DateTime Utc);

/// <summary>What became of a LinkResult on this PC.</summary>
public enum LinkApplyStatus
{
    /// <summary>The account is linked and its license is active here.</summary>
    Activated,
    /// <summary>The account is linked; it holds no license for CluadeX (offer the store).</summary>
    LinkedNoLicense,
    /// <summary>The license is in use on another PC and the user chose not to move it.</summary>
    MoveDeclined,
    /// <summary>The account is linked but the license could not be activated (<see cref="LinkApplyResult.ErrorCode"/>).</summary>
    ActivationFailed,
}

public sealed record LinkApplyResult(LinkApplyStatus Status, string? ErrorCode, string? LicenseType, string PurchaseUrl);

/// <summary>
/// Sign-in to an Xman Studio account from the app (installer contract 1 + 2.2/2.3): <c>app-auth/health</c>, password
/// <c>app-auth/login</c>, the QR device flow (<c>device/start</c> + <c>device/poll</c>) and <c>free-license</c>. A LinkResult is
/// applied here: the account is remembered (name, masked e-mail, when — never a password, a token or a device code) and
/// a bought key goes through the existing <c>/activate</c> (with force_rebind only after the user agreed to move it).
///
/// The server part is built in parallel; until it is deployed health answers 404, which is <see cref="AppAuthAvailability.NotDeployed"/>
/// — account sign-in is hidden, typed keys and the trial keep working.
/// </summary>
public sealed class XmanAccountService
{
    private readonly SettingsService _settings;
    private readonly XmanLicenseService _license;
    private readonly ActivationService _activation;
    private readonly DebugLogService? _log;
    // Named like the other API clients' field so the harnesses can swap it for a refusing one.
    private HttpClient _httpClient;

    public XmanAccountService(SettingsService settings, XmanLicenseService license, ActivationService activation, DebugLogService? log = null)
    {
        _settings = settings;
        _license = license;
        _activation = activation;
        _log = log;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"CluadeX/{AutoUpdateService.CurrentVersion}");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>…/api/v1/product/cluadex-ai-coding-assistant (or the debug override).</summary>
    public string BaseUrl => _license.BaseUrl;

    /// <summary>The account is linked on this PC.</summary>
    public bool IsLinked => _settings.Settings.AccountLinked;
    public string? AccountEmailMasked => _settings.Settings.AccountEmailMasked;
    public string? AccountName => _settings.Settings.AccountName;

    /// <summary>The last health answer (Unknown until asked).</summary>
    public AppAuthHealth LastHealth { get; private set; } = AppAuthHealth.Unknown;

    /// <summary>The last call that failed — the connection report's "Last error" line.</summary>
    public AccountCallError? LastError { get; private set; }

    /// <summary>The account was linked or unlinked.</summary>
    public event Action? AccountChanged;

    // ─── calls ───────────────────────────────────────────────────────────────────────────

    /// <summary>GET app-auth/health — call first. Never throws (except cancellation).</summary>
    public async Task<AppAuthHealth> HealthAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, "/app-auth/health", null, ct);
        AppAuthHealth h;
        if (r.Json && r.Success && r.Data is { } d)
        {
            bool Cap(string n) => d.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Object
                                  && c.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            h = new AppAuthHealth(AppAuthAvailability.Ready, Cap("password_login"), Cap("device_login"), Cap("free_license"),
                XmanLicenseService.Utc(Str(d, "server_time")) ?? r.DateUtc, r.Status, null, null);
        }
        else
        {
            string code = r.Code ?? "NOT_JSON";
            // A 404 still carries the server's Date header: the clock check works before the route exists.
            h = new AppAuthHealth(code == "NOT_DEPLOYED" ? AppAuthAvailability.NotDeployed : AppAuthAvailability.Unreachable,
                false, false, false, r.Status != null ? r.DateUtc : null, r.Status, code, r.Tech);
        }
        LastHealth = h;
        _log?.Info("Account", $"app-auth/health · {h.Availability} · HTTP {h.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "-"} · {h.ErrorCode ?? "ok"}"
                              + (h.Availability == AppAuthAvailability.Ready ? $" · password={h.PasswordLogin} device={h.DeviceLogin} free={h.FreeLicense}" : ""));
        return h;
    }

    /// <summary>POST app-auth/login. The password goes to the server once and is kept nowhere (not in a log, not in a report).</summary>
    public async Task<AccountCall> LoginAsync(string? login, string? password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(login)) return new AccountCall(false, null, "EMPTY_LOGIN", null, null);
        if (string.IsNullOrEmpty(password)) return new AccountCall(false, null, "EMPTY_PASSWORD", null, null);
        var payload = MachinePayload();
        payload["login"] = login.Trim();
        payload["password"] = password;
        var r = await SendAsync(HttpMethod.Post, "/app-auth/login", payload, ct);
        return ToCall(r, "/app-auth/login");
    }

    /// <summary>POST app-auth/device/start — a code for the QR. Null code = failed (the other fields say why).</summary>
    public async Task<(DeviceCode? Code, string? ErrorCode, int? HttpStatus, string? Tech)> StartDeviceAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Post, "/app-auth/device/start", MachinePayload(), ct);
        if (r.Json && r.Success && r.Data is { } d)
        {
            string? secret = Str(d, "device_code"), user = Str(d, "user_code"), uri = Str(d, "verification_uri"), full = Str(d, "verification_uri_complete");
            if (!string.IsNullOrEmpty(secret) && !string.IsNullOrEmpty(user) && SafeVerificationUri(full ?? uri) is { } safe)
            {
                int expires = Int(d, "expires_in") is > 0 and var e ? Math.Min(e, 3600) : 600;
                int interval = Int(d, "interval") is > 0 and var i ? Math.Clamp(i, 1, 30) : 3;
                return (new DeviceCode(secret, user, SafeVerificationUri(uri) ?? safe, safe, expires, interval, DateTime.UtcNow), null, r.Status, null);
            }
            Remember("BAD_RESPONSE", r.Status, "/app-auth/device/start");
            return (null, "BAD_RESPONSE", r.Status, "device/start: missing or unsafe fields");
        }
        string code = r.Code ?? "NOT_JSON";
        Remember(code, r.Status, "/app-auth/device/start");
        return (null, code, r.Status, r.Tech);
    }

    /// <summary>POST app-auth/device/poll — once per <see cref="DeviceCode.IntervalSeconds"/> (SLOW_DOWN adds 2 s).</summary>
    public async Task<DevicePoll> PollDeviceAsync(DeviceCode code, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, string> { ["device_code"] = code.Secret, ["machine_id"] = XmanLicenseService.GetMachineFingerprint() };
        var r = await SendAsync(HttpMethod.Post, "/app-auth/device/poll", payload, ct);
        if (r.Json && r.Success && r.Data is { } d && ParseLinkResult(d) is { } link)
            return new DevicePoll(DevicePollState.Approved, link, null, r.Status, null);
        var state = r.Code switch
        {
            "AUTHORIZATION_PENDING" => DevicePollState.Pending,
            "SLOW_DOWN" => DevicePollState.SlowDown,
            "ACCESS_DENIED" => DevicePollState.Denied,
            "EXPIRED_TOKEN" => DevicePollState.Expired,
            _ => DevicePollState.Failed,
        };
        string? err = r.Json && r.Success ? "BAD_RESPONSE" : r.Code ?? "NOT_JSON";
        if (state == DevicePollState.Failed) Remember(err!, r.Status, "/app-auth/device/poll");
        return new DevicePoll(state, null, state is DevicePollState.Pending or DevicePollState.SlowDown ? null : err, r.Status, r.Tech);
    }

    /// <summary>POST app-auth/free-license — the quick path ("ติดตั้งแบบเร็ว").</summary>
    public async Task<AccountCall> FreeLicenseAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Post, "/app-auth/free-license", MachinePayload(), ct);
        return ToCall(r, "/app-auth/free-license");
    }

    // ─── applying a LinkResult ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Remember the account and put its license to work on this PC (contract 2.3). A bought key goes through /activate;
    /// when it is active on another PC <paramref name="confirmMove"/> is asked ("ไลเซ่นนี้ใช้อยู่บนเครื่องอื่น ย้ายมาเครื่องนี้ไหม")
    /// and only a yes sends force_rebind. A free/trial key the server already tied to this PC is taken as it is.
    /// </summary>
    public async Task<LinkApplyResult> ApplyAsync(LinkResult link, Func<Task<bool>> confirmMove, CancellationToken ct = default)
    {
        string purchase = XmanLicenseService.SafeStoreUrl(link.PurchaseUrl);
        if (link.AccountLinked) RememberAccount(link);

        if (link.License is not { } lic) return new LinkApplyResult(LinkApplyStatus.LinkedNoLicense, null, null, purchase);

        if (!lic.IsPaid)
        {
            bool taken = _activation.AdoptServerGrant(new LicenseGrant(lic.Key, lic.Type, lic.ExpiresUtc));
            _log?.Info("Account", $"license from the account taken as is · type={lic.Type} · source={lic.Source} · adopted={taken}");
            return new LinkApplyResult(LinkApplyStatus.Activated, null, lic.Type, purchase);
        }

        var r = await _activation.ActivateLinkedAsync(lic.Key, forceRebind: false, ct);
        if (r.Success) return Done(r, lic);
        if (r.ErrorCode == "ALREADY_ACTIVATED_OTHER_DEVICE")
        {
            Remember(r.ErrorCode, 403, "/activate");
            if (!await confirmMove()) return new LinkApplyResult(LinkApplyStatus.MoveDeclined, r.ErrorCode, lic.Type, purchase);
            r = await _activation.ActivateLinkedAsync(lic.Key, forceRebind: true, ct);
            if (r.Success) return Done(r, lic);
        }
        string code = r.ErrorCode ?? "INVALID_LICENSE";
        Remember(code, null, "/activate");
        _log?.Warn("Account", $"license from the account not activated · {code}");
        return new LinkApplyResult(LinkApplyStatus.ActivationFailed, code, lic.Type, purchase);

        LinkApplyResult Done(LicenseCallResult ok, LinkedLicense l)
        {
            _log?.Info("Account", $"license from the account activated · type={ok.Grant?.Type ?? l.Type}");
            return new LinkApplyResult(LinkApplyStatus.Activated, null, ok.Grant?.Type ?? l.Type, purchase);
        }
    }

    /// <summary>Forget the account on this PC (the license, if any, stays).</summary>
    public void Unlink()
    {
        _settings.UpdateSettings(s =>
        {
            s.AccountLinked = false;
            s.AccountEmailMasked = null;
            s.AccountName = null;
            s.AccountLinkedUtc = null;
        });
        AccountChanged?.Invoke();
    }

    private void RememberAccount(LinkResult link)
    {
        var now = DateTime.UtcNow;
        _settings.UpdateSettings(s =>
        {
            s.AccountLinked = true;
            s.AccountEmailMasked = Trim(link.EmailMasked, 120);
            s.AccountName = Trim(link.AccountName, 120);
            s.AccountLinkedUtc = now;
        });
        _log?.Info("Account", $"account linked on this PC · {Trim(link.EmailMasked, 120) ?? "(no e-mail)"}");
        AccountChanged?.Invoke();
    }

    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= max ? s.Trim() : s.Trim()[..max];

    // ─── parsing (public for the harness) ─────────────────────────────────────────────────

    /// <summary>The LinkResult under <c>data</c>, or null when it is not one.</summary>
    public static LinkResult? ParseLinkResult(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        string? name = null, email = null;
        if (data.TryGetProperty("account", out var acc) && acc.ValueKind == JsonValueKind.Object)
        {
            name = Str(acc, "name");
            email = Str(acc, "email_masked");
        }
        bool linked = data.TryGetProperty("account_linked", out var al) && al.ValueKind == JsonValueKind.True;
        LinkedLicense? lic = null;
        if (data.TryGetProperty("license", out var l) && l.ValueKind == JsonValueKind.Object && Str(l, "license_key") is { Length: > 0 } key)
        {
            lic = new LinkedLicense(key.Trim(), Str(l, "license_type") ?? "", XmanLicenseService.Utc(Str(l, "expires_at")),
                Str(l, "source") ?? "", l.TryGetProperty("bound_to_other_device", out var b) && b.ValueKind == JsonValueKind.True);
        }
        if (!linked && lic == null) return null;   // neither an account nor a license: not a LinkResult
        return new LinkResult(name, email, linked, lic, XmanLicenseService.SafeStoreUrl(Str(data, "purchase_url")));
    }

    /// <summary>The page the QR opens: https on xman4289.com only (a QR is a link the user's phone will follow).</summary>
    public static string? SafeVerificationUri(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return null;
#if DEBUG
        if (u.IsLoopback && u.Scheme == Uri.UriSchemeHttp) return u.AbsoluteUri;   // the harness's mock server
#endif
        return u.Scheme == Uri.UriSchemeHttps && (u.Host == "xman4289.com" || u.Host.EndsWith(".xman4289.com", StringComparison.Ordinal))
            ? u.AbsoluteUri : null;
    }

    // ─── transport ──────────────────────────────────────────────────────────────────────

    private readonly record struct Reply(int? Status, bool Json, bool Success, string? Code, JsonElement? Data, string? Tech)
    {
        /// <summary>The response's Date header (UTC), when there was a response.</summary>
        public DateTime? DateUtc { get; init; }
    }

    private static Dictionary<string, string> MachinePayload() => new()
    {
        ["machine_id"] = XmanLicenseService.GetMachineFingerprint(),
        ["machine_name"] = Environment.MachineName,
        ["app_version"] = AutoUpdateService.CurrentVersion,
    };

    private AccountCall ToCall(Reply r, string endpoint)
    {
        if (r.Json && r.Success && r.Data is { } d && ParseLinkResult(d) is { } link)
            return new AccountCall(true, link, null, r.Status, null);
        string code = r.Json && r.Success ? "BAD_RESPONSE" : r.Code ?? "NOT_JSON";
        Remember(code, r.Status, endpoint);
        return new AccountCall(false, null, code, r.Status, r.Tech);
    }

    private void Remember(string code, int? status, string endpoint)
    {
        LastError = new AccountCallError(code, status, endpoint, DateTime.UtcNow);
        _log?.Info("Account", $"{endpoint} · {code} · HTTP {status?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
    }

    /// <summary>One call. Never throws but for the caller's own cancellation: a transport failure is OFFLINE, 5xx is
    /// SERVER_ERROR, 429 RATE_LIMIT_EXCEEDED, a page instead of JSON NOT_JSON, a 404 without an error code NOT_DEPLOYED.</summary>
    private async Task<Reply> SendAsync(HttpMethod method, string path, Dictionary<string, string>? payload, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(method, BaseUrl + path);
            if (payload != null) req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            int status = (int)resp.StatusCode;
            string media = resp.Content.Headers.ContentType?.MediaType ?? "";
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Classify(status, media, body) with { DateUtc = resp.Headers.Date?.UtcDateTime };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // HttpClient's own timeout is a TaskCanceledException without our token: that is "no answer", not a cancel.
            return new Reply(null, false, false, "OFFLINE", null, $"{ex.GetType().Name}: {FirstLine(ex.Message)}");
        }
    }

    /// <summary>The envelope rules, pure (the harness feeds it answers). Public for tests.</summary>
    public static (int? Status, bool Json, bool Success, string? Code, string? Tech) ClassifyForTest(int status, string media, string body)
    {
        var r = Classify(status, media, body);
        return (r.Status, r.Json, r.Success, r.Code, r.Tech);
    }

    private static Reply Classify(int status, string media, string body)
    {
        JsonElement? root = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) root = doc.RootElement.Clone();
        }
        catch (JsonException) { }

        if (root is not { } r)
        {
            // Not JSON: a captive portal, Cloudflare's challenge, a proxy page — or a 5xx/429 page.
            string code = status == 429 ? "RATE_LIMIT_EXCEEDED" : status >= 500 ? "SERVER_ERROR" : status == 404 ? "NOT_DEPLOYED" : "NOT_JSON";
            return new Reply(status, false, false, code, null, $"HTTP {status} {media} ({body.Length} bytes, not JSON)");
        }
        bool success = r.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        string? err = Str(r, "error_code") ?? Str(r, "code");
        JsonElement? data = r.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : null;
        if (!success && err == null)
            err = status switch
            {
                404 => "NOT_DEPLOYED",                 // Laravel's own 404 for a route that does not exist yet
                429 => "RATE_LIMIT_EXCEEDED",
                >= 500 => "SERVER_ERROR",
                422 => "VALIDATION_FAILED",
                _ => "HTTP_" + status.ToString(CultureInfo.InvariantCulture),
            };
        return new Reply(status, true, success, err, data, success ? null : $"HTTP {status} {err}");
    }

    private static string FirstLine(string s) { int i = s.IndexOfAny(['\r', '\n']); return (i > 0 ? s[..i] : s).Trim(); }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;
}
