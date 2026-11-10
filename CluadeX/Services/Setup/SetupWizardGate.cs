namespace CluadeX.Services.Setup;

/// <summary>
/// Whether this launch opens the first-run setup wizard. Pure, so the rule is tested rather than trusted.
///
/// Shown: when started with --setup; on Velopack's first run (Setup.exe just installed CluadeX — never an update, which
/// does not fire that hook) even when an uninstall kept the settings and they say the wizard was done (v3.0.275: the
/// reinstall skipped it); and when there was no settings file before this launch and the wizard was never finished.
/// Not shown: every other start, including the restart after an automatic update.
/// </summary>
public static class SetupWizardGate
{
    public static bool FreshInstall(bool wizardCompleted, bool noSettingsBeforeLaunch, bool velopackFirstRun) =>
        velopackFirstRun || (!wizardCompleted && noSettingsBeforeLaunch);

    public static bool Show(bool forced, bool wizardCompleted, bool noSettingsBeforeLaunch, bool velopackFirstRun) =>
        forced || FreshInstall(wizardCompleted, noSettingsBeforeLaunch, velopackFirstRun);
}
