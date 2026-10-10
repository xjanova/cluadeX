using System.Windows.Controls;

namespace CluadeX.Views.Settings;

/// <summary>The theme picker card content in Settings › General. All behaviour lives in SettingsViewModel
/// (ThemeOptions / SelectThemeCommand) and ThemeService.</summary>
public partial class ThemePicker : UserControl
{
    public ThemePicker()
    {
        InitializeComponent();
    }
}
