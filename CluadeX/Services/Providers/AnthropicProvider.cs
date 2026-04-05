using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

public class AnthropicProvider : ApiProviderBase
{
    private readonly CostTrackingService? _costTracker;

    // Pre-cached empty JSON object to avoid allocating a new JsonDocument on every tool_use block
    private static readonly JsonElement _emptyJsonObject =
        JsonDocument.Parse("{}").RootElement.Clone();

    public override string ProviderId => "Anthropic";
    public override string DisplayName => "Anthropic Claude";

    public static readonly string[] KnownModels =
    [
        "claude-sonnet-4-20250514", "claude-opus-4-20250514",
        "claude-haiku-3-5-20241022", "claude-3-5-sonnet-20241022",
    ];

    public AnthropicProvider(SettingsService settingsService, CostTrackingService? costTracker = null)
        : base(settingsService)
    {
        _costTracker = costTracker;
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
            ["stream"] = true,
        };

        // Extended thinking: when enabled, send thinking parameter (disables temperature/top_p)
        if (settings.ExtendedThinkingEnabled)
        {
            // Ensure max_tokens > budget_tokens (Anthropic API requirement)
            int budgetTokens = settings.ThinkingBudgetTokens;
            int maxTokens = settings.MaxTokens;
            if (maxTokens <= budgetTokens)
                maxTokens = budgetTokens + 4096; // Auto-raise max_tokens
            requestObj["max_tokens"] = maxTokens;

            requestObj["thinking"] = new Dictionary<string, object>
            {
                ["type"] = "enabled",
                ["budget_tokens"] = budgetTokens,
            };
            // Note: temperature and top_p are not allowed with extended thinking
        }
        else
        {
            requestObj["temperature"] = (double)settings.Temperature;
            requestObj["top_p"] = (double)settings.TopP;
        }

        // Prompt caching: wrap system prompt in cache_control blocks
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            if (settings.PromptCachingEnabled)
            {
                requestObj["system"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = systemPrompt,
                        ["cache_control"] = new Dictionary<string, string> { ["type"] = "ephemeral" },
                    },
                };
            }
            else
            {
                requestObj["system"] = systemPrompt;
            }
        }

        var body = JsonSerializer.Serialize(requestObj);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages");
        request.Headers.Add("x-api-key", config.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        {
            var betaFeatures = new List<string>();
            if (settings.ExtendedThinkingEnabled)
                betaFeatures.Add("interleaved-thinking-2025-05-14");
            if (settings.PromptCachingEnabled)
                betaFeatures.Add("prompt-caching-2024-07-31");
            if (betaFeatures.Count > 0)
                request.Headers.Add("anthropic-beta", string.Join(",", betaFeatures));
        }
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
                        string deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() ?? "" : "";

                        if (deltaType == "thinking_delta" && delta.TryGetProperty("thinking", out var thinkingProp))
                        {
                            // Extended thinking content — emit with marker for UI
                            content = thinkingProp.GetString();
                            if (content != null)
                                content = $"<thinking>{content}</thinking>";
                        }
                        else if (delta.TryGetProperty("text", out var textProp))
                        {
                            content = textProp.GetString();
                        }
                    }
                    else if (type == "message_delta" && root.TryGetProperty("usage", out var usageDelta))
                    {
                        // Record output token usage (not a new request — input was recorded at message_start)
                        int outTokens = usageDelta.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                        if (_costTracker != null && outTokens > 0)
                            _costTracker.RecordUsage(model, 0, outTokens, isNewRequest: false);
                    }
                    else if (type == "message_start" && root.TryGetProperty("message", out var msgStart))
                    {
                        // Record input token usage from message_start event
                        if (msgStart.TryGetProperty("usage", out var startUsage))
                        {
                            int inTokens = startUsage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                            int cacheRead = startUsage.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0;
                            int cacheCreate = startUsage.TryGetProperty("cache_creation_input_tokens", out var cc) ? cc.GetInt32() : 0;
                            if (_costTracker != null && inTokens > 0)
                                _costTracker.RecordUsage(model, inTokens, 0, cacheRead, cacheCreate);
                        }
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

    // ═══════════════════════════════════════════
    // Phase 5: Native Tool Use Support
    // ═══════════════════════════════════════════

    public override bool SupportsNativeToolUse => true;

    public override async Task<NativeToolResponse> ChatWithToolsAsync(
        List<NativeMessage> messages,
        string systemPrompt,
        List<ToolSchema> tools,
        CancellationToken ct = default)
    {
        var config = GetConfig();
        string baseUrl = config.BaseUrl?.TrimEnd('/') ?? "https://api.anthropic.com";
        string model = config.EffectiveModelId ?? "claude-sonnet-4-20250514";
        var settings = _settingsService.Settings;

        // Build tool definitions
        var toolDefs = tools.Select(t => new Dictionary<string, object>
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["input_schema"] = t.InputSchema,
        }).ToList();

        // Build request
        var requestObj = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages.Select(m => new Dictionary<string, object>
            {
                ["role"] = m.Role,
                ["content"] = m.Content.Select<ContentBlock, object>(block => block.Type switch
                {
                    "text" => new Dictionary<string, object> { ["type"] = "text", ["text"] = block.Text ?? "" },
                    "tool_use" => new Dictionary<string, object>
                    {
                        ["type"] = "tool_use",
                        ["id"] = block.Id ?? "",
                        ["name"] = block.Name ?? "",
                        ["input"] = block.Input ?? _emptyJsonObject,
                    },
                    "tool_result" => new Dictionary<string, object>
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = block.ToolUseId ?? "",
                        ["content"] = block.Content ?? "",
                        ["is_error"] = block.IsError ?? false,
                    },
                    _ => new Dictionary<string, object> { ["type"] = "text", ["text"] = block.Text ?? "" },
                }).ToList(),
            }).ToList(),
            ["max_tokens"] = settings.MaxTokens,
            ["tools"] = toolDefs,
        };

        // Extended thinking for native tool use
        if (settings.ExtendedThinkingEnabled)
        {
            int budgetTokens = settings.ThinkingBudgetTokens;
            int maxTokens = settings.MaxTokens;
            // Ensure max_tokens > budget_tokens (Anthropic API requirement)
            if (maxTokens <= budgetTokens)
                maxTokens = budgetTokens + 4096;
            requestObj["max_tokens"] = maxTokens;

            requestObj["thinking"] = new Dictionary<string, object>
            {
                ["type"] = "enabled",
                ["budget_tokens"] = budgetTokens,
            };
        }

        // Prompt caching for system prompt
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            if (settings.PromptCachingEnabled)
            {
                requestObj["system"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "text",
                        ["text"] = systemPrompt,
                        ["cache_control"] = new Dictionary<string, string> { ["type"] = "ephemeral" },
                    },
                };
            }
            else
            {
                requestObj["system"] = systemPrompt;
            }
        }

        var body = JsonSerializer.Serialize(requestObj, new JsonSerializerOptions { WriteIndented = false });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages");
        request.Headers.Add("x-api-key", config.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        {
            var betaFeatures = new List<string>();
            if (settings.ExtendedThinkingEnabled)
                betaFeatures.Add("interleaved-thinking-2025-05-14");
            if (settings.PromptCachingEnabled)
                betaFeatures.Add("prompt-caching-2024-07-31");
            if (betaFeatures.Count > 0)
                request.Headers.Add("anthropic-beta", string.Join(",", betaFeatures));
        }
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrEmpty(config.ApiKey))
                error = error.Replace(config.ApiKey, "[REDACTED]");

            // Re-throw as HttpRequestException with status code for reactive compaction
            throw new HttpRequestException(
                $"Anthropic API error: {error}",
                null,
                response.StatusCode);
        }

        var responseText = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseText);
        var root = doc.RootElement;

        var result = new NativeToolResponse();

        // Parse stop_reason
        if (root.TryGetProperty("stop_reason", out var stopReasonProp))
            result.StopReason = stopReasonProp.GetString() ?? "end_turn";

        // Parse usage
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var inputTokens))
                result.InputTokens = inputTokens.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var outputTokens))
                result.OutputTokens = outputTokens.GetInt32();

            // Record cost
            int cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var cr2) ? cr2.GetInt32() : 0;
            int cacheCreate = usage.TryGetProperty("cache_creation_input_tokens", out var cc2) ? cc2.GetInt32() : 0;
            _costTracker?.RecordUsage(model, result.InputTokens, result.OutputTokens, cacheRead, cacheCreate);
        }

        // Parse content blocks
        if (root.TryGetProperty("content", out var contentArray))
        {
            var textParts = new List<string>();
            var thinkingParts = new List<string>();

            foreach (var block in contentArray.EnumerateArray())
            {
                // Use TryGetProperty for safety — malformed blocks shouldn't crash parsing
                if (!block.TryGetProperty("type", out var typeEl)) continue;
                string type = typeEl.GetString() ?? "";

                switch (type)
                {
                    case "text":
                        if (block.TryGetProperty("text", out var textEl))
                            textParts.Add(textEl.GetString() ?? "");
                        break;

                    case "tool_use":
                        if (block.TryGetProperty("id", out var idEl) &&
                            block.TryGetProperty("name", out var nameEl) &&
                            block.TryGetProperty("input", out var inputEl))
                        {
                            result.ToolCalls.Add(new NativeToolCall
                            {
                                Id = idEl.GetString() ?? "",
                                Name = nameEl.GetString() ?? "",
                                Input = inputEl.Clone(),
                            });
                        }
                        break;

                    case "thinking":
                        if (block.TryGetProperty("thinking", out var thinking))
                            thinkingParts.Add(thinking.GetString() ?? "");
                        break;
                }
            }

            result.TextContent = textParts.Count > 0 ? string.Join("\n", textParts) : null;
            result.ThinkingContent = thinkingParts.Count > 0 ? string.Join("\n", thinkingParts) : null;
        }

        return result;
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
