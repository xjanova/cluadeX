namespace CluadeX.Services;

/// <summary>Something about the license the user should be told about.</summary>
public enum LicenseNotice
{
    /// <summary>This PC was registered and got its free trial key at startup.</summary>
    TrialStarted,
    /// <summary>The free trial ran out (said once per install).</summary>
    TrialEnded,
    /// <summary>A license the server already held for this PC was taken back (e.g. after a reinstall).</summary>
    Restored,
}

/// <summary>
/// Activation key system for gating advanced/premium features.
/// Keys are issued and checked by the xman4289.com license API (<see cref="XmanLicenseService"/>); the last
/// confirmed state is cached in settings so the right tier shows at startup and survives a while offline.
/// </summary>
public class ActivationService
{
    /// <summary>How long a key keeps working with no answer from the server since it was last confirmed.</summary>
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(30);

    private readonly SettingsService _settingsService;
    private readonly XmanLicenseService _licenseService;
    private readonly DebugLogService? _log;

    private string _tier = "free";   // free | trial | pro | lifetime
    private DateTime? _expiresUtc;
    private bool _hasKey;
    private bool _graceExpired;
    private bool _lastReportedActive;
    // One trial request at a time: the startup provisioning and the dialog's button could otherwise both ask.
    private readonly SemaphoreSlim _trialGate = new(1, 1);

    public bool IsActivated => _hasKey && !_graceExpired && _tier != "free"
                               && (_expiresUtc == null || _expiresUtc > DateTime.UtcNow);
    public bool IsTrial => IsActivated && _tier == "trial";
    public bool IsLifetime => IsActivated && _tier == "lifetime";
    /// <summary>When the active license stops working (UTC); null when it never does or nothing is active.</summary>
    public DateTime? ExpiresAtUtc => IsActivated ? _expiresUtc : null;
    public string ActivationTier => IsActivated ? _tier : "free";
    public string TierDisplayName => ActivationTier switch
    {
        "trial" => "Pro Trial",
        "pro" => "Pro",
        "lifetime" => "Pro Lifetime",
        _ => "Free",
    };

    /// <summary>The saved key with its middle hidden (DEMO-AB12-••••-WX34), or null.</summary>
    public string? MaskedKey => Mask(_settingsService.Settings.ActivationKey);

    /// <summary>What /register-device said this launch (null until it answered).</summary>
    public DeviceRegistration? LastRegistration { get; private set; }

    /// <summary>"Get a free trial key" may be offered: the server has never given this PC a trial or a license,
    /// and this install never asked.</summary>
    public bool CanRequestTrial => !IsActivated && !_settingsService.Settings.LicenseTrialRequested
                                   && LastRegistration?.IsFreshDevice == true;

    public event Action? ActivationChanged;

    /// <summary>Raised (on a background thread) for things the user should be told about.</summary>
    public event Action<LicenseNotice>? Notice;

    /// <summary>
    /// Raised when the key is already activated on another device.
    /// The UI should show a confirmation dialog; call ActivateForceAsync() to proceed.
    /// Args: (licenseKey, serverMessage)
    /// </summary>
    public event Action<string, string>? DeviceConflictDetected;

    public ActivationService(SettingsService settingsService, XmanLicenseService licenseService,
        DebugLogService? log = null)
    {
        _settingsService = settingsService;
        _licenseService = licenseService;
        _log = log;
        CheckSavedActivationLocal();
        _lastReportedActive = IsActivated;
    }

    /// <summary>Server license_type → tier.</summary>
    public static string TierFor(string? licenseType) => (licenseType ?? "").Trim().ToLowerInvariant() switch
    {
        "demo" or "trial" => "trial",
        "lifetime" => "lifetime",
        "free" => "free",
        _ => "pro",   // yearly, monthly, weekly, daily — and keys saved before the type was recorded
    };

    /// <summary>Startup state from the cache (offline-safe). <see cref="ValidateOnlineAsync"/> corrects it.</summary>
    private void CheckSavedActivationLocal()
    {
        var s = _settingsService.Settings;
        _hasKey = !string.IsNullOrEmpty(s.ActivationKey);
        if (!_hasKey)
        {
            _tier = "free";
            _expiresUtc = null;
            _graceExpired = false;
            return;
        }

        _tier = TierFor(s.LicenseType);
        _expiresUtc = AsUtc(s.LicenseExpiresUtc);

        // A key saved before the confirmation time was recorded starts its offline grace now.
        if (AsUtc(s.LicenseValidatedUtc) is not { } confirmed)
        {
            confirmed = DateTime.UtcNow;
            _settingsService.UpdateSettings(x => x.LicenseValidatedUtc = confirmed);
        }
        // A confirmation dated in the future is a tampered file, not a confirmation.
        _graceExpired = confirmed > DateTime.UtcNow.AddDays(1) || DateTime.UtcNow - confirmed > OfflineGrace;
    }

    /// <summary>Validate saved key against the license server. Call at startup after UI is ready.</summary>
    public async Task ValidateOnlineAsync(CancellationToken ct = default)
    {
        var key = _settingsService.Settings.ActivationKey;
        if (string.IsNullOrEmpty(key))
        {
            _hasKey = false;
            _tier = "free";
            return;
        }

        try
        {
            var r = await _licenseService.ValidateAsync(key, ct);
            if (r.Valid)
            {
                var s = _settingsService.Settings;
                Adopt(key, r.Grant?.Type ?? s.LicenseType, r.Grant != null ? r.Grant.ExpiresUtc : AsUtc(s.LicenseExpiresUtc));
                Report();
                return;
            }

            // The server says the key is over (not found on this PC, revoked or expired) — drop it.
            bool wasTrial = _tier == "trial" || TierFor(r.Grant?.Type) == "trial";
            _log?.Info("License", $"saved key no longer valid ({r.ErrorCode}) — cleared");
            ClearSavedKey();
            Report();
            if (wasTrial) AnnounceTrialEnded();
        }
        catch (Exception ex)
        {
            // Network/timeout/rate-limit/5xx — keep the cached activation (offline grace). Only a server verdict
            // clears the key; a transient failure must not silently wipe the user's license.
            _log?.Debug("License", $"online validation skipped (offline?): {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Once per launch, after <see cref="ValidateOnlineAsync"/>: registers this PC with xman4289.com, takes back a
    /// license the server already holds for it, and — on a PC the server has never given a trial or a license —
    /// gets the free trial key right away, so a fresh install starts with Pro on and nothing to type.
    /// </summary>
    public async Task AutoProvisionAsync(CancellationToken ct = default)
    {
        DeviceRegistration? reg = null;
        try
        {
            reg = await _licenseService.RegisterDeviceAsync(ct);
            LastRegistration = reg;
            _log?.Info("License", $"device registered · status={reg.Status} · canTrial={reg.CanStartTrial}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log?.Debug("License", $"register-device failed (offline?): {ex.GetType().Name}");
        }

        // A saved key — active, or waiting for the server to confirm it — is the validator's business.
        if (!string.IsNullOrEmpty(_settingsService.Settings.ActivationKey) || reg == null)
        {
            Report();
            return;
        }

        try
        {
            var held = await _licenseService.CheckMachineAsync(ct);
            if (held != null && TierFor(held.Type) != "free" && (held.ExpiresUtc == null || held.ExpiresUtc > DateTime.UtcNow))
            {
                Adopt(held.Key, held.Type, held.ExpiresUtc);
                _log?.Info("License", $"took back the license this PC already holds ({held.Type})");
                Report();
                Notice?.Invoke(LicenseNotice.Restored);
                return;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log?.Debug("License", $"check-machine failed (offline?): {ex.GetType().Name}");
            return;   // try again next launch — never ask for a trial without knowing what the PC holds
        }

        if (reg.IsFreshDevice && !_settingsService.Settings.LicenseTrialRequested)
        {
            var (ok, code, _) = await StartTrialAsync(ct);
            _log?.Info("License", ok ? "free trial key issued for this PC" : $"no trial key ({code})");
            if (ok) Notice?.Invoke(LicenseNotice.TrialStarted);
            return;
        }

        Report();   // CanRequestTrial may have changed
        if (reg.TrialIsOver) AnnounceTrialEnded();
    }

    /// <summary>
    /// Ask the server for this PC's one free trial key. Returns (success, errorCode, serverMessage); errorCode is
    /// <see cref="XmanLicenseService.ErrorOffline"/> when the server could not be reached.
    /// </summary>
    public async Task<(bool success, string? errorCode, string message)> StartTrialAsync(CancellationToken ct = default)
    {
        await _trialGate.WaitAsync(ct);
        try { return await StartTrialCoreAsync(ct); }
        finally { _trialGate.Release(); }
    }

    private async Task<(bool success, string? errorCode, string message)> StartTrialCoreAsync(CancellationToken ct)
    {
        if (IsActivated) return (false, "ALREADY_LICENSED", "");
        if (_settingsService.Settings.LicenseTrialRequested) return (false, "TRIAL_NOT_AVAILABLE", "");

        LicenseCallResult r;
        try { r = await _licenseService.StartDemoAsync(ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (false, XmanLicenseService.ErrorOffline, ex.Message);
        }

        // The server answered, so this install never asks again whatever it said — a PC that keeps asking after
        // its trial is the one the server flags as abuse.
        _settingsService.UpdateSettings(s => s.LicenseTrialRequested = true);

        if (r.Success && r.Grant != null)
        {
            Adopt(r.Grant.Key, r.Grant.Type, r.Grant.ExpiresUtc);
            Report();
            return (true, null, r.Message);
        }

        // The trial is running but its key wasn't handed back — the PC holds it, so fetch it.
        if (r.ErrorCode is "TRIAL_ACTIVE" or "ALREADY_LICENSED")
        {
            try
            {
                var held = await _licenseService.CheckMachineAsync(ct);
                if (held != null && (held.ExpiresUtc == null || held.ExpiresUtc > DateTime.UtcNow))
                {
                    Adopt(held.Key, held.Type, held.ExpiresUtc);
                    Report();
                    return (true, null, r.Message);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log?.Debug("License", $"check-machine after {r.ErrorCode} failed: {ex.GetType().Name}");
            }
        }

        Report();
        return (false, r.ErrorCode, r.Message);
    }

    /// <summary>Attempt to activate with a key via online API. Returns (success, message, errorCode).</summary>
    public async Task<(bool success, string message, string? errorCode)> ActivateAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return (false, "Please enter an activation key.", "EMPTY");

        key = key.Trim();
        var r = await _licenseService.ActivateAsync(key, forceRebind: false, ct);

        if (r.Success && r.Grant != null)
        {
            Adopt(r.Grant.Key, r.Grant.Type, r.Grant.ExpiresUtc);
            Report();
            return (true, $"Activated! Tier: {TierDisplayName}", null);
        }

        // Key is already activated on another device — notify UI for confirmation
        if (r.ErrorCode == "ALREADY_ACTIVATED_OTHER_DEVICE")
        {
            DeviceConflictDetected?.Invoke(key, r.Message);
            return (false, r.Message + "\n\nต้องการย้ายมาเครื่องนี้หรือไม่? (เครื่องเก่าจะถูกยกเลิก)", r.ErrorCode);
        }

        return (false, !string.IsNullOrEmpty(r.Message) ? r.Message : "Invalid activation key. Please check and try again.",
            r.ErrorCode);
    }

    /// <summary>Move the key here from the PC it is active on (the server releases the old one).</summary>
    public async Task<(bool success, string message)> ActivateForceAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return (false, "Please enter an activation key.");

        key = key.Trim();
        var r = await _licenseService.ActivateAsync(key, forceRebind: true, ct);

        if (r.Success && r.Grant != null)
        {
            Adopt(r.Grant.Key, r.Grant.Type, r.Grant.ExpiresUtc);
            Report();
            return (true, $"ย้ายเครื่องสำเร็จ! Tier: {TierDisplayName}");
        }

        return (false, !string.IsNullOrEmpty(r.Message) ? r.Message : "Failed to transfer license.");
    }

    /// <summary>Deactivate and remove the saved key.</summary>
    public async Task DeactivateAsync(CancellationToken ct = default)
    {
        var key = _settingsService.Settings.ActivationKey;
        if (!string.IsNullOrEmpty(key))
            await _licenseService.DeactivateAsync(key, ct);

        ClearSavedKey();
        Report();
    }

    /// <summary>Deactivate without waiting: local state first, the server is told in the background.</summary>
    public void Deactivate()
    {
        var key = _settingsService.Settings.ActivationKey;
        ClearSavedKey();
        Report();

        if (!string.IsNullOrEmpty(key))
            _ = Task.Run(() => _licenseService.DeactivateAsync(key));
    }

    /// <summary>Call periodically (the UI does, every minute): notices a trial or license running out while the
    /// app is open, and tells everyone once.</summary>
    public void CheckExpiry()
    {
        bool active = IsActivated;
        if (active == _lastReportedActive) return;
        bool wasTrial = _tier == "trial";
        Report();
        if (!active && wasTrial) AnnounceTrialEnded();
    }

    /// <summary>Check if a specific feature requires activation.</summary>
    public bool IsFeatureUnlocked(string featureKey)
    {
        if (IsActivated) return true; // All features unlocked when activated (and not expired)

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
            // Local Git is a basic dev capability (like Claude Code) — free tier:
            // commit, add, status, merge, branch, checkout, diff, log, stash, init, clone.
            // Publishing to a remote (git_push) + GitHub PRs stay behind feature.github.
            "feature.git" => true,
            // Nothing in the app gates these, and the safety ones (permissions, command blocking) must never be
            // paywalled — free, so the Features page never shows a lock that isn't real.
            "feature.huggingface" => true,
            "feature.contextMemory" => true,
            "feature.smartEditing" => true,
            "feature.plugins" => true,
            "feature.permissions" => true,
            "feature.commandBlock" => true,
            // These require activation (the same list as FeaturesViewModel.GatedFeatures):
            "feature.openai" => false,
            "feature.anthropic" => false,
            "feature.gemini" => false,
            "feature.multiProvider" => false, // Cloud providers need activation
            "feature.github" => false,
            "feature.webFetch" => false,
            "feature.taskManager" => false,
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

    // ─── Helpers ───

    private void Adopt(string key, string? licenseType, DateTime? expiresUtc)
    {
        var now = DateTime.UtcNow;
        _settingsService.UpdateSettings(s =>
        {
            s.ActivationKey = key;
            s.LicenseType = licenseType;
            s.LicenseExpiresUtc = expiresUtc;
            s.LicenseValidatedUtc = now;
        });
        _hasKey = true;
        _tier = TierFor(licenseType);
        _expiresUtc = expiresUtc;
        _graceExpired = false;
    }

    private void ClearSavedKey()
    {
        _settingsService.UpdateSettings(s =>
        {
            s.ActivationKey = null;
            s.LicenseType = null;
            s.LicenseExpiresUtc = null;
            s.LicenseValidatedUtc = null;
        });
        _hasKey = false;
        _tier = "free";
        _expiresUtc = null;
        _graceExpired = false;
    }

    private void Report()
    {
        _lastReportedActive = IsActivated;
        ActivationChanged?.Invoke();
    }

    private void AnnounceTrialEnded()
    {
        if (_settingsService.Settings.LicenseTrialEndedNoticeShown) return;
        _settingsService.UpdateSettings(s => s.LicenseTrialEndedNoticeShown = true);
        Notice?.Invoke(LicenseNotice.TrialEnded);
    }

    private static DateTime? AsUtc(DateTime? d) => d is { } v
        ? v.Kind switch
        {
            DateTimeKind.Utc => v,
            DateTimeKind.Local => v.ToUniversalTime(),
            _ => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        }
        : null;

    internal static string? Mask(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var parts = key.Trim().Split('-');
        if (parts.Length >= 3)
            return string.Join("-", parts.Select((p, i) => i == 0 || i == parts.Length - 1 ? p : new string('•', p.Length)));
        return key.Length <= 6 ? new string('•', key.Length) : key[..3] + new string('•', key.Length - 6) + key[^3..];
    }
}
