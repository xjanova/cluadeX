using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace ThemeTest;

internal static partial class Program
{
    static bool ChatRefOnly;

    /// <summary>
    /// The chat's reference chip ("# CX-8cf58c2a" + copy), pinned at the top of the chat page (owner 2026-11-09), in every
    /// theme — light and dark. Checks: it is on the page and shows the chat's reference, its text and its copy glyph stand
    /// out from the chip (contrast measured on the render), and at least one light and one dark theme were looked at.
    /// Saved to renders\chat-ref\&lt;theme&gt;.png (the chip, cropped) and &lt;theme&gt;-top.png (the top of the window) for the eye.
    ///
    ///   dotnet run -- --chat-ref --out D:\...\themes
    /// </summary>
    static async Task ChatRefShots(object themes)
    {
        Log("\n## Chat reference chip — pinned at the top of the chat, every theme");
        string dir = Path.Combine(OutRoot, "renders", "chat-ref");
        Directory.CreateDirectory(dir);
        MainVm.NavigateToCommand.Execute("Permissions");
        await Idle();
        MainVm.NavigateToCommand.Execute("Chat");
        await Idle(200);
        var ids = themes == null ? new List<string> { "current" }
            : ((IEnumerable)Prop(themes, "Themes")).Cast<object>().Select(t => (string)Prop(t, "Id")).ToList();
        bool sawLight = false, sawDark = false;
        foreach (string id in ids)
        {
            if (themes != null) { Invoke(themes, "Apply", id, true); await Idle(200); }
            var root = (FrameworkElement)RootVisual(MainWin);
            var chip = Find<Border>(root).FirstOrDefault(b => b.Name == "ChatRefChip");
            var text = Find<TextBox>(root).FirstOrDefault(t => t.Name == "ChatRefText");
            var copy = Find<Button>(root).FirstOrDefault(b => b.Name == "ChatRefCopyButton");
            string want = (string)Prop(MainVm.ChatVM, "CurrentChatRef") ?? "";
            if (chip == null || text == null || copy == null)
            {
                Check($"chat-ref {id}: the chip is on the chat page", false, $"chip {chip != null} · text {text != null} · copy {copy != null}");
                continue;
            }
            Check($"chat-ref {id}: the chip shows, with this chat's reference", chip.IsVisible && chip.ActualWidth > 40 && want.StartsWith("CX-") && text.Text == want,
                $"visible {chip.IsVisible} · {chip.ActualWidth:0} px · '{text.Text}' vs '{want}'");

            var full = await Stable(() => (root, W, H), $"chat-ref/{id}");
            Int32Rect Box(FrameworkElement e, int pad)
            {
                var r = e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
                int x = Math.Max(0, (int)Math.Floor(r.X) - pad), y = Math.Max(0, (int)Math.Floor(r.Y) - pad);
                return new Int32Rect(x, y, Math.Min(W - x, (int)Math.Ceiling(r.Width) + 2 * pad), Math.Min(H - y, (int)Math.Ceiling(r.Height) + 2 * pad));
            }
            SavePng(new CroppedBitmap(full, Box(chip, 8)), Path.Combine(dir, id.ToLowerInvariant() + ".png"));
            SavePng(new CroppedBitmap(full, new Int32Rect(0, 0, W, 260)), Path.Combine(dir, id.ToLowerInvariant() + "-top.png"));   // in its place

            // Contrast: the most common colour of the region is its background; the pixel farthest from it in luminance is
            // the ink (text strokes, the glyph). WCAG ratio between the two, and how many pixels are clearly ink.
            (double Bg, double Ratio, int Ink) Measure(Int32Rect box)
            {
                var px = Pixels(new CroppedBitmap(full, box));
                var lum = new double[px.Length / 4];
                var counts = new Dictionary<int, int>();
                for (int i = 0; i < lum.Length; i++)
                {
                    byte b = px[i * 4], g = px[i * 4 + 1], r = px[i * 4 + 2];
                    lum[i] = Lum(r, g, b);
                    int key = (r << 16) | (g << 8) | b;
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
                int bgKey = counts.MaxBy(kv => kv.Value).Key;
                double bg = Lum((byte)(bgKey >> 16), (byte)(bgKey >> 8), (byte)bgKey);
                double ink = lum.MaxBy(l => Math.Abs(l - bg));
                int inkPx = lum.Count(l => Math.Abs(l - bg) > 0.12);
                return (bg, (Math.Max(bg, ink) + 0.05) / (Math.Min(bg, ink) + 0.05), inkPx);
            }
            var t = Measure(Box(text, 0));
            var c = Measure(Box(copy, -3));
            if (t.Bg > 0.5) sawLight = true;
            if (t.Bg < 0.2) sawDark = true;
            Log($"  {id}: chip background luminance {t.Bg:0.00} ({(t.Bg > 0.5 ? "light" : "dark")}) · text {t.Ratio:0.0}:1, {t.Ink} ink px · copy glyph {c.Ratio:0.0}:1, {c.Ink} ink px");
            Check($"chat-ref {id}: the reference text stands out from the chip (≥ 4.5:1)", t.Ratio >= 4.5 && t.Ink >= 30, $"{t.Ratio:0.0}:1 · {t.Ink} px");
            Check($"chat-ref {id}: the copy glyph stands out from the chip (≥ 3:1)", c.Ratio >= 3 && c.Ink >= 8, $"{c.Ratio:0.0}:1 · {c.Ink} px");
        }
        if (themes != null)
        {
            Check("chat-ref: a light and a dark theme were both checked", sawLight && sawDark, $"light {sawLight} · dark {sawDark}");
            Invoke(themes, "Apply", "Midnight", true);
            await Idle(200);
        }
    }

    /// <summary>WCAG relative luminance of an sRGB colour.</summary>
    static double Lum(byte r, byte g, byte b)
    {
        static double C(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * C(r) + 0.7152 * C(g) + 0.0722 * C(b);
    }
}
