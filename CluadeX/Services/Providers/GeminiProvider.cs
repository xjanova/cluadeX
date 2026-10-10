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

    private static Dictionary<string, object> BuildGenerationConfig(string model, AppSettings settings)
    {
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
        return generationConfig;
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

        var request = new Dictionary<string, object>
        {
            ["contents"] = contents,
            ["generationConfig"] = BuildGenerationConfig(model, settings),
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

    // ═══════════════════════════════════════════
    // Native tool use — function calling
    // Gemini 3 binds its reasoning to the call through a thoughtSignature on the first functionCall part
    // of each step; a follow-up that drops it is a 400. So the model's parts are kept raw and echoed back
    // exactly; responses go in the next user turn as functionResponse parts (calls first, then results).
    // ═══════════════════════════════════════════

    public override bool SupportsNativeToolUse => NativeToolsUsable;

    /// <summary>Models whose API refused tool images this session (they count as blind from then on).</summary>
    private readonly HashSet<string> _imagesRefused = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gemini models are multimodal; one that refused images this session is not.</summary>
    public override bool SupportsToolResultImages(string modelId)
    {
        string m = string.IsNullOrWhiteSpace(modelId) ? ModelOf(GetConfig()) : modelId.Trim();
        if (m.StartsWith("models/")) m = m["models/".Length..];
        lock (_imagesRefused) return !_imagesRefused.Contains(m);
    }

    /// <summary>Does a 400 body talk about the image (inline_data / inlineData / mime type / image)?</summary>
    internal static bool NamesImage(string? error) =>
        !string.IsNullOrEmpty(error)
        && (error.Contains("image", StringComparison.OrdinalIgnoreCase)
            || error.Contains("inline_data", StringComparison.OrdinalIgnoreCase)
            || error.Contains("inlineData", StringComparison.OrdinalIgnoreCase)
            || error.Contains("mime", StringComparison.OrdinalIgnoreCase));

    /// <summary>Gemini's prompt-overflow 400 ("The input token count (N) exceeds the maximum number of tokens allowed").</summary>
    internal static bool IsTokenOverflow(string? error) =>
        !string.IsNullOrEmpty(error)
        && (error.Contains("token count", StringComparison.OrdinalIgnoreCase)
            || error.Contains("maximum number of tokens", StringComparison.OrdinalIgnoreCase));

    /// <summary>Prefix for call ids CluadeX makes up when the model sent none (older models).</summary>
    private const string SyntheticCallPrefix = "gemini_call_";
    /// <summary>Documented bypass for function calls the client injected itself (no model signature).</summary>
    private const string SkipSignature = "skip_thought_signature_validator";

    /// <summary>generateContent contents for a native tool conversation (public for the contract harness).</summary>
    public static List<object> BuildToolContents(List<NativeMessage> messages)
    {
        // tool_use id → name, for functionResponse parts (the API wants the call's exact name back)
        var callNames = messages.SelectMany(m => m.Content).Where(b => b.Type == "tool_use" && b.Id != null)
            .GroupBy(b => b.Id!).ToDictionary(g => g.Key, g => g.First().Name ?? "");
        var contents = new List<object>();
        string? lastRole = null;

        void Add(string role, List<object> parts)
        {
            if (parts.Count == 0) return;
            if (role == lastRole && contents[^1] is Dictionary<string, object> prev && prev["parts"] is List<object> prevParts)
                prevParts.AddRange(parts);   // two turns of the same role in a row: merge
            else
                contents.Add(new Dictionary<string, object> { ["role"] = role, ["parts"] = parts });
            lastRole = role;
        }

        foreach (var msg in messages)
        {
            var parts = new List<object>();
            if (msg.Role == "assistant")
            {
                var raw = msg.Content.FirstOrDefault(b => b.Type == "provider_item" && b.Provider == "gemini" && !string.IsNullOrEmpty(b.Data));
                if (raw != null)
                {
                    // The model's own parts, untouched — thoughtSignature included.
                    foreach (var part in JsonDocument.Parse(raw.Data!).RootElement.EnumerateArray())
                        parts.Add(part.Clone());
                }
                else
                {
                    // A turn the agent loop wrote itself: no model signature exists, so injected calls carry
                    // the documented bypass value on the first functionCall.
                    bool first = true;
                    foreach (var b in msg.Content)
                    {
                        if (b.Type == "text" && !string.IsNullOrEmpty(b.Text))
                            parts.Add(new Dictionary<string, object> { ["text"] = b.Text! });
                        else if (b.Type == "tool_use")
                        {
                            var call = new Dictionary<string, object> { ["name"] = b.Name ?? "", ["args"] = b.Input ?? EmptyObjectElement() };
                            if (b.Id != null && !b.Id.StartsWith(SyntheticCallPrefix)) call["id"] = b.Id;
                            var part = new Dictionary<string, object> { ["functionCall"] = call };
                            if (first) { part["thoughtSignature"] = SkipSignature; first = false; }
                            parts.Add(part);
                        }
                    }
                }
                Add("model", parts);
            }
            else
            {
                // Every functionResponse first, then any text the loop added.
                foreach (var b in msg.Content.Where(b => b.Type == "tool_result"))
                {
                    string id = b.ToolUseId ?? "";
                    var fr = new Dictionary<string, object>
                    {
                        ["name"] = callNames.TryGetValue(id, out var n) ? n : "",
                        ["response"] = new Dictionary<string, object> { [b.IsError == true ? "error" : "result"] = b.Content ?? "" },
                    };
                    if (id.Length > 0 && !id.StartsWith(SyntheticCallPrefix)) fr["id"] = id;
                    parts.Add(new Dictionary<string, object> { ["functionResponse"] = fr });
                    // The tool's images ride right behind its functionResponse, as inlineData parts of the same turn.
                    if (b.Images is { Count: > 0 } imgs)
                        foreach (var img in imgs)
                            parts.Add(new Dictionary<string, object>
                            {
                                ["inlineData"] = new Dictionary<string, object> { ["mimeType"] = img.MediaType, ["data"] = img.Base64 },
                            });
                }
                foreach (var b in msg.Content.Where(b => b.Type == "text" && !string.IsNullOrEmpty(b.Text)))
                    parts.Add(new Dictionary<string, object> { ["text"] = b.Text! });
                Add("user", parts);
            }
        }
        return contents;
    }

    /// <summary>The request body for one native tool turn (public for the contract harness).</summary>
    public Dictionary<string, object> BuildToolRequest(List<NativeMessage> messages, string systemPrompt,
        List<ToolSchema> tools, string? toolChoice)
    {
        string model = ModelOf(GetConfig());
        var req = new Dictionary<string, object>
        {
            ["contents"] = BuildToolContents(messages),
            ["generationConfig"] = BuildGenerationConfig(model, _settingsService.Settings),
        };
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            req["systemInstruction"] = new Dictionary<string, object>
            {
                ["parts"] = new List<object> { new Dictionary<string, object> { ["text"] = systemPrompt } },
            };
        if (tools.Count > 0)
        {
            req["tools"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    // parametersJsonSchema takes plain JSON Schema (additionalProperties etc.), unlike the
                    // OpenAPI-subset `parameters`, which lacks keywords MCP tool schemas use.
                    ["functionDeclarations"] = tools.Select(t => (object)new Dictionary<string, object>
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parametersJsonSchema"] = t.InputSchema.ValueKind == JsonValueKind.Object
                            ? t.InputSchema
                            : JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone(),
                    }).ToList(),
                },
            };
            string choice = toolChoice?.Trim() ?? "";
            string lower = choice.ToLowerInvariant();
            var fcc = new Dictionary<string, object>
            {
                ["mode"] = lower switch
                {
                    "" or "auto" => "AUTO",
                    "none" => "NONE",
                    _ => "ANY",          // "required"/"any", or one named tool
                },
            };
            if (lower is not ("" or "auto" or "none" or "required" or "any"))
                fcc["allowedFunctionNames"] = new[] { choice };
            req["toolConfig"] = new Dictionary<string, object> { ["functionCallingConfig"] = fcc };
        }
        return req;
    }

    public override async Task<NativeToolResponse> ChatWithToolsAsync(
        List<NativeMessage> messages,
        string systemPrompt,
        List<ToolSchema> tools,
        Action<string>? onTextDelta = null,
        CancellationToken ct = default,
        string? toolChoice = null)
    {
        var config = GetConfig();
        string model = ModelOf(config);
        // A model that can't take the images (or a refused one this session) gets text and the omitted note.
        if (HasToolImages(messages) && !SupportsToolResultImages(model))
            messages = WithoutImages(messages, null);
        var body = JsonSerializer.Serialize(BuildToolRequest(messages, systemPrompt, tools, toolChoice));

        string url = $"{BaseUrlOf(config)}/v1beta/models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-goog-api-key", config.ApiKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            // inlineData beside a functionResponse has never met the live API (no key on the dev machine): if it is
            // refused, retry ONCE as text and treat this model as blind for the rest of the session. Only a 400 that
            // names the image counts — an overflow, a thought-signature or a schema error is not about the picture
            // and must not blind the model for the session.
            // (An overflow that happens to mention image tokens is still an overflow: the loop drops images first.)
            if ((int)response.StatusCode == 400 && HasToolImages(messages) && NamesImage(error) && !IsTokenOverflow(error))
            {
                lock (_imagesRefused) _imagesRefused.Add(model);
                string why = ShortApiMessage(error, config.ApiKey);
                var retried = await ChatWithToolsAsync(WithoutImages(messages, why),
                    systemPrompt, tools, onTextDelta, ct, toolChoice);
                retried.ImagesRefused = true;
                retried.ImagesRefusedReason = why;
                return retried;
            }
            string explained = DescribeHttpError("Gemini", (int)response.StatusCode, error, config.ApiKey, model, KeysUrl, BillingUrl);
            // Gemini words a prompt overflow as "The input token count (N) exceeds the maximum number of tokens
            // allowed (M)" — no "context" / "too long", so it used to turn native tools off below instead of
            // reaching the agent loop's overflow path (which drops tool images first, then compacts).
            if ((int)response.StatusCode == 400 && IsTokenOverflow(error))
                throw new HttpRequestException("Prompt too long for the model's context window. " + explained,
                    null, response.StatusCode);
            if (DisableNativeToolsIfRejected((int)response.StatusCode, explained) is { } fallback) return fallback;
            throw new HttpRequestException(explained, null, response.StatusCode);
        }

        var result = await ParseToolStreamAsync(response.Content, onTextDelta, ct);
        if (_costTracker != null && (result.InputTokens > 0 || result.OutputTokens > 0))
            _costTracker.RecordUsage(model, result.InputTokens, result.OutputTokens, 0, 0);
        ConfirmNativeTools();
        return result;
    }

    /// <summary>Parse a streamed function-calling response. Every non-thought part is kept raw, in order,
    /// for the verbatim echo; thought parts become the thinking text. Public for the contract harness.</summary>
    public static async Task<NativeToolResponse> ParseToolStreamAsync(HttpContent content, Action<string>? onTextDelta, CancellationToken ct)
    {
        using var stream = await content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var result = new NativeToolResponse();
        var echo = new List<JsonElement>();
        var text = new StringBuilder();
        var thinking = new StringBuilder();
        string? finish = null, block = null;
        JsonElement? usage = null;
        int synthetic = 0;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;
            onTextDelta?.Invoke(string.Empty);   // keep-alive for the loop's idle timeout (see IAiProvider)
            if (!line.StartsWith("data: ")) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line["data: ".Length..]); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("usageMetadata", out var um)) usage = um.Clone();
                if (root.TryGetProperty("promptFeedback", out var pf) && pf.TryGetProperty("blockReason", out var br))
                    block = br.GetString();
                if (!root.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0) continue;
                var cand = cands[0];
                if (cand.TryGetProperty("finishReason", out var fr)) finish = fr.GetString();
                if (!cand.TryGetProperty("content", out var c) || !c.TryGetProperty("parts", out var parts)
                    || parts.ValueKind != JsonValueKind.Array) continue;

                foreach (var part in parts.EnumerateArray())
                {
                    bool thought = part.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True;
                    if (thought)
                    {
                        if (part.TryGetProperty("text", out var tt)) thinking.Append(tt.GetString());
                        continue;   // summaries aren't sent back
                    }
                    echo.Add(part.Clone());   // incl. empty-text parts that only carry a thoughtSignature
                    if (part.TryGetProperty("text", out var tp) && tp.GetString() is { Length: > 0 } s)
                    {
                        text.Append(s);
                        onTextDelta?.Invoke(s);
                    }
                    else if (part.TryGetProperty("functionCall", out var fc))
                    {
                        string name = fc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        string id = fc.TryGetProperty("id", out var i) && i.GetString() is { Length: > 0 } gid
                            ? gid : SyntheticCallPrefix + synthetic++;
                        JsonElement args = fc.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object
                            ? a.Clone() : EmptyObjectElement();
                        result.ToolCalls.Add(new NativeToolCall { Id = id, Name = name, Input = args });
                    }
                }
            }
        }

        if (echo.Count > 0)
            result.Blocks.Add(new ContentBlock { Type = "provider_item", Provider = "gemini", Data = JsonSerializer.Serialize(echo) });
        if (text.Length > 0)
            result.Blocks.Add(new ContentBlock { Type = "text", Text = text.ToString() });
        foreach (var call in result.ToolCalls)
            result.Blocks.Add(new ContentBlock { Type = "tool_use", Id = call.Id, Name = call.Name, Input = call.Input });

        if (usage is { } u)
        {
            int Get(string name) => u.TryGetProperty(name, out var v) && v.TryGetInt32(out var x) ? x : 0;
            int cached = Get("cachedContentTokenCount");
            result.InputTokens = Math.Max(0, Get("promptTokenCount") - cached);
            result.OutputTokens = Get("candidatesTokenCount") + Get("thoughtsTokenCount");
        }
        result.TextContent = text.Length > 0 ? text.ToString() : null;
        result.ThinkingContent = thinking.Length > 0 ? thinking.ToString() : null;

        string? why = block ?? finish;
        if (result.ToolCalls.Count == 0 && text.Length == 0
            && why is "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII")
        {
            result.TextContent = string.Format(LocalizedResources.Instance["api.err.blocked"], "Gemini", why);
            result.StopReason = "refusal";
            return result;
        }
        result.StopReason = result.ToolCalls.Count > 0 ? "tool_use" : finish == "MAX_TOKENS" ? "max_tokens" : "end_turn";
        return result;
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
