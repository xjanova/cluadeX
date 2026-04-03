using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services;

/// <summary>
/// Bug reporting service connected to xman4289.com API.
/// API: https://xman4289.com/api/v1/bug-reports
/// Users can submit bugs directly from the app.
/// </summary>
public class BugReportService
{
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "https://xman4289.com/api/v1/bug-reports";

    public BugReportService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CluadeX/2.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>Submit a bug report to xman server.</summary>
    public async Task<(bool success, string message)> SubmitAsync(
        BugReportData report, CancellationToken ct = default)
    {
        try
        {
            var payload = new Dictionary<string, object>
            {
                ["product_name"] = "CluadeX",
                ["product_version"] = report.AppVersion,
                ["report_type"] = report.Type,
                ["title"] = report.Title,
                ["description"] = report.Description,
                ["priority"] = report.Priority,
                ["severity"] = report.Severity,
                ["os_version"] = Environment.OSVersion.ToString(),
                ["app_version"] = report.AppVersion,
                ["device_id"] = XmanLicenseService.GetMachineFingerprint(),
            };

            if (!string.IsNullOrEmpty(report.StackTrace))
                payload["stack_trace"] = report.StackTrace;

            if (report.Metadata != null)
                payload["metadata"] = report.Metadata;

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(BaseUrl, content, ct);

            var body = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            bool success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            string message = root.TryGetProperty("message", out var msg)
                ? msg.GetString() ?? "Submitted"
                : "Submitted";

            return (success, message);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to submit: {ex.Message}");
        }
    }
}

public class BugReportData
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "bug"; // bug, feature, crash, performance
    public string Priority { get; set; } = "medium"; // low, medium, high, critical
    public string Severity { get; set; } = "minor"; // trivial, minor, major, critical, blocker
    public string AppVersion { get; set; } = "2.0.0";
    public string? StackTrace { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}
