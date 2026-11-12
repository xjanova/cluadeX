using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CluadeX;

/// <summary>
/// The activity rail when its icons do not all fit (a short window): the list scrolls, but its scrollbar is hidden, so
/// nothing said there was more (owner 2026-11-12: "ทำให้ผู้ใช้รู้ว่า สามารถ สกรอร์เลื่อนมาได้อีก"). Now the edge that has
/// more fades out and shows a chevron; resting the pointer on the chevron glides the list ("hover move"), a click moves
/// it one icon, and each chevron goes when its end is reached. The fade is an opacity mask, so it reads in every theme.
/// </summary>
public partial class MainWindow
{
    /// <summary>How far the edge fades (px) and how fast a hover glides (px per second).</summary>
    public const double RailFadePx = 26, RailGlidePxPerSecond = 260;
    /// <summary>One icon and its margin: what a click on a chevron moves.</summary>
    public const double RailStepPx = 48;

    private DispatcherTimer? _railGlide;
    private int _railGlideDir;
    private DateTime _railGlideLast;

    /// <summary>Which edges have more past them, for a scroll state. Pure (the harness checks it).</summary>
    public static (bool Up, bool Down) RailMore(double offset, double scrollable)
        => (offset > 0.5, scrollable - offset > 0.5);

    private void RailScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateRailHints();

    /// <summary>Show the chevrons and the fade for the edges that have more; hook the chevrons once.</summary>
    private void UpdateRailHints()
    {
        if (RailScroll == null) return;
        HookRailHint(RailMoreUp, -1);
        HookRailHint(RailMoreDown, +1);
        var (up, down) = RailMore(RailScroll.VerticalOffset, RailScroll.ScrollableHeight);
        RailMoreUp.Visibility = up ? Visibility.Visible : Visibility.Collapsed;
        RailMoreDown.Visibility = down ? Visibility.Visible : Visibility.Collapsed;
        if ((_railGlideDir < 0 && !up) || (_railGlideDir > 0 && !down)) StopRailGlide();
        RailScroll.OpacityMask = RailMask(up, down, RailScroll.ActualHeight);
    }

    /// <summary>Opaque in the middle, fading to clear on an edge that has more. Null = nothing to fade.</summary>
    public static Brush? RailMask(bool up, bool down, double height)
    {
        if ((!up && !down) || height <= RailFadePx * 2) return null;
        double f = RailFadePx / height;
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        mask.GradientStops.Add(new GradientStop(up ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, f));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - f));
        mask.GradientStops.Add(new GradientStop(down ? Colors.Transparent : Colors.Black, 1));
        mask.Freeze();
        return mask;
    }

    private void HookRailHint(Border hint, int dir)
    {
        if (hint.Tag is int) return;   // hooked already (the XAML Tag "up"/"down" is replaced by the direction)
        hint.Tag = dir;
        hint.MouseEnter += (_, _) => StartRailGlide(dir);
        hint.MouseLeave += (_, _) => StopRailGlide();
        hint.MouseLeftButtonUp += (_, e) =>
        {
            RailScroll.ScrollToVerticalOffset(Math.Clamp(RailScroll.VerticalOffset + dir * RailStepPx, 0, RailScroll.ScrollableHeight));
            e.Handled = true;
        };
    }

    private void StartRailGlide(int dir)
    {
        _railGlideDir = dir;
        _railGlideLast = DateTime.UtcNow;
        if (_railGlide == null)
        {
            _railGlide = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _railGlide.Tick += (_, _) =>
            {
                var now = DateTime.UtcNow;
                double step = (now - _railGlideLast).TotalSeconds * RailGlidePxPerSecond;
                _railGlideLast = now;
                double to = Math.Clamp(RailScroll.VerticalOffset + _railGlideDir * step, 0, RailScroll.ScrollableHeight);
                RailScroll.ScrollToVerticalOffset(to);
                if (to <= 0 || to >= RailScroll.ScrollableHeight) StopRailGlide();
            };
        }
        _railGlide.Start();
    }

    private void StopRailGlide()
    {
        _railGlideDir = 0;
        _railGlide?.Stop();
    }
}
