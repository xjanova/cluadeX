using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CluadeX.Models;

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

    // Regex to match [ACTION: tool_name]...[/ACTION] blocks
    private static readonly Regex ActionRegex = new(
        @"\[ACTION:\s*(\w+)\](.*?)\[/ACTION\]",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Raised when a tool requires user confirmation (PermAction.Ask).</summary>
    public event Func<string, string, Task<bool>>? OnPermissionRequired;

    public AgentToolService(FileSystemService fileSystem, CodeExecutionService codeExecution,
        GitService gitService, GitHubService gitHubService, PermissionService permissionService,
        SettingsService settingsService, ActivationService activationService)
    {
        _fileSystem = fileSystem;
        _codeExecution = codeExecution;
        _gitService = gitService;
        _gitHubService = gitHubService;
        _permissionService = permissionService;
        _settingsService = settingsService;
        _activationService = activationService;
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

            calls.Add(new ToolCall
            {
                Type = toolType.Value,
                ToolName = toolName,
                Arguments = args,
                RawText = match.Value,
            });
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
            var perm = _permissionService.CheckPermission(resource, scope);
            if (perm == PermAction.Deny)
                return Fail(call, $"Permission denied for {scope}: {resource}");
            if (perm == PermAction.Ask)
            {
                bool allowed = OnPermissionRequired != null
                    && await OnPermissionRequired.Invoke(call.ToolName, $"{scope}: {resource}");
                if (!allowed)
                    return Fail(call, $"User denied permission for {scope}: {resource}");
            }
            return call.Type switch
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

                _ => new ToolResult
                {
                    Type = call.Type,
                    ToolName = call.ToolName,
                    Success = false,
                    Error = $"Unknown tool: {call.ToolName}",
                },
            };
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

        sb.AppendLine("""

            RULES:
            - You can use multiple tools in a single response
            - Always read a file before editing it
            - Use edit_file for small changes, write_file for creating new files or full rewrites
            - Explain what you're doing before and after using tools
            - Paths are relative to the project root
            - After making changes, verify them by reading the file or running tests
            """);

        if (gitEnabled)
        {
            sb.AppendLine("""
            - Use git tools to manage version control
            - Always check git_status before committing
            - Write meaningful commit messages
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
    // Helpers
    // ════════════════════════════════════════════

    private static ToolType? ResolveToolType(string toolName)
    {
        return toolName switch
        {
            // File tools
            "read_file" or "readfile" => ToolType.ReadFile,
            "write_file" or "writefile" => ToolType.WriteFile,
            "edit_file" or "editfile" => ToolType.EditFile,
            "list_files" or "listfiles" or "list_directory" or "ls" => ToolType.ListFiles,
            "search_files" or "searchfiles" or "find_files" => ToolType.SearchFiles,
            "search_content" or "searchcontent" or "grep" => ToolType.SearchContent,
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

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#}KB";
        return $"{bytes / (1024.0 * 1024.0):0.##}MB";
    }
}
