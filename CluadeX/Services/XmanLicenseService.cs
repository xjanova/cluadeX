using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services;

/// <summary>
/// Connects CluadeX activation system to xman4289.com license API.
/// API: https://xman4289.com/api/v1/product/cluadex
/// Endpoints: /register-device, /activate, /validate, /deactivate, /pricing, /demo
/// </summary>
public class XmanLicenseService
{
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;

    private const string BaseUrl = "https://xman4289.com/api/v1/product/cluadex";
    private const string ProductSlug = "cluadex";

    public event Action<string>? OnStatusChanged;

    public XmanLicenseService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CluadeX/2.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>Generate a unique machine fingerprint for device binding.</summary>
    public static string GetMachineFingerprint()
    {
        string raw = $"{Environment.MachineName}:{Environment.UserName}:{Environment.ProcessorCount}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Register this device with the xman license server.</summary>
    public async Task<(bool success, string message)> RegisterDeviceAsync(CancellationToken ct = default)
    {
        try
        {
            var payload = new Dictionary<string, string>
            {
                ["machine_id"] = GetMachineFingerprint(),
                ["device_name"] = Environment.MachineName,
                ["os_version"] = Environment.OSVersion.ToString(),
                ["app_version"] = GetAppVersion(),
            };

            var response = await PostAsync($"{BaseUrl}/register-device", payload, ct);
            return (response.success, response.message);
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>Activate a license key via xman API.</summary>
    public async Task<(bool success, string tier, string message)> ActivateAsync(
        string licenseKey, CancellationToken ct = default)
    {
        try
        {
            OnStatusChanged?.Invoke("Activating license...");

            var payload = new Dictionary<string, string>
            {
                ["license_key"] = licenseKey.Trim(),
                ["machine_id"] = GetMachineFingerprint(),
                ["device_name"] = Environment.MachineName,
                ["app_version"] = GetAppVersion(),
            };

            var response = await PostAsync($"{BaseUrl}/activate", payload, ct);

            if (response.success)
            {
                string tier = response.data?.GetProperty("status").GetString() switch
                {
                    "lifetime" => "enterprise",
                    "yearly" or "monthly" => "pro",
                    "demo" or "daily" or "weekly" => "dev",
                    _ => "pro",
                };

                OnStatusChanged?.Invoke("License activated!");
                return (true, tier, response.message);
            }

            OnStatusChanged?.Invoke("Activation failed");
            return (false, "free", response.message);
        }
        catch (Exception ex)
        {
            OnStatusChanged?.Invoke("Connection error");
            return (false, "free", $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>Validate existing license key is still active.</summary>
    public async Task<(bool valid, string tier, DateTime? expiresAt)> ValidateAsync(
        string licenseKey, CancellationToken ct = default)
    {
        try
        {
            var payload = new Dictionary<string, string>
            {
                ["license_key"] = licenseKey.Trim(),
                ["machine_id"] = GetMachineFingerprint(),
            };

            var response = await PostAsync($"{BaseUrl}/validate", payload, ct);

            if (response.success && response.data.HasValue)
            {
                string tier = response.data.Value.GetProperty("status").GetString() switch
                {
                    "lifetime" => "enterprise",
                    "yearly" or "monthly" => "pro",
                    _ => "pro",
                };

                DateTime? expires = null;
                if (response.data.Value.TryGetProperty("expires_at", out var exp)
                    && exp.ValueKind == JsonValueKind.String)
                    expires = DateTime.TryParse(exp.GetString(), out var d) ? d : null;

                return (true, tier, expires);
            }

            return (false, "free", null);
        }
        catch
        {
            // Offline fallback — allow cached activation
            return (false, "free", null);
        }
    }

    /// <summary>Deactivate license from this machine.</summary>
    public async Task<bool> DeactivateAsync(string licenseKey, CancellationToken ct = default)
    {
        try
        {
            var payload = new Dictionary<string, string>
            {
                ["license_key"] = licenseKey.Trim(),
                ["machine_id"] = GetMachineFingerprint(),
            };

            var response = await PostAsync($"{BaseUrl}/deactivate", payload, ct);
            return response.success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Get pricing information.</summary>
    public async Task<PricingInfo?> GetPricingAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/pricing");
            using var response = await _httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var data))
            {
                return new PricingInfo
                {
                    MonthlyPrice = data.TryGetProperty("monthly", out var m) ? m.GetDecimal() : 199,
                    YearlyPrice = data.TryGetProperty("yearly", out var y) ? y.GetDecimal() : 899,
                    LifetimePrice = data.TryGetProperty("lifetime", out var l) ? l.GetDecimal() : 4999,
                    Currency = "THB",
                };
            }
        }
        catch { }

        // Default pricing
        return new PricingInfo
        {
            MonthlyPrice = 199, YearlyPrice = 899, LifetimePrice = 4999, Currency = "THB"
        };
    }

    // ─── Helpers ───

    private async Task<(bool success, string message, JsonElement? data)> PostAsync(
        string url, Dictionary<string, string> payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(url, content, ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        bool success = root.TryGetProperty("success", out var s) && s.GetBoolean();
        string message = root.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
        JsonElement? data = root.TryGetProperty("data", out var d) ? d : null;

        return (success, message, data);
    }

    private static string GetAppVersion()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        return asm.GetName().Version?.ToString() ?? "2.0.0";
    }
}

public class PricingInfo
{
    public decimal MonthlyPrice { get; set; } = 199;
    public decimal YearlyPrice { get; set; } = 899;
    public decimal LifetimePrice { get; set; } = 4999;
    public string Currency { get; set; } = "THB";
}
