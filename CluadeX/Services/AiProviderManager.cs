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

    private Action<string>? _statusHandler;
    private Action<bool>? _loadingHandler;
    private Action<string>? _errorHandler;

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
        CostTrackingService costTracker)
    {
        _settingsService = settingsService;

        // Register all providers. LocalGgufProvider + LlamaServerProvider come from
        // DI (singletons) so the LocalGguf router shares the same llama-server
        // instance with the standalone LlamaServer provider — preventing duplicate
        // server processes when the user toggles between them.
        _providers[AiProviderType.Local] = localProvider;
        _providers[AiProviderType.LlamaServer] = serverProvider;
        _providers[AiProviderType.OpenAI] = new OpenAiProvider(settingsService);
        _providers[AiProviderType.Anthropic] = new AnthropicProvider(settingsService, costTracker);
        _providers[AiProviderType.Gemini] = new GeminiProvider(settingsService);
        _providers[AiProviderType.Ollama] = new OllamaProvider(settingsService);

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

            await provider.InitializeAsync(ct);
        }
        catch (Exception ex)
        {
            OnStatusChanged?.Invoke($"Error: {ex.Message}");
        }
        finally
        {
            lock (_switchLock) { _isSwitching = false; }
        }
    }

    public IAiProvider GetProvider(AiProviderType type)
        => _providers.TryGetValue(type, out var p) ? p : _activeProvider;

    public async Task ReinitializeActiveAsync(CancellationToken ct = default)
    {
        try
        {
            await _activeProvider.InitializeAsync(ct);
        }
        catch (Exception ex)
        {
            OnStatusChanged?.Invoke($"Error: {ex.Message}");
        }
    }

    private void SubscribeToProvider(IAiProvider provider)
    {
        _statusHandler = s => OnStatusChanged?.Invoke(s);
        _loadingHandler = b => OnLoadingChanged?.Invoke(b);
        _errorHandler = e => OnError?.Invoke(e);
        provider.OnStatusChanged += _statusHandler;
        provider.OnLoadingChanged += _loadingHandler;
        provider.OnError += _errorHandler;
    }

    private void UnsubscribeFromProvider(IAiProvider provider)
    {
        if (_statusHandler != null) provider.OnStatusChanged -= _statusHandler;
        if (_loadingHandler != null) provider.OnLoadingChanged -= _loadingHandler;
        if (_errorHandler != null) provider.OnError -= _errorHandler;
        _statusHandler = null;
        _loadingHandler = null;
        _errorHandler = null;
    }

    public void Dispose()
    {
        UnsubscribeFromProvider(_activeProvider);
        foreach (var provider in _providers.Values)
            provider.Dispose();
        _providers.Clear();
    }
}
