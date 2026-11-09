namespace CluadeX.ViewModels;

public partial class SettingsViewModel
{
    private VoiceSettingsViewModel? _voice;
    /// <summary>Settings › Voice › who reads the replies aloud (engine, key, model, voice, cloning, each girl's voice).</summary>
    public VoiceSettingsViewModel Voice => _voice ??= new VoiceSettingsViewModel(_settingsService, T);
}
