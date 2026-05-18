using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Background extraction of durable memories from a chat session. After a session ends
/// (user clicks "New Chat" or closes the app), we scan the transcript for facts worth
/// keeping — user preferences, project decisions, references to external resources —
/// and persist them via MemoryService so the next session starts with context.
///
/// Extraction is best-effort: failures are swallowed and never block the UI. The
/// service runs off-thread; callers fire-and-forget.
/// </summary>
public class SessionMemoryService
{
    private readonly MemoryService _memoryService;
    private readonly AiProviderManager _providerManager;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;

    // Keep extraction cheap — if a session is too short there's nothing to learn,
    // and too long would blow past typical context windows for a background pass.
    private const int MinMessagesToExtract = 6;
    private const int MaxMessagesToScan = 60;
    private const int MaxTranscriptChars = 60_000;

    public SessionMemoryService(
        MemoryService memoryService,
        AiProviderManager providerManager,
        SettingsService settingsService,
        LocalizationService localizationService)
    {
        _memoryService = memoryService;
        _providerManager = providerManager;
        _settingsService = settingsService;
        _localizationService = localizationService;
    }

    /// <summary>
    /// Kick off extraction for a finished session without awaiting. Safe to call from
    /// UI code — all work (including the LLM round-trip) runs on a background thread.
    /// </summary>
    public void ExtractInBackground(IReadOnlyList<ChatMessage> messages)
    {
        if (!_settingsService.Settings.SessionMemoryEnabled) return;
        if (messages.Count < MinMessagesToExtract) return;

        // Copy defensively — the session's message list can keep mutating after we return.
        var snapshot = messages.ToList();

        _ = Task.Run(async () =>
        {
            try
            {
                await ExtractAndSaveAsync(snapshot, CancellationToken.None);
            }
            catch
            {
                // Memory extraction is best-effort. Never surface errors to the user.
            }
        });
    }

    /// <summary>Synchronous API for callers that want to await (tests, settings-triggered runs).</summary>
    public async Task ExtractAndSaveAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var provider = _providerManager.ActiveProvider;
        if (!provider.IsReady) return;

        string transcript = BuildTranscript(messages);
        if (string.IsNullOrWhiteSpace(transcript)) return;

        string prompt = BuildExtractionPrompt(transcript);
        string response;
        try
        {
            response = await provider.GenerateAsync(
                history: new List<ChatMessage>(),
                userMessage: prompt,
                systemPrompt: SystemPromptForMemoryExtraction,
                ct: ct);
        }
        catch
        {
            return;
        }

        var candidates = ParseMemoryCandidates(response);
        if (candidates.Count == 0) return;

        // Deduplicate against existing memories by name (case-insensitive)
        var existing = _memoryService.ListAllMemories()
            .Select(m => m.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (existing.Contains(candidate.Name)) continue;
            try
            {
                _memoryService.SaveMemory(
                    name: candidate.Name,
                    type: candidate.Type,
                    description: candidate.Description,
                    content: candidate.Content,
                    isProjectScope: candidate.Scope == "project");
            }
            catch { /* best-effort */ }
        }
    }

    private static string BuildTranscript(IReadOnlyList<ChatMessage> messages)
    {
        // Skip tool actions / agent status / thinking — they're noise for memory extraction.
        // Only keep user prompts and assistant text answers.
        var relevant = messages
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant)
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .TakeLast(MaxMessagesToScan)
            .ToList();

        if (relevant.Count < MinMessagesToExtract) return string.Empty;

        var sb = new StringBuilder();
        foreach (var msg in relevant)
        {
            sb.Append(msg.Role == MessageRole.User ? "USER: " : "ASSISTANT: ");
            sb.AppendLine(msg.Content.Trim());
            sb.AppendLine();
            if (sb.Length >= MaxTranscriptChars) break;
        }

        string transcript = sb.ToString();
        if (transcript.Length > MaxTranscriptChars)
            transcript = transcript[..MaxTranscriptChars] + "\n...(truncated)";
        return transcript;
    }

    private const string SystemPromptForMemoryExtraction = """
        You extract durable, non-obvious facts from a chat transcript between a user and
        an AI coding assistant. Return JSON only — no prose, no code fences.

        Save ONLY:
          - user     : stable facts about the user (role, preferences, expertise, tools they use)
          - feedback : guidance on how to work with them (corrections they've given, approaches they validated — include *why*)
          - project  : ongoing work context (deadlines, decisions, who-does-what, constraints). Convert relative dates to absolute.
          - reference: pointers to external systems (URLs, Slack channels, dashboards) and what they're used for

        DO NOT save:
          - code patterns, conventions, file paths, project structure (derivable from the codebase)
          - git history, recent changes
          - debugging solutions or fix recipes
          - ephemeral task state or conversation context
          - anything already obvious from reading the repo

        Output schema (a JSON array, possibly empty):
        [
          {
            "name": "short_snake_case_identifier",
            "type": "user|feedback|project|reference",
            "description": "one concise line — the hook used to decide relevance later",
            "content": "the memory body. For feedback/project, include a **Why:** line and a **How to apply:** line.",
            "scope": "global|project"
          }
        ]

        If nothing meets the bar, return []. Prefer precision over recall — a wrong memory
        is worse than a missing one.
        """;

    private static string BuildExtractionPrompt(string transcript)
    {
        return $"""
            TRANSCRIPT:
            ---
            {transcript}
            ---

            Return a JSON array of memories to save. Empty array if nothing durable.
            """;
    }

    /// <summary>Extract the first JSON array from the model response, tolerant of surrounding prose.</summary>
    internal static List<MemoryCandidate> ParseMemoryCandidates(string response)
    {
        var result = new List<MemoryCandidate>();
        if (string.IsNullOrWhiteSpace(response)) return result;

        // Find the first '[' and matching ']' — models sometimes wrap output in prose or fences.
        int start = response.IndexOf('[');
        int end = response.LastIndexOf(']');
        if (start < 0 || end <= start) return result;

        string jsonSlice = response[start..(end + 1)];

        try
        {
            using var doc = JsonDocument.Parse(jsonSlice);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var candidate = new MemoryCandidate
                {
                    Name = GetStringProp(item, "name"),
                    Type = GetStringProp(item, "type"),
                    Description = GetStringProp(item, "description"),
                    Content = GetStringProp(item, "content"),
                    Scope = GetStringProp(item, "scope", "global"),
                };

                if (!IsValidCandidate(candidate)) continue;
                result.Add(candidate);
            }
        }
        catch
        {
            // Malformed JSON — return whatever we parsed so far (likely empty).
        }

        return result;
    }

    private static bool IsValidCandidate(MemoryCandidate c)
    {
        if (string.IsNullOrWhiteSpace(c.Name)) return false;
        if (string.IsNullOrWhiteSpace(c.Content)) return false;
        if (c.Type is not ("user" or "feedback" or "project" or "reference")) return false;
        return true;
    }

    private static string GetStringProp(JsonElement obj, string name, string fallback = "")
    {
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString() ?? fallback;
        return fallback;
    }

    internal class MemoryCandidate
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Description { get; set; } = "";
        public string Content { get; set; } = "";
        public string Scope { get; set; } = "global";
    }
}
