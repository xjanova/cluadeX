using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CluadeX.Views;

/// <summary>
/// Small motion helpers for the plan and agent-map rows (owner 2026-11-03: "หน้าแผนงานควรมีอินเมชั่นของงานแต่ละแผน
/// ตามสเตตัสจริง"): the accent a plan row's live parts wear, a progress fill that glides to each new value, and plain
/// double properties a Style trigger can animate in place of a transform.
/// <para>Why the doubles: a RenderTransform written in a DataTemplate is frozen with the template, and a Style
/// storyboard cannot animate "(UIElement.RenderTransform).(RotateTransform.Angle)" on it ("Cannot animate '(0).(1)' on an
/// immutable object instance"). <see cref="SpinProperty"/> and the others are properties of the element itself; each
/// keeps its own unfrozen transform (made on first use) in step with the animated value.</para>
/// </summary>
public static class Motion
{
    /// <summary>Degrees, turned about the element's RenderTransformOrigin.</summary>
    public static readonly DependencyProperty SpinProperty = DependencyProperty.RegisterAttached(
        "Spin", typeof(double), typeof(Motion), new PropertyMetadata(0.0, (d, e) =>
        {
            if (d is UIElement el) Own<RotateTransform>(el).Angle = (double)e.NewValue;
        }));
    public static double GetSpin(DependencyObject d) => (double)d.GetValue(SpinProperty);
    public static void SetSpin(DependencyObject d, double value) => d.SetValue(SpinProperty, value);

    /// <summary>A uniform scale about the RenderTransformOrigin (1 = as laid out).</summary>
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached(
        "Scale", typeof(double), typeof(Motion), new PropertyMetadata(1.0, (d, e) =>
        {
            if (d is not UIElement el) return;
            var s = Own<ScaleTransform>(el);
            s.ScaleX = s.ScaleY = (double)e.NewValue;
        }));
    public static double GetScale(DependencyObject d) => (double)d.GetValue(ScaleProperty);
    public static void SetScale(DependencyObject d, double value) => d.SetValue(ScaleProperty, value);

    /// <summary>A sideways shift in pixels (a shake).</summary>
    public static readonly DependencyProperty ShiftXProperty = DependencyProperty.RegisterAttached(
        "ShiftX", typeof(double), typeof(Motion), new PropertyMetadata(0.0, (d, e) =>
        {
            if (d is UIElement el) Own<TranslateTransform>(el).X = (double)e.NewValue;
        }));
    public static double GetShiftX(DependencyObject d) => (double)d.GetValue(ShiftXProperty);
    public static void SetShiftX(DependencyObject d, double value) => d.SetValue(ShiftXProperty, value);

    /// <summary>A shift down (or up, negative) in pixels (an arrival, a tick).</summary>
    public static readonly DependencyProperty ShiftYProperty = DependencyProperty.RegisterAttached(
        "ShiftY", typeof(double), typeof(Motion), new PropertyMetadata(0.0, (d, e) =>
        {
            if (d is UIElement el) Own<TranslateTransform>(el).Y = (double)e.NewValue;
        }));
    public static double GetShiftY(DependencyObject d) => (double)d.GetValue(ShiftYProperty);
    public static void SetShiftY(DependencyObject d, double value) => d.SetValue(ShiftYProperty, value);

    /// <summary>Where the element's OpacityMask band is, in widths of the element (0 = the band as drawn, -1 / 1 = one
    /// width to the left / right): a sweep moves the mask, not the element. The mask is copied once, unfrozen.</summary>
    public static readonly DependencyProperty SweepProperty = DependencyProperty.RegisterAttached(
        "Sweep", typeof(double), typeof(Motion), new PropertyMetadata(-1.0, (d, e) =>
        {
            if (d is not UIElement { OpacityMask: { } mask } el) return;
            if (mask.IsFrozen || mask.RelativeTransform is not TranslateTransform { IsFrozen: false })
            {
                mask = mask.CloneCurrentValue();
                mask.RelativeTransform = new TranslateTransform();
                el.OpacityMask = mask;
            }
            ((TranslateTransform)mask.RelativeTransform).X = (double)e.NewValue;
        }));
    public static double GetSweep(DependencyObject d) => (double)d.GetValue(SweepProperty);
    public static void SetSweep(DependencyObject d, double value) => d.SetValue(SweepProperty, value);

    /// <summary>The element's own transform of type <typeparamref name="T"/>: the one it has when that is unfrozen, else a
    /// new one set as its RenderTransform (an element animates one kind of transform).</summary>
    private static T Own<T>(UIElement el) where T : Transform, new()
    {
        if (el.RenderTransform is T { IsFrozen: false } mine) return mine;
        var made = new T();
        el.RenderTransform = made;
        return made;
    }

    /// <summary>The colour of a plan row's live parts (the turning ring, the sweep, the highlight), inherited down the
    /// tree. Unset = the theme's Accent.1; the plan bar sets its author's colour so the rows match the bar.</summary>
    public static readonly DependencyProperty AccentProperty = DependencyProperty.RegisterAttached(
        "Accent", typeof(Brush), typeof(Motion),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static Brush? GetAccent(DependencyObject d) => (Brush?)d.GetValue(AccentProperty);
    public static void SetAccent(DependencyObject d, Brush? value) => d.SetValue(AccentProperty, value);

    /// <summary>0..1 on a fill element: it is drawn that much of its width (a ScaleX from its left edge) and glides
    /// there over half a second when the value changes — once per change, no clock left running.</summary>
    public static readonly DependencyProperty FractionProperty = DependencyProperty.RegisterAttached(
        "Fraction", typeof(double), typeof(Motion), new PropertyMetadata(0.0, OnFractionChanged));

    public static double GetFraction(DependencyObject d) => (double)d.GetValue(FractionProperty);
    public static void SetFraction(DependencyObject d, double value) => d.SetValue(FractionProperty, value);

    private static void OnFractionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        double to = Math.Clamp(e.NewValue is double v && !double.IsNaN(v) ? v : 0, 0, 1);
        if (el.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            scale = new ScaleTransform(0, 1);
            el.RenderTransform = scale;
            el.RenderTransformOrigin = new Point(0, 0.5);
        }
        // Not on screen: no one would see the glide, so the value is just set.
        if (!el.IsVisible)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.ScaleX = to;
            return;
        }
        var glide = new DoubleAnimation(to, TimeSpan.FromMilliseconds(550))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        Timeline.SetDesiredFrameRate(glide, 30);
        // The end value stays as the base value, so the held animation can be dropped once it lands.
        glide.Completed += (_, _) =>
        {
            if (Math.Abs(GetFraction(d) - to) > 1e-9) return;   // a newer glide took over
            scale.ScaleX = to;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, glide, HandoffBehavior.SnapshotAndReplace);
    }

    // ── State loops ────────────────────────────────────────────────────────────────────────────────────────────────
    // A Style trigger's EnterActions can fire more than once for one element without its ExitActions in between (a row
    // added to a map already on screen was begun three times), and each extra BeginStoryboard leaves the clock before it
    // ticking until the GC finds it. Here the trigger only sets LoopOn; this code begins the storyboard once, on the
    // element itself, while it is on and on screen, and removes it the moment either stops being true.

    /// <summary>The endless storyboard this element plays while <see cref="LoopOnProperty"/> is true and it is on
    /// screen. Its animations target the element itself (no TargetName).</summary>
    public static readonly DependencyProperty LoopProperty = DependencyProperty.RegisterAttached(
        "Loop", typeof(Storyboard), typeof(Motion), new PropertyMetadata(null, OnLoopChanged));
    public static Storyboard? GetLoop(DependencyObject d) => (Storyboard?)d.GetValue(LoopProperty);
    public static void SetLoop(DependencyObject d, Storyboard? value) => d.SetValue(LoopProperty, value);

    /// <summary>The state the loop belongs to holds (an item at work, a sub-agent running) — set by a trigger.</summary>
    public static readonly DependencyProperty LoopOnProperty = DependencyProperty.RegisterAttached(
        "LoopOn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnLoopChanged));
    public static bool GetLoopOn(DependencyObject d) => (bool)d.GetValue(LoopOnProperty);
    public static void SetLoopOn(DependencyObject d, bool value) => d.SetValue(LoopOnProperty, value);

    /// <summary>The storyboard begun on the element (null: none) — at most one at a time.</summary>
    private static readonly DependencyProperty RunningLoopProperty = DependencyProperty.RegisterAttached(
        "RunningLoop", typeof(Storyboard), typeof(Motion), new PropertyMetadata(null));

    private static readonly DependencyProperty LoopHookedProperty = DependencyProperty.RegisterAttached(
        "LoopHooked", typeof(bool), typeof(Motion), new PropertyMetadata(false));

    /// <summary>True while a loop is begun on <paramref name="d"/> (tests and the minimise pause read it).</summary>
    public static bool IsLooping(DependencyObject d) => d.GetValue(RunningLoopProperty) != null;

    private static void OnLoopChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if (!(bool)fe.GetValue(LoopHookedProperty))
        {
            fe.SetValue(LoopHookedProperty, true);
            fe.IsVisibleChanged += (_, _) => SyncLoop(fe);
            fe.Loaded += (_, _) => SyncLoop(fe);
            fe.Unloaded += (_, _) => SyncLoop(fe);
        }
        SyncLoop(fe);
    }

    private static void SyncLoop(FrameworkElement fe)
    {
        var want = GetLoopOn(fe) && fe.IsVisible && fe.IsLoaded ? GetLoop(fe) : null;
        var running = (Storyboard?)fe.GetValue(RunningLoopProperty);
        if (ReferenceEquals(want, running)) return;
        if (running != null)
        {
            try { running.Remove(fe); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Motion] {ex.Message}"); }
            fe.ClearValue(RunningLoopProperty);
        }
        if (want == null) return;
        try
        {
            want.Begin(fe, HandoffBehavior.SnapshotAndReplace, isControllable: true);
            fe.SetValue(RunningLoopProperty, want);
            // A loop that starts while its window is minimised waits for the restore (AmbientAnimation resumes it).
            if (Window.GetWindow(fe) is { WindowState: WindowState.Minimized }) want.Pause(fe);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Motion] {ex.Message}"); }
    }

    /// <summary>Pause (or resume) the loop begun on <paramref name="fe"/>, if any — the minimised window's rule.</summary>
    internal static void SetLoopPaused(FrameworkElement fe, bool paused)
    {
        if (fe.GetValue(RunningLoopProperty) is not Storyboard sb) return;
        try { if (paused) sb.Pause(fe); else sb.Resume(fe); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Motion] {ex.Message}"); }
    }
}
