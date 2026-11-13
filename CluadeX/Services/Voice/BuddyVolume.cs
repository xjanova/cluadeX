using CluadeX.Services.Voice.Tts;
using CluadeX.Views;

namespace CluadeX.Services.Voice;

/// <summary>
/// How loud น้อง CluadeX is (owner 2026-11-13: "เสียงน้องอวาต้าควรปรับโวลุ่มได้"). One setting, AppSettings.BuddyVolume
/// (0–100), for everything of hers that makes a sound: the sounds in her clips and the lines she says
/// (<see cref="CluadexBuddy.SoundVolume"/>), the replies she reads aloud inside the app (<see cref="VoicePlayer.MasterVolume"/>)
/// and the same replies when the mic window plays them (<see cref="PageLevel"/>). The question chime is not hers
/// (System.Media.SoundPlayer has no volume) and keeps Windows' level.
/// <para>50 is the level she always had: WPF players default to 0.5, and her clips were fixed at 0.5. The mic window's
/// page played at full (1.0), so there 50 and above stay at full and only lower levels turn it down.</para>
/// </summary>
public static class BuddyVolume
{
    public const int Default = 50;

    /// <summary>The setting as a WPF player volume, 0–1 (50 → 0.5). A value outside 0–100 counts as its nearest end.</summary>
    public static double Level(int setting) => Math.Clamp(setting, 0, 100) / 100.0;

    /// <summary>The setting as the mic window's page volume, 0–1: twice <see cref="Level"/>, capped (50 and above → 1).</summary>
    public static double PageLevelOf(int setting) => Math.Min(1.0, Level(setting) * 2);

    /// <summary>What the mic window's page plays at now (read by ChromeDictationService for each state it sends).</summary>
    public static double PageLevel { get; private set; } = 1.0;

    /// <summary>Pass the saved level to every player. From the UI thread: the players that are sounding follow at once.</summary>
    public static void Apply(int setting)
    {
        double level = Level(setting);
        CluadexBuddy.SoundVolume = level;
        VoicePlayer.MasterVolume = level;
        PageLevel = PageLevelOf(setting);
    }
}
