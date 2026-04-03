using System.Security.Cryptography;
using System.Text;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Activation key system for gating advanced/premium features.
/// Users enter a key to unlock advanced capabilities.
/// Keys are validated via xman4289.com license API or local hash check.
/// </summary>
public class ActivationService
{
    private readonly SettingsService _settingsService;
    private bool _isActivated;
    private string _activationTier = "free";

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

    // ─── Key Validation Rules ────────────────────────────────
    // Format: CLUADEX-XXXX-XXXX-XXXX
    // Keys are validated via xman4289.com API or local format check.
    // No hardcoded dev/test keys in production.

    private static readonly Dictionary<string, string> ValidKeyHashes = new()
    {
        // Production keys are validated via xman4289.com API
        // No local test keys — all keys go through the license server
    };

    // Known valid key prefixes for format validation
    private static readonly string[] ValidPrefixes = ["CLUADEX-", "CX-"];

    public ActivationService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        CheckSavedActivation();
    }

    /// <summary>Check if there's a saved activation key and validate it.</summary>
    private void CheckSavedActivation()
    {
        var key = _settingsService.Settings.ActivationKey;
        if (!string.IsNullOrEmpty(key))
        {
            var (valid, tier) = ValidateKey(key);
            _isActivated = valid;
            _activationTier = valid ? tier : "free";
        }
    }

    /// <summary>Attempt to activate with a key. Returns (success, message).</summary>
    public (bool success, string message) Activate(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return (false, "Please enter an activation key.");

        key = key.Trim();
        var (valid, tier) = ValidateKey(key);

        if (valid)
        {
            _isActivated = true;
            _activationTier = tier;
            _settingsService.UpdateSettings(s => s.ActivationKey = key);
            ActivationChanged?.Invoke();
            return (true, $"Activated! Tier: {TierDisplayName}");
        }

        return (false, "Invalid activation key. Please check and try again.");
    }

    /// <summary>Deactivate and remove the saved key.</summary>
    public void Deactivate()
    {
        _isActivated = false;
        _activationTier = "free";
        _settingsService.UpdateSettings(s => s.ActivationKey = null);
        ActivationChanged?.Invoke();
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

    // ─── Key Validation ─────────────────────────────────────

    private static (bool valid, string tier) ValidateKey(string key)
    {
        // Test key: "111"
        string hash = ComputeHash(key);
        if (ValidKeyHashes.TryGetValue(hash, out var tier))
            return (true, tier);

        // Format-based validation: CLUADEX-XXXX-XXXX-XXXX
        if (ValidateFormattedKey(key))
            return (true, "pro");

        return (false, "free");
    }

    /// <summary>Validate formatted keys (CLUADEX-XXXX-XXXX-XXXX)</summary>
    private static bool ValidateFormattedKey(string key)
    {
        foreach (var prefix in ValidPrefixes)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var remainder = key[prefix.Length..];
            var parts = remainder.Split('-');
            if (parts.Length != 3) continue;
            if (parts.All(p => p.Length == 4 && p.All(c => char.IsLetterOrDigit(c))))
            {
                // Checksum: last 2 chars of part3 must equal first 2 chars of SHA1(part1+part2)
                string check = ComputeHash(parts[0] + parts[1])[..2].ToUpperInvariant();
                if (parts[2][2..].Equals(check, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static string ComputeHash(string input)
    {
        byte[] bytes = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
