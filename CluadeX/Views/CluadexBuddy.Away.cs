using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CluadeX.Services;

namespace CluadeX.Views;

/// <summary>
/// The owner is away from the computer (owner 2026-11-08: "มีเหตุการณ์ที่แบบเราไม่อยู่นานๆ ก็เรียกได้ ฮัลโหล มีใครอยู่มั้ย
/// ภาษาอังกฤษ แทนการหลับบ้าง"): once nobody has touched the mouse or keyboard for <see cref="AwayAfter"/> while she is idle
/// or asleep, she calls out now and then — "Hello? Is anyone there?" — waking up first if she was asleep, and dozing off
/// again after. At most <see cref="AwayCallsMax"/> calls per absence, <see cref="AwayCallEvery"/> or more apart; any input
/// starts the count again. Only with sound on, and only where she can be seen (not minimised, not hidden).
/// <para>While a call plays she keeps moving even when another app has the focus (<see cref="_forcePlay"/>); otherwise an idle
/// CluadeX in the background asks for no frames, as before.</para>
/// </summary>
public partial class CluadexBuddy
{
    public static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan AwayCallEvery = TimeSpan.FromMinutes(8);
    public const int AwayCallsMax = 3;

    private DispatcherTimer? _awayTimer;
    private DateTime _nextAwayAt = DateTime.MinValue;
    private int _awayCalls;

    /// <summary>An away call is playing: she moves and is heard although CluadeX is in the background.</summary>
    private bool _forcePlay;

    /// <summary>How many times she has called out in this absence (tests).</summary>
    public int AwayCalls => _awayCalls;

    private void StartAway()
    {
        SoundsChanged -= OnSoundsChanged;
        SoundsChanged += OnSoundsChanged;
        if (_awayTimer != null) return;
        _awayTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _awayTimer.Tick += (_, _) => AwayTick(UserIdle());
        _awayTimer.Start();
    }

    private void StopAway()
    {
        SoundsChanged -= OnSoundsChanged;
        _awayTimer?.Stop();
        _awayTimer = null;
        _forcePlay = false;
    }

    /// <summary>Sound turned off: the clips playing go quiet at once; turned on: they follow the same rule as a new one.</summary>
    private void OnSoundsChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(OnSoundsChanged); return; }
        ResyncSound();
    }

    /// <summary>One check of the timer, with how long the owner has been idle (public for tests).</summary>
    public void AwayTick(TimeSpan idle)
    {
        if (idle < TimeSpan.FromMinutes(1))
        {
            // they are back: she called and nobody came, so she sulks a little first; the next absence starts the count again
            if (_awayCalls > 0) CallOut(CanTalk ? SulkLines : SulkClips);
            _awayCalls = 0;
            _nextAwayAt = DateTime.MinValue;
            return;
        }
        if (idle < AwayAfter || _awayCalls >= AwayCallsMax || DateTime.UtcNow < _nextAwayAt) return;
        if (!CallAway()) return;
        _awayCalls++;
        _nextAwayAt = DateTime.UtcNow + AwayCallEvery + TimeSpan.FromSeconds(Dice.Next(0, 240));
    }

    /// <summary>She calls out for the owner (waking up first when asleep, then back to sleep). False when she cannot now.</summary>
    public bool CallAway() => CallOut(AwayLines);

    /// <summary>One of <paramref name="lines"/> for the owner, wherever they are looking: waking up first when asleep, then
    /// back to her scene. False when she cannot now (sound off, hidden, busy, no clips).</summary>
    private bool CallOut(string[] lines)
    {
        if (!IsLoaded || !ClipsOn || !SoundsOn || !IsVisible || _window is { WindowState: WindowState.Minimized }) return false;
        if (Shown is not (BuddyMood.Idle or BuddyMood.Sleep) || ClipShown == "" || lines.Contains(ClipShown)) return false;
        string? line = Pick(lines, _lastLine);
        if (line == null) return false;
        _lastLine = line;
        if (Spoken.Contains(line)) _lastSpokeAt = DateTime.UtcNow;
        bool asleep = Shown == BuddyMood.Sleep;
        var chain = new List<string>();
        if (asleep) chain.Add(WakeClip);
        chain.Add(line);
        // asleep: she yawns and dozes off again; the yawn is part of the call so she is seen asleep, not paused mid-yawn
        if (asleep && Pick(Scenes[BuddyMood.Sleep].Intro, null) is { } yawn) chain.Add(yawn);
        _forcePlay = true;
        SyncPause();
        string back = asleep ? BuddyMood.Sleep : _listening ? ListeningScene : BuddyMood.Idle;
        bool ok = PlayChain(chain, 0, () =>
        {
            _forcePlay = false;
            ResyncSound();   // the call is over: what plays next in the background is quiet again
            bool going = StartScene(back, skipIntro: asleep);
            SyncPause();
            return going;
        });
        if (!ok) { _forcePlay = false; SyncPause(); }
        return ok;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>How long since the last mouse or keyboard input anywhere on the computer (zero when Windows cannot say).</summary>
    private static TimeSpan UserIdle()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        uint ms = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(ms);
    }
}
