// The Node Graph canvas colour (owner 2026-11-08 screenshot: the whole canvas solid cyan in Neon). Surface.Sunken is a
// translucent tint in three themes; it must be laid over the page, never have its alpha forced to 255. Reads the real
// theme files, so a new theme with a bright translucent tint is caught too.
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using CluadeX.Views.UiStudio;

namespace ApiTest;

internal static class NodeGraphBgWire
{
    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("offline · node graph canvas colour per theme");
        var neon = NodeGraphGpu.Over(Color.FromArgb(0x0A, 0x00, 0xE5, 0xFF), Color.FromRgb(0x18, 0x18, 0x25));
        check("graph-bg", "Neon's 4 % cyan tint over its page is a dark colour, not solid cyan",
            neon.B < 0x40 && neon.G < 0x40, $"got #{neon.R:X2}{neon.G:X2}{neon.B:X2}");
        var opaque = NodeGraphGpu.Over(Color.FromRgb(0xF4, 0xF4, 0xF8), Color.FromRgb(0, 0, 0));
        check("graph-bg", "an opaque colour comes through unchanged", opaque == Color.FromRgb(0xF4, 0xF4, 0xF8), opaque.ToString());

        var dir = FindThemes();
        check("graph-bg", "theme files found", dir != null, dir ?? "no CluadeX/Resources/Themes above the test");
        if (dir == null) return Task.CompletedTask;
        foreach (var f in Directory.GetFiles(dir, "*.xaml"))
        {
            var text = File.ReadAllText(f);
            Color? Get(string key)
            {
                var m = Regex.Match(text, $@"x:Key=""{Regex.Escape(key)}""\s+Color=""(#[0-9A-Fa-f]{{6,8}})""");
                return m.Success ? (Color)ColorConverter.ConvertFromString(m.Groups[1].Value) : null;
            }
            var sunken = Get("Surface.Sunken");
            var page = Get("Page.Background");
            if (sunken is null || page is null) continue;
            var seen = NodeGraphGpu.Over(sunken.Value, page.Value.A == 255 ? page.Value : NodeGraphGpu.Over(page.Value, Colors.Black));
            bool pageLight = Luma(page.Value) > 0.5, seenLight = Luma(seen) > 0.5;
            check("graph-bg", $"{Path.GetFileNameWithoutExtension(f)}: the canvas reads as light/dark like its page",
                pageLight == seenLight, $"page {page} canvas #{seen.R:X2}{seen.G:X2}{seen.B:X2}");
        }
        return Task.CompletedTask;
    }

    static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    static string? FindThemes()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "CluadeX", "Resources", "Themes");
            if (Directory.Exists(p)) return p;
        }
        return null;
    }
}
