using System.IO;
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
    private readonly HookService? _hookService;
    private readonly CostTrackingService? _costTrackingService;

    // Agentic loop step cap — user-configurable (was a hardcoded 15 that cut off complex tasks mid-flight).
    private int MaxAgentIterations => Math.Clamp(_settingsService.Settings.MaxAgentIterations, 1, 100);

    // High-value BUILT-IN tools offered to a SMALL-context local model (the full ~46-schema catalogue eats
    // 40-70% of a 4k window). This list is BUILT-INS ONLY — MCP tools are selected by budget, not by name
    // (see FitLocalToolSchemas). The small-ctx NOTE in GetSystemPrompt is derived from the same fit, so
    // prompt + schemas cannot drift.
    private static readonly HashSet<string> CoreLocalToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "list_files", "search_content", "search_files", "codebase_search",
        "find_symbol", "list_symbols", "edit_file", "multi_edit", "write_file",
        "run_command", "run_build", "run_tests",
        // Git finishing tools — small-ctx local models otherwise only have run_command for git, and
        // weak models tend to shell-chain `git add && git commit && git merge` with POSIX quoting that
        // breaks on Windows. The dedicated tools are reliable (temp-file commit msg, validated branch).
        // These names only survive the subset filter when the Git feature already emitted their schemas.
        // git_push is intentionally excluded — it's the paid "publish to remote" step (feature.github).
        "git_status", "git_add", "git_commit", "git_merge",
        // brain_recall stays in the core set: the system prompt instructs the model to consult BrainX,
        // and stripping the tool here while keeping the instruction made small-ctx local models the ONLY
        // tier that couldn't reach the brain (CluadeX ↔ BrainX are meant to be used together, always).
        "brain_recall",
        // Same contradiction as brain_recall: section 10 of the system prompt lists the available
        // skills and tells the model to run them with skill_invoke, so the tool has to survive the
        // subset filter or "/commit" style requests dead-end on a tool the model was told to use.
        "skill_invoke",
    };

    // ─── Local tool-catalogue fit ───────────────────────────────────────────────────────────────
    private static bool IsMcpToolName(string name) => name.StartsWith("mcp__", StringComparison.Ordinal);

    /// <summary>Server key out of a qualified MCP name (mcp__{server}__{tool}).</summary>
    private static string McpServerOf(string qualifiedName)
    {
        int start = "mcp__".Length;
        int end = qualifiedName.IndexOf("__", start, StringComparison.Ordinal);
        return end > start ? qualifiedName[start..end] : qualifiedName;
    }

    /// <summary>
    /// Token cost of one schema once --jinja renders the OpenAI "tools" field into the prompt. Uses the
    /// shared <see cref="CluadeX.Helpers.TokenBudget.EstimateTokens"/> (biased high, and counts non-ASCII
    /// at 1 token/char) — a flat chars/4 under-counted the real prompt by ~25% and let the fit overflow
    /// the window it exists to protect. The constant covers the per-tool JSON envelope
    /// ({"type":"function","function":{"name":…,"description":…,"parameters":…}}).
    /// </summary>
    private static int EstimateSchemaTokens(Services.Providers.ToolSchema t)
    {
        string raw = "";
        try
        {
            if (t.InputSchema.ValueKind != System.Text.Json.JsonValueKind.Undefined)
                raw = t.InputSchema.GetRawText();
        }
        catch { raw = new string('x', 256); }
        return CluadeX.Helpers.TokenBudget.EstimateTokens(t.Name + t.Description + raw) + 16;
    }

    /// <summary>
    /// Choose the tool schemas a LOCAL model gets, fitted to its context window.
    ///
    /// Built-ins trim to <see cref="CoreLocalToolNames"/> below 16k ctx — the full catalogue costs
    /// 6-8k tokens once the server's chat template renders it and overflows a small window.
    ///
    /// MCP tools are NOT name-trimmed. A whitelist of built-in names can never match a qualified
    /// "mcp__server__tool", so filtering by that list dropped EVERY MCP tool at small ctx — silently,
    /// and while the prompt told the model it had a core set that never mentioned them. The user
    /// configured those servers deliberately and nothing built-in substitutes for them, so they are
    /// bounded only by a token budget, which bites solely when a large fleet of servers would eat the
    /// window. That budget applies at EVERY local ctx: the old ≥16k path sent the whole MCP catalogue
    /// unmetered, which is its own overflow (30 BrainX schemas alone measure ~5.6k tokens).
    ///
    /// Admission order is core built-ins → MCP → the remaining built-ins. The long tail of built-ins
    /// yielding to MCP is deliberate: everything in the tail has a run_command fallback, an MCP server
    /// has none.
    /// </summary>
    /// <param name="reservedTokens">
    /// Everything the request needs that ISN'T schemas — system prompt, history, and generation. Measured
    /// by the caller, never guessed: a hardcoded fraction under-reserved a Thai system prompt and the
    /// request overflowed at n_prompt_tokens=12729 against a 12288 window.
    /// </param>
    private static List<Services.Providers.ToolSchema> FitLocalToolSchemas(
        List<Services.Providers.ToolSchema> all, int ctxTokens, int reservedTokens,
        out int mcpKept, out int mcpDropped)
    {
        var builtIns = all.Where(t => !IsMcpToolName(t.Name)).ToList();
        var mcp = all.Where(t => IsMcpToolName(t.Name)).ToList();

        var core = builtIns.Where(t => CoreLocalToolNames.Contains(t.Name)).ToList();
        if (core.Count == 0) core = builtIns; // guard: never nuke all tools on a name mismatch
        var tail = ctxTokens < 16000
            ? new List<Services.Providers.ToolSchema>()          // small ctx: core built-ins only
            : builtIns.Except(core).ToList();

        int budget = Math.Max(600, ctxTokens - reservedTokens);
        var kept = new List<Services.Providers.ToolSchema>();
        int keptMcp = 0, droppedMcp = 0;

        void Admit(List<Services.Providers.ToolSchema> group, bool countMcp)
        {
            foreach (var t in group)
            {
                int cost = EstimateSchemaTokens(t);
                if (cost > budget) { if (countMcp) droppedMcp++; continue; }
                budget -= cost;
                kept.Add(t);
                if (countMcp) keptMcp++;
            }
        }

        Admit(core, false);
        Admit(mcp, true); // registration order — the server's own idea of what matters first
        Admit(tail, false);

        mcpKept = keptMcp;
        mcpDropped = droppedMcp;
        return kept;
    }

    // ─── System prompt cache (avoids blocking git/file I/O on UI thread) ───
    private string? _cachedSystemPrompt;
    private DateTime _promptCacheExpiry = DateTime.MinValue;
    private readonly object _promptCacheLock = new();

    /// <summary>
    /// Pre-build the system prompt on a background thread.
    /// Call this before ChatStreamAsync to avoid blocking the UI thread.
    /// </summary>
    public async Task<string> GetSystemPromptAsync()
    {
        // Return cached if still valid (cache for 30 seconds)
        lock (_promptCacheLock)
        {
            if (_cachedSystemPrompt != null && DateTime.UtcNow < _promptCacheExpiry)
                return _cachedSystemPrompt;
        }

        string prompt = await Task.Run(() => GetSystemPrompt());

        lock (_promptCacheLock)
        {
            _cachedSystemPrompt = prompt;
            _promptCacheExpiry = DateTime.UtcNow.AddSeconds(30);
        }

        return prompt;
    }

    /// <summary>Invalidate the cached system prompt (e.g., when working directory changes).</summary>
    public void InvalidateSystemPromptCache()
    {
        lock (_promptCacheLock)
        {
            _cachedSystemPrompt = null;
            _promptCacheExpiry = DateTime.MinValue;
        }
    }

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
            // Local git only — push lives behind feature.github, so listing it here would
            // promise a capability the runtime gate denies.
            sb.AppendLine(isThai
                ? "- จัดการ Git ในเครื่อง: status, add, commit, pull, branch, checkout, merge, diff, log, stash"
                : "- Local Git version control: status, add, commit, pull, branch, checkout, merge, diff, log, stash");
        }
        if (features.GitHubIntegration && _activationService.IsFeatureUnlocked("feature.github"))
        {
            sb.AppendLine(isThai
                ? "- เชื่อมต่อ GitHub: push ขึ้น remote, สร้าง PR, ดู issues, จัดการ repo (ต้องติดตั้ง gh CLI)"
                : "- GitHub integration: push to a remote, create PRs, list issues, view repos (requires gh CLI)");
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
        // Section 3a: Respond efficiently (don't over-call tools on trivial asks)
        // ═══════════════════════════════════════════
        sb.AppendLine();
        sb.AppendLine(isThai ? "# ตอบอย่างมีประสิทธิภาพ" : "# Respond Efficiently");
        sb.AppendLine(isThai
            ? """
              - คำถามง่ายๆ ทักทาย หรือพูดคุยทั่วไป (เช่น "ใช้โมเดลอะไร", "สวัสดี") ให้ตอบตรงๆ ทันที — อย่าเรียก tool ถ้ามันไม่ได้ช่วยอะไร
              - ข้อมูลพื้นฐาน (โมเดล, ไดเรกทอรี, branch) อยู่ในหัวข้อ Environment ด้านล่างแล้ว ตอบจากตรงนั้นได้เลย ไม่ต้องเรียก tool
              - เรียกอ่านไฟล์/รันคำสั่งเฉพาะเมื่องานนั้นต้องใช้บริบทโปรเจกต์จริงๆ หรือต้องลงมือแก้/ตรวจสอบ
              - ขึ้นต้นด้วยคำตอบเลย กระชับ ไม่ต้องเกริ่นยาว
              """
            : """
              - For simple questions, greetings, or chit-chat (e.g. "what model are you?", "hi"), answer directly — do NOT call a tool when it adds nothing.
              - Basic facts (model, working directory, branch) are already in the Environment section below — answer from there without any tool call.
              - Only read files or run commands when the task genuinely needs project context or an actual change/check.
              - Lead with the answer. Keep it concise; skip long preambles.
              """);

        // ═══════════════════════════════════════════
        // Section 3b: Code intelligence & memory (use what makes CluadeX unique)
        // ═══════════════════════════════════════════
        sb.AppendLine();
        sb.AppendLine(isThai ? "# ปัญญาโค้ด & ความจำ" : "# Code Intelligence & Memory");
        sb.AppendLine(isThai
            ? """
              - มี CODEBASE MAP (ด้านล่าง) สรุป type/function ของทั้งโปรเจค — ใช้นำทางก่อน แล้วค่อย read_file ไฟล์ที่เกี่ยวข้อง แทนการเดาหรือ grep มั่ว
              - จะหาว่า "X อยู่ตรงไหน / ทำงานยังไง" ใช้ codebase_search (จัดอันดับความเกี่ยวข้อง ฉลาดกว่า grep ดิบ) แล้ว read_file ผลลัพธ์อันดับต้น
              - หานิยามของชื่อ (class/method/function) ใช้ find_symbol "ชื่อ" (go-to-definition ไม่ต้องใช้ coord); ดูโครงไฟล์ (class/method พร้อมเลขบรรทัด) ใช้ list_symbols — แม่นกว่าและถูกกว่าการ read ทั้งไฟล์
              - งานที่ไม่ trivial: เรียก brain_recall ก่อนลงมือ เพื่อดูบทเรียน/การตัดสินใจ/บั๊กที่เคยเจอจาก BrainX (อย่าแก้บั๊กเดิมซ้ำรอย)
              - หลังแก้โค้ด: ยืนยันก่อนบอกว่าเสร็จ — เรียก run_build (auto-detect คำสั่ง build/type-check) และ run_tests (สรุป pass/fail + เทสต์ที่ fail) และ/หรือ lsp_diagnostics กับไฟล์ที่แก้ อ่าน error แล้วแก้ จน build ผ่านและเทสต์เขียว
              """
            : """
              - A CODEBASE MAP (below) lists the project's types/functions. Use it to navigate, then read_file the relevant files — don't guess or blind-grep.
              - To locate "where is X handled?", use codebase_search (ranked relevance, smarter than raw grep), then read_file the top hits.
              - To find where a NAME (class/method/function) is DEFINED, use find_symbol "name" (go-to-definition, no coords needed). To outline a file (symbols + line numbers) use list_symbols — both beat reading the whole file.
              - For non-trivial tasks, call brain_recall BEFORE starting, to surface past lessons / decisions / bugs from BrainX (don't re-solve a bug you already solved).
              - After editing code, VERIFY before claiming done: call run_build (auto-detects the build/type-check command), run_tests (distilled pass/fail + failing tests), and/or lsp_diagnostics on the changed file; read any errors and fix them, then build again until clean and tests pass.
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
              7. อ่านไฟล์ด้วย read_file "ก่อน" แก้เสมอ (ระบบจะบล็อกการแก้ไฟล์ที่ยังไม่ได้อ่าน หรือไฟล์ที่เปลี่ยนบนดิสก์หลังอ่าน) แล้วใช้ edit_file find/replace ที่แม่นยำแทนการเขียนใหม่ทั้งไฟล์ — แก้หลายจุดใช้ multi_edit (atomic: พลาดจุดเดียวไม่เขียนทั้งไฟล์); ไฟล์ใหญ่ใช้ read_file offset/limit
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
              7. ALWAYS read_file a file BEFORE you edit/write it (the system blocks edits to a file you haven't read, or one that changed on disk since you read it). Then prefer minimal changes — edit_file with precise find/replace over rewriting whole files. For SEVERAL edits to one file, use multi_edit (atomic — if any hunk fails to match, nothing is written). For large files, read_file with offset+limit.
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
        // Section 10: Available Skills
        // ═══════════════════════════════════════════
        try
        {
            var allSkills = _agentToolService?.GetAvailableSkillNames();
            if (allSkills is { Count: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine(isThai ? "# Skill ที่ใช้ได้" : "# Available Skills");
                sb.AppendLine(isThai
                    ? "คุณสามารถเรียกใช้ skill ผ่านเครื่องมือ `skill_invoke` เมื่อผู้ใช้พิมพ์ /command หรือเมื่อ skill เหมาะกับงาน:"
                    : "You can invoke skills via the `skill_invoke` tool when the user types /command or when a skill fits the task:");
                foreach (var (name, desc) in allSkills)
                    sb.AppendLine($"- `{name}`: {desc}");
                sb.AppendLine();
            }
        }
        catch { /* Skills not available yet */ }

        // Section 11: Output Efficiency
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

    // ─── Claude Code-style Tool Verb Mapping ────────────────────────
    // Maps tool names to human-readable verbs for status display.
    // Pattern from Claude Code: sessionRunner.ts TOOL_VERBS
    private static readonly Dictionary<string, string> ToolVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read_file"] = "Reading",
        ["write_file"] = "Writing",
        ["edit_file"] = "Editing",
        ["list_files"] = "Listing",
        ["search_files"] = "Searching",
        ["search_content"] = "Searching",
        ["glob"] = "Searching",
        ["grep"] = "Searching",
        ["run_command"] = "Running",
        ["powershell"] = "Running PowerShell",
        ["git_status"] = "Checking git status",
        ["git_diff"] = "Diffing",
        ["git_log"] = "Reading git log",
        ["git_commit"] = "Committing",
        ["git_merge"] = "Merging",
        ["git_push"] = "Pushing",
        ["git_pull"] = "Pulling",
        ["git_clone"] = "Cloning",
        ["git_init"] = "Initializing repo",
        ["git_checkout"] = "Switching branch",
        ["git_branch"] = "Branching",
        ["git_add"] = "Staging",
        ["git_stash"] = "Stashing",
        ["git_worktree_create"] = "Creating worktree",
        ["git_worktree_remove"] = "Removing worktree",
        ["gh_pr_create"] = "Creating PR",
        ["gh_pr_list"] = "Listing PRs",
        ["gh_issue_create"] = "Creating issue",
        ["gh_issue_list"] = "Listing issues",
        ["gh_repo_view"] = "Viewing repo",
        ["web_fetch"] = "Fetching",
        ["web_search"] = "Searching web",
        ["notebook_edit"] = "Editing notebook",
        ["memory_save"] = "Saving memory",
        ["memory_list"] = "Listing memories",
        ["brain_recall"] = "Recalling from BrainX",
        ["lsp_diagnostics"] = "Checking diagnostics",
        ["codebase_search"] = "Searching codebase",
        ["instinct_evolve"] = "Evolving instincts",
        ["memory_delete"] = "Deleting memory",
        ["skill_invoke"] = "Invoking skill",
        ["ask_user"] = "Asking user",
        ["create_directory"] = "Creating directory",
        ["repl"] = "Running REPL",
        ["todo_write"] = "Updating tasks",
        ["plan_mode"] = "Planning",
        ["agent_spawn"] = "Spawning agent",
        ["config"] = "Reading config",
        ["hex_info"] = "Inspecting binary",
        ["hex_open"] = "Opening in Hex Editor",
        ["hex_read"] = "Reading bytes",
        ["hex_search"] = "Searching binary",
        ["hex_patch"] = "Patching bytes",
    };

    // Random spinner verbs for the thinking phase (inspired by Claude Code spinnerVerbs.ts)
    private static readonly string[] SpinnerVerbs =
    [
        "Thinking", "Reasoning", "Analyzing", "Processing", "Computing",
        "Evaluating", "Considering", "Formulating", "Crafting", "Brewing",
        "Contemplating", "Architecting", "Calculating", "Synthesizing",
        "Assembling", "Preparing", "Working", "Pondering",
    ];

    private static readonly string[] SpinnerVerbsTh =
    [
        "กำลังคิด", "กำลังวิเคราะห์", "กำลังประมวลผล", "กำลังพิจารณา",
        "กำลังสังเคราะห์", "กำลังเตรียม", "กำลังสร้าง", "กำลังวางแผน",
    ];

    /// <summary>
    /// Builds a human-readable status message for a tool execution.
    /// E.g., "Reading main.cs" or "Running npm install" instead of "Tool: read_file..."
    /// </summary>
    private static string GetToolStatusMessage(string toolName, Dictionary<string, string>? args)
    {
        string verb = ToolVerbs.GetValueOrDefault(toolName, toolName);

        // Extract the most relevant target from arguments
        string? target = null;
        if (args != null)
        {
            target = args.GetValueOrDefault("path")
                  ?? args.GetValueOrDefault("file_path")
                  ?? args.GetValueOrDefault("pattern")
                  ?? args.GetValueOrDefault("url")
                  ?? args.GetValueOrDefault("query")
                  ?? args.GetValueOrDefault("skill")
                  ?? args.GetValueOrDefault("branch")
                  ?? args.GetValueOrDefault("notebook_path");

            // For commands, take first 60 chars
            if (target == null && args.TryGetValue("command", out var cmd))
                target = cmd.Length > 60 ? cmd[..60] + "..." : cmd;
        }

        if (!string.IsNullOrEmpty(target))
        {
            // For file paths, show just the filename for brevity
            if (target.Contains('/') || target.Contains('\\'))
            {
                string fileName = Path.GetFileName(target);
                if (!string.IsNullOrEmpty(fileName))
                    target = fileName;
            }
            return $"{verb} {target}";
        }
        return $"{verb}...";
    }

    public string GetRandomSpinnerVerb()
    {
        bool isThai = _localizationService.CurrentLanguage == "th";
        var verbs = isThai ? SpinnerVerbsTh : SpinnerVerbs;
        return verbs[Random.Shared.Next(verbs.Length)];
    }

    /// <summary>Fires when the agent wants to report status to the UI.</summary>
    public event Action<string>? OnAgentStatus;

    /// <summary>Fires when a tool action completes (for adding to chat UI).</summary>
    public event Action<ToolResult>? OnToolExecuted;

    /// <summary>Fires right before a tool starts executing (for inline status display).</summary>
    public event Action<string, string>? OnToolStarting; // toolName, statusMessage

    /// <summary>Fires when the agent produces thinking/reasoning text (real-time display).</summary>
    public event Action<string, int>? OnThinkingUpdate; // text, stepNumber

    /// <summary>Fires per-token during agentic generation for real-time streaming display.</summary>
    public event Action<string, int>? OnAgenticStreamingToken; // token, stepNumber

    private readonly BrainSyncService? _brainSync;
    private readonly RepoMapService? _repoMap;

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
        MemoryService memoryService,
        HookService? hookService = null,
        CostTrackingService? costTrackingService = null,
        BrainSyncService? brainSync = null,
        RepoMapService? repoMap = null,
        DebugLogService? debugLog = null)
    {
        _debugLog = debugLog;
        _brainSync = brainSync;
        _repoMap = repoMap;
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
        _hookService = hookService;
        _costTrackingService = costTrackingService;
    }

    // Agent-loop tracing: when the user reports "it just doesn't work", the daily log file must be
    // able to answer WHAT the model returned and WHERE the turn died. Startup lines alone can't.
    private readonly DebugLogService? _debugLog;

    /// <summary>Gets system prompt with or without tool definitions based on whether a project is open.</summary>
    public string GetSystemPrompt()
    {
        var sb = new StringBuilder(BuildBaseSystemPrompt());

        // ─── Context-window awareness (fixes the local-model "hang / no response") ───
        // Local providers run with a SMALL context (default 4096 tokens). The full Anthropic-grade context
        // dump — tool definitions + project tree + codebase map + key files — is many thousands of tokens
        // and OVERFLOWS that window. On a local model that means an agonizingly slow prefill or no output at
        // all. So we scale what we inject to the active model's actual context size. (API providers with huge
        // windows are unaffected — they still get the full prompt, identical to before.)
        bool isLocalProvider = _providerManager.ActiveProviderType
            is AiProviderType.Local or AiProviderType.LlamaServer or AiProviderType.Ollama;
        int contextTokens = isLocalProvider
            ? Math.Max(2048, (int)_settingsService.Settings.ContextSize)
            : 1_000_000;
        // The legacy TEXT tool catalogue (~3k tokens) is only for the [ACTION:] loop. When native tool
        // use is on, schemas are sent via the API "tools" field (and rendered into the prompt by the
        // server's chat template) — including the text catalogue too DOUBLE-pays those tokens, which is
        // exactly what blew a ctx-8192 request up to ~13.7k and overflowed the window.
        bool localNativeTools = isLocalProvider && _settingsService.Settings.LocalNativeToolUseEnabled;
        bool includeToolDefs     = !isLocalProvider || (!localNativeTools && contextTokens >= 8000);
        bool includeHeavyContext = !isLocalProvider || contextTokens >= 16000; // codebase map + tree + key files

        // ═══════════════════════════════════════════
        // Dynamic Section: Environment Info
        // ═══════════════════════════════════════════
        sb.AppendLine();
        sb.AppendLine("# Environment");
        sb.AppendLine($"- Platform: {Environment.OSVersion.VersionString}");
        sb.AppendLine($"- Shell: PowerShell / cmd");
        sb.AppendLine($"- Provider: {_providerManager.ActiveProvider?.DisplayName ?? "Unknown"}");
        try
        {
            string activeProviderId = _providerManager.ActiveProvider?.ProviderId ?? "";
            if (_settingsService.Settings.ProviderConfigs.TryGetValue(activeProviderId, out var activeCfg)
                && !string.IsNullOrWhiteSpace(activeCfg.EffectiveModelId))
                sb.AppendLine($"- Model: {activeCfg.EffectiveModelId}");
        }
        catch { /* model id is best-effort */ }
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
                    // Run both git commands in parallel (saves ~1-3s).
                    // Hard timeout (6s) so we never stall system-prompt construction on a hung git.
                    var branchTask = Task.Run(GetGitBranchSync);
                    var statusTask = Task.Run(GetGitStatusSync);
                    Task.WaitAll(new[] { branchTask, statusTask }, 6000);

                    string gitBranch = branchTask.IsCompletedSuccessfully ? branchTask.Result : "";
                    if (!string.IsNullOrEmpty(gitBranch))
                    {
                        sb.AppendLine($"- Git Branch: {gitBranch.Trim()}");
                        string gitStatus = statusTask.IsCompletedSuccessfully ? statusTask.Result : "";
                        if (!string.IsNullOrWhiteSpace(gitStatus))
                        {
                            sb.AppendLine("- Git Status:");
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
            if (includeToolDefs)
            {
                sb.AppendLine(_agentToolService.GetToolDefinitionsPrompt());
            }
            else if (localNativeTools)
            {
                // Native tool use: schemas ride the API "tools" field, so the prompt only needs the
                // discipline line — not a token-expensive text copy of every tool. It MUST describe the
                // set we actually send, though: telling the model tools are "disabled", or naming a core
                // set that omits the MCP tools we handed it, is the contradiction that wrecks weak-model
                // tool selection. Derive the names from the real catalogue rather than a hardcoded list
                // that has to be kept in sync by hand.
                //
                // Deliberately NOT the budget fit: that needs a measured system prompt, which is what we
                // are building right now. Only the token BUDGET is unknown here — which built-ins qualify,
                // and which servers exist, is not — so the NOTE stays truthful without the circularity.
                var catalogue = _agentToolService.BuildNativeToolSchemas();
                var builtInNames = catalogue.Where(t => !IsMcpToolName(t.Name))
                    .Select(t => t.Name)
                    .Where(n => contextTokens >= 16000 || CoreLocalToolNames.Contains(n))
                    .ToList();
                // git_* names reach the set only when the Git feature actually emitted their schemas —
                // advertising them unconditionally offered tools IsToolAllowed denies.
                bool gitInSet = builtInNames.Contains("git_commit", StringComparer.OrdinalIgnoreCase);

                if (contextTokens >= 16000)
                {
                    sb.AppendLine("NOTE: You have the full built-in tool catalogue (provided as structured tool schemas). "
                        + "ALWAYS read_file before editing; after an edit, run_build (and run_tests) to verify before you say you're done.");
                }
                else
                {
                    sb.AppendLine("NOTE: Context is limited, so you have a CORE built-in tool set: "
                        + string.Join(", ", builtInNames)
                        + ". ALWAYS read_file before editing; after an edit, run_build (and run_tests) to verify. "
                        + (gitInSet
                            ? "For git, PREFER the dedicated tools over run_command 'git ...': use git_commit "
                              + "(pass stage_all=true to stage everything first) and git_merge — shell quoting for git is "
                              + "unreliable on Windows. "
                            : "")
                        + "Increase Context Size to ≥ 16384 for the full built-in toolset.");
                }

                // MCP tools are the user's own configured servers. Say they exist, because a weak model
                // that isn't told tends to NARRATE the call instead of making it. No counts: a tight
                // window can drop some, and the tool list itself is the authority on what is callable.
                var mcpServers = catalogue.Where(t => IsMcpToolName(t.Name))
                    .Select(t => McpServerOf(t.Name))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToList();
                if (mcpServers.Count > 0)
                {
                    sb.AppendLine("You ALSO have MCP tools from " + string.Join(", ", mcpServers.Select(s => $"'{s}'"))
                        + ". They are REAL callable tools, named mcp__<server>__<tool> — call them by that full "
                        + "qualified name, and only the ones actually present in your tool list. Never answer that "
                        + "you 'will use' one or ask the user to go ahead: call it now, then answer from its result.");
                }
            }
            else
            {
                // Legacy [ACTION:] path with no tool catalogue fits — run as a plain chat assistant rather
                // than overflow the window (which is what hangs).
                sb.AppendLine("NOTE: This local model's context window is small, so file/tool actions are "
                    + "disabled for now to keep responses fast and reliable. Increase Context Size to ≥ 8192 "
                    + "in Settings (or pick a larger-context model) to enable the full agentic toolset.");
            }
            sb.AppendLine();

            // ─── Few-shot trace (weak local models) ───
            // Showing the read→edit→verify shape once teaches the call FORMAT + discipline far better than
            // prose rules. Local-only + whenever tools are actually available (text catalogue OR native schemas).
            if (isLocalProvider && (includeToolDefs || localNativeTools))
            {
                sb.AppendLine("EXAMPLE of the read→edit→verify discipline (follow this shape every time):");
                sb.AppendLine("  1. read_file(\"src/Calc.cs\") — see the real current code BEFORE changing it.");
                sb.AppendLine("  2. multi_edit(\"src/Calc.cs\", edits=[{find:\"return a - b;\", replace:\"return a + b;\"}]) — atomic find/replace.");
                sb.AppendLine("  3. run_build — if it FAILS, read the error, fix with another edit, build again until green; then run_tests.");
                sb.AppendLine("  4. Only after it's green do you say you're done.");
                sb.AppendLine();
            }

            if (includeHeavyContext)
            {
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

                // Codebase map — a symbol-level outline so the agent knows the whole project's types and
                // APIs without being pointed at files (IDE-grade awareness). Bounded + cached; on Anthropic
                // it rides inside the prompt-cached system prompt, so it's effectively free after request #1.
                try
                {
                    string repoMap = _repoMap?.GetRepoMap(6000) ?? "";
                    if (!string.IsNullOrWhiteSpace(repoMap))
                    {
                        sb.AppendLine();
                        sb.AppendLine("CODEBASE MAP (key declarations per file — read a file for full content):");
                        sb.AppendLine(repoMap);
                    }
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
            }
            else
            {
                // Small local context: skip the multi-thousand-token codebase map + key-file dump that
                // overflows the window (the root cause of the local "hang / no response"). Give just a
                // shallow tree so the model knows the layout, and steer it to pull details on demand.
                try
                {
                    string tree = _fileSystemService.GetProjectTree(2);
                    if (tree.Length > 1200)
                        tree = tree[..1200] + "\n... (truncated)";
                    sb.AppendLine("PROJECT STRUCTURE (top level — use read_file / search tools to go deeper):");
                    sb.AppendLine(tree);
                }
                catch { /* ignore */ }

                // ─── Shrunk repo map at the 8k tier ───
                // The full 6k-char map is reserved for ≥16k, but even ~400-800 tokens of "these are the real
                // files + class names" massively cuts path/symbol hallucination — the #1 weak-model failure.
                // So inject a tiny map from 8k upward instead of all-or-nothing at 16k.
                if (contextTokens >= 8000)
                {
                    try
                    {
                        string smallMap = _repoMap?.GetRepoMap(1500) ?? "";
                        if (!string.IsNullOrWhiteSpace(smallMap))
                        {
                            sb.AppendLine();
                            sb.AppendLine("CODEBASE MAP (key types per file — read a file for full content):");
                            sb.AppendLine(smallMap);
                        }
                    }
                    catch { /* ignore */ }
                }
            }

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

        // ─── Learned habits (instinct read-back) ───
        // Closes the learning loop: instincts extracted across past sessions are finally re-injected so the
        // model benefits from them (before, extraction was write-only and the model never saw them again).
        try
        {
            string? instincts = _agentToolService.GetTopInstinctsBlock(5);
            if (!string.IsNullOrWhiteSpace(instincts))
            {
                sb.AppendLine();
                sb.AppendLine("# Learned habits (apply when relevant)");
                sb.AppendLine(instincts);
            }
        }
        catch { /* instincts optional */ }

        // ─── Hard fit-guard for local models ───
        // Last line of defence: never hand a local model a system prompt that alone blows past its context
        // window — prefilling an over-long prompt is the slow-hang we're killing. Keep the head (identity +
        // tools + env) and drop the tail, leaving room for the conversation and the reply.
        if (isLocalProvider)
        {
            string built = sb.ToString();
            int budgetTokens = (int)(contextTokens * 0.7); // leave ~30% for the question + the answer
            if (_contextMemoryService.EstimateTokens(built) > budgetTokens)
            {
                int charBudget = Math.Max(800, budgetTokens * 4); // ~4 chars/token heuristic
                if (built.Length > charBudget)
                    built = built[..charBudget]
                        + "\n\n[System prompt trimmed to fit this model's context window. Increase Context Size "
                        + "in Settings, or use a larger-context model, for fuller project awareness.]";
                return built;
            }
        }

        return sb.ToString();
    }

    /// <summary>Get current git branch synchronously (for prompt injection). Safe process pattern.</summary>
    private string GetGitBranchSync() => RunGitSync("branch --show-current");

    /// <summary>Get short git status synchronously (for prompt injection). Safe process pattern.</summary>
    private string GetGitStatusSync() => RunGitSync("status --short");

    /// <summary>
    /// Run a short git command safely (no deadlock, no orphan processes).
    /// Uses ReadToEndAsync + WaitForExit with a hard timeout; kills the process tree on timeout.
    /// </summary>
    private string RunGitSync(string arguments, int timeoutMs = 3000)
    {
        System.Diagnostics.Process? proc = null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = _fileSystemService.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = false, // Don't redirect stderr to avoid deadlock
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return "";

            // Read stdout on a worker thread so we never block the process's pipe buffer.
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception killEx)
                { System.Diagnostics.Debug.WriteLine($"[git {arguments}] kill failed: {killEx.Message}"); }
                return "";
            }

            // Process exited; bound the read too in case the buffer never closed.
            return outputTask.Wait(500) ? outputTask.Result.Trim() : "";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // git not installed — expected on machines without git
            return "";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[git {arguments}] failed: {ex.Message}");
            return "";
        }
        finally
        {
            proc?.Dispose();
        }
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

    /// <summary>Detect the project type based on key files. Uses File.Exists (fast, no exceptions).</summary>
    private string DetectProjectType()
    {
        if (!_fileSystemService.HasWorkingDirectory) return "";
        string wd = _fileSystemService.WorkingDirectory;
        var types = new List<string>();

        bool hasFile(string name) => File.Exists(Path.Combine(wd, name));

        // .NET / C#
        if (Directory.EnumerateFiles(wd, "*.csproj", SearchOption.TopDirectoryOnly).Any()
            || Directory.EnumerateFiles(wd, "*.sln", SearchOption.TopDirectoryOnly).Any())
            types.Add("C# / .NET");

        if (hasFile("package.json"))
        {
            types.Add("Node.js");
            if (hasFile("tsconfig.json")) types.Add("TypeScript");
        }
        if (hasFile("pyproject.toml") || hasFile("setup.py") || hasFile("requirements.txt"))
            types.Add("Python");
        if (hasFile("Cargo.toml")) types.Add("Rust");
        if (hasFile("go.mod")) types.Add("Go");
        if (hasFile("pom.xml") || hasFile("build.gradle")) types.Add("Java");
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
        // Build system prompt on background thread (avoids blocking UI with git/file I/O)
        string systemPrompt = await GetSystemPromptAsync();

        await foreach (var token in _providerManager.ActiveProvider.ChatAsync(history, userMessage, systemPrompt, ct))
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
        // Entry trace FIRST — a turn that dies before the loop (brain recall, system prompt build,
        // provider not ready) used to leave the log completely empty, which is indistinguishable
        // from "the user never sent anything".
        var prov = _providerManager.ActiveProvider;
        _debugLog?.Info("Agent", $"turn start · provider={prov?.ProviderId ?? "null"} ready={prov?.IsReady} "
            + $"nativeTools={prov?.SupportsNativeToolUse} msgLen={userMessage?.Length ?? 0} history={history.Count}");

        // ─── Auto-recall: pull relevant lessons from BrainX into this task (best-effort, gated) ───
        userMessage = await MaybePrependBrainContextAsync(userMessage ?? "", progress, ct);

        // ─── Dual-mode dispatch: Native tool_use vs legacy [ACTION:] ───
        if (_providerManager.ActiveProvider.SupportsNativeToolUse)
        {
            return await ExecuteNativeToolLoopAsync(history, userMessage, progress, ct);
        }

        _debugLog?.Warn("Agent", "dispatching to LEGACY [ACTION:] loop (provider has no native tool use) — "
            + "local file-edit reliability is much lower on this path");
        return await ExecuteLegacyToolLoopAsync(history, userMessage, progress, ct);
    }

    /// <summary>
    /// Auto-recall: before an agentic task, search the connected BrainX for relevant coding-lessons /
    /// past decisions and prepend the top hits to the user message (model input only — the persisted
    /// user message is untouched). Best-effort: gated by setting, 3s timeout, silent on any failure so
    /// the task never stalls on the brain.
    /// </summary>
    private async Task<string> MaybePrependBrainContextAsync(string userMessage, IProgress<string>? progress, CancellationToken ct)
    {
        if (_brainSync == null || !_settingsService.Settings.BrainAutoRecallEnabled) return userMessage;
        if (string.IsNullOrWhiteSpace(userMessage) || !_brainSync.IsBrainAvailable) return userMessage;
        // Skip the brain round-trip for very short / conversational asks ("hi", "โมเดลอะไร", "thanks").
        // Auto-recall targets non-trivial coding tasks; on a quick question the extra latency is exactly
        // what makes the app feel sluggish — which is the whole complaint we're fixing here.
        if (userMessage.Trim().Length < 25) return userMessage;

        try
        {
            progress?.Report("Recalling from BrainX...");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // 6s (was 3s): semantic search adds an embedding round-trip; still best-effort and skipped
            // entirely for short conversational prompts, so the worst case stays bounded.
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));

            string query = userMessage.Length > 200 ? userMessage[..200] : userMessage;
            // semantic:true — a raw 200-char task sentence (especially Thai) almost never keyword-matches
            // note titles, so keyword search returned 0 hits and auto-recall was effectively dead. The
            // brain's semantic search embeds the query and finds topical neighbors; it falls back to
            // keyword search server-side when embeddings are unavailable.
            string lessons = await _brainSync.SearchAsync(query, limit: 3, semantic: true, timeoutCts.Token);

            // SearchAsync returns "(...)" sentinels for not-connected / error / empty — skip those.
            if (string.IsNullOrWhiteSpace(lessons) || lessons.StartsWith("(", StringComparison.Ordinal))
                return userMessage;

            return
                "<brainx_recall>\n" +
                "Relevant prior knowledge from your BrainX knowledge base (past decisions, bug fixes, lessons). " +
                "Consult it if helpful; ignore if not relevant to this task:\n\n" +
                lessons +
                "\n</brainx_recall>\n\n" +
                userMessage;
        }
        catch
        {
            return userMessage; // never block a task on the brain
        }
    }

    /// <summary>Legacy agentic loop using [ACTION:] text parsing (for non-Anthropic providers).</summary>
    private async Task<AgentLoopResult> ExecuteLegacyToolLoopAsync(
        List<ChatMessage> history,
        string userMessage,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var result = new AgentLoopResult();
        string systemPrompt = await GetSystemPromptAsync();

        // ─── Smart context enrichment (respects SmartEditing toggle) ───
        string enrichedMessage = userMessage;
        if (_fileSystemService.HasWorkingDirectory && _settingsService.Settings.Features.SmartEditing)
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
        int unknownToolFeedbackCount = 0;          // legacy-path "unknown tool — did you mean" nudges (bounded)
        const int MaxUnknownToolFeedbacks = 3;

        for (int iteration = 0; iteration < MaxAgentIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            result.TurnCount = iteration + 1;

            var step = new AgentStep { StepNumber = iteration + 1 };

            // ─── Generate response with retry ───
            bool isThai = _localizationService.CurrentLanguage == "th";
            string thinkVerb = GetRandomSpinnerVerb();
            string stepStatus = iteration == 0
                ? $"{thinkVerb}..."
                : (isThai ? $"ขั้นที่ {iteration + 1}/{MaxAgentIterations} · {thinkVerb}..." : $"Step {iteration + 1}/{MaxAgentIterations} · {thinkVerb}...");
            progress?.Report(stepStatus);
            OnAgentStatus?.Invoke(stepStatus);

            string response;
            try
            {
                response = await GenerateWithRetryAsync(
                    workingHistory, currentMessage, systemPrompt, ct, iteration + 1);
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
                // ─── Unknown-tool feedback (weak models, legacy [ACTION:] path) ───
                // The model may have written an [ACTION:foo] for a tool that doesn't exist; ParseToolCalls
                // drops those silently. Give it ONE bounded corrective nudge so it can retry with a real
                // tool name instead of us treating the malformed attempt as a finished answer.
                if (unknownToolFeedbackCount < MaxUnknownToolFeedbacks && iteration < MaxAgentIterations - 1)
                {
                    var unknownNames = _agentToolService.GetUnknownActionNames(response);
                    if (unknownNames.Count > 0)
                    {
                        unknownToolFeedbackCount++;
                        var known = _agentToolService.GetRegisteredToolNames().ToList();
                        string fb = "These tool names are not registered: " + string.Join(", ", unknownNames.Select(u =>
                        {
                            var s = CluadeX.Helpers.ToolCallSalvage.SuggestClosestName(u, known);
                            return s != null ? $"'{u}' (did you mean '{s}'?)" : $"'{u}'";
                        })) + ". Re-issue your action using one of the exact tool names you were given.";

                        step.ThinkingText = _agentToolService.StripToolCalls(response);
                        OnThinkingUpdate?.Invoke(step.ThinkingText ?? "", iteration + 1);
                        result.Steps.Add(step);
                        workingHistory.Add(new ChatMessage { Role = MessageRole.Assistant, Content = response });
                        workingHistory.Add(new ChatMessage { Role = MessageRole.System, Content = fb });
                        currentMessage = fb;
                        continue; // let the model retry with a valid tool name
                    }
                }

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

            string toolsStatus = toolCalls.Count == 1
                ? GetToolStatusMessage(toolCalls[0].ToolName, toolCalls[0].Arguments)
                : $"Running {toolCalls.Count} tools in parallel...";
            progress?.Report(toolsStatus);
            OnAgentStatus?.Invoke(toolsStatus);

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
            // Fire OnToolStarting for each concurrent tool
            foreach (var call in concurrent)
            {
                string callStatus = GetToolStatusMessage(call.ToolName, call.Arguments);
                OnToolStarting?.Invoke(call.ToolName, callStatus);
            }
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
            string callStatus = GetToolStatusMessage(call.ToolName, call.Arguments);
            progress?.Report(callStatus);
            OnAgentStatus?.Invoke(callStatus);
            OnToolStarting?.Invoke(call.ToolName, callStatus);

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
    /// <summary>
    /// File names the model's answer talks about that really exist in the project. Used to catch
    /// "here is the updated Program.cs: ```…```" when Program.cs was never written. Only names that
    /// resolve to an actual file are returned, so prose mentioning a made-up path is ignored.
    /// </summary>
    internal static HashSet<string> ExtractMentionedProjectFiles(string text, string workingDir)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(workingDir) || !System.IO.Directory.Exists(workingDir)) return found;

        // Only bother when the answer actually contains code — a plain sentence naming a file is
        // discussion, not a claim to have edited it.
        if (!text.Contains("```")) return found;

        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     text, @"[\w\-.]+\.(cs|csx|ts|tsx|js|jsx|py|java|kt|go|rb|php|cpp|c|h|hpp|xaml|json|md|html|css|sql)\b"))
        {
            string name = m.Value;
            try
            {
                if (System.IO.Directory.EnumerateFiles(workingDir, name, System.IO.SearchOption.AllDirectories).Any())
                    found.Add(name);
            }
            catch { /* unreadable tree — skip */ }
        }
        return found;
    }

    /// <summary>
    /// Squeeze a build log down to the lines that actually name the problem (error/warning lines),
    /// so a guard message stays small enough for an 8k local context.
    /// </summary>
    internal static string CondenseBuildErrors(string raw, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var keep = lines
            .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("): warning", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct()
            .Take(6)
            .ToList();
        // Nothing matched (a test runner, say) — fall back to the tail, which is where failures land.
        string text = keep.Count > 0
            ? string.Join("\n", keep)
            : string.Join("\n", lines.Reverse().Take(6).Reverse());
        if (text.Length > maxChars) text = text[..maxChars] + " …";
        return text;
    }

    /// Force-compact the working history for reactive compaction on 413.
    /// Keeps the last 10 messages (or fewer if history is small) and prepends a summary note.
    /// Technique from Claude Code: keep enough context for the agent to understand
    /// the current task while reducing total token count.
    /// </summary>
    private List<ChatMessage> ForceCompactHistory(List<ChatMessage> history)
    {
        if (history.Count <= 10) return history;

        // Keep last 10 messages — enough context for agent to continue working
        int keepCount = Math.Min(10, history.Count);
        var kept = history.TakeLast(keepCount).ToList();

        // Build a brief summary of what was compacted
        int removedCount = history.Count - keepCount;
        int removedToolCalls = history.Take(removedCount)
            .Count(m => m.Role == MessageRole.ToolAction);

        kept.Insert(0, new ChatMessage
        {
            Role = MessageRole.System,
            Content = $"[Context was compacted due to length. {removedCount} earlier messages removed " +
                      $"({removedToolCalls} tool results). The conversation continues with the most recent context.]",
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
        string systemPrompt = await GetSystemPromptAsync();
        bool isThai = _localizationService.CurrentLanguage == "th";
        int maxTokenRecoveryCount = 0;
        const int MaxOutputTokenRecoveries = 3;
        bool hasAttemptedReactiveCompact = false;
        int autoVerifyCount = 0;                 // bounded auto-build-after-edit (in-loop reflexion)
        const int MaxAutoVerifies = 6;
        // Write→review→finish discipline (the "เหมือนคุณ" loop): after the model has successfully
        // changed files, it should review the result once and CONCLUDE — not wander (observed live:
        // after a perfect write+edit it kept exploring/run_build-ing a txt project until the budget).
        bool anyWriteSucceeded = false;
        int stepsSinceMutation = 0;              // steps since the last successful file change
        bool postWriteReviewNudged = false;
        bool wrapUpForced = false;
        int failedEditAttempts = 0;              // edits/writes the model TRIED that all failed
        string? lastEditFailPath = null;         // file of the last failed edit — lets recovery name the exact call
        int ctxCompactAttempts = 0;              // context-overflow compactions used this turn (max 3)
        var unresolvedEditPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // files whose edit failed and was never redone
        int unresolvedGuards = 0;                // how many times we've blocked a finish on an unresolved file
        var writtenPathsThisTurn = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // files actually written this turn
        int describedNotDoneGuards = 0;          // blocks on "showed the code but never wrote it"
        bool lastBuildFailed = false;            // the build/tests are currently RED
        string lastBuildError = "";              // …and this is what they said
        string lastBuildPath = "build";          // "build" or "tests" — for the message wording
        int repeatedBuildNoEdit = 0;             // consecutive failing builds with no edit in between
        int redBuildGuards = 0;                  // how many times we've blocked a finish on a red build
        int falseFinishGuards = 0;               // times we've blocked a "done" with zero successful changes (cap 2)
        bool lastEditFailNeededRead = false;     // last edit failed the read-before-edit guard (recoverable)
        string? forceToolNextStep = null;        // constrained-decoding override for the next turn (A3)
        bool loopIsLocal = _providerManager.ActiveProviderType
            is AiProviderType.Local or AiProviderType.LlamaServer or AiProviderType.Ollama;
        // Plan-first: force a tool call on step 0 of a non-trivial LOCAL task so the model acts instead of
        // stalling with prose. Off for API models (they don't need it) and trivial questions.
        bool planFirstPending = loopIsLocal
            && _settingsService.Settings.PlanFirstForceToolEnabled
            && LooksNonTrivialTask(userMessage);

        // ─── Escalation ladder ───
        // When the local model stalls, hand the task (with full accumulated context) to a stronger API model.
        var loopProvider = _providerManager.ActiveProvider;
        int iterationBudget = MaxAgentIterations;
        bool hasEscalated = false;
        const int EscalationBonusIterations = 8;
        Services.Providers.IAiProvider? escalationProvider = loopIsLocal ? ResolveEscalationProvider(loopProvider) : null;
        string? lastErrorSig = null;
        int repeatErrorCount = 0;

        // ─── Smart context enrichment (respects SmartEditing toggle) ───
        if (_fileSystemService.HasWorkingDirectory && _settingsService.Settings.Features.SmartEditing)
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

        // ─── Ctx-aware tool subset (weak local models) ───
        // The full catalogue costs ~6-8k tokens once the server's chat template renders it — it only fits
        // comfortably from 16k ctx upward (at 8192 it blew the request up to ~13.7k and overflowed). Fit it
        // to the window instead: built-ins trim to the core below 16k, MCP tools are budgeted (never
        // name-filtered — a built-in whitelist can't match "mcp__server__tool", so filtering by it stripped
        // every MCP tool the user had configured). The prompt's NOTE is derived from the same fit.
        bool isLocal = _providerManager.ActiveProviderType
            is AiProviderType.Local or AiProviderType.LlamaServer or AiProviderType.Ollama;
        int localCtxTokens = Math.Max(2048, (int)_settingsService.Settings.ContextSize);
        int mcpKept = 0, mcpDropped = 0, reservedTokens = 0;
        if (isLocal)
        {
            // Measure the non-schema footprint instead of assuming a fraction of the window: system
            // prompt + the history that will be sent + real room to answer. The generation share is
            // capped at a sixth of the window so a large MaxTokens can't starve the prompt.
            reservedTokens = CluadeX.Helpers.TokenBudget.EstimateTokens(systemPrompt)
                + CluadeX.Helpers.TokenBudget.EstimateTokens(userMessage)
                + history.TakeLast(20).Sum(m => CluadeX.Helpers.TokenBudget.EstimateTokens(m.Content))
                + Math.Max(512, Math.Min(_settingsService.Settings.MaxTokens, localCtxTokens / 6));
            toolSchemas = FitLocalToolSchemas(toolSchemas, localCtxTokens, reservedTokens, out mcpKept, out mcpDropped);
        }

        _debugLog?.Info("Agent", $"native loop start · provider={loopProvider.ProviderId} ctx={_settingsService.Settings.ContextSize} "
            + $"schemas={toolSchemas.Count}{(isLocal ? $" (fitted · mcp {mcpKept} kept/{mcpDropped} dropped · ~{toolSchemas.Sum(EstimateSchemaTokens)}tok · reserved {reservedTokens}tok)" : " (full)")} "
            + $"sysPrompt~{CluadeX.Helpers.TokenBudget.EstimateTokens(systemPrompt)}tok");
        if (mcpDropped > 0)
            _debugLog?.Warn("Agent", $"{mcpDropped} MCP tool schema(s) did not fit ctx={localCtxTokens} — raise Context Size to offer the full set");

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

        for (int iteration = 0; iteration < iterationBudget; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            result.TurnCount = iteration + 1;

            var step = new AgentStep { StepNumber = iteration + 1 };

            string nativeThinkVerb = GetRandomSpinnerVerb();
            string nativeStepStatus = iteration == 0
                ? $"{nativeThinkVerb}..."
                : (isThai ? $"ขั้นที่ {iteration + 1}/{MaxAgentIterations} · {nativeThinkVerb}..." : $"Step {iteration + 1}/{MaxAgentIterations} · {nativeThinkVerb}...");
            progress?.Report(nativeStepStatus);
            OnAgentStatus?.Invoke(nativeStepStatus);

            // Microcompact — shrink old tool results (keep recent turns verbatim).
            // Runs only when the conversation has accumulated enough turns for it to matter.
            bool willCompact = _settingsService.Settings.MicrocompactEnabled && nativeMessages.Count > 6;

            // PreCompact hook fires once per loop iteration where compaction would run,
            // BEFORE the trimming so a snapshot hook can capture the full history.
            if (willCompact && _hookService != null)
            {
                int approxTokens = nativeMessages.Sum(m =>
                    m.Content.Sum(c => (c.Text?.Length ?? 0) / 4));
                try
                {
                    await _hookService.ExecutePreCompactHooksAsync(
                        new HookSessionContext
                        {
                            MessageCount = nativeMessages.Count,
                            TokenEstimate = approxTokens,
                        }, ct);
                }
                catch { /* best-effort */ }
            }

            var outboundMessages = willCompact
                ? MicrocompactNativeMessages(nativeMessages,
                    _settingsService.Settings.MicrocompactKeepRecentTurns,
                    _settingsService.Settings.MicrocompactMaxOldResultChars)
                : nativeMessages;

            // Call API with native tools. Two fixes for the "feels frozen / no response" problems live here:
            //   1. onTextDelta streams the model's reply to the UI live (agentic mode used to stay silent
            //      until the entire multi-step turn finished, so even a one-line answer looked like a hang).
            //   2. An IDLE timeout wraps the request: the window resets on every streamed chunk, so a
            //      healthy (even slow) generation is never cut off — only a genuinely stalled connection
            //      trips it, surfacing a clean retryable error instead of the HttpClient's 5-minute ceiling.
            int nativeStreamStep = iteration + 1;
            int timeoutSec = _settingsService.Settings.InteractiveRequestTimeoutSeconds;
            // The local native tool path is NON-streaming: onTextDelta never fires, so the idle window
            // can never reset. A long prefill + generation on a big local model routinely exceeds the
            // interactive timeout — the "idle" timer was killing perfectly healthy turns. A local server
            // is compute-bound, not network-flaky, so give it a much larger absolute budget instead.
            if (timeoutSec > 0 && loopIsLocal) timeoutSec = Math.Max(timeoutSec, 600);
            Services.Providers.NativeToolResponse response = null!;
            using (var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (timeoutSec > 0) callCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                // Force a tool call only on step 0 of a non-trivial local task (constrained decoding via A3).
                string? iterToolChoice = (iteration == 0 && planFirstPending && toolSchemas.Count > 0)
                    ? "required" : null;
                // Forced recovery (set by the false-finish guard): a weak model that failed an edit and
                // then reverts to PROSE instead of reading/writing. Force the exact recovery tool so it
                // acts instead of apologizing. Takes priority over plan-first/wrap-up for this one turn.
                if (forceToolNextStep != null && toolSchemas.Any(t => t.Name == forceToolNextStep || forceToolNextStep is "required"))
                {
                    iterToolChoice = forceToolNextStep;
                    forceToolNextStep = null;
                }
                // Anti-wander: the task's files are written and the model has burned several steps
                // without changing anything since — force a FINAL prose answer (tool_choice=none) so
                // the turn concludes instead of exploring until the iteration budget dies.
                else if (wrapUpForced && iterToolChoice == null) iterToolChoice = "none";
                try
                {
                    response = await loopProvider.ChatWithToolsAsync(
                        outboundMessages, systemPrompt, toolSchemas,
                        onTextDelta: token =>
                        {
                            if (timeoutSec > 0) callCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec)); // reset idle window
                            OnAgenticStreamingToken?.Invoke(token, nativeStreamStep);
                        },
                        ct: callCts.Token,
                        toolChoice: iterToolChoice);
                }
                catch (OperationCanceledException) when (callCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // Per-call timeout fired (NOT a user cancel) — surface as retryable so the outer
                    // auto-retry re-issues the request instead of leaving a dead spinner.
                    throw new TimeoutException(
                        $"Request timed out after {timeoutSec}s — the model did not respond (timeout).");
                }
                catch (HttpRequestException ex) when (
                    ex.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge
                    // Anthropic reports context overflow as 400 invalid_request_error "prompt is too long",
                    // not 413 — without this clause the compaction path never fired for Anthropic at all.
                    || (ex.StatusCode == System.Net.HttpStatusCode.BadRequest
                        && (ex.Message.Contains("too long", StringComparison.OrdinalIgnoreCase)
                            || ex.Message.Contains("context", StringComparison.OrdinalIgnoreCase))))
                {
                    // ─── Reactive compaction on 413 (prompt too long) ───
                    if (!hasAttemptedReactiveCompact)
                    {
                        hasAttemptedReactiveCompact = true;
                        OnAgentStatus?.Invoke(isThai ? "Context เต็ม — กำลังบีบอัด..." : "Context overflow — compacting...");
                        int keepCount = Math.Min(10, nativeMessages.Count);
                        nativeMessages = nativeMessages.TakeLast(keepCount).ToList();
                        // TakeLast can sever a tool_use/tool_result pair (assistant tool_use dropped, its
                        // user tool_result kept) — Anthropic rejects the very next request with a 400,
                        // which used to surface as an immediate unrecoverable error right after compacting.
                        StripOrphanedToolBlocks(nativeMessages);
                        nativeMessages.Insert(0, new Services.Providers.NativeMessage
                        {
                            Role = "user",
                            Content = { new Services.Providers.ContentBlock
                            {
                                Type = "text",
                                Text = "[Context was compacted due to length. Earlier conversation history has been summarized.]",
                            }},
                        });
                        nativeMessages = EnsureAlternatingRoles(nativeMessages);
                        continue;
                    }
                    result.StopReason = "error";
                    result.FinalResponse = isThai
                        ? "Context ยาวเกินไปแม้หลังบีบอัดแล้ว กรุณาเริ่ม session ใหม่"
                        : "Context too long even after compaction. Please start a new session.";
                    break;
                }
            }

            // ─── Strip leaked control tokens from local-model tool-turn text ───
            // The streaming chat path sanitizes, but the native tool path surfaces TextContent raw — a weak
            // local model that leaks <|im_end|>/<|eot_id|>/role tokens on a tool turn would show them verbatim.
            if (loopIsLocal && !string.IsNullOrEmpty(response.TextContent))
            {
                var (cleanText, _) = CluadeX.Helpers.ModelOutputSanitizer.SanitizeStreamChunk(response.TextContent);
                response.TextContent = cleanText;
            }

            // Display thinking content
            if (!string.IsNullOrEmpty(response.ThinkingContent))
            {
                step.ThinkingText = response.ThinkingContent;
                OnThinkingUpdate?.Invoke(response.ThinkingContent, iteration + 1);
            }

            step.ResponseText = response.TextContent ?? "";

            _debugLog?.Info("Agent", $"step {iteration + 1}: stop={response.StopReason} toolCalls={response.ToolCalls.Count} "
                + $"textLen={(response.TextContent ?? "").Length} in={response.InputTokens} out={response.OutputTokens}"
                + (response.ToolCalls.Count > 0 ? $" [{string.Join(",", response.ToolCalls.Select(t => t.Name))}]" : ""));

            // ─── Provider-reported error (HTTP failure, unsupported backend) ───
            // Providers signal hard failures with StopReason="error" instead of throwing, so the
            // tool_use/tool_result bookkeeping above stays balanced. Surface it as a failed turn —
            // previously these arrived as "end_turn" and the error text was shown as the model's answer.
            if (response.StopReason == "error")
            {
                // Context overflow arrives as a NON-thrown error on the local path (llama-server 400 →
                // StopReason=error), which bypassed the HttpRequestException compaction catch above —
                // a long turn died at the exact step compaction exists to save. Route it there.
                string errText = response.TextContent ?? "";
                // Compaction is retried, NOT one-shot. A single attempt meant that once anything else
                // in the turn had already compacted, the next overflow — even by 3 tokens — killed the
                // run outright. Each attempt keeps fewer messages than the last.
                if (ctxCompactAttempts < 3
                    && (errText.Contains("Context window too small", StringComparison.OrdinalIgnoreCase)
                        || errText.Contains("context size", StringComparison.OrdinalIgnoreCase)))
                {
                    ctxCompactAttempts++;
                    hasAttemptedReactiveCompact = true;
                    _debugLog?.Warn("Agent", $"context overflow at step {iteration + 1} — compacting and retrying "
                        + $"(attempt {ctxCompactAttempts}/3)");
                    OnAgentStatus?.Invoke(isThai ? "Context เต็ม — กำลังบีบอัด..." : "Context overflow — compacting...");
                    int[] ladder = { 10, 6, 3 };
                    int keepCount = Math.Min(ladder[Math.Min(ctxCompactAttempts - 1, ladder.Length - 1)], nativeMessages.Count);
                    nativeMessages = nativeMessages.TakeLast(keepCount).ToList();
                    StripOrphanedToolBlocks(nativeMessages);
                    nativeMessages.Insert(0, new Services.Providers.NativeMessage
                    {
                        Role = "user",
                        Content = { new Services.Providers.ContentBlock
                        {
                            Type = "text",
                            Text = "[Context was compacted due to length. Earlier conversation history has been summarized. Continue the task.]",
                        }},
                    });
                    nativeMessages = EnsureAlternatingRoles(nativeMessages);
                    continue;
                }

                _debugLog?.Error("Agent", $"provider error at step {iteration + 1}: {errText.Replace("\n", " ")}");
                result.Steps.Add(step);
                result.StopReason = "error";
                result.FinalResponse = string.IsNullOrWhiteSpace(response.TextContent)
                    ? (isThai ? "Provider ตอบกลับผิดพลาด (ไม่มีรายละเอียด)" : "The provider returned an error (no details).")
                    : response.TextContent;
                break;
            }

            // ─── Max output token recovery ───
            if (response.StopReason == "max_tokens" && maxTokenRecoveryCount < MaxOutputTokenRecoveries)
            {
                maxTokenRecoveryCount++;
                OnAgentStatus?.Invoke(isThai
                    ? $"คำตอบถูกตัด — กำลังขอต่อ... ({maxTokenRecoveryCount}/{MaxOutputTokenRecoveries})"
                    : $"Response truncated — requesting continuation... ({maxTokenRecoveryCount}/{MaxOutputTokenRecoveries})");

                // Add the partial response as assistant, then ask to continue. The text block must never
                // be empty: a thinking-only truncation leaves TextContent null, and an assistant message
                // with zero content blocks is rejected by the API (400) — killing the recovery it's part of.
                var partialAssistant = new Services.Providers.NativeMessage { Role = "assistant" };
                partialAssistant.Content.Add(new Services.Providers.ContentBlock
                {
                    Type = "text",
                    Text = !string.IsNullOrEmpty(response.TextContent) ? response.TextContent : "(response truncated before any text was produced)",
                });
                nativeMessages.Add(partialAssistant);
                nativeMessages.Add(new Services.Providers.NativeMessage
                {
                    Role = "user",
                    Content = { new Services.Providers.ContentBlock
                    {
                        Type = "text",
                        Text = "Your response was truncated due to max_tokens. Please continue from where you left off.",
                    }},
                });
                nativeMessages = EnsureAlternatingRoles(nativeMessages);
                result.Steps.Add(step);
                continue;
            }

            // ─── Tool-call repair (weak local models) ───
            // A small model often emits the tool call as JSON/prose in content instead of via the
            // structured tool_calls channel. Salvage it so the loop continues instead of treating the
            // attempt as a final answer. Deterministic; only names that resolve to a real tool are accepted.
            // loopIsLocal gate: salvage exists for weak local models that emit tool JSON as prose. An API
            // model's final answer that merely QUOTES tool-call JSON (examples, docs) must never be
            // hijacked into an actual tool execution.
            if (response.ToolCalls.Count == 0 && loopIsLocal && !hasEscalated && _settingsService.Settings.LocalToolCallRepairEnabled)
            {
                var salvaged = CluadeX.Helpers.ToolCallSalvage.ExtractFromText(
                    response.TextContent, name => _agentToolService.ResolveToolTypePublic(name) != null);
                if (salvaged.Count > 0)
                {
                    int si = 0;
                    foreach (var (name, input) in salvaged)
                        response.ToolCalls.Add(new Services.Providers.NativeToolCall
                        {
                            Id = $"call_salvage_{iteration}_{si++}",
                            Name = name,
                            Input = input,
                        });
                    response.StopReason = "tool_use";
                    response.TextContent = null; // the "text" WAS the tool call — don't echo the raw JSON back
                    OnAgentStatus?.Invoke(isThai ? "กู้คืน tool call จากข้อความ..." : "Recovered tool call from text...");
                    _debugLog?.Info("Agent", $"step {iteration + 1}: salvaged {salvaged.Count} tool call(s) from prose [{string.Join(",", salvaged.Select(s => s.Name))}]");
                }
            }

            // No tool calls = final response
            if (response.ToolCalls.Count == 0)
            {
                // ─── Red-build guard ───
                // The single most important honesty rule: never let the turn end while the build the
                // model itself ran is still failing. Without this the model watched run_build fail four
                // times, changed nothing, and then wrote a confident "I added the method" summary while
                // the project did not compile. A red build outranks the model's opinion that it is done.
                if (redBuildGuards < 3 && lastBuildFailed && iteration < MaxAgentIterations - 1)
                {
                    redBuildGuards++;
                    _debugLog?.Warn("Agent", $"red-build guard at step {iteration + 1}: "
                        + $"{lastBuildPath} still failing, refusing to finish (guard {redBuildGuards}/3)");
                    OnAgentStatus?.Invoke(isThai
                        ? "build ยังไม่ผ่าน — ให้แก้ต่อ..."
                        : "The build is still failing — fixing...");
                    result.Steps.Add(step);

                    var redAssistant = new Services.Providers.NativeMessage { Role = "assistant" };
                    redAssistant.Content.Add(new Services.Providers.ContentBlock
                    {
                        Type = "text",
                        Text = string.IsNullOrWhiteSpace(response.TextContent) ? "(done)" : response.TextContent,
                    });
                    nativeMessages.Add(redAssistant);

                    // Keep this SHORT. On an 8k local context a verbose guard is itself what pushes the
                    // next request over the limit — keep only the compiler lines that name the problem.
                    string errTail = CondenseBuildErrors(lastBuildError, 500);
                    nativeMessages.Add(new Services.Providers.NativeMessage
                    {
                        Role = "user",
                        Content = { new Services.Providers.ContentBlock
                        {
                            Type = "text",
                            Text = $"STOP — the {lastBuildPath} is STILL FAILING. Do not summarise.\n{errTail}\n"
                                 + "Fix it: read_file the file in the error, then write_file with the COMPLETE "
                                 + "corrected content. Do not re-run the build before editing.",
                        } },
                    });
                    // Force a tool call so it cannot answer in prose again.
                    forceToolNextStep = "required";
                    // A red build also cancels any pending wrap-up: finishing is exactly what we're blocking.
                    wrapUpForced = false;
                    stepsSinceMutation = 0;
                    continue;
                }

                // ─── Described-but-not-done guard ───
                // The weak-model failure that no build check can catch: it does step 1, then WRITES OUT
                // steps 2-4 as prose ("Now let's add the call in Program.cs: ```csharp …```") and stops.
                // The build is green, no edit failed — but the task is half done. If the final answer
                // names a project file it never actually wrote this turn, send it back to do it.
                if (describedNotDoneGuards < 2 && iteration < MaxAgentIterations - 1
                    && !string.IsNullOrWhiteSpace(response.TextContent))
                {
                    var named = ExtractMentionedProjectFiles(response.TextContent!, _fileSystemService.WorkingDirectory);
                    named.ExceptWith(writtenPathsThisTurn);
                    if (named.Count > 0)
                    {
                        describedNotDoneGuards++;
                        string todo = named.First();
                        _debugLog?.Warn("Agent", $"described-not-done guard at step {iteration + 1}: "
                            + $"answer describes changes to {todo} but it was never written");
                        OnAgentStatus?.Invoke(isThai
                            ? $"ยังไม่ได้เขียน {todo} จริง — ให้ทำต่อ..."
                            : $"{todo} was described but never written — continuing...");
                        result.Steps.Add(step);

                        var descAssistant = new Services.Providers.NativeMessage { Role = "assistant" };
                        descAssistant.Content.Add(new Services.Providers.ContentBlock
                        { Type = "text", Text = response.TextContent! });
                        nativeMessages.Add(descAssistant);
                        nativeMessages.Add(new Services.Providers.NativeMessage
                        {
                            Role = "user",
                            Content = { new Services.Providers.ContentBlock
                            {
                                Type = "text",
                                Text = $"You DESCRIBED the change to '{todo}' but never wrote it — showing code in a "
                                     + "reply does not modify the file. Do it for real now: "
                                     + $"write_file('{todo}', <the COMPLETE file content including your change>). "
                                     + "One tool call. No explanation.",
                            } },
                        });
                        forceToolNextStep = "required";
                        wrapUpForced = false;
                        stepsSinceMutation = 0;
                        continue;
                    }
                }

                // ─── Unresolved-file guard ───
                // Some file the model tried to change is STILL not changed. A green build does not
                // prove the task is done — a call that was never added simply doesn't compile into
                // anything. Name the exact file and make it finish the job.
                if (unresolvedGuards < 2 && unresolvedEditPaths.Count > 0 && iteration < MaxAgentIterations - 1)
                {
                    unresolvedGuards++;
                    string stuck = unresolvedEditPaths.First();
                    _debugLog?.Warn("Agent", $"unresolved-file guard at step {iteration + 1}: "
                        + $"{unresolvedEditPaths.Count} file(s) still unchanged ({stuck}) — refusing to finish");
                    OnAgentStatus?.Invoke(isThai
                        ? $"ยังไม่ได้แก้ {stuck} — ให้ทำต่อ..."
                        : $"{stuck} was not actually changed — continuing...");
                    result.Steps.Add(step);

                    var unresolvedAssistant = new Services.Providers.NativeMessage { Role = "assistant" };
                    unresolvedAssistant.Content.Add(new Services.Providers.ContentBlock
                    {
                        Type = "text",
                        Text = string.IsNullOrWhiteSpace(response.TextContent) ? "(done)" : response.TextContent,
                    });
                    nativeMessages.Add(unresolvedAssistant);
                    nativeMessages.Add(new Services.Providers.NativeMessage
                    {
                        Role = "user",
                        Content = { new Services.Providers.ContentBlock
                        {
                            Type = "text",
                            Text = $"STOP — '{stuck}' was NOT changed. Your edit to it failed and you never retried, "
                                 + "so do not claim it is done.\nDo this now, in one step: "
                                 + $"write_file('{stuck}', <the COMPLETE file content including your change>). "
                                 + "If you need to see it first, read_file it — but do not summarise until it is written.",
                        } },
                    });
                    forceToolNextStep = "required";
                    wrapUpForced = false;
                    stepsSinceMutation = 0;
                    continue;
                }

                // ─── False-finish guard ───
                // The model is concluding, but it TRIED to change files and every attempt failed while
                // NOTHING was ever successfully written (the observed "edit failed x2 → build/test the
                // unchanged file → 'tests passed!'" fake finish). Don't let it claim success — send it
                // back once with an explicit instruction to actually make the change via write_file.
                // Trigger on the FIRST failed edit with zero successful writes (a weak model that hits the
                // read-before-edit guard or one find-mismatch and then gives up / fake-finishes). >=1, not >=2.
                if (falseFinishGuards < 2 && !anyWriteSucceeded && failedEditAttempts >= 1
                    && iteration < MaxAgentIterations - 1)
                {
                    falseFinishGuards++;
                    // Force the model's hand on the recovery turn: read the file (if that was the blocker),
                    // else force SOME tool so it can't apologize its way out again.
                    forceToolNextStep = lastEditFailNeededRead ? "read_file" : "required";
                    _debugLog?.Warn("Agent", $"false-finish blocked at step {iteration + 1}: {failedEditAttempts} failed edit(s), 0 successful writes, neededRead={lastEditFailNeededRead}, force={forceToolNextStep}");
                    OnAgentStatus?.Invoke(isThai ? "ยังไม่ได้แก้ไฟล์จริง — ให้ทำต่อ..." : "No change was actually written — retrying...");
                    result.Steps.Add(step);
                    var fakeAssistant = new Services.Providers.NativeMessage { Role = "assistant" };
                    fakeAssistant.Content.Add(new Services.Providers.ContentBlock { Type = "text",
                        Text = string.IsNullOrWhiteSpace(response.TextContent) ? "(done)" : response.TextContent });
                    nativeMessages.Add(fakeAssistant);
                    // read-before-edit failure is fully recoverable BY YOU — don't ask the user, just read+edit.
                    // Second block = the model already got a chance (and typically already read the file);
                    // stop suggesting reads entirely and dictate the ONE call that finishes the job.
                    string pathArg = lastEditFailPath != null ? $"'{lastEditFailPath}'" : "path";
                    string recover = falseFinishGuards >= 2
                        ? $"STOP — the file is STILL unchanged and you have already read it. Do NOT read again, do NOT "
                          + $"explain, do NOT ask. Your NEXT message must be exactly ONE tool call: "
                          + $"write_file({pathArg}, <the ENTIRE corrected file content — every line, including your change>)."
                        : lastEditFailNeededRead
                        ? "STOP — you have NOT changed the file yet. Your edit failed because you must read the "
                          + "file FIRST. Do it yourself now — do NOT ask the user. Call read_file(path), then "
                          + "edit_file / multi_edit with the exact lines, then run_build to verify."
                        : $"STOP — you have NOT changed any file yet. Your edit failed, so the file is unchanged. "
                          + $"Do NOT say you are done and do NOT ask the user. Call write_file({pathArg}, <the "
                          + $"COMPLETE corrected file content>) now, then verify.";
                    nativeMessages.Add(new Services.Providers.NativeMessage
                    {
                        Role = "user",
                        Content = { new Services.Providers.ContentBlock { Type = "text", Text = recover } },
                    });
                    nativeMessages = EnsureAlternatingRoles(nativeMessages);
                    continue;
                }

                // ─── Self-correction: validate code blocks in final response ───
                string textContent = response.TextContent ?? "";
                var validationFeedback = ValidateResponseCode(textContent);
                if (validationFeedback != null && iteration < MaxAgentIterations - 1)
                {
                    // Code has issues — ask model to fix
                    if (!string.IsNullOrWhiteSpace(textContent))
                        OnThinkingUpdate?.Invoke(textContent, iteration + 1);
                    result.Steps.Add(step);

                    var codeFixAssistant = new Services.Providers.NativeMessage { Role = "assistant" };
                    codeFixAssistant.Content.Add(new Services.Providers.ContentBlock { Type = "text", Text = textContent });
                    nativeMessages.Add(codeFixAssistant);
                    nativeMessages.Add(new Services.Providers.NativeMessage
                    {
                        Role = "user",
                        Content = { new Services.Providers.ContentBlock { Type = "text", Text = validationFeedback } },
                    });
                    nativeMessages = EnsureAlternatingRoles(nativeMessages);
                    continue; // re-generate
                }

                result.Steps.Add(step);
                result.FinalResponse = textContent;
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
            // Partition into read-only (safe for parallel) and write tools
            var userResultMsg = new Services.Providers.NativeMessage { Role = "user" };
            var toolResults = new List<ToolResult>();

            var readOnlyCalls = response.ToolCalls.Where(tc =>
            {
                var t = _agentToolService.ResolveToolTypePublic(tc.Name) ?? ToolType.RunCommand;
                return new ToolCall { Type = t }.IsConcurrencySafe;
            }).ToList();
            var writeCalls = response.ToolCalls.Except(readOnlyCalls).ToList();

            // Helper to execute a tool call and collect results
            int aggregateChars = 0;
            async Task ExecuteNativeToolCall(Services.Providers.NativeToolCall toolCall)
            {
                // Add tool_use block to assistant message FIRST. From here on, this tool_use MUST get a
                // matching tool_result or the next API request is malformed — Anthropic rejects an
                // assistant tool_use with no corresponding user tool_result. So everything below runs
                // under a try that ALWAYS emits a result (even when a tool throws), and a single failing
                // tool can no longer abort the entire turn through Task.WhenAll.
                lock (assistantMsg.Content)
                {
                    assistantMsg.Content.Add(new Services.Providers.ContentBlock
                    {
                        Type = "tool_use",
                        Id = toolCall.Id,
                        Name = toolCall.Name,
                        Input = toolCall.Input,
                    });
                }

                string resultContent;
                bool isError;
                try
                {
                    ct.ThrowIfCancellationRequested();

                    // Unknown tool name → surface a clear error to the model (handled by the catch below).
                    // NEVER silently fall back to RunCommand: a misspelled tool name must not become a shell exec.
                    var resolvedType = _agentToolService.ResolveToolTypePublic(toolCall.Name);
                    if (resolvedType == null)
                    {
                        var suggestion = CluadeX.Helpers.ToolCallSalvage.SuggestClosestName(
                            toolCall.Name, toolSchemas.Select(t => t.Name));
                        string didYouMean = suggestion != null ? $" Did you mean '{suggestion}'?" : "";
                        throw new InvalidOperationException(
                            $"Unknown tool '{toolCall.Name}' — not a registered tool.{didYouMean} Only call tools that were provided to you.");
                    }

                    var call = new ToolCall
                    {
                        ToolName = toolCall.Name,
                        Type = resolvedType.Value,
                        Arguments = ParseJsonInputToArgs(toolCall.Input),
                    };
                    string nativeCallStatus = GetToolStatusMessage(toolCall.Name, call.Arguments);
                    OnAgentStatus?.Invoke(nativeCallStatus);
                    progress?.Report(nativeCallStatus);
                    OnToolStarting?.Invoke(toolCall.Name, nativeCallStatus);

                    var toolResult = await _agentToolService.ExecuteToolAsync(call, ct);

                    // ─── Write validation for write/edit operations ───
                    if (call.Type is ToolType.WriteFile or ToolType.EditFile or ToolType.MultiEdit && toolResult.Success)
                    {
                        var writeValidation = ValidateToolWrite(call);
                        if (writeValidation != null)
                        {
                            toolResult = new ToolResult
                            {
                                ToolName = toolResult.ToolName,
                                Type = toolResult.Type,
                                Success = true,
                                Output = toolResult.Output + $"\n⚠ Validation: {writeValidation}",
                                Summary = toolResult.Summary + " (with warnings)",
                            };
                        }
                    }

                    lock (toolResults) { toolResults.Add(toolResult); }
                    OnToolExecuted?.Invoke(toolResult);
                    if (_debugLog != null)
                    {
                        string errHead = (toolResult.Error ?? "").Replace("\n", " ").Trim();
                        if (errHead.Length > 160) errHead = errHead[..160];
                        _debugLog.Info("Agent", $"tool {toolCall.Name}: {(toolResult.Success ? "OK" : "FAIL · " + errHead)}");
                    }

                    // Per-tool and aggregate budget enforcement
                    resultContent = (toolResult.Success ? toolResult.Output : toolResult.Error) ?? string.Empty;
                    isError = !toolResult.Success;
                    if (resultContent.Length > MaxPerToolOutputChars)
                        resultContent = resultContent[..MaxPerToolOutputChars] + "\n... (truncated)";
                    int currentAggregate = Interlocked.Add(ref aggregateChars, resultContent.Length);
                    if (currentAggregate > MaxAggregateOutputChars)
                        resultContent = resultContent[..Math.Min(resultContent.Length, 500)] + "\n... (aggregate budget exceeded, truncated)";
                }
                catch (OperationCanceledException)
                {
                    throw; // user cancelled — let the whole turn unwind; these local messages are discarded, never sent
                }
                catch (Exception ex)
                {
                    // Degrade a thrown tool into an error result instead of nuking the turn (and breaking
                    // the tool_use/tool_result balance for every OTHER tool in this same batch).
                    resultContent = $"Tool '{toolCall.Name}' failed: {ex.GetType().Name}: {ex.Message}";
                    isError = true;
                    var errResult = new ToolResult { ToolName = toolCall.Name, Success = false, Error = resultContent };
                    lock (toolResults) { toolResults.Add(errResult); }
                    OnToolExecuted?.Invoke(errResult);
                }

                lock (userResultMsg.Content)
                {
                    userResultMsg.Content.Add(new Services.Providers.ContentBlock
                    {
                        Type = "tool_result",
                        ToolUseId = toolCall.Id,
                        Content = resultContent,
                        IsError = isError,
                    });
                }
            }

            // Run read-only tools in parallel
            if (readOnlyCalls.Count > 0)
                await Task.WhenAll(readOnlyCalls.Select(ExecuteNativeToolCall));

            // Run write tools sequentially
            foreach (var toolCall in writeCalls)
                await ExecuteNativeToolCall(toolCall);

            // ─── Auto-verify after edits (in-loop reflexion) ───
            // A weak model edits, claims done, and never runs the build. If this turn changed files (and the
            // model didn't already verify), run the detected build and feed failures back so it's FORCED to
            // confront real compiler errors instead of declaring false victory. Bounded so a slow build can't
            // dominate the task; system-initiated so it doesn't prompt for permission.
            if (_settingsService.Settings.AutoVerifyAfterEditEnabled
                && autoVerifyCount < MaxAutoVerifies
                && !ct.IsCancellationRequested
                && toolResults.Any(r => r.Success && r.Type is ToolType.WriteFile or ToolType.EditFile or ToolType.MultiEdit)
                && !toolResults.Any(r => r.Type is ToolType.RunBuild)
                && _fileSystemService.DetectBuildCommand() != null)
            {
                autoVerifyCount++;
                OnAgentStatus?.Invoke(isThai ? "ตรวจ build อัตโนมัติหลังแก้ไข..." : "Auto-verifying build after edit...");
                try
                {
                    var verify = await _agentToolService.RunVerifyBuildAsync(ct);
                    OnToolExecuted?.Invoke(verify);
                    string note;
                    if (verify.Success)
                    {
                        note = "[auto-verify] ✓ Build passed after your edit.";
                    }
                    else
                    {
                        string raw = ((verify.Output ?? "") + "\n" + (verify.Error ?? "")).Trim();
                        if (raw.Length > 2000) raw = raw[..2000] + "\n... (truncated)";
                        note = "[auto-verify] ✗ Build FAILED after your edit — fix these errors before continuing:\n" + raw;
                    }
                    userResultMsg.Content.Add(new Services.Providers.ContentBlock { Type = "text", Text = note });
                }
                catch (OperationCanceledException) { throw; }
                catch { /* verify is best-effort; never break the turn */ }
            }

            // ─── Write→review→finish bookkeeping ───
            bool mutatedThisStep = toolResults.Any(r => r.Success
                && r.Type is ToolType.WriteFile or ToolType.EditFile or ToolType.MultiEdit);
            if (mutatedThisStep) { anyWriteSucceeded = true; stepsSinceMutation = 0; }
            else if (anyWriteSucceeded) stepsSinceMutation++;
            // Count edit/write attempts that FAILED (find-block mismatch, etc.) — used to catch the
            // "edit failed, then gave up / declared success on the unchanged file" false finish.
            var failedEdits = toolResults.Where(r => !r.Success
                && r.Type is ToolType.WriteFile or ToolType.EditFile or ToolType.MultiEdit).ToList();
            failedEditAttempts += failedEdits.Count;
            if (failedEdits.Count > 0)
            {
                lastEditFailNeededRead =
                    (failedEdits[^1].Error ?? "").Contains("must read", StringComparison.OrdinalIgnoreCase);
                lastEditFailPath = failedEdits[^1].FilePath ?? lastEditFailPath;
            }

            // Per-file edit outcome. A turn that touches two files can succeed on one and fail on the
            // other; the old whole-turn "did anything get written?" flag then reported success and the
            // model confidently claimed BOTH were done. Observed exactly that: StringUtils.cs edited,
            // Program.cs edit failed, build still green (a missing call doesn't break compilation),
            // and the summary claimed both. Track which paths are still unresolved.
            foreach (var f in failedEdits)
                if (!string.IsNullOrEmpty(f.FilePath)) unresolvedEditPaths.Add(f.FilePath!);
            foreach (var w in toolResults.Where(r => r.Success
                         && r.Type is ToolType.WriteFile or ToolType.EditFile or ToolType.MultiEdit))
                if (!string.IsNullOrEmpty(w.FilePath))
                {
                    unresolvedEditPaths.Remove(w.FilePath!);
                    writtenPathsThisTurn.Add(System.IO.Path.GetFileName(w.FilePath!));
                }

            // ─── Build-state bookkeeping ───
            // A red build is the strongest signal we have that the work is NOT done. Track it so the
            // turn cannot end while it is red, and so re-running the build without editing anything
            // gets called out. (Observed: the model watched run_build fail four times in a row, never
            // touched the file, and the wandering breaker then let it finish claiming success.)
            var buildResults = toolResults.Where(r => r.Type is ToolType.RunBuild or ToolType.RunTests).ToList();
            if (buildResults.Count > 0)
            {
                var last = buildResults[^1];
                bool nowFailing = !last.Success;
                if (nowFailing && lastBuildFailed && !mutatedThisStep) repeatedBuildNoEdit++;
                else if (!nowFailing || mutatedThisStep) repeatedBuildNoEdit = 0;
                lastBuildFailed = nowFailing;
                lastBuildError = nowFailing
                    ? ((last.Error ?? "") + "\n" + (last.Output ?? "")).Trim()
                    : "";
                lastBuildPath = last.Type == ToolType.RunBuild ? "build" : "tests";
            }
            else if (mutatedThisStep)
            {
                // The file changed, so whatever the previous build said is now stale.
                lastBuildFailed = false;
                repeatedBuildNoEdit = 0;
            }

            // Re-running a build that can't have changed wastes the whole step budget. Say so.
            if (repeatedBuildNoEdit >= 1 && !mutatedThisStep)
            {
                userResultMsg.Content.Add(new Services.Providers.ContentBlock
                {
                    Type = "text",
                    Text = $"[stop] You ran the {lastBuildPath} again without changing any file, so the result is "
                         + "identical. The build will keep failing until you EDIT the source. Fix the code now: "
                         + "read_file the failing file, then write_file with the COMPLETE corrected content.",
                });
            }

            // After a successful write in a project with NO build system (html/docs/scripts), there is
            // no compiler oracle — teach the same review loop a strong assistant uses: read the file
            // back once, then CONCLUDE. Without this the weak model kept "verifying" a txt project
            // with dotnet build and wandering until the step budget died.
            if (mutatedThisStep && !postWriteReviewNudged
                && _settingsService.Settings.AutoVerifyAfterEditEnabled
                && _fileSystemService.DetectBuildCommand() == null)
            {
                postWriteReviewNudged = true;
                userResultMsg.Content.Add(new Services.Providers.ContentBlock
                {
                    Type = "text",
                    Text = "[post-write review] There is no build system in this project, so do NOT run build "
                         + "commands. Review your work like this: read_file the file you just wrote ONCE to "
                         + "confirm it is correct and complete; then give your FINAL answer summarizing what "
                         + "you created. Do not explore other files.",
                });
            }

            // Wandering breaker: files were written, and several consecutive steps changed nothing —
            // next step forces a final prose answer (see iterToolChoice above).
            // NOT while the build is red: wrapping up a failing build is the exact false finish the
            // red-build guard exists to prevent, and this breaker used to hand it the exit.
            if (anyWriteSucceeded && !wrapUpForced && stepsSinceMutation >= 4 && loopIsLocal
                && !lastBuildFailed)
            {
                wrapUpForced = true;
                _debugLog?.Info("Agent", $"wrap-up forced at step {iteration + 1} (no file changes for {stepsSinceMutation} steps after a successful write)");
                OnAgentStatus?.Invoke(isThai ? "งานหลักเสร็จแล้ว — ให้สรุปปิดงาน..." : "Main work done — wrapping up...");
                userResultMsg.Content.Add(new Services.Providers.ContentBlock
                {
                    Type = "text",
                    Text = "[wrap up] The files are written. Stop exploring — give your final answer NOW: "
                         + "state what you created/changed and where. Keep it short.",
                });
            }

            // ─── Mid-loop goal re-injection (anti-drift for weak small-ctx models) ───
            // After microcompaction trims old turns, a small model loses the thread. Periodically re-state the
            // original request + the read→verify rule so it keeps finishing the task instead of wandering.
            int reminderEvery = Math.Max(2, _settingsService.Settings.MidLoopReminderEvery);
            if (_settingsService.Settings.MidLoopReminderEnabled && loopIsLocal
                && iteration > 0 && (iteration + 1) % reminderEvery == 0)
            {
                // userMessage may have a <brainx_recall> block prepended by auto-recall — skip past it so
                // the reminder quotes the user's actual request, not the first 200 chars of brain JSON.
                string goal = userMessage;
                int recallEnd = goal.IndexOf("</brainx_recall>", StringComparison.Ordinal);
                if (recallEnd >= 0) goal = goal[(recallEnd + "</brainx_recall>".Length)..];
                goal = goal.Replace("\n", " ").Trim();
                if (goal.Length > 200) goal = goal[..200] + "…";
                userResultMsg.Content.Add(new Services.Providers.ContentBlock
                {
                    Type = "text",
                    Text = $"[reminder] The user's request: \"{goal}\". Stay focused on completing it — read a file "
                         + "before editing it, then run_build / run_tests to verify before you say you're done.",
                });
            }

            // ─── Escalation trigger (local model stuck → hand off to a stronger API model) ───
            if (escalationProvider != null && !hasEscalated)
            {
                string errSig = string.Join("|", toolResults
                    .Where(r => !r.Success).Select(r => (r.Error ?? "").Trim()).Where(e => e.Length > 0).Take(3));
                bool stagnating = false;
                if (errSig.Length > 0)
                {
                    if (errSig == lastErrorSig) repeatErrorCount++;
                    else { repeatErrorCount = 0; lastErrorSig = errSig; }
                    stagnating = repeatErrorCount >= 2; // the same error 3 turns running
                }
                else
                {
                    // Clean turn breaks the streak — without this, error→success→same-error counted as
                    // "consecutive" and escalated (paid hand-off) off non-consecutive hiccups.
                    repeatErrorCount = 0;
                    lastErrorSig = "";
                }
                bool nearExhaustion = !result.Success && iteration >= iterationBudget - 1;
                if (stagnating || nearExhaustion)
                {
                    hasEscalated = true;
                    _debugLog?.Warn("Agent", $"escalating to {escalationProvider.ProviderId} · reason={(stagnating ? "repeated error" : "near exhaustion")} · errSig={lastErrorSig[..Math.Min(120, lastErrorSig.Length)]}");
                    loopProvider = escalationProvider;
                    toolSchemas = _agentToolService.BuildNativeToolSchemas(); // full catalogue for the strong model
                    iterationBudget = iteration + 1 + EscalationBonusIterations;
                    string elabel = escalationProvider.DisplayName ?? escalationProvider.ProviderId;
                    OnAgentStatus?.Invoke(isThai
                        ? $"⤴ ส่งต่อให้ {elabel} (โมเดล local ติด)..."
                        : $"⤴ Escalating to {elabel} (local model stuck)...");
                    userResultMsg.Content.Add(new Services.Providers.ContentBlock
                    {
                        Type = "text",
                        Text = "[escalation] A smaller local model was working on this task and got stuck "
                             + (stagnating ? "(it kept repeating the same error). " : "(it ran out of steps). ")
                             + "You are a stronger model — review the conversation and tool results above, then "
                             + "finish the task correctly.",
                    });
                }
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
            _debugLog?.Warn("Agent", $"loop ended NOT successful · stop={result.StopReason} steps={result.Steps.Count}");

        if (!result.Success && string.IsNullOrEmpty(result.StopReason))
        {
            result.StopReason = "max_iterations";
            string lastText = result.Steps.LastOrDefault()?.ResponseText ?? "";
            // Never end the turn with an empty bubble: when the last step produced no text (it was a
            // tool-only step), say plainly that the step budget ran out instead of showing nothing.
            result.FinalResponse = !string.IsNullOrWhiteSpace(lastText)
                ? lastText
                : (isThai
                    ? $"หมดงบ {result.Steps.Count} ขั้นตอนก่อนงานเสร็จ — งานอาจค้างกลางทาง พิมพ์ \"ทำต่อ\" เพื่อให้ทำต่อจากจุดเดิม"
                    : $"Ran out of steps ({result.Steps.Count}) before finishing — the task may be incomplete. Say \"continue\" to pick up where it left off.");
        }

        // Stop hook — runs after the agentic loop returns, with session telemetry.
        if (_hookService != null)
        {
            try
            {
                decimal totalCost = _costTrackingService?.TotalCostUsd ?? 0;
                int totalTokens = (_costTrackingService?.TotalInputTokens ?? 0)
                                + (_costTrackingService?.TotalOutputTokens ?? 0);
                await _hookService.ExecuteStopHooksAsync(
                    new HookSessionContext
                    {
                        Model = _providerManager.ActiveProvider?.GetType().Name ?? "unknown",
                        SessionCostUsd = (double)totalCost,
                        SessionTokens = totalTokens,
                        TurnCount = result.TurnCount,
                    }, ct);
            }
            catch { /* best-effort */ }
        }

        progress?.Report("Done");
        OnAgentStatus?.Invoke("Ready");
        return result;
    }

    /// <summary>Remove tool_result blocks whose matching assistant tool_use was dropped (and vice versa)
    /// after a hard truncation like TakeLast — an unpaired block makes the next API request invalid.
    /// Messages left with no content are removed entirely.</summary>
    private static void StripOrphanedToolBlocks(List<Services.Providers.NativeMessage> messages)
    {
        var toolUseIds = new HashSet<string>(
            messages.Where(m => m.Role == "assistant")
                    .SelectMany(m => m.Content)
                    .Where(b => b.Type == "tool_use" && !string.IsNullOrEmpty(b.Id))
                    .Select(b => b.Id!));
        var toolResultIds = new HashSet<string>(
            messages.Where(m => m.Role == "user")
                    .SelectMany(m => m.Content)
                    .Where(b => b.Type == "tool_result" && !string.IsNullOrEmpty(b.ToolUseId))
                    .Select(b => b.ToolUseId!));

        foreach (var msg in messages)
        {
            msg.Content.RemoveAll(b =>
                (b.Type == "tool_result" && (string.IsNullOrEmpty(b.ToolUseId) || !toolUseIds.Contains(b.ToolUseId)))
                || (b.Type == "tool_use" && (string.IsNullOrEmpty(b.Id) || !toolResultIds.Contains(b.Id))));
        }
        messages.RemoveAll(m => m.Content.Count == 0);
    }

    /// <summary>The configured escalation-target provider, or null when escalation is off / mis-configured /
    /// points back at the current provider / can't run the native tool loop.</summary>
    private Services.Providers.IAiProvider? ResolveEscalationProvider(Services.Providers.IAiProvider current)
    {
        var s = _settingsService.Settings;
        if (!s.EscalationEnabled) return null;
        if (!Enum.TryParse<AiProviderType>(s.EscalationProviderName, ignoreCase: true, out var t)) return null;
        var p = _providerManager.GetProvider(t);
        if (p == null || ReferenceEquals(p, current)) return null;
        if (!p.IsReady) return null; // unconfigured target (no API key / not connected) must not kill a live task
        if (!p.SupportsNativeToolUse) return null; // escalation rides the native tool loop
        return p;
    }

    /// <summary>Heuristic: does the user message describe a non-trivial coding TASK (worth forcing a first
    /// tool call / planning) vs a trivial question? Length or an action verb (EN or TH) ⇒ non-trivial.</summary>
    private static bool LooksNonTrivialTask(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return false;
        string m = userMessage.ToLowerInvariant();
        if (m.Length > 200) return true;
        string[] verbs =
        {
            "implement", "add ", "create", "build", "fix", "refactor", "write ", "change", "update",
            "rename", "move ", "delete", "remove", "replace", "migrate", "wire ", "extract", "generate",
            // git / repo / shell tasks — these need a tool call, but weak models otherwise chat/refuse.
            "clone", "git", "repo", "download", "fetch", "install", "setup", "run ", "npm", "dotnet",
            // analysis tasks that still require reading files / running tools before answering.
            "read", "explain", "summari", "review", "analyz", "explore", "find ", "search", "list ",
            "แก้", "เพิ่ม", "สร้าง", "ทำ", "เขียน", "ย้าย", "ลบ", "ปรับ", "รีแฟกเตอร์",
            "โคลน", "ดาวน์โหลด", "ติดตั้ง", "รัน", "อ่าน", "สรุป", "อธิบาย", "ตรวจ", "ดู", "หา", "ค้นหา", "รีวิว",
        };
        foreach (var v in verbs) if (m.Contains(v)) return true;
        return false;
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

    /// <summary>
    /// Microcompact: shrink the old portion of the conversation before re-sending it
    /// to the API. Keeps the last <paramref name="keepRecentTurns"/> messages verbatim
    /// (the agent needs them intact to continue reasoning). For older messages it:
    ///   • drops image content (base64 blobs in text)
    ///   • truncates large tool_result bodies to <paramref name="maxOldResultChars"/>
    ///   • collapses ISO-8601 timestamps to date-only form
    ///
    /// Returns a shallow-copied list; the caller's list is not mutated so nativeMessages
    /// (which we still append to for the real conversation state) stays faithful.
    /// </summary>
    internal static List<Services.Providers.NativeMessage> MicrocompactNativeMessages(
        List<Services.Providers.NativeMessage> messages,
        int keepRecentTurns,
        int maxOldResultChars)
    {
        if (messages.Count == 0) return messages;
        if (keepRecentTurns < 1) keepRecentTurns = 1;
        if (maxOldResultChars < 100) maxOldResultChars = 100;

        int oldBoundary = Math.Max(0, messages.Count - keepRecentTurns);
        var result = new List<Services.Providers.NativeMessage>(messages.Count);

        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            // Recent messages kept as-is (reference, not clone — we don't mutate them)
            if (i >= oldBoundary)
            {
                result.Add(msg);
                continue;
            }

            var compacted = new Services.Providers.NativeMessage { Role = msg.Role };
            foreach (var block in msg.Content)
            {
                compacted.Content.Add(CompactBlock(block, maxOldResultChars));
            }
            result.Add(compacted);
        }

        return result;
    }

    private static Services.Providers.ContentBlock CompactBlock(
        Services.Providers.ContentBlock block, int maxChars)
    {
        switch (block.Type)
        {
            case "tool_result":
            {
                string content = block.Content ?? "";
                content = StripInlineImages(content);
                content = CompressTimestamps(content);
                if (content.Length > maxChars)
                    content = content[..maxChars] + $"\n... (microcompact: trimmed {content.Length - maxChars} chars)";
                return new Services.Providers.ContentBlock
                {
                    Type = "tool_result",
                    ToolUseId = block.ToolUseId,
                    Content = content,
                    IsError = block.IsError,
                };
            }
            case "text":
            {
                string text = block.Text ?? "";
                text = StripInlineImages(text);
                text = CompressTimestamps(text);
                // Don't truncate text blocks — they're typically short and semantic.
                // Only images/timestamps are stripped.
                return new Services.Providers.ContentBlock { Type = "text", Text = text };
            }
            default:
                // tool_use, thinking — keep unchanged (they drive semantics)
                return block;
        }
    }

    /// <summary>Remove base64 image blobs. The assistant can't see images from prior turns
    /// anyway once we re-send the transcript; the placeholder preserves a breadcrumb.</summary>
    private static string StripInlineImages(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        // Common patterns: data:image/...;base64,AAA...  or  <img src="data:image/...
        return System.Text.RegularExpressions.Regex.Replace(
            s,
            @"data:image/[a-zA-Z]+;base64,[A-Za-z0-9+/=]{40,}",
            "[image stripped]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>Collapse full ISO-8601 timestamps to date-only form in old tool results.
    /// Models rarely need the microsecond precision once several turns have passed.</summary>
    private static string CompressTimestamps(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        // 2026-04-17T15:42:33.123Z   →   2026-04-17
        // 2026-04-17T15:42:33+07:00 →   2026-04-17
        return System.Text.RegularExpressions.Regex.Replace(
            s,
            @"(\d{4}-\d{2}-\d{2})T\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+-]\d{2}:?\d{2})?",
            "$1");
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
        List<ChatMessage> history, string message, string systemPrompt, CancellationToken ct,
        int stepNumber = 0)
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

                // Use unique step ID per retry so UI creates fresh bubble (avoids appending to partial content)
                int effectiveStep = stepNumber * 100 + retry;

                // Idle timeout: the window resets on every streamed token (below), so a healthy stream is
                // never cut off — only a stalled one becomes a retryable error instead of hanging on the
                // shared HttpClient's 5-minute ceiling.
                int timeoutSec = _settingsService.Settings.InteractiveRequestTimeoutSeconds;
                using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (timeoutSec > 0) callCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

                // Use streaming (ChatAsync) to fire tokens in real-time to UI
                var sb = new System.Text.StringBuilder();
                try
                {
                    await foreach (var token in _providerManager.ActiveProvider.ChatAsync(
                        history, message, systemPrompt, callCts.Token))
                    {
                        if (timeoutSec > 0) callCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec)); // reset idle window
                        sb.Append(token);
                        OnAgenticStreamingToken?.Invoke(token, effectiveStep);
                    }
                }
                catch (OperationCanceledException) when (callCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // Timeout, not a user cancel — convert to a transient error so the retry loop re-issues.
                    throw new TimeoutException($"Request timed out after {timeoutSec}s (timeout).");
                }
                return sb.ToString();
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

        string systemPrompt = await GetSystemPromptAsync();
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
        string systemPrompt = await GetSystemPromptAsync();

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
