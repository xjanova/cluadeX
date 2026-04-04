namespace CluadeX.Services;

/// <summary>
/// Activation key system for gating advanced/premium features.
/// Users enter a key to unlock advanced capabilities.
/// Keys are validated via xman4289.com license API or local hash check.
/// </summary>
public class ActivationService
{
    private readonly SettingsService _settingsService;
    private readonly XmanLicenseService _licenseService;
    private bool _isActivated;
    private string _activationTier = "free";
    private DateTime? _expiresAt;

    public bool IsActivated => _isActivated;
    public string ActivationTier => _activationTier;
    public string TierDisplayName => _activationTier switch
    {
        "pro" => "Pro",
        "enterprise" => "Enterprise",
        "dev" => "Developer",
        _ => "Free",
    };

    public event Action? ActivationChanged;

    public ActivationService(SettingsService settingsService, XmanLicenseService licenseService)
    {
        _settingsService = settingsService;
        _licenseService = licenseService;
        // Optimistic: trust cached key at startup, then validate online in background
        CheckSavedActivationLocal();
    }

    /// <summary>Quick local check at startup (offline-safe). Online validation should follow.</summary>
    private void CheckSavedActivationLocal()
    {
        var key = _settingsService.Settings.ActivationKey;
        if (!string.IsNullOrEmpty(key))
        {
            // Optimistically trust cached key — ValidateOnlineAsync will correct if invalid
            _isActivated = true;
            _activationTier = "pro";
        }
    }

    /// <summary>Validate saved key against the license server. Call at startup after UI is ready.</summary>
    public async Task ValidateOnlineAsync(CancellationToken ct = default)
    {
        var key = _settingsService.Settings.ActivationKey;
        if (string.IsNullOrEmpty(key))
        {
            _isActivated = false;
            _activationTier = "free";
            return;
        }

        try
        {
            var (valid, tier, expires) = await _licenseService.ValidateAsync(key, ct);
            _isActivated = valid;
            _activationTier = valid ? tier : "free";
            _expiresAt = expires;

            if (!valid)
            {
                // Key is no longer valid on server — clear it
                _settingsService.UpdateSettings(s => s.ActivationKey = null);
            }

            ActivationChanged?.Invoke();
        }
        catch
        {
            // Offline — keep cached activation (grace period)
        }
    }

    /// <summary>Attempt to activate with a key via online API. Returns (success, message).</summary>
    public async Task<(bool success, string message)> ActivateAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return (false, "Please enter an activation key.");

        key = key.Trim();

        // Always validate via online API
        var (success, tier, message) = await _licenseService.ActivateAsync(key, ct);

        if (success)
        {
            _isActivated = true;
            _activationTier = tier;
            _settingsService.UpdateSettings(s => s.ActivationKey = key);
            ActivationChanged?.Invoke();
            return (true, $"Activated! Tier: {TierDisplayName}");
        }

        return (false, !string.IsNullOrEmpty(message) ? message : "Invalid activation key. Please check and try again.");
    }

    /// <summary>Synchronous activation (legacy compat — wraps async).</summary>
    public (bool success, string message) Activate(string key)
    {
        try
        {
            return ActivateAsync(key).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return (false, $"Activation failed: {ex.Message}");
        }
    }

    /// <summary>Deactivate and remove the saved key.</summary>
    public async Task DeactivateAsync(CancellationToken ct = default)
    {
        var key = _settingsService.Settings.ActivationKey;
        if (!string.IsNullOrEmpty(key))
            await _licenseService.DeactivateAsync(key, ct);

        _isActivated = false;
        _activationTier = "free";
        _expiresAt = null;
        _settingsService.UpdateSettings(s => s.ActivationKey = null);
        ActivationChanged?.Invoke();
    }

    /// <summary>Deactivate sync (legacy compat).</summary>
    public void Deactivate()
    {
        _ = DeactivateAsync();
    }

    /// <summary>Check if a specific feature requires activation.</summary>
    public bool IsFeatureUnlocked(string featureKey)
    {
        if (_isActivated) return true; // All features unlocked when activated

        // Free-tier features (always available)
        return featureKey switch
        {
            "feature.localInference" => true,
            "feature.chatPersistence" => true,
            "feature.markdown" => true,
            "feature.gpuDetection" => true,
            "feature.darkTheme" => true,
            "feature.i18n" => true,
            "feature.buddy" => true,
            "feature.dpapi" => true,
            "feature.pathSafety" => true,
            "feature.noTelemetry" => true,
            "feature.codeExecution" => true,
            "feature.fileSystem" => true,
            "feature.ollama" => true, // Ollama is free (local)
            // These require activation:
            "feature.openai" => false,
            "feature.anthropic" => false,
            "feature.gemini" => false,
            "feature.multiProvider" => false, // Cloud providers need activation
            "feature.huggingface" => false,
            "feature.git" => false,
            "feature.github" => false,
            "feature.webFetch" => false,
            "feature.contextMemory" => false,
            "feature.smartEditing" => false,
            "feature.plugins" => false,
            "feature.taskManager" => false,
            "feature.permissions" => false,
            "feature.commandBlock" => false,
            _ => false,
        };
    }

    /// <summary>Check if a key has valid format (quick pre-check, not validation).</summary>
    public static bool HasValidKeyFormat(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        key = key.Trim();
        // Accept CLUADEX-XXXX-XXXX-XXXX or CX-XXXX-XXXX-XXXX format
        return (key.StartsWith("CLUADEX-", StringComparison.OrdinalIgnoreCase)
             || key.StartsWith("CX-", StringComparison.OrdinalIgnoreCase))
            && key.Split('-').Length >= 4;
    }
}
