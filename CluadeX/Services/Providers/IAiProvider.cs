using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

public interface IAiProvider : IDisposable
{
    string ProviderId { get; }
    string DisplayName { get; }
    bool IsReady { get; }
    bool IsLoading { get; }
    string StatusMessage { get; }

    event Action<string>? OnStatusChanged;
    event Action<bool>? OnLoadingChanged;
    event Action<string>? OnError;

    Task InitializeAsync(CancellationToken ct = default);

    IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default);

    Task<string> GenerateAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default);

    Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default);

    // ═══════════════════════════════════════════
    // Phase 5: Native Tool Use Support
    // ═══════════════════════════════════════════

    /// <summary>Whether this provider supports native tool_use API format (structured tool calls).</summary>
    bool SupportsNativeToolUse => false;

    /// <summary>Chat with native tool support. Only called when SupportsNativeToolUse is true.
    /// <paramref name="onTextDelta"/> (optional) is invoked with each streamed text chunk as it arrives,
    /// so the agent loop can render the model's reply live instead of waiting for the whole turn.</summary>
    /// <param name="toolChoice">Constrain tool selection: null/"auto" = model's choice (default), "required" =
    /// must call some tool, "none" = no tools, or an exact tool name = must call that tool. Used by the planner
    /// and forced-retry to make a weak local model emit a constrained tool call instead of free prose.</param>
    Task<NativeToolResponse> ChatWithToolsAsync(
        List<NativeMessage> messages,
        string systemPrompt,
        List<ToolSchema> tools,
        Action<string>? onTextDelta = null,
        CancellationToken ct = default,
        string? toolChoice = null)
        => Task.FromResult(new NativeToolResponse { TextContent = "Native tool use not supported by this provider." });

    // ═══════════════════════════════════════════
    // Tool images (vision)
    // ═══════════════════════════════════════════

    /// <summary>
    /// Can <paramref name="modelId"/> (empty = the provider's configured model) SEE images that come back in tool
    /// results (<see cref="ContentBlock.Images"/>)? False = the agent loop drops the images and tells the model in
    /// text that it did not see them. Must answer from what is already known (cached), without I/O.
    /// </summary>
    bool SupportsToolResultImages(string modelId) => false;

    /// <summary>The same question, allowed to ask the backend once (Ollama's /api/show) — the agent loop calls this
    /// before a turn so <see cref="SupportsToolResultImages"/> then answers from the cache.</summary>
    Task<bool> SupportsToolResultImagesAsync(string modelId, CancellationToken ct = default)
        => Task.FromResult(SupportsToolResultImages(modelId));
}

// ═══════════════════════════════════════════
// Native Tool Use Models
// ═══════════════════════════════════════════

/// <summary>A cloud provider that can list the models its key can call (Settings fills the model
/// dropdown from this after Test Connection, so new releases appear without a code change).</summary>
public interface IModelLister
{
    /// <summary>Model IDs, newest first; null when the listing fails (keep the built-in list).</summary>
    Task<List<string>?> ListModelsAsync(CancellationToken ct = default);
}

/// <summary>Tool schema for native API tool definitions.</summary>
public class ToolSchema
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public JsonElement InputSchema { get; set; }
}

/// <summary>Native API message with structured content blocks.</summary>
public class NativeMessage
{
    public string Role { get; set; } = "user"; // user, assistant
    public List<ContentBlock> Content { get; set; } = new();
}

/// <summary>A content block in a native API message.</summary>
public class ContentBlock
{
    public string Type { get; set; } = "text"; // text, tool_use, tool_result, thinking, redacted_thinking, provider_item
    public string? Text { get; set; }          // also the reasoning text of a thinking block

    // For thinking / redacted_thinking blocks — must be sent back byte-for-byte
    public string? Signature { get; set; }
    public string? Data { get; set; }

    /// <summary>provider_item: which provider's raw JSON <see cref="Data"/> holds ("openai" reasoning
    /// item, "gemini" model parts). Echoed verbatim by that provider; every other provider skips it.</summary>
    public string? Provider { get; set; }

    // For tool_use blocks
    public string? Id { get; set; }
    public string? Name { get; set; }
    public JsonElement? Input { get; set; }

    // For tool_result blocks
    public string? ToolUseId { get; set; }
    public string? Content { get; set; }
    public bool? IsError { get; set; }

    /// <summary>tool_result: images that go with <see cref="Content"/> (null = text only). Each provider
    /// serializes them in its own wire format; a provider that cannot see images drops them and says so in
    /// text. Kept apart from Content so the text budgets never touch the base64.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<ToolImage>? Images { get; set; }
}

/// <summary>Response from a native tool_use API call.</summary>
public class NativeToolResponse
{
    public string? TextContent { get; set; }
    public string? ThinkingContent { get; set; }
    public List<NativeToolCall> ToolCalls { get; set; } = new();
    public string StopReason { get; set; } = "end_turn"; // end_turn, tool_use, max_tokens, refusal
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }

    /// <summary>The API refused the tool images of this request (a 400 that named the image) and the provider
    /// answered from a text-only retry. The agent loop then drops those images from its own conversation
    /// (<see cref="Vision.ToolImageWire.DropRefused"/>) so later steps don't resend them into the same 400.</summary>
    public bool ImagesRefused { get; set; }

    /// <summary>The API's own reason for <see cref="ImagesRefused"/> (key redacted, one short line).</summary>
    public string? ImagesRefusedReason { get; set; }

    /// <summary>Every block the model emitted, in order (thinking, redacted_thinking, text, tool_use).
    /// Empty for providers that don't report blocks. Anthropic rejects a continued tool loop whose
    /// thinking blocks were dropped or altered, so the assistant turn must be rebuilt from these.</summary>
    public List<ContentBlock> Blocks { get; set; } = new();

    /// <summary>The assistant turn to append to the conversation: the emitted blocks in order when the
    /// provider reported them, otherwise the text then the tool calls. With
    /// <paramref name="includeToolUse"/> false the tool_use blocks are left out, for a caller that adds
    /// each one as it executes it.</summary>
    public NativeMessage ToAssistantMessage(bool includeToolUse = true)
    {
        var msg = new NativeMessage { Role = "assistant" };
        if (Blocks.Count > 0)
        {
            foreach (var b in Blocks)
                if (includeToolUse || b.Type != "tool_use") msg.Content.Add(b);
            return msg;
        }
        if (!string.IsNullOrEmpty(TextContent))
            msg.Content.Add(new ContentBlock { Type = "text", Text = TextContent });
        if (includeToolUse)
            foreach (var c in ToolCalls)
                msg.Content.Add(new ContentBlock { Type = "tool_use", Id = c.Id, Name = c.Name, Input = c.Input });
        return msg;
    }
}

/// <summary>A tool call extracted from the native API response.</summary>
public class NativeToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public JsonElement Input { get; set; }
}
