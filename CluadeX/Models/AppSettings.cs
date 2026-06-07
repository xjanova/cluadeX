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
    public uint ContextSize { get; set; } = 4096;
    public int GpuLayerCount { get; set; } = -1; // -1 = auto (all layers)
    public float Temperature { get; set; } = 0.6f;
    public float TopP { get; set; } = 0.9f;
    public int MaxTokens { get; set; } = 4096;
    public int RepeatPenaltyTokens { get; set; } = 64;
    public float RepeatPenalty { get; set; } = 1.1f;

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
    public bool InstinctLearningEnabled { get; set; } = false;

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
