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
    private readonly Dictionary<string, McpTool> _qualifiedNameMap = new();
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

    /// <summary>Generate tool definition text for the LLM system prompt.</summary>
    public string GetToolDefinitionsPrompt()
    {
        lock (_lock)
        {
            if (_qualifiedNameMap.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("    MCP SERVER TOOLS (external tool servers):");
            sb.AppendLine();

            foreach (var (serverName, tools) in _serverTools)
            {
                if (tools.Count == 0) continue;

                sb.AppendLine($"    [{serverName}] — {tools.Count} tool(s):");
                sb.AppendLine();

                foreach (var tool in tools)
                {
                    sb.AppendLine($"    {tool.QualifiedName} — {tool.Description}");

                    // Generate parameter hints from inputSchema
                    if (tool.InputSchema.HasValue)
                    {
                        var schema = tool.InputSchema.Value;
                        if (schema.TryGetProperty("properties", out var props))
                        {
                            sb.AppendLine($"        [ACTION: {tool.QualifiedName}]");
                            var required = new HashSet<string>();
                            if (schema.TryGetProperty("required", out var reqArr))
                            {
                                foreach (var r in reqArr.EnumerateArray())
                                    required.Add(r.GetString() ?? "");
                            }

                            foreach (var prop in props.EnumerateObject())
                            {
                                string type = prop.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "string" : "string";
                                string desc = prop.Value.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
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
