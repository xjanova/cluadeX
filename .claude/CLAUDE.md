# CluadeX — Claude Code Instructions

## Project Overview
CluadeX is a WPF .NET 8 desktop AI coding assistant at ~97% feature parity with Claude Code CLI.
The Claude Code source is at `E:\Code\src\src` (TypeScript/Node.js) — use it as reference for any new features.

## Architecture
- **Framework**: WPF (.NET 8.0), MVVM, DI container (28 services)
- **Entry**: `App.xaml.cs` (DI), `MainWindow.xaml` (navigation)
- **Agent brain**: `Services/CodeAgentService.cs` — dual-mode agentic loop (native tool_use for Anthropic, legacy [ACTION:] for others)
- **Tool dispatch**: `Services/AgentToolService.cs` — 46+ tools, permission checks, hooks
- **Providers**: `Services/Providers/` — Anthropic, OpenAI, Gemini, Ollama, Local GGUF, LlamaServer
- **UI**: `Views/` (7 pages), `ViewModels/` (8 VMs)

## Key Services (28 total)
| Service | Purpose |
|---------|---------|
| CodeAgentService | Agentic loop, system prompt, dual-mode dispatch |
| AgentToolService | 46+ tools, parsing, native schemas, hooks |
| AnthropicProvider | Native tool_use, extended thinking, prompt caching, cost recording |
| SkillService | /commit /review-pr /simplify, YAML frontmatter skills |
| CostTrackingService | Per-model USD cost tracking |
| MemoryService | 4-type persistent memory (user/feedback/project/reference) |
| HookService | PreToolUse/PostToolUse shell hooks |
| PermissionService | Wildcard + tool-scoped permission patterns |
| McpServerManager | MCP 2.0 stdio transport, tool registry |

## What Was Already Done (10 Phases)
1. ✅ System prompt (10 sections, env info, git status, Claude Code standard)
2. ✅ Agentic loop (parallel tools, 413 recovery, max output recovery, result budgets)
3. ✅ New tools (glob, grep, ask_user, config, notebook_edit, powershell, memory_save/list/delete)
4. ✅ Skills system (3 built-in, YAML frontmatter, slash commands)
5. ✅ Native tool_use API (Anthropic structured blocks, dual-mode loop)
6. ✅ Extended thinking + prompt caching (budget_tokens, cache_control, beta headers)
7. ✅ Cost tracking (per-model, USD, cache tokens, wired to AnthropicProvider)
8. ✅ Memory system (4 types, MEMORY.md index, injected into prompt)
9. ✅ Hooks (PreToolUse/PostToolUse, sanitized injection, concurrent pipe reads)
10. ✅ Advanced permissions (wildcards, tool-scoped patterns, ** glob)

## Audit completed — All fixes applied
- grep alias collision → fixed
- Process deadlocks (git, powershell, hooks) → fixed with concurrent reads
- HookService command injection → fixed with SanitizeShellArg
- ThinkingBudgetTokens > MaxTokens → auto-raise max_tokens
- Cost double-counting → isNewRequest parameter
- ToolResult mutation → clone before truncate
- Native loop missing enrichment → added
- Notebook trailing newline → fixed per .ipynb spec
- anthropic-beta headers → added for thinking + caching
- Permission pattern ignored → checks both tool + resource
- Missing native schemas → added git_clone/init/worktree/agent_spawn

## Completed in Session (2026-04-04)
- [x] Implement SkillInvoke tool — now resolves skill by name, returns prompt content + allowed tools
- [x] Add Extended Thinking toggle in ChatView status bar — checkbox with brain icon, persists to settings
- [x] Add Memory management UI in SettingsView — list/preview/delete memories with refresh
- [x] Fix notebook_edit multi-line parsing — `new_source:` now handled like `code:` in ParseNotebookEditArgs
- [x] Wire MCP tools into native schemas — dynamic schema generation from MCP tool registry
- [x] MCP initialization at startup — `McpServerManager.InitializeAsync()` called in App.OnStartup
- [x] Added `skill_invoke` native schema + system prompt tool definition (#47)
- [x] MCP Server Management page — full UI (add/remove/start/stop/edit/toggle/logs/tools)
- [x] McpServerDisplayItem model with observable status/toolCount/logOutput
- [x] McpServerManager.SetConfig/RemoveConfig for UI config management
- [x] Navigation wired: nav button, DataTemplate, localization (en/th)

## Completed in Session (2026-04-17 — Part 1)
- [x] Gemma 4 loading — `--reasoning-format none` + stream parser reads both `content` and `reasoning_content` (LlamaServerProvider.cs). Gemma 4 was emitting tokens into `reasoning_content` which our SSE parser ignored → user saw blank output.
- [x] Native tool loop — verified wired in production via `ExecuteAgenticAsync` → `ExecuteNativeToolLoopAsync` (CodeAgentService.cs:818)
- [x] Microcompact (CodeAgentService.cs:MicrocompactNativeMessages) — strips base64 images, compresses ISO-8601 timestamps, truncates old tool_result bodies. Gated by `MicrocompactEnabled` setting (default on).
- [x] SessionMemoryService.cs — background extraction on session end; opt-in via `SessionMemoryEnabled` setting.
- [x] Localization — `LocExtension` XAML markup extension (`{services:Loc key}`) with live refresh on language change. Settings + Features views localized.
- [x] Model Catalog List/Grid toggle (ModelManagerView.xaml + ModelManagerViewModel.cs) — UniformGrid via DataTrigger, persisted to `ModelCatalogGridView` setting.

## Completed in Session (2026-04-17 — Part 2: Sidebar UX & Model Loading)
- [x] **LocalGgufProvider _activeBackend bug** — set flag BEFORE `await LoadModelAsync` (was after) so status/loading events don't get filtered during load; revert to None on exception.
- [x] **Sidebar project filtering** — `ChatSession.ProjectPath` + DB v2 migration (`ALTER TABLE sessions ADD COLUMN project_path`). `VisibleSessions` ObservableCollection auto-synced with `Sessions.CollectionChanged`. `ShowAllProjectSessions` toggle in sidebar header.
- [x] **Sticky Buddy** — moved Buddy widget OUT of ScrollViewer into a pinned Row 2 of the sidebar Grid. New layout: Logo / Scroll (nav+history) / Buddy / Status.
- [x] **Collapsible MENU** — gradient "MENU" button at top of nav stack; chevron rotates; `IsNavExpanded` persisted to `SidebarNavExpanded` setting; default collapsed so chat history dominates.
- [x] **3D menu styling** — new `NavCheckedFill` / `NavHoverFill` / `NavIconBubble` gradient brushes in Theme.xaml. Selected nav items get drop-shadow, colored icon bubble, accent strip. Hover preview with glow.
- [x] **GPU live monitor in sidebar** — `GpuDetectionService.GetLiveStats()` polled every 2s via DispatcherTimer in MainViewModel. Rolling 24-sample sparkline (Polyline bound to `GpuSparklinePoints` PointCollection) + temperature chip (color gradient: green < 60 < yellow < 75 < orange < 85 < red).
- [x] **Multi-GPU tensor-split** — `GpuDetectionService.DetectAllNvidiaGpus()` enumerates all NVIDIA cards; `LlamaServerProvider` auto-adds `--tensor-split vram1,vram2,...`.
- [x] **Full CPU cores default** — `ThreadCount = 0` now maps to `Environment.ProcessorCount` (previously no `-t` flag, so llama.cpp used half).
- [x] **Load progress parser** — `LlamaServerProvider.ReportLoadProgress` mines stderr for `offloaded N/M layers`, `load_tensors: ... MiB`, `init_from_params`, `server is listening`. Progress percentage surfaces in UI during VRAM upload.
- [x] **Fit indicator badges** — `ModelInfo.RecommendedModel.FitTier` enum (Excellent/Good/Partial/Poor/TooLarge) computed per-card in `ModelManagerViewModel.ComputeFit()`. Colored border + pill label on catalog cards. Full catalog shown (previously hid models larger than VRAM).
- [x] **Gemma catalog expansion** — added 9 new Gemma entries: Gemma 4 9B/14B, CodeGemma 4 9B, Gemma 3N E2B/E4B, Gemma 3 QAT 1B/4B/12B/27B, Gemma 2 2B/9B/27B, CodeGemma 7B. Total 17 Gemma variants.
- [x] **HF search filter** — added `filter=gguf&pipeline_tag=text-generation` + client-side blacklist (embedding, image, audio, speech, LoRA). Over-fetch 40 → display 20 to survive filter drops.
- [x] **"Read more" HF link** — `ModelInfo.HuggingFaceUrl` computed property; each search-result card has a link button to the HF model page.
- [x] **Auto-load routes through LocalGgufProvider** — ChatViewModel's startup auto-load now uses arch-aware router so Gemma 4/Llama 4/Qwen 3 models resume on restart (previously would silently fail with `UnsupportedModelArchitectureException`).
- [x] **README overhaul** — added Sidebar & UX section, Advanced Model Loading section, Session Memory to memory table, GPU live monitor to core features.

## Important paths / guarantees
- New DI singletons: `SessionMemoryService`, `LocalGgufProvider` now injected into ChatViewModel (optional constructor arg — falls back to direct LLamaSharp if missing).
- `AppSettings` new fields: `MicrocompactEnabled`, `MicrocompactKeepRecentTurns`, `MicrocompactMaxOldResultChars`, `SessionMemoryEnabled`, `ModelCatalogGridView`, `SidebarNavExpanded`.
- `ChatSession.ProjectPath` — DB schema v2, migration is idempotent (`ALTER TABLE ADD COLUMN` wrapped in try/catch for duplicate-column).
- `LocalizedResources.Instance` — singleton bound by `{services:Loc key}` XAML extension. Initialized in `App.OnStartup` after DI build.
- All live-polling (GPU stats) uses DispatcherTimer + Task.Run marshaling. No UI-thread blocking.

## Remaining Work (for next session)
- [ ] Run full integration test with Anthropic API (real keys, multi-turn agentic session)
- [ ] Localization: PermissionsView, TasksView, McpServersView still have hardcoded strings
- [ ] Fit indicator's `-ngl` suggestion is not auto-applied when user clicks "Load" — user has to set it manually in Settings. Could add "Apply recommended settings" button per-card.
- [ ] Session Memory extraction UI — currently opt-in via settings only; no visible indicator when it runs/completes.

## Build & Run
```bash
cd E:\Code\ClaudeClient
dotnet build CluadeX/CluadeX.csproj
# Output: CluadeX\bin\Debug\net8.0-windows\CluadeX.exe
```

## File Map (key files)
```
CluadeX/
├── Services/
│   ├── CodeAgentService.cs      # Agentic loop + system prompt (~1400 LOC)
│   ├── AgentToolService.cs      # Tool dispatch + schemas (~2700 LOC)
│   ├── CostTrackingService.cs   # API cost tracking
│   ├── MemoryService.cs         # Persistent memory system
│   ├── HookService.cs           # PreToolUse/PostToolUse hooks
│   ├── SkillService.cs          # Skill discovery + built-in skills
│   ├── PermissionService.cs     # Wildcard permissions
│   └── Providers/
│       ├── IAiProvider.cs       # Interface + native tool models
│       ├── ApiProviderBase.cs   # Base class
│       └── AnthropicProvider.cs # Native tool_use + thinking + caching
├── Models/
│   ├── AgentTool.cs             # ToolType enum (46+), ToolCall, ToolResult
│   ├── AppSettings.cs           # Settings + FeatureToggles
│   ├── SkillDefinition.cs       # Skill model
│   └── PermissionRule.cs        # Permission rule model
├── ViewModels/
│   ├── ChatViewModel.cs         # Chat UI logic, cost display, skills
│   └── SettingsViewModel.cs     # Settings bindings (thinking, caching)
└── Views/
    ├── ChatView.xaml            # Chat UI (cost text, context bar)
    └── SettingsView.xaml        # Settings UI (thinking toggle)
```
