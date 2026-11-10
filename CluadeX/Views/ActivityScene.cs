using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using CluadeX.Services;
using CluadeX.Services.Agents;

namespace CluadeX.Views;

/// <summary>
/// The activity card's scene: a core (the model) above five stations — think, explore, edit, run, team — with a beam
/// to the station the run is at, data packets flowing along it (in when it reads, out when it writes or runs), comets
/// on two orbits, sub-agents as satellites going round the core, ripples while it waits on the owner, a red flash and
/// a shake when a tool fails, a burst when the turn ends. Everything it shows comes from its <see cref="Track"/>.
/// <para>Cheap by design (owner: animation that does not eat the machine). The drawing is RETAINED: it is built once per
/// change of the track (a tool started, an agent joined) with the moving parts as unfrozen geometries, transforms and
/// opacity groups, and each frame only writes numbers into those — no brush, pen, geometry or text is made per frame,
/// and nothing is re-laid out. Frames come from <see cref="CompositionTarget.Rendering"/>, hooked only while the run is
/// live (or a 1.2 s end burst plays), the scene is on screen and the window is not minimised; capped at 30 fps, 15
/// while another app has the focus. Animations off in Windows, or software rendering: one still frame.</para>
/// </summary>
public sealed class ActivityScene : FrameworkElement
{
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(ActivityTrack), typeof(ActivityScene), new PropertyMetadata(null, (d, _) => ((ActivityScene)d).OnTrackChanged()));
    public ActivityTrack? Track { get => (ActivityTrack?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    /// <summary>Bound to the track's version: a change rebuilds the still parts.</summary>
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(ActivityScene), new PropertyMetadata(-1, (d, _) => ((ActivityScene)d).OnTrackChanged()));
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }

    /// <summary>Once a second while the scene moves (the card's clock follows it, so it stops with it).</summary>
    public event EventHandler? SecondTick;

    /// <summary>The frame loop is hooked now (tests: it must let go when the run is idle, hidden or minimised).</summary>
    public bool IsHooked { get; private set; }
    /// <summary>Frames drawn since load (tests).</summary>
    public int FrameCount { get; private set; }
    /// <summary>Tests: draw this moment (seconds) instead of the clock's.</summary>
    public double? FixedTime { get; set; }
    /// <summary>Tests and the Windows "animations" switch: never hook the loop.</summary>
    public static bool AnimationsAllowed =>
        SystemParameters.ClientAreaAnimation && (RenderCapability.Tier >> 16) > 0;

    private const double StillTime = 0.65, BurstSeconds = 1.2;
    private readonly DrawingGroup _root = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrame = -1;
    private int _lastSecond = -1;
    private Window? _window;
    private bool _themeHooked;
    private ActivityMood _lastMood = ActivityMood.Idle;
    private double _burstFrom = -10;
    private bool _dirty = true;
    private Size _builtSize;

    // ─── the palette (theme tokens, rebuilt on a theme switch) ───
    private Color[] _station = new Color[5];
    private Color _wait, _danger, _success, _muted, _text, _faint, _base, _line;
    private readonly Dictionary<(Color, byte), SolidColorBrush> _solid = new();
    private readonly Dictionary<Color, RadialGradientBrush> _glow = new();

    // ─── the moving parts of the current drawing ───
    private Point _core;
    private double _coreR;
    private readonly Point[] _stationAt = new Point[5];
    private readonly (Point A, Point C, Point B)[] _links = new (Point, Point, Point)[5];
    private readonly TranslateTransform _scan = new();
    private readonly ScaleTransform _glowScale = new();
    private readonly TranslateTransform _shake = new();
    private readonly ScaleTransform _haloScale = new();
    private readonly DrawingGroup _halo = new();
    private readonly DrawingGroup _flash = new();
    private readonly EllipseGeometry[] _comets = Make(12);
    private readonly EllipseGeometry[] _packets = Make(3);
    private readonly DrawingGroup[] _packetFade = MakeGroups(3);
    private readonly DrawingGroup[] _sparks = MakeGroups(6);
    private readonly EllipseGeometry[] _ripples = Make(3);
    private readonly DrawingGroup[] _rippleFade = MakeGroups(3);
    private readonly List<(ActivitySatellite Sat, EllipseGeometry Back, EllipseGeometry Front, DrawingGroup BackFade, DrawingGroup FrontFade)> _satNodes = new();
    private bool _inward, _hasBeam, _spin, _waiting, _thinking, _flashOn, _burst;
    private ActivityStation _flashAt;
    private double _speed = 1;

    private static EllipseGeometry[] Make(int n) { var a = new EllipseGeometry[n]; for (int i = 0; i < n; i++) a[i] = new EllipseGeometry(); return a; }
    private static DrawingGroup[] MakeGroups(int n) { var a = new DrawingGroup[n]; for (int i = 0; i < n; i++) a[i] = new DrawingGroup(); return a; }

    private static readonly string[] StationGlyph = { "", "", "", "", "" };
    private static readonly string[] StationKey = { "activity.station.think", "activity.station.explore", "activity.station.edit", "activity.station.run", "activity.station.team" };

    public ActivityScene()
    {
        SnapsToDevicePixels = false;
        IsHitTestVisible = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => Sync();
        SizeChanged += (_, _) => { _dirty = true; Sync(); };
    }

    protected override void OnRender(DrawingContext dc) => dc.DrawDrawing(_root);

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 280 : available.Width, double.IsInfinity(available.Height) ? 136 : Math.Min(available.Height, 136));

    // ─── lifetime ───

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window != null)
        {
            _window.StateChanged += OnWindowChanged;
            _window.Activated += OnWindowChanged;
            _window.Deactivated += OnWindowChanged;
        }
        if (!_themeHooked && ThemeService.Current is { } ts) { ts.ThemeChanged += OnThemeChanged; _themeHooked = true; }
        LocalizedResources.Instance.PropertyChanged += OnLanguageChanged;
        _dirty = true;
        Sync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window != null)
        {
            _window.StateChanged -= OnWindowChanged;
            _window.Activated -= OnWindowChanged;
            _window.Deactivated -= OnWindowChanged;
            _window = null;
        }
        if (_themeHooked && ThemeService.Current is { } ts) { ts.ThemeChanged -= OnThemeChanged; _themeHooked = false; }
        LocalizedResources.Instance.PropertyChanged -= OnLanguageChanged;
        Unhook();
    }

    private void OnWindowChanged(object? sender, EventArgs e) => Sync();

    /// <summary>The station names are drawn text: a language switch rebuilds them.</summary>
    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        _dirty = true;
        Dispatcher.BeginInvoke(Sync);
    }

    private void OnThemeChanged(string _)
    {
        _solid.Clear();
        _glow.Clear();
        _dirty = true;
        Dispatcher.BeginInvoke(Sync);
    }

    private void OnTrackChanged()
    {
        var mood = Track?.Mood ?? ActivityMood.Idle;
        if (mood != _lastMood && mood is ActivityMood.Done or ActivityMood.Failed or ActivityMood.Stopped
            && _lastMood is ActivityMood.Live or ActivityMood.Waiting)
            _burstFrom = Now;
        _lastMood = mood;
        _dirty = true;
        Sync();
    }

    private double Now => FixedTime ?? _clock.Elapsed.TotalSeconds;

    /// <summary>Whether frames are wanted now: something moves, and someone can see it.</summary>
    public bool WantsFrames
    {
        get
        {
            if (!IsLoaded || !IsVisible || ActualWidth < 40 || !AnimationsAllowed) return false;
            if (_window is { WindowState: WindowState.Minimized }) return false;
            var t = Track;
            bool live = t != null && (t.IsLive || t.IsFlashing(DateTime.Now));
            return live || Now - _burstFrom < BurstSeconds;
        }
    }

    private void Sync()
    {
        if (!IsLoaded) return;
        if (_dirty) Build();
        if (WantsFrames) Hook();
        else
        {
            Unhook();
            Animate(StillTime, still: true);
        }
    }

    private void Hook()
    {
        if (IsHooked) return;
        CompositionTarget.Rendering += OnFrame;
        IsHooked = true;
        _lastFrame = -1;
    }

    private void Unhook()
    {
        if (!IsHooked) return;
        CompositionTarget.Rendering -= OnFrame;
        IsHooked = false;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double now = Now;
        double gap = _window is { IsActive: false } ? 1 / 15.0 : 1 / 30.0;
        if (_lastFrame >= 0 && now - _lastFrame < gap * 0.92) return;
        _lastFrame = now;
        if (!WantsFrames) { Sync(); return; }
        if (_dirty) Build();
        Animate(now, still: false);
        int sec = (int)now;
        if (sec != _lastSecond) { _lastSecond = sec; SecondTick?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>Tests: rebuild and draw the moment <paramref name="seconds"/> now, whatever the loop does.</summary>
    public void DrawAt(double seconds)
    {
        FixedTime = seconds;
        Build();
        Animate(seconds, still: false);
    }

    // ─── palette ───

    private void LoadPalette()
    {
        Color C(string token, Color fb) => ThemeService.ColorOf(token, fb);
        _station[0] = C("Accent.1", Color.FromRgb(0x8B, 0x5C, 0xF6));
        _station[1] = C("Status.Info", Color.FromRgb(0x06, 0xB6, 0xD4));
        _station[2] = C("Accent.2", Color.FromRgb(0xEC, 0x48, 0x99));
        _station[3] = C("Status.Warning", Color.FromRgb(0xF5, 0x9E, 0x0B));
        _station[4] = C("Accent.3", Color.FromRgb(0x22, 0xD3, 0xEE));
        _wait = C("Status.Warning", Color.FromRgb(0xF5, 0x9E, 0x0B));
        _danger = C("Status.Danger", Color.FromRgb(0xEF, 0x44, 0x44));
        _success = C("Status.Success", Color.FromRgb(0x22, 0xC5, 0x5E));
        _muted = C("Text.Muted", Color.FromRgb(0x88, 0x88, 0x99));
        _text = C("Text.Primary", Colors.White);
        _faint = C("Text.Faint", Color.FromRgb(0x66, 0x66, 0x77));
        _base = C("Surface.Sunken", Color.FromRgb(0x10, 0x10, 0x18));
        _line = C("Border.Subtle", Color.FromRgb(0x33, 0x33, 0x44));
    }

    private SolidColorBrush Solid(Color c, byte alpha = 255)
    {
        if (_solid.TryGetValue((c, alpha), out var b)) return b;
        b = new SolidColorBrush(Color.FromArgb((byte)(c.A * alpha / 255), c.R, c.G, c.B));
        b.Freeze();
        return _solid[(c, alpha)] = b;
    }

    private RadialGradientBrush Glow(Color c)
    {
        if (_glow.TryGetValue(c, out var b)) return b;
        b = new RadialGradientBrush();
        b.GradientStops.Add(new GradientStop(Color.FromArgb(150, c.R, c.G, c.B), 0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(60, c.R, c.G, c.B), 0.45));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        b.Freeze();
        return _glow[c] = b;
    }

    private static Pen PenOf(Brush b, double w, DashStyle? dash = null)
    {
        var p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (dash != null) p.DashStyle = dash;
        p.Freeze();
        return p;
    }

    /// <summary>The colour of the core now: the station's while it works, else the mood's.</summary>
    private Color CoreColor(ActivityTrack t) => t.Mood switch
    {
        ActivityMood.Waiting => _wait,
        ActivityMood.Done => _success,
        ActivityMood.Failed => _danger,
        ActivityMood.Stopped or ActivityMood.Idle => _muted,
        _ => _station[(int)t.Station],
    };

    private static string CoreGlyph(ActivityTrack t) => t.Mood switch
    {
        ActivityMood.Waiting => "",
        ActivityMood.Done => "",
        ActivityMood.Failed => "",
        ActivityMood.Stopped => "",
        ActivityMood.Idle => "",
        _ => StationGlyph[(int)t.Station],
    };

    // ─── the drawing (once per change) ───

    private void Build()
    {
        _dirty = false;
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40) { using (_root.Open()) { } return; }
        LoadPalette();
        _builtSize = new Size(w, h);
        var t = Track ?? new ActivityTrack();
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var uiFont = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var uiBold = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var icons = new Typeface(new FontFamily("Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        _core = new Point(w / 2, 46);
        _coreR = 15;
        double pad = 26, ys = h - 38;
        for (int i = 0; i < 5; i++)
        {
            _stationAt[i] = new Point(pad + i * (w - 2 * pad) / 4, ys);
            var a = new Point(_core.X, _core.Y + _coreR);
            var b = new Point(_stationAt[i].X, ys - 13);
            _links[i] = (a, new Point(_stationAt[i].X * 0.8 + _core.X * 0.2, _core.Y + 22), b);
        }

        bool live = t.IsLive;
        var core = CoreColor(t);
        int at = (int)t.Station;
        _hasBeam = t.Mood == ActivityMood.Live;
        _inward = t.Station == ActivityStation.Explore;
        _spin = live;
        _waiting = t.Mood == ActivityMood.Waiting;
        _thinking = t.Mood == ActivityMood.Live && t.Station == ActivityStation.Think;
        _flashOn = t.IsFlashing(DateTime.Now);
        _flashAt = t.ErrorStation;
        _burst = t.Mood is ActivityMood.Done or ActivityMood.Failed or ActivityMood.Stopped;
        _speed = t.Station switch { ActivityStation.Run => 1.7, ActivityStation.Edit => 1.3, ActivityStation.Think => 0.8, _ => 1.1 };

        using var dc = _root.Open();
        // the stage: a sunken panel with a faint dot grid and a scan band
        var stage = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(Solid(_base, 200), PenOf(Solid(_line), 1), new Rect(0.5, 0.5, w - 1, h - 1), 10, 10);
        dc.PushClip(new RectangleGeometry(stage, 10, 10));
        var dot = Solid(_line, 150);
        for (double y = 10; y < h; y += 14)
            for (double x = 10 + (((int)(y / 14)) % 2) * 7; x < w; x += 14)
                dc.DrawRectangle(dot, null, new Rect(x, y, 1.2, 1.2));
        if (live)
        {
            var band = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            var c = _station[at];
            band.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0));
            band.GradientStops.Add(new GradientStop(Color.FromArgb(34, c.R, c.G, c.B), 0.5));
            band.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            band.Freeze();
            dc.PushTransform(_scan);
            dc.DrawRectangle(band, null, new Rect(-60, 0, 60, h));
            dc.Pop();
        }

        // the links: faint for every station, brighter where it has been this turn, a beam to where it is
        for (int i = 0; i < 5; i++)
        {
            var (a, c, b) = _links[i];
            var g = new StreamGeometry();
            using (var sg = g.Open()) { sg.BeginFigure(a, false, false); sg.QuadraticBezierTo(c, b, true, false); }
            g.Freeze();
            bool here = _hasBeam && i == at;
            var pen = here ? PenOf(Solid(_station[i], 230), 2.2)
                : t.Visited((ActivityStation)i) && t.Mood != ActivityMood.Idle ? PenOf(Solid(_station[i], 110), 1.2)
                : PenOf(Solid(_line, 200), 1, DashStyles.Dot);
            if (here) dc.DrawGeometry(null, PenOf(Solid(_station[i], 60), 6), g);
            dc.DrawGeometry(null, pen, g);
        }

        // packets on the beam
        if (_hasBeam)
            for (int k = 0; k < _packets.Length; k++)
            {
                using (var pdc = _packetFade[k].Open())
                    pdc.DrawGeometry(Solid(_station[at]), PenOf(Solid(_text, 150), 0.8), _packets[k]);
                dc.DrawDrawing(_packetFade[k]);
            }

        // the stations
        for (int i = 0; i < 5; i++)
        {
            var p = _stationAt[i];
            bool here = live && i == at;
            bool been = t.Visited((ActivityStation)i) && t.Mood != ActivityMood.Idle;
            var col = _station[i];
            if (here)
            {
                _haloScale.CenterX = p.X; _haloScale.CenterY = p.Y;
                using (var hdc = _halo.Open())
                {
                    hdc.PushTransform(_haloScale);
                    hdc.DrawEllipse(Glow(col), null, p, 26, 26);
                    hdc.Pop();
                }
                dc.DrawDrawing(_halo);
            }
            dc.DrawEllipse(Solid(_base), null, p, 12.5, 12.5);
            dc.DrawEllipse(here ? Solid(col, 70) : been ? Solid(col, 30) : Solid(_line, 60),
                           PenOf(here ? Solid(col) : been ? Solid(col, 150) : Solid(_line), here ? 2 : 1.2), p, 12, 12);
            var glyph = new FormattedText(StationGlyph[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, icons, 11,
                                          here ? Solid(_text) : been ? Solid(col) : Solid(_faint), dip);
            dc.DrawText(glyph, new Point(p.X - glyph.Width / 2, p.Y - glyph.Height / 2));
            var label = new FormattedText(LocalizedResources.Instance[StationKey[i]], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                          here ? uiBold : uiFont, 10, here ? Solid(col) : been ? Solid(_muted) : Solid(_faint), dip)
                        { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, MaxTextWidth = Math.Max(20, (w - 2 * pad) / 4) };
            dc.DrawText(label, new Point(p.X - label.Width / 2, p.Y + 15));
            if (i == (int)_flashAt)
            {
                using (var fdc = _flash.Open())
                {
                    fdc.DrawEllipse(Solid(_danger, 60), PenOf(Solid(_danger), 2.4), p, 15, 15);
                    fdc.DrawEllipse(null, PenOf(Solid(_danger, 120), 1.2), p, 20, 20);
                }
                dc.DrawDrawing(_flash);
            }
        }

        // satellites behind the core
        _satNodes.Clear();
        var orbitPen = PenOf(Solid(_line, 170), 1, DashStyles.Dash);
        if (t.Satellites.Count > 0)
        {
            dc.PushTransform(new RotateTransform(-8, _core.X, _core.Y));
            dc.DrawEllipse(null, orbitPen, _core, 60, 19);
            dc.Pop();
        }
        foreach (var s in t.Satellites)
        {
            var node = (s, new EllipseGeometry(), new EllipseGeometry(), new DrawingGroup(), new DrawingGroup());
            _satNodes.Add(node);
            var sc = s.State switch { "done" => _success, "failed" => _danger, "stopped" => _muted, _ => _station[4] };
            using (var bdc = node.Item4.Open()) bdc.DrawGeometry(Solid(sc, 170), null, node.Item2);
            dc.DrawDrawing(node.Item4);
        }

        // the orbits and their comets
        dc.PushTransform(new RotateTransform(-16, _core.X, _core.Y));
        dc.DrawEllipse(null, PenOf(Solid(core, live ? (byte)90 : (byte)50), 1), _core, 28, 9);
        dc.Pop();
        dc.PushTransform(new RotateTransform(24, _core.X, _core.Y));
        dc.DrawEllipse(null, PenOf(Solid(core, live ? (byte)60 : (byte)35), 1), _core, 36, 12);
        dc.Pop();
        if (live)
            for (int i = 0; i < _comets.Length; i++)
            {
                byte a = (byte)(230 - (i % 6) * 36);
                dc.DrawGeometry(Solid(i < 6 ? core : _text, a), null, _comets[i]);
            }

        // the core: glow, disc, ring, glyph (it shakes after a failed tool)
        dc.PushTransform(_shake);
        _glowScale.CenterX = _core.X; _glowScale.CenterY = _core.Y;
        dc.PushTransform(_glowScale);
        dc.DrawEllipse(Glow(core), null, _core, _coreR * 2.3, _coreR * 2.3);
        dc.Pop();
        var disc = new RadialGradientBrush { GradientOrigin = new Point(0.35, 0.3), Center = new Point(0.45, 0.4), RadiusX = 0.7, RadiusY = 0.7 };
        disc.GradientStops.Add(new GradientStop(Color.FromArgb(255, (byte)Math.Min(255, core.R + 70), (byte)Math.Min(255, core.G + 70), (byte)Math.Min(255, core.B + 70)), 0));
        disc.GradientStops.Add(new GradientStop(core, 0.55));
        disc.GradientStops.Add(new GradientStop(Color.FromArgb(255, (byte)(core.R * 0.55), (byte)(core.G * 0.55), (byte)(core.B * 0.55)), 1));
        disc.Freeze();
        dc.DrawEllipse(t.Mood == ActivityMood.Idle ? Solid(_muted, 120) : disc, PenOf(Solid(_text, 120), 1), _core, _coreR, _coreR);
        var cg = new FormattedText(CoreGlyph(t), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, icons, 13, Brushes.White, dip);
        dc.DrawText(cg, new Point(_core.X - cg.Width / 2, _core.Y - cg.Height / 2));
        dc.Pop();

        // sparks round the core while it thinks
        for (int i = 0; i < _sparks.Length; i++)
        {
            double ang = i * Math.PI / 3 + 0.3;
            var sp = new Point(_core.X + Math.Cos(ang) * (_coreR + 7), _core.Y + Math.Sin(ang) * (_coreR + 7));
            using (var sdc = _sparks[i].Open()) sdc.DrawEllipse(Solid(_text, 220), null, sp, 1.4, 1.4);
            _sparks[i].Opacity = 0;
            dc.DrawDrawing(_sparks[i]);
        }

        // ripples: waiting on the owner, or the end burst
        var rippleColor = _waiting ? _wait : core;
        for (int i = 0; i < _ripples.Length; i++)
        {
            _ripples[i].Center = _core;
            using (var rdc = _rippleFade[i].Open()) rdc.DrawGeometry(null, PenOf(Solid(rippleColor, 220), 1.6), _ripples[i]);
            _rippleFade[i].Opacity = 0;
            dc.DrawDrawing(_rippleFade[i]);
        }

        // satellites in front of the core
        foreach (var node in _satNodes)
        {
            var s = node.Sat;
            var sc = s.State switch { "done" => _success, "failed" => _danger, "stopped" => _muted, _ => _station[4] };
            using (var fdc = node.FrontFade.Open()) fdc.DrawGeometry(Solid(sc), PenOf(Solid(_text, 160), 0.8), node.Front);
            dc.DrawDrawing(node.FrontFade);
        }
        if (t.Agents > t.Satellites.Count)
        {
            var more = new FormattedText("+" + (t.Agents - t.Satellites.Count), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                         uiBold, 10, Solid(_station[4]), dip);
            dc.DrawText(more, new Point(_core.X + 66, _core.Y - 6));
        }
        dc.Pop();   // clip
    }

    // ─── a frame: numbers only ───

    private void Animate(double t, bool still)
    {
        FrameCount++;
        var track = Track;
        double w = _builtSize.Width, h = _builtSize.Height;
        if (w <= 0) return;

        _scan.X = still ? -100 : (t * 70 % (w + 160)) - 40;
        double breath = 1 + (_spin ? 0.08 : 0.03) * Math.Sin(t * 2.6);
        _glowScale.ScaleX = _glowScale.ScaleY = breath;
        double hal = 0.85 + 0.2 * (0.5 + 0.5 * Math.Sin(t * 4.2));
        _haloScale.ScaleX = _haloScale.ScaleY = hal;

        // a failed tool: its station flashes and the core shakes, fading out over the flash
        double left = track == null ? 0 : (track.ErrorUntil - DateTime.Now).TotalSeconds / ActivityTrack.ErrorFlash.TotalSeconds;
        bool flashing = !still && left > 0;
        _flash.Opacity = flashing ? 0.4 + 0.6 * Math.Abs(Math.Sin(t * 9)) : track is { Mood: ActivityMood.Failed, Failures: > 0 } ? 0.7 : 0;
        _shake.X = flashing ? 2.2 * Math.Sin(t * 47) * left : 0;

        // comets: six on each orbit, a head and its tail
        if (_spin)
            for (int ring = 0; ring < 2; ring++)
            {
                double a = ring == 0 ? 28 : 36, b = ring == 0 ? 9 : 12, tilt = (ring == 0 ? -16 : 24) * Math.PI / 180;
                double head = (ring == 0 ? 1 : -0.7) * t * 2.4 * _speed + ring * 2.1;
                for (int k = 0; k < 6; k++)
                {
                    double th = head - (ring == 0 ? 1 : -1) * k * 0.16;
                    var e = _comets[ring * 6 + k];
                    e.Center = Orbit(_core, a, b, tilt, th);
                    e.RadiusX = e.RadiusY = (k == 0 ? 2.4 : 2.0 - k * 0.28);
                }
            }

        // packets: in from the station while it reads, out to it while it changes or runs
        if (_hasBeam && track != null)
        {
            var (A, C, B) = _links[(int)track.Station];
            for (int k = 0; k < _packets.Length; k++)
            {
                double u = (t * 0.75 * _speed + k / 3.0) % 1.0;
                if (_inward) u = 1 - u;
                _packets[k].Center = Bezier(A, C, B, u);
                _packets[k].RadiusX = _packets[k].RadiusY = 2.6;
                _packetFade[k].Opacity = Math.Sin(Math.PI * u);
            }
        }

        // sparks while it thinks
        for (int i = 0; i < _sparks.Length; i++)
            _sparks[i].Opacity = _thinking ? Math.Max(0, Math.Sin(t * 3.1 + i * 1.7)) : 0;

        // ripples: two at a time while it waits; one ring out at the end of the turn
        double burstAge = t - _burstFrom;
        for (int i = 0; i < _ripples.Length; i++)
        {
            double op = 0, r = _coreR;
            if (_waiting && i < 2 && !still)
            {
                double p = (t / 1.8 + i / 2.0) % 1.0;
                r = _coreR + 4 + 30 * p; op = 0.9 * (1 - p);
            }
            else if (_waiting && i == 0)
            { r = _coreR + 9; op = 0.6; }
            else if (_burst && !still && i == 2 && burstAge >= 0 && burstAge < BurstSeconds)
            {
                double p = burstAge / BurstSeconds;
                r = _coreR + 6 + 46 * p; op = 1 - p;
            }
            _ripples[i].RadiusX = _ripples[i].RadiusY = r;
            _rippleFade[i].Opacity = op;
        }

        // satellites: round the core; behind it on the far side of the orbit, in front on the near side
        double tilt8 = -8 * Math.PI / 180;
        foreach (var (sat, back, front, backFade, frontFade) in _satNodes)
        {
            double th = sat.Slot * 2 * Math.PI + (sat.IsRunning ? t * 0.9 : t * 0.15);
            var p = Orbit(_core, 60, 19, tilt8, th);
            double depth = Math.Sin(th);
            double r = 5.6 * (0.8 + 0.2 * depth);
            back.Center = front.Center = p;
            back.RadiusX = back.RadiusY = front.RadiusX = front.RadiusY = r;
            backFade.Opacity = depth < 0 ? 0.55 : 0;
            frontFade.Opacity = depth >= 0 ? 1 : 0;
        }
    }

    private static Point Orbit(Point c, double a, double b, double tilt, double th)
    {
        double x = a * Math.Cos(th), y = b * Math.Sin(th);
        double ct = Math.Cos(tilt), st = Math.Sin(tilt);
        return new Point(c.X + x * ct - y * st, c.Y + x * st + y * ct);
    }

    private static Point Bezier(Point a, Point c, Point b, double u)
    {
        double v = 1 - u;
        return new Point(v * v * a.X + 2 * v * u * c.X + u * u * b.X, v * v * a.Y + 2 * v * u * c.Y + u * u * b.Y);
    }
}
