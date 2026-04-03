using System.Text;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Autonomous coding agent that can write, execute, and fix code iteratively.
/// Supports an agentic tool-use loop for file system operations.
/// Integrates SmartEditingService for code validation and context enrichment.
/// Integrates ContextMemoryService for automatic context compaction.
/// </summary>
public class CodeAgentService
{
    private readonly AiProviderManager _providerManager;
    private readonly CodeExecutionService _codeExecutionService;
    private readonly AgentToolService _agentToolService;
    private readonly FileSystemService _fileSystemService;
    private readonly SettingsService _settingsService;
    private readonly SmartEditingService _smartEditingService;
    private readonly ContextMemoryService _contextMemoryService;

    private const int MaxAgentIterations = 15;

    private const string BaseSystemPrompt = """
        You are CluadeX, an expert AI coding assistant running locally. You help users write, debug, test, and improve code autonomously.

        CAPABILITIES:
        - Write clean, production-quality code in any programming language
        - Debug and fix errors based on compiler/runtime output
        - Refactor and optimize existing code
        - Explain code and architectural decisions
        - Design complete applications and systems
        - Read, write, and edit files directly on the user's machine
        - Run shell commands to build, test, and execute code
        - Full Git version control: status, add, commit, push, pull, branch, merge, diff, log, stash
        - GitHub integration: create PRs, list issues, view repos (requires gh CLI)
        - Smart code analysis: structure extraction, bracket validation, minimal diffs

        RULES:
        1. Always write code inside markdown code blocks with the language specified: ```language
        2. Write complete, runnable code - never use placeholders like "..."
        3. Handle edge cases and errors properly
        4. Follow best practices and idiomatic patterns for the language
        5. When fixing errors, analyze the error message carefully and provide the complete corrected code
        6. Add brief comments for complex logic only
        7. When editing files, prefer minimal changes - use edit_file with precise find/replace over rewriting entire files
        8. Validate your code mentally before writing - ensure brackets/braces balance

        PROBLEM-SOLVING APPROACH:
        Think step by step like an expert software engineer:
        1. Understand the requirement fully - ask clarifying questions if needed
        2. Explore existing code first (read_file, search_content) to understand the codebase
        3. Design the solution architecture before writing code
        4. Implement with clean, readable code
        5. Validate: check for syntax errors, edge cases, and error handling
        6. Test if possible: run the code to verify it works
        7. If errors occur, analyze carefully and fix systematically

        If you receive error output from code execution, analyze it carefully and provide a corrected version.
        Always provide the COMPLETE code, not just the changed parts.
        """;

    /// <summary>Fires when the agent wants to report status to the UI.</summary>
    public event Action<string>? OnAgentStatus;

    /// <summary>Fires when a tool action completes (for adding to chat UI).</summary>
    public event Action<ToolResult>? OnToolExecuted;

    public CodeAgentService(
        AiProviderManager providerManager,
        CodeExecutionService codeExecutionService,
        AgentToolService agentToolService,
        FileSystemService fileSystemService,
        SettingsService settingsService,
        SmartEditingService smartEditingService,
        ContextMemoryService contextMemoryService)
    {
        _providerManager = providerManager;
        _codeExecutionService = codeExecutionService;
        _agentToolService = agentToolService;
        _fileSystemService = fileSystemService;
        _settingsService = settingsService;
        _smartEditingService = smartEditingService;
        _contextMemoryService = contextMemoryService;
    }

    /// <summary>Gets system prompt with or without tool definitions based on whether a project is open.</summary>
    public string GetSystemPrompt()
    {
        var sb = new StringBuilder(BaseSystemPrompt);

        if (_fileSystemService.HasWorkingDirectory)
        {
            sb.AppendLine();
            sb.AppendLine($"CURRENT PROJECT: {_fileSystemService.WorkingDirectory}");
            sb.AppendLine();
            sb.AppendLine(_agentToolService.GetToolDefinitionsPrompt());
            sb.AppendLine();

            // Include compact project tree for context
            try
            {
                string tree = _fileSystemService.GetProjectTree(2);
                if (tree.Length < 2000)
                {
                    sb.AppendLine("PROJECT STRUCTURE:");
                    sb.AppendLine(tree);
                }
            }
            catch { /* ignore */ }
        }

        return sb.ToString();
    }

    // ═══════════════════════════════════════════
    // Basic Streaming Chat (no tools)
    // ═══════════════════════════════════════════
    public async IAsyncEnumerable<string> ChatStreamAsync(
        List<ChatMessage> history,
        string userMessage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var token in _providerManager.ActiveProvider.ChatAsync(history, userMessage, GetSystemPrompt(), ct))
        {
            yield return token;
        }
    }

    // ═══════════════════════════════════════════
    // Agentic Tool-Use Loop
    // ═══════════════════════════════════════════
    /// <summary>
    /// Run the agentic loop: generate → parse tools → execute → validate → feed results → repeat.
    /// Integrates smart editing for code validation and context enrichment.
    /// Integrates context memory for automatic compaction.
    /// </summary>
    public async Task<AgentLoopResult> ExecuteAgenticAsync(
        List<ChatMessage> history,
        string userMessage,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var result = new AgentLoopResult();
        string systemPrompt = GetSystemPrompt();

        // ─── Smart context enrichment ───
        // Detect file references in user message and auto-load relevant context
        string enrichedMessage = userMessage;
        if (_fileSystemService.HasWorkingDirectory)
        {
            var mentionedFiles = _smartEditingService.ExtractMentionedFiles(userMessage);
            if (mentionedFiles.Count > 0)
            {
                enrichedMessage = _smartEditingService.EnhanceEditRequest(userMessage, mentionedFiles);
                OnAgentStatus?.Invoke($"Loaded context for {mentionedFiles.Count} referenced file(s)");
            }
        }

        // ─── Auto-compact history if context is getting full ───
        var compactedHistory = history
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant or MessageRole.ToolAction)
            .ToList();

        if (_contextMemoryService.ShouldSummarize(compactedHistory))
        {
            OnAgentStatus?.Invoke("Compacting conversation history...");
            compactedHistory = _contextMemoryService.CompactHistory(compactedHistory);
        }

        // Build working history
        var workingHistory = compactedHistory
            .Select(m => new ChatMessage
            {
                Role = m.Role == MessageRole.ToolAction ? MessageRole.System : m.Role,
                Content = m.Role == MessageRole.ToolAction
                    ? $"[Tool: {m.ToolName}] {(m.ToolSuccess ? "OK" : "Error")}: {m.ToolSummary}"
                    : m.Content,
            })
            .ToList();

        string currentMessage = enrichedMessage;

        for (int iteration = 0; iteration < MaxAgentIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();

            var step = new AgentStep { StepNumber = iteration + 1 };

            // ─── Generate response ───
            progress?.Report(iteration == 0 ? "Thinking..." : $"Continuing... (step {iteration + 1})");
            OnAgentStatus?.Invoke(iteration == 0 ? "Thinking..." : $"Agent step {iteration + 1}...");

            string response = await _providerManager.ActiveProvider.GenerateAsync(
                workingHistory, currentMessage, systemPrompt, ct);
            step.ResponseText = response;

            // ─── Check for tool calls ───
            var toolCalls = _agentToolService.ParseToolCalls(response);
            step.ToolCalls = toolCalls;

            if (toolCalls.Count == 0)
            {
                // No tools used — validate any code blocks in the final response
                var validationFeedback = ValidateResponseCode(response);
                if (validationFeedback != null && iteration < MaxAgentIterations - 1)
                {
                    // Code has issues — ask model to fix
                    step.ThinkingText = _agentToolService.StripToolCalls(response);
                    result.Steps.Add(step);

                    workingHistory.Add(new ChatMessage { Role = MessageRole.Assistant, Content = response });
                    workingHistory.Add(new ChatMessage { Role = MessageRole.System, Content = validationFeedback });
                    currentMessage = validationFeedback;
                    continue; // re-generate
                }

                result.Steps.Add(step);
                result.FinalResponse = response;
                result.Success = true;
                break;
            }

            // ─── Execute tools ───
            string cleanText = _agentToolService.StripToolCalls(response);
            if (!string.IsNullOrWhiteSpace(cleanText))
                step.ThinkingText = cleanText;

            progress?.Report($"Running {toolCalls.Count} tool(s)...");
            OnAgentStatus?.Invoke($"Executing {toolCalls.Count} tool(s)...");

            var toolResults = new List<ToolResult>();
            foreach (var call in toolCalls)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Running: {call.ToolName}...");
                OnAgentStatus?.Invoke($"Tool: {call.ToolName}...");

                var toolResult = await _agentToolService.ExecuteToolAsync(call, ct);

                // ─── Smart validation for write/edit operations ───
                if (call.Type is ToolType.WriteFile or ToolType.EditFile && toolResult.Success)
                {
                    var writeValidation = ValidateToolWrite(call);
                    if (writeValidation != null)
                    {
                        toolResult = new ToolResult
                        {
                            ToolName = toolResult.ToolName,
                            Success = true,
                            Output = toolResult.Output + $"\n⚠ Validation: {writeValidation}",
                            Summary = toolResult.Summary + " (with warnings)",
                        };
                    }
                }

                toolResults.Add(toolResult);

                // Fire event for UI
                OnToolExecuted?.Invoke(toolResult);
            }
            step.ToolResults = toolResults;
            result.Steps.Add(step);

            // ─── Feed results back to model ───
            workingHistory.Add(new ChatMessage
            {
                Role = MessageRole.Assistant,
                Content = response,
            });

            string toolFeedback = _agentToolService.FormatToolResults(toolResults);
            workingHistory.Add(new ChatMessage
            {
                Role = MessageRole.System,
                Content = toolFeedback,
            });

            currentMessage = toolFeedback;
        }

        // If we exhausted iterations
        if (!result.Success)
        {
            result.FinalResponse = result.Steps.LastOrDefault()?.ResponseText ?? "Agent reached maximum iterations.";
            OnAgentStatus?.Invoke("Agent loop complete.");
        }

        progress?.Report("Done");
        OnAgentStatus?.Invoke("Ready");
        return result;
    }

    // ═══════════════════════════════════════════
    // Smart Code Validation
    // ═══════════════════════════════════════════

    /// <summary>
    /// Validate code blocks in the model response. Returns feedback string if issues found, null if OK.
    /// </summary>
    private string? ValidateResponseCode(string response)
    {
        var codeBlocks = _codeExecutionService.ExtractCodeBlocks(response);
        if (codeBlocks.Count == 0) return null;

        var allIssues = new List<string>();
        foreach (var block in codeBlocks)
        {
            var validation = _smartEditingService.ValidateCode(block.Code, block.Language);
            if (!validation.IsValid)
            {
                allIssues.AddRange(validation.Issues.Select(i => $"[{block.Language}] {i}"));
            }
        }

        if (allIssues.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("⚠ Code validation detected issues in your response:");
        foreach (var issue in allIssues)
            sb.AppendLine($"  - {issue}");
        sb.AppendLine();
        sb.AppendLine("Please fix these issues and provide the corrected code.");
        return sb.ToString();
    }

    /// <summary>
    /// Validate content written by write_file or edit_file tools.
    /// Returns warning string if issues found, null if OK.
    /// </summary>
    private string? ValidateToolWrite(ToolCall call)
    {
        try
        {
            string? content = null;
            if (call.Arguments.TryGetValue("content", out var c)) content = c;
            else if (call.Arguments.TryGetValue("new_content", out var nc)) content = nc;

            if (string.IsNullOrEmpty(content)) return null;

            string ext = "";
            if (call.Arguments.TryGetValue("path", out var path))
                ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

            string lang = ext switch
            {
                ".cs" => "csharp", ".py" => "python", ".js" => "javascript",
                ".ts" => "typescript", ".java" => "java", ".cpp" => "cpp",
                ".c" => "c", ".rs" => "rust", ".go" => "go",
                _ => "text"
            };

            var validation = _smartEditingService.ValidateCode(content, lang);
            if (!validation.IsValid)
                return string.Join("; ", validation.Issues);
            if (validation.Warnings.Count > 0)
                return string.Join("; ", validation.Warnings);

            return null;
        }
        catch { return null; }
    }

    // ═══════════════════════════════════════════
    // Auto-execute mode (code execution with fix loop)
    // ═══════════════════════════════════════════
    public async Task<AgentResult> ExecuteWithAutoFixAsync(
        List<ChatMessage> history,
        string userMessage,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var result = new AgentResult();
        int maxAttempts = _settingsService.Settings.MaxAutoFixAttempts;
        string currentMessage = userMessage;
        string systemPrompt = GetSystemPrompt();

        for (int attempt = 0; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt == 0)
            {
                progress?.Report("Generating code...");
                OnAgentStatus?.Invoke("Generating code...");
            }
            else
            {
                progress?.Report($"Fixing code (attempt {attempt}/{maxAttempts})...");
                OnAgentStatus?.Invoke($"Auto-fixing attempt {attempt}/{maxAttempts}...");
            }

            // Generate response
            string response = await _providerManager.ActiveProvider.GenerateAsync(history, currentMessage, systemPrompt, ct);
            result.Response = response;
            result.Attempts = attempt + 1;

            // Extract code blocks
            var codeBlocks = _codeExecutionService.ExtractCodeBlocks(response);
            result.CodeBlocks = codeBlocks;

            if (codeBlocks.Count == 0 || !_settingsService.Settings.AutoExecuteCode)
            {
                result.Success = true;
                break;
            }

            // Validate code before execution
            var mainBlock = codeBlocks.Last();
            var validation = _smartEditingService.ValidateCode(mainBlock.Code, mainBlock.Language);
            if (!validation.IsValid && attempt < maxAttempts)
            {
                // Code has syntax issues — ask model to fix before running
                var fixMsg = $"Code validation found issues before execution:\n" +
                             string.Join("\n", validation.Issues.Select(i => $"  - {i}")) +
                             "\n\nPlease fix and provide the complete corrected code.";

                history.Add(new ChatMessage { Role = MessageRole.Assistant, Content = response });
                history.Add(new ChatMessage { Role = MessageRole.System, Content = fixMsg });
                currentMessage = fixMsg;
                continue;
            }

            // Execute the last (main) code block
            progress?.Report($"Executing {mainBlock.Language} code...");
            OnAgentStatus?.Invoke($"Running {mainBlock.Language} code...");

            var execResult = await _codeExecutionService.ExecuteAsync(mainBlock.Code, mainBlock.Language, ct);
            result.ExecutionResult = execResult;

            if (execResult.Success)
            {
                progress?.Report("Code executed successfully!");
                OnAgentStatus?.Invoke("Code executed successfully!");
                result.Success = true;
                break;
            }

            if (attempt >= maxAttempts)
            {
                progress?.Report($"Failed after {maxAttempts} attempts.");
                OnAgentStatus?.Invoke("Auto-fix limit reached.");
                result.Success = false;
                break;
            }

            // Build fix message for next iteration
            currentMessage = BuildFixPrompt(mainBlock, execResult);

            history.Add(new ChatMessage { Role = MessageRole.Assistant, Content = response });
            history.Add(new ChatMessage
            {
                Role = MessageRole.CodeExecution,
                Content = currentMessage,
            });
        }

        return result;
    }

    private static string BuildFixPrompt(CodeBlock codeBlock, CodeExecutionResult execResult)
    {
        var sb = new StringBuilder();
        sb.AppendLine("The code execution failed with the following error:");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(execResult.FullOutput.Length > 2000
            ? execResult.FullOutput[..2000] + "\n... (truncated)"
            : execResult.FullOutput);
        sb.AppendLine("```");
        sb.AppendLine();

        if (execResult.TimedOut)
        {
            sb.AppendLine("The code timed out after 30 seconds. Check for infinite loops or long-running operations.");
        }

        sb.AppendLine("Please analyze the error and provide the complete corrected code. Show the ENTIRE fixed code, not just the changed parts.");

        return sb.ToString();
    }
}

/// <summary>Result of the agentic tool-use loop.</summary>
public class AgentLoopResult
{
    public bool Success { get; set; }
    public string FinalResponse { get; set; } = string.Empty;
    public List<AgentStep> Steps { get; set; } = new();
    public int TotalToolCalls => Steps.Sum(s => s.ToolCalls.Count);
}

public class AgentResult
{
    public bool Success { get; set; }
    public string Response { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public List<CodeBlock> CodeBlocks { get; set; } = new();
    public CodeExecutionResult? ExecutionResult { get; set; }
}
