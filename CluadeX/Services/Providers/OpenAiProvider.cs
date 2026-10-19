using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

public partial class OpenAiProvider : ApiProviderBase, IModelLister
{
    private readonly CostTrackingService? _costTracker;

    public override string ProviderId => "OpenAI";
    public override string DisplayName => "OpenAI";

    /// <summary>Used when no model is picked: GPT-6.1 Sol, "for complex tasks where time and cost matter". Same 1.05M
    /// window and 128K output as Astra at a fifth of the price ($2 / $10 vs $10 / $50) — an agent loop sends the whole
    /// conversation every step, so that ratio is the bill. Astra stays one pick away, and the dispatcher already
    /// escalates a fix two workers could not make to it (DispatchPolicy.StrongerModel).</summary>
    public const string DefaultModel = "gpt-6.1-sol";

    // Newest first (October 2026, developers.openai.com/api/docs/models). Only the offline starting list: Test
    // Connection replaces it with the models this key can actually call (GET /models). Shut down or retiring:
    // o-series, gpt-4, gpt-4-turbo, gpt-3.5-turbo, the dated gpt-5 snapshots (2026-12-11) — deliberately not listed;
    // gpt-4.1 / gpt-4o stay until their 2027-01-14 shutdown (replacement: gpt-6-luna).
    public static readonly string[] KnownModels =
    [
        "gpt-6-astra",     // most capable, hardest end-to-end work
        "gpt-6.1-sol",     // high performance where time and cost matter (the default)
        "gpt-6-sol",       // the previous Sol
        "gpt-6-luna",      // most efficient, high-volume
        "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna",
        "gpt-5.5", "gpt-5.4", "gpt-5.4-mini",
        "gpt-4.1", "gpt-4o", "gpt-4o-mini",
    ];

    private const string KeysUrl = "platform.openai.com/api-keys";
    private const string BillingUrl = "platform.openai.com/settings/organization/billing";
    // Reasoning tokens count against max_completion_tokens; a local-model-sized cap leaves nothing for the answer.
    private const int ReasoningMaxTokensFloor = 32000;

    /// <summary>Highest reasoning_effort the model accepts, or null for a non-reasoning model (gpt-4.1,
    /// gpt-4o, unknown names). Reasoning models take max_completion_tokens and reject temperature/top_p.</summary>
    public static string? MaxEffort(string? model)
    {
        string m = model?.Trim().ToLowerInvariant() ?? "";
        int slash = m.LastIndexOf('/');                       // proxies: "openai/gpt-6-sol"
        if (slash >= 0) m = m[(slash + 1)..];
        if (m.StartsWith("gpt-6") || m.StartsWith("gpt-5.6")) return "max";
        if (Regex.IsMatch(m, @"^gpt-5\.[2-5]")) return "xhigh";
        if (m.StartsWith("gpt-5") || Regex.IsMatch(m, @"^o[134](-|$)")) return "high";
        return null;
    }

    /// <summary>The model's published output ceiling (reasoning tokens included), or null when this build doesn't know
    /// it and the setting goes out as it is. GPT-6 Astra / 6.1 Sol / 6 Sol / Luna: 128K — a larger max_output_tokens
    /// is refused rather than trimmed.</summary>
    public static int? MaxOutputTokens(string? model)
    {
        string m = model?.Trim().ToLowerInvariant() ?? "";
        int slash = m.LastIndexOf('/');
        if (slash >= 0) m = m[(slash + 1)..];
        return m.StartsWith("gpt-6") ? 128_000 : null;
    }

    /// <summary>The output budget a request asks for: the setting, raised to the reasoning floor for a reasoning
    /// model, then held at the model's ceiling.</summary>
    public static int OutputBudget(string model, int setting)
    {
        int want = MaxEffort(model) != null ? Math.Max(setting, ReasoningMaxTokensFloor) : setting;
        return MaxOutputTokens(model) is int cap ? Math.Min(want, cap) : want;
    }

    /// <summary>The user's effort pick as this model accepts it (rounded down), or null to omit the field.</summary>
    public static string? EffortFor(string model, string? pick, bool thinkingToggle)
    {
        string? max = MaxEffort(model);
        if (max == null) return null;
        string p = EffortLevels.Normalize(pick);
        if (p == EffortLevels.Auto) return thinkingToggle ? Clamp("high", max) : null;
        return Clamp(p, max);

        static string Clamp(string p, string max) =>
            Array.IndexOf(EffortLevels.All, p) > Array.IndexOf(EffortLevels.All, max) ? max : p;
    }

    public OpenAiProvider(SettingsService settingsService, CostTrackingService? costTracker = null)
        : base(settingsService)
    {
        _costTracker = costTracker;
    }

    private static string BaseUrlOf(ProviderConfig config) =>
        string.IsNullOrWhiteSpace(config.BaseUrl) ? "https://api.openai.com/v1" : config.BaseUrl.TrimEnd('/');

    private static string ModelOf(ProviderConfig config) => Agents.SubAgentModel.Pick(
        string.IsNullOrWhiteSpace(config.EffectiveModelId) ? DefaultModel : config.EffectiveModelId.Trim());

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
            SetStatus(success ? $"OpenAI ready ({ModelOf(config)})" : msg);
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
        string model = ModelOf(config);
        var settings = _settingsService.Settings;

        var messages = BuildChatMessages(history, userMessage, systemPrompt);

        var requestObj = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = true,
            // Usage arrives in one last chunk only when asked for — that's what cost tracking reads.
            ["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true },
        };

        if (MaxEffort(model) != null)
        {
            // Reasoning model: no temperature/top_p, max_completion_tokens (includes reasoning tokens).
            requestObj["max_completion_tokens"] = OutputBudget(model, settings.MaxTokens);
            if (EffortFor(model, Models.EffortLevels.Effective(settings.ReasoningEffort), settings.ExtendedThinkingEnabled) is { } effort)
                requestObj["reasoning_effort"] = effort;
        }
        else
        {
            requestObj["temperature"] = (double)settings.Temperature;
            requestObj["max_tokens"] = settings.MaxTokens;
        }
        LiveModelReport.Sent(Models.AiProviderType.OpenAI, model,
            requestObj.TryGetValue("reasoning_effort", out var sentEffort) ? sentEffort as string : null, MaxEffort(model) != null);

        var body = JsonSerializer.Serialize(requestObj);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrlOf(config)}/chat/completions");
        request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        UsageMeter.Report(UsageMeter.FromOpenAi(response, model));   // rate-limit headroom for the usage strip

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            RaiseError(DescribeHttpError("OpenAI", (int)response.StatusCode, error, config.ApiKey, model, KeysUrl, BillingUrl));
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
                var root = doc.RootElement;
                if (root.TryGetProperty("model", out var servedModel) && servedModel.ValueKind == JsonValueKind.String)
                    LiveModelReport.Served(Models.AiProviderType.OpenAI, servedModel.GetString());
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("delta", out var delta)
                    && delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    content = c.GetString();

                if (_costTracker != null && root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    int inTok = usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var a) ? a : 0;
                    int outTok = usage.TryGetProperty("completion_tokens", out var ot) && ot.TryGetInt32(out var b) ? b : 0;
                    int cached = usage.TryGetProperty("prompt_tokens_details", out var pd)
                                 && pd.TryGetProperty("cached_tokens", out var cd) && cd.TryGetInt32(out var cc) ? cc : 0;
                    if (inTok > 0 || outTok > 0)
                        _costTracker.RecordUsage(model, inTok - cached, outTok, cached, 0);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip malformed chunks */ }

            if (!string.IsNullOrEmpty(content))
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
    // Native tool use — Responses API
    // Chat Completions can't combine tools with reasoning on GPT-5.4+ (and GPT-6 Astra / 6.1 Sol take no
    // tools there at all), so the agent loop talks to POST /v1/responses. Stateless: store:false, and the
    // encrypted reasoning items come back (include) so they can be echoed on the next tool turn.
    // ═══════════════════════════════════════════

    public override bool SupportsNativeToolUse => NativeToolsUsable;

    /// <summary>How tool images go out on the Responses API.</summary>
    public enum ResponsesImageForm
    {
        /// <summary>function_call_output.output = [input_text, input_image…] (documented; the default).</summary>
        OutputArray,
        /// <summary>output stays text; one following user message carries the input_image items (the fallback when
        /// the API refuses the array form).</summary>
        UserMessage,
        /// <summary>No image is sent; the output says the model could not see it.</summary>
        Omit,
    }

    // Vision-capable families (the Responses API takes input_image for these): gpt-4o / 4.1 / 4.5 / 5 / 6,
    // gpt-4-turbo, o1 (not o1-mini / o1-preview), o3, o4, computer-use-preview — deep-research models included.
    // Audio / realtime / transcribe / tts and the search-preview models are text-only, as are o3-mini and
    // gpt-4-turbo-preview (the older text-only turbo).
    private static readonly Regex VisionModelId = new(
        @"^(gpt-4o|gpt-4\.1|gpt-4\.5|gpt-4-turbo|gpt-5|gpt-6|o1(?!-mini|-preview)|o3|o4|chatgpt-4o|computer-use-preview)",
        RegexOptions.IgnoreCase);
    private static readonly Regex NonVisionModelId = new(
        @"(audio|realtime|transcribe|tts|-search-|search-preview|^o3-mini|^gpt-4-turbo-preview)", RegexOptions.IgnoreCase);

    /// <summary>Images this provider's API refused for a model this session — it is blind from then on.</summary>
    private readonly HashSet<string> _imagesRefused = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The API refused the output-array form once: use the user-message form from then on.</summary>
    private bool _outputArrayRefused;

    public override bool SupportsToolResultImages(string modelId)
    {
        string m = string.IsNullOrWhiteSpace(modelId) ? ModelOf(GetConfig()) : modelId.Trim();
        lock (_imagesRefused) if (_imagesRefused.Contains(m)) return false;
        return VisionModelId.IsMatch(m) && !NonVisionModelId.IsMatch(m);
    }

    /// <summary>Responses API input items from the provider-neutral conversation.</summary>
    public static List<object> BuildResponsesInput(List<NativeMessage> messages, ResponsesImageForm imageForm = ResponsesImageForm.OutputArray)
    {
        var input = new List<object>();
        foreach (var msg in messages)
        {
            if (msg.Role == "assistant")
            {
                // A turn the model produced: its output items go back untouched and in order — reasoning
                // (encrypted state under store:false), message (with its phase), function_call (with id).
                // The text/tool_use blocks in the same message are views of those items, so skip them.
                var raw = msg.Content.Where(b => b.Type == "provider_item" && b.Provider == "openai" && !string.IsNullOrEmpty(b.Data)).ToList();
                if (raw.Count > 0)
                {
                    foreach (var b in raw) input.Add(JsonDocument.Parse(b.Data!).RootElement.Clone());
                    continue;
                }
                // A turn the agent loop wrote itself (recovery prompts, synthetic calls): rebuild it.
                foreach (var b in msg.Content)
                {
                    switch (b.Type)
                    {
                        case "text" when !string.IsNullOrEmpty(b.Text):
                            input.Add(new Dictionary<string, object> { ["role"] = "assistant", ["content"] = b.Text! });
                            break;
                        case "tool_use":
                            input.Add(new Dictionary<string, object>
                            {
                                ["type"] = "function_call",
                                ["call_id"] = b.Id ?? "",
                                ["name"] = b.Name ?? "",
                                ["arguments"] = b.Input?.GetRawText() ?? "{}",
                            });
                            break;
                    }
                }
            }
            else
            {
                // Tool outputs first (they answer the calls right before them), then any text the loop added.
                var followImages = new List<ToolImage>();
                foreach (var b in msg.Content.Where(b => b.Type == "tool_result"))
                {
                    string outText = (b.IsError == true ? "ERROR: " : "") + (b.Content ?? "");
                    object output = outText;
                    if (b.Images is { Count: > 0 } imgs)
                    {
                        switch (imageForm)
                        {
                            case ResponsesImageForm.OutputArray:
                                var items = new List<object>();
                                if (outText.Length > 0) items.Add(new Dictionary<string, object> { ["type"] = "input_text", ["text"] = outText });
                                items.AddRange(imgs.Select(InputImage));
                                output = items;
                                break;
                            case ResponsesImageForm.UserMessage:
                                followImages.AddRange(imgs);
                                break;
                            default:
                                output = Vision.ToolImageWire.AppendNote(outText, Vision.ToolImageWire.OmittedForBlindModel(imgs, null, "image"));
                                break;
                        }
                    }
                    input.Add(new Dictionary<string, object>
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = b.ToolUseId ?? "",
                        ["output"] = output,
                    });
                }
                string text = string.Join("\n\n", msg.Content.Where(b => b.Type == "text" && !string.IsNullOrEmpty(b.Text)).Select(b => b.Text));
                // Images the user attached to this message: input_image parts in front of its text.
                var userImages = msg.Content.Where(b => b.Type == "text" && b.Images is { Count: > 0 }).SelectMany(b => b.Images!).ToList();
                if (followImages.Count == 0 && userImages.Count > 0)
                {
                    var parts = new List<object>(userImages.Select(InputImage));
                    if (text.Length > 0) parts.Add(new Dictionary<string, object> { ["type"] = "input_text", ["text"] = text });
                    input.Add(new Dictionary<string, object> { ["role"] = "user", ["content"] = parts });
                }
                else if (followImages.Count > 0)
                {
                    // One user message: what the images are, the images, then the loop's own text.
                    var parts = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "input_text",
                            ["text"] = followImages.Count == 1
                                ? "[The image returned by the tool call above]"
                                : $"[The {followImages.Count} images returned by the tool calls above, in order]",
                        },
                    };
                    parts.AddRange(followImages.Select(InputImage));
                    parts.AddRange(userImages.Select(InputImage));
                    if (text.Length > 0) parts.Add(new Dictionary<string, object> { ["type"] = "input_text", ["text"] = text });
                    input.Add(new Dictionary<string, object> { ["role"] = "user", ["content"] = parts });
                }
                else if (text.Length > 0)
                    input.Add(new Dictionary<string, object> { ["role"] = "user", ["content"] = text });
            }
        }
        return input;
    }

    private static object InputImage(ToolImage img) => new Dictionary<string, object>
    {
        ["type"] = "input_image",
        ["image_url"] = Vision.ToolImageWire.DataUrl(img),
        ["detail"] = "auto",
    };

    /// <summary>The request body for one native tool turn (public for the contract harness).</summary>
    public Dictionary<string, object> BuildResponsesRequest(List<NativeMessage> messages, string systemPrompt,
        List<ToolSchema> tools, string? toolChoice, ResponsesImageForm imageForm = ResponsesImageForm.OutputArray)
    {
        string model = ModelOf(GetConfig());
        var settings = _settingsService.Settings;
        bool reasoning = MaxEffort(model) != null;
        var req = new Dictionary<string, object>
        {
            ["model"] = model,
            ["input"] = BuildResponsesInput(messages, imageForm),
            ["stream"] = true,
            ["store"] = false,
            ["max_output_tokens"] = OutputBudget(model, settings.MaxTokens),
        };
        if (!string.IsNullOrWhiteSpace(systemPrompt)) req["instructions"] = systemPrompt;
        if (tools.Count > 0)
        {
            req["tools"] = tools.Select(t => (object)new Dictionary<string, object>
            {
                ["type"] = "function",
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = t.InputSchema.ValueKind == JsonValueKind.Object
                    ? t.InputSchema
                    : JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone(),
                ["strict"] = false,   // our schemas aren't all strict-mode compatible (optional fields, MCP tools)
            }).ToList();
            req["tool_choice"] = (toolChoice?.Trim().ToLowerInvariant()) switch
            {
                null or "" or "auto" => "auto",
                "none" => "none",
                "required" or "any" => "required",
                _ => new Dictionary<string, object> { ["type"] = "function", ["name"] = toolChoice! },
            };
        }
        if (reasoning)
        {
            var r = new Dictionary<string, object>();
            if (EffortFor(model, Models.EffortLevels.Effective(settings.ReasoningEffort), settings.ExtendedThinkingEnabled) is { } e) r["effort"] = e;
            if (settings.ExtendedThinkingEnabled) r["summary"] = "auto";
            if (r.Count > 0) req["reasoning"] = r;
            req["include"] = new[] { "reasoning.encrypted_content" };
        }
        else
        {
            req["temperature"] = (double)settings.Temperature;
        }
        LiveModelReport.Sent(Models.AiProviderType.OpenAI, model,
            req.TryGetValue("reasoning", out var ro) && ro is Dictionary<string, object> rd && rd.TryGetValue("effort", out var re) ? re as string : null,
            reasoning);
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

        // Tool images: the documented output array first; if the API refuses it, the same images in a following
        // user message (remembered for the session); if it refuses images altogether, text only — the model is
        // told it could not see them, and this model counts as blind from then on.
        bool hasImages = HasToolImages(messages);
        var form = !hasImages ? ResponsesImageForm.OutputArray
            : !SupportsToolResultImages(model) ? ResponsesImageForm.Omit
            : _outputArrayRefused ? ResponsesImageForm.UserMessage
            : ResponsesImageForm.OutputArray;

        HttpResponseMessage response;
        string? refusedWhy = null;
        while (true)
        {
            var body = JsonSerializer.Serialize(BuildResponsesRequest(messages, systemPrompt, tools, toolChoice, form));
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrlOf(config)}/responses");
            request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            UsageMeter.Report(UsageMeter.FromOpenAi(response, model));
            if (response.IsSuccessStatusCode) break;

            var error = await response.Content.ReadAsStringAsync(ct);
            int status = (int)response.StatusCode;
            response.Dispose();
            // The array form refused: only a 400 that is about the output or the image. A context_length_exceeded
            // (or any other 400) goes on to the agent loop, whose overflow path drops images first — it must not
            // switch the whole session to the fallback form.
            if (status == 400 && hasImages && form == ResponsesImageForm.OutputArray
                && (error.Contains("output", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("image", StringComparison.OrdinalIgnoreCase)))
            {
                _outputArrayRefused = true;
                form = ResponsesImageForm.UserMessage;
                continue;
            }
            if (status == 400 && hasImages && form == ResponsesImageForm.UserMessage
                && error.Contains("image", StringComparison.OrdinalIgnoreCase))
            {
                lock (_imagesRefused) _imagesRefused.Add(model);
                refusedWhy = ShortApiMessage(error, config.ApiKey);
                messages = WithoutImages(messages, refusedWhy);
                hasImages = false;
                form = ResponsesImageForm.OutputArray;
                continue;
            }
            string explained = DescribeHttpError("OpenAI", status, error, config.ApiKey, model, KeysUrl, BillingUrl);
            if (DisableNativeToolsIfRejected(status, explained) is { } fallback) return fallback;
            throw new HttpRequestException(explained, null, (System.Net.HttpStatusCode)status);
        }

        using var ownedResponse = response;
        var result = await ParseResponsesStreamAsync(response.Content, onTextDelta, ct);
        // Cached input is billed at the cached rate (and counts toward GPT-6's 272K long-context line) — it was dropped
        // here, so a cache-heavy agent step read as nearly free.
        if (_costTracker != null && (result.InputTokens > 0 || result.OutputTokens > 0 || result.CacheReadTokens > 0))
            _costTracker.RecordUsage(model, result.InputTokens, result.OutputTokens, result.CacheReadTokens, 0);
        ConfirmNativeTools();
        if (refusedWhy != null)
        {
            // Answered from the text-only retry: the loop drops the refused images from its conversation.
            result.ImagesRefused = true;
            result.ImagesRefusedReason = refusedWhy;
        }
        return result;
    }

    /// <summary>Parse a Responses API SSE stream into a NativeToolResponse. Text streams live through
    /// <paramref name="onTextDelta"/>; the authoritative content is each response.output_item.done item.
    /// Public so the contract harness can feed it recorded streams.</summary>
    public static async Task<NativeToolResponse> ParseResponsesStreamAsync(HttpContent content, Action<string>? onTextDelta, CancellationToken ct)
    {
        using var stream = await content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var result = new NativeToolResponse();
        var items = new SortedDictionary<int, JsonElement>();   // output_index → finished item
        var summary = new StringBuilder();
        string? stop = null;

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;
            onTextDelta?.Invoke(string.Empty);   // keep-alive for the loop's idle timeout (see IAiProvider)
            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                switch (type)
                {
                    case "response.output_text.delta":
                        if (root.TryGetProperty("delta", out var d) && d.GetString() is { Length: > 0 } s)
                            onTextDelta?.Invoke(s);
                        break;
                    case "response.reasoning_summary_text.delta":
                        if (root.TryGetProperty("delta", out var rd)) summary.Append(rd.GetString());
                        break;
                    case "response.output_item.done":
                        if (root.TryGetProperty("item", out var item) && root.TryGetProperty("output_index", out var oi)
                            && oi.TryGetInt32(out int idx))
                            items[idx] = item.Clone();
                        break;
                    case "response.completed":
                    case "response.incomplete":
                        if (root.TryGetProperty("response", out var resp))
                        {
                            if (resp.TryGetProperty("model", out var servedModel) && servedModel.ValueKind == JsonValueKind.String)
                                LiveModelReport.Served(Models.AiProviderType.OpenAI, servedModel.GetString());
                            if (resp.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                            {
                                int cached = u.TryGetProperty("input_tokens_details", out var itd)
                                             && itd.TryGetProperty("cached_tokens", out var ctk) && ctk.TryGetInt32(out var cv) ? cv : 0;
                                result.InputTokens = (u.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var iv) ? iv : 0) - cached;
                                result.CacheReadTokens = cached;
                                result.OutputTokens = u.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var ov) ? ov : 0;
                            }
                            if (type == "response.incomplete")
                                stop = resp.TryGetProperty("incomplete_details", out var det) && det.TryGetProperty("reason", out var why)
                                       && why.GetString() == "max_output_tokens" ? "max_tokens" : "end_turn";
                        }
                        break;
                    case "response.failed":
                    case "error":
                        string msg = (root.TryGetProperty("response", out var fr) && fr.TryGetProperty("error", out var fe) ? fe
                                      : root.TryGetProperty("error", out var ee) ? ee : root) is { ValueKind: JsonValueKind.Object } eo
                                     && eo.TryGetProperty("message", out var m) ? m.GetString() ?? "unknown error" : "unknown error";
                        throw new HttpRequestException($"OpenAI streaming error: {msg}");
                }
            }
        }

        var text = new StringBuilder();
        foreach (var item in items.Values)
        {
            // Every output item is echoed verbatim on the next turn (OpenAI: pass back everything since the
            // last user message untouched); the text / tool_use blocks below are views for the agent loop.
            result.Blocks.Add(new ContentBlock { Type = "provider_item", Provider = "openai", Data = item.GetRawText() });
            string kind = item.TryGetProperty("type", out var k) ? k.GetString() ?? "" : "";
            switch (kind)
            {
                case "message":
                    if (item.TryGetProperty("content", out var parts) && parts.ValueKind == JsonValueKind.Array)
                        foreach (var p in parts.EnumerateArray())
                            if (p.TryGetProperty("text", out var pt) && pt.GetString() is { Length: > 0 } s)
                            {
                                text.Append(s);
                                result.Blocks.Add(new ContentBlock { Type = "text", Text = s });
                            }
                    break;
                case "function_call":
                    string callId = item.TryGetProperty("call_id", out var cid) ? cid.GetString() ?? "" : "";
                    string name = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                    string args = item.TryGetProperty("arguments", out var ar) ? ar.GetString() ?? "" : "";
                    JsonElement input;
                    try { input = string.IsNullOrWhiteSpace(args) ? EmptyObjectElement() : JsonDocument.Parse(args).RootElement.Clone(); }
                    catch (JsonException) { input = EmptyObjectElement(); }
                    if (input.ValueKind != JsonValueKind.Object) input = EmptyObjectElement();
                    result.ToolCalls.Add(new NativeToolCall { Id = callId, Name = name, Input = input });
                    result.Blocks.Add(new ContentBlock { Type = "tool_use", Id = callId, Name = name, Input = input });
                    break;
            }
        }

        result.TextContent = text.Length > 0 ? text.ToString() : null;
        result.ThinkingContent = summary.Length > 0 ? summary.ToString() : null;
        result.StopReason = stop ?? (result.ToolCalls.Count > 0 ? "tool_use" : "end_turn");
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
            // The key works; say so even when the picked model is missing, but say that too.
            return ids.Contains(model, StringComparer.OrdinalIgnoreCase)
                ? (true, $"Connected to OpenAI ({model})")
                : (true, $"Connected to OpenAI — {string.Format(LocalizedResources.Instance["api.err.modelMissing"], model)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (!string.IsNullOrEmpty(config.ApiKey))
                msg = msg.Replace(config.ApiKey, "[REDACTED]");
            return (false, $"Connection failed: {msg}");
        }
    }

    /// <summary>Chat-capable model IDs this key can call, newest first; null when the listing fails.</summary>
    public async Task<List<string>?> ListModelsAsync(CancellationToken ct = default)
    {
        var config = GetConfig();
        if (string.IsNullOrWhiteSpace(config.ApiKey)) return null;
        try { return (await FetchModelsAsync(config, ct)).Ids; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    // GET /models has no capability field — keep text-chat families, drop audio/image/embedding/etc. gpt-live-1 is a
    // voice model on its own Live API (/v1/live/sessions); gpt-image-2.5-sunburst / -flare are images-only.
    private static readonly Regex ChatModelId = new(@"^(gpt-|o[134](-|$)|chatgpt-)", RegexOptions.IgnoreCase);
    private static readonly Regex NonChatModelId = new(
        @"(tts|transcribe|realtime|audio|image|embedding|moderation|search|instruct|dall-e|whisper|davinci|babbage|codex|^gpt-live)",
        RegexOptions.IgnoreCase);

    /// <summary>True for a text-chat model ID from OpenAI's own /models list.</summary>
    public static bool IsChatModel(string id) => ChatModelId.IsMatch(id) && !NonChatModelId.IsMatch(id);

    private async Task<(List<string>? Ids, string? Error)> FetchModelsAsync(ProviderConfig config, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrlOf(config)}/models");
        request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");
        using var response = await _httpClient.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return (null, DescribeHttpError("OpenAI", (int)response.StatusCode, body, config.ApiKey, ModelOf(config), KeysUrl, BillingUrl));

        var models = new List<(string Id, long Created)>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var m in data.EnumerateArray())
            {
                if (!m.TryGetProperty("id", out var idEl) || idEl.GetString() is not { Length: > 0 } id) continue;
                long created = m.TryGetProperty("created", out var cr) && cr.TryGetInt64(out var c) ? c : 0;
                models.Add((id, created));
            }

        // A custom BaseUrl (proxy / compatible server) may use any naming — only filter OpenAI's own list.
        bool officialApi = string.IsNullOrWhiteSpace(config.BaseUrl) || config.BaseUrl.Contains("api.openai.com");
        return (models
            .Where(x => !officialApi || IsChatModel(x.Id))
            .OrderByDescending(x => x.Created)
            .Select(x => x.Id)
            .ToList(), null);
    }
}
