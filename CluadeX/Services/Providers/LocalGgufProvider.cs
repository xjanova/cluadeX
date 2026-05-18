using System.Runtime.CompilerServices;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

/// <summary>
/// "Local GGUF" provider that transparently routes between two backends:
///   • LLamaSharp (in-process, fast) — for models the bundled llama.cpp recognizes.
///   • llama-server.exe (out-of-process, llama-backend/) — for newer architectures
///     like Gemma 3/4, Llama 4, Qwen 3 that LLamaSharp's pinned llama.cpp doesn't
///     handle. The fallback is invisible to the user — they pick "Local GGUF" and
///     load whatever model they have; we figure out which backend can run it.
/// </summary>
public class LocalGgufProvider : IAiProvider
{
    private readonly LlamaInferenceService _llamaService;
    private readonly LlamaServerProvider _serverProvider;

    private enum Backend { None, LlamaSharp, LlamaServer }
    private Backend _activeBackend = Backend.None;
    private string? _loadedModelPath;

    // Store event handlers as fields for proper unsubscription
    private readonly Action<string> _statusHandler;
    private readonly Action<bool> _loadingHandler;
    private readonly Action<string> _errorHandler;
    private readonly Action<string> _serverStatusHandler;
    private readonly Action<bool> _serverLoadingHandler;
    private readonly Action<string> _serverErrorHandler;

    public string ProviderId => "Local";
    public string DisplayName => "Local GGUF";
    public bool IsReady => _activeBackend switch
    {
        Backend.LlamaSharp => _llamaService.IsModelLoaded,
        Backend.LlamaServer => _serverProvider.IsServerRunning && _serverProvider.IsReady,
        _ => false,
    };
    public bool IsLoading => _llamaService.IsLoading || _serverProvider.IsLoading;
    public string StatusMessage => _activeBackend switch
    {
        Backend.LlamaSharp => _llamaService.IsModelLoaded
            ? $"Model: {_llamaService.LoadedModelName}"
            : _llamaService.IsLoading ? "Loading model..." : "No model loaded",
        Backend.LlamaServer => _serverProvider.StatusMessage,
        _ => _llamaService.IsLoading ? "Loading model..." : "No model loaded",
    };

    public event Action<string>? OnStatusChanged;
    public event Action<bool>? OnLoadingChanged;
    public event Action<string>? OnError;

    public LocalGgufProvider(LlamaInferenceService llamaService, LlamaServerProvider serverProvider)
    {
        _llamaService = llamaService;
        _serverProvider = serverProvider;

        _statusHandler = s => { if (_activeBackend == Backend.LlamaSharp) OnStatusChanged?.Invoke(s); };
        _loadingHandler = b => { if (_activeBackend == Backend.LlamaSharp) OnLoadingChanged?.Invoke(b); };
        _errorHandler = e => { if (_activeBackend == Backend.LlamaSharp) OnError?.Invoke(e); };

        _serverStatusHandler = s => { if (_activeBackend == Backend.LlamaServer) OnStatusChanged?.Invoke(s); };
        _serverLoadingHandler = b => { if (_activeBackend == Backend.LlamaServer) OnLoadingChanged?.Invoke(b); };
        _serverErrorHandler = e => { if (_activeBackend == Backend.LlamaServer) OnError?.Invoke(e); };

        _llamaService.OnStatusChanged += _statusHandler;
        _llamaService.OnLoadingChanged += _loadingHandler;
        _llamaService.OnError += _errorHandler;

        _serverProvider.OnStatusChanged += _serverStatusHandler;
        _serverProvider.OnLoadingChanged += _serverLoadingHandler;
        _serverProvider.OnError += _serverErrorHandler;
    }

    public Task InitializeAsync(CancellationToken ct = default)
    {
        // Local model loading is managed from the Models tab — no-op here
        return Task.CompletedTask;
    }

    /// <summary>
    /// Load a GGUF model. Picks the right backend based on the model's architecture:
    /// known-supported archs (llama, qwen2, gemma2, mistral, phi3 etc.) go to LLamaSharp;
    /// newer architectures route to llama-server.exe.
    /// </summary>
    public async Task LoadModelAsync(string modelPath, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // Pre-inspect the GGUF so we can pick the backend BEFORE doing any expensive work
        // (memory mapping, GPU upload, etc.). If inspection returns null we assume the file
        // is loadable by LLamaSharp and let it fail naturally.
        var (needsFallback, arch) = GgufMetadataReader.InspectModel(modelPath);

        if (needsFallback)
        {
            progress?.Report($"Detected '{arch}' architecture — using llama-server backend.");
            await SwitchToServerBackendAsync(modelPath, progress, ct);
            return;
        }

        // Try LLamaSharp first. If it throws our typed "unsupported architecture" exception
        // (the inspector missed something), fall back to llama-server transparently.
        // For other failures we propagate — the user needs to see the real error.
        try
        {
            await UnloadInternalAsync(keepBackend: Backend.LlamaSharp);

            // Route events to the LlamaSharp backend BEFORE loading so progress/loading/
            // status updates during LoadModelAsync actually propagate to the UI. If we set
            // the flag only after load, IsReady and status messages fired during load get
            // filtered out and the status bar stays stuck on "No model loaded".
            _activeBackend = Backend.LlamaSharp;
            _loadedModelPath = modelPath;
            try
            {
                await _llamaService.LoadModelAsync(modelPath, progress, ct);
            }
            catch
            {
                _activeBackend = Backend.None;
                _loadedModelPath = null;
                throw;
            }
        }
        catch (UnsupportedModelArchitectureException ex)
        {
            progress?.Report($"LLamaSharp can't handle '{ex.Architecture}'. Falling back to llama-server...");
            await SwitchToServerBackendAsync(modelPath, progress, ct);
        }
    }

    private async Task SwitchToServerBackendAsync(string modelPath, IProgress<string>? progress, CancellationToken ct)
    {
        await UnloadInternalAsync(keepBackend: Backend.LlamaServer);

        // Flip to LlamaServer BEFORE launching. Events (status/loading) fired by
        // LlamaServerProvider during startup are gated by _activeBackend — if we wait until
        // after LoadModelAsync returns, "Starting llama-server...", "Waiting for model...",
        // and the final "Ready:" status all get dropped and MainViewModel never learns the
        // model is loaded.
        _activeBackend = Backend.LlamaServer;
        _loadedModelPath = modelPath;
        try
        {
            await _serverProvider.LoadModelAsync(modelPath, progress, ct);
        }
        catch
        {
            _activeBackend = Backend.None;
            _loadedModelPath = null;
            throw;
        }
    }

    /// <summary>Unload whichever backend is currently active. Pass <paramref name="keepBackend"/> to skip unloading one.</summary>
    private async Task UnloadInternalAsync(Backend keepBackend = Backend.None)
    {
        if (_activeBackend == Backend.LlamaSharp && keepBackend != Backend.LlamaSharp)
        {
            _llamaService.UnloadModel();
        }
        else if (_activeBackend == Backend.LlamaServer && keepBackend != Backend.LlamaServer)
        {
            await _serverProvider.StopServerAsync();
        }
    }

    public IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default)
        => _activeBackend == Backend.LlamaServer
            ? _serverProvider.ChatAsync(history, userMessage, systemPrompt, ct)
            : _llamaService.ChatAsync(history, userMessage, systemPrompt, ct);

    public Task<string> GenerateAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default)
        => _activeBackend == Backend.LlamaServer
            ? _serverProvider.GenerateAsync(history, userMessage, systemPrompt, ct)
            : _llamaService.GenerateAsync(history, userMessage, systemPrompt, ct);

    public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        return _activeBackend switch
        {
            Backend.LlamaServer => _serverProvider.TestConnectionAsync(ct),
            Backend.LlamaSharp => Task.FromResult(_llamaService.IsModelLoaded
                ? (true, $"Model loaded: {_llamaService.LoadedModelName}")
                : (false, "No model loaded.")),
            _ => Task.FromResult((false, "No model loaded. Go to Models tab to load a GGUF model.")),
        };
    }

    public void Dispose()
    {
        _llamaService.OnStatusChanged -= _statusHandler;
        _llamaService.OnLoadingChanged -= _loadingHandler;
        _llamaService.OnError -= _errorHandler;
        _serverProvider.OnStatusChanged -= _serverStatusHandler;
        _serverProvider.OnLoadingChanged -= _serverLoadingHandler;
        _serverProvider.OnError -= _serverErrorHandler;
    }
}
