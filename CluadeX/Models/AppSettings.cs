using System.IO;

namespace CluadeX.Models;

public class AppSettings
{
    // Directory paths - defaults are set by SettingsService based on portable vs installed mode
    public string ModelDirectory { get; set; } = string.Empty;
    public string CacheDirectory { get; set; } = string.Empty;
    public string LogDirectory { get; set; } = string.Empty;
    public string TempDirectory { get; set; } = string.Empty;
    public string SessionDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Additional directories to scan for GGUF models (besides the main ModelDirectory).
    /// Users can add folders where they already have models downloaded.
    /// </summary>
    public List<string> AdditionalModelDirectories { get; set; } = new();

    public string? SelectedModelPath { get; set; }
    public string? SelectedModelName { get; set; }

    // Inference settings
    // 8192 (was 4096): CluadeX's agentic system prompt + the core tool schemas alone are ~4.4k tokens, so a
    // 4096 window overflowed on the VERY FIRST agentic message ("request exceeds the available context size").
    // 8192 fits the agent loop with room for a real conversation and costs only ~tens of MB extra KV cache on
    // an 8B model — well within the GPUs that run local mode. Raise further for big-repo work.
    // 12288, not 8192. Measured on a real agentic turn: system prompt (~2.7k) + the CORE tool
    // schemas + one source file + a couple of tool results lands at ~8.2k — i.e. 8192 overflows by
    // a few tokens and compaction can't help, because that floor is not history. 12k leaves room to
    // actually finish a task; the extra KV cache is ~0.3 GB on a 7B Q4 model.
    public uint ContextSize { get; set; } = 12288;
    public int GpuLayerCount { get; set; } = -1; // -1 = auto (all layers)
    public float Temperature { get; set; } = 0.6f;
    public float TopP { get; set; } = 0.9f;
    public int MaxTokens { get; set; } = 4096;
    public int RepeatPenaltyTokens { get; set; } = 64;
    public float RepeatPenalty { get; set; } = 1.1f;
    /// <summary>top-k sampling sent on the local native tool-call path (llama-server/Ollama). Weak
    /// models drift into low-probability garbage without it, exactly when emitting structured JSON.</summary>
    public int TopK { get; set; } = 40;
    /// <summary>min-p sampling sent on the local native tool-call path. Trims the low-probability tail
    /// so tool-call JSON stays well-formed. 0 disables.</summary>
    public float MinP { get; set; } = 0.05f;
    /// <summary>Temperature used on the native tool-calling path — lower than the chat temperature because
    /// structured tool-call JSON needs determinism, not creativity. The tool path uses
    /// Min(Temperature, ToolCallTemperature) so an already-low chat temp is respected.</summary>
    public float ToolCallTemperature { get; set; } = 0.2f;

    // Backend settings
    public string GpuBackend { get; set; } = "Auto";
    public int BatchSize { get; set; } = 512;
    public int ThreadCount { get; set; } = 0;

    /// <summary>
    /// Path to a custom llama.cpp backend directory containing llama.dll, ggml.dll, etc.
    /// When set, overrides the bundled LLamaSharp backend DLLs.
    /// Use this to support newer model architectures (e.g., Gemma 4) before LLamaSharp updates.
    /// </summary>
    public string? CustomLlamaCppBackendPath { get; set; }

    // Agent settings
    public bool AutoExecuteCode { get; set; } = false;
    public int MaxAutoFixAttempts { get; set; } = 3;
    /// <summary>Max steps the agentic loop takes before stopping (clamped 1–100). Higher handles bigger tasks —
    /// 50, not 25: serve → open → check the console → fix → re-check a web page alone takes a dozen steps.</summary>
    public int MaxAgentIterations { get; set; } = 50;

    /// <summary>Start every chat in "Accept edits" mode: write_file / edit_file / multi_edit / create_directory
    /// INSIDE the open project run without a prompt (never execute, MCP, browse or paths outside the project).</summary>
    public bool AcceptEditsDefault { get; set; } = false;

    /// <summary>The agent opening a folder OUTSIDE the open project (a related repo it found on its own, open_project):
    /// "ask" (default — a prompt with "Always allow this folder") or "allow" (never ask). Owner 2026-10-18: "ต้องขออนุญาต
    /// ในการเปิดโฟลเดอร์อื่น จะให้ถามเสมอ หรือไม่ต้องถามอีกก็ได้".</summary>
    public string RelatedFolderAccess { get; set; } = "ask";
    /// <summary>Folders answered "Always allow": the agent opens them (and anything inside) without asking.</summary>
    public List<string> AllowedProjectFolders { get; set; } = new();

    // ─── Dev Preview: background processes + built-in browser (feature.devPreview) ───
    /// <summary>Browser engine of the browser_* tools: "auto" or "webview2" (the built-in browser), or "chrome" (the
    /// user's real Chrome through CluadeX's own extension, Settings ▸ Agent ▸ Your Chrome).</summary>
    public string BrowserEngine { get; set; } = "auto";
    /// <summary>Longest edge, in pixels, of a screenshot sent to a model (also capped at ~1.15 MP).</summary>
    public int BrowserScreenshotMaxEdge { get; set; } = 1568;
    /// <summary>Pages outside localhost / the open project: "ask" (default), "allow" or "deny".</summary>
    public string BrowserAllowExternal { get; set; } = "ask";
    /// <summary>Most background processes (dev servers, watchers) running at once.</summary>
    public int BgMaxProcesses { get; set; } = 8;
    /// <summary>Tool images kept in the conversation sent to the model; older ones become a text note with the file path.</summary>
    public int KeepRecentImages { get; set; } = 2;
    /// <summary>Older tool images may pile up this many past KeepRecentImages before they are cut back in one go
    /// (each cut rewrites an earlier turn: one prompt-cache restart per cut instead of one per screenshot).</summary>
    public int ImagePruneSlack { get; set; } = 3;

    // ─── Claude in Chrome connector (through Claude Code) ───
    /// <summary>The user switched the "Browser (Claude in Chrome)" connector on.</summary>
    public bool ChromeConnectorEnabled { get; set; } = false;
    /// <summary>Full path of claude.exe (or cli.js) to use instead of the one CluadeX finds. Empty = find it.</summary>
    public string ClaudeCodePathOverride { get; set; } = "";
    /// <summary>The browser the connector drives when several are connected (the extension's device id). Empty = ask.</summary>
    public string ChromePairedDeviceId { get; set; } = "";
    /// <summary>Let Claude in Chrome act without its own per-action prompts — only honoured in bypass mode.</summary>
    public bool ChromeActWithoutAsking { get; set; } = false;
    /// <summary>IDLE timeout (seconds) for an interactive model call in the chat/agent loop: the window
    /// resets on every streamed token, so a healthy (even slow) generation is never cut off — only a
    /// connection that produces NOTHING for this long is aborted, surfacing a clean "timed out" error
    /// (which the auto-retry then handles) instead of sitting on the shared HttpClient's 5-minute ceiling.
    /// 0 = no idle cap (rely on the HttpClient ceiling).</summary>
    public int InteractiveRequestTimeoutSeconds { get; set; } = 120;
    /// <summary>Require the agent to read a file before editing it, and block edits to a file that changed on
    /// disk since the last read (Claude Code-style — prevents blind edits and clobbering external changes).</summary>
    public bool EnforceReadBeforeEdit { get; set; } = true;
    /// <summary>When the agent edits a file, auto-open/refresh it in the Code Editor and scroll to the change.</summary>
    public bool LiveEditFollow { get; set; } = true;

    /// <summary>When the API's credit/quota runs out mid-task, carry the same turn on with Claude Dev (the user's
    /// Claude login) and a handoff brief instead of stopping on a billing error.</summary>
    public bool AutoFailoverOnQuota { get; set; } = true;

    // ─── Dispatch policy (Services/Agents/DispatchPolicy.cs) ───
    /// <summary>Effort decides when the agent fans out: low none, medium read-only sub-agents on the cheapest model,
    /// high workers in git worktrees with a reviewer, xhigh/max also speculative fixes. Off = sub-agents as before.</summary>
    public bool DispatchPolicyEnabled { get; set; } = true;

    // ─── Desktop control (Services/Desktop) ───
    /// <summary>The desktop_* tools: see and drive other programs on this PC (testing a desktop app, a game window,
    /// an installer). On by default — every app still asks once per run (or "Allow in this chat").</summary>
    public bool DesktopControlEnabled { get; set; } = true;
    /// <summary>Sub-agents, workers and fixers one run may have going at the same time.</summary>
    public int MaxParallelAgents { get; set; } = 3;
    /// <summary>Race 2–3 fixers in their own worktrees on a red build the main loop could not fix.</summary>
    public bool SpeculativeFixEnabled { get; set; } = true;
    public int SpeculativeFixCount { get; set; } = 3;
    /// <summary>How long a speculative-fix race may take before the main loop gets the build back.</summary>
    public int SpeculativeFixMinutes { get; set; } = 6;
    /// <summary>Model per sub-agent kind ("explore", "code", "review", "fix", "hardfix"); empty = the policy's choice.</summary>
    public Dictionary<string, string> SubAgentModelOverrides { get; set; } = new();
    /// <summary>When the agent mutates a file while the user is on the Chat page, auto-switch to the
    /// Code Editor page so the edit is visible live (the editor embeds the same chat docked right).
    /// Off by default since 2026-10-20 (owner: "ไม่จำเป็นต้องสลับไปดูแบบ real time อัตโนมัติแล้ว แต่ให้อัพเดทตามกันจริงๆ
    /// เมื่อเขียนไฟล์") — the editor follows the files without taking the user off the page they are on.</summary>
    public bool AutoOpenEditorOnAgentEdit { get; set; } = false;

    /// <summary>Where a new project the AI creates goes (open_project create="true" with a name). Empty = the folder that
    /// already holds most of the user's projects (e.g. D:\Code), else Documents\CluadeX Projects.</summary>
    public string? DefaultProjectRoot { get; set; }

    /// <summary>The one-time switch of <see cref="AutoOpenEditorOnAgentEdit"/> to off (2026-10-20) was applied.</summary>
    public bool EditorNoAutoSwitchMigrated { get; set; }

    /// <summary>The newest settings.json.corrupt-* backup already looked at for lost API keys (each backup once).</summary>
    public string? KeyRecoveryCheckedBackup { get; set; }
    /// <summary>Animate agent edits in the Code Editor as live typing (typewriter reveal of the changed
    /// region + flash highlight when done). Falls back to an instant refresh for very large changes.</summary>
    public bool LiveCodingAnimationEnabled { get; set; } = true;
    /// <summary>Code Editor view toggles (toolbar): long lines wrap, spaces/tabs/line ends shown, text zoom.</summary>
    public bool EditorWordWrap { get; set; }
    public bool EditorShowWhitespace { get; set; }
    public double EditorZoom { get; set; } = 1.0;
    public string PreferredLanguage { get; set; } = "C#";
    /// <summary>Code Editor ▸ AI comments: how much the AI comments the code it writes ("none" | "key" | "detailed") and in
    /// which language ("th" | "en" | "match" = the file's own). "none" sends no instruction at all — the old behaviour.
    /// <see cref="CodeCommentProjects"/> overrides both for one project (key: Services.CodeCommentPolicy.ProjectKey).</summary>
    public string CodeCommentLevel { get; set; } = "none";
    public string CodeCommentLanguage { get; set; } = "match";
    public Dictionary<string, CodeCommentSetting> CodeCommentProjects { get; set; } = new();

    /// <summary>
    /// Local native tool-calling. When ON, llama-server / Ollama use OpenAI-style function-calling
    /// (tools + tool_calls) and run the SAME structured native agent loop as Anthropic, instead of the
    /// fragile [ACTION:] text parser. Turn OFF for local models whose chat template lacks tool support
    /// (older GGUFs) — they fall back to the text-protocol loop. Default ON: modern coding models
    /// (Qwen2.5-Coder, Llama 3.x, Hermes, Mistral-Nemo) all ship tool templates.
    /// </summary>
    public bool LocalNativeToolUseEnabled { get; set; } = true;

    /// <summary>
    /// Tool-call repair for weak local models. When ON, if the model emits NO structured tool_call but its
    /// text contains a JSON/fenced tool-call ({name,arguments} / OpenAI tool_calls shape), CluadeX salvages
    /// it and continues the loop instead of ending the turn; and unknown/misspelled tool names get a
    /// "did you mean X?" suggestion fed back. Deterministic + only accepts names that resolve to real tools.
    /// </summary>
    public bool LocalToolCallRepairEnabled { get; set; } = true;

    /// <summary>
    /// Plan-first for weak local models: on the FIRST step of a non-trivial task, force a tool call
    /// (constrained decoding) so the model STARTS acting (read/search/plan via todo_write) instead of
    /// stalling with prose and ending the turn. Local-only; uses the provider's tool_choice="required".
    /// </summary>
    public bool PlanFirstForceToolEnabled { get; set; } = true;

    /// <summary>
    /// Mid-loop goal re-injection: every few agent steps, re-state the user's original request + the
    /// read→verify rule so a weak small-context model doesn't drift after microcompaction trims old turns.
    /// </summary>
    public bool MidLoopReminderEnabled { get; set; } = true;
    /// <summary>Re-inject the goal reminder every N agent steps (min 2).</summary>
    public int MidLoopReminderEvery { get; set; } = 4;

    /// <summary>
    /// Auto-recall from BrainX. When ON, before each agentic task CluadeX searches the connected
    /// BrainX MCP brain for relevant coding-lessons / past decisions and injects the top hits into
    /// the model's context. Best-effort + short-timeout: if the brain is offline the task proceeds
    /// normally. The agent can also pull on demand via the `brain_recall` tool regardless of this flag.
    /// </summary>
    public bool BrainAutoRecallEnabled { get; set; } = true;

    /// <summary>
    /// Semantic codebase search. When ON, codebase_search re-ranks its keyword candidates by embedding
    /// similarity (via Ollama EmbeddingModel) for "find code that does X" queries. Fully graceful: if
    /// Ollama / the embedding model isn't available it silently falls back to keyword ranking. No
    /// persistent index is built — embeddings are computed on demand for the query + top candidates.
    /// </summary>
    public bool SemanticSearchEnabled { get; set; } = true;

    /// <summary>Ollama model used for embeddings (semantic codebase search). Pull it with `ollama pull nomic-embed-text`.</summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    // ─── Autonomous coding loop (selling-point) ───
    // Edit → run the project's build/tests ON THIS MACHINE → read failures → fix → repeat until green,
    // then review the changes for hidden bugs, iterating. All loops are capped to avoid runaway cost.
    /// <summary>
    /// Auto-verify after edit, in the DEFAULT interactive loop (not just the opt-in autonomous mode). When ON,
    /// after a turn that edited/wrote files — and the model didn't already verify — CluadeX auto-runs the
    /// detected build and feeds failures back, so a weak model is forced to confront real compiler errors
    /// instead of declaring false victory. Bounded per task. System-initiated, so it doesn't prompt for
    /// permission. Set AutoVerifyCommand to override the detected build command.
    /// </summary>
    public bool AutoVerifyAfterEditEnabled { get; set; } = true;

    // ─── Model-escalation ladder (local → stronger API model) ───
    /// <summary>
    /// Escalate to a stronger API model when the LOCAL model stalls (hits max iterations OR repeats the same
    /// error several turns). This is the "local-first with a safety net" lever: the ~30% of work a small model
    /// can't finish (deep architecture, novel design, gnarly debugging) gets handed to Claude/etc.
    /// OPT-IN — it SENDS your conversation + tool results to the external provider below and costs money.
    /// </summary>
    public bool EscalationEnabled { get; set; } = false;
    /// <summary>Provider to escalate to. Must support native tool use ("Anthropic" today; OpenAI/Gemini need a
    /// native tool-loop implementation first). Configure that provider's API key + model in its own settings.</summary>
    public string EscalationProviderName { get; set; } = "Anthropic";

    /// <summary>Master switch for the autonomous build-test-fix-review loop (opt-in: it runs commands + spends tokens).</summary>
    public bool AutonomousLoopEnabled { get; set; } = false;
    /// <summary>Max build/test → fix attempts before giving up (1-25).</summary>
    public int AutoFixMaxIterations { get; set; } = 5;
    /// <summary>Verify command to run after each fix. Empty = auto-detect from the project (dotnet/npm/cargo/go/pytest).</summary>
    public string AutoVerifyCommand { get; set; } = "";
    /// <summary>After the build is green, run the hidden-bug review loop.</summary>
    public bool AutoReviewEnabled { get; set; } = true;
    /// <summary>"until_clean" = review+fix until a pass finds nothing (capped); "fixed_rounds" = always run N rounds.</summary>
    public string AutoReviewMode { get; set; } = "until_clean";
    /// <summary>Review rounds: the hard cap for until_clean, and the exact count for fixed_rounds (1-25).</summary>
    public int AutoReviewMaxRounds { get; set; } = 3;

    // Extended Thinking (Anthropic Claude)
    // Note: budget_tokens must be < max_tokens when thinking is enabled.
    // When thinking is on, MaxTokens is auto-raised to 16384 if lower.
    public bool ExtendedThinkingEnabled { get; set; } = false;
    public int ThinkingBudgetTokens { get; set; } = 10000; // default 10K thinking tokens
    /// <summary>"auto" or low / medium / high / xhigh / max — see <see cref="EffortLevels"/>.</summary>
    public string ReasoningEffort { get; set; } = EffortLevels.Auto;
    public bool PromptCachingEnabled { get; set; } = true; // use Anthropic prompt caching
    /// <summary>Claude loads MCP tools on demand (server-side tool search) instead of carrying every definition in
    /// every request. Off = all tools up front, as before.</summary>
    public bool ToolSearchEnabled { get; set; } = true;
    /// <summary>A paid run that has spent this much (USD) stops and asks before going on; each "go on" allows the
    /// same again. 0 = never asks.</summary>
    public decimal RunBudgetUsd { get; set; } = 2m;
    /// <summary>A paid run asks before going on once CluadeX has spent this much today (USD, all chats). 0 = no daily cap.</summary>
    public decimal DailyBudgetUsd { get; set; }
    /// <summary>The chat page's right-hand panel (project files, latest image, latest run) is shown.</summary>
    public bool ChatSidePanel { get; set; } = true;
    /// <summary>Widths the user dragged panels to, by <see cref="Views.ResizeGrip.Key"/> (px).</summary>
    public Dictionary<string, double> PanelWidths { get; set; } = new();
    public bool ShowThinkingSteps { get; set; } = true; // show AI thinking/reasoning in chat

    /// <summary>
    /// Show the agent's working-out in the chat transcript — the per-tool rows and
    /// status lines. OFF by default (owner, 2026-08-04: *"ในช่องแชทให้เหลือแต่สิ่งที่ผู้ใช้
    /// ต้องการเห็นพอ"*): a run that reads six files and searches the brain twice buries
    /// the actual answer under nine rows of machinery the user never asked to see.
    ///
    /// Hiding is never total. A failed tool, a file diff and a permission request stay
    /// visible at all times — those are things the user must judge, not progress
    /// chatter, and a UI that hides a failure is back to lying about its own state.
    /// </summary>
    public bool ShowAgentInternals { get; set; } = false;

    // Microcompact — shrink old tool results before resending to the API.
    // Keeps the most recent KeepRecentTurns tool_result blocks verbatim; older ones
    // get summarized. Without this, long agentic loops balloon the prompt because
    // the same large tool outputs keep being re-sent each turn.
    public bool MicrocompactEnabled { get; set; } = true;
    public int MicrocompactKeepRecentTurns { get; set; } = 3;       // recent assistant/user pairs to preserve verbatim
    public int MicrocompactMaxOldResultChars { get; set; } = 800;   // cap on older tool_result content

    // Session Memory — on session end, extract durable facts from the transcript and
    // persist them as memory files. Runs in background; never blocks the UI. Off by
    // default to avoid surprising the user with LLM calls they didn't ask for.
    public bool SessionMemoryEnabled { get; set; } = false;

    // Voice mode (2026-10-19) — both off by default.
    /// <summary>While the mic window is open, it reads each finished reply aloud with the browser's own free speech
    /// synthesis (a Thai voice when there is one). Costs nothing.</summary>
    public bool VoiceSpeakReplies { get; set; } = false;
    /// <summary>The Live voice button: a GPT-Live call (OpenAI Live API, $0.05/min + the model doing the work) where
    /// each spoken request runs through this chat's agent on the active provider. Needs an OpenAI API key.</summary>
    public bool VoiceLiveEnabled { get; set; } = false;
    /// <summary>A Live voice session is closed after this many minutes (the cost cap). 1-60.</summary>
    public int VoiceLiveMaxMinutes { get; set; } = 10;
    /// <summary>GPT-Live's voice (fixed for a session). marin is the API's default.</summary>
    public string VoiceLiveVoice { get; set; } = "marin";

    /// <summary>Background work nobody waits for (session-memory and instinct extraction) goes through the
    /// provider's batch API when it has one — Anthropic and OpenAI: half the price, the result lands within
    /// minutes to hours instead of seconds. Off → those calls run live, as before.</summary>
    public bool BatchBackgroundWork { get; set; } = true;

    /// <summary>The desktop pet says things now and then (moods, builds, late nights).</summary>
    public bool PetTalks { get; set; } = true;

    /// <summary>The pet may follow the mouse around to tease you.</summary>
    public bool PetMischief { get; set; } = true;

    /// <summary>Prank mode (off by default): the pet now and then leaves a poop on its floor and nibbles a patch of the
    /// screen. Clicking the pet fast (punishing it) puts everything back. Never over the place you are typing.</summary>
    public bool PetPranks { get; set; }

    /// <summary>
    /// Instinct learning loop. When ON, after a session ends CluadeX runs a background LLM pass over the
    /// transcript to extract reusable behavioral instincts (corrections the user made, approaches that
    /// worked) into the Instinct system; STRONG instincts then sync to BrainX. Opt-in (off by default)
    /// because it spends tokens the user didn't explicitly request — essentially free on a local model.
    /// </summary>
    public bool InstinctLearningEnabled { get; set; } = true;

    // Strategic Compaction Toast (Sprint 2 #2) — non-blocking nudge that
    // appears at logical breakpoints (50+ tool calls / context ≥ 60% / 75%),
    // letting the user one-click compact BEFORE the alarm fires. On by default
    // because it's a pure UX improvement; can be disabled if the user finds
    // it too chatty.
    public bool EnableStrategicCompactionToast { get; set; } = true;

    /// <summary>Compact by itself where the toast would ask (owner, 2026-10-13: "ที่มันคอยถามว่าจะบีบอัดไหม ให้มีติ๊ก
    /// เพื่อให้บีบอัดออโต้ได้"). Ticked on the toast or in Settings. A turn in progress is never cut: the compact waits
    /// for it to end. Off by default — compacting changes what the model is sent, so it is the user's choice.</summary>
    public bool AutoCompactWhenSuggested { get; set; } = false;

    /// <summary>The credit balance the user read off the provider's billing page (USD; null = not set). No provider
    /// offers an API for the prepaid balance (Anthropic, OpenAI, Google — checked 2026-10-13), so the spend panel shows
    /// this minus what CluadeX has spent since <see cref="CreditBalanceSetAt"/> — an estimate, labelled as one.</summary>
    public decimal? CreditBalanceUsd { get; set; }
    public DateTime? CreditBalanceSetAt { get; set; }
    /// <summary>Today's spend at the moment the balance was entered (what that day spent before it is not subtracted).</summary>
    public decimal CreditBalanceSpentThatDay { get; set; }
    /// <summary>Independent API credit anchors and package prices; legacy unassigned credit stays separate.</summary>
    public Dictionary<string, ProviderSpendSettings> ProviderSpending { get; set; } = new();

    // Model Catalog view preference — list (default) vs grid (2-column tiles).
    public bool ModelCatalogGridView { get; set; } = false;

    // Sidebar system menu — collapsed by default to give chat history maximum real estate.
    public bool SidebarNavExpanded { get; set; } = false;

    // UI settings
    public double FontSize { get; set; } = 14;
    public bool StreamOutput { get; set; } = true;

    // Language / Localization
    public string Language { get; set; } = "en"; // "en" or "th"

    /// <summary>The theme the app starts in and the picker shows as in use: "Classic", "Midnight" or "NeonCyber"
    /// (Services.ThemeIds). Anything else starts Midnight and is rewritten — see ThemeService.ApplySaved.</summary>
    public string Theme { get; set; } = "Midnight";

    /// <summary>Lavender Night's custom background / accent (#RRGGBB; empty = the theme's own). See ThemeTint.</summary>
    public string LavenderBackground { get; set; } = "";
    public string LavenderAccent { get; set; } = "";

    // Activation Key — unlocks advanced features
    public string? ActivationKey { get; set; }
    /// <summary>Server license_type of <see cref="ActivationKey"/> (demo, yearly, lifetime, …).</summary>
    public string? LicenseType { get; set; }
    /// <summary>When the saved key stops working (UTC); null = never.</summary>
    public DateTime? LicenseExpiresUtc { get; set; }
    /// <summary>Last time the server confirmed the saved key (UTC) — bounds how long it keeps working offline.</summary>
    public DateTime? LicenseValidatedUtc { get; set; }
    /// <summary>This install already asked the server for its one free trial key — never ask again.</summary>
    public bool LicenseTrialRequested { get; set; }
    /// <summary>The "your free trial has ended" invitation has been shown.</summary>
    public bool LicenseTrialEndedNoticeShown { get; set; }

    /// <summary>The user chose "Not interested" / "don't show again" on the BrainX invitation — never opened by
    /// itself again (the brain chip still opens it on request).</summary>
    public bool BrainXInviteDismissed { get; set; }

    /// <summary>The user removed BrainX on the MCP Servers page or pressed "Not interested": an installed BrainX is
    /// no longer registered and started by itself at launch. Cleared only by pressing Connect / Install in the
    /// BrainX window.</summary>
    public bool BrainXAutoConnectDeclined { get; set; }

    /// <summary>Host name or IP of the NVIDIA DGX Spark on the local network that serves models (null = none).</summary>
    public string? DgxSparkHost { get; set; }

    /// <summary>The context size in force before a Spark model raised it — put back the moment CluadeX leaves the
    /// Spark, because the same setting sizes a local llama-server load (a Spark-sized window on a desktop GPU is
    /// the out-of-VRAM case).</summary>
    public uint? DgxSparkSavedContextSize { get; set; }

    // Feature Toggles — users can enable/disable optional features
    public FeatureToggles Features { get; set; } = new();

    // HuggingFace settings
    public string? HuggingFaceToken { get; set; }

    // AI Provider settings
    public AiProviderType ActiveProvider { get; set; } = AiProviderType.Local;
    public Dictionary<string, ProviderConfig> ProviderConfigs { get; set; } = new();
}

/// <summary>
/// Feature toggles — allows users to enable/disable optional features.
/// All core features default to enabled. Fun/experimental features can be toggled.
/// </summary>
public class FeatureToggles
{
    // Core (always available)
    public bool BuddyCompanion { get; set; } = true;
    public bool PluginSystem { get; set; } = true;
    public bool TaskManager { get; set; } = true;
    public bool WebFetch { get; set; } = true;
    public bool GitIntegration { get; set; } = true;
    public bool GitHubIntegration { get; set; } = true;
    public bool ContextMemory { get; set; } = true;
    public bool SmartEditing { get; set; } = true;
    public bool MarkdownRendering { get; set; } = true;

    // MCP Servers
    public bool McpServers { get; set; } = true;

    // Dev Preview — bg_* background processes and browser_* built-in browser. On by default; the license gate
    // (feature.devPreview, see Services/Tools/DevPreviewGate) decides separately whether it may run.
    public bool DevPreview { get; set; } = true;

    // Security
    public bool PermissionSystem { get; set; } = true;
    public bool DangerousCommandBlocking { get; set; } = true;
    // NOTE: there are deliberately no PathTraversalProtection / DpapiEncryption toggles here.
    // Both protections are unconditional — FileSystemService.ResolveSafePath always enforces
    // containment and SettingsService always encrypts with DPAPI. Fields for them used to exist,
    // were read by nothing, and only served to make settings.json look like security could be
    // switched off. The Features page shows them as always-on badges (IsToggleable = false).
}

/// <summary>One project's own AI-comment choice (AppSettings.CodeCommentProjects): same values as the global pair.</summary>
public class CodeCommentSetting
{
    public string Level { get; set; } = "none";
    public string Language { get; set; } = "match";
}
