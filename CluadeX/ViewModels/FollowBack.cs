namespace CluadeX.ViewModels;

/// <summary>
/// Follow mode ("ตามดูโค้ด") takes the window to the Code page for the agent's edits; once the edits stop it goes back to
/// the Chat page (owner 2026-11-02: "เด้งกลับหน้าแชทปกติ เมื่อแก้ไฟล์เสร็จแล้ว มาไม่น้อยกว่า 10 วินาที", settings
/// FollowReturnToChat / FollowReturnSeconds). The decision, apart from the timer, so it can be tested.
/// </summary>
public static class FollowBack
{
    public const int MinSeconds = 10, MaxSeconds = 600;

    public enum Verdict
    {
        /// <summary>Still inside the wait after the last edit.</summary>
        Wait,
        /// <summary>Go back to the Chat page now.</summary>
        Return,
        /// <summary>Not follow mode's page any more (turned off, the user moved or took over): stop watching.</summary>
        GiveUp,
    }

    /// <param name="onCode">The window is on the Code page.</param>
    /// <param name="broughtByFollow">Follow mode opened it (not the user).</param>
    /// <param name="followStart">When follow mode opened it.</param>
    /// <param name="lastInput">The user's last key, click or wheel in the window.</param>
    public static Verdict Decide(bool enabled, bool onCode, bool broughtByFollow, DateTime followStart, DateTime lastEdit,
        DateTime lastInput, DateTime now, int seconds)
    {
        if (!enabled || !onCode || !broughtByFollow) return Verdict.GiveUp;
        // The user typed, clicked or scrolled since: they work on that page now, and it stays.
        if (lastInput >= followStart) return Verdict.GiveUp;
        return now - lastEdit >= TimeSpan.FromSeconds(Math.Clamp(seconds, MinSeconds, MaxSeconds)) ? Verdict.Return : Verdict.Wait;
    }
}
