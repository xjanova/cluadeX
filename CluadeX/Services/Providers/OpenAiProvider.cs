using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

public class OpenAiProvider : ApiProviderBase
{
    public override string ProviderId => "OpenAI";
    public override string DisplayName => "OpenAI";

    public static readonly string[] KnownModels =
    [
        "gpt-4o", "gpt-4o-mini", "gpt-4-turbo", "gpt-4",
        "gpt-3.5-turbo", "o1", "o1-mini", "o1-pro", "o3", "o3-mini",
    ];

    /// <summary>
    /// Reasoning models that don't support temperature/top_p and use max_completion_tokens.
    /// </summary>
    private static readonly HashSet<string> ReasoningModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "o1", "o1-mini", "o1-pro", "o3", "o3-mini",
    };

    private static bool IsReasoningModel(string model)
        => ReasoningModels.Contains(model) || model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
           || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase);

    public OpenAiProvider(SettingsService settingsService) : base(settingsService) { }

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
            SetStatus(success ? $"OpenAI ready ({config.EffectiveModelId ?? "gpt-4o"})" : msg);
        }
        finally
        {
            SetLoading(false);
        }
    }

    public override async IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var config = GetConfig();
        string baseUrl = config.BaseUrl?.TrimEnd('/') ?? "https://api.openai.com/v1";
        string model = config.EffectiveModelId ?? "gpt-4o";
        var settings = _settingsService.Settings;

        var messages = BuildChatMessages(history, userMessage, systemPrompt);
        bool isReasoning = IsReasoningModel(model);

        // Build request object — reasoning models don't support temperature/top_p
        // and use max_completion_tokens instead of max_tokens
        var requestObj = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = true,
        };

        if (isReasoning)
        {
            requestObj["max_completion_tokens"] = settings.MaxTokens;
        }
        else
        {
            requestObj["temperature"] = (double)settings.Temperature;
            requestObj["max_tokens"] = settings.MaxTokens;
            requestObj["top_p"] = (double)settings.TopP;
        }

        var body = JsonSerializer.Serialize(requestObj);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrEmpty(config.ApiKey))
                error = error.Replace(config.ApiKey, "[REDACTED]");
            RaiseError($"OpenAI API error {response.StatusCode}: {error}");
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
            if (data == "[DONE]") break;

            string? content = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() > 0)
                {
                    var delta = choices[0].GetProperty("delta");
                    if (delta.TryGetProperty("content", out var c))
                        content = c.GetString();
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
            string baseUrl = config.BaseUrl?.TrimEnd('/') ?? "https://api.openai.com/v1";
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
            request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");

            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return (true, $"Connected to OpenAI ({config.EffectiveModelId ?? "gpt-4o"})");

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
