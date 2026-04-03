using CluadeX.Models;

namespace CluadeX.Services.Providers;

public interface IAiProvider : IDisposable
{
    string ProviderId { get; }
    string DisplayName { get; }
    bool IsReady { get; }
    bool IsLoading { get; }
    string StatusMessage { get; }

    event Action<string>? OnStatusChanged;
    event Action<bool>? OnLoadingChanged;
    event Action<string>? OnError;

    Task InitializeAsync(CancellationToken ct = default);

    IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default);

    Task<string> GenerateAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default);

    Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default);
}
