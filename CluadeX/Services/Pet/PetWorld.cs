namespace CluadeX.Services.Pet;

/// <summary>A rectangle in screen pixels (the same coordinates GetWindowRect gives this process).</summary>
public readonly record struct PetRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

public enum PetFloorKind { Ground, Taskbar, Window }

/// <summary>Something the pet can stand on: a horizontal edge at <see cref="Y"/> from X1 to X2. A window's floor
/// remembers the window (and where its left edge was) so the pet rides along when the window moves.</summary>
public readonly record struct PetFloor(int Y, int X1, int X2, PetFloorKind Kind, nint Owner = 0, int OwnerLeft = 0)
{
    public bool Under(double x) => x >= X1 && x <= X2;
}

/// <summary>A visible top-level window, listed top of the Z-order first.</summary>
public readonly record struct PetWindow(nint Handle, PetRect Rect);

/// <summary>A monitor and its work area (the part the taskbar does not cover).</summary>
public readonly record struct PetMonitor(PetRect Bounds, PetRect Work);

/// <summary>The pet's body: <see cref="X"/>/<see cref="Y"/> are its feet (centre bottom), in screen pixels.</summary>
public sealed class PetBody
{
    public double X, Y, Vx, Vy;
    public bool Grounded;
    public PetFloor? Floor;
}

/// <summary>Floors and physics. Pure — the harness feeds it rectangles; PetDesktop feeds it the real screen.</summary>
public static class PetWorld
{
    public const double Gravity = 2600;     // px/s²
    public const double MaxFall = 2400;     // px/s
    public const int WindowMinWidth = 140;

    /// <summary>Every edge the pet can stand on: the bottom of each work area (the top of the taskbar when it is at
    /// the bottom), and the top edge of each window wherever no window above it covers that edge. A window top too
    /// close to the top of its screen (a maximized window) is no floor: the pet would stand off-screen.</summary>
    public static List<PetFloor> Floors(IReadOnlyList<PetMonitor> monitors, IReadOnlyList<PetWindow> windowsTopFirst, int petHeight)
    {
        var floors = new List<PetFloor>();
        foreach (var m in monitors)
            floors.Add(new PetFloor(m.Work.Bottom, m.Work.Left, m.Work.Right - 1,
                m.Work.Bottom < m.Bounds.Bottom ? PetFloorKind.Taskbar : PetFloorKind.Ground));

        for (int i = 0; i < windowsTopFirst.Count; i++)
        {
            var w = windowsTopFirst[i].Rect;
            if (w.Width < WindowMinWidth) continue;
            var mon = monitors.FirstOrDefault(m => m.Bounds.Contains(Math.Clamp((w.Left + w.Right) / 2, m.Bounds.Left, m.Bounds.Right - 1), w.Top)
                                                   && w.Top >= m.Bounds.Top && w.Top < m.Bounds.Bottom);
            if (mon.Bounds.Width == 0) continue;                                  // its top edge is on no screen
            if (w.Top < mon.Work.Top + petHeight || w.Top >= mon.Work.Bottom) continue;
            var spans = new List<(int a, int b)> { (Math.Max(w.Left, mon.Work.Left), Math.Min(w.Right, mon.Work.Right) - 1) };
            // Covered by a window above it (the pixel row just above the edge is hidden): not standable there.
            for (int j = 0; j < i && spans.Count > 0; j++)
            {
                var above = windowsTopFirst[j].Rect;
                if (!(above.Top <= w.Top - 1 && above.Bottom > w.Top - 1)) continue;
                spans = Subtract(spans, above.Left, above.Right - 1);
            }
            foreach (var (a, b) in spans)
                if (b - a >= 24) floors.Add(new PetFloor(w.Top, a, b, PetFloorKind.Window, windowsTopFirst[i].Handle, w.Left));
        }
        return floors;
    }

    static List<(int a, int b)> Subtract(List<(int a, int b)> spans, int cutA, int cutB)
    {
        var result = new List<(int a, int b)>();
        foreach (var (a, b) in spans)
        {
            if (cutB < a || cutA > b) { result.Add((a, b)); continue; }
            if (cutA > a) result.Add((a, cutA - 1));
            if (cutB < b) result.Add((cutB + 1, b));
        }
        return result;
    }

    /// <summary>After a rescan: a pet standing on a window that moved goes with it; one whose floor is gone falls.</summary>
    public static void Follow(PetBody body, IReadOnlyList<PetFloor> floors)
    {
        if (!body.Grounded || body.Floor is not { } old) return;
        if (old.Owner != 0)
        {
            int dx = 0;
            var same = floors.Where(f => f.Owner == old.Owner).ToList();
            if (same.Count > 0) dx = same[0].OwnerLeft - old.OwnerLeft;
            double nx = body.X + dx;
            var now = same.FirstOrDefault(f => f.Under(nx));
            if (now.Owner != 0)
            {
                body.X = nx; body.Y = now.Y; body.Floor = now;
                return;
            }
        }
        else
        {
            var now = floors.FirstOrDefault(f => f.Owner == 0 && f.Under(body.X) && Math.Abs(f.Y - body.Y) <= 2);
            if (now.X2 > now.X1) { body.Y = now.Y; body.Floor = now; return; }
        }
        body.Grounded = false;   // the floor went away (window closed, minimized, covered): fall
        body.Floor = null;
    }

    /// <summary>One physics step. Returns true when the pet landed in this step.</summary>
    public static bool Step(PetBody b, IReadOnlyList<PetFloor> floors, double dt, PetRect screen)
    {
        bool landed = false;
        if (b.Grounded)
        {
            b.X += b.Vx * dt;
            var f = b.Floor;
            if (f is not { } floor || !floor.Under(b.X))
            {
                // Walked off the edge.
                b.Grounded = false;
                b.Floor = null;
            }
        }
        if (!b.Grounded)
        {
            double prevY = b.Y;
            b.Vy = Math.Min(b.Vy + Gravity * dt, MaxFall);
            b.X += b.Vx * dt;
            b.Y += b.Vy * dt;
            if (b.Vy >= 0)
            {
                PetFloor? land = null;
                foreach (var f in floors)
                    if (f.Under(b.X) && f.Y >= prevY - 0.5 && f.Y <= b.Y && (land == null || f.Y < land.Value.Y)) land = f;
                // Fell past everything (off every work area): catch it on the lowest ground under it, or the screen.
                if (land == null && b.Y > screen.Bottom)
                {
                    var ground = floors.Where(f => f.Owner == 0 && f.Under(b.X)).OrderByDescending(f => f.Y).FirstOrDefault();
                    land = ground.X2 > ground.X1 ? ground : new PetFloor(screen.Bottom, screen.Left, screen.Right, PetFloorKind.Ground);
                }
                if (land is { } l)
                {
                    b.Y = l.Y; b.Vy = 0; b.Vx = 0; b.Grounded = true; b.Floor = l;
                    landed = true;
                }
            }
        }
        b.X = Math.Clamp(b.X, screen.Left + 8, screen.Right - 8);
        return landed;
    }

    /// <summary>The pet's box (feet at x, y; <paramref name="halfW"/> each side, <paramref name="h"/> tall) comes within
    /// <paramref name="margin"/> of <paramref name="zone"/>.</summary>
    public static bool Overlaps(PetRect zone, double x, double y, double halfW, double h, int margin) =>
        x + halfW > zone.Left - margin && x - halfW < zone.Right + margin && y > zone.Top - margin && y - h < zone.Bottom + margin;

    /// <summary>
    /// Where to step to on <paramref name="floor"/> so the pet no longer covers any work zone (the box being typed in):
    /// null = it covers none; NaN = no spot on this floor is clear (jump elsewhere); else the clear x nearest to it.
    /// Pure, so the harness checks it.
    /// </summary>
    public static double? DodgeX(PetFloor floor, IReadOnlyList<PetRect> zones, double x, double y, double halfW, double h, int margin)
    {
        var hit = zones.Where(z => Overlaps(z, x, y, halfW, h, margin)).ToList();
        if (hit.Count == 0) return null;
        double lo = floor.X1 + halfW, hi = floor.X2 - halfW;
        if (hi < lo) return double.NaN;
        var spots = new List<double> { lo, hi };
        foreach (var z in zones)
        {
            spots.Add(z.Left - margin - halfW - 1);
            spots.Add(z.Right + margin + halfW + 1);
        }
        double best = double.NaN;
        foreach (var s in spots)
        {
            if (s < lo || s > hi) continue;
            if (zones.Any(z => Overlaps(z, s, y, halfW, h, margin))) continue;
            if (double.IsNaN(best) || Math.Abs(s - x) < Math.Abs(best - x)) best = s;
        }
        return best;
    }

    /// <summary>The side edges of the work areas the pet can climb: (x of the edge, top, bottom, side −1 = left wall /
    /// +1 = right wall). Pure.</summary>
    public static List<(int X, int Top, int Bottom, int Side)> Walls(IReadOnlyList<PetMonitor> monitors)
    {
        var walls = new List<(int, int, int, int)>();
        foreach (var m in monitors)
        {
            // An edge shared with a neighbouring monitor is not a wall (the pet would climb in mid-air).
            bool LeftOpen() => monitors.Any(o => !o.Equals(m) && o.Work.Right == m.Work.Left && o.Work.Top < m.Work.Bottom && o.Work.Bottom > m.Work.Top);
            bool RightOpen() => monitors.Any(o => !o.Equals(m) && o.Work.Left == m.Work.Right && o.Work.Top < m.Work.Bottom && o.Work.Bottom > m.Work.Top);
            if (!LeftOpen()) walls.Add((m.Work.Left, m.Work.Top, m.Work.Bottom, -1));
            if (!RightOpen()) walls.Add((m.Work.Right, m.Work.Top, m.Work.Bottom, 1));
        }
        return walls;
    }

    /// <summary>The launch velocity for a jump from the body's feet to (x, y): up high enough to clear the target
    /// edge by a little, across in the time that takes.</summary>
    public static (double vx, double vy) JumpTo(PetBody b, double x, double y)
    {
        double rise = Math.Max(30, b.Y - y + 40);
        double vy = -Math.Sqrt(2 * Gravity * rise);
        double tUp = -vy / Gravity, tDown = Math.Sqrt(2 * Math.Max(1, y - (b.Y - rise)) / Gravity);
        double vx = (x - b.X) / (tUp + tDown);
        return (vx, vy);
    }
}
