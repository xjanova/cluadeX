using System.Windows.Controls;

namespace CluadeX.Views.Settings;

/// <summary>Settings › Voice › who reads the replies aloud. All behaviour lives in VoiceSettingsViewModel.</summary>
public partial class VoiceSettingsPanel : UserControl
{
    public VoiceSettingsPanel()
    {
        InitializeComponent();
        // Leaving the page saves what is still waiting and stops a test sound.
        Unloaded += (_, _) => (DataContext as ViewModels.VoiceSettingsViewModel)?.Close();
        Loaded += (_, _) => (DataContext as ViewModels.VoiceSettingsViewModel)?.Load();
    }

    // Show / hide the ElevenLabs key (a plain Button: the theme has no ToggleButton look).
    private void ToggleKey_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.VoiceSettingsViewModel vm) vm.ShowElevenKey = !vm.ShowElevenKey;
    }
}
