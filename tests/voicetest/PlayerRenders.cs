using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Voice.Tts;
using CluadeX.Views;

namespace VoiceTest;

/// <summary>
/// The voice player box (VoicePlayerBar) rendered for real in every theme, Thai and English, playing / paused / done,
/// at its narrowest and widest — to PNGs in voice-out\player. Fails on an untranslated text, a blank render, the play
/// glyph or the reply's words lost against their background, the wrong glyph or state text, or a control outside the
/// box. The queue behind it plays into a silent stand-in: nothing reaches the speakers.
/// </summary>
internal static partial class Program
{
    static async Task PlayerRenders(SettingsService settings, LocalizationService loc)
    {
        Log("\n## The voice player box — every theme, th + en");
        string dir = Path.Combine(Out, "player");
        Directory.CreateDirectory(dir);
        var themes = new ThemeService(settings);

        var o = new FakeOutput();
        var q = new ReplyVoiceQueue(o);
        var girls = new[] { "claude", "grok", "codex" };
        var msgs = girls.Select((g, i) => new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = i == 0 ? "สวัสดีค่ะ แก้บั๊กปุ่มบันทึกให้แล้วนะคะ ลองกดดูอีกทีได้เลยค่ะ ถ้ายังไม่ได้บอกน้องได้นะ" : $"Reply {i + 1}",
            Character = ChatCharacter.ForGirl(g),
        }).ToList();

        var bar = new VoicePlayerBar { DataContext = q, Width = 560 };
        var host = new Border { Padding = new Thickness(16), Child = bar };
        host.SetResourceReference(Border.BackgroundProperty, "Bg.Elevated");
        var win = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight, Left = -3000, Top = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        win.Show();

        bool sawLight = false, sawDark = false;
        foreach (var lang in new[] { "th", "en" })
        {
            loc.SetLanguage(lang);
            foreach (var theme in themes.Themes.Where(t => t.IsReady).Select(t => t.Id))
            {
                themes.Apply(theme, persist: false);
                q.Clear();
                for (int i = 0; i < msgs.Count; i++)
                    q.Enqueue(new SpokenItem(msgs[i].Content, i == 0, girls[i], msgs[i]));
                await Idle(120);
                string tag = $"player {theme} {lang}";

                // playing, two waiting
                Check($"{tag}: playing the first, two waiting", q.IsPlaying && q.Waiting == 2, $"{q.State} · {q.Waiting}");
                int colours = Save(host, Path.Combine("player", $"{theme}-{lang}-playing.png"));
                Check($"{tag}: rendered", bar.ActualHeight > 40 && colours > 25, $"{colours} colours · {bar.ActualHeight:0} px");
                var texts = AllText(bar);
                var raw = texts.Where(t => t.StartsWith("voice.") || t.Contains("{0}")).ToList();
                Check($"{tag}: every text is translated", raw.Count == 0, string.Join(", ", raw));
                string want = lang == "th" ? "กำลังอ่าน" : "reading", waiting = lang == "th" ? "รอคิวอีก 2" : "2 waiting";
                string line = string.Join(" ", texts);
                Check($"{tag}: says it is reading, 1 / 3, two waiting", line.Contains(want) && line.Contains("1 / 3") && line.Contains(waiting), Short(line, 120));
                Check($"{tag}: her first words show", texts.Any(t => t.StartsWith(msgs[0].Content[..9], StringComparison.Ordinal)), Short(string.Join(" | ", texts), 160));
                Check($"{tag}: the play button shows pause", (string)((Button)bar.FindName("PlayButton")).Content == "", ((string)((Button)bar.FindName("PlayButton")).Content).Length.ToString());

                var full = Snap(host);
                var disc = Measure(full, host, ((Button)bar.FindName("PlayButton")), -6, median: true);   // the middle of the disc (Classic draws a glossy gradient)
                var words = Measure(full, host, ((TextBlock)bar.FindName("PreviewText")), 0);
                if (words.Bg > 0.5) sawLight = true;
                if (words.Bg < 0.2) sawDark = true;
                Check($"{tag}: the pause glyph stands out from its disc (≥ 3:1)", disc.Ratio >= 3 && disc.Ink >= 8, $"{disc.Ratio:0.0}:1 · {disc.Ink} px");
                Check($"{tag}: her words stand out from the box (≥ 4.5:1)", words.Ratio >= 4.5 && words.Ink >= 30, $"{words.Ratio:0.0}:1 · {words.Ink} px");

                // paused
                q.TogglePause();
                await Idle(60);
                Save(host, Path.Combine("player", $"{theme}-{lang}-paused.png"));
                line = string.Join(" ", AllText(bar));
                Check($"{tag}: paused — says so, the button shows play", line.Contains(lang == "th" ? "หยุดไว้" : "paused") && (string)((Button)bar.FindName("PlayButton")).Content == "", Short(line, 80));

                // done: all three read
                q.TogglePause(); o.Finish(); await Idle(); o.Finish(); await Idle(); o.Finish();
                await Idle(60);
                Save(host, Path.Combine("player", $"{theme}-{lang}-done.png"));
                line = string.Join(" ", AllText(bar));
                Check($"{tag}: done — 3 / 3, nothing waiting", line.Contains(lang == "th" ? "อ่านจบแล้ว" : "done") && line.Contains("3 / 3") && !line.Contains(lang == "th" ? "รอคิว" : "waiting"), Short(line, 80));

                // narrowest and widest: every button inside the box
                foreach (double w in new[] { 360.0, 640.0 })
                {
                    bar.Width = w;
                    await Idle(40);
                    var outside = Find<Button>(bar).Where(b => b.IsVisible)
                        .Select(b => (b, r: b.TransformToAncestor(bar).TransformBounds(new Rect(0, 0, b.ActualWidth, b.ActualHeight))))
                        .Where(x => x.r.Left < -1 || x.r.Right > bar.ActualWidth + 1 || x.r.Top < -1 || x.r.Bottom > bar.ActualHeight + 1)
                        .Select(x => $"{x.b.Name} {x.r.Left:0}..{x.r.Right:0}").ToList();
                    Check($"{tag} @ {w:0}: every button inside the box", outside.Count == 0, string.Join(", ", outside));
                }
                bar.Width = 560;
            }
        }
        Check("player: a light and a dark theme were both looked at", sawLight && sawDark, $"light {sawLight} · dark {sawDark}");
        q.Clear();
        win.Close();
        loc.SetLanguage("th");
    }

    static List<string> AllText(DependencyObject root)
    {
        var all = new List<string>();
        foreach (var t in Find<TextBlock>(root).Where(t => t.IsVisible))
        {
            string s = t.Inlines.Count > 0 ? string.Concat(t.Inlines.OfType<Run>().Select(r => r.Text + " ")) : t.Text;
            if (s.Trim().Length > 0) all.Add(s.Trim());
        }
        return all;
    }

    static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in Find<T>(c)) yield return x;
        }
    }

    static string Short(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    static RenderTargetBitmap Snap(FrameworkElement el)
    {
        el.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(el.ActualWidth), (int)Math.Ceiling(el.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(el);
        return bmp;
    }

    /// <summary>The most common colour of the element's area is its background; the pixel farthest from it in
    /// luminance is the ink. WCAG ratio between the two, and how many pixels are clearly ink.</summary>
    static (double Bg, double Ratio, int Ink) Measure(BitmapSource full, FrameworkElement root, FrameworkElement e, int pad, bool median = false)
    {
        var r = e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
        int x = Math.Max(0, (int)Math.Floor(r.X) - pad), y = Math.Max(0, (int)Math.Floor(r.Y) - pad);
        int w = Math.Min(full.PixelWidth - x, (int)Math.Ceiling(r.Width) + 2 * pad), h = Math.Min(full.PixelHeight - y, (int)Math.Ceiling(r.Height) + 2 * pad);
        if (w <= 0 || h <= 0) return (0, 1, 0);
        var crop = new CroppedBitmap(full, new Int32Rect(x, y, w, h));
        var px = new byte[w * h * 4];
        crop.CopyPixels(px, w * 4, 0);
        var lum = new double[w * h];
        var counts = new Dictionary<int, int>();
        for (int i = 0; i < lum.Length; i++)
        {
            byte b = px[i * 4], g = px[i * 4 + 1], rr = px[i * 4 + 2];
            lum[i] = Lum(rr, g, b);
            int key = (rr << 16) | (g << 8) | b;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        int bgKey = counts.MaxBy(kv => kv.Value).Key;
        double bg = Lum((byte)(bgKey >> 16), (byte)(bgKey >> 8), (byte)bgKey);
        double ink = lum.MaxBy(l => Math.Abs(l - bg));
        if (median)
        {
            // A gradient (Classic's glossy disc) has no one colour: the middle luminance of what is not ink stands for it.
            var under = lum.Where(l => Math.Abs(l - ink) > 0.25).OrderBy(l => l).ToList();
            if (under.Count > 0) bg = under[under.Count / 2];
        }
        int inkPx = lum.Count(l => Math.Abs(l - bg) > 0.12);
        return (bg, (Math.Max(bg, ink) + 0.05) / (Math.Min(bg, ink) + 0.05), inkPx);
    }

    static double Lum(byte r, byte g, byte b)
    {
        static double C(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * C(r) + 0.7152 * C(g) + 0.0722 * C(b);
    }
}
