using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Mcp;

/// <summary>
/// Registry of all tools discovered from MCP servers.
/// Maps qualified tool names to their server + schema.
/// Generates prompt text for the LLM.
/// </summary>
public class McpToolRegistry
{
    private readonly Dictionary<string, List<McpTool>> _serverTools = new();
    // Case-insensitive: the legacy [ACTION:] path lowercases tool names before lookup, so a
    // server key with any capital letter (e.g. "Desktop_Commander") made every one of its tools
    // silently unresolvable on that path. The native path passes names verbatim and is unaffected.
    private readonly Dictionary<string, McpTool> _qualifiedNameMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Total number of MCP tools across all servers.</summary>
    public int TotalToolCount
    {
        get { lock (_lock) return _qualifiedNameMap.Count; }
    }

    /// <summary>Update the tool list for a server.</summary>
    public void UpdateServer(string serverName, List<McpTool> tools)
    {
        lock (_lock)
        {
            // Remove old tools for this server
            if (_serverTools.ContainsKey(serverName))
            {
                foreach (var oldTool in _serverTools[serverName])
                    _qualifiedNameMap.Remove(oldTool.QualifiedName);
            }

            _serverTools[serverName] = tools;

            foreach (var tool in tools)
                _qualifiedNameMap[tool.QualifiedName] = tool;
        }
    }

    /// <summary>Remove all tools for a server.</summary>
    public void RemoveServer(string serverName)
    {
        lock (_lock)
        {
            if (_serverTools.TryGetValue(serverName, out var tools))
            {
                foreach (var tool in tools)
                    _qualifiedNameMap.Remove(tool.QualifiedName);
                _serverTools.Remove(serverName);
            }
        }
    }

    /// <summary>Get all tools for a specific server.</summary>
    public List<McpTool> GetToolsForServer(string serverName)
    {
        lock (_lock)
            return _serverTools.TryGetValue(serverName, out var tools) ? new(tools) : new();
    }

    /// <summary>Get all tools across all servers.</summary>
    public List<McpTool> GetAllTools()
    {
        lock (_lock)
            return _qualifiedNameMap.Values.ToList();
    }

    /// <summary>Look up a tool by qualified name (mcp__{server}__{tool}).</summary>
    public McpTool? FindTool(string qualifiedName)
    {
        lock (_lock)
            return _qualifiedNameMap.TryGetValue(qualifiedName, out var tool) ? tool : null;
    }

    /// <summary>Try to resolve a tool name that might be qualified or bare.</summary>
    public McpTool? ResolveTool(string name)
    {
        lock (_lock)
        {
            // Exact qualified name match
            if (_qualifiedNameMap.TryGetValue(name, out var tool))
                return tool;

            // Try as bare name (if unique across servers)
            var matches = _qualifiedNameMap.Values
                .Where(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return matches.Count == 1 ? matches[0] : null;
        }
    }

    /// <summary>Generate tool definition text for the LLM system prompt. <paramref name="describe"/> (optional)
    /// gives the description to show for a tool, or null to leave the tool out — how tool policies hide a tool
    /// or reword it in the [ACTION:] catalogue exactly as they do in the native schemas.</summary>
    public string GetToolDefinitionsPrompt(Func<McpTool, string?>? describe = null)
    {
        lock (_lock)
        {
            if (_qualifiedNameMap.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("    MCP SERVER TOOLS (external tool servers):");
            sb.AppendLine();

            foreach (var (serverName, allTools) in _serverTools)
            {
                var shown = allTools
                    .Select(t => (Tool: t, Description: describe == null ? t.Description ?? "" : describe(t)))
                    .Where(x => x.Description != null)
                    .ToList();
                if (shown.Count == 0) continue;

                sb.AppendLine($"    [{serverName}] — {shown.Count} tool(s):");
                sb.AppendLine();

                foreach (var (tool, description) in shown)
                {
                    sb.AppendLine($"    {tool.QualifiedName} — {description}");

                    // Parameter hints from the SANITIZED schema — the same types the native schema and the argument
                    // typing use ($ref / anyOf / ["string","null"] resolved; limits folded into the description). A raw
                    // "type" array used to throw here and take the whole [ACTION:] catalogue down.
                    if (tool.InputSchema.HasValue)
                    {
                        var schema = McpSchemaSanitizer.SanitizeCached(tool.ServerName, tool.Name, tool.InputSchema);
                        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                        {
                            sb.AppendLine($"        [ACTION: {tool.QualifiedName}]");
                            var required = new HashSet<string>();
                            if (schema.TryGetProperty("required", out var reqArr) && reqArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var r in reqArr.EnumerateArray())
                                    if (r.ValueKind == JsonValueKind.String) required.Add(r.GetString() ?? "");
                            }

                            foreach (var prop in props.EnumerateObject())
                            {
                                string type = McpSchemaSanitizer.TypeOf(prop.Value) ?? "string";
                                string desc = prop.Value.TryGetProperty("description", out var d)
                                    && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                                string reqMark = required.Contains(prop.Name) ? " (required)" : "";
                                sb.AppendLine($"        {prop.Name}: <{type}>{reqMark} — {desc}");
                            }
                            sb.AppendLine("        [/ACTION]");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"        [ACTION: {tool.QualifiedName}]");
                        sb.AppendLine("        [/ACTION]");
                    }

                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }
    }
}
