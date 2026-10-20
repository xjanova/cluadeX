using System.Text;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Tracks context window usage, token estimates, and memory consumption.
/// Provides real-time metrics for the chat UI.
/// </summary>
public class ContextMemoryService
{
    private readonly SettingsService _settingsService;

    public ContextMemoryService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// Estimate token count for a string using a word-based heuristic.
    /// More accurate than simple char division:
    /// - Average English word ≈ 1.3 tokens (subword tokenization)
    /// - Code tokens are shorter (operators, brackets count as separate tokens)
    /// - Whitespace/newlines are generally free or merged
    /// </summary>
    public int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        int tokens = 0;

        // Count words (sequences of alphanumeric/underscore chars)
        bool inWord = false;
        int wordCount = 0;
        int punctCount = 0;
        int digitRunCount = 0;
        bool inDigitRun = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (char.IsLetterOrDigit(c) || c == '_')
            {
                if (!inWord) { wordCount++; inWord = true; }
                if (char.IsDigit(c))
                {
                    if (!inDigitRun) { digitRunCount++; inDigitRun = true; }
                }
                else
                {
                    inDigitRun = false;
                }
            }
            else
            {
                inWord = false;
                inDigitRun = false;

                // Punctuation/operators each typically become their own token
                if (!char.IsWhiteSpace(c))
                    punctCount++;
            }
        }

        // Words: ~1.3 tokens each (subword tokenization splits long/uncommon words)
        tokens += (int)(wordCount * 1.3);

        // Each punctuation/operator is roughly 1 token
        tokens += punctCount;

        // Newlines: roughly 1 token per newline
        int newlines = 0;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') newlines++;
        tokens += newlines;

        // Minimum: at least 1 token for non-empty text
        return Math.Max(1, tokens);
    }

    /// <summary>
    /// Calculate total tokens used across all messages in a conversation.
    /// </summary>
    public int CalculateTotalTokens(IEnumerable<ChatMessage> messages)
    {
        int total = 0;
        foreach (var msg in messages)
        {
            total += EstimateTokens(msg.Content);
            if (msg.ToolOutput != null)
                total += EstimateTokens(msg.ToolOutput);
        }
        return total;
    }

    /// <summary>The context window of the model that answers this chat. It used to be Settings.ContextSize for
    /// every provider — a LOCAL setting (12,288 by default) — so with Claude (1M tokens) about 11k tokens of
    /// chat read as FULL, the bar said "Start a New Chat NOW", and the agent loop's 75% auto-compaction
    /// summarised history a 1M-token model had plenty of room for.</summary>
    public int MaxContextTokens
    {
        get
        {
            var s = _settingsService.Settings;
            string? model = s.ProviderConfigs.TryGetValue(s.ActiveProvider.ToString(), out var cfg) ? cfg.EffectiveModelId : null;
            if (s.ActiveProvider == AiProviderType.Anthropic && string.IsNullOrWhiteSpace(model))
                model = Providers.AnthropicProvider.DefaultModel;
            return ContextWindowFor(s.ActiveProvider, model, s.ContextSize);
        }
    }

    /// <summary>Context window in tokens for a provider + model. Local backends (GGUF, llama-server, Ollama) run
    /// at the window the user configured. API models use the published windows; an unknown id gets its
    /// family's conservative value, so a model released after this build never reads as roomier than it is.</summary>
    public static int ContextWindowFor(AiProviderType provider, string? modelId, uint localContextSize)
    {
        string id = (modelId ?? "").Trim().ToLowerInvariant();
        switch (provider)
        {
            case AiProviderType.Local:
            case AiProviderType.LlamaServer:
            case AiProviderType.Ollama:
                return (int)Math.Max(512u, localContextSize);

            case AiProviderType.Anthropic:
            {
                // Claude Opus/Sonnet 4.6+, Fable/Mythos and Haiku 5.5+: 1M. Haiku 4.5 and older: 200K.
                if (id.EndsWith("[1m]")) return 1_000_000;
                if (id is "opus" or "sonnet" or "fable") return 1_000_000;   // Claude Code aliases: the current models
                if (id == "haiku") return ContextWindowFor(provider, "claude-haiku-5-5", localContextSize);
                var m = Regex.Match(id, @"claude-(opus|sonnet|haiku|fable|mythos)-(\d+)(?:-(\d)(?!\d))?");
                if (!m.Success) return 200_000;
                string family = m.Groups[1].Value;
                int v = int.Parse(m.Groups[2].Value) * 10 + (m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
                bool oneMillion = family is "fable" or "mythos"
                    || (family is "opus" or "sonnet" && v >= 46)
                    || (family == "haiku" && v >= 55);
                return oneMillion ? 1_000_000 : 200_000;
            }

            case AiProviderType.OpenAI:
                if (id.StartsWith("openai/")) id = id["openai/".Length..];
                if (id.StartsWith("gpt-6")) return 1_050_000;   // Astra, 6.1 Sol, 6 Sol, Luna (922K in + 128K out)
                if (id.StartsWith("gpt-4.1")) return 1_047_576;
                if (id.StartsWith("gpt-5")) return 400_000;
                if (id.StartsWith("o1") || id.StartsWith("o3") || id.StartsWith("o4")) return 200_000;
                return 128_000;   // gpt-4o, gpt-4-turbo and anything unrecognised

            case AiProviderType.Gemini:
                return id.StartsWith("gemini-1.5-pro") ? 2_097_152 : 1_048_576;

            case AiProviderType.ClaudeDev:
            {
                // It was a fixed 200K: Opus 5.5 behind "default" read as a fifth of its real window. Now: what the CLI
                // itself reported for its last run (only when "default" is what ran it — an explicit pick is its own
                // model), else the model's published window like the API provider.
                bool byDefault = id is "" or "default" or "auto";
                string? reported = Providers.ClaudeDevProvider.ReportedModel?.Trim().ToLowerInvariant();
                if (Providers.ClaudeDevProvider.ReportedContextWindow is int w && w > 0
                    && (byDefault || (reported != null && reported.StartsWith(id))))
                    return w;
                string model = !byDefault ? id : reported ?? Providers.AnthropicProvider.DefaultModel;
                return ContextWindowFor(AiProviderType.Anthropic, model, localContextSize);
            }

            case AiProviderType.GeminiDev:
                return 1_048_576;   // the Gemini CLI's models (Gemini 3.x / 4) all have a 1M window

            case AiProviderType.AntigravityDev:
                return 1_048_576;   // agy's default is a Gemini 3.x model (1M); its Claude / GPT-OSS slugs are smaller

            case AiProviderType.OpenAIDev:
                // Codex's models are the API's: the same windows; "default" is Codex's own pick (a GPT-6, 1M+).
                return ContextWindowFor(AiProviderType.OpenAI, id is "" or "default" or "auto" ? "gpt-6" : id, localContextSize);

            case AiProviderType.BrainX:
                return 200_000;   // the room's agents run the Claude Code CLI's standard window

            default:
                return 128_000;
        }
    }

    /// <summary>
    /// Get context usage as percentage (0-100).
    /// </summary>
    public double GetContextUsagePercent(IEnumerable<ChatMessage> messages)
    {
        int used = CalculateTotalTokens(messages);
        int max = MaxContextTokens;
        if (max <= 0) return 0;
        return Math.Min(100.0, (double)used / max * 100.0);
    }

    /// <summary>
    /// Get formatted context usage string.
    /// </summary>
    public string GetContextUsageDisplay(IEnumerable<ChatMessage> messages)
    {
        int used = CalculateTotalTokens(messages);
        int max = MaxContextTokens;
        return $"{used:N0} / {max:N0} tokens ({GetContextUsagePercent(messages):F0}%)";
    }

    /// <summary>
    /// Determine if conversation should be summarized to save context.
    /// Returns true when usage exceeds 75%.
    /// </summary>
    public bool ShouldSummarize(IEnumerable<ChatMessage> messages)
    {
        // Respect feature toggle — if ContextMemory is disabled, never auto-summarize
        if (!_settingsService.Settings.Features.ContextMemory) return false;
        return GetContextUsagePercent(messages) > 75.0;
    }

    /// <summary>
    /// Build a compaction prompt for AI-powered summarization.
    /// The AI provider should generate a summary using this prompt.
    /// Returns null if no compaction needed.
    /// </summary>
    public string? BuildCompactPrompt(List<ChatMessage> messages, int keepRecent = 10)
    {
        if (messages.Count <= keepRecent + 1) return null;

        var toSummarize = messages.Take(messages.Count - keepRecent).ToList();
        var sb = new StringBuilder();

        sb.AppendLine("Summarize this conversation for context continuity. Include:");
        sb.AppendLine("1. User's original request and intent");
        sb.AppendLine("2. Key decisions made and approaches taken");
        sb.AppendLine("3. File names, function signatures, and code patterns mentioned");
        sb.AppendLine("4. Errors encountered and how they were resolved");
        sb.AppendLine("5. User feedback and corrections");
        sb.AppendLine("6. Current state of the work (what's done, what's remaining)");
        sb.AppendLine();
        sb.AppendLine("Be concise but preserve ALL technical details (file paths, function names, code snippets).");
        sb.AppendLine("Do NOT lose any file names, error messages, or specific instructions.");
        sb.AppendLine();
        sb.AppendLine("--- CONVERSATION TO SUMMARIZE ---");

        foreach (var msg in toSummarize)
        {
            string role = msg.Role switch
            {
                MessageRole.User => "User",
                MessageRole.Assistant => "Assistant",
                MessageRole.ToolAction => $"Tool({msg.ToolName})",
                MessageRole.System => "System",
                _ => msg.Role.ToString(),
            };

            // Truncate very long tool outputs to save compaction tokens
            string content = msg.Content;
            if (msg.Role == MessageRole.ToolAction && content.Length > 500)
                content = content[..500] + "... [truncated]";
            else if (content.Length > 1500)
                content = content[..1500] + "... [truncated]";

            sb.AppendLine($"[{role}]: {content}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Create a compacted history using an AI-generated summary.
    /// Call BuildCompactPrompt first, send it to the AI, then pass the result here.
    /// </summary>
    public List<ChatMessage> CompactWithSummary(List<ChatMessage> messages, string aiSummary, int keepRecent = 10)
    {
        if (messages.Count <= keepRecent + 1)
            return messages;

        var toKeep = messages.Skip(messages.Count - keepRecent).ToList();

        var summaryMsg = new ChatMessage
        {
            Role = MessageRole.System,
            Content = $"[Conversation Summary — earlier messages compressed]\n{aiSummary}",
        };

        var result = new List<ChatMessage> { summaryMsg };
        result.AddRange(toKeep);
        return result;
    }

    /// <summary>
    /// Fallback: Create a compact summary without AI (simple truncation).
    /// Used when AI summarization is not available.
    /// Keeps the last N messages intact and compresses the rest.
    /// </summary>
    public List<ChatMessage> CompactHistory(List<ChatMessage> messages, int keepRecent = 10)
    {
        if (messages.Count <= keepRecent + 1)
            return messages;

        var toSummarize = messages.Take(messages.Count - keepRecent).ToList();
        var toKeep = messages.Skip(messages.Count - keepRecent).ToList();

        var summaryMsg = new ChatMessage
        {
            Role = MessageRole.System,
            Content = BuildCompactionSummary(toSummarize),
        };

        var result = new List<ChatMessage> { summaryMsg };
        result.AddRange(toKeep);
        return result;
    }

    /// <summary>
    /// A model-facing summary of <paramref name="toSummarize"/> (no AI call): every user request, the latest
    /// tool actions and the gist of the assistant's replies. <paramref name="previousSummary"/> — the summary of
    /// an earlier compaction — is carried forward, so compacting twice never forgets the first stretch.
    /// Used by the agent loop's automatic compaction and by the chat's "Compact now" marker.
    /// </summary>
    public string BuildCompactionSummary(IReadOnlyList<ChatMessage> toSummarize, string? previousSummary = null)
    {
        // Smart summary: extract key info rather than blindly truncating
        var userRequests = new List<string>();
        var toolActions = new List<string>();
        var keyDecisions = new List<string>();

        foreach (var msg in toSummarize)
        {
            switch (msg.Role)
            {
                case MessageRole.User:
                    // Keep full user requests (they're usually short and important)
                    string userContent = msg.Content.Length > 300
                        ? msg.Content[..300] + "..." : msg.Content;
                    userRequests.Add(userContent);
                    break;

                case MessageRole.ToolAction:
                    // Just keep summaries for tool actions
                    toolActions.Add($"{msg.ToolName}: {msg.ToolSummary ?? msg.Content[..Math.Min(100, msg.Content.Length)]}");
                    break;

                case MessageRole.Assistant:
                    // Extract first 200 chars of each assistant response
                    string decision = msg.Content.Length > 200
                        ? msg.Content[..200] + "..." : msg.Content;
                    keyDecisions.Add(decision);
                    break;
            }
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            sb.AppendLine(previousSummary.Trim());
            sb.AppendLine();
        }
        sb.AppendLine($"[Context Summary — {toSummarize.Count} earlier messages compressed]");

        if (userRequests.Count > 0)
        {
            sb.AppendLine("\nUser requests:");
            foreach (var r in userRequests)
                sb.AppendLine($"  - {r}");
        }

        if (toolActions.Count > 0)
        {
            sb.AppendLine($"\nTool actions ({toolActions.Count}):");
            // Keep last 10 tool actions, summarize the rest
            var recent = toolActions.TakeLast(10);
            if (toolActions.Count > 10)
                sb.AppendLine($"  ... ({toolActions.Count - 10} earlier actions omitted)");
            foreach (var t in recent)
                sb.AppendLine($"  - {t}");
        }

        if (keyDecisions.Count > 0)
        {
            sb.AppendLine($"\nKey decisions ({keyDecisions.Count}):");
            foreach (var d in keyDecisions.TakeLast(5))
                sb.AppendLine($"  - {d}");
        }

        return sb.ToString().TrimEnd();
    }
}
