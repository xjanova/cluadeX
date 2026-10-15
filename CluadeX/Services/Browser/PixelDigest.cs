using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CluadeX.Services.Browser;

/// <summary>Decoded pixels of one screenshot (BGRA, top-down).</summary>
public sealed class PixelFrame
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int Stride { get; init; }
    public byte[] Bgra { get; init; } = Array.Empty<byte>();

    /// <summary>(R, G, B) at image pixel (x, y), clamped to the frame.</summary>
    public (byte R, byte G, byte B) At(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        int i = y * Stride + x * 4;
        return (Bgra[i + 2], Bgra[i + 1], Bgra[i]);
    }

    /// <summary>Decode a JPEG/PNG. Works on any thread (WIC is free-threaded; the bitmaps stay local).</summary>
    public static PixelFrame Decode(byte[] encoded)
    {
        using var ms = new MemoryStream(encoded, writable: false);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgra32) frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = frame.PixelWidth * 4;
        var buffer = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(buffer, stride, 0);
        return new PixelFrame { Width = frame.PixelWidth, Height = frame.PixelHeight, Stride = stride, Bgra = buffer };
    }
}

/// <summary>A canvas on the page, in screenshot pixels.</summary>
public sealed record CanvasRegion(string Label, int X, int Y, int W, int H, int CssW, int CssH);

/// <summary>A coarse luminance grid of one screenshot, to tell "is it moving?" without keeping the image.</summary>
public sealed class FrameSignature
{
    public int Cols { get; init; }
    public int Rows { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    /// <summary>What the shot showed (viewport/full page/region + scroll + size) — only same-key shots compare.</summary>
    public string Key { get; init; } = "";
    public byte[] Cells { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// The text that goes with every screenshot, so a model that cannot see images still learns what is on screen:
/// dominant colours, each canvas blank or not, and how much changed since the previous shot ("animating").
/// </summary>
public static class PixelDigest
{
    private const int GridCols = 128;

    public static FrameSignature Signature(PixelFrame f, string key)
    {
        int cols = Math.Min(GridCols, f.Width);
        int rows = Math.Max(1, (int)Math.Round(cols * (double)f.Height / Math.Max(1, f.Width)));
        rows = Math.Min(rows, f.Height);
        var cells = new byte[cols * rows];
        for (int r = 0; r < rows; r++)
        {
            int y0 = r * f.Height / rows, y1 = Math.Max(y0 + 1, (r + 1) * f.Height / rows);
            for (int c = 0; c < cols; c++)
            {
                int x0 = c * f.Width / cols, x1 = Math.Max(x0 + 1, (c + 1) * f.Width / cols);
                long sum = 0; int n = 0;
                int stepY = Math.Max(1, (y1 - y0) / 4), stepX = Math.Max(1, (x1 - x0) / 4);
                for (int y = y0; y < y1; y += stepY)
                    for (int x = x0; x < x1; x += stepX)
                    {
                        int i = y * f.Stride + x * 4;
                        sum += (f.Bgra[i + 2] * 299 + f.Bgra[i + 1] * 587 + f.Bgra[i] * 114) / 1000;
                        n++;
                    }
                cells[r * cols + c] = (byte)(n > 0 ? sum / n : 0);
            }
        }
        return new FrameSignature { Cols = cols, Rows = rows, ImageWidth = f.Width, ImageHeight = f.Height, Key = key, Cells = cells };
    }

    /// <summary>Percent of grid cells that changed by more than a few levels (null = the shots don't compare).</summary>
    public static double? ChangedPercent(FrameSignature? before, FrameSignature now, (int X, int Y, int W, int H)? region = null)
    {
        if (before == null || before.Key != now.Key || before.Cols != now.Cols || before.Rows != now.Rows) return null;
        int c0 = 0, c1 = now.Cols, r0 = 0, r1 = now.Rows;
        if (region is { } g)
        {
            c0 = Math.Clamp(g.X * now.Cols / Math.Max(1, now.ImageWidth), 0, now.Cols);
            c1 = Math.Clamp((g.X + g.W) * now.Cols / Math.Max(1, now.ImageWidth) + 1, 0, now.Cols);
            r0 = Math.Clamp(g.Y * now.Rows / Math.Max(1, now.ImageHeight), 0, now.Rows);
            r1 = Math.Clamp((g.Y + g.H) * now.Rows / Math.Max(1, now.ImageHeight) + 1, 0, now.Rows);
        }
        int total = 0, changed = 0;
        for (int r = r0; r < r1; r++)
            for (int c = c0; c < c1; c++)
            {
                total++;
                int i = r * now.Cols + c;
                if (Math.Abs(before.Cells[i] - now.Cells[i]) > 6) changed++;
            }
        return total == 0 ? null : 100.0 * changed / total;
    }

    /// <summary>The digest lines.</summary>
    public static string Describe(PixelFrame f, IReadOnlyList<CanvasRegion> canvases, FrameSignature? previous, FrameSignature now,
        IReadOnlyList<string> dialogs, bool? overflowX)
    {
        var sb = new StringBuilder();
        sb.Append("Pixel digest: ").Append(DominantText(f, 0, 0, f.Width, f.Height, 5)).Append('\n');

        double? changed = ChangedPercent(previous, now);
        if (changed is double ch)
            sb.Append($"Changed since the previous screenshot: {ch:0.#}%{(ch > 0 ? " — the page is animating / changed" : " — nothing moved")}\n");
        else
            sb.Append("Changed since the previous screenshot: n/a (first shot of this view)\n");

        if (canvases.Count == 0) sb.Append("Canvases: none on screen\n");
        foreach (var cv in canvases.Take(8))
        {
            int x0 = Math.Clamp(cv.X, 0, f.Width), y0 = Math.Clamp(cv.Y, 0, f.Height);
            int x1 = Math.Clamp(cv.X + cv.W, 0, f.Width), y1 = Math.Clamp(cv.Y + cv.H, 0, f.Height);
            sb.Append($"Canvas {cv.Label} {cv.CssW}x{cv.CssH} css px at image ({cv.X},{cv.Y}): ");
            if (x1 - x0 < 2 || y1 - y0 < 2) { sb.Append("outside the screenshot\n"); continue; }
            var (distinct, topShare, topHex) = Histogram(f, x0, y0, x1, y1);
            if (topShare >= 99.5) sb.Append($"BLANK — uniform {topHex}");
            else sb.Append($"NOT blank — {distinct} colours, most common {topHex} {topShare:0.#}%; ")
                   .Append(DominantText(f, x0, y0, x1, y1, 3));
            double? cvChanged = ChangedPercent(previous, now, (x0, y0, x1 - x0, y1 - y0));
            if (cvChanged is double cc) sb.Append($"; changed {cc:0.#}%{(cc > 0 ? " (animating)" : " (static)")}");
            sb.Append('\n');
        }
        if (overflowX == true) sb.Append("Layout: the page is wider than the viewport (horizontal scroll)\n");
        foreach (var d in dialogs.Take(5)) sb.Append("Dialog shown (auto-dismissed): ").Append(d).Append('\n');
        return sb.ToString().TrimEnd();
    }

    private static string DominantText(PixelFrame f, int x0, int y0, int x1, int y1, int top)
    {
        var counts = new Dictionary<int, int>();
        int total = 0;
        int area = Math.Max(1, (x1 - x0) * (y1 - y0));
        int step = Math.Max(1, (int)Math.Sqrt(area / 60_000.0));
        for (int y = y0; y < y1; y += step)
            for (int x = x0; x < x1; x += step)
            {
                int i = y * f.Stride + x * 4;
                int key = ((f.Bgra[i + 2] >> 4) << 8) | ((f.Bgra[i + 1] >> 4) << 4) | (f.Bgra[i] >> 4);
                counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
                total++;
            }
        if (total == 0) return "empty";
        var parts = counts.OrderByDescending(kv => kv.Value).Take(top)
            .Select(kv => $"{Hex(kv.Key)} {100.0 * kv.Value / total:0.#}% ({Name(kv.Key)})");
        return "dominant colours " + string.Join(", ", parts);
    }

    private static (int Distinct, double TopShare, string TopHex) Histogram(PixelFrame f, int x0, int y0, int x1, int y1)
    {
        var counts = new Dictionary<int, int>();
        int total = 0;
        int area = Math.Max(1, (x1 - x0) * (y1 - y0));
        int step = Math.Max(1, (int)Math.Sqrt(area / 60_000.0));
        for (int y = y0; y < y1; y += step)
            for (int x = x0; x < x1; x += step)
            {
                int i = y * f.Stride + x * 4;
                // JPEG noise: quantize to 4 bits so a flat fill is still "one colour".
                int key = ((f.Bgra[i + 2] >> 4) << 8) | ((f.Bgra[i + 1] >> 4) << 4) | (f.Bgra[i] >> 4);
                counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
                total++;
            }
        if (total == 0) return (0, 100, "#000000");
        var topKv = counts.MaxBy(kv => kv.Value);
        // Colours that cover a meaningful share (JPEG fringes create many 1-pixel buckets).
        int distinct = counts.Count(kv => kv.Value * 1000 >= total);
        return (Math.Max(1, distinct), 100.0 * topKv.Value / total, Hex(topKv.Key));
    }

    private static string Hex(int key)
    {
        int r = ((key >> 8) & 0xF) * 17, g = ((key >> 4) & 0xF) * 17, b = (key & 0xF) * 17;
        return $"#{r:x2}{g:x2}{b:x2}";
    }

    /// <summary>A rough colour word, for a model that reads text only.</summary>
    internal static string Name(int key)
    {
        double r = ((key >> 8) & 0xF) / 15.0, g = ((key >> 4) & 0xF) / 15.0, b = (key & 0xF) / 15.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d < 0.12) return l < 0.15 ? "black" : l > 0.88 ? "white" : l < 0.45 ? "dark grey" : "grey";
        double h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        string hue = h < 15 || h >= 345 ? "red" : h < 45 ? "orange" : h < 70 ? "yellow" : h < 160 ? "green"
                   : h < 200 ? "cyan" : h < 255 ? "blue" : h < 290 ? "purple" : "pink";
        return (l < 0.3 ? "dark " : l > 0.75 ? "light " : "") + hue;
    }
}

/// <summary>The sizing rules of a screenshot sent to a model.</summary>
public static class ScreenshotPipeline
{
    /// <summary>Largest image area sent to a model (≈1.15 megapixels).</summary>
    public const double MaxPixels = 1_150_000;

    /// <summary>clip.scale so the encoded image's long edge is at most <paramref name="maxEdge"/> and its area at
    /// most <see cref="MaxPixels"/>: min(1, maxEdge/(long·dpr), √(MaxPixels/(w·h·dpr²))).</summary>
    public static double Scale(double cssW, double cssH, double dpr, int maxEdge)
    {
        cssW = Math.Max(1, cssW); cssH = Math.Max(1, cssH);
        if (dpr <= 0) dpr = 1;
        maxEdge = Math.Clamp(maxEdge, 200, 4096);
        double byEdge = maxEdge / (Math.Max(cssW, cssH) * dpr);
        double byArea = Math.Sqrt(MaxPixels / (cssW * cssH * dpr * dpr));
        // A hair under the exact bound, so rounding in the encoder can never step over it.
        return Math.Min(1.0, Math.Min(byEdge, byArea) * 0.995);
    }
}
