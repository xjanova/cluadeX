using System.Text.Json;
using System.Text.Json.Serialization;

namespace CluadeX.Models;

// ════════════════════════════════════════════
// MCP Server Configuration
// ════════════════════════════════════════════

/// <summary>Root config file structure (matches Claude Desktop format).</summary>
public class McpConfigFile
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();
}

/// <summary>Configuration for a single MCP server.</summary>
public class McpServerConfig
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = new();

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Runtime: server name (key from config).</summary>
    [JsonIgnore]
    public string Name { get; set; } = "";
}

// ════════════════════════════════════════════
// MCP Tool Definitions
// ════════════════════════════════════════════

/// <summary>A tool discovered from an MCP server.</summary>
public class McpTool
{
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string Description { get; set; } = "";
    public JsonElement? InputSchema { get; set; }

    /// <summary>Which MCP server provides this tool.</summary>
    public string ServerName { get; set; } = "";

    /// <summary>Fully qualified name: mcp__{server}__{tool}</summary>
    public string QualifiedName => $"mcp__{ServerName}__{Name}";
}

/// <summary>Content item in a tool result.</summary>
public class McpContentItem
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text"; // text, image, resource_link

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; } // base64 for images

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>Result from tools/call.</summary>
public class McpToolResult
{
    [JsonPropertyName("content")]
    public List<McpContentItem> Content { get; set; } = new();

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }

    /// <summary>Flatten all text content into a single string.</summary>
    public string GetTextOutput()
    {
        var texts = Content
            .Where(c => c.Type == "text" && c.Text != null)
            .Select(c => c.Text!);
        return string.Join("\n", texts);
    }
}

// ════════════════════════════════════════════
// JSON-RPC 2.0 Messages
// ════════════════════════════════════════════

/// <summary>JSON-RPC 2.0 Request (expects response).</summary>
public class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public object? Params { get; set; }
}

/// <summary>JSON-RPC 2.0 Notification (no response expected, no id).</summary>
public class JsonRpcNotification
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; set; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public object? Params { get; set; }
}

/// <summary>JSON-RPC 2.0 Response.</summary>
public class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }

    public bool IsSuccess => Error == null;
}

/// <summary>JSON-RPC 2.0 Error object.</summary>
public class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

// ════════════════════════════════════════════
// MCP Initialize Types
// ════════════════════════════════════════════

public class McpClientInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "CluadeX";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "2.1.0";
}

public class McpClientCapabilities
{
    [JsonPropertyName("roots")]
    public McpRootsCapability? Roots { get; set; }
}

public class McpRootsCapability
{
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; set; } = true;
}

public class McpInitializeParams
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "2025-03-26";

    [JsonPropertyName("capabilities")]
    public McpClientCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("clientInfo")]
    public McpClientInfo ClientInfo { get; set; } = new();
}

public class McpServerCapabilities
{
    [JsonPropertyName("tools")]
    public JsonElement? Tools { get; set; }

    [JsonPropertyName("resources")]
    public JsonElement? Resources { get; set; }

    [JsonPropertyName("prompts")]
    public JsonElement? Prompts { get; set; }
}

public class McpInitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = "";

    [JsonPropertyName("capabilities")]
    public McpServerCapabilities? Capabilities { get; set; }

    [JsonPropertyName("serverInfo")]
    public McpClientInfo? ServerInfo { get; set; }
}
