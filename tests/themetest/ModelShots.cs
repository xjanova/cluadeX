using System.IO;
using System.Windows.Controls;
using CluadeX.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ThemeTest;

/// <summary>
/// --model-shots: extra pictures of the Model Manager for a human look, next to model-manager.png — the catalog cards
/// (scrolled), the page in Thai, and the narrowest window the app allows (900 px: nothing may be cut off there).
/// They are not compared with a golden; the page's own checks live in tests/managertest (advisor + static XAML).
/// </summary>
internal static partial class Program
{
    static bool ModelShots;

    static async Task ModelManagerShots(string set, string dir)
    {
        var view = FindPageView("model-manager");
        var sv = view == null ? null : Find<ScrollViewer>(view).OrderByDescending(s => s.ExtentHeight).FirstOrDefault();
        if (sv == null) { Log("  note: model-manager view / scroller not found — no extra shots"); return; }
        var loc = Sp.GetRequiredService<LocalizationService>();

        async Task Shot(string name, int w, int h, double offset)
        {
            sv.ScrollToVerticalOffset(offset);
            FreezeVolatile();
            var bmp = await Stable(() => (RootVisual(MainWin), w, h), $"{set}/{name}");
            SavePng(bmp, Path.Combine(dir, name + ".png"));
        }

        await Shot("model-manager-cards", W, H, 640);
        await Shot("model-manager-cards-2", W, H, 1500);

        string before = loc.CurrentLanguage;
        loc.SetLanguage("th");
        await Idle(300);
        await Shot("model-manager-th", W, H, 0);
        await Shot("model-manager-th-cards", W, H, 640);
        loc.SetLanguage(before);
        await Idle(300);

        const int NW = 900;
        MainWin.Width = NW;
        await Idle(600);
        await Shot("model-manager-narrow", NW, H, 0);
        await Shot("model-manager-narrow-cards", NW, H, 900);
        MainWin.Width = W;
        await Idle(600);
        sv.ScrollToTop();
        await Idle(200);
        Log($"  model-manager extra shots → {dir}");
    }
}
