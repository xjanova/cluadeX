namespace CluadeX.Services.Gpu;

/// <summary>Who tells the owner about the slider: the pet (when it is out and talks) or a tooltip on the graph.</summary>
public enum GpuShareHintChannel { Tooltip, Pet }

/// <summary>
/// The GPU share as stored in AppSettings, plus the first-use hint's memory. No WPF here, so the harness drives it
/// against a real SettingsService (portable.txt keeps it off ~/.cluadex).
/// </summary>
public sealed class GpuShareSettings
{
    private readonly SettingsService _settings;
    public GpuShareSettings(SettingsService settings) => _settings = settings;

    public int Percent => GpuShare.Clamp(_settings.Settings.GpuUsagePercent);
    public bool Used => _settings.Settings.GpuShareHintDone;
    public int HintShown => _settings.Settings.GpuShareHintShown;

    /// <summary>Store a new share. Moving the slider is "using" it: the hint never comes back.</summary>
    public void SetPercent(int percent)
    {
        int p = GpuShare.Clamp(percent);
        if (p == _settings.Settings.GpuUsagePercent && _settings.Settings.GpuShareHintDone) return;
        _settings.UpdateSettings(s => { s.GpuUsagePercent = p; s.GpuShareHintDone = true; });
    }

    /// <summary>The popup was opened, or the hint closed with "Got it" — either way the owner knows now.</summary>
    public void MarkUsed()
    {
        if (!_settings.Settings.GpuShareHintDone) _settings.UpdateSettings(s => s.GpuShareHintDone = true);
    }

    public bool HintDue(TimeSpan graphVisibleFor, DateTime nowUtc)
    {
        var s = _settings.Settings;
        return GpuShareHint.ShouldShow(s.GpuShareHintDone, s.GpuShareHintShown, s.GpuShareHintLastUtc, graphVisibleFor, nowUtc);
    }

    public void NoteHintShown(DateTime nowUtc)
        => _settings.UpdateSettings(s => { s.GpuShareHintShown++; s.GpuShareHintLastUtc = nowUtc; });

    /// <summary>The pet says it when it is enabled, out on the screen and allowed to talk; otherwise the tooltip.</summary>
    public static GpuShareHintChannel ChooseChannel(bool petEnabled, bool petOut, bool petTalks)
        => petEnabled && petOut && petTalks ? GpuShareHintChannel.Pet : GpuShareHintChannel.Tooltip;
}
