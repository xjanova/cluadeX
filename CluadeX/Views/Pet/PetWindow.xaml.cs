using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CluadeX.Services.Pet;

namespace CluadeX.Views.Pet;

/// <summary>
/// The pet on the desktop: a small transparent, topmost window that never takes focus, moved around the whole
/// screen. It stands on the taskbar and on the top edges of windows (PetWorld), falls when its floor goes away,
/// rides along when the window under it moves, and can be clicked, patted, picked up and thrown.
/// </summary>
public partial class PetWindow : Window
{
    private enum Act { Idle, Walk, Sit, Sleep, Chase, Hatching }

    private readonly PetService _svc;
    private readonly PetBody _body = new();
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Dictionary<string, (BitmapSource bmp, int emptyRows)> _frames = new();

    private List<PetFloor> _floors = new();
    private List<PetMonitor> _monitors = new();
    private PetRect _screen;
    private nint _hwnd;
    private double _scale = 1;
    private double _last, _scanAt, _decideAt, _talkAt, _bubbleUntil, _faceUntil, _actUntil, _squashUntil, _wobbleUntil;
    private Act _act = Act.Idle;
    private double _walkTarget;
    private int _facing = 1;
    private PetFace _face = PetFace.Normal;
    private bool _pressed, _dragging, _thrown, _hiddenForFullScreen, _sleepingForIdle;
    private (int x, int y) _pressAt;
    private double _grabDx, _grabDy;
    private readonly Queue<(double t, double x, double y)> _dragTrail = new();
    private readonly List<double> _clickTimes = new();
    private Action? _onHatched;
    private int _lastLateNightHour = -1;
    private DateTime _sessionStart = DateTime.Now;
    private bool _saidLongSession, _saidMorning;
    private int _pick;

    public PetWindow(PetService svc)
    {
        _svc = svc;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            PetDesktop.MakeOverlay(_hwnd);
            _scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            Enter();
        };
        _svc.Moment += OnMoment;
        _svc.Changed += OnChanged;
        Sprite.MouseLeftButtonDown += Sprite_Down;
        Sprite.MouseMove += Sprite_Move;
        Sprite.MouseLeftButtonUp += Sprite_Up;
        Sprite.MouseRightButtonUp += (_, e) => { ShowMenu(); e.Handled = true; };
        // Another window popped up mid-drag (a dialog, a permission prompt) and took the button release: without this
        // the pet stayed glued to the cursor and swallowed every click on the PC.
        Sprite.LostMouseCapture += (_, _) => { if (_pressed || _dragging) EndPress(click: false); };
        _tick.Tick += (_, _) => Frame();
        Closed += (_, _) =>
        {
            _tick.Stop();
            _svc.Moment -= OnMoment;
            _svc.Changed -= OnChanged;
        };
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>For the harness: what it is doing and where.</summary>
    public string DebugState => $"act={_act} grounded={_body.Grounded} floor={_body.Floor?.Kind}@{_body.Floor?.Y} x={_body.X:F0} y={_body.Y:F0} hidden={_hiddenForFullScreen}";
    private int Px(double dip) => (int)Math.Round(dip * _scale);

    /// <summary>Drop in from above the CluadeX window (or the middle of the main screen).</summary>
    private void Enter()
    {
        Scan();
        var main = Application.Current?.MainWindow;
        var mainRect = main != null ? PetDesktop.WindowRect(new WindowInteropHelper(main).Handle) : default;
        var mon = _monitors.FirstOrDefault();
        if (mainRect.Width > 0 && main!.WindowState == WindowState.Normal)
        {
            _body.X = mainRect.Left + mainRect.Width * (0.25 + _rng.NextDouble() * 0.5);
            _body.Y = Math.Max(mon.Work.Top + Px(60), mainRect.Top - Px(220));
        }
        else
        {
            _body.X = mon.Work.Left + mon.Work.Width * 0.75;
            _body.Y = mon.Work.Top + Px(80);
        }
        _body.Grounded = false;
        _last = Now;
        _decideAt = Now + 2;
        _talkAt = Now + 40 + _rng.Next(40);
        Place();
        _tick.Start();
        if (!_svc.IsHatched) Say(PetMoment.EggIdle);
        else Say(DateTime.Now.Hour < 10 ? PetMoment.Morning : PetMoment.Hello);
        _saidMorning = DateTime.Now.Hour < 10;
    }

    // ─── the loop ────────────────────────────────────────────────────────

    private void Frame()
    {
        double now = Now, dt = Math.Min(0.05, now - _last);
        _last = now;

        if (now >= _scanAt) { Scan(); _scanAt = now + 0.4; }
        if (_hiddenForFullScreen) return;

        // The button is no longer down but no release reached us (it went to a window that popped up): let go.
        if ((_dragging || _pressed) && !PetDesktop.LeftButtonDown()) EndPress(click: false);

        if (_dragging)
        {
            var (cx, cy) = PetDesktop.Cursor();
            _body.X = cx - _grabDx;
            _body.Y = cy - _grabDy;
            _dragTrail.Enqueue((now, _body.X, _body.Y));
            while (_dragTrail.Count > 0 && now - _dragTrail.Peek().t > 0.12) _dragTrail.Dequeue();
        }
        else
        {
            bool wasFalling = !_body.Grounded;
            double fallSpeed = _body.Vy;
            if (PetWorld.Step(_body, _floors, dt, _screen) && wasFalling)
                Landed(fallSpeed);
            if (_body.Grounded) Behave(now);
        }

        Talk(now);
        Draw(now);
        Place();
    }

    private void Scan()
    {
        _monitors = PetDesktop.Monitors();
        if (_monitors.Count == 0) return;
        _screen = new PetRect(_monitors.Min(m => m.Bounds.Left), _monitors.Min(m => m.Bounds.Top),
                              _monitors.Max(m => m.Bounds.Right), _monitors.Max(m => m.Bounds.Bottom));
        var skip = new HashSet<nint> { _hwnd };
        _floors = PetWorld.Floors(_monitors, PetDesktop.Windows(skip), Px(90));
        PetWorld.Follow(_body, _floors);

        var ours = new HashSet<nint> { _hwnd };
        if (Application.Current?.MainWindow is { } mw) ours.Add(new WindowInteropHelper(mw).Handle);
        bool fs = PetDesktop.FullScreenAppInFront(_monitors, ours);
        if (fs != _hiddenForFullScreen)
        {
            _hiddenForFullScreen = fs;
            Visibility = fs ? Visibility.Hidden : Visibility.Visible;
        }
    }

    private void Landed(double speed)
    {
        if (speed > 700) { _squashUntil = Now + 0.18; }
        if (_thrown)
        {
            _thrown = false;
            Say(PetMoment.Dropped);
            if (_svc.IsHatched) SetFace(speed > 1500 ? PetFace.Sad : PetFace.Happy, 1.5);
        }
        else if (_svc.IsHatched && _rng.Next(6) == 0 && _body.Floor is { } f)
            Say(f.Kind == PetFloorKind.Window ? PetMoment.OnWindow : PetMoment.OnTaskbar);
    }

    // ─── behaviour ───────────────────────────────────────────────────────

    private void Behave(double now)
    {
        if (!_svc.IsHatched || _act == Act.Hatching)
        {
            _body.Vx = 0;
            // The egg rocks now and then; more often the closer it is to hatching.
            if (_act != Act.Hatching && now >= _decideAt)
            {
                _wobbleUntil = now + 0.8;
                // Waiting for the AI's answer: it hardly stops shaking.
                _decideAt = _svc.HatchPending ? now + 1.2 : now + Math.Max(2.5, 9 - 7 * _svc.HatchProgress) + _rng.NextDouble() * 3;
            }
            return;
        }

        // Asleep because nobody is at the PC: wake on the first input.
        double idle = PetDesktop.IdleSeconds();
        if (_act == Act.Sleep && _sleepingForIdle && idle < 2)
        {
            _sleepingForIdle = false;
            Say(PetMoment.WakeUp);
            Do(Act.Idle, 2);
        }
        else if (_act != Act.Sleep && idle > 180)
        {
            _sleepingForIdle = true;
            Say(PetMoment.FallAsleep);
            Do(Act.Sleep, 9999);
        }

        switch (_act)
        {
            case Act.Walk:
            case Act.Chase:
            {
                if (_act == Act.Chase) _walkTarget = PetDesktop.Cursor().x;
                if (_body.Floor is { } f) _walkTarget = Math.Clamp(_walkTarget, f.X1 + Px(14), f.X2 - Px(14));
                double d = _walkTarget - _body.X;
                if (Math.Abs(d) < Px(4) || now >= _actUntil) { _body.Vx = 0; Do(Act.Idle, 1.5 + _rng.NextDouble() * 2); break; }
                _facing = Math.Sign(d);
                double speed = (_act == Act.Chase ? 95 : 55) * (_svc.State.Personality == PetPersonality.Hyper ? 1.6 : 1.0);
                _body.Vx = _facing * Px(speed);
                break;
            }
            default:
                _body.Vx = 0;
                break;
        }

        if (now >= _actUntil && _act is Act.Idle or Act.Sit or Act.Sleep && !_sleepingForIdle) Decide(now);
    }

    private void Do(Act act, double seconds)
    {
        _act = act;
        _actUntil = Now + seconds;
    }

    private void Decide(double now)
    {
        var p = _svc.State.Personality;
        bool night = DateTime.Now.Hour is >= 22 or < 5;
        // idle, walk, sit, sleep, chase, jump
        double[] w = p switch
        {
            PetPersonality.Cheerful => [3, 4, 2, 0.5, 1, 2],
            PetPersonality.Grumpy => [3, 2, 4, 1, 0.3, 0.5],
            PetPersonality.Sassy => [2, 3, 2, 0.5, 3, 1.5],
            PetPersonality.Shy => [3, 3, 3, 1, 0, 0.5],
            PetPersonality.Sleepy => [2, 1.5, 3, 4, 0.2, 0.3],
            PetPersonality.Hyper => [1, 5, 0.5, 0.2, 2, 4],
            PetPersonality.Caring => [3, 3, 2, 0.5, 1.5, 1.5],
            _ => [3, 2, 4, 0.5, 0.5, 1],
        };
        if (night) w[3] *= 3;
        if (!_svc.Mischief) w[4] = 0;
        double roll = _rng.NextDouble() * w.Sum();
        int pick = 0;
        while (pick < w.Length - 1 && roll >= w[pick]) { roll -= w[pick]; pick++; }

        switch (pick)
        {
            case 0: Do(Act.Idle, 2 + _rng.NextDouble() * 3); break;
            case 1:
            {
                var f = _body.Floor!.Value;
                // Shy ones head for an edge; others anywhere along the floor.
                _walkTarget = p == PetPersonality.Shy
                    ? (_rng.Next(2) == 0 ? f.X1 : f.X2)
                    : Math.Clamp(_body.X + (_rng.NextDouble() * 2 - 1) * Px(420), f.X1, f.X2);
                Do(Act.Walk, 12);
                break;
            }
            case 2: Do(Act.Sit, 5 + _rng.NextDouble() * 10); break;
            case 3: Do(Act.Sleep, 15 + _rng.NextDouble() * 25); break;
            case 4: Do(Act.Chase, 5 + _rng.NextDouble() * 6); break;
            case 5: if (!JumpSomewhere(p == PetPersonality.Caring)) Do(Act.Idle, 2); break;
        }
    }

    /// <summary>Hop onto another floor in reach — a window's top edge above or below, or the taskbar.</summary>
    private bool JumpSomewhere(bool towardApp)
    {
        nint mainHwnd = Application.Current?.MainWindow is { } mw ? new WindowInteropHelper(mw).Handle : 0;
        var candidates = _floors.Where(f => !Equals(f, _body.Floor)
                                            && f.Y > _body.Y - Px(330) && f.Y < _body.Y + Px(650)
                                            && Math.Min(Math.Abs(f.X1 - _body.X), Math.Abs(f.X2 - _body.X)) < Px(480)
                                            && f.X2 - f.X1 > Px(60)).ToList();
        if (towardApp && candidates.Any(f => f.Owner == mainHwnd)) candidates = candidates.Where(f => f.Owner == mainHwnd).ToList();
        if (candidates.Count == 0) return false;
        var target = candidates[_rng.Next(candidates.Count)];
        double tx = Math.Clamp(_body.X, target.X1 + Px(30), target.X2 - Px(30));
        var (vx, vy) = PetWorld.JumpTo(_body, tx, target.Y);
        _body.Vx = vx; _body.Vy = vy;
        _body.Grounded = false;
        _body.Floor = null;
        _facing = Math.Sign(vx) == 0 ? _facing : Math.Sign(vx);
        Do(Act.Idle, 1.5);
        return true;
    }

    // ─── talking ─────────────────────────────────────────────────────────

    private void Talk(double now)
    {
        if (now < _talkAt) return;
        _talkAt = now + 60 + _rng.Next(90);
        if (!_svc.IsHatched) { Say(_svc.HatchProgress > 0.85 ? PetMoment.EggAlmost : PetMoment.EggIdle); return; }
        var t = DateTime.Now;
        if (t.Hour is >= 23 or < 4 && _lastLateNightHour != t.Hour && PetDesktop.IdleSeconds() < 60)
        {
            _lastLateNightHour = t.Hour;
            Say(PetMoment.LateNight);
        }
        else if (!_saidLongSession && (t - _sessionStart).TotalMinutes > 90 && PetDesktop.IdleSeconds() < 60)
        {
            _saidLongSession = true;
            Say(PetMoment.LongSession);
        }
        else if (!_saidMorning && t.Hour is >= 6 and < 10)
        {
            _saidMorning = true;
            Say(PetMoment.Morning);
        }
        else if (_act != Act.Sleep) Say(PetMoment.Idle);
    }

    /// <summary>Small talk: it never covers a line that is still showing (a greeting, a build result …).</summary>
    private static bool IsSmallTalk(PetMoment m) => m is PetMoment.Idle or PetMoment.OnTaskbar or PetMoment.OnWindow
        or PetMoment.EggIdle or PetMoment.Dropped or PetMoment.PickedUp;

    private void Say(PetMoment m)
    {
        if (IsSmallTalk(m) && Bubble.Opacity > 0.5 && Now < _bubbleUntil) return;
        string line = _svc.Line(m, _pick++ + _rng.Next(7));
        if (line.Length == 0) return;
        BubbleText.Text = line;
        Bubble.Opacity = 1;
        _bubbleUntil = Now + Math.Clamp(2.5 + line.Length * 0.07, 3, 7);
    }

    private void SetFace(PetFace face, double seconds)
    {
        _face = face;
        _faceUntil = Now + seconds;
    }

    private void OnMoment(PetMoment m)
    {
        Dispatcher.BeginInvoke(() =>
        {
            Say(m);
            if (!_svc.IsHatched) { _wobbleUntil = Now + 0.8; return; }
            var p = _svc.State.Personality;
            switch (m)
            {
                case PetMoment.BuildFailed: SetFace(p is PetPersonality.Grumpy or PetPersonality.Sassy ? PetFace.Angry : PetFace.Sad, 3); break;
                case PetMoment.BuildPassed or PetMoment.TurnDone or PetMoment.Hello: SetFace(PetFace.Happy, 3); Hop(); break;
                case PetMoment.Error: SetFace(PetFace.Surprised, 2.5); break;
                case PetMoment.Hatched: SetFace(PetFace.Love, 4); Hop(); break;
                case PetMoment.PutAway: SetFace(PetFace.Happy, 2); break;
            }
            if (_act == Act.Sleep && m is not PetMoment.PutAway) { _sleepingForIdle = false; Do(Act.Idle, 2); }
        });
    }

    private void OnChanged() => Dispatcher.BeginInvoke(() => _frames.Clear());

    private void Hop()
    {
        if (!_body.Grounded) return;
        _body.Vy = -Px(520);
        _body.Grounded = false;
        _body.Floor = null;
    }

    // ─── hatching ────────────────────────────────────────────────────────

    /// <summary>Shake, crack, burst — then <paramref name="hatch"/> turns the egg into the pet.</summary>
    public void PlayHatch(Action hatch)
    {
        _onHatched = hatch;
        Do(Act.Hatching, 3.2);
        _wobbleUntil = Now + 3.2;
        Say(PetMoment.EggAlmost);
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            Fx.Text = "✨";
            Fx.Opacity = 1;
            _onHatched?.Invoke();
            _onHatched = null;
            _frames.Clear();
            Do(Act.Idle, 2);
            var fade = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            fade.Tick += (_, _) => { fade.Stop(); Fx.Opacity = 0; };
            fade.Start();
        };
        t.Start();
    }

    // ─── drawing ─────────────────────────────────────────────────────────

    private void Draw(double now)
    {
        if (now >= _bubbleUntil && Bubble.Opacity > 0) Bubble.Opacity = Math.Max(0, Bubble.Opacity - 0.08);
        if (now >= _faceUntil) _face = PetFace.Normal;

        string key;
        Func<uint[]> make;
        if (!_svc.IsHatched)
        {
            int stage = (int)(_svc.HatchProgress * 10);
            key = $"egg{stage}";
            make = () => PetArt.RenderEgg(_svc.State.EggHue, _svc.HatchProgress, _svc.State.EggSpots);
        }
        else
        {
            PetPose pose;
            PetFace face = _face;
            if (_dragging) { pose = PetPose.Dangle; face = PetFace.Surprised; }
            else if (!_body.Grounded) pose = PetPose.Jump;
            else if (_act == Act.Sleep) { pose = PetPose.Sleep; face = PetFace.Sleepy; }
            else if (_act == Act.Sit) pose = PetPose.Sit;
            else if (Math.Abs(_body.Vx) > 1) pose = ((int)(now * 6) % 2 == 0) ? PetPose.Walk1 : PetPose.Walk2;
            else pose = PetPose.Stand;
            // Blink every few seconds.
            if (face == PetFace.Normal && (now % 4.3) < 0.15) face = PetFace.Blink;
            key = $"{pose}:{face}";
            var look = _svc.State.Look;
            make = () => PetArt.Render(look, pose, face);
        }
        if (!_frames.TryGetValue(key, out var frame))
        {
            var px = make();
            int empty = 0;
            for (int row = PetArt.Size - 1; row >= 0; row--)
            {
                bool any = false;
                for (int x = 0; x < PetArt.Size; x++) if ((px[row * PetArt.Size + x] >> 24) != 0) { any = true; break; }
                if (any) break;
                empty++;
            }
            frame = (PetService.ToBitmap(px), empty);
            _frames[key] = frame;
        }
        Sprite.Source = frame.bmp;

        double unit = Sprite.Width / PetArt.Size;
        Flip.ScaleX = _facing >= 0 ? 1 : -1;
        bool squash = now < _squashUntil;
        Flip.ScaleY = squash ? 0.86 : 1;
        Flip.ScaleX *= squash ? 1.12 : 1;
        Wobble.Angle = now < _wobbleUntil ? Math.Sin(now * (_act == Act.Hatching ? 40 : 18)) * (_act == Act.Hatching ? 12 : 7) : 0;
        Nudge.Y = frame.emptyRows * unit;
        Nudge.X = _act == Act.Hatching ? Math.Sin(now * 55) * 2 : 0;
        // Zzz while asleep
        if (_act == Act.Sleep && _svc.IsHatched) { Fx.Text = (int)(now * 1.5) % 3 == 0 ? "z" : (int)(now * 1.5) % 3 == 1 ? "zZ" : "zZz"; Fx.Opacity = 0.8; Fx.Foreground = System.Windows.Media.Brushes.SlateBlue; }
        else if (Fx.Text.StartsWith("z")) Fx.Opacity = 0;
    }

    private void Place()
    {
        if (_hwnd == 0) return;
        int w = Px(Width), h = Px(Height);
        PetDesktop.Move(_hwnd, (int)Math.Round(_body.X - w / 2.0), (int)Math.Round(_body.Y - h));
    }

    // ─── mouse ───────────────────────────────────────────────────────────

    private void Sprite_Down(object sender, MouseButtonEventArgs e)
    {
        _pressed = true;
        _pressAt = PetDesktop.Cursor();
        Sprite.CaptureMouse();
        e.Handled = true;
    }

    private void Sprite_Move(object sender, MouseEventArgs e)
    {
        if (!_pressed || _dragging) return;
        var (x, y) = PetDesktop.Cursor();
        if (Math.Abs(x - _pressAt.x) + Math.Abs(y - _pressAt.y) < Px(6)) return;
        _dragging = true;
        _grabDx = x - _body.X;
        _grabDy = y - _body.Y;
        _body.Grounded = false;
        _body.Floor = null;
        _body.Vx = _body.Vy = 0;
        _dragTrail.Clear();
        if (_act is Act.Sleep or Act.Walk or Act.Chase) Do(Act.Idle, 2);
        Say(PetMoment.PickedUp);
    }

    private void Sprite_Up(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        EndPress(click: true);
    }

    /// <summary>The press is over: a drag ends in a throw/drop, a short press is a click. Safe to call twice.</summary>
    private void EndPress(bool click)
    {
        // State first: releasing the capture raises LostMouseCapture, which calls back in here.
        bool wasPressed = _pressed, wasDragging = _dragging;
        _pressed = false;
        _dragging = false;
        if (Sprite.IsMouseCaptured) Sprite.ReleaseMouseCapture();
        if (!wasPressed && !wasDragging) return;
        if (wasDragging)
        {
            // Thrown with the speed the mouse had over the last ~0.1 s.
            if (_dragTrail.Count >= 2)
            {
                var a = _dragTrail.Peek();
                var b = _dragTrail.Last();
                double dt = Math.Max(0.016, b.t - a.t);
                _body.Vx = Math.Clamp((b.x - a.x) / dt, -Px(1600), Px(1600));
                _body.Vy = Math.Clamp((b.y - a.y) / dt, -Px(1600), Px(1600));
            }
            _thrown = true;
            return;
        }
        if (click) Clicked();
    }

    private void Clicked()
    {
        double now = Now;
        _clickTimes.Add(now);
        _clickTimes.RemoveAll(t => now - t > 3);
        _svc.CountPoke();
        if (!_svc.IsHatched)
        {
            _wobbleUntil = now + 0.8;
            Say(PetMoment.EggPoked);
            return;
        }
        if (_act == Act.Sleep) { _sleepingForIdle = false; Do(Act.Idle, 2); Say(PetMoment.WakeUp); return; }
        bool doubleClick = _clickTimes.Count >= 2 && now - _clickTimes[^2] < 0.35;
        if (_clickTimes.Count >= 6)
        {
            _svc.AddAffection(-2);
            SetFace(PetFace.Angry, 2.5);
            Say(PetMoment.PokedALot);
            // Runs off a little.
            if (_body.Floor is { } f) { _walkTarget = Math.Clamp(_body.X + (_rng.Next(2) == 0 ? -1 : 1) * Px(260), f.X1, f.X2); Do(Act.Walk, 4); }
        }
        else if (doubleClick)
        {
            _svc.AddAffection(2);
            SetFace(PetFace.Love, 2.5);
            Say(PetMoment.Patted);
            Hearts();
        }
        else
        {
            _svc.AddAffection(1);
            SetFace(_svc.State.Personality is PetPersonality.Grumpy ? PetFace.Angry : PetFace.Surprised, 1.2);
            Say(PetMoment.Poked);
            Hop();
        }
    }

    private void Hearts()
    {
        Fx.Text = "♥";
        Fx.Foreground = System.Windows.Media.Brushes.Crimson;
        Fx.Opacity = 1;
        var fade = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        fade.Tick += (_, _) => { fade.Stop(); if (Fx.Text == "♥") Fx.Opacity = 0; };
        fade.Start();
    }

    /// <summary>Pat from elsewhere (the title-bar chip).</summary>
    public void Pat()
    {
        if (!_svc.IsHatched) { _wobbleUntil = Now + 0.8; Say(PetMoment.EggPoked); return; }
        _svc.AddAffection(2);
        SetFace(PetFace.Love, 2.5);
        Say(PetMoment.Patted);
        Hearts();
    }

    private void ShowMenu()
    {
        bool th = _svc.Thai;
        var menu = new ContextMenu();
        MenuItem Item(string text, Action act) { var mi = new MenuItem { Header = text }; mi.Click += (_, _) => act(); menu.Items.Add(mi); return mi; }
        menu.Items.Add(new MenuItem { Header = $"{_svc.Title} — {_svc.Subtitle}", IsEnabled = false });
        menu.Items.Add(new Separator());
        if (_svc.IsHatched)
        {
            Item(th ? "ลูบหัว" : "Pat", Pat);
            Item(_act == Act.Sleep ? (th ? "ปลุก" : "Wake up") : (th ? "ไปนอน" : "Take a nap"), () =>
            {
                if (_act == Act.Sleep) { _sleepingForIdle = false; Do(Act.Idle, 2); Say(PetMoment.WakeUp); }
                else { Do(Act.Sleep, 60); Say(PetMoment.FallAsleep); }
            });
            Item(th ? "เดินตามเมาส์" : "Follow my mouse", () => Do(Act.Chase, 20));
            Item(th ? "มาหาที่หน้าต่าง CluadeX" : "Come to the CluadeX window", () => { if (!JumpSomewhere(towardApp: true)) Say(PetMoment.Idle); });
        }
        Item(th ? "รายละเอียด…" : "About…", () =>
        {
            BubbleText.Text = _svc.Details;
            Bubble.Opacity = 1;
            _bubbleUntil = Now + 9;
        });
        menu.Items.Add(new Separator());
        var talk = Item(th ? "พูดได้" : "Talks", () => _svc.SetTalks(!_svc.Talks));
        talk.IsCheckable = true; talk.IsChecked = _svc.Talks;
        var tease = Item(th ? "แกล้งได้ (เดินตามเมาส์เอง)" : "Teases (follows the mouse on its own)", () => _svc.SetMischief(!_svc.Mischief));
        tease.IsCheckable = true; tease.IsChecked = _svc.Mischief;
        menu.Items.Add(new Separator());
        Item(th ? "เก็บเข้าบ้าน (ซ่อน)" : "Put away (hide)", _svc.PutAway);
        menu.IsOpen = true;
    }
}
