using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
    private readonly string? _notes;
    private readonly Func<CancellationToken, Task<List<ReleaseNoteEntry>>>? _loadNotes;
    private readonly CancellationTokenSource _life = new();
    private bool _ready, _expanded, _notesLoaded;

    public UpdateReadyChoice Choice { get; private set; } = UpdateReadyChoice.Later;

    /// <param name="from">The running version.</param>
    /// <param name="to">The staged version.</param>
    /// <param name="notes">Release notes markdown (null or empty → the generic line).</param>
    /// <param name="sizeBytes">The downloaded package size, 0 when unknown.</param>
    /// <param name="allowed">This install may update by itself (<see cref="AutoUpdateEntitlement.Allowed(ActivationService?, Models.AppSettings)"/>).</param>
    /// <param name="settings">Where the switch is saved; null in the test renders.</param>
    /// <param name="loadNotes">"All the details": the notes of every version in between (AutoUpdateService.FetchReleaseNotesAsync);
    /// null = only the notes that came with the update.</param>
    public UpdateReadyWindow(string from, string to, string? notes, long sizeBytes, bool allowed, SettingsService? settings,
        Func<CancellationToken, Task<List<ReleaseNoteEntry>>>? loadNotes = null)
    {
        InitializeComponent();
        FontFamily = TypographyService.BundledFamily;
        _settings = settings;
        _allowed = allowed;
        _notes = notes;
        _loadNotes = loadNotes;
        Closed += (_, _) => _life.Cancel();

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

    /// <summary>"All the details": the notes of every version between this one and the new one open inside this window
    /// (owner 2026-11-09: the details belong in the updater, taken from xman4289.com — never a link to GitHub). Pressed
    /// again: back to the four lines. Without the site: the notes that came with the update, in full.</summary>
    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        if (_expanded)
        {
            _expanded = false;
            AllNotesScroll.Visibility = Visibility.Collapsed;
            Notes.Visibility = Visibility.Visible;
            DetailsButton.Content = L("upd.details");
            return;
        }
        _expanded = true;
        Notes.Visibility = Visibility.Collapsed;
        AllNotesScroll.Visibility = Visibility.Visible;
        DetailsButton.Content = L("upd.lessNotes");
        if (_notesLoaded) return;

        AllNotesPanel.Children.Clear();
        AllNotesPanel.Children.Add(NoteLine(L("upd.notesLoading"), dim: true));
        List<ReleaseNoteEntry> entries = new();
        try
        {
            if (_loadNotes != null) entries = await _loadNotes(_life.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { CommandErrorSink.Report("UpdateReady.Details", ex); }
        if (!IsLoaded) return;
        _notesLoaded = entries.Count > 0;
        ShowAllNotes(entries);
    }

    private void ShowAllNotes(IReadOnlyList<ReleaseNoteEntry> entries)
    {
        AllNotesPanel.Children.Clear();
        if (entries.Count == 0)
        {
            // the site could not be read: what came with the update, every line of it
            var staged = AutoUpdateService.ReleaseNotesBullets(_notes, 40);
            AllNotesPanel.Children.Add(NoteLine(L(staged.Count > 0 ? "upd.notesOffline" : "upd.noNotes"), dim: true));
            foreach (var b in staged) AllNotesPanel.Children.Add(Bullet(b));
            return;
        }
        foreach (var entry in entries)
        {
            string date = entry.ReleasedAt is { } d ? " · " + d.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentUICulture) : "";
            var head = new TextBlock
            {
                Text = "v" + entry.Version + date,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("Muted"),
                Margin = new Thickness(0, AllNotesPanel.Children.Count == 0 ? 2 : 12, 0, 2),
            };
            AllNotesPanel.Children.Add(head);
            var bullets = AutoUpdateService.ReleaseNotesBullets(entry.Notes, 30);
            if (bullets.Count == 0) AllNotesPanel.Children.Add(NoteLine(L("upd.noNotes"), dim: true));
            foreach (var b in bullets) AllNotesPanel.Children.Add(Bullet(b));
        }
    }

    private FrameworkElement Bullet(string text)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        g.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 6, Height = 6, Fill = new SolidColorBrush(Color.FromRgb(0xF0, 0xAB, 0xFC)),
            Margin = new Thickness(0, 8, 12, 0), VerticalAlignment = VerticalAlignment.Top,
        };
        var t = new TextBlock { Text = text, Style = (Style)FindResource("Bullet") };
        System.Windows.Controls.Grid.SetColumn(t, 1);
        g.Children.Add(dot);
        g.Children.Add(t);
        return g;
    }

    private TextBlock NoteLine(string text, bool dim) => new()
    {
        Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4),
        Foreground = (Brush)FindResource(dim ? "Dim" : "Muted"),
    };

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
