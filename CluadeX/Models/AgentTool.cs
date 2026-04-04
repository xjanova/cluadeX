namespace CluadeX.Models;

/// <summary>
/// Types of tools the agent can use to interact with the file system and environment.
/// </summary>
public enum ToolType
{
    // File operations
    ReadFile,
    WriteFile,
    EditFile,
    ListFiles,
    SearchFiles,
    SearchContent,
    RunCommand,
    CreateDirectory,

    // Git operations
    GitStatus,
    GitAdd,
    GitCommit,
    GitPush,
    GitPull,
    GitBranch,
    GitCheckout,
    GitDiff,
    GitLog,
    GitClone,
    GitInit,
    GitStash,

    // GitHub operations
    GhPrCreate,
    GhPrList,
    GhIssueCreate,
    GhIssueList,
    GhRepoView,

    // Web operations
    WebFetch,
    WebSearch,

    // Interactive REPL
    Repl,

    // Task management
    TaskCreate,
    TaskList,
    TaskStop,
    TaskOutput,

    // Git worktree
    GitWorktreeCreate,
    GitWorktreeRemove,

    // Agent sub-task
    AgentSpawn,

    // MCP (external tool servers)
    McpTool,

    // Agent meta-tools
    TodoWrite,
    PlanMode,

    // Phase 3: New tools (Claude Code parity)
    GlobSearch,      // Pattern-based file matching (e.g., **/*.cs)
    Grep,            // Regex content search with context lines
    AskUser,         // Ask user a question via UI dialog
    Config,          // Get/set CluadeX settings from chat
    NotebookEdit,    // Edit Jupyter .ipynb notebook cells
    PowerShell,      // Explicit PowerShell execution

    // Skills system (Phase 4)
    SkillInvoke,     // Invoke a skill by name

    // Memory tools
    MemorySave,      // Save a memory entry
    MemoryList,      // List all memories
    MemoryDelete,    // Delete a memory
}

/// <summary>
/// Represents a parsed tool call from the model's output.
/// </summary>
public class ToolCall
{
    public ToolType Type { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public Dictionary<string, string> Arguments { get; set; } = new();
    public string RawText { get; set; } = string.Empty;

    /// <summary>For MCP tools: the server that provides this tool.</summary>
    public string? McpServerName { get; set; }
    /// <summary>For MCP tools: the original tool name on the server.</summary>
    public string? McpToolName { get; set; }

    public string GetArg(string key, string defaultValue = "")
        => Arguments.TryGetValue(key, out var val) ? val : defaultValue;

    /// <summary>Whether this tool is safe to execute concurrently with other tools.
    /// Read-only tools and queries are safe; write/execute tools are not.</summary>
    public bool IsConcurrencySafe => Type switch
    {
        ToolType.ReadFile or ToolType.ListFiles or ToolType.SearchFiles
        or ToolType.SearchContent or ToolType.GlobSearch or ToolType.Grep
        or ToolType.GitStatus or ToolType.GitLog
        or ToolType.GitDiff or ToolType.GitBranch
        or ToolType.GhPrList or ToolType.GhIssueList or ToolType.GhRepoView
        or ToolType.WebSearch or ToolType.WebFetch
        or ToolType.TaskList or ToolType.TaskOutput
        or ToolType.AskUser or ToolType.MemoryList => true,
        _ => false,
    };

    /// <summary>Whether this tool only reads data (no side effects).</summary>
    public bool IsReadOnly => Type switch
    {
        ToolType.ReadFile or ToolType.ListFiles or ToolType.SearchFiles
        or ToolType.SearchContent or ToolType.GlobSearch or ToolType.Grep
        or ToolType.GitStatus or ToolType.GitLog
        or ToolType.GitDiff or ToolType.GitBranch
        or ToolType.GhPrList or ToolType.GhIssueList or ToolType.GhRepoView
        or ToolType.TaskList or ToolType.TaskOutput
        or ToolType.AskUser or ToolType.MemoryList => true,
        _ => false,
    };
}

/// <summary>
/// Represents the result of executing a tool.
/// </summary>
public class ToolResult
{
    public ToolType Type { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    /// <summary>Short description for display in the chat UI.</summary>
    public string Summary { get; set; } = string.Empty;
    /// <summary>Input arguments passed to the tool (for UI display).</summary>
    public Dictionary<string, string>? Arguments { get; set; }
}

/// <summary>
/// Represents one step in the agent's agentic execution loop.
/// </summary>
public class AgentStep
{
    public int StepNumber { get; set; }
    public string? ThinkingText { get; set; }
    public List<ToolCall> ToolCalls { get; set; } = new();
    public List<ToolResult> ToolResults { get; set; } = new();
    public string? ResponseText { get; set; }
}

/// <summary>A TODO item managed by the agent's todo_write tool.</summary>
public class TodoItem
{
    public string Content { get; set; } = "";
    public string Status { get; set; } = "pending"; // pending, in_progress, completed
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
