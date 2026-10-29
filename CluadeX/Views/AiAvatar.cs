using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace CluadeX.Views;

/// <summary>What the AI's avatar is doing beside its words.</summary>
public enum AiAvatarMode
{
    /// <summary>A reply that is done: her portrait, still.</summary>
    Idle,
    /// <summary>Running tools or writing the reply: she taps away at a little laptop.</summary>
    Typing,
    /// <summary>Thinking before she answers: she sways over an open book whose pages turn.</summary>
    Reading,
}

/// <summary>
/// The AI's round portrait at the right of the chat, the end its speech bubbles point to (owner 2026-10-29: "ให้มีหาง
/// เป็นเหมือนกล่องคำพูดมาถึงตรงรูปอวาต้า … ทำให้อวาต้าเคลื่อนไหวขณะกำลังคิดหรือทำงานอยู่ เช่นกำลังพิมพ์คอม หรือเปิด
/// หนังสือตำรา น่ารักๆ"). Drawn here, not from pictures: a laptop with tapping hands while she works, a book with a
/// page turning while she thinks. Animations run only while she is busy and on screen (an old reply costs nothing),
/// and not at all when Windows' "show animations" is off.
/// </summary>
public sealed class AiAvatar : Grid
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(nameof(Mode), typeof(AiAvatarMode),
        typeof(AiAvatar), new PropertyMetadata(AiAvatarMode.Idle, (d, _) => ((AiAvatar)d).Apply()));

    /// <summary>Her face (ChatCharacter.FaceBrush); null shows CluadeX's logo.</summary>
    public static readonly DependencyProperty FaceProperty = DependencyProperty.Register(nameof(Face), typeof(Brush),
        typeof(AiAvatar), new PropertyMetadata(null, (d, _) => ((AiAvatar)d).ApplyFace()));

    /// <summary>Her colour: the ring around the portrait and the glow of the laptop screen.</summary>
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush),
        typeof(AiAvatar), new PropertyMetadata(null, (d, _) => ((AiAvatar)d).ApplyAccent()));

    public AiAvatarMode Mode { get => (AiAvatarMode)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public Brush? Face { get => (Brush?)GetValue(FaceProperty); set => SetValue(FaceProperty, value); }
    public Brush? Accent { get => (Brush?)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    private const double Side = 34;
    /// <summary>The laptop and the book are drawn on a 20-wide grid and shown this much larger.</summary>
    private const double PropScale = 1.35;

    private readonly Ellipse _ring = new() { StrokeThickness = 2 };
    private readonly Ellipse _face = new() { Margin = new Thickness(2) };
    private readonly Image _logo = new()
    {
        Source = new BitmapImage(new Uri("pack://application:,,,/CluadeX;component/Assets/logo.png")),
        Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TranslateTransform _bob = new();
    private readonly RotateTransform _sway = new(0, Side / 2, Side);

    private readonly Canvas _laptop;
    private readonly Rectangle[] _codeLines = new Rectangle[3];
    private readonly TranslateTransform _leftHand = new(), _rightHand = new();
    private readonly Rectangle _screen = new() { Width = 14, Height = 9, RadiusX = 1.5, RadiusY = 1.5, StrokeThickness = 1 };

    private readonly Canvas _book;
    private readonly ScaleTransform _page = new(1, 1, 10, 0);

    public AiAvatar()
    {
        Width = Side + 12;
        Height = Side + 8;
        ClipToBounds = false;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(_logo, BitmapScalingMode.HighQuality);

        var body = new Grid
        {
            Width = Side, Height = Side, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = new TransformGroup { Children = { _sway, _bob } },
        };
        var plate = new Ellipse();
        plate.SetResourceReference(Shape.FillProperty, "Bg.Elevated");
        body.Children.Add(plate);
        body.Children.Add(_logo);
        body.Children.Add(_face);
        body.Children.Add(_ring);
        Children.Add(body);

        _laptop = BuildLaptop();
        _book = BuildBook();
        foreach (var prop in new[] { _laptop, _book })
        {
            prop.RenderTransformOrigin = new Point(0, 1);   // grows up and right from her lower left, over her shoulder
            prop.RenderTransform = new ScaleTransform(PropScale, PropScale);
        }
        Children.Add(_laptop);
        Children.Add(_book);

        ApplyFace();
        ApplyAccent();
        Loaded += (_, _) => Apply();
        Unloaded += (_, _) => StopAnimation();   // a row scrolled out of the list (or recycled): nothing keeps ticking
        IsVisibleChanged += (_, _) => Apply();
    }

    // ───────────────────────────── the props ─────────────────────────────

    /// <summary>A laptop 20 × 13 at her lower left (towards the bubble), screen glowing in her colour, two hands on it.</summary>
    private Canvas BuildLaptop()
    {
        var c = new Canvas { Width = 20, Height = 13, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false };
        _screen.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x33));
        Canvas.SetLeft(_screen, 3);
        Canvas.SetTop(_screen, 0);
        c.Children.Add(_screen);
        double[] widths = { 7, 5, 8 };
        for (int i = 0; i < 3; i++)
        {
            _codeLines[i] = new Rectangle { Width = widths[i], Height = 1.3, RadiusX = 0.6, RadiusY = 0.6, Opacity = 0.25 };
            Canvas.SetLeft(_codeLines[i], 5);
            Canvas.SetTop(_codeLines[i], 2 + i * 2.2);
            c.Children.Add(_codeLines[i]);
        }
        var deck = new Path { Data = Geometry.Parse("M0,9.4 H20 L18.4,12.4 H1.6 Z"), Fill = new SolidColorBrush(Color.FromRgb(0xB4, 0xBC, 0xCC)) };
        c.Children.Add(deck);
        c.Children.Add(Hand(5.2, _leftHand));
        c.Children.Add(Hand(11.6, _rightHand));
        return c;

        static Ellipse Hand(double x, TranslateTransform t)
        {
            var h = new Ellipse
            {
                Width = 4.2, Height = 3.2, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xDD, 0xC6)),
                Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0xC8, 0x8E, 0x74)), StrokeThickness = 0.5, RenderTransform = t,
            };
            Canvas.SetLeft(h, x);
            Canvas.SetTop(h, 8.2);
            return h;
        }
    }

    /// <summary>An open book 20 × 12 at her lower left, a few lines of text on each page, one page that turns.</summary>
    private Canvas BuildBook()
    {
        var c = new Canvas { Width = 20, Height = 12, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false };
        var paper = new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xE8));
        var edge = new SolidColorBrush(Color.FromRgb(0xC9, 0xA9, 0x7A));
        var ink = new SolidColorBrush(Color.FromArgb(0x90, 0x8A, 0x7A, 0x66));
        c.Children.Add(new Path { Data = Geometry.Parse("M10,2 Q5,0 0,2 V12 Q5,10 10,12 Z"), Fill = paper, Stroke = edge, StrokeThickness = 0.7 });
        c.Children.Add(new Path { Data = Geometry.Parse("M10,2 Q15,0 20,2 V12 Q15,10 10,12 Z"), Fill = paper, Stroke = edge, StrokeThickness = 0.7 });
        for (int i = 0; i < 3; i++)
        {
            c.Children.Add(Line(2, 4 + i * 2, 6));
            c.Children.Add(Line(12, 4 + i * 2, 6));
        }
        c.Children.Add(new Path
        {
            Data = Geometry.Parse("M10,2 Q15,0 20,2 V12 Q15,10 10,12 Z"), Fill = new SolidColorBrush(Color.FromRgb(0xF6, 0xE9, 0xCC)),
            Stroke = edge, StrokeThickness = 0.7, RenderTransform = _page,
        });
        return c;

        Rectangle Line(double x, double y, double w)
        {
            var r = new Rectangle { Width = w, Height = 0.8, Fill = ink };
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            return r;
        }
    }

    private void ApplyFace()
    {
        _face.Fill = Face;
        _face.Visibility = Face != null ? Visibility.Visible : Visibility.Collapsed;
        _logo.Visibility = Face != null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyAccent()
    {
        Brush accent = Accent ?? (TryFindResource("Accent.1") as Brush) ?? Brushes.MediumPurple;
        _ring.Stroke = accent;
        _screen.Stroke = accent;
        foreach (var l in _codeLines) if (l != null) l.Fill = accent;
    }

    // ───────────────────────────── moving ─────────────────────────────

    /// <summary>What is animated now, so it can all be stopped: (the object, its property).</summary>
    private readonly List<(IAnimatable Target, DependencyProperty Property)> _animated = new();

    private void Apply()
    {
        _laptop.Visibility = Mode == AiAvatarMode.Typing ? Visibility.Visible : Visibility.Collapsed;
        _book.Visibility = Mode == AiAvatarMode.Reading ? Visibility.Visible : Visibility.Collapsed;
        StopAnimation();
        if (Mode == AiAvatarMode.Idle || !IsLoaded || !IsVisible) return;
        if (!SystemParameters.ClientAreaAnimation)
        {
            foreach (var l in _codeLines) l.Opacity = 0.9;   // still: the screen shows its code, the page lies open
            return;
        }
        if (Mode == AiAvatarMode.Typing) StartTyping();
        else StartReading();
    }

    private void Animate(IAnimatable target, DependencyProperty property, AnimationTimeline animation)
    {
        animation.RepeatBehavior = RepeatBehavior.Forever;
        if (animation.CanFreeze) animation.Freeze();
        target.BeginAnimation(property, animation);
        _animated.Add((target, property));
    }

    private void StopAnimation()
    {
        foreach (var (target, property) in _animated) target.BeginAnimation(property, null);
        _animated.Clear();
        _bob.Y = 0;
        _sway.Angle = 0;
        _page.ScaleX = 1;
        _leftHand.Y = _rightHand.Y = 0;
        foreach (var l in _codeLines) l.Opacity = 0.25;
    }

    private static readonly TimeSpan TypingLoop = TimeSpan.FromSeconds(1.28);

    /// <summary>Quick little taps of each hand in turn, the head nodding to them, the code lines lighting up one by one.</summary>
    private void StartTyping()
    {
        Animate(_leftHand, TranslateTransform.YProperty, Taps(0));
        Animate(_rightHand, TranslateTransform.YProperty, Taps(0.16));
        Animate(_bob, TranslateTransform.YProperty,
            new DoubleAnimation(0, -1.6, TimeSpan.FromSeconds(0.32)) { AutoReverse = true, EasingFunction = new SineEase() });
        for (int i = 0; i < _codeLines.Length; i++)
        {
            var glow = new DoubleAnimationUsingKeyFrames { Duration = TypingLoop };
            glow.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.25, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            glow.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.2 + i * 0.32))));
            Animate(_codeLines[i], OpacityProperty, glow);
        }

        static DoubleAnimationUsingKeyFrames Taps(double offset)
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = TypingLoop };
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            for (double t = offset; t + 0.12 <= TypingLoop.TotalSeconds; t += 0.32)
            {
                a.KeyFrames.Add(new EasingDoubleKeyFrame(-1.6, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t + 0.06)), new SineEase()));
                a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t + 0.12)), new SineEase()));
            }
            return a;
        }
    }

    private static readonly TimeSpan ReadingLoop = TimeSpan.FromSeconds(3.2);

    /// <summary>A slow sway over the book, and every few seconds a page turns from right to left.</summary>
    private void StartReading()
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var sway = new DoubleAnimationUsingKeyFrames { Duration = ReadingLoop };
        sway.KeyFrames.Add(new EasingDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.8)), ease));
        sway.KeyFrames.Add(new EasingDoubleKeyFrame(4, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.4)), ease));
        sway.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(ReadingLoop), ease));
        Animate(_sway, RotateTransform.AngleProperty, sway);

        var turn = new DoubleAnimationUsingKeyFrames { Duration = ReadingLoop };
        turn.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        turn.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6))));
        turn.KeyFrames.Add(new EasingDoubleKeyFrame(-1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.5)), ease));
        turn.KeyFrames.Add(new DiscreteDoubleKeyFrame(-1, KeyTime.FromTimeSpan(ReadingLoop)));
        Animate(_page, ScaleTransform.ScaleXProperty, turn);
    }
}
