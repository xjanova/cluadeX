using CluadeX.Models;

namespace CluadeX.Services.Batch;

/// <summary>
/// A provider that can run requests through its batch API: about half the price of the same request sent live, in
/// exchange for an answer that comes back later (usually minutes, at most 24 hours). Single-shot only — a batch item
/// cannot call tools and wait for their results, so the agent loop never goes through here; work nobody is waiting
/// on does (session-memory extraction, a chat question sent with "batch").
/// </summary>
public interface IBatchProvider
{
    /// <summary>This provider, as configured right now (key, endpoint, model), accepts batch jobs.</summary>
    bool SupportsBatch { get; }

    /// <summary>The model a submitted batch runs on (the provider's configured model).</summary>
    string BatchModel { get; }

    /// <summary>Submit the requests as one batch; returns the provider's batch id.</summary>
    Task<string> SubmitBatchAsync(IReadOnlyList<BatchRequest> requests, CancellationToken ct = default);

    Task<BatchStatus> GetBatchStatusAsync(string batchId, CancellationToken ct = default);

    /// <summary>One result per request that finished (in any way); call once the status is ended.</summary>
    Task<IReadOnlyList<BatchResult>> GetBatchResultsAsync(string batchId, CancellationToken ct = default);

    Task CancelBatchAsync(string batchId, CancellationToken ct = default);
}

/// <summary>One single-shot request: an optional system prompt, the earlier turns, the new user message.</summary>
public sealed record BatchRequest(string CustomId, string? SystemPrompt, IReadOnlyList<ChatMessage> History, string UserMessage);

public enum BatchState { InProgress, Ended, Failed, Expired, Canceled }

/// <param name="Detail">The provider's own status word, for the jobs list ("validating", "finalizing" …).</param>
public sealed record BatchStatus(BatchState State, int Succeeded, int Errored, int Pending, string Detail);

/// <param name="Ok">The request produced an answer (Text). Otherwise Error says why (errored, expired, canceled).</param>
public sealed record BatchResult(string CustomId, bool Ok, string Text, string? Error, int InputTokens, int OutputTokens);

/// <summary>Thrown when a batch call is refused; the message is user-facing (key redacted).</summary>
public sealed class BatchException(string message) : Exception(message);
