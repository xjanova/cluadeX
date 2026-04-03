using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services;

/// <summary>
/// Auto-update service connected to xman4289.com version API.
/// API: https://xman4289.com/api/v1/products/cluadex
/// Checks for updates on startup and periodically.
/// Downloads are gated by license validation.
/// </summary>
public class AutoUpdateService
{
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;
    private const string BaseUrl = "https://xman4289.com/api/v1/products/cluadex";

    public event Action<UpdateInfo>? OnUpdateAvailable;

    public AutoUpdateService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CluadeX/2.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>Check if a newer version is available.</summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            string currentVersion = GetCurrentVersion();

            var payload = new Dictionary<string, string>
            {
                ["current_version"] = currentVersion,
                ["machine_id"] = XmanLicenseService.GetMachineFingerprint(),
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync($"{BaseUrl}/check-update", content, ct);

            var body = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            bool updateAvailable = root.TryGetProperty("update_available", out var ua) && ua.GetBoolean();

            if (updateAvailable)
            {
                var info = new UpdateInfo
                {
                    NewVersion = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                    CurrentVersion = currentVersion,
                    DownloadUrl = root.TryGetProperty("download_url", out var dl) ? dl.GetString() ?? "" : "",
                    Changelog = root.TryGetProperty("changelog", out var cl) ? cl.GetString() ?? "" : "",
                    FileSize = root.TryGetProperty("file_size", out var fs) ? fs.GetInt64() : 0,
                    FileName = root.TryGetProperty("download_filename", out var fn) ? fn.GetString() ?? "" : "",
                };

                OnUpdateAvailable?.Invoke(info);
                return info;
            }

            return null; // No update
        }
        catch
        {
            return null; // Silently fail — don't bother user if server is down
        }
    }

    /// <summary>Get the latest version info without comparing.</summary>
    public async Task<UpdateInfo?> GetLatestVersionAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{BaseUrl}/version", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            return new UpdateInfo
            {
                NewVersion = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                CurrentVersion = GetCurrentVersion(),
                Changelog = root.TryGetProperty("changelog", out var cl) ? cl.GetString() ?? "" : "",
                FileSize = root.TryGetProperty("file_size", out var fs) ? fs.GetInt64() : 0,
                FileName = root.TryGetProperty("download_filename", out var fn) ? fn.GetString() ?? "" : "",
            };
        }
        catch
        {
            return null;
        }
    }

    private static string GetCurrentVersion()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        return asm.GetName().Version?.ToString(3) ?? "2.0.0";
    }
}

public class UpdateInfo
{
    public string NewVersion { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string Changelog { get; set; } = "";
    public long FileSize { get; set; }
    public string FileName { get; set; } = "";

    public bool IsNewer => !string.IsNullOrEmpty(NewVersion) && NewVersion != CurrentVersion;
    public string FileSizeDisplay => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => $"{FileSize / 1024.0:F1} KB",
        _ => $"{FileSize / (1024.0 * 1024.0):F1} MB",
    };
}
