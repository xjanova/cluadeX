using System.Net.Http;
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
    private readonly MemoryService _memoryService;

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

        // ═══════════════════════════════════════════
        // Section 1: Core Identity
        // ═══════════════════════════════════════════
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

        // ═══════════════════════════════════════════
        // Section 2: Capabilities (conditional on features)
        // ═══════════════════════════════════════════
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

        // ═══════════════════════════════════════════
        // Section 3: Doing Tasks
        // ═══════════════════════════════════════════
        sb.AppendLine();
        sb.AppendLine(isThai ? "# การทำงาน" : "# Doing Tasks");
        sb.AppendLine(isThai
            ? """
              - อ่านโค้ดที่มีอยู่ก่อนเสนอการเปลี่ยนแปลงเสมอ อย่าเดาเนื้อหาไฟล์
              - อย่าสร้างไฟล์ใหม่ถ้าไม่จำเป็น — ใช้ edit_file แก้ไฟล์ที่มีอยู่แทน
              - อย่าเพิ่ม feature, refactor, หรือ "ปรับปรุง" เกินกว่าที่ถูกขอ
              - อย่าเพิ่ม docstrings, comments, หรือ type annotations ในโค้ดที่คุณไม่ได้แก้
              - อย่าเพิ่ม error handling สำหรับสถานการณ์ที่เกิดขึ้นไม่ได้
              - อย่าสร้าง helpers, utilities, หรือ abstractions สำหรับ one-time operations
              - โค้ดที่คล้ายกัน 3 บรรทัดดีกว่า premature abstraction
              - ถ้าแนวทางหนึ่งล้มเหลว ให้วิเคราะห์สาเหตุก่อนเปลี่ยนวิธี — อย่า retry แบบสุ่ม
              """
            : """
              - In general, do not propose changes to code you haven't read. Read it first.
              - Do not create files unless absolutely necessary. Prefer editing existing files.
              - Don't add features, refactor code, or make "improvements" beyond what was asked.
              - Don't add docstrings, comments, or type annotations to code you didn't change.
              - Don't add error handling for scenarios that can't happen. Trust internal code.
              - Don't create helpers, utilities, or abstractions for one-time operations.
              - Three similar lines of code is better than a premature abstraction.
              - If an approach fails, diagnose why before switching tactics — don't retry blindly.
              """);

        // ═══════════════════════════════════════════
        // Section 4: Executing Actions with Care
        // ═══════════════════════════════════════════
        sb.AppendLine();
        sb.AppendLine(isThai ? "# การดำเนินการอย่างระมัดระวัง" : "# Executing Actions with Care");
        sb.AppendLine(isThai
            ? """
              พิจารณาความสามารถในการย้อนกลับและขอบเขตผลกระทบของทุกการกระทำ
              สามารถทำ local, reversible actions ได้อิสระ (แก้ไฟล์, รันเทสต์)
              แต่สำหรับ actions ที่ย้อนกลับยาก หรือกระทบระบบส่วนรวม ให้ถามผู้ใช้ก่อน:

              ตัวอย่าง actions ที่ควรถามก่อน:
              - Destructive operations: ลบไฟล์/branch, drop table, rm -rf, overwrite uncommitted changes
              - Hard-to-reverse: force-push, git reset --hard, amend published commits
              - Actions ที่คนอื่นเห็น: push code, สร้าง/comment PR/issues, ส่งข้อความ
              - การ push ไปยัง remote ถือเป็น action ที่ต้องถามเสมอ

              เมื่อเจออุปสรรค อย่าใช้ destructive actions เป็นทางลัด
              ให้วิเคราะห์สาเหตุรากเหง้าและแก้ปัญหาจริง
              """
            : """
              Carefully consider the reversibility and blast radius of actions.
              You can freely take local, reversible actions like editing files or running tests.
              But for actions that are hard to reverse or affect shared systems, check with the user first.

              Examples of risky actions that warrant user confirmation:
              - Destructive operations: deleting files/branches, dropping tables, rm -rf, overwriting uncommitted changes
              - Hard-to-reverse operations: force-pushing, git reset --hard, amending published commits
              - Actions visible to others: pushing code, creating/commenting on PRs or issues, sending messages
              - Pushing to remote always requires confirmation

              When you encounter an obstacle, do not use destructive actions as a shortcut.
              Investigate root causes and fix underlying issues rather than bypassing safety checks.
              """);

        // ═══════════════════════════════════════════
        // Section 5: Using Your Tools
        // ═══════════════════════════════════════════
        sb.AppendLine();
        sb.AppendLine(isThai ? "# การใช้เครื่องมือ" : "# Using Your Tools");
        sb.AppendLine(isThai
            ? """
              ห้ามใช้ run_command เมื่อมีเครื่องมือเฉพาะทางที่เหมาะกว่า:
              - อ่านไฟล์: ใช้ read_file แทน run_command กับ cat/head/tail
              - แก้ไฟล์: ใช้ edit_file แทน run_command กับ sed/awk
              - สร้างไฟล์: ใช้ write_file แทน run_command กับ echo/cat heredoc
              - ค้นหาไฟล์: ใช้ search_files แทน run_command กับ find/ls
              - ค้นหาเนื้อหา: ใช้ search_content แทน run_command กับ grep/rg
              ใช้ run_command เฉพาะสำหรับ system commands ที่ต้องการ shell execution จริงๆ
              เช่น: build, test, install, git operations ที่ไม่มีเครื่องมือเฉพาะ
              """
            : """
              Do NOT use run_command when a relevant dedicated tool is provided:
              - To read files: use read_file instead of run_command with cat/head/tail
              - To edit files: use edit_file instead of run_command with sed/awk
              - To create files: use write_file instead of run_command with echo/cat heredoc
              - To search for files: use search_files instead of run_command with find/ls
              - To search content: use search_content instead of run_command with grep/rg
              Reserve run_command exclusively for system commands that require shell execution.
              For example: build, test, install, git operations without a dedicated tool.
              """);

        // ═══════════════════════════════════════════
        // Section 6: Rules (Code Quality)
        // ═══════════════════════════════════════════
        sb.AppendLine();
        sb.AppendLine(isThai ? "# กฎการเขียนโค้ด" : "# Code Rules");
        sb.AppendLine(isThai
            ? """
              1. เขียนโค้ดใน markdown code block พร้อมระบุภาษาเสมอ: ```language
              2. เขียนโค้ดครบถ้วน รันได้จริง — ห้ามใช้ placeholder เช่น "..."
              3. จัดการ edge cases และ errors อย่างเหมาะสม
              4. ปฏิบัติตาม best practices และรูปแบบ idiomatic ของภาษานั้นๆ
              5. เมื่อแก้ error ให้วิเคราะห์ error message อย่างละเอียดและให้โค้ดที่แก้ไขแล้วทั้งหมด
              6. ใส่คอมเมนต์เฉพาะ logic ที่ซับซ้อนเท่านั้น — อย่าเพิ่มคอมเมนต์ในโค้ดที่ไม่ได้แก้
              7. เมื่อแก้ไฟล์ ใช้ edit_file กับ find/replace ที่แม่นยำ แทนการเขียนไฟล์ใหม่ทั้งหมด
              8. ตรวจสอบโค้ดในใจก่อนเขียน — ให้แน่ใจว่าวงเล็บ/ปีกกาสมดุล
              9. ห้ามแนะนำ security vulnerabilities (command injection, XSS, SQL injection)
              """
            : """
              1. Always write code inside markdown code blocks with the language specified: ```language
              2. Write complete, runnable code — never use placeholders like "..."
              3. Handle edge cases and errors properly
              4. Follow best practices and idiomatic patterns for the language
              5. When fixing errors, analyze the error message carefully and provide the complete corrected code
              6. Add comments for complex logic only — don't add comments to code you didn't change
              7. When editing files, prefer minimal changes — use edit_file with precise find/replace over rewriting entire files
              8. Validate your code mentally before writing — ensure brackets/braces balance
              9. Do not introduce security vulnerabilities (command injection, XSS, SQL injection, OWASP top 10)
              """);

        // ═══════════════════════════════════════════
        // Section 7: Git Workflow Guidance
        // ═══════════════════════════════════════════
        if (features.GitIntegration && _activationService.IsFeatureUnlocked("feature.git"))
        {
            sb.AppendLine();
            sb.AppendLine(isThai ? "# Git Workflow" : "# Git Workflow");
            sb.AppendLine(isThai
                ? """
                  เมื่อต้อง commit:
                  1. ดู git status และ git diff ก่อนเพื่อดูการเปลี่ยนแปลงทั้งหมด
                  2. ดู git log เพื่อเข้าใจ commit message style ของ repo
                  3. เขียน commit message สั้นกระชับ (1-2 ประโยค) เน้นว่า "ทำไม" มากกว่า "ทำอะไร"
                  4. ห้าม commit ไฟล์ที่อาจมี secrets (.env, credentials.json)
                  5. ใช้ git add เฉพาะไฟล์ที่เกี่ยวข้อง — หลีกเลี่ยง "git add ."
                  6. สร้าง commit ใหม่เสมอ — ห้ามใช้ --amend ยกเว้นผู้ใช้ขอ
                  7. ห้ามใช้ --no-verify หรือ skip hooks ยกเว้นผู้ใช้ขอ

                  เมื่อต้องสร้าง PR:
                  1. ดู git log และ git diff main...HEAD เพื่อเข้าใจการเปลี่ยนแปลงทั้งหมด
                  2. เขียน PR title สั้น (<70 ตัวอักษร) ใช้ description สำหรับรายละเอียด
                  3. ใส่ Summary (bullet points) และ Test plan
                  """
                : """
                  When committing:
                  1. Run git status and git diff first to see all changes
                  2. Run git log to understand the repo's commit message style
                  3. Draft a concise (1-2 sentence) commit message focusing on "why" not "what"
                  4. Never commit files that likely contain secrets (.env, credentials.json)
                  5. Add specific files by name — avoid "git add ." or "git add -A"
                  6. Always create NEW commits — never --amend unless the user explicitly asks
                  7. Never skip hooks (--no-verify) unless the user explicitly asks

                  When creating PRs:
                  1. Run git log and git diff main...HEAD to understand ALL changes
                  2. Keep PR title short (<70 chars), use description for details
                  3. Include a Summary (bullet points) and Test plan
                  """);
        }

        // ═══════════════════════════════════════════
        // Section 8: Advanced Reasoning & Self-Correction
        // ═══════════════════════════════════════════
        sb.AppendLine();
        sb.AppendLine(isThai ? "# การให้เหตุผลขั้นสูง" : "# Advanced Reasoning");
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

              การแก้ไขตัวเอง:
              - ถ้าเครื่องมือ return error ให้อ่าน error ให้ดี แล้วลองวิธีอื่น
              - ถ้า edit_file ไม่เจอข้อความ ให้อ่านไฟล์ก่อนเพื่อดูเนื้อหาจริง
              - ถ้า run_command ล้มเหลว ให้วิเคราะห์ output และปรับคำสั่ง
              - อย่ายอมแพ้ง่ายๆ — ลองวิธีต่างๆ อย่างน้อย 2-3 วิธี
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

              Self-Correction:
              - If a tool returns an error, read the error carefully and try a different approach
              - If edit_file can't find the text, read the file first to see actual content
              - If run_command fails, analyze the output and adjust the command
              - Don't give up easily — try at least 2-3 different approaches
              """);

        // ═══════════════════════════════════════════
        // Section 9: Tone & Style
        // ═══════════════════════════════════════════
        sb.AppendLine();
        sb.AppendLine(isThai ? "# สไตล์การตอบ" : "# Tone & Style");
        sb.AppendLine(isThai
            ? """
              - ตอบสั้นกระชับ ตรงประเด็น
              - ใช้ emojis เฉพาะเมื่อผู้ใช้ขอ
              - เมื่ออ้างอิงโค้ด ใช้รูปแบบ file_path:line_number
              - ใช้ภาษาไทยที่เป็นธรรมชาติ สุภาพ และเป็นมิตร
              - ใช้คำเทคนิคภาษาอังกฤษได้ตามปกติ (function, class, API, commit)
              - อธิบายแนวคิดซับซ้อนด้วยภาษาง่ายๆ
              - เมื่อผู้ใช้ถามเป็นภาษาไทย ตอบเป็นภาษาไทยเสมอ
              - เมื่อผู้ใช้ถามเป็นภาษาอังกฤษ ตอบเป็นภาษาอังกฤษ
              - ถ้าผู้ใช้สลับภาษา ให้สลับตาม
              """
            : """
              - Your responses should be short and concise
              - Only use emojis if the user explicitly requests it
              - When referencing code, include file_path:line_number format
              - Lead with the answer or action, not the reasoning
              - Skip filler words, preamble, and unnecessary transitions
              - Do not restate what the user said — just do it
              - If you can say it in one sentence, don't use three
              """);

        // ═══════════════════════════════════════════
        // Section 10: Output Efficiency
        // ═══════════════════════════════════════════
        sb.AppendLine();
        sb.AppendLine(isThai ? "# ประสิทธิภาพ Output" : "# Output Efficiency");
        sb.AppendLine(isThai
            ? """
              สำคัญ: ตรงประเด็นเลย ลองวิธีง่ายที่สุดก่อน อย่าทำเกินจำเป็น

              เน้น output เฉพาะ:
              - การตัดสินใจที่ต้องการ input จากผู้ใช้
              - สถานะอัพเดทเมื่อถึง milestone สำคัญ
              - Errors หรือ blockers ที่เปลี่ยนแผน
              """
            : """
              IMPORTANT: Go straight to the point. Try the simplest approach first. Do not overdo it.

              Focus text output on:
              - Decisions that need the user's input
              - High-level status updates at natural milestones
              - Errors or blockers that change the plan
              """);

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
        ActivationService activationService,
        MemoryService memoryService)
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
        _memoryService = memoryService;
    }

    /// <summary>Gets system prompt with or without tool definitions based on whether a project is open.</summary>
    public string GetSystemPrompt()
    {
        var sb = new StringBuilder(BuildBaseSystemPrompt());

        // ═══════════════════════════════════════════
        // Dynamic Section: Environment Info
        // ═══════════════════════════════════════════
        sb.AppendLine();
        sb.AppendLine("# Environment");
        sb.AppendLine($"- Platform: {Environment.OSVersion.VersionString}");
        sb.AppendLine($"- Shell: PowerShell / cmd");
        sb.AppendLine($"- Model: {_providerManager.ActiveProvider?.GetType().Name ?? "Unknown"}");
        sb.AppendLine($"- Date: {DateTime.Now:yyyy-MM-dd}");
        sb.AppendLine($"- CluadeX Version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.1.0"}");

        if (_fileSystemService.HasWorkingDirectory)
        {
            sb.AppendLine($"- Working Directory: {_fileSystemService.WorkingDirectory}");

            // ═══════════════════════════════════════════
            // Dynamic Section: Git Status (injected at session start)
            // ═══════════════════════════════════════════
            if (_settingsService.Settings.Features.GitIntegration
                && _activationService.IsFeatureUnlocked("feature.git"))
            {
                try
                {
                    string gitBranch = GetGitBranchSync();
                    if (!string.IsNullOrEmpty(gitBranch))
                    {
                        sb.AppendLine($"- Git Branch: {gitBranch.Trim()}");
                        string gitStatus = GetGitStatusSync();
                        if (!string.IsNullOrWhiteSpace(gitStatus))
                        {
                            sb.AppendLine("- Git Status:");
                            // Limit git status to first 20 lines
                            var lines = gitStatus.Split('\n');
                            foreach (var line in lines.Take(20))
                                sb.AppendLine($"  {line}");
                            if (lines.Length > 20)
                                sb.AppendLine($"  ... ({lines.Length - 20} more files)");
                        }
                        else
                        {
                            sb.AppendLine("- Git Status: clean (no changes)");
                        }
                    }
                }
                catch { /* not a git repo or git not available */ }
            }

            // ═══════════════════════════════════════════
            // Dynamic Section: Tool Definitions
            // ═══════════════════════════════════════════
            sb.AppendLine();
            sb.AppendLine(_agentToolService.GetToolDefinitionsPrompt());
            sb.AppendLine();

            // Include project tree (up to 3 levels, larger budget)
            try
            {
                string tree = _fileSystemService.GetProjectTree(3);
                if (tree.Length > 4000)
                    tree = tree[..4000] + "\n... (truncated)";
                sb.AppendLine("PROJECT STRUCTURE:");
                sb.AppendLine(tree);
            }
            catch { /* ignore */ }

            // Auto-read key project files for context
            sb.AppendLine();
            sb.AppendLine("KEY PROJECT FILES:");
            AppendKeyFileIfExists(sb, "CLAUDE.md");
            AppendKeyFileIfExists(sb, ".claude/CLAUDE.md");
            AppendKeyFileIfExists(sb, ".cluadex/CLAUDE.md");
            AppendKeyFileIfExists(sb, "README.md");
            AppendKeyFileIfExists(sb, "package.json", 500);
            AppendKeyFileIfExists(sb, "Cargo.toml", 300);
            AppendKeyFileIfExists(sb, "pyproject.toml", 300);
            AppendKeyFileIfExists(sb, ".gitignore", 200);

            // Detect project type and add specific context
            string projType = DetectProjectType();
            if (!string.IsNullOrEmpty(projType))
            {
                sb.AppendLine();
                sb.AppendLine($"DETECTED PROJECT TYPE: {projType}");
            }
        }

        // ═══════════════════════════════════════════
        // Dynamic Section: Memory (MEMORY.md from global + project)
        // ═══════════════════════════════════════════
        try
        {
            string memoryContent = _memoryService.LoadMemoryIndex();
            if (!string.IsNullOrWhiteSpace(memoryContent))
            {
                sb.AppendLine();
                sb.AppendLine("# Memory");
                sb.AppendLine(memoryContent);
            }
        }
        catch { /* memory not available */ }

        return sb.ToString();
    }

    /// <summary>Get current git branch synchronously (for prompt injection). Safe process pattern.</summary>
    private string GetGitBranchSync()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "branch --show-current")
            {
                WorkingDirectory = _fileSystemService.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = false, // Don't redirect stderr to avoid deadlock
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return "";
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return output.Trim();
        }
        catch { return ""; }
    }

    /// <summary>Get short git status synchronously (for prompt injection). Safe process pattern.</summary>
    private string GetGitStatusSync()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "status --short")
            {
                WorkingDirectory = _fileSystemService.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = false, // Don't redirect stderr to avoid deadlock
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return "";
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return output.Trim();
        }
        catch { return ""; }
    }

    /// <summary>Read a key project file and append to system prompt if it exists.</summary>
    private void AppendKeyFileIfExists(StringBuilder sb, string relativePath, int maxChars = 1000)
    {
        try
        {
            string content = _fileSystemService.ReadFile(relativePath);
            if (string.IsNullOrWhiteSpace(content)) return;

            if (content.Length > maxChars)
                content = content[..maxChars] + "\n... (truncated)";

            sb.AppendLine($"\n--- {relativePath} ---");
            sb.AppendLine(content);
        }
        catch { /* file doesn't exist — skip */ }
    }

    /// <summary>Detect the project type based on key files.</summary>
    private string DetectProjectType()
    {
        var types = new List<string>();

        bool hasFile(string name)
        {
            try { _fileSystemService.ReadFile(name); return true; }
            catch { return false; }
        }

        // .NET / C#
        if (_fileSystemService.SearchFiles("*.csproj", ".").Count > 0 || _fileSystemService.SearchFiles("*.sln", ".").Count > 0)
            types.Add("C# / .NET");

        // Node.js / TypeScript
        if (hasFile("package.json"))
        {
            types.Add("Node.js");
            if (hasFile("tsconfig.json")) types.Add("TypeScript");
        }

        // Python
        if (hasFile("pyproject.toml") || hasFile("setup.py") || hasFile("requirements.txt"))
            types.Add("Python");

        // Rust
        if (hasFile("Cargo.toml")) types.Add("Rust");

        // Go
        if (hasFile("go.mod")) types.Add("Go");

        // Java
        if (hasFile("pom.xml") || hasFile("build.gradle")) types.Add("Java");

        // Flutter / Dart
        if (hasFile("pubspec.yaml")) types.Add("Flutter / Dart");

        return string.Join(", ", types);
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
        // ─── Dual-mode dispatch: Native tool_use vs legacy [ACTION:] ───
        if (_providerManager.ActiveProvider.SupportsNativeToolUse)
        {
            return await ExecuteNativeToolLoopAsync(history, userMessage, progress, ct);
        }

        return await ExecuteLegacyToolLoopAsync(history, userMessage, progress, ct);
    }

    /// <summary>Legacy agentic loop using [ACTION:] text parsing (for non-Anthropic providers).</summary>
    private async Task<AgentLoopResult> ExecuteLegacyToolLoopAsync(
        List<ChatMessage> history,
        string userMessage,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var result = new AgentLoopResult();
        string systemPrompt = GetSystemPrompt();

        // ─── Smart context enrichment ───
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
        int maxTokenRecoveryCount = 0;
        bool hasAttemptedReactiveCompact = false;
        const int MaxOutputTokenRecoveries = 3;

        for (int iteration = 0; iteration < MaxAgentIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            result.TurnCount = iteration + 1;

            var step = new AgentStep { StepNumber = iteration + 1 };

            // ─── Generate response with retry ───
            bool isThai = _localizationService.CurrentLanguage == "th";
            progress?.Report(iteration == 0
                ? (isThai ? "กำลังคิด..." : "Thinking...")
                : (isThai ? $"กำลังดำเนินการต่อ... (ขั้นที่ {iteration + 1}/{MaxAgentIterations})" : $"Step {iteration + 1}/{MaxAgentIterations}..."));
            OnAgentStatus?.Invoke(iteration == 0
                ? (isThai ? "กำลังคิด..." : "Thinking...")
                : (isThai ? $"ขั้นที่ {iteration + 1}/{MaxAgentIterations}..." : $"Step {iteration + 1}/{MaxAgentIterations}..."));

            string response;
            try
            {
                response = await GenerateWithRetryAsync(
                    workingHistory, currentMessage, systemPrompt, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge)
            {
                // ─── Reactive compaction on 413 (prompt too long) ───
                if (!hasAttemptedReactiveCompact)
                {
                    hasAttemptedReactiveCompact = true;
                    OnAgentStatus?.Invoke(isThai ? "Context เต็ม — กำลังบีบอัด..." : "Context overflow — compacting...");
                    workingHistory = ForceCompactHistory(workingHistory);
                    continue; // retry with compacted history
                }
                result.StopReason = "error";
                result.FinalResponse = isThai
                    ? "Context ยาวเกินไปแม้หลังบีบอัดแล้ว กรุณาเริ่ม session ใหม่"
                    : "Context too long even after compaction. Please start a new session.";
                break;
            }
            step.ResponseText = response;

            // ─── Max output token recovery ───
            // If the response looks truncated (ends mid-sentence, mid-code-block), retry
            if (IsResponseTruncated(response) && maxTokenRecoveryCount < MaxOutputTokenRecoveries)
            {
                maxTokenRecoveryCount++;
                OnAgentStatus?.Invoke(isThai
                    ? $"คำตอบถูกตัด — กำลังขอต่อ... ({maxTokenRecoveryCount}/{MaxOutputTokenRecoveries})"
                    : $"Response truncated — requesting continuation... ({maxTokenRecoveryCount}/{MaxOutputTokenRecoveries})");

                workingHistory.Add(new ChatMessage { Role = MessageRole.Assistant, Content = response });
                workingHistory.Add(new ChatMessage
                {
                    Role = MessageRole.System,
                    Content = "Your response was truncated. Please continue from where you left off."
                });
                currentMessage = "Continue from where you left off.";
                result.Steps.Add(step);
                continue;
            }

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
                result.StopReason = "end_turn";
                break;
            }

            // ─── Execute tools (parallel for concurrency-safe, sequential for others) ───
            string cleanText = _agentToolService.StripToolCalls(response);
            if (!string.IsNullOrWhiteSpace(cleanText))
            {
                step.ThinkingText = cleanText;
                OnThinkingUpdate?.Invoke(cleanText, iteration + 1);
            }

            progress?.Report($"Running {toolCalls.Count} tool(s)...");
            OnAgentStatus?.Invoke($"Executing {toolCalls.Count} tool(s)...");

            var toolResults = await ExecuteToolCallsAsync(toolCalls, progress, ct);
            step.ToolResults = toolResults;
            result.Steps.Add(step);

            // Fire events for UI
            foreach (var tr in toolResults)
                OnToolExecuted?.Invoke(tr);

            // ─── Feed results back to model (with budget enforcement) ───
            workingHistory.Add(new ChatMessage
            {
                Role = MessageRole.Assistant,
                Content = response,
            });

            string toolFeedback = FormatToolResultsWithBudget(toolResults);
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
            result.StopReason = "max_iterations";
            result.FinalResponse = result.Steps.LastOrDefault()?.ResponseText ?? "Agent reached maximum iterations.";
            OnAgentStatus?.Invoke("Agent loop complete.");
        }

        progress?.Report("Done");
        OnAgentStatus?.Invoke("Ready");
        return result;
    }

    // ═══════════════════════════════════════════
    // Parallel Tool Execution (Phase 2)
    // ═══════════════════════════════════════════

    /// <summary>
    /// Execute tool calls with parallel execution for concurrency-safe tools.
    /// Partitions into concurrent batch (read-only) and sequential batch (writes/executes).
    /// </summary>
    private async Task<List<ToolResult>> ExecuteToolCallsAsync(
        List<ToolCall> toolCalls, IProgress<string>? progress, CancellationToken ct)
    {
        var results = new List<ToolResult>();

        // Partition: concurrent-safe first, then sequential
        var concurrent = toolCalls.Where(c => c.IsConcurrencySafe).ToList();
        var sequential = toolCalls.Where(c => !c.IsConcurrencySafe).ToList();

        // Run concurrent batch in parallel
        if (concurrent.Count > 0)
        {
            progress?.Report($"Running {concurrent.Count} read tool(s) in parallel...");
            var parallelTasks = concurrent.Select(async call =>
            {
                ct.ThrowIfCancellationRequested();
                var toolResult = await _agentToolService.ExecuteToolAsync(call, ct);
                return toolResult;
            });

            var concurrentResults = await Task.WhenAll(parallelTasks);
            results.AddRange(concurrentResults);
        }

        // Run sequential batch one-by-one
        foreach (var call in sequential)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Running: {call.ToolName}...");
            OnAgentStatus?.Invoke($"Tool: {call.ToolName}...");

            var toolResult = await _agentToolService.ExecuteToolAsync(call, ct);

            // Smart validation for write/edit operations
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

            results.Add(toolResult);
        }

        return results;
    }

    // ═══════════════════════════════════════════
    // Tool Result Budget Enforcement (Phase 2)
    // ═══════════════════════════════════════════

    private const int MaxPerToolOutputChars = 50_000;
    private const int MaxAggregateOutputChars = 200_000;

    /// <summary>
    /// Format tool results with per-tool and aggregate size budgets.
    /// Truncates large outputs to prevent context overflow.
    /// </summary>
    private string FormatToolResultsWithBudget(List<ToolResult> results)
    {
        // Truncate individual results WITHOUT mutating originals
        var truncated = results.Select(r =>
        {
            if (r.Output.Length > MaxPerToolOutputChars)
            {
                return new ToolResult
                {
                    Type = r.Type, ToolName = r.ToolName, Success = r.Success,
                    Error = r.Error, Summary = r.Summary,
                    Output = r.Output[..MaxPerToolOutputChars]
                        + $"\n... (truncated, {r.Output.Length:N0} chars total)",
                };
            }
            return r;
        }).ToList();

        string formatted = _agentToolService.FormatToolResults(truncated);

        // Enforce aggregate budget
        if (formatted.Length > MaxAggregateOutputChars)
        {
            int originalLen = formatted.Length;
            formatted = formatted[..MaxAggregateOutputChars]
                + $"\n... (aggregate output truncated, {originalLen:N0} chars total)";
        }

        return formatted;
    }

    // ═══════════════════════════════════════════
    // Response Truncation Detection (Phase 2)
    // ═══════════════════════════════════════════

    /// <summary>
    /// Detect if the model response was likely truncated (max_tokens hit).
    /// Heuristics: unclosed code blocks, ends mid-word, or ends with incomplete ACTION block.
    /// </summary>
    private static bool IsResponseTruncated(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;

        // Check for unclosed markdown code blocks
        int openFences = 0;
        foreach (var line in response.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```"))
                openFences++;
        }
        if (openFences % 2 != 0) return true;

        // Check for unclosed [ACTION:] block
        int actionOpens = System.Text.RegularExpressions.Regex.Matches(response, @"\[ACTION:").Count;
        int actionCloses = System.Text.RegularExpressions.Regex.Matches(response, @"\[/ACTION\]").Count;
        if (actionOpens > actionCloses) return true;

        return false;
    }

    /// <summary>
    /// Force-compact the working history aggressively (for reactive compaction on 413).
    /// Keeps only the last 5 messages and prepends a summary note.
    /// </summary>
    private List<ChatMessage> ForceCompactHistory(List<ChatMessage> history)
    {
        if (history.Count <= 5) return history;

        int keepCount = Math.Min(5, history.Count);
        var kept = history.TakeLast(keepCount).ToList();

        kept.Insert(0, new ChatMessage
        {
            Role = MessageRole.System,
            Content = "[Context was compacted due to length. Earlier conversation history has been summarized. " +
                      $"Original history had {history.Count} messages.]",
        });

        return kept;
    }

    // ═══════════════════════════════════════════
    // Native Tool Use Agentic Loop (Anthropic)
    // ═══════════════════════════════════════════

    /// <summary>
    /// Agentic loop using Anthropic's native tool_use API format.
    /// Sends structured tool schemas, receives tool_use blocks, sends tool_result blocks.
    /// </summary>
    private async Task<AgentLoopResult> ExecuteNativeToolLoopAsync(
        List<ChatMessage> history,
        string userMessage,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var result = new AgentLoopResult();
        string systemPrompt = GetSystemPrompt();
        bool isThai = _localizationService.CurrentLanguage == "th";

        // ─── Smart context enrichment (same as legacy loop) ───
        if (_fileSystemService.HasWorkingDirectory)
        {
            var mentionedFiles = _smartEditingService.ExtractMentionedFiles(userMessage);
            if (mentionedFiles.Count > 0)
            {
                userMessage = _smartEditingService.EnhanceEditRequest(userMessage, mentionedFiles);
                OnAgentStatus?.Invoke($"Loaded context for {mentionedFiles.Count} referenced file(s)");
            }
        }

        // Build native tool schemas
        var toolSchemas = _agentToolService.BuildNativeToolSchemas();

        // Build initial messages (with history compaction)
        var nativeMessages = new List<Services.Providers.NativeMessage>();

        // Use last 20 messages (compact if conversation is longer)
        var relevantHistory = history.Count > 20 ? history.TakeLast(20).ToList() : history;
        foreach (var msg in relevantHistory)
        {
            string role = msg.Role == MessageRole.Assistant ? "assistant" : "user";
            if (msg.Role == MessageRole.System || msg.Role == MessageRole.ToolAction)
                role = "user"; // System/tool messages sent as user role

            var nMsg = new Services.Providers.NativeMessage { Role = role };
            nMsg.Content.Add(new Services.Providers.ContentBlock
            {
                Type = "text",
                Text = msg.Content,
            });
            nativeMessages.Add(nMsg);
        }

        // Add current user message
        nativeMessages.Add(new Services.Providers.NativeMessage
        {
            Role = "user",
            Content = { new Services.Providers.ContentBlock { Type = "text", Text = userMessage } },
        });

        // Ensure alternating roles
        nativeMessages = EnsureAlternatingRoles(nativeMessages);

        for (int iteration = 0; iteration < MaxAgentIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            result.TurnCount = iteration + 1;

            var step = new AgentStep { StepNumber = iteration + 1 };

            progress?.Report(iteration == 0
                ? (isThai ? "กำลังคิด..." : "Thinking...")
                : (isThai ? $"ขั้นที่ {iteration + 1}/{MaxAgentIterations}..." : $"Step {iteration + 1}/{MaxAgentIterations}..."));
            OnAgentStatus?.Invoke(iteration == 0
                ? (isThai ? "กำลังคิด..." : "Thinking...")
                : (isThai ? $"ขั้นที่ {iteration + 1}/{MaxAgentIterations}..." : $"Step {iteration + 1}/{MaxAgentIterations}..."));

            // Call API with native tools
            Services.Providers.NativeToolResponse response;
            try
            {
                response = await _providerManager.ActiveProvider.ChatWithToolsAsync(
                    nativeMessages, systemPrompt, toolSchemas, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge)
            {
                // Keep only last 5 messages and retry
                if (nativeMessages.Count > 5)
                {
                    nativeMessages = nativeMessages.TakeLast(5).ToList();
                    nativeMessages = EnsureAlternatingRoles(nativeMessages);
                    continue;
                }
                result.StopReason = "error";
                result.FinalResponse = "Context too long. Please start a new session.";
                break;
            }

            // Display thinking content
            if (!string.IsNullOrEmpty(response.ThinkingContent))
            {
                step.ThinkingText = response.ThinkingContent;
                OnThinkingUpdate?.Invoke(response.ThinkingContent, iteration + 1);
            }

            step.ResponseText = response.TextContent ?? "";

            // No tool calls = final response
            if (response.ToolCalls.Count == 0)
            {
                result.Steps.Add(step);
                result.FinalResponse = response.TextContent ?? "";
                result.Success = true;
                result.StopReason = response.StopReason;
                break;
            }

            // Build assistant message with text + tool_use blocks
            var assistantMsg = new Services.Providers.NativeMessage { Role = "assistant" };
            if (!string.IsNullOrEmpty(response.TextContent))
            {
                assistantMsg.Content.Add(new Services.Providers.ContentBlock
                {
                    Type = "text",
                    Text = response.TextContent,
                });

                // Show the text to UI
                if (!string.IsNullOrWhiteSpace(response.TextContent))
                    OnThinkingUpdate?.Invoke(response.TextContent, iteration + 1);
            }

            // Execute tool calls and build tool_result blocks
            var userResultMsg = new Services.Providers.NativeMessage { Role = "user" };
            var toolResults = new List<ToolResult>();

            foreach (var toolCall in response.ToolCalls)
            {
                // Add tool_use block to assistant message
                assistantMsg.Content.Add(new Services.Providers.ContentBlock
                {
                    Type = "tool_use",
                    Id = toolCall.Id,
                    Name = toolCall.Name,
                    Input = toolCall.Input,
                });

                // Resolve and execute tool
                ct.ThrowIfCancellationRequested();
                OnAgentStatus?.Invoke($"Tool: {toolCall.Name}...");
                progress?.Report($"Running: {toolCall.Name}...");

                var call = new ToolCall
                {
                    ToolName = toolCall.Name,
                    Type = _agentToolService.ResolveToolTypePublic(toolCall.Name) ?? ToolType.RunCommand,
                    Arguments = ParseJsonInputToArgs(toolCall.Input),
                };

                var toolResult = await _agentToolService.ExecuteToolAsync(call, ct);
                toolResults.Add(toolResult);
                OnToolExecuted?.Invoke(toolResult);

                // Add tool_result block to user message
                string resultContent = toolResult.Success ? toolResult.Output : toolResult.Error;
                if (resultContent.Length > MaxPerToolOutputChars)
                    resultContent = resultContent[..MaxPerToolOutputChars] + "\n... (truncated)";

                userResultMsg.Content.Add(new Services.Providers.ContentBlock
                {
                    Type = "tool_result",
                    ToolUseId = toolCall.Id,
                    Content = resultContent,
                    IsError = !toolResult.Success,
                });
            }

            step.ToolResults = toolResults;
            step.ToolCalls = toolResults.Select(r => new ToolCall
            {
                ToolName = r.ToolName,
                Type = r.Type,
            }).ToList();
            result.Steps.Add(step);

            // Add messages to conversation
            nativeMessages.Add(assistantMsg);
            nativeMessages.Add(userResultMsg);
        }

        if (!result.Success)
        {
            result.StopReason = "max_iterations";
            result.FinalResponse = result.Steps.LastOrDefault()?.ResponseText ?? "Agent reached maximum iterations.";
        }

        progress?.Report("Done");
        OnAgentStatus?.Invoke("Ready");
        return result;
    }

    /// <summary>Parse JSON input element to string dictionary for ToolCall args.</summary>
    private static Dictionary<string, string> ParseJsonInputToArgs(System.Text.Json.JsonElement input)
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (input.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var prop in input.EnumerateObject())
                {
                    args[prop.Name] = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        ? prop.Value.GetString() ?? ""
                        : prop.Value.GetRawText();
                }
            }
        }
        catch { }
        return args;
    }

    /// <summary>Ensure message list has strictly alternating user/assistant roles.</summary>
    private static List<Services.Providers.NativeMessage> EnsureAlternatingRoles(
        List<Services.Providers.NativeMessage> messages)
    {
        if (messages.Count == 0) return messages;

        var result = new List<Services.Providers.NativeMessage>();
        string? lastRole = null;

        foreach (var msg in messages)
        {
            if (msg.Role == lastRole && result.Count > 0)
            {
                // Merge into previous message
                result[^1].Content.AddRange(msg.Content);
            }
            else
            {
                result.Add(msg);
                lastRole = msg.Role;
            }
        }

        // Ensure first message is user
        if (result.Count > 0 && result[0].Role == "assistant")
        {
            result.Insert(0, new Services.Providers.NativeMessage
            {
                Role = "user",
                Content = { new Services.Providers.ContentBlock { Type = "text", Text = "[Conversation continues]" } },
            });
        }

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
    public int TurnCount { get; set; }
    public string StopReason { get; set; } = "end_turn"; // end_turn, max_iterations, max_tokens, error
}

public class AgentResult
{
    public bool Success { get; set; }
    public string Response { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public List<CodeBlock> CodeBlocks { get; set; } = new();
    public CodeExecutionResult? ExecutionResult { get; set; }
}
