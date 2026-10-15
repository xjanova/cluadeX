using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

public abstract class ApiProviderBase : IAiProvider
{
    // Shared SocketsHttpHandler for connection pooling & DNS refresh across ALL providers.
    // Sharing the handler is the recommended .NET pattern; each HttpClient is then cheap.
    // A single shared handler avoids socket exhaustion and respects ApiProvider DI lifetimes.
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 8,
        EnableMultipleHttp2Connections = true,
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
        KeepAlivePingDelay = TimeSpan.FromSeconds(30),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    protected readonly SettingsService _settingsService;
    protected readonly HttpClient _httpClient;

    public abstract string ProviderId { get; }
    public abstract string DisplayName { get; }
    public bool IsReady { get; protected set; }
    public bool IsLoading { get; protected set; }
    public string StatusMessage { get; protected set; } = "Not configured";

    public event Action<string>? OnStatusChanged;
    public event Action<bool>? OnLoadingChanged;
    public event Action<string>? OnError;

    protected ApiProviderBase(SettingsService settingsService)
    {
        _settingsService = settingsService;
        // `disposeHandler: false` — the handler is shared and must outlive every client.
        _httpClient = new HttpClient(SharedHandler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) };
    }

    protected ProviderConfig GetConfig()
    {
        if (_settingsService.Settings.ProviderConfigs.TryGetValue(ProviderId, out var config))
            return config;
        return new ProviderConfig();
    }

    protected void SetStatus(string message)
    {
        StatusMessage = message;
        OnStatusChanged?.Invoke(message);
    }

    protected void SetLoading(bool loading)
    {
        IsLoading = loading;
        OnLoadingChanged?.Invoke(loading);
    }

    protected void RaiseError(string message)
    {
        OnError?.Invoke(message);
    }

    // ── Native tool calling safety net ──
    // OpenAI's Responses API and Gemini function calling are driven from documented wire formats that
    // can't be exercised without a key. If the API rejects the very first native tool request, switch
    // this provider to the [ACTION:] text protocol (SupportsNativeToolUse → false, which also rebuilds
    // the system prompt) instead of failing every agent turn.
    private bool _nativeToolsConfirmed, _nativeToolsRejected;

    /// <summary>False once the API rejected a native tool request that had never worked before.</summary>
    protected bool NativeToolsUsable => !_nativeToolsRejected;

    /// <summary>Call after a native tool request succeeds.</summary>
    protected void ConfirmNativeTools() => _nativeToolsConfirmed = true;

    /// <summary>For a failed native tool request: when it looks like "this endpoint/model doesn't take
    /// these tool fields" (400/422 before any success), turn native tools off and return the turn's
    /// error; otherwise null and the caller reports the error normally.</summary>
    protected NativeToolResponse? DisableNativeToolsIfRejected(int status, string explained)
    {
        if (_nativeToolsConfirmed || status is not (400 or 422)) return null;
        // Overflow and quota problems are not about the tool format.
        if (explained.Contains("too long", StringComparison.OrdinalIgnoreCase)
            || explained.Contains("context", StringComparison.OrdinalIgnoreCase)
            || explained.Contains("quota", StringComparison.OrdinalIgnoreCase))
            return null;
        _nativeToolsRejected = true;
        return new NativeToolResponse
        {
            StopReason = "error",
            TextContent = LocalizedResources.Instance["api.err.nativeToolsOff"] + "\n" + explained,
        };
    }

    /// <summary>Turn an HTTP error from a cloud API into what to do about it, keeping the API's own message
    /// (key redacted) after the explanation. Never words a hint with "context" or "too long": the agent
    /// loop reads those in a 400 as prompt overflow and compacts.</summary>
    /// <param name="keysUrl">Where to get a new key.</param>
    /// <param name="billingUrl">Where to add credit / quota.</param>
    protected static string DescribeHttpError(string provider, int status, string body, string? apiKey,
        string model, string keysUrl, string billingUrl)
    {
        if (!string.IsNullOrEmpty(apiKey)) body = body.Replace(apiKey, "[REDACTED]");
        string apiMsg = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            // OpenAI {error:{message}}, Gemini {error:{message,status}}; some proxies send [ {error:…} ]
            var root = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                ? doc.RootElement[0] : doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("message", out var em) && em.GetString() is { Length: > 0 } s)
                apiMsg = s;
        }
        catch (JsonException) { /* not JSON — keep the raw body */ }

        string L(string key) => LocalizedResources.Instance[key];
        bool Has(string word) => apiMsg.Contains(word, StringComparison.OrdinalIgnoreCase);
        string? hint =
            status == 401 || Has("api key not valid") || Has("incorrect api key") || Has("API_KEY_INVALID")
                ? string.Format(L("api.err.badKey"), provider, keysUrl)
            : Has("insufficient_quota") || Has("exceeded your current quota") || Has("billing") || Has("credit")
                ? string.Format(L("api.err.quota"), provider, billingUrl)
            : status == 404 || Has("model_not_found") || (Has("model") && Has("not found"))
                ? string.Format(L("api.err.model"), provider, model)
            : status == 403 ? string.Format(L("api.err.permission"), provider)
            : status == 429 ? string.Format(L("api.err.rateLimit"), provider)
            : status >= 500 ? string.Format(L("api.err.overloaded"), provider)
            : null;

        string head = $"HTTP {status}: {apiMsg}";
        return hint == null ? head : $"{hint}\n({head})";
    }

    // Simple record to avoid reflection on anonymous types
    protected record ChatMsg(string role, string content);

    protected List<object> BuildChatMessages(List<ChatMessage> history, string userMessage, string? systemPrompt,
        string systemRole = "system", string userRole = "user", string assistantRole = "assistant")
    {
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new ChatMsg(systemRole, systemPrompt));

        // Build history ensuring strict user/assistant alternation.
        // ToolAction and CodeExecution are merged into adjacent messages
        // to avoid consecutive same-role messages.
        string? lastRole = !string.IsNullOrWhiteSpace(systemPrompt) ? systemRole : null;

        foreach (var msg in history)
        {
            if (string.IsNullOrWhiteSpace(msg.Content)) continue;

            string role;
            string content;

            switch (msg.Role)
            {
                case MessageRole.User:
                    role = userRole;
                    content = msg.Content;
                    break;
                case MessageRole.Assistant:
                    role = assistantRole;
                    content = msg.Content;
                    break;
                case MessageRole.CodeExecution:
                    // Merge into preceding assistant message, or treat as assistant context
                    role = assistantRole;
                    content = $"[Code Execution Result]\n{msg.Content}";
                    break;
                case MessageRole.ToolAction:
                    // Merge into preceding assistant message
                    role = assistantRole;
                    content = $"[Tool: {msg.ToolName}] {msg.Content}";
                    break;
                case MessageRole.System:
                    // Skip mid-conversation system messages (already handled via systemPrompt)
                    continue;
                default:
                    continue;
            }

            // Merge consecutive same-role messages
            if (role == lastRole && messages.Count > 0)
            {
                var prev = (ChatMsg)messages[^1];
                string prevContent = prev.content;
                messages[^1] = new ChatMsg(role, prevContent + "\n" + content);
            }
            else
            {
                messages.Add(new ChatMsg(role, content));
            }
            lastRole = role;
        }

        // Ensure current user message doesn't create consecutive user messages
        if (lastRole == userRole && messages.Count > 0)
        {
            // Merge with the last user message
            var prev = (ChatMsg)messages[^1];
            messages[^1] = new ChatMsg(userRole, prev.content + "\n" + userMessage);
        }
        else
        {
            messages.Add(new ChatMsg(userRole, userMessage));
        }

        return messages;
    }

    // GetContentFromAnonymous removed — replaced by ChatMsg record (no reflection needed)

    public abstract Task InitializeAsync(CancellationToken ct = default);

    public abstract IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default);

    public abstract Task<string> GenerateAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default);

    public abstract Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default);

    // Phase 5: Native tool use support (default: not supported)
    public virtual bool SupportsNativeToolUse => false;

    public virtual Task<NativeToolResponse> ChatWithToolsAsync(
        List<NativeMessage> messages,
        string systemPrompt,
        List<ToolSchema> tools,
        Action<string>? onTextDelta = null,
        CancellationToken ct = default,
        string? toolChoice = null)
        => Task.FromResult(new NativeToolResponse
        {
            TextContent = "Native tool use not supported by this provider.",
            StopReason = "end_turn",
        });

    /// <summary>Map a provider-neutral toolChoice ("auto"/"none"/"required"/"&lt;toolName&gt;") to the OpenAI
    /// tool_choice wire value. Null/empty/"auto" → "auto" (the model decides).</summary>
    protected static object BuildOpenAiToolChoice(string? toolChoice)
    {
        if (string.IsNullOrWhiteSpace(toolChoice)) return "auto";
        switch (toolChoice.Trim().ToLowerInvariant())
        {
            case "auto": return "auto";
            case "none": return "none";
            case "required":
            case "any": return "required";
            default:
                return new Dictionary<string, object>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object> { ["name"] = toolChoice.Trim() },
                };
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // OpenAI-style native tool-calling helpers (shared by llama-server + Ollama).
    // Translate between our provider-neutral NativeMessage/ToolSchema model and the OpenAI
    // function-calling wire format (tools[] + tool_calls[]), so local models run the SAME structured
    // agent loop as Anthropic instead of the fragile [ACTION:] text parser.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Convert NativeMessages (text / tool_use / tool_result blocks) to OpenAI chat messages.</summary>
    /// <param name="argumentsAsObject">
    /// OpenAI/llama-server expect tool_call arguments as a JSON-encoded STRING; Ollama wants an OBJECT.
    /// </param>
    protected List<object> BuildOpenAiToolMessages(List<NativeMessage> messages, string? systemPrompt, bool argumentsAsObject = false)
    {
        var result = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            result.Add(new Dictionary<string, object> { ["role"] = "system", ["content"] = systemPrompt });

        foreach (var msg in messages)
        {
            if (msg.Role == "assistant")
            {
                var text = new StringBuilder();
                var toolCalls = new List<object>();
                foreach (var block in msg.Content)
                {
                    if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
                        text.Append(block.Text);
                    else if (block.Type == "tool_use")
                    {
                        object argsValue = argumentsAsObject
                            ? (block.Input.HasValue ? block.Input.Value : EmptyObjectElement())
                            : (block.Input.HasValue ? block.Input.Value.GetRawText() : "{}");
                        toolCalls.Add(new Dictionary<string, object>
                        {
                            ["id"] = block.Id ?? "",
                            ["type"] = "function",
                            ["function"] = new Dictionary<string, object>
                            {
                                ["name"] = block.Name ?? "",
                                ["arguments"] = argsValue,
                            },
                        });
                    }
                }
                var asMsg = new Dictionary<string, object> { ["role"] = "assistant", ["content"] = text.ToString() };
                if (toolCalls.Count > 0) asMsg["tool_calls"] = toolCalls;
                result.Add(asMsg);
            }
            else // "user": text stays user; tool_result blocks become separate role:"tool" messages
            {
                var userText = new StringBuilder();
                var images = new List<ToolImage>();
                var imageMode = ToolImageMode;
                foreach (var block in msg.Content)
                {
                    if (block.Type == "tool_result")
                    {
                        string content = block.Content ?? "";
                        if (block.Images is { Count: > 0 } imgs)
                        {
                            // Chat-completions tool messages are text-only everywhere: the pixels ride in one
                            // user message after them (vision models), or the model is told plainly it can't see.
                            if (imageMode == ChatToolImageMode.Omit)
                                content = Vision.ToolImageWire.AppendNote(content, Vision.ToolImageWire.OmittedForBlindModel(imgs, null, "image"));
                            else
                                images.AddRange(imgs);
                        }
                        result.Add(new Dictionary<string, object>
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = block.ToolUseId ?? "",
                            ["content"] = content,
                        });
                    }
                    else if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
                    {
                        userText.Append(block.Text);
                    }
                }
                if (images.Count > 0)
                {
                    // ONE user message (two in a row trip strict chat templates): what the images are, the images,
                    // then whatever text the loop added.
                    string header = images.Count == 1
                        ? "[The image returned by the tool call above]"
                        : $"[The {images.Count} images returned by the tool calls above, in order]";
                    if (imageMode == ChatToolImageMode.OllamaImages)
                    {
                        result.Add(new Dictionary<string, object>
                        {
                            ["role"] = "user",
                            ["content"] = userText.Length > 0 ? header + "\n" + userText : header,
                            ["images"] = images.Select(i => i.Base64).ToList(),
                        });
                    }
                    else
                    {
                        var parts = new List<object> { new Dictionary<string, object> { ["type"] = "text", ["text"] = header } };
                        foreach (var img in images)
                            parts.Add(new Dictionary<string, object>
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new Dictionary<string, object> { ["url"] = Vision.ToolImageWire.DataUrl(img) },
                            });
                        if (userText.Length > 0) parts.Add(new Dictionary<string, object> { ["type"] = "text", ["text"] = userText.ToString() });
                        result.Add(new Dictionary<string, object> { ["role"] = "user", ["content"] = parts });
                    }
                }
                else if (userText.Length > 0)
                    result.Add(new Dictionary<string, object> { ["role"] = "user", ["content"] = userText.ToString() });
            }
        }
        return result;
    }

    // ── Tool images ──

    /// <summary>How <see cref="BuildOpenAiToolMessages"/> sends tool images on the chat-completions wire.</summary>
    protected enum ChatToolImageMode
    {
        /// <summary>The model can't see: no image is sent; each tool message says so in text.</summary>
        Omit,
        /// <summary>One following user message with OpenAI image_url parts (data URLs) — llama-server with an mmproj.</summary>
        ImageUrlMessage,
        /// <summary>One following user message with Ollama's images[] (raw base64) — an Ollama vision model.</summary>
        OllamaImages,
    }

    /// <summary>The image wire for this provider's current model. Default: the model can't see images.</summary>
    protected virtual ChatToolImageMode ToolImageMode => ChatToolImageMode.Omit;

    /// <summary>Can the model see images returned in tool results? Default false (see each provider).</summary>
    public virtual bool SupportsToolResultImages(string modelId) => false;

    public virtual Task<bool> SupportsToolResultImagesAsync(string modelId, CancellationToken ct = default)
        => Task.FromResult(SupportsToolResultImages(modelId));

    /// <summary>The chat-completions messages this provider would send for a native tool conversation, exactly as
    /// <see cref="ChatWithToolsAsync"/> builds them (public for the contract harness — llama-server can't be
    /// pointed at a mock without starting a real server).</summary>
    public List<object> PreviewToolMessages(List<NativeMessage> messages, string? systemPrompt)
        => BuildOpenAiToolMessages(messages, systemPrompt, argumentsAsObject: ProviderId == "Ollama");

    /// <summary>
    /// Correction for a chars/4 estimate of a serialized chat-completions body: the base64 that rides in it is
    /// not text tokens — a vision model is charged ~w×h/750 per image. Without this a single screenshot "costs"
    /// ~50k tokens and the max_tokens clamp leaves the model no room to answer. Zero when images are not sent.
    /// </summary>
    protected int ToolImageTokenAdjustment(List<NativeMessage> messages)
    {
        if (ToolImageMode == ChatToolImageMode.Omit) return 0;
        int adj = 0;
        foreach (var m in messages)
            foreach (var b in m.Content)
                if (b.Images is { Count: > 0 } imgs)
                    foreach (var img in imgs)
                        adj += Vision.ToolImageWire.EstimateTokens(img) - (img.Base64?.Length ?? 0) / 4;
        return adj;
    }

    /// <summary>True when any block in <paramref name="messages"/> carries images.</summary>
    protected static bool HasToolImages(List<NativeMessage> messages)
        => messages.Any(m => m.Content.Any(b => b.Images is { Count: > 0 }));

    /// <summary>A copy of <paramref name="messages"/> with every image removed and a note in its place: the API's
    /// refusal (<paramref name="why"/> given — a retry after a 400 that named the image; the same note the agent
    /// loop then writes into its own conversation, see <see cref="Vision.ToolImageWire.DropRefused"/>), or that the
    /// model cannot see images (<paramref name="why"/> null).</summary>
    protected static List<NativeMessage> WithoutImages(List<NativeMessage> messages, string? why)
    {
        var copy = new List<NativeMessage>(messages.Count);
        foreach (var m in messages)
        {
            var nm = new NativeMessage { Role = m.Role };
            foreach (var b in m.Content)
            {
                if (b.Images is not { Count: > 0 } imgs) { nm.Content.Add(b); continue; }
                string note = why != null
                    ? Vision.ToolImageWire.RefusedByApi(imgs, why)
                    : Vision.ToolImageWire.OmittedForBlindModel(imgs, null, "image");
                nm.Content.Add(new ContentBlock
                {
                    Type = b.Type, Text = b.Type == "tool_result" ? b.Text : Vision.ToolImageWire.AppendNote(b.Text, note),
                    Signature = b.Signature, Data = b.Data, Provider = b.Provider, Id = b.Id, Name = b.Name, Input = b.Input,
                    ToolUseId = b.ToolUseId, IsError = b.IsError,
                    Content = b.Type == "tool_result" ? Vision.ToolImageWire.AppendNote(b.Content, note) : b.Content,
                });
            }
            copy.Add(nm);
        }
        return copy;
    }

    /// <summary>The API's own error message out of a JSON error body (for a one-line "why"), or a short raw cut.</summary>
    protected static string ShortApiMessage(string body, string? apiKey = null)
    {
        body ??= "";
        if (!string.IsNullOrEmpty(apiKey)) body = body.Replace(apiKey, "[REDACTED]");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("message", out var em) && em.GetString() is { Length: > 0 } s)
                return s.Length > 200 ? s[..200] + "…" : s;
        }
        catch (JsonException) { }
        return body.Length > 200 ? body[..200] + "…" : body;
    }

    /// <summary>Convert our ToolSchema list to OpenAI tools[] (type:function) definitions.</summary>
    protected List<object> BuildOpenAiToolDefs(List<ToolSchema> tools)
    {
        return tools.Select(t => (object)new Dictionary<string, object>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = t.InputSchema.ValueKind == JsonValueKind.Object
                    ? (object)t.InputSchema
                    : new Dictionary<string, object> { ["type"] = "object", ["properties"] = new Dictionary<string, object>() },
            },
        }).ToList();
    }

    /// <summary>Parse an OpenAI-format (/v1/chat/completions) response into a NativeToolResponse.</summary>
    protected NativeToolResponse ParseOpenAiToolResponse(string json)
    {
        var result = new NativeToolResponse();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("usage", out var usage))
            {
                // TryGetInt32 (not GetInt32): a float/string token count must NOT throw and abort the whole
                // parse — that would silently discard valid tool_calls in the same response.
                if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var pti)) result.InputTokens = pti;
                if (usage.TryGetProperty("completion_tokens", out var ctk) && ctk.TryGetInt32(out var cti)) result.OutputTokens = cti;
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                result.StopReason = "end_turn";
                return result;
            }

            var choice = choices[0];
            string finishReason = choice.TryGetProperty("finish_reason", out var fr) ? (fr.GetString() ?? "") : "";

            if (choice.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                {
                    var text = contentEl.GetString();
                    if (!string.IsNullOrEmpty(text)) result.TextContent = text;
                }
                if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    int idx = 0;
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        if (!tc.TryGetProperty("function", out var fn)) continue;
                        string name = fn.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                        if (string.IsNullOrEmpty(name)) continue;
                        string id = tc.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
                        if (string.IsNullOrEmpty(id)) id = $"call_{idx}";
                        result.ToolCalls.Add(new NativeToolCall { Id = id, Name = name, Input = ParseToolArguments(fn) });
                        idx++;
                    }
                }
            }

            result.StopReason = result.ToolCalls.Count > 0 ? "tool_use"
                : finishReason == "length" ? "max_tokens" : "end_turn";
        }
        catch (Exception ex)
        {
            result.TextContent = $"[failed to parse tool response] {ex.Message}";
            result.StopReason = "end_turn";
        }
        return result;
    }

    /// <summary>Extract a function-call's arguments as a JSON object (OpenAI sends a string, Ollama an object).</summary>
    protected static JsonElement ParseToolArguments(JsonElement fn)
    {
        if (!fn.TryGetProperty("arguments", out var argsEl))
            return EmptyObjectElement();
        if (argsEl.ValueKind == JsonValueKind.String)
        {
            string raw = argsEl.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(raw)) return EmptyObjectElement();
            try { using var d = JsonDocument.Parse(raw); return d.RootElement.Clone(); }
            catch { return EmptyObjectElement(); }
        }
        // Ollama path: arguments is already a value. Only an object is valid tool input — anything else
        // (array/scalar from a misbehaving model) would break downstream property extraction.
        return argsEl.ValueKind == JsonValueKind.Object ? argsEl.Clone() : EmptyObjectElement();
    }

    /// <summary>A detached, empty JSON object element (survives its source document's disposal).</summary>
    protected static JsonElement EmptyObjectElement()
    {
        using var d = JsonDocument.Parse("{}");
        return d.RootElement.Clone();
    }

    public virtual void Dispose()
    {
        // Disposes the HttpClient but NOT the shared handler (passed disposeHandler: false in ctor).
        _httpClient.Dispose();
    }
}
