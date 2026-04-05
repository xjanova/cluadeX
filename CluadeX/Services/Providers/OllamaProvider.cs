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

    private const string DefaultBaseUrl = "http://localhost:11434";

    private volatile List<string> _availableModels = new();
    public IReadOnlyList<string> AvailableModels => _availableModels;

    /// <summary>Token counts from the last completed generation (populated from final streaming chunk).</summary>
    public int LastPromptTokens { get; private set; }
    public int LastCompletionTokens { get; private set; }
    public long LastTotalDurationNs { get; private set; }
    public long LastEvalDurationNs { get; private set; }

    public OllamaProvider(SettingsService settingsService) : base(settingsService) { }

    private string GetBaseUrl()
    {
        var config = GetConfig();
        return config.BaseUrl?.TrimEnd('/') ?? DefaultBaseUrl;
    }

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
        string baseUrl = GetBaseUrl();

        try
        {
            using var response = await _httpClient.GetAsync($"{baseUrl}/api/tags", ct);
            response.EnsureSuccessStatusCode();

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
        catch (HttpRequestException ex)
        {
            RaiseError($"Cannot reach Ollama at {baseUrl}: {ex.Message}");
        }
        catch { /* Ollama might not be running */ }

        return _availableModels.ToList();
    }

    public override async IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string baseUrl = GetBaseUrl();
        var config = GetConfig();
        string model = config.EffectiveModelId ?? "llama3";
        var settings = _settingsService.Settings;

        // Reset token counts
        LastPromptTokens = 0;
        LastCompletionTokens = 0;
        LastTotalDurationNs = 0;
        LastEvalDurationNs = 0;

        // Build messages with proper role handling
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new { role = "system", content = systemPrompt });

        string? lastRole = "system";
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

            if (string.IsNullOrWhiteSpace(content)) continue;

            // Merge consecutive messages with same role (Ollama requires alternating)
            if (role == lastRole && messages.Count > 0)
            {
                // Append to the last message's content
                var lastMsg = messages[^1];
                var lastContent = lastMsg.GetType().GetProperty("content")?.GetValue(lastMsg) as string;
                messages[^1] = new { role, content = lastContent + "\n\n" + content };
            }
            else
            {
                messages.Add(new { role, content });
            }
            lastRole = role;
        }

        // Ensure last message before user is not also "user"
        if (lastRole == "user" && messages.Count > 0)
        {
            var lastMsg = messages[^1];
            var lastContent = lastMsg.GetType().GetProperty("content")?.GetValue(lastMsg) as string;
            messages[^1] = new { role = "user", content = lastContent + "\n\n" + userMessage };
        }
        else
        {
            messages.Add(new { role = "user", content = userMessage });
        }

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
                repeat_penalty = (double)settings.RepeatPenalty,
                repeat_last_n = settings.RepeatPenaltyTokens,
            },
        };

        var body = JsonSerializer.Serialize(requestObj);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/chat");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            // Ollama is unreachable — mark as not ready and give actionable error
            IsReady = false;
            RaiseError($"Cannot connect to Ollama at {baseUrl}. Is Ollama running? ({ex.Message})");
            yield break;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            RaiseError($"Request to Ollama timed out. The model may be loading or Ollama is unresponsive.");
            yield break;
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            string hint = response.StatusCode switch
            {
                System.Net.HttpStatusCode.NotFound =>
                    $"Model '{model}' not found. Run 'ollama pull {model}' to download it.",
                System.Net.HttpStatusCode.InternalServerError =>
                    $"Ollama internal error (model may be out of memory). Try a smaller model.\n{TruncateError(error)}",
                System.Net.HttpStatusCode.ServiceUnavailable =>
                    "Ollama is busy loading a model. Please wait and try again.",
                _ => $"Ollama API error {(int)response.StatusCode}: {TruncateError(error)}",
            };
            RaiseError(hint);
            response.Dispose();
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
                {
                    isDone = true;

                    // Extract token counts and performance metrics from final chunk
                    if (root.TryGetProperty("prompt_eval_count", out var promptTokens))
                        LastPromptTokens = promptTokens.GetInt32();
                    if (root.TryGetProperty("eval_count", out var evalTokens))
                        LastCompletionTokens = evalTokens.GetInt32();
                    if (root.TryGetProperty("total_duration", out var totalDur))
                        LastTotalDurationNs = totalDur.GetInt64();
                    if (root.TryGetProperty("eval_duration", out var evalDur))
                        LastEvalDurationNs = evalDur.GetInt64();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (JsonException ex)
            {
                RaiseError($"[malformed response chunk]: {ex.Message}");
            }
            catch { /* skip truly broken lines */ }

            if (content != null)
                yield return content;

            if (isDone)
                break;
        }

        response.Dispose();
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
        string baseUrl = GetBaseUrl();

        try
        {
            var models = await GetAvailableModelsAsync(ct);
            if (models.Count == 0)
                return (true, $"Ollama connected at {baseUrl} but no models found. Run 'ollama pull <model>' first.");
            return (true, $"Ollama connected — {models.Count} model(s) available");
        }
        catch (HttpRequestException)
        {
            return (false, $"Cannot reach Ollama at {baseUrl}. Is Ollama running? Start it with 'ollama serve'.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    private static string TruncateError(string error) =>
        error.Length > 200 ? error[..200] + "..." : error;
}
