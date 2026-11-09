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
        SizeChanged += (_, e) => { if (e.WidthChanged) Fit(e.NewSize.Width); };
    }

    /// <summary>Below this width the girl stands above the settings, not beside them.</summary>
    public const double NarrowBelow = 640;

    /// <summary>True while the girl stands above the settings (a narrow settings card).</summary>
    public bool IsNarrow { get; private set; }

    // The settings grid puts this panel in a card of a few hundred pixels: 166 px of girl beside the settings
    // left the labels one letter wide (2026-11-09). The panel's width comes from its card, never from what
    // is in it, so switching here cannot loop.
    private void Fit(double width)
    {
        bool narrow = width > 0 && width < NarrowBelow;
        if (narrow == IsNarrow && MascotSide.Visibility != MascotTop.Visibility) return;
        IsNarrow = narrow;
        MascotTop.Visibility = narrow ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        MascotSide.Visibility = narrow ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }

    // Show / hide the ElevenLabs key (a plain Button: the theme has no ToggleButton look).
    private void ToggleKey_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.VoiceSettingsViewModel vm) vm.ShowElevenKey = !vm.ShowElevenKey;
    }
}
