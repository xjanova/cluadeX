# CluadeX — Remaining Work (Session Handoff)

## Status: 85% feature parity with Claude Code CLI

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

### 7. MCP Server Integration ❌ NOT DONE
- Claude Code: Full MCP client (stdio, SSE, HTTP, WebSocket)
- CluadeX: NOT IMPLEMENTED
- Need: MCP client service + tool registration from MCP servers
- This is a BIG feature — may need its own development cycle

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
