# CluadeX — Claude Code Instructions

## Branch
Development happens on `main` (3.0.1xx). The old `feat/claude-code-style-status-display` branch was merged and
deleted on 2026-10-18 (a bundle of the removed branches is at `E:\cluadex-backups-branches-20261018.bundle`).
First two commands of every session:
```bash
git branch --show-current
grep -m1 "<Version>" CluadeX/CluadeX.csproj
```

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
| McpServerManager | MCP 2.0 stdio + Streamable HTTP transports (`IMcpTransport`), tool registry |
| CodeIntelligenceService | Search across files, go-to-definition, find-references, rename, autocomplete |
| DiffService | Side-by-side diff (Myers) + merge-conflict parse/resolve |
| DebugAdapterService | DAP client — debugpy / netcoredbg, breakpoints, stack, variables |

## IDE surface (the "replace VS Code for the AI coding workflow" track)
The workbench left panel is a three-mode activity bar: **EXPLORER | SEARCH | DEBUG**.

| Feature | Key | Notes |
|---------|-----|-------|
| Search across files | `Ctrl+Shift+F` | literal/regex, case, whole-word, include/exclude globs |
| Go to definition | `F12` | |
| Find references | `Shift+F12` | |
| Rename symbol | `F2` | preview mandatory, then confirm |
| Autocomplete | `Ctrl+Space` | also auto-opens after 2 chars |
| Side-by-side diff | diff button in Source Control | HEAD vs working tree |
| Breakpoints | click the editor gutter | |
| Live errors + hover | status-bar ⊗/⚠ count → Problems | real language server per project (`Services/Lsp/`) |
| Errors with no install | Problems ▸ Check | parsed from builds/tests/terminal runs (`Services/Problems/`) |
| What the AI changed | `Alt+F5` / `Shift+Alt+F5`, gutter click | vs the file before the AI's last turn (checkpoints); revert one hunk with confirm |
| Editor → chat | right-click selection, ✦ on a Problem, "to chat" on search | into the running AI's queue when one works here |

**Design rule these all follow — no dead buttons.** Nothing here requires an install to be useful:
navigation/search/rename/completion work off a bounded workspace scan plus `RepoMapService`
heuristics, and *upgrade* to real semantic results when a language server happens to be connected
(none is installed on the dev machine — that is exactly why the fallback exists). The debugger is
the one feature that genuinely needs an external adapter, so it **probes** for `debugpy` /
`netcoredbg` and shows the install command instead of offering a Start button that does nothing.
Live errors follow the same rule: `EditorLanguageService` probes for csharp-ls/OmniSharp,
typescript-language-server, pyright/pylsp and `dart language-server`; absent → the Problems panel shows the install
command (Copy / Run in terminal / Check again). Edits go incrementally after a pause; servers stop with the project
and on exit. Proven by `tests/lsptest` (fake stdio server + the real Dart SDK when present; `--offline` skips it).
The language server is optional: `BuildProblemsService` reads the output of every build/test/check the agent or the
editor terminal runs (dotnet, tsc, eslint, dart/flutter, cargo, go, python, pytest…) into the same squiggles. After an
AI run only checks that run none of the project's own code start by themselves (dart analyze, the Python AST pass);
MSBuild, cargo, go and the project's `node_modules\.bin\tsc.cmd` run only on a Check click.

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

## MCP over HTTP + the XDREAMER connection (3.0.189)
- `McpHttpClient` (Streamable HTTP: JSON or SSE answers, Mcp-Session-Id, MCP-Protocol-Version, 404 = session gone,
  401/403 = key refused, 32 MB cap, never retries a POST) is shared by two users: `McpHttpTransport` (an
  mcp_servers.json entry with `url` and no `command`; `${VAR}` in headers from `env`, then Windows' environment) and
  `ConnectorLauncher.RelayHttpAsync` (stdio ↔ HTTP, what `CluadeX.exe --connector xdreamer` runs — BrainX's bridge
  and CluadeX's own list both start that, so the key never leaves the DPAPI store).
- https anywhere, plain http only to loopback (`McpHttpClient.IsAllowedEndpoint`).
- XDREAMER = ai.xman4289.com (`ConnectorCatalog.XDreamerUrl`), keys `axm-…` from /developer, `ConnectorNeed.None`.
- Proven offline by `tests/mcptest` N1–N8 (in-process fake server) and `tests/setuptest` C1, C9b, C9c.

## Connection wizard (Setup page)
- `Services/Setup/ProviderConnections.cs` turns the setup rows' facts into one provider's steps (pure, so the wizard and
  the checklist cannot disagree). `ViewModels/ConnectWizardViewModel.cs` drives it; the overlay and hub cards are in `SetupView.xaml`.
- The chat jumps there only for the owner's own click or Send (`ConnectionRequested`), with the message left in the box.
  `ChooseFromOutsideAsync` stays a plain refusal, and a wizard closed unfinished never traps the next Send (`_signInLooked`).
- The test step never runs for Local / LlamaServer: a test must not load weights into VRAM.
- Art is in `Assets/Wizard/` (made in ChatGPT). Proven by `tests/setuptest/WizardTests.cs` and `tests/chattest/ConnectWizardTests.cs`.

## MCP page is live, and CluadeX follows BrainX (`McpServerManager.Live.cs`)
- The page reads state, not a snapshot: state changes post to the VM, a 5 s timer redraws "connected for / last answer",
  and pings (30 s local, 60 s remote) turn a process that is alive but silent into `unresponsive` — 2 missed in a row. A
  server with a call in flight is never pinged (brainx-mcp reads stdin on one thread, busy is not hung), -32601 counts as
  alive, -32603 does not (a launcher with no worker), and a server that never answered a ping is simply not pinged again.
- BrainX's own launcher does the swap: `~\.brainx\mcp` is the same path for every version, the client's process is a thin
  launcher, and it swaps its worker when idle, on the same pipe, ending with `tools/list_changed`. Its replayed initialize
  answer is swallowed, so **nothing tells CluadeX the new version**: the brain (only the brain) is asked with a second
  `initialize` when disk ≠ reported. Never send that to another server.
- CluadeX steps in when the launcher didn't move within `BrainSwapGrace` (45 s) or the brain stops answering:
  `ReplaceBrainAsync` starts the new process **beside** the old, handshake + tools + `SwapSoak`, then one atomic
  `_transports.TryUpdate`. Old process drains its calls and stops; state never leaves Ready, tools never vanish. A build
  that fails leaves the old serving, says so on the page, and is tried once per build stamp (dll mtime:length).
- Only a `brainx-mcp` program is followed, not any server with "brain" in its name. A brain the user stopped stays stopped.
- Proven by `tests/mcptest` L1–L20 (this exe copied to brainx-mcp.exe, a version is the text of runtime.version) and, on
  demand, `dotnet run -- --real-launcher <copy of ~\.brainx\mcp with the old dll, new one in next\>` (R1–R4,
  `BRAINX_SANDBOX=1` + throwaway vault).

## Auto-comment existing code (phase 5, 2026-11-02)
- Code Editor toolbar ▸ **Auto-comment** (beside ▸ AI comments, which only shapes code the AI *writes*). Scope: every
  function / only changed lines (`git diff -U0 HEAD`) / key points / whole file / selection; comment language; model.
- `Services/AutoComment/`: the model returns only `{line, text}` (forced `submit_comments` tool, or JSON as text);
  `CommentLexer` decides which line starts are plain code (C#, Python, JS/TS/JSX, Dart, PHP, Java, Go, C/C++, XML/XAML);
  `CommentPlanner` inserts whole comment lines and `Verify` proves strip = original + unchanged lexer state;
  `AutoCommentService.Apply` keeps a Time Machine point, writes atomically, re-proves the bytes from disk, else restores.
- From the chat: the `add_code_comments` tool (`AutoCommentToolModule`, a tool module — no edit to `AgentToolService`)
  and the `/comment` skill, which allows only that tool (+ glob / list_files), so the chat model cannot edit code. No
  path = the file open in the Code Editor; a tab with unsaved typing is refused. The permission prompt carries the cost
  estimate and is shown even with permissions off when the run is paid; inside a chat run the restore point is that
  run's step (`Apply` uses the current `AgentRunScope`).
- Proven by `tests/commenttest` (109 checks, mocked model, no network; T* = the chat tool through the real
  `AgentToolService`). Not yet driven by hand in the running app.

## Splash update, Design mode, verified Studio loads (3.0.229, 2026-11-03)
- Splash: an update found at start is downloaded fully, installed and restarted before the main window opens.
  `UpdateProgressComposer` = bytes over a fixed total (delta vs full decided first), `StartupUpdatePolicy` caps the check
  (25 s) and restart prep (6 s); `Program.Main` turns Velopack auto-apply off so the splash owns the install.
  `UpdateAttemptLog` is the circuit breaker. Proven offline by `tests/apitest` (UpdateProgressWire); never a real install.
- Design mode (`Services/Agents/DesignGate.cs`, like WorkflowGate): per-chat switch + owner-words regex, decided once per
  turn (`TakeOwnerTurn`), the result rides on the run. A design turn opens the UI Studio (`DesignViewRequested`).
- While the AI works in a project the Studio is locked (`StudioService.WriteBlocked`, overlay `__cx.setLocked`); it
  catches up on unlock. Page loads go through `PageLoadTracker` (Navigating → Verifying → Loaded/…/Failed, generation-
  guarded, survives a killed browser). Proven by `tests/chattest` DM*/DL*/SW*, `tests/pagestest`, `tests/studiotest`.
- The old UsageWindow is gone: its account/savings views live in the Billing page (`Views/Billing/`).

## Failure-audit fixes (2026-11-06, from 19 days of `~/.cluadex/logs`)
- Loop guard (`Services/Agents/AgentLoopGuard.cs`, was PR #82): same call + same error twice = nudge, four = stop.
- `Providers/CliRefusals.cs`: a CLI refusal that is a plan/rate limit or a policy flag is **not** run again while the CLI
  is still signed in (OpenAI Dev used to retry and fail in 3 s). The CLI's error text is kept in
  `AgentLoopResult.RefusalText` and logged (`ForLog` redacts keys); a safeguard flag gets a "switch model" hint.
- `UiStallWatchdog`: stall lines say `UI busy N ms in K ops` (low busy = CPU starved or a Win32 loop, not our code) and
  name a `DispatcherTimer` by its Tick handler. Models page reads the model folders off the UI thread on a hardware reading.
- File tools accept `%TEMP%\cluadex-agent` (`Helpers/AgentScratch.cs`, pruned after 3 days); the outside-the-project
  refusal names it and `open_project add="true"`. `CheckpointService` follows portable.txt like the other data.
- Proven by `tests/loopguardtest` F1–F9.

## llama.cpp download in the app (2026-11-06)
- `Services/LlamaCppInstaller.cs`: newest llama.cpp Windows build → `<data root>\llama-backend` (`LlamaBackendInfo.ManagedBackendDir`,
  survives app updates). GitHub's "latest" is a source-only tag (v0.9.0); the b#### builds with zips are prereleases, so
  the releases list is read. GPU cc ≥ 7.5 → CUDA 13, older NVIDIA (GTX 10xx) → CUDA 12 + cudart, other GPU → Vulkan,
  none → CPU; a CUDA build whose `--list-devices` shows no CUDA falls back to Vulkan. sha256 from GitHub, size cap,
  zip-slip guard, `--version` must answer, old folder swapped aside (never while its llama-server runs). One at a time.
- `LlamaBackendInfo.FindServerDir` is the one search (custom → downloaded → app\llama-backend → PATH, links followed)
  for the provider, the Setup row and the arch badge. Models page: card + button, badge on installed files, and a
  refused load offers the download (with size) and loads again. Setup ▸ llama-server row downloads in-app (was winget).
- Proven by `tests/archtest` I1–I10 (localhost GitHub, fake zips, nothing started). `dotnet run -- --real-install <dir>`
  does the real thing: 2026-11-06 b12144 CUDA 12, 648 MB, qwen35 known, CUDA0 = GTX 1070 Ti.
- Real load (owner's OK): `dotnet run -- --real-load <gguf> <backend dir>` (ctx 4096). Qwen3.5-4B-Q6_K on that build:
  GPU 999 layers + vision, ready in 5 s (35 s cold), ≈ 3.9 GB VRAM, answers. Its inline `<think>` block now goes to the
  Thinking block (`Helpers/ThinkTags.cs` in `LlamaServerProvider`, stream + tool loop) — it used to land in the reply.

## Grok + the full-screen model picker (3.0.244, 2026-11-06)
- Grok (xAI API) keeps `AiProviderType.XAI`; `Providers/Compatible/GrokModels.cs` owns its models, reasoning_effort per
  model, context and prices. Grok Dev (`GrokDevProvider`) runs the Grok Build CLI on the owner's sign-in, with CluadeX's
  guard hook, chat-tools MCP server and sub-agent gate through a per-turn GROK_HOME (`GrokTurnHome`). Proven offline by
  `tests/apitest` (GrokDevWire), setuptest, billingtest, personatest. **No live xAI call and no signed-in Grok Dev turn yet.**
- The model box is a button that opens `Views/ModelPickerOverlay.xaml`: provider cards with each girl, then that
  provider's models (tools / vision / thinking / context / suits / price / status). Facts are read from the owning
  services (`Services/ModelPicker/ModelFacts.cs`, `PickStatusRules`), never typed twice; a pick goes through
  `SelectedChatModel` / `SelectedLocalModel` / the wizard. Grok Dev is a CLI card (plan price, sign-in looked at on open)
  after xAI's API card. Locked while the chat's run works. Proven by `tests/chattest` PK/PR/PF/PKS.
- Girls (owner 2026-11-06): a GGUF is CluadeX's purple girl, Ollama stays มายด์, Antigravity has her own, Grok = Grok.

## A CLI agent in a chat with no project (2026-11-06)
- A CLI agent (Codex, Gemini Dev, Antigravity, Claude Dev, Grok Dev) gets its sandbox from `DecideWorkerGrant`: no folder,
  or a folder with no write Allow rule and Accept edits off = read-only (it cannot ask before each write). Codex answered
  "read-only" for 13 turns because nothing told it about `open_project`.
- Every CLI wrapper now carries `ChatToolBridge.OpenProjectHint(noProject)`: with nothing open, call open_project (create="true"
  for a new folder) yourself, never answer "cannot".
- A CLI turn that moves the run to another folder (`FolderOpenedDuringTurn`) is followed at once by a turn that starts there
  (`CarryOnInOpenedFolderAsync`, once per turn). Before it, `AgentToolService.AskCliEditGrantAsync` asks once per folder per
  chat (card "cli_edit_folder"; a Deny/Ask write rule or an unsafe root = no). The yes counts as Accept edits for that
  folder in that chat (`DecideWorkerGrant(..., editsApprovedInChat)`). A no adds `agent.cliEditDeclined` and starts nothing.
- Antigravity's guard now lets a read-only chat call CluadeX's chat tools (`AntigravityGuard.ReadOnlyChatTools`: open_project,
  new_chat, close_chat, update_plan, wrap_up); Codex now gets `CliAgentPolicy.PromptLine` (it has no spawn hook).
- Proven by `tests/chattest` COP0–COP4 (`CHATTEST_ONLY=COP`, scripted CLI through the real chat page) and safetytest's
  Antigravity card checks. Not yet tried with a real Codex turn.

## CLI parity — what Claude Dev gets, every CLI gets (2026-11-06, audit = brain note 77faca9ac76d)
- `ChatToolBridge.ChatToolsHint(CliToolNames, splitChats, plan)` names new_chat / close_chat, wrap_up, every
  `EngineeringToolModule` tool and add_code_comments in each CLI's own names (Plain "… (MCP server cluadex)", Claude
  `mcp__cluadex__x`, Grok `cluadex__x` through use_tool); Antigravity also gets update_plan (it has no plan tool).
- `WorkflowGate.SplitGuide(AiProviderType)`: the Ultra guide with spawn_agent (Codex), codebase_investigator (Gemini),
  spawn_subagent (Grok); Antigravity has no sub-agent tool → straight to the workflow. Reaches worker turns with chat tools.
- The CLI daily budget stops every CLI agent (`IsCliAgent`), not only those with `RunsOwnSubAgents` (Antigravity ran past it).
- Brain tools = `ClaudeDevProvider.BrainToolsFor(grant)` everywhere: Gemini's brain is trusted + `includeTools` (yolo used
  to run ssh_run unasked); Codex without its own brain gets the whole server from ~/.claude.json as a `-c` override with
  only those tools approved; Grok gets a `[mcp_servers.brainx-brain]` table + `--allow MCPTool(brainx-brain__<t>)` per tool,
  **not on a run-grant turn** (bypassPermissions would run every brain tool).
- Antigravity's guard: those brain tools pass in any chat; any other server's MCP tool in a worker run → the red prompt
  (`Verdict.AskUser`), cluadex tools still pass (they gate themselves).
- Codex sees the user's pictures: `AttachmentDigest.CliImagePaths` reads the CLI digest's image lines back, Codex gets
  `--image=<path>` (one each — the flag would swallow the stdin `-`; comma paths skipped; image extensions only, ≤ 10).
- Proven by `tests/apitest` CliParityWire (cp-*) and safetytest's Antigravity MCP checks. No live CLI turn yet.

## Code boxes, question cards with steps, every kind of attachment (2026-11-06)
- Code fences are found by `MarkdownHelper.FindCodeFences` (line scanner, not a regex): ``` or ~~~ of any length, any info
  string (c#, c++, `python title=…`, `ts:file`, `{.rust}`), fences indented in a list (dedented), a fence at the end of a
  sentence. An opening line counts only once its newline arrived (streaming). Every block gets the box with Copy.
- The question card renders its text through the same Markdown (code in a question gets Copy). `ask_user` takes
  `questions: [{question, options, multiSelect}]` on the native loop and the CLIs' MCP tool; Claude Code's AskUserQuestion
  with >1 question or multiSelect uses the same card. One card, a step each (`ChatMessage.SetSteps`, `AskStepState`):
  bar "2 / 3", ‹ Back / Next ›, tick buttons, a review, one numbered answer each (`AskStep.AnswersText`). Saved Content =
  every question + answer; a reloaded card is the plain one.
- Attachments: audio/video → `MediaInfo.Describe` (ffprobe if on PATH, else Windows' shell properties; never hex_read).
  HEIC/AVIF/ICO/JXL → PNG through WIC when a codec exists, else a file with a localized reason. A picture dragged out of
  a browser (`ChatViewModel.WebImageSource`: the drag's HTML `<img src>` against SourceURL, or an image link; http(s) and
  data:image only, 20 MB / 20 s cap) is downloaded and attached; a text selection drag stays the text box's.
- Proven by `tests/chattest` CF*/AS*/XM* (`CHATTEST_ONLY=UX`) and safetytest "steps:" (through the real --cx-tools).
  Not yet driven by hand with a real browser drag or a real CLI asking several questions.

## Agent map = a node canvas, each agent has its own window (2026-11-07)
- The chat's agent map card draws `Views/AgentGraph/AgentGraphView` (made only for an agent-map row): the main agent and
  every sub-agent are nodes on a pannable, zoomable canvas. Layout and edges are pure (`Services/Agents/AgentGraphMath.cs`):
  one row up to 5, then a grid; row 1 hangs on S-curves, deeper rows run down lanes in the column gaps and turn into the
  node's side (no edge crosses a node, lanes ordered so no turn crosses a live lane); a dragged node gets a direct curve.
- Wheel zooms toward the cursor (0.2–2.5; at a limit the wheel falls through to the chat), background drag pans (some of
  the graph always stays on screen), node drag moves it, ⤢ fits (≤ 100 %), ↺ puts dragged nodes back. Zoom / pan /
  dragged places live on the row (`ChatMessage.AgentGraph`), not the control, so a recycled row keeps them (not saved).
- A running agent's edge carries one light (one shared `PulseLoop`, a Motion loop → stops off screen / minimised); done =
  calm solid green, failed = dashed red, stopped = dashed muted.
- A click opens `AgentDetailWindow` (one per agent, owned, never modal): task, model, time (ticks while it runs), tool
  count, cost, what it does now / its report, and every step with its result. Steps come from `SubAgentEvent` "tool"
  (with `CallId`) + the new "result" phase (`Result` ≤ 2000 chars, `Ok`) → `AgentNode.Activity` (≤ 300 steps, session only).
  ClaudeDevStream sends results for the CLI's sub-agent tools too.
- The window steers an agent CluadeX runs itself (agent_spawn on the native loop): `Services/Agents/SubAgentControl.cs`
  (made per spawn in `RunRoutedSubAgentAsync`, its token linked to the run's, never the reverse; handed to the node on
  "start"). **Stop** ends that agent only (asks first when it may edit; a second press is a no-op): the held loop's wrapper
  turns that cancel into an ordinary tool result for the main agent and a "stopped" event → node "คุณสั่งหยุด". **Send**
  queues a message (≤ 4000 chars, ≤ 20 waiting) read at its next step as a text block in the last user message; its row
  goes ✓ "read at step N", or ✗ "not read" when the agent ended first (reported BEFORE done/stopped/failed — the map
  closes steps still open at that event). Ended / stopping / a CLI's own agent / a dispatcher's worker: controls off and
  one line says why. The wrapper also ends the node on the run's stop or a crash (`_subAgentLast` is an AsyncLocal set
  inside the loop, so the router's own catch never saw it).
- Proven by `tests/chattest` AG1–AG16 (`CHATTEST_ONLY=AG`, renders with `CHATTEST_RENDERS`; AG12–AG16 run the real held
  loop with a scripted native provider through the real chat page); MO's clock counts follow.

## A written file must parse before "done"; each model's tool track record (2026-11-07)
- `Services/Agents/WrittenFileCheck.cs`: after a step that wrote files (native loop, gated by `AutoVerifyAfterEditEnabled`),
  JSON / XML (DTD refused) are parsed in-process, `.js` goes through `node --check` (parse only; ESM re-checked as `.mjs`,
  JSX / a broken package.json above it are not blamed — only `SyntaxError` counts), an HTML page's inline scripts are
  checked at the page's own line and its local script / stylesheet / image refs must exist, `.py` through `ast.parse`.
  No node / Python = skipped. Files > 2 MB skipped, ≤ 20 files, ≤ 10 issues, 10 s per tool, no shell (ArgumentList).
  The model reads `[file-check] ✗ …` at once; the **broken-file guard** refuses a finish twice (like red-build), then the
  answer ends with CluadeX's own ⚠️ line naming the file. This is what the build-based auto-verify missed for plain web pages.
- `Services/Agents/ToolTrackRecord.cs` (`<data root>\tool-record.json`): every call of an offered tool, per model label
  (`ModelLabelFor`), with a failure kind (`Classify`; the user's refusals are not counted). A tool with ≥ 3 fails in ≥ 5 calls
  at ≥ 30 % gets one advice line in that model's next system prompt (native + [ACTION:] loops, ≤ 3 tools). Counts halve
  at 200 calls; ≤ 60 models.
- Already elsewhere, not redone here: `find_tools` for local models (`feat/local-tool-budget`, unmerged), context rescue +
  honest overflow stop (`feat/local-model-health`, unmerged), escalation on a stuck local model (main, `EscalationEnabled`).
- Proven by `tests/loopguardtest` W* (real node + real Python when installed) / T*, and `tests/chattest` DC1–DC10
  (`CHATTEST_ONLY=DC`, the real agent loop + real write_file with a scripted native provider).

## The plan card that looked hung; the pages off the rail (2026-11-07)
- The chat's plan card (`ChatMessage.PlanHeader` / `PlanElapsed`) used to change only when the agent changed its plan:
  a 70-minute Claude Dev run ticked its plan for 12 minutes, so the card stood at "12m 26s" and, after the run, still
  said "กำลังดำเนินการ (2/6)". Now the run's 500 ms elapsed timer moves every running card's clock
  (`ChatViewModel.TickPlanClocks`), and `EndTurn` → `ClosePlanCard` sets the header to done or "งานจบแล้ว (ติ๊กแผนไว้ 2/6)"
  (`PlanCardHeader`, `chat.plan.ended`) with the run's whole time. A plan change queued before the end cannot bring
  "working" back. Proven by `tests/chattest` PC0–PC3 (`CHATTEST_ONLY=PC`, the real chat page + native loop).
- Skills, Subagents, Plugins, Hook Library, Instincts, Permissions, Features, Security Shield, Hex Editor and Debug Log are
  not on the rail (owner 2026-10-11). Their only button, Settings ▸ More tools, was removed on 2026-10-16, which left
  them on Ctrl+K / Ctrl+number only. Back as one list, `MainViewModel.MoreToolTargets` / `MoreToolPages`, shown twice:
  the rail's ⋯ button (bottom, above Settings; its own radio group, lit while one of those pages is on screen; a left
  click opens its menu) and the Settings ▸ General ▸ More tools card. Proven by `tests/pagestest` MT1–MT6
  (`dotnet run -- --tools`, the shipping MainWindow).

## Important paths / guarantees
- New DI singletons: `SessionMemoryService`, `LocalGgufProvider` now injected into ChatViewModel (optional constructor arg — falls back to direct LLamaSharp if missing).
- `AppSettings` new fields: `MicrocompactEnabled`, `MicrocompactKeepRecentTurns`, `MicrocompactMaxOldResultChars`, `SessionMemoryEnabled`, `ModelCatalogGridView`, `SidebarNavExpanded`.
- `ChatSession.ProjectPath` — DB schema v2, migration is idempotent (`ALTER TABLE ADD COLUMN` wrapped in try/catch for duplicate-column).
- `LocalizedResources.Instance` — singleton bound by `{services:Loc key}` XAML extension. Initialized in `App.OnStartup` after DI build.
- All live-polling (GPU stats) uses DispatcherTimer + Task.Run marshaling. No UI-thread blocking.

## Remaining Work (for next session)
- [ ] The whole IDE surface above is built and unit-proven but **has not been driven by hand in the
      running app** — click through search / F12 / F2 / diff / breakpoints once.
- [ ] The 7B two-file ceiling: a weak local model reliably does single-file+build, but a coordinated
      two-file change is at its limit. Options: escalate-to-API on stall (`EscalationEnabled` has UI)
      or plan → per-file subtask decomposition in the agent loop.
- [x] Anthropic API live: 4 models × chat / tools / thinking / effort pass (`tests/apitest`, 2026-10-09).
      The owner's key is not workspace-scoped — Settings ▸ AI Provider ▸ Workspace ID must stay filled.
- [ ] A full multi-turn agentic session with the Anthropic API in the running app
- [ ] OpenAI (Responses API) and Gemini (function calling + thoughtSignature echo) native tool loops are
      built from the documented wire formats and pass the localhost mock in `tests/apitest`, but have
      never met the live APIs (no keys on the dev machine). If the API rejects the first tool request the
      provider switches itself to the [ACTION:] text protocol — run `tests/apitest` live once a key exists.
- [ ] Re-verify a real local-model coding task on BOTH local backends — llama-server (native tool_use)
      and in-process LLamaSharp ([ACTION:] loop) — since the prompt now follows the loaded backend's
      real tool protocol. Before this fix the LLamaSharp path made zero tool calls. Ask + check VRAM first.

## Build & Run
```bash
cd D:\Code\ClaudeClient   # the repo lives on D: — E:\Code\ClaudeClient is retired, do not build or edit there
dotnet build CluadeX/CluadeX.csproj
# Output: CluadeX\bin\DebugBuild\net8.0-windows\CluadeX.exe
```
**Building while CluadeX is running fails with MSB3027** (the .exe is locked). Either kill it, or
build somewhere else — which is also how to compile-check without disturbing a running instance:
```bash
dotnet build CluadeX/CluadeX.csproj -p:OutDir=/some/scratch/bld/
```

## Proving a feature actually works
The house standard is a console harness that `<Reference>`s the **built `CluadeX.dll`** and asserts
against real inputs — not mocks. The coding-tool contract lives in the repo at `tests/tooltest`
(96 checks: read/edit/write/glob/grep/shell/build/tests, editor Save, IDE rename, and that the tool
descriptions match the behaviour). It drives the real `AgentToolService` through the same name
resolver the native tool loop uses, phrasing every call the way a model does, in a throwaway git repo;
`portable.txt` keeps it off the real `~/.cluadex`. Run: `dotnet build CluadeX/CluadeX.csproj`, then
`cd tests/tooltest && dotnet run` (report: `bin/Debug/net8.0-windows/tool-report.md`).
The provider contract lives in `tests/apitest`. Offline (free, no key): per-model request rules for
Claude / OpenAI / Gemini (thinking, sampling, effort, forced tool_choice), error explanations, the
assistant-turn rebuild that carries thinking blocks, rate-limit header parsing, and the OpenAI + Gemini
wire format against a localhost mock. Live (costs cents, key + Workspace ID read from settings):
Test Connection, chat, tool round trips with/without thinking, edited history, effort levels, model
listing. `dotnet run -- --offline` · `dotnet run -- claude-opus-5-5,claude-haiku-5-5` · and
`dotnet run -- --offline --db-health <copy of codex.db>` proves the empty-chat cleanup refuses to touch
a damaged chat database. Run it whenever a model list or request shape changes.
The license flow (register PC → check-machine → one free `/demo` key → expiry → restore after reinstall,
rate-limit/5xx never wiping a key) runs in the same `--offline` pass against a localhost copy of the
xmanstudio `ProductLicenseController` rules (`tests/apitest/MockLicense.cs`) — never against xman4289.com:
**a PC that calls `/demo` again after its trial gets flagged as trial abuse on the real server.** The client
reads the mock's URL from `CLUADEX_LICENSE_API`, which only Debug builds honour.
Gemini Dev's guard is proven in `tests/safetytest/GeminiCliReal.cs`: the real provider on the real installed CLI with
its hidden `--fake-responses` recording (no account, no network, no model). It needs the normal worktree build, not
`-p:OutDir=` (the test wants a `CluadeX.exe` of the linked DLL's version). To try a newer CLI without installing it,
put a folder with `gemini.cmd` and `node_modules\@google\gemini-cli\bundle` first on PATH — and read the
`(Ready · <path>)` line, because that is the only proof which CLI ran.
Which llama.cpp can load a GGUF is read from the llama.dll files themselves (`LlamaBackendInfo`): LLamaSharp's
`runtimes\win-x64\native\<variant>\llama.dll` (always shipped; 0.26 has no `qwen35`) and the one beside llama-server
(often absent from an installed copy). `LocalBackendRouter.Decide` routes in-process / llama-server, or refuses at once
with a TH/EN message naming the ways out (`ModelArchitectureNotSupportedException`). Proven by `tests/archtest`
(synthetic GGUF + fake dll files, no model, no process, no GPU).
The older IDE harness `navtest` (132 assertions: search, navigation, rename, completion, diff, merge
conflicts, live debugpy) lived in a session scratchpad and its source is gone — **harnesses belong in
`tests/`, never in a scratchpad.** These harnesses have repeatedly caught bugs the compiler could not:
loose subsequence matching in completion, a phantom deleted line when diffing a new file, grep that
only ever searched the first 100 files, write_file adding a BOM to every file it touched.

**The harness links the DLL and does NOT rebuild it — always rebuild CluadeX first**, or you will
test a stale binary and believe a fix that isn't in it.

**Never load a local model for a test without asking the owner first and checking free VRAM**
(`nvidia-smi`): an E2E load of a 7B at ctx 12288 next to ~3.2 GB of desktop VRAM use restarted the
whole PC (2026-09-24). The contract harness needs no model.

Static checkers worth re-running after any XAML change (they catch what compiles but renders blank):
every `{Binding}` path resolves to a real member, every `{StaticResource}` key exists, every
`{services:Loc}` key is defined **with a Thai value** (a missing key renders as the raw key).

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
