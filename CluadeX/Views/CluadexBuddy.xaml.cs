using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using CluadeX.Services;

namespace CluadeX.Views;

/// <summary>
/// น้อง CluadeX's face in the activity bar, moving with what CluadeX does (<see cref="BuddyMood"/>):
/// idle — breathes and blinks now and then; working — a neon arc runs round her while she bobs and sways;
/// waiting — head cocked, an amber rim pulsing and a "?" bouncing; happy — a hop and a sparkle, once;
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
        Loaded += (_, _) => { HookWindow(); Apply(); };
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => SyncPause();
    }

    private void HookWindow()
    {
        if (_window != null || Window.GetWindow(this) is not { } w) return;
        _window = w;
        w.Activated += (_, _) => SyncPause();
        w.Deactivated += (_, _) => SyncPause();
        w.StateChanged += (_, _) => SyncPause();
    }

    private void Stop()
    {
        if (_loop == null) return;
        try { _loop.Remove(this); } catch (InvalidOperationException) { }
        _loop = null;
        _paused = false;
    }

    /// <summary>Paused when nobody could see it, and — when she has nothing to do — while CluadeX is in the background.</summary>
    private void SyncPause()
    {
        if (_loop == null) return;
        bool calm = Shown is BuddyMood.Idle or BuddyMood.Sleep;
        bool pause = !IsVisible || _window is { WindowState: WindowState.Minimized } || (calm && _window is { IsActive: false });
        if (pause == _paused) return;
        try { if (pause) _loop.Pause(this); else _loop.Resume(this); }
        catch (InvalidOperationException) { return; }
        _paused = pause;
    }

    private void Apply()
    {
        if (!IsLoaded) return;
        string mood = Mood ?? BuddyMood.Idle;
        if (mood == Shown && _loop != null) return;
        Stop();
        Reset();
        Shown = mood;
        var sb = new Storyboard();
        switch (mood)
        {
            case BuddyMood.Working:
                WorkArc.Visibility = Visibility.Visible;
                Rim.Opacity = 0.35;
                Forever(sb, ArcSpin, RotateTransform.AngleProperty, 0, 360, 1.2, reverse: false);
                Forever(sb, Bob, TranslateTransform.YProperty, 0, -1.4, 0.45);
                Forever(sb, Tilt, RotateTransform.AngleProperty, -4, 4, 1.7);
                Blink(sb, 3.2);
                Timeline.SetDesiredFrameRate(sb, 24);
                break;
            case BuddyMood.Waiting:
                Rim.SetResourceReference(Shape.StrokeProperty, "Status.Warning");
                AskChip.Visibility = Visibility.Visible;
                Tilt.Angle = 7;   // head cocked: "yes?"
                Forever(sb, Rim, OpacityProperty, 0.35, 1, 0.8);
                Forever(sb, AskHop, TranslateTransform.YProperty, 0, -2.5, 0.4);
                Blink(sb, 3.6);
                Timeline.SetDesiredFrameRate(sb, 20);
                break;
            case BuddyMood.Happy:
                Rim.SetResourceReference(Shape.StrokeProperty, "Status.Success");
                Rim.Opacity = 0.9;
                Once(sb, Bob, TranslateTransform.YProperty, (0, 0), (0.15, -4), (0.3, 0), (0.45, -2.5), (0.6, 0));
                Once(sb, Sparkle, OpacityProperty, (0, 0), (0.2, 1), (0.9, 1), (1.3, 0));
                Once(sb, SparkleScale, ScaleTransform.ScaleXProperty, (0, 0.3), (0.25, 1.2), (0.5, 0.9));
                Once(sb, SparkleScale, ScaleTransform.ScaleYProperty, (0, 0.3), (0.25, 1.2), (0.5, 0.9));
                Timeline.SetDesiredFrameRate(sb, 30);
                break;
            case BuddyMood.Sad:
                Rim.SetResourceReference(Shape.StrokeProperty, "Status.Danger");
                Rim.Opacity = 0.9;
                SadChip.Visibility = Visibility.Visible;
                Once(sb, Tilt, RotateTransform.AngleProperty, (0, 0), (0.08, -7), (0.18, 7), (0.28, -5), (0.38, 4), (0.5, 0));
                Timeline.SetDesiredFrameRate(sb, 30);
                break;
            case BuddyMood.Sleep:
                FaceAsleep.Opacity = 1;
                Tilt.Angle = 9;   // nodding off
                Rim.Opacity = 0.3;
                Forever(sb, Breathe, ScaleTransform.ScaleXProperty, 1, 1.035, 1.9);
                Forever(sb, Breathe, ScaleTransform.ScaleYProperty, 1, 1.035, 1.9);
                FloatZ(sb, Z1, Z1Rise, 0);
                FloatZ(sb, Z2, Z2Rise, 1.4);
                Timeline.SetDesiredFrameRate(sb, 10);
                break;
            default:   // idle
                Forever(sb, Breathe, ScaleTransform.ScaleXProperty, 1, 1.025, 1.6);
                Forever(sb, Breathe, ScaleTransform.ScaleYProperty, 1, 1.025, 1.6);
                Blink(sb, 4.2);
                Timeline.SetDesiredFrameRate(sb, 12);
                break;
        }
        _loop = sb;
        try { sb.Begin(this, HandoffBehavior.SnapshotAndReplace, isControllable: true); }
        catch (InvalidOperationException) { _loop = null; return; }
        SyncPause();
    }

    /// <summary>Back to the plain face: nothing turned, nothing shown, the accent rim.</summary>
    private void Reset()
    {
        WorkArc.Visibility = AskChip.Visibility = SadChip.Visibility = Visibility.Collapsed;
        Rim.SetResourceReference(Shape.StrokeProperty, "Accent.2");
        Rim.Opacity = 0.55;
        FaceAsleep.Opacity = Sparkle.Opacity = Z1.Opacity = Z2.Opacity = 0;
        Tilt.Angle = 0;
        Bob.Y = 0;
        Breathe.ScaleX = Breathe.ScaleY = 1;
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

    /// <summary>Eyes shut for a moment at the end of every <paramref name="period"/> seconds.</summary>
    private void Blink(Storyboard sb, double period)
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(period), RepeatBehavior = RepeatBehavior.Forever };
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(period - 0.2))));
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(period - 0.06))));
        Add(sb, a, FaceAsleep, OpacityProperty);
    }

    /// <summary>A "z" that fades in, drifts up and fades out, every 2.8 s, starting <paramref name="delay"/> s in.</summary>
    private static void FloatZ(Storyboard sb, UIElement z, TranslateTransform rise, double delay)
    {
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.8), RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(delay) };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.7))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.4))));
        Add(sb, fade, z, OpacityProperty);
        var up = new DoubleAnimation(2, -7, TimeSpan.FromSeconds(2.8)) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(delay) };
        Add(sb, up, rise, TranslateTransform.YProperty);
    }

    private static void Add(Storyboard sb, Timeline a, DependencyObject target, DependencyProperty prop)
    {
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, new PropertyPath(prop));
        sb.Children.Add(a);
    }
}
