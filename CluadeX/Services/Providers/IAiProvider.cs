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

    /// <summary>Chat with native tool support. Only called when SupportsNativeToolUse is true.</summary>
    Task<NativeToolResponse> ChatWithToolsAsync(
        List<NativeMessage> messages,
        string systemPrompt,
        List<ToolSchema> tools,
        CancellationToken ct = default)
        => Task.FromResult(new NativeToolResponse { TextContent = "Native tool use not supported by this provider." });
}

// ═══════════════════════════════════════════
// Native Tool Use Models
// ═══════════════════════════════════════════

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
    public string Type { get; set; } = "text"; // text, tool_use, tool_result, thinking
    public string? Text { get; set; }

    // For tool_use blocks
    public string? Id { get; set; }
    public string? Name { get; set; }
    public JsonElement? Input { get; set; }

    // For tool_result blocks
    public string? ToolUseId { get; set; }
    public string? Content { get; set; }
    public bool? IsError { get; set; }
}

/// <summary>Response from a native tool_use API call.</summary>
public class NativeToolResponse
{
    public string? TextContent { get; set; }
    public string? ThinkingContent { get; set; }
    public List<NativeToolCall> ToolCalls { get; set; } = new();
    public string StopReason { get; set; } = "end_turn"; // end_turn, tool_use, max_tokens
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}

/// <summary>A tool call extracted from the native API response.</summary>
public class NativeToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public JsonElement Input { get; set; }
}
