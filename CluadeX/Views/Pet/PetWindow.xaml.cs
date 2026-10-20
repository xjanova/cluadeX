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
    private enum Act { Idle, Walk, Sit, Sleep, Chase, Hatching, Climb, Hang }

    private readonly PetService _svc;
    private readonly PetBody _body = new();
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    // Background, not Render: a Render-priority timer ran ahead of the app's own input and layout at 30 fps, always —
    // asleep, idle or hidden (2026-10-20 stall audit). The interval also drops when nothing moves (see Frame).
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };

    // Where the user works (PetZones + the caret of the app in front), the screen's climbable edges, prank windows.
    private List<PetRect> _zones = new();
    private List<(int X, int Top, int Bottom, int Side)> _walls = new();
    private (int X, int Top, int Bottom, int Side)? _wall;
    private bool _hurry, _climbAfterWalk;
    private double _dodgeAt, _saidDodgeAt = -999, _climbTop, _poopAt, _biteAt, _prankPauseUntil;
    private readonly List<PetPrank> _pranks = new();
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
            ClearPranks();
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
        _poopAt = Now + 150 + _rng.Next(120);
        _biteAt = Now + 120 + _rng.Next(120);
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
        if (_hiddenForFullScreen) { SetPace(500); return; }

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
        else if (_act is Act.Climb or Act.Hang) Climbing(now, dt);
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

        // Nothing moving (sitting, asleep, standing still, no wobble, no bubble fading): 10 frames a second is plenty.
        bool calm = _body.Grounded && !_dragging && !_pressed && Math.Abs(_body.Vx) < 1 && now >= _wobbleUntil && now >= _squashUntil
                    && _act is Act.Idle or Act.Sit or Act.Sleep && (Bubble.Opacity <= 0 || now < _bubbleUntil);
        SetPace(calm ? 100 : 33);
    }

    private void SetPace(int ms)
    {
        if ((int)_tick.Interval.TotalMilliseconds != ms) _tick.Interval = TimeSpan.FromMilliseconds(ms);
    }

    // The pet's body inside its window (the 96 px sprite, not the bubble's room above it).
    private double HalfW => Px(40);
    private double BodyH => Px(88);

    // ─── keeping out of the way ──────────────────────────────────────────

    /// <summary>Standing on (or in front of) the box the user is typing in: step aside — or hop to another floor when
    /// this one has no clear spot. Wakes up first if it was asleep there.</summary>
    private void Dodge(double now)
    {
        if (_zones.Count == 0 || _body.Floor is not { } f) return;
        var x = PetWorld.DodgeX(f, _zones, _body.X, _body.Y, HalfW, BodyH, Px(8));
        if (x == null) return;
        if (_act == Act.Sleep) _sleepingForIdle = false;
        if (now - _saidDodgeAt > 30) { _saidDodgeAt = now; Say(PetMoment.Dodge); }
        _climbAfterWalk = false;
        if (double.IsNaN(x.Value))
        {
            if (JumpSomewhere(towardApp: false, avoidZones: true)) return;
            // Nowhere on this floor and nowhere to jump: walk off its nearer end and drop down.
            _walkTarget = _body.X - f.X1 < f.X2 - _body.X ? f.X1 - Px(40) : f.X2 + Px(40);
            _hurry = true;
            Do(Act.Walk, 6);
            return;
        }
        _walkTarget = x.Value;
        _hurry = true;
        Do(Act.Walk, 6);
    }

    private bool InZone(double x, double y) => _zones.Any(z => PetWorld.Overlaps(z, x, y, HalfW, BodyH, Px(8)));

    // ─── climbing the screen's edges ─────────────────────────────────────

    /// <summary>Walk to the end of this floor where it meets the edge of the screen, then climb it.</summary>
    private bool StartClimb()
    {
        if (_body.Floor is not { } f) return false;
        var reach = _walls.Where(w => _body.Y > w.Top + Px(260) && _body.Y <= w.Bottom + 2
                                      && (w.Side < 0 ? Math.Abs(f.X1 - w.X) <= Px(6) : Math.Abs(f.X2 - (w.X - 1)) <= Px(6))).ToList();
        if (reach.Count == 0) return false;
        var wall = reach[_rng.Next(reach.Count)];
        _wall = wall;
        _walkTarget = wall.Side < 0 ? f.X1 + Px(14) : f.X2 - Px(14);
        _climbAfterWalk = true;
        Do(Act.Walk, 20);
        return true;
    }

    private void BeginClimbing()
    {
        if (_wall is not { } w) return;
        _climbAfterWalk = false;
        _body.Grounded = false;
        _body.Floor = null;
        _body.Vx = _body.Vy = 0;
        _body.X = w.Side < 0 ? w.X + 2 : w.X - 2;
        double low = w.Top + Px(120), high = _body.Y - Px(160);
        _climbTop = high > low ? low + _rng.NextDouble() * (high - low) : low;
        _facing = -w.Side;
        Do(Act.Climb, 30);
    }

    private void Climbing(double now, double dt)
    {
        if (_wall is not { } w) { Do(Act.Idle, 1); _body.Grounded = false; return; }
        if (_act == Act.Climb)
        {
            _body.Y -= Px(75) * dt;
            if (_body.Y <= _climbTop || now >= _actUntil)
            {
                Do(Act.Hang, 3 + _rng.NextDouble() * 6);
                if (_rng.Next(2) == 0) Say(PetMoment.Climb);
            }
        }
        else if (now >= _actUntil || InZone(_body.X, _body.Y))
        {
            // Let go: a little push away from the wall, then gravity — it lands on whatever is below.
            _wall = null;
            _body.Grounded = false;
            _body.Vx = -w.Side * Px(140);
            _body.Vy = -Px(180);
            Do(Act.Idle, 1.5);
        }
    }

    // ─── prank mode ──────────────────────────────────────────────────────

    private void Prank(double now)
    {
        if (!_svc.Pranks || now < _prankPauseUntil || _act is Act.Sleep or Act.Hatching || _body.Floor is not { } f) return;
        if (now >= _poopAt && _act is Act.Idle or Act.Sit)
        {
            _poopAt = now + 240 + _rng.Next(360);
            if (_pranks.Count(p => p.What == PetPrank.Kind.Poop) < 3)
            {
                int px = (int)(_body.X - _facing * Px(50));
                if (px > f.X1 + Px(16) && px < f.X2 - Px(16) && !_zones.Any(z => PetWorld.Overlaps(z, px, _body.Y, Px(16), Px(30), Px(6))))
                {
                    var poop = PetPrank.Poop(px, (int)_body.Y, _scale, CleanPoop);
                    poop.Show();
                    _pranks.Add(poop);
                    SetFace(PetFace.Happy, 2);
                    Say(PetMoment.Poop);
                }
            }
        }
        if (now >= _biteAt)
        {
            _biteAt = now + 180 + _rng.Next(300);
            if (_pranks.Count(p => p.What == PetPrank.Kind.Bite) < 2 && BitePlace(f) is { } rect)
            {
                var bite = PetPrank.Bite(rect, _scale, _rng.Next());
                bite.Show();
                _pranks.Add(bite);
                SetFace(PetFace.Happy, 2.5);
                Say(PetMoment.Bite);
                Hop();
            }
        }
    }

    /// <summary>A patch beside the pet to nibble: into the window it stands on (just under its top edge), else above
    /// its floor — never over a work zone, always inside the screen.</summary>
    private PetRect? BitePlace(PetFloor f)
    {
        int w = Px(150 + _rng.Next(80)), h = Px(90 + _rng.Next(60));
        var mon = _monitors.FirstOrDefault(m => m.Bounds.Contains((int)_body.X, Math.Max(m.Bounds.Top, (int)_body.Y - 1)));
        if (mon.Bounds.Width == 0) return null;
        int side = _facing >= 0 ? 1 : -1;
        foreach (var (dx, below) in new[] { (side, true), (-side, true), (side, false), (-side, false) })
        {
            if (below && f.Kind != PetFloorKind.Window) continue;
            int left = dx > 0 ? (int)_body.X + Px(30) : (int)_body.X - Px(30) - w;
            int top = below ? (int)_body.Y + Px(14) : (int)_body.Y - Px(20) - h;
            var r = new PetRect(left, top, left + w, top + h);
            bool inside = r.Left >= mon.Work.Left && r.Right <= mon.Work.Right && r.Top >= mon.Work.Top && r.Bottom <= mon.Work.Bottom;
            bool clear = !_zones.Any(z => z.Left < r.Right + Px(30) && z.Right > r.Left - Px(30) && z.Top < r.Bottom + Px(30) && z.Bottom > r.Top - Px(30));
            if (inside && clear) return r;
        }
        return null;
    }

    private void CleanPoop(PetPrank p)
    {
        _pranks.Remove(p);
        p.Close();
        if (!_svc.IsHatched) return;
        SetFace(PetFace.Sad, 2);
        Say(PetMoment.Cleaned);
    }

    private void ClearPranks()
    {
        foreach (var p in _pranks.ToList()) { try { p.Close(); } catch { } }
        _pranks.Clear();
    }

    private void Scan()
    {
        _monitors = PetDesktop.Monitors();
        if (_monitors.Count == 0) return;
        _screen = new PetRect(_monitors.Min(m => m.Bounds.Left), _monitors.Min(m => m.Bounds.Top),
                              _monitors.Max(m => m.Bounds.Right), _monitors.Max(m => m.Bounds.Bottom));
        var skip = new HashSet<nint> { _hwnd };
        foreach (var p in _pranks) { var h = new WindowInteropHelper(p).Handle; if (h != 0) skip.Add(h); }
        _floors = PetWorld.Floors(_monitors, PetDesktop.Windows(skip), Px(90));
        PetWorld.Follow(_body, _floors);
        _walls = PetWorld.Walls(_monitors);

        // Where the user works: CluadeX's input boxes, and a band around the text caret of the app in front.
        _zones = PetZones.CluadeXAreas();
        if (PetDesktop.CaretRect() is { } caret)
            _zones.Add(new PetRect(caret.Left - Px(160), caret.Top - Px(36), caret.Right + Px(260), caret.Bottom + Px(36)));

        // Prank leftovers heal by themselves after a while.
        foreach (var p in _pranks.Where(p => DateTime.Now >= p.Until).ToList()) { _pranks.Remove(p); try { p.Close(); } catch { } }

        var ours = new HashSet<nint>(skip);
        if (Application.Current?.MainWindow is { } mw) ours.Add(new WindowInteropHelper(mw).Handle);
        bool fs = PetDesktop.FullScreenAppInFront(_monitors, ours);
        if (fs != _hiddenForFullScreen)
        {
            _hiddenForFullScreen = fs;
            Visibility = fs ? Visibility.Hidden : Visibility.Visible;
            foreach (var p in _pranks) p.Visibility = fs ? Visibility.Hidden : Visibility.Visible;   // never over a game or a film
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

        // Out of the way of the box the user is typing in (checked a few times a second).
        if (now >= _dodgeAt) { _dodgeAt = now + 0.3; Dodge(now); }

        switch (_act)
        {
            case Act.Walk:
            case Act.Chase:
            {
                if (_act == Act.Chase)
                {
                    _walkTarget = PetDesktop.Cursor().x;
                    if (InZone(_walkTarget, _body.Y)) { _body.Vx = 0; Do(Act.Idle, 2); break; }   // not into the typing area
                }
                // A dodge with no clear spot may walk off the edge on purpose; anything else stays on its floor.
                bool offEdge = _hurry && _body.Floor is { } ff && (_walkTarget < ff.X1 || _walkTarget > ff.X2);
                if (!offEdge && _body.Floor is { } f) _walkTarget = Math.Clamp(_walkTarget, f.X1 + Px(14), f.X2 - Px(14));
                double d = _walkTarget - _body.X;
                if (Math.Abs(d) < Px(4) || now >= _actUntil)
                {
                    _body.Vx = 0;
                    _hurry = false;
                    if (_climbAfterWalk && Math.Abs(d) < Px(4)) BeginClimbing();
                    else { _climbAfterWalk = false; Do(Act.Idle, 1.5 + _rng.NextDouble() * 2); }
                    break;
                }
                _facing = Math.Sign(d);
                double speed = (_act == Act.Chase ? 95 : _hurry ? 130 : 55) * (_svc.State.Personality == PetPersonality.Hyper ? 1.6 : 1.0);
                _body.Vx = _facing * Px(speed);
                break;
            }
            default:
                _body.Vx = 0;
                break;
        }

        Prank(now);
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
        // idle, walk, sit, sleep, chase, jump, climb (the screen's edge)
        double[] w = p switch
        {
            PetPersonality.Cheerful => [3, 4, 2, 0.5, 1, 2, 1],
            PetPersonality.Grumpy => [3, 2, 4, 1, 0.3, 0.5, 0.3],
            PetPersonality.Sassy => [2, 3, 2, 0.5, 3, 1.5, 1],
            PetPersonality.Shy => [3, 3, 3, 1, 0, 0.5, 0.5],
            PetPersonality.Sleepy => [2, 1.5, 3, 4, 0.2, 0.3, 0.2],
            PetPersonality.Hyper => [1, 5, 0.5, 0.2, 2, 4, 2.5],
            PetPersonality.Caring => [3, 3, 2, 0.5, 1.5, 1.5, 0.6],
            _ => [3, 2, 4, 0.5, 0.5, 1, 0.8],
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
                // Shy ones head for an edge; others anywhere along the floor — never into the typing area.
                _walkTarget = p == PetPersonality.Shy
                    ? (_rng.Next(2) == 0 ? f.X1 : f.X2)
                    : Math.Clamp(_body.X + (_rng.NextDouble() * 2 - 1) * Px(420), f.X1, f.X2);
                if (InZone(_walkTarget, _body.Y)) { Do(Act.Sit, 4); break; }
                Do(Act.Walk, 12);
                break;
            }
            case 2: Do(Act.Sit, 5 + _rng.NextDouble() * 10); break;
            case 3: Do(Act.Sleep, 15 + _rng.NextDouble() * 25); break;
            case 4: Do(Act.Chase, 5 + _rng.NextDouble() * 6); break;
            case 5: if (!JumpSomewhere(p == PetPersonality.Caring, avoidZones: true)) Do(Act.Idle, 2); break;
            case 6: if (!StartClimb()) Do(Act.Idle, 2); break;
        }
    }

    /// <summary>Hop onto another floor in reach — a window's top edge above or below, or the taskbar.</summary>
    private bool JumpSomewhere(bool towardApp, bool avoidZones = false)
    {
        nint mainHwnd = Application.Current?.MainWindow is { } mw ? new WindowInteropHelper(mw).Handle : 0;
        var candidates = _floors.Where(f => !Equals(f, _body.Floor)
                                            && f.Y > _body.Y - Px(330) && f.Y < _body.Y + Px(650)
                                            && Math.Min(Math.Abs(f.X1 - _body.X), Math.Abs(f.X2 - _body.X)) < Px(480)
                                            && f.X2 - f.X1 > Px(60)
                                            && (!avoidZones || !InZone(Math.Clamp(_body.X, f.X1 + Px(30), f.X2 - Px(30)), f.Y))).ToList();
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
            else if (_act == Act.Climb) pose = ((int)(now * 5) % 2 == 0) ? PetPose.Walk1 : PetPose.Walk2;
            else if (_act == Act.Hang) pose = PetPose.Dangle;
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
        // On the wall: lying along the screen's edge, feet on it (turned about its feet), swaying a little while it hangs.
        if (_act is Act.Climb or Act.Hang && _wall is { } wall)
            Wobble.Angle = (wall.Side < 0 ? 90 : -90) + (_act == Act.Hang ? Math.Sin(now * 2.2) * 6 : 0);
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
        if (_act is Act.Sleep or Act.Walk or Act.Chase or Act.Climb or Act.Hang) { _wall = null; _climbAfterWalk = false; _hurry = false; Do(Act.Idle, 2); }
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
        // Punished for its pranks (clicked fast while its mess is on screen): everything goes back, it says sorry and
        // behaves for ten minutes.
        if (_clickTimes.Count >= 6 && _pranks.Count > 0)
        {
            ClearPranks();
            _clickTimes.Clear();
            _prankPauseUntil = now + 600;
            SetFace(PetFace.Sad, 3);
            Say(PetMoment.Punished);
            return;
        }
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
        var prank = Item(th ? "โหมดแกล้ง (ขี้ทิ้ง · แทะหน้าจอ — จิ้มรัว ๆ เพื่อทำโทษ)" : "Prank mode (poops · nibbles the screen — click fast to punish)", () =>
        {
            bool on = !_svc.Pranks;
            _svc.SetPranks(on);
            if (on) { _poopAt = Now + 25; _biteAt = Now + 40; _prankPauseUntil = 0; }
            else ClearPranks();
        });
        prank.IsCheckable = true; prank.IsChecked = _svc.Pranks;
        menu.Items.Add(new Separator());
        Item(th ? "เก็บเข้าบ้าน (ซ่อน)" : "Put away (hide)", _svc.PutAway);
        menu.IsOpen = true;
    }
}
