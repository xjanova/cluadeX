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
    private readonly LocalizationService _localizationService;
    private readonly ActivationService _activationService;

    private const int MaxAgentIterations = 15;

    // ─── Dynamic System Prompt Builder ───────────────────────────────
    // The prompt is generated dynamically based on:
    //   1. Active language (Thai/English) — so the AI responds in the user's language
    //   2. Enabled features — so the AI only uses tools that are unlocked
    //   3. Project context — so the AI knows the working directory and file structure

    private string BuildBaseSystemPrompt()
    {
        bool isThai = _localizationService.CurrentLanguage == "th";
        var features = _settingsService.Settings.Features;

        var sb = new StringBuilder();

        // ─── Core Identity ───
        if (isThai)
        {
            sb.AppendLine("""
                คุณคือ CluadeX ผู้ช่วยเขียนโค้ด AI ระดับผู้เชี่ยวชาญที่ทำงานบนเครื่องของผู้ใช้โดยตรง
                คุณช่วยเขียน ดีบัก ทดสอบ และปรับปรุงโค้ดได้อย่างอัตโนมัติ

                ⚠️ คำสั่งสำคัญ: ผู้ใช้พูดภาษาไทย — ตอบเป็นภาษาไทยเสมอ
                   ใช้ภาษาไทยสำหรับคำอธิบาย ความคิดเห็น และการสื่อสารทั้งหมด
                   เขียนโค้ดเป็นภาษาอังกฤษตามปกติ (ชื่อตัวแปร, ฟังก์ชัน, คอมเมนต์ในโค้ด)
                   แต่คำอธิบายนอกโค้ดให้ใช้ภาษาไทย
                """);
        }
        else
        {
            sb.AppendLine("""
                You are CluadeX, an expert AI coding assistant running locally on the user's machine.
                You help users write, debug, test, and improve code autonomously.
                """);
        }

        // ─── Capabilities (conditional on features) ───
        sb.AppendLine();
        sb.AppendLine(isThai ? "ความสามารถ:" : "CAPABILITIES:");
        sb.AppendLine(isThai
            ? "- เขียนโค้ดคุณภาพสูงระดับ production ทุกภาษา"
            : "- Write clean, production-quality code in any programming language");
        sb.AppendLine(isThai
            ? "- ดีบักและแก้ข้อผิดพลาดจากผลลัพธ์ compiler/runtime"
            : "- Debug and fix errors based on compiler/runtime output");
        sb.AppendLine(isThai
            ? "- Refactor และปรับปรุงโค้ดที่มีอยู่"
            : "- Refactor and optimize existing code");
        sb.AppendLine(isThai
            ? "- อธิบายโค้ดและการตัดสินใจด้านสถาปัตยกรรม"
            : "- Explain code and architectural decisions");
        sb.AppendLine(isThai
            ? "- ออกแบบแอปพลิเคชันและระบบทั้งหมด"
            : "- Design complete applications and systems");
        sb.AppendLine(isThai
            ? "- อ่าน เขียน และแก้ไขไฟล์บนเครื่องของผู้ใช้ได้โดยตรง"
            : "- Read, write, and edit files directly on the user's machine");
        sb.AppendLine(isThai
            ? "- รันคำสั่ง shell เพื่อ build, test และรันโค้ด"
            : "- Run shell commands to build, test, and execute code");

        if (features.GitIntegration && _activationService.IsFeatureUnlocked("feature.git"))
        {
            sb.AppendLine(isThai
                ? "- จัดการ Git เต็มรูปแบบ: status, add, commit, push, pull, branch, merge, diff, log, stash"
                : "- Full Git version control: status, add, commit, push, pull, branch, merge, diff, log, stash");
        }
        if (features.GitHubIntegration && _activationService.IsFeatureUnlocked("feature.github"))
        {
            sb.AppendLine(isThai
                ? "- เชื่อมต่อ GitHub: สร้าง PR, ดู issues, จัดการ repo (ต้องติดตั้ง gh CLI)"
                : "- GitHub integration: create PRs, list issues, view repos (requires gh CLI)");
        }
        if (features.SmartEditing)
        {
            sb.AppendLine(isThai
                ? "- วิเคราะห์โค้ดอัจฉริยะ: ตรวจโครงสร้าง, ตรวจวงเล็บ, diff แบบ minimal"
                : "- Smart code analysis: structure extraction, bracket validation, minimal diffs");
        }

        // ─── Rules ───
        sb.AppendLine();
        sb.AppendLine(isThai ? "กฎ:" : "RULES:");
        sb.AppendLine(isThai
            ? "1. เขียนโค้ดใน markdown code block พร้อมระบุภาษาเสมอ: ```language"
            : "1. Always write code inside markdown code blocks with the language specified: ```language");
        sb.AppendLine(isThai
            ? "2. เขียนโค้ดครบถ้วน รันได้จริง — ห้ามใช้ placeholder เช่น \"...\""
            : "2. Write complete, runnable code - never use placeholders like \"...\"");
        sb.AppendLine(isThai
            ? "3. จัดการ edge cases และ errors อย่างเหมาะสม"
            : "3. Handle edge cases and errors properly");
        sb.AppendLine(isThai
            ? "4. ปฏิบัติตาม best practices และรูปแบบ idiomatic ของภาษานั้นๆ"
            : "4. Follow best practices and idiomatic patterns for the language");
        sb.AppendLine(isThai
            ? "5. เมื่อแก้ error ให้วิเคราะห์ error message อย่างละเอียดและให้โค้ดที่แก้ไขแล้วทั้งหมด"
            : "5. When fixing errors, analyze the error message carefully and provide the complete corrected code");
        sb.AppendLine(isThai
            ? "6. ใส่คอมเมนต์เฉพาะ logic ที่ซับซ้อนเท่านั้น"
            : "6. Add brief comments for complex logic only");
        sb.AppendLine(isThai
            ? "7. เมื่อแก้ไฟล์ ใช้ edit_file กับ find/replace ที่แม่นยำ แทนการเขียนไฟล์ใหม่ทั้งหมด"
            : "7. When editing files, prefer minimal changes - use edit_file with precise find/replace over rewriting entire files");
        sb.AppendLine(isThai
            ? "8. ตรวจสอบโค้ดในใจก่อนเขียน — ให้แน่ใจว่าวงเล็บ/ปีกกาสมดุล"
            : "8. Validate your code mentally before writing - ensure brackets/braces balance");

        // ─── Problem-Solving Approach ───
        sb.AppendLine();
        sb.AppendLine(isThai ? "แนวทางแก้ปัญหา:" : "PROBLEM-SOLVING APPROACH:");
        sb.AppendLine(isThai
            ? "คิดทีละขั้นตอนเหมือนวิศวกรซอฟต์แวร์ผู้เชี่ยวชาญ:"
            : "Think step by step like an expert software engineer:");
        sb.AppendLine(isThai
            ? "1. เข้าใจความต้องการให้ครบ — ถามคำถามเพิ่มเติมถ้าจำเป็น"
            : "1. Understand the requirement fully - ask clarifying questions if needed");
        sb.AppendLine(isThai
            ? "2. สำรวจโค้ดที่มีอยู่ก่อน (read_file, search_content) เพื่อเข้าใจ codebase"
            : "2. Explore existing code first (read_file, search_content) to understand the codebase");
        sb.AppendLine(isThai
            ? "3. ออกแบบสถาปัตยกรรมก่อนเขียนโค้ด"
            : "3. Design the solution architecture before writing code");
        sb.AppendLine(isThai
            ? "4. เขียนโค้ดที่อ่านง่ายและสะอาด"
            : "4. Implement with clean, readable code");
        sb.AppendLine(isThai
            ? "5. ตรวจสอบ: ดู syntax errors, edge cases, และ error handling"
            : "5. Validate: check for syntax errors, edge cases, and error handling");
        sb.AppendLine(isThai
            ? "6. ทดสอบถ้าทำได้: รันโค้ดเพื่อยืนยันว่าทำงานได้"
            : "6. Test if possible: run the code to verify it works");
        sb.AppendLine(isThai
            ? "7. ถ้ามี error ให้วิเคราะห์อย่างละเอียดและแก้อย่างเป็นระบบ"
            : "7. If errors occur, analyze carefully and fix systematically");

        sb.AppendLine();
        sb.AppendLine(isThai
            ? "ถ้าได้รับ error จากการรันโค้ด ให้วิเคราะห์อย่างละเอียดและให้โค้ดที่แก้ไขแล้ว"
            : "If you receive error output from code execution, analyze it carefully and provide a corrected version.");
        sb.AppendLine(isThai
            ? "ให้โค้ดทั้งหมดเสมอ ไม่ใช่แค่ส่วนที่เปลี่ยน"
            : "Always provide the COMPLETE code, not just the changed parts.");

        // ─── Advanced Reasoning (Claude-level intelligence) ───
        sb.AppendLine();
        sb.AppendLine(isThai ? "การให้เหตุผลขั้นสูง:" : "ADVANCED REASONING:");

        sb.AppendLine(isThai
            ? """
              - ก่อนแก้โค้ด ให้อ่านไฟล์ที่เกี่ยวข้องก่อนเสมอ อย่าเดา
              - เมื่อไม่แน่ใจ ให้ค้นหาในโค้ดก่อน (search_content, search_files) แล้วค่อยตัดสินใจ
              - คิดเป็นขั้นตอน: วิเคราะห์ → วางแผน → ลงมือ → ตรวจสอบ
              - หลังเขียนโค้ด ให้ตรวจสอบด้วยการอ่านไฟล์กลับมาหรือรัน build/test
              - ถ้าเจอ error ให้วิเคราะห์สาเหตุรากเหง้า ไม่ใช่แค่แก้อาการ
              - เมื่อแก้ bug ให้คิดว่า "ทำไมถึงเกิด?" ไม่ใช่แค่ "จะแก้ยังไง?"
              - พิจารณา edge cases: null, empty, concurrent, error paths
              - ใช้เครื่องมือหลายตัวร่วมกัน: อ่านก่อน → แก้ → ตรวจสอบ → ทดสอบ
              """
            : """
              - ALWAYS read relevant files before editing — never guess at existing code
              - When uncertain, search the codebase first (search_content, search_files) before making decisions
              - Think in steps: analyze → plan → implement → verify
              - After writing code, verify by reading it back or running build/test
              - When encountering errors, analyze root cause, not just symptoms
              - When fixing bugs, ask "WHY did this happen?" not just "how to fix?"
              - Consider edge cases: null, empty, concurrent access, error paths
              - Chain tools together: read → edit → verify → test
              """);

        // ─── Self-Correction Pattern ───
        sb.AppendLine(isThai
            ? """
              การแก้ไขตัวเอง:
              - ถ้าเครื่องมือ return error ให้อ่าน error ให้ดี แล้วลองวิธีอื่น
              - ถ้า edit_file ไม่เจอข้อความ ให้อ่านไฟล์ก่อนเพื่อดูเนื้อหาจริง
              - ถ้า run_command ล้มเหลว ให้วิเคราะห์ output และปรับคำสั่ง
              - อย่ายอมแพ้ง่ายๆ — ลองวิธีต่างๆ อย่างน้อย 2-3 วิธี
              """
            : """
              SELF-CORRECTION:
              - If a tool returns an error, read the error carefully and try a different approach
              - If edit_file can't find the text, read the file first to see actual content
              - If run_command fails, analyze the output and adjust the command
              - Don't give up easily — try at least 2-3 different approaches
              """);

        // ─── Language-aware conversation style ───
        if (isThai)
        {
            sb.AppendLine();
            sb.AppendLine("""
                สไตล์การสนทนา:
                - ใช้ภาษาไทยที่เป็นธรรมชาติ สุภาพ และเป็นมิตร
                - ใช้คำเทคนิคภาษาอังกฤษได้ตามปกติ (เช่น function, class, API, commit)
                - อธิบายแนวคิดซับซ้อนด้วยภาษาง่ายๆ
                - เมื่อผู้ใช้ถามเป็นภาษาไทย ตอบเป็นภาษาไทยเสมอ
                - เมื่อผู้ใช้ถามเป็นภาษาอังกฤษ ตอบเป็นภาษาอังกฤษ
                - ถ้าผู้ใช้สลับภาษา ให้สลับตาม
                """);
        }

        return sb.ToString();
    }

    private const int MaxRetries = 2;

    /// <summary>Fires when the agent wants to report status to the UI.</summary>
    public event Action<string>? OnAgentStatus;

    /// <summary>Fires when a tool action completes (for adding to chat UI).</summary>
    public event Action<ToolResult>? OnToolExecuted;

    /// <summary>Fires when the agent produces thinking/reasoning text (real-time display).</summary>
    public event Action<string, int>? OnThinkingUpdate; // text, stepNumber

    public CodeAgentService(
        AiProviderManager providerManager,
        CodeExecutionService codeExecutionService,
        AgentToolService agentToolService,
        FileSystemService fileSystemService,
        SettingsService settingsService,
        SmartEditingService smartEditingService,
        ContextMemoryService contextMemoryService,
        LocalizationService localizationService,
        ActivationService activationService)
    {
        _providerManager = providerManager;
        _codeExecutionService = codeExecutionService;
        _agentToolService = agentToolService;
        _fileSystemService = fileSystemService;
        _settingsService = settingsService;
        _smartEditingService = smartEditingService;
        _contextMemoryService = contextMemoryService;
        _localizationService = localizationService;
        _activationService = activationService;
    }

    /// <summary>Gets system prompt with or without tool definitions based on whether a project is open.</summary>
    public string GetSystemPrompt()
    {
        var sb = new StringBuilder(BuildBaseSystemPrompt());

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

            // Try AI-powered compaction first
            string? compactPrompt = _contextMemoryService.BuildCompactPrompt(compactedHistory);
            if (compactPrompt != null)
            {
                try
                {
                    OnAgentStatus?.Invoke("Summarizing context with AI...");
                    string aiSummary = await _providerManager.ActiveProvider.GenerateAsync(
                        new List<ChatMessage>(),
                        compactPrompt,
                        "You are a conversation summarizer. Produce a concise but complete summary. Preserve ALL technical details.",
                        ct);

                    if (!string.IsNullOrWhiteSpace(aiSummary) && aiSummary.Length > 50)
                    {
                        compactedHistory = _contextMemoryService.CompactWithSummary(compactedHistory, aiSummary);
                        OnAgentStatus?.Invoke("Context compacted with AI summary.");
                    }
                    else
                    {
                        compactedHistory = _contextMemoryService.CompactHistory(compactedHistory);
                    }
                }
                catch
                {
                    // Fallback to simple compaction if AI fails
                    compactedHistory = _contextMemoryService.CompactHistory(compactedHistory);
                }
            }
            else
            {
                compactedHistory = _contextMemoryService.CompactHistory(compactedHistory);
            }
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

            // ─── Generate response with retry ───
            bool isThai = _localizationService.CurrentLanguage == "th";
            progress?.Report(iteration == 0
                ? (isThai ? "กำลังคิด..." : "Thinking...")
                : (isThai ? $"กำลังดำเนินการต่อ... (ขั้นที่ {iteration + 1})" : $"Continuing... (step {iteration + 1})"));
            OnAgentStatus?.Invoke(iteration == 0
                ? (isThai ? "กำลังคิด..." : "Thinking...")
                : (isThai ? $"ขั้นที่ {iteration + 1}..." : $"Agent step {iteration + 1}..."));

            string response = await GenerateWithRetryAsync(
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
                    OnThinkingUpdate?.Invoke(step.ThinkingText ?? "", iteration + 1);
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
            {
                step.ThinkingText = cleanText;
                OnThinkingUpdate?.Invoke(cleanText, iteration + 1);
            }

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
    // Retry-Aware Generation
    // ═══════════════════════════════════════════

    /// <summary>
    /// Generate AI response with automatic retry on transient failures.
    /// Retries up to MaxRetries times with exponential backoff.
    /// </summary>
    private async Task<string> GenerateWithRetryAsync(
        List<ChatMessage> history, string message, string systemPrompt, CancellationToken ct)
    {
        Exception? lastException = null;

        for (int retry = 0; retry <= MaxRetries; retry++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (retry > 0)
                {
                    bool isThai = _localizationService.CurrentLanguage == "th";
                    OnAgentStatus?.Invoke(isThai
                        ? $"ลองใหม่ครั้งที่ {retry}/{MaxRetries}..."
                        : $"Retrying ({retry}/{MaxRetries})...");
                    await Task.Delay(retry * 1000, ct); // exponential backoff
                }

                return await _providerManager.ActiveProvider.GenerateAsync(
                    history, message, systemPrompt, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastException = ex;
                // Only retry on transient/network errors
                if (ex.Message.Contains("429") || ex.Message.Contains("503") ||
                    ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("network", StringComparison.OrdinalIgnoreCase))
                    continue;
                throw; // non-transient error, don't retry
            }
        }

        throw lastException ?? new Exception("Generation failed after retries");
    }

    // ═══════════════════════════════════════════
    // Code Review (can be invoked from chat)
    // ═══════════════════════════════════════════

    /// <summary>
    /// Review code changes made during the session. Returns a review summary.
    /// This is called when the user asks for a code review or at session end.
    /// </summary>
    public async Task<string> ReviewCodeAsync(
        List<ChatMessage> history,
        string? specificFile = null,
        CancellationToken ct = default)
    {
        bool isThai = _localizationService.CurrentLanguage == "th";

        var reviewPrompt = isThai
            ? """
              กรุณารีวิวโค้ดที่เปลี่ยนแปลงในเซสชันนี้ ตรวจสอบ:
              1. ❌ บัก หรือ logic ผิดพลาด
              2. ⚠️ ปัญหาด้านความปลอดภัย (hardcoded secrets, injection, ข้อมูลรั่ว)
              3. 🔧 โค้ดที่ควรปรับปรุง (ซ้ำซ้อน, ซับซ้อนเกินไป, ไม่มี error handling)
              4. 📝 เอกสารที่ขาดหาย
              5. ✅ สิ่งที่ทำได้ดี

              ให้คะแนนรวม: ⭐ 1-5 ดาว
              ให้สรุปสั้นๆ ตามด้วยรายละเอียดแต่ละจุด
              """
            : """
              Please review the code changes made in this session. Check for:
              1. ❌ Bugs or logic errors
              2. ⚠️ Security issues (hardcoded secrets, injection, data leaks)
              3. 🔧 Code that should be improved (duplication, overcomplexity, missing error handling)
              4. 📝 Missing documentation
              5. ✅ Things done well

              Give an overall rating: ⭐ 1-5 stars
              Provide a brief summary followed by details for each point.
              """;

        if (specificFile != null)
        {
            reviewPrompt = (isThai ? $"รีวิวไฟล์: {specificFile}\n\n" : $"Review file: {specificFile}\n\n") + reviewPrompt;

            // Auto-load the file content for context
            if (_fileSystemService.HasWorkingDirectory)
            {
                try
                {
                    string content = _fileSystemService.ReadFile(specificFile);
                    reviewPrompt += $"\n\nFile content:\n```\n{content}\n```";
                }
                catch { /* file might not exist */ }
            }
        }

        string systemPrompt = GetSystemPrompt();
        return await GenerateWithRetryAsync(history, reviewPrompt, systemPrompt, ct);
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
