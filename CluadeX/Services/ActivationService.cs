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

    public bool IsActivated => _isActivated && (_expiresAt == null || _expiresAt > DateTime.UtcNow);
    public string ActivationTier => _activationTier;
    public string TierDisplayName => _activationTier switch
    {
        "pro" => "Pro",
        "enterprise" => "Enterprise",
        "dev" => "Developer",
        _ => "Free",
    };

    public event Action? ActivationChanged;

    /// <summary>
    /// Raised when the key is already activated on another device.
    /// The UI should show a confirmation dialog; call ActivateForceAsync() to proceed.
    /// Args: (licenseKey, serverMessage)
    /// </summary>
    public event Action<string, string>? DeviceConflictDetected;

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
        catch (Exception ex)
        {
            // Network/timeout/parse error — keep cached activation (grace period).
            // Only server-confirmed invalidity clears the key; transient failures
            // must not silently wipe the user's license.
            System.Diagnostics.Debug.WriteLine($"[ActivationService] Online validation skipped (offline): {ex.GetType().Name}");
        }
    }

    /// <summary>Attempt to activate with a key via online API. Returns (success, message).</summary>
    public async Task<(bool success, string message)> ActivateAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return (false, "Please enter an activation key.");

        key = key.Trim();

        var (success, tier, message, errorCode) = await _licenseService.ActivateAsync(key, forceRebind: false, ct);

        if (success)
        {
            _isActivated = true;
            _activationTier = tier;
            _settingsService.UpdateSettings(s => s.ActivationKey = key);
            ActivationChanged?.Invoke();
            return (true, $"Activated! Tier: {TierDisplayName}");
        }

        // Key is already activated on another device — notify UI for confirmation
        if (errorCode == "ALREADY_ACTIVATED_OTHER_DEVICE")
        {
            DeviceConflictDetected?.Invoke(key, message);
            return (false, message + "\n\nต้องการย้ายมาเครื่องนี้หรือไม่? (เครื่องเก่าจะถูกยกเลิก)");
        }

        return (false, !string.IsNullOrEmpty(message) ? message : "Invalid activation key. Please check and try again.");
    }

    /// <summary>Force activate: deactivate old device and activate on this device.</summary>
    public async Task<(bool success, string message)> ActivateForceAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return (false, "Please enter an activation key.");

        key = key.Trim();

        // Step 1: Deactivate from old device
        await _licenseService.DeactivateAsync(key, ct);

        // Step 2: Activate on this device with force_rebind
        var (success, tier, message, _) = await _licenseService.ActivateAsync(key, forceRebind: true, ct);

        if (success)
        {
            _isActivated = true;
            _activationTier = tier;
            _settingsService.UpdateSettings(s => s.ActivationKey = key);
            ActivationChanged?.Invoke();
            return (true, $"ย้ายเครื่องสำเร็จ! Tier: {TierDisplayName}");
        }

        return (false, !string.IsNullOrEmpty(message) ? message : "Failed to transfer license.");
    }

    /// <summary>Synchronous activation (legacy compat — wraps async safely to avoid SynchronizationContext deadlock).</summary>
    public (bool success, string message) Activate(string key)
    {
        try
        {
            // Use Task.Run to escape UI SynchronizationContext and prevent deadlock
            return Task.Run(() => ActivateAsync(key)).GetAwaiter().GetResult();
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
        // Clear local state first (even if server call fails)
        _isActivated = false;
        _activationTier = "free";
        _expiresAt = null;
        var key = _settingsService.Settings.ActivationKey;
        _settingsService.UpdateSettings(s => s.ActivationKey = null);
        ActivationChanged?.Invoke();

        // Best-effort server deactivation in background
        if (!string.IsNullOrEmpty(key))
            _ = Task.Run(() => _licenseService.DeactivateAsync(key));
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
        // Accept any key format: XXXX-XXXX-XXXX-XXXX (server validates the actual key)
        return key.Contains('-') && key.Split('-').Length >= 3 && key.Length >= 10;
    }
}
