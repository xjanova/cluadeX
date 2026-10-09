using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services;

/// <summary>
/// CluadeX ↔ the xman4289.com license API (xmanstudio <c>ProductLicenseController</c>).
/// Base: https://xman4289.com/api/v1/product/cluadex-ai-coding-assistant
/// POST /register-device, /check-machine, /demo, /activate, /validate, /deactivate · GET /pricing.
/// Request fields and response shapes were read off the backend source on 2026-10-09 — e.g. the license
/// fields sit under <c>data</c>, /register-device names the PC <c>machine_name</c>, and /validate answers
/// <c>success:true</c> with <c>is_valid:false</c> for an expired or revoked key.
/// </summary>
public class XmanLicenseService
{
    public const string ProductSlug = "cluadex-ai-coding-assistant";
    private const string DefaultBaseUrl = "https://xman4289.com/api/v1/product/" + ProductSlug;

    /// <summary>The store page a key is bought on (what /pricing reports as purchase_url).</summary>
    public const string DefaultPurchaseUrl = "https://xman4289.com/products/" + ProductSlug;

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public event Action<string>? OnStatusChanged;

    public XmanLicenseService(SettingsService settingsService)
    {
        _baseUrl = DefaultBaseUrl;
#if DEBUG
        // Debug builds only: the test harness (tests/apitest) points this at a localhost mock so no test ever
        // registers a device or burns a trial on the real server. A shipped (Release) build ignores it, so it
        // cannot be used to aim an installed copy at a fake license server.
        var overrideUrl = Environment.GetEnvironmentVariable("CLUADEX_LICENSE_API");
        if (!string.IsNullOrWhiteSpace(overrideUrl)) _baseUrl = overrideUrl.TrimEnd('/');
#endif
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"CluadeX/{AutoUpdateService.CurrentVersion}");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>Unique machine fingerprint for device binding (64 hex chars; the server wants 32–64).</summary>
    public static string GetMachineFingerprint()
    {
        string raw = $"{Environment.MachineName}:{Environment.UserName}:{Environment.ProcessorCount}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Register (or refresh) this PC in the backend's device registry — last seen, app version, OS.
    /// Also tells us whether the server would grant this PC a trial. Throws when the server can't be reached.
    /// </summary>
    public async Task<DeviceRegistration> RegisterDeviceAsync(CancellationToken ct = default)
    {
        var payload = new Dictionary<string, string>
        {
            ["machine_id"] = GetMachineFingerprint(),
            ["machine_name"] = Environment.MachineName,
            ["os_version"] = Environment.OSVersion.ToString(),
            ["app_version"] = AutoUpdateService.CurrentVersion,
        };

        var r = await PostAsync("/register-device", payload, ct);
        if (!r.Success || r.Data is not { } d)
            return new DeviceRegistration(false, "", false, false, null, false);

        string status = Str(d, "device_status") ?? "";
        bool trialExpired = false;
        DateTime? trialEnds = null;
        if (d.TryGetProperty("trial_info", out var ti) && ti.ValueKind == JsonValueKind.Object)
        {
            trialEnds = Utc(Str(ti, "expires_at"));
            trialExpired = ti.TryGetProperty("is_expired", out var ie) && ie.ValueKind == JsonValueKind.True;
        }
        return new DeviceRegistration(true, status, Bool(d, "can_start_trial"), Bool(d, "is_suspicious"),
            trialEnds, trialExpired);
    }

    /// <summary>
    /// The license already bound to this PC, if any (a reinstall, or a key activated here before) — so it comes
    /// back without the user typing it. Null when the server has none. Throws when the server can't be reached.
    /// </summary>
    public async Task<LicenseGrant?> CheckMachineAsync(CancellationToken ct = default)
    {
        var payload = new Dictionary<string, string> { ["machine_id"] = GetMachineFingerprint() };
        var r = await PostAsync("/check-machine", payload, ct);
        if (!r.Success || !Bool(r.Root, "has_license") || r.Data is not { } d) return null;
        return ReadGrant(d, null);
    }

    /// <summary>
    /// Ask for this PC's free trial key. The server hands out one per PC and flags a PC that keeps asking after
    /// its trial ended, so callers must only ask a PC the server has never given a trial or a license.
    /// Throws when the server can't be reached.
    /// </summary>
    public async Task<LicenseCallResult> StartDemoAsync(CancellationToken ct = default)
    {
        var payload = new Dictionary<string, string>
        {
            ["machine_id"] = GetMachineFingerprint(),
            ["app_version"] = AutoUpdateService.CurrentVersion,
        };
        var r = await PostAsync("/demo", payload, ct);
        if (r.Success && r.Data is { } d)
        {
            var grant = ReadGrant(d, "demo");
            if (grant != null) return new LicenseCallResult(true, grant, r.Message, null);
        }
        return new LicenseCallResult(false, null, r.Message, r.ErrorCode);
    }

    /// <summary>Activate a license key on this PC. <paramref name="forceRebind"/> moves it from another PC.</summary>
    public async Task<LicenseCallResult> ActivateAsync(string licenseKey, bool forceRebind = false, CancellationToken ct = default)
    {
        try
        {
            OnStatusChanged?.Invoke("Activating license...");

            var fingerprint = GetMachineFingerprint();
            var payload = new Dictionary<string, string>
            {
                ["license_key"] = licenseKey.Trim(),
                ["machine_id"] = fingerprint,
                ["machine_fingerprint"] = fingerprint,
                ["device_name"] = Environment.MachineName,
                ["app_version"] = AutoUpdateService.CurrentVersion,
            };
            if (forceRebind)
                payload["force_rebind"] = "true";

            var r = await PostAsync("/activate", payload, ct);
            if (r.Success && r.Data is { } d)
            {
                OnStatusChanged?.Invoke("License activated!");
                var grant = ReadGrant(d, null) ?? new LicenseGrant(licenseKey.Trim().ToUpperInvariant(), "", null);
                if (string.IsNullOrEmpty(grant.Key)) grant = grant with { Key = licenseKey.Trim().ToUpperInvariant() };
                return new LicenseCallResult(true, grant, r.Message, null);
            }

            OnStatusChanged?.Invoke("Activation failed");
            return new LicenseCallResult(false, null, r.Message, r.ErrorCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            OnStatusChanged?.Invoke("Connection error");
            return new LicenseCallResult(false, null, $"Connection failed: {ex.Message}", ErrorOffline);
        }
    }

    /// <summary>
    /// Ask the server whether a saved key is still good on this PC. Returns Valid=false only when the server says
    /// so outright — key unknown/not on this PC, revoked or expired. Throws on anything else (offline, timeout,
    /// rate limit, 5xx, a page that isn't the API's JSON) so a hiccup never wipes a paid license.
    /// </summary>
    public async Task<ValidateResult> ValidateAsync(string licenseKey, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, string>
        {
            ["license_key"] = licenseKey.Trim(),
            ["machine_id"] = GetMachineFingerprint(),
        };

        var r = await PostAsync("/validate", payload, ct);
        if (r.Success && r.Data is { } d)
        {
            var grant = ReadGrant(d, null);
            // Older servers left is_valid out; success alone meant valid then.
            bool valid = !r.Root.TryGetProperty("is_valid", out var iv) || iv.ValueKind == JsonValueKind.True;
            return new ValidateResult(valid, grant, valid ? null : "LICENSE_EXPIRED");
        }
        if (r.ErrorCode is "INVALID_LICENSE" or "LICENSE_REVOKED" or "LICENSE_EXPIRED")
            return new ValidateResult(false, null, r.ErrorCode);

        throw new HttpRequestException($"License server gave no verdict ({r.ErrorCode ?? r.Message})");
    }

    /// <summary>Release the key from this PC (it can then be activated here or elsewhere).</summary>
    public async Task<bool> DeactivateAsync(string licenseKey, CancellationToken ct = default)
    {
        try
        {
            var payload = new Dictionary<string, string>
            {
                ["license_key"] = licenseKey.Trim(),
                ["machine_id"] = GetMachineFingerprint(),
            };
            return (await PostAsync("/deactivate", payload, ct)).Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>What the store sells and where. Falls back to the published prices when offline.</summary>
    public async Task<PricingInfo> GetPricingAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/pricing");
            using var response = await _httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                var info = new PricingInfo { PurchaseUrl = SafeStoreUrl(Str(data, "purchase_url")) };
                if (data.TryGetProperty("plans", out var plans) && plans.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in plans.EnumerateObject())
                    {
                        if (!p.Value.TryGetProperty("price", out var price) || !price.TryGetDecimal(out var amount)) continue;
                        int? days = p.Value.TryGetProperty("duration_days", out var dd) && dd.ValueKind == JsonValueKind.Number
                            ? dd.GetInt32() : null;
                        info.Plans.Add(new PricePlan(p.Name, amount, Str(p.Value, "currency") ?? "THB", days));
                    }
                }
                if (info.Plans.Count > 0) return info;
            }
        }
        catch { }

        return PricingInfo.Fallback();
    }

    /// <summary>
    /// The purchase link we open in the browser: only ever a page on xman4289.com (owner rule — customers buy and
    /// download there, never anywhere else), whatever the API returned.
    /// </summary>
    public static string SafeStoreUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
           && (u.Host == "xman4289.com" || u.Host.EndsWith(".xman4289.com", StringComparison.Ordinal))
            ? u.AbsoluteUri
            : DefaultPurchaseUrl;

    // ─── Helpers ───

    /// <summary>The error code an Activate result carries when the server could not be reached.</summary>
    public const string ErrorOffline = "OFFLINE";

    private readonly record struct ApiReply(bool Success, string Message, string? ErrorCode, JsonElement? Data, JsonElement Root);

    private async Task<ApiReply> PostAsync(string path, Dictionary<string, string> payload, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(_baseUrl + path, content, ct);

        // Rate limit and 5xx are the server's trouble, not a verdict on the key — throw so callers treat them as
        // "offline" and keep what they have.
        if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            throw new HttpRequestException($"License server returned {(int)response.StatusCode}");

        var body = await response.Content.ReadAsStringAsync(ct);
        JsonElement root;
        using (var doc = JsonDocument.Parse(body))   // HTML (a proxy page, a 404 page) throws here → offline
            root = doc.RootElement.Clone();
        if (root.ValueKind != JsonValueKind.Object)
            throw new HttpRequestException("License server answered with something other than a JSON object");

        bool success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        string message = Str(root, "message") ?? Str(root, "error") ?? "";
        string? code = Str(root, "error_code") ?? Str(root, "code");
        JsonElement? data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : null;
        return new ApiReply(success, message, code, data, root);
    }

    private static LicenseGrant? ReadGrant(JsonElement d, string? typeIfMissing)
    {
        var key = Str(d, "license_key");
        if (string.IsNullOrWhiteSpace(key)) return null;
        var type = Str(d, "license_type") ?? Str(d, "type") ?? typeIfMissing ?? "";
        return new LicenseGrant(key.Trim(), type, Utc(Str(d, "expires_at")));
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>ISO-8601 from the server ("…Z") → UTC. DateTime.TryParse alone would hand back local time, and
    /// comparing that with UtcNow moved every expiry by the PC's UTC offset (7 h in Thailand).</summary>
    internal static DateTime? Utc(string? iso)
        => !string.IsNullOrWhiteSpace(iso)
           && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.UtcDateTime : null;
}

/// <summary>A license the server tied to this PC. <see cref="Type"/> is the server's license_type
/// (demo, monthly, yearly, lifetime, …); <see cref="ExpiresUtc"/> null = never expires.</summary>
public sealed record LicenseGrant(string Key, string Type, DateTime? ExpiresUtc);

/// <summary>What /register-device says about this PC. <see cref="Status"/>: pending (never had a trial or a
/// license), trial, licensed, expired, demo, blocked.</summary>
public sealed record DeviceRegistration(bool Registered, string Status, bool CanStartTrial, bool IsSuspicious,
    DateTime? TrialExpiresUtc, bool TrialExpired)
{
    /// <summary>The server has never given this PC a trial or a license — the only state in which asking for a
    /// trial is safe (a PC that asks again after its trial gets flagged as trial abuse).</summary>
    public bool IsFreshDevice => Registered && Status == "pending" && CanStartTrial && !IsSuspicious;

    /// <summary>This PC had its trial and it is over.</summary>
    public bool TrialIsOver => Registered && (Status is "expired" or "demo" || (Status == "trial" && TrialExpired));
}

public sealed record LicenseCallResult(bool Success, LicenseGrant? Grant, string Message, string? ErrorCode);

public sealed record ValidateResult(bool Valid, LicenseGrant? Grant, string? ErrorCode);

public sealed record PricePlan(string Type, decimal Price, string Currency, int? DurationDays);

public class PricingInfo
{
    public List<PricePlan> Plans { get; } = new();
    public string PurchaseUrl { get; set; } = XmanLicenseService.DefaultPurchaseUrl;

    /// <summary>The store's prices (xmanstudio config/licenses.php) for when /pricing can't be reached.</summary>
    public static PricingInfo Fallback()
    {
        var p = new PricingInfo();
        p.Plans.Add(new PricePlan("yearly", 199, "THB", 365));
        p.Plans.Add(new PricePlan("lifetime", 1999, "THB", null));
        return p;
    }
}
