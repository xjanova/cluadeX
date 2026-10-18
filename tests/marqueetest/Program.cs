using System.Windows;
using System.Windows.Controls;
using CluadeX.Views;

namespace MarqueeTest;

/// <summary>
/// The gliding title / status bar (MarqueeScroller) on a narrow window, through the REAL CluadeX.dll: at the end of its
/// glide the row's last item must be inside the bar — also after an item grows or appears AFTER the first layout (a
/// longer stats text, the spend chip). 2026-10-17: the row was measured at a finite width, so later growth never
/// reached the scroller; the glide stopped 54 px short on the owner's 1094 px window until the window was resized.
/// 2026-10-18, the TITLE bar (T*, H*, N*): its row has star columns (the search pill gives way) and lies in the window's
/// caption, where WPF sees the mouse only over the chips; pointing at the gliding text never brought out the end. The
/// hover now reads the real cursor; here a test pointer (PointerOverride) stands in for it and frames are stepped by hand.
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
        // Several windows one after another: closing the first must not shut the app (and its layout) down.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
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

        TitleBar();
        Nested();
        Console.WriteLine($"\n{Pass} passed, {Fail} failed");
        return Fail == 0 ? 0 : 1;
    }

    static FrameworkElement Chip(string text, double margin = 6) =>
        new Border { Margin = new Thickness(margin, 6, 0, 6), Padding = new Thickness(10, 2, 10, 2), Child = new TextBlock { Text = text, FontSize = 11.5 } };

    /// <summary>Let real frames run for a while (the bar's own CompositionTarget.Rendering glide).</summary>
    static void Pump(int ms)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>The title bar of MainWindow.xaml as it is laid out: the brand (outside the bar), then the gliding row —
    /// project, a star gap, the search pill in 6*, a star gap, the chips, the buttons, the buddy — then the caption
    /// buttons, on the owner's 1094 px window.</summary>
    static void TitleBar()
    {
        Console.WriteLine("\nTitle bar (star columns, hover)");
        var row = new Grid();
        foreach (var w in new[] { 0, 0, 1, 6, 1, 0, 0, 0, 0, 0, 0 })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? GridLength.Auto : new GridLength(w, GridUnitType.Star) });
        void Put(UIElement e, int col) { Grid.SetColumn(e, col); row.Children.Add(e); }
        var project = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var version = new TextBlock { Text = "v3.0.140 · c69a9e8 · 18 ต.ค. 13:26", FontSize = 10, Margin = new Thickness(8, 0, 0, 0) };
        project.Children.Add(version);
        project.Children.Add(new TextBlock { Text = "ClaudeClient", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 0, 0, 0), MaxWidth = 180 });
        project.Children.Add(new TextBlock { Text = @"E:\Code\ClaudeClient\.claude\worktrees\angry-booth", FontSize = 11, Margin = new Thickness(8, 0, 0, 0), MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis });
        project.Children.Add(Chip("feat/chat-split-map", 10));
        Put(project, 1);
        Put(new Button { Content = new TextBlock { Text = "Search sessions, commands, files…", TextTrimming = TextTrimming.CharacterEllipsis }, MaxWidth = 440, Margin = new Thickness(12, 8, 12, 8) }, 3);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        chips.Children.Add(Chip("วันนี้ $0.42 · 12%"));
        chips.Children.Add(Chip("DGX Spark"));
        chips.Children.Add(Chip("Pro"));
        Put(chips, 5);
        for (int c = 6; c <= 8; c++) Put(new Border { Width = 32, Height = 32, Margin = new Thickness(4, 0, 0, 0) }, c);
        var buddy = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10, 6, 12, 6), Visibility = Visibility.Collapsed };
        buddy.Children.Add(new TextBlock { Text = "นกฮูกเรืองแสงมอสส์ · เนิร์ด", FontSize = 11, FontWeight = FontWeights.SemiBold });
        buddy.Children.Add(new TextBlock { Text = "lv 7 · happy", FontSize = 9 });
        Put(buddy, 9);

        var bar = new MarqueeScroller { Content = row, PointerOverride = () => null };
        var title = new Grid { Height = 42 };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(new TextBlock { Text = "CluadeX", FontSize = 19, FontWeight = FontWeights.Black, Margin = new Thickness(50, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(bar, 1); title.Children.Add(bar);
        var caption = new Border { Width = 126 };
        Grid.SetColumn(caption, 2); title.Children.Add(caption);
        var win = new Window
        {
            Width = 1094, Height = 120, Left = -3000, Top = -3000, ShowActivated = false, WindowStyle = WindowStyle.None,
            Content = new Border { Child = title, VerticalAlignment = VerticalAlignment.Top },
        };
        win.Show();
        win.UpdateLayout();

        FrameworkElement last = buddy;
        Rect Bounds(FrameworkElement e) => e.TransformToAncestor(bar).TransformBounds(new Rect(e.RenderSize));
        void Frames(double seconds) { for (double t = 0; t < seconds; t += 1 / 60.0) { bar.Step(1 / 60.0); win.UpdateLayout(); } }
        string Where() => $"offset {bar.HorizontalOffset:0}/{bar.ScrollableWidth:0}, viewport {bar.ViewportWidth:0}, last item {Bounds(last).Left:0}..{Bounds(last).Right:0}";
        bool LastInside() => Bounds(last).Right <= bar.ViewportWidth + 0.5 && Bounds(last).Left >= -0.5;

        void Ends(string id, string label)
        {
            win.UpdateLayout();
            Check($"{id} {label}: the row overflows the bar", bar.ScrollableWidth > 12, Where());
            // Glide: no pointer, the row travels to its end by itself (a rest at the start included).
            bar.PointerOverride = () => null;
            bool atEnd = false;
            for (int i = 0; i < 60 * 120 && !atEnd; i++)
            {
                bar.Step(1 / 60.0); win.UpdateLayout();
                atEnd = bar.HorizontalOffset >= bar.ScrollableWidth - 0.5;
            }
            Check($"{id} {label}: the glide reaches the row's end", atEnd, Where());
            Check($"{id} {label}: at the glide's end the last item is inside the bar", LastInside(), Where());
            // Hover: the pointer near the right edge brings out the end, near the left edge the start, mid-bar the middle.
            bar.PointerOverride = () => new Point(bar.ActualWidth * 0.9, bar.ActualHeight / 2);
            Frames(1.5);
            Check($"{id} {label}: pointing near the bar's right edge shows the last item",
                LastInside() && bar.HorizontalOffset >= bar.ScrollableWidth - 0.5, Where());
            bar.PointerOverride = () => new Point(bar.ActualWidth * 0.1, bar.ActualHeight / 2);
            Frames(1.5);
            Check($"{id} {label}: pointing near the bar's left edge shows the start", bar.HorizontalOffset <= 0.5, Where());
            bar.PointerOverride = () => new Point(bar.ActualWidth * 0.5, bar.ActualHeight / 2);
            Frames(1.5);
            Check($"{id} {label}: pointing mid-bar shows the middle", Math.Abs(bar.HorizontalOffset - bar.ScrollableWidth / 2) <= 1, Where());
            // A still pointer holds the row: it does not glide away from under it.
            double held = bar.HorizontalOffset;
            Frames(3);
            Check($"{id} {label}: the row stays put under a still pointer", Math.Abs(bar.HorizontalOffset - held) <= 0.5, $"{held:0.0} → {bar.HorizontalOffset:0.0}");
            bar.PointerOverride = () => null;
            bar.ScrollToHorizontalOffset(0); win.UpdateLayout();
        }

        last = (FrameworkElement)row.Children[row.Children.Count - 2];   // the last button, before the buddy appears
        Ends("T1", "first layout (no buddy yet)");
        buddy.Visibility = Visibility.Visible;
        last = buddy;
        Ends("T2", "after the buddy chip appeared");
        version.Text = "v3.0.141 · 1234abc · 18 ต.ค. 23:59 · DEV build with a long tail";
        ((TextBlock)((Border)chips.Children[0]).Child).Text = "วันนี้ $12.40 / $20.00 · 62% of today's budget";
        Ends("T3", "after the version and spend chips grew");
        win.Width = 760;
        Ends("T4", "on a narrower window (760 px)");

        // The real cursor cannot be over an off-screen window: the real probe answers 'not over' and the bar glides.
        bar.PointerOverride = null;
        bar.ScrollToHorizontalOffset(0); win.UpdateLayout();
        Pump(1500);
        Check("H1 with the real cursor probe (window off-screen) the bar glides by itself", bar.HorizontalOffset > 1, Where());
        win.Close();
    }

    /// <summary>A gliding bar inside another one's row, in a fixed-width column: the inner one overflows its own width and
    /// glides by itself, and so does the outer one; both reach their ends.</summary>
    static void Nested()
    {
        Console.WriteLine("\nNested bars");
        var innerRow = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < 8; i++) innerRow.Children.Add(new TextBlock { Text = $"inner item {i} ·", FontSize = 12, Margin = new Thickness(8, 0, 0, 0) });
        var inner = new MarqueeScroller { Content = innerRow };
        var outerRow = new Grid();
        outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        outerRow.Children.Add(new TextBlock { Text = "a long left label that takes most of the bar's width", FontSize = 12 });
        Grid.SetColumn(inner, 1); outerRow.Children.Add(inner);
        var tail = new TextBlock { Text = "the outer row's last item", FontSize = 12, Margin = new Thickness(8, 0, 12, 0) };
        Grid.SetColumn(tail, 2); outerRow.Children.Add(tail);
        var outer = new MarqueeScroller { Content = outerRow, Height = 33 };
        var win = new Window
        {
            Width = 420, Height = 80, Left = -3000, Top = -3000, ShowActivated = false, WindowStyle = WindowStyle.None,
            Content = new Border { Child = outer, VerticalAlignment = VerticalAlignment.Top },
        };
        win.Show();
        win.UpdateLayout();
        Check("N1 the inner bar is held to its column's width", Math.Abs(inner.ActualWidth - 160) <= 0.5, $"{inner.ActualWidth:0}");
        Check("N1 the inner bar overflows its own width", inner.ScrollableWidth > 12, $"scrollable {inner.ScrollableWidth:0}");
        Check("N1 the outer bar overflows too", outer.ScrollableWidth > 12, $"scrollable {outer.ScrollableWidth:0}");
        Pump(2000);
        Check("N2 the nested bar glides by itself", inner.HorizontalOffset > 1, $"{inner.HorizontalOffset:0.0}/{inner.ScrollableWidth:0}");
        Check("N2 the outer bar glides by itself", outer.HorizontalOffset > 1, $"{outer.HorizontalOffset:0.0}/{outer.ScrollableWidth:0}");
        inner.PointerOverride = () => null; outer.PointerOverride = () => null;
        for (int i = 0; i < 60 * 60; i++)
        {
            inner.Step(1 / 60.0); outer.Step(1 / 60.0); win.UpdateLayout();
            if (inner.HorizontalOffset >= inner.ScrollableWidth - 0.5 && outer.HorizontalOffset >= outer.ScrollableWidth - 0.5) break;
        }
        var lastInner = (FrameworkElement)innerRow.Children[^1];
        double ir = lastInner.TransformToAncestor(inner).TransformBounds(new Rect(lastInner.RenderSize)).Right;
        double or = tail.TransformToAncestor(outer).TransformBounds(new Rect(tail.RenderSize)).Right;
        Check("N3 the nested bar's last item comes into view", ir <= inner.ViewportWidth + 0.5, $"ends at {ir:0}, viewport {inner.ViewportWidth:0}");
        Check("N3 the outer bar's last item comes into view", or <= outer.ViewportWidth + 0.5, $"ends at {or:0}, viewport {outer.ViewportWidth:0}");
        win.Close();
    }
}
