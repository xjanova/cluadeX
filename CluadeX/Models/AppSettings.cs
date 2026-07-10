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
    public uint ContextSize { get; set; } = 8192;
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
    /// <summary>Max steps the agentic loop takes before stopping (clamped 1–100). Higher handles bigger tasks.</summary>
    public int MaxAgentIterations { get; set; } = 25;
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
    /// <summary>When the agent mutates a file while the user is on the Chat page, auto-switch to the
    /// Code Editor page so the edit is visible live (the editor embeds the same chat docked right).</summary>
    public bool AutoOpenEditorOnAgentEdit { get; set; } = true;
    /// <summary>Animate agent edits in the Code Editor as live typing (typewriter reveal of the changed
    /// region + flash highlight when done). Falls back to an instant refresh for very large changes.</summary>
    public bool LiveCodingAnimationEnabled { get; set; } = true;
    public string PreferredLanguage { get; set; } = "C#";

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
    public bool PromptCachingEnabled { get; set; } = true; // use Anthropic prompt caching
    public bool ShowThinkingSteps { get; set; } = true; // show AI thinking/reasoning in chat

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

    // Model Catalog view preference — list (default) vs grid (2-column tiles).
    public bool ModelCatalogGridView { get; set; } = false;

    // Sidebar system menu — collapsed by default to give chat history maximum real estate.
    public bool SidebarNavExpanded { get; set; } = false;

    // UI settings
    public double FontSize { get; set; } = 14;
    public bool StreamOutput { get; set; } = true;

    // Language / Localization
    public string Language { get; set; } = "en"; // "en" or "th"

    // Activation Key — unlocks advanced features
    public string? ActivationKey { get; set; }

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

    // Security
    public bool PermissionSystem { get; set; } = true;
    public bool DangerousCommandBlocking { get; set; } = true;
    public bool PathTraversalProtection { get; set; } = true;
    public bool DpapiEncryption { get; set; } = true;
}
