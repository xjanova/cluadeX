using System.Diagnostics;
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
    /// Estimate token count for a string.
    /// Uses ~3.5 chars per token heuristic (similar to GPT tokenization).
    /// </summary>
    public int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(text.Length / 3.5);
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
        return GetContextUsagePercent(messages) > 75.0;
    }

    /// <summary>
    /// Create a compact summary of older messages to free context space.
    /// Keeps the last N messages intact and compresses the rest.
    /// </summary>
    public List<ChatMessage> CompactHistory(List<ChatMessage> messages, int keepRecent = 6)
    {
        if (messages.Count <= keepRecent + 1)
            return messages;

        var toSummarize = messages.Take(messages.Count - keepRecent).ToList();
        var toKeep = messages.Skip(messages.Count - keepRecent).ToList();

        var summaryParts = new List<string>();
        foreach (var msg in toSummarize)
        {
            string role = msg.Role.ToString();
            string content = msg.Content.Length > 200
                ? msg.Content[..200] + "..."
                : msg.Content;
            summaryParts.Add($"[{role}]: {content}");
        }

        var summaryMsg = new ChatMessage
        {
            Role = MessageRole.System,
            Content = $"[Context Summary - {toSummarize.Count} earlier messages compressed]\n" +
                      string.Join("\n", summaryParts),
        };

        var result = new List<ChatMessage> { summaryMsg };
        result.AddRange(toKeep);
        return result;
    }
}
