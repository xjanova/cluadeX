using System.Windows;
using System.Windows.Controls;
using CluadeX.Views;

namespace MarqueeTest;

/// <summary>
/// The gliding title / status bar (MarqueeScroller) on a narrow window, through the REAL CluadeX.dll: at the end of its
/// glide the row's last item must be inside the bar — also after an item grows or appears AFTER the first layout (a
/// longer stats text, the spend chip). 2026-10-17: the row was measured at a finite width, so later growth never
/// reached the scroller; the glide stopped 54 px short on the owner's 1094 px window until the window was resized.
/// No settings, no network. Run: build CluadeX, then `dotnet run -p:CluadeXBin=&lt;OutDir&gt;\`.
/// </summary>
internal static class Program
{
    static int Pass, Fail;
    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS {name}"); return; }
        Fail++; Console.WriteLine($"  FAIL {name}");
        if (detail.Length > 0) Console.WriteLine("       " + detail);
    }

    [STAThread]
    static int Main()
    {
        var app = new Application();
        // The status bar's shape: five Auto columns, a star spacer, the right-hand stats.
        var g = new Grid();
        foreach (var w in new[] { "a", "a", "a", "a", "a", "*", "a" })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w == "*" ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        TextBlock T(string s, int col, double maxW = double.PositiveInfinity)
        {
            var t = new TextBlock { Text = s, FontSize = 12, Margin = new Thickness(18, 0, 0, 0), MaxWidth = maxW, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(t, col); g.Children.Add(t); return t;
        }
        T("พร้อมใช้งาน status text", 0, 360);
        T("model name here", 2, 200);
        T("29,702 / 200,000 tokens (15%)", 3);
        T("brain v2.9.511 · 128 tools", 4);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
        Grid.SetColumn(right, 6); g.Children.Add(right);
        var last = new TextBlock { Text = "4 โหลด · 236.5 วิ · 0.0 tok/s", FontSize = 12 };
        right.Children.Add(last);
        var bar = new MarqueeScroller { Content = g, Height = 33 };
        var win = new Window
        {
            Width = 600, Height = 120, Left = -3000, Top = -3000, ShowActivated = false, WindowStyle = WindowStyle.None,
            Content = new Border { Child = bar, VerticalAlignment = VerticalAlignment.Bottom },
        };
        win.Show();

        // At the far end of the glide, the last item's right edge is inside the viewport.
        void EndVisible(string id, string label)
        {
            win.UpdateLayout();
            bar.ScrollToHorizontalOffset(bar.ScrollableWidth);
            win.UpdateLayout();
            var r = last.TransformToAncestor(bar).TransformBounds(new Rect(last.RenderSize));
            Check($"{id} {label}: the last item is reachable", r.Right <= bar.ViewportWidth + 0.5,
                $"viewport {bar.ViewportWidth:0}, extent {bar.ExtentWidth:0}, row {g.ActualWidth:0}, last item ends at {r.Right:0}");
            Check($"{id} {label}: the extent is the row's width", Math.Abs(bar.ExtentWidth - g.ActualWidth) <= 0.5,
                $"extent {bar.ExtentWidth:0} vs row {g.ActualWidth:0}");
            bar.ScrollToHorizontalOffset(0);
            win.UpdateLayout();
        }

        EndVisible("M1", "first layout, overflowing");
        last.Text = "4 โหลด · 236.5 วิ · 0.0 tok/s · and a much longer tail of stats";
        EndVisible("M2", "after the last text grew");
        right.Children.Insert(0, new TextBlock { Text = "$0.42", FontSize = 12, Margin = new Thickness(0, 0, 14, 0) });
        EndVisible("M3", "after a chip appeared");
        win.Width = 500;
        EndVisible("M4", "after the window narrowed");

        // Everything fits again: the row is an ordinary bar, nothing to glide, the star spacer takes the slack.
        right.Children.RemoveAt(0);
        last.Text = "4";
        win.Width = 1400;
        win.UpdateLayout();
        Check("M5 fits again: nothing to scroll", bar.ScrollableWidth <= 0.5, $"scrollable {bar.ScrollableWidth:0}");
        Check("M5 fits again: the row fills the bar (the star column stretches)", Math.Abs(g.ActualWidth - bar.ViewportWidth) <= 0.5,
            $"row {g.ActualWidth:0} vs viewport {bar.ViewportWidth:0}");
        win.Close();
        Console.WriteLine($"\n{Pass} passed, {Fail} failed");
        return Fail == 0 ? 0 : 1;
    }
}
