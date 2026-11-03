using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CluadeX.Services;

namespace CluadeX.Views;

/// <summary>
/// Every girl (Services/GirlArt) standing in a row behind the new chat page, faint and fading out at the edges (owner
/// 2026-11-01: "ภาพสาวๆทุกคนเป็นแบล็คกราวด์ด้านหลังลางๆ แบบเท่ห์ๆ ไล่เฟด"). Never hit-tested.
///
/// What it costs: the pictures are decoded only once it is first on screen (a theme that hides it, Classic, never
/// decodes them) and at the height they are shown, not the cut-outs' 820 px. The one animation is the entrance — each
/// girl fading up in turn, ~1.7 s, capped at 30 fps — played when the page appears; nothing runs after it, so an empty
/// chat left open costs no frames. AmbientAnimation pauses the entrance while the page is hidden or the window is
/// minimised, like every other storyboard started from Triggers.
/// </summary>
public sealed class GirlsBackdrop : Grid
{
    /// <summary>The tallest the figures stand (DIPs): they shrink with a short chat area, never grow past this.</summary>
    public const double MaxFigureHeight = 560, MinFigureHeight = 240;
    /// <summary>The cut-outs' own height in pixels: decoding taller only adds memory.</summary>
    private const int SourceHeight = 820;

    private readonly StackPanel _row = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
    };
    private readonly List<Image> _figures = new();
    private readonly Storyboard _enter = new();

    /// <summary>The pixel height the pictures were decoded at (0 = not yet).</summary>
    public int DecodedHeight { get; private set; }
    /// <summary>The figures, for a harness to look at.</summary>
    public IReadOnlyList<Image> Figures => _figures;
    /// <summary>The entrance, for a harness to look at (its state is read per element: GetCurrentState(this)).</summary>
    public Storyboard Entrance => _enter;

    public GirlsBackdrop()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;

        // A soft spotlight on the girls' faces and shoulders: full strength there, gone at every edge — whatever the
        // width, the row melts into the page (and into the composer under it) instead of ending in a cut.
        var mask = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.48),
            GradientOrigin = new Point(0.5, 0.48),
            RadiusX = 0.62,
            RadiusY = 0.78,
        };
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0, 0, 0), 0.0));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0xB0, 0, 0, 0), 0.5));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1.0));
        mask.Freeze();
        OpacityMask = mask;

        Children.Add(_row);
        var names = GirlArt.Names;
        for (int i = 0; i < names.Count; i++)
        {
            var img = new Image
            {
                Stretch = Stretch.Uniform,
                Opacity = 0,
                Height = MaxFigureHeight,
                // Shoulder to shoulder: each cut-out has air around her.
                Margin = new Thickness(-24, 0, -24, 0),
                RenderTransform = new TranslateTransform(0, 26),
                Tag = names[i],
            };
            _row.Children.Add(img);
            _figures.Add(img);

            var start = TimeSpan.FromMilliseconds(110 * i);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)) { BeginTime = start, EasingFunction = ease };
            Storyboard.SetTarget(fade, img);
            Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
            var rise = new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(1100)) { BeginTime = start, EasingFunction = ease };
            Storyboard.SetTarget(rise, img);
            Storyboard.SetTargetProperty(rise, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
            _enter.Children.Add(fade);
            _enter.Children.Add(rise);
        }
        Timeline.SetDesiredFrameRate(_enter, 30);

        // Started from Triggers with a NAME, so AmbientAnimation (WhileVisible, SetMinimized) can pause and resume it.
        var trigger = new EventTrigger(LoadedEvent);
        trigger.Actions.Add(new BeginStoryboard { Storyboard = _enter, Name = "GirlsEnter" });
        Triggers.Add(trigger);
        AmbientAnimation.SetWhileVisible(this, true);

        // Done: the girls keep where it left them as their own values and the clock goes. Held at its end instead, the
        // storyboard asks for no frames but stays a live clock for as long as the page lives (tests/animtest counts it).
        _enter.Completed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            // A replay begun in between is left to run.
            try { if (_enter.GetCurrentState(this) == ClockState.Active) return; }
            catch (InvalidOperationException) { return; }
            foreach (var img in _figures)
            {
                img.Opacity = 1;
                ((TranslateTransform)img.RenderTransform).Y = 0;
            }
            _enter.Remove(this);
        });

        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Decode(); };
        SizeChanged += (_, _) => Fit();
    }

    /// <summary>The page came back (a new empty chat): the girls come in again.</summary>
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != VisibilityProperty || !IsLoaded || (Visibility)e.NewValue != Visibility.Visible) return;
        _enter.Begin(this, HandoffBehavior.SnapshotAndReplace, isControllable: true);
        // IsVisible catches up after this; until it does (or for good, when the page itself is hidden) it waits.
        if (!IsVisible) _enter.Pause(this);
    }

    private void Decode()
    {
        if (DecodedHeight > 0 || _figures.Count == 0) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        int px = Math.Min(SourceHeight, (int)Math.Ceiling(MaxFigureHeight * scale));
        foreach (var img in _figures) img.Source = GirlArt.Image(img.Tag as string, px);
        DecodedHeight = px;
    }

    private void Fit()
    {
        if (ActualHeight <= 0) return;
        double h = Math.Clamp(ActualHeight * 0.9, MinFigureHeight, MaxFigureHeight);
        foreach (var img in _figures)
            if (Math.Abs(img.Height - h) > 0.5) img.Height = h;
    }
}
