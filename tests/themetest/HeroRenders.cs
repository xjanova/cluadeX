using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Services;
using CluadeX.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ThemeTest;

internal static partial class Program
{
    static bool HeroOnly;

    /// <summary>
    /// น้อง CluadeX big in the top-left corner (owner 2026-11-08: her sketch — a square under the title bar over the rail and
    /// part of the chat list) and Settings › Text size and font. For each theme, two window sizes, two zooms and two pages
    /// (Chat with its list, Settings without): nothing a user reads or clicks is under her, she is big beside the list and
    /// rail-sized without it, and the whole shell is saved for the eye in renders\hero\. Then the bundled font: every Latin
    /// weight draws from its own Noto Sans CX file (no fake bold), Thai is left to Leelawadee UI (WPF mis-places Thai marks
    /// in Noto Sans Thai — TypographyService.BundledFamily), and Thai at 9–28 px is saved for the eye.
    /// </summary>
    static async Task HeroShots()
    {
        Log("\n## น้อง CluadeX in the corner, text size and font (rendered for the eye)");
        string dir = Path.Combine(OutRoot, "renders", "hero");
        var themes = ThemeServiceOrNull();
        var typo = Sp.GetRequiredService<TypographyService>();
        typo.ApplySaved();
        var hero = MainWin.FindName("BuddyHero") as FrameworkElement;
        Check("hero: น้อง CluadeX is on the window", hero != null);
        if (hero == null) return;
        Check("hero: she is still a button to Home", hero is Button b0 && ReferenceEquals(b0.Command, MainVm.ChatVM.GoHomeCommand));
        Check("hero: her face is the big picture", FindAll<CluadexBuddy>(MainWin).Count == 1);

        var sets = new[] { ("Classic", "light"), ("Midnight", "dark"), ("Lavender", "lavender"), ("NeonCyber", "neon") };
        var sizes = new[] { (980, 640), (1600, 1000) };
        var zooms = new[] { 1.0, 1.4 };
        foreach (var (id, label) in sets)
        {
            if (themes != null) { Invoke(themes, "Apply", id, false); await Idle(200); }
            foreach (var (w, h) in sizes)
            foreach (double z in zooms)
            foreach (string page in new[] { "Chat", "Settings" })
            {
                MainWin.Width = w; MainWin.Height = h;
                typo.SetScale(z, persist: false);
                MainVm.NavigateToCommand.Execute(page);
                if (page == "Settings") MainVm.SettingsVM.SelectedCategory = "general";
                await Idle(350);
                string tag = $"{label} {w}×{h} {z * 100:0}% {page}";
                var root = (UIElement)RootVisual(MainWin);
                Rect her = Bounds(hero, root);
                double scale = z;
                bool chat = page == "Chat";
                double expected = CluadeX.MainWindow.HeroSizeFor(chat ? ((FrameworkElement)MainWin.FindName("ChatSidebar")).ActualWidth : 0,
                                                         ((FrameworkElement)MainWin.FindName("MainContentGrid")).ActualHeight) * scale;
                Check($"hero [{tag}]: her size is {(chat ? "big beside the list" : "the rail's")}",
                      Math.Abs(her.Width - expected) < 1.5 && (chat ? her.Width >= 84 * scale - 1 : Math.Abs(her.Width - 60 * scale) < 1.5),
                      $"{her.Width:0.#} px, expected {expected:0.#}");
                Check($"hero [{tag}]: she sits right under the title bar at the left edge",
                      Math.Abs(her.X) < 1.5 && Math.Abs(her.Y - 42 * scale) < 2.5, $"{her.X:0.#},{her.Y:0.#}");
                var under = Under(hero, her, root);
                Check($"hero [{tag}]: nothing to read or click is under her", under.Count == 0, string.Join("; ", under.Take(6)));
                if (z == 1.0 || w == 1600)
                {
                    FreezeVolatile();
                    var full = Snapshot(root, w, h);
                    SavePng(full, Path.Combine(dir, $"{label}-{w}x{h}-{z * 100:0}-{page.ToLowerInvariant()}.png"));
                    if (chat && z == 1.0 && w == 1600)
                        SavePng(new TransformedBitmap(new CroppedBitmap(full, new Int32Rect(0, 0, 420, 300)), new ScaleTransform(2, 2)),
                                Path.Combine(dir, $"{label}-corner-2x.png"));
                }
            }
        }
        typo.SetScale(1.0, persist: false);
        MainWin.Width = W; MainWin.Height = H;
        if (themes != null) { Invoke(themes, "Apply", "Midnight", false); await Idle(200); }

        // ── the chat list narrowed to its least: she shrinks so the title keeps its room ──
        var side = (FrameworkElement)MainWin.FindName("ChatSidebar");
        MainVm.NavigateToCommand.Execute("Chat");
        double oldW = side.Width;
        side.Width = 180;
        await Idle(250);
        {
            var root = (UIElement)RootVisual(MainWin);
            Rect her = Bounds(hero, root);
            Check("hero [narrow list]: she shrinks with a 180 px list", her.Width < 124 && her.Width >= 60, $"{her.Width:0.#}");
            var under = Under(hero, her, root);
            Check("hero [narrow list]: nothing under her", under.Count == 0, string.Join("; ", under.Take(6)));
        }
        side.Width = oldW;
        await Idle(150);

        // ── the bundled font ──
        typo.SetFont(TypographyService.BundledFont, persist: false);
        await Idle(150);
        Check("font: the bundled font goes on as Font.Ui", ReferenceEquals(Application.Current.Resources["Font.Ui"], TypographyService.BundledFamily));
        if (themes != null) { Invoke(themes, "Apply", "Classic", false); await Idle(150); }
        Check("font: it stays when the theme changes", ReferenceEquals(Application.Current.Resources["Font.Ui"], TypographyService.BundledFamily)
              && MainWin.TryFindResource("Font.Ui") == TypographyService.BundledFamily);
        foreach (var (weight, file) in new[] { (FontWeights.Normal, "NotoSansCX-Regular.ttf"), (FontWeights.Medium, "NotoSansCX-Medium.ttf"),
                                               (FontWeights.SemiBold, "NotoSansCX-SemiBold.ttf"), (FontWeights.Bold, "NotoSansCX-Bold.ttf") })
        {
            var tf = new Typeface(TypographyService.BundledFamily, FontStyles.Normal, weight, FontStretches.Normal);
            bool got = tf.TryGetGlyphTypeface(out var g);
            string uri = got ? g.FontUri.ToString() : "none";
            Check($"font: {weight} draws from its own file ({file}), not a fake bold", got && uri.EndsWith(file, StringComparison.OrdinalIgnoreCase)
                  && !tf.IsBoldSimulated, $"{uri} simulated={tf.IsBoldSimulated}");
            if (got)
                Check($"font: {weight} has Latin but no Thai, so Thai falls through to Leelawadee UI",
                      "AZaz09éß".All(c => g.CharacterToGlyphMap.ContainsKey(c)) && !Enumerable.Range(0x0E01, 0x5B).Any(c => g.CharacterToGlyphMap.ContainsKey(c)));
        }
        {
            var lee = new Typeface(new FontFamily("Leelawadee UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            Check("font: Leelawadee UI is on this PC and has the Thai block", lee.TryGetGlyphTypeface(out var lg)
                  && Enumerable.Range(0x0E01, 0x2E).All(c => lg.CharacterToGlyphMap.ContainsKey(c)));
        }
        // Thai stacking at every size, for the eye (and nothing falls back to another font)
        {
            const string sample = "น้ำใจ ปั๊ม ญี่ปุ่น กี่ปี ฎีกา ฤๅษี ปู่ย่า ตั๊กแตน · Aa 012";
            var panel = new StackPanel { Background = (Brush)MainWin.FindResource("Bg.Window"), Margin = new Thickness(0) };
            foreach (double size in new[] { 9.0, 11, 13, 16, 20, 28 })
            foreach (var wt in new[] { FontWeights.Normal, FontWeights.Bold })
                panel.Children.Add(new TextBlock
                {
                    Text = $"{size:0}px {(wt == FontWeights.Bold ? "B" : " ")}  {sample}", FontFamily = TypographyService.BundledFamily,
                    FontSize = size, FontWeight = wt, Margin = new Thickness(10, 2, 10, 2), Foreground = (Brush)MainWin.FindResource("Text.Primary"),
                });
            panel.Measure(new Size(900, double.PositiveInfinity));
            panel.Arrange(new Rect(0, 0, 900, panel.DesiredSize.Height));
            panel.UpdateLayout();
            SavePng(Snapshot(panel, 900, (int)Math.Ceiling(panel.DesiredSize.Height)), Path.Combine(dir, "thai-sizes-bundled.png"));
        }

        // Settings › Text size and font, light and dark, at 100 % (the card scrolled into view)
        MainVm.NavigateToCommand.Execute("Settings");
        MainVm.SettingsVM.SelectedCategory = "general";
        foreach (var (id, label) in new[] { ("Classic", "light"), ("Midnight", "dark") })
        {
            if (themes != null) { Invoke(themes, "Apply", id, false); await Idle(250); }
            var card = FindAll<Border>(MainWin).FirstOrDefault(x => x.Name == "TypographyCard");
            Check($"settings [{label}]: the Text size and font card is on the General page", card is { IsVisible: true });
            card?.BringIntoView();
            await Idle(300);
            FreezeVolatile();
            SavePng(Snapshot(RootVisual(MainWin), W, H), Path.Combine(dir, $"settings-typography-{label}.png"));
        }
        typo.SetFont(TypographyService.ThemeFont, persist: false);
        if (themes != null) { Invoke(themes, "Apply", "Midnight", false); await Idle(150); }
        Check("font: back to the theme's own takes the override out", !ReferenceEquals(Application.Current.Resources["Font.Ui"], TypographyService.BundledFamily)
              && ReferenceEquals(MainWin.TryFindResource("Font.Ui"), TypographyService.ThemeFamily()));
        Log($"  shots: {dir}");
    }

    static Rect Bounds(FrameworkElement e, UIElement root) =>
        e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));

    /// <summary>What part of <paramref name="e"/> is on screen: its bounds cut by every scrolling or clipping ancestor.</summary>
    static Rect VisiblePart(FrameworkElement e, UIElement root)
    {
        Rect r = Bounds(e, root);
        for (DependencyObject p = VisualTreeHelper.GetParent(e); p != null && p != root; p = VisualTreeHelper.GetParent(p))
        {
            if (p is UIElement u && !u.IsVisible) return Rect.Empty;
            if (p is ScrollContentPresenter or ScrollViewer || p is UIElement { ClipToBounds: true })
            {
                var fe = (FrameworkElement)p;
                r.Intersect(Bounds(fe, root));
                if (r.IsEmpty) return r;
            }
        }
        return r;
    }

    /// <summary>Text, inputs and buttons a user could see that overlap her square (her own parts left out).</summary>
    static List<string> Under(FrameworkElement hero, Rect her, UIElement root)
    {
        var bad = new List<string>();
        her.Inflate(-2, -2);
        foreach (var el in VisualChildren(root).OfType<FrameworkElement>())
        {
            if (el is not (TextBlock or TextBox or ButtonBase)) continue;
            if (!el.IsVisible || el.ActualWidth < 2 || el.ActualHeight < 2 || el.Opacity < 0.05) continue;
            if (ReferenceEquals(el, hero) || Ancestors(el).Contains(hero)) continue;
            if (el is TextBlock tb && string.IsNullOrWhiteSpace(tb.Text)) continue;
            // a TextBlock inside a button is counted as the button
            if (el is TextBlock && Ancestors(el).OfType<ButtonBase>().Any()) continue;
            Rect r;
            try { r = VisiblePart(el, root); } catch (InvalidOperationException) { continue; }
            if (r.IsEmpty || r.Width < 1 || r.Height < 1) continue;
            if (!r.IntersectsWith(her)) continue;
            var cut = Rect.Intersect(r, her);
            if (cut.Width < 2 || cut.Height < 2) continue;
            string what = el switch { TextBlock t => $"text '{Clip(t.Text)}'", TextBox => "text box", ButtonBase bb => $"button {bb.ToolTip as string ?? bb.Name}", _ => el.GetType().Name };
            bad.Add($"{what} @{r.X:0},{r.Y:0} {r.Width:0}×{r.Height:0}");
        }
        return bad;
    }

    static string Clip(string s) => s.Length > 24 ? s[..24] + "…" : s;
}
