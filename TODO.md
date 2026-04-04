# CluadeX — Remaining Work (Session Handoff)

## Status: ~97% feature parity with Claude Code CLI (audited + all critical fixes applied)

## Claude Code Parity — COMPLETED (10 Phases)

### Phase 1: System Prompt Upgrade ✅
- Restructured into 10 named sections (Core Identity, Capabilities, Doing Tasks, Actions with Care, Using Tools, Code Rules, Git Workflow, Advanced Reasoning, Tone & Style, Output Efficiency)
- Dynamic environment info injection (OS, shell, model, date, version)
- Git status/branch injection at session start
- Reads .cluadex/CLAUDE.md for project instructions

### Phase 2: Agentic Loop Improvements ✅
- Parallel tool execution (IsConcurrencySafe on ToolCall, Task.WhenAll for read-only tools)
- Max output token recovery (3 retries on truncated responses)
- Reactive compaction on 413 errors (force-compact history)
- Tool result budget enforcement (50K per tool, 200K aggregate)
- Turn counting + StopReason tracking

### Phase 3: New Tools ✅
- GlobSearch (Microsoft.Extensions.FileSystemGlobbing)
- Grep (regex with context lines, output modes)
- AskUser (UI dialog for clarification)
- ConfigTool (read settings from chat)
- NotebookEdit (Jupyter .ipynb cell editing)
- PowerShell (explicit pwsh execution)

### Phase 4: Skills System ✅
- SkillDefinition model with YAML frontmatter parsing
- SkillService with discovery from ~/.cluadex/skills/ and {project}/.cluadex/skills/
- 3 built-in skills: /commit, /review-pr, /simplify
- Slash command interception in ChatViewModel
- SkillInvoke tool type for AI invocation

### Phase 5: Native tool_use API (Anthropic) ✅
- IAiProvider extended with SupportsNativeToolUse + ChatWithToolsAsync
- ToolSchema, NativeMessage, ContentBlock, NativeToolResponse models
- AnthropicProvider.ChatWithToolsAsync — sends tools array, parses tool_use/tool_result/thinking blocks
- All other providers get safe defaults via ApiProviderBase

### Phase 6: Extended Thinking + Prompt Caching ✅
- Extended thinking parameter with budget_tokens (AppSettings.ExtendedThinkingEnabled)
- Prompt caching with cache_control ephemeral on system prompt
- Thinking block streaming (thinking_delta) with <thinking> markers
- Both ChatAsync and ChatWithToolsAsync support thinking + caching

### Phase 7: Cost Tracking ✅
- CostTrackingService with per-model pricing (Claude, GPT-4, Gemini)
- Records input/output tokens, cache read/creation tokens, USD cost
- Wired into AnthropicProvider (message_start + message_delta SSE events)
- CostText property in ChatViewModel updated every 5 seconds
- FormatCost() display: "$X.XXXX (N in / N out / N requests)"

### Phase 8: Memory System ✅
- MemoryService with 4 types: user, feedback, project, reference
- MEMORY.md index file (200-line cap per Claude Code spec)
- Global (~/.cluadex/memory/) and project (.cluadex/memory/) scopes
- SaveMemory, LoadMemory, RemoveMemory, ListAllMemories
- Injected into system prompt via CodeAgentService

### Phase 9: Hooks System ✅
- HookService with PreToolUse / PostToolUse phases
- Config from .cluadex/hooks.json and ~/.cluadex/hooks.json
- Variable substitution ({tool}, {path}, {command})
- Wildcard matcher pattern support
- PreToolUse can block tool execution
- Wired into AgentToolService.ExecuteToolAsync

### Phase 10: Advanced Permissions ✅
- Tool-scoped permission rules (ToolName field on PermissionRule)
- Claude Code-style patterns: "run_command(npm:*)", "read_file(/src/**)"
- ** glob support (matches across path separators)
- CheckPermission now accepts optional toolName parameter

### Wiring & Integration ✅
- CostTrackingService wired into AnthropicProvider (message_start/message_delta SSE events)
- CostTrackingService.FormatCost() displayed in ChatView via CostText property (updates every 5s)
- HookService wired into AgentToolService.ExecuteToolAsync (PreToolUse blocks, PostToolUse best-effort)
- MemoryService wired into CodeAgentService system prompt (MEMORY.md index injected)
- Native tool_use agentic loop (ExecuteNativeToolLoopAsync) fully wired — dual-mode dispatch
- BuildNativeToolSchemas() generates JSON Schema for all 46+ tools
- Extended Thinking + Prompt Caching UI in SettingsView.xaml with save/load
- Memory tools (memory_save, memory_list, memory_delete) added to agent + native schemas
- AiProviderManager passes CostTrackingService to AnthropicProvider

### Architecture Stats
- **Tool Count**: 46+ (File 8, Git 14, GitHub 5, Web 2, REPL 1, Task 4, Worktree 2, Meta 5, New 6, Memory 3)
- **Services**: 28 (DI registered in App.xaml.cs)
- **New Services**: SkillService, CostTrackingService, MemoryService, HookService
- **New Models**: SkillDefinition, ToolSchema, NativeMessage, ContentBlock, NativeToolResponse, NativeToolCall, MemoryEntry, HookDefinition, HookResult, CostSummary, ModelUsage
- **NuGet Added**: Microsoft.Extensions.FileSystemGlobbing

## CRITICAL — Must Fix Now

### 1. Web Search Tool ✅ DONE
- Added `web_search` tool using DuckDuckGo HTML (no API key needed)
- WebFetchService.SearchAsync() parses DuckDuckGo results
- Registered in AgentToolService with full tool definitions

### 2. Token Estimation ✅ FIXED
- Replaced `text.Length / 3.5` with word-based heuristic
- Counts words (~1.3 tokens each), punctuation (1 token each), newlines
- LED context bar now shows accurate estimates

### 3. REPL Tool ✅ DONE
- Added `ToolType.Repl` with persistent Python/Node.js sessions
- ReplSession.cs manages stdin/stdout with sentinel-based output detection
- State persists between calls (variables, imports, etc.)
- Supports: exec, close actions

### 4. Chat Session Loading ✅ FIXED
- Root cause: CurrentSession.Messages out of sync with Messages ObservableCollection
- Fix: AutoSave now syncs `CurrentSession.Messages = Messages.ToList()` before saving
- LoadSession now replaces the Sessions list entry with the full loaded session
- RestoreSessions properly replaces metadata-only entries
- Added Debug.WriteLine for diagnostics

### 5. HuggingFace Search — Pagination ✅ DONE
- Added `_allSearchResults` backing list with `ApplySearchPagination()`
- SearchPage/SearchTotalPages with Next/Prev commands
- 12 items per page (matches curated models pagination)

## HIGH — Advanced Features

### 6. Agent Sub-task Spawning ✅ DONE
- Added `ToolType.AgentSpawn` with `OnAgentSpawnRequested` event
- AgentToolService fires event; CodeAgentService can handle spawning
- Tool format: task + context args

### 7. MCP Server Integration ✅ DONE (Core)
- McpServerManager: stdio JSON-RPC 2.0 transport, server lifecycle management
- McpStdioTransport: subprocess stdio communication with 30s timeout
- McpToolRegistry: qualified name resolution (mcp__server__tool), prompt generation
- Auto-initialization at App startup (non-blocking)
- Dynamic native schema generation for Anthropic tool_use API
- System prompt injection of MCP tool definitions
- Tool dispatch: resolve → call → format result
- Config: `mcp_servers.json` with per-server enable/disable
- Full management UI: McpServersView (add/remove/start/stop/edit/toggle/logs/tools)
- McpServerDisplayItem observable model for UI binding
- Navigation integrated (nav button, DataTemplate, localization en/th)
- NOT yet done: SSE, HTTP, WebSocket transports (only stdio)

### 8. Task Scheduling (Background Jobs) ✅ DONE
- Added `task_create`, `task_list`, `task_stop`, `task_output` tools
- Wired TaskManagerService into AgentToolService
- Agent can now start long-running commands in background

### 9. Worktree Isolation ✅ DONE
- Added `git_worktree_create` and `git_worktree_remove` tools
- Made GitService.RunGitAsync public for worktree commands
- Agent can create isolated branches for parallel work

### 10. LSP Integration ✅ DONE (Basic)
- Created LspClientService with stdio JSON-RPC client
- Supports: initialize, hover, completions, go-to-definition
- Known servers: C# (OmniSharp), Python (pylsp), TypeScript, Rust, Go, Java
- Registered in DI (App.xaml.cs)
- Not yet wired as agent tool — available for future integration

## MEDIUM — Nice to Have

### 11. Notebook Editing (Jupyter)
### 12. Skill/Template System
### 13. Voice/Speech Input
### 14. Team Collaboration
### 15. PowerShell Native Tool (vs generic run_command)

## UI Bugs to Fix

### Language Toggle
- Nav labels change (NavChat→แชท) ✅
- But page content (Settings labels, Features descriptions) still hardcoded English
- Need: All page titles/subtitles should bind to LocalizationService.T()

### Save Feedback
- Added TextBlock next to Save button ✅
- But some users may not see it — consider toast notification

### Model Catalog
- Star ratings added ✅
- Pagination for curated models added ✅
- VRAM info was already there ✅
- Search results pagination added ✅
- Need: List/Grid view toggle

## Other Fixes

### AutoUpdateService
- Fixed version comparison: was using string.Compare (lexicographic), now uses System.Version (semantic)
- Bumped version to 2.1.0

## Architecture Notes

### Current Tool Count: 37 (in AgentToolService.cs)
- File: 8 (read, write, edit, list, search_files, search_content, run_command, mkdir)
- Git: 14 (status, add, commit, push, pull, branch, checkout, diff, log, clone, init, stash, worktree_create, worktree_remove)
- GitHub: 5 (pr_create, pr_list, issue_create, issue_list, repo_view)
- Web: 2 (web_fetch, web_search)
- REPL: 1 (repl — Python/Node.js)
- Tasks: 4 (task_create, task_list, task_stop, task_output)
- Meta: 3 (todo_write, plan_mode, agent_spawn)

### Services (DI registered in App.xaml.cs): 23
SettingsService, GpuDetectionService, HuggingFaceService, LlamaInferenceService,
CodeExecutionService, FileSystemService, GitService, GitHubService, AgentToolService,
ContextMemoryService, SmartEditingService, DatabaseService, ChatPersistenceService,
AiProviderManager, CodeAgentService, PluginService, PermissionService, TaskManagerService,
WebFetchService, BuddyService, LocalizationService, ActivationService,
XmanLicenseService, BugReportService, AutoUpdateService, LspClientService

### Key Files
- Agent brain: `Services/CodeAgentService.cs` (bilingual prompt, retry, review)
- Tool dispatch: `Services/AgentToolService.cs` (~1600 lines, all 37 tools)
- REPL sessions: `Services/ReplSession.cs` (persistent Python/Node.js)
- LSP client: `Services/LspClientService.cs` (stdio JSON-RPC)
- Chat UI: `ViewModels/ChatViewModel.cs` (LED bar, token stats, session mgmt)
- Main nav: `ViewModels/MainViewModel.cs` (nav labels, update bar, buddy)
- Model loading: `Services/LlamaInferenceService.cs` (LLamaSharp 0.26.0)
- Version API: `Services/AutoUpdateService.cs` (xman + GitHub fallback)
- License: `Services/XmanLicenseService.cs` (xman4289.com API)

### External Integrations
- xman4289.com: License API, Bug Reports, Auto-Update, Version Check
- GitHub: PAT token configured, Release sync enabled
- Landing page: https://xman4289.com/cluadex
- Product page: https://xman4289.com/products/cluadex-ai-coding-assistant
