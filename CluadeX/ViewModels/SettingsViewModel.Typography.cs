using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>One entry of the font list in Settings › General › Text size and font.</summary>
public sealed class FontChoice : ViewModelBase
{
    public FontChoice(string id, FontFamily preview, string label, bool isHeader = false)
    {
        Id = id;
        Preview = preview;
        _label = label;
        IsHeader = isHeader;
    }

    /// <summary>The value stored in AppSettings.UiFont ("" = theme, "bundled", or an installed family name).</summary>
    public string Id { get; }

    /// <summary>The family the entry's own name is drawn in, so the list shows what each font looks like.</summary>
    public FontFamily Preview { get; }

    /// <summary>A section label in the list ("Installed on this PC"), not something to pick.</summary>
    public bool IsHeader { get; }

    private string _label;
    public string Label { get => _label; set => SetProperty(ref _label, value); }
}

/// <summary>A preset button of the text size row.</summary>
public sealed class ScalePreset : ViewModelBase
{
    public ScalePreset(double scale, string key) { Scale = scale; Key = key; }
    public double Scale { get; }
    public string Key { get; }
    public string Percent => $"{Scale * 100:0} %";
    private string _label = "";
    public string Label { get => _label; set => SetProperty(ref _label, value); }
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public partial class SettingsViewModel
{
    private TypographyService? Typography => TypographyService.Current;

    /// <summary>น้อง CluadeX's speech bubble on or off (Appearance card); saved at once, read live by MainWindow.BuddyBubble.cs.</summary>
    public bool BuddyBubbles
    {
        get => _settingsService.Settings.BuddyBubbles;
        set
        {
            if (_settingsService.Settings.BuddyBubbles == value) return;
            try { _settingsService.UpdateSettings(s => s.BuddyBubbles = value); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Settings] buddy bubbles: {ex.Message}"); }
            OnPropertyChanged();
        }
    }

    /// <summary>น้อง CluadeX's sound on or off (Appearance card); saved at once, MainWindow passes it to her (CluadexBuddy.SoundsOn).</summary>
    public bool BuddySounds
    {
        get => _settingsService.Settings.BuddySounds;
        set
        {
            if (_settingsService.Settings.BuddySounds == value) return;
            try { _settingsService.UpdateSettings(s => s.BuddySounds = value); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Settings] buddy sounds: {ex.Message}"); }
            OnPropertyChanged();
        }
    }

    private readonly ObservableCollection<FontChoice> _fontChoices = new();
    private bool _fontsBuilt;

    /// <summary>Theme font, the bundled font, then every installed font — built the first time the Settings page asks,
    /// not at startup (reading the system's font list is not free).</summary>
    public ObservableCollection<FontChoice> FontChoices
    {
        get
        {
            if (!_fontsBuilt) { _fontsBuilt = true; BuildFontChoices(); }
            return _fontChoices;
        }
    }

    public ObservableCollection<ScalePreset> ScalePresets { get; } = new()
    {
        new(0.9, "typo.preset.small"), new(1.0, "typo.preset.normal"), new(1.15, "typo.preset.large"), new(1.3, "typo.preset.xlarge"),
    };

    private bool _typographyReady;

    /// <summary>Called once from the constructor: labels follow the language, the controls follow the service.</summary>
    private void InitTypography()
    {
        if (_typographyReady) return;
        _typographyReady = true;
        RefreshTypographyTexts();
        _localizationService.LanguageChanged += RefreshTypographyTexts;
        TypographyService.Changed += OnTypographyChanged;
    }

    private void BuildFontChoices()
    {
        _fontChoices.Clear();
        _fontChoices.Add(new FontChoice(TypographyService.ThemeFont, TypographyService.ThemeFamily(), T("typo.font.theme")));
        _fontChoices.Add(new FontChoice(TypographyService.BundledFont, TypographyService.BundledFamily, T("typo.font.bundled")));
        _fontChoices.Add(new FontChoice("\u0001header", new FontFamily("Segoe UI"), T("typo.font.installed"), isHeader: true));
        foreach (var name in TypographyService.InstalledFonts())
            _fontChoices.Add(new FontChoice(name, new FontFamily(name), name));
    }

    private void RefreshTypographyTexts()
    {
        foreach (var c in _fontChoices)
        {
            if (c.Id == TypographyService.ThemeFont) c.Label = T("typo.font.theme");
            else if (c.Id == TypographyService.BundledFont) c.Label = T("typo.font.bundled");
            else if (c.IsHeader) c.Label = T("typo.font.installed");
        }
        foreach (var p in ScalePresets) p.Label = T(p.Key);
        OnTypographyChanged();
    }

    private void OnTypographyChanged()
    {
        double s = Typography?.Scale ?? 1.0;
        foreach (var p in ScalePresets) p.IsSelected = Math.Abs(p.Scale - s) < 0.001;
        OnPropertyChanged(nameof(UiScalePercent));
        OnPropertyChanged(nameof(SelectedFontChoice));
        OnPropertyChanged(nameof(PreviewFontFamily));
        OnPropertyChanged(nameof(CanZoomOut));
        OnPropertyChanged(nameof(CanZoomIn));
    }

    /// <summary>"110 %" — the zoom on screen now.</summary>
    public string UiScalePercent => $"{(Typography?.Scale ?? 1.0) * 100:0} %";

    public bool CanZoomOut => (Typography?.Scale ?? 1.0) > TypographyService.MinScale + 0.001;
    public bool CanZoomIn => (Typography?.Scale ?? 1.0) < TypographyService.MaxScale - 0.001;

    private ICommand? _zoomOut, _zoomIn, _zoomReset, _zoomPreset, _openFontLicence;
    public ICommand ZoomOutCommand => _zoomOut ??= new RelayCommand(() => Typography?.SetScale(Typography.Scale - TypographyService.Step));
    public ICommand ZoomInCommand => _zoomIn ??= new RelayCommand(() => Typography?.SetScale(Typography.Scale + TypographyService.Step));
    public ICommand ZoomResetCommand => _zoomReset ??= new RelayCommand(() => Typography?.SetScale(1.0));
    public ICommand ZoomPresetCommand => _zoomPreset ??= new RelayCommand<ScalePreset>(p => { if (p != null) Typography?.SetScale(p.Scale); });

    /// <summary>The font in use, as a list entry. Setting it applies the font at once (a section header is ignored).</summary>
    public FontChoice? SelectedFontChoice
    {
        get
        {
            string id = Typography?.FontId ?? TypographyService.ThemeFont;
            return FontChoices.FirstOrDefault(c => !c.IsHeader && string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        }
        set
        {
            if (value == null || value.IsHeader) { OnPropertyChanged(); return; }
            Typography?.SetFont(value.Id);
            OnPropertyChanged();
        }
    }

    /// <summary>The family the preview box draws with: the chosen one, or the theme's.</summary>
    public FontFamily PreviewFontFamily => TypographyService.FamilyFor(Typography?.FontId ?? TypographyService.ThemeFont);

    /// <summary>Opens the OFL text that ships beside CluadeX.exe (Licenses\NotoSans-OFL.txt).</summary>
    public ICommand OpenFontLicenceCommand => _openFontLicence ??= new RelayCommand(() =>
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Licenses", "NotoSans-OFL.txt");
        if (!System.IO.File.Exists(path)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { /* no app for .txt: nothing to do */ }
    });
}
