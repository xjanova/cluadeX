# CluadeX — Remaining Work (Session Handoff)

## Status: 68% feature parity with Claude Code CLI

## CRITICAL — Must Fix Now

### 1. Web Search Tool
- Claude Code has `WebSearchTool` (Perplexity API integration)
- CluadeX only has `web_fetch` (fetch single URL)
- Need: Add `web_search` tool using free search API or scraping
- File: `Services/AgentToolService.cs` — add ToolType.WebSearch + handler
- File: `Models/AgentTool.cs` — add enum value

### 2. Token Estimation (BROKEN)
- `Services/ContextMemoryService.cs` uses `text.Length / 3.5` heuristic — VERY inaccurate
- Should use tiktoken or llama.cpp tokenizer
- LED context bar shows wrong numbers because of this
- Fix: Use LLamaSharp's built-in tokenizer when model is loaded

### 3. REPL Tool (Missing)
- Claude Code has interactive Python/Node.js REPL with persistent state
- CluadeX: NOT IMPLEMENTED
- Need: Add `ToolType.Repl` with Process-based REPL session management
- Keep stdin/stdout open between calls

### 4. Chat Session Loading (STILL BROKEN)
- Users report: clicking old chat sessions does nothing
- Root cause likely: Messages added to `Messages` ObservableCollection but
  `CurrentSession.Messages` may be out of sync with DB
- Need deep debugging with actual running app

### 5. HuggingFace Search — No Pagination in Search Results
- Curated models have pagination (added)
- But search results from HuggingFace API don't paginate
- Need: Add SearchPage/SearchTotalPages to ModelManagerViewModel
- SearchResults ObservableCollection needs paging

## HIGH — Advanced Features

### 6. Agent Sub-task Spawning
- Claude Code: `AgentTool` spawns autonomous sub-agents
- CluadeX: Only single agent loop, no sub-agents
- Need: Allow agent to spawn child agents for parallel work

### 7. MCP Server Integration
- Claude Code: Full MCP client (stdio, SSE, HTTP, WebSocket)
- CluadeX: NOT IMPLEMENTED
- Need: MCP client service + tool registration from MCP servers
- This is a BIG feature — may need its own development cycle

### 8. Task Scheduling (Background Jobs)
- Claude Code: TaskCreate/Get/List/Update/Stop/Output tools
- CluadeX: `TaskManagerService` exists but NOT connected to agent tools
- Need: Wire TaskManagerService into AgentToolService as tools

### 9. Worktree Isolation
- Claude Code: EnterWorktree/ExitWorktree for isolated git work
- CluadeX: NOT IMPLEMENTED
- Need: `git worktree add/remove` integration

### 10. LSP Integration
- Claude Code: LSPTool for language server features
- CluadeX: NOT IMPLEMENTED
- Need: LSP client for code intelligence (completions, diagnostics)

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
- Search results still need pagination
- Need: List/Grid view toggle

## Architecture Notes

### Current Tool Count: 28 (in AgentToolService.cs)
- File: 8 (read, write, edit, list, search_files, search_content, run_command, mkdir)
- Git: 12 (status, add, commit, push, pull, branch, checkout, diff, log, clone, init, stash)
- GitHub: 5 (pr_create, pr_list, issue_create, issue_list, repo_view)
- Web: 1 (web_fetch)
- Meta: 2 (todo_write, plan_mode)

### Services (DI registered in App.xaml.cs): 22
SettingsService, GpuDetectionService, HuggingFaceService, LlamaInferenceService,
CodeExecutionService, FileSystemService, GitService, GitHubService, AgentToolService,
ContextMemoryService, SmartEditingService, DatabaseService, ChatPersistenceService,
AiProviderManager, CodeAgentService, PluginService, PermissionService, TaskManagerService,
WebFetchService, BuddyService, LocalizationService, ActivationService,
XmanLicenseService, BugReportService, AutoUpdateService

### Key Files
- Agent brain: `Services/CodeAgentService.cs` (bilingual prompt, retry, review)
- Tool dispatch: `Services/AgentToolService.cs` (1348 lines, all 28 tools)
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
