using System.Diagnostics;
using System.Text;
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

    /// <summary>Maximum context tokens from settings.</summary>
    public int MaxContextTokens => (int)_settingsService.Settings.ContextSize;

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
    /// Get process memory usage display string.
    /// </summary>
    public string GetMemoryUsageDisplay()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            long workingSet = process.WorkingSet64;
            return $"RAM: {FormatBytes(workingSet)}";
        }
        catch
        {
            return "RAM: --";
        }
    }

    /// <summary>Get working set in MB.</summary>
    public double GetWorkingSetMB()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.WorkingSet64 / (1024.0 * 1024.0);
        }
        catch { return 0; }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F0} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
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

        var summaryMsg = new ChatMessage
        {
            Role = MessageRole.System,
            Content = sb.ToString(),
        };

        var result = new List<ChatMessage> { summaryMsg };
        result.AddRange(toKeep);
        return result;
    }
}
