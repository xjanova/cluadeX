using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services;
using CluadeX.ViewModels;

namespace CluadeX.Views.Setup;

/// <summary>What the person chose in <see cref="UpdateReadyWindow"/>.</summary>
public enum UpdateReadyChoice { Later, Now, LinkAccount }

/// <summary>
/// "A new version is ready" (design board UpdateReady), shown when the banner's Install &amp; Restart is pressed on a
/// staged update: current → new version, what is new (up to four lines of the release notes), the auto-update switch
/// (enabled only when this install may update by itself: a paid/trial license or a linked account), and
/// Update and restart / Later / all the details. Esc and the X mean Later. It only asks — the caller installs.
/// </summary>
public partial class UpdateReadyWindow : Window
{
    private readonly SettingsService? _settings;
    private readonly bool _allowed;
    private bool _ready;

    public UpdateReadyChoice Choice { get; private set; } = UpdateReadyChoice.Later;

    /// <param name="from">The running version.</param>
    /// <param name="to">The staged version.</param>
    /// <param name="notes">Release notes markdown (null or empty → the generic line).</param>
    /// <param name="sizeBytes">The downloaded package size, 0 when unknown.</param>
    /// <param name="allowed">This install may update by itself (<see cref="AutoUpdateEntitlement.Allowed(ActivationService?, Models.AppSettings)"/>).</param>
    /// <param name="settings">Where the switch is saved; null in the test renders.</param>
    public UpdateReadyWindow(string from, string to, string? notes, long sizeBytes, bool allowed, SettingsService? settings)
    {
        InitializeComponent();
        FontFamily = TypographyService.BundledFamily;
        _settings = settings;
        _allowed = allowed;

        SubText.Text = L("upd.sub");
        FromText.Text = "v" + from.TrimStart('v');
        ToText.Text = "v" + to.TrimStart('v');
        DateText.Text = sizeBytes > 0 ? $"{sizeBytes / 1048576.0:0.0} MB" : "";
        var bullets = AutoUpdateService.ReleaseNotesBullets(notes, 4);
        Notes.ItemsSource = bullets.Count > 0 ? bullets : new[] { L("upd.noNotes") };

        AutoSwitch.IsEnabled = allowed;
        AutoSwitch.IsChecked = allowed && (settings?.Settings.AutoUpdateEnabled ?? true);
        LinkRow.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;

        string? girl = GirlArt.PickName();
        GirlImage.Source = GirlArt.PoseImage(girl, 2);
        if (GirlImage.Source == null) GirlImage.Visibility = Visibility.Collapsed;
        else GirlGlowFx.Color = GirlArt.Glow(girl);

        Root.SizeChanged += (_, _) => Root.Clip = new RectangleGeometry(new Rect(0, 0, Root.ActualWidth, Root.ActualHeight), 16, 16);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Finish(UpdateReadyChoice.Later); }
        };
        Loaded += (_, _) => { _ready = true; UpdateButton.Focus(); };
    }

    private static string L(string key) => LocalizedResources.Instance[key];

    private void Finish(UpdateReadyChoice choice)
    {
        Choice = choice;
        if (IsLoaded) Close();
    }

    private void Update_Click(object sender, RoutedEventArgs e) => Finish(UpdateReadyChoice.Now);
    private void Later_Click(object sender, RoutedEventArgs e) => Finish(UpdateReadyChoice.Later);
    private void Link_Click(object sender, RoutedEventArgs e) => Finish(UpdateReadyChoice.LinkAccount);

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AutoUpdateService.LatestReleasePage) { UseShellExecute = true }); }
        catch (Exception ex) { CommandErrorSink.Report("UpdateReady.Details", ex); }
    }

    /// <summary>The switch saves at once (only when this install may update by itself — otherwise it cannot move).</summary>
    private void AutoSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || !_allowed || _settings == null) return;
        bool on = AutoSwitch.IsChecked == true;
        if (_settings.Settings.AutoUpdateEnabled == on) return;
        _settings.Settings.AutoUpdateEnabled = on;
        try { _settings.Save(); }
        catch (Exception ex) { CommandErrorSink.Report("UpdateReady.Switch", ex); }
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindButton(d)) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private static bool FindButton(DependencyObject d)
    {
        for (var p = d; p != null; p = p is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (p is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.ToggleButton) return true;
        return false;
    }
}
