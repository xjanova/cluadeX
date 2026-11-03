namespace CluadeX.Services;

/// <summary>
/// What the splash does with the startup update, as pure rules (tests/apitest UpdateProgressWire proves them).
///
/// Owner 2026-11-03: "หน้า สแปรช หากเจออัพเดท ให้โหลดให้เสร็จแล้วติดตั้งก่อน … อย่าค้าง". The splash used to give up on a
/// download after 20 s (60 s when almost done) and open the old version. Now: the CHECK is bounded
/// (<see cref="CheckCap"/> — Velopack's check takes no token and can sit for minutes on a dead network); once a download
/// has started the splash waits for it to the end, then installs and restarts. The user can always Skip (the download
/// goes on in the background and installs when the app closes), and a failure enters the app with the reason shown.
/// </summary>
public static class StartupUpdatePolicy
{
    /// <summary>The longest the splash waits for the check itself (not the download) before opening the app; the
    /// check goes on in the background and the main window's update bar takes over.</summary>
    public static readonly TimeSpan CheckCap = TimeSpan.FromSeconds(25);

    /// <summary>The longest a restart from the splash waits for the app's own startup work that writes files (license,
    /// MCP) before flushing and applying. Never longer: the apply must not be held forever.</summary>
    public static readonly TimeSpan RestartPrepCap = TimeSpan.FromSeconds(6);

    public enum Next
    {
        /// <summary>Keep waiting (and keep the row up to date).</summary>
        Wait,
        /// <summary>The user went in (Skip, Enter, or closed the splash): never restart under them.</summary>
        Skipped,
        /// <summary>The check is still running after <see cref="CheckCap"/>: open the app, the check continues.</summary>
        CheckTooSlow,
        /// <summary>The job finished: read its result.</summary>
        Finished,
    }

    /// <param name="jobDone">The shared check-and-download job has completed.</param>
    /// <param name="phase">Where the update stands now.</param>
    /// <param name="sinceCheckStarted">Time since the splash started (or joined) the job.</param>
    /// <param name="entered">The user has gone into the app.</param>
    public static Next Decide(bool jobDone, UpdatePhase phase, TimeSpan sinceCheckStarted, bool entered)
    {
        if (entered) return Next.Skipped;
        if (jobDone) return Next.Finished;
        // Downloading or preparing: wait to the end — a stall is shown, Skip is always there, the downloader gives up
        // by itself after its failures in a row, and that ends the job.
        if (phase is UpdatePhase.Downloading or UpdatePhase.Preparing) return Next.Wait;
        // Staged / Failed are the job's last statements (its task completes right after them): reading one before the
        // task is marked done must not open the app on a finished download ("slow network") instead of installing it.
        if (phase is UpdatePhase.Staged or UpdatePhase.Failed) return Next.Wait;
        return sinceCheckStarted >= CheckCap ? Next.CheckTooSlow : Next.Wait;
    }

    /// <summary>May the splash (or the exit hook) install <paramref name="version"/> without being asked? Only the real
    /// circuit breaker says no — a version that already failed <see cref="UpdateAttemptLog.MaxConsecutiveFailures"/>
    /// times. One or two earlier failures are shown, never a reason to stop.</summary>
    public static bool MayAutoInstall(UpdateAttemptLog attempts, string? version) =>
        !string.IsNullOrEmpty(version) && !attempts.ShouldStopTrying(version);

    /// <summary>The splash's overall bar: <paramref name="done"/> of <paramref name="total"/> checks, plus the update
    /// row's own share while it downloads (<paramref name="updateShare"/> 0..1), never past the end.</summary>
    public static double OverallPercent(int done, int total, double updateShare)
    {
        if (total <= 0) return 0;
        double v = done + Math.Clamp(updateShare, 0, 1);
        return Math.Clamp(100.0 * v / total, 0, 100);
    }
}
