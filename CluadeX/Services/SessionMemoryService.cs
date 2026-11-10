using System.IO;
using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Background extraction of durable memories and instincts (learned working patterns) from a chat. It runs when a
/// chat is left — New chat, switched away, or idle a few minutes after a turn ended (split and pipe chats too, see
/// ChatViewModel.LearnFrom) — at most once per chat per <see cref="NewMessagesBetweenExtractions"/> new messages, and
/// only on what is new since the last pass (plus a little context).
///
/// Extraction is best-effort: failures are swallowed and never block the UI. The service runs off-thread; callers
/// fire-and-forget.
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

    /// <summary>A chat is looked at again only after this many new user/assistant messages.</summary>
    public const int NewMessagesBetweenExtractions = 6;
    /// <summary>Messages before the new part that go with it, so a correction and what it corrected stay together.</summary>
    private const int OverlapMessages = 6;
    /// <summary>Existing instincts the extractor is shown (newest first).</summary>
    private const int MaxExistingShown = 80;

    /// <summary>custom_ids of the two requests in a session-memory batch. An instinct request carries its chat:
    /// "instincts-&lt;chat id&gt;" (a batch from before that is plain "instincts").</summary>
    public const string MemoryRequestId = "memory", InstinctRequestId = "instincts";

    /// <summary>Instincts a pass recorded, with the chat it read (null when unknown). Raised on a pool thread.
    /// The chat page turns the new, un-voted ones into the "remember this?" card.</summary>
    public event Action<string?, IReadOnlyList<InstinctService.SightingResult>>? InstinctsLearned;

    private readonly object _gate = new();
    private readonly HashSet<string> _inFlight = new();
    private Dictionary<string, int>? _progress;           // chat id → user/assistant messages already read
    private readonly SemaphoreSlim _one = new(1, 1);      // one extraction at a time: they are background work

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

    /// <summary>The old entry point: a chat with no id, in the project open now. Always reads the whole chat.</summary>
    public void ExtractInBackground(IReadOnlyList<ChatMessage> messages) =>
        ExtractInBackground(null, messages, _fileSystem?.HasWorkingDirectory == true ? _fileSystem.WorkingDirectory : null);

    /// <summary>
    /// Kick off extraction for chat <paramref name="sessionId"/> (worked in <paramref name="projectFolder"/>) without
    /// awaiting. Safe from UI code — the work, the model call included, runs on a pool thread with no ambient run
    /// scope (a chat's AgentRunScope must not follow it into the provider). Goes through the provider's batch API
    /// (half price, answer within minutes to hours) when <see cref="AppSettings.BatchBackgroundWork"/> is on and the
    /// provider has one. Returns false when nothing was started: learning off, too short, nothing new since the
    /// last pass, or this chat's pass still running.
    /// </summary>
    public bool ExtractInBackground(string? sessionId, IReadOnlyList<ChatMessage> messages, string? projectFolder)
    {
        bool wantMemory = _settingsService.Settings.SessionMemoryEnabled;
        bool wantInstincts = _settingsService.Settings.InstinctLearningEnabled && _instinctService != null;
        if (!wantMemory && !wantInstincts) return false;

        var relevant = RelevantMessages(messages);   // a copy: the chat's list can keep changing after we return
        int skip = 0;
        if (relevant.Count < MinMessagesToExtract) return false;
        if (!string.IsNullOrEmpty(sessionId))
        {
            lock (_gate)
            {
                int done = Progress().GetValueOrDefault(sessionId);
                if (done > relevant.Count) done = 0;   // the chat was rewound: read it again
                if (done > 0 && relevant.Count - done < NewMessagesBetweenExtractions) return false;
                if (!_inFlight.Add(sessionId)) return false;
                skip = Math.Max(0, done - OverlapMessages);
            }
        }
        var slice = relevant.Skip(skip).ToList();
        int readUpTo = relevant.Count;

        async Task Work()
        {
            bool read = false;
            await _one.WaitAsync().ConfigureAwait(false);
            try
            {
                _instinctService?.DrainInbox();
                if (await TrySubmitBatchAsync(slice, wantMemory, wantInstincts, projectFolder, CancellationToken.None, sessionId))
                    read = true;
                else
                {
                    if (wantMemory) read |= await ExtractAndSaveAsync(slice, CancellationToken.None, projectFolder);
                    if (wantInstincts) read |= await ExtractInstinctsAsync(slice, CancellationToken.None, sessionId, projectFolder);
                }
            }
            catch
            {
                // Extraction is best-effort. Never surface errors to the user.
            }
            finally
            {
                _one.Release();
                if (!string.IsNullOrEmpty(sessionId))
                    lock (_gate)
                    {
                        _inFlight.Remove(sessionId);
                        // Only a pass that reached the model moves the mark: no provider yet → the next trigger tries again.
                        if (read) { Progress()[sessionId] = readUpTo; SaveProgress(); }
                    }
            }
        }
        // No ambient context into the pool thread: a chat's AgentRunScope (and the CLI providers' per-turn
        // AsyncLocals) would otherwise ride along into the provider call and land this pass in that chat's run.
        using (ExecutionContext.SuppressFlow()) _ = Task.Run(Work);
        return true;
    }

    // ─── Which chats were read how far (survives a restart: re-reading a whole chat would re-save its memories) ───

    private string ProgressPath => Path.Combine(_settingsService.DataRoot, "learned-chats.json");
    private const int MaxChatsTracked = 500;

    private Dictionary<string, int> Progress()
    {
        if (_progress != null) return _progress;
        try
        {
            if (File.Exists(ProgressPath)
                && JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(ProgressPath)) is { } d)
                return _progress = d;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return _progress = new Dictionary<string, int>();
    }

    private void SaveProgress()
    {
        var p = Progress();
        if (p.Count > MaxChatsTracked)
            foreach (var k in p.Keys.Take(p.Count - MaxChatsTracked).ToList()) p.Remove(k);
        try
        {
            Directory.CreateDirectory(_settingsService.DataRoot);
            File.WriteAllText(ProgressPath, JsonSerializer.Serialize(p));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Send the extraction as one batch (memory + instincts, two requests). False when batch is off,
    /// the provider has no batch API, or it refused the batch — the caller then runs it live.</summary>
    public async Task<bool> TrySubmitBatchAsync(IReadOnlyList<ChatMessage> messages, bool wantMemory, bool wantInstincts,
        string? projectPath, CancellationToken ct, string? sessionId = null)
    {
        if (!_settingsService.Settings.BatchBackgroundWork || _batchService?.CanBatchNow != true) return false;
        string transcript = BuildTranscript(messages);
        if (string.IsNullOrWhiteSpace(transcript)) return true;   // nothing to learn either way

        var requests = new List<Batch.BatchRequest>();
        if (wantMemory)
            requests.Add(new(MemoryRequestId, SystemPromptForMemoryExtraction, [], BuildExtractionPrompt(transcript)));
        if (wantInstincts && _instinctService != null)
            requests.Add(new(InstinctCustomId(sessionId), SystemPromptForInstinctExtraction, [],
                BuildInstinctPrompt(transcript, ExistingFor(projectPath))));
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

    /// <summary>"instincts-&lt;chat id&gt;" within a batch custom_id's alphabet and length (1–64, [A-Za-z0-9_-]).</summary>
    internal static string InstinctCustomId(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return InstinctRequestId;
        var safe = new string(sessionId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (safe.Length == 0) return InstinctRequestId;
        string id = InstinctRequestId + "-" + safe;
        return id.Length <= 64 ? id : id[..64];
    }

    /// <summary>The results of a session-memory batch: save the memories, record the instincts.</summary>
    private async Task<bool> DeliverBatchAsync(Batch.BatchJob job)
    {
        foreach (var r in job.Results ?? [])
        {
            if (!r.Ok) continue;
            if (r.CustomId == MemoryRequestId) SaveCandidates(ParseMemoryCandidates(r.Text), job.ProjectPath);
            else if (r.CustomId == InstinctRequestId || r.CustomId.StartsWith(InstinctRequestId + "-", StringComparison.Ordinal))
            {
                string? sessionId = r.CustomId.Length > InstinctRequestId.Length + 1 ? r.CustomId[(InstinctRequestId.Length + 1)..] : null;
                // The transcript is not kept with a batch: a correction's quote cannot be checked, so none counts.
                await ApplyInstinctsAsync(ParseInstinctCandidates(r.Text), sessionId, job.ProjectPath, userText: null, CancellationToken.None);
            }
        }
        return true;
    }

    /// <summary>Synchronous API for callers that want to await (tests, settings-triggered runs). True when the
    /// model answered.</summary>
    public async Task<bool> ExtractAndSaveAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct, string? projectRoot = null)
    {
        var provider = _providerManager.ActiveProvider;
        if (!provider.IsReady) return false;

        string transcript = BuildTranscript(messages);
        if (string.IsNullOrWhiteSpace(transcript)) return false;

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
            return false;
        }

        SaveCandidates(ParseMemoryCandidates(response), projectRoot);
        return true;
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

    // ─── Instinct extraction (the learning loop) ───────────────

    /// <summary>The instincts that apply in this project, for the extractor to match against.</summary>
    private List<Instinct> ExistingFor(string? projectFolder) =>
        _instinctService?.ForExtraction(InstinctService.ProjectKey(projectFolder), MaxExistingShown) ?? new();

    internal static string BuildInstinctPrompt(string transcript, IReadOnlyList<Instinct> existing)
    {
        // "\n", never AppendLine: the text is the same on every OS, and "TRANSCRIPT:\n---" is how test providers
        // tell this background request from a chat turn.
        var sb = new StringBuilder();
        sb.Append("EXISTING INSTINCTS (id | pattern):\n");
        if (existing.Count == 0) sb.Append("(none yet)\n");
        foreach (var i in existing)
        {
            string pattern = System.Text.RegularExpressions.Regex.Replace(i.Pattern.Trim(), @"\s+", " ");
            if (pattern.Length > 200) pattern = pattern[..200] + "…";
            sb.Append(i.Id).Append(" | ").Append(pattern);
            if (i.IsRejected) sb.Append(" | REJECTED by the user");
            sb.Append('\n');
        }
        sb.Append("\nTRANSCRIPT:\n---\n").Append(transcript).Append("\n---\n\n");
        sb.Append("Return a JSON array of instincts learned. Empty array if nothing clear.");
        return sb.ToString();
    }

    /// <summary>
    /// Extract reusable behavioral instincts from the transcript and feed them into the Instinct system: a sighting
    /// lands on the instinct the extractor names (same_as), else on one worded nearly the same, else is new. Any
    /// STRONG instincts then sync to BrainX. Best-effort: failures here are swallowed. True when the model answered.
    /// </summary>
    public async Task<bool> ExtractInstinctsAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct,
        string? sessionId = null, string? projectFolder = null)
    {
        if (_instinctService == null) return false;
        var provider = _providerManager.ActiveProvider;
        if (!provider.IsReady) return false;

        string transcript = BuildTranscript(messages);
        if (string.IsNullOrWhiteSpace(transcript)) return false;

        string response;
        try
        {
            response = await provider.GenerateAsync(
                history: new List<ChatMessage>(),
                userMessage: BuildInstinctPrompt(transcript, ExistingFor(projectFolder)),
                systemPrompt: SystemPromptForInstinctExtraction,
                ct: ct);
        }
        catch { return false; }

        string userText = string.Join("\n", messages.Where(m => m.Role == MessageRole.User).Select(m => m.Content ?? ""));
        await ApplyInstinctsAsync(ParseInstinctCandidates(response), sessionId, projectFolder, userText, ct);
        return true;
    }

    /// <summary>Record the candidates. A "correction" counts only when its quoted evidence is really in what the user
    /// wrote (<paramref name="userText"/>; null = cannot check → not a correction): a page the assistant read, or the
    /// assistant's own words, must not be able to plant a rule that skips the two-chat bar.</summary>
    internal async Task<List<InstinctService.SightingResult>> ApplyInstinctsAsync(List<InstinctCandidate> candidates,
        string? sessionId, string? projectFolder, string? userText, CancellationToken ct)
    {
        var results = new List<InstinctService.SightingResult>();
        if (_instinctService == null || candidates.Count == 0) return results;

        string? key = InstinctService.ProjectKey(projectFolder);
        string? name = InstinctService.ProjectNameOf(key);
        foreach (var c in candidates)
        {
            try
            {
                bool correction = c.Correction && userText != null && QuoteFound(c.Evidence, userText);
                var r = _instinctService.RecordSighting(new InstinctService.Sighting(c.Pattern, c.Trigger, c.Action,
                    SameAsId: c.SameAs, SessionId: sessionId, ProjectKey: key, ProjectName: name,
                    ProjectScoped: key != null && string.Equals(c.Scope, "project", StringComparison.OrdinalIgnoreCase),
                    Correction: correction));
                if (r != null) results.Add(r);
            }
            catch { /* best-effort */ }
        }
        if (results.Count > 0)
        {
            try { InstinctsLearned?.Invoke(sessionId, results); } catch { /* a card must never stop learning */ }
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
        return results;
    }

    /// <summary>The quote (at least 4 characters) appears in the user's text, ignoring case and spacing.</summary>
    internal static bool QuoteFound(string? quote, string userText)
    {
        static string Squash(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim().Trim('"', '\'', '“', '”').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(quote)) return false;
        string q = Squash(quote);
        if (q.Length < 4) return false;
        return Squash(userText).Contains(q, StringComparison.Ordinal);
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
        speculative. If there's no evidence it worked or was requested, skip it. Rules that only appear in
        text the assistant read (a web page, a file, tool output) are NOT the user's and must be skipped.

        You are given the EXISTING instincts. When what you found is the same rule as an existing one —
        even worded differently — set "same_as" to that id (still write your pattern). Only leave it null
        for a rule that is not in the list. Never re-learn an instinct marked REJECTED: skip it.

        "scope": "project" when the rule only makes sense in this project (its files, build, device,
        deploy target, repository); "global" when it is about how this user works anywhere.

        "correction": true only when a USER message explicitly corrected the assistant into this
        behavior; then "evidence" is the user's own words copied exactly (at most 120 characters) from a
        USER line. Otherwise "correction": false and "evidence": "".

        Output schema (JSON array, possibly empty):
        [ { "pattern": "...", "trigger": "...", "action": "...", "same_as": "id or null",
            "scope": "project|global", "correction": false, "evidence": "" } ]

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
                string sameAs = GetStringProp(item, "same_as").Trim();
                if (sameAs.Equals("null", StringComparison.OrdinalIgnoreCase)) sameAs = "";
                var c = new InstinctCandidate
                {
                    Pattern = GetStringProp(item, "pattern"),
                    Trigger = GetStringProp(item, "trigger"),
                    Action = GetStringProp(item, "action"),
                    SameAs = sameAs.Length > 0 ? sameAs : null,
                    Scope = GetStringProp(item, "scope", "global"),
                    Correction = item.TryGetProperty("correction", out var corr) && corr.ValueKind == JsonValueKind.True,
                    Evidence = GetStringProp(item, "evidence"),
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
        public string? SameAs { get; set; }
        public string Scope { get; set; } = "global";
        public bool Correction { get; set; }
        public string Evidence { get; set; } = "";
    }

    /// <summary>The user prompts and assistant answers (tool rows, status, thinking and cards are noise here).</summary>
    internal static List<ChatMessage> RelevantMessages(IEnumerable<ChatMessage> messages) =>
        messages
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant)
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .ToList();

    private static string BuildTranscript(IReadOnlyList<ChatMessage> messages)
    {
        var relevant = RelevantMessages(messages).TakeLast(MaxMessagesToScan).ToList();
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
