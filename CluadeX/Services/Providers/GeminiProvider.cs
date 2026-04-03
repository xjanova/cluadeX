using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

public class GeminiProvider : ApiProviderBase
{
    public override string ProviderId => "Gemini";
    public override string DisplayName => "Google Gemini";

    public static readonly string[] KnownModels =
    [
        "gemma-3-27b-it", "gemma-3-12b-it", "gemma-3-4b-it", "gemma-3-1b-it",
        "gemini-2.5-pro", "gemini-2.5-flash",
        "gemini-2.0-flash", "gemini-1.5-pro", "gemini-1.5-flash",
    ];

    public GeminiProvider(SettingsService settingsService) : base(settingsService) { }

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        var config = GetConfig();
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            IsReady = false;
            SetStatus("API key not configured");
            return;
        }

        SetLoading(true);
        try
        {
            var (success, msg) = await TestConnectionAsync(ct);
            IsReady = success;
            SetStatus(success
                ? $"Gemini ready ({config.EffectiveModelId ?? "gemini-2.0-flash"})"
                : msg);
        }
        finally
        {
            SetLoading(false);
        }
    }

    /// <summary>
    /// Build Gemini request with strictly alternating user/model roles.
    /// Gemini API requires alternating roles — merge consecutive same-role messages.
    /// </summary>
    private Dictionary<string, object> BuildGeminiRequest(List<ChatMessage> history, string userMessage, string? systemPrompt)
    {
        var settings = _settingsService.Settings;
        var contents = new List<Dictionary<string, object>>();

        string? lastRole = null;
        var partsBuffer = new List<Dictionary<string, string>>();

        void FlushBuffer()
        {
            if (lastRole != null && partsBuffer.Count > 0)
            {
                contents.Add(new Dictionary<string, object>
                {
                    ["role"] = lastRole,
                    ["parts"] = partsBuffer.ToList(), // snapshot
                });
                partsBuffer.Clear();
            }
        }

        foreach (var msg in history)
        {
            string role = msg.Role switch
            {
                MessageRole.Assistant => "model",
                _ => "user",
            };

            string text = msg.Role switch
            {
                MessageRole.CodeExecution => $"[Code Execution Result]\n{msg.Content}",
                MessageRole.ToolAction => $"[Tool: {msg.ToolName}]\n{msg.Content}",
                _ => msg.Content,
            };

            if (string.IsNullOrWhiteSpace(text)) continue;

            if (role == lastRole)
            {
                // Merge consecutive same-role messages
                partsBuffer.Add(new Dictionary<string, string> { ["text"] = text });
            }
            else
            {
                FlushBuffer();
                lastRole = role;
                partsBuffer.Add(new Dictionary<string, string> { ["text"] = text });
            }
        }

        FlushBuffer();

        // Add user message — merge if last was also user
        if (lastRole == "user" && contents.Count > 0)
        {
            var last = contents[^1];
            if (last["parts"] is List<Dictionary<string, string>> existingParts)
            {
                existingParts.Add(new Dictionary<string, string> { ["text"] = userMessage });
            }
        }
        else
        {
            contents.Add(new Dictionary<string, object>
            {
                ["role"] = "user",
                ["parts"] = new List<Dictionary<string, string>>
                {
                    new() { ["text"] = userMessage }
                },
            });
        }

        // Gemini requires first message to be "user"
        if (contents.Count > 0 && contents[0]["role"] as string == "model")
        {
            contents.Insert(0, new Dictionary<string, object>
            {
                ["role"] = "user",
                ["parts"] = new List<Dictionary<string, string>>
                {
                    new() { ["text"] = "[Conversation continues]" }
                },
            });
        }

        var request = new Dictionary<string, object>
        {
            ["contents"] = contents,
            ["generationConfig"] = new Dictionary<string, object>
            {
                ["temperature"] = (double)settings.Temperature,
                ["topP"] = (double)settings.TopP,
                ["maxOutputTokens"] = settings.MaxTokens,
            },
        };

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            request["system_instruction"] = new Dictionary<string, object>
            {
                ["parts"] = new List<Dictionary<string, string>>
                {
                    new() { ["text"] = systemPrompt }
                }
            };
        }

        return request;
    }

    public override async IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var config = GetConfig();
        string baseUrl = config.BaseUrl?.TrimEnd('/') ?? "https://generativelanguage.googleapis.com";
        string model = config.EffectiveModelId ?? "gemini-2.0-flash";

        var requestObj = BuildGeminiRequest(history, userMessage, systemPrompt);
        var body = JsonSerializer.Serialize(requestObj);

        string url = $"{baseUrl}/v1beta/models/{model}:streamGenerateContent?alt=sse";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-goog-api-key", config.ApiKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            // Sanitize API key from error messages
            if (config.ApiKey != null)
                error = error.Replace(config.ApiKey, "[REDACTED]");
            RaiseError($"Gemini API error {response.StatusCode}: {error}");
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];

            string? content = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                {
                    var candidate = candidates[0];
                    if (candidate.TryGetProperty("content", out var contentObj)
                        && contentObj.TryGetProperty("parts", out var parts)
                        && parts.GetArrayLength() > 0)
                    {
                        if (parts[0].TryGetProperty("text", out var textProp))
                            content = textProp.GetString();
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip malformed chunks */ }

            if (content != null)
                yield return content;
        }
    }

    public override async Task<string> GenerateAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default)
    {
        // Delegate to ChatAsync for consistent error handling via events
        var sb = new StringBuilder();
        await foreach (var token in ChatAsync(history, userMessage, systemPrompt, ct))
            sb.Append(token);
        return sb.ToString();
    }

    public override async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var config = GetConfig();
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            return (false, "API key is required");

        try
        {
            string baseUrl = config.BaseUrl?.TrimEnd('/') ?? "https://generativelanguage.googleapis.com";

            // List models to validate the key
            string url = $"{baseUrl}/v1beta/models";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-goog-api-key", config.ApiKey);
            using var response = await _httpClient.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
                return (true, $"Connected to Gemini ({config.EffectiveModelId ?? "gemini-2.0-flash"})");

            var error = await response.Content.ReadAsStringAsync(ct);
            // Sanitize API key from error messages
            if (config.ApiKey != null)
                error = error.Replace(config.ApiKey, "[REDACTED]");
            return (false, $"HTTP {(int)response.StatusCode}: {error}");
        }
        catch (Exception ex)
        {
            // Sanitize API key from exception messages
            var msg = ex.Message;
            if (config.ApiKey != null)
                msg = msg.Replace(config.ApiKey, "[REDACTED]");
            return (false, $"Connection failed: {msg}");
        }
    }
}
