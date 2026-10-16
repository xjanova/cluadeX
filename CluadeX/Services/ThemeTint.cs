using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace CluadeX.Services;

/// <summary>
/// Lavender Night's custom colours (owner 2026-10-16: "ธีมนี้มีความพิเศษคือ custom สีได้"). The whole theme
/// dictionary is recoloured from two picks — the background and the accent — instead of a handful of keys, so
/// every panel, gradient, glow and hover stays in one family. Neutral (low-saturation or dark) colours take the
/// background's hue and depth; vivid colours take the accent's hue. Status, diff, syntax, chart and role colours
/// keep their meaning (green ok, red error) and are never touched.
/// </summary>
public static class ThemeTint
{
    /// <summary>The background picks offered (the first is the theme's own), as #RRGGBB.</summary>
    public static readonly (string Hex, string NameTh, string NameEn)[] Backgrounds =
    [
        ("#13061A", "ม่วงราตรี (เดิม)", "Violet night (default)"),
        ("#0B0F24", "น้ำเงินเที่ยงคืน", "Midnight blue"),
        ("#071A1C", "เขียวหัวเป็ดลึก", "Deep teal"),
        ("#1E0612", "ไวน์แดง", "Wine"),
        ("#1A0E0A", "มอคค่า", "Mocha"),
        ("#0A1408", "ป่าราตรี", "Night forest"),
        ("#101216", "เทาสเลท", "Slate"),
        ("#050506", "ดำสนิท", "Jet black"),
        ("#140A2A", "ครามม่วง", "Indigo"),
        ("#24102A", "พลัมนุ่ม", "Soft plum"),
    ];

    /// <summary>The accent picks offered (the first is the theme's own).</summary>
    public static readonly (string Hex, string NameTh, string NameEn)[] Accents =
    [
        ("#A44CFF", "ลาเวนเดอร์ (เดิม)", "Lavender (default)"),
        ("#FF5ECF", "ชมพูนีออน", "Neon pink"),
        ("#5EB8FF", "ฟ้าคริสตัล", "Crystal sky"),
        ("#3EE6B0", "มิ้นต์", "Mint"),
        ("#F5C451", "ทองจันทร์", "Moon gold"),
        ("#FF7A6B", "ปะการัง", "Coral"),
        ("#7C83FF", "คราม", "Periwinkle"),
        ("#E0E0FF", "ขาวมุก", "Pearl white"),
    ];

    private static readonly string[] KeepKeys = ["Status.", "Diff.", "Syntax.", "Chart.", "Role.", "Success", "Warning", "Danger", "Error", "Added", "Removed"];

    /// <summary>Recolour <paramref name="dict"/> in place. Null / unparsable picks leave that half unchanged.</summary>
    public static void Apply(ResourceDictionary dict, string? background, string? accent)
    {
        Color? bg = Parse(background), ac = Parse(accent);
        if (bg == null && ac == null) return;
        var (bgH, bgS, bgL) = bg is { } b ? ToHsl(b) : (0, 0, 0);
        var (acH, acS, _) = ac is { } a ? ToHsl(a) : (0, 0, 0);
        const double baseBgL = 0.065;   // the default background's lightness: picks scale depth relative to it

        Color Map(Color c)
        {
            var (h, s, l) = ToHsl(c);
            bool neutral = s < 0.35 || l < 0.22;
            if (neutral && bg != null)
                return FromHsl(bgH, Math.Min(1, s * (bgS / 0.5 + 0.2)), Math.Clamp(l * (0.35 + bgL / baseBgL * 0.65), 0, 1), c.A);
            if (!neutral && ac != null)
                return FromHsl(acH, Math.Clamp(s * (0.6 + acS * 0.4), 0, 1), l, c.A);
            return c;
        }

        foreach (var key in dict.Keys.Cast<object>().ToList())
        {
            string k = key as string ?? "";
            if (KeepKeys.Any(x => k.Contains(x, StringComparison.Ordinal))) continue;
            switch (dict[key])
            {
                case SolidColorBrush sb:
                    dict[key] = Freeze(new SolidColorBrush(Map(sb.Color)) { Opacity = sb.Opacity });
                    break;
                case GradientBrush gb:
                    var copy = gb.CloneCurrentValue();
                    foreach (var stop in copy.GradientStops) stop.Color = Map(stop.Color);
                    dict[key] = Freeze(copy);
                    break;
                case DropShadowEffect fx:
                    var e = fx.CloneCurrentValue();
                    e.Color = Map(e.Color);
                    dict[key] = Freeze(e);
                    break;
                case Color col:
                    dict[key] = Map(col);
                    break;
            }
        }
    }

    private static T Freeze<T>(T f) where T : Freezable { if (f.CanFreeze) f.Freeze(); return f; }

    public static Color? Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return (Color)ColorConverter.ConvertFromString(hex.Trim().StartsWith('#') ? hex.Trim() : "#" + hex.Trim()); }
        catch { return null; }
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, h = 0, s = 0;
        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    private static Color FromHsl(double h, double s, double l, byte a)
    {
        double C(double t)
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            t = (t % 1 + 1) % 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        double hh = h / 360;
        return Color.FromArgb(a, (byte)Math.Round(C(hh + 1 / 3.0) * 255), (byte)Math.Round(C(hh) * 255), (byte)Math.Round(C(hh - 1 / 3.0) * 255));
    }
}
