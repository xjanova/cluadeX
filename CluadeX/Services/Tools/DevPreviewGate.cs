namespace CluadeX.Services.Tools;

/// <summary>
/// The one gate for the Dev Preview capabilities (bg_* background processes and browser_* built-in browser).
/// Owner's licensing decision: available to every ACTIVATED license, the free time-limited trial key included;
/// on the free tier it is locked exactly like feature.taskManager. The refusal names the reason and the action
/// that fixes it — how to start the free trial when this PC can still get one.
/// </summary>
public static class DevPreviewGate
{
    /// <summary>The ActivationService feature key.</summary>
    public const string FeatureKey = "feature.devPreview";

    /// <summary>Switched on in Features AND unlocked by the license.</summary>
    public static bool IsAvailable(SettingsService settings, ActivationService activation) =>
        settings.Settings.Features.DevPreview && activation.IsFeatureUnlocked(FeatureKey);

    /// <summary>
    /// Null when Dev Preview may be used; otherwise the localized reason, naming <paramref name="what"/> (a tool
    /// name such as "bg_serve", or a feature name). Order matters: a free user who also switched the feature off
    /// first needs the license, so the license reason is given first.
    /// </summary>
    public static string? Refusal(SettingsService settings, ActivationService activation, string what)
    {
        if (!activation.IsFeatureUnlocked(FeatureKey))
        {
            var s = settings.Settings;
            // The trial is gone when this install already asked for it, or the saved key is an expired trial key.
            // Unknown (the server not asked yet this launch, offline) → explain how to start it: the dialog says
            // so itself if the server refuses.
            bool trialUsed = !activation.CanRequestTrial
                && (s.LicenseTrialRequested || ActivationService.TierFor(s.LicenseType) == "trial");
            return string.Format(L(trialUsed ? "devPreview.locked.trialUsed" : "devPreview.locked.trial"),
                what, L("license.chip.none"), L("license.trialButton"));
        }
        if (!settings.Settings.Features.DevPreview)
            return string.Format(L("devPreview.off"), what, L("feature.devPreview"));
        return null;
    }

    private static string L(string key) => LocalizedResources.Instance[key];
}
