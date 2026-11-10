using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using CluadeX.Services;
using CluadeX.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace ThemeTest;

internal static partial class Program
{
    static bool TitleBarOnly;

    /// <summary>
    /// The main window's title bar with the license chip right after the name (owner 2026-11-08): every theme, at the
    /// narrowest window (900), a common one (1440) and a wide one (1920), in each chip state, in English and Thai. Checks that the chip sits
    /// after the wordmark, overlaps neither the search pill nor the caption buttons, is clickable in the caption, and that
    /// the window still drags from the name and from the empty part of the bar.
    ///
    ///   dotnet run -- --titlebar --out D:\...\renders\cluadex\titlebar
    /// </summary>
    static async Task TitleBarShots(object themes)
    {
        Log("\n## Title bar — the license chip next to the name");
        var loc = Sp.GetRequiredService<LocalizationService>();
        var ids = ((IEnumerable)Prop(themes, "Themes")).Cast<object>().Select(t => (string)Prop(t, "Id")).ToList();
        Log("themes: " + string.Join(", ", ids));
        string dir = Path.Combine(OutRoot, "titlebar");

        foreach (string id in ids)
        {
            Invoke(themes, "Apply", id, true);
            await Idle(200);
            foreach (string lang in new[] { "en", "th" })
            {
                loc.SetLanguage(lang);
                await Idle(100);
                foreach (int w in new[] { 900, 1440, 1920 })
                {
                    var states = id.Equals("Midnight", StringComparison.OrdinalIgnoreCase) && lang == "en" ? new[] { "none", "trial", "active" } : new[] { "none" };
                    foreach (string state in states)
                        await TitleBarShot(dir, id, lang, w, state, loc);
                }
            }
        }
        loc.SetLanguage("en");
        MainWin.Width = W;
        Invoke(themes, "Apply", "Midnight", true);
        await Idle(200);
    }

    static async Task TitleBarShot(string dir, string theme, string lang, int width, string state, LocalizationService loc)
    {
        SetChip(state, loc);
        MainWin.Width = width;
        await Idle(250);
        string name = $"{theme.ToLowerInvariant()}-{lang}-{width}-{state}";
        var root = (FrameworkElement)RootVisual(MainWin);
        var full = await Stable(() => (root, width, H), "titlebar/" + name);
        var crop = new CroppedBitmap(full, new Int32Rect(0, 0, width, 42));
        string path = Path.Combine(dir, name + ".png");
        SavePng(crop, path);
        Log($"  {name}: {path}");

        // where everything is, in window coordinates
        Rect R(FrameworkElement e) => e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
        var chip = Find<Button>(root).FirstOrDefault(b => b.Command == MainVm.OpenLicenseCommand && b.IsVisible);
        var mark = Find<TextBlock>(root).FirstOrDefault(t => t.Inlines.OfType<System.Windows.Documents.Run>().Any(r => r.Text == "Cluade") && t.IsVisible);
        var pill = (FrameworkElement)MainWin.FindName("TitlePalette");
        var min = (FrameworkElement)MainWin.FindName("BtnMinimize");
        var titleRow = (FrameworkElement)MainWin.FindName("TitleContent");
        var glide = titleRow == null ? null : FindAncestor<CluadeX.Views.MarqueeScroller>(titleRow);
        if (chip == null || mark == null || min == null || glide == null)
        {
            Check($"titlebar {name}: found the chip, the name, the glide and the caption buttons", false,
                $"chip {chip != null} · name {mark != null} · glide {glide != null} · caption {min != null}");
            return;
        }
        Rect c = R(chip), m = R(mark), g = R(glide), cap = R(min);
        Check($"titlebar {name}: the chip comes right after the name", c.Left >= m.Right - 0.5 && c.Left - m.Right <= 20, $"name ends {m.Right:0}, chip starts {c.Left:0}");
        Check($"titlebar {name}: the chip is centred in the bar", Math.Abs((c.Top + c.Bottom) / 2 - 21) <= 2, $"{c.Top:0}-{c.Bottom:0}");
        Check($"titlebar {name}: the chip ends before the gliding row starts", c.Right <= g.Left + 0.5, $"chip {c.Right:0} · row {g.Left:0}");
        Check($"titlebar {name}: the chip stays clear of the caption buttons", c.Right < cap.Left, $"chip {c.Right:0} · caption {cap.Left:0}");
        if (width >= 1920)   // room for everything: the search pill is there, and the chip keeps clear of it
            Check($"titlebar {name}: the search pill shows on a wide window", pill is { IsVisible: true, ActualWidth: > 100 }, $"{pill?.ActualWidth:0} px");
        if (pill is { IsVisible: true, ActualWidth: > 0 })
        {
            Rect p = R(pill);
            Check($"titlebar {name}: the chip stays clear of the search pill", !c.IntersectsWith(p) || c.Right <= p.Left, $"chip {c.Left:0}-{c.Right:0} · pill {p.Left:0}-{p.Right:0}");
        }
        Check($"titlebar {name}: the chip takes clicks in the caption", WindowChrome.GetIsHitTestVisibleInChrome(chip));

        // dragging: the caption is the top 42 px; a point drags when what is under it is not marked hit-test-visible
        var chrome = WindowChrome.GetWindowChrome(MainWin);
        Check($"titlebar {name}: the caption covers the bar", chrome != null && chrome.CaptionHeight >= 40, $"{chrome?.CaptionHeight}");
        bool Drags(Point pt)
        {
            var hit = root.InputHitTest(pt);
            return hit == null || !WindowChrome.GetIsHitTestVisibleInChrome(hit);
        }
        Check($"titlebar {name}: the window drags from the name", Drags(new Point((m.Left + m.Right) / 2, 21)));
        int free = 0;
        for (double x = g.Left + 2; x < Math.Min(g.Right, cap.Left) - 2; x += 4) if (Drags(new Point(x, 21))) free++;
        Check($"titlebar {name}: the window drags from the empty part of the bar", free > 0, $"{free * 4} px free");
    }

    /// <summary>The chip's three looks, without a license server: no key (the glowing invitation), trial, Pro.</summary>
    static void SetChip(string state, LocalizationService loc)
    {
        void P(string prop, object v) => typeof(MainViewModel).GetProperty(prop)!.SetValue(MainVm, v);
        switch (state)
        {
            case "trial": P("LicenseChipState", "trial"); P("LicenseChipText", string.Format(loc.T("license.chip.trial"), loc.CurrentLanguage == "th" ? "6 วัน" : "6 days")); break;
            case "active": P("LicenseChipState", "active"); P("LicenseChipText", "Pro"); break;
            default: P("LicenseChipState", "none"); P("LicenseChipText", loc.T("license.chip.none")); break;
        }
    }

    static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }
}
