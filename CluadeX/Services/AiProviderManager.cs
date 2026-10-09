using CluadeX.Models;
using CluadeX.Services.Providers;

namespace CluadeX.Services;

public class AiProviderManager : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly Dictionary<AiProviderType, IAiProvider> _providers = new();
    private IAiProvider _activeProvider;
    private readonly object _switchLock = new();
    private bool _isSwitching;

    // Stable handler delegates — created ONCE so Subscribe/Unsubscribe always reference the SAME
    // instances. Reallocating them per provider-switch (the old bug) could orphan a provider's
    // subscription (never detachable) → leak + duplicate status/loading/error events to the UI.
    private readonly Action<string> _statusHandler;
    private readonly Action<bool> _loadingHandler;
    private readonly Action<string> _errorHandler;

    private readonly DebugLogService? _log;
    private Task? _startupInit;

    public IAiProvider ActiveProvider => _activeProvider;
    public AiProviderType ActiveProviderType => _settingsService.Settings.ActiveProvider;

    public event Action<IAiProvider>? OnProviderChanged;
    public event Action<string>? OnStatusChanged;
    public event Action<bool>? OnLoadingChanged;
    public event Action<string>? OnError;

    public AiProviderManager(
        SettingsService settingsService,
        LocalGgufProvider localProvider,
        LlamaServerProvider serverProvider,
        CostTrackingService costTracker,
        FileSystemService fileSystemService,
        DebugLogService? debugLog = null)
    {
        _settingsService = settingsService;
        _log = debugLog;
        _statusHandler = s => OnStatusChanged?.Invoke(s);
        _loadingHandler = b => OnLoadingChanged?.Invoke(b);
        _errorHandler = e => OnError?.Invoke(e);

        // Register all providers. LocalGgufProvider + LlamaServerProvider come from
        // DI (singletons) so the LocalGguf router shares the same llama-server
        // instance with the standalone LlamaServer provider — preventing duplicate
        // server processes when the user toggles between them.
        _providers[AiProviderType.Local] = localProvider;
        _providers[AiProviderType.LlamaServer] = serverProvider;
        _providers[AiProviderType.OpenAI] = new OpenAiProvider(settingsService, costTracker);
        _providers[AiProviderType.Anthropic] = new AnthropicProvider(settingsService, costTracker);
        _providers[AiProviderType.Gemini] = new GeminiProvider(settingsService, costTracker);
        _providers[AiProviderType.Ollama] = new OllamaProvider(settingsService, debugLog);
        // The installed Claude Code CLI as a model — needs the working directory so
        // the CLI runs in (and reads) the user's actual project.
        _providers[AiProviderType.ClaudeDev] = new ClaudeDevProvider(settingsService, fileSystemService);

        // Set initial active provider
        var activeType = settingsService.Settings.ActiveProvider;
        _activeProvider = _providers.ContainsKey(activeType)
            ? _providers[activeType]
            : _providers[AiProviderType.Local];

        SubscribeToProvider(_activeProvider);
    }

    public async Task SwitchProviderAsync(AiProviderType type, CancellationToken ct = default)
    {
        if (!_providers.TryGetValue(type, out var provider)) return;
        if (provider == _activeProvider) return;

        // Guard against concurrent switching
        lock (_switchLock)
        {
            if (_isSwitching) return;
            _isSwitching = true;
        }

        try
        {
            UnsubscribeFromProvider(_activeProvider);
            _activeProvider = provider;
            SubscribeToProvider(provider);

            _settingsService.UpdateSettings(s => s.ActiveProvider = type);
            OnProviderChanged?.Invoke(provider);

            await InitializeSafeAsync(provider, ct);
        }
        catch (Exception ex)
        {
            _log?.Warn("Provider", $"switch to {type} failed", ex);
            OnStatusChanged?.Invoke(string.Format(LocalizedResources.Instance["chat.provider.initFailed"], provider.DisplayName));
        }
        finally
        {
            lock (_switchLock) { _isSwitching = false; }
        }
    }

    public IAiProvider GetProvider(AiProviderType type)
        => _providers.TryGetValue(type, out var p) ? p : _activeProvider;

    public Task ReinitializeActiveAsync(CancellationToken ct = default) => InitializeSafeAsync(_activeProvider, ct);

    /// <summary>
    /// Bring the persisted provider up once per run. The constructor only PICKS the provider; until now nothing
    /// initialised it at startup (only Settings ▸ Use this provider and MCP set_model did), so after every restart
    /// Anthropic/OpenAI/Gemini/Ollama sat at IsReady=false: the first message answered "not configured" with a
    /// perfectly good key and the status bar said OFFLINE. Idempotent and safe to fire-and-forget: a local GGUF
    /// provider's initialise is a no-op (weights are only ever loaded on an explicit request), an API provider's
    /// is a 1-token connection test.
    /// </summary>
    public Task InitializeActiveOnceAsync(CancellationToken ct = default)
    {
        lock (_switchLock)
            return _startupInit ??= InitializeAndAnnounceAsync(_activeProvider, ct);
    }

    /// <summary>Initialise, then tell listeners the active provider's state is now known. MainViewModel only
    /// re-reads IsModelLoaded (the OFFLINE/ONLINE badge) on loading-changed and provider-changed; a provider
    /// whose initialise never toggles "loading" (llama-server, an API provider with no key) would otherwise leave
    /// the badge showing the pre-init state.</summary>
    private async Task InitializeAndAnnounceAsync(IAiProvider provider, CancellationToken ct)
    {
        await InitializeSafeAsync(provider, ct);
        if (ReferenceEquals(provider, _activeProvider))
            OnProviderChanged?.Invoke(provider);
    }

    /// <summary>
    /// True when the active provider can take a request — waiting for the startup initialise if it is still
    /// running, and trying once more when it failed (the network may have been down at launch, or the key was
    /// added since). Never loads a local model: that stays an explicit action with a VRAM check.
    /// </summary>
    public async Task<bool> EnsureActiveReadyAsync(CancellationToken ct = default)
    {
        var provider = _activeProvider;
        if (provider.IsReady) return true;

        Task? pending;
        lock (_switchLock) pending = _startupInit;
        if (pending is { IsCompleted: false })
        {
            try { await pending.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* InitializeSafeAsync already logged it */ }
            if (provider.IsReady) return true;
        }

        if (ActiveProviderType == AiProviderType.Local) return false;   // a GGUF model is loaded explicitly, not here
        await InitializeAndAnnounceAsync(provider, ct);
        return provider.IsReady;
    }

    /// <summary>Initialise without letting an exception escape. The failure goes to the debug log; the status
    /// line gets a localized sentence instead of the raw exception text it used to show ("Error: {ex.Message}").</summary>
    private async Task InitializeSafeAsync(IAiProvider provider, CancellationToken ct)
    {
        try
        {
            await provider.InitializeAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // cancelled by the caller — nothing to report
        }
        catch (Exception ex)
        {
            _log?.Warn("Provider", $"{provider.DisplayName}: initialise failed", ex);
            OnStatusChanged?.Invoke(string.Format(LocalizedResources.Instance["chat.provider.initFailed"], provider.DisplayName));
        }
    }

    private void SubscribeToProvider(IAiProvider provider)
    {
        provider.OnStatusChanged += _statusHandler;
        provider.OnLoadingChanged += _loadingHandler;
        provider.OnError += _errorHandler;
    }

    private void UnsubscribeFromProvider(IAiProvider provider)
    {
        provider.OnStatusChanged -= _statusHandler;
        provider.OnLoadingChanged -= _loadingHandler;
        provider.OnError -= _errorHandler;
    }

    public void Dispose()
    {
        UnsubscribeFromProvider(_activeProvider);
        foreach (var provider in _providers.Values)
            provider.Dispose();
        _providers.Clear();
    }
}
