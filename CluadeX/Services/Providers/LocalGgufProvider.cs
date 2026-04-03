using System.Runtime.CompilerServices;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

public class LocalGgufProvider : IAiProvider
{
    private readonly LlamaInferenceService _llamaService;

    // Store event handlers as fields for proper unsubscription
    private readonly Action<string> _statusHandler;
    private readonly Action<bool> _loadingHandler;
    private readonly Action<string> _errorHandler;

    public string ProviderId => "Local";
    public string DisplayName => "Local GGUF";
    public bool IsReady => _llamaService.IsModelLoaded;
    public bool IsLoading => _llamaService.IsLoading;
    public string StatusMessage => _llamaService.IsModelLoaded
        ? $"Model: {_llamaService.LoadedModelName}"
        : _llamaService.IsLoading ? "Loading model..." : "No model loaded";

    public event Action<string>? OnStatusChanged;
    public event Action<bool>? OnLoadingChanged;
    public event Action<string>? OnError;

    public LocalGgufProvider(LlamaInferenceService llamaService)
    {
        _llamaService = llamaService;
        _statusHandler = s => OnStatusChanged?.Invoke(s);
        _loadingHandler = b => OnLoadingChanged?.Invoke(b);
        _errorHandler = e => OnError?.Invoke(e);
        _llamaService.OnStatusChanged += _statusHandler;
        _llamaService.OnLoadingChanged += _loadingHandler;
        _llamaService.OnError += _errorHandler;
    }

    public Task InitializeAsync(CancellationToken ct = default)
    {
        // Local model loading is managed from the Models tab — no-op here
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default)
        => _llamaService.ChatAsync(history, userMessage, systemPrompt, ct);

    public Task<string> GenerateAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default)
        => _llamaService.GenerateAsync(history, userMessage, systemPrompt, ct);

    public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
        => Task.FromResult(_llamaService.IsModelLoaded
            ? (true, $"Model loaded: {_llamaService.LoadedModelName}")
            : (false, "No model loaded. Go to Models tab to load a GGUF model."));

    public void Dispose()
    {
        _llamaService.OnStatusChanged -= _statusHandler;
        _llamaService.OnLoadingChanged -= _loadingHandler;
        _llamaService.OnError -= _errorHandler;
    }
}
