using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

public class AnthropicProvider : ApiProviderBase
{
    public override string ProviderId => "Anthropic";
    public override string DisplayName => "Anthropic Claude";

    public static readonly string[] KnownModels =
    [
        "claude-sonnet-4-20250514", "claude-opus-4-20250514",
        "claude-haiku-3-5-20241022", "claude-3-5-sonnet-20241022",
    ];

    public AnthropicProvider(SettingsService settingsService) : base(settingsService) { }

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
                ? $"Claude ready ({config.EffectiveModelId ?? "claude-sonnet-4-20250514"})"
                : msg);
        }
        finally
        {
            SetLoading(false);
        }
    }

    /// <summary>
    /// Build Anthropic messages with strictly alternating user/assistant roles.
    /// Uses Dictionary instead of anonymous types for reliable serialization.
    /// </summary>
    private List<Dictionary<string, string>> BuildAnthropicMessages(List<ChatMessage> history, string userMessage)
    {
        var messages = new List<Dictionary<string, string>>();
        string? lastRole = null;
        var contentBuffer = new StringBuilder();

        void FlushBuffer()
        {
            if (lastRole != null && contentBuffer.Length > 0)
            {
                messages.Add(new Dictionary<string, string>
                {
                    ["role"] = lastRole,
                    ["content"] = contentBuffer.ToString(),
                });
                contentBuffer.Clear();
            }
        }

        foreach (var msg in history)
        {
            string role = msg.Role switch
            {
                MessageRole.Assistant => "assistant",
                _ => "user",
            };

            string content = msg.Role switch
            {
                MessageRole.System => $"[System]\n{msg.Content}",
                MessageRole.CodeExecution => $"[Code Execution Result]\n{msg.Content}",
                MessageRole.ToolAction => $"[Tool: {msg.ToolName}]\n{msg.Content}",
                _ => msg.Content,
            };

            if (string.IsNullOrWhiteSpace(content)) continue;

            // Anthropic requires strictly alternating roles — merge consecutive same-role
            if (role == lastRole)
            {
                contentBuffer.Append("\n\n").Append(content);
            }
            else
            {
                FlushBuffer();
                lastRole = role;
                contentBuffer.Append(content);
            }
        }

        FlushBuffer();

        // Add user message — merge if last was also user
        if (lastRole == "user" && messages.Count > 0)
        {
            var last = messages[^1];
            messages.RemoveAt(messages.Count - 1);
            messages.Add(new Dictionary<string, string>
            {
                ["role"] = "user",
                ["content"] = last["content"] + "\n\n" + userMessage,
            });
        }
        else
        {
            messages.Add(new Dictionary<string, string>
            {
                ["role"] = "user",
                ["content"] = userMessage,
            });
        }

        // Anthropic requires first message to be "user"
        if (messages.Count > 0 && messages[0]["role"] == "assistant")
        {
            messages.Insert(0, new Dictionary<string, string>
            {
                ["role"] = "user",
                ["content"] = "[Conversation continues]",
            });
        }

        return messages;
    }

    public override async IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var config = GetConfig();
        string baseUrl = config.BaseUrl?.TrimEnd('/') ?? "https://api.anthropic.com";
        string model = config.EffectiveModelId ?? "claude-sonnet-4-20250514";
        var settings = _settingsService.Settings;

        var messages = BuildAnthropicMessages(history, userMessage);

        var requestObj = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = settings.MaxTokens,
            ["temperature"] = (double)settings.Temperature,
            ["top_p"] = (double)settings.TopP,
            ["stream"] = true,
        };
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            requestObj["system"] = systemPrompt;

        var body = JsonSerializer.Serialize(requestObj);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages");
        request.Headers.Add("x-api-key", config.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrEmpty(config.ApiKey))
                error = error.Replace(config.ApiKey, "[REDACTED]");
            RaiseError($"Anthropic API error {response.StatusCode}: {error}");
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("event: "))
                continue;

            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];

            string? content = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeProp))
                {
                    string type = typeProp.GetString() ?? "";
                    if (type == "content_block_delta" && root.TryGetProperty("delta", out var delta))
                    {
                        if (delta.TryGetProperty("text", out var textProp))
                            content = textProp.GetString();
                    }
                    else if (type == "message_stop")
                    {
                        break;
                    }
                    else if (type == "error")
                    {
                        var errorMsg = root.TryGetProperty("error", out var err)
                            ? err.GetProperty("message").GetString() ?? "Unknown error"
                            : "Unknown error";
                        RaiseError($"Anthropic error: {errorMsg}");
                        yield break;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip malformed SSE chunks */ }

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
            string baseUrl = config.BaseUrl?.TrimEnd('/') ?? "https://api.anthropic.com";
            string model = config.EffectiveModelId ?? "claude-sonnet-4-20250514";

            var requestObj = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = new[] { new Dictionary<string, string> { ["role"] = "user", ["content"] = "Hi" } },
                ["max_tokens"] = 1,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages");
            request.Headers.Add("x-api-key", config.ApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(
                JsonSerializer.Serialize(requestObj), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return (true, $"Connected to Anthropic ({model})");

            var error = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrEmpty(config.ApiKey))
                error = error.Replace(config.ApiKey, "[REDACTED]");
            return (false, $"HTTP {(int)response.StatusCode}: {error}");
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (!string.IsNullOrEmpty(config.ApiKey))
                msg = msg.Replace(config.ApiKey, "[REDACTED]");
            return (false, $"Connection failed: {msg}");
        }
    }
}
