using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using CluadeX.Services;
using CluadeX.Services.Problems;

namespace CluadeX.Views.Editor;

/// <summary>
/// The change marks' motion (owner 2026-11-09: "ทำให้แจ่ม มีอนิเมชั่น สวยงาม ต่างกับของคนอื่น แต่ ไม่กินเครื่อง"):
///   • a mark that is new on screen draws itself in — the gutter pill grows from its middle, the AI's line tint sweeps in
///     from the gutter with a bright leading edge, a deletion's dashed rule runs across; on opening a file they cascade
///     top to bottom (45 ms apart);
///   • the mark you jump to (next / previous, a click, the peek) flashes once.
/// Cheap by construction: nothing runs between animations — CompositionTarget.Rendering is hooked only while one plays
/// (≤ ~0.8 s) and unhooks itself; the state is a function of the clock, so a missed frame never leaves a mark half-drawn;
/// only the lines on screen are painted (the renderer / margin already clip to them); off when Windows says "no
/// animations" (SystemParameters.ClientAreaAnimation), on software rendering, or when the editor is not on screen.
/// </summary>
public sealed class ChangeMarkFx
{
    public const double AppearMs = 420, StaggerMs = 45, PulseMs = 760;
    private const int MaxStagger = 10;

    /// <summary>The harness's switch (off = every mark drawn complete at once).</summary>
    public static bool Allowed { get; set; } = true;

    public static bool MotionOn => Allowed && SystemParameters.ClientAreaAnimation && (RenderCapability.Tier >> 16) > 0;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Action _redraw;
    private readonly Func<bool> _onScreen;
    private readonly Dictionary<AiChangeHunk, string> _keyOf = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, double> _startOf = new(StringComparer.Ordinal);
    private HashSet<string> _known = new(StringComparer.Ordinal);
    private string? _pulseKey;
    private double _pulseAt = double.NegativeInfinity;
    private bool _hooked;

    public ChangeMarkFx(Action redraw, Func<bool> onScreen)
    {
        _redraw = redraw;
        _onScreen = onScreen;
    }

    private double Now => _clock.Elapsed.TotalMilliseconds;

    /// <summary>Hooked to the render loop right now (the harness checks it lets go).</summary>
    public bool IsHooked => _hooked;

    public bool IsAnimating
    {
        get
        {
            double now = Now;
            return now < _pulseAt + PulseMs || _startOf.Values.Any(s => now < s + AppearMs);
        }
    }

    /// <summary>
    /// The marks now, each with a key that stays the same while the change is the same change (owner, kind and the line
    /// above it — not its line number, so typing above it does not make every mark below draw itself in again).
    /// <paramref name="fresh"/>: another file — every mark is new (the cascade).
    /// </summary>
    public void Track(IReadOnlyList<(AiChangeHunk Hunk, string Key)> marks, bool fresh)
    {
        if (fresh) { _known.Clear(); _startOf.Clear(); _pulseKey = null; }
        bool motion = MotionOn && _onScreen();
        double now = Now;
        int n = 0;
        _keyOf.Clear();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (hunk, key) in marks)
        {
            _keyOf[hunk] = key;
            known.Add(key);
            if (motion && !_known.Contains(key) && !_startOf.ContainsKey(key))
                _startOf[key] = now + StaggerMs * Math.Min(n++, MaxStagger);
        }
        _known = known;
        foreach (var gone in _startOf.Keys.Where(k => !known.Contains(k)).ToList()) _startOf.Remove(gone);
        Kick();
    }

    /// <summary>The mark you landed on flashes once.</summary>
    public void Pulse(AiChangeHunk hunk)
    {
        if (!MotionOn || !_onScreen() || !_keyOf.TryGetValue(hunk, out var key)) return;
        _pulseKey = key;
        _pulseAt = Now;
        Kick();
    }

    /// <summary>0 → 1, eased (cubic out); 1 when the mark is not animating.</summary>
    public double Appear(AiChangeHunk hunk)
    {
        if (_startOf.Count == 0 || !_keyOf.TryGetValue(hunk, out var key) || !_startOf.TryGetValue(key, out var start)) return 1;
        double t = (Now - start) / AppearMs;
        if (t >= 1) return 1;
        if (t <= 0) return 0;
        double u = 1 - t;
        return 1 - u * u * u;
    }

    /// <summary>1 → 0 over the flash (quadratic fall-off); 0 for every other mark.</summary>
    public double Flash(AiChangeHunk hunk)
    {
        if (_pulseKey == null || !_keyOf.TryGetValue(hunk, out var key) || key != _pulseKey) return 0;
        double t = (Now - _pulseAt) / PulseMs;
        if (t < 0 || t >= 1) return 0;
        double u = 1 - t;
        return u * u;
    }

    /// <summary>Snap every mark to its end (the editor went away, or the user turned motion off).</summary>
    public void Finish()
    {
        _startOf.Clear();
        _pulseKey = null;
        _pulseAt = double.NegativeInfinity;
        Unhook();
    }

    private void Kick()
    {
        if (!IsAnimating) { _redraw(); return; }
        if (_hooked) return;
        CompositionTarget.Rendering += OnFrame;
        _hooked = true;
    }

    private void Unhook()
    {
        if (!_hooked) return;
        CompositionTarget.Rendering -= OnFrame;
        _hooked = false;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!_onScreen()) { Finish(); _redraw(); return; }
        _redraw();
        if (IsAnimating) return;
        // Done: drop the finished starts so the next draw takes the quick path, and let go of the render loop.
        double now = Now;
        foreach (var k in _startOf.Where(p => now >= p.Value + AppearMs).Select(p => p.Key).ToList()) _startOf.Remove(k);
        if (now >= _pulseAt + PulseMs) _pulseKey = null;
        Unhook();
        _redraw();
    }
}

/// <summary>
/// The marks' paint, built once per colour and frozen (a theme switch calls <see cref="Reset"/>): the gutter pill's
/// vertical sheen, the line tint that is strongest by the gutter and fades across the code, the sweep's leading edge.
/// </summary>
public static class ChangeMarkPaint
{
    private static readonly Dictionary<(Color, int), Brush> s_cache = new();

    public static void Reset()
    {
        lock (s_cache) s_cache.Clear();
    }

    public static Color ColorFor(AiChangeKind kind, bool byUser) => byUser
        ? ThemeService.ColorOf("Editor.Change.User", Colors.DarkOrange)
        : kind switch
        {
            AiChangeKind.Added => ThemeService.ColorOf("Editor.Change.Added", Colors.SeaGreen),
            AiChangeKind.Modified => ThemeService.ColorOf("Editor.Change.Modified", Colors.SteelBlue),
            _ => ThemeService.ColorOf("Editor.Change.Removed", Colors.IndianRed),
        };

    public static Color TintColorFor(AiChangeHunk h) => h.ByUser
        ? ThemeService.ColorOf("Editor.Change.User.Tint", Colors.Transparent)
        : h.Kind == AiChangeKind.Added
            ? ThemeService.ColorOf("Editor.Change.Added.Tint", Colors.Transparent)
            : ThemeService.ColorOf("Editor.Change.Modified.Tint", Colors.Transparent);

    /// <summary>The gutter pill: a lighter sheen at the top that settles into the mark's colour by 40 % of its height.</summary>
    public static Brush Pill(Color c) => Get(c, 0, () => new LinearGradientBrush(new GradientStopCollection
    {
        new(Lighten(c, 0.35), 0), new(c, 0.4), new(c, 1),
    }, new Point(0, 0), new Point(0, 1)));

    /// <summary>The line tint: 1.7× by the gutter, the token's own strength a third of the way, 0.6× at the far edge.</summary>
    public static Brush Tint(Color c) => Get(c, 1, () => new LinearGradientBrush(new GradientStopCollection
    {
        new(Alpha(c, 1.7), 0), new(c, 0.35), new(Alpha(c, 0.6), 1),
    }, new Point(0, 0.5), new Point(1, 0.5)));

    /// <summary>The sweep's leading edge: clear → the mark's colour at 45 %.</summary>
    public static Brush Edge(Color c) => Get(c, 2, () => new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromArgb(0, c.R, c.G, c.B), 0), new(Color.FromArgb(115, c.R, c.G, c.B), 1),
    }, new Point(0, 0.5), new Point(1, 0.5)));

    public static Brush Solid(Color c) => Get(c, 3, () => new SolidColorBrush(c));

    private static Brush Get(Color c, int kind, Func<Brush> make)
    {
        lock (s_cache)
        {
            if (s_cache.TryGetValue((c, kind), out var b)) return b;
            b = make();
            b.Freeze();
            if (s_cache.Count > 64) s_cache.Clear();
            s_cache[(c, kind)] = b;
            return b;
        }
    }

    public static Color Alpha(Color c, double factor) => Color.FromArgb((byte)Math.Clamp(Math.Round(c.A * factor), 0, 255), c.R, c.G, c.B);

    private static Color Lighten(Color c, double by) => Color.FromArgb(c.A,
        (byte)(c.R + (255 - c.R) * by), (byte)(c.G + (255 - c.G) * by), (byte)(c.B + (255 - c.B) * by));
}
