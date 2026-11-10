using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using CluadeX.Services;

namespace CluadeX.Views;

/// <summary>
/// น้อง CluadeX's face in the top-left corner (any size: she is drawn in a Viewbox), moving with what CluadeX does (<see cref="BuddyMood"/>):
/// idle — breathes and blinks now and then; working — a small spinner at her shoulder while she bobs and sways;
/// waiting — head cocked, an amber glow pulsing and a "?" bouncing; happy — a hop and a sparkle, once;
/// sad — a shake and a red "!"; asleep — eyes closed, head down, slow breaths and z's floating up.
/// <para>Each mood is one storyboard begun on this control at 10–24 fps. It is paused while the window is minimised or
/// the control hidden, and — when she is idle or asleep — while another app has the focus, so an idle CluadeX asks for
/// no frames.</para>
/// </summary>
public partial class CluadexBuddy : UserControl
{
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(string), typeof(CluadexBuddy), new PropertyMetadata(BuddyMood.Idle, (d, _) => ((CluadexBuddy)d).Apply()));

    /// <summary>A <see cref="BuddyMood"/> value (bound to ChatViewModel.BuddyMoodState).</summary>
    public string Mood
    {
        get => (string)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    private Storyboard? _loop;
    private Window? _window;
    private bool _paused;

    /// <summary>The mood whose motion is set up now (tests).</summary>
    public string Shown { get; private set; } = "";

    /// <summary>A storyboard of hers is begun and not paused (tests and the idle-cost check).</summary>
    public bool IsAnimating => _loop != null && !_paused;

    public CluadexBuddy()
    {
        InitializeComponent();
        SetBinding(MoodProperty, new System.Windows.Data.Binding("BuddyMoodState") { FallbackValue = BuddyMood.Idle });
        Loaded += (_, _) => { HookWindow(); HookDisplay(); Apply(); StartAway(); };
        Unloaded += (_, _) => { StopAway(); UnhookDisplay(); Stop(); HideClips(); };
        IsVisibleChanged += (_, _) => SyncPause();
        SizeChanged += (_, _) => UpdateSlideClip();
    }

    private void HookWindow()
    {
        if (_window != null || Window.GetWindow(this) is not { } w) return;
        _window = w;
        w.Activated += (_, _) => { SyncPause(); OnFocusChanged(); };
        w.Deactivated += (_, _) => { SyncPause(); OnFocusChanged(); };
        w.StateChanged += (_, _) => SyncPause();
    }

    private void Stop()
    {
        if (_loop == null) return;
        try { _loop.Remove(this); } catch (InvalidOperationException) { }
        _loop = null;
        _paused = false;
    }

    /// <summary>Paused when nobody could see it, and — when she has nothing to do — while CluadeX is in the background
    /// (except while she calls out for the owner, CluadexBuddy.Away.cs).</summary>
    private void SyncPause()
    {
        if (_loop == null) return;
        bool calm = Shown is BuddyMood.Idle or BuddyMood.Sleep;
        bool pause = !IsVisible || _window is { WindowState: WindowState.Minimized } || (calm && !_forcePlay && _window is { IsActive: false });
        if (pause == _paused) return;
        try { if (pause) _loop.Pause(this); else _loop.Resume(this); }
        catch (InvalidOperationException) { return; }
        _paused = pause;
        SyncClipPause(pause);
    }

    private void Apply()
    {
        if (!IsLoaded) return;
        string mood = Mood ?? BuddyMood.Idle;
        if (mood == Shown && _loop != null) return;
        Stop();
        Reset();
        string previous = Shown;
        Shown = mood;
        // Her clip for the mood, when there is one: then the clip moves her and the storyboard keeps only the glow, the
        // chips, the sparkle and the z's; without one the drawn motion (breathing, bobbing, tilting, blinking) runs as before.
        bool live = StartMoodClip(mood, previous);
        // The slideshow is a picture in her plate, not her cut-out: her head's motion never reaches it (it used to tilt the
        // slide 6° out of the plate when she fell asleep). It breathes inside its clip instead (SlideZoom, never below 1).
        bool slides = SlidesOn;
        bool drawn = !live && !slides;
        var sb = new Storyboard();
        // Distances are in her 120-unit canvas (the Viewbox scales them with her).
        switch (mood)
        {
            case BuddyMood.Working:
                WorkChip.Visibility = Visibility.Visible;
                Glow.SetResourceReference(Shape.FillProperty, "Accent.1");
                Forever(sb, Glow, OpacityProperty, 0.22, 0.48, 0.9);
                Forever(sb, ArcSpin, RotateTransform.AngleProperty, 0, 360, 1.1, reverse: false);
                if (drawn)
                {
                    Forever(sb, Bob, TranslateTransform.YProperty, 0, -2.5, 0.45);
                    Forever(sb, Tilt, RotateTransform.AngleProperty, -2.5, 2.5, 1.7);
                    Blink(sb, 3.2);
                }
                Timeline.SetDesiredFrameRate(sb, 24);
                break;
            case BuddyMood.Waiting:
                Glow.SetResourceReference(Shape.FillProperty, "Status.Warning");
                AskChip.Visibility = Visibility.Visible;
                if (drawn) Tilt.Angle = 5;   // head cocked: "yes?"
                Forever(sb, Glow, OpacityProperty, 0.2, 0.55, 0.8);
                Forever(sb, AskHop, TranslateTransform.YProperty, 0, -6, 0.4);
                if (drawn) Blink(sb, 3.6);
                Timeline.SetDesiredFrameRate(sb, 20);
                break;
            case BuddyMood.Happy:
                Glow.SetResourceReference(Shape.FillProperty, "Status.Success");
                Glow.Opacity = 0.5;
                if (drawn) Once(sb, Bob, TranslateTransform.YProperty, (0, 0), (0.15, -9), (0.3, 0), (0.45, -5), (0.6, 0));
                if (slides) SlidePulse(sb, 1.04);
                Once(sb, Sparkle, OpacityProperty, (0, 0), (0.2, 1), (0.9, 1), (1.3, 0));
                Once(sb, SparkleScale, ScaleTransform.ScaleXProperty, (0, 0.3), (0.25, 1.2), (0.5, 0.9));
                Once(sb, SparkleScale, ScaleTransform.ScaleYProperty, (0, 0.3), (0.25, 1.2), (0.5, 0.9));
                Timeline.SetDesiredFrameRate(sb, 30);
                break;
            case BuddyMood.Sad:
                Glow.SetResourceReference(Shape.FillProperty, "Status.Danger");
                Glow.Opacity = 0.45;
                SadChip.Visibility = Visibility.Visible;
                if (drawn) Once(sb, Tilt, RotateTransform.AngleProperty, (0, 0), (0.08, -5), (0.18, 5), (0.28, -3.5), (0.38, 3), (0.5, 0));
                Timeline.SetDesiredFrameRate(sb, 30);
                break;
            case BuddyMood.Sleep:
                FaceAsleep.Opacity = 1;
                FaceAwake.Opacity = 0;
                Glow.Opacity = 0.16;
                if (drawn)
                {
                    Tilt.Angle = 6;   // nodding off
                    Forever(sb, Breathe, ScaleTransform.ScaleXProperty, 1, 1.02, 1.9);
                    Forever(sb, Breathe, ScaleTransform.ScaleYProperty, 1, 1.02, 1.9);
                }
                if (slides) SlideBreathe(sb, 1.02, 1.9);
                FloatZ(sb, Z1, Z1Rise, 0);
                FloatZ(sb, Z2, Z2Rise, 1.4);
                Timeline.SetDesiredFrameRate(sb, 10);
                break;
            default:   // idle
                if (drawn)
                {
                    Forever(sb, Breathe, ScaleTransform.ScaleXProperty, 1, 1.015, 1.6);
                    Forever(sb, Breathe, ScaleTransform.ScaleYProperty, 1, 1.015, 1.6);
                    Blink(sb, 4.2);
                }
                if (slides) SlideBreathe(sb, 1.015, 1.6);
                Forever(sb, GlowScale, ScaleTransform.ScaleXProperty, 0.96, 1.04, 2.4);
                Forever(sb, GlowScale, ScaleTransform.ScaleYProperty, 0.96, 1.04, 2.4);
                Timeline.SetDesiredFrameRate(sb, 12);
                break;
        }
        _loop = sb;
        try { sb.Begin(this, HandoffBehavior.SnapshotAndReplace, isControllable: true); }
        catch (InvalidOperationException) { _loop = null; return; }
        SyncPause();
    }

    /// <summary>Back to the plain face: nothing turned, nothing shown, the accent glow.</summary>
    private void Reset()
    {
        WorkChip.Visibility = AskChip.Visibility = SadChip.Visibility = Visibility.Collapsed;
        Glow.SetResourceReference(Shape.FillProperty, "Accent.2");
        Glow.Opacity = 0.32;
        GlowScale.ScaleX = GlowScale.ScaleY = 1;
        FaceAsleep.Opacity = Sparkle.Opacity = Z1.Opacity = Z2.Opacity = 0;
        FaceAwake.Opacity = 1;
        Tilt.Angle = 0;
        Bob.Y = 0;
        Breathe.ScaleX = Breathe.ScaleY = 1;
        SlideZoom.ScaleX = SlideZoom.ScaleY = 1;
    }

    /// <summary>The slide breathes: grows to <paramref name="to"/> and back, about the face, inside its clip (never below 1,
    /// so its cut edges never come inside her plate).</summary>
    private void SlideBreathe(Storyboard sb, double to, double seconds)
    {
        Forever(sb, SlideZoom, ScaleTransform.ScaleXProperty, 1, to, seconds);
        Forever(sb, SlideZoom, ScaleTransform.ScaleYProperty, 1, to, seconds);
    }

    /// <summary>One small swell of the slide (a turn finished well), back to 1.</summary>
    private void SlidePulse(Storyboard sb, double peak)
    {
        Once(sb, SlideZoom, ScaleTransform.ScaleXProperty, (0, 1), (0.18, peak), (0.6, 1));
        Once(sb, SlideZoom, ScaleTransform.ScaleYProperty, (0, 1), (0.18, peak), (0.6, 1));
    }

    private static void Forever(Storyboard sb, DependencyObject target, DependencyProperty prop, double from, double to,
                                double seconds, bool reverse = true)
    {
        var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = reverse,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = reverse ? new SineEase { EasingMode = EasingMode.EaseInOut } : null,
        };
        Add(sb, a, target, prop);
    }

    private static void Once(Storyboard sb, DependencyObject target, DependencyProperty prop, params (double At, double Value)[] keys)
    {
        var a = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        foreach (var (at, v) in keys)
            a.KeyFrames.Add(new EasingDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at)), new SineEase()));
        Add(sb, a, target, prop);
    }

    /// <summary>Eyes shut for a moment at the end of every <paramref name="period"/> seconds. The two stills are not drawn in
    /// quite the same pose, so the open-eyed one goes out while the closed-eyed one shows: stacked, her open-eyed outline
    /// peeked out around the other as a grey rim (owner 2026-11-08: "ทำให้เนียนกว่านี้").</summary>
    private void Blink(Storyboard sb, double period)
    {
        foreach (var (face, shut) in new[] { (FaceAsleep, 1.0), (FaceAwake, 0.0) })
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(period), RepeatBehavior = RepeatBehavior.Forever };
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(1 - shut, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(shut, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(period - 0.2))));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(1 - shut, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(period - 0.06))));
            Add(sb, a, face, OpacityProperty);
        }
    }

    /// <summary>A "z" that fades in, drifts up and fades out, every 2.8 s, starting <paramref name="delay"/> s in.</summary>
    private static void FloatZ(Storyboard sb, UIElement z, TranslateTransform rise, double delay)
    {
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.8), RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(delay) };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.7))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.4))));
        Add(sb, fade, z, OpacityProperty);
        var up = new DoubleAnimation(4, -16, TimeSpan.FromSeconds(2.8)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(delay) };
        Add(sb, up, rise, TranslateTransform.YProperty);
    }

    private static void Add(Storyboard sb, Timeline a, DependencyObject target, DependencyProperty prop)
    {
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, new PropertyPath(prop));
        sb.Children.Add(a);
    }
}
