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
        CancellationToken ct = default)
        => Task.FromResult(new NativeToolResponse
        {
            TextContent = "Native tool use not supported by this provider.",
            StopReason = "end_turn",
        });

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
                foreach (var block in msg.Content)
                {
                    if (block.Type == "tool_result")
                    {
                        result.Add(new Dictionary<string, object>
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = block.ToolUseId ?? "",
                            ["content"] = block.Content ?? "",
                        });
                    }
                    else if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
                    {
                        userText.Append(block.Text);
                    }
                }
                if (userText.Length > 0)
                    result.Add(new Dictionary<string, object> { ["role"] = "user", ["content"] = userText.ToString() });
            }
        }
        return result;
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
