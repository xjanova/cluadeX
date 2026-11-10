using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CluadeX.Services;
using CluadeX.Services.Localization;
using CluadeX.Views;

namespace CluadeX;

/// <summary>
/// น้อง CluadeX's speech bubble (owner 2026-11-08: "บางทีก็มีบับเบิ้ล แนะนำลอยออกมาได้"). It floats out from her cheek for a
/// few seconds and a click closes it:
/// <list type="bullet">
/// <item><b>A tip</b> (<see cref="BuddyBubbleKeys.Tips"/>, one after another) — the first <see cref="FirstTipAfter"/> after
/// start, then every <see cref="TipEvery"/>, only while she is idle beside the chat list and CluadeX is the active window.
/// She plays her "suggest" clip with it.</item>
/// <item><b>A word</b> when a turn finishes, fails or waits for the owner — at most once per <see cref="SayEvery"/> each, and
/// only while CluadeX is the active window (the run badges and her face already say it otherwise).</item>
/// </list>
/// Off with AppSettings.BuddyBubbles (Settings › General › Appearance).
/// </summary>
public partial class MainWindow
{
    public static readonly TimeSpan FirstTipAfter = TimeSpan.FromMinutes(4);
    public static readonly TimeSpan TipEvery = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan SayEvery = TimeSpan.FromMinutes(2);

    private DispatcherTimer? _tipTimer, _bubbleTimer, _typingTimer;
    private DateTime _nextTipAt = DateTime.UtcNow + FirstTipAfter;
    private int _tipIndex;
    private readonly Dictionary<string, DateTime> _saidAt = new();
    private string _bubbleMood = "";

    /// <summary>The text she is saying now ("" = no bubble), for tests.</summary>
    public string BuddyBubbleSaying => BuddyBubble.Visibility == Visibility.Visible ? BuddyBubbleText.Text : "";

    private bool BubblesOn => _viewModel.Settings.Settings.BuddyBubbles;

    private void InitBuddyBubble()
    {
        BuddyBubble.MouseLeftButtonUp += (_, e) => { HideBuddyBubble(); e.Handled = true; };
        BuddyBubble.SizeChanged += (_, _) => PlaceBuddyBubble();
        _tipTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _tipTimer.Tick += (_, _) => MaybeTip();
        _tipTimer.Start();
        _bubbleMood = _viewModel.ChatVM.BuddyMoodState;
        _viewModel.ChatVM.PropertyChanged += OnBuddyMoodChanged;
        Deactivated += (_, _) => HideBuddyBubble();
        // her sound follows the setting, live (CluadexBuddy.SoundsOn)
        SyncBuddySounds();
        _viewModel.Settings.SettingsChanged += SyncBuddySounds;
        // she listens while the owner types (her "listening" clips), and goes back to idle 4 s after the last key
        _typingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _typingTimer.Tick += (_, _) => { _typingTimer.Stop(); HeroBuddy()?.SetListening(false); };
        Closed += (_, _) =>
        {
            _tipTimer?.Stop();
            _bubbleTimer?.Stop();
            _typingTimer?.Stop();
            _viewModel.ChatVM.PropertyChanged -= OnBuddyMoodChanged;
            _viewModel.Settings.SettingsChanged -= SyncBuddySounds;
        };
    }

    /// <summary>Her sound and how she shows (clips, stills, slides) follow the settings, live.</summary>
    private void SyncBuddySounds()
    {
        CluadexBuddy.SoundsOn = _viewModel.Settings.Settings.BuddySounds;
        CluadexBuddy.Display = _viewModel.Settings.Settings.BuddyDisplay;
    }

    /// <summary>Her right-click menu: sound, and clips / stills / the girls' slideshow. The ticks show what is on now.</summary>
    private void BuddyMenu_Opened(object sender, RoutedEventArgs e)
    {
        BuddyMenuSound.IsChecked = _viewModel.SettingsVM.BuddySounds;
        BuddyMenuClips.IsChecked = CluadexBuddy.Display == CluadexBuddy.DisplayClips;
        BuddyMenuStills.IsChecked = CluadexBuddy.Display == CluadexBuddy.DisplayStills;
        BuddyMenuSlides.IsChecked = CluadexBuddy.Display == CluadexBuddy.DisplaySlides;
    }

    private void BuddyMenuSound_Click(object sender, RoutedEventArgs e) =>
        _viewModel.SettingsVM.BuddySounds = !_viewModel.SettingsVM.BuddySounds;

    private void BuddyMenuDisplay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string mode }) return;
        SetBuddyDisplay(mode);
    }

    /// <summary>Clips, stills or slides: saved (kept after a restart) and passed to her at once.</summary>
    internal void SetBuddyDisplay(string mode)
    {
        if (_viewModel.Settings.Settings.BuddyDisplay != mode)
            try { _viewModel.Settings.UpdateSettings(s => s.BuddyDisplay = mode); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Buddy] display: {ex.Message}"); }
        CluadexBuddy.Display = mode;
    }

    /// <summary>The speaker in her plate's corner: her sound off or on, saved (the Settings page's box follows).</summary>
    private void BuddySoundToggle_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;   // not a click on her: no Home, no startle
        _viewModel.SettingsVM.BuddySounds = !_viewModel.SettingsVM.BuddySounds;
    }

    /// <summary>How often a click on her makes her jump (owner 2026-11-08: "คลิ๊กที่เธอมีการตกใจได้ในบางครั้ง" — sometimes,
    /// not every time), and the least time between two.</summary>
    public const double StartleChance = 0.3;
    public static readonly TimeSpan StartleGap = TimeSpan.FromSeconds(10);
    private static readonly Random StartleDice = new();
    private DateTime _lastStartleAt = DateTime.MinValue;

    /// <summary>A click on her (it also goes Home, BuddyHero's command): now and then she jumps.</summary>
    private void BuddyHero_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != BuddyHero) return;
        if (DateTime.UtcNow - _lastStartleAt < StartleGap || StartleDice.NextDouble() >= StartleChance) return;
        _lastStartleAt = DateTime.UtcNow;
        // after Home has run (it may change her mood, which would replace the startle at once)
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => HeroBuddy()?.Startle());
    }

    private void OnBuddyMoodChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.ChatViewModel.UserInput))
        {
            bool typing = !string.IsNullOrEmpty(_viewModel.ChatVM.UserInput);
            HeroBuddy()?.SetListening(typing);
            _typingTimer?.Stop();
            if (typing) _typingTimer?.Start();
            return;
        }
        if (e.PropertyName != nameof(ViewModels.ChatViewModel.BuddyMoodState)) return;
        string mood = _viewModel.ChatVM.BuddyMoodState;
        if (mood == _bubbleMood) return;
        _bubbleMood = mood;
        string? key = mood switch
        {
            BuddyMood.Happy => "buddy.say.happy",
            BuddyMood.Sad => "buddy.say.sad",
            BuddyMood.Waiting => "buddy.say.waiting",
            _ => null,
        };
        if (key == null)
        {
            // she moved on (working again, asleep …): a word about the old mood is stale
            if (mood != BuddyMood.Idle && _saidKey is "buddy.say.happy" or "buddy.say.sad" or "buddy.say.waiting") HideBuddyBubble();
            return;
        }
        if (!CanSpeak(needsList: false)) return;
        if (_saidAt.TryGetValue(key, out var last) && DateTime.UtcNow - last < SayEvery) return;
        _saidAt[key] = DateTime.UtcNow;
        ShowBuddyBubble(key);
    }

    private void MaybeTip()
    {
        if (DateTime.UtcNow < _nextTipAt) return;
        if (!CanSpeak(needsList: true) || _viewModel.ChatVM.BuddyMoodState != BuddyMood.Idle || BuddyBubble.Visibility == Visibility.Visible)
            return;
        _nextTipAt = DateTime.UtcNow + TipEvery;
        string key = BuddyBubbleKeys.Tips[_tipIndex++ % BuddyBubbleKeys.Tips.Length];
        ShowBuddyBubble(key);
        HeroBuddy()?.Suggest();
    }

    /// <summary>On, CluadeX is the active window and not minimised, and — for tips — the chat list is beside her (on the
    /// other pages she is rail-sized and a bubble would sit on the page).</summary>
    private bool CanSpeak(bool needsList) =>
        BubblesOn && IsActive && WindowState != WindowState.Minimized && BuddyHero.IsVisible
        && (!needsList || ChatSidebar.IsVisible);

    private CluadexBuddy? HeroBuddy() => BuddyHero.Template?.FindName("Buddy", BuddyHero) as CluadexBuddy;

    private string? _saidKey;

    /// <summary>She says <paramref name="key"/> (a localization key) for a few seconds.</summary>
    internal void ShowBuddyBubble(string key)
    {
        string text = LocalizedResources.Instance[key];
        _saidKey = key;
        BuddyBubbleText.Text = text;
        BuddyBubble.BeginAnimation(OpacityProperty, null);
        BuddyBubble.Visibility = Visibility.Visible;
        PlaceBuddyBubble();
        if (SystemParameters.ClientAreaAnimation)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var t = TimeSpan.FromMilliseconds(220);
            BuddyBubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, t));
            BuddyBubbleScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, 1, t) { EasingFunction = ease });
            BuddyBubbleScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, t) { EasingFunction = ease });
            BuddyBubbleRise.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(8, 0, t) { EasingFunction = ease });
        }
        else BuddyBubble.Opacity = 1;
        if (UIElementAutomationPeer.CreatePeerForElement(BuddyBubble) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        // long enough to read: 4 s plus a little per character, 5–11 s
        double secs = Math.Clamp(4 + text.Length * 0.06, 5, 11);
        _bubbleTimer?.Stop();
        _bubbleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(secs) };
        _bubbleTimer.Tick += (_, _) => HideBuddyBubble();
        _bubbleTimer.Start();
    }

    internal void HideBuddyBubble()
    {
        _bubbleTimer?.Stop();
        _saidKey = null;
        if (BuddyBubble.Visibility != Visibility.Visible) return;
        if (!SystemParameters.ClientAreaAnimation || !IsActive) { BuddyBubble.Visibility = Visibility.Collapsed; return; }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => { if (_saidKey == null) BuddyBubble.Visibility = Visibility.Collapsed; };
        BuddyBubble.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Right of her face, its tail at her cheek: the bubble's bottom-left corner sits just below her face (her face
    /// is about 0.85 of her width down her 1:2 frame).</summary>
    private void PlaceBuddyBubble()
    {
        if (BuddyBubble.Visibility != Visibility.Visible) return;
        double s = BuddyHeroSize > 0 ? BuddyHeroSize : HeroBig;
        Canvas.SetLeft(BuddyBubble, s - 10);
        Canvas.SetTop(BuddyBubble, Math.Max(4, s * 0.95 - BuddyBubble.ActualHeight));
    }
}
