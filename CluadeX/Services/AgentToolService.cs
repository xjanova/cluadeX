using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CluadeX.Models;
using CluadeX.Services.Mcp;

namespace CluadeX.Services;

/// <summary>
/// Parses tool calls from model output and executes them via FileSystemService / CodeExecutionService.
/// Tool format: [ACTION: tool_name]\nkey: value\n[/ACTION]
/// </summary>
public class AgentToolService
{
    private readonly FileSystemService _fileSystem;
    private readonly CodeExecutionService _codeExecution;
    private readonly GitService _gitService;
    private readonly GitHubService _gitHubService;
    private readonly PermissionService _permissionService;
    private readonly SettingsService _settingsService;
    private readonly ActivationService _activationService;
    private readonly WebFetchService _webFetchService;
    private readonly TaskManagerService _taskManager;
    private readonly McpServerManager _mcpManager;

    // ─── TODO List State ───
    private readonly List<TodoItem> _todoItems = new();
    public IReadOnlyList<TodoItem> TodoItems => _todoItems;
    public event Action? TodoChanged;

    // ─── REPL Session State ───
    private readonly Dictionary<string, ReplSession> _replSessions = new();

    // ─── Agent Sub-task Spawning ───
    /// <summary>Raised when agent requests a sub-task spawn. Returns sub-agent result.</summary>
    public event Func<string, string, CancellationToken, Task<string>>? OnAgentSpawnRequested;

    // Regex to match [ACTION: tool_name]...[/ACTION] blocks
    private static readonly Regex ActionRegex = new(
        @"\[ACTION:\s*(\w+)\](.*?)\[/ACTION\]",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Raised when a tool requires user confirmation (PermAction.Ask).</summary>
    public event Func<string, string, Task<bool>>? OnPermissionRequired;

    private readonly HookService _hookService;
    private readonly MemoryService _memoryService;
    private readonly SkillService _skillService;

    public AgentToolService(FileSystemService fileSystem, CodeExecutionService codeExecution,
        GitService gitService, GitHubService gitHubService, PermissionService permissionService,
        SettingsService settingsService, ActivationService activationService,
        WebFetchService webFetchService, TaskManagerService taskManager,
        McpServerManager mcpManager, HookService hookService, MemoryService memoryService,
        SkillService skillService)
    {
        _fileSystem = fileSystem;
        _codeExecution = codeExecution;
        _gitService = gitService;
        _gitHubService = gitHubService;
        _permissionService = permissionService;
        _settingsService = settingsService;
        _activationService = activationService;
        _webFetchService = webFetchService;
        _taskManager = taskManager;
        _mcpManager = mcpManager;
        _hookService = hookService;
        _memoryService = memoryService;
        _skillService = skillService;
    }

    // ─── Parse Tool Calls from Model Output ───
    public List<ToolCall> ParseToolCalls(string modelOutput)
    {
        var calls = new List<ToolCall>();

        foreach (Match match in ActionRegex.Matches(modelOutput))
        {
            string toolName = match.Groups[1].Value.Trim().ToLowerInvariant();
            string body = match.Groups[2].Value.Trim();

            var toolType = ResolveToolType(toolName);
            if (toolType == null) continue;

            var args = ParseArguments(body, toolType.Value);

            var call = new ToolCall
            {
                Type = toolType.Value,
                ToolName = toolName,
                Arguments = args,
                RawText = match.Value,
            };

            // Set MCP metadata if it's an MCP tool
            if (toolType == ToolType.McpTool)
            {
                var mcpTool = _mcpManager.ToolRegistry.ResolveTool(toolName);
                if (mcpTool != null)
                {
                    call.McpServerName = mcpTool.ServerName;
                    call.McpToolName = mcpTool.Name;
                }
            }

            calls.Add(call);
        }

        return calls;
    }

    // ─── Check if output contains any tool calls ───
    public bool HasToolCalls(string modelOutput)
    {
        return ActionRegex.IsMatch(modelOutput);
    }

    // ─── Strip tool calls from response text (for display) ───
    public string StripToolCalls(string modelOutput)
    {
        return ActionRegex.Replace(modelOutput, "").Trim();
    }

    // ─── Execute a Single Tool Call ───
    public async Task<ToolResult> ExecuteToolAsync(ToolCall call, CancellationToken ct = default)
    {
        try
        {
            // ── Feature toggle check ──
            if (!IsToolAllowed(call.Type))
            {
                string featureName = call.Type switch
                {
                    ToolType.GitStatus or ToolType.GitAdd or ToolType.GitCommit or
                    ToolType.GitPush or ToolType.GitPull or ToolType.GitBranch or
                    ToolType.GitCheckout or ToolType.GitDiff or ToolType.GitLog or
                    ToolType.GitClone or ToolType.GitInit or ToolType.GitStash => "Git",
                    ToolType.GhPrCreate or ToolType.GhPrList or
                    ToolType.GhIssueCreate or ToolType.GhIssueList or ToolType.GhRepoView => "GitHub",
                    _ => call.ToolName,
                };
                return Fail(call, $"{featureName} tools are disabled. Enable in Features page or activate with a key.");
            }

            // ── Permission check ──
            string resource = call.GetArg("path", call.GetArg("command", call.ToolName));
            string scope = call.Type switch
            {
                ToolType.WriteFile or ToolType.EditFile or ToolType.CreateDirectory => "write",
                ToolType.RunCommand => "execute",
                ToolType.ReadFile or ToolType.ListFiles or ToolType.SearchFiles or ToolType.SearchContent => "read",
                _ => "execute",
            };
            var perm = _permissionService.CheckPermission(resource, scope, call.ToolName);
            if (perm == PermAction.Deny)
                return Fail(call, $"Permission denied for {scope}: {resource}");
            if (perm == PermAction.Ask)
            {
                bool allowed = OnPermissionRequired != null
                    && await OnPermissionRequired.Invoke(call.ToolName, $"{scope}: {resource}");
                if (!allowed)
                    return Fail(call, $"User denied permission for {scope}: {resource}");
            }

            // ── PreToolUse hooks ──
            var hookResult = await _hookService.ExecutePreToolHooksAsync(call, ct);
            if (!hookResult.Success)
                return Fail(call, $"PreToolUse hook blocked execution: {hookResult.Message}");

            var toolResult = call.Type switch
            {
                // File tools
                ToolType.ReadFile => ExecuteReadFile(call),
                ToolType.WriteFile => ExecuteWriteFile(call),
                ToolType.EditFile => ExecuteEditFile(call),
                ToolType.ListFiles => ExecuteListFiles(call),
                ToolType.SearchFiles => ExecuteSearchFiles(call),
                ToolType.SearchContent => ExecuteSearchContent(call),
                ToolType.RunCommand => await ExecuteRunCommandAsync(call, ct),
                ToolType.CreateDirectory => ExecuteCreateDirectory(call),

                // Git tools
                ToolType.GitStatus => await ExecuteGitStatusAsync(call),
                ToolType.GitAdd => await ExecuteGitAddAsync(call),
                ToolType.GitCommit => await ExecuteGitCommitAsync(call),
                ToolType.GitPush => await ExecuteGitPushAsync(call),
                ToolType.GitPull => await ExecuteGitPullAsync(call),
                ToolType.GitBranch => await ExecuteGitBranchAsync(call),
                ToolType.GitCheckout => await ExecuteGitCheckoutAsync(call),
                ToolType.GitDiff => await ExecuteGitDiffAsync(call),
                ToolType.GitLog => await ExecuteGitLogAsync(call),
                ToolType.GitClone => await ExecuteGitCloneAsync(call, ct),
                ToolType.GitInit => await ExecuteGitInitAsync(call),
                ToolType.GitStash => await ExecuteGitStashAsync(call),

                // GitHub tools
                ToolType.GhPrCreate => await ExecuteGhPrCreateAsync(call),
                ToolType.GhPrList => await ExecuteGhPrListAsync(call),
                ToolType.GhIssueCreate => await ExecuteGhIssueCreateAsync(call),
                ToolType.GhIssueList => await ExecuteGhIssueListAsync(call),
                ToolType.GhRepoView => await ExecuteGhRepoViewAsync(call),

                // Web tools
                ToolType.WebFetch => await ExecuteWebFetchAsync(call, ct),
                ToolType.WebSearch => await ExecuteWebSearchAsync(call, ct),

                // Interactive REPL
                ToolType.Repl => await ExecuteReplAsync(call, ct),

                // Task management
                ToolType.TaskCreate => ExecuteTaskCreate(call),
                ToolType.TaskList => ExecuteTaskList(call),
                ToolType.TaskStop => ExecuteTaskStop(call),
                ToolType.TaskOutput => ExecuteTaskOutput(call),

                // Git worktree
                ToolType.GitWorktreeCreate => await ExecuteGitWorktreeCreateAsync(call),
                ToolType.GitWorktreeRemove => await ExecuteGitWorktreeRemoveAsync(call),

                // Agent sub-task
                ToolType.AgentSpawn => await ExecuteAgentSpawnAsync(call, ct),

                // MCP tools
                ToolType.McpTool => await ExecuteMcpToolAsync(call, ct),

                // Agent meta-tools
                ToolType.TodoWrite => ExecuteTodoWrite(call),
                ToolType.PlanMode => ExecutePlanMode(call),

                // Phase 3: New tools
                ToolType.GlobSearch => ExecuteGlobSearch(call),
                ToolType.Grep => ExecuteGrepSearch(call),
                ToolType.AskUser => await ExecuteAskUserAsync(call),
                ToolType.Config => ExecuteConfig(call),
                ToolType.NotebookEdit => ExecuteNotebookEdit(call),
                ToolType.PowerShell => await ExecutePowerShellAsync(call, ct),

                // Phase 4: Skills
                ToolType.SkillInvoke => ExecuteSkillInvoke(call),

                // Memory tools
                ToolType.MemorySave => ExecuteMemorySave(call),
                ToolType.MemoryList => ExecuteMemoryList(call),
                ToolType.MemoryDelete => ExecuteMemoryDelete(call),

                _ => new ToolResult
                {
                    Type = call.Type,
                    ToolName = call.ToolName,
                    Success = false,
                    Error = $"Unknown tool: {call.ToolName}",
                },
            };

            // ── Attach arguments for UI display ──
            toolResult.Arguments = call.Arguments;

            // ── PostToolUse hooks ──
            _ = _hookService.ExecutePostToolHooksAsync(call, toolResult, ct);

            return toolResult;
        }
        catch (Exception ex)
        {
            return new ToolResult
            {
                Type = call.Type,
                ToolName = call.ToolName,
                Success = false,
                Error = ex.Message,
                Summary = $"Error: {ex.Message}",
            };
        }
    }

    // ─── Execute All Tool Calls ───
    public async Task<List<ToolResult>> ExecuteAllToolsAsync(
        List<ToolCall> calls, CancellationToken ct = default)
    {
        var results = new List<ToolResult>();
        foreach (var call in calls)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ExecuteToolAsync(call, ct);
            results.Add(result);
        }
        return results;
    }

    // ─── Format Tool Results for Model Feedback ───
    public string FormatToolResults(List<ToolResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[TOOL RESULTS]");

        foreach (var r in results)
        {
            sb.AppendLine($"--- {r.ToolName} ({(r.Success ? "OK" : "ERROR")}) ---");
            if (r.Success)
            {
                string output = r.Output;
                // Truncate very long outputs to keep context manageable
                if (output.Length > 4000)
                    output = output[..4000] + "\n... (truncated)";
                sb.AppendLine(output);
            }
            else
            {
                sb.AppendLine($"Error: {r.Error}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("[/TOOL RESULTS]");
        return sb.ToString();
    }

    // ─── Build Native Tool Schemas (for Anthropic tool_use API) ───
    public List<Providers.ToolSchema> BuildNativeToolSchemas()
    {
        var schemas = new List<Providers.ToolSchema>();
        var features = _settingsService.Settings.Features;
        bool gitEnabled = features.GitIntegration && _activationService.IsFeatureUnlocked("feature.git");
        bool githubEnabled = features.GitHubIntegration && _activationService.IsFeatureUnlocked("feature.github");

        // Helper to create a JSON schema from parameter definitions
        static System.Text.Json.JsonElement MakeSchema(params (string name, string type, string desc, bool required)[] props)
        {
            var properties = new Dictionary<string, object>();
            var required = new List<string>();
            foreach (var (name, type, desc, req) in props)
            {
                properties[name] = new Dictionary<string, string> { ["type"] = type, ["description"] = desc };
                if (req) required.Add(name);
            }
            var schema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
            };
            var json = System.Text.Json.JsonSerializer.Serialize(schema);
            return System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
        }

        // File tools
        schemas.Add(new() { Name = "read_file", Description = "Read the contents of a file",
            InputSchema = MakeSchema(("path", "string", "File path relative to project root", true)) });
        schemas.Add(new() { Name = "write_file", Description = "Create or overwrite a file",
            InputSchema = MakeSchema(("path", "string", "File path", true), ("content", "string", "File content", true)) });
        schemas.Add(new() { Name = "edit_file", Description = "Find and replace text in an existing file",
            InputSchema = MakeSchema(("path", "string", "File path", true), ("find", "string", "Text to find", true), ("replace", "string", "Replacement text", true)) });
        schemas.Add(new() { Name = "list_files", Description = "List files and directories",
            InputSchema = MakeSchema(("path", "string", "Directory path", true)) });
        schemas.Add(new() { Name = "search_files", Description = "Find files matching a pattern",
            InputSchema = MakeSchema(("pattern", "string", "File glob pattern", true), ("path", "string", "Search directory", false)) });
        schemas.Add(new() { Name = "search_content", Description = "Search for text in file contents",
            InputSchema = MakeSchema(("query", "string", "Search text", true), ("path", "string", "Search directory", false), ("pattern", "string", "File pattern filter", false)) });
        schemas.Add(new() { Name = "run_command", Description = "Execute a shell command",
            InputSchema = MakeSchema(("command", "string", "Shell command to run", true)) });
        schemas.Add(new() { Name = "create_directory", Description = "Create a directory",
            InputSchema = MakeSchema(("path", "string", "Directory path", true)) });

        // Glob + Grep
        schemas.Add(new() { Name = "glob", Description = "Find files matching glob patterns (e.g., **/*.cs)",
            InputSchema = MakeSchema(("pattern", "string", "Glob pattern", true), ("path", "string", "Base directory", false)) });
        schemas.Add(new() { Name = "grep_search", Description = "Regex content search with context lines",
            InputSchema = MakeSchema(("pattern", "string", "Regex pattern", true), ("path", "string", "Search directory", false), ("glob", "string", "File filter", false), ("output_mode", "string", "content|files_with_matches|count", false), ("context", "string", "Context lines", false)) });

        // Git tools
        if (gitEnabled)
        {
            schemas.Add(new() { Name = "git_status", Description = "Show git working tree status", InputSchema = MakeSchema() });
            schemas.Add(new() { Name = "git_add", Description = "Stage files for commit", InputSchema = MakeSchema(("paths", "string", "Files to stage (space-separated)", true)) });
            schemas.Add(new() { Name = "git_commit", Description = "Create a git commit", InputSchema = MakeSchema(("message", "string", "Commit message", true)) });
            schemas.Add(new() { Name = "git_diff", Description = "Show changes", InputSchema = MakeSchema(("args", "string", "Diff arguments", false)) });
            schemas.Add(new() { Name = "git_log", Description = "Show commit history", InputSchema = MakeSchema(("args", "string", "Log arguments", false)) });
            schemas.Add(new() { Name = "git_branch", Description = "List or create branches", InputSchema = MakeSchema(("args", "string", "Branch arguments", false)) });
            schemas.Add(new() { Name = "git_push", Description = "Push to remote", InputSchema = MakeSchema(("remote", "string", "Remote name", false), ("branch", "string", "Branch name", false)) });
            schemas.Add(new() { Name = "git_pull", Description = "Pull from remote", InputSchema = MakeSchema(("args", "string", "Pull arguments", false)) });
            schemas.Add(new() { Name = "git_checkout", Description = "Switch branches or restore files", InputSchema = MakeSchema(("target", "string", "Branch or file", true)) });
            schemas.Add(new() { Name = "git_stash", Description = "Stash changes", InputSchema = MakeSchema(("action", "string", "push|pop|list|drop", false)) });
            schemas.Add(new() { Name = "git_clone", Description = "Clone a repository", InputSchema = MakeSchema(("url", "string", "Repository URL", true), ("path", "string", "Target directory", false)) });
            schemas.Add(new() { Name = "git_init", Description = "Initialize a git repository", InputSchema = MakeSchema() });
            schemas.Add(new() { Name = "git_worktree_create", Description = "Create an isolated git worktree", InputSchema = MakeSchema(("branch", "string", "Branch name", true), ("path", "string", "Worktree path", true)) });
            schemas.Add(new() { Name = "git_worktree_remove", Description = "Remove a git worktree", InputSchema = MakeSchema(("path", "string", "Worktree path", true)) });
        }

        // GitHub tools
        if (githubEnabled)
        {
            schemas.Add(new() { Name = "gh_pr_create", Description = "Create a GitHub pull request", InputSchema = MakeSchema(("title", "string", "PR title", true), ("body", "string", "PR body", false), ("base", "string", "Base branch", false)) });
            schemas.Add(new() { Name = "gh_pr_list", Description = "List pull requests", InputSchema = MakeSchema() });
            schemas.Add(new() { Name = "gh_issue_create", Description = "Create a GitHub issue", InputSchema = MakeSchema(("title", "string", "Issue title", true), ("body", "string", "Issue body", false)) });
            schemas.Add(new() { Name = "gh_issue_list", Description = "List issues", InputSchema = MakeSchema() });
        }

        // Web tools
        if (features.WebFetch && _activationService.IsFeatureUnlocked("feature.webFetch"))
        {
            schemas.Add(new() { Name = "web_fetch", Description = "Fetch a URL", InputSchema = MakeSchema(("url", "string", "URL to fetch", true)) });
            schemas.Add(new() { Name = "web_search", Description = "Search the web", InputSchema = MakeSchema(("query", "string", "Search query", true)) });
        }

        // REPL
        schemas.Add(new() { Name = "repl", Description = "Run code in a persistent REPL session (Python/Node.js)",
            InputSchema = MakeSchema(("language", "string", "python or node", true), ("code", "string", "Code to execute", true), ("action", "string", "exec or close", false)) });

        // Task management
        if (features.TaskManager)
        {
            schemas.Add(new() { Name = "task_create", Description = "Start a background command", InputSchema = MakeSchema(("command", "string", "Shell command", true)) });
            schemas.Add(new() { Name = "task_list", Description = "List background tasks", InputSchema = MakeSchema() });
            schemas.Add(new() { Name = "task_stop", Description = "Stop a background task", InputSchema = MakeSchema(("task_id", "string", "Task ID", true)) });
            schemas.Add(new() { Name = "task_output", Description = "Get task output", InputSchema = MakeSchema(("task_id", "string", "Task ID", true)) });
        }

        // Meta tools
        schemas.Add(new() { Name = "todo_write", Description = "Update the task/todo list", InputSchema = MakeSchema(("content", "string", "Todo item", true), ("status", "string", "pending|in_progress|completed", false)) });
        schemas.Add(new() { Name = "plan_mode", Description = "Create an execution plan before starting work", InputSchema = MakeSchema(("plan", "string", "The plan content", true)) });
        schemas.Add(new() { Name = "ask_user", Description = "Ask the user a question for clarification", InputSchema = MakeSchema(("question", "string", "Question to ask", true), ("options", "string", "Comma-separated options", false)) });
        schemas.Add(new() { Name = "powershell", Description = "Execute a PowerShell command", InputSchema = MakeSchema(("command", "string", "PowerShell command", true)) });
        schemas.Add(new() { Name = "notebook_edit", Description = "Edit a Jupyter notebook cell", InputSchema = MakeSchema(("notebook_path", "string", "Path to .ipynb file", true), ("cell_number", "string", "Cell index (0-based)", true), ("new_source", "string", "New cell content", true), ("edit_mode", "string", "replace|insert|delete", false), ("cell_type", "string", "code|markdown", false)) });
        schemas.Add(new() { Name = "config", Description = "View CluadeX configuration", InputSchema = MakeSchema(("action", "string", "get", false), ("key", "string", "Config key", false)) });
        schemas.Add(new() { Name = "agent_spawn", Description = "Spawn a sub-agent for a complex sub-task", InputSchema = MakeSchema(("task", "string", "Task description", true), ("context", "string", "Additional context", false)) });
        schemas.Add(new() { Name = "skill_invoke", Description = "Invoke a skill by name (e.g., commit, review-pr, simplify)", InputSchema = MakeSchema(("skill", "string", "Skill name to invoke", true), ("args", "string", "Arguments to pass to the skill", false)) });

        // Memory tools
        schemas.Add(new() { Name = "memory_save", Description = "Save persistent memory that survives across sessions", InputSchema = MakeSchema(("name", "string", "Memory name", true), ("content", "string", "Memory content", true), ("type", "string", "user|feedback|project|reference", false), ("description", "string", "Short description", false), ("scope", "string", "global|project", false)) });
        schemas.Add(new() { Name = "memory_list", Description = "List all saved memories", InputSchema = MakeSchema() });
        schemas.Add(new() { Name = "memory_delete", Description = "Delete a saved memory", InputSchema = MakeSchema(("name", "string", "Memory name to delete", true), ("scope", "string", "global|project", false)) });

        // MCP server tools (dynamically discovered)
        if (features.McpServers)
        {
            foreach (var mcpTool in _mcpManager.ToolRegistry.GetAllTools())
            {
                // Convert MCP tool's JSON Schema to native format
                var paramsList = new List<(string name, string type, string desc, bool required)>();
                if (mcpTool.InputSchema.HasValue)
                {
                    try
                    {
                        var schema = mcpTool.InputSchema.Value;
                        var props = schema.GetProperty("properties");
                        var reqList = schema.TryGetProperty("required", out var reqEl)
                            ? reqEl.EnumerateArray().Select(e => e.GetString() ?? "").ToHashSet()
                            : new HashSet<string>();

                        foreach (var prop in props.EnumerateObject())
                        {
                            string pType = prop.Value.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "string" : "string";
                            string pDesc = prop.Value.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? "" : "";
                            paramsList.Add((prop.Name, pType, pDesc, reqList.Contains(prop.Name)));
                        }
                    }
                    catch { /* skip malformed schemas */ }
                }
                schemas.Add(new() { Name = mcpTool.QualifiedName, Description = mcpTool.Description ?? mcpTool.Name, InputSchema = MakeSchema(paramsList.ToArray()) });
            }
        }

        return schemas;
    }

    // ─── Get Tool Definitions Prompt (feature-aware) ───
    public string GetToolDefinitionsPrompt()
    {
        var features = _settingsService.Settings.Features;
        bool gitEnabled = features.GitIntegration && _activationService.IsFeatureUnlocked("feature.git");
        bool githubEnabled = features.GitHubIntegration && _activationService.IsFeatureUnlocked("feature.github");

        var sb = new StringBuilder();
        sb.AppendLine("""
            AVAILABLE TOOLS:
            You can use tools to interact with the user's file system and project.
            To use a tool, write a tool call block in this exact format:

            [ACTION: tool_name]
            key: value
            [/ACTION]

            Available tools:

            1. read_file - Read the contents of a file
               [ACTION: read_file]
               path: relative/path/to/file.ext
               [/ACTION]

            2. write_file - Create or overwrite a file with new content
               [ACTION: write_file]
               path: relative/path/to/file.ext
               content:
               (file content here, everything after "content:" until [/ACTION])
               [/ACTION]

            3. edit_file - Find and replace text in an existing file
               [ACTION: edit_file]
               path: relative/path/to/file.ext
               find: (exact text to find)
               replace: (replacement text)
               [/ACTION]

            4. list_files - List files and directories
               [ACTION: list_files]
               path: .
               [/ACTION]

            5. search_files - Find files matching a glob pattern
               [ACTION: search_files]
               pattern: *.cs
               path: .
               [/ACTION]

            6. search_content - Search for text in file contents (like grep)
               [ACTION: search_content]
               query: search text
               path: .
               pattern: *.cs
               [/ACTION]

            7. run_command - Execute a shell command
               [ACTION: run_command]
               command: dotnet build
               [/ACTION]

            8. create_directory - Create a new directory
               [ACTION: create_directory]
               path: new/directory/path
               [/ACTION]
            """);

        if (gitEnabled)
        {
            sb.AppendLine("""

            GIT TOOLS:

            9. git_status - Show working tree status
               [ACTION: git_status]
               [/ACTION]

            10. git_add - Stage files for commit
                [ACTION: git_add]
                path: .
                [/ACTION]

            11. git_commit - Commit staged changes
                [ACTION: git_commit]
                message: your commit message here
                [/ACTION]

            12. git_push - Push commits to remote
                [ACTION: git_push]
                [/ACTION]

            13. git_pull - Pull changes from remote
                [ACTION: git_pull]
                [/ACTION]

            14. git_branch - List or create branches
                [ACTION: git_branch]
                action: list
                [/ACTION]
                Or create: action: create, name: feature-branch

            15. git_checkout - Switch branches
                [ACTION: git_checkout]
                branch: main
                [/ACTION]

            16. git_diff - Show changes
                [ACTION: git_diff]
                staged: false
                [/ACTION]

            17. git_log - Show commit history
                [ACTION: git_log]
                count: 10
                [/ACTION]

            18. git_clone - Clone a repository
                [ACTION: git_clone]
                url: https://github.com/owner/repo.git
                [/ACTION]

            19. git_init - Initialize a new git repository
                [ACTION: git_init]
                [/ACTION]

            20. git_stash - Stash/unstash changes
                [ACTION: git_stash]
                action: push
                message: WIP
                [/ACTION]
                Or pop: action: pop
            """);
        }

        if (githubEnabled)
        {
            sb.AppendLine("""

            GITHUB TOOLS (requires gh CLI):

            21. gh_pr_create - Create a Pull Request
                [ACTION: gh_pr_create]
                title: PR title
                body: PR description
                base: main
                [/ACTION]

            22. gh_pr_list - List Pull Requests
                [ACTION: gh_pr_list]
                state: open
                [/ACTION]

            23. gh_issue_create - Create an Issue
                [ACTION: gh_issue_create]
                title: Issue title
                body: Issue description
                [/ACTION]

            24. gh_issue_list - List Issues
                [ACTION: gh_issue_list]
                state: open
                [/ACTION]

            25. gh_repo_view - View repository info
                [ACTION: gh_repo_view]
                [/ACTION]
            """);
        }

        // Web tools (always available if feature unlocked)
        if (_activationService.IsFeatureUnlocked("feature.webFetch")
            && _settingsService.Settings.Features.WebFetch)
        {
            sb.AppendLine("""

            WEB TOOLS:

            26. web_fetch - Fetch a URL and get text content
                [ACTION: web_fetch]
                url: https://example.com/api/data
                [/ACTION]

            27. web_search - Search the web and get results
                [ACTION: web_search]
                query: how to implement binary search in C#
                max_results: 5
                [/ACTION]
            """);
        }

        // REPL tool (always available)
        sb.AppendLine("""

            INTERACTIVE REPL:

            28. repl - Execute code in a persistent Python or Node.js REPL session
                State persists between calls (variables, imports, etc.)
                [ACTION: repl]
                language: python
                code:
                import math
                print(math.sqrt(144))
                [/ACTION]
                Or Node.js:
                [ACTION: repl]
                language: node
                code: console.log(Array.from({length: 5}, (_, i) => i * 2))
                [/ACTION]
                Close session: action: close, language: python
        """);

        // Task management tools
        if (_settingsService.Settings.Features.TaskManager)
        {
            sb.AppendLine("""

            TASK MANAGEMENT (Background Jobs):

            29. task_create - Start a background task (long-running commands)
                [ACTION: task_create]
                name: Run tests
                command: dotnet test
                [/ACTION]

            30. task_list - List all background tasks and their status
                [ACTION: task_list]
                [/ACTION]

            31. task_stop - Stop a running background task
                [ACTION: task_stop]
                id: 1
                [/ACTION]

            32. task_output - Get the output of a background task
                [ACTION: task_output]
                id: 1
                [/ACTION]
            """);
        }

        // Agent meta-tools (always available)
        sb.AppendLine("""

            AGENT META-TOOLS:

            33. todo_write - Manage a TODO list for tracking progress
                [ACTION: todo_write]
                action: add
                content: Implement authentication system
                [/ACTION]
                Actions: add, complete (index: N or content: text), list, clear

            34. plan_mode - Create an execution plan before implementing
                [ACTION: plan_mode]
                action: create
                plan:
                1. Read existing code
                2. Design the solution
                3. Implement changes
                4. Verify and test
                [/ACTION]

            35. agent_spawn - Spawn a sub-agent for parallel or complex subtasks
                [ACTION: agent_spawn]
                task: Research the best sorting algorithm for this dataset
                context: The dataset has 10M records with mostly sorted data
                [/ACTION]
        """);

        // Phase 3: New tools
        sb.AppendLine("""

            38. glob - Find files matching glob patterns (faster than search_files for patterns)
                [ACTION: glob]
                pattern: **/*.cs
                path: src
                [/ACTION]

            39. grep_search - Regex-based content search with context lines
                [ACTION: grep_search]
                pattern: class\s+\w+Service
                path: .
                glob: *.cs
                output_mode: content
                context: 2
                ignore_case: true
                [/ACTION]
                output_mode options: content (shows matching lines), files_with_matches (file paths only), count

            40. ask_user - Ask the user a question when you need clarification
                [ACTION: ask_user]
                question: Which database should we use?
                options: PostgreSQL, SQLite, MySQL
                [/ACTION]

            41. config - View CluadeX configuration settings
                [ACTION: config]
                action: get
                key: language
                [/ACTION]

            42. notebook_edit - Edit Jupyter notebook (.ipynb) cells
                [ACTION: notebook_edit]
                notebook_path: analysis.ipynb
                cell_number: 2
                edit_mode: replace
                cell_type: code
                new_source: import pandas as pd
                [/ACTION]
                edit_mode options: replace, insert, delete

            43. powershell - Execute PowerShell commands (Windows)
                [ACTION: powershell]
                command: Get-Process | Sort-Object CPU -Descending | Select-Object -First 5
                timeout: 30000
                [/ACTION]

            44. memory_save - Save persistent memory (survives across sessions)
                [ACTION: memory_save]
                name: user_preference
                type: user
                description: User's coding preferences
                content: User prefers TypeScript with strict mode, uses pnpm
                scope: global
                [/ACTION]
                type options: user, feedback, project, reference
                scope options: global (all projects), project (this project only)

            45. memory_list - List all saved memories
                [ACTION: memory_list]
                [/ACTION]

            46. memory_delete - Delete a saved memory
                [ACTION: memory_delete]
                name: user_preference
                scope: global
                [/ACTION]

            47. skill_invoke - Invoke a skill (reusable prompt template) by name
                [ACTION: skill_invoke]
                skill: commit
                args: fix login bug
                [/ACTION]
                Available skills: commit, review-pr, simplify (+ any user/project skills)
        """);

        // MCP server tools (dynamically discovered)
        if (_settingsService.Settings.Features.McpServers)
        {
            string mcpPrompt = _mcpManager.ToolRegistry.GetToolDefinitionsPrompt();
            if (!string.IsNullOrEmpty(mcpPrompt))
                sb.AppendLine(mcpPrompt);
        }

        sb.AppendLine("""

            RULES:
            - You can use multiple tools in a single response
            - Always read a file before editing it
            - Use edit_file for small changes, write_file for creating new files or full rewrites
            - Explain what you're doing before and after using tools
            - Paths are relative to the project root
            - After making changes, verify them by reading the file or running tests
            - For complex tasks, use plan_mode first to outline your approach
            - Use todo_write to track progress on multi-step tasks
            """);

        if (gitEnabled)
        {
            sb.AppendLine("""
            - For MCP tools, use the fully qualified name (mcp__server__tool)
            - Use git tools to manage version control
            - Always check git_status before committing
            - Write meaningful commit messages

            GIT WORKTREE (isolated branches):

            36. git_worktree_create - Create an isolated worktree for a new branch
                [ACTION: git_worktree_create]
                branch: feature-x
                path: ../.worktrees/feature-x
                [/ACTION]

            37. git_worktree_remove - Remove a worktree when done
                [ACTION: git_worktree_remove]
                path: ../.worktrees/feature-x
                [/ACTION]
            """);
        }

        return sb.ToString();
    }

    /// <summary>Check if a tool type is allowed by current feature settings.</summary>
    private bool IsToolAllowed(ToolType type)
    {
        var features = _settingsService.Settings.Features;
        return type switch
        {
            // Git tools — require Git feature enabled + activation
            ToolType.GitStatus or ToolType.GitAdd or ToolType.GitCommit or
            ToolType.GitPush or ToolType.GitPull or ToolType.GitBranch or
            ToolType.GitCheckout or ToolType.GitDiff or ToolType.GitLog or
            ToolType.GitClone or ToolType.GitInit or ToolType.GitStash
                => features.GitIntegration && _activationService.IsFeatureUnlocked("feature.git"),

            // GitHub tools — require GitHub feature enabled + activation
            ToolType.GhPrCreate or ToolType.GhPrList or
            ToolType.GhIssueCreate or ToolType.GhIssueList or ToolType.GhRepoView
                => features.GitHubIntegration && _activationService.IsFeatureUnlocked("feature.github"),

            // Web tools — require activation
            ToolType.WebFetch or ToolType.WebSearch
                => features.WebFetch && _activationService.IsFeatureUnlocked("feature.webFetch"),

            // Task management — require TaskManager feature
            ToolType.TaskCreate or ToolType.TaskList or ToolType.TaskStop or ToolType.TaskOutput
                => features.TaskManager,

            // REPL — always available (core tool)
            ToolType.Repl => true,

            // Worktree — requires Git
            ToolType.GitWorktreeCreate or ToolType.GitWorktreeRemove
                => features.GitIntegration && _activationService.IsFeatureUnlocked("feature.git"),

            // Agent sub-task — always available
            ToolType.AgentSpawn => true,

            // MCP tools — require MCP feature enabled
            ToolType.McpTool => features.McpServers,

            // Agent meta-tools (todo, plan) — always available
            ToolType.TodoWrite or ToolType.PlanMode => true,

            // Phase 3: New tools — always available (core tools)
            ToolType.GlobSearch or ToolType.Grep or ToolType.AskUser
            or ToolType.Config or ToolType.NotebookEdit => true,

            // PowerShell — always available on Windows
            ToolType.PowerShell => true,

            // Phase 4: Skills — always available
            ToolType.SkillInvoke => true,

            // Memory tools — always available
            ToolType.MemorySave or ToolType.MemoryList or ToolType.MemoryDelete => true,

            // File/code tools — always available (core features)
            _ => true,
        };
    }

    // ════════════════════════════════════════════
    // Tool Implementations
    // ════════════════════════════════════════════

    private ToolResult ExecuteReadFile(ToolCall call)
    {
        string path = call.GetArg("path");
        if (string.IsNullOrEmpty(path))
            return Fail(call, "Missing 'path' argument");

        string content = _fileSystem.ReadFile(path);
        int lineCount = content.Split('\n').Length;

        return new ToolResult
        {
            Type = call.Type,
            ToolName = call.ToolName,
            Success = true,
            Output = content,
            Summary = $"Read {path} ({lineCount} lines)",
        };
    }

    private ToolResult ExecuteWriteFile(ToolCall call)
    {
        string path = call.GetArg("path");
        string content = call.GetArg("content");
        if (string.IsNullOrEmpty(path))
            return Fail(call, "Missing 'path' argument");

        _fileSystem.WriteFile(path, content);

        return new ToolResult
        {
            Type = call.Type,
            ToolName = call.ToolName,
            Success = true,
            Output = $"File written: {path} ({content.Length} bytes)",
            Summary = $"Wrote {path}",
        };
    }

    private ToolResult ExecuteEditFile(ToolCall call)
    {
        string path = call.GetArg("path");
        string find = call.GetArg("find");
        string replace = call.GetArg("replace");

        if (string.IsNullOrEmpty(path))
            return Fail(call, "Missing 'path' argument");
        if (string.IsNullOrEmpty(find))
            return Fail(call, "Missing 'find' argument");

        var (found, replacements) = _fileSystem.EditFile(path, find, replace);

        if (!found)
        {
            return new ToolResult
            {
                Type = call.Type,
                ToolName = call.ToolName,
                Success = false,
                Error = $"Text not found in {path}. Make sure 'find' matches exactly.",
                Summary = $"Edit failed: text not found in {path}",
            };
        }

        return new ToolResult
        {
            Type = call.Type,
            ToolName = call.ToolName,
            Success = true,
            Output = $"Edited {path}: {replacements} replacement(s) made",
            Summary = $"Edited {path} ({replacements} changes)",
        };
    }

    private ToolResult ExecuteListFiles(ToolCall call)
    {
        string path = call.GetArg("path", ".");
        var entries = _fileSystem.ListDirectory(path, 2);

        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            if (e.IsDirectory)
                sb.AppendLine($"  {e.RelativePath}/");
            else
                sb.AppendLine($"  {e.RelativePath} ({FormatSize(e.SizeBytes)})");
        }

        return new ToolResult
        {
            Type = call.Type,
            ToolName = call.ToolName,
            Success = true,
            Output = sb.ToString().TrimEnd(),
            Summary = $"Listed {entries.Count} items in {path}",
        };
    }

    private ToolResult ExecuteSearchFiles(ToolCall call)
    {
        string pattern = call.GetArg("pattern", "*.*");
        string path = call.GetArg("path", ".");

        var results = _fileSystem.SearchFiles(pattern, path);

        var sb = new StringBuilder();
        foreach (var r in results)
            sb.AppendLine($"  {r}");

        return new ToolResult
        {
            Type = call.Type,
            ToolName = call.ToolName,
            Success = true,
            Output = results.Count > 0
                ? sb.ToString().TrimEnd()
                : "No files found matching pattern.",
            Summary = $"Found {results.Count} files matching '{pattern}'",
        };
    }

    private ToolResult ExecuteSearchContent(ToolCall call)
    {
        string query = call.GetArg("query");
        string path = call.GetArg("path", ".");
        string filePattern = call.GetArg("pattern", "*.*");

        if (string.IsNullOrEmpty(query))
            return Fail(call, "Missing 'query' argument");

        var results = _fileSystem.SearchContent(query, path, filePattern);

        var sb = new StringBuilder();
        foreach (var r in results)
            sb.AppendLine($"  {r.FilePath}:{r.LineNumber}: {r.LineContent}");

        return new ToolResult
        {
            Type = call.Type,
            ToolName = call.ToolName,
            Success = true,
            Output = results.Count > 0
                ? sb.ToString().TrimEnd()
                : "No matches found.",
            Summary = $"Found {results.Count} matches for '{query}'",
        };
    }

    private async Task<ToolResult> ExecuteRunCommandAsync(ToolCall call, CancellationToken ct)
    {
        string command = call.GetArg("command");
        if (string.IsNullOrEmpty(command))
            return Fail(call, "Missing 'command' argument");

        // Security: block dangerous commands with comprehensive patterns
        string cmdLower = command.ToLowerInvariant().Trim();
        string[] blockedPatterns = [
            "rm -rf /", "rm -rf ~", "rm -rf .",
            "format ", "format\t",
            "del /s", "del /f", "del /q",
            "rmdir /s", "rd /s",
            "remove-item -recurse",
            "shutdown", "restart-computer",
            "reg delete", "reg add",
            "net user", "net localgroup",
            "takeown", "icacls",
            "mkfs.", "dd if=",
            "> /dev/", ">/dev/",
        ];
        // Also block shell escape patterns
        string[] blockedContains = [
            "| rm ", "| del ", "&& rm ", "&& del ",
            "; rm ", "; del ", "` rm ", "` del ",
            "invoke-webrequest", "invoke-restmethod",
            "start-bitstransfer",
            "certutil -urlcache",
            "bitsadmin /transfer",
        ];
        foreach (var blocked in blockedPatterns)
        {
            if (cmdLower.StartsWith(blocked))
                return Fail(call, $"Command blocked for safety: {command}");
        }
        foreach (var blocked in blockedContains)
        {
            if (cmdLower.Contains(blocked))
                return Fail(call, $"Command blocked for safety: {command}");
        }

        try
        {
            string fileName, arguments;
            if (OperatingSystem.IsWindows())
            {
                fileName = "cmd.exe";
                arguments = $"/c {command}";
            }
            else
            {
                fileName = "/bin/bash";
                arguments = $"-c \"{command.Replace("\"", "\\\"")}\"";
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = _fileSystem.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
                return Fail(call, "Failed to start process");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(30000); // 30s timeout

            string stdout, stderr;
            try
            {
                await process.WaitForExitAsync(cts.Token);
                stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
                stderr = await process.StandardError.ReadToEndAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                return Fail(call, "Command timed out (30s limit)");
            }

            string output = stdout;
            if (!string.IsNullOrEmpty(stderr))
                output += (string.IsNullOrEmpty(output) ? "" : "\n") + stderr;

            // Truncate long output
            if (output.Length > 5000)
                output = output[..5000] + "\n... (truncated)";

            bool success = process.ExitCode == 0;

            return new ToolResult
            {
                Type = call.Type,
                ToolName = call.ToolName,
                Success = success,
                Output = output,
                Error = success ? "" : $"Exit code: {process.ExitCode}",
                Summary = success
                    ? $"Command OK: {command}"
                    : $"Command failed (exit {process.ExitCode}): {command}",
            };
        }
        catch (Exception ex)
        {
            return Fail(call, $"Failed to execute: {ex.Message}");
        }
    }

    private ToolResult ExecuteCreateDirectory(ToolCall call)
    {
        string path = call.GetArg("path");
        if (string.IsNullOrEmpty(path))
            return Fail(call, "Missing 'path' argument");

        _fileSystem.CreateDirectory(path);

        return new ToolResult
        {
            Type = call.Type,
            ToolName = call.ToolName,
            Success = true,
            Output = $"Directory created: {path}",
            Summary = $"Created directory {path}",
        };
    }

    // ════════════════════════════════════════════
    // Git Tool Implementations
    // ════════════════════════════════════════════

    private async Task<ToolResult> ExecuteGitStatusAsync(ToolCall call)
    {
        var r = await _gitService.StatusAsync();
        return GitToToolResult(call, r, "Git status");
    }

    private async Task<ToolResult> ExecuteGitAddAsync(ToolCall call)
    {
        string path = call.GetArg("path", ".");
        var r = await _gitService.AddAsync(path);
        return GitToToolResult(call, r, $"Staged: {path}");
    }

    private async Task<ToolResult> ExecuteGitCommitAsync(ToolCall call)
    {
        string message = call.GetArg("message");
        if (string.IsNullOrEmpty(message))
            return Fail(call, "Missing 'message' argument");
        var r = await _gitService.CommitAsync(message);
        return GitToToolResult(call, r, $"Committed: {message}");
    }

    private async Task<ToolResult> ExecuteGitPushAsync(ToolCall call)
    {
        string remote = call.GetArg("remote", "");
        string branch = call.GetArg("branch", "");
        bool setUpstream = call.GetArg("set_upstream", "false") == "true";

        GitResult r;
        if (setUpstream)
            r = await _gitService.PushSetUpstreamAsync(
                string.IsNullOrEmpty(remote) ? "origin" : remote,
                string.IsNullOrEmpty(branch) ? null : branch);
        else
            r = await _gitService.PushAsync(remote, branch);
        return GitToToolResult(call, r, "Pushed to remote");
    }

    private async Task<ToolResult> ExecuteGitPullAsync(ToolCall call)
    {
        string remote = call.GetArg("remote", "");
        string branch = call.GetArg("branch", "");
        var r = await _gitService.PullAsync(remote, branch);
        return GitToToolResult(call, r, "Pulled from remote");
    }

    private async Task<ToolResult> ExecuteGitBranchAsync(ToolCall call)
    {
        string action = call.GetArg("action", "list").ToLowerInvariant();
        string name = call.GetArg("name", "");

        GitResult r;
        switch (action)
        {
            case "create":
                if (string.IsNullOrEmpty(name))
                    return Fail(call, "Missing 'name' argument for branch creation");
                r = await _gitService.CreateBranchAsync(name);
                return GitToToolResult(call, r, $"Created branch: {name}");

            case "delete":
                if (string.IsNullOrEmpty(name))
                    return Fail(call, "Missing 'name' argument for branch deletion");
                r = await _gitService.DeleteBranchAsync(name);
                return GitToToolResult(call, r, $"Deleted branch: {name}");

            default: // list
                r = await _gitService.ListBranchesAsync();
                return GitToToolResult(call, r, "Listed branches");
        }
    }

    private async Task<ToolResult> ExecuteGitCheckoutAsync(ToolCall call)
    {
        string branch = call.GetArg("branch");
        if (string.IsNullOrEmpty(branch))
            return Fail(call, "Missing 'branch' argument");
        var r = await _gitService.CheckoutAsync(branch);
        return GitToToolResult(call, r, $"Switched to: {branch}");
    }

    private async Task<ToolResult> ExecuteGitDiffAsync(ToolCall call)
    {
        string path = call.GetArg("path", "");
        bool staged = call.GetArg("staged", "false").ToLowerInvariant() == "true";

        GitResult r = staged
            ? await _gitService.DiffStagedAsync(path)
            : await _gitService.DiffAsync(path);
        return GitToToolResult(call, r, staged ? "Staged diff" : "Unstaged diff");
    }

    private async Task<ToolResult> ExecuteGitLogAsync(ToolCall call)
    {
        int count = 10;
        if (int.TryParse(call.GetArg("count", "10"), out int c)) count = c;
        string file = call.GetArg("file", "");

        GitResult r;
        if (!string.IsNullOrEmpty(file))
            r = await _gitService.LogFileAsync(file, count);
        else
            r = await _gitService.LogAsync(count);
        return GitToToolResult(call, r, $"Git log ({count} entries)");
    }

    private async Task<ToolResult> ExecuteGitCloneAsync(ToolCall call, CancellationToken ct)
    {
        string url = call.GetArg("url");
        string dir = call.GetArg("directory", "");
        if (string.IsNullOrEmpty(url))
            return Fail(call, "Missing 'url' argument");

        var r = string.IsNullOrEmpty(dir)
            ? await _gitHubService.CloneRepoAsync(url, null, ct)
            : await _gitHubService.CloneRepoAsync(url, dir, ct);
        return GitToToolResult(call, r, $"Cloned: {url}");
    }

    private async Task<ToolResult> ExecuteGitInitAsync(ToolCall call)
    {
        var r = await _gitService.InitAsync();
        return GitToToolResult(call, r, "Initialized git repository");
    }

    private async Task<ToolResult> ExecuteGitStashAsync(ToolCall call)
    {
        string action = call.GetArg("action", "push").ToLowerInvariant();
        string message = call.GetArg("message", "");

        GitResult r = action switch
        {
            "pop" => await _gitService.StashPopAsync(),
            "list" => await _gitService.StashListAsync(),
            _ => await _gitService.StashAsync(message),
        };
        return GitToToolResult(call, r, $"Stash {action}");
    }

    // ════════════════════════════════════════════
    // GitHub Tool Implementations
    // ════════════════════════════════════════════

    private async Task<ToolResult> ExecuteGhPrCreateAsync(ToolCall call)
    {
        string title = call.GetArg("title");
        string body = call.GetArg("body", "");
        string baseBranch = call.GetArg("base", "main");

        if (string.IsNullOrEmpty(title))
            return Fail(call, "Missing 'title' argument");

        var r = await _gitHubService.CreatePullRequestAsync(title, body, baseBranch);
        return GitToToolResult(call, r, $"Created PR: {title}");
    }

    private async Task<ToolResult> ExecuteGhPrListAsync(ToolCall call)
    {
        string state = call.GetArg("state", "open");
        int limit = 10;
        if (int.TryParse(call.GetArg("limit", "10"), out int l)) limit = l;

        var r = await _gitHubService.ListPullRequestsAsync(state, limit);
        return GitToToolResult(call, r, $"Listed PRs ({state})");
    }

    private async Task<ToolResult> ExecuteGhIssueCreateAsync(ToolCall call)
    {
        string title = call.GetArg("title");
        string body = call.GetArg("body", "");

        if (string.IsNullOrEmpty(title))
            return Fail(call, "Missing 'title' argument");

        var r = await _gitHubService.CreateIssueAsync(title, body);
        return GitToToolResult(call, r, $"Created issue: {title}");
    }

    private async Task<ToolResult> ExecuteGhIssueListAsync(ToolCall call)
    {
        string state = call.GetArg("state", "open");
        int limit = 10;
        if (int.TryParse(call.GetArg("limit", "10"), out int l)) limit = l;

        var r = await _gitHubService.ListIssuesAsync(state, limit);
        return GitToToolResult(call, r, $"Listed issues ({state})");
    }

    private async Task<ToolResult> ExecuteGhRepoViewAsync(ToolCall call)
    {
        var r = await _gitHubService.ViewRepoAsync();
        return GitToToolResult(call, r, "Repo info");
    }

    /// <summary>Convert a GitResult to a ToolResult.</summary>
    private static ToolResult GitToToolResult(ToolCall call, GitResult gitResult, string summary)
    {
        return new ToolResult
        {
            Type = call.Type,
            ToolName = call.ToolName,
            Success = gitResult.Success,
            Output = gitResult.Success ? gitResult.Output : gitResult.FullOutput,
            Error = gitResult.Success ? "" : gitResult.Error,
            Summary = gitResult.Success ? summary : $"Failed: {summary}",
        };
    }

    // ════════════════════════════════════════════
    // Web Fetch Tool
    // ════════════════════════════════════════════

    private async Task<ToolResult> ExecuteWebFetchAsync(ToolCall call, CancellationToken ct)
    {
        string url = call.GetArg("url");
        if (string.IsNullOrEmpty(url))
            return Fail(call, "Missing 'url' argument");

        try
        {
            string content = await _webFetchService.FetchAsync(url, ct);
            return new ToolResult
            {
                Type = call.Type,
                ToolName = call.ToolName,
                Success = true,
                Output = content.Length > 4000 ? content[..4000] + "\n... (truncated)" : content,
                Summary = $"Fetched {url} ({content.Length} chars)",
            };
        }
        catch (Exception ex)
        {
            return Fail(call, $"Fetch failed: {ex.Message}");
        }
    }

    // ════════════════════════════════════════════
    // TODO & Plan Mode Tools
    // ════════════════════════════════════════════

    private ToolResult ExecuteTodoWrite(ToolCall call)
    {
        string action = call.GetArg("action", "add");
        string content = call.GetArg("content", call.GetArg("text", call.GetArg("item")));
        string status = call.GetArg("status", "pending");

        switch (action.ToLowerInvariant())
        {
            case "add":
                if (string.IsNullOrEmpty(content))
                    return Fail(call, "Missing 'content' for todo item");
                _todoItems.Add(new TodoItem { Content = content, Status = status });
                TodoChanged?.Invoke();
                return new ToolResult
                {
                    Type = call.Type, ToolName = call.ToolName, Success = true,
                    Output = $"Added: {content} [{status}]",
                    Summary = $"Added TODO: {content}",
                };

            case "complete" or "done":
                int idx = int.TryParse(call.GetArg("index", "-1"), out var i) ? i : -1;
                if (idx >= 0 && idx < _todoItems.Count)
                {
                    _todoItems[idx].Status = "completed";
                    TodoChanged?.Invoke();
                    return new ToolResult
                    {
                        Type = call.Type, ToolName = call.ToolName, Success = true,
                        Output = $"Completed: {_todoItems[idx].Content}",
                        Summary = $"Completed TODO #{idx}",
                    };
                }
                // Try matching by content
                var match = _todoItems.FirstOrDefault(t =>
                    t.Content.Contains(content ?? "", StringComparison.OrdinalIgnoreCase) && t.Status != "completed");
                if (match != null)
                {
                    match.Status = "completed";
                    TodoChanged?.Invoke();
                    return new ToolResult
                    {
                        Type = call.Type, ToolName = call.ToolName, Success = true,
                        Output = $"Completed: {match.Content}",
                        Summary = $"Completed TODO: {match.Content}",
                    };
                }
                return Fail(call, "TODO item not found");

            case "list":
                if (_todoItems.Count == 0)
                    return new ToolResult
                    {
                        Type = call.Type, ToolName = call.ToolName, Success = true,
                        Output = "No TODOs in list.",
                        Summary = "TODO list empty",
                    };
                var sb = new StringBuilder();
                for (int j = 0; j < _todoItems.Count; j++)
                {
                    var t = _todoItems[j];
                    string icon = t.Status == "completed" ? "✅" : t.Status == "in_progress" ? "🔄" : "⬜";
                    sb.AppendLine($"{j}. {icon} [{t.Status}] {t.Content}");
                }
                return new ToolResult
                {
                    Type = call.Type, ToolName = call.ToolName, Success = true,
                    Output = sb.ToString(),
                    Summary = $"TODO: {_todoItems.Count} items ({_todoItems.Count(t => t.Status == "completed")} done)",
                };

            case "clear":
                _todoItems.Clear();
                TodoChanged?.Invoke();
                return new ToolResult
                {
                    Type = call.Type, ToolName = call.ToolName, Success = true,
                    Output = "TODO list cleared.",
                    Summary = "Cleared TODO list",
                };

            default:
                return Fail(call, $"Unknown action: {action}. Use: add, complete, list, clear");
        }
    }

    private ToolResult ExecutePlanMode(ToolCall call)
    {
        string planContent = call.GetArg("plan", call.GetArg("content", ""));
        string action = call.GetArg("action", "create");

        if (action == "create" || !string.IsNullOrEmpty(planContent))
        {
            return new ToolResult
            {
                Type = call.Type,
                ToolName = call.ToolName,
                Success = true,
                Output = $"📋 PLAN:\n{planContent}\n\nI will now execute this plan step by step.",
                Summary = "Created execution plan",
            };
        }

        return new ToolResult
        {
            Type = call.Type,
            ToolName = call.ToolName,
            Success = true,
            Output = "Plan mode activated. Create a plan with action: create and plan: <your plan>",
            Summary = "Plan mode ready",
        };
    }

    // ════════════════════════════════════════════
    // Web Search Tool
    // ════════════════════════════════════════════

    private async Task<ToolResult> ExecuteWebSearchAsync(ToolCall call, CancellationToken ct)
    {
        string query = call.GetArg("query", call.GetArg("q"));
        if (string.IsNullOrEmpty(query))
            return Fail(call, "Missing 'query' argument");

        int maxResults = 10;
        if (int.TryParse(call.GetArg("max_results", "10"), out int mr)) maxResults = mr;

        try
        {
            string results = await _webFetchService.SearchAsync(query, maxResults, ct);
            return new ToolResult
            {
                Type = call.Type,
                ToolName = call.ToolName,
                Success = true,
                Output = results.Length > 4000 ? results[..4000] + "\n... (truncated)" : results,
                Summary = $"Web search: {query}",
            };
        }
        catch (Exception ex)
        {
            return Fail(call, $"Search failed: {ex.Message}");
        }
    }

    // ════════════════════════════════════════════
    // REPL Tool (Persistent Python/Node.js sessions)
    // ════════════════════════════════════════════

    private async Task<ToolResult> ExecuteReplAsync(ToolCall call, CancellationToken ct)
    {
        string language = call.GetArg("language", call.GetArg("lang", "python")).ToLowerInvariant();
        string code = call.GetArg("code", call.GetArg("input", call.GetArg("command")));
        string action = call.GetArg("action", "exec").ToLowerInvariant();

        if (action == "close" || action == "stop")
        {
            if (_replSessions.TryGetValue(language, out var existing))
            {
                existing.Dispose();
                _replSessions.Remove(language);
                return new ToolResult
                {
                    Type = call.Type, ToolName = call.ToolName, Success = true,
                    Output = $"{language} REPL session closed.",
                    Summary = $"Closed {language} REPL",
                };
            }
            return Fail(call, $"No active {language} REPL session");
        }

        if (string.IsNullOrEmpty(code))
            return Fail(call, "Missing 'code' argument");

        // Get or create REPL session
        if (!_replSessions.TryGetValue(language, out var session) || session.HasExited)
        {
            string exe = language switch
            {
                "python" or "py" => OperatingSystem.IsWindows() ? "python" : "python3",
                "node" or "nodejs" or "javascript" or "js" => "node",
                _ => language,
            };

            string args = language switch
            {
                "python" or "py" => "-i -u", // Interactive, unbuffered
                "node" or "nodejs" or "javascript" or "js" => "-i",
                _ => "",
            };

            try
            {
                session = new ReplSession(exe, args, _fileSystem.WorkingDirectory);
                _replSessions[language] = session;
            }
            catch (Exception ex)
            {
                return Fail(call, $"Failed to start {language} REPL: {ex.Message}");
            }
        }

        try
        {
            string output = await session.ExecuteAsync(code, ct, timeoutMs: 15000);

            // Truncate long output
            if (output.Length > 4000)
                output = output[..4000] + "\n... (truncated)";

            return new ToolResult
            {
                Type = call.Type,
                ToolName = call.ToolName,
                Success = true,
                Output = output,
                Summary = $"REPL ({language}): executed {code.Split('\n').Length} line(s)",
            };
        }
        catch (TimeoutException)
        {
            return Fail(call, "REPL execution timed out (15s limit). The session is still active.");
        }
        catch (Exception ex)
        {
            return Fail(call, $"REPL error: {ex.Message}");
        }
    }

    // ════════════════════════════════════════════
    // Task Management Tools (wrapping TaskManagerService)
    // ════════════════════════════════════════════

    private ToolResult ExecuteTaskCreate(ToolCall call)
    {
        string name = call.GetArg("name", call.GetArg("title", "Task"));
        string command = call.GetArg("command");
        if (string.IsNullOrEmpty(command))
            return Fail(call, "Missing 'command' argument");

        int taskId = _taskManager.CreateTask(name, command);
        return new ToolResult
        {
            Type = call.Type, ToolName = call.ToolName, Success = true,
            Output = $"Task #{taskId} created: {name}\nCommand: {command}\nStatus: running",
            Summary = $"Created task #{taskId}: {name}",
        };
    }

    private ToolResult ExecuteTaskList(ToolCall call)
    {
        var tasks = _taskManager.Tasks;
        if (tasks.Count == 0)
            return new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true,
                Output = "No tasks.",
                Summary = "No tasks",
            };

        var sb = new StringBuilder();
        foreach (var t in tasks)
        {
            string icon = t.Status switch { "running" => "🔄", "completed" => "✅", "failed" => "❌", "stopped" => "⏹", _ => "?" };
            sb.AppendLine($"{icon} #{t.Id} [{t.Status}] {t.Name} — {t.Command}");
        }

        return new ToolResult
        {
            Type = call.Type, ToolName = call.ToolName, Success = true,
            Output = sb.ToString().TrimEnd(),
            Summary = $"Listed {tasks.Count} task(s)",
        };
    }

    private ToolResult ExecuteTaskStop(ToolCall call)
    {
        if (!int.TryParse(call.GetArg("id", call.GetArg("task_id")), out int taskId))
            return Fail(call, "Missing or invalid 'id' argument");

        _taskManager.StopTask(taskId);

        return new ToolResult
        {
            Type = call.Type, ToolName = call.ToolName, Success = true,
            Output = $"Task #{taskId} stop requested.",
            Summary = $"Stopped task #{taskId}",
        };
    }

    private ToolResult ExecuteTaskOutput(ToolCall call)
    {
        if (!int.TryParse(call.GetArg("id", call.GetArg("task_id")), out int taskId))
            return Fail(call, "Missing or invalid 'id' argument");

        var task = _taskManager.Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task == null)
            return Fail(call, $"Task #{taskId} not found");

        string output = task.Output ?? "(no output yet)";
        if (output.Length > 4000)
            output = output[^4000..]; // Show last 4KB

        return new ToolResult
        {
            Type = call.Type, ToolName = call.ToolName, Success = true,
            Output = $"Task #{taskId} [{task.Status}]:\n{output}",
            Summary = $"Output of task #{taskId} ({task.Status})",
        };
    }

    // ════════════════════════════════════════════
    // Git Worktree Tools
    // ════════════════════════════════════════════

    private async Task<ToolResult> ExecuteGitWorktreeCreateAsync(ToolCall call)
    {
        string branch = call.GetArg("branch", call.GetArg("name"));
        string path = call.GetArg("path", "");

        if (string.IsNullOrEmpty(branch))
            return Fail(call, "Missing 'branch' argument");

        if (string.IsNullOrEmpty(path))
            path = $"../.worktrees/{branch}";

        var r = await _gitService.RunGitAsync($"worktree add \"{path}\" -b \"{branch}\"");
        return GitToToolResult(call, r, $"Created worktree: {path} (branch: {branch})");
    }

    private async Task<ToolResult> ExecuteGitWorktreeRemoveAsync(ToolCall call)
    {
        string path = call.GetArg("path", call.GetArg("name"));
        if (string.IsNullOrEmpty(path))
            return Fail(call, "Missing 'path' argument");

        var r = await _gitService.RunGitAsync($"worktree remove \"{path}\" --force");
        return GitToToolResult(call, r, $"Removed worktree: {path}");
    }

    // ════════════════════════════════════════════
    // Agent Sub-task Spawn
    // ════════════════════════════════════════════

    private async Task<ToolResult> ExecuteAgentSpawnAsync(ToolCall call, CancellationToken ct)
    {
        string task = call.GetArg("task", call.GetArg("prompt"));
        string context = call.GetArg("context", "");

        if (string.IsNullOrEmpty(task))
            return Fail(call, "Missing 'task' argument");

        if (OnAgentSpawnRequested == null)
            return Fail(call, "Agent spawning not available — no handler registered");

        try
        {
            string result = await OnAgentSpawnRequested.Invoke(task, context, ct);
            return new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true,
                Output = result.Length > 4000 ? result[..4000] + "\n... (truncated)" : result,
                Summary = $"Sub-agent completed: {task[..Math.Min(50, task.Length)]}",
            };
        }
        catch (Exception ex)
        {
            return Fail(call, $"Sub-agent failed: {ex.Message}");
        }
    }

    // ════════════════════════════════════════════
    // MCP Tool Execution
    // ════════════════════════════════════════════

    private async Task<ToolResult> ExecuteMcpToolAsync(ToolCall call, CancellationToken ct)
    {
        string serverName = call.McpServerName ?? "";
        string toolName = call.McpToolName ?? call.ToolName;

        if (string.IsNullOrEmpty(serverName))
        {
            // Try to resolve from qualified name
            var mcpTool = _mcpManager.ToolRegistry.ResolveTool(call.ToolName);
            if (mcpTool == null)
                return Fail(call, $"MCP tool not found: {call.ToolName}");
            serverName = mcpTool.ServerName;
            toolName = mcpTool.Name;
        }

        if (!_mcpManager.IsServerRunning(serverName))
            return Fail(call, $"MCP server '{serverName}' is not running. Start it first.");

        try
        {
            var result = await _mcpManager.CallToolAsync(serverName, toolName, call.Arguments, ct);
            string output = result.GetTextOutput();

            if (output.Length > 4000)
                output = output[..4000] + "\n... (truncated)";

            return new ToolResult
            {
                Type = call.Type,
                ToolName = call.ToolName,
                Success = !result.IsError,
                Output = output,
                Error = result.IsError ? output : "",
                Summary = result.IsError
                    ? $"MCP error ({serverName}/{toolName}): {output[..Math.Min(80, output.Length)]}"
                    : $"MCP ({serverName}/{toolName}): OK",
            };
        }
        catch (Exception ex)
        {
            return Fail(call, $"MCP call failed ({serverName}/{toolName}): {ex.Message}");
        }
    }

    // ════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════

    /// <summary>Public wrapper for ResolveToolType (used by native tool_use loop).</summary>
    public ToolType? ResolveToolTypePublic(string toolName) => ResolveToolType(toolName);

    private ToolType? ResolveToolType(string toolName)
    {
        // Check MCP tools first (qualified names: mcp__server__tool)
        if (toolName.StartsWith("mcp__") && _mcpManager.ToolRegistry.ResolveTool(toolName) != null)
            return ToolType.McpTool;

        return toolName switch
        {
            // File tools
            "read_file" or "readfile" => ToolType.ReadFile,
            "write_file" or "writefile" => ToolType.WriteFile,
            "edit_file" or "editfile" => ToolType.EditFile,
            "list_files" or "listfiles" or "list_directory" or "ls" => ToolType.ListFiles,
            "search_files" or "searchfiles" or "find_files" => ToolType.SearchFiles,
            "search_content" or "searchcontent" => ToolType.SearchContent,
            "run_command" or "runcommand" or "shell" or "exec" or "run" => ToolType.RunCommand,
            "create_directory" or "mkdir" or "create_dir" => ToolType.CreateDirectory,

            // Git tools
            "git_status" or "gitstatus" => ToolType.GitStatus,
            "git_add" or "gitadd" => ToolType.GitAdd,
            "git_commit" or "gitcommit" => ToolType.GitCommit,
            "git_push" or "gitpush" => ToolType.GitPush,
            "git_pull" or "gitpull" => ToolType.GitPull,
            "git_branch" or "gitbranch" => ToolType.GitBranch,
            "git_checkout" or "gitcheckout" => ToolType.GitCheckout,
            "git_diff" or "gitdiff" => ToolType.GitDiff,
            "git_log" or "gitlog" => ToolType.GitLog,
            "git_clone" or "gitclone" => ToolType.GitClone,
            "git_init" or "gitinit" => ToolType.GitInit,
            "git_stash" or "gitstash" => ToolType.GitStash,

            // GitHub tools
            "gh_pr_create" or "ghprcreate" or "pr_create" => ToolType.GhPrCreate,
            "gh_pr_list" or "ghprlist" or "pr_list" => ToolType.GhPrList,
            "gh_issue_create" or "ghissuecreate" or "issue_create" => ToolType.GhIssueCreate,
            "gh_issue_list" or "ghissuelist" or "issue_list" => ToolType.GhIssueList,
            "gh_repo_view" or "ghrepoview" or "repo_view" => ToolType.GhRepoView,

            // Web tools
            "web_fetch" or "webfetch" or "fetch_url" or "fetch" => ToolType.WebFetch,
            "web_search" or "websearch" or "search" or "search_web" => ToolType.WebSearch,

            // Interactive REPL
            "repl" or "python" or "node" or "repl_exec" => ToolType.Repl,

            // Task management tools
            "task_create" or "taskcreate" or "create_task" => ToolType.TaskCreate,
            "task_list" or "tasklist" or "list_tasks" => ToolType.TaskList,
            "task_stop" or "taskstop" or "stop_task" => ToolType.TaskStop,
            "task_output" or "taskoutput" or "get_task_output" => ToolType.TaskOutput,

            // Git worktree
            "git_worktree_create" or "worktree_create" or "enter_worktree" => ToolType.GitWorktreeCreate,
            "git_worktree_remove" or "worktree_remove" or "exit_worktree" => ToolType.GitWorktreeRemove,

            // Agent sub-task
            "agent_spawn" or "spawn_agent" or "sub_agent" => ToolType.AgentSpawn,

            // Agent meta-tools
            "todo_write" or "todowrite" or "todo" or "update_todo" => ToolType.TodoWrite,
            "plan_mode" or "planmode" or "plan" or "enter_plan" => ToolType.PlanMode,

            // Phase 3: New tools
            "glob" or "glob_search" or "search_glob" or "find_glob" => ToolType.GlobSearch,
            "grep" or "grep_search" or "grep_content" or "regex_search" => ToolType.Grep,
            "ask_user" or "ask_question" or "ask_user_question" => ToolType.AskUser,
            "config" or "get_config" or "set_config" => ToolType.Config,
            "notebook_edit" or "notebookedit" or "edit_notebook" => ToolType.NotebookEdit,
            "powershell" or "pwsh" or "ps" => ToolType.PowerShell,

            // Phase 4: Skills
            "skill" or "skill_invoke" or "invoke_skill" => ToolType.SkillInvoke,

            // Memory tools
            "memory_save" or "save_memory" or "remember" => ToolType.MemorySave,
            "memory_list" or "list_memories" or "memories" => ToolType.MemoryList,
            "memory_delete" or "delete_memory" or "forget" => ToolType.MemoryDelete,

            _ => null,
        };
    }

    private Dictionary<string, string> ParseArguments(string body, ToolType toolType)
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(body)) return args;

        // For write_file, handle "content:" specially since it can be multi-line
        if (toolType == ToolType.WriteFile)
        {
            return ParseWriteFileArgs(body);
        }

        // For edit_file, handle "find:" and "replace:" which can be multi-line
        if (toolType == ToolType.EditFile)
        {
            return ParseEditFileArgs(body);
        }

        // For REPL, handle "code:" specially since it can be multi-line
        if (toolType == ToolType.Repl)
        {
            return ParseReplArgs(body);
        }

        // For plan_mode, handle "plan:" multi-line
        if (toolType == ToolType.PlanMode)
        {
            return ParseWriteFileArgs(body.Replace("plan:", "content:"));
        }

        // For notebook_edit, handle "new_source:" as multi-line (cell content can span lines)
        if (toolType == ToolType.NotebookEdit)
        {
            return ParseNotebookEditArgs(body);
        }

        // Standard key: value parsing for single-line args
        foreach (var line in body.Split('\n'))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            int colonIdx = trimmed.IndexOf(':');
            if (colonIdx > 0)
            {
                string key = trimmed[..colonIdx].Trim().ToLowerInvariant();
                string value = trimmed[(colonIdx + 1)..].Trim();
                args[key] = value;
            }
        }

        return args;
    }

    private Dictionary<string, string> ParseWriteFileArgs(string body)
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = body.Split('\n');

        bool inContent = false;
        var contentBuilder = new StringBuilder();

        foreach (var line in lines)
        {
            if (inContent)
            {
                contentBuilder.AppendLine(line);
                continue;
            }

            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (trimmed.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
            {
                // Everything after "content:" is content
                string afterKey = trimmed["content:".Length..].TrimStart();
                if (!string.IsNullOrEmpty(afterKey))
                    contentBuilder.AppendLine(afterKey);
                inContent = true;
                continue;
            }

            int colonIdx = trimmed.IndexOf(':');
            if (colonIdx > 0)
            {
                string key = trimmed[..colonIdx].Trim().ToLowerInvariant();
                string value = trimmed[(colonIdx + 1)..].Trim();
                args[key] = value;
            }
        }

        if (contentBuilder.Length > 0)
        {
            args["content"] = contentBuilder.ToString().TrimEnd('\r', '\n');
        }

        return args;
    }

    private Dictionary<string, string> ParseEditFileArgs(string body)
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = body.Split('\n');

        string? currentKey = null;
        var valueBuilder = new StringBuilder();

        foreach (var line in lines)
        {
            string trimmed = line.Trim();

            // Check if this line starts a new key
            bool isNewKey = false;
            if (trimmed.StartsWith("path:", StringComparison.OrdinalIgnoreCase) && currentKey != "find" && currentKey != "replace")
                isNewKey = true;
            else if (trimmed.StartsWith("find:", StringComparison.OrdinalIgnoreCase) && currentKey != "replace")
                isNewKey = true;
            else if (trimmed.StartsWith("replace:", StringComparison.OrdinalIgnoreCase))
                isNewKey = true;

            if (isNewKey)
            {
                // Save previous key
                if (currentKey != null)
                    args[currentKey] = valueBuilder.ToString().TrimEnd('\r', '\n');

                int colonIdx = trimmed.IndexOf(':');
                currentKey = trimmed[..colonIdx].Trim().ToLowerInvariant();
                string afterKey = trimmed[(colonIdx + 1)..].TrimStart();
                valueBuilder.Clear();
                if (!string.IsNullOrEmpty(afterKey))
                    valueBuilder.AppendLine(afterKey);
            }
            else if (currentKey != null)
            {
                valueBuilder.AppendLine(line);
            }
            else
            {
                // Parse as simple key: value
                if (!string.IsNullOrEmpty(trimmed))
                {
                    int colonIdx = trimmed.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        string key = trimmed[..colonIdx].Trim().ToLowerInvariant();
                        string value = trimmed[(colonIdx + 1)..].Trim();
                        args[key] = value;
                    }
                }
            }
        }

        // Save last key
        if (currentKey != null)
            args[currentKey] = valueBuilder.ToString().TrimEnd('\r', '\n');

        return args;
    }

    private Dictionary<string, string> ParseReplArgs(string body)
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = body.Split('\n');

        bool inCode = false;
        var codeBuilder = new StringBuilder();

        foreach (var line in lines)
        {
            if (inCode)
            {
                codeBuilder.AppendLine(line);
                continue;
            }

            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (trimmed.StartsWith("code:", StringComparison.OrdinalIgnoreCase))
            {
                string afterKey = trimmed["code:".Length..].TrimStart();
                if (!string.IsNullOrEmpty(afterKey))
                    codeBuilder.AppendLine(afterKey);
                inCode = true;
                continue;
            }

            int colonIdx = trimmed.IndexOf(':');
            if (colonIdx > 0)
            {
                string key = trimmed[..colonIdx].Trim().ToLowerInvariant();
                string value = trimmed[(colonIdx + 1)..].Trim();
                args[key] = value;
            }
        }

        if (codeBuilder.Length > 0)
            args["code"] = codeBuilder.ToString().TrimEnd('\r', '\n');

        return args;
    }

    private Dictionary<string, string> ParseNotebookEditArgs(string body)
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = body.Split('\n');

        bool inSource = false;
        var sourceBuilder = new StringBuilder();

        foreach (var line in lines)
        {
            if (inSource)
            {
                sourceBuilder.AppendLine(line);
                continue;
            }

            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (trimmed.StartsWith("new_source:", StringComparison.OrdinalIgnoreCase))
            {
                string afterKey = trimmed["new_source:".Length..].TrimStart();
                if (!string.IsNullOrEmpty(afterKey))
                    sourceBuilder.AppendLine(afterKey);
                inSource = true;
                continue;
            }

            int colonIdx = trimmed.IndexOf(':');
            if (colonIdx > 0)
            {
                string key = trimmed[..colonIdx].Trim().ToLowerInvariant();
                string value = trimmed[(colonIdx + 1)..].Trim();
                args[key] = value;
            }
        }

        if (sourceBuilder.Length > 0)
            args["new_source"] = sourceBuilder.ToString().TrimEnd('\r', '\n');

        return args;
    }

    private static ToolResult Fail(ToolCall call, string error)
    {
        return new ToolResult
        {
            Type = call.Type,
            ToolName = call.ToolName,
            Success = false,
            Error = error,
            Summary = $"Error: {error}",
        };
    }

    // ════════════════════════════════════════════
    // Memory Tools
    // ════════════════════════════════════════════

    private ToolResult ExecuteMemorySave(ToolCall call)
    {
        string name = call.GetArg("name");
        string content = call.GetArg("content");
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(content))
            return Fail(call, "Missing 'name' and 'content' arguments");

        string type = call.GetArg("type", "user"); // user, feedback, project, reference
        string description = call.GetArg("description", name);
        bool projectScope = call.GetArg("scope", "global") == "project";

        try
        {
            _memoryService.SaveMemory(name, type, description, content, projectScope);
            return new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true,
                Output = $"Memory saved: {name} ({type}, {(projectScope ? "project" : "global")} scope)",
                Summary = $"Saved memory: {name}",
            };
        }
        catch (Exception ex)
        {
            return Fail(call, $"Failed to save memory: {ex.Message}");
        }
    }

    private ToolResult ExecuteMemoryList(ToolCall call)
    {
        try
        {
            var memories = _memoryService.ListAllMemories();
            if (memories.Count == 0)
                return new ToolResult
                {
                    Type = call.Type, ToolName = call.ToolName, Success = true,
                    Output = "No memories saved yet.",
                    Summary = "No memories",
                };

            var sb = new StringBuilder($"Found {memories.Count} memory/memories:\n\n");
            foreach (var m in memories)
                sb.AppendLine($"- [{m.Type}] {m.Name}: {m.Description} ({m.Scope})");

            return new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true,
                Output = sb.ToString(),
                Summary = $"Listed {memories.Count} memories",
            };
        }
        catch (Exception ex)
        {
            return Fail(call, $"Failed to list memories: {ex.Message}");
        }
    }

    private ToolResult ExecuteMemoryDelete(ToolCall call)
    {
        string name = call.GetArg("name");
        if (string.IsNullOrEmpty(name))
            return Fail(call, "Missing 'name' argument");

        bool projectScope = call.GetArg("scope", "global") == "project";

        try
        {
            bool deleted = _memoryService.RemoveMemory(name, projectScope);
            return new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true,
                Output = deleted ? $"Memory deleted: {name}" : $"Memory not found: {name}",
                Summary = deleted ? $"Deleted: {name}" : $"Not found: {name}",
            };
        }
        catch (Exception ex)
        {
            return Fail(call, $"Failed to delete memory: {ex.Message}");
        }
    }

    // ════════════════════════════════════════════
    // Phase 4: SkillInvoke Tool
    // ════════════════════════════════════════════

    private ToolResult ExecuteSkillInvoke(ToolCall call)
    {
        string skillName = call.GetArg("skill");
        if (string.IsNullOrEmpty(skillName))
        {
            // If no skill specified, list available skills
            var allSkills = _skillService.GetAllSkills();
            if (allSkills.Count == 0)
                return Fail(call, "No skills available.");

            var sb = new StringBuilder("Available skills:\n\n");
            foreach (var s in allSkills)
                sb.AppendLine($"- **{s.Name}**: {s.Description}{(s.IsBuiltIn ? " (built-in)" : "")}");

            return new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true,
                Output = sb.ToString(),
                Summary = $"Listed {allSkills.Count} available skills",
            };
        }

        var skill = _skillService.GetSkillByName(skillName);
        if (skill == null)
        {
            var allSkills = _skillService.GetAllSkills();
            var names = string.Join(", ", allSkills.Select(s => s.Name));
            return Fail(call, $"Skill '{skillName}' not found. Available: {names}");
        }

        string args = call.GetArg("args", "");

        // Build the skill prompt
        var prompt = new StringBuilder();
        prompt.AppendLine($"# Executing Skill: {skill.Name}");
        prompt.AppendLine($"Description: {skill.Description}");
        if (skill.AllowedTools.Count > 0)
            prompt.AppendLine($"Allowed tools: {string.Join(", ", skill.AllowedTools)}");
        prompt.AppendLine();
        prompt.AppendLine(skill.PromptContent);
        if (!string.IsNullOrEmpty(args))
        {
            prompt.AppendLine();
            prompt.AppendLine($"User arguments: {args}");
        }

        return new ToolResult
        {
            Type = call.Type, ToolName = call.ToolName, Success = true,
            Output = prompt.ToString(),
            Summary = $"Skill '{skill.Name}' prompt loaded — execute it now",
            // The caller (CodeAgentService) should feed this prompt back into the agentic loop
        };
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#}KB";
        return $"{bytes / (1024.0 * 1024.0):0.##}MB";
    }

    // ════════════════════════════════════════════
    // Phase 3: GlobSearch Tool
    // ════════════════════════════════════════════

    private ToolResult ExecuteGlobSearch(ToolCall call)
    {
        string pattern = call.GetArg("pattern");
        if (string.IsNullOrEmpty(pattern))
            return Fail(call, "Missing 'pattern' argument (e.g., **/*.cs, src/**/*.ts)");

        string path = call.GetArg("path", ".");
        string fullPath = _fileSystem.ResolveSafePath(path);

        try
        {
            var matcher = new Microsoft.Extensions.FileSystemGlobbing.Matcher();
            matcher.AddInclude(pattern);
            var result = matcher.Execute(
                new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(
                    new DirectoryInfo(fullPath)));

            var files = result.Files
                .Select(f => f.Path)
                .OrderByDescending(f =>
                {
                    try { return File.GetLastWriteTime(Path.Combine(fullPath, f)); }
                    catch { return DateTime.MinValue; }
                })
                .Take(100)
                .ToList();

            if (files.Count == 0)
                return new ToolResult
                {
                    Type = call.Type, ToolName = call.ToolName, Success = true,
                    Output = $"No files matched pattern '{pattern}' in {path}",
                    Summary = "Glob: 0 matches",
                };

            var sb = new StringBuilder();
            sb.AppendLine($"Found {files.Count} file(s) matching '{pattern}':");
            foreach (var f in files)
                sb.AppendLine(f);
            if (result.Files.Count() > 100)
                sb.AppendLine($"... ({result.Files.Count() - 100} more files not shown)");

            return new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true,
                Output = sb.ToString(),
                Summary = $"Glob: {files.Count} match(es) for '{pattern}'",
            };
        }
        catch (Exception ex)
        {
            return Fail(call, $"Glob search failed: {ex.Message}");
        }
    }

    // ════════════════════════════════════════════
    // Phase 3: Grep Tool
    // ════════════════════════════════════════════

    private ToolResult ExecuteGrepSearch(ToolCall call)
    {
        string pattern = call.GetArg("pattern");
        if (string.IsNullOrEmpty(pattern))
            return Fail(call, "Missing 'pattern' argument (regex pattern to search for)");

        string path = call.GetArg("path", ".");
        string glob = call.GetArg("glob", "*");
        string outputMode = call.GetArg("output_mode", "files_with_matches");
        int context = int.TryParse(call.GetArg("context", "0"), out int c) ? c : 0;
        int headLimit = int.TryParse(call.GetArg("head_limit", "250"), out int h) ? h : 250;
        bool ignoreCase = call.GetArg("ignore_case", "true") == "true";

        string fullPath = _fileSystem.ResolveSafePath(path);

        try
        {
            var regex = new Regex(pattern,
                (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.Compiled);

            var allFiles = _fileSystem.SearchFiles(glob, path);
            var sb = new StringBuilder();
            int matchCount = 0;
            int fileMatchCount = 0;

            foreach (var filePath in allFiles)
            {
                if (matchCount >= headLimit) break;

                try
                {
                    string resolvedFile = _fileSystem.ResolveSafePath(filePath);
                    // Skip binary files
                    if (IsBinaryFile(resolvedFile)) continue;

                    string content = File.ReadAllText(resolvedFile);
                    var lines = content.Split('\n');
                    bool fileHasMatch = false;

                    for (int i = 0; i < lines.Length && matchCount < headLimit; i++)
                    {
                        if (regex.IsMatch(lines[i]))
                        {
                            if (!fileHasMatch)
                            {
                                fileHasMatch = true;
                                fileMatchCount++;
                                if (outputMode == "files_with_matches")
                                {
                                    sb.AppendLine(filePath);
                                    matchCount++;
                                    break;
                                }
                            }

                            if (outputMode == "content")
                            {
                                // Show context lines
                                int start = Math.Max(0, i - context);
                                int end = Math.Min(lines.Length - 1, i + context);
                                if (matchCount > 0) sb.AppendLine("--");
                                sb.AppendLine($"{filePath}:");
                                for (int j = start; j <= end; j++)
                                {
                                    string prefix = j == i ? ">" : " ";
                                    sb.AppendLine($"{prefix}{j + 1}: {lines[j]}");
                                }
                                matchCount++;
                            }
                            else if (outputMode == "count")
                            {
                                matchCount++;
                            }
                        }
                    }
                }
                catch { /* skip unreadable files */ }
            }

            if (outputMode == "count")
                sb.AppendLine($"Found {matchCount} match(es) in {fileMatchCount} file(s)");

            if (matchCount == 0)
                return new ToolResult
                {
                    Type = call.Type, ToolName = call.ToolName, Success = true,
                    Output = $"No matches found for pattern '{pattern}'",
                    Summary = "Grep: 0 matches",
                };

            return new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true,
                Output = sb.ToString(),
                Summary = $"Grep: {matchCount} match(es) for '{pattern}'",
            };
        }
        catch (RegexParseException ex)
        {
            return Fail(call, $"Invalid regex pattern: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Fail(call, $"Grep search failed: {ex.Message}");
        }
    }

    private static bool IsBinaryFile(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var buffer = new byte[Math.Min(8192, stream.Length)];
            int read = stream.Read(buffer, 0, buffer.Length);
            for (int i = 0; i < read; i++)
                if (buffer[i] == 0) return true;
            return false;
        }
        catch { return true; }
    }

    // ════════════════════════════════════════════
    // Phase 3: AskUser Tool
    // ════════════════════════════════════════════

    /// <summary>Raised when agent needs to ask user a question via UI dialog.</summary>
    public event Func<string, List<string>, Task<string>>? OnAskUserQuestion;

    private async Task<ToolResult> ExecuteAskUserAsync(ToolCall call)
    {
        string question = call.GetArg("question", call.GetArg("q"));
        if (string.IsNullOrEmpty(question))
            return Fail(call, "Missing 'question' argument");

        string optionsStr = call.GetArg("options", "");
        var options = string.IsNullOrWhiteSpace(optionsStr)
            ? new List<string>()
            : optionsStr.Split(',').Select(o => o.Trim()).Where(o => !string.IsNullOrEmpty(o)).ToList();

        if (OnAskUserQuestion == null)
            return Fail(call, "AskUser not available — no UI handler connected");

        try
        {
            string answer = await OnAskUserQuestion.Invoke(question, options);
            return new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true,
                Output = $"User answered: {answer}",
                Summary = $"Asked user: {question.Substring(0, Math.Min(50, question.Length))}...",
            };
        }
        catch (Exception ex)
        {
            return Fail(call, $"Failed to ask user: {ex.Message}");
        }
    }

    // ════════════════════════════════════════════
    // Phase 3: Config Tool
    // ════════════════════════════════════════════

    private ToolResult ExecuteConfig(ToolCall call)
    {
        string action = call.GetArg("action", "get");
        string key = call.GetArg("key");

        if (string.IsNullOrEmpty(key))
        {
            // List all config
            var settings = _settingsService.Settings;
            var sb = new StringBuilder("Current configuration:\n");
            sb.AppendLine($"  language: {settings.Language}");
            sb.AppendLine($"  active_provider: {settings.ActiveProvider}");
            sb.AppendLine($"  features.git: {settings.Features.GitIntegration}");
            sb.AppendLine($"  features.github: {settings.Features.GitHubIntegration}");
            sb.AppendLine($"  features.web_fetch: {settings.Features.WebFetch}");
            sb.AppendLine($"  features.smart_editing: {settings.Features.SmartEditing}");
            sb.AppendLine($"  features.plugins: {settings.Features.PluginSystem}");
            sb.AppendLine($"  features.mcp_servers: {settings.Features.McpServers}");
            sb.AppendLine($"  features.task_manager: {settings.Features.TaskManager}");
            sb.AppendLine($"  features.permissions: {settings.Features.PermissionSystem}");

            return new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true,
                Output = sb.ToString(),
                Summary = "Listed configuration",
            };
        }

        if (action == "get")
        {
            string? value = key.ToLowerInvariant() switch
            {
                "language" => _settingsService.Settings.Language,
                "active_provider" or "provider" => _settingsService.Settings.ActiveProvider.ToString(),
                _ => null,
            };

            if (value == null)
                return Fail(call, $"Unknown config key: {key}");

            return new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true,
                Output = $"{key} = {value}",
                Summary = $"Config: {key}",
            };
        }

        // Set is read-only for security — only get is supported
        return Fail(call, "Config set is not supported via tool. Use the Settings page.");
    }

    // ════════════════════════════════════════════
    // Phase 3: NotebookEdit Tool
    // ════════════════════════════════════════════

    private ToolResult ExecuteNotebookEdit(ToolCall call)
    {
        string notebookPath = call.GetArg("notebook_path", call.GetArg("path"));
        if (string.IsNullOrEmpty(notebookPath))
            return Fail(call, "Missing 'notebook_path' argument");

        string newSource = call.GetArg("new_source", call.GetArg("source", call.GetArg("content")));
        string cellType = call.GetArg("cell_type", "code");
        string editMode = call.GetArg("edit_mode", "replace");
        int cellNumber = int.TryParse(call.GetArg("cell_number", "0"), out int cn) ? cn : 0;

        string resolvedPath = _fileSystem.ResolveSafePath(notebookPath);

        try
        {
            string json = File.ReadAllText(resolvedPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("cells", out var cells) || cells.GetArrayLength() == 0)
                return Fail(call, "Notebook has no cells or is not a valid .ipynb file");

            // Use System.Text.Json to modify the notebook
            using var stream = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();

                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "cells")
                    {
                        writer.WritePropertyName("cells");
                        writer.WriteStartArray();

                        int index = 0;
                        foreach (var cell in cells.EnumerateArray())
                        {
                            if (editMode == "delete" && index == cellNumber)
                            {
                                index++;
                                continue; // skip this cell
                            }

                            if (editMode == "insert" && index == cellNumber)
                            {
                                // Insert new cell before
                                WriteNewCell(writer, newSource, cellType);
                            }

                            if (editMode == "replace" && index == cellNumber)
                            {
                                // Write modified cell
                                WriteNewCell(writer, newSource, cellType);
                            }
                            else
                            {
                                cell.WriteTo(writer); // keep original
                            }

                            index++;
                        }

                        if (editMode == "insert" && cellNumber >= cells.GetArrayLength())
                        {
                            // Append at end
                            WriteNewCell(writer, newSource, cellType);
                        }

                        writer.WriteEndArray();
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            File.WriteAllText(resolvedPath, System.Text.Encoding.UTF8.GetString(stream.ToArray()));

            return new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName, Success = true,
                Output = $"Notebook {editMode}d cell {cellNumber} in {notebookPath}",
                Summary = $"Notebook: {editMode} cell {cellNumber}",
            };
        }
        catch (Exception ex)
        {
            return Fail(call, $"Notebook edit failed: {ex.Message}");
        }
    }

    private static void WriteNewCell(System.Text.Json.Utf8JsonWriter writer, string source, string cellType)
    {
        writer.WriteStartObject();
        writer.WriteString("cell_type", cellType);
        writer.WritePropertyName("source");
        writer.WriteStartArray();
        var lines = source.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            writer.WriteStringValue(i < lines.Length - 1 ? lines[i] + "\n" : lines[i]);
        writer.WriteEndArray();
        writer.WritePropertyName("metadata");
        writer.WriteStartObject();
        writer.WriteEndObject();
        if (cellType == "code")
        {
            writer.WritePropertyName("outputs");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteNumber("execution_count", 0);
        }
        writer.WriteEndObject();
    }

    // ════════════════════════════════════════════
    // Phase 3: PowerShell Tool
    // ════════════════════════════════════════════

    private async Task<ToolResult> ExecutePowerShellAsync(ToolCall call, CancellationToken ct)
    {
        string command = call.GetArg("command", call.GetArg("script"));
        if (string.IsNullOrEmpty(command))
            return Fail(call, "Missing 'command' argument");

        int timeoutMs = int.TryParse(call.GetArg("timeout", "30000"), out int t) ? t : 30000;
        timeoutMs = Math.Min(timeoutMs, 120000); // Cap at 2 minutes

        System.Diagnostics.Process? proc = null;
        try
        {
            // Find PowerShell executable
            string psExe = File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe")
                ? @"C:\Program Files\PowerShell\7\pwsh.exe"
                : "powershell.exe";

            var psi = new System.Diagnostics.ProcessStartInfo(psExe, $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"")}\"")
            {
                WorkingDirectory = _fileSystem.HasWorkingDirectory ? _fileSystem.WorkingDirectory : Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
                return Fail(call, "Failed to start PowerShell process");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            // Read stdout/stderr concurrently to avoid pipe deadlock
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            string output = stdout;
            if (!string.IsNullOrWhiteSpace(stderr))
                output += $"\n[stderr]: {stderr}";

            if (output.Length > 5000)
                output = output[..5000] + "\n... (truncated)";

            return new ToolResult
            {
                Type = call.Type, ToolName = call.ToolName,
                Success = proc.ExitCode == 0,
                Output = output,
                Error = proc.ExitCode != 0 ? $"Exit code: {proc.ExitCode}" : "",
                Summary = $"PowerShell: {command[..Math.Min(60, command.Length)]}",
            };
        }
        catch (OperationCanceledException)
        {
            try { proc?.Kill(true); } catch { }
            return Fail(call, $"PowerShell timed out after {timeoutMs}ms");
        }
        catch (Exception ex)
        {
            return Fail(call, $"PowerShell failed: {ex.Message}");
        }
        finally
        {
            proc?.Dispose();
        }
    }
}
