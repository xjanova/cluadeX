using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CluadeX.Services.Pet;

namespace CluadeX.Views.Pet;

/// <summary>
/// What the pet leaves behind in prank mode (owner 2026-10-20: "ถ้าใครได้ตัวกวนๆ ก็ขี้ได้ แกล้ง ลบหน้าจอส่วนนั้นให้หายไปได้
/// แก้ได้ด้วยการคลิ๊กที่สัตว์เลี้ยงถี่ๆ เหมือนทำโทษ"): a poop on its floor (click it to clean it up) or a nibbled patch of
/// the screen. A patch only LOOKS like a hole — every click goes through it to the window underneath, so no work is
/// ever blocked — and it heals by itself after a while. Small topmost windows that never take the focus.
/// </summary>
public sealed class PetPrank : Window
{
    public enum Kind { Poop, Bite }

    public Kind What { get; }
    public DateTime Until { get; }
    private nint _hwnd;
    private readonly int _x, _y;

    private PetPrank(Kind kind, UIElement content, double widthDip, double heightDip, int x, int y, TimeSpan life, bool clickThrough)
    {
        What = kind;
        Until = DateTime.Now + life;
        _x = x; _y = y;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = widthDip;
        Height = heightDip;
        Content = content;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            if (clickThrough) PetDesktop.MakeClickThrough(_hwnd); else PetDesktop.MakeOverlay(_hwnd);
            PetDesktop.Move(_hwnd, _x, _y);
        };
    }

    /// <summary>A poop at (x = its centre, y = the floor) in screen pixels. <paramref name="cleaned"/> runs when it is clicked away.</summary>
    public static PetPrank Poop(int x, int floorY, double scale, Action<PetPrank> cleaned)
    {
        var text = new TextBlock
        {
            Text = "💩", FontSize = 22, FontFamily = new FontFamily("Segoe UI Emoji"), Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "🧹", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
        };
        double w = 30, h = 30;
        var p = new PetPrank(Kind.Poop, text, w, h, x - (int)(w * scale / 2), floorY - (int)(h * scale), TimeSpan.FromMinutes(15), clickThrough: false);
        text.MouseLeftButtonUp += (_, e) => { e.Handled = true; cleaned(p); };
        return p;
    }

    /// <summary>A nibbled patch, <paramref name="rect"/> in screen pixels: a transparency checkerboard (the "erased"
    /// look of an image editor) inside a bitten, jagged edge, a few crumbs around it. Click-through.</summary>
    public static PetPrank Bite(PetRect rect, double scale, int seed)
    {
        double w = rect.Width / scale, h = rect.Height / scale;
        var rng = new Random(seed);
        // A jagged outline: bites along every side.
        var fig = new PathFigure { IsClosed = true, StartPoint = new Point(8, 8) };
        void Edge(Point a, Point b, Vector inward)
        {
            int teeth = 4 + rng.Next(4);
            for (int i = 1; i <= teeth; i++)
            {
                var p = a + (b - a) * (i / (double)teeth);
                var mid = a + (b - a) * ((i - 0.5) / teeth) + inward * (4 + rng.NextDouble() * 9);
                fig.Segments.Add(new QuadraticBezierSegment(mid, p, true));
            }
        }
        Edge(new Point(8, 8), new Point(w - 8, 8), new Vector(0, 1));
        Edge(new Point(w - 8, 8), new Point(w - 8, h - 8), new Vector(-1, 0));
        Edge(new Point(w - 8, h - 8), new Point(8, h - 8), new Vector(0, -1));
        Edge(new Point(8, h - 8), new Point(8, 8), new Vector(1, 0));
        var shape = new PathGeometry(new[] { fig });

        var checker = new DrawingBrush
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 12, 12), ViewportUnits = BrushMappingMode.Absolute,
            Drawing = new DrawingGroup
            {
                Children =
                {
                    new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)), null, new RectangleGeometry(new Rect(0, 0, 12, 12))),
                    new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)), null,
                        new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 6, 6)), new RectangleGeometry(new Rect(6, 6, 6, 6)) } }),
                },
            },
        };
        var canvas = new Canvas { Width = w, Height = h, IsHitTestVisible = false };
        canvas.Children.Add(new System.Windows.Shapes.Path { Data = shape, Fill = checker, Stroke = new SolidColorBrush(Color.FromArgb(0x90, 0x55, 0x40, 0x30)), StrokeThickness = 1.5 });
        for (int i = 0; i < 6; i++)   // crumbs
        {
            var crumb = new System.Windows.Shapes.Ellipse { Width = 2 + rng.Next(3), Height = 2 + rng.Next(3), Fill = new SolidColorBrush(Color.FromArgb(0xB0, 0x6B, 0x50, 0x3A)) };
            Canvas.SetLeft(crumb, rng.NextDouble() * w);
            Canvas.SetTop(crumb, h - 6 + rng.NextDouble() * 5);
            canvas.Children.Add(crumb);
        }
        return new PetPrank(Kind.Bite, canvas, w, h, rect.Left, rect.Top, TimeSpan.FromMinutes(2), clickThrough: true);
    }

    public PetRect ScreenRect(double scale) => new(_x, _y, _x + (int)(Width * scale), _y + (int)(Height * scale));
}
