using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

public class OllamaProvider : ApiProviderBase
{
    public override string ProviderId => "Ollama";
    public override string DisplayName => "Ollama (Local)";

    private volatile List<string> _availableModels = new();
    public IReadOnlyList<string> AvailableModels => _availableModels;

    public OllamaProvider(SettingsService settingsService) : base(settingsService) { }

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        SetLoading(true);
        try
        {
            var (success, msg) = await TestConnectionAsync(ct);
            IsReady = success;
            SetStatus(success
                ? $"Ollama ready ({GetConfig().EffectiveModelId ?? "llama3"})"
                : msg);
        }
        finally
        {
            SetLoading(false);
        }
    }

    public async Task<List<string>> GetAvailableModelsAsync(CancellationToken ct = default)
    {
        var config = GetConfig();
        string baseUrl = config.BaseUrl?.TrimEnd('/') ?? "http://localhost:11434";

        try
        {
            using var response = await _httpClient.GetAsync($"{baseUrl}/api/tags", ct);
            if (!response.IsSuccessStatusCode)
                return _availableModels.ToList();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var newModels = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var models))
            {
                foreach (var model in models.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var name))
                    {
                        var modelName = name.GetString();
                        if (!string.IsNullOrEmpty(modelName))
                            newModels.Add(modelName);
                    }
                }
            }

            // Thread-safe swap
            _availableModels = newModels;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Ollama might not be running */ }

        return _availableModels.ToList();
    }

    public override async IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var config = GetConfig();
        string baseUrl = config.BaseUrl?.TrimEnd('/') ?? "http://localhost:11434";
        string model = config.EffectiveModelId ?? "llama3";
        var settings = _settingsService.Settings;

        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new { role = "system", content = systemPrompt });

        foreach (var msg in history)
        {
            string role = msg.Role switch
            {
                MessageRole.Assistant => "assistant",
                MessageRole.System => "system",
                _ => "user",
            };

            string content = msg.Role switch
            {
                MessageRole.CodeExecution => $"[Code Execution Result]\n{msg.Content}",
                MessageRole.ToolAction => $"[Tool: {msg.ToolName}]\n{msg.Content}",
                _ => msg.Content,
            };

            if (!string.IsNullOrWhiteSpace(content))
                messages.Add(new { role, content });
        }

        messages.Add(new { role = "user", content = userMessage });

        var requestObj = new
        {
            model,
            messages,
            stream = true,
            options = new
            {
                temperature = (double)settings.Temperature,
                top_p = (double)settings.TopP,
                num_predict = settings.MaxTokens,
            },
        };

        var body = JsonSerializer.Serialize(requestObj);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/chat");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            RaiseError($"Ollama API error {response.StatusCode}: {error}");
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            string? content = null;
            bool isDone = false;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                // Extract content BEFORE checking done flag
                // (the final chunk with done:true may contain the last token)
                if (root.TryGetProperty("message", out var message)
                    && message.TryGetProperty("content", out var c))
                {
                    content = c.GetString();
                }

                if (root.TryGetProperty("done", out var done) && done.GetBoolean())
                    isDone = true;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip malformed lines */ }

            if (content != null)
                yield return content;

            if (isDone)
                break;
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
        string baseUrl = config.BaseUrl?.TrimEnd('/') ?? "http://localhost:11434";

        try
        {
            // GetAvailableModelsAsync already calls /api/tags and parses the response
            var models = await GetAvailableModelsAsync(ct);
            return (true, $"Ollama connected — {models.Count} model(s) available");
        }
        catch (HttpRequestException)
        {
            return (false, $"Cannot reach Ollama at {baseUrl}. Is Ollama running?");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }
}
