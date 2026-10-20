using System.Windows;

namespace CluadeX.Services.Pet;

/// <summary>
/// Where the user is working right now — the pet keeps out of it (owner 2026-10-20: "สัตว์เลี้ยงหลบช่องที่เรากำลังทำงาน
/// ได้เองแบบน่ารัก เช่นมันกำลังเดินบังช่องที่เรากำลังพิมพ์ ก็หลบให้"). CluadeX's own input areas register here (the chat
/// composer, docked or on its page); the text caret of whatever app is in front comes from PetDesktop.CaretRect.
/// UI thread only (the pet's timer runs on it).
/// </summary>
public static class PetZones
{
    private static readonly List<WeakReference<FrameworkElement>> Areas = new();

    /// <summary>A CluadeX input area the pet must not stand on (while it is on screen).</summary>
    public static void Register(FrameworkElement area)
    {
        Areas.RemoveAll(w => !w.TryGetTarget(out var e) || ReferenceEquals(e, area));
        Areas.Add(new WeakReference<FrameworkElement>(area));
    }

    /// <summary>The registered areas that are on screen now, in screen pixels.</summary>
    public static List<PetRect> CluadeXAreas()
    {
        var list = new List<PetRect>();
        Areas.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in Areas)
        {
            if (!w.TryGetTarget(out var e) || !e.IsVisible || e.ActualWidth < 1 || PresentationSource.FromVisual(e) == null) continue;
            try
            {
                var a = e.PointToScreen(new Point(0, 0));
                var b = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
                list.Add(new PetRect((int)Math.Min(a.X, b.X), (int)Math.Min(a.Y, b.Y), (int)Math.Max(a.X, b.X), (int)Math.Max(a.Y, b.Y)));
            }
            catch (InvalidOperationException) { /* not connected to a window at the moment */ }
        }
        return list;
    }
}
