using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ThemeTest;

internal static partial class Program
{
    static bool RailOnly;

    /// <summary>
    /// The activity rail in a short window (2026-11-12, owner: "ทำให้ผู้ใช้รู้ว่า สามารถ สกรอร์เลื่อนมาได้อีก … hover move"):
    /// the edge with more icons fades and shows a chevron, resting on the chevron glides the list, a click moves one icon,
    /// and a window tall enough for every icon shows neither. Every theme; renders\rail\&lt;theme&gt;-top/-end.png.
    ///
    ///   dotnet run -- --rail --out D:\...\themes
    /// </summary>
    static async Task RailShots(object themes)
    {
        Log("\n## Activity rail — more icons than fit: fade, chevrons, hover glide, every theme");
        string dir = Path.Combine(OutRoot, "renders", "rail");
        Directory.CreateDirectory(dir);
        var root = (FrameworkElement)RootVisual(MainWin);
        var scroll = Find<ScrollViewer>(root).FirstOrDefault(s => s.Name == "RailScroll");
        var up = Find<Border>(root).FirstOrDefault(b => b.Name == "RailMoreUp");
        var down = Find<Border>(root).FirstOrDefault(b => b.Name == "RailMoreDown");
        if (scroll == null || up == null || down == null) { Check("rail: the scroll and both chevrons are in the window", false, $"{scroll != null} {up != null} {down != null}"); return; }

        try
        {
            MainWin.Height = 560;
            await Idle(300);
            scroll.ScrollToTop();
            await Idle(200);
            Check("rail: a short window scrolls the icons", scroll.ScrollableHeight > 40, $"scrollable {scroll.ScrollableHeight:0} px");
            Check("rail: at the top — the down chevron and a fade below, none above",
                down.IsVisible && !up.IsVisible && scroll.OpacityMask != null, $"down {down.IsVisible} up {up.IsVisible} mask {scroll.OpacityMask != null}");

            down.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            await Idle(450);
            double glided = scroll.VerticalOffset;
            down.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            await Idle(200);
            double after = scroll.VerticalOffset;
            await Idle(200);
            Check("rail: resting on the down chevron glides the list, leaving it stops", glided > 20 && Math.Abs(scroll.VerticalOffset - after) < 0.5,
                $"glided to {glided:0}, then {after:0} → {scroll.VerticalOffset:0}");
            Check("rail: part way — both chevrons", up.IsVisible && (down.IsVisible || scroll.VerticalOffset >= scroll.ScrollableHeight - 0.5), "");

            scroll.ScrollToTop();
            await Idle(100);
            down.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            await Idle(150);
            Check("rail: a click on the chevron moves one icon", Math.Abs(scroll.VerticalOffset - Math.Min(48, scroll.ScrollableHeight)) < 1, $"{scroll.VerticalOffset:0}");

            var ids = themes == null ? new List<string> { "current" }
                : ((IEnumerable)Prop(themes, "Themes")).Cast<object>().Select(t => (string)Prop(t, "Id")).ToList();
            foreach (string id in ids)
            {
                if (themes != null) { Invoke(themes, "Apply", id, true); await Idle(200); }
                scroll.ScrollToTop();
                await Idle(150);
                SavePng(await Stable(() => ((FrameworkElement)RootVisual(MainWin), W, 560), $"rail/{id}-top"), Path.Combine(dir, id.ToLowerInvariant() + "-top.png"));
                scroll.ScrollToEnd();
                await Idle(150);
                Check($"rail {id}: at the end — the up chevron, no down", up.IsVisible && !down.IsVisible, "");
                SavePng(await Stable(() => ((FrameworkElement)RootVisual(MainWin), W, 560), $"rail/{id}-end"), Path.Combine(dir, id.ToLowerInvariant() + "-end.png"));
            }
        }
        finally
        {
            MainWin.Height = H;
            await Idle(300);
        }
        scroll.ScrollToTop();
        await Idle(150);
        bool fits = scroll.ScrollableHeight < 0.5;
        Check("rail: a window tall enough for every icon shows no chevron and no fade",
            !fits || (!up.IsVisible && !down.IsVisible && scroll.OpacityMask == null), $"scrollable {scroll.ScrollableHeight:0}");
        Check("rail: pure edge rule", CluadeX.MainWindow.RailMore(0, 100) == (false, true) && CluadeX.MainWindow.RailMore(50, 100) == (true, true)
            && CluadeX.MainWindow.RailMore(100, 100) == (true, false) && CluadeX.MainWindow.RailMore(0, 0) == (false, false)
            && CluadeX.MainWindow.RailMask(false, false, 400) == null && CluadeX.MainWindow.RailMask(true, true, 40) == null, "");
    }
}
