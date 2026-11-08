using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Who gets updates downloaded and installed by themselves (installer contract 2.1):
/// <c>AutoUpdateAllowed = (license valid AND paid or trial) OR account linked</c>.
/// Everyone else is still told a new version is out and can press "อัปเดตเลย" — only the automatic download/apply is
/// held back. The user's own switch (<see cref="AppSettings.AutoUpdateEnabled"/>) can turn it off even when allowed.
/// </summary>
public static class AutoUpdateEntitlement
{
    /// <summary>The rule itself, kept pure for the test harness.</summary>
    public static bool Allowed(bool licenseValidPaidOrTrial, bool accountLinked) => licenseValidPaidOrTrial || accountLinked;

    /// <summary>The rule on this install: an active trial/Pro/lifetime license (never Free) or a linked account.</summary>
    public static bool Allowed(ActivationService? activation, AppSettings settings)
        => Allowed(activation?.IsActivated == true, settings.AccountLinked);

    /// <summary>Allowed AND the user left the switch on: what the background download / startup install / exit install obey.</summary>
    public static bool Automatic(ActivationService? activation, AppSettings settings)
        => Allowed(activation, settings) && settings.AutoUpdateEnabled;
}
