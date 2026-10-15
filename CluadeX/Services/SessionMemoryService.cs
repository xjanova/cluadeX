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
    private readonly InstinctService? _instinctService;
    private readonly BrainSyncService? _brainSync;
    private readonly Batch.BatchService? _batchService;
    private readonly FileSystemService? _fileSystem;

    // Keep extraction cheap — if a session is too short there's nothing to learn,
    // and too long would blow past typical context windows for a background pass.
    private const int MinMessagesToExtract = 6;
    private const int MaxMessagesToScan = 60;
    private const int MaxTranscriptChars = 60_000;

    /// <summary>custom_ids of the two requests in a session-memory batch.</summary>
    public const string MemoryRequestId = "memory", InstinctRequestId = "instincts";

    public SessionMemoryService(
        MemoryService memoryService,
        AiProviderManager providerManager,
        SettingsService settingsService,
        LocalizationService localizationService,
        InstinctService? instinctService = null,
        BrainSyncService? brainSync = null,
        Batch.BatchService? batchService = null,
        FileSystemService? fileSystem = null)
    {
        _memoryService = memoryService;
        _providerManager = providerManager;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _instinctService = instinctService;
        _brainSync = brainSync;
        _batchService = batchService;
        _fileSystem = fileSystem;
        _batchService?.RegisterHandler(Batch.BatchKinds.SessionMemory, DeliverBatchAsync);
    }

    /// <summary>
    /// Kick off extraction for a finished session without awaiting. Safe to call from
    /// UI code — all work (including the LLM round-trip) runs on a background thread.
    /// Goes through the provider's batch API (half price, answer within minutes to hours) when
    /// <see cref="AppSettings.BatchBackgroundWork"/> is on and the provider has one.
    /// </summary>
    public void ExtractInBackground(IReadOnlyList<ChatMessage> messages)
    {
        bool wantMemory = _settingsService.Settings.SessionMemoryEnabled;
        bool wantInstincts = _settingsService.Settings.InstinctLearningEnabled && _instinctService != null;
        if (!wantMemory && !wantInstincts) return;
        if (messages.Count < MinMessagesToExtract) return;

        // Copy defensively — the session's message list can keep mutating after we return.
        var snapshot = messages.ToList();
        // The project the chat was in: a batch answer can arrive after the user moved on to another one.
        string? project = _fileSystem?.HasWorkingDirectory == true ? _fileSystem.WorkingDirectory : null;

        _ = Task.Run(async () =>
        {
            try
            {
                if (await TrySubmitBatchAsync(snapshot, wantMemory, wantInstincts, project, CancellationToken.None)) return;
                if (wantMemory) await ExtractAndSaveAsync(snapshot, CancellationToken.None);
                if (wantInstincts) await ExtractInstinctsAsync(snapshot, CancellationToken.None);
            }
            catch
            {
                // Extraction is best-effort. Never surface errors to the user.
            }
        });
    }

    /// <summary>Send the extraction as one batch (memory + instincts, two requests). False when batch is off,
    /// the provider has no batch API, or it refused the batch — the caller then runs it live.</summary>
    public async Task<bool> TrySubmitBatchAsync(IReadOnlyList<ChatMessage> messages, bool wantMemory, bool wantInstincts,
        string? projectPath, CancellationToken ct)
    {
        if (!_settingsService.Settings.BatchBackgroundWork || _batchService?.CanBatchNow != true) return false;
        string transcript = BuildTranscript(messages);
        if (string.IsNullOrWhiteSpace(transcript)) return true;   // nothing to learn either way

        var requests = new List<Batch.BatchRequest>();
        if (wantMemory)
            requests.Add(new(MemoryRequestId, SystemPromptForMemoryExtraction, [], BuildExtractionPrompt(transcript)));
        if (wantInstincts && _instinctService != null)
            requests.Add(new(InstinctRequestId, SystemPromptForInstinctExtraction, [], BuildInstinctPrompt(transcript)));
        if (requests.Count == 0) return true;
        try
        {
            string title = _localizationService.CurrentLanguage == "th" ? "สกัดความจำจากแชท" : "Session memory extraction";
            await _batchService.SubmitAsync(Batch.BatchKinds.SessionMemory, title, requests, projectPath: projectPath, ct: ct);
            return true;
        }
        catch (Batch.BatchException) { return false; }
        catch (System.Net.Http.HttpRequestException) { return false; }
    }

    /// <summary>The results of a session-memory batch: save the memories, record the instincts.</summary>
    private async Task<bool> DeliverBatchAsync(Batch.BatchJob job)
    {
        foreach (var r in job.Results ?? [])
        {
            if (!r.Ok) continue;
            if (r.CustomId == MemoryRequestId) SaveCandidates(ParseMemoryCandidates(r.Text), job.ProjectPath);
            else if (r.CustomId == InstinctRequestId) await ApplyInstinctsAsync(ParseInstinctCandidates(r.Text), CancellationToken.None);
        }
        return true;
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

        SaveCandidates(ParseMemoryCandidates(response), projectRoot: null);
    }

    private void SaveCandidates(List<MemoryCandidate> candidates, string? projectRoot)
    {
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
                    isProjectScope: candidate.Scope == "project",
                    projectRoot: projectRoot);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── Instinct extraction (Wave 5 — the learning loop) ───────────────

    private static string BuildInstinctPrompt(string transcript) =>
        $"TRANSCRIPT:\n---\n{transcript}\n---\n\nReturn a JSON array of instincts learned. Empty array if nothing clear.";

    /// <summary>
    /// Extract reusable behavioral instincts from the transcript and feed them into the Instinct
    /// system (create-or-bump). Any STRONG instincts then sync to BrainX so the lesson survives across
    /// machines. Best-effort: the caller gates this; failures here are swallowed.
    /// </summary>
    public async Task ExtractInstinctsAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        if (_instinctService == null) return;
        var provider = _providerManager.ActiveProvider;
        if (!provider.IsReady) return;

        string transcript = BuildTranscript(messages);
        if (string.IsNullOrWhiteSpace(transcript)) return;

        string response;
        try
        {
            response = await provider.GenerateAsync(
                history: new List<ChatMessage>(),
                userMessage: BuildInstinctPrompt(transcript),
                systemPrompt: SystemPromptForInstinctExtraction,
                ct: ct);
        }
        catch { return; }

        await ApplyInstinctsAsync(ParseInstinctCandidates(response), ct);
    }

    private async Task ApplyInstinctsAsync(List<InstinctCandidate> candidates, CancellationToken ct)
    {
        if (_instinctService == null || candidates.Count == 0) return;

        foreach (var c in candidates)
        {
            try { _instinctService.RecordObservation(c.Pattern, c.Trigger, c.Action); }
            catch { /* best-effort */ }
        }

        // Push any now-STRONG, unsynced instincts to BrainX so the lesson survives across machines.
        if (_brainSync != null)
        {
            try
            {
                await _brainSync.SyncAllStrongAsync(_instinctService.GetAll(), ct);
                // Persist the BrainNoteId that sync assigned in-memory — otherwise a restart re-creates
                // duplicate brain notes (sync sets the id on the cached object but doesn't write the store).
                foreach (var inst in _instinctService.GetAll())
                    if (inst.SyncedToBrain) _instinctService.Persist(inst);
            }
            catch { /* brain offline — fine */ }
        }
    }

    private const string SystemPromptForInstinctExtraction = """
        You extract behavioral INSTINCTS from a transcript between a user and an AI coding assistant —
        reusable working patterns that should guide FUTURE sessions. Return JSON only, no prose/fences.

        An instinct captures HOW to work well with this user/codebase, learned from what happened:
          - pattern: a short imperative rule (the lesson). e.g. "Build before claiming a fix is done"
          - trigger: WHEN it applies.  e.g. "After editing C# in this WPF project"
          - action:  WHAT to do.       e.g. "dotnet build the csproj and check for CS errors"

        Extract ONLY instincts with clear evidence in THIS transcript:
          - the user corrected the assistant's approach (encode the corrected behavior)
          - an approach clearly worked and should be repeated
          - a workflow / convention the user expects

        DO NOT extract: one-off task facts, file contents, generic programming advice, or anything
        speculative. If there's no evidence it worked or was requested, skip it.

        Output schema (JSON array, possibly empty):
        [ { "pattern": "...", "trigger": "...", "action": "..." } ]

        Prefer precision. 0-3 instincts per session is normal. Return [] if nothing was clearly learned.
        """;

    /// <summary>Parse the first JSON array of instincts from the model response, tolerant of prose.</summary>
    internal static List<InstinctCandidate> ParseInstinctCandidates(string response)
    {
        var result = new List<InstinctCandidate>();
        if (string.IsNullOrWhiteSpace(response)) return result;
        int start = response.IndexOf('[');
        int end = response.LastIndexOf(']');
        if (start < 0 || end <= start) return result;
        try
        {
            using var doc = JsonDocument.Parse(response[start..(end + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var c = new InstinctCandidate
                {
                    Pattern = GetStringProp(item, "pattern"),
                    Trigger = GetStringProp(item, "trigger"),
                    Action = GetStringProp(item, "action"),
                };
                if (string.IsNullOrWhiteSpace(c.Pattern)) continue;
                result.Add(c);
            }
        }
        catch { /* malformed — return what we have */ }
        return result;
    }

    internal class InstinctCandidate
    {
        public string Pattern { get; set; } = "";
        public string Trigger { get; set; } = "";
        public string Action { get; set; } = "";
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
