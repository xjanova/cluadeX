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

## Remaining Work (for next session)
- [ ] Wire `ChatWithToolsAsync` native loop into actual production use (needs real-world testing)
- [ ] Consider Microcompact (tool result optimization — clearing images, compressing timestamps)
- [ ] Consider Session Memory (background extraction into persistent memory files)
- [ ] Run full integration test with Anthropic API
- [ ] Localization: page content labels still hardcoded English (Settings, Features views)
- [ ] Model Catalog: List/Grid view toggle

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
