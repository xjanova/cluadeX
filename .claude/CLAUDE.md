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

## Replies read aloud — ElevenLabs, OpenAI, free voices (2026-11-09)
- Settings › Voice (`Views/Settings/VoiceSettingsPanel.xaml` + `VoiceSettingsViewModel`, settings in `Models/VoiceSettings.cs`):
  browser (free, mic window only), Windows (free, offline System.Speech), ElevenLabs (own key, DPAPI like the others),
  OpenAI (reuses the OpenAI provider's key). ElevenLabs controls follow what the chosen model reports (style, speaker boost,
  v3's three stabilities, Thai or not); voice cloning needs the consent tick.
- Each girl reads her own reply in her own mood (`Services/Voice/Tts/VoicePersona.cs`) and can have her own voice per engine.
  Audio plays in the mic window when it is open (`/audio` on ChromeDictationService), else in the app (`VoicePlayer`).
- The app's ComboBox template ignores DisplayMemberPath in the closed box: give records `ToString() => Label`.
- Proven offline by `tests/apitest --voice` (stand-in ElevenLabs/OpenAI server) and `tests/voicetest` (renders the panel
  per engine to PNGs). **No live ElevenLabs/OpenAI call yet** — set ELEVENLABS_API_KEY for apitest's live check.
- **Edge is the default engine** (`Services/Voice/Tts/EdgeTts.cs`): Microsoft Edge read-aloud's free neural voices over a
  WebSocket, Thai Premwadee/Niwat. It is unofficial and has **two hosts**, tried in this order:
  `api.msedgeservices.com` with a `Sec-MS-GEC` **v1** token (edge-tts 7.2.3), then `speech.platform.bing.com` with a **v2**
  token (edge-tts 7.3.1). Both tokens are pinned by vectors in `tests/apitest/EdgeTtsWire.cs`. **Trap (2026-11-10):** the bing
  host drops nearly every request over ~300 Thai characters, and edge-tts 7.3.1 fails the same way. A finished reply is
  longer than that, so with bing alone every reply was silent and the only sign was a status line. Text also goes in pieces of
  ≤3,000 UTF-8 bytes (`EdgeTts.Pieces`). `apitest --edge` speaks a 1,456-character Thai reply three times and checks it
  came from msedgeservices. If voices fail, compare both hosts against the current edge-tts on PyPI first.
- **Every read-aloud decision is logged under `[Voice]`**: what was read (engine, host, size, time), or why nothing was —
  the row the turn ended on, an Edge failure, or audio that did not play. Look there before guessing.
- **Trap: Windows voices read Thai as silence.** System.Speech sees desktop voices only; this PC has en-US only, and an
  English voice given Thai text writes a 46-byte WAV with no error. `WindowsTts` now throws `WindowsVoiceMissingException`
  (no voice for the language, or a silent WAV) and the chat/test fall back to Edge. Windows' own Thai voice (Pattara) is
  OneCore — System.Speech cannot use it even when installed.
- The browser engine is used only while the mic window is open; otherwise the reply goes through Edge.
- The speaker under the girl's face on each reply (`ChatView.xaml`, `ToggleSpeakRepliesCommand`) flips the same switch as
  Settings › Voice; turning it off stops her mid-sentence.
- Settings cards can be narrow: rows are `Views/Settings/FitRow.cs` (label + controls on one line until the label would
  drop under 180 px, then controls under the label); the mascot moves above the settings below 640 px. voicetest checks
  420/520/900 px for squeezed text and controls outside the card.

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

## Codex's Windows sandbox — what CluadeX repairs (2026-11-09)
- Codex gives a folder two write SIDs (`~/.codex/cap_sid`): `workspace_by_cwd` for runs whose cwd it is (shell commands) and
  `writable_root_by_path` for runs that have it as a writable root — the file-edit helper (`codex --codex-run-as-fs-helper`)
  starts at the drive root, so its edits use the second. The setup step grants each SID on the root when it is first used and
  cannot re-stamp anything the sandbox account owns. A folder the sandbox made before the second SID existed keeps only
  the first: edits in it fail ("write file … failed"), shell writes work. XSurLab, 2026-11-09: src/tests/design/docs/scripts.
- `CodexSandboxRepair` rebuilds (copy as the user, swap, delete) a sandbox-owned `.git`, and every sandbox-owned file or
  folder missing one of the root's inherited grants (`StaleEntries`). Before a turn: folders + root files, skipped when the
  root's grants are unchanged since the last walk; after a failed edit/command: every file, 10 s budget. Package caches and
  build output are never walked; a tree over 20,000 files / 1 GB or written while copying is left as it was.
- Proven by `tests/apitest --codex-sandbox` (offline with real ACLs + live `codex sandbox` reproducing the gap and the fix).
  `apitest --repair-sandbox <dir> [--dry]` runs the same repair on a real folder.
- Python edits on Windows: open with `newline=''` (else LF files come back CRLF) and keep the BOM.

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

## GPU share slider (2026-11-06)
- Click the status-bar VRAM graph → vertical 0–100 % slider (`ViewModels/GpuShareViewModel.cs`, popups in
  `MainWindow.xaml` beside `GpuGraph` — not inside it: a click in a Popup bubbles to its parent's MouseBinding).
  Stored in `AppSettings.GpuUsagePercent` (100 = no cap). `Services/Gpu/GpuShare.cs` scales layers from the GGUF's
  `{arch}.block_count` (+1 output layer): llama-server/LLamaSharp `-ngl`, Ollama `num_gpu` (block count from `/api/show`).
  0 % = CPU route. Applies on the next load (Ollama: next request); a loaded GGUF gets a "Reload model now" button, never auto.
- First-use hint (`GpuShareHint`: after 2 min of graph, every 3 h, ≤ 6 times, never once used); the pet says it instead
  when it is out and talks (`PetMoment.GpuShareHint`). Keys in `Localization/GpuShareKeys.cs`.
- Proven by `tests/gpusharetest` (103 checks: math, synthetic GGUF headers, persistence, hint, the real popup XAML
  rendered in all 4 themes to `bin/.../renders`). Not yet driven by hand in the running app.

## Smart skill picking + the token gauge (2026-11-07)
- Every switched-on skill used to cost its line in the system prompt on every turn. Now, per chat (composer switch
  "Skill อัตโนมัติ", default from Skills page ▸ `SkillAutoPickEnabled`, overrides in `skill-pick-chats.json` via
  `Services/Skills/SkillPickGate`), `CodeAgentService.PickSkillsForTurnAsync` picks the skills that fit the turn **before**
  the model starts and appends them to the turn's message (`SkillPick.BlockFor`). The system prompt then carries one fixed
  line, so a different pick each turn never breaks the prompt cache. Off = the old full list (`SkillBudget.ListingLine`).
- `Skills/SkillPicker` (pure): the owner's typed words (`AgentRunScope.TurnText`, set by the chat page, without the folders
  line / notices / attachments), a Thai→English word table, words learned from turns that ran a skill
  (`SkillService.LearnTerms`, ≤ 40 per skill in skill-usage.json), pins ("ใช้ทุกงาน", `PinnedSkills`), the turn's brain
  recall text (weak), and the brain's ranking. Rare words weigh more (idf); ≥ 3.0 and ≥ 35 % of the best match; ≤ `SkillPickMax`
  (5) and ≤ the ceiling; named (/name, `commit` as a word) and pinned always. Small local window: ≤ 2, ≤ 200 tok. A /command
  turn picks nothing (`NoSkillPick`). The chat shows a ✦ row under the message (nothing fitted = no row, the tooltip says so).
- The brain's half (`Skills/SkillBrainIndex`, `SkillBrainIndexEnabled`): with BrainX connected, one card note per skill in
  `Notes/CluadeX-Skills`, tag `cluadex-skill` (name, description, when to use — **never the prompt**; a skill always runs from
  its file here). Written in the background when missing/changed (state `skill-brain-index.json`, found by exact title first
  so a reinstall does not duplicate, a refused card is not retried until restart). Each turn `brain_semantic_search` with
  `tag` runs beside the auto-recall (4 s cap): its first place counts on its own, later ones need a word of the task too.
- Gauge (`Skills/SkillBudget`, Skills page header): per-turn tokens of the offered list vs the ceiling = 1 % of the active
  model's window (250–4000), the ceiling at mid-bar; each row shows ≈ per turn / ≈ when it runs, "หนัก" when its prompt is
  over a tenth of the window. CLI agents use their own skills (noted, not counted).
- Proven by `tests/skilltest` K*/A*/L*/G*/B*/S*/V13–14 (99 checks) and `tests/chattest` SP1–SP7 (`CHATTEST_ONLY=SP`, the real
  chat page + native loop). **Not yet with a live brain**: the card writing and ranking were only checked against the brain's
  answer shape, not run by the app against BrainX.

## Reading long pages and media transcripts (2026-11-10)
- `web_fetch` reads a page in parts: `offset` + `max_chars` (clamped to [500, most]; `AgentToolService.WebFetchReadBudget()`:
  cloud 20k default / 30k most, local = half of `ReadCharBudget`, 4k–20k). The answer says "characters a–b of N" and ends with
  the offset to read on. Parts after the first come from `WebFetchService.FetchForReadingAsync` (8 pages, 10 min, Ok outcomes
  only) so part 2 never downloads again; a first read (offset 0) always fetches fresh. `WebFetchService.Slice` never splits a
  surrogate pair and backs up to a space within the last tenth. Offset past the end = `chat.web.offsetPastEnd`.
- `media_transcript` (`Services/Tools/MediaTranscriptToolModule.cs`, ToolType `MediaTranscript` appended last; helpers in
  `Services/Media/`: `YtDlp.cs`, `Subtitles.cs`, `SpeechToText.cs`): a link (YouTube or anything yt-dlp knows), an 11-char
  YouTube id, or a local audio/video file. A link → yt-dlp subtitles (human first, then the original-language auto track;
  free, card `browse` once per host). `speech=true` (or a file) → ffmpeg 10-min mono 16 kHz pieces → OpenAI `whisper-1`
  with the key of AI Provider › OpenAI — **paid ($0.006/min)**, so its card shows the length and price and is asked even with
  permissions off; refused above 4 h. yt-dlp always runs `--ignore-config --no-playlist` with the URL after `--`.
  Transcripts are saved in `DataRoot\transcripts` (30 days) and read again in parts for free. Neither yt-dlp nor the key is
  bundled: missing ones answer with the install line / where to set the key. Web gate = Features.WebFetch + `feature.webFetch`.
- Proven by `tests/tooltest` WR1–WR3 (slicer, parts through `ExecuteWebFetchAsync` against RedirectMock `/long`, cache life)
  and MT1–MT6 (parsers, track choice, a stand-in yt-dlp, a real ffmpeg tone → a localhost stand-in Whisper).
  **Not yet:** a real yt-dlp run (not installed here), a real Whisper call, the tool driven in the running app, and whether
  CLI agents (Claude Dev / Codex) see it over ChatToolBridge.

## Watches: the AI waits for CI / a background process and is woken with the result (2026-11-07)
- Tools `watch_ci` (pr / commit / branch / repo; none = the current branch's PR, else HEAD), `watch_process` (bg id),
  `watch_list`, `watch_cancel` live in `EngineeringToolModule.Watch.cs`, so every CLI agent gets them over ChatToolBridge
  too. Free (no card): they only read. The tool result tells the model to end its turn — no polling, no sleep.
- `Services/Watch/RunWatchService.cs` (`RunWatchService.Current`, made with the tool modules in App.ConfigureServices):
  CI looked at every 15→90 s through `GhCiProbe` (`gh pr checks --json`, plain listing on an old gh, `gh run list
  --commit|--branch`; argv only, every value re-checked each look, nothing may start with '-'). **gh matches a commit by
  its full hash only** — a short one is expanded with `git rev-parse` or refused. No checks within 5 min = ended;
  5 failed looks in a row or a fatal answer (gh missing / signed out / no such PR) = error; deadline (CI ≤ 6 h, process
  ≤ 12 h). ≤ 3 per chat, ≤ 12 in all, same target = the watch already there. `DataRoot\watch-jobs.json`: CI watches
  are picked up again after a restart, process watches are closed (bg ids restart with the app).
- `ViewModels/ChatViewModel.Watch.cs`: the card row (`ChatMessage.WatchTag`, `WatchCardView`, Cancel). On the end: a run
  works in that chat → notice row + a note it reads at its next step (a note it never took is delivered again at
  EndTurn); nobody works there → a turn starts in that chat (`ContinueChatForPipeAsync(..., fromWatch: true)`: not
  marked FromPipe, off screen in its own list). Settings ▸ Agent ▸ `WatchAutoContinue` off, or more than `MaxWakes` (6)
  such turns in 2 h (reset when the owner writes there) = a notice only. Ctrl+Alt+End and deleting the chat cancel watches.
- Dry-run (`ProcessLaunchPolicy`, every host but CluadeX.exe) blocks gh here too (`GhCiProbe.GhBlocked`): the probe answers
  a fatal `[dry-run]` and `watch_ci` skips `gh pr view` — a harness never polls GitHub unless it adds gh.exe to `Allowed`.
- Proven by `tests/watchtest` (92 offline + 4 `--live` = real gh, read-only, on this repo's PR 91 and commit ddbd141) and
  `tests/chattest` WJ0–WJ14 (`CHATTEST_ONLY=WJ`, the real chat page + native loop + real `cmd` background processes).
  Not yet: a real CI watch woken in the running app, and a CLI agent (Claude Dev / Codex) calling watch_ci for real.

## Opening a chat fast: lazy row parts (2026-11-12)
- The chat row template in `ChatView.xaml` holds every row kind. Each rarely-shown part (question card, permission box,
  plan card, next-step box, agent map, diff, tool output ...) sits in a `ContentControl` with style `LazyRowPart` or
  `LazyRowPart.<Role>`; its Visibility is the part's guard and the part is its `ContentTemplate`. A collapsed element is
  never measured, so the part is built only when it shows: 321 -> 108 elements per row, switch freeze 210 -> 165 ms.
  **A new part that only some rows show goes in such a wrapper too**, guard copied onto the wrapper, its own rules kept.
- `ChatList` caches half a page each side (`VirtualizingPanel.CacheLength`); the default page built 2-3x the visible rows.
- Measure a switch: `tests/animtest` with `--db <copy of codex.db> --switch` (`SwitchChecks.cs`; `SWITCH_TIMELINE=1`,
  `SWITCH_SPLIT=1`, `SWITCH_HANDLERS=1`). Point the copy's `project_path`s at throwaway folders first.
- Not done: row containers are not reused across a switch (a new `Messages` collection = a new ItemsSource).

## Tidy-up from the v3.0.319 logs (2026-11-13)
What 22 hours of `~/.cluadex/logs` + `usage-log.jsonl` on 3.0.319 showed, and what changed. Numbers: 817 Anthropic requests,
$59 billed — cache reads 107.7M tokens ($21.5), cache writes 4.37M ($21.8, at $5/M: 25× a read), output 0.59M ($11.7).
- **A switch the app makes by itself is on screen.** `AgentRunScope.AutoSwitch` (`NoteAutoSwitch` / `ClearAutoSwitch`, event
  `AutoSwitchChanged`) is set at every automatic hop (turn-start routing, a CLI out of its limit, a failed native step, the
  native → CLI handoff) and cleared by the owner's own pick. `ChatViewModel.AutoSwitch.cs`: the composer's model button names
  the stand-in (face, colour, price), the `AutoSwitchChip` beside it says what it stands in for, and its tip says since when,
  when the pick is tried again (`QuotaBook.Until`) and whether the stand-in bills per token. An idle chat keeps the badge
  while the pick is still cooling. Proven by `tests/apitest --failover` and `tests/chattest` MB9 (`CHATTEST_ONLY=PICK`).
- **A CLI login that ran out goes to the other logins first** (`FailoverPolicy.Chain`: plan-priced before pay-per-token; an
  API that failed still goes to the other APIs first; `Settings.FailoverOrder` goes before either). Five chats whose Codex
  login ran out had gone on with the Anthropic API for about $40 while a signed-in Claude login stood by.
- **"auto" effort reads the owner's words only.** `AutoEffortPolicy.OwnWords` now also stops at the picked-skills block
  (`SkillPick.BlockMarkTh/En`) and at a failover's handoff note (`HandoffMarkTh/En`): the skill list's "- name: text" lines
  read as a five-item spec and "debug" in a description as a bug, so 14 of 15 native turns started at xhigh.
- **`browser_*` schemas are deferred** like `desktop_*` (tool search loads them on demand; other providers get them in full).
- **message_chat: 6 per pair per 30 minutes** (was 10; eleven chats sent 102 in a night, one pair ten in 28 minutes).
- **Stop hook**: `{model}` is the run's label, not the provider's class name; its timeout is 8 s (PowerShell start alone took
  over 3 s under load).
- **Her volume**: `AppSettings.BuddyVolume` (0–100, 50 = the old level) through `Services/Voice/BuddyVolume.cs` to her clips
  (`CluadexBuddy.SoundVolume`, its own event — not `SoundsChanged`), the read-aloud player (`VoicePlayer.MasterVolume`) and
  the mic window's page (`say.vol`). Sliders: her right-click menu, Settings › General › Appearance, Settings › Voice. Not
  tied to `BuddySounds`: with her clip sounds off she still reads replies. She is 30 % smaller beside the chat list
  (`HeroBig` 105, `HeroMid` 77, `HeroSmall` 60). Proven by `tests/themetest --hero`, `tests/voicetest`.
- **Notes into a chat have a box**: a row with `IsNotice` is drawn by the notice card (`CxChat.NoticeCard`, a `LazyRowPart`),
  not the System bubble (no fill, no border). `IsChatNote` (it carries `ToolOutput` for the model: a split's report, a
  message_chat) gets the accent stripe and "note from another chat". Proven by `tests/chattest` SR1n (`CHATTEST_ONLY=SPLIT`).
- **Chat list**: the chats take 2.2 parts of the height to the files' 1, rows are 2 px tighter each side, Ctrl+K finds a chat
  by its name or project (300 newest), Ctrl+Tab / Ctrl+Shift+Tab step through the list (`ChatViewModel.StepChat`).
- **Microsoft Edge** takes the same extension: `ChromeBridgeInstall.HostKeys` registers the host under Chrome's key and
  `HKCU\Software\Microsoft\Edge\NativeMessagingHosts`; Settings has "Add the extension to Edge" with Edge's own steps, and
  the status line names the browser connected (`ChromeBridgeConnector.IsEdgeUserAgent`). `tests/chrometest/edge-probe.mjs`
  loads the folder into real Edge on a temp profile (fixed id, every API, window + tab group + debugger + screenshot); it
  touches no registry and no pipe. **Not yet:** a real connection from Edge through the native host. `chrometest` now puts
  back the host registration it found (it used to leave the owner's connection cut until CluadeX started again) — and it
  must not be run while the owner's own Chrome with the extension is open: that Chrome can take the harness's pipe.
- **Found, not changed** (each needs a live API run to tune): the conversation's cache marks live 5 minutes, so one tool
  that runs longer (a build, a wait for approval) re-writes the whole conversation (104k tokens = $0.52 once, step 82);
  the image hard cap re-wrote 95k tokens to drop 24k (step 92); the tools have no cache mark of their own, so a chat with
  another system prompt re-writes the same 30k of tool schemas; `ReplyVoiceQueue` let 9 replies wait behind the one read.

## Claude Dev runs the newest Claude Code CLI on the PC (2026-11-13)
- Owner: "ทำไม fable ยัง 5.1 ยังไม่ล่าสุด". Claude Dev sends the CLI an alias ("fable", "opus" …) and the CLI turns it into
  a model, so the model is as new as the CLI. `ClaudeDevProvider.FindCli` took the first install it met — PATH before
  everything — and npm's copy was 2.1.296 (2026-10-20, before Fable 5.2 shipped on 2026-11-09) while the desktop app's
  was 2.1.321. `claude-fable-5-2` is not in the 2.1.296 exe at all (0 hits; 25 in 2.1.321).
- Now every install is a candidate (`CliCandidates`: PATH, npm's, the desktop app's newest folder first, the native
  installer's) and the newest version wins; equal versions, or a preferred copy that cannot say its version, keep the old
  order. `CliVersionAt` reads the version without starting anything: the desktop app's version folder, npm's
  `package.json` beside `bin\`, else the exe's version resource.
- `Settings.ClaudeCodePathOverride` (a claude.exe that exists) goes before the search (`CliOverride`): the way back when
  the newest is not the one wanted. A copy that an update removed while CluadeX runs is looked for again at the next turn
  (`InitializeAsync` re-checks `File.Exists`) — the desktop app replaces its versioned folder.
- Proven by `tests/apitest --clifind` (16 checks on a made-up disk) and `--clifind-real` (this PC's real installs, read
  only: it prints each with its version and the one picked). **Not yet:** a real Claude Dev turn on the newer copy, and
  the model id its `fable` alias reports — the harness's shell is not signed in to the CLI.

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

## Trying a branch without a release (2026-11-10)
Owner: releases take too long to try work — and (2026-11-11) "เมื่อทำเสร็จคุณบิ้วทดสอบเอง ไม่ต้องให้ฉันลอง".
When a branch is done, test it yourself:
`powershell -ExecutionPolicy Bypass -File Scripts\try-branch.ps1 <branch> -Smoke` (~2 min). It builds, opens the trial
minimised, checks it, closes it, prints PASS/FAIL per check and exits 1 on a failure. Report those lines; do not hand the
owner a shortcut to try. Check the feature itself as well (its test harness, or the trial's log under
`D:\CluadeX-Try\<branch>\app\Data\logs`). Without `-Smoke` the script makes a desktop shortcut "CluadeX ตัวลอง - <branch>.lnk"
for when the owner asks to try it themselves.
- `-Smoke` checks: still running, main window, chip says "ตัวลอง", `app\Data\install.json` is the trial's, no trial path in
  `~\.cluadex`, BrainX `mcp-bridges.json` or the Chrome host manifest, `CluadeX.lnk` unchanged, no update check, no
  `[CRITICAL]`/unhandled line in this run's log (`[ERROR]` lines are listed, not failed).
- It builds Debug (~1 min), copies to `D:\CluadeX-Try\<branch>\app` with `portable.txt` + `trial.txt`, copies the owner's
  `settings.json` + `mcp_servers.json` once (read-only copy), and makes its own shortcut — never `CluadeX.lnk`.
- `trial.txt` = `SettingsService.IsTrialCopy`: no BrainX bridge repoint (`ConnectionService.ReconcileAsync/RegisterAsync`),
  no Chrome host registration (`ChromeBridgeConnector`), no self-update (`AutoUpdateService.StartBackgroundChecks` and the
  window's `MainViewModel.CheckForUpdate` — a portable copy used to offer the release over itself), no desktop shortcut.
  The version chip says "ตัวลอง".
- A branch older than v3.0.306 lacks these guards: the script refuses it — merge main into it first.
- `-Remove` deletes the trial folder, its shortcut and the worktree the script made.
