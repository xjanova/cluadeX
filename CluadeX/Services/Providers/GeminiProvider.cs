using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

public class GeminiProvider : ApiProviderBase, IModelLister
{
    private readonly CostTrackingService? _costTracker;

    public override string ProviderId => "Gemini";
    public override string DisplayName => "Google Gemini";

    /// <summary>Used when no model is picked — Google's recommended model for new projects (October 2026).</summary>
    public const string DefaultModel = "gemini-3.8-flash";

    // Newest first (October 2026). Only the offline starting list: Test Connection replaces it with the
    // models this key can call (GET /v1beta/models). gemini-2.0-* and all 1.5 models are shut down;
    // 2.5 is still served, but only to accounts that already used it.
    public static readonly string[] KnownModels =
    [
        "gemini-3.8-flash", "gemini-3.7-flash", "gemini-3.6-flash", "gemini-3.5-flash",
        "gemini-3.5-flash-lite", "gemini-3.1-pro-preview", "gemini-3.1-flash-lite",
        "gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.5-flash-lite",
        // Gemma (open models served by the same API)
        "gemma-4-31b-it", "gemma-4-26b-a4b-it", "gemma-4-e4b-it", "gemma-4-e2b-it",
        "gemma-3-27b-it", "gemma-3-12b-it", "gemma-3-4b-it", "gemma-3-1b-it",
    ];

    private const string KeysUrl = "aistudio.google.com/apikey";
    private const string BillingUrl = "aistudio.google.com/usage";
    // maxOutputTokens counts thinking tokens; a local-model-sized cap leaves nothing for the answer.
    private const int ThinkingMaxTokensFloor = 32768;

    public enum ThinkingControl { None, Level, Budget }

    /// <summary>Gemini 3+ takes thinkingConfig.thinkingLevel and ignores (soon rejects) temperature/topP;
    /// 2.5 takes thinkingBudget instead — sending both is a 400. Gemma and unknown names: neither.</summary>
    public static ThinkingControl ThinkingOf(string? model)
    {
        string m = model?.Trim().ToLowerInvariant() ?? "";
        if (m.StartsWith("gemini-2.5")) return ThinkingControl.Budget;
        if (m.StartsWith("gemini-") && !m.StartsWith("gemini-1") && !m.StartsWith("gemini-2")) return ThinkingControl.Level;
        return ThinkingControl.None;
    }

    /// <summary>generationConfig.thinkingConfig for the user's effort pick and thinking toggle, or null to
    /// leave the model on its default. The toggle also asks for thought summaries so the UI can show them.</summary>
    public static Dictionary<string, object>? ThinkingConfigFor(string model, string? pick, bool thinkingToggle)
    {
        var control = ThinkingOf(model);
        if (control == ThinkingControl.None) return null;
        string p = EffortLevels.Normalize(pick);
        var cfg = new Dictionary<string, object>();

        if (control == ThinkingControl.Level)
        {
            // Every Gemini 3 model has low / medium / high; higher picks round down to high.
            string? level = p switch
            {
                "low" => "low",
                "medium" => "medium",
                "high" or "xhigh" or "max" => "high",
                _ => thinkingToggle ? "high" : null,
            };
            if (level != null) cfg["thinkingLevel"] = level;
        }
        else
        {
            bool pro = model.Contains("pro", StringComparison.OrdinalIgnoreCase);
            int? budget = p switch
            {
                "low" => 1024,
                "medium" => 8192,
                "high" => 24576,
                "xhigh" or "max" => pro ? 32768 : 24576,
                _ => thinkingToggle ? -1 : null,   // -1 = dynamic
            };
            if (budget != null) cfg["thinkingBudget"] = budget.Value;
        }

        if (thinkingToggle) cfg["includeThoughts"] = true;
        return cfg.Count > 0 ? cfg : null;
    }

    public GeminiProvider(SettingsService settingsService, CostTrackingService? costTracker = null)
        : base(settingsService)
    {
        _costTracker = costTracker;
    }

    private static string BaseUrlOf(ProviderConfig config) =>
        string.IsNullOrWhiteSpace(config.BaseUrl) ? "https://generativelanguage.googleapis.com" : config.BaseUrl.TrimEnd('/');

    private static string ModelOf(ProviderConfig config)
    {
        string m = string.IsNullOrWhiteSpace(config.EffectiveModelId) ? DefaultModel : config.EffectiveModelId.Trim();
        return m.StartsWith("models/") ? m["models/".Length..] : m;
    }

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
            SetStatus(success ? $"Gemini ready ({ModelOf(config)})" : msg);
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
    private Dictionary<string, object> BuildGeminiRequest(List<ChatMessage> history, string userMessage, string? systemPrompt, string model)
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

        // Add user message — merge if last was also user (the request always ends on a user turn:
        // a prefilled final model turn is a 400 on Gemini 3.6+)
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

        var thinking = ThinkingOf(model);
        var generationConfig = new Dictionary<string, object>
        {
            ["maxOutputTokens"] = thinking == ThinkingControl.None
                ? settings.MaxTokens : Math.Max(settings.MaxTokens, ThinkingMaxTokensFloor),
        };
        // Sampling is ignored by Gemini 3 and announced to become a 400 — only older models get it.
        if (thinking != ThinkingControl.Level)
        {
            generationConfig["temperature"] = (double)settings.Temperature;
            generationConfig["topP"] = (double)settings.TopP;
        }
        if (ThinkingConfigFor(model, settings.ReasoningEffort, settings.ExtendedThinkingEnabled) is { } tc)
            generationConfig["thinkingConfig"] = tc;

        var request = new Dictionary<string, object>
        {
            ["contents"] = contents,
            ["generationConfig"] = generationConfig,
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
        string model = ModelOf(config);

        var requestObj = BuildGeminiRequest(history, userMessage, systemPrompt, model);
        var body = JsonSerializer.Serialize(requestObj);

        string url = $"{BaseUrlOf(config)}/v1beta/models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-goog-api-key", config.ApiKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            RaiseError(DescribeHttpError("Gemini", (int)response.StatusCode, error, config.ApiKey, model, KeysUrl, BillingUrl));
            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        bool anyText = false;
        string? blockReason = null;
        JsonElement? lastUsage = null;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];

            var chunks = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("usageMetadata", out var um)) lastUsage = um.Clone();
                if (root.TryGetProperty("promptFeedback", out var pf) && pf.TryGetProperty("blockReason", out var br))
                    blockReason = br.GetString();

                if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                {
                    var candidate = candidates[0];
                    // Every part, not just the first: a chunk can carry a thought summary and text together.
                    if (candidate.TryGetProperty("content", out var contentObj)
                        && contentObj.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (!part.TryGetProperty("text", out var textProp) || textProp.GetString() is not { Length: > 0 } t)
                                continue;
                            bool thought = part.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True;
                            chunks.Add(thought ? $"<thinking>{t}</thinking>" : t);
                            if (!thought) anyText = true;
                        }
                    }
                    if (candidate.TryGetProperty("finishReason", out var fr)
                        && fr.GetString() is "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII")
                        blockReason = fr.GetString();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip malformed chunks */ }

            foreach (var c in chunks)
                yield return c;
        }

        if (!anyText && blockReason != null)
            RaiseError(string.Format(LocalizedResources.Instance["api.err.blocked"], "Gemini", blockReason));

        // usageMetadata is cumulative per chunk — the last one is the whole request.
        if (_costTracker != null && lastUsage is { } u)
        {
            int Get(string name) => u.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
            int cached = Get("cachedContentTokenCount");
            int input = Get("promptTokenCount") - cached;
            int output = Get("candidatesTokenCount") + Get("thoughtsTokenCount");
            if (input > 0 || output > 0)
                _costTracker.RecordUsage(model, Math.Max(0, input), output, cached, 0);
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
            return (false, LocalizedResources.Instance["anthropic.err.noKey"]);

        string model = ModelOf(config);
        try
        {
            var (ids, error) = await FetchModelsAsync(config, ct);
            if (ids == null) return (false, error!);
            return ids.Contains(model, StringComparer.OrdinalIgnoreCase)
                ? (true, $"Connected to Gemini ({model})")
                : (true, $"Connected to Gemini — {string.Format(LocalizedResources.Instance["api.err.modelMissing"], model)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Sanitize API key from exception messages
            var msg = ex.Message;
            if (config.ApiKey != null)
                msg = msg.Replace(config.ApiKey, "[REDACTED]");
            return (false, $"Connection failed: {msg}");
        }
    }

    /// <summary>Text-generation models this key can call (newest Gemini first, then Gemma); null on failure.</summary>
    public async Task<List<string>?> ListModelsAsync(CancellationToken ct = default)
    {
        var config = GetConfig();
        if (string.IsNullOrWhiteSpace(config.ApiKey)) return null;
        try { return (await FetchModelsAsync(config, ct)).Ids; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private static readonly string[] NonChatMarkers = ["tts", "image", "embedding", "live", "audio", "aqa", "imagen", "veo", "robotics"];

    private async Task<(List<string>? Ids, string? Error)> FetchModelsAsync(ProviderConfig config, CancellationToken ct)
    {
        var ids = new List<string>();
        string? pageToken = null;
        for (int page = 0; page < 10; page++)
        {
            string url = $"{BaseUrlOf(config)}/v1beta/models?pageSize=1000"
                         + (pageToken != null ? $"&pageToken={Uri.EscapeDataString(pageToken)}" : "");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-goog-api-key", config.ApiKey);
            using var response = await _httpClient.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return (null, DescribeHttpError("Gemini", (int)response.StatusCode, body, config.ApiKey, ModelOf(config), KeysUrl, BillingUrl));

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                foreach (var m in models.EnumerateArray())
                {
                    if (!m.TryGetProperty("name", out var n) || n.GetString() is not { Length: > 0 } name) continue;
                    bool generates = m.TryGetProperty("supportedGenerationMethods", out var sg) && sg.ValueKind == JsonValueKind.Array
                                     && sg.EnumerateArray().Any(x => x.GetString() == "generateContent");
                    string id = name.StartsWith("models/") ? name["models/".Length..] : name;
                    if (generates && !NonChatMarkers.Any(x => id.Contains(x, StringComparison.OrdinalIgnoreCase)))
                        ids.Add(id);
                }
            pageToken = root.TryGetProperty("nextPageToken", out var npt) ? npt.GetString() : null;
            if (string.IsNullOrEmpty(pageToken)) break;
        }

        return (ids.Distinct()
            .OrderBy(id => id.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList(), null);
    }
}
